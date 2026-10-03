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

        SizeToContent = SizeToContent.Manual;
        Width = BubbleSizeDip;
        Height = BubbleSizeDip;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += OnSourceInitialized;
    }

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

        if (Capsule is null) {
            // R-08: never let a presentation fault take the process down.
            Log.Warning("StatusBubble: capsule element missing; leaving the bubble hidden");
            Hide();
            return;
        }

        Capsule.Background = ResolveBrush(state.Color);

        // Show without activating: never Activate/Focus/SetForegroundWindow on this window.
        Show();
    }

    private static SolidColorBrush ResolveBrush(BubbleColor color) {
        var key = StatusBubblePolicy.TokenKey(color);
        if (Application.Current?.Resources[key] is SolidColorBrush brush) {
            return brush;
        }

        return Brushes.Transparent;
    }

    /// <summary>
    /// Pins exact physical bounds without activation. Used AFTER Show, because the HWND does not
    /// exist before then — and WPF's own sizing proved unreliable for this frame recipe (it laid
    /// the window out far larger than its content).
    /// </summary>
    internal void SetPhysicalBounds(Rectangle physicalBounds) {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) {
            return;
        }

        var ok = PInvoke.SetWindowPos(
            new HWND(handle),
            HWND.Null,
            physicalBounds.X,
            physicalBounds.Y,
            physicalBounds.Width,
            physicalBounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        if (!ok) {
            Log.Warning("StatusBubble: SetPhysicalBounds failed for {Bounds}", physicalBounds);
        }
    }

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
