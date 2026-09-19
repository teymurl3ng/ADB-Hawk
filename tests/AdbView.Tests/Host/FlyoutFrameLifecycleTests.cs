#pragma warning disable CA1707
#pragma warning disable CA2007

namespace AdbView.Tests.Host;

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbView.Host;
using AdbView.State;
using Xunit;

/// <summary>Defect 1: custom-frame NC collapse must hold across show/resize lifecycle.</summary>
[Collection("AppHostSerial")]
public sealed class FlyoutFrameLifecycleTests {
    [Fact]
    public async Task Create_Show_Resize_Pin_Hide_Show_ClientTop_Has_No_Caption_Inset() {
        await Task.Yield();
        using var flyout = new FlyoutWindow();
        // Force handle + backdrop + RecalcNonClientFrame (OnHandleCreated).
        Assert.NotEqual(IntPtr.Zero, flyout.Handle);

        flyout.Bounds = new Rectangle(100, 100, 376, 400);
        flyout.Show();
        Application.DoEvents();
        AssertClientTopCollapsed(flyout, "after first show");

        flyout.Size = new Size(376, 520);
        Application.DoEvents();
        flyout.RecalcNonClientFrame();
        AssertClientTopCollapsed(flyout, "after taller resize");

        flyout.Size = new Size(376, 360);
        Application.DoEvents();
        flyout.RecalcNonClientFrame();
        AssertClientTopCollapsed(flyout, "after shorter resize");

        flyout.IsPinned = true;
        Application.DoEvents();
        flyout.RecalcNonClientFrame();
        AssertClientTopCollapsed(flyout, "after pin");

        flyout.IsPinned = false;
        flyout.Hide();
        Application.DoEvents();
        flyout.Show();
        Application.DoEvents();
        flyout.RecalcNonClientFrame();
        AssertClientTopCollapsed(flyout, "after hide/show");
    }

    [Fact]
    public void Light_Theme_Opaque_Fallback_Is_Light() {
        using var flyout = new FlyoutWindow();
        _ = flyout.Handle;
        flyout.ApplyShellAppearance(ThemeKind.Light, transparencyEnabled: false);
        Assert.Equal(ThemeKind.Light, flyout.AppliedTheme);
        Assert.False(flyout.BackdropActive);
        Assert.True(flyout.BackColor.R > 200, $"expected light BackColor, got {flyout.BackColor}");
    }

    [Fact]
    public void Dark_Theme_Opaque_Fallback_Is_Dark() {
        using var flyout = new FlyoutWindow();
        _ = flyout.Handle;
        flyout.ApplyShellAppearance(ThemeKind.Dark, transparencyEnabled: false);
        Assert.Equal(ThemeKind.Dark, flyout.AppliedTheme);
        Assert.True(flyout.BackColor.R < 80, $"expected dark BackColor, got {flyout.BackColor}");
    }

    [Fact]
    public void Dark_To_Light_Updates_Applied_Theme() {
        using var flyout = new FlyoutWindow();
        _ = flyout.Handle;
        flyout.ApplyShellAppearance(ThemeKind.Dark, transparencyEnabled: false);
        Assert.Equal(ThemeKind.Dark, flyout.AppliedTheme);
        flyout.ApplyShellAppearance(ThemeKind.Light, transparencyEnabled: false);
        Assert.Equal(ThemeKind.Light, flyout.AppliedTheme);
        Assert.True(flyout.BackColor.R > 200);
    }

    [Fact]
    public void Light_To_Dark_Updates_Applied_Theme() {
        using var flyout = new FlyoutWindow();
        _ = flyout.Handle;
        flyout.ApplyShellAppearance(ThemeKind.Light, transparencyEnabled: false);
        flyout.ApplyShellAppearance(ThemeKind.Dark, transparencyEnabled: false);
        Assert.Equal(ThemeKind.Dark, flyout.AppliedTheme);
        Assert.True(flyout.BackColor.R < 80);
    }

    private static void AssertClientTopCollapsed(FlyoutWindow flyout, string step) {
        int inset = flyout.MeasureClientTopInsetPx();
        Assert.True(
            inset >= 0 && inset < FlyoutWindow.CaptionInsetRejectPx,
            $"{step}: client-top inset {inset}px looks like a native caption (reject ≥ {FlyoutWindow.CaptionInsetRejectPx})");
    }
}
