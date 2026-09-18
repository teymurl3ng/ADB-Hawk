#pragma warning disable CA1707
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.IO;
using AdbView.Adb;
using Xunit;

public sealed class AdbExecutableLocatorTests {
    private readonly Dictionary<string, string> _env = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _existingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Environment.SpecialFolder, string> _specialFolders = new();

    private AdbExecutableLocator CreateLocator() {
        return new AdbExecutableLocator(
            getEnv: k => _env.TryGetValue(k, out var val) ? val : null,
            fileExists: path => _existingFiles.Contains(path) || _existingFiles.Contains(Path.GetFullPath(path)),
            getFolderPath: folder => _specialFolders.TryGetValue(folder, out var val) ? val : string.Empty);
    }

    [Fact]
    public void Locate_Precedence1_AndroidSdkRoot_BeatsAllOthers() {
        // Test N: Precedence 1–5
        _env["ANDROID_SDK_ROOT"] = @"C:\SdkRoot";
        _env["ANDROID_HOME"] = @"C:\AndroidHome";
        _env["PATH"] = @"C:\PathDir";
        _env["LOCALAPPDATA"] = @"C:\LocalAppData";
        _env["ProgramFiles"] = @"C:\ProgramFiles";

        _existingFiles.Add(@"C:\SdkRoot\platform-tools\adb.exe");
        _existingFiles.Add(@"C:\AndroidHome\platform-tools\adb.exe");
        _existingFiles.Add(@"C:\PathDir\adb.exe");
        _existingFiles.Add(@"C:\LocalAppData\Android\Sdk\platform-tools\adb.exe");
        _existingFiles.Add(@"C:\ProgramFiles\Android\android-sdk\platform-tools\adb.exe");

        var locator = CreateLocator();
        var result = locator.Locate();

        Assert.Equal(Path.GetFullPath(@"C:\SdkRoot\platform-tools\adb.exe"), result);
    }

    [Fact]
    public void Locate_Precedence2_AndroidHome_BeatsPathAndAppData() {
        _env["ANDROID_HOME"] = @"C:\AndroidHome";
        _env["PATH"] = @"C:\PathDir";
        _env["LOCALAPPDATA"] = @"C:\LocalAppData";

        _existingFiles.Add(@"C:\AndroidHome\platform-tools\adb.exe");
        _existingFiles.Add(@"C:\PathDir\adb.exe");
        _existingFiles.Add(@"C:\LocalAppData\Android\Sdk\platform-tools\adb.exe");

        var locator = CreateLocator();
        var result = locator.Locate();

        Assert.Equal(Path.GetFullPath(@"C:\AndroidHome\platform-tools\adb.exe"), result);
    }

    [Fact]
    public void Locate_Precedence3_Path_BeatsAppDataAndProgramFiles() {
        _env["PATH"] = @"C:\PathDir";
        _env["LOCALAPPDATA"] = @"C:\LocalAppData";
        _env["ProgramFiles"] = @"C:\ProgramFiles";

        _existingFiles.Add(@"C:\PathDir\adb.exe");
        _existingFiles.Add(@"C:\LocalAppData\Android\Sdk\platform-tools\adb.exe");
        _existingFiles.Add(@"C:\ProgramFiles\Android\android-sdk\platform-tools\adb.exe");

        var locator = CreateLocator();
        var result = locator.Locate();

        Assert.Equal(Path.GetFullPath(@"C:\PathDir\adb.exe"), result);
    }

    [Fact]
    public void Locate_Precedence4_LocalAppData_BeatsProgramFiles() {
        _env["LOCALAPPDATA"] = @"C:\LocalAppData";
        _env["ProgramFiles"] = @"C:\ProgramFiles";

        _existingFiles.Add(@"C:\LocalAppData\Android\Sdk\platform-tools\adb.exe");
        _existingFiles.Add(@"C:\ProgramFiles\Android\android-sdk\platform-tools\adb.exe");

        var locator = CreateLocator();
        var result = locator.Locate();

        Assert.Equal(Path.GetFullPath(@"C:\LocalAppData\Android\Sdk\platform-tools\adb.exe"), result);
    }

    [Fact]
    public void Locate_Precedence5_ProgramFiles_UsedWhenOthersAbsent() {
        _env["ProgramFiles"] = @"C:\ProgramFiles";
        _existingFiles.Add(@"C:\ProgramFiles\Android\android-sdk\platform-tools\adb.exe");

        var locator = CreateLocator();
        var result = locator.Locate();

        Assert.Equal(Path.GetFullPath(@"C:\ProgramFiles\Android\android-sdk\platform-tools\adb.exe"), result);
    }

    [Fact]
    public void Locate_HostileCwdAdb_IsIgnored() {
        // Test O: hostile adb.exe in current working directory is ignored
        string cwd = Directory.GetCurrentDirectory();
        string hostileAdb = Path.Combine(cwd, "adb.exe");
        _existingFiles.Add(hostileAdb);

        // PATH contains only a safe empty/non-adb directory
        _env["PATH"] = @"C:\SomeOtherFolder";

        var locator = CreateLocator();
        var result = locator.Locate();

        // Must return null, never the hostile CWD adb.exe
        Assert.Null(result);
    }

    [Fact]
    public void Locate_EmptyPathEntries_DoNotFallbackToCurrentDirectory() {
        // Empty entry in PATH (e.g. ";;C:\\bin;;") represents CWD on Windows/DOS
        string cwd = Directory.GetCurrentDirectory();
        _existingFiles.Add(Path.Combine(cwd, "adb.exe"));

        _env["PATH"] = ";; ;  ;C:\\EmptyBin;;";

        var locator = CreateLocator();
        var result = locator.Locate();

        Assert.Null(result);
    }

    [Fact]
    public void Locate_MalformedPathEntry_ToleratedWithoutThrowing() {
        _env["PATH"] = "C:\\ValidBin;C:\\Invalid|<>*?;C:\\AnotherBin";
        _existingFiles.Add(@"C:\AnotherBin\adb.exe");

        var locator = CreateLocator();
        var result = locator.Locate();

        Assert.Equal(Path.GetFullPath(@"C:\AnotherBin\adb.exe"), result);
    }

    [Fact]
    public void Locate_NoCandidateExists_ReturnsNull() {
        _env["ANDROID_SDK_ROOT"] = @"C:\MissingSdk";
        _env["PATH"] = @"C:\MissingPath";

        var locator = CreateLocator();
        var result = locator.Locate();

        Assert.Null(result);
    }
}
