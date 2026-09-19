#pragma warning disable CA1707

namespace AdbView.Tests.Integration.Chaos;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AdbView.Tests.Boundaries;
using Xunit;
using static AdbView.Tests.Boundaries.Repo;

/// <summary>PHASES Phase 9 audit: empty catch, Delay loops, lock in State/.</summary>
public sealed class ChaosAuditTests {
    private static readonly Regex EmptyBareCatch = new(
        @"catch\s*\{\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EmptySwallowAll = new(
        @"catch\s*\(\s*Exception(?:\s+\w+)?\s*\)\s*\{\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TaskDelayCall = new(
        @"Task\.Delay\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LockStatement = new(
        @"\block\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void No_Empty_Catch_Blocks_In_Product_Source() {
        var violations = new List<string>();
        foreach (var file in SourceFiles(RootFile("src", "AdbView"), [".cs"])) {
            var text = File.ReadAllText(file);
            foreach (Match m in EmptyBareCatch.Matches(text)) {
                violations.Add($"{Rel(file)}: bare empty catch {{}} at index {m.Index}");
            }

            foreach (Match m in EmptySwallowAll.Matches(text)) {
                violations.Add($"{Rel(file)}: empty catch (Exception) at index {m.Index}");
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Adb_And_State_TaskDelay_Only_As_Default_Delay_Injection() {
        // Sanctioned: `delay ?? Task.Delay` default injection and Backoff/tick owners.
        // Forbidden: bare `await Task.Delay(...)` loops inside Adb/ or State/.
        var roots = new[] {
            RootFile("src", "AdbView", "Adb"),
            RootFile("src", "AdbView", "State"),
        };
        var violations = new List<string>();
        foreach (var root in roots) {
            foreach (var file in SourceFiles(root, [".cs"])) {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++) {
                    string line = lines[i];
                    if (!TaskDelayCall.IsMatch(line)) {
                        continue;
                    }

                    // Allowed: default injection `delay ?? Task.Delay` / `_delay = delay ?? Task.Delay`
                    if (line.Contains("?? Task.Delay", StringComparison.Ordinal)
                        || line.Contains("delay ?? Task.Delay", StringComparison.Ordinal)) {
                        continue;
                    }

                    // Allowed: StateEngine constructor default `delay ?? Task.Delay` already covered;
                    // Backoff.cs may call Task.Delay through injected delegate only — flag raw awaits.
                    if (line.Contains("await Task.Delay", StringComparison.Ordinal)
                        || line.Contains("return Task.Delay", StringComparison.Ordinal)) {
                        violations.Add($"{Rel(file)}:{i + 1}: {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void State_Folder_Has_No_Lock_Statements() {
        var violations = new List<string>();
        foreach (var file in SourceFiles(RootFile("src", "AdbView", "State"), [".cs"])) {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++) {
                if (LockStatement.IsMatch(lines[i])) {
                    violations.Add($"{Rel(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    private static string Rel(string path) => Path.GetRelativePath(Root, path);
}
