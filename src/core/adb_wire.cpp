#include "core/adb_wire.hpp"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <string>
#include <unordered_set>

namespace adbhawk::core {

namespace {

std::string_view TrimSv(std::string_view sv) {
  while (!sv.empty() &&
         std::isspace(static_cast<unsigned char>(sv.front())) != 0) {
    sv.remove_prefix(1);
  }
  while (!sv.empty() &&
         std::isspace(static_cast<unsigned char>(sv.back())) != 0) {
    sv.remove_suffix(1);
  }
  return sv;
}

bool StartsWith(std::string_view s, std::string_view prefix) {
  return s.size() >= prefix.size() && s.substr(0, prefix.size()) == prefix;
}

bool EqualsIgnoreCaseAscii(std::string_view a, std::string_view b) {
  if (a.size() != b.size()) {
    return false;
  }
  for (size_t i = 0; i < a.size(); ++i) {
    if (std::tolower(static_cast<unsigned char>(a[i])) !=
        std::tolower(static_cast<unsigned char>(b[i]))) {
      return false;
    }
  }
  return true;
}

bool StartsWithIgnoreCaseAscii(std::string_view s, std::string_view prefix) {
  if (s.size() < prefix.size()) {
    return false;
  }
  return EqualsIgnoreCaseAscii(s.substr(0, prefix.size()), prefix);
}

bool IsKnownAttributeToken(std::string_view token) {
  return StartsWith(token, "usb:") || StartsWith(token, "product:") ||
         StartsWith(token, "model:") || StartsWith(token, "device:") ||
         StartsWith(token, "transport_id:");
}

MdnsServiceKind ClassifyMdnsServiceType(std::string_view service_type) {
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

bool ParseTrackDevicesLine(std::string_view line, RawDevice* out) {
  line = TrimSv(line);
  if (line.empty()) {
    return false;
  }

  std::string serial;
  std::string_view rest;

  // In AOSP transport.cpp append_transport_info(), an empty USB serial is printed
  // literally as "(no serial number)" padded to 22 columns (`%-22s %s`).
  constexpr std::string_view kNoSerialLiteral = "(no serial number)";
  if (StartsWith(line, kNoSerialLiteral) &&
      (line.size() == kNoSerialLiteral.size() ||
       std::isspace(static_cast<unsigned char>(
           line[kNoSerialLiteral.size()])) != 0)) {
    serial = std::string(kNoSerialLiteral);
    rest = TrimSv(line.substr(kNoSerialLiteral.size()));
  } else {
    size_t ws = 0;
    while (ws < line.size() &&
           std::isspace(static_cast<unsigned char>(line[ws])) == 0) {
      ++ws;
    }
    if (ws == 0 || ws >= line.size()) {
      return false;
    }
    serial = std::string(line.substr(0, ws));
    rest = TrimSv(line.substr(ws));
  }

  if (rest.empty()) {
    return false;
  }

  std::string state_token;
  std::string_view attrs;

  // Linux kCsNoPerm emits:
  // "no permissions" or "no permissions (<reason>); see [http://developer.android.com/...]"
  // followed by space-separated key:value attributes (usb:..., transport_id:...).
  constexpr std::string_view kNoPermissionsPrefix = "no permissions";
  if (StartsWith(rest, kNoPermissionsPrefix) &&
      (rest.size() == kNoPermissionsPrefix.size() ||
       std::isspace(static_cast<unsigned char>(
           rest[kNoPermissionsPrefix.size()])) != 0)) {
    state_token = std::string(kNoPermissionsPrefix);
    // Advance token-by-token until we hit a known attribute key (usb:, product:, etc.).
    std::string_view cursor = TrimSv(rest.substr(kNoPermissionsPrefix.size()));
    while (!cursor.empty()) {
      size_t tok_end = 0;
      while (tok_end < cursor.size() &&
             std::isspace(static_cast<unsigned char>(cursor[tok_end])) == 0) {
        ++tok_end;
      }
      std::string_view tok = cursor.substr(0, tok_end);
      if (IsKnownAttributeToken(tok)) {
        attrs = cursor;
        break;
      }
      cursor = TrimSv(cursor.substr(tok_end));
    }
  } else {
    size_t state_end = 0;
    while (state_end < rest.size() &&
           std::isspace(static_cast<unsigned char>(rest[state_end])) == 0) {
      ++state_end;
    }
    state_token = std::string(rest.substr(0, state_end));
    attrs = TrimSv(rest.substr(state_end));
  }

  if (state_token.empty()) {
    return false;
  }

  RawDevice dev;
  dev.serial = std::move(serial);
  dev.state_token = std::move(state_token);
  dev.is_proto_binary = false;

  while (!attrs.empty()) {
    size_t tok_end = 0;
    while (tok_end < attrs.size() &&
           std::isspace(static_cast<unsigned char>(attrs[tok_end])) == 0) {
      ++tok_end;
    }
    std::string_view tok = attrs.substr(0, tok_end);
    attrs = TrimSv(attrs.substr(tok_end));

    size_t colon = tok.find(':');
    if (colon == std::string_view::npos || colon == 0 ||
        colon + 1 >= tok.size()) {
      continue;
    }
    std::string_view key = tok.substr(0, colon);
    std::string_view val = tok.substr(colon + 1);
    if (key == "product") {
      dev.product = std::string(val);
    } else if (key == "model") {
      dev.model = std::string(val);
    } else if (key == "device") {
      dev.device = std::string(val);
    } else if (key == "usb") {
      dev.usb_devpath = std::string(tok);
    }
  }

  // On Linux packaged adb (e.g. 34.0.4-debian), `host:track-devices-proto-binary` is absent,
  // so `track-devices-l` is the primary stream (D4). When `usb:<bus>` is present on the line,
  // AOSP transport.cpp populated `t->devpath` from the USB transport (`ConnectionType = 1`).
  // When the serial is a `host:port` endpoint or `_adb-tls-connect._tcp` service instance and
  // no `usb:` attribute is present, it is a socket/Wi-Fi transport (`ConnectionType = 2`).
  if (dev.usb_devpath.has_value()) {
    dev.connection_type = 1;
  } else {
    std::string host;
    int port = 0;
    if (TrySplitHostPort(dev.serial, &host, &port) ||
        dev.serial.find("_adb-tls-connect._tcp") != std::string::npos ||
        dev.serial.find("_adb._tcp") != std::string::npos) {
      dev.connection_type = 2;
    }
  }

  *out = std::move(dev);
  return true;
}

}  // namespace

std::optional<std::string> EncodeAdbFrame(std::string_view payload) {
  if (payload.size() > 0xFFFFu) {
    return std::nullopt;
  }
  constexpr char kHex[] = "0123456789abcdef";
  unsigned int len = static_cast<unsigned int>(payload.size());
  std::string out;
  out.reserve(4 + payload.size());
  out.push_back(kHex[(len >> 12) & 0xF]);
  out.push_back(kHex[(len >> 8) & 0xF]);
  out.push_back(kHex[(len >> 4) & 0xF]);
  out.push_back(kHex[len & 0xF]);
  out.append(payload);
  return out;
}

bool TryParseHex4Length(std::string_view header4, size_t* out_length) {
  if (header4.size() != 4 || out_length == nullptr) {
    return false;
  }
  size_t val = 0;
  for (char c : header4) {
    val <<= 4;
    if (c >= '0' && c <= '9') {
      val |= static_cast<size_t>(c - '0');
    } else if (c >= 'a' && c <= 'f') {
      val |= static_cast<size_t>(c - 'a' + 10);
    } else if (c >= 'A' && c <= 'F') {
      val |= static_cast<size_t>(c - 'A' + 10);
    } else {
      return false;
    }
  }
  if (val > kMaxAdbFrameBytes) {
    return false;
  }
  *out_length = val;
  return true;
}

AdbWireStatus ParseAdbStatusHeader(std::string_view header4) {
  if (header4 == "OKAY") {
    return AdbWireStatus::Okay;
  }
  if (header4 == "FAIL") {
    return AdbWireStatus::Fail;
  }
  return AdbWireStatus::Malformed;
}

bool TryParseTrackDevicesL(std::string_view payload,
                           std::vector<RawDevice>* out_devices) {
  if (out_devices == nullptr || payload.size() > kMaxAdbFrameBytes) {
    return false;
  }
  out_devices->clear();
  if (payload.empty()) {
    return true;
  }

  size_t pos = 0;
  while (pos < payload.size()) {
    size_t nl = payload.find('\n', pos);
    std::string_view line = (nl == std::string_view::npos)
                                ? payload.substr(pos)
                                : payload.substr(pos, nl - pos);
    pos = (nl == std::string_view::npos) ? payload.size() : nl + 1;

    line = TrimSv(line);
    if (line.empty()) {
      continue;
    }
    RawDevice dev;
    if (!ParseTrackDevicesLine(line, &dev)) {
      return false;
    }
    out_devices->push_back(std::move(dev));
  }
  return true;
}

bool TrySplitHostPort(std::string_view endpoint,
                      std::string* out_host,
                      int* out_port) {
  if (out_host == nullptr || out_port == nullptr) {
    return false;
  }
  endpoint = TrimSv(endpoint);
  if (endpoint.empty()) {
    return false;
  }

  std::string_view host_part;
  std::string_view port_part;

  if (endpoint.front() == '[') {
    size_t close = endpoint.find(']');
    if (close == std::string_view::npos || close <= 1 ||
        close + 2 >= endpoint.size() || endpoint[close + 1] != ':') {
      return false;
    }
    host_part = endpoint.substr(1, close - 1);
    port_part = endpoint.substr(close + 2);
  } else {
    size_t colon = endpoint.rfind(':');
    if (colon == std::string_view::npos || colon == 0 ||
        colon + 1 >= endpoint.size()) {
      return false;
    }
    // Reject bare IPv6 (more than one colon without brackets).
    if (endpoint.find(':') != colon) {
      return false;
    }
    host_part = endpoint.substr(0, colon);
    port_part = endpoint.substr(colon + 1);
  }

  if (host_part.empty() || port_part.empty()) {
    return false;
  }
  for (char c : port_part) {
    if (std::isdigit(static_cast<unsigned char>(c)) == 0) {
      return false;
    }
  }
  int port = 0;
  auto [ptr, ec] =
      std::from_chars(port_part.data(), port_part.data() + port_part.size(), port);
  if (ec != std::errc{} || ptr != port_part.data() + port_part.size() ||
      port <= 0 || port > 65535) {
    return false;
  }

  *out_host = std::string(host_part);
  *out_port = port;
  return true;
}

std::vector<RawMdnsService> ParseMdnsServicesList(std::string_view payload) {
  std::vector<RawMdnsService> out;
  if (payload.empty() || payload.size() > kMaxAdbFrameBytes) {
    return out;
  }

  std::unordered_set<std::string> seen;
  size_t pos = 0;
  while (pos < payload.size()) {
    size_t nl = payload.find('\n', pos);
    std::string_view line = (nl == std::string_view::npos)
                                ? payload.substr(pos)
                                : payload.substr(pos, nl - pos);
    pos = (nl == std::string_view::npos) ? payload.size() : nl + 1;

    line = TrimSv(line);
    if (line.empty() ||
        StartsWithIgnoreCaseAscii(line, "List of discovered")) {
      continue;
    }

    size_t t1 = line.find('\t');
    if (t1 == std::string_view::npos) {
      continue;
    }
    size_t t2 = line.find('\t', t1 + 1);
    if (t2 == std::string_view::npos) {
      continue;
    }

    std::string_view instance = TrimSv(line.substr(0, t1));
    std::string_view service_type = TrimSv(line.substr(t1 + 1, t2 - (t1 + 1)));
    std::string_view endpoint = TrimSv(line.substr(t2 + 1));
    size_t extra_tab = endpoint.find('\t');
    if (extra_tab != std::string_view::npos) {
      endpoint = TrimSv(endpoint.substr(0, extra_tab));
    }

    std::string host;
    int port = 0;
    if (!TrySplitHostPort(endpoint, &host, &port)) {
      continue;
    }
    if (instance.empty() && service_type.empty()) {
      continue;
    }

    RawMdnsService svc;
    svc.instance_name = std::string(instance);
    svc.service_type = std::string(service_type);
    svc.kind = ClassifyMdnsServiceType(svc.service_type);
    svc.port = port;
    if (host.find(':') != std::string::npos) {
      svc.ipv6.push_back(host);
    } else {
      svc.ipv4 = host;
    }

    std::string key = svc.DiscoveryKey();
    if (seen.insert(key).second) {
      out.push_back(std::move(svc));
    }
  }
  return out;
}

std::optional<int> TryParseBatteryPercent(std::string_view dumpsys_utf8) {
  size_t pos = 0;
  while (pos < dumpsys_utf8.size()) {
    size_t nl = dumpsys_utf8.find('\n', pos);
    std::string_view line = (nl == std::string_view::npos)
                                ? dumpsys_utf8.substr(pos)
                                : dumpsys_utf8.substr(pos, nl - pos);
    pos = (nl == std::string_view::npos) ? dumpsys_utf8.size() : nl + 1;

    if (!line.empty() && line.back() == '\r') {
      line.remove_suffix(1);
    }
    // Must match `^[ \t]*level:[ \t]*(-?\d+)[ \t]*$` and not `Capacity level:`
    size_t i = 0;
    while (i < line.size() && (line[i] == ' ' || line[i] == '\t')) {
      ++i;
    }
    constexpr std::string_view kPrefix = "level:";
    if (!StartsWith(line.substr(i), kPrefix)) {
      continue;
    }
    std::string_view val_part = TrimSv(line.substr(i + kPrefix.size()));
    if (val_part.empty()) {
      continue;
    }
    int level = -1;
    auto [ptr, ec] = std::from_chars(
        val_part.data(), val_part.data() + val_part.size(), level);
    if (ec != std::errc{} || ptr != val_part.data() + val_part.size()) {
      continue;
    }
    if (level < 0 || level > 100) {
      return std::nullopt;
    }
    return level;
  }
  return std::nullopt;
}

std::optional<std::string> TryParseGetpropValue(std::string_view raw_utf8) {
  std::string out;
  out.reserve(raw_utf8.size());
  for (char c : raw_utf8) {
    if (c != '\r' && c != '\n') {
      out.push_back(c);
    }
  }
  std::string_view trimmed = TrimSv(out);
  if (trimmed.empty() || EqualsIgnoreCaseAscii(trimmed, "null") ||
      EqualsIgnoreCaseAscii(trimmed, "unknown")) {
    return std::nullopt;
  }
  return std::string(trimmed);
}

std::optional<int> TryParseAdbServerVersionHex(std::string_view hex_payload) {
  hex_payload = TrimSv(hex_payload);
  size_t val = 0;
  if (!TryParseHex4Length(hex_payload, &val)) {
    return std::nullopt;
  }
  return static_cast<int>(val);
}

std::optional<int> TryParseAdbClientVersionOutput(std::string_view cli_output) {
  // Matches "Android Debug Bridge version X.Y.NN" -> NN
  constexpr std::string_view kMarker = "Android Debug Bridge version ";
  size_t pos = cli_output.find(kMarker);
  if (pos == std::string_view::npos) {
    return std::nullopt;
  }
  std::string_view rest = cli_output.substr(pos + kMarker.size());
  size_t end = 0;
  while (end < rest.size() &&
         (std::isdigit(static_cast<unsigned char>(rest[end])) != 0 ||
          rest[end] == '.')) {
    ++end;
  }
  std::string_view ver = rest.substr(0, end);
  size_t last_dot = ver.rfind('.');
  if (last_dot == std::string_view::npos || last_dot + 1 >= ver.size()) {
    return std::nullopt;
  }
  std::string_view proto_str = ver.substr(last_dot + 1);
  int proto = 0;
  auto [ptr, ec] = std::from_chars(
      proto_str.data(), proto_str.data() + proto_str.size(), proto);
  if (ec != std::errc{} || ptr != proto_str.data() + proto_str.size() ||
      proto <= 0) {
    return std::nullopt;
  }
  return proto;
}

}  // namespace adbhawk::core
