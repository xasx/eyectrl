namespace EyeCtrl.Core.Diagnostics;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using EyeCtrl.Core.Models;

/// <summary>
/// The stranding ledger (Wave D D1): the log-only record that lets a real day prove "never
/// stranded". It writes one grep-friendly <c>STRAND-E1</c> line per event and keeps the running
/// totals behind the daily summary — it never changes what the app does.
/// <para>
/// <b>What it records.</b> Every exit from <see cref="InteractionState.Paused"/> with
/// its source; every Backstage open and close with its source and how long it stayed open; an
/// unclean end of the previous session (a marker file survives a crash and is deleted on a clean
/// exit); refused second launches (an append-only side file written before logging exists, counted
/// by the running instance's next summary); and one daily summary at shutdown and at local
/// midnight. The summary totals a day's time paused, pauses left by gaze versus other paths,
/// Backstage time and closes by source, and refused second launches.
/// </para>
/// <para>
/// <b>Never on a hot path.</b> Every method here runs on a state transition or a user gesture —
/// never per gaze sample. The midnight timer is the only background work.
/// </para>
/// <para>
/// <b>Best effort, never behavioural.</b> File IO failures (a read-only profile, a locked log
/// file) are swallowed: a ledger that cannot write must not affect the app. The injected sink is
/// likewise contained. All public methods are thread-safe; the pause transitions arrive on the UI
/// thread while the midnight summary arrives on a timer thread.
/// </para>
/// </summary>
public sealed class StrandingLedger : IDisposable
{
    /// <summary>The stable, grep-friendly prefix of every line this class writes.</summary>
    public const string Prefix = "STRAND-E1";

    /// <summary>A source the caller could not name. Recorded rather than guessed at.</summary>
    public const string UnknownSource = "unknown";

    /// <summary>The closed set of pause-exit sources the app can produce today.</summary>
    public static class PauseSources
    {
        /// <summary>The toolbar's Pause/Play disc (a gaze dwell).</summary>
        public const string Toolbar = "toolbar";

        /// <summary>The Resume strip's disc (a gaze dwell).</summary>
        public const string Strip = "strip";

        /// <summary>The tray menu item (a mouse click, or TD Control's gaze mouse).</summary>
        public const string Tray = "tray";

        /// <summary>The sub-pixel probe's F5 resume (a keyboard key, settings-side only).</summary>
        public const string Probe = "probe";

        /// <summary>The pause ENTERED by a <c>--paused</c> or <c>general.startPaused</c> launch.</summary>
        public const string Startup = "startup";

        /// <summary>No caller named the source.</summary>
        public const string Unknown = UnknownSource;
    }

    /// <summary>The closed set of Backstage open/close sources the app can produce today.</summary>
    public static class BackstageSources
    {
        /// <summary>Opened from the toolbar's Backstage disc.</summary>
        public const string Toolbar = "toolbar";

        /// <summary>Shown again when the uninstall hand-off ends without removing EyeCtrl.</summary>
        public const string HandoffRestore = "handoff_restore";

        /// <summary>Shown again because the owner asked for Backstage while the hand-off hid it.</summary>
        public const string OwnerReopen = "owner_reopen";

        /// <summary>Closed by a gaze dwell on the round Close disc.</summary>
        public const string Disc = "disc";

        /// <summary>Closed by a mouse click on the round Close disc.</summary>
        public const string Mouse = "mouse";

        /// <summary>Closed by the global Esc hatch.</summary>
        public const string Esc = "esc";

        /// <summary>Closed by Alt+F4 (or any OS close of the window).</summary>
        public const string AltF4 = "altf4";

        /// <summary>Closed by a card that hands the screen back (Calibrate; Quit/Restart end the app).</summary>
        public const string Card = "card";

        /// <summary>Hidden for the uninstall hand-off; the same window returns on a non-uninstall end.</summary>
        public const string Handoff = "handoff";

        /// <summary>Closed because the app is exiting.</summary>
        public const string Shutdown = "shutdown";

        /// <summary>Closed through the <c>CloseBackstageMessage</c> path.</summary>
        public const string Message = "message";

        /// <summary>No caller named the source.</summary>
        public const string Unknown = UnknownSource;
    }

    private const string StampFormat = "yyyy-MM-ddTHH:mm:ss.fff";

    private readonly string _markerPath;
    private readonly string _refusedLaunchesPath;
    private readonly Action<string> _writeLine;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<DateOnly, DayStats> _days = new();

    private DateTime? _pausedSince;
    private DateTime? _backstageSince;
    private DateOnly? _lastFlushedDay;
    private Timer? _midnightTimer;
    private bool _started;
    private bool _ended;

    /// <summary>Creates the ledger over the app's own data folder. Tests pass a temp folder.</summary>
    /// <param name="markerPath">The session marker; it survives a crash and is deleted on clean exit.</param>
    /// <param name="refusedLaunchesPath">The append-only refused-launch side file.</param>
    /// <param name="writeLine">The Information sink, without the prefix (the ledger adds it).</param>
    /// <param name="now">The local clock; <see cref="DateTime.Now"/> when omitted.</param>
    public StrandingLedger(
        string markerPath,
        string refusedLaunchesPath,
        Action<string> writeLine,
        Func<DateTime>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(refusedLaunchesPath);
        _markerPath = markerPath;
        _refusedLaunchesPath = refusedLaunchesPath;
        _writeLine = writeLine ?? throw new ArgumentNullException(nameof(writeLine));
        _now = now ?? (static () => DateTime.Now);
    }

    /// <summary>The marker file under <paramref name="appDataDir"/>, named here so App and tests agree.</summary>
    public static string SessionMarkerPath(string appDataDir) =>
        Path.Combine(appDataDir, "strand-session.marker");

    /// <summary>The refused-launch side file under <paramref name="appDataDir"/>.</summary>
    public static string RefusedLaunchesPath(string appDataDir) =>
        Path.Combine(appDataDir, "strand-refused-launches.log");

    /// <summary>
    /// Starts the session: reports an unclean previous end (a marker still on disk), writes this
    /// session's marker, and arms the local-midnight summary. Call once, after logging exists.
    /// </summary>
    public void StartSession()
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
            var now = _now();

            var markerExisted = false;
            var previousStart = "-";
            try
            {
                if (File.Exists(_markerPath))
                {
                    markerExisted = true;
                    previousStart = ParseStamp(File.ReadAllText(_markerPath)) ?? "-";
                }
            }
            catch
            {
                // A marker we cannot read is still evidence that a previous session did not end
                // cleanly (the clean path deletes it). Never fail the start over it.
                markerExisted = File.Exists(_markerPath);
            }

            if (markerExisted)
                EmitLocked($"session_prev end=unclean prev_start={previousStart}");

            try
            {
                var directory = Path.GetDirectoryName(_markerPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(_markerPath, now.ToString(StampFormat, CultureInfo.InvariantCulture),
                    Utf8NoBom);
            }
            catch
            {
                // Best effort: without the marker the next start simply cannot report this session.
            }

            ArmMidnightLocked();
        }
    }

    /// <summary>Records that Paused was entered. Repeated calls while paused are no-ops.</summary>
    /// <param name="source">One of <see cref="PauseSources"/> (or a new closed id).</param>
    public void OnPauseEntered(string source)
    {
        _ = source;   // the entry names a source for future readers; today's lines are the exits
        lock (_gate)
        {
            if (_pausedSince is not null)
                return;
            var now = _now();
            _pausedSince = now;
            DayForLocked(DateOnly.FromDateTime(now)).Pauses++;
        }
    }

    /// <summary>
    /// Records the exit from Paused and writes the one line that names its source and duration.
    /// A no-op when no entry was recorded (the adapter can see a transition it did not open).
    /// </summary>
    public void OnPauseExited(string source)
    {
        lock (_gate)
        {
            if (_pausedSince is not { } since)
                return;

            _pausedSince = null;
            var now = _now();
            var named = Normalise(source);
            AddPausedLocked(since, now);

            var day = DayForLocked(DateOnly.FromDateTime(now));
            if (IsGazePauseSource(named))
                day.PausesLeftByGaze++;
            else
                day.PausesLeftByOther++;

            EmitLocked($"pause_exit src={named} paused_ms={MillisecondsBetween(since, now)}");
        }
    }

    /// <summary>Records a Backstage open and writes its line. Repeated calls while open are no-ops.</summary>
    /// <param name="source">One of <see cref="BackstageSources"/> (or a new closed id).</param>
    public void OnBackstageOpened(string source)
    {
        lock (_gate)
        {
            if (_backstageSince is not null)
                return;
            _backstageSince = _now();
            EmitLocked($"backstage_open src={Normalise(source)}");
        }
    }

    /// <summary>Records a Backstage close, writes its line, and adds the open time to the day.</summary>
    /// <param name="source">One of <see cref="BackstageSources"/> (or a new closed id).</param>
    public void OnBackstageClosed(string source)
    {
        lock (_gate)
        {
            if (_backstageSince is not { } since)
                return;

            _backstageSince = null;
            var now = _now();
            var named = Normalise(source);
            AddBackstageLocked(since, now);

            var day = DayForLocked(DateOnly.FromDateTime(now));
            day.BackstageCloses++;
            day.CloseBySource[named] = day.CloseBySource.TryGetValue(named, out var count) ? count + 1 : 1;

            EmitLocked($"backstage_close src={named} open_ms={MillisecondsBetween(since, now)}");
        }
    }

    /// <summary>
    /// Writes one summary line and drops the days it covered. Called with <c>midnight</c> by the
    /// internal timer (all days before today) and with <c>shutdown</c> by the exit sequence (all
    /// days up to and including today). Days already reported are never reported again.
    /// </summary>
    /// <param name="reason">A closed id: <c>midnight</c> or <c>shutdown</c>.</param>
    public void WriteDailySummary(string reason)
    {
        lock (_gate)
        {
            // A timer callback racing EndSession must not append a late midnight line after the
            // final shutdown summary (and the marker it already deleted).
            if (_ended)
                return;
            WriteDailySummaryLocked(Normalise(reason));
        }
    }

    /// <summary>
    /// Writes the final summary, stops the timer and deletes the session marker. Idempotent; the
    /// marker is deleted only after the summary, so a crash here is still reported as unclean.
    /// </summary>
    public void EndSession()
    {
        lock (_gate)
        {
            if (_ended)
                return;
            _ended = true;

            WriteDailySummaryLocked("shutdown");
            StopMidnightLocked();

            try
            {
                if (File.Exists(_markerPath))
                    File.Delete(_markerPath);
            }
            catch
            {
                // A marker we could not delete makes the next start report unclean — the safe lie.
            }
        }
    }

    /// <summary>
    /// Appends one refused-launch line to the side file. Runs in the duplicate instance BEFORE
    /// logging exists, so it writes the file directly and never throws. The running instance reads
    /// the file in its next daily summary — no IPC, and the exit itself is unchanged.
    /// </summary>
    /// <param name="appDataDir">The app's own data folder (never the owner's in a test).</param>
    /// <param name="now">The local time; the summary groups these lines by day.</param>
    /// <param name="kind">A closed id: <c>duplicate</c> or <c>restart</c>.</param>
    public static void RecordRefusedSecondLaunch(string appDataDir, DateTime now, string kind = "duplicate")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDir);
        try
        {
            var path = RefusedLaunchesPath(appDataDir);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(path,
                now.ToString(StampFormat, CultureInfo.InvariantCulture) + " kind=" + Normalise(kind)
                + Environment.NewLine,
                Utf8NoBom);
        }
        catch
        {
            // The refused launch still exits exactly as before; a lost line is not a behaviour.
        }
    }

    /// <summary>The next local midnight after <paramref name="now"/> (the midnight timer's due time).</summary>
    internal static DateTime NextMidnight(DateTime now) => now.Date.AddDays(1);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            StopMidnightLocked();
        }
    }

    private void WriteDailySummaryLocked(string reason)
    {
        var now = _now();
        var today = DateOnly.FromDateTime(now);

        var days = _days.Keys
            .Where(day => reason == "midnight" ? day < today : day <= today)
            .OrderBy(day => day)
            .ToList();

        if (reason == "midnight")
        {
            // A midnight summary exists even on a day with no events: an all-zero line is how a
            // reader proves the app was running and the ledger was alive.
            var yesterday = today.AddDays(-1);
            if (!days.Contains(yesterday))
            {
                days.Add(yesterday);
                days.Sort();
            }
        }
        else if (days.Count == 0 || days[^1] != today)
        {
            days.Add(today);
        }

        foreach (var day in days)
        {
            EmitLocked(SummaryLineLocked(reason, day, now));
            _days.Remove(day);
            _lastFlushedDay = day;
        }
    }

    private string SummaryLineLocked(string reason, DateOnly day, DateTime now)
    {
        var stats = _days.TryGetValue(day, out var existing) ? existing : new DayStats();
        var pausedMs = stats.PausedMs + ActiveOverlapMilliseconds(_pausedSince, day, now);
        var backstageMs = stats.BackstageMs + ActiveOverlapMilliseconds(_backstageSince, day, now);

        var line = new StringBuilder(Prefix.Length + 256);
        line.Append("day_summary reason=").Append(reason)
            .Append(" day=").Append(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(" paused_ms=").Append(pausedMs)
            .Append(" pauses=").Append(stats.Pauses)
            .Append(" left_gaze=").Append(stats.PausesLeftByGaze)
            .Append(" left_other=").Append(stats.PausesLeftByOther)
            .Append(" backstage_ms=").Append(backstageMs)
            .Append(" backstage_closes=").Append(stats.BackstageCloses);

        // A fixed order and a fixed key set: a log reader can diff two days without case work.
        AppendCloseCount(line, "bs_disc", stats, BackstageSources.Disc);
        AppendCloseCount(line, "bs_mouse", stats, BackstageSources.Mouse);
        AppendCloseCount(line, "bs_esc", stats, BackstageSources.Esc);
        AppendCloseCount(line, "bs_altf4", stats, BackstageSources.AltF4);
        AppendCloseCount(line, "bs_card", stats, BackstageSources.Card);
        AppendCloseCount(line, "bs_handoff", stats, BackstageSources.Handoff);
        AppendCloseCount(line, "bs_shutdown", stats, BackstageSources.Shutdown);
        AppendCloseCount(line, "bs_message", stats, BackstageSources.Message);

        var known = 0;
        foreach (var count in stats.CloseBySource.Values)
            known += count;
        line.Append(" bs_other=").Append(stats.BackstageCloses - known);

        line.Append(" refused=").Append(RefusedLaunchesOnLocked(day));
        return line.ToString();
    }

    private static void AppendCloseCount(StringBuilder line, string key, DayStats stats, string source)
    {
        line.Append(' ').Append(key).Append('=')
            .Append(stats.CloseBySource.TryGetValue(source, out var count) ? count : 0);
    }

    private int RefusedLaunchesOnLocked(DateOnly day)
    {
        try
        {
            if (!File.Exists(_refusedLaunchesPath))
                return 0;

            var count = 0;
            foreach (var line in File.ReadLines(_refusedLaunchesPath))
            {
                var space = line.IndexOf(' ');
                var stamp = space < 0 ? line : line[..space];
                if (DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var at)
                    && DateOnly.FromDateTime(at) == day)
                {
                    count++;
                }
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private void AddPausedLocked(DateTime from, DateTime to)
    {
        foreach (var (day, ms) in SplitByDay(from, to))
        {
            if (_lastFlushedDay is { } flushed && day <= flushed)
                continue;   // already counted by the summary that reported this day
            DayForLocked(day).PausedMs += ms;
        }
    }

    private void AddBackstageLocked(DateTime from, DateTime to)
    {
        foreach (var (day, ms) in SplitByDay(from, to))
        {
            if (_lastFlushedDay is { } flushed && day <= flushed)
                continue;
            DayForLocked(day).BackstageMs += ms;
        }
    }

    /// <summary>
    /// Splits an interval at local midnights, so a pause or Backstage visit that spans midnight is
    /// reported on both days rather than wholly on the exit's day.
    /// </summary>
    private static IEnumerable<(DateOnly Day, long Milliseconds)> SplitByDay(DateTime from, DateTime to)
    {
        if (to <= from)
            yield break;

        var cursor = from;
        while (cursor < to)
        {
            var day = DateOnly.FromDateTime(cursor);
            var dayEnd = cursor.Date.AddDays(1);
            var segmentEnd = to < dayEnd ? to : dayEnd;
            yield return (day, MillisecondsBetween(cursor, segmentEnd));
            cursor = segmentEnd;
        }
    }

    /// <summary>The part of [<paramref name="since"/>, now] that falls inside <paramref name="day"/>.</summary>
    private static long ActiveOverlapMilliseconds(DateTime? since, DateOnly day, DateTime now)
    {
        if (since is not { } from)
            return 0;

        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var start = from > dayStart ? from : dayStart;
        var end = now < dayEnd ? now : dayEnd;
        return end <= start ? 0 : MillisecondsBetween(start, end);
    }

    private static long MillisecondsBetween(DateTime from, DateTime to) =>
        to <= from ? 0 : (long)Math.Round((to - from).TotalMilliseconds);

    private static bool IsGazePauseSource(string source) =>
        source is PauseSources.Strip or PauseSources.Toolbar;

    private DayStats DayForLocked(DateOnly day)
    {
        if (!_days.TryGetValue(day, out var stats))
            _days[day] = stats = new DayStats();
        return stats;
    }

    private void EmitLocked(string line)
    {
        try
        {
            _writeLine(Prefix + " " + line);
        }
        catch
        {
            // The ledger must never be able to change what the app does.
        }
    }

    private void ArmMidnightLocked()
    {
        _midnightTimer ??= new Timer(static state => ((StrandingLedger)state!).OnMidnight(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var now = _now();
        var due = NextMidnight(now) - now;
        if (due <= TimeSpan.Zero)
            due = TimeSpan.FromMinutes(1);
        _midnightTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void OnMidnight()
    {
        try
        {
            WriteDailySummary("midnight");
        }
        catch
        {
            // A timer callback that throws would kill the process on some runtimes; the next
            // midnight (or the shutdown summary) still covers the ledger.
        }

        lock (_gate)
        {
            if (!_ended)
                ArmMidnightLocked();
        }
    }

    private void StopMidnightLocked()
    {
        _midnightTimer?.Dispose();
        _midnightTimer = null;
    }

    private static string Normalise(string? source) =>
        string.IsNullOrWhiteSpace(source) ? UnknownSource
        : source.IndexOfAny([' ', '\r', '\n', '=']) >= 0 ? UnknownSource
        : source;

    private static string? ParseStamp(string text)
    {
        var trimmed = text.Trim();
        return DateTime.TryParseExact(trimmed, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at)
            ? at.ToString(StampFormat, CultureInfo.InvariantCulture)
            : null;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private sealed class DayStats
    {
        public long PausedMs { get; set; }
        public int Pauses { get; set; }
        public int PausesLeftByGaze { get; set; }
        public int PausesLeftByOther { get; set; }
        public long BackstageMs { get; set; }
        public int BackstageCloses { get; set; }
        public Dictionary<string, int> CloseBySource { get; } = new(StringComparer.Ordinal);
    }
}
