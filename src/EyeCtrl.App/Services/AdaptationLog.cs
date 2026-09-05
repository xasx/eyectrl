namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// The struggle-adaptation session log (design §10). Two sides:
/// <list type="bullet">
/// <item>Serilog entries on gated-mode-set transitions — unchanged since 1b-⑤c.</item>
/// <item>The Phase-2 review file <c>%APPDATA%/EyeCtrl/adaptation-log.jsonl</c>: one JSON line per
/// learned-fill change (<c>kind":"learned"</c>, knob + old/new fill) and per mode transition
/// (<c>kind":"modes"</c>, the gated mode set + aggregate + the learned snapshot). Momentary-only
/// changes write nothing (they are derivative of mode transitions). In a sensitive context every
/// entry collapses to a coarse <c>kind":"suppressed"</c> marker (§8.10). Units are the provider's
/// band fills — absolute effective values are consumer-relative (a dwell fill composes against ~20
/// baselines) and are deliberately not logged (spec §10 amendment). Size-capped: on append past
/// <see cref="MaxLogBytes"/> the file rotates to <c>.jsonl.1</c> (single generation, replaced).</item>
/// </list>
/// All writes happen on the dispatcher thread (provider.Changed / the coordinator's tick), so the
/// I/O stays off the gaze hot path (§9.1). A null <c>jsonlPath</c> disables the file side entirely.
/// The decision methods are pure/headless-testable; only <see cref="Start"/>'s subscriptions are runtime.
/// </summary>
public sealed class AdaptationLog : IDisposable
{
    /// <summary>The JSONL size cap; a write past it rotates the file to <c>.jsonl.1</c>. Starting value, tunable.</summary>
    public const int MaxLogBytes = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly EffectiveSettingsProvider _provider;
    private readonly string? _jsonlPath;
    private readonly HashSet<FailureMode> _loggedGatedModes = new();
    private readonly Dictionary<AdaptationKnob, double> _lastLearned = new();
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    /// <summary>Initializes the log over its posture source, the suppression seam, and the provider whose
    /// learned fills are logged.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Posture"/> is recorded in each entry.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); when suppressing, only the coarse marker is logged.</param>
    /// <param name="provider">The effective-settings provider — its <c>Changed</c> drives learned-fill entries.</param>
    /// <param name="jsonlPath">Full path of the JSONL review file, or null to disable the file side.</param>
    public AdaptationLog(AdaptationSettings adaptation, IFeedbackGate feedbackGate, EffectiveSettingsProvider provider, string? jsonlPath = null)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
        _provider = provider;
        _jsonlPath = jsonlPath;
    }

    /// <summary>The set of failure modes whose confidence gate is met in the given assessment.</summary>
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
            WriteJsonl(new { ts = DateTimeOffset.UtcNow, kind = "suppressed" });
            return AdaptationLogAction.SuppressedMarker;
        }

        Log.Information(
            "Adaptation: struggle {AggregateLevel:F2}, gated modes [{Modes}], posture {Posture}",
            assessment.AggregateLevel,
            string.Join(", ", current.OrderBy(m => m)),
            _adaptation.Posture);
        WriteJsonl(new
        {
            ts = DateTimeOffset.UtcNow,
            kind = "modes",
            aggregateLevel = Math.Round(assessment.AggregateLevel, 2),
            modes = current.OrderBy(m => m).Select(m => m.ToString()).ToArray(),
            posture = _adaptation.Posture.ToString(),
            learned = LearnedSnapshot(),
        });
        return AdaptationLogAction.Logged;
    }

    /// <summary>
    /// Appends a <c>kind:"caregiver"</c> entry for one alert onset (§10 Phase-3 resolution). Called by
    /// <see cref="CaregiverAlertService"/> via its injected callback on UNSUPPRESSED onsets only — a
    /// suppressed onset writes nothing, so the file never carries a timing trace of credential-adjacent
    /// struggle (§8.10, the same discipline as <see cref="OnProviderChanged"/>'s suppressed-marker rule).
    /// Rides the same size cap and rotation as every other entry.
    /// </summary>
    /// <param name="condition">Which sustained condition raised the alert.</param>
    public void LogCaregiverAlert(CaregiverAlertCondition condition) =>
        WriteJsonl(new { ts = DateTimeOffset.UtcNow, kind = "caregiver", condition = condition.ToString() });

    // provider.Changed handler: writes one entry per knob whose learned fill changed since the last
    // observation — including a knob that DROPPED OUT of the enumeration (decayed/reset to 0), so the
    // §8.7 "given back" event and the §8.6 reset stay visible (review round 1, finding 13). Momentary-
    // only changes write nothing, suppressed or not: `Changed` fires at up to 4 Hz during momentary
    // ramps, and an unconditional suppressed marker there would be a 4 Hz timing trace of interaction
    // during credential entry — the opposite of §8.10 (finding 11). The coarse marker is written only
    // when there was something to suppress.
    private void OnProviderChanged()
    {
        var current = _provider.EnumerateLearnedFills().ToDictionary(e => e.Knob, e => e.Fill);
        var changed = new List<(AdaptationKnob Knob, double Old, double New)>();
        foreach (var (knob, fill) in current)
        {
            double last = _lastLearned.TryGetValue(knob, out double v) ? v : 0.0;
            if (fill != last)
                changed.Add((knob, last, fill));
        }
        foreach (var knob in _lastLearned.Keys)
            if (!current.ContainsKey(knob))
                changed.Add((knob, _lastLearned[knob], 0.0)); // dropped out = decayed or reset to zero

        if (changed.Count == 0)
            return; // momentary-only change (or no-op) — nothing to log, suppressed or not

        if (_feedbackGate.IsFeedbackSuppressed)
        {
            WriteJsonl(new { ts = DateTimeOffset.UtcNow, kind = "suppressed" });
        }
        else
        {
            foreach (var (knob, old, @new) in changed)
                WriteJsonl(new { ts = DateTimeOffset.UtcNow, kind = "learned", knob = knob.ToString(), old, @new });
        }
        RefreshLearnedSnapshot();
    }

    private void RefreshLearnedSnapshot()
    {
        _lastLearned.Clear();
        foreach (var (knob, fill) in _provider.EnumerateLearnedFills())
            _lastLearned[knob] = fill;
    }

    private object LearnedSnapshot()
    {
        var dict = new Dictionary<string, double>();
        foreach (var (knob, fill) in _provider.EnumerateLearnedFills())
            dict[JsonNamingPolicy.CamelCase.ConvertName(knob.ToString())] = Math.Round(fill, 4);
        return dict;
    }

    private void WriteJsonl(object entry)
    {
        if (_jsonlPath is null)
            return;
        try
        {
            var file = new FileInfo(_jsonlPath);
            if (file.Exists && file.Length > MaxLogBytes)
                File.Move(_jsonlPath, _jsonlPath + ".1", overwrite: true);
            File.AppendAllText(_jsonlPath, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Warning(ex, "Failed to append the adaptation log entry to {FilePath}; entry skipped", _jsonlPath);
        }
    }

    /// <summary>Subscribes to the coordinator's per-tick assessment stream and the provider's change
    /// stream. Call once on the UI thread; idempotent.</summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> drives the transition entries.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return;
        _coordinator = coordinator;
        coordinator.AssessmentUpdated += OnAssessmentEvent;
        _provider.Changed += OnProviderChanged;
        RefreshLearnedSnapshot(); // entries are diffs from now on
    }

    private void OnAssessmentEvent(StruggleAssessment assessment) => OnAssessment(assessment);

    /// <summary>Unsubscribes from both streams.</summary>
    public void Dispose()
    {
        if (_coordinator is not null)
        {
            _coordinator.AssessmentUpdated -= OnAssessmentEvent;
            _coordinator = null;
        }
        _provider.Changed -= OnProviderChanged;
    }
}
