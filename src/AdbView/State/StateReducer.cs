namespace AdbView.State;

using System;
using AdbView.Adb.Model;

internal static class StateReducer {
    public const long MaxJsSafeInteger = 9007199254740991L; // 2^53 - 1

    public static AppSnapshot Reduce(AppSnapshot current, AdbViewEvent evt, DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(evt);

        return evt switch {
            AdbServerStateChangedEvent serverChanged => ReduceServerStateChanged(current, serverChanged.State, now),
            SetPinnedEvent setPinned => ReduceSetPinned(current, setPinned.Pinned, now),
            _ => current,
        };
    }

    private static AppSnapshot ReduceServerStateChanged(AppSnapshot current, AdbServerStateChanged state, DateTimeOffset now) {
        var targetHealth = MapHealth(state.Phase, current);

        if (current.Health == targetHealth) {
            return current;
        }

        return current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Health = targetHealth,
        };
    }

    private static AppSnapshot ReduceSetPinned(AppSnapshot current, bool pinned, DateTimeOffset now) {
        if (current.Shell.Pinned == pinned) {
            return current;
        }

        return current with {
            Revision = NextRevision(current.Revision),
            GeneratedAt = now,
            Shell = current.Shell with { Pinned = pinned },
        };
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

        string trayTooltip = current != null && !current.Devices.IsEmpty
            ? current.Health.TrayTooltip
            : Vocabulary.Health.NoDevicesTrayTooltip;

        TrayStatusKind trayKind = current != null && !current.Devices.IsEmpty
            ? current.Health.Tray
            : TrayStatusKind.Idle;

        return new HealthView {
            Kind = kind,
            Title = title,
            Detail = detail,
            Hint = null,
            Tray = trayKind,
            TrayTooltip = trayTooltip,
        };
    }

    public static long NextRevision(long currentRevision) {
        if (currentRevision >= MaxJsSafeInteger) {
            throw new InvalidOperationException(
                $"AppSnapshot revision {currentRevision} has reached JavaScript safe integer limit ({MaxJsSafeInteger}). Further mutations are rejected to preserve latest-wins ordering invariant.");
        }

        return currentRevision + 1;
    }
}
