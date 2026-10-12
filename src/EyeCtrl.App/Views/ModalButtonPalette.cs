namespace EyeCtrl.App.Views;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;

/// <summary>
/// Fill and label colours for modal buttons.
///
/// The label colour depends on the fill and cannot be a constant. Affirmative and
/// Negative are bright semantic tokens on the dark themes, so they take a dark
/// label; Neutral is a dark surface, so it takes a light one. Hardcoding the dark
/// label for all three is exactly the mistake that produced a 2.16:1 button
/// during the overlay migration, so the pairing lives here and is gated by
/// ModalButtonPaletteTests.
///
/// Daylight inverts the premise: its semantic fills are DARK on a light palette,
/// so the same kinds want a light label. The bright-kind label is therefore
/// MEASURED — the surface brush with the higher WCAG contrast against the fill —
/// which resolves to SurfaceDark in all three dark themes (preserving them
/// exactly) and to SurfaceLight in Daylight, where the base surface leaves
/// AccentDanger at 4.44:1, under the 4.5 floor Negative is held to.
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
        // Bright fills: the surface label that contrasts more (dark on the dark
        // themes, light on Daylight — see the class doc).
        ModalChoiceKind.Affirmative => (theme.ToggleOn, BrightLabel(theme.ToggleOn, theme)),
        ModalChoiceKind.Negative => (theme.AccentDanger, BrightLabel(theme.AccentDanger, theme)),

        // Mid-ramp fill, label from the far end (light label on the dark themes,
        // dark on Daylight). Same surface as the ordinary control buttons, so a
        // neutral option reads as "another way forward", not as a second confirm.
        ModalChoiceKind.Neutral => (theme.SurfaceMedium, theme.BrushTextPrimary),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Picks whichever of the theme's two surface labels — the translucent base
    /// (<see cref="ITheme.SurfaceDark"/>) or the opaque elevated step
    /// (<see cref="ITheme.SurfaceLight"/>) — contrasts more with <paramref name="fill"/>.
    ///
    /// Both candidates sit at the same end of the theme's ramp, so on the dark themes
    /// this resolves to the darker base exactly as the hardcoded rule did, and only a
    /// theme whose ramps run the other way (Daylight) flips to the lighter one.
    /// </summary>
    /// <param name="fill">The bright semantic fill the label sits on.</param>
    /// <param name="theme">The theme the label candidates come from.</param>
    /// <returns>The frozen surface brush with the higher contrast against the fill.</returns>
    private static SolidColorBrush BrightLabel(Color fill, ITheme theme) =>
        Contrast(fill, theme.SurfaceDark) >= Contrast(fill, theme.SurfaceLight)
            ? theme.BrushSurfaceDark
            : theme.BrushSurfaceLight;

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// The blend factor applied to dwell progress when this kind's fill is blended toward
    /// <see cref="Tokens.Accent"/> during a dwell (see <c>UpdateModalVisuals</c>).
    ///
    /// Polarity-free statement, because the factors must hold on both kinds of theme.
    /// The bright fills (<see cref="ModalChoiceKind.Affirmative"/>,
    /// <see cref="ModalChoiceKind.Negative"/>) carry a label from the theme's surface
    /// ramp, the OPPOSITE end of the ramp from the accent — so blending toward the
    /// accent moves the fill away from the label on every theme (Midnight's bright
    /// fills darken toward a bright accent; Daylight's darken further), and 0.5 is
    /// safe with a clear progress cue. The Neutral fill is a MID-ramp surface with a
    /// label from the far end: blending toward the accent moves the fill TOWARD the
    /// label's side of the ramp, so the 0.5 the bright kinds use would collapse the
    /// dark themes' Neutral to 2.62:1 at full dwell — below the 3.0 large-text AA
    /// floor, exactly when the button fires. The smaller factor keeps the worst case
    /// (full dwell, any theme) at ~5.1:1, clearing the 4.5 normal-text floor. The
    /// dwell still reads as "toward accent"; only its intensity adapts to the fill.
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
