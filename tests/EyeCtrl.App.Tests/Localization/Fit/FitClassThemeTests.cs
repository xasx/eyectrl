namespace EyeCtrl.App.Tests.Localization.Fit;

using System.Linq;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09 review-01 D1 (the coordinator's decision): a class whose ROOM changes with the theme binds
/// English in every theme, so the tightest — Contrast — decides. The themes differ only in
/// <c>BorderThicknessStandard</c> (1 in Midnight and Ember, 2 in Contrast), and two classes take
/// their room from it: the quick-actions tile (B114: a 106 DIP label under Midnight, 104 under
/// Contrast), the caregiver alert's banner (C-alert: 670 → 668) and the eye-care summary card
/// (its border widens the card by 2). The Settings controls use
/// literal borders and are theme-immune. T10's German must then fit every theme as well.
///
/// <para>
/// Switching the theme writes process-global state, so this lives in the exclusive "ActiveTheme"
/// collection and restores both the theme and the Application dictionary (<see cref="ThemeScope"/>).
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class FitClassThemeTests
{
    /// <summary>
    /// The fit classes whose room comes from a themed token. PV1 joins them: the preview card's
    /// border is <c>StandardBorderThickness</c>, so Contrast's 2 DIP border is the tightest panel.
    /// </summary>
    internal static readonly string[] ThemeDependentClasses = ["B114", "C-alert", "card", "PV1"];

    private readonly ITestOutputHelper _output;

    public FitClassThemeTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string, string> ThemesAndClasses()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in ThemeRegistry.Names)
        {
            foreach (var fitClass in ThemeDependentClasses)
                data.Add(theme, fitClass);
        }

        return data;
    }

    [StaTheory]
    [MemberData(nameof(ThemesAndClasses))]
    public void EveryEnglishText_FitsItsClass_InEveryTheme(string theme, string fitClass)
    {
        using var scope = ThemeScope.Activate(ThemeRegistry.Resolve(theme));
        FitClassTests.AssertEnglishFits(fitClass, _output, theme);
    }

    /// <summary>
    /// The theme switch must reach the windows the measurers build, or every theme above measures
    /// Midnight: the same tile text gets 104 DIP of label width under Contrast and 106 under
    /// Midnight.
    /// </summary>
    [StaFact]
    public void TheContrastTheme_NarrowsTheTileThePerThemeGateMeasures()
    {
        var item = FitCatalog.ItemsOf("B114").First();
        FitVerdict midnight, contrast;
        using (ThemeScope.Activate(MidnightTheme.Instance))
            midnight = AppFitMeasurers.Measurers["B114"](item.Key, item.Text);
        using (ThemeScope.Activate(ContrastTheme.Instance))
            contrast = AppFitMeasurers.Measurers["B114"](item.Key, item.Text);

        _output.WriteLine($"Midnight: {midnight}");
        _output.WriteLine($"Contrast: {contrast}");
        Check.That(midnight.Detail).Contains("label width 106");
        Check.WithCustomMessage("Contrast measured the tile like Midnight: the theme switch is not reaching the windows.")
            .That(contrast.Detail).Contains("label width 104");
    }

    /// <summary>The list is complete: every class whose measurer's room moved with the theme is in it.</summary>
    [StaFact]
    public void EveryThemeDependentClass_IsCheckedPerTheme()
    {
        var moved = new System.Collections.Generic.List<string>();
        foreach (var (fitClass, measure) in AppFitMeasurers.Measurers)
        {
            var item = FitCatalog.ItemsOf(fitClass).FirstOrDefault();
            if (item is null) continue;
            var text = AppFitMeasurers.Samples(item)[0];
            string midnight, contrast;
            using (ThemeScope.Activate(MidnightTheme.Instance))
                midnight = measure(item.Key, text).Detail;
            using (ThemeScope.Activate(ContrastTheme.Instance))
                contrast = measure(item.Key, text).Detail;
            if (midnight != contrast) moved.Add(fitClass);
        }

        _output.WriteLine("Classes whose measurement moves with the theme: " + string.Join(", ", moved));
        Check.WithCustomMessage("A class's room moves with the theme but is not checked per theme: "
                + string.Join(", ", moved.Except(ThemeDependentClasses)))
            .That(moved.Except(ThemeDependentClasses)).IsEmpty();
    }
}
