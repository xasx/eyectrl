namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// The D17 collision seam: whether the panel rect would cover another gaze-consuming surface.
/// Task 10 ships the always-clear stub so the service's show-time gate and its immediate
/// <c>NoPanel</c> publish (P11) exist end-to-end; Task 11 replaces the implementation with the real
/// gaze-consumer inventory (keyboard, edge-button bar, action menu, modes menu, external dwell) and
/// re-runs the check on every chrome change and panel resize.
/// </summary>
public interface IOverlayCollision
{
    /// <summary>Whether <paramref name="panelRect"/> is clear of every live gaze consumer.</summary>
    bool IsClear(PhysicalRect panelRect);
}

/// <summary>
/// Task 10's stand-in implementation: the panel is always clear. Never delete the registration —
/// Task 11 swaps the mapped implementation, and a missing registration would make the container
/// resolution fail on the first enable edge (silently, inside AdaptationActivation's catch).
/// </summary>
public sealed class AlwaysClearOverlayCollision : IOverlayCollision
{
    /// <inheritdoc />
    public bool IsClear(PhysicalRect panelRect) => true;
}
