namespace EyeCtrl.App.Views;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;

/// <summary>
/// Fill and label colours for modal buttons.
///
/// The label colour depends on the fill and cannot be a constant. Affirmative and
/// Negative are bright semantic tokens, so they take a dark label; Neutral is a dark
/// surface, so it takes a light one. Hardcoding the dark label for all three is exactly
/// the mistake that produced a 2.16:1 button during the overlay migration, so the pairing
/// lives here and is gated by ModalButtonPaletteTests.
/// </summary>
public static class ModalButtonPalette
{
    /// <summary>Fill and label for a choice kind.</summary>
    /// <param name="kind">The choice's meaning.</param>
    /// <returns>Background colour and a frozen foreground brush.</returns>
    public static (Color Background, SolidColorBrush Foreground) For(ModalChoiceKind kind) => kind switch
    {
        // Bright fills: dark label.
        ModalChoiceKind.Affirmative => (Tokens.ToggleOn, Tokens.BrushSurfaceDark),
        ModalChoiceKind.Negative => (Tokens.AccentDanger, Tokens.BrushSurfaceDark),

        // Dark fill: light label. Same surface as the ordinary control buttons, so a
        // neutral option reads as "another way forward", not as a second confirm.
        ModalChoiceKind.Neutral => (Tokens.SurfaceMedium, Tokens.BrushTextPrimary),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// The blend factor applied to dwell progress when this kind's fill is blended toward
    /// <see cref="Tokens.Accent"/> during a dwell (see <c>UpdateModalVisuals</c>).
    ///
    /// The bright fills (<see cref="ModalChoiceKind.Affirmative"/>,
    /// <see cref="ModalChoiceKind.Negative"/>) gain label contrast as they blend toward the
    /// bright Accent, so the app-wide factor of 0.5 is safe and gives a clear progress cue.
    /// The dark <see cref="ModalChoiceKind.Neutral"/> fill carries a LIGHT label: blending
    /// toward the bright Accent raises the fill's luminance while the label stays fixed, so
    /// a 0.5 factor collapses contrast to 2.62:1 at full dwell — below the 3.0 large-text AA
    /// floor, exactly when the button fires. The smaller factor keeps the worst case (full
    /// dwell) at ~5.1:1, clearing the 4.5 normal-text floor. The dwell still reads as
    /// "toward accent"; only its intensity adapts to the fill.
    /// </summary>
    /// <param name="kind">The choice's meaning.</param>
    /// <returns>The blend factor in [0, 1] applied to dwell progress.</returns>
    public static double DwellBlendFactor(ModalChoiceKind kind) => kind switch
    {
        ModalChoiceKind.Affirmative => 0.5,
        ModalChoiceKind.Negative => 0.5,
        ModalChoiceKind.Neutral => 0.25,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
