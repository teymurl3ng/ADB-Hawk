#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable CA1307
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class AdbServerSupervisorTests {
    private DateTimeOffset _now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecoveryLaunchExceptionDoesNotEndSupervisorIteration() {
        await using var server = new FakeAdbServer();
        server.SetHandler((socket, _) => { socket.Close(); return Task.CompletedTask; });
        var runner = new TestProcessRunner {
            Handler = (_, _) => throw new System.ComponentModel.Win32Exception("synthetic launch failure"),
        };
        var supervisor = new AdbServerSupervisor(connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: new TestExecutableLocator { Result = @"C:\fake\adb.exe" }, processRunner: runner, getUtcNow: () => _now);
        await supervisor.RunIterationAsync(TestContext.Current.CancellationToken);
        _now = _now.AddSeconds(11);
        var failed = await supervisor.RunIterationAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AdbServerPhase.Unavailable, failed.Phase);
        Assert.Equal(2, runner.CallCount);
        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task MissingExecutableLaunchIsAnObservedFailureResult() {
        var result = await new AdbProcessRunner().StartServerAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "adb.exe"), TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("launch failed", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitRevalidationRefreshesCapabilitiesAtSameProtocolVersion() {
        await using var server = new FakeAdbServer();
        int statusQueries = 0;
        server.SetHandler(async (socket, command) => {
            if (command == "host:version") {
                await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            }
            else {
                statusQueries++;
                var payload = CreateServerStatusPayload();
                await socket.SendAsync(Encoding.ASCII.GetBytes($"OKAY{payload.Length:X4}"), SocketFlags.None);
                await socket.SendAsync(payload, SocketFlags.None);
            }
        });
        var supervisor = new AdbServerSupervisor(connectionFactory: () => new AdbHostConnection(server.EndPoint));
        var first = await supervisor.RunIterationAsync();
        supervisor.RequestProbe();
        var second = await supervisor.RunIterationAsync();
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.Equal(first.ProtocolVersion, second.ProtocolVersion);
        Assert.Equal(2, statusQueries);
        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task RequestProbeInterruptsLongBackoff() {
        await using var server = new FakeAdbServer();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var supervisor = new AdbServerSupervisor(connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: new TestExecutableLocator(), delay: (_, ct) => {
                waiting.TrySetResult();
                return Task.Delay(TimeSpan.FromMinutes(1), ct);
            });
        var running = supervisor.RunAsync(stop.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SetHandler(async (socket, command) => {
            byte[] response = command == "host:version" ? "OKAY00040029"u8.ToArray() : "OKAY0000"u8.ToArray();
            await socket.SendAsync(response, SocketFlags.None);
        });
        supervisor.StateChanged += state => { if (state.Phase == AdbServerPhase.Up) recovered.TrySetResult(); };
        supervisor.RequestProbe();
        try {
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
        finally {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    private sealed class TestProcessRunner : IAdbProcessRunner {
        public int CallCount { get; private set; }
        public List<string> ExecutablePathsCalled { get; } = new();
        public Func<string, CancellationToken, Task<ProcessResult>>? Handler { get; set; }

        public Task<ProcessResult> StartServerAsync(string executablePath, CancellationToken ct = default) {
            CallCount++;
            ExecutablePathsCalled.Add(executablePath);
            if (Handler != null) {
                return Handler(executablePath, ct);
            }
            return Task.FromResult(new ProcessResult(0, "daemon started successfully", string.Empty, false));
        }

        public Task<ProcessResult> KillServerAsync(string executablePath, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Supervisor scenarios never kill the server (decision 0008 amendment governs the only kill path).");
    }

    private sealed class TestExecutableLocator : IAdbExecutableLocator {
        public int CallCount { get; private set; }
        public string? Result { get; set; }

        public string? Locate() {
            CallCount++;
            return Result;
        }
    }

    private static byte[] CreateServerStatusPayload(string version = "37.0.1") {
        var ms = new List<byte>();
        // field 5 (version, wire 2)
        byte[] verBytes = Encoding.UTF8.GetBytes(version);
        ms.Add(0x2A);
        ms.Add((byte)verBytes.Length);
        ms.AddRange(verBytes);
        return ms.ToArray();
    }

    [Fact]
    public async Task ScenarioA_ServerAlreadyUp_ProbesCapabilitiesOnceAndNeverInvokesLocator() {
        // Test A: server already Up
        // - no executable discovery/start required
        // - one capability probe for generation
        // - Up event
        await using var server = new FakeAdbServer();
        int statusQueries = 0;
        server.SetHandler(async (socket, cmd) => {
            if (cmd == "host:version") {
                await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            }
            else if (cmd == "host:server-status") {
                statusQueries++;
                byte[] proto = CreateServerStatusPayload("37.0.1");
                byte[] header = Encoding.ASCII.GetBytes($"OKAY{proto.Length:X4}");
                await socket.SendAsync(header, SocketFlags.None);
                await socket.SendAsync(proto, SocketFlags.None);
            }
        });

        var locator = new TestExecutableLocator { Result = @"C:\Android\adb.exe" };
        var processRunner = new TestProcessRunner();

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: locator,
            processRunner: processRunner,
            getUtcNow: () => _now);

        var state = await supervisor.RunIterationAsync();

        Assert.Equal(AdbServerPhase.Up, state.Phase);
        Assert.Equal(41, state.ProtocolVersion);
        Assert.Equal(1, state.Generation);
        Assert.NotNull(state.Status);
        Assert.Equal("37.0.1", state.Status!.Version);

        // Crucial invariant: executable discovery must NOT run when server is already Up
        Assert.Equal(0, locator.CallCount);
        Assert.Equal(0, processRunner.CallCount);
        Assert.Equal(1, statusQueries);

        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task ScenarioB_ServerAbsentAtStartup_StartsServerAndTransitionsToUp() {
        // Test B: server absent at startup + adb.exe found
        // - probe fails
        // - start-server requested exactly once
        // - fake server becomes available
        // - reprobe succeeds
        // - Up
        await using var server = new FakeAdbServer();
        bool serverLive = false;

        server.SetHandler(async (socket, cmd) => {
            if (!serverLive) {
                socket.Close();
                return;
            }

            if (cmd == "host:version") {
                await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            }
            else if (cmd == "host:server-status") {
                byte[] proto = CreateServerStatusPayload("37.0.1");
                byte[] header = Encoding.ASCII.GetBytes($"OKAY{proto.Length:X4}");
                await socket.SendAsync(header, SocketFlags.None);
                await socket.SendAsync(proto, SocketFlags.None);
            }
        });

        var locator = new TestExecutableLocator { Result = Path.GetFullPath(@"C:\Android\platform-tools\adb.exe") };
        var processRunner = new TestProcessRunner {
            Handler = (exe, ct) => {
                // Simulates start-server bringing up the daemon
                serverLive = true;
                return Task.FromResult(new ProcessResult(0, "* daemon started successfully *", string.Empty, false));
            }
        };

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: locator,
            processRunner: processRunner,
            getUtcNow: () => _now);

        var state = await supervisor.RunIterationAsync();

        Assert.Equal(AdbServerPhase.Up, state.Phase);
        Assert.Equal(1, processRunner.CallCount);
        Assert.Equal(1, locator.CallCount);
        Assert.Equal(Path.GetFullPath(@"C:\Android\platform-tools\adb.exe"), processRunner.ExecutablePathsCalled[0]);

        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task ScenarioC_ServerAbsentAndAdbMissing_RestoringBefore10s_UnavailableAt10s() {
        // Test C: server absent + adb.exe missing
        // - no process launch
        // - restoring before 10s
        // - unavailable at/after 10s
        await using var server = new FakeAdbServer();
        // Server immediately rejects/closes
        server.SetHandler((socket, cmd) => {
            socket.Close();
            return Task.CompletedTask;
        });

        var locator = new TestExecutableLocator { Result = null };
        var processRunner = new TestProcessRunner();

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: locator,
            processRunner: processRunner,
            getUtcNow: () => _now);

        // t = 0s: initial probe failure, missing adb.exe
        var stateT0 = await supervisor.RunIterationAsync();
        Assert.Equal(AdbServerPhase.Restoring, stateT0.Phase);
        Assert.Equal(0, processRunner.CallCount);

        // t = 5s: still within the 10s restoring window
        _now += TimeSpan.FromSeconds(5);
        var stateT5 = await supervisor.RunIterationAsync();
        Assert.Equal(AdbServerPhase.Restoring, stateT5.Phase);
        Assert.Equal(0, processRunner.CallCount);

        // t = 10s: reached 10s threshold -> Unavailable
        _now += TimeSpan.FromSeconds(5);
        var stateT10 = await supervisor.RunIterationAsync();
        Assert.Equal(AdbServerPhase.Unavailable, stateT10.Phase);
        Assert.Equal("adb.exe not found", stateT10.Reason);
        Assert.Equal(0, processRunner.CallCount);
    }

    [Fact]
    public async Task ScenarioD_StartServerFailure_RestoringBefore10s_UnavailableAt10sWithTypedReason() {
        // Test D: start-server process failure
        // - typed reason retained
        // - restoring/unavailable timeline follows frozen rule
        await using var server = new FakeAdbServer();
        server.SetHandler((socket, cmd) => {
            socket.Close();
            return Task.CompletedTask;
        });

        var locator = new TestExecutableLocator { Result = Path.GetFullPath(@"C:\Android\platform-tools\adb.exe") };
        var processRunner = new TestProcessRunner {
            Handler = (exe, ct) => Task.FromResult(new ProcessResult(1, string.Empty, "error: permission denied", false))
        };

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: locator,
            processRunner: processRunner,
            getUtcNow: () => _now);

        // t = 0s
        var stateT0 = await supervisor.RunIterationAsync();
        Assert.Equal(AdbServerPhase.Restoring, stateT0.Phase);

        // t = 10s
        _now += TimeSpan.FromSeconds(10);
        var stateT10 = await supervisor.RunIterationAsync();
        Assert.Equal(AdbServerPhase.Unavailable, stateT10.Phase);
        Assert.Contains("error: permission denied", stateT10.Reason);
    }

    [Fact]
    public async Task ScenarioE_StartServerTimeout_AttemptsCleanupAndReportsUnavailableAfter10s() {
        // Test E: start-server process timeout
        await using var server = new FakeAdbServer();
        server.SetHandler((socket, cmd) => {
            socket.Close();
            return Task.CompletedTask;
        });

        var locator = new TestExecutableLocator { Result = Path.GetFullPath(@"C:\Android\platform-tools\adb.exe") };
        var processRunner = new TestProcessRunner {
            Handler = (exe, ct) => Task.FromResult(new ProcessResult(-1, string.Empty, "Process timed out after 20 seconds", true))
        };

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: locator,
            processRunner: processRunner,
            getUtcNow: () => _now);

        // t = 0s: initial failure, status is Restoring
        var stateT0 = await supervisor.RunIterationAsync();
        Assert.Equal(AdbServerPhase.Restoring, stateT0.Phase);

        // Advance to 10s: reached 10s threshold -> Unavailable
        _now += TimeSpan.FromSeconds(10);
        var state = await supervisor.RunIterationAsync();

        Assert.Equal(AdbServerPhase.Unavailable, state.Phase);
        Assert.Contains("timed out", state.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScenarioF_RecoveryAfterFailure_ResetsBackoff() {
        // Test F: recovery after failure; next iteration succeeds, backoff resets
        await using var server = new FakeAdbServer();
        bool isUp = false;

        server.SetHandler(async (socket, cmd) => {
            if (!isUp) {
                socket.Close();
                return;
            }
            if (cmd == "host:version") {
                await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            }
            else if (cmd == "host:server-status") {
                byte[] proto = CreateServerStatusPayload("37.0.1");
                byte[] header = Encoding.ASCII.GetBytes($"OKAY{proto.Length:X4}");
                await socket.SendAsync(header, SocketFlags.None);
                await socket.SendAsync(proto, SocketFlags.None);
            }
        });

        var backoff = new Backoff(jitterProvider: () => 0.0);
        var locator = new TestExecutableLocator { Result = null };
        var processRunner = new TestProcessRunner();

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: locator,
            processRunner: processRunner,
            backoff: backoff,
            getUtcNow: () => _now);

        // Fail once: advances backoff step
        await supervisor.RunIterationAsync();
        backoff.NextDelay(); // advance backoff step
        Assert.True(backoff.CurrentStep > 0);

        // Now daemon becomes available
        isUp = true;
        var recoveredState = await supervisor.RunIterationAsync();

        Assert.Equal(AdbServerPhase.Up, recoveredState.Phase);
        Assert.Equal(0, backoff.CurrentStep); // Backoff was reset!
    }

    [Fact]
    public async Task ScenarioG_ServerDisappearsAfterUp_StartsNewOutageAndRestores() {
        // Test G: server disappears after Up
        await using var server = new FakeAdbServer();
        bool isUp = true;

        server.SetHandler(async (socket, cmd) => {
            if (!isUp) {
                socket.Close();
                return;
            }
            if (cmd == "host:version") {
                await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            }
            else if (cmd == "host:server-status") {
                byte[] proto = CreateServerStatusPayload("37.0.1");
                byte[] header = Encoding.ASCII.GetBytes($"OKAY{proto.Length:X4}");
                await socket.SendAsync(header, SocketFlags.None);
                await socket.SendAsync(proto, SocketFlags.None);
            }
        });

        var locator = new TestExecutableLocator { Result = Path.GetFullPath(@"C:\Android\platform-tools\adb.exe") };
        var processRunner = new TestProcessRunner();

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: locator,
            processRunner: processRunner,
            getUtcNow: () => _now);

        // Step 1: Up
        var state1 = await supervisor.RunIterationAsync();
        Assert.Equal(AdbServerPhase.Up, state1.Phase);

        // Step 2: Server dies (e.g. adb kill-server from another tool)
        isUp = false;
        _now += TimeSpan.FromSeconds(100); // previous time advance should not taint the new outage

        var state2 = await supervisor.RunIterationAsync();
        // New outage at t=0 (< 10s) must be Restoring
        Assert.Equal(AdbServerPhase.Restoring, state2.Phase);

        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task ScenarioH_CapabilityProbeOccursOncePerGeneration() {
        // Test H: capability probe occurs once per generation
        await using var server = new FakeAdbServer();
        int versionQueries = 0;
        int statusQueries = 0;

        server.SetHandler(async (socket, cmd) => {
            if (cmd == "host:version") {
                versionQueries++;
                await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            }
            else if (cmd == "host:server-status") {
                statusQueries++;
                byte[] proto = CreateServerStatusPayload("37.0.1");
                byte[] header = Encoding.ASCII.GetBytes($"OKAY{proto.Length:X4}");
                await socket.SendAsync(header, SocketFlags.None);
                await socket.SendAsync(proto, SocketFlags.None);
            }
        });

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: new TestExecutableLocator(),
            processRunner: new TestProcessRunner(),
            getUtcNow: () => _now);

        // 3 consecutive iterations while Up
        await supervisor.RunIterationAsync();
        await supervisor.RunIterationAsync();
        await supervisor.RunIterationAsync();

        // 3 liveness checks, but EXACTLY 1 status query
        Assert.Equal(3, versionQueries);
        Assert.Equal(1, statusQueries);
    }

    [Fact]
    public async Task ScenarioI_VersionChange_CausesGenerationIncrementAndCapabilityRefresh() {
        // Test I: version/status change causes generation/capability refresh
        await using var server = new FakeAdbServer();
        int versionReturned = 41;
        int statusQueries = 0;

        server.SetHandler(async (socket, cmd) => {
            if (cmd == "host:version") {
                string hex = versionReturned.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
                await socket.SendAsync(Encoding.ASCII.GetBytes($"OKAY0004{hex}"), SocketFlags.None);
            }
            else if (cmd == "host:server-status") {
                statusQueries++;
                byte[] proto = CreateServerStatusPayload(versionReturned == 41 ? "37.0.1" : "38.0.0");
                byte[] header = Encoding.ASCII.GetBytes($"OKAY{proto.Length:X4}");
                await socket.SendAsync(header, SocketFlags.None);
                await socket.SendAsync(proto, SocketFlags.None);
            }
        });

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: new TestExecutableLocator(),
            processRunner: new TestProcessRunner(),
            getUtcNow: () => _now);

        // Generation 1 at version 41
        var state1 = await supervisor.RunIterationAsync();
        Assert.Equal(1, state1.Generation);
        Assert.Equal(41, state1.ProtocolVersion);
        Assert.Equal(1, statusQueries);

        // Version changes to 42
        versionReturned = 42;
        var state2 = await supervisor.RunIterationAsync();

        Assert.Equal(2, state2.Generation);
        Assert.Equal(42, state2.ProtocolVersion);
        Assert.Equal(2, statusQueries);
        Assert.Equal("38.0.0", state2.Status!.Version);
    }

    [Fact]
    public async Task ScenarioJ_Cancellation_StopsSupervisorPromptly() {
        // Test J: cancellation stops supervisor promptly
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var supervisor = new AdbServerSupervisor();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.RunAsync(cts.Token));
    }
}
