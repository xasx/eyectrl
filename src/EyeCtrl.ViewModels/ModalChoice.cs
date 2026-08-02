namespace EyeCtrl.ViewModels;

using System;

/// <summary>
/// What a modal choice means, so the view can colour it. Deliberately semantic rather
/// than a colour: EyeCtrl.ViewModels references only EyeCtrl.Core and cannot see the
/// palette in EyeCtrl.Controls.
/// </summary>
public enum ModalChoiceKind
{
    /// <summary>The action the modal is asking about. Rendered as the confirm button.</summary>
    Affirmative,

    /// <summary>An alternative way forward — neither the primary action nor backing out.</summary>
    Neutral,

    /// <summary>Backing out without acting.</summary>
    Negative,
}

/// <summary>
/// One button in a settings modal.
/// </summary>
/// <param name="Id">
/// Dwell-target id, unique within the modal. The two-button form uses <c>modal_yes</c>
/// and <c>modal_no</c>, which existing confirmations and their tests depend on.
/// </param>
/// <param name="Label">Text on the button.</param>
/// <param name="Kind">Drives colour; see <see cref="ModalChoiceKind"/>.</param>
/// <param name="OnChosen">
/// Invoked when this choice is taken, before the modal closes. Null for a choice that
/// only dismisses.
/// </param>
public sealed record ModalChoice(string Id, string Label, ModalChoiceKind Kind, Action? OnChosen);
