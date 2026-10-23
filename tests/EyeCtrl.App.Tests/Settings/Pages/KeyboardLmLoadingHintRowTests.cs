namespace EyeCtrl.App.Tests.Settings.Pages;

using System.Linq;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

/// <summary>
/// The lm-loading-hint Settings row (KeyboardSettings.ShowLMLoadingIndicator): present on the
/// Keyboard page next to the prediction rows, writable, and — per the owner's decision —
/// "Alle Optionen" ONLY: the simple tier must not grow a row for a display nicety.
/// </summary>
public class KeyboardLmLoadingHintRowTests
{
    private static (EyeCtrlSettings Settings, SettingsPageContext Ctx) Build()
    {
        var settings = new EyeCtrlSettings();
        return (settings, TestSettingsPageContext.Build(settings: settings));
    }

    [Fact]
    public void TheRow_SitsAfterThePredictionCountToggle_DefaultsOn()
    {
        var (_, ctx) = Build();
        var rows = new KeyboardPageSource(ctx).Build().Single().Rows;

        var index = rows.Select((r, i) => (r, i)).Single(t => t.r.Id == "k_lm_loading_hint");
        var before = rows[index.i - 1].Id;
        Check.WithCustomMessage("the row belongs next to the prediction rows")
            .That(before).IsEqualTo("k_pred_count");

        var toggle = index.r as ToggleSpec;
        Check.That(toggle).IsNotNull();
        Check.WithCustomMessage("the setting's default is ON (KeyboardSettings)")
            .That(toggle!.Get()).IsTrue();
    }

    [Fact]
    public void TogglingTheRow_WritesTheSetting()
    {
        var (settings, ctx) = Build();
        var row = new KeyboardPageSource(ctx).Build().Single().Rows
            .OfType<ToggleSpec>().Single(t => t.Id == "k_lm_loading_hint");

        row.Set(false);

        Check.That(settings.Keyboard.ShowLMLoadingIndicator).IsFalse();
        row.Set(true);
        Check.That(settings.Keyboard.ShowLMLoadingIndicator).IsTrue();
    }

    [Fact]
    public void TheRow_IsNotSimpleMode_AlleOptionenOnly()
    {
        var (_, ctx) = Build();
        var source = new KeyboardPageSource(ctx);

        var row = source.Build().Single().Rows.Single(r => r.Id == "k_lm_loading_hint");
        Check.WithCustomMessage("the row must carry no Simple flag (Einfach excludes it)")
            .That(row.Simple).IsFalse();

        var filtered = SimpleSettingsFilter.Apply(source.Build().ToList());
        Check.WithCustomMessage(
                "the simple tier must not show the loading-hint row")
            .That(filtered.SelectMany(page => page.Rows).Any(r => r.Id == "k_lm_loading_hint"))
            .IsFalse();
    }
}
