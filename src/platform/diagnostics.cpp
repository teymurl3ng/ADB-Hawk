#include "platform/diagnostics.hpp"

#include <fcntl.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <netinet/in.h>
#include <pwd.h>
#include <sys/socket.h>
#include <unistd.h>

#include <algorithm>
#include <charconv>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace adbhawk::platform {

namespace {

std::string ReadWholeFile(const char* path) {
  int fd = ::open(path, O_RDONLY | O_CLOEXEC);
  if (fd < 0) {
    return {};
  }
  std::string out;
  char buf[4096];
  while (true) {
    ssize_t n = ::read(fd, buf, sizeof(buf));
    if (n <= 0) {
      break;
    }
    out.append(buf, static_cast<size_t>(n));
  }
  ::close(fd);
  return out;
}

std::string ResolveUidName(uid_t uid) {
  struct passwd pwd {};
  struct passwd* result = nullptr;
  char buf[1024];
  if (::getpwuid_r(uid, &pwd, buf, sizeof(buf), &result) == 0 &&
      result != nullptr && pwd.pw_name != nullptr && pwd.pw_name[0] != '\0') {
    return std::string(pwd.pw_name);
  }
  return "uid:" + std::to_string(uid);
}

void AppendHoldersFromTable(std::string_view table,
                            uid_t self_uid,
                            std::vector<std::string>* out) {
  size_t pos = 0;
  bool first_line = true;
  while (pos < table.size()) {
    size_t eol = table.find('\n', pos);
    std::string_view line = (eol == std::string_view::npos)
                                ? table.substr(pos)
                                : table.substr(pos, eol - pos);
    pos = (eol == std::string_view::npos) ? table.size() : (eol + 1);
    if (first_line) {
      first_line = false;
      continue;
    }

    // Columns in /proc/net/udp{,6}:
    // 0: sl  1: local_address  2: rem_address  3: st  4: tx_queue:rx_queue
    // 5: tr:tm->when  6: retrnsmt  7: uid
    std::vector<std::string_view> cols;
    size_t i = 0;
    while (i < line.size()) {
      while (i < line.size() && (line[i] == ' ' || line[i] == '\t')) {
        ++i;
      }
      if (i >= line.size()) {
        break;
      }
      size_t start = i;
      while (i < line.size() && line[i] != ' ' && line[i] != '\t') {
        ++i;
      }
      cols.push_back(line.substr(start, i - start));
      if (cols.size() >= 9) {
        break;
      }
    }
    if (cols.size() < 8) {
      continue;
    }

    std::string_view local_addr = cols[1];
    size_t colon = local_addr.rfind(':');
    if (colon == std::string_view::npos) {
      continue;
    }
    std::string_view port_hex = local_addr.substr(colon + 1);
    uint32_t port = 0;
    auto [p_ptr, p_ec] = std::from_chars(
        port_hex.data(), port_hex.data() + port_hex.size(), port, 16);
    if (p_ec != std::errc{} ||
        p_ptr != port_hex.data() + port_hex.size() || port != 5353U) {
      continue;
    }

    std::string_view uid_sv = cols[7];
    uint32_t parsed_uid = 0;
    auto [u_ptr, u_ec] =
        std::from_chars(uid_sv.data(), uid_sv.data() + uid_sv.size(),
                        parsed_uid, 10);
    if (u_ec != std::errc{} || u_ptr != uid_sv.data() + uid_sv.size()) {
      continue;
    }

    auto uid = static_cast<uid_t>(parsed_uid);
    if (uid == self_uid) {
      continue;
    }

    std::string name = ResolveUidName(uid);
    if (std::find(out->begin(), out->end(), name) == out->end()) {
      out->push_back(std::move(name));
    }
  }
}

bool IsVirtualSysfsNetInterface(std::string_view ifname) {
  if (ifname.empty() || ifname.find('/') != std::string_view::npos) {
    return false;
  }
  std::string sys_path = "/sys/devices/virtual/net/" + std::string(ifname);
  return ::access(sys_path.c_str(), F_OK) == 0;
}

}  // namespace

std::vector<std::string> ParseUdp5353HoldersFromProc(
    std::string_view proc_net_udp,
    std::string_view proc_net_udp6,
    uid_t self_uid) {
  std::vector<std::string> holders;
  AppendHoldersFromTable(proc_net_udp, self_uid, &holders);
  AppendHoldersFromTable(proc_net_udp6, self_uid, &holders);
  std::sort(holders.begin(), holders.end());
  return holders;
}

std::vector<MulticastInterfaceInfo> EnumerateMulticastInterfaces() {
  std::vector<MulticastInterfaceInfo> out;
  struct ifaddrs* ifa_head = nullptr;
  if (::getifaddrs(&ifa_head) != 0 || ifa_head == nullptr) {
    return out;
  }

  for (struct ifaddrs* ifa = ifa_head; ifa != nullptr; ifa = ifa->ifa_next) {
    if (ifa->ifa_name == nullptr || ifa->ifa_name[0] == '\0') {
      continue;
    }
    unsigned int flags = ifa->ifa_flags;
    if ((flags & IFF_UP) == 0 || (flags & IFF_RUNNING) == 0 ||
        (flags & IFF_MULTICAST) == 0 || (flags & IFF_LOOPBACK) != 0) {
      continue;
    }
    unsigned int idx = ::if_nametoindex(ifa->ifa_name);
    if (idx == 0) {
      continue;
    }

    auto it = std::find_if(out.begin(), out.end(),
                           [idx](const MulticastInterfaceInfo& info) {
                             return info.ifindex == idx;
                           });
    if (it == out.end()) {
      MulticastInterfaceInfo info;
      info.ifindex = idx;
      info.name = ifa->ifa_name;
      info.is_virtual = IsVirtualSysfsNetInterface(info.name);
      out.push_back(std::move(info));
      it = out.end() - 1;
    }

    if (ifa->ifa_addr != nullptr && ifa->ifa_addr->sa_family == AF_INET) {
      const auto* sin =
          reinterpret_cast<const struct sockaddr_in*>(ifa->ifa_addr);
      uint32_t addr_be = sin->sin_addr.s_addr;
      if (std::find(it->ipv4_addrs_be.begin(), it->ipv4_addrs_be.end(),
                    addr_be) == it->ipv4_addrs_be.end()) {
        it->ipv4_addrs_be.push_back(addr_be);
      }
    }
  }

  ::freeifaddrs(ifa_head);
  std::sort(out.begin(), out.end(),
            [](const MulticastInterfaceInfo& a,
               const MulticastInterfaceInfo& b) {
              return a.ifindex < b.ifindex;
            });
  return out;
}

core::DiscoveryDiagnosis CollectDiscoveryDiagnosis(
    core::DiscoveryProbeOutcome probe_outcome,
    int announcement_count) {
  core::DiscoveryDiagnosis diag;
  diag.probe_outcome = probe_outcome;
  diag.announcement_count = announcement_count;

  std::string udp4 = ReadWholeFile("/proc/net/udp");
  std::string udp6 = ReadWholeFile("/proc/net/udp6");
  diag.port_holders = ParseUdp5353HoldersFromProc(udp4, udp6, ::geteuid());

  auto ifaces = EnumerateMulticastInterfaces();
  for (const auto& iface : ifaces) {
    if (iface.is_virtual) {
      diag.virtual_adapters.push_back(iface.name);
    }
  }
  return diag;
}

}  // namespace adbhawk::platform
