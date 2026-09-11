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
/// Threading: <see cref="OnSample"/> is called on the gaze thread; the writes happen on whichever
/// thread ends the session. Sample accumulation touches only the open window, and the session ends
/// only after the gaze subscription is gone, so the two never overlap.
/// </para>
/// </summary>
public sealed class ScriptedGroundTruthRecorder : IGroundTruthSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly Action<Exception>? _onFault;

    private readonly List<GroundTruthWindow> _windows = new();
    private string _providerType = string.Empty;
    private PhysicalPoint _screenSize;
    private int _settleWindowMs;

    // The window currently open. Held as fields rather than a mutable struct in the list so the
    // gaze-thread update is a handful of field writes with no copying.
    private int _openTargetId = -1;
    private PhysicalPoint _openTarget;
    private long _openFromUs;
    private long? _openArrivedUs;
    private long _openLastUs;
    private int _openSamples;

    /// <summary>Initializes the recorder.</summary>
    /// <param name="filePath">Full path of the sidecar to write. Its directory is created if absent.</param>
    /// <param name="onFault">Optional one-shot report if the sidecar cannot be written.</param>
    public ScriptedGroundTruthRecorder(string filePath, Action<Exception>? onFault = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A sidecar path is required.", nameof(filePath));
        _filePath = filePath;
        _onFault = onFault;
    }

    /// <summary>Windows closed so far this session, in presentation order.</summary>
    public IReadOnlyList<GroundTruthWindow> Windows => _windows;

    /// <summary>Whether a session is currently being recorded.</summary>
    public bool IsRecording { get; private set; }

    /// <inheritdoc />
    public void BeginSession(string providerType, PhysicalPoint screenSize, int settleWindowMs)
    {
        // Discard BEFORE clearing, not after: closing an open window appends it, so the other order
        // would resurrect a previous session's last target into the freshly cleared list.
        DiscardOpenWindow();
        _windows.Clear();
        _providerType = providerType;
        _screenSize = screenSize;
        _settleWindowMs = settleWindowMs;
        IsRecording = true;
    }

    /// <inheritdoc />
    public void OnSample(int targetId, PhysicalPoint target, long deviceTimestampUs, bool hasArrived)
    {
        if (!IsRecording) return;

        if (targetId != _openTargetId)
        {
            CloseOpenWindow();
            _openTargetId = targetId;
            _openTarget = target;
            _openFromUs = deviceTimestampUs;
            _openArrivedUs = null;
            _openSamples = 0;
        }

        // First sample at which the collector reports arrival. Latched, not overwritten: arrival is
        // the moment the fixation being measured starts, and a later sample cannot un-start it.
        if (hasArrived && _openArrivedUs is null) _openArrivedUs = deviceTimestampUs;

        _openLastUs = deviceTimestampUs;
        _openSamples++;
    }

    /// <inheritdoc />
    public void CompleteSession()
    {
        if (!IsRecording) return;
        CloseOpenWindow();
        IsRecording = false;

        var session = new GroundTruthSession(
            GroundTruthSession.CurrentSchemaVersion,
            _providerType,
            _screenSize.X,
            _screenSize.Y,
            _settleWindowMs,
            _windows.ToArray());

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Atomic, like every other file this app writes: a torn sidecar would be read as a
            // ground truth it is not, which is worse than having none at all.
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOptions));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // A failed sidecar costs the run's ground truth, never the calibration it rode along with.
            try { _onFault?.Invoke(ex); } catch { /* nothing left to report to */ }
        }
    }

    /// <inheritdoc />
    public void AbandonSession()
    {
        // A cancelled run has no ground truth worth keeping: the user stopped partway, so the windows
        // gathered describe targets they may never have looked at. Discard the open one FIRST — closing
        // it would append it to the list this is about to clear, leaving exactly the stale window the
        // abandonment exists to drop.
        DiscardOpenWindow();
        _windows.Clear();
        IsRecording = false;
    }

    private void CloseOpenWindow()
    {
        if (_openTargetId >= 0 && _openSamples > 0)
        {
            _windows.Add(new GroundTruthWindow(
                _openTargetId, _openTarget.X, _openTarget.Y,
                _openFromUs, _openArrivedUs, _openLastUs, _openSamples));
        }
        DiscardOpenWindow();
    }

    private void DiscardOpenWindow()
    {
        _openTargetId = -1;
        _openSamples = 0;
        _openArrivedUs = null;
    }
}
