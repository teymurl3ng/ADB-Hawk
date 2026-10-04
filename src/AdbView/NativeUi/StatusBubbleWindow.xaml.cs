namespace AdbView.NativeUi;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AdbView.State;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

#pragma warning disable CA1515 // XAML x:Class requires a public partial class.
/// <summary>
/// The status bubble (decision 0020): a very small, always-on-top, NON-ACTIVATING square in the
/// bottom-right of the work area, filled with the aggregate status colour. It deliberately does
/// the opposite of <see cref="NativeFlyoutWindow"/> on activation — the flyout calls
/// SetForegroundWindow and dismisses on deactivate; the bubble must never take focus.
///
/// Styles and the frame recipe mirror the flyout (which is proven on this machine): keep
/// WS_CAPTION for DWM, collapse the non-client frame in WM_NCCALCSIZE, and round with DWM
/// attribute 33 rather than AllowsTransparency. Added here: WS_EX_NOACTIVATE plus
/// WM_MOUSEACTIVATE → MA_NOACTIVATE, because the style alone is not airtight — mouse-hover
/// activation bypasses it (research/2026-10-03-status-bubble-native-approaches.md).
/// </summary>
public sealed partial class StatusBubbleWindow : Window {
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_DPICHANGED = 0x02E0;
    private const int MA_NOACTIVATE = 3;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    /// <summary>Design size of the bubble capsule in DIP (hover expansion grows it in N3).</summary>
    internal const double BubbleSizeDip = 22;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUNDSMALL = 3;

    private HwndSource? _source;
    private ThemeKind _theme = ThemeKind.Dark;
    private BubbleState? _state;

    public StatusBubbleWindow() {
        // Required: without this the named elements (Capsule) are null and Apply would crash.
        InitializeComponent();

        // WindowStyle=None (WS_POPUP): a CAPTIONED window has a shell-enforced minimum size
        // (~133x38 DIP here), which stretched the capsule into a bar. Frameless is the only way
        // to get a truly tiny window without the layered (AllowsTransparency) path.
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        // Must be set before Show(); it only governs the initial show, which is why the
        // WM_MOUSEACTIVATE handler exists as well.
        ShowActivated = false;

        // DWM rounding instead of the layered path (no AllowsTransparency).
        AllowsTransparency = false;

        // Content-sized: hovering shows the status text and the window grows leftwards (the
        // anchor math repositions it). Frameless WindowStyle=None has no shell minimum-size
        // floor, so the content governs — the captioned recipe floored at 133x38 DIP and
        // stretched the capsule (research 2026-10-03 addendum).
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnSizeChanged;

        // Hover reveals the status text; clicking opens the flyout. Both are wired here rather
        // than in XAML so the behaviour stays testable and the XAML stays dumb.
        Shell.MouseEnter += (_, _) => SetHovered(true);
        Shell.MouseLeave += (_, _) => SetHovered(false);
        Shell.MouseLeftButtonUp += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Raised when the user clicks the bubble (open the flyout).</summary>
    internal event Action? OpenRequested;

    /// <summary>Applied theme (dark-mode DWM attribute follows it).</summary>
    internal ThemeKind AppliedTheme => _theme;

    /// <summary>What the bubble is currently showing; null while hidden.</summary>
    internal BubbleState? State => _state;

    /// <summary>
    /// Scale used by the placement math. Before the handle exists the HwndSource is null, so fall
    /// back to the system DPI from a screen DC — the same correction the flyout needed, because
    /// 1.0 here would place the bubble using physical numbers as DIP on a scaled display.
    /// </summary>
    internal double DpiScale {
        get {
            var source = _source;
            if (source is not null) {
                return source.CompositionTarget!.TransformToDevice.M11;
            }

            using var screen = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return screen.DpiX / 96.0;
        }
    }

    internal IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    internal void ApplyTheme(ThemeKind theme) {
        _theme = theme;
        if (_source is not null) {
            ApplyDwmAttributes(_source.Handle);
        }
    }

    /// <summary>
    /// Shows the bubble for <paramref name="state"/>, or hides it when null. Colour comes from
    /// the existing token families (never a new palette); the state's own text is used by the
    /// hover expansion in phase N3.
    /// </summary>
    internal void Apply(BubbleState? state) {
        _state = state;

        if (state is null) {
            Hide();
            return;
        }

        if (Dot is null) {
            // R-08: never let a presentation fault take the process down.
            Log.Warning("StatusBubble: capsule element missing; leaving the bubble hidden");
            Hide();
            return;
        }

        _detail = state.Detail;
        Dot.Background = ResolveBrush(state.Color);
        if (_hovered) {
            Detail.Text = _detail;
            Detail.Visibility = Visibility.Visible;
        }

        // Show without activating: never Activate/Focus/SetForegroundWindow on this window.
        Show();

        // Decision 0021: appear fade (Bare Minimum 83 ms per Motion in Windows) on the CONTENT -
        // a window-level fade needs the layered path (banned here), content opacity is free.
        Shell.Opacity = 0;
        Shell.BeginAnimation(
            System.Windows.UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation {
                From = 0,
                To = 1,
                Duration = (System.Windows.Duration)Application.Current.Resources["Motion.Feedback"],
            });
    }

    private static SolidColorBrush ResolveBrush(BubbleColor color) {
        var key = StatusBubblePolicy.TokenKey(color);
        if (Application.Current?.Resources[key] is SolidColorBrush brush) {
            return brush;
        }

        return Brushes.Transparent;
    }

    private double _anchorRightDip;
    private double _anchorBottomDip;
    private bool _hovered;

    /// <summary>Distance kept from the work area's right/bottom edges, in DIP.</summary>
    internal const double AnchorMarginDip = 12;

    /// <summary>
    /// Records the bottom-right corner (in DIP) the bubble hangs from. The window positions
    /// itself from this, so growing on hover extends leftwards and the corner never moves.
    /// </summary>
    internal void SetAnchor(double rightDip, double bottomDip) {
        _anchorRightDip = rightDip;
        _anchorBottomDip = bottomDip;
        Reanchor();
    }

    /// <summary>Its own desired size in DIP — what the Host needs to know for logging/verification.</summary>
    internal Size DesiredSizeDip => new(ActualWidth, ActualHeight);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) {
        Reanchor();
    }

    private void Reanchor() {
        if (_anchorRightDip <= 0 || _anchorBottomDip <= 0) {
            return;
        }

        double width = ActualWidth > 0 ? ActualWidth : BubbleSizeDip;
        double height = ActualHeight > 0 ? ActualHeight : BubbleSizeDip;
        Left = _anchorRightDip - width - AnchorMarginDip;
        Top = _anchorBottomDip - height - AnchorMarginDip;
    }

    /// <summary>Shows or hides the status text; the window then grows or shrinks to fit.</summary>
    internal void SetHovered(bool hovered) {
        Log.Information(
            "StatusBubble: SetHovered({Hovered}) detail-len={Len} current={W}x{H}",
            hovered,
            _detail.Length,
            Math.Round(ActualWidth),
            Math.Round(ActualHeight));
        if (_hovered == hovered) {
            return;
        }

        _hovered = hovered;
        if (hovered && !string.IsNullOrEmpty(_detail)) {
            Detail.Text = _detail;
            Detail.Visibility = Visibility.Visible;
        }
        else {
            Detail.Visibility = Visibility.Collapsed;
        }

        // SizeToContent grows/shrinks the window; OnSizeChanged re-anchors the corner.
    }

    private string _detail = string.Empty;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int newStyle);

    private void OnSourceInitialized(object? sender, EventArgs e) {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        IntPtr hwnd = _source.Handle;

        // WS_EX_TOOLWINDOW: no taskbar entry, no Alt+Tab. WS_EX_NOACTIVATE: clicks and programmatic
        // shows do not make it foreground.
        int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
        _ = SetWindowLongW(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        // SetWindowLong requires this to take effect.
        _ = PInvoke.SetWindowPos(
            new HWND(hwnd),
            HWND.Null,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED
                | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                | SET_WINDOW_POS_FLAGS.SWP_NOZORDER
                | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        _source.AddHook(WndProc);
        ApplyDwmAttributes(hwnd);
    }

    private unsafe void ApplyDwmAttributes(IntPtr hwnd) {
        try {
            var window = new HWND(hwnd);
            int useDarkMode = _theme == ThemeKind.Dark ? 1 : 0;
            _ = PInvoke.DwmSetWindowAttribute(
                window,
                (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)DWMWA_USE_IMMERSIVE_DARK_MODE,
                &useDarkMode,
                sizeof(int));

            var corner = DWMWCP_ROUNDSMALL;
            _ = PInvoke.DwmSetWindowAttribute(
                window,
                (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)DWMWA_WINDOW_CORNER_PREFERENCE,
                &corner,
                sizeof(int));
        }
#pragma warning disable CA1031 // R-08: a DWM failure degrades to square corners; documented.
        catch (Exception ex) {
            Log.Warning(ex, "StatusBubble: DWM attribute application failed; square corners remain");
        }
#pragma warning restore CA1031
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        switch (msg) {
            case WM_NCCALCSIZE:
                if (wParam != IntPtr.Zero) {
                    handled = true;
                    return IntPtr.Zero; // collapse NC: WS_CAPTION kept for DWM, never drawn
                }

                break;

            case WM_NCACTIVATE:
                // Never draw an active/inactive frame transition.
                handled = true;
                return new IntPtr(1);

            case WM_MOUSEACTIVATE:
                // Belt and braces for WS_EX_NOACTIVATE: decline activation but KEEP the click
                // (MA_NOACTIVATE, not MA_NOACTIVATEANDEAT).
                handled = true;
                return new IntPtr(MA_NOACTIVATE);

            case WM_DPICHANGED:
                // Adopt the suggested rect; the caller re-places on display changes too.
                var suggested = Marshal.PtrToStructure<RECT>(lParam);
                _ = PInvoke.SetWindowPos(
                    new HWND(hwnd),
                    HWND.Null,
                    suggested.left,
                    suggested.top,
                    suggested.right - suggested.left,
                    suggested.bottom - suggested.top,
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }
}
