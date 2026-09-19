#pragma warning disable CA1707
namespace AdbView.Tests.Adb.Protocol;

using AdbView.Adb.Protocol;
using Xunit;

public sealed class AdbHostServiceDiagnosticsTests {
    [Theory]
    [InlineData("unknown host service")]
    [InlineData("unknown host service 'host:track-devices-proto-binary'")]
    [InlineData("unknown host service 'host:track-devices-l'")]
    public void IsUnknownHostService_AospDiagnostics_True(string reason) {
        Assert.True(AdbHostServiceDiagnostics.IsUnknownHostService(reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("device offline")]
    [InlineData("closed")]
    [InlineData("Unknown Host Service")]
    [InlineData("service unknown host service")]
    public void IsUnknownHostService_OtherDiagnostics_False(string? reason) {
        Assert.False(AdbHostServiceDiagnostics.IsUnknownHostService(reason));
    }
}
