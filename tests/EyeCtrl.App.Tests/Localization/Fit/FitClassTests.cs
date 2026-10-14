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
/// <see cref="EnglishExceptions"/> with its reason AND its measured figure, before German is blamed
/// for anything. The list is two-sided and bounded (review-01 R1): a failure not on it fails
/// <see cref="EveryEnglishText_FitsItsClass_OrIsARecordedException"/>; a listed key whose measurement
/// gets WORSE than its recorded figure (by more than <see cref="ExceptionToleranceDip"/>) fails —
/// "Double click and hold" cannot ride on "Double click"'s waiver; and a listed key that starts to
/// FIT fails too, so the entry is deleted. Both checks run on the face the entry was measured in
/// (matched case-insensitively): a CI runner without Segoe UI Variable resolves Segoe UI, whose
/// figures are logged for every key.
/// </para>
///
/// <para>
/// <b>The list is ENGLISH-ONLY.</b> It waives English text the project shipped before T09; it says
/// nothing about any translation. T10's German is measured with no exceptions at all.
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
/// singleton, hence the "WpfApplication" collection. The classes whose room changes with the theme
/// are also checked in every theme by <see cref="FitClassThemeTests"/>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class FitClassTests
{
    /// <summary>How much worse than its recorded figure an excepted key may measure before it fails.</summary>
    internal const double ExceptionToleranceDip = 0.1;

    /// <summary>One recorded ENGLISH exception — never consulted for a translation.</summary>
    /// <param name="Key">The key whose English does not meet its class.</param>
    /// <param name="FitClass">The class it fails.</param>
    /// <param name="FaceFile">The font file it was measured in; the bound and staleness are checked on that face.</param>
    /// <param name="MarginDip">
    /// The measured <see cref="FitVerdict.Margin"/> (room less need; negative: it does not fit). The
    /// key fails once it measures worse than this by more than <see cref="ExceptionToleranceDip"/>.
    /// </param>
    /// <param name="Reason">Why it is accepted, and who revisits it.</param>
    internal sealed record EnglishException(string Key, string FitClass, string FaceFile, double MarginDip, string Reason);

    /// <summary>The English baseline's exceptions. Each has a reason and a measured bound.</summary>
    internal static readonly EnglishException[] EnglishExceptions =
    [
        // The debate measured "Double click" at +1.77 DIP; this harness measures +1.8 at 2.0. The
        // whole ink box is inside the arc, 0.2 DIP short of the +2 margin. Pinned as the ticket's
        // recorded exception. T13 re-measured it (2026-10-12): the German ring's "2× Klick" keeps
        // 14.7 DIP and the Settings row's "Doppelklick" would keep 0.85, so the ring's wording is
        // the only candidate at issue. The owner KEPT this exception on 2026-10-12, over lowering the
        // threshold or rewording the English, so no threshold changed. For the record: a reworded
        // English "2× click" measures +15.6 DIP on the ring (a figure that otherwise exists only in
        // the output of TheDoubleClickCandidates_AreMeasured_InTheRing_ForTheOwnersDecision).
        new("overlays.ring.doubleClick", "A105", "SegUIVar.ttf", -0.2,
            "+1.8 DIP against the +2 rule: inside the arc, short of the margin by 0.2 (debate: +1.77). "
            + "T13 re-measured it 2026-10-12 (+1.8; German \"2× Klick\" +14.7, \"Doppelklick\" +0.85; a "
            + "reworded English \"2× click\" +15.6). Kept by the owner 2026-10-12; no threshold changed."),
        new("settings.ring.doubleClick", "A105", "SegUIVar.ttf", -0.2,
            "The Settings mirror of overlays.ring.doubleClick, measured in the same runtime ring: +1.8 DIP. "
            + "T13 re-measured it 2026-10-12; the German candidates' figures are on the overlays key's entry."),
    ];

    /// <summary>
    /// The measured classes that carry no key in the shipped table today (B94x2 is the default
    /// 100x100 button; T11 found no real key in it). Their theory rows measure nothing, which is
    /// said out loud here instead of passing silently: any OTHER class with no item fails, and a
    /// listed class that gains a key fails <see cref="OnlyTheListedClasses_HaveNoItems"/> until it
    /// is removed from this list, which turns its gates on.
    /// </summary>
    internal static readonly string[] ClassesWithNoKeys = ["B94x2"];

    private readonly ITestOutputHelper _output;

    public FitClassTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OnlyTheListedClasses_HaveNoItems()
    {
        var empty = AppFitMeasurers.Measurers.Keys
            .Where(fitClass => FitCatalog.ItemsOf(fitClass).Count == 0)
            .OrderBy(fitClass => fitClass, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
                "A class with no key is measured in every theme and 'passes' with nothing to check. "
                + "List it in FitClassTests.ClassesWithNoKeys (and say why), or give it its keys.")
            .That(empty).ContainsExactly(ClassesWithNoKeys.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

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
    public void EveryEnglishText_FitsItsClass_OrIsARecordedException(string fitClass) =>
        AssertEnglishFits(fitClass, _output, "the active theme");

    /// <summary>
    /// The English check for one class under whatever theme is active: every text fits, or is a
    /// recorded exception that has not got worse and has not started to fit.
    /// </summary>
    internal static void AssertEnglishFits(string fitClass, ITestOutputHelper output, string context)
    {
        var measure = AppFitMeasurers.Measurers[fitClass];
        var items = FitCatalog.ItemsOf(fitClass);
        if (ClassesWithNoKeys.Contains(fitClass, StringComparer.Ordinal))
        {
            output.WriteLine($"{fitClass} carries no key today (FitClassTests.ClassesWithNoKeys); nothing to measure ({context}).");
            return;
        }

        Check.WithCustomMessage($"{fitClass} has no English items: the gate would measure nothing.")
            .That(items.Count).IsStrictlyGreaterThan(0);
        var exceptions = EnglishExceptions.Where(e => e.FitClass == fitClass).ToList();
        var failures = new List<string>();
        var stale = new List<string>();

        foreach (var item in items)
        {
            foreach (var text in AppFitMeasurers.Samples(item))
            {
                var verdict = measure(item.Key, text);
                output.WriteLine($"{fitClass} {item} \"{text}\" ({context}): {verdict} (margin {verdict.Margin:0.####})");

                var exception = exceptions.FirstOrDefault(e => e.Key == item.Key);
                if (exception is null)
                {
                    if (!verdict.Fits) failures.Add($"{item} \"{text}\": {verdict}");
                    continue;
                }

                // Checked before the face: an exception bounds its margin's rule on its face, and
                // waives nothing else on any face.
                if (verdict.Unwaivable is { } rule)
                {
                    failures.Add($"{item} \"{text}\": its exception bounds the {fitClass} margin only, and it also breaks {rule}. {verdict}");
                    continue;
                }

                if (!verdict.Face.Contains(exception.FaceFile, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"  (the exception for {item.Key} was recorded on {exception.FaceFile}; this face is not bounded)");
                    continue;
                }

                if (verdict.Fits)
                    stale.Add($"{item} now fits ({verdict}); remove its exception (\"{exception.Reason}\").");
                else if (double.IsNaN(verdict.Margin))
                    failures.Add($"{item}: an excepted key needs a measured margin to be bounded, and {fitClass} gives none.");
                else if (verdict.Margin < exception.MarginDip - ExceptionToleranceDip)
                    failures.Add($"{item} \"{text}\" got WORSE than its recorded exception: margin "
                                 + $"{verdict.Margin:0.##} DIP vs {exception.MarginDip:0.##} on record. {verdict}");
            }
        }

        var keys = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        stale.AddRange(exceptions.Where(e => !keys.Contains(e.Key))
            .Select(e => $"{e.Key} is no longer in {fitClass}; remove its exception."));

        Check.WithCustomMessage(
                $"{failures.Count} English text(s) do not fit class {fitClass} at 2.0 ({context}). Fix the text "
                + "or the layout, or record a reasoned, measured exception in FitClassTests.EnglishExceptions:\n  "
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
        // 300, not 120: the summary card's bound is the 1920 DIP window, which 120 W at 12 DIP fits.
        yield return ("unbreakable", new string('W', 300));
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
