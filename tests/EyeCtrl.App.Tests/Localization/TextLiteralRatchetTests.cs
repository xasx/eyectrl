namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using NFluent;
using Xunit;

/// <summary>
/// T02's literal ratchet: hard-coded UI text is frozen at today's count, per file and per literal.
/// A new sink literal fails <see cref="UnbaselinedTextLiterals_FailTheRatchet"/>; a baseline entry
/// that no longer exists fails <see cref="BaselineEntriesNoLongerFound_FailTheRatchet"/>. The
/// string moves of T06–T08 delete their entries as they key the text; after T08 only the true
/// exceptions remain, and since T09 <see cref="TheBaseline_HoldsOnlyTrueExceptions"/> asserts that
/// against <c>localization/text-literal-exceptions.json</c>, where each entry carries a category
/// and a reason.
///
/// <para>
/// The scan is sink-based, not sentence-based: <see cref="TextLiteralScan"/> lists the syntactic
/// positions where text reaches the screen (per the ticket) and the producer files whose return
/// values are UI text. Comments are stripped before scanning, and log calls, exception
/// constructors and <c>nameof</c> are not sinks — so a diagnostic string is invisible to the
/// ratchet by construction rather than by a word list.
/// </para>
/// </summary>
public class TextLiteralRatchetTests
{
    /// <summary>
    /// Set <c>EYECTRL_WRITE_TEXT_BASELINE=1</c> to rewrite the baseline from the current tree.
    /// Used to create it once and to re-baseline after a deliberate text move; never set in CI.
    /// </summary>
    public const string WriteEnvironmentVariable = "EYECTRL_WRITE_TEXT_BASELINE";

    private const string BaselineRelativePath = "localization/text-literal-baseline.json";

    private const string ExceptionsRelativePath = "localization/text-literal-exceptions.json";

    /// <summary>The closed list of reasons a literal may stay in source (T09).</summary>
    private static readonly string[] ExceptionCategories =
        ["glyph", "product", "endonym", "id", "data", "log", "preset-legacy-label"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Keep quotes and non-ASCII readable in the diff; the file is a reviewable artifact.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Every scanned file: the five UI projects plus the Core/lib producer files.</summary>
    public static IEnumerable<(string Relative, string Text)> ScannedFiles() =>
        TextLiteralScan.ScannedSourceFiles();

    private static string BaselinePath() =>
        Path.Combine(
            SourceScanner.RepoRoot().FullName,
            BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<TextLiteralScan.Entry> ScanRepo() =>
        ScannedFiles()
            .SelectMany(file => TextLiteralScan.FindSites(file.Relative, file.Text))
            .ToList();

    private static List<TextLiteralScan.Entry> LoadBaseline()
    {
        var file = JsonSerializer.Deserialize<BaselineFile>(
            File.ReadAllText(BaselinePath()), JsonOptions)!;
        return file.Entries;
    }

    [Fact]
    public void UnbaselinedTextLiterals_FailTheRatchet()
    {
        var baseline = LoadBaseline()
            .ToDictionary(e => (e.Path, e.Literal), e => e.Count);
        var offenders = new List<string>();

        foreach (var entry in ScanRepo())
        {
            var known = baseline.TryGetValue((entry.Path, entry.Literal), out var count) ? count : 0;
            if (entry.Count > known)
                offenders.Add($"{entry.Path}: {entry.Literal} (found x{entry.Count}, baseline x{known})");
        }

        Check.WithCustomMessage(
                "New hard-coded UI text found at a screen sink. Move it into strings.json and use "
                + "T(key), or — when it is a genuine non-text literal (a glyph, a product name, an "
                + "endonym, an id) — re-baseline with "
                + $"{WriteEnvironmentVariable}=1 dotnet test --filter TextLiteralRatchetTests.\n  "
                + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    [Fact]
    public void BaselineEntriesNoLongerFound_FailTheRatchet()
    {
        var found = ScanRepo().ToDictionary(e => (e.Path, e.Literal), e => e.Count);
        var stale = LoadBaseline()
            .Where(entry =>
                !found.TryGetValue((entry.Path, entry.Literal), out var count) || count < entry.Count)
            .Select(entry =>
            {
                var count = found.TryGetValue((entry.Path, entry.Literal), out var c) ? c : 0;
                return $"{entry.Path}: {entry.Literal} (baseline x{entry.Count}, found x{count})";
            })
            .ToList();

        Check.WithCustomMessage(
                "Stale baseline entries: the literal is gone (or less frequent), so the entry "
                + "protects nothing. Remove the line, or re-baseline with "
                + $"{WriteEnvironmentVariable}=1 dotnet test --filter TextLiteralRatchetTests.\n  "
                + string.Join("\n  ", stale))
            .That(stale).IsEmpty();
    }

    /// <summary>
    /// The baseline is a generated artifact; it stays sorted so parallel tickets edit disjoint
    /// regions and a diff shows exactly what moved.
    /// </summary>
    [Fact]
    public void Baseline_IsSortedByPathThenLiteral()
    {
        var baseline = LoadBaseline();
        var sorted = baseline
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Literal, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage("The baseline file is not sorted; regenerate it.")
            .That(baseline.SequenceEqual(sorted)).IsTrue();
    }

    /// <summary>
    /// Rewrites the baseline from the current tree when <see cref="WriteEnvironmentVariable"/> is
    /// 1. A plain green run does nothing — the variable is never set in CI.
    /// </summary>
    [Fact]
    public void RegenerateBaseline_WhenTheEnvironmentVariableAsks()
    {
        if (Environment.GetEnvironmentVariable(WriteEnvironmentVariable) != "1") return;

        var entries = ScanRepo()
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Literal, StringComparer.Ordinal)
            .ToList();
        File.WriteAllText(BaselinePath(), JsonSerializer.Serialize(new BaselineFile(entries), JsonOptions));
    }

    /// <summary>
    /// T09's final ratchet check (moved from T08, recheck N4): after T06–T08 the baseline holds
    /// ONLY true exceptions. Every baseline entry must be named in the exceptions file with a
    /// category from the closed list and a reason; every exception must still be in the baseline.
    /// The sink kinds the batch-3 reviews found unrecorded (<c>const string</c> initialisers,
    /// <c>statusText:</c>, <c>Label:</c>, string <c>=&gt;</c> members, <c>ShowBannerAsync</c>) were
    /// closed first, so this is not vacuous for them — their pins are below.
    /// </summary>
    [Fact]
    public void TheBaseline_HoldsOnlyTrueExceptions()
    {
        var path = Path.Combine(
            SourceScanner.RepoRoot().FullName,
            ExceptionsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var exceptions = JsonSerializer.Deserialize<ExceptionsFile>(File.ReadAllText(path), JsonOptions)!.Entries;
        var baseline = LoadBaseline();

        var named = exceptions.ToDictionary(e => (e.Path, e.Literal));
        var unexplained = baseline
            .Where(entry => !named.ContainsKey((entry.Path, entry.Literal)))
            .Select(entry => $"{entry.Path}: {entry.Literal}")
            .ToList();
        var inBaseline = baseline.Select(entry => (entry.Path, entry.Literal)).ToHashSet();
        var stale = exceptions
            .Where(e => !inBaseline.Contains((e.Path, e.Literal)))
            .Select(e => $"{e.Path}: {e.Literal}")
            .ToList();
        var malformed = exceptions
            .Where(e => !ExceptionCategories.Contains(e.Category) || string.IsNullOrWhiteSpace(e.Reason))
            .Select(e => $"{e.Path}: {e.Literal} (category '{e.Category}')")
            .ToList();

        Check.WithCustomMessage(
                "Baseline literals that are not a named true exception — key them, or (if they are "
                + $"genuinely a glyph, product name, endonym, id, data or log line) add them to {ExceptionsRelativePath} "
                + "with a reason:\n  " + string.Join("\n  ", unexplained))
            .That(unexplained).IsEmpty();
        Check.WithCustomMessage("Exceptions no longer in the baseline; remove them:\n  " + string.Join("\n  ", stale))
            .That(stale).IsEmpty();
        Check.WithCustomMessage(
                $"Exceptions need a category from [{string.Join(", ", ExceptionCategories)}] and a reason:\n  "
                + string.Join("\n  ", malformed))
            .That(malformed).IsEmpty();
    }

    // ────── The scanner's falsification pins: it must catch the forms it exists for ──────

    /// <summary>T09 (T06 review N2): a const string initialiser is a sink.</summary>
    [Fact]
    public void TheScan_CatchesAConstStringInitialiser() =>
        CheckCatches(
            "private const string StartupErrorTitle = \"EyeCtrl — Startup Error\";",
            "\"EyeCtrl — Startup Error\"");

    /// <summary>T09 review-01 R4 (mutant A20): the const hole in another keyword is a sink too.</summary>
    [Fact]
    public void TheScan_CatchesAStaticReadonlyStringInitialiser()
    {
        CheckCatches(
            "private static readonly string Hint = \"Please check on the user now\";",
            "\"Please check on the user now\"");
        CheckCatches(
            "internal readonly static string Hint = \"Please check on the user now\";",
            "\"Please check on the user now\"");
    }

    /// <summary>T09 (T06 review N2): the setup window's <c>statusText:</c> argument is a sink.</summary>
    [Fact]
    public void TheScan_CatchesAStatusTextArgument() =>
        CheckCatches(
            "var w = new SupervisorSetupWindow(settings, statusText: \"Supervisor state: \" + state);",
            "\"Supervisor state: \"");

    /// <summary>T09 (T08 review R5): the capital <c>Label:</c> of a record argument is a sink.</summary>
    [Fact]
    public void TheScan_CatchesACapitalLabelArgument() =>
        CheckCatches(
            "new EdgeButtonDefinition(id, Symbol: string.Empty, dwellMs, Label: \"Shift\");",
            "\"Shift\"");

    /// <summary>T09 (T08 review R5): an expression-bodied string member is a sink, switch arms included.</summary>
    [Fact]
    public void TheScan_CatchesAnExpressionBodiedStringMember()
    {
        CheckCatches("public override string Name => \"Saccade Jumps\";", "\"Saccade Jumps\"");
        CheckCatches(
            "internal static string Line(int k) => k switch { 0 => \"Nice tracking!\", _ => \"Keep going\" };",
            "\"Keep going\"");
    }

    /// <summary>T09 (T08 review R5): the eye-care banner's <c>ShowBannerAsync</c> argument is a sink.</summary>
    [Fact]
    public void TheScan_CatchesTheShowBannerAsyncArgument() =>
        CheckCatches(
            "var accepted = await _hooks.ShowBannerAsync(\"Time for an eye break\");",
            "\"Time for an eye break\"");

    /// <summary>
    /// T09: QuickActionPresets (Core) is on the scan surface, so the named preset-label exception
    /// names real literals — and its ids and payloads stay out (only the Label argument is a sink).
    /// </summary>
    [Fact]
    public void TheScan_CoversQuickActionPresets_LabelsOnly()
    {
        Check.That(TextLiteralScan.IsScanned("src/EyeCtrl.Core/Models/QuickActionPresets.cs")).IsTrue();
        var found = TextLiteralScan.FindSites(
                "src/EyeCtrl.Core/Models/QuickActionPresets.cs",
                "new QuickActionPreset(\"kbd.copy\", \"Copy\", \"Ctrl+C\"),")
            .Select(e => e.Literal)
            .ToList();
        Check.That(found).ContainsExactly("\"Copy\"");
    }

    private static void CheckCatches(string source, string expected) =>
        Check.WithCustomMessage($"the scan no longer recognises UI text in:\n  {source}")
            .That(Scan(source).Contains(expected)).IsTrue();

    private static void CheckIgnores(string source) =>
        Check.WithCustomMessage($"the scan now records a non-text literal in:\n  {source}")
            .That(Scan(source)).IsEmpty();

    private static List<string> Scan(string source) =>
        TextLiteralScan.FindSites("src/EyeCtrl.App/Views/Fixture.cs", source)
            .Select(e => e.Literal)
            .ToList();

    [Fact]
    public void TheScan_CatchesAnInterpolatedLiteralAtATextSink() =>
        CheckCatches(
            "DistanceText.Text = string.Create(provider, $\"{mm:F0} mm\");",
            "$\"{mm:F0} mm\"");

    /// <summary>
    /// The commonest shapes, all three of which the pre-fixup `\s*` regex swallowed (F1). Each
    /// must be found with the value starting immediately after the `=`/`:`.
    /// </summary>
    [Fact]
    public void TheScan_CatchesABareTextAssignment() =>
        CheckCatches(
            "StatusLabel.Text = \"Launch requested.\";",
            "\"Launch requested.\"");

    [Fact]
    public void TheScan_CatchesABareDescription() =>
        CheckCatches(
            "new SliderSpec(\"id\", \"Label\", 1, 2, 1, 0, Get, Set) { Description = \"R6 description.\" };",
            "\"R6 description.\"");

    [Fact]
    public void TheScan_CatchesANamedLabel() =>
        CheckCatches(
            "var s = new DwellSlider(registrar, \"sl\", label: \"Smoothing\");",
            "\"Smoothing\"");

    [Fact]
    public void TheScan_CatchesTheHostSwatchNamesField() =>
        CheckCatches(
            "internal static readonly string[] ColorSwatchNames = [\"Accent\", \"Green\"];",
            "\"Accent\"");

    /// <summary>
    /// The changed controls' positional text arguments (F3): the settings host passes them
    /// positionally, so R5's `"R5 On"` must be visible to the ratchet.
    /// </summary>
    [Fact]
    public void TheScan_CatchesPositionalControlText() =>
        CheckCatches(
            "var t = new DwellToggle(registrar, \"id\", \"Label\", \"On\", \"Off\", get, set, cat, r, ms);",
            "\"On\"");

    /// <summary>The slider is a sink too (fixup-02 N1): its label is text when passed positionally.</summary>
    [Fact]
    public void TheScan_CatchesAPositionalSliderLabel() =>
        CheckCatches(
            "var s = new DwellSlider(registrar, \"sl\", \"Smoothing Factor\", 0, 1, 0.1, 1, get, set, cat, r, ms);",
            "\"Smoothing Factor\"");

    [Fact]
    public void TheScan_CatchesShowErrorAndRingJumpSpec()
    {
        CheckCatches(
            "_ctx.ShowError(\"Another download is already in progress.\");",
            "\"Another download is already in progress.\"");
        CheckCatches(
            "new RingJumpSpec(\"am_preview\", \"View ring preview\", Go, ButtonKind.Default, 300, 100);",
            "\"View ring preview\"");
    }

    /// <summary>
    /// The emoji grid's special-button caption is a sink (T15 F4): its hard-coded English "Back"
    /// reached a German overlay because a helper argument was not a sink. A glyph has no letter, so
    /// it stays out of the record, as everywhere else.
    /// </summary>
    [Fact]
    public void TheScan_CatchesAHardCodedSpecialButtonCaption()
    {
        CheckCatches("AddSpecialButton(backRow, \"Back\", \"skinToneBack\", 2.0);", "\"Back\"");
        CheckIgnores("AddSpecialButton(backRow, \"\\u25C0\", \"skinToneBack\", 2.0);");
    }

    [Fact]
    public void TheScan_CatchesASpecLabel() =>
        CheckCatches(
            "new SliderSpec(\"cal_points\", \"Calibration Points\", 1, 2, 1, 0, Get, Set)"
            + " { Description = \"How many points.\" };",
            "\"Calibration Points\"");

    [Fact]
    public void TheScan_CatchesShowModalAndSetName() =>
        CheckCatches(
            "_ctx.ShowModal(\"Clear saved calibration?\", () => { });"
            + " AutomationProperties.SetName(anchor, \"Close\");",
            "\"Clear saved calibration?\"");

    [Fact]
    public void TheScan_CatchesXamlAttributes()
    {
        var found = TextLiteralScan.FindSites(
            "src/EyeCtrl.App/Views/Fixture.xaml",
            "<TextBlock Text=\"Good\" ToolTip=\"Distance quality\" Language=\"en-US\" />");
        Check.That(found.Select(e => e.Literal)).HasSize(2);
        Check.That(found.Select(e => e.Literal)).Contains("\"Good\"");
        Check.That(found.Select(e => e.Literal)).Contains("\"Distance quality\"");
    }

    [Fact]
    public void TheScan_CatchesAProducerMember()
    {
        var found = TextLiteralScan.FindSites(
            "src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml.cs",
            "internal static string QualityWord(DistanceQuality q) =>"
            + " q is DistanceQuality.Good ? \"Good\" : \"Unknown\";");
        Check.That(found.Select(e => e.Literal)).Contains("\"Good\"");
        Check.That(found.Select(e => e.Literal)).Contains("\"Unknown\"");
    }

    /// <summary>
    /// T07: a producer member's UI text can live in a PROPERTY (<c>string StatusText { get { … }
    /// } }</c>), which the method-only member form never matched — the T06 review's N1 hole that
    /// let <c>SupervisionToggleService</c>'s status lines stay invisible to the ratchet.
    /// </summary>
    [Fact]
    public void TheScan_CatchesAProducerProperty()
    {
        var found = TextLiteralScan.FindSites(
            "src/EyeCtrl.App/Services/SupervisionToggleService.cs",
            "public string StatusText { get { return \"Starting the Supervisor...\"; } }");
        Check.That(found.Select(e => e.Literal)).Contains("\"Starting the Supervisor...\"");
    }

    /// <summary>Log and diagnostic strings are not screen text; the ticket excludes them.</summary>
    [Fact]
    public void TheScan_IgnoresLogAndDiagnosticCalls()
    {
        CheckIgnores("Log.Information(\"Gaze at {X:F0}, {Y:F0}\", x, y);");
        CheckIgnores("Diag($\"raw y={y:F0}, x={x:F0}\");");
    }

    [Fact]
    public void TheScan_IgnoresGlyphsDigitsAndNameof()
    {
        CheckIgnores("_stateText.Text = \"\\u25C9 \";");
        CheckIgnores("_stateText.Text = \"42\";");
        CheckIgnores("_stateText.Text = nameof(QualityWord);");
    }

    [Fact]
    public void TheScan_RecordsTheLabelAndDisplay_ButNotTheIdOrCode()
    {
        var found = TextLiteralScan.FindSites(
            "src/EyeCtrl.App/Views/Fixture.cs",
            "new ComboBoxSpec(\"k_layout\", \"Layout\", new[] { \"Qwerty\" },"
            + " new[] { \"Qwerty\" }, Get, Set);");
        var literals = found.Select(e => e.Literal).ToList();
        Check.That(literals).Contains("\"Layout\"");
        Check.WithCustomMessage("the display array is text and must be recorded.")
            .That(literals).Contains("\"Qwerty\"");
        Check.WithCustomMessage("a combo's id is not text.")
            .That(literals).Not.Contains("\"k_layout\"");
    }

    private sealed record BaselineFile(List<TextLiteralScan.Entry> Entries);

    /// <summary>One named true exception: the literal, its category and why it stays.</summary>
    private sealed record ExceptionEntry(string Path, string Literal, string Category, string Reason);

    private sealed record ExceptionsFile(List<ExceptionEntry> Entries);
}
