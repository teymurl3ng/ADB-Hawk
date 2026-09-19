#pragma warning disable CA1707

namespace AdbView.Tests.App;

using AdbView.App;
using Xunit;

public sealed class CommandLineStartupTests {
    [Fact]
    public void Normal_Args_Not_Silent() {
        var o = CommandLine.Parse([]);
        Assert.False(o.StartupSilent);
        Assert.Equal("", o.FixtureSuffix);
    }

    [Fact]
    public void Startup_Flag_Is_Silent() {
        var o = CommandLine.Parse(["--startup"]);
        Assert.True(o.StartupSilent);
    }

    [Fact]
    public void Fixture_Parsed_Without_Startup() {
        var o = CommandLine.Parse(["--test-fixture=empty"]);
        Assert.False(o.StartupSilent);
        Assert.Equal("?fixture=empty", o.FixtureSuffix);
    }

    [Fact]
    public void Initial_Show_Policy_Normal_Vs_Startup_Vs_Fixture() {
        Assert.True(AdbView.Host.AppHost.ShouldRequestInitialShow(headless: false, isFixtureMode: false, startupSilent: false));
        Assert.False(AdbView.Host.AppHost.ShouldRequestInitialShow(headless: false, isFixtureMode: false, startupSilent: true));
        Assert.False(AdbView.Host.AppHost.ShouldRequestInitialShow(headless: false, isFixtureMode: true, startupSilent: false));
        Assert.False(AdbView.Host.AppHost.ShouldRequestInitialShow(headless: true, isFixtureMode: false, startupSilent: false));
    }
}
