namespace WinUiShellProbe;

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

// Nonshipping, synthetic window/input feasibility. It does not own product state.
internal sealed class NativeLifetimeProbe : IDisposable {
    internal const uint TestMessage = 0x8000 + 38;
    private readonly ProbeWindow _flyout;
    private readonly FrameworkElement _root;
    private readonly ToggleButton _pin;
    private readonly ProbeContentController _content;
    private readonly HWND _flyoutHandle;
    private readonly Window _bubble;
    private readonly HWND _bubbleHandle;
    private readonly NativeWindowHook _flyoutHook;
    private readonly NativeWindowHook _bubbleHook;
    private readonly NativeWindowHook? _bubbleChildHook;
    private readonly HOOKPROC _bubbleActivationProcedure;
    private readonly HHOOK _bubbleActivationHook;
    private readonly NativeTrayProbe _tray;
    private readonly InputNonClientPointerSource _pointerSource;
    private readonly KeyboardAccelerator _escape = new() { Key = VirtualKey.Escape };
    private readonly Action _environmentChanged;
    private bool _disposed;
    private bool _changingVisibility;

    public NativeLifetimeProbe(ProbeWindow flyout, FrameworkElement root, ToggleButton pin, ProbeContentController content, Action environmentChanged) {
        _flyout = flyout;
        _root = root;
        _pin = pin;
        _content = content;
        _environmentChanged = environmentChanged;
        _flyoutHandle = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(flyout));
        _pointerSource = InputNonClientPointerSource.GetForWindowId(flyout.AppWindow.Id);
        if (flyout.AppWindow.Presenter is OverlappedPresenter flyoutPresenter) {
            flyoutPresenter.IsAlwaysOnTop = true;
        }
        _bubble = new Window { Title = "ADB View — isolated bubble probe" };
        _bubbleHandle = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(_bubble));
        _bubble.AppWindow.IsShownInSwitchers = false;
        if (_bubble.AppWindow.Presenter is OverlappedPresenter presenter) {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        var bubbleButton = new Button {
            Content = "●",
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            IsTabStop = false,
            AllowFocusOnInteraction = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bubbleButton, "Open ADB View probe");
        ToolTipService.SetToolTip(bubbleButton, "ADB View — synthetic connected fixture");
        bubbleButton.Click += (_, _) => {
            ProbeDiagnostics.Stage($"Bubble action entry; foreground {(nint)PInvoke.GetForegroundWindow():X}.");
            ShowFlyout();
        };
        _bubble.Content = bubbleButton;
        nint styles = PInvoke.GetWindowLongPtr(_bubbleHandle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetLastError(0);
        nint previous = PInvoke.SetWindowLongPtr(_bubbleHandle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            styles | (nint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE | (nint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) {
            throw new InvalidOperationException("Probe bubble nonactivation style failed.");
        }
        if (!PInvoke.SetWindowPos(_bubbleHandle, default, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE)) {
            throw new InvalidOperationException("Probe bubble style refresh failed.");
        }
        double scale = PInvoke.GetDpiForWindow(_flyoutHandle) / 96.0;
        var bubbleSize = new SizeInt32((int)Math.Round(32 * scale), (int)Math.Round(32 * scale));
        _bubble.AppWindow.ResizeClient(bubbleSize);
        var area = DisplayArea.GetFromWindowId(flyout.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        _bubble.AppWindow.Move(new PointInt32(area.X + area.Width - _bubble.AppWindow.Size.Width - (int)Math.Round(8 * scale),
            area.Y + area.Height - _bubble.AppWindow.Size.Height - (int)Math.Round(8 * scale)));
        _bubbleHook = new NativeWindowHook(_bubbleHandle, HandleBubbleMessage);
        _bubbleActivationProcedure = FilterActivation;
        _bubbleActivationHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_CBT,
            _bubbleActivationProcedure, new HINSTANCE(0), PInvoke.GetCurrentThreadId());
        if (_bubbleActivationHook.IsNull) {
            throw new InvalidOperationException("Native bubble: own-thread activation guard registration failed.");
        }
        HWND bubbleChild = PInvoke.GetWindow(_bubbleHandle, GET_WINDOW_CMD.GW_CHILD);
        if (!bubbleChild.IsNull) {
            _bubbleChildHook = new NativeWindowHook(bubbleChild, (message, wParam, lParam) =>
                message == PInvoke.WM_MOUSEACTIVATE ? HandleBubbleMessage(message, wParam, lParam) : null);
            ProbeDiagnostics.Stage("Bubble child input HWND subclassed.");
        }
        _flyoutHook = new NativeWindowHook(_flyoutHandle, HandleFlyoutMessage);
        _tray = new NativeTrayProbe(_flyoutHandle, ToggleFlyout, ShowFlyout, () => _flyout.Close());
        _flyout.Activated += OnActivated;
        _escape.Invoked += OnEscapeInvoked;
        _root.KeyboardAccelerators.Add(_escape);
        _pin.Visibility = Visibility.Visible;
        _pin.Checked += OnPinChanged;
        _pin.Unchecked += OnPinChanged;
        ProbeDiagnostics.Stage("Native tray and nonactivating bubble initialized");
    }

    public bool IsPinned => _pin.IsChecked == true;
    public bool FlyoutVisible => _flyout.AppWindow.IsVisible;
    public bool BubbleVisible => _bubble.AppWindow.IsVisible;
    public long BubbleHandle => (long)(nint)_bubbleHandle;
    public bool BubbleNoActivateStyle => (PInvoke.GetWindowLongPtr(_bubbleHandle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
        & (nint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE) != 0;
    public int BubbleMouseActivationCount { get; private set; }
    public int BubbleActivationCount { get; private set; }
    public int BubbleForegroundActivationCount { get; private set; }
    public int DismissalCount { get; private set; }
    public int ShowCount { get; private set; }
    public int EnvironmentMessageCount { get; private set; }
    public NativeTrayProbe Tray => _tray;

    public void SetSyntheticPinned(bool pinned) => _pin.IsChecked = pinned;

    public void ShowStartupBubble() {
        _content.SetPresentationVisible(false);
        _bubble.AppWindow.Show(false);
    }

    public void UpdateEnvironment() {
        double scale = PInvoke.GetDpiForWindow(_flyoutHandle) / 96.0;
        var area = DisplayArea.GetFromWindowId(_flyout.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var client = new SizeInt32((int)Math.Round(32 * scale), (int)Math.Round(32 * scale));
        if (_bubble.AppWindow.ClientSize.Width != client.Width || _bubble.AppWindow.ClientSize.Height != client.Height) {
            _bubble.AppWindow.ResizeClient(client);
        }
        _bubble.AppWindow.Move(ProbePlacement.Position(area, _bubble.AppWindow.Size, scale));
        OnPinChanged(_pin, new RoutedEventArgs());
    }

    public void ShowFlyout() {
        ProbeDiagnostics.Stage($"Showing flyout; foreground {(nint)PInvoke.GetForegroundWindow():X}; flyout {(nint)_flyoutHandle:X}; bubble {(nint)_bubbleHandle:X}.");
        _changingVisibility = true;
        try {
            _bubble.AppWindow.Hide();
            _content.SetPresentationVisible(true);
            _flyout.Activate();
            if (PInvoke.GetForegroundWindow() != _flyoutHandle) {
                bool accepted = PInvoke.SetForegroundWindow(_flyoutHandle);
                ProbeDiagnostics.Stage($"User-open foreground request accepted {accepted}; foreground {(nint)PInvoke.GetForegroundWindow():X}; flyout {(nint)_flyoutHandle:X}.");
                if (!accepted) {
                    ProbeDiagnostics.Stage("Native window: Windows declined the user-open foreground request; no forced activation or retry.");
                }
            }
            _pin.Focus(FocusState.Programmatic);
            ShowCount++;
        }
        finally {
            _changingVisibility = false;
        }
    }

    public void HideFlyout() {
        if (!FlyoutVisible) {
            return;
        }
        _changingVisibility = true;
        ProbeDiagnostics.Stage("Hiding flyout and showing bubble without activation.");
        try {
            _content.SetPresentationVisible(false);
            _bubble.AppWindow.Show(false);
            _flyout.AppWindow.Hide();
            DismissalCount++;
        }
        finally {
            _changingVisibility = false;
        }
    }

    public void ToggleFlyout() {
        if (FlyoutVisible) {
            HideFlyout();
        }
        else {
            ShowFlyout();
        }
    }

    public async Task<object> VerifyAsync() {
        // Test-driver waits exercise native visibility and callback lifetime; no animation loop.
        _pin.IsChecked = true;
        _content.ShowPairing();
        HideFlyout();
        await Task.Delay(80).ConfigureAwait(true);
        bool hiddenSettles = !_content.IsProgressActive && !_content.IsAnimating && !FlyoutVisible;
        bool showDidNotActivateBubble = PInvoke.GetForegroundWindow() != _bubbleHandle;
        bool pinnedRetained = IsPinned;
        bool bubbleSize = _bubble.AppWindow.ClientSize.Width == (int)Math.Round(32 * _root.XamlRoot.RasterizationScale)
            && _bubble.AppWindow.ClientSize.Height == (int)Math.Round(32 * _root.XamlRoot.RasterizationScale);
        _tray.RemoveForRecreationTest();
        PInvoke.SendMessage(_flyoutHandle, _tray.TaskbarCreatedMessage, default, default);
        bool recreation = _tray.AddCount == 2;
        PInvoke.SendMessage(_flyoutHandle, NativeTrayProbe.CallbackMessage, default,
            new LPARAM((1 << 16) | (int)NativeTrayProbe.KeyboardSelect));
        bool keyboardTrayOpened = FlyoutVisible && !BubbleVisible && _tray.SelectionCount == 1;
        PInvoke.SendMessage(_flyoutHandle, NativeTrayProbe.CallbackMessage, default,
            new LPARAM((1 << 16) | (int)PInvoke.WM_LBUTTONUP));
        bool noDuplicateToggle = FlyoutVisible && _tray.SelectionCount == 1;
        PInvoke.SendMessage(_flyoutHandle, PInvoke.WM_DISPLAYCHANGE, default, default);
        await Task.Delay(40).ConfigureAwait(true);
        bool environmentDelivered = EnvironmentMessageCount > 0;
        _pin.IsChecked = false;
        _content.ShowList(false);
        await Task.Delay(50).ConfigureAwait(true);
        Passed = hiddenSettles && showDidNotActivateBubble && pinnedRetained && bubbleSize
            && recreation && keyboardTrayOpened && noDuplicateToggle && environmentDelivered && !_bubble.AppWindow.IsShownInSwitchers;
        return new {
            Passed,
            HiddenMotionSettled = hiddenSettles,
            NonactivatingShow = showDidNotActivateBubble,
            PinRetainedOnHide = pinnedRetained,
            BubbleSize32Dip = bubbleSize,
            HiddenReceiverRecreation = recreation,
            KeyboardTrayOpened = keyboardTrayOpened,
            NoDuplicateButtonUpToggle = noDuplicateToggle,
            DisplayMessageDelivered = environmentDelivered,
            Meaning = "Synthetic messages exercise handlers; real input and Explorer recreation remain separate checks.",
        };
    }

    public bool Passed { get; private set; }

    public void Dispose() {
        if (!_disposed) {
            _disposed = true;
            _flyout.Activated -= OnActivated;
            _escape.Invoked -= OnEscapeInvoked;
            _root.KeyboardAccelerators.Remove(_escape);
            _pin.Checked -= OnPinChanged;
            _pin.Unchecked -= OnPinChanged;
            _pointerSource.ClearRegionRects(NonClientRegionKind.Caption);
            _tray.Dispose();
            if (!PInvoke.UnhookWindowsHookEx(_bubbleActivationHook)) {
                ProbeDiagnostics.Stage("Native bubble: own-thread activation guard removal failed.");
            }
            _flyoutHook.Dispose();
            _bubbleHook.Dispose();
            _bubbleChildHook?.Dispose();
            _bubble.Close();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) {
        if (args.WindowActivationState != WindowActivationState.Deactivated) {
            bool focused = _pin.Focus(FocusState.Programmatic);
            ProbeDiagnostics.Stage($"Flyout active; pin focus accepted {focused}; focus HWND {(nint)PInvoke.GetFocus():X}.");
            if (FocusManager.GetFocusedElement(_root.XamlRoot) is FrameworkElement focusedElement) {
                ProbeDiagnostics.Stage($"XAML focus element {focusedElement.Name} ({focusedElement.GetType().Name}).");
            }
        }
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_changingVisibility
            && !IsPinned && !_tray.MenuIsOpen && FlyoutVisible && !_tray.IsPointerOverIcon()) {
            HWND foreground = PInvoke.GetForegroundWindow();
            if (foreground != _flyoutHandle && PInvoke.GetWindow(foreground, GET_WINDOW_CMD.GW_OWNER) != _flyoutHandle) {
                HideFlyout();
            }
        }
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) {
        ProbeDiagnostics.Stage($"Escape accelerator received on {_content.Route}; pin {IsPinned}.");
        if (_content.Route != "list") {
            _content.ShowList();
        }
        else {
            HideFlyout();
        }
        args.Handled = true;
    }

    private void OnPinChanged(object sender, RoutedEventArgs args) {
        if (IsPinned) {
            double scale = _root.XamlRoot.RasterizationScale;
            _pointerSource.SetRegionRects(NonClientRegionKind.Caption, [new RectInt32(
                (int)Math.Round(88 * scale), (int)Math.Round(16 * scale),
                (int)Math.Round(192 * scale), (int)Math.Round(48 * scale))]);
        }
        else {
            _pointerSource.ClearRegionRects(NonClientRegionKind.Caption);
        }
    }

    private LRESULT? HandleBubbleMessage(uint message, WPARAM wParam, LPARAM lParam) {
        if (message == PInvoke.WM_MOUSEACTIVATE) {
            BubbleMouseActivationCount++;
            return new LRESULT((int)PInvoke.MA_NOACTIVATE);
        }
        if (message == PInvoke.WM_ACTIVATE && (wParam.Value & 0xffff) != 0) {
            BubbleActivationCount++;
            HWND foreground = PInvoke.GetForegroundWindow();
            if (foreground == _bubbleHandle) {
                BubbleForegroundActivationCount++;
            }
            ProbeDiagnostics.Stage($"Bubble WM_ACTIVATE kind {wParam.Value & 0xffff}; foreground {(nint)foreground:X}; bubble {(nint)_bubbleHandle:X}; previous {lParam.Value:X}.");
        }
        return null;
    }

    private LRESULT FilterActivation(int code, WPARAM wParam, LPARAM lParam) {
        try {
            if (code == PInvoke.HCBT_ACTIVATE && wParam.Value == (nuint)(nint)_bubbleHandle) {
                ProbeDiagnostics.Stage("Native bubble: rejected activation of the nonactivating surface.");
                return new LRESULT(1);
            }
            return PInvoke.CallNextHookEx(_bubbleActivationHook, code, wParam, lParam);
        }
        catch (Exception exception) {
            ProbeDiagnostics.Fail(exception);
            Environment.Exit(1);
            throw;
        }
    }

    private LRESULT? HandleFlyoutMessage(uint message, WPARAM wParam, LPARAM lParam) {
        if (message == TestMessage) {
            switch (wParam.Value) {
                case 0:
                    return new LRESULT((FlyoutVisible ? 1 : 0) | (BubbleVisible ? 2 : 0)
                    | (IsPinned ? 4 : 0) | (_tray.MenuIsOpen ? 8 : 0) | (_content.IsProgressActive ? 16 : 0)
                    | (_content.Route == "details" ? 32 : 0) | (_content.Route == "pairing" ? 64 : 0));
                case 1: HideFlyout(); break;
                case 2: ShowFlyout(); break;
                case 3: _pin.IsChecked = true; break;
                case 4: _pin.IsChecked = false; break;
                case 5: _content.ShowDetails(0); break;
                case 6: _content.ShowPairing(); break;
                case 7: _content.ShowList(false); break;
                case 8: _content.ShowList(); break;
                case 9: return new LRESULT(BubbleForegroundActivationCount);
                case 999: throw new InvalidOperationException("Intentional native callback failure for the isolated probe.");
                default: throw new ArgumentOutOfRangeException(nameof(wParam), "Unknown isolated probe command.");
            }
            return new LRESULT(0);
        }
        var trayResult = _tray?.Handle(message, wParam, lParam);
        if (trayResult is not null) {
            return trayResult;
        }
        if (message is PInvoke.WM_DPICHANGED or PInvoke.WM_DISPLAYCHANGE or PInvoke.WM_SETTINGCHANGE) {
            EnvironmentMessageCount++;
            _content.SetPresentationVisible(FlyoutVisible);
            _environmentChanged();
        }
        return null;
    }
}
