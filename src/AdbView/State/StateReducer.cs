namespace AdbView.State;

using System;
using System.Collections.Immutable;
using System.Linq;
using AdbView.Adb;
using AdbView.Adb.Model;
using DomainPhase = AdbView.Adb.Model.PairingPhase;

internal static class StateReducer {
    public const long MaxJsSafeInteger = 9007199254740991L; // 2^53 - 1
    public static readonly TimeSpan TrackerFailureGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan WirelessRestoringDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WirelessUnavailableDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PairingWaitTimeout = TimeSpan.FromSeconds(60);

    private static readonly DeviceIdentity SharedIdentity = new();

    public static AppSnapshot Reduce(AppSnapshot current, AdbViewEvent evt, DateTimeOffset now) {
        return ReduceWithDeviceState(current, DeviceEngineState.Empty, evt, now).Snapshot;
    }

    public static ReduceResult ReduceWithDeviceState(
        AppSnapshot current,
        DeviceEngineState deviceState,
        AdbViewEvent evt,
        DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(deviceState);
        ArgumentNullException.ThrowIfNull(evt);

        return evt switch {
            AdbServerStateChangedEvent serverChanged =>
                ReduceServerStateChanged(current, deviceState, serverChanged.State, now),
            SetPinnedEvent setPinned =>
                ReduceSetPinned(current, deviceState, setPinned.Pinned, now),
            DeviceListReceivedEvent list =>
                ReduceDeviceList(current, deviceState, list, now),
            DeviceTrackerFailedEvent =>
                ReduceTrackerFailed(current, deviceState),
            DeviceTrackerGraceExpiredEvent grace =>
                ReduceGraceExpired(current, deviceState, grace.Generation, now),
            MdnsServicesReceivedEvent mdns =>
                ReduceMdnsServices(current, deviceState, mdns, now),
            MdnsTrackerFailedEvent =>
                ReduceMdnsFailed(current, deviceState),
            WirelessDiscoveryRestoringExpiredEvent restoring =>
                ReduceWirelessRestoringExpired(current, deviceState, restoring.Generation, now),
            WirelessDiscoveryUnavailableExpiredEvent unavailable =>
                ReduceWirelessUnavailableExpired(current, deviceState, unavailable.Generation, now),
            PairingProgressedEvent pairing =>
                ReducePairingProgressed(current, deviceState, pairing, now),
            PairingWaitExpiredEvent wait =>
                ReducePairingWaitExpired(current, deviceState, wait.Generation, now),
            _ => ReduceResult.Unchanged(current, deviceState),
        };
    }

    private static ReduceResult ReduceServerStateChanged(
        AppSnapshot current,
        DeviceEngineState deviceState,
        AdbServerStateChanged state,
        DateTimeOffset now) {
        var targetHealth = MapHealth(state.Phase, current);

        if (current.Health == targetHealth) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Health = WithDeviceDerivedTray(targetHealth, current.Devices, current.PairingOffers),
        };

        return ReduceResult.With(next, deviceState);
    }

    private static ReduceResult ReduceSetPinned(
        AppSnapshot current,
        DeviceEngineState deviceState,
        bool pinned,
        DateTimeOffset now) {
        if (current.Shell.Pinned == pinned) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Shell = current.Shell with { Pinned = pinned },
        };

        return ReduceResult.With(next, deviceState);
    }

    private static ReduceResult ReduceDeviceList(
        AppSnapshot current,
        DeviceEngineState deviceState,
        DeviceListReceivedEvent list,
        DateTimeOffset now) {
        if (deviceState.LatestAppliedListAt is DateTimeOffset latest && list.At < latest) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var mapped = DeviceViewMapper.MapList(
            list.Devices,
            current.Devices,
            list.At,
            SharedIdentity,
            deviceState.LatestMdnsServices);

        string? countLabel = DeviceViewMapper.DeviceCountLabel(mapped.Length);
        var health = WithDeviceDerivedTray(current.Health, mapped, current.PairingOffers);

        bool devicesEqual = DevicesSemanticallyEqual(current.Devices, mapped);
        bool countEqual = current.DeviceCountLabel == countLabel;
        bool healthEqual = current.Health == health;

        PairingOperationView? pairing = current.Pairing;
        var nextState = deviceState with {
            LatestAppliedListAt = list.At,
            ActiveGraceGeneration = 0,
        };

        GraceCommand pairingWait = GraceCommand.None;
        if (pairing is { Phase: PairingPhase.PairedWaitingForDevice }
            && MatchesWaitingPairing(pairing, mapped, deviceState)) {
            pairing = null;
            nextState = nextState with {
                ActivePairingWaitGeneration = 0,
                ActivePairingOfferId = null,
                ActivePairingHost = null,
                ActivePairingSerial = null,
                ActivePairingDisplayName = null,
            };
            pairingWait = GraceCommand.Cancel;
        }

        GraceCommand grace = deviceState.ActiveGraceGeneration != 0
            ? GraceCommand.Cancel
            : GraceCommand.None;

        bool pairingEqual = ReferenceEquals(pairing, current.Pairing)
            || (pairing is null && current.Pairing is null)
            || (pairing is not null && current.Pairing is not null && pairing == current.Pairing);

        if (devicesEqual && countEqual && healthEqual && pairingEqual) {
            return ReduceResult.With(current, nextState, grace: grace, pairingWait: pairingWait);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Devices = mapped,
            DeviceCountLabel = countLabel,
            Health = health,
            Pairing = pairing,
        };

        return ReduceResult.With(next, nextState, grace: grace, pairingWait: pairingWait);
    }

    private static bool MatchesWaitingPairing(
        PairingOperationView pairing,
        ImmutableArray<DeviceView> devices,
        DeviceEngineState deviceState) {
        if (devices.IsDefaultOrEmpty) {
            return false;
        }

        string? host = deviceState.ActivePairingHost;
        string? serial = deviceState.ActivePairingSerial;

        foreach (DeviceView device in devices) {
            if (device.Transport != TransportKind.WiFi) {
                continue;
            }

            if (!string.IsNullOrEmpty(serial)
                && (string.Equals(device.Detail.Serial, serial, StringComparison.Ordinal)
                    || device.Id.Equals("wifi:" + serial, StringComparison.Ordinal))) {
                return true;
            }

            if (!string.IsNullOrEmpty(host)
                && !string.IsNullOrEmpty(device.Detail.Address)
                && AddressHostMatches(device.Detail.Address, host)) {
                return true;
            }

            // OfferId correspondence when serial was the only identity signal on the offer.
            if (!string.IsNullOrEmpty(pairing.OfferId)
                && device.Id.Contains(pairing.DisplayName, StringComparison.Ordinal)
                && false) {
                // Intentionally never match by display name alone.
            }
        }

        return false;
    }

    private static bool AddressHostMatches(string address, string host) {
        // address is "host:port" or "[ipv6]:port"
        if (address.StartsWith('[')) {
            int close = address.IndexOf(']', StringComparison.Ordinal);
            if (close > 1) {
                return string.Equals(address[1..close], host, StringComparison.OrdinalIgnoreCase);
            }
        }

        int colon = address.LastIndexOf(':');
        if (colon > 0) {
            return string.Equals(address[..colon], host, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(address, host, StringComparison.OrdinalIgnoreCase);
    }

    private static ReduceResult ReduceTrackerFailed(
        AppSnapshot current,
        DeviceEngineState deviceState) {
        long nextGen = deviceState.ActiveGraceGeneration + 1;
        if (nextGen <= 0) {
            nextGen = 1;
        }

        var nextState = deviceState with { ActiveGraceGeneration = nextGen };
        return ReduceResult.With(current, nextState, grace: GraceCommand.Schedule(nextGen));
    }

    private static ReduceResult ReduceGraceExpired(
        AppSnapshot current,
        DeviceEngineState deviceState,
        long generation,
        DateTimeOffset now) {
        if (generation == 0 || generation != deviceState.ActiveGraceGeneration) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        if (current.Devices.IsDefaultOrEmpty && current.DeviceCountLabel is null) {
            var clearedState = deviceState with { ActiveGraceGeneration = 0 };
            return ReduceResult.With(current, clearedState);
        }

        var empty = ImmutableArray<DeviceView>.Empty;
        var health = WithDeviceDerivedTray(current.Health, empty, current.PairingOffers);

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Devices = empty,
            DeviceCountLabel = null,
            Health = health,
        };

        var nextState = deviceState with { ActiveGraceGeneration = 0 };
        return ReduceResult.With(next, nextState);
    }

    private static ReduceResult ReduceMdnsServices(
        AppSnapshot current,
        DeviceEngineState deviceState,
        MdnsServicesReceivedEvent mdns,
        DateTimeOffset now) {
        if (deviceState.LatestAppliedMdnsAt is DateTimeOffset latest && mdns.At < latest) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var (offers, bindings) = PairingOfferMapper.Map(mdns.Services);
        var health = WithDeviceDerivedTray(current.Health, current.Devices, offers);

        PairingOperationView? pairing = current.Pairing;
        GraceCommand pairingWait = GraceCommand.None;
        var nextPairingState = deviceState;

        if (pairing is not null && !bindings.ContainsKey(pairing.OfferId)) {
            // Offer withdrawal clears the operation (ENGINEERING.md §6.8).
            pairing = null;
            nextPairingState = nextPairingState with {
                ActivePairingWaitGeneration = 0,
                ActivePairingOfferId = null,
                ActivePairingHost = null,
                ActivePairingSerial = null,
                ActivePairingDisplayName = null,
            };
            pairingWait = GraceCommand.Cancel;
        }

        var warnings = ClearWirelessWarnings(current.Warnings);
        bool offersEqual = PairingOfferMapper.OffersSemanticallyEqual(current.PairingOffers, offers);
        bool healthEqual = current.Health == health;
        bool warningsEqual = WarningsEqual(current.Warnings, warnings);
        bool pairingEqual = ReferenceEquals(pairing, current.Pairing)
            || (pairing is null && current.Pairing is null)
            || (pairing is not null && current.Pairing is not null && pairing == current.Pairing);

        var nextState = nextPairingState with {
            LatestAppliedMdnsAt = mdns.At,
            LatestMdnsServices = mdns.Services,
            OfferBindings = bindings,
            WirelessFailed = false,
            ActiveWirelessRestoringGeneration = 0,
            ActiveWirelessUnavailableGeneration = 0,
        };

        GraceCommand cancelRestoring = deviceState.ActiveWirelessRestoringGeneration != 0
            ? GraceCommand.Cancel
            : GraceCommand.None;
        GraceCommand cancelUnavailable = deviceState.ActiveWirelessUnavailableGeneration != 0
            ? GraceCommand.Cancel
            : GraceCommand.None;

        if (offersEqual && healthEqual && warningsEqual && pairingEqual) {
            return ReduceResult.With(
                current,
                nextState,
                pairingWait: pairingWait,
                wirelessRestoring: cancelRestoring,
                wirelessUnavailable: cancelUnavailable);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            PairingOffers = offers,
            Pairing = pairing,
            Health = health,
            Warnings = warnings,
        };

        return ReduceResult.With(
            next,
            nextState,
            pairingWait: pairingWait,
            wirelessRestoring: cancelRestoring,
            wirelessUnavailable: cancelUnavailable);
    }

    private static ReduceResult ReduceMdnsFailed(
        AppSnapshot current,
        DeviceEngineState deviceState) {
        // Do not flip global Health. Arm restoring (5s) and unavailable (60s) timers.
        long restoringGen = deviceState.ActiveWirelessRestoringGeneration + 1;
        if (restoringGen <= 0) {
            restoringGen = 1;
        }

        long unavailableGen = deviceState.ActiveWirelessUnavailableGeneration + 1;
        if (unavailableGen <= 0) {
            unavailableGen = 1;
        }

        var nextState = deviceState with {
            WirelessFailed = true,
            ActiveWirelessRestoringGeneration = restoringGen,
            ActiveWirelessUnavailableGeneration = unavailableGen,
        };

        return ReduceResult.With(
            current,
            nextState,
            wirelessRestoring: GraceCommand.Schedule(restoringGen),
            wirelessUnavailable: GraceCommand.Schedule(unavailableGen));
    }

    private static ReduceResult ReduceWirelessRestoringExpired(
        AppSnapshot current,
        DeviceEngineState deviceState,
        long generation,
        DateTimeOffset now) {
        if (generation == 0
            || generation != deviceState.ActiveWirelessRestoringGeneration
            || !deviceState.WirelessFailed) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        // If Unavailable already present, do not downgrade to Restoring.
        if (HasWirelessWarning(current.Warnings, Vocabulary.Wireless.UnavailableTitle)) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var warning = new SubsystemWarningView {
            Subsystem = SubsystemKind.WirelessDiscovery,
            Severity = SeverityKind.Informational,
            Title = Vocabulary.Wireless.RestoringTitle,
            Detail = Vocabulary.Wireless.RestoringDetail,
        };

        var warnings = ReplaceWirelessWarning(current.Warnings, warning);
        if (WarningsEqual(current.Warnings, warnings)) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Warnings = warnings,
        };

        return ReduceResult.With(next, deviceState);
    }

    private static ReduceResult ReduceWirelessUnavailableExpired(
        AppSnapshot current,
        DeviceEngineState deviceState,
        long generation,
        DateTimeOffset now) {
        if (generation == 0
            || generation != deviceState.ActiveWirelessUnavailableGeneration
            || !deviceState.WirelessFailed) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var warning = new SubsystemWarningView {
            Subsystem = SubsystemKind.WirelessDiscovery,
            Severity = SeverityKind.Attention,
            Title = Vocabulary.Wireless.UnavailableTitle,
            Detail = Vocabulary.Wireless.UnavailableDetail,
        };

        var warnings = ReplaceWirelessWarning(current.Warnings, warning);
        if (WarningsEqual(current.Warnings, warnings)) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Warnings = warnings,
        };

        return ReduceResult.With(next, deviceState);
    }

    private static ReduceResult ReducePairingProgressed(
        AppSnapshot current,
        DeviceEngineState deviceState,
        PairingProgressedEvent evt,
        DateTimeOffset now) {
        string displayName = evt.DisplayName
            ?? (deviceState.OfferBindings.TryGetValue(evt.OfferId, out var binding)
                ? binding.DisplayName
                : Vocabulary.Pairing.DefaultDeviceName);

        PairingOperationView? operation = PairingOperationMapper.Map(evt.OfferId, evt.Phase, displayName);

        GraceCommand pairingWait = GraceCommand.None;
        var nextState = deviceState;

        if (evt.Phase == DomainPhase.PairedWaitingForDevice) {
            long waitGen = deviceState.ActivePairingWaitGeneration + 1;
            if (waitGen <= 0) {
                waitGen = 1;
            }

            string? host = bindingHost(deviceState, evt.OfferId);
            string? serial = bindingSerial(deviceState, evt.OfferId);
            nextState = deviceState with {
                ActivePairingWaitGeneration = waitGen,
                ActivePairingOfferId = evt.OfferId,
                ActivePairingHost = host,
                ActivePairingSerial = serial,
                ActivePairingDisplayName = displayName,
            };
            pairingWait = GraceCommand.Schedule(waitGen);
        }
        else if (evt.Phase is DomainPhase.Cleared or DomainPhase.Failed or DomainPhase.AwaitingCode or DomainPhase.Pairing) {
            if (deviceState.ActivePairingWaitGeneration != 0) {
                pairingWait = GraceCommand.Cancel;
            }

            nextState = deviceState with {
                ActivePairingWaitGeneration = 0,
                ActivePairingOfferId = evt.Phase == DomainPhase.Cleared ? null : evt.OfferId,
                ActivePairingHost = evt.Phase == DomainPhase.Cleared ? null : bindingHost(deviceState, evt.OfferId),
                ActivePairingSerial = evt.Phase == DomainPhase.Cleared ? null : bindingSerial(deviceState, evt.OfferId),
                ActivePairingDisplayName = evt.Phase == DomainPhase.Cleared ? null : displayName,
            };
        }

        bool pairingEqual = (operation is null && current.Pairing is null)
            || (operation is not null && current.Pairing is not null && operation == current.Pairing);

        if (pairingEqual) {
            return ReduceResult.With(current, nextState, pairingWait: pairingWait);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Pairing = operation,
            // Devices intentionally untouched — paired ≠ connected.
        };

        return ReduceResult.With(next, nextState, pairingWait: pairingWait);

        static string? bindingHost(DeviceEngineState state, string offerId) =>
            state.OfferBindings.TryGetValue(offerId, out var b) ? b.Host : state.ActivePairingHost;

        static string? bindingSerial(DeviceEngineState state, string offerId) =>
            state.OfferBindings.TryGetValue(offerId, out var b) ? b.Serial : state.ActivePairingSerial;
    }

    private static ReduceResult ReducePairingWaitExpired(
        AppSnapshot current,
        DeviceEngineState deviceState,
        long generation,
        DateTimeOffset now) {
        if (generation == 0 || generation != deviceState.ActivePairingWaitGeneration) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        if (current.Pairing is not { Phase: PairingPhase.PairedWaitingForDevice }) {
            var cleared = deviceState with { ActivePairingWaitGeneration = 0 };
            return ReduceResult.With(current, cleared);
        }

        var nextState = deviceState with {
            ActivePairingWaitGeneration = 0,
            ActivePairingOfferId = null,
            ActivePairingHost = null,
            ActivePairingSerial = null,
            ActivePairingDisplayName = null,
        };

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Pairing = null,
        };

        return ReduceResult.With(next, nextState);
    }

    public static HealthView MapHealth(AdbServerPhase phase, AppSnapshot? current = null) {
        if (phase == AdbServerPhase.Unavailable) {
            return new HealthView {
                Kind = GlobalHealthKind.AdbUnavailable,
                Title = Vocabulary.Health.AdbUnavailableTitle,
                Detail = Vocabulary.Health.AdbUnavailableDetail,
                Hint = Vocabulary.Health.AdbUnavailableHint,
                Tray = TrayStatusKind.Failure,
                TrayTooltip = Vocabulary.Health.AdbUnavailableTrayTooltip,
            };
        }

        bool isRestoring = phase is AdbServerPhase.Probing or AdbServerPhase.Starting or AdbServerPhase.Restoring;
        var kind = isRestoring ? GlobalHealthKind.AdbRestoring : GlobalHealthKind.Healthy;
        var title = isRestoring ? Vocabulary.Health.AdbRestoringTitle : null;
        var detail = isRestoring ? Vocabulary.Health.AdbRestoringDetail : null;

        var devices = current?.Devices ?? ImmutableArray<DeviceView>.Empty;
        var offers = current?.PairingOffers ?? ImmutableArray<PairingOfferView>.Empty;
        var (tray, tooltip) = TrayStatusAggregator.Aggregate(kind, devices, offers);

        return new HealthView {
            Kind = kind,
            Title = title,
            Detail = detail,
            Hint = null,
            Tray = tray,
            TrayTooltip = tooltip,
        };
    }

    public static HealthView WithDeviceDerivedTray(
        HealthView health,
        ImmutableArray<DeviceView> devices,
        ImmutableArray<PairingOfferView> pairingOffers) {
        if (health.Kind == GlobalHealthKind.AdbUnavailable) {
            return health with {
                Tray = TrayStatusKind.Failure,
                TrayTooltip = Vocabulary.Health.AdbUnavailableTrayTooltip,
            };
        }

        var (tray, tooltip) = TrayStatusAggregator.Aggregate(health.Kind, devices, pairingOffers);
        return health with { Tray = tray, TrayTooltip = tooltip };
    }

    public static long NextRevision(long currentRevision) {
        if (currentRevision >= MaxJsSafeInteger) {
            throw new InvalidOperationException(
                $"AppSnapshot revision {currentRevision} has reached JavaScript safe integer limit ({MaxJsSafeInteger}). Further mutations are rejected to preserve latest-wins ordering invariant.");
        }

        return currentRevision + 1;
    }

    private static ImmutableArray<SubsystemWarningView> ClearWirelessWarnings(
        ImmutableArray<SubsystemWarningView> warnings) {
        if (warnings.IsDefaultOrEmpty) {
            return ImmutableArray<SubsystemWarningView>.Empty;
        }

        return warnings.Where(w => w.Subsystem != SubsystemKind.WirelessDiscovery).ToImmutableArray();
    }

    private static ImmutableArray<SubsystemWarningView> ReplaceWirelessWarning(
        ImmutableArray<SubsystemWarningView> warnings,
        SubsystemWarningView warning) {
        var builder = ImmutableArray.CreateBuilder<SubsystemWarningView>();
        if (!warnings.IsDefaultOrEmpty) {
            foreach (var w in warnings) {
                if (w.Subsystem != SubsystemKind.WirelessDiscovery) {
                    builder.Add(w);
                }
            }
        }

        builder.Add(warning);
        return builder.ToImmutable();
    }

    private static bool HasWirelessWarning(ImmutableArray<SubsystemWarningView> warnings, string title) {
        if (warnings.IsDefaultOrEmpty) {
            return false;
        }

        return warnings.Any(w =>
            w.Subsystem == SubsystemKind.WirelessDiscovery
            && string.Equals(w.Title, title, StringComparison.Ordinal));
    }

    private static bool WarningsEqual(
        ImmutableArray<SubsystemWarningView> left,
        ImmutableArray<SubsystemWarningView> right) {
        if (left.IsDefault) {
            left = ImmutableArray<SubsystemWarningView>.Empty;
        }

        if (right.IsDefault) {
            right = ImmutableArray<SubsystemWarningView>.Empty;
        }

        if (left.Length != right.Length) {
            return false;
        }

        for (int i = 0; i < left.Length; i++) {
            if (left[i] != right[i]) {
                return false;
            }
        }

        return true;
    }

    private static bool DevicesSemanticallyEqual(
        ImmutableArray<DeviceView> left,
        ImmutableArray<DeviceView> right) {
        if (left.IsDefault) {
            left = ImmutableArray<DeviceView>.Empty;
        }

        if (right.IsDefault) {
            right = ImmutableArray<DeviceView>.Empty;
        }

        if (left.Length != right.Length) {
            return false;
        }

        for (int i = 0; i < left.Length; i++) {
            if (!ReferenceEquals(left[i], right[i]) && !DeviceViewMapper.IsUnchanged(left[i], right[i])) {
                return false;
            }
        }

        return true;
    }
}
