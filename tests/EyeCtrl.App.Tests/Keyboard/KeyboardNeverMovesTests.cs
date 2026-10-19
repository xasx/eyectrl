namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
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
/// happens at the next show. Size changes while open (sub-layout switch, prediction bar,
/// settings re-layout) move nothing. Every move that still happens (yield, Flip) arms the D13
/// suppression, so nothing dwells until the gaze travels about half a key.
///
/// <para>
/// Driven through <see cref="KeyboardWindow.Place"/> — the exact placement rule
/// <c>RepositionOnScreen</c> calls with <c>SystemParameters.WorkArea</c> and
/// <c>ActualWidth</c>/<c>ActualHeight</c> — because the brief requires headless runs: the real
/// <c>Show()</c> path lands the topmost overlay on the owner's screen, and the app-launching
/// tests are opt-in for that reason. <c>Window.Top</c> is real state even on an unshown window
/// (the RestartNoticeWindow placement tests use the same seam).
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
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Show);
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
    public void ReShow_TakesTheUnYieldedPosition()
    {
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Show);
            window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            window.SetToolbarOccupancy(false, "none", 0);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(YieldedBottomTop);

            // The next show is where an un-yield is allowed.
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Show);
            Check.That(window.Top).IsEqualTo(BottomTop);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SizeChange_WhileOpen_MovesNothing_ButTheFirstLayoutPlaces()
    {
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Show);
            Check.That(window.Top).IsEqualTo(BottomTop);

            // A taller sub-layout / prediction bar must not shift the keys under the gaze.
            window.Place(WorkArea, 1200, 600, KeyboardMove.Keep);
            Check.That(window.Top).IsEqualTo(BottomTop);
        }
        finally
        {
            window.Close();
        }

        var fresh = NewWindow();
        try
        {
            fresh.SetScreenEdge("bottom");
            fresh.Place(WorkArea, 1200, 600, KeyboardMove.Keep);
            Check.WithCustomMessage("the first layout of a visibility session must still place")
                .That(fresh.Top).IsEqualTo(1040 - 600);
        }
        finally
        {
            fresh.Close();
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

    private sealed record Probe(KeyboardWindow Window, KeyboardViewModel Vm, HwndSource Source)
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
    private static Probe Open()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new KeyboardWindow();
        window.SetViewModel(vm);
        window.SetScreenEdge("bottom");
        window.LoadLayout(SyntheticGrid(), 1.0, keyDwellMs: KeyDwellMs,
            predictionDwellMs: 800, cooldownMs: 0);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Show);

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

        var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: PointToScreen would throw and the key centres would never "
                + "publish, so the suppression assertions would pass vacuously.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
        Check.WithCustomMessage("No key centres collected — the probe would measure nothing.")
            .That(Centres(window)).Not.IsEmpty();

        return new Probe(window, vm, source);
    }

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;
}
