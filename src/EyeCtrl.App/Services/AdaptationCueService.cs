namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

/// <summary>
/// Drives the on-screen adaptation cue (design §10). Subscribes to <see cref="AdaptationCoordinator"/>'s
/// per-tick <c>AssessmentUpdated</c> event and, when the current assessment shows gated struggle, flashes
/// a subtle corner badge whose intensity scales with the aggregate level. The badge is suppressed when
/// <see cref="AdaptationSettings.ShowCue"/> is off or the <see cref="IFeedbackGate"/> is suppressing
/// (sensitive contexts, §8.10). No explicit <c>Enabled</c> check is needed: when adaptation is disabled
/// the coordinator reports <see cref="StruggleAssessment.None"/> (aggregate 0 ⇒ hidden). The decision
/// logic (<see cref="ComputeCueState"/>/<see cref="OnAssessment"/>) is pure and headless-testable; only
/// <see cref="Start"/>'s event subscription is runtime.
/// </summary>
public sealed class AdaptationCueService : IDisposable
{
    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly ICuePresenter _presenter;
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    /// <summary>Initializes the cue service over its settings gate, suppression seam, and presenter.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.ShowCue"/> gates the cue.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); the no-op gate never suppresses.</param>
    /// <param name="presenter">The cue renderer (the WPF window at runtime, a fake in tests).</param>
    public AdaptationCueService(AdaptationSettings adaptation, IFeedbackGate feedbackGate, ICuePresenter presenter)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
        _presenter = presenter;
    }

    /// <summary>
    /// Pure cue decision: visible (with intensity = the aggregate level) only when the cue is enabled,
    /// not suppressed, and the assessment shows gated struggle (<c>AggregateLevel &gt; 0</c>).
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <param name="showCue">Whether the cue is enabled (<see cref="AdaptationSettings.ShowCue"/>).</param>
    /// <param name="feedbackSuppressed">Whether feedback is suppressed (<see cref="IFeedbackGate.IsFeedbackSuppressed"/>).</param>
    /// <returns>The presentation decision.</returns>
    public static CueState ComputeCueState(StruggleAssessment assessment, bool showCue, bool feedbackSuppressed)
    {
        if (!showCue || feedbackSuppressed)
            return new CueState(false, 0.0);

        double level = assessment.AggregateLevel;
        if (level <= 0.0)
            return new CueState(false, 0.0);

        return new CueState(true, Math.Clamp(level, 0.0, 1.0));
    }

    /// <summary>
    /// Handles one assessment: computes the <see cref="CueState"/> against the live settings/gate and
    /// flashes the badge when visible. Returns the state for test assertions.
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <returns>The presentation decision taken.</returns>
    public CueState OnAssessment(StruggleAssessment assessment)
    {
        CueState state = ComputeCueState(assessment, _adaptation.ShowCue, _feedbackGate.IsFeedbackSuppressed);
        if (state.Visible)
            _presenter.Show(state.Intensity);
        else
            _presenter.Hide(); // immediately drop the badge when the decision flips to hidden — e.g. feedback
                               // suppression turning on mid-struggle (§8.10) must not let the 2 s auto-fade
                               // keep flashing a visible cue in a sensitive context.
        return state;
    }

    /// <summary>
    /// Subscribes to the coordinator's per-tick assessment stream. Call once on the UI thread; idempotent
    /// (a second call is a no-op, so it never double-subscribes).
    /// </summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> event drives the cue.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return; // already started
        _coordinator = coordinator;
        coordinator.AssessmentUpdated += OnAssessmentEvent;
    }

    private void OnAssessmentEvent(StruggleAssessment assessment) => OnAssessment(assessment);

    /// <summary>Unsubscribes from the coordinator's assessment stream.</summary>
    public void Dispose()
    {
        if (_coordinator is not null)
        {
            _coordinator.AssessmentUpdated -= OnAssessmentEvent;
            _coordinator = null;
        }
    }
}
