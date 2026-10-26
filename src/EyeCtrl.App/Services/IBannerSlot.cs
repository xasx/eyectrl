namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// The one bottom-centre banner slot (B4). The production implementation is
/// <see cref="BannerSlot"/>; the interface exists so the presenters' routing (enqueue vs pre-empt)
/// is pinnable without showing a window.
/// </summary>
public interface IBannerSlot
{
    /// <summary>Queues a banner at the BACK of the FIFO line; it shows now when the slot is free.
    /// The live occupant is refreshed instead of queued again.</summary>
    void Enqueue(IBannerSlotOccupant banner);

    /// <summary>Takes the slot AT ONCE. A live banner is taken down and put back at the FRONT of
    /// the queue, so it resumes when the pre-emptor closes rather than being lost.</summary>
    void Preempt(IBannerSlotOccupant banner);

    /// <summary>Takes a banner out of the slot without returning it to the queue: hidden if it is
    /// live, dropped if it is only waiting.</summary>
    void Withdraw(IBannerSlotOccupant banner);

    /// <summary>Whether this banner is the one currently on screen.</summary>
    bool IsLive(IBannerSlotOccupant banner);

    /// <summary>The live occupant's visible bounds in physical pixels; false when the slot is
    /// empty or the live banner is not on screen.</summary>
    bool TryGetVisibleBounds(out PhysicalRect bounds);

    /// <summary>The live bounds as a one-or-zero element list (the restart notice's keep-clear
    /// source, which concatenates physical rectangles).</summary>
    IReadOnlyList<PhysicalRect> LiveBoundsOrEmpty();
}
