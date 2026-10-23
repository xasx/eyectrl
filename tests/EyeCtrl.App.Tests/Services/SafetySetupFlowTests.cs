namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The one-time safety setup chain, headless: the modal is a recording seam, so the chain is
/// driven the way the dwell does (pick a choice of the card on screen) with no window. Every test
/// that asserts a settings write names the setting; the mutations that prove them are listed in
/// the ticket's report.
/// </summary>
public class SafetySetupFlowTests : IDisposable
{
    private readonly List<(string Message, IReadOnlyList<ModalChoice> Choices)> _cards = [];
    private readonly List<Action> _background = [];
    private readonly List<bool> _modeCalls = [];
    private readonly List<string> _tempFiles = [];
    private int _saves;

    private readonly EyeCtrlSettings _settings = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            File.Delete(f);
            File.Delete(f + ".tmp");
        }
    }

    // ── startup decision ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, false, true)]   // never answered, normal launch: ask
    [InlineData(true, false, false, false)]   // answered to the end: never again
    [InlineData(false, true, false, false)]   // --paused: a Supervisor recovery relaunch
    [InlineData(false, false, true, false)]   // --test-mode
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    public void ShouldPrompt_OnlyWhenNeverAnswered_AndNotPausedOrTestMode(
        bool done, bool paused, bool testMode, bool expected) =>
        Check.That(SafetySetupStartup.ShouldPrompt(done, paused, testMode)).IsEqualTo(expected);

    // ── the chain ────────────────────────────────────────────────────────────

    [Fact]
    public void Start_ShowsTheFirstStepCard_WithASingleCentredChoice()
    {
        var flow = Flow();
        flow.Start();

        Check.That(_cards).HasSize(1);
        Check.That(_cards[0].Message).IsEqualTo("Safety setup: question 1 of 5");
        Check.That(_cards[0].Choices).HasSize(1);
    }

    [Fact]
    public void TheChain_AsksTheFiveQuestionsInOrder_EachBehindAStepCard()
    {
        var flow = Flow();
        flow.Start();
        var asked = new List<string>();

        for (var q = 0; q < SafetySetupFlow.QuestionCount; q++)
        {
            Check.That(Top.Message).IsEqualTo($"Safety setup: question {q + 1} of 5");
            Pick("safety_next");
            asked.Add(Top.Message);
            Pick(Top.Choices[^1].Id);   // the last choice of each question: No / All
        }

        Check.That(asked).ContainsExactly(
            "Bring EyeCtrl back automatically after a crash?",
            "Start EyeCtrl when you sign in?\nIf another gaze pointer also starts at sign-in, you will see two.",
            "Play a sound when a dwell clicks?",
            "Show eye-tracking status?",
            "Settings view: Basic or All?");
    }

    [Fact]
    public void EveryQuestion_IsDirectlyPrecededByASingleChoiceCard_SoAGazeStillOnTheLastAnswerCannotAnswerIt()
    {
        // Modal dwell targets arm on registration: a question shown straight after another
        // would be answered by the gaze still resting on the previous answer's button. The step
        // card (one centred button) between them is the guard; two-choice cards are never adjacent.
        var flow = Flow();
        flow.Start();
        var sizes = new List<int>();

        for (var card = 0; card < 2 * SafetySetupFlow.QuestionCount; card++)
        {
            sizes.Add(Top.Choices.Count);
            Pick(Top.Choices[^1].Id);
        }

        Check.That(sizes).ContainsExactly(1, 2, 1, 2, 1, 2, 1, 2, 1, 2);
    }

    [Fact]
    public void Question2_StartOnBoot_YesAndNo()
    {
        AnswerThrough(1, "safety_yes");
        Check.That(_settings.General.StartOnBoot).IsTrue();

        var again = new EyeCtrlSettings { General = { StartOnBoot = true } };
        var flow = Flow(settings: again);
        flow.Start();
        Pick("safety_next"); Pick("safety_no");          // q1 No
        Pick("safety_next");                              // q2 asked
        Pick("safety_no");
        Check.That(again.General.StartOnBoot).IsFalse();
    }

    [Fact]
    public void Question3_DwellCue_YesAndNo()
    {
        AnswerThrough(2, "safety_yes");
        Check.That(_settings.Dwell.CueEnabled).IsTrue();

        var again = new EyeCtrlSettings { Dwell = { CueEnabled = true } };
        var flow = Flow(settings: again);
        flow.Start();
        Pick("safety_next"); Pick("safety_no");
        Pick("safety_next"); Pick("safety_no");
        Pick("safety_next"); Pick("safety_no");           // q3 No
        Check.That(again.Dwell.CueEnabled).IsFalse();
    }

    [Fact]
    public void Question4_TrackStatus_YesAndNo()
    {
        AnswerThrough(3, "safety_yes");
        Check.That(_settings.TrackStatus.Enabled).IsTrue();

        var again = new EyeCtrlSettings { TrackStatus = { Enabled = true } };
        var flow = Flow(settings: again);
        flow.Start();
        for (var i = 0; i < 4; i++) { Pick("safety_next"); Pick("safety_no"); }
        Check.That(again.TrackStatus.Enabled).IsFalse();
    }

    [Fact]
    public void Question5_SettingsView_BasicAndAll_GoThroughTheWindowsModeSwitch()
    {
        var flow = Flow();
        flow.Start();
        for (var i = 0; i < 4; i++) { Pick("safety_next"); Pick("safety_no"); }
        Pick("safety_next");
        Check.That(Top.Choices.Select(c => c.Id)).ContainsExactly("safety_basic", "safety_all");
        Pick("safety_basic");
        Check.That(_modeCalls).ContainsExactly(true);

        _cards.Clear();
        flow.Start();
        for (var i = 0; i < 4; i++) { Pick("safety_next"); Pick("safety_no"); }
        Pick("safety_next"); Pick("safety_all");
        Check.That(_modeCalls).ContainsExactly(true, false);
    }

    [Fact]
    public void Question1_Yes_RequestsCrashRecovery_NoLeavesItOff()
    {
        var supervision = WorkingSupervision(_settings);
        var flow = Flow(supervision: supervision);
        flow.Start();
        Pick("safety_next"); Pick("safety_no");
        Check.That(supervision.Enabled).IsFalse();

        _cards.Clear();
        flow.Start();
        Pick("safety_next"); Pick("safety_yes");
        Check.That(supervision.Enabled).IsTrue();
        Check.That(_settings.Supervision.Enabled).IsTrue();
    }

    [Fact]
    public void EveryAnswer_IsAppliedAtOnce_AndSavedThroughTheWindowsPath()
    {
        var flow = Flow();
        flow.Start();
        Pick("safety_next");
        Pick("safety_no");               // q1: no save needed for a No / no-op
        var before = _saves;
        Pick("safety_next");
        Pick("safety_yes");              // q2
        Check.That(_saves).IsStrictlyGreaterThan(before);
        Check.That(_settings.General.StartOnBoot).IsTrue();
    }

    // ── completion and abandonment ───────────────────────────────────────────

    [Fact]
    public void TheFlagIsSetAndSaved_OnlyWithTheLastAnswer()
    {
        var flow = Flow();
        flow.Start();
        for (var i = 0; i < 4; i++)
        {
            Pick("safety_next"); Pick("safety_no");
            Check.That(_settings.General.SafetySetupDone).IsFalse();
        }

        Pick("safety_next");
        var savesBefore = _saves;
        Pick("safety_basic");

        Check.That(_settings.General.SafetySetupDone).IsTrue();
        Check.That(_saves).IsStrictlyGreaterThan(savesBefore);
    }

    [Fact]
    public void AnAbandonedChain_KeepsTheEarlierAnswers_AndLeavesTheFlagFalse()
    {
        var flow = Flow();
        flow.Start();
        Pick("safety_next"); Pick("safety_no");          // q1
        Pick("safety_next"); Pick("safety_yes");         // q2 StartOnBoot
        Pick("safety_next"); Pick("safety_yes");         // q3 CueEnabled
        Pick("safety_next");                              // q4 shown …
        // … and the window closes here: nothing more is picked.

        Check.That(_settings.General.StartOnBoot).IsTrue();
        Check.That(_settings.Dwell.CueEnabled).IsTrue();
        Check.That(_settings.TrackStatus.Enabled).IsFalse();
        Check.That(_settings.General.SafetySetupDone).IsFalse();
    }

    [Fact]
    public void Start_RunsTheWholeChainAgain_WhateverTheFlagSays()
    {
        _settings.General.SafetySetupDone = true;
        var flow = Flow();

        flow.Start();

        Check.That(_cards).HasSize(1);
        Check.That(_cards[0].Message).IsEqualTo("Safety setup: question 1 of 5");
    }

    // ── crash recovery that fails ────────────────────────────────────────────

    [Fact]
    public void ACrashRecoveryThatCannotStart_ShowsItsExistingFailureText_ThenContinues()
    {
        var unavailable = new SupervisionToggleService(
            _settings,
            new SupervisorBringUp(_settings, () => { }, () => false, _ => { }, () => { }, () => false),
            new SupervisorStartupEnsure(_settings, () => { }, () => false, () => false),
            persistEnabled: _ => { }, supervisorInstalled: () => false, isSupervisorConnected: () => false,
            reconcileRunKey: () => { }, runInBackground: w => w(), postToUi: w => w());
        var flow = Flow(supervision: unavailable);
        flow.Start();
        Pick("safety_next");
        Pick("safety_yes");

        // The held failure comes before the next step card, with one acknowledgement.
        Check.That(Top.Message).IsEqualTo(
            "Automatic crash recovery is unavailable: the EyeCtrl Supervisor is not installed.");
        Check.That(Top.Choices.Select(c => c.Id)).ContainsExactly("safety_ok");

        Pick("safety_ok");
        Check.That(Top.Message).IsEqualTo("Safety setup: question 2 of 5");
        Check.That(_settings.Supervision.Enabled).IsFalse();
    }

    [Fact]
    public void AnAsyncFailure_WaitsForTheNextBoundary_NeverReplacingAQuestionOnScreen()
    {
        var supervision = FailingAsyncSupervision(_settings);
        var flow = Flow(supervision: supervision);
        flow.Start();
        Pick("safety_next");
        Pick("safety_yes");                               // bring-up is now in flight
        Pick("safety_next");                              // q2 is on screen …
        var questionOnScreen = Top.Message;
        RunBackground();                                  // … when the bring-up fails and rolls back

        Check.That(Top.Message).IsEqualTo(questionOnScreen);
        Pick("safety_no");                                // q2 answered

        Check.That(Top.Message).IsEqualTo("The Supervisor could not be started, so supervision is off.");
        Pick("safety_ok");
        Check.That(Top.Message).IsEqualTo("Safety setup: question 3 of 5");
    }

    [Fact]
    public void AnAsyncFailure_ThatArrivesAfterTheLastAnswer_IsShownAtOnce()
    {
        var supervision = FailingAsyncSupervision(_settings);
        var flow = Flow(supervision: supervision);
        flow.Start();
        Pick("safety_next"); Pick("safety_yes");
        for (var i = 0; i < 3; i++) { Pick("safety_next"); Pick("safety_no"); }
        Pick("safety_next"); Pick("safety_all");
        Check.That(_settings.General.SafetySetupDone).IsTrue();
        var shown = _cards.Count;

        RunBackground();

        Check.That(_cards).HasSize(shown + 1);
        Check.That(Top.Message).IsEqualTo("The Supervisor could not be started, so supervision is off.");
    }

    [Fact]
    public void AnAbandonedChain_IgnoresALateCrashRecoveryResult()
    {
        // The window closes after the last answer while crash recovery is still in flight: the
        // late failure must not raise a modal on the closed window's (disposed) view model. Without
        // Abandon this exact sequence shows it — AnAsyncFailure_ThatArrivesAfterTheLastAnswer.
        var supervision = FailingAsyncSupervision(_settings);
        var flow = Flow(supervision: supervision);
        flow.Start();
        Pick("safety_next"); Pick("safety_yes");
        for (var i = 0; i < 3; i++) { Pick("safety_next"); Pick("safety_no"); }
        Pick("safety_next"); Pick("safety_all");
        var shown = _cards.Count;

        flow.Abandon();
        RunBackground();

        Check.That(_cards).HasSize(shown);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private (string Message, IReadOnlyList<ModalChoice> Choices) Top => _cards[^1];

    /// <summary>Picks a choice of the card on screen the way the dwell does.</summary>
    private void Pick(string id)
    {
        var choice = Top.Choices.Single(c => c.Id == id);
        choice.OnChosen!.Invoke();
    }

    /// <summary>Answers questions 1…n−1 No, then question n with <paramref name="id"/>.</summary>
    private void AnswerThrough(int questionIndex, string id)
    {
        var flow = Flow();
        flow.Start();
        for (var i = 0; i < questionIndex; i++) { Pick("safety_next"); Pick("safety_no"); }
        Pick("safety_next");
        Pick(id);
    }

    private void RunBackground()
    {
        var work = _background.ToList();
        _background.Clear();
        foreach (var w in work) w();
    }

    private SafetySetupFlow Flow(EyeCtrlSettings? settings = null, SupervisionToggleService? supervision = null) =>
        new(settings ?? _settings, supervision,
            (message, choices) => _cards.Add((message, choices)),
            () => _saves++,
            _modeCalls.Add);

    private SupervisionToggleService WorkingSupervision(EyeCtrlSettings settings)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_a2_{Guid.NewGuid()}.json");
        _tempFiles.Add(path);
        var (_, toggle, _) = SupervisionComposition.Create(
            settings, path, new FakeStartupRegistrar(),
            enablePublishing: () => { },
            launchSupervisor: () => true,
            tryReachSupervisor: () => true,
            isSupervisorInstalled: () => true,
            isLaunchRegistered: () => true,
            isSupervisorConnected: () => true,
            runInBackground: work => work(),
            postToUi: work => work());
        return toggle;
    }

    /// <summary>A switch whose bring-up runs only when the test says so, and then fails (launch refused).</summary>
    private SupervisionToggleService FailingAsyncSupervision(EyeCtrlSettings settings) =>
        new(settings,
            new SupervisorBringUp(settings, () => { }, () => false, _ => { }, () => { }, () => false),
            new SupervisorStartupEnsure(settings, () => { }, () => false, () => false),
            persistEnabled: _ => { }, supervisorInstalled: () => true, isSupervisorConnected: () => false,
            reconcileRunKey: () => { }, runInBackground: _background.Add, postToUi: w => w());

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool IsRegistered => true;
        public void SetStartOnBoot(bool enabled) { }
    }
}
