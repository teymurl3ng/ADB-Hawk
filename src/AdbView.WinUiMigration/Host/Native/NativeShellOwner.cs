namespace AdbView.WinUiMigration.Host.Native;

using AdbView.NativeUi;
using AdbView.State;
using AdbView.WinUiMigration.NativeUi;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

// Currently composed only by explicitly isolated native fixtures. Production lifetime stays WPF.
internal sealed class NativeShellOwner : IDisposable {
    private readonly Window _flyout;
    private readonly FrameworkElement _root;
    private readonly SnapshotPresenter _content;
    private readonly PanelWindowController _bounds;
    private readonly Action _show;
    private readonly Action _hide;
    private readonly Func<AppSnapshot?> _snapshot;
    private readonly StatusBubblePresenter _bubblePolicy;
    private readonly BubbleInputGuard _bubbleInput;
    private readonly WindowMessageHook _flyoutHook;
    private readonly InputNonClientPointerSource _pointer;
    private readonly KeyboardAccelerator _escape = new() { Key = VirtualKey.Escape };
    private bool _disposed;
    private bool _environmentPending;
    private bool _changingVisibility;
    private bool _fullScreen;
    private bool _suspended;
    public NativeReceiver Receiver { get; }
    public NativeTray Tray { get; }
    public BubbleWindow Bubble { get; }
    public int EnvironmentRecomputations { get; private set; }
    public int CaptionRegionCount { get; private set; }
    public bool Disposed => _disposed;
    public int BubbleForegroundActivations => _bubbleInput.ForegroundActivations;
    public NativeShellOwner(Window flyout, FrameworkElement root, SnapshotPresenter content, PanelWindowController bounds,
        Func<AppSnapshot?> snapshot, Action show, Action hide, Guid identity,
        Func<DateTimeOffset> clock, Func<TimeSpan, CancellationToken, Task> delay) {
        _flyout = flyout; _root = root; _content = content; _bounds = bounds; _snapshot = snapshot; _show = show; _hide = hide;
        _bubblePolicy = new StatusBubblePresenter(clock, delay);
        Receiver = new NativeReceiver();
        Bubble = new BubbleWindow();
        _bubbleInput = new BubbleInputGuard(Bubble);
        _bounds.PlaceBubble(Bubble);
        Tray = new NativeTray(Receiver, identity, Toggle, Open, flyout.Close);
        _pointer = InputNonClientPointerSource.GetForWindowId(flyout.AppWindow.Id);
        if (flyout.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) { presenter.IsAlwaysOnTop = true; }
        _flyoutHook = new WindowMessageHook(new HWND(WinRT.Interop.WindowNative.GetWindowHandle(flyout)), HandleEnvironment);
        Receiver.Message += HandleEnvironment;
        _bubblePolicy.Changed += OnBubbleChanged;
        Tray.PreferencesChanged += RefreshBubble; Tray.BalloonClicked += OpenNotification;
        Bubble.OpenRequested += Open;
        _escape.Invoked += OnEscape; _root.KeyboardAccelerators.Add(_escape);
        _flyout.Activated += OnActivated;
    }
    public void Apply(AppSnapshot snapshot) {
        if (_disposed) { return; }
        Tray.Apply(snapshot); _bubblePolicy.Accept(snapshot.Health.Tray, snapshot.Health.TrayTooltip);
        UpdateCaption(); RefreshBubble();
    }
    public void VisibilityChanged() { if (!_disposed) { RefreshBubble(); } }
    public void SetFullScreenSuppressed(bool suppressed) { _fullScreen = suppressed; RefreshBubble(); }
    public void SetSuspended(bool suspended) { _suspended = suspended; RefreshBubble(); }
    private void OnBubbleChanged(BubbleState? state) {
        // Read policy.Current at dispatch time; queued expirations never replace newer truth.
        if (_disposed) { return; }
        if (_root.DispatcherQueue.HasThreadAccess) { RefreshBubble(); }
        else if (!_root.DispatcherQueue.TryEnqueue(RefreshBubble) && !_disposed) { Log.Warning("Bubble: UI dispatcher rejected lifetime update"); }
    }
    public void RefreshBubble() {
        if (_disposed) { return; }
        BubbleState? state = _bubblePolicy.Current;
        if (!BubbleVisibility.ShouldShow(Tray.BubbleEnabled, _flyout.AppWindow.IsVisible, _fullScreen, _suspended, state is not null)
            || state is null || _snapshot() is not { } snapshot) { Bubble.AppWindow.Hide(); return; }
        Bubble.Apply(state, snapshot.Shell); Bubble.AppWindow.Show(false);
    }
    public void Open() {
        if (_disposed) { return; }
        _changingVisibility = true;
        try {
            Bubble.AppWindow.Hide(); _show(); _flyout.Activate();
            var handle = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(_flyout));
            if (PInvoke.GetForegroundWindow() != handle && !PInvoke.SetForegroundWindow(handle)) {
                Log.Debug("Native flyout: Windows declined the user-open foreground request");
            }
        }
        finally { _changingVisibility = false; }
    }
    public void Toggle() { if (_flyout.AppWindow.IsVisible) { Hide(); } else { Open(); } }
    public void Hide() {
        if (_disposed) { return; }
        _changingVisibility = true;
        try { _hide(); RefreshBubble(); }
        finally { _changingVisibility = false; }
    }
    private void OpenNotification() { _content.OpenFromNotification(); Open(); }
    public void Escape() {
        var popup = VisualTreeHelperPopups();
        if (popup is not null) { popup.IsOpen = false; }
        else if (_content.DetailsId is not null || _content.Pairing is not null) { _content.Back(); }
        else { Hide(); }
    }
    private Popup? VisualTreeHelperPopups() {
        if (_root.XamlRoot is not { } xamlRoot) { return null; }
        var popups = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
        return popups.Count == 0 ? null : popups[popups.Count - 1];
    }
    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Escape(); args.Handled = true; }
    private void OnActivated(object sender, WindowActivatedEventArgs args) {
        if (_disposed || _changingVisibility || args.WindowActivationState != WindowActivationState.Deactivated
            || !_flyout.AppWindow.IsVisible || _snapshot()?.Shell.Pinned == true || Tray.MenuIsOpen || Tray.IsPointerOverIcon()) { return; }
        var handle = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(_flyout));
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground != handle && PInvoke.GetWindow(foreground, GET_WINDOW_CMD.GW_OWNER) != handle
            && VisualTreeHelperPopups() is null) { Hide(); }
    }
    private void UpdateCaption() {
        if (_snapshot()?.Shell.Pinned != true) { _pointer.ClearRegionRects(NonClientRegionKind.Caption); CaptionRegionCount = 0; return; }
        double width = Math.Max(0, Math.Min(160, _root.ActualWidth - 180));
        if (width == 0) { _pointer.ClearRegionRects(NonClientRegionKind.Caption); CaptionRegionCount = 0; return; }
        double scale = PInvoke.GetDpiForWindow(new HWND(WinRT.Interop.WindowNative.GetWindowHandle(_flyout))) / 96.0;
        _pointer.SetRegionRects(NonClientRegionKind.Caption, [new RectInt32((int)Math.Round(100 * scale), (int)Math.Round(16 * scale),
            (int)Math.Round(width * scale), (int)Math.Round(48 * scale))]); CaptionRegionCount = 1;
    }
    private LRESULT? HandleEnvironment(uint message, WPARAM wParam, LPARAM lParam) {
        if (_disposed || message is not (PInvoke.WM_DPICHANGED or PInvoke.WM_DISPLAYCHANGE or PInvoke.WM_SETTINGCHANGE)) { return null; }
        if (!_environmentPending) {
            _environmentPending = true;
            if (!_root.DispatcherQueue.TryEnqueue(() => {
                _environmentPending = false;
                if (_disposed) { return; }
                _content.ContentMotion.Settle(); _bounds.PlaceBeforeShow(); _bounds.PlaceBubble(Bubble);
                UpdateCaption(); EnvironmentRecomputations++;
            })) { _environmentPending = false; throw new InvalidOperationException("Environment: UI dispatcher rejected bounds update."); }
        }
        return null;
    }
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true; _flyout.Activated -= OnActivated;
        _escape.Invoked -= OnEscape; _root.KeyboardAccelerators.Remove(_escape);
        _pointer.ClearRegionRects(NonClientRegionKind.Caption); CaptionRegionCount = 0;
        Receiver.Message -= HandleEnvironment; _flyoutHook.Dispose();
        Tray.PreferencesChanged -= RefreshBubble; Tray.BalloonClicked -= OpenNotification;
        Bubble.OpenRequested -= Open; _bubblePolicy.Changed -= OnBubbleChanged;
        _bubblePolicy.Dispose(); Tray.Dispose(); _bubbleInput.Dispose(); Bubble.Dispose(); Receiver.Dispose();
    }
}
