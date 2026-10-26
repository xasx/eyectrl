namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Adaptation;
using Serilog;

/// <summary>
/// Surfaces the drift "report" (design §7.1): when the monitor gates the <see cref="FailureMode.Drift"/>
/// mode (a cross-context coherent near-miss bias, §13.1), shows a report-only notice suggesting the user
/// consider recalibrating in Tobii — **calibration is never auto-touched** (§8.5). Rate-limited to once per
/// drift-gated episode (re-armed when Drift clears) and suppressed in sensitive contexts (§8.10). Subscribes
/// to <see cref="AdaptationCoordinator"/>'s per-tick <c>AssessmentUpdated</c> event; the decision
/// (<see cref="OnAssessment"/>) is pure and headless-testable via its <c>bool</c> return.
/// </summary>
public sealed class DriftNoticeService : IDisposable
{
    private readonly IFeedbackGate _feedbackGate;
    private readonly IDriftNoticePresenter _presenter;
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose
    private bool _driftEpisodeActive;
    // True from Dispose until the next Start. A queued notice may outlive the runtime (B4 fixup-01
    // H1): the slot can reach it after adaptation was switched off, and it must not show then.
    private bool _runtimeStopped;

    /// <summary>Initializes the service over the suppression seam and the notice presenter.</summary>
    /// <param name="feedbackGate">The suppression seam (§8.10); when suppressing, no visible notice is shown.</param>
    /// <param name="presenter">The notice renderer (the WPF banner at runtime, a fake in tests).</param>
    public DriftNoticeService(IFeedbackGate feedbackGate, IDriftNoticePresenter presenter)
    {
        _feedbackGate = feedbackGate;
        _presenter = presenter;
    }

    /// <summary>
    /// Whether a notice the slot is holding may still be shown when the slot reaches it (B4
    /// fixup-01 H1). The slot can hold a notice for an unbounded time (a caregiver alert stays up
    /// as long as its condition is sustained), so the once-per-episode decision taken at the onset
    /// is re-validated: the runtime is still running, feedback is not suppressed, and the drift
    /// episode that raised the notice is still current. False drops the queued notice — never pop
    /// it late in a sensitive context, after the disable, or minutes after the drift cleared.
    /// </summary>
    internal bool CanShowNotice =>
        !_runtimeStopped && _driftEpisodeActive && !_feedbackGate.IsFeedbackSuppressed;

    /// <summary>
    /// Shows the drift notice on the leading edge of a drift-gated episode (once per episode), unless
    /// feedback is suppressed. Re-arms when Drift is no longer gated.
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <returns>True if a notice was shown this tick.</returns>
    public bool OnAssessment(StruggleAssessment assessment)
    {
        bool driftGated = assessment.ForMode(FailureMode.Drift) is { GatePassed: true };

        if (!driftGated)
        {
            _driftEpisodeActive = false; // re-arm for the next drift episode
            return false;
        }

        if (_driftEpisodeActive)
            return false; // already handled this episode

        // Mark the episode handled regardless of suppression, so a later suppression change can't pop
        // the notice late. Consequence: if feedback is suppressed at the drift-gate ONSET, this episode
        // shows NO notice at all — even if suppression lifts mid-episode — which is the §8.10-faithful
        // trade (never leak feedback in a sensitive context; adaptation still runs silently). A fresh
        // drift episode (after Drift clears and re-gates) re-arms and can show again.
        _driftEpisodeActive = true;
        if (_feedbackGate.IsFeedbackSuppressed)
            return false;

        _presenter.ShowDriftNotice();
        Log.Information("Adaptation: drift detected — suggested re-calibration (calibration not modified, §8.5)");
        return true;
    }

    /// <summary>Subscribes to the coordinator's per-tick assessment stream. Call once on the UI thread; idempotent.</summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> event drives the notice.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        _runtimeStopped = false;
        if (_coordinator is not null)
            return;
        _coordinator = coordinator;
        coordinator.AssessmentUpdated += OnAssessmentEvent;
    }

    private void OnAssessmentEvent(StruggleAssessment assessment) => OnAssessment(assessment);

    /// <summary>
    /// Unsubscribes from the coordinator's assessment stream and takes this service's notice out of
    /// the banner slot (B4 fixup-01 H1): a notice that was waiting behind a caregiver alert must not
    /// appear after adaptation was switched off — including during App exit, which deactivates the
    /// runtime too. The episode state resets, so a later re-enable starts a fresh episode.
    /// </summary>
    public void Dispose()
    {
        if (_coordinator is not null)
        {
            _coordinator.AssessmentUpdated -= OnAssessmentEvent;
            _coordinator = null;
        }
        _runtimeStopped = true;
        _driftEpisodeActive = false;
        try
        {
            _presenter.WithdrawDriftNotice();
        }
        catch (Exception ex)
        {
            // Teardown touches a WPF window (the live notice). A throw must not abort the rest of
            // the disable path — the caregiver service's Dispose and the UIA hook stop follow.
            Log.Warning(ex, "Failed to withdraw the drift notice from the banner slot during teardown.");
        }
    }
}
