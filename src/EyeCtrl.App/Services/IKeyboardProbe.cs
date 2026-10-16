namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// The keyboard visibility seam the notes read (P1 D5). A small, non-lazy, UI-thread object —
/// deliberately NOT the D17 inventory, whose lazy construction and preview-panel focus serve a
/// different consumer. Wired once from EventCoordinator into <see cref="INoteManager.SetKeyboardProbe"/>.
/// </summary>
public interface IKeyboardProbe
{
    /// <summary>Whether the eye keyboard is on screen (<c>KeyboardViewModel.IsVisible</c>).</summary>
    bool IsVisible { get; }

    /// <summary>
    /// The keyboard's hit area in physical pixels: the keyboard window's bounds (when the keyboard
    /// is visible), plus the visible edge bar's button union — the Close/Flip pair that sits
    /// OUTSIDE the keyboard window (KeyboardCoordinator's bar anchor). The edge-bar rect appears
    /// whenever the BAR is visible, even while the keyboard itself is hidden (fixup n2): the
    /// behaviour is conservative on purpose — the gate would rather over-cover than let a dwell
    /// slip under a hit area.
    /// </summary>
    IReadOnlyList<PhysicalRect> Rects();

    /// <summary>
    /// Raised one dispatcher hop after any of the probe's sources change the keyboard's on-screen
    /// geometry: the keyboard VM's IsVisible/IsEmojiMode, the keyboard window's SizeChanged/Loaded,
    /// KeyboardEdgeToggledMessage, and the edge bar's ButtonSetChanged — the same source set
    /// GazeConsumerInventory wires.
    /// </summary>
    event Action? Changed;
}
