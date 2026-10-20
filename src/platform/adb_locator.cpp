#include "platform/adb_locator.hpp"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

#include <algorithm>
#include <cctype>
#include <cerrno>
#include <charconv>
#include <cstdlib>
#include <string>
#include <unordered_set>
#include <vector>

#include "core/adb_wire.hpp"

namespace adbhawk::platform {

namespace {

std::optional<std::string> GetEnvOpt(const char* name) {
  const char* val = std::getenv(name);
  if (val == nullptr || val[0] == '\0') {
    return std::nullopt;
  }
  return std::string(val);
}

bool IsUsableExecutableFile(const std::string& path) {
  if (path.empty() || path.front() != '/') {
    return false;
  }
  struct stat st {};
  if (::stat(path.c_str(), &st) != 0) {
    return false;
  }
  if (!S_ISREG(st.st_mode)) {
    return false;
  }
  return ::faccessat(AT_FDCWD, path.c_str(), X_OK, AT_EACCESS) == 0;
}

std::string JoinPath(std::string_view dir, std::string_view rel) {
  while (!dir.empty() && dir.back() == '/') {
    dir.remove_suffix(1);
  }
  if (dir.empty()) {
    return std::string(rel);
  }
  return std::string(dir) + "/" + std::string(rel);
}

std::string ReadSmallProcFile(const char* path) {
  int fd = ::open(path, O_RDONLY | O_CLOEXEC);
  if (fd < 0) {
    return {};
  }
  std::string out;
  char buf[4096];
  while (true) {
    ssize_t n = ::read(fd, buf, sizeof(buf));
    if (n > 0) {
      out.append(buf, static_cast<size_t>(n));
      if (out.size() > 512 * 1024) {
        break;
      }
    } else if (n < 0 && errno == EINTR) {
      continue;
    } else {
      break;
    }
  }
  ::close(fd);
  return out;
}

void InspectProcTcpTable(std::string_view content,
                         uint16_t port,
                         uid_t expected_uid,
                         bool* found_expected,
                         bool* found_other) {
  size_t pos = 0;
  bool first_line = true;
  while (pos < content.size()) {
    size_t nl = content.find('\n', pos);
    std::string_view line = (nl == std::string_view::npos)
                                ? content.substr(pos)
                                : content.substr(pos, nl - pos);
    pos = (nl == std::string_view::npos) ? content.size() : nl + 1;
    if (first_line) {
      first_line = false;
      continue;
    }
    // Tokenize whitespace-separated columns:
    // 0: sl  1: local_address  2: rem_address  3: st  4..6: queues/tr  7: uid
    std::vector<std::string_view> cols;
    cols.reserve(10);
    size_t i = 0;
    while (i < line.size() && cols.size() < 9) {
      while (i < line.size() &&
             std::isspace(static_cast<unsigned char>(line[i])) != 0) {
        ++i;
      }
      if (i >= line.size()) {
        break;
      }
      size_t start = i;
      while (i < line.size() &&
             std::isspace(static_cast<unsigned char>(line[i])) == 0) {
        ++i;
      }
      cols.push_back(line.substr(start, i - start));
    }
    if (cols.size() < 8) {
      continue;
    }
    // Column 3 (`st`) == "0A" means TCP_LISTEN.
    if (cols[3] != "0A" && cols[3] != "0a") {
      continue;
    }
    std::string_view local = cols[1];
    size_t colon = local.rfind(':');
    if (colon == std::string_view::npos || colon + 1 >= local.size()) {
      continue;
    }
    std::string_view port_hex = local.substr(colon + 1);
    size_t parsed_port = 0;
    if (!core::TryParseHex4Length(port_hex, &parsed_port) ||
        parsed_port != static_cast<size_t>(port)) {
      continue;
    }
    uint32_t uid_val = 0;
    auto [ptr, ec] = std::from_chars(
        cols[7].data(), cols[7].data() + cols[7].size(), uid_val);
    if (ec != std::errc{} || ptr != cols[7].data() + cols[7].size()) {
      continue;
    }
    if (static_cast<uid_t>(uid_val) == expected_uid) {
      *found_expected = true;
    } else {
      *found_other = true;
    }
  }
}

void CollectListeningPortsFromTable(std::string_view content,
                                    uid_t expected_uid,
                                    uint16_t min_port,
                                    uint16_t max_port,
                                    std::vector<uint16_t>* out) {
  size_t pos = 0;
  bool first_line = true;
  while (pos < content.size()) {
    size_t nl = content.find('\n', pos);
    std::string_view line = (nl == std::string_view::npos)
                                ? content.substr(pos)
                                : content.substr(pos, nl - pos);
    pos = (nl == std::string_view::npos) ? content.size() : nl + 1;
    if (first_line) {
      first_line = false;
      continue;
    }
    std::vector<std::string_view> cols;
    cols.reserve(10);
    size_t i = 0;
    while (i < line.size() && cols.size() < 9) {
      while (i < line.size() &&
             std::isspace(static_cast<unsigned char>(line[i])) != 0) {
        ++i;
      }
      if (i >= line.size()) {
        break;
      }
      size_t start = i;
      while (i < line.size() &&
             std::isspace(static_cast<unsigned char>(line[i])) == 0) {
        ++i;
      }
      cols.push_back(line.substr(start, i - start));
    }
    if (cols.size() < 8) {
      continue;
    }
    if (cols[3] != "0A" && cols[3] != "0a") {
      continue;
    }
    std::string_view local = cols[1];
    size_t colon = local.rfind(':');
    if (colon == std::string_view::npos || colon + 1 >= local.size()) {
      continue;
    }
    std::string_view port_hex = local.substr(colon + 1);
    size_t parsed_port = 0;
    if (!core::TryParseHex4Length(port_hex, &parsed_port) ||
        parsed_port < min_port || parsed_port > max_port) {
      continue;
    }
    uint32_t uid_val = 0;
    auto [ptr, ec] = std::from_chars(
        cols[7].data(), cols[7].data() + cols[7].size(), uid_val);
    if (ec != std::errc{} || ptr != cols[7].data() + cols[7].size()) {
      continue;
    }
    if (static_cast<uid_t>(uid_val) == expected_uid) {
      out->push_back(static_cast<uint16_t>(parsed_port));
    }
  }
}

int ConnectLoopbackCandidate(uint16_t port, int timeout_ms) {
  int fd = ::socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
  if (fd < 0) {
    return -1;
  }
  sockaddr_in addr{};
  addr.sin_family = AF_INET;
  addr.sin_port = htons(port);
  addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

  int rc = ::connect(fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr));
  if (rc == 0) {
    return fd;
  }
  if (errno != EINPROGRESS) {
    ::close(fd);
    return -1;
  }
  struct pollfd pfd{};
  pfd.fd = fd;
  pfd.events = POLLOUT;
  int pr = ::poll(&pfd, 1, timeout_ms);
  if (pr <= 0 || (pfd.revents & POLLOUT) == 0) {
    ::close(fd);
    return -1;
  }
  int so_err = 0;
  socklen_t len = sizeof(so_err);
  if (::getsockopt(fd, SOL_SOCKET, SO_ERROR, &so_err, &len) != 0 || so_err != 0) {
    ::close(fd);
    return -1;
  }
  return fd;
}

bool SendExactCandidate(int fd, std::string_view data, int timeout_ms) {
  size_t offset = 0;
  while (offset < data.size()) {
    struct pollfd pfd{};
    pfd.fd = fd;
    pfd.events = POLLOUT;
    int pr = ::poll(&pfd, 1, timeout_ms);
    if (pr <= 0 || (pfd.revents & POLLOUT) == 0) {
      return false;
    }
    ssize_t n = ::send(fd, data.data() + offset, data.size() - offset, MSG_NOSIGNAL);
    if (n > 0) {
      offset += static_cast<size_t>(n);
    } else if (n < 0 && (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK)) {
      continue;
    } else {
      return false;
    }
  }
  return true;
}

bool RecvExactCandidate(int fd, uint8_t* buf, size_t count, int timeout_ms) {
  size_t offset = 0;
  while (offset < count) {
    struct pollfd pfd{};
    pfd.fd = fd;
    pfd.events = POLLIN;
    int pr = ::poll(&pfd, 1, timeout_ms);
    if (pr <= 0 || (pfd.revents & POLLIN) == 0) {
      return false;
    }
    ssize_t n = ::recv(fd, buf + offset, count - offset, 0);
    if (n > 0) {
      offset += static_cast<size_t>(n);
    } else if (n < 0 && (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK)) {
      continue;
    } else {
      return false;
    }
  }
  return true;
}

}  // namespace

AdbLocatorEnv ReadCurrentLocatorEnv() {
  return AdbLocatorEnv{
      .android_home = GetEnvOpt("ANDROID_HOME"),
      .android_sdk_root = GetEnvOpt("ANDROID_SDK_ROOT"),
      .path_env = GetEnvOpt("PATH"),
      .home_dir = GetEnvOpt("HOME"),
  };
}

std::optional<std::string> LocateAdbExecutable(const AdbLocatorEnv& env) {
  // 1. $ANDROID_HOME/platform-tools/adb
  if (env.android_home.has_value() && !env.android_home->empty() &&
      env.android_home->front() == '/') {
    std::string candidate =
        JoinPath(*env.android_home, "platform-tools/adb");
    if (IsUsableExecutableFile(candidate)) {
      return candidate;
    }
  }

  // 2. $ANDROID_SDK_ROOT/platform-tools/adb
  if (env.android_sdk_root.has_value() && !env.android_sdk_root->empty() &&
      env.android_sdk_root->front() == '/') {
    std::string candidate =
        JoinPath(*env.android_sdk_root, "platform-tools/adb");
    if (IsUsableExecutableFile(candidate)) {
      return candidate;
    }
  }

  // 3. $PATH entries (skip ALL non-absolute entries, not just "" and ".")
  if (env.path_env.has_value() && !env.path_env->empty()) {
    std::string_view p = *env.path_env;
    size_t pos = 0;
    while (pos <= p.size()) {
      size_t colon = p.find(':', pos);
      std::string_view entry = (colon == std::string_view::npos)
                                   ? p.substr(pos)
                                   : p.substr(pos, colon - pos);
      if (!entry.empty() && entry.front() == '/') {
        std::string candidate = JoinPath(entry, "adb");
        if (IsUsableExecutableFile(candidate)) {
          return candidate;
        }
      }
      if (colon == std::string_view::npos) {
        break;
      }
      pos = colon + 1;
    }
  }

  // 4. $HOME/Android/Sdk/platform-tools/adb
  if (env.home_dir.has_value() && !env.home_dir->empty() &&
      env.home_dir->front() == '/') {
    std::string candidate =
        JoinPath(*env.home_dir, "Android/Sdk/platform-tools/adb");
    if (IsUsableExecutableFile(candidate)) {
      return candidate;
    }
  }

  // 5. Distro and /opt fallback locations
  constexpr std::string_view kFallbacks[] = {
      "/usr/lib/android-sdk/platform-tools/adb",
      "/usr/bin/adb",
      "/opt/android-sdk/platform-tools/adb",
  };
  for (std::string_view fb : kFallbacks) {
    std::string candidate(fb);
    if (IsUsableExecutableFile(candidate)) {
      return candidate;
    }
  }
  return std::nullopt;
}

std::vector<uint16_t> FindListeningTcpPortsForUid(
    std::string_view proc_net_tcp,
    std::string_view proc_net_tcp6,
    uid_t expected_uid,
    uint16_t min_port,
    uint16_t max_port) {
  std::vector<uint16_t> ports;
  CollectListeningPortsFromTable(proc_net_tcp, expected_uid, min_port, max_port,
                                &ports);
  CollectListeningPortsFromTable(proc_net_tcp6, expected_uid, min_port, max_port,
                                &ports);
  std::sort(ports.begin(), ports.end());
  ports.erase(std::unique(ports.begin(), ports.end()), ports.end());
  return ports;
}

AdbServerProbeResult ProbeAdbServerCandidate(uint16_t port, int timeout_ms) {
  AdbServerProbeResult res{};
  res.port = port;

  // 1. Probe version: "host:version" (frame "000Chost:version")
  int fd = ConnectLoopbackCandidate(port, timeout_ms);
  if (fd < 0) {
    return res;
  }
  if (!SendExactCandidate(fd, "000Chost:version", timeout_ms)) {
    ::close(fd);
    return res;
  }
  uint8_t hdr[4];
  if (!RecvExactCandidate(fd, hdr, 4, timeout_ms) ||
      core::ParseAdbStatusHeader(
          std::string_view(reinterpret_cast<const char*>(hdr), 4)) !=
          core::AdbWireStatus::Okay) {
    ::close(fd);
    return res;
  }
  res.is_adb_server = true;
  ::close(fd);

  // 2. Query attached devices: "host:devices" (frame "000Chost:devices")
  fd = ConnectLoopbackCandidate(port, timeout_ms);
  if (fd < 0) {
    return res;
  }
  if (!SendExactCandidate(fd, "000Chost:devices", timeout_ms)) {
    ::close(fd);
    return res;
  }
  if (!RecvExactCandidate(fd, hdr, 4, timeout_ms) ||
      core::ParseAdbStatusHeader(
          std::string_view(reinterpret_cast<const char*>(hdr), 4)) !=
          core::AdbWireStatus::Okay) {
    ::close(fd);
    return res;
  }
  uint8_t len_hex[4];
  if (!RecvExactCandidate(fd, len_hex, 4, timeout_ms)) {
    ::close(fd);
    return res;
  }
  size_t payload_len = 0;
  if (!core::TryParseHex4Length(
          std::string_view(reinterpret_cast<const char*>(len_hex), 4),
          &payload_len) ||
      payload_len > 64 * 1024) {
    ::close(fd);
    return res;
  }
  std::string payload;
  payload.resize(payload_len);
  if (payload_len > 0) {
    if (!RecvExactCandidate(fd, reinterpret_cast<uint8_t*>(payload.data()),
                            payload_len, timeout_ms)) {
      ::close(fd);
      return res;
    }
  }
  ::close(fd);

  // Parse lines: "<serial>\t<state>\n"
  size_t pos = 0;
  while (pos < payload.size()) {
    size_t eol = payload.find('\n', pos);
    std::string_view line =
        (eol == std::string_view::npos)
            ? std::string_view(payload).substr(pos)
            : std::string_view(payload).substr(pos, eol - pos);
    pos = (eol == std::string_view::npos) ? payload.size() : eol + 1;
    while (!line.empty() &&
           std::isspace(static_cast<unsigned char>(line.back())) != 0) {
      line.remove_suffix(1);
    }
    while (!line.empty() &&
           std::isspace(static_cast<unsigned char>(line.front())) != 0) {
      line.remove_prefix(1);
    }
    if (line.empty()) {
      continue;
    }
    size_t sep = line.find_first_of(" \t");
    std::string_view serial =
        (sep == std::string_view::npos) ? line : line.substr(0, sep);
    std::string_view state =
        (sep == std::string_view::npos) ? "" : line.substr(sep + 1);
    while (!state.empty() &&
           std::isspace(static_cast<unsigned char>(state.front())) != 0) {
      state.remove_prefix(1);
    }
    if (!serial.empty() && state != "offline") {
      res.device_serials.push_back(std::string(serial));
      res.device_count++;
    }
  }
  return res;
}

std::vector<AdbServerProbeResult> DiscoverActiveAdbServers(uid_t expected_uid) {
  std::string tcp4 = ReadSmallProcFile("/proc/net/tcp");
  std::string tcp6 = ReadSmallProcFile("/proc/net/tcp6");
  auto candidate_ports =
      FindListeningTcpPortsForUid(tcp4, tcp6, expected_uid, 5037, 5050);

  bool has_5037 = false;
  for (uint16_t p : candidate_ports) {
    if (p == 5037) {
      has_5037 = true;
      break;
    }
  }
  if (!has_5037) {
    candidate_ports.insert(candidate_ports.begin(), 5037);
  }

  std::vector<AdbServerProbeResult> results;
  for (uint16_t p : candidate_ports) {
    auto res = ProbeAdbServerCandidate(p, 80);
    if (res.is_adb_server) {
      results.push_back(std::move(res));
    }
  }

  std::stable_sort(
      results.begin(), results.end(),
      [](const AdbServerProbeResult& a, const AdbServerProbeResult& b) {
        if ((a.device_count > 0) != (b.device_count > 0)) {
          return a.device_count > b.device_count;
        }
        if (a.port == 5037) return true;
        if (b.port == 5037) return false;
        return a.port < b.port;
      });
  return results;
}

uint16_t ResolveAdbServerPort(std::optional<std::string_view> env_port,
                              bool auto_discover_with_devices) {
  std::optional<std::string> from_env;
  if (!env_port.has_value()) {
    from_env = GetEnvOpt("ANDROID_ADB_SERVER_PORT");
    if (from_env.has_value()) {
      env_port = *from_env;
    }
  }
  if (env_port.has_value() && !env_port->empty()) {
    std::string_view sv = *env_port;
    while (!sv.empty() &&
           std::isspace(static_cast<unsigned char>(sv.front())) != 0) {
      sv.remove_prefix(1);
    }
    while (!sv.empty() &&
           std::isspace(static_cast<unsigned char>(sv.back())) != 0) {
      sv.remove_suffix(1);
    }
    int port = 0;
    auto [ptr, ec] = std::from_chars(sv.data(), sv.data() + sv.size(), port);
    if (ec != std::errc{} || ptr != sv.data() + sv.size() || port <= 0 ||
        port > 65535) {
      return 5037;
    }
    return static_cast<uint16_t>(port);
  }

  if (auto_discover_with_devices) {
    auto servers = DiscoverActiveAdbServers(::geteuid());
    for (const auto& s : servers) {
      if (s.is_adb_server && s.device_count > 0) {
        return s.port;
      }
    }
    for (const auto& s : servers) {
      if (s.is_adb_server && s.port == 5037) {
        return 5037;
      }
    }
  }
  return 5037;
}

ListenerOwnerStatus CheckTcpListenerOwnerFromProc(
    std::string_view proc_net_tcp,
    std::string_view proc_net_tcp6,
    uint16_t port,
    uid_t expected_uid) {
  bool found_expected = false;
  bool found_other = false;
  InspectProcTcpTable(proc_net_tcp, port, expected_uid, &found_expected,
                      &found_other);
  InspectProcTcpTable(proc_net_tcp6, port, expected_uid, &found_expected,
                      &found_other);
  if (found_expected) {
    return ListenerOwnerStatus::OwnedByExpectedUid;
  }
  if (found_other) {
    return ListenerOwnerStatus::OwnedByOtherUid;
  }
  return ListenerOwnerStatus::NotFound;
}

ListenerOwnerStatus VerifyLocalAdbServerOwner(uint16_t port,
                                              uid_t expected_uid) {
  std::string tcp4 = ReadSmallProcFile("/proc/net/tcp");
  std::string tcp6 = ReadSmallProcFile("/proc/net/tcp6");
  return CheckTcpListenerOwnerFromProc(tcp4, tcp6, port, expected_uid);
}

std::optional<int> QueryAdbClientProtocolVersion(const std::string& adb_path) {
  if (!IsUsableExecutableFile(adb_path)) {
    return std::nullopt;
  }
  int pipefd[2] = {-1, -1};
  if (::pipe2(pipefd, O_CLOEXEC) != 0) {
    return std::nullopt;
  }

  pid_t pid = ::fork();
  if (pid < 0) {
    ::close(pipefd[0]);
    ::close(pipefd[1]);
    return std::nullopt;
  }
  if (pid == 0) {
    ::close(pipefd[0]);
    int devnull = ::open("/dev/null", O_RDWR | O_CLOEXEC);
    if (devnull >= 0) {
      ::dup2(devnull, STDIN_FILENO);
      ::dup2(devnull, STDERR_FILENO);
      if (devnull > 2) {
        ::close(devnull);
      }
    }
    ::dup2(pipefd[1], STDOUT_FILENO);
    if (pipefd[1] > 2) {
      ::close(pipefd[1]);
    }
    char arg0[] = "adb";
    char arg1[] = "version";
    char* const argv[] = {arg0, arg1, nullptr};
    ::execv(adb_path.c_str(), argv);
    ::_exit(127);
  }

  ::close(pipefd[1]);
  std::string output;
  struct pollfd pfd {};
  pfd.fd = pipefd[0];
  pfd.events = POLLIN;

  while (output.size() < 4096) {
    int pr = ::poll(&pfd, 1, 2000);
    if (pr <= 0) {
      break;
    }
    char buf[512];
    ssize_t n = ::read(pipefd[0], buf, sizeof(buf));
    if (n > 0) {
      output.append(buf, static_cast<size_t>(n));
    } else {
      break;
    }
  }
  ::close(pipefd[0]);

  int status = 0;
  (void)::waitpid(pid, &status, 0);
  return core::TryParseAdbClientVersionOutput(output);
}

}  // namespace adbhawk::platform
