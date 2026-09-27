namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The render half of spec L8's dimming guarantee: the ViewModels suite pins that dimmed
/// (latched-incompatible) Drag/ScrollMode items are NON-TARGETS, but nothing else proved the user
/// can SEE the refusal — a disabled slot that renders fully live while having no dwell target is
/// exactly the "disabled control that does not pretend to work" rule L8 forbids (external review
/// I2: the dim branch could be deleted with the whole suite green).
///
/// <para>
/// <c>ActionMenuWindow.Show</c> puts every disabled item's <c>DwellArcButton</c> parts under a
/// per-button host <see cref="Canvas"/> at <c>ActionMenuWindow.DisabledOpacity</c> (group
/// opacity), while live items' parts go straight onto <c>MenuCanvas</c>. <c>DwellArcButton.AddTo</c>
/// adds only shapes — never a nested canvas — so the host canvases are the ONLY
/// <see cref="Canvas"/> children <c>MenuCanvas</c> ever has.
/// </para>
///
/// <para>
/// Construction-only <c>[StaFact]</c> pattern (compare <c>EdgeButtonBarCaptureTests</c>): a real
/// window in the test host, no app launch. The gate supplies the overlay token dictionary the
/// window's <c>Tokens.Brush*</c> reads need; the async screen-capture preview is fully
/// try-contained in production and is not awaited here.
/// </para>
/// </summary>
public class ActionMenuWindowDimRenderTests
{
    [StaFact]
    public void DisabledItems_RenderUnderAHostCanvasAtDisabledOpacity_LiveItemsDoNot()
    {
        Fixtures.WpfApplicationGate.EnsureApplicationWithTokens();

        // Drag and ScrollMode pinned onto layer one (the ActionMenuViewModelLatchTests pattern),
        // then latched: both dim, LeftClick stays live.
        var settings = new EyeCtrlSettings();
        settings.ActionMenu.PrimaryActions = new List<InteractionAction>
        { InteractionAction.LeftClick, InteractionAction.Drag, InteractionAction.ScrollMode };
        var vm = new ActionMenuViewModel(settings, new WeakReferenceMessenger());
        vm.Receive(new ShowActionMenuMessage(
            Center: new PhysicalPoint(500, 500),
            Actions: new List<InteractionAction>
            { InteractionAction.LeftClick, InteractionAction.Drag, InteractionAction.ScrollMode },
            CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false));
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        var window = new ActionMenuWindow { DataContext = vm };
        try
        {
            window.Show(
                new PhysicalPoint(500, 500),
                new List<InteractionAction> { InteractionAction.LeftClick },
                showBackButton: false, dwellTimeMs: 600, ringRadius: 250, previewCircleSize: 250,
                borderColor: "#FFFFFF", positionIndicatorStyle: PositionIndicatorStyle.Crosshair);

            // AddTo adds only shapes, so the host canvases are the only Canvas children — and the
            // opacity comes from the PRODUCT constant, so a value change fails here too. Two
            // hosts means the live LeftClick item's parts sit directly on MenuCanvas.
            var hosts = window.MenuCanvas.Children.OfType<Canvas>().ToList();
            Check.That(hosts).HasSize(2); // Drag + ScrollMode
            Check.That(hosts.All(h => Math.Abs(h.Opacity - ActionMenuWindow.DisabledOpacity) < 0.001)).IsTrue();
            Check.That(hosts.All(h => h.Children.Count > 0)).IsTrue(); // the dimmed button parts ride inside
        }
        finally
        {
            window.Hide();
            window.Close();
        }
    }
}
