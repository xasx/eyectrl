namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The note gate's occluder wiring (P2 D3, T2 fixup 02): ONE place builds the four production
/// delegates, so the exact production lambdas are executable from a real-container test (review
/// 02 B1 — the first version inlined them in App.xaml.cs and resolved
/// <c>ITrackStatusOverlay</c>, which the container does not register; every gaze sample threw
/// while a note existed, and no headless test could see it).
/// <para>
/// Sources and their owners: the lag mark through its late-attached bridge (empty until the
/// instrumented host attaches it), the drift notice and caregiver alert through their DI
/// presenters, and the track-status card through <see cref="EventCoordinationContext.OverlayManager"/>'s
/// <c>TrackStatus</c> — the window is constructed by the overlay manager, not registered in DI.
/// </para>
/// <para>
/// A throwing source is survived by the gate itself: <see cref="NoteManager.OccludersContain"/>
/// fails closed (the sample counts as occluded) and logs once per source until it recovers.
/// </para>
/// </summary>
internal static class NoteOccluderWiring
{
    /// <summary>Wires the four occluder sources onto <paramref name="noteManager"/>. Called once
    /// from App startup, after the coordination context exists; every delegate reads late, so
    /// attach order does not matter.</summary>
    internal static void Wire(INoteManager noteManager, IServiceProvider services, EventCoordinationContext context)
    {
        noteManager.SetOccluderSources(
            lagMarkRects: () => services.GetRequiredService<LagMarkBridge>().LiveTargetRects(),
            driftNoticeBounds: () =>
                services.GetRequiredService<IDriftNoticePresenter>().TryGetVisibleBounds(out var drift)
                    ? drift : null,
            caregiverAlertBounds: () =>
                services.GetRequiredService<ICaregiverAlertPresenter>().TryGetVisibleBounds(out var alert)
                    ? alert : null,
            trackStatusBounds: () =>
                context.OverlayManager.TrackStatus.TryGetVisibleBounds(out var track) ? track : null);
    }
}
