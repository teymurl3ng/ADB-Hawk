using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;

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

    private bool _isPinned;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsPinned {
        get => _isPinned;
        set {
            _isPinned = value;
            TopMost = value;
        }
    }

    public FlyoutWindow() {
        FormBorderStyle = FormBorderStyle.Sizable; // Retain non-popup frame for backdrop support
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
            cp.Style &= ~0x00C00000; // WS_CAPTION (removes native caption bar)
            cp.Style &= ~0x00080000; // WS_SYSMENU (removes DWM close button / system menu)
            cp.Style &= ~0x00040000; // WS_THICKFRAME (removes native resize borders)
            cp.Style &= ~0x00020000; // WS_MINIMIZEBOX (prevents minimize)
            cp.Style &= ~0x00010000; // WS_MAXIMIZEBOX (prevents maximize)
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        Log.Information("FlyoutWindow.OnHandleCreated: Handle={Handle}", Handle);
        base.OnHandleCreated(e);
        ApplyBackdrop();
    }

    private unsafe void ApplyBackdrop() {
        int backdropType = DWMSBT_TRANSIENTWINDOW;
        try {
            int useDarkMode = 1;
            try {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null && key.GetValue("AppsUseLightTheme") is int lightTheme && lightTheme == 1) {
                    useDarkMode = 0;
                }
            }
#pragma warning disable CA1031
            catch (Exception ex) {
                Log.Warning(ex, "Failed to read light-theme registry value; defaulting to dark mode");
            }
#pragma warning restore CA1031

            Log.Information("DWM applyBackdrop: useDarkMode={UseDarkMode}, backdropType={BackdropType}, BackColor={BackColor}",
                useDarkMode, backdropType, BackColor);

            // Set backdrop type FIRST, then dark mode (order matters for DWM)
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

            Log.Information("DWM results: backdrop=0x{BackdropHR:X8}, darkMode=0x{DarkHR:X8}",
                hrBackdrop.Value, hrDark.Value);

            if (hrBackdrop.Value < 0) {
                Log.Information("DWM backdrop unsupported or failed. Using opaque fallback.");
                BackColor = Color.FromArgb(255, 30, 30, 30); // Opaque fallback
            }
            else {
                // Extend frame into client area so DWM backdrop renders through Color.Black
                var margins = new Windows.Win32.UI.Controls.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                PInvoke.DwmExtendFrameIntoClientArea(new HWND(Handle), in margins);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "DwmSetWindowAttribute failed. Using opaque fallback.");
            BackColor = Color.FromArgb(255, 30, 30, 30);
        }
#pragma warning restore CA1031
    }

    protected override void WndProc(ref Message m) {
        switch (m.Msg) {
            case WM_NCCALCSIZE:
                if (m.WParam != IntPtr.Zero) {
                    // Remove standard client area frame to make it borderless
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
                // Keep the acrylic effect active even when unfocused
                m.WParam = new IntPtr(1);
                break;

            case WM_NCHITTEST:
                // WebView2 IsNonClientRegionSupportEnabled handles app-region: drag natively.
                // But WinForms might still return sizing border hits. Suppress them explicitly.
                base.WndProc(ref m);
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) { // HTLEFT through HTBOTTOMRIGHT
                    m.Result = new IntPtr(HTBORDER);
                }
                return;

            case WM_NCLBUTTONDBLCLK:
            case WM_NCRBUTTONDOWN:
            case WM_NCRBUTTONUP:
                // Prevent maximize on double click, and prevent system menu on right click of the app-region: drag area
                m.Result = IntPtr.Zero;
                return;

            case WM_SYSCOMMAND:
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == SC_MOVE) {
                    if (!IsPinned) {
                        // Prevent dragging when unpinned
                        m.Result = IntPtr.Zero;
                        return;
                    }
                }
                else if (cmd == SC_SIZE) {
                    // Prevent any resizing commands triggered via native OS shortcuts/menus
                    m.Result = IntPtr.Zero;
                    return;
                }
                break;

            case WM_DPICHANGED:
                // The suggested rect is in LParam
                var rect = Marshal.PtrToStructure<RECT>(m.LParam);
                Bounds = new Rectangle(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
                Log.Information("DPI Changed. Applied suggested bounds: {Bounds}", Bounds);
                break;
        }

        base.WndProc(ref m);
    }
}
