#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace adbhawk::core {

enum class GlobalHealthKind {
  Healthy,
  AdbUnavailable,
  AdbRestoring,
};

enum class TrayStatusKind {
  Idle,
  Connected,
  Attention,
  Failure,
  Special,
  PairingAvailable,
};

enum class ThemeKind {
  Dark,
  Light,
};

enum class DeviceStateKind {
  Connected,
  Unauthorized,
  UsbAccessDenied,  // Linux D12: "no permissions" (udev/USB access denied on host)
  Offline,
  Recovery,
  Bootloader,
  Sideload,
  Rescue,
  Authorizing,
  Connecting,
  Detached,
  Unknown,
};

enum class SeverityKind {
  Positive,
  Attention,
  Failure,
  Informational,
  Neutral,
};

enum class TransportKind {
  Usb,
  WiFi,
  Unknown,
};

struct HealthView {
  GlobalHealthKind kind = GlobalHealthKind::Healthy;
  std::optional<std::string> title;
  std::optional<std::string> detail;
  std::optional<std::string> hint;
  TrayStatusKind tray = TrayStatusKind::Idle;
  std::string tray_tooltip;

  bool operator==(const HealthView&) const = default;
};

struct ShellView {
  bool pinned = false;
  ThemeKind theme = ThemeKind::Dark;
  bool transparency_enabled = false;
  bool forced_colors = false;
  bool reduced_motion = false;

  bool operator==(const ShellView&) const = default;
};

struct CopyFieldView {
  std::string key;
  std::string label;
  std::string value;

  bool operator==(const CopyFieldView&) const = default;
};

struct DeviceDetailView {
  std::optional<std::string> serial;
  std::optional<std::string> exact_model;
  std::optional<std::string> android_version;
  std::optional<std::string> address;
  std::vector<CopyFieldView> copy_fields;

  bool operator==(const DeviceDetailView&) const = default;
};

struct DeviceView {
  std::string id;
  std::string display_name;
  DeviceStateKind state = DeviceStateKind::Unknown;
  SeverityKind severity = SeverityKind::Neutral;
  std::string state_label;
  std::string status_line;
  std::optional<std::string> guidance;
  TransportKind transport = TransportKind::Unknown;
  std::string transport_label;
  std::optional<int> battery_percent;
  std::string meta_template;
  int64_t state_since_ms = 0;
  std::optional<int64_t> connected_since_ms;
  DeviceDetailView detail;

  bool operator==(const DeviceView&) const = default;
};

struct PairingOfferView {
  std::string id;
  std::string display_name;
  std::string heading;
  std::string detail;
  std::string action_label;
  std::string host_port;

  bool operator==(const PairingOfferView&) const = default;
};

enum class PairingPhase {
  Cleared,
  AwaitingCode,
  Pairing,
  PairedWaitingForDevice,
  Failed,
};

enum class PairingFailureKind {
  WrongCode,
  Timeout,
  RateLimited,
  InvalidRequest,
};

struct PairingQrView {
  std::string svg_data_uri;
  std::vector<std::string> instructions;
  std::string cancel_label;

  bool operator==(const PairingQrView&) const = default;
};

struct PairingOperationView {
  std::string offer_id;
  std::string display_name;
  PairingPhase phase = PairingPhase::AwaitingCode;
  std::string title;
  std::optional<std::string> detail;
  bool can_submit_code = false;
  bool can_cancel = true;
  bool can_retry = false;
  int code_length = 6;

  bool operator==(const PairingOperationView&) const = default;
};

enum class SubsystemKind {
  WirelessDiscovery,
};

struct SubsystemWarningView {
  SubsystemKind subsystem = SubsystemKind::WirelessDiscovery;
  SeverityKind severity = SeverityKind::Informational;
  std::string title;
  std::string detail;

  bool operator==(const SubsystemWarningView&) const = default;
};

enum class DiscoveryProbeOutcome {
  NotRun,
  Measured,
  Unavailable,
};

struct DiscoveryDiagnosis {
  DiscoveryProbeOutcome probe_outcome = DiscoveryProbeOutcome::NotRun;
  int announcement_count = 0;
  std::vector<std::string> port_holders;
  std::vector<std::string> virtual_adapters;

  bool operator==(const DiscoveryDiagnosis&) const = default;
};

enum class DiscoveryRepairActionKind {
  RestartAdb,
};

enum class DiscoveryRepairOutcome {
  Completed,
  Declined,
  Failed,
};

struct DiscoveryRepairDescriptor {
  std::string id;
  DiscoveryRepairActionKind kind = DiscoveryRepairActionKind::RestartAdb;
  std::optional<std::string> target;

  bool operator==(const DiscoveryRepairDescriptor&) const = default;
};

struct DiscoveryRepairCardAction {
  std::string id;
  std::string label;
  bool is_admin = false;
  bool is_running = false;
  std::string running_label;

  bool operator==(const DiscoveryRepairCardAction&) const = default;
};

struct DiscoveryHealthView {
  std::string title;
  std::string detail;
  std::vector<std::string> evidence;
  std::string action_label;
  bool can_check_again = true;
  std::vector<DiscoveryRepairCardAction> repairs;
  std::optional<std::string> result_line;

  bool operator==(const DiscoveryHealthView&) const = default;
};

struct AppSnapshot {
  int64_t revision = 0;
  int64_t generated_at_ms = 0;
  HealthView health;
  ShellView shell;
  std::vector<DeviceView> devices;
  std::vector<PairingOfferView> pairing_offers;
  std::optional<PairingOperationView> pairing;
  std::optional<PairingQrView> pairing_qr;
  std::vector<SubsystemWarningView> warnings;
  std::optional<DiscoveryHealthView> discovery_health;
  std::optional<std::string> device_count_label;

  bool operator==(const AppSnapshot&) const = default;
};

// Formats `{age}` inside a template using milliseconds on CLOCK_BOOTTIME.
std::string FormatAge(std::string_view template_str, int64_t since_ms, int64_t now_ms);

// Renders a single-line deterministic JSON representation for `--print-snapshots` and tests.
std::string SnapshotToJson(const AppSnapshot& snapshot);

}  // namespace adbhawk::core
