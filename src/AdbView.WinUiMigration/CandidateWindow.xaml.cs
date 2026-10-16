namespace AdbView.WinUiMigration;

using System.Text.Json;
using System.Threading;
using AdbView.Presentation;
using AdbView.State;
using AdbView.WinUiMigration.Host;
using AdbView.WinUiMigration.Host.Native;
using AdbView.WinUiMigration.NativeUi;
using AdbView.WinUiMigration.NativeUi.Motion;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Win32;
using Windows.Win32.Foundation;

internal sealed partial class CandidateWindow : Window, IDisposable {
    private readonly CandidateOptions _options;
    private readonly List<UiAssertion> _assertions = [];
    private readonly PanelWindowController _windowController;
    private readonly SnapshotPresenter _presenter;
    private readonly SnapshotMailbox _mailbox;
    private readonly MotionPolicy _motionPolicy;
    private readonly SystemMotionPreferenceSource _motionSource;
    private readonly MotionPreferenceSubscription _motionSubscription;
    private readonly SurfaceTransitionController _surface;
    private readonly NativeShellOwner? _nativeOwner;
    private readonly List<TaskCompletionSource> _bubbleDeadlines = [];
    private readonly System.Diagnostics.Stopwatch _fixtureClock = System.Diagnostics.Stopwatch.StartNew();
    public event Action<ShellAppearanceChangedEvent>? AppearanceChangedRequested;
    private Control? _initialFocus;
    private TextBox? _codeInput => _presenter.Pairing?.CodeInput;
    private readonly FixtureShellActions _fixtureActions = new();
    private LiveShell? _liveShell;
    private bool _liveVisible;
    private bool _verificationStarted;
    private bool _disposed;
    private ScrollViewer BodyScroll => _presenter.ListScroll;

    public static void ValidateFixture(string fixture) {
        if (!DeviceFixtures.Names.Contains(fixture, StringComparer.Ordinal)) {
            throw new ArgumentException("Unknown candidate fixture.", nameof(fixture));
        }
    }

    internal CandidateWindow(CandidateOptions options) {
        _options = options;
        Stage("initializing window XAML");
        try { InitializeComponent(); }
        catch (Exception exception) { Stage(exception.ToString()); throw; }
        Stage("window XAML initialized");
        HeaderPhone.Data = ViewElements.PhoneGeometry();
        if (options.Fixture == "text-200") { AppTitle.FontSize = 29; BackButton.FontSize = 28; }
        Stage("header geometry assigned");
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter) {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        _windowController = new PanelWindowController(this);
        _windowController.PlaceBeforeShow();
        Stage("native placement complete");
        _motionSource = new SystemMotionPreferenceSource();
        _motionPolicy = new MotionPolicy(_motionSource.Enabled);
        LiveShell? liveShell = null;
        if (options.Live) {
            // M08 live-composition slice: created before the presenter because the presenter's
            // actions come from the backend adapter; its events fire only after Start(), which
            // this constructor never performs.
            liveShell = new LiveShell(
                () => Volatile.Read(ref _liveVisible),
                PublishLiveSnapshot,
                work => DispatcherQueue.TryEnqueue(() => work()));
            _liveShell = liveShell;
        }
        _presenter = new SnapshotPresenter(Root, PageHost, BackButton, PinButton,
            options.Fixture == "text-200" ? 2 : 1, options.ResultPath is null ? NativeClipboard.Copy : RecordFixtureCopy,
            liveShell is not null ? liveShell.Actions : _fixtureActions,
            _motionPolicy, () => AdbView.Bridge.FixtureSnapshotProvider.T0 + _fixtureClock.Elapsed);
        _motionSubscription = new MotionPreferenceSubscription(_motionSource, DispatcherQueue, ApplyMotionPreference);
        _surface = new SurfaceTransitionController(() => { AppWindow.Show(); Activate(); }, () => AppWindow.Hide(), interactive => {
            Root.IsHitTestVisible = interactive;
            _presenter.SetPresentationVisible(interactive);
        });
        _mailbox = new SnapshotMailbox(action => DispatcherQueue.TryEnqueue(() => action()), RenderSnapshot);
        if (options.Live || options.Fixture is "native-scenarios" or "native-bubble") {
            _nativeOwner = new NativeShellOwner(this, Root, _presenter, _windowController, () => _presenter.Snapshot,
                ShowSurface, HideSurface,
                options.Live ? CandidateOptions.LiveTrayIdentity : Guid.Parse(options.InstanceName[CandidateOptions.IdentityPrefix.Length..]),
                () => AdbView.Bridge.FixtureSnapshotProvider.T0 + _fixtureClock.Elapsed,
                options.ResultPath is null ? Task.Delay : HoldBubbleDeadline);
            if (liveShell is not null) {
                liveShell.AttachTray(_nativeOwner.Tray);
                // Pin stays snapshot-owned: the request rounds trips through the reducer.
                _presenter.PinRequested += OnLivePinRequested;
                // Reduced-motion changes are backend truth, not only local presentation.
                AppearanceChangedRequested += OnLiveAppearanceChanged;
            }
        }
        _mailbox.Publish(liveShell is not null ? liveShell.InitialSnapshot : DeviceFixtures.Snapshot(options.Fixture));
        Stage("initial snapshot published");
        Root.Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void PublishLiveSnapshot(AppSnapshot snapshot) {
        if (!_disposed) { _mailbox.Publish(snapshot); }
    }

    private void OnLivePinRequested() {
        bool? pinned = _presenter.Snapshot?.Shell.Pinned;
        if (pinned is { } current) { _liveShell?.PostPinned(!current); }
    }

    private void OnLiveAppearanceChanged(ShellAppearanceChangedEvent appearance) {
        _liveShell?.PostAppearance(appearance);
    }

    private readonly List<string> _fixtureCopies = [];
    private bool _fixtureCopySucceeds = true;
    private void Stage(string value) {
        if (_options.ResultPath is not null) { File.AppendAllText(_options.ResultPath + ".startup.log", value + Environment.NewLine); }
    }
    private bool RecordFixtureCopy(string text) { _fixtureCopies.Add(text); return _fixtureCopySucceeds; }
    private void ApplyMotionPreference(bool enabled) {
        if (_disposed) { return; }
        _motionPolicy.SetSystemEnabled(enabled);
        if (_presenter.Snapshot is { Shell: { } shell }) {
            // M08 composition routes this existing event into StateEngine.Post.
            AppearanceChangedRequested?.Invoke(new ShellAppearanceChangedEvent(shell.Theme, shell.TransparencyEnabled, shell.ForcedColors, !enabled));
        }
    }
    public void ShowSurface() {
        _liveVisible = true;
        _surface.Open();
        _nativeOwner?.VisibilityChanged();
        _liveShell?.SetPresentationState(LivePinned);
    }
    public void HideSurface() {
        _liveVisible = false;
        _surface.Close();
        _nativeOwner?.VisibilityChanged();
        _liveShell?.SetPresentationState(LivePinned);
    }
    private bool LivePinned => _presenter.Snapshot?.Shell.Pinned ?? false;
    public void SuspendPresentation() => _surface.Suspend();
    public void ResumePresentation() => _surface.Resume();

    private void RenderSnapshot(AppSnapshot snapshot) {
        try { ApplySnapshot(snapshot); }
        catch (Exception exception) { Stage(exception.ToString()); Console.Error.WriteLine("Snapshot presentation: " + exception); throw; }
    }

    private void ApplySnapshot(AppSnapshot snapshot) {
        Stage("applying snapshot " + snapshot.Revision);
        _presenter.Apply(snapshot);
        _nativeOwner?.Apply(snapshot);
        Stage("presenter applied");
        _afterApplyForVerification?.Invoke(snapshot);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) {
        Stage("root loaded");
        if (_verificationStarted) { return; }
        _verificationStarted = true;
        await Task.Yield();
        if (_options.Fixture == "details") {
            _presenter.OpenDevice(_presenter.Snapshot!.Devices[0].Id);
            Root.UpdateLayout();
            await Task.Yield();
        }
        if (_presenter.Snapshot is { } initial && (initial.Pairing is not null || initial.PairingQr is not null)) {
            _presenter.OpenPairing(); Root.UpdateLayout(); await Task.Yield();
        }
        _initialFocus = _presenter.FirstFocus;
        _initialFocus.Focus(FocusState.Keyboard);
        if (_options.Fixture == "native-bubble") { _nativeOwner!.Tray.Select(4); _nativeOwner.Hide(); }
        if (_options.ResultPath is null) { return; }
        try {
            await VerifyAsync().ConfigureAwait(true);
            if (_options.Fixture == "list-scenarios") {
                await VerifyListScenariosAsync().ConfigureAwait(true);
            }
            if (_options.Fixture == "pairing-scenarios") {
                await VerifyPairingScenariosAsync().ConfigureAwait(true);
            }
            if (_options.Fixture == "motion-scenarios") {
                await VerifyMotionScenariosAsync().ConfigureAwait(true);
            }
            if (_options.Fixture == "motion-recording") {
                await RecordMotionAsync().ConfigureAwait(true);
            }
            if (_options.Fixture == "native-scenarios") {
                await VerifyNativeScenariosAsync().ConfigureAwait(true);
            }
            Environment.ExitCode = 0;
        }
        catch (InvalidOperationException exception) {
            _assertions.Add(new UiAssertion("verification-exception", false, exception.GetType().Name + ": " + exception.Message));
            Environment.ExitCode = 1;
        }
        try {
            var result = new {
                SchemaVersion = 1,
                Passed = Environment.ExitCode == 0 && _assertions.All(assertion => assertion.Passed),
                ProcessId = Environment.ProcessId,
                _options.Fixture,
                Instance = _options.InstanceName,
                UiThreadId = Environment.CurrentManagedThreadId,
                WindowsBuild = Environment.OSVersion.Version.ToString(),
                Executable = Environment.ProcessPath,
                NativeBoundsWrites = _windowController.NativeBoundsWrites,
                Assertions = _assertions,
            };
            await File.WriteAllTextAsync(_options.ResultPath, JsonSerializer.Serialize(result)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine("Candidate result write failed: " + exception);
            Environment.ExitCode = 1;
        }
        Close();
        Application.Current.Exit();
    }

    private void Check(string name, bool condition) {
        _assertions.Add(new UiAssertion(name, condition, null));
        if (!condition) { throw new InvalidOperationException("UI assertion failed: " + name); }
    }
    private bool HasText(string? expected) => _presenter.HasText(expected);

    private async Task VerifyAsync() {
        Check("real-xaml-ui-thread", DispatcherQueue.HasThreadAccess && Root.IsLoaded && Root.XamlRoot is not null);
        Check("visible-native-window", AppWindow.IsVisible && !AppWindow.IsShownInSwitchers);
        var handle = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Check("interactive-foreground-window", PInvoke.IsWindowVisible(handle) && PInvoke.GetForegroundWindow() == handle);
        Check("focus-in-xaml-root", ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), _initialFocus));
        Check("core-without-wpf-reference", typeof(AppSnapshot).Assembly.GetName().Name == "AdbView.Core"
            && !typeof(CandidateWindow).Assembly.GetReferencedAssemblies().Any(reference => reference.Name == "AdbView"));
        Check("isolated-fixture-identity", _options.InstanceName.StartsWith(CandidateOptions.IdentityPrefix, StringComparison.Ordinal));
        AppSnapshot snapshot = DeviceFixtures.Snapshot(_options.Fixture);
        Check("snapshot-pin-projection", AutomationProperties.GetName(PinButton) == (snapshot.Shell.Pinned ? "Unpin flyout" : "Pin flyout"));
        Check("stable-scroll-container", BodyScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Visible
            && BodyScroll.HorizontalScrollMode == ScrollMode.Disabled);
        if (snapshot.Health.Kind != GlobalHealthKind.Healthy) {
            Check("global-health-vocabulary", HasText(snapshot.Health.Title) && HasText(snapshot.Health.Detail) && HasText(snapshot.Health.Hint));
            Check("global-health-suppresses-empty", !HasText("No ADB devices"));
        }
        else if (_options.Fixture == "details") {
            DeviceView device = snapshot.Devices[0];
            Check("details-snapshot-values", HasText(device.DisplayName) && HasText(device.StateLabel)
                && HasText(device.Detail.ExactModel) && HasText(device.Detail.AndroidVersion)
                && device.Detail.CopyFields.All(field => HasText(field.Label) && HasText(field.Value)));
            Check("details-back-focus", BackButton.Visibility == Visibility.Visible && ReferenceEquals(_initialFocus, BackButton));
        }
        else if (snapshot.Pairing is { } operation) {
            Check("pairing-backend-wording", HasText(operation.Title) && HasText(operation.Detail));
            Check("pairing-entry-focus", _codeInput?.Visibility == Visibility.Visible
                ? _codeInput is not null && _codeInput.MaxLength == operation.CodeLength && ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), _codeInput)
                : ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), BackButton));
            Check("pairing-suppresses-device-empty-regions", !HasText("No ADB devices")
                && snapshot.Devices.All(device => !HasText(device.StatusLine)));
            if (_options.Fixture == "pairing-progress-reduced") {
                Check("reduced-busy-fixture-has-static-status-and-no-clocks", !_motionPolicy.Enabled
                    && !_presenter.Pairing!.BusyIndicator.IsActive && !_presenter.ContentMotion.IsActive && !_presenter.AgeTicker.IsRunning);
            }
        }
        else if (snapshot.PairingQr is { } qr) {
            await _presenter.Pairing!.Qr.Pending.ConfigureAwait(true);
            Check("qr-local-image-loaded", _presenter.Pairing.Qr.Image.Source is not null);
            Check("qr-snapshot-instructions", HasText(string.Join(Environment.NewLine, qr.Instructions)));
        }
        else if (snapshot.DiscoveryHealth is { } health) {
            Check("discovery-snapshot-wording", HasText(health.Title) && health.Evidence.All(HasText));
            Check("discovery-action-availability", _presenter.CheckButton!.IsEnabled == health.CanCheckAgain);
        }
        else if (!snapshot.PairingOffers.IsEmpty) {
            Check("offer-snapshot-wording", snapshot.PairingOffers.All(offer => HasText(offer.Heading) && HasText(offer.Detail)));
            Check("offer-action-present", SnapshotPresenter.Descendants(PageHost).OfType<Button>().Any(button => button.Content is string text && text == "Pair"));
        }
        else if (!snapshot.Devices.IsEmpty) {
            Check("device-name-status-vocabulary", snapshot.Devices.All(device => HasText(device.DisplayName) && HasText(device.StatusLine)));
            Check("local-warning-keeps-device-truth", snapshot.Warnings.All(warning => HasText(warning.Title) && HasText(warning.Detail)));
        }
        else {
            Check("healthy-empty-copy", HasText("No ADB devices") && HasText("Connect a device using USB or wireless debugging."));
        }
        int thread = Environment.CurrentManagedThreadId;
        await DispatcherTurnAsync().ConfigureAwait(true);
        Check("scenario-resumed-on-same-ui-thread", Environment.CurrentManagedThreadId == thread && DispatcherQueue.HasThreadAccess);
        if (snapshot.Devices.Length > 2) {
            Root.UpdateLayout();
            Check("density-scrolls-without-horizontal-overflow", BodyScroll.ScrollableHeight > 0 && BodyScroll.ScrollableWidth <= 1);
        }
        if (_options.Fixture == "text-200") {
            Check("fixture-text-is-200-percent", _presenter.Rows.Values.All(row => row.Name.FontSize == 30)
                && AppTitle.FontSize == 29 && BackButton.FontSize == 28);
        }
    }

    private async Task DispatcherTurnAsync() {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Check("dispatcher-accepts-scenario", DispatcherQueue.TryEnqueue(() => dispatched.SetResult()));
        await dispatched.Task.ConfigureAwait(true);
    }

    private void OnClosed(object sender, WindowEventArgs args) {
        Dispose();
    }

    /// <summary>Starts the live backend after the window exists. Fixtures never call this: they
    /// own their snapshots and must not touch ADB.</summary>
    internal void StartLiveBackend() => _liveShell?.Start();

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        Root.Loaded -= OnLoaded; Closed -= OnClosed;
        _liveShell?.Dispose();
        _nativeOwner?.Dispose();
        _motionSubscription.Dispose(); _motionSource.Dispose(); _surface.Dispose();
        _mailbox.Dispose(); _presenter.Dispose();
        GC.SuppressFinalize(this);
    }
    private sealed record UiAssertion(string Name, bool Passed, string? Detail);
}
