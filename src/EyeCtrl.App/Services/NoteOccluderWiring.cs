namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.ViewModels;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The note gate's occluder wiring (P2 D3, T2 fixup 02): ONE place builds the production
/// delegates, so the exact production lambdas are executable from a real-container test (review
/// 02 B1 — the first version inlined them in App.xaml.cs and resolved
/// <c>ITrackStatusOverlay</c>, which the container does not register; every gaze sample threw
/// while a note existed, and no headless test could see it).
/// <para>
/// Sources and their owners: the lag mark through its late-attached bridge (empty until the
/// instrumented host attaches it), the bottom-centre banner slot through its live occupant (B4: ONE
/// aggregate source — the drift notice and the caregiver alert are shown only through the slot,
/// and future status cards will join the same way), the track-status card through
/// <see cref="EventCoordinationContext.OverlayManager"/>'s <c>TrackStatus</c> — the window is
/// constructed by the overlay manager, not registered in DI — the status strip's label card
/// (B2b), and C10's blocked-input banner (fixup-04) through its own visible bounds, plus the
/// banner's hide latch.
/// </para>
/// <para>
/// A throwing source is survived by the note gate itself (<see cref="NoteManager"/>'s occluder
/// read fails closed: the sample counts as occluded) and logs once per source until it recovers.
/// </para>
/// </summary>
internal static class NoteOccluderWiring
{
    /// <summary>Wires every occluder source onto <paramref name="noteManager"/> and attaches the
    /// blocked banner's hide latch. Called once from App startup, after the coordination context
    /// exists; every delegate reads late, so attach order does not matter.</summary>
    internal static void Wire(INoteManager noteManager, IServiceProvider services, EventCoordinationContext context)
    {
        noteManager.SetOccluderSources(
            // T5 review R2 + external review I4: the browser's WHOLE window bounds while the
            // window is up — an opaque browser occludes notes across its whole face, not just
            // where a dwell target sits (a note outside the links would fire a click onto the
            // page). The dwell rects stay with the D17 inventory (GazeConsumerInventory),
            // whose collision check wants the finer set.
            browseRects: () => services.GetRequiredService<BrowseViewModel>().WindowOccluderRects,
            lagMarkRects: () => services.GetRequiredService<LagMarkBridge>().LiveTargetRects(),
            // B4: the slot's live occupant, or null when the slot is empty. ONE source for every
            // bottom-centre banner (the drift notice and the caregiver alert replaced the two
            // per-banner delegates this wiring used to pass).
            slotOccupantBounds: () =>
                services.GetRequiredService<BannerSlot>().TryGetVisibleBounds(out var slotOccupant)
                    ? slotOccupant : null,
            trackStatusBounds: () =>
                context.OverlayManager.TrackStatus.TryGetVisibleBounds(out var track) ? track : null,
            // P4 D4: the capacity notice is manager-owned (never in DI); the wiring reads its
            // visible bounds through the manager seam so the notice gates notes exactly like
            // every other occluder.
            noteNoticeBounds: () =>
                ((NoteManager)noteManager).TryGetNoticeVisibleBounds(out var notice) ? notice : null,
            // B2b: the status strip's label card (paused / tracking lost / no tracker) whenever the
            // window is up. Like the track-status card, the window belongs to the overlay manager.
            statusStripBounds: () =>
                context.OverlayManager.PauseIndicator.TryGetVisibleBounds(out var strip) ? strip : null,
            // C10 fixup-04: the whole visible blocked-input banner while it is up. A note fires from
            // EyeCtrl's OWN gaze dwell (600 ms), which keeps running while UIPI discards the injected
            // clicks the banner reports - so the click-through strip does NOT protect a note's ✕
            // beneath its 700 ms emergency buttons; it must occlude instead.
            blockedBannerBounds: () =>
                services.GetRequiredService<BlockedInputBannerWindow>()
                    .TryGetBlockedVisibleBounds(out var blockedBanner) ? blockedBanner : null);

        // C10 fixup-04's hide latch: the banner hides on the first verified move, while the gaze
        // usually still rests where its buttons were. Latch every note target under that spot, so a
        // held gaze needs its 150 ms leave before a note control at the former banner position arms.
        var blockedBanner = services.GetRequiredService<IBlockedInputBanner>();
        blockedBanner.Hidden += (_, _) => noteManager.LatchTargetsUnderTheGaze();
    }
}
