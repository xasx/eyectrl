namespace EyeCtrl.App.Tests.Localization;

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// Deployment completeness: the embedded English parses, and the loose German table and manifest
/// land beside the App exe in build output with the metadata that carries them into publish
/// output, loose (not swallowed by the single-file bundle).
/// </summary>
/// <remarks>
/// The publish half is asserted on the csproj metadata because a headless test cannot run
/// <c>dotnet publish</c>; the metadata is exactly what publish consumes, and the build half is
/// checked against the real App output folder, not only the transitive copy in this test's own
/// output.
/// </remarks>
public class LocalizationDeploymentTests
{
    private static readonly string[] LooseFiles =
    [
        StringTable.GermanFileName,
        StringTable.ManifestFileName
    ];

    private static string AppProjectDirectory =>
        Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App");

    /// <summary>
    /// The configuration this test binary was built under, so the App folder checked is the one
    /// that was actually built rather than a stale sibling.
    /// </summary>
    private static string Configuration()
    {
        var parts = AppContext.BaseDirectory.Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var bin = Array.LastIndexOf(parts, "bin");
        Check.WithCustomMessage($"Could not find a 'bin' segment in '{AppContext.BaseDirectory}'")
            .That(bin).IsStrictlyGreaterThan(-1);
        return parts[bin + 1];
    }

    [Fact]
    public void EmbeddedEnglish_Parses()
    {
        var content = LocalizationTestData.EnglishContent();
        Check.That(content).IsNotNull();
    }

    [Fact]
    public void LooseFiles_LandBesideTheAppExe_InBuildOutput()
    {
        var appOutput = Path.Combine(AppProjectDirectory, "bin", Configuration(), "net10.0-windows");

        Check.WithCustomMessage($"The App build output folder '{appOutput}' does not exist")
            .That(Directory.Exists(appOutput)).IsTrue();
        Check.That(File.Exists(Path.Combine(appOutput, "EyeCtrl.App.exe")))
            .As("the App exe exists in its build output").IsTrue();

        foreach (var name in LooseFiles)
        {
            Check.WithCustomMessage(
                    $"'{name}' is not beside the App exe in build output ({appOutput}); the German files "
                    + "must stay loose and be copied to the output root.")
                .That(File.Exists(Path.Combine(appOutput, name))).IsTrue();
        }

        // The same files also flow to this test's output through the ProjectReference, which is
        // what a Consumer of the App project sees.
        foreach (var name in LooseFiles)
        {
            Check.That(File.Exists(Path.Combine(AppContext.BaseDirectory, name))).IsTrue();
        }
    }

    [Fact]
    public void AppCsproj_ShipsThemLoose_ForBuildAndPublish()
    {
        var document = XDocument.Load(Path.Combine(AppProjectDirectory, "EyeCtrl.App.csproj"));

        foreach (var name in LooseFiles)
        {
            var item = document.Descendants("Content").SingleOrDefault(element =>
                ((string?)element.Attribute("Include"))?.EndsWith(name, StringComparison.OrdinalIgnoreCase) == true);

            Check.WithCustomMessage($"EyeCtrl.App.csproj has no <Content> item for '{name}'")
                .That(item).IsNotNull();
            Check.That((string?)item!.Element("Link"))
                .As($"'{name}' must be Linked to the output root, beside the App exe").IsEqualTo(name);
            Check.That((string?)item.Element("CopyToOutputDirectory"))
                .As($"{name} CopyToOutputDirectory").IsEqualTo("PreserveNewest");
            Check.That((string?)item.Element("CopyToPublishDirectory"))
                .As($"{name} CopyToPublishDirectory").IsEqualTo("PreserveNewest");
            Check.That((string?)item.Element("ExcludeFromSingleFile"))
                .As($"{name} must stay loose in a single-file publish").IsEqualTo("true");
        }
    }
}
