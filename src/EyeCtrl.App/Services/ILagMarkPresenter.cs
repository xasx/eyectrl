namespace EyeCtrl.App.Services;

using System;
using System.Windows;
using EyeCtrl.Core.Models;

/// <summary>
/// The lag-mark button (T0(a)): the temporary, dwellable "Lag" overlay an instrumented session
/// shows in the work area's bottom-right, so the owner can mark a lag moment in the moment with
/// one forgiving dwell. Separate from <c>LagMarkHost</c> so the host's lifetime, hide-while-modals
/// and mark-logging decisions are headless-testable with a fake; the WPF <c>LagMarkWindow</c> is
/// the runtime implementation.
/// </summary>
public interface ILagMarkPresenter
{
    /// <summary>Whether the button is on screen (gaze is only fed to it while it is).</summary>
    bool IsShowing { get; }

    /// <summary>Shows the button at the work area's bottom-right, clear of chrome. Idempotent.</summary>
    void ShowButton();

    /// <summary>Takes the button down. No-op when it is not showing.</summary>
    void HideButton();

    /// <summary>Feeds one gaze sample to the button's dwell.</summary>
    /// <param name="gaze">The gaze point in physical pixels.</param>
    /// <param name="timestampMicroseconds">The sample time.</param>
    void OnGaze(PhysicalPoint gaze, long timestampMicroseconds);

    /// <summary>
    /// Raised when a dwell completes, with the timestamp of the sample that completed it — the
    /// owner's gaze moment, which the mark must not let the UI's own lag delay.
    /// </summary>
    event Action<long>? Marked;

    /// <summary>
    /// Raised when the button MOVED itself (the chrome it keeps clear of appeared, went or moved,
    /// review 01 F1). The host re-sends the button's zone so the activation-offer engine avoids the
    /// place the button is, not the place it was.
    /// </summary>
    event Action? Moved;

    /// <summary>The dwell hit rectangle in physical screen pixels (the visual grown by 48 DIP per side).</summary>
    PhysicalRect HitBounds { get; }

    /// <summary>The button's on-screen rectangle in DIP, as the shown line reports it.</summary>
    Rect ShownRectDip { get; }
}
