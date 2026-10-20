#include "core/vocabulary.hpp"

#include <algorithm>
#include <cctype>
#include <string>

namespace adbhawk::core {

namespace {

std::string TrimAscii(std::string_view sv) {
  std::string out;
  out.reserve(sv.size());
  for (char c : sv) {
    if (c != '\r' && c != '\n') {
      out.push_back(c);
    }
  }
  size_t first = 0;
  while (first < out.size() &&
         std::isspace(static_cast<unsigned char>(out[first])) != 0) {
    ++first;
  }
  size_t last = out.size();
  while (last > first &&
         std::isspace(static_cast<unsigned char>(out[last - 1])) != 0) {
    --last;
  }
  return out.substr(first, last - first);
}

bool EqualsIgnoreCase(std::string_view a, std::string_view b) {
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

bool StartsWithIgnoreCase(std::string_view s, std::string_view prefix) {
  if (s.size() < prefix.size()) {
    return false;
  }
  return EqualsIgnoreCase(s.substr(0, prefix.size()), prefix);
}

bool ContainsIgnoreCase(std::string_view haystack, std::string_view needle) {
  if (needle.empty()) {
    return true;
  }
  if (haystack.size() < needle.size()) {
    return false;
  }
  for (size_t i = 0; i + needle.size() <= haystack.size(); ++i) {
    if (EqualsIgnoreCase(haystack.substr(i, needle.size()), needle)) {
      return true;
    }
  }
  return false;
}

bool LooksLikeEndpoint(std::string_view s) {
  size_t colon = s.rfind(':');
  if (colon == std::string_view::npos || colon == 0 || colon + 1 >= s.size()) {
    return false;
  }
  for (size_t i = colon + 1; i < s.size(); ++i) {
    if (std::isdigit(static_cast<unsigned char>(s[i])) == 0) {
      return false;
    }
  }
  return true;
}

bool TryAcceptCandidate(std::optional<std::string_view> raw, std::string* out) {
  if (!raw.has_value()) {
    return false;
  }
  std::string cleaned = TrimAscii(*raw);
  if (cleaned.empty()) {
    return false;
  }
  if (EqualsIgnoreCase(cleaned, "null") || EqualsIgnoreCase(cleaned, "unknown")) {
    return false;
  }
  if (IsTransportOrProtocolIdentifier(cleaned)) {
    return false;
  }
  *out = std::move(cleaned);
  return true;
}

std::string TitleCaseBrand(std::string_view value) {
  if (value.empty()) {
    return {};
  }
  bool all_lower = true;
  for (char c : value) {
    unsigned char uc = static_cast<unsigned char>(c);
    if (std::isalpha(uc) != 0 && std::islower(uc) == 0) {
      all_lower = false;
      break;
    }
  }
  std::string out(value);
  if (all_lower) {
    out[0] = static_cast<char>(std::toupper(static_cast<unsigned char>(out[0])));
  }
  return out;
}

std::optional<std::string> PreferBrandLabel(
    std::optional<std::string_view> brand,
    std::optional<std::string_view> manufacturer) {
  std::string accepted;
  if (TryAcceptCandidate(brand, &accepted)) {
    return TitleCaseBrand(accepted);
  }
  if (TryAcceptCandidate(manufacturer, &accepted)) {
    return TitleCaseBrand(accepted);
  }
  return std::nullopt;
}

void AppendJsonEscaped(std::string* out, std::string_view s) {
  out->push_back('"');
  for (unsigned char c : s) {
    switch (c) {
      case '"':
        out->append("\\\"");
        break;
      case '\\':
        out->append("\\\\");
        break;
      case '\b':
        out->append("\\b");
        break;
      case '\f':
        out->append("\\f");
        break;
      case '\n':
        out->append("\\n");
        break;
      case '\r':
        out->append("\\r");
        break;
      case '\t':
        out->append("\\t");
        break;
      default:
        if (c < 0x20) {
          constexpr char kHex[] = "0123456789abcdef";
          out->append("\\u00");
          out->push_back(kHex[(c >> 4) & 0xF]);
          out->push_back(kHex[c & 0xF]);
        } else {
          out->push_back(static_cast<char>(c));
        }
        break;
    }
  }
  out->push_back('"');
}

void AppendJsonOptionalString(std::string* out,
                              const std::optional<std::string>& opt) {
  if (!opt.has_value()) {
    out->append("null");
  } else {
    AppendJsonEscaped(out, *opt);
  }
}

std::string_view GlobalHealthKindName(GlobalHealthKind kind) {
  switch (kind) {
    case GlobalHealthKind::Healthy:
      return "Healthy";
    case GlobalHealthKind::AdbUnavailable:
      return "AdbUnavailable";
    case GlobalHealthKind::AdbRestoring:
      return "AdbRestoring";
  }
  return "Healthy";
}

std::string_view TrayStatusKindName(TrayStatusKind kind) {
  switch (kind) {
    case TrayStatusKind::Idle:
      return "Idle";
    case TrayStatusKind::Connected:
      return "Connected";
    case TrayStatusKind::Attention:
      return "Attention";
    case TrayStatusKind::Failure:
      return "Failure";
    case TrayStatusKind::Special:
      return "Special";
    case TrayStatusKind::PairingAvailable:
      return "PairingAvailable";
  }
  return "Idle";
}

std::string_view DeviceStateKindName(DeviceStateKind kind) {
  switch (kind) {
    case DeviceStateKind::Connected:
      return "Connected";
    case DeviceStateKind::Unauthorized:
      return "Unauthorized";
    case DeviceStateKind::UsbAccessDenied:
      return "UsbAccessDenied";
    case DeviceStateKind::Offline:
      return "Offline";
    case DeviceStateKind::Recovery:
      return "Recovery";
    case DeviceStateKind::Bootloader:
      return "Bootloader";
    case DeviceStateKind::Sideload:
      return "Sideload";
    case DeviceStateKind::Rescue:
      return "Rescue";
    case DeviceStateKind::Authorizing:
      return "Authorizing";
    case DeviceStateKind::Connecting:
      return "Connecting";
    case DeviceStateKind::Detached:
      return "Detached";
    case DeviceStateKind::Unknown:
      return "Unknown";
  }
  return "Unknown";
}

std::string_view TransportKindName(TransportKind kind) {
  switch (kind) {
    case TransportKind::Usb:
      return "Usb";
    case TransportKind::WiFi:
      return "WiFi";
    case TransportKind::Unknown:
      return "Unknown";
  }
  return "Unknown";
}

}  // namespace

namespace Vocabulary {

namespace Pairing {

std::string FailedDetailFor(PairingFailureKind kind) {
  switch (kind) {
    case PairingFailureKind::Timeout:
      return "The phone did not respond in time. Try again.";
    case PairingFailureKind::RateLimited:
      return "Too many attempts. Wait a moment and try again.";
    case PairingFailureKind::InvalidRequest:
      return "The pairing offer changed. Tap Pair and enter the new code.";
    case PairingFailureKind::WrongCode:
      return std::string(kFailedDetail);
  }
  return std::string(kFailedDetail);
}

std::string CodeEntryTitle(std::string_view display_name) {
  return "Pairing with " + std::string(display_name);
}

std::string ProgressTitle(std::string_view display_name) {
  return "Pairing with " + std::string(display_name) + "…";
}

std::string SuccessTitle(std::string_view display_name) {
  return "Paired with " + std::string(display_name);
}

}  // namespace Pairing

namespace Discovery {

std::string BrokenDetail(std::string_view display_name) {
  return std::string(display_name) +
         " is connected over Wi-Fi, but ADB isn't receiving device announcements on this network.";
}

std::string MeasuredEvidence(int count) {
  if (count == 1) {
    return "1 device announcement is visible on this network.";
  }
  return std::to_string(count) +
         " device announcements are visible on this network.";
}

std::string PortHeldEvidence(std::string_view process_or_owner_name) {
  return std::string(process_or_owner_name) + " is using the discovery port.";
}

std::string PortHoldersEvidence(std::string_view first_name,
                                int additional_count) {
  if (additional_count == 1) {
    return std::string(first_name) +
           " and 1 other app are using the discovery port.";
  }
  return std::string(first_name) + " and " + std::to_string(additional_count) +
         " other apps are using the discovery port.";
}

std::string VirtualAdaptersNamedEvidence(std::string_view only) {
  return std::string(kVirtualAdaptersEvidence) + " (" + std::string(only) + ").";
}

std::string VirtualAdaptersNamedEvidence(std::string_view first,
                                         std::string_view second) {
  return std::string(kVirtualAdaptersEvidence) + " (" + std::string(first) +
         ", " + std::string(second) + ").";
}

std::string RepairLabel(DiscoveryRepairActionKind kind) {
  switch (kind) {
    case DiscoveryRepairActionKind::RestartAdb:
      return std::string(kRestartAdbLabel);
  }
  return std::string(kRestartAdbLabel);
}

std::string RepairRunningLabel(DiscoveryRepairActionKind kind) {
  switch (kind) {
    case DiscoveryRepairActionKind::RestartAdb:
      return std::string(kRestartAdbRunningLabel);
  }
  return std::string(kRestartAdbRunningLabel);
}

std::string ResultLineFor(DiscoveryRepairOutcome outcome) {
  switch (outcome) {
    case DiscoveryRepairOutcome::Completed:
      return std::string(kResultStillBroken);
    case DiscoveryRepairOutcome::Declined:
      return std::string(kResultDeclined);
    case DiscoveryRepairOutcome::Failed:
      return std::string(kResultFailed);
  }
  return std::string(kResultFailed);
}

}  // namespace Discovery

namespace Notifications {

std::string DeviceConnectedTitle(std::string_view display_name) {
  return std::string(display_name) + " connected";
}

std::string DeviceConnectedBody(std::string_view transport_label) {
  return "Connected over " + std::string(transport_label);
}

std::string DeviceDisconnectedTitle(std::string_view display_name) {
  return std::string(display_name) + " disconnected";
}

std::string AuthorizationRequiredTitle(std::string_view display_name) {
  return std::string(display_name) + " needs authorization";
}

std::string UsbAccessDeniedTitle(std::string_view display_name) {
  return std::string(display_name) + " needs USB access";
}

std::string DeviceProblemTitle(std::string_view display_name) {
  return std::string(display_name) + " is offline";
}

std::string PairingAvailableBody(std::string_view display_name) {
  return std::string(display_name) + " is waiting for a pairing code";
}

std::string PairingSuccessTitle(std::string_view display_name) {
  return Pairing::SuccessTitle(display_name);
}

std::string DevicesChangedTitle(int count) {
  return std::to_string(count) + " devices changed";
}

}  // namespace Notifications

}  // namespace Vocabulary

bool IsTransportOrProtocolIdentifier(std::string_view candidate) {
  std::string trimmed = TrimAscii(candidate);
  if (trimmed.empty()) {
    return true;
  }
  if (StartsWithIgnoreCase(trimmed, "adb-") ||
      StartsWithIgnoreCase(trimmed, "studio-") ||
      StartsWithIgnoreCase(trimmed, "adbview-")) {
    return true;
  }
  if (LooksLikeEndpoint(trimmed)) {
    return true;
  }
  if (ContainsIgnoreCase(trimmed, "_adb-tls-connect._tcp") ||
      ContainsIgnoreCase(trimmed, "_adb-tls-pairing._tcp") ||
      ContainsIgnoreCase(trimmed, "_adb._tcp") ||
      ContainsIgnoreCase(trimmed, "._tcp")) {
    return true;
  }
  return false;
}

std::string ResolveDeviceDisplayName(
    std::optional<std::string_view> marketing_name,
    std::optional<std::string_view> device_name,
    std::optional<std::string_view> model,
    std::optional<std::string_view> brand,
    std::optional<std::string_view> manufacturer,
    std::optional<std::string_view> product,
    std::optional<std::string_view> given_name) {
  std::string accepted;
  if (TryAcceptCandidate(marketing_name, &accepted)) {
    return accepted;
  }
  if (TryAcceptCandidate(device_name, &accepted)) {
    return accepted;
  }
  if (TryAcceptCandidate(given_name, &accepted)) {
    return accepted;
  }
  if (TryAcceptCandidate(model, &accepted)) {
    auto brand_label = PreferBrandLabel(brand, manufacturer);
    bool technical_code = accepted.find(' ') == std::string::npos;
    if (brand_label.has_value() && technical_code &&
        !StartsWithIgnoreCase(accepted, *brand_label + " ") &&
        !EqualsIgnoreCase(accepted, *brand_label)) {
      return *brand_label + " " + accepted;
    }
    return accepted;
  }
  if (TryAcceptCandidate(product, &accepted)) {
    return accepted;
  }
  return std::string(Vocabulary::Pairing::kDefaultDeviceName);
}

std::string ResolvePairingDisplayName(const RawMdnsService& service,
                                      std::span<const DeviceView> devices) {
  std::string accepted;
  if (service.given_name.has_value() &&
      TryAcceptCandidate(*service.given_name, &accepted)) {
    return accepted;
  }
  if (service.serial.has_value() && !service.serial->empty()) {
    for (const auto& d : devices) {
      if (d.detail.serial.has_value() && *d.detail.serial == *service.serial) {
        return d.display_name;
      }
    }
  }
  if (service.product_model.has_value() &&
      TryAcceptCandidate(*service.product_model, &accepted)) {
    return accepted;
  }
  return std::string(Vocabulary::Pairing::kDefaultDeviceName);
}

std::string FormatAge(std::string_view template_str,
                      int64_t since_ms,
                      int64_t now_ms) {
  int64_t delta_ms = now_ms - since_ms;
  if (delta_ms < 0) {
    delta_ms = 0;
  }
  int64_t secs = delta_ms / 1000;
  std::string age;
  if (secs < 5) {
    age = "Just now";
  } else if (secs < 60) {
    age = std::to_string(secs) + "s ago";
  } else if (secs < 3600) {
    int64_t mins = secs / 60;
    if (mins == 2 && secs % 60 == 0) {
      age = "2 minutes ago";
    } else {
      age = std::to_string(mins) + "m ago";
    }
  } else {
    int64_t hours = secs / 3600;
    age = std::to_string(hours) + "h ago";
  }

  std::string out(template_str);
  constexpr std::string_view kToken = "{age}";
  size_t pos = out.find(kToken);
  if (pos != std::string::npos) {
    out.replace(pos, kToken.size(), age);
  }
  return out;
}

std::string SnapshotToJson(const AppSnapshot& s) {
  std::string out;
  out.reserve(512);
  out.append("{\"revision\":");
  out.append(std::to_string(s.revision));
  out.append(",\"generatedAtMs\":");
  out.append(std::to_string(s.generated_at_ms));
  out.append(",\"health\":{\"kind\":");
  AppendJsonEscaped(&out, GlobalHealthKindName(s.health.kind));
  out.append(",\"title\":");
  AppendJsonOptionalString(&out, s.health.title);
  out.append(",\"detail\":");
  AppendJsonOptionalString(&out, s.health.detail);
  out.append(",\"hint\":");
  AppendJsonOptionalString(&out, s.health.hint);
  out.append(",\"tray\":");
  AppendJsonEscaped(&out, TrayStatusKindName(s.health.tray));
  out.append(",\"trayTooltip\":");
  AppendJsonEscaped(&out, s.health.tray_tooltip);
  out.append("},\"shell\":{\"pinned\":");
  out.append(s.shell.pinned ? "true" : "false");
  out.append(",\"theme\":");
  AppendJsonEscaped(&out, s.shell.theme == ThemeKind::Dark ? "Dark" : "Light");
  out.append(",\"reducedMotion\":");
  out.append(s.shell.reduced_motion ? "true" : "false");
  out.append("},\"deviceCountLabel\":");
  AppendJsonOptionalString(&out, s.device_count_label);
  out.append(",\"devices\":[");
  for (size_t i = 0; i < s.devices.size(); ++i) {
    if (i > 0) {
      out.push_back(',');
    }
    const auto& d = s.devices[i];
    out.append("{\"id\":");
    AppendJsonEscaped(&out, d.id);
    out.append(",\"displayName\":");
    AppendJsonEscaped(&out, d.display_name);
    out.append(",\"state\":");
    AppendJsonEscaped(&out, DeviceStateKindName(d.state));
    out.append(",\"stateLabel\":");
    AppendJsonEscaped(&out, d.state_label);
    out.append(",\"statusLine\":");
    AppendJsonEscaped(&out, d.status_line);
    out.append(",\"guidance\":");
    AppendJsonOptionalString(&out, d.guidance);
    out.append(",\"transport\":");
    AppendJsonEscaped(&out, TransportKindName(d.transport));
    out.append(",\"transportLabel\":");
    AppendJsonEscaped(&out, d.transport_label);
    out.append(",\"batteryPercent\":");
    if (d.battery_percent.has_value()) {
      out.append(std::to_string(*d.battery_percent));
    } else {
      out.append("null");
    }
    out.append(",\"serial\":");
    AppendJsonOptionalString(&out, d.detail.serial);
    out.append(",\"exactModel\":");
    AppendJsonOptionalString(&out, d.detail.exact_model);
    out.append(",\"androidVersion\":");
    AppendJsonOptionalString(&out, d.detail.android_version);
    out.append(",\"address\":");
    AppendJsonOptionalString(&out, d.detail.address);
    out.push_back('}');
  }
  out.append("],\"pairingOffers\":[");
  for (size_t i = 0; i < s.pairing_offers.size(); ++i) {
    if (i > 0) {
      out.push_back(',');
    }
    const auto& o = s.pairing_offers[i];
    out.append("{\"id\":");
    AppendJsonEscaped(&out, o.id);
    out.append(",\"displayName\":");
    AppendJsonEscaped(&out, o.display_name);
    out.append(",\"heading\":");
    AppendJsonEscaped(&out, o.heading);
    out.append(",\"detail\":");
    AppendJsonEscaped(&out, o.detail);
    out.push_back('}');
  }
  out.append("]}");
  return out;
}

}  // namespace adbhawk::core
