#pragma once

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace adbhawk::core {

inline constexpr uint16_t kDnsTypeA = 1;
inline constexpr uint16_t kDnsTypePtr = 12;
inline constexpr uint16_t kDnsTypeTxt = 16;
inline constexpr uint16_t kDnsTypeAaaa = 28;
inline constexpr uint16_t kDnsTypeSrv = 33;
inline constexpr uint16_t kDnsClassInet = 1;

struct DnsRecord {
  std::string name;
  uint16_t type = 0;
  uint32_t ttl = 0;
  bool cache_flush = false;
  std::optional<std::string> ptr_target;
  std::optional<std::string> srv_target;
  std::optional<uint16_t> srv_port;
  std::vector<std::pair<std::string, std::string>> txt_pairs;
  std::optional<std::string> ipv4_address;
  std::optional<std::string> ipv6_address;

  bool operator==(const DnsRecord&) const = default;
};

// Builds a standard QM DNS query packet for `service_domain` and `record_type`.
// Returns empty vector if `service_domain` has invalid label lengths.
std::vector<uint8_t> BuildMdnsQuery(uint16_t message_id,
                                    std::string_view service_domain,
                                    uint16_t record_type = kDnsTypePtr);

// Parses DNS response records (answer + authority + additional) with RFC 1035
// pointer-compression loop protection (<= 32 jumps). Returns false if malformed or not a response.
bool TryParseMdnsRecords(std::span<const uint8_t> message,
                         std::vector<DnsRecord>* out_records);

// Collects PTR target names from a DNS response (used by one-shot discovery probe).
bool TryCollectMdnsPtrTargets(std::span<const uint8_t> message,
                              std::vector<std::string>* out_targets);

}  // namespace adbhawk::core
