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
            foreach (Match match in pattern.Matches(text)) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: WinForms type '{match.Value}' outside Host/");
            }

            if (text.Contains("System.Windows.Forms", StringComparison.Ordinal)) {
                violations.Add($"{Path.GetRelativePath(Root, file)}: 'System.Windows.Forms' referenced outside Host/");
            }
        }

        AssertNoViolations(violations);
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
}
