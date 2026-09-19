#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable xUnit1030
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using Xunit;

/// <summary>Observe-only Phase 7A metadata smoke. Skips when Samsung A06 is not attached.</summary>
public sealed class MetadataHardwareSmokeTests {
    private readonly ITestOutputHelper _output;

    public MetadataHardwareSmokeTests(ITestOutputHelper output) {
        _output = output;
    }

    [Fact]
    public async Task Observe_Metadata_On_SamsungA06_IfAttached() {
        string? usbSerial = await FindSamsungSerialAsync(preferWifi: false);
        if (usbSerial is null) {
            _output.WriteLine("[MetadataSmoke] Samsung A06 not attached; skipped.");
            return;
        }

        var probe = new MetadataProbe(new MetadataDeviceShellClient());
        var target = new MetadataProbeTarget("usb:" + usbSerial, usbSerial, Generation: 1);
        var result = await probe.ProbeAsync(target, CancellationToken.None);

        _output.WriteLine(
            $"[MetadataSmoke] AdbSerial={usbSerial} Battery={result.BatteryPercent} ({result.BatteryStatus}) Model={result.ExactModel} ({result.ExactModelStatus}) Version={result.AndroidVersion} ({result.AndroidVersionStatus})");

        Assert.Equal(MetadataFieldStatus.Ok, result.BatteryStatus);
        Assert.InRange(result.BatteryPercent!.Value, 0, 100);
        Assert.Equal(MetadataFieldStatus.Ok, result.ExactModelStatus);
        Assert.False(string.IsNullOrWhiteSpace(result.ExactModel));
        Assert.Equal(MetadataFieldStatus.Ok, result.AndroidVersionStatus);
        Assert.False(string.IsNullOrWhiteSpace(result.AndroidVersion));

        // Optional Wi-Fi targeting if present (do not recreate pairing).
        string? wifiSerial = await FindSamsungSerialAsync(preferWifi: true);
        if (wifiSerial is not null && !string.Equals(wifiSerial, usbSerial, StringComparison.Ordinal)) {
            var wifiResult = await probe.ProbeCommandAsync(
                new MetadataProbeTarget("wifi:" + wifiSerial, wifiSerial, 1),
                MetadataCommand.ExactModel,
                CancellationToken.None);
            _output.WriteLine($"[MetadataSmoke] Wi-Fi AdbSerial={wifiSerial} Model={wifiResult.TextValue}");
            Assert.Equal(MetadataFieldStatus.Ok, wifiResult.Status);
            Assert.Equal(result.ExactModel, wifiResult.TextValue);
        }
    }

    private static async Task<string?> FindSamsungSerialAsync(bool preferWifi) {
        try {
            await using var conn = new AdbHostConnection();
            // Prefer track-devices-l text for a quick serial list without proto dependency here.
            var payload = await conn.ExecuteOneShotAsync("host:devices-l");
            string text = System.Text.Encoding.UTF8.GetString(payload.Span);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines) {
                if (!line.Contains("device", StringComparison.Ordinal)) {
                    continue;
                }

                bool looksSamsung = line.Contains("A065", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("R8VXA02SNKH", StringComparison.Ordinal);
                if (!looksSamsung) {
                    continue;
                }

                string serial = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                bool isWifi = serial.Contains(':', StringComparison.Ordinal);
                if (preferWifi == isWifi) {
                    return serial;
                }
            }

            // Fall back: any Samsung serial if preferred transport missing.
            foreach (var line in lines) {
                if (!line.Contains("device", StringComparison.Ordinal)) {
                    continue;
                }

                bool looksSamsung = line.Contains("A065", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("R8VXA02SNKH", StringComparison.Ordinal);
                if (!looksSamsung) {
                    continue;
                }

                return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            }
        }
        catch (AdbException) {
            return null;
        }
        catch (OperationCanceledException) {
            return null;
        }

        return null;
    }
}
