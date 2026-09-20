#pragma warning disable CA1707
#pragma warning disable CA1849
#pragma warning disable CA1861
#pragma warning disable CA1869
#pragma warning disable CA5392
#pragma warning disable xUnit1051

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
/// Headed WebView2 + native WM_NCHITTEST heatmap for pinned header drag.
/// Set ADBVIEW_HEADER_DRAG_PROBE=1 to run the live probe; otherwise validates
/// parent-fallback semantics only (WebView child owns NC caption hits).
/// </summary>
public sealed class HeaderDragNativeProbeTests {
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string ArtifactDir =>
        Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "artifacts", "diagnosis", "header-drag-native");

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTBORDER = 18;

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr PackLParam(int x, int y) =>
        (IntPtr)((y << 16) | (x & 0xFFFF));

    [Fact]
    public void FlyoutWindow_Parent_NCHITTEST_Does_Not_Invent_Caption() {
        using var flyout = new FlyoutWindow();
        flyout.IsPinned = true;
        flyout.Bounds = new Rectangle(100, 100, 376, 420);
        flyout.Show();
        Application.DoEvents();

        var pts = new List<object>();
        for (int row = 0; row < 4; row++) {
            for (int col = 0; col < 8; col++) {
                int sx = flyout.Left + 20 + col * 40;
                int sy = flyout.Top + 8 + row * 8;
                IntPtr result = SendMessage(
                    flyout.Handle,
                    WM_NCHITTEST,
                    IntPtr.Zero,
                    PackLParam(sx, sy));
                int hit = result.ToInt32();
                pts.Add(new { sx, sy, hit, name = HitName(hit) });
                Assert.True(
                    hit == HTCLIENT || hit == HTBORDER || hit == 0,
                    $"unexpected parent hit {hit} at {sx},{sy} — parent must not invent HTCAPTION");
                Assert.NotEqual(HTCAPTION, hit);
            }
        }

        Directory.CreateDirectory(ArtifactDir);
        File.WriteAllText(
            Path.Combine(ArtifactDir, "parent-nchittest.json"),
            JsonSerializer.Serialize(new { note = "parent never HTCAPTION", pts }, JsonOpts));
        flyout.Hide();
    }

    [Fact]
    public void Headed_WebView2_Header_Heatmap_When_Enabled() {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ADBVIEW_HEADER_DRAG_PROBE"),
                "1",
                StringComparison.Ordinal)) {
            return;
        }

        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);
        var thread = new Thread(() => {
            try {
                try {
                    Application.EnableVisualStyles();
                }
#pragma warning disable CA1031
                catch (InvalidOperationException) {
                    // Already initialized in this process.
                }
#pragma warning restore CA1031
                RunHeadedHeatmapSta(ex => failure = ex);
            }
#pragma warning disable CA1031
            catch (Exception ex) {
                failure = ex;
            }
#pragma warning restore CA1031
            finally {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(90)), "headed header drag probe timed out");
        if (failure is not null) {
            throw new InvalidOperationException("headed header drag probe failed", failure);
        }
    }

    private static void RunHeadedHeatmapSta(Action<Exception?> onFail) {
        using var form = new Form {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Bounds = new Rectangle(80, 80, 376, 200),
            BackColor = Color.Black,
        };
        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        form.Shown += async (_, _) => {
            try {
                await web.EnsureCoreWebView2Async(null).ConfigureAwait(true);
                web.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
                const string Html = """
                    <!DOCTYPE html><html><body style="margin:0;background:transparent">
                    <header id="h" style="height:40px;display:flex;align-items:center;justify-content:space-between;
                      -webkit-app-region:drag;background:rgba(32,35,41,.9);color:#fff;padding:0 12px;font:600 13px Segoe UI">
                      <span>ADB View</span>
                      <button id="pin" style="-webkit-app-region:no-drag;width:28px;height:28px">P</button>
                    </header>
                    <script>
                      window.__heat = function(){
                        const h=document.getElementById('h'); const r=h.getBoundingClientRect();
                        const cells=[];
                        for(let y=2;y<r.height;y+=4) for(let x=2;x<r.width;x+=12){
                          const el=document.elementFromPoint(r.left+x,r.top+y);
                          let cur=el, region='none';
                          while(cur){
                            const s=getComputedStyle(cur);
                            const v=s.webkitAppRegion||s.getPropertyValue('-webkit-app-region');
                            if(v&&v!=='none'){region=v;break;}
                            cur=cur.parentElement;
                          }
                          const interactive=!!(el&&el.closest('button'));
                          cells.push({x,y,region,interactive,tag:el?el.tagName:null});
                        }
                        return {w:r.width,h:r.height,cells};
                      };
                    </script></body></html>
                    """;
                web.NavigateToString(Html);
                await Task.Delay(600).ConfigureAwait(true);

                string json = await web.CoreWebView2.ExecuteScriptAsync("window.__heat()").ConfigureAwait(true);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                int drag = 0, nodrag = 0, interactive = 0;
                int upperDrag = 0, lowerDrag = 0, upperN = 0, lowerN = 0;
                int captionHits = 0, upperCaption = 0, lowerCaption = 0;
                double mid = root.GetProperty("h").GetDouble() / 2.0;
                var nativeHits = new List<object>();
                foreach (var c in root.GetProperty("cells").EnumerateArray()) {
                    bool inter = c.GetProperty("interactive").GetBoolean();
                    string region = c.GetProperty("region").GetString() ?? "none";
                    double y = c.GetProperty("y").GetDouble();
                    if (inter) {
                        interactive++;
                    }
                    else if (region == "drag") {
                        drag++;
                    }
                    else {
                        nodrag++;
                    }

                    if (y < mid) {
                        upperN++;
                        if (!inter && region == "drag") {
                            upperDrag++;
                        }
                    }
                    else {
                        lowerN++;
                        if (!inter && region == "drag") {
                            lowerDrag++;
                        }
                    }

                    int lx = c.GetProperty("x").GetInt32();
                    int ly = c.GetProperty("y").GetInt32();
                    var screen = web.PointToScreen(new Point(lx, ly));
                    IntPtr hitPtr = SendMessage(web.Handle, WM_NCHITTEST, IntPtr.Zero, PackLParam(screen.X, screen.Y));
                    int hit = hitPtr.ToInt32();
                    if (hit == HTCAPTION) {
                        captionHits++;
                        if (y < mid) {
                            upperCaption++;
                        }
                        else {
                            lowerCaption++;
                        }
                    }

                    nativeHits.Add(new {
                        lx,
                        ly,
                        screenX = screen.X,
                        screenY = screen.Y,
                        cssRegion = region,
                        interactive = inter,
                        nativeHit = hit,
                        nativeName = HitName(hit),
                    });
                }

                Directory.CreateDirectory(ArtifactDir);
                var report = new {
                    css = new {
                        drag,
                        nodrag,
                        interactive,
                        upperDragFrac = upperN == 0 ? 0 : (double)upperDrag / upperN,
                        lowerDragFrac = lowerN == 0 ? 0 : (double)lowerDrag / lowerN,
                    },
                    captionHits,
                    upperCaption,
                    lowerCaption,
                    nativeHits,
                    note = "Compare upper vs lower native HTCAPTION density to reproduce human dead-zone.",
                };
                await File.WriteAllTextAsync(
                    Path.Combine(ArtifactDir, "native-heatmap.json"),
                    JsonSerializer.Serialize(report, JsonOpts)).ConfigureAwait(true);
                await File.WriteAllTextAsync(
                    Path.Combine(ArtifactDir, "native-heatmap-summary.txt"),
                    $"css drag={drag} nodrag={nodrag} interactive={interactive}\n" +
                    $"upperDragFrac={report.css.upperDragFrac:F2} lowerDragFrac={report.css.lowerDragFrac:F2}\n" +
                    $"HTCAPTION count={captionHits} upper={upperCaption} lower={lowerCaption}\n").ConfigureAwait(true);
                onFail(null);
            }
#pragma warning disable CA1031
            catch (Exception ex) {
                onFail(ex);
            }
#pragma warning restore CA1031
            finally {
                form.Close();
            }
        };

        Application.Run(form);
    }

    private static string HitName(int hit) => hit switch {
        HTCLIENT => "HTCLIENT",
        HTCAPTION => "HTCAPTION",
        HTBORDER => "HTBORDER",
        _ => hit.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
