#pragma warning disable CA1707
#pragma warning disable CA2007

namespace AdbView.Tests.Integration.Chaos;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbView.Bridge;
using AdbView.Host;
using AdbView.State;
using Microsoft.Web.WebView2.Core;
using Xunit;

/// <summary>ENGINEERING §9.7 / §11 rows 16–17 recovery policy (no live Chromium crash).</summary>
[Collection("AppHostSerial")]
public sealed class ChaosWebViewRecoveryTests {
    private sealed class NullBridgeHost : IBridgeHost {
        public void SendSnapshot(SnapshotEnvelope envelope) { }
        public void SetPinned(bool isPinned) { }
        public void HideFlyout() { }
        public void ResizeFlyout(int clampedEffectiveHeight) { }
    }

    [Fact]
    public async Task Row17_Recreate_Backoff_Sequence_Then_TrayOnly() {
        var delays = new List<TimeSpan>();
        var clockStart = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);
        var now = clockStart;

        using var form = new Form();
        var coordinator = new BridgeCoordinator(
            new NullBridgeHost(),
            isPinned: false,
            initialSnapshot: StateEngine.CreateDefaultSnapshot());

        using var host = new WebViewHost(
            form,
            coordinator,
            clock: () => now,
            delay: ts => {
                delays.Add(ts);
                return Task.CompletedTask;
            });
        host.SuppressRecreateForTests = true;

        await host.SimulateProcessFailedForTestsAsync(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        now = now.AddSeconds(2);
        await host.SimulateProcessFailedForTestsAsync(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        now = now.AddSeconds(2);
        await host.SimulateProcessFailedForTestsAsync(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        now = now.AddSeconds(2);
        await host.SimulateProcessFailedForTestsAsync(CoreWebView2ProcessFailedKind.BrowserProcessExited);

        Assert.True(host.IsTrayOnly);
        Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(15), delays[2]);
        Assert.Equal(3, delays.Count); // fourth blocked by tray-only
    }

    [Fact]
    public async Task Row16_RenderProcess_Failure_Uses_Recovery_Path() {
        var delays = new List<TimeSpan>();
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);

        using var form = new Form();
        var coordinator = new BridgeCoordinator(
            new NullBridgeHost(),
            isPinned: false,
            initialSnapshot: StateEngine.CreateDefaultSnapshot());

        using var host = new WebViewHost(
            form,
            coordinator,
            clock: () => now,
            delay: ts => {
                delays.Add(ts);
                return Task.CompletedTask;
            });
        host.SuppressRecreateForTests = true;

        // Without a live CoreWebView2, renderer failure falls through to recreate (still no backend restart).
        await host.SimulateProcessFailedForTestsAsync(CoreWebView2ProcessFailedKind.RenderProcessExited);
        Assert.False(host.IsTrayOnly);
        Assert.Contains(TimeSpan.FromSeconds(1), delays);
    }

    [Fact]
    public void Recreate_Backoff_Table_Matches_Engineering() {
        Assert.Equal(
            new[] {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(60),
            },
            WebViewHost.RecreateBackoff);
        Assert.Equal(3, WebViewHost.MaxRecoveriesInWindow);
        Assert.Equal(TimeSpan.FromMinutes(5), WebViewHost.RecoveryWindow);
    }
}
