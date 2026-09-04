namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;
using System;
using System.Collections.Generic;

/// <summary>
/// The persisted Phase-2 learned state (design §7.4): one forgiving band-fill fraction per knob,
/// global in v1 (per-context learned is a future extension — the store is keyed by knob alone).
/// Validated per entry on load; <see cref="AdaptationStateFile"/> drops invalid entries and keeps
/// the survivors (the ClampGazeIndicator precedent at file granularity).
/// </summary>
/// <param name="Version">Schema version; a mismatch with <see cref="CurrentVersion"/> resets the state.</param>
/// <param name="SavedAtUtc">When the state was written (diagnostic only — never used for expiry).</param>
/// <param name="Learned">Per-knob learned band fills, each in <c>[0, <see cref="AdaptationSettings.LearnedMaxFill"/>]</c>.</param>
public sealed record AdaptationState(
    int Version,
    DateTimeOffset SavedAtUtc,
    IReadOnlyDictionary<AdaptationKnob, double> Learned)
{
    /// <summary>The schema version this build writes and accepts.</summary>
    public const int CurrentVersion = 1;

    /// <summary>An empty state — no learned offsets (fresh install, or rejected file).</summary>
    public static AdaptationState Empty { get; } = new(CurrentVersion, DateTimeOffset.MinValue, new Dictionary<AdaptationKnob, double>());

    /// <summary>Whether a persisted learned value is in-band (finite, forgiving-only, ≤ the cap).</summary>
    public static bool IsValueValid(double value) =>
        double.IsFinite(value) && value >= 0.0 && value <= AdaptationSettings.LearnedMaxFill;
}
