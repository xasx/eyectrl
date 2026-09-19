namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NFluent;

/// <summary>
/// Source pins for model activation (ticket 8). Like the other census guards in this project
/// they pin spelling, not behaviour — <see cref="ModelManagementServiceTests"/> is the evidence.
///
/// <para>
/// The defect they guard: two writers of <c>Keyboard.MLModelSize</c> agreed only by accident,
/// and the download one was gated on a predicate (<c>ActiveModelId</c> empty or "Off") that the
/// registry's own seed — <c>ActiveModelId = settings.Keyboard.MLModelSize</c> — had already made
/// false for every default install. Downloads therefore never activated anything, and the id
/// they should have written is still seeded in a second file, so a second writer is exactly how
/// the disagreement comes back.
/// </para>
/// </summary>
public class ModelActivationWiringTests
{
    /// <summary>
    /// <c>MLModelSize</c> assignments. Case-sensitive and <c>=(?!=)</c> on purpose: the backing
    /// field declaration is <c>_mLModelSize = "360M"</c> (lower-case m), which is a distinct
    /// declaration, not a write to the property, and every equality comparison is a read.
    /// </summary>
    private static readonly Regex PropertyWrite = new(@"MLModelSize\s*=(?!=)", RegexOptions.Compiled);

    [Fact]
    public void MLModelSize_HasExactlyOneWriteSite()
    {
        var root = SourceScanner.RepoRoot().FullName;
        var sites = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs",
                     SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var text = SourceScanner.StripComments(File.ReadAllText(file));
            // One entry per WRITE, not per file: a second write beside the first in the same
            // service is the shape this guard exists for, and a per-file count cannot see it.
            foreach (Match _ in PropertyWrite.Matches(text))
                sites.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        Check.WithCustomMessage(
                "MLModelSize must have exactly ONE write site — ModelManagementService.SetActiveModel — "
                + "so Settings, both download paths and startup reconciliation cannot disagree about "
                + "which model is active. This defect is precisely two writers that could disagree: "
                + "the registry seeds ActiveModelId from this same setting, so a writer guarded by the "
                + "seed's own value can never fire. A second write landing anywhere else re-opens it.")
            .That(sites).HasSize(1);
        Check.WithCustomMessage(
                "The single write must stay in ModelManagementService, beside the registry-side "
                + "ActiveModelId write it has to stay in step with.")
            .That(sites[0]).IsEqualTo("src/EyeCtrl.App/Services/ModelManagementService.cs");
    }

    [Fact]
    public void Startup_ReconcilesTheActiveModel_BeforeTheKeyboardLoads()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "App.xaml.cs"));

        var reconcile = source.IndexOf("ReconcileActiveModel()", StringComparison.Ordinal);
        var loadKeyboard = source.IndexOf("LoadKeyboard(", reconcile < 0 ? 0 : reconcile,
            StringComparison.Ordinal);

        Check.WithCustomMessage(
                "Startup must reconcile a stored active-model id that names a model which is not on "
                + "disk. Without the call the fix only helps FUTURE downloads: an install whose "
                + "settings say \"360M\" while the model it downloaded is 1.5B keeps loading nothing.")
            .That(reconcile).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "Reconciliation must run before LoadKeyboard builds the prediction stack from the "
                + "same setting; after it, the repaired id only reaches the stack on the NEXT "
                + "session, which is one more session of the very symptom this fixes.")
            .That(reconcile).IsStrictlyLessThan(loadKeyboard);
    }
}
