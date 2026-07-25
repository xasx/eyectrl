namespace EyeCtrl.App.Services;

/// <summary>
/// Renders the report-only drift notice. Separate from <see cref="DriftNoticeService"/> so the service's
/// once-per-episode/gating decision is headless-testable with a fake; the WPF <c>DriftNoticeWindow</c> is
/// the runtime implementation. The notice is informational only — it never modifies calibration (§8.5).
/// </summary>
public interface IDriftNoticePresenter
{
    /// <summary>Shows the auto-fading "tracking may have drifted — consider re-calibrating" banner.</summary>
    void ShowDriftNotice();
}
