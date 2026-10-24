namespace EyeCtrl.ViewModels;

/// <summary>
/// The interaction context that currently shows the shared right-edge button bar. Each context
/// claims the bar when it shows its buttons and releases it when it hides them; a claim by one
/// owner displaces whatever another owner had up, and the displaced owner's buttons are put back
/// when the displacer releases the bar.
/// </summary>
/// <remarks>
/// Closed on purpose: the bar's contents are a safety surface (the only gaze-reachable way out of
/// a drag, scroll or repeat mode), so "who may take the bar from whom" is a fixed, reviewable list
/// rather than an arbitrary token. Adding a context means adding a member here and a claim site.
/// </remarks>
public enum EdgeBarOwner
{
    /// <summary>The action menu's Close/Help pair, and the left-edge modifier bar.</summary>
    ActionMenu,

    /// <summary>The manual-selection magnifier's Close/Recapture pair.</summary>
    ManualSelection,

    /// <summary>The keyboard's Close/Flip pair, on its own tracked anchor.</summary>
    Keyboard,

    /// <summary>Repeat mode's stop button.</summary>
    Repeat,

    /// <summary>Scroll mode's stop button.</summary>
    Scroll,

    /// <summary>A live drag's Cancel/Back pair.</summary>
    Drag,
}
