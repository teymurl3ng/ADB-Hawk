namespace AdbView.Tests.Presentation;

using System.Collections.Concurrent;
using AdbView.Bridge;
using AdbView.Presentation;
using AdbView.State;
using Xunit;

public sealed class SnapshotMailboxTests {
    [Fact]
    public void OlderAndDuplicateRevisionsNeverReplaceLatestSlot() {
        var queued = new Queue<Action>();
        var seen = new List<long>();
        using var mailbox = new SnapshotMailbox(action => { queued.Enqueue(action); return true; }, snapshot => seen.Add(snapshot.Revision));
        Assert.True(mailbox.Publish(Snapshot(20)));
        Assert.False(mailbox.Publish(Snapshot(19)));
        Assert.False(mailbox.Publish(Snapshot(20)));
        Assert.True(mailbox.Publish(Snapshot(21)));
        Assert.Single(queued)();
        Assert.Equal([21L], seen);
    }

    [Fact]
    public void PublicationDuringRenderSchedulesExactlyOneFollowingDrain() {
        var queued = new Queue<Action>();
        var seen = new List<long>();
        SnapshotMailbox? mailbox = null;
        mailbox = new SnapshotMailbox(action => { queued.Enqueue(action); return true; }, snapshot => {
            seen.Add(snapshot.Revision);
            if (snapshot.Revision == 20) {
                Assert.True(mailbox!.Publish(Snapshot(21)));
                Assert.True(mailbox.Publish(Snapshot(22)));
            }
        });
        using (mailbox) {
            mailbox.Publish(Snapshot(20));
            queued.Dequeue()();
            Assert.Single(queued);
            queued.Dequeue()();
            Assert.Empty(queued);
            Assert.Equal([20L, 22L], seen);
            Assert.True(mailbox.Publish(Snapshot(23)));
            queued.Dequeue()();
            Assert.Equal([20L, 22L, 23L], seen);
        }
    }

    [Fact]
    public async Task ConcurrentPublishAndDrainResetCannotLoseHighestRevision() {
        var queued = new ConcurrentQueue<Action>();
        long applied = -1;
        using var mailbox = new SnapshotMailbox(action => { queued.Enqueue(action); return true; }, snapshot => applied = snapshot.Revision);
        // A separate producer races the drain/reset boundary repeatedly, without sleeps.
        Task producer = Task.Run(() => { for (long revision = 0; revision <= 10000; revision++) { mailbox.Publish(Snapshot(revision)); } }, TestContext.Current.CancellationToken);
        while (!producer.IsCompleted || !queued.IsEmpty) {
            if (queued.TryDequeue(out Action? drain)) { drain(); }
        }
        await producer.ConfigureAwait(true);
        while (queued.TryDequeue(out Action? drain)) { drain(); }
        Assert.Equal(10000, applied);
        Assert.False(mailbox.Publish(Snapshot(9999)));
        Assert.Empty(queued);
    }

    [Fact]
    public void DispatcherRejectionIsExplicitAndDisposesPendingDelivery() {
        using var mailbox = new SnapshotMailbox(_ => false, _ => throw new InvalidOperationException("Must not render."));
        Assert.Throws<InvalidOperationException>(() => mailbox.Publish(Snapshot(20)));
        Assert.False(mailbox.Publish(Snapshot(21)));
    }

    [Fact]
    public void DisposeInvalidatesQueuedCallback() {
        Action? callback = null;
        int rendered = 0;
        using var mailbox = new SnapshotMailbox(action => { callback = action; return true; }, _ => rendered++);
        mailbox.Publish(Snapshot(20));
        mailbox.Dispose();
        callback!();
        Assert.Equal(0, rendered);
        Assert.False(mailbox.Publish(Snapshot(21)));
    }

    private static AppSnapshot Snapshot(long revision) => FixtureSnapshotProvider.GetSnapshot("empty", revision: revision);

    [Fact]
    public void RenderFailureClosesMailboxAndPropagates() {
        Action? callback = null;
        using var mailbox = new SnapshotMailbox(action => { callback = action; return true; }, _ => throw new InvalidOperationException("Fixture render failed."));
        mailbox.Publish(Snapshot(20));
        Assert.Throws<InvalidOperationException>(() => callback!());
        Assert.False(mailbox.Publish(Snapshot(21)));
    }
}
