using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbView.App;
using AdbView.Bridge;
using AdbView.Diagnostics;
using AdbView.Host;
using AdbView.State;
using Serilog;

namespace AdbView.Host;

internal sealed class AppHost : IBridgeHost, IDisposable {
    private readonly SingleInstance? _singleInstance;
    private readonly TrayIcon? _trayIcon;
    private readonly PowerAndSession? _powerAndSession;
    private readonly FlyoutWindow? _flyoutWindow;
    private readonly WebViewHost? _webViewHost;
    private readonly BridgeCoordinator? _coordinator;
    private readonly StateEngine? _stateEngine;
    private readonly Adb.AdbServerSupervisor? _supervisor;
    private readonly DeviceTrackerLifecycle? _deviceTrackerLifecycle;
    private readonly MdnsTrackerLifecycle? _mdnsTrackerLifecycle;
    private readonly PairingSession? _pairingSession;
    private readonly System.Threading.CancellationTokenSource _appLifetimeCts = new();
    private Task? _supervisorTask;
    private readonly string _fixtureSuffix;

    private bool _isVisible;
    private int _currentEffectiveHeight = 500;
    private int _lastAppliedHeight;
    public BridgeCoordinator? Coordinator => _coordinator;
    internal StateEngine? StateEngine => _stateEngine;
    internal Adb.AdbServerSupervisor? Supervisor => _supervisor;
    internal DeviceTrackerLifecycle? DeviceTrackerLifecycle => _deviceTrackerLifecycle;
    internal MdnsTrackerLifecycle? MdnsTrackerLifecycle => _mdnsTrackerLifecycle;
    internal PairingSession? PairingSession => _pairingSession;

    public AppHost(int sessionId, string fixtureSuffix = "")
        : this(sessionId, fixtureSuffix, supervisorFactory: null, bridgeHost: null, headless: false) { }

    /// <param name="headless">
    /// When true, skips WinForms/tray/webview/single-instance. Composition unit tests
    /// must use this: creating Controls installs WinFormsSynchronizationContext and
    /// async awaits deadlock without Application.Run.
    /// </param>
    internal AppHost(
        int sessionId,
        string fixtureSuffix,
        Func<Adb.AdbServerSupervisor>? supervisorFactory,
        IBridgeHost? bridgeHost = null,
        bool headless = false,
        Func<Adb.DeviceTracker>? deviceTrackerFactory = null,
        Func<Adb.MdnsTracker>? mdnsTrackerFactory = null,
        Adb.PairingCoordinator? pairingCoordinator = null) {
        _fixtureSuffix = fixtureSuffix;
        LogConfig.Initialize();
        Log.Information("AppHost starting in session {SessionId} (headless={Headless})", sessionId, headless);

        if (!headless) {
            Application.EnableVisualStyles();
            try {
                Application.SetCompatibleTextRenderingDefault(false);
            }
            catch (InvalidOperationException) {
                // Already set or first window already created in process (e.g. test runner).
            }

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
                if (_isVisible && (_flyoutWindow == null || !_flyoutWindow.IsPinned)) HideFlyout();
            };

            _flyoutWindow = new FlyoutWindow();
            _flyoutWindow.Deactivate += OnFlyoutDeactivate;
        }
        else {
            _singleInstance = null;
        }

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

        bool isFixtureMode = !string.IsNullOrEmpty(fixtureSuffix);

        var initialSnapshot = !string.IsNullOrEmpty(initialFixture)
            ? FixtureSnapshotProvider.GetSnapshot(initialFixture, isPinned: false, revision: 1)
            : StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);

        _stateEngine = new StateEngine(initialSnapshot);
        _stateEngine.SnapshotPublished += OnSnapshotPublished;

        IBridgeHost coordinatorHost = bridgeHost ?? (IBridgeHost)this;
        if (headless && bridgeHost == null) {
            throw new ArgumentException("headless AppHost requires an injected IBridgeHost.", nameof(bridgeHost));
        }

        if (!isFixtureMode) {
            _supervisor = supervisorFactory != null
                ? supervisorFactory()
                : new Adb.AdbServerSupervisor(
                    connectionFactory: () => new Adb.Protocol.AdbHostConnection(),
                    locator: new Adb.AdbExecutableLocator(),
                    processRunner: new Adb.AdbProcessRunner(),
                    backoff: new Adb.Backoff());

            _deviceTrackerLifecycle = new DeviceTrackerLifecycle(
                post: evt => _stateEngine.Post(evt),
                trackerFactory: deviceTrackerFactory);

            _mdnsTrackerLifecycle = new MdnsTrackerLifecycle(
                post: evt => _stateEngine.Post(evt),
                trackerFactory: mdnsTrackerFactory);

            _pairingSession = new PairingSession(
                post: evt => _stateEngine.Post(evt),
                resolveOffer: offerId => {
                    var state = _stateEngine.CurrentDeviceState;
                    return state.OfferBindings.TryGetValue(offerId, out var binding) ? binding : null;
                },
                coordinator: pairingCoordinator);

            _supervisor.StateChanged += OnSupervisorStateChanged;
        }

        _coordinator = new BridgeCoordinator(
            coordinatorHost,
            isPinned: false,
            initialSnapshot: initialSnapshot,
            onSetPinned: pinned => _stateEngine.Post(new SetPinnedEvent(pinned)),
            onPairStart: offerId => _pairingSession?.Start(offerId),
            onPairSubmit: (offerId, code) => _pairingSession?.Submit(offerId, code),
            onPairCancel: offerId => _pairingSession?.Cancel(offerId));

        if (!headless) {
            _webViewHost = new WebViewHost(_flyoutWindow!, _coordinator);
            _trayIcon?.Apply(initialSnapshot.Health.Tray, initialSnapshot.Health.TrayTooltip);
        }
    }

    private void OnSupervisorStateChanged(Adb.Model.AdbServerStateChanged state) {
        _stateEngine?.Post(new AdbServerStateChangedEvent(state));
        _deviceTrackerLifecycle?.OnServerStateChanged(state);
        _mdnsTrackerLifecycle?.OnServerStateChanged(state);
    }

    private void OnSnapshotPublished(AppSnapshot snapshot) {
        _coordinator?.UpdateSnapshot(snapshot);
        if (_trayIcon is not null) {
            if (TryMarshalToFlyoutUi(new Action<AppSnapshot>(ApplyTrayFromSnapshot), snapshot)) {
                return;
            }

            ApplyTrayFromSnapshot(snapshot);
        }
    }

    private void ApplyTrayFromSnapshot(AppSnapshot snapshot) =>
        _trayIcon?.Apply(snapshot.Health.Tray, snapshot.Health.TrayTooltip);

    public void Run() {
        if (_singleInstance is not { IsOwner: true }) return;

        if (AppDomain.CurrentDomain.BaseDirectory.Contains("smoke", StringComparison.OrdinalIgnoreCase)) {
            Log.Information("Smoke run detected via path. Exiting successfully.");
            return;
        }

        if (_supervisor != null) {
            _supervisorTask = Task.Run(() => _supervisor.RunAsync(_appLifetimeCts.Token));
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
        if (TryMarshalToFlyoutUi(new Action(OnToggleFlyout))) {
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

    /// <summary>
    /// Marshals to the flyout UI thread. Synchronous Invoke without a message loop
    /// deadlocks (CompositionTests construct AppHost without Application.Run; the
    /// supervisor can also publish before Run starts). BeginInvoke when a handle
    /// exists so work runs once the loop starts; otherwise skip.
    /// </summary>
    private bool TryMarshalToFlyoutUi(Delegate method, params object[] args) {
        if (_flyoutWindow == null || !_flyoutWindow.InvokeRequired) {
            return false;
        }

        if (Application.MessageLoop) {
            _flyoutWindow.Invoke(method, args);
            return true;
        }

        if (_flyoutWindow.IsHandleCreated) {
            _ = _flyoutWindow.BeginInvoke(method, args);
            return true;
        }

        Log.Debug("AppHost UI marshal skipped: no message loop and no handle");
        return true;
    }

    public void SendSnapshot(SnapshotEnvelope envelope) {
        if (TryMarshalToFlyoutUi(new Action<SnapshotEnvelope>(SendSnapshot), envelope)) {
            return;
        }

        var json = BridgeSerializer.SerializeSnapshotEnvelope(envelope);
        _webViewHost?.PostWebMessageAsJson(json);
    }

    public void SetPinned(bool isPinned) {
        if (TryMarshalToFlyoutUi(new Action<bool>(SetPinned), isPinned)) {
            return;
        }

        if (_flyoutWindow != null) {
            _flyoutWindow.IsPinned = isPinned;
            Log.Information("AppHost.SetPinned: FlyoutWindow.IsPinned={IsPinned}, TopMost={TopMost}",
                _flyoutWindow.IsPinned, _flyoutWindow.TopMost);
        }
    }

    public void HideFlyout() {
        if (TryMarshalToFlyoutUi(new Action(HideFlyout))) {
            return;
        }

        _flyoutWindow?.Hide();
        _isVisible = false;
    }

    public void ResizeFlyout(int clampedEffectiveHeight) {
        if (TryMarshalToFlyoutUi(new Action<int>(ResizeFlyout), clampedEffectiveHeight)) {
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

    private int _disposed;

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        _appLifetimeCts.Cancel();
        try {
            if (_supervisorTask != null && !_supervisorTask.IsCompleted) {
                _supervisorTask.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException) {
            // Clean shutdown
        }

        _pairingSession?.Dispose();
        _mdnsTrackerLifecycle?.Dispose();
        _deviceTrackerLifecycle?.Dispose();
        _stateEngine?.Dispose();
        _appLifetimeCts.Dispose();

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
        _singleInstance?.Dispose();
        Log.CloseAndFlush();
    }
}
