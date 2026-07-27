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
    /// <summary>
    /// Minimum gap between badge flashes, matching <c>CueOverlayWindow</c>'s ~2 s fade so each pulse
    /// completes before the next begins. Without it the coordinator's 4 Hz assessment stream re-flashed
    /// eight times per fade: the badge never actually faded (contradicting the §10 "auto-fades over ~2 s"
    /// design), and every tick cost a topmost <c>SetWindowPos</c> plus a fresh WPF <c>DoubleAnimation</c> on
    /// the same UI thread that renders the gaze cursor and services dwell updates.
    /// </summary>
    internal const long ReflashIntervalMs = 2000;

    /// <summary>
    /// Intensity rise that re-flashes immediately rather than waiting out <see cref="ReflashIntervalMs"/>,
    /// so a struggle that is getting materially worse is still surfaced promptly.
    /// </summary>
    internal const double IntensityEscalationDelta = 0.15;

    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly ICuePresenter _presenter;
    private readonly Func<long> _nowMs;
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    // Flash-throttle state. _flashing also makes Hide idempotent: the hidden branch fires on every tick that
    // carries struggle while the cue is gated off or suppressed, and CueOverlayWindow.Hide is two dependency
    // -property writes — worth skipping when there is nothing on screen to take down.
    private bool _flashing;
    private long _lastFlashMs;
    private double _lastFlashIntensity;

    /// <summary>Initializes the cue service over its settings gate, suppression seam, and presenter.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.ShowCue"/> gates the cue.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); the no-op gate never suppresses.</param>
    /// <param name="presenter">The cue renderer (the WPF window at runtime, a fake in tests).</param>
    /// <param name="nowMs">Monotonic millisecond clock for the flash throttle; defaults to
    /// <see cref="Environment.TickCount64"/>. Injectable so the throttle is headless-testable without waiting.</param>
    public AdaptationCueService(AdaptationSettings adaptation, IFeedbackGate feedbackGate, ICuePresenter presenter,
        Func<long>? nowMs = null)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
        _presenter = presenter;
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
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
    /// flashes the badge when visible, subject to the <see cref="ReflashIntervalMs"/> throttle. Returns the
    /// state for test assertions — the returned decision is the unthrottled one, so a caller can tell what
    /// the cue state *is* regardless of whether this particular tick repainted.
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <returns>The presentation decision taken.</returns>
    public CueState OnAssessment(StruggleAssessment assessment)
    {
        CueState state = ComputeCueState(assessment, _adaptation.ShowCue, _feedbackGate.IsFeedbackSuppressed);
        if (!state.Visible)
        {
            // Immediately drop the badge when the decision flips to hidden — e.g. feedback suppression
            // turning on mid-struggle (§8.10) must not let the 2 s auto-fade keep flashing a visible cue in
            // a sensitive context. Only when something was actually flashed, so a persistently-gated-off cue
            // does not re-issue Hide on every tick.
            if (_flashing)
            {
                _flashing = false;
                _presenter.Hide();
            }
            return state;
        }

        if (ShouldFlash(state.Intensity))
        {
            _flashing = true;
            _lastFlashMs = _nowMs();
            _lastFlashIntensity = state.Intensity;
            _presenter.Show(state.Intensity);
        }
        return state;
    }

    // Flash on the leading edge of an episode, when the previous pulse has had time to fade, or when
    // intensity has escalated materially since the last flash.
    private bool ShouldFlash(double intensity) =>
        !_flashing
        || intensity >= _lastFlashIntensity + IntensityEscalationDelta
        || _nowMs() - _lastFlashMs >= ReflashIntervalMs;

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
