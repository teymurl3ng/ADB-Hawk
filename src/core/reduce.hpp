#pragma once

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <unordered_map>
#include <utility>
#include <variant>
#include <vector>

#include "core/adb_wire.hpp"
#include "core/protobuf_lite.hpp"
#include "core/snapshot.hpp"
#include "core/vocabulary.hpp"

namespace adbhawk::core {

enum class AdbServerPhase {
  Probing,
  Starting,
  Up,
  Restoring,
  Unavailable,
};

struct AdbServerStateChangedEvent {
  AdbServerPhase phase = AdbServerPhase::Probing;
  std::optional<bool> mdns_enabled;
};

struct SetPinnedEvent {
  bool pinned = false;
};

struct ShellAppearanceChangedEvent {
  ThemeKind theme = ThemeKind::Dark;
  bool transparency_enabled = false;
  bool forced_colors = false;
  bool reduced_motion = false;
};

struct DeviceListReceivedEvent {
  int64_t sequence = 0;
  int64_t at_ms = 0;
  std::vector<RawDevice> devices;
};

struct DeviceTrackerFailedEvent {};

struct DeviceTrackerGraceExpiredEvent {
  int64_t generation = 0;
};

struct MdnsServicesReceivedEvent {
  int64_t sequence = 0;
  int64_t at_ms = 0;
  std::vector<RawMdnsService> services;
};

struct MdnsFallbackReceivedEvent {
  int64_t sequence = 0;
  int64_t at_ms = 0;
  std::vector<RawMdnsService> services;
};

struct MdnsTrackerFailedEvent {};

struct WirelessDiscoveryRestoringExpiredEvent {
  int64_t generation = 0;
};

struct WirelessDiscoveryUnavailableExpiredEvent {
  int64_t generation = 0;
};

struct PairingProgressedEvent {
  std::string offer_id;
  PairingPhase phase = PairingPhase::Cleared;
  PairingFailureKind failure_kind = PairingFailureKind::WrongCode;
  std::optional<std::string> display_name;
};

struct PairingWaitExpiredEvent {
  int64_t generation = 0;
};

struct MetadataReceivedEvent {
  std::string device_id;
  std::optional<int> battery;
  std::optional<std::string> model;
  std::optional<std::string> android_version;
  std::optional<std::string> marketing_name;
  std::optional<std::string> device_name;
  std::optional<std::string> brand;
  std::optional<std::string> manufacturer;
};

struct DiscoveryCheckStartedEvent {};

struct DiscoveryDiagnosisReceivedEvent {
  DiscoveryDiagnosis diagnosis;
};

struct DiscoveryRepairCatalogEvent {
  std::vector<DiscoveryRepairDescriptor> repairs;
};

struct DiscoveryRepairStartedEvent {
  std::string repair_id;
};

struct DiscoveryRepairFinishedEvent {
  std::string repair_id;
  DiscoveryRepairOutcome outcome = DiscoveryRepairOutcome::Completed;
};

struct PairingQrStartedEvent {
  PairingQrView view;
};

struct PairingQrClearedEvent {
  int64_t generation = 0;
};

using AdbViewEvent = std::variant<AdbServerStateChangedEvent,
                                  SetPinnedEvent,
                                  ShellAppearanceChangedEvent,
                                  DeviceListReceivedEvent,
                                  DeviceTrackerFailedEvent,
                                  DeviceTrackerGraceExpiredEvent,
                                  MdnsServicesReceivedEvent,
                                  MdnsFallbackReceivedEvent,
                                  MdnsTrackerFailedEvent,
                                  WirelessDiscoveryRestoringExpiredEvent,
                                  WirelessDiscoveryUnavailableExpiredEvent,
                                  PairingProgressedEvent,
                                  PairingWaitExpiredEvent,
                                  MetadataReceivedEvent,
                                  DiscoveryCheckStartedEvent,
                                  DiscoveryDiagnosisReceivedEvent,
                                  DiscoveryRepairCatalogEvent,
                                  DiscoveryRepairStartedEvent,
                                  DiscoveryRepairFinishedEvent,
                                  PairingQrStartedEvent,
                                  PairingQrClearedEvent>;

struct DeviceMetadataSession {
  int64_t generation = 0;
  std::string adb_serial;
  bool connected_eligible = false;
  std::optional<int64_t> battery_observed_at_ms;

  bool operator==(const DeviceMetadataSession&) const = default;
};

struct PairingOfferBinding {
  std::string offer_id;
  std::string host;
  int port = 0;
  std::optional<std::string> serial;
  std::string display_name;

  bool operator==(const PairingOfferBinding&) const = default;
};

struct DeviceEngineState {
  int64_t latest_device_sequence = 0;
  std::optional<int64_t> latest_applied_list_at_ms;
  std::vector<RawDevice> latest_raw_devices;
  int64_t active_grace_generation = 0;
  int64_t next_outage_generation = 0;

  int64_t latest_mdns_sequence = 0;
  std::optional<int64_t> latest_applied_mdns_at_ms;
  std::vector<RawMdnsService> latest_mdns_services;

  int64_t latest_fallback_sequence = 0;
  std::optional<int64_t> latest_applied_fallback_at_ms;
  std::vector<RawMdnsService> latest_fallback_services;

  bool wireless_failed = false;
  int64_t active_wireless_restoring_generation = 0;
  int64_t active_wireless_unavailable_generation = 0;

  std::unordered_map<std::string, DeviceMetadataSession> metadata_sessions;
  int64_t next_metadata_generation = 0;

  std::unordered_map<std::string, PairingOfferBinding> offer_bindings;
  int64_t active_pairing_wait_generation = 0;
  std::optional<std::string> active_pairing_offer_id;
  std::optional<std::string> active_pairing_host;
  std::optional<std::string> active_pairing_serial;
  std::optional<std::string> active_pairing_display_name;

  int64_t active_qr_session_generation = 0;
  int64_t next_qr_session_generation = 1;

  std::optional<bool> server_mdns_enabled;
  bool discovery_check_running = false;
  std::optional<DiscoveryDiagnosis> latest_discovery_diagnosis;
  std::vector<DiscoveryRepairDescriptor> repair_catalog;
  std::unordered_map<std::string, DiscoveryRepairOutcome> repair_results;
  std::optional<std::string> running_repair_id;
  std::optional<std::string> awaiting_measure_repair_id;
  std::optional<std::string> repair_result_line;
};

enum class GraceCommandKind {
  None,
  Cancel,
  Schedule,
};

struct GraceCommand {
  GraceCommandKind kind = GraceCommandKind::None;
  int64_t generation = 0;

  static GraceCommand None() { return {}; }
  static GraceCommand Cancel() { return {GraceCommandKind::Cancel, 0}; }
  static GraceCommand Schedule(int64_t gen) {
    return {GraceCommandKind::Schedule, gen};
  }

  bool operator==(const GraceCommand&) const = default;
};

struct ReduceResult {
  AppSnapshot snapshot;
  DeviceEngineState device_state;
  GraceCommand grace;
  GraceCommand pairing_wait;
  GraceCommand wireless_restoring;
  GraceCommand wireless_unavailable;
  GraceCommand pairing_qr;
};

inline constexpr int64_t kTrackerFailureGraceMs = 2'000;
inline constexpr int64_t kWirelessRestoringDelayMs = 5'000;
inline constexpr int64_t kWirelessUnavailableDelayMs = 60'000;
inline constexpr int64_t kPairingWaitTimeoutMs = 60'000;
inline constexpr int64_t kQrSessionTimeoutMs = 120'000;

AppSnapshot MakeInitialSnapshot(int64_t now_ms = 0);

std::vector<RawMdnsService> MergeEffectiveMdnsServices(
    std::span<const RawMdnsService> latest_mdns,
    std::span<const RawMdnsService> latest_fallback);

std::pair<TrayStatusKind, std::string> AggregateTrayStatus(
    GlobalHealthKind health_kind,
    std::span<const DeviceView> devices,
    std::span<const PairingOfferView> pairing_offers);

ReduceResult ReduceWithDeviceState(const AppSnapshot& current,
                                   const DeviceEngineState& device_state,
                                   const AdbViewEvent& evt,
                                   int64_t now_ms);

AppSnapshot Reduce(const AppSnapshot& current,
                   const AdbViewEvent& evt,
                   int64_t now_ms);

}  // namespace adbhawk::core
