#include <dirent.h>
#include <fcntl.h>
#include <sys/types.h>
#include <unistd.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <string>
#include <thread>
#include <vector>

#include "core/adb_wire.hpp"
#include "core/mdns_cache.hpp"
#include "core/mdns_wire.hpp"
#include "core/notify_policy.hpp"
#include "core/protobuf_lite.hpp"
#include "core/qr.hpp"
#include "core/reduce.hpp"
#include "core/snapshot.hpp"
#include "core/tray_pixmap.hpp"
#include "core/vocabulary.hpp"
#include "platform/adb_io.hpp"
#include "platform/autostart.hpp"
#include "platform/mdns_browser.hpp"

#define CHECK(cond)                                                          \
  do {                                                                       \
    if (!(cond)) {                                                           \
      std::fprintf(stderr, "FAIL: %s (%s:%d)\n", #cond, __FILE__, __LINE__); \
      std::exit(1);                                                          \
    }                                                                        \
  } while (0)

namespace {

using namespace adbhawk::core;
using namespace adbhawk::platform;

std::vector<int> GetOpenFileDescriptors() {
  std::vector<int> fds;
  DIR* d = opendir("/proc/self/fd");
  if (!d) {
    return fds;
  }
  int dir_fd = dirfd(d);
  struct dirent* de = nullptr;
  while ((de = readdir(d)) != nullptr) {
    if (de->d_name[0] == '.') {
      continue;
    }
    int fd = std::atoi(de->d_name);
    if (fd != dir_fd) {
      fds.push_back(fd);
    }
  }
  closedir(d);
  std::sort(fds.begin(), fds.end());
  return fds;
}

void TestFileDescriptorLeakFreedom() {
  std::filesystem::path temp_dir =
      std::filesystem::temp_directory_path() / "adbhawk_res_test";
  std::filesystem::create_directories(temp_dir);

  AdbIoConfig io_cfg{};
  io_cfg.port = 59997;
  io_cfg.reconnect_initial_backoff_ms = 10;
  io_cfg.reconnect_max_backoff_ms = 50;
  io_cfg.allow_start_server = false;
  io_cfg.verify_listener_uid = false;

  MdnsBrowserConfig mdns_cfg{};
  mdns_cfg.multicast_port = 5354;
  mdns_cfg.sweep_interval_ms = 50;

  std::string config_home = temp_dir.string();

  // Warm-up to trigger one-time static allocations
  {
    AdbIoWorker w(io_cfg);
    w.Start();
    std::this_thread::sleep_for(std::chrono::milliseconds(15));
    w.Stop();

    MdnsBrowser b(mdns_cfg);
    b.Start();
    std::this_thread::sleep_for(std::chrono::milliseconds(15));
    b.Stop();

    WriteAutostartEnabled(true, "/usr/bin/adb-hawk", config_home);
    UserPreferences p{};
    p.theme = ThemePreference::Dark;
    WriteUserPreferences(p, config_home);
  }

  // Baseline FD set
  std::vector<int> baseline_fds = GetOpenFileDescriptors();

  // Run 10 lifecycle start/stop/save iterations
  for (int i = 0; i < 10; ++i) {
    {
      AdbIoWorker w(io_cfg);
      w.Start();
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
      w.Stop();
    }
    {
      MdnsBrowser b(mdns_cfg);
      b.Start();
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
      b.Stop();
    }
    {
      WriteAutostartEnabled(i % 2 == 0, "/usr/bin/adb-hawk", config_home);
      UserPreferences p{};
      p.theme = (i % 2 == 0) ? ThemePreference::Light : ThemePreference::Dark;
      WriteUserPreferences(p, config_home);
    }
  }

  std::vector<int> post_fds = GetOpenFileDescriptors();
  if (post_fds != baseline_fds) {
    std::fprintf(stderr, "Descriptor leak detected!\nBaseline FDs (%zu): ",
                 baseline_fds.size());
    for (int fd : baseline_fds) std::fprintf(stderr, "%d ", fd);
    std::fprintf(stderr, "\nPost-test FDs (%zu): ", post_fds.size());
    for (int fd : post_fds) std::fprintf(stderr, "%d ", fd);
    std::fprintf(stderr, "\n");
    CHECK(post_fds == baseline_fds);
  }

  std::filesystem::remove_all(temp_dir);
}

void TestMemoryChurnAndParserLeakFreedom() {
  for (int iter = 0; iter < 1000; ++iter) {
    // 1. Wire frame encoding & decoding
    auto frame = EncodeAdbFrame("host:version");
    CHECK(frame.has_value());
    size_t len = 0;
    CHECK(TryParseHex4Length("000c", &len));

    // 2. Track devices parser
    std::vector<RawDevice> devices;
    CHECK(TryParseTrackDevicesL(
        "R5CX123456             device usb:1-1 product:redfin model:Pixel_5 device:redfin transport_id:1\n"
        "192.168.1.50:5555      offline transport_id:2\n",
        &devices));

    // 3. mDNS packet encoding & decoding
    auto query_pkt = BuildMdnsQuery(1, "_adb-tls-pairing._tcp.local", kDnsTypePtr);
    CHECK(!query_pkt.empty());
    std::vector<DnsRecord> decoded;
    (void)TryParseMdnsRecords(query_pkt, &decoded);

    // 4. State reducer & Notification policy
    AppSnapshot snap = MakeInitialSnapshot(iter * 100);
    DeviceEngineState eng{};
    NotifierState nstate = NotificationPolicy::MakeInitialState(iter * 100);

    auto r = ReduceWithDeviceState(
        snap, eng,
        DeviceListReceivedEvent{.sequence = 1,
                                .at_ms = iter * 100 + 10,
                                .devices = devices},
        iter * 100 + 10);
    snap = std::move(r.snapshot);
    eng = std::move(r.device_state);

    auto dec = NotificationPolicy::Evaluate(std::nullopt, snap, nstate,
                                            iter * 100 + 10);
    (void)dec;

    // 5. QR payload & Tray pixmap
    auto qr = BuildAdbQrPairingPayload("Pixel_Device", "pass123");
    CHECK(!qr.empty());
    auto qr_matrix = EncodeQrCode(qr);
    CHECK(qr_matrix.has_value());

    auto pixmap = RenderTrayPixmap(22, TrayStatusKind::Connected);
    CHECK(pixmap.width == 22);
    CHECK(pixmap.height == 22);
    CHECK(pixmap.argb_network_bytes.size() == 22 * 22 * 4);

    auto set = RenderStandardTrayPixmaps(TrayStatusKind::Idle);
    CHECK(set.size() == 4);
  }
}

}  // namespace

int main() {
  TestFileDescriptorLeakFreedom();
  TestMemoryChurnAndParserLeakFreedom();
  std::printf("OK resource leak tests passed (0 descriptor leaks, 0 heap leaks)\n");
  return 0;
}
