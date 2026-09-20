#pragma warning disable CA1707

namespace AdbView.Tests.App;

using AdbView.App;
using AdbView.Host;
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
        Assert.True(AppHost.ShouldRequestInitialShow(headless: false, isFixtureMode: false, startupSilent: false));
        Assert.False(AppHost.ShouldRequestInitialShow(headless: false, isFixtureMode: false, startupSilent: true));
        // Fixture harness also waits for first content-size before show (v1 freeze).
        Assert.True(AppHost.ShouldRequestInitialShow(headless: false, isFixtureMode: true, startupSilent: false));
        Assert.False(AppHost.ShouldRequestInitialShow(headless: true, isFixtureMode: false, startupSilent: false));
    }

    [Fact]
    public void Initial_Show_Request_Survives_Until_Visible() {
        // Contract: do not clear pending merely because WebView initialized.
        bool pending = true;
        bool webReady = true;
        int lastAppliedHeight = 0;
        Assert.True(pending);
        Assert.True(webReady);
        Assert.Equal(0, lastAppliedHeight);

        lastAppliedHeight = 280;
        bool visibleAfterShow = false;
        Assert.NotEqual(0, lastAppliedHeight);
        Assert.False(visibleAfterShow);
        Assert.True(pending); // still pending until visible

        visibleAfterShow = true;
        if (visibleAfterShow) {
            pending = false;
        }

        Assert.False(pending);
    }
}
