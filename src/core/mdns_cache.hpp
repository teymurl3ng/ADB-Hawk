#pragma once

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

#include "core/mdns_wire.hpp"
#include "core/vocabulary.hpp"

namespace adbhawk::core {

class MdnsRecordCache {
 public:
  static constexpr size_t kRecordLimit = 4096;
  static constexpr int64_t kRequeryFloorMs = 60'000;
  static constexpr int64_t kLoopSliceMs = 1'000;

  void Clear();

  // Absorbs a batch of parsed DNS records at `now_ms`.
  // Returns true if the correlated `Services()` set changed.
  bool Absorb(std::span<const DnsRecord> records, int64_t now_ms);

  // Evicts expired records at `now_ms`. Returns true if `Services()` changed.
  bool Sweep(int64_t now_ms);

  // Returns due follow-up questions `(name, qtype)` for incomplete instances.
  std::vector<std::pair<std::string, uint16_t>> TakeDueQuestions(int64_t now_ms);

  // Returns delay in milliseconds until the next refresh or question is due.
  int64_t RefreshDelayMs(int64_t now_ms) const;

  // Advances refresh schedule for records whose refresh time has passed.
  void MarkRefreshSent(int64_t now_ms);

  // Returns the correlated active ADB mDNS services (`_adb-tls-connect._tcp`,
  // `_adb-tls-pairing._tcp`, `_adb._tcp`).
  const std::vector<RawMdnsService>& Services() const { return services_; }

  // Helper: splits `<instance>._adb-tls-connect._tcp.local` into instance and service type.
  static bool TrySplitInstanceDomain(std::string_view full_name,
                                     std::string* out_instance,
                                     std::string* out_service_type);

 private:
  struct CachedRecord {
    DnsRecord record;
    int64_t received_at_ms = 0;
    int64_t expires_at_ms = 0;
    int64_t next_refresh_at_ms = 0;
    int refresh_attempt = 0;
  };

  struct PendingQuestion {
    std::string name;
    uint16_t type = 0;
    int attempts = 0;
    int64_t next_at_ms = 0;
  };

  bool Rebuild(int64_t now_ms);

  std::unordered_map<std::string, CachedRecord> records_;
  std::unordered_map<std::string, PendingQuestion> questions_;
  std::vector<RawMdnsService> services_;
};

}  // namespace adbhawk::core
