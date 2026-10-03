namespace AdbView.Notifications;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AdbView.State;

/// <summary>
/// Pure transition detection + hold arming. No I/O, no timers, no Windows APIs.
/// Scheduling and re-check-at-due live in <see cref="NotificationCoordinator"/>.
/// </summary>
internal static class NotificationPolicy {
    // Decision 0019: 0.5 s, not 2.5 s. The device state is already instant everywhere else (push
    // tracker -> synchronous reduce -> immediate flyout/tray); only the toast waited, and 2.5 s
    // read as "the app is slow". Half a second still swallows a sub-second cable blip, which is
    // what the flap debounce exists for (ENGINEERING.md §8.2).
    public static readonly TimeSpan HoldConnected = TimeSpan.FromSeconds(0.5);
    public static readonly TimeSpan HoldDisconnected = TimeSpan.FromSeconds(0.5);
    public static readonly TimeSpan HoldAuthorization = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan HoldDeviceProblem = TimeSpan.FromSeconds(0.5);
    public static readonly TimeSpan HoldPairingAvailable = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan HoldAdbUnavailable = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HoldDiscoveryBroken = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan DedupCooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PairingOfferCooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StartupSuppress = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ResumeSuppress = TimeSpan.FromSeconds(8);
    public const int BurstIndividualCap = 3;
    public const string GlobalSubject = "global";

    /// <summary>
    /// Detect transitions on an accepted snapshot. Does not emit; arms holds or
    /// returns immediate PairingResult intents.
    /// </summary>
    public static NotificationDecision Evaluate(
        AppSnapshot? previous,
        AppSnapshot next,
        NotifierState state,
        DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(state);

        var nextState = state;

        // Clear outage latch when health leaves AdbUnavailable.
        if (next.Health.Kind != GlobalHealthKind.AdbUnavailable && state.AdbUnavailableOutageNotified) {
            nextState = nextState with { AdbUnavailableOutageNotified = false };
        }

        // Clear the discovery-episode latch when the health card clears (decision 0008 amendment):
        // a later episode may notify again.
        if (next.DiscoveryHealth is null && previous?.DiscoveryHealth is not null && state.DiscoveryBrokenNotified) {
            nextState = nextState with { DiscoveryBrokenNotified = false };
        }

        if (state.ShuttingDown || state.Suspended) {
            return new NotificationDecision(
                ImmutableArray<PendingHold>.Empty,
                ImmutableArray<NotificationKey>.Empty,
                ImmutableArray<NotificationIntent>.Empty,
                nextState);
        }

        // Startup / resume suppress: absorb into baseline — no pending, no deferred surprise.
        if (now < state.SuppressUntil) {
            return new NotificationDecision(
                ImmutableArray<PendingHold>.Empty,
                ImmutableArray<NotificationKey>.Empty,
                ImmutableArray<NotificationIntent>.Empty,
                nextState with { BaselineEstablished = true });
        }

        if (previous is null || !state.BaselineEstablished) {
            return new NotificationDecision(
                ImmutableArray<PendingHold>.Empty,
                ImmutableArray<NotificationKey>.Empty,
                ImmutableArray<NotificationIntent>.Empty,
                nextState with { BaselineEstablished = true });
        }

        var arm = ImmutableArray.CreateBuilder<PendingHold>();
        var cancel = ImmutableArray.CreateBuilder<NotificationKey>();
        var immediate = ImmutableArray.CreateBuilder<NotificationIntent>();

        DetectDeviceTransitions(previous, next, now, arm, cancel);
        DetectPairingOffer(previous, next, now, arm, cancel);
        DetectPairingResult(previous, next, immediate);
        DetectAdbUnavailable(previous, next, now, nextState, arm, cancel);
        DetectDiscoveryBroken(previous, next, now, nextState, arm, cancel);

        return new NotificationDecision(
            arm.ToImmutable(),
            cancel.ToImmutable(),
            immediate.ToImmutable(),
            nextState with { BaselineEstablished = true });
    }

    /// <summary>
    /// At hold expiry: emit only if the transition is still true on <paramref name="current"/>.
    /// </summary>
    public static NotificationIntent? TryMaterializeHold(
        PendingHold hold,
        AppSnapshot current,
        NotifierState state,
        DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(state);

        if (state.ShuttingDown || state.Suspended || now < state.SuppressUntil) {
            return null;
        }

        if (!IsHoldStillValid(hold.Key, current)) {
            return null;
        }

        if (IsDedupBlocked(hold.Key, state, now)) {
            return null;
        }

        if (hold.Key.Kind == NotificationKind.PairingAvailable
            && IsPairingOfferCooldownBlocked(hold.Key.Subject, state, now)) {
            return null;
        }

        if (hold.Key.Kind == NotificationKind.AdbUnavailable && state.AdbUnavailableOutageNotified) {
            return null;
        }

        if (hold.Key.Kind == NotificationKind.DiscoveryBroken && state.DiscoveryBrokenNotified) {
            return null;
        }

        return BuildIntent(hold.Key, current);
    }

    public static bool IsDedupBlocked(NotificationKey key, NotifierState state, DateTimeOffset now) {
        if (state.LastEmittedAt.TryGetValue(key.Composite, out DateTimeOffset last)
            && now - last < DedupCooldown) {
            return true;
        }

        return false;
    }

    public static bool IsPairingOfferCooldownBlocked(string offerId, NotifierState state, DateTimeOffset now) {
        if (state.LastPairingOfferEmittedAt.TryGetValue(offerId, out DateTimeOffset last)
            && now - last < PairingOfferCooldown) {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Apply ≤3 / 10s burst: emit up to 3 individuals; excess collapses into one
    /// <see cref="NotificationKind.DevicesChanged"/> with N = excess count.
    /// PairingResult bypasses burst accounting.
    /// </summary>
    public static (ImmutableArray<NotificationIntent> ToEmit, NotifierState NextState) ApplyBurstAndRecord(
        ImmutableArray<NotificationIntent> candidates,
        NotifierState state,
        DateTimeOffset now) {
        var next = PruneBurstWindow(state, now);
        var emit = ImmutableArray.CreateBuilder<NotificationIntent>();
        var excess = ImmutableArray.CreateBuilder<NotificationIntent>();
        int usedInWindow = next.RecentBurstEmits.Length;

        foreach (NotificationIntent intent in candidates) {
            if (intent.Key.Kind == NotificationKind.PairingResult) {
                if (IsDedupBlocked(intent.Key, next, now)) {
                    continue;
                }

                emit.Add(intent);
                next = RecordEmit(next, intent, now, countsTowardBurst: false);
                continue;
            }

            if (IsDedupBlocked(intent.Key, next, now)) {
                continue;
            }

            if (intent.Key.Kind == NotificationKind.PairingAvailable
                && IsPairingOfferCooldownBlocked(intent.Key.Subject, next, now)) {
                continue;
            }

            if (intent.Key.Kind == NotificationKind.AdbUnavailable && next.AdbUnavailableOutageNotified) {
                continue;
            }

            if (intent.Key.Kind == NotificationKind.DiscoveryBroken && next.DiscoveryBrokenNotified) {
                continue;
            }

            if (usedInWindow < BurstIndividualCap) {
                emit.Add(intent);
                next = RecordEmit(next, intent, now, countsTowardBurst: true);
                usedInWindow++;
            }
            else {
                excess.Add(intent);
            }
        }

        if (excess.Count > 0) {
            int n = excess.Count;
            var aggregate = new NotificationIntent(
                new NotificationKey(NotificationKind.DevicesChanged, GlobalSubject),
                Vocabulary.Notifications.DevicesChangedTitle(n),
                Vocabulary.Notifications.DevicesChangedBody,
                NotificationSeverity.Info);

            // Aggregate itself counts as one burst slot emit; still record for dedup.
            if (!IsDedupBlocked(aggregate.Key, next, now)) {
                emit.Add(aggregate);
                next = RecordEmit(next, aggregate, now, countsTowardBurst: true);
            }

            // Mark collapsed individuals as emitted for 60s dedup so they don't re-fire alone.
            foreach (NotificationIntent collapsed in excess) {
                next = next with {
                    LastEmittedAt = next.LastEmittedAt.SetItem(collapsed.Key.Composite, now),
                };
                if (collapsed.Key.Kind == NotificationKind.PairingAvailable) {
                    next = next with {
                        LastPairingOfferEmittedAt =
                            next.LastPairingOfferEmittedAt.SetItem(collapsed.Key.Subject, now),
                    };
                }

                if (collapsed.Key.Kind == NotificationKind.AdbUnavailable) {
                    next = next with { AdbUnavailableOutageNotified = true };
                }

                if (collapsed.Key.Kind == NotificationKind.DiscoveryBroken) {
                    next = next with { DiscoveryBrokenNotified = true };
                }
            }
        }

        return (emit.ToImmutable(), next);
    }

    public static NotifierState ForResume(NotifierState state, DateTimeOffset now) =>
        state with {
            Suspended = false,
            SuppressUntil = now.Add(ResumeSuppress),
            BaselineEstablished = true,
            // Pending holds cleared by coordinator; outage latch kept if still unavailable.
        };

    public static NotifierState ForSuspend(NotifierState state) =>
        state with { Suspended = true };

    public static NotifierState ForShutdown(NotifierState state) =>
        state with { ShuttingDown = true, Suspended = true };

    private static NotifierState PruneBurstWindow(NotifierState state, DateTimeOffset now) {
        var kept = state.RecentBurstEmits
            .Where(t => now - t < BurstWindow)
            .ToImmutableArray();
        return state with { RecentBurstEmits = kept };
    }

    private static NotifierState RecordEmit(
        NotifierState state,
        NotificationIntent intent,
        DateTimeOffset now,
        bool countsTowardBurst) {
        var next = state with {
            LastEmittedAt = state.LastEmittedAt.SetItem(intent.Key.Composite, now),
        };

        if (intent.Key.Kind == NotificationKind.PairingAvailable) {
            next = next with {
                LastPairingOfferEmittedAt =
                    next.LastPairingOfferEmittedAt.SetItem(intent.Key.Subject, now),
            };
        }

        if (intent.Key.Kind == NotificationKind.AdbUnavailable) {
            next = next with { AdbUnavailableOutageNotified = true };
        }

        if (intent.Key.Kind == NotificationKind.DiscoveryBroken) {
            next = next with { DiscoveryBrokenNotified = true };
        }

        if (countsTowardBurst) {
            next = next with { RecentBurstEmits = next.RecentBurstEmits.Add(now) };
        }

        return next;
    }

    private static void DetectDeviceTransitions(
        AppSnapshot previous,
        AppSnapshot next,
        DateTimeOffset now,
        ImmutableArray<PendingHold>.Builder arm,
        ImmutableArray<NotificationKey>.Builder cancel) {
        var prevById = previous.Devices.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var nextById = next.Devices.ToDictionary(d => d.Id, StringComparer.Ordinal);

        foreach (DeviceView device in next.Devices) {
            prevById.TryGetValue(device.Id, out DeviceView? before);

            if (device.State == DeviceStateKind.Connected
                && (before is null || before.State != DeviceStateKind.Connected)) {
                Arm(arm, new NotificationKey(NotificationKind.DeviceConnected, device.Id), now, HoldConnected);
            }
            else if (device.State != DeviceStateKind.Connected) {
                Cancel(cancel, new NotificationKey(NotificationKind.DeviceConnected, device.Id));
            }

            if (device.State == DeviceStateKind.Unauthorized
                && (before is null || before.State != DeviceStateKind.Unauthorized)) {
                Arm(arm, new NotificationKey(NotificationKind.AuthorizationRequired, device.Id), now, HoldAuthorization);
            }
            else if (device.State != DeviceStateKind.Unauthorized) {
                Cancel(cancel, new NotificationKey(NotificationKind.AuthorizationRequired, device.Id));
            }

            if (device.State == DeviceStateKind.Offline
                && (before is null || before.State != DeviceStateKind.Offline)) {
                Arm(arm, new NotificationKey(NotificationKind.DeviceProblem, device.Id), now, HoldDeviceProblem);
            }
            else if (device.State != DeviceStateKind.Offline) {
                Cancel(cancel, new NotificationKey(NotificationKind.DeviceProblem, device.Id));
            }
        }

        foreach (DeviceView was in previous.Devices) {
            if (!nextById.ContainsKey(was.Id)) {
                // Presence ended. Cancel in-flight per-device holds for this id.
                Cancel(cancel, new NotificationKey(NotificationKind.DeviceConnected, was.Id));
                Cancel(cancel, new NotificationKey(NotificationKind.AuthorizationRequired, was.Id));
                Cancel(cancel, new NotificationKey(NotificationKind.DeviceProblem, was.Id));

                // Connected → absent (ENGINEERING §8) and Offline → absent (ADB often
                // reports offline briefly before dropping a transport, e.g. adb disconnect).
                if (was.State == DeviceStateKind.Connected
                    || was.State == DeviceStateKind.Offline) {
                    Arm(
                        arm,
                        new NotificationKey(NotificationKind.DeviceDisconnected, was.Id),
                        now,
                        HoldDisconnected,
                        was.DisplayName);
                }
            }
            else if (nextById.ContainsKey(was.Id)) {
                Cancel(cancel, new NotificationKey(NotificationKind.DeviceDisconnected, was.Id));
            }

            if (!nextById.TryGetValue(was.Id, out DeviceView? nowDev)
                || nowDev.State != DeviceStateKind.Connected) {
                Cancel(cancel, new NotificationKey(NotificationKind.DeviceConnected, was.Id));
            }
        }
    }

    private static void DetectPairingOffer(
        AppSnapshot previous,
        AppSnapshot next,
        DateTimeOffset now,
        ImmutableArray<PendingHold>.Builder arm,
        ImmutableArray<NotificationKey>.Builder cancel) {
        var prevIds = previous.PairingOffers.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var nextIds = next.PairingOffers.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

        foreach (PairingOfferView offer in next.PairingOffers) {
            if (!prevIds.Contains(offer.Id)) {
                Arm(arm, new NotificationKey(NotificationKind.PairingAvailable, offer.Id), now, HoldPairingAvailable);
            }
        }

        foreach (string id in prevIds) {
            if (!nextIds.Contains(id)) {
                Cancel(cancel, new NotificationKey(NotificationKind.PairingAvailable, id));
            }
        }
    }

    private static void DetectPairingResult(
        AppSnapshot previous,
        AppSnapshot next,
        ImmutableArray<NotificationIntent>.Builder immediate) {
        PairingOperationView? cur = next.Pairing;
        if (cur is null) {
            return;
        }

        if (cur.Phase is not (PairingPhase.PairedWaitingForDevice or PairingPhase.Failed)) {
            return;
        }

        PairingOperationView? before = previous.Pairing;
        if (before is not null
            && before.OfferId == cur.OfferId
            && before.Phase == cur.Phase) {
            return;
        }

        // Entering success/fail from a different phase (or new op).
        if (before is not null
            && before.OfferId == cur.OfferId
            && before.Phase is PairingPhase.PairedWaitingForDevice or PairingPhase.Failed
            && before.Phase == cur.Phase) {
            return;
        }

        bool entered =
            before is null
            || before.OfferId != cur.OfferId
            || before.Phase != cur.Phase;

        if (!entered) {
            return;
        }

        if (cur.Phase == PairingPhase.PairedWaitingForDevice) {
            immediate.Add(new NotificationIntent(
                new NotificationKey(NotificationKind.PairingResult, cur.OfferId),
                Vocabulary.Notifications.PairingSuccessTitle(cur.DisplayName),
                Vocabulary.Notifications.PairingSuccessBody,
                NotificationSeverity.Info));
        }
        else {
            immediate.Add(new NotificationIntent(
                new NotificationKey(NotificationKind.PairingResult, cur.OfferId),
                Vocabulary.Notifications.PairingFailedTitle,
                // Same cause-selected line the flyout card shows (decision 0007);
                // a missing detail falls back to the frozen wrong-code line.
                cur.Detail ?? Vocabulary.Pairing.FailedDetail,
                NotificationSeverity.Warning));
        }
    }

    private static void DetectAdbUnavailable(
        AppSnapshot previous,
        AppSnapshot next,
        DateTimeOffset now,
        NotifierState state,
        ImmutableArray<PendingHold>.Builder arm,
        ImmutableArray<NotificationKey>.Builder cancel) {
        var key = new NotificationKey(NotificationKind.AdbUnavailable, GlobalSubject);
        if (next.Health.Kind == GlobalHealthKind.AdbUnavailable
            && previous.Health.Kind != GlobalHealthKind.AdbUnavailable
            && !state.AdbUnavailableOutageNotified) {
            Arm(arm, key, now, HoldAdbUnavailable);
        }
        else if (next.Health.Kind != GlobalHealthKind.AdbUnavailable) {
            Cancel(cancel, key);
        }
    }

    /// <summary>
    /// Decision 0008 amendment: one balloon per broken-discovery episode. Edge-triggered on the
    /// card appearing; cancelled if the card clears during the hold; latched once per episode
    /// (latch cleared when the card clears, so a later episode notifies again).
    /// </summary>
    private static void DetectDiscoveryBroken(
        AppSnapshot previous,
        AppSnapshot next,
        DateTimeOffset now,
        NotifierState state,
        ImmutableArray<PendingHold>.Builder arm,
        ImmutableArray<NotificationKey>.Builder cancel) {
        var key = new NotificationKey(NotificationKind.DiscoveryBroken, GlobalSubject);
        if (next.DiscoveryHealth is not null
            && previous.DiscoveryHealth is null
            && !state.DiscoveryBrokenNotified) {
            Arm(arm, key, now, HoldDiscoveryBroken);
        }
        else if (next.DiscoveryHealth is null) {
            Cancel(cancel, key);
        }
    }

    private static void Arm(
        ImmutableArray<PendingHold>.Builder arm,
        NotificationKey key,
        DateTimeOffset now,
        TimeSpan hold,
        string? displayNameHint = null) {
        arm.Add(new PendingHold(key, now.Add(hold), now, displayNameHint));
    }

    private static void Cancel(ImmutableArray<NotificationKey>.Builder cancel, NotificationKey key) {
        cancel.Add(key);
    }

    private static bool IsHoldStillValid(NotificationKey key, AppSnapshot current) {
        switch (key.Kind) {
            case NotificationKind.DeviceConnected: {
                    DeviceView? d = current.Devices.FirstOrDefault(x => x.Id == key.Subject);
                    return d is not null && d.State == DeviceStateKind.Connected;
                }

            case NotificationKind.DeviceDisconnected:
                return !current.Devices.Any(x => x.Id == key.Subject);

            case NotificationKind.AuthorizationRequired: {
                    DeviceView? d = current.Devices.FirstOrDefault(x => x.Id == key.Subject);
                    return d is not null && d.State == DeviceStateKind.Unauthorized;
                }

            case NotificationKind.DeviceProblem: {
                    DeviceView? d = current.Devices.FirstOrDefault(x => x.Id == key.Subject);
                    return d is not null && d.State == DeviceStateKind.Offline;
                }

            case NotificationKind.PairingAvailable:
                return current.PairingOffers.Any(o => o.Id == key.Subject);

            case NotificationKind.AdbUnavailable:
                return current.Health.Kind == GlobalHealthKind.AdbUnavailable;

            case NotificationKind.DiscoveryBroken:
                return current.DiscoveryHealth is not null;

            default:
                return false;
        }
    }

    private static NotificationIntent? BuildIntent(NotificationKey key, AppSnapshot current) {
        switch (key.Kind) {
            case NotificationKind.DeviceConnected: {
                    DeviceView? d = current.Devices.FirstOrDefault(x => x.Id == key.Subject);
                    if (d is null) {
                        return null;
                    }

                    return new NotificationIntent(
                        key,
                        Vocabulary.Notifications.DeviceConnectedTitle(d.DisplayName),
                        Vocabulary.Notifications.DeviceConnectedBody(d.TransportLabel),
                        NotificationSeverity.Info);
                }

            case NotificationKind.DeviceDisconnected:
                return null; // coordinator uses DisplayNameHint

            case NotificationKind.AuthorizationRequired: {
                    DeviceView? d = current.Devices.FirstOrDefault(x => x.Id == key.Subject);
                    if (d is null) {
                        return null;
                    }

                    return new NotificationIntent(
                        key,
                        Vocabulary.Notifications.AuthorizationRequiredTitle(d.DisplayName),
                        Vocabulary.Notifications.AuthorizationRequiredBody,
                        NotificationSeverity.Warning);
                }

            case NotificationKind.DeviceProblem: {
                    DeviceView? d = current.Devices.FirstOrDefault(x => x.Id == key.Subject);
                    if (d is null) {
                        return null;
                    }

                    return new NotificationIntent(
                        key,
                        Vocabulary.Notifications.DeviceProblemTitle(d.DisplayName),
                        Vocabulary.Notifications.DeviceProblemBody,
                        NotificationSeverity.Warning);
                }

            case NotificationKind.PairingAvailable: {
                    PairingOfferView? o = current.PairingOffers.FirstOrDefault(x => x.Id == key.Subject);
                    if (o is null) {
                        return null;
                    }

                    return new NotificationIntent(
                        key,
                        Vocabulary.Notifications.PairingAvailableTitle,
                        Vocabulary.Notifications.PairingAvailableBody(o.DisplayName),
                        NotificationSeverity.Info);
                }

            case NotificationKind.AdbUnavailable:
                return new NotificationIntent(
                    key,
                    Vocabulary.Notifications.AdbUnavailableTitle,
                    Vocabulary.Notifications.AdbUnavailableBody,
                    NotificationSeverity.Error);

            case NotificationKind.DiscoveryBroken when current.DiscoveryHealth is not null:
                // Same copy as the card (§32 same terminology; decision 0008 amendment).
                return new NotificationIntent(
                    key,
                    current.DiscoveryHealth.Title,
                    current.DiscoveryHealth.Detail,
                    NotificationSeverity.Warning);

            default:
                return null;
        }
    }

    public static NotificationIntent BuildDisconnectedIntent(NotificationKey key, string displayName) =>
        new(
            key,
            Vocabulary.Notifications.DeviceDisconnectedTitle(displayName),
            string.Empty,
            NotificationSeverity.Info);
}
