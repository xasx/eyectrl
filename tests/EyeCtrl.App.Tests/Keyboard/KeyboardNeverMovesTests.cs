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
/// C1: once open, the keyboard never moves while the gaze rests on it — or on the ground the move
/// would cover. A toolbar that appears may make it yield (move away); a toolbar that hides must
/// NOT make it move back AT THAT MOMENT — that is the 280 px jump under a resting gaze that
/// re-mapped the gaze onto another key. Owner decision 24.10: the keyboard moves back once the
/// gaze is off it, through <see cref="KeyboardMove.UnYield"/> and only after the gaze has been
/// continuously outside the window PLUS the band it would move into for
/// <see cref="KeyboardUnYieldGate.OffThresholdUs"/>; the next show also un-yields. A size change
/// re-anchors the DOCKED edge (so a grown 5-row function layout cannot run off the work area)
/// while keeping the applied yield; a settings reload that repeats the same edge is not a Flip.
/// Yield, Flip and the docked-edge re-anchor arm D13 at the pre-move gaze; the gaze-off un-yield
/// cannot (its gate requires the gaze to be off the grid), so it arms a ONE-SHOT landing anchor at
/// the first sample that returns to the moved grid. Either way nothing dwells until the gaze
/// travels about half a key.
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
    public void UnYield_BottomDock_MovesBackToTheEdge()
    {
        // Owner decision 24.10: with the toolbar gone, the gaze-off gate's UnYield gives the
        // applied yield back. The placement itself is the unit here; the gate is separate.
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(YieldedBottomTop);

            window.SetToolbarOccupancy(false, "none", 0);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.UnYield);
            Check.That(window.Top).IsEqualTo(BottomTop);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void UnYield_TopDock_MovesBackToTheEdge()
    {
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("top");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            Check.That(window.Top).IsEqualTo(WorkArea.Top);

            window.SetToolbarOccupancy(true, "top", ToolbarExtentDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(WorkArea.Top + ToolbarExtentDip);

            window.SetToolbarOccupancy(false, "none", 0);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.UnYield);
            Check.That(window.Top).IsEqualTo(WorkArea.Top);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void UnYield_WithASmallerRemainingToolbar_IsPartial()
    {
        // The toolbar shrank while still on the edge: the keyboard gives back only the difference
        // — it must not leave the smaller toolbar overlapping the keys.
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(YieldedBottomTop);

            window.SetToolbarOccupancy(true, "bottom", 60);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.UnYield);
            Check.That(window.Top).IsEqualTo(BottomTop - 60);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void UnYield_NeverYieldsFurther()
    {
        // A bigger toolbar appears without its own Yield placement: UnYield must not act on a
        // LARGER current yield. Yielding further is a Yield's job, and the gaze is off the keys
        // when UnYield arrives.
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            window.SetToolbarOccupancy(true, "bottom", 60);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(BottomTop - 60);

            window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.UnYield);
            Check.WithCustomMessage("UnYield yielded further toward a larger toolbar")
                .That(window.Top).IsEqualTo(BottomTop - 60);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void UnYield_WhenAlreadyUnYielded_DoesNothing()
    {
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.UnYield);
            Check.That(window.Top).IsEqualTo(BottomTop);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The wiring half of the gaze-off un-yield (owner decision 24.10): real gaze samples through
    /// the real <see cref="KeyboardWindow.UpdateGaze"/> entry point. The window is placed with a
    /// toolbar yield, the toolbar hides, and the keyboard must move back only after 300 ms of
    /// CONTINUOUS samples outside the whole window — never while the gaze is on a key, and never
    /// on the strength of an off-run that an inside sample interrupted.
    /// </summary>
    [StaFact]
    public void GazeOffTheWindow_UnYieldsOnlyAfterTheOffTime_AGazeOnTheKeysNeverDoes()
    {
        using var probe = Open();
        var window = probe.Window;
        var contentRect = ContentScreenRect(probe);
        window.UnYieldProbeGeometry = new KeyboardWindow.UnYieldGeometry(
            WorkArea, WidthDip, HeightDip, contentRect, PixelsPerDip: 1.0);

        window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        Check.That(window.Top).IsEqualTo(YieldedBottomTop);

        // The reported incident: the toolbar auto-hides. The Yield path itself must not move the
        // keyboard back at that moment (ToolbarHides_WhileOpen_DoesNotUnYield pins the placement).
        window.SetToolbarOccupancy(false, "none", 0);
        Check.That(window.Top).IsEqualTo(YieldedBottomTop);

        var keyCentre = Centres(window)["key_0_0"];
        var offWindow = new PhysicalPoint(contentRect.X - 500, contentRect.Y - 500);

        // 250 ms off the window: under the 300 ms threshold, nothing may move.
        for (long t = 1_000_000; t <= 1_250_000; t += 50_000)
        {
            window.UpdateGaze(offWindow, t);
            Check.WithCustomMessage("the keyboard un-yielded before 300 ms off the window")
                .That(window.Top).IsEqualTo(YieldedBottomTop);
        }

        // A gaze kept ON the keys for 550 ms (it types, but must never move the window), which
        // also resets the 250 ms off-run above.
        for (long t = 1_300_000; t <= 1_850_000; t += 50_000)
        {
            window.UpdateGaze(keyCentre, t);
            Check.WithCustomMessage("a gaze resting on a key un-yielded the keyboard")
                .That(window.Top).IsEqualTo(YieldedBottomTop);
        }

        // Off again. If the inside samples had not reset the run, this first sample would already
        // pass the threshold; a fresh run needs its own 300 ms.
        var offAgainStart = 1_900_000;
        for (long t = offAgainStart; t < offAgainStart + KeyboardUnYieldGate.OffThresholdUs; t += 50_000)
        {
            window.UpdateGaze(offWindow, t);
            Check.WithCustomMessage(
                    "the interrupted off-run was credited: the keyboard moved before a full "
                    + "300 ms of continuous off-window samples")
                .That(window.Top).IsEqualTo(YieldedBottomTop);
        }

        // 300 ms of continuous off-window samples: the un-yield.
        window.UpdateGaze(offWindow, offAgainStart + KeyboardUnYieldGate.OffThresholdUs);
        Check.WithCustomMessage("the keyboard did not un-yield after 300 ms off the window")
            .That(window.Top).IsEqualTo(BottomTop);
    }

    [StaFact]
    public void GazeInTheVacatedBand_DoesNotUnYield_AtTheBottomDock()
        => AssertGazeInTheBandDoesNotUnYield("bottom");

    [StaFact]
    public void GazeInTheVacatedBand_DoesNotUnYield_AtTheTopDock()
        => AssertGazeInTheBandDoesNotUnYield("top");

    /// <summary>
    /// Review 01 F1, the inverted probe: the band between the docked edge and the yielded keyboard
    /// is outside the CURRENT rect but inside the one the keyboard would move into. A gaze resting
    /// there must not un-yield — that is exactly the report's "keyboard jumped under the gaze"
    /// case, and the landing fired a key 462 ms later before the keep-clear rect.
    /// </summary>
    private static void AssertGazeInTheBandDoesNotUnYield(string edge)
    {
        using var probe = Open();
        var window = probe.Window;
        var contentRect = ContentScreenRect(probe);
        window.UnYieldProbeGeometry = new KeyboardWindow.UnYieldGeometry(
            WorkArea, WidthDip, HeightDip, contentRect, PixelsPerDip: 1.0);

        window.SetScreenEdge(edge);
        window.PlaceForShow(WorkArea, WidthDip, HeightDip);
        window.SetToolbarOccupancy(true, edge, ToolbarExtentDip);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        var yieldedTop = edge == "top"
            ? WorkArea.Top + ToolbarExtentDip : BottomTop - ToolbarExtentDip;
        Check.That(window.Top).IsEqualTo(yieldedTop);

        // The toolbar auto-hides: the Yield path itself must not move the keyboard.
        window.SetToolbarOccupancy(false, "none", 0);
        Check.That(window.Top).IsEqualTo(yieldedTop);

        // 20 px inside the 132 px band the keyboard would move into (the toolbar's old strip).
        var band = edge == "top"
            ? new PhysicalPoint(contentRect.X + 10, contentRect.Y - 20)
            : new PhysicalPoint(contentRect.X + 10, contentRect.Y + contentRect.Height + 20);

        for (long t = 1_000_000; t <= 2_000_000; t += 50_000)
        {
            window.UpdateGaze(band, t);
            Check.WithCustomMessage(
                    $"a gaze resting in the {edge}-dock landing band un-yielded the keyboard onto it")
                .That(window.Top).IsEqualTo(yieldedTop);
        }
    }

    [StaFact]
    public void GazeBeyondTheBand_StillUnYieldsAfter300Ms()
    {
        using var probe = Open();
        var window = probe.Window;
        var contentRect = ContentScreenRect(probe);
        window.UnYieldProbeGeometry = new KeyboardWindow.UnYieldGeometry(
            WorkArea, WidthDip, HeightDip, contentRect, PixelsPerDip: 1.0);

        window.SetScreenEdge("bottom");
        window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        window.SetToolbarOccupancy(false, "none", 0);

        // 200 px beyond the window: past the 132 px keep-clear band, so fully clear of both rects.
        var clear = new PhysicalPoint(contentRect.X + 10, contentRect.Y + contentRect.Height + 200);
        const long start = 1_000_000;
        for (long t = start; t < start + KeyboardUnYieldGate.OffThresholdUs; t += 50_000)
        {
            window.UpdateGaze(clear, t);
            Check.WithCustomMessage("the keyboard un-yielded before the off time was complete")
                .That(window.Top).IsEqualTo(YieldedBottomTop);
        }

        window.UpdateGaze(clear, start + KeyboardUnYieldGate.OffThresholdUs);
        Check.WithCustomMessage("the keyboard did not un-yield with the gaze fully clear")
            .That(window.Top).IsEqualTo(BottomTop);
    }

    /// <summary>
    /// Review 01 F1b: the un-yield arms a ONE-SHOT landing anchor at the first sample that reaches
    /// the key area after the move, so a gaze that returns onto the shifted grid does not dwell
    /// while it rests. Travelling beyond half a key clears it, and it is NOT re-armed: typing then
    /// works normally. Without the arm, the landing fires a key at the key dwell.
    /// </summary>
    [StaFact]
    public void LandingAfterAnUnYield_DoesNotTypeUntilTheGazeTravels()
    {
        using var probe = Open();
        var window = probe.Window;
        var contentRect = ContentScreenRect(probe);
        window.UnYieldProbeGeometry = new KeyboardWindow.UnYieldGeometry(
            WorkArea, WidthDip, HeightDip, contentRect, PixelsPerDip: 1.0);

        window.SetScreenEdge("bottom");
        window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        window.SetToolbarOccupancy(false, "none", 0);

        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);
        var keyCentre = Centres(window)["key_0_0"];
        var clear = new PhysicalPoint(contentRect.X + 10, contentRect.Y + contentRect.Height + 200);

        long t = 1_000_000;
        for (; t <= 1_300_000; t += 50_000)
            window.UpdateGaze(clear, t);
        Check.That(window.Top).IsEqualTo(BottomTop);   // the premise: the un-yield moved the window

        // Land on a key and hold far past the 400 ms key dwell: the landing anchor must suppress.
        for (; t <= 2_500_000; t += 50_000)
        {
            window.UpdateGaze(keyCentre, t);
            Check.WithCustomMessage("a key fired on the landing sample after the un-yield")
                .That(fired).IsEmpty();
        }

        // More than half a key away (the anchor's radius) and back: normal typing resumes, which
        // also proves the anchor was consumed once instead of re-arming at every sample.
        var away = new PhysicalPoint(keyCentre.X + 120, keyCentre.Y);
        for (; t <= 3_000_000; t += 50_000)
            window.UpdateGaze(away, t);
        for (var hold = t; hold <= t + 800_000; hold += 50_000)
            window.UpdateGaze(keyCentre, hold);

        Check.WithCustomMessage("the keyboard never typed again after the landing anchor cleared")
            .That(fired).Contains("key_0_0");
    }

    /// <summary>
    /// Fixup 02 R1 (owner decision): the pending landing expires 1000 ms after the move. A gaze
    /// that only comes back to the key area after that has seen the moved grid, so it must type
    /// normally rather than hit an unexplained dead key.
    /// </summary>
    [StaFact]
    public void GazeReturnsAfterTheLandingGuardWindow_TypesNormally()
    {
        using var probe = Open();
        var window = probe.Window;
        var contentRect = ContentScreenRect(probe);
        window.UnYieldProbeGeometry = new KeyboardWindow.UnYieldGeometry(
            WorkArea, WidthDip, HeightDip, contentRect, PixelsPerDip: 1.0);

        window.SetScreenEdge("bottom");
        window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        window.SetToolbarOccupancy(false, "none", 0);

        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);
        var keyCentre = Centres(window)["key_0_0"];
        var clear = new PhysicalPoint(contentRect.X + 10, contentRect.Y + contentRect.Height + 200);

        // The un-yield fires at 1.3 s; keep the gaze off past the 1 s guard.
        long t = 1_000_000;
        for (; t <= 1_300_000; t += 50_000)
            window.UpdateGaze(clear, t);
        Check.That(window.Top).IsEqualTo(BottomTop);   // the premise: the un-yield moved the window
        for (; t <= 2_350_000; t += 50_000)
            window.UpdateGaze(clear, t);

        // The FIRST key-area sample is at 2.4 s — 1.1 s after the move: no landing anchor.
        for (; t <= 3_400_000 && fired.Count == 0; t += 50_000)
            window.UpdateGaze(keyCentre, t);

        Check.WithCustomMessage("a return after the guard window was still suppressed")
            .That(fired).Contains("key_0_0");
    }

    /// <summary>
    /// Fixup 02 R1 (owner decision): the landing guard is switchable. With it off, an un-yield arms
    /// no anchor at all, so a quick return types after the normal dwell — the un-yield itself is
    /// unchanged.
    /// </summary>
    [StaFact]
    public void LandingGuardOff_AShortLandingTypesNormally()
    {
        using var probe = Open();
        var window = probe.Window;
        var settings = new EyeCtrlSettings();
        settings.Keyboard.LandingGuardAfterUnYield = false;
        window.SetSettings(settings);
        var contentRect = ContentScreenRect(probe);
        window.UnYieldProbeGeometry = new KeyboardWindow.UnYieldGeometry(
            WorkArea, WidthDip, HeightDip, contentRect, PixelsPerDip: 1.0);

        window.SetScreenEdge("bottom");
        window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        window.SetToolbarOccupancy(false, "none", 0);

        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);
        var keyCentre = Centres(window)["key_0_0"];
        var clear = new PhysicalPoint(contentRect.X + 10, contentRect.Y + contentRect.Height + 200);

        long t = 1_000_000;
        for (; t <= 1_300_000; t += 50_000)
            window.UpdateGaze(clear, t);
        Check.That(window.Top).IsEqualTo(BottomTop);   // the premise: the un-yield moved the window

        // A 50 ms return, inside the guard window: with the setting off there is nothing to wait for.
        for (; t <= 2_500_000 && fired.Count == 0; t += 50_000)
            window.UpdateGaze(keyCentre, t);

        Check.WithCustomMessage("the landing guard was still armed with its setting off")
            .That(fired).Contains("key_0_0");
    }

    [Fact]
    public void UnYieldGate_ReadsTheWholeWindowRect_NotTheKeyGrid()
    {
        // Review 01 F3 / mutant M8: the gate's production read must be the WHOLE window (keys,
        // prediction bar, emoji tabs, chrome), never KeyRows. The wiring tests replace only the
        // geometry READS, so no headless run exercises this production line; the source pin is
        // what keeps it from drifting to the key grid.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Overlays", "KeyboardWindow.xaml.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("the gaze-off un-yield no longer reads the whole window rect")
            .That(source.Contains("!TryGetScreenRect(this,outvarwindowRect)")).IsTrue();
        Check.WithCustomMessage("the gaze-off un-yield measures the key grid instead of the window")
            .That(source.Contains("TryGetScreenRect(KeyRows,outvarwindowRect)")).IsFalse();
        // Review 02 R2 / N1: the DIP yield must be converted by the window's own px/DIP ratio. A
        // "1.0" shortcut halves the keep-clear band at the owner's 200 % and no headless seam can
        // see it (the seam supplies the ratio), so the production line is pinned here.
        Check.WithCustomMessage("the gaze-off un-yield no longer converts DIP with the window's px/DIP")
            .That(source.Contains("KeyboardUnYieldGate.PixelsPerDip(windowRect,ActualHeight)")).IsTrue();
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

            // A show is an un-yield (the other one is the gaze-off gate while open). This drives
            // the same PlaceForShow the coordinator calls after Show(); deleting its session
            // reset leaves this red (it would keep the yielded Top).
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
        // R2-2: a width change must re-centre horizontally (N6).
        Check.WithCustomMessage("the keyboard is not horizontally centred after the width change")
            .That(probe.Window.Left + function.Width / 2).IsEqualTo(WorkArea.Width / 2);
    }

    [StaFact]
    public void Yield_OnlyGrowsWhileOpen_ASmallerExtentDoesNotPartiallyUnYield()
    {
        // R2-3: the toolbar's extent can shrink while it is still visible. A smaller non-zero
        // yield must NOT move the keyboard back under the gaze (N1b).
        var window = NewWindow();
        try
        {
            window.SetScreenEdge("bottom");
            window.PlaceForShow(WorkArea, WidthDip, HeightDip);
            window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);   // 132
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.That(window.Top).IsEqualTo(BottomTop - ToolbarExtentDip);

            window.SetToolbarOccupancy(true, "bottom", 60);                 // smaller, non-zero
            window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
            Check.WithCustomMessage("a smaller non-zero yield partially un-yielded the open keyboard")
                .That(window.Top).IsEqualTo(BottomTop - ToolbarExtentDip);
        }
        finally
        {
            window.Close();
        }
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
    public void SwapThenKeep_QwertzLettersToFunction_RestingGazeFiresNothing()
    {
        // R2-1 (the reviewer's measured regression): the swap arms D13, RebuildKeys empties the
        // key-position dictionary, then the size change's Keep moves the window. The window-move
        // arm must be non-destructive: a destructive re-arm takes the "no centres" path and
        // NULLS the swap's anchor, and the resting gaze types the key that landed under it
        // (measured: `space` x4 on qwertz).
        using var probe = OpenShippedKeys("qwertz");
        AssertSwapThenKeepFiresNothing(probe, "switchSub:function", "function");
    }

    [StaFact]
    public void SwapThenKeep_QwertyLettersToNumbers_RestingGazeFiresNothing()
    {
        // The width-only variant: the Keep re-centres horizontally (Left changes, Top does not).
        // A destructive arm here lets the ABC key fire (a switchSub bounce back to letters).
        using var probe = OpenShippedKeys("qwerty");
        AssertSwapThenKeepFiresNothing(probe, "switchSub:numbers", "numbers");
    }

    [StaFact]
    public void KeepWithoutASwap_StillArmsTheSuppression()
    {
        // The non-destructive arm must still arm when nothing is live (N5k: Keep moves without
        // arming). No swap here — a settings-reload-style resize moves the window under a
        // resting gaze, and nothing may fire.
        using var probe = Open();
        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);

        var centre = Centres(probe.Window)["key_0_0"];
        probe.Window.UpdateGaze(centre, 0);

        probe.Window.Place(WorkArea, WidthDip, 600, KeyboardMove.Keep);   // taller → Top moves

        for (long t = 50_000; t < 2_050_000; t += 50_000)
        {
            probe.Window.UpdateGaze(centre, t);
            Check.WithCustomMessage("a key fired after a Keep that moved the window")
                .That(fired).IsEmpty();
        }
    }

    private static void AssertSwapThenKeepFiresNothing(
        Probe probe, string switchKeyId, string expectedSubLayout)
    {
        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);
        var centre = Centres(probe.Window)[switchKeyId];

        long ts = 0;
        for (; ts < 2_000_000 && probe.Vm.ActiveSubLayoutId != expectedSubLayout; ts += 50_000)
            probe.Window.UpdateGaze(centre, ts);

        Check.WithCustomMessage($"the {switchKeyId} dwell did not reach {expectedSubLayout}")
            .That(probe.Vm.ActiveSubLayoutId).IsEqualTo(expectedSubLayout);
        Check.WithCustomMessage("the swap itself did not fire the switch key")
            .That(fired).Contains(switchKeyId);

        // The production order: the swap rebuilt the grid and emptied the key positions, the
        // layout pass re-arranges and raises SizeChanged → Keep with the new size.
        fired.Clear();
        var resized = MeasureContent(probe);
        probe.Window.Place(WorkArea, resized.Width, resized.Height, KeyboardMove.Keep);

        for (long t = ts + 50_000; t < ts + 2_050_000; t += 50_000)
        {
            probe.Window.UpdateGaze(centre, t);
            Check.WithCustomMessage(
                    "a key fired after the swap+Keep under the resting gaze — the move arm "
                    + "destroyed the swap's suppression")
                .That(fired).IsEmpty();
        }
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

    /// <summary>A shipped layout with its real key geometry, for the swap+Keep probes.</summary>
    private static Probe OpenShippedKeys(string layoutName)
    {
        var layout = KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", $"{layoutName}.json"));
        return Open(layout, scaleToDip: true);
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

    /// <summary>
    /// The probe content's rect in physical pixels — the window's whole on-screen surface (the
    /// content was lifted out of the never-shown Window, so this is what the gaze-off gate must
    /// judge "off the keyboard" against in the headless harness).
    /// </summary>
    private static PhysicalRect ContentScreenRect(Probe probe)
    {
        var topLeft = probe.Content.PointToScreen(new Point(0, 0));
        var bottomRight = probe.Content.PointToScreen(
            new Point(probe.Content.ActualWidth, probe.Content.ActualHeight));
        return new PhysicalRect(topLeft.X, topLeft.Y,
            bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }
}
