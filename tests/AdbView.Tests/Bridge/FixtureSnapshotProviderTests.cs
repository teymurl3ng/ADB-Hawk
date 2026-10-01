namespace AdbView.Tests.Bridge;

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdbView.Bridge;
using AdbView.State;
using AdbView.Tests.Boundaries;
using Xunit;

public sealed class FixtureSnapshotProviderTests {
    [Fact]
    public void CanonicalFixtureNamesContainsAllEighteen() {
        Assert.Equal(18, FixtureSnapshotProvider.CanonicalFixtureNames.Length);
        var expected = new[] {
            "empty", "connected-usb", "connected-wifi", "expanded",
            "unauthorized", "offline", "recovery", "bootloader",
            "sideload", "unknown", "multi-device", "pairing-offer",
            "pairing-code", "pairing-progress", "pairing-success", "pairing-failure",
            "discovery-health", "pairing-qr"
        };
        foreach (var name in expected) {
            Assert.Contains(name, FixtureSnapshotProvider.CanonicalFixtureNames);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("connected-usb")]
    [InlineData("connected-wifi")]
    [InlineData("expanded")]
    [InlineData("unauthorized")]
    [InlineData("offline")]
    [InlineData("recovery")]
    [InlineData("bootloader")]
    [InlineData("sideload")]
    [InlineData("unknown")]
    [InlineData("multi-device")]
    [InlineData("pairing-offer")]
    [InlineData("pairing-code")]
    [InlineData("pairing-progress")]
    [InlineData("pairing-success")]
    [InlineData("pairing-failure")]
    [InlineData("discovery-health")]
    [InlineData("pairing-qr")]
    public void AllCanonicalFixturesAreValidAndRoundtripSerializable(string fixtureName) {
        var snapshot = FixtureSnapshotProvider.GetSnapshot(fixtureName, isPinned: false, revision: 1);
        Assert.NotNull(snapshot);
        Assert.Equal(BridgeProtocol.Version, snapshot.Protocol);
        Assert.Equal(1, snapshot.Revision);
        Assert.Equal(FixtureSnapshotProvider.T0, snapshot.GeneratedAt);

        // Validation against schema invariants
        BridgeSerializer.ValidateSnapshot(snapshot);

        // Roundtrip serialization
        var json = BridgeSerializer.SerializeSnapshot(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(json));

        var roundtripped = BridgeSerializer.DeserializeSnapshot(json);
        Assert.NotNull(roundtripped);
        Assert.Equal(snapshot.Protocol, roundtripped.Protocol);
        Assert.Equal(snapshot.Revision, roundtripped.Revision);
        Assert.Equal(snapshot.Devices.Length, roundtripped.Devices.Length);
    }

    [Fact]
    public void ConnectedUsbHasUsbTransportAndNoAddress() {
        var snapshot = FixtureSnapshotProvider.GetSnapshot("connected-usb");
        Assert.Single(snapshot.Devices);
        var device = snapshot.Devices[0];

        Assert.Equal("Galaxy S24", device.DisplayName);
        Assert.Equal(DeviceStateKind.Connected, device.State);
        Assert.Equal(TransportKind.Usb, device.Transport);
        Assert.Equal("USB", device.TransportLabel);
        Assert.Equal(82, device.BatteryPercent);
        Assert.Equal("Connected {age}", device.MetaTemplate);
        Assert.Equal(FixtureSnapshotProvider.T0, device.StateSince);
        Assert.Null(device.Detail.Address);
        Assert.Equal("SM-S921W", device.Detail.ExactModel);
        Assert.Equal("R5CW••••••••", device.Detail.Serial);
    }

    [Fact]
    public void ConnectedWifiHasWifiTransportAndValidAddress() {
        var snapshot = FixtureSnapshotProvider.GetSnapshot("connected-wifi");
        Assert.Single(snapshot.Devices);
        var device = snapshot.Devices[0];

        Assert.Equal("Galaxy S24", device.DisplayName);
        Assert.Equal(DeviceStateKind.Connected, device.State);
        Assert.Equal(TransportKind.WiFi, device.Transport);
        Assert.Equal("Wi-Fi", device.TransportLabel);
        Assert.Equal(82, device.BatteryPercent);
        Assert.Equal("Connected {age}", device.MetaTemplate);
        Assert.Equal(FixtureSnapshotProvider.T0 - TimeSpan.FromSeconds(46), device.StateSince);
        Assert.Equal("192.168.1.144:39421", device.Detail.Address);
        Assert.Equal("SM-S921W", device.Detail.ExactModel);
        Assert.Equal("R5CW••••••••", device.Detail.Serial);
    }

    [Fact]
    public void ExpandedHasStateSince120SecondsAgo() {
        var snapshot = FixtureSnapshotProvider.GetSnapshot("expanded");
        Assert.Single(snapshot.Devices);
        var device = snapshot.Devices[0];

        Assert.Equal(FixtureSnapshotProvider.T0 - TimeSpan.FromSeconds(120), device.StateSince);
        Assert.Equal("192.168.1.144:39421", device.Detail.Address);
    }

    [Fact]
    public void MultiDeviceHasSixDevicesPreservingOrderAndProperties() {
        var snapshot = FixtureSnapshotProvider.GetSnapshot("multi-device");
        Assert.Equal(6, snapshot.Devices.Length);
        Assert.Equal("6 devices", snapshot.DeviceCountLabel);

        Assert.Equal("d1", snapshot.Devices[0].Id);
        Assert.Equal("Galaxy S24", snapshot.Devices[0].DisplayName);
        Assert.Equal(TransportKind.WiFi, snapshot.Devices[0].Transport);

        Assert.Equal("d2", snapshot.Devices[1].Id);
        Assert.Equal("Pixel 8", snapshot.Devices[1].DisplayName);
        Assert.Equal(TransportKind.Usb, snapshot.Devices[1].Transport);
        Assert.Null(snapshot.Devices[1].Detail.Address);

        Assert.Equal("d3", snapshot.Devices[2].Id);
        Assert.Equal("Pixel 5", snapshot.Devices[2].DisplayName);
        Assert.Equal(DeviceStateKind.Unauthorized, snapshot.Devices[2].State);
        Assert.Null(snapshot.Devices[2].BatteryPercent);

        Assert.Equal("d4", snapshot.Devices[3].Id);
        Assert.Equal("Galaxy Tab S10", snapshot.Devices[3].DisplayName);
        Assert.Equal(DeviceStateKind.Recovery, snapshot.Devices[3].State);

        Assert.Equal("d5", snapshot.Devices[4].Id);
        Assert.Equal("OnePlus 13", snapshot.Devices[4].DisplayName);
        Assert.Equal(DeviceStateKind.Offline, snapshot.Devices[4].State);
        Assert.Equal("192.168.1.189:41235", snapshot.Devices[4].Detail.Address);

        Assert.Equal("d6", snapshot.Devices[5].Id);
        Assert.Equal("Nothing Phone", snapshot.Devices[5].DisplayName);
        Assert.Equal(TransportKind.Usb, snapshot.Devices[5].Transport);
        Assert.Equal(74, snapshot.Devices[5].BatteryPercent);
    }

    [Fact]
    public void PairingFixturesPreserveExactPhasesAndCopy() {
        var offerSnap = FixtureSnapshotProvider.GetSnapshot("pairing-offer");
        Assert.Single(offerSnap.PairingOffers);
        Assert.Equal("offer-1", offerSnap.PairingOffers[0].Id);
        Assert.Equal("Galaxy S24", offerSnap.PairingOffers[0].DisplayName);
        Assert.Equal("Wireless pairing available", offerSnap.PairingOffers[0].Heading);
        Assert.Equal("Waiting for a pairing code", offerSnap.PairingOffers[0].Detail);
        Assert.Equal("Pair", offerSnap.PairingOffers[0].ActionLabel);
        Assert.Null(offerSnap.Pairing);

        var codeSnap = FixtureSnapshotProvider.GetSnapshot("pairing-code");
        Assert.NotNull(codeSnap.Pairing);
        Assert.Equal(PairingPhase.AwaitingCode, codeSnap.Pairing!.Phase);
        Assert.Equal("Pairing with Galaxy S24", codeSnap.Pairing.Title);
        Assert.Equal("Enter the six-digit code shown on the phone.", codeSnap.Pairing.Detail);
        Assert.True(codeSnap.Pairing.CanSubmitCode);
        Assert.True(codeSnap.Pairing.CanCancel);
        Assert.False(codeSnap.Pairing.CanRetry);

        var progressSnap = FixtureSnapshotProvider.GetSnapshot("pairing-progress");
        Assert.NotNull(progressSnap.Pairing);
        Assert.Equal(PairingPhase.Pairing, progressSnap.Pairing!.Phase);
        Assert.Equal("Pairing with Galaxy S24…", progressSnap.Pairing.Title);
        Assert.Equal("Keep the phone nearby while pairing completes.", progressSnap.Pairing.Detail);
        Assert.True(progressSnap.Pairing.CanCancel);

        var successSnap = FixtureSnapshotProvider.GetSnapshot("pairing-success");
        Assert.NotNull(successSnap.Pairing);
        Assert.Equal(PairingPhase.PairedWaitingForDevice, successSnap.Pairing!.Phase);
        Assert.Equal("Paired with Galaxy S24", successSnap.Pairing.Title);
        Assert.Equal("Waiting for ADB connection…", successSnap.Pairing.Detail);
        Assert.Empty(successSnap.Devices);

        var failureSnap = FixtureSnapshotProvider.GetSnapshot("pairing-failure");
        Assert.NotNull(failureSnap.Pairing);
        Assert.Equal(PairingPhase.Failed, failureSnap.Pairing!.Phase);
        Assert.Equal("Pairing failed", failureSnap.Pairing.Title);
        Assert.Equal("Check the code and try again.", failureSnap.Pairing.Detail);
        Assert.True(failureSnap.Pairing.CanRetry);
        Assert.True(failureSnap.Pairing.CanCancel);
    }

    [Fact]
    public void ExportCanonicalSnapshotsJsonMatchesCommittedFile() {
        var targetFile = Repo.RootFile("ui", "src", "contract", "__fixtures__", "canonicalSnapshots.json");
        var snapshots = FixtureSnapshotProvider.GetAllSnapshots(isPinned: false, revision: 1);

        var rootNode = new JsonObject();
        foreach (var (name, snapshot) in snapshots) {
            var json = BridgeSerializer.SerializeSnapshot(snapshot);
            rootNode[name] = JsonNode.Parse(json);
        }

        if (!File.Exists(targetFile)) {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            var generatedJson = rootNode.ToJsonString(jsonOptions) + "\n";
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.WriteAllText(targetFile, generatedJson);
        }

        Assert.True(File.Exists(targetFile));
        var content = File.ReadAllText(targetFile);
        var parsedCommitted = JsonNode.Parse(content);
        Assert.NotNull(parsedCommitted);
        Assert.True(JsonNode.DeepEquals(rootNode, parsedCommitted), "Committed canonicalSnapshots.json does not match generated snapshot data.");
    }
}
