namespace AdbView.WinUiMigration.NativeUi;

using System.Collections.Immutable;
using System.Globalization;
using AdbView.Bridge;
using AdbView.State;
using QRCoder;

internal static class DeviceFixtures {
    public static readonly ImmutableArray<string> Names = ["empty", "list", "details", "pairing", "adb-unavailable", "adb-restoring",
        "wireless-unavailable", "wireless-restoring", "two-devices", "multi-device", "many-devices", "long-names", "missing-metadata",
        "unknown", "unauthorized", "offline", "connected-wifi", "text-200", "list-scenarios", "pairing-scenarios",
        "pairing-offer", "pairing-progress", "pairing-progress-reduced", "pairing-success", "pairing-failure", "pairing-qr", "discovery-health", "motion-scenarios", "motion-recording", "native-scenarios", "native-bubble"];

    public static AppSnapshot Snapshot(string fixture, long revision = 1) {
        string canonical = fixture switch {
            "list" or "details" or "two-devices" or "multi-device" or "many-devices" or "long-names" or "text-200" or "list-scenarios" or "missing-metadata" or "motion-scenarios" or "motion-recording" or "native-scenarios" or "native-bubble" => "connected-usb",
            "pairing" or "pairing-scenarios" => "pairing-code",
            "pairing-progress-reduced" => "pairing-progress",
            _ => fixture,
        };
        AppSnapshot snapshot = FixtureSnapshotProvider.GetSnapshot(canonical, revision: revision);
        if (fixture == "pairing-progress-reduced") { snapshot = snapshot with { Shell = snapshot.Shell with { ReducedMotion = true } }; }
        if (snapshot.PairingQr is { } qr) {
            // Deliberately not a WIFI/ADB payload and contains no pairing credential.
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode("ADB View nonsecret rendering fixture", QRCodeGenerator.ECCLevel.M);
            using var renderer = new SvgQRCode(data);
            snapshot = snapshot with {
                PairingQr = qr with {
                    SvgDataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(renderer.GetGraphic(4))),
                }
            };
        }
        int count = fixture switch {
            "two-devices" => 2,
            "multi-device" or "text-200" => 8,
            "many-devices" or "list-scenarios" => 50,
            "long-names" => 2,
            _ => snapshot.Devices.Length,
        };
        if (snapshot.Devices.Length > 0 && count != snapshot.Devices.Length) {
            DeviceView original = snapshot.Devices[0];
            snapshot = snapshot with {
                Devices = Enumerable.Range(0, count).Select(index => original with {
                    Id = "fixture-device-" + index.ToString(CultureInfo.InvariantCulture),
                    DisplayName = index == 0 ? original.DisplayName : "Phone " + (index + 1).ToString(CultureInfo.InvariantCulture),
                }).ToImmutableArray(),
                DeviceCountLabel = count.ToString(CultureInfo.InvariantCulture) + " devices",
            };
        }
        if (fixture is "long-names" or "text-200") {
            snapshot = snapshot with {
                Devices = snapshot.Devices.Select(device => device with {
                    DisplayName = "A long phone name that must remain readable without widening the ADB View panel",
                }).ToImmutableArray()
            };
        }
        if (fixture == "missing-metadata") {
            snapshot = snapshot with {
                Devices = snapshot.Devices.Select(device => device with {
                    BatteryPercent = null,
                    Detail = new DeviceDetailView { CopyFields = [] },
                }).ToImmutableArray()
            };
        }
        return snapshot;
    }
}
