#pragma warning disable CA1707

namespace AdbView.Tests.State;

using System.Collections.Immutable;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Xunit;

public sealed class PairingDisplayNameTests {
    private static RawMdnsService Svc(
        string instance,
        string? given = null,
        string? model = null,
        string? serial = null) => new() {
            InstanceName = instance,
            ServiceType = MdnsServiceTypes.Pairing,
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = "192.168.1.10",
            Port = 37123,
            GivenName = given,
            ProductModel = model,
            Serial = serial,
            Format = RawMdnsFormat.ProtoBinary,
        };

    private static DeviceView Device(string displayName, string? serial) {
        var since = DateTimeOffset.Parse("2026-09-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return new DeviceView {
            Id = "dev-" + displayName,
            DisplayName = displayName,
            State = DeviceStateKind.Connected,
            Severity = SeverityKind.Informational,
            StateLabel = "Connected",
            StatusLine = "Connected · USB",
            Guidance = null,
            Transport = TransportKind.Usb,
            TransportLabel = "USB",
            BatteryPercent = null,
            MetaTemplate = "Connected {age} ago",
            StateSince = since,
            ConnectedSince = since,
            Detail = new DeviceDetailView {
                Serial = serial,
                ExactModel = null,
                AndroidVersion = null,
                Address = null,
                CopyFields = ImmutableArray<CopyFieldView>.Empty,
            },
        };
    }

    [Fact]
    public void A_GivenName_Wins() {
        Assert.Equal(
            "Galaxy S24",
            PairingDisplayName.Resolve(Svc("adb-x-y", given: "Galaxy S24", model: "SM-S921B"), ImmutableArray<DeviceView>.Empty));
    }

    [Fact]
    public void B_Structured_Serial_Match_Uses_Tracked_DisplayName() {
        var devices = ImmutableArray.Create(Device("SM_A065F", "RFCX116CYYX"));
        Assert.Equal(
            "SM_A065F",
            PairingDisplayName.Resolve(
                Svc("adb-RFCX116CYYX-JYueKC", serial: "RFCX116CYYX", model: "Other"),
                devices));
    }

    [Fact]
    public void C_Model_When_No_Match() {
        Assert.Equal(
            "SM-A065F",
            PairingDisplayName.Resolve(Svc("adb-x-y", model: "SM-A065F"), ImmutableArray<DeviceView>.Empty));
    }

    [Fact]
    public void D_Raw_Adb_Instance_Never_Shown() {
        Assert.Equal(
            Vocabulary.Pairing.DefaultDeviceName,
            PairingDisplayName.Resolve(Svc("adb-RFCX116CYYX-JYueKC"), ImmutableArray<DeviceView>.Empty));
    }

    [Fact]
    public void E_Studio_Instance_Never_Shown() {
        Assert.Equal(
            Vocabulary.Pairing.DefaultDeviceName,
            PairingDisplayName.Resolve(Svc("studio-ABC-DEF"), ImmutableArray<DeviceView>.Empty));
        Assert.True(PairingDisplayName.IsProtocolInstanceLabel("studio-ABC-DEF"));
    }

    [Fact]
    public void H_Offer_Id_Still_Uses_DiscoveryKey() {
        var (offers, bindings) = PairingOfferMapper.Map(
            ImmutableArray.Create(Svc("adb-RFCX116CYYX-JYueKC")));
        Assert.Single(offers);
        Assert.Equal(Vocabulary.Pairing.DefaultDeviceName, offers[0].DisplayName);
        Assert.Contains("adb-RFCX116CYYX-JYueKC", offers[0].Id, StringComparison.Ordinal);
        Assert.True(bindings.ContainsKey(offers[0].Id));
    }

    [Fact]
    public void No_False_Merge_On_Model_Alone() {
        var devices = ImmutableArray.Create(Device("USB Phone", "OTHERSERIAL"));
        Assert.Equal(
            "SM-A065F",
            PairingDisplayName.Resolve(Svc("adb-x-y", model: "SM-A065F"), devices));
    }

    [Fact]
    public void F_G_Notification_And_Result_Use_Friendly_Mapper() {
        var (offers, _) = PairingOfferMapper.Map(
            ImmutableArray.Create(Svc("adb-RFCX116CYYX-JYueKC")));
        Assert.Equal(Vocabulary.Pairing.DefaultDeviceName, offers[0].DisplayName);
        string body = Vocabulary.Notifications.PairingAvailableBody(offers[0].DisplayName);
        Assert.DoesNotContain("adb-", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Vocabulary.Pairing.DefaultDeviceName, body, StringComparison.Ordinal);
        string success = Vocabulary.Notifications.PairingSuccessTitle(offers[0].DisplayName);
        Assert.DoesNotContain("adb-", success, StringComparison.OrdinalIgnoreCase);
    }
}
