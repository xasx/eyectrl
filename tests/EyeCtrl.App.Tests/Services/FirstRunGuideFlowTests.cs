namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

public class FirstRunGuideFlowTests
{
    private sealed class FakeSurface : IGuideSurface
    {
        public List<FirstRunGuideStep> Shown { get; } = new();
        public List<string> SafetyMessages { get; } = new();
        public IReadOnlyList<ModalChoice>? LastSafetyChoices;
        public int HideCount; public int ShowCount; public int CloseCount;
        public void ShowStep(FirstRunGuideStep step) => Shown.Add(step);
        public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
        {
            SafetyMessages.Add(message);
            LastSafetyChoices = choices;
        }
        public void HideCard() => HideCount++;
        public void ShowCard() => ShowCount++;
        public void Close() => CloseCount++;
    }

    private static FirstRunGuideFlow Flow(
        FakeSurface surface, EyeCtrlSettings settings, bool tracker = true, bool dockedToolbar = true)
        => new(settings, trackerPresent: tracker, toolbarTaskAvailable: dockedToolbar, surface,
               createSafetyFlow: (showModal, onEnded) => new SafetySetupFlow(settings, supervision: null,
                   showModal, applyAndSave: () => { }, setSimpleSettingsUi: _ => { }, onEnded: onEnded));

    [Fact]
    public void Start_ShowsWelcome_Then_AdvancesThroughAllSteps_ToFinish()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        var steps = new List<FirstRunGuideStep>();
        flow.StepEntered += steps.Add;
        GuideExitReason? exit = null;
        flow.Closed += reason => exit = reason;

        flow.Start();
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.Welcome);
        flow.Advance();                                   // Start button
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.MeetDwell);
        flow.NotifyTaskSucceeded();                       // practice dwell
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Advance();                                   // Continue
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Calibrate);
        flow.NotifyCalibrationEnded();                    // Idle/Cancelled
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.ToolbarTask);
        flow.NotifyTaskSucceeded();                       // summon
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Theme);
        flow.Advance();                                   // Continue
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Safety);
        Check.That(s.SafetyMessages).IsEmpty();            // transition card, no chain yet (r2 F8)

        flow.BeginSafetyQuestions();                       // the transition card's Begin
        Check.That(s.SafetyMessages).HasSize(1);           // question 0 arrived on the surface
        for (var i = 0; i < 4; i++)
            s.LastSafetyChoices!.Single(c => c.Id == "safety_yes").OnChosen!.Invoke();
        s.LastSafetyChoices!.Single(c => c.Id == "safety_basic").OnChosen!.Invoke();
        Check.That(settings.General.SafetySetupDone).IsTrue();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Done);
        flow.Advance();                                     // Finish (r1 F4)
        Check.That(exit).IsEqualTo(GuideExitReason.Finished);
        Check.That(s.CloseCount).IsEqualTo(1);
    }

    [Fact]
    public void NoTracker_OmitsCalibrate_AndFloatingToolbar_OmitsToolbarTask()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings, tracker: false, dockedToolbar: false);

        flow.Start();
        flow.Advance();
        flow.NotifyTaskSucceeded();
        flow.Advance();                                   // → Theme (Calibrate AND ToolbarTask omitted)
        Check.That(s.Shown).ContainsOnlyElementsThatMatch(
            st => st is FirstRunGuideStep.Welcome or FirstRunGuideStep.MeetDwell
                       or FirstRunGuideStep.DwellTime or FirstRunGuideStep.Theme);
    }

    [Fact]
    public void Skip_WorksOnTasksAndCalibrate_Only()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        flow.Start();
        flow.Advance();                                   // Welcome → MeetDwell
        flow.Skip();
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Skip();                                      // INVALID on DwellTime (Continue-only)
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Advance();                                   // → Calibrate
        flow.Skip();                                      // → ToolbarTask
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.ToolbarTask);
    }

    [Fact]
    public void Later_OnWelcome_AndChromeClose_AbandonWithoutTheFlag()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        GuideExitReason? exit = null;
        flow.Closed += reason => exit = reason;
        flow.Start();
        flow.Close();
        Check.That(s.CloseCount).IsEqualTo(1);
        Check.That(exit).IsEqualTo(GuideExitReason.Abandoned);
        Check.That(settings.General.SafetySetupDone).IsFalse();
    }

    [Fact]
    public void SafetyCard_RoutesThroughTheSurface_AndForwardingRunsTheChoice()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        flow.Start();
        flow.Advance(); flow.NotifyTaskSucceeded(); flow.Advance();
        flow.NotifyCalibrationEnded(); flow.NotifyTaskSucceeded(); flow.Advance();

        flow.BeginSafetyQuestions();
        Check.That(s.SafetyMessages.Count).IsEqualTo(1);
        s.LastSafetyChoices!.Single(c => c.Id == "safety_yes").OnChosen!.Invoke();
        Check.That(s.SafetyMessages.Count).IsEqualTo(2);
        Check.That(s.LastSafetyChoices!.Single(c => c.Id == "safety_no")).IsNotNull();
    }

    [Fact]
    public void Host_RefusesASecondFlow_WhileOneIsOpen()
    {
        var host = new FirstRunGuideHost();
        Check.That(host.TryBegin(out var lease1)).IsTrue();
        Check.That(host.TryBegin(out _)).IsFalse();
        lease1.Dispose();
        Check.That(host.TryBegin(out var lease2)).IsTrue();
        lease2.Dispose();
    }

    // ── the action-row table (S1) ────────────────────────────────────────────────────────────
    //
    // Mirrors GuideSurface.RowChoicesFor + OnGuideChoice's dispatch switch (SettingsUiCoordinator):
    // every step's row ids and the flow call each maps to, hard-coded. A row id whose dispatch
    // leaves the flow untouched is a silent no-op — and since the VM clears EVERY chrome target
    // before dispatching, a no-op strand is exactly what S1 was: ToolbarTask's Continue pressed
    // Advance() with no case for the step, and the card sat target-less under all the guide's
    // claims. A new step or a new row id must update this table.

    private sealed record Row(FirstRunGuideStep Step, string Id, string Effect, FirstRunGuideStep? ExpectedStep);

    private static readonly Row[] ActionRowTable =
    [
        new(FirstRunGuideStep.Welcome, "guide_start", "advance", FirstRunGuideStep.MeetDwell),
        new(FirstRunGuideStep.Welcome, "guide_later", "close-abandoned", null),
        new(FirstRunGuideStep.MeetDwell, "guide_dwell_skip", "skip", FirstRunGuideStep.DwellTime),
        new(FirstRunGuideStep.DwellTime, "guide_dwelltime_continue", "advance", FirstRunGuideStep.Calibrate),
        // guide_calibrate_now has NO flow-level call: the wiring maps it to StartCalibration and
        // the flow only moves on NotifyCalibrationEnded (or the refusal guard re-shows). The
        // flow-level strand check is that the step is escapable — via Skip, the row below.
        new(FirstRunGuideStep.Calibrate, "guide_calibrate_now", "wiring-side", null),
        new(FirstRunGuideStep.Calibrate, "guide_calibrate_skip", "skip", FirstRunGuideStep.ToolbarTask),
        new(FirstRunGuideStep.ToolbarTask, "guide_toolbar_continue", "advance", FirstRunGuideStep.Theme),
        new(FirstRunGuideStep.ToolbarTask, "guide_toolbar_skip", "skip", FirstRunGuideStep.Theme),
        new(FirstRunGuideStep.Theme, "guide_theme_continue", "advance", FirstRunGuideStep.Safety),
        new(FirstRunGuideStep.Safety, "guide_safety_begin", "safety-chain", null),
        new(FirstRunGuideStep.Done, "guide_done_finish", "close-finished", null),
    ];

    /// <summary>The wiring's row dispatch (OnGuideChoice), flow-side: which flow call each row
    /// id maps to. Kept next to the table so a drift between the two is one diff apart.</summary>
    private static void DispatchRow(FirstRunGuideFlow flow, string id)
    {
        switch (id)
        {
            case "guide_start":
            case "guide_dwelltime_continue":
            case "guide_toolbar_continue":
            case "guide_theme_continue":
                flow.Advance();
                break;
            case "guide_later": flow.Close(); break;
            case "guide_dwell_skip":
            case "guide_calibrate_skip":
            case "guide_toolbar_skip":
                flow.Skip();
                break;
            case "guide_calibrate_now": break;   // wiring-side: StartCalibration (see the row note)
            case "guide_safety_begin": flow.BeginSafetyQuestions(); break;
            case "guide_done_finish": flow.Finish(); break;
            default: throw new InvalidOperationException($"unmapped row id: {id}");
        }
    }

    /// <summary>A fresh flow driven to <paramref name="step"/> through ROW-mapped calls only —
    /// the route production takes (plus the chain's own buttons to get past Safety, whose
    /// OnChosen delegates ride the same dispatch).</summary>
    private static (FirstRunGuideFlow Flow, FakeSurface Surface, EyeCtrlSettings Settings) FlowAt(FirstRunGuideStep step)
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        flow.Start();
        if (step != FirstRunGuideStep.Welcome)
        {
            DispatchRow(flow, "guide_start");                        // → MeetDwell
            if (step != FirstRunGuideStep.MeetDwell)
            {
                DispatchRow(flow, "guide_dwell_skip");               // → DwellTime
                if (step != FirstRunGuideStep.DwellTime)
                {
                    DispatchRow(flow, "guide_dwelltime_continue");   // → Calibrate
                    if (step != FirstRunGuideStep.Calibrate)
                    {
                        DispatchRow(flow, "guide_calibrate_skip");   // → ToolbarTask
                        if (step != FirstRunGuideStep.ToolbarTask)
                        {
                            DispatchRow(flow, "guide_toolbar_skip"); // → Theme
                            if (step != FirstRunGuideStep.Theme)
                            {
                                DispatchRow(flow, "guide_theme_continue");   // → Safety
                                if (step == FirstRunGuideStep.Done)
                                {
                                    DispatchRow(flow, "guide_safety_begin"); // the chain starts
                                    for (var i = 0; i < 4; i++)
                                        s.LastSafetyChoices!.Single(c => c.Id == "safety_yes").OnChosen!.Invoke();
                                    s.LastSafetyChoices!.Single(c => c.Id == "safety_basic").OnChosen!.Invoke();
                                }
                            }
                        }
                    }
                }
            }
        }
        Check.That(s.Shown[^1]).IsEqualTo(step);
        return (flow, s, settings);
    }

    [Fact]
    public void EveryActionRowChoice_MovesTheFlow_NeverASilentNoOp()
    {
        foreach (var row in ActionRowTable)
        {
            var (flow, s, _) = FlowAt(row.Step);
            GuideExitReason? exit = null;
            flow.Closed += reason => exit = reason;

            DispatchRow(flow, row.Id);

            switch (row.Effect)
            {
                case "advance":
                case "skip":
                    Check.WithCustomMessage($"{row.Step}.{row.Id} must move the flow")
                        .That(s.Shown[^1]).IsEqualTo(row.ExpectedStep!.Value);
                    break;
                case "close-abandoned":
                    Check.WithCustomMessage($"{row.Step}.{row.Id} must close the flow")
                        .That(s.CloseCount).IsEqualTo(1);
                    Check.That(exit).IsEqualTo(GuideExitReason.Abandoned);
                    break;
                case "close-finished":
                    Check.WithCustomMessage($"{row.Step}.{row.Id} must close the flow")
                        .That(s.CloseCount).IsEqualTo(1);
                    Check.That(exit).IsEqualTo(GuideExitReason.Finished);
                    break;
                case "safety-chain":
                    Check.WithCustomMessage($"{row.Step}.{row.Id} must start the safety chain")
                        .That(s.SafetyMessages.Count).IsEqualTo(1);
                    break;
                case "wiring-side":
                    // guide_calibrate_now: no flow-level call — documented above, and the step is
                    // escapable (the calibrate_skip row proves it). Nothing to assert here beyond
                    // the dispatch not throwing.
                    break;
            }
        }
    }

    [Fact]
    public void Continue_OnToolbarTask_AdvancesToTheme_NotASilentNoOp()
    {
        // The S1 pin: ToolbarTask's Continue is the one row the plan's own flow test missed —
        // it drove the step with NotifyTaskSucceeded, which production never invokes on a summon.
        var (flow, s, _) = FlowAt(FirstRunGuideStep.ToolbarTask);
        var shownBefore = s.Shown.Count;

        flow.Advance();

        Check.That(s.Shown.Count).IsStrictlyGreaterThan(shownBefore);
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.Theme);
    }
}
