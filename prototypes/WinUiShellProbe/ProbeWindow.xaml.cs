namespace WinUiShellProbe;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;

internal sealed partial class ProbeWindow : Window, IDisposable {
    private readonly TransparentBackdrop? _transparentBackdrop;
    private SurfaceMotionProbe? _motion;
    private readonly ProbeContentController _content;
    private readonly UISettings _settings = new();
    private NativeLifetimeProbe? _native;
    private bool _environmentQueued;
    private bool _disposed;

    public void InitializeNative() => _native ??= new NativeLifetimeProbe(this, Root, PinButton, _content, QueueEnvironmentChange);

    public async Task VerifyStartupAsync(string path) {
        InitializeNative();
        long trayReady = System.Diagnostics.Stopwatch.GetTimestamp();
        _native!.ShowStartupBubble();
        await Task.Delay(150).ConfigureAwait(true);
        bool passed = !AppWindow.IsVisible && _native.BubbleVisible && _native.BubbleForegroundActivationCount == 0;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new {
            Passed = passed,
            TrayReadyQpc = trayReady,
            Frequency = System.Diagnostics.Stopwatch.Frequency,
            FlyoutVisible = AppWindow.IsVisible,
            BubbleVisible = _native.BubbleVisible,
            Meaning = "Fresh process; primary window was constructed but never activated or shown.",
        })).ConfigureAwait(true);
        Environment.ExitCode = passed ? 0 : 1;
        Dispose();
        Close();
        Application.Current.Exit();
    }

    private void QueueEnvironmentChange() {
        if (_environmentQueued || _disposed) {
            return;
        }
        _environmentQueued = true;
        if (!DispatcherQueue.TryEnqueue(() => {
            _environmentQueued = false;
            if (_disposed) {
                return;
            }
            var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
            double scale = PInvoke.GetDpiForWindow(hwnd) / 96.0;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var border = new SizeInt32(AppWindow.Size.Width - AppWindow.ClientSize.Width, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            var client = ProbePlacement.ClientSize(area, scale, border);
            Root.Width = client.Width / scale;
            Root.Height = client.Height / scale;
            Envelope.Width = Root.Width;
            Envelope.Height = Root.Height;
            if (AppWindow.ClientSize.Width != client.Width || AppWindow.ClientSize.Height != client.Height) {
                AppWindow.ResizeClient(client);
            }
            AppWindow.Move(ProbePlacement.Position(area, AppWindow.Size, scale));
            _native?.UpdateEnvironment();
        })) {
            throw new InvalidOperationException("Probe display-change dispatch failed.");
        }
    }

    public ProbeWindow() {
        ProbeDiagnostics.Stage("Loading probe XAML");
        InitializeComponent();
        ProbeDiagnostics.Stage("Probe XAML loaded");
        _content = new ProbeContentController(ContentRegion, BackButton, _settings.AnimationsEnabled);
        _settings.AnimationsEnabledChanged += OnMotionPreferenceChanged;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter) {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        bool transparent = Environment.GetCommandLineArgs().Contains("--surface", StringComparer.Ordinal);
        if (!transparent) {
            // Contract fallback B has no invisible native envelope around its surface.
            Envelope.Width = 376;
            Envelope.Height = 480;
            Root.Margin = new Thickness(0);
        }
        if (transparent) {
            ProbeDiagnostics.Stage("Installing transparent native backdrop brush");
            _transparentBackdrop = new TransparentBackdrop(this);
            ProbeDiagnostics.Stage("Transparent brush installed");
        }
        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
        double scale = PInvoke.GetDpiForWindow(hwnd) / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(Envelope.Width * scale), (int)Math.Round(Envelope.Height * scale)));
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(workArea.X + workArea.Width - AppWindow.Size.Width - (int)Math.Round(8 * scale),
            workArea.Y + workArea.Height - AppWindow.Size.Height - (int)Math.Round(8 * scale)));
        Root.Loaded += OnRootLoaded;
        Closed += (sender, args) => Dispose();
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _settings.AnimationsEnabledChanged -= OnMotionPreferenceChanged;
        _native?.Dispose();
        _native = null;
        _content.Dispose();
        _motion?.Dispose();
        _motion = null;
        SystemBackdrop = null;
        _transparentBackdrop?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnMotionPreferenceChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) {
        if (!DispatcherQueue.TryEnqueue(() => _content.SetMotionEnabled(_settings.AnimationsEnabled))) {
            ProbeDiagnostics.Stage("Motion preference arrived after the probe dispatcher stopped.");
        }
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs args) {
        Root.Loaded -= OnRootLoaded;
        ProbeDiagnostics.Stage("Probe root Loaded");
        if (Environment.GetCommandLineArgs().Contains("--native", StringComparer.Ordinal)) {
            InitializeNative();
            if (Environment.GetCommandLineArgs().Contains("--pinned", StringComparer.Ordinal)) {
                _native!.SetSyntheticPinned(true);
            }
            if (Environment.GetCommandLineArgs().Contains("--force-native-failure", StringComparer.Ordinal)) {
                var handle = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
                PInvoke.SendMessage(handle, NativeLifetimeProbe.TestMessage, new WPARAM(999), default);
            }
        }
        var resultArgument = Environment.GetCommandLineArgs()
            .FirstOrDefault(value => value.StartsWith("--verify=", StringComparison.Ordinal));
        if (resultArgument is null) {
            return;
        }

        string resultPath = resultArgument["--verify=".Length..];
        bool useAcrylic = Environment.GetCommandLineArgs().Contains("--acrylic", StringComparer.Ordinal);
        SystemBackdropElement? boundedBackdrop = null;
        if (useAcrylic) {
            ProbeDiagnostics.Stage("Creating bounded Acrylic after root Loaded");
            boundedBackdrop = new SystemBackdropElement {
                CornerRadius = new CornerRadius(8),
                SystemBackdrop = new DesktopAcrylicBackdrop(),
            };
            BackdropHost.Children.Add(boundedBackdrop);
        }
        // Test-driver delay only; this prototype does not animate from a timer.
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(true);
        var initialSize = AppWindow.ClientSize;
        _content.ApplySnapshot(20, 1);
        _content.ShowDetails(0);
        await Task.Delay(40).ConfigureAwait(true);
        bool olderRejected = !_content.ApplySnapshot(19, 0) && _content.DeviceCount == 1;
        _content.ApplySnapshot(21, 0);
        bool removalImmediate = _content.Route == "list";
        _content.ApplySnapshot(22, 8);
        _content.ShowPairing();
        _content.SetMotionEnabled(false);
        bool reducedMotionSettled = !_content.IsAnimating && !_content.IsProgressActive;
        _content.ShowList(false);
        _content.SetMotionEnabled(_settings.AnimationsEnabled);
        await Task.Delay(200).ConfigureAwait(true);
        bool stableNativeSize = AppWindow.ClientSize.Width == initialSize.Width
            && AppWindow.ClientSize.Height == initialSize.Height;
        bool placementPassed = ProbePlacement.Verify();
        object? nativeResult = _native is null ? null : await _native.VerifyAsync().ConfigureAwait(true);
        object? workloadResult = null;
        bool workloadPassed = true;
        if (Environment.GetCommandLineArgs().Contains("--workload", StringComparer.Ordinal)) {
            _native?.SetSyntheticPinned(true);
            _native?.ShowFlyout();
            await File.WriteAllTextAsync(resultPath + ".workload-ready.json", JsonSerializer.Serialize(new {
                ProcessId = Environment.ProcessId,
                Frequency = System.Diagnostics.Stopwatch.Frequency,
            })).ConfigureAwait(true);
            var driverStart = Environment.GetCommandLineArgs()
                .FirstOrDefault(value => value.StartsWith("--driver-start=", StringComparison.Ordinal));
            if (driverStart is not null) {
                var armWait = System.Diagnostics.Stopwatch.StartNew();
                while (!File.Exists(driverStart["--driver-start=".Length..])) {
                    if (armWait.Elapsed > TimeSpan.FromSeconds(60)) {
                        throw new TimeoutException("The external workload driver did not release its start signal.");
                    }
                    await Task.Delay(50).ConfigureAwait(true);
                }
            }
            else {
                await Task.Delay(2000).ConfigureAwait(true);
            }
            workloadResult = await ProbeWorkload.RunAsync(_content, DispatcherQueue).ConfigureAwait(true);
            workloadPassed = _content.Revision == 1022;
        }
        bool testSurface = Environment.GetCommandLineArgs().Contains("--surface", StringComparer.Ordinal);
        if (testSurface) {
            _motion = new SurfaceMotionProbe(Root);
            _motion.Settle(false);
            foreach (int reverseAfter in new[] { 42, 84, 150 }) {
                _motion.SetShown(true);
                await Task.Delay(reverseAfter).ConfigureAwait(true);
                _motion.SetShown(false);
                await Task.Delay(reverseAfter).ConfigureAwait(true);
                _motion.SetShown(true);
                await Task.Delay(220).ConfigureAwait(true);
            }
        }
        var holdArgument = Environment.GetCommandLineArgs()
            .FirstOrDefault(value => value.StartsWith("--hold-ms=", StringComparison.Ordinal));
        if (holdArgument is not null) {
            int holdMilliseconds = int.Parse(holdArgument["--hold-ms=".Length..], CultureInfo.InvariantCulture);
            if (holdMilliseconds is < 0 or > 60000) {
                throw new FormatException("The prototype hold must be between zero and 60000 milliseconds.");
            }
            await File.WriteAllTextAsync(resultPath + ".ready.json", JsonSerializer.Serialize(new {
                ProcessId = Environment.ProcessId,
                Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this).ToInt64(),
                X = AppWindow.Position.X,
                Y = AppWindow.Position.Y,
                Width = AppWindow.Size.Width,
                Height = AppWindow.Size.Height,
                Scale = Root.XamlRoot.RasterizationScale,
                BubbleHwnd = _native?.BubbleHandle,
                TrayCallback = NativeTrayProbe.CallbackMessage,
                TestMessage = NativeLifetimeProbe.TestMessage,
            })).ConfigureAwait(true);
            await Task.Delay(holdMilliseconds).ConfigureAwait(true);
        }
        bool forceFailure = Environment.GetCommandLineArgs().Contains("--force-failure", StringComparer.Ordinal);
        bool passed = Root.ActualWidth == 376 && Root.ActualHeight == 480
            && (!useAcrylic || boundedBackdrop?.SystemBackdrop is not null)
            && (!testSurface || _motion?.IsActive == false)
            && olderRejected && removalImmediate && reducedMotionSettled && stableNativeSize
            && placementPassed
            && (_native is null || _native.Passed)
            && workloadPassed
            && !AppWindow.IsShownInSwitchers && !forceFailure;
        var result = new {
            Passed = passed,
            Stage = "Stable API and XAML startup probe",
            ContentSource = "Compiled XAML",
            Width = Root.ActualWidth,
            Height = Root.ActualHeight,
            Variant = useAcrylic ? "Bounded Acrylic" : "Opaque baseline",
            BackdropType = boundedBackdrop?.SystemBackdrop?.GetType().FullName,
            ForcedFailure = forceFailure,
            TransparentEnvelope = testSurface,
            MotionGeneration = _motion?.Generation,
            CompletedGeneration = _motion?.CompletedGeneration,
            OlderRevisionRejected = olderRejected,
            RemovedDetailsCleared = removalImmediate,
            ReducedMotionSettled = reducedMotionSettled,
            NativeSizeUnchanged = stableNativeSize,
            SyntheticPlacementMatrixPassed = placementPassed,
            Native = nativeResult,
            Workload = workloadResult,
            AnimationsEnabled = _settings.AnimationsEnabled,
            VisibleAtCompletion = AppWindow.IsVisible,
            BubbleMouseActivationCount = _native?.BubbleMouseActivationCount,
            BubbleActivationCount = _native?.BubbleActivationCount,
            BubbleForegroundActivationCount = _native?.BubbleForegroundActivationCount,
            DismissalCount = _native?.DismissalCount,
            ShowCount = _native?.ShowCount,
            BubbleNoActivateStyle = _native?.BubbleNoActivateStyle,
        };
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result)).ConfigureAwait(true);
        Environment.ExitCode = passed ? 0 : 1;
        _native?.Dispose();
        Close();
        Application.Current.Exit();
    }
}
