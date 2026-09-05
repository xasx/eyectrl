namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// Drives the caregiver alert (design §10, Phase 3): a prominent app-owned banner plus an attention
/// chime, shown while high struggle or poor tracking stays <b>sustained</b>. Subscribes to
/// <see cref="AdaptationCoordinator"/>'s per-tick <c>AssessmentUpdated</c> event and consults the same
/// <see cref="IFeedbackGate"/> as the cue/log/drift (§8.10). The decision
/// (<see cref="OnAssessment"/>/<see cref="Track"/>/<see cref="EvaluateOnset"/>) is pure and
/// headless-testable over an injected clock; only <see cref="Start"/>'s event subscription is runtime.
/// </summary>
public sealed class CaregiverAlertService : IDisposable
{
    /// <summary>Aggregate struggle level that sustains the struggle condition. Fixed and
    /// posture-independent: a caregiver channel must not get more sensitive because adaptation is eager (D4).</summary>
    internal const double StruggleLevelThreshold = 0.5;

    /// <summary>How long a condition must be continuously met before an alert onset is due (§10 "sustained").</summary>
    internal const long SustainMs = 60_000;

    /// <summary>How long a condition must stay clear before it may alert again (the re-arm gap, D5).</summary>
    internal const long ClearGapMs = 30_000;

    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly ICaregiverAlertPresenter _presenter;
    private readonly Action<CaregiverAlertCondition>? _logAlert;
    private readonly Func<long> _nowMs;
    private readonly ConditionState _struggle = new();
    private readonly ConditionState _poorTracking = new();
    private CaregiverAlertCondition? _showing; // the condition whose window is currently up
    private bool _hasAlerted; // rate-limit clock flag — TickCount64 has no overflow-safe "never" sentinel
    private long _lastAlertMs;
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    /// <summary>Per-condition sustain/episode state (dispatcher thread only, like the cue's throttle state).</summary>
    private sealed class ConditionState
    {
        public long SustainedSince = long.MinValue; // MinValue = not currently sustained
        public long LastClearedAt = long.MinValue;  // MinValue = never cleared (first episode)
        public bool AlertedThisEpisode;
        public bool SuppressedOnsetConsumed;
    }

    /// <summary>Initializes the service over its settings gate, suppression seam, presenter, and optional log sink.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.CaregiverAlerts"/> gates onsets and
    /// <see cref="AdaptationSettings.CaregiverMinIntervalMinutes"/> is the rate limit.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); the no-op gate never suppresses.</param>
    /// <param name="presenter">The alert renderer (the WPF window at runtime, a fake in tests).</param>
    /// <param name="logAlert">Invoked once per unsuppressed alert onset — the JSONL hook (D8).</param>
    /// <param name="nowMs">Monotonic millisecond clock; injectable so sustain/rate-limit timing is headless-testable.</param>
    public CaregiverAlertService(AdaptationSettings adaptation, IFeedbackGate feedbackGate,
        ICaregiverAlertPresenter presenter, Action<CaregiverAlertCondition>? logAlert = null,
        Func<long>? nowMs = null)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
        _presenter = presenter;
        _logAlert = logAlert;
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
    }

    /// <summary>
    /// Handles one assessment: tracks both conditions' sustain state, takes the window down on
    /// suppression/feature-off (immediately) and on episode end (fade), and fires at most one alert
    /// onset per episode, subject to the global rate limit.
    /// </summary>
    /// <param name="assessment">The current struggle assessment (None on non-assessing ticks).</param>
    /// <returns>True iff an alert onset fired this tick.</returns>
    public bool OnAssessment(StruggleAssessment assessment)
    {
        long now = _nowMs();
        bool none = assessment.Modes.Count == 0 && assessment.AggregateLevel <= 0.0;

        // Hard visibility gates first: take the window down AT ONCE (never fade) when feedback is
        // suppressed (§8.10) or the feature was switched off mid-display. The window cannot reappear
        // for this episode — its onset is already marked alerted — so there is no suppression-lift pop.
        if (_showing is { } && (!_adaptation.CaregiverAlerts || _feedbackGate.IsFeedbackSuppressed))
        {
            _showing = null;
            _presenter.HideCaregiverAlert();
        }

        Track(_struggle, met: !none && assessment.AggregateLevel >= StruggleLevelThreshold, now);
        Track(_poorTracking, met: !none && assessment.ForMode(FailureMode.PoorTracking) is { GatePassed: true }, now);

        // The window fades (~1 s) when the showing condition's episode ends — including the None
        // assessments the coordinator sends while paused/disconnected/disabled.
        if (_showing is { } showing && !IsSustained(showing))
        {
            _showing = null;
            _presenter.FadeOutCaregiverAlert();
        }

        // Poor tracking is evaluated first: when both conditions are due it is the more specific,
        // more actionable cause (and the one that fires with zero interactions, §6.3). With the
        // short-circuit, the struggle onset is not even evaluated on a tick where poor tracking
        // fired; on subsequent ticks the global rate-limit clock blocks it (round-1 F7).
        return EvaluateOnset(_poorTracking, CaregiverAlertCondition.SustainedPoorTracking, now)
            || EvaluateOnset(_struggle, CaregiverAlertCondition.SustainedStruggle, now);
    }

    // Updates one condition's sustain/episode state. met=false ends the episode; met=true starts (or
    // continues) one. An episode starting < ClearGapMs after the previous one cleared is the SAME
    // flare-up and inherits its alerted/consumed state, so a flapping condition cannot re-alert by
    // clearing for a moment (D5).
    private void Track(ConditionState state, bool met, long now)
    {
        if (met)
        {
            if (state.SustainedSince == long.MinValue)
            {
                bool sameFlareUp = state.LastClearedAt != long.MinValue
                    && now - state.LastClearedAt < ClearGapMs;
                if (!sameFlareUp)
                {
                    state.AlertedThisEpisode = false;
                    state.SuppressedOnsetConsumed = false;
                }
                state.SustainedSince = now;
            }
        }
        else if (state.SustainedSince != long.MinValue)
        {
            state.SustainedSince = long.MinValue;
            state.LastClearedAt = now;
        }
    }

    // Fires this condition's alert onset if it is due: sustained >= SustainMs, this episode not yet
    // alerted/consumed, the feature on, feedback not suppressed, and the global interval elapsed.
    // A rate-limit block DELAYS the onset (retried every tick while sustained) rather than consuming
    // it — a safety channel must eventually surface a long episode (D5). A suppressed onset is
    // CONSUMED (§8.10, the DriftNoticeService trade) and must not start the rate-limit clock, or a
    // password-adjacent struggle would silently lock the channel for the whole interval (D6).
    private bool EvaluateOnset(ConditionState state, CaregiverAlertCondition condition, long now)
    {
        if (state.SustainedSince == long.MinValue
            || state.AlertedThisEpisode
            || state.SuppressedOnsetConsumed
            || now - state.SustainedSince < SustainMs
            || !_adaptation.CaregiverAlerts)
        {
            return false;
        }

        if (_feedbackGate.IsFeedbackSuppressed)
        {
            state.SuppressedOnsetConsumed = true;
            return false;
        }

        if (_hasAlerted && now - _lastAlertMs < (long)(_adaptation.CaregiverMinIntervalMinutes * 60_000.0))
            return false;

        state.AlertedThisEpisode = true;
        _hasAlerted = true;
        _lastAlertMs = now;
        _showing = condition;
        _presenter.ShowCaregiverAlert(condition);
        _logAlert?.Invoke(condition);
        Log.Information("Adaptation: caregiver alert shown ({Condition})", condition);
        return true;
    }

    private bool IsSustained(CaregiverAlertCondition condition) =>
        StateFor(condition).SustainedSince != long.MinValue;

    private ConditionState StateFor(CaregiverAlertCondition condition) =>
        condition == CaregiverAlertCondition.SustainedPoorTracking ? _poorTracking : _struggle;

    /// <summary>Subscribes to the coordinator's per-tick assessment stream. Call once on the UI thread; idempotent.</summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> event drives the alert.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return;
        _coordinator = coordinator;
        coordinator.AssessmentUpdated += OnAssessmentEvent;
    }

    private void OnAssessmentEvent(StruggleAssessment assessment) => OnAssessment(assessment);

    /// <summary>
    /// Unsubscribes, takes the window down without fading, and resets both conditions' sustain/
    /// episode state. The disable edge never delivers a None — StopRuntime's ResetMomentary sets
    /// LastAssessment without raising, and the timer is disposed synchronously — so this is the
    /// only place pre-disable sustain can die; without the reset, a stale sustain fires instantly
    /// on the first re-enabled tick (round-1 F2). The rate-limit clock deliberately SURVIVES — the
    /// singleton outlives enable/disable toggles, and toggling adaptation off and on must not
    /// bypass the interval (D11).
    /// </summary>
    public void Dispose()
    {
        if (_coordinator is not null)
        {
            _coordinator.AssessmentUpdated -= OnAssessmentEvent;
            _coordinator = null;
        }
        if (_showing is { })
        {
            _showing = null;
            _presenter.HideCaregiverAlert();
        }
        Reset(_struggle);
        Reset(_poorTracking);
    }

    private static void Reset(ConditionState state)
    {
        state.SustainedSince = long.MinValue;
        state.LastClearedAt = long.MinValue;
        state.AlertedThisEpisode = false;
        state.SuppressedOnsetConsumed = false;
    }
}
