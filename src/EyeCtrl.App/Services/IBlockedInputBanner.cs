namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Input;
using EyeCtrl.Core.Models;

/// <summary>
/// The coordinator's view of C10's banner: show it for a blocked state, hide it, feed it gaze, and
/// learn that the owner asked for the way out or a dismissal. The production implementation is
/// <see cref="Overlays.BlockedInputBannerWindow"/>; the interface exists so the routing (show vs
/// dismiss per episode, ring state) is pinnable without standing up a window.
/// </summary>
internal interface IBlockedInputBanner
{
    /// <summary>Shows the banner for <paramref name="state"/> (a repeated call refreshes it).</summary>
    void ShowBlocked(BlockedInputState state);

    /// <summary>Hides the banner; safe when it is already hidden.</summary>
    void HideBlocked();

    /// <summary>Feeds one live gaze sample to the banner's dwell detector.</summary>
    /// <param name="gaze">The gaze point in physical pixels.</param>
    /// <param name="timestampMicroseconds">The sample's timestamp.</param>
    void OnGaze(PhysicalPoint gaze, long timestampMicroseconds);

    /// <summary>Raised when the owner dwells the "to desktop / previous window" button.</summary>
    event EventHandler? EscapeRequested;

    /// <summary>Raised when the owner dwells the dismiss button.</summary>
    event EventHandler? DismissRequested;

    /// <summary>
    /// Raised AFTER the banner actually left the screen: the coordinator's clear, a dismiss dwell,
    /// or Dispose — never for a hide that was a no-op (it was not showing). C10 fixup-04's hide
    /// latch: the note gate latches the targets under the held gaze on this event, because the
    /// banner's emergency buttons sat over a possible note control.
    /// </summary>
    event EventHandler? Hidden;
}
