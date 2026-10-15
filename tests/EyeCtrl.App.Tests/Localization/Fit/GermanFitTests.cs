namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T10: every key's GERMAN, measured in its fit class's real geometry at 2.0 — T09's English gate
/// (<see cref="FitClassTests"/>), run on the draft through the same measurers. Binding: fit is not
/// advisory for a translation; a German text that fails its class is reworded.
///
/// <para>
/// <b>Every class in every theme.</b> T09 bound only the classes whose room moves with the theme
/// (<see cref="FitClassThemeTests.ThemeDependentClasses"/>) per theme; German is checked in all
/// three for every class, so the tightest theme decides wherever it differs.
/// </para>
///
/// <para>
/// <b>No German exceptions.</b> <see cref="FitClassTests.EnglishExceptions"/> waives English shipped
/// before T09 and is never consulted here. <see cref="GermanExceptions"/> is the German list: empty,
/// and any entry must carry a reason and a measured, bounded margin like the English ones.
/// </para>
///
/// <para>
/// Switching the theme writes process-global state, so this lives in the exclusive "ActiveTheme"
/// collection (<see cref="ThemeScope"/> restores it).
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class GermanFitTests
{
    /// <summary>One recorded GERMAN exception: a reason and the measured margin it may not get worse than.</summary>
    internal sealed record GermanException(string Key, string FitClass, double MarginDip, string Reason);

    /// <summary>The German exceptions. None: every German text fits its class.</summary>
    internal static readonly GermanException[] GermanExceptions = [];

    private readonly ITestOutputHelper _output;

    public GermanFitTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string, string> ThemesAndClasses()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in ThemeRegistry.Names)
        {
            foreach (var fitClass in AppFitMeasurers.Measurers.Keys.OrderBy(c => c, StringComparer.Ordinal))
                data.Add(theme, fitClass);
        }

        return data;
    }

    [StaTheory]
    [MemberData(nameof(ThemesAndClasses))]
    public void EveryGermanText_FitsItsClass(string theme, string fitClass)
    {
        using var scope = ThemeScope.Activate(ThemeRegistry.Resolve(theme));
        var measure = AppFitMeasurers.Measurers[fitClass];
        var items = GermanDraft.ItemsOf(fitClass);
        if (FitClassTests.ClassesWithNoKeys.Contains(fitClass, StringComparer.Ordinal))
        {
            _output.WriteLine($"{theme} {fitClass} carries no key today (FitClassTests.ClassesWithNoKeys); nothing to measure.");
            Check.That(items).IsEmpty();
            return;
        }

        var failures = new List<string>();
        var stale = new List<string>();
        var exceptions = GermanExceptions.Where(e => e.FitClass == fitClass).ToList();

        foreach (var item in items)
        {
            foreach (var text in AppFitMeasurers.Samples(item))
            {
                var verdict = measure(item.Key, text);
                _output.WriteLine($"{theme} {fitClass} {item} \"{text}\": {verdict} (margin {verdict.Margin:0.##})");
                var exception = exceptions.FirstOrDefault(e => e.Key == item.Key);
                if (exception is null)
                {
                    if (!verdict.Fits) failures.Add($"{item} \"{text}\": {verdict}");
                }
                else if (verdict.Unwaivable is { } rule)
                {
                    failures.Add($"{item} \"{text}\": its exception bounds the {fitClass} margin only, and it also breaks {rule}.");
                }
                else if (verdict.Fits)
                {
                    stale.Add($"{item} now fits; remove its German exception (\"{exception.Reason}\").");
                }
                else if (double.IsNaN(verdict.Margin) || verdict.Margin < exception.MarginDip - FitClassTests.ExceptionToleranceDip)
                {
                    failures.Add($"{item} \"{text}\" is worse than its recorded German exception "
                                 + $"({verdict.Margin:0.##} vs {exception.MarginDip:0.##} DIP): {verdict}");
                }
            }
        }

        // Not "equal to the English count": the German list is built from the English list, so that
        // comparison could never fail. The floor is what matters.
        Check.WithCustomMessage($"{fitClass} has no German items: the gate would measure nothing.")
            .That(items.Count).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage(
                $"{failures.Count} German text(s) do not fit class {fitClass} at 2.0 under {theme}. Reword the "
                + "German (T10: fit is binding):\n  " + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
        Check.WithCustomMessage("Stale German fit exceptions:\n  " + string.Join("\n  ", stale)).That(stale).IsEmpty();
    }

    /// <summary>The German list names real keys of the class it claims, each with a reason.</summary>
    [Fact]
    public void EveryGermanException_NamesAKeyOfItsClass_WithAReason()
    {
        var fitByKey = FitCatalog.FitByKey();
        var bad = GermanExceptions
            .Where(e => !fitByKey.TryGetValue(e.Key, out var fit) || fit != e.FitClass || string.IsNullOrWhiteSpace(e.Reason))
            .Select(e => e.Key)
            .ToList();
        Check.That(bad).IsEmpty();
    }
}
