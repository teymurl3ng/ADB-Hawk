using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using static AdbView.Tests.Boundaries.Repo;

namespace AdbView.Tests.Boundaries;

/// <summary>
/// Repository/source-boundary invariants (AGENTS.md R-01, R-02, R-04, R-07, R-09,
/// R-13, R-15; ENGINEERING.md §3.1, §10.1, §10.6, §15; PHASES.md Phase 0).
/// Every test here fails loudly when its invariant is violated; none of them may be
/// loosened without a governing-document change.
/// </summary>
public sealed class BoundaryTests {
    private static readonly string[] CSharpExtensions = [".cs"];
    private static readonly string[] FrontendExtensions = [".ts", ".tsx", ".js", ".jsx", ".mts", ".cts"];

    private static string AppSourceRoot => RootFile("src", "AdbView");
    private static string HostBoundary => RootFile("src", "AdbView", "Host");
    private static string SystemClockFile => RootFile("src", "AdbView", "App", "SystemClock.cs");
    private static string UiSourceRoot => RootFile("ui", "src");
    private static string ActionsModule => RootFile("ui", "src", "bridge", "actions.ts");

    private static string ReadText(string path) => File.ReadAllText(path);

    private static void AssertNoViolations(List<string> violations) {
        Assert.True(
            violations.Count == 0,
            $"Boundary violated in {violations.Count} location(s):{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    // 1. No WinForms control types outside the Host/ boundary (R-04).
    [Fact]
    public void WinFormsControlTypesAppearOnlyInHostBoundary() {
        var controlTypes = new[]
        {
            "Form", "Control", "Button", "Label", "TextBox", "CheckBox", "RadioButton",
            "ComboBox", "ListBox", "CheckedListBox", "Panel", "PictureBox", "NotifyIcon",
            "ContextMenuStrip", "ToolStripMenuItem", "ToolStrip", "MenuStrip", "StatusStrip",
            "DataGridView", "ToolTip", "TabControl", "SplitContainer", "StatusBar",
        };
        var pattern = new Regex($@"\b({string.Join("|", controlTypes)})\b");
        var violations = new List<string>();

        foreach (var file in SourceFiles(AppSourceRoot, CSharpExtensions)) {
            if (file.StartsWith(HostBoundary, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var text = ReadText(file);

            // False positive correction: allow the exact IPC contract string (AdbView.Control.<session-id>)
            // without weakening the rule against actual WinForms Control type usage.
            var scrubbedText = text.Replace("\"AdbView.Control.", "\"AdbView.IPC_CONTRACT.", StringComparison.Ordinal)
                .Replace("string Label", "string FIELD_LABEL", StringComparison.Ordinal)
                .Replace("Label =", "FIELD_LABEL =", StringComparison.Ordinal);

            foreach (Match match in pattern.Matches(scrubbedText)) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: WinForms type '{match.Value}' outside Host/");
            }

            if (scrubbedText.Contains("System.Windows.Forms", StringComparison.Ordinal)) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: 'System.Windows.Forms' referenced outside Host/");
            }
        }

        AssertNoViolations(violations);
    }

    [Fact]
    public void BoundaryTestAllowsIpcContractButRejectsWinFormsControl() {
        var textAllowed = "var pipe = \"AdbView.Control.123\";";
        var textRejected = "public class MyControl : Control { }";

        var scrubbedAllowed = textAllowed.Replace("\"AdbView.Control.", "\"AdbView.IPC_CONTRACT.", StringComparison.Ordinal);
        var scrubbedRejected = textRejected.Replace("\"AdbView.Control.", "\"AdbView.IPC_CONTRACT.", StringComparison.Ordinal);

        var pattern = new Regex(@"\b(Control)\b");

        Assert.Empty(pattern.Matches(scrubbedAllowed));
        Assert.Single(pattern.Matches(scrubbedRejected));
    }

    [Fact]
    public void BoundaryTestAllowsStringLabelPropertyButRejectsWinFormsLabelControl() {
        var textAllowed = "public required string Label { get; init; }";
        var textRejected = "public class MyLabel : Label { }";

        var scrubbedAllowed = textAllowed.Replace("string Label", "string FIELD_LABEL", StringComparison.Ordinal);
        var scrubbedRejected = textRejected.Replace("string Label", "string FIELD_LABEL", StringComparison.Ordinal);

        var pattern = new Regex(@"\b(Label)\b");

        Assert.Empty(pattern.Matches(scrubbedAllowed));
        Assert.Single(pattern.Matches(scrubbedRejected));
    }

    // 2. No System.Net.Http usage or reference anywhere in application source (R-13).
    [Fact]
    public void SystemNetHttpIsAbsent() {
        var violations = new List<string>();
        foreach (var file in SourceFiles(RootFile("src"), CSharpExtensions)) {
            if (ReadText(file).Contains("System.Net.Http", StringComparison.Ordinal)) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: System.Net.Http is forbidden (no network access; R-13)");
            }
        }

        // Package and assembly references are checked structurally (XML elements),
        // so that policy comments naming the forbidden package do not trip the gate.
        foreach (var file in SourceFiles(Root, [".csproj", ".props"])) {
            var doc = XDocument.Load(file);
            var referenced = doc.Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion" or "Reference")
                .Select(e => (string?)e.Attribute("Include") ?? string.Empty);
            if (referenced.Any(name => name.Contains("System.Net.Http", StringComparison.Ordinal))) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: System.Net.Http is forbidden (no network access; R-13)");
            }
        }

        AssertNoViolations(violations);
    }

    // 3. No ambient clock reads outside SystemClock (ENGINEERING.md §7.2).
    [Fact]
    public void AmbientClockReadsAppearOnlyInSystemClock() {
        var pattern = new Regex(@"DateTime\.(Now|UtcNow)|DateTimeOffset\.UtcNow");
        var violations = new List<string>();

        foreach (var file in SourceFiles(AppSourceRoot, CSharpExtensions)) {
            if (file.Equals(SystemClockFile, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            foreach (Match match in pattern.Matches(ReadText(file))) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: '{match.Value}' outside App/SystemClock.cs");
            }
        }

        AssertNoViolations(violations);
    }

    // 4. postMessage only in ui/src/bridge/actions.ts (ENGINEERING.md §10.1).
    [Fact]
    public void PostMessageAppearsOnlyInBridgeActionsModule() {
        var violations = new List<string>();
        foreach (var file in SourceFiles(UiSourceRoot, FrontendExtensions)) {
            if (file.Equals(ActionsModule, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (ReadText(file).Contains("postMessage", StringComparison.Ordinal)) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: postMessage outside ui/src/bridge/actions.ts");
            }
        }

        AssertNoViolations(violations);
    }

    // 5. Forbidden component identifiers do not exist (DESIGN.md §16, R-02).
    [Fact]
    public void ForbiddenComponentIdentifiersAreAbsent() {
        var forbidden = new[] { "CompactDeviceCard", "DenseDeviceCard", "MultiDeviceCard", "MiniDeviceCard", "PinnedDeviceCard" };
        var pattern = new Regex($@"\b({string.Join("|", forbidden)})\b");
        var violations = new List<string>();

        foreach (var root in new[] { RootFile("src"), UiSourceRoot }) {
            foreach (var file in SourceFiles(root, [".cs", ".ts", ".tsx", ".js", ".jsx"])) {
                foreach (Match match in pattern.Matches(ReadText(file))) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: '{match.Value}' is a forbidden component identifier");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 6. DESIGN.md matches its committed authority hash (R-01).
    [Fact]
    public void DesignAuthorityHashMatches() {
        var authorityFile = RootFile("docs", "DESIGN.sha256");
        Assert.True(File.Exists(authorityFile), "docs/DESIGN.sha256 is missing; it is the committed design authority hash");

        var expected = ReadText(authorityFile).Trim();

        // Hash the canonical LF-normalized content. `.gitattributes` (`* text=auto`)
        // stores DESIGN.md LF-normalized in git, but a checkout may materialize CRLF
        // (e.g. GitHub Actions windows runners default to autocrlf=true). The
        // authority hash pins the versioned content, so it is computed over the
        // normalized bytes, independent of how the working copy was materialized.
        var raw = File.ReadAllBytes(RootFile("DESIGN.md"));
        using var normalized = new MemoryStream(raw.Length);
        for (var i = 0; i < raw.Length; i++) {
            if (raw[i] == (byte)'\r' && i + 1 < raw.Length && raw[i + 1] == (byte)'\n')
                continue;
            normalized.WriteByte(raw[i]);
        }
        var actual = Convert.ToHexString(SHA256.HashData(normalized.ToArray()));

        Assert.True(
            string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
            $"DESIGN.md no longer matches its authority hash. Expected {expected}, got {actual}. " +
            "DESIGN.md is frozen (AGENTS.md R-01): changing this hash is an explicit design-authority action, never a build fix.");
    }

    // 7. NuGet direct pins match ENGINEERING.md §15 / the Phase 0 research note, exactly (R-09).
    [Fact]
    public void NuGetPinsMatchEngineeringAuthority() {
        var expected = new Dictionary<string, string> {
            ["Microsoft.Web.WebView2"] = "1.0.4191.47",
            ["Serilog"] = "4.4.0",
            ["Serilog.Sinks.File"] = "7.0.0",
            ["Microsoft.Windows.CsWin32"] = "0.3.333",
            ["xunit.v3.mtp-v2"] = "4.0.0",
        };

        var doc = XDocument.Load(RootFile("Directory.Packages.props"));
        var actual = doc.Descendants("PackageVersion").ToDictionary(
            e => (string?)e.Attribute("Include") ?? string.Empty,
            e => (string?)e.Attribute("Version") ?? string.Empty);

        var problems = new List<string>();
        foreach (var (package, version) in expected) {
            if (!actual.TryGetValue(package, out var actualVersion)) {
                problems.Add($"{package}: pin missing from Directory.Packages.props");
            }
            else if (!string.Equals(version, actualVersion, StringComparison.Ordinal)) {
                problems.Add($"{package}: expected {version}, found {actualVersion}");
            }
        }

        foreach (var package in actual.Keys.Where(p => !expected.ContainsKey(p))) {
            problems.Add($"{package}: not in ENGINEERING.md §15 — adding a dependency requires a research note and a §15 edit in the same change");
        }

        // Central package management must actually be used: no csproj pins its own versions.
        foreach (var project in SourceFiles(Root, [".csproj"])) {
            var projectDoc = XDocument.Load(project);
            foreach (var reference in projectDoc.Descendants("PackageReference")) {
                if (reference.Attribute("Version") is not null || reference.Attribute("VersionOverride") is not null) {
                    problems.Add($"{Path.GetRelativePath(Root, project)}: PackageReference carries its own version; versions live only in Directory.Packages.props");
                }
            }
        }

        AssertNoViolations(problems);
    }

    // 8. npm direct dependencies are exact and match the Phase 0 research pins (R-09).
    [Fact]
    public void NpmDirectDependenciesAreExactAndPinned() {
        var expected = new Dictionary<string, string> {
            ["react"] = "19.3.0",
            ["react-dom"] = "19.3.0",
            ["@types/react"] = "19.3.0",
            ["@types/react-dom"] = "19.3.0",
            ["typescript"] = "6.0.3",
            ["vite"] = "8.2.2",
            ["@vitejs/plugin-react"] = "6.1.1",
            ["tailwindcss"] = "4.3.3",
            ["@tailwindcss/vite"] = "4.3.3",
            ["vitest"] = "5.0.1",
            ["@testing-library/react"] = "16.3.3",
            ["@testing-library/dom"] = "10.4.2",
            ["@testing-library/jest-dom"] = "7.0.1",
            ["jsdom"] = "30.0.1",
            ["@playwright/test"] = "1.63.0",
            ["eslint"] = "10.10.0",
            ["typescript-eslint"] = "8.70.0",
            ["eslint-plugin-react-hooks"] = "7.1.1",
            ["prettier"] = "3.9.6",
        };

        using var document = JsonDocument.Parse(ReadText(RootFile("ui", "package.json")));
        var root = document.RootElement;
        var actual = new Dictionary<string, string>();
        foreach (var section in new[] { "dependencies", "devDependencies" }) {
            if (!root.TryGetProperty(section, out var deps)) {
                continue;
            }

            foreach (var dep in deps.EnumerateObject()) {
                actual[dep.Name] = dep.Value.GetString() ?? string.Empty;
            }
        }

        var problems = new List<string>();
        foreach (var (name, version) in expected) {
            if (!actual.TryGetValue(name, out var actualVersion)) {
                problems.Add($"{name}: pin missing from ui/package.json");
            }
            else if (!string.Equals(version, actualVersion, StringComparison.Ordinal)) {
                problems.Add($"{name}: expected {version}, found {actualVersion}");
            }
        }

        foreach (var name in actual.Keys.Where(n => !expected.ContainsKey(n))) {
            problems.Add($"{name}: not in ENGINEERING.md §15 — adding a dependency requires a research note and a §15 edit in the same change");
        }

        // Exactness: no ranges, wildcards, tags, or aliases on any direct dependency.
        var exactVersion = new Regex(@"^\d+(\.\d+){1,3}$");
        foreach (var (name, version) in actual) {
            if (!exactVersion.IsMatch(version)) {
                problems.Add($"{name}: '{version}' is not an exact version; ranges, wildcards, and tags are forbidden (AGENTS.md R-09)");
            }
        }

        AssertNoViolations(problems);
    }

    // 9. VSTest compatibility packages are absent (Phase 0 research: MTP v2 only).
    [Fact]
    public void VSTestCompatibilityPackagesAreAbsent() {
        var forbidden = new[] { "Microsoft.NET.Test.Sdk", "xunit.runner.visualstudio" };
        var violations = new List<string>();

        // Structural check: only real <PackageReference>/<PackageVersion> elements count;
        // policy comments naming the forbidden packages must not trip the gate.
        foreach (var file in SourceFiles(Root, [".csproj", ".props"])) {
            var doc = XDocument.Load(file);
            var referenced = doc.Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion")
                .Select(e => (string?)e.Attribute("Include") ?? string.Empty);
            foreach (var package in forbidden.Where(p => referenced.Contains(p, StringComparer.Ordinal))) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: {package} must not be referenced (xunit.v3.mtp-v2 only; see research/2026-09-16-toolchain-and-gates.md)");
            }
        }

        AssertNoViolations(violations);
    }

    // 10. Built frontend output contains no absolute http(s) URL (ENGINEERING.md §10.6).
    [Fact]
    public void BuiltFrontendContainsNoAbsoluteHttpUrls() {
        var dist = RootFile("ui", "dist");
        Assert.True(
            Directory.Exists(dist) && File.Exists(Path.Combine(dist, "index.html")),
            "ui/dist is missing or incomplete — run scripts/build.ps1 before running the gate (the built frontend is inspected, not the sources)");

        // Inert, non-dereferenceable URL constants that bundlers legitimately embed.
        // Each entry is documented; anything else absolute fails the gate.
        var inertUrlPatterns = new[]
        {
            // XML/SVG/MathML namespace identifiers (React's renderer embeds these).
            @"http://www\.w3\.org/[^""'\s)<>]+",
            // React production error-code constants — developer diagnostics, never fetched.
            @"https://react\.dev/errors/\d*",
            // Tailwind's compiled-CSS license/credit comment — never fetched.
            @"https://tailwindcss\.com",
        };
        var absoluteUrl = new Regex(@"https?://");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dist, "*", SearchOption.AllDirectories)) {
            var text = ReadText(file);
            foreach (var pattern in inertUrlPatterns) {
                text = Regex.Replace(text, pattern, string.Empty);
            }

            if (absoluteUrl.IsMatch(text)) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: contains an absolute http(s) URL — production assets must be fully local (DESIGN.md §36)");
            }
        }

        AssertNoViolations(violations);
    }

    // 11. Production sources contain zero legacy bridge strings (R-04, Phase 3 Wave 2).
    [Fact]
    public void LegacyBridgeStringsAreAbsentFromProductionSource() {
        var legacyStrings = new[] { "toggle_pin", "pinned:true", "pinned:false" };
        var violations = new List<string>();

        foreach (var root in new[] { AppSourceRoot, UiSourceRoot }) {
            foreach (var file in SourceFiles(root, [".cs", ".ts", ".tsx", ".js", ".jsx"])) {
                if (file.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".test.tsx", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                var text = ReadText(file);
                foreach (var legacy in legacyStrings) {
                    if (text.Contains(legacy, StringComparison.Ordinal)) {
                        violations.Add($"{Path.GetRelativePath(Root, file)}: contains legacy bridge string '{legacy}'");
                    }
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 12. React components do not access WebView2 APIs directly (Phase 3 Wave 2).
    [Fact]
    public void ReactComponentsDoNotAccessWebViewApisDirectly() {
        var violations = new List<string>();
        var componentsDir = RootFile("ui", "src", "components");
        var appFile = RootFile("ui", "src", "App.tsx");

        var filesToCheck = SourceFiles(componentsDir, FrontendExtensions).Append(appFile);
        foreach (var file in filesToCheck) {
            var text = ReadText(file);
            if (text.Contains("chrome.webview", StringComparison.Ordinal) ||
                text.Contains("window.chrome", StringComparison.Ordinal)) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: direct access to WebView API (must use bridge module)");
            }
        }

        AssertNoViolations(violations);
    }

    // 13. Production frontend sources contain no browser-owned fixture generators (R-07, Phase 3 Wave 3).
    [Fact]
    public void FrontendFixtureGeneratorsAreAbsentFromProductionSource() {
        var forbiddenGenerators = new[] { "getFixtureDevices" };
        var violations = new List<string>();

        foreach (var file in SourceFiles(UiSourceRoot, FrontendExtensions)) {
            if (file.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".test.tsx", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("__fixtures__", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var text = ReadText(file);
            foreach (var generator in forbiddenGenerators) {
                if (text.Contains(generator, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: contains forbidden fixture generator '{generator}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 14. Native WebView authority guard in useSnapshot: production path must never fall back to static fixtures (R-06, R-07).
    [Fact]
    public void NativeWebViewAuthorityGuardIsEnforcedInUseSnapshot() {
        var useSnapshotPath = RootFile("ui", "src", "bridge", "useSnapshot.ts");
        var text = ReadText(useSnapshotPath);

        // Prove that window.chrome?.webview presence explicitly blocks fixture fallback
        Assert.Contains("if (window.chrome?.webview) return null;", text, StringComparison.Ordinal);
        // Prove that standalone fixture fallback is gated behind isTestMode()
        Assert.Contains("if (!isTestMode()) return null;", text, StringComparison.Ordinal);
    }

    // 15. Adb.Protocol does not reference forbidden layers (R-04, Phase 4 Wave 4A/4B).
    [Fact]
    public void AdbProtocolDoesNotReferenceForbiddenLayers() {
        var protocolDir = RootFile("src", "AdbView", "Adb", "Protocol");
        var forbiddenTokens = new[]
        {
            "AdbView.State",
            "AppSnapshot",
            "AdbView.Bridge",
            "AdbView.Host",
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "AdbExecutableLocator",
            "AdbServerSupervisor",
            "AdbCapabilities",
            "AdbServerStatus",
            "RawDevice",
            "DeviceView",
            "System.Net.Http",
        };

        var violations = new List<string>();
        foreach (var file in SourceFiles(protocolDir, CSharpExtensions)) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 16. Adb capability layer does not reference forbidden layers (R-04, Phase 4 Wave 4B).
    [Fact]
    public void AdbCapabilityLayerDoesNotReferenceForbiddenLayers() {
        var capabilityFiles = new[]
        {
            RootFile("src", "AdbView", "Adb", "AdbCapabilities.cs"),
            RootFile("src", "AdbView", "Adb", "AdbServerStatus.cs"),
        };
        var forbiddenTokens = new[]
        {
            "AdbView.State",
            "AppSnapshot",
            "AdbView.Bridge",
            "AdbView.Host",
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "AdbExecutableLocator",
            "AdbServerSupervisor",
            "RawDevice",
            "DeviceView",
            "System.Net.Http",
        };

        var violations = new List<string>();
        foreach (var file in capabilityFiles) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 17. Adb lifecycle layer does not reference forbidden layers (R-04, Phase 4 Wave 4C).
    [Fact]
    public void AdbLifecycleLayerDoesNotReferenceForbiddenLayers() {
        var lifecycleFiles = new[]
        {
            RootFile("src", "AdbView", "Adb", "AdbExecutableLocator.cs"),
            RootFile("src", "AdbView", "Adb", "AdbProcessRunner.cs"),
            RootFile("src", "AdbView", "Adb", "AdbServerSupervisor.cs"),
            RootFile("src", "AdbView", "Adb", "Backoff.cs"),
            RootFile("src", "AdbView", "Adb", "Model", "AdbServerPhase.cs"),
            RootFile("src", "AdbView", "Adb", "Model", "AdbServerStateChanged.cs"),
        };

        var forbiddenTokens = new[]
        {
            "AdbView.State",
            "AppSnapshot",
            "AdbView.Bridge",
            "AdbView.Host",
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "RawDevice",
            "DeviceView",
            "DeviceTracker",
            "System.Net.Http",
        };
        // Decision 0008 amendment (2026-10-01): the single sanctioned kill lives in
        // AdbProcessRunner.cs only (user-initiated ADB restart). Everywhere else stays forbidden.
        var killTokens = new[]
        {
            "host:kill",
            "kill-server",
        };
        var killExemptFile = RootFile("src", "AdbView", "Adb", "AdbProcessRunner.cs");

        var violations = new List<string>();
        foreach (var file in lifecycleFiles) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }

            if (file == killExemptFile) {
                continue;
            }

            foreach (var token in killTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 18. State core does not reference transport sockets/UI/host (R-04).
    // Phase 5C: RawDevice / DeviceListReceived / DeviceTrackerFailed* event names are allowed;
    // framing, sockets, and Host/Bridge remain forbidden.
    [Fact]
    public void StateCoreDoesNotReferenceForbiddenLayers() {
        var stateFiles = new[]
        {
            RootFile("src", "AdbView", "State", "StateReducer.cs"),
            RootFile("src", "AdbView", "State", "StateEngine.cs"),
            RootFile("src", "AdbView", "State", "Vocabulary.cs"),
            RootFile("src", "AdbView", "State", "AdbViewEvent.cs"),
            RootFile("src", "AdbView", "State", "DeviceViewMapper.cs"),
            RootFile("src", "AdbView", "State", "TrayStatusAggregator.cs"),
        };

        var forbiddenTokens = new[]
        {
            "System.Net.Sockets",
            "System.Diagnostics.Process",
            "AdbHostConnection",
            "Microsoft.Web.WebView2",
            "System.Windows.Forms",
            "AdbView.Host",
            "AdbView.Bridge",
            "System.Net.Http",
            "track-devices",
            "ExecuteFramedStreamAsync",
            "TcpClient",
        };

        var violations = new List<string>();
        foreach (var file in stateFiles) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 19. Device tracker / proto parser layer does not reference State/UI (Phase 5A).
    [Fact]
    public void DeviceTrackerLayerDoesNotReferenceForbiddenLayers() {
        var trackerFiles = new[]
        {
            RootFile("src", "AdbView", "Adb", "DeviceTracker.cs"),
            RootFile("src", "AdbView", "Adb", "DevicesProtoParser.cs"),
            RootFile("src", "AdbView", "Adb", "TrackDevicesLParser.cs"),
            RootFile("src", "AdbView", "Adb", "DeviceTrackerBackoff.cs"),
            RootFile("src", "AdbView", "Adb", "Model", "RawDevice.cs"),
        };

        var forbiddenTokens = new[]
        {
            "AdbView.State",
            "StateEngine",
            "StateReducer",
            "AppSnapshot",
            "DeviceView",
            "DeviceIdentity",
            "NormalizedDevice",
            "DeviceSemanticState",
            "AdbView.Bridge",
            "BridgeCoordinator",
            "AdbView.Host",
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "System.Net.Http",
            "Google.Protobuf",
        };

        var violations = new List<string>();
        foreach (var file in trackerFiles) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 19b. MdnsTracker / parsers do not reference State/UI (Phase 6A).
    [Fact]
    public void MdnsTrackerLayerDoesNotReferenceForbiddenLayers() {
        var trackerFiles = new[]
        {
            RootFile("src", "AdbView", "Adb", "MdnsTracker.cs"),
            RootFile("src", "AdbView", "Adb", "MdnsTrackerBackoff.cs"),
            RootFile("src", "AdbView", "Adb", "MdnsServicesProtoParser.cs"),
            RootFile("src", "AdbView", "Adb", "MdnsServicesProtoTextParser.cs"),
            RootFile("src", "AdbView", "Adb", "MdnsServicesListParser.cs"),
            RootFile("src", "AdbView", "Adb", "MdnsServiceTypes.cs"),
            RootFile("src", "AdbView", "Adb", "MdnsTrackServicesCli.cs"),
            RootFile("src", "AdbView", "Adb", "Model", "RawMdnsService.cs"),
        };

        var forbiddenTokens = new[]
        {
            "AdbView.State",
            "StateEngine",
            "StateReducer",
            "AppSnapshot",
            "PairingOfferView",
            "PairingOperationView",
            "DeviceView",
            "AdbView.Bridge",
            "BridgeCoordinator",
            "AdbView.Host",
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "System.Net.Http",
            "Google.Protobuf",
            "flyout",
        };

        var violations = new List<string>();
        foreach (var file in trackerFiles) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 19c. PairingCoordinator / host:pair client do not reference State/UI (Phase 6B).
    [Fact]
    public void PairingCoordinatorLayerDoesNotReferenceForbiddenLayers() {
        var files = new[]
        {
            RootFile("src", "AdbView", "Adb", "PairingCoordinator.cs"),
            RootFile("src", "AdbView", "Adb", "HostPairClient.cs"),
            RootFile("src", "AdbView", "Adb", "PairCliFallback.cs"),
            RootFile("src", "AdbView", "Adb", "PairingEndpoint.cs"),
            RootFile("src", "AdbView", "Adb", "PairingCode.cs"),
            RootFile("src", "AdbView", "Adb", "Model", "PairingProgressed.cs"),
        };

        var forbiddenTokens = new[]
        {
            "AdbView.State",
            "StateEngine",
            "StateReducer",
            "AppSnapshot",
            "PairingOfferView",
            "PairingOperationView",
            "DeviceView",
            "AdbView.Bridge",
            "BridgeCoordinator",
            "AdbView.Host",
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "System.Net.Http",
            "Google.Protobuf",
        };

        var violations = new List<string>();
        foreach (var file in files) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 19e. Phase 7A metadata probe domain does not reference State/UI/Bridge.
    [Fact]
    public void MetadataProbeLayerDoesNotReferenceForbiddenLayers() {
        var files = new[]
        {
            RootFile("src", "AdbView", "Adb", "MetadataProbe.cs"),
            RootFile("src", "AdbView", "Adb", "MetadataDeviceShellClient.cs"),
            RootFile("src", "AdbView", "Adb", "MetadataParsers.cs"),
            RootFile("src", "AdbView", "Adb", "Model", "MetadataProbeModels.cs"),
        };

        var forbiddenTokens = new[]
        {
            "AdbView.State",
            "StateEngine",
            "StateReducer",
            "AppSnapshot",
            "DeviceView",
            "DeviceViewMapper",
            "AdbView.Bridge",
            "BridgeCoordinator",
            "FixtureSnapshotProvider",
            "AdbView.Host",
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "System.Net.Http",
            "System.Diagnostics.Process",
            "NotificationPolicy",
            "Google.Protobuf",
        };

        var violations = new List<string>();
        foreach (var file in files) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 19g. Phase 8A notification domain stays free of Host/ADB protocol/Windows UI.
    [Fact]
    public void NotificationDomainDoesNotReferenceForbiddenLayers() {
        var dir = RootFile("src", "AdbView", "Notifications");
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            : Array.Empty<string>();

        var forbiddenTokens = new[]
        {
            "System.Windows.Forms",
            "NotifyIcon",
            "ShowBalloonTip",
            "Microsoft.Web.WebView2",
            "BridgeCoordinator",
            "DeviceTracker",
            "MdnsTracker",
            "MetadataProbe",
            "MetadataDeviceShellClient",
            "host:transport",
            "System.Net.Http",
            "Shell_NotifyIcon",
        };

        var violations = new List<string>();
        foreach (var file in files) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 19f. Phase 7B metadata lifecycle does not reference WinForms/WebView/Bridge protocol.
    [Fact]
    public void DeviceMetadataLifecycleDoesNotReferenceForbiddenLayers() {
        var files = new[]
        {
            RootFile("src", "AdbView", "Host", "DeviceMetadataLifecycle.cs"),
        };

        var forbiddenTokens = new[]
        {
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "BridgeCoordinator",
            "BridgeSerializer",
            "WebViewHost",
            "FlyoutWindow",
            "NotificationPolicy",
            "System.Net.Http",
        };

        var violations = new List<string>();
        foreach (var file in files) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 19d. Phase 6C Host lifecycles may post State events but Adb trackers stay free of State.
    [Fact]
    public void Phase6CAdbTrackersStillDoNotReferenceState() {
        var files = new[]
        {
            RootFile("src", "AdbView", "Adb", "MdnsTracker.cs"),
            RootFile("src", "AdbView", "Adb", "PairingCoordinator.cs"),
            RootFile("src", "AdbView", "Adb", "HostPairClient.cs"),
        };
        var forbidden = new[] { "AdbView.State", "StateEngine", "AppSnapshot", "PairingOfferView" };
        var violations = new List<string>();
        foreach (var file in files) {
            var text = ReadText(file);
            foreach (var token in forbidden) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 20. Adb.Protocol still must not reference DeviceTracker / RawDevice (Phase 5A).
    [Fact]
    public void AdbProtocolDoesNotReferenceDeviceTrackerOrRawDevice() {
        var protocolDir = RootFile("src", "AdbView", "Adb", "Protocol");
        var forbiddenTokens = new[] { "DeviceTracker", "RawDevice", "DevicesProtoParser", "TrackDevicesLParser", "MdnsTracker", "RawMdnsService", "MdnsServicesProtoParser" };
        var violations = new List<string>();
        foreach (var file in SourceFiles(protocolDir, CSharpExtensions)) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references '{token}'");
                }
            }
        }
        AssertNoViolations(violations);
    }

    // 21. DeviceIdentity / NormalizedDevice do not reference State/UI (Phase 5B).
    [Fact]
    public void DeviceIdentityLayerDoesNotReferenceForbiddenLayers() {
        var identityFiles = new[]
        {
            RootFile("src", "AdbView", "Adb", "DeviceIdentity.cs"),
            RootFile("src", "AdbView", "Adb", "Model", "NormalizedDevice.cs"),
        };

        var forbiddenTokens = new[]
        {
            "AdbView.State",
            "StateEngine",
            "StateReducer",
            "AppSnapshot",
            "DeviceView",
            "AdbView.Bridge",
            "BridgeCoordinator",
            "AdbView.Host",
            "System.Windows.Forms",
            "Microsoft.Web.WebView2",
            "System.Net.Http",
            "Google.Protobuf",
        };

        var violations = new List<string>();
        foreach (var file in identityFiles) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references forbidden layer token '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 22. Adb.Protocol still must not reference DeviceIdentity / NormalizedDevice (Phase 5B).
    [Fact]
    public void AdbProtocolDoesNotReferenceDeviceIdentity() {
        var protocolDir = RootFile("src", "AdbView", "Adb", "Protocol");
        var forbiddenTokens = new[] { "DeviceIdentity", "NormalizedDevice", "DeviceSemanticState", "DeviceTransport" };
        var violations = new List<string>();
        foreach (var file in SourceFiles(protocolDir, CSharpExtensions)) {
            var text = ReadText(file);
            foreach (var token in forbiddenTokens) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references '{token}'");
                }
            }
        }
        AssertNoViolations(violations);
    }

    // 23. DeviceTracker remains unaware of State (Phase 5C).
    [Fact]
    public void DeviceTrackerDoesNotReferenceStateEngine() {
        var files = new[]
        {
            RootFile("src", "AdbView", "Adb", "DeviceTracker.cs"),
            RootFile("src", "AdbView", "Adb", "DevicesProtoParser.cs"),
            RootFile("src", "AdbView", "Adb", "TrackDevicesLParser.cs"),
            RootFile("src", "AdbView", "Adb", "DeviceIdentity.cs"),
        };

        var forbidden = new[] { "StateEngine", "StateReducer", "AppSnapshot", "AdbView.State", "BridgeCoordinator" };
        var violations = new List<string>();
        foreach (var file in files) {
            var text = ReadText(file);
            foreach (var token in forbidden) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }

    // 24. StateReducer must not open sockets / ADB framing (Phase 5C).
    [Fact]
    public void StateReducerDoesNotReferenceSocketsOrFraming() {
        var files = new[]
        {
            RootFile("src", "AdbView", "Adb", "DeviceIdentity.cs"), // identity ok
            RootFile("src", "AdbView", "State", "StateReducer.cs"),
            RootFile("src", "AdbView", "State", "DeviceViewMapper.cs"),
            RootFile("src", "AdbView", "State", "TrayStatusAggregator.cs"),
        };

        // Only assert State files for socket/framing tokens.
        var stateFiles = new[]
        {
            RootFile("src", "AdbView", "State", "StateReducer.cs"),
            RootFile("src", "AdbView", "State", "DeviceViewMapper.cs"),
            RootFile("src", "AdbView", "State", "TrayStatusAggregator.cs"),
            RootFile("src", "AdbView", "State", "Vocabulary.cs"),
        };

        var forbidden = new[]
        {
            "AdbHostConnection",
            "TcpClient",
            "Socket",
            "ExecuteFramedStreamAsync",
            "System.Net.Sockets",
        };

        var violations = new List<string>();
        foreach (var file in stateFiles) {
            var text = ReadText(file);
            foreach (var token in forbidden) {
                if (text.Contains(token, StringComparison.Ordinal)) {
                    violations.Add($"{Path.GetRelativePath(Root, file)}: references '{token}'");
                }
            }
        }

        AssertNoViolations(violations);
    }
}

