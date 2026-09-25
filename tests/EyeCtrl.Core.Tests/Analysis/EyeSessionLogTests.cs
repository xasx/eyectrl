namespace EyeCtrl.Core.Tests.Analysis;

/// <summary>
/// Teeth for the in-vivo log instrument: session splitting (including the next process's pre-Startup
/// lines), paused-time subtraction, the activation / action / quick-abandonment definition, and the
/// frozen snapshot baseline when the log snapshot is present.
/// </summary>
public class EyeSessionLogTests(ITestOutputHelper output)
{
    /// <summary>
    /// The log set the debate measured: sessions starting before this date. Later sessions (future use)
    /// are excluded from the pinned baseline so this test does not break when the user runs the app.
    /// </summary>
    private static readonly DateTime BaselineWindowEnd = new(2026, 9, 25);

    private static bool InBaselineWindow(EyeSessionLog.Session s) => s.Start < BaselineWindowEnd;

    // ── Synthetic lines ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_EndsASessionAtItsOwnLastLine_NotAtTheNextProcesssFirstLine()
    {
        // The shape of every real launch: a provider-init line 40 ms before the next Startup. It must
        // belong to the STARTING process; attributing it to the previous session is what turned one
        // real Mouse session into 1,301 minutes.
        var lines = new[]
        {
            "2026-09-16 23:50:15.519 [INF] [EyeCtrl.Platform.Gaze.GazeProviderFactory] Using Tobii Stream Engine",
            "2026-09-16 23:50:15.578 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", settings=C:\\x",
            "2026-09-16 23:50:16.000 [INF] [A] State \u2192 \"Fixation\"",
            "2026-09-16 23:51:43.000 [INF] [] Keyboard hidden",
            "2026-09-16 23:55:00.119 [INF] [EyeCtrl.Platform.Gaze.GazeProviderFactory] Using Tobii Stream Engine",
            "2026-09-16 23:55:00.152 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", settings=C:\\x",
            "2026-09-16 23:55:01.000 [INF] [A] State \u2192 \"Fixation\"",
        };
        var sessions = EyeSessionLog.Parse(lines, "synthetic.log");
        Check.That(sessions).HasSize(2);

        Check.That(sessions[0].End).IsEqualTo(new DateTime(2026, 9, 16, 23, 51, 43));
        Check.That(sessions[0].SpanMinutes).IsCloseTo(1.458, 0.01);
        // The second session still starts at its Startup line, not at the provider-init line.
        Check.That(sessions[1].Start).IsEqualTo(new DateTime(2026, 9, 16, 23, 55, 0, 152));
    }

    [Fact]
    public void Parse_ARelaunchHoursLater_DoesNotExtendThePreviousSession()
    {
        var lines = new[]
        {
            "2026-09-16 23:50:00.000 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", settings=C:\\x",
            "2026-09-16 23:50:05.000 [INF] [A] State \u2192 \"FreeLook\"",
            "2026-09-17 03:00:00.000 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"Mouse\", settings=C:\\x",
        };
        var sessions = EyeSessionLog.Parse(lines, "synthetic.log");
        Check.That(sessions).HasSize(2);
        Check.That(sessions[0].End).IsEqualTo(new DateTime(2026, 9, 16, 23, 50, 5));
        Check.That(sessions[0].SpanMinutes).IsCloseTo(0.083, 0.01);
    }

    [Fact]
    public void Parse_SubtractsPausedIntervalsFromEyeTime()
    {
        // The 09-16 23:52 shape: paused 36 s before the session's own end, never resumed.
        var lines = new[]
        {
            "2026-09-16 23:53:00.000 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", settings=C:\\x",
            "2026-09-16 23:53:10.000 [INF] [A] State \u2192 \"FreeLook\"",
            "2026-09-16 23:53:34.000 [INF] [A] State \u2192 \"Paused\"",
            "2026-09-16 23:54:00.000 [INF] [] Keyboard hidden",
        };
        var sessions = EyeSessionLog.Parse(lines, "synthetic.log");
        Check.That(sessions).HasSize(1);
        Check.That(sessions[0].SpanMinutes).IsCloseTo(1.0, 0.001);
        Check.That(sessions[0].PausedMinutes).IsCloseTo(26.0 / 60.0, 0.001);
        Check.That(sessions[0].Minutes).IsCloseTo(34.0 / 60.0, 0.001);
    }

    [Fact]
    public void Parse_OneActivationPerActivation_NotPerSubStateEntry()
    {
        // ActionMenuVisible + Action; then a manual activation whose sub-menu opens and returns —
        // the return is not a new activation.
        var lines = new[]
        {
            "2026-09-16 23:50:15.578 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", settings=C:\\x",
            "2026-09-16 23:50:16.000 [INF] [A] State \u2192 \"Fixation\"",
            "2026-09-16 23:50:20.000 [INF] [A] State \u2192 \"ActionMenuVisible\"",
            "2026-09-16 23:50:22.000 [INF] [A] State \u2192 \"FreeLook\"",
            "2026-09-16 23:50:22.010 [INF] [] Action: \"LeftClick\" at (10, 10) [PreciseTarget=true]",
            "2026-09-16 23:50:30.000 [INF] [A] State \u2192 \"ManualSelectionActive\"",
            "2026-09-16 23:50:31.000 [INF] [A] State \u2192 \"ManualSelectionActionMenuVisible\"",
            "2026-09-16 23:50:32.000 [INF] [A] State \u2192 \"ManualSelectionActive\"",
            "2026-09-16 23:50:34.000 [INF] [A] State \u2192 \"FreeLook\"",
            "2026-09-16 23:50:40.000 [INF] [A] State \u2192 \"ManualSelectionActive\"",
            "2026-09-16 23:50:41.000 [INF] [A] State \u2192 \"FreeLook\"",
        };
        var sessions = EyeSessionLog.Parse(lines, "synthetic.log");
        var session = sessions.Single();

        // Three activations, not four: the sub-menu return is not a new one. The 4 s close is a slow
        // abandonment (outside the 3 s cap); the final 1 s close is quick.
        Check.That(session.ActivationEntries).IsEqualTo(3);
        Check.That(session.ActionEntries).IsEqualTo(1);
        Check.That(session.QuickAbandonments).IsEqualTo(1);
    }

    [Fact]
    public void Parse_InfersPaused_WhenTheSessionNeverLeftThePausedState()
    {
        var lines = new[]
        {
            "2026-09-24 01:46:24.022 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", settings=C:\\x",
            "2026-09-24 01:46:24.491 [INF] [A] State \u2192 \"Paused\"",
            "2026-09-24 03:29:31.068 [INF] [] Shutdown",
        };
        var sessions = EyeSessionLog.Parse(lines, "paused.log");
        Check.That(sessions).HasSize(1);
        Check.That(sessions[0].Paused).IsTrue();
        Check.That(sessions[0].SpanMinutes).IsCloseTo(103.1, 0.2);
        Check.That(sessions[0].Minutes).IsCloseTo(0.0, 0.2);
    }

    [Fact]
    public void ResolveDirectory_IsNullWithoutTheEnvironmentVariable()
    {
        // There is deliberately no live-%APPDATA% fallback: the rolling logger deletes the oldest day,
        // so a default would silently change the baseline the pin asserts. The variable is restored so
        // this test cannot clobber the snapshot test later in the same collection.
        var original = Environment.GetEnvironmentVariable(EyeSessionLog.DirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EyeSessionLog.DirectoryEnvironmentVariable, null);
            Check.That(EyeSessionLog.ResolveDirectory()).IsNull();
            Check.That(EyeSessionLog.DirectoryHasLogs(null)).IsFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EyeSessionLog.DirectoryEnvironmentVariable, original);
        }
    }

    // ── Real snapshot ─────────────────────────────────────────────────────────

    [Fact]
    public void FrozenSnapshot_ReproducesTheCorrectedEyeOnlyBaseline()
    {
        var directory = EyeSessionLog.ResolveDirectory();
        Assert.SkipWhen(!EyeSessionLog.DirectoryHasLogs(directory),
            "No log snapshot. Set " + EyeSessionLog.DirectoryEnvironmentVariable
            + " to a COPY of the log directory (the app rotates the live one).");

        var sessions = EyeSessionLog.ParseDirectory(directory!);
        var baseline = sessions.Where(InBaselineWindow).ToList();
        var aggregate = EyeSessionLog.AggregateEyeSessions(baseline);
        output.WriteLine(EyeSessionLog.BuildReport(directory!, baseline));

        // Seven unpaused StreamEngine sessions. Eye time is the corrected 12.23 minutes: each session
        // ends at its own last line (not at the next process's), and the 09-16 23:52 pause is excluded.
        // That is below reviewer A's 12.84 because the fixup additionally subtracts paused intervals.
        Check.That(aggregate.SessionCount).IsEqualTo(7);
        Check.That(aggregate.EyeMinutes).IsCloseTo(12.23, 0.1);
        Check.That(aggregate.Activations).IsEqualTo(30);
        Check.That(aggregate.Actions).IsEqualTo(4);
        Check.That(aggregate.ActionsPer5Minutes).IsCloseTo(1.64, 0.05);
        Check.That(aggregate.QuickAbandonments).IsEqualTo(19);
        Check.That(aggregate.QuickAbandonmentsPer5Minutes).IsCloseTo(7.77, 0.1);

        // The last session is StreamEngine on 09-24 and was paused for its whole span.
        var paused = baseline.Single(s => s.Provider == "StreamEngine" && s.Paused);
        Check.That(paused.Start.Date).IsEqualTo(new DateTime(2026, 9, 24));
        Check.That(paused.SpanMinutes).IsCloseTo(103.1, 0.2);
    }
}
