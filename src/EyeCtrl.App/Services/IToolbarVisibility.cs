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
    /// <summary>Shows the toolbar (and hides the pause indicator if it was up).</summary>
    void ShowToolbar();

    /// <summary>Hides the toolbar. Shows the pause indicator iff <paramref name="paused"/> is true.</summary>
    /// <param name="paused">Whether the app is currently paused (controls the edge bar).</param>
    void HideToolbar(bool paused);

    /// <summary>Force-shows the toolbar and hides the pause indicator (tray fallback / suspend-recover).</summary>
    void ForceShow();

    /// <summary>Repositions the pause indicator along the given docked edge ("Top"/"Bottom"/"Left"/"Right").</summary>
    void PositionIndicator(string edge);

    /// <summary>
    /// Activates or deactivates toolbar dwell processing. Hide/ForceShow/calibration call this.
    /// When deactivated, in-progress dwell state is reset (DwellDetector.ResetAllState) so the
    /// next activation cannot fire from a stale timestamp.
    /// </summary>
    void SetActive(bool active);
}
