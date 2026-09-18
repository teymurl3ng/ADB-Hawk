using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbView.App;
using AdbView.Bridge;
using AdbView.Diagnostics;
using AdbView.Host;
using Serilog;

namespace AdbView.Host;

internal sealed class AppHost : IBridgeHost, IDisposable {
    private readonly SingleInstance _singleInstance;
    private readonly TrayIcon? _trayIcon;
    private readonly PowerAndSession? _powerAndSession;
    private readonly FlyoutWindow? _flyoutWindow;
    private readonly WebViewHost? _webViewHost;
    private readonly BridgeCoordinator? _coordinator;
    private readonly string _fixtureSuffix;

    private bool _isVisible;
    private int _currentEffectiveHeight = 500;
    private int _lastAppliedHeight;
    public BridgeCoordinator? Coordinator => _coordinator;

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

        string? initialFixture = null;
        if (!string.IsNullOrEmpty(fixtureSuffix)) {
            const string prefix = "?fixture=";
            var idx = fixtureSuffix.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) {
                var rawName = fixtureSuffix[(idx + prefix.Length)..];
                var amp = rawName.IndexOf('&', StringComparison.Ordinal);
                initialFixture = amp >= 0 ? rawName[..amp] : rawName;
            }
        }

        var initialSnapshot = !string.IsNullOrEmpty(initialFixture)
            ? FixtureSnapshotProvider.GetSnapshot(initialFixture, isPinned: false, revision: 1)
            : FixtureSnapshotProvider.GetSnapshot("empty", isPinned: false, revision: 1);

        _coordinator = new BridgeCoordinator(this, isPinned: false, initialSnapshot);
        _webViewHost = new WebViewHost(_flyoutWindow, _coordinator);
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

        var monitor = (placement.Rect.Width > 0 && placement.Rect.Height > 0)
            ? Screen.FromRectangle(placement.Rect)
            : TrayIcon.GetTaskbarScreen();

        var dpiScale = _flyoutWindow.DeviceDpi / 96.0;

        var anchor = FlyoutPlacement.ResolveAnchor(
            placement.Rect,
            monitor.WorkingArea,
            monitor.Bounds,
            placement.Edge,
            dpiScale
        );

        var physicalBounds = FlyoutPlacement.CalculatePlacement(
            anchor,
            placement.Edge,
            monitor.WorkingArea,
            dpiScale,
            new Size(376, _currentEffectiveHeight)
        );

        Log.Information("ShowFlyout: physicalBounds={Bounds}, anchor={Anchor}, monitorWorkArea={WorkArea}", physicalBounds, anchor, monitor.WorkingArea);
        _flyoutWindow.Bounds = physicalBounds;
        _flyoutWindow.TopMost = _flyoutWindow.IsPinned;
        _flyoutWindow.Show();

        // Take foreground explicitly so we actually receive Deactivate when clicking away
        if (!_flyoutWindow.IsPinned) {
            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND(_flyoutWindow.Handle));
        }

        _isVisible = true;
    }

    public void SendSnapshot(SnapshotEnvelope envelope) {
        if (_flyoutWindow != null && _flyoutWindow.InvokeRequired) {
            _flyoutWindow.Invoke(new Action<SnapshotEnvelope>(SendSnapshot), envelope);
            return;
        }

        var json = BridgeSerializer.SerializeSnapshotEnvelope(envelope);
        _webViewHost?.PostWebMessageAsJson(json);
    }

    public void SetPinned(bool isPinned) {
        if (_flyoutWindow != null && _flyoutWindow.InvokeRequired) {
            _flyoutWindow.Invoke(new Action<bool>(SetPinned), isPinned);
            return;
        }

        if (_flyoutWindow != null) {
            _flyoutWindow.IsPinned = isPinned;
            Log.Information("AppHost.SetPinned: FlyoutWindow.IsPinned={IsPinned}, TopMost={TopMost}",
                _flyoutWindow.IsPinned, _flyoutWindow.TopMost);
        }
    }

    public void HideFlyout() {
        if (_flyoutWindow != null && _flyoutWindow.InvokeRequired) {
            _flyoutWindow.Invoke(new Action(HideFlyout));
            return;
        }

        _flyoutWindow?.Hide();
        _isVisible = false;
    }

    public void ResizeFlyout(int clampedEffectiveHeight) {
        if (_flyoutWindow != null && _flyoutWindow.InvokeRequired) {
            _flyoutWindow.Invoke(new Action<int>(ResizeFlyout), clampedEffectiveHeight);
            return;
        }

        if (clampedEffectiveHeight == _currentEffectiveHeight && _lastAppliedHeight != 0) {
            Log.Information("AppHost.ResizeFlyout suppressed duplicate: {Height}", clampedEffectiveHeight);
            return;
        }

        _currentEffectiveHeight = clampedEffectiveHeight;
        _lastAppliedHeight = clampedEffectiveHeight;

        Log.Information("AppHost.ResizeFlyout: applied {Height}", clampedEffectiveHeight);

        if (_isVisible && _flyoutWindow != null) {
            UpdateFlyoutBounds();
        }
    }

    private void UpdateFlyoutBounds() {
        if (_flyoutWindow == null || _trayIcon == null) return;

        var dpiScale = _flyoutWindow.DeviceDpi / 96.0;

        if (_flyoutWindow.IsPinned) {
            int physicalWidth = (int)Math.Round(376 * dpiScale);
            int physicalHeight = (int)Math.Round(_currentEffectiveHeight * dpiScale);
            var currentBounds = _flyoutWindow.Bounds;
            if (currentBounds.Width != physicalWidth || currentBounds.Height != physicalHeight) {
                _flyoutWindow.Size = new Size(physicalWidth, physicalHeight);
                Log.Information("UpdateFlyoutBounds (pinned): new size {Size}", _flyoutWindow.Size);
            }
        }
        else {
            var placement = _trayIcon.GetPlacementInfo();
            var monitor = (placement.Rect.Width > 0 && placement.Rect.Height > 0)
                ? Screen.FromRectangle(placement.Rect)
                : TrayIcon.GetTaskbarScreen();

            var anchor = FlyoutPlacement.ResolveAnchor(
                placement.Rect,
                monitor.WorkingArea,
                monitor.Bounds,
                placement.Edge,
                dpiScale
            );

            var physicalBounds = FlyoutPlacement.CalculatePlacement(
                anchor,
                placement.Edge,
                monitor.WorkingArea,
                dpiScale,
                new Size(376, _currentEffectiveHeight)
            );

            if (_flyoutWindow.Bounds != physicalBounds) {
                _flyoutWindow.Bounds = physicalBounds;
                Log.Information("UpdateFlyoutBounds: physicalBounds={Bounds}", physicalBounds);
            }
        }
    }

    private void OnFlyoutDeactivate(object? sender, EventArgs e) {
        Log.Information("OnFlyoutDeactivate: isPinned={IsPinned}", _flyoutWindow?.IsPinned);
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
