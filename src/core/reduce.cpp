#include "core/reduce.hpp"

#include <algorithm>
#include <cctype>
#include <string>
#include <unordered_set>

namespace adbhawk::core {

namespace {

constexpr int kMaxTooltipLength = 127;

bool EqualsIgnoreCaseAscii(std::string_view a, std::string_view b) {
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

bool ContainsIgnoreCaseAscii(std::string_view haystack,
                             std::string_view needle) {
  if (needle.empty()) {
    return true;
  }
  if (haystack.size() < needle.size()) {
    return false;
  }
  for (size_t i = 0; i + needle.size() <= haystack.size(); ++i) {
    if (EqualsIgnoreCaseAscii(haystack.substr(i, needle.size()), needle)) {
      return true;
    }
  }
  return false;
}

std::string TruncateAtWordBoundary(std::string_view text,
                                   size_t max_len = kMaxTooltipLength) {
  if (text.size() <= max_len) {
    return std::string(text);
  }
  std::string_view slice = text.substr(0, max_len + 1);
  size_t cut = slice.rfind(' ');
  if (cut == std::string_view::npos || cut == 0) {
    return std::string(text.substr(0, max_len));
  }
  std::string_view prefix = text.substr(0, cut);
  while (!prefix.empty() && prefix.back() == ' ') {
    prefix.remove_suffix(1);
  }
  return std::string(prefix);
}

bool IsSpecialState(DeviceStateKind st) {
  return st == DeviceStateKind::Recovery || st == DeviceStateKind::Bootloader ||
         st == DeviceStateKind::Sideload || st == DeviceStateKind::Rescue;
}

bool IsAttentionState(DeviceStateKind st) {
  return st == DeviceStateKind::Unauthorized ||
         st == DeviceStateKind::UsbAccessDenied;
}

const DeviceView* SelectTraySubject(TrayStatusKind tray,
                                    std::span<const DeviceView> devices) {
  if (devices.empty()) {
    return nullptr;
  }
  switch (tray) {
    case TrayStatusKind::Attention:
      for (const auto& d : devices) {
        if (IsAttentionState(d.state)) {
          return &d;
        }
      }
      for (const auto& d : devices) {
        if (d.state == DeviceStateKind::Offline) {
          return &d;
        }
      }
      return &devices[0];
    case TrayStatusKind::Special:
      for (const auto& d : devices) {
        if (IsSpecialState(d.state)) {
          return &d;
        }
      }
      return &devices[0];
    case TrayStatusKind::Connected:
      for (const auto& d : devices) {
        if (d.state == DeviceStateKind::Connected) {
          return &d;
        }
      }
      return &devices[0];
    default:
      return &devices[0];
  }
}

std::string BuildTrayTooltip(
    TrayStatusKind tray,
    std::span<const DeviceView> devices,
    std::span<const PairingOfferView> pairing_offers) {
  size_t count = devices.size();
  if (tray == TrayStatusKind::PairingAvailable && !pairing_offers.empty()) {
    const std::string& offer_name = pairing_offers[0].display_name;
    std::string tip = (count >= 2)
                          ? ("Pairing available: " + offer_name + " · " +
                             std::to_string(count) + " devices")
                          : ("Pairing available: " + offer_name);
    return TruncateAtWordBoundary(tip);
  }

  const DeviceView* subject = SelectTraySubject(tray, devices);
  if (subject == nullptr) {
    return std::string(Vocabulary::Health::kNoDevicesTrayTooltip);
  }

  std::string label = subject->state_label;
  if (tray == TrayStatusKind::Connected) {
    label = std::string(Vocabulary::Device::kConnectedStateLabel);
  }

  if (count <= 1) {
    if (tray == TrayStatusKind::Connected) {
      return TruncateAtWordBoundary("Connected: " + subject->display_name +
                                    " (" + subject->transport_label + ")");
    }
    return TruncateAtWordBoundary(label + ": " + subject->display_name);
  }
  return TruncateAtWordBoundary(label + ": " + subject->display_name + " · " +
                                std::to_string(count) + " devices");
}

struct NormalizedDevice {
  std::string id;
  std::string hardware_serial;
  std::string adb_serial;
  TransportKind transport = TransportKind::Unknown;
  DeviceStateKind state = DeviceStateKind::Unknown;
  std::string display_name;
  std::optional<std::string> product;
  std::optional<std::string> model;
  std::optional<std::string> device;
};

std::string StripDnsSdServiceSuffix(std::string_view serial) {
  while (!serial.empty() &&
         std::isspace(static_cast<unsigned char>(serial.back())) != 0) {
    serial.remove_suffix(1);
  }
  constexpr std::string_view kSuffixes[] = {
      "._adb-tls-connect._tcp",
      "._adb-tls-pairing._tcp",
      "._adb._tcp",
  };
  for (std::string_view sfx : kSuffixes) {
    if (serial.size() > sfx.size() &&
        EqualsIgnoreCaseAscii(serial.substr(serial.size() - sfx.size()), sfx)) {
      return std::string(serial.substr(0, serial.size() - sfx.size()));
    }
  }
  return std::string(serial);
}

std::optional<std::string> ExtractSerialFromInstanceName(
    std::string_view instance_name) {
  if (instance_name.size() <= 4 || instance_name.substr(0, 4) != "adb-") {
    return std::nullopt;
  }
  std::string_view rest = instance_name.substr(4);
  // Strict form: adb-<serial>-<6 random alphanum>
  if (rest.size() > 7 && rest[rest.size() - 7] == '-') {
    bool alnum = true;
    for (size_t i = rest.size() - 6; i < rest.size(); ++i) {
      if (std::isalnum(static_cast<unsigned char>(rest[i])) == 0) {
        alnum = false;
        break;
      }
    }
    if (alnum) {
      return std::string(rest.substr(0, rest.size() - 7));
    }
  }
  // Loose form: adb-<serial>-<anything>
  size_t dash = rest.find('-');
  if (dash != std::string_view::npos && dash > 0) {
    return std::string(rest.substr(0, dash));
  }
  return std::nullopt;
}

bool ServiceHostMatches(const RawMdnsService& service, std::string_view host) {
  if (service.ipv4.has_value() && EqualsIgnoreCaseAscii(*service.ipv4, host)) {
    return true;
  }
  for (const auto& v6 : service.ipv6) {
    if (EqualsIgnoreCaseAscii(v6, host)) {
      return true;
    }
  }
  return false;
}

bool EndpointMatchesService(std::string_view endpoint_serial,
                            const RawMdnsService& service) {
  std::string host;
  int port = 0;
  if (!TrySplitHostPort(endpoint_serial, &host, &port)) {
    return false;
  }
  if (service.port != port) {
    return false;
  }
  return ServiceHostMatches(service, host);
}

std::optional<std::string> FindConnectSerial(
    std::string_view endpoint_serial,
    std::span<const RawMdnsService> services) {
  for (const auto& svc : services) {
    if (svc.kind != MdnsServiceKind::Connect ||
        svc.service_type != "_adb-tls-connect._tcp") {
      continue;
    }
    if (EndpointMatchesService(endpoint_serial, svc)) {
      if (svc.serial.has_value() && !svc.serial->empty()) {
        return *svc.serial;
      }
      auto from_inst = ExtractSerialFromInstanceName(svc.instance_name);
      if (from_inst.has_value() && !from_inst->empty()) {
        return from_inst;
      }
    }
  }

  std::string host;
  int port = 0;
  if (!TrySplitHostPort(endpoint_serial, &host, &port)) {
    return std::nullopt;
  }
  for (const auto& svc : services) {
    if (svc.kind != MdnsServiceKind::Connect ||
        svc.service_type != "_adb-tls-connect._tcp") {
      continue;
    }
    if (!ServiceHostMatches(svc, host)) {
      continue;
    }
    if (svc.serial.has_value() && !svc.serial->empty()) {
      return *svc.serial;
    }
    auto from_inst = ExtractSerialFromInstanceName(svc.instance_name);
    if (from_inst.has_value() && !from_inst->empty()) {
      return from_inst;
    }
  }
  return std::nullopt;
}

std::optional<std::string> FindConnectGivenName(
    std::string_view endpoint_or_serial,
    std::string_view hardware_serial,
    std::span<const RawMdnsService> services) {
  for (const auto& svc : services) {
    if (svc.kind != MdnsServiceKind::Connect ||
        svc.service_type != "_adb-tls-connect._tcp") {
      continue;
    }
    bool match = EndpointMatchesService(endpoint_or_serial, svc) ||
                 (svc.serial.has_value() && *svc.serial == hardware_serial);
    if (match && svc.given_name.has_value() && !svc.given_name->empty()) {
      return *svc.given_name;
    }
  }
  return std::nullopt;
}

std::string ResolveHardwareSerial(std::string_view raw_serial,
                                  std::span<const RawMdnsService> services) {
  std::string candidate = StripDnsSdServiceSuffix(raw_serial);
  auto extracted = ExtractSerialFromInstanceName(candidate);
  if (extracted.has_value() && !extracted->empty()) {
    return *extracted;
  }
  std::string host;
  int port = 0;
  if (TrySplitHostPort(raw_serial, &host, &port)) {
    auto from_connect = FindConnectSerial(raw_serial, services);
    if (from_connect.has_value() && !from_connect->empty()) {
      return *from_connect;
    }
    return std::string(raw_serial);
  }
  return candidate;
}

TransportKind MapTransport(std::optional<int> connection_type) {
  if (!connection_type.has_value()) {
    return TransportKind::Unknown;
  }
  switch (*connection_type) {
    case 1:
      return TransportKind::Usb;
    case 2:
      return TransportKind::WiFi;
    default:
      return TransportKind::Unknown;
  }
}

DeviceStateKind MapRawDeviceState(const RawDevice& raw) {
  if (raw.is_proto_binary) {
    if (!raw.proto_state.has_value()) {
      return DeviceStateKind::Unknown;
    }
    switch (*raw.proto_state) {
      case 1:
        return DeviceStateKind::Connecting;
      case 2:
        return DeviceStateKind::Authorizing;
      case 3:
        return DeviceStateKind::Unauthorized;
      case 4:  // StateNoPermission -> Linux D12 UsbAccessDenied!
        return DeviceStateKind::UsbAccessDenied;
      case 5:
        return DeviceStateKind::Detached;
      case 6:
        return DeviceStateKind::Offline;
      case 7:
        return DeviceStateKind::Bootloader;
      case 8:
        return DeviceStateKind::Connected;
      case 10:
        return DeviceStateKind::Recovery;
      case 11:
        return DeviceStateKind::Sideload;
      case 12:
        return DeviceStateKind::Rescue;
      default:
        return DeviceStateKind::Unknown;
    }
  }

  if (!raw.state_token.has_value() || raw.state_token->empty()) {
    return DeviceStateKind::Unknown;
  }
  const std::string& st = *raw.state_token;
  if (st == "device") return DeviceStateKind::Connected;
  if (st == "unauthorized") return DeviceStateKind::Unauthorized;
  if (st == "no permissions") return DeviceStateKind::UsbAccessDenied;
  if (st == "offline") return DeviceStateKind::Offline;
  if (st == "recovery") return DeviceStateKind::Recovery;
  if (st == "bootloader") return DeviceStateKind::Bootloader;
  if (st == "sideload") return DeviceStateKind::Sideload;
  if (st == "rescue") return DeviceStateKind::Rescue;
  if (st == "authorizing") return DeviceStateKind::Authorizing;
  if (st == "connecting") return DeviceStateKind::Connecting;
  if (st == "detached") return DeviceStateKind::Detached;
  return DeviceStateKind::Unknown;
}

std::string BuildDeviceId(TransportKind transport,
                          std::string_view hardware_serial,
                          std::optional<std::string_view> usb_devpath) {
  std::string prefix;
  switch (transport) {
    case TransportKind::Usb:
      prefix = "usb:";
      break;
    case TransportKind::WiFi:
      prefix = "wifi:";
      break;
    case TransportKind::Unknown:
      prefix = "unk:";
      break;
  }
  if (hardware_serial == "(no serial number)" && usb_devpath.has_value() &&
      !usb_devpath->empty()) {
    return prefix + std::string(*usb_devpath);
  }
  return prefix + std::string(hardware_serial);
}

std::vector<NormalizedDevice> SelectNormalizedTransports(
    std::span<const RawDevice> raw_devices,
    std::span<const RawMdnsService> mdns_services) {
  std::vector<NormalizedDevice> out;
  std::unordered_map<std::string, size_t> index_by_id;

  for (const auto& raw : raw_devices) {
    TransportKind transport = MapTransport(raw.connection_type);
    std::string hw_serial = ResolveHardwareSerial(raw.serial, mdns_services);
    std::optional<std::string_view> usb_path =
        raw.usb_devpath.has_value()
            ? std::optional<std::string_view>(*raw.usb_devpath)
            : std::nullopt;
    std::string id = BuildDeviceId(transport, hw_serial, usb_path);
    DeviceStateKind st = MapRawDeviceState(raw);
    auto given = FindConnectGivenName(raw.serial, hw_serial, mdns_services);
    std::string display_name = ResolveDeviceDisplayName(
        /*marketing_name=*/std::nullopt,
        /*device_name=*/std::nullopt,
        /*model=*/raw.model.has_value()
            ? std::optional<std::string_view>(*raw.model)
            : std::nullopt,
        /*brand=*/std::nullopt,
        /*manufacturer=*/std::nullopt,
        /*product=*/raw.product.has_value()
            ? std::optional<std::string_view>(*raw.product)
            : std::nullopt,
        /*given_name=*/given.has_value()
            ? std::optional<std::string_view>(*given)
            : std::nullopt);

    NormalizedDevice norm{
        .id = std::move(id),
        .hardware_serial = std::move(hw_serial),
        .adb_serial = raw.serial,
        .transport = transport,
        .state = st,
        .display_name = std::move(display_name),
        .product = raw.product,
        .model = raw.model,
        .device = raw.device,
    };

    auto it = index_by_id.find(norm.id);
    if (it == index_by_id.end()) {
      index_by_id.emplace(norm.id, out.size());
      out.push_back(std::move(norm));
    } else {
      auto& existing = out[it->second];
      if (norm.state == DeviceStateKind::Connected &&
          existing.state != DeviceStateKind::Connected) {
        existing = std::move(norm);
      }
      // If one transport has host:port adb_serial (the actual socket endpoint)
      // and the other has a DNS-SD instance name (adb-<serial>...), ensure
      // existing retains the socket endpoint so address & copy actions work.
      std::string h1, h2;
      int p1 = 0, p2 = 0;
      if (!TrySplitHostPort(existing.adb_serial, &h1, &p1) &&
          TrySplitHostPort(norm.adb_serial, &h2, &p2)) {
        existing.adb_serial = norm.adb_serial;
      }
    }
  }
  return out;
}

std::string_view TransportLabelFor(TransportKind t) {
  switch (t) {
    case TransportKind::Usb:
      return Vocabulary::Device::kTransportUsb;
    case TransportKind::WiFi:
      return Vocabulary::Device::kTransportWifi;
    case TransportKind::Unknown:
      return Vocabulary::Device::kTransportUnknown;
  }
  return Vocabulary::Device::kTransportUnknown;
}

std::string_view StateLabelFor(DeviceStateKind st) {
  switch (st) {
    case DeviceStateKind::Connected:
      return Vocabulary::Device::kConnectedStateLabel;
    case DeviceStateKind::Unauthorized:
      return Vocabulary::Device::kUnauthorizedStateLabel;
    case DeviceStateKind::UsbAccessDenied:
      return Vocabulary::Device::kUsbAccessDeniedStateLabel;
    case DeviceStateKind::Offline:
      return Vocabulary::Device::kOfflineStateLabel;
    case DeviceStateKind::Recovery:
      return Vocabulary::Device::kRecoveryStateLabel;
    case DeviceStateKind::Bootloader:
      return Vocabulary::Device::kBootloaderStateLabel;
    case DeviceStateKind::Sideload:
      return Vocabulary::Device::kSideloadStateLabel;
    case DeviceStateKind::Rescue:
      return Vocabulary::Device::kRescueStateLabel;
    case DeviceStateKind::Authorizing:
      return Vocabulary::Device::kAuthorizingStateLabel;
    case DeviceStateKind::Connecting:
      return Vocabulary::Device::kConnectingStateLabel;
    case DeviceStateKind::Detached:
      return Vocabulary::Device::kDetachedStateLabel;
    case DeviceStateKind::Unknown:
      return Vocabulary::Device::kUnknownStateLabel;
  }
  return Vocabulary::Device::kUnknownStateLabel;
}

SeverityKind SeverityFor(DeviceStateKind st) {
  switch (st) {
    case DeviceStateKind::Connected:
      return SeverityKind::Positive;
    case DeviceStateKind::Unauthorized:
    case DeviceStateKind::UsbAccessDenied:
    case DeviceStateKind::Authorizing:
      return SeverityKind::Attention;
    case DeviceStateKind::Offline:
      return SeverityKind::Failure;
    case DeviceStateKind::Recovery:
    case DeviceStateKind::Bootloader:
    case DeviceStateKind::Sideload:
    case DeviceStateKind::Rescue:
    case DeviceStateKind::Connecting:
      return SeverityKind::Informational;
    case DeviceStateKind::Detached:
    case DeviceStateKind::Unknown:
      return SeverityKind::Neutral;
  }
  return SeverityKind::Neutral;
}

std::optional<std::string> GuidanceFor(DeviceStateKind st) {
  switch (st) {
    case DeviceStateKind::Unauthorized:
      return std::string(Vocabulary::Device::kUnauthorizedGuidance);
    case DeviceStateKind::UsbAccessDenied:
      return std::string(Vocabulary::Device::kUsbAccessDeniedGuidance);
    case DeviceStateKind::Offline:
      return std::string(Vocabulary::Device::kOfflineGuidance);
    default:
      return std::nullopt;
  }
}

std::optional<std::string> ResolveAddressField(
    TransportKind transport,
    std::string_view adb_serial,
    std::string_view hardware_serial,
    std::span<const RawMdnsService> services = {}) {
  if (transport != TransportKind::WiFi) {
    return std::nullopt;
  }
  std::string host;
  int port = 0;
  if (TrySplitHostPort(adb_serial, &host, &port)) {
    return std::string(adb_serial);
  }
  if (TrySplitHostPort(hardware_serial, &host, &port)) {
    return std::string(hardware_serial);
  }
  for (const auto& svc : services) {
    if (svc.kind == MdnsServiceKind::Connect &&
        svc.service_type == "_adb-tls-connect._tcp" &&
        svc.ipv4.has_value() && svc.port > 0) {
      if ((svc.serial.has_value() && *svc.serial == hardware_serial) ||
          ExtractSerialFromInstanceName(svc.instance_name) == hardware_serial) {
        return *svc.ipv4 + ":" + std::to_string(svc.port);
      }
    }
  }
  return std::nullopt;
}

std::optional<std::string> ResolveDisplaySerialField(
    std::string_view hardware_serial) {
  if (hardware_serial.empty() || hardware_serial == "(no serial number)") {
    return std::nullopt;
  }
  std::string host;
  int port = 0;
  if (TrySplitHostPort(hardware_serial, &host, &port)) {
    return std::nullopt;
  }
  if (ContainsIgnoreCaseAscii(hardware_serial, "_adb-tls-") ||
      ContainsIgnoreCaseAscii(hardware_serial, "._tcp") ||
      ContainsIgnoreCaseAscii(hardware_serial, "_adb._tcp") ||
      (hardware_serial.size() >= 4 &&
       EqualsIgnoreCaseAscii(hardware_serial.substr(0, 4), "adb-"))) {
    return std::nullopt;
  }
  return std::string(hardware_serial);
}

int TransportSortRank(TransportKind t) {
  switch (t) {
    case TransportKind::Usb:
      return 0;
    case TransportKind::WiFi:
      return 1;
    case TransportKind::Unknown:
      return 2;
  }
  return 2;
}

std::vector<DeviceView> OrderDevices(std::vector<DeviceView> devices) {
  std::sort(devices.begin(), devices.end(),
            [](const DeviceView& a, const DeviceView& b) {
              int ra = TransportSortRank(a.transport);
              int rb = TransportSortRank(b.transport);
              if (ra != rb) {
                return ra < rb;
              }
              int64_t ta = a.connected_since_ms.value_or(a.state_since_ms);
              int64_t tb = b.connected_since_ms.value_or(b.state_since_ms);
              if (ta != tb) {
                return ta < tb;
              }
              std::string_view sa = a.detail.serial.has_value()
                                        ? std::string_view(*a.detail.serial)
                                        : (a.detail.address.has_value()
                                               ? std::string_view(*a.detail.address)
                                               : std::string_view{});
              std::string_view sb = b.detail.serial.has_value()
                                        ? std::string_view(*b.detail.serial)
                                        : (b.detail.address.has_value()
                                               ? std::string_view(*b.detail.address)
                                               : std::string_view{});
              if (sa != sb) {
                return sa < sb;
              }
              return a.id < b.id;
            });
  return devices;
}

bool IsDeviceConnectedOnWifi(const std::string& host,
                             const std::optional<std::string>& serial,
                             std::span<const DeviceView> devices) {
  for (const auto& d : devices) {
    if (d.transport != TransportKind::WiFi) {
      continue;
    }
    // Match by serial
    if (serial.has_value() && !serial->empty()) {
      if ((d.detail.serial.has_value() && *d.detail.serial == *serial) ||
          d.id == ("wifi:" + *serial) || d.id == *serial) {
        return true;
      }
    }
    // Match by IP address
    if (d.detail.address.has_value() && !host.empty()) {
      std::string d_host;
      int d_port = 0;
      if (TrySplitHostPort(*d.detail.address, &d_host, &d_port) &&
          EqualsIgnoreCaseAscii(d_host, host)) {
        return true;
      }
    }
  }
  return false;
}

std::pair<std::vector<PairingOfferView>,
          std::unordered_map<std::string, PairingOfferBinding>>
MapPairingOffers(std::span<const RawMdnsService> services,
                 std::span<const DeviceView> devices,
                 const std::optional<std::string>& active_pairing_offer_id = std::nullopt,
                 const std::optional<std::string>& active_pairing_host = std::nullopt) {
  std::vector<PairingOfferView> offers;
  std::unordered_map<std::string, PairingOfferBinding> bindings;
  std::unordered_set<std::string> seen;

  for (const auto& svc : services) {
    if (svc.kind != MdnsServiceKind::Pairing ||
        svc.service_type != "_adb-tls-pairing._tcp" || svc.port <= 0 ||
        svc.port > 65535) {
      continue;
    }
    std::string host;
    if (svc.ipv4.has_value() && !svc.ipv4->empty()) {
      host = *svc.ipv4;
    } else if (!svc.ipv6.empty() && !svc.ipv6[0].empty()) {
      host = svc.ipv6[0];
    } else {
      continue;
    }

    std::string id = svc.DiscoveryKey();
    if (!seen.insert(id).second) {
      continue;
    }

    std::string display_name = ResolvePairingDisplayName(svc, devices);
    bindings.emplace(
        id, PairingOfferBinding{
                .offer_id = id,
                .host = host,
                .port = svc.port,
                .serial = (svc.serial.has_value() && !svc.serial->empty())
                              ? svc.serial
                              : std::nullopt,
                .display_name = display_name,
            });

    // Suppress offer if device is currently being paired, waiting for post-pair connect, or already connected
    if (active_pairing_offer_id.has_value() && !active_pairing_offer_id->empty() &&
        *active_pairing_offer_id == id) {
      continue;
    }
    if (active_pairing_host.has_value() && !active_pairing_host->empty() &&
        EqualsIgnoreCaseAscii(*active_pairing_host, host)) {
      continue;
    }
    if (IsDeviceConnectedOnWifi(host, svc.serial, devices)) {
      continue;
    }

    std::string host_port = host + ":" + std::to_string(svc.port);
    offers.push_back(PairingOfferView{
        .id = id,
        .display_name = display_name,
        .heading = std::string(Vocabulary::Pairing::kOfferHeading),
        .detail = std::string(Vocabulary::Pairing::kOfferDetail),
        .action_label = std::string(Vocabulary::Pairing::kActionLabel),
        .host_port = std::move(host_port),
    });
  }
  return {std::move(offers), std::move(bindings)};
}

std::optional<PairingOperationView> MapPairingOperation(
    const std::string& offer_id,
    PairingPhase phase,
    PairingFailureKind failure_kind,
    const std::string& display_name) {
  switch (phase) {
    case PairingPhase::Cleared:
      return std::nullopt;
    case PairingPhase::AwaitingCode:
      return PairingOperationView{
          .offer_id = offer_id,
          .display_name = display_name,
          .phase = PairingPhase::AwaitingCode,
          .title = Vocabulary::Pairing::CodeEntryTitle(display_name),
          .detail = std::string(Vocabulary::Pairing::kCodeEntryDetail),
          .can_submit_code = true,
          .can_cancel = true,
          .can_retry = false,
          .code_length = 6,
      };
    case PairingPhase::Pairing:
      return PairingOperationView{
          .offer_id = offer_id,
          .display_name = display_name,
          .phase = PairingPhase::Pairing,
          .title = Vocabulary::Pairing::ProgressTitle(display_name),
          .detail = std::string(Vocabulary::Pairing::kProgressDetail),
          .can_submit_code = false,
          .can_cancel = true,
          .can_retry = false,
          .code_length = 6,
      };
    case PairingPhase::PairedWaitingForDevice:
      return PairingOperationView{
          .offer_id = offer_id,
          .display_name = display_name,
          .phase = PairingPhase::PairedWaitingForDevice,
          .title = Vocabulary::Pairing::SuccessTitle(display_name),
          .detail = std::string(Vocabulary::Pairing::kSuccessDetail),
          .can_submit_code = false,
          .can_cancel = true,
          .can_retry = false,
          .code_length = 6,
      };
    case PairingPhase::Failed:
      return PairingOperationView{
          .offer_id = offer_id,
          .display_name = display_name,
          .phase = PairingPhase::Failed,
          .title = std::string(Vocabulary::Pairing::kFailedTitle),
          .detail = Vocabulary::Pairing::FailedDetailFor(failure_kind),
          .can_submit_code = false,
          .can_cancel = true,
          .can_retry = true,
          .code_length = 6,
      };
  }
  return std::nullopt;
}

HealthView WithDeviceDerivedTray(
    HealthView health,
    std::span<const DeviceView> devices,
    std::span<const PairingOfferView> pairing_offers) {
  if (health.kind == GlobalHealthKind::AdbUnavailable) {
    health.tray = TrayStatusKind::Failure;
    health.tray_tooltip =
        std::string(Vocabulary::Health::kAdbUnavailableTrayTooltip);
    return health;
  }
  auto [tray, tooltip] =
      AggregateTrayStatus(health.kind, devices, pairing_offers);
  health.tray = tray;
  health.tray_tooltip = std::move(tooltip);
  return health;
}

HealthView MapPhaseToHealth(AdbServerPhase phase, const AppSnapshot& current) {
  if (phase == AdbServerPhase::Unavailable) {
    return HealthView{
        .kind = GlobalHealthKind::AdbUnavailable,
        .title = std::string(Vocabulary::Health::kAdbUnavailableTitle),
        .detail = std::string(Vocabulary::Health::kAdbUnavailableDetail),
        .hint = std::string(Vocabulary::Health::kAdbUnavailableHint),
        .tray = TrayStatusKind::Failure,
        .tray_tooltip =
            std::string(Vocabulary::Health::kAdbUnavailableTrayTooltip),
    };
  }
  bool restoring = (phase == AdbServerPhase::Probing ||
                    phase == AdbServerPhase::Starting ||
                    phase == AdbServerPhase::Restoring);
  GlobalHealthKind kind = restoring ? GlobalHealthKind::AdbRestoring
                                    : GlobalHealthKind::Healthy;
  auto [tray, tooltip] =
      AggregateTrayStatus(kind, current.devices, current.pairing_offers);
  return HealthView{
      .kind = kind,
      .title = restoring
                   ? std::optional<std::string>(
                         Vocabulary::Health::kAdbRestoringTitle)
                   : std::nullopt,
      .detail = restoring
                    ? std::optional<std::string>(
                          Vocabulary::Health::kAdbRestoringDetail)
                    : std::nullopt,
      .hint = std::nullopt,
      .tray = tray,
      .tray_tooltip = std::move(tooltip),
  };
}

bool IsStaleSequence(int64_t seq,
                     int64_t latest_seq,
                     int64_t at_ms,
                     std::optional<int64_t> latest_at_ms) {
  if (seq > 0) {
    return seq <= latest_seq;
  }
  return latest_at_ms.has_value() && at_ms < *latest_at_ms;
}

bool MatchesWaitingPairing(const std::vector<DeviceView>& devices,
                           const DeviceEngineState& state) {
  for (const auto& d : devices) {
    if (d.transport != TransportKind::WiFi) {
      continue;
    }
    if (state.active_pairing_serial.has_value() &&
        !state.active_pairing_serial->empty()) {
      if ((d.detail.serial.has_value() &&
           *d.detail.serial == *state.active_pairing_serial) ||
          d.id == ("wifi:" + *state.active_pairing_serial)) {
        return true;
      }
    }
    if (state.active_pairing_host.has_value() &&
        !state.active_pairing_host->empty() && d.detail.address.has_value()) {
      std::string host;
      int port = 0;
      if (TrySplitHostPort(*d.detail.address, &host, &port) &&
          EqualsIgnoreCaseAscii(host, *state.active_pairing_host)) {
        return true;
      }
    }
  }
  return false;
}

bool IsDiscoveryBroken(const AppSnapshot& snapshot,
                       const DeviceEngineState& state) {
  if (snapshot.health.kind != GlobalHealthKind::Healthy) {
    return false;
  }
  if (state.server_mdns_enabled != true || state.wireless_failed) {
    return false;
  }
  if (!state.latest_applied_mdns_at_ms.has_value()) {
    return false;
  }
  if (!state.latest_mdns_services.empty()) {
    return false;
  }
  for (const auto& d : snapshot.devices) {
    if (d.transport == TransportKind::WiFi &&
        d.state == DeviceStateKind::Connected) {
      return true;
    }
  }
  return false;
}

DiscoveryHealthView BuildDiscoveryHealthView(const AppSnapshot& snapshot,
                                             const DeviceEngineState& state) {
  std::string device_name(Vocabulary::Pairing::kDefaultDeviceName);
  for (const auto& d : snapshot.devices) {
    if (d.transport == TransportKind::WiFi &&
        d.state == DeviceStateKind::Connected) {
      device_name = d.display_name;
      break;
    }
  }

  std::vector<std::string> evidence;
  if (state.latest_discovery_diagnosis.has_value()) {
    const auto& diag = *state.latest_discovery_diagnosis;
    switch (diag.probe_outcome) {
      case DiscoveryProbeOutcome::Measured:
        if (diag.announcement_count > 0) {
          evidence.push_back(
              Vocabulary::Discovery::MeasuredEvidence(diag.announcement_count));
        } else {
          evidence.push_back(
              std::string(Vocabulary::Discovery::kMeasuredNothingEvidence));
        }
        break;
      case DiscoveryProbeOutcome::Unavailable:
        evidence.push_back(
            std::string(Vocabulary::Discovery::kProbeUnavailableEvidence));
        break;
      case DiscoveryProbeOutcome::NotRun:
        break;
    }

    if (diag.port_holders.size() == 1) {
      evidence.push_back(
          Vocabulary::Discovery::PortHeldEvidence(diag.port_holders[0]));
    } else if (diag.port_holders.size() > 1) {
      evidence.push_back(Vocabulary::Discovery::PortHoldersEvidence(
          diag.port_holders[0],
          static_cast<int>(diag.port_holders.size() - 1)));
    }

    if (diag.virtual_adapters.size() == 1) {
      evidence.push_back(Vocabulary::Discovery::VirtualAdaptersNamedEvidence(
          diag.virtual_adapters[0]));
    } else if (diag.virtual_adapters.size() > 1) {
      evidence.push_back(Vocabulary::Discovery::VirtualAdaptersNamedEvidence(
          diag.virtual_adapters[0], diag.virtual_adapters[1]));
    }
  }

  std::vector<DiscoveryRepairCardAction> repairs;
  for (const auto& desc : state.repair_catalog) {
    auto it = state.repair_results.find(desc.id);
    if (it != state.repair_results.end() &&
        it->second == DiscoveryRepairOutcome::Completed) {
      continue;
    }
    bool running = (state.running_repair_id.has_value() &&
                    *state.running_repair_id == desc.id);
    repairs.push_back(DiscoveryRepairCardAction{
        .id = desc.id,
        .label = Vocabulary::Discovery::RepairLabel(desc.kind),
        .is_admin = false,
        .is_running = running,
        .running_label = Vocabulary::Discovery::RepairRunningLabel(desc.kind),
    });
  }

  return DiscoveryHealthView{
      .title = std::string(Vocabulary::Discovery::kBrokenTitle),
      .detail = Vocabulary::Discovery::BrokenDetail(device_name),
      .evidence = std::move(evidence),
      .action_label =
          std::string(Vocabulary::Discovery::kCheckAgainActionLabel),
      .can_check_again = !state.discovery_check_running &&
                         !state.running_repair_id.has_value(),
      .repairs = std::move(repairs),
      .result_line = state.repair_result_line,
  };
}

void ApplyDiscoveryHealth(ReduceResult* res, int64_t now_ms) {
  if (!IsDiscoveryBroken(res->snapshot, res->device_state)) {
    if (!res->snapshot.discovery_health.has_value() &&
        !res->device_state.latest_discovery_diagnosis.has_value() &&
        !res->device_state.discovery_check_running &&
        res->device_state.repair_catalog.empty() &&
        !res->device_state.running_repair_id.has_value() &&
        res->device_state.repair_results.empty() &&
        !res->device_state.repair_result_line.has_value()) {
      return;
    }
    res->device_state.latest_discovery_diagnosis = std::nullopt;
    res->device_state.discovery_check_running = false;
    res->device_state.repair_catalog.clear();
    res->device_state.repair_results.clear();
    res->device_state.running_repair_id = std::nullopt;
    res->device_state.awaiting_measure_repair_id = std::nullopt;
    res->device_state.repair_result_line = std::nullopt;
    if (res->snapshot.discovery_health.has_value()) {
      res->snapshot.revision += 1;
      res->snapshot.generated_at_ms = now_ms;
      res->snapshot.discovery_health = std::nullopt;
    }
    return;
  }

  DiscoveryHealthView view =
      BuildDiscoveryHealthView(res->snapshot, res->device_state);
  if (res->snapshot.discovery_health.has_value() &&
      *res->snapshot.discovery_health == view) {
    return;
  }
  res->snapshot.revision += 1;
  res->snapshot.generated_at_ms = now_ms;
  res->snapshot.discovery_health = std::move(view);
}

}  // namespace

std::vector<RawMdnsService> MergeEffectiveMdnsServices(
    std::span<const RawMdnsService> adb_services,
    std::span<const RawMdnsService> fallback_services) {
  if (fallback_services.empty()) {
    return std::vector<RawMdnsService>(adb_services.begin(), adb_services.end());
  }
  if (adb_services.empty()) {
    return std::vector<RawMdnsService>(fallback_services.begin(),
                                       fallback_services.end());
  }
  std::unordered_set<std::string> keys;
  std::vector<RawMdnsService> merged;
  merged.reserve(adb_services.size() + fallback_services.size());
  for (const auto& s : adb_services) {
    keys.insert(s.DiscoveryKey());
    merged.push_back(s);
  }
  for (const auto& s : fallback_services) {
    if (keys.insert(s.DiscoveryKey()).second) {
      merged.push_back(s);
    }
  }
  return merged;
}

AppSnapshot MakeInitialSnapshot(int64_t now_ms) {
  AppSnapshot snap;
  snap.revision = 0;
  snap.generated_at_ms = now_ms;
  snap.health = HealthView{
      .kind = GlobalHealthKind::Healthy,
      .title = std::nullopt,
      .detail = std::nullopt,
      .hint = std::nullopt,
      .tray = TrayStatusKind::Idle,
      .tray_tooltip = std::string(Vocabulary::Health::kNoDevicesTrayTooltip),
  };
  snap.shell = ShellView{
      .pinned = false,
      .theme = ThemeKind::Dark,
      .transparency_enabled = false,
      .forced_colors = false,
      .reduced_motion = false,
  };
  return snap;
}

std::pair<TrayStatusKind, std::string> AggregateTrayStatus(
    GlobalHealthKind health_kind,
    std::span<const DeviceView> devices,
    std::span<const PairingOfferView> pairing_offers) {
  if (health_kind == GlobalHealthKind::AdbUnavailable) {
    return {TrayStatusKind::Failure,
            std::string(Vocabulary::Health::kAdbUnavailableTrayTooltip)};
  }
  if (devices.empty() && pairing_offers.empty()) {
    return {TrayStatusKind::Idle,
            std::string(Vocabulary::Health::kNoDevicesTrayTooltip)};
  }

  bool any_connected = false;
  bool any_attention = false;
  bool any_offline = false;
  bool any_special = false;
  for (const auto& d : devices) {
    if (d.state == DeviceStateKind::Connected) any_connected = true;
    if (IsAttentionState(d.state)) any_attention = true;
    if (d.state == DeviceStateKind::Offline) any_offline = true;
    if (IsSpecialState(d.state)) any_special = true;
  }
  bool any_pairing = !pairing_offers.empty();

  // Priority: Failure -> Attention -> PairingAvailable -> Special -> Connected -> Idle
  if (any_attention || (any_offline && !any_connected)) {
    return {TrayStatusKind::Attention,
            BuildTrayTooltip(TrayStatusKind::Attention, devices, pairing_offers)};
  }
  if (any_pairing) {
    return {TrayStatusKind::PairingAvailable,
            BuildTrayTooltip(TrayStatusKind::PairingAvailable, devices,
                             pairing_offers)};
  }
  if (any_special) {
    return {TrayStatusKind::Special,
            BuildTrayTooltip(TrayStatusKind::Special, devices, pairing_offers)};
  }
  if (any_connected) {
    return {TrayStatusKind::Connected,
            BuildTrayTooltip(TrayStatusKind::Connected, devices, pairing_offers)};
  }
  return {TrayStatusKind::Idle,
          std::string(Vocabulary::Health::kNoDevicesTrayTooltip)};
}

void ReconcileDeviceViews(
    AppSnapshot& snapshot,
    DeviceEngineState& state,
    std::span<const RawDevice> raw_devices,
    std::span<const RawMdnsService> effective_services,
    int64_t at_ms,
    int64_t now_ms,
    std::optional<int64_t> sequence,
    GraceCommand* out_grace,
    GraceCommand* out_pairing_wait) {
  std::vector<NormalizedDevice> normalized =
      SelectNormalizedTransports(raw_devices, effective_services);

  std::unordered_map<std::string, DeviceMetadataSession> next_sessions;
  int64_t counter = state.next_metadata_generation;
  for (const auto& norm : normalized) {
    bool connected = (norm.state == DeviceStateKind::Connected);
    auto it = state.metadata_sessions.find(norm.id);
    if (it == state.metadata_sessions.end()) {
      ++counter;
      next_sessions.emplace(
          norm.id, DeviceMetadataSession{counter, norm.adb_serial,
                                         connected, std::nullopt});
    } else {
      bool serial_changed = (it->second.adb_serial != norm.adb_serial);
      bool left_conn = it->second.connected_eligible && !connected;
      bool entered_conn = !it->second.connected_eligible && connected;
      if (serial_changed || left_conn || entered_conn) {
        ++counter;
        next_sessions.emplace(
            norm.id, DeviceMetadataSession{counter, norm.adb_serial,
                                           connected, std::nullopt});
      } else {
        DeviceMetadataSession kept = it->second;
        kept.adb_serial = norm.adb_serial;
        kept.connected_eligible = connected;
        next_sessions.emplace(norm.id, std::move(kept));
      }
    }
  }

  std::unordered_map<std::string, const DeviceView*> prev_by_id;
  for (const auto& d : snapshot.devices) {
    prev_by_id.emplace(d.id, &d);
  }

  std::vector<DeviceView> mapped;
  mapped.reserve(normalized.size());
  for (const auto& norm : normalized) {
    const DeviceView* prior = nullptr;
    auto pit = prev_by_id.find(norm.id);
    if (pit != prev_by_id.end()) {
      prior = pit->second;
    }
    bool keep_meta = false;
    auto oit = state.metadata_sessions.find(norm.id);
    auto nit = next_sessions.find(norm.id);
    if (oit != state.metadata_sessions.end() &&
        nit != next_sessions.end() &&
        oit->second.generation == nit->second.generation) {
      keep_meta = true;
    }

    std::string transport_label(TransportLabelFor(norm.transport));
    std::string state_label(StateLabelFor(norm.state));
    int64_t state_since = at_ms;
    std::optional<int64_t> connected_since =
        (norm.state == DeviceStateKind::Connected)
            ? std::optional<int64_t>(at_ms)
            : std::nullopt;
    if (prior != nullptr && prior->state == norm.state) {
      state_since = prior->state_since_ms;
      connected_since =
          (norm.state == DeviceStateKind::Connected)
              ? std::optional<int64_t>(
                    prior->connected_since_ms.value_or(at_ms))
              : std::nullopt;
    }

    auto address = ResolveAddressField(norm.transport, norm.adb_serial,
                                       norm.hardware_serial, effective_services);
    auto disp_serial = ResolveDisplaySerialField(norm.hardware_serial);

    std::vector<CopyFieldView> copy_fields;
    if (address.has_value() && !address->empty()) {
      copy_fields.push_back(CopyFieldView{
          .key = std::string(Vocabulary::Device::kCopyAddressKey),
          .label = std::string(Vocabulary::Device::kCopyAddressLabel),
          .value = *address,
      });
    }
    if (disp_serial.has_value() && !disp_serial->empty()) {
      copy_fields.push_back(CopyFieldView{
          .key = std::string(Vocabulary::Device::kCopySerialKey),
          .label = std::string(Vocabulary::Device::kCopySerialLabel),
          .value = *disp_serial,
      });
    }

    std::optional<int> battery =
        (keep_meta && prior != nullptr) ? prior->battery_percent
                                        : std::nullopt;
    std::optional<std::string> exact_model =
        (keep_meta && prior != nullptr) ? prior->detail.exact_model
                                        : std::nullopt;
    std::optional<std::string> android_ver =
        (keep_meta && prior != nullptr) ? prior->detail.android_version
                                        : std::nullopt;
    std::string display_name =
        (keep_meta && prior != nullptr &&
         prior->detail.exact_model.has_value())
            ? prior->display_name
            : norm.display_name;

    mapped.push_back(DeviceView{
        .id = norm.id,
        .display_name = std::move(display_name),
        .state = norm.state,
        .severity = SeverityFor(norm.state),
        .state_label = state_label,
        .status_line = state_label + " · " + transport_label,
        .guidance = GuidanceFor(norm.state),
        .transport = norm.transport,
        .transport_label = std::move(transport_label),
        .battery_percent = battery,
        .meta_template =
            std::string(norm.state == DeviceStateKind::Connected
                            ? Vocabulary::Device::kConnectedMetaTemplate
                            : Vocabulary::Device::kAgeOnlyMetaTemplate),
        .state_since_ms = state_since,
        .connected_since_ms = connected_since,
        .detail =
            DeviceDetailView{
                .serial = disp_serial,
                .exact_model = exact_model,
                .android_version = android_ver,
                .address = address,
                .copy_fields = std::move(copy_fields),
            },
    });
  }

  mapped = OrderDevices(std::move(mapped));
  std::optional<std::string> count_label =
      (mapped.size() >= 2) ? std::optional<std::string>(
                                 std::to_string(mapped.size()) + " devices")
                           : std::nullopt;

  std::vector<PairingOfferView> next_offers = snapshot.pairing_offers;
  next_offers.erase(
      std::remove_if(next_offers.begin(), next_offers.end(),
                     [&](const PairingOfferView& off) {
                       if (state.active_pairing_offer_id.has_value() &&
                           *state.active_pairing_offer_id == off.id) {
                         return true;
                       }
                       auto it = state.offer_bindings.find(off.id);
                       if (it != state.offer_bindings.end()) {
                         if (state.active_pairing_host.has_value() &&
                             EqualsIgnoreCaseAscii(it->second.host,
                                                   *state.active_pairing_host)) {
                           return true;
                         }
                         return IsDeviceConnectedOnWifi(it->second.host,
                                                       it->second.serial,
                                                       mapped);
                       }
                       return false;
                     }),
      next_offers.end());

  HealthView next_health = WithDeviceDerivedTray(
      snapshot.health, mapped, next_offers);

  if (sequence.has_value()) {
    if (state.active_grace_generation != 0 && out_grace) {
      *out_grace = GraceCommand::Cancel();
    }
    state.latest_applied_list_at_ms = at_ms;
    state.latest_device_sequence =
        std::max(state.latest_device_sequence, *sequence);
    state.active_grace_generation = 0;
  }
  state.metadata_sessions = std::move(next_sessions);
  state.next_metadata_generation = counter;

  std::optional<PairingOperationView> next_pairing = snapshot.pairing;
  if (next_pairing.has_value() &&
      next_pairing->phase == PairingPhase::PairedWaitingForDevice &&
      MatchesWaitingPairing(mapped, state)) {
    next_pairing = std::nullopt;
    state.active_pairing_wait_generation = 0;
    state.active_pairing_offer_id = std::nullopt;
    state.active_pairing_host = std::nullopt;
    state.active_pairing_serial = std::nullopt;
    state.active_pairing_display_name = std::nullopt;
    if (out_pairing_wait) {
      *out_pairing_wait = GraceCommand::Cancel();
    }
  }

  if (snapshot.devices != mapped ||
      snapshot.device_count_label != count_label ||
      snapshot.health != next_health ||
      snapshot.pairing != next_pairing ||
      snapshot.pairing_offers != next_offers) {
    snapshot.revision += 1;
    snapshot.generated_at_ms = now_ms;
    snapshot.devices = std::move(mapped);
    snapshot.device_count_label = std::move(count_label);
    snapshot.health = std::move(next_health);
    snapshot.pairing = std::move(next_pairing);
    snapshot.pairing_offers = std::move(next_offers);
  }
}

ReduceResult ReduceWithDeviceState(const AppSnapshot& current,
                                   const DeviceEngineState& device_state,
                                   const AdbViewEvent& evt,
                                   int64_t now_ms) {
  int64_t initial_rev = current.revision;
  ReduceResult res{
      .snapshot = current,
      .device_state = device_state,
      .grace = GraceCommand::None(),
      .pairing_wait = GraceCommand::None(),
      .wireless_restoring = GraceCommand::None(),
      .wireless_unavailable = GraceCommand::None(),
      .pairing_qr = GraceCommand::None(),
  };

  auto reduce_tracker_failed = [&]() {
    if (res.device_state.active_grace_generation == 0) {
      int64_t gen = res.device_state.next_outage_generation + 1;
      res.device_state.active_grace_generation = gen;
      res.device_state.next_outage_generation = gen;
      res.grace = GraceCommand::Schedule(gen);
    }
  };

  auto reduce_offer_source = [&](std::span<const RawMdnsService> effective,
                                 std::vector<RawMdnsService> source_services,
                                 bool is_adb_source,
                                 int64_t source_at_ms) {
    auto [offers, bindings] = MapPairingOffers(
        effective, res.snapshot.devices, res.device_state.active_pairing_offer_id,
        res.device_state.active_pairing_host);
    HealthView next_health = WithDeviceDerivedTray(
        res.snapshot.health, res.snapshot.devices, offers);
    std::optional<PairingOperationView> next_pairing = res.snapshot.pairing;

    bool op_active = next_pairing.has_value() &&
                     (next_pairing->phase == PairingPhase::Pairing ||
                      next_pairing->phase ==
                          PairingPhase::PairedWaitingForDevice);
    if (next_pairing.has_value() && !op_active &&
        bindings.find(next_pairing->offer_id) == bindings.end()) {
      next_pairing = std::nullopt;
      res.device_state.active_pairing_wait_generation = 0;
      res.device_state.active_pairing_offer_id = std::nullopt;
      res.device_state.active_pairing_host = std::nullopt;
      res.device_state.active_pairing_serial = std::nullopt;
      res.device_state.active_pairing_display_name = std::nullopt;
      res.pairing_wait = GraceCommand::Cancel();
    }

    std::vector<SubsystemWarningView> next_warnings = res.snapshot.warnings;
    if (is_adb_source) {
      next_warnings.erase(
          std::remove_if(next_warnings.begin(), next_warnings.end(),
                         [](const SubsystemWarningView& w) {
                           return w.subsystem ==
                                  SubsystemKind::WirelessDiscovery;
                         }),
          next_warnings.end());
    }

    res.device_state.offer_bindings = std::move(bindings);
    if (is_adb_source) {
      res.device_state.latest_applied_mdns_at_ms = source_at_ms;
      res.device_state.latest_mdns_services = std::move(source_services);
      if (res.device_state.active_wireless_restoring_generation != 0) {
        res.wireless_restoring = GraceCommand::Cancel();
      }
      if (res.device_state.active_wireless_unavailable_generation != 0) {
        res.wireless_unavailable = GraceCommand::Cancel();
      }
      res.device_state.wireless_failed = false;
      res.device_state.active_wireless_restoring_generation = 0;
      res.device_state.active_wireless_unavailable_generation = 0;
    } else {
      res.device_state.latest_applied_fallback_at_ms = source_at_ms;
      res.device_state.latest_fallback_services = std::move(source_services);
    }

    if (res.snapshot.pairing_offers != offers ||
        res.snapshot.health != next_health ||
        res.snapshot.warnings != next_warnings ||
        res.snapshot.pairing != next_pairing) {
      res.snapshot.revision += 1;
      res.snapshot.generated_at_ms = now_ms;
      res.snapshot.pairing_offers = std::move(offers);
      res.snapshot.health = std::move(next_health);
      res.snapshot.warnings = std::move(next_warnings);
      res.snapshot.pairing = std::move(next_pairing);
    }
  };

  if (const auto* sc = std::get_if<AdbServerStateChangedEvent>(&evt)) {
    res.device_state.server_mdns_enabled = sc->mdns_enabled;
    if (sc->phase != AdbServerPhase::Up) {
      reduce_tracker_failed();
    }
    HealthView target_health = MapPhaseToHealth(sc->phase, res.snapshot);
    if (res.snapshot.health != target_health) {
      res.snapshot.revision += 1;
      res.snapshot.generated_at_ms = now_ms;
      res.snapshot.health = WithDeviceDerivedTray(
          target_health, res.snapshot.devices, res.snapshot.pairing_offers);
    }
  } else if (const auto* sp = std::get_if<SetPinnedEvent>(&evt)) {
    if (res.snapshot.shell.pinned != sp->pinned) {
      res.snapshot.revision += 1;
      res.snapshot.generated_at_ms = now_ms;
      res.snapshot.shell.pinned = sp->pinned;
    }
  } else if (const auto* sa = std::get_if<ShellAppearanceChangedEvent>(&evt)) {
    ShellView next_shell{
        .pinned = res.snapshot.shell.pinned,
        .theme = sa->theme,
        .transparency_enabled = sa->transparency_enabled,
        .forced_colors = sa->forced_colors,
        .reduced_motion = sa->reduced_motion,
    };
    if (res.snapshot.shell != next_shell) {
      res.snapshot.revision += 1;
      res.snapshot.generated_at_ms = now_ms;
      res.snapshot.shell = next_shell;
    }
  } else if (const auto* dl = std::get_if<DeviceListReceivedEvent>(&evt)) {
    if (!IsStaleSequence(dl->sequence, res.device_state.latest_device_sequence,
                         dl->at_ms,
                         res.device_state.latest_applied_list_at_ms)) {
      res.device_state.latest_raw_devices = dl->devices;
      std::vector<RawMdnsService> effective_services =
          MergeEffectiveMdnsServices(
              res.device_state.latest_mdns_services,
              res.device_state.latest_fallback_services);

      ReconcileDeviceViews(res.snapshot, res.device_state, dl->devices,
                           effective_services, dl->at_ms, now_ms,
                           dl->sequence, &res.grace, &res.pairing_wait);
    }
  } else if (std::holds_alternative<DeviceTrackerFailedEvent>(evt)) {
    reduce_tracker_failed();
  } else if (const auto* ge =
                 std::get_if<DeviceTrackerGraceExpiredEvent>(&evt)) {
    if (ge->generation != 0 &&
        ge->generation == res.device_state.active_grace_generation) {
      res.device_state.active_grace_generation = 0;
      res.device_state.metadata_sessions.clear();
      res.device_state.latest_raw_devices.clear();
      if (!res.snapshot.devices.empty() ||
          res.snapshot.device_count_label.has_value()) {
        res.snapshot.revision += 1;
        res.snapshot.generated_at_ms = now_ms;
        res.snapshot.devices.clear();
        res.snapshot.device_count_label = std::nullopt;
        res.snapshot.health = WithDeviceDerivedTray(
            res.snapshot.health, res.snapshot.devices,
            res.snapshot.pairing_offers);
      }
    }
  } else if (const auto* ms = std::get_if<MdnsServicesReceivedEvent>(&evt)) {
    if (!IsStaleSequence(ms->sequence, res.device_state.latest_mdns_sequence,
                         ms->at_ms,
                         res.device_state.latest_applied_mdns_at_ms)) {
      res.device_state.latest_mdns_sequence =
          std::max(res.device_state.latest_mdns_sequence, ms->sequence);
      auto effective = MergeEffectiveMdnsServices(
          ms->services, res.device_state.latest_fallback_services);
      if (!res.device_state.latest_raw_devices.empty()) {
        ReconcileDeviceViews(res.snapshot, res.device_state,
                             res.device_state.latest_raw_devices, effective,
                             ms->at_ms, now_ms, std::nullopt, nullptr,
                             &res.pairing_wait);
      }
      reduce_offer_source(effective, ms->services, true, ms->at_ms);
    }
  } else if (const auto* mf = std::get_if<MdnsFallbackReceivedEvent>(&evt)) {
    if (!IsStaleSequence(
            mf->sequence, res.device_state.latest_fallback_sequence, mf->at_ms,
            res.device_state.latest_applied_fallback_at_ms)) {
      res.device_state.latest_fallback_sequence =
          std::max(res.device_state.latest_fallback_sequence, mf->sequence);
      auto effective = MergeEffectiveMdnsServices(
          res.device_state.latest_mdns_services, mf->services);
      if (!res.device_state.latest_raw_devices.empty()) {
        ReconcileDeviceViews(res.snapshot, res.device_state,
                             res.device_state.latest_raw_devices, effective,
                             mf->at_ms, now_ms, std::nullopt, nullptr,
                             &res.pairing_wait);
      }
      reduce_offer_source(effective, mf->services, false, mf->at_ms);
    }
  } else if (std::holds_alternative<MdnsTrackerFailedEvent>(evt)) {
    if (!res.device_state.wireless_failed) {
      int64_t gen = res.device_state.next_outage_generation + 1;
      res.device_state.wireless_failed = true;
      res.device_state.active_wireless_restoring_generation = gen;
      res.device_state.active_wireless_unavailable_generation = gen;
      res.device_state.next_outage_generation = gen;
      res.wireless_restoring = GraceCommand::Schedule(gen);
      res.wireless_unavailable = GraceCommand::Schedule(gen);
    }
  } else if (const auto* wre =
                 std::get_if<WirelessDiscoveryRestoringExpiredEvent>(&evt)) {
    if (wre->generation != 0 &&
        wre->generation ==
            res.device_state.active_wireless_restoring_generation &&
        res.device_state.wireless_failed) {
      bool has_unavail = false;
      for (const auto& w : res.snapshot.warnings) {
        if (w.subsystem == SubsystemKind::WirelessDiscovery &&
            w.title == Vocabulary::Wireless::kUnavailableTitle) {
          has_unavail = true;
          break;
        }
      }
      if (!has_unavail) {
        SubsystemWarningView warning{
            .subsystem = SubsystemKind::WirelessDiscovery,
            .severity = SeverityKind::Informational,
            .title = std::string(Vocabulary::Wireless::kRestoringTitle),
            .detail = std::string(Vocabulary::Wireless::kRestoringDetail),
        };
        std::vector<SubsystemWarningView> next_warnings;
        for (const auto& w : res.snapshot.warnings) {
          if (w.subsystem != SubsystemKind::WirelessDiscovery) {
            next_warnings.push_back(w);
          }
        }
        next_warnings.push_back(std::move(warning));
        if (res.snapshot.warnings != next_warnings) {
          res.snapshot.revision += 1;
          res.snapshot.generated_at_ms = now_ms;
          res.snapshot.warnings = std::move(next_warnings);
        }
      }
    }
  } else if (const auto* wue =
                 std::get_if<WirelessDiscoveryUnavailableExpiredEvent>(&evt)) {
    if (wue->generation != 0 &&
        wue->generation ==
            res.device_state.active_wireless_unavailable_generation &&
        res.device_state.wireless_failed) {
      res.device_state.latest_mdns_services.clear();
      auto fallback_copy = res.device_state.latest_fallback_services;
      reduce_offer_source(
          fallback_copy, fallback_copy, false,
          res.device_state.latest_applied_fallback_at_ms.value_or(now_ms));

      SubsystemWarningView warning{
          .subsystem = SubsystemKind::WirelessDiscovery,
          .severity = SeverityKind::Attention,
          .title = std::string(Vocabulary::Wireless::kUnavailableTitle),
          .detail = std::string(Vocabulary::Wireless::kUnavailableDetail),
      };
      std::vector<SubsystemWarningView> next_warnings;
      for (const auto& w : res.snapshot.warnings) {
        if (w.subsystem != SubsystemKind::WirelessDiscovery) {
          next_warnings.push_back(w);
        }
      }
      next_warnings.push_back(std::move(warning));
      if (res.snapshot.warnings != next_warnings) {
        res.snapshot.revision += 1;
        res.snapshot.generated_at_ms = now_ms;
        res.snapshot.warnings = std::move(next_warnings);
      }
    }
  } else if (const auto* pp = std::get_if<PairingProgressedEvent>(&evt)) {
    std::string display_name =
        pp->display_name.has_value()
            ? *pp->display_name
            : (res.device_state.offer_bindings.find(pp->offer_id) !=
                       res.device_state.offer_bindings.end()
                   ? res.device_state.offer_bindings.at(pp->offer_id)
                         .display_name
                   : std::string(Vocabulary::Pairing::kDefaultDeviceName));

    auto op = MapPairingOperation(pp->offer_id, pp->phase, pp->failure_kind,
                                  display_name);

    auto bind_it = res.device_state.offer_bindings.find(pp->offer_id);
    std::optional<std::string> b_host =
        (bind_it != res.device_state.offer_bindings.end())
            ? std::optional<std::string>(bind_it->second.host)
            : res.device_state.active_pairing_host;
    std::optional<std::string> b_serial =
        (bind_it != res.device_state.offer_bindings.end())
            ? bind_it->second.serial
            : res.device_state.active_pairing_serial;

    if (pp->phase == PairingPhase::PairedWaitingForDevice) {
      int64_t wait_gen = res.device_state.active_pairing_wait_generation + 1;
      if (wait_gen <= 0) wait_gen = 1;
      res.device_state.active_pairing_wait_generation = wait_gen;
      res.device_state.active_pairing_offer_id = pp->offer_id;
      res.device_state.active_pairing_host = b_host;
      res.device_state.active_pairing_serial = b_serial;
      res.device_state.active_pairing_display_name = display_name;
      res.pairing_wait = GraceCommand::Schedule(wait_gen);
    } else {
      if (res.device_state.active_pairing_wait_generation != 0) {
        res.pairing_wait = GraceCommand::Cancel();
      }
      res.device_state.active_pairing_wait_generation = 0;
      if (pp->phase == PairingPhase::Cleared) {
        res.device_state.active_pairing_offer_id = std::nullopt;
        res.device_state.active_pairing_host = std::nullopt;
        res.device_state.active_pairing_serial = std::nullopt;
        res.device_state.active_pairing_display_name = std::nullopt;
      } else {
        res.device_state.active_pairing_offer_id = pp->offer_id;
        res.device_state.active_pairing_host = b_host;
        res.device_state.active_pairing_serial = b_serial;
        res.device_state.active_pairing_display_name = display_name;
      }
    }

    bool offers_changed = false;
    if (pp->phase == PairingPhase::PairedWaitingForDevice && !pp->offer_id.empty()) {
      auto orig_sz = res.snapshot.pairing_offers.size();
      res.snapshot.pairing_offers.erase(
          std::remove_if(res.snapshot.pairing_offers.begin(),
                         res.snapshot.pairing_offers.end(),
                         [&](const PairingOfferView& off) {
                           return off.id == pp->offer_id;
                         }),
          res.snapshot.pairing_offers.end());
      if (res.snapshot.pairing_offers.size() != orig_sz) {
        offers_changed = true;
        res.snapshot.health = WithDeviceDerivedTray(
            res.snapshot.health, res.snapshot.devices, res.snapshot.pairing_offers);
      }
    }

    if (res.snapshot.pairing != op || offers_changed) {
      res.snapshot.revision += 1;
      res.snapshot.generated_at_ms = now_ms;
      res.snapshot.pairing = std::move(op);
    }
  } else if (const auto* pwe = std::get_if<PairingWaitExpiredEvent>(&evt)) {
    if (pwe->generation != 0 &&
        pwe->generation == res.device_state.active_pairing_wait_generation) {
      res.device_state.active_pairing_wait_generation = 0;
      if (res.snapshot.pairing.has_value() &&
          res.snapshot.pairing->phase ==
              PairingPhase::PairedWaitingForDevice) {
        res.device_state.active_pairing_offer_id = std::nullopt;
        res.device_state.active_pairing_host = std::nullopt;
        res.device_state.active_pairing_serial = std::nullopt;
        res.device_state.active_pairing_display_name = std::nullopt;
        res.snapshot.revision += 1;
        res.snapshot.generated_at_ms = now_ms;
        res.snapshot.pairing = std::nullopt;
      }
    }
  } else if (const auto* mr = std::get_if<MetadataReceivedEvent>(&evt)) {
    for (size_t i = 0; i < res.snapshot.devices.size(); ++i) {
      if (res.snapshot.devices[i].id != mr->device_id) {
        continue;
      }
      DeviceView dev = res.snapshot.devices[i];
      std::optional<int> battery = mr->battery;
      std::optional<std::string> model =
          mr->model.has_value() ? mr->model : dev.detail.exact_model;
      std::optional<std::string> version =
          mr->android_version.has_value() ? mr->android_version
                                          : dev.detail.android_version;

      bool naming_probe = mr->marketing_name.has_value() ||
                          mr->device_name.has_value() || mr->model.has_value();
      std::string display_name =
          naming_probe
              ? ResolveDeviceDisplayName(
                    mr->marketing_name.has_value()
                        ? std::optional<std::string_view>(*mr->marketing_name)
                        : std::nullopt,
                    mr->device_name.has_value()
                        ? std::optional<std::string_view>(*mr->device_name)
                        : std::nullopt,
                    model.has_value()
                        ? std::optional<std::string_view>(*model)
                        : std::nullopt,
                    mr->brand.has_value()
                        ? std::optional<std::string_view>(*mr->brand)
                        : std::nullopt,
                    mr->manufacturer.has_value()
                        ? std::optional<std::string_view>(*mr->manufacturer)
                        : std::nullopt)
              : dev.display_name;

      auto sit = res.device_state.metadata_sessions.find(mr->device_id);
      if (sit != res.device_state.metadata_sessions.end()) {
        sit->second.battery_observed_at_ms =
            battery.has_value() ? std::optional<int64_t>(now_ms) : std::nullopt;
      }

      if (dev.battery_percent != battery || dev.detail.exact_model != model ||
          dev.detail.android_version != version ||
          dev.display_name != display_name) {
        dev.battery_percent = battery;
        dev.detail.exact_model = std::move(model);
        dev.detail.android_version = std::move(version);
        dev.display_name = std::move(display_name);
        res.snapshot.devices[i] = std::move(dev);
        res.snapshot.health = WithDeviceDerivedTray(
            res.snapshot.health, res.snapshot.devices,
            res.snapshot.pairing_offers);
        res.snapshot.revision += 1;
        res.snapshot.generated_at_ms = now_ms;
      }
      break;
    }
  } else if (std::holds_alternative<DiscoveryCheckStartedEvent>(evt)) {
    res.device_state.discovery_check_running = true;
  } else if (const auto* ddr =
                 std::get_if<DiscoveryDiagnosisReceivedEvent>(&evt)) {
    res.device_state.latest_discovery_diagnosis = ddr->diagnosis;
    res.device_state.discovery_check_running = false;
    res.device_state.repair_result_line =
        res.device_state.awaiting_measure_repair_id.has_value()
            ? std::optional<std::string>(
                  Vocabulary::Discovery::kResultStillBroken)
            : std::nullopt;
    res.device_state.awaiting_measure_repair_id = std::nullopt;
  } else if (const auto* drc =
                 std::get_if<DiscoveryRepairCatalogEvent>(&evt)) {
    res.device_state.repair_catalog = drc->repairs;
  } else if (const auto* drs =
                 std::get_if<DiscoveryRepairStartedEvent>(&evt)) {
    if (!res.device_state.running_repair_id.has_value()) {
      res.device_state.running_repair_id = drs->repair_id;
      res.device_state.repair_result_line = std::nullopt;
    }
  } else if (const auto* drf =
                 std::get_if<DiscoveryRepairFinishedEvent>(&evt)) {
    if (res.device_state.running_repair_id == drf->repair_id) {
      res.device_state.running_repair_id = std::nullopt;
      res.device_state.repair_results[drf->repair_id] = drf->outcome;
      if (drf->outcome == DiscoveryRepairOutcome::Completed) {
        res.device_state.awaiting_measure_repair_id = drf->repair_id;
        res.device_state.repair_result_line = std::nullopt;
      } else {
        res.device_state.awaiting_measure_repair_id = std::nullopt;
        res.device_state.repair_result_line =
            Vocabulary::Discovery::ResultLineFor(drf->outcome);
      }
    }
  } else if (const auto* qrs = std::get_if<PairingQrStartedEvent>(&evt)) {
    if (res.device_state.active_qr_session_generation == 0) {
      int64_t gen = res.device_state.next_qr_session_generation;
      if (gen <= 0) gen = 1;
      res.device_state.active_qr_session_generation = gen;
      res.device_state.next_qr_session_generation = gen + 1;
      res.snapshot.revision += 1;
      res.snapshot.generated_at_ms = now_ms;
      res.snapshot.pairing_qr = qrs->view;
      res.pairing_qr = GraceCommand::Schedule(gen);
    }
  } else if (const auto* qrc = std::get_if<PairingQrClearedEvent>(&evt)) {
    int64_t active = res.device_state.active_qr_session_generation;
    if (active != 0 && (qrc->generation == 0 || qrc->generation == active)) {
      res.device_state.active_qr_session_generation = 0;
      res.pairing_qr = GraceCommand::Cancel();
      if (res.snapshot.pairing_qr.has_value()) {
        res.snapshot.revision += 1;
        res.snapshot.generated_at_ms = now_ms;
        res.snapshot.pairing_qr = std::nullopt;
      }
    }
  }

  ApplyDiscoveryHealth(&res, now_ms);
  if (res.snapshot.revision > initial_rev) {
    res.snapshot.revision = initial_rev + 1;
  }
  return res;
}

AppSnapshot Reduce(const AppSnapshot& current,
                   const AdbViewEvent& evt,
                   int64_t now_ms) {
  DeviceEngineState empty_state;
  return ReduceWithDeviceState(current, empty_state, evt, now_ms).snapshot;
}

}  // namespace adbhawk::core
