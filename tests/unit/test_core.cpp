#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "core/adb_wire.hpp"
#include "core/mdns_cache.hpp"
#include "core/mdns_wire.hpp"
#include "core/motion.hpp"
#include "core/notify_policy.hpp"
#include "core/protobuf_lite.hpp"
#include "core/qr.hpp"
#include "core/reduce.hpp"
#include "core/snapshot.hpp"
#include "core/tray_pixmap.hpp"
#include "core/vocabulary.hpp"

namespace {

int g_failures = 0;

void Check(bool cond, const char* expr, const char* file, int line) {
  if (!cond) {
    std::fprintf(stderr, "FAIL %s:%d: %s\n", file, line, expr);
    ++g_failures;
  }
}

#define CHECK(expr) Check((expr), #expr, __FILE__, __LINE__)

using namespace adbhawk::core;

void TestVocabularyAndDisplayNames() {
  CHECK(Vocabulary::Health::kAdbUnavailableHint ==
        "Install Android Platform Tools or make adb available on your PATH.");
  CHECK(Vocabulary::Device::kUsbAccessDeniedStateLabel ==
        "USB access not permitted");
  CHECK(Vocabulary::Device::kUsbAccessDeniedGuidance ==
        "This computer isn't allowing access to the phone's USB connection.");
  CHECK(Vocabulary::Menu::kStartAtLogin == "Start at login");

  // Reject raw protocol/transport identifiers as display names.
  CHECK(ResolveDeviceDisplayName(std::nullopt, std::nullopt,
                                 "adb-R5CX123456-a1B2c3", std::nullopt,
                                 std::nullopt, "192.168.1.40:5555",
                                 "studio-998877") == "Android device");
  // Brand + technical model code -> "Samsung SM-S921B"
  CHECK(ResolveDeviceDisplayName(std::nullopt, std::nullopt, "SM-S921B",
                                 "samsung", std::nullopt) ==
        "Samsung SM-S921B");
  // Marketing name wins
  CHECK(ResolveDeviceDisplayName("Galaxy S24", "My Phone", "SM-S921B",
                                 "samsung") == "Galaxy S24");

  CHECK(FormatAge("Connected {age}", 1000, 3000) == "Connected Just now");
  CHECK(FormatAge("Connected {age}", 1000, 16000) == "Connected 15s ago");
  CHECK(FormatAge("{age}", 0, 120'000) == "2 minutes ago");
  CHECK(FormatAge("{age}", 0, 180'000) == "3m ago");
  CHECK(FormatAge("{age}", 0, 7'200'000) == "2h ago");
}

void TestAdbWireAndTrackDevicesL() {
  auto frame = EncodeAdbFrame("host:version");
  CHECK(frame.has_value());
  CHECK(*frame == "000chost:version");

  size_t len = 0;
  CHECK(TryParseHex4Length("0029", &len) && len == 0x29u);
  CHECK(!TryParseHex4Length("002g", &len));
  CHECK(!TryParseHex4Length("002", &len));

  CHECK(ParseAdbStatusHeader("OKAY") == AdbWireStatus::Okay);
  CHECK(ParseAdbStatusHeader("FAIL") == AdbWireStatus::Fail);
  CHECK(ParseAdbStatusHeader("NOPE") == AdbWireStatus::Malformed);

  // Linux track-devices-l with USB device, Wi-Fi device, and two Linux `no permissions` forms:
  std::string payload =
      "R5CX123456             device usb:1-2 product:e1q model:SM_S921B device:e1q transport_id:1\n"
      "192.168.1.42:38123     device product:panther model:Pixel_7 device:panther transport_id:2\n"
      "R5CX999999             no permissions (missing udev rules?); see [http://developer.android.com/tools/device.html] usb:1-3 transport_id:3\n"
      "(no serial number)     no permissions (user teymur is not in the plugdev group); see [http://developer.android.com/tools/device.html] usb:1-4 transport_id:4\n";

  std::vector<RawDevice> devs;
  CHECK(TryParseTrackDevicesL(payload, &devs));
  CHECK(devs.size() == 4u);

  CHECK(devs[0].serial == "R5CX123456");
  CHECK(devs[0].state_token == "device");
  CHECK(devs[0].connection_type == 1);
  CHECK(devs[0].model == "SM_S921B");

  CHECK(devs[1].serial == "192.168.1.42:38123");
  CHECK(devs[1].state_token == "device");
  CHECK(devs[1].connection_type == 2);
  CHECK(devs[1].model == "Pixel_7");

  CHECK(devs[2].serial == "R5CX999999");
  CHECK(devs[2].state_token == "no permissions");
  CHECK(devs[2].connection_type == 1);
  CHECK(devs[2].usb_devpath == "usb:1-3");

  CHECK(devs[3].serial == "(no serial number)");
  CHECK(devs[3].state_token == "no permissions");
  CHECK(devs[3].connection_type == 1);
  CHECK(devs[3].usb_devpath == "usb:1-4");

  // Metadata parsers
  std::string dumpsys =
      "Current Battery Service state:\n"
      "  AC powered: false\n"
      "  USB powered: true\n"
      "  Capacity level: 3\n"
      "  level: 87\n";
  CHECK(TryParseBatteryPercent(dumpsys) == 87);
  CHECK(!TryParseBatteryPercent("  Capacity level: 50\n").has_value());
  CHECK(!TryParseBatteryPercent("  level: 105\n").has_value());

  CHECK(!TryParseGetpropValue("null\n").has_value());
  CHECK(!TryParseGetpropValue("unknown\r\n").has_value());
  CHECK(TryParseGetpropValue("15\n") == "15");

  // Version parsers (for R-10 server/client protocol match check)
  CHECK(TryParseAdbServerVersionHex("0029") == 41);
  CHECK(TryParseAdbClientVersionOutput(
            "Android Debug Bridge version 1.0.41\nVersion 34.0.4-debian\n") ==
        41);

  // Mdns services list parser
  std::string mdns_list =
      "List of discovered mdns services\n"
      "adb-R5CX123456-AbCdEf\t_adb-tls-connect._tcp\t192.168.1.42:38123\n"
      "adb-R5CX123456-Pair01\t_adb-tls-pairing._tcp\t[fe80::1234]:41234\n"
      "bad-unbracketed-v6\t_adb-tls-pairing._tcp\tfe80::1234:41234\n";
  auto parsed_mdns = ParseMdnsServicesList(mdns_list);
  CHECK(parsed_mdns.size() == 2u);
  CHECK(parsed_mdns[0].kind == MdnsServiceKind::Connect);
  CHECK(parsed_mdns[0].ipv4 == "192.168.1.42");
  CHECK(parsed_mdns[0].port == 38123);
  CHECK(parsed_mdns[1].kind == MdnsServiceKind::Pairing);
  CHECK(parsed_mdns[1].ipv6.size() == 1u &&
        parsed_mdns[1].ipv6[0] == "fe80::1234");
  CHECK(parsed_mdns[1].port == 41234);
}

void TestProtobufLite() {
  // Build a valid AdbServerStatus proto with unknown future field 99:
  // field 3 (mdns_backend = 2 Openscreen): tag 0x18, val 0x02
  // field 5 (version = "35.0.2"): tag 0x2A, len 6, "35.0.2"
  // field 12 (mdns_enabled = true): tag 0x60, val 0x01
  // field 99 (unknown varint): tag (99<<3)|0 = 792 -> 0x98 0x06, val 0x2A
  std::vector<uint8_t> status_bytes = {
      0x18, 0x02, 0x2A, 0x06, '3',  '5',  '.', '0',
      '.',  '2',  0x60, 0x01, 0x98, 0x06, 0x2A};
  AdbServerStatus st;
  CHECK(TryParseAdbServerStatusProto(status_bytes, &st));
  CHECK(st.mdns_backend == AdbMdnsBackend::Openscreen);
  CHECK(st.version == "35.0.2");
  CHECK(st.mdns_enabled == true);

  // Build Devices proto with one USB device in StateNoPermission (4):
  // Inner Device: field 1 ("R5C1") = 0x0A 0x04 'R' '5' 'C' '1'
  //               field 2 (state = 4) = 0x10 0x04
  //               field 7 (connection_type = 1 USB) = 0x38 0x01
  std::vector<uint8_t> dev_bytes = {0x0A, 0x0A, 0x0A, 0x04, 'R',  '5',
                                    'C',  '1',  0x10, 0x04, 0x38, 0x01};
  std::vector<RawDevice> raw_devs;
  CHECK(TryParseDevicesProto(dev_bytes, &raw_devs));
  CHECK(raw_devs.size() == 1u);
  CHECK(raw_devs[0].serial == "R5C1");
  CHECK(raw_devs[0].proto_state == 4);
  CHECK(raw_devs[0].connection_type == 1);
}

void TestMdnsWireAndCache() {
  auto query = BuildMdnsQuery(0x1234, "_adb-tls-pairing._tcp.local", kDnsTypePtr);
  CHECK(!query.empty());

  // Malicious compression loop at offset 12 (`0xC0 0x0C` jumping to itself):
  std::vector<uint8_t> loop_pkt = {
      0x00, 0x00, 0x84, 0x00,  // QR=1 response
      0x00, 0x00,              // QDCOUNT=0
      0x00, 0x01,              // ANCOUNT=1
      0x00, 0x00, 0x00, 0x00,
      0xC0, 0x0C,              // self-pointer loop!
      0x00, 0x0C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x78, 0x00, 0x02, 0xC0, 0x0C};
  std::vector<DnsRecord> bad_records;
  CHECK(!TryParseMdnsRecords(loop_pkt, &bad_records));

  // Correlate out-of-order PTR + SRV + TXT + A in MdnsRecordCache:
  MdnsRecordCache cache;
  DnsRecord ptr_rec{};
  ptr_rec.name = "_adb-tls-pairing._tcp.local";
  ptr_rec.type = kDnsTypePtr;
  ptr_rec.ttl = 120;
  ptr_rec.ptr_target = "adb-R5CX123456-Pair01._adb-tls-pairing._tcp.local";

  DnsRecord srv_rec{};
  srv_rec.name = "adb-R5CX123456-Pair01._adb-tls-pairing._tcp.local";
  srv_rec.type = kDnsTypeSrv;
  srv_rec.ttl = 120;
  srv_rec.srv_target = "phone.local";
  srv_rec.srv_port = 41999;

  DnsRecord txt_rec{};
  txt_rec.name = "adb-R5CX123456-Pair01._adb-tls-pairing._tcp.local";
  txt_rec.type = kDnsTypeTxt;
  txt_rec.ttl = 120;
  txt_rec.txt_pairs = {{"name", "Pixel 8"}, {"api", "35"}};

  DnsRecord a_rec{};
  a_rec.name = "phone.local";
  a_rec.type = kDnsTypeA;
  a_rec.ttl = 120;
  a_rec.ipv4_address = "192.168.1.50";

  CHECK(!cache.Absorb(std::span<const DnsRecord>(&ptr_rec, 1), 1000));
  CHECK(cache.Services().empty());  // Incomplete until SRV + A arrive
  std::vector<DnsRecord> rest = {srv_rec, txt_rec, a_rec};
  CHECK(cache.Absorb(rest, 1100));
  CHECK(cache.Services().size() == 1u);
  CHECK(cache.Services()[0].instance_name == "adb-R5CX123456-Pair01");
  CHECK(cache.Services()[0].port == 41999);
  CHECK(cache.Services()[0].ipv4 == "192.168.1.50");
  CHECK(cache.Services()[0].product_model == "Pixel 8");
  CHECK(cache.Services()[0].serial == "R5CX123456");

  // Goodbye packet (TTL = 0) evicts immediately
  DnsRecord goodbye = ptr_rec;
  goodbye.ttl = 0;
  CHECK(cache.Absorb(std::span<const DnsRecord>(&goodbye, 1), 1200));
  CHECK(cache.Services().empty());
}

void TestStateReducerAndLinuxUsbAccessDenied() {
  AppSnapshot snap = MakeInitialSnapshot(0);
  DeviceEngineState eng;

  // Receive a Linux `no permissions` USB device + a connected Wi-Fi device for the same serial:
  std::vector<RawDevice> raw;
  CHECK(TryParseTrackDevicesL(
      "R5CX123456             no permissions (missing udev rules?); see [http://developer.android.com/tools/device.html] usb:1-2 transport_id:1\n",
      &raw));

  auto r1 = ReduceWithDeviceState(
      snap, eng,
      DeviceListReceivedEvent{.sequence = 1, .at_ms = 1000, .devices = raw},
      1000);
  snap = r1.snapshot;
  eng = r1.device_state;

  CHECK(snap.devices.size() == 1u);
  CHECK(snap.devices[0].state == DeviceStateKind::UsbAccessDenied);
  CHECK(snap.devices[0].state_label == "USB access not permitted");
  CHECK(snap.devices[0].guidance ==
        "This computer isn't allowing access to the phone's USB connection.");
  CHECK(snap.health.tray == TrayStatusKind::Attention);
  CHECK(snap.health.tray_tooltip ==
        "USB access not permitted: Android device");

  // Tracker failure arms 2s grace period without immediately dropping devices:
  auto r2 = ReduceWithDeviceState(snap, eng, DeviceTrackerFailedEvent{}, 1500);
  snap = r2.snapshot;
  eng = r2.device_state;
  CHECK(r2.grace.kind == GraceCommandKind::Schedule);
  CHECK(snap.devices.size() == 1u);

  // Grace expiry clears devices:
  auto r3 = ReduceWithDeviceState(
      snap, eng, DeviceTrackerGraceExpiredEvent{r2.grace.generation}, 3500);
  snap = r3.snapshot;
  eng = r3.device_state;
  CHECK(snap.devices.empty());
  CHECK(snap.health.tray == TrayStatusKind::Idle);
}

void TestDoubleTransportIdentityCollapse() {
  AppSnapshot snap = MakeInitialSnapshot(0);
  DeviceEngineState eng;

  std::vector<RawDevice> devs;
  CHECK(TryParseTrackDevicesL(
      "192.168.18.169:36007   device product:e1qcsx model:SM_S921W device:e1q transport_id:26\n"
      "adb-RFCX116CYYX-JYuekC._adb-tls-connect._tcp device product:e1qcsx model:SM_S921W device:e1q transport_id:25\n",
      &devs));
  CHECK(devs.size() == 2u);

  // Before mDNS services arrive, the two transports appear with per-port and instance IDs:
  auto r1 = ReduceWithDeviceState(
      snap, eng,
      DeviceListReceivedEvent{.sequence = 1, .at_ms = 100, .devices = devs},
      100);
  snap = r1.snapshot;
  eng = r1.device_state;
  CHECK(snap.devices.size() == 2u);

  // mDNS services arrive afterwards:
  std::string mdns_text =
      "List of discovered mdns services\n"
      "adb-RFCX116CYYX-JYuekC\t_adb-tls-connect._tcp\t192.168.18.169:36007\n";
  auto svcs = ParseMdnsServicesList(mdns_text);
  CHECK(svcs.size() == 1u);

  auto r2 = ReduceWithDeviceState(
      snap, eng,
      MdnsServicesReceivedEvent{.sequence = 1, .at_ms = 150, .services = svcs},
      150);
  snap = r2.snapshot;
  eng = r2.device_state;

  // Now the transports must collapse to ONE card:
  CHECK(snap.devices.size() == 1u);
  CHECK(snap.devices[0].id == "wifi:RFCX116CYYX");
  CHECK(snap.devices[0].transport == TransportKind::WiFi);
  CHECK(snap.devices[0].detail.serial.has_value() &&
        *snap.devices[0].detail.serial == "RFCX116CYYX");
  CHECK(snap.devices[0].detail.address.has_value() &&
        *snap.devices[0].detail.address == "192.168.18.169:36007");

  // Reverse ordering test: instance is first, ip:port is second:
  std::vector<RawDevice> rev_devs = {devs[1], devs[0]};
  AppSnapshot snap_rev = MakeInitialSnapshot(0);
  DeviceEngineState eng_rev;
  eng_rev.latest_mdns_services = svcs;
  auto r_rev = ReduceWithDeviceState(
      snap_rev, eng_rev,
      DeviceListReceivedEvent{.sequence = 1, .at_ms = 200, .devices = rev_devs},
      200);
  CHECK(r_rev.snapshot.devices.size() == 1u);
  CHECK(r_rev.snapshot.devices[0].id == "wifi:RFCX116CYYX");
  CHECK(r_rev.snapshot.devices[0].detail.address.has_value() &&
        *r_rev.snapshot.devices[0].detail.address == "192.168.18.169:36007");
}

void TestNotificationPolicyAndBurstCollapse() {
  NotifierState nstate = NotificationPolicy::MakeInitialState(0);
  AppSnapshot s0 = MakeInitialSnapshot(0);

  // Within 5s startup suppression, transitions establish baseline without arming holds.
  std::vector<RawDevice> raw1;
  CHECK(TryParseTrackDevicesL(
      "SER1 device usb:1-1 model:Pixel_8\n", &raw1));
  AppSnapshot s1 = Reduce(
      s0, DeviceListReceivedEvent{.sequence = 1, .at_ms = 2000, .devices = raw1},
      2000);
  auto dec0 = NotificationPolicy::Evaluate(s0, s1, nstate, 2000);
  CHECK(dec0.arm_holds.empty());
  nstate = dec0.next_state;

  // At t=6000ms (after 5s startup window), 4 devices connect simultaneously -> burst collapse!
  std::vector<RawDevice> raw5;
  CHECK(TryParseTrackDevicesL(
      "SER1 device usb:1-1 model:Pixel_8\n"
      "SER2 device usb:1-2 model:Pixel_7\n"
      "SER3 device usb:1-3 model:Pixel_6\n"
      "SER4 device usb:1-4 model:Pixel_5\n"
      "SER5 device usb:1-5 model:Pixel_4\n",
      &raw5));
  AppSnapshot s2 = Reduce(
      s1, DeviceListReceivedEvent{.sequence = 2, .at_ms = 6000, .devices = raw5},
      6000);
  auto dec1 = NotificationPolicy::Evaluate(s1, s2, nstate, 6000);
  CHECK(dec1.arm_holds.size() == 4u);
  nstate = dec1.next_state;

  std::vector<NotificationIntent> ready;
  for (const auto& h : dec1.arm_holds) {
    auto m = NotificationPolicy::TryMaterializeHold(h, s2, nstate, 6500);
    CHECK(m.has_value());
    ready.push_back(*m);
  }
  auto [emitted, next_nstate] =
      NotificationPolicy::ApplyBurstAndRecord(ready, nstate, 6500);
  // 3 individual + 1 collapsed ("1 devices changed") = 4 total
  CHECK(emitted.size() == 4u);
  CHECK(emitted[3].key.kind == NotificationKind::DevicesChanged);
}

void TestQrMotionAndTrayPixmap() {
  std::string payload = BuildAdbQrPairingPayload("adbview-1234abcd", "654321");
  CHECK(payload == "WIFI:T:ADB;S:adbview-1234abcd;P:654321;;");
  auto uri = RenderQrSvgDataUri(payload);
  CHECK(uri.has_value());
  CHECK(uri->rfind("data:image/svg+xml;base64,", 0) == 0);
  std::string redacted = RedactQrPairingSession("adbview-1234abcd");
  CHECK(redacted.find("654321") == std::string::npos);

  CHECK(MotionDurationMs(MotionRecipeKind::Instant, false) == 0);
  CHECK(MotionDurationMs(MotionRecipeKind::FastFade, false) == 83);
  CHECK(MotionDurationMs(MotionRecipeKind::ShortSpatial, false) == 167);
  CHECK(MotionDurationMs(MotionRecipeKind::ShortSpatial, true) == 0);
  CHECK(EvaluateShortSpatialEasing(0.0) == 0.0);
  CHECK(EvaluateShortSpatialEasing(1.0) == 1.0);
  CHECK(EvaluateShortSpatialEasing(0.5) > 0.5);

  auto pixmaps = RenderStandardTrayPixmaps(TrayStatusKind::Connected);
  CHECK(pixmaps.size() == 4u);
  CHECK(pixmaps[0].width == 16 && pixmaps[0].argb_network_bytes.size() == 16u * 16u * 4u);
  CHECK(pixmaps[3].width == 32 && pixmaps[3].argb_network_bytes.size() == 32u * 32u * 4u);
}

}  // namespace

int main() {
  TestVocabularyAndDisplayNames();
  TestAdbWireAndTrackDevicesL();
  TestProtobufLite();
  TestMdnsWireAndCache();
  TestStateReducerAndLinuxUsbAccessDenied();
  TestDoubleTransportIdentityCollapse();
  TestNotificationPolicyAndBurstCollapse();
  TestQrMotionAndTrayPixmap();

  if (g_failures != 0) {
    std::fprintf(stderr, "%d test failure(s)\n", g_failures);
    return 1;
  }
  std::printf("OK all core unit tests passed\n");
  return 0;
}
