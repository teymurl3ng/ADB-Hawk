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
    private bool _animationsEnabled = true;

    public event EventHandler? Deactivated95;

    /// <summary>Raised when the user presses Escape (web-parity dismiss gesture, decision 0022).</summary>
    public event EventHandler? DismissRequested;

    public NativeFlyoutWindow() {
        // ShellView XAML resolves StaticResource icon/token lookups during InitializeComponent —
        // the dark dictionary must be merged BEFORE the content tree parses.
        Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("pack://application:,,,/NativeUi/Themes/FluentDark.xaml", UriKind.Absolute),
        });

        Title = "ADB View";
        Width = 376;
        Height = 500;
        WindowStyle = WindowStyle.SingleBorderWindow; // keeps WS_CAPTION for DWM
        ResizeMode = ResizeMode.CanResize;            // THICKFRAME stripped in source hook
        ShowInTaskbar = false;
        ShowActivated = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Esc-to-dismiss (web parity): the web dismissed with Escape unless pinned; ported so the
        // gesture survives the WebView removal (decision 0022).
        PreviewKeyDown += (_, e) => {
            if (e.Key == System.Windows.Input.Key.Escape) {
                e.Handled = true;
                DismissRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        var shell = new Shell.ShellView { DataContext = new Shell.NativeShellViewModel() };
        Content = shell;
        // Decision 0021: self-sizing. Every layout pass of the shell re-measures the content with
        // infinite height; a clamped step is applied instantly (bottom edge frozen). LayoutUpdated
        // fires often — the applied-height dedup in ApplySelfSizedHeight keeps this to real steps.
        shell.ShellRoot.LayoutUpdated += (_, _) => ApplySelfSizedHeight();
        ContentRendered += (_, _) => ApplySelfSizedHeight();
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

    /// <summary>
    /// Placement scale for physical-pixel bounds. Before first Show the HwndSource does not
    /// exist yet, and a 1.0 fallback sizes DIP numbers as physical pixels — undersizing the
    /// window on scaled displays. Fall back to the system DPI from a screen DC instead.
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

    /// <summary>Raised when self-sizing applied a new height (AppHost syncs placement state).</summary>
    internal event Action<double>? SelfSizedHeightChanged;

    private double _appliedSelfSizedHeight;

    /// <summary>
    /// Decision 0021: one instant self-size step per settled content change. Measures the shell
    /// with infinite height, clamps (ShellSizing), and applies via SetWindowPos with the bottom
    /// edge frozen — growth extends upwards, never moves the tray-adjacent edge. No interpolation,
    /// no per-frame work: sub-DIP noise is deduplicated away.
    /// </summary>
    private void ApplySelfSizedHeight() {
        if (_source is null || Content is not Shell.ShellView shell) {
            return;
        }

        // Measure with infinite height: inside the ScrollViewer the content already sees an
        // unconstrained height, but re-measuring the ROOT guarantees the full desired size
        // regardless of the current window height.
        double width = shell.ActualWidth > 0 ? shell.ActualWidth : ShellSizing.WidthDip;
        shell.ShellRoot.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        double desired = shell.ShellRoot.DesiredSize.Height;

        // The tray's monitor is the right one (the flyout anchors to the tray); the screen lookup
        // belongs to Host/ per the R-04 boundary, so it comes from TrayIcon, not from here.
        double scale = DpiScale;
        double workTopDip = TrayIcon.GetTaskbarScreen().WorkingArea.Top / scale;
        double bottomDip = Top + Height;

        var step = ShellSizing.Step(desired, workTopDip, bottomDip);
        if (step is null) {
            return;
        }

        var (targetHeight, targetTop) = step.Value;

        // Compare against the window's ACTUAL height (synced from the OS), not the Height
        // property: external SetWindowPos moves do not update the logical property, so using it
        // for dedup would fight the placement animation forever.
        if (!ShellSizing.IsWorthAStep(targetHeight, ActualHeight)) {
            return;
        }

        // Preserve the CURRENT physical x/width (placement owns them; Left/Top are unreliable here)
        // and apply the new y/height: bottom edge frozen, one call, no activation.
        if (!Win32Helpers.TryGetWindowRect(_source.Handle, out var rect)) {
            Log.Warning("SelfSizedHeight: TryGetWindowRect failed; skipping the step");
            return;
        }

        Log.Information(
            "SelfSizedHeight: stepping {Current:F0} -> {Target:F0} DIP (top {Top:F0}, physical {Px})",
            ActualHeight, targetHeight, targetTop, rect.top);

        var ok = PInvoke.SetWindowPos(
            new HWND(_source.Handle),
            HWND.Null,
            rect.left,
            (int)Math.Round(targetTop * scale),
            rect.right - rect.left,
            (int)Math.Round(targetHeight * scale),
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        if (!ok) {
            Log.Warning("SelfSizedHeight: SetWindowPos failed for {Height}DIP", targetHeight);
            return;
        }

        // Keep the WPF property in sync without another layout storm.
        _appliedSelfSizedHeight = targetHeight;
        Height = targetHeight;
        Top = targetTop;
        SelfSizedHeightChanged?.Invoke(targetHeight);
    }

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
        ApplyMotionDictionary(dictionary, _animationsEnabled);

        // Replace token values in place (keeps DynamicResource references alive).
        var merged = Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++) {
            if (merged[i].Source?.OriginalString.Contains("Fluent", StringComparison.Ordinal) == true) {
                merged[i] = dictionary;
                Log.Information("Window theme dictionary swapped to {Theme}", theme);
                return;
            }
        }

        merged.Add(dictionary);
        Log.Information("Window theme dictionary added: {Theme}", theme);
    }

    /// <summary>
    /// Decision 0021: storyboards inside sealed templates must freeze, so their durations are
    /// StaticResource (baked at parse) — a live resource override cannot reach them. Reduced
    /// motion instead swaps the chrome dictionary for ControlsReduced.xaml: identical styles,
    /// instant setters, zero tokens. Swapping a merged dictionary re-resolves the implicit
    /// styles, so the change is live. The desired state is remembered, so a gate that arrives
    /// before the theme dictionary exists is honoured the moment it loads.
    /// </summary>
    internal void ApplyMotionGate(bool animationsEnabled) {
        _animationsEnabled = animationsEnabled;
        var merged = Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++) {
            if (merged[i].Source?.OriginalString.Contains("Fluent", StringComparison.Ordinal) == true
                && ApplyMotionDictionary(merged[i], animationsEnabled)) {
                return;
            }
        }

        // No theme dictionary loaded yet (pre-SourceInitialized): the remembered flag is
        // applied by ApplyThemeDictionary. Nothing to warn about.
    }

    private static bool ApplyMotionDictionary(ResourceDictionary theme, bool animationsEnabled) {
        var wanted = animationsEnabled ? "Controls.xaml" : "ControlsReduced.xaml";
        var nested = theme.MergedDictionaries;
        for (var i = 0; i < nested.Count; i++) {
            var source = nested[i].Source?.OriginalString ?? string.Empty;
            bool isControls = source.EndsWith("Controls.xaml", StringComparison.Ordinal)
                || source.EndsWith("ControlsReduced.xaml", StringComparison.Ordinal);
            if (isControls && !source.EndsWith(wanted, StringComparison.Ordinal)) {
                nested[i] = new ResourceDictionary {
                    Source = new Uri("pack://application:,,,/NativeUi/Themes/" + wanted, UriKind.Absolute),
                };
                Log.Information(
                    "Motion gate: chrome dictionary swapped to {Dictionary} (animations {State}).",
                    wanted,
                    animationsEnabled ? "enabled" : "reduced to instant");
                return true;
            }
        }

        return false;
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
        // ApplyShellAppearance early-returns while _source is null, so a theme that changed
        // before first show left this dictionary stale (mixed-theme render). Re-sync now.
        ApplyThemeDictionary(_theme);
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
