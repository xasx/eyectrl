namespace EyeCtrl.App.Tests.Settings.Pages;

using System.Linq;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The settings resets must not reopen the cross-theme ring/trail carry-over at runtime
/// (review round 2, N2). Both reset buttons write <see cref="SettingsDefaults"/> — Midnight
/// absolutes — and previously painted the live theme's halo with them until the next
/// restart; they now re-derive through <see cref="Services.ThemeStartup.MigrateHaloedMarks"/>.
///
/// Serialised into the ActiveTheme collection: these activate Daylight, which mutates the
/// process-global <see cref="Tokens.Current"/>; Midnight is restored in each finally.
/// </summary>
[Collection("ActiveTheme")]
public class ResetMarkMigrationTests
{
    [Fact]
    public void CursorReset_OnDaylight_RewritesTheRingAsTheThemeAccent()
    {
        var settings = new EyeCtrlSettings();
        try
        {
            Tokens.Activate(DaylightTheme.Instance);
            var rec = new RecordingSettingsPageContext(settings);
            var reset = new CursorPageSource(rec.Context).Build()
                .SelectMany(p => p.Rows).OfType<ButtonSpec>()
                .Single(b => b.Id == "c_reset");

            reset.OnActivate();
            Check.That(rec.LastModalOnYes).IsNotNull();
            rec.LastModalOnYes!();

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#07343E");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void AdvancedReset_OnDaylight_RewritesTheTrailAsTheThemeWarning()
    {
        var settings = new EyeCtrlSettings();
        try
        {
            Tokens.Activate(DaylightTheme.Instance);
            var rec = new RecordingSettingsPageContext(settings);
            var reset = new AdvancedPageSource(rec.Context).Build()
                .SelectMany(p => p.Rows).OfType<ButtonSpec>()
                .Single(b => b.Id == "av_reset");

            reset.OnActivate();
            Check.That(rec.LastModalOnYes).IsNotNull();
            rec.LastModalOnYes!();

            Check.That(settings.Drag.TrailColor).IsEqualTo("#7A1000");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }
}
