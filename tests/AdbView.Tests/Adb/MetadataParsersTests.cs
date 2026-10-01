#pragma warning disable CA1707
namespace AdbView.Tests.Adb;

using System;
using System.Text;
using AdbView.Adb;
using Xunit;

public sealed class MetadataParsersTests {
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(67)]
    public void Battery_Accepts_Valid_Levels(int level) {
        string dump = $"Current Battery Service state:\n  AC powered: false\n  level: {level}\n  scale: 100\n";
        Assert.Equal(level, MetadataParsers.TryParseBatteryPercent(Encoding.UTF8.GetBytes(dump)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(1000)]
    public void Battery_Rejects_OutOfRange(int level) {
        string dump = $"  level: {level}\n";
        Assert.Null(MetadataParsers.TryParseBatteryPercent(Encoding.UTF8.GetBytes(dump)));
    }

    [Fact]
    public void Battery_Rejects_NonNumeric() {
        Assert.Null(MetadataParsers.TryParseBatteryPercent(Encoding.UTF8.GetBytes("  level: abc\n")));
    }

    [Fact]
    public void Battery_Rejects_Missing_Level() {
        Assert.Null(MetadataParsers.TryParseBatteryPercent(Encoding.UTF8.GetBytes("Current Battery Service state:\n  scale: 100\n")));
    }

    [Fact]
    public void Battery_Rejects_Empty() {
        Assert.Null(MetadataParsers.TryParseBatteryPercent(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Battery_Ignores_Capacity_Level_And_Oem_Log_Fragments() {
        // Samsung A06-shaped: Capacity level + ACTION log lines with level:N, must not win.
        const string dump = """
            Current Battery Service state:
              AC powered: false
              USB powered: true
              level: 97
              scale: 100
              Capacity level: 4
            09-19 01:45:31.959  Sending ACTION_BATTERY_CHANGED: level:100, status:5, health:2
            """;
        Assert.Equal(97, MetadataParsers.TryParseBatteryPercent(Encoding.UTF8.GetBytes(dump)));
    }

    [Fact]
    public void ExactModel_Valid_And_Empty() {
        Assert.Equal("SM-A065F", MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("SM-A065F\n")));
        Assert.Null(MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("\n")));
        Assert.Null(MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("   \n")));
        Assert.Null(MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("null\n")));
        Assert.Null(MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("unknown\n")));
    }

    [Fact]
    public void AndroidVersion_Preserves_Preview_And_Rejects_Empty() {
        Assert.Equal("16", MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("16\n")));
        Assert.Equal("Baklava", MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("Baklava\n")));
        Assert.Equal("15 DP1", MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("15 DP1\n")));
        Assert.Null(MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("\r\n")));
    }

    [Fact]
    public void AndroidVersion_Is_Release_String_Not_Sdk_Mapping() {
        // ExactModel/AndroidVersion parsers accept only raw getprop bytes — no SDK int parameter exists.
        Assert.Equal("16", MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("16\n")));
        Assert.NotEqual("16", MetadataParsers.TryParseGetpropValue(Encoding.UTF8.GetBytes("36\n")));
    }
}
