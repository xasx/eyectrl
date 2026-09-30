namespace EyeCtrl.Core.ActionMenu;

/// <summary>
/// What a dwell on a ring item does. Replaced a bool <c>IsBackButton</c> on 2026-08-26: the ring
/// gained a second layer, and with it three distinct non-action affordances that a bool cannot
/// separate.
///
/// <para>
/// Lives in Core beside <see cref="RingPlan"/> because the plan's slots are read by both the
/// runtime ring and the settings preview, and both must agree on what a slot IS. (It began life
/// in EyeCtrl.ViewModels, where only the runtime VM consumed it.)
/// </para>
/// </summary>
public enum ActionMenuItemKind
{
    /// <summary>Performs its action and closes the menu.</summary>
    Action,

    /// <summary>Opens the ring's second layer. Does not close the menu.</summary>
    OpenMore,

    /// <summary>Returns to the ring's first layer. Does not close the menu.</summary>
    BackToPrimary,

    /// <summary>Leaves the menu entirely — the Back that returns from zoom or manual selection.</summary>
    ExitMenu,
}
