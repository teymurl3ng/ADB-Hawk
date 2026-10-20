#include <arpa/inet.h>
#include <net/if.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <unistd.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "core/mdns_wire.hpp"
#include "platform/adb_locator.hpp"
#include "platform/autostart.hpp"
#include "platform/diagnostics.hpp"
#include "platform/mdns_browser.hpp"
#include "platform/usb_sentinel.hpp"

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

void AppendU16Be(std::vector<uint8_t>* out, uint16_t v) {
  out->push_back(static_cast<uint8_t>((v >> 8U) & 0xFFU));
  out->push_back(static_cast<uint8_t>(v & 0xFFU));
}

void AppendU32Be(std::vector<uint8_t>* out, uint32_t v) {
  out->push_back(static_cast<uint8_t>((v >> 24U) & 0xFFU));
  out->push_back(static_cast<uint8_t>((v >> 16U) & 0xFFU));
  out->push_back(static_cast<uint8_t>((v >> 8U) & 0xFFU));
  out->push_back(static_cast<uint8_t>(v & 0xFFU));
}

void AppendDnsName(std::vector<uint8_t>* out, std::string_view name) {
  size_t pos = 0;
  while (pos < name.size()) {
    size_t dot = name.find('.', pos);
    std::string_view label = (dot == std::string_view::npos)
                                 ? name.substr(pos)
                                 : name.substr(pos, dot - pos);
    pos = (dot == std::string_view::npos) ? name.size() : (dot + 1);
    if (label.empty()) {
      continue;
    }
    out->push_back(static_cast<uint8_t>(label.size()));
    for (char c : label) {
      out->push_back(static_cast<uint8_t>(c));
    }
  }
  out->push_back(0);
}

std::vector<uint8_t> BuildMdnsPairingAnnouncementPacket(uint32_t ttl) {
  std::vector<uint8_t> pkt;
  AppendU16Be(&pkt, 0);       // ID = 0
  AppendU16Be(&pkt, 0x8400);  // QR=1, AA=1
  AppendU16Be(&pkt, 0);       // QDCOUNT = 0
  AppendU16Be(&pkt, 4);       // ANCOUNT = 4 (PTR, SRV, TXT, A)
  AppendU16Be(&pkt, 0);       // NSCOUNT = 0
  AppendU16Be(&pkt, 0);       // ARCOUNT = 0

  const std::string_view service_domain = "_adb-tls-pairing._tcp.local";
  const std::string_view instance_domain =
      "adb-R5CT123456A-pair._adb-tls-pairing._tcp.local";
  const std::string_view host_domain = "pixel8.local";

  // 1. PTR record: _adb-tls-pairing._tcp.local -> adb-R5CT123456A-pair._adb-tls-pairing._tcp.local
  AppendDnsName(&pkt, service_domain);
  AppendU16Be(&pkt, kDnsTypePtr);
  AppendU16Be(&pkt, kDnsClassInet);
  AppendU32Be(&pkt, ttl);
  std::vector<uint8_t> ptr_rdata;
  AppendDnsName(&ptr_rdata, instance_domain);
  AppendU16Be(&pkt, static_cast<uint16_t>(ptr_rdata.size()));
  pkt.insert(pkt.end(), ptr_rdata.begin(), ptr_rdata.end());

  // 2. SRV record: instance_domain -> pixel8.local:38123
  AppendDnsName(&pkt, instance_domain);
  AppendU16Be(&pkt, kDnsTypeSrv);
  AppendU16Be(&pkt, kDnsClassInet);
  AppendU32Be(&pkt, ttl);
  std::vector<uint8_t> srv_rdata;
  AppendU16Be(&srv_rdata, 0);      // priority
  AppendU16Be(&srv_rdata, 0);      // weight
  AppendU16Be(&srv_rdata, 38123);  // port
  AppendDnsName(&srv_rdata, host_domain);
  AppendU16Be(&pkt, static_cast<uint16_t>(srv_rdata.size()));
  pkt.insert(pkt.end(), srv_rdata.begin(), srv_rdata.end());

  // 3. TXT record: instance_domain -> ["name=Pixel 8 Pro", "v=1"]
  AppendDnsName(&pkt, instance_domain);
  AppendU16Be(&pkt, kDnsTypeTxt);
  AppendU16Be(&pkt, kDnsClassInet);
  AppendU32Be(&pkt, ttl);
  std::vector<uint8_t> txt_rdata;
  const std::string_view kv1 = "name=Pixel 8 Pro";
  txt_rdata.push_back(static_cast<uint8_t>(kv1.size()));
  txt_rdata.insert(txt_rdata.end(), kv1.begin(), kv1.end());
  AppendU16Be(&pkt, static_cast<uint16_t>(txt_rdata.size()));
  pkt.insert(pkt.end(), txt_rdata.begin(), txt_rdata.end());

  // 4. A record: pixel8.local -> 192.168.1.50
  AppendDnsName(&pkt, host_domain);
  AppendU16Be(&pkt, kDnsTypeA);
  AppendU16Be(&pkt, kDnsClassInet);
  AppendU32Be(&pkt, ttl);
  AppendU16Be(&pkt, 4);
  pkt.push_back(192);
  pkt.push_back(168);
  pkt.push_back(1);
  pkt.push_back(50);

  return pkt;
}

void TestDiagnosticsProcParsing() {
  uid_t self_uid = ::geteuid();
  std::string table4 =
      "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops\n"
      "   1: 00000000:14E9 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 12345 2 00000000 0\n"
      "   2: 00000000:14E9 00000000:0000 07 00000000:00000000 00:00000000 00000000  " +
      std::to_string(self_uid) +
      "        0 12346 2 00000000 0\n"
      "   3: 00000000:0035 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 12347 2 00000000 0\n";
  auto holders = ParseUdp5353HoldersFromProc(table4, "", self_uid);
  REQUIRE(holders.size() == 1);
  REQUIRE(holders[0] == "root");
}

void TestAutostartAndPreferences() {
  REQUIRE(FormatDesktopExecValue("/opt/adb$hawk/bin/adb-hawk %f") ==
          "\"/opt/adb\\$hawk/bin/adb-hawk %%f\"");

  std::string tmp_dir =
      "/tmp/adbhawk-test-" + std::to_string(static_cast<long>(::getpid()));

  REQUIRE(WriteAutostartEnabled(true, "/usr/local/bin/adb-hawk", tmp_dir));
  REQUIRE(ReadAutostartEnabled(tmp_dir));

  struct stat dir_st {};
  REQUIRE(::stat((tmp_dir + "/autostart").c_str(), &dir_st) == 0);
  REQUIRE((dir_st.st_mode & 0777) == 0700);

  struct stat file_st {};
  REQUIRE(::stat((tmp_dir + "/autostart/adb-hawk.desktop").c_str(), &file_st) ==
          0);
  REQUIRE((file_st.st_mode & 0777) == 0600);

  REQUIRE(WriteAutostartEnabled(false, "/usr/local/bin/adb-hawk", tmp_dir));
  REQUIRE(!ReadAutostartEnabled(tmp_dir));

  UserPreferences prefs{};
  prefs.theme = ThemePreference::Light;
  prefs.show_status_bubble = true;
  REQUIRE(WriteUserPreferences(prefs, tmp_dir));
  REQUIRE(ReadUserPreferences(tmp_dir) == prefs);

  // Test backward compatibility fallback from legacy adb-view paths
  (void)::mkdir((tmp_dir + "/adb-view").c_str(), 0700);
  FILE* fp = ::fopen((tmp_dir + "/adb-view/prefs.ini").c_str(), "w");
  if (fp != nullptr) {
    std::fputs("[Preferences]\ntheme=dark\nstatus_bubble=true\n", fp);
    std::fclose(fp);
  }
  // Remove adb-hawk prefs to force fallback
  (void)::unlink((tmp_dir + "/adb-hawk/prefs.ini").c_str());
  UserPreferences fallback_prefs = ReadUserPreferences(tmp_dir);
  REQUIRE(fallback_prefs.theme == ThemePreference::Dark);
  REQUIRE(fallback_prefs.show_status_bubble == true);

  (void)::unlink((tmp_dir + "/autostart/adb-hawk.desktop").c_str());
  (void)::unlink((tmp_dir + "/autostart/adb-view.desktop").c_str());
  (void)::rmdir((tmp_dir + "/autostart").c_str());
  (void)::unlink((tmp_dir + "/adb-hawk/prefs.ini").c_str());
  (void)::rmdir((tmp_dir + "/adb-hawk").c_str());
  (void)::unlink((tmp_dir + "/adb-view/prefs.ini").c_str());
  (void)::rmdir((tmp_dir + "/adb-view").c_str());
  (void)::rmdir(tmp_dir.c_str());
}

void TestMdnsBrowserMulticastCoexistenceAndGoodbye() {
  constexpr uint16_t kTestPort = 15353;

  // Bind a competing `SO_REUSEADDR` socket on the same port (simulating avahi-daemon).
  int competing_fd =
      ::socket(AF_INET, SOCK_DGRAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
  REQUIRE(competing_fd >= 0);
  int one = 1;
  REQUIRE(::setsockopt(competing_fd, SOL_SOCKET, SO_REUSEADDR, &one,
                       sizeof(one)) == 0);
  struct sockaddr_in bind_addr {};
  bind_addr.sin_family = AF_INET;
  bind_addr.sin_addr.s_addr = htonl(INADDR_ANY);
  bind_addr.sin_port = htons(kTestPort);
  REQUIRE(::bind(competing_fd, reinterpret_cast<struct sockaddr*>(&bind_addr),
                 sizeof(bind_addr)) == 0);

  MdnsBrowserConfig cfg{};
  cfg.multicast_port = kTestPort;
  cfg.include_loopback = true;
  cfg.sweep_interval_ms = 200;

  MdnsBrowser browser(cfg);
  browser.Start();

  std::this_thread::sleep_for(std::chrono::milliseconds(80));

  // Close the competing socket now that coexistence during bind is verified,
  // ensuring subsequent loopback packets are directed to the browser.
  ::close(competing_fd);
  competing_fd = -1;

  // Send an mDNS announcement over loopback multicast and unicast loopback.
  int tx_fd = ::socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, 0);
  REQUIRE(tx_fd >= 0);
  unsigned char loop = 1;
  (void)::setsockopt(tx_fd, IPPROTO_IP, IP_MULTICAST_LOOP, &loop, sizeof(loop));
  unsigned int lo_idx = ::if_nametoindex("lo");
  if (lo_idx != 0) {
    struct ip_mreqn mreq {};
    mreq.imr_ifindex = static_cast<int>(lo_idx);
    (void)::setsockopt(tx_fd, IPPROTO_IP, IP_MULTICAST_IF, &mreq, sizeof(mreq));
  }

  auto announce_pkt = BuildMdnsPairingAnnouncementPacket(/*ttl=*/120);
  struct sockaddr_in dst {};
  dst.sin_family = AF_INET;
  dst.sin_port = htons(kTestPort);

  // Send to 224.0.0.251 and also 127.0.0.1 so it succeeds regardless of sandbox multicast routing
  (void)::inet_pton(AF_INET, "224.0.0.251", &dst.sin_addr);
  (void)::sendto(tx_fd, announce_pkt.data(), announce_pkt.size(), 0,
                 reinterpret_cast<const struct sockaddr*>(&dst), sizeof(dst));

  dst.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  ssize_t sent = ::sendto(tx_fd, announce_pkt.data(), announce_pkt.size(), 0,
                          reinterpret_cast<const struct sockaddr*>(&dst),
                          sizeof(dst));
  REQUIRE(sent == static_cast<ssize_t>(announce_pkt.size()));

  bool saw_offer = false;
  for (int i = 0; i < 40; ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    auto evs = browser.DrainEvents();
    for (const auto& ev : evs) {
      if (const auto* mf = std::get_if<MdnsFallbackReceivedEvent>(&ev)) {
        if (mf->services.size() == 1 &&
            mf->services[0].instance_name == "adb-R5CT123456A-pair" &&
            mf->services[0].port == 38123 &&
            mf->services[0].ipv4 == "192.168.1.50" &&
            mf->services[0].product_model == "Pixel 8 Pro") {
          saw_offer = true;
        }
      }
    }
    if (saw_offer) {
      break;
    }
  }
  REQUIRE(saw_offer);
  REQUIRE(browser.CurrentAnnouncementCount() == 1);

  // Trigger RejoinAndQuery() (resume from suspend simulation, F10)
  browser.RejoinAndQuery();
  std::this_thread::sleep_for(std::chrono::milliseconds(50));

  // Send TTL=0 goodbye packet and verify immediate eviction:
  auto goodbye_pkt = BuildMdnsPairingAnnouncementPacket(/*ttl=*/0);
  (void)::inet_pton(AF_INET, "224.0.0.251", &dst.sin_addr);
  (void)::sendto(tx_fd, goodbye_pkt.data(), goodbye_pkt.size(), 0,
                 reinterpret_cast<const struct sockaddr*>(&dst), sizeof(dst));

  dst.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  sent = ::sendto(tx_fd, goodbye_pkt.data(), goodbye_pkt.size(), 0,
                  reinterpret_cast<const struct sockaddr*>(&dst),
                  sizeof(dst));
  REQUIRE(sent == static_cast<ssize_t>(goodbye_pkt.size()));

  bool saw_eviction = false;
  for (int i = 0; i < 40; ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    auto evs = browser.DrainEvents();
    for (const auto& ev : evs) {
      if (const auto* mf = std::get_if<MdnsFallbackReceivedEvent>(&ev)) {
        if (mf->services.empty()) {
          saw_eviction = true;
        }
      }
    }
    if (saw_eviction) {
      break;
    }
  }
  REQUIRE(saw_eviction);
  REQUIRE(browser.CurrentAnnouncementCount() == 0);

  browser.Stop();
  ::close(tx_fd);
  if (competing_fd >= 0) {
    ::close(competing_fd);
  }
}

void TestUsbSentinelAndPortDiscovery() {
  // 1. Test FindListeningTcpPortsForUid with mock /proc/net/tcp content
  std::string_view mock_tcp =
      "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n"
      "   0: 0100007F:13AD 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 138574 2 0000000000000000 100 0 0 10 0\n"
      "   1: 0100007F:13AE 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 138575 2 0000000000000000 100 0 0 10 0\n"
      "   2: 0100007F:13AF 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1001        0 138576 2 0000000000000000 100 0 0 10 0\n"
      "   3: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 138577 2 0000000000000000 100 0 0 10 0\n";
  auto ports = FindListeningTcpPortsForUid(mock_tcp, "", 1000, 5037, 5050);
  REQUIRE(ports.size() == 2);
  REQUIRE(ports[0] == 5037);
  REQUIRE(ports[1] == 5038);

  // 2. Test ScanSysfsUsbAdbDevices with a temporary mock sysfs directory
  char temp_template[] = "/tmp/adb_hawk_sysfs_test_XXXXXX";
  char* temp_dir = ::mkdtemp(temp_template);
  REQUIRE(temp_dir != nullptr);

  std::string base(temp_dir);
  std::string dev1_dir = base + "/1-4";
  std::string iface1_dir = base + "/1-4:1.3";
  std::string dev2_dir = base + "/1-2";
  std::string iface2_dir = base + "/1-2:1.0";

  REQUIRE(::mkdir(dev1_dir.c_str(), 0755) == 0);
  REQUIRE(::mkdir(iface1_dir.c_str(), 0755) == 0);
  REQUIRE(::mkdir(dev2_dir.c_str(), 0755) == 0);
  REQUIRE(::mkdir(iface2_dir.c_str(), 0755) == 0);

  auto write_file = [](const std::string& p, std::string_view content) {
    FILE* f = std::fopen(p.c_str(), "w");
    REQUIRE(f != nullptr);
    std::fwrite(content.data(), 1, content.size(), f);
    std::fclose(f);
  };

  write_file(iface1_dir + "/bInterfaceClass", "ff\n");
  write_file(iface1_dir + "/bInterfaceSubClass", "42\n");
  write_file(iface1_dir + "/bInterfaceProtocol", "01\n");
  write_file(dev1_dir + "/idVendor", "04e8\n");
  write_file(dev1_dir + "/idProduct", "6860\n");
  write_file(dev1_dir + "/product", "SAMSUNG_Android\n");
  write_file(dev1_dir + "/manufacturer", "SAMSUNG\n");
  write_file(dev1_dir + "/serial", "R8VXA02SNKH\n");

  write_file(iface2_dir + "/bInterfaceClass", "03\n");
  write_file(iface2_dir + "/bInterfaceSubClass", "01\n");
  write_file(iface2_dir + "/bInterfaceProtocol", "02\n");
  write_file(dev2_dir + "/idVendor", "145f\n");
  write_file(dev2_dir + "/product", "Gaming Mouse\n");

  auto found = ScanSysfsUsbAdbDevices(base);
  REQUIRE(found.size() == 1);
  REQUIRE(found[0].bus_id == "1-4");
  REQUIRE(found[0].id_vendor == "04e8");
  REQUIRE(found[0].id_product == "6860");
  REQUIRE(found[0].product == "SAMSUNG_Android");
  REQUIRE(found[0].manufacturer == "SAMSUNG");
  REQUIRE(found[0].serial == "R8VXA02SNKH");

  // Clean up mock files and directories
  std::remove((iface1_dir + "/bInterfaceClass").c_str());
  std::remove((iface1_dir + "/bInterfaceSubClass").c_str());
  std::remove((iface1_dir + "/bInterfaceProtocol").c_str());
  std::remove((dev1_dir + "/idVendor").c_str());
  std::remove((dev1_dir + "/idProduct").c_str());
  std::remove((dev1_dir + "/product").c_str());
  std::remove((dev1_dir + "/manufacturer").c_str());
  std::remove((dev1_dir + "/serial").c_str());
  std::remove((iface2_dir + "/bInterfaceClass").c_str());
  std::remove((iface2_dir + "/bInterfaceSubClass").c_str());
  std::remove((iface2_dir + "/bInterfaceProtocol").c_str());
  std::remove((dev2_dir + "/idVendor").c_str());
  std::remove((dev2_dir + "/product").c_str());
  ::rmdir(iface1_dir.c_str());
  ::rmdir(dev1_dir.c_str());
  ::rmdir(iface2_dir.c_str());
  ::rmdir(dev2_dir.c_str());
  ::rmdir(base.c_str());
}

}  // namespace

int main() {
  TestDiagnosticsProcParsing();
  TestAutostartAndPreferences();
  TestMdnsBrowserMulticastCoexistenceAndGoodbye();
  TestUsbSentinelAndPortDiscovery();
  std::printf("OK mDNS browser, diagnostics, autostart, and USB sentinel tests passed\n");
  return 0;
}
