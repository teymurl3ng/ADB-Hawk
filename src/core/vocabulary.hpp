#pragma once

#include <optional>
#include <span>
#include <string>
#include <string_view>

#include "core/snapshot.hpp"

namespace adbhawk::core {

enum class MdnsServiceKind {
  Unknown,
  Connect,
  Pairing,
  LegacyTcp,
};

struct RawMdnsService {
  std::string instance_name;
  std::string service_type;
  MdnsServiceKind kind = MdnsServiceKind::Unknown;
  std::optional<std::string> domain;
  std::optional<std::string> ipv4;
  std::vector<std::string> ipv6;
  int port = 0;
  std::optional<std::string> product_model;
  std::optional<std::string> build_version_sdk_full;
  std::optional<std::string> given_name;
  std::optional<std::string> serial;
  std::optional<std::string> mdns_service_version;
  std::optional<std::string> hostname;
  std::optional<bool> known_device;

  std::string DiscoveryKey() const {
    return instance_name + "\x1f" + service_type;
  }

  bool operator==(const RawMdnsService&) const = default;
};

namespace Vocabulary {

namespace Health {
inline constexpr std::string_view kAdbUnavailableTitle = "ADB unavailable";
inline constexpr std::string_view kAdbUnavailableDetail =
    "ADB Hawk couldn't find or start ADB.";
// Linux delta (DESIGN.md):
inline constexpr std::string_view kAdbUnavailableHint =
    "Install Android Platform Tools or make adb available on your PATH.";
inline constexpr std::string_view kAdbUnavailableTrayTooltip = "ADB unavailable";

inline constexpr std::string_view kAdbRestoringTitle = "Restoring ADB…";
inline constexpr std::string_view kAdbRestoringDetail =
    "ADB Hawk is reconnecting to the local ADB service.";

inline constexpr std::string_view kNoDevicesTrayTooltip = "No ADB devices";
}  // namespace Health

namespace Device {
inline constexpr std::string_view kConnectedStateLabel = "Connected";
inline constexpr std::string_view kUnauthorizedStateLabel = "Authorization required";
// Linux D12 delta (DESIGN.md):
inline constexpr std::string_view kUsbAccessDeniedStateLabel =
    "USB access not permitted";
inline constexpr std::string_view kOfflineStateLabel = "Device offline";
inline constexpr std::string_view kRecoveryStateLabel = "Recovery";
inline constexpr std::string_view kBootloaderStateLabel = "Bootloader";
inline constexpr std::string_view kSideloadStateLabel = "Sideload";
inline constexpr std::string_view kRescueStateLabel = "Rescue";
inline constexpr std::string_view kAuthorizingStateLabel = "Authorizing";
inline constexpr std::string_view kConnectingStateLabel = "Connecting";
inline constexpr std::string_view kDetachedStateLabel = "Detached";
inline constexpr std::string_view kUnknownStateLabel = "Unknown state";

inline constexpr std::string_view kUnauthorizedGuidance =
    "Check the phone and allow USB debugging.";
inline constexpr std::string_view kUsbAccessDeniedGuidance =
    "This computer isn't allowing access to the phone's USB connection.";
inline constexpr std::string_view kOfflineGuidance =
    "ADB can see this device, but the connection isn’t responding.";

inline constexpr std::string_view kConnectedMetaTemplate = "Connected {age}";
inline constexpr std::string_view kAgeOnlyMetaTemplate = "{age}";

inline constexpr std::string_view kTransportUsb = "USB";
inline constexpr std::string_view kTransportWifi = "Wi-Fi";
inline constexpr std::string_view kTransportUnknown = "Unknown";

inline constexpr std::string_view kCopySerialKey = "serial";
inline constexpr std::string_view kCopySerialLabel = "Serial";
inline constexpr std::string_view kCopyAddressKey = "address";
inline constexpr std::string_view kCopyAddressLabel = "Address";
}  // namespace Device

namespace Pairing {
inline constexpr std::string_view kOfferHeading = "Wireless pairing available";
inline constexpr std::string_view kOfferDetail = "Waiting for a pairing code";
inline constexpr std::string_view kActionLabel = "Pair";
inline constexpr std::string_view kDefaultDeviceName = "Android device";

inline constexpr std::string_view kCodeEntryDetail =
    "Enter the six-digit code shown on the phone.";
inline constexpr std::string_view kProgressDetail =
    "Keep the phone nearby while pairing completes.";
inline constexpr std::string_view kSuccessDetail = "Waiting for ADB connection…";
inline constexpr std::string_view kFailedTitle = "Pairing failed";
inline constexpr std::string_view kFailedDetail = "Check the code and try again.";

std::string FailedDetailFor(PairingFailureKind kind);
std::string CodeEntryTitle(std::string_view display_name);
std::string ProgressTitle(std::string_view display_name);
std::string SuccessTitle(std::string_view display_name);

inline constexpr std::string_view kQrInstructionPhone =
    "On the phone, open Developer options → Wireless debugging.";
inline constexpr std::string_view kQrInstructionScan =
    "Tap Pair device with QR code and point the camera at this code.";
inline constexpr std::string_view kQrCancelLabel = "Cancel";
}  // namespace Pairing

namespace Wireless {
inline constexpr std::string_view kUnavailableTitle =
    "Wireless discovery unavailable";
inline constexpr std::string_view kUnavailableDetail =
    "USB ADB is still working normally.";
inline constexpr std::string_view kRestoringTitle =
    "Restoring wireless discovery…";
inline constexpr std::string_view kRestoringDetail =
    "USB ADB is still working normally.";
}  // namespace Wireless

namespace Discovery {
inline constexpr std::string_view kBrokenTitle =
    "Wireless discovery isn't working";
inline constexpr std::string_view kCheckAgainActionLabel = "Check again";
inline constexpr std::string_view kMeasuredNothingEvidence =
    "No announcements were measured just now.";
inline constexpr std::string_view kProbeUnavailableEvidence =
    "ADB Hawk couldn't measure the network just now.";
inline constexpr std::string_view kVirtualAdaptersEvidence =
    "Virtual network adapters are active";

std::string BrokenDetail(std::string_view display_name);
std::string MeasuredEvidence(int count);
std::string PortHeldEvidence(std::string_view process_or_owner_name);
std::string PortHoldersEvidence(std::string_view first_name, int additional_count);
std::string VirtualAdaptersNamedEvidence(std::string_view only);
std::string VirtualAdaptersNamedEvidence(std::string_view first,
                                         std::string_view second);

inline constexpr std::string_view kRestartAdbLabel = "Restart ADB";
inline constexpr std::string_view kRestartAdbRunningLabel = "Restarting ADB…";
inline constexpr std::string_view kResultStillBroken =
    "Still not working after this action.";
inline constexpr std::string_view kResultDeclined =
    "Approval was declined, so nothing was changed.";
inline constexpr std::string_view kResultFailed =
    "The action couldn't be completed.";

std::string RepairLabel(DiscoveryRepairActionKind kind);
std::string RepairRunningLabel(DiscoveryRepairActionKind kind);
std::string ResultLineFor(DiscoveryRepairOutcome outcome);
}  // namespace Discovery

namespace Notifications {
std::string DeviceConnectedTitle(std::string_view display_name);
std::string DeviceConnectedBody(std::string_view transport_label);
std::string DeviceDisconnectedTitle(std::string_view display_name);
std::string AuthorizationRequiredTitle(std::string_view display_name);
inline constexpr std::string_view kAuthorizationRequiredBody =
    Device::kUnauthorizedGuidance;
std::string UsbAccessDeniedTitle(std::string_view display_name);
inline constexpr std::string_view kUsbAccessDeniedBody =
    Device::kUsbAccessDeniedGuidance;
std::string DeviceProblemTitle(std::string_view display_name);
inline constexpr std::string_view kDeviceProblemBody = Device::kOfflineGuidance;
inline constexpr std::string_view kPairingAvailableTitle = Pairing::kOfferHeading;
std::string PairingAvailableBody(std::string_view display_name);
std::string PairingSuccessTitle(std::string_view display_name);
inline constexpr std::string_view kPairingSuccessBody = Pairing::kSuccessDetail;
inline constexpr std::string_view kPairingFailedTitle = Pairing::kFailedTitle;
inline constexpr std::string_view kAdbUnavailableTitle =
    Health::kAdbUnavailableTitle;
inline constexpr std::string_view kAdbUnavailableBody =
    Health::kAdbUnavailableDetail;
std::string DevicesChangedTitle(int count);
inline constexpr std::string_view kDevicesChangedBody = "";
}  // namespace Notifications

namespace Menu {
inline constexpr std::string_view kOpenAdbView = "Open ADB Hawk";
inline constexpr std::string_view kStartAtLogin = "Start at login";
inline constexpr std::string_view kNotifications = "Notifications";
inline constexpr std::string_view kStatusBubble = "Status bubble";
inline constexpr std::string_view kTheme = "Theme";
inline constexpr std::string_view kThemeSystem = "System";
inline constexpr std::string_view kThemeLight = "Light";
inline constexpr std::string_view kThemeDark = "Dark";
inline constexpr std::string_view kExit = "Exit";
}  // namespace Menu

}  // namespace Vocabulary

// Human presentation title resolver. Never returns transport/protocol identifiers.
std::string ResolveDeviceDisplayName(
    std::optional<std::string_view> marketing_name = std::nullopt,
    std::optional<std::string_view> device_name = std::nullopt,
    std::optional<std::string_view> model = std::nullopt,
    std::optional<std::string_view> brand = std::nullopt,
    std::optional<std::string_view> manufacturer = std::nullopt,
    std::optional<std::string_view> product = std::nullopt,
    std::optional<std::string_view> given_name = std::nullopt);

std::string ResolvePairingDisplayName(const RawMdnsService& service,
                                      std::span<const DeviceView> devices);

bool IsTransportOrProtocolIdentifier(std::string_view candidate);

}  // namespace adbhawk::core
