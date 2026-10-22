namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
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
/// A window MOVE (a toolbar yield, a Flip, a DPI change) arms the dwell suppression at the gaze's resting
/// point, but never disturbs a live swap arm (R1-c fixup 02). "Live" used to mean "not yet cleared", and
/// the anchor is only cleared by an in-grid sample outside its radius: the key cooldown and an off-grid gaze
/// skip that check. So a swap, a gaze that travels more than half a key during the cooldown, and a move kept
/// the STALE anchor and gave the new resting point none: the key under it fired. The live anchor is kept
/// only while the gaze is still inside it (O9).
///
/// <para>
/// Never calls <c>Window.Show()</c>: the shipped layouts are hosted in an off-screen, never-visible
/// <c>HwndSource</c>, and placement is driven through <c>Place</c> / <c>PlaceForShow</c> with an explicit
/// work area (a real <c>SizeChanged</c> never fires there, so the tests call <c>Place(Keep)</c> where
/// production's handler would).
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardStaleSwapAnchorTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int DwellMs = 450;
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);

    private sealed record Rig(KeyboardWindow W, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source) : IDisposable
    {
        public void Dispose() { Source.Dispose(); W.Close(); }
        public Size Measure()
        {
            Content.InvalidateMeasure();
            Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Content.Arrange(new Rect(Content.DesiredSize));
            Content.UpdateLayout();
            return Content.DesiredSize;
        }
    }

    private static Rig Open(string layoutFile, int cooldownMs = 0)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var root = SourceScanner.RepoRoot().FullName;
        var layout = KeyboardLayoutLoader.Load(Path.Combine(root, "src", "EyeCtrl.App", "Resources", "Layouts", layoutFile));
        var w = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        w.SetViewModel(vm);
        w.LoadLayout(layout, 1.0, keyDwellMs: DwellMs, predictionDwellMs: 800, cooldownMs: cooldownMs);
        w.SetSettings(new EyeCtrlSettings());
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        var source = new HwndSource(new HwndSourceParameters("kb-stale-swap-anchor")
        {
            WindowStyle = WsPopup, PositionX = -30000, PositionY = -30000, Width = 1, Height = 1,
        });
        source.RootVisual = content;
        var rig = new Rig(w, vm, content, source);
        rig.Measure();
        Check.That(PresentationSource.FromVisual(content)).IsNotNull();
        w.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
        Check.That(Centres(w)).Not.IsEmpty();
        return rig;
    }

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;

    /// <summary>
    /// A window move (toolbar yield) inside the post-activation cooldown after a swap, with the
    /// gaze already moved to a far key B. The cooldown skips the suppression check, so the swap's
    /// anchor A is still "live"; the non-destructive move arm keeps A instead of anchoring at B.
    /// </summary>
    [StaTheory]
    [InlineData(400)]
    public void YieldInsideTheSwapCooldown_GazeMovedAway_RestingGazeFiresNothing(int cooldownMs)
    {
        using var rig = Open("qwertz.json", cooldownMs);
        var before = rig.Measure();
        rig.W.SetScreenEdge("bottom");
        rig.W.PlaceForShow(WorkArea, before.Width, before.Height);

        var fn = Centres(rig.W)["switchSub:function"];
        long ts = 1_000;
        for (; ts < 3_000_000 && rig.Vm.ActiveSubLayoutId != "function"; ts += 20_000)
            rig.W.UpdateGaze(fn, ts);
        Check.That(rig.Vm.ActiveSubLayoutId).IsEqualTo("function");
        var swapTs = ts;
        var after = rig.Measure();
        rig.W.Place(WorkArea, after.Width, after.Height, KeyboardMove.Keep);

        // Gaze jumps to a far key of the NEW grid while the cooldown is still running.
        rig.W.UpdateGaze(new PhysicalPoint(-99999, -99999), ts);   // repopulate centres
        var c = Centres(rig.W);
        var b = c["key:F1"];
        ts += 20_000;
        rig.W.UpdateGaze(b, ts);           // inside the grid, inside the cooldown
        ts += 20_000;
        rig.W.UpdateGaze(b, ts);

        var fired = new List<string>();
        rig.Vm.KeyDwellCompleted += id => fired.Add(id);

        // The toolbar appears: a yield moves the window under the resting gaze at B.
        rig.W.SetToolbarOccupancyState(true, "bottom", 132);
        var topBefore = rig.W.Top;
        rig.W.Place(WorkArea, after.Width, after.Height, KeyboardMove.Yield);
        var inCooldown = ts - swapTs < cooldownMs * 1000L;

        for (long t = ts + 20_000; t < ts + 2_000_000; t += 20_000)
            rig.W.UpdateGaze(b, t);

        Check.That(inCooldown).IsTrue();
        Check.That(rig.W.Top).IsNotEqualTo(topBefore);
        Check.WithCustomMessage($"a key fired under the resting gaze after a yield inside the cooldown: [{string.Join(", ", fired)}]")
            .That(fired).IsEmpty();
    }
    /// <summary>
    /// The backlogged order — a queued gaze sample is processed AFTER the swap's rebuild but
    /// BEFORE the layout pass (Normal beats Render), so the centre dictionary is repopulated from
    /// unarranged containers; then SizeChanged → Keep runs the move arm.
    /// </summary>
    [StaTheory]
    [InlineData("qwertz.json", "function")]
    [InlineData("qwerty.json", "numbers")]
    public void SizeChangingSwap_BackloggedSampleBeforeLayout_RestingGazeFiresNothing(string layoutFile, string target)
    {
        using var rig = Open(layoutFile);
        var before = rig.Measure();
        rig.W.SetScreenEdge("bottom");
        rig.W.PlaceForShow(WorkArea, before.Width, before.Height);
        var p = Centres(rig.W)["switchSub:" + target];
        long ts = 1_000;
        for (; ts < 3_000_000 && rig.Vm.ActiveSubLayoutId != target; ts += 20_000)
            rig.W.UpdateGaze(p, ts);
        Check.That(rig.Vm.ActiveSubLayoutId).IsEqualTo(target);

        var fired = new List<string>();
        rig.Vm.KeyDwellCompleted += id => fired.Add(id);

        rig.W.UpdateGaze(p, ts); ts += 20_000;      // the queued sample, before any layout
        var distinct = new HashSet<PhysicalPoint>(Centres(rig.W).Values).Count;
        var after = rig.Measure();
        rig.W.Place(WorkArea, after.Width, after.Height, KeyboardMove.Keep);
        for (long t = ts; t < ts + 2_000_000; t += 20_000)
            rig.W.UpdateGaze(p, t);

        Check.That(fired).IsEmpty();
    }
}
