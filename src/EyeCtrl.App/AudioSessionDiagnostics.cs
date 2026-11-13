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
    private static int _failureLogged;
    private static int _failureReportCount;
    // R4 (review 02): the shutdown latch. After Shutdown, queued or late work must not
    // re-register or log — the process is exiting and App.OnExit has already torn the
    // listeners down.
    private static int _shutDown;

    /// <summary>Test seam (fixup 03 S1 verification): true once a diagnostics failure has been
    /// reported. One failure is reported for the app's lifetime; the latch keeps it at one
    /// (review 02's fail-once rule).</summary>
    internal static bool FailureLogged => Volatile.Read(ref _failureLogged) == 1;

    /// <summary>Test seam (review 05 V1): how many failure LINES were actually emitted. The
    /// fail-once latch must keep this at 1 per app lifetime — a bool cannot detect a second
    /// report (mutant M10), a count can.</summary>
    internal static int FailureReportCount => Volatile.Read(ref _failureReportCount);
    /// <summary>
    /// Test seam for WHERE the Core Audio work runs. Production (null) queues onto a background
    /// MTA thread-pool thread — review 01 F4: cold COM costs ~105 ms, and no Core Audio call may
    /// ever run synchronously inside a key fire or a window transition. Tests install an inline
    /// runner for determinism.
    /// </summary>
    internal static Action<Action>? WorkScheduler { get; set; }

    /// <summary>Test wiring; production uses the Core Audio implementations (CoreAudioInterop).
    /// Review 04 T4: DRAINS first and REFUSES to reconfigure while queued diagnostics work is in
    /// flight — silently zeroing the counters under parked items resurrected the fixup-01 flake
    /// family and made later drains time out. Tests run their parked work first (the collection
    /// base class does it in Dispose), or this throws — loud, per R6.</summary>
    internal static void ConfigureForTesting(IAudioStateReader? reader, IAudioChangeListener? listener)
    {
        if (!DrainQueuedWork())
            throw new InvalidOperationException(
                "queued F5 diagnostics work did not drain within 5 s; run the parked work before reconfiguring");
        lock (Gate)
        {
            _reader = reader;
            _listener = listener;
            _changeLine = null;
            _firstClickDone = 0;
            _endpointEventsAttached = false;
            _sessionEventsAttached = false;
            _failureLogged = 0;
            _failureReportCount = 0;
            _shutDown = 0;
            _queuedWorkScheduled = 0;
            _queuedWorkCompleted = 0;
        }
    }

    /// <summary>Production composition (KeyboardCoordinator.LoadKeyboard): restore the Core Audio
    /// pair if tests (or a future dispose path) left it unset. Idempotent and cheap. LOCK-FREE on
    /// purpose (review 02 R3): this runs on the UI thread at every settings apply, so it must
    /// never wait on a lock the diagnostics worker may hold across a COM call.
    /// Fixup 03 S1: it constructs nothing when the pair is already wired, and construction itself
    /// acquires no COM — the observer's MMDeviceEnumerator (CoCreate ~44 ms cold) is acquired
    /// lazily on the diagnostics worker, inside its _comLock. This must never touch or wait on
    /// the audio stack: the owner would have no gaze control while it ran.</summary>
    public static void EnsureProductionWiring()
    {
        // The guards keep the Interlocked version from constructing a throwaway observer/reader
        // on every settings apply (review 03 S1).
        if (Volatile.Read(ref _listener) is null)
            Interlocked.CompareExchange(ref _listener, new CoreAudioInterop.CoreAudioChangeObserver(), null);
        if (Volatile.Read(ref _reader) is null)
            Interlocked.CompareExchange(ref _reader, new CoreAudioInterop.CoreAudioStateReader(), null);
    }

    /// <summary>Test accessor for the composition pin: true iff the REAL Core Audio pair is wired.</summary>
    internal static bool ProductionWired =>
        _reader is CoreAudioInterop.CoreAudioStateReader && _listener is CoreAudioInterop.CoreAudioChangeObserver;

    /// <summary>The queued unit of the first click: one snapshot, then event registration.
    /// The latch is re-checked at execution time: work queued before App.OnExit's shutdown must
    /// not register or log afterwards (review 02 R4).</summary>
    private static void FirstClickWork()
    {
        if (Volatile.Read(ref _shutDown) == 1) return;
        LogSnapshot();
        TryRegisterChangeEvents();
    }

    /// <summary>Every click funnels here; the once-guard keeps the synchronous cost to one
    /// Interlocked read — the COM work itself is queued off the typing path (review 01 F4:
    /// a cold first read measures ~105 ms and must never delay a key).</summary>
    public static void OnClickAttempted()
    {
        if (Volatile.Read(ref _shutDown) == 1) return;
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
        if (Volatile.Read(ref _shutDown) == 1) return;
        if (Volatile.Read(ref _firstClickDone) == 0) return;
        RunOffTypingPath(TransitionWork);
    }

    /// <summary>The queued unit of a transition: baseline snapshot + registration retry, with
    /// the same late-work latch as <see cref="FirstClickWork"/>.</summary>
    private static void TransitionWork()
    {
        if (Volatile.Read(ref _shutDown) == 1) return;
        LogSnapshot();
        if (_endpointEventsAttached && _sessionEventsAttached) return;
        TryRegisterChangeEvents();
    }

    /// <summary>Bounded wait until every scheduled diagnostics work item has completed. Tests
    /// that queue (the production default scheduler) MUST drain in their finally: a pool-thread
    /// work item that lands inside a LATER test runs against that test's statics — observed as
    /// production snapshot lines appearing in another test's sink (fixup 01 flake). Returns
    /// false on timeout (review 02 R6) so the caller can fail loudly instead of silently
    /// resetting the statics under an in-flight item.</summary>
    internal static bool DrainQueuedWork(int timeoutMs = 5000)
    {
        var scheduled = Volatile.Read(ref _queuedWorkScheduled);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _queuedWorkCompleted) < scheduled && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
        return Volatile.Read(ref _queuedWorkCompleted) >= scheduled;
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
        if (Volatile.Read(ref _shutDown) == 1) return;
        try
        {
            var listener = _listener;
            if (listener is null) return;
            _changeLine ??= Emit;
            // R3 (review 02): the COM calls run OUTSIDE Gate — the UI thread takes Gate in
            // EnsureProductionWiring/Shutdown and must never wait behind a hung AudioSrv RPC.
            // The attached flags are read racily and the winners published under the lock; a
            // duplicate registration attempt is impossible because the listener's own state
            // guard is idempotent.
            var endpointWasAttached = _endpointEventsAttached;
            var sessionWasAttached = _sessionEventsAttached;
            var endpointAttached = endpointWasAttached || listener.TryRegister(_changeLine);
            var sessionAttached = endpointAttached &&
                (sessionWasAttached || listener.TryRegisterSessionEvents());
            lock (Gate)
            {
                _endpointEventsAttached = endpointAttached;
                _sessionEventsAttached = sessionAttached;
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

    private static void Emit(string line) => Log.Information("{Line}", line);

    /// <summary>A failure of the diagnostics layer costs ONE line for the app's lifetime; after
    /// that the degraded instruments (the show/hide snapshots) run silently. Information level:
    /// this is a diagnostics note, never a typing-path event.</summary>
    private static void ReportFailure(string what, Exception? ex)
    {
        // Interlocked, not a lock (review 02 R3): this can run on the worker thread while the UI
        // thread wants Gate; a rare duplicate failure line would be harmless, the latch keeps it
        // to one in practice.
        if (Interlocked.Exchange(ref _failureLogged, 1) == 1) return;
        var detail = ex is null ? "failed" : $"failed ({ex.GetType().Name}: {ex.Message})";
        Emit($"Keyboard audio diagnostics: {what} {detail}; staying silent about further failures.");
        Interlocked.Increment(ref _failureReportCount); // review 05 V1: the assertion counts LINES
    }

    /// <summary>App teardown (App.OnExit): latch the diagnostics off FIRST (no queued or late
    /// work may register or log afterwards — review 02 R4), then hand the COM teardown to the
    /// pool. SIGNAL-AND-RETURN (review 02 R3): this runs on the UI thread and must never wait on
    /// an in-flight or hung COM call — the process exit cleans up whatever is still running.</summary>
    public static void Shutdown()
    {
        Volatile.Write(ref _shutDown, 1);
        var listener = _listener;
        if (listener is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                listener.Shutdown();
            }
            catch
            {
                // Teardown: never let COM ruin the exit path.
            }
        });
    }
}
