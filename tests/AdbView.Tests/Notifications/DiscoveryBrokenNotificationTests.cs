namespace AdbView.Tests.Notifications;

using System;
using System.Collections.Immutable;
using AdbView.Notifications;
using AdbView.State;
using Xunit;

public sealed class DiscoveryBrokenNotificationTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static AppSnapshot SnapshotWithCard(bool present) =>
        StateEngine.CreateDefaultSnapshot() with {
            GeneratedAt = T0,
            DiscoveryHealth = present
                ? new DiscoveryHealthView {
                    Title = "Wireless discovery isn't working",
                    Detail = "Galaxy S24 is connected over Wi-Fi, but ADB isn't receiving device announcements on this network.",
                    Evidence = ImmutableArray<string>.Empty,
                    ActionLabel = "Check again",
                    CanCheckAgain = true,
                    Repairs = ImmutableArray<DiscoveryRepairCardAction>.Empty,
                    ResultLine = null,
                }
                : null,
        };

    private static NotifierState ReadyState(bool notified = false) {
        var state = NotifierState.Create(T0.AddSeconds(-30));
        return state with {
            BaselineEstablished = true,
            SuppressUntil = DateTimeOffset.MinValue,
            DiscoveryBrokenNotified = notified,
        };
    }

    private static NotificationDecision Evaluate(AppSnapshot previous, AppSnapshot next, NotifierState state) =>
        NotificationPolicy.Evaluate(previous, next, state, T0);

    [Fact]
    public void CardAppearanceArmsHoldWithCardCopy() {
        var decision = Evaluate(SnapshotWithCard(false), SnapshotWithCard(true), ReadyState());

        var hold = Assert.Single(decision.ArmHolds);
        Assert.Equal(NotificationKind.DiscoveryBroken, hold.Key.Kind);
        Assert.Equal(NotificationPolicy.GlobalSubject, hold.Key.Subject);
        Assert.Equal(NotificationPolicy.HoldDiscoveryBroken, hold.DueAt - T0);
    }

    [Fact]
    public void MaterializedIntentUsesSameCopyAsCard() {
        var snapshot = SnapshotWithCard(true);

        var intent = NotificationPolicy.TryMaterializeHold(
            new PendingHold(
                new NotificationKey(NotificationKind.DiscoveryBroken, NotificationPolicy.GlobalSubject),
                T0,
                T0,
                null),
            snapshot,
            ReadyState(),
            T0);

        Assert.NotNull(intent);
        Assert.Equal(snapshot.DiscoveryHealth!.Title, intent!.Title);
        Assert.Equal(snapshot.DiscoveryHealth.Detail, intent.Body);
        Assert.Equal(NotificationSeverity.Warning, intent.Severity);
    }

    [Fact]
    public void CardClearDuringHoldCancels() {
        var decision = Evaluate(SnapshotWithCard(true), SnapshotWithCard(false), ReadyState());

        Assert.Contains(
            new NotificationKey(NotificationKind.DiscoveryBroken, NotificationPolicy.GlobalSubject),
            decision.CancelKeys);
        Assert.Empty(decision.ArmHolds);
    }

    [Fact]
    public void LatchBlocksSecondEmitUntilCardClears() {
        // Episode 1: emit → card stays → re-arm attempt blocked by latch.
        var first = Evaluate(SnapshotWithCard(false), SnapshotWithCard(true), ReadyState());
        var state = first.NextState with { DiscoveryBrokenNotified = true };

        var second = Evaluate(SnapshotWithCard(true), SnapshotWithCard(true), state);
        Assert.Empty(second.ArmHolds);

        // Card clears → latch resets → a new episode arms again.
        var cleared = Evaluate(SnapshotWithCard(true), SnapshotWithCard(false), state);
        Assert.False(cleared.NextState.DiscoveryBrokenNotified);

        var third = Evaluate(SnapshotWithCard(false), SnapshotWithCard(true), cleared.NextState);
        Assert.Single(third.ArmHolds);
    }

    [Fact]
    public void RecordingEmitSetsTheLatch() {
        var intent = new NotificationIntent(
            new NotificationKey(NotificationKind.DiscoveryBroken, NotificationPolicy.GlobalSubject),
            "title",
            "body",
            NotificationSeverity.Warning);

        var (_, nextState) = NotificationPolicy.ApplyBurstAndRecord(
            ImmutableArray.Create(intent),
            ReadyState(),
            T0);

        Assert.True(nextState.DiscoveryBrokenNotified);
    }

    [Fact]
    public void SuppressWindowAbsorbsAppearance() {
        var state = ReadyState() with { SuppressUntil = T0.AddSeconds(10) };

        var decision = Evaluate(SnapshotWithCard(false), SnapshotWithCard(true), state);

        Assert.Empty(decision.ArmHolds);
    }
}
