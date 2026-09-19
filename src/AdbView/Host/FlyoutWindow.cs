using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AdbView.State;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AdbView.Host;

internal sealed class FlyoutWindow : Form {
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
    private const int HTBORDER = 18;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WA_INACTIVE = 0;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_ROUNDSMALL = 3;

    private bool _isPinned;
    private ThemeKind _theme = ThemeKind.Dark;
    private bool _transparencyEnabled = true;
    private bool _backdropActive;

    /// <summary>
    /// Caption-class height (~32 px at 96 DPI). Frame regression rejects insets ≥ this
    /// (distinguishes full Windows caption from few-pixel DWM borders).
    /// </summary>
    internal const int CaptionInsetRejectPx = 28;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsPinned {
        get => _isPinned;
        set {
            _isPinned = value;
            TopMost = value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ThemeKind AppliedTheme => _theme;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool BackdropActive => _backdropActive;

    public FlyoutWindow() {
        // Sizable + WS_CAPTION retained (CreateParams) so DWM acrylic works; NC collapsed in WM_NCCALCSIZE.
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
#pragma warning disable CA1303 // Window title for native identification, not user-facing localized string
        Text = "ADB View";
#pragma warning restore CA1303
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            // KEEP WS_CAPTION (0x00C00000) — ENGINEERING.md §9.1 / acrylic requirement.
            cp.Style |= 0x00C00000; // WS_CAPTION
            cp.Style &= ~0x00080000; // WS_SYSMENU
            cp.Style &= ~0x00040000; // WS_THICKFRAME
            cp.Style &= ~0x00020000; // WS_MINIMIZEBOX
            cp.Style &= ~0x00010000; // WS_MAXIMIZEBOX
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        Log.Information("FlyoutWindow.OnHandleCreated: Handle={Handle}", Handle);
        base.OnHandleCreated(e);
        ApplyBackdrop(_theme, _transparencyEnabled);
        RecalcNonClientFrame();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        // First visible frame must already have collapsed NC (Defect 1).
        RecalcNonClientFrame();
    }

    /// <summary>
    /// Forces non-client recalculation after custom-frame / backdrop establishment
    /// (SetWindowPos + SWP_FRAMECHANGED). Centralized — do not scatter magic flags.
    /// </summary>
    internal void RecalcNonClientFrame() {
        if (!IsHandleCreated) {
            return;
        }

        PInvoke.SetWindowPos(
            new HWND(Handle),
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

        Log.Information("FlyoutWindow.RecalcNonClientFrame applied");
    }

    /// <summary>
    /// Client top relative to window top in physical pixels. ≈0 when NC is collapsed;
    /// ≈caption height when a real title bar is present.
    /// </summary>
    internal int MeasureClientTopInsetPx() {
        if (!IsHandleCreated) {
            return -1;
        }

        var client = PointToScreen(Point.Empty);
        return client.Y - Bounds.Y;
    }

    internal void ApplyShellAppearance(ThemeKind theme, bool transparencyEnabled) {
        _theme = theme;
        _transparencyEnabled = transparencyEnabled;
        if (!IsHandleCreated) {
            return;
        }

        ApplyBackdrop(theme, transparencyEnabled);
        RecalcNonClientFrame();
    }

    private unsafe void ApplyBackdrop(ThemeKind theme, bool transparencyEnabled) {
        int backdropType = DWMSBT_TRANSIENTWINDOW;
        int useDarkMode = theme == ThemeKind.Light ? 0 : 1;
        _backdropActive = false;

        try {
            Log.Information(
                "DWM applyBackdrop: theme={Theme}, useDarkMode={UseDarkMode}, transparency={Transparency}",
                theme,
                useDarkMode,
                transparencyEnabled);

            if (!transparencyEnabled) {
                ApplyOpaqueFallback(theme);
                return;
            }

            var hrBackdrop = PInvoke.DwmSetWindowAttribute(
                new HWND(Handle),
                (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)38, // DWMWA_SYSTEMBACKDROP_TYPE
                &backdropType,
                sizeof(int));

            var hrDark = PInvoke.DwmSetWindowAttribute(
                new HWND(Handle),
                (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)20, // DWMWA_USE_IMMERSIVE_DARK_MODE
                &useDarkMode,
                sizeof(int));

            int corner = DWMWCP_ROUNDSMALL;
            var hrCorner = PInvoke.DwmSetWindowAttribute(
                new HWND(Handle),
                (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)33, // DWMWA_WINDOW_CORNER_PREFERENCE
                &corner,
                sizeof(int));

            Log.Information(
                "DWM results: backdrop=0x{BackdropHR:X8}, darkMode=0x{DarkHR:X8}, corner=0x{CornerHR:X8}",
                hrBackdrop.Value,
                hrDark.Value,
                hrCorner.Value);

            if (hrBackdrop.Value < 0) {
                Log.Information("DWM backdrop unsupported or failed. Using opaque fallback.");
                ApplyOpaqueFallback(theme);
            }
            else {
                BackColor = Color.Black; // Transparent acrylic host surface
                var margins = new Windows.Win32.UI.Controls.MARGINS {
                    cxLeftWidth = -1,
                    cxRightWidth = -1,
                    cyTopHeight = -1,
                    cyBottomHeight = -1,
                };
                PInvoke.DwmExtendFrameIntoClientArea(new HWND(Handle), in margins);
                _backdropActive = true;
            }
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "DwmSetWindowAttribute failed. Using opaque fallback.");
            ApplyOpaqueFallback(theme);
        }
#pragma warning restore CA1031
    }

    private void ApplyOpaqueFallback(ThemeKind theme) {
        _backdropActive = false;
        // DESIGN §22 opaque tokens: intentional light/dark solid surface.
        BackColor = theme == ThemeKind.Light
            ? Color.FromArgb(255, 243, 244, 246)
            : Color.FromArgb(255, 32, 35, 41);
    }

    protected override void WndProc(ref Message m) {
        switch (m.Msg) {
            case WM_NCCALCSIZE:
                if (m.WParam != IntPtr.Zero) {
                    // Collapse non-client area so WS_CAPTION remains for DWM but is not drawn.
                    m.Result = IntPtr.Zero;
                    return;
                }
                break;

            case WM_ACTIVATE:
                base.WndProc(ref m);
                if ((m.WParam.ToInt32() & 0xFFFF) == WA_INACTIVE && ActiveForm != this) {
                    OnDeactivate(EventArgs.Empty);
                }
                return;

            case WM_ACTIVATEAPP:
                base.WndProc(ref m);
                if (m.WParam == IntPtr.Zero && ActiveForm != this) {
                    OnDeactivate(EventArgs.Empty);
                }
                return;

            case WM_NCACTIVATE:
                m.WParam = new IntPtr(1);
                break;

            case WM_NCHITTEST:
                base.WndProc(ref m);
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) {
                    m.Result = new IntPtr(HTBORDER);
                }
                return;

            case WM_NCLBUTTONDBLCLK:
            case WM_NCRBUTTONDOWN:
            case WM_NCRBUTTONUP:
                m.Result = IntPtr.Zero;
                return;

            case WM_SYSCOMMAND:
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == SC_MOVE) {
                    if (!IsPinned) {
                        m.Result = IntPtr.Zero;
                        return;
                    }
                }
                else if (cmd == SC_SIZE) {
                    m.Result = IntPtr.Zero;
                    return;
                }
                break;

            case WM_DPICHANGED:
                var rect = Marshal.PtrToStructure<RECT>(m.LParam);
                Bounds = new Rectangle(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
                Log.Information("DPI Changed. Applied suggested bounds: {Bounds}", Bounds);
                RecalcNonClientFrame();
                break;
        }

        base.WndProc(ref m);
    }
}
