namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class GeneralPageSourceTests
{
    [Fact]
    public void General_OnePageWithThemeBootPauseTrackAndThreeButtons()
    {
        var rows = new GeneralPageSource(TestSettingsPageContext.Build()).Build().Single().Rows;
        Check.That(rows.OfType<ComboBoxSpec>().Single().Id).IsEqualTo("g_theme");
        Check.That(rows.OfType<ToggleSpec>().Select(t => t.Id))
            .Contains(new[] { "g_boot", "g_pause", "g_track" });
        var buttons = rows.OfType<ButtonSpec>().ToList();
        Check.That(buttons.Select(b => b.Id)).Contains(new[] { "g_restart", "g_reset", "g_resetall" });
        // The General page has THREE reset buttons (g_restart, g_reset, g_resetall), so
        // ContainsExactly(ButtonKind.Reset) would wrongly pin count=1 and fail. NFluent 3.1.0 has
        // no ContainsOnly on IEnumerable<T> (CS1061 — the AGENTS.md cheat sheet lists
        // ContainsOnlyElementsThatMatch, not ContainsOnly). This asserts "all (any count) buttons
        // are Reset kind" without pinning the count.
        Check.That(buttons.Select(b => b.Kind)).ContainsOnlyElementsThatMatch(k => k == ButtonKind.Reset);
    }
}
