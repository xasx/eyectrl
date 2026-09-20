namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// The emoji category bar and the emoji grid are two dwell surfaces driven from one gaze point,
/// evaluated one statement apart in <see cref="KeyboardWindow.UpdateGaze"/> — and until this
/// ticket only the grid half of the pair was partitioned. The category tabs' hit areas are circles
/// of each tab's half-diagonal (<c>EmojiGridManager.AddCategoryTab</c>) and the bar sits directly
/// above the grid, so the top band of the first emoji row lies inside the tab circles above it.
/// <b>Measured</b> (fixture scale 1.0, adaptation margin 0, worst pair <c>cat_symbols</c> vs
/// <c>emoji_8</c>): to the cell centres the clearance is <b>19.75 / 30.50 / 51.01 px</b> at
/// <c>KeySizeMultiplier</c> 0.5 / 1.0 / 2.0 — the ticket's 30.5 is the 1.0 number, and reading it
/// as "the circle misses the cell" is what hid the defect. Against the <c>KeyRows</c> rect edge,
/// which is the boundary that makes BOTH detectors live (<c>ProcessKeyGaze</c> latches the nearest
/// key for any point inside that rect), the circles fall short by <b>0.25 px</b> at 0.5 and cross
/// it by <b>6.50 px</b> at 1.0 and <b>20.99 px</b> at 2.0 — 49 px and 122 px wide chords. The
/// 2+6+2 DIP gutters between the bar and the grid are fixed while the half-diagonal radius scales,
/// so exposure grows proportionally worse with key size — worst at <c>KeySizeMultiplier</c> 2.0,
/// the same physical geometry a 200 % display produces at multiplier 1.0 (higher DPI scales the
/// radius and the distances together, so the multiplier is the only axis that moves one and not
/// the other) — the ticket's hint that 0.5 "wants re-checking" is inverted; 0.5 is the safe end.
/// A reach for a top-row emoji then
/// completes a category tab, whose handler rebuilds the grid and empties the key registry under
/// the live dwell. This is ticket 3's
/// defect family — its comment sits one statement above the category call that never received the
/// same treatment.
///
/// <para>
/// <b>Which test is the probe and which is a guard.</b> Only
/// <see cref="GridGaze_InsideACategoryHitCircle_NeverAccumulatesCategoryDwell"/> (with
/// <see cref="GridExcursion_MidDwell_DoesNotCreditTheOnGridInterval"/>, which pins the reset the
/// first one cannot distinguish from a bare skip) is a defect probe: red before the partition.
/// <see cref="CategoryBarGaze_NeverAccumulatesGridDwell"/> already
/// held before the partition — outside the <c>KeyRows</c> rect <c>ProcessKeyGaze</c> early-returns
/// after nulling its latch (<c>KeyboardViewModel.cs</c>), so the key grid was always partitioned
/// from the bar. It is a regression guard for the other half of the acceptance, not evidence of a
/// second fix. <see cref="CategoryHitCircles_NeverSwallowAnEmojiCellCentre"/> is a geometry census,
/// not a probe: the invariant it asserts holds before and after, and it exists so the clearance
/// the ticket asks for is measured at every shipped multiplier rather than assumed to scale;
/// <see cref="CategoryClearance_GrowsWithTheMultiplier_ItDoesNotScaleInvariantly"/> pins the shape
/// of that measurement (exposure grows with the multiplier — the absolute figures are diagnostic,
/// not asserted), and <see cref="CategoryAndGridDetectors_RunTheSameDwell"/> pins the
/// <c>P == K</c> premise of the tie-break argument recorded in <c>UpdateGaze</c>.
/// </para>
///
/// <para>
/// <b>Scale is the point, and it must not come from <c>DpiHelper</c>.</b> The probe runs at
/// <c>KeySizeMultiplier</c> 1.0 and 2.0, and the census adds 0.5. Ticket 12's diagnosis turned on a
/// geometry term that only misbehaves away from the fixture's default multiplier, and at 0.5 the
/// tab circles do not reach the grid at all, so the probe would be vacuous there — its overlap
/// guard says so instead of passing quietly. The multipliers are the settings guard's whole
/// validated range (<c>KeyboardSettings</c> 0.5..2.0). The multiplier scales both terms through
/// the one code path that builds them; injecting <c>DpiHelper</c> into this fixture instead would
/// double the radius and not the distances (it pins physical == DIP via <c>LayoutTransform</c>),
/// which is ticket 12's mismatch re-injected and would "prove" a defect the product does not have.
/// </para>
///
/// <para>
/// The fixture is the off-screen <c>WS_POPUP</c> HwndSource path: no <c>Show()</c>, no visible
/// window, CI-subset safe. The gaze point is derived from the app's own reported geometry (the
/// registered category targets and the cells' <c>PointToScreen</c> rects), never from constants,
/// and every measurement is at adaptation margin 0 (adaptation is off in this fixture's settings;
/// a routed margin could grow the tab radii).
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardCategoryGridExclusionTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int KeyDwellMs = 450;
    private const int PredictionDwellMs = 500;
    private const int CooldownMs = 400;

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static string EmojiJsonPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    private static Dictionary<string, PhysicalPoint> KeyCentres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, (Border Container, TextBlock? Label)> KeyVisuals(KeyboardWindow w) =>
        (Dictionary<string, (Border Container, TextBlock? Label)>)typeof(KeyboardWindow)
            .GetField("_keyVisuals", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    /// <summary>The category detector the window's emoji manager actually drives.</summary>
    private static DwellDetector CategoryDetector(KeyboardWindow w)
    {
        var manager = (EmojiGridManager)typeof(KeyboardWindow)
            .GetField("_emojiManager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;
        return (DwellDetector)typeof(EmojiGridManager)
            .GetField("_categoryDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
    }

    private static List<DwellTarget> CategoryTargets(DwellDetector detector) =>
        ((ConcurrentDictionary<string, DwellTarget>)typeof(DwellDetector)
            .GetField("_targets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(detector)!).Values.ToList();

    /// <summary>The element's on-screen rectangle in physical pixels, both corners through PointToScreen.</summary>
    private static PhysicalRect CellRect(FrameworkElement cell)
    {
        var topLeft = cell.PointToScreen(new Point(0, 0));
        var bottomRight = cell.PointToScreen(new Point(cell.ActualWidth, cell.ActualHeight));
        return new PhysicalRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private static bool IsInsideKeyArea(KeyboardWindow w, PhysicalPoint p)
    {
        var topLeft = w.KeyRows.PointToScreen(new Point(0, 0));
        var bottomRight = w.KeyRows.PointToScreen(new Point(w.KeyRows.ActualWidth, w.KeyRows.ActualHeight));
        return new PhysicalRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y)
            .Contains(p.X, p.Y);
    }

    /// <summary>
    /// A laid-out keyboard at the requested <c>KeySizeMultiplier</c>, with the shipped emoji data
    /// wired, hosted in an HwndSource that is never made visible.
    /// </summary>
    private static Probe Open(double keySizeMultiplier)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var emojiPath = EmojiJsonPath();
        Check.WithCustomMessage(
                $"The shipped emoji data is not at {emojiPath}, so the grid would never be built "
                + "and every assertion below would be vacuous.")
            .That(File.Exists(emojiPath)).IsTrue();

        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(ShippedQwerty(), keySizeMultiplier,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: CooldownMs);
        w.SetEmojiData(emojiPath,
            Path.Combine(Path.GetTempPath(), "eyectrl_category_grid_test_" + Guid.NewGuid().ToString("N") + ".json"));

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-category-grid-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: UpdateGaze's gate would catch PointToScreen's throw and "
                + "return false, and every assertion would pass VACUOUSLY. Fix the environment or "
                + "the fixture, never delete this guard.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        return new Probe(w, vm, content, source);
    }

    private static void Feed(Probe probe, PhysicalPoint at, long fromUs, long toUs, long stepUs = 30_000)
    {
        Check.WithCustomMessage(
                $"Feed({fromUs}..{toUs}) step {stepUs}: the endpoint is not reachable on the step "
                + "grid, so the final sample would be dropped and a completion at the endpoint "
                + "would silently never happen. Round the endpoint to the cadence.")
            .That((toUs - fromUs) % stepUs).IsEqualTo(0);

        for (var t = fromUs; t <= toUs; t += stepUs)
            probe.Window.UpdateGaze(at, t);
    }

    /// <summary>
    /// Enters emoji mode through the real activation path — dwelling the shipped layout's
    /// <c>switchEmoji</c> key — and refreshes the grid's and category bar's positions. Ticket 1's
    /// fixture trap applies to the caller's next read: after the layout rebuild no pass has run,
    /// so every new cell centre collapses to one unarranged point until <c>UpdateLayout</c>.
    /// </summary>
    private static void EnterEmojiMode(Probe probe)
    {
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

        Check.WithCustomMessage(
                "The shipped qwerty layout has no live switchEmoji key centre, so emoji mode "
                + "could not be entered and everything below would be vacuous.")
            .That(KeyCentres(probe.Window).ContainsKey("switchEmoji")).IsTrue();
        Feed(probe, KeyCentres(probe.Window)["switchEmoji"], 30_000, 480_000);
        Check.WithCustomMessage("Dwelling switchEmoji did not enter emoji mode.")
            .That(probe.Vm.IsEmojiMode).IsTrue();

        probe.Content.UpdateLayout();
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 510_000);
    }

    /// <summary>
    /// One category-tab circle's clearance from one emoji cell: to the cell's centre and to the
    /// cell's nearest edge, both measured from the circle's own boundary. Negative means the
    /// circle reaches past that point (penetrates the cell / swallows the centre).
    /// </summary>
    private sealed record Clearance(
        string TabId, string CellId, double ToCentre, double ToEdge,
        double Radius, PhysicalPoint TabCentre, PhysicalRect CellRect);

    private static List<Clearance> CategoryClearances(Probe probe)
    {
        var targets = CategoryTargets(CategoryDetector(probe.Window))
            .Where(t => t.Id.StartsWith("cat_", StringComparison.Ordinal))
            .ToList();
        var visuals = KeyVisuals(probe.Window);

        var result = new List<Clearance>();
        foreach (var target in targets)
        {
            foreach (var (id, (container, _)) in visuals)
            {
                if (!id.StartsWith("emoji_", StringComparison.Ordinal)) continue;
                var rect = CellRect(container);
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                double nearestX = Math.Clamp(target.Center.X, rect.X, rect.X + rect.Width);
                double nearestY = Math.Clamp(target.Center.Y, rect.Y, rect.Y + rect.Height);
                double edgeDistance = Math.Sqrt(
                    Math.Pow(target.Center.X - nearestX, 2) + Math.Pow(target.Center.Y - nearestY, 2));
                double centreDistance = Math.Sqrt(
                    Math.Pow(target.Center.X - rect.CenterX, 2) + Math.Pow(target.Center.Y - rect.CenterY, 2));

                result.Add(new Clearance(target.Id, id,
                    centreDistance - target.Radius, edgeDistance - target.Radius,
                    target.Radius, target.Center, rect));
            }
        }
        return result;
    }

    /// <summary>
    /// How far the worst category tab's hit circle reaches past the <c>KeyRows</c> rect edge — the
    /// boundary that makes BOTH detectors live, because <c>ProcessKeyGaze</c> latches the nearest
    /// key for any point inside that rect. Positive is the lens the partition must cover; this is
    /// the penetration the ticket's centre-to-centre 30.5 px does not describe.
    /// </summary>
    private sealed record RectPenetration(string TabId, double Radius, double DistanceToGrid, double Penetration);

    private static RectPenetration CategoryRectPenetration(Probe probe)
    {
        var topLeft = probe.Window.KeyRows.PointToScreen(new Point(0, 0));
        var bottomRight = probe.Window.KeyRows.PointToScreen(
            new Point(probe.Window.KeyRows.ActualWidth, probe.Window.KeyRows.ActualHeight));
        var grid = new PhysicalRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);

        return CategoryTargets(CategoryDetector(probe.Window))
            .Where(t => t.Id.StartsWith("cat_", StringComparison.Ordinal))
            .Select(t =>
            {
                double nearestX = Math.Clamp(t.Center.X, grid.X, grid.X + grid.Width);
                double nearestY = Math.Clamp(t.Center.Y, grid.Y, grid.Y + grid.Height);
                double distance = Math.Sqrt(
                    Math.Pow(t.Center.X - nearestX, 2) + Math.Pow(t.Center.Y - nearestY, 2));
                return new RectPenetration(t.Id, t.Radius, distance, t.Radius - distance);
            })
            .OrderByDescending(p => p.Penetration)
            .First();
    }

    /// <summary>A point that is inside the key/emoji grid AND inside a category tab's hit circle.</summary>
    private sealed record OverlapPoint(
        PhysicalPoint Point, string TabId, string CellId, double Overlap, double Radius);

    /// <summary>
    /// The deepest point on the grid that is inside a category hit circle, derived from the app's
    /// own registered targets and cell rects. The point is nudged half the penetration depth off
    /// the cell's edge toward its centre, so it is strictly inside both (moving toward the cell
    /// centre can raise the distance to the tab centre by at most that step, leaving
    /// radius − overlap/2 > 0). Null when no circle reaches the grid at this multiplier.
    /// </summary>
    private static OverlapPoint? FindGridPointInsideCategoryCircle(Probe probe)
    {
        OverlapPoint? best = null;
        foreach (var target in CategoryTargets(CategoryDetector(probe.Window)))
        {
            if (!target.Id.StartsWith("cat_", StringComparison.Ordinal)) continue;
            foreach (var (id, (container, _)) in KeyVisuals(probe.Window))
            {
                if (!id.StartsWith("emoji_", StringComparison.Ordinal)) continue;
                var rect = CellRect(container);
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                double nearestX = Math.Clamp(target.Center.X, rect.X, rect.X + rect.Width);
                double nearestY = Math.Clamp(target.Center.Y, rect.Y, rect.Y + rect.Height);
                double edgeDistance = Math.Sqrt(
                    Math.Pow(target.Center.X - nearestX, 2) + Math.Pow(target.Center.Y - nearestY, 2));
                if (edgeDistance >= target.Radius) continue;

                double overlap = target.Radius - edgeDistance;
                double toCentreX = rect.CenterX - nearestX, toCentreY = rect.CenterY - nearestY;
                double toCentreLength = Math.Sqrt(toCentreX * toCentreX + toCentreY * toCentreY);
                double step = Math.Min(overlap / 2, toCentreLength / 2);
                var point = toCentreLength < 1e-9
                    ? new PhysicalPoint(rect.CenterX, rect.CenterY)
                    : new PhysicalPoint(nearestX + toCentreX / toCentreLength * step,
                                        nearestY + toCentreY / toCentreLength * step);

                if (best is null || overlap > best.Overlap)
                    best = new OverlapPoint(point, target.Id, id, overlap, target.Radius);
            }
        }
        return best;
    }

    // ---- the defect probe: gaze inside the grid never accumulates category dwell ----

    /// <summary>
    /// The gaze point is inside the key area and inside the tab circle above it — the exact
    /// overlap that lets a reach for the top emoji row reach the category bar. It is deliberately
    /// not a cell centre: a centre is outside every tab circle at every shipped multiplier (the
    /// 1.0 centre clearance is 30.50 px), so a centre-based negative is green on the broken code
    /// and measures nothing. The hold runs for
    /// well past the category dwell: under the defect the tab completes first (it is evaluated one
    /// statement before the key tracker), rebuilds the grid and empties the key registry under the
    /// live cell dwell. The positive control at the end dwells the tab's own centre, proving the
    /// detector was alive while the negative assertion was made.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void GridGaze_InsideACategoryHitCircle_NeverAccumulatesCategoryDwell(double keySizeMultiplier)
    {
        var probe = Open(keySizeMultiplier);
        try
        {
            EnterEmojiMode(probe);
            var overlap = FindGridPointInsideCategoryCircle(probe);
            Check.WithCustomMessage(
                    $"At KeySizeMultiplier {keySizeMultiplier} no emoji cell point lies inside a "
                    + "category tab's hit circle, so this probe cannot exercise the defect at all — "
                    + "it would pass against the broken code for the wrong reason. The census in "
                    + "CategoryHitCircles_NeverSwallowAnEmojiCellCentre records the measured "
                    + "clearance; if the geometry changed, move this InlineData and say why.")
                .That(overlap).IsNotNull();
            var probePoint = overlap!;

            Check.WithCustomMessage(
                    $"The chosen point ({probePoint.Point.X:F1},{probePoint.Point.Y:F1}) is not "
                    + "inside the key area, so it would never reach the key tracker and the probe "
                    + "would measure nothing.")
                .That(IsInsideKeyArea(probe.Window, probePoint.Point)).IsTrue();
            var probeTarget = CategoryTargets(CategoryDetector(probe.Window))
                .Single(t => t.Id == probePoint.TabId);
            Check.WithCustomMessage(
                    "The chosen point is not inside the category target's own hit test, so the "
                    + "category detector would not consider it.")
                .That(probeTarget.HitTest(probePoint.Point)).IsTrue();
            Check.WithCustomMessage(
                    $"The chosen cell '{probePoint.CellId}' has no reported screen centre, so the "
                    + "cell dwell below would be vacuous.")
                .That(KeyCentres(probe.Window).ContainsKey(probePoint.CellId)).IsTrue();

            var detector = CategoryDetector(probe.Window);
            var progress = new List<(string Id, double Value)>();
            var activations = new List<string>();
            detector.TargetProgress += (id, p) => progress.Add((id, p));
            detector.TargetActivated += activations.Add;

            var cellCompletions = new List<string>();
            probe.Vm.KeyDwellCompleted += cellCompletions.Add;

            Feed(probe, probePoint.Point, 1_000_000, 1_600_000);
            var gridPhaseProgress = progress.ToList();

            Check.WithCustomMessage(
                    $"Gaze on the emoji grid accumulated category dwell. The point "
                    + $"({probePoint.Point.X:F1},{probePoint.Point.Y:F1}) is inside the key area AND "
                    + $"inside {probePoint.TabId}'s hit circle ({probePoint.Radius:F1} px radius, "
                    + $"{probePoint.Overlap:F1} px penetration into {probePoint.CellId}). Under the "
                    + $"defect the tab completes and rebuilds the grid under the live dwell. "
                    + $"Category progress seen: {string.Join(", ", gridPhaseProgress.Select(p => $"{p.Id}={p.Value:F2}"))}")
                .That(gridPhaseProgress.Where(p => p.Value > 0.0)).IsEmpty();
            Check.WithCustomMessage(
                    $"A category tab activated from inside the key/emoji grid: "
                    + $"{string.Join(", ", activations)}. Its handler calls BuildGrid, which clears "
                    + "the key registry — the mid-dwell destruction ticket 12 measured.")
                .That(activations).IsEmpty();

            // Grid-side positive control: the same hold must complete the cell it is on.
            Check.WithCustomMessage(
                    $"The cell under the gaze ({probePoint.CellId}) did not complete its dwell, so "
                    + "the hold was not processed as key gaze — or the grid registry was emptied "
                    + $"mid-dwell. Completed ids: "
                    + $"{(cellCompletions.Count == 0 ? "none" : string.Join(", ", cellCompletions))}. "
                    + "None means no key completed at all, which is also what a stealing category "
                    + "activation looks like; a different id means the wrong cell latched.")
                .That(cellCompletions.Count(id => id == probePoint.CellId)).IsEqualTo(1);

            // Category-side positive control: the detector must fire on its own tab's centre, or
            // the negative assertions above would be the vacuous readings of a dead detector.
            Check.WithCustomMessage("The category tab centre is inside the key area, so the control "
                    + "would not exercise the bar-reachable path.")
                .That(IsInsideKeyArea(probe.Window, probeTarget.Center)).IsFalse();
            Feed(probe, probeTarget.Center, 1_700_000, 2_300_000);
            Check.WithCustomMessage(
                    "The category detector never fired while dwelling the tab's own centre after the "
                    + "grid hold, so the 'no category dwell from the grid' assertion above would "
                    + "have been vacuous — it may have been reading a dead detector.")
                .That(activations).Contains(probePoint.TabId);
        }
        finally { probe.Dispose(); }
    }

    // ---- the reset half: a grid excursion restarts an in-progress tab dwell ----

    /// <summary>
    /// Pins the reset, not merely the skip. A category dwell is most of the way through (300 ms of
    /// 450), the gaze crosses onto the grid for a 90 ms on-grid interval — three fed samples, so
    /// the DETECTOR sees no sample for 120 ms between the last pre-excursion tab sample and the
    /// first return sample, under its 150 ms gap clamp, meaning a later off-grid sample would
    /// silently credit the interval if the detector were only skipped — and then returns to the
    /// tab for another 300 ms. Under the reset the dwell restarted on return (entry at +420 ms,
    /// needs +870 ms), so no activation may occur in the return window; a skip-without-reset
    /// fires at +450 ms, the first return samples, crediting the interval spent on the grid. That
    /// is the difference between "the grid cannot accumulate category dwell" and "the grid merely
    /// pauses it".
    ///
    /// <para>
    /// The teeth of this test are the 120 ms detector-visible gap, and that is why it is asserted
    /// rather than assumed: at or above the detector's <c>MaxCreditedSampleGapUs</c> the
    /// <c>Update</c> re-anchors <c>EnteredTimestamp</c> for free, a skip-without-reset passes, and
    /// this test silently stops measuring the reset. A one-sample cadence change is all it takes.
    /// </para>
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void GridExcursion_MidDwell_DoesNotCreditTheOnGridInterval(double keySizeMultiplier)
    {
        var probe = Open(keySizeMultiplier);
        try
        {
            EnterEmojiMode(probe);
            var overlap = FindGridPointInsideCategoryCircle(probe);
            Check.WithCustomMessage(
                    $"At KeySizeMultiplier {keySizeMultiplier} no grid point lies inside a category "
                    + "hit circle, so the excursion below would leave and re-enter the same single "
                    + "target and the test would not exercise the boundary it exists for.")
                .That(overlap).IsNotNull();
            var probePoint = overlap!;

            var detector = CategoryDetector(probe.Window);
            var activations = new List<string>();
            detector.TargetActivated += activations.Add;
            var tab = CategoryTargets(detector).Single(t => t.Id == probePoint.TabId);

            const long t0 = 1_000_000;
            const long stepUs = 30_000;
            const int preExcursionSamples = 10;   // 0..10 → 300 ms of tab dwell, below the 450 ms
            const int onGridSamples = 3;          // 330..390 ms: 3 samples, 60 ms of feed
            const int returnSamples = 10;         // 420..720 ms: no activation under the reset
            const int controlSamples = 10;        // keeps holding; the restarted dwell completes
            // DwellDetector.MaxCreditedSampleGapUs is private; its value is the 150 ms bound the
            // detector re-anchors at (>=). Cited so this test cannot drift from the mechanism.
            const long detectorGapClampUs = 150_000;

            long lastPreExcursionUs = t0 + preExcursionSamples * stepUs;
            long firstReturnUs = t0 + (preExcursionSamples + onGridSamples + 1) * stepUs;
            long detectorVisibleGapUs = firstReturnUs - lastPreExcursionUs;
            Check.WithCustomMessage(
                    $"The on-grid excursion presents a {detectorVisibleGapUs} us gap to the "
                    + $"detector, at or above its {detectorGapClampUs} us re-anchor clamp. At or "
                    + "above the clamp DwellDetector.Update re-anchors EnteredTimestamp for free, "
                    + "so a skip-without-reset restarts the tab even without ResetCategoryState and "
                    + "this test stops measuring the reset. Keep the gap strictly below the clamp.")
                .That(detectorVisibleGapUs).IsStrictlyLessThan(detectorGapClampUs);

            // 300 ms on the tab — below the 450 ms dwell, so no activation can fire yet.
            Feed(probe, tab.Center, t0, lastPreExcursionUs);
            Check.WithCustomMessage(
                    $"The tab activated after only {preExcursionSamples * stepUs / 1000} ms of a "
                    + "450 ms dwell, so the pre-excursion state this test depends on never existed.")
                .That(activations).IsEmpty();

            // Every grid sample is inside both the key area and the tab circle.
            Feed(probe, probePoint.Point,
                t0 + (preExcursionSamples + 1) * stepUs,
                t0 + (preExcursionSamples + onGridSamples) * stepUs);

            // Back on the tab. Under the reset no activation is possible (the dwell restarted at
            // firstReturnUs and needs 450 ms of continued gaze); under skip-without-reset the
            // original entry's elapsed crosses the dwell here and the tab fires.
            Feed(probe, tab.Center, firstReturnUs, firstReturnUs + returnSamples * stepUs);
            Check.WithCustomMessage(
                    $"The category tab activated after a {onGridSamples * stepUs / 1000} ms feed of "
                    + "excursion onto the emoji grid: the on-grid interval was credited to the tab "
                    + "dwell even though the gaze was inside the key area. The grid must reset "
                    + "category accumulation, not pause it (under the detector's gap clamp, the "
                    + "clamp will not re-anchor for you). "
                    + $"Activations: {string.Join(", ", activations)}.")
                .That(activations).IsEmpty();

            // Positive control: keep holding; the restarted dwell must still be able to complete.
            Feed(probe, tab.Center,
                firstReturnUs + (returnSamples + 1) * stepUs,
                firstReturnUs + (returnSamples + 1 + controlSamples) * stepUs);
            Check.WithCustomMessage(
                    "The tab never activated after the excursion even with a full dwell's worth of "
                    + "continued gaze, so the 'not credited' assertion above may have been reading "
                    + "a detector that could no longer fire at all.")
                .That(activations).Contains(probePoint.TabId);
        }
        finally { probe.Dispose(); }
    }

    // ---- regression guard, already green pre-fix: the bar does not accumulate grid dwell ----

    /// <summary>
    /// The other half of the acceptance — gaze on the category bar never accumulates key/emoji
    /// dwell — <b>already held before the partition</b>: outside the <c>KeyRows</c> rect the
    /// verdict passed to <c>ProcessKeyGaze</c> is false and it early-returns after nulling its
    /// latch. This is a regression guard for that half, NOT a defect probe; its green is not a
    /// second bug fixed. The dwell on the tab is the positive control: the tab itself must
    /// activate, proving the hold reached the category surface and the no-completion assertion is
    /// not reading an inert pipeline.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CategoryBarGaze_NeverAccumulatesGridDwell(double keySizeMultiplier)
    {
        var probe = Open(keySizeMultiplier);
        try
        {
            EnterEmojiMode(probe);
            var detector = CategoryDetector(probe.Window);
            var activations = new List<string>();
            detector.TargetActivated += activations.Add;
            var cellCompletions = new List<string>();
            probe.Vm.KeyDwellCompleted += cellCompletions.Add;

            var tab = CategoryTargets(detector).First(t => t.Id.StartsWith("cat_", StringComparison.Ordinal));
            Check.WithCustomMessage(
                    $"The chosen tab '{tab.Id}' has no real centre (radius {tab.Radius:F1}), so the "
                    + "hold would land nowhere and the guard would be vacuous.")
                .That(tab.Radius).IsStrictlyGreaterThan(0.0);
            Check.WithCustomMessage(
                    $"The chosen tab '{tab.Id}' reports a centre inside the key area, so this guard "
                    + "would not be about the category bar at all.")
                .That(IsInsideKeyArea(probe.Window, tab.Center)).IsFalse();

            Feed(probe, tab.Center, 1_000_000, 1_600_000);

            Check.WithCustomMessage(
                    "Gaze on the category bar accumulated key/emoji dwell. The key tracker is "
                    + "already partitioned by the same key-area verdict (KeyboardViewModel."
                    + "ProcessKeyGaze early-returns when isGazeInsideWindow is false), so this is "
                    + "a regression guard for that half of the acceptance — not a second fix. "
                    + $"Key completions: "
                    + $"{(cellCompletions.Count == 0 ? "none" : string.Join(", ", cellCompletions))}")
                .That(cellCompletions).IsEmpty();
            Check.WithCustomMessage(
                    $"The category tab '{tab.Id}' never activated during the hold, so the "
                    + "no-key-completion assertion above would be the reading of an inert pipeline.")
                .That(activations).Contains(tab.Id);
        }
        finally { probe.Dispose(); }
    }

    // ---- the measurement the ticket asks for: clearance at 0.5, 1.0 and 2.0 ----

    /// <summary>
    /// The census behind the partition: at every shipped multiplier, no category tab's hit circle
    /// reaches any emoji cell's <b>centre</b> — a user aiming at the middle of an emoji is never
    /// simultaneously dwelling a tab. The invariant holds before and after the fix (geometry is
    /// unchanged); its job is to keep the clearance measured and stated rather than assumed, and
    /// the failure message carries the live worst-pair numbers for both boundaries. At 0.5 the
    /// circle misses the grid entirely; at 1.0 and 2.0 it crosses the <c>KeyRows</c> edge and
    /// penetrates the cells' top band, which is exactly the overlap the defect probe uses.
    ///
    /// <para>
    /// The absolute figures are <b>diagnostic, not asserted</b>: a layout tweak can move them
    /// without changing the defect or the safe core (measured — changing the tab margin deepens
    /// every band and this stays green), so pinning them would be a ratchet that fires on honest
    /// layout work. The asserted properties are the safe core here and the monotonic shape in
    /// <see cref="CategoryClearance_GrowsWithTheMultiplier_ItDoesNotScaleInvariantly"/>.
    /// </para>
    /// </summary>
    [StaTheory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CategoryHitCircles_NeverSwallowAnEmojiCellCentre(double keySizeMultiplier)
    {
        var probe = Open(keySizeMultiplier);
        try
        {
            EnterEmojiMode(probe);
            var clearances = CategoryClearances(probe);
            Check.WithCustomMessage(
                    $"At KeySizeMultiplier {keySizeMultiplier} the census measured nothing — no "
                    + "category target or no emoji cell was registered.")
                .That(clearances).Not.IsEmpty();

            var worstToCentre = clearances.OrderBy(c => c.ToCentre).First();
            var worstToEdge = clearances.OrderBy(c => c.ToEdge).First();
            var rectPenetration = CategoryRectPenetration(probe);
            Check.WithCustomMessage(
                    $"At KeySizeMultiplier {keySizeMultiplier} a category hit circle reaches an emoji "
                    + $"cell's centre: {worstToCentre.TabId} vs {worstToCentre.CellId} at clearance "
                    + $"{worstToCentre.ToCentre:F2} px (negative = the centre is inside the {worstToCentre.Radius:F2} px "
                    + "radius circle), so a user aiming at the middle of an emoji would also be "
                    + $"dwelling the category tab. Measured worst edge clearance: "
                    + $"{worstToEdge.ToEdge:F2} px ({worstToEdge.TabId} vs {worstToEdge.CellId}, "
                    + $"cell {worstToEdge.CellRect.Width:F0}x{worstToEdge.CellRect.Height:F0}). "
                    + "Worst penetration past the KeyRows rect edge — the boundary that makes both "
                    + $"detectors live: {rectPenetration.Penetration:F2} px ({rectPenetration.TabId}, "
                    + $"radius {rectPenetration.Radius:F2}, tab centre to rect {rectPenetration.DistanceToGrid:F2}); "
                    + "positive means the circle crosses the edge into the grid.")
                .That(worstToCentre.ToCentre).IsStrictlyGreaterThan(0.0);
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// Acceptance criterion 2's shape, measured rather than assumed: the exposure does not scale
    /// invariantly. The tab circle's half-diagonal grows with the multiplier while the 2+6+2 DIP
    /// gutters between the bar and the grid do not, so both the rect penetration and the
    /// cell-centre clearance are strictly increasing in the multiplier — the ticket's steer that
    /// 0.5 "wants re-checking" is inverted. The absolute figures stay diagnostic (see the census);
    /// this pins only the ordering, which survives both honest directions of layout change: a
    /// deeper gutter shifts every value but cannot flatten the growth, and a gutter that removes
    /// the overlap makes the penetrations all negative and still ordered.
    /// </summary>
    [StaFact]
    public void CategoryClearance_GrowsWithTheMultiplier_ItDoesNotScaleInvariantly()
    {
        var measurements = new List<(double Multiplier, double RectPenetration, double MinCentreClearance)>();
        foreach (var multiplier in new[] { 0.5, 1.0, 2.0 })
        {
            var probe = Open(multiplier);
            try
            {
                EnterEmojiMode(probe);
                var clearances = CategoryClearances(probe);
                Check.WithCustomMessage(
                        $"At KeySizeMultiplier {multiplier} the census measured nothing — no "
                        + "category target or no emoji cell was registered, so the ordering below "
                        + "would be built from an empty measurement.")
                    .That(clearances).Not.IsEmpty();

                measurements.Add((multiplier,
                    CategoryRectPenetration(probe).Penetration,
                    clearances.Min(c => c.ToCentre)));
            }
            finally { probe.Dispose(); }
        }

        var measured = string.Join(", ",
            measurements.Select(m => $"m={m.Multiplier}: rectPenetration={m.RectPenetration:F2}px, "
                + $"minCentreClearance={m.MinCentreClearance:F2}px"));
        for (var i = 1; i < measurements.Count; i++)
        {
            Check.WithCustomMessage(
                    $"The category/grid exposure did not grow with KeySizeMultiplier: {measured}. "
                    + "Ticket 13's acceptance asked for the clearance to be measured rather than "
                    + "assumed to scale; with fixed DIP gutters and a scaling half-diagonal both "
                    + "quantities are increasing in the multiplier, and 0.5 is the safe end. If a "
                    + "geometry change made this fail, it most likely reduced the exposure rather "
                    + "than grew it — re-read the measurement before treating it as a regression.")
                .That(measurements[i].RectPenetration)
                .IsStrictlyGreaterThan(measurements[i - 1].RectPenetration);
            Check.WithCustomMessage(
                    $"The cell-centre clearance did not grow with KeySizeMultiplier: {measured}.")
                .That(measurements[i].MinCentreClearance)
                .IsStrictlyGreaterThan(measurements[i - 1].MinCentreClearance);
        }
    }

    /// <summary>
    /// The <c>P == K</c> premise of the tie-break argument recorded in
    /// <c>KeyboardWindow.UpdateGaze</c>: both surfaces run the same configured dwell, so a
    /// category activation from the grid is decided by statement order and not by a shorter
    /// clock. <c>LoadLayout</c> is what enforces it — <c>_viewModel.Configure(keyDwellMs, ...)</c>
    /// and <c>_emojiManager.SetKeyDwellMs(keyDwellMs)</c> one line apart — and nothing else
    /// asserts it: dropping the emoji half makes the category dwell 0 and trips
    /// <see cref="GridExcursion_MidDwell_DoesNotCreditTheOnGridInterval"/> on its own premise,
    /// which sends the next maintainer to that test's timings instead of here. Read from the
    /// live detectors, not from the constants.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CategoryAndGridDetectors_RunTheSameDwell(double keySizeMultiplier)
    {
        var probe = Open(keySizeMultiplier);
        try
        {
            EnterEmojiMode(probe);
            var categoryTarget = CategoryTargets(CategoryDetector(probe.Window))
                .First(t => t.Id.StartsWith("cat_", StringComparison.Ordinal));
            var gridDwellMs = (int)typeof(KeyboardViewModel)
                .GetField("_keyDwellMs", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(probe.Vm)!;

            Check.WithCustomMessage(
                    $"At KeySizeMultiplier {keySizeMultiplier} the category tab runs "
                    + $"{categoryTarget.DwellTimeMicroseconds} us while the key/emoji grid runs "
                    + $"{gridDwellMs} ms. UpdateGaze's tie-break argument — the category update is "
                    + "the earlier statement and takes every tie — presupposes both surfaces run "
                    + "the SAME configured dwell, which LoadLayout enforces by passing one "
                    + "keyDwellMs to Configure and SetKeyDwellMs. A mismatch means the tab and the "
                    + "grid are racing on different clocks.")
                .That(categoryTarget.DwellTimeMicroseconds)
                .IsEqualTo((long)gridDwellMs * MathConstants.MicrosecondsPerMillisecond);
        }
        finally { probe.Dispose(); }
    }
}
