#include <arpa/inet.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_set>
#include <vector>

#include "core/adb_wire.hpp"
#include "core/reduce.hpp"
#include "core/snapshot.hpp"
#include "core/vocabulary.hpp"
#include "platform/adb_io.hpp"
#include "platform/adb_locator.hpp"

namespace {

using namespace adbhawk::core;
using namespace adbhawk::platform;

#define REQUIRE(cond)                                                       \
  do {                                                                      \
    if (!(cond)) {                                                          \
      std::fprintf(stderr, "FAIL: %s:%d: %s\n", __FILE__, __LINE__, #cond); \
      std::exit(1);                                                         \
    }                                                                       \
  } while (0)

void AppendVarint(std::string* out, uint64_t val) {
  while (val >= 0x80U) {
    out->push_back(static_cast<char>((val & 0x7FU) | 0x80U));
    val >>= 7U;
  }
  out->push_back(static_cast<char>(val & 0x7FU));
}

void AppendTag(std::string* out, uint32_t field, uint32_t wire_type) {
  AppendVarint(out, (static_cast<uint64_t>(field) << 3U) | wire_type);
}

void AppendBytesField(std::string* out, uint32_t field, std::string_view sv) {
  AppendTag(out, field, 2U);
  AppendVarint(out, sv.size());
  out->append(sv);
}

void AppendVarintField(std::string* out, uint32_t field, uint64_t val) {
  AppendTag(out, field, 0U);
  AppendVarint(out, val);
}

std::string BuildServerStatusProtoBytes() {
  std::string msg;
  AppendVarintField(&msg, 1, 2);  // LIBUSB
  AppendVarintField(&msg, 2, 2);  // OPENSCREEN
  AppendBytesField(&msg, 3, "34.0.4-fake");
  AppendBytesField(&msg, 5, "/usr/bin/adb");
  AppendVarintField(&msg, 7, 1);  // mdns_enabled = true
  return msg;
}

std::string BuildDevicesProtoBytes() {
  std::string dev;
  AppendBytesField(&dev, 1, "R5CT123456A");
  AppendVarintField(&dev, 2, 8);  // DEVICE (Connected)
  AppendBytesField(&dev, 3, "1-1");
  AppendBytesField(&dev, 4, "husky");
  AppendBytesField(&dev, 5, "Pixel 8 Pro");
  AppendBytesField(&dev, 6, "husky");
  AppendVarintField(&dev, 7, 1);  // USB
  AppendVarintField(&dev, 8, 21);

  std::string outer;
  AppendBytesField(&outer, 1, dev);
  return outer;
}

std::string Hex4Prefix(std::string_view payload) {
  char buf[5];
  std::snprintf(buf, sizeof(buf), "%04zx", payload.size());
  std::string out(buf, 4);
  out.append(payload);
  return out;
}

bool SendAll(int fd, std::string_view data) {
  size_t off = 0;
  while (off < data.size()) {
    ssize_t n = ::send(fd, data.data() + off, data.size() - off, MSG_NOSIGNAL);
    if (n <= 0) {
      return false;
    }
    off += static_cast<size_t>(n);
  }
  return true;
}

bool RecvExact(int fd, char* buf, size_t len) {
  size_t off = 0;
  while (off < len) {
    ssize_t n = ::recv(fd, buf + off, len - off, 0);
    if (n <= 0) {
      return false;
    }
    off += static_cast<size_t>(n);
  }
  return true;
}

bool ReadAdbRequest(int fd, std::string* out) {
  char hex[4];
  if (!RecvExact(fd, hex, 4)) {
    return false;
  }
  size_t len = 0;
  if (!TryParseHex4Length(std::string_view(hex, 4), &len)) {
    return false;
  }
  out->assign(len, '\0');
  if (len > 0 && !RecvExact(fd, out->data(), len)) {
    return false;
  }
  return true;
}

class FakeAdbServer {
 public:
  explicit FakeAdbServer(bool enable_proto_tracker)
      : enable_proto_tracker_(enable_proto_tracker) {
    listen_fd_ = ::socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    REQUIRE(listen_fd_ >= 0);
    int one = 1;
    (void)::setsockopt(listen_fd_, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));

    struct sockaddr_in sin {};
    sin.sin_family = AF_INET;
    sin.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    sin.sin_port = 0;
    REQUIRE(::bind(listen_fd_, reinterpret_cast<struct sockaddr*>(&sin),
                   sizeof(sin)) == 0);
    REQUIRE(::listen(listen_fd_, 32) == 0);

    socklen_t slen = sizeof(sin);
    REQUIRE(::getsockname(listen_fd_, reinterpret_cast<struct sockaddr*>(&sin),
                          &slen) == 0);
    port_ = ntohs(sin.sin_port);

    accept_thread_ = std::thread([this]() { AcceptLoop(); });
  }

  ~FakeAdbServer() { Stop(); }

  uint16_t port() const { return port_; }

  void SetRefuseTrackerConnections(bool refuse) {
    refuse_tracker_.store(refuse);
  }

  void DropActiveTracker() {
    std::lock_guard<std::mutex> lock(mu_);
    if (active_tracker_fd_ >= 0) {
      ::shutdown(active_tracker_fd_, SHUT_RDWR);
    }
  }

  void Stop() {
    if (stopped_.exchange(true)) {
      return;
    }
    DropActiveTracker();
    if (listen_fd_ >= 0) {
      ::shutdown(listen_fd_, SHUT_RDWR);
      ::close(listen_fd_);
      listen_fd_ = -1;
    }
    if (accept_thread_.joinable()) {
      accept_thread_.join();
    }
    for (auto& t : client_threads_) {
      if (t.joinable()) {
        t.join();
      }
    }
  }

 private:
  void AcceptLoop() {
    while (!stopped_.load()) {
      struct pollfd pfd {};
      pfd.fd = listen_fd_;
      pfd.events = POLLIN;
      int rc = ::poll(&pfd, 1, 50);
      if (rc <= 0) {
        continue;
      }
      int cfd = ::accept4(listen_fd_, nullptr, nullptr, SOCK_CLOEXEC);
      if (cfd < 0) {
        continue;
      }
      client_threads_.emplace_back([this, cfd]() { HandleClient(cfd); });
    }
  }

  void HandleClient(int fd) {
    std::string req;
    if (!ReadAdbRequest(fd, &req)) {
      ::close(fd);
      return;
    }

    if (req == "host:version") {
      if (refuse_tracker_.load()) {
        ::close(fd);
        return;
      }
      (void)SendAll(fd, "OKAY00040029");
      ::close(fd);
      return;
    }

    if (req == "host:server-status") {
      std::string proto = BuildServerStatusProtoBytes();
      (void)SendAll(fd, "OKAY" + Hex4Prefix(proto));
      ::close(fd);
      return;
    }

    if (req == "host:mdns:services") {
      std::string payload =
          "adb-R5CT123456A-abc\t_adb-tls-connect._tcp.\t192.168.1.50:41235\n"
          "adb-R5CT123456A-pair\t_adb-tls-pairing._tcp.\t192.168.1.50:38123\n";
      (void)SendAll(fd, "OKAY" + Hex4Prefix(payload));
      ::close(fd);
      return;
    }

    if (req.starts_with("host:pair:")) {
      std::string payload =
          "Successfully paired to 192.168.1.50:38123 [guid=adb-R5CT123456A-abc]\n";
      (void)SendAll(fd, "OKAY" + Hex4Prefix(payload));
      ::close(fd);
      return;
    }

    if (req.starts_with("host:connect:")) {
      std::string payload = "connected to 192.168.1.50:41235\n";
      (void)SendAll(fd, "OKAY" + Hex4Prefix(payload));
      ::close(fd);
      return;
    }

    if (req.starts_with("host:transport:")) {
      (void)SendAll(fd, "OKAY");
      std::string cmd;
      if (!ReadAdbRequest(fd, &cmd)) {
        ::close(fd);
        return;
      }
      if (cmd == "shell:getprop ro.product.model") {
        (void)SendAll(fd, "OKAYPixel 8 Pro\n");
      } else if (cmd == "shell:getprop ro.product.brand") {
        (void)SendAll(fd, "OKAYgoogle\n");
      } else if (cmd == "shell:getprop ro.product.manufacturer") {
        (void)SendAll(fd, "OKAYGoogle\n");
      } else if (cmd == "shell:getprop ro.product.marketname") {
        (void)SendAll(fd, "OKAYPixel 8 Pro\n");
      } else if (cmd == "shell:getprop ro.build.version.release") {
        (void)SendAll(fd, "OKAY15\n");
      } else if (cmd == "shell:settings get global device_name") {
        (void)SendAll(fd, "OKAYTeymur's Pixel\n");
      } else if (cmd == "shell:dumpsys battery") {
        (void)SendAll(fd,
                      "OKAYCurrent Battery Service state:\n"
                      "  USB powered: true\n"
                      "  level: 88\n"
                      "  scale: 100\n");
      } else {
        (void)SendAll(fd, "FAIL0007unknown");
      }
      ::close(fd);
      return;
    }

    if (req == "host:track-devices-proto-binary") {
      if (refuse_tracker_.load()) {
        ::close(fd);
        return;
      }
      if (!enable_proto_tracker_) {
        (void)SendAll(fd, "FAIL000funknown command");
        ::close(fd);
        return;
      }
      {
        std::lock_guard<std::mutex> lock(mu_);
        active_tracker_fd_ = fd;
      }
      std::string frame = BuildDevicesProtoBytes();
      if (SendAll(fd, "OKAY" + Hex4Prefix(frame))) {
        char dummy = 0;
        (void)::recv(fd, &dummy, 1, 0);
      }
      {
        std::lock_guard<std::mutex> lock(mu_);
        if (active_tracker_fd_ == fd) {
          active_tracker_fd_ = -1;
        }
      }
      ::close(fd);
      return;
    }

    if (req == "host:track-devices-l") {
      if (refuse_tracker_.load()) {
        ::close(fd);
        return;
      }
      {
        std::lock_guard<std::mutex> lock(mu_);
        active_tracker_fd_ = fd;
      }
      std::string text_list =
          "R5CT123456A            device usb:1-2 product:husky model:Pixel_8_Pro device:husky transport_id:10\n"
          "(no serial number)     no permissions (user in plugdev group; are your udev rules wrong?); see [http://developer.android.com/tools/device.html] usb:1-4 transport_id:11\n";
      if (SendAll(fd, "OKAY" + Hex4Prefix(text_list))) {
        char dummy = 0;
        (void)::recv(fd, &dummy, 1, 0);
      }
      {
        std::lock_guard<std::mutex> lock(mu_);
        if (active_tracker_fd_ == fd) {
          active_tracker_fd_ = -1;
        }
      }
      ::close(fd);
      return;
    }

    (void)SendAll(fd, "FAIL000funknown command");
    ::close(fd);
  }

  bool enable_proto_tracker_ = false;
  int listen_fd_ = -1;
  uint16_t port_ = 0;
  std::atomic<bool> stopped_{false};
  std::atomic<bool> refuse_tracker_{false};
  std::mutex mu_;
  int active_tracker_fd_ = -1;
  std::thread accept_thread_;
  std::vector<std::thread> client_threads_;
};

void TestTextFallbackMetadataAndUsbAccessDenied() {
  FakeAdbServer server(/*enable_proto_tracker=*/false);

  // Verify `/proc/net/tcp` UID ownership check on our loopback listener.
  REQUIRE(VerifyLocalAdbServerOwner(server.port(), ::geteuid()) ==
          ListenerOwnerStatus::OwnedByExpectedUid);

  AdbIoConfig cfg{};
  cfg.port = server.port();
  cfg.auto_port_discovery = false;
  cfg.verify_listener_uid = true;
  cfg.allow_start_server = false;

  AdbIoWorker worker(cfg);
  worker.Start();

  int64_t now_ms = 1000;
  AppSnapshot snap = MakeInitialSnapshot(now_ms);
  DeviceEngineState dstate{};
  std::unordered_set<std::string> metadata_requested;

  bool saw_connected_card = false;
  bool saw_usb_denied_card = false;
  bool saw_battery_88 = false;

  for (int attempt = 0; attempt < 60; ++attempt) {
    std::this_thread::sleep_for(std::chrono::milliseconds(40));
    now_ms += 40;
    auto batch = worker.DrainEvents();
    for (const auto& ev : batch) {
      auto res = ReduceWithDeviceState(snap, dstate, ev, now_ms);
      snap = std::move(res.snapshot);
      dstate = std::move(res.device_state);
    }

    // Request metadata for newly eligible connected devices.
    for (const auto& [dev_id, sess] : dstate.metadata_sessions) {
      if (sess.connected_eligible && !metadata_requested.contains(dev_id)) {
        metadata_requested.insert(dev_id);
        worker.RequestMetadataProbe(dev_id, sess.adb_serial);
      }
    }

    if (snap.devices.size() == 2) {
      for (const auto& d : snap.devices) {
        if (d.state == DeviceStateKind::Connected) {
          saw_connected_card = true;
          if (d.battery_percent.has_value() && *d.battery_percent == 88 &&
              d.display_name == "Pixel 8 Pro" &&
              d.detail.android_version == "15") {
            saw_battery_88 = true;
          }
        }
        if (d.state == DeviceStateKind::UsbAccessDenied &&
            d.state_label == Vocabulary::Device::kUsbAccessDeniedStateLabel) {
          saw_usb_denied_card = true;
        }
      }
    }
    if (saw_connected_card && saw_usb_denied_card && saw_battery_88) {
      break;
    }
  }

  worker.Stop();
  server.Stop();

  REQUIRE(saw_connected_card);
  REQUIRE(saw_usb_denied_card);
  REQUIRE(saw_battery_88);
  REQUIRE(snap.health.tray == TrayStatusKind::Attention);
}

void TestProtoTrackerGracePeriodAndPair() {
  FakeAdbServer server(/*enable_proto_tracker=*/true);

  AdbIoConfig cfg{};
  cfg.port = server.port();
  cfg.auto_port_discovery = false;
  cfg.verify_listener_uid = true;
  cfg.allow_start_server = false;

  AdbIoWorker worker(cfg);
  worker.Start();

  int64_t now_ms = 5000;
  AppSnapshot snap = MakeInitialSnapshot(now_ms);
  DeviceEngineState dstate{};
  GraceCommand last_grace = GraceCommand::None();

  bool ready = false;
  for (int i = 0; i < 40; ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(40));
    now_ms += 40;
    auto batch = worker.DrainEvents();
    for (const auto& ev : batch) {
      auto res = ReduceWithDeviceState(snap, dstate, ev, now_ms);
      snap = std::move(res.snapshot);
      dstate = std::move(res.device_state);
      if (res.grace.kind != GraceCommandKind::None) {
        last_grace = res.grace;
      }
    }
    if (snap.health.tray == TrayStatusKind::Connected &&
        snap.devices.size() == 1) {
      ready = true;
      break;
    }
  }
  REQUIRE(ready);

  // Force drop active tracker and pause re-accepts to test the 2-second grace period.
  server.SetRefuseTrackerConnections(true);
  server.DropActiveTracker();

  bool scheduled_grace = false;
  int64_t grace_gen = 0;
  for (int i = 0; i < 30; ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    now_ms += 30;
    auto batch = worker.DrainEvents();
    for (const auto& ev : batch) {
      auto res = ReduceWithDeviceState(snap, dstate, ev, now_ms);
      snap = std::move(res.snapshot);
      dstate = std::move(res.device_state);
      if (res.grace.kind == GraceCommandKind::Schedule) {
        scheduled_grace = true;
        grace_gen = res.grace.generation;
      }
    }
    if (scheduled_grace) {
      break;
    }
  }
  REQUIRE(scheduled_grace);
  // During the 2s grace period, device list is preserved without dropping the card.
  REQUIRE(snap.devices.size() == 1);

  // Fire DeviceTrackerGraceExpiredEvent after 2000ms:
  now_ms += kTrackerFailureGraceMs;
  {
    auto res = ReduceWithDeviceState(
        snap, dstate, DeviceTrackerGraceExpiredEvent{grace_gen}, now_ms);
    snap = std::move(res.snapshot);
    dstate = std::move(res.device_state);
  }
  REQUIRE(snap.devices.empty());
  REQUIRE(snap.health.kind == GlobalHealthKind::AdbRestoring);

  // Re-enable tracker on server and verify automatic recovery to Healthy/Connected.
  server.SetRefuseTrackerConnections(false);
  bool recovered = false;
  for (int i = 0; i < 40; ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    now_ms += 50;
    auto batch = worker.DrainEvents();
    for (const auto& ev : batch) {
      auto res = ReduceWithDeviceState(snap, dstate, ev, now_ms);
      snap = std::move(res.snapshot);
      dstate = std::move(res.device_state);
    }
    if (snap.health.kind == GlobalHealthKind::Healthy &&
        snap.devices.size() == 1) {
      recovered = true;
      break;
    }
  }
  REQUIRE(recovered);

  // Test pairing request on worker:
  worker.RequestPairing("offer-1", "192.168.1.50", 38123, "654321",
                        "Pixel 8 Pro");

  bool paired_waiting = false;
  for (int i = 0; i < 30; ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    now_ms += 30;
    auto batch = worker.DrainEvents();
    for (const auto& ev : batch) {
      auto res = ReduceWithDeviceState(snap, dstate, ev, now_ms);
      snap = std::move(res.snapshot);
      dstate = std::move(res.device_state);
    }
    if (snap.pairing.has_value() &&
        snap.pairing->phase == PairingPhase::PairedWaitingForDevice) {
      paired_waiting = true;
      break;
    }
  }
  REQUIRE(paired_waiting);

  worker.Stop();
  server.Stop();
}

}  // namespace

int main() {
  TestTextFallbackMetadataAndUsbAccessDenied();
  TestProtoTrackerGracePeriodAndPair();
  std::printf("OK fake_adbd integration tests passed\n");
  return 0;
}
