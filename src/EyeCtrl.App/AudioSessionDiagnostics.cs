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
    private static IAudioStateReader? _reader;
    private static IAudioChangeListener? _listener;
    private static Action<string>? _changeLine;
    private static int _firstClickDone;
    private static bool _endpointEventsAttached, _sessionEventsAttached;
    private static bool _failureLogged;

    /// <summary>Test seam: every diagnostics line (snapshots and change events) is captured here
    /// instead of Serilog when set.</summary>
    internal static Action<string>? LineSink { get; set; }

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

    /// <summary>Every click funnels here; the once-guard keeps the cost to one Interlocked read
    /// after the first click — never COM on the typing path.</summary>
    public static void OnClickAttempted()
    {
        if (Interlocked.CompareExchange(ref _firstClickDone, 1, 0) != 0) return;
        LogSnapshot();
        TryRegisterChangeEvents();
    }

    /// <summary>
    /// A real keyboard show/hide after the first click: a baseline snapshot (boundary state of
    /// the typing session) plus a retry of any registration that is still missing. A no-op
    /// before the first click — nothing to baseline yet, and COM stays off the pre-click path.
    /// </summary>
    public static void OnKeyboardTransition()
    {
        if (Volatile.Read(ref _firstClickDone) == 0) return;
        LogSnapshot();
        lock (Gate)
        {
            if (_endpointEventsAttached && _sessionEventsAttached) return;
        }
        TryRegisterChangeEvents();
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
            var snapshot = _reader?.Read();
            Emit(snapshot?.FormatLine() ?? "Keyboard audio: state unavailable (Core Audio read failed)");
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
