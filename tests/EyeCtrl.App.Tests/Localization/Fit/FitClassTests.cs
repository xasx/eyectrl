namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09 PR 1: every key's English, measured in its fit class's real geometry at 2.0.
///
/// <para>
/// <b>The English baseline.</b> An English failure is either fixed or recorded in
/// <see cref="EnglishExceptions"/> with its reason, before German is blamed for anything. The list
/// is two-sided: a failure not on it fails <see cref="EveryEnglishText_FitsItsClass_OrIsARecordedException"/>,
/// and an entry that starts to FIT fails it too — on the face the entry was measured in, because a
/// CI runner without Segoe UI Variable resolves Segoe UI and measures different widths (the face
/// is logged for every key).
/// </para>
///
/// <para>
/// <b>Teeth.</b> <see cref="AnOverLongText_FailsEveryMeasurementPathOfTheClass"/> pushes an
/// unbreakable and a long spaced text through every key's measurement path: a class whose
/// measurer cannot fail is a class with no gate.
/// </para>
///
/// <para>
/// The windows are built, never shown (<see cref="FitHost"/>), so these run in CI's
/// <c>Category!=UI</c> set. The class constructs overlay windows against the WPF Application
/// singleton, hence the "WpfApplication" collection.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class FitClassTests
{
    /// <summary>One recorded English exception.</summary>
    /// <param name="Key">The key whose English does not meet its class.</param>
    /// <param name="FitClass">The class it fails.</param>
    /// <param name="FaceFile">The font file it was measured in; staleness is checked on that face only.</param>
    /// <param name="Reason">Why it is accepted, and who revisits it.</param>
    internal sealed record EnglishException(string Key, string FitClass, string FaceFile, string Reason);

    /// <summary>The English baseline's exceptions. Each has a reason; none is a German problem.</summary>
    internal static readonly EnglishException[] EnglishExceptions =
    [
        // The debate measured "Double click" at +1.77 DIP; this harness measures +1.8 at 2.0. The
        // whole ink box is inside the arc, 0.2 DIP short of the +2 margin. Pinned as the ticket's
        // recorded exception; T13 revisits the ring before Step 3 switches it on.
        new("overlays.ring.doubleClick", "A105", "SEGUIVAR.TTF",
            "+1.8 DIP against the +2 rule: inside the arc, short of the margin by 0.2 (debate: +1.77). T13 revisits."),
        new("settings.ring.doubleClick", "A105", "SEGUIVAR.TTF",
            "The Settings mirror of overlays.ring.doubleClick, measured in the same runtime ring: +1.8 DIP. T13 revisits."),
        // The summary card sizes to its content (EyeCareExerciseWindow.xaml, an auto-width card in
        // a centred StackPanel): there is no box, so nothing clips. T08 classed these A56 so German
        // stays short beside the 56 DIP banner captions; the class budget is advisory here.
        new("eye-care.summary.title", "A56", "SEGUIVAR.TTF",
            "65.1 DIP: an auto-width summary card with no box, so nothing clips; the 56 budget is advisory."),
        new("eye-care.summary.reactionMs", "A56", "SEGUIVAR.TTF",
            "61.6 DIP: an auto-width summary card with no box, so nothing clips; the 56 budget is advisory."),
    ];

    private readonly ITestOutputHelper _output;

    public FitClassTests(ITestOutputHelper output) => _output = output;

    /// <summary>Every class this project measures.</summary>
    public static TheoryData<string> AppClasses()
    {
        var data = new TheoryData<string>();
        foreach (var fitClass in AppFitMeasurers.Measurers.Keys.OrderBy(c => c, StringComparer.Ordinal))
            data.Add(fitClass);
        return data;
    }

    [StaTheory]
    [MemberData(nameof(AppClasses))]
    public void EveryEnglishText_FitsItsClass_OrIsARecordedException(string fitClass)
    {
        var measure = AppFitMeasurers.Measurers[fitClass];
        var items = FitCatalog.ItemsOf(fitClass);
        var exceptions = EnglishExceptions.Where(e => e.FitClass == fitClass).ToList();
        var failures = new List<string>();
        var stale = new List<string>();

        foreach (var item in items)
        {
            foreach (var text in AppFitMeasurers.Samples(item))
            {
                var verdict = measure(item.Key, text);
                _output.WriteLine($"{fitClass} {item} \"{text}\": {verdict}");

                var exception = exceptions.FirstOrDefault(e => e.Key == item.Key);
                if (!verdict.Fits && exception is null)
                    failures.Add($"{item} \"{text}\": {verdict}");
                else if (verdict.Fits && exception is not null && verdict.Face.Contains(exception.FaceFile, StringComparison.Ordinal))
                    stale.Add($"{item} now fits ({verdict}); remove its exception (\"{exception.Reason}\").");
            }
        }

        var keys = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        stale.AddRange(exceptions.Where(e => !keys.Contains(e.Key))
            .Select(e => $"{e.Key} is no longer in {fitClass}; remove its exception."));

        Check.WithCustomMessage(
                $"{failures.Count} English text(s) do not fit class {fitClass} at 2.0. Fix the text or the "
                + "layout, or record a reasoned exception in FitClassTests.EnglishExceptions:\n  "
                + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
        Check.WithCustomMessage("Stale English fit exceptions:\n  " + string.Join("\n  ", stale))
            .That(stale).IsEmpty();
    }

    [StaTheory]
    [MemberData(nameof(AppClasses))]
    public void AnOverLongText_FailsEveryMeasurementPathOfTheClass(string fitClass)
    {
        var measure = AppFitMeasurers.Measurers[fitClass];
        var keys = FitCatalog.ItemsOf(fitClass).Select(i => i.Key).Distinct().DefaultIfEmpty("fit.probe").ToList();
        var survivors = new List<string>();

        foreach (var key in keys)
        {
            foreach (var (name, text) in OverLongTexts())
            {
                var verdict = measure(key, text);
                _output.WriteLine($"{fitClass} {key} {name}: {verdict}");
                if (verdict.Fits) survivors.Add($"{key} with the {name} text: {verdict}");
            }
        }

        Check.WithCustomMessage(
                $"Class {fitClass}'s measurer passed an over-long text, so it cannot fail — the gate is "
                + "vacuous:\n  " + string.Join("\n  ", survivors))
            .That(survivors).IsEmpty();
    }

    /// <summary>
    /// The over-long probes: one unbreakable run (fails a run or one-line rule) and a long spaced
    /// text (fails a height rule, or a class-C window's work-area budget).
    /// </summary>
    internal static IEnumerable<(string Name, string Text)> OverLongTexts()
    {
        yield return ("unbreakable", new string('W', 120));
        yield return ("spaced", string.Join(" ", Enumerable.Repeat("Wwww", 2000)));
    }

    /// <summary>
    /// Every fit class in the closed list is measured somewhere: here, in EyeCtrl.Supervisor.Tests,
    /// or by PR 2's pagination gates. A class added without a measurer is a class with no gate.
    /// </summary>
    [Fact]
    public void EveryFitClass_HasAMeasurer()
    {
        var measured = AppFitMeasurers.Measurers.Keys
            .Concat(AppFitMeasurers.SupervisorMeasuredClasses)
            .Concat(AppFitMeasurers.PaginatedClasses)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        var declared = StringTableSchema.FitClasses.OrderBy(c => c, StringComparer.Ordinal).ToList();

        Check.WithCustomMessage("The fit classes measured do not match StringTableSchema.FitClasses.")
            .That(measured).IsEqualTo(declared);
    }

    /// <summary>
    /// A Settings button class is measured in the box <see cref="AppFitMeasurers.SettingsButtonBox"/>
    /// names; this pins that box against every <c>ButtonSpec</c>/<c>RingJumpSpec</c> call site whose
    /// label is a key of that class, so the box measured is the box shipped. It also fails when a
    /// classed key is never found at a site (the scan would otherwise pass for nothing).
    /// </summary>
    [Fact]
    public void SettingsButtonBoxes_MatchEveryCallSite()
    {
        var fitByKey = FitCatalog.FitByKey();
        var mismatches = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Both `new ButtonSpec(` and a ButtonSpec-returning member's target-typed `=> new(`
        // (QuickActionsPageSource.BackButton).
        var call = new Regex(
            @"(?:new\s+(?:ButtonSpec|RingJumpSpec)|ButtonSpec\s+\w+\s*\([^()]*\)\s*=>\s*new)\s*\(",
            RegexOptions.Compiled);
        var key = new Regex(@"(?<![A-Za-z0-9_])T\s*\(\s*""(?<key>settings\.[^""]+)""", RegexOptions.Compiled);

        foreach (var (path, text) in LocalizationTestData.SourceFiles()
                     .Where(f => f.Relative.StartsWith("src/EyeCtrl.App/Views/Settings/Sources/", StringComparison.Ordinal)))
        {
            var masked = CSharpSourceText.MaskLiterals(text);
            foreach (Match match in call.Matches(masked))
            {
                var open = match.Index + match.Length - 1;
                var args = CSharpSourceText.SplitArguments(text, open);
                if (args.Count < 2) continue;
                var label = text[args[1].Start..args[1].End];
                var width = Box(text, args, "Width", 4);
                var height = Box(text, args, "Height", 5);

                foreach (Match k in key.Matches(label))
                {
                    var labelKey = k.Groups["key"].Value;
                    if (!fitByKey.TryGetValue(labelKey, out var fitClass)) continue;
                    if (fitClass != "B260x2" && !fitClass.StartsWith("btn", StringComparison.Ordinal)) continue;
                    seen.Add(labelKey);
                    var box = AppFitMeasurers.SettingsButtonBox(fitClass);
                    if (width != box.Width || height != box.Height)
                        mismatches.Add($"{path}: {labelKey} ({fitClass}) is drawn {width}×{height}, measured {box.Width}×{box.Height}");
                }
            }
        }

        var classed = fitByKey.Where(p => p.Value == "B260x2" || p.Value.StartsWith("btn", StringComparison.Ordinal))
            .Select(p => p.Key).ToList();
        var unseen = classed.Where(k => !seen.Contains(k)).ToList();

        Check.WithCustomMessage("Settings button classes measure a box the call site does not draw:\n  "
                + string.Join("\n  ", mismatches))
            .That(mismatches).IsEmpty();
        Check.WithCustomMessage("Keys classed as Settings buttons that no ButtonSpec site labels with:\n  "
                + string.Join("\n  ", unseen))
            .That(unseen).IsEmpty();
    }

    private static double Box(string text, IReadOnlyList<(int Start, int End)> args, string name, int position)
    {
        foreach (var (start, end) in args)
        {
            var arg = text[start..end];
            var named = Regex.Match(arg, $@"^{name}\s*:\s*(?<v>\d+(?:\.\d+)?)$");
            if (named.Success) return double.Parse(named.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (position < args.Count
            && double.TryParse(text[args[position].Start..args[position].End], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var positional))
            return positional;
        return double.NaN;
    }
}
