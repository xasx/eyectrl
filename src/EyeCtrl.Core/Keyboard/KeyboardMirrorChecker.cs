namespace EyeCtrl.Core.Keyboard;

using EyeCtrl.Core.Interfaces;

/// <summary>
/// E1's mirror check: does the keyboard's own record of what it typed (the mirror,
/// <c>_recentText + CurrentInput</c>) still match the tail of the focused control? A mismatch
/// means the target app changed the text behind the keyboard's back (autocorrect, autocomplete,
/// auto-capitalisation), which is what the editing release has to know per app.
///
/// <para>
/// <b>It can never block or delay a hide or an Enter.</b> <see cref="Begin"/> returns at once; the
/// UI Automation read runs on the thread pool under a hard time box
/// (<see cref="DefaultTimeBox"/>, 200 ms). A read that has not answered by then is logged
/// <c>unsupported</c> and abandoned (UI Automation cannot be cancelled), and while an abandoned
/// read is still running no new one starts — a hung control costs one pooled thread, not one per
/// Enter. Any exception is <c>unsupported</c>. The mirror is read by the CALLER on the UI thread
/// before the keystroke is sent, so the snapshot cannot race the edit it describes.
/// </para>
///
/// <para>
/// <b>Content-free.</b> The comparison happens in memory; only the verdict and three lengths are
/// logged, never a string. A sensitive (password) context is not read at all and is logged
/// <c>suppressed</c> — those events must be excluded from the divergence rate.
/// </para>
/// </summary>
public sealed class KeyboardMirrorChecker
{
    /// <summary>The hard time box for the whole read.</summary>
    public static readonly TimeSpan DefaultTimeBox = TimeSpan.FromMilliseconds(200);

    /// <summary>The control's tail ends with the mirror.</summary>
    public const string Match = "match";
    /// <summary>The control's tail differs from the mirror.</summary>
    public const string Mismatch = "mismatch";
    /// <summary>No TextPattern, no probe, a read still in flight, a timeout or any failure.</summary>
    public const string Unsupported = "unsupported";
    /// <summary>A sensitive (password) context: the control was not read at all.</summary>
    public const string Suppressed = "suppressed";
    /// <summary>The mirror was empty, so there was nothing to compare.</summary>
    public const string Empty = "empty";

    // The read asks for this many characters beyond the mirror, so an Enter that already landed
    // (its trailing line break) cannot push the compared tail out of the window.
    private const int ReadSlack = 2;

    private readonly IKeyboardContextProbe? _probe;
    private readonly ISensitiveContextDetector? _sensitive;
    private readonly KeyboardE1Recorder _recorder;
    private readonly TimeSpan _timeBox;
    private int _readInFlight;

    /// <param name="recorder">Where the verdict is logged.</param>
    /// <param name="probe">The OS probe; null makes every check <c>unsupported</c>.</param>
    /// <param name="sensitive">When it reports a sensitive context the control is not read.</param>
    /// <param name="timeBox">The hard time box; defaults to <see cref="DefaultTimeBox"/>.</param>
    public KeyboardMirrorChecker(KeyboardE1Recorder recorder, IKeyboardContextProbe? probe,
        ISensitiveContextDetector? sensitive = null, TimeSpan? timeBox = null)
    {
        _recorder = recorder;
        _probe = probe;
        _sensitive = sensitive;
        _timeBox = timeBox ?? DefaultTimeBox;
    }

    /// <summary>
    /// Starts one check and returns immediately. The returned task completes after the verdict is
    /// logged and never faults; callers must not await it on a path that must not wait.
    /// </summary>
    /// <param name="at"><c>hide</c> or <c>enter</c>.</param>
    /// <param name="mirror">The keyboard's record, snapshotted by the caller. Never logged.</param>
    public Task Begin(string at, string mirror)
    {
        try
        {
            if (!_recorder.Enabled)
                return Task.CompletedTask;
            if (mirror.Length == 0)
                return Report(at, Empty, 0, 0, 0);
            if (_sensitive?.IsSensitiveActive == true)
                return Report(at, Suppressed, mirror.Length, 0, 0);
            if (_probe is null)
                return Report(at, Unsupported, mirror.Length, 0, 0);
            if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0)
                return Report(at, Unsupported, mirror.Length, 0, 0);

            return Task.Run(() => CheckAsync(at, mirror));
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    private async Task CheckAsync(string at, string mirror)
    {
        string? tail = null;
        Task<string?>? read = null;
        try
        {
            read = Task.Run(() => _probe!.ReadFocusedTextTail(mirror.Length + ReadSlack));
            // Free the slot only when the read itself ends, not when the time box does.
            _ = read.ContinueWith(_ => Interlocked.Exchange(ref _readInFlight, 0),
                TaskScheduler.Default);
            tail = await read.WaitAsync(_timeBox).ConfigureAwait(false);
        }
        catch (Exception)
        {
            tail = null; // timeout, UIA failure, anything: unsupported
            if (read is null)
                Interlocked.Exchange(ref _readInFlight, 0);
        }

        try
        {
            var (result, controlLength, common) = Compare(mirror, tail);
            _recorder.RecordMirror(at, result, mirror.Length, controlLength, common);
        }
        catch (Exception)
        {
            // Logging must never throw into the app.
        }
    }

    private Task Report(string at, string result, int mirrorLength, int controlLength, int common)
    {
        _recorder.RecordMirror(at, result, mirrorLength, controlLength, common);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pure comparison. A line break that the Enter keystroke already added is ignored; the control
    /// tail must END WITH the mirror. Returns the verdict, the compared tail's length and the length of
    /// the common suffix (how many trailing characters agree) — the only things that get logged.
    /// </summary>
    public static (string Result, int ControlLength, int CommonSuffix) Compare(string mirror, string? controlTail)
    {
        if (controlTail is null)
            return (Unsupported, 0, 0);

        var tail = controlTail.TrimEnd('\r', '\n');
        if (tail.EndsWith(mirror, StringComparison.Ordinal))
            return (Match, tail.Length, mirror.Length);

        var common = 0;
        while (common < mirror.Length && common < tail.Length
               && mirror[mirror.Length - 1 - common] == tail[tail.Length - 1 - common])
            common++;
        return (Mismatch, tail.Length, common);
    }
}
