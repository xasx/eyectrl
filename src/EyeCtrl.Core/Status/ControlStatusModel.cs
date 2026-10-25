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
/// <b>State-exit debounce (300 ms).</b> The ticket's "debounce state exits by about 300 ms" is
/// applied where a state can exit on a transient condition: LEAVING Paused for Normal requires the
/// unpaused condition to hold for 300 ms, so a pause/unpause/repause sequence cannot flicker the
/// strip. Entries are never delayed (a pause shows at once), and an exit to a MORE URGENT state is
/// never delayed either (priority beats the debounce). <see cref="ControlState.TrackingLost"/> is
/// deliberately exempt on its way back to Normal: the ticket's explicit rule — "it clears on the
/// first valid sample" — wins, and the 3 s entry threshold is already a far stronger debounce than
/// 300 ms.
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
    /// How long the unpaused condition must hold before the state may leave
    /// <see cref="ControlState.Paused"/> for <see cref="ControlState.Normal"/> — the anti-flicker
    /// debounce the ticket calls "debounce state exits by about 300 ms".
    /// </summary>
    public const int StateExitDebounceMs = 300;

    private readonly TimeProvider _time;
    private readonly bool _eyeTrackerProvider;

    // Two locks, never nested in the opposite order: _gate owns the status, _notifyGate serializes
    // handler delivery so a late publisher cannot deliver an older status after a newer one.
    private readonly object _gate = new();
    private readonly object _notifyGate = new();

    private long _lastValidSampleTimestamp;
    private bool _paused;
    private long? _disconnectedSince;
    private long? _pausedExitSince;
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
            _pausedExitSince = null;
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
    /// </summary>
    /// <param name="isConnected">The provider's <c>IGazeProvider.IsConnected</c> value right now.</param>
    public void Poll(bool isConnected)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
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

        ControlState desired = DesiredState(now);
        if (desired == _state)
        {
            _pausedExitSince = null;
        }
        else if (desired == ControlState.Normal && _state == ControlState.Paused)
        {
            // The one transient exit: resuming. Hold Paused until "not paused" has been true for
            // the debounce window, so a rapid pause/unpause/repause does not blink the strip.
            _pausedExitSince ??= now;
            if (_time.GetElapsedTime(_pausedExitSince.Value, now)
                >= TimeSpan.FromMilliseconds(StateExitDebounceMs))
            {
                _state = desired;
                _pausedExitSince = null;
            }
        }
        else
        {
            // Entries, priority promotions (Paused -> Lost) and the explicit Lost-clear-on-the-first-
            // valid-sample rule are all immediate.
            _state = desired;
            _pausedExitSince = null;
        }

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
