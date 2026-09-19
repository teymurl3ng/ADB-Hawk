#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class MetadataProbeTests {
    private static MetadataProbeTarget Target(string serial, string deviceId = "usb:R8", long gen = 1) =>
        new(deviceId, serial, gen);

    private static MetadataProbe CreateProbe(FakeAdbServer server, List<TimeSpan>? delays = null, TimeSpan? timeout = null) {
        var shell = new MetadataDeviceShellClient(
            endPoint: server.EndPoint,
            requestTimeout: timeout ?? TimeSpan.FromSeconds(5));
        return new MetadataProbe(
            shell,
            delay: (d, ct) => {
                delays?.Add(d);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task A_Correct_Host_Transport_Serial_Selected() {
        await using var server = new FakeAdbServer();
        var transports = new List<string>();
        server.RespondDeviceShell((serial, shell) => {
            if (shell.Length == 0) {
                return FakeDeviceShellPlan.Ok(Array.Empty<byte>());
            }

            transports.Add(serial);
            Assert.Equal("shell:getprop ro.product.model", shell);
            return FakeDeviceShellPlan.OkText("SM-A065F\n");
        });

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("R8VXA02SNKH"), MetadataCommand.ExactModel, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Ok, result.Status);
        Assert.Equal("SM-A065F", result.TextValue);
        Assert.Contains("R8VXA02SNKH", transports);
        Assert.Contains(server.ReceivedCommands, c => c == "host:transport:R8VXA02SNKH");
    }

    [Fact]
    public async Task B_Usb_Serial_Preserved_Exactly() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText("ok\n"));

        var probe = CreateProbe(server);
        await probe.ProbeCommandAsync(Target("R8VXA02SNKH"), MetadataCommand.ExactModel, CancellationToken.None);
        Assert.Contains(server.ReceivedCommands, c => c == "host:transport:R8VXA02SNKH");
        Assert.DoesNotContain(server.ReceivedCommands, c => c.Contains("usb:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task C_Wifi_HostPort_Serial_Preserved_Exactly() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText("SM-A065F\n"));

        var probe = CreateProbe(server);
        await probe.ProbeCommandAsync(Target("192.168.18.248:5555", "wifi:R8"), MetadataCommand.ExactModel, CancellationToken.None);
        Assert.Contains(server.ReceivedCommands, c => c == "host:transport:192.168.18.248:5555");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(42)]
    public async Task DEF_Battery_Valid_Values(int level) {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText($"  level: {level}\n"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Ok, result.Status);
        Assert.Equal(level, result.BatteryPercent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task GH_Battery_OutOfRange_Rejected(int level) {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText($"  level: {level}\n"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Unavailable, result.Status);
        Assert.Null(result.BatteryPercent);
    }

    [Fact]
    public async Task I_Battery_NonNumeric_Rejected() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText("  level: xx\n"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task J_Battery_Missing_Level_Rejected() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText("Current Battery Service state:\n  scale: 100\n"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task K_Model_Valid() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText("SM-A065F\n"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.ExactModel, CancellationToken.None);
        Assert.Equal("SM-A065F", result.TextValue);
    }

    [Fact]
    public async Task L_Model_Empty_Null() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText("\n"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.ExactModel, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Unavailable, result.Status);
        Assert.Null(result.TextValue);
    }

    [Fact]
    public async Task MN_AndroidVersion_Valid_And_Preview() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText("Baklava\n"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.AndroidVersion, CancellationToken.None);
        Assert.Equal("Baklava", result.TextValue);
    }

    [Fact]
    public async Task O_AndroidVersion_Empty_Null() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.OkText("   "));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.AndroidVersion, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Unavailable, result.Status);
        Assert.Null(result.TextValue);
    }

    [Fact]
    public async Task P_Transport_Fail_Local() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, _) => FakeDeviceShellPlan.TransportFail("device 'X' not found"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("X"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.TransportFailed, result.Status);
    }

    [Fact]
    public async Task Q_Shell_Fail_Local() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.ShellFail("closed"));

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.ShellFailed, result.Status);
    }

    [Fact]
    public async Task R_Timeout_Deterministic() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.Hang());

        var delays = new List<TimeSpan>();
        var probe = CreateProbe(server, delays, timeout: TimeSpan.FromMilliseconds(50));
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Timeout, result.Status);
        Assert.Equal(new[] { MetadataProbe.RetryDelay }, delays);
    }

    [Fact]
    public async Task S_Cancellation_Deterministic() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((_, shell) =>
            shell.Length == 0
                ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                : FakeDeviceShellPlan.Hang());

        using var cts = new CancellationTokenSource();
        var shell = new MetadataDeviceShellClient(server.EndPoint, requestTimeout: TimeSpan.FromSeconds(30));
        var probe = new MetadataProbe(shell, delay: (_, _) => Task.CompletedTask);

        var task = probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, cts.Token);
        await Task.Delay(30);
        await cts.CancelAsync();
        var result = await task;
        Assert.Equal(MetadataFieldStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task TUV_Retry_Once_After_Injected_2s_Then_Success() {
        await using var server = new FakeAdbServer();
        int shellHits = 0;
        server.RespondDeviceShell((_, shell) => {
            if (shell.Length == 0) {
                return FakeDeviceShellPlan.Ok(Array.Empty<byte>());
            }

            shellHits++;
            if (shellHits == 1) {
                return FakeDeviceShellPlan.ShellFail("busy");
            }

            return FakeDeviceShellPlan.OkText("  level: 55\n");
        });

        var delays = new List<TimeSpan>();
        var probe = CreateProbe(server, delays);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Ok, result.Status);
        Assert.Equal(55, result.BatteryPercent);
        Assert.Equal(2, shellHits);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task W_Retry_Exhaustion_Stops() {
        await using var server = new FakeAdbServer();
        int shellHits = 0;
        server.RespondDeviceShell((_, shell) => {
            if (shell.Length == 0) {
                return FakeDeviceShellPlan.Ok(Array.Empty<byte>());
            }

            shellHits++;
            return FakeDeviceShellPlan.ShellFail("nope");
        });

        var delays = new List<TimeSpan>();
        var probe = CreateProbe(server, delays);
        var result = await probe.ProbeCommandAsync(Target("S1"), MetadataCommand.Battery, CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.ShellFailed, result.Status);
        Assert.Equal(2, shellHits);
        Assert.Single(delays);
    }

    [Fact]
    public async Task X_Partial_Success_Preserved() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((serial, shell) => {
            if (shell.Length == 0) {
                return FakeDeviceShellPlan.Ok(Array.Empty<byte>());
            }

            return shell switch {
                "shell:dumpsys battery" => FakeDeviceShellPlan.OkText("  level: 80\n"),
                "shell:getprop ro.product.model" => FakeDeviceShellPlan.OkText("SM-A065F\n"),
                "shell:getprop ro.build.version.release" => FakeDeviceShellPlan.OkText("\n"),
                _ => FakeDeviceShellPlan.ShellFail("unexpected"),
            };
        });

        var probe = CreateProbe(server);
        var result = await probe.ProbeAsync(Target("S1"), CancellationToken.None);
        Assert.Equal(80, result.BatteryPercent);
        Assert.Equal("SM-A065F", result.ExactModel);
        Assert.Null(result.AndroidVersion);
        Assert.Equal(MetadataFieldStatus.Ok, result.BatteryStatus);
        Assert.Equal(MetadataFieldStatus.Ok, result.ExactModelStatus);
        Assert.Equal(MetadataFieldStatus.Unavailable, result.AndroidVersionStatus);
    }

    [Fact]
    public async Task Y_Two_Devices_Never_Cross_Target() {
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((serial, shell) => {
            if (shell.Length == 0) {
                return FakeDeviceShellPlan.Ok(Array.Empty<byte>());
            }

            string body = serial switch {
                "USB-SERIAL" => "USB-MODEL\n",
                "10.0.0.2:5555" => "WIFI-MODEL\n",
                _ => throw new InvalidOperationException($"Wrong serial: {serial}"),
            };
            return FakeDeviceShellPlan.OkText(body);
        });

        var probe = CreateProbe(server);
        var usb = await probe.ProbeCommandAsync(Target("USB-SERIAL", "usb:1"), MetadataCommand.ExactModel, CancellationToken.None);
        var wifi = await probe.ProbeCommandAsync(Target("10.0.0.2:5555", "wifi:1"), MetadataCommand.ExactModel, CancellationToken.None);
        Assert.Equal("USB-MODEL", usb.TextValue);
        Assert.Equal("WIFI-MODEL", wifi.TextValue);
    }

    [Fact]
    public void Z_No_Arbitrary_Shell_Command_Surface() {
        var methods = typeof(IMetadataDeviceShellClient).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.Single(methods);
        Assert.Equal("ExecuteFixedShellAsync", methods[0].Name);
        Assert.Equal(typeof(MetadataCommand), methods[0].GetParameters()[1].ParameterType);
        Assert.DoesNotContain(methods, m => m.GetParameters().Any(p => p.ParameterType == typeof(string) && p.Name is "command" or "shell" or "service"));
    }

    private static readonly string[] ExpectedShellSuffixes = [
        "dumpsys battery",
        "getprop ro.product.model",
        "getprop ro.build.version.release",
    ];

    [Fact]
    public void AA_Exactly_Three_CompileTime_Command_Strings() {
        var suffixes = MetadataDeviceShellClient.AllowedShellCommandSuffixes.ToArray();
        Assert.Equal(3, suffixes.Length);
        Assert.Equal(ExpectedShellSuffixes, suffixes);
        Assert.Equal("shell:dumpsys battery", MetadataDeviceShellClient.ServiceName(MetadataCommand.Battery));
        Assert.Equal("shell:getprop ro.product.model", MetadataDeviceShellClient.ServiceName(MetadataCommand.ExactModel));
        Assert.Equal(
            "shell:getprop ro.build.version.release",
            MetadataDeviceShellClient.ServiceName(MetadataCommand.AndroidVersion));
    }

    [Fact]
    public void AB_No_AdbExe_Shell_Dependency() {
        string text = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                AdbView.Tests.Boundaries.Repo.Root,
                "src", "AdbView", "Adb", "MetadataDeviceShellClient.cs"));
        Assert.DoesNotContain("adb.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProcessStartInfo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Diagnostics.Process", text, StringComparison.Ordinal);

        string probeText = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                AdbView.Tests.Boundaries.Repo.Root,
                "src", "AdbView", "Adb", "MetadataProbe.cs"));
        Assert.DoesNotContain("ProcessStartInfo", probeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutation_DeviceId_Must_Not_Be_Transport_Serial() {
        // Guard: host:transport must use AdbSerial, not DeviceId.
        await using var server = new FakeAdbServer();
        server.RespondDeviceShell((serial, shell) => {
            if (shell.Length == 0) {
                return serial == "REAL-SERIAL"
                    ? FakeDeviceShellPlan.Ok(Array.Empty<byte>())
                    : FakeDeviceShellPlan.TransportFail("wrong");
            }

            Assert.Equal("REAL-SERIAL", serial);
            return FakeDeviceShellPlan.OkText("ok\n");
        });

        var probe = CreateProbe(server);
        var result = await probe.ProbeCommandAsync(
            new MetadataProbeTarget("usb:REAL-SERIAL", "REAL-SERIAL", 1),
            MetadataCommand.ExactModel,
            CancellationToken.None);
        Assert.Equal(MetadataFieldStatus.Ok, result.Status);
        Assert.DoesNotContain(server.ReceivedCommands, c => c == "host:transport:usb:REAL-SERIAL");
    }

    [Fact]
    public void ExactModel_Source_Is_Getprop_Not_Tracker_Model() {
        // Mutation guard: no tracker-model API on MetadataProbe / parsers.
        Assert.Null(typeof(MetadataParsers).GetMethod("FromTrackerModel"));
        Assert.Null(typeof(MetadataProbe).GetMethod("FromTrackerModel"));
    }
}
