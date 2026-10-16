using System.Runtime.InteropServices;

// Preserve M02's app-local runtime search policy, including SDK initialization.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
