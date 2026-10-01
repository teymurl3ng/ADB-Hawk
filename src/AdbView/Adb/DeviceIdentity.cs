namespace AdbView.Adb;

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using AdbView.Adb.Model;
using Serilog;

/// <summary>
/// The only serial-shape heuristics in the repo (ENGINEERING.md §6.7).
/// Converts Phase 5A <see cref="RawDevice"/> into <see cref="NormalizedDevice"/>.
/// Does not own timestamps, UI wording, or snapshot integration.
/// </summary>
internal sealed partial class DeviceIdentity {
    // Proto ConnectionType (adb_host.proto @ 1cf2f017…)
    private const int ConnectionTypeUnknown = 0;
    private const int ConnectionTypeUsb = 1;
    private const int ConnectionTypeSocket = 2;

    // Proto ConnectionState
    private const int StateAny = 0;
    private const int StateConnecting = 1;
    private const int StateAuthorizing = 2;
    private const int StateUnauthorized = 3;
    private const int StateNoPermission = 4;
    private const int StateDetached = 5;
    private const int StateOffline = 6;
    private const int StateBootloader = 7;
    private const int StateDevice = 8;
    private const int StateHost = 9;
    private const int StateRecovery = 10;
    private const int StateSideload = 11;
    private const int StateRescue = 12;

    private readonly ConcurrentDictionary<string, byte> _unknownStateDiagnostics = new(StringComparer.Ordinal);
    private readonly Action<string>? _onUnknownStateDiagnostic;

    public DeviceIdentity(Action<string>? onUnknownStateDiagnostic = null) {
        _onUnknownStateDiagnostic = onUnknownStateDiagnostic;
    }

    public NormalizedDevice Normalize(
        RawDevice raw,
        ImmutableArray<RawMdnsService> mdnsServices = default) {
        ArgumentNullException.ThrowIfNull(raw);
        if (mdnsServices.IsDefault) {
            mdnsServices = ImmutableArray<RawMdnsService>.Empty;
        }

        DeviceTransport transport = MapTransport(raw.ConnectionType);
        string hardwareSerial = ResolveHardwareSerial(raw.Serial, mdnsServices);
        string id = BuildId(transport, hardwareSerial);
        DeviceSemanticState state = MapState(raw);
        string? givenName = FindConnectGivenName(raw.Serial, hardwareSerial, mdnsServices);
        string displayName = ResolveDisplayName(givenName, raw.Model, raw.Product, hardwareSerial);

        return new NormalizedDevice {
            Id = id,
            HardwareSerial = hardwareSerial,
            AdbSerial = raw.Serial,
            Transport = transport,
            SemanticState = state,
            DisplayName = displayName,
            Product = raw.Product,
            Model = raw.Model,
            Device = raw.Device,
        };
    }

    public ImmutableArray<NormalizedDevice> NormalizeList(
        ImmutableArray<RawDevice> devices,
        ImmutableArray<RawMdnsService> mdnsServices = default) {
        if (devices.IsDefaultOrEmpty) {
            return ImmutableArray<NormalizedDevice>.Empty;
        }

        if (mdnsServices.IsDefault) {
            mdnsServices = ImmutableArray<RawMdnsService>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<NormalizedDevice>(devices.Length);
        foreach (RawDevice raw in devices) {
            builder.Add(Normalize(raw, mdnsServices));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// ENGINEERING.md §6.7 rule 1 (exact): "Transport comes from proto connection_type:
    /// USB → Usb, SOCKET → WiFi, absent → Unknown."
    /// §6.11 mentions text-path "transport inferred from serial shape" but defines no algorithm;
    /// §6.7 names only 2b/2c as serial-shape heuristics (hardware-serial identity, not transport);
    /// §11 row 6 permits text fallback with transport Unknown. Do not invent transport heuristics.
    /// </summary>
    internal static DeviceTransport MapTransport(int? connectionType) {
        return connectionType switch {
            ConnectionTypeUsb => DeviceTransport.Usb,
            ConnectionTypeSocket => DeviceTransport.WiFi,
            ConnectionTypeUnknown => DeviceTransport.Unknown,
            null => DeviceTransport.Unknown,
            _ => DeviceTransport.Unknown,
        };
    }

    /// <summary>
    /// Hardware serial resolution (ENGINEERING.md §6.7 rule 2).
    /// Rule 2c: host:port serials resolve via matching <c>_adb-tls-connect._tcp</c> serial when present.
    /// </summary>
    internal static string ResolveHardwareSerial(
        string serial,
        ImmutableArray<RawMdnsService> mdnsServices = default) {
        ArgumentNullException.ThrowIfNull(serial);
        if (mdnsServices.IsDefault) {
            mdnsServices = ImmutableArray<RawMdnsService>.Empty;
        }

        // Strip DNS-SD service-type suffixes some trackers append to the instance name.
        string candidate = StripDnsSdServiceSuffix(serial);

        Match mdns = AdbMdnsInstanceSerialRegex().Match(candidate);
        if (mdns.Success) {
            return mdns.Groups["serial"].Value;
        }

        // Broader adb-<hardware>-… form (spaces / duplicate markers after the guid segment).
        Match loose = AdbMdnsLooseInstanceSerialRegex().Match(candidate);
        if (loose.Success) {
            return loose.Groups["serial"].Value;
        }

        if (EndpointSerialRegex().IsMatch(serial)) {
            string? fromConnect = FindConnectSerial(serial, mdnsServices);
            if (!string.IsNullOrEmpty(fromConnect)) {
                return fromConnect;
            }

            return serial;
        }

        // Prefer stripped candidate so DNS-SD residues never become HardwareSerial.
        return candidate;
    }

    internal static string StripDnsSdServiceSuffix(string serial) {
        Match suffix = DnsSdServiceSuffixRegex().Match(serial);
        return suffix.Success ? serial[..suffix.Index] : serial;
    }

    internal static string BuildId(DeviceTransport transport, string hardwareSerial) {
        string prefix = transport switch {
            DeviceTransport.Usb => "usb:",
            DeviceTransport.WiFi => "wifi:",
            _ => "unk:",
        };
        return prefix + hardwareSerial;
    }

    /// <summary>
    /// Early DisplayName before shell naming metadata (ENGINEERING.md §6.7).
    /// Never uses transport identity (serial / host:port / adb-*) as presentation.
    /// </summary>
    internal static string ResolveDisplayName(
        string? givenName,
        string? model,
        string? product,
        string hardwareSerial) {
        _ = hardwareSerial; // kept for call-site compatibility; never used as DisplayName
        if (IsUsableEarlyLabel(givenName)) {
            return givenName!.Trim();
        }

        if (IsUsableEarlyLabel(model)) {
            return model!.Trim();
        }

        if (IsUsableEarlyLabel(product)) {
            return product!.Trim();
        }

        // Must match Vocabulary.Pairing.DefaultDeviceName (State must not be referenced here).
        return "Android device";
    }

    private static bool IsUsableEarlyLabel(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (AdbMdnsLooseInstanceSerialRegex().IsMatch(trimmed)
            || EndpointSerialRegex().IsMatch(trimmed)
            || DnsSdServiceSuffixRegex().IsMatch(trimmed)) {
            return false;
        }

        return true;
    }

    /// <summary>Back-compat overload used by older call sites/tests.</summary>
    internal static string ResolveDisplayName(string? model, string? product, string hardwareSerial) =>
        ResolveDisplayName(null, model, product, hardwareSerial);

    private static string? FindConnectSerial(string endpointSerial, ImmutableArray<RawMdnsService> services) {
        if (services.IsDefaultOrEmpty) {
            return null;
        }

        foreach (RawMdnsService service in services) {
            if (service.Kind != MdnsServiceKind.Connect
                || !string.Equals(service.ServiceType, MdnsServiceTypes.Connect, StringComparison.Ordinal)) {
                continue;
            }

            if (!EndpointMatchesService(endpointSerial, service)) {
                continue;
            }

            if (!string.IsNullOrEmpty(service.Serial)) {
                return service.Serial;
            }
        }

        return null;
    }

    private static string? FindConnectGivenName(
        string endpointOrSerial,
        string hardwareSerial,
        ImmutableArray<RawMdnsService> services) {
        if (services.IsDefaultOrEmpty) {
            return null;
        }

        foreach (RawMdnsService service in services) {
            if (service.Kind != MdnsServiceKind.Connect
                || !string.Equals(service.ServiceType, MdnsServiceTypes.Connect, StringComparison.Ordinal)) {
                continue;
            }

            bool match = EndpointMatchesService(endpointOrSerial, service)
                || (!string.IsNullOrEmpty(service.Serial)
                    && string.Equals(service.Serial, hardwareSerial, StringComparison.Ordinal));
            if (!match) {
                continue;
            }

            if (!string.IsNullOrEmpty(service.GivenName)) {
                return service.GivenName;
            }
        }

        return null;
    }

    private static bool EndpointMatchesService(string endpointSerial, RawMdnsService service) {
        if (!TrySplitEndpoint(endpointSerial, out string host, out int port)) {
            return false;
        }

        if (service.Port != port) {
            return false;
        }

        if (!string.IsNullOrEmpty(service.Ipv4)
            && string.Equals(service.Ipv4, host, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (!service.Ipv6.IsDefaultOrEmpty) {
            foreach (string v6 in service.Ipv6) {
                if (string.Equals(v6, host, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TrySplitEndpoint(string endpoint, out string host, out int port) {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrEmpty(endpoint)) {
            return false;
        }

        if (endpoint.StartsWith('[')) {
            int close = endpoint.IndexOf(']', StringComparison.Ordinal);
            if (close < 2 || close + 2 >= endpoint.Length || endpoint[close + 1] != ':') {
                return false;
            }

            host = endpoint[1..close];
            return int.TryParse(endpoint[(close + 2)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out port);
        }

        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1) {
            return false;
        }

        host = endpoint[..colon];
        return int.TryParse(endpoint[(colon + 1)..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out port);
    }

    private DeviceSemanticState MapState(RawDevice raw) {
        if (raw.Format == RawDeviceFormat.ProtoBinary) {
            return MapProtoState(raw.ProtoState);
        }

        return MapTextState(raw.StateToken);
    }

    private DeviceSemanticState MapProtoState(int? protoState) {
        if (protoState is null) {
            ReportUnknownState("proto:<missing>");
            return DeviceSemanticState.Unknown;
        }

        DeviceSemanticState mapped = protoState.Value switch {
            StateAny => DeviceSemanticState.Unknown,
            StateConnecting => DeviceSemanticState.Connecting,
            StateAuthorizing => DeviceSemanticState.Authorizing,
            StateUnauthorized => DeviceSemanticState.Unauthorized,
            StateNoPermission => DeviceSemanticState.Unknown,
            StateDetached => DeviceSemanticState.Detached,
            StateOffline => DeviceSemanticState.Offline,
            StateBootloader => DeviceSemanticState.Bootloader,
            StateDevice => DeviceSemanticState.Connected,
            StateHost => DeviceSemanticState.Unknown,
            StateRecovery => DeviceSemanticState.Recovery,
            StateSideload => DeviceSemanticState.Sideload,
            StateRescue => DeviceSemanticState.Rescue,
            _ => DeviceSemanticState.Unknown,
        };

        if (mapped == DeviceSemanticState.Unknown
            && protoState.Value is not (StateAny or StateNoPermission or StateHost)) {
            ReportUnknownState($"proto:{protoState.Value}");
        }

        return mapped;
    }

    private DeviceSemanticState MapTextState(string? stateToken) {
        if (string.IsNullOrEmpty(stateToken)) {
            ReportUnknownState("text:<missing>");
            return DeviceSemanticState.Unknown;
        }

        DeviceSemanticState mapped = stateToken switch {
            "device" => DeviceSemanticState.Connected,
            "unauthorized" => DeviceSemanticState.Unauthorized,
            "offline" => DeviceSemanticState.Offline,
            "recovery" => DeviceSemanticState.Recovery,
            "bootloader" => DeviceSemanticState.Bootloader,
            "sideload" => DeviceSemanticState.Sideload,
            "rescue" => DeviceSemanticState.Rescue,
            "authorizing" => DeviceSemanticState.Authorizing,
            "connecting" => DeviceSemanticState.Connecting,
            "detached" => DeviceSemanticState.Detached,
            _ => DeviceSemanticState.Unknown,
        };

        if (mapped == DeviceSemanticState.Unknown) {
            ReportUnknownState($"text:{stateToken}");
        }

        return mapped;
    }

    private void ReportUnknownState(string key) {
        if (!_unknownStateDiagnostics.TryAdd(key, 0)) {
            return;
        }

        Log.Information("Unrecognized ADB device state value: {RawState}", key);
        _onUnknownStateDiagnostic?.Invoke(key);
    }

    // mDNS-style wireless instance name used as adb serial on some builds.
    [GeneratedRegex(@"^adb-(?<serial>[^-]+)-[A-Za-z0-9]+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex AdbMdnsInstanceSerialRegex();

    [GeneratedRegex(@"^adb-(?<serial>[^-]+)-", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex AdbMdnsLooseInstanceSerialRegex();

    [GeneratedRegex(@"\._adb(?:-tls-(?:connect|pairing))?\._tcp\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DnsSdServiceSuffixRegex();

    // host:port including IPv4 and bracketed IPv6 ([addr]:port).
    [GeneratedRegex(@"^.+:\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointSerialRegex();
}
