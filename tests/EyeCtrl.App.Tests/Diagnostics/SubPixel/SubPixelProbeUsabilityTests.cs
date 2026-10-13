namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// SP-4c — the probe-usability decisions, headless. The window gets an HWND from
/// <c>EnsureHandle</c> and is NEVER shown.
///
/// <para>
/// D1: every button's <see cref="DwellOutlineOverlay"/> outline follows its own target's dwell
/// progress (all seven, not only answer 1). D2/D3 (amended by fixup 01): the button sizes and the
/// grown-RECTANGLE hit areas, no live pair overlapping, at the owner's 2.0/3840×2400 and at
/// 1.0/1920×1080. Fixup 01 F1: no text block and no demo band sits inside a live hit area, and one
/// slow read of every text block fires nothing — review 01's reading probe, ported. D5: the Intro's
/// motion demo (the trial's drift law, both conditions alternating per loop, hidden elsewhere) and
/// the Intro layout. D5's timer pin.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class SubPixelProbeUsabilityTests
{
    private static readonly (double Scale, double WidthDip, double HeightDip)[] OwnerScales =
    [
        (2.0, 1920, 1200),   // 3840 × 2400 device px
        (1.0, 1920, 1080),
    ];

    private static readonly string[] AllTargetIds =
    [
        SubPixelProbeViewModel.AnswerOneTargetId,
        SubPixelProbeViewModel.AnswerTwoTargetId,
        SubPixelProbeViewModel.EndTestTargetId,
        SubPixelProbeViewModel.IntroStartTargetId,
        SubPixelProbeViewModel.AskYesTargetId,
        SubPixelProbeViewModel.AskLaterTargetId,
        SubPixelProbeViewModel.ResultDoneTargetId,
    ];

    /// <summary>The screen each target is honoured on (End test is on every screen).</summary>
    private static SubPixelProbePhase PhaseFor(string id) => id switch
    {
        SubPixelProbeViewModel.AnswerOneTargetId or SubPixelProbeViewModel.AnswerTwoTargetId
            => SubPixelProbePhase.Answer,
        SubPixelProbeViewModel.IntroStartTargetId => SubPixelProbePhase.Intro,
        SubPixelProbeViewModel.AskYesTargetId or SubPixelProbeViewModel.AskLaterTargetId
            => SubPixelProbePhase.Ask,
        SubPixelProbeViewModel.ResultDoneTargetId => SubPixelProbePhase.Result,
        _ => SubPixelProbePhase.Intro,
    };

    /// <summary>
    /// D2's sizes and D3's (amended) hit-area margins, per target — literals, so a mutant that
    /// changes the production constant (or the margin a button gets) cannot move the test's
    /// expectation with it.
    /// </summary>
    private static (double Width, double Height, double Margin) SpecFor(string id) => id switch
    {
        SubPixelProbeViewModel.AnswerOneTargetId or SubPixelProbeViewModel.AnswerTwoTargetId
            => (380, 260, 48),
        SubPixelProbeViewModel.EndTestTargetId
            => (280, 120, 16),
        _ => (420, 170, 48),
    };

    /// <summary>The live targets of every screen, as the D3 no-overlap rule and F1 need them.</summary>
    private static readonly (SubPixelProbePhase Phase, string[] Ids)[] LiveTargets =
    [
        (SubPixelProbePhase.Intro, [SubPixelProbeViewModel.IntroStartTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Answer, [SubPixelProbeViewModel.AnswerOneTargetId, SubPixelProbeViewModel.AnswerTwoTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Ask, [SubPixelProbeViewModel.AskYesTargetId, SubPixelProbeViewModel.AskLaterTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Result, [SubPixelProbeViewModel.ResultDoneTargetId, SubPixelProbeViewModel.EndTestTargetId]),
    ];

    // ------------------------------------------------------------------ D1: the cue on the button

    [StaFact]
    public void D1_TheDwellCue_ShowsEachTargetsProgressOnItsOwnButton()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = 700;

        foreach (var id in AllTargetIds)
        {
            using var vm = NewViewModel(settings.Dwell);
            vm.Start();
            var window = NewWindow(vm, scale: 1.0, widthDip: 1920, heightDip: 1080);
            try
            {
                SetPhase(vm, SubPixelProbePhase.Idle);
                SetPhase(vm, PhaseFor(id));

                var centreDip = CentreInScene(window, id);
                var at = new PhysicalPoint(centreDip.X, centreDip.Y);

                window.OnFrame();
                foreach (var other in AllTargetIds)
                {
                    Check.WithCustomMessage($"{other}: a target with no dwell must show no outline")
                        .That(window.CueForTest(other)!.Outline.StrokeThickness).IsEqualTo(0.0);
                }

                // A realistic 10 ms cadence: a single 350 ms hole would itself be a dropout.
                Feed(vm, at, 0);
                for (long t = 10_000; t <= 350_000; t += 10_000) Feed(vm, at, t);
                window.OnFrame();

                Check.WithCustomMessage($"{id}: progress 0.5 must draw half the outline on its own button")
                    .That(window.CueForTest(id)!.Outline.StrokeThickness)
                    .IsCloseTo(DwellOutlineOverlay.MaxThickness * 0.5, 1e-9);

                // The other six stay empty: each cue reads its own target, not a neighbour's.
                foreach (var other in AllTargetIds)
                {
                    if (other == id) continue;
                    Check.WithCustomMessage($"O4: {other} showed {id}'s progress")
                        .That(window.CueForTest(other)!.Outline.StrokeThickness).IsEqualTo(0.0);
                }

                // Fixup 02 N2b: an outside landing within the grace keeps the FROZEN progress on
                // this button's outline (the owner must not read "lost it"), and it clears once the
                // grace expires. This kills GetProgress -> 0 at the App level, through C6.
                var (_, _, margin) = SpecFor(id);
                var outside = new PhysicalPoint(at.X + (SpecFor(id).Width / 2 + margin + 80), at.Y);
                Feed(vm, outside, 360_000);
                window.OnFrame();
                Check.WithCustomMessage($"N2b: {id}'s outline emptied during an outside pause")
                    .That(window.CueForTest(id)!.Outline.StrokeThickness)
                    .IsCloseTo(DwellOutlineOverlay.MaxThickness * 0.5, 1e-9);

                Feed(vm, outside, 710_000);   // 360 ms after the last inside sample: past the grace
                window.OnFrame();
                Check.WithCustomMessage($"N2b: {id}'s outline must clear after the grace expires")
                    .That(window.CueForTest(id)!.Outline.StrokeThickness).IsEqualTo(0.0);
            }
            finally
            {
                window.Close();
            }
        }
    }

    // Mutation (D1-binding): UpdateDwellCues hardcodes one button's progress (or 0) -> the
    //   per-button checks go red.
    // Mutation (O4): End test's cue reads Start's progress -> the loop's "other six" check for
    //   answer_1/Start goes red on End test's iteration.
    // Mutation (D1-overlay): the cue is not attached (BuildButton drops Attach) -> CueForTest is
    //   null and every thickness write is a no-op: red.

    // ------------------------------------------------ D2/D3: sizes, hit areas, no overlap

    [StaFact]
    public void D2D3_ButtonsHitAreasAndNoOverlap_MatchTheDecisionsAtBothOwnerScales()
    {
        foreach (var (scale, widthDip, heightDip) in OwnerScales)
            AssertGeometry(scale, widthDip, heightDip);
    }

    private static void AssertGeometry(double scale, double widthDip, double heightDip)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();
        var window = NewWindow(vm, scale, widthDip, heightDip);
        try
        {
            var detector = (DwellDetector)typeof(SubPixelProbeViewModel)
                .GetField("_targets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;

            foreach (var (phase, ids) in LiveTargets)
            {
                SetPhase(vm, SubPixelProbePhase.Idle);
                SetPhase(vm, phase);

                foreach (var id in ids)
                {
                    var element = window.TargetElementForTest(id)!;
                    var (expectedWidth, expectedHeight, margin) = SpecFor(id);

                    Check.WithCustomMessage($"{id}@{scale}: the drawn size is not the D2 size")
                        .That(element.Width).IsEqualTo(expectedWidth);
                    Check.WithCustomMessage($"{id}@{scale}: the drawn size is not the D2 size")
                        .That(element.Height).IsEqualTo(expectedHeight);

                    // The D3 (amended) hit area: the button grown by its margin, nothing more.
                    var centre = CentreInScene(window, id);
                    var centrePhys = new PhysicalPoint(centre.X * scale, centre.Y * scale);
                    double reachX = (expectedWidth / 2.0 + margin - 1) * scale;
                    double outsideX = (expectedWidth / 2.0 + margin + 1) * scale;
                    double reachY = (expectedHeight / 2.0 + margin - 1) * scale;
                    double outsideY = (expectedHeight / 2.0 + margin + 1) * scale;

                    Check.WithCustomMessage(
                            $"{id}@{scale}: {reachX:F0} px sideways must be inside the grown rectangle")
                        .That(detector.TargetHit(id, new PhysicalPoint(centrePhys.X + reachX, centrePhys.Y)))
                        .IsTrue();
                    Check.WithCustomMessage(
                            $"{id}@{scale}: {outsideX:F0} px sideways must be outside the grown rectangle "
                            + "(a bigger reach would cover the neighbour/edge)")
                        .That(detector.TargetHit(id, new PhysicalPoint(centrePhys.X + outsideX, centrePhys.Y)))
                        .IsFalse();
                    Check.WithCustomMessage(
                            $"{id}@{scale}: {reachY:F0} px vertically must be inside the grown rectangle")
                        .That(detector.TargetHit(id, new PhysicalPoint(centrePhys.X, centrePhys.Y + reachY)))
                        .IsTrue();
                    Check.WithCustomMessage(
                            $"{id}@{scale}: {outsideY:F0} px vertically must be outside the grown rectangle "
                            + "(fixup 01 F1: a circle here reached the text)")
                        .That(detector.TargetHit(id, new PhysicalPoint(centrePhys.X, centrePhys.Y + outsideY)))
                        .IsFalse();
                }

                for (int i = 0; i < ids.Length; i++)
                {
                    for (int j = i + 1; j < ids.Length; j++)
                    {
                        var a = HitRectInScene(window, ids[i]);
                        var b = HitRectInScene(window, ids[j]);
                        var overlap = Rect.Intersect(a, b);

                        Check.WithCustomMessage(
                                $"D3: {ids[i]} and {ids[j]} hit areas overlap in {phase} at scale {scale}: "
                                + $"{a} ∩ {b} = {overlap}")
                            .That(overlap.IsEmpty).IsTrue();
                    }
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    // Mutation (D2): any button size constant reverted (e.g. AnswerWidth 380 -> 220) -> the size
    //   pin goes red.
    // Mutation (D3-margin): the answers' 48 becomes 0, or End test's 16 becomes 48 -> the
    //   inside/outside probes go red.
    // Mutation (D3-overlap): the Ask screen goes back to stacking -> the Yes/Later overlap check.
    // Mutation (F1-reg): the rects go back to circles (Register with a radius) -> the vertical
    //   outside probe at width/2 + margin + 1 is inside a circle: red.

    // --------------------------------------------------------------- D2: every screen fits

    [StaFact]
    public void D2_EveryScreenFitsInsideTheWindowAtBothOwnerScales()
    {
        foreach (var (scale, widthDip, heightDip) in OwnerScales)
            AssertScreensFit(scale, widthDip, heightDip);
    }

    private static void AssertScreensFit(double scale, double widthDip, double heightDip)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();
        var window = NewWindow(vm, scale, widthDip, heightDip);
        try
        {
            foreach (var (phase, ids) in ScreensWithLiveTargets)
            {
                SetPhase(vm, SubPixelProbePhase.Idle);
                SetPhase(vm, phase);

                if (window.PanelForTest(phase) is { } panel)
                {
                    var rect = RectInScene(window, panel);
                    Check.WithCustomMessage($"{phase} panel at {scale} is not inside the window: {rect}")
                        .That(rect.Left >= 0 && rect.Top >= 0
                            && rect.Right <= widthDip + 0.5 && rect.Bottom <= heightDip + 0.5).IsTrue();
                }

                foreach (var targetId in ids)
                {
                    var element = window.TargetElementForTest(targetId)!;
                    var rect = RectInScene(window, element);
                    Check.WithCustomMessage($"{targetId} at {scale} is not inside the window: {rect}")
                        .That(rect.Left >= 0 && rect.Top >= 0
                            && rect.Right <= widthDip + 0.5 && rect.Bottom <= heightDip + 0.5).IsTrue();
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    // Mutation (D2-fit): the Intro text block moved back below the band (panel top past the
    //   window's bottom) -> at 1.0 the panel check goes red.

    // ------------------------------------------- Fixup 01 F1: no text inside a live hit area

    [StaFact]
    public void F1_NoVisibleTextBlock_SitsInsideALiveHitArea_OnEveryScreenAtBothScales()
    {
        foreach (var (scale, widthDip, heightDip) in OwnerScales)
            AssertNoTextInsideHitAreas(scale, widthDip, heightDip);
    }

    private static void AssertNoTextInsideHitAreas(double scale, double widthDip, double heightDip)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();
        var window = NewWindow(vm, scale, widthDip, heightDip);
        try
        {
            foreach (var (phase, ids) in ScreensWithLiveTargets)
            {
                SetPhase(vm, SubPixelProbePhase.Idle);
                SetPhase(vm, phase);

                var hits = ids.Select(id => HitRectInScene(window, id)).ToList();
                int checkedPairs = 0;
                foreach (var text in VisibleTextBlocks(window, out var root))
                {
                    var rect = RectInScene(text, root);
                    if (rect.Width <= 0 || rect.Height <= 0) continue;

                    foreach (var hit in hits)
                    {
                        checkedPairs++;
                        double clearance = RectDistance(rect, hit);
                        Check.WithCustomMessage(
                                $"{phase}@{scale}: '{Snippet(text.Text)}' ({rect}) is only {clearance:F1} DIP "
                                + $"from a live hit area ({hit}); the ticket requires at least 24")
                            .That(clearance).IsGreaterOrEqualThan(24);
                    }
                }

                Check.WithCustomMessage($"{phase}@{scale}: no text-to-hit-area pair was checked")
                    .That(checkedPairs).IsStrictlyGreaterThan(0);
            }
        }
        finally
        {
            window.Close();
        }
    }

    // Mutation (F1-text): TextToButtonGapDip 96 -> 24 -> the Ask title (and the result text) sit
    //   inside the grown hit area: clearance is negative -> red.
    // Mutation (F1-intro): the Intro text block is placed below the band instead of above -> the
    //   Start hit area covers it -> red.

    private static string Snippet(string text) =>
        text.Length <= 30 ? text : text[..30] + "…";

    // ------------------------------------------------------ Fixup 01 F1 / fixup 02 R1: the demo band

    /// <summary>
    /// Fixup 01 F1: the Intro text sits above the demo band and Start below it, the band clear of
    /// Start's hit area. Fixup 02 R1: at the owner's ring (250) that clearance is at least 80 DIP —
    /// above their measured 75–130 px gaze bias — and for rings 10, 250 and 500 Start, its whole
    /// grown hit area and End test all stay on-screen (the clamp), with the text still 24 DIP above
    /// the band.
    /// </summary>
    [StaFact]
    public void D5_OnTheIntro_TheDemoBandClearsStartsHitArea_AndTheTextSitsAboveIt()
    {
        foreach (var (scale, widthDip, heightDip) in OwnerScales)
        {
            foreach (var ring in new[] { 10.0, 250.0, 500.0 })
                AssertIntroClearance(scale, widthDip, heightDip, ring);
        }
    }

    private static void AssertIntroClearance(double scale, double widthDip, double heightDip, double ring)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();
        var window = NewWindow(vm, scale, widthDip, heightDip,
            cursor: new CursorSettings { CircleSize = ring });
        try
        {
            SetPhase(vm, SubPixelProbePhase.Idle);
            SetPhase(vm, SubPixelProbePhase.Intro);

            var startRect = HitRectInScene(window, SubPixelProbeViewModel.IntroStartTargetId);
            var endRect = HitRectInScene(window, SubPixelProbeViewModel.EndTestTargetId);
            double bandTop = heightDip / 2 - window.RingForTest.SizeDip / 2;
            double bandBottom = heightDip / 2 + window.RingForTest.SizeDip / 2;
            double clearance = startRect.Top - bandBottom;

            if (ring == 250)
            {
                Check.WithCustomMessage(
                        $"ring 250 @{scale}: the demo band's bottom {bandBottom:F0} vs Start's hit-area "
                        + $"top {startRect.Top:F0} — only {clearance:F1} DIP of clearance (R1 asks for 80)")
                    .That(clearance).IsGreaterOrEqualThan(80);
            }
            else
            {
                Check.WithCustomMessage(
                        $"ring {ring} @{scale}: the band ({bandBottom:F0}) must not overlap Start's hit "
                        + $"area (top {startRect.Top:F0}); the clamp trades clearance for on-screen")
                    .That(clearance).IsGreaterOrEqualThan(0);
            }

            Check.WithCustomMessage($"ring {ring} @{scale}: Start's hit area {startRect} is off-screen")
                .That(startRect.Left >= 0 && startRect.Top >= 0
                    && startRect.Right <= widthDip && startRect.Bottom <= heightDip).IsTrue();
            Check.WithCustomMessage($"ring {ring} @{scale}: End test's hit area {endRect} is off-screen")
                .That(endRect.Left >= 0 && endRect.Top >= 0
                    && endRect.Right <= widthDip && endRect.Bottom <= heightDip).IsTrue();

            var textRect = RectInScene(window, window.PanelForTest(SubPixelProbePhase.Intro)!);
            Check.WithCustomMessage($"ring {ring} @{scale}: the Intro text {textRect} is off-screen")
                .That(textRect.Left >= 0 && textRect.Top >= 0
                    && textRect.Right <= widthDip && textRect.Bottom <= heightDip).IsTrue();
            Check.WithCustomMessage(
                    $"ring {ring} @{scale}: the text ({textRect.Bottom:F0}) must sit 24 DIP above the "
                    + $"band (top {bandTop:F0})")
                .That(textRect.Bottom).IsLessOrEqualThan(bandTop - 24 + 1e-9);
        }
        finally
        {
            window.Close();
        }
    }

    // Mutation (R1): IntroStartGapDip 128 -> 80 -> the clearance is 32 < 80 at the owner's ring: red.
    // Mutation (R1-clamp): the Math.Min clamp removed -> at ring 500 on 1080 DIP Start's hit area
    //   ends past the window's bottom: the on-screen check goes red.

    // ------------------------------------------- Fixup 01 F1: review 01's reading probe, ported

    [StaFact]
    public void F1_ReadingEveryTextBlockOnce_FiresNothing_OnEveryScreenAtBothScales()
    {
        foreach (var (scale, widthDip, heightDip) in OwnerScales)
            AssertReadingFiresNothing(scale, widthDip, heightDip);
    }

    private static void AssertReadingFiresNothing(double scale, double widthDip, double heightDip)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = 700;

        foreach (var screen in new[]
                 {
                     SubPixelProbePhase.Intro,
                     SubPixelProbePhase.Ask,
                     SubPixelProbePhase.Result,
                     SubPixelProbePhase.Answer,
                     SubPixelProbePhase.VoidHold,
                     SubPixelProbePhase.Interval1,
                 })
        {
            ManualTimeProvider? time = screen == SubPixelProbePhase.VoidHold ? new ManualTimeProvider() : null;
            using var vm = NewViewModel(settings.Dwell, time);
            vm.Start();
            var window = NewWindow(vm, scale, widthDip, heightDip);
            try
            {
                var runs = 0;
                vm.RunEnded += _ => runs++;
                long ts = 1_000_000;

                if (screen == SubPixelProbePhase.VoidHold)
                {
                    // VoidHold is driven for real on the virtual clock: any VALID sample continues
                    // the block by design (Q2), so a reflected phase would crash C5 — and the thing
                    // to prove here is that reading the "Rest your eyes" text does not fire a target
                    // (End test is the only live one) and so does not end the run.
                    vm.BeginTrials();
                    while (vm.Phase != SubPixelProbePhase.Interval1 || vm.IsDemo)
                    {
                        time!.Advance(30);
                        ts += 30_000;
                        Feed(vm, new PhysicalPoint(50, 50), ts);
                        Check.That(ts).IsStrictlyLessThan(60_000_000);
                    }

                    vm.ReportRenderGap(100);
                    Check.That(vm.Phase).IsEqualTo(SubPixelProbePhase.VoidHold);
                }
                else
                {
                    SetPhase(vm, SubPixelProbePhase.Idle);
                    SetPhase(vm, screen);
                }

                int samples = 0;
                foreach (var text in VisibleTextBlocks(window, out var root))
                {
                    var rect = RectInScene(text, root);
                    // A slow left-to-right read of every line of the block: ~360 DIP/s, 30 ms
                    // samples (review 01's Probe_ReadingTheTextActivatesTheButton, every line).
                    for (double y = rect.Top + rect.Height * 0.25;
                         y <= rect.Bottom - 1;
                         y += Math.Max(6, rect.Height / 6))
                    {
                        for (double x = rect.Left; x <= rect.Right; x += 11)
                        {
                            Feed(vm, new PhysicalPoint(x * scale, y * scale), ts);
                            ts += 30_000;
                            samples++;
                        }
                    }
                }

                Check.WithCustomMessage($"{screen}@{scale}: no text was found to read")
                    .That(samples).IsStrictlyGreaterThan(50);

                if (screen != SubPixelProbePhase.VoidHold)
                {
                    Check.WithCustomMessage(
                            $"{screen}@{scale}: one read of the screen's text ({samples} samples) must not "
                            + $"fire anything; phase is {vm.Phase}")
                        .That(vm.Phase).IsEqualTo(screen);
                }

                Check.WithCustomMessage($"{screen}@{scale}: the read ended the run")
                    .That(runs).IsEqualTo(0);
            }
            finally
            {
                window.Close();
            }
        }
    }

    // Mutation (F1-read): the Ask row's TextToButtonGapDip 96 -> 0 -> reading the title fires Yes
    //   at 2.0 and 1.0: the phase check goes red.
    // Measured: moving the question onto the answers' row (0.22 -> 0.52 of the height) does NOT
    // turn this probe red — the question block spans the full width and its glyphs sit between the
    // two hit areas — but F1_NoVisibleTextBlock's clearance pin catches that mutant. The two pins
    // are deliberately complementary: geometry there, behaviour here.

    // ---------------------------------------------------------------- D5: the Intro motion demo

    [StaFact]
    public void D5_TheIntroDemo_DriftsLikeATrial_AlternatesBothConditions_AndIsHiddenElsewhere()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();

        long ticks = 0;
        var window = NewWindow(vm, scale: 1.0, widthDip: 1920, heightDip: 1080, frameTicks: () => ticks);
        try
        {
            var ring = window.RingForTest;
            // A1's trial timing as LITERALS (5000 ms intervals, 800 ms gap): the demo must follow
            // the trial's timing, so a demo that kept the old 3000/800 pair fails here.
            const double travel = SubPixelAbDriftMotion.StepPixelsPerTick
                * (5000 / (double)SubPixelAbDriftMotion.TickMilliseconds);
            double startX = 1920 / 2.0 - travel / 2.0;
            double centreY = 1080 / 2.0;

            SetPhase(vm, SubPixelProbePhase.Idle);
            SetPhase(vm, SubPixelProbePhase.Intro);

            window.OnFrame();
            Check.WithCustomMessage("The Intro must draw the demo ring").That(window.IsRingDrawnForTest).IsTrue();
            AssertRingAt(ring, new PhysicalPoint(startX, centreY), snap: true);

            // 1.8 s into the first interval: 60 ticks × 0.25 px East — exactly a trial's drift law.
            ticks = 1800;
            window.OnFrame();
            AssertRingAt(ring, new PhysicalPoint(startX + 15, centreY), snap: true);

            // The pair's 0.8 s gap (5000..5800) hides the ring, exactly as a trial's gap does.
            ticks = 5000 + 400;
            window.OnFrame();
            Check.WithCustomMessage("The demo ring must hide during the gap")
                .That(window.IsRingDrawnForTest).IsFalse();

            // The pair's second interval runs the other condition — the same path, no snapping.
            ticks = 5000 + 800 + 1800;
            window.OnFrame();
            Check.That(window.IsRingDrawnForTest).IsTrue();
            AssertRingAt(ring, new PhysicalPoint(startX + 15, centreY), snap: false);

            // Fixup 01 F5: the NEXT loop alternates the order — B first, the snapshot off.
            // One full loop is 2 × (5000 + 800) = 11600 ms.
            ticks = 11600 + 1800;
            window.OnFrame();
            AssertRingAt(ring, new PhysicalPoint(startX + 15, centreY), snap: false);
            ticks = 11600 + 5800 + 1800;
            window.OnFrame();
            AssertRingAt(ring, new PhysicalPoint(startX + 15, centreY), snap: true);

            // The demo is the Intro's alone: on another screen the ring is not drawn by it.
            SetPhase(vm, SubPixelProbePhase.Answer);
            ticks += 1000;
            window.OnFrame();
            Check.WithCustomMessage("The demo must not draw outside the Intro")
                .That(window.IsRingDrawnForTest).IsFalse();
        }
        finally
        {
            window.Close();
        }
    }

    // Mutation (D5-snap): both demo intervals pass the same snap flag -> a condition assertion
    //   goes red (the two expectations differ at this centre: 837.5 snapped to 838).
    // Mutation (F5): the per-loop alternation removed (cycle parity ignored) -> the second loop's
    //   B-first assertion goes red.
    // Mutation (D5-law): the demo position is computed with a wrong step (e.g. 0.5 px/tick) -> the
    //   1.8 s position assertions go red.
    // Mutation (D5-gap): the gap keeps drawing -> the hidden assertion goes red.
    // Mutation (D5-scope): the demo draws on every phase -> the Answer assertion goes red.

    private static void AssertRingAt(ProbeRingVisual ring, PhysicalPoint centre, bool snap)
    {
        var expected = SubPixelPlacement.TopLeft(centre, ring.SizeDip, 1.0, snap);
        var transform = (TranslateTransform)ring.RenderTransform;
        Check.WithCustomMessage($"The ring is not centred on {centre} (snap: {snap})")
            .That(transform.X).IsCloseTo(expected.X, 1e-9);
        Check.That(transform.Y).IsCloseTo(expected.Y, 1e-9);
    }

    // -------------------------------------------------- D5: the Intro timeout vs the watchdog

    [Fact]
    public void D5_TheIntroTimeout_StaysBelowTheWatchdogsNoProgressBound()
    {
        Check.WithCustomMessage(
                "The Intro's own timeout must fire before C7's 90 s no-progress watchdog, or the "
                + "watchdog aborts the demo screen first and the Intro timer is unreachable (SP-4c D5)")
            .That((long)SubPixelProbeViewModel.IntroTimeoutMs)
            .IsStrictlyLessThan(SubPixelProbeCoordinator.NoProgressMs);
    }

    // Mutation (D5-timeout): IntroTimeoutMs above NoProgressMs -> red.

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>The screens whose text the F1 pins cover, with the targets live on each.</summary>
    private static readonly (SubPixelProbePhase Phase, string[] Ids)[] ScreensWithLiveTargets =
    [
        (SubPixelProbePhase.Intro, [SubPixelProbeViewModel.IntroStartTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Ask, [SubPixelProbeViewModel.AskYesTargetId, SubPixelProbeViewModel.AskLaterTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Result, [SubPixelProbeViewModel.ResultDoneTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Answer, [SubPixelProbeViewModel.AnswerOneTargetId, SubPixelProbeViewModel.AnswerTwoTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.VoidHold, [SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Interval1, [SubPixelProbeViewModel.EndTestTargetId]),
    ];

    private static SubPixelProbeViewModel NewViewModel(
        DwellSettings? dwell = null, TimeProvider? timeProvider = null) => new(
        new WeakReferenceMessenger(),
        timeProvider ?? new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
        SubPixelAbSchedule.Create(seed: 7, new PhysicalRect(0, 0, 1920, 1080)),
        dwell ?? new EyeCtrlSettings().Dwell,
        () => true);

    private static SubPixelProbeWindow NewWindow(
        SubPixelProbeViewModel vm,
        double scale,
        double widthDip,
        double heightDip,
        Func<long>? frameTicks = null,
        CursorSettings? cursor = null)
    {
        var window = new SubPixelProbeWindow(
            null, vm, cursor ?? new CursorSettings(), () => { }, viewFault: () => { },
            scale, frameTicks, frameTicksPerMs: frameTicks is null ? 0 : 1.0);
        window.Width = widthDip;
        window.Height = heightDip;
        new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    private static void Feed(SubPixelProbeViewModel vm, PhysicalPoint position, long timestampUs) =>
        vm.Receive(new GazeCursorMovedMessage(position, timestampUs));

    private static Point CentreInScene(SubPixelProbeWindow window, string id)
    {
        var element = window.TargetElementForTest(id)!;
        return element.TransformToAncestor((Visual)window.Content!)
            .Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
    }

    /// <summary>The laid-out button's rect, in scene DIP.</summary>
    private static Rect RectInScene(SubPixelProbeWindow window, FrameworkElement element)
        => RectInScene(element, (Visual)window.Content!);

    private static Rect RectInScene(FrameworkElement element, Visual root)
    {
        var topLeft = element.TransformToAncestor(root).Transform(new Point(0, 0));
        return new Rect(topLeft, element.RenderSize);
    }

    /// <summary>
    /// The D3 (amended) hit area of a target, in scene DIP: the button grown by the PRODUCTION
    /// margin (not the test's literal), so a margin mutant moves this rect into the text and the
    /// F1 clearance checks go red.
    /// </summary>
    private static Rect HitRectInScene(SubPixelProbeWindow window, string id)
    {
        var element = window.TargetElementForTest(id)!;
        double margin = id == SubPixelProbeViewModel.EndTestTargetId
            ? SubPixelProbeWindow.EndButtonHitMarginDip
            : SubPixelProbeWindow.ButtonHitMarginDip;
        var rect = RectInScene(window, element);
        return new Rect(
            rect.Left - margin, rect.Top - margin, rect.Width + 2 * margin, rect.Height + 2 * margin);
    }

    /// <summary>The distance between two rects in DIP; 0 when they intersect.</summary>
    private static double RectDistance(Rect a, Rect b)
    {
        double dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        double dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Every VISIBLE TextBlock that is not part of a button's own face (its label lives inside its
    /// hit area by construction). The scene root is returned for rect math.
    /// </summary>
    private static List<TextBlock> VisibleTextBlocks(SubPixelProbeWindow window, out Visual root)
    {
        var sceneRoot = (Visual)window.Content!;
        root = sceneRoot;
        var buttons = AllTargetIds
            .Select(id => (DependencyObject?)window.TargetElementForTest(id))
            .Where(b => b is not null)
            .Cast<DependencyObject>()
            .ToList();

        var found = new List<TextBlock>();
        Collect(sceneRoot, found);
        return found
            .Where(tb => !string.IsNullOrWhiteSpace(tb.Text)
                && IsShownInScene(tb, sceneRoot)
                && !buttons.Any(button => IsAncestorOf(button, tb)))
            .ToList();
    }

    private static void Collect(DependencyObject node, List<TextBlock> into)
    {
        if (node is TextBlock tb) into.Add(tb);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Collect(VisualTreeHelper.GetChild(node, i), into);
    }

    private static bool IsAncestorOf(DependencyObject ancestor, DependencyObject node)
    {
        for (DependencyObject? n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    private static bool IsShownInScene(DependencyObject node, Visual root)
    {
        for (DependencyObject? n = node; n is not null; n = VisualTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, root)) return true;
            if (n is UIElement u && u.Visibility != Visibility.Visible) return false;
        }

        return false;
    }

    /// <summary>Drives the view to a phase headlessly through C5's observable Phase (SP-3 F8).</summary>
    private static void SetPhase(SubPixelProbeViewModel vm, SubPixelProbePhase phase) =>
        typeof(SubPixelProbeViewModel)
            .GetProperty(nameof(SubPixelProbeViewModel.Phase), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(vm, phase);
}
