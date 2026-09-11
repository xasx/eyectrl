namespace EyeCtrl.Core.Diagnostics;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Taps the RAW gaze stream for offline analysis by decorating the real <see cref="IGazeProvider"/>:
/// it subscribes to the inner provider's samples, hands each to a recording sink, and re-raises it
/// unchanged. Every other member forwards verbatim, so consumers cannot tell it is there.
/// <para>
/// A decorator rather than a <c>GazeSampled</c> handler on purpose. The recording has to be of the
/// stream as the device produced it — before calibration, before the filter, before gap
/// interpolation — because the point of the exercise is to measure what the pipeline is given, not
/// what it produces. Tapping here also keeps the recorder free of any dependency on engine
/// internals, and lets it state provider identity in the header.
/// </para>
/// <para>
/// <b>Opt-in.</b> Gated per sample on <see cref="DiagnosticsSettings.RecordRawGaze"/>, which is false
/// by default (same discipline as <c>GazeQualityAggregator</c>'s adaptation gate): while it is off,
/// this class does one bool read and a delegate invoke per sample and nothing else — no buffer, no
/// file, no allocation.
/// </para>
/// <para>
/// <b>Nothing escapes into the provider.</b> The inner provider's callback runs on the Tobii poll
/// thread, where a managed exception unwinds through a native Stream Engine frame: the provider
/// treats that as a device error, drops <c>IsConnected</c> and reconnects with backoff
/// (<c>TobiiStreamEngineProvider.cs</c>'s poll-loop catch). A diagnostic that can cost the user their
/// only input channel is not worth having, so every recording fault is swallowed here and the
/// recorder self-disables — the sample is still re-raised.
/// </para>
/// </summary>
public sealed class RecordingGazeProvider : IGazeProviderDecorator
{
    private readonly IGazeProvider _inner;
    private readonly IGazeRecordingSink _sink;
    private readonly DiagnosticsSettings _diagnostics;
    private readonly Action<Exception>? _onFault;

    private readonly Action<GazePoint> _innerSampleHandler;
    private Action<GazePoint>? _handlers;
    private volatile bool _recordingFaulted;
    private int _faultReported;   // 0/1 latch behind ReportFaultOnce; Interlocked, so any thread may report
    private bool _disposed;

    /// <summary>Initializes the decorator over a provider and a sink.</summary>
    /// <param name="inner">The real provider. All members forward to it.</param>
    /// <param name="sink">Where recorded samples go. Only touched while recording is enabled.</param>
    /// <param name="diagnostics">Settings carrying the opt-in gate; required so it cannot be bypassed by omission.</param>
    /// <param name="onFault">Optional one-shot fault report, invoked at most once when recording self-disables.</param>
    public RecordingGazeProvider(
        IGazeProvider inner,
        IGazeRecordingSink sink,
        DiagnosticsSettings diagnostics,
        Action<Exception>? onFault = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _onFault = onFault;

        // Cached delegate: subscribing with a method group would allocate a new one per
        // subscribe/unsubscribe pair, and the unsubscribe in Dispose must match the instance used here.
        _innerSampleHandler = OnInnerSample;
        _inner.GazePointReceived += _innerSampleHandler;
    }

    /// <inheritdoc />
    public IGazeProvider Inner => _inner;

    /// <summary><c>true</c> once a recording fault has permanently disabled the tap for this session.</summary>
    public bool RecordingFaulted => _recordingFaulted;

    /// <inheritdoc />
    /// <remarks>
    /// Subscribers are held here rather than forwarded to the inner provider so the sample reaches the
    /// sink BEFORE any consumer sees it: the recording is then a faithful record of the input even if a
    /// consumer throws. It also keeps unsubscription symmetric — <see cref="Dispose"/> detaches exactly
    /// one handler from the inner provider regardless of how many consumers subscribed here.
    /// </remarks>
    public event Action<GazePoint>? GazePointReceived
    {
        add => _handlers += value;
        remove => _handlers -= value;
    }

    private void OnInnerSample(GazePoint sample)
    {
        // The gate, read per sample. A bool is a single aligned byte and cannot tear; the worst a race
        // with the settings thread costs is one sample recorded either side of the toggle.
        if (_diagnostics.RecordRawGaze && !_recordingFaulted)
        {
            try
            {
                _sink.Record(sample);
            }
            catch (Exception ex)
            {
                // Self-disable rather than fault again on the next sample: at 30-90 Hz a repeating fault
                // would be a per-sample try/catch storm on the gaze thread, and a broken recorder cannot
                // fix itself mid-session.
                _recordingFaulted = true;
                ReportFaultOnce(ex);
            }
        }

        // Unconditional and outside the try: the decorator is transparent, so the sample is re-raised
        // whatever the recorder did. A consumer's own exception propagates exactly as it would without
        // this class in the chain — GazeEngine guards its handlers, and swallowing here would hide a
        // pipeline fault behind a diagnostic.
        _handlers?.Invoke(sample);
    }

    private void ReportFaultOnce(Exception ex)
    {
        // Once per INSTANCE, not once per call site. Three sites can report (record, flush, dispose),
        // and the constructor's contract says the callback fires at most once — without this latch a
        // record fault followed by a flush fault would report twice and quietly break that promise.
        if (Interlocked.Exchange(ref _faultReported, 1) != 0) return;

        // Reporting is itself best-effort: a throwing logger must not become the fault that reaches the
        // poll loop. Mirrors HandlerFaultReporter's treatment of a broken sink.
        try { _onFault?.Invoke(ex); }
        catch { /* nothing left to report to */ }
    }

    /// <inheritdoc />
    public void Start() => _inner.Start();

    /// <inheritdoc />
    /// <remarks>
    /// Asks the sink to persist and returns; it does NOT wait. The inner provider's <c>Stop</c> joins
    /// its poll thread on a budget, and adding a disk flush in front of that join would spend the
    /// user's shutdown on a diagnostic. Whatever is still buffered is drained by the sink's own thread,
    /// or on <see cref="Dispose"/>.
    /// </remarks>
    public void Stop()
    {
        try { _sink.RequestFlush(); }
        catch (Exception ex) { _recordingFaulted = true; ReportFaultOnce(ex); }
        _inner.Stop();
    }

    /// <inheritdoc />
    public bool IsConnected => _inner.IsConnected;

    /// <inheritdoc />
    public GazeProviderCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    /// <remarks>Forwarded so a provider that raises track status is not silenced by being wrapped.</remarks>
    public event Action<TrackStatusSample>? TrackStatusReceived
    {
        add => _inner.TrackStatusReceived += value;
        remove => _inner.TrackStatusReceived -= value;
    }

    /// <inheritdoc />
    public bool TryGetLatestStatus(out TrackStatusSample sample) => _inner.TryGetLatestStatus(out sample);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inner.GazePointReceived -= _innerSampleHandler;
        _handlers = null;

        // The sink owns its own shutdown budget; a diagnostic must not be able to hang disposal.
        try { _sink.Dispose(); }
        catch (Exception ex) { ReportFaultOnce(ex); }

        _inner.Dispose();
    }
}
