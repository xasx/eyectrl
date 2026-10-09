namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// V1 (a)–(k) of the sub-pixel A/B build plan: the C5 trial state machine
/// (<see cref="SubPixelProbeViewModel"/>) driven by a fake <see cref="TimeProvider"/> advanced
/// together with the gaze timestamps. Every case carries the mutation listed in its comment; the
/// mutation table lives in the ticket's execution record.
/// </summary>
public class SubPixelProbeViewModelTests
{
    private const int Seed = 4242;

    private static readonly PhysicalRect Screen = new(0, 0, 1920, 1080);
    private static readonly PhysicalPoint Neutral = new(50, 50);
    private static readonly PhysicalPoint ButtonOne = new(400, 300);
    private static readonly PhysicalPoint ButtonTwo = new(1000, 300);
    private static readonly PhysicalPoint EndButton = new(1800, 1000);

    // ---------------------------------------------------------------- V1 (a) phases and durations

    [Fact]
    public void V1a_DemoPair_RunsFirst_Unscored_WithTheDesignDurations()
    {
        var f = new Harness();
        f.Start();

        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);
        Check.That(f.Vm.IsDemo).IsTrue();
        Check.That(f.Vm.CurrentTrial).IsNull();

        f.Drive(2970);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);

        f.Drive(30);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Gap);
        Check.That(f.Vm.IsDemo).IsTrue();

        f.Drive(770);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Gap);

        f.Drive(30);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval2);

        f.Drive(2990);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval2);

        f.Drive(30);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);
        Check.That(f.Vm.IsDemo).IsFalse();
        Check.That(f.Vm.CurrentTrial).IsEqualTo(f.Schedule.DriftTrials[0]);
        Check.That(f.Vm.CurrentBlockTrialOutcomes).IsEmpty();
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);

        Check.That(f.PhaseLog).HasSize(4);
        Check.That(f.PhaseLog[^1]).IsEqualTo(SubPixelProbePhase.Interval1);
        Check.That(f.PhaseLog).Not.Contains(SubPixelProbePhase.Answer);
    }

    // Mutation (a1): ScriptedIntervalMs 3000 -> 2500 (the 2970 check goes red).
    // Mutation (a2): GapMs 800 -> 900 (the 770 + 30 checks go red).

    [Fact]
    public void V1a2_LiveBlock_UsesFourSecondIntervals()
    {
        var f = new Harness(SubPixelProbeSessionKind.Session2);
        f.Start();

        Check.That(f.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Live);

        f.Drive(3970);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);

        f.Drive(30);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Gap);

        f.Drive(770);
        f.Drive(30);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval2);

        f.Drive(3970);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval2);

        f.Drive(30);
        Check.That(f.Vm.IsDemo).IsFalse();
        Check.That(f.Vm.CurrentTrial).IsEqualTo(f.Schedule.LiveTrials[0]);
    }

    // Mutation (a2): the live interval drops to LiveIntervalMs 3000 (the 3970 check goes red).

    // ------------------------------------------------- V1 (b) order, conditions and block sequence

    [Fact]
    public void V1b_ConditionsFollowTheTrialOrder_AndEachBlockIsSymmetric()
    {
        var f = new Harness();
        f.Start();

        var orders = new List<SubPixelAbOrder>();
        for (int i = 0; i < SubPixelAbSchedule.TrialsPerBlock; i++)
        {
            bool atTrial = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo);
            Check.That(atTrial).IsTrue();

            var trial = f.Vm.CurrentTrial;
            Check.That(trial).IsNotNull();
            Check.That(trial!).IsEqualTo(f.Schedule.DriftTrials[i]);

            Check.That(f.Vm.Interval1Condition)
                .IsEqualTo(trial!.Order == SubPixelAbOrder.AFirst
                    ? SubPixelAbCondition.A
                    : SubPixelAbCondition.B);
            Check.That(f.Vm.Interval2Condition)
                .IsEqualTo(trial.Order == SubPixelAbOrder.AFirst
                    ? SubPixelAbCondition.B
                    : SubPixelAbCondition.A);

            orders.Add(trial.Order);
            f.AnswerCurrentTrial(SubPixelAbCondition.B);
        }

        Check.That(orders.Count(order => order == SubPixelAbOrder.AFirst)).IsEqualTo(5);
        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Static);
        Check.That(f.Vm.IsDemo).IsTrue();

        var g = new Harness(SubPixelProbeSessionKind.Session2);
        g.Start();
        Check.That(g.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Live);
        Check.That(g.PhaseLog).HasSize(1);
    }

    // Mutation (b1): swap the two IntervalCondition bodies -> the condition checks go red.
    // Mutation (b2): Session1's block queue starts with Static -> the drift-trial equality goes red.

    // ------------------------------------------------------------- V1 (c) answer dwell and radius

    [Fact]
    public void V1c_AnswerDwell_ReadsToolbarButtonDwellMsLive_AndHonoursTheRegisteredRadius()
    {
        var f = new Harness();
        f.Start();

        // The setting changes between construction and registration: the dwell must be read at
        // registration, not cached at construction.
        f.Dwell.ToolbarButtonDwellMs = 400;
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => f.AnswerClicks++);
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

        Check.That(f.Vm.GetProgress(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control)).IsEqualTo(0);

        bool reached = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer, at: Outside);
        Check.That(reached).IsTrue();

        // 76 px from the button centre: outside the registered 75 px radius, however long the hold.
        f.Drive(900, at: Outside);
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);

        // 74 px: inside. 400 ms dwell -> no answer before it, one after it.
        f.Drive(360, at: Inside);
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);
        f.Drive(120, at: Inside);

        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(1);
        Check.That(f.AnswerClicks).IsEqualTo(1);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);

        var first = f.Schedule.DriftTrials[0];
        Check.That(f.Vm.CurrentAPicks)
            .IsEqualTo(first.Order == SubPixelAbOrder.AFirst ? 1 : 0);
        Check.That(f.Vm.CurrentBPicks)
            .IsEqualTo(first.Order == SubPixelAbOrder.AFirst ? 0 : 1);

        // The next trial re-reads the setting: 200 ms now.
        f.Dwell.ToolbarButtonDwellMs = 200;
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => f.AnswerClicks++);
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

        reached = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer, at: Inside);
        Check.That(reached).IsTrue();
        f.Drive(270, at: Inside);

        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(2);
        Check.That(f.AnswerClicks).IsEqualTo(2);
    }

    // Mutation (c1): use the caller's dwellMs instead of the settings value -> the 400 ms answer
    //   arrives too early (still 0 at 480 ms with the passed 700).
    // Mutation (c2): hardcode 700 -> same red as (c1) with the 400 ms setting.
    // Mutation (c3): ignore radiusPhysical (use a huge radius) -> the 900 ms outside hold answers.

    // ----------------------------------------------------------------- V1 (d) no dwell carry-over

    [Fact]
    public void V1d_AnswerDwell_DoesNotCarryOverFromIntervalTwo()
    {
        var f = new Harness();
        f.Start();
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => f.AnswerClicks++);
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

        bool atTrial = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo);
        Check.That(atTrial).IsTrue();
        bool atInterval2 = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval2, at: Neutral);
        Check.That(atInterval2).IsTrue();

        // Enter the button 600 ms before the answer screen: on the boundary the detector holds a
        // live, unactivated 600 ms accumulation with no cooldown in flight — the state the onset
        // reset must discard. (Holding from the start would let the detector's own cooldown null
        // the state and mask a missing reset.)
        f.Drive(2400, at: Neutral);
        f.Drive(600, at: ButtonOne);

        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Answer);
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);

        // Without the reset the first answer-phase samples inherit the 600 ms and answer ~120 ms
        // in; with it, the fresh dwell needs its full 700 ms.
        f.Drive(660, at: ButtonOne);
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);

        f.Drive(90, at: ButtonOne);
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(1);
    }

    // Mutation (d1): remove the answer-onset ResetAllState -> dwell accumulated during interval 2
    //   (more than 3 s) delivers at the first answer-phase sample: the 660 ms check goes red.
    // Mutation (d2): feed the detector during interval 2 but deliver at onset -> same red.

    // ------------------------------------------------------------- V1 (e) invalid-gaze void rule

    [Fact]
    public void V1e_InvalidGazeOver500ms_VoidsAndRequeues_Exactly500DoesNot()
    {
        var f = new Harness();
        f.Start();

        bool atTrial = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo);
        Check.That(atTrial).IsTrue();
        var trial1 = f.Vm.CurrentTrial;

        // 50 ms invalid samples: the run reaches exactly 500 ms at the 11th sample.
        f.Valid = false;
        for (int i = 0; i < 10; i++) f.Tick(50);

        f.Tick(50);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(0);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);

        f.Tick(50);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);
        Check.That(f.Vm.CurrentTrial).IsEqualTo(f.Schedule.DriftTrials[1]);
        Check.That(f.Vm.CurrentBlockTrialOutcomes).HasSize(1);
        Check.That(f.Vm.CurrentBlockTrialOutcomes[0].VoidReason)
            .IsEqualTo(SubPixelProbeVoidReason.InvalidGaze);

        f.Valid = true;
        f.PlayBlock();

        Check.That(f.Blocks).HasSize(1);
        var drift = f.Blocks[0];
        Check.That(drift.Block).IsEqualTo(SubPixelAbBlock.Drift);
        Check.That(drift.Tally.Voids).IsEqualTo(1);
        Check.That(drift.Tally.Scored).IsEqualTo(10);
        Check.That(drift.Trials).HasSize(11);
        Check.That(drift.Trials[0].VoidReason).IsEqualTo(SubPixelProbeVoidReason.InvalidGaze);
        Check.That(drift.Trials[^1].Trial).IsEqualTo(trial1);
    }

    // Mutation (e1): InvalidGazeVoidMs 500 -> 700 -> the 550 ms run never voids.
    // Mutation (e2): the comparison > becomes >= -> the exactly-500 sample voids.

    // ----------------------------------------------------------------- V1 (f) render-gap void rule

    [Fact]
    public void V1f_RenderGapOver50ms_VoidsOnlyInsideAnInterval()
    {
        var f = new Harness();
        f.Start();

        bool atTrial = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo);
        Check.That(atTrial).IsTrue();

        f.Vm.ReportRenderGap(50);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(0);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);

        f.Vm.ReportRenderGap(51);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);
        Check.That(f.Vm.CurrentTrial).IsEqualTo(f.Schedule.DriftTrials[1]);

        // Outside the intervals a report is recorded but never voids.
        bool atGap = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Gap, at: Neutral);
        Check.That(atGap).IsTrue();
        f.Vm.ReportRenderGap(500);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Gap);
    }

    // Mutation (f1): RenderGapVoidMs 50 -> 100 -> the 51 ms report never voids.
    // Mutation (f2): void on any phase -> the 500 ms report during the gap voids.
    // Mutation (f3): the comparison > becomes >= -> the exactly-50 report voids.

    // ----------------------------------------------------------- V1 (g) requeue keeps the trial

    [Fact]
    public void V1g_RequeueGoesToTheEndOfTheBlock_AndKeepsTheTrial()
    {
        var f = new Harness();
        f.Start();

        bool atTrial = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo);
        Check.That(atTrial).IsTrue();
        var trial1 = f.Vm.CurrentTrial;

        f.Vm.ReportRenderGap(100);

        // The block carries on with the next scheduled trial; the voided one is appended.
        Check.That(f.Vm.CurrentTrial).IsEqualTo(f.Schedule.DriftTrials[1]);

        f.PlayBlock();

        var drift = f.Blocks[0];
        Check.That(drift.Trials).HasSize(11);
        Check.That(drift.Trials[1].Trial).IsEqualTo(f.Schedule.DriftTrials[1]);
        Check.That(drift.Trials[^1].Trial).IsEqualTo(trial1);
        Check.That(drift.Trials[^1].Answer).IsEqualTo(SubPixelAbCondition.B);
        Check.That(drift.Tally.BPicks).IsEqualTo(10);
        Check.That(drift.Tally.APicks).IsEqualTo(0);
    }

    // Mutation (g1): append the requeue at the front (insert at _pendingIndex) -> the next current
    //   trial is trial 1 again: the line right after the void goes red.

    // ------------------------------------------------------------------- V1 (h) the void cap

    [Fact]
    public void V1h_FourthVoidEndsTheBlock_DriftCapSkipsStatic_AndSessionHasNoVerdict()
    {
        var f = new Harness();
        f.Start();

        bool atTrial = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo);
        Check.That(atTrial).IsTrue();

        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);

        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Blocks[0].Block).IsEqualTo(SubPixelAbBlock.Drift);
        Check.That(f.Blocks[0].Tally.Voids).IsEqualTo(4);
        Check.That(f.Blocks[0].Tally.Scored).IsEqualTo(0);
        Check.That(f.Blocks[0].Trials).HasSize(4);

        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.BlocksCompleted);
        Check.That(f.Sessions[0].Aborted).IsFalse();
        Check.That(f.Sessions[0].Blocks).HasSize(1);

        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
        Check.That(f.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Drift);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);

        int phases = f.PhaseLog.Count;
        f.Time.Advance(60_000);
        Check.That(f.PhaseLog).HasSize(phases);

        // The capped tally is valid input for the verdict: no session-1 verdict, static skipped.
        var verdict = SubPixelAbVerdict.Session1(f.Blocks[0].Tally, null);
        Check.That(verdict.Outcome).IsEqualTo(SubPixelAbOutcome.Incomplete);
        Check.That(verdict.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.Skipped);
    }

    [Fact]
    public void V1h2_StaticCap_KeepsTheDriftVerdict_AndEndsTheSessionIncomplete()
    {
        var f = new Harness();
        f.Start();
        f.PlayBlock();

        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Blocks[0].Block).IsEqualTo(SubPixelAbBlock.Drift);
        Check.That(f.Blocks[0].Tally.BPicks).IsEqualTo(10);

        bool atStatic = f.DriveUntil(() =>
            f.Vm.CurrentBlock == SubPixelAbBlock.Static
            && f.Vm.Phase == SubPixelProbePhase.Interval1
            && !f.Vm.IsDemo);
        Check.That(atStatic).IsTrue();

        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);

        Check.That(f.Blocks).HasSize(2);
        Check.That(f.Blocks[1].Block).IsEqualTo(SubPixelAbBlock.Static);
        Check.That(f.Blocks[1].Tally.Voids).IsEqualTo(4);

        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.BlocksCompleted);
        Check.That(f.Sessions[0].Blocks).HasSize(2);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);

        var verdict = SubPixelAbVerdict.Session1(f.Blocks[0].Tally, f.Blocks[1].Tally);
        Check.That(verdict.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(verdict.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.Incomplete);
    }

    // Mutation (h1): the cap comparison > MaxVoidsPerBlock becomes > MaxVoidsPerBlock + 1 -> after
    //   the 4th void the block keeps running: Blocks is empty and the session has not ended.
    // Mutation (h2): the capped-drift conditional always starts the next block -> after the 4th
    //   void the static block runs and the session stays live.

    // ------------------------------------------------------------------ V1 (i) the eyes-closed exit

    [Fact]
    public void V1i_EyesClosed_InvalidSamplesAlone_EndTheSessionAfterTenSeconds()
    {
        var f = new Harness();
        f.Start();

        bool reached = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer, at: Neutral);
        Check.That(reached).IsTrue();

        // Closed eyes: samples keep arriving, flagged invalid.
        f.Valid = false;
        f.Drive(9900, at: Neutral);
        Check.That(f.Sessions).IsEmpty();

        f.Drive(600, at: Neutral);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.EyesClosed);
        Check.That(f.Sessions[0].Aborted).IsTrue();
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);
    }

    [Fact]
    public void V1i2_EyesClosed_SilenceEndsTheSession()
    {
        var f = new Harness();
        f.Start();
        f.Drive(600, at: Neutral);

        // A dropped tracker: no samples at all, so only the timer can notice.
        f.Time.Advance(9990);
        Check.That(f.Sessions).IsEmpty();

        f.Time.Advance(30);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.EyesClosed);
    }

    [Fact]
    public void V1i3_ValidSamples_KeepTheSessionAlivePastTenSeconds()
    {
        var f = new Harness();
        f.Start();

        f.Drive(12_000, at: Neutral);
        Check.That(f.Sessions).IsEmpty();
        Check.That(f.Vm.Phase).IsNotEqualTo(SubPixelProbePhase.Ended);

        f.Time.Advance(10_100);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.EyesClosed);
    }

    // Mutation (i1): re-arm the eyes-closed timer on ANY sample (move the anchor and Change out of
    //   the valid branch) -> the invalid stream never trips it.
    // Mutation (i2): check the timeout only inside Receive (timer callback becomes a no-op) -> the
    //   silence case never trips.
    // Mutation (i3): end at 10 s from Start instead of from the last valid sample -> the 12 s
    //   valid drive goes red.

    // ------------------------------------------------------------------ V1 (j) the no-answer abort

    [Fact]
    public void V1j_NoAnswer_EndsTheSessionAfterSixtySeconds_FromTheAnswerOnset()
    {
        var f = new Harness();
        f.Start();

        bool reached = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer, at: Neutral);
        Check.That(reached).IsTrue();

        f.Drive(59_500, at: Neutral);
        Check.That(f.Sessions).IsEmpty();

        f.Drive(700, at: Neutral);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.NoAnswer);
        Check.That(f.Sessions[0].Aborted).IsTrue();
    }

    [Fact]
    public void V1j2_EachAnswerPhase_GetsAFreshSixtySecondWindow()
    {
        var f = new Harness();
        f.Start();
        f.AnswerCurrentTrial(SubPixelAbCondition.B);

        bool reached = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer, at: Neutral);
        Check.That(reached).IsTrue();

        f.Drive(59_500, at: Neutral);
        Check.That(f.Sessions).IsEmpty();

        f.Drive(700, at: Neutral);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.NoAnswer);
    }

    // Mutation (j1): arm the answer timer at interval-1 start instead of at answer onset -> the
    //   59.5 s silence check goes red (the abort lands inside it).
    // Mutation (j2): drop the answer timeout entirely -> the session never aborts.
    // Mutation (j3): one 60 s window from the first answer -> V1j2's 59.5 s check goes red.

    // ------------------------------------------------------- V1 (k) End test and dispose quiescence

    [Theory]
    [InlineData(SubPixelProbePhase.Interval1)]
    [InlineData(SubPixelProbePhase.Gap)]
    [InlineData(SubPixelProbePhase.Interval2)]
    [InlineData(SubPixelProbePhase.Answer)]
    public void V1k_EndTest_EndsTheSessionInEveryPhase_AndStopsTheTimers(SubPixelProbePhase target)
    {
        var f = new Harness();
        f.Start();

        bool reached = f.DriveUntil(() => f.Vm.Phase == target, at: Neutral);
        Check.That(reached).IsTrue();

        f.Vm.SimulateActivated(SubPixelProbeViewModel.EndTestTargetId);

        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.EndTest);
        Check.That(f.Sessions[0].Aborted).IsTrue();
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);
        Check.That(f.Blocks).IsEmpty();

        int phases = f.PhaseLog.Count;
        f.Drive(60_000, at: Neutral);
        Check.That(f.PhaseLog).HasSize(phases);
    }

    [Fact]
    public void V1k2_EndTestButton_AnswersByDwell_AndRunsItsCallback()
    {
        var f = new Harness();
        f.Start();
        f.Vm.Register(SubPixelProbeViewModel.EndTestTargetId, DwellCategory.Control, 75, 700, () => f.EndTestClicks++);
        f.Vm.UpdatePosition(SubPixelProbeViewModel.EndTestTargetId, DwellCategory.Control, EndButton);

        bool reached = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer, at: Neutral);
        Check.That(reached).IsTrue();

        f.Drive(660, at: EndButton);
        Check.That(f.Sessions).IsEmpty();

        f.Drive(90, at: EndButton);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.EndTest);
        Check.That(f.EndTestClicks).IsEqualTo(1);
    }

    [Fact]
    public void V1k3_DisposeFromABlockCompletedHandler_LeavesNoTimerAndRaisesNothingAfter()
    {
        var f = new Harness();
        int blocksSeen = 0;
        f.Vm.BlockCompleted += _ =>
        {
            blocksSeen++;
            f.Vm.Dispose();
        };

        f.Start();
        f.PlayBlock();

        Check.That(blocksSeen).IsEqualTo(1);
        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Sessions).IsEmpty();
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Idle);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);

        int phases = f.PhaseLog.Count;
        f.Time.Advance(60_000);
        Check.That(f.PhaseLog).HasSize(phases);
    }

    // Mutation (k1): EndSession skips StopTimers -> a live timer survives the End test and the
    //   ScheduledTimerCount check goes red.
    // Mutation (k2): the End test id is not handled in OnTargetActivated -> no SessionCompleted.
    // Mutation (k3): drop the IsDisposed check after the BlockCompleted raise in CompleteBlock ->
    //   the disposed view model starts the static block; RaisePhaseAdvanced runs and the armed
    //   timer is disposed: the test goes red on the phase count or on the thrown
    //   ObjectDisposedException.

    private static PhysicalPoint Inside => new(ButtonOne.X + 74, ButtonOne.Y);
    private static PhysicalPoint Outside => new(ButtonOne.X + 76, ButtonOne.Y);

    /// <summary>
    /// Drives a <see cref="SubPixelProbeViewModel"/> on the fake clock: every tick advances the fake
    /// time and sends one gaze message stamped from the same virtual timeline, so the phase timers
    /// (TimeProvider) and the dwell detectors (gaze microseconds) never drift apart.
    /// </summary>
    private sealed class Harness
    {
        public readonly FakeTimeProvider Time = new();
        public readonly WeakReferenceMessenger Messenger = new();
        public readonly DwellSettings Dwell = new();
        public readonly SubPixelAbSchedule Schedule;
        public readonly SubPixelProbeViewModel Vm;
        public readonly List<SubPixelProbePhase> PhaseLog = new();
        public readonly List<SubPixelProbeBlockOutcome> Blocks = new();
        public readonly List<SubPixelProbeSessionOutcome> Sessions = new();

        public bool Valid = true;
        public long GazeUs;
        public int AnswerClicks;
        public int EndTestClicks;

        public Harness(SubPixelProbeSessionKind session = SubPixelProbeSessionKind.Session1)
        {
            Schedule = SubPixelAbSchedule.Create(Seed, Screen);
            Vm = new SubPixelProbeViewModel(Messenger, Time, Schedule, session, Dwell, () => Valid);
            Vm.PhaseAdvanced += () => PhaseLog.Add(Vm.Phase);
            Vm.BlockCompleted += outcome => Blocks.Add(outcome);
            Vm.SessionCompleted += outcome => Sessions.Add(outcome);
        }

        public void Start() => Vm.Start();

        /// <summary>Advances the clock by <paramref name="ms"/> and sends one gaze sample at <paramref name="at"/>.</summary>
        public void Tick(long ms = 30, PhysicalPoint? at = null)
        {
            Time.Advance(ms);
            GazeUs += ms * 1000;
            Messenger.Send(new GazeCursorMovedMessage(at ?? Neutral, GazeUs));
        }

        /// <summary>Drives gaze samples at <paramref name="step"/> ms cadence for <paramref name="ms"/> total.</summary>
        public void Drive(long ms, PhysicalPoint? at = null, long step = 30)
        {
            for (long elapsed = 0; elapsed < ms; elapsed += step)
                Tick(Math.Min(step, ms - elapsed), at);
        }

        /// <summary>Drives until <paramref name="condition"/> holds; returns whether it held before the limit.</summary>
        public bool DriveUntil(Func<bool> condition, PhysicalPoint? at = null, long limitMs = 300_000)
        {
            for (long elapsed = 0; elapsed < limitMs; elapsed += 30)
            {
                if (condition()) return true;
                Tick(30, at);
            }

            return condition();
        }

        /// <summary>Drives to the answer screen of the current trial and simulates the picked button.</summary>
        public void AnswerCurrentTrial(SubPixelAbCondition pick)
        {
            bool reached = DriveUntil(() => Vm.Phase == SubPixelProbePhase.Answer);
            Check.That(reached).IsTrue();

            string id = pick == Vm.Interval1Condition
                ? SubPixelProbeViewModel.AnswerOneTargetId
                : SubPixelProbeViewModel.AnswerTwoTargetId;
            Vm.SimulateActivated(id);
        }

        /// <summary>Plays the current block to completion, answering every trial with <paramref name="pick"/>.</summary>
        public void PlayBlock(SubPixelAbCondition pick = SubPixelAbCondition.B)
        {
            int target = Blocks.Count + 1;
            long elapsed = 0;

            // A virtual-time budget, so a broken state machine fails the test instead of spinning.
            while (Blocks.Count < target && elapsed < 300_000)
            {
                Tick();
                elapsed += 30;
                if (Vm.Phase == SubPixelProbePhase.Answer)
                    AnswerCurrentTrial(pick);
            }

            Check.WithCustomMessage($"The block did not complete within {elapsed} ms of virtual time.")
                .That(Blocks).HasSize(target);
        }
    }
}
