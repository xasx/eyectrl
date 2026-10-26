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
/// B7 (and fixup-01): the eye-care surfaces are gaze-reachable. The exercise's Skip is an r50
/// dwell target on the mid-right edge (mid-left for a right-docked toolbar), clear of every
/// exercise dot path INCLUDING the saccade transits, with A1 grace, a progress outline and
/// leave-before-arm; the banner's rings are r50 discs, 24 DIP apart, clear of the toolbar for
/// every dock, with visible progress and A1's grace.
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

    private static ToolbarSettings Toolbar(string edge = "Top", double size = 120) =>
        new() { IsDocked = true, DockedEdge = edge, Size = size };

    // ─────────────────────────────────────────────── the exercise Skip's geometry

    [Theory]
    [InlineData(1920.0, 1200.0, 1.0, "Top")]
    [InlineData(1920.0, 1200.0, 2.0, "Top")]
    [InlineData(1366.0, 768.0, 1.0, "Top")]
    [InlineData(1366.0, 768.0, 2.0, "Top")]
    [InlineData(1920.0, 1200.0, 2.0, "Right")]
    [InlineData(1366.0, 768.0, 1.0, "Right")]
    public void ComputeSkipGeometry_IsMidRightEdge_OrMidLeftForARightDockedToolbar(
        double screenDipWidth, double screenDipHeight, double scale, string dockedEdge)
    {
        var toolbar = Toolbar(dockedEdge);
        var (center, radius) = EyeCareExerciseWindow.ComputeSkipGeometry(
            screenDipWidth, screenDipHeight, toolbar, scale, scale);

        var atRight = dockedEdge != "Right";
        Check.That(center.X).IsCloseTo(
            (atRight ? screenDipWidth - EyeCarePlacement.SkipCenterInsetDip
                     : EyeCarePlacement.SkipCenterInsetDip) * scale, 0.001);
        Check.That(center.Y).IsCloseTo(screenDipHeight / 2 * scale, 0.001);
        Check.That(radius).IsCloseTo(EyeCareExerciseWindow.SkipHitRadiusDip * scale, 0.001);

        // The R4 size floor, fully on-screen, and mid-edge rather than in a corner.
        Check.That(radius).IsGreaterOrEqualThan(50 * scale);
        Check.That(center.X - radius).IsGreaterOrEqualThan(0);
        Check.That(center.X + radius).IsLessOrEqualThan(screenDipWidth * scale);
        Check.That(center.Y - radius).IsGreaterOrEqualThan(0);
        Check.That(center.Y + radius).IsLessOrEqualThan(screenDipHeight * scale);
        var insetDip = atRight
            ? screenDipWidth - center.X / scale
            : center.X / scale;
        Check.WithCustomMessage("the Skip disc is not at the side edge")
            .That(insetDip).IsLessOrEqualThan(EyeCarePlacement.SkipCenterInsetDip + 0.5);
        Check.That(center.Y / scale).IsCloseTo(screenDipHeight / 2, 0.5);
    }

    /// <summary>
    /// The exit is clear of every exercise's dot path — including where the EYE travels: a
    /// saccade dot jumps, so consecutive samples far apart are joined into their transit segment
    /// and sampled too (fixup-01 F4). No dot or transit point comes within the drawn dot's own
    /// radius of the Skip hit circle. The pool is the production one, over a full 45 s run.
    /// </summary>
    [Theory]
    [InlineData(1920.0, 1200.0, 1.0)]
    [InlineData(1920.0, 1200.0, 2.0)]
    [InlineData(1366.0, 768.0, 1.0)]
    [InlineData(1366.0, 768.0, 2.0)]
    public void ComputeSkipGeometry_IsClearOfEveryExerciseDotPath_IncludingSaccadeTransits(
        double screenDipWidth, double screenDipHeight, double scale)
    {
        var (skipCenter, skipRadius) = EyeCareExerciseWindow.ComputeSkipGeometry(
            screenDipWidth, screenDipHeight, Toolbar(), scale, scale);
        var pool = AppBootstrapper.BuildExercisePool(
            new PhysicalPoint(screenDipWidth / 2 * scale, screenDipHeight / 2 * scale));

        // The dot's drawn diameter is 36 DIP (XAML TargetDot), so its edge must never reach the
        // Skip hit circle. Measured minimum today: 280 DIP at 1366×768 (the saccade's
        // lower-right target) and 441 DIP on the owner's screen; this floor is the dot's radius.
        var dotRadius = 18 * scale;
        var minimum = skipRadius + dotRadius;
        const long durationUs = 45_000_000;
        const long step = 10_000;

        foreach (var exercise in pool)
        {
            PhysicalPoint? previous = null;
            for (var t = 0L; t <= durationUs; t += step)
            {
                var dot = exercise.GetTargetAt(t);
                if (previous is { } before && Distance(before, dot) > 20 * scale)
                {
                    // The dot jumped between two samples: the eye crosses the gap, so the whole
                    // transit segment is part of the path (F4 — a saccade's BL→BR sweep).
                    for (var f = 0.0; f <= 1.0; f += 0.01)
                        AssertClear(exercise, $"transit at t={t / 1000.0:0.#} ms", Lerp(before, dot, f));
                }

                AssertClear(exercise, $"t={t / 1000.0:0.#} ms", dot);
                previous = dot;
            }
        }

        void AssertClear(IExercise exercise, string when, PhysicalPoint point)
        {
            var distance = Distance(point, skipCenter);
            Check.WithCustomMessage(
                    $"{exercise.Name} passes at ({point.X:0.#}, {point.Y:0.#}) ({when}) — "
                    + $"{distance:0.#} px from the Skip centre, inside the {minimum:0.#} px "
                    + "clearance the drawn dot needs.")
                .That(distance).IsGreaterOrEqualThan(minimum);
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
        var toolbar = Toolbar();
        var dwell = FastDwell();
        var window = NewExercise(dwell, new WeakReferenceMessenger(), toolbar);
        try
        {
            _ = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });

            var target = window.SkipDwellDetector.Targets.Single();
            var (expected, radius) = EyeCareExerciseWindow.ComputeSkipGeometry(
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight,
                toolbar,
                EyeCtrl.Core.Services.DpiHelper.ScaleX,
                EyeCtrl.Core.Services.DpiHelper.ScaleY);

            Check.That(target.Id).IsEqualTo(EyeCareExerciseWindow.SkipTargetId);
            Check.That(target.Center.X).IsCloseTo(expected.X, 1.0);
            Check.That(target.Center.Y).IsCloseTo(expected.Y, 1.0);
            Check.That(target.Radius).IsCloseTo(radius, 1.0);
            Check.That(target.DwellTimeMicroseconds).IsEqualTo(200 * 1000L);
            Check.That(window.ToolbarSettingsForTest).IsSameReferenceAs(toolbar);
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
            Check.WithCustomMessage("the Skip disc stayed drawn after the dwell finished (F5)")
                .That(window.SkipTarget.Visibility).IsEqualTo(Visibility.Collapsed);
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
    public void ACancelledRun_ClearsTheSkipTarget_AndHidesTheDisc()
    {
        var window = NewExercise(FastDwell(), new WeakReferenceMessenger());
        try
        {
            _ = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            Check.That(window.SkipDwellDetector.Targets).HasSize(1);
            Check.WithCustomMessage("the Skip disc must be drawn during a run")
                .That(window.SkipTarget.Visibility).IsEqualTo(Visibility.Visible);

            window.Cancel();

            Check.That(window.SkipDwellDetector.Targets).IsEmpty();
            Check.That(window.SkipDwellOutline.Outline.StrokeThickness).IsEqualTo(0.0);
            // F5: no dead disc during the 3 s summary — a gaze user must not rest on an exit
            // that no longer responds.
            Check.WithCustomMessage("the Skip disc outlived the cancelled run")
                .That(window.SkipTarget.Visibility).IsEqualTo(Visibility.Collapsed);

            // The next run brings it back.
            _ = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            Check.That(window.SkipTarget.Visibility).IsEqualTo(Visibility.Visible);
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void ClickingSkip_StillEndsTheRun_AndStaysSilent()
    {
        // M9: the dwell threshold with the cue's `if (true)` mutant must die here — a click ends
        // the run but publishes no DwellActivationMessage (spec D8: the cue is the dwell's).
        var messenger = new WeakReferenceMessenger();
        var cues = new CueProbe(messenger);
        var window = NewExercise(FastDwell(), messenger);
        try
        {
            var run = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });

            window.SkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Check.That(run.IsCompleted).IsTrue();
            Check.WithCustomMessage("a click published the dwell's cue")
                .That(cues.Count).IsEqualTo(0);
        }
        finally
        {
            Close(window);
        }
    }

    // ─────────────────────────────────────────────── leave-before-arm blips (F7 M2/M3)

    /// <summary>
    /// M2: a single 30 ms sample outside is a jitter, not a look-away. The 150 ms window must not
    /// collapse to "any outside sample" — the mutation <c>SkipLeaveBeforeArmMs 150 → 0</c> arms
    /// here and this goes red.
    /// </summary>
    [StaFact]
    public void ASingleOutsideBlip_DoesNotArmSkip()
    {
        var window = NewExercise(FastDwell(), new WeakReferenceMessenger());
        try
        {
            var run = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            var center = window.SkipDwellDetector.Targets.Single().Center;

            Feed(window, center, 0, 600);          // still resting on the newly shown disc
            window.OnGaze(Off, 630_000);           // ONE 30 ms blip outside
            Feed(window, center, 660, 1000);       // back on the disc

            Check.WithCustomMessage("one jitter sample outside armed the Skip exit")
                .That(window.SkipArmedForTest).IsFalse();
            Check.WithCustomMessage("the blip started a dwell").That(run.IsCompleted).IsFalse();
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// M3: short outside blips must not add up. Each return resets the 150 ms window, so a series
    /// of 30 ms excursions (150 ms in total) never arms — the mutation that drops the reset in
    /// <c>UpdateSkipArm</c> counts them cumulatively and goes red.
    /// </summary>
    [StaFact]
    public void RepeatedOutsideBlips_DoNotAddUpToAnArm()
    {
        var window = NewExercise(FastDwell(), new WeakReferenceMessenger());
        try
        {
            var run = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });
            var center = window.SkipDwellDetector.Targets.Single().Center;

            Feed(window, center, 0, 600);
            var t = 630L;
            for (var blip = 0; blip < 6; blip++)
            {
                window.OnGaze(Off, t * 1000);
                window.OnGaze(Off, (t + 30) * 1000);
                t += 60;
                window.OnGaze(center, t * 1000);
                t += 30;
            }

            Check.WithCustomMessage("short outside blips added up to an arm")
                .That(window.SkipArmedForTest).IsFalse();
            Check.That(run.IsCompleted).IsFalse();
        }
        finally
        {
            Close(window);
        }
    }

    // ─────────────────────────────────────────────── live settings at session entry (F7 M4/M5/M6/M9, F6)

    [StaFact]
    public void ALiveGraceEdit_ReachesTheSkipDetectorAtRunEntry()
    {
        // M5: the window is a startup singleton, so BeginSkipTarget must re-read the live grace.
        var dwell = FastDwell(graceMs: 200);
        var window = NewExercise(dwell, new WeakReferenceMessenger());
        try
        {
            dwell.DwellPauseGraceMs = 700;
            _ = window.RunAsync("Test", 60_000_000, _ => Off, (_, _) => { });

            Check.WithCustomMessage("a live DwellPauseGraceMs edit did not reach Skip")
                .That(window.SkipDwellDetector.PauseGraceMs).IsEqualTo(700);
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void ALiveGraceEdit_ReachesTheBannerDetectorAtShow()
    {
        // M4: same contract for the banner's rings.
        var dwell = FastDwell(graceMs: 200);
        var window = NewBanner(dwell);
        try
        {
            dwell.DwellPauseGraceMs = 800;
            _ = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);

            Check.WithCustomMessage("a live DwellPauseGraceMs edit did not reach the banner")
                .That(window.DwellForTest.PauseGraceMs).IsEqualTo(800);
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void ALiveToolbarSizeEdit_MovesTheBannerRingsAtShow()
    {
        // M6: the rings are placed from the live ToolbarSettings at every show, not at startup.
        var toolbar = Toolbar(size: 120);
        var window = NewBanner(FastDwell(), toolbar);
        try
        {
            _ = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);
            var before = window.DwellForTest.Targets.Single(t => t.Id == "start").Center.Y;

            window.Cancel();
            toolbar.Size = 200;
            _ = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);
            var after = window.DwellForTest.Targets.Single(t => t.Id == "start").Center.Y;

            Check.WithCustomMessage("a live toolbar-size edit did not move the rings down")
                .That(after).IsStrictlyGreaterThan(before);
            Check.That(after / EyeCtrl.Core.Services.DpiHelper.ScaleY).IsCloseTo(
                EyeCareBannerWindow.RingsTopDip(toolbar) + EyeCareBannerWindow.HitRadiusDip, 1.0);
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void TheBannerRingDwellTime_FollowsTheLiveSetting()
    {
        // F6: the rings must register ActivationButtonDwellMs at ShowAsync, not the ctor's value.
        var dwell = FastDwell();
        var window = NewBanner(dwell);
        try
        {
            _ = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);
            Check.That(window.DwellForTest.Targets.Single(t => t.Id == "start").DwellTimeMicroseconds)
                .IsEqualTo(200 * 1000L);

            window.Cancel();
            dwell.ActivationButtonDwellMs = 350;
            _ = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);

            Check.WithCustomMessage("a live ActivationButtonDwellMs edit did not reach the rings")
                .That(window.DwellForTest.Targets.Single(t => t.Id == "start").DwellTimeMicroseconds)
                .IsEqualTo(350 * 1000L);
        }
        finally
        {
            Close(window);
        }
    }

    [StaFact]
    public void TheWindow_OnARightDockedToolbar_DrawsTheRingsOnTheirLeftAnchoredTargets()
    {
        // F2's drawn half: the geometry may move the panel left, but the PANEL must follow too, or
        // the circle the user aims at is not the circle gaze is tested against.
        var toolbar = Toolbar("Right");
        var window = NewBanner(FastDwell(), toolbar);
        try
        {
            _ = window.ShowAsync("msg", new PhysicalPoint(960, 600), autoSnoozeSeconds: 60);
            window.UpdateLayout();

            var root = (FrameworkElement)window.Content;
            var start = window.DwellForTest.Targets.Single(t => t.Id == "start");
            var snooze = window.DwellForTest.Targets.Single(t => t.Id == "snooze");
            Check.WithCustomMessage("a right-docked toolbar must move the rings to the left")
                .That(window.StartRing.TransformToAncestor(root)
                    .TransformBounds(new Rect(window.StartRing.RenderSize)).X)
                .IsStrictlyLessThan(window.ActualWidth / 2);

            foreach (var (name, ring, target) in new[]
                     {
                         ("start", (FrameworkElement)window.StartRing, start.Center),
                         ("snooze", (FrameworkElement)window.SnoozeRing, snooze.Center),
                     })
            {
                var box = ring.TransformToAncestor(root)
                    .TransformBounds(new Rect(ring.RenderSize));
                var centreX = EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalX(box.X + box.Width / 2);
                var centreY = EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalY(box.Y + box.Height / 2);
                Check.WithCustomMessage($"{name}: the drawn ring is not on its dwell target")
                    .That(centreX).IsCloseTo(target.X, 1.0);
                Check.That(centreY).IsCloseTo(target.Y, 1.0);
            }
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

    private static EyeCareExerciseWindow NewExercise(
        DwellSettings dwell, IMessenger messenger, ToolbarSettings? toolbar = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return InvisibleTestWindow.Prepare(new EyeCareExerciseWindow(
            dwell, messenger, toolbarSettings: toolbar));
    }

    private static EyeCareBannerWindow NewBanner(DwellSettings dwell, ToolbarSettings? toolbar = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return InvisibleTestWindow.Prepare(new EyeCareBannerWindow(
            dwell.ActivationButtonDwellMs, messenger: new WeakReferenceMessenger(),
            dwellSettings: dwell, toolbarSettings: toolbar));
    }

    private static double Distance(PhysicalPoint a, PhysicalPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static PhysicalPoint Lerp(PhysicalPoint a, PhysicalPoint b, double f) =>
        new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);

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
