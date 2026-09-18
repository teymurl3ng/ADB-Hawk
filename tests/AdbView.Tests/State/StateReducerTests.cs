#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable CA1307
#pragma warning disable xUnit1051
namespace AdbView.Tests.State;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Bridge;
using AdbView.State;
using Xunit;

public sealed class StateReducerTests {
    private static readonly DateTimeOffset FixedUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScenarioA_Probing_MapsToAdbRestoring() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0));

        var next = StateReducer.Reduce(initial, evt, FixedUtc);

        Assert.Equal(GlobalHealthKind.AdbRestoring, next.Health.Kind);
        Assert.Equal(Vocabulary.Health.AdbRestoringTitle, next.Health.Title);
        Assert.Equal(Vocabulary.Health.AdbRestoringDetail, next.Health.Detail);
        Assert.Null(next.Health.Hint);
        Assert.Equal(Vocabulary.Health.NoDevicesTrayTooltip, next.Health.TrayTooltip);
        Assert.Equal(TrayStatusKind.Idle, next.Health.Tray);
        Assert.Equal(2, next.Revision);
        Assert.Equal(FixedUtc, next.GeneratedAt);
    }

    [Fact]
    public void ScenarioB_Starting_MapsToAdbRestoring() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Starting, null, 1, 100));

        var next = StateReducer.Reduce(initial, evt, FixedUtc);

        Assert.Equal(GlobalHealthKind.AdbRestoring, next.Health.Kind);
        Assert.Equal(Vocabulary.Health.AdbRestoringTitle, next.Health.Title);
        Assert.Equal(Vocabulary.Health.AdbRestoringDetail, next.Health.Detail);
        Assert.Null(next.Health.Hint);
        Assert.Equal(Vocabulary.Health.NoDevicesTrayTooltip, next.Health.TrayTooltip);
        Assert.Equal(TrayStatusKind.Idle, next.Health.Tray);
        Assert.Equal(2, next.Revision);
    }

    [Fact]
    public void ScenarioC_Restoring_MapsToAdbRestoring() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Restoring, null, 2, 200));

        var next = StateReducer.Reduce(initial, evt, FixedUtc);

        Assert.Equal(GlobalHealthKind.AdbRestoring, next.Health.Kind);
        Assert.Equal(Vocabulary.Health.AdbRestoringTitle, next.Health.Title);
        Assert.Equal(Vocabulary.Health.AdbRestoringDetail, next.Health.Detail);
        Assert.Null(next.Health.Hint);
        Assert.Equal(Vocabulary.Health.NoDevicesTrayTooltip, next.Health.TrayTooltip);
        Assert.Equal(TrayStatusKind.Idle, next.Health.Tray);
        Assert.Equal(2, next.Revision);
    }

    [Fact]
    public void ScenarioD_Up_MapsToHealthy() {
        // Start from Unavailable so transition to Up changes health
        var initial = StateReducer.Reduce(
            StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1),
            new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0)),
            FixedUtc);

        var status = new AdbServerStatus { ExecutablePath = "C:\\adb.exe", Version = "41" };
        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Up, status, 0, 0));

        var next = StateReducer.Reduce(initial, evt, FixedUtc.AddSeconds(1));

        Assert.Equal(GlobalHealthKind.Healthy, next.Health.Kind);
        Assert.Null(next.Health.Title);
        Assert.Null(next.Health.Detail);
        Assert.Null(next.Health.Hint);
        Assert.Equal(Vocabulary.Health.NoDevicesTrayTooltip, next.Health.TrayTooltip);
        Assert.Equal(TrayStatusKind.Idle, next.Health.Tray);
        Assert.Equal(3, next.Revision);
    }

    [Fact]
    public void ScenarioE_Unavailable_MapsToAdbUnavailable() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0));

        var next = StateReducer.Reduce(initial, evt, FixedUtc);

        Assert.Equal(GlobalHealthKind.AdbUnavailable, next.Health.Kind);
        Assert.Equal(Vocabulary.Health.AdbUnavailableTitle, next.Health.Title);
        Assert.Equal(Vocabulary.Health.AdbUnavailableDetail, next.Health.Detail);
        Assert.Equal(Vocabulary.Health.AdbUnavailableHint, next.Health.Hint);
        Assert.Equal(Vocabulary.Health.AdbUnavailableTrayTooltip, next.Health.TrayTooltip);
        Assert.Equal(TrayStatusKind.Failure, next.Health.Tray);
        Assert.Equal(2, next.Revision);
    }

    [Fact]
    public void ScenarioF_NonHealthFields_SurviveHealthTransition() {
        var initial = FixtureSnapshotProvider.GetSnapshot("connected-usb", isPinned: true, revision: 10);

        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0));
        var next = StateReducer.Reduce(initial, evt, FixedUtc);

        Assert.Equal(11, next.Revision);
        Assert.Equal(FixedUtc, next.GeneratedAt);
        Assert.Equal(GlobalHealthKind.AdbUnavailable, next.Health.Kind);
        Assert.True(next.Shell.Pinned);
        Assert.Equal(initial.DeviceCountLabel, next.DeviceCountLabel);
        Assert.Equal(initial.Devices.Length, next.Devices.Length);
        Assert.Equal(initial.Devices[0].Id, next.Devices[0].Id);
        Assert.Equal(initial.PairingOffers.Length, next.PairingOffers.Length);
        Assert.Equal(initial.Pairing, next.Pairing);
        Assert.Equal(initial.Warnings.Length, next.Warnings.Length);
    }

    [Fact]
    public void ScenarioG_MonotonicRevisionIncrement() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        var evt1 = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0));
        var next1 = StateReducer.Reduce(initial, evt1, FixedUtc);

        var status = new AdbServerStatus { ExecutablePath = "C:\\adb.exe", Version = "41" };
        var evt2 = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Up, status, 0, 0));
        var next2 = StateReducer.Reduce(next1, evt2, FixedUtc.AddSeconds(1));

        Assert.Equal(2, next1.Revision);
        Assert.Equal(3, next2.Revision);
    }

    [Fact]
    public void ScenarioH_DuplicateSemanticEvent_DoesNotIncrementRevision() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        var evt1 = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0));
        var next1 = StateReducer.Reduce(initial, evt1, FixedUtc);

        // Probing -> Starting has same HealthView (AdbRestoring), so semantic duplicate
        var evt2 = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Starting, null, 1, 50));
        var next2 = StateReducer.Reduce(next1, evt2, FixedUtc.AddSeconds(1));

        Assert.Same(next1, next2);
        Assert.Equal(2, next2.Revision);

        // Duplicate pin event
        var pinEvt1 = new SetPinnedEvent(false); // already false
        var next3 = StateReducer.Reduce(next2, pinEvt1, FixedUtc.AddSeconds(2));
        Assert.Same(next2, next3);
        Assert.Equal(2, next3.Revision);
    }

    [Fact]
    public void ScenarioI_Revision_MaxSafeMinusOneToMaxSafe_Succeeds() {
        long maxSafe = StateReducer.MaxJsSafeInteger;
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: maxSafe - 1);

        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0));
        var next = StateReducer.Reduce(initial, evt, FixedUtc);

        Assert.Equal(maxSafe, next.Revision);
    }

    [Fact]
    public void ScenarioI_Revision_MaxSafeWithMeaningfulChange_ThrowsInvalidOperationException() {
        long maxSafe = StateReducer.MaxJsSafeInteger;
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: maxSafe);

        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0));

        var ex = Assert.Throws<InvalidOperationException>(() => StateReducer.Reduce(initial, evt, FixedUtc));
        Assert.Contains(maxSafe.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioI_Revision_MaxSafeWithDuplicateSemanticEvent_SucceedsWithoutChangingRevision() {
        long maxSafe = StateReducer.MaxJsSafeInteger;
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: maxSafe);

        // Duplicate pin event: already false
        var evt = new SetPinnedEvent(false);
        var next = StateReducer.Reduce(initial, evt, FixedUtc);

        Assert.Same(initial, next);
        Assert.Equal(maxSafe, next.Revision);
    }

    [Fact]
    public void ScenarioP_HealthUpdate_PreservesPinnedState() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: true, revision: 5);
        var evt = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0));

        var next = StateReducer.Reduce(initial, evt, FixedUtc);

        Assert.True(next.Shell.Pinned);
        Assert.Equal(6, next.Revision);
    }

    [Fact]
    public void ScenarioQ_PinUpdate_PreservesHealth() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        var evt1 = new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0));
        var next1 = StateReducer.Reduce(initial, evt1, FixedUtc);

        var pinEvt = new SetPinnedEvent(true);
        var next2 = StateReducer.Reduce(next1, pinEvt, FixedUtc.AddSeconds(1));

        Assert.True(next2.Shell.Pinned);
        Assert.Equal(GlobalHealthKind.AdbUnavailable, next2.Health.Kind);
        Assert.Equal(Vocabulary.Health.AdbUnavailableTitle, next2.Health.Title);
        Assert.Equal(3, next2.Revision);
    }

    [Fact]
    public void Scenario_StateReducer_IsPure_ContainsNoTasksOrSideEffects() {
        var type = typeof(StateReducer);

        // Verify class is static (abstract + sealed)
        Assert.True(type.IsAbstract && type.IsSealed);

        // Verify no instance fields or mutable static fields
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
            Assert.True(field.IsLiteral || field.IsInitOnly, $"Field {field.Name} must be constant or readonly");
        }

        // Verify no method returns or accepts Task, TaskCompletionSource, Channel, or delegate callbacks
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
            if (method.DeclaringType != type) continue;

            Assert.False(typeof(Task).IsAssignableFrom(method.ReturnType), $"Method {method.Name} returns Task");
            Assert.False(typeof(Delegate).IsAssignableFrom(method.ReturnType), $"Method {method.Name} returns Delegate");

            foreach (var p in method.GetParameters()) {
                var pType = p.ParameterType;
                Assert.False(typeof(Task).IsAssignableFrom(pType), $"Method {method.Name} has Task parameter {p.Name}");
                Assert.False(typeof(CancellationToken).IsAssignableFrom(pType), $"Method {method.Name} has CancellationToken parameter {p.Name}");
                Assert.False(typeof(Delegate).IsAssignableFrom(pType), $"Method {method.Name} has Delegate parameter {p.Name}");
                Assert.False(pType.Name.Contains("TaskCompletionSource", StringComparison.Ordinal), $"Method {method.Name} has TaskCompletionSource parameter {p.Name}");
            }
        }
    }
}
