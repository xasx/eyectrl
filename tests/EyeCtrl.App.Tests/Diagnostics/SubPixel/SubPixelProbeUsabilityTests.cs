namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Reflection;
using System.Windows;
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
/// D1: every button's <see cref="DwellOutlineOverlay"/> outline follows C5's dwell progress.
/// D2/D3: the button sizes, the forgiving hit circles and the no-overlap rule at the owner's
/// 2.0/3840×2400 and at 1.0/1920×1080, plus the layout fit at both. D5: the Intro's motion demo
/// (the trial's drift law, both conditions, hidden elsewhere) and the Intro layout that keeps the
/// Start button and its hit circle off the demo's path.
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

    /// <summary>
    /// D2's sizes and D3's hit-circle margins, per target — literals, so a mutant that changes the
    /// production constant (or the margin a button gets) cannot move the test's expectation with it.
    /// </summary>
    private static (double Width, double Height, double Margin) SpecFor(string id) => id switch
    {
        SubPixelProbeViewModel.AnswerOneTargetId or SubPixelProbeViewModel.AnswerTwoTargetId
            => (380, 260, 48),
        SubPixelProbeViewModel.EndTestTargetId
            => (280, 120, 16),
        _ => (420, 170, 48),
    };

    /// <summary>The live targets of every screen, in the shape the D3 no-overlap rule needs.</summary>
    private static readonly (SubPixelProbePhase Phase, string[] Ids)[] LiveTargets =
    [
        (SubPixelProbePhase.Intro, [SubPixelProbeViewModel.IntroStartTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Answer, [SubPixelProbeViewModel.AnswerOneTargetId, SubPixelProbeViewModel.AnswerTwoTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Ask, [SubPixelProbeViewModel.AskYesTargetId, SubPixelProbeViewModel.AskLaterTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Result, [SubPixelProbeViewModel.ResultDoneTargetId, SubPixelProbeViewModel.EndTestTargetId]),
    ];

    // ------------------------------------------------------------------ D1: the cue on the button

    [StaFact]
    public void D1_TheDwellCue_ShowsTheTargetsProgressOnItsButton()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = 700;
        using var vm = NewViewModel(settings.Dwell);

        var window = NewWindow(vm, scale: 1.0, widthDip: 1920, heightDip: 1080);
        try
        {
            vm.Start();
            SetPhase(vm, SubPixelProbePhase.Idle);
            SetPhase(vm, SubPixelProbePhase.Answer);

            var cue = window.CueForTest(SubPixelProbeViewModel.AnswerOneTargetId);
            Check.That(cue).IsNotNull();
            var centre = CentreInScene(window, SubPixelProbeViewModel.AnswerOneTargetId);
            var at = new PhysicalPoint(centre.X, centre.Y);

            window.OnFrame();
            Check.WithCustomMessage("A target with no dwell must show no outline")
                .That(cue!.Outline.StrokeThickness).IsEqualTo(0.0);

            // A realistic cadence: a 350 ms hole between two samples would itself be a dropout,
            // and the grace would (correctly) treat it as one.
            Feed(vm, at, 0);
            for (long t = 10_000; t <= 350_000; t += 10_000) Feed(vm, at, t);
            window.OnFrame();
            Check.WithCustomMessage("Progress 0.5 must draw half the outline")
                .That(cue.Outline.StrokeThickness)
                .IsCloseTo(DwellOutlineOverlay.MaxThickness * 0.5, 1e-9);

            for (long t = 360_000; t <= 630_000; t += 10_000) Feed(vm, at, t);   // 0.9
            window.OnFrame();
            Check.WithCustomMessage("Progress 0.9 must draw 90 % of the outline")
                .That(cue.Outline.StrokeThickness)
                .IsCloseTo(DwellOutlineOverlay.MaxThickness * 0.9, 1e-9);
        }
        finally
        {
            window.Close();
        }
    }

    // Mutation (D1-binding): UpdateDwellCues hardcodes 0 (or 1) -> both thickness checks go red.
    // Mutation (D1-overlay): the cue is not attached (BuildButton drops Attach) -> CueForTest is
    //   null and every thickness write is a no-op: red.

    // ------------------------------------------------ D2/D3: sizes, hit circles, no overlap

    [StaFact]
    public void D2D3_ButtonsHitCirclesAndNoOverlap_MatchTheDecisionsAtBothOwnerScales()
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

                    var centre = CentreInScene(window, id);
                    var centrePhys = new PhysicalPoint(centre.X * scale, centre.Y * scale);
                    double radiusPhysical = (Math.Max(expectedWidth, expectedHeight) / 2.0 + margin) * scale;

                    Check.WithCustomMessage(
                            $"{id}@{scale}: a point {radiusPhysical - 1} px from the centre must be inside "
                            + $"the D3 hit circle ({margin} DIP margin)")
                        .That(detector.TargetHit(id, new PhysicalPoint(centrePhys.X + radiusPhysical - 1, centrePhys.Y)))
                        .IsTrue();
                    Check.WithCustomMessage(
                            $"{id}@{scale}: a point {radiusPhysical + 1} px from the centre must be outside "
                            + $"the D3 hit circle (the owner's bias is not covered by a longer circle)")
                        .That(detector.TargetHit(id, new PhysicalPoint(centrePhys.X + radiusPhysical + 1, centrePhys.Y)))
                        .IsFalse();
                }

                for (int i = 0; i < ids.Length; i++)
                {
                    for (int j = i + 1; j < ids.Length; j++)
                    {
                        var a = CentreInScene(window, ids[i]);
                        var b = CentreInScene(window, ids[j]);
                        double ra = (Math.Max(SpecFor(ids[i]).Width, SpecFor(ids[i]).Height) / 2.0
                            + SpecFor(ids[i]).Margin) * scale;
                        double rb = (Math.Max(SpecFor(ids[j]).Width, SpecFor(ids[j]).Height) / 2.0
                            + SpecFor(ids[j]).Margin) * scale;
                        double distance = Math.Sqrt(
                            Math.Pow(a.X * scale - b.X * scale, 2) + Math.Pow(a.Y * scale - b.Y * scale, 2));

                        Check.WithCustomMessage(
                                $"D3: {ids[i]} and {ids[j]} hit circles overlap in {phase} at scale {scale} "
                                + $"(centres {distance:F0} px apart, radii {ra:F0} + {rb:F0})")
                            .That(distance).IsStrictlyGreaterThan(ra + rb);
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
    // Mutation (D3-radius): Register goes back to Math.Min (half the SHORTER side) -> the
    //   inside-the-circle probe at radius - 1 goes red for every non-square button.
    // Mutation (D3-margin): the answers' 48 becomes 0 or the End test's 16 becomes 48 -> the
    //   outside/inside probes go red.
    // Mutation (D3-overlap): the Ask screen goes back to stacking (24 DIP apart) -> the pairwise
    //   no-overlap check for Yes/Later goes red.

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
            foreach (var (phase, id) in new (SubPixelProbePhase, string?)[]
                     {
                         (SubPixelProbePhase.Intro, null),
                         (SubPixelProbePhase.VoidHold, null),
                         (SubPixelProbePhase.Ask, null),
                         (SubPixelProbePhase.Result, null),
                         (SubPixelProbePhase.Answer, SubPixelProbeViewModel.AnswerOneTargetId),
                     })
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

                if (id is not null)
                {
                    var rect = RectInScene(window, window.TargetElementForTest(id)!);
                    Check.WithCustomMessage($"The answer button at {scale} is not inside the window: {rect}")
                        .That(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= widthDip && rect.Bottom <= heightDip)
                        .IsTrue();
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    // Mutation (D2-fit): the Intro panel is centred again (or the demo gap dropped) -> at 1.0 the
    //   panel's bottom passes the window's bottom: red.

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
            double travel = SubPixelAbDriftMotion.StepPixelsPerTick
                * (SubPixelProbeViewModel.ScriptedIntervalMs / (double)SubPixelAbDriftMotion.TickMilliseconds);
            double startX = 1920 / 2.0 - travel / 2.0;
            double centreY = 1080 / 2.0;

            SetPhase(vm, SubPixelProbePhase.Idle);
            SetPhase(vm, SubPixelProbePhase.Intro);

            window.OnFrame();
            Check.WithCustomMessage("The Intro must draw the demo ring").That(window.IsRingDrawnForTest).IsTrue();
            AssertRingAt(ring, new PhysicalPoint(startX, centreY), snap: true);

            // 1.8 s into interval 1: 60 ticks × 0.25 px East — exactly a trial's drift law.
            ticks = 1800;
            window.OnFrame();
            AssertRingAt(ring, new PhysicalPoint(startX + 15, centreY), snap: true);

            // The pair's 0.8 s gap hides the ring, exactly as a trial's gap does.
            ticks = 3400;
            window.OnFrame();
            Check.WithCustomMessage("The demo ring must hide during the gap")
                .That(window.IsRingDrawnForTest).IsFalse();

            // Interval 2 runs condition B — the same path, the same law, no snapping.
            ticks = 3800 + 1800;
            window.OnFrame();
            Check.That(window.IsRingDrawnForTest).IsTrue();
            AssertRingAt(ring, new PhysicalPoint(startX + 15, centreY), snap: false);

            // It loops: one cycle later the pair repeats with condition A again.
            ticks = 7600 + 1800;
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

    // Mutation (D5-snap): both demo intervals pass snap: true -> the condition-B assertion goes
    //   red (the two expectations differ at this centre: 837.5 snapped to 838).
    // Mutation (D5-swap): the demo's snap flags swap -> both condition assertions go red.
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

    [StaFact]
    public void D5_OnTheIntro_StartIsBelowTheDemoBand_AndItsHitCircleClearsIt()
    {
        foreach (var (scale, widthDip, heightDip) in OwnerScales)
            AssertIntroClearance(scale, widthDip, heightDip);
    }

    private static void AssertIntroClearance(double scale, double widthDip, double heightDip)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();
        var window = NewWindow(vm, scale, widthDip, heightDip);
        try
        {
            SetPhase(vm, SubPixelProbePhase.Idle);
            SetPhase(vm, SubPixelProbePhase.Intro);

            var startDip = CentreInScene(window, SubPixelProbeViewModel.IntroStartTargetId);
            var startPhys = new PhysicalPoint(startDip.X * scale, startDip.Y * scale);
            double startRadius = (420 / 2.0 + 48) * scale;   // D3's literal margin, not the constant
            double ringRadius = window.RingForTest.SizeDip * scale / 2.0;
            double bandCentreY = heightDip * scale / 2.0;
            double bandBottom = bandCentreY + ringRadius;

            // The demo's travel is horizontal, so the vertical gap IS the closest approach —
            // provided the Start button sits under the band's x extent (it does: centred panel).
            Check.WithCustomMessage(
                    $"At {scale}, the Start hit circle reaches the demo band: its top is at "
                    + $"{startPhys.Y - startRadius:F1}, the band's bottom at {bandBottom:F1}")
                .That(startPhys.Y - startRadius).IsStrictlyGreaterThan(bandBottom);
            Check.WithCustomMessage($"At {scale}, Start is not below the demo's centre line")
                .That(startPhys.Y).IsStrictlyGreaterThan(bandCentreY);
        }
        finally
        {
            window.Close();
        }
    }

    // Mutation (D5-clearance): the Intro panel centred again -> the ring path runs through the
    //   Start hit circle: the clearance assertion goes red.

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

    private static SubPixelProbeViewModel NewViewModel(DwellSettings? dwell = null) => new(
        new WeakReferenceMessenger(),
        new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
        SubPixelAbSchedule.Create(seed: 7, new PhysicalRect(0, 0, 1920, 1080)),
        dwell ?? new EyeCtrlSettings().Dwell,
        () => true);

    private static SubPixelProbeWindow NewWindow(
        SubPixelProbeViewModel vm,
        double scale,
        double widthDip,
        double heightDip,
        Func<long>? frameTicks = null)
    {
        var window = new SubPixelProbeWindow(
            null, vm, new CursorSettings(), () => { }, viewFault: () => { },
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

    private static Rect RectInScene(SubPixelProbeWindow window, FrameworkElement element)
    {
        var topLeft = element.TransformToAncestor((Visual)window.Content!).Transform(new Point(0, 0));
        return new Rect(topLeft, element.RenderSize);
    }

    /// <summary>Drives the view to a phase headlessly through C5's observable Phase (SP-3 F8).</summary>
    private static void SetPhase(SubPixelProbeViewModel vm, SubPixelProbePhase phase) =>
        typeof(SubPixelProbeViewModel)
            .GetProperty(nameof(SubPixelProbeViewModel.Phase), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(vm, phase);
}
