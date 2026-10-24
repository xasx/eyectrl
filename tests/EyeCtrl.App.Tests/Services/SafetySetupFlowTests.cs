namespace EyeCtrl.App.Tests.Services;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

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
    private readonly List<bool> _ended = [];
    private readonly List<Scheduled> _scheduled = [];
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

    // ── startup decision and request ─────────────────────────────────────────

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

    [Theory]
    [InlineData(false, false, false, 1)]
    [InlineData(true, false, false, 0)]
    [InlineData(false, true, false, 0)]
    [InlineData(false, false, true, 0)]
    public void MaybeRequest_SendsTheRequest_ExactlyWhenItShouldPrompt(
        bool done, bool paused, bool testMode, int expectedSends)
    {
        var messenger = new WeakReferenceMessenger();
        var received = new RequestProbe();
        messenger.Register(received);

        var sent = SafetySetupStartup.MaybeRequest(done, paused, testMode, messenger);

        Check.That(received.Count).IsEqualTo(expectedSends);
        Check.That(sent).IsEqualTo(expectedSends == 1);
    }

    // ── coordinator launch ───────────────────────────────────────────────────

    [Fact]
    public void Launch_WithNoWindow_OpensOne_ThenStartsAChainThatClosesIt()
    {
        var steps = new List<string>();

        SafetySetupLaunch.Run(windowOpen: false,
            openWindow: () => steps.Add("open"),
            start: closeWhenDone => steps.Add($"start close={closeWhenDone}"));

        Check.That(steps).ContainsExactly("open", "start close=True");
    }

    [Fact]
    public void Launch_WithAWindowOpen_StartsOnIt_AndLeavesItOpen()
    {
        var steps = new List<string>();

        SafetySetupLaunch.Run(windowOpen: true,
            openWindow: () => steps.Add("open"),
            start: closeWhenDone => steps.Add($"start close={closeWhenDone}"));

        Check.That(steps).ContainsExactly("start close=False");
    }

    // ── the window's host ────────────────────────────────────────────────────

    [Fact]
    public void Host_NeverStartsOverAModalThatIsUp()
    {
        var created = 0;
        var host = new SafetySetupHost(() => true, onEnded => { created++; return Flow(onEnded: onEnded); }, () => { });

        Check.That(host.Start(closeWhenDone: true)).IsFalse();
        Check.That(created).IsEqualTo(0);
        Check.That(_cards).IsEmpty();
    }

    [Fact]
    public void Host_StartedByTheStartupRequest_ClosesTheWindow_AfterTheLastAnswer()
    {
        var closes = 0;
        var host = new SafetySetupHost(() => false, onEnded => Flow(onEnded: onEnded), () => closes++);

        host.Start(closeWhenDone: true);
        AnswerAll("safety_no", "safety_basic");

        Check.That(closes).IsEqualTo(1);
    }

    [Fact]
    public void Host_StartedByTheStartupRequest_ClosesTheWindow_OnLater()
    {
        var closes = 0;
        var host = new SafetySetupHost(() => false, onEnded => Flow(onEnded: onEnded), () => closes++);

        host.Start(closeWhenDone: true);
        Pick("safety_later");

        Check.That(closes).IsEqualTo(1);
        Check.That(_settings.General.SafetySetupDone).IsFalse();
    }

    [Fact]
    public void Host_StartedFromTheRow_LeavesTheWindowOpen()
    {
        var closes = 0;
        var host = new SafetySetupHost(() => false, onEnded => Flow(onEnded: onEnded), () => closes++);

        host.Start(closeWhenDone: false);
        AnswerAll("safety_no", "safety_basic");

        Check.That(closes).IsEqualTo(0);
        Check.That(_settings.General.SafetySetupDone).IsTrue();
    }

    [Fact]
    public void Host_WindowClosed_AbandonsTheChain_SoAStaleCardCannotCloseOrEndIt()
    {
        var closes = 0;
        var host = new SafetySetupHost(() => false, onEnded => Flow(onEnded: onEnded), () => closes++);
        host.Start(closeWhenDone: true);
        var intro = Top;

        host.OnWindowClosed();
        intro.Choices.Single(c => c.Id == "safety_later").OnChosen!.Invoke();

        Check.That(closes).IsEqualTo(0);
        Check.That(_ended).IsEmpty();
    }

    [Fact]
    public void Host_WindowClosed_DropsALateCrashRecoveryResult()
    {
        var supervision = FailingAsyncSupervision(_settings);
        var host = new SafetySetupHost(() => false,
            onEnded => Flow(supervision: supervision, onEnded: onEnded), () => { });
        host.Start(closeWhenDone: true);
        Pick("safety_start"); Pick("safety_yes");         // bring-up in flight
        var shown = _cards.Count;

        host.OnWindowClosed();
        RunBackground();                                   // … fails after the window closed
        Pick("safety_no");                                 // a stale Q2 card, never a failure card

        Check.That(_cards.Skip(shown).Select(c => c.Message))
            .Not.Contains("The Supervisor could not be started, so supervision is off.");
    }

    // ── the chain ────────────────────────────────────────────────────────────

    [Fact]
    public void Start_ShowsTheIntro_WithStartAndLater()
    {
        var flow = Flow();
        flow.Start();

        Check.That(_cards).HasSize(1);
        Check.That(Top.Message).IsEqualTo("Safety setup: 5 short questions.");
        Check.That(Top.Choices.Select(c => c.Id)).ContainsExactly("safety_start", "safety_later");
        Check.That(Top.Choices.Select(c => c.Label)).ContainsExactly("✓ Start", "Later");
    }

    [Fact]
    public void Later_EndsTheChain_AsksNothing_AndLeavesTheFlagFalse()
    {
        var flow = Flow();
        flow.Start();

        Pick("safety_later");

        Check.That(_cards).HasSize(1);
        Check.That(_ended).ContainsExactly(false);
        Check.That(_settings.General.SafetySetupDone).IsFalse();
        Check.That(_saves).IsEqualTo(0);
    }

    [Fact]
    public void TheChain_AsksTheFiveQuestionsInOrder_EachNumbered_AndNoCardBetweenThem()
    {
        var flow = Flow();
        flow.Start();
        Pick("safety_start");

        var asked = new List<string>();
        for (var q = 0; q < SafetySetupFlow.QuestionCount; q++)
        {
            asked.Add(Top.Message);
            Check.That(Top.Choices).HasSize(2);
            Pick(Top.Choices[^1].Id);   // the last choice of each question: No / All settings
        }

        Check.That(asked).ContainsExactly(
            "Question 1 of 5\nBring EyeCtrl back automatically after a crash?",
            "Question 2 of 5\nStart EyeCtrl when you sign in?\nIf another eye-control app also starts at sign-in, you will see two gaze points.",
            "Question 3 of 5\nPlay a sound when a dwell clicks?",
            "Question 4 of 5\nShow Track Status?",
            "Question 5 of 5\nSettings view: Basic or All settings?");
        Check.That(_cards).HasSize(1 + SafetySetupFlow.QuestionCount);
        Check.That(_ended).ContainsExactly(true);
    }

    [Fact]
    public void Question2_StartOnBoot_YesAndNo()
    {
        AnswerThrough(1, "safety_yes");
        Check.That(_settings.General.StartOnBoot).IsTrue();

        var again = new EyeCtrlSettings { General = { StartOnBoot = true } };
        Flow(settings: again).Start();
        Pick("safety_start");
        Pick("safety_no");                                // q1
        Pick("safety_no");                                // q2
        Check.That(again.General.StartOnBoot).IsFalse();
    }

    [Fact]
    public void Question3_DwellCue_YesAndNo()
    {
        AnswerThrough(2, "safety_yes");
        Check.That(_settings.Dwell.CueEnabled).IsTrue();

        var again = new EyeCtrlSettings { Dwell = { CueEnabled = true } };
        Flow(settings: again).Start();
        Pick("safety_start");
        for (var i = 0; i < 3; i++) Pick("safety_no");
        Check.That(again.Dwell.CueEnabled).IsFalse();
    }

    [Fact]
    public void Question4_TrackStatus_YesAndNo()
    {
        AnswerThrough(3, "safety_yes");
        Check.That(_settings.TrackStatus.Enabled).IsTrue();

        var again = new EyeCtrlSettings { TrackStatus = { Enabled = true } };
        Flow(settings: again).Start();
        Pick("safety_start");
        for (var i = 0; i < 4; i++) Pick("safety_no");
        Check.That(again.TrackStatus.Enabled).IsFalse();
    }

    [Fact]
    public void Question5_SettingsView_BasicAndAll_GoThroughTheWindowsModeSwitch()
    {
        var flow = Flow();
        flow.Start();
        Pick("safety_start");
        for (var i = 0; i < 4; i++) Pick("safety_no");
        Check.That(Top.Choices.Select(c => c.Id)).ContainsExactly("safety_basic", "safety_all");
        Check.That(Top.Choices.Select(c => c.Label)).ContainsExactly("Basic", "All settings");
        Pick("safety_basic");
        Check.That(_modeCalls).ContainsExactly(true);

        flow.Start();
        Pick("safety_start");
        for (var i = 0; i < 4; i++) Pick("safety_no");
        Pick("safety_all");
        Check.That(_modeCalls).ContainsExactly(true, false);
    }

    [Fact]
    public void Question1_Yes_RequestsCrashRecovery()
    {
        var (supervision, _) = WorkingSupervision(_settings);
        Flow(supervision: supervision).Start();
        Pick("safety_start");
        Pick("safety_yes");

        Check.That(supervision.Enabled).IsTrue();
        Check.That(_settings.Supervision.Enabled).IsTrue();
    }

    [Fact]
    public void Question1_No_IsANoOp_EvenWhileCrashRecoveryIsOn()
    {
        // The coordinator's safety-first rule: a stray "No" on a re-run must never remove the
        // safety net. Disabling stays in Settings → General.
        _settings.Supervision.Enabled = true;
        var (supervision, persisted) = WorkingSupervision(_settings);
        Flow(supervision: supervision).Start();
        Pick("safety_start");

        Pick("safety_no");

        Check.That(supervision.Enabled).IsTrue();
        Check.That(_settings.Supervision.Enabled).IsTrue();
        Check.That(persisted).IsEmpty();
        Check.That(supervision.InFlight).IsFalse();
    }

    [Fact]
    public void EveryAnswer_IsAppliedAtOnce_AndSavedThroughTheWindowsPath()
    {
        Flow().Start();
        Pick("safety_start");
        Pick("safety_no");               // q1: nothing to save for a No / no-op
        var before = _saves;
        Pick("safety_yes");              // q2
        Check.That(_saves).IsStrictlyGreaterThan(before);
        Check.That(_settings.General.StartOnBoot).IsTrue();
    }

    // ── completion and abandonment ───────────────────────────────────────────

    [Fact]
    public void TheFlagIsSetAndSaved_OnlyWithTheLastAnswer()
    {
        Flow().Start();
        Pick("safety_start");
        for (var i = 0; i < 4; i++)
        {
            Pick("safety_no");
            Check.That(_settings.General.SafetySetupDone).IsFalse();
        }

        var savesBefore = _saves;
        Pick("safety_basic");

        Check.That(_settings.General.SafetySetupDone).IsTrue();
        Check.That(_saves).IsStrictlyGreaterThan(savesBefore);
    }

    [Fact]
    public void AnAbandonedChain_KeepsTheEarlierAnswers_AndLeavesTheFlagFalse()
    {
        Flow().Start();
        Pick("safety_start");
        Pick("safety_no");               // q1
        Pick("safety_yes");              // q2 StartOnBoot
        Pick("safety_yes");              // q3 CueEnabled
        // q4 is on screen, and the window closes here: nothing more is picked.

        Check.That(_settings.General.StartOnBoot).IsTrue();
        Check.That(_settings.Dwell.CueEnabled).IsTrue();
        Check.That(_settings.TrackStatus.Enabled).IsFalse();
        Check.That(_settings.General.SafetySetupDone).IsFalse();
        Check.That(_ended).IsEmpty();
    }

    [Fact]
    public void Start_RunsTheWholeChainAgain_WhateverTheFlagSays()
    {
        _settings.General.SafetySetupDone = true;

        Flow().Start();

        Check.That(_cards).HasSize(1);
        Check.That(Top.Message).IsEqualTo("Safety setup: 5 short questions.");
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
        Flow(supervision: unavailable).Start();
        Pick("safety_start");
        Pick("safety_yes");

        Check.That(Top.Message).IsEqualTo(
            "Automatic crash recovery is unavailable: the EyeCtrl Supervisor is not installed.");
        Check.That(Top.Choices.Select(c => c.Id)).ContainsExactly("safety_ok");

        Pick("safety_ok");
        Check.That(Top.Message).StartsWith("Question 2 of 5");
        Check.That(_settings.Supervision.Enabled).IsFalse();
    }

    [Fact]
    public void AnAsyncFailure_WaitsForTheNextBoundary_NeverReplacingAQuestionOnScreen()
    {
        var supervision = FailingAsyncSupervision(_settings);
        Flow(supervision: supervision).Start();
        Pick("safety_start");
        Pick("safety_yes");                               // bring-up in flight; q2 on screen …
        var questionOnScreen = Top.Message;
        RunBackground();                                  // … when the bring-up fails and rolls back

        Check.That(Top.Message).IsEqualTo(questionOnScreen);
        Pick("safety_no");                                // q2 answered

        Check.That(Top.Message).IsEqualTo("The Supervisor could not be started, so supervision is off.");
        Pick("safety_ok");
        Check.That(Top.Message).StartsWith("Question 3 of 5");
    }

    [Fact]
    public void AFailureHeldAtTheLastAnswer_IsAcknowledged_BeforeTheChainEnds()
    {
        var supervision = FailingAsyncSupervision(_settings);
        Flow(supervision: supervision).Start();
        Pick("safety_start");
        Pick("safety_yes");
        for (var i = 0; i < 3; i++) Pick("safety_no");
        RunBackground();                                  // fails while q5 is on screen
        Pick("safety_all");

        Check.That(Top.Message).IsEqualTo("The Supervisor could not be started, so supervision is off.");
        Check.That(_ended).IsEmpty();
        Pick("safety_ok");
        Check.That(_ended).ContainsExactly(true);
    }

    // ── crash recovery still in flight at the last answer (review 02 N2) ─────

    [Fact]
    public void AnInFlightBringUpAtTheLastAnswer_ShowsTheCheckingCard_AndDoesNotEnd()
    {
        Flow(supervision: FailingAsyncSupervision(_settings)).Start();
        Pick("safety_start");
        Pick("safety_yes");                               // bring-up in flight
        for (var i = 0; i < 3; i++) Pick("safety_no");
        Pick("safety_all");                               // the last answer

        Check.That(Top.Message).IsEqualTo("Checking crash recovery…");
        Check.That(Top.Choices).IsEmpty();
        Check.That(_ended).IsEmpty();
        Check.That(_settings.General.SafetySetupDone).IsTrue();   // the answers are all in
    }

    [Fact]
    public void TheCheckingCard_OnFailure_ShowsTheFailureCard_ThenEnds()
    {
        Flow(supervision: FailingAsyncSupervision(_settings)).Start();
        Pick("safety_start");
        Pick("safety_yes");
        for (var i = 0; i < 3; i++) Pick("safety_no");
        Pick("safety_all");

        RunBackground();                                  // the bring-up fails and rolls back

        Check.That(Top.Message).IsEqualTo("The Supervisor could not be started, so supervision is off.");
        Check.That(Top.Choices.Select(c => c.Id)).ContainsExactly("safety_ok");
        Check.That(_ended).IsEmpty();
        Pick("safety_ok");
        Check.That(_ended).ContainsExactly(true);
    }

    [Fact]
    public void TheCheckingCard_OnSuccess_EndsAtOnce()
    {
        Flow(supervision: WorkingAsyncSupervision(_settings)).Start();
        Pick("safety_start");
        Pick("safety_yes");
        for (var i = 0; i < 3; i++) Pick("safety_no");
        Pick("safety_all");
        Check.That(Top.Message).IsEqualTo("Checking crash recovery…");
        var shown = _cards.Count;

        RunBackground();                                  // the Supervisor answers

        Check.That(_settings.Supervision.Enabled).IsTrue();
        Check.That(_cards).HasSize(shown);                // no further card
        Check.That(_ended).ContainsExactly(true);
    }

    [Fact]
    public void TheCeiling_TurnsAResultThatNeverComes_IntoACouldNotConfirmFailure_ThenEnds()
    {
        Flow(supervision: FailingAsyncSupervision(_settings)).Start();
        Pick("safety_start");
        Pick("safety_yes");
        Check.That(_scheduled).HasSize(1);
        Check.That(_scheduled[0].Delay).IsEqualTo(SupervisorBringUp.StartWait + TimeSpan.FromSeconds(5));
        for (var i = 0; i < 3; i++) Pick("safety_no");
        Pick("safety_all");
        Check.That(Top.Message).IsEqualTo("Checking crash recovery…");

        _scheduled[0].Action();                           // 20 s, and still no result

        Check.That(Top.Message).IsEqualTo("Crash recovery could not be confirmed. Check it in Settings → General.");
        Check.That(Top.Choices.Select(c => c.Id)).ContainsExactly("safety_ok");
        Pick("safety_ok");
        Check.That(_ended).ContainsExactly(true);

        var shown = _cards.Count;
        RunBackground();                                  // a result after the ceiling is ignored
        Check.That(_cards).HasSize(shown);
    }

    [Fact]
    public void TheCeiling_ReachedMidChain_IsShownAtTheNextBoundary()
    {
        Flow(supervision: FailingAsyncSupervision(_settings)).Start();
        Pick("safety_start");
        Pick("safety_yes");                               // q2 on screen
        var question = Top.Message;

        _scheduled[0].Action();

        Check.That(Top.Message).IsEqualTo(question);      // never over a question
        Pick("safety_no");
        Check.That(Top.Message).IsEqualTo("Crash recovery could not be confirmed. Check it in Settings → General.");
    }

    [Fact]
    public void AResult_CancelsTheCeiling_AndAbandonCancelsIt()
    {
        Flow(supervision: FailingAsyncSupervision(_settings)).Start();
        Pick("safety_start");
        Pick("safety_yes");
        RunBackground();
        Check.That(_scheduled[0].Disposed).IsTrue();
        _scheduled[0].Action();                           // a stale tick after the result changes nothing
        Pick("safety_no");
        Check.That(Top.Message).IsEqualTo("The Supervisor could not be started, so supervision is off.");

        var flow = Flow(supervision: FailingAsyncSupervision(_settings));
        flow.Start();
        Pick("safety_start");
        Pick("safety_yes");
        flow.Abandon();
        Check.That(_scheduled[1].Disposed).IsTrue();
    }

    [Fact]
    public void AnAbandonedChain_IgnoresALateCrashRecoveryResult()
    {
        var supervision = FailingAsyncSupervision(_settings);
        var flow = Flow(supervision: supervision);
        flow.Start();
        Pick("safety_start");
        Pick("safety_yes");                               // q2 on screen, bring-up in flight

        flow.Abandon();
        RunBackground();
        Pick("safety_no");                                // a stale q2 card is still answered …

        // … but the dropped failure is never shown at that boundary.
        Check.That(Top.Message).StartsWith("Question 3 of 5");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private (string Message, IReadOnlyList<ModalChoice> Choices) Top => _cards[^1];

    /// <summary>Picks a choice of the card on screen the way the dwell does.</summary>
    private void Pick(string id) => Top.Choices.Single(c => c.Id == id).OnChosen!.Invoke();

    /// <summary>Start, then questions 1…n−1 No, then question n with <paramref name="id"/>.</summary>
    private void AnswerThrough(int questionIndex, string id)
    {
        Flow().Start();
        Pick("safety_start");
        for (var i = 0; i < questionIndex; i++) Pick("safety_no");
        Pick(id);
    }

    /// <summary>The card on screen is the intro: start, answer q1–q4 with <paramref name="yesNo"/>, q5 with <paramref name="mode"/>.</summary>
    private void AnswerAll(string yesNo, string mode)
    {
        Pick("safety_start");
        for (var i = 0; i < 4; i++) Pick(yesNo);
        Pick(mode);
    }

    private void RunBackground()
    {
        var work = _background.ToList();
        _background.Clear();
        foreach (var w in work) w();
    }

    private SafetySetupFlow Flow(
        EyeCtrlSettings? settings = null, SupervisionToggleService? supervision = null, Action<bool>? onEnded = null) =>
        new(settings ?? _settings, supervision,
            (message, choices) => _cards.Add((message, choices)),
            () => _saves++,
            _modeCalls.Add,
            completed => { _ended.Add(completed); onEnded?.Invoke(completed); },
            (delay, action) =>
            {
                var s = new Scheduled(delay, action);
                _scheduled.Add(s);
                return s;
            });

    /// <summary>One captured <c>schedule</c> call: the test fires it by hand.</summary>
    private sealed class Scheduled(TimeSpan delay, Action action) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;
        public Action Action { get; } = action;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>A switch whose bring-up runs only when the test says so, and then succeeds.</summary>
    private SupervisionToggleService WorkingAsyncSupervision(EyeCtrlSettings settings) =>
        new(settings,
            new SupervisorBringUp(settings, () => { }, () => true, _ => { }, () => { }, () => true),
            new SupervisorStartupEnsure(settings, () => { }, () => true, () => true),
            persistEnabled: _ => { }, supervisorInstalled: () => true, isSupervisorConnected: () => true,
            reconcileRunKey: () => { }, runInBackground: _background.Add, postToUi: w => w());

    /// <summary>A switch that turns on (reachable Supervisor) synchronously; the list records every
    /// persist of supervision.enabled through the sanctioned path.</summary>
    private (SupervisionToggleService Toggle, List<bool> Persisted) WorkingSupervision(EyeCtrlSettings settings)
    {
        var persisted = new List<bool>();
        var toggle = new SupervisionToggleService(
            settings,
            new SupervisorBringUp(settings, () => { }, () => true, persisted.Add, () => { }, () => true),
            new SupervisorStartupEnsure(settings, () => { }, () => true, () => true),
            persistEnabled: v => persisted.Add(v), supervisorInstalled: () => true, isSupervisorConnected: () => true,
            reconcileRunKey: () => { }, runInBackground: w => w(), postToUi: w => w());
        return (toggle, persisted);
    }

    /// <summary>A switch whose bring-up runs only when the test says so, and then fails (launch refused).</summary>
    private SupervisionToggleService FailingAsyncSupervision(EyeCtrlSettings settings) =>
        new(settings,
            new SupervisorBringUp(settings, () => { }, () => false, _ => { }, () => { }, () => false),
            new SupervisorStartupEnsure(settings, () => { }, () => false, () => false),
            persistEnabled: _ => { }, supervisorInstalled: () => true, isSupervisorConnected: () => false,
            reconcileRunKey: () => { }, runInBackground: _background.Add, postToUi: w => w());

    // internal: implements IRecipient<T> (the MVVM generator needs at least internal visibility).
    internal sealed class RequestProbe : IRecipient<SafetySetupRequestedMessage>
    {
        public int Count { get; private set; }
        public void Receive(SafetySetupRequestedMessage message) => Count++;
    }
}
