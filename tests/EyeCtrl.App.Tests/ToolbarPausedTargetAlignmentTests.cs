namespace EyeCtrl.App.Tests;

using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The drawn toolbar face must sit on the dwell target published for it — in every pause state and
/// on every dock edge.
///
/// <para>
/// This is the regression for B1 on 921cd26b: the resume hint lived in the band's flow as a
/// Collapsed↔Visible element, so showing it moved every button ~162 px off a latched dwell target.
/// The visible Play button went dead and the visible SelectionMode button paused the app. The
/// targets are published once (<see cref="ToolbarWindow.UpdateGaze"/> → the VM's
/// <c>UpdateButtonScreenPosition</c>) and the coordinator's gaze route stops delivering after the
/// latch, so a layout shift is never repaired.
/// </para>
///
/// <para>
/// Geometry is verified at effective scale 2.0 — the user's 3840×2400 at 200 % — by hosting the
/// window's content in an off-screen <see cref="HwndSource"/> whose LayoutTransform makes device
/// pixels mean the 2.0 space (reviewer A's probe technique). The VM's hit radius is computed at
/// DpiHelper 1.0 from a 240-DIP setting, which is the same 120 physical px production publishes
/// for a 120-DIP button at ScaleX 2.0. Nothing is ever shown on the user's screen.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ToolbarPausedTargetAlignmentTests
{
    /// <summary>The production default button diameter, in DIP.</summary>
    private const double ButtonSizeDip = 120;

    /// <summary>
    /// Emulates ScaleX 2.0 for the VM: radius = setting/2 DIP → physical px at DpiHelper 1.0, so
    /// 240 gives the 120 px the user's 2.0 display publishes for a 120-DIP button.
    /// </summary>
    private const double RadiusEmulatingToolbarSizeDip = 240;

    private const double EffectiveScale = 2.0;

    /// <summary>
    /// How far a drawn face may move across the pause toggle. The correct shift is zero; this is
    /// slack for floating-point/layout noise, far below the sub-pixel claim.
    /// </summary>
    private const double FaceShiftTolerancePx = 0.5;

    /// <summary>Every button that renders in the band and therefore gets a dwell target.</summary>
    private static readonly string[] BandButtonIds =
    {
        "Pause", "SelectionMode", "Keyboard", "Settings", "TaskSwitcher", "Modes", "ResetAdaptation",
    };

    /// <summary>The user's work area in DIP: 3840×2400 physical at 200 %, generously rounded down.</summary>
    private static readonly Rect WorkAreaDip = new(0, 0, 1920, 1200);

    [StaTheory]
    [InlineData(false, "Top")]     // unpaused → the hint appears (runtime pause)
    [InlineData(false, "Bottom")]
    [InlineData(false, "Left")]
    [InlineData(false, "Right")]
    [InlineData(true, "Top")]      // paused → the hint disappears (first resume after --paused)
    [InlineData(true, "Bottom")]
    [InlineData(true, "Left")]
    [InlineData(true, "Right")]
    public void DrawnFacesStayOnTheirDwellTargets_AcrossPauseToggle(bool startPaused, string edge)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        // The assembly's DpiHelperRestore resets this after the test. Pinned to 1.0 so the radius
        // emulation above is exact regardless of the monitor the suite runs on.
        DpiHelper.Initialize(1.0, 1.0);

        var window = new ToolbarWindow();
        window.Initialize(ButtonSizeDip, 700);
        window.SetMode(isDocked: true, edge: edge);

        var settings = new EyeCtrlSettings();
        settings.Toolbar.Size = RadiusEmulatingToolbarSizeDip;
        var vm = new ToolbarViewModel(settings, new WeakReferenceMessenger());
        window.DataContext = vm;
        window.BindToViewModel();

        var content = (FrameworkElement)window.Content;
        window.Content = null;
        using var source = new HwndSource(new HwndSourceParameters("toolbar-paused-target-alignment")
        {
            // WS_POPUP, parked off-screen, 1x1: the content is hosted for layout and
            // PointToScreen only. Never shown to anyone.
            WindowStyle = unchecked((int)0x80000000),
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });
        var factor = EffectiveScale / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;

        if (startPaused)
            window.SetPausedState(true); // --paused: the hint is up before the first placement

        Layout(content);
        var sizeBefore = content.DesiredSize;

        // Publish every target exactly as the first gaze sample does in production.
        window.UpdateGaze(new PhysicalPoint(0, 0), 0);
        Check.That(window.TargetsInitialized).IsTrue();
        var published = BandButtonIds.ToDictionary(id => id, id => FaceCenter(window, id));

        // The pause state flips — what OverlayVmBindingCoordinator does on the VM's IsPaused change.
        window.SetPausedState(!startPaused);
        Layout(content);
        var sizeAfter = content.DesiredSize;

        // Structural invariant: the reserved Hidden slot means the band's desired size is IDENTICAL
        // in both states. This is what kills both Collapsed variants on all four edges — the face
        // check alone is absorbed by the hit radius on vertical docks, and the old PlaceAtEdge
        // check re-placed at the post-toggle size and could not fail.
        Check.WithCustomMessage(
                $"[startPaused={startPaused}, edge={edge}] the pause toggle changed the band's "
                + $"desired size {sizeBefore} -> {sizeAfter}; the hint must reserve its slot "
                + "(Visibility.Hidden, never Collapsed).")
            .That(sizeAfter).IsEqualTo(sizeBefore);

        // The correct shift is zero; 0.5 px is slack for floating-point/layout noise, far below
        // the sub-pixel claim and ~240× tighter than the hit radius.
        var failures = new List<string>();
        foreach (var id in BandButtonIds)
        {
            var now = FaceCenter(window, id);
            var target = published[id];
            var distance = Math.Sqrt(Math.Pow(now.X - target.X, 2) + Math.Pow(now.Y - target.Y, 2));
            if (distance > FaceShiftTolerancePx)
                failures.Add(
                    $"{id}: drawn ({now.X:F2},{now.Y:F2}) moved {distance:F2} px from its published "
                    + $"target ({target.X:F0},{target.Y:F0})");
        }

        Check.WithCustomMessage(
                $"[startPaused={startPaused}, edge={edge}] the pause toggle moved drawn faces off "
                + "their published dwell targets (the visible button no longer dwells):\n"
                + string.Join("\n", failures))
            .That(failures).IsEmpty();

        // And the docked window must stay on screen. Placed from the PRE-toggle size — the size
        // the state was actually laid out at — so this cannot be satisfied by re-measuring after
        // the change.
        var natural = new Size(sizeBefore.Width / factor, sizeBefore.Height / factor);
        var (left, top) = OverlayPositioning.PlaceAtEdge(WorkAreaDip, edge, natural.Width, natural.Height);
        Check.WithCustomMessage(
                $"[edge={edge}] docked toolbar {natural.Width:F0}x{natural.Height:F0} DIP lands at "
                + $"({left:F0},{top:F0}) — outside the {WorkAreaDip.Width:F0}x{WorkAreaDip.Height:F0} work area.")
            .That(left >= 0 && top >= 0
                  && left + natural.Width <= WorkAreaDip.Width
                  && top + natural.Height <= WorkAreaDip.Height).IsTrue();

        window.Close();
    }

    private static void Layout(FrameworkElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
    }

    /// <summary>The face's centre in device ("physical") pixels — what the dwell target was built from.</summary>
    private static PhysicalPoint FaceCenter(ToolbarWindow window, string id)
    {
        var face = window.ButtonForTest(id).Background;
        var p = face.PointToScreen(new Point(face.ActualWidth / 2, face.ActualHeight / 2));
        return new PhysicalPoint(p.X, p.Y);
    }
}
