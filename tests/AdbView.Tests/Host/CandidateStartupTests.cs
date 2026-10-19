namespace AdbView.Tests.Host;

using System.Collections.Concurrent;
using System.IO.Pipes;
using AdbView.Host;
using AdbView.Tests.Boundaries;
using AdbView.WinUiMigration.Host;
using Xunit;

public sealed class CandidateStartupTests {
    [Fact]
    public void StartupCommandSelectsLiveSilentModeAndCannotMixWithFixtures() {
        var startup = CandidateOptions.Parse(["--startup"]);
        Assert.True(startup.Live); Assert.True(startup.StartupSilent); Assert.Null(startup.ResultPath);
        Assert.False(CandidateOptions.Parse(["--live"]).StartupSilent);
        Assert.True(CandidateOptions.Parse(["--startup", "--live"]).StartupSilent);
        foreach (string extra in new[] { "--startup", "--fixture=empty", "--instance=" + Guid.NewGuid().ToString("N"), "--verify=" + Path.GetFullPath("result.json") }) {
            Assert.Throws<ArgumentException>(() => CandidateOptions.Parse(["--startup", extra]));
        }
    }

    [Fact]
    public void ControlIdentityIsCandidateOnlyAndSeparatesUsersAndSessions() {
        string identity = CandidateInstanceIdentity.ForUserSession("S-1-5-21-1", 1);
        Assert.StartsWith(CandidateOptions.LiveIdentity + ".", identity, StringComparison.Ordinal);
        Assert.NotEqual(identity, CandidateInstanceIdentity.ForUserSession("S-1-5-21-2", 1));
        Assert.NotEqual(identity, CandidateInstanceIdentity.ForUserSession("S-1-5-21-1", 2));
        Assert.DoesNotContain("AdbView.Control.", identity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActualLocalPipeAcknowledgesEachToggleWithoutCoalescing() {
        string name = PipeName(); int toggles = 0; var failures = new ConcurrentQueue<Exception>();
        using var channel = new CandidateControlChannel(name);
        channel.Start(() => { Interlocked.Increment(ref toggles); return true; }, failures.Enqueue);
        for (int index = 0; index < 20; index++) {
            Assert.True(await CandidateControlChannel.SendToggleAsync(name, TimeSpan.FromSeconds(3)).ConfigureAwait(true));
        }
        Assert.Equal(20, Volatile.Read(ref toggles)); Assert.Empty(failures);
        Assert.Throws<InvalidOperationException>(() => channel.Start(() => true, failures.Enqueue));
    }

    [Fact]
    public async Task DispatcherRejectionIsAcknowledgedAsFailureAndTheNextRequestStillRuns() {
        string name = PipeName(); bool accept = false; var failures = new ConcurrentQueue<Exception>();
        using var channel = new CandidateControlChannel(name);
        channel.Start(() => accept, failures.Enqueue);
        Assert.False(await CandidateControlChannel.SendToggleAsync(name, TimeSpan.FromSeconds(3)).ConfigureAwait(true));
        accept = true;
        Assert.True(await CandidateControlChannel.SendToggleAsync(name, TimeSpan.FromSeconds(3)).ConfigureAwait(true));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task UnknownCommandAndDisconnectedClientDoNotInvokeToggleOrEndTheServer() {
        string name = PipeName(); int toggles = 0; var failures = new ConcurrentQueue<Exception>();
        using var channel = new CandidateControlChannel(name);
        channel.Start(() => { Interlocked.Increment(ref toggles); return true; }, failures.Enqueue);
        using (var client = Client(name)) {
            await client.ConnectAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
            await client.WriteAsync(new byte[] { 99 }, TestContext.Current.CancellationToken).ConfigureAwait(true);
            var response = new byte[1];
            await client.ReadExactlyAsync(response, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(0, response[0]);
        }
        using (var disconnected = Client(name)) {
            await disconnected.ConnectAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        Assert.True(await CandidateControlChannel.SendToggleAsync(name, TimeSpan.FromSeconds(3)).ConfigureAwait(true));
        Assert.Equal(1, Volatile.Read(ref toggles)); Assert.Empty(failures);
    }

    [Fact]
    public async Task SilentClientDeadlineReleasesTheOnlyPipeSlot() {
        string name = PipeName(); var failures = new ConcurrentQueue<Exception>();
        using var channel = new CandidateControlChannel(name, TimeSpan.FromMilliseconds(50));
        channel.Start(() => true, failures.Enqueue);
        using var silent = Client(name);
        await silent.ConnectAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
        var response = new byte[1];
        Assert.Equal(0, await silent.ReadAsync(response, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(await CandidateControlChannel.SendToggleAsync(name, TimeSpan.FromSeconds(3)).ConfigureAwait(true));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task OwnerFailureIsReportedAndTheClientCannotClaimSuccess() {
        string name = PipeName(); var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var channel = new CandidateControlChannel(name);
        channel.Start(() => throw new InvalidOperationException("Injected owner dispatch failure."), exception => failed.TrySetResult(exception));
        Assert.False(await CandidateControlChannel.SendToggleAsync(name, TimeSpan.FromSeconds(3)).ConfigureAwait(true));
        Assert.IsType<InvalidOperationException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task MissingOwnerHasBoundedFailureAndDisposalCancelsAnIdleServer() {
        string name = PipeName();
        Assert.False(await CandidateControlChannel.SendToggleAsync(name, TimeSpan.FromMilliseconds(30)).ConfigureAwait(true));
        var failures = new ConcurrentQueue<Exception>();
        var channel = new CandidateControlChannel(name);
        channel.Start(() => true, failures.Enqueue);
        channel.Dispose(); channel.Dispose();
        Assert.Throws<ObjectDisposedException>(() => channel.Start(() => true, failures.Enqueue)); Assert.Empty(failures);
    }

    [Fact]
    public async Task ShutdownCancelsAConnectedClientWithoutWaitingForItsDeadline() {
        string name = PipeName(); var failures = new ConcurrentQueue<Exception>();
        using var channel = new CandidateControlChannel(name, TimeSpan.FromMinutes(1));
        channel.Start(() => true, failures.Enqueue);
        using var client = Client(name);
        await client.ConnectAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
        channel.Dispose(); Assert.Empty(failures);
    }

    [Fact]
    public void StartupControllerWritesOnlyCandidateRegistrationAndKeepsProductionEntry() {
        var store = new RunStore();
        store.Values[StartupRegistrationContract.ValueName] = "production sentinel";
        string exe = Path.GetFullPath("candidate folder/AdbView.WinUiMigration.exe");
        var startup = new StartupRegistration(store, () => exe);
        var owner = new CandidatePreferenceController(new UserPreferences(new PreferenceStore()), _ => { }, _ => { }, startup);
        Assert.False(owner.Execute(0).Startup); Assert.Empty(store.Writes);
        foreach (uint command in new uint[] { 3, 4, 10, 11, 12 }) { owner.Execute(command); }
        Assert.Empty(store.Writes);
        Assert.True(owner.Execute(2).Startup);
        Assert.Equal("\"" + exe + "\" --startup", store.Values[CandidateRegistryRunKeyStore.ValueName]);
        Assert.True(CandidateOptions.Parse([StartupRegistrationContract.StartupArgument]).StartupSilent);
        Assert.False(owner.Execute(2).Startup); Assert.False(store.Values.ContainsKey(CandidateRegistryRunKeyStore.ValueName));
        Assert.Equal("production sentinel", store.Values[StartupRegistrationContract.ValueName]);
        Assert.Equal([CandidateRegistryRunKeyStore.ValueName, CandidateRegistryRunKeyStore.ValueName], store.Writes);
    }

    [Fact]
    public void FailedStartupWriteKeepsReadbackUncheckedAndDoesNotChangeOtherPreferences() {
        var run = new RunStore { FailWrite = true }; var preferences = new PreferenceStore();
        var owner = new CandidatePreferenceController(new UserPreferences(preferences), _ => { }, _ => { },
            new StartupRegistration(run, () => Path.GetFullPath("candidate.exe")));
        Assert.False(owner.Execute(2).Startup); Assert.Empty(run.Values); Assert.Equal(0, preferences.Writes);
    }

    [Fact]
    public void RunAdapterMapsOnlyTheExistingContractNameAndNeverInstantiatesProductionDefaults() {
        Assert.IsAssignableFrom<IRunKeyStore>(new CandidateRegistryRunKeyStore());
        Assert.Equal("ADB View WinUI Migration", CandidateRegistryRunKeyStore.MapName(StartupRegistrationContract.ValueName));
        Assert.Throws<ArgumentOutOfRangeException>(() => CandidateRegistryRunKeyStore.MapName("other"));
        string source = File.ReadAllText(Repo.RootFile("src", "AdbView.WinUiMigration", "Host", "CandidateControlChannel.cs"));
        Assert.Equal(2, source.Split("PipeOptions.CurrentUserOnly", StringSplitOptions.None).Length - 1);
        string window = File.ReadAllText(Repo.RootFile("src", "AdbView.WinUiMigration", "CandidateWindow.xaml.cs"));
        Assert.DoesNotContain("new StartupRegistration()", window, StringComparison.Ordinal);
    }

    private static string PipeName() => "AdbView.WinUiMigration.Test." + Guid.NewGuid().ToString("N");
    private static NamedPipeClientStream Client(string name) => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private sealed class PreferenceStore : IPreferenceStore {
        public int Writes { get; private set; }
        public object? GetValue(string name) => null;
        public void SetValue(string name, object value, Microsoft.Win32.RegistryValueKind kind) { Writes++; }
    }
    private sealed class RunStore : IRunKeyStore {
        public Dictionary<string, string> Values { get; } = [];
        public List<string> Writes { get; } = [];
        public bool FailWrite { get; init; }
        public string? GetValue(string name) => Values.GetValueOrDefault(CandidateRegistryRunKeyStore.MapName(name));
        public void SetValue(string name, string value) {
            string mapped = CandidateRegistryRunKeyStore.MapName(name); Writes.Add(mapped);
            if (FailWrite) { throw new UnauthorizedAccessException("Injected startup write failure."); }
            Values[mapped] = value;
        }
        public void DeleteValue(string name) { string mapped = CandidateRegistryRunKeyStore.MapName(name); Writes.Add(mapped); Values.Remove(mapped); }
        public string[] GetValueNames() => Values.Keys.ToArray();
    }
}
