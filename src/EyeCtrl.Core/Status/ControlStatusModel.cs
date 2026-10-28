namespace EyeCtrl.Core.Status;

using EyeCtrl.Core.Enums;

/// <summary>
/// The headless control-status state machine (Wave B ticket B1): one source of truth for "do you
/// have control?", driven by three raw signals — valid gaze samples, the provider's
/// <c>IsConnected</c>, and the interaction state machine's paused flag.
/// <para>
/// <b>Priority</b> (when several conditions hold): NoTracker &gt; TrackingLost &gt; Paused &gt;
/// Normal. <see cref="ControlState.Blocked"/> is reserved and no path here raises it.
/// </para>
/// <para>
/// <b>TrackingLost</b> is entered when the last VALID gaze sample is at least 3 s old (an eye tracker
/// is the provider). It clears on the first valid sample — the 3 s of no samples is itself the
/// debounce, so a returning sample is allowed to clear it immediately (the 09-24 storm's longest
/// gap was 0.53 s, far below the threshold).
/// </para>
/// <para>
/// <b>Disconnected</b> is a separate flag, not a state: it turns true only after
/// <c>IsConnected</c> has been observed false continuously for more than 10 s. A single connected
/// observation resets the clock, so the storm's 0.1 s connected windows keep it false forever.
/// </para>
/// <para>
/// <b>No exit debounce (B1 fixup-01 / review F2).</b> The original ticket's "debounce state exits
/// by about 300 ms" was implemented on leaving Paused and then deleted by the coordinator: Pause is
/// a deliberate edge, the walk-away storm never touches Paused, and the extra 300–400 ms was pure
/// lag on the strip. The 3 s Lost-gaze entry threshold IS the state debounce (and the 10 s
/// continuous bound is the flag's). Every state transition after its condition is therefore
/// immediate. B2a/B2b's paused strip hides on the interaction state machine's
/// <c>InteractionManager.CurrentState</c>, never on this mirrored status — see
/// <see cref="ControlStatus.State"/>.
/// </para>
/// <para>
/// Thread safety: samples arrive on the gaze thread, pause transitions on the UI thread and the
/// connectivity poll on a timer thread, so every entry point takes one private lock. Handlers run
/// outside that lock, on the calling thread, in commit order, with coalescing: a handler that runs
/// late sees the newest status, never a stale one. Time comes from an injected
/// <see cref="TimeProvider"/> so tests can drive a fake clock.
/// </para>
/// </summary>
public sealed class ControlStatusModel
{
    /// <summary>
    /// How long no valid gaze sample may last, while an eye tracker is the provider, before the state
    /// becomes <see cref="ControlState.TrackingLost"/> (3 s).
    /// </summary>
    public const int TrackingLostAfterMs = 3_000;

    /// <summary>
    /// How long <c>IGazeProvider.IsConnected</c> must be false CONTINUOUSLY before
    /// <see cref="ControlStatus.Disconnected"/> turns true (more than 10 s).
    /// </summary>
    public const int DisconnectAfterMs = 10_000;

    /// <summary>
    /// The longest gap between two connectivity polls that still counts as observation. If more than
    /// this elapses (timer starvation, suspend/resume), the interval in between was never observed,
    /// so it is NOT evidence of a continuous disconnect and the false-since clock restarts at the new
    /// poll (B1 review 01 F3).
    /// </summary>
    public const int MaxPollGapMs = 1_000;

    private readonly TimeProvider _time;
    private readonly bool _eyeTrackerProvider;

    // Two locks, never nested in the opposite order: _gate owns the status, _notifyGate serializes
    // handler delivery so a late publisher cannot deliver an older status after a newer one.
    private readonly object _gate = new();
    private readonly object _notifyGate = new();

    private long _lastValidSampleTimestamp;
    private bool _paused;
    private long? _disconnectedSince;
    private long? _lastPollTimestamp;
    private long _lastConnectEdgeCount;
    private ControlState _state;
    private bool _disconnected;
    private ControlStatus _published;
    private ControlStatus _lastNotified;

    /// <summary>
    /// Creates the model in its initial state. Nothing is timed or observed until
    /// <see cref="Start"/> is called, so a slow app boot cannot age the lost-gaze clock before the
    /// provider is actually streaming.
    /// </summary>
    /// <param name="providerType">
    /// The RESOLVED provider type (the app passes <c>GazeProviderFactory.GetProviderType</c>'s answer,
    /// which unwraps decorators). <see cref="GazeProviderType.Mouse"/> is the no-tracker case; every
    /// other value is treated as an eye tracker.
    /// </param>
    /// <param name="timeProvider">The clock. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="initiallyPaused">
    /// The interaction state at construction — <c>true</c> for a <c>--paused</c>/<c>StartPaused</c>
    /// launch, so the first published status is already Paused.
    /// </param>
    public ControlStatusModel(
        GazeProviderType providerType,
        TimeProvider? timeProvider = null,
        bool initiallyPaused = false)
    {
        _time = timeProvider ?? TimeProvider.System;
        _eyeTrackerProvider = providerType != GazeProviderType.Mouse;
        _paused = initiallyPaused;

        long now = _time.GetTimestamp();
        _lastValidSampleTimestamp = now;
        _state = InitialState();
        _published = new ControlStatus(_state, Disconnected: false);
        _lastNotified = _published;
    }

    /// <summary>The current status. Safe to read from any thread.</summary>
    public ControlStatus Current
    {
        get
        {
            lock (_gate)
                return _published;
        }
    }

    /// <summary>
    /// Raised when <see cref="Current"/> changes. The payload is the committed status; handlers run
    /// on the thread that caused the change (the running service marshals them to the UI thread) and
    /// must stay cheap.
    /// </summary>
    public event Action<ControlStatus>? StatusChanged;

    /// <summary>
    /// Seeds the clocks for the running session. Call once, when the gaze stream starts (not when
    /// the model is constructed): the 3 s lost-gaze window must begin with the stream, or a slow
    /// splash/boot would report tracking lost before the tracker ever had a chance to deliver.
    /// The initial state is published silently — read it with <see cref="Current"/>.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            _lastValidSampleTimestamp = now;
            _disconnectedSince = null;
            _lastPollTimestamp = null;
            _lastConnectEdgeCount = 0;
            _disconnected = false;
            _state = InitialState();
            _published = new ControlStatus(_state, Disconnected: false);
            _lastNotified = _published;
        }
    }

    /// <summary>
    /// Reports one gaze sample. Only samples with <c>IsValid</c> count — a tracker-held (blink or
    /// eyes-lost) sample is explicitly NOT evidence, so callers must pass validity through rather
    /// than call this for every frame.
    /// </summary>
    /// <param name="isValid">The sample's <c>GazePoint.IsValid</c> flag.</param>
    public void OnGazeSample(bool isValid)
    {
        if (!isValid)
            return;

        lock (_gate)
        {
            _lastValidSampleTimestamp = _time.GetTimestamp();
            RecomputeLocked();
        }
        PublishIfChanged();
    }

    /// <summary>
    /// Reports the interaction state machine's paused flag (<c>CurrentState == Paused</c>).
    /// </summary>
    /// <param name="paused">Whether the user is paused.</param>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            RecomputeLocked();
        }
        PublishIfChanged();
    }

    /// <summary>
    /// The periodic tick: pass the provider's live <c>IsConnected</c> value. Call it on a regular
    /// cadence (the service polls at 10 Hz); this is what detects both the 3 s lost-gaze threshold
    /// and the continuous 10 s disconnect.
    /// <para>
    /// An interval longer than <see cref="MaxPollGapMs"/> is treated as unobserved: the false-since
    /// clock restarts at this poll, because two false observations either side of a sleep are not
    /// evidence that the tracker was down the whole time (review 01 F3).
    /// </para>
    /// </summary>
    /// <param name="isConnected">The provider's <c>IGazeProvider.IsConnected</c> value right now.</param>
    /// <param name="connectEdgeCount">
    /// The provider's <c>IGazeProvider.ConnectEdgeCount</c> at the same moment (B1 review 01 F4 /
    /// B3). A count that differs from the previous poll means a connect edge happened BETWEEN the
    /// two polls, so the interval cannot have been continuously disconnected. Optional (default 0)
    /// so providers and tests without the counter keep the pre-hardening semantics.
    /// </param>
    public void Poll(bool isConnected, long connectEdgeCount = 0)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();

            if (_lastPollTimestamp is { } previous
                && _time.GetElapsedTime(previous, now) > TimeSpan.FromMilliseconds(MaxPollGapMs))
            {
                _disconnectedSince = null;
            }
            _lastPollTimestamp = now;

            // A connect edge between two polls means the interval was NOT continuously disconnected,
            // even though both snapshots read false (B1 review 01 F4: a synthetic 100 ms window
            // aliases a fixed 10 Hz poll). The counter only resets — an edge never starts the clock.
            if (connectEdgeCount != _lastConnectEdgeCount)
            {
                _lastConnectEdgeCount = connectEdgeCount;
                _disconnectedSince = null;
            }

            if (isConnected)
            {
                _disconnectedSince = null;
            }
            else if (_disconnectedSince is null)
            {
                _disconnectedSince = now;
            }

            RecomputeLocked();
        }
        PublishIfChanged();
    }

    private ControlState InitialState() =>
        !_eyeTrackerProvider ? ControlState.NoTracker
        : _paused ? ControlState.Paused
        : ControlState.Normal;

    private void RecomputeLocked()
    {
        long now = _time.GetTimestamp();

        _disconnected = _disconnectedSince is { } since
            && _time.GetElapsedTime(since, now) > TimeSpan.FromMilliseconds(DisconnectAfterMs);

        // No exit debounce (review 01 F2): every transition is immediate once its condition holds.
        // The 3 s Lost threshold and the 10 s continuous-disconnect bound are the debounces.
        _state = DesiredState(now);

        _published = new ControlStatus(_state, _disconnected);
    }

    private ControlState DesiredState(long now)
    {
        if (!_eyeTrackerProvider)
            return ControlState.NoTracker;

        if (_time.GetElapsedTime(_lastValidSampleTimestamp, now) >= TimeSpan.FromMilliseconds(TrackingLostAfterMs))
            return ControlState.TrackingLost;

        return _paused ? ControlState.Paused : ControlState.Normal;
    }

    /// <summary>
    /// Re-raises <see cref="Current"/> through the same ordered gate as a change. The runtime adapter
    /// calls this once when it starts, so its initial publish is serialized with any status change
    /// that raced the event subscription (review 01 F5): a newer change can never be followed by the
    /// older initial value. Always raises once; if a change already published in the race window the
    /// duplicate carries the NEWER value, never a stale one.
    /// </summary>
    public void PublishCurrent()
    {
        lock (_notifyGate)
        {
            ControlStatus status;
            lock (_gate)
            {
                _lastNotified = _published;
                status = _published;
            }
            StatusChanged?.Invoke(status);
        }
    }

    private void PublishIfChanged()
    {
        lock (_notifyGate)
        {
            ControlStatus status;
            lock (_gate)
            {
                if (_published == _lastNotified)
                    return;
                _lastNotified = _published;
                status = _published;
            }
            StatusChanged?.Invoke(status);
        }
    }
}
