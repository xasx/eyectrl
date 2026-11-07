namespace EyeCtrl.App.Services;

using System;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using EyeCtrl.Setup;

/// <summary>One poll's verdict for <see cref="UninstallHandoffWatch" />.</summary>
/// <param name="Done">Whether the hand-off is over.</param>
/// <param name="SessionSeen">Whether Setup's session was observed live at any point (carried to
/// the next poll).</param>
/// <param name="SessionGoneSince">When the observed session was first seen missing (null while it
/// is live, or before it was ever seen). Carried to the next poll, so the launcher → run-copy gap
/// can be told from a real end.</param>
/// <param name="End">The end, when <paramref name="Done" />.</param>
/// <param name="Outcome">Setup's own recorded outcome, when this end came from the record.</param>
internal readonly record struct UninstallWatchVerdict(
    bool Done, bool SessionSeen, DateTimeOffset? SessionGoneSince,
    UninstallHandoffEnd End, SetupOutcome? Outcome);

/// <summary>
/// The pure part of the uninstall watch (C9 S6e fixup-08): what one poll knows, and whether the
/// hand-off is over. Extracted so every branch — a record while Setup still runs, a session that
/// vanishes without a record, a watch that never sees a session — is testable with a synthetic
/// clock and no sleeps.
/// </summary>
internal static class UninstallHandoffOutcome
{
    /// <summary>
    /// The record's outcome when <c>setup-last.json</c> is THIS hand-off's terminal uninstall
    /// record, or <c>null</c> when it is not: an older record (the previous sitting left one),
    /// another verb, or the transient <see cref="SetupOutcome.HandedOff" /> the parent deliberately
    /// does not write.
    /// </summary>
    internal static SetupOutcome? Classify(SetupResult? record, DateTimeOffset handoffStartedAt)
    {
        if (record is null)
            return null;

        if (record.Verb != SetupVerb.Uninstall)
            return null;

        // HandedOff is never recorded (SetupWorkflow.Finish skips the write), but a record from a
        // newer Setup must still never be read as terminal.
        if (record.Outcome == SetupOutcome.HandedOff)
            return null;

        if (record.Time < handoffStartedAt)
            return null;

        return record.Outcome;
    }

    /// <summary>
    /// Decides what one poll means.
    /// </summary>
    /// <remarks>
    /// <para><b>The record is written before Setup's result screen, and the session ends after
    /// it.</b> Setup records the result, then <c>Finish</c> blocks until the result window has
    /// closed itself (10 s for a cancel, 60 s for a failure), and only then does the process
    /// release the session mutex. Waiting for both is what keeps Settings from covering the
    /// failure text the owner has to read (the S6e review's N1 contrast work).</para>
    /// <para><b>The launcher → run-copy gap (review-08 H1).</b> The installed launcher starts the
    /// run copy and returns at once, so for a moment NO process holds the session — the mutex is
    /// re-taken only when the run copy's own <c>Program.Main</c> runs. A seen session that is
    /// missing is therefore not an end until it has stayed missing for
    /// <paramref name="vanishGrace" /> (the run copy's own takeover bound); a re-appearance resets
    /// that window. A terminal record still ends at once: the gap has no record.</para>
    /// <para><b>Never strand:</b> a session that vanishes and stays gone without a record (Setup
    /// crashed), a session that was never seen (the launch died before taking the mutex), and a
    /// watch that outlives its cap all end as <see cref="UninstallHandoffEnd.Unknown" />, which
    /// restores Settings. A successful uninstall ends as
    /// <see cref="UninstallHandoffEnd.Uninstalled" /> as soon as the record is read: Setup stops
    /// the App in the same run, so there is nothing to restore.</para>
    /// </remarks>
    internal static UninstallWatchVerdict Evaluate(
        DateTimeOffset handoffStartedAt,
        DateTimeOffset now,
        SetupResult? record,
        bool sessionLive,
        bool sessionSeen,
        DateTimeOffset? sessionGoneSince,
        TimeSpan startGrace,
        TimeSpan vanishGrace,
        TimeSpan maximumWatch)
    {
        var seen = sessionSeen || sessionLive;
        var goneSince = sessionLive ? null : seen ? sessionGoneSince ?? now : sessionGoneSince;
        var outcome = Classify(record, handoffStartedAt);

        // EyeCtrl is being removed: Setup stops the App itself. Never bring Settings back — a
        // window raised over the removal would be the last thing the owner sees.
        if (outcome == SetupOutcome.Uninstalled)
            return new UninstallWatchVerdict(true, seen, goneSince, UninstallHandoffEnd.Uninstalled, outcome);

        // Setup is finished AND gone: the result screen has closed too.
        if (outcome is { } terminal && !sessionLive)
            return new UninstallWatchVerdict(true, seen, goneSince, UninstallHandoffEnd.NotUninstalled, terminal);

        // The cap wins over everything except a completed uninstall above: after this long the
        // owner must not be left in front of a hidden Settings, even if the mutex is stuck.
        if (now - handoffStartedAt >= maximumWatch)
            return new UninstallWatchVerdict(true, seen, goneSince, UninstallHandoffEnd.Unknown, null);

        if (sessionLive)
            return new UninstallWatchVerdict(false, seen, goneSince, UninstallHandoffEnd.Unknown, null);

        if (seen)
        {
            // Review-08 H1: the launcher released the session and the run copy has not taken it
            // yet. Only a session that has stayed gone for the takeover window means Setup died.
            var goneFor = now - (goneSince ?? now);
            return goneFor >= vanishGrace
                ? new UninstallWatchVerdict(true, seen, goneSince, UninstallHandoffEnd.Unknown, null)
                : new UninstallWatchVerdict(false, seen, goneSince, UninstallHandoffEnd.Unknown, null);
        }

        // No session ever appeared: the launch never became a Setup session (a Busy refusal, a
        // crash, a machine-slow start that passed the grace). Bounded, then restore.
        if (now - handoffStartedAt >= startGrace)
            return new UninstallWatchVerdict(true, seen, goneSince, UninstallHandoffEnd.Unknown, null);

        return new UninstallWatchVerdict(false, seen, goneSince, UninstallHandoffEnd.Unknown, null);
    }
}

/// <summary>
/// The production watch (C9 S6e fixup-08): one immediate poll, then one-shot scheduled polls until
/// the hand-off ends. Nothing here runs on a background thread — the schedule seam delivers every
/// tick on the UI thread (a <see cref="DispatcherTimer" /> in production), so the restore callback
/// never needs marshalling and the window is only touched by its owner thread.
/// </summary>
public sealed class UninstallHandoffWatch : IUninstallHandoffWatch, IDisposable
{
    /// <summary>How often the record and the session are re-read. Both are local and tiny.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long the watch tolerates "no session and no record" before calling it unknown. Setup
    /// takes its session mutex in <c>Program.Main</c> before any window, elevation or file write,
    /// so this bound covers a slow ShellExecute start and the launcher's run-copy preparation —
    /// it is only ever spent when Setup never started at all. Matches the App's hand-off settle
    /// window, because both answer the same "did the launch become a Setup?" question.
    /// </summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a session that HAS been seen must stay missing before the watch calls it an end
    /// (review-08 H1). The installed launcher starts the run copy and returns at once, so for a
    /// moment no process holds the session; the run copy re-takes it within
    /// <see cref="SetupSessionGuard.HandoffTakeoverWait" /> (15 s, the same bound the run copy
    /// itself waits for its launcher). Anything shorter and a poll that lands in that gap would
    /// bring Settings back over the Q8 that is about to appear — the very F4 this feature fixes.
    /// </summary>
    public static readonly TimeSpan VanishGrace = SetupSessionGuard.HandoffTakeoverWait;

    /// <summary>
    /// The hard cap. Q8's own timeout is five minutes and a failure result stays up for 60 s, so
    /// this leaves room for both plus a slow machine; past it Settings comes back whatever the
    /// mutex says. Never strand the owner.
    /// </summary>
    public static readonly TimeSpan MaximumWatch = TimeSpan.FromMinutes(10);

    private readonly Func<SetupResult?> _readRecord;
    private readonly Func<bool> _sessionLive;
    private readonly Func<TimeSpan, Action, IDisposable?> _schedule;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _startGrace;
    private readonly TimeSpan _vanishGrace;
    private readonly TimeSpan _maximumWatch;

    private DateTimeOffset _handoffStartedAt;
    private Action<UninstallHandoffEnd, SetupOutcome?>? _ended;
    private bool _sessionSeen;
    private DateTimeOffset? _sessionGoneSince;
    private bool _finished;
    private IDisposable? _pending;

    /// <summary>Creates the watch.</summary>
    /// <param name="readRecord">Reads <c>setup-last.json</c>, or null when absent/unreadable.</param>
    /// <param name="sessionLive">Whether Setup's single-session mutex is held right now.</param>
    /// <param name="schedule">Schedules one tick after a delay; returns a handle disposed before
    /// the next tick or at the end. Production is a one-shot DispatcherTimer; tests drive ticks
    /// manually.</param>
    /// <param name="utcNow">The clock behind the grace and the cap.</param>
    /// <param name="startGrace">Overridable for tests; production uses <see cref="StartGrace" />.</param>
    /// <param name="vanishGrace">Overridable for tests; production uses <see cref="VanishGrace" />.</param>
    /// <param name="maximumWatch">Overridable for tests; production uses <see cref="MaximumWatch" />.</param>
    public UninstallHandoffWatch(
        Func<SetupResult?> readRecord,
        Func<bool> sessionLive,
        Func<TimeSpan, Action, IDisposable?> schedule,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? startGrace = null,
        TimeSpan? maximumWatch = null,
        TimeSpan? vanishGrace = null)
    {
        _readRecord = readRecord ?? throw new ArgumentNullException(nameof(readRecord));
        _sessionLive = sessionLive ?? throw new ArgumentNullException(nameof(sessionLive));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _startGrace = startGrace ?? StartGrace;
        _vanishGrace = vanishGrace ?? VanishGrace;
        _maximumWatch = maximumWatch ?? MaximumWatch;

        if (_startGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startGrace));
        if (_vanishGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(vanishGrace));
        if (_maximumWatch < _startGrace || _maximumWatch < _vanishGrace)
            throw new ArgumentOutOfRangeException(nameof(maximumWatch));
    }

    /// <inheritdoc />
    public void Start(DateTimeOffset handoffStartedAt, Action<UninstallHandoffEnd, SetupOutcome?> ended)
    {
        if (_finished || _ended is not null)
            return;

        _ended = ended ?? throw new ArgumentNullException(nameof(ended));
        _handoffStartedAt = handoffStartedAt;
        Poll();
    }

    /// <summary>Stops the watch without calling back (the App or the window is going away).</summary>
    public void Dispose()
    {
        _finished = true;
        _ended = null;
        StopPending();
    }

    private void Poll()
    {
        if (_finished || _ended is null)
            return;

        UninstallWatchVerdict verdict;
        try
        {
            var now = _utcNow();
            var sessionLive = TrySessionLive();

            // Read the record only when Setup can no longer be writing it: its session is gone,
            // or the cap is reached. Setup writes the record while it still holds that mutex, so
            // this makes the read race-free by construction — a concurrent read could otherwise
            // make Setup's File.Move(overwrite) fail and lose the outcome (its recorder only logs).
            var record = !sessionLive || now - _handoffStartedAt >= _maximumWatch
                ? TryReadRecord()
                : null;

            verdict = UninstallHandoffOutcome.Evaluate(
                _handoffStartedAt, now, record, sessionLive,
                _sessionSeen, _sessionGoneSince, _startGrace, _vanishGrace, _maximumWatch);
        }
        catch (Exception)
        {
            // A seam that throws is still an end: Settings must come back.
            Finish(UninstallHandoffEnd.Unknown, null);
            return;
        }

        _sessionSeen = verdict.SessionSeen;
        _sessionGoneSince = verdict.SessionGoneSince;
        if (verdict.Done)
        {
            Finish(verdict.End, verdict.Outcome);
            return;
        }

        ScheduleNext();
    }

    private void ScheduleNext()
    {
        StopPending();
        try
        {
            _pending = _schedule(PollInterval, Poll);
        }
        catch (Exception)
        {
            // No tick will arrive, so waiting any longer would strand the owner.
            Finish(UninstallHandoffEnd.Unknown, null);
        }
    }

    private SetupResult? TryReadRecord()
    {
        try
        {
            return _readRecord();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool TrySessionLive()
    {
        try
        {
            return _sessionLive();
        }
        catch (Exception)
        {
            // Unknown is "not live" here: the record still ends the watch, and the grace/cap keep
            // a broken probe from holding Settings hidden forever.
            return false;
        }
    }

    private void Finish(UninstallHandoffEnd end, SetupOutcome? outcome)
    {
        if (_finished)
            return;

        var ended = _ended;
        _finished = true;
        _ended = null;
        StopPending();

        try
        {
            ended?.Invoke(end, outcome);
        }
        catch (Exception)
        {
            // The watcher's contract ends here; a restore that threw must not loop the watch.
        }
    }

    private void StopPending()
    {
        var pending = _pending;
        _pending = null;
        try
        {
            pending?.Dispose();
        }
        catch (Exception)
        {
            // A timer that refuses to dispose is the dispatcher's problem, not the watch's.
        }
    }
}

/// <summary>
/// The production watch factory: Setup's result record and session mutex, a one-shot
/// <see cref="DispatcherTimer" /> per poll, and the wall clock. The timer is created on the thread
/// that calls <see cref="IUninstallHandoffWatch.Start" /> — the UI thread, from the Settings row's
/// activation — so every tick and the restore arrive there.
/// </summary>
public sealed class UninstallHandoffWatchFactory : IUninstallHandoffWatchFactory
{
    private readonly Func<SetupResult?> _readRecord;
    private readonly Func<bool> _sessionLive;
    private readonly Func<TimeSpan, Action, IDisposable?> _schedule;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <summary>Creates the factory from its seams.</summary>
    public UninstallHandoffWatchFactory(
        Func<SetupResult?> readRecord,
        Func<bool> sessionLive,
        Func<TimeSpan, Action, IDisposable?> schedule,
        Func<DateTimeOffset>? utcNow = null)
    {
        _readRecord = readRecord ?? throw new ArgumentNullException(nameof(readRecord));
        _sessionLive = sessionLive ?? throw new ArgumentNullException(nameof(sessionLive));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The machine's watch: <c>%APPDATA%\EyeCtrl\setup-last.json</c> through the same layout the
    /// S6f notice uses, and Setup's production session name. Nothing is written.
    /// </summary>
    public static UninstallHandoffWatchFactory CreateForProduction()
    {
        var recordPath = SetupNoticeFiles.CreateForProduction().RecordPath;
        return new UninstallHandoffWatchFactory(
            () => ReadRecord(recordPath),
            () => SetupSessionGuard.IsSessionLive(),
            (delay, tick) => ScheduleOnce(delay, tick),
            () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reads the record, allowing the writer's replace (<see cref="FileShare.ReadWrite" /> +
    /// <see cref="FileShare.Delete" />). The watch calls this only once Setup's session is gone, so
    /// it cannot race Setup's own write; the wide sharing mode is belt and braces for the cap read
    /// and for a record that another party is still replacing.
    /// </summary>
    internal static SetupResult? ReadRecord(string recordPath)
    {
        try
        {
            if (!File.Exists(recordPath))
                return null;

            using var stream = new FileStream(
                recordPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<SetupResult>(reader.ReadToEnd(), SetupJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IUninstallHandoffWatch Create() =>
        new UninstallHandoffWatch(_readRecord, _sessionLive, _schedule, _utcNow);

    private static IDisposable ScheduleOnce(TimeSpan delay, Action tick)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tick();
        };
        timer.Start();
        return new StopOnDispose(timer);
    }

    /// <summary><see cref="DispatcherTimer" /> stops but does not dispose; the watch disposes the
    /// handle before scheduling the next tick and when it ends.</summary>
    private sealed class StopOnDispose : IDisposable
    {
        private DispatcherTimer? _timer;

        public StopOnDispose(DispatcherTimer timer) => _timer = timer;

        public void Dispose() => Interlocked.Exchange(ref _timer, null)?.Stop();
    }
}
