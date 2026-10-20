#include "core/mdns_cache.hpp"

#include <algorithm>
#include <cctype>
#include <string>
#include <unordered_set>

namespace adbhawk::core {

namespace {

std::string CanonicalName(std::string_view s) {
  while (!s.empty() && s.back() == '.') {
    s.remove_suffix(1);
  }
  std::string out;
  out.reserve(s.size());
  for (char c : s) {
    out.push_back(
        static_cast<char>(std::toupper(static_cast<unsigned char>(c))));
  }
  return out;
}

bool SameDomain(std::string_view a, std::string_view b) {
  return CanonicalName(a) == CanonicalName(b);
}

bool EndsWithIgnoreCase(std::string_view s, std::string_view suffix) {
  if (s.size() < suffix.size()) {
    return false;
  }
  for (size_t i = 0; i < suffix.size(); ++i) {
    char ca = s[s.size() - suffix.size() + i];
    char cb = suffix[i];
    if (std::tolower(static_cast<unsigned char>(ca)) !=
        std::tolower(static_cast<unsigned char>(cb))) {
      return false;
    }
  }
  return true;
}

MdnsServiceKind ClassifyService(std::string_view service_type) {
  if (service_type == "_adb-tls-connect._tcp") {
    return MdnsServiceKind::Connect;
  }
  if (service_type == "_adb-tls-pairing._tcp") {
    return MdnsServiceKind::Pairing;
  }
  if (service_type == "_adb._tcp") {
    return MdnsServiceKind::LegacyTcp;
  }
  return MdnsServiceKind::Unknown;
}

bool IsTrackedInstance(std::string_view name) {
  std::string inst;
  std::string type;
  return MdnsRecordCache::TrySplitInstanceDomain(name, &inst, &type);
}

std::string RecordKey(const DnsRecord& r) {
  std::string key = CanonicalName(r.name);
  key.push_back('\x1f');
  key.append(std::to_string(r.type));
  key.push_back('\x1f');
  if (r.type == kDnsTypePtr && r.ptr_target.has_value()) {
    key.append(CanonicalName(*r.ptr_target));
  } else if (r.ipv4_address.has_value()) {
    key.append(*r.ipv4_address);
  } else if (r.ipv6_address.has_value()) {
    key.append(*r.ipv6_address);
  }
  return key;
}

std::string QuestionKey(std::string_view canonical_name, uint16_t type) {
  return std::string(canonical_name) + "\x1f" + std::to_string(type);
}

std::optional<std::string> ExtractSerialFromAospInstance(
    std::string_view instance) {
  if (instance.size() <= 4 || instance.substr(0, 4) != "adb-") {
    return std::nullopt;
  }
  std::string_view rest = instance.substr(4);
  if (rest.size() > 7 && rest[rest.size() - 7] == '-') {
    bool alnum = true;
    for (size_t i = rest.size() - 6; i < rest.size(); ++i) {
      if (std::isalnum(static_cast<unsigned char>(rest[i])) == 0) {
        alnum = false;
        break;
      }
    }
    if (alnum) {
      return std::string(rest.substr(0, rest.size() - 7));
    }
  }
  for (char c : rest) {
    if (std::isalnum(static_cast<unsigned char>(c)) == 0) {
      return std::nullopt;
    }
  }
  return std::string(rest);
}

}  // namespace

bool MdnsRecordCache::TrySplitInstanceDomain(std::string_view full_name,
                                             std::string* out_instance,
                                             std::string* out_service_type) {
  while (!full_name.empty() && full_name.back() == '.') {
    full_name.remove_suffix(1);
  }
  constexpr std::string_view kLocalSuffix = ".local";
  if (EndsWithIgnoreCase(full_name, kLocalSuffix)) {
    full_name.remove_suffix(kLocalSuffix.size());
  }

  constexpr std::string_view kKnownTypes[] = {
      "._adb-tls-connect._tcp",
      "._adb-tls-pairing._tcp",
      "._adb._tcp",
  };
  for (std::string_view suffix : kKnownTypes) {
    if (EndsWithIgnoreCase(full_name, suffix)) {
      std::string_view inst = full_name.substr(0, full_name.size() - suffix.size());
      if (inst.empty()) {
        return false;
      }
      if (out_instance != nullptr) {
        *out_instance = std::string(inst);
      }
      if (out_service_type != nullptr) {
        *out_service_type = std::string(suffix.substr(1));
      }
      return true;
    }
  }
  return false;
}

void MdnsRecordCache::Clear() {
  records_.clear();
  questions_.clear();
  services_.clear();
}

bool MdnsRecordCache::Absorb(std::span<const DnsRecord> records,
                             int64_t now_ms) {
  // RFC 6762 cache-flush grace for A/AAAA records: give older records on the same host 1s grace.
  std::unordered_set<std::string> incoming_keys;
  for (const auto& r : records) {
    incoming_keys.insert(RecordKey(r));
  }
  for (const auto& incoming : records) {
    if (!incoming.cache_flush ||
        (incoming.type != kDnsTypeA && incoming.type != kDnsTypeAaaa)) {
      continue;
    }
    for (auto& [k, cached] : records_) {
      if (cached.record.type == incoming.type &&
          SameDomain(cached.record.name, incoming.name) &&
          cached.received_at_ms < now_ms - 1000 &&
          incoming_keys.find(k) == incoming_keys.end()) {
        cached.expires_at_ms = now_ms + 1000;
      }
    }
  }

  for (const auto& r : records) {
    bool is_service_rec =
        (r.type == kDnsTypePtr && r.ptr_target.has_value() &&
         IsTrackedInstance(*r.ptr_target)) ||
        ((r.type == kDnsTypeSrv || r.type == kDnsTypeTxt) &&
         IsTrackedInstance(r.name));
    std::string_view trimmed_name = r.name;
    while (!trimmed_name.empty() && trimmed_name.back() == '.') {
      trimmed_name.remove_suffix(1);
    }
    bool is_addr_rec =
        (r.type == kDnsTypeA || r.type == kDnsTypeAaaa) &&
        (r.ipv4_address.has_value() || r.ipv6_address.has_value()) &&
        EndsWithIgnoreCase(trimmed_name, ".local");
    if (!is_service_rec && !is_addr_rec) {
      continue;
    }

    std::string key = RecordKey(r);
    if (r.ttl == 0) {
      records_.erase(key);
      if (r.type == kDnsTypePtr || r.type == kDnsTypeSrv) {
        std::string withdrawn =
            CanonicalName(r.ptr_target.has_value() ? *r.ptr_target : r.name);
        for (auto it = records_.begin(); it != records_.end();) {
          if (CanonicalName(it->second.record.name) == withdrawn ||
              (it->second.record.ptr_target.has_value() &&
               CanonicalName(*it->second.record.ptr_target) == withdrawn)) {
            it = records_.erase(it);
          } else {
            ++it;
          }
        }
      }
      continue;
    }

    if (records_.size() >= kRecordLimit &&
        records_.find(key) == records_.end()) {
      continue;
    }

    int64_t capped_ttl_ms =
        static_cast<int64_t>(std::min<uint32_t>(r.ttl, 4500u)) * 1000;
    CachedRecord entry;
    entry.record = r;
    entry.received_at_ms = now_ms;
    entry.expires_at_ms = now_ms + capped_ttl_ms;
    entry.next_refresh_at_ms = now_ms + (capped_ttl_ms * 80) / 100;
    entry.refresh_attempt = 0;
    records_[key] = std::move(entry);
  }

  return Rebuild(now_ms);
}

bool MdnsRecordCache::Sweep(int64_t now_ms) {
  for (auto it = records_.begin(); it != records_.end();) {
    if (it->second.expires_at_ms <= now_ms) {
      it = records_.erase(it);
    } else {
      ++it;
    }
  }
  return Rebuild(now_ms);
}

bool MdnsRecordCache::Rebuild(int64_t now_ms) {
  std::vector<const CachedRecord*> live;
  live.reserve(records_.size());
  for (const auto& [k, rec] : records_) {
    if (rec.expires_at_ms > now_ms) {
      live.push_back(&rec);
    }
  }

  // Collect canonical instance names from PTR and SRV records.
  std::unordered_map<std::string, std::string> instance_names;
  for (const CachedRecord* item : live) {
    std::optional<std::string> candidate;
    if (item->record.type == kDnsTypePtr &&
        item->record.ptr_target.has_value()) {
      candidate = *item->record.ptr_target;
    } else if (item->record.type == kDnsTypeSrv) {
      candidate = item->record.name;
    }
    if (candidate.has_value() && IsTrackedInstance(*candidate)) {
      instance_names.try_emplace(CanonicalName(*candidate), *candidate);
    }
  }

  std::vector<RawMdnsService> next_services;
  std::unordered_set<std::string> required_question_keys;

  for (const auto& [canon_inst, full_name] : instance_names) {
    std::string inst;
    std::string svc_type;
    if (!TrySplitInstanceDomain(full_name, &inst, &svc_type)) {
      continue;
    }

    const CachedRecord* ptr = nullptr;
    const CachedRecord* srv = nullptr;
    const CachedRecord* txt = nullptr;
    for (const CachedRecord* row : live) {
      if (row->record.type == kDnsTypePtr &&
          row->record.ptr_target.has_value() &&
          SameDomain(*row->record.ptr_target, full_name)) {
        ptr = row;
      } else if (row->record.type == kDnsTypeSrv &&
                 SameDomain(row->record.name, full_name)) {
        srv = row;
      } else if (row->record.type == kDnsTypeTxt &&
                 SameDomain(row->record.name, full_name)) {
        txt = row;
      }
    }

    std::vector<const CachedRecord*> addr_rows;
    if (srv != nullptr && srv->record.srv_target.has_value()) {
      for (const CachedRecord* row : live) {
        if ((row->record.ipv4_address.has_value() ||
             row->record.ipv6_address.has_value()) &&
            SameDomain(row->record.name, *srv->record.srv_target)) {
          addr_rows.push_back(row);
        }
      }
      std::sort(addr_rows.begin(), addr_rows.end(),
                [](const CachedRecord* a, const CachedRecord* b) {
                  return a->received_at_ms > b->received_at_ms;
                });
    }

    if (srv == nullptr) {
      std::string qk = QuestionKey(canon_inst, kDnsTypeSrv);
      required_question_keys.insert(qk);
      questions_.try_emplace(
          qk, PendingQuestion{full_name, kDnsTypeSrv, 0, now_ms + 1000});
    }
    if (txt == nullptr) {
      std::string qk = QuestionKey(canon_inst, kDnsTypeTxt);
      required_question_keys.insert(qk);
      questions_.try_emplace(
          qk, PendingQuestion{full_name, kDnsTypeTxt, 0, now_ms + 1000});
    }
    if (srv != nullptr && srv->record.srv_target.has_value() &&
        addr_rows.empty()) {
      std::string canon_host = CanonicalName(*srv->record.srv_target);
      std::string qk_a = QuestionKey(canon_host, kDnsTypeA);
      std::string qk_aaaa = QuestionKey(canon_host, kDnsTypeAaaa);
      required_question_keys.insert(qk_a);
      required_question_keys.insert(qk_aaaa);
      questions_.try_emplace(
          qk_a, PendingQuestion{*srv->record.srv_target, kDnsTypeA, 0,
                                now_ms + 1000});
      questions_.try_emplace(
          qk_aaaa, PendingQuestion{*srv->record.srv_target, kDnsTypeAaaa, 0,
                                   now_ms + 1000});
    }

    // Only emit a correlated RawMdnsService once SRV port and at least one address are known.
    if (srv == nullptr || !srv->record.srv_port.has_value() ||
        *srv->record.srv_port == 0 || addr_rows.empty()) {
      continue;
    }

    RawMdnsService svc;
    svc.instance_name = inst;
    svc.service_type = svc_type;
    svc.kind = ClassifyService(svc_type);
    svc.port = static_cast<int>(*srv->record.srv_port);
    svc.hostname = srv->record.srv_target;
    svc.serial = ExtractSerialFromAospInstance(inst);

    for (const CachedRecord* ar : addr_rows) {
      if (ar->record.ipv4_address.has_value() && !svc.ipv4.has_value()) {
        svc.ipv4 = *ar->record.ipv4_address;
      }
      if (ar->record.ipv6_address.has_value()) {
        svc.ipv6.push_back(*ar->record.ipv6_address);
      }
    }
    std::sort(svc.ipv6.begin(), svc.ipv6.end());
    svc.ipv6.erase(std::unique(svc.ipv6.begin(), svc.ipv6.end()),
                   svc.ipv6.end());

    if (txt != nullptr) {
      for (const auto& [k, v] : txt->record.txt_pairs) {
        if (k == "name" && !v.empty()) {
          svc.product_model = v;
        } else if (k == "api" && !v.empty()) {
          svc.build_version_sdk_full = v;
        } else if (k == "v" && !v.empty()) {
          svc.mdns_service_version = v;
        }
      }
    }
    (void)ptr;
    next_services.push_back(std::move(svc));
  }

  for (auto it = questions_.begin(); it != questions_.end();) {
    if (required_question_keys.find(it->first) ==
        required_question_keys.end()) {
      it = questions_.erase(it);
    } else {
      ++it;
    }
  }

  std::sort(next_services.begin(), next_services.end(),
            [](const RawMdnsService& a, const RawMdnsService& b) {
              return a.DiscoveryKey() < b.DiscoveryKey();
            });

  if (next_services == services_) {
    return false;
  }
  services_ = std::move(next_services);
  return true;
}

std::vector<std::pair<std::string, uint16_t>> MdnsRecordCache::TakeDueQuestions(
    int64_t now_ms) {
  std::vector<std::pair<std::string, uint16_t>> due;
  for (auto& [k, q] : questions_) {
    if (q.next_at_ms <= now_ms) {
      due.emplace_back(q.name, q.type);
      q.attempts = std::min(q.attempts + 1, 6);
      int64_t backoff_s = std::min(1 << q.attempts, 60);
      q.next_at_ms = now_ms + backoff_s * 1000;
    }
  }
  return due;
}

int64_t MdnsRecordCache::RefreshDelayMs(int64_t now_ms) const {
  int64_t delay = kRequeryFloorMs;
  for (const auto& [k, item] : records_) {
    int64_t rem = item.next_refresh_at_ms - now_ms;
    if (rem < delay) {
      delay = rem;
    }
  }
  for (const auto& [k, q] : questions_) {
    int64_t rem = q.next_at_ms - now_ms;
    if (rem < delay) {
      delay = rem;
    }
  }
  return std::max<int64_t>(delay, kLoopSliceMs);
}

void MdnsRecordCache::MarkRefreshSent(int64_t now_ms) {
  for (auto& [k, item] : records_) {
    if (item.next_refresh_at_ms > now_ms) {
      continue;
    }
    int attempt = std::min(item.refresh_attempt + 1, 4);
    int64_t ttl_ms = item.expires_at_ms - item.received_at_ms;
    int pct = (attempt < 4) ? (80 + attempt * 5) : 100;
    item.refresh_attempt = attempt;
    item.next_refresh_at_ms = item.received_at_ms + (ttl_ms * pct) / 100;
  }
}

}  // namespace adbhawk::core
