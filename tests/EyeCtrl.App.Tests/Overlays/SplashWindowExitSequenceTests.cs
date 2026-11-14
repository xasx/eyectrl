// tests/EyeCtrl.App.Tests/Overlays/SplashWindowExitSequenceTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The exit sequence runs on real storyboards: BeginExit commits the catch-up from the CURRENT
/// arc values, the title follows, hold and fade end in a Closed controller and a closed window.
/// Budgets sit BELOW the 3 s hard deadline so a stuck sequence fails the test instead of being
/// masked by the backstop (r4 #7). The splash window itself is never shown.
///
/// <para>The sequence cannot ride the pump alone: storyboard <c>Completed</c> events dispatch only
/// while the thread's MediaContext render loop is alive, and without a shown window that loop dies
/// once no animated value changes any more — the animation still reaches its end value, but the
/// completion tick never runs (probe P1/P3/P5/P6 vs P2/P7/P8). So each test opens a tiny
/// Prepare'd off-screen KEEPER window on the test thread; it keeps the render loop pumping while
/// the splash's own storyboards, clocks and timers run exactly as in production. The keeper is
/// invisible (InvisibleTestWindow.Prepare: Opacity 0, no activation) and off every monitor.</para>
///
/// <para>The splash itself is constructed through InvisibleTestWindow.PrepareCloaked: the
/// no-visible-window guard requires every real-window construction in a test to be prepared, and
/// the splash cannot join the Opacity=0 family (its XAML animates element opacities, which the
/// InvisibleTestWindowTests pins reject) — the DWM cloak assumes nothing about Opacity.</para>
/// </summary>
[Collection("WpfApplication")]
public class SplashWindowExitSequenceTests
{
    /// <summary>
    /// Opens the render-loop keeper (see the class comment): a 10x10 window at -32000 that is
    /// never visible to the user — InvisibleTestWindow.Prepare'd (the no-visible-window guard's
    /// trusted pattern) and positioned off every monitor on top of that.
    /// </summary>
    private static Window OpenRenderLoopKeeper()
    {
        var keeper = InvisibleTestWindow.Prepare(new Window
        {
            Width = 10,
            Height = 10,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Content = new Border(),
        });
        keeper.Show();
        return keeper;
    }

    [StaFact]
    public void BeginExit_DuringEntrance_RunsCatchUpTitleHoldExit_AndCloses()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = InvisibleTestWindow.PrepareCloaked(new SplashWindow());
        var keeper = OpenRenderLoopKeeper();
        try
        {
            splash.OnSplashShown();
            splash.BeginExit();     // warm start: fill never started → full 3 s course remains

            var closed = StaTestPump.PumpUntil(
                () => splash.CurrentBeat is SplashBeat.Closed,
                TimeSpan.FromSeconds(2.5));                     // < the 3 s hard deadline
            Check.That(closed).IsTrue();
            Check.That(splash.CurrentBeat).IsEqualTo(SplashBeat.Closed);
            Check.That(splash.ClosedReason).IsEqualTo("closed");   // NOT a dead-man/hard-deadline rescue
        }
        finally
        {
            keeper.Close();
            splash.ForceClose();
        }
    }

    [StaFact]
    public void BeginExit_Twice_IsHarmless()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = InvisibleTestWindow.PrepareCloaked(new SplashWindow());
        var keeper = OpenRenderLoopKeeper();
        try
        {
            splash.OnSplashShown();
            splash.BeginExit();
            splash.BeginExit();
            var closed = StaTestPump.PumpUntil(() => splash.CurrentBeat is SplashBeat.Closed, TimeSpan.FromSeconds(2.5));
            Check.That(closed).IsTrue();
            Check.That(splash.ClosedReason).IsEqualTo("closed");
        }
        finally
        {
            keeper.Close();
            splash.ForceClose();
        }
    }

    /// <summary>
    /// Slow start (spec §6.1): the base course completes on its own and the beat becomes Filled —
    /// the splash waits, no title, no exit. The tail starts only when readiness arrives (r4 #5).
    /// The pump budget (~4 s) exceeds the 3.55 s base course on purpose.
    /// </summary>
    [StaFact]
    public void SlowStart_BaseFillCompletes_WaitsInFilled_ThenClosesAfterReadiness()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = InvisibleTestWindow.PrepareCloaked(new SplashWindow());
        var keeper = OpenRenderLoopKeeper();
        try
        {
            splash.OnSplashShown();
            var filled = StaTestPump.PumpUntil(() => splash.CurrentBeat is SplashBeat.Filled, TimeSpan.FromSeconds(4.2));
            Check.That(filled).IsTrue();
            Check.That(splash.ClosedReason).IsNull();           // still waiting — not closed by anything

            splash.BeginExit();                                  // remaining ≈ 0 → no catch-up, straight tail
            var tail = StaTestPump.PumpUntil(
                () => splash.CurrentBeat is SplashBeat.Hold or SplashBeat.Exit or SplashBeat.Closed,
                TimeSpan.FromSeconds(1.0));
            Check.That(tail).IsTrue();
            // I1 pin: the straight-to-title tail must leave the Filling fill clock holding the
            // finals — stopping it (as the code once did) reverts the resting arc on the next tick.
            // A revert would read 0 / −180, far outside the tolerance.
            Check.WithCustomMessage($"DwellArc.Opacity while the tail runs = {splash.DwellArc.Opacity} (reverted?)")
                .That(splash.DwellArc.Opacity).IsCloseTo(1, 1e-6);
            Check.WithCustomMessage($"ArcRotate.Angle while the tail runs = {splash.ArcRotate.Angle} (reverted?)")
                .That(splash.ArcRotate.Angle).IsCloseTo(0, 1e-6);

            var closed = StaTestPump.PumpUntil(() => splash.CurrentBeat is SplashBeat.Closed, TimeSpan.FromSeconds(1.5));
            Check.That(closed).IsTrue();
            Check.That(splash.ClosedReason).IsEqualTo("closed");
        }
        finally
        {
            keeper.Close();
            splash.ForceClose();
        }
    }

    /// <summary>
    /// Mid-fill request (the scenario r4 #3 exists for): the base course is visibly playing when
    /// readiness arrives — the catch-up must CONTINUE the arc from the captured value (no backward
    /// jump, no restart) and close normally. Pins the capture-order dependency (the fill clock's
    /// ClockController.Stop() defers property reversion to the next tick, and BeginExit captures
    /// and begins in the same synchronous call) and the mid-fill band of the course-local
    /// RemainingBaseCourse read.
    /// </summary>
    [StaFact]
    public void BeginExit_MidFill_CatchUpContinuesFromTheCapturedValue_AndCloses()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = InvisibleTestWindow.PrepareCloaked(new SplashWindow());
        var keeper = OpenRenderLoopKeeper();
        try
        {
            splash.OnSplashShown();
            var playing = StaTestPump.PumpUntil(() => Math.Abs(splash.ArcRotate.Angle - (-180.0)) > 1, TimeSpan.FromSeconds(1.5));
            Check.That(playing).IsTrue();                    // the base fill clock drives the arc — it started past its 0.55 s delay
            StaTestPump.PumpUntil(() => false, TimeSpan.FromMilliseconds(900));   // stay in the fill: well past the delay
            var angleAtRequest = splash.ArcRotate.Angle;
            Check.WithCustomMessage($"the fill should be mid-course at the request, angle={angleAtRequest}")
                .That(angleAtRequest > -170.0).IsTrue();

            splash.BeginExit();
            double minAngle = double.MaxValue;
            var closed = StaTestPump.PumpUntil(() =>
            {
                minAngle = Math.Min(minAngle, splash.ArcRotate.Angle);
                return splash.CurrentBeat is SplashBeat.Closed;
            }, TimeSpan.FromSeconds(2.5));                   // < the 3 s hard deadline
            Check.That(closed).IsTrue();
            Check.That(splash.CurrentBeat).IsEqualTo(SplashBeat.Closed);
            Check.That(splash.ClosedReason).IsEqualTo("closed");
            Check.WithCustomMessage($"the arc jumped backwards: min {minAngle} against the captured {angleAtRequest}")
                .That(minAngle >= angleAtRequest - 2.0).IsTrue();
        }
        finally
        {
            keeper.Close();
            splash.ForceClose();
        }
    }
}
