#include "core/notify_policy.hpp"

#include <algorithm>
#include <unordered_map>
#include <unordered_set>

#include "core/vocabulary.hpp"

namespace adbhawk::core {

namespace {

bool IsDedupBlocked(const NotificationKey& key,
                    const NotifierState& state,
                    int64_t now_ms) {
  auto it = state.last_emitted_at_ms.find(key.Composite());
  return it != state.last_emitted_at_ms.end() &&
         (now_ms - it->second) < NotificationPolicy::kDedupCooldownMs;
}

bool IsPairingOfferCooldownBlocked(const std::string& offer_id,
                                   const NotifierState& state,
                                   int64_t now_ms) {
  auto it = state.last_pairing_offer_emitted_at_ms.find(offer_id);
  return it != state.last_pairing_offer_emitted_at_ms.end() &&
         (now_ms - it->second) < NotificationPolicy::kPairingOfferCooldownMs;
}

NotifierState PruneBurstWindow(NotifierState state, int64_t now_ms) {
  state.recent_burst_emits_ms.erase(
      std::remove_if(state.recent_burst_emits_ms.begin(),
                     state.recent_burst_emits_ms.end(),
                     [now_ms](int64_t t) {
                       return (now_ms - t) >= NotificationPolicy::kBurstWindowMs;
                     }),
      state.recent_burst_emits_ms.end());
  return state;
}

void RecordEmit(NotifierState* state,
                const NotificationIntent& intent,
                int64_t now_ms,
                bool counts_toward_burst) {
  state->last_emitted_at_ms[intent.key.Composite()] = now_ms;
  if (intent.key.kind == NotificationKind::PairingAvailable) {
    state->last_pairing_offer_emitted_at_ms[intent.key.subject] = now_ms;
  }
  if (intent.key.kind == NotificationKind::AdbUnavailable) {
    state->adb_unavailable_outage_notified = true;
  }
  if (intent.key.kind == NotificationKind::DiscoveryBroken) {
    state->discovery_broken_notified = true;
  }
  if (counts_toward_burst) {
    state->recent_burst_emits_ms.push_back(now_ms);
  }
}

const DeviceView* FindDevice(const AppSnapshot& snap,
                             const std::string& device_id) {
  for (const auto& d : snap.devices) {
    if (d.id == device_id) {
      return &d;
    }
  }
  return nullptr;
}

const PairingOfferView* FindOffer(const AppSnapshot& snap,
                                  const std::string& offer_id) {
  for (const auto& o : snap.pairing_offers) {
    if (o.id == offer_id) {
      return &o;
    }
  }
  return nullptr;
}

bool IsHoldStillValid(const NotificationKey& key, const AppSnapshot& current) {
  switch (key.kind) {
    case NotificationKind::DeviceConnected: {
      const DeviceView* d = FindDevice(current, key.subject);
      return d != nullptr && d->state == DeviceStateKind::Connected;
    }
    case NotificationKind::DeviceDisconnected:
      return FindDevice(current, key.subject) == nullptr;
    case NotificationKind::AuthorizationRequired: {
      const DeviceView* d = FindDevice(current, key.subject);
      return d != nullptr && d->state == DeviceStateKind::Unauthorized;
    }
    case NotificationKind::UsbAccessDenied: {
      const DeviceView* d = FindDevice(current, key.subject);
      return d != nullptr && d->state == DeviceStateKind::UsbAccessDenied;
    }
    case NotificationKind::DeviceProblem: {
      const DeviceView* d = FindDevice(current, key.subject);
      return d != nullptr && d->state == DeviceStateKind::Offline;
    }
    case NotificationKind::PairingAvailable:
      return FindOffer(current, key.subject) != nullptr;
    case NotificationKind::AdbUnavailable:
      return current.health.kind == GlobalHealthKind::AdbUnavailable;
    case NotificationKind::DiscoveryBroken:
      return current.discovery_health.has_value();
    case NotificationKind::PairingResult:
    case NotificationKind::DevicesChanged:
      return false;
  }
  return false;
}

}  // namespace

NotificationDecision NotificationPolicy::Evaluate(
    const std::optional<AppSnapshot>& previous,
    const AppSnapshot& next,
    const NotifierState& state,
    int64_t now_ms) {
  NotifierState next_state = state;

  if (next.health.kind != GlobalHealthKind::AdbUnavailable &&
      next_state.adb_unavailable_outage_notified) {
    next_state.adb_unavailable_outage_notified = false;
  }
  if (!next.discovery_health.has_value() && previous.has_value() &&
      previous->discovery_health.has_value() &&
      next_state.discovery_broken_notified) {
    next_state.discovery_broken_notified = false;
  }

  if (next_state.shutting_down || next_state.suspended) {
    return NotificationDecision{{}, {}, {}, std::move(next_state)};
  }

  if (now_ms < next_state.suppress_until_ms || !previous.has_value() ||
      !next_state.baseline_established) {
    next_state.baseline_established = true;
    return NotificationDecision{{}, {}, {}, std::move(next_state)};
  }

  next_state.baseline_established = true;
  std::vector<PendingHold> arm;
  std::vector<NotificationKey> cancel;
  std::vector<NotificationIntent> immediate;

  auto arm_hold = [&](NotificationKind kind, const std::string& subject,
                      int64_t hold_ms,
                      std::optional<std::string> hint = std::nullopt) {
    arm.push_back(PendingHold{
        .key = NotificationKey{kind, subject},
        .due_at_ms = now_ms + hold_ms,
        .armed_at_ms = now_ms,
        .display_name_hint = std::move(hint),
    });
  };
  auto cancel_hold = [&](NotificationKind kind, const std::string& subject) {
    cancel.push_back(NotificationKey{kind, subject});
  };

  std::unordered_map<std::string, const DeviceView*> prev_by_id;
  for (const auto& d : previous->devices) {
    prev_by_id.emplace(d.id, &d);
  }
  std::unordered_map<std::string, const DeviceView*> next_by_id;
  for (const auto& d : next.devices) {
    next_by_id.emplace(d.id, &d);
  }

  for (const auto& d : next.devices) {
    const DeviceView* before = nullptr;
    auto it = prev_by_id.find(d.id);
    if (it != prev_by_id.end()) {
      before = it->second;
    }

    if (d.state == DeviceStateKind::Connected &&
        (before == nullptr || before->state != DeviceStateKind::Connected)) {
      arm_hold(NotificationKind::DeviceConnected, d.id, kHoldConnectedMs);
    } else if (d.state != DeviceStateKind::Connected) {
      cancel_hold(NotificationKind::DeviceConnected, d.id);
    }

    if (d.state == DeviceStateKind::Unauthorized &&
        (before == nullptr || before->state != DeviceStateKind::Unauthorized)) {
      arm_hold(NotificationKind::AuthorizationRequired, d.id,
               kHoldAuthorizationMs);
    } else if (d.state != DeviceStateKind::Unauthorized) {
      cancel_hold(NotificationKind::AuthorizationRequired, d.id);
    }

    if (d.state == DeviceStateKind::UsbAccessDenied &&
        (before == nullptr ||
         before->state != DeviceStateKind::UsbAccessDenied)) {
      arm_hold(NotificationKind::UsbAccessDenied, d.id, kHoldUsbAccessDeniedMs);
    } else if (d.state != DeviceStateKind::UsbAccessDenied) {
      cancel_hold(NotificationKind::UsbAccessDenied, d.id);
    }

    if (d.state == DeviceStateKind::Offline &&
        (before == nullptr || before->state != DeviceStateKind::Offline)) {
      arm_hold(NotificationKind::DeviceProblem, d.id, kHoldDeviceProblemMs);
    } else if (d.state != DeviceStateKind::Offline) {
      cancel_hold(NotificationKind::DeviceProblem, d.id);
    }
  }

  for (const auto& was : previous->devices) {
    auto it = next_by_id.find(was.id);
    if (it == next_by_id.end()) {
      cancel_hold(NotificationKind::DeviceConnected, was.id);
      cancel_hold(NotificationKind::AuthorizationRequired, was.id);
      cancel_hold(NotificationKind::UsbAccessDenied, was.id);
      cancel_hold(NotificationKind::DeviceProblem, was.id);

      if (was.state == DeviceStateKind::Connected ||
          was.state == DeviceStateKind::Offline) {
        arm_hold(NotificationKind::DeviceDisconnected, was.id,
                 kHoldDisconnectedMs, was.display_name);
      }
    } else {
      cancel_hold(NotificationKind::DeviceDisconnected, was.id);
      if (it->second->state != DeviceStateKind::Connected) {
        cancel_hold(NotificationKind::DeviceConnected, was.id);
      }
    }
  }

  // Pairing offers
  std::unordered_set<std::string> prev_offers;
  for (const auto& o : previous->pairing_offers) {
    prev_offers.insert(o.id);
  }
  std::unordered_set<std::string> next_offers;
  for (const auto& o : next.pairing_offers) {
    next_offers.insert(o.id);
    if (prev_offers.find(o.id) == prev_offers.end()) {
      arm_hold(NotificationKind::PairingAvailable, o.id,
               kHoldPairingAvailableMs);
    }
  }
  for (const auto& id : prev_offers) {
    if (next_offers.find(id) == next_offers.end()) {
      cancel_hold(NotificationKind::PairingAvailable, id);
    }
  }

  // Immediate pairing result
  if (next.pairing.has_value() &&
      (next.pairing->phase == PairingPhase::PairedWaitingForDevice ||
       next.pairing->phase == PairingPhase::Failed)) {
    bool entered = !previous->pairing.has_value() ||
                   previous->pairing->offer_id != next.pairing->offer_id ||
                   previous->pairing->phase != next.pairing->phase;
    if (entered) {
      if (next.pairing->phase == PairingPhase::PairedWaitingForDevice) {
        immediate.push_back(NotificationIntent{
            .key = NotificationKey{NotificationKind::PairingResult,
                                   next.pairing->offer_id},
            .title = Vocabulary::Notifications::PairingSuccessTitle(
                next.pairing->display_name),
            .body = std::string(Vocabulary::Notifications::kPairingSuccessBody),
            .severity = NotificationSeverity::Info,
        });
      } else {
        immediate.push_back(NotificationIntent{
            .key = NotificationKey{NotificationKind::PairingResult,
                                   next.pairing->offer_id},
            .title = std::string(Vocabulary::Notifications::kPairingFailedTitle),
            .body = next.pairing->detail.value_or(
                std::string(Vocabulary::Pairing::kFailedDetail)),
            .severity = NotificationSeverity::Warning,
        });
      }
    }
  }

  // ADB unavailable
  if (next.health.kind == GlobalHealthKind::AdbUnavailable &&
      previous->health.kind != GlobalHealthKind::AdbUnavailable &&
      !next_state.adb_unavailable_outage_notified) {
    arm_hold(NotificationKind::AdbUnavailable, std::string(kGlobalSubject),
             kHoldAdbUnavailableMs);
  } else if (next.health.kind != GlobalHealthKind::AdbUnavailable) {
    cancel_hold(NotificationKind::AdbUnavailable, std::string(kGlobalSubject));
  }

  // Discovery broken
  if (next.discovery_health.has_value() &&
      !previous->discovery_health.has_value() &&
      !next_state.discovery_broken_notified) {
    arm_hold(NotificationKind::DiscoveryBroken, std::string(kGlobalSubject),
             kHoldDiscoveryBrokenMs);
  } else if (!next.discovery_health.has_value()) {
    cancel_hold(NotificationKind::DiscoveryBroken, std::string(kGlobalSubject));
  }

  return NotificationDecision{std::move(arm), std::move(cancel),
                              std::move(immediate), std::move(next_state)};
}

std::optional<NotificationIntent> NotificationPolicy::TryMaterializeHold(
    const PendingHold& hold,
    const AppSnapshot& current,
    const NotifierState& state,
    int64_t now_ms) {
  if (state.shutting_down || state.suspended ||
      now_ms < state.suppress_until_ms) {
    return std::nullopt;
  }
  if (!IsHoldStillValid(hold.key, current)) {
    return std::nullopt;
  }
  if (IsDedupBlocked(hold.key, state, now_ms)) {
    return std::nullopt;
  }
  if (hold.key.kind == NotificationKind::PairingAvailable &&
      IsPairingOfferCooldownBlocked(hold.key.subject, state, now_ms)) {
    return std::nullopt;
  }
  if (hold.key.kind == NotificationKind::AdbUnavailable &&
      state.adb_unavailable_outage_notified) {
    return std::nullopt;
  }
  if (hold.key.kind == NotificationKind::DiscoveryBroken &&
      state.discovery_broken_notified) {
    return std::nullopt;
  }

  switch (hold.key.kind) {
    case NotificationKind::DeviceConnected: {
      const DeviceView* d = FindDevice(current, hold.key.subject);
      if (d == nullptr) return std::nullopt;
      return NotificationIntent{
          .key = hold.key,
          .title =
              Vocabulary::Notifications::DeviceConnectedTitle(d->display_name),
          .body =
              Vocabulary::Notifications::DeviceConnectedBody(d->transport_label),
          .severity = NotificationSeverity::Info,
      };
    }
    case NotificationKind::DeviceDisconnected: {
      std::string name = hold.display_name_hint.value_or(
          std::string(Vocabulary::Pairing::kDefaultDeviceName));
      return NotificationIntent{
          .key = hold.key,
          .title = Vocabulary::Notifications::DeviceDisconnectedTitle(name),
          .body = {},
          .severity = NotificationSeverity::Info,
      };
    }
    case NotificationKind::AuthorizationRequired: {
      const DeviceView* d = FindDevice(current, hold.key.subject);
      if (d == nullptr) return std::nullopt;
      return NotificationIntent{
          .key = hold.key,
          .title = Vocabulary::Notifications::AuthorizationRequiredTitle(
              d->display_name),
          .body =
              std::string(Vocabulary::Notifications::kAuthorizationRequiredBody),
          .severity = NotificationSeverity::Warning,
      };
    }
    case NotificationKind::UsbAccessDenied: {
      const DeviceView* d = FindDevice(current, hold.key.subject);
      if (d == nullptr) return std::nullopt;
      return NotificationIntent{
          .key = hold.key,
          .title =
              Vocabulary::Notifications::UsbAccessDeniedTitle(d->display_name),
          .body = std::string(Vocabulary::Notifications::kUsbAccessDeniedBody),
          .severity = NotificationSeverity::Warning,
      };
    }
    case NotificationKind::DeviceProblem: {
      const DeviceView* d = FindDevice(current, hold.key.subject);
      if (d == nullptr) return std::nullopt;
      return NotificationIntent{
          .key = hold.key,
          .title =
              Vocabulary::Notifications::DeviceProblemTitle(d->display_name),
          .body = std::string(Vocabulary::Notifications::kDeviceProblemBody),
          .severity = NotificationSeverity::Warning,
      };
    }
    case NotificationKind::PairingAvailable: {
      const PairingOfferView* o = FindOffer(current, hold.key.subject);
      if (o == nullptr) return std::nullopt;
      return NotificationIntent{
          .key = hold.key,
          .title =
              std::string(Vocabulary::Notifications::kPairingAvailableTitle),
          .body = Vocabulary::Notifications::PairingAvailableBody(
              o->display_name),
          .severity = NotificationSeverity::Info,
      };
    }
    case NotificationKind::AdbUnavailable:
      return NotificationIntent{
          .key = hold.key,
          .title = std::string(Vocabulary::Notifications::kAdbUnavailableTitle),
          .body = std::string(Vocabulary::Notifications::kAdbUnavailableBody),
          .severity = NotificationSeverity::Error,
      };
    case NotificationKind::DiscoveryBroken:
      if (!current.discovery_health.has_value()) return std::nullopt;
      return NotificationIntent{
          .key = hold.key,
          .title = current.discovery_health->title,
          .body = current.discovery_health->detail,
          .severity = NotificationSeverity::Warning,
      };
    case NotificationKind::PairingResult:
    case NotificationKind::DevicesChanged:
      return std::nullopt;
  }
  return std::nullopt;
}

std::pair<std::vector<NotificationIntent>, NotifierState>
NotificationPolicy::ApplyBurstAndRecord(
    std::span<const NotificationIntent> candidates,
    const NotifierState& state,
    int64_t now_ms) {
  NotifierState next = PruneBurstWindow(state, now_ms);
  std::vector<NotificationIntent> to_emit;
  std::vector<NotificationIntent> excess;
  size_t used_in_window = next.recent_burst_emits_ms.size();

  for (const auto& intent : candidates) {
    if (intent.key.kind == NotificationKind::PairingResult) {
      if (IsDedupBlocked(intent.key, next, now_ms)) {
        continue;
      }
      to_emit.push_back(intent);
      RecordEmit(&next, intent, now_ms, false);
      continue;
    }

    if (IsDedupBlocked(intent.key, next, now_ms)) {
      continue;
    }
    if (intent.key.kind == NotificationKind::PairingAvailable &&
        IsPairingOfferCooldownBlocked(intent.key.subject, next, now_ms)) {
      continue;
    }
    if (intent.key.kind == NotificationKind::AdbUnavailable &&
        next.adb_unavailable_outage_notified) {
      continue;
    }
    if (intent.key.kind == NotificationKind::DiscoveryBroken &&
        next.discovery_broken_notified) {
      continue;
    }

    if (used_in_window < kBurstIndividualCap) {
      to_emit.push_back(intent);
      RecordEmit(&next, intent, now_ms, true);
      ++used_in_window;
    } else {
      excess.push_back(intent);
    }
  }

  if (!excess.empty()) {
    int n = static_cast<int>(excess.size());
    NotificationIntent aggregate{
        .key = NotificationKey{NotificationKind::DevicesChanged,
                               std::string(kGlobalSubject)},
        .title = Vocabulary::Notifications::DevicesChangedTitle(n),
        .body = std::string(Vocabulary::Notifications::kDevicesChangedBody),
        .severity = NotificationSeverity::Info,
    };
    if (!IsDedupBlocked(aggregate.key, next, now_ms)) {
      to_emit.push_back(aggregate);
      RecordEmit(&next, aggregate, now_ms, true);
    }
    for (const auto& collapsed : excess) {
      RecordEmit(&next, collapsed, now_ms, false);
    }
  }

  return {std::move(to_emit), std::move(next)};
}

NotifierState NotificationPolicy::ForResume(const NotifierState& state,
                                            int64_t now_ms) {
  NotifierState next = state;
  next.suspended = false;
  next.suppress_until_ms = now_ms + kResumeSuppressMs;
  next.baseline_established = true;
  return next;
}

NotifierState NotificationPolicy::ForSuspend(const NotifierState& state) {
  NotifierState next = state;
  next.suspended = true;
  return next;
}

NotifierState NotificationPolicy::ForShutdown(const NotifierState& state) {
  NotifierState next = state;
  next.shutting_down = true;
  next.suspended = true;
  return next;
}

}  // namespace adbhawk::core
