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
}
