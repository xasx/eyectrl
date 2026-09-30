namespace EyeCtrl.Core.ActionMenu;

/// <summary>
/// Which of the radial action menu's two layers a <see cref="RingPlan"/> invocation describes.
/// </summary>
public enum RingLayer
{
    /// <summary>
    /// The first layer a menu opens on: the primary actions, a More target when the second layer
    /// holds anything, and — only for zoom/manual-selection opens — the Back that exits the menu.
    /// </summary>
    Primary,

    /// <summary>
    /// The layer behind More: every remaining visible action, then a Back that returns to the
    /// first layer.
    /// </summary>
    Secondary,
}
