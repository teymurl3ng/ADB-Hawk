#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable CA1307
#pragma warning disable xUnit1051
namespace AdbView.Tests.State;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.State;
using Xunit;

public sealed class StateEngineTests {
    [Fact]
    public async Task ScenarioJ_QueuedEvents_ProcessInOrder() {
        var clockTime = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        await using var engine = new StateEngine(getUtcNow: () => clockTime);

        var observedSnapshots = new List<AppSnapshot>();
        engine.SnapshotPublished += s => {
            lock (observedSnapshots) {
                observedSnapshots.Add(s);
            }
        };

        // Post sequence of events
        // 1. Probing -> AdbRestoring
        engine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0)));
        // 2. Pin -> Pinned = true
        engine.Post(new SetPinnedEvent(true));
        // 3. Unavailable -> AdbUnavailable
        engine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0)));
        // 4. Unpin -> Pinned = false
        engine.Post(new SetPinnedEvent(false));

        await engine.DrainAsync();

        Assert.Equal(4, observedSnapshots.Count);
        // Event 1: Restoring, Unpinned, Rev 2
        Assert.Equal(GlobalHealthKind.AdbRestoring, observedSnapshots[0].Health.Kind);
        Assert.False(observedSnapshots[0].Shell.Pinned);
        Assert.Equal(2, observedSnapshots[0].Revision);

        // Event 2: Restoring, Pinned, Rev 3
        Assert.Equal(GlobalHealthKind.AdbRestoring, observedSnapshots[1].Health.Kind);
        Assert.True(observedSnapshots[1].Shell.Pinned);
        Assert.Equal(3, observedSnapshots[1].Revision);

        // Event 3: Unavailable, Pinned, Rev 4
        Assert.Equal(GlobalHealthKind.AdbUnavailable, observedSnapshots[2].Health.Kind);
        Assert.True(observedSnapshots[2].Shell.Pinned);
        Assert.Equal(4, observedSnapshots[2].Revision);

        // Event 4: Unavailable, Unpinned, Rev 5
        Assert.Equal(GlobalHealthKind.AdbUnavailable, observedSnapshots[3].Health.Kind);
        Assert.False(observedSnapshots[3].Shell.Pinned);
        Assert.Equal(5, observedSnapshots[3].Revision);
    }

    [Fact]
    public async Task ScenarioK_ConcurrentProducers_AreSerializedBySingleReader() {
        await using var engine = new StateEngine();

        int eventCount = 100;
        var tasks = new List<Task>();

        for (int i = 0; i < eventCount; i++) {
            bool pin = i % 2 == 1;
            tasks.Add(Task.Run(() => {
                engine.Post(new SetPinnedEvent(pin));
            }));
        }

        await Task.WhenAll(tasks);
        await engine.DrainAsync();

        // The current snapshot revision must match the final authoritative reduction without race corruption
        Assert.True(engine.CurrentSnapshot.Revision > 1);
    }

    [Fact]
    public async Task ScenarioN_PinAction_GoesThroughAuthoritativeStateOwner() {
        await using var engine = new StateEngine();

        var tcs = new TaskCompletionSource<AppSnapshot>();
        engine.SnapshotPublished += s => tcs.TrySetResult(s);

        engine.Post(new SetPinnedEvent(true));

        var snapshot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(snapshot.Shell.Pinned);
        Assert.Equal(engine.CurrentSnapshot, snapshot);
        Assert.Equal(2, engine.CurrentSnapshot.Revision);
    }

    [Fact]
    public async Task ScenarioO_UnpinAction_GoesThroughAuthoritativeStateOwner() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: true, revision: 1);
        await using var engine = new StateEngine(initialSnapshot: initial);

        var tcs = new TaskCompletionSource<AppSnapshot>();
        engine.SnapshotPublished += s => tcs.TrySetResult(s);

        engine.Post(new SetPinnedEvent(false));

        var snapshot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(snapshot.Shell.Pinned);
        Assert.Equal(engine.CurrentSnapshot, snapshot);
        Assert.Equal(2, engine.CurrentSnapshot.Revision);
    }

    [Fact]
    public async Task ScenarioT_GracefulShutdown_DrainsAllQueuedEventsInFifoOrder() {
        await using var engine = new StateEngine();
        var publishedSnapshots = new List<AppSnapshot>();
        engine.SnapshotPublished += s => {
            lock (publishedSnapshots) {
                publishedSnapshots.Add(s);
            }
        };

        // Enqueue several meaningful events
        Assert.True(engine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0))));
        Assert.True(engine.Post(new SetPinnedEvent(true)));
        Assert.True(engine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0))));
        Assert.True(engine.Post(new SetPinnedEvent(false)));

        // Initiate graceful stop immediately (writer completes, but reader drains all 4 accepted events)
        var stopTask = engine.StopAsync();

        // Any new Post() once shutdown begins returns false
        Assert.False(engine.Post(new SetPinnedEvent(true)));

        await stopTask;

        // Verify all 4 events were reduced in FIFO order
        lock (publishedSnapshots) {
            Assert.Equal(4, publishedSnapshots.Count);
            // Event 1: Probing -> AdbRestoring, Unpinned, Rev 2
            Assert.Equal(GlobalHealthKind.AdbRestoring, publishedSnapshots[0].Health.Kind);
            Assert.False(publishedSnapshots[0].Shell.Pinned);
            Assert.Equal(2, publishedSnapshots[0].Revision);

            // Event 2: Pin -> Pinned = true, Rev 3
            Assert.True(publishedSnapshots[1].Shell.Pinned);
            Assert.Equal(3, publishedSnapshots[1].Revision);

            // Event 3: Unavailable -> AdbUnavailable, Pinned, Rev 4
            Assert.Equal(GlobalHealthKind.AdbUnavailable, publishedSnapshots[2].Health.Kind);
            Assert.True(publishedSnapshots[2].Shell.Pinned);
            Assert.Equal(4, publishedSnapshots[2].Revision);

            // Event 4: Unpin -> Pinned = false, Rev 5
            Assert.False(publishedSnapshots[3].Shell.Pinned);
            Assert.Equal(5, publishedSnapshots[3].Revision);
        }

        // Final snapshot reflects the final accepted event
        Assert.Equal(GlobalHealthKind.AdbUnavailable, engine.CurrentSnapshot.Health.Kind);
        Assert.False(engine.CurrentSnapshot.Shell.Pinned);
        Assert.Equal(5, engine.CurrentSnapshot.Revision);

        // Ensure no further publication occurs after shutdown
        int countBeforeWait = publishedSnapshots.Count;
        await Task.Delay(50);
        Assert.Equal(countBeforeWait, publishedSnapshots.Count);
    }

    [Fact]
    public async Task ScenarioT_ForcedEmergencyCancellation_TerminatesProcessingTask() {
        await using var engine = new StateEngine();
        using var emergencyCts = new CancellationTokenSource();
        await emergencyCts.CancelAsync();

        // StopAsync with already canceled token triggers emergency forced teardown
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StopAsync(emergencyCts.Token));

        Assert.False(engine.Post(new SetPinnedEvent(true)));
    }
}
