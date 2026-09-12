namespace EyeCtrl.Controls.Icons;

using EyeCtrl.Core.Models;

/// <summary>
/// The main toolbar's icons, named by meaning rather than by appearance.
///
/// Separate from the button ids in <c>ToolbarViewModel</c> because one button shows several
/// icons over its life: Pause swaps to <see cref="Play"/> while paused, and the selection-mode
/// button shows one of three icons for the mode it is currently in.
///
/// Separate from <see cref="ActionIcon"/> because that set was authored as all mice until the
/// keyboard glyphs joined it. These are application controls and share none of that
/// vocabulary.
/// </summary>
public enum ToolbarIconKey
{
    /// <summary>Two upright bars — input is running; dwelling pauses it.</summary>
    Pause,
    /// <summary>Right-pointing triangle — input is paused; dwelling resumes it.</summary>
    Play,
    /// <summary>Slashed ring — <see cref="SelectionMode.None"/>, raw pixel targeting.</summary>
    SelectionNone,
    /// <summary>
    /// A ring with four tick marks outside it and a filled dot at its centre —
    /// <see cref="SelectionMode.ManualSelection"/>, zoom and dwell.
    /// </summary>
    SelectionManual,
    /// <summary>Ring around a control — <see cref="SelectionMode.AutoSelection"/>, snap to control.</summary>
    SelectionAuto,
    /// <summary>Key rows in a rounded case.</summary>
    Keyboard,
    /// <summary>Gear.</summary>
    Settings,
    /// <summary>Two offset windows.</summary>
    TaskSwitcher,
    /// <summary>Three stacked bars with the top one filled — a chosen mode among options; opens the modes menu.</summary>
    Modes,
    /// <summary>Mouse with left button filled — locked-action dwell mode, Left click (reuses the ActionIcons glyph).</summary>
    ModeLeftClick,
    /// <summary>Mouse with right button filled — locked-action dwell mode, Right click.</summary>
    ModeRightClick,
    /// <summary>Mouse with ripples and a "2" — locked-action dwell mode, Double click.</summary>
    ModeDoubleClick,
    /// <summary>Mouse with wheel pressed — locked-action dwell mode, Middle click.</summary>
    ModeMiddleClick,
    /// <summary>Counter-clockwise arrow — reset struggle adaptation to baseline.</summary>
    ResetAdaptation,
    /// <summary>Eye — the gaze provider is a real tracker.</summary>
    ProviderEye,
    /// <summary>Mouse — the gaze provider is the mouse fallback.</summary>
    ProviderMouse,
}
