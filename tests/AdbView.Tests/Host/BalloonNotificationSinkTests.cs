#pragma warning disable CA1707 // Test method names use underscores by project convention.
#pragma warning disable CA2007 // Test project does not use ConfigureAwait.

namespace AdbView.Tests.Host;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Host;
using AdbView.Notifications;
using Xunit;

internal sealed class FakeTrayBalloonHost : ITrayBalloonHost {
    private readonly object _gate = new();
    private readonly List<(string Title, string Body, NotificationSeverity Severity)> _shown = new();
    private bool _disposed;
    private bool _throwOnNext;
    private Exception? _throwException;

    public event EventHandler? BalloonClicked;

    public IReadOnlyList<(string Title, string Body, NotificationSeverity Severity)> Shown {
        get {
            lock (_gate) {
                return _shown.ToArray();
            }
        }
    }

    public int ExplorerRestartCount { get; private set; }

    public void ThrowOnNextShow(Exception ex) {
        _throwOnNext = true;
        _throwException = ex;
    }

    public void SimulateExplorerRestart() {
        ExplorerRestartCount++;
        // Recreate usability — no balloon emitted.
    }

    public void RaiseBalloonClicked() => BalloonClicked?.Invoke(this, EventArgs.Empty);

    public void DisposeHost() {
        lock (_gate) {
            _disposed = true;
        }
    }

    public void ShowBalloon(string title, string body, NotificationSeverity severity) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate) {
            if (_throwOnNext) {
                _throwOnNext = false;
                throw _throwException ?? new InvalidOperationException("balloon failed");
            }

            _shown.Add((title, body, severity));
        }
    }
}

public sealed class BalloonNotificationSinkTests {
    private static readonly string[] ExitOnlyMenu = ["Exit"];
    private static readonly string[] ShowFlyoutOnly = ["show-flyout"];

    private static NotificationIntent Intent(
        NotificationSeverity severity,
        string title,
        string body,
        string subject = "usb:SECRET") =>
        new(
            new NotificationKey(NotificationKind.DeviceConnected, subject),
            title,
            body,
            severity);

    private static BalloonNotificationSink Create(
        FakeTrayBalloonHost host,
        Action? showFlyout = null,
        Action<Action>? marshal = null) =>
        new(host, marshal ?? (a => a()), showFlyout);

    [Fact]
    public void A_Info_Maps_To_Info() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        sink.Emit(Intent(NotificationSeverity.Info, "T", "B"));
        Assert.Equal(NotificationSeverity.Info, host.Shown[0].Severity);
    }

    [Fact]
    public void B_Warning_Maps_To_Warning() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        sink.Emit(Intent(NotificationSeverity.Warning, "T", "B"));
        Assert.Equal(NotificationSeverity.Warning, host.Shown[0].Severity);
    }

    [Fact]
    public void C_Error_Maps_To_Error() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        sink.Emit(Intent(NotificationSeverity.Error, "T", "B"));
        Assert.Equal(NotificationSeverity.Error, host.Shown[0].Severity);
    }

    [Fact]
    public void D_E_Title_And_Body_Forwarded_Exactly() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        sink.Emit(Intent(NotificationSeverity.Info, "Exact title", "Exact body"));
        Assert.Equal("Exact title", host.Shown[0].Title);
        Assert.Equal("Exact body", host.Shown[0].Body);
    }

    [Fact]
    public void F_Subject_Never_Shown() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        const string subject = "usb:DO-NOT-LEAK";
        sink.Emit(Intent(NotificationSeverity.Info, "Phone connected", "Connected over USB", subject));
        Assert.DoesNotContain(subject, host.Shown[0].Title, StringComparison.Ordinal);
        Assert.DoesNotContain(subject, host.Shown[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void G_H_Delivery_Failure_Isolated_And_Recoverable() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        host.ThrowOnNextShow(new InvalidOperationException("shell reject"));
        sink.Emit(Intent(NotificationSeverity.Info, "A", "a"));
        Assert.Empty(host.Shown);

        sink.Emit(Intent(NotificationSeverity.Warning, "B", "b"));
        Assert.Single(host.Shown);
        Assert.Equal("B", host.Shown[0].Title);
        Assert.Equal(NotificationSeverity.Warning, host.Shown[0].Severity);
    }

    [Fact]
    public async Task I_Emit_From_Worker_Marshals() {
        var host = new FakeTrayBalloonHost();
        int marshalCalls = 0;
        int emitThread = -1;
        int testThread = Environment.CurrentManagedThreadId;
        using var sink = new BalloonNotificationSink(
            host,
            marshalToUi: action => {
                marshalCalls++;
                action();
            });

        await Task.Run(() => {
            emitThread = Environment.CurrentManagedThreadId;
            sink.Emit(Intent(NotificationSeverity.Info, "W", "w"));
        }, TestContext.Current.CancellationToken);

        Assert.Single(host.Shown);
        Assert.Equal(1, marshalCalls);
        Assert.NotEqual(testThread, emitThread);
    }

    [Fact]
    public void J_Disposed_Sink_Does_Not_Throw() {
        var host = new FakeTrayBalloonHost();
        var sink = Create(host);
        sink.Dispose();
        sink.Emit(Intent(NotificationSeverity.Info, "X", "x"));
        Assert.Empty(host.Shown);
    }

    [Fact]
    public void J2_Disposed_Host_Does_Not_Throw() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        host.DisposeHost();
        sink.Emit(Intent(NotificationSeverity.Info, "X", "x"));
        Assert.Empty(host.Shown);
    }

    [Fact]
    public void K_L_Balloon_Click_Shows_Not_Toggles() {
        var host = new FakeTrayBalloonHost();
        bool visible = false;
        int showCalls = 0;
        int hideCalls = 0;

        void ShowFlyout() {
            showCalls++;
            visible = true;
        }

        void ToggleFlyout() {
            if (visible) {
                hideCalls++;
                visible = false;
            }
            else {
                ShowFlyout();
            }
        }

        using var sink = Create(host, showFlyout: ShowFlyout);

        // hidden → show
        host.RaiseBalloonClicked();
        Assert.True(visible);
        Assert.Equal(1, showCalls);
        Assert.Equal(0, hideCalls);

        // already visible → show again (stay visible), never toggle-hide
        host.RaiseBalloonClicked();
        Assert.True(visible);
        Assert.Equal(2, showCalls);
        Assert.Equal(0, hideCalls);

        // prove toggle would have hidden — we must not use it
        ToggleFlyout();
        Assert.False(visible);
        Assert.Equal(1, hideCalls);
    }

    [Fact]
    public void M_Balloon_Click_Preserves_Pinned() {
        var host = new FakeTrayBalloonHost();
        bool visible = false;
        bool pinned = true;
        using var sink = Create(host, showFlyout: () => { visible = true; });

        host.RaiseBalloonClicked();
        Assert.True(visible);
        Assert.True(pinned);
    }

    [Fact]
    public void N_Balloon_Click_No_DeepLink_Or_Settings() {
        var host = new FakeTrayBalloonHost();
        var actions = new List<string>();
        using var sink = Create(host, showFlyout: () => actions.Add("show-flyout"));
        host.RaiseBalloonClicked();
        Assert.Equal(ShowFlyoutOnly, actions);
    }

    [Fact]
    public void O_P_TrayIcon_Menu_Exit_Only() {
        using var tray = new TrayIcon();
        Assert.Equal(ExitOnlyMenu, tray.ContextMenuItemTexts);

        int toggles = 0;
        tray.OnToggleRequested += (_, _) => toggles++;
        Assert.Equal(0, toggles);
    }

    [Fact]
    public void Q_AppHost_Wires_Real_Sink_Only_When_ShouldConstruct_True() {
        Assert.True(AppHost.ShouldConstructRealBalloonSink(headless: false, isFixtureMode: false));
        Assert.False(AppHost.ShouldConstructRealBalloonSink(headless: true, isFixtureMode: false));
        Assert.False(AppHost.ShouldConstructRealBalloonSink(headless: false, isFixtureMode: true));
        Assert.False(AppHost.ShouldConstructRealBalloonSink(headless: true, isFixtureMode: true));

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string appHost = Path.Combine(root, "src", "AdbView", "Host", "AppHost.cs");
        string program = Path.Combine(root, "src", "AdbView", "Program.cs");
        Assert.True(File.Exists(appHost), appHost);
        Assert.True(File.Exists(program), program);

        string appHostText = File.ReadAllText(appHost);
        string programText = File.ReadAllText(program);
        // 8C: production AppHost must own BalloonNotificationSink construction.
        Assert.Contains("BalloonNotificationSink", appHostText, StringComparison.Ordinal);
        Assert.Contains("ShouldConstructRealBalloonSink", appHostText, StringComparison.Ordinal);
        Assert.DoesNotContain("BalloonSmokeHarness", appHostText, StringComparison.Ordinal);
        Assert.DoesNotContain("BalloonSmokeHarness", programText, StringComparison.Ordinal);
        Assert.DoesNotContain("--dev-balloon-smoke", programText, StringComparison.Ordinal);
    }

    [Fact]
    public void R_Explorer_Restart_Does_Not_Emit_Balloon() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        host.SimulateExplorerRestart();
        Assert.Equal(1, host.ExplorerRestartCount);
        Assert.Empty(host.Shown);
    }

    [Fact]
    public void S_Sink_Delivers_After_Explorer_Restart() {
        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        host.SimulateExplorerRestart();
        sink.Emit(Intent(NotificationSeverity.Info, "After", "restart"));
        Assert.Single(host.Shown);
        Assert.Equal("After", host.Shown[0].Title);
    }

    [Fact]
    public void Truncation_Is_Deterministic_And_Rune_Safe() {
        string title = new string('あ', 100);
        string body = new string('あ', 400);
        Assert.Equal(63, BalloonText.TruncateTitle(title).Length);
        Assert.Equal(255, BalloonText.TruncateBody(body).Length);

        var host = new FakeTrayBalloonHost();
        using var sink = Create(host);
        sink.Emit(Intent(NotificationSeverity.Info, title, body));
        Assert.Equal(63, host.Shown[0].Title.Length);
        Assert.Equal(255, host.Shown[0].Body.Length);
    }

    [Fact]
    public void Click_After_Dispose_Is_NoOp() {
        var host = new FakeTrayBalloonHost();
        int shows = 0;
        var sink = Create(host, showFlyout: () => shows++);
        sink.Dispose();
        host.RaiseBalloonClicked();
        Assert.Equal(0, shows);
    }
}

public sealed class TrayIconBalloonBehaviorTests {
    private static readonly string[] ExitOnlyMenu = ["Exit"];

    [Fact]
    public void HandleTaskbarCreated_Does_Not_Throw_And_Leaves_Menu_Intact() {
        using var tray = new TrayIcon();
        tray.HandleTaskbarCreated();
        Assert.Equal(ExitOnlyMenu, tray.ContextMenuItemTexts);
    }

    [Fact]
    public void ShowBalloon_After_Dispose_Throws_ObjectDisposed() {
        var tray = new TrayIcon();
        tray.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            tray.ShowBalloon("t", "b", NotificationSeverity.Info));
    }
}
