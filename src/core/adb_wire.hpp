#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/vocabulary.hpp"

namespace adbhawk::core {

inline constexpr size_t kMaxAdbFrameBytes = 1024 * 1024;  // 1 MiB cap (R-09)

enum class AdbWireStatus {
  Okay,
  Fail,
  Malformed,
};

struct RawDevice {
  std::string serial;
  std::optional<int> proto_state;
  std::optional<std::string> state_token;
  // 1 = USB, 2 = Socket (Wi-Fi), std::nullopt = unknown
  std::optional<int> connection_type;
  std::optional<std::string> product;
  std::optional<std::string> model;
  std::optional<std::string> device;
  std::optional<std::string> usb_devpath;
  bool is_proto_binary = false;

  bool operator==(const RawDevice&) const = default;
};

// Encodes an ADB client request with a 4-hex length prefix ("000chost:version").
// Returns std::nullopt if payload exceeds 0xFFFF bytes.
std::optional<std::string> EncodeAdbFrame(std::string_view payload);

// Parses a 4-byte ASCII hex length prefix. Rejects non-hex characters or lengths > kMaxAdbFrameBytes.
bool TryParseHex4Length(std::string_view header4, size_t* out_length);

// Parses the 4-byte status header ("OKAY" or "FAIL").
AdbWireStatus ParseAdbStatusHeader(std::string_view header4);

// Parses `host:track-devices-l` text payload into RawDevice entries.
// Handles Linux "(no serial number)" and multi-word "no permissions (...); see [...]" states.
bool TryParseTrackDevicesL(std::string_view payload,
                           std::vector<RawDevice>* out_devices);

// Parses one-shot `host:mdns:services` text listing (`instance\tservice_type\thost:port\n`).
std::vector<RawMdnsService> ParseMdnsServicesList(std::string_view payload);

// Splits `host:port` or `[ipv6]:port` without misparsing unbracketed IPv6 addresses.
bool TrySplitHostPort(std::string_view endpoint,
                      std::string* out_host,
                      int* out_port);

// Strict metadata shell output parsers (`dumpsys battery` and `getprop`).
std::optional<int> TryParseBatteryPercent(std::string_view dumpsys_utf8);
std::optional<std::string> TryParseGetpropValue(std::string_view raw_utf8);

// Parses protocol version from `host:version` hex payload (e.g. "0029" -> 41)
// or `adb version` CLI output ("Android Debug Bridge version 1.0.41" -> 41).
std::optional<int> TryParseAdbServerVersionHex(std::string_view hex_payload);
std::optional<int> TryParseAdbClientVersionOutput(std::string_view cli_output);

}  // namespace adbhawk::core
