namespace EyeCtrl.Core.Tests.Analysis;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Parses <c>eyectrl-*.log</c> files into eye-only sessions and the in-vivo metrics the debate
/// pre-registered: activations, <c>Action:</c> completions, and quick abandonments per 5 minutes of
/// eye time.
/// <para>
/// The split is the load-bearing part, and the hard part is where a session ENDS. Only one of the 24
/// sessions in the baseline logs ever writes <c>Shutdown</c>, so the end has to be inferred: a session
/// ends at its own last line, never at the next process's first line. A new process writes a
/// provider-init line (<c>GazeProviderFactory … Using Tobii Stream Engine</c>) tens of milliseconds
/// before its <c>Startup:</c> line; attributing that line to the previous session would absorb the
/// entire idle gap — one real session measured 1,301 minutes that way, and a crashed-then-hours-later
/// relaunch would silently deflate every per-5-minute rate.
/// </para>
/// <para>
/// Paused intervals are subtracted from eye time: a paused session exercises nothing. And the mouse
/// provider's sessions are dropped: its cursor is driven by <c>GetCursorPos</c> reading the position
/// the gaze engine itself writes, so counting its "activations" measures a loopback, not the user's eyes.
/// </para>
/// <para>
/// Read-only, and never writes: the app rotates these files under its own schedule. Real-data callers
/// pass a copy via <see cref="DirectoryEnvironmentVariable"/> — there is deliberately no live-directory
/// fallback, because the rolling logger deletes the oldest day, which would silently change the baseline.
/// </para>
/// </summary>
public static class EyeSessionLog
{
    /// <summary>Environment variable naming a COPY of the log directory. Real-data tests skip when unset.</summary>
    public const string DirectoryEnvironmentVariable = "EYECTRL_LOG_DIR";

    /// <summary>The provider a session must name to count as eye input.</summary>
    public const string EyeProvider = "StreamEngine";

    /// <summary>A close within this many seconds, with no action, is a "quick abandonment".</summary>
    public const int QuickAbandonmentSeconds = 3;

    /// <summary>
    /// Lines within this window before a <c>Startup:</c> line belong to the STARTING process, not the
    /// previous session. The observed provider-init lead is 29-61 ms; one second is a wide margin and
    /// far below any real gap between a session's own last line and the next launch.
    /// </summary>
    public const int ProcessStartLeadMs = 1000;

    /// <summary>State transitions that START an activation. The manual sub-menu does not start a new one.</summary>
    private static readonly string[] ActivationStates = ["ActionMenuVisible", "ManualSelectionActive"];

    /// <summary>States that belong to an already-started activation and do not close it.</summary>
    private static readonly string[] ActivationSubStates =
        ["ActionMenuVisible", "ManualSelectionActive", "ManualSelectionActionMenuVisible"];

    private static readonly Regex StartupPattern = new(
        "Startup: EyeCtrl.*provider=\"(?<provider>[^\"]+)\"", RegexOptions.Compiled);

    private static readonly Regex StatePattern = new(
        "State . \"(?<state>[^\"]+)\"", RegexOptions.Compiled);

    private static readonly Regex ActionPattern = new(
        "] Action: \"", RegexOptions.Compiled);

    private static readonly Regex TimestampPattern = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})", RegexOptions.Compiled);

    private static readonly Regex PausedStartupPattern = new(
        @"paused=(?<paused>true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>One process lifetime as the log records it.</summary>
    /// <param name="Start">Timestamp of the <c>Startup:</c> line.</param>
    /// <param name="End">Timestamp of the session's OWN last line (never the next process's).</param>
    /// <param name="Provider">The provider named by the startup line.</param>
    /// <param name="Paused">Whether the session never left the Paused state (or said so at startup).</param>
    /// <param name="PausedMinutes">Total time spent in the Paused state; excluded from <see cref="Minutes"/>.</param>
    /// <param name="ActivationEntries">Transitions into <c>ActionMenuVisible</c> or <c>ManualSelectionActive</c> from outside an activation.</param>
    /// <param name="ActionEntries"><c>Action:</c> lines associated with an activation.</param>
    /// <param name="QuickAbandonments">Activations that closed with no action within <see cref="QuickAbandonmentSeconds"/> seconds.</param>
    /// <param name="File">Source log file, for traceability in the report.</param>
    public sealed record Session(
        DateTime Start,
        DateTime End,
        string Provider,
        bool Paused,
        double PausedMinutes,
        int ActivationEntries,
        int ActionEntries,
        int QuickAbandonments,
        string File)
    {
        /// <summary>Wall-clock span from startup to the session's own last line.</summary>
        public double SpanMinutes => (End - Start).TotalMinutes;

        /// <summary>Eye time: the span minus paused intervals — the denominator of every per-5-minute metric.</summary>
        public double Minutes => Math.Max(0, SpanMinutes - PausedMinutes);

        /// <summary>Whether this session is eye-only input that was not paused.</summary>
        public bool IsEyeSession => Provider == EyeProvider && !Paused;
    }

    /// <summary>Aggregate metrics over a set of sessions.</summary>
    public sealed record Aggregate(int SessionCount, double EyeMinutes, int Activations, int Actions, int QuickAbandonments)
    {
        /// <summary>Actions per 5 minutes of eye time — the end-to-end success metric the in-vivo guard checks.</summary>
        public double ActionsPer5Minutes => EyeMinutes <= 0 ? 0 : Actions / EyeMinutes * 5.0;

        /// <summary>Quick abandonments per 5 minutes — the guard's cost metric.</summary>
        public double QuickAbandonmentsPer5Minutes => EyeMinutes <= 0 ? 0 : QuickAbandonments / EyeMinutes * 5.0;

        /// <summary>Activations per 5 minutes.</summary>
        public double ActivationsPer5Minutes => EyeMinutes <= 0 ? 0 : Activations / EyeMinutes * 5.0;
    }

    /// <summary>The log directory named by <see cref="DirectoryEnvironmentVariable"/>, or null when unset.</summary>
    public static string? ResolveDirectory() =>
        Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } custom
            ? custom
            : null;

    /// <summary>True when the directory holds at least one <c>eyectrl-*.log</c> file.</summary>
    public static bool DirectoryHasLogs(string? directory) =>
        !string.IsNullOrEmpty(directory)
        && Directory.Exists(directory)
        && Directory.EnumerateFiles(directory, "eyectrl-*.log").Any();

    /// <summary>
    /// Parses every <c>eyectrl-*.log</c> in a directory, oldest file first.
    /// <para>
    /// Sessions are cut at the file boundary even though a process can outlive midnight: the daily
    /// rolling logger puts consecutive lines in consecutive files with nothing to say they are one
    /// process. None of the eye sessions in the baseline spans midnight.
    /// </para>
    /// </summary>
    public static List<Session> ParseDirectory(string directory)
    {
        var sessions = new List<Session>();
        if (!Directory.Exists(directory)) return sessions;

        foreach (var file in Directory.EnumerateFiles(directory, "eyectrl-*.log").OrderBy(f => f, StringComparer.Ordinal))
            sessions.AddRange(ParseFile(file));
        return sessions;
    }

    /// <summary>Parses one log file into its sessions.</summary>
    public static List<Session> ParseFile(string path) => Parse(File.ReadLines(path), Path.GetFileName(path));

    /// <summary>
    /// Parses already-read lines. Exposed for tests with synthetic lines. A session ends at its own
    /// last line: lines within <see cref="ProcessStartLeadMs"/> before a <c>Startup:</c> belong to the
    /// starting process and are trimmed from the previous session.
    /// </summary>
    public static List<Session> Parse(IEnumerable<string> lines, string file = "(inline)")
    {
        var sessions = new List<Session>();
        SessionBuilder? current = null;

        foreach (var raw in lines)
        {
            var match = TimestampPattern.Match(raw);
            if (!match.Success) continue;
            if (!DateTime.TryParseExact(match.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                continue;

            var startup = StartupPattern.Match(raw);
            if (startup.Success)
            {
                // The starting process's own pre-Startup lines are not the previous session's activity.
                current?.TrimProcessStart(timestamp);
                if (current is not null) sessions.Add(current.Build(file));
                bool? explicitPaused = null;
                var pausedMatch = PausedStartupPattern.Match(raw);
                if (pausedMatch.Success)
                    explicitPaused = pausedMatch.Groups["paused"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                current = new SessionBuilder(timestamp, startup.Groups["provider"].Value, explicitPaused);
                continue;
            }

            // Lines before the first Startup in a file belong to that first process's own start and
            // never to a previous session (there is none in this file).
            current?.Add(timestamp, raw);
        }

        if (current is not null) sessions.Add(current.Build(file));
        return sessions;
    }

    /// <summary>Aggregates the unpaused eye sessions in <paramref name="sessions"/>.</summary>
    public static Aggregate AggregateEyeSessions(IEnumerable<Session> sessions)
    {
        var eye = sessions.Where(s => s.IsEyeSession).ToList();
        return new Aggregate(
            eye.Count,
            eye.Sum(s => s.Minutes),
            eye.Sum(s => s.ActivationEntries),
            eye.Sum(s => s.ActionEntries),
            eye.Sum(s => s.QuickAbandonments));
    }

    /// <summary>Renders the baseline table for the report, one line per session plus the aggregate.</summary>
    public static string BuildReport(string directory, IReadOnlyList<Session> sessions, Func<Session, bool>? filter = null)
    {
        var selected = (filter is null ? sessions : sessions.Where(filter)).ToList();
        var aggregate = AggregateEyeSessions(selected);

        var sb = new StringBuilder();
        sb.Append("Eye-session baseline — ").AppendLine(directory);
        foreach (var s in selected.OrderBy(s => s.Start))
        {
            sb.Append("  ").Append(s.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append("  ").Append(s.Provider.PadRight(12))
              .Append(s.Paused ? " PAUSED " : "        ")
              .Append(s.Minutes.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7)).Append(" min")
              .Append(s.PausedMinutes >= 0.05
                  ? " (paused " + s.PausedMinutes.ToString("0.0", CultureInfo.InvariantCulture) + ")"
                  : "          ")
              .Append("  activations ").Append(s.ActivationEntries.ToString(CultureInfo.InvariantCulture).PadLeft(3))
              .Append("  actions ").Append(s.ActionEntries.ToString(CultureInfo.InvariantCulture).PadLeft(2))
              .Append("  quick ").Append(s.QuickAbandonments.ToString(CultureInfo.InvariantCulture).PadLeft(3))
              .Append("  ").AppendLine(s.File);
        }
        sb.AppendLine();
        sb.Append("  unpaused eye sessions: ").Append(aggregate.SessionCount)
          .AppendLine();
        sb.Append("  eye time: ").Append(aggregate.EyeMinutes.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(" min");
        sb.Append("  activations: ").Append(aggregate.Activations)
          .Append(" (").Append(aggregate.ActivationsPer5Minutes.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(" / 5 min)");
        sb.Append("  Actions: ").Append(aggregate.Actions)
          .Append(" (").Append(aggregate.ActionsPer5Minutes.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(" / 5 min)");
        sb.Append("  quick abandonments: ").Append(aggregate.QuickAbandonments)
          .Append(" (").Append(aggregate.QuickAbandonmentsPer5Minutes.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(" / 5 min)");
        return sb.ToString();
    }

    private sealed class SessionBuilder(DateTime start, string provider, bool? explicitPaused)
    {
        private readonly List<DateTime> _timestamps = [];
        private readonly List<(DateTime Timestamp, string State)> _states = [];
        private readonly List<DateTime> _actions = [];

        public void Add(DateTime timestamp, string line)
        {
            _timestamps.Add(timestamp);
            var state = StatePattern.Match(line);
            if (state.Success) _states.Add((timestamp, state.Groups["state"].Value));
            else if (ActionPattern.IsMatch(line)) _actions.Add(timestamp);
        }

        /// <summary>
        /// Drops trailing lines that belong to the process starting at <paramref name="nextStartup"/>:
        /// its provider-init lines land milliseconds before its Startup line and must not extend this
        /// session's end.
        /// </summary>
        public void TrimProcessStart(DateTime nextStartup)
        {
            var cutoff = nextStartup.AddMilliseconds(-ProcessStartLeadMs);
            _timestamps.RemoveAll(ts => ts > cutoff);
            _states.RemoveAll(s => s.Timestamp > cutoff);
            _actions.RemoveAll(a => a > cutoff);
        }

        public Session Build(string file)
        {
            var end = _timestamps.Count > 0 ? _timestamps[^1] : start;
            if (end < start) end = start;

            // Paused intervals: from each entry into Paused to the next non-Paused transition, or to
            // the session's own end when it finished paused.
            double pausedMinutes = 0;
            DateTime? pausedAt = null;
            foreach (var (timestamp, state) in _states)
            {
                if (state == "Paused") pausedAt ??= timestamp;
                else if (pausedAt is { } since)
                {
                    pausedMinutes += (timestamp - since).TotalMinutes;
                    pausedAt = null;
                }
            }
            if (pausedAt is { } open) pausedMinutes += (end - open).TotalMinutes;

            // Activations: an entry into an activation state from OUTSIDE one. The sub-menu state and
            // the return from it stay inside the same activation.
            int activations = 0, actions = 0, quick = 0;
            bool active = false;
            DateTime activationStart = default;
            void Close(DateTime closedAt)
            {
                bool completed = _actions.Any(a => a >= activationStart && a <= closedAt.AddSeconds(QuickAbandonmentSeconds));
                if (completed) actions++;
                else if ((closedAt - activationStart).TotalSeconds <= QuickAbandonmentSeconds) quick++;
            }

            foreach (var (timestamp, state) in _states)
            {
                bool entry = ActivationStates.Contains(state);
                bool inside = ActivationSubStates.Contains(state);
                if (entry && !active)
                {
                    activations++;
                    activationStart = timestamp;
                    active = true;
                }
                else if (!inside && active)
                {
                    Close(timestamp);
                    active = false;
                }
            }
            if (active) Close(end);

            bool paused = explicitPaused ?? (_states.Count > 0 && _states.All(s => s.State == "Paused"));
            return new Session(start, end, provider, paused, pausedMinutes, activations, actions, quick, file);
        }
    }
}
