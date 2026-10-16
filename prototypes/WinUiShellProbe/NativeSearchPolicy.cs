using System.Runtime.InteropServices;

// Includes the SDK-injected self-contained runtime initializer. SafeDirectories
// excludes current-directory/PATH lookup while supporting the app-local runtime.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
