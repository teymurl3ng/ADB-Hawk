using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbView.Bridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Serilog;

namespace AdbView.Host;

internal sealed class WebViewHost : IDisposable {
    private readonly Control _parent;
    private readonly BridgeCoordinator _coordinator;
    private WebView2? _webView;
    private int _recoveryAttempts;
    private const int MaxRecoveryAttempts = 3;
    private string _initialUrlSuffix = "";
    private string _currentFixtureName = "";

    public WebViewHost(Control parent, BridgeCoordinator coordinator) {
        _parent = parent;
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public async Task InitializeAsync(string initialUrlSuffix = "") {
        _initialUrlSuffix = initialUrlSuffix;
        if (!string.IsNullOrEmpty(initialUrlSuffix)) {
            const string prefix = "?fixture=";
            var idx = initialUrlSuffix.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) {
                var rawName = initialUrlSuffix[(idx + prefix.Length)..];
                var amp = rawName.IndexOf('&', StringComparison.Ordinal);
                _currentFixtureName = amp >= 0 ? rawName[..amp] : rawName;
            }
        }
        try {
            _webView = new WebView2 {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Transparent
            };
            _parent.Controls.Add(_webView);

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ADBView",
                "WebView2");

            var envOptions = new CoreWebView2EnvironmentOptions();
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions).ConfigureAwait(true);

            await _webView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            ConfigureSecurity();
            ConfigureEvents();

            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var wwwroot = Path.Combine(currentDir, "wwwroot");
            var htmlPath = Path.Combine(wwwroot, "index.html");

            Log.Information("Resolved wwwroot path: {Path}", wwwroot);

            if (File.Exists(htmlPath)) {
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app.adbview.local",
                    wwwroot,
                    CoreWebView2HostResourceAccessKind.Allow);

                var targetUri = new Uri("https://app.adbview.local/index.html" + initialUrlSuffix);
                Log.Information("Navigating to: {Uri}", targetUri);
                _webView.Source = targetUri;
            }
            else {
                Log.Warning("Static html page not found at {Path}", htmlPath);
                _webView.NavigateToString("<html><body style='color: white; background: transparent;'>ADB View Shell</body></html>");
            }

            _recoveryAttempts = 0; // Reset on success
            Log.Information("WebView2 initialized successfully.");
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Error(ex, "Failed to initialize WebView2.");
            HandleFailure();
        }
#pragma warning restore CA1031
    }

    private void ConfigureSecurity() {
        if (_webView?.CoreWebView2 == null) return;
        var settings = _webView.CoreWebView2.Settings;

        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false; // Disable for Release
#if DEBUG
        settings.AreDevToolsEnabled = true;
#endif
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsWebMessageEnabled = true; // Temporary for Phase 1 pin test
        settings.IsNonClientRegionSupportEnabled = true; // Required for app-region: drag
    }

    private void ConfigureEvents() {
        if (_webView?.CoreWebView2 == null) return;

        _webView.CoreWebView2.ProcessFailed += OnProcessFailed;
        _webView.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true; // Block new windows
        _webView.CoreWebView2.DownloadStarting += (s, e) => e.Cancel = true; // Block downloads
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        _webView.CoreWebView2.NavigationStarting += (s, e) => {
            Log.Information("WebView2 NavigationStarting: {Uri}", e.Uri);
            if (!string.IsNullOrEmpty(_initialUrlSuffix) && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) {
                var query = uri.Query;
                const string prefix = "?fixture=";
                var idx = query.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) {
                    var fixtureName = query[(idx + prefix.Length)..];
                    var amp = fixtureName.IndexOf('&', StringComparison.Ordinal);
                    if (amp >= 0) fixtureName = fixtureName[..amp];
                    if (!string.IsNullOrWhiteSpace(fixtureName) && !string.Equals(fixtureName, _currentFixtureName, StringComparison.OrdinalIgnoreCase)) {
                        _currentFixtureName = fixtureName;
                        var isPinned = _coordinator.CurrentSnapshot?.Shell.Pinned ?? false;
                        var nextRev = _coordinator.NextRevision();
                        var snapshot = FixtureSnapshotProvider.GetSnapshot(fixtureName, isPinned, nextRev);
                        _coordinator.UpdateSnapshot(snapshot);
                        Log.Information("Test fixture selected via navigation: {FixtureName}, revision {Revision}", fixtureName, nextRev);
                    }
                }
            }
        };

        _webView.CoreWebView2.NavigationCompleted += async (s, e) => {
            if (e.IsSuccess) {
                Log.Information("WebView2 NavigationCompleted successfully: {Id}", e.NavigationId);
                _recoveryAttempts = 0;

                // Diagnostic: log the WebView's computed prefers-color-scheme
                try {
                    var darkResult = await _webView.CoreWebView2.ExecuteScriptAsync(
                        "window.matchMedia('(prefers-color-scheme: dark)').matches").ConfigureAwait(false);
                    var lightResult = await _webView.CoreWebView2.ExecuteScriptAsync(
                        "window.matchMedia('(prefers-color-scheme: light)').matches").ConfigureAwait(false);
                    var forcedResult = await _webView.CoreWebView2.ExecuteScriptAsync(
                        "window.matchMedia('(forced-colors: active)').matches").ConfigureAwait(false);
                    var htmlBg = await _webView.CoreWebView2.ExecuteScriptAsync(
                        "getComputedStyle(document.documentElement).backgroundColor").ConfigureAwait(false);
                    var bodyBg = await _webView.CoreWebView2.ExecuteScriptAsync(
                        "getComputedStyle(document.body).backgroundColor").ConfigureAwait(false);
                    var bodyColor = await _webView.CoreWebView2.ExecuteScriptAsync(
                        "getComputedStyle(document.body).color").ConfigureAwait(false);
                    Log.Information("WebView theme diagnostic: prefers-dark={Dark}, prefers-light={Light}, forced-colors={Forced}, html-bg={HtmlBg}, body-bg={BodyBg}, body-color={BodyColor}",
                        darkResult, lightResult, forcedResult, htmlBg, bodyBg, bodyColor);
                }
#pragma warning disable CA1031
                catch (Exception ex) {
                    Log.Warning(ex, "Failed to evaluate WebView theme diagnostic");
                }
#pragma warning restore CA1031
            }
            else {
                Log.Error("WebView2 NavigationCompleted failed. Status: {Status}, Error: {Error}", e.WebErrorStatus, e.WebErrorStatus);
            }
        };

        _webView.CoreWebView2.DOMContentLoaded += (s, e) => {
            Log.Information("WebView2 DOMContentLoaded: {Id}", e.NavigationId);
        };
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
        try {
            var json = e.WebMessageAsJson;
            _coordinator.HandleWebMessage(json);
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "Failed to handle WebMessage in WebViewHost");
        }
#pragma warning restore CA1031
    }

    public void PostWebMessageAsJson(string json) {
        if (_webView?.CoreWebView2 == null) {
            Log.Warning("Cannot post web message: CoreWebView2 is null.");
            return;
        }

        try {
            _webView.CoreWebView2.PostWebMessageAsJson(json);
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "Failed to post web message to WebView2");
        }
#pragma warning restore CA1031
    }

    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) {
        Log.Warning("WebView2 ProcessFailed: {Kind}, Reason: {Reason}", e.ProcessFailedKind, e.Reason);

        switch (e.ProcessFailedKind) {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // Browser process died. Complete recreation required.
                HandleFailure();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                // Renderer crashed or hung. Try to reload.
                if (_recoveryAttempts < MaxRecoveryAttempts && _webView?.CoreWebView2 != null) {
                    _recoveryAttempts++;
                    Log.Information("Attempting to reload renderer. Attempt {Attempt}", _recoveryAttempts);
                    _webView.Reload();
                }
                else {
                    HandleFailure();
                }
                break;
            case (CoreWebView2ProcessFailedKind)7: // GpuProcessExited (7 in older SDKs or named in newer)
            case (CoreWebView2ProcessFailedKind)8: // UtilityProcessExited (8)
            case (CoreWebView2ProcessFailedKind)9: // SandboxHelperProcessExited (9)
            case (CoreWebView2ProcessFailedKind)10: // GpuProcessHung (10)
            case (CoreWebView2ProcessFailedKind)4: // FrameRenderProcessExited
                // These processes are typically recovered automatically by the WebView2 browser process.
                Log.Information("Non-fatal or self-recovering process failure ({Kind}). No explicit recovery needed.", e.ProcessFailedKind);
                break;
            default:
                // Unknown/other failures default to complete recreation if they affect the app
                Log.Information("Unknown process failure ({Kind}). Will wait and see if it recovers, or we can handle it if needed.", e.ProcessFailedKind);
                break;
        }
    }

    private async void HandleFailure() {
        if (_recoveryAttempts >= MaxRecoveryAttempts) {
            Log.Error("Max WebView2 recovery attempts reached. Giving up.");
            return;
        }

        _recoveryAttempts++;
        Log.Information("Attempting complete recreation of WebView2. Attempt {Attempt}", _recoveryAttempts);

        DisposeWebView();
        // Wait briefly before recreating
        await Task.Delay(1000).ConfigureAwait(true);
        await InitializeAsync(_initialUrlSuffix).ConfigureAwait(true);
    }

    private void DisposeWebView() {
        if (_webView != null) {
            if (_webView.CoreWebView2 != null) {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.ProcessFailed -= OnProcessFailed;
            }
            _parent.Controls.Remove(_webView);
            _webView.Dispose();
            _webView = null;
        }
    }

    public void Dispose() {
        DisposeWebView();
    }
}
