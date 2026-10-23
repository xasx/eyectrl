namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// The fixed "out" slot (synthesis #3/#6): on the shared right-edge anchor every context's exit
/// is the UPPER slot, one radius-plus-half-gap above the anchor — 60 DIP at the shipped sizes
/// (negative Y, because positive Y is down). A secondary button goes in the mirrored lower slot.
///
/// <para>
/// Action menu, manual selection and the keyboard were already in that slot; the ticket asks for
/// them to be PINNED here so a future layout edit cannot quietly move the one button the owner
/// aims at in every mode. Repeat and scroll move here from the anchor centre, and the drag's
/// Cancel/Back pair moves here at the full r50 edge-bar size.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class EdgeBarOutSlotTests
{
    /// <summary>ButtonRadius (50) + ManualSelectionSideButtonGap / 2 (10).</summary>
    private const double SlotOffsetDip = 60.0;

    public EdgeBarOutSlotTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    private static void CheckUpper(EdgeButtonDefinition definition) =>
        Check.WithCustomMessage($"{definition.Id} must be the upper out slot.")
             .That(definition.VerticalOffset).IsCloseTo(-SlotOffsetDip, 1e-9);

    private static void CheckLower(EdgeButtonDefinition definition) =>
        Check.WithCustomMessage($"{definition.Id} must be the lower secondary slot.")
             .That(definition.VerticalOffset).IsCloseTo(SlotOffsetDip, 1e-9);

    [Fact]
    public void ActionMenuClose_IsTheUpperSlot_AndHelpIsLower()
    {
        var definitions = ActionMenuEdgeButtons.Definitions(600);
        CheckUpper(definitions.Single(d => d.Id == ActionMenuEdgeButtons.CloseId));
        CheckLower(definitions.Single(d => d.Id == ActionMenuEdgeButtons.HelpId));
    }

    [Fact]
    public void ManualSelectionClose_IsTheUpperSlot_AndRecaptureIsLower()
    {
        var definitions = ManualSelectionEdgeButtons.Definitions(600);
        CheckUpper(definitions.Single(d => d.Id == ManualSelectionEdgeButtons.CloseId));
        CheckLower(definitions.Single(d => d.Id == ManualSelectionEdgeButtons.RecaptureId));
    }

    [Fact]
    public void KeyboardClose_IsTheUpperSlot_AndFlipIsLower()
    {
        // The keyboard keeps its own tracked anchor, but not its own slot order: Close is the
        // exit, so it is always upper on both screen edges.
        var definitions = KeyboardEdgeButtons.Definitions(600);
        CheckUpper(definitions.Single(d => d.Id == KeyboardEdgeButtons.CloseId));
        CheckLower(definitions.Single(d => d.Id == KeyboardEdgeButtons.FlipId));
    }

    [Fact]
    public void DragCancel_IsR50InTheUpperSlot()
    {
        var cancel = DragCoordinator.CreateCancelButtonDefinition(600);

        CheckUpper(cancel);
        Check.That(cancel.RadiusLogical).IsEqualTo(UIConstants.ButtonRadius);
    }

    [Fact]
    public void DragBack_IsR50InTheLowerSlot()
    {
        var back = DragCoordinator.CreateBackButtonDefinition(600);

        CheckLower(back);
        Check.That(back.RadiusLogical).IsEqualTo(UIConstants.ButtonRadius);
    }

    [Fact]
    public void TheTwoDragButtons_DoNotOverlap()
    {
        var cancel = DragCoordinator.CreateCancelButtonDefinition(600);
        var back = DragCoordinator.CreateBackButtonDefinition(600);

        var centreDistance = back.VerticalOffset - cancel.VerticalOffset;
        var radii = cancel.RadiusLogical + back.RadiusLogical;

        Check.WithCustomMessage(
                $"centres {centreDistance:0.#} DIP apart leave a {centreDistance - radii:0.#} "
                + "DIP gap between the r50 discs")
             .That(centreDistance - radii)
             .IsCloseTo(UIConstants.ManualSelectionSideButtonGap, 1e-9);
    }

    [Fact]
    public void RepeatStop_IsTheUpperSlot()
    {
        Check.That(RepeatModeController.StopButtonVerticalOffsetPhysical)
             .IsCloseTo(-SlotOffsetDip, 1e-9);
    }

    [Fact]
    public void ScrollStop_IsTheUpperSlot()
    {
        Check.That(ScrollModeController.StopButtonVerticalOffsetPhysical)
             .IsCloseTo(-SlotOffsetDip, 1e-9);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void EveryRightEdgeMode_PutsItsExitAtTheSameAbsoluteCentre(double scale)
    {
        // The owner's check: "the out slot sits in the same place in each mode". Every mode whose
        // exit lives on the right-edge anchor resolves that anchor through ONE shared helper, so
        // the centres agree by construction at every DPI. The keyboard is the deliberate
        // exception (it tracks the keyboard's edge) and is covered by its own slot pin above.
        DpiHelper.Initialize(scale, scale);
        try
        {
            Check.That(ScreenGeometry.SharedRightEdgeInsetLogical).IsEqualTo(30.0);

            var expected = ScreenGeometry.SharedRightEdgeExitAnchor()
                + new PhysicalPoint(0, -SlotOffsetDip * DpiHelper.ScaleY);

            var exits = new (string Mode, PhysicalPoint Centre)[]
            {
                ("ActionMenu",
                    OverlayMessageCoordinator.ActionMenuExitAnchorPhysical
                    + Offset(ActionMenuEdgeButtons.Definitions(600)
                        .Single(d => d.Id == ActionMenuEdgeButtons.CloseId))),
                ("ManualSelection",
                    OverlayVmBindingCoordinator.ManualSelectionExitAnchorPhysical
                    + Offset(ManualSelectionEdgeButtons.Definitions(600)
                        .Single(d => d.Id == ManualSelectionEdgeButtons.CloseId))),
                ("Drag",
                    DragCoordinator.ExitAnchorPhysical
                    + Offset(DragCoordinator.CreateCancelButtonDefinition(600))),
                ("Repeat",
                    RepeatModeController.ExitAnchorPhysical
                    + Offset(RepeatModeController.CreateRepeatStopButtonDefinition(
                        600, RepeatModeController.StopButtonVerticalOffsetPhysical))),
                ("Scroll",
                    ScrollModeController.ExitAnchorPhysical
                    + Offset(ScrollModeController.CreateScrollStopButtonDefinition(
                        600, ScrollModeController.StopButtonVerticalOffsetPhysical))),
            };

            foreach (var (mode, centre) in exits)
            {
                Check.WithCustomMessage(
                        $"{mode}'s exit is at ({centre.X:F1},{centre.Y:F1}) but the shared out "
                        + $"slot is ({expected.X:F1},{expected.Y:F1}) at DPI {scale}.")
                     .That(centre).IsEqualTo(expected);
            }
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    private static PhysicalPoint Offset(EdgeButtonDefinition definition) =>
        new(0, definition.VerticalOffset);

    [StaFact]
    public void ScrollEntry_DrawsTheStopButtonWhereItsStopZoneIs()
    {
        // The engine's stop zone is where the button is drawn. When the button moved from the
        // anchor's centre to the upper slot, a zone left behind on the anchor would let the page
        // keep scrolling while the user dwells their way out.
        var bar = new TestFakes.FakeEdgeButtonBar();
        var (controller, im, _, _) =
            ScrollModeControllerThreadingTests.NewScrollWorld(
                new TestFakes.FakeInputSimulator(), edgeBar: bar);
        using (controller)
        {
            ScrollModeControllerThreadingTests.EnterScrollMode(im);

            var definition = bar.EdgeBarVm!.DefinitionFor("scroll-stop");
            Check.That(definition).IsNotNull();
            CheckUpper(definition!);

            var drawnCentre = bar.EdgeBarVm.SeedCenters["scroll-stop"];
            Check.WithCustomMessage(
                    "the scroll entry did not draw the stop button on the shared exit anchor")
                 .That(drawnCentre).IsEqualTo(
                     ScrollModeController.ExitAnchorPhysical
                     + new PhysicalPoint(0, ScrollModeController.StopButtonVerticalOffsetPhysical));
            Check.That(controller.OverlayVm).IsNotNull();
            Check.WithCustomMessage("the drawn button and the engine's stop zone disagree")
                 .That(controller.OverlayVm!.StopButtonCenter)
                 .IsEqualTo(drawnCentre);
        }
    }
}
