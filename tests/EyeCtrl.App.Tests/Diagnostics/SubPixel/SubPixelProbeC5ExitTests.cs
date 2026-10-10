namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// H1, M3 and M4 (fixup 02): real C5 events driven through C7 — the End-test exit, a full session 1
/// played in virtual time to the Ask screen, the results writes those trigger, and the render-gap
/// clock. The run's C5 is built on an injected <see cref="ManualTimeProvider"/>, so nothing here
/// depends on real time; the window C7 builds stays the recording double.
/// </summary>
[Collection("WpfApplication")]
public class SubPixelProbeC5ExitTests
{
    private static (ProbeTestWorld World, ManualTimeProvider Time, ProbeRunDriver Driver) NewRunWorld(
        Func<double?>? refreshRateHz = null)
    {
        var world = new ProbeTestWorld(holdKeyboardClaim: true, refreshRateHz: refreshRateHz);
        var time = new ManualTimeProvider();
        world.Host.ViewModelFactory = (schedule, _, _, mode, resume) =>
        {
            var vm = new SubPixelProbeViewModel(
                world.Messenger, time, schedule, world.Settings.Dwell, () => true, mode, resume);
            world.Vms.Add(vm);
            return vm;
        };

        world.Coordinator.StartForTest();
        world.Pump();
        var session = world.Session;
        return (world, time, new ProbeRunDriver(time, world.Messenger, session.Vm!));
    }

    private static JsonDocument ReadResult(ProbeSession session) =>
        JsonDocument.Parse(File.ReadAllText(session.Writer!.FilePath));

    // ---- H1-1: End test through C5 → posted Restore → closed window + endTest in the file ---------

    [Fact]
    public void EndTestThroughC5_PostsTheRestore_ThenClosesAndWritesEndTest()
    {
        var (world, _, driver) = NewRunWorld();
        using var worldLifetime = world;
        var session = world.Session;
        var vm = session.Vm!;
        driver.StartToTrials();

        // Three engine samples 30 ms apart, so the recorded cadence is measurable (M3).
        world.Provider.Raise(new GazePoint(500, 500, 0, IsValid: true));
        world.Provider.Raise(new GazePoint(500, 500, 30_000, IsValid: true));
        world.Provider.Raise(new GazePoint(500, 500, 60_000, IsValid: true));

        vm.SimulateActivated(SubPixelProbeViewModel.EndTestTargetId);

        // The C5-triggered Restore is POSTED: the run is still live straight after the raise.
        Check.WithCustomMessage("RunEnded restored inline; C5 would be disposed inside its own call stack")
            .That(world.Modal.ProbeOpen).IsTrue();
        Check.That(world.Host.CountOf("DisposeViewModel")).IsEqualTo(0);
        Check.That(world.Messenger.IsRegistered<GazeCursorMovedMessage>(vm)).IsTrue();

        world.Pump();

        Check.WithCustomMessage("RunEnded never restored; the opaque window would stay up")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.Windows[0].ClosedObserved).IsTrue();
        Check.That(world.Messenger.IsRegistered<GazeCursorMovedMessage>(vm)).IsFalse();

        using var document = ReadResult(session);
        Check.WithCustomMessage("C7 never wrote the run's end reason")
            .That(document.RootElement.GetProperty("runEndReason").GetString()).IsEqualTo("endTest");
        Check.That(document.RootElement.GetProperty("sampleCadenceMs").GetDouble()).IsCloseTo(30.0, 0.001);
        Check.WithCustomMessage(
                "A C5-ended run must not carry an abortReason; the window-close re-entry made every run "
                + "look aborted (fixup 04 R1/HR2)")
            .That(document.RootElement.TryGetProperty("abortReason", out _)).IsFalse();
    }

    // ---- H1-2: session 1 in virtual time → Ask, with the blocks and session in the file ----------

    [Fact]
    public void Session1ToAConvictingEnd_ReachesAsk_WithTheWindowOpen_AndWritesBlocksAndSession()
    {
        var (world, _, driver) = NewRunWorld();
        using var _ = world;
        var session = world.Session;

        driver.StartToTrials();
        driver.PlayBlock(SubPixelAbCondition.B);   // drift, all B → convicts
        driver.PlayBlock(SubPixelAbCondition.B);   // static, supporting evidence

        // Drain anything posted at SessionCompleted: a Restore posted there would end the run and
        // the Continue-now screen could never appear (the rev-6.1 bug).
        world.Pump();

        Check.WithCustomMessage("SessionCompleted restored; the Continue-now screen could never appear")
            .That(session.Vm!.Phase).IsEqualTo(SubPixelProbePhase.Ask);
        Check.That(world.Modal.ProbeOpen).IsTrue();
        Check.That(world.Host.Windows[0].ClosedObserved).IsFalse();

        using var document = ReadResult(session);
        Check.WithCustomMessage("C7 never wrote the blocks")
            .That(document.RootElement.GetProperty("blocks").GetArrayLength()).IsEqualTo(2);
        var sessions = document.RootElement.GetProperty("sessions");
        Check.WithCustomMessage("C7 never wrote the session result")
            .That(sessions.GetArrayLength()).IsEqualTo(1);
        Check.That(sessions[0].GetProperty("outcome").GetString()).IsEqualTo("convicts");
        Check.That(sessions[0].GetProperty("session").GetString()).IsEqualTo("session1");
    }

    // ---- M3: a completed run keeps C5's own reason -------------------------------------------------

    [Fact]
    public void ACompletedRun_WritesCompleted_NotTheAbortFallback()
    {
        var (world, _, driver) = NewRunWorld();
        using var worldLifetime = world;
        var session = world.Session;
        driver.StartToTrials();
        driver.PlayBlock(SubPixelAbCondition.B);
        driver.PlayBlock(SubPixelAbCondition.B);

        session.Vm!.DeclineLiveGaze();       // Ask → Result
        session.Vm.AcknowledgeResult();      // Result → RunEnded(Completed)
        world.Pump();

        using var document = ReadResult(session);
        Check.WithCustomMessage(
                "C5's own run-end reason must survive; a completed run says 'completed', not the abort fallback")
            .That(document.RootElement.GetProperty("runEndReason").GetString()).IsEqualTo("completed");
        Check.WithCustomMessage(
                "A completed run must not carry an abortReason; the window-close re-entry made every run "
                + "look aborted (fixup 04 R1/HR2)")
            .That(document.RootElement.TryGetProperty("abortReason", out _)).IsFalse();
    }

    // ---- N5: the default frame rate interprets Stopwatch-scale ticks ------------------------------

    [StaFact]
    public void TheDefaultFrameRate_InterpretsStopwatchScaleTicks()
    {
        // N5 / M4-rate + M4-nodiv: inject Stopwatch-scale ticks with frameTicksPerMs 0, so the
        // production rate is selected. A rate of 1.0 or a raw-tick gap makes the 40 ms case void.
        WpfApplicationGate.EnsureApplicationWithTokens();

        foreach (var (gapMs, expectVoid) in new[] { (40L, false), (60L, true) })
        {
            var (world, _, driver) = NewRunWorld();
            using var _ = world;
            var session = world.Session;
            driver.StartToTrials();
            driver.DriveToFirstScoredInterval1();

            long baseTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedMs = 0;
            var window = new SubPixelProbeWindow(
                null, session.Vm!, new CursorSettings(), () => { }, viewFault: () => { },
                scale: 1.0,
                frameTicks: () => baseTicks + (long)(System.Diagnostics.Stopwatch.Frequency * elapsedMs / 1000.0),
                frameTicksPerMs: 0);
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                window.OnFrame();
                elapsedMs = gapMs;
                window.OnFrame();

                Check.WithCustomMessage(
                        $"A {gapMs} ms Stopwatch-scale gap must {(expectVoid ? "" : "not ")}void the trial; "
                        + $"phase is {session.Vm!.Phase}")
                    .That(session.Vm!.Phase == SubPixelProbePhase.VoidHold).IsEqualTo(expectVoid);
            }
            finally
            {
                window.Close();
            }
        }
    }

    // ---- N7: the injected refresh rate reaches the file -------------------------------------------

    [Fact]
    public void TheInjectedRefreshRate_IsRecorded()
    {
        var (world, _, driver) = NewRunWorld(() => 60.25);
        using var _ = world;
        var session = world.Session;
        driver.StartToTrials();
        session.Vm!.SimulateActivated(SubPixelProbeViewModel.EndTestTargetId);
        world.Pump();

        using var document = ReadResult(session);
        Check.WithCustomMessage("displayRefreshHz is absent although the refresh provider returned 60.25")
            .That(document.RootElement.GetProperty("displayRefreshHz").GetDouble()).IsCloseTo(60.25, 1e-9);
    }

    // ---- R2: the two-session file built by the writer ---------------------------------------------

    [Fact]
    public void ASession2CompletedInTheSameRun_EndsTheResumeOffer()
    {
        var (world, _, driver) = NewRunWorld();
        using var _ = world;
        var session = world.Session;
        driver.StartToTrials();
        driver.PlayBlock(SubPixelAbCondition.B);   // drift → convicts
        driver.PlayBlock(SubPixelAbCondition.B);   // static
        session.Vm!.ContinueToLiveGaze();          // Ask → Yes
        driver.PlayBlock(SubPixelAbCondition.B);   // live block completes in the same run

        using var document = ReadResult(session);
        Check.WithCustomMessage("The same run must have written both sessions")
            .That(document.RootElement.GetProperty("sessions").GetArrayLength()).IsEqualTo(2);

        Check.WithCustomMessage("A completed session 2 in the same file must end the resume offer")
            .That(SubPixelAbResultReader.ReadNewestConvictingSession1(world.ResultsDir)).IsNull();
    }

    [Fact]
    public void ASession2AbortedInTheSameRun_StillOffersTheResume()
    {
        var (world, _, driver) = NewRunWorld();
        using var _ = world;
        var session = world.Session;
        driver.StartToTrials();
        driver.PlayBlock(SubPixelAbCondition.B);
        driver.PlayBlock(SubPixelAbCondition.B);
        session.Vm!.ContinueToLiveGaze();
        session.Vm.EndTest();                      // aborted mid-session-2
        world.Pump();

        using var document = ReadResult(session);
        Check.That(document.RootElement.GetProperty("sessions").GetArrayLength()).IsEqualTo(2);
        Check.That(document.RootElement.GetProperty("sessions")[1].GetProperty("aborted").GetBoolean())
            .IsTrue();

        var verdict = SubPixelAbResultReader.ReadNewestConvictingSession1(world.ResultsDir);
        Check.WithCustomMessage("An aborted session 2 must not block the retry (review 03 R2)")
            .That(verdict).IsNotNull();
        Check.That(verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
    }

    // ---- HR12: the keyboard is hidden before the backdrop is captured ------------------------------

    [Fact]
    public void TheKeyboardIsHiddenBeforeTheBackdropIsCaptured()
    {
        var (world, _, _) = NewRunWorld();
        using var _ = world;

        var names = world.Host.Names.ToList();
        int hidden = names.IndexOf("HideKeyboard");
        int captured = names.IndexOf("CaptureBackdrop");

        Check.That(hidden).IsStrictlyGreaterThan(-1);
        Check.That(captured).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The backdrop was captured before the keyboard was hidden; the keyboard could be baked in")
            .That(hidden).IsStrictlyLessThan(captured);
    }

    // ---- M4: the render-gap clock ----------------------------------------------------------------

    [StaFact]
    public void ARenderGapOver50ms_VoidsTheTrial_And40msDoesNot()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        foreach (var (gapMs, expectVoid) in new[] { (40L, false), (60L, true) })
        {
            var (world, _, driver) = NewRunWorld();
            using var _ = world;
            var session = world.Session;
            driver.StartToTrials();
            driver.DriveToFirstScoredInterval1();

            long ticks = 1_000_000;
            var window = new SubPixelProbeWindow(
                null, session.Vm!, new CursorSettings(), () => { }, viewFault: () => { },
                scale: 1.0, frameTicks: () => ticks, frameTicksPerMs: 1.0);
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                window.OnFrame();          // anchors the frame clock
                ticks += gapMs;
                window.OnFrame();          // reports the gap to C5

                Check.WithCustomMessage(
                        $"A {gapMs} ms render gap must {(expectVoid ? "" : "not ")}void the trial; "
                        + $"phase is {session.Vm!.Phase}")
                    .That(session.Vm!.Phase == SubPixelProbePhase.VoidHold).IsEqualTo(expectVoid);
            }
            finally
            {
                window.Close();
            }
        }
    }
}
