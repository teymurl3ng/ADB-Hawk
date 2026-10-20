#include "platform/adb_io.hpp"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/eventfd.h>
#include <sys/socket.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <functional>
#include <string>
#include <vector>

#include "core/adb_wire.hpp"
#include "core/protobuf_lite.hpp"
#include "platform/adb_locator.hpp"

namespace adbhawk::platform {

namespace {

void DrainEventFd(int fd) {
  uint64_t val = 0;
  while (::read(fd, &val, sizeof(val)) > 0) {
  }
}

void SignalEventFd(int fd) {
  if (fd < 0) {
    return;
  }
  uint64_t val = 1;
  if (::write(fd, &val, sizeof(val)) < 0) {
    // Ignore EAGAIN / EBADF on wakeup eventfd.
  }
}

void ZeroStringSecret(std::string* s) {
  if (s == nullptr) {
    return;
  }
  volatile char* p = s->data();
  for (size_t i = 0; i < s->size(); ++i) {
    p[i] = '\0';
  }
  s->clear();
}

int ConnectLoopbackNonBlocking(uint16_t port,
                               int wakeup_fd,
                               int timeout_ms) {
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

  struct pollfd pfds[2]{};
  pfds[0].fd = fd;
  pfds[0].events = POLLOUT;
  pfds[1].fd = wakeup_fd;
  pfds[1].events = POLLIN;

  int pr = ::poll(pfds, 2, timeout_ms);
  if (pr <= 0 || (pfds[1].revents & POLLIN) != 0) {
    ::close(fd);
    return -1;
  }

  int so_err = 0;
  socklen_t len = sizeof(so_err);
  if (::getsockopt(fd, SOL_SOCKET, SO_ERROR, &so_err, &len) != 0 ||
      so_err != 0) {
    ::close(fd);
    return -1;
  }
  return fd;
}

bool SendAllWithWakeup(int fd,
                       int wakeup_fd,
                       std::string_view data,
                       int timeout_ms) {
  size_t offset = 0;
  while (offset < data.size()) {
    ssize_t n =
        ::send(fd, data.data() + offset, data.size() - offset, MSG_NOSIGNAL);
    if (n > 0) {
      offset += static_cast<size_t>(n);
      continue;
    }
    if (n < 0 && errno == EINTR) {
      continue;
    }
    if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
      struct pollfd pfds[2]{};
      pfds[0].fd = fd;
      pfds[0].events = POLLOUT;
      pfds[1].fd = wakeup_fd;
      pfds[1].events = POLLIN;
      int pr = ::poll(pfds, 2, timeout_ms);
      if (pr <= 0 || (pfds[1].revents & POLLIN) != 0) {
        return false;
      }
      continue;
    }
    return false;
  }
  return true;
}

bool RecvExactWithWakeup(int fd,
                         int wakeup_fd,
                         uint8_t* buf,
                         size_t count,
                         int timeout_ms,
                         const std::function<void()>& on_wakeup = {}) {
  size_t offset = 0;
  while (offset < count) {
    ssize_t n = ::recv(fd, buf + offset, count - offset, 0);
    if (n > 0) {
      offset += static_cast<size_t>(n);
      continue;
    }
    if (n == 0) {
      return false;
    }
    if (errno == EINTR) {
      continue;
    }
    if (errno == EAGAIN || errno == EWOULDBLOCK) {
      struct pollfd pfds[2]{};
      pfds[0].fd = fd;
      pfds[0].events = POLLIN;
      pfds[1].fd = wakeup_fd;
      pfds[1].events = POLLIN;
      int pr = ::poll(pfds, 2, timeout_ms);
      if (pr <= 0) {
        return false;
      }
      if ((pfds[1].revents & POLLIN) != 0) {
        if (on_wakeup) {
          on_wakeup();
        } else {
          return false;
        }
      }
      continue;
    }
    return false;
  }
  return true;
}

bool ReadLengthPrefixedPayload(int fd,
                               int wakeup_fd,
                               std::vector<uint8_t>* out_payload,
                               int timeout_ms,
                               const std::function<void()>& on_wakeup = {}) {
  uint8_t hdr[4];
  if (!RecvExactWithWakeup(fd, wakeup_fd, hdr, 4, timeout_ms, on_wakeup)) {
    return false;
  }
  size_t len = 0;
  std::string_view hdr_sv(reinterpret_cast<const char*>(hdr), 4);
  if (!core::TryParseHex4Length(hdr_sv, &len)) {
    return false;
  }
  out_payload->resize(len);
  if (len == 0) {
    return true;
  }
  return RecvExactWithWakeup(fd, wakeup_fd, out_payload->data(), len,
                             timeout_ms, on_wakeup);
}

enum class OneShotStatus {
  Okay,
  Fail,
  IoError,
};

OneShotStatus ExecuteOneShotHostService(uint16_t port,
                                        int wakeup_fd,
                                        std::string_view service,
                                        std::vector<uint8_t>* out_payload,
                                        int timeout_ms = 2000) {
  int fd = ConnectLoopbackNonBlocking(port, wakeup_fd, timeout_ms);
  if (fd < 0) {
    return OneShotStatus::IoError;
  }
  auto frame = core::EncodeAdbFrame(service);
  if (!frame.has_value() ||
      !SendAllWithWakeup(fd, wakeup_fd, *frame, timeout_ms)) {
    ::close(fd);
    return OneShotStatus::IoError;
  }
  uint8_t status_hdr[4];
  if (!RecvExactWithWakeup(fd, wakeup_fd, status_hdr, 4, timeout_ms)) {
    ::close(fd);
    return OneShotStatus::IoError;
  }
  auto status = core::ParseAdbStatusHeader(
      std::string_view(reinterpret_cast<const char*>(status_hdr), 4));
  if (status == core::AdbWireStatus::Malformed) {
    ::close(fd);
    return OneShotStatus::IoError;
  }
  if (out_payload != nullptr) {
    if (!ReadLengthPrefixedPayload(fd, wakeup_fd, out_payload, timeout_ms)) {
      ::close(fd);
      return status == core::AdbWireStatus::Fail ? OneShotStatus::Fail
                                                 : OneShotStatus::IoError;
    }
  }
  ::close(fd);
  return status == core::AdbWireStatus::Okay ? OneShotStatus::Okay
                                             : OneShotStatus::Fail;
}

std::optional<std::string> RunDeviceShellCommand(uint16_t port,
                                                 int wakeup_fd,
                                                 std::string_view adb_serial,
                                                 std::string_view shell_cmd,
                                                 int timeout_ms = 2000) {
  int fd = ConnectLoopbackNonBlocking(port, wakeup_fd, timeout_ms);
  if (fd < 0) {
    return std::nullopt;
  }
  std::string transport_req = "host:transport:" + std::string(adb_serial);
  auto t_frame = core::EncodeAdbFrame(transport_req);
  if (!t_frame.has_value() ||
      !SendAllWithWakeup(fd, wakeup_fd, *t_frame, timeout_ms)) {
    ::close(fd);
    return std::nullopt;
  }
  uint8_t st[4];
  if (!RecvExactWithWakeup(fd, wakeup_fd, st, 4, timeout_ms) ||
      core::ParseAdbStatusHeader(
          std::string_view(reinterpret_cast<const char*>(st), 4)) !=
          core::AdbWireStatus::Okay) {
    ::close(fd);
    return std::nullopt;
  }

  std::string sh_req = "shell:" + std::string(shell_cmd);
  auto s_frame = core::EncodeAdbFrame(sh_req);
  if (!s_frame.has_value() ||
      !SendAllWithWakeup(fd, wakeup_fd, *s_frame, timeout_ms)) {
    ::close(fd);
    return std::nullopt;
  }
  if (!RecvExactWithWakeup(fd, wakeup_fd, st, 4, timeout_ms) ||
      core::ParseAdbStatusHeader(
          std::string_view(reinterpret_cast<const char*>(st), 4)) !=
          core::AdbWireStatus::Okay) {
    ::close(fd);
    return std::nullopt;
  }

  std::string output;
  char buf[1024];
  while (output.size() < 64 * 1024) {
    struct pollfd pfds[2]{};
    pfds[0].fd = fd;
    pfds[0].events = POLLIN;
    pfds[1].fd = wakeup_fd;
    pfds[1].events = POLLIN;
    int pr = ::poll(pfds, 2, timeout_ms);
    if (pr <= 0 || (pfds[1].revents & POLLIN) != 0) {
      break;
    }
    ssize_t n = ::recv(fd, buf, sizeof(buf), 0);
    if (n > 0) {
      output.append(buf, static_cast<size_t>(n));
    } else {
      break;
    }
  }
  ::close(fd);
  return output;
}

bool SpawnAdbStartServerSafe(const std::string& adb_path) {
  pid_t pid = ::fork();
  if (pid < 0) {
    return false;
  }
  if (pid == 0) {
    int devnull = ::open("/dev/null", O_RDWR | O_CLOEXEC);
    if (devnull >= 0) {
      ::dup2(devnull, STDIN_FILENO);
      ::dup2(devnull, STDOUT_FILENO);
      ::dup2(devnull, STDERR_FILENO);
      if (devnull > 2) {
        ::close(devnull);
      }
    }
    char arg0[] = "adb";
    char arg1[] = "start-server";
    char* const argv[] = {arg0, arg1, nullptr};
    ::execv(adb_path.c_str(), argv);
    ::_exit(127);
  }
  int status = 0;
  if (::waitpid(pid, &status, 0) < 0) {
    return false;
  }
  return WIFEXITED(status) && WEXITSTATUS(status) == 0;
}

}  // namespace

int64_t NowBoottimeMs() {
  struct timespec ts {};
  if (::clock_gettime(CLOCK_BOOTTIME, &ts) != 0) {
    ::clock_gettime(CLOCK_MONOTONIC, &ts);
  }
  return static_cast<int64_t>(ts.tv_sec) * 1000 +
         static_cast<int64_t>(ts.tv_nsec) / 1'000'000;
}

AdbIoWorker::AdbIoWorker(AdbIoConfig config)
    : config_(std::move(config)), active_port_(config_.port) {
  stop_fd_ = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
  wakeup_fd_ = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
  notify_fd_ = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
}

uint16_t AdbIoWorker::ActivePort() const {
  std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(mu_));
  return active_port_;
}

AdbIoWorker::~AdbIoWorker() {
  Stop();
  if (stop_fd_ >= 0) {
    ::close(stop_fd_);
  }
  if (wakeup_fd_ >= 0) {
    ::close(wakeup_fd_);
  }
  if (notify_fd_ >= 0) {
    ::close(notify_fd_);
  }
}

void AdbIoWorker::Start() {
  if (running_) {
    return;
  }
  DrainEventFd(stop_fd_);
  {
    std::lock_guard<std::mutex> lock(mu_);
    stop_requested_ = false;
  }
  running_ = true;
  thread_ = std::thread(&AdbIoWorker::ThreadMain, this);
}

void AdbIoWorker::Stop() {
  if (!running_) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(mu_);
    stop_requested_ = true;
  }
  SignalEventFd(stop_fd_);
  WakeWorker();
  if (thread_.joinable()) {
    thread_.join();
  }
  running_ = false;
}

std::vector<core::AdbViewEvent> AdbIoWorker::DrainEvents() {
  DrainEventFd(notify_fd_);
  std::lock_guard<std::mutex> lock(mu_);
  std::vector<core::AdbViewEvent> out;
  out.swap(pending_events_);
  return out;
}

void AdbIoWorker::RequestMetadataProbe(std::string device_id,
                                       std::string adb_serial) {
  {
    std::lock_guard<std::mutex> lock(mu_);
    metadata_tasks_.push_back(
        MetadataTask{std::move(device_id), std::move(adb_serial)});
  }
  WakeWorker();
}

void AdbIoWorker::RequestPairing(std::string offer_id,
                                 std::string host,
                                 int port,
                                 std::string six_digit_code,
                                 std::string display_name) {
  {
    std::lock_guard<std::mutex> lock(mu_);
    pair_tasks_.push_back(PairTask{
        .offer_id = std::move(offer_id),
        .host = std::move(host),
        .port = port,
        .code = std::move(six_digit_code),
        .display_name = std::move(display_name),
    });
  }
  WakeWorker();
}

void AdbIoWorker::RequestConnect(std::string host, int port) {
  {
    std::lock_guard<std::mutex> lock(mu_);
    connect_tasks_.push_back(ConnectTask{std::move(host), port});
  }
  WakeWorker();
}

void AdbIoWorker::PushEvent(core::AdbViewEvent evt) {
  {
    std::lock_guard<std::mutex> lock(mu_);
    pending_events_.push_back(std::move(evt));
  }
  SignalEventFd(notify_fd_);
}

void AdbIoWorker::WakeWorker() { SignalEventFd(wakeup_fd_); }

void AdbIoWorker::RunPendingTasks() {
  std::vector<MetadataTask> meta;
  std::vector<PairTask> pairs;
  std::vector<ConnectTask> connects;
  {
    std::lock_guard<std::mutex> lock(mu_);
    meta.swap(metadata_tasks_);
    pairs.swap(pair_tasks_);
    connects.swap(connect_tasks_);
  }

  for (const auto& m : meta) {
    ExecuteMetadataTask(m);
  }
  for (auto& p : pairs) {
    ExecutePairTask(std::move(p));
  }
  for (const auto& c : connects) {
    ExecuteConnectTask(c);
  }
}

void AdbIoWorker::ExecuteMetadataTask(const MetadataTask& task) {
  uint16_t port = ActivePort();
  auto model_raw = RunDeviceShellCommand(
      port, stop_fd_, task.adb_serial, "getprop ro.product.model");
  auto brand_raw = RunDeviceShellCommand(
      port, stop_fd_, task.adb_serial, "getprop ro.product.brand");
  auto manuf_raw = RunDeviceShellCommand(port, stop_fd_,
                                         task.adb_serial,
                                         "getprop ro.product.manufacturer");
  auto market_raw = RunDeviceShellCommand(port, stop_fd_,
                                          task.adb_serial,
                                          "getprop ro.product.marketname");
  auto ver_raw = RunDeviceShellCommand(port, stop_fd_,
                                       task.adb_serial,
                                       "getprop ro.build.version.release");
  auto devname_raw = RunDeviceShellCommand(
      port, stop_fd_, task.adb_serial,
      "settings get global device_name");
  auto bat_raw = RunDeviceShellCommand(port, stop_fd_,
                                       task.adb_serial, "dumpsys battery");

  core::MetadataReceivedEvent evt;
  evt.device_id = task.device_id;
  if (model_raw.has_value()) {
    evt.model = core::TryParseGetpropValue(*model_raw);
  }
  if (brand_raw.has_value()) {
    evt.brand = core::TryParseGetpropValue(*brand_raw);
  }
  if (manuf_raw.has_value()) {
    evt.manufacturer = core::TryParseGetpropValue(*manuf_raw);
  }
  if (market_raw.has_value()) {
    evt.marketing_name = core::TryParseGetpropValue(*market_raw);
  }
  if (ver_raw.has_value()) {
    evt.android_version = core::TryParseGetpropValue(*ver_raw);
  }
  if (devname_raw.has_value()) {
    evt.device_name = core::TryParseGetpropValue(*devname_raw);
  }
  if (bat_raw.has_value()) {
    evt.battery = core::TryParseBatteryPercent(*bat_raw);
  }
  PushEvent(std::move(evt));
}

void AdbIoWorker::ExecutePairTask(PairTask task) {
  PushEvent(core::PairingProgressedEvent{
      .offer_id = task.offer_id,
      .phase = core::PairingPhase::Pairing,
      .failure_kind = core::PairingFailureKind::WrongCode,
      .display_name = task.display_name,
  });

  std::string addr =
      (task.host.find(':') != std::string::npos && task.host.front() != '[')
          ? ("[" + task.host + "]:" + std::to_string(task.port))
          : (task.host + ":" + std::to_string(task.port));
  std::string wire_cmd = "host:pair:" + task.code + ":" + addr;
  ZeroStringSecret(&task.code);

  std::vector<uint8_t> reply;
  auto st = ExecuteOneShotHostService(ActivePort(), stop_fd_, wire_cmd,
                                      &reply, 10'000);
  ZeroStringSecret(&wire_cmd);

  if (st == OneShotStatus::Okay) {
    PushEvent(core::PairingProgressedEvent{
        .offer_id = task.offer_id,
        .phase = core::PairingPhase::PairedWaitingForDevice,
        .failure_kind = core::PairingFailureKind::WrongCode,
        .display_name = task.display_name,
    });
  } else {
    std::string_view err_msg(reinterpret_cast<const char*>(reply.data()),
                             reply.size());
    core::PairingFailureKind fkind = core::PairingFailureKind::WrongCode;
    if (st == OneShotStatus::IoError ||
        err_msg.find("timed out") != std::string_view::npos) {
      fkind = core::PairingFailureKind::Timeout;
    } else if (err_msg.find("too many") != std::string_view::npos) {
      fkind = core::PairingFailureKind::RateLimited;
    }
    PushEvent(core::PairingProgressedEvent{
        .offer_id = task.offer_id,
        .phase = core::PairingPhase::Failed,
        .failure_kind = fkind,
        .display_name = task.display_name,
    });
  }
}

void AdbIoWorker::ExecuteConnectTask(const ConnectTask& task) {
  std::string addr =
      (task.host.find(':') != std::string::npos && task.host.front() != '[')
          ? ("[" + task.host + "]:" + std::to_string(task.port))
          : (task.host + ":" + std::to_string(task.port));
  std::string wire_cmd = "host:connect:" + addr;
  std::vector<uint8_t> reply;
  (void)ExecuteOneShotHostService(ActivePort(), stop_fd_, wire_cmd, &reply,
                                  5'000);
}

void AdbIoWorker::ThreadMain() {
  int64_t outage_started_ms = NowBoottimeMs();
  int64_t backoff_ms = config_.reconnect_initial_backoff_ms;
  bool unavailable_emitted = false;
  bool start_attempted_in_outage = false;

  auto should_stop = [this]() {
    std::lock_guard<std::mutex> lock(mu_);
    return stop_requested_;
  };

  auto sleep_with_wakeup = [&](int64_t wait_ms) {
    struct pollfd pfd {};
    pfd.fd = wakeup_fd_;
    pfd.events = POLLIN;
    int pr = ::poll(&pfd, 1, static_cast<int>(wait_ms));
    if (pr > 0 && (pfd.revents & POLLIN) != 0) {
      DrainEventFd(wakeup_fd_);
      if (!should_stop()) {
        RunPendingTasks();
      }
    }
  };

  PushEvent(core::AdbServerStateChangedEvent{
      .phase = core::AdbServerPhase::Probing,
      .mdns_enabled = std::nullopt,
  });

  while (!should_stop()) {
    RunPendingTasks();

    uint16_t target_port = config_.port;
    if (config_.auto_port_discovery) {
      auto servers = DiscoverActiveAdbServers(::geteuid());
      for (const auto& s : servers) {
        if (s.is_adb_server && s.device_count > 0) {
          target_port = s.port;
          break;
        }
      }
    }
    {
      std::lock_guard<std::mutex> lock(mu_);
      active_port_ = target_port;
    }

    if (config_.verify_listener_uid) {
      auto owner = VerifyLocalAdbServerOwner(target_port, ::geteuid());
      if (owner == ListenerOwnerStatus::OwnedByOtherUid) {
        // Refuse to show devices from a listener owned by another UID (R-10).
        PushEvent(core::AdbServerStateChangedEvent{
            .phase = core::AdbServerPhase::Unavailable,
            .mdns_enabled = std::nullopt,
        });
        unavailable_emitted = true;
        sleep_with_wakeup(config_.reconnect_max_backoff_ms);
        continue;
      }
    }

    std::vector<uint8_t> ver_payload;
    auto ver_st = ExecuteOneShotHostService(
        target_port, stop_fd_, "host:version", &ver_payload, 1500);
    if (ver_st != OneShotStatus::Okay ||
        !core::TryParseAdbServerVersionHex(std::string_view(
            reinterpret_cast<const char*>(ver_payload.data()),
            ver_payload.size()))
             .has_value()) {
      if (config_.allow_start_server && !start_attempted_in_outage &&
          config_.adb_executable_path.has_value()) {
        start_attempted_in_outage = true;
        PushEvent(core::AdbServerStateChangedEvent{
            .phase = core::AdbServerPhase::Starting,
            .mdns_enabled = std::nullopt,
        });
        (void)SpawnAdbStartServerSafe(*config_.adb_executable_path);
        continue;
      }

      int64_t elapsed = NowBoottimeMs() - outage_started_ms;
      if (elapsed >= config_.unavailable_timeout_ms ||
          (!config_.adb_executable_path.has_value() &&
           config_.allow_start_server)) {
        if (!unavailable_emitted) {
          PushEvent(core::AdbServerStateChangedEvent{
              .phase = core::AdbServerPhase::Unavailable,
              .mdns_enabled = std::nullopt,
          });
          unavailable_emitted = true;
        }
      } else {
        PushEvent(core::AdbServerStateChangedEvent{
            .phase = core::AdbServerPhase::Restoring,
            .mdns_enabled = std::nullopt,
        });
      }

      sleep_with_wakeup(backoff_ms);
      backoff_ms = std::min(backoff_ms * 2, config_.reconnect_max_backoff_ms);
      continue;
    }

    // Server answered `host:version` with valid protocol hex.
    // Probe `host:server-status` once (D4).
    std::optional<bool> mdns_enabled;
    std::vector<uint8_t> status_payload;
    if (ExecuteOneShotHostService(target_port, stop_fd_,
                                  "host:server-status", &status_payload,
                                  1500) == OneShotStatus::Okay) {
      core::AdbServerStatus parsed_status;
      if (core::TryParseAdbServerStatusProto(status_payload, &parsed_status)) {
        mdns_enabled = parsed_status.mdns_enabled;
      }
    }

    PushEvent(core::AdbServerStateChangedEvent{
        .phase = core::AdbServerPhase::Up,
        .mdns_enabled = mdns_enabled,
    });
    unavailable_emitted = false;
    start_attempted_in_outage = false;
    backoff_ms = config_.reconnect_initial_backoff_ms;

    // Open streaming device tracker: try `host:track-devices-proto-binary` once,
    // fall back to `host:track-devices-l` (D4).
    bool use_proto = false;
    int track_fd = ConnectLoopbackNonBlocking(target_port, stop_fd_, 1500);
    if (track_fd >= 0) {
      auto proto_req = core::EncodeAdbFrame("host:track-devices-proto-binary");
      uint8_t hdr[4];
      if (proto_req.has_value() &&
          SendAllWithWakeup(track_fd, stop_fd_, *proto_req, 1500) &&
          RecvExactWithWakeup(track_fd, stop_fd_, hdr, 4, 1500) &&
          core::ParseAdbStatusHeader(
              std::string_view(reinterpret_cast<const char*>(hdr), 4)) ==
              core::AdbWireStatus::Okay) {
        use_proto = true;
      } else {
        ::close(track_fd);
        track_fd = -1;
      }
    }

    if (track_fd < 0) {
      track_fd = ConnectLoopbackNonBlocking(target_port, stop_fd_, 1500);
      if (track_fd >= 0) {
        auto text_req = core::EncodeAdbFrame("host:track-devices-l");
        uint8_t hdr[4];
        if (!text_req.has_value() ||
            !SendAllWithWakeup(track_fd, stop_fd_, *text_req, 1500) ||
            !RecvExactWithWakeup(track_fd, stop_fd_, hdr, 4, 1500) ||
            core::ParseAdbStatusHeader(
                std::string_view(reinterpret_cast<const char*>(hdr), 4)) !=
                core::AdbWireStatus::Okay) {
          ::close(track_fd);
          track_fd = -1;
        }
      }
    }

    if (track_fd < 0) {
      PushEvent(core::DeviceTrackerFailedEvent{});
      outage_started_ms = NowBoottimeMs();
      sleep_with_wakeup(backoff_ms);
      continue;
    }

    bool stream_ok = true;
    size_t last_device_count = 0;
    int64_t last_zero_devices_check_ms = NowBoottimeMs();

    while (stream_ok && !should_stop()) {
      struct pollfd pfds[2]{};
      pfds[0].fd = track_fd;
      pfds[0].events = POLLIN;
      pfds[1].fd = wakeup_fd_;
      pfds[1].events = POLLIN;

      int pr = ::poll(pfds, 2, 2000);
      if (pr < 0) {
        if (errno == EINTR) {
          continue;
        }
        stream_ok = false;
        break;
      }
      if (pr == 0) {
        // Poll timed out (2000ms idle)
        if (config_.auto_port_discovery && last_device_count == 0) {
          int64_t now_ms = NowBoottimeMs();
          if (now_ms - last_zero_devices_check_ms >= 3000) {
            last_zero_devices_check_ms = now_ms;
            auto servers = DiscoverActiveAdbServers(::geteuid());
            for (const auto& s : servers) {
              if (s.is_adb_server && s.device_count > 0 && s.port != target_port) {
                // An alternate port has active devices! Break and reconnect to it.
                stream_ok = false;
                break;
              }
            }
          }
        }
        continue;
      }
      if ((pfds[1].revents & POLLIN) != 0) {
        DrainEventFd(wakeup_fd_);
        if (should_stop()) {
          break;
        }
        RunPendingTasks();
      }
      if ((pfds[0].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
        std::vector<uint8_t> frame_bytes;
        if (!ReadLengthPrefixedPayload(track_fd, stop_fd_, &frame_bytes, 2000)) {
          stream_ok = false;
          break;
        }
        std::vector<core::RawDevice> devices;
        bool parsed = false;
        if (use_proto) {
          parsed = core::TryParseDevicesProto(frame_bytes, &devices);
        } else {
          parsed = core::TryParseTrackDevicesL(
              std::string_view(reinterpret_cast<const char*>(frame_bytes.data()),
                               frame_bytes.size()),
              &devices);
        }
        if (!parsed) {
          stream_ok = false;
          break;
        }
        // Query one-shot `host:mdns:services` whenever the device list updates.
        // Pushing MdnsServicesReceivedEvent before DeviceListReceivedEvent allows
        // the state reducer to correlate Wi-Fi IP:port endpoints with mDNS instances
        // on the initial frame, avoiding a momentary double-card flash.
        std::vector<uint8_t> mdns_bytes;
        if (ExecuteOneShotHostService(target_port, stop_fd_,
                                      "host:mdns:services", &mdns_bytes,
                                      1000) == OneShotStatus::Okay) {
          auto svcs = core::ParseMdnsServicesList(std::string_view(
              reinterpret_cast<const char*>(mdns_bytes.data()),
              mdns_bytes.size()));
          PushEvent(core::MdnsServicesReceivedEvent{
              .sequence = ++next_mdns_seq_,
              .at_ms = NowBoottimeMs(),
              .services = std::move(svcs),
          });
        }

        last_device_count = devices.size();
        int64_t now_ms = NowBoottimeMs();
        int64_t seq = ++next_device_seq_;
        PushEvent(core::DeviceListReceivedEvent{
            .sequence = seq,
            .at_ms = now_ms,
            .devices = std::move(devices),
        });
      }
    }

    ::close(track_fd);
    if (!should_stop()) {
      PushEvent(core::DeviceTrackerFailedEvent{});
      outage_started_ms = NowBoottimeMs();
    }
  }
}

}  // namespace adbhawk::platform
