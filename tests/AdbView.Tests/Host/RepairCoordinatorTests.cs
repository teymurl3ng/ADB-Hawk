namespace AdbView.Tests.Host;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Host;
using AdbView.State;
using Xunit;

public sealed class RepairCoordinatorTests {
    private sealed class FakeRunner : IAdbProcessRunner {
        public int KillCalls;
        public ProcessResult KillResult { get; set; } = new(0, "", "", false);
        public Func<Task<ProcessResult>>? KillHandler { get; set; }

        public Task<ProcessResult> StartServerAsync(string executablePath, CancellationToken ct = default) =>
            throw new InvalidOperationException("Repair tests never start the server.");

        public Task<ProcessResult> KillServerAsync(string executablePath, CancellationToken ct = default) {
            KillCalls++;
            return KillHandler?.Invoke() ?? Task.FromResult(KillResult);
        }
    }

    private sealed class FakeLocator : IAdbExecutableLocator {
        public string? Result { get; set; } = @"C:\adb\adb.exe";
        public string? Locate() => Result;
    }

    private sealed class FakeLauncher : IElevatedProcessLauncher {
        public List<(string FileName, IReadOnlyList<string> Args)> Calls { get; } = new();
        public ProcessLaunchResult Result { get; set; } =
            new(ProcessLaunchOutcome.Launched, 0, null);

        public Task<ProcessLaunchResult> LaunchAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct) {
            Calls.Add((fileName, arguments));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeServiceState : IServiceStateSource {
        public bool? Result { get; set; } = false;
        public Task<bool?> IsServiceRunningAsync(string serviceName, CancellationToken ct) =>
            Task.FromResult(Result);
    }

    private sealed class FakeDiagnostics : INetworkDiagnosticsSource {
        public IReadOnlyList<int> GetDiscoveryPortOwnerPids() => Array.Empty<int>();
        public IReadOnlyList<NetworkAdapterProbeInfo> GetOperationalAdapters() => Adapters;
        public IReadOnlyList<IPAddress> GetMulticastCandidateAddresses() => Array.Empty<IPAddress>();
        public IReadOnlyList<NetworkAdapterProbeInfo> Adapters { get; set; } =
            Array.Empty<NetworkAdapterProbeInfo>();
    }

    private sealed class EventSink {
        public List<AdbViewEvent> Events { get; } = new();
        public int CheckCount;

        public void Post(AdbViewEvent evt) => Events.Add(evt);
        public Task CheckAsync() {
            CheckCount++;
            return Task.CompletedTask;
        }
    }

    private static NetworkAdapterProbeInfo Adapter(string name) =>
        new(name, name, NetworkInterfaceType.Ethernet);

    [Fact]
    public async Task RestartNonzeroExitAndUnexpectedExceptionBothPublishFailure() {
        var (coordinator, runner, _, _, _, sink) = Create();
        await coordinator.RebuildCatalogAsync(CancellationToken.None);
        runner.KillResult = new ProcessResult(5, "", "denied", false);
        await coordinator.RunAsync("restart-adb", 1, CancellationToken.None);
        Assert.Equal(DiscoveryRepairOutcome.Failed, Assert.Single(sink.Events.OfType<DiscoveryRepairFinishedEvent>()).Outcome);
        sink.Events.Clear();
        runner.KillHandler = () => throw new System.ComponentModel.Win32Exception("synthetic launch failure");
        await coordinator.RunAsync("restart-adb", 1, CancellationToken.None);
        Assert.Single(sink.Events.OfType<DiscoveryRepairStartedEvent>());
        Assert.Equal(DiscoveryRepairOutcome.Failed, Assert.Single(sink.Events.OfType<DiscoveryRepairFinishedEvent>()).Outcome);
    }

    [Fact]
    public async Task ElevatedNonzeroExitDoesNotCreateUndoOrReportCompletion() {
        var (coordinator, _, launcher, service, _, sink) = Create();
        service.Result = true;
        launcher.Result = new ProcessLaunchResult(ProcessLaunchOutcome.Launched, 5, null);
        await coordinator.RebuildCatalogAsync(CancellationToken.None);
        await coordinator.RunAsync("stop-service:Tailscale", 1, CancellationToken.None);
        Assert.Equal(DiscoveryRepairOutcome.Failed, Assert.Single(sink.Events.OfType<DiscoveryRepairFinishedEvent>()).Outcome);
        service.Result = false;
        sink.Events.Clear();
        await coordinator.RebuildCatalogAsync(CancellationToken.None);
        Assert.DoesNotContain(Assert.Single(sink.Events.OfType<DiscoveryRepairCatalogEvent>()).Repairs,
            repair => repair.Kind == DiscoveryRepairActionKind.StartService);
    }

    [Fact]
    public async Task AppliedRepairRemainsReversibleAfterDiagnosticCardClears() {
        var (coordinator, _, _, service, _, sink) = Create();
        service.Result = true;
        await coordinator.RebuildCatalogAsync(CancellationToken.None);
        await coordinator.RunAsync("stop-service:Tailscale", 1, CancellationToken.None);
        service.Result = false;
        coordinator.OnSnapshotPublished(StateEngine.CreateDefaultSnapshot(), DeviceEngineState.Empty);
        sink.Events.Clear();
        await coordinator.RebuildCatalogAsync(CancellationToken.None);
        Assert.Contains(Assert.Single(sink.Events.OfType<DiscoveryRepairCatalogEvent>()).Repairs,
            repair => repair.Id == "start-service:Tailscale");
    }

    private static (RepairCoordinator, FakeRunner, FakeLauncher, FakeServiceState, FakeDiagnostics, EventSink) Create(
        FakeDiagnostics? diagnostics = null) {
        var runner = new FakeRunner();
        var launcher = new FakeLauncher();
        var service = new FakeServiceState();
        var source = diagnostics ?? new FakeDiagnostics();
        var sink = new EventSink();
        var coordinator = new RepairCoordinator(
            processRunner: runner,
            executableLocator: new FakeLocator(),
            diagnosticsSource: source,
            launcher: launcher,
            serviceState: service,
            post: sink.Post,
            runDiscoveryCheck: sink.CheckAsync);
        return (coordinator, runner, launcher, service, source, sink);
    }

    [Fact]
    public void ValidTargetAcceptsDocumentedNamesAndRejectsInjection() {
        Assert.True(RepairCoordinator.IsValidTarget("vEthernet (Default Switch)"));
        Assert.True(RepairCoordinator.IsValidTarget("Tailscale"));
        Assert.False(RepairCoordinator.IsValidTarget("evil\"name"));
        Assert.False(RepairCoordinator.IsValidTarget("a;b"));
        Assert.False(RepairCoordinator.IsValidTarget("a|b"));
        Assert.False(RepairCoordinator.IsValidTarget(new string('a', 65)));
        Assert.False(RepairCoordinator.IsValidTarget(""));
    }

    [Fact]
    public async Task CatalogOffersRestartAlwaysAndAdminRepairsOnlyForFindings() {
        var (coordinator, _, _, service, diagnostics, sink) = Create();
        service.Result = true;
        var adapters = new[] {
            Adapter("vEthernet (Default Switch)"),
            Adapter("Wi-Fi"),
        };
        diagnostics.Adapters = adapters;

        await coordinator.RebuildCatalogAsync(CancellationToken.None);

        var catalog = Assert.IsType<DiscoveryRepairCatalogEvent>(sink.Events.Single()).Repairs;
        var expected = new[] {
            ("restart-adb", DiscoveryRepairActionKind.RestartAdb, (string?)null),
            ("stop-service:Tailscale", DiscoveryRepairActionKind.StopService, "Tailscale"),
            ("disable-adapter:vEthernet (Default Switch)", DiscoveryRepairActionKind.DisableAdapter, "vEthernet (Default Switch)"),
        };
        Assert.Equal(expected, catalog.Select(r => (r.Id, r.Kind, r.Target)).ToArray());
    }

    [Fact]
    public async Task CatalogHidesAbsentService() {
        var (coordinator, _, _, service, _, sink) = Create();
        service.Result = false;

        await coordinator.RebuildCatalogAsync(CancellationToken.None);

        var catalog = Assert.IsType<DiscoveryRepairCatalogEvent>(sink.Events.Single()).Repairs;
        Assert.Contains(catalog, r => r.Kind == DiscoveryRepairActionKind.RestartAdb);
        Assert.DoesNotContain(catalog, r => r.Kind is DiscoveryRepairActionKind.StopService or DiscoveryRepairActionKind.StartService);
    }

    [Fact]
    public async Task RestartAdbSpawnsKillAndCompletes() {
        var (coordinator, runner, _, _, _, sink) = Create();
        await coordinator.RebuildCatalogAsync(CancellationToken.None);
        sink.Events.Clear();

        await coordinator.RunAsync("restart-adb", currentServerGeneration: 7, CancellationToken.None);

        Assert.Equal(1, runner.KillCalls);
        var finished = Assert.IsType<DiscoveryRepairFinishedEvent>(sink.Events.OfType<DiscoveryRepairFinishedEvent>().Single());
        Assert.Equal(DiscoveryRepairOutcome.Completed, finished.Outcome);
        Assert.Equal("restart-adb", finished.RepairId);
    }

    [Fact]
    public async Task RestartAdbWithoutExecutableFails() {
        var sink = new EventSink();
        var coordinator = new RepairCoordinator(
            new FakeRunner(),
            new NullLocator(),
            new FakeDiagnostics(),
            new FakeLauncher(),
            new FakeServiceState(),
            sink.Post,
            sink.CheckAsync);
        await coordinator.RebuildCatalogAsync(CancellationToken.None);

        await coordinator.RunAsync("restart-adb", 1, CancellationToken.None);

        var finished = Assert.IsType<DiscoveryRepairFinishedEvent>(sink.Events.OfType<DiscoveryRepairFinishedEvent>().Single());
        Assert.Equal(DiscoveryRepairOutcome.Failed, finished.Outcome);
    }

    private sealed class NullLocator : IAdbExecutableLocator {
        public string? Locate() => null;
    }

    [Fact]
    public async Task StopServiceBuildsFixedScCommandAndUnlocksUndo() {
        var (coordinator, _, launcher, service, _, sink) = Create();
        service.Result = true;
        await coordinator.RebuildCatalogAsync(CancellationToken.None);
        sink.Events.Clear();
        service.Result = false; // after the stop, the service no longer runs

        await coordinator.RunAsync("stop-service:Tailscale", 3, CancellationToken.None);

        var call = Assert.Single(launcher.Calls);
        Assert.Equal("sc.exe", call.FileName);
        var expectedArgs = new[] { "stop", "Tailscale" };
        Assert.Equal(expectedArgs, call.Args);
        Assert.Contains(sink.Events.OfType<DiscoveryRepairCatalogEvent>().Last().Repairs,
            r => r.Kind == DiscoveryRepairActionKind.StartService && r.Target == "Tailscale");
        Assert.DoesNotContain(sink.Events.OfType<DiscoveryRepairCatalogEvent>().Last().Repairs,
            r => r.Kind == DiscoveryRepairActionKind.StopService);
    }

    [Fact]
    public async Task DisableAdapterBuildsFixedNetshCommand() {
        var adapters = new[] { Adapter("vEthernet (Default Switch)") };
        var diagnostics = new FakeDiagnostics { Adapters = adapters };
        var (coordinator, _, launcher, _, _, _) = Create(diagnostics);
        await coordinator.RebuildCatalogAsync(CancellationToken.None);

        await coordinator.RunAsync("disable-adapter:vEthernet (Default Switch)", 3, CancellationToken.None);

        var call = Assert.Single(launcher.Calls);
        Assert.Equal("netsh.exe", call.FileName);
        var expectedArgs = new[] {
            "interface", "set", "interface", "name=vEthernet (Default Switch)", "admin=disable",
        };
        Assert.Equal(expectedArgs, call.Args);
    }

    [Fact]
    public async Task DeclinedLaunchReportsDeclinedWithoutUndoOrMeasure() {
        var (coordinator, _, launcher, service, _, sink) = Create();
        launcher.Result = new ProcessLaunchResult(ProcessLaunchOutcome.Declined, null, 1223);
        service.Result = true;
        await coordinator.RebuildCatalogAsync(CancellationToken.None);
        sink.Events.Clear();
        sink.CheckCount = 0;

        await coordinator.RunAsync("stop-service:Tailscale", 3, CancellationToken.None);

        var finished = Assert.IsType<DiscoveryRepairFinishedEvent>(sink.Events.OfType<DiscoveryRepairFinishedEvent>().Single());
        Assert.Equal(DiscoveryRepairOutcome.Declined, finished.Outcome);
        Assert.DoesNotContain(sink.Events, e => e is DiscoveryRepairCatalogEvent);
        Assert.Equal(0, sink.CheckCount);
    }

    [Fact]
    public async Task UnknownRepairIdIsIgnored() {
        var (coordinator, _, _, _, _, sink) = Create();
        await coordinator.RunAsync("does-not-exist", 1, CancellationToken.None);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task PostRestartMeasureFiresOnceOnFirstFrameAfterUpGeneration() {
        var (coordinator, _, _, _, _, sink) = Create();
        await coordinator.RebuildCatalogAsync(CancellationToken.None);

        await coordinator.RunAsync("restart-adb", currentServerGeneration: 7, CancellationToken.None);
        int checkCountAfterRun = sink.CheckCount; // restart itself does not measure
        Assert.Equal(0, checkCountAfterRun);

        // Same-generation Up and pre-restart frames: no measure.
        coordinator.OnServerUp(7);
        coordinator.OnSnapshotPublished(
            StateEngine.CreateDefaultSnapshot(),
            DeviceEngineState.Empty with { LatestAppliedMdnsAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        Assert.Equal(0, sink.CheckCount);

        // New generation but no mDNS frame yet: still no measure.
        coordinator.OnServerUp(8);
        coordinator.OnSnapshotPublished(StateEngine.CreateDefaultSnapshot(), DeviceEngineState.Empty);
        Assert.Equal(0, sink.CheckCount);

        // First frame of the new session: exactly one measure, and it never re-fires.
        coordinator.OnSnapshotPublished(
            StateEngine.CreateDefaultSnapshot(),
            DeviceEngineState.Empty with { LatestAppliedMdnsAt = DateTimeOffset.UtcNow });
        coordinator.OnSnapshotPublished(
            StateEngine.CreateDefaultSnapshot(),
            DeviceEngineState.Empty with { LatestAppliedMdnsAt = DateTimeOffset.UtcNow });
        Assert.Equal(1, sink.CheckCount);
    }

    [Fact]
    public void CardClearResetsSessionBookkeeping() {
        var (coordinator, _, _, _, _, _) = Create();
        coordinator.OnSnapshotPublished(StateEngine.CreateDefaultSnapshot(), DeviceEngineState.Empty);

        // After a card clear, restart recovery does nothing (no armed session).
        coordinator.OnServerUp(99);
        coordinator.OnSnapshotPublished(
            StateEngine.CreateDefaultSnapshot(),
            DeviceEngineState.Empty with { LatestAppliedMdnsAt = DateTimeOffset.UtcNow });
    }
}
