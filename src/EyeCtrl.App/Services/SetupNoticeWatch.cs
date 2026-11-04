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
    public SetupNoticeWatch(
        SetupLastNoticeService service,
        TimeSpan window,
        TimeSpan interval,
        Action<TimeSpan, Action> schedule,
        Func<DateTimeOffset> now,
        Func<Action, IDisposable?>? fileChange = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _fileChange = fileChange;
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

    /// <summary>
    /// Starts watching: one immediate check (a record from the previous session still shows at
    /// this start, as before), then the first scheduled poll.
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

        Poke();
        if (!_done)
            _schedule(_interval, Poll);
    }

    /// <summary>One scheduled poll: check, then either finish or schedule the next.</summary>
    internal void Poll()
    {
        if (_done)
            return;

        if (_service.CheckStartup())
        {
            NoticeShown = true;
            Finish();
            return;
        }

        if (_now() - _startedAt >= _window)
        {
            Finish();
            return;
        }

        _schedule(_interval, Poll);
    }

    /// <summary>The file-change path: check right away, never re-arm the poll chain (it runs on its
    /// own, so change storms cannot multiply timers).</summary>
    internal void Poke()
    {
        if (_done)
            return;

        if (_service.CheckStartup())
        {
            NoticeShown = true;
            Finish();
        }
    }

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
