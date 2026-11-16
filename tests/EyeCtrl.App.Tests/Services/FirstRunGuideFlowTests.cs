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
}
