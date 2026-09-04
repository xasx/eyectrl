namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

/// <summary>
/// The Phase-2 learned layer (design §7.4): a slow, forgiving-only EWMA over the momentary layer's
/// forgiving band fills. One mechanism both accumulates (sustained momentary relaxation ⇒ the offset
/// creeps up, so the momentary layer starts from a partially-relaxed state) and decays toward baseline
/// (observed fill 0 ⇒ the offset shrinks — §8.7: the learned layer must continuously re-earn itself).
/// The observed value is the controller's <b>post-hysteresis</b> fill, so the promotion criterion is
/// the EWMA time constant applied to already-gated, already-sustained evidence — no second confidence
/// machinery. Ticked by the <c>AdaptationCoordinator</c> on assess ticks only: paused, disconnected,
/// or disabled stretches freeze the state (no sample ⇒ no decay — no offline aging either, decision
/// D9). A per-knob <c>Adapt*</c> toggle switched off zeroes that knob's learned offset
/// <b>synchronously</b> on the settings change (whole-branch review finding R2), with the sample path
/// as a backstop — an off switch must stop adapting at once, not decay for 40 minutes (review round
/// 1, finding 8).
/// Persisted via <see cref="AdaptationStateFile"/>; cleared by <see cref="ResetLearned"/> (the manual
/// escape and the settings action — never by session boundaries, §13.1). Single-threaded (dispatcher).
/// </summary>
public sealed class AdaptationLearner
{
    /// <summary>Wall-clock spacing between EWMA samples. 30 s — with <see cref="Alpha"/> 0.05 this is a
    /// time constant of ≈ 10 minutes: full-band momentary struggle learns to the 0.5 cap in ≈ 9 minutes,
    /// and calm decays a cap-filled offset below the push epsilon in ≈ 40 minutes. Starting value, tunable.</summary>
    public const long SampleIntervalMicroseconds = 30_000_000L;

    /// <summary>EWMA weight of the newest observation. 0.05. Starting value, tunable.</summary>
    public const double Alpha = 0.05;

    /// <summary>Minimum movement since the last push before a new value is written to the provider —
    /// the §7.4 rate limit (and the §9.1 budget guard: learned pushes are ≥ 30 s apart per knob, far
    /// inside the smoothing rebuild budget). 0.01. Starting value, tunable.</summary>
    public const double PushEpsilon = 0.01;

    // "Not yet sampled." long.MinValue, NOT 0 — gaze timestamps carry an arbitrary epoch and 0 is a
    // legitimate value (the Unseeded discipline; see AdaptationCoordinator._latestGazeTs).
    private const long Unsampled = long.MinValue;

    private static readonly AdaptationKnob[] Knobs =
    {
        AdaptationKnob.DwellTime,
        AdaptationKnob.FixationRadius,
        AdaptationKnob.Smoothing,
        AdaptationKnob.TargetGeometry,
    };

    // The per-knob Adapt* toggle names → their knobs, for the synchronous off-switch zeroing (R2).
    private static readonly IReadOnlyDictionary<string, AdaptationKnob> KnobToggles =
        new Dictionary<string, AdaptationKnob>
        {
            [nameof(AdaptationSettings.AdaptDwell)] = AdaptationKnob.DwellTime,
            [nameof(AdaptationSettings.AdaptTolerance)] = AdaptationKnob.FixationRadius,
            [nameof(AdaptationSettings.AdaptSmoothing)] = AdaptationKnob.Smoothing,
            [nameof(AdaptationSettings.AdaptGeometry)] = AdaptationKnob.TargetGeometry,
        };

    private readonly EffectiveSettingsProvider _provider;
    private readonly AdaptationSettings _adaptation;
    private readonly ILogger<AdaptationLearner>? _logger;
    private readonly string? _stateFilePath; // null = no persistence (unit tests)
    private readonly Dictionary<AdaptationKnob, double> _learned = new();
    private readonly Dictionary<AdaptationKnob, double> _pushed = new();
    private long _lastSampleAt = Unsampled;
    // Set when THIS session ever loaded state or took a sample. A never-participated session (the app
    // run with adaptation off: Load is skipped and the coordinator never ticks) must not clobber the
    // persisted offsets a previous session earned when OnExit persists (whole-branch review finding R1).
    private bool _sessionParticipated;

    /// <summary>Initializes the learner over the provider it observes and pushes into.</summary>
    /// <param name="provider">The effective-settings provider — read for momentary fills, written with learned fills.</param>
    /// <param name="adaptation">Adaptation settings — the per-knob <c>Adapt*</c> toggles zero a knob's learned offset immediately when off.</param>
    /// <param name="logger">Optional logger; persistence failures are swallowed after logging (§11).</param>
    /// <param name="stateFilePath">Full path of <c>adaptation-state.json</c>, or null to disable persistence.</param>
    public AdaptationLearner(
        EffectiveSettingsProvider provider,
        AdaptationSettings adaptation,
        ILogger<AdaptationLearner>? logger = null,
        string? stateFilePath = null)
    {
        _provider = provider;
        _adaptation = adaptation;
        _logger = logger;
        _stateFilePath = stateFilePath;
        // Synchronous off-switch zeroing (whole-branch review finding R2): toggling a per-knob Adapt*
        // switch off must clear that knob's learned offset at once, not at the next (≤ 30 s away)
        // sample. PropertyChanged fires on the UI thread after the field is set, so the handler reads
        // the new value. No unsubscribe: learner and settings are co-extensive container singletons
        // (the provider's subscription to the same object follows the same discipline).
        _adaptation.PropertyChanged += OnAdaptationSettingsChanged;
    }

    private void OnAdaptationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || !KnobToggles.TryGetValue(e.PropertyName, out var knob))
            return;
        if (IsKnobEnabled(knob))
            return; // toggle ON needs nothing — the EWMA resumes from zero as the controller re-relaxes

        _learned[knob] = 0.0;
        double pushed = _pushed.TryGetValue(knob, out double p) ? p : 0.0;
        if (pushed != 0.0)
        {
            _provider.SetLearnedFill(knob, 0.0);
            _pushed[knob] = 0.0;
        }
    }

    /// <summary>Loads the persisted state (if any) and applies its learned fills to the provider directly,
    /// bypassing <see cref="PushEpsilon"/> (persisted values were already rate-limited when they were earned).
    /// Clears the provider's learned store first, so a reload can never leave stale entries for knobs the
    /// file no longer mentions.</summary>
    public void Load()
    {
        _sessionParticipated = true;
        _learned.Clear();
        _pushed.Clear();
        _provider.ClearLearnedFills();
        if (_stateFilePath is null)
            return;
        var state = AdaptationStateFile.Load(_stateFilePath, _logger);
        foreach (var (knob, fill) in state.Learned)
        {
            _learned[knob] = fill;
            _pushed[knob] = fill;
            if (fill != 0.0)
                _provider.SetLearnedFill(knob, fill);
        }
    }

    /// <summary>
    /// Advances one tick: samples the momentary forgiving fills (per knob, at <see cref="DwellContext.Other"/>
    /// — the global momentary layer) no more often than <see cref="SampleIntervalMicroseconds"/>, steps each
    /// knob's learned EWMA toward its observation (or zeroes it outright when its per-knob toggle is off),
    /// clamps to <c>[0, <see cref="AdaptationSettings.LearnedMaxFill"/>]</c>, and pushes to the provider only
    /// when a knob moved ≥ <see cref="PushEpsilon"/> since its last push.
    /// </summary>
    /// <param name="nowMicroseconds">The current tick timestamp, in microseconds (the gaze clock).</param>
    public void Tick(long nowMicroseconds)
    {
        // Gaze-clock restart (review round 1, finding 7): `now` is the coordinator's latest GAZE timestamp,
        // and a tracker reconnect can restart that clock near 0. Waiting on a due time would then block
        // sampling for the old epoch's uptime — the silent-degradation class StruggleMonitor and GazeEngine
        // already guard. Resync on any backward jump and treat it as the first sample of a new epoch.
        if (nowMicroseconds < _lastSampleAt)
            _lastSampleAt = Unsampled;
        if (_lastSampleAt != Unsampled && nowMicroseconds - _lastSampleAt < SampleIntervalMicroseconds)
            return;
        _lastSampleAt = nowMicroseconds;
        _sessionParticipated = true; // a live sample — this session's learning is real and persistable

        foreach (var knob in Knobs)
        {
            double observed = _provider.TryGetMomentaryFill(knob, DwellContext.Other, out double forgiving, out _)
                ? forgiving
                : 0.0; // the tightening component is deliberately invisible — learned is forgiving-only (§7.4)
            double value = _learned.TryGetValue(knob, out double l) ? l : 0.0;
            // A disabled per-knob toggle zeroes the learned offset outright (finding 8): the off switch
            // must stop adapting at once. The EWMA path runs only for enabled knobs.
            value = IsKnobEnabled(knob) ? value + Alpha * (observed - value) : 0.0;
            value = Math.Clamp(value, 0.0, AdaptationSettings.LearnedMaxFill);
            _learned[knob] = value;

            double pushed = _pushed.TryGetValue(knob, out double p) ? p : 0.0;
            if (Math.Abs(value - pushed) >= PushEpsilon)
            {
                _provider.SetLearnedFill(knob, value);
                _pushed[knob] = value;
            }
        }
    }

    private bool IsKnobEnabled(AdaptationKnob knob) => knob switch
    {
        AdaptationKnob.DwellTime => _adaptation.AdaptDwell,
        AdaptationKnob.FixationRadius => _adaptation.AdaptTolerance,
        AdaptationKnob.Smoothing => _adaptation.AdaptSmoothing,
        AdaptationKnob.TargetGeometry => _adaptation.AdaptGeometry,
        _ => true,
    };

    /// <summary>Zeroes every learned offset, clears the provider, and persists the zeroed state — the manual
    /// escape's learned half and the settings-page action (§10). Idempotent. The persist is deliberately
    /// synchronous: the reset's durability matters more than milliseconds on this path (a lost reset would
    /// resurrect cleared offsets on the next start), the write is one small JSON file, and every I/O failure
    /// is caught below — the escape itself cannot fail (review round 1, finding 16).</summary>
    public void ResetLearned()
    {
        _learned.Clear();
        _pushed.Clear();
        _provider.ClearLearnedFills();
        PersistCore(); // explicit user intent — durable even in a never-participated session (R1)
    }

    /// <summary>Persists the current learned state (all four knobs, zeros included — a stable schema),
    /// but only when this session participated (loaded or sampled). A session that never enabled
    /// adaptation must leave the file a previous session earned untouched (whole-branch review finding
    /// R1). Best-effort: an I/O failure logs and is swallowed (§11 — a comfort feature must never end
    /// the session).</summary>
    public void Persist()
    {
        if (!_sessionParticipated)
            return;
        PersistCore();
    }

    // The unconditional write shared by Persist and ResetLearned.
    private void PersistCore()
    {
        if (_stateFilePath is null)
            return;
        try
        {
            AdaptationStateFile.Save(_stateFilePath, new AdaptationState(
                AdaptationState.CurrentVersion, DateTimeOffset.UtcNow, Snapshot().ToDictionary(e => e.Knob, e => e.Fill)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex, "Failed to persist adaptation state to {FilePath}; learned offsets continue in memory", _stateFilePath);
        }
    }

    /// <summary>The current learned fills for all knobs (zeros included), for diagnostics and persistence.</summary>
    public IReadOnlyList<(AdaptationKnob Knob, double Fill)> Snapshot() =>
        Knobs.Select(k => (k, _learned.TryGetValue(k, out double v) ? v : 0.0)).ToList();
}
