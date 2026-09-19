namespace AdbView.State;

using System;
using System.Collections.Immutable;
using AdbView.Adb;
using AdbView.Adb.Model;

internal static class StateReducer {
    public const long MaxJsSafeInteger = 9007199254740991L; // 2^53 - 1
    public static readonly TimeSpan TrackerFailureGrace = TimeSpan.FromSeconds(2);

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

        // Device events do not own Health; health events do not rewrite Devices.
        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Health = WithDeviceDerivedTray(targetHealth, current.Devices, current.PairingOffers),
        };

        return new ReduceResult(next, deviceState, GraceCommand.None);
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

        return new ReduceResult(next, deviceState, GraceCommand.None);
    }

    private static ReduceResult ReduceDeviceList(
        AppSnapshot current,
        DeviceEngineState deviceState,
        DeviceListReceivedEvent list,
        DateTimeOffset now) {
        // Out-of-order: older than latest applied → ignore completely (ENGINEERING.md §7.3).
        // Equal At is not older → apply (reconcile; may no-op if semantically identical).
        if (deviceState.LatestAppliedListAt is DateTimeOffset latest && list.At < latest) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        var mapped = DeviceViewMapper.MapList(list.Devices, current.Devices, list.At, SharedIdentity);
        string? countLabel = DeviceViewMapper.DeviceCountLabel(mapped.Length);
        var health = WithDeviceDerivedTray(current.Health, mapped, current.PairingOffers);

        bool devicesEqual = DevicesSemanticallyEqual(current.Devices, mapped);
        bool countEqual = current.DeviceCountLabel == countLabel;
        bool healthEqual = current.Health == health;

        var nextState = deviceState with {
            LatestAppliedListAt = list.At,
            ActiveGraceGeneration = 0,
        };

        GraceCommand grace = deviceState.ActiveGraceGeneration != 0
            ? GraceCommand.Cancel
            : GraceCommand.None;

        if (devicesEqual && countEqual && healthEqual) {
            // Still advance LatestAppliedListAt / cancel grace even when snapshot is unchanged.
            return new ReduceResult(current, nextState, grace);
        }

        var next = current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Devices = mapped,
            DeviceCountLabel = countLabel,
            Health = health,
        };

        return new ReduceResult(next, nextState, grace);
    }

    private static ReduceResult ReduceTrackerFailed(
        AppSnapshot current,
        DeviceEngineState deviceState) {
        // Retain devices; arm/re-arm 2 s grace. Snapshot unchanged → no revision.
        long nextGen = deviceState.ActiveGraceGeneration + 1;
        if (nextGen <= 0) {
            nextGen = 1;
        }

        var nextState = deviceState with { ActiveGraceGeneration = nextGen };
        return new ReduceResult(current, nextState, GraceCommand.Schedule(nextGen));
    }

    private static ReduceResult ReduceGraceExpired(
        AppSnapshot current,
        DeviceEngineState deviceState,
        long generation,
        DateTimeOffset now) {
        // Stale grace token after recovery → ignore.
        if (generation == 0 || generation != deviceState.ActiveGraceGeneration) {
            return ReduceResult.Unchanged(current, deviceState);
        }

        if (current.Devices.IsDefaultOrEmpty && current.DeviceCountLabel is null) {
            var clearedState = deviceState with { ActiveGraceGeneration = 0 };
            return new ReduceResult(current, clearedState, GraceCommand.None);
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
        return new ReduceResult(next, nextState, GraceCommand.None);
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
            // Prefer reference equality (unchanged instances reused by mapper); fall back to content.
            if (!ReferenceEquals(left[i], right[i]) && !DeviceViewMapper.IsUnchanged(left[i], right[i])) {
                return false;
            }
        }

        return true;
    }
}
