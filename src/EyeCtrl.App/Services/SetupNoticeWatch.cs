namespace EyeCtrl.App.Services;

using System;

/// <summary>
/// Review 01 M2: Setup writes <c>setup-last.json</c> at least 20 s AFTER it starts the App (the
/// pass rule waits that long for the App to stay alive), so a single read at startup always misses
/// the record of the update that just happened — the very session the notice is for. This watch
/// keeps checking for a newer record for a bounded window after startup: a periodic re-read
/// (production: a one-shot <c>DispatcherTimer</c> chain) accelerated by a file-change callback
/// (production: a <c>FileSystemWatcher</c> on the record's folder). The once-per-record-<c>time</c>
/// rule stays in <see cref="SetupLastNoticeService" />, untouched.
/// </summary>
public sealed class SetupNoticeWatch : IDisposable
{
    private readonly SetupLastNoticeService _service;
    private readonly TimeSpan _window;
    private readonly TimeSpan _interval;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Action, IDisposable?>? _fileChange;
    private readonly Func<bool>? _isGuideOpen;
    private readonly Func<bool>? _isBackstageOpen;

    private DateTimeOffset _startedAt;
    private bool _started;
    private bool _done;
    private IDisposable? _fileWatch;

    /// <summary>Creates the watch.</summary>
    /// <param name="service">The notice service whose <see cref="SetupLastNoticeService.CheckStartup" />
    /// is re-run per tick.</param>
    /// <param name="window">How long after <see cref="Start" /> the watch keeps looking (the
    /// ticket suggests ~90 s: past Setup's 20 s alive check, its 30 s token timeout and a revert).</param>
    /// <param name="interval">The poll interval between ticks.</param>
    /// <param name="schedule">Schedules one tick; the App supplies a one-shot DispatcherTimer, the
    /// tests a manual trigger. Ticks arrive on the UI thread.</param>
    /// <param name="now">The clock the window is measured against (a fake in the tests).</param>
    /// <param name="fileChange">Optional registration for a change callback (the FileSystemWatcher
    /// path); the callback must arrive on the UI thread, which the App's wrapper guarantees. The
    /// returned disposable is released when the watch finishes. Tests pass a disposable recorder
    /// and drive polls.</param>
    /// <param name="isGuideOpen">Whether the first-run guide holds the screen; while it does, the
    /// checks and the expiry window both pause, and the guide's close pokes once (r4 F3, r1 F24).</param>
    /// <param name="isBackstageOpen">Whether the fullscreen Backstage menu holds the screen. Same
    /// rule as the guide: this watch shows THROUGH the host presenter directly, bypassing every
    /// host guard, so the watch itself must defer or a card pixels-over the menu would be
    /// undismissable by gaze for up to the watch's 90 s window.</param>
    public SetupNoticeWatch(
        SetupLastNoticeService service,
        TimeSpan window,
        TimeSpan interval,
        Action<TimeSpan, Action> schedule,
        Func<DateTimeOffset> now,
        Func<Action, IDisposable?>? fileChange = null,
        Func<bool>? isGuideOpen = null,
        Func<bool>? isBackstageOpen = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _fileChange = fileChange;
        _isGuideOpen = isGuideOpen;
        _isBackstageOpen = isBackstageOpen;
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        if (interval <= TimeSpan.Zero || interval > window)
            throw new ArgumentOutOfRangeException(nameof(interval));
        _window = window;
        _interval = interval;
    }

    /// <summary>True once a notice was shown by this watch.</summary>
    public bool NoticeShown { get; private set; }

    /// <summary>True when the window closed (after a notice, or with none).</summary>
    public bool IsDone => _done;

    /// <summary>Guide opened or closed: closing pokes once (r4 F3).</summary>
    /// <param name="open">True when the first-run guide has opened.</param>
    public void OnGuideOpenChanged(bool open)
    {
        if (!open) Poke();
    }

    /// <summary>The fullscreen Backstage menu opened or closed: closing pokes once, exactly like
    /// the guide's close. Opening itself needs no action — <see cref="ScreenHeld"/> makes every
    /// tick and the expiry window stand down.</summary>
    /// <param name="open">True when Backstage has opened.</param>
    public void OnBackstageOpenChanged(bool open)
    {
        if (!open) Poke();
    }

    /// <summary>
    /// Starts watching: one immediate check (a record from the previous session still shows at
    /// this start), then the first scheduled poll. Review 02 R5: an OLD record shown by that check
    /// does NOT end the watch — this session's own record is still to come, and the once-per-time
    /// rule makes a repeat of the same record impossible. Only a notice whose record time is newer
    /// than this session's start ends the watch.
    /// </summary>
    public void Start()
    {
        if (_started)
            return;

        _started = true;
        _startedAt = _now();
        try
        {
            _fileWatch = _fileChange?.Invoke(Poke);
        }
        catch (Exception)
        {
            // The poll alone is the fallback; a watcher that cannot start costs latency, not the notice.
            _fileWatch = null;
        }

        if (CheckStartupWhenScreenFree())
        {
            NoticeShown = true;
            if (IsCurrentSessionRecord())
            {
                Finish();
                return;
            }
        }

        _schedule(_interval, Poll);
    }

    /// <summary>One scheduled poll: check, then either finish or schedule the next.</summary>
    internal void Poll()
    {
        if (_done)
            return;

        if (CheckStartupWhenScreenFree())
        {
            NoticeShown = true;
            if (IsCurrentSessionRecord())
            {
                Finish();
                return;
            }
        }

        // The 90 s window must not run out behind the guide (r1 F24) or the fullscreen Backstage
        // menu: expiry is deferred while either holds the screen, and its close-time poke shows the
        // notice then.
        if (_now() - _startedAt >= _window && !ScreenHeld)
        {
            Finish();
            return;
        }

        _schedule(_interval, Poll);
    }

    /// <summary>The file-change path: check right away, never re-arm the poll chain (it runs on its
    /// own, so change storms cannot multiply timers). A poke before <see cref="Start" /> is a
    /// no-op: the App subscribes the guide-close poke at construction (S2-1 defers the Start into
    /// the guide-closed handler), so the startup guide's close lands on a not-yet-started watch —
    /// and must not read, show, or finish it; Start's own immediate check then handles the record.</summary>
    internal void Poke()
    {
        if (!_started || _done)
            return;

        if (!CheckStartupWhenScreenFree())
            return;

        NoticeShown = true;
        if (IsCurrentSessionRecord())
            Finish();
    }

    /// <summary>True when the last shown record is newer than this watch's start — this session's
    /// own outcome, as opposed to a previous session's record surfaced at the immediate check.</summary>
    private bool IsCurrentSessionRecord() =>
        _service.LastShownTime is { } shown && shown > _startedAt;

    /// <summary>r4 F3 + r1 F24: CheckStartup writes setup-notice-shown.json BEFORE _show (no burn
    /// behind the guide), and the expiry window must not run out while the guide holds the
    /// screen — both skip while a fullscreen surface is up; the close-time poke shows then.</summary>
    private bool ScreenHeld =>
        (_isGuideOpen?.Invoke() ?? false) || (_isBackstageOpen?.Invoke() ?? false);

    private bool CheckStartupWhenScreenFree() => !ScreenHeld && _service.CheckStartup();

    private void Finish()
    {
        if (_done)
            return;

        _done = true;
        _fileWatch?.Dispose();
        _fileWatch = null;
    }

    /// <inheritdoc />
    public void Dispose() => Finish();
}
