namespace EyeCtrl.Core.Models;

/// <summary>
/// What the activation button names on its face: the action its completion opens, chosen from the
/// selection mode at the moment the offer is shown (B2, owner pick Q1).
///
/// <para>
/// In <see cref="SelectionMode.ManualSelection"/> the activation opens the magnifier, so the button
/// carries a magnifier glyph and „Lupe" / "Magnifier". Every other mode opens the action menu, so it
/// carries the menu glyph and „Menü" / "Menu". The label names the action, not the mode.
/// </para>
/// </summary>
public enum ActivationButtonContent
{
    /// <summary>A magnifier: completion opens the manual-selection magnifier.</summary>
    Magnifier,

    /// <summary>A menu: completion opens the action menu.</summary>
    Menu,
}
