namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09 PR 2, NON-binding: every App fit class measured under the pseudo-locale, in each of the
/// three themes — a theme can change a box (Contrast's 2 DIP border makes the quick-actions tile's
/// label 104 DIP, not 106) — plus a COMPOUND pass (adjacent words joined, review-02 R2) that shows
/// where real German compounds will press on a line. Written to
/// <c>t09-fit-report/pseudo-locale-app.md</c> in the test output (the App.Tests bin folder) as an
/// early warning for T10, which regenerates it with
/// <c>dotnet test tests/EyeCtrl.App.Tests --filter PseudoLocaleFitReportTests</c>. It asserts only
/// that every key was measured in every pass; a text that does not fit is a finding, never a
/// failure.
///
/// <para>
/// Switching the theme writes process-global state, so this lives in the exclusive "ActiveTheme"
/// collection and restores both the theme and the Application dictionary (<see cref="ThemeScope"/>).
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class PseudoLocaleFitReportTests
{
    private const string CompoundPass = "Midnight (compound)";

    private readonly ITestOutputHelper _output;

    public PseudoLocaleFitReportTests(ITestOutputHelper output) => _output = output;

    [StaFact]
    public void PseudoLocaleFitReport_IsWritten_ForEveryKeyInEveryTheme()
    {
        var rows = new List<FitReportRow>();
        void Pass(string name, Func<string, IReadOnlyList<FitCatalog.Item>> itemsOf)
        {
            foreach (var (fitClass, measure) in AppFitMeasurers.Measurers.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                foreach (var item in itemsOf(fitClass))
                {
                    foreach (var text in AppFitMeasurers.Samples(item))
                        rows.Add(new FitReportRow(name, fitClass, item.ToString(), text, measure(item.Key, text)));
                }
            }
        }

        foreach (var theme in ThemeRegistry.All)
        {
            using var scope = ThemeScope.Activate(theme);
            Pass(theme.Name, PseudoLocale.ItemsOf);
        }

        using (ThemeScope.Activate(MidnightTheme.Instance))
            Pass(CompoundPass, PseudoLocale.CompoundItemsOf);

        var path = FitReport.Write("pseudo-locale-app.md", "T09 pseudo-locale fit report — App", rows);
        foreach (var group in rows.GroupBy(r => (r.Theme, r.FitClass)))
            _output.WriteLine($"{group.Key.Theme} {group.Key.FitClass}: {group.Count(r => !r.Verdict.Fits)} of {group.Count()} do not fit");
        _output.WriteLine("Report: " + path);

        var perPass = AppFitMeasurers.Measurers.Keys.Sum(c => PseudoLocale.ItemsOf(c).Sum(i => AppFitMeasurers.Samples(i).Count));
        Check.WithCustomMessage("The report did not measure every key in every pass.")
            .That(rows).HasSize(perPass * (ThemeRegistry.All.Count + 1));
        Check.That(File.Exists(path)).IsTrue();

        // Contrast's 2 DIP border must reach the tile: the same key measures a different box there.
        string TileDetail(string theme, string key) =>
            rows.Single(r => r.Theme == theme && r.FitClass == "B114" && r.Key == key).Verdict.Detail;
        var probe = PseudoLocale.ItemsOf("B114")[0].ToString();
        Check.WithCustomMessage("The Contrast theme measured the tile like Midnight: the theme switch is not reaching the windows.")
            .That(TileDetail("Contrast", probe)).IsNotEqualTo(TileDetail("Midnight", probe));
    }
}
