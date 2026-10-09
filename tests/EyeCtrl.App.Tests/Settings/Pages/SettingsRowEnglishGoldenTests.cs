namespace EyeCtrl.App.Tests.Settings.Pages;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// T07 fixup-02 R2: a checked-in English golden for every Settings row, in build order — the row's
/// label, its description and, for buttons, the modal message and choices the activation shows.
/// It is what kills the wrong-site swaps the row tests could not see (review 02 S04, S05, S11,
/// S12, S16, S17, S22, S31, S34): the Review-All modal asking the Reset General question, a row
/// reading another row's text.
///
/// <para>
/// The expectations never come from <c>T(key)</c>: the file holds the approved English and the
/// capture reads what the page sources resolve through the embedded English table. Regenerate
/// after a deliberate text move with
/// <c>EYECTRL_WRITE_SETTINGS_GOLDEN=1 dotnet test --filter RegenerateSettingsRowGolden</c>.
/// </para>
///
/// <para>
/// The capture forces English with <see cref="StringTable.ResetToEmbeddedEnglish"/> and never
/// activates another table, so it needs no exclusive collection (the observer hook reddens any
/// test that would start on a leaked non-English global).
/// </para>
/// </summary>
public class SettingsRowEnglishGoldenTests
{
    /// <summary>Set to 1 to rewrite the golden from the current tree. Never set in CI.</summary>
    public const string WriteEnvironmentVariable = "EYECTRL_WRITE_SETTINGS_GOLDEN";

    private const string GoldenRelativePath = "localization/settings-row-english.golden.json";

    private const string Meta =
        "Approved English for every Settings row, in build order per page source: the label, the "
        + "description, and for buttons the modal message and choices the activation shows. "
        + "Regenerate with EYECTRL_WRITE_SETTINGS_GOLDEN=1; never compute an expectation with T(key).";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal sealed record GoldenFile(
        [property: JsonPropertyName("_meta")] string Meta,
        IReadOnlyList<GoldenSource> Sources);

    internal sealed record GoldenSource(string Name, IReadOnlyList<GoldenRow> Rows);

    internal sealed record GoldenRow(string Id, string Label, string? Description, GoldenModal? Modal);

    internal sealed record GoldenModal(string Message, IReadOnlyList<string> Choices);

    [Fact]
    public void EverySettingsRow_KeepsItsApprovedEnglish_InOrder()
    {
        var expected = LoadGolden();
        var actual = CaptureAllSources();

        var differences = Differences(actual, expected.Sources);
        Check.WithCustomMessage(
                "The Settings rows' English drifted from the approved golden. If the move was "
                + "deliberate, regenerate with "
                + $"{WriteEnvironmentVariable}=1 dotnet test --filter RegenerateSettingsRowGolden.\n  "
                + string.Join("\n  ", differences))
            .That(differences).IsEmpty();
    }

    [Fact]
    public void RegenerateSettingsRowGolden_WhenTheEnvironmentVariableAsks()
    {
        if (Environment.GetEnvironmentVariable(WriteEnvironmentVariable) != "1") return;

        var file = new GoldenFile(Meta, CaptureAllSources());
        File.WriteAllText(GoldenPath(), JsonSerializer.Serialize(file, JsonOptions));
    }

    // ────── The capture ──────

    /// <summary>
    /// Every page source, built with its real fixture, in a stable order. Source captures that
    /// navigate (Models → Manage) are ordered; each source gets its own settings/services so one
    /// source's activations cannot leak into the next.
    /// </summary>
    private static List<GoldenSource> CaptureAllSources()
    {
        StringTable.ResetToEmbeddedEnglish();
        var sources = new List<GoldenSource>();

        void Plain(string name, Func<SettingsPageContext, ISettingsPageSource> factory)
        {
            var recorder = new RecordingSettingsPageContext();
            Capture(sources, name, recorder, factory(recorder.Context));
        }

        Plain("Cursor", context => new CursorPageSource(context));
        Plain("Dwell", context => new DwellPageSource(context));
        Plain("Selection", context => new SelectionPageSource(context));
        Plain("Layout", context => new LayoutPageSource(context));
        Plain("ActionMenu", context => new ActionMenuPageSource(context));
        Plain("Keyboard", context => new KeyboardPageSource(context));
        Plain("Toolbar", context => new ToolbarPageSource(context));
        Plain("Calibration", context => new CalibrationPageSource(context));
        Plain("EyeCare", context => new EyeCarePageSource(context));
        Plain("Adaptation", context => new AdaptationPageSource(context));
        Plain("Advanced", context => new AdvancedPageSource(context));
        Plain("Diagnostics", context => new DiagnosticsPageSource(context));

        CaptureDictionary(sources);
        CaptureGeneral(sources);
        CaptureQuickActions(sources);
        CaptureModels(sources);
        return sources;
    }

    private static void Capture(
        List<GoldenSource> sources, string name, RecordingSettingsPageContext recorder,
        ISettingsPageSource source) =>
        sources.Add(new GoldenSource(name, CaptureRows(source.Build(), recorder)));

    /// <summary>
    /// One row per spec, in order. For a button, the activation is driven through the recorder so
    /// the modal IT shows is attributed to its row: the two-button form lands in
    /// <see cref="RecordingSettingsPageContext.LastModalMessage"/>, the three-choice form on the
    /// view model (<c>ModalMessage</c>/<c>ModalChoices</c>).
    /// </summary>
    private static List<GoldenRow> CaptureRows(
        IReadOnlyList<PageSpec> pages, RecordingSettingsPageContext recorder)
    {
        var rows = new List<GoldenRow>();
        foreach (var spec in pages.SelectMany(page => page.Rows))
        {
            var label = spec is LabelSpec labelSpec ? labelSpec.Text() : spec.Label;
            GoldenModal? modal = null;

            if (spec is ButtonSpec button)
            {
                recorder.ClearModalCapture();
                button.OnActivate();

                var viewModel = recorder.Context.ViewModel;
                var message = recorder.LastModalMessage
                    ?? (viewModel is not null && !string.IsNullOrEmpty(viewModel.ModalMessage)
                        ? viewModel.ModalMessage
                        : null);
                if (message is not null)
                {
                    var choices = viewModel is null
                        ? []
                        : viewModel.ModalChoices.Select(choice => choice.Label).ToList();
                    modal = new GoldenModal(message, choices);
                }
            }

            rows.Add(new GoldenRow(spec.Id, label, spec.Description, modal));
        }

        return rows;
    }

    /// <summary>
    /// The learned-words page with a seeded dictionary, over a unique temp dir pair (the
    /// KeyboardDictionaryPageTests fixture shape).
    /// </summary>
    private static void CaptureDictionary(List<GoldenSource> sources)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "eyectrl_golden_kbp_" + Guid.NewGuid());
        var appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl_golden_kbp_app_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(baseDir, "Resources", "Dictionaries"));
        Directory.CreateDirectory(appDataDir);
        try
        {
            File.WriteAllText(
                Path.Combine(baseDir, "Resources", "Dictionaries", "en.json"),
                JsonSerializer.Serialize(new { words = new Dictionary<string, int> { ["hello"] = 100 } }));
            File.WriteAllText(
                Path.Combine(appDataDir, "user_dictionary_en.json"),
                JsonSerializer.Serialize(new Dictionary<string, int>
                {
                    ["hello"] = 100, ["zeppelin"] = 5, ["aardvark"] = 9,
                }));

            var settings = new EyeCtrlSettings();
            settings.Keyboard.LanguagePack = "en";
            var dictionary = new UserDictionaryService(settings, appDataDir, baseDir);
            var recorder = new RecordingSettingsPageContext(settings: settings, dictionary: dictionary);
            Capture(sources, "KeyboardDictionary", recorder, new KeyboardPageSource(recorder.Context));
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(appDataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void CaptureGeneral(List<GoldenSource> sources)
    {
        var settings = new EyeCtrlSettings();
        var recorder = new RecordingSettingsPageContext(
            settings: settings,
            viewModel: QaTestSupport.MakeViewModel(),
            supervision: BuildSupervision(settings));
        Capture(sources, "General", recorder, new GeneralPageSource(recorder.Context));
    }

    private static void CaptureQuickActions(List<GoldenSource> sources)
    {
        var recorder = new RecordingSettingsPageContext(viewModel: QaTestSupport.MakeViewModel());
        Capture(sources, "QuickActions", recorder, new QuickActionsPageSource(recorder.Context));
    }

    private static void CaptureModels(List<GoldenSource> sources)
    {
        // List, then Manage: the last List row (md_manage) navigates, and the NEXT capture reads
        // the Manage view. Order is load-bearing, documented here.
        using (var scope = ModelTestFakes.Create())
        {
            var (registry, management) = scope.WithDownloadedBundled();
            var recorder = new RecordingSettingsPageContext(
                registry: registry, modelManagement: management);
            var source = new ModelsPageSource(recorder.Context);
            Capture(sources, "Models", recorder, source);
            Capture(sources, "ModelsManage", recorder, source);
        }

        using (var scope = ModelTestFakes.Create())
        {
            var (registry, management) = scope.WithHashedDownloadedBundled();
            registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
            var recorder = new RecordingSettingsPageContext(
                registry: registry, modelManagement: management);
            Capture(sources, "ModelsRepair", recorder, new ModelsPageSource(recorder.Context));
        }
    }

    /// <summary>An installed, disconnected Supervisor: the General page's row renders.</summary>
    private static SupervisionToggleService BuildSupervision(EyeCtrlSettings settings) =>
        new(settings,
            bringUp: new SupervisorBringUp(settings, () => { }, () => false, _ => { }, () => { }, () => false),
            startupEnsure: new SupervisorStartupEnsure(settings, () => { }, () => false, () => false),
            persistEnabled: _ => { },
            supervisorInstalled: () => true,
            isSupervisorConnected: () => false,
            reconcileRunKey: () => { },
            runInBackground: action => action(),
            postToUi: action => action());

    // ────── File IO and comparison ──────

    private static string GoldenPath() =>
        Path.Combine(
            SourceScanner.RepoRoot().FullName,
            GoldenRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static GoldenFile LoadGolden() =>
        JsonSerializer.Deserialize<GoldenFile>(File.ReadAllText(GoldenPath()), JsonOptions)!;

    /// <summary>Human-readable per-row differences; empty means identical.</summary>
    private static List<string> Differences(
        List<GoldenSource> actual, IReadOnlyList<GoldenSource> expected)
    {
        var differences = new List<string>();
        if (actual.Count != expected.Count)
            differences.Add($"source count: actual {actual.Count}, golden {expected.Count}");

        for (var i = 0; i < Math.Min(actual.Count, expected.Count); i++)
        {
            var actualSource = actual[i];
            var expectedSource = expected[i];
            if (actualSource.Name != expectedSource.Name)
            {
                differences.Add(
                    $"source[{i}]: actual \"{actualSource.Name}\", golden \"{expectedSource.Name}\"");
                continue;
            }

            if (actualSource.Rows.Count != expectedSource.Rows.Count)
                differences.Add(
                    $"{actualSource.Name}: row count actual {actualSource.Rows.Count}, "
                    + $"golden {expectedSource.Rows.Count}");

            for (var r = 0; r < Math.Min(actualSource.Rows.Count, expectedSource.Rows.Count); r++)
            {
                var actualRow = actualSource.Rows[r];
                var expectedRow = expectedSource.Rows[r];
                var where = $"{actualSource.Name}[{r}] id=\"{actualRow.Id}\"";
                if (actualRow.Id != expectedRow.Id)
                    differences.Add($"{where}: id golden \"{expectedRow.Id}\"");
                if (actualRow.Label != expectedRow.Label)
                    differences.Add(
                        $"{where}: label actual \"{actualRow.Label}\", golden \"{expectedRow.Label}\"");
                if (actualRow.Description != expectedRow.Description)
                    differences.Add(
                        $"{where}: description actual \"{actualRow.Description}\", "
                        + $"golden \"{expectedRow.Description}\"");
                if (!ModalEquals(actualRow.Modal, expectedRow.Modal))
                    differences.Add(
                        $"{where}: modal actual {DescribeModal(actualRow.Modal)}, "
                        + $"golden {DescribeModal(expectedRow.Modal)}");
            }
        }

        return differences;
    }

    private static bool ModalEquals(GoldenModal? actual, GoldenModal? expected)
    {
        if (actual is null || expected is null) return actual is null && expected is null;
        return actual.Message == expected.Message
            && actual.Choices.SequenceEqual(expected.Choices, StringComparer.Ordinal);
    }

    private static string DescribeModal(GoldenModal? modal) =>
        modal is null
            ? "<none>"
            : $"\"{modal.Message}\" choices [{string.Join(" | ", modal.Choices)}]";
}
