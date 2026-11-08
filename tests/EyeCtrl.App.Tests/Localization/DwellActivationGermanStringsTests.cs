namespace EyeCtrl.App.Tests.Localization;

using System.Linq;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// R3: the „Aktivierung" row's strings exist in the SHIPPED German (the owner's machine is de-DE;
/// a settings surface missing any key degrades whole-surface to English, which would strand the
/// owner on English sliders). The exact texts are pinned as literals — the owner's decision
/// wording (08.11): „Ring füllt sich" / „Knopf neben dem Ring" and „Füllzeit des Rings (ms)".
/// Exclusive collection: the global table is activated and restored around each test.
/// </summary>
[Collection("StringTable Tests (App)")]
public class DwellActivationGermanStringsTests
{
    [Fact]
    public void TheActivationRowStrings_ResolveGerman()
    {
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            var t = StringTable.Current;
            Check.WithCustomMessage("settings.dwell.activation_style.label")
                .That(t.T("settings.dwell.activation_style.label")).IsEqualTo("Aktivierung");
            Check.WithCustomMessage("settings.dwell.activation_style.option.rest_fill")
                .That(t.T("settings.dwell.activation_style.option.rest_fill")).IsEqualTo("Ring füllt sich");
            Check.WithCustomMessage("settings.dwell.activation_style.option.offer_button")
                .That(t.T("settings.dwell.activation_style.option.offer_button")).IsEqualTo("Knopf neben dem Ring");
            Check.WithCustomMessage("settings.dwell.rest_fill.label")
                .That(t.T("settings.dwell.rest_fill.label")).IsEqualTo("Füllzeit des Rings (ms)");

            // The descriptions resolve German too (and so differ from the English table's) —
            // including d_act's RestFill description (holistic fixup-01 F5).
            foreach (var key in new[]
                     {
                         "settings.dwell.activation_style.description",
                         "settings.dwell.rest_fill.description",
                         "settings.dwell.activation.other_uses.description",
                     })
            {
                Check.WithCustomMessage($"{key} resolved its English — the German entry is missing")
                    .That(t.T(key)).Not.IsEqualTo(StringTable.EmbeddedEnglish.T(key));
                Check.That(string.IsNullOrWhiteSpace(t.T(key))).IsFalse();
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void TheActivationRowBuildsGerman_InTheShippedTable()
    {
        // End to end through the page source: the combo's Display and both rows' labels come out
        // German when the shipped table is active — not just the key lookups above.
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            var rec = new EyeCtrl.App.Tests.Settings.Pages.RecordingSettingsPageContext();
            var rows = new EyeCtrl.App.Views.Settings.DwellPageSource(rec.Context).Build()
                .SelectMany(p => p.Rows).ToList();
            var combo = rows.OfType<EyeCtrl.App.Views.Settings.ComboBoxSpec>()
                .Single(c => c.Id == "d_activation");
            Check.That(combo.Display).ContainsExactly("Ring füllt sich", "Knopf neben dem Ring");
            Check.That(combo.Label).IsEqualTo("Aktivierung");
            Check.That(rows.OfType<EyeCtrl.App.Views.Settings.SliderSpec>()
                .Single(s => s.Id == "d_rest_fill").Label).IsEqualTo("Füllzeit des Rings (ms)");
            var dAct = rows.OfType<EyeCtrl.App.Views.Settings.SliderSpec>().Single(s => s.Id == "d_act");
            Check.That(dAct.Label).IsEqualTo("Aktivator (ms)");
            Check.That(dAct.Description).StartsWith("Gilt für den Augenpflege-Hinweis");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }
}
