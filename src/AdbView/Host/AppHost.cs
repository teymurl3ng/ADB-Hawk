using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbView.App;
using AdbView.Bridge;
using AdbView.Diagnostics;
using AdbView.Notifications;
using AdbView.State;
using Microsoft.Win32;
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
    private readonly MdnsFallbackLifecycle? _mdnsFallbackLifecycle;
    private readonly PairingSession? _pairingSession;
    private readonly QrPairingSession? _qrPairingSession;
    private readonly PostPairConnectCoordinator? _postPairConnect;
    private FlyoutRevealAnimator? _presenter;
    private bool _animationsEnabled = true;
    private bool _lastAnimationsEnabledLogged;
    private readonly DeviceMetadataLifecycle? _metadataLifecycle;
    private readonly NotificationCoordinator? _notificationCoordinator;
    private readonly INotificationSink? _notificationSink;
    private readonly BalloonNotificationSink? _balloonSink;
    private readonly UserPreferences? _userPreferences;
    private readonly INetworkDiagnosticsSource? _networkDiagnosticsSource;
    private readonly RepairCoordinator? _repairCoordinator;
    private int _discoveryCheckRunning;
    private long _lastServerGeneration;
    private bool _lastSeenDiscoveryCard;
    private readonly System.Threading.CancellationTokenSource _appLifetimeCts = new();
    private Task? _supervisorTask;
    private readonly string _fixtureSuffix;
    private readonly bool _headless;
    private readonly bool _isFixtureMode;
    private readonly bool _startupSilent;
    private bool _pendingInitialShow;
    private bool _initialShowCompleted;
    private bool _webViewReadyForShow;
    private ThemeKind _lastAppliedTheme = ThemeKind.Dark;
    private bool _lastAppliedTransparency = true;

    private bool _isVisible;
    private int _currentEffectiveHeight = 500;
    private int _lastAppliedHeight;
    private int _lastReportedContentHeight;
    private int _hiddenContentSizeReportsForInitialShow;
    private bool _suppressLightDismiss;
    private bool _lightDismissArmed;
    private int _showGeneration;
    private long _contentSizeSeq;
    public BridgeCoordinator? Coordinator => _coordinator;
    internal StateEngine? StateEngine => _stateEngine;
    internal Adb.AdbServerSupervisor? Supervisor => _supervisor;
    internal DeviceTrackerLifecycle? DeviceTrackerLifecycle => _deviceTrackerLifecycle;
    internal MdnsTrackerLifecycle? MdnsTrackerLifecycle => _mdnsTrackerLifecycle;
    internal PairingSession? PairingSession => _pairingSession;
    internal DeviceMetadataLifecycle? MetadataLifecycle => _metadataLifecycle;
    internal NotificationCoordinator? NotificationCoordinator => _notificationCoordinator;
    internal INotificationSink? NotificationSink => _notificationSink;
    internal bool UsesRealBalloonNotificationSink { get; }
    internal bool PendingInitialShow => _pendingInitialShow;
    internal bool InitialShowCompleted => _initialShowCompleted;

    /// <summary>
    /// Real <see cref="BalloonNotificationSink"/> only for headed, non-fixture production.
    /// Fixture and headless always use <see cref="NullNotificationSink"/> (or a test inject).
    /// </summary>
    internal static bool ShouldConstructRealBalloonSink(bool headless, bool isFixtureMode) =>
        !headless && !isFixtureMode;

    /// <summary>Normal interactive / fixture first launch requests one deferred flyout show.</summary>
    internal static bool ShouldRequestInitialShow(bool headless, bool isFixtureMode, bool startupSilent) =>
        !headless && !startupSilent;


    public AppHost(int sessionId, string fixtureSuffix = "", bool headless = false, bool startupSilent = false)
        : this(
            sessionId,
            fixtureSuffix,
            supervisorFactory: null,
            bridgeHost: null,
            headless: headless,
            startupSilent: startupSilent) { }

    /// <param name="headless">
    /// When true, skips WinForms/tray/webview/single-instance. Composition unit tests
    /// must use this: creating Controls installs WinFormsSynchronizationContext and
    /// async awaits deadlock without Application.Run.
    /// </param>
    /// <param name="startupSilent">
    /// When true (--startup), stay tray-only; do not auto-show flyout on first launch.
    /// </param>
    internal AppHost(
        int sessionId,
        string fixtureSuffix,
        Func<Adb.AdbServerSupervisor>? supervisorFactory,
        IBridgeHost? bridgeHost = null,
        bool headless = false,
        Func<Adb.DeviceTracker>? deviceTrackerFactory = null,
        Func<Adb.MdnsTracker>? mdnsTrackerFactory = null,
        Adb.PairingCoordinator? pairingCoordinator = null,
        Adb.IMetadataProbe? metadataProbe = null,
        INotificationSink? notificationSink = null,
        Func<DateTimeOffset>? notificationClock = null,
        Func<TimeSpan, CancellationToken, Task>? notificationDelay = null,
        bool startupSilent = false,
        INetworkDiagnosticsSource? networkDiagnosticsSource = null,
        IElevatedProcessLauncher? processLauncher = null,
        IServiceStateSource? serviceStateSource = null) {
        _fixtureSuffix = fixtureSuffix;
        _headless = headless;
        _isFixtureMode = !string.IsNullOrEmpty(fixtureSuffix);
        _startupSilent = startupSilent;
        // Decision 0008: real diagnostics only for headed production; fixtures/tests stay hermetic.
        _networkDiagnosticsSource = networkDiagnosticsSource
            ?? (!headless && !_isFixtureMode ? new WindowsNetworkDiagnosticsSource() : null);
        // Normal interactive first launch: request one deferred show once UI is presentable.
        _pendingInitialShow = ShouldRequestInitialShow(headless, _isFixtureMode, startupSilent);
        LogConfig.Initialize();
        Log.Information(
            "AppHost starting in session {SessionId} (headless={Headless} fixture={Fixture} startupSilent={StartupSilent} pendingShow={PendingShow})",
            sessionId,
            headless,
            _isFixtureMode,
            _startupSilent,
            _pendingInitialShow);

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
                UsesRealBalloonNotificationSink = false;
                _notificationSink = NullNotificationSink.Instance;
                _notificationCoordinator = null;
                _balloonSink = null;
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

            _userPreferences = new UserPreferences();
            _trayIcon = new TrayIcon(_userPreferences, new StartupRegistration());
            _trayIcon.OnToggleRequested += (s, e) => OnToggleFlyout();
            _trayIcon.OnOpenRequested += (s, e) => ShowFlyoutOnly();
            _trayIcon.OnExitRequested += (s, e) => Application.Exit();
            _trayIcon.PreferencesChanged += OnTrayPreferencesChanged;

            _powerAndSession = new PowerAndSession();
            _powerAndSession.DisplaySettingsChanged += OnDisplayChanged;
            _powerAndSession.Suspend += OnPowerSuspend;
            _powerAndSession.Resume += OnPowerResume;
            _powerAndSession.SessionSwitch += OnSessionSwitch;

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

        var initialSnapshot = !string.IsNullOrEmpty(initialFixture)
            ? FixtureSnapshotProvider.GetSnapshot(initialFixture, isPinned: false, revision: 1)
            : StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);

        _stateEngine = new StateEngine(initialSnapshot);
        _stateEngine.SnapshotPublished += OnSnapshotPublished;

        if (!_headless && !_isFixtureMode) {
            PostCurrentShellAppearance();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        IBridgeHost coordinatorHost = bridgeHost ?? (IBridgeHost)this;
        if (headless && bridgeHost == null) {
            throw new ArgumentException("headless AppHost requires an injected IBridgeHost.", nameof(bridgeHost));
        }

        if (!_isFixtureMode) {
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

            // Decision 0008 follow-through: first-party fallback browser (headed production only;
            // interface candidates come from the diagnostics source). Tests/fixtures stay hermetic.
            if (_networkDiagnosticsSource is not null) {
                _mdnsFallbackLifecycle = new MdnsFallbackLifecycle(
                    post: evt => _stateEngine.Post(evt),
                    interfaces: () => _networkDiagnosticsSource.GetMulticastCandidateAddresses());
            }

            _pairingSession = new PairingSession(
                post: evt => _stateEngine.Post(evt),
                resolveOffer: offerId => {
                    var state = _stateEngine.CurrentDeviceState;
                    return state.OfferBindings.TryGetValue(offerId, out var binding) ? binding : null;
                },
                coordinator: pairingCoordinator);

            // Decision 0010: QR pairing session hands off into the same pairing flow.
            _qrPairingSession = new QrPairingSession(
                post: evt => _stateEngine.Post(evt),
                startOffer: offerId => _pairingSession.Start(offerId),
                submitCode: (offerId, code) => _pairingSession.Submit(offerId, code));

            // Decision 0013: graceful presentation (mode-B growth + open/close slides).
            _presenter = new FlyoutRevealAnimator(
                new FormsTimerTickSource(),
                bounds => {
                    if (_flyoutWindow is { IsDisposed: false } window) {
                        window.Bounds = bounds;
                    }
                });

            // Decision 0009: one active connect toward the paired device while waiting.
            _postPairConnect = new PostPairConnectCoordinator(new Adb.HostConnectClient());

            _metadataLifecycle = new DeviceMetadataLifecycle(
                postMetadata: (gen, evt) => _stateEngine.PostMetadata(gen, evt),
                probe: metadataProbe);

            _stateEngine.MetadataTargetsChanged += targets => _metadataLifecycle.OnTargetsChanged(targets);

            _supervisor.StateChanged += OnSupervisorStateChanged;
        }

        _coordinator = new BridgeCoordinator(
            coordinatorHost,
            isPinned: false,
            initialSnapshot: initialSnapshot,
            onSetPinned: pinned => _stateEngine.Post(new SetPinnedEvent(pinned)),
            onPairStart: offerId => _pairingSession?.Start(offerId),
            onPairSubmit: (offerId, code) => _pairingSession?.Submit(offerId, code),
            onPairCancel: offerId => _pairingSession?.Cancel(offerId),
            onDiscoveryRecheck: () => _ = RunDiscoveryCheckAsync(),
            onDiscoveryRepair: repairId => _ = RunDiscoveryRepairAsync(repairId),
            onPairQrStart: () => _qrPairingSession?.Start(),
            onPairQrCancel: () => _qrPairingSession?.Cancel());

        if (_networkDiagnosticsSource is not null && _stateEngine is not null) {
            // Decision 0008 amendment: repair actions only exist where diagnostics exist.
            _repairCoordinator = new RepairCoordinator(
                processRunner: new Adb.AdbProcessRunner(),
                executableLocator: new Adb.AdbExecutableLocator(),
                diagnosticsSource: _networkDiagnosticsSource,
                launcher: processLauncher ?? new WindowsElevatedProcessLauncher(),
                serviceState: serviceStateSource ?? new ScServiceStateSource(),
                post: evt => _stateEngine.Post(evt),
                runDiscoveryCheck: RunDiscoveryCheckAsync);
        }

        if (!headless) {
            _webViewHost = new WebViewHost(_flyoutWindow!, _coordinator);
            _trayIcon?.Apply(initialSnapshot.Health.Tray, initialSnapshot.Health.TrayTooltip);
        }

        // Phase 8C: notifications from accepted StateEngine snapshots only.
        UsesRealBalloonNotificationSink =
            notificationSink is null && ShouldConstructRealBalloonSink(headless, _isFixtureMode);

        if (notificationSink is not null) {
            _notificationSink = notificationSink;
            _balloonSink = null;
        }
        else if (UsesRealBalloonNotificationSink) {
            _balloonSink = new BalloonNotificationSink(
                _trayIcon!,
                MarshalNotificationToUi,
                ShowFlyoutOnly);
            _notificationSink = _balloonSink;
        }
        else {
            _notificationSink = NullNotificationSink.Instance;
            _balloonSink = null;
        }

        Func<DateTimeOffset> clock = notificationClock ?? (() => App.SystemClock.UtcNow);
        // Explicit lambda: method-group Task.Delay can bind ambiguously across TFMs.
        Func<TimeSpan, CancellationToken, Task> delay = notificationDelay
            ?? ((TimeSpan d, CancellationToken ct) => Task.Delay(d, ct));
        _notificationCoordinator = new NotificationCoordinator(clock, delay, _notificationSink);
        if (_userPreferences is not null) {
            _notificationCoordinator.SetNotificationsEnabled(_userPreferences.NotificationsEnabled);
        }

        // Silent baseline: first authoritative snapshot establishes NotifierState with zero balloons.
        _notificationCoordinator.AcceptSnapshot(initialSnapshot);

        Log.Information(
            "AppHost notifications: RealBalloonSink={RealBalloon} Fixture={Fixture} Headless={Headless}",
            UsesRealBalloonNotificationSink,
            _isFixtureMode,
            _headless);
    }

    private void MarshalNotificationToUi(Action work) {
        if (_flyoutWindow is null) {
            work();
            return;
        }

        if (!_flyoutWindow.InvokeRequired) {
            work();
            return;
        }

        if (Application.MessageLoop) {
            _flyoutWindow.Invoke(work);
            return;
        }

        if (_flyoutWindow.IsHandleCreated) {
            _ = _flyoutWindow.BeginInvoke(work);
            return;
        }

        // No message loop yet (tests / pre-Run): deliver synchronously so sinks stay testable.
        work();
    }

    /// <summary>
    /// Balloon click: show-only. Never toggles a visible flyout closed; never deep-links.
    /// </summary>
    private void ShowFlyoutOnly() {
        if (_flyoutWindow is null || _trayIcon is null) {
            return;
        }

        if (TryMarshalToFlyoutUi(new Action(ShowFlyoutOnly))) {
            return;
        }

        // Already visible (pinned or not) → remain; ShowFlyout repositions but does not hide.
        ShowFlyout();
    }

    private void OnPowerSuspend(object? sender, EventArgs e) {
        _notificationCoordinator?.OnSuspend();
        _metadataLifecycle?.OnSuspend();
        if (_isVisible && (_flyoutWindow == null || !_flyoutWindow.IsPinned)) {
            HideFlyout();
        }
    }

    private void OnPowerResume(object? sender, EventArgs e) {
        if (_stateEngine is not null) {
            _notificationCoordinator?.RebaselineForResume(_stateEngine.CurrentSnapshot);
        }

        _metadataLifecycle?.OnResume();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) {
        if (e.Reason == SessionSwitchReason.SessionUnlock && _stateEngine is not null) {
            _notificationCoordinator?.RebaselineForResume(_stateEngine.CurrentSnapshot);
        }

        if (_isVisible && (_flyoutWindow == null || !_flyoutWindow.IsPinned)) {
            HideFlyout();
        }
    }

    private void OnSupervisorStateChanged(Adb.Model.AdbServerStateChanged state) {
        _lastServerGeneration = Math.Max(_lastServerGeneration, state.Generation);
        _stateEngine?.Post(new AdbServerStateChangedEvent(state));
        _deviceTrackerLifecycle?.OnServerStateChanged(state);
        _mdnsTrackerLifecycle?.OnServerStateChanged(state);
        _mdnsFallbackLifecycle?.OnServerStateChanged(state);
        if (state.Phase == Adb.Model.AdbServerPhase.Up) {
            _repairCoordinator?.OnServerUp(state.Generation);
        }
    }

    private async Task RunDiscoveryRepairAsync(string repairId) {
        var coordinator = _repairCoordinator;
        if (coordinator is null) {
            return;
        }

        await coordinator
            .RunAsync(repairId, Interlocked.Read(ref _lastServerGeneration), _appLifetimeCts.Token)
            .ConfigureAwait(false);
    }

    private void OnSnapshotPublished(AppSnapshot snapshot) {
        // Offer withdrawal clears the snapshot card in the reducer; release the
        // coordinator too so a later pairStart for any offer is accepted (§6.8).
        _pairingSession?.CancelIfOfferWithdrawn();
        if (_repairCoordinator is not null && _stateEngine is not null) {
            _repairCoordinator.OnSnapshotPublished(snapshot, _stateEngine.CurrentDeviceState);
            if (_lastSeenDiscoveryCard && snapshot.DiscoveryHealth is null) {
                _lastSeenDiscoveryCard = false;
            }
            else if (!_lastSeenDiscoveryCard && snapshot.DiscoveryHealth is not null) {
                // Card just appeared (or reappeared): refresh the evidence-gated offer set.
                _lastSeenDiscoveryCard = true;
                _ = _repairCoordinator.RebuildCatalogAsync(_appLifetimeCts.Token);
            }
        }

        if (_stateEngine is not null) {
            _postPairConnect?.OnSnapshotPublished(snapshot, _stateEngine.CurrentDeviceState);
            _qrPairingSession?.OnSnapshotPublished(snapshot, _stateEngine.CurrentDeviceState);
        }
        ApplyNativeShellFromSnapshot(snapshot);
        _notificationCoordinator?.AcceptSnapshot(snapshot);

        _coordinator?.UpdateSnapshot(snapshot);
        if (_metadataLifecycle is not null) {
            bool pinned = _flyoutWindow?.IsPinned ?? snapshot.Shell.Pinned;
            _metadataLifecycle.SetPresentationState(_isVisible, pinned);
        }

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

        // v1: intentional opaque surface only (decision 0004 — Acrylic deferred).
        bool transparency = ShellMaterialPolicy.ResolveTransparencyEnabled();
        ThemeKind theme = ResolveEffectiveTheme();
        _lastAppliedTheme = theme;
        _lastAppliedTransparency = transparency;
        _flyoutWindow!.ApplyShellAppearance(theme, transparency);

        // Force handle creation so OnHandleCreated/ApplyBackdrop runs before WebView2 init
        var _ = _flyoutWindow.Handle;
        Log.Information("FlyoutWindow handle forced: {Handle}", _flyoutWindow.Handle);

        // Prime width to 376 epx before WebView navigates. Default WinForms size is ~300px;
        // measuring at that width inflates wrapped content height and the first show sticks
        // (decision 0005 freezes HWND after show).
        PrimeHiddenLayoutViewport();

#pragma warning disable CA2008 // TaskScheduler
        _webViewHost?.InitializeAsync(_fixtureSuffix).ContinueWith(t => {
            if (t.IsFaulted) Log.Error(t.Exception, "WebView initialization failed in background.");
            else {
                _webViewReadyForShow = true;
                _flyoutWindow?.Invoke(new Action(() => {
                    _webViewHost?.ApplyOpaqueBackground(_lastAppliedTheme);
                }));
                if (!string.IsNullOrEmpty(_fixtureSuffix)) {
                    // Fixture harness: wait for first content-size (same as production) so
                    // presentation height matches the frozen visible-session invariant.
                    _flyoutWindow?.Invoke(new Action(TryCompletePendingInitialShow));
                }
                else {
                    _flyoutWindow?.Invoke(new Action(TryCompletePendingInitialShow));
                }
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

        // Presentation height = latest measured natural content (clamped by caller / prior Apply).
        if (_lastReportedContentHeight > 0) {
            _currentEffectiveHeight = _lastReportedContentHeight;
            _lastAppliedHeight = _lastReportedContentHeight;
        }

        if (_webViewHost is { IsTrayOnly: true }) {
            _ = _webViewHost.TryRecoverFromTrayOnlyAsync();
        }

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

        // Programmatic show must not be eaten by activation churn / failed SetForegroundWindow.
        _suppressLightDismiss = true;
        _lightDismissArmed = false;
        int showGen = ++_showGeneration;

        // Decision 0013: cancel any close animation (its terminal Hide would eat this show),
        // then present. Animated open: slide out of the tray edge over 167 ms.
        _closeAnimating = false;
        _presenter?.Cancel();
        bool reducedMotion = !_animationsEnabled;
        if (!reducedMotion) {
            _flyoutWindow.Bounds = SlideTarget(physicalBounds, placement.Edge, monitor.WorkingArea, intoTray: true);
        }
        else {
            _flyoutWindow.Bounds = physicalBounds;
        }

        _flyoutWindow.TopMost = _flyoutWindow.IsPinned;
        _flyoutWindow.Show();
        _flyoutWindow.RecalcNonClientFrame();

        // Take foreground explicitly so we actually receive Deactivate when clicking away
        if (!_flyoutWindow.IsPinned) {
            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND(_flyoutWindow.Handle));
        }

        _isVisible = _flyoutWindow.Visible;

        if (reducedMotion) {
            _suppressLightDismiss = false;
        }

        if (!reducedMotion) {
            _presenter?.AnimateBounds(
                _flyoutWindow.Bounds,
                physicalBounds,
                FlyoutRevealAnimator.OpenDuration,
                completed: () => {
                    if (showGen != _showGeneration) {
                        return; // a hide superseded this open mid-flight
                    }

                    _suppressLightDismiss = false;
                    ArmLightDismiss(showGen);
                    Log.Information("ShowFlyout: open reveal completed.");
                });
        }
        else {
            // Arm light-dismiss only after this show's message batch — not via Thread.Sleep.
            if (_flyoutWindow.IsHandleCreated) {
                _ = _flyoutWindow.BeginInvoke(new Action(() => {
                    if (showGen != _showGeneration) {
                        return;
                    }

                    _lightDismissArmed = true;
                    Log.Information("ShowFlyout: light-dismiss armed (visible={Visible})", _flyoutWindow.Visible);
                }));
            }
            else {
                _lightDismissArmed = true;
            }
        }

        NotifyMetadataPresentation();
        MaybeStartDiscoveryCheck();

        // Decision 0012 cleanup: a manual show satisfies the pending initial-show request —
        // without this, a tray toggle before the first report leaves the flag dangling and the
        // window auto re-opens later at a different height (audit RACE 5).
        if (FlyoutInteractionPolicy.ShouldClearPendingInitialShowOnManualShow(_pendingInitialShow, _initialShowCompleted)) {
            _pendingInitialShow = false;
            _initialShowCompleted = true;
            Log.Information("AppHost: pending initial show satisfied by manual show.");
        }
    }

    /// <summary>Decision 0008: measure when the flyout opens while the health card is visible.</summary>
    private void MaybeStartDiscoveryCheck() {
        if (_networkDiagnosticsSource is null || _stateEngine is null) {
            return;
        }

        if (_stateEngine.CurrentSnapshot.DiscoveryHealth is null) {
            return;
        }

        _ = RunDiscoveryCheckAsync();
    }

    /// <summary>Arms light-dismiss unless the show generation was superseded (decision 0013).</summary>
    private void ArmLightDismiss(int showGen) {
        if (_flyoutWindow is not { IsHandleCreated: true }) {
            _lightDismissArmed = true;
            return;
        }

        _ = _flyoutWindow.BeginInvoke(new Action(() => {
            if (showGen != _showGeneration) {
                return;
            }

            _lightDismissArmed = true;
            Log.Information("ShowFlyout: light-dismiss armed (visible={Visible})", _flyoutWindow.Visible);
        }));
    }

    /// <summary>
    /// Decision 0013: the off-screen slide origin/target — the final bounds pushed along the
    /// tray-normal axis. intoTray=true: fully behind the taskbar (close target). intoTray=false:
    /// one gap-width toward the tray from the final spot (open origin).
    /// </summary>
    private static Rectangle SlideTarget(Rectangle finalBounds, FlyoutPlacement.TaskbarEdge edge, Rectangle workArea, bool intoTray) {
        const int openOffset = 24; // effective px; small nudge the window slides over
        var moved = finalBounds;
        int gap = intoTray ? 0 : openOffset;
        switch (edge) {
            case FlyoutPlacement.TaskbarEdge.Bottom:
                moved.Y = intoTray ? workArea.Bottom : finalBounds.Bottom + openOffset;
                break;
            case FlyoutPlacement.TaskbarEdge.Top:
                moved.Y = intoTray ? workArea.Top - moved.Height : finalBounds.Top - openOffset;
                break;
            case FlyoutPlacement.TaskbarEdge.Left:
                moved.X = intoTray ? workArea.Left - moved.Width : finalBounds.Left - openOffset;
                break;
            case FlyoutPlacement.TaskbarEdge.Right:
                moved.X = intoTray ? workArea.Right : finalBounds.Right + openOffset;
                break;
        }

        return moved;
    }

    private async Task RunDiscoveryCheckAsync() {
        var source = _networkDiagnosticsSource;
        var engine = _stateEngine;
        if (source is null || engine is null) {
            return;
        }

        if (Interlocked.CompareExchange(ref _discoveryCheckRunning, 1, 0) != 0) {
            return;
        }

        try {
            engine.Post(new DiscoveryCheckStartedEvent());

            var evidence = DiscoveryDiagnostics.Collect(
                source,
                Environment.ProcessId,
                ResolveProcessName);
            var interfaces = source.GetMulticastCandidateAddresses();
            var probe = await Adb.MdnsReferenceProbe
                .ProbeAsync(interfaces, _appLifetimeCts.Token)
                .ConfigureAwait(false);

            var diagnosis = new DiscoveryDiagnosis(
                probe.Status switch {
                    Adb.MdnsProbeStatus.Measured => DiscoveryProbeOutcome.Measured,
                    _ => DiscoveryProbeOutcome.Unavailable,
                },
                probe.AnnouncementCount,
                [.. evidence.PortHolderNames],
                [.. evidence.VirtualAdapterNames]);
            engine.Post(new DiscoveryDiagnosisReceivedEvent(diagnosis));
        }
        catch (OperationCanceledException) {
            // App shutting down; no diagnosis to report.
        }
#pragma warning disable CA1031 // R-08: the failure is logged AND surfaced as inconclusive evidence.
        catch (Exception ex) {
            // R-08: the measurement failure is logged AND surfaced as an inconclusive
            // diagnosis — the card never silently keeps stale evidence.
            Log.Warning(ex, "Discovery diagnosis run failed; reporting inconclusive evidence.");
            engine.Post(new DiscoveryDiagnosisReceivedEvent(
                new DiscoveryDiagnosis(
                    DiscoveryProbeOutcome.Unavailable,
                    0,
                    System.Collections.Immutable.ImmutableArray<string>.Empty,
                    System.Collections.Immutable.ImmutableArray<string>.Empty)));
        }
#pragma warning restore CA1031
        finally {
            Interlocked.Exchange(ref _discoveryCheckRunning, 0);
        }
    }

    private string? ResolveProcessName(int processId) {
        try {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process?.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            // PID exited between table enumeration and lookup — expected race, logged per R-08.
            Log.Debug(ex, "Discovery diagnostics: process {Pid} exited before name lookup.", processId);
            return null;
        }
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

        NotifyMetadataPresentation();
    }

    public void HideFlyout() {
        if (TryMarshalToFlyoutUi(new Action(HideFlyout))) {
            return;
        }

        _showGeneration++; // invalidate pending light-dismiss arm
        _lightDismissArmed = false;
        // Adopt latest measured content height so the next show re-presents correctly.
        if (_lastReportedContentHeight > 0) {
            _currentEffectiveHeight = _lastReportedContentHeight;
            _lastAppliedHeight = _lastReportedContentHeight;
        }

        _isVisible = false;
        NotifyMetadataPresentation();

        // Decision 0013: graceful close — slide into the tray edge over 83 ms, then hide.
        // _showGeneration was bumped above, so any Show during the slide cancels the terminal
        // Hide (the close animation's generation check fails) and the flyout stays open.
        var window = _flyoutWindow;
        if (window is not null && window.Visible && _animationsEnabled && _trayIcon is not null) {
            var placement = _trayIcon.GetPlacementInfo();
            var monitor = (placement.Rect.Width > 0 && placement.Rect.Height > 0)
                ? Screen.FromRectangle(placement.Rect)
                : TrayIcon.GetTaskbarScreen();
            int closeGen = _showGeneration;
            _closeAnimating = true;

            _presenter?.Cancel();
            _presenter?.AnimateBounds(
                window.Bounds,
                SlideTarget(window.Bounds, placement.Edge, monitor.WorkingArea, intoTray: true),
                FlyoutRevealAnimator.CloseDuration,
                completed: () => {
                    _closeAnimating = false;
                    if (closeGen != _showGeneration) {
                        return; // a show superseded this close mid-slide
                    }

                    window.Hide();
                    Log.Information("HideFlyout: close slide completed.");
                });
        }
        else {
            _closeAnimating = false;
            _presenter?.Cancel();
            window?.Hide();
        }
    }

    private void NotifyMetadataPresentation() {
        bool pinned = _flyoutWindow?.IsPinned
            ?? _stateEngine?.CurrentSnapshot.Shell.Pinned
            ?? false;
        _metadataLifecycle?.SetPresentationState(_isVisible, pinned);
    }

    public void ResizeFlyout(
        int clampedEffectiveHeight,
        ContentSizePhase phase = ContentSizePhase.Live,
        string? layoutKey = null) {
        if (TryMarshalToFlyoutUi(
                new Action<int, ContentSizePhase, string?>(ResizeFlyout),
                clampedEffectiveHeight,
                phase,
                layoutKey ?? string.Empty)) {
            return;
        }

        long seq = ++_contentSizeSeq;
        _lastReportedContentHeight = clampedEffectiveHeight;

        bool visible = _isVisible && _flyoutWindow is { Visible: true };
        Log.Information(
            "AppHost.ResizeFlyout: seq={Seq} phase={Phase} height={Height} visible={Visible}",
            seq,
            phase,
            clampedEffectiveHeight,
            visible);

        // Decision 0013 (amending 0012): while visible the height change is an animated mode-B
        // retarget (child pinned to final size once; window frame glides). Hidden keeps the
        // instant path (priming / next-show), except during a close slide where applying would
        // fight the animation — the height is recorded for the next show instead.
        if (visible) {
            AnimateVisibleResize(clampedEffectiveHeight);
            return;
        }

        if (_closeAnimating) {
            return;
        }

        bool applied = ApplyImmediateResize(clampedEffectiveHeight, force: false);

        // The initial-show gate only concerns the hidden priming path; while visible the step
        // above already applied and there is nothing to complete.
        if (applied) {
            if (_pendingInitialShow && !_initialShowCompleted) {
                _hiddenContentSizeReportsForInitialShow++;
                if (!FlyoutInteractionPolicy.ShouldCompleteInitialShow(_hiddenContentSizeReportsForInitialShow)) {
                    Log.Information(
                        "AppHost: initial show waiting for settled content-size (report={Report}, height={Height})",
                        _hiddenContentSizeReportsForInitialShow,
                        clampedEffectiveHeight);
                    return;
                }
            }

            TryCompletePendingInitialShow();
        }
    }

    private void PrimeHiddenLayoutViewport() {
        if (_flyoutWindow is null) {
            return;
        }

        var dpiScale = _flyoutWindow.DeviceDpi / 96.0;
        int physicalWidth = (int)Math.Round(376 * dpiScale);
        int physicalHeight = (int)Math.Round(_currentEffectiveHeight * dpiScale);
        var primed = new Size(physicalWidth, physicalHeight);
        if (_flyoutWindow.Size == primed) {
            return;
        }

        _flyoutWindow.Size = primed;
        _flyoutWindow.RecalcNonClientFrame();
        Log.Information(
            "AppHost: primed hidden layout viewport {Width}x{Height} (effective 376x{EffectiveHeight})",
            physicalWidth,
            physicalHeight,
            _currentEffectiveHeight);
    }

    private bool _closeAnimating;

    /// <summary>Decision 0013: animated mode-B growth/shrink while visible. Bookkeeping adopts
    /// the new height immediately (0 ms truth for state); only the window's presentation
    /// animates, with the WebView child pinned to the final size once (no per-frame Chromium
    /// work — the measured 0003/0005 jank).</summary>
    private void AnimateVisibleResize(int newHeight) {
        var window = _flyoutWindow;
        if (window is null) {
            return;
        }

        if (newHeight == _currentEffectiveHeight && _lastAppliedHeight != 0) {
            return;
        }

        if (!_animationsEnabled || _presenter is null) {
            ApplyImmediateResize(newHeight, force: false);
            return;
        }

        _currentEffectiveHeight = newHeight;
        _lastAppliedHeight = newHeight;

        var target = ComputeBoundsForEffectiveHeight(newHeight);
        if (window.Bounds == target) {
            return;
        }

        var dpiScale = window.DeviceDpi / 96.0;
        var finalChildBounds = new Rectangle(
            Point.Empty,
            new Size((int)Math.Round(376 * dpiScale), (int)Math.Round(newHeight * dpiScale)));
        _webViewHost?.PinChildBounds(finalChildBounds);

        _presenter.AnimateBounds(
            window.Bounds,
            target,
            FlyoutRevealAnimator.GrowthDuration,
            completed: () => {
                _webViewHost?.UnpinChildBounds();
                window.RecalcNonClientFrame();
            });
    }

    private bool ApplyImmediateResize(int clampedEffectiveHeight, bool force) {
        if (!force && clampedEffectiveHeight == _currentEffectiveHeight && _lastAppliedHeight != 0) {
            Log.Information("AppHost.ResizeFlyout suppressed duplicate: {Height}", clampedEffectiveHeight);
            return false;
        }

        _currentEffectiveHeight = clampedEffectiveHeight;
        _lastAppliedHeight = clampedEffectiveHeight;

        // Hidden path (priming / next-show); the visible path animates via AnimateVisibleResize.
        if (_flyoutWindow != null) {
            UpdateFlyoutBounds();
        }

        return true;
    }

    private Rectangle ComputeBoundsForEffectiveHeight(int effectiveHeight) {
        if (_flyoutWindow is null || _trayIcon is null) {
            return Rectangle.Empty;
        }

        var dpiScale = _flyoutWindow.DeviceDpi / 96.0;
        int physicalWidth = (int)Math.Round(376 * dpiScale);

        if (_flyoutWindow.IsPinned) {
            var current = _flyoutWindow.Bounds;
            int physicalHeight = (int)Math.Round(effectiveHeight * dpiScale);
            return new Rectangle(current.X, current.Y, physicalWidth, physicalHeight);
        }

        var placement = _trayIcon.GetPlacementInfo();
        var monitor = (placement.Rect.Width > 0 && placement.Rect.Height > 0)
            ? Screen.FromRectangle(placement.Rect)
            : TrayIcon.GetTaskbarScreen();
        var anchor = FlyoutPlacement.ResolveAnchor(
            placement.Rect,
            monitor.WorkingArea,
            monitor.Bounds,
            placement.Edge,
            dpiScale);
        return FlyoutPlacement.CalculatePlacement(
            anchor,
            placement.Edge,
            monitor.WorkingArea,
            dpiScale,
            new Size(376, effectiveHeight));
    }

    private void TryCompletePendingInitialShow() {
        if (!_pendingInitialShow || _initialShowCompleted || _startupSilent || _headless) {
            return;
        }

        if (!_webViewReadyForShow) {
            return;
        }

        // Require at least one valid content-size report (not merely the placeholder) before first paint.
        if (_lastAppliedHeight == 0) {
            return;
        }

        Log.Information("AppHost: completing pending initial flyout show (height={Height})", _currentEffectiveHeight);
        ShowFlyout();

        // Clear the request only after the flyout is actually visible.
        if (FlyoutInteractionPolicy.ShouldClearInitialShowRequest(_flyoutWindow is { Visible: true })) {
            _pendingInitialShow = false;
            _initialShowCompleted = true;
            Log.Information("AppHost: initial show completed (visible=true)");
        }
        else {
            Log.Warning("AppHost: initial ShowFlyout did not yield Visible; pending show retained");
        }
    }

    private void OnTrayPreferencesChanged(object? sender, EventArgs e) {
        if (_userPreferences is not null) {
            _notificationCoordinator?.SetNotificationsEnabled(_userPreferences.NotificationsEnabled);
        }

        PostCurrentShellAppearance();
    }

    private ThemeKind ResolveEffectiveTheme() {
        ThemePreference preference = _userPreferences?.ThemePreference ?? ThemePreference.System;
        return EffectiveTheme.Resolve(preference, SystemTheme.ReadAppsTheme());
    }

    private void PostCurrentShellAppearance() {
        _stateEngine?.Post(new ShellAppearanceChangedEvent(
            ResolveEffectiveTheme(),
            ShellMaterialPolicy.ResolveTransparencyEnabled(),
            SystemTheme.ReadForcedColors(),
            SystemTheme.ReadClientAreaAnimation()));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle) {
            PostCurrentShellAppearance();
        }
    }

    private void ApplyNativeShellFromSnapshot(AppSnapshot snapshot) {
        if (_flyoutWindow is null) {
            return;
        }

        // Decision 0013: OS reduced-motion turns every presentation animation into the 0012
        // instant steps. Logged so "is it animating?" is a one-grep answer.
        _animationsEnabled = !snapshot.Shell.ReducedMotion;
        if (_animationsEnabled != _lastAnimationsEnabledLogged) {
            _lastAnimationsEnabledLogged = _animationsEnabled;
            Log.Information(
                "Shell animations {State} (OS client-area animation preference).",
                _animationsEnabled ? "ENABLED" : "DISABLED (OS reduced motion)");
        }

        var theme = snapshot.Shell.Theme;
        // v1 always opaque; ForcedColors still disables any residual transparency path.
        bool transparency = snapshot.Shell.TransparencyEnabled
            && !snapshot.Shell.ForcedColors
            && ShellMaterialPolicy.ResolveTransparencyEnabled();
        if (theme == _lastAppliedTheme && transparency == _lastAppliedTransparency) {
            return;
        }

        _lastAppliedTheme = theme;
        _lastAppliedTransparency = transparency;
        if (TryMarshalToFlyoutUi(new Action(() => {
            _flyoutWindow.ApplyShellAppearance(theme, transparency);
            _webViewHost?.ApplyOpaqueBackground(theme);
        }))) {
            return;
        }

        _flyoutWindow.ApplyShellAppearance(theme, transparency);
        _webViewHost?.ApplyOpaqueBackground(theme);
    }

    private void UpdateFlyoutBounds() {
        if (_flyoutWindow == null || _trayIcon == null) return;

        var physicalBounds = ComputeBoundsForEffectiveHeight(_currentEffectiveHeight);

        if (_flyoutWindow.IsPinned) {
            if (_flyoutWindow.Size != physicalBounds.Size) {
                _flyoutWindow.Size = physicalBounds.Size;
                Log.Information("UpdateFlyoutBounds (pinned): new size {Size}", _flyoutWindow.Size);
                _flyoutWindow.RecalcNonClientFrame();
            }
        }
        else if (_flyoutWindow.Bounds != physicalBounds) {
            _flyoutWindow.Bounds = physicalBounds;
            Log.Information("UpdateFlyoutBounds: physicalBounds={Bounds}", physicalBounds);
            _flyoutWindow.RecalcNonClientFrame();
        }
    }

    private void OnFlyoutDeactivate(object? sender, EventArgs e) {
        bool pinned = _flyoutWindow?.IsPinned ?? false;
        if (!FlyoutInteractionPolicy.ShouldHonorLightDismiss(_suppressLightDismiss, _lightDismissArmed, pinned)) {
            Log.Information(
                "OnFlyoutDeactivate ignored: suppress={Suppress} armed={Armed} pinned={Pinned}",
                _suppressLightDismiss,
                _lightDismissArmed,
                pinned);
            return;
        }

        Log.Information("OnFlyoutDeactivate: isPinned={IsPinned}", pinned);
        HideFlyout();
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

        // Phase 8C: stop notification intents before producer teardown can invent removals.
        _notificationCoordinator?.BeginShutdown();

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
        _metadataLifecycle?.Dispose();
        _mdnsTrackerLifecycle?.Dispose();
        _mdnsFallbackLifecycle?.Dispose();
        _deviceTrackerLifecycle?.Dispose();
        _stateEngine?.Dispose();
        _appLifetimeCts.Dispose();

        _notificationCoordinator?.Dispose();
        _balloonSink?.Dispose();

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        if (_powerAndSession != null) {
            _powerAndSession.DisplaySettingsChanged -= OnDisplayChanged;
            _powerAndSession.Suspend -= OnPowerSuspend;
            _powerAndSession.Resume -= OnPowerResume;
            _powerAndSession.SessionSwitch -= OnSessionSwitch;
            _powerAndSession.Dispose();
        }
        if (_flyoutWindow != null) {
            _flyoutWindow.Deactivate -= OnFlyoutDeactivate;
            _flyoutWindow.Dispose();
        }
        if (_trayIcon != null) {
            _trayIcon.PreferencesChanged -= OnTrayPreferencesChanged;
            _trayIcon.Dispose();
        }

        _webViewHost?.Dispose();
        _singleInstance?.Dispose();
        Log.CloseAndFlush();
    }
}
