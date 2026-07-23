namespace EyeCtrl.App.Services;

using System;
using System.Windows.Threading;
using EyeCtrl.Core.Adaptation;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IStruggleTelemetrySink"/> wrapper that serializes every report onto one thread (the UI
/// dispatcher) before delegating to an inner sink, and swallows any exception the inner throws
/// (design §9 serialized channel, §11 no-throw on the gaze path). This is the sink handed to
/// interaction surfaces.
/// </summary>
public sealed class DispatcherTelemetrySink : IStruggleTelemetrySink, IGazeQualitySink
{
    private readonly IStruggleTelemetrySink _inner;
    private readonly IGazeQualitySink? _innerQuality;
    private readonly Func<bool> _hasThreadAccess;
    private readonly Action<Action> _post;
    private readonly ILogger<DispatcherTelemetrySink>? _logger;

    /// <summary>Production constructor: serializes onto <paramref name="dispatcher"/>.</summary>
    /// <param name="inner">The sink that receives reports on the dispatcher thread.</param>
    /// <param name="dispatcher">The UI dispatcher that owns the serialized channel.</param>
    /// <param name="logger">Optional logger for swallowed inner-sink exceptions.</param>
    /// <param name="innerQuality">Optional gaze-quality sink forwarded on the dispatcher thread.</param>
    public DispatcherTelemetrySink(IStruggleTelemetrySink inner, Dispatcher dispatcher,
        ILogger<DispatcherTelemetrySink>? logger = null, IGazeQualitySink? innerQuality = null)
        : this(inner, dispatcher.CheckAccess, a => dispatcher.BeginInvoke(a), logger, innerQuality)
    {
    }

    /// <summary>Testing constructor: injects the thread-access check and post delegate directly.</summary>
    /// <param name="inner">The inner sink.</param>
    /// <param name="hasThreadAccess">Returns true when the caller is already on the serialized thread.</param>
    /// <param name="post">Queues an action onto the serialized thread.</param>
    /// <param name="logger">Optional logger for swallowed inner-sink exceptions.</param>
    /// <param name="innerQuality">Optional gaze-quality sink forwarded on the dispatcher thread.</param>
    public DispatcherTelemetrySink(IStruggleTelemetrySink inner, Func<bool> hasThreadAccess,
        Action<Action> post, ILogger<DispatcherTelemetrySink>? logger = null,
        IGazeQualitySink? innerQuality = null)
    {
        _inner = inner;
        _innerQuality = innerQuality;
        _hasThreadAccess = hasThreadAccess;
        _post = post;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Report(in DwellOutcome outcome)
    {
        var copy = outcome; // struct copy so it can be captured when marshaling
        if (_hasThreadAccess())
            Forward(copy);
        else
            _post(() => Forward(copy));
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
        var copy = summary;
        if (_hasThreadAccess())
            ForwardQuality(copy);
        else
            _post(() => ForwardQuality(copy));
    }

    private void ForwardQuality(GazeQualitySummary summary)
    {
        try { _innerQuality!.ReportGazeQuality(summary); }
        catch (Exception ex) { _logger?.LogError(ex, "Gaze-quality sink threw; summary dropped."); }
    }
}
