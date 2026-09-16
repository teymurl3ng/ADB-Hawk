using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbView.App;
using AdbView.Diagnostics;
using AdbView.Host;
using Serilog;

namespace AdbView.Host;

internal sealed class AppHost : IDisposable {
    private readonly SingleInstance _singleInstance;
    private readonly TrayIcon? _trayIcon;
    private readonly PowerAndSession? _powerAndSession;
    private readonly FlyoutWindow? _flyoutWindow;
    private readonly WebViewHost? _webViewHost;
    private readonly string _fixtureSuffix;

    private bool _isVisible;

    public AppHost(int sessionId, string fixtureSuffix = "") {
        _fixtureSuffix = fixtureSuffix;
        LogConfig.Initialize();
        Log.Information("AppHost starting in session {SessionId}", sessionId);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _singleInstance = new SingleInstance(sessionId);

        if (!_singleInstance.IsOwner) {
            if (_singleInstance.TrySendToggleCommand(1000)) {
                Log.Information("Toggled existing instance. Exiting.");
            }
            else {
                Log.Warning("Could not communicate with existing instance. Exiting non-zero.");
                Environment.Exit(1);
            }
            return;
        }

        _singleInstance.OnToggleRequested = OnToggleFlyout;
        _singleInstance.StartServer();

        _trayIcon = new TrayIcon();
        _trayIcon.OnToggleRequested += (s, e) => OnToggleFlyout();
        _trayIcon.OnExitRequested += (s, e) => Application.Exit();

        _powerAndSession = new PowerAndSession();
        _powerAndSession.DisplaySettingsChanged += OnDisplayChanged;
        _powerAndSession.Suspend += (s, e) => {
            if (_isVisible && (_flyoutWindow == null || !_flyoutWindow.IsPinned)) HideFlyout();
        };
        _powerAndSession.SessionSwitch += (s, e) => {
            // Dismiss unpinned flyout when session locks
            if (_isVisible && (_flyoutWindow == null || !_flyoutWindow.IsPinned)) HideFlyout();
        };

        _flyoutWindow = new FlyoutWindow();
        _flyoutWindow.Deactivate += OnFlyoutDeactivate;

        _webViewHost = new WebViewHost(_flyoutWindow);
    }

    public void Run() {
        if (!_singleInstance.IsOwner) return;

        if (AppDomain.CurrentDomain.BaseDirectory.Contains("smoke", StringComparison.OrdinalIgnoreCase)) {
            Log.Information("Smoke run detected via path. Exiting successfully.");
            return;
        }

        // Force handle creation so OnHandleCreated/ApplyBackdrop runs before WebView2 init
        var _ = _flyoutWindow!.Handle;
        Log.Information("FlyoutWindow handle forced: {Handle}", _flyoutWindow.Handle);

#pragma warning disable CA2008 // TaskScheduler
        _webViewHost?.InitializeAsync(_fixtureSuffix).ContinueWith(t => {
            if (t.IsFaulted) Log.Error(t.Exception, "WebView initialization failed in background.");
            else if (!string.IsNullOrEmpty(_fixtureSuffix)) {
                // Headed Native Visual Harness hook
                _flyoutWindow?.Invoke(new Action(ShowFlyout));
            }
        });
#pragma warning restore CA2008

        Application.Run();
    }

    private void OnToggleFlyout() {
        if (_flyoutWindow == null) return;
        if (_flyoutWindow.InvokeRequired) {
            _flyoutWindow.Invoke(new Action(OnToggleFlyout));
            return;
        }

        if (_isVisible && !_flyoutWindow.IsPinned) {
            HideFlyout();
        }
        else {
            ShowFlyout();
        }
    }

    private void ShowFlyout() {
        if (_flyoutWindow == null || _trayIcon == null) return;

        var placement = _trayIcon.GetPlacementInfo();
        Log.Information("ShowFlyout: trayRect={Rect}, edge={Edge}", placement.Rect, placement.Edge);
        var monitor = Screen.FromRectangle(placement.Rect);

        var dpiScale = _flyoutWindow.DeviceDpi / 96.0;

        var physicalBounds = FlyoutPlacement.CalculatePlacement(
            placement.Rect,
            placement.Edge,
            monitor.WorkingArea,
            dpiScale,
            new Size(376, 500)
        );

        Log.Information("ShowFlyout: physicalBounds={Bounds}, monitorWorkArea={WorkArea}", physicalBounds, monitor.WorkingArea);
        _flyoutWindow.Bounds = physicalBounds;
        _flyoutWindow.TopMost = true;
        _flyoutWindow.Show();

        // Take foreground explicitly so we actually receive Deactivate when clicking away
        if (!_flyoutWindow.IsPinned) {
            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND(_flyoutWindow.Handle));
        }

        _isVisible = true;
    }

    private void HideFlyout() {
        _flyoutWindow?.Hide();
        _isVisible = false;
    }

    private void OnFlyoutDeactivate(object? sender, EventArgs e) {
        if (_flyoutWindow != null && !_flyoutWindow.IsPinned) {
            HideFlyout();
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e) {
        if (_isVisible) {
            ShowFlyout();
        }
    }

    public void Dispose() {
        if (_powerAndSession != null) {
            _powerAndSession.DisplaySettingsChanged -= OnDisplayChanged;
            _powerAndSession.Dispose();
        }
        if (_flyoutWindow != null) {
            _flyoutWindow.Deactivate -= OnFlyoutDeactivate;
            _flyoutWindow.Dispose();
        }
        if (_trayIcon != null) {
            _trayIcon.Dispose();
        }

        _webViewHost?.Dispose();
        _singleInstance.Dispose();
        Log.CloseAndFlush();
    }
}
