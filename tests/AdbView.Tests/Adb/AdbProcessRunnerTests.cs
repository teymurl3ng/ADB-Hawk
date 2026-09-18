#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AdbView.Adb;
using Xunit;

public sealed class AdbProcessRunnerTests {
    [Fact]
    public async Task StartServerAsync_RelativePath_ThrowsArgumentException() {
        var runner = new AdbProcessRunner();
        await Assert.ThrowsAsync<ArgumentException>(() => runner.StartServerAsync("adb.exe"));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.StartServerAsync(@"platform-tools\adb.exe"));
    }

    [Fact]
    public async Task StartServerAsync_NullPath_ThrowsArgumentNullException() {
        var runner = new AdbProcessRunner();
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.StartServerAsync(null!));
    }

    [Fact]
    public void ProcessExecutionContract_InvariantsEnforced() {
        // Test L & M: no kill-server command ever constructed, environment unmodified, UseShellExecute = false
        // We verify that the process start contract uses exact invariants
        string dummyExe = Path.GetFullPath(@"C:\Android\platform-tools\adb.exe");
        var psi = AdbProcessRunner.CreateStartInfo(dummyExe);

        Assert.Equal("start-server", psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);

        // Confirm no forbidden ADB_* variable is ever modified
        Assert.False(psi.Environment.ContainsKey("ADB_MDNS_OPENSCREEN"));
        Assert.False(psi.Environment.ContainsKey("ADB_MDNS"));
        Assert.False(psi.Environment.ContainsKey("ADB_LIBUSB"));
        Assert.False(psi.Environment.ContainsKey("ADB_TRACE"));
    }
}
