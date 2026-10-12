namespace EyeCtrl.App.Tests;

using System.Windows.Media;
using EyeCtrl.App.Services;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The ring/trail colour migration added with Daylight (review round 1, I1).
///
/// Persisted haloed-mark colours are absolute hexes (§7.1), so a theme switch
/// carries the old theme's mark onto the new theme's StrokeHalo — where the
/// mark/halo edge §4.1.3 depends on can collapse to 1.25:1.
/// <see cref="ThemeStartup.MigrateHaloedMarks"/> re-derives them at theme
/// application. These tests pin the rule end to end, through
/// <see cref="ThemeStartup.Apply"/> — the same entry the app uses.
///
/// Serialised into the ActiveTheme collection like ThemeStartupTests: Apply
/// activates a theme, which mutates the process-global <see cref="Tokens.Current"/>.
/// </summary>
[Collection("ActiveTheme")]
public class ThemeStartupMarkMigrationTests
{
    private static EyeCtrlSettings SettingsWith(string themeName, string circleColor, string trailColor) =>
        new()
        {
            Theme = { Name = themeName },
            Cursor = { CircleColor = circleColor },
            Drag = { TrailColor = trailColor },
        };

    private static double Contrast(Color a, Color b)
    {
        double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        var (la, lb) = (Channel(a.R) * 0.2126 + Channel(a.G) * 0.7152 + Channel(a.B) * 0.0722,
                        Channel(b.R) * 0.2126 + Channel(b.G) * 0.7152 + Channel(b.B) * 0.0722);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    [Fact]
    public void DefaultRing_OnDaylight_BecomesTheDaylightAccent_AndClearsTheHaloBar()
    {
        try
        {
            var settings = SettingsWith("Daylight", SettingsDefaults.Accent, SettingsDefaults.AccentWarning);

            ThemeStartup.Apply(settings, new());

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#07343E");
            // The property the migration exists for: the migrated mark clears 7:1
            // against the theme it now sits on. The pre-migration value managed 1.25:1.
            Check.That(Contrast(Parse(settings.Cursor.CircleColor), DaylightTheme.Instance.StrokeHalo))
                 .IsStrictlyGreaterThan(7.0);
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void CrossThemePreset_MapsToTheSameIndexPreset_GreenStaysTheRingGreen()
    {
        try
        {
            // Midnight's Green is preset #1; Daylight's preset #1 is the clamped ring
            // green (DwellColorPicker.RingGreen), not the semantic AccentSuccess.
            var settings = SettingsWith("Daylight", "#7EE787", "#7EE787");

            ThemeStartup.Apply(settings, new());

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#005200");
            Check.That(settings.Drag.TrailColor).IsEqualTo("#005200");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void TrailDefault_OnDaylight_MapsToTheDaylightWarning()
    {
        try
        {
            var settings = SettingsWith("Daylight", "#07343E", "#E8A33C");

            ThemeStartup.Apply(settings, new());

            // The trail's DEFAULT value is Midnight's warning — a preset of another
            // theme, so it maps by index to Daylight's warning, NOT to the accent the
            // ring default uses. (The failing-custom snap to this same warning preset
            // is pinned separately, by CustomColour_FailingTheNewHaloBar.)
            Check.That(settings.Drag.TrailColor).IsEqualTo("#7A1000");
            Check.That(settings.Cursor.CircleColor).IsEqualTo("#07343E");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void DaylightGreenAndTextPresets_OnMidnight_MapByIndex_Symmetrically()
    {
        try
        {
            // GREEN and TEXT deliberately, not the accent/warning: those sit at the
            // same index as each setting's snap default (0 and 2), so a test built on
            // them cannot tell same-index mapping from the default snap apart (round
            // 2's mutation (c) finding). Green is index 1 — mapped by index it stays
            // green; if the same-index branch were deleted it would fail Midnight's
            // halo and snap to the accent, a different colour.
            var settings = SettingsWith("Midnight", "#005200", "#0C0D12");

            ThemeStartup.Apply(settings, new());

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#7EE787");
            Check.That(settings.Drag.TrailColor).IsEqualTo("#D6DEF5");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void CustomColour_ClearingTheNewHaloBar_IsKept()
    {
        try
        {
            // #802000 is nobody's preset and clears 9.9:1 against white — a user
            // choice the migration has no business touching.
            var settings = SettingsWith("Daylight", "#802000", "#802000");

            ThemeStartup.Apply(settings, new());

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#802000");
            Check.That(settings.Drag.TrailColor).IsEqualTo("#802000");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void CustomColour_FailingTheNewHaloBar_SnapsToEachSettingDefault()
    {
        try
        {
            // #FF00FF is nobody's preset and manages only ~3.2:1 on white.
            var settings = SettingsWith("Daylight", "#FF00FF", "#FF00FF");

            ThemeStartup.Apply(settings, new());

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#07343E");
            Check.That(settings.Drag.TrailColor).IsEqualTo("#7A1000");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void HandEditedHex_IsMatchedCaseInsensitively_AndNormalised()
    {
        try
        {
            var settings = SettingsWith("Daylight", "#a5f3fc", "#e8a33c");

            ThemeStartup.Apply(settings, new());

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#07343E");
            Check.That(settings.Drag.TrailColor).IsEqualTo("#7A1000");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void MidnightDefaults_OnMidnight_AreUntouched()
    {
        try
        {
            // Today's shipping flow must be a no-op: every default is already a
            // Midnight preset, and the dark themes' flow predates the migration.
            var settings = new EyeCtrlSettings();

            ThemeStartup.Apply(settings, new());

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#A5F3FC");
            Check.That(settings.Drag.TrailColor).IsEqualTo("#E8A33C");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void AlreadyTheNewThemePreset_IsUnchanged()
    {
        try
        {
            var settings = SettingsWith("Daylight", "#005200", "#7A1000");

            ThemeStartup.Apply(settings, new());

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#005200");
            Check.That(settings.Drag.TrailColor).IsEqualTo("#7A1000");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }
}
