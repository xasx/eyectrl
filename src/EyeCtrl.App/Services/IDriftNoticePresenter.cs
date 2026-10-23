namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// Renders the report-only drift notice. Separate from <see cref="DriftNoticeService"/> so the service's
/// once-per-episode/gating decision is headless-testable with a fake; the WPF <c>DriftNoticeWindow</c> is
/// the runtime implementation. The notice is informational only — it never modifies calibration (§8.5).
/// </summary>
public interface IDriftNoticePresenter
{
    /// <summary>Shows the auto-fading "Tracking may have drifted — consider recalibrating in Tobii" banner.</summary>
    void ShowDriftNotice();

    /// <summary>
    /// The banner's visible bounds in physical pixels while it is on screen (P2 D3): the note
    /// gate's occluder source. False (and <paramref name="bounds"/> default) when it is not
    /// showing — the fade-out tail keeps reporting true until the window hides, which is the
    /// conservative reading.
    /// </summary>
    bool TryGetVisibleBounds(out PhysicalRect bounds);
}
