namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// SP-4e T2 (amendment A1) — the interval corner digit. The owner could not tell the rings apart
/// ("Can't tell ring 1 from 2"): the "1"/"2" in the bottom-left corner was a 14 DIP
/// <c>TextSecondary</c> line. A1 makes it the answer buttons' 96 DIP in <c>TextPrimary</c>, still
/// bottom-left, still far from the ring and outside the toolbar band.
///
/// <para>
/// The pin builds the REAL headless <see cref="SubPixelProbeWindow"/> at both owner scales, per
/// registered theme, with the owner's ring (220 DIP) and at the settings ceiling (500 DIP), and
/// reads the digit's own brushes and its laid-out box off the tree. It asserts: the size EQUALS
/// <see cref="SubPixelProbeWindow.AnswerLabelFontSizeDip"/> (a mutant back to
/// <c>FontSizeBodyLarge</c> fails on the equality, not only through geometry); the colour is the
/// theme's <c>TextPrimary</c>; the box sits in the bottom-left quadrant and inside the screen; it is
/// below the toolbar band; it does not overlap End test's grown hit area; and it keeps a pinned
/// clearance from the ring's maximum extent — the screen's middle half, grown by the drift's
/// 41.5 physical px and the ring's radius, which is the worst case of any trial the schedule can
/// produce (drift and static; the live target never carries the ring). The 41.5 px is a LITERAL:
/// the envelope must not shrink with the production constant a mutant moves.
/// </para>
///
/// <para>
/// This class activates themes and reads process-global token brushes, so it sits in the exclusive
/// <c>ActiveTheme</c> collection and restores Midnight afterwards.
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class SubPixelProbeCornerLabelTests
{
    /// <summary>T2's digit size as a literal: a mutant back to 14 DIP must fail here.</summary>
    private const double AnswerDigitsDip = 96;

    /// <summary>The owner's ring during the legibility look (A1 records 220 DIP, thickness 2).</summary>
    private const double OwnerRingDip = 220;

    /// <summary>The settings ceiling — <see cref="CursorSettings.CircleSize"/> validates 10–500.</summary>
    private const double MaxRingDip = 500;

    /// <summary>
    /// The pinned clearance at the owner's ring. Measured: "1" keeps 283.5 DIP at 2.0 and 262.7 at
    /// 1.0; "2" is the wider digit (55.2 DIP) and keeps 270.0 / 249.3. 240 is the floor with
    /// headroom, and any overlap fails through the intersection check anyway.
    /// </summary>
    private const double OwnerRingClearanceDip = 240;

    /// <summary>
    /// The pinned clearance at the 500 DIP settings ceiling. Measured 143.5 DIP at 2.0 and 122.7 at
    /// 1.0 — the digit still clears the largest ring the app accepts.
    /// </summary>
    private const double MaxRingClearanceDip = 100;

    /// <summary>The drift's whole-interval travel, in physical px: 0.25 × 166 whole 30 ms ticks.</summary>
    private const double DriftTravelPx = 41.5;

    /// <summary>
    /// The production stack between the trial label's box bottom and the digit's box top, as a
    /// literal: SP-4e fixup 01 F2b pins the 8 DIP gap so the review's X5 (the label drawn through
    /// the digit) cannot survive a production constant change.
    /// </summary>
    private const double TrialLabelGapDip = 8;

    /// <summary>F1's clearance rule, reused against End test's hit area.</summary>
    private const double HitAreaClearanceDip = 24;

    private static readonly (double Scale, double WidthDip, double HeightDip)[] OwnerScales =
    [
        (2.0, 1920, 1200),   // 3840 × 2400 device px
        (1.0, 1920, 1080),
    ];

    [StaFact]
    public void EveryRegisteredTheme_KeepsTheCornerDigitBigBrightAndClear()
    {
        var rendered = new List<string>();
        try
        {
            foreach (var theme in ThemeRegistry.All)
            {
                Tokens.Activate(theme);
                rendered.Add(theme.Name);

                foreach (var (scale, widthDip, heightDip) in OwnerScales)
                {
                    AssertCornerDigit(theme, scale, widthDip, heightDip, OwnerRingDip, OwnerRingClearanceDip);
                    AssertCornerDigit(theme, scale, widthDip, heightDip, MaxRingDip, MaxRingClearanceDip);
                }
            }
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }

        // The SP-4d fixup 01 F2 pattern: bind the names the LOOP rendered to the registry, so a
        // narrowed loop cannot pass on a separate count expression.
        Check.WithCustomMessage(
                $"The loop rendered [{string.Join(", ", rendered)}]; the registry holds "
                + $"[{string.Join(", ", ThemeRegistry.Names)}]. A theme the app can present is not "
                + "covered by this gate (SP-4e T2).")
            .That(rendered).ContainsExactly(ThemeRegistry.Names.ToArray());
    }

    // Mutation (t3): the corner label back to Tokens.FontSizeBodyLarge -> the 96 DIP equality goes red.
    // Mutation (t4): the corner label moved to the screen centre -> the bottom-left-quadrant pin and
    //   the ring-clearance pin both go red.
    // Mutation (colour): the corner label back to TextSecondary -> the colour pin goes red.

    private static void AssertCornerDigit(
        ITheme theme, double scale, double widthDip, double heightDip, double ringDip, double clearanceFloorDip)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();
        var window = NewWindow(vm, scale, widthDip, heightDip, ringDip);
        try
        {
            Check.WithCustomMessage($"{theme.Name}@{scale}: the ring does not draw at {ringDip} DIP")
                .That(window.RingForTest.SizeDip).IsEqualTo(ringDip);

            foreach ((var phase, string digit) in new[]
                     {
                         (SubPixelProbePhase.Interval1, "1"),
                         (SubPixelProbePhase.Interval2, "2"),
                     })
            {
                SetPhase(vm, SubPixelProbePhase.Idle);
                SetPhase(vm, phase);

                var label = window.CornerDigitForTest;
                string where = $"{theme.Name}@{scale}/{digit}";

                Check.WithCustomMessage($"{where}: the corner label shows '{label.Text}', not '{digit}'")
                    .That(label.Text).IsEqualTo(digit);

                Check.WithCustomMessage(
                        $"{where}: the digit is {label.FontSize} DIP; T2 requires the answer buttons' "
                        + $"{AnswerDigitsDip}.")
                    .That(label.FontSize).IsEqualTo(AnswerDigitsDip);

                var colour = ((SolidColorBrush)label.Foreground).Color;
                Check.WithCustomMessage(
                        $"{where}: the digit colour is {colour}, not the theme's TextPrimary "
                        + $"({theme.TextPrimary}).")
                    .That(colour).IsEqualTo(theme.TextPrimary);

                var box = ContentBoxInScene(label, (Visual)window.Content!);

                // On screen, anchored to the bottom-left CORNER (SP-4e fixup 01 F2a): a
                // centre-in-the-quadrant check passed the reviewer's t4a (the digit at mid-height on
                // the left edge), so the pin asks what "bottom-left" means — the box's bottom edge
                // within 48 DIP of the window's bottom, and its left edge within 48 DIP of the left.
                // Measured: bottom at height-24 and left at 24, both scales.
                Check.WithCustomMessage($"{where}: the digit box {box} is off-screen")
                    .That(box.Left >= 0 && box.Top >= 0 && box.Right <= widthDip && box.Bottom <= heightDip)
                    .IsTrue();
                Check.WithCustomMessage(
                        $"{where}: the digit box {box} is not bottom-left: bottom {box.Bottom:F1} "
                        + $"(needs >= {heightDip - 48:F1}) at height {heightDip}, left {box.Left:F1} "
                        + "(needs <= 48)")
                    .That(box.Bottom >= heightDip - 48 && box.Left <= 48).IsTrue();

                // Outside the toolbar's top band. The band is the toolbar's size plus the 40 DIP
                // margin RepeatModeController.BuildToolbarBandRect uses, both logical DIP.
                double bandBottomDip = new EyeCtrlSettings().Toolbar.Size + 40;
                Check.WithCustomMessage(
                        $"{where}: the digit box top is {box.Top:F1} DIP, inside the toolbar band "
                        + $"(bottom {bandBottomDip:F1})")
                    .That(box.Top).IsStrictlyGreaterThan(bandBottomDip);

                // No overlap with End test's grown hit area (bottom-right).
                var endHit = HitRectInScene(window, SubPixelProbeViewModel.EndTestTargetId);
                Check.WithCustomMessage(
                        $"{where}: the digit box {box} overlaps End test's hit area {endHit}")
                    .That(Rect.Intersect(box, endHit).IsEmpty).IsTrue();
                Check.WithCustomMessage(
                        $"{where}: the digit box {box} is only {RectDistance(box, endHit):F1} DIP from "
                        + $"End test's hit area {endHit} (F1 asks {HitAreaClearanceDip})")
                    .That(RectDistance(box, endHit)).IsGreaterOrEqualThan(HitAreaClearanceDip);

                // The ring's maximum extent over any scheduled trial: the middle half of the screen,
                // grown by the drift's whole-interval travel and the ring's radius. The 41.5 px is
                // the A1 law's own value and a literal, so a mutant that shrinks the interval cannot
                // shrink this envelope. Static parks sit inside the region and the live target never
                // carries the ring, so drift is the worst case.
                double radius = ringDip / 2.0;
                double travelDip = DriftTravelPx / scale;
                var extent = new Rect(
                    widthDip * 0.25 - travelDip - radius,
                    heightDip * 0.25 - travelDip - radius,
                    widthDip * 0.5 + 2 * (travelDip + radius),
                    heightDip * 0.5 + 2 * (travelDip + radius));

                Check.WithCustomMessage(
                        $"{where}: the digit box {box} overlaps the ring's maximum extent {extent} "
                        + $"(ring {ringDip}, travel {travelDip:F1} DIP at {scale})")
                    .That(Rect.Intersect(box, extent).IsEmpty).IsTrue();
                Check.WithCustomMessage(
                        $"{where}: the digit box {box} keeps only {extent.Left - box.Right:F1} DIP from "
                        + $"the ring's maximum extent {extent}; the pin asks {clearanceFloorDip} "
                        + $"(ring {ringDip}, travel {travelDip:F1} DIP at {scale})")
                    .That(extent.Left - box.Right).IsGreaterOrEqualThan(clearanceFloorDip);
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ F2b: the trial label's stack

    /// <summary>
    /// SP-4e fixup 01 F2b (review 01 X5): on a real scored trial the trial label ("Drift block ·
    /// trial 1 of 10") stacks above the digit's box with the production 8 DIP gap. No pin looked at
    /// the two boxes together, so X5 (the label drawn through the 96 DIP digit) survived; this one
    /// measures both on the driven window at both owner scales.
    /// </summary>
    [StaFact]
    public void TheTrialLabel_StacksAboveTheDigit_WithTheEightDipGap_OnARealScoredTrial()
    {
        foreach (var (scale, widthDip, heightDip) in OwnerScales)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            var time = new ManualTimeProvider();
            using var vm = NewViewModel(time);
            vm.Start();
            var window = NewWindow(vm, scale, widthDip, heightDip, OwnerRingDip);
            try
            {
                // The demo pair runs first; drive on the virtual clock to the first SCORED interval,
                // where the trial label carries its real text ("the demo hint" is a different line).
                vm.BeginTrials();
                long ts = 1_000_000;
                int steps = 0;
                while (vm.Phase != SubPixelProbePhase.Interval1 || vm.IsDemo)
                {
                    time.Advance(30);
                    ts += 30_000;
                    Feed(vm, new PhysicalPoint(50, 50), ts);
                    Check.WithCustomMessage(
                            $"The run did not reach the first scored interval within 60 s of virtual "
                            + $"time (phase {vm.Phase}, demo {vm.IsDemo})")
                        .That(++steps).IsStrictlyLessThan(2_000);
                }

                var scene = (Visual)window.Content!;
                var digitBox = ContentBoxInScene(window.CornerDigitForTest, scene);

                string expectedTrial = StringTable.Current.T(
                    "overlays.subpixel.trial_label",
                    StringTable.Current.T("overlays.subpixel.block_drift"), 1);
                var trialLabel = FindTextBlock(scene, expectedTrial);
                Check.WithCustomMessage(
                        $"@{scale}: the trial label '{expectedTrial}' is not in the scene; it holds "
                        + $"[{string.Join(" | ", AllVisibleTexts(scene))}]")
                    .That(trialLabel).IsNotNull();

                var trialBox = RectInScene(trialLabel!, scene);

                Check.WithCustomMessage(
                        $"@{scale}: the trial label {trialBox} overlaps the digit box {digitBox}")
                    .That(Rect.Intersect(trialBox, digitBox).IsEmpty).IsTrue();

                double gap = digitBox.Top - trialBox.Bottom;
                Check.WithCustomMessage(
                        $"@{scale}: the trial label {trialBox} keeps only {gap:F2} DIP above the digit "
                        + $"box {digitBox}; F2b pins the production {TrialLabelGapDip} DIP stack")
                    .That(gap).IsGreaterOrEqualThan(TrialLabelGapDip - 1e-6);
            }
            finally
            {
                window.Close();
            }
        }
    }

    // Mutation (X5): CornerToTrialLabelGapDip -100 (the trial label through the digit) -> the
    //   intersection and the gap check both go red.

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>The digit's laid-out box in scene DIP, from its own character rects.</summary>
    private static Rect ContentBoxInScene(TextBlock label, Visual scene)
    {
        var start = label.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var end = label.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        var origin = label.TransformToAncestor(scene).Transform(new Point(0, 0));

        double left = Math.Min(start.Left, end.Left);
        double right = Math.Max(start.Right, end.Right);
        double top = Math.Min(start.Top, end.Top);
        double bottom = Math.Max(start.Bottom, end.Bottom);
        return new Rect(origin.X + left, origin.Y + top, right - left, bottom - top);
    }

    /// <summary>The D3 grown hit area of a target in scene DIP (the usability tests' rule).</summary>
    private static Rect HitRectInScene(SubPixelProbeWindow window, string id)
    {
        var element = window.TargetElementForTest(id)!;
        double margin = id == SubPixelProbeViewModel.EndTestTargetId
            ? SubPixelProbeWindow.EndButtonHitMarginDip
            : SubPixelProbeWindow.ButtonHitMarginDip;
        var topLeft = element.TransformToAncestor((Visual)window.Content!).Transform(new Point(0, 0));
        return new Rect(
            topLeft.X - margin, topLeft.Y - margin,
            element.ActualWidth + 2 * margin, element.ActualHeight + 2 * margin);
    }

    /// <summary>The distance between two rects in DIP; 0 when they intersect.</summary>
    private static double RectDistance(Rect a, Rect b)
    {
        double dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        double dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static SubPixelProbeViewModel NewViewModel(TimeProvider? timeProvider = null) => new(
        new WeakReferenceMessenger(),
        timeProvider ?? new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
        SubPixelAbSchedule.Create(seed: 7, new PhysicalRect(0, 0, 1920, 1080)),
        new EyeCtrlSettings().Dwell,
        () => true);

    private static void Feed(SubPixelProbeViewModel vm, PhysicalPoint position, long timestampUs) =>
        vm.Receive(new GazeCursorMovedMessage(position, timestampUs));

    /// <summary>The element's laid-out box in scene DIP (the layout box, not the content rects).</summary>
    private static Rect RectInScene(FrameworkElement element, Visual scene)
    {
        var topLeft = element.TransformToAncestor(scene).Transform(new Point(0, 0));
        return new Rect(topLeft, element.RenderSize);
    }

    /// <summary>The first TextBlock in the subtree whose text matches exactly, or null.</summary>
    private static TextBlock? FindTextBlock(DependencyObject node, string text)
    {
        if (node is TextBlock tb && tb.Text == text) return tb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindTextBlock(VisualTreeHelper.GetChild(node, i), text);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>Every non-blank TextBlock text in the subtree, for a failure's diagnostic.</summary>
    private static IEnumerable<string> AllVisibleTexts(DependencyObject node)
    {
        if (node is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) yield return tb.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var text in AllVisibleTexts(VisualTreeHelper.GetChild(node, i)))
                yield return text;
    }

    private static SubPixelProbeWindow NewWindow(
        SubPixelProbeViewModel vm, double scale, double widthDip, double heightDip, double ringDip)
    {
        var window = new SubPixelProbeWindow(
            null, vm, new CursorSettings { CircleSize = ringDip }, () => { }, viewFault: () => { }, scale);
        ProbeWindowTestHarness.EnsureHandleAt(window, widthDip, heightDip);
        return window;
    }

    /// <summary>Drives the view to a phase headlessly through C5's observable Phase (SP-3 F8).</summary>
    private static void SetPhase(SubPixelProbeViewModel vm, SubPixelProbePhase phase) =>
        typeof(SubPixelProbeViewModel)
            .GetProperty(nameof(SubPixelProbeViewModel.Phase), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(vm, phase);
}
