namespace EyeCtrl.Architecture.Tests;

using System.Xml.Linq;

/// <summary>
/// C9 S6f's publish-shape ratchet (the coordinator's condition on the App → EyeCtrl.Setup
/// reference): the App's single-file publish must carry NO loose <c>EyeCtrl.Setup.*</c> file.
/// The suppression is the narrow msbuild target in <c>EyeCtrl.App.csproj</c>; without this gate, a
/// later edit dropping it (or widening it to the managed dll, which the single-file bundler packs
/// and the installed App needs at type-load) would surface only at release time. The byte-level
/// result is pinned once in the S6f implementation report by a real
/// <c>Publish-Signed.ps1 -DryRun -NoSign</c> run; this test pins the declaration that produces it.
/// </summary>
public class AppPublishShapeTests
{
    private const string SuppressionTarget = "DropReferencedSetupStickerFilesFromPublish";

    private static XDocument AppProject()
    {
        var csproj = Path.Combine(FindRepoRoot(), "src", "EyeCtrl.App", "EyeCtrl.App.csproj");
        Check.WithCustomMessage($"EyeCtrl.App.csproj not found at {csproj}").That(File.Exists(csproj)).IsTrue();
        return XDocument.Load(csproj);
    }

    [Fact]
    public void App_Publish_Drops_Only_The_Referenced_Setup_Sticker_Files()
    {
        var document = AppProject();

        var target = document.Descendants()
            .Where(element => element.Name.LocalName == "Target")
            .SingleOrDefault(element => (string?)element.Attribute("Name") == SuppressionTarget);

        Check.WithCustomMessage(
            $"EyeCtrl.App.csproj must keep the {SuppressionTarget} target: the App publishes a " +
            "single file, and a loose EyeCtrl.Setup.* sticker beside it violates the S6f publish " +
            "contract (Publish-Signed refuses a colliding EyeCtrl.Setup.exe).")
            .That(target).IsNotNull();

        var afterTargets = (string?)target!.Attribute("AfterTargets") ?? "";
        Check.WithCustomMessage("The suppression must run on the publish item computation and after " +
                                "the single-file bundle is generated (an SDK reorder must not revive the loose file).")
            .That(afterTargets.Contains("ComputeFilesToPublish", StringComparison.Ordinal)).IsTrue();
        Check.That(afterTargets.Contains("GenerateSingleFileBundle", StringComparison.Ordinal)).IsTrue();

        var removals = target.Descendants()
            .Where(element => element.Name.LocalName == "ResolvedFileToPublish")
            .ToList();
        Check.That(removals).HasSize(1);

        var condition = (string?)removals[0].Attribute("Condition") ?? "";
        Check.WithCustomMessage("The suppression must still name the runtimeconfig sticker.")
            .That(condition.Contains("EyeCtrl.Setup.runtimeconfig.json", StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage("The suppression must still name the deps sticker.")
            .That(condition.Contains("EyeCtrl.Setup.deps.json", StringComparison.Ordinal)).IsTrue();

        // The dangerous edit: a blanket "EyeCtrl.Setup.*" (or a condition naming the dll) would
        // strip the MANAGED assembly from the publish item set before the bundler packs it, and
        // the installed App would fail at type-load with no test run ever noticing.
        Check.WithCustomMessage("The suppression must never drop the managed EyeCtrl.Setup.dll — " +
                                "the single-file bundler packs it, and the App loads its types at runtime.")
            .That(condition.Contains("EyeCtrl.Setup.dll", StringComparison.Ordinal)).IsFalse();
        Check.WithCustomMessage("Keep the suppression name-exact; a wildcard would silently cover the dll.")
            .That(condition.Contains("EyeCtrl.Setup.*", StringComparison.Ordinal)).IsFalse();
    }

    [Fact]
    public void App_Keeps_The_Setup_ProjectReference()
    {
        var document = AppProject();

        var references = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => ((string?)element.Attribute("Include") ?? "").Replace('\\', '/'))
            .ToList();

        Check.WithCustomMessage(
            "The App's S6f gateways read EyeCtrl.Setup's shapes (SetupJson/SetupResult, the inbox " +
            "scanner, the catalog verifier, SetupPaths); the reference is the coordinator-approved " +
            "option (a). The publish target above trims its sticker files, not the reference.")
            .That(references.Any(reference => reference.EndsWith(
                "EyeCtrl.Setup/EyeCtrl.Setup.csproj", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EyeCtrl.slnx")))
            directory = directory.Parent;

        Check.WithCustomMessage("EyeCtrl.slnx not found above the test bin.").That(directory).IsNotNull();
        return directory!.FullName;
    }
}
