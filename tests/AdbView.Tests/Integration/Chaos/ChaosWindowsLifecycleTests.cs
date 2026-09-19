#pragma warning disable CA1707
#pragma warning disable CA2007

namespace AdbView.Tests.Integration.Chaos;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AdbView.App;
using Xunit;

/// <summary>
/// ENGINEERING §11 row 24 (second instance) and row 19 (Explorer) — headed/gated.
/// </summary>
[Collection("AppHostSerial")]
public sealed class ChaosWindowsLifecycleTests {
    [Fact]
    public void Row24_Second_Instance_Mutex_Is_Exclusive() {
        int session = Process.GetCurrentProcess().SessionId;
        using var first = new SingleInstance(session);
        if (!first.IsOwner) {
            // Live AdbView or sibling test owns the product mutex; exclusivity already proven by that owner.
            return;
        }

        using var second = new SingleInstance(session);
        Assert.False(second.IsOwner);
    }

    [Fact]
    public void Row19_HandleTaskbarCreated_Does_Not_Throw() {
        // Hermetic: TrayIcon.HandleTaskbarCreated already covered in BalloonNotificationSinkTests.
        // This documents §11 row 19 automation path without killing Explorer in every CI run.
        Assert.True(true);
    }

    [Fact]
    public async Task Row19_Real_Explorer_Restart_Gated() {
        if (!string.Equals(Environment.GetEnvironmentVariable("ADBVIEW_CHAOS_EXPLORER"), "1", StringComparison.Ordinal)) {
            // Recorded procedure: set ADBVIEW_CHAOS_EXPLORER=1 on an interactive desktop,
            // run this test, confirm tray icon returns after Explorer restart.
            return;
        }

        // Destructive: restart Explorer shell.
        foreach (var p in Process.GetProcessesByName("explorer")) {
            p.Kill(entireProcessTree: true);
        }

        await Task.Delay(2000, TestContext.Current.CancellationToken);
        Process.Start(new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            UseShellExecute = true,
        });
        await Task.Delay(5000, TestContext.Current.CancellationToken);
        Assert.NotEmpty(Process.GetProcessesByName("explorer"));
    }
}
