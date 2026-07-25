namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// The momentary struggle-adaptation controller (design §7.1/§7.2). Consumes a
/// <see cref="StruggleAssessment"/> per tick and pushes bounded, reversible per-knob band-fill fractions
/// into the <see cref="EffectiveSettingsProvider"/>. Stateful: holds each knob's current forgiving and
/// tightening fill plus a relax-sustain timer per direction, advanced by the tick timestamp. Forgiving-only
/// except the single quarantined dwell tightening (longer dwell), which only the accidental-activation mode
/// drives, lives in its own sub-band, and is instantly escapable via <see cref="Reset"/> (§8.2). Ticked by
/// the AdaptationCoordinator (plan 1b-⑤) — not self-ticking. Single-threaded (dispatcher).
/// </summary>
public sealed class AdaptationController
{
    private readonly EffectiveSettingsProvider _provider;
    private readonly AdaptationSettings _adaptation;

    // Per-knob momentary state. Fills are the [0, 1] band position; RelaxSince records when the relax
    // condition began (long.MinValue = not currently sustaining a relax), for the SustainSeconds hold.
    private sealed class KnobState
    {
        public double ForgivingFill;
        public double TighteningFill;
        public long ForgivingRelaxSince = long.MinValue;
        public long TighteningRelaxSince = long.MinValue;
    }

    private readonly Dictionary<AdaptationKnob, KnobState> _state = new()
    {
        [AdaptationKnob.FixationRadius] = new(),
        [AdaptationKnob.DwellTime] = new(),
        [AdaptationKnob.Smoothing] = new(),
        [AdaptationKnob.TargetGeometry] = new(),
    };

    /// <summary>Initializes the controller over the effective-settings provider and adaptation settings.</summary>
    /// <param name="provider">The provider the controller pushes momentary fills into.</param>
    /// <param name="adaptation">Adaptation settings: master switch, posture (→ <see cref="PosturePresets"/>), and per-knob enables — all read live per tick.</param>
    public AdaptationController(EffectiveSettingsProvider provider, AdaptationSettings adaptation)
    {
        _provider = provider;
        _adaptation = adaptation;
    }

    /// <summary>
    /// Advances the controller one tick: for each enabled knob, computes its drive from the gated feeding
    /// modes, steps its fill (relax/recover/deadband per the posture tuning and
    /// <paramref name="nowMicroseconds"/>), and pushes the new fills into the provider when they change.
    /// Clears all momentary state and returns immediately when adaptation is disabled (§12: disabled ⇒ no delta).
    /// </summary>
    /// <param name="assessment">The current struggle assessment (per-mode gated scores).</param>
    /// <param name="nowMicroseconds">The current tick timestamp, in microseconds (advances the sustain timers).</param>
    public void Tick(StruggleAssessment assessment, long nowMicroseconds)
    {
        if (!_adaptation.Enabled)
        {
            Reset();
            return;
        }

        // §11 defensive degradation: an unexpected exception during a tick (e.g. a future unhandled
        // AdaptationPosture in PosturePresets.For) must NOT crash the dispatcher tick loop or leave partial
        // knob state. Degrade to baseline (clear all momentary state) and let the next tick retry from clean.
        // This honours the §11 catch the PosturePresets.For <exception> doc claims the controller applies.
        try
        {
            PostureTuning tuning = PosturePresets.For(_adaptation.Posture);

            // Tolerance: loosen (forgiving = larger) on sustain-failure OR drift (union of forgiving responses, §8.2).
            UpdateKnob(AdaptationKnob.FixationRadius, _adaptation.AdaptTolerance, tuning, nowMicroseconds,
                forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure, FailureMode.Drift),
                tighteningDrive: 0.0);
            // Dwell: shorten (forgiving) on sustain-failure; the ONE quarantined tightening (longer dwell) on
            // accidental-activation ONLY, in its own sub-band, instantly escapable via Reset (§8.2).
            UpdateKnob(AdaptationKnob.DwellTime, _adaptation.AdaptDwell, tuning, nowMicroseconds,
                forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure),
                tighteningDrive: GatedScore(assessment, FailureMode.AccidentalActivation));
            // Smoothing: more (parameter down) on sustain-failure OR poor-tracking (union).
            UpdateKnob(AdaptationKnob.Smoothing, _adaptation.AdaptSmoothing, tuning, nowMicroseconds,
                forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure, FailureMode.PoorTracking),
                tighteningDrive: 0.0);
            // Geometry: enlarge on aiming OR poor-tracking (union).
            UpdateKnob(AdaptationKnob.TargetGeometry, _adaptation.AdaptGeometry, tuning, nowMicroseconds,
                forgivingDrive: ForgivingDrive(assessment, FailureMode.Aiming, FailureMode.PoorTracking),
                tighteningDrive: 0.0);
        }
        catch
        {
            Reset();
        }
    }

    /// <summary>
    /// Clears all momentary adaptation — every knob's fill and sustain timer — and the provider's stored
    /// fills, returning effective settings to baseline. The always-reachable escape (§8.6); invoked by the
    /// coordinator on the toolbar/tray reset and on session boundaries (plan 1b-⑤).
    /// </summary>
    public void Reset()
    {
        foreach (KnobState s in _state.Values)
        {
            s.ForgivingFill = 0.0;
            s.TighteningFill = 0.0;
            s.ForgivingRelaxSince = long.MinValue;
            s.TighteningRelaxSince = long.MinValue;
        }
        _provider.ClearMomentaryFills();
    }

    // Steps a knob's forgiving + tightening fills for one tick and pushes them to the provider ONLY when a
    // fill changed (so Changed / any consumer rebuild fires at most once per tick — bounded by the tick rate,
    // §9.1). A disabled knob collapses immediately to baseline and drops its sustain timers (§12).
    private void UpdateKnob(AdaptationKnob knob, bool enabled, PostureTuning tuning, long now,
        double forgivingDrive, double tighteningDrive)
    {
        KnobState s = _state[knob];
        double beforeForgiving = s.ForgivingFill;
        double beforeTightening = s.TighteningFill;

        if (!enabled)
        {
            s.ForgivingFill = 0.0;
            s.TighteningFill = 0.0;
            s.ForgivingRelaxSince = long.MinValue;
            s.TighteningRelaxSince = long.MinValue;
        }
        else
        {
            s.ForgivingFill = Step(s.ForgivingFill, forgivingDrive, tuning, now, ref s.ForgivingRelaxSince);
            s.TighteningFill = Step(s.TighteningFill, tighteningDrive, tuning, now, ref s.TighteningRelaxSince);
        }

        if (s.ForgivingFill != beforeForgiving || s.TighteningFill != beforeTightening)
            _provider.SetMomentaryFill(knob, DwellContext.Other, s.ForgivingFill, s.TighteningFill);
    }

    // One relax/recover/deadband step (§7.2). relaxSince tracks when the relax condition began (advanced by
    // the tick 'now') so the score must stay above RelaxThreshold for SustainSeconds before relaxing. Sustain
    // is WALL-CLOCK (now − relaxSince), not a consecutive-tick count, so SustainSeconds maps to real seconds
    // independent of the tick rate; a tick gap therefore reads as a continuous hold (the coordinator, 1b-⑤,
    // ticks monotonically at a fixed cadence and Resets on Pause, so no stale relaxSince survives a pause).
    private static double Step(double fill, double drive, PostureTuning tuning, long now, ref long relaxSince)
    {
        if (drive >= tuning.RelaxThreshold)
        {
            if (relaxSince == long.MinValue)
                relaxSince = now;
            if (now - relaxSince >= (long)tuning.SustainSeconds * 1_000_000L)
                return Math.Min(1.0, fill + tuning.RelaxStepFraction); // relax toward the band edge
            return fill; // above threshold but not yet held long enough → hold
        }

        relaxSince = long.MinValue; // dropped below the relax threshold → stop sustaining
        if (drive < tuning.RecoverThreshold)
            return Math.Max(0.0, fill - tuning.RecoverStepFraction); // recover toward baseline
        return fill; // deadband [RecoverThreshold, RelaxThreshold) → hold (hysteresis, §8.8)
    }

    // Peak GATED score among a knob's feeding modes (the union of forgiving responses, §8.2). Ungated modes
    // contribute 0 — the controller acts only on gated modes (§6.3; the F4 safety).
    private static double ForgivingDrive(StruggleAssessment assessment, params FailureMode[] feeders)
    {
        double drive = 0.0;
        foreach (FailureMode mode in feeders)
            drive = Math.Max(drive, GatedScore(assessment, mode));
        return drive;
    }

    // The mode's score when its confidence gate is met, else 0 (F4: below-gate evidence never drives adaptation).
    private static double GatedScore(StruggleAssessment assessment, FailureMode mode)
    {
        FailureModeAssessment? m = assessment.ForMode(mode);
        return m.HasValue && m.Value.GatePassed ? m.Value.Score : 0.0;
    }
}
