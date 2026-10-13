namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Box-first targeting on the REAL window: <b>every point inside a key's box selects that key, and
/// every point outside all boxes selects exactly what the nearest-centre rule always selected.</b>
///
/// <para>
/// Driven through <see cref="KeyboardWindow.UpdateGaze"/> on the shipped QWERTZ letters page at
/// <c>KeySizeMultiplier</c> 1.5, hosted the way <see cref="KeyboardGateProbeTests"/> hosts it (an
/// off-screen <see cref="HwndSource"/>, never <c>Show()</c>; a root <see cref="ScaleTransform"/>
/// imposes the effective scale, NOT <c>DpiHelper</c>, which <c>PointToScreen</c> ignores). Scale 2.0
/// is this user's display; 1.25 catches a fix that special-cases integral scales.
/// </para>
///
/// <para><b>The oracle is independent of the production box.</b> Each key's expected box is
/// measured here from its Border — <c>PointToScreen</c> of both corners — and its width is checked
/// against <c>ActualWidth × scale</c>, so a DIP or mixed-unit box in production (the keyboard-gate
/// defect's shape) disagrees with the oracle instead of agreeing with itself.</para>
///
/// <para><b>Teeth.</b> The sweep asserts that some in-box points are NEARER another key's centre —
/// the cases nearest-centre gets wrong — so the suite cannot pass against nearest-centre (the
/// box test removed, or production no longer passing the boxes), and that gap points exist, so the
/// gap half cannot pass vacuously.</para>
///
/// <para><b>Reading the pick.</b> One sample per point, 200 ms apart: every sample is past the F3
/// gap bound (150 ms), so a same-key sample restarts its dwell and no key can ever activate and
/// null <c>NearestKeyId</c> mid-sweep. A changed key takes the NearestChanged branch. Either way
/// <c>NearestKeyId</c> is the pick for that point.</para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardBoxTargetingProbeTests
{
    private const double KeySizeMultiplier = 1.5;
    private const int WsPopup = unchecked((int)0x80000000);
    private const long SampleStepUs = 200_000;

    private static KeyboardLayout ShippedQwertz() =>
        KeyboardLayoutLoader.Load(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));

    private static T Field<T>(KeyboardWindow w, string name) =>
        (T)typeof(KeyboardWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        Field<Dictionary<string, PhysicalPoint>>(w, "_keyScreenPositions");

    private static Dictionary<string, PhysicalRect> ProductionBoxes(KeyboardWindow w) =>
        Field<Dictionary<string, PhysicalRect>>(w, "_keyScreenBoxes");

    private static Dictionary<string, (Border Container, TextBlock? Label)> Visuals(KeyboardWindow w) =>
        Field<Dictionary<string, (Border, TextBlock?)>>(w, "_keyVisuals");

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source, double Scale)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static Probe Open(double effectiveScale)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var w = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        w.SetViewModel(vm);
        w.LoadLayout(ShippedQwertz(), KeySizeMultiplier, keyDwellMs: 450, predictionDwellMs: 800, cooldownMs: 0);

        var content = (FrameworkElement)w.Content;
        w.Content = null;   // detach from the Window, which is never shown

        var source = new HwndSource(new HwndSourceParameters("kb-box-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var factor = effectiveScale / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;

        // SizeToContent="WidthAndHeight", as KeyboardGateProbeTests.Open explains.
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: PointToScreen would throw, every rect would be refused and "
                + "the sweep would measure nothing. Fix the fixture, never delete this guard.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        w.UpdateGaze(new PhysicalPoint(0, 0), 0);
        Check.That(Centres(w)).Not.IsEmpty();
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");

        return new Probe(w, vm, content, source, effectiveScale);
    }

    /// <summary>Each key's box measured by the TEST from its Border, in physical pixels.</summary>
    private static Dictionary<string, PhysicalRect> OracleBoxes(Probe probe)
    {
        var boxes = new Dictionary<string, PhysicalRect>();
        foreach (var (id, (container, _)) in Visuals(probe.Window))
        {
            var tl = container.PointToScreen(new Point(0, 0));
            var br = container.PointToScreen(new Point(container.ActualWidth, container.ActualHeight));
            var box = new PhysicalRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);

            // Physical, not DIP: the box is the Border's DIP extent times the effective scale.
            // Within 1.5 px, because layout rounding and PointToScreen snap each edge to a whole
            // device pixel (126.8 DIP at 2.0 measures 254 px, 159.5 DIP measures 320); a DIP or
            // mixed-unit box is off by 25–100 %, i.e. tens of pixels.
            Check.WithCustomMessage($"'{id}' box width {box.Width} != {container.ActualWidth} DIP × {probe.Scale}")
                 .That(Math.Abs(box.Width - container.ActualWidth * probe.Scale)).IsStrictlyLessThan(1.5);
            Check.WithCustomMessage($"'{id}' box height {box.Height} != {container.ActualHeight} DIP × {probe.Scale}")
                 .That(Math.Abs(box.Height - container.ActualHeight * probe.Scale)).IsStrictlyLessThan(1.5);
            boxes[id] = box;
        }
        return boxes;
    }

    /// <summary>Today's rule, verbatim: strict <c>&lt;</c> over the window's centres in their order.</summary>
    private static string? NearestCentre(Dictionary<string, PhysicalPoint> centres, PhysicalPoint p)
    {
        string? best = null;
        var bestDist = double.MaxValue;
        foreach (var (id, c) in centres)
        {
            var d = p.DistanceSquaredTo(c);
            if (d < bestDist)
            {
                bestDist = d;
                best = id;
            }
        }
        return best;
    }

    private static string? Pick(Probe probe, PhysicalPoint p, ref long ts)
    {
        ts += SampleStepUs;
        probe.Window.UpdateGaze(p, ts);
        return probe.Vm.NearestKeyId;
    }

    private static PhysicalRect KeyRowsRect(Probe probe)
    {
        var rows = Field<FrameworkElement>(probe.Window, "KeyRows");
        var tl = rows.PointToScreen(new Point(0, 0));
        var br = rows.PointToScreen(new Point(rows.ActualWidth, rows.ActualHeight));
        return new PhysicalRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
    }

    [StaTheory]
    [InlineData(1.25)]
    [InlineData(2.0)]   // this user's actual display scale
    public void ProductionBoxes_ArePhysicalPixels_AndMatchTheOracle(double effectiveScale)
    {
        var probe = Open(effectiveScale);
        try
        {
            var oracle = OracleBoxes(probe);
            var production = ProductionBoxes(probe.Window);
            var centres = Centres(probe.Window);

            Check.That(production.Keys).IsEquivalentTo(oracle.Keys);
            foreach (var (id, expected) in oracle)
            {
                var actual = production[id];
                Check.WithCustomMessage($"'{id}' production box {actual} != oracle {expected} at scale {effectiveScale}")
                     .That(Math.Abs(actual.X - expected.X) + Math.Abs(actual.Y - expected.Y)
                           + Math.Abs(actual.Width - expected.Width) + Math.Abs(actual.Height - expected.Height))
                     .IsStrictlyLessThan(0.01);
                // Centre and box come from the same transform. Within 1 px: PointToScreen returns
                // whole device pixels, so the midpoint of two rounded corners and the rounded
                // midpoint can differ by half a pixel on each axis.
                Check.WithCustomMessage($"'{id}' centre {centres[id]} is not its box's centre {expected.Center}")
                     .That(Math.Sqrt(centres[id].DistanceSquaredTo(expected.Center))).IsStrictlyLessThan(1.0);
            }
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// A sub-layout swap rebuilds the keys; the boxes must be the NEW layout's, with nothing left
    /// over from the old one (a box outliving its key is a hit region nobody can see).
    /// </summary>
    [StaFact]
    public void AfterASubLayoutSwap_TheBoxesAreExactlyTheNewLayouts()
    {
        var probe = Open(2.0);
        try
        {
            var lettersIds = ProductionBoxes(probe.Window).Keys.ToHashSet();
            probe.Vm.SwitchSubLayout("numbers");
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 1_000);

            var oracle = OracleBoxes(probe);
            var production = ProductionBoxes(probe.Window);
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("numbers");
            // The layouts differ, or this would prove nothing.
            Check.That(oracle.Keys.ToHashSet().SetEquals(lettersIds)).IsFalse();
            Check.That(production.Keys).IsEquivalentTo(oracle.Keys);
            foreach (var (id, expected) in oracle)
                Check.WithCustomMessage($"'{id}' box {production[id]} != oracle {expected} after the swap")
                     .That(Math.Abs(production[id].X - expected.X) + Math.Abs(production[id].Width - expected.Width))
                     .IsStrictlyLessThan(0.01);
        }
        finally { probe.Dispose(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// Every production box equals the test's own two-corner <c>PointToScreen</c> of its Border
    /// (within 0.01 px), and the key's centre lies inside it. Returns the oracle.
    /// </summary>
    private static Dictionary<string, PhysicalRect> AssertBoxesMatchBorders(Probe probe, string step)
    {
        var oracle = OracleBoxes(probe);
        var production = ProductionBoxes(probe.Window);
        var centres = Centres(probe.Window);
        Check.WithCustomMessage($"{step}: box ids differ from the visual tree's")
             .That(production.Keys).IsEquivalentTo(oracle.Keys);
        foreach (var (id, expected) in oracle)
        {
            var actual = production[id];
            Check.WithCustomMessage($"{step}: '{id}' box {actual} != {expected}")
                 .That(Math.Abs(actual.X - expected.X) + Math.Abs(actual.Y - expected.Y)
                       + Math.Abs(actual.Width - expected.Width) + Math.Abs(actual.Height - expected.Height))
                 .IsStrictlyLessThan(0.01);
            Check.WithCustomMessage($"{step}: '{id}' centre {centres[id]} is outside its box {actual}")
                 .That(actual.Contains(centres[id].X, centres[id].Y)).IsTrue();
        }
        return oracle;
    }

    /// <summary>
    /// The boxes are recomputed on EVERY sample, not only when the keys are rebuilt. The keyboard
    /// moves without a rebuild whenever the toolbar yield or the screen edge changes its position
    /// (<c>RepositionOnScreen</c>), and its scale changes with the monitor's DPI. Because pass 1
    /// OVERRIDES nearest-centre, a stale box does not just blur the pick — it wins, and a gaze on one
    /// key types whichever key used to sit there. Review-01 F1: a box computed once and never
    /// refreshed passed every other test.
    ///
    /// <para>The move is a <c>SetWindowPos</c> on the off-screen, never-visible popup; the scale
    /// change swaps the root <see cref="ScaleTransform"/> (the DPI-change proxy this fixture uses).
    /// Each step asserts the boxes actually moved, so a no-op step cannot pass vacuously.</para>
    /// </summary>
    [StaFact]
    public void BoxesFollowAMove_AndAScaleChange_WithoutARebuild()
    {
        var probe = Open(2.0);
        try
        {
            var initial = AssertBoxesMatchBorders(probe, "initial");

            Check.WithCustomMessage("SetWindowPos failed; the move step would test nothing.")
                 .That(SetWindowPos(probe.Source.Handle, IntPtr.Zero, -29000, -29500, 0, 0,
                     SwpNoSize | SwpNoZOrder | SwpNoActivate)).IsTrue();
            probe.Window.UpdateGaze(new PhysicalPoint(0, 0), 200_000);
            var moved = AssertBoxesMatchBorders(probe, "after move");
            Check.WithCustomMessage("The move did not move the keys — the step is vacuous.")
                 .That(Math.Abs(moved["modifier:win"].X - initial["modifier:win"].X)).IsStrictlyGreaterThan(500);

            var factor = 1.25 / probe.Source.CompositionTarget!.TransformToDevice.M11;
            probe.Content.LayoutTransform = new ScaleTransform(factor, factor);
            probe.Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            probe.Content.Arrange(new Rect(probe.Content.DesiredSize));
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(0, 0), 400_000);
            var rescaled = AssertBoxesMatchBorders(probe with { Scale = 1.25 }, "after scale change");
            Check.WithCustomMessage("The scale change did not resize the keys — the step is vacuous.")
                 .That(rescaled["modifier:win"].Width).IsStrictlyLessThan(moved["modifier:win"].Width * 0.7);
        }
        finally { probe.Dispose(); }
    }

    /// <summary>The in-box grid points (7×5 per key, edges included) whose nearest centre is ANOTHER key's.</summary>
    private static List<(string Owner, PhysicalPoint Point)> TeethPoints(Probe probe)
    {
        var centres = new Dictionary<string, PhysicalPoint>(Centres(probe.Window));
        var teeth = new List<(string, PhysicalPoint)>();
        foreach (var (id, box) in OracleBoxes(probe))
            for (var i = 0; i <= 6; i++)
            for (var j = 0; j <= 4; j++)
            {
                var p = new PhysicalPoint(box.X + box.Width * i / 6.0, box.Y + box.Height * j / 4.0);
                if (NearestCentre(centres, p) != id)
                    teeth.Add((id, p));
            }
        return teeth;
    }

    /// <summary>
    /// Entering a key from the UNLATCHED state — the common real path: <c>NearestKeyId</c> is
    /// nulled after every activation, cooldown, gaze-left and D13 rest. The first sample inside a
    /// key's box but nearer a neighbour's centre must latch the box's key, not the neighbour.
    /// Every such grid point is entered fresh: an off-grid sample first (GazeLeft → unlatched),
    /// then the point. Review-01 F2: "boxes ignored while nothing is latched" survived the App suite.
    /// </summary>
    [StaTheory]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void UnlatchedEntry_InsideABoxNearerANeighbour_LatchesTheBoxOwner(double effectiveScale)
    {
        var probe = Open(effectiveScale);
        try
        {
            var teeth = TeethPoints(probe);
            Check.WithCustomMessage("No in-box point is nearer another centre — the test has no teeth.")
                 .That(teeth).Not.IsEmpty();

            var wrong = new List<string>();
            long ts = 0;
            foreach (var (owner, p) in teeth)
            {
                Pick(probe, new PhysicalPoint(-99999, -99999), ref ts);
                Check.That(probe.Vm.NearestKeyId).IsNull();
                var picked = Pick(probe, p, ref ts);
                if (picked != owner)
                    wrong.Add($"{owner}@({p.X:F1},{p.Y:F1})→{picked ?? "null"}");
            }

            Check.WithCustomMessage(
                    $"{wrong.Count} of {teeth.Count} unlatched entries at scale {effectiveScale} latched "
                    + "the neighbour: " + string.Join("; ", wrong.Take(20)))
                .That(wrong).IsEmpty();
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// The same entry straight after a real ACTIVATION, which is what nulls the latch most often:
    /// a letter is dwelled to completion (a continuous 50 ms stream, so F3 does not restart it),
    /// and the very next sample lands inside the Caps key's box at a point nearer a neighbour's
    /// centre. Caps must latch.
    /// </summary>
    [StaFact]
    public void AfterAnActivation_TheFirstSampleInsideCapsNearItsEdge_LatchesCaps()
    {
        var probe = Open(2.0);
        try
        {
            var capsTooth = TeethPoints(probe).First(t => t.Owner == "toggle:capslock").Point;
            var fired = new List<string>();
            probe.Vm.KeyDwellCompleted += fired.Add;

            var centres = new Dictionary<string, PhysicalPoint>(Centres(probe.Window));
            var letter = centres["key_0_1"];   // Q
            long ts = 1_000_000;
            for (var t = ts; t <= ts + 450_000; t += 50_000)
                probe.Window.UpdateGaze(letter, t);

            Check.WithCustomMessage("The letter never activated, so the entry below is not post-activation.")
                 .That(fired).ContainsExactly("key_0_1");
            Check.That(probe.Vm.NearestKeyId).IsNull();

            probe.Window.UpdateGaze(capsTooth, ts + 500_000);
            Check.WithCustomMessage($"The first sample after an activation, inside Caps at ({capsTooth.X:F1},"
                    + $"{capsTooth.Y:F1}), latched '{probe.Vm.NearestKeyId}'.")
                 .That(probe.Vm.NearestKeyId).IsEqualTo("toggle:capslock");
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// Every key, a 7×5 grid over its box INCLUDING the edges and corners (the edges are inclusive):
    /// each point picks that key. The grid spacing is relative to the key, so a wide key is sampled
    /// across its whole width — exactly where nearest-centre hands its ends to the neighbours.
    /// </summary>
    [StaTheory]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void EveryPointInsideAKeyBox_SelectsThatKey(double effectiveScale)
    {
        var probe = Open(effectiveScale);
        try
        {
            var oracle = OracleBoxes(probe);
            var centres = new Dictionary<string, PhysicalPoint>(Centres(probe.Window));
            var wrong = new List<string>();
            var nearestCentreWouldMiss = 0;
            var points = 0;
            long ts = 0;

            foreach (var (id, box) in oracle)
            {
                for (var i = 0; i <= 6; i++)
                for (var j = 0; j <= 4; j++)
                {
                    var p = new PhysicalPoint(box.X + box.Width * i / 6.0, box.Y + box.Height * j / 4.0);
                    points++;
                    if (NearestCentre(centres, p) != id)
                        nearestCentreWouldMiss++;
                    var picked = Pick(probe, p, ref ts);
                    if (picked != id)
                        wrong.Add($"{id}@({p.X:F1},{p.Y:F1})→{picked ?? "null"}");
                }
            }

            Check.WithCustomMessage(
                    $"{wrong.Count} of {points} in-box points at scale {effectiveScale} picked another key: "
                    + string.Join("; ", wrong.Take(20)))
                .That(wrong).IsEmpty();
            Check.WithCustomMessage(
                    "No sampled in-box point is nearer another key's centre, so this test cannot tell "
                    + "box-first from nearest-centre — it would pass against the old rule.")
                .That(nearestCentreWouldMiss).IsStrictlyGreaterThan(0);
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// A sweep of the whole KeyRows rect — gutters, row indents and the band around the rows: every
    /// point outside all boxes still selects a key, and EXACTLY the key today's nearest-centre loop
    /// selects (pass 2 is the old rule verbatim); every point inside a box selects its owner.
    /// </summary>
    [StaTheory]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void KeyRowsSweep_InBoxPicksTheOwner_GapsPickExactlyTheNearestCentre(double effectiveScale)
    {
        var probe = Open(effectiveScale);
        try
        {
            var oracle = OracleBoxes(probe);
            var centres = new Dictionary<string, PhysicalPoint>(Centres(probe.Window));
            var rect = KeyRowsRect(probe);
            // ~3 DIP steps: finer than the 4 DIP gutter, so every gutter is crossed by samples.
            var step = 3.0 * effectiveScale;
            var wrong = new List<string>();
            int gapPoints = 0, boxPoints = 0, ownerNotNearest = 0;
            long ts = 0;

            for (var y = rect.Y + 0.5; y < rect.Y + rect.Height; y += step)
            for (var x = rect.X + 0.5; x < rect.X + rect.Width; x += step)
            {
                var p = new PhysicalPoint(x, y);
                var owner = oracle.Where(kv => kv.Value.Contains(x, y)).Select(kv => kv.Key).ToList();
                Check.WithCustomMessage($"({x:F1},{y:F1}) is inside {owner.Count} boxes; keys must not overlap")
                     .That(owner.Count).IsStrictlyLessThan(2);

                var nearest = NearestCentre(centres, p);
                string? expected;
                if (owner.Count == 1)
                {
                    boxPoints++;
                    expected = owner[0];
                    if (nearest != expected)
                        ownerNotNearest++;
                }
                else
                {
                    gapPoints++;
                    expected = nearest;
                }

                var picked = Pick(probe, p, ref ts);
                if (picked is null || picked != expected)
                    wrong.Add($"({x:F1},{y:F1}) {(owner.Count == 1 ? "box" : "gap")} expected {expected} got {picked ?? "null"}");
            }

            Check.WithCustomMessage(
                    $"{wrong.Count} of {gapPoints + boxPoints} sweep points at scale {effectiveScale} "
                    + "picked the wrong key: " + string.Join("; ", wrong.Take(20)))
                .That(wrong).IsEmpty();
            Check.WithCustomMessage("The sweep crossed no gap — the gap half would be vacuous.")
                 .That(gapPoints).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage("No in-box sweep point disagreed with nearest-centre — no teeth.")
                 .That(ownerNotNearest).IsStrictlyGreaterThan(0);
        }
        finally { probe.Dispose(); }
    }
}
