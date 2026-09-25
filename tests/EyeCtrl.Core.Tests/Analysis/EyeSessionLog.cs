namespace EyeCtrl.Core.Tests.Analysis;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Parses <c>%APPDATA%/EyeCtrl/logs/eyectrl-*.log</c> into eye-only sessions and the in-vivo metrics
/// the debate pre-registered: activations, <c>Action:</c> completions, and quick abandonments per
/// 5 minutes of eye time.
/// <para>
/// The split is the load-bearing part. A "session" is one <c>Startup:</c> line to the next one in the
/// same file — a process lifetime — and the mouse provider's sessions are dropped: its cursor is
/// driven by <c>GetCursorPos</c> reading the position the gaze engine itself writes, so counting its
/// "activations" measures a loopback, not the user's eyes. What remains is the only in-vivo evidence
/// there is.
/// </para>
/// <para>
/// Read-only, and never writes: the app rotates these files under its own schedule.
/// </para>
/// </summary>
public static class EyeSessionLog
{
    /// <summary>Environment override for the log directory; tests point it at a copy.</summary>
    public const string DirectoryEnvironmentVariable = "EYECTRL_LOG_DIR";

    /// <summary>The provider a session must name to count as eye input.</summary>
    public const string EyeProvider = "StreamEngine";

    /// <summary>A close within this many seconds, with no action, is a "quick abandonment".</summary>
    public const int QuickAbandonmentSeconds = 3;

    /// <summary>State transitions that START an activation. The manual sub-menu does not start a new one.</summary>
    private static readonly string[] ActivationStates = ["ActionMenuVisible", "ManualSelectionActive"];

    /// <summary>States that belong to an already-started activation and do not close it.</summary>
    private static readonly string[] ActivationSubStates = ["ActionMenuVisible", "ManualSelectionActive", "ManualSelectionActionMenuVisible"];

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
    /// <param name="End">Timestamp of the last line before the next startup (or of the file's last line).</param>
    /// <param name="Provider">The provider named by the startup line.</param>
    /// <param name="Paused">Whether the session never left the Paused state (or said so at startup).</param>
    /// <param name="ActivationEntries">Transitions into <c>ActionMenuVisible</c> or <c>ManualSelectionActive</c>.</param>
    /// <param name="ActionEntries"><c>Action:</c> lines associated with an activation.</param>
    /// <param name="QuickAbandonments">Activations that closed with no action within <see cref="QuickAbandonmentSeconds"/> seconds.</param>
    /// <param name="File">Source log file, for traceability in the report.</param>
    public sealed record Session(
        DateTime Start,
        DateTime End,
        string Provider,
        bool Paused,
        int ActivationEntries,
        int ActionEntries,
        int QuickAbandonments,
        string File)
    {
        /// <summary>Session wall-clock duration in minutes — the denominator of every per-5-minute metric.</summary>
        public double Minutes => (End - Start).TotalMinutes;

        /// <summary>Whether this session is eye-only input that was not paused.</summary>
        public bool IsEyeSession => Provider == EyeProvider && !Paused;
    }

    /// <summary>Aggregate metrics over a set of sessions.</summary>
    /// <param name="SessionCount">Sessions aggregated.</param>
    /// <param name="EyeMinutes">Total eye time in minutes.</param>
    /// <param name="Activations">Activation entries.</param>
    /// <param name="Actions"><c>Action:</c> completions.</param>
    /// <param name="QuickAbandonments">Quick abandonments.</param>
    public sealed record Aggregate(int SessionCount, double EyeMinutes, int Activations, int Actions, int QuickAbandonments)
    {
        /// <summary>Actions per 5 minutes of eye time — the end-to-end success metric the in-vivo guard checks.</summary>
        public double ActionsPer5Minutes => EyeMinutes <= 0 ? 0 : Actions / EyeMinutes * 5.0;

        /// <summary>Quick abandonments per 5 minutes — the guard's cost metric.</summary>
        public double QuickAbandonmentsPer5Minutes => EyeMinutes <= 0 ? 0 : QuickAbandonments / EyeMinutes * 5.0;

        /// <summary>Activations per 5 minutes.</summary>
        public double ActivationsPer5Minutes => EyeMinutes <= 0 ? 0 : Activations / EyeMinutes * 5.0;
    }

    /// <summary>The log directory, honouring <see cref="DirectoryEnvironmentVariable"/>.</summary>
    public static string ResolveDirectory() =>
        Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "logs");

    /// <summary>True when the directory holds at least one <c>eyectrl-*.log</c> file.</summary>
    public static bool DirectoryHasLogs(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "eyectrl-*.log").Any();

    /// <summary>
    /// Parses every <c>eyectrl-*.log</c> in a directory, oldest file first.
    /// <para>
    /// Sessions are cut at the file boundary even though a process can outlive midnight: the daily
    /// rolling logger puts consecutive lines in consecutive files with nothing to say they are one
    /// process, and none of the eye sessions in the baseline spans midnight. A long session cut in two
    /// would be two sessions, which is explicit here rather than silently guessed.
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

    /// <summary>Parses one log file into its sessions. Exposed for tests with synthetic lines.</summary>
    public static List<Session> ParseFile(string path) => Parse(File.ReadLines(path), Path.GetFileName(path));

    /// <summary>Parses already-read lines. Exposed for tests with synthetic lines.</summary>
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
                // A startup closes the previous session at the previous line; the new one begins here.
                if (current is not null) sessions.Add(current.Build(file));
                bool? explicitPaused = null;
                var pausedMatch = PausedStartupPattern.Match(raw);
                if (pausedMatch.Success)
                    explicitPaused = pausedMatch.Groups["paused"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                current = new SessionBuilder(timestamp, startup.Groups["provider"].Value, explicitPaused);
                continue;
            }

            if (current is null) continue;
            current.End = timestamp;

            var state = StatePattern.Match(raw);
            if (state.Success) current.States.Add((timestamp, state.Groups["state"].Value));
            else if (ActionPattern.IsMatch(raw)) current.Actions.Add(timestamp);
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
        var eye = selected.Where(s => s.IsEyeSession).ToList();
        var aggregate = AggregateEyeSessions(eye);

        var sb = new StringBuilder();
        sb.Append("Eye-session baseline — ").AppendLine(directory);
        foreach (var s in selected.OrderBy(s => s.Start))
        {
            sb.Append("  ").Append(s.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append("  ").Append(s.Provider.PadRight(12))
              .Append(s.Paused ? " PAUSED " : "        ")
              .Append(s.Minutes.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(8)).Append(" min")
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
        public DateTime End { get; set; } = start;
        public List<(DateTime Timestamp, string State)> States { get; } = [];
        public List<DateTime> Actions { get; } = [];

        public Session Build(string file)
        {
            int activations = 0, actions = 0, quick = 0;
            for (int i = 0; i < States.Count; i++)
            {
                if (!ActivationStates.Contains(States[i].State)) continue;
                activations++;

                DateTime close = End;
                for (int j = i + 1; j < States.Count; j++)
                {
                    if (ActivationSubStates.Contains(States[j].State)) continue;
                    close = States[j].Timestamp;
                    break;
                }

                // An action lands a few milliseconds AFTER the state has already left the activation,
                // so the association window is the activation plus the abandonment grace.
                var windowEnd = close.AddSeconds(QuickAbandonmentSeconds);
                bool completed = Actions.Any(a => a >= States[i].Timestamp && a <= windowEnd);
                if (completed) actions++;
                else if ((close - States[i].Timestamp).TotalSeconds <= QuickAbandonmentSeconds) quick++;
            }

            bool paused = explicitPaused ?? (States.Count > 0 && States.All(s => s.State == "Paused"));
            return new Session(start, End, provider, paused, activations, actions, quick, file);
        }
    }
}
