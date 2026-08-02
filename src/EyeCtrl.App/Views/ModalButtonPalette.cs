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
    /// <summary>Fill and label for a choice kind, in the active theme.</summary>
    /// <param name="kind">The choice's meaning.</param>
    /// <returns>Background colour and a frozen foreground brush.</returns>
    public static (Color Background, SolidColorBrush Foreground) For(ModalChoiceKind kind) =>
        For(kind, Tokens.Current);

    /// <summary>
    /// Fill and label for a choice kind in a SPECIFIC theme.
    ///
    /// The theme is a parameter rather than a read of <see cref="Tokens.Current"/> so the
    /// contrast gates can measure every registered theme, not merely whichever one happens
    /// to be active. That matters here: these pairings were chosen against a fixed palette,
    /// and the theme picker now varies all five tokens underneath them. Mutating
    /// <see cref="Tokens.Current"/> from a test would be the alternative, and it is a shared
    /// global — the same shape as the type-init snapshotting the theme slice had to undo.
    /// </summary>
    /// <param name="kind">The choice's meaning.</param>
    /// <param name="theme">The theme to resolve the pairing against.</param>
    /// <returns>Background colour and a frozen foreground brush.</returns>
    public static (Color Background, SolidColorBrush Foreground) For(ModalChoiceKind kind, ITheme theme) => kind switch
    {
        // Bright fills: dark label.
        ModalChoiceKind.Affirmative => (theme.ToggleOn, theme.BrushSurfaceDark),
        ModalChoiceKind.Negative => (theme.AccentDanger, theme.BrushSurfaceDark),

        // Dark fill: light label. Same surface as the ordinary control buttons, so a
        // neutral option reads as "another way forward", not as a second confirm.
        ModalChoiceKind.Neutral => (theme.SurfaceMedium, theme.BrushTextPrimary),

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
