namespace EyeCtrl.Core.Diagnostics;

using System.Text.Json;
using EyeCtrl.Core.Models;

/// <summary>
/// Records the ground truth of a scripted collection run and writes it as a sidecar next to the raw
/// recording.
/// <para>
/// The point of the exercise: a free-view recording can only be scored against its own centroid, which
/// by construction cannot see a systematic offset — if every sample is 40 px to the left of where the
/// user was looking, the centroid is 40 px to the left too and the deviation reads as zero. A scripted
/// run knows where the user was ASKED to look, so the analyzer can score absolute accuracy.
/// </para>
/// <para>
/// Deliberately a sidecar rather than a change to <c>calibration.json</c>: that file's shape is load-
/// bearing (a transform applied to every gaze point, guarded by a validator, matched on monitor
/// identity), and a diagnostic has no business widening it. The sidecar also lives and dies with the
/// recording it describes, which is the thing it has to stay aligned to.
/// </para>
/// <para>
/// <b>Self-contained since the bias probe.</b> Each window carries its own raw samples, so the
/// measure-only probe's offline estimator runs from the sidecar alone instead of the separate
/// <c>gaze-recording.csv</c>, whose single path is appended to and rotated across sessions. The cost
/// is sidecar size; the payoff is that a measurement session cannot be invalidated by a later launch,
/// and the probe does not depend on a global diagnostics flag a gaze-only user cannot see.
/// </para>
/// <para>
/// Threading: <see cref="OnSample"/> is called on the gaze thread; the writes happen on whichever
/// thread ends the session. Sample accumulation touches only the open-window fields, which no other
/// thread reads. The window LIST is different — the gaze thread appends to it at each target
/// transition while the ending thread snapshots it — so every touch of <c>_windows</c> is under
/// <c>_windowsGate</c>. The ordering argument that the subscription is gone by then is true for the
/// ordinary path and worth nothing for an abandon mid-run, and a <c>List&lt;T&gt;</c> resized under a
/// concurrent read does not fail loudly: it returns a torn array, or a ground truth silently missing a
/// target. The lock is taken once per target (nine times a session), never per sample.
/// </para>
/// </summary>
public sealed class ScriptedGroundTruthRecorder : IGroundTruthSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Names, not numbers: the sidecar is read by a human operator and by the offline estimator,
        // and an outcome of 1 tells neither which ending it was. Reading is case-insensitive, so a
        // hand-edited file still parses.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _filePath;
    private readonly Action<Exception>? _onFault;
    // Injectable so the same-millisecond collision path can be tested deterministically.
    private readonly Func<DateTime> _utcNow;

    private readonly List<GroundTruthWindow> _windows = new();
    private readonly object _windowsGate = new();
    private string _providerType = string.Empty;
    private PhysicalPoint _screenSize;
    private int _settleWindowMs;
    private GroundTruthOutcome _outcome = GroundTruthOutcome.Completed;
    private GroundTruthMode _mode = GroundTruthMode.Calibration;
    // Where THIS session's sidecar goes. The configured path for a normal calibration; a per-session
    // timestamped sibling for a measure-only probe, because probe → Tobii recalibration → probe again
    // must not let the second probe erase the first.
    private string _writePath;

    // The window currently open. Held as fields rather than a mutable struct in the list so the
    // gaze-thread update is a handful of field writes with no copying.
    private int _openTargetId = -1;
    private PhysicalPoint _openTarget;
    private long _openFromUs;
    private long? _openArrivedUs;
    private long _openLastUs;
    private int _openSamples;
    // The raw stream behind the open window, so the finished sidecar needs no other file to be
    // analyzed. Touched only by the gaze thread; handed to the windows list (under the gate) when
    // the window closes.
    private readonly List<GroundTruthSample> _openSampleList = new();

    /// <summary>
    /// Test seam: how the open window's samples are snapshotted when the window closes. Production is
    /// <c>ToArray</c>; a test replaces it to fault the close deterministically (an in-flight gaze
    /// callback racing the snapshot is the real, un-testable trigger).
    /// </summary>
    internal Func<List<GroundTruthSample>, GroundTruthSample[]> SnapshotOpenSamples { get; set; } =
        static list => list.ToArray();

    /// <summary>Initializes the recorder.</summary>
    /// <param name="filePath">Full path of the sidecar to write. Its directory is created if absent.</param>
    /// <param name="onFault">Optional one-shot report if the sidecar cannot be written.</param>
    /// <param name="utcNow">Clock used to name a probe's own sidecar; defaults to the system UTC clock.</param>
    public ScriptedGroundTruthRecorder(
        string filePath, Action<Exception>? onFault = null, Func<DateTime>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A sidecar path is required.", nameof(filePath));
        _filePath = filePath;
        _writePath = filePath;
        _onFault = onFault;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    /// <summary>
    /// Windows closed so far this session, in presentation order. A snapshot: the live list is appended
    /// to from the gaze thread, so handing out a reference to it would let a caller enumerate a list
    /// mid-resize.
    /// </summary>
    public IReadOnlyList<GroundTruthWindow> Windows
    {
        get { lock (_windowsGate) { return _windows.ToArray(); } }
    }

    /// <summary>Whether a session is currently being recorded.</summary>
    public bool IsRecording { get; private set; }

    /// <inheritdoc />
    public void BeginSession(
        string providerType, PhysicalPoint screenSize, int settleWindowMs,
        GroundTruthMode mode = GroundTruthMode.Calibration)
    {
        // Discard BEFORE clearing, not after: closing an open window appends it, so the other order
        // would resurrect a previous session's last target into the freshly cleared list.
        DiscardOpenWindow();
        lock (_windowsGate) { _windows.Clear(); }
        _providerType = providerType;
        _screenSize = screenSize;
        _settleWindowMs = settleWindowMs;
        _outcome = GroundTruthOutcome.Completed;
        _mode = mode;
        _writePath = mode == GroundTruthMode.MeasureOnly ? ProbePath(_filePath) : _filePath;
        IsRecording = true;
    }

    /// <summary>
    /// A per-session filename for a measure-only probe, a sibling of the configured sidecar path:
    /// <c>bias-probe-&lt;UTC&gt;.groundtruth.json</c>, with a numeric suffix if two sessions land in
    /// the same millisecond. Unique by construction, so a probe can never overwrite another probe or a
    /// normal calibration's sidecar — the plan is probe → Tobii recalibration → probe again, and both
    /// probes must survive to be compared.
    /// </summary>
    private string ProbePath(string configuredPath)
    {
        var directory = Path.GetDirectoryName(configuredPath);
        if (string.IsNullOrEmpty(directory)) directory = ".";
        var stamp = _utcNow().ToString("yyyyMMdd'T'HHmmss'.'fff", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"bias-probe-{stamp}.groundtruth.json");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"bias-probe-{stamp}-{n}.groundtruth.json");
        return path;
    }

    /// <inheritdoc />
    public void OnSample(int targetId, PhysicalPoint target, GazePoint sample, bool hasArrived)
    {
        if (!IsRecording) return;

        if (targetId != _openTargetId)
        {
            CloseOpenWindow();
            _openTargetId = targetId;
            _openTarget = target;
            _openFromUs = sample.TimestampMicroseconds;
            _openArrivedUs = null;
            _openSamples = 0;
        }

        // First sample at which the collector reports arrival. Latched, not overwritten: arrival is
        // the moment the fixation being measured starts, and a later sample cannot un-start it.
        if (hasArrived && _openArrivedUs is null) _openArrivedUs = sample.TimestampMicroseconds;

        _openLastUs = sample.TimestampMicroseconds;
        _openSamples++;
        _openSampleList.Add(new GroundTruthSample(sample.X, sample.Y, sample.TimestampMicroseconds, sample.IsValid));
    }

    /// <inheritdoc />
    public void CompleteSession(GroundTruthOutcome outcome = GroundTruthOutcome.Completed)
    {
        if (!IsRecording) return;
        _outcome = outcome;
        // Stop accepting samples BEFORE closing the open window. An in-flight gaze callback has
        // already passed its IsRecording check, so this narrows the race to that one callback.
        IsRecording = false;

        // A faulted close costs the OPEN window, not the probe: an in-flight sample can still throw
        // out of ToArray, and if that took the outer try with it every window already closed would be
        // lost too. Report it and write what we have.
        try
        {
            CloseOpenWindow();
        }
        catch (Exception ex)
        {
            ReportFault(ex);
        }

        try
        {
            // Snapshot inside the try, with the rest of the write: a list read while the gaze thread
            // appends is what can throw here, and letting it escape CompleteSession would turn a
            // missing diagnostic sidecar into a failed calibration. Everything in this method is
            // best-effort by the same argument; there was no reason for one step of it to be an
            // exception.
            GroundTruthWindow[] windows;
            lock (_windowsGate) { windows = _windows.ToArray(); }

            var session = new GroundTruthSession(
                GroundTruthSession.CurrentSchemaVersion,
                _providerType,
                _screenSize.X,
                _screenSize.Y,
                _settleWindowMs,
                windows)
            {
                Outcome = _outcome,
                Mode = _mode,
            };

            var directory = Path.GetDirectoryName(_writePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Atomic, like every other file this app writes: a torn sidecar would be read as a
            // ground truth it is not, which is worse than having none at all.
            var tmp = _writePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOptions));
            File.Move(tmp, _writePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // A failed sidecar costs the run's ground truth, never the calibration it rode along with.
            ReportFault(ex);
        }
    }

    /// <summary>Best-effort fault report: the sink's owner is told, and nothing else is disturbed.</summary>
    private void ReportFault(Exception ex)
    {
        try { _onFault?.Invoke(ex); } catch { /* nothing left to report to */ }
    }

    /// <inheritdoc />
    public void AbandonSession()
    {
        // A cancelled run has no ground truth worth keeping: the user stopped partway, so the windows
        // gathered describe targets they may never have looked at. Discard the open one FIRST — closing
        // it would append it to the list this is about to clear, leaving exactly the stale window the
        // abandonment exists to drop.
        DiscardOpenWindow();
        lock (_windowsGate) { _windows.Clear(); }
        IsRecording = false;
    }

    private void CloseOpenWindow()
    {
        if (_openTargetId >= 0 && _openSamples > 0)
        {
            var window = new GroundTruthWindow(
                _openTargetId, _openTarget.X, _openTarget.Y,
                _openFromUs, _openArrivedUs, _openLastUs, _openSamples)
            {
                Samples = SnapshotOpenSamples(_openSampleList),
            };
            lock (_windowsGate) { _windows.Add(window); }
        }
        DiscardOpenWindow();
    }

    private void DiscardOpenWindow()
    {
        _openTargetId = -1;
        _openSamples = 0;
        _openArrivedUs = null;
        _openSampleList.Clear();
    }
}
