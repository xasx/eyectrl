namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// G3: geometry parity for the shared ring painter. These construct buttons and canvases on an STA
/// thread but never a window, so they run headless in CI.
///
/// <para>
/// The parity claim is centres-only, at DPI 1.0 and 2.0: at runtime scale the painter must place
/// every button exactly where the window's old inline loop did — the canvas point
/// <see cref="ActionMenuGeometry.ButtonCanvasCenter"/> produced — and it must always paint the
/// TRUE button radius, never a scaled one. The scaled mode is pinned the same way: the
/// <c>LayoutTransform</c> carries the scale, the buttons inside stay true-size, and the separate
/// labels sit at the scaled centres with an unscaled font.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ActionMenuRingPainterTests : IDisposable
{
    /// <summary>
    /// Restores the process-global scale after every test, exactly as
    /// <c>ActionMenuGeometryTests</c> does — xUnit does not guarantee which theory case runs last.
    /// </summary>
    public void Dispose() => DpiHelper.Initialize(1.0, 1.0);

    /// <summary>
    /// Builds a VM, drives it open, and converts its items the way <c>ActionMenuWindow.Show</c>
    /// does — including the absolute-physical → window-relative conversion through
    /// <see cref="ActionMenuGeometry.ButtonCanvasCenter"/> — then paints them. With
    /// <paramref name="latched"/>, Drag and ScrollMode are pinned onto layer one and a modifier is
    /// latched, so the dimmed slots go through the painter too.
    /// </summary>
    private static (ActionMenuViewModel Vm, List<RingPaintSlot> Slots) OpenAndBuildSlots(
        double scale, bool showBack = false, bool latched = false)
    {
        DpiHelper.Initialize(scale, scale);

        var settings = new EyeCtrlSettings();
        if (latched)
        {
            settings.ActionMenu.PrimaryActions = new List<InteractionAction>
            { InteractionAction.LeftClick, InteractionAction.Drag, InteractionAction.ScrollMode };
        }

        var messenger = new WeakReferenceMessenger();
        var vm = new ActionMenuViewModel(settings, messenger);

        // Menu center as an absolute physical-screen point (as GazeEngineAdapter supplies it).
        var center = new PhysicalPoint(1200 * scale, 800 * scale);
        vm.Receive(new ShowActionMenuMessage(
            center,
            settings.ActionMenu.VisibleActions,
            ShowBack: showBack,
            DwellTimeMs: 600,
            GazePoint: center,
            ManualSelectionDisplayRadius: 0,
            CircleSize: settings.Cursor.CircleSize));
        if (latched)
            vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        var windowSize = RingPlan.WindowSizeFor(vm.EffectiveRingRadius);
        var canvasCenter = new Point(windowSize / 2, windowSize / 2);

        var slots = vm.Items
            .Select(i => new RingPaintSlot(
                i.Id,
                ActionMenuGeometry.ButtonCanvasCenter(i.Position, center, canvasCenter),
                i.Icon,
                i.Label,
                i.Kind,
                i.IsDisabled))
            .ToList();

        return (vm, slots);
    }

    /// <summary>
    /// At DPI 1.0 and 2.0 the painter's runtime mode reproduces today's coordinates exactly: every
    /// button's drawn centre equals the <see cref="ActionMenuGeometry.ButtonCanvasCenter"/> the
    /// window computed, and every button is the true diameter — a <c>Create(s × radius)</c> would
    /// change the width at one of the two scales. The latched cases put dimmed slots through the
    /// same path: their parts ride in a per-button host canvas at origin, so the drawn centre and
    /// width must not move.
    /// </summary>
    [StaTheory]
    [InlineData(1.0, false)]
    [InlineData(2.0, false)]
    [InlineData(1.0, true)]
    [InlineData(2.0, true)]
    public void RuntimePaint_PlacesEveryButtonAtTheCanvasCenter_AtAnyDpi(double scale, bool latched)
    {
        var (vm, slots) = OpenAndBuildSlots(scale, latched: latched);
        using var _vm = vm;
        var disabledCount = slots.Count(s => s.IsDisabled);
        Check.WithCustomMessage("The latched case dimmed nothing, so it would pin nothing.")
             .That(disabledCount > 0).IsEqualTo(latched);

        var canvas = new Canvas();
        var buttons = ActionMenuRingPainter.Paint(canvas, slots, RingPaintOptions.Runtime(showLabels: false));

        Check.That(buttons.Count).IsEqualTo(slots.Count);
        Check.That(buttons.Keys).IsEquivalentTo(slots.Select(s => s.Id));

        foreach (var slot in slots)
        {
            var button = buttons[slot.Id];
            var message = $"slot '{slot.Id}' at scale {scale}";
            var drawnCenter = new Point(
                Canvas.GetLeft(button.Background) + ActionMenuGeometry.ButtonRadius,
                Canvas.GetTop(button.Background) + ActionMenuGeometry.ButtonRadius);

            Check.WithCustomMessage(message).That(drawnCenter.X).IsCloseTo(slot.Center.X, 1e-9);
            Check.WithCustomMessage(message).That(drawnCenter.Y).IsCloseTo(slot.Center.Y, 1e-9);

            // The true radius, always: the painter never passes a scaled radius into Create.
            Check.WithCustomMessage(message)
                 .That(button.Background.Width).IsEqualTo(ActionMenuGeometry.ButtonRadius * 2);
        }

        // Runtime mode paints directly onto the canvas — no transform wrapper between it and the
        // window's own children. The only nested canvases are the dim hosts, one per disabled
        // slot, untransformed and at DisabledOpacity.
        var hosts = canvas.Children.OfType<Canvas>().ToList();
        Check.That(hosts).HasSize(disabledCount);
        foreach (var host in hosts)
        {
            // At origin: the centre check above reads each part's offset relative to its HOST, so
            // a displaced host would draw dimmed slots off their spot with that check still green.
            Check.That(OffsetOrZero(Canvas.GetLeft(host))).IsEqualTo(0.0);
            Check.That(OffsetOrZero(Canvas.GetTop(host))).IsEqualTo(0.0);
            Check.That(host.LayoutTransform.Value.IsIdentity).IsTrue();
            Check.That(host.RenderTransform.Value.IsIdentity).IsTrue();
            Check.That(host.Opacity).IsCloseTo(ActionMenuRingPainter.DisabledOpacity, 1e-9);
        }
    }

    /// <summary>An unset <c>Canvas.Left</c>/<c>Top</c> (NaN) places a child at 0, same as an explicit 0.</summary>
    private static double OffsetOrZero(double offset) => double.IsNaN(offset) ? 0.0 : offset;

    /// <summary>
    /// Runtime label mode: the Help toggle decides whether the label goes into <c>Create</c>, and
    /// the runtime More slot is never annotated ("More", not "More +N") — the "+N" is the
    /// preview's deliberate divergence.
    /// </summary>
    [StaFact]
    public void RuntimePaint_DrawsLabelsIntoTheButton_OnlyWhenAsked_AndNeverAnnotatesMore()
    {
        var settings = new EyeCtrlSettings();
        settings.ActionMenu.PrimaryActions = new List<InteractionAction> { InteractionAction.LeftClick };
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(settings, messenger);

        vm.Receive(new ShowActionMenuMessage(
            new PhysicalPoint(900, 500),
            new List<InteractionAction> { InteractionAction.LeftClick, InteractionAction.Keyboard },
            ShowBack: false,
            DwellTimeMs: 600,
            new PhysicalPoint(900, 500),
            CircleSize: 250));

        var slots = vm.Items
            .Select(i => new RingPaintSlot(i.Id, new Point(100, 100), i.Icon, i.Label, i.Kind))
            .ToList();

        var withoutLabels = ActionMenuRingPainter.Paint(
            new Canvas(), slots, RingPaintOptions.Runtime(showLabels: false));
        foreach (var id in withoutLabels.Keys)
            Check.WithCustomMessage($"{id} drew a label while the Help toggle is off")
                 .That(withoutLabels[id].Label).IsNull();

        var withLabels = ActionMenuRingPainter.Paint(
            new Canvas(), slots, RingPaintOptions.Runtime(showLabels: true));
        foreach (var slot in slots)
            Check.WithCustomMessage($"{slot.Id} lost its label while the Help toggle is on")
                 .That(withLabels[slot.Id].Label!.Text).IsEqualTo(slot.Label);

        Check.WithCustomMessage("The runtime More slot was annotated; '+N' belongs to the preview.")
             .That(withLabels["More"].Label!.Text).IsEqualTo("More");
    }

    /// <summary>
    /// The scaled mode: one nested true-size painting carrying the <c>LayoutTransform</c>, buttons
    /// inside at their true centres and true radius, and unscaled labels on the target canvas at
    /// the scaled RUNTIME label position. Labels are direct children of the target canvas
    /// precisely because they must not inherit the transform.
    ///
    /// <para>
    /// The label position is measured against a labelled true-size <c>DwellArcButton</c> built by
    /// the same factory the runtime uses — the runtime draws the label under the icon, inside the
    /// button, and the preview must place its unscaled label at that offset SCALED. Centring the
    /// label on the glyph (the T3 shape) drew type across the icon at the 494 budget; the
    /// positive offset assertion below is that regression's tripwire.
    /// </para>
    /// </summary>
    [StaFact]
    public void ScaledPreviewPaint_KeepsButtonsTrueSize_AndPlacesUnscaledLabelsAtTheScaledRuntimePosition()
    {
        const double scale = 0.5;

        var plan = RingPlan.Create(
            new List<InteractionAction>
            {
                InteractionAction.LeftClick,
                InteractionAction.RightClick,
                InteractionAction.DoubleClick,
                InteractionAction.Drag,
                InteractionAction.Keyboard,
            },
            new List<InteractionAction> { InteractionAction.LeftClick, InteractionAction.RightClick },
            circleSize: 250);
        var layer = plan.BuildLayer(RingLayer.Primary, showBack: false);

        var canvasCenter = new Point(plan.WindowSize / 2, plan.WindowSize / 2);
        var slots = layer.Slots
            .Select(slot =>
            {
                var radians = slot.AngleDegrees * MathConstants.DegreesToRadians;
                return new RingPaintSlot(
                    slot.Id,
                    new Point(
                        canvasCenter.X + plan.EffectiveRingRadius * Math.Cos(radians),
                        canvasCenter.Y + plan.EffectiveRingRadius * Math.Sin(radians)),
                    slot.Icon,
                    slot.Label,
                    slot.Kind);
            })
            .ToList();

        // The runtime label placement, read off a labelled true-size button: its top relative to
        // the button centre. Same radius for every slot, so one probe answers for all of them.
        var probeCenter = new Point(500, 500);
        var probe = DwellArcButton.Create(
            probeCenter, ActionMenuGeometry.ButtonRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.More), label: "probe");
        Check.WithCustomMessage(
                "The runtime label no longer sits below the button's centre, so the preview's "
                + "offset would not clear the glyph.")
             .That(Canvas.GetTop(probe.Label!) - probeCenter.Y).IsStrictlyGreaterThan(0);
        var runtimeLabelTopOffset = Canvas.GetTop(probe.Label!) - probeCenter.Y;

        var canvas = new Canvas();
        var buttons = ActionMenuRingPainter.Paint(
            canvas, slots, RingPaintOptions.Preview(scale, plan.SecondaryActions.Count));

        var painting = canvas.Children.OfType<Canvas>().Single();
        Check.That(painting.LayoutTransform).IsInstanceOf<ScaleTransform>();
        var transform = (ScaleTransform)painting.LayoutTransform;
        Check.That(transform.ScaleX).IsEqualTo(scale);
        Check.That(transform.ScaleY).IsEqualTo(scale);

        // Labels are siblings of the painting, not inside it — the transform must not touch them.
        var labels = canvas.Children.OfType<TextBlock>().ToList();
        Check.That(labels).HasSize(slots.Count);

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var button = buttons[slot.Id];

            var message = $"slot '{slot.Id}'";
            // True-size painting inside the transform.
            Check.WithCustomMessage(message)
                 .That(Canvas.GetLeft(button.Background) + ActionMenuGeometry.ButtonRadius)
                 .IsCloseTo(slot.Center.X, 1e-9);
            Check.WithCustomMessage(message)
                 .That(button.Background.Width).IsEqualTo(ActionMenuGeometry.ButtonRadius * 2);
            Check.WithCustomMessage(message).That(button.Label).IsNull();

            var label = labels[i];
            Check.WithCustomMessage(message)
                 .That(label.FontSize).IsEqualTo(Tokens.FontSizeSubtitle);
            Check.WithCustomMessage(message)
                 .That(Canvas.GetLeft(label) + label.DesiredSize.Width / 2)
                 .IsCloseTo(slot.Center.X * scale, 1e-9);
            // The SCALED runtime top — not the scaled centre (which would sit on the glyph).
            Check.WithCustomMessage(message)
                 .That(Canvas.GetTop(label))
                 .IsCloseTo((slot.Center.Y + runtimeLabelTopOffset) * scale, 1e-9);
        }

        var more = slots.Single(slot => slot.Kind == ActionMenuItemKind.OpenMore);
        Check.That(labels[slots.IndexOf(more)].Text)
             .IsEqualTo($"More +{plan.SecondaryActions.Count}");
    }

    /// <summary>
    /// "Render without Back" is compositional: the preview builds its layer with
    /// <c>showBack: false</c> and the painter draws exactly the slots it is given — it neither
    /// invents nor drops one. The plan still composes the ExitMenu slot when the flag is set,
    /// which is the runtime's shape.
    /// </summary>
    [StaFact]
    public void PreviewPaint_DrawsExactlyItsSlots_SoNoBackWhenThePlanOmitsIt()
    {
        var plan = RingPlan.Create(
            new List<InteractionAction> { InteractionAction.LeftClick, InteractionAction.Keyboard },
            new List<InteractionAction> { InteractionAction.LeftClick },
            circleSize: 250);

        var previewLayer = plan.BuildLayer(RingLayer.Primary, showBack: false);
        Check.That(previewLayer.Slots.Any(slot => slot.Kind == ActionMenuItemKind.ExitMenu)).IsFalse();

        var slots = previewLayer.Slots
            .Select(slot => new RingPaintSlot(slot.Id, new Point(75, 75), slot.Icon, slot.Label, slot.Kind))
            .ToList();
        var buttons = ActionMenuRingPainter.Paint(
            new Canvas(), slots, RingPaintOptions.Preview(scale: 1.0, plan.SecondaryActions.Count));

        Check.That(buttons).ContainsKey("More");
        Check.That(buttons).Not.ContainsKey("Back");
        Check.That(buttons.Count).IsEqualTo(previewLayer.Slots.Count);
    }

    /// <summary>
    /// Zero targets by construction, not by a flag: the painter's surface accepts no
    /// <c>IDwellRegistrar</c> (and no <c>DwellControl</c>), so nothing it paints can register a
    /// dwell target. The recording-registrar gate on the render path lands with the preview host.
    /// </summary>
    [Fact]
    public void ThePainterSurface_TakesNoDwellRegistrar()
    {
        var methods = typeof(ActionMenuRingPainter)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);

        Check.That(methods).Not.IsEmpty();

        foreach (var method in methods)
        foreach (var parameter in method.GetParameters())
        {
            Check.WithCustomMessage(
                    $"The painter takes a {parameter.ParameterType.Name}. The painter must stay "
                    + "structurally incapable of registering dwell targets — registration is the "
                    + "window/ViewModel path.")
                 .That(typeof(EyeCtrl.Core.Processing.IDwellRegistrar).IsAssignableFrom(parameter.ParameterType))
                 .IsFalse();
            Check.WithCustomMessage(
                    $"The painter takes a {parameter.ParameterType.Name}; a DwellControl would "
                    + "register on construction.")
                 .That(typeof(EyeCtrl.Controls.DwellControl).IsAssignableFrom(parameter.ParameterType))
                 .IsFalse();
        }
    }

    /// <summary>
    /// A registration call as code — <c>AddTarget(</c>, whatever receiver precedes it.
    /// </summary>
    private static readonly Regex AddTargetCall = new(@"AddTarget\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// A dwell-control construction as code. <c>new DwellControl</c> itself is impossible (its
    /// constructor is protected); the forms that matter are its subclasses, which register when
    /// attached — so the scan is on the <c>new Dwell…</c> shape, not one class name.
    /// </summary>
    private static readonly Regex DwellControlConstruction = new(@"new\s+Dwell\w+", RegexOptions.Compiled);

    /// <summary>
    /// The pattern's own falsification pin: a scan that cannot recognise the forms it forbids
    /// cannot catch them. Both shapes are the real registration path the surface pin above is
    /// meant to exclude structurally.
    /// </summary>
    [Theory]
    [InlineData("registrar.AddTarget(new DwellTarget(\"x\", point, radius, 600));")]
    [InlineData("var c = new DwellButton(registrar, \"x\", \"Label\", action, fill, cat, r, ms);")]
    [InlineData("var c = new DwellToggle(registrar, \"x\", \"Label\", get, set, cat, r, ms);")]
    public void ThePainterScan_RecognisesTheFormsItForbids(string form)
    {
        Check.WithCustomMessage($"The scan no longer recognises this forbidden form:\n  {form}")
             .That(AddTargetCall.IsMatch(form) || DwellControlConstruction.IsMatch(form)).IsTrue();
    }

    /// <summary>
    /// M1: the surface pin above checks parameter TYPES only, and that is not the whole
    /// structural claim its own comment makes. A registrar could be reached without appearing in
    /// the signature — a stored field, a closure — and a dwell control constructed inside the
    /// file would register when the host attaches it. This reads the painter's SOURCE
    /// (comment-stripped, so the class doc's mentions of both names are not live code) and
    /// asserts neither form exists, so "constructs no DwellControl" is a measured claim.
    /// </summary>
    [Fact]
    public void ThePainterSource_RegistersNothing_AndConstructsNoDwellControl()
    {
        var source = SourceScanner.ScannedFiles()
            .Single(f => f.Relative == "src/EyeCtrl.App/Overlays/ActionMenuRingPainter.cs").Text;

        Check.WithCustomMessage(
                "ActionMenuRingPainter calls AddTarget. The painter must be structurally "
                + "incapable of registering dwell targets — registration is the window/ViewModel "
                + "path, never the painting path a preview also runs.")
             .That(AddTargetCall.IsMatch(source)).IsFalse();
        Check.WithCustomMessage(
                "ActionMenuRingPainter constructs a dwell control. DwellControl subclasses "
                + "register when attached, so a preview painted through this file could become a "
                + "live target. The painter draws DwellArcButtons only.")
             .That(DwellControlConstruction.IsMatch(source)).IsFalse();
    }
}
