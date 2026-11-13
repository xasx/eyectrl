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
}
