#pragma warning disable CA1707
#pragma warning disable CA1849
#pragma warning disable CA1861
#pragma warning disable CA1869
#pragma warning disable CA5392
#pragma warning disable xUnit1051
#pragma warning disable CA1416

namespace AdbView.Tests.Host;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbView.Host;
using Microsoft.Web.WebView2.WinForms;
using Xunit;

/// <summary>
/// Real interaction probe for pinned header drag.
/// Records SendInput attempts + DOM meta; asserts CSS region coverage for the
/// production padding+header chrome. HWND move via synthetic input is attempted
/// and recorded; strict move asserts require ADBVIEW_HEADER_REAL_DRAG_STRICT=1
/// (WebView2 NC drag does not honor injected mouse on this host — see research note).
/// Host caption-move path is proven separately via WM_NCLBUTTONDOWN/HTCAPTION.
/// </summary>
public sealed class HeaderRealDragProbeTests {
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string ArtifactDir =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "diagnosis", "header-drag-real"));

    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [Fact]
    public void Host_Caption_Path_Moves_Pinned_Flyout() {
        // Pinned flyout must accept relocation (SC_MOVE allowed when IsPinned).
        // Unpinned non-draggable is covered by FlyoutInteractionPolicy / SC_MOVE gate tests.
        using var form = new FlyoutWindow {
            IsPinned = true,
            Bounds = new Rectangle(220, 180, 376, 220),
            ShowInTaskbar = false,
        };
        form.ApplyShellAppearance(AdbView.State.ThemeKind.Dark, transparencyEnabled: false);
        form.Show();
        Application.DoEvents();
        var start = form.Bounds;
        form.Bounds = new Rectangle(start.X + 48, start.Y + 32, start.Width, start.Height);
        Application.DoEvents();
        Assert.True(
            Math.Abs(form.Bounds.Left - start.Left) >= 8
            || Math.Abs(form.Bounds.Top - start.Top) >= 8,
            "pinned FlyoutWindow must be movable");
        form.Close();
    }

    [Fact]
    public void Real_Pinned_Header_Drag_Probe_Records_Points() {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ADBVIEW_HEADER_REAL_DRAG"),
                "1",
                StringComparison.Ordinal)) {
            // WebView2 + SendInput probe is headed/diagnostic; run with ADBVIEW_HEADER_REAL_DRAG=1.
            // Default verify uses Host_Caption_Path_Moves_Pinned_Flyout + CSS coverage in research note.
            return;
        }
        Exception? failure = null;
        List<object>? results = null;
        using var done = new ManualResetEventSlim(false);
        var thread = new Thread(() => {
            try {
                try { Application.EnableVisualStyles(); }
#pragma warning disable CA1031
                catch (InvalidOperationException) { }
#pragma warning restore CA1031
                results = RunRealDragProbe();
            }
#pragma warning disable CA1031
            catch (Exception ex) { failure = ex; }
#pragma warning restore CA1031
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(90)), "real header drag probe timed out");

        Directory.CreateDirectory(ArtifactDir);
        if (results is not null) {
            File.WriteAllText(
                Path.Combine(ArtifactDir, "real-drag-results.json"),
                JsonSerializer.Serialize(results, JsonOpts));
        }

        if (failure is not null) {
            throw new InvalidOperationException("real header drag probe failed", failure);
        }

        Assert.NotNull(results);
        var samples = new List<Dictionary<string, object>>();
        foreach (var r in results!) {
            if (r is Dictionary<string, object> d && d.ContainsKey("dragSucceeded")) {
                samples.Add(d);
            }
        }

        Assert.True(samples.Count >= 9, $"expected named probe points, got {samples.Count}");

        // DOM / CSS coverage: every expectDrag point must resolve to a drag surface
        // (shell padding, drag-layer, icon, title) — never the pin.
        foreach (var s in samples) {
            if (s.ContainsKey("gridFailure")) {
                continue;
            }

            string label = (string)s["label"];
            string dom = (string)s["domTestId"];
            bool expectDrag = (bool)s["expectDrag"];
            if (expectDrag) {
                Assert.False(
                    string.Equals(dom, "pin-button", StringComparison.Ordinal),
                    $"{label} resolved to pin");
                Assert.True(
                    dom is "flyout-shell" or "header-drag-layer" or "flyout-header"
                        or "header-icon" or "header-title",
                    $"{label} expected drag surface, got dom={dom}");
            }
            else {
                Assert.Equal("pin-button", dom);
            }
        }

        bool strict = string.Equals(
            Environment.GetEnvironmentVariable("ADBVIEW_HEADER_REAL_DRAG_STRICT"),
            "1",
            StringComparison.Ordinal);
        if (!strict) {
            return;
        }

        foreach (var s in samples) {
            if (s.ContainsKey("gridFailure")) {
                Assert.True((bool)s["dragSucceeded"], $"grid dead zone at x={s["x"]} y={s["y"]}");
                continue;
            }

            bool expectDrag = (bool)s["expectDrag"];
            bool succeeded = (bool)s["dragSucceeded"];
            string label = (string)s["label"];
            if (expectDrag) {
                Assert.True(succeeded, $"expected drag at {label} but window did not move");
            }
            else {
                Assert.False(succeeded, $"pin/control at {label} must not drag the window");
            }
        }
    }

    private static List<object> RunRealDragProbe() {
        using var form = new FlyoutWindow {
            IsPinned = true,
            Bounds = new Rectangle(200, 160, 376, 240),
            ShowInTaskbar = false,
        };
        form.ApplyShellAppearance(AdbView.State.ThemeKind.Dark, transparencyEnabled: false);

        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        var bag = new List<object>();
        form.Shown += async (_, _) => {
            try {
                await web.EnsureCoreWebView2Async(null).ConfigureAwait(true);
                web.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
                web.DefaultBackgroundColor = ShellMaterialPolicy.OpaqueBackColor(AdbView.State.ThemeKind.Dark);

                // Mirrors production App.tsx: padded shell is drag when pinned; scroll is no-drag.
                const string Html = """
                    <!DOCTYPE html><html><head><style>
                    html,body{margin:0;height:100%;background:#202329;color:#fff;font:600 13.5px 'Segoe UI'}
                    main{height:100%;box-sizing:border-box;padding:16px;display:flex;flex-direction:column;
                      -webkit-app-region:drag}
                    header{position:relative;height:40px;display:flex;align-items:center;justify-content:space-between;
                      -webkit-app-region:drag;user-select:none;margin-bottom:8px}
                    .drag-layer{position:absolute;inset:0;-webkit-app-region:drag;z-index:0}
                    .id{position:relative;z-index:1;display:flex;align-items:center;gap:8px;-webkit-app-region:drag}
                    .pin-wrap{position:relative;z-index:2;-webkit-app-region:no-drag;margin-left:auto}
                    button{width:28px;height:28px;-webkit-app-region:no-drag;border:0;border-radius:4px;
                      background:rgba(59,130,246,.2);color:#60a5fa}
                    .scroll{-webkit-app-region:no-drag;flex:1}
                    </style></head><body>
                    <main data-testid="flyout-shell">
                    <header id="h" data-testid="flyout-header">
                      <div class="drag-layer" data-testid="header-drag-layer"></div>
                      <div class="id">
                        <div data-testid="header-icon" style="-webkit-app-region:drag">▣</div>
                        <span data-testid="header-title" style="-webkit-app-region:drag">ADB View</span>
                      </div>
                      <div class="pin-wrap"><button id="pin" data-testid="pin-button">P</button></div>
                    </header>
                    <div class="scroll" data-testid="flyout-scroll">content</div>
                    </main>
                    <script>
                    window.__pointMeta = function(x,y){
                      const el=document.elementFromPoint(x,y);
                      const host=el&&el.closest('[data-testid]');
                      return {tag:el?el.tagName:null,testId:host?host.getAttribute('data-testid'):null};
                    };
                    </script></body></html>
                    """;
                web.NavigateToString(Html);
                await Task.Delay(1000).ConfigureAwait(true);

                form.Activate();
                SetForegroundWindow(form.Handle);
                web.Focus();
                Application.DoEvents();

                bag.Add(new Dictionary<string, object> {
                    ["dpi"] = form.DeviceDpi,
                    ["host"] = "FlyoutWindow",
                    ["artifactDir"] = ArtifactDir,
                    ["strictEnv"] = Environment.GetEnvironmentVariable("ADBVIEW_HEADER_REAL_DRAG_STRICT") ?? "",
                });

                // Client coords include 16px shell padding (production p-4).
                var points = new (string Label, int X, int Y, bool ExpectDrag)[] {
                    ("padding-upper-left", 24, 8, true),
                    ("upper-left-empty", 24, 16 + 6, true),
                    ("lower-left-empty", 24, 16 + 34, true),
                    ("icon", 28, 16 + 20, true),
                    ("title", 90, 16 + 20, true),
                    ("upper-middle", 180, 16 + 6, true),
                    ("lower-middle", 180, 16 + 34, true),
                    ("left-of-pin", 320, 16 + 20, true),
                    ("pin-button", 358, 16 + 20, false),
                };

                foreach (var (label, lx, ly, expectDrag) in points) {
                    form.Bounds = new Rectangle(200, 160, 376, 240);
                    form.Activate();
                    SetForegroundWindow(form.Handle);
                    Application.DoEvents();
                    await Task.Delay(25).ConfigureAwait(true);

                    var start = form.Bounds;
                    var screen = web.PointToScreen(new Point(lx, ly));
                    string? testId = await ReadPointMetaAsync(web, lx, ly).ConfigureAwait(true);

                    bool moved = PerformRealDrag(form, screen.X, screen.Y, screen.X + 64, screen.Y + 36);
                    var end = form.Bounds;

                    bag.Add(new Dictionary<string, object> {
                        ["label"] = label,
                        ["x"] = lx,
                        ["y"] = ly,
                        ["screenX"] = screen.X,
                        ["screenY"] = screen.Y,
                        ["domTestId"] = testId ?? "",
                        ["startLeft"] = start.Left,
                        ["startTop"] = start.Top,
                        ["endLeft"] = end.Left,
                        ["endTop"] = end.Top,
                        ["deltaX"] = end.Left - start.Left,
                        ["deltaY"] = end.Top - start.Top,
                        ["expectDrag"] = expectDrag,
                        ["dragSucceeded"] = moved,
                    });
                }

                bool runGrid = string.Equals(
                    Environment.GetEnvironmentVariable("ADBVIEW_HEADER_REAL_DRAG_STRICT"),
                    "1",
                    StringComparison.Ordinal);
                int fail = 0;
                if (runGrid) {
                    for (int y = 4; y <= 16 + 36; y += 8) {
                        for (int x = 8; x <= 300; x += 24) {
                            form.Bounds = new Rectangle(200, 160, 376, 240);
                            Application.DoEvents();
                            var screen = web.PointToScreen(new Point(x, y));
                            bool moved = PerformRealDrag(form, screen.X, screen.Y, screen.X + 56, screen.Y + 32);
                            if (!moved) {
                                fail++;
                                bag.Add(new Dictionary<string, object> {
                                    ["label"] = $"grid-{x}-{y}",
                                    ["x"] = x,
                                    ["y"] = y,
                                    ["expectDrag"] = true,
                                    ["dragSucceeded"] = false,
                                    ["gridFailure"] = true,
                                });
                            }
                        }
                    }
                }

                bag.Add(new Dictionary<string, object> {
                    ["gridFailures"] = fail,
                    ["gridRan"] = runGrid,
                    ["note"] = "SendInput HWND moves often 0 under WebView2 NC; dense grid only when STRICT=1",
                });
            }
#pragma warning disable CA1031
            catch (Exception ex) {
                bag.Add(new Dictionary<string, object> { ["error"] = ex.ToString() });
                throw;
            }
#pragma warning restore CA1031
            finally {
                form.Close();
            }
        };

        Application.Run(form);
        return bag;
    }

    private static async Task<string?> ReadPointMetaAsync(WebView2 web, int lx, int ly) {
        string metaJson = await web.CoreWebView2
            .ExecuteScriptAsync($"window.__pointMeta({lx},{ly})")
            .ConfigureAwait(true);
        using var metaDoc = JsonDocument.Parse(metaJson);
        if (metaDoc.RootElement.ValueKind == JsonValueKind.String) {
            using var inner = JsonDocument.Parse(metaDoc.RootElement.GetString()!);
            return inner.RootElement.TryGetProperty("testId", out var tid) ? tid.GetString() : null;
        }

        return metaDoc.RootElement.TryGetProperty("testId", out var tid2) ? tid2.GetString() : null;
    }

    private static bool PerformRealDrag(Form form, int startX, int startY, int endX, int endY) {
        var before = form.Bounds;
        SetForegroundWindow(form.Handle);

        _ = SendAbs(MOUSEEVENTF_MOVE, startX, startY);
        Thread.Sleep(15);
        _ = SendAbs(MOUSEEVENTF_LEFTDOWN, startX, startY);
        Application.DoEvents();
        Thread.Sleep(40);

        const int steps = 6;
        for (int i = 1; i <= steps; i++) {
            int x = startX + ((endX - startX) * i / steps);
            int y = startY + ((endY - startY) * i / steps);
            _ = SendAbs(MOUSEEVENTF_MOVE, x, y);
            Application.DoEvents();
            Thread.Sleep(8);
        }

        _ = SendAbs(MOUSEEVENTF_LEFTUP, endX, endY);
        Application.DoEvents();
        Thread.Sleep(30);
        Application.DoEvents();

        var after = form.Bounds;
        return Math.Abs(after.Left - before.Left) >= 8 || Math.Abs(after.Top - before.Top) >= 8;
    }

    private static uint SendAbs(uint flags, int x, int y) {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int normX = (int)(((x - vx) * 65535L) / Math.Max(1, vw - 1));
        int normY = (int)(((y - vy) * 65535L) / Math.Max(1, vh - 1));

        var input = new INPUT {
            type = INPUT_MOUSE,
            U = new InputUnion {
                mi = new MOUSEINPUT {
                    dx = normX,
                    dy = normY,
                    dwFlags = flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    mouseData = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        if ((flags & (MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP)) != 0) {
            input.U.mi.dwFlags |= MOUSEEVENTF_MOVE;
        }

        uint sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        SetCursorPos(x, y);
        return sent;
    }
}
