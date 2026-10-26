namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// One banner that can occupy the bottom-centre slot (B4). The slot owns WHEN a banner may be on
/// screen; the occupant owns how it looks and how long it lives. The drift notice and the caregiver
/// alert are the first occupants, and B5's status cards join through this same surface.
///
/// <para>
/// The slot calls <see cref="ShowInSlot"/> only when this banner is the live one, and
/// <see cref="HideFromSlot"/> when it takes the banner down (pre-emption, withdrawal). An occupant
/// that leaves the screen by itself — its own hold or fade ended — raises <see cref="Closed"/>, and
/// the slot then shows the next queued banner. <see cref="HideFromSlot"/> must raise it too, so a
/// pre-empted banner can be resumed later without the slot's state going stale.
/// </para>
/// </summary>
public interface IBannerSlotOccupant
{
    /// <summary>Puts the banner on screen. A repeated call on the live occupant is a refresh
    /// (the window's own re-show discipline), not a second copy.</summary>
    void ShowInSlot();

    /// <summary>Takes the banner off the screen at once (pre-emption or withdrawal). The occupant
    /// then raises <see cref="Closed"/>.</summary>
    void HideFromSlot();

    /// <summary>Raised when the banner has left the screen — its own hold/fade completed, or
    /// <see cref="HideFromSlot"/> took it down.</summary>
    event EventHandler? Closed;

    /// <summary>The banner's visible bounds in physical pixels while it is on screen (the note
    /// gate's aggregate slot source and the restart notice's keep-clear); false when it is not.</summary>
    bool TryGetVisibleBounds(out PhysicalRect bounds);
}
