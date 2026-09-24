namespace EyeCtrl.App.Services;

/// <summary>
/// The toolbar-visibility seam used by <see cref="ToolbarAutoHideController"/>.
/// Abstracts <c>OverlayManager.Toolbar</c> + <c>PauseIndicator</c> so the controller's
/// state machine is testable without WPF windows or a dispatcher.
/// </summary>
/// <remarks>
/// All methods must marshal onto the UI thread themselves (the controller calls them
/// from the gaze thread). Implementations: a real adapter over OverlayManager, and a
/// recorded-call fake in tests.
/// </remarks>
public interface IToolbarVisibility
{
    /// <summary>Shows the toolbar (animated slide-in). Raises the pause indicator back above the
    /// band iff it is commanded visible (see <see cref="SetPauseIndicator"/>).</summary>
    void ShowToolbar();

    /// <summary>Hides the toolbar. The pause indicator is NOT touched — its visibility is
    /// commanded only through <see cref="SetPauseIndicator"/>.</summary>
    /// <param name="paused">Whether the app is currently paused. Log-only here: no hide
    /// behaviour branches on it (the strip is commanded solely through
    /// <see cref="SetPauseIndicator"/>; issue #166 nit — the old wording implied otherwise).</param>
    void HideToolbar(bool paused);

    /// <summary>Force-shows the toolbar instantly (tray fallback / suspend-recover). Raises the
    /// pause indicator back above the band iff it is commanded visible.</summary>
    void ForceShow();

    /// <summary>
    /// Commands the pause edge strip's visibility. The strip's state lives HERE (edge-guarded),
    /// not in band show/hide paths: every pause flip produces exactly one command (spec H1 —
    /// unpause with the band up must dismiss it; nothing else will).
    /// </summary>
    void SetPauseIndicator(bool visible);

    /// <summary>Repositions the pause indicator along the given docked edge ("Top"/"Bottom"/"Left"/"Right").</summary>
    void PositionIndicator(string edge);

    /// <summary>
    /// Activates or deactivates toolbar dwell processing. Hide/ForceShow/calibration call this.
    /// When deactivated, in-progress dwell state is reset (DwellDetector.ResetAllState) so the
    /// next activation cannot fire from a stale timestamp.
    /// </summary>
    void SetActive(bool active);
}
