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
    /// <summary>Mouse whose left button is a solid piece lifted out of the outline ("popped").</summary>
    LeftClick,
    /// <summary>Mouse whose right button is a solid piece lifted out of the outline ("popped").</summary>
    RightClick,
    /// <summary>Two mice, the back one peeking out up-left — the outline itself says "twice".</summary>
    DoubleClick,
    /// <summary>Mouse whose wheel pillar rises through the body's socket — the pressed middle button.</summary>
    MiddleClick,
    /// <summary>Mouse with the left button popped out and motion bars trailing at the left.</summary>
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
    /// <summary>A bold question mark — toggles the action names on. Navigation, not an action.</summary>
    Help,
    /// <summary>A solid X — dismisses the menu without performing anything. Navigation, not an action.</summary>
    Close,
    /// <summary>A keyboard with key rows and a space bar — the ring's show-keyboard action.</summary>
    Keyboard,
    /// <summary>A keyboard with up and down chevrons — flips the keyboard between screen edges.</summary>
    KeyboardEdge,
    /// <summary>A camera with a clockwise arrow — captures the screen region again. Not a mouse: it re-shoots, not clicks.</summary>
    Recapture,
    /// <summary>An up-arrow modifier keycap — the Shift button on the left-edge mod bar.</summary>
    Shift,
    /// <summary>A caret-marked modifier keycap — the Ctrl button on the left-edge mod bar.</summary>
    Ctrl,
    /// <summary>A diamond-marked modifier keycap — the Alt button on the left-edge mod bar.</summary>
    Alt,
    /// <summary>Shift latched: same glyph, plus a rim circling the glyph and a filled margin dot.</summary>
    ShiftLatched,
    /// <summary>Ctrl latched (rim + margin dot).</summary>
    CtrlLatched,
    /// <summary>Alt latched (rim + margin dot).</summary>
    AltLatched,
}
