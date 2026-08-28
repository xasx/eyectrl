namespace EyeCtrl.Core.Models;

/// <summary>
/// Names one drawn icon in the action menu's set. A name only — the path data lives in
/// <c>EyeCtrl.Controls.Icons.ActionIcons</c>, and the mapping from an
/// <see cref="InteractionAction"/> lives in <c>ActionMenuViewModel</c>.
///
/// <para>
/// <b>Why the name is in Core rather than beside the geometry.</b> EyeCtrl.ViewModels must be
/// able to say WHICH icon a menu item wants without referencing WPF, and
/// <c>EdgeButtonDefinition</c> needs the same. A ViewModels reference to EyeCtrl.Controls would
/// pull System.Windows.Media into a project whose tests deliberately run without a WPF
/// Application.
/// </para>
///
/// <para>
/// Members do NOT correspond one-to-one with <see cref="InteractionAction"/>:
/// <see cref="Back"/> is a navigation affordance rather than an action, and this enum will grow
/// entries for the More layer and the Help toggle that no InteractionAction names.
/// </para>
/// </summary>
public enum ActionIcon
{
    /// <summary>Mouse with its left button filled.</summary>
    LeftClick,
    /// <summary>Mouse with its right button filled.</summary>
    RightClick,
    /// <summary>Left button filled, two click ripples off the upper-left, and a "2" at the right margin.</summary>
    DoubleClick,
    /// <summary>Left button filled, with motion bars trailing at the left.</summary>
    Drag,
    /// <summary>Mouse with a wheel and an up chevron at the right margin.</summary>
    ScrollUp,
    /// <summary>Mouse with a wheel and a down chevron at the right margin.</summary>
    ScrollDown,
    /// <summary>Mouse with a wheel and a left-pointing chevron at the right margin.</summary>
    ScrollLeft,
    /// <summary>Mouse with a wheel and a right-pointing chevron at the right margin.</summary>
    ScrollRight,
    /// <summary>Mouse with a wheel and both up and down chevrons — the gaze-joystick scroll mode.</summary>
    ScrollMode,
    /// <summary>A sticky note with a pin through its top edge.</summary>
    PinNote,
    /// <summary>A return arrow. Navigation, not an action.</summary>
    Back,
    /// <summary>Three dots — opens the ring's second layer. Navigation, not an action.</summary>
    More,
    /// <summary>A question mark — toggles the action names on. Navigation, not an action.</summary>
    Help,
    /// <summary>An X — dismisses the menu without performing anything. Navigation, not an action.</summary>
    Close,
}
