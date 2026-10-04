namespace EyeCtrl.Core;

/// <summary>
/// The owners of a fixation-suppression claim on <see cref="GazeEngine"/>. Each owner acquires and
/// releases only its own claim; fixation stays suppressed while any claim is held. Add a member
/// for a new owner rather than sharing one — a shared key brings back last-writer-wins.
/// </summary>
public enum FixationSuppressor
{
    /// <summary>The on-screen keyboard, while shown from the toolbar.</summary>
    Keyboard,

    /// <summary>Calibration, while gaze is collected at the targets and during training.</summary>
    Calibration,

    /// <summary>The toolbar's Modes menu, while open.</summary>
    ModesMenu,

    /// <summary>External dwell, while a cooperating app's target is active.</summary>
    ExternalDwell,
}
