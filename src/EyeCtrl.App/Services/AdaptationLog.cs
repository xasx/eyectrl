namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// The struggle-adaptation session log (design §10). Subscribes to <see cref="AdaptationCoordinator"/>'s
/// per-tick <c>AssessmentUpdated</c> event and writes a structured Serilog entry whenever the set of
/// <em>gated</em> failure modes changes — the coarsest adaptation-action boundary available (per-knob
/// deltas aren't observable: the provider's <c>Changed</c> event is payloadless and the controller exposes
/// no change-feed; finer detail + the JSONL file/review are Phase 2). Logging on transitions (not per tick)
/// keeps I/O off the hot path (§9.1). In a sensitive context the entry is replaced by a coarse
/// <c>"adaptation suppressed (sensitive context)"</c> marker (§8.10/§10). The decision
/// (<see cref="OnAssessment"/>) is pure and headless-testable via its <see cref="AdaptationLogAction"/>
/// return; only <see cref="Start"/>'s subscription is runtime. No explicit <c>Enabled</c> check is needed:
/// while adaptation is disabled the coordinator reports <see cref="StruggleAssessment.None"/> (no gated
/// modes ⇒ no transitions ⇒ silent).
/// </summary>
public sealed class AdaptationLog : IDisposable
{
    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly HashSet<FailureMode> _loggedGatedModes = new();
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    /// <summary>Initializes the log over its posture source and the suppression seam.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Posture"/> is recorded in each entry.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); when suppressing, only the coarse marker is logged.</param>
    public AdaptationLog(AdaptationSettings adaptation, IFeedbackGate feedbackGate)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
    }

    /// <summary>The set of failure modes whose confidence gate is met in the given assessment.</summary>
    /// <param name="assessment">The assessment to inspect.</param>
    /// <returns>The gated modes (empty when there is no gated struggle).</returns>
    public static IReadOnlySet<FailureMode> GatedModes(StruggleAssessment assessment)
    {
        var set = new HashSet<FailureMode>();
        foreach (var mode in assessment.Modes)
            if (mode.GatePassed)
                set.Add(mode.Mode);
        return set;
    }

    /// <summary>
    /// Logs a transition entry if the gated-mode set changed since the last assessment; otherwise no-op.
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <returns>Which logging action was taken.</returns>
    public AdaptationLogAction OnAssessment(StruggleAssessment assessment)
    {
        IReadOnlySet<FailureMode> current = GatedModes(assessment);
        if (_loggedGatedModes.SetEquals(current))
            return AdaptationLogAction.None;

        _loggedGatedModes.Clear();
        _loggedGatedModes.UnionWith(current);

        if (_feedbackGate.IsFeedbackSuppressed)
        {
            Log.Information("Adaptation suppressed (sensitive context)");
            return AdaptationLogAction.SuppressedMarker;
        }

        Log.Information(
            "Adaptation: struggle {AggregateLevel:F2}, gated modes [{Modes}], posture {Posture}",
            assessment.AggregateLevel,
            string.Join(", ", current.OrderBy(m => m)),
            _adaptation.Posture);
        return AdaptationLogAction.Logged;
    }

    /// <summary>Subscribes to the coordinator's per-tick assessment stream. Call once on the UI thread; idempotent.</summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> event drives the log.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return;
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
