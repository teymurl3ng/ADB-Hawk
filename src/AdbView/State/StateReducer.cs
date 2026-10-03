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
    public static readonly TimeSpan QrSessionTimeout = TimeSpan.FromMinutes(2); // decision 0010

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

        var result = evt switch {
            AdbServerStateChangedEvent serverChanged =>
                ReduceServerStateChanged(current, deviceState, serverChanged.State, now),
            SetPinnedEvent setPinned =>
                ReduceSetPinned(current, deviceState, setPinned.Pinned, now),
            ShellAppearanceChangedEvent appearance =>
                ReduceShellAppearance(current, deviceState, appearance, now),
            DeviceListReceivedEvent list =>
                ReduceDeviceList(current, deviceState, list, now),
            DeviceTrackerFailedEvent =>
                ReduceTrackerFailed(current, deviceState),
            DeviceTrackerGraceExpiredEvent grace =>
                ReduceGraceExpired(current, deviceState, grace.Generation, now),
            MdnsServicesReceivedEvent mdns =>
                ReduceMdnsServices(current, deviceState, mdns, now),
            MdnsFallbackReceivedEvent fallback =>
                ReduceMdnsFallback(current, deviceState, fallback, now),
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
            MetadataReceivedEvent metadata =>
                ReduceMetadataReceived(current, deviceState, metadata, now),
            DiscoveryCheckStartedEvent =>
                ReduceDiscoveryCheckStarted(current, deviceState),
            DiscoveryDiagnosisReceivedEvent diagnosis =>
                ReduceDiscoveryDiagnosisReceived(current, deviceState, diagnosis),
            DiscoveryRepairCatalogEvent catalog =>
                ReduceDiscoveryRepairCatalog(current, deviceState, catalog),
            DiscoveryRepairStartedEvent repairStart =>
                ReduceDiscoveryRepairStarted(current, deviceState, repairStart),
            DiscoveryRepairFinishedEvent repairDone =>
                ReduceDiscoveryRepairFinished(current, deviceState, repairDone),
            PairingQrStartedEvent qrStart =>
                ReducePairingQrStarted(current, deviceState, qrStart, now),
            PairingQrClearedEvent qrClear =>
                ReducePairingQrCleared(current, deviceState, qrClear, now),
            _ => ReduceResult.Unchanged(current, deviceState),
        };

        // Decision 0008: the discovery-health trigger is re-evaluated after every event so the
        // card always reflects the latest devices / services / server state (event-driven; no timers).
        return ApplyDiscoveryHealth(result, now);
    }

    private static ReduceResult ReduceServerStateChanged(
        AppSnapshot current,
        DeviceEngineState deviceState,
        AdbServerStateChanged state,
        DateTimeOffset now) {
        var targetHealth = MapHealth(state.Phase, current);
        var nextState = deviceState with { ServerMdnsEnabled = state.Status?.MdnsEnabled };

        if (current.Health == targetHealth && deviceState.ServerMdnsEnabled == nextState.ServerMdnsEnabled) {
            return ReduceResult.Unchanged(current, nextState);
        }

        if (current.Health == targetHealth) {
            return ReduceResult.With(current, nextState);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Health = WithDeviceDerivedTray(targetHealth, current.Devices, current.PairingOffers),
        };

        return ReduceResult.With(next, nextState);
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

    private static ReduceResult ReduceShellAppearance(
        AppSnapshot current,
        DeviceEngineState deviceState,
        ShellAppearanceChangedEvent appearance,
        DateTimeOffset now) {
        var shell = current.Shell;
        if (shell.Theme == appearance.Theme
            && shell.TransparencyEnabled == appearance.TransparencyEnabled
            && shell.ForcedColors == appearance.ForcedColors
            && shell.ReducedMotion == appearance.ReducedMotion) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Shell = shell with {
                Theme = appearance.Theme,
                TransparencyEnabled = appearance.TransparencyEnabled,
                ForcedColors = appearance.ForcedColors,
                ReducedMotion = appearance.ReducedMotion,
            },
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

        var (sessions, nextGenCounter) = ReconcileMetadataSessions(
            list.Devices,
            deviceState.MetadataSessions,
            deviceState.NextMetadataGeneration,
            deviceState.LatestMdnsServices);

        // Decision 0011: identity enrichment (rule 2c hardware-serial resolution) consumes the
        // effective discovery set — adb authoritative, fallback fills the gap. Feeding only the
        // ADB source left Wi-Fi devices per-port identified on browse-broken hosts, rendering
        // one phone's two transports as two cards.
        var identityServices = EffectiveServices(
            deviceState.LatestMdnsServices,
            deviceState.LatestFallbackServices);

        var mapped = DeviceViewMapper.MapList(
            list.Devices,
            current.Devices,
            list.At,
            SharedIdentity,
            identityServices,
            preserveMetadata: id =>
                deviceState.MetadataSessions.TryGetValue(id, out var oldSession)
                && sessions.TryGetValue(id, out var newSession)
                && oldSession.Generation == newSession.Generation);

        string? countLabel = DeviceViewMapper.DeviceCountLabel(mapped.Length);
        var health = WithDeviceDerivedTray(current.Health, mapped, current.PairingOffers);

        bool devicesEqual = DevicesSemanticallyEqual(current.Devices, mapped);
        bool countEqual = current.DeviceCountLabel == countLabel;
        bool healthEqual = current.Health == health;

        PairingOperationView? pairing = current.Pairing;
        var nextState = deviceState with {
            LatestAppliedListAt = list.At,
            ActiveGraceGeneration = 0,
            MetadataSessions = sessions,
            NextMetadataGeneration = nextGenCounter,
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

        bool sessionsEqual = deviceState.MetadataSessions == sessions
            && deviceState.NextMetadataGeneration == nextGenCounter;

        if (devicesEqual && countEqual && healthEqual && pairingEqual && sessionsEqual) {
            return ReduceResult.With(current, nextState, grace: grace, pairingWait: pairingWait);
        }

        if (devicesEqual && countEqual && healthEqual && pairingEqual) {
            // Sessions changed (generation bump) without DeviceView delta — still publish targets via state.
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

    /// <summary>
    /// Allocates monotonic metadata generations. Never reuses a generation after removal/reappear
    /// or AdbSerial change. Overflow throws (no silent wrap).
    /// </summary>
    internal static (ImmutableDictionary<string, DeviceMetadataSession> Sessions, long NextCounter)
        ReconcileMetadataSessions(
            ImmutableArray<RawDevice> rawDevices,
            ImmutableDictionary<string, DeviceMetadataSession> previous,
            long nextCounter,
            ImmutableArray<RawMdnsService> mdnsServices) {
        if (mdnsServices.IsDefault) {
            mdnsServices = ImmutableArray<RawMdnsService>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, DeviceMetadataSession>(StringComparer.Ordinal);
        long counter = nextCounter;

        if (!rawDevices.IsDefaultOrEmpty) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (RawDevice raw in rawDevices) {
                NormalizedDevice normalized = SharedIdentity.Normalize(raw, mdnsServices);
                if (!seen.Add(normalized.Id)) {
                    continue;
                }

                bool connected = normalized.SemanticState == DeviceSemanticState.Connected;
                string adbSerial = normalized.AdbSerial;

                if (!previous.TryGetValue(normalized.Id, out DeviceMetadataSession? old)) {
                    long gen = AllocateMetadataGeneration(ref counter);
                    builder[normalized.Id] = new DeviceMetadataSession(gen, adbSerial, connected, null);
                    continue;
                }

                bool serialChanged = !string.Equals(old.AdbSerial, adbSerial, StringComparison.Ordinal);
                bool leftConnected = old.ConnectedEligible && !connected;
                bool enteredConnected = !old.ConnectedEligible && connected;

                if (serialChanged || leftConnected || enteredConnected) {
                    long gen = AllocateMetadataGeneration(ref counter);
                    builder[normalized.Id] = new DeviceMetadataSession(gen, adbSerial, connected, null);
                }
                else {
                    builder[normalized.Id] = old with {
                        AdbSerial = adbSerial,
                        ConnectedEligible = connected,
                    };
                }
            }
        }

        return (builder.ToImmutable(), counter);
    }

    internal static long AllocateMetadataGeneration(ref long nextCounter) {
        checked {
            nextCounter++;
        }

        if (nextCounter <= 0) {
            throw new InvalidOperationException("Metadata generation counter overflow.");
        }

        return nextCounter;
    }

    private static ReduceResult ReduceMetadataReceived(
        AppSnapshot current,
        DeviceEngineState deviceState,
        MetadataReceivedEvent evt,
        DateTimeOffset now) {
        if (string.IsNullOrEmpty(evt.DeviceId) || current.Devices.IsDefaultOrEmpty) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        int index = -1;
        for (int i = 0; i < current.Devices.Length; i++) {
            if (current.Devices[i].Id == evt.DeviceId) {
                index = i;
                break;
            }
        }

        if (index < 0) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        DeviceView device = current.Devices[index];
        int? battery = evt.Battery; // null clears
        string? model = evt.Model ?? device.Detail.ExactModel;
        string? version = evt.AndroidVersion ?? device.Detail.AndroidVersion;

        // Refine DisplayName only when a naming candidate arrived. Brand/manufacturer alone
        // must not overwrite an early tracker title with the generic fallback.
        bool namingProbe = evt.MarketingName is not null
            || evt.DeviceName is not null
            || evt.Model is not null;

        string displayName = namingProbe
            ? DeviceDisplayName.Resolve(
                marketingName: evt.MarketingName,
                deviceName: evt.DeviceName,
                model: model,
                brand: evt.Brand,
                manufacturer: evt.Manufacturer)
            : device.DisplayName;

        DateTimeOffset? observed = battery is null ? null : now;
        var nextState = deviceState;
        if (deviceState.MetadataSessions.TryGetValue(evt.DeviceId, out DeviceMetadataSession? session)) {
            nextState = deviceState with {
                MetadataSessions = deviceState.MetadataSessions.SetItem(
                    evt.DeviceId,
                    session with { BatteryObservedAt = observed }),
            };
        }

        if (device.BatteryPercent == battery
            && device.Detail.ExactModel == model
            && device.Detail.AndroidVersion == version
            && device.DisplayName == displayName) {
            // Values unchanged — still refresh BatteryObservedAt so successful
            // same-% battery probes reset the 30s/120s staleness clocks.
            return ReduceResult.With(current, nextState);
        }

        var updatedDetail = device.Detail with {
            ExactModel = model,
            AndroidVersion = version,
        };
        var updatedDevice = device with {
            BatteryPercent = battery,
            DisplayName = displayName,
            Detail = updatedDetail,
        };

        var devices = current.Devices.SetItem(index, updatedDevice);
        // Order unchanged — metadata must not reorder cards.
        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Devices = devices,
        };

        return ReduceResult.With(next, nextState);
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

        var nextState = deviceState with {
            ActiveGraceGeneration = 0,
            MetadataSessions = ImmutableDictionary<string, DeviceMetadataSession>.Empty,
        };
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

        // Decision 0008 follow-through: the ADB server stays authoritative. When its list is
        // empty, fallback rows fill the gap so offers/pairing survive a broken server browse.
        var effective = EffectiveServices(mdns.Services, deviceState.LatestFallbackServices);
        return ReduceOfferSource(
            current,
            deviceState,
            effective,
            mdns.Services,
            MdnsSourceKind.Adb,
            mdns.At,
            now);
    }

    private static ReduceResult ReduceMdnsFallback(
        AppSnapshot current,
        DeviceEngineState deviceState,
        MdnsFallbackReceivedEvent fallback,
        DateTimeOffset now) {
        if (deviceState.LatestAppliedFallbackAt is DateTimeOffset latest && fallback.At < latest) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var effective = EffectiveServices(deviceState.LatestMdnsServices, fallback.Services);
        return ReduceOfferSource(
            current,
            deviceState,
            effective,
            fallback.Services,
            MdnsSourceKind.Fallback,
            fallback.At,
            now);
    }

    /// <summary>Instance-level union (decision 0016): adb rows win per DiscoveryKey; fallback
    /// rows fill keys adb does not report (supersedes the whole-set rule of 0008/0011).</summary>
    private static ImmutableArray<RawMdnsService> EffectiveServices(
        ImmutableArray<RawMdnsService> adbServices,
        ImmutableArray<RawMdnsService> fallbackServices) =>
        MdnsEffectiveSet.Merge(adbServices, fallbackServices);

    internal enum MdnsSourceKind { Adb, Fallback }

    // ---------- QR pairing (decision 0010) ----------

    private static ReduceResult ReducePairingQrStarted(
        AppSnapshot current,
        DeviceEngineState deviceState,
        PairingQrStartedEvent evt,
        DateTimeOffset now) {
        if (deviceState.ActiveQrSessionGeneration != 0) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        long generation = deviceState.NextQrSessionGeneration;
        if (generation <= 0) {
            generation = 1;
        }

        var nextState = deviceState with {
            ActiveQrSessionGeneration = generation,
            NextQrSessionGeneration = checked(generation + 1),
        };
        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            PairingQr = evt.View,
        };
        return ReduceResult.With(next, nextState, pairingQr: GraceCommand.Schedule(generation));
    }

    private static ReduceResult ReducePairingQrCleared(
        AppSnapshot current,
        DeviceEngineState deviceState,
        PairingQrClearedEvent evt,
        DateTimeOffset now) {
        long active = deviceState.ActiveQrSessionGeneration;
        if (active == 0) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        // Generation 0 = user-initiated cancel / pairing handoff: clear the active session.
        // A timer expiry carries its generation and is ignored when stale.
        if (evt.Generation != 0 && evt.Generation != active) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var nextState = deviceState with { ActiveQrSessionGeneration = 0 };
        if (current.PairingQr is null) {
            return ReduceResult.With(current, nextState, pairingQr: GraceCommand.Cancel);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            PairingQr = null,
        };
        return ReduceResult.With(next, nextState, pairingQr: GraceCommand.Cancel);
    }

    /// <summary>
    /// Shared offer-source path: maps the effective service set into offers/bindings, applies
    /// offer-withdrawal, health/tray, and (adb source only) wireless-warning bookkeeping.
    /// </summary>
    private static ReduceResult ReduceOfferSource(
        AppSnapshot current,
        DeviceEngineState deviceState,
        ImmutableArray<RawMdnsService> effectiveServices,
        ImmutableArray<RawMdnsService> sourceServices,
        MdnsSourceKind source,
        DateTimeOffset sourceAt,
        DateTimeOffset now) {
        bool treatAsAdbSource = source == MdnsSourceKind.Adb;
        var (offers, bindings) = PairingOfferMapper.Map(effectiveServices, current.Devices);
        var health = WithDeviceDerivedTray(current.Health, current.Devices, offers);

        PairingOperationView? pairing = current.Pairing;
        GraceCommand pairingWait = GraceCommand.None;
        var nextPairingState = deviceState;

        // Decision 0014: Android closes the _adb-tls-pairing._tcp advertisement the moment
        // pairing completes — withdrawal during an in-flight (Pairing) or successful
        // (PairedWaitingForDevice) operation is expected phone behavior and must NOT clear the
        // operation, the preserved pairing host, or the wait (host:connect still needs to fire
        // from the preserved host; the in-flight host:pair result lands within its 30 s bound
        // and self-cleans the phase). Pre-success withdrawal while merely AWAITING the code
        // (phone left pairing mode, stale offer) cancels as before.
        bool operationActive = pairing is { Phase: PairingPhase.Pairing or PairingPhase.PairedWaitingForDevice };
        if (pairing is not null
            && !operationActive
            && !bindings.ContainsKey(pairing.OfferId)) {
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

        var warnings = treatAsAdbSource ? ClearWirelessWarnings(current.Warnings) : current.Warnings;
        bool offersEqual = PairingOfferMapper.OffersSemanticallyEqual(current.PairingOffers, offers);
        bool healthEqual = current.Health == health;
        bool warningsEqual = WarningsEqual(current.Warnings, warnings);
        bool pairingEqual = ReferenceEquals(pairing, current.Pairing)
            || (pairing is null && current.Pairing is null)
            || (pairing is not null && current.Pairing is not null && pairing == current.Pairing);

        var stamped = (source == MdnsSourceKind.Adb
            ? nextPairingState with {
                LatestAppliedMdnsAt = sourceAt,
                LatestMdnsServices = sourceServices,
            }
            : nextPairingState with {
                LatestAppliedFallbackAt = sourceAt,
                LatestFallbackServices = sourceServices,
            }) with { OfferBindings = bindings };
        var nextState = treatAsAdbSource
            ? stamped with {
                WirelessFailed = false,
                ActiveWirelessRestoringGeneration = 0,
                ActiveWirelessUnavailableGeneration = 0,
            }
            : stamped;

        GraceCommand cancelRestoring = treatAsAdbSource && deviceState.ActiveWirelessRestoringGeneration != 0
            ? GraceCommand.Cancel
            : GraceCommand.None;
        GraceCommand cancelUnavailable = treatAsAdbSource && deviceState.ActiveWirelessUnavailableGeneration != 0
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

        PairingOperationView? operation =
            PairingOperationMapper.Map(evt.OfferId, evt.Phase, evt.FailureKind, displayName);

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

    private static ReduceResult ReduceDiscoveryCheckStarted(
        AppSnapshot current,
        DeviceEngineState deviceState) {
        if (deviceState.DiscoveryCheckRunning) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        return ReduceResult.With(current, deviceState with { DiscoveryCheckRunning = true });
    }

    private static ReduceResult ReduceDiscoveryDiagnosisReceived(
        AppSnapshot current,
        DeviceEngineState deviceState,
        DiscoveryDiagnosisReceivedEvent evt) {
        var diagnosis = evt.Diagnosis with {
            PortHolders = Normalize(evt.Diagnosis.PortHolders),
            VirtualAdapters = Normalize(evt.Diagnosis.VirtualAdapters),
        };

        // Decision 0008 amendment: a measurement following a completed repair reports the
        // momentary outcome while the trigger is still broken; restoration clears the card
        // (and the line with it). The line is momentary: any later measurement clears it.
        string? resultLine = deviceState.AwaitingMeasureRepairId is null
            ? null
            : Vocabulary.Discovery.ResultStillBroken;

        var nextState = deviceState with {
            LatestDiscoveryDiagnosis = diagnosis,
            DiscoveryCheckRunning = false,
            AwaitingMeasureRepairId = null,
            RepairResultLine = resultLine,
        };
        return ReduceResult.With(current, nextState);

        static ImmutableArray<string> Normalize(ImmutableArray<string> values) =>
            values.IsDefault ? ImmutableArray<string>.Empty : values;
    }

    private static ReduceResult ReduceDiscoveryRepairCatalog(
        AppSnapshot current,
        DeviceEngineState deviceState,
        DiscoveryRepairCatalogEvent evt) {
        var repairs = evt.Repairs.IsDefault ? ImmutableArray<DiscoveryRepairDescriptor>.Empty : evt.Repairs;
        var nextState = deviceState with { RepairCatalog = repairs };
        return ReduceResult.With(current, nextState);
    }

    private static ReduceResult ReduceDiscoveryRepairStarted(
        AppSnapshot current,
        DeviceEngineState deviceState,
        DiscoveryRepairStartedEvent evt) {
        if (deviceState.RunningRepairId is not null) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        // A new attempt supersedes the previous momentary line.
        var nextState = deviceState with {
            RunningRepairId = evt.RepairId,
            RepairResultLine = null,
        };
        return ReduceResult.With(current, nextState);
    }

    private static ReduceResult ReduceDiscoveryRepairFinished(
        AppSnapshot current,
        DeviceEngineState deviceState,
        DiscoveryRepairFinishedEvent evt) {
        if (deviceState.RunningRepairId != evt.RepairId) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        bool measuresAfter = evt.Outcome == DiscoveryRepairOutcome.Completed
            && MeasuresAfterCompletion(deviceState, evt.RepairId);
        string? resultLine = evt.Outcome == DiscoveryRepairOutcome.Completed
            ? null // reported by the follow-up measurement (or the card clears on restoration)
            : Vocabulary.Discovery.ResultLineFor(evt.Outcome);

        var nextState = deviceState with {
            RunningRepairId = null,
            RepairResults = deviceState.RepairResults.SetItem(evt.RepairId, evt.Outcome),
            AwaitingMeasureRepairId = measuresAfter ? evt.RepairId : null,
            RepairResultLine = resultLine,
        };
        return ReduceResult.With(current, nextState);

        static bool MeasuresAfterCompletion(DeviceEngineState state, string repairId) {
            var descriptor = state.RepairCatalog.FirstOrDefault(r => r.Id == repairId);
            return descriptor is not null
                && descriptor.Kind is DiscoveryRepairActionKind.RestartAdb
                    or DiscoveryRepairActionKind.StopService
                    or DiscoveryRepairActionKind.DisableAdapter;
        }
    }

    /// <summary>
    /// Decision 0008 trigger: a connected Wi-Fi device must be announcing itself, so
    /// zero services on a healthy server (discovery enabled, tracker not failed, at least one
    /// complete service list received) is a measured discovery failure. Quiet-but-healthy
    /// networks never surface the card.
    /// </summary>
    private static bool IsDiscoveryBroken(AppSnapshot snapshot, DeviceEngineState deviceState) {
        if (snapshot.Health.Kind != GlobalHealthKind.Healthy) {
            return false;
        }

        if (deviceState.ServerMdnsEnabled != true || deviceState.WirelessFailed) {
            return false;
        }

        // A complete list must have arrived at least once — empty-before-first-frame is not evidence.
        if (deviceState.LatestAppliedMdnsAt is null) {
            return false;
        }

        if (!deviceState.LatestMdnsServices.IsDefaultOrEmpty) {
            return false;
        }

        return HasConnectedWifiDevice(snapshot.Devices);
    }

    private static bool HasConnectedWifiDevice(ImmutableArray<DeviceView> devices) {
        if (devices.IsDefaultOrEmpty) {
            return false;
        }

        for (int i = 0; i < devices.Length; i++) {
            if (devices[i].Transport == TransportKind.WiFi
                && devices[i].State == DeviceStateKind.Connected) {
                return true;
            }
        }

        return false;
    }

    private static ReduceResult ApplyDiscoveryHealth(ReduceResult result, DateTimeOffset now) {
        var snapshot = result.Snapshot;
        var deviceState = result.DeviceState;

        if (!IsDiscoveryBroken(snapshot, deviceState)) {
            if (snapshot.DiscoveryHealth is null
                && deviceState.LatestDiscoveryDiagnosis is null
                && !deviceState.DiscoveryCheckRunning
                && deviceState.RepairCatalog.IsEmpty
                && deviceState.RunningRepairId is null
                && deviceState.RepairResults.IsEmpty
                && deviceState.RepairResultLine is null) {
                return result;
            }

            // Evidence is momentary (RFC 6762 legacy responses live ≤10 s): drop it with the card.
            // Repair state belongs to the card episode and clears with it (fresh episode, fresh
            // catalog — the coordinator rebuilds offers on the next card).
            var clearedState = deviceState with {
                LatestDiscoveryDiagnosis = null,
                DiscoveryCheckRunning = false,
                RepairCatalog = ImmutableArray<DiscoveryRepairDescriptor>.Empty,
                RepairResults = ImmutableDictionary<string, DiscoveryRepairOutcome>.Empty,
                RunningRepairId = null,
                AwaitingMeasureRepairId = null,
                RepairResultLine = null,
            };
            var clearedSnapshot = snapshot with {
                Revision = NextRevision(snapshot.Revision),
                GeneratedAt = now,
                DiscoveryHealth = null,
            };
            return result with { Snapshot = clearedSnapshot, DeviceState = clearedState };
        }

        var view = BuildDiscoveryHealthView(snapshot, deviceState);
        if (DiscoveryHealthViewEquals(snapshot.DiscoveryHealth, view)) {
            return result;
        }

        var next = snapshot with {
            Revision = NextRevision(snapshot.Revision),
            GeneratedAt = now,
            DiscoveryHealth = view,
        };
        return result with { Snapshot = next };
    }

    private static DiscoveryHealthView BuildDiscoveryHealthView(
        AppSnapshot snapshot,
        DeviceEngineState deviceState) {
        string deviceName = Vocabulary.Pairing.DefaultDeviceName;
        for (int i = 0; i < snapshot.Devices.Length; i++) {
            var device = snapshot.Devices[i];
            if (device.Transport == TransportKind.WiFi && device.State == DeviceStateKind.Connected) {
                deviceName = device.DisplayName;
                break;
            }
        }

        var evidence = ImmutableArray.CreateBuilder<string>();
        if (deviceState.LatestDiscoveryDiagnosis is { } diagnosis) {
            switch (diagnosis.ProbeOutcome) {
                case DiscoveryProbeOutcome.Measured when diagnosis.AnnouncementCount > 0:
                    evidence.Add(Vocabulary.Discovery.MeasuredEvidence(diagnosis.AnnouncementCount));
                    break;
                case DiscoveryProbeOutcome.Measured:
                    evidence.Add(Vocabulary.Discovery.MeasuredNothingEvidence);
                    break;
                case DiscoveryProbeOutcome.Unavailable:
                    evidence.Add(Vocabulary.Discovery.ProbeUnavailableEvidence);
                    break;
            }

            if (diagnosis.PortHolders.Length == 1) {
                evidence.Add(Vocabulary.Discovery.PortHeldEvidence(diagnosis.PortHolders[0]));
            }
            else if (diagnosis.PortHolders.Length > 1) {
                evidence.Add(Vocabulary.Discovery.PortHoldersEvidence(diagnosis.PortHolders[0], diagnosis.PortHolders.Length - 1));
            }

            if (diagnosis.VirtualAdapters.Length == 1) {
                evidence.Add(Vocabulary.Discovery.VirtualAdaptersNamedEvidence(diagnosis.VirtualAdapters[0]));
            }
            else if (diagnosis.VirtualAdapters.Length > 1) {
                evidence.Add(Vocabulary.Discovery.VirtualAdaptersNamedEvidence(diagnosis.VirtualAdapters[0], diagnosis.VirtualAdapters[1]));
            }
        }

        var repairs = ImmutableArray.CreateBuilder<DiscoveryRepairCardAction>();
        if (!deviceState.RepairCatalog.IsDefaultOrEmpty) {
            foreach (var descriptor in deviceState.RepairCatalog) {
                // Completed actions leave the card; their undo entries are separate catalog items.
                if (deviceState.RepairResults.TryGetValue(descriptor.Id, out var outcome)
                    && outcome == DiscoveryRepairOutcome.Completed) {
                    continue;
                }

                bool running = deviceState.RunningRepairId == descriptor.Id;
                bool isAdmin = descriptor.Kind is not DiscoveryRepairActionKind.RestartAdb;
                repairs.Add(new DiscoveryRepairCardAction {
                    Id = descriptor.Id,
                    Label = Vocabulary.Discovery.RepairLabel(descriptor.Kind, descriptor.Target),
                    IsAdmin = isAdmin,
                    IsRunning = running,
                    RunningLabel = Vocabulary.Discovery.RepairRunningLabel(descriptor.Kind, descriptor.Target),
                });
            }
        }

        return new DiscoveryHealthView {
            Title = Vocabulary.Discovery.BrokenTitle,
            Detail = Vocabulary.Discovery.BrokenDetail(deviceName),
            Evidence = evidence.ToImmutable(),
            ActionLabel = Vocabulary.Discovery.CheckAgainActionLabel,
            CanCheckAgain = !deviceState.DiscoveryCheckRunning && deviceState.RunningRepairId is null,
            Repairs = repairs.ToImmutable(),
            ResultLine = deviceState.RepairResultLine,
        };
    }

    private static bool DiscoveryHealthViewEquals(DiscoveryHealthView? left, DiscoveryHealthView right) {
        if (left is null) {
            return false;
        }

        if (left.Title != right.Title
            || left.Detail != right.Detail
            || left.ActionLabel != right.ActionLabel
            || left.CanCheckAgain != right.CanCheckAgain
            || left.ResultLine != right.ResultLine
            || left.Evidence.IsDefault
            || left.Evidence.Length != right.Evidence.Length
            || left.Repairs.IsDefault
            || left.Repairs.Length != right.Repairs.Length) {
            return false;
        }

        for (int i = 0; i < left.Evidence.Length; i++) {
            if (left.Evidence[i] != right.Evidence[i]) {
                return false;
            }
        }

        for (int i = 0; i < left.Repairs.Length; i++) {
            if (left.Repairs[i] != right.Repairs[i]) {
                return false;
            }
        }

        return true;
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
