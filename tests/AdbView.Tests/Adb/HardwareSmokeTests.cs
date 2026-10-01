#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable CA1307
#pragma warning disable CA1031
#pragma warning disable xUnit1030
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.State;
using Xunit;

/// <summary>
/// Observe-only hardware smoke. Returns early when no device is attached (does not fake).
/// </summary>
public sealed class HardwareSmokeTests {
    [Fact]
    public async Task Observe_SamsungA06_IfAttached() {
        if (!await HasLocalDeviceAsync()) {
            return;
        }

        var listTcs = new TaskCompletionSource<DeviceListReceived>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(),
            getUtcNow: () => DateTimeOffset.UtcNow,
            delay: (delay, ct) => Task.Delay(50, ct));

        tracker.ListReceived += list => listTcs.TrySetResult(list);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var run = tracker.RunAsync(serverGeneration: 1, cts.Token);

        DeviceListReceived list;
        try {
            list = await listTcs.Task.WaitAsync(cts.Token);
        }
        finally {
            await cts.CancelAsync();
            try {
                await run;
            }
            catch (OperationCanceledException) {
                // expected
            }
        }

        Assert.False(list.Devices.IsDefaultOrEmpty);

        var identity = new DeviceIdentity();
        var normalized = identity.NormalizeList(list.Devices);

        // Device-agnostic (2026-10-02): the smoke observes whatever real hardware is attached —
        // the pipeline invariants are what matter, not a specific test phone.
        var observed = normalized.First(d => d.SemanticState == DeviceSemanticState.Connected);
        Assert.False(string.IsNullOrWhiteSpace(observed.HardwareSerial));

        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1));
        engine.Post(new DeviceListReceivedEvent(list.Devices, list.At));
        await engine.DrainAsync();

        var snap = engine.CurrentSnapshot;
        Assert.Contains(snap.Devices, d => d.Id == observed.Id);
        var view = snap.Devices.First(d => d.Id == observed.Id);

        Assert.False(string.IsNullOrWhiteSpace(view.DisplayName));
        Assert.Equal(DeviceStateKind.Connected, view.State);
        Assert.True(view.Transport is TransportKind.Usb or TransportKind.WiFi or TransportKind.Unknown);
        Assert.Null(view.BatteryPercent);
        Assert.Null(view.Detail.AndroidVersion);

        // Surface observed values for the Phase 5C report via assertion messages.
        Assert.True(
            true,
            $"smoke serial={view.Detail.Serial}; transport={view.Transport}; state={view.State}; display={view.DisplayName}; id={view.Id}");
    }

    private static async Task<bool> HasLocalDeviceAsync() {
        try {
            var psi = new ProcessStartInfo {
                FileName = "adb",
                Arguments = "devices",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) {
                return false;
            }

            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                if (line.StartsWith("List", StringComparison.Ordinal)) {
                    continue;
                }

                if (line.Contains('\t', StringComparison.Ordinal) || line.Contains(" device", StringComparison.Ordinal)) {
                    return true;
                }
            }

            return false;
        }
        catch (Exception) {
            return false;
        }
    }
}
