namespace EyeCtrl.App.Services;

using System;
using System.Windows.Threading;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IStruggleTelemetrySink"/> wrapper that serializes every report onto one thread (the UI
/// dispatcher) before delegating to an inner sink, and swallows any exception the inner throws
/// (design §9 serialized channel, §11 no-throw on the gaze path). This is the sink handed to
/// interaction surfaces.
/// <para>
/// It is also the enable gate for the report path (§9.1 "disabled = current cost"): while
/// <see cref="AdaptationSettings.Enabled"/> is false, a report is dropped here — before the dispatcher
/// marshal and before the buffering sink retains anything. Gating at this boundary rather than inside
/// <see cref="BufferingTelemetrySink"/> is what avoids the <c>BeginInvoke</c> for outcomes reported from
/// the gaze thread. Consequence: nothing accumulates while disabled, so the monitor starts from an empty
/// window on enable — which is what the coordinator's reset-on-enable edge already establishes.
/// </para>
/// </summary>
public sealed class DispatcherTelemetrySink : IStruggleTelemetrySink, IGazeQualitySink
{
    private readonly IStruggleTelemetrySink _inner;
    private readonly IGazeQualitySink? _innerQuality;
    private readonly AdaptationSettings _adaptation;
    private readonly Func<bool> _hasThreadAccess;
    private readonly Action<Action> _post;
    private readonly ILogger<DispatcherTelemetrySink>? _logger;

    /// <summary>Production constructor: serializes onto <paramref name="dispatcher"/>.</summary>
    /// <param name="inner">The sink that receives reports on the dispatcher thread.</param>
    /// <param name="dispatcher">The UI dispatcher that owns the serialized channel.</param>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Enabled"/> gates reporting.</param>
    /// <param name="logger">Optional logger for swallowed inner-sink exceptions.</param>
    /// <param name="innerQuality">Optional gaze-quality sink forwarded on the dispatcher thread.</param>
    public DispatcherTelemetrySink(IStruggleTelemetrySink inner, Dispatcher dispatcher,
        AdaptationSettings adaptation,
        ILogger<DispatcherTelemetrySink>? logger = null, IGazeQualitySink? innerQuality = null)
        : this(inner, adaptation, dispatcher.CheckAccess, a => dispatcher.BeginInvoke(a), logger, innerQuality)
    {
    }

    /// <summary>Testing constructor: injects the thread-access check and post delegate directly.</summary>
    /// <param name="inner">The inner sink.</param>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Enabled"/> gates reporting.
    /// Required rather than optional so the gate cannot be bypassed by omission.</param>
    /// <param name="hasThreadAccess">Returns true when the caller is already on the serialized thread.</param>
    /// <param name="post">Queues an action onto the serialized thread.</param>
    /// <param name="logger">Optional logger for swallowed inner-sink exceptions.</param>
    /// <param name="innerQuality">Optional gaze-quality sink forwarded on the dispatcher thread.</param>
    public DispatcherTelemetrySink(IStruggleTelemetrySink inner, AdaptationSettings adaptation,
        Func<bool> hasThreadAccess,
        Action<Action> post, ILogger<DispatcherTelemetrySink>? logger = null,
        IGazeQualitySink? innerQuality = null)
    {
        _inner = inner;
        _innerQuality = innerQuality;
        _adaptation = adaptation;
        _hasThreadAccess = hasThreadAccess;
        _post = post;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Report(in DwellOutcome outcome)
    {
        if (!_adaptation.Enabled)
            return; // §9.1 enable gate — dropped before any marshal or retention
        var copy = outcome; // struct copy so it can be captured when marshaling
        if (_hasThreadAccess())
            Forward(copy);
        else
            Post(() => Forward(copy)); // post itself can throw (e.g. BeginInvoke during shutdown)
    }

    private void Forward(DwellOutcome outcome)
    {
        try { _inner.Report(outcome); }
        catch (Exception ex) { _logger?.LogError(ex, "Struggle telemetry sink threw; outcome dropped."); }
    }

    /// <inheritdoc />
    public void ReportGazeQuality(in GazeQualitySummary summary)
    {
        if (_innerQuality is null) return;
        if (!_adaptation.Enabled)
            return; // §9.1 enable gate; the aggregator also stops producing summaries, this is the backstop
        var copy = summary;
        if (_hasThreadAccess())
            ForwardQuality(copy);
        else
            Post(() => ForwardQuality(copy));
    }

    private void ForwardQuality(GazeQualitySummary summary)
    {
        try { _innerQuality!.ReportGazeQuality(summary); }
        catch (Exception ex) { _logger?.LogError(ex, "Gaze-quality sink threw; summary dropped."); }
    }

    // Marshals an action onto the serialized thread, swallowing any failure of the post itself
    // (design §11 no-throw on the gaze path — the inner sink's exceptions are caught in Forward*,
    // the post delegate's exceptions are caught here).
    private void Post(Action action)
    {
        try { _post(action); }
        catch (Exception ex) { _logger?.LogError(ex, "Telemetry dispatcher post threw; outcome dropped."); }
    }
}
