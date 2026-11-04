namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;

/// <summary>
/// S6c D9: Setup logged only to stderr, and the elevated children have no console at all, so a
/// crash left no trace. Every phase now appends a rolling <c>setup-*.log</c> under the App's log
/// folder, and an unhandled exception is logged and recorded as failed.
/// </summary>
public class SetupLogTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 11, 4, 22, 3, 0, TimeSpan.Zero);

    private static FileSetupLog Log(
        SetupHarness harness, string phase = "medium", string verb = "update", int pid = 1234) =>
        new(harness.Paths, harness.Files, harness.Guard,
            new SetupLogContext(phase, verb, pid), () => FixedNow);

    [Fact]
    public void TheFileLog_AppendsUnderAppData_WithPhaseVerbAndPid()
    {
        var harness = new SetupHarness();

        Log(harness).Info("staging verified");
        Log(harness).Warn("a warning");
        Log(harness).Error("a failure");

        var path = Path.Combine(harness.Paths.LogsRoot, "setup-20261104.log");
        Check.That(harness.Files.FileExists(path)).IsTrue();
        var text = harness.Files.ReadAllText(path);
        Check.That(text).Contains("staging verified");
        Check.That(text).Contains("a warning");
        Check.That(text).Contains("a failure");
        Check.That(text).Contains("[INF] [medium] [update] [pid 1234]");
        Check.That(text).Contains("[WRN] [medium] [update] [pid 1234]");
        Check.That(text).Contains("[ERR] [medium] [update] [pid 1234]");
    }

    [Fact]
    public void TheMediumAndElevatedPhases_AppendToTheSameDailyFile()
    {
        var harness = new SetupHarness();

        Log(harness, "medium", "update", 100).Info("medium line");
        Log(harness, "elevated", "apply-update", 200).Info("elevated line");

        var text = harness.Files.ReadAllText(
            Path.Combine(harness.Paths.LogsRoot, "setup-20261104.log"));
        Check.That(text).Contains("medium line");
        Check.That(text).Contains("elevated line");
        Check.That(text).Contains("[elevated] [apply-update] [pid 200]");
        Check.That(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)).HasSize(2);
    }

    [Fact]
    public void ADifferentDay_RollsToANewFile()
    {
        var harness = new SetupHarness();
        var first = Log(harness);
        first.Info("day one");

        var nextDay = new FileSetupLog(
            harness.Paths, harness.Files, harness.Guard,
            new SetupLogContext("medium", "update", 1234),
            () => FixedNow.AddDays(1));
        nextDay.Info("day two");

        Check.That(harness.Files.FileExists(
            Path.Combine(harness.Paths.LogsRoot, "setup-20261104.log"))).IsTrue();
        Check.That(harness.Files.FileExists(
            Path.Combine(harness.Paths.LogsRoot, "setup-20261105.log"))).IsTrue();
    }

    [Fact]
    public void ALogWriteThatFails_IsSwallowed()
    {
        var harness = new SetupHarness();
        harness.Files.AppendFailure = _ => true;

        Log(harness).Info("this cannot be written");

        Check.That(harness.Files.FileExists(
            Path.Combine(harness.Paths.LogsRoot, "setup-20261104.log"))).IsFalse();
    }

    [Fact]
    public void AnUnhandledException_IsLoggedAndRecordedAsFailed()
    {
        var log = new RecordingSetupLog();
        SetupResult? recorded = null;
        var command = SetupCommandLine.Parse(["update", @"C:\inbox\2.0.0"]);

        var exit = Program.HandleCrash(
            command, new InvalidOperationException("boom"), log, result => recorded = result);

        Check.That(exit).IsEqualTo(SetupExitCodes.Failed);
        Check.That(log.Lines.Any(line => line.Contains(
            "Unhandled exception: System.InvalidOperationException", StringComparison.Ordinal))).IsTrue();
        Check.That(recorded).IsNotNull();
        Check.That(recorded!.Verb).IsEqualTo(SetupVerb.Update);
        Check.That(recorded.Outcome).IsEqualTo(SetupOutcome.Failed);
        Check.That(recorded.Detail).IsEqualTo("unhandled:InvalidOperationException");
    }

    [Theory]
    [InlineData("prepare-update")]
    [InlineData("apply-update")]
    [InlineData("apply-rollback")]
    [InlineData("revert")]
    [InlineData("uninstall")]
    [InlineData("repair")]
    public void AnElevatedCrash_LogsButDoesNotWriteTheRecord(string job)
    {
        // N1: only the medium side writes setup-last.json. The elevated child logs, the medium
        // parent maps its Failed exit code and records the outcome.
        var log = new RecordingSetupLog();
        SetupResult? recorded = null;
        var args = job == "prepare-update"
            ? new[] { "--internal", job, @"C:\inbox\2.0.0", "--run-id", "r1" }
            : new[] { "--internal", job, "--run-id", "r1" };
        var command = SetupCommandLine.Parse(args);

        var exit = Program.HandleCrash(
            command, new InvalidOperationException("boom"), log, result => recorded = result);

        Check.That(exit).IsEqualTo(SetupExitCodes.Failed);
        Check.That(recorded).IsNull();
        Check.That(log.Lines.Any(line => line.Contains(
            "Unhandled exception: System.InvalidOperationException", StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void AHelpCrash_DoesNotRecordAFailure()
    {
        var log = new RecordingSetupLog();
        SetupResult? recorded = null;
        var command = SetupCommandLine.Parse(["--help"]);

        var exit = Program.HandleCrash(
            command, new InvalidOperationException("boom"), log, result => recorded = result);

        Check.That(exit).IsEqualTo(SetupExitCodes.Failed);
        Check.That(recorded).IsNull();
        Check.That(log.Lines).Not.IsEmpty();
    }

    [Theory]
    [InlineData(SetupJob.Update, "update", "medium")]
    [InlineData(SetupJob.InternalApplyUpdate, "apply-update", "elevated")]
    [InlineData(SetupJob.Rollback, "rollback", "medium")]
    public void ALogContext_NamesThePhaseAndVerb(SetupJob job, string verb, string phase)
    {
        var command = new ParsedSetupCommand(job, null, false, null, null, null, null);

        var context = SetupLogContext.For(command, 4711);

        Check.That(context.Phase).IsEqualTo(phase);
        Check.That(context.Verb).IsEqualTo(verb);
        Check.That(context.ProcessId).IsEqualTo(4711);
    }
}
