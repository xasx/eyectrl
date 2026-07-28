namespace EyeCtrl.App.Tests;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// EyeCtrl.Core cannot reference EyeCtrl.Controls, so these four persisted colour
/// defaults are string literals that must match Tokens by value (spec §5 Phase 2).
/// This test is the only thing keeping them in sync.
/// </summary>
public class SettingsDefaultsMatchTokensTests
{
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    [Fact]
    public void CursorCircleColour_DefaultsToAccent() =>
        Check.That(Parse(new CursorSettings().CircleColor)).IsEqualTo(Tokens.Accent);

    [Fact]
    public void CalibrationTargetColour_DefaultsToAccent() =>
        Check.That(Parse(new CalibrationSettings().TargetColor)).IsEqualTo(Tokens.Accent);

    [Fact]
    public void AutoSelectionHighlight_DefaultsToAccent() =>
        Check.That(Parse(new AutoSelectionSettings().HighlightColor)).IsEqualTo(Tokens.Accent);

    [Fact]
    public void DragTrail_DefaultsToAccentWarning()
    {
        // Warm and deliberately distinct from Accent, so a motion trail is never
        // mistaken for dwell progress (spec §5 step 15).
        Check.That(Parse(new DragSettings().TrailColor)).IsEqualTo(Tokens.AccentWarning);
    }
}
