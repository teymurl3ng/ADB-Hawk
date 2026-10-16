namespace WinUiShellProbe;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

internal static class ProbeWorkload {
    public static async Task<object> RunAsync(ProbeContentController content, DispatcherQueue dispatcher) {
        var mailbox = new ProbeSnapshotMailbox(dispatcher, (revision, count) => content.ApplySnapshot(revision, count));
        content.ShowPairing();
        long firstRevision = content.Revision + 1;
        long burstStart = Stopwatch.GetTimestamp();
        // A background synthetic source targets 100 snapshots/s for ten seconds.
        // Delays pace test data only, never animation or production state.
        await Task.Run(async () => {
            for (int index = 0; index < 1000; index++) {
                mailbox.Publish(firstRevision + index, 8);
                TimeSpan remaining = TimeSpan.FromMilliseconds((index + 1) * 10)
                    - Stopwatch.GetElapsedTime(burstStart);
                if (remaining > TimeSpan.Zero) {
                    await Task.Delay(remaining).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(true);
        // A UI-thread sentinel after the final publication closes the last drain race.
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() => drained.SetResult())) {
            throw new InvalidOperationException("The workload drain sentinel could not be dispatched.");
        }
        await drained.Task.ConfigureAwait(true);
        bool highestDelivered = content.Revision == firstRevision + 999;
        double burstMilliseconds = Stopwatch.GetElapsedTime(burstStart).TotalMilliseconds;
        long navigationStart = Stopwatch.GetTimestamp();
        var mutationMilliseconds = new List<double>();
        for (int index = 0; index < 100; index++) {
            long start = Stopwatch.GetTimestamp();
            switch (index % 3) {
                case 0: content.ShowList(); break;
                case 1: content.ShowDetails(index % 8); break;
                case 2: content.ShowPairing(); break;
            }
            mutationMilliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            await Task.Delay(200).ConfigureAwait(true);
        }
        content.ShowList(false);
        mutationMilliseconds.Sort();
        return new {
            Passed = highestDelivered && mailbox.Published == 1000,
            HighestDelivered = highestDelivered,
            FinalRevision = content.Revision,
            mailbox.Published,
            mailbox.Rendered,
            BurstStartQpc = burstStart,
            BurstMilliseconds = burstMilliseconds,
            NavigationStartQpc = navigationStart,
            NavigationEndQpc = Stopwatch.GetTimestamp(),
            Stopwatch.Frequency,
            NavigationCount = mutationMilliseconds.Count,
            MutationP95Milliseconds = mutationMilliseconds[94],
            MutationMaxMilliseconds = mutationMilliseconds[^1],
            Meaning = "Mutation times measure UI-thread calls, not first-useful or presented frames. Validate the accompanying presentation trace separately.",
        };
    }
}
