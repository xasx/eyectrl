namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// C1: once open, the keyboard keeps its position until it closes. A toolbar that appears may
/// make it yield (move away); a toolbar that hides must NOT make it move back — that is the
/// 280 px jump under a resting gaze that re-mapped the gaze onto another key. The un-yield
/// happens at the next show. A size change re-anchors the DOCKED edge (so a grown 5-row
/// function layout cannot run off the work area) while keeping the applied yield; a settings
/// reload that repeats the same edge is not a Flip. Every move that still happens (yield, Flip,
/// docked-edge re-anchor) arms the D13 suppression, so nothing dwells until the gaze travels
/// about half a key.
///
/// <para>
/// Driven through <see cref="KeyboardWindow.Place"/> / <see cref="KeyboardWindow.PlaceForShow"/>
/// — the exact placement rules <c>RepositionOnScreen</c> calls with
/// <c>SystemParameters.WorkArea</c> and <c>ActualWidth</c>/<c>ActualHeight</c> — because the
/// brief requires headless runs: the real <c>Show()</c> path lands the topmost overlay on the
/// owner's screen, and the app-launching tests are opt-in for that reason. <c>Window.Top</c> is
/// real state even on an unshown window (the RestartNoticeWindow placement tests use the same
/// seam); the F1 size tests measure the shipped layout's real <c>DesiredSize</c> in a
/// never-visible <c>HwndSource</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardNeverMovesTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private const double WidthDip = 1000;
    private const double HeightDip = 400;
    private const double ToolbarExtentDip = 132;
    private const int KeyDwellMs = 400;
    private const int WsPopup = unchecked((int)0x80000000);

    private static KeyboardWindow NewWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return new KeyboardWindow();
    }

    // Bottom-docked Top: 1040 - 400 = 640. The yielded Top: 640 - 132 = 508.
    private const double BottomTop = 640;
    private const double YieldedBottomTop = 508;

    [StaFact]
    public void ToolbarHides_WhileOpen_DoesNotUnYield()
    {
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            Check.That(window.Top).IsEqualTo(BottomTop);

            // The toolbar appears on the keyboard's edge: one yield, allowed.
            window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(YieldedBottomTop);

            // The toolbar hides (auto-hide is the reported incident): the keyboard must stay
            // where it yielded, NOT jump back while the gaze may be resting on a key.
            window.SetToolbarOccupancy(false, "none", 0);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(YieldedBottomTop);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ReShow_TakesTheUnYieldedPosition_ThroughTheShowPlacement()
    {
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);          // production show path
            Check.That(window.Top).IsEqualTo(BottomTop);

            window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            window.SetToolbarOccupancy(false, "none", 0);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(YieldedBottomTop);

            // The next show is where an un-yield is allowed. This drives the same
            // PlaceForShow the coordinator calls after Show(); deleting its session reset
            // leaves this red (it would keep the yielded Top).
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            Check.That(window.Top).IsEqualTo(BottomTop);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void BottomDock_GrowingToFunction_ReAnchorsTheBottomEdge()
    {
        // F1: the reviewer measured qwertz letters→function 394→468 DIP on a 1040 DIP work
        // area; a fixed Top pushes the whole Fn bottom row 74 DIP past the work area. Use the
        // REAL shipped layout and its measured content size (off-screen HwndSource, no Show).
        using var probe = OpenShipped();
        var letters = MeasureContent(probe);
        probe.Window.SetScreenEdge("bottom");
        // The first layout of a session arrives as a Keep (the window's SizeChanged).
        probe.Window.Place(WorkArea, letters.Width, letters.Height, KeyboardMove.Keep);
        Check.That(probe.Window.Top + letters.Height).IsEqualTo(WorkArea.Bottom);

        probe.Vm.SwitchSubLayout("function");
        var function = MeasureContent(probe);
        Check.WithCustomMessage("the premise: letters→function grows taller")
            .That(function.Height).IsStrictlyGreaterThan(letters.Height);

        probe.Window.Place(WorkArea, function.Width, function.Height, KeyboardMove.Keep);

        Check.WithCustomMessage("the grown keyboard ran past the work area's bottom edge")
            .That(probe.Window.Top + function.Height <= WorkArea.Bottom).IsTrue();
        Check.That(probe.Window.Top + function.Height).IsEqualTo(WorkArea.Bottom);
        Check.That(probe.Window.Top).IsEqualTo(WorkArea.Bottom - function.Height);
    }

    [StaFact]
    public void YieldedBottomDock_KeepsItsYieldThroughAGrowth()
    {
        // F1: re-anchoring must not become an un-yield. The yielded bottom edge stays put while
        // the keyboard grows toward the screen centre.
        using var probe = OpenShipped();
        var letters = MeasureContent(probe);
        probe.Window.SetScreenEdge("bottom");
        probe.Window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
        probe.Window.PlaceForShow(WorkArea, letters.Width, letters.Height);
        Check.That(probe.Window.Top + letters.Height)
            .IsEqualTo(WorkArea.Bottom - ToolbarExtentDip);

        probe.Vm.SwitchSubLayout("function");
        var function = MeasureContent(probe);
        probe.Window.Place(WorkArea, function.Width, function.Height, KeyboardMove.Keep);

        Check.WithCustomMessage("the growth lost the applied yield (un-yielded while open)")
            .That(probe.Window.Top + function.Height)
            .IsEqualTo(WorkArea.Bottom - ToolbarExtentDip);
    }

    [StaFact]
    public void TopDock_Growth_LeavesTheTopEdgeFixed()
    {
        using var probe = OpenShipped();
        var letters = MeasureContent(probe);
        probe.Window.SetScreenEdge("top");
        probe.Window.PlaceForShow(WorkArea, letters.Width, letters.Height);
        Check.That(probe.Window.Top).IsEqualTo(WorkArea.Top);

        probe.Vm.SwitchSubLayout("function");
        var function = MeasureContent(probe);
        probe.Window.Place(WorkArea, function.Width, function.Height, KeyboardMove.Keep);

        Check.That(probe.Window.Top).IsEqualTo(WorkArea.Top);
    }

    [StaFact]
    public void SameEdgeReload_IsNotAFlip_AndKeepsTheYield()
    {
        // F3: LoadKeyboard re-sends the unchanged edge on every settings reload. Only a real
        // edge change may Flip (move + un-yield) an open keyboard.
        Check.That(KeyboardWindow.MoveForEdgeChange("Bottom", "bottom")).IsEqualTo(KeyboardMove.Keep);
        Check.That(KeyboardWindow.MoveForEdgeChange("bottom", "Top")).IsEqualTo(KeyboardMove.Flip);

        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            window.SetToolbarOccupancy(false, "none", 0);
            Check.That(window.Top).IsEqualTo(YieldedBottomTop);

            // A reload whose edge did not change: Keep, so the applied yield survives.
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Keep);
            Check.WithCustomMessage("a same-edge reload un-yielded the open keyboard")
                .That(window.Top).IsEqualTo(YieldedBottomTop);

            // A real edge change is still a Flip.
            window.SetScreenEdge("top");
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Flip);
            Check.That(window.Top).IsEqualTo(WorkArea.Top);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void SetScreenEdge_WiresTheEdgeDecisionIntoThePlacement()
    {
        // F3's wiring half: the pure decision is above, and this pin keeps SetScreenEdge from
        // going back to an unconditional Flip (which would un-yield on every settings reload).
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Overlays", "KeyboardWindow.xaml.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "SetScreenEdge no longer routes through MoveForEdgeChange: a settings reload "
                + "(same edge) would Flip and un-yield an open keyboard.")
            .That(source.Contains(
                "varmove=MoveForEdgeChange(_screenEdge,edge);_screenEdge=edge;RepositionOnScreen(move);"))
            .IsTrue();
    }

    [StaFact]
    public void DpiChanged_ArmsTheSuppression_RestingGazeFiresNothing()
    {
        // F5: the DpiChanged subscription itself is the seam. Raise the real routed event on the
        // window (no display needed) and then hold the same screen point.
        using var probe = Open();
        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);

        var centre = Centres(probe.Window)["key_0_0"];
        probe.Window.UpdateGaze(centre, 0);

        // The event args' constructor is internal in WPF; build them reflectively and raise the
        // public routed event so the window's real subscription runs (M6's seam).
        var ctor = typeof(DpiChangedEventArgs)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 4);
        var args = (DpiChangedEventArgs)ctor.Invoke(new object[]
        {
            new DpiScale(1, 1), new DpiScale(1.5, 1.5), Window.DpiChangedEvent, probe.Window,
        });
        probe.Window.RaiseEvent(args);

        for (long t = 50_000; t < 2_050_000; t += 50_000)
        {
            probe.Window.UpdateGaze(centre, t);
            Check.WithCustomMessage("a key fired after a DPI change with the gaze resting")
                .That(fired).IsEmpty();
        }
    }

    [StaFact]
    public void Yield_ArmsTheSuppression_RestingGazeFiresNothing_UntilItTravels()
    {
        using var probe = Open();
        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);

        var centre = Centres(probe.Window)["key_0_0"];
        probe.Window.UpdateGaze(centre, 0);   // the gaze rests on a key

        var top = probe.Window.Top;
        probe.Window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
        probe.Window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        Check.That(probe.Window.Top).IsEqualTo(top - ToolbarExtentDip);

        // Hold the same SCREEN point far past the key dwell: nothing may fire.
        for (long t = 50_000; t < 2_050_000; t += 50_000)
        {
            probe.Window.UpdateGaze(centre, t);
            Check.WithCustomMessage("the key that landed under the resting gaze fired")
                .That(fired).IsEmpty();
        }

        // About 0.8 of a key away (pitch ~74 px at this scale): suppression clears, keys fire.
        var moved = new PhysicalPoint(centre.X + 60, centre.Y);
        for (long t = 2_100_000; t < 4_000_000 && fired.Count == 0; t += 50_000)
            probe.Window.UpdateGaze(moved, t);

        Check.That(fired).Not.IsEmpty();
    }

    [StaFact]
    public void Flip_ArmsTheSuppression_RestingGazeFiresNothing_UntilItTravels()
    {
        using var probe = Open();
        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);

        var centre = Centres(probe.Window)["key_0_0"];
        probe.Window.UpdateGaze(centre, 0);

        // The edge-bar Flip: the window moves to the other edge (a deliberate, allowed move).
        probe.Window.SetScreenEdge("top");
        probe.Window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Flip);
        Check.That(probe.Window.Top).IsEqualTo(0);

        for (long t = 50_000; t < 2_050_000; t += 50_000)
        {
            probe.Window.UpdateGaze(centre, t);
            Check.WithCustomMessage("the key that landed under the resting gaze after Flip fired")
                .That(fired).IsEmpty();
        }

        var moved = new PhysicalPoint(centre.X + 60, centre.Y);
        for (long t = 2_100_000; t < 4_000_000 && fired.Count == 0; t += 50_000)
            probe.Window.UpdateGaze(moved, t);

        Check.That(fired).Not.IsEmpty();
    }

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, HwndSource Source, FrameworkElement Content)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static KeyboardLayout SyntheticGrid()
    {
        var rows = new List<List<KeyDefinition>>();
        for (var r = 0; r < 3; r++)
        {
            var row = new List<KeyDefinition>();
            for (var c = 0; c < 10; c++)
                row.Add(new KeyDefinition { Label = $"{r}{c}", Output = $"{r}{c}", Width = 1.0 });
            rows.Add(row);
        }
        return new KeyboardLayout
        {
            Name = "Synthetic",
            DefaultSubLayout = "letters",
            SubLayouts = new Dictionary<string, SubLayout>
            {
                ["letters"] = new() { Label = "abc", Rows = rows },
            },
        };
    }

    /// <summary>
    /// A laid-out keyboard in an off-screen, never-visible <see cref="HwndSource"/> — the
    /// KeyboardGateProbeTests harness. <c>Window.Show()</c> is never called, so no topmost
    /// overlay reaches the owner's screen.
    /// </summary>
    private static Probe Open() => Open(SyntheticGrid());

    /// <summary>The real shipped QWERTZ layout, arranged for its real DIP size (F1's measured
    /// letters→function growth). No LayoutTransform: the size under test is the window's own DIP
    /// size, independent of the test machine's DPI.</summary>
    private static Probe OpenShipped()
    {
        var layout = KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));
        return Open(layout, scaleToDip: false);
    }

    private static Probe Open(KeyboardLayout layout, bool scaleToDip = true)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new KeyboardWindow();
        window.SetViewModel(vm);
        window.SetScreenEdge("bottom");
        window.LoadLayout(layout, 1.0, keyDwellMs: KeyDwellMs,
            predictionDwellMs: 800, cooldownMs: 0);
        window.PlaceForShow(WorkArea, WidthDip, HeightDip);

        var content = (FrameworkElement)window.Content;
        window.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-never-moves-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        if (scaleToDip)
        {
            var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
            content.LayoutTransform = new ScaleTransform(factor, factor);
        }

        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: PointToScreen would throw and the key centres would never "
                + "publish, so the suppression assertions would pass vacuously.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
        if (scaleToDip)
            Check.WithCustomMessage("No key centres collected — the probe would measure nothing.")
                .That(Centres(window)).Not.IsEmpty();

        return new Probe(window, vm, source, content);
    }

    /// <summary>Re-arranges the detached content and returns its DIP size, which is what a
    /// <c>SizeToContent</c> keyboard window would take.</summary>
    private static Size MeasureContent(Probe probe)
    {
        probe.Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        probe.Content.Arrange(new Rect(probe.Content.DesiredSize));
        probe.Content.UpdateLayout();
        return probe.Content.DesiredSize;
    }

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;
}
