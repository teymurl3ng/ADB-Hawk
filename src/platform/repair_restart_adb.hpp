#pragma once

#include <cstdint>
#include <optional>
#include <string>

#include "core/snapshot.hpp"

namespace adbhawk::platform {

// User-initiated non-root `Restart ADB` repair action (D9, R-10).
// Stops the local ADB server on `port` (after verifying listener UID ownership)
// and spawns `<adb_path> start-server` with an argv array and CLOEXEC file descriptors.
core::DiscoveryRepairOutcome ExecuteUserInitiatedRestartAdb(
    uint16_t port,
    const std::optional<std::string>& adb_path);

}  // namespace adbhawk::platform
