namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09 PR 2, NON-binding: every App fit class measured under the pseudo-locale, in each of the
/// three themes — a theme can change a box (Contrast's 2 DIP border makes the quick-actions tile's
/// label 104 DIP, not 106) — written to <c>t09-fit-report/pseudo-locale-app.md</c> in the test
/// output as an early warning for T10. It asserts only that every key was measured in every theme;
/// a pseudo-locale text that does not fit is a finding, never a failure.
///
/// <para>
/// Switching the theme writes <see cref="Tokens.Current"/> and the Application's resources, both
/// process-global, so this lives in the exclusive "ActiveTheme" collection and restores both: the
/// theme to Midnight, and the Application dictionary to exactly the keys and values it had.
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class PseudoLocaleFitReportTests
{
    private readonly ITestOutputHelper _output;

    public PseudoLocaleFitReportTests(ITestOutputHelper output) => _output = output;

    [StaFact]
    public void PseudoLocaleFitReport_IsWritten_ForEveryKeyInEveryTheme()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var resources = Application.Current.Resources;
        var before = resources.Keys.Cast<object>().ToDictionary(k => k, k => resources[k]);
        var rows = new List<FitReportRow>();
        try
        {
            foreach (var theme in ThemeRegistry.All)
            {
                Tokens.Activate(theme);
                ThemeResourceApplier.Apply(resources, theme);
                foreach (var (fitClass, measure) in AppFitMeasurers.Measurers.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    foreach (var item in PseudoLocale.ItemsOf(fitClass))
                    {
                        foreach (var text in AppFitMeasurers.Samples(item))
                            rows.Add(new FitReportRow(theme.Name, fitClass, item.ToString(), text, measure(item.Key, text)));
                    }
                }
            }
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
            foreach (var key in resources.Keys.Cast<object>().ToList())
            {
                if (!before.ContainsKey(key)) resources.Remove(key);
            }

            foreach (var (key, value) in before) resources[key] = value;
        }

        var path = FitReport.Write("pseudo-locale-app.md", "T09 pseudo-locale fit report — App", rows);
        foreach (var group in rows.GroupBy(r => (r.Theme, r.FitClass)))
            _output.WriteLine($"{group.Key.Theme} {group.Key.FitClass}: {group.Count(r => !r.Verdict.Fits)} of {group.Count()} do not fit");
        _output.WriteLine("Report: " + path);

        var perTheme = AppFitMeasurers.Measurers.Keys.Sum(c => PseudoLocale.ItemsOf(c).Sum(i => AppFitMeasurers.Samples(i).Count));
        Check.WithCustomMessage("The report did not measure every key in every theme.")
            .That(rows).HasSize(perTheme * ThemeRegistry.All.Count);
        Check.That(File.Exists(path)).IsTrue();
        // Contrast's 2 DIP border must reach the tile: the same key measures a different box there.
        string TileDetail(string theme, string key) =>
            rows.Single(r => r.Theme == theme && r.FitClass == "B114" && r.Key == key).Verdict.Detail;
        var probe = PseudoLocale.ItemsOf("B114")[0].ToString();
        Check.WithCustomMessage("The Contrast theme measured the tile like Midnight: the theme switch is not reaching the windows.")
            .That(TileDetail("Contrast", probe)).IsNotEqualTo(TileDetail("Midnight", probe));
    }
}
