namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The modes menu's two contracts since the labels were dropped (user decision 2026-10-06): every
/// icon's real drawn ink clears the progress arc's inner edge by the required +2 DIP at every
/// toolbar size, and each drawn disc sits exactly on the target the window publishes.
///
/// <para>
/// Both checks build the real window through the internal <c>BuildButtonsAt</c> seam, which lays
/// the panel out and rebuilds the buttons without <c>Show()</c> — no HWND, no screen, no input.
/// The icons are measured on the window's own built <see cref="DwellArcButton"/>s by
/// <see cref="ArcLabelFit.MeasureButton"/>, walking the real flattened geometry (never box
/// corners), so the window's production sizing is checked against the rendered result.
/// </para>
///
/// <para>
/// This class writes <c>DpiHelper</c>'s process-global scale (1.0, so physical pixels are DIPs),
/// so it lives in the exclusive DpiHelper collection; the window's <c>StaticResource</c> lookups
/// need the shared <see cref="WpfApplicationGate"/> token dictionary.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ModesMenuFitTests
{
    private const double WindowLeft = 120.0;
    private const double WindowTop = 250.0;

    private readonly ITestOutputHelper _output;

    public ModesMenuFitTests(ITestOutputHelper output)
    {
        _output = output;
        DpiHelper.Initialize(1.0, 1.0);
    }

    /// <summary>Every slider size (40–120 step 4) plus off-grid sizes so the sizing is computed.</summary>
    public static TheoryData<double> ToolbarSizes()
    {
        var data = new TheoryData<double>();
        for (var size = 40.0; size <= 120.0; size += 4.0)
            data.Add(size);
        data.Add(110); // off the slider's grid
        data.Add(150); // above the slider, inside validation's 40–200
        data.Add(200);
        return data;
    }

    /// <summary>
    /// Every icon, at every toolbar size: the real ink stays at least +2 DIP inside the arc's
    /// inner edge, and no button draws a label. 40 is the tightest size (a 20 DIP radius) and the
    /// reason ContainerRatio.Solo stops at 0.58.
    /// </summary>
    [StaTheory]
    [MemberData(nameof(ToolbarSizes))]
    public void EveryToolbarSize_DrawsEveryIconInsideTheArc(double size)
    {
        var (window, _) = Build(size);
        Layout(window);
        var buttons = window.DrawnButtons;

        Check.That(buttons).HasSize(4);

        foreach (var (id, button) in buttons)
        {
            var fit = ArcLabelFit.MeasureButton(button, id);
            _output.WriteLine($"{size:0.#}: {Describe(id, fit)}");

            Check.WithCustomMessage($"{size:0.#}/{id}: the button must carry a drawn icon.")
                 .That(fit.HasIcon).IsTrue();
            Check.WithCustomMessage(
                    $"{size:0.#}/{id}: no label may be drawn — the modes menu is icon-only since "
                    + "the user dropped the labels.")
                 .That(fit.HasLabel).IsFalse();
            Check.WithCustomMessage(
                    $"{size:0.#}/{id}: icon ink clears the arc's inner edge "
                    + $"({fit.ArcInnerEdgeDip:0.#} DIP) by {fit.IconClearanceDip:0.##} DIP; the "
                    + $"assertion needs at least {ArcLabelFit.RequiredClearanceDip:0.#}.")
                 .That(fit.IconClearanceDip).IsGreaterOrEqualThan(ArcLabelFit.RequiredClearanceDip);
        }
    }

    /// <summary>
    /// The accessibility half of dropping the labels: every icon-only button still exposes the
    /// ring's action name to UIA ("Left click", ...), through the invisible automation anchor on
    /// the built button.
    /// </summary>
    [StaFact]
    public void EveryButton_ExposesItsActionNameToUia()
    {
        var (window, _) = Build(112);
        Layout(window);

        var expected = new[]
        {
            ("modes_left", InteractionAction.LeftClick),
            ("modes_right", InteractionAction.RightClick),
            ("modes_double", InteractionAction.DoubleClick),
            ("modes_middle", InteractionAction.MiddleClick),
        };

        foreach (var (id, action) in expected)
        {
            var button = window.DrawnButtons[id];

            Check.WithCustomMessage($"{id}: an icon-only button needs an automation anchor for UIA.")
                 .That(button.AutomationAnchor).IsNotNull();
            Check.WithCustomMessage($"{id}: the anchor must publish the ring's action name.")
                 .That(AutomationProperties.GetName(button.AutomationAnchor!))
                 .IsEqualTo(RingLabelMap.For(action));
            Check.WithCustomMessage($"{id}: the anchor must be sized to the disc so its UIA rect is "
                    + "the button's, not a text sliver.")
                 .That(button.AutomationAnchor!.Width).IsEqualTo(button.Background.Width);
            Check.That(button.AutomationAnchor!.Height).IsEqualTo(button.Background.Height);
        }
    }

    /// <summary>
    /// Replaces revision 1's vacuous invariant (critique M3): the drawn disc's own Background
    /// ellipse must sit exactly on the target the window published — the two are separate
    /// arithmetic paths in the window, so a 1 DIP offset in drawing fails here. Runs at every
    /// toolbar size.
    /// </summary>
    [StaTheory]
    [MemberData(nameof(ToolbarSizes))]
    public void EveryToolbarSize_DrawsEachDiscExactlyOnItsPublishedTarget(double size)
    {
        var (window, vm) = Build(size);
        Layout(window);

        Check.That(vm.RowScreenPositions).HasSize(4);
        foreach (var target in vm.RowScreenPositions)
        {
            var background = window.DrawnButtons[target.Id].Background;
            var centreX = Canvas.GetLeft(background) + background.Width / 2;
            var centreY = Canvas.GetTop(background) + background.Height / 2;
            var expectedX = DpiHelper.PhysicalToLogicalX(target.Center.X) - window.Left;
            var expectedY = DpiHelper.PhysicalToLogicalY(target.Center.Y) - window.Top;
            var expectedDiameter = 2 * DpiHelper.PhysicalToLogicalX(target.Radius);

            Check.WithCustomMessage(
                    $"{size:0.#}/{target.Id}: drawn centre ({centreX:0.###}, {centreY:0.###}) must equal "
                    + $"the published target ({expectedX:0.###}, {expectedY:0.###}) in window coordinates.")
                 .That(centreX).IsCloseTo(expectedX, 1e-6);
            Check.WithCustomMessage(
                    $"{size:0.#}/{target.Id}: drawn centre Y {centreY:0.###} must equal the published "
                    + $"target Y {expectedY:0.###}.")
                 .That(centreY).IsCloseTo(expectedY, 1e-6);
            Check.WithCustomMessage(
                    $"{size:0.#}/{target.Id}: the disc's width {background.Width:0.###} must equal twice "
                    + $"the published radius ({expectedDiameter:0.###}).")
                 .That(background.Width).IsCloseTo(expectedDiameter, 1e-6);
            Check.WithCustomMessage(
                    $"{size:0.#}/{target.Id}: the disc's height {background.Height:0.###} must equal twice "
                    + $"the published radius ({expectedDiameter:0.###}).")
                 .That(background.Height).IsCloseTo(expectedDiameter, 1e-6);
        }
    }

    private static string Describe(string id, ArcLabelFitResult fit) =>
        $"{id}: inner edge {fit.ArcInnerEdgeDip:0.#}; icon ink clearance {fit.IconClearanceDip:0.##} DIP; "
        + $"label drawn={fit.HasLabel}; face {fit.ResolvedFace}";

    /// <summary>
    /// Builds the real window through the production seam and publishes the row targets the way
    /// <c>ShowAtTopCentre</c> does after <c>Show()</c>. The window is never shown.
    /// </summary>
    private static (ModesMenuWindow Window, ModesMenuViewModel Vm) Build(double sizeLogical)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var vm = new ModesMenuViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new ModesMenuWindow();
        window.Configure(vm, sizeLogical);
        vm.SetRowScreenPositions(window.BuildButtonsAt(sizeLogical, WindowLeft, WindowTop));
        return (window, vm);
    }

    /// <summary>
    /// The window is never shown, so its canvas has no presentation source and its children are
    /// never measured or arranged; a bare Canvas Measure/Arrange is all the icon paths need.
    /// </summary>
    private static void Layout(ModesMenuWindow window)
    {
        var canvas = (Canvas)window.FindName("ButtonCanvas");
        canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        canvas.Arrange(new Rect(new Point(0, 0), canvas.DesiredSize));
    }
}
