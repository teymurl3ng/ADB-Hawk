#include "core/mdns_wire.hpp"

#include <cctype>
#include <string>
#include <unordered_set>

namespace adbhawk::core {

namespace {

constexpr size_t kHeaderLength = 12;
constexpr int kMaxPointerJumps = 32;
constexpr size_t kMaxDecodedNameBytes = 255;

uint16_t ReadU16Be(std::span<const uint8_t> s, size_t offset) {
  return static_cast<uint16_t>(
      (static_cast<uint16_t>(s[offset]) << 8) |
      static_cast<uint16_t>(s[offset + 1]));
}

uint32_t ReadU32Be(std::span<const uint8_t> s, size_t offset) {
  return (static_cast<uint32_t>(s[offset]) << 24) |
         (static_cast<uint32_t>(s[offset + 1]) << 16) |
         (static_cast<uint32_t>(s[offset + 2]) << 8) |
         static_cast<uint32_t>(s[offset + 3]);
}

void WriteU16Be(std::vector<uint8_t>* buf, uint16_t val) {
  buf->push_back(static_cast<uint8_t>((val >> 8) & 0xFFu));
  buf->push_back(static_cast<uint8_t>(val & 0xFFu));
}

std::string LowerAscii(std::string_view s) {
  std::string out;
  out.reserve(s.size());
  for (char c : s) {
    out.push_back(
        static_cast<char>(std::tolower(static_cast<unsigned char>(c))));
  }
  return out;
}

std::string FormatIpv4(std::span<const uint8_t> b) {
  return std::to_string(b[0]) + "." + std::to_string(b[1]) + "." +
         std::to_string(b[2]) + "." + std::to_string(b[3]);
}

std::string FormatIpv6(std::span<const uint8_t> b) {
  // Canonical RFC 5952 compressed lowercase IPv6 representation.
  uint16_t words[8];
  for (size_t i = 0; i < 8; ++i) {
    words[i] = ReadU16Be(b, i * 2);
  }
  int best_start = -1;
  int best_len = 0;
  int cur_start = -1;
  int cur_len = 0;
  for (int i = 0; i < 8; ++i) {
    if (words[i] == 0) {
      if (cur_start < 0) {
        cur_start = i;
        cur_len = 1;
      } else {
        ++cur_len;
      }
      if (cur_len > best_len && cur_len >= 2) {
        best_start = cur_start;
        best_len = cur_len;
      }
    } else {
      cur_start = -1;
      cur_len = 0;
    }
  }

  constexpr char kHex[] = "0123456789abcdef";
  auto append_hex16 = [&](std::string* out, uint16_t w) {
    if (w == 0) {
      out->push_back('0');
      return;
    }
    bool started = false;
    for (int shift = 12; shift >= 0; shift -= 4) {
      uint8_t nib = static_cast<uint8_t>((w >> shift) & 0xFu);
      if (nib != 0 || started) {
        out->push_back(kHex[nib]);
        started = true;
      }
    }
  };

  std::string out;
  for (int i = 0; i < 8; ++i) {
    if (best_start >= 0 && i >= best_start && i < best_start + best_len) {
      if (i == best_start) {
        out.append("::");
      }
      continue;
    }
    if (!out.empty() && out.back() != ':') {
      out.push_back(':');
    }
    append_hex16(&out, words[i]);
  }
  return out;
}

bool TryDecodeDnsName(std::span<const uint8_t> message,
                      size_t offset,
                      std::string* out_name,
                      size_t* out_next_offset) {
  std::string builder;
  builder.reserve(96);
  size_t cursor = offset;
  size_t next_offset = offset;
  int jumps = 0;
  bool followed_pointer = false;

  while (true) {
    if (cursor >= message.size()) {
      return false;
    }
    uint8_t len_byte = message[cursor];
    if (len_byte == 0) {
      cursor += 1;
      if (!followed_pointer) {
        next_offset = cursor;
      }
      break;
    }

    uint8_t top = static_cast<uint8_t>(len_byte & 0xC0u);
    if (top == 0xC0u) {
      if (cursor + 1 >= message.size()) {
        return false;
      }
      size_t pointer =
          ((static_cast<size_t>(len_byte & 0x3Fu) << 8) |
           static_cast<size_t>(message[cursor + 1]));
      if (++jumps > kMaxPointerJumps) {
        return false;
      }
      if (!followed_pointer) {
        next_offset = cursor + 2;
        followed_pointer = true;
      }
      cursor = pointer;
    } else if (top == 0x00u) {
      size_t label_len = static_cast<size_t>(len_byte & 0x3Fu);
      if (label_len == 0 || cursor + 1 + label_len > message.size()) {
        return false;
      }
      if (!builder.empty()) {
        builder.push_back('.');
      }
      if (builder.size() + label_len > kMaxDecodedNameBytes) {
        return false;
      }
      builder.append(
          reinterpret_cast<const char*>(message.data() + cursor + 1),
          label_len);
      cursor += 1 + label_len;
    } else {
      // Extended label types 0x40 / 0x80 are unsupported.
      return false;
    }
  }

  if (out_name != nullptr) {
    *out_name = std::move(builder);
  }
  *out_next_offset = next_offset;
  return true;
}

std::optional<DnsRecord> ParseRecordRdata(std::string name,
                                          uint16_t type,
                                          uint32_t ttl,
                                          bool cache_flush,
                                          std::span<const uint8_t> message,
                                          size_t data_offset,
                                          size_t data_length) {
  std::span<const uint8_t> rdata = message.subspan(data_offset, data_length);
  DnsRecord rec;
  rec.name = std::move(name);
  rec.type = type;
  rec.ttl = ttl;
  rec.cache_flush = cache_flush;

  switch (type) {
    case kDnsTypePtr: {
      std::string target;
      size_t ptr_end = 0;
      if (!TryDecodeDnsName(message, data_offset, &target, &ptr_end) ||
          ptr_end > data_offset + data_length) {
        return std::nullopt;
      }
      rec.ptr_target = std::move(target);
      return rec;
    }
    case kDnsTypeSrv: {
      if (rdata.size() < 7) {
        return std::nullopt;
      }
      std::string srv_target;
      size_t srv_end = 0;
      if (!TryDecodeDnsName(message, data_offset + 6, &srv_target, &srv_end) ||
          srv_end > data_offset + data_length) {
        return std::nullopt;
      }
      rec.srv_port = ReadU16Be(rdata, 4);
      rec.srv_target = std::move(srv_target);
      return rec;
    }
    case kDnsTypeTxt: {
      std::unordered_set<std::string> seen_keys;
      size_t cursor = 0;
      while (cursor < rdata.size()) {
        size_t len = rdata[cursor];
        cursor += 1;
        if (cursor + len > rdata.size()) {
          break;
        }
        std::string_view entry(
            reinterpret_cast<const char*>(rdata.data() + cursor), len);
        cursor += len;
        size_t eq = entry.find('=');
        if (eq != std::string_view::npos && eq > 0) {
          std::string key(entry.substr(0, eq));
          if (seen_keys.insert(LowerAscii(key)).second) {
            rec.txt_pairs.emplace_back(std::move(key),
                                       std::string(entry.substr(eq + 1)));
          }
        }
      }
      return rec;
    }
    case kDnsTypeA: {
      if (rdata.size() != 4) {
        return std::nullopt;
      }
      rec.ipv4_address = FormatIpv4(rdata);
      return rec;
    }
    case kDnsTypeAaaa: {
      if (rdata.size() != 16) {
        return std::nullopt;
      }
      rec.ipv6_address = FormatIpv6(rdata);
      return rec;
    }
    default:
      return std::nullopt;
  }
}

}  // namespace

std::vector<uint8_t> BuildMdnsQuery(uint16_t message_id,
                                    std::string_view service_domain,
                                    uint16_t record_type) {
  while (!service_domain.empty() && service_domain.front() == '.') {
    service_domain.remove_prefix(1);
  }
  while (!service_domain.empty() && service_domain.back() == '.') {
    service_domain.remove_suffix(1);
  }
  if (service_domain.empty()) {
    return {};
  }

  std::vector<std::string_view> labels;
  size_t pos = 0;
  while (pos < service_domain.size()) {
    size_t dot = service_domain.find('.', pos);
    std::string_view label =
        (dot == std::string_view::npos)
            ? service_domain.substr(pos)
            : service_domain.substr(pos, dot - pos);
    if (label.empty() || label.size() > 63) {
      return {};
    }
    labels.push_back(label);
    if (dot == std::string_view::npos) {
      break;
    }
    pos = dot + 1;
  }
  if (labels.empty()) {
    return {};
  }

  std::vector<uint8_t> buf;
  buf.reserve(kHeaderLength + service_domain.size() + 8);
  WriteU16Be(&buf, message_id);
  WriteU16Be(&buf, 0x0000u);  // standard query, QM (no QU bit)
  WriteU16Be(&buf, 0x0001u);  // QDCOUNT = 1
  WriteU16Be(&buf, 0x0000u);  // ANCOUNT = 0
  WriteU16Be(&buf, 0x0000u);  // NSCOUNT = 0
  WriteU16Be(&buf, 0x0000u);  // ARCOUNT = 0

  for (std::string_view label : labels) {
    buf.push_back(static_cast<uint8_t>(label.size()));
    buf.insert(buf.end(), label.begin(), label.end());
  }
  buf.push_back(0x00u);  // root label
  WriteU16Be(&buf, record_type);
  WriteU16Be(&buf, kDnsClassInet);
  return buf;
}

bool TryParseMdnsRecords(std::span<const uint8_t> message,
                         std::vector<DnsRecord>* out_records) {
  if (out_records == nullptr || message.size() < kHeaderLength) {
    return false;
  }
  uint16_t flags = ReadU16Be(message, 2);
  if ((flags & 0x8000u) == 0) {
    return false;  // QR bit must be 1 (response)
  }

  uint16_t qdcount = ReadU16Be(message, 4);
  uint16_t ancount = ReadU16Be(message, 6);
  uint16_t nscount = ReadU16Be(message, 8);
  uint16_t arcount = ReadU16Be(message, 10);

  size_t offset = kHeaderLength;
  for (uint16_t i = 0; i < qdcount; ++i) {
    if (!TryDecodeDnsName(message, offset, nullptr, &offset) ||
        offset + 4 > message.size()) {
      return false;
    }
    offset += 4;
  }

  uint32_t total = static_cast<uint32_t>(ancount) +
                   static_cast<uint32_t>(nscount) +
                   static_cast<uint32_t>(arcount);
  for (uint32_t i = 0; i < total; ++i) {
    std::string name;
    if (!TryDecodeDnsName(message, offset, &name, &offset) ||
        offset + 10 > message.size()) {
      return false;
    }
    uint16_t type = ReadU16Be(message, offset);
    uint16_t rr_class = ReadU16Be(message, offset + 2);
    uint32_t ttl = ReadU32Be(message, offset + 4);
    uint16_t rdlen = ReadU16Be(message, offset + 8);
    offset += 10;

    if (offset + rdlen > message.size()) {
      return false;
    }
    if ((rr_class & 0x7FFFu) == kDnsClassInet) {
      bool cache_flush = (rr_class & 0x8000u) != 0;
      auto rec = ParseRecordRdata(std::move(name), type, ttl, cache_flush,
                                  message, offset, rdlen);
      if (rec.has_value()) {
        out_records->push_back(std::move(*rec));
      }
    }
    offset += rdlen;
  }
  return true;
}

bool TryCollectMdnsPtrTargets(std::span<const uint8_t> message,
                              std::vector<std::string>* out_targets) {
  if (out_targets == nullptr) {
    return false;
  }
  std::vector<DnsRecord> records;
  if (!TryParseMdnsRecords(message, &records)) {
    return false;
  }
  std::unordered_set<std::string> seen(out_targets->begin(), out_targets->end());
  for (const auto& r : records) {
    if (r.type == kDnsTypePtr && r.ptr_target.has_value() &&
        !r.ptr_target->empty()) {
      if (seen.insert(*r.ptr_target).second) {
        out_targets->push_back(*r.ptr_target);
      }
    }
  }
  return true;
}

}  // namespace adbhawk::core
