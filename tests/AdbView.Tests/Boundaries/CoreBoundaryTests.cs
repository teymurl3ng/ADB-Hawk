namespace AdbView.Tests.Boundaries;

using System.Reflection;
using System.Xml.Linq;
using AdbView.Adb;
using AdbView.Bridge;
using AdbView.Host;
using AdbView.NativeUi;
using AdbView.NativeUi.Shell;
using AdbView.Notifications;
using AdbView.State;
using Xunit;

public sealed class CoreBoundaryTests {
    private static bool IsDesktopReference(string name) =>
        name is "AdbView" or "PresentationCore" or "PresentationFramework" or "WindowsBase"
        || name.StartsWith("System.Windows", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.UI", StringComparison.Ordinal)
        || name.StartsWith("Windows.Win32", StringComparison.Ordinal);

    [Theory]
    [InlineData("AdbView", true)]
    [InlineData("PresentationFramework", true)]
    [InlineData("WindowsBase", true)]
    [InlineData("System.Windows.Forms.Primitives", true)]
    [InlineData("Microsoft.UI.Xaml", true)]
    [InlineData("Windows.Win32", true)]
    [InlineData("Serilog", false)]
    [InlineData("System.Drawing.Primitives", false)]
    public void CoreReferenceGuardRejectsDesktopDependencies(string name, bool forbidden) {
        ArgumentNullException.ThrowIfNull(name);
        Assert.Equal(forbidden, IsDesktopReference(name));
    }

    [Fact]
    public void CompiledCoreHasNoDesktopOrExecutableDependency() {
        var assembly = typeof(StateEngine).Assembly;
        Assert.Equal("AdbView.Core", assembly.GetName().Name);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => IsDesktopReference(reference.Name!));
        foreach (var type in assembly.GetTypes()) {
            Assert.DoesNotContain(
                type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance),
                method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0);
        }
    }

    [Fact]
    public void CoreProjectHasNoDesktopSdkNativeGeneratorOrProjectBackReference() {
        var project = XDocument.Load(Repo.RootFile("src", "AdbView.Core", "AdbView.Core.csproj"));
        Assert.Equal("Microsoft.NET.Sdk", (string?)project.Root!.Attribute("Sdk"));
        Assert.Equal("net10.0-windows", project.Descendants("TargetFramework").Single().Value);
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.DoesNotContain(project.Descendants(), element =>
            element.Name.LocalName is "UseWPF" or "UseWindowsForms" or "UseWinUI"
            && string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(project.Descendants("PackageReference"), element =>
            ((string?)element.Attribute("Include"))?.Contains("CsWin32", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void BackendAndPurePoliciesHaveOneOwnerInCore() {
        var expected = typeof(StateEngine).Assembly;
        Type[] types = [typeof(AdbServerSupervisor), typeof(NotificationCoordinator), typeof(BackendRuntime),
            typeof(FixtureSnapshotProvider), typeof(DeviceMetadataLifecycle), typeof(StatusBubblePolicy),
            typeof(StatusBubblePresenter), typeof(ShellSizing), typeof(AgeFormatter)];
        Assert.All(types, type => Assert.Same(expected, type.Assembly));
        var appProject = XDocument.Load(Repo.RootFile("src", "AdbView", "AdbView.csproj"));
        Assert.Contains(appProject.Descendants("ProjectReference"), element =>
            ((string?)element.Attribute("Include"))?.Contains("AdbView.Core", StringComparison.Ordinal) == true);
    }
}
