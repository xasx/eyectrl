namespace EyeCtrl.Core.Tests.Analysis;

/// <summary>
/// Teeth for the in-vivo log instrument: session splitting, the paused flag, and the activation /
/// action / quick-abandonment definition, first on synthetic lines whose answer is known by hand, then
/// on the user's real logs when they are present.
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

    private static readonly string[] SyntheticLines =
    [
        "2026-09-16 23:50:15.578 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", settings=C:\\x",
        "2026-09-16 23:50:16.000 [INF] [A] State \u2192 \"Fixation\"",
        "2026-09-16 23:50:20.000 [INF] [A] State \u2192 \"ActionMenuVisible\"",
        "2026-09-16 23:50:22.000 [INF] [A] State \u2192 \"FreeLook\"",
        "2026-09-16 23:50:22.010 [INF] [] Action: \"LeftClick\" at (10, 10) [PreciseTarget=true]",
        "2026-09-16 23:50:30.000 [INF] [A] State \u2192 \"ManualSelectionActive\"",
        "2026-09-16 23:50:31.000 [INF] [A] State \u2192 \"FreeLook\"",
        "2026-09-16 23:50:40.000 [INF] [A] State \u2192 \"ManualSelectionActive\"",
        "2026-09-16 23:50:41.000 [INF] [A] State \u2192 \"ManualSelectionActionMenuVisible\"",
        "2026-09-16 23:50:42.000 [INF] [A] State \u2192 \"ManualSelectionActive\"",
        "2026-09-16 23:50:44.000 [INF] [A] State \u2192 \"FreeLook\"",
        "2026-09-16 23:51:00.000 [INF] [] Shutdown",
        "2026-09-16 23:55:00.000 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"Mouse\", settings=C:\\x",
        "2026-09-16 23:55:01.000 [INF] [A] State \u2192 \"ActionMenuVisible\"",
        "2026-09-16 23:55:03.000 [INF] [A] State \u2192 \"FreeLook\"",
        "2026-09-16 23:56:00.000 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", paused=true, settings=C:\\x",
        "2026-09-16 23:56:01.000 [INF] [A] State \u2192 \"Paused\"",
        "2026-09-16 23:58:00.000 [INF] [] Shutdown",
    ];

    [Fact]
    public void Parse_SplitsAtStartup_AndCountsTheAgreedDefinition()
    {
        var sessions = EyeSessionLog.Parse(SyntheticLines, "synthetic.log");
        Check.That(sessions).HasSize(3);

        // Cut at the next Startup: start 23:50:15.578 to the last line before 23:55:00.
        var eye = sessions[0];
        Check.That(eye.Provider).IsEqualTo("StreamEngine");
        Check.That(eye.Paused).IsFalse();
        Check.That(eye.End).IsEqualTo(new DateTime(2026, 9, 16, 23, 51, 0));

        // Four activation entries: ActionMenuVisible, then each entry into ManualSelectionActive
        // (the sub-menu does not start a new one; returning to ManualSelectionActive does).
        Check.That(eye.ActivationEntries).IsEqualTo(4);
        // One completion: the LeftClick lands 20 ms after the menu closed.
        Check.That(eye.ActionEntries).IsEqualTo(1);
        // Two quick abandonments: the 1 s close at 23:50:31 and the 2 s close at 23:50:44. The 4 s
        // close at 23:50:44 is a slow abandonment, which is not what the guard counts.
        Check.That(eye.QuickAbandonments).IsEqualTo(2);

        // The mouse provider's activation measures a cursor loopback and is never aggregated.
        var mouse = sessions[1];
        Check.That(mouse.Provider).IsEqualTo("Mouse");
        Check.That(mouse.ActivationEntries).IsEqualTo(1);
        Check.That(mouse.IsEyeSession).IsFalse();

        // Explicit paused=true wins even before the state inference would say anything.
        Check.That(sessions[2].Paused).IsTrue();
        Check.That(sessions[2].IsEyeSession).IsFalse();

        var aggregate = EyeSessionLog.AggregateEyeSessions(sessions);
        Check.That(aggregate.SessionCount).IsEqualTo(1);
        Check.That(aggregate.Activations).IsEqualTo(4);
        Check.That(aggregate.Actions).IsEqualTo(1);
        Check.That(aggregate.QuickAbandonments).IsEqualTo(2);
        Check.That(aggregate.ActionsPer5Minutes).IsCloseTo(1 / eye.Minutes * 5, 1e-9);
    }

    [Fact]
    public void Parse_InfersPaused_WhenTheSessionNeverLeftThePausedState()
    {
        // The 09-24 shape: the only State line is "Paused". No explicit paused= marker exists yet
        // (that line is ticket 1's job), so the parser has to read it off the transitions.
        var lines = new[]
        {
            "2026-09-24 01:46:24.022 [INF] [] Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\", settings=C:\\x",
            "2026-09-24 01:46:24.491 [INF] [A] State \u2192 \"Paused\"",
            "2026-09-24 03:29:31.068 [INF] [] Shutdown",
        };
        var sessions = EyeSessionLog.Parse(lines, "paused.log");
        Check.That(sessions).HasSize(1);
        Check.That(sessions[0].Paused).IsTrue();
        Check.That(sessions[0].Minutes).IsCloseTo(103.1, 0.2);
    }

    // ── Real logs ─────────────────────────────────────────────────────────────

    [Fact]
    public void RealLogs_ReproduceTheEyeOnlyBaseline()
    {
        var directory = EyeSessionLog.ResolveDirectory();
        Assert.SkipWhen(!EyeSessionLog.DirectoryHasLogs(directory),
            "No eyectrl-*.log files in " + directory + " (override with "
            + EyeSessionLog.DirectoryEnvironmentVariable + ").");

        var sessions = EyeSessionLog.ParseDirectory(directory);
        var baseline = sessions.Where(InBaselineWindow).ToList();
        var aggregate = EyeSessionLog.AggregateEyeSessions(baseline);
        output.WriteLine(EyeSessionLog.BuildReport(directory, baseline));

        // The eye-only split: seven unpaused StreamEngine sessions, about 18.6 minutes in total.
        Check.That(aggregate.SessionCount).IsEqualTo(7);
        Check.That(aggregate.EyeMinutes).IsCloseTo(18.58, 0.2);

        // The last session is StreamEngine on 09-24 and was paused for its whole 6,187 s.
        var paused = baseline.Single(s => s.Provider == "StreamEngine" && s.Paused);
        Check.That(paused.Start.Date).IsEqualTo(new DateTime(2026, 9, 24));
        Check.That(paused.Minutes).IsCloseTo(103.1, 0.2);

        // The in-vivo baseline the guard compares against: ~1.1 Actions and ~5.1 quick
        // abandonments per 5 minutes of eye time.
        Check.That(aggregate.Actions).IsEqualTo(4);
        Check.That(aggregate.ActionsPer5Minutes).IsCloseTo(1.08, 0.05);
        Check.That(aggregate.QuickAbandonments).IsEqualTo(19);
        Check.That(aggregate.QuickAbandonmentsPer5Minutes).IsCloseTo(5.11, 0.1);
    }
}
