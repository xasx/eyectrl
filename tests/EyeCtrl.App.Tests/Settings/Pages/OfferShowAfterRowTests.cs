namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// Round button B3's row on the Dwell („Verweilzeiten“) page: „Aktivator erscheint nach (ms)“, right
/// after the activator's own dwell, spanning exactly the setting's 500–2500 guard in steps of 100, and
/// live (its Set writes the setting the engine follows).
/// </summary>
public class OfferShowAfterRowTests
{
    [Fact]
    public void TheRow_SitsRightAfterTheActivatorsDwell_WithTheGuardsRangeAndStep100()
    {
        var ctx = TestSettingsPageContext.Build();
        var rows = new DwellPageSource(ctx).Build()[0].Rows;
        int act = rows.ToList().FindIndex(r => r.Id == "d_act");

        var row = (SliderSpec)rows[act + 1];
        Check.That(row.Id).IsEqualTo("d_act_show");
        Check.That(row.Min).IsEqualTo(500);
        Check.That(row.Max).IsEqualTo(2500);
        Check.That(row.Step).IsEqualTo(100);
        Check.That(row.Get()).IsEqualTo(1000);

        row.Set(1800);
        Check.That(ctx.Settings.Dwell.OfferShowAfterMs).IsEqualTo(1800);
    }

    [Fact]
    public void TheRow_ReadsInEnglishAndInTheShippedGerman()
    {
        Check.That(StringTable.Current.T("settings.dwell.activation_show_after.label"))
            .IsEqualTo("Activator appears after (ms)");
        Check.That(StringTable.Current.T("settings.dwell.activation_show_after.description"))
            .IsEqualTo("How long you look at one spot before the activator appears.");

        Check.That(GermanDraft.TextOf("settings.dwell.activation_show_after.label", ""))
            .IsEqualTo("Aktivator erscheint nach (ms)");
        Check.That(GermanDraft.TextOf("settings.dwell.activation_show_after.description", ""))
            .IsEqualTo("Wie lange Sie auf eine Stelle schauen, bevor der Aktivator erscheint.");
    }
}
