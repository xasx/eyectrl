namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// V1 (a)–(m) of the sub-pixel A/B build plan (revision 6.1): the C5 run state machine
/// (<see cref="SubPixelProbeViewModel"/>) driven by a fake <see cref="TimeProvider"/> advanced
/// together with the gaze timestamps. Every canonical case carries the mutation listed in its
/// comment; the mutation table lives in the ticket's execution record. Tests adopted from SP-3
/// review 01's probe set carry the probe id (P3–P15).
/// </summary>
public class SubPixelProbeViewModelTests
{
    private const int Seed = 4242;

    private static readonly PhysicalRect Screen = new(0, 0, 1920, 1080);
    private static readonly PhysicalPoint Neutral = new(50, 50);
    private static readonly PhysicalPoint ButtonOne = new(400, 300);
    private static readonly PhysicalPoint ButtonTwo = new(1000, 300);
    private static readonly PhysicalPoint EndButton = new(1800, 1000);

    // ------------------------------------------------------------------------- V1 (a), (f): demo, phases, durations

    [Fact]
    public void V1a_IntroThenDemoPair_RunsFirst_Unscored_WithTheDesignDurations()
    {
        var f = new Harness();
        f.Vm.Start();
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Intro);
        Check.That(f.PhaseLog).HasSize(1);

        f.Vm.BeginTrials();

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

        // (f) the demo pair is never scored: no answer screen, no tally, no outcome.
        Check.That(f.PhaseLog).Not.Contains(SubPixelProbePhase.Answer);
        Check.That(f.Blocks).IsEmpty();
        Check.That(f.Sessions).IsEmpty();

        Check.That(f.PhaseLog).HasSize(5);
        Check.That(f.PhaseLog[^1]).IsEqualTo(SubPixelProbePhase.Interval1);
    }

    // Mutation (a1): ScriptedIntervalMs 3000 -> 2500 (the 2970 check goes red).
    // Mutation (a2): GapMs 800 -> 900 (the 770 + 30 checks go red).
    // Mutation (f): let the demo reach Answer and score it -> the unscored assertions go red.

    [Fact]
    public void V1a2_LiveBlock_UsesFourSecondIntervals()
    {
        var f = new Harness();
        f.StartToTrials();
        f.CompleteSession1();

        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ask);
        f.Vm.ContinueToLiveGaze();

        Check.That(f.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Live);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);

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

    // Mutation (a3): LiveIntervalMs 4000 -> 3000 (the 3970 checks go red).

    [Fact]
    public void V1a3_P3_DwellCompletedDuringIntervalTwo_IsNotDeliveredAtOnset()
    {
        var f = new Harness();
        f.StartToTrials();
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => f.AnswerClicks++);
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo)).IsTrue();
        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval2)).IsTrue();

        // Held on the button for all of interval 2 (3 s > the 700 ms dwell) and across the boundary.
        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer, ButtonOne)).IsTrue();
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);
        Check.That(f.Vm.CurrentTrial).IsEqualTo(f.Schedule.DriftTrials[0]);

        f.Drive(660, ButtonOne);
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);

        f.Drive(90, ButtonOne);
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(1);
    }

    // Mutation (a4): deferred delivery — the dwell completed inside interval 2 is delivered at
    //   onset -> the 660 ms check (and the earlier ScoredInCurrentBlock == 0) goes red.
    // Mutation (a1): onset ResetAllState/ResetCooldown removed -> the accumulation survives into
    //   the answer screen -> red.

    // ------------------------------------------------------------------------------- V1 (b): End test in every phase

    [Theory]
    [InlineData(SubPixelProbePhase.Interval1)]
    [InlineData(SubPixelProbePhase.Gap)]
    [InlineData(SubPixelProbePhase.Interval2)]
    [InlineData(SubPixelProbePhase.Answer)]
    public void V1b_EndTest_EndsTheRunInTheDemoAndAnswerPhases(SubPixelProbePhase target)
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == target)).IsTrue();
        f.Vm.SimulateActivated(SubPixelProbeViewModel.EndTestTargetId);

        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EndTest);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.EndTest);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);
        Check.That(f.Blocks).IsEmpty();

        int phases = f.PhaseLog.Count;
        f.Drive(60_000);
        Check.That(f.PhaseLog).HasSize(phases);
    }

    // Mutation (b-demo): End test ignored in any demo phase -> no RunEnded / no SessionCompleted.

    [Theory]
    [InlineData(SubPixelProbePhase.Interval1)]
    [InlineData(SubPixelProbePhase.Gap)]
    [InlineData(SubPixelProbePhase.Interval2)]
    public void V1b2_P8_EndTest_EndsTheRunInTheScoredIntervals(SubPixelProbePhase target)
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == target && !f.Vm.IsDemo)).IsTrue();
        f.Vm.SimulateActivated(SubPixelProbeViewModel.EndTestTargetId);

        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EndTest);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);
    }

    // Mutation (b1/b2/b3): End test ignored in the scored interval 1 / gap / interval 2 -> red.

    // ------------------------------------------------------------------------------ V1 (c): the 60 s no-answer abort

    [Fact]
    public void V1c_NoAnswer_EndsTheSessionAndRunAfterSixtySeconds()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();

        f.Drive(59_500);
        Check.That(f.Sessions).IsEmpty();
        Check.That(f.Runs).IsEmpty();

        f.Drive(700);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.NoAnswer);
        Check.That(f.Sessions[0].Aborted).IsTrue();
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.NoAnswer);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
    }

    [Fact]
    public void V1c2_P4_NoAnswer_FiresOnTheTimer_WithoutASampleAtSixtySeconds()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();

        // Sparse valid samples every 9 s keep the eyes-closed exit quiet (its threshold is 10 s).
        for (int i = 0; i < 6; i++) f.Tick(9000);
        Check.That(f.Runs).IsEmpty();

        // Silence over the 60 s mark: only the answer timer can fire.
        f.Time.Advance(6100);
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.NoAnswer);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.NoAnswer);
    }

    // Mutation (c): the 60 s timeout checked only inside Receive -> no sample arrives after 54 s,
    //   so nothing fires at 60 s: the Runs assertions go red.
    // Mutation (c2): the answer timer armed at interval-1 start -> the NoAnswer lands inside the
    //   59.5 s wait of V1c -> red.

    [Fact]
    public void V1c3_EachAnswerPhase_GetsAFreshSixtySecondWindow()
    {
        var f = new Harness();
        f.StartToTrials();
        f.AnswerCurrentTrial(SubPixelAbCondition.B);

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();

        f.Drive(59_500);
        Check.That(f.Runs).IsEmpty();

        f.Drive(700);
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.NoAnswer);
    }

    // Mutation (c3): one 60 s window from the first answer -> the second window fires inside its
    //   59.5 s wait -> red.

    // ------------------------------------------------------------------------- V1 (d), (e), (m): the void rules

    [Fact]
    public void V1d_InvalidGazeOver500ms_VoidsAndRequeues_Exactly500DoesNot()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo)).IsTrue();
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
        Check.That(drift.Tally.Voids).IsEqualTo(1);
        Check.That(drift.Tally.Scored).IsEqualTo(10);
        Check.That(drift.Trials).HasSize(11);
        Check.That(drift.Trials[^1].Trial).IsEqualTo(trial1);
    }

    // Mutation (d1): InvalidGazeVoidMs 500 -> 700 -> the 550 ms run never voids.
    // Mutation (d2): the comparison > becomes >= -> the exactly-500 sample voids.
    // Mutation (d3, o10): the requeue dropped or inserted at the front -> the last-trial assertion
    //   or the next-trial assertion goes red.

    [Fact]
    public void V1d2_P9_InvalidGaze_And_RenderGap_VoidInIntervalTwo()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval2 && !f.Vm.IsDemo)).IsTrue();
        f.Vm.ReportRenderGap(51);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval2 && !f.Vm.IsDemo)).IsTrue();
        f.Valid = false;
        f.Drive(600);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(2);
        Check.That(f.Vm.CurrentBlockTrialOutcomes[^1].VoidReason)
            .IsEqualTo(SubPixelProbeVoidReason.InvalidGaze);
    }

    // Mutation (d4): an invalid run in interval 2 never voids -> red.
    // Mutation (e2): a render gap in interval 2 ignored -> red.

    [Fact]
    public void V1d3_P10_InvalidRunStartedInTheGap_IsNotChargedToIntervalTwo()
    {
        var f = new Harness();
        f.StartToTrials();

        // The run starts 300 ms into the gap and keeps arriving through the interval-2 boundary.
        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Gap && !f.Vm.IsDemo)).IsTrue();
        f.Drive(300);
        f.Valid = false;
        f.Drive(500);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval2);

        // Only 240 ms of invalid gaze inside interval 2: no void.
        f.Drive(240);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(0);
    }

    // Mutation (F1): the interval-2 entry does not clear _invalidSince -> the gap's 500 ms is
    //   charged to interval 2 -> the run crosses the threshold inside the 240 ms -> red.

    [Fact]
    public void V1e_RenderGapOver50ms_VoidsOnlyInsideAnInterval()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo)).IsTrue();

        f.Vm.ReportRenderGap(50);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(0);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);

        f.Vm.ReportRenderGap(51);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);
        Check.That(f.Vm.CurrentTrial).IsEqualTo(f.Schedule.DriftTrials[1]);

        // Outside the intervals a report is ignored entirely.
        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Gap)).IsTrue();
        f.Vm.ReportRenderGap(500);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Gap);
    }

    // Mutation (e1): RenderGapVoidMs 50 -> 100 -> the 51 ms report never voids.
    // Mutation (e3): void on any phase -> the 500 ms report during the gap voids.
    // Mutation (e4): the comparison > becomes >= -> the exactly-50 report voids.

    [Fact]
    public void V1m_SilenceOver500msInsideAnInterval_VoidsOnTheTimer_Exactly500DoesNot()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo)).IsTrue();

        // No samples at all: the silence guard must fire on its own.
        f.Time.Advance(500);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(0);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);

        f.Time.Advance(1);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);
        Check.That(f.Vm.CurrentBlockTrialOutcomes[0].VoidReason)
            .IsEqualTo(SubPixelProbeVoidReason.Silence);

        // A dropout that begins in the gap is charged from the interval-2 start only.
        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Gap && !f.Vm.IsDemo)).IsTrue();
        f.Time.Advance(800);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval2);
        f.Time.Advance(400);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);
        f.Time.Advance(110);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(2);
    }

    [Fact]
    public void V1m2_SilenceOutsideAnInterval_DoesNotVoid()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Gap && !f.Vm.IsDemo)).IsTrue();
        f.Time.Advance(700);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Gap);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(0);

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();
        f.Time.Advance(5000);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(0);
    }

    // Mutation (m1): silence ignored (no silence timer) -> the 501 ms check never voids.
    // Mutation (m2): the silence run measured from before the interval -> it fires early in
    //   interval 2 (the 400 ms check goes red).
    // Mutation (m3): void on silence outside an interval -> V1m2 goes red.

    [Fact]
    public void V1e2_RequeueGoesToTheEndOfTheBlock_AndKeepsTheTrial()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo)).IsTrue();
        var trial1 = f.Vm.CurrentTrial;

        f.Vm.ReportRenderGap(100);

        // The block carries on with the next scheduled trial; the voided one is appended.
        Check.That(f.Vm.CurrentTrial).IsEqualTo(f.Schedule.DriftTrials[1]);

        f.PlayBlock();

        var drift = f.Blocks[0];
        Check.That(drift.Trials).HasSize(11);
        Check.That(drift.Trials[1].Trial).IsEqualTo(f.Schedule.DriftTrials[1]);
        Check.That(drift.Trials[^1].Trial).IsEqualTo(trial1);
        Check.That(drift.Tally.BPicks).IsEqualTo(10);
        Check.That(drift.Tally.APicks).IsEqualTo(0);
    }

    // Mutation (o10): append the requeue at the front -> the next current trial is trial 1 again.

    // --------------------------------------------------------------------------- V1 (h), P12: the void cap

    [Fact]
    public void V1h_FourthVoidEndsTheBlock_DriftCapSkipsStatic_AndGoesToResult()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo)).IsTrue();

        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);

        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Blocks[0].Block).IsEqualTo(SubPixelAbBlock.Drift);
        Check.That(f.Blocks[0].Tally.Voids).IsEqualTo(4);
        Check.That(f.Blocks[0].Tally.Scored).IsEqualTo(0);

        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.BlocksCompleted);
        Check.That(f.Sessions[0].Aborted).IsFalse();
        Check.That(f.Sessions[0].Blocks).HasSize(1);
        Check.That(f.Runs).IsEmpty();

        // No conviction: the run goes straight to Result, not Ask (canonical (i)).
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Result);
        Check.That(f.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Drift);
        Check.That(f.Vm.Session1Verdict).IsNotNull();
        Check.That(f.Vm.Session1Verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Incomplete);
        Check.That(f.Vm.Session1Verdict.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.Skipped);

        f.Vm.AcknowledgeResult();
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.Completed);
    }

    [Fact]
    public void V1h2_StaticCap_KeepsTheDriftVerdict_AndGoesToResult()
    {
        var f = new Harness();
        f.StartToTrials();
        f.PlayBlock();

        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Blocks[0].Block).IsEqualTo(SubPixelAbBlock.Drift);
        Check.That(f.Blocks[0].Tally.BPicks).IsEqualTo(10);

        Check.That(f.DriveUntil(() =>
            f.Vm.CurrentBlock == SubPixelAbBlock.Static
            && f.Vm.Phase == SubPixelProbePhase.Interval1
            && !f.Vm.IsDemo)).IsTrue();

        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);

        Check.That(f.Blocks).HasSize(2);
        Check.That(f.Blocks[1].Block).IsEqualTo(SubPixelAbBlock.Static);
        Check.That(f.Blocks[1].Tally.Voids).IsEqualTo(4);

        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Blocks).HasSize(2);

        // The static cap leaves the drift conviction standing: the Ask screen appears, with the
        // static readout marked incomplete.
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ask);
        Check.That(f.Vm.Session1Verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(f.Vm.Session1Verdict.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.Incomplete);
    }

    [Fact]
    public void V1h3_P12_DriftWithExactlyThreeVoids_StillRunsTheStaticBlock()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo)).IsTrue();
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.Vm.ReportRenderGap(100);
        f.PlayBlock();

        Check.That(f.Blocks[0].Tally.Voids).IsEqualTo(3);
        Check.That(f.Sessions).IsEmpty();
        Check.That(f.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Static);
    }

    // Mutation (h1): the cap > MaxVoidsPerBlock becomes > +1 -> the 4th void does not end the
    //   block (the Phase == Result check goes red).
    // Mutation (h2): the capped-drift conditional always starts the next block -> the static block
    //   runs after the cap -> red.
    // Mutation (o1): the continue condition <= becomes < -> a 3-void drift skips static -> red.
    // Mutation (o6): the cap comparison > becomes >= -> a 3-void block is cut -> red.

    // ------------------------------------------------------------------------- V1 (g), (h): the eyes-closed exit

    [Fact]
    public void V1g_EyesClosed_InvalidSamplesAlone_EndTheRunAfterTenSeconds()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();

        // Closed eyes: samples keep arriving, flagged invalid.
        f.Valid = false;
        f.Drive(9900);
        Check.That(f.Runs).IsEmpty();

        f.Drive(600);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.EyesClosed);
        Check.That(f.Sessions[0].Aborted).IsTrue();
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EyesClosed);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);
    }

    [Fact]
    public void V1g2_EyesClosed_SilenceEndsTheRun()
    {
        var f = new Harness();
        f.StartToTrials();
        f.Drive(600);

        // A dropped tracker: no samples at all, so only the timer can notice.
        f.Time.Advance(9990);
        Check.That(f.Runs).IsEmpty();

        f.Time.Advance(30);
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EyesClosed);
    }

    [Fact]
    public void V1g3_ValidSamples_KeepTheRunAlivePastTenSeconds()
    {
        var f = new Harness();
        f.StartToTrials();

        f.Drive(12_000);
        Check.That(f.Runs).IsEmpty();
        Check.That(f.Vm.Phase).IsNotEqualTo(SubPixelProbePhase.Ended);

        f.Time.Advance(10_100);
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EyesClosed);
    }

    [Fact]
    public void V1g4_P13_SilenceFromStart_ExitsAfterTenSeconds()
    {
        var f = new Harness();
        f.Vm.Start();

        f.Time.Advance(9990);
        Check.That(f.Runs).IsEmpty();

        f.Time.Advance(20);
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EyesClosed);
        Check.That(f.Sessions).IsEmpty();
    }

    // Mutation (g1): re-arm the eyes-closed timer on ANY sample -> the invalid stream never trips.
    // Mutation (g2): evaluate the timeout only inside Receive -> the silence case never trips.
    // Mutation (g3): a single 10 s window from Start -> V1g3 goes red.
    // Mutation (o2): the eyes-closed timer not armed at Start -> V1g4 never trips.

    // --------------------------------------------------------------------------------- V1 (i): the full run

    [Fact]
    public void V1i_P7_FullRun_RaisesInOrder_AndAsksOnlyOnAConviction()
    {
        var f = new Harness();
        f.StartToTrials();

        // Session 1: drift then static. Ten B picks in drift convict.
        f.PlayBlock();
        f.PlayBlock();

        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Session).IsEqualTo(SubPixelProbeSessionKind.Session1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.BlocksCompleted);
        Check.That(f.Sessions[0].Aborted).IsFalse();
        Check.That(f.Sessions[0].Blocks).HasSize(2);
        Check.That(f.Runs).IsEmpty();

        // SessionCompleted is raised before the Ask screen: the Ask phase change is its successor.
        Check.That(f.Vm.Session1Verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ask);
        Check.That(f.Events[^2]).IsEqualTo("S:Session1:BlocksCompleted:False:2");
        Check.That(f.Events[^1]).IsEqualTo("P:Ask");

        // Ask -> Yes starts session 2 in the same run.
        f.Vm.ContinueToLiveGaze();
        f.PlayBlock();

        Check.That(f.Sessions).HasSize(2);
        Check.That(f.Sessions[1].Session).IsEqualTo(SubPixelProbeSessionKind.Session2);
        Check.That(f.Sessions[1].Blocks).HasSize(1);
        Check.That(f.Runs).IsEmpty();
        Check.That(f.Vm.Session2Verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Result);

        f.Vm.AcknowledgeResult();

        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.Completed);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
        Check.That(f.Events[^2]).IsEqualTo("P:Ended");
        Check.That(f.Events[^1]).IsEqualTo("R:Completed");
    }

    [Fact]
    public void V1i2_Acquittal_SkipsAsk_AndGoesStraightToResult()
    {
        var f = new Harness();
        f.StartToTrials();

        // Ten A picks in drift: the verdict acquits, so no Ask.
        f.PlayBlock(SubPixelAbCondition.A);
        f.PlayBlock(SubPixelAbCondition.A);

        Check.That(f.Vm.Session1Verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Acquits);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Result);
        Check.That(f.PhaseLog).Not.Contains(SubPixelProbePhase.Ask);
        Check.That(f.Sessions).HasSize(1);

        f.Vm.AcknowledgeResult();
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.Completed);
    }

    // Mutation (i1): the last block's normal end raises no SessionCompleted -> the Sessions checks.
    // Mutation (i2): SessionCompleted raised before the last BlockCompleted -> the event-seq check.
    // Mutation (i3): Ask shown on an acquittal -> V1i2's Result/Not.Contains checks go red.

    // ------------------------------------------------------------------ V1 (l): Intro, Ask and Result exits

    [Theory]
    [InlineData(SubPixelProbePhase.Intro)]
    [InlineData(SubPixelProbePhase.Ask)]
    [InlineData(SubPixelProbePhase.Result)]
    public void V1l_EndTest_EndsTheRunOnEveryScreen(SubPixelProbePhase screen)
    {
        var f = new Harness();
        f.ReachScreen(screen);

        f.Vm.SimulateActivated(SubPixelProbeViewModel.EndTestTargetId);

        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EndTest);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);
    }

    [Theory]
    [InlineData(SubPixelProbePhase.Intro)]
    [InlineData(SubPixelProbePhase.Ask)]
    [InlineData(SubPixelProbePhase.Result)]
    public void V1l2_EyesClosed_EndsTheRunOnEveryScreen(SubPixelProbePhase screen)
    {
        var f = new Harness();
        f.ReachScreen(screen);

        f.Time.Advance(10_100);

        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EyesClosed);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
    }

    [Theory]
    [InlineData(SubPixelProbePhase.Intro)]
    [InlineData(SubPixelProbePhase.Ask)]
    [InlineData(SubPixelProbePhase.Result)]
    public void V1l3_NoAnswer_EndsTheRunOnEveryScreen(SubPixelProbePhase screen)
    {
        var f = new Harness();
        f.ReachScreen(screen);

        // Valid samples keep the eyes-closed exit quiet; the screen timeout is the only exit left.
        f.Drive(60_500);
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.NoAnswer);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
    }

    // Mutation (l1): any exit removed from any of the three phases -> that theory case goes red.
    // Mutation (l2): the screen timeout not armed on Intro/Ask/Result -> V1l3 goes red.

    [Fact]
    public void V1l4_AskYes_StartsSessionTwo_AskLater_EndsAtResult()
    {
        var f = new Harness();
        f.ReachScreen(SubPixelProbePhase.Ask);

        f.Vm.ContinueToLiveGaze();

        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Interval1);
        Check.That(f.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Live);
        Check.That(f.Vm.IsDemo).IsTrue();

        var g = new Harness();
        g.ReachScreen(SubPixelProbePhase.Result);

        Check.That(g.Vm.Session2Verdict).IsNull();
        Check.That(g.Vm.Session1Verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        g.Vm.AcknowledgeResult();
        Check.That(g.Runs).HasSize(1);
        Check.That(g.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.Completed);
    }

    // Mutation (l3): Later starts session 2 -> the Result/Session2Verdict checks go red; Yes does
    //   not start session 2 -> the CurrentBlock == Live check goes red.

    // ------------------------------------------------------------------ V1 (j): Dispose, and V1 (k): PhaseAdvanced

    [Fact]
    public void V1j_P6_Dispose_UnregistersFromTheMessenger()
    {
        var f = new Harness();
        f.Vm.Start();

        Check.That(f.Messenger.IsRegistered<GazeCursorMovedMessage>(f.Vm)).IsTrue();
        f.Vm.Dispose();
        Check.That(f.Messenger.IsRegistered<GazeCursorMovedMessage>(f.Vm)).IsFalse();
    }

    // Mutation (j1): Dispose keeps the messenger registration -> red.

    [Fact]
    public void V1j2_DisposeFromABlockCompletedHandler_LeavesNoTimerAndRaisesNothingAfter()
    {
        var f = new Harness();
        int blocksSeen = 0;
        f.Vm.BlockCompleted += _ =>
        {
            blocksSeen++;
            f.Vm.Dispose();
        };

        f.StartToTrials();
        f.PlayBlock();

        Check.That(blocksSeen).IsEqualTo(1);
        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Sessions).IsEmpty();
        Check.That(f.Runs).IsEmpty();
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Idle);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);

        int phases = f.PhaseLog.Count;
        f.Time.Advance(60_000);
        Check.That(f.PhaseLog).HasSize(phases);
    }

    // Mutation (j2): no IsDisposed check after the BlockCompleted raise -> the disposed view model
    //   starts the static block -> red.
    // Mutation (j3): Dispose leaves the timers armed -> the ScheduledTimerCount check goes red.

    [Fact]
    public void V1j3_P11_EndTestFromABlockCompletedHandler_LeavesTheRunEnded()
    {
        var f = new Harness();
        f.Vm.BlockCompleted += _ => f.Vm.EndTest();

        f.StartToTrials();
        f.PlayBlock();

        // The drift block was recorded, the session aborted, and the run ended: nothing after.
        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.EndTest);
        Check.That(f.Sessions[0].Aborted).IsTrue();
        Check.That(f.Sessions[0].Blocks).HasSize(1);
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.EndTest);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
        Check.That(f.Events[^2]).IsEqualTo("P:Ended");
        Check.That(f.Events[^1]).IsEqualTo("R:EndTest");
    }

    // Mutation (F2): CompleteBlock checks only IsDisposed -> EndTest inside the handler leaves
    //   _ended unchecked -> the static block starts, Phase becomes Interval1, red.
    // Mutation (j4): EndTest drops the SessionCompleted for a live session -> the Sessions checks.

    [Fact]
    public void V1k_P5_PhaseAdvanced_OnAnswerOnset_AndOnAnswerToNext()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();
        Check.That(f.PhaseLog[^1]).IsEqualTo(SubPixelProbePhase.Answer);

        int before = f.PhaseLog.Count;
        f.Vm.SimulateActivated(SubPixelProbeViewModel.AnswerOneTargetId);

        Check.That(f.PhaseLog).HasSize(before + 1);
        Check.That(f.PhaseLog[^1]).IsEqualTo(SubPixelProbePhase.Interval1);
    }

    [Fact]
    public void V1k2_PhaseAdvanced_OnEnded_AndPhaseRaisesPropertyChanged()
    {
        var f = new Harness();
        var phaseChanges = new List<SubPixelProbePhase>();
        f.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SubPixelProbeViewModel.Phase))
                phaseChanges.Add(f.Vm.Phase);
        };

        f.StartToTrials();
        f.Vm.EndTest();

        Check.That(f.PhaseLog[^1]).IsEqualTo(SubPixelProbePhase.Ended);
        Check.That(phaseChanges).Not.IsEmpty();
        Check.That(phaseChanges[^1]).IsEqualTo(SubPixelProbePhase.Ended);
        Check.That(f.Events[^2]).IsEqualTo("P:Ended");
        Check.That(f.Events[^1]).IsEqualTo("R:EndTest");
    }

    // Mutation (k1): no PhaseAdvanced on answer onset -> P5's first check goes red.
    // Mutation (k2): no PhaseAdvanced on answer -> next -> P5's count check goes red.
    // Mutation (k3/F8): no PhaseAdvanced on the terminal transition -> V1k2's PhaseLog check.

    // ------------------------------------------------------------------- V1: dwell contract (P14, P15, o4, o8)

    [Fact]
    public void V1c4_AnswerDwell_ReadsToolbarButtonDwellMsLive_AndHonoursTheRegisteredRadius()
    {
        var f = new Harness();
        f.StartToTrials();

        // The setting changes between construction and registration: the dwell must be read at
        // registration, not cached at construction.
        f.Dwell.ToolbarButtonDwellMs = 400;
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => f.AnswerClicks++);
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

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
        Check.That(f.Vm.CurrentAPicks).IsEqualTo(first.Order == SubPixelAbOrder.AFirst ? 1 : 0);
        Check.That(f.Vm.CurrentBPicks).IsEqualTo(first.Order == SubPixelAbOrder.AFirst ? 0 : 1);

        // The next trial re-reads the setting: 200 ms now.
        f.Dwell.ToolbarButtonDwellMs = 200;
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => f.AnswerClicks++);
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

        reached = f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer, at: Inside);
        Check.That(reached).IsTrue();
        f.Drive(270, at: Inside);

        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(2);
    }

    // Mutation (c4): use the caller's dwellMs instead of the settings value -> the 400 ms answer
    //   arrives too early; hardcoding 700 goes red the same way.
    // Mutation (c5): ignore radiusPhysical (huge radius) -> the outside hold answers.

    [Fact]
    public void V1c5_P14_InvalidSamples_DoNotAdvanceAnswerDwell()
    {
        var f = new Harness();
        f.StartToTrials();
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => { });
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();
        f.Valid = false;
        f.Drive(1500, ButtonOne);

        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);
    }

    [Fact]
    public void V1c6_o4_GapSamples_DoNotAdvanceAnswerDwell()
    {
        var f = new Harness();
        f.StartToTrials();
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => { });
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();

        // Held (blink) frames keep arriving with a frozen position: they must not complete a dwell.
        for (int i = 0; i < 50; i++) f.TickGap(30, ButtonOne);
        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(0);
    }

    [Fact]
    public void V1c7_P15_ResponseTime_FromAnswerOnset()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();
        f.Drive(1200);
        long expected = f.Vm.PhaseElapsedMs;
        Check.That(expected).IsStrictlyGreaterThan(1199);

        f.Vm.SimulateActivated(SubPixelProbeViewModel.AnswerOneTargetId);
        Check.That(f.Vm.CurrentBlockTrialOutcomes[0].ResponseTimeMs).IsEqualTo(expected);
    }

    [Fact]
    public void V1c8_o8_DisposeFromTheAnswerCallback_LeavesNoTimerAndRaisesNothingAfter()
    {
        var f = new Harness();
        f.StartToTrials();
        f.Vm.Register(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, 75, 700, () => f.Vm.Dispose());
        f.Vm.UpdatePosition(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control, ButtonOne);

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();
        int phases = f.PhaseLog.Count;

        f.Vm.SimulateActivated(SubPixelProbeViewModel.AnswerOneTargetId);

        Check.That(f.Vm.ScoredInCurrentBlock).IsEqualTo(1);
        Check.That(f.PhaseLog).HasSize(phases);
        Check.That(f.Time.ScheduledTimerCount).IsEqualTo(0);
    }

    // Mutation (o3): invalid samples feed the detector -> the 1500 ms hold answers.
    // Mutation (o4): IsInGap samples feed the detector -> the 50 gap frames answer.
    // Mutation (o5): ResponseTimeMs recorded as 0 -> P15 goes red.
    // Mutation (o8): the answer path's post-callback guard removed -> a PhaseAdvanced fires after
    //   dispose: the phase-count check goes red.

    // ------------------------------------------------------------------- V1: statistics (F6, o7)

    [Fact]
    public void V1stats_PerTrialStatistics_ResetAtEachTrial_AndIgnoreOtherPhases()
    {
        var f = new Harness();
        f.StartToTrials();

        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Interval1 && !f.Vm.IsDemo)).IsTrue();
        f.Vm.ReportRenderGap(60);
        Check.That(f.Vm.CurrentBlockVoids).IsEqualTo(1);
        Check.That(f.Vm.CurrentBlockTrialOutcomes[0].LongestRenderGapMs).IsEqualTo(60);

        // Trial 2: no interval reports; a report on its answer screen must not leak into the record.
        Check.That(f.DriveUntil(() => f.Vm.Phase == SubPixelProbePhase.Answer)).IsTrue();
        f.Vm.ReportRenderGap(200);
        f.Vm.SimulateActivated(SubPixelProbeViewModel.AnswerOneTargetId);

        Check.That(f.Vm.CurrentBlockTrialOutcomes).HasSize(2);
        Check.That(f.Vm.CurrentBlockTrialOutcomes[1].LongestRenderGapMs).IsEqualTo(0);
    }

    // Mutation (F6): record render gaps outside the intervals -> the 200 ms answer-phase report
    //   leaks into trial 2: red.
    // Mutation (o7): the per-trial statistics are not reset at interval 1 -> trial 2 inherits the
    //   60 ms: red.

    // --------------------------------------------------------------------------- session-2 resume

    [Fact]
    public void V1resume_ResumeRunsOnlyTheLiveBlock_AndEndsAtResultThenRunEnded()
    {
        var verdict = ConvictingSession1();
        var f = new Harness(SubPixelProbeStartMode.ResumeSession2, verdict);

        f.Vm.Start();
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Intro);
        Check.That(f.Vm.Session1Verdict).IsEqualTo(verdict);

        f.Vm.BeginTrials();

        // The live block only: no drift, no static, and never the Ask screen.
        Check.That(f.Vm.CurrentBlock).IsEqualTo(SubPixelAbBlock.Live);
        Check.That(f.Vm.IsDemo).IsTrue();

        f.PlayBlock();

        Check.That(f.Blocks).HasSize(1);
        Check.That(f.Blocks[0].Block).IsEqualTo(SubPixelAbBlock.Live);
        Check.That(f.Sessions).HasSize(1);
        Check.That(f.Sessions[0].Session).IsEqualTo(SubPixelProbeSessionKind.Session2);
        Check.That(f.Sessions[0].Reason).IsEqualTo(SubPixelProbeEndReason.BlocksCompleted);
        Check.That(f.PhaseLog).Not.Contains(SubPixelProbePhase.Ask);
        Check.That(f.Runs).IsEmpty();
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Result);
        Check.That(f.Vm.Session2Verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(f.Vm.Session1Verdict).IsEqualTo(verdict);

        f.Vm.AcknowledgeResult();
        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(SubPixelProbeRunEndReason.Completed);
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
    }

    // Mutation (resume1): BeginTrials ignores the start mode and runs session 1 -> the live-block
    //   and no-Ask assertions go red.

    [Fact]
    public void V1resume2_NonConvictingOrMissingSession1_IsRefused()
    {
        var acquit = SubPixelAbVerdict.Session1(
            new SubPixelAbBlockTally(5, 5, 0), new SubPixelAbBlockTally(5, 5, 0));
        var incomplete = SubPixelAbVerdict.Session1(new SubPixelAbBlockTally(0, 0, 4), null);

        Check.That(SubPixelProbeViewModel.CanResumeSession2(acquit)).IsFalse();
        Check.That(SubPixelProbeViewModel.CanResumeSession2(incomplete)).IsFalse();
        Check.That(SubPixelProbeViewModel.CanResumeSession2(null)).IsFalse();
        Check.That(SubPixelProbeViewModel.CanResumeSession2(ConvictingSession1())).IsTrue();

        ArgumentException? ex = null;
        try { _ = new Harness(SubPixelProbeStartMode.ResumeSession2, acquit); }
        catch (ArgumentException e) { ex = e; }
        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("resumedSession1Verdict");

        ex = null;
        try { _ = new Harness(SubPixelProbeStartMode.ResumeSession2, null); }
        catch (ArgumentException e) { ex = e; }
        Check.That(ex).IsNotNull();
    }

    // Mutation (resume2): the constructor's refusal guard removed -> the throws never happen.

    [Theory]
    [InlineData(SubPixelProbeRunEndReason.EndTest)]
    [InlineData(SubPixelProbeRunEndReason.EyesClosed)]
    [InlineData(SubPixelProbeRunEndReason.NoAnswer)]
    public void V1resume3_ResumeIntroHonoursEveryExit(SubPixelProbeRunEndReason exit)
    {
        var f = new Harness(SubPixelProbeStartMode.ResumeSession2, ConvictingSession1());
        f.Vm.Start();
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Intro);

        switch (exit)
        {
            case SubPixelProbeRunEndReason.EndTest:
                f.Vm.SimulateActivated(SubPixelProbeViewModel.EndTestTargetId);
                break;
            case SubPixelProbeRunEndReason.EyesClosed:
                f.Time.Advance(10_100);
                break;
            default:
                f.Drive(60_500);
                break;
        }

        Check.That(f.Runs).HasSize(1);
        Check.That(f.Runs[0]).IsEqualTo(exit);
        Check.That(f.Sessions).IsEmpty();
        Check.That(f.Vm.Phase).IsEqualTo(SubPixelProbePhase.Ended);
    }

    // Mutation (resume3): the resume skips the Intro (Start goes straight to the live block) ->
    //   the Phase == Intro check and all three exit cases go red.

    // ------------------------------------------------------------------------------------ V1 helpers

    private static SubPixelAbSession1Verdict ConvictingSession1() =>
        SubPixelAbVerdict.Session1(
            new SubPixelAbBlockTally(2, 8, 0), new SubPixelAbBlockTally(5, 5, 0));

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
        public readonly List<SubPixelProbeRunEndReason> Runs = new();
        public readonly List<string> Events = new();

        public bool Valid = true;
        public long GazeUs;
        public int AnswerClicks;
        public int EndTestClicks;

        public Harness(
            SubPixelProbeStartMode startMode = SubPixelProbeStartMode.FullRun,
            SubPixelAbSession1Verdict? resumedSession1Verdict = null)
        {
            Schedule = SubPixelAbSchedule.Create(Seed, Screen);
            Vm = new SubPixelProbeViewModel(
                Messenger, Time, Schedule, Dwell, () => Valid, startMode, resumedSession1Verdict);
            Vm.PhaseAdvanced += () =>
            {
                PhaseLog.Add(Vm.Phase);
                Events.Add("P:" + Vm.Phase);
            };
            Vm.BlockCompleted += outcome =>
            {
                Blocks.Add(outcome);
                Events.Add("B:" + outcome.Block);
            };
            Vm.SessionCompleted += outcome =>
            {
                Sessions.Add(outcome);
                Events.Add($"S:{outcome.Session}:{outcome.Reason}:{outcome.Aborted}:{outcome.Blocks.Count}");
            };
            Vm.RunEnded += reason =>
            {
                Runs.Add(reason);
                Events.Add("R:" + reason);
            };
        }

        /// <summary>Starts the run and leaves the Intro screen.</summary>
        public void StartToTrials()
        {
            Vm.Start();
            Check.That(Vm.Phase).IsEqualTo(SubPixelProbePhase.Intro);
            Vm.BeginTrials();
        }

        /// <summary>Plays session 1 to its normal end (drift then static), answering <paramref name="pick"/>.</summary>
        public void CompleteSession1(SubPixelAbCondition pick = SubPixelAbCondition.B)
        {
            PlayBlock(pick);
            PlayBlock(pick);
        }

        /// <summary>Parks the run on the given screen (Intro, Ask or Result) for the exit tests.</summary>
        public void ReachScreen(SubPixelProbePhase screen)
        {
            Vm.Start();
            if (screen == SubPixelProbePhase.Intro) return;

            Vm.BeginTrials();
            CompleteSession1();

            if (screen == SubPixelProbePhase.Ask) return;

            Check.That(Vm.Phase).IsEqualTo(SubPixelProbePhase.Ask);
            Vm.DeclineLiveGaze();
            Check.That(Vm.Phase).IsEqualTo(SubPixelProbePhase.Result);
        }

        /// <summary>Advances the clock by <paramref name="ms"/> and sends one gaze sample at <paramref name="at"/>.</summary>
        public void Tick(long ms = 30, PhysicalPoint? at = null)
        {
            Time.Advance(ms);
            GazeUs += ms * 1000;
            Messenger.Send(new GazeCursorMovedMessage(at ?? Neutral, GazeUs));
        }

        /// <summary>Advances by <paramref name="ms"/> and sends one held (blink) frame.</summary>
        public void TickGap(long ms = 30, PhysicalPoint? at = null)
        {
            Time.Advance(ms);
            GazeUs += ms * 1000;
            Messenger.Send(new GazeCursorMovedMessage(new GazeFrame(at ?? Neutral, GazeUs, IsInGap: true)));
        }

        /// <summary>Drives gaze samples at 30 ms cadence for <paramref name="ms"/> total.</summary>
        public void Drive(long ms, PhysicalPoint? at = null)
        {
            for (long elapsed = 0; elapsed < ms; elapsed += 30)
                Tick(Math.Min(30, ms - elapsed), at);
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
            while (Blocks.Count < target && Runs.Count == 0 && elapsed < 300_000)
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
