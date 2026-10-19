namespace AdbView.Tests.Boundaries;

using System.Xml.Linq;
using AdbView.WinUiMigration.Host;
using Xunit;

public sealed class CandidateHarnessTests {
    [Fact]
    public void FixtureOptionsUseDistinctGuidIdentityAndPreserveResultPath() {
        string suffix = Guid.NewGuid().ToString("N");
        string result = Path.GetFullPath("result directory/fixture.json");
        CandidateOptions options = CandidateOptions.Parse(["--fixture=empty", "--instance=" + suffix, "--verify=" + result]);
        Assert.Equal("empty", options.Fixture);
        Assert.Equal(result, options.ResultPath);
        Assert.Equal("AdbView.WinUiMigration.Fixture." + suffix, options.InstanceName);
        Assert.NotEqual(options.InstanceName, CandidateOptions.Parse(["--fixture=empty", "--instance=" + Guid.NewGuid().ToString("N")]).InstanceName);
    }

    [Theory]
    [InlineData("--startup")]
    [InlineData("--fixture=empty")]
    [InlineData("--instance=installed")]
    [InlineData("--verify=relative.json")]
    public void InvalidOrRepeatedOptionsFailBeforeUiInitialization(string invalid) {
        Assert.Throws<ArgumentException>(() => CandidateOptions.Parse([
            "--fixture=empty", "--instance=" + Guid.NewGuid().ToString("N"), invalid]));
        Assert.Throws<ArgumentException>(() => CandidateOptions.Parse([]));
    }

    [Fact]
    public void CandidateReferencesOnlyCoreAndUsesProvenSelfContainedShape() {
        var project = XDocument.Load(Repo.RootFile("src", "AdbView.WinUiMigration", "AdbView.WinUiMigration.csproj"));
        Assert.Equal("..\\AdbView.Core\\AdbView.Core.csproj", (string?)Assert.Single(project.Descendants("ProjectReference")).Attribute("Include"));
        foreach (string property in new[] { "UseWinUI", "WindowsAppSDKSelfContained", "SelfContained" }) {
            Assert.Equal("true", Assert.Single(project.Descendants(property)).Value);
        }
        Assert.Equal("false", Assert.Single(project.Descendants("PublishSingleFile")).Value);
        Assert.Equal("false", Assert.Single(project.Descendants("PublishTrimmed")).Value);
        Assert.Empty(project.Descendants("UseWPF"));
        Assert.Empty(project.Descendants("UseWindowsForms"));
        string program = File.ReadAllText(Repo.RootFile("src", "AdbView.WinUiMigration", "Program.cs"));
        Assert.True(program.IndexOf("CandidateOptions.Parse", StringComparison.Ordinal) < program.IndexOf("new Mutex", StringComparison.Ordinal));
        string[] sources = Repo.SourceFiles(Repo.RootFile("src", "AdbView.WinUiMigration"), [".cs"]).ToArray();
        Assert.All(sources, source => {
            string text = File.ReadAllText(source);
            if (Path.GetRelativePath(Repo.RootFile("src", "AdbView.WinUiMigration"), source).Replace('\\', '/') != "Host/LiveBackendSession.cs") {
                Assert.DoesNotContain("new BackendRuntime", text, StringComparison.Ordinal);
            }
            if (Path.GetRelativePath(Repo.RootFile("src", "AdbView.WinUiMigration"), source).Replace('\\', '/') is not ("Host/CandidateRegistryPreferenceStore.cs" or "Host/CandidateRegistryRunKeyStore.cs")) {
                Assert.DoesNotContain("Microsoft.Win32.Registry", text, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("System.Windows.Forms", text, StringComparison.Ordinal);
        });
        string window = File.ReadAllText(Repo.RootFile("src", "AdbView.WinUiMigration", "CandidateWindow.xaml.cs"));
        Assert.Contains("if (options.Live)", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new LiveBackendSession", File.ReadAllText(Repo.RootFile("src", "AdbView.WinUiMigration", "NativeUi", "DeviceFixtures.cs")), StringComparison.Ordinal);
        Assert.Contains("new UserPreferences(new CandidateRegistryPreferenceStore())", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new UserPreferences()", window, StringComparison.Ordinal);
        Assert.Equal(["..\\AdbView\\Host\\UserPreferences.cs", "..\\AdbView\\Host\\StartupRegistration.cs"], project.Descendants("Compile").Select(node => (string?)node.Attribute("Include")));
    }
}
