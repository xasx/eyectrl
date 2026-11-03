namespace EyeCtrl.Setup.Tests;

using System.Text;
using System.Xml.Linq;
using EyeCtrl.Setup;

public class SetupManifestTests
{
    [Fact]
    public void TheSetupManifest_IsAsInvokerWithUiAccessFalse()
    {
        var document = XDocument.Parse(File.ReadAllText(ManifestPath));
        var privilege = document.Descendants()
            .Single(e => e.Name.LocalName == "requestedExecutionLevel");

        Check.That((string?)privilege.Attribute("level")).IsEqualTo("asInvoker");
        Check.That((string?)privilege.Attribute("uiAccess")).IsEqualTo("false");
    }

    [Fact]
    public void TheProject_SelectsThatManifest_AndNeverTheUiAccessOne()
    {
        var project = XDocument.Load(ProjectPath);
        var manifests = project.Descendants()
            .Where(e => e.Name.LocalName == "ApplicationManifest")
            .Select(e => Path.GetFileName(e.Value.Trim()))
            .ToList();

        Check.That(manifests).ContainsExactly("app.manifest");
        Check.That(File.ReadAllText(ProjectPath)).DoesNotContain("app.uiaccess.manifest");
        Check.That(project.Descendants().Any(e =>
            e.Name.LocalName.Equals("EyeCtrlUiAccess", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    [Fact]
    public void TheBuiltExe_EmbedsThePlainManifest()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EyeCtrl.Setup.exe");
        Check.WithCustomMessage($"the built Setup apphost is missing at {exe}")
            .That(File.Exists(exe)).IsTrue();

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(exe));
        Check.That(text).Contains("uiAccess=\"false\"");
        Check.That(text).DoesNotContain("uiAccess=\"true\"");
    }

    [Fact]
    public void TheSolution_ListsSetupAndItsTests()
    {
        var solution = File.ReadAllText(SolutionPath);

        Check.That(solution).Contains("src/EyeCtrl.Setup/EyeCtrl.Setup.csproj");
        Check.That(solution).Contains("tests/EyeCtrl.Setup.Tests/EyeCtrl.Setup.Tests.csproj");
    }

    [Fact]
    public void SetupStartsTheAppWithShellExecute_AndNeverCreateProcess()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "WindowsProcessControl.cs"));

        Check.That(source).Contains("UseShellExecute = true");
        Check.That(source).DoesNotContain("UseShellExecute = false");
    }

    [Fact]
    public void TheElevatedBroker_UsesShellExecuteRunas()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "ShellExecuteElevationBroker.cs"));

        Check.That(source).Contains("UseShellExecute = true");
        Check.That(source).Contains("Verb = \"runas\"");
    }

    [Fact]
    public void ThePinnedThumbprint_IsThePlansValue()
    {
        Check.That(SetupConstants.PinnedThumbprint)
            .IsEqualTo("C1039769A2D9ED01F93CEBC78B61176EE6B12F91");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EyeCtrl.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find EyeCtrl.slnx above " + AppContext.BaseDirectory);
    }

    private static string ProjectPath =>
        Path.Combine(RepoRoot(), "src", "EyeCtrl.Setup", "EyeCtrl.Setup.csproj");

    private static string ManifestPath =>
        Path.Combine(RepoRoot(), "src", "EyeCtrl.Setup", "app.manifest");

    private static string SolutionPath => Path.Combine(RepoRoot(), "EyeCtrl.slnx");
}
