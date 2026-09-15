namespace AdbView.Tests.Boundaries;

/// <summary>
/// Locates the repository root from the test output directory so the boundary tests
/// always scan the real source tree, never a copy.
/// </summary>
internal static class Repo {
    public static string Root { get; } = FindRoot();

    public static string RootFile(params string[] relative)
        => Path.Combine([Root, .. relative]);

    public static IReadOnlyList<string> SourceFiles(string root, string[] extensions) {
        if (!Directory.Exists(root)) {
            return [];
        }

        var excluded = new HashSet<string>(["bin", "obj", "node_modules", "dist", "wwwroot"], StringComparer.OrdinalIgnoreCase);
        var extensionSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
            var parts = Path.GetFullPath(file)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Skip(1).Any(excluded.Contains)) {
                continue;
            }

            if (extensionSet.Contains(Path.GetExtension(file))) {
                files.Add(file);
            }
        }

        return files;
    }

    private static string FindRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ADBView.sln"))) {
            dir = dir.Parent;
        }

        return dir is not null
            ? dir.FullName
            : throw new InvalidOperationException($"ADBView.sln not found in any parent of {AppContext.BaseDirectory}");
    }
}
