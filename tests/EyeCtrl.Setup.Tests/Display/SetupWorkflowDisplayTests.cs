namespace EyeCtrl.Setup.Tests.Display;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Display;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;

/// <summary>
/// The workflow's display seam: update and rollback report verify → stop → swap → start and then
/// the terminal result; a plain uninstall reports stop; <c>--quiet</c>, repair and a hand-off show
/// nothing; a blocked uninstall still shows its result (needsHelp) before asking anything.
/// </summary>
public class SetupWorkflowDisplayTests
{
    [Fact]
    public void Update_ReportsEveryStage_ThenTheResult()
    {
        var harness = new SetupHarness();
        var factory = new RecordingDisplayFactory();
        var package = SeedUpdatable(harness);
        var workflow = Workflow(harness, factory);

        var result = workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        var display = factory.Single(SetupVerb.Update);
        Check.That(display.Stages).ContainsExactly(
            SetupStage.Verify, SetupStage.Stop, SetupStage.Swap, SetupStage.Start);
        Check.That(display.Results).HasSize(1);
        Check.That(display.Results[0].Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(display.Disposed).IsTrue();
    }

    [Fact]
    public void TheTerminalLine_IsWrittenBeforeTheResultWindowWaits()
    {
        // O7: the line used to be stamped after display.Finish, which waits 10 s (60 s after a
        // failure), so correlating the log with the record needed that offset. The line now shares
        // the record's instant, and a separate close line records when the window ended.
        var harness = new SetupHarness();
        var factory = new RecordingDisplayFactory(harness.Logging);
        var package = SeedUpdatable(harness);
        var workflow = Workflow(harness, factory);

        var result = workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        var display = factory.Single(SetupVerb.Update);
        Check.WithCustomMessage("The terminal line was not in the log when the result window was shown.")
            .That(display.LinesAtFinish).Contains("INF Update: Ok");
        Check.WithCustomMessage("The terminal line was written after the result window's wait.")
            .That(display.LinesAtFinish.Last()).IsEqualTo("INF Update: Ok");
        Check.That(harness.Logging.Lines.Last()).IsEqualTo("INF Update: result window closed.");
        Check.WithCustomMessage("The close line is not after the terminal line.")
            .That(harness.Logging.Lines.IndexOf("INF Update: Ok"))
            .IsStrictlyLessThan(harness.Logging.Lines.IndexOf("INF Update: result window closed."));
    }

    [Fact]
    public void Rollback_ReportsEveryStage_ThenTheResult()
    {
        var harness = new SetupHarness();
        var factory = new RecordingDisplayFactory();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        var workflow = Workflow(harness, factory);

        var result = workflow.Rollback();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        var display = factory.Single(SetupVerb.Rollback);
        Check.That(display.Stages).ContainsExactly(
            SetupStage.Verify, SetupStage.Stop, SetupStage.Swap, SetupStage.Start);
        Check.That(display.Results[0].Outcome).IsEqualTo(SetupOutcome.Ok);
    }

    [Fact]
    public void Uninstall_ReportsTheStopStage_ThenTheResult()
    {
        var harness = new SetupHarness();
        var factory = new RecordingDisplayFactory();
        harness.SeedInstalled("1.0.0");
        harness.Choice.Choice = UninstallChoice.Keep;
        var workflow = Workflow(harness, factory);

        var result = workflow.Uninstall(quiet: false, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
        var display = factory.Single(SetupVerb.Uninstall);
        Check.That(display.Stages).ContainsExactly(SetupStage.Stop);
        Check.That(display.Results[0].Detail).IsEqualTo("kept");
    }

    [Fact]
    public void QuietUninstall_CreatesNoDisplay_AndNeverAsks()
    {
        var harness = new SetupHarness();
        var factory = new RecordingDisplayFactory();
        harness.SeedInstalled("1.0.0");
        var workflow = Workflow(harness, factory);

        var result = workflow.Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
        Check.That(factory.Created).IsEmpty();
        Check.That(harness.Choice.Calls).IsEqualTo(0);
    }

    [Fact]
    public void Repair_CreatesNoDisplay()
    {
        var harness = new SetupHarness();
        var factory = new RecordingDisplayFactory();
        harness.SeedPrevious("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.Registry.SeedString(
            Microsoft.Win32.RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName, "\"run\" repair");
        var workflow = Workflow(harness, factory);

        var result = workflow.Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Repaired);
        Check.That(factory.Created).IsEmpty();
    }

    [Fact]
    public void AHandedOffVerb_DoesNotFinishTheDisplay()
    {
        var harness = new SetupHarness();
        var factory = new RecordingDisplayFactory();
        var package = SeedUpdatable(harness);
        // Running from EyeCtrl itself: the verb must hand off to a run copy and exit.
        harness.Environment.ProcessPath = harness.Paths.InstalledSetupExePath;
        var workflow = Workflow(harness, factory);

        var result = workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        var display = factory.Single(SetupVerb.Update);
        Check.WithCustomMessage("The parent process finished a display for work it handed off.")
            .That(display.Results).IsEmpty();
        Check.That(display.Stages).IsEmpty();
        Check.That(display.Disposed).IsTrue();
    }

    [Fact]
    public void ABlockedUninstall_ShowsNeedsHelp_AndNeverAsksOrChanges()
    {
        var harness = new SetupHarness();
        var factory = new RecordingDisplayFactory();
        harness.SeedInstalled("1.0.0");
        harness.SetPolicy(5);
        var workflow = Workflow(harness, factory);

        var result = workflow.Uninstall(quiet: false, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Blocked);
        Check.That(harness.Choice.Calls).IsEqualTo(0);
        Check.That(harness.Broker.Requests).IsEmpty();
        var display = factory.Single(SetupVerb.Uninstall);
        var text = SetupDisplayText.Result(
            EyeCtrl.Core.Localization.StringTable.EmbeddedEnglish, result, harness.Paths.AppDataRoot);
        Check.That(text.Text).IsEqualTo(EyeCtrl.Core.Localization.StringTable.EmbeddedEnglish.T("uninstall.needsHelp"));
        Check.That(display.Results[0].Outcome).IsEqualTo(SetupOutcome.Blocked);
    }

    private static string SeedUpdatable(SetupHarness harness)
    {
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        return package;
    }

    private static SetupWorkflow Workflow(SetupHarness harness, ISetupDisplayFactory factory) =>
        new(
            harness.Paths, harness.Policy, harness.Broker, harness.AppStart, harness.Planner,
            harness.Files, harness.Processes, harness.Choice, harness.RunIds, harness.Metadata,
            harness.Recorder, harness.Environment, harness.Clock, harness.Logging, factory);

    private sealed class RecordingDisplayFactory : ISetupDisplayFactory
    {
        private readonly RecordingSetupLog? _log;

        public RecordingDisplayFactory(RecordingSetupLog? log = null) => _log = log;

        public List<(SetupVerb Verb, RecordingDisplay Display)> Created { get; } = [];

        public ISetupDisplay Create(SetupVerb verb)
        {
            var display = new RecordingDisplay(_log);
            Created.Add((verb, display));
            return display;
        }

        public RecordingDisplay Single(SetupVerb verb)
        {
            Check.WithCustomMessage($"No display was created for {verb}.")
                .That(Created.Count(c => c.Verb == verb)).IsEqualTo(1);
            return Created.Single(c => c.Verb == verb).Display;
        }
    }

    private sealed class RecordingDisplay : ISetupDisplay
    {
        private readonly RecordingSetupLog? _log;

        public RecordingDisplay(RecordingSetupLog? log = null) => _log = log;

        public List<SetupStage> Stages { get; } = [];

        public List<SetupResult> Results { get; } = [];

        /// <summary>O7: the log as it stood when the result window was asked to finish.</summary>
        public List<string> LinesAtFinish { get; } = [];

        public bool Disposed { get; private set; }

        public void Stage(SetupStage stage) => Stages.Add(stage);

        public void Finish(SetupResult result)
        {
            Results.Add(result);
            if (_log is not null)
                LinesAtFinish.AddRange(_log.Lines);
        }

        public void Dispose() => Disposed = true;
    }
}
