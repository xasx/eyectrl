namespace EyeCtrl.App;

using System.Globalization;
using Serilog;

/// <summary>
/// F5 diagnostics: what Windows did with EyeCtrl's audio. Before this ticket the click code
/// could not observe its own silence — SoundPlayer discarded winmm's verdict and nothing read
/// the Core Audio state — so a per-app mute or endpoint problem (Phase 1's surviving
/// hypotheses) left no trace anywhere.
/// <para>
/// Three instruments, every one guarded so none can throw into the typing path:
/// (1) ONE line at the first click after app start — the default render endpoint's friendly
/// name and EyeCtrl's own audio session state (volume, mute, session state);
/// (2) one line per CHANGE, via IAudioSessionEvents (volume/mute, session state, session
/// disconnected) and IMMNotificationClient (default device changed) — registered at that first
/// click and retried at every later real show/hide, because winmm creates the session lazily
/// and it may not exist yet at the first click;
/// (3) a snapshot line at every real show/hide after the first click. Deliberately kept in
/// BOTH modes (not only when registration fails): the events are the unproven half — a COM
/// callable wrapper on this machine — and the incident happened mid-show, so the boundary
/// states are the cheap insurance; the cost is two lines per keyboard session.
/// Any failure of the Core Audio layer logs once and degrades to instrument (3) only.
/// </para>
/// <para>
/// Static on purpose: KeyboardSoundService instances are recreated by every settings apply,
/// but the audio session — and therefore the first-click guard and the registration — are app
/// state shared by all of them. Lines are content-free (device names and audio state only);
/// no E1 content ever passes through here.
/// </para>
/// </summary>
internal static class AudioSessionDiagnostics
{
    public sealed record AudioStateSnapshot(
        string? EndpointName, bool SessionPresent, double? Volume, bool? Mute, string? SessionState)
    {
        /// <summary>The snapshot line's exact format — pinned by a test, culture-invariant.</summary>
        public string FormatLine()
        {
            var endpoint = EndpointName is null ? "<unavailable>" : $"\"{EndpointName}\"";
            if (!SessionPresent) return $"Keyboard audio: endpoint={endpoint} session: not created yet";
            var volume = Volume?.ToString("F2", CultureInfo.InvariantCulture) ?? "?";
            return $"Keyboard audio: endpoint={endpoint} session: volume={volume} mute={Mute?.ToString() ?? "?"} state={SessionState ?? "?"}";
        }
    }

    // ---- change-event lines: pure formatters, pinned by tests; the COM callbacks call these ----

    internal static string FormatVolumeChange(float newVolume, bool newMute) =>
        $"Keyboard audio change: volume={newVolume.ToString("F2", CultureInfo.InvariantCulture)} mute={newMute}";

    internal static string FormatSessionStateChange(string newState) =>
        $"Keyboard audio change: session state={newState}";

    internal static string FormatSessionDisconnected(string reason) =>
        $"Keyboard audio change: session disconnected: {reason}";

    internal static string FormatDefaultDeviceChanged(string endpointName) =>
        $"Keyboard audio change: default device -> \"{endpointName}\"";

    /// <summary>Read seam — production is the Core Audio COM walk; tests fake it.</summary>
    internal interface IAudioStateReader
    {
        /// <summary>Null means the state could not be read this time (never throws upward).</summary>
        AudioStateSnapshot? Read();
    }

    /// <summary>
    /// Change-event seam. TryRegister attaches the endpoint listener (default device changes)
    /// and returns whether it attached; TryRegisterSessionEvents attaches the session events
    /// once our session exists (callers retry until it returns true); Shutdown unregisters in
    /// reverse order. Change lines arrive on OS threads through the callback — the callback is
    /// Serilog (thread-safe) or the test sink.
    /// </summary>
    internal interface IAudioChangeListener
    {
        bool TryRegister(Action<string> onChangeLine);
        bool TryRegisterSessionEvents();
        void Shutdown();
    }

    private static readonly object Gate = new();
    // Production wiring (review 01 F1): the Core Audio pair is the DEFAULT — constructed when
    // this type is first touched and re-installable via EnsureProductionWiring. Tests replace
    // both through ConfigureForTesting; the KeyboardCoordinator calls EnsureProductionWiring
    // where it builds the sound service, and a composition test pins that call.
    private static IAudioStateReader? _reader = new CoreAudioInterop.CoreAudioStateReader();
    private static IAudioChangeListener? _listener = new CoreAudioInterop.CoreAudioChangeObserver();
    private static Action<string>? _changeLine;
    private static int _firstClickDone;
    private static bool _endpointEventsAttached, _sessionEventsAttached;
    private static bool _failureLogged;

    /// <summary>Test seam: every diagnostics line (snapshots and change events) is captured here
    /// instead of Serilog when set.</summary>
    internal static Action<string>? LineSink { get; set; }

    /// <summary>
    /// Test seam for WHERE the Core Audio work runs. Production (null) queues onto a background
    /// MTA thread-pool thread — review 01 F4: cold COM costs ~105 ms, and no Core Audio call may
    /// ever run synchronously inside a key fire or a window transition. Tests install an inline
    /// runner for determinism.
    /// </summary>
    internal static Action<Action>? WorkScheduler { get; set; }

    /// <summary>Test wiring; production uses the Core Audio implementations (CoreAudioInterop).</summary>
    internal static void ConfigureForTesting(IAudioStateReader? reader, IAudioChangeListener? listener)
    {
        lock (Gate)
        {
            _reader = reader;
            _listener = listener;
            _changeLine = null;
            _firstClickDone = 0;
            _endpointEventsAttached = false;
            _sessionEventsAttached = false;
            _failureLogged = false;
        }
    }

    /// <summary>Production composition (KeyboardCoordinator.LoadKeyboard): restore the Core Audio
    /// pair if tests (or a future dispose path) left it unset. Idempotent, cheap, no COM here —
    /// constructing the reader/observer does not touch WASAPI.</summary>
    public static void EnsureProductionWiring()
    {
        lock (Gate)
        {
            _reader ??= new CoreAudioInterop.CoreAudioStateReader();
            _listener ??= new CoreAudioInterop.CoreAudioChangeObserver();
        }
    }

    /// <summary>Test accessor for the composition pin: true iff the REAL Core Audio pair is wired.</summary>
    internal static bool ProductionWired =>
        _reader is CoreAudioInterop.CoreAudioStateReader && _listener is CoreAudioInterop.CoreAudioChangeObserver;

    /// <summary>The queued unit of the first click: one snapshot, then event registration.</summary>
    private static void FirstClickWork()
    {
        LogSnapshot();
        TryRegisterChangeEvents();
    }

    /// <summary>Every click funnels here; the once-guard keeps the synchronous cost to one
    /// Interlocked read — the COM work itself is queued off the typing path (review 01 F4:
    /// a cold first read measures ~105 ms and must never delay a key).</summary>
    public static void OnClickAttempted()
    {
        if (Interlocked.CompareExchange(ref _firstClickDone, 1, 0) != 0) return;
        RunOffTypingPath(FirstClickWork);
    }

    /// <summary>
    /// A real keyboard show/hide after the first click: a baseline snapshot (boundary state of
    /// the typing session) plus a retry of any registration that is still missing. A no-op
    /// before the first click — nothing to baseline yet. The work is queued off the typing path,
    /// exactly like the first-click work.
    /// </summary>
    public static void OnKeyboardTransition()
    {
        if (Volatile.Read(ref _firstClickDone) == 0) return;
        RunOffTypingPath(() =>
        {
            LogSnapshot();
            lock (Gate)
            {
                if (_endpointEventsAttached && _sessionEventsAttached) return;
            }
            TryRegisterChangeEvents();
        });
    }

    /// <summary>Production runs the diagnostics work on an MTA thread-pool thread (COM is happy
    /// there, the UI thread never waits); the test scheduler keeps the seam synchronous. Both
    /// counters let tests drain the queue before they reset the statics — a pool-thread work
    /// item that lands inside a later test would bump THAT test's reader (review 01 fixup:
    /// observed as a full-suite-only flake in Transitions_BeforeAnyClick_DoNothing).</summary>
    internal static int QueuedWorkScheduled => Volatile.Read(ref _queuedWorkScheduled);
    internal static int QueuedWorkCompleted => Volatile.Read(ref _queuedWorkCompleted);
    private static int _queuedWorkScheduled, _queuedWorkCompleted;

    private static void RunOffTypingPath(Action work)
    {
        void Run()
        {
            try
            {
                work();
            }
            catch
            {
                // The queued work is fully guarded internally; this is the last-resort net so a
                // thread-pool thread can never surface an unhandled exception into the process.
            }
            Interlocked.Increment(ref _queuedWorkCompleted);
        }

        Interlocked.Increment(ref _queuedWorkScheduled);
        var scheduler = WorkScheduler;
        if (scheduler is not null)
        {
            scheduler(Run);
            return;
        }
        ThreadPool.QueueUserWorkItem(_ => Run());
    }

    private static void TryRegisterChangeEvents()
    {
        try
        {
            var listener = _listener;
            if (listener is null) return;
            lock (Gate)
            {
                _changeLine ??= Emit;
                if (!_endpointEventsAttached) _endpointEventsAttached = listener.TryRegister(_changeLine);
                if (_endpointEventsAttached && !_sessionEventsAttached) _sessionEventsAttached = listener.TryRegisterSessionEvents();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("change-event registration", ex);
        }
    }

    private static void LogSnapshot()
    {
        try
        {
            var reader = _reader;
            if (reader is null)
            {
                Emit("Keyboard audio: diagnostics not configured (no reader wired)");
                return;
            }
            Emit(reader.Read()?.FormatLine() ?? "Keyboard audio: state unavailable (Core Audio read failed)");
        }
        catch (Exception ex)
        {
            ReportFailure("state read", ex);
        }
    }

    private static void Emit(string line)
    {
        var sink = LineSink;
        if (sink is not null) sink(line);
        else Log.Information("{Line}", line);
    }

    /// <summary>A failure of the diagnostics layer costs ONE line for the app's lifetime; after
    /// that the degraded instruments (the show/hide snapshots) run silently. Information level:
    /// this is a diagnostics note, never a typing-path event.</summary>
    private static void ReportFailure(string what, Exception? ex)
    {
        lock (Gate)
        {
            if (_failureLogged) return;
            _failureLogged = true;
        }
        var detail = ex is null ? "failed" : $"failed ({ex.GetType().Name}: {ex.Message})";
        Emit($"Keyboard audio diagnostics: {what} {detail}; staying silent about further failures.");
    }

    /// <summary>App teardown (App.OnExit): unregister the COM listeners while the process is
    /// still alive rather than letting WASAPI call into a dying CCW. Best-effort.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _listener?.Shutdown(); }
            catch
            {
                // Teardown: never let COM ruin the exit path.
            }
            _endpointEventsAttached = false;
            _sessionEventsAttached = false;
        }
    }
}
