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

    public NormalizedDevice Normalize(RawDevice raw) {
        ArgumentNullException.ThrowIfNull(raw);

        DeviceTransport transport = MapTransport(raw.ConnectionType);
        string hardwareSerial = ResolveHardwareSerial(raw.Serial);
        string id = BuildId(transport, hardwareSerial);
        DeviceSemanticState state = MapState(raw);
        string displayName = ResolveDisplayName(raw.Model, raw.Product, hardwareSerial);

        return new NormalizedDevice {
            Id = id,
            HardwareSerial = hardwareSerial,
            Transport = transport,
            SemanticState = state,
            DisplayName = displayName,
            Product = raw.Product,
            Model = raw.Model,
            Device = raw.Device,
        };
    }

    public ImmutableArray<NormalizedDevice> NormalizeList(ImmutableArray<RawDevice> devices) {
        if (devices.IsDefaultOrEmpty) {
            return ImmutableArray<NormalizedDevice>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<NormalizedDevice>(devices.Length);
        foreach (RawDevice raw in devices) {
            builder.Add(Normalize(raw));
        }

        // Preserve duplicates as distinct list entries (USB/Wi-Fi stay distinct by Id;
        // same-Id collisions are not merged or suffix-renamed — ENGINEERING has no merge rule).
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
    /// Phase 5: mDNS connect lookup for host:port is unavailable → keep host:port.
    /// </summary>
    internal static string ResolveHardwareSerial(string serial) {
        ArgumentNullException.ThrowIfNull(serial);

        Match mdns = AdbMdnsInstanceSerialRegex().Match(serial);
        if (mdns.Success) {
            return mdns.Groups["serial"].Value;
        }

        if (EndpointSerialRegex().IsMatch(serial)) {
            // Rule 2c without Phase 6 mDNS enrichment.
            return serial;
        }

        return serial;
    }

    internal static string BuildId(DeviceTransport transport, string hardwareSerial) {
        string prefix = transport switch {
            DeviceTransport.Usb => "usb:",
            DeviceTransport.WiFi => "wifi:",
            _ => "unk:",
        };
        return prefix + hardwareSerial;
    }

    internal static string ResolveDisplayName(string? model, string? product, string hardwareSerial) {
        if (!string.IsNullOrEmpty(model)) {
            return model;
        }

        if (!string.IsNullOrEmpty(product)) {
            return product;
        }

        return hardwareSerial;
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

    // host:port including IPv4 and bracketed IPv6 ([addr]:port).
    [GeneratedRegex(@"^.+:\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointSerialRegex();
}
