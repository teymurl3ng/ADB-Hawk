#include <cstdint>
#include <cstdio>
#include <random>
#include <span>
#include <string_view>
#include <vector>

#include "core/adb_wire.hpp"
#include "core/mdns_cache.hpp"
#include "core/mdns_wire.hpp"
#include "core/protobuf_lite.hpp"

int main() {
  using namespace adbhawk::core;

  std::mt19937_64 rng(0xADB4A0C2026ULL);
  MdnsRecordCache cache;

  // Seed inputs with realistic frames + adversarial edge cases, then mutate across 15,000 rounds.
  const std::vector<std::vector<uint8_t>> seeds = {
      {},
      {'0', '0', '2', '9'},
      {'R', '5', 'C', 'X', ' ', 'n', 'o', ' ', 'p', 'e', 'r', 'm', 'i', 's',
       's', 'i', 'o', 'n', 's', ' ', '(', 'u', 'd', 'e', 'v', ')', ' ', 'u',
       's', 'b', ':', '1', '-', '2', '\n'},
      {0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
       0xC0, 0x0C, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x02,
       0xC0, 0x0E},
      {0x0A, 0x0C, 0x0A, 0x04, 'S', 'E', 'R', '1', 0x10, 0x08, 0x38, 0x01},
      {0x18, 0x02, 0x2A, 0x06, '3', '5', '.', '0', '.', '2', 0x60, 0x01},
  };

  std::vector<uint8_t> buf;
  buf.reserve(4096);

  for (int iter = 0; iter < 15000; ++iter) {
    const auto& base = seeds[static_cast<size_t>(iter) % seeds.size()];
    buf = base;
    size_t target_len = static_cast<size_t>(rng() % 512);
    buf.resize(target_len);
    for (size_t i = 0; i < target_len; ++i) {
      if ((rng() & 3u) == 0) {
        buf[i] = static_cast<uint8_t>(rng() & 0xFFu);
      }
    }
    // Inject DNS compression pointer bytes 0xC0 frequently to stress anti-loop bounds.
    if (target_len >= 16 && (iter % 3 == 0)) {
      buf[2] = 0x84;  // QR=1
      buf[6] = 0x00;
      buf[7] = static_cast<uint8_t>(1 + (rng() % 4));
      size_t pos = 12 + static_cast<size_t>(rng() % (target_len - 12));
      buf[pos] = static_cast<uint8_t>(0xC0u | (rng() & 0x3Fu));
    }

    std::string_view sv(reinterpret_cast<const char*>(buf.data()), buf.size());
    std::span<const uint8_t> sp(buf.data(), buf.size());

    size_t hex_len = 0;
    if (sv.size() >= 4) {
      (void)TryParseHex4Length(sv.substr(0, 4), &hex_len);
      (void)ParseAdbStatusHeader(sv.substr(0, 4));
    }

    std::vector<RawDevice> devs;
    (void)TryParseTrackDevicesL(sv, &devs);
    (void)ParseMdnsServicesList(sv);
    (void)TryParseBatteryPercent(sv);
    (void)TryParseGetpropValue(sv);
    (void)TryParseAdbServerVersionHex(sv);
    (void)TryParseAdbClientVersionOutput(sv);

    AdbServerStatus status;
    (void)TryParseAdbServerStatusProto(sp, &status);
    (void)TryParseDevicesProto(sp, &devs);
    std::vector<RawMdnsService> mdns_svcs;
    (void)TryParseMdnsServicesProto(sp, &mdns_svcs);

    std::vector<DnsRecord> dns_recs;
    if (TryParseMdnsRecords(sp, &dns_recs)) {
      (void)cache.Absorb(dns_recs, static_cast<int64_t>(iter) * 100);
    }
    if (iter % 500 == 0) {
      (void)cache.Sweep(static_cast<int64_t>(iter) * 100);
    }
  }

  std::printf("OK 15000 fuzz iterations completed under ASan+UBSan\n");
  return 0;
}
