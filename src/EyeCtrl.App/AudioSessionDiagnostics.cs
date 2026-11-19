namespace EyeCtrl.App;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Serilog;

/// <summary>
/// F5/F10 diagnostics: what Windows did with EyeCtrl's audio. Before F5 the click code could not
/// observe its own silence — SoundPlayer discarded winmm's verdict and nothing read the Core Audio
/// state — so a per-app mute or endpoint problem (Phase 1's surviving hypotheses) left no trace
/// anywhere.
/// <para>
/// F5's instruments, every one guarded so none can throw into the typing path:
/// (1) ONE line at the first click after app start — the default render endpoint's friendly name
/// and EyeCtrl's own audio session state (volume, mute, session state);
/// (2) one line per CHANGE, via IAudioSessionEvents and IMMNotificationClient — registered at that
/// first click and retried at every later real show/hide, because winmm creates the session lazily
/// and it may not exist yet at the first click;
/// (3) a snapshot line at every real show/hide after the first click.
/// </para>
/// <para>
/// F10 adds the layer BELOW EyeCtrl's session, where the 9th sitting's silence had to live:
/// (4) the render endpoint's master volume and mute, in every snapshot and as a change line via
/// IAudioEndpointVolumeCallback (a per-app session can be healthy while the device is muted);
/// (5) a per-show reached/silent count from the endpoint's IAudioMeterInformation peak during each
/// click's play window — reached means "the ENDPOINT MIX was audible in the window", not "this
/// click was heard", so it is read together with the endpoint volume/mute — plus ONE line at the
/// first silent click of a show with that moment's state;
/// (6) ducking events via IAudioVolumeDuckNotification (NULL session id = all communications
/// ducking), logged with the log line's own timestamp;
/// (7) a per-show late-start count: click requested → session state Active, measured from the
/// state callback's timestamp, so a device-wake delay is distinguishable from a silent click.
/// Any failure of the Core Audio layer logs once and degrades to instrument (3) only.
/// </para>
/// <para>
/// Static on purpose: KeyboardSoundService instances are recreated by every settings apply, but
/// the audio session — and therefore the first-click guard, the registrations and the per-show
/// meter window — are app state shared by all of them. Lines are content-free (device names and
/// audio state only); no E1 content ever passes through here.
/// </para>
/// </summary>
internal static class AudioSessionDiagnostics
{
    public sealed record AudioStateSnapshot(
        string? EndpointName, double? EndpointVolume, bool? EndpointMute,
        bool SessionPresent, double? Volume, bool? Mute, string? SessionState)
    {
        /// <summary>The snapshot line's exact format — pinned by a test, culture-invariant.</summary>
        public string FormatLine() => "Keyboard audio: " + FormatStateDetails(this);
    }

    /// <summary>
    /// The state fields shared by the snapshot line and the first-silent-click line: the endpoint
    /// master volume/mute first (the device's own answer, F10), then EyeCtrl's session. A field
    /// that could not be read renders "?" — never a fabricated 0.
    /// </summary>
    internal static string FormatStateDetails(AudioStateSnapshot? snapshot)
    {
        if (snapshot is null) return "state unavailable";
        var endpoint = snapshot.EndpointName is null ? "<unavailable>" : $"\"{snapshot.EndpointName}\"";
        var endpointVolume = snapshot.EndpointVolume?.ToString("F2", CultureInfo.InvariantCulture) ?? "?";
        var endpointPart = $"endpoint={endpoint} endpoint: volume={endpointVolume} "
            + $"mute={snapshot.EndpointMute?.ToString() ?? "?"}";
        if (!snapshot.SessionPresent) return $"{endpointPart} session: not created yet";
        var volume = snapshot.Volume?.ToString("F2", CultureInfo.InvariantCulture) ?? "?";
        return $"{endpointPart} session: volume={volume} mute={snapshot.Mute?.ToString() ?? "?"} "
            + $"state={snapshot.SessionState ?? "?"}";
    }

    /// <summary>The first-silent-click line (F10): once per show, with the state at that moment.
    /// Deliberately not per click — a silent show would otherwise write one line per key.</summary>
    internal static string FormatSilentClickLine(AudioStateSnapshot? snapshot) =>
        snapshot is null
            ? "Keyboard click silent: no output peak during the play window; audio state unavailable"
            : $"Keyboard click silent: no output peak during the play window; {FormatStateDetails(snapshot)}";

    // ---- change-event lines: pure formatters, pinned by tests; the COM callbacks call these ----

    internal static string FormatVolumeChange(float newVolume, bool newMute) =>
        $"Keyboard audio change: volume={newVolume.ToString("F2", CultureInfo.InvariantCulture)} mute={newMute}";

    internal static string FormatEndpointVolumeChange(float newVolume, bool newMute) =>
        $"Keyboard audio change: endpoint volume={newVolume.ToString("F2", CultureInfo.InvariantCulture)} mute={newMute}";

    internal static string FormatSessionStateChange(string newState) =>
        $"Keyboard audio change: session state={newState}";

    internal static string FormatSessionDisconnected(string reason) =>
        $"Keyboard audio change: session disconnected: {reason}";

    internal static string FormatDefaultDeviceChanged(string endpointName) =>
        $"Keyboard audio change: default device -> \"{endpointName}\"";

    internal static string FormatDuckStart(int communicationsSessions) =>
        $"Keyboard duck: communications ducking started (sessions={communicationsSessions})";

    internal static string FormatDuckEnd() => "Keyboard duck: communications ducking ended";

    /// <summary>The closed vocabulary a COM callback reports; only SessionState carries a state
    /// name (the F10 late-start metric keys on "Active"), the rest are line-only.</summary>
    internal enum AudioChangeKind
    {
        Volume,
        EndpointVolume,
        SessionState,
        SessionDisconnected,
        DefaultDevice,
        DuckStarted,
        DuckEnded,
    }

    internal readonly record struct AudioChange(AudioChangeKind Kind, string Line, string? SessionState = null);

    /// <summary>Read seam — production is the Core Audio COM walk; tests fake it.</summary>
    internal interface IAudioStateReader
    {
        /// <summary>Null means the state could not be read this time (never throws upward).</summary>
        AudioStateSnapshot? Read();
    }

    /// <summary>
    /// Change-event seam. TryRegister attaches every ENDPOINT-level instrument (default-device
    /// changes, the master volume/mute callback, ducking) and returns whether all of them
    /// attached; TryRegisterSessionEvents attaches the session events once our session exists
    /// (callers retry until it returns true); Shutdown unregisters everything. Changes arrive on
    /// OS threads through the callback — the callback is Serilog (thread-safe) or the test sink.
    /// </summary>
    internal interface IAudioChangeListener
    {
        bool TryRegister(Action<AudioChange> onChange);
        bool TryRegisterSessionEvents();
        void Shutdown();
    }

    /// <summary>
    /// Meter seam (F10). Production polls IAudioMeterInformation::GetPeakValue on the render
    /// endpoint until a peak appears, the click's window closes, or <paramref name="stop"/>
    /// says a newer click/show superseded this one. Null = no verdict: the meter could not be
    /// read at all, or the sampler was superseded before it had watched SilentAfterMs (review-02
    /// N1). A supersession after that bound with no peak answers 0 (silent): a silent click never
    /// ends its own window early, so supersession is exactly how it ends in the real failure case.
    /// Tests return a scripted peak instantly (no sleeping, no COM).
    /// </summary>
    internal interface IAudioMeterProbe
    {
        float? SamplePeak(Func<bool> stop);
    }

    private static readonly object Gate = new();
    // Production wiring (review 01 F1): the Core Audio instruments are the DEFAULT — constructed
    // when this type is first touched and re-installable via EnsureProductionWiring. Tests replace
    // all three through ConfigureForTesting; the KeyboardCoordinator calls EnsureProductionWiring
    // where it builds the sound service, and a composition test pins that call.
    private static IAudioStateReader? _reader = new CoreAudioInterop.CoreAudioStateReader();
    private static IAudioChangeListener? _listener = new CoreAudioInterop.CoreAudioChangeObserver();
    private static IAudioMeterProbe? _meter = new CoreAudioInterop.CoreAudioMeterProbe();
    private static Action<AudioChange>? _changeEvent;
    private static int _firstClickDone;
    private static bool _endpointEventsAttached, _sessionEventsAttached;
    private static int _failureLogged;
    private static int _failureReportCount;
    // Review-01 F3: at most one queued session-events retry at a time.
    private static int _sessionRetryScheduled;
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

    /// <summary>Test seam (F10): how many first-silent-click lines were emitted. Reset by
    /// ConfigureForTesting; the per-show latch must add exactly one per show with a silent click.</summary>
    internal static int SilentClickReports => Volatile.Read(ref _silentClickReports);

    /// <summary>
    /// Test seam for WHERE the Core Audio work runs. Production (null) queues onto a background
    /// MTA thread-pool thread — review 01 F4: cold COM costs ~105 ms, and no Core Audio call may
    /// ever run synchronously inside a key fire or a window transition. Tests install an inline
    /// runner for determinism.
    /// </summary>
    internal static Action<Action>? WorkScheduler { get; set; }

    /// <summary>Review-01 F4/R6 test seam: the clock behind the click→Active measurement, so the
    /// stale-stamp guard can be driven without a real 3 s sleep. Production (null) is
    /// <see cref="Stopwatch"/>.</summary>
    internal static Func<long>? TimestampProviderForTesting { get; set; }

    private static long Timestamp() => TimestampProviderForTesting?.Invoke() ?? Stopwatch.GetTimestamp();

    /// <summary>Test accessor: true once the session's own events are attached (F3's retry
    /// contract).</summary>
    internal static bool SessionEventsAttached => Volatile.Read(ref _sessionEventsAttached);

    /// <summary>Test wiring; production uses the Core Audio implementations (CoreAudioInterop).
    /// Review 04 T4: DRAINS first and REFUSES to reconfigure while queued diagnostics work is in
    /// flight — silently zeroing the counters under parked items resurrected the fixup-01 flake
    /// family and made later drains time out. Tests run their parked work first (the collection
    /// base class does it in Dispose), or this throws — loud, per R6.</summary>
    internal static void ConfigureForTesting(
        IAudioStateReader? reader, IAudioChangeListener? listener, IAudioMeterProbe? meter = null)
    {
        if (!DrainQueuedWork())
            throw new InvalidOperationException(
                "queued F5 diagnostics work did not drain within 5 s; run the parked work before reconfiguring");
        lock (Gate)
        {
            _reader = reader;
            _listener = listener;
            _meter = meter;
            _changeEvent = null;
            _firstClickDone = 0;
            _endpointEventsAttached = false;
            _sessionEventsAttached = false;
            _failureLogged = 0;
            _failureReportCount = 0;
            _sessionRetryScheduled = 0;
            _shutDown = 0;
            TimestampProviderForTesting = null;
            _queuedWorkScheduled = 0;
            _queuedWorkCompleted = 0;
            _silentClickReports = 0;
            _sessionActive = 0;
            _meterWindow = new MeterWindow();
            Interlocked.Increment(ref _clickGeneration);
            while (PendingClicks.TryDequeue(out _)) { }
        }
    }

    /// <summary>Production composition (KeyboardCoordinator.LoadKeyboard): restore the Core Audio
    /// instruments if tests (or a future dispose path) left them unset. Idempotent and cheap.
    /// LOCK-FREE on purpose (review 02 R3): this runs on the UI thread at every settings apply, so
    /// it must never wait on a lock the diagnostics worker may hold across a COM call.
    /// Fixup 03 S1: it constructs nothing when the set is already wired, and construction itself
    /// acquires no COM — the observer's MMDeviceEnumerator (CoCreate ~44 ms cold) and the meter are
    /// acquired lazily on the diagnostics worker. This must never touch or wait on the audio
    /// stack: the owner would have no gaze control while it ran.</summary>
    public static void EnsureProductionWiring()
    {
        // The guards keep the Interlocked version from constructing a throwaway instance on
        // every settings apply (review 03 S1).
        if (Volatile.Read(ref _listener) is null)
            Interlocked.CompareExchange(ref _listener, new CoreAudioInterop.CoreAudioChangeObserver(), null);
        if (Volatile.Read(ref _reader) is null)
            Interlocked.CompareExchange(ref _reader, new CoreAudioInterop.CoreAudioStateReader(), null);
        if (Volatile.Read(ref _meter) is null)
            Interlocked.CompareExchange(ref _meter, new CoreAudioInterop.CoreAudioMeterProbe(), null);
    }

    /// <summary>Test accessor for the composition pin: true iff the REAL Core Audio set is wired.</summary>
    internal static bool ProductionWired =>
        _reader is CoreAudioInterop.CoreAudioStateReader
        && _listener is CoreAudioInterop.CoreAudioChangeObserver
        && _meter is CoreAudioInterop.CoreAudioMeterProbe;

    /// <summary>The queued unit of the first click. Registration comes FIRST (F10 item 4: the
    /// endpoint volume callback and ducking must exist before the first click's state is read and
    /// before any change can happen during the first show); the snapshot line follows. The latch
    /// is re-checked at execution time: work queued before App.OnExit's shutdown must not register
    /// or log afterwards (review 02 R4).</summary>
    private static void FirstClickWork()
    {
        if (Volatile.Read(ref _shutDown) == 1) return;
        TryRegisterChangeEvents();
        LogSnapshot();
    }

    /// <summary>Every click funnels here; the once-guard keeps the synchronous cost to one
    /// Interlocked read — the COM work itself is queued off the typing path (review 01 F4:
    /// a cold first read measures ~105 ms and must never delay a key).
    /// <para>
    /// Review-01 F3: winmm creates our audio session on the first PlaySound, so the first-click
    /// work can still find no session and leave the session instrument blind for the whole first
    /// show (the 9th sitting's mechanism). Every later click therefore retries the session events
    /// while they are unattached — at most one retry in flight, queued on the same worker.
    /// </para></summary>
    public static void OnClickAttempted()
    {
        if (Volatile.Read(ref _shutDown) == 1) return;
        if (Interlocked.CompareExchange(ref _firstClickDone, 1, 0) == 0)
        {
            RunOffTypingPath(FirstClickWork);
            return;
        }
        if (Volatile.Read(ref _sessionEventsAttached)) return;
        if (Interlocked.CompareExchange(ref _sessionRetryScheduled, 1, 0) != 0) return;
        RunOffTypingPath(SessionRetryWork);
    }

    /// <summary>Review-01 F3: one queued retry of the still-missing session registration. The
    /// flag is cleared even when the latch or the listener wins the race, so a later click can
    /// try again; the observer's own _comLock and idempotence make concurrent attempts safe.</summary>
    private static void SessionRetryWork()
    {
        Interlocked.Exchange(ref _sessionRetryScheduled, 0);
        if (Volatile.Read(ref _shutDown) == 1) return;
        if (Volatile.Read(ref _sessionEventsAttached)) return;
        TryRegisterChangeEvents();
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

    // ---- F10: per-show instruments (the meter window and the start-latency measurements) ----

    /// <summary>A silent click is "no peak above this". Linear 0..1 (GetPeakValue's normalized
    /// range); 0.001 is about -60 dBFS — far below a click at any audible volume, far above the
    /// exact 0 of a stopped stream.</summary>
    internal const float MeterReachedThreshold = 0.001f;

    /// <summary>A start slower than this is "late" (the add-on's per-show count). Justification:
    /// a healthy click goes Active ~10-40 ms after the request (the 9th sitting measured ~35 ms);
    /// the sitting's suspicious starts were ~400 ms. 200 ms is clear of normal jitter and still
    /// catches a device-wake delay.</summary>
    internal const int LateStartThresholdMs = 200;

    private const int MaxPendingClicks = 32;

    private sealed class MeterWindow
    {
        public int Reached;
        public int Silent;
        public int Pending;
        public int Unmetered;
        public int Late;
        public int MaxLateMs;
        public int FirstSilentLogged;
    }

    internal readonly record struct MeterCounts(
        int Reached, int Silent, int Pending, int Unmetered, int Late, int MaxLateMs);

    private readonly record struct PendingClick(long RequestedAt, MeterWindow Window);

    /// <summary>Longest click→Active delay that is still treated as THIS click's start. A stamp
    /// older than this can only be one whose Active event was never observed (the session was
    /// already Active, or registration had not happened yet) — measuring it would blame the next
    /// click for it.</summary>
    private const int MaxStartMeasurementMs = 3000;

    /// <summary>The counting window of the current show. A verdict or a late-start measurement
    /// carries its own window, so a sample that lands after the hide line can never pollute the
    /// NEXT show's counts.</summary>
    private static MeterWindow _meterWindow = new();
    private static int _clickGeneration;
    private static int _silentClickReports;
    private static int _sessionActive;
    // Click requests awaiting their session Active transition. A queue, not a single slot: two
    // clicks without an intervening Inactive must not borrow each other's Active measurement.
    private static readonly ConcurrentQueue<PendingClick> PendingClicks = new();

    /// <summary>Opens a fresh meter window for a real show (KeyboardSoundService.OnKeyboardShown,
    /// the same real-transition discipline as the E1 counters).</summary>
    public static void OnShowStarted()
    {
        if (Volatile.Read(ref _shutDown) == 1) return;
        Volatile.Write(ref _meterWindow, new MeterWindow());
        Interlocked.Increment(ref _clickGeneration);
        while (PendingClicks.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Every click winmm accepted (KeyboardSoundService.PlayKeyClick's played branch — a skipped
    /// or refused click cannot reach the speakers). Costs: one queue write for the start-latency
    /// measurement, plus one queued sampler when a meter is wired. Both run off the typing path.
    /// </summary>
    public static void OnClickPlayed()
    {
        if (Volatile.Read(ref _shutDown) == 1) return;
        var window = Volatile.Read(ref _meterWindow);
        // A start is only measurable when our session events are attached AND the session is not
        // already Active: otherwise no Active transition will arrive for THIS click, and queuing a
        // stamp would make the NEXT click's Active blame this one (a phantom "late").
        if (Volatile.Read(ref _sessionEventsAttached) && Volatile.Read(ref _sessionActive) == 0)
        {
            if (PendingClicks.Count >= MaxPendingClicks) PendingClicks.TryDequeue(out _);
            PendingClicks.Enqueue(new PendingClick(Timestamp(), window));
        }
        var meter = _meter;
        if (meter is null)
        {
            // A played click with no meter wired can never get a verdict; count it as unmetered
            // so the hide line's arithmetic still accounts for every played click:
            // reached + silent + pending + unmetered = played.
            Interlocked.Increment(ref window.Unmetered);
            return;
        }
        var generation = Interlocked.Increment(ref _clickGeneration);
        Interlocked.Increment(ref window.Pending);
        RunOffTypingPath(() => SampleClick(window, generation, meter));
    }

    private static void SampleClick(MeterWindow window, int generation, IAudioMeterProbe meter)
    {
        try
        {
            if (Volatile.Read(ref _shutDown) == 1) return;
            bool Stop() => Volatile.Read(ref _shutDown) == 1
                || Volatile.Read(ref _clickGeneration) != generation
                || !ReferenceEquals(Volatile.Read(ref _meterWindow), window);
            var peak = meter.SamplePeak(Stop);
            // Review-01 F5: the shutdown latch is re-checked after the sample, so a sampler that
            // finishes after Shutdown can neither count nor write the silent line (shutdown is
            // always no verdict, even when the probe already watched past SilentAfterMs).
            if (Volatile.Read(ref _shutDown) == 1) return;
            if (peak is null)
            {
                // Review 02 N1: no verdict — an unreadable meter, or a supersession before the
                // probe had watched SilentAfterMs. Counted so the hide line's arithmetic stays
                // exact: reached + silent + pending + unmetered = played.
                Interlocked.Increment(ref window.Unmetered);
                return;
            }
            if (peak.Value > MeterReachedThreshold)
            {
                Interlocked.Increment(ref window.Reached);
                return;
            }
            Interlocked.Increment(ref window.Silent);
            ReportFirstSilentClick(window);
        }
        finally
        {
            Interlocked.Decrement(ref window.Pending);
        }
    }

    /// <summary>ONE line per show at its first silent click, carrying that moment's endpoint and
    /// session state. Runs on the diagnostics worker; the read is guarded like every other.</summary>
    private static void ReportFirstSilentClick(MeterWindow window)
    {
        if (Interlocked.Exchange(ref window.FirstSilentLogged, 1) != 0) return;
        Interlocked.Increment(ref _silentClickReports);
        try
        {
            Emit(FormatSilentClickLine(_reader?.Read()));
        }
        catch (Exception ex)
        {
            ReportFailure("silent-click state read", ex);
        }
    }

    /// <summary>
    /// The hide line's meter fields: a NON-BLOCKING snapshot of the show's window (review-01 F2 —
    /// this runs on the dispatcher, so it must never sleep). A click whose verdict is still in
    /// flight is reported as <c>pending</c>, which is exactly what that field is for; its later
    /// verdict belongs to the show that already closed and is discarded with the window.
    /// <c>unmetered</c> counts the clicks that ended with no verdict (review-02 N1): an unreadable
    /// meter, a supersession before the probe had watched SilentAfterMs, or a click played while
    /// no meter was wired. Together they account for every played click:
    /// reached + silent + pending + unmetered = played.
    /// </summary>
    internal static MeterCounts TakeMeterCounts()
    {
        var window = Volatile.Read(ref _meterWindow);
        return new MeterCounts(
            Volatile.Read(ref window.Reached),
            Volatile.Read(ref window.Silent),
            Volatile.Read(ref window.Pending),
            Volatile.Read(ref window.Unmetered),
            Volatile.Read(ref window.Late),
            Volatile.Read(ref window.MaxLateMs));
    }

    /// <summary>The click→Active measurement (F10 add-on): one queued request per played click,
    /// popped by the session's own Active transition. Runs on the WASAPI callback thread; the
    /// window is the one the click was requested in, so a late event cannot land in a new show.</summary>
    private static void RecordStartLatency()
    {
        // Drop stamps whose Active was never observed instead of attributing them to a later
        // click; the first plausible stamp is this Active's click.
        while (PendingClicks.TryDequeue(out var click))
        {
            var ms = ElapsedMs(click.RequestedAt);
            if (ms > MaxStartMeasurementMs) continue;
            UpdateMax(ref click.Window.MaxLateMs, ms);
            if (ms >= LateStartThresholdMs) Interlocked.Increment(ref click.Window.Late);
            return;
        }
    }

    private static int ElapsedMs(long since) =>
        (int)Math.Min(int.MaxValue, (Timestamp() - since) * 1000L / Stopwatch.Frequency);

    private static void UpdateMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value)
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
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
            _changeEvent ??= OnChange;
            // R3 (review 02): the COM calls run OUTSIDE Gate — the UI thread takes Gate in
            // EnsureProductionWiring/Shutdown and must never wait behind a hung AudioSrv RPC.
            // The attached flags are read racily and the winners published under the lock; a
            // duplicate registration attempt is impossible because the listener's own state
            // guard is idempotent.
            var endpointWasAttached = _endpointEventsAttached;
            var sessionWasAttached = _sessionEventsAttached;
            var endpointAttached = endpointWasAttached || listener.TryRegister(_changeEvent);
            // F10: the session instrument is NOT gated on the endpoint-level result any more — a
            // ducking registration failure must not cost the per-app volume/mute/state events.
            var sessionAttached = sessionWasAttached || listener.TryRegisterSessionEvents();
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

    private static void OnChange(AudioChange change)
    {
        try
        {
            Emit(change.Line);
            if (change.Kind != AudioChangeKind.SessionState) return;
            if (change.SessionState == "Active")
            {
                Volatile.Write(ref _sessionActive, 1);
                RecordStartLatency();
            }
            else
            {
                Volatile.Write(ref _sessionActive, 0);
            }
        }
        catch (Exception ex)
        {
            ReportFailure("change-event line", ex);
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
    /// an in-flight or hung COM call — the process exit cleans up whatever is still running.
    /// A second call is a no-op (F10's shutdown pin).</summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutDown, 1) == 1) return;
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
