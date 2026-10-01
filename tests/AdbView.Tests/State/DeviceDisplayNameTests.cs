#pragma warning disable CA1707
namespace AdbView.Tests.State;

using AdbView.State;
using Xunit;

public sealed class DeviceDisplayNameTests {
    [Fact]
    public void Samsung_Like_DeviceName_Wins_Over_Model_Code() {
        string name = DeviceDisplayName.Resolve(
            marketingName: null,
            deviceName: "Galaxy A06",
            model: "SM-A065F",
            brand: "samsung");
        Assert.Equal("Galaxy A06", name);
    }

    [Fact]
    public void Xiaomi_Redmi_Marketname_Wins() {
        string name = DeviceDisplayName.Resolve(
            marketingName: "Redmi Note 14 Pro 5G",
            deviceName: "Redmi Note 14 Pro 5G",
            model: "24090RA29G",
            brand: "Redmi");
        Assert.Equal("Redmi Note 14 Pro 5G", name);
    }

    [Fact]
    public void Xiaomi_Certification_Model_Uses_Marketname() {
        Assert.Equal(
            "Xiaomi 15",
            DeviceDisplayName.Resolve(marketingName: "Xiaomi 15", model: "24129PN74G"));
    }

    [Fact]
    public void Pixel_Like_DeviceName_Or_Model() {
        Assert.Equal(
            "Pixel 9 Pro",
            DeviceDisplayName.Resolve(
                marketingName: null,
                deviceName: "Pixel 9 Pro",
                model: "Pixel 9 Pro",
                brand: "google"));
    }

    [Fact]
    public void User_Renamed_DeviceName_Wins() {
        Assert.Equal(
            "Work Phone",
            DeviceDisplayName.Resolve(
                marketingName: null,
                deviceName: "Work Phone",
                model: "SM-S921B"));
    }

    [Fact]
    public void Older_Android_Model_Only() {
        Assert.Equal(
            "Nexus 5X",
            DeviceDisplayName.Resolve(
                marketingName: null,
                deviceName: null,
                model: "Nexus 5X",
                brand: "google"));
    }

    [Fact]
    public void Minimal_Model_Code_Gets_Brand_Prefix() {
        Assert.Equal(
            "Samsung SM-A065F",
            DeviceDisplayName.Resolve(
                marketingName: null,
                deviceName: null,
                model: "SM-A065F",
                brand: "samsung"));
    }

    [Fact]
    public void Does_Not_Duplicate_Brand_In_Model() {
        Assert.Equal(
            "Pixel 9 Pro",
            DeviceDisplayName.Resolve(model: "Pixel 9 Pro", brand: "google"));
        Assert.Equal(
            "Samsung Galaxy",
            DeviceDisplayName.Resolve(model: "Samsung Galaxy", brand: "samsung"));
    }

    [Fact]
    public void Marketname_Beats_DeviceName() {
        Assert.Equal(
            "Redmi Note 14",
            DeviceDisplayName.Resolve(
                marketingName: "Redmi Note 14",
                deviceName: "Teymur's Phone",
                model: "24090RA29G"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("NULL")]
    [InlineData("unknown")]
    [InlineData("Unknown")]
    public void Rejects_Empty_Null_Unknown_Tokens(string? bad) {
        Assert.Equal(
            "SM-A065F",
            DeviceDisplayName.Resolve(marketingName: bad, deviceName: bad, model: "SM-A065F"));
    }

    [Theory]
    [InlineData("adb-R8VXA02SNKH-xyz_adb-tls-connect._tcp")]
    [InlineData("adb-R8VXA02SNKH")]
    [InlineData("studio-abc")]
    [InlineData("192.168.1.20:42137")]
    [InlineData("10.0.0.2:5555")]
    public void Rejects_Transport_Like_Candidates(string transportLike) {
        Assert.Equal(
            "SM-A065F",
            DeviceDisplayName.Resolve(
                marketingName: transportLike,
                deviceName: transportLike,
                model: "SM-A065F"));
        Assert.True(DeviceDisplayName.IsTransportLike(transportLike));
    }

    [Fact]
    public void Unicode_Product_Names_Preserved() {
        Assert.Equal(
            "红米 Note 14",
            DeviceDisplayName.Resolve(marketingName: "红米 Note 14\n"));
    }

    [Fact]
    public void Safe_Generic_Fallback() {
        Assert.Equal(
            Vocabulary.Pairing.DefaultDeviceName,
            DeviceDisplayName.Resolve());
    }

    [Fact]
    public void Early_Path_Never_Uses_Serial() {
        Assert.Equal(
            Vocabulary.Pairing.DefaultDeviceName,
            DeviceDisplayName.ResolveEarly(null, null, null));
        Assert.Equal(
            "SM_A065F",
            DeviceDisplayName.ResolveEarly(null, "SM_A065F", "a06xx"));
    }

    [Fact]
    public void Brand_Title_Case_Cosmetic_Only() {
        Assert.Equal("Samsung", DeviceDisplayName.TitleCaseBrand("samsung"));
        Assert.Equal("Google", DeviceDisplayName.TitleCaseBrand("google"));
        Assert.Equal("Xiaomi", DeviceDisplayName.TitleCaseBrand("Xiaomi"));
        Assert.Equal("Redmi", DeviceDisplayName.TitleCaseBrand("Redmi"));
    }

    [Fact]
    public void Usb_And_Wifi_Same_Metadata_Same_DisplayName() {
        string ResolvePair(string? market, string? device, string? model, string? brand) =>
            DeviceDisplayName.Resolve(market, device, model, brand);

        string usb = ResolvePair(null, "Galaxy A06", "SM-A065F", "samsung");
        string wifi = ResolvePair(null, "Galaxy A06", "SM-A065F", "samsung");
        Assert.Equal(usb, wifi);
        Assert.Equal("Galaxy A06", usb);
    }
}
