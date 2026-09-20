namespace AdbView.State;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using AdbView.Adb;
using AdbView.Adb.Model;
using Serilog;

/// <summary>
/// Maps normalized devices into AppSnapshot DeviceView rows. Uses DeviceIdentity; no protocol parsing.
/// </summary>
internal static partial class DeviceViewMapper {
    private static readonly ConcurrentOnceSet DuplicateIdDiagnostics = new();

    public static ImmutableArray<DeviceView> MapList(
        ImmutableArray<RawDevice> rawDevices,
        ImmutableArray<DeviceView> previous,
        DateTimeOffset at,
        DeviceIdentity identity,
        ImmutableArray<RawMdnsService> mdnsServices = default,
        Func<string, bool>? preserveMetadata = null) {
        ArgumentNullException.ThrowIfNull(identity);

        if (rawDevices.IsDefaultOrEmpty) {
            return ImmutableArray<DeviceView>.Empty;
        }

        if (mdnsServices.IsDefault) {
            mdnsServices = ImmutableArray<RawMdnsService>.Empty;
        }

        var previousById = previous.IsDefaultOrEmpty
            ? new Dictionary<string, DeviceView>(StringComparer.Ordinal)
            : previous.ToDictionary(d => d.Id, StringComparer.Ordinal);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<DeviceView>(rawDevices.Length);

        foreach (RawDevice raw in rawDevices) {
            NormalizedDevice normalized = identity.Normalize(raw, mdnsServices);
            if (!seenIds.Add(normalized.Id)) {
                if (DuplicateIdDiagnostics.TryAdd(normalized.Id)) {
                    Log.Information(
                        "Dropping duplicate normalized device Id in tracker list: {DeviceId}",
                        normalized.Id);
                }

                continue;
            }

            previousById.TryGetValue(normalized.Id, out DeviceView? prior);
            bool keepMeta = preserveMetadata?.Invoke(normalized.Id) == true;
            DeviceView mapped = MapOne(normalized, prior, at, keepMeta);
            if (prior is not null && IsUnchanged(prior, mapped)) {
                builder.Add(prior);
            }
            else {
                builder.Add(mapped);
            }
        }

        return OrderDevices(builder.ToImmutable());
    }

    /// <summary>
    /// Content equality that ignores ImmutableArray backing-reference identity
    /// (record == on DeviceView is false for equal CopyFields arrays).
    /// </summary>
    internal static bool IsUnchanged(DeviceView previous, DeviceView next) =>
        previous.Id == next.Id
        && previous.DisplayName == next.DisplayName
        && previous.State == next.State
        && previous.Severity == next.Severity
        && previous.StateLabel == next.StateLabel
        && previous.StatusLine == next.StatusLine
        && previous.Guidance == next.Guidance
        && previous.Transport == next.Transport
        && previous.TransportLabel == next.TransportLabel
        && previous.BatteryPercent == next.BatteryPercent
        && previous.MetaTemplate == next.MetaTemplate
        && previous.StateSince == next.StateSince
        && previous.ConnectedSince == next.ConnectedSince
        && previous.Detail.Serial == next.Detail.Serial
        && previous.Detail.ExactModel == next.Detail.ExactModel
        && previous.Detail.AndroidVersion == next.Detail.AndroidVersion
        && previous.Detail.Address == next.Detail.Address
        && previous.Detail.CopyFields.SequenceEqual(next.Detail.CopyFields);

    public static DeviceView MapOne(
        NormalizedDevice normalized,
        DeviceView? previous,
        DateTimeOffset at,
        bool preserveMetadata = false) {
        ArgumentNullException.ThrowIfNull(normalized);

        DeviceStateKind state = MapState(normalized.SemanticState);
        TransportKind transport = MapTransport(normalized.Transport);
        string transportLabel = TransportLabel(transport);
        string stateLabel = StateLabel(state);
        SeverityKind severity = Severity(state);

        DateTimeOffset stateSince;
        DateTimeOffset? connectedSince;

        if (previous is not null && previous.State == state) {
            stateSince = previous.StateSince;
            connectedSince = state == DeviceStateKind.Connected
                ? previous.ConnectedSince ?? at
                : null;
        }
        else {
            stateSince = at;
            connectedSince = state == DeviceStateKind.Connected ? at : null;
        }

        string? address = ResolveAddress(transport, normalized.AdbSerial, normalized.HardwareSerial);
        string? displaySerial = ResolveDisplaySerial(transport, normalized.HardwareSerial);

        var copyFields = ImmutableArray.CreateBuilder<CopyFieldView>(2);
        if (!string.IsNullOrEmpty(address)) {
            copyFields.Add(new CopyFieldView {
                Key = Vocabulary.Device.CopyAddressKey,
                Label = Vocabulary.Device.CopyAddressLabel,
                Value = address,
            });
        }

        if (!string.IsNullOrEmpty(displaySerial)) {
            copyFields.Add(new CopyFieldView {
                Key = Vocabulary.Device.CopySerialKey,
                Label = Vocabulary.Device.CopySerialLabel,
                Value = displaySerial,
            });
        }

        int? battery = preserveMetadata ? previous?.BatteryPercent : null;
        string? exactModel = preserveMetadata ? previous?.Detail.ExactModel : null;
        string? androidVersion = preserveMetadata ? previous?.Detail.AndroidVersion : null;

        return new DeviceView {
            Id = normalized.Id,
            DisplayName = normalized.DisplayName,
            State = state,
            Severity = severity,
            StateLabel = stateLabel,
            StatusLine = $"{stateLabel} · {transportLabel}",
            Guidance = Guidance(state),
            Transport = transport,
            TransportLabel = transportLabel,
            BatteryPercent = battery,
            MetaTemplate = state == DeviceStateKind.Connected
                ? Vocabulary.Device.ConnectedMetaTemplate
                : Vocabulary.Device.AgeOnlyMetaTemplate,
            StateSince = stateSince,
            ConnectedSince = connectedSince,
            Detail = new DeviceDetailView {
                Serial = displaySerial,
                ExactModel = exactModel,
                AndroidVersion = androidVersion,
                Address = address,
                CopyFields = copyFields.ToImmutable(),
            },
        };
    }

    public static ImmutableArray<DeviceView> OrderDevices(ImmutableArray<DeviceView> devices) {
        if (devices.IsDefaultOrEmpty || devices.Length < 2) {
            return devices;
        }

        // ENGINEERING.md §4.3: USB before Wi-Fi, then connectedSince ?? stateSince, then serial/id.
        return devices
            .OrderBy(TransportRank)
            .ThenBy(d => d.ConnectedSince ?? d.StateSince)
            .ThenBy(d => d.Detail.Serial ?? d.Detail.Address ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public static string? DeviceCountLabel(int count) =>
        count >= 2 ? $"{count} devices" : null;

    private static int TransportRank(DeviceView d) => d.Transport switch {
        TransportKind.Usb => 0,
        TransportKind.WiFi => 1,
        _ => 2,
    };

    private static DeviceStateKind MapState(DeviceSemanticState state) => state switch {
        DeviceSemanticState.Connected => DeviceStateKind.Connected,
        DeviceSemanticState.Unauthorized => DeviceStateKind.Unauthorized,
        DeviceSemanticState.Offline => DeviceStateKind.Offline,
        DeviceSemanticState.Recovery => DeviceStateKind.Recovery,
        DeviceSemanticState.Bootloader => DeviceStateKind.Bootloader,
        DeviceSemanticState.Sideload => DeviceStateKind.Sideload,
        DeviceSemanticState.Rescue => DeviceStateKind.Rescue,
        DeviceSemanticState.Authorizing => DeviceStateKind.Authorizing,
        DeviceSemanticState.Connecting => DeviceStateKind.Connecting,
        DeviceSemanticState.Detached => DeviceStateKind.Detached,
        _ => DeviceStateKind.Unknown,
    };

    private static TransportKind MapTransport(DeviceTransport transport) => transport switch {
        DeviceTransport.Usb => TransportKind.Usb,
        DeviceTransport.WiFi => TransportKind.WiFi,
        _ => TransportKind.Unknown,
    };

    private static string TransportLabel(TransportKind transport) => transport switch {
        TransportKind.Usb => Vocabulary.Device.TransportUsb,
        TransportKind.WiFi => Vocabulary.Device.TransportWifi,
        _ => Vocabulary.Device.TransportUnknown,
    };

    private static string StateLabel(DeviceStateKind state) => state switch {
        DeviceStateKind.Connected => Vocabulary.Device.ConnectedStateLabel,
        DeviceStateKind.Unauthorized => Vocabulary.Device.UnauthorizedStateLabel,
        DeviceStateKind.Offline => Vocabulary.Device.OfflineStateLabel,
        DeviceStateKind.Recovery => Vocabulary.Device.RecoveryStateLabel,
        DeviceStateKind.Bootloader => Vocabulary.Device.BootloaderStateLabel,
        DeviceStateKind.Sideload => Vocabulary.Device.SideloadStateLabel,
        DeviceStateKind.Rescue => Vocabulary.Device.RescueStateLabel,
        DeviceStateKind.Authorizing => Vocabulary.Device.AuthorizingStateLabel,
        DeviceStateKind.Connecting => Vocabulary.Device.ConnectingStateLabel,
        DeviceStateKind.Detached => Vocabulary.Device.DetachedStateLabel,
        _ => Vocabulary.Device.UnknownStateLabel,
    };

    private static SeverityKind Severity(DeviceStateKind state) => state switch {
        DeviceStateKind.Connected => SeverityKind.Positive,
        DeviceStateKind.Unauthorized => SeverityKind.Attention,
        DeviceStateKind.Offline => SeverityKind.Failure,
        DeviceStateKind.Recovery => SeverityKind.Informational,
        DeviceStateKind.Bootloader => SeverityKind.Informational,
        DeviceStateKind.Sideload => SeverityKind.Informational,
        DeviceStateKind.Rescue => SeverityKind.Informational,
        DeviceStateKind.Authorizing => SeverityKind.Attention,
        DeviceStateKind.Connecting => SeverityKind.Informational,
        DeviceStateKind.Detached => SeverityKind.Neutral,
        _ => SeverityKind.Neutral,
    };

    private static string? Guidance(DeviceStateKind state) => state switch {
        DeviceStateKind.Unauthorized => Vocabulary.Device.UnauthorizedGuidance,
        DeviceStateKind.Offline => Vocabulary.Device.OfflineGuidance,
        _ => null,
    };

    private static string? ResolveAddress(
        TransportKind transport,
        string adbSerial,
        string hardwareSerial) {
        if (transport != TransportKind.WiFi) {
            return null;
        }

        // Prefer transport selector (AdbSerial); fall back to hardware when it is an endpoint.
        if (EndpointSerialRegex().IsMatch(adbSerial)) {
            return adbSerial;
        }

        return EndpointSerialRegex().IsMatch(hardwareSerial) ? hardwareSerial : null;
    }

    /// <summary>
    /// Hardware/display serial only — never an ADB host:port transport selector,
    /// mDNS service instance name, or <c>_adb-tls-*.tcp</c> DNS-SD residue.
    /// </summary>
    private static string? ResolveDisplaySerial(TransportKind transport, string hardwareSerial) {
        if (string.IsNullOrEmpty(hardwareSerial)) {
            return null;
        }

        if (EndpointSerialRegex().IsMatch(hardwareSerial)) {
            return null;
        }

        // Internal transport / discovery names must never appear as the Serial row.
        if (LooksLikeMdnsOrDiscoveryIdentity(hardwareSerial)) {
            return null;
        }

        return hardwareSerial;
    }

    internal static bool LooksLikeMdnsOrDiscoveryIdentity(string value) {
        if (value.Contains("_adb-tls-", StringComparison.OrdinalIgnoreCase)
            || value.Contains("._tcp", StringComparison.OrdinalIgnoreCase)
            || value.Contains("_adb._tcp", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        // Unextracted adb-<guid/service-instance> forms must never be display Serial.
        if (value.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^.+:\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointSerialRegex();

    private sealed class ConcurrentOnceSet {
        private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

        public bool TryAdd(string key) => _keys.TryAdd(key, 0);
    }
}
