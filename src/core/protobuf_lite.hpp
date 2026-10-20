#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <vector>

#include "core/adb_wire.hpp"
#include "core/vocabulary.hpp"

namespace adbhawk::core {

enum class AdbMdnsBackend {
  Unknown = 0,
  Bonjour = 1,
  Openscreen = 2,
  Libadbmdns = 3,
};

struct AdbServerStatus {
  std::string version;
  bool mdns_enabled = false;
  AdbMdnsBackend mdns_backend = AdbMdnsBackend::Unknown;
  std::optional<std::string> log_path;
  std::optional<std::string> executable_path;

  bool operator==(const AdbServerStatus&) const = default;
};

// Parses `host:server-status` binary protobuf payload.
bool TryParseAdbServerStatusProto(std::span<const uint8_t> bytes,
                                  AdbServerStatus* out_status);

// Parses `host:track-devices-proto-binary` binary protobuf payload.
bool TryParseDevicesProto(std::span<const uint8_t> bytes,
                          std::vector<RawDevice>* out_devices);

// Parses `host:track-mdns-services` binary protobuf payload.
bool TryParseMdnsServicesProto(std::span<const uint8_t> bytes,
                               std::vector<RawMdnsService>* out_services);

}  // namespace adbhawk::core
