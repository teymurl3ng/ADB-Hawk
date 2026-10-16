namespace AdbView.State;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

/// <summary>
/// Tray aggregate priority and tooltip wording (ENGINEERING.md §7.5, DESIGN.md §30–§31).
/// </summary>
internal static class TrayStatusAggregator {
    public const int MaxTooltipLength = 127;

    public static (TrayStatusKind Tray, string Tooltip) Aggregate(
        GlobalHealthKind healthKind,
        ImmutableArray<DeviceView> devices,
        ImmutableArray<PairingOfferView> pairingOffers) {
        if (healthKind == GlobalHealthKind.AdbUnavailable) {
            return (TrayStatusKind.Failure, Vocabulary.Health.AdbUnavailableTrayTooltip);
        }

        if (devices.IsDefaultOrEmpty && pairingOffers.IsDefaultOrEmpty) {
            return (TrayStatusKind.Idle, Vocabulary.Health.NoDevicesTrayTooltip);
        }

        bool anyConnected = devices.Any(d => d.State == DeviceStateKind.Connected);
        bool anyUnauthorized = devices.Any(d => d.State == DeviceStateKind.Unauthorized);
        bool anyOffline = devices.Any(d => d.State == DeviceStateKind.Offline);
        bool anySpecial = devices.Any(IsSpecial);
        bool anyPairing = !pairingOffers.IsDefaultOrEmpty;

        // Priority: Failure (handled) → Attention → PairingAvailable → Special → Connected → Idle
        if (anyUnauthorized || (anyOffline && !anyConnected)) {
            return (TrayStatusKind.Attention, BuildTooltip(TrayStatusKind.Attention, devices, pairingOffers));
        }

        if (anyPairing) {
            return (TrayStatusKind.PairingAvailable, BuildTooltip(TrayStatusKind.PairingAvailable, devices, pairingOffers));
        }

        if (anySpecial) {
            return (TrayStatusKind.Special, BuildTooltip(TrayStatusKind.Special, devices, pairingOffers));
        }

        if (anyConnected) {
            return (TrayStatusKind.Connected, BuildTooltip(TrayStatusKind.Connected, devices, pairingOffers));
        }

        return (TrayStatusKind.Idle, Vocabulary.Health.NoDevicesTrayTooltip);
    }

    internal static string TruncateAtWordBoundary(string text, int maxLength = MaxTooltipLength) {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxLength) {
            return text;
        }

        // Prefer cutting at the last whitespace that leaves a non-empty prefix within maxLength.
        int cut = text.LastIndexOf(' ', maxLength);
        if (cut <= 0) {
            return text[..maxLength];
        }

        return text[..cut].TrimEnd();
    }

    private static string BuildTooltip(
        TrayStatusKind tray,
        ImmutableArray<DeviceView> devices,
        ImmutableArray<PairingOfferView> pairingOffers) {
        int count = devices.Length;

        if (tray == TrayStatusKind.PairingAvailable && !pairingOffers.IsDefaultOrEmpty) {
            string offerName = pairingOffers[0].DisplayName;
            string tip = count >= 2
                ? $"Pairing available: {offerName} · {count} devices"
                : $"Pairing available: {offerName}";
            return TruncateAtWordBoundary(tip);
        }

        DeviceView? subject = SelectSubject(tray, devices);
        if (subject is null) {
            return Vocabulary.Health.NoDevicesTrayTooltip;
        }

        string label = tray switch {
            TrayStatusKind.Attention when subject.State == DeviceStateKind.Unauthorized
                => Vocabulary.Device.UnauthorizedStateLabel,
            TrayStatusKind.Attention when subject.State == DeviceStateKind.Offline
                => Vocabulary.Device.OfflineStateLabel,
            TrayStatusKind.Special => subject.StateLabel,
            TrayStatusKind.Connected => Vocabulary.Device.ConnectedStateLabel,
            _ => subject.StateLabel,
        };

        if (count <= 1) {
            if (tray == TrayStatusKind.Connected) {
                return TruncateAtWordBoundary(
                    $"Connected: {subject.DisplayName} ({subject.TransportLabel})");
            }

            return TruncateAtWordBoundary($"{label}: {subject.DisplayName}");
        }

        return TruncateAtWordBoundary($"{label}: {subject.DisplayName} · {count} devices");
    }

    private static DeviceView? SelectSubject(TrayStatusKind tray, ImmutableArray<DeviceView> devices) {
        IEnumerable<DeviceView> seq = devices;
        return tray switch {
            TrayStatusKind.Attention =>
                seq.FirstOrDefault(d => d.State == DeviceStateKind.Unauthorized)
                ?? seq.FirstOrDefault(d => d.State == DeviceStateKind.Offline),
            TrayStatusKind.Special => seq.FirstOrDefault(IsSpecial),
            TrayStatusKind.Connected => seq.FirstOrDefault(d => d.State == DeviceStateKind.Connected),
            _ => seq.FirstOrDefault(),
        };
    }

    private static bool IsSpecial(DeviceView d) =>
        d.State is DeviceStateKind.Recovery
            or DeviceStateKind.Bootloader
            or DeviceStateKind.Sideload
            or DeviceStateKind.Rescue;
}
