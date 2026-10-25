namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// B7: the eye-care surfaces are gaze-reachable. The exercise's click-only corner Skip becomes an
/// r50 mid-bottom dwell target (A1 grace, a progress outline, leave-before-arm); the banner's rings
/// become r50 discs, 24 DIP apart, below the toolbar band, with visible progress and A1's grace.
///
/// <para>
/// Everything is headless. The windows are prepared with <see cref="InvisibleTestWindow.Prepare"/>
/// before their first show (Opacity 0, no activation), the geometry is pinned through the pure
/// <c>Compute*Geometry</c> seams at DPI 1 and 2, and the dwells are driven by feeding samples
/// straight to <c>OnGaze</c> — no dispatcher pump, no input, no real window on screen.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class EyeCareDwellTests
{
    private static readonly PhysicalPoint Off = new(20, 20);

    private static DwellSettings FastDwell(int graceMs = 500) => new()
    {
        ActivationButtonDwellMs = 200,
        DwellPauseGraceMs = graceMs,
    };

    // ─────────────────────────────────────────────── the exercise Skip's geometry

    [Theory]
    [InlineData(1920.0, 1200.0, 1.0)]
    [InlineData(1920.0, 1200.0, 2.0)]
    [InlineData(1366.0, 768.0, 1.0)]
    [InlineData(1366.0, 768.0, 2.0)]
    public void ComputeSkipGeometry_IsMidBottomEdge_FullyOnScreen(double screenDipWidth, double screenDipHeight, double scale)
    {
        var (center, radius) = EyeCareExerciseWindow.ComputeSkipGeometry(
            screenDipWidth, screenDipHeight, scale, scale);

        Check.That(center.X).IsCloseTo(screenDipWidth / 2 * scale, 0.001);
        Check.That(center.Y).IsCloseTo(
            (screenDipHeight - EyeCareExerciseWindow.SkipBottomInsetDip
             - EyeCareExerciseWindow.SkipHitRadiusDip) * scale, 0.001);
        Check.That(radius).IsCloseTo(EyeCareExerciseWindow.SkipHitRadiusDip * scale, 0.001);

        // The R4 size floor and full inset.
        Check.That(radius).IsGreaterOrEqualThan(50 * scale);
        Check.That(center.X - radius).IsGreaterOrEqualThan(0);
        Check.That(center.X + radius).IsLessOrEqualThan(screenDipWidth * scale);
        Check.That(center.Y - radius).IsGreaterOrEqualThan(0);
        Check.That(center.Y + radius).IsLessOrEqualThan(screenDipHeight * scale);
    }

    /// <summary>
    /// The exit is clear of every exercise's dot path: no sampled target centre comes within the
    /// dot's own drawn radius of the Skip hit circle. The pool is the PRODUCTION pool
    /// (<see cref="AppBootstrapper.BuildExercisePool"/>), sampled over a full 45-second default
    /// run, so a new or moved exercise that sweeps across the exit fails here.
    /// </summary>
    [Theory]
    [InlineData(1920.0, 1200.0, 1.0)]
    [InlineData(1920.0, 1200.0, 2.0)]
    [InlineData(1366.0, 768.0, 1.0)]
    [InlineData(1366.0, 768.0, 2.0)]
    public void ComputeSkipGeometry_IsClearOfEveryExerciseDotPath(double screenDipWidth, double screenDipHeight, double scale)
    {
        var (skipCenter, skipRadius) = EyeCareExerciseWindow.ComputeSkipGeometry(
            screenDipWidth, screenDipHeight, scale, scale);
        var pool = AppBootstrapper.BuildExercisePool(
            new PhysicalPoint(screenDipWidth / 2 * scale, screenDipHeight / 2 * scale));

        // The dot's drawn diameter is 36 DIP (XAML TargetDot), so its edge must never reach the
        // Skip hit circle. Measured minimum today: ~96 physical px on a 1366×768 screen (the
        // 0.6·min-radius orbit's lowest point); this floor is the dot's own radius.
        var dotRadius = 18 * scale;
        const long durationUs = 45_000_000;
        foreach (var exercise in pool)
        {
            for (var t = 0L; t <= durationUs; t += 50_000)
            {
                var dot = exercise.GetTargetAt(t);
                var dx = dot.X - skipCenter.X;
                var dy = dot.Y - skipCenter.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                Check.WithCustomMessage(
                        $"{exercise.Name} passes at ({dot.X:0.#}, {dot.Y:0.#}) at t={t / 1000.0:0.#} ms — "
                        + $"{distance:0.#} px from the Skip centre, inside the {(skipRadius + dotRadius):0.#} px "
                        + "clearance the drawn dot needs.")
                    .That(distance).IsGreaterOrEqualThan(skipRadius + dotRadius);
            }
        }
    }

    [StaFact]
    public void TheSkipDisc_IsDrawnOnItsDwellTarget()
    {
        var window = NewExercise(FastDwell(), new WeakReferenceMessenger());
        try
        {
            _ = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            window.UpdateLayout();

            var target = window.SkipDwellDetector.Targets.Single();
            var root = (FrameworkElement)window.Content;
            var box = window.SkipTarget.TransformToAncestor(root)
                .TransformBounds(new Rect(window.SkipTarget.RenderSize));
            var centreX = EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalX(box.X + box.Width / 2);
            var centreY = EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalY(box.Y + box.Height / 2);

            Check.WithCustomMessage(
                    $"the drawn disc is centred at ({centreX:0.#}, {centreY:0.#}), not on its dwell target "
                    + $"({target.Center.X:0.#}, {target.Center.Y:0.#}) — the circle the user aims at moved.")
                .That(centreX).IsCloseTo(target.Center.X, 1.0);
            Check.That(centreY).IsCloseTo(target.Center.Y, 1.0);

            // The drawn diameter IS the registered hit diameter (r50), not a smaller button.
            Check.That(EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalX(box.Width))
                .IsCloseTo(2 * target.Radius, 0.5);
            Check.That(EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalY(box.Height))
                .IsCloseTo(2 * target.Radius, 0.5);
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void TheRun_RegistersTheSkipDwellOnThatGeometry()
    {
        var window = NewExercise(FastDwell(), new WeakReferenceMessenger());
        try
        {
            _ = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });

            var target = window.SkipDwellDetector.Targets.Single();
            var (expected, radius) = EyeCareExerciseWindow.ComputeSkipGeometry(
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight,
                EyeCtrl.Core.Services.DpiHelper.ScaleX,
                EyeCtrl.Core.Services.DpiHelper.ScaleY);

            Check.That(target.Id).IsEqualTo(EyeCareExerciseWindow.SkipTargetId);
            Check.That(target.Center.X).IsCloseTo(expected.X, 1.0);
            Check.That(target.Center.Y).IsCloseTo(expected.Y, 1.0);
            Check.That(target.Radius).IsCloseTo(radius, 1.0);
            Check.That(target.DwellTimeMicroseconds)
                .IsEqualTo(FastDwell().ActivationButtonDwellMs * 1000L);
        }
        finally
        {
            Close(window);
        }
    }

    // ─────────────────────────────────────────────── the exercise Skip's behaviour

    [StaFact]
    public void ADwellOnSkip_EndsTheRun_AndPublishesTheCue()
    {
        var messenger = new WeakReferenceMessenger();
        var cues = new CueProbe(messenger);
        var window = NewExercise(FastDwell(), messenger);
        try
        {
            var run = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            var center = window.SkipDwellDetector.Targets.Single().Center;

            Feed(window, Off, 0, 200);            // leave-before-arm
            Feed(window, center, 230, 500);       // 270 ms on the target

            Check.WithCustomMessage("the Skip dwell did not end the run").That(run.IsCompleted).IsTrue();
            Check.That(run.IsCompletedSuccessfully).IsTrue();
            Check.That(cues.Count).IsEqualTo(1);
            Check.WithCustomMessage("the target outlived the run")
                .That(window.SkipDwellDetector.Targets).IsEmpty();
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// A blink mid-dwell must not restart the Skip hold (A1's grace). The sample silence at the
    /// 150 ms clamp PAUSES the 200 ms dwell instead of resetting it, so the run ends at ~360 ms
    /// of the on-target feed; a grace-0 detector would need a fresh 200 ms from the return and is
    /// still running at 390 ms — the mutation that drops the grace turns this RED.
    /// </summary>
    [StaFact]
    public void ABlinkMidDwell_StillCompletesTheSkip()
    {
        var window = NewExercise(FastDwell(graceMs: 500), new WeakReferenceMessenger());
        try
        {
            var run = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            var center = window.SkipDwellDetector.Targets.Single().Center;

            Feed(window, Off, 0, 200);                    // arm
            Feed(window, center, 230, 380);               // 150 ms accumulated
            Check.That(window.SkipDwellOutline.Outline.StrokeThickness).IsStrictlyGreaterThan(0);

            // Blink: a 150 ms silence (the detector's clamp) while still on the target.
            window.OnGaze(center, 530_000);
            Check.WithCustomMessage("the blink already completed the dwell").That(run.IsCompleted).IsFalse();

            Feed(window, center, 560, 620);               // grace resumes from 150 ms
            Check.WithCustomMessage(
                    "the blink restarted the Skip dwell — the pause grace did not reach the detector")
                .That(run.IsCompleted).IsTrue();
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void GazeAlreadyOnSkip_NeedsALookAwayFirst()
    {
        var window = NewExercise(FastDwell(), new WeakReferenceMessenger());
        try
        {
            var run = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            var center = window.SkipDwellDetector.Targets.Single().Center;

            Feed(window, center, 0, 2000);
            Check.WithCustomMessage("the Skip target armed under a resting gaze")
                .That(window.SkipArmedForTest).IsFalse();
            Check.WithCustomMessage("a resting gaze ended the exercise by itself")
                .That(run.IsCompleted).IsFalse();

            Feed(window, Off, 2030, 2230);        // a real look-away (>= 150 ms outside)
            Check.That(window.SkipArmedForTest).IsTrue();

            Feed(window, center, 2260, 2500);
            Check.That(run.IsCompleted).IsTrue();
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void ACancelledRun_ClearsTheSkipTarget()
    {
        var window = NewExercise(FastDwell(), new WeakReferenceMessenger());
        try
        {
            _ = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            Check.That(window.SkipDwellDetector.Targets).HasSize(1);

            window.Cancel();

            Check.That(window.SkipDwellDetector.Targets).IsEmpty();
            Check.That(window.SkipDwellOutline.Outline.StrokeThickness).IsEqualTo(0.0);
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void ClickingSkip_StillEndsTheRun()
    {
        var window = NewExercise(FastDwell(), new WeakReferenceMessenger());
        try
        {
            var run = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });

            window.SkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Check.That(run.IsCompleted).IsTrue();
        }
        finally
        {
            Close(window);
        }
    }

    // ─────────────────────────────────────────────── the banner's rings

    [StaFact]
    public void TheBanner_RegistersR50Rings_WithGrace()
    {
        var window = NewBanner(FastDwell(graceMs: 350));
        try
        {
            _ = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);

            var targets = window.DwellForTest.Targets
                .OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
            Check.That(targets).HasSize(2);
            foreach (var target in targets)
            {
                Check.That(target.Radius)
                    .IsCloseTo(EyeCareBannerWindow.HitRadiusDip * EyeCtrl.Core.Services.DpiHelper.ScaleX, 0.001);
                Check.That(target.DwellTimeMicroseconds).IsEqualTo(200 * 1000L);
            }

            // A1's grace reached the detector.
            Check.That(window.DwellForTest.PauseGraceMs).IsEqualTo(350);
            Check.WithCustomMessage("the banner's hold survives a neighbour").That(
                window.DwellForTest.OtherTargetReleasesHold).IsTrue();
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void TheBannerRings_ShowProgress_WhileDwelling()
    {
        var window = NewBanner(FastDwell());
        try
        {
            _ = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);
            var start = window.DwellForTest.Targets.Single(t => t.Id == "start");
            var rest = EyeCareBannerWindow.RingStrokeRest;

            for (var t = 0L; t <= 90_000; t += 30_000)
                window.OnGaze(start.Center, t);

            Check.WithCustomMessage("the Start ring showed no dwell progress")
                .That(window.StartRing.StrokeThickness).IsStrictlyGreaterThan(rest);
            Check.That(window.StartRing.StrokeThickness)
                .IsLessOrEqualThan(EyeCareBannerWindow.RingStrokeFull);
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void TheBanner_AcceptsOnStart_AndClearsTheRings()
    {
        var window = NewBanner(FastDwell());
        try
        {
            var shown = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);
            var start = window.DwellForTest.Targets.Single(t => t.Id == "start");

            for (var t = 0L; t <= 240_000; t += 30_000)
                window.OnGaze(start.Center, t);

            Check.That(shown.IsCompleted).IsTrue();
            Check.That(shown.Result).IsTrue();
            Check.That(window.DwellForTest.Targets).IsEmpty();
            Check.That(window.StartRing.StrokeThickness)
                .IsEqualTo(EyeCareBannerWindow.RingStrokeRest);
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// The banner's A1 grace, with the same timing pin as the exercise: the 150 ms blink PAUSES
    /// the 200 ms hold, so Start activates by 390 ms; a grace-0 detector restarts at 300 ms and
    /// needs until 500 — the mutation that fails to pass DwellPauseGraceMs turns this RED.
    /// </summary>
    [StaFact]
    public void TheBannerBlinkMidDwell_StillAccepts()
    {
        var window = NewBanner(FastDwell(graceMs: 500));
        try
        {
            var shown = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);
            var start = window.DwellForTest.Targets.Single(t => t.Id == "start");

            for (var t = 0L; t <= 150_000; t += 30_000)
                window.OnGaze(start.Center, t);
            window.OnGaze(start.Center, 300_000);      // the blink: a 150 ms silence
            Check.That(shown.IsCompleted).IsFalse();

            for (var t = 330_000L; t <= 390_000; t += 30_000)
                window.OnGaze(start.Center, t);

            Check.WithCustomMessage(
                    "the blink restarted the banner's hold — the pause grace did not reach the detector")
                .That(shown.IsCompleted).IsTrue();
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void CancellingTheBanner_ResetsTheRingProgress()
    {
        var window = NewBanner(FastDwell());
        try
        {
            var shown = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);
            var start = window.DwellForTest.Targets.Single(t => t.Id == "start");
            for (var t = 0L; t <= 90_000; t += 30_000)
                window.OnGaze(start.Center, t);
            Check.That(window.StartRing.StrokeThickness)
                .IsStrictlyGreaterThan(EyeCareBannerWindow.RingStrokeRest);

            window.Cancel();

            Check.That(shown.IsCompleted).IsTrue();
            Check.That(shown.Result).IsFalse();
            Check.That(window.DwellForTest.Targets).IsEmpty();
            Check.That(window.StartRing.StrokeThickness)
                .IsEqualTo(EyeCareBannerWindow.RingStrokeRest);
        }
        finally
        {
            Close(window);
        }
    }

    // ─────────────────────────────────────────────── helpers

    private sealed class CueProbe
    {
        public int Count { get; private set; }

        public CueProbe(IMessenger messenger) =>
            messenger.Register<DwellActivationMessage>(this, (_, _) => Count++);
    }

    private static EyeCareExerciseWindow NewExercise(DwellSettings dwell, IMessenger messenger)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return InvisibleTestWindow.Prepare(new EyeCareExerciseWindow(dwell, messenger));
    }

    private static EyeCareBannerWindow NewBanner(DwellSettings dwell)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return InvisibleTestWindow.Prepare(new EyeCareBannerWindow(
            dwell.ActivationButtonDwellMs, messenger: new WeakReferenceMessenger(),
            dwellSettings: dwell));
    }

    private static void Feed(EyeCareExerciseWindow window, PhysicalPoint point, long fromMs, long toMs)
    {
        for (var t = fromMs; t <= toMs; t += 30)
            window.OnGaze(point, t * 1000);
    }

    private static void Close(Window window)
    {
        if (window is EyeCareExerciseWindow exercise) exercise.Cancel();
        if (window is EyeCareBannerWindow banner) banner.Cancel();
        window.Close();
    }
}
