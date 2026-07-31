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
///
/// Pinned to <see cref="MidnightTheme"/> rather than to Tokens.Current: these are
/// *defaults for persisted, theme-independent* values (a user's chosen cursor
/// colour is not rewritten when they switch theme), so they track the shipping
/// palette specifically, not whichever theme happens to be active. Every preset a
/// user can pick still clears the §4.1.3 halo bar in every theme — worst case
/// 9.13:1 — which is what makes theme-independence safe here.
/// </summary>
public class SettingsDefaultsMatchTokensTests
{
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    [Fact]
    public void CursorCircleColour_DefaultsToMidnightAccent() =>
        Check.That(Parse(new CursorSettings().CircleColor))
             .IsEqualTo(MidnightTheme.Instance.Accent);

    [Fact]
    public void CalibrationTargetColour_DefaultsToMidnightAccent() =>
        Check.That(Parse(new CalibrationSettings().TargetColor))
             .IsEqualTo(MidnightTheme.Instance.Accent);

    [Fact]
    public void AutoSelectionHighlight_DefaultsToMidnightAccent() =>
        Check.That(Parse(new AutoSelectionSettings().HighlightColor))
             .IsEqualTo(MidnightTheme.Instance.Accent);

    [Fact]
    public void DragTrail_DefaultsToMidnightAccentWarning()
    {
        // Warm and deliberately distinct from Accent, so a motion trail is never
        // mistaken for dwell progress (spec §5 step 15).
        Check.That(Parse(new DragSettings().TrailColor))
             .IsEqualTo(MidnightTheme.Instance.AccentWarning);
    }
}
