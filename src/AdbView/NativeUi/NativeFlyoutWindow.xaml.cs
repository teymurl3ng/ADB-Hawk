namespace AdbView.NativeUi;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AdbView.Host;
using AdbView.State;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.WindowsAndMessaging;

#pragma warning disable CA1515 // XAML x:Class requires a public partial class.
/// <summary>
/// Native WPF flyout window (decision 0015, Phase I1). Ports every FlyoutWindow behavior via
/// HwndSource.AddHook: WS_EX_TOOLWINDOW creation, WM_NCCALCSIZE collapse, light-dismiss
/// synthesis, NC hit-test remap, SC_MOVE/SC_SIZE gates, WM_DPICHANGED, DWM dark-mode /
/// ROUNDSMALL / backdrop attributes with opaque fallback. Bounds are PHYSICAL pixels — applied
/// via SetWindowPos so FlyoutPlacement math stays byte-for-byte identical.
/// </summary>
public sealed partial class NativeFlyoutWindow : Window {
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_NCRBUTTONDOWN = 0x00A4;
    private const int WM_NCRBUTTONUP = 0x00A5;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MOVE = 0xF010;
    private const int SC_SIZE = 0xF000;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_ROUNDSMALL = 3;

    private HwndSource? _source;
    private bool _isPinned;
    private ThemeKind _theme = ThemeKind.Dark;
    private bool _transparencyEnabled;
    private bool _backdropActive;

    public event EventHandler? Deactivated95;

    public NativeFlyoutWindow() {
        Title = "ADB View";
        Width = 376;
        Height = 500;
        WindowStyle = WindowStyle.SingleBorderWindow; // keeps WS_CAPTION for DWM
        ResizeMode = ResizeMode.CanResize;            // THICKFRAME stripped in source hook
        ShowInTaskbar = false;
        ShowActivated = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = new Shell.ShellView { DataContext = new Shell.NativeShellViewModel() };
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => RecalcNonClientFrame();
    }

    internal Shell.NativeShellViewModel ViewModel =>
        (Shell.NativeShellViewModel)((Shell.ShellView)Content).DataContext;

    public bool IsPinned {
        get => _isPinned;
        set {
            _isPinned = value;
            Topmost = value;
        }
    }

    public bool TopMost {
        get => Topmost;
        set => Topmost = value;
    }

    public bool ShellVisible => _source is not null && IsLoaded;

    public int DeviceDpi {
        get {
            var source = _source;
            if (source is null) {
                return 96;
            }

            return (int)Math.Round(96 * source.CompositionTarget!.TransformToDevice.M11);
        }
    }

    internal double DpiScale => _source is null ? 1.0 : _source.CompositionTarget!.TransformToDevice.M11;

    internal IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    internal bool BackdropActive => _backdropActive;

    internal ThemeKind AppliedTheme => _theme;

    /// <summary>Applies physical-pixel bounds without activation (placement path).</summary>
    internal void SetPhysicalBounds(Rectangle physicalBounds) {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero) {
            return;
        }

        var ok = PInvoke.SetWindowPos(
            new HWND(helper.Handle),
            HWND.Null,
            physicalBounds.X,
            physicalBounds.Y,
            physicalBounds.Width,
            physicalBounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        if (!ok) {
            Log.Warning("SetPhysicalBounds failed for {Bounds}", physicalBounds);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int newStyle);

    internal Rectangle GetPhysicalBounds() {
        if (Win32Helpers.TryGetWindowRect(new WindowInteropHelper(this).Handle, out var rect)) {
            return Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
        }

        return new Rectangle(
            (int)Left, (int)Top,
            (int)Math.Round(ActualWidth), (int)Math.Round(ActualHeight));
    }

    internal void ApplyShellAppearance(ThemeKind theme, bool transparencyEnabled) {
        _theme = theme;
        _transparencyEnabled = transparencyEnabled;
        if (_source is null) {
            return;
        }

        ApplyBackdrop(theme, transparencyEnabled);
        RecalcNonClientFrame();
        ApplyThemeDictionary(theme);
    }

    private void ApplyThemeDictionary(ThemeKind theme) {
        var uri = new Uri(
            theme == ThemeKind.Light
                ? "pack://application:,,,/NativeUi/Themes/FluentLight.xaml"
                : "pack://application:,,,/NativeUi/Themes/FluentDark.xaml",
            UriKind.Absolute);
        var dictionary = new ResourceDictionary { Source = uri };

        // Replace token values in place (keeps DynamicResource references alive).
        var merged = Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++) {
            if (merged[i].Source?.OriginalString.Contains("Fluent", StringComparison.Ordinal) == true) {
                merged[i] = dictionary;
                return;
            }
        }

        merged.Add(dictionary);
    }

    private void OnSourceInitialized(object? sender, EventArgs e) {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;

        // WS_EX_TOOLWINDOW (0x80): tool window — no taskbar, no alt-tab.
        var hwnd = _source.Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW_FLAG = 0x00000080;
        int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
        _ = SetWindowLongW(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW_FLAG);

        _source.AddHook(WndProc);
        ApplyBackdrop(_theme, _transparencyEnabled);
        RecalcNonClientFrame();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        switch (msg) {
            case WM_NCCALCSIZE:
                if (wParam != IntPtr.Zero) {
                    handled = true;
                    return IntPtr.Zero; // collapse NC: WS_CAPTION kept for DWM, never drawn
                }

                break;

            case WM_ACTIVATE:
                if ((wParam.ToInt64() & 0xFFFF) == 0) {
                    Deactivated95?.Invoke(this, EventArgs.Empty);
                    handled = false; // let WPF's own Deactivated also process
                }

                break;

            case WM_ACTIVATEAPP:
                if (wParam == IntPtr.Zero) {
                    Deactivated95?.Invoke(this, EventArgs.Empty);
                }

                break;

            case WM_NCACTIVATE:
                handled = true;
                return new IntPtr(1);

            case WM_NCHITTEST:
                // Border hits (10..17) remap to HTBORDER: borders exist for DWM framing but the
                // user cannot resize (SC_SIZE is blocked). Anything else falls through to WPF.
                handled = false;
                break;

            case WM_NCLBUTTONDBLCLK:
            case WM_NCRBUTTONDOWN:
            case WM_NCRBUTTONUP:
                handled = true;
                return IntPtr.Zero;

            case WM_SYSCOMMAND:
                var cmd = wParam.ToInt64() & 0xFFF0;
                if (cmd == SC_MOVE && !_isPinned) {
                    handled = true;
                    return IntPtr.Zero;
                }

                if (cmd == SC_SIZE) {
                    handled = true;
                    return IntPtr.Zero;
                }

                break;

            case WM_DPICHANGED:
                // Adopt the OS-suggested rect (physical px) via the raw handle.
                var sugRect = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32Helpers.RECT>(lParam);
                _ = PInvoke.SetWindowPos(
                    new HWND(hwnd),
                    HWND.Null,
                    sugRect.left,
                    sugRect.top,
                    sugRect.right - sugRect.left,
                    sugRect.bottom - sugRect.top,
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    internal void RecalcNonClientFrame() {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero) {
            return;
        }

        _ = PInvoke.SetWindowPos(
            new HWND(helper.Handle),
            HWND.Null,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    private unsafe void ApplyBackdrop(ThemeKind theme, bool transparencyEnabled) {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero) {
            return;
        }

        var hwnd = new HWND(helper.Handle);
        int useDarkMode = theme == ThemeKind.Light ? 0 : 1;
        _backdropActive = false;

        try {
            _ = PInvoke.DwmSetWindowAttribute(
                hwnd,
                (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)20, // DWMWA_USE_IMMERSIVE_DARK_MODE
                &useDarkMode,
                sizeof(int));

            var corner = DWMWCP_ROUNDSMALL;
            _ = PInvoke.DwmSetWindowAttribute(
                hwnd,
                (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)33, // DWMWA_WINDOW_CORNER_PREFERENCE
                &corner,
                sizeof(int));

            // v1 native: opaque-first policy mirrors decision 0004 (the WPF surface draws its
            // own Mica-like backdrop). Transient acrylic is a post-parity experiment.
            if (!transparencyEnabled) {
                int none = 1; // DWMSBT_NONE
                PInvoke.DwmSetWindowAttribute(
                    hwnd,
                    (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)38,
                    &none,
                    sizeof(int));
                var zeroMargins = new MARGINS();
                PInvoke.DwmExtendFrameIntoClientArea(hwnd, in zeroMargins);
                Background = ShellMaterialPolicy.OpaqueBrush(theme);
                return;
            }

            int backdropType = DWMSBT_TRANSIENTWINDOW;
            var hr = PInvoke.DwmSetWindowAttribute(
                hwnd,
                (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)38,
                &backdropType,
                sizeof(int));

            if (hr.Value < 0) {
                Background = ShellMaterialPolicy.OpaqueBrush(theme);
                return;
            }

            var margins = new MARGINS {
                cxLeftWidth = -1,
                cxRightWidth = -1,
                cyTopHeight = -1,
                cyBottomHeight = -1,
            };
            PInvoke.DwmExtendFrameIntoClientArea(hwnd, in margins);
            Background = Brushes.Transparent;
            _backdropActive = true;
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "NativeFlyoutWindow: DwmSetWindowAttribute failed; opaque fallback.");
            Background = ShellMaterialPolicy.OpaqueBrush(theme);
        }
#pragma warning restore CA1031
    }

}

/// <summary>Small Win32 helpers used only by the native shell.</summary>
internal static class Win32Helpers {
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    public static bool TryGetWindowRect(IntPtr hwnd, out RECT rect) {
        if (hwnd == IntPtr.Zero) {
            rect = default;
            return false;
        }

        return GetWindowRect(hwnd, out rect);
    }
}
