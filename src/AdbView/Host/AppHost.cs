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
    private bool _animationsEverLogged;
    private readonly bool _nativeUi;
    private NativeUi.NativeFlyoutWindow? _nativeWindow;
    private NativeUi.Shell.NativeShellViewModel? _shellViewModel;
    private NativeUi.StatusBubbleWindow? _statusBubble;
    private NativeUi.StatusBubblePresenter? _statusBubblePresenter;
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

    /// <summary>Decision 0019: delivery-time predicate for the notification coordinator. Read from
    /// the state/thread-pool threads, so it goes through Volatile rather than the plain field.</summary>
    internal bool IsFlyoutVisible => System.Threading.Volatile.Read(ref _isVisible);
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
        IServiceStateSource? serviceStateSource = null,
        bool nativeUi = false) {
        _fixtureSuffix = fixtureSuffix;
        _headless = headless;
        _nativeUi = nativeUi;
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
            _trayIcon.OnExitRequested += (s, e) => {
                if (_nativeUi) {
                    // Dispatcher.Run keeps the STA loop alive; Application.Exit would not.
                    _nativeWindow?.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
                    return;
                }

                Application.Exit();
            };
            _trayIcon.PreferencesChanged += OnTrayPreferencesChanged;

            _powerAndSession = new PowerAndSession();
            _powerAndSession.DisplaySettingsChanged += OnDisplayChanged;
            _powerAndSession.Suspend += OnPowerSuspend;
            _powerAndSession.Resume += OnPowerResume;
            _powerAndSession.SessionSwitch += OnSessionSwitch;

            if (_nativeUi) {
                // Decision 0015 Phase I1: native WPF shell. Tray stays WinForms interop.
                // The WPF Application object must exist before any card view-model reads
                // Application.Current.Resources (theme tokens) — it is null otherwise, and a
                // device-list render would NRE the dispatcher and kill the process silently.
                if (System.Windows.Application.Current is null) {
                    var app = new System.Windows.Application {
                        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                    };
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary {
                        Source = new Uri("pack://application:,,,/NativeUi/Themes/FluentDark.xaml", UriKind.Absolute),
                    });
                }

                _nativeWindow = new NativeUi.NativeFlyoutWindow();
                _nativeWindow.Deactivated95 += OnFlyoutDeactivate;
                // Keep the visibility flag in step with the window itself. It is assigned from
                // ShellVisible immediately after Show, before the window has loaded, so it could
                // read false while the flyout was on screen — which silently defeated decision
                // 0019's "no toast while the flyout is visible" suppression. IsVisibleChanged
                // fires on the UI thread; readers use a volatile read.
                // ... and re-evaluate the bubble on the same transition. HideNativeFlyout refreshes
                // it while the close slide is still running (the window is not hidden yet), so
                // without this the bubble stayed hidden after the flyout had gone.
                _nativeWindow.IsVisibleChanged += (_, e) => {
                    _isVisible = (bool)e.NewValue;
                    RefreshStatusBubble();
                };
                _shellViewModel = (NativeUi.Shell.NativeShellViewModel)((NativeUi.Shell.ShellView)_nativeWindow.Content).DataContext;
                _shellViewModel.StartQrRequested += StartQrPairingSession;
                _shellViewModel.QrCancelRequested += () => _qrPairingSession?.Cancel();
                _shellViewModel.PinChangeRequested += OnPinChangeRequestedFromShell;
                // Native surface forwards the exact action verbs the bridge contract defines
                // (WebActions.cs); the same coordinators serve both UIs.
                _shellViewModel.PairingActionRequested += (action, offerId, code) => {
                    switch (action) {
                        case "pairStart":
                            _pairingSession?.Start(offerId);
                            break;
                        case "pairSubmit":
                            _pairingSession?.Submit(offerId, code ?? string.Empty);
                            break;
                        case "pairCancel":
                            _pairingSession?.Cancel(offerId);
                            break;
                        case "pairQrStart":
                            // Decision 0017: QR failure offers "Scan again" - always a fresh code.
                            StartQrPairingSession();
                            break;
                        default:
                            Log.Warning("NativeUi: unknown pairing action {Action}", action);
                            break;
                    }
                };
                _shellViewModel.DiscoveryRepairRequested += repairId => _ = RunDiscoveryRepairAsync(repairId);
                _shellViewModel.DiscoveryRecheckRequested += () => _ = RunDiscoveryCheckAsync();
            }
            else {
                _flyoutWindow = new FlyoutWindow();
                _flyoutWindow.Deactivate += OnFlyoutDeactivate;
            }
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
                submitCode: (offerId, code) => _pairingSession.Submit(offerId, code),
                isStartActive: offerId => _pairingSession.IsActive(offerId));

            // Decision 0013: graceful presentation (mode-B growth + open/close slides).
            // Native shell: DispatcherTimer tick + physical SetWindowPos on the WPF hwnd.
            _presenter = new FlyoutRevealAnimator(
                _nativeUi ? new NativeUi.DispatcherTimerTickSource() : new FormsTimerTickSource(),
                bounds => {
                    if (_nativeUi) {
                        _nativeWindow?.SetPhysicalBounds(bounds);
                    }
                    else if (_flyoutWindow is { IsDisposed: false } window) {
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
            onPairQrStart: StartQrPairingSession,
            onPairQrCancel: () => _qrPairingSession?.Cancel());

        if (_nativeUi) {
            // Snapshots published before the coordinator existed (PostCurrentShellAppearance's
            // async post) never reached it — seed its baseline before declaring readiness.
            _coordinator.UpdateSnapshot(_stateEngine.CurrentSnapshot);
            // No WebView handshake exists in native mode; declare readiness so snapshots
            // start flowing to the shell instead of queuing in UpdateSnapshot.
            _coordinator.MarkReady();

            // Decision 0020: the status bubble is opt-in, so nothing is constructed until the
            // preference says so.
            if (_userPreferences?.StatusBubbleEnabled == true) {
                EnsureStatusBubble();
            }
        }

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

        if (!headless && !_nativeUi) {
            _webViewHost = new WebViewHost(_flyoutWindow!, _coordinator);
        }

        if (!headless) {
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
        _notificationCoordinator = new NotificationCoordinator(
            clock,
            delay,
            _notificationSink,
            suppressDelivery: () => IsFlyoutVisible);
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
        if (_nativeUi) {
            // Native shell: notifications deliver on the WPF Dispatcher.
            var dispatcher = _nativeWindow?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            if (!dispatcher.CheckAccess()) {
                _ = dispatcher.BeginInvoke(work);
                return;
            }

            work();
            return;
        }

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
        if (_nativeUi) {
            if (TryMarshalToFlyoutUi(new Action(ShowFlyoutOnly))) {
                return;
            }

            ShowNativeFlyout();
            return;
        }

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
        // Decision 0017: the reducer clears a fulfilled pairing wait but cannot release the
        // coordinator's single-operation slot — a parked slot rejects every later pairStart.
        _pairingSession?.ReconcileWithSnapshot(snapshot);
        // Decision 0020: the bubble mirrors the tray aggregate, so it can never disagree with
        // the tray icon. The tray's own tooltip is the bubble's status text.
        _statusBubblePresenter?.Accept(snapshot.Health.Tray, snapshot.Health.TrayTooltip);
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
        // Forensics 2026-10-03: stale binaries masqueraded as unfixed bugs (pin/theme fixes
        // absent from every user session). Identity + resolved theme are logged at startup so
        // one log glance answers "which build, which theme".
        Log.Information(
            "AppHost identity: version={Version} native={Native} theme={Theme} transparency={Transparency}",
            typeof(AppHost).Assembly.GetName().Version?.ToString() ?? "unknown",
            _nativeUi,
            theme,
            transparency);

        if (_nativeUi) {
            // Decision 0015 Phase I1: native shell boots on the WPF dispatcher. No WebView,
            // no measurement priming — the window self-sizes from XAML layout (full self-
            // sizing + growth anchoring lands in Phase I3). I1 presents at the 500 epx
            // placeholder so the initial show fires; device content updates in place.
            _nativeWindow!.ApplyShellAppearance(theme, transparency);
            _webViewReadyForShow = true;
            Log.Information("AppHost.Run: native UI mode (dispatcher loop).");
            _currentEffectiveHeight = 500;
            _lastAppliedHeight = 500;
            TryCompletePendingInitialShow();
            System.Windows.Threading.Dispatcher.Run();
            return;
        }

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
        if (_nativeUi) {
            if (TryMarshalToFlyoutUi(new Action(OnToggleFlyout))) {
                return;
            }

            if (_isVisible) {
                HideNativeFlyout();
            }
            else {
                ShowNativeFlyout();
            }

            return;
        }

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

    private void ShowNativeFlyout() {
        var window = _nativeWindow;
        if (window is null || _trayIcon is null) {
            return;
        }

        RefreshAnimationPreference();

        var placement = _trayIcon.GetPlacementInfo();
        var monitor = (placement.Rect.Width > 0 && placement.Rect.Height > 0)
            ? Screen.FromRectangle(placement.Rect)
            : TrayIcon.GetTaskbarScreen();

        var physicalBounds = FlyoutPlacement.CalculatePlacement(
            FlyoutPlacement.ResolveAnchor(
                placement.Rect,
                monitor.WorkingArea,
                monitor.Bounds,
                placement.Edge,
                window.DpiScale),
            placement.Edge,
            monitor.WorkingArea,
            window.DpiScale,
            new Size(376, _currentEffectiveHeight));

        _suppressLightDismiss = true;
        _lightDismissArmed = false;
        int showGen = ++_showGeneration;

        _presenter?.Cancel();
        bool reducedMotion = !_animationsEnabled;
        window.SetPhysicalBounds(reducedMotion
            ? physicalBounds
            : SlideTarget(physicalBounds, placement.Edge, monitor.WorkingArea, intoTray: true));
        window.TopMost = window.IsPinned;
        window.Show();

        if (!window.IsPinned) {
            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND(window.Handle));
        }

        _isVisible = window.ShellVisible;

        if (reducedMotion) {
            _suppressLightDismiss = false;
        }

        if (!reducedMotion) {
            _presenter?.AnimateBounds(
                window.GetPhysicalBounds(),
                physicalBounds,
                FlyoutRevealAnimator.OpenDuration,
                completed: () => {
                    if (showGen != _showGeneration) {
                        return;
                    }

                    _suppressLightDismiss = false;
                    _lightDismissArmed = true;
                    Log.Information("ShowNativeFlyout: open slide completed.");
                });
        }
        else {
            _lightDismissArmed = true;
        }

        NotifyMetadataPresentation();
        MaybeStartDiscoveryCheck();
        RefreshStatusBubble();

        if (FlyoutInteractionPolicy.ShouldClearPendingInitialShowOnManualShow(_pendingInitialShow, _initialShowCompleted)) {
            _pendingInitialShow = false;
            _initialShowCompleted = true;
        }
    }

    private void HideNativeFlyout() {
        var window = _nativeWindow;
        if (window is null) {
            return;
        }

        _showGeneration++;
        _lightDismissArmed = false;
        _isVisible = false;
        RefreshStatusBubble();
        NotifyMetadataPresentation();

        if (!_animationsEnabled) {
            _presenter?.Cancel();
            window.Hide();
            return;
        }

        if (_trayIcon is null) {
            window.Hide();
            return;
        }

        var placement = _trayIcon.GetPlacementInfo();
        var monitor = (placement.Rect.Width > 0 && placement.Rect.Height > 0)
            ? Screen.FromRectangle(placement.Rect)
            : TrayIcon.GetTaskbarScreen();
        int closeGen = _showGeneration;
        _closeAnimating = true;

        _presenter?.Cancel();
        _presenter?.AnimateBounds(
            window.GetPhysicalBounds(),
            SlideTarget(window.GetPhysicalBounds(), placement.Edge, monitor.WorkingArea, intoTray: true),
            FlyoutRevealAnimator.CloseDuration,
            completed: () => {
                _closeAnimating = false;
                if (closeGen != _showGeneration) {
                    return; // a show superseded this close mid-slide
                }

                window.Hide();
                Log.Information("HideNativeFlyout: close slide completed.");
            });
    }

    private void ShowFlyout() {
        if (_nativeUi) {
            ShowNativeFlyout();
            return;
        }

        if (_flyoutWindow == null || _trayIcon == null) return;

        // Decision 0013: re-read the OS animation preference at every open — the Win11
        // "Animation effects" toggle does not raise a preference category we handle, so this
        // is the reliable moment to pick up a flip (one probe per user-initiated open).
        RefreshAnimationPreference();

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

    /// <summary>Decision 0013: re-reads the OS client-area-animation preference and applies it
    /// through <see cref="ShellMotionGate"/>. The gate is assigned DIRECTLY here — the show path
    /// reads it synchronously right after this call — and the appearance event carries the real
    /// value so the snapshot flag follows. (The 2026-10-04 audit found the event used to carry a
    /// hardcoded false, which reset the gate to "enabled" on every snapshot and permanently
    /// defeated reduced motion.) No timer — one probe per user-initiated open.</summary>
    private void RefreshAnimationPreference() {
        bool osAnimations = SystemTheme.ReadClientAreaAnimation();
        (bool enabled, bool changed) = ShellMotionGate.Apply(osAnimations, _animationsEnabled);
        if (!changed) {
            return;
        }

        _animationsEnabled = enabled;
        Log.Information(
            "Shell animations {State} (re-read at flyout open).",
            enabled ? "ENABLED" : "DISABLED (OS reduced motion)");
        _animationsEverLogged = true;
        PostCurrentShellAppearance();
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
        if (_nativeUi) {
            // WinForms contract parity: return false = "caller proceeds inline". Only marshal
            // when actually off the shell thread.
            var dispatcher = _nativeWindow?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) {
                return false;
            }

            _ = dispatcher.BeginInvoke(method, args);
            return true;
        }

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

        if (_nativeUi) {
            // Native shell binds the AppSnapshot record directly (no JSON hop).
            Log.Debug("NativeUi snapshot: rev={Rev} devices={Devices} pairing={Pairing}", envelope.Snapshot.Revision, envelope.Snapshot.Devices.Length, envelope.Snapshot.Pairing is not null);
            _shellViewModel?.ApplySnapshot(envelope.Snapshot);
            return;
        }

        var json = BridgeSerializer.SerializeSnapshotEnvelope(envelope);
        _webViewHost?.PostWebMessageAsJson(json);
    }

    public void SetPinned(bool isPinned) {
        if (TryMarshalToFlyoutUi(new Action<bool>(SetPinned), isPinned)) {
            return;
        }

        if (_nativeUi && _nativeWindow is not null) {
            if (_nativeWindow.IsPinned == isPinned) {
                return;
            }

            _nativeWindow.IsPinned = isPinned;
            Log.Information("AppHost.SetPinned: native IsPinned={IsPinned}", isPinned);
            // The metadata tick gates on pinned AND visible; skip-starved probes after pinning
            // were a native-only gap (the WinForms branch falls through to this call).
            NotifyMetadataPresentation();
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

        if (_nativeUi) {
            HideNativeFlyout();
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

        // Decision 0015 Phase I1: native shell self-sizes — no report-driven sizing.
        if (_nativeUi) {
            return;
        }

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
        bool visibleNow = _nativeUi
            ? (_nativeWindow?.ShellVisible ?? false)
            : _flyoutWindow is { Visible: true };
        if (FlyoutInteractionPolicy.ShouldClearInitialShowRequest(visibleNow)) {
            _pendingInitialShow = false;
            _initialShowCompleted = true;
            Log.Information("AppHost: initial show completed (visible=true)");
        }
        else {
            Log.Warning("AppHost: initial ShowFlyout did not yield Visible; pending show retained");
        }
    }

    /// <summary>Decision 0020: creates the bubble on first use. Off by default, so an owner who
    /// never enables the toggle pays nothing.</summary>
    private void EnsureStatusBubble() {
        if (_nativeUi == false || _statusBubble is not null) {
            return;
        }

        try {
            _statusBubblePresenter = new NativeUi.StatusBubblePresenter(
                () => App.SystemClock.UtcNow,
                static (span, ct) => Task.Delay(span, ct));
            _statusBubble = new NativeUi.StatusBubbleWindow();
            _statusBubble.ApplyTheme(_lastAppliedTheme);
            // Clicking the bubble opens the flyout — the same path as the tray's "Open ADB View".
            _statusBubble.OpenRequested += ShowFlyoutOnly;
            _statusBubblePresenter.Changed += OnStatusBubbleChanged;

            // Seed with the current truth so the toggle shows something immediately.
            var current = _stateEngine?.CurrentSnapshot;
            if (current is not null) {
                _statusBubblePresenter.Accept(current.Health.Tray, current.Health.TrayTooltip);
            }
        }
#pragma warning disable CA1031 // R-08: construction failure is logged; the toggle simply has no effect.
        catch (Exception ex) {
            Log.Warning(ex, "StatusBubble: construction failed; the bubble stays off");
            DisposeStatusBubble();
        }
#pragma warning restore CA1031
    }

    private void DisposeStatusBubble() {
        _statusBubblePresenter?.Dispose();
        _statusBubblePresenter = null;

        var window = _statusBubble;
        _statusBubble = null;
        if (window is null) {
            return;
        }

        try {
            window.Hide();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException) {
            Log.Warning(ex, "StatusBubble: hide on teardown failed");
        }
    }

    private void OnStatusBubbleChanged(NativeUi.BubbleState? state) {
        _ = state;
        RefreshStatusBubble();
    }

    /// <summary>
    /// Shows, hides or re-anchors the bubble from the presenter's current state.
    ///
    /// The bubble is hidden while the flyout is visible: the flyout occupies the same bottom-right
    /// corner rectangle (it also intercepts the mouse over it), and it is already showing exactly
    /// the truth the bubble would echo — the same reasoning as decision 0019's "no toast while the
    /// flyout is on screen".
    /// </summary>
    private void RefreshStatusBubble() {
        var window = _statusBubble;
        if (window is null) {
            return;
        }

        // Not IsFlyoutVisible: that field is assigned from ShellVisible immediately after Show,
        // before the window has loaded, so it can read false while the flyout is on screen. The
        // window's own IsVisible is accurate for "is it on screen right now".
        bool flyoutOnScreen = _nativeUi ? _nativeWindow?.IsVisible == true : _isVisible;
        NativeUi.BubbleState? state = flyoutOnScreen ? null : _statusBubblePresenter?.Current;

        void Deliver() {
            if (state is null) {
                window.Apply(null);
                return;
            }

            window.ApplyTheme(_lastAppliedTheme);
            AnchorStatusBubble(window);
            window.Apply(state);
        }

        if (window.Dispatcher.CheckAccess()) {
            Deliver();
        }
        else {
            _ = window.Dispatcher.BeginInvoke((Action)Deliver);
        }
    }

    /// <summary>
    /// Bottom-right of the taskbar monitor's work area — the work area already excludes the
    /// taskbar on any edge, so this does not chase the taskbar rect (research
    /// 2026-10-03-status-bubble-native-approaches.md). WinForms returns that rect in physical
    /// pixels, so it is divided back into DIP for WPF.
    /// </summary>
    /// <summary>
    /// Hands the bubble the bottom-right corner of the taskbar monitor's work area, in DIP. The
    /// window positions itself from that, so hover growth extends leftwards and the corner holds.
    /// WinForms reports the work area in physical pixels, hence the division by the scale.
    /// </summary>
    private static void AnchorStatusBubble(NativeUi.StatusBubbleWindow window) {
        var work = TrayIcon.GetTaskbarScreen().WorkingArea;
        double scale = window.DpiScale <= 0 ? 1.0 : window.DpiScale;
        window.SetAnchor(work.Right / scale, work.Bottom / scale);
    }

    private void OnTrayPreferencesChanged(object? sender, EventArgs e) {
        if (_userPreferences is not null) {
            _notificationCoordinator?.SetNotificationsEnabled(_userPreferences.NotificationsEnabled);
        }

        // Decision 0020: the tray toggle takes effect immediately.
        if (_userPreferences?.StatusBubbleEnabled == true) {
            EnsureStatusBubble();
        }
        else {
            DisposeStatusBubble();
        }

        PostCurrentShellAppearance();
    }

    private ThemeKind ResolveEffectiveTheme() {
        ThemePreference preference = _userPreferences?.ThemePreference ?? ThemePreference.System;
        return EffectiveTheme.Resolve(preference, SystemTheme.ReadAppsTheme());
    }

    private void PostCurrentShellAppearance() {
        // Decision 0013 follow-up (forensics 2026-10-02 #3): the ctor-context probe of
        // SPI_GETCLIENTAREAANIMATION returns OFF on this machine class while the OS value is
        // verifiably ON (PowerShell probe: 4/4 TRUE; app ctor probe: DISABLED seconds later in
        // the same session, then ENABLED at every flyout open). The startup post therefore
        // ASSUMES animations on; RefreshAnimationPreference re-probes at every flyout open —
        // which has been reliable in 6/6 sessions — and corrects the gate (and the web layer's
        // reduced-motion snapshot flag) before any meaningful interaction. The web layer's
        // @media (prefers-reduced-motion) CSS is independent of this value either way.
        // The startup post runs in the ctor context, where the probe is unreliable, and the gate
        // starts ASSUMING animations on (corrected by the first reliable probe at flyout open —
        // forensics 2026-10-02 #3). Posting the gate (not a literal) is what keeps the snapshot's
        // ReducedMotion flag truthful for the shell, the web layer, and the gate's own mirror.
        _stateEngine?.Post(new ShellAppearanceChangedEvent(
            ResolveEffectiveTheme(),
            ShellMaterialPolicy.ResolveTransparencyEnabled(),
            SystemTheme.ReadForcedColors(),
            !_animationsEnabled));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
        // Decision 0013: any preference change re-evaluates shell appearance — the Win11
        // "Animation effects" toggle proved not to arrive under the previously-handled
        // categories, and the reducer dedupes no-op updates anyway.
        PostCurrentShellAppearance();
    }

    private void ApplyNativeShellFromSnapshot(AppSnapshot snapshot) {
        if (!_nativeUi && _flyoutWindow is null) {
            return;
        }

        // Decision 0013: OS reduced-motion turns every presentation animation into the 0012
        // instant steps. Logged on first evaluation and on change — a session that STARTS
        // disabled must log too (forensics 2026-10-02 #2: the change-only guard was silent
        // in exactly the case it was built to diagnose).
        bool animationsEnabled = !snapshot.Shell.ReducedMotion;
        if (animationsEnabled != _animationsEnabled || !_animationsEverLogged) {
            Log.Information(
                "Shell animations {State} (OS client-area animation preference).",
                animationsEnabled ? "ENABLED" : "DISABLED (OS reduced motion)");
            _animationsEverLogged = true;
        }

        _animationsEnabled = animationsEnabled;

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
        if (_nativeUi) {
            if (TryMarshalToFlyoutUi(new Action(() => ApplyNativeTheme(theme, transparency)))) {
                return;
            }

            ApplyNativeTheme(theme, transparency);
            return;
        }

        // WebView path (guarded above: !_nativeUi && _flyoutWindow is not null).
        if (TryMarshalToFlyoutUi(new Action(() => {
            _flyoutWindow?.ApplyShellAppearance(theme, transparency);
            _webViewHost?.ApplyOpaqueBackground(theme);
        }))) {
            return;
        }

        _flyoutWindow?.ApplyShellAppearance(theme, transparency);
        _webViewHost?.ApplyOpaqueBackground(theme);
    }

    private void StartQrPairingSession() {
        // Decision 0016: the phone advertises only while its pairing dialog is open — a query
        // burst now is what lets the fallback bridge the deaf-adb window (R-05: event, not timer).
        _qrPairingSession?.Start();
        _mdnsFallbackLifecycle?.TriggerQuery();
    }

    private void OnPinChangeRequestedFromShell(bool pinned) {
        // The StateEngine is the single pin authority (identical to the bridge 'pin' action).
        // The earlier direct SetPinned bypass let every following snapshot silently un-pin —
        // the round trip through the reducer is what makes the icon and the window agree.
        // Headless constructs no engine; the shell only exists headed.
        if (_stateEngine is not null) {
            _stateEngine.Post(new SetPinnedEvent(pinned));
        }
    }

    private void ApplyNativeTheme(ThemeKind theme, bool transparency) {
        _nativeWindow?.ApplyShellAppearance(theme, transparency);
        SwapAppThemeDictionary(theme);
        _shellViewModel?.RefreshThemeResources();
        Log.Information("Native shell theme applied: {Theme} (transparency={Transparency})", theme, transparency);
    }

    private static void SwapAppThemeDictionary(ThemeKind theme) {
        if (System.Windows.Application.Current is null) {
            return;
        }

        var uri = new Uri(
            theme == ThemeKind.Light
                ? "pack://application:,,,/NativeUi/Themes/FluentLight.xaml"
                : "pack://application:,,,/NativeUi/Themes/FluentDark.xaml",
            UriKind.Absolute);
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++) {
            if (merged[i].Source?.OriginalString.Contains("Fluent", StringComparison.Ordinal) == true) {
                merged[i] = new System.Windows.ResourceDictionary { Source = uri };
                Log.Information("App theme dictionary swapped to {Theme}", theme);
                return;
            }
        }

        merged.Add(new System.Windows.ResourceDictionary { Source = uri });
        Log.Information("App theme dictionary added: {Theme}", theme);
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
        bool pinned = _nativeUi
            ? _nativeWindow?.IsPinned ?? false
            : _flyoutWindow?.IsPinned ?? false;
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

        DisposeStatusBubble();
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
