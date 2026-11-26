// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideGeometryTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Headless small-screen geometry gate for <see cref="FirstRunGuideWindow"/> (issue #372).
///
/// <para>
/// Runs in CI's <c>Category!=UI</c> set (no <c>Window.Show()</c>; hosts the real <c>Card</c>
/// visual tree in <see cref="FitHost"/> at effective scale 2.0) so viewports smaller than
/// 1920×1200 DIP — including 1280×720 (1080p @ 150%), 1366×768, 1536×864, 1280×800, 1920×1080
/// and 1024×768 — are measured at their exact requested logical dimensions, independent of the
/// CI runner's physical monitor resolution.
/// </para>
///
/// <para>
/// Verifies every guide step shape in both shipped English and shipped German:
/// (1) exact <see cref="Transform.Identity"/> at ≥1920×1200 and uniform
/// <c>min(1, W/1920, H/1200)</c> below it;
/// (2) every visible text block, button card, chip, close arc and practice ring stays inside
/// <c>[0, W] × [0, H]</c>;
/// (3) zero visual overlap between structural regions (title, body, close arc, practice ring /
/// theme chips, dwell knob row, action choice row);
/// (4) every registered chrome and practice <see cref="DwellTarget"/> matches its rendered visual
/// centre and scaled radius 1:1, covers its card's four corners, and keeps strictly positive
/// pairwise daylight from every other active dwell circle.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class FirstRunGuideGeometryTests
{
    private const double ContainmentToleranceDip = 0.5;
    private const double CentreMatchToleranceDip = 1.0;

    [Theory]
    [InlineData(1920, 1200, 1.0)]
    [InlineData(2560, 1440, 1.0)]
    [InlineData(1920, 1080, 0.9)]
    [InlineData(1536, 864, 0.72)]
    [InlineData(1366, 768, 0.64)]
    [InlineData(1280, 800, 1280.0 / 1920.0)]
    [InlineData(1280, 720, 0.6)]
    [InlineData(1024, 768, 1024.0 / 1920.0)]
    [InlineData(0, 720, 1.0)]
    [InlineData(1280, -1, 1.0)]
    [InlineData(double.NaN, 720, 1.0)]
    public void ComputeSurfaceScale_MatchesUniformMinFormula_AndGuardsInvalidSizes(
        double widthDip, double heightDip, double expected)
    {
        Check.That(FirstRunGuideWindow.ComputeSurfaceScale(widthDip, heightDip))
            .IsCloseTo(expected, 1e-9);
    }

    public static IEnumerable<object[]> ViewportCases()
    {
        yield return [1920.0, 1200.0, "reference 1920x1200"];
        yield return [2560.0, 1440.0, "large 2560x1440"];
        yield return [1920.0, 1080.0, "1080p@100% 1920x1080"];
        yield return [1536.0, 864.0, "1080p@125% 1536x864"];
        yield return [1366.0, 768.0, "laptop 1366x768"];
        yield return [1280.0, 800.0, "16:10 1280x800"];
        yield return [1280.0, 720.0, "1080p@150% 1280x720"];
        yield return [1024.0, 768.0, "4:3 1024x768"];
    }

    [StaTheory]
    [MemberData(nameof(ViewportCases))]
    public void EveryGuideStep_FitsCleanlyWithoutVisualOrDwellOverlap_InEnglishAndGerman(
        double widthDip, double heightDip, string label)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var previousScale = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        var tables = new (string Name, StringTable Table)[]
        {
            ("en", StringTable.Load(UiLanguage.English, CultureInfo.InvariantCulture, AppContext.BaseDirectory)),
            ("de", ShippedGerman.Load()),
        };

        var steps = new[]
        {
            FirstRunGuideStep.Welcome,
            FirstRunGuideStep.MeetDwell,
            FirstRunGuideStep.DwellTime,
            FirstRunGuideStep.Calibrate,
            FirstRunGuideStep.ToolbarTask,
            FirstRunGuideStep.Theme,
            FirstRunGuideStep.Safety,
            FirstRunGuideStep.Done,
        };

        try
        {
            // Match FitHost's effective scale (2.0) so DpiHelper.LogicalToPhysicalX in
            // FirstRunGuideViewModel and PointToScreen in FirstRunGuideWindow share one pixel grid.
            DpiHelper.Update(FitHost.Scale, FitHost.Scale);

            foreach (var (lang, table) in tables)
            {
                var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
                    new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };
                var window = InvisibleTestWindow.Prepare(new FirstRunGuideWindow(vm, widthDip, heightDip));
                try
                {
                    var expectedScale = FirstRunGuideWindow.ComputeSurfaceScale(widthDip, heightDip);
                    Check.That(window.SurfaceScale).IsCloseTo(expectedScale, 1e-9);
                    Check.That(vm.SurfaceScale).IsCloseTo(expectedScale, 1e-9);

                    if (expectedScale < 1.0)
                    {
                        Check.That(window.LayoutRoot.LayoutTransform).IsInstanceOf<ScaleTransform>();
                        var st = (ScaleTransform)window.LayoutRoot.LayoutTransform;
                        Check.That(st.ScaleX).IsCloseTo(expectedScale, 1e-9);
                        Check.That(st.ScaleY).IsCloseTo(expectedScale, 1e-9);
                    }
                    else
                    {
                        Check.That(window.LayoutRoot.LayoutTransform).IsSameReferenceAs(Transform.Identity);
                    }

                    // Detach the production visual tree into FitHost at the exact requested DIP
                    // viewport (never clamped by the runner's physical monitor). Preserve the
                    // window's DataContext so TitleText / BodyText bindings evaluate identically.
                    var content = (FrameworkElement)window.Content;
                    window.Content = null;
                    content.DataContext = vm;
                    using var host = FitHost.Open(content, width: widthDip, height: heightDip, inheritFrom: window);

                    var screen = new Rect(0, 0, widthDip, heightDip);
                    var originPx = content.PointToScreen(new Point(0, 0));

                    foreach (var step in steps)
                    {
                        var ctx = $"{label} / {lang} / {step}";
                        RenderStep(window, table, step);
                        host.Relayout();
                        window.PushCentresNow();

                        // 1. Structural visuals stay inside [0, W] x [0, H] and never collide.
                        var titleRect = BoundsIn(window.TitleText, content);
                        var bodyRect = BoundsIn(window.BodyText, content);
                        AssertInside(screen, titleRect, $"{ctx}: TitleText");
                        AssertInside(screen, bodyRect, $"{ctx}: BodyText");
                        AssertDisjoint(titleRect, bodyRect, $"{ctx}: TitleText vs BodyText");

                        var hasClose = step is not (FirstRunGuideStep.Welcome or FirstRunGuideStep.Done);
                        Rect? closeRect = null;
                        if (hasClose)
                        {
                            var closeVisual = (FrameworkElement)window.ChromeCloseHost.Content;
                            closeRect = BoundsIn(closeVisual, content);
                            AssertInside(screen, closeRect.Value, $"{ctx}: ChromeCloseHost");
                            AssertDisjoint(titleRect, closeRect.Value, $"{ctx}: TitleText vs ChromeCloseHost");
                            AssertDisjoint(bodyRect, closeRect.Value, $"{ctx}: BodyText vs ChromeCloseHost");
                        }

                        var choiceCards = window.ChoiceRow.Children.OfType<FrameworkElement>().ToList();
                        Check.WithCustomMessage($"{ctx}: expected at least one action choice card")
                            .That(choiceCards.Count).IsStrictlyGreaterThan(0);
                        var choiceRects = choiceCards.Select(c => BoundsIn(c, content)).ToList();
                        for (var i = 0; i < choiceRects.Count; i++)
                        {
                            AssertInside(screen, choiceRects[i], $"{ctx}: ChoiceRow[{i}]");
                            AssertDisjoint(bodyRect, choiceRects[i], $"{ctx}: BodyText vs ChoiceRow[{i}]");
                            if (closeRect is { } cr)
                                AssertDisjoint(cr, choiceRects[i], $"{ctx}: ChromeCloseHost vs ChoiceRow[{i}]");
                            for (var j = i + 1; j < choiceRects.Count; j++)
                                AssertDisjoint(choiceRects[i], choiceRects[j], $"{ctx}: ChoiceRow[{i}] vs [{j}]");
                        }

                        Rect? stepVisualRect = null;
                        var chipRects = new List<Rect>();
                        if (step is FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime)
                        {
                            var ring = ((Grid)window.StepContent.Content).Children.OfType<Ellipse>().First();
                            stepVisualRect = BoundsIn(ring, content);
                            AssertInside(screen, stepVisualRect.Value, $"{ctx}: PracticeRing");
                            AssertDisjoint(bodyRect, stepVisualRect.Value, $"{ctx}: BodyText vs PracticeRing");
                            if (closeRect is { } cr)
                                AssertDisjoint(cr, stepVisualRect.Value, $"{ctx}: ChromeCloseHost vs PracticeRing");
                            foreach (var choiceRect in choiceRects)
                                AssertDisjoint(stepVisualRect.Value, choiceRect, $"{ctx}: PracticeRing vs ChoiceRow");
                        }
                        else if (step == FirstRunGuideStep.Theme)
                        {
                            var chipGrid = (Grid)window.StepContent.Content;
                            var chips = chipGrid.Children.OfType<FrameworkElement>().ToList();
                            Check.WithCustomMessage($"{ctx}: Theme step must render 4 chips")
                                .That(chips.Count).IsEqualTo(4);
                            foreach (var chip in chips)
                                chipRects.Add(BoundsIn(chip, content));

                            stepVisualRect = chipRects.Aggregate(Rect.Union);
                            for (var i = 0; i < chipRects.Count; i++)
                            {
                                AssertInside(screen, chipRects[i], $"{ctx}: ThemeChip[{i}]");
                                AssertDisjoint(bodyRect, chipRects[i], $"{ctx}: BodyText vs ThemeChip[{i}]");
                                if (closeRect is { } cr)
                                    AssertDisjoint(cr, chipRects[i], $"{ctx}: ChromeCloseHost vs ThemeChip[{i}]");
                                foreach (var choiceRect in choiceRects)
                                    AssertDisjoint(chipRects[i], choiceRect, $"{ctx}: ThemeChip[{i}] vs ChoiceRow");
                                for (var j = i + 1; j < chipRects.Count; j++)
                                    AssertDisjoint(chipRects[i], chipRects[j], $"{ctx}: ThemeChip[{i}] vs [{j}]");
                            }
                        }

                        var knobCards = new List<FrameworkElement>();
                        if (step == FirstRunGuideStep.DwellTime)
                        {
                            var knobRect = BoundsIn(window.KnobRow, content);
                            AssertInside(screen, knobRect, $"{ctx}: KnobRow");
                            AssertDisjoint(bodyRect, knobRect, $"{ctx}: BodyText vs KnobRow");
                            AssertDisjoint(stepVisualRect!.Value, knobRect, $"{ctx}: PracticeRing vs KnobRow");
                            foreach (var choiceRect in choiceRects)
                                AssertDisjoint(knobRect, choiceRect, $"{ctx}: KnobRow vs ChoiceRow");

                            var plusMinusPanel = window.KnobRow.Children.OfType<StackPanel>().Single();
                            knobCards = plusMinusPanel.Children.OfType<FrameworkElement>().ToList();
                            Check.WithCustomMessage($"{ctx}: KnobRow must render − and + cards")
                                .That(knobCards.Count).IsEqualTo(2);
                        }

                        // 2. Active dwell targets (chrome + practice): centres match rendered
                        //    elements, radii cover their cards, and NO two circles overlap.
                        var allTargets = new List<DwellTarget>(vm.DetectorForTest.Targets);
                        if (step is FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime)
                        {
                            Check.WithCustomMessage($"{ctx}: practice target must be registered")
                                .That(vm.PracticeDetectorForTest.Targets).HasSize(1);
                            allTargets.Add(vm.PracticeDetectorForTest.Targets[0]);
                        }
                        else
                        {
                            Check.That(vm.PracticeDetectorForTest.Targets).IsEmpty();
                        }

                        foreach (var target in allTargets)
                        {
                            var centreDip = ToDipInContent(target.Center, originPx);
                            var radiusDip = target.Radius / FitHost.Scale;

                            Check.WithCustomMessage(
                                    $"{ctx}: target '{target.Id}' centre ({centreDip.X:F1}, {centreDip.Y:F1}) outside {widthDip}x{heightDip}")
                                .That(centreDip.X >= 0 && centreDip.X <= widthDip && centreDip.Y >= 0 && centreDip.Y <= heightDip)
                                .IsTrue();
                            Check.WithCustomMessage($"{ctx}: target '{target.Id}' must have positive radius")
                                .That(radiusDip).IsStrictlyGreaterThan(0);

                            if (target.Id == "guide_practice")
                            {
                                var ring = stepVisualRect!.Value;
                                var expectedCentre = new Point(ring.X + ring.Width / 2, ring.Y + ring.Height / 2);
                                Check.WithCustomMessage($"{ctx}: practice target centre drift")
                                    .That(Distance(centreDip, expectedCentre)).IsLessOrEqualThan(CentreMatchToleranceDip);
                                Check.WithCustomMessage($"{ctx}: practice radius must match rendered ring")
                                    .That(radiusDip).IsCloseTo(ring.Width / 2, 0.5);
                            }
                            else if (target.Id == "guide_close")
                            {
                                var cr = closeRect!.Value;
                                var expectedCentre = new Point(cr.X + cr.Width / 2, cr.Y + cr.Height / 2);
                                Check.WithCustomMessage($"{ctx}: guide_close centre drift")
                                    .That(Distance(centreDip, expectedCentre)).IsLessOrEqualThan(CentreMatchToleranceDip);
                                Check.WithCustomMessage($"{ctx}: guide_close scaled radius")
                                    .That(radiusDip).IsCloseTo(90 * expectedScale, 0.5);
                            }
                        }

                        // Verify every visual choice card, knob ± card, and theme chip is covered
                        // by its own dwell target circle at its exact rendered centre.
                        AssertCardsCoveredByTargets(ctx, choiceCards, vm.DetectorForTest.Targets, content, originPx);
                        if (knobCards.Count > 0)
                            AssertCardsCoveredByTargets(ctx, knobCards, vm.DetectorForTest.Targets, content, originPx);
                        if (step == FirstRunGuideStep.Theme)
                        {
                            var chips = ((Grid)window.StepContent.Content).Children.OfType<FrameworkElement>().ToList();
                            AssertCardsCoveredByTargets(ctx, chips, vm.DetectorForTest.Targets, content, originPx);
                        }

                        // Pairwise non-overlap across EVERY active target (chrome + practice).
                        for (var i = 0; i < allTargets.Count; i++)
                        {
                            var a = allTargets[i];
                            var ca = ToDipInContent(a.Center, originPx);
                            var ra = a.Radius / FitHost.Scale;
                            for (var j = i + 1; j < allTargets.Count; j++)
                            {
                                var b = allTargets[j];
                                var cb = ToDipInContent(b.Center, originPx);
                                var rb = b.Radius / FitHost.Scale;
                                var dist = Distance(ca, cb);
                                var daylight = dist - (ra + rb);
                                Check.WithCustomMessage(
                                        $"{ctx}: dwell circles '{a.Id}' (r={ra:F1}) and '{b.Id}' (r={rb:F1}) "
                                        + $"overlap (dist={dist:F1}, daylight={daylight:F1} DIP)")
                                    .That(daylight).IsStrictlyGreaterThan(0);
                            }
                        }
                    }
                }
                finally
                {
                    window.Close();
                    vm.Dispose();
                }
            }
        }
        finally
        {
            DpiHelper.Update(previousScale.ScaleX, previousScale.ScaleY);
        }
    }

    [StaFact]
    public void DisplayGeometryChangedMessage_ReappliesViewport_RescalesRadiiOnPureDpiChange_AndDetachesOnClose()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var previousScale = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        var messenger = new WeakReferenceMessenger();
        var table = StringTable.Load(UiLanguage.English, CultureInfo.InvariantCulture, AppContext.BaseDirectory);

        try
        {
            DpiHelper.Update(1.0, 1.0);
            var vm = new FirstRunGuideViewModel(messenger, dwellCooldownMs: 0,
                new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };
            var window = InvisibleTestWindow.Prepare(new FirstRunGuideWindow(vm, 1920, 1200));
            try
            {
                RenderStep(window, table, FirstRunGuideStep.DwellTime);
                var expectedActionHalfDiag = Math.Sqrt(320 * 320 / 4.0 + 190 * 190 / 4.0);

                Check.That(window.Width).IsEqualTo(1920);
                Check.That(window.Height).IsEqualTo(1200);
                Check.That(window.SurfaceScale).IsEqualTo(1.0);
                Check.That(window.LayoutRoot.LayoutTransform).IsSameReferenceAs(Transform.Identity);
                Check.That(vm.DetectorForTest.Targets.Single(t => t.Id == "guide_dwelltime_continue").Radius)
                    .IsCloseTo(expectedActionHalfDiag * 1.0, 1e-6);
                Check.That(vm.PracticeDetectorForTest.Targets.Single(t => t.Id == "guide_practice").Radius)
                    .IsCloseTo(110.0 * 1.0, 1e-6);

                // 1. Live shrink to 1080p @ 150% (1280×720 DIP, scale 0.6): window resizes,
                //    LayoutRoot scales to 0.6, and physical radii use 0.6 * 1.5.
                DpiHelper.Update(1.5, 1.5);
                messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1920, 1080, 1.5, 1.5)));

                Check.That(window.Width).IsCloseTo(1280, 1e-6);
                Check.That(window.Height).IsCloseTo(720, 1e-6);
                Check.That(window.SurfaceScale).IsCloseTo(0.6, 1e-9);
                Check.That(vm.SurfaceScale).IsCloseTo(0.6, 1e-9);
                Check.That(window.LayoutRoot.LayoutTransform).IsInstanceOf<ScaleTransform>();
                var scaleTransform = (ScaleTransform)window.LayoutRoot.LayoutTransform;
                Check.That(scaleTransform.ScaleX).IsCloseTo(0.6, 1e-9);
                Check.That(scaleTransform.ScaleY).IsCloseTo(0.6, 1e-9);
                Check.That(vm.DetectorForTest.Targets.Single(t => t.Id == "guide_dwelltime_continue").Radius)
                    .IsCloseTo(expectedActionHalfDiag * 0.6 * 1.5, 1e-6);
                Check.That(vm.PracticeDetectorForTest.Targets.Single(t => t.Id == "guide_practice").Radius)
                    .IsCloseTo(110.0 * 0.6 * 1.5, 1e-6);

                // 2. Return to 1920×1200 @ 100%, then apply a PURE DPI change to 3840×2400 @ 200%
                //    (logical size stays 1920×1200 and SurfaceScale stays 1.0, yet physical radii
                //    must still double to match the new 2.0 DPI scale).
                DpiHelper.Update(1.0, 1.0);
                messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1920, 1200, 1.0, 1.0)));
                Check.That(window.SurfaceScale).IsEqualTo(1.0);
                Check.That(window.LayoutRoot.LayoutTransform).IsSameReferenceAs(Transform.Identity);
                Check.That(vm.DetectorForTest.Targets.Single(t => t.Id == "guide_dwelltime_continue").Radius)
                    .IsCloseTo(expectedActionHalfDiag * 1.0, 1e-6);

                DpiHelper.Update(2.0, 2.0);
                messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(3840, 2400, 2.0, 2.0)));
                Check.That(window.Width).IsCloseTo(1920, 1e-6);
                Check.That(window.Height).IsCloseTo(1200, 1e-6);
                Check.That(window.SurfaceScale).IsEqualTo(1.0);
                Check.That(window.LayoutRoot.LayoutTransform).IsSameReferenceAs(Transform.Identity);
                Check.That(vm.DetectorForTest.Targets.Single(t => t.Id == "guide_dwelltime_continue").Radius)
                    .IsCloseTo(expectedActionHalfDiag * 2.0, 1e-6);
                Check.That(vm.PracticeDetectorForTest.Targets.Single(t => t.Id == "guide_practice").Radius)
                    .IsCloseTo(110.0 * 2.0, 1e-6);

                // 3. Transient unusable 0×0 geometry is ignored.
                messenger.Send(new DisplayGeometryChangedMessage(DisplayGeometry.Unscaled));
                Check.That(window.Width).IsCloseTo(1920, 1e-6);
                Check.That(window.Height).IsCloseTo(1200, 1e-6);

                // 4. Closing the window detaches it from further DisplayGeometryChanged messages.
                window.Close();
                DpiHelper.Update(1.0, 1.0);
                messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1280, 720, 1.0, 1.0)));
                Check.That(window.Width).IsCloseTo(1920, 1e-6);
                Check.That(window.Height).IsCloseTo(1200, 1e-6);
            }
            finally
            {
                window.Close();
                vm.Dispose();
            }
        }
        finally
        {
            DpiHelper.Update(previousScale.ScaleX, previousScale.ScaleY);
        }
    }

    [StaFact]
    public void DisplayGeometryChangedMessage_AutomaticallyRepushesCentresOnHostedSurface()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var previousScale = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        var messenger = new WeakReferenceMessenger();
        var table = StringTable.Load(UiLanguage.English, CultureInfo.InvariantCulture, AppContext.BaseDirectory);

        try
        {
            DpiHelper.Update(FitHost.Scale, FitHost.Scale);
            var vm = new FirstRunGuideViewModel(messenger, dwellCooldownMs: 0,
                new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };
            // Start at 1920×1200, host the live Card inside a 1280×720 FitHost, then deliver a
            // DisplayGeometryChangedMessage for 1280×720 @ 2.0 and verify the automatic PushCentres
            // aligns every chrome and practice target without any manual PushCentresNow call.
            var window = InvisibleTestWindow.Prepare(new FirstRunGuideWindow(vm, 1920, 1200));
            try
            {
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.DataContext = vm;
                using var host = FitHost.Open(content, width: 1280, height: 720, inheritFrom: window);

                RenderStep(window, table, FirstRunGuideStep.DwellTime);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                messenger.Send(new DisplayGeometryChangedMessage(
                    new DisplayGeometry(1280 * FitHost.Scale, 720 * FitHost.Scale, FitHost.Scale, FitHost.Scale)));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                var originPx = content.PointToScreen(new Point(0, 0));
                var choiceCards = window.ChoiceRow.Children.OfType<FrameworkElement>().ToList();
                var knobCards = window.KnobRow.Children.OfType<StackPanel>().Single()
                    .Children.OfType<FrameworkElement>().ToList();

                AssertCardsCoveredByTargets("live-resize 1280x720", choiceCards, vm.DetectorForTest.Targets, content, originPx);
                AssertCardsCoveredByTargets("live-resize 1280x720", knobCards, vm.DetectorForTest.Targets, content, originPx);

                var ring = ((Grid)window.StepContent.Content).Children.OfType<Ellipse>().First();
                var ringRect = BoundsIn(ring, content);
                var practice = vm.PracticeDetectorForTest.Targets.Single();
                var practiceCentreDip = ToDipInContent(practice.Center, originPx);
                Check.That(Distance(practiceCentreDip,
                        new Point(ringRect.X + ringRect.Width / 2, ringRect.Y + ringRect.Height / 2)))
                    .IsLessOrEqualThan(CentreMatchToleranceDip);
                Check.That(practice.Radius / FitHost.Scale).IsCloseTo(ringRect.Width / 2, 0.5);
            }
            finally
            {
                window.Close();
                vm.Dispose();
            }
        }
        finally
        {
            DpiHelper.Update(previousScale.ScaleX, previousScale.ScaleY);
        }
    }

    private static void RenderStep(FirstRunGuideWindow window, StringTable table, FirstRunGuideStep step)
    {
        var (title, body) = step switch
        {
            FirstRunGuideStep.Welcome => (table.T("guide.welcome.title"), table.T("guide.welcome.body")),
            FirstRunGuideStep.MeetDwell => (table.T("guide.dwell.title"), table.T("guide.dwell.body")),
            FirstRunGuideStep.DwellTime => (table.T("guide.dwelltime.title"), table.T("guide.dwelltime.body")),
            FirstRunGuideStep.Calibrate => (table.T("guide.calibrate.title"), table.T("guide.calibrate.body")),
            FirstRunGuideStep.ToolbarTask => (table.T("guide.toolbar.title"),
                string.Format(CultureInfo.InvariantCulture, table.T("guide.toolbar.body"),
                    table.T("guide.toolbar.edge.bottom"))),
            FirstRunGuideStep.Theme => (table.T("guide.theme.title"), table.T("guide.theme.body")),
            FirstRunGuideStep.Safety => (table.T("guide.safety.title"), table.T("guide.safety.body")),
            FirstRunGuideStep.Done => (table.T("guide.done.title"), table.T("guide.done.body")),
            _ => (string.Empty, string.Empty),
        };

        (string Label, string Id, ModalChoiceKind Kind)[] choices = step switch
        {
            FirstRunGuideStep.Welcome => [
                (table.T("guide.welcome.start"), "guide_start", ModalChoiceKind.Affirmative),
                (table.T("guide.later"), "guide_later", ModalChoiceKind.Negative)],
            FirstRunGuideStep.MeetDwell => [(table.T("guide.dwell.skip"), "guide_dwell_skip", ModalChoiceKind.Neutral)],
            FirstRunGuideStep.DwellTime => [(table.T("guide.dwelltime.continue"), "guide_dwelltime_continue", ModalChoiceKind.Affirmative)],
            FirstRunGuideStep.Calibrate => [
                (table.T("guide.calibrate.now"), "guide_calibrate_now", ModalChoiceKind.Affirmative),
                (table.T("guide.calibrate.skip"), "guide_calibrate_skip", ModalChoiceKind.Neutral)],
            FirstRunGuideStep.ToolbarTask => [
                (table.T("guide.toolbar.continue"), "guide_toolbar_continue", ModalChoiceKind.Affirmative),
                (table.T("guide.toolbar.skip"), "guide_toolbar_skip", ModalChoiceKind.Neutral)],
            FirstRunGuideStep.Theme => [(table.T("guide.theme.continue"), "guide_theme_continue", ModalChoiceKind.Affirmative)],
            FirstRunGuideStep.Safety => [
                (table.T("settings.modal.yes"), "guide_safety_yes", ModalChoiceKind.Affirmative),
                (table.T("settings.modal.no"), "guide_safety_no", ModalChoiceKind.Neutral)],
            FirstRunGuideStep.Done => [(table.T("guide.done.finish"), "guide_done_finish", ModalChoiceKind.Affirmative)],
            _ => [],
        };

        (string Label, string Id)[] chips =
        [
            (table.T("guide.theme.midnight"), "guide_theme_midnight"),
            (table.T("guide.theme.ember"), "guide_theme_ember"),
            (table.T("guide.theme.daylight"), "guide_theme_daylight"),
            (table.T("guide.theme.contrast"), "guide_theme_contrast"),
        ];

        var chromeClose = step is not (FirstRunGuideStep.Welcome or FirstRunGuideStep.Done);
        window.ShowCardContent(title, body, chromeClose);
        window.SetChoices(choices);
        window.SetChromeClose(chromeClose, table.T("guide.chrome.close"));
        window.SetPractice(step is FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime);
        window.SetDwellKnob(step == FirstRunGuideStep.DwellTime);
        window.SetThemeGrid(step == FirstRunGuideStep.Theme, chips);
    }

    private static void AssertCardsCoveredByTargets(
        string ctx,
        IReadOnlyList<FrameworkElement> cards,
        IReadOnlyList<DwellTarget> targets,
        FrameworkElement content,
        Point originPx)
    {
        foreach (var card in cards)
        {
            var rect = BoundsIn(card, content);
            var cardCentre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var nearest = targets
                .Select(t => (Target: t, CentreDip: ToDipInContent(t.Center, originPx), RadiusDip: t.Radius / FitHost.Scale))
                .MinBy(x => Distance(x.CentreDip, cardCentre));

            Check.WithCustomMessage($"{ctx}: card at {rect} does not align with its dwell target '{nearest.Target.Id}'")
                .That(Distance(nearest.CentreDip, cardCentre)).IsLessOrEqualThan(CentreMatchToleranceDip);

            var halfDiag = Math.Sqrt(rect.Width * rect.Width / 4 + rect.Height * rect.Height / 4);
            Check.WithCustomMessage(
                    $"{ctx}: target '{nearest.Target.Id}' radius {nearest.RadiusDip:F1} DIP must match "
                    + $"rendered card half-diagonal {halfDiag:F1} DIP")
                .That(nearest.RadiusDip).IsCloseTo(halfDiag, 0.5);
        }
    }

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor)
    {
        Check.WithCustomMessage($"{element.Name} rendered with zero size")
            .That(element.RenderSize.Width > 0 && element.RenderSize.Height > 0).IsTrue();
        return element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    }

    private static Point ToDipInContent(PhysicalPoint pt, Point originPx) =>
        new((pt.X - originPx.X) / FitHost.Scale, (pt.Y - originPx.Y) / FitHost.Scale);

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void AssertInside(Rect container, Rect element, string what)
    {
        var padded = new Rect(
            container.X - ContainmentToleranceDip,
            container.Y - ContainmentToleranceDip,
            container.Width + 2 * ContainmentToleranceDip,
            container.Height + 2 * ContainmentToleranceDip);
        Check.WithCustomMessage($"{what} ({element}) clips outside viewport ({container})")
            .That(padded.Contains(element)).IsTrue();
    }

    private static void AssertDisjoint(Rect a, Rect b, string what)
    {
        var overlap = Rect.Intersect(a, b);
        var area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
        Check.WithCustomMessage($"{what}: rects overlap ({a} vs {b}, intersection={overlap})")
            .That(area).IsLessOrEqualThan(0.5);
    }
}
