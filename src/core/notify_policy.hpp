#pragma once

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "core/snapshot.hpp"

namespace adbhawk::core {

enum class NotificationKind {
  DeviceConnected,
  DeviceDisconnected,
  AuthorizationRequired,
  UsbAccessDenied,
  DeviceProblem,
  PairingAvailable,
  PairingResult,
  AdbUnavailable,
  DiscoveryBroken,
  DevicesChanged,
};

enum class NotificationSeverity {
  Info,
  Warning,
  Error,
};

struct NotificationKey {
  NotificationKind kind = NotificationKind::DeviceConnected;
  std::string subject;

  std::string Composite() const {
    return std::to_string(static_cast<int>(kind)) + ":" + subject;
  }

  bool operator==(const NotificationKey&) const = default;
};

struct PendingHold {
  NotificationKey key;
  int64_t due_at_ms = 0;
  int64_t armed_at_ms = 0;
  std::optional<std::string> display_name_hint;

  bool operator==(const PendingHold&) const = default;
};

struct NotificationIntent {
  NotificationKey key;
  std::string title;
  std::string body;
  NotificationSeverity severity = NotificationSeverity::Info;

  bool operator==(const NotificationIntent&) const = default;
};

struct NotifierState {
  bool baseline_established = false;
  bool shutting_down = false;
  bool suspended = false;
  int64_t suppress_until_ms = 0;
  bool adb_unavailable_outage_notified = false;
  bool discovery_broken_notified = false;
  std::unordered_map<std::string, int64_t> last_emitted_at_ms;
  std::unordered_map<std::string, int64_t> last_pairing_offer_emitted_at_ms;
  std::vector<int64_t> recent_burst_emits_ms;
};

struct NotificationDecision {
  std::vector<PendingHold> arm_holds;
  std::vector<NotificationKey> cancel_holds;
  std::vector<NotificationIntent> immediate_intents;
  NotifierState next_state;
};

class NotificationPolicy {
 public:
  static constexpr int64_t kHoldConnectedMs = 500;
  static constexpr int64_t kHoldDisconnectedMs = 500;
  static constexpr int64_t kHoldAuthorizationMs = 1'500;
  static constexpr int64_t kHoldUsbAccessDeniedMs = 1'500;
  static constexpr int64_t kHoldDeviceProblemMs = 500;
  static constexpr int64_t kHoldPairingAvailableMs = 1'500;
  static constexpr int64_t kHoldAdbUnavailableMs = 10'000;
  static constexpr int64_t kHoldDiscoveryBrokenMs = 1'500;

  static constexpr int64_t kDedupCooldownMs = 60'000;
  static constexpr int64_t kPairingOfferCooldownMs = 600'000;
  static constexpr int64_t kBurstWindowMs = 10'000;
  static constexpr int64_t kStartupSuppressMs = 5'000;
  static constexpr int64_t kResumeSuppressMs = 8'000;
  static constexpr size_t kBurstIndividualCap = 3;
  static constexpr std::string_view kGlobalSubject = "global";

  static NotifierState MakeInitialState(int64_t started_at_ms) {
    NotifierState st;
    st.suppress_until_ms = started_at_ms + kStartupSuppressMs;
    return st;
  }

  static NotificationDecision Evaluate(
      const std::optional<AppSnapshot>& previous,
      const AppSnapshot& next,
      const NotifierState& state,
      int64_t now_ms);

  static std::optional<NotificationIntent> TryMaterializeHold(
      const PendingHold& hold,
      const AppSnapshot& current,
      const NotifierState& state,
      int64_t now_ms);

  static std::pair<std::vector<NotificationIntent>, NotifierState>
  ApplyBurstAndRecord(std::span<const NotificationIntent> candidates,
                      const NotifierState& state,
                      int64_t now_ms);

  static NotifierState ForResume(const NotifierState& state, int64_t now_ms);
  static NotifierState ForSuspend(const NotifierState& state);
  static NotifierState ForShutdown(const NotifierState& state);
};

}  // namespace adbhawk::core
