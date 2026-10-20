#pragma once

#include <sys/types.h>

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace adbhawk::platform {

struct AdbLocatorEnv {
  std::optional<std::string> android_home;
  std::optional<std::string> android_sdk_root;
  std::optional<std::string> path_env;
  std::optional<std::string> home_dir;
};

// Reads current process environment (`ANDROID_HOME`, `ANDROID_SDK_ROOT`, `PATH`, `HOME`).
AdbLocatorEnv ReadCurrentLocatorEnv();

// Deterministic search for the `adb` binary:
// 1. $ANDROID_HOME/platform-tools/adb
// 2. $ANDROID_SDK_ROOT/platform-tools/adb
// 3. $PATH entries (strictly absolute paths only; relative entries "", "." etc. skipped)
// 4. $HOME/Android/Sdk/platform-tools/adb
// 5. /usr/lib/android-sdk/platform-tools/adb
// 6. /opt/android-sdk/platform-tools/adb
// Requires regular file (`S_ISREG`) and executable (`faccessat(..., X_OK, AT_EACCESS)`).
std::optional<std::string> LocateAdbExecutable(const AdbLocatorEnv& env);

// Reads `ANDROID_ADB_SERVER_PORT` read-only (never modifies environment). Defaults to 5037.
// If auto_discover_with_devices is true and no explicit env/arg port is set,
// checks candidate user-owned ADB server ports and returns the one with active devices.
uint16_t ResolveAdbServerPort(
    std::optional<std::string_view> env_port = std::nullopt,
    bool auto_discover_with_devices = true);

struct AdbServerProbeResult {
  uint16_t port = 0;
  bool is_adb_server = false;
  size_t device_count = 0;
  std::vector<std::string> device_serials;

  bool operator==(const AdbServerProbeResult&) const = default;
};

// Returns candidate listening ports owned by `expected_uid` between `min_port` and `max_port`.
std::vector<uint16_t> FindListeningTcpPortsForUid(
    std::string_view proc_net_tcp,
    std::string_view proc_net_tcp6,
    uid_t expected_uid,
    uint16_t min_port = 5037,
    uint16_t max_port = 5050);

// Non-blocking query to test if `port` is an active ADB server and count attached devices.
AdbServerProbeResult ProbeAdbServerCandidate(uint16_t port,
                                             int timeout_ms = 80);

// Discovers active user ADB servers, prioritizing candidate ports that currently have devices attached.
std::vector<AdbServerProbeResult> DiscoverActiveAdbServers(uid_t expected_uid);

enum class ListenerOwnerStatus {
  OwnedByExpectedUid,
  OwnedByOtherUid,
  NotFound,
};

// Inspects `/proc/net/tcp` and `/proc/net/tcp6` (or injected content in tests)
// to verify that a TCP LISTEN socket on `port` belongs to `expected_uid` (R-10).
ListenerOwnerStatus CheckTcpListenerOwnerFromProc(
    std::string_view proc_net_tcp,
    std::string_view proc_net_tcp6,
    uint16_t port,
    uid_t expected_uid);

ListenerOwnerStatus VerifyLocalAdbServerOwner(uint16_t port,
                                              uid_t expected_uid);

// Runs `<adb_path> version` via fork/execve (argv array, CLOEXEC pipe, no shell)
// and parses the protocol version number (`1.0.NN` -> `NN`).
std::optional<int> QueryAdbClientProtocolVersion(const std::string& adb_path);

}  // namespace adbhawk::platform
