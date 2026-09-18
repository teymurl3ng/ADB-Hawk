#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Protocol;
using Xunit;

public sealed class AdbLiveSmokeTests {
    private readonly ITestOutputHelper _output;

    public AdbLiveSmokeTests(ITestOutputHelper output) {
        _output = output;
    }

    [Fact]
    public async Task LiveAdbServer_IfRunning_ReturnsValidVersionAndStatus() {
        // Safe, non-mutating smoke check against live 127.0.0.1:5037.
        // If an ADB server is not running on the machine, skips cleanly without failing CI.
        try {
            var capabilities = new AdbCapabilities();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            int version = await capabilities.GetVersionAsync(cts.Token);
            _output.WriteLine($"[LiveSmoke] Live ADB server responded to host:version with protocol version {version}.");
            Assert.True(version > 0, "ADB protocol version must be greater than 0.");

            try {
                var status = await capabilities.GetServerStatusAsync(cts.Token);
                _output.WriteLine($"[LiveSmoke] Live ADB server responded to host:server-status: version='{status.Version}', mdns_enabled={status.MdnsEnabled}, mdns_backend={status.MdnsBackend}.");
            }
            catch (AdbProtocolException ex) when (ex.ErrorCode == AdbErrorCode.CommandFailed) {
                // Older adb server versions may not support host:server-status
                _output.WriteLine($"[LiveSmoke] host:server-status not supported on running server: {ex.Message}");
            }
        }
        catch (AdbConnectionException ex) {
            _output.WriteLine($"[LiveSmoke] No live ADB server available at 127.0.0.1:5037 ({ex.Message}); live smoke skipped.");
            Assert.Skip($"Live ADB server not running at 127.0.0.1:5037 ({ex.Message}).");
        }
        catch (OperationCanceledException) {
            _output.WriteLine("[LiveSmoke] Connect/query timed out to 127.0.0.1:5037; live smoke skipped.");
            Assert.Skip("Connect/query timed out to 127.0.0.1:5037.");
        }
    }
}
