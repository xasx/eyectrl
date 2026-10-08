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
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.ActionMenu;
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
/// This class writes <c>DpiHelper</c>'s process-global scale — 1.0 for most cases and 1.5/2.0 for
/// the disc-placement matrix (2.0 is the user's display, where physical differs from logical) —
/// so it lives in the exclusive DpiHelper collection and restores 1.0 after every test; the
/// window's <c>StaticResource</c> lookups need the shared <see cref="WpfApplicationGate"/> token
/// dictionary.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ModesMenuFitTests : IDisposable
{
    private const double WindowLeft = 120.0;
    private const double WindowTop = 250.0;

    private readonly ITestOutputHelper _output;

    public ModesMenuFitTests(ITestOutputHelper output)
    {
        _output = output;
        DpiHelper.Initialize(1.0, 1.0);
    }

    /// <summary>
    /// Restores the process-global scale after every test (the repo's DpiHelper-writer pattern;
    /// the assembly restore attribute is the backstop). The scale-2.0 cases below are the user's
    /// 200% display, and xUnit does not guarantee which theory case runs last.
    /// </summary>
    public void Dispose() => DpiHelper.Initialize(1.0, 1.0);

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
    /// Sizes × display scales for the disc-equals-target pin. The full slider sweep runs at 1.0,
    /// where the class's default lives; 1.5 and 2.0 are sampled across the range (40 = tightest
    /// target, 200 = largest). 2.0 is the user's actual display, where a missing or doubled
    /// LogicalToPhysical in ComputeButtonCentres cannot hide behind physical == logical.
    /// </summary>
    public static TheoryData<double, double> ToolbarSizesAndScales()
    {
        var data = new TheoryData<double, double>();
        for (var size = 40.0; size <= 120.0; size += 4.0)
            data.Add(size, 1.0);
        data.Add(110, 1.0);
        data.Add(150, 1.0);
        data.Add(200, 1.0);
        foreach (var size in new[] { 40.0, 56.0, 80.0, 112.0, 200.0 })
        {
            data.Add(size, 1.5);
            data.Add(size, 2.0);
        }
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

            // The fit rule is one-sided (a smaller icon only gains clearance), so pin the size
            // itself: flipping ModesMenuWindow back to Standard keeps every fit green while
            // silently undoing the user's "bigger icons" decision.
            var expectedScale = button.Background.Width * ContainerRatio.Solo / ActionIcons.GridSize;
            Check.WithCustomMessage(
                    $"{size:0.#}/{id}: icon scale {fit.IconScale:0.####} must equal diameter "
                    + $"{button.Background.Width:0.#} x ContainerRatio.Solo / {ActionIcons.GridSize} "
                    + $"= {expectedScale:0.####} (Standard would draw "
                    + $"{button.Background.Width * ContainerRatio.Dominant / ActionIcons.GridSize:0.####}).")
                 .That(fit.IconScale).IsCloseTo(expectedScale, 1e-9);
        }
    }

    /// <summary>
    /// The accessibility half of dropping the labels: every icon-only button exposes the HOST's
    /// name to UIA through the invisible automation anchor on the built button — a custom source
    /// here, so a window that read the English map directly fails.
    /// </summary>
    [StaFact]
    public void EveryButton_ExposesTheHostsActionNameToUia()
    {
        var labels = new RingLabelSource(action => $"modes:{action}", BackLabel: "Back", MoreLabel: "More");
        var (window, _) = Build(112, labels);
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
            Check.WithCustomMessage($"{id}: the anchor must publish the host's action name.")
                 .That(AutomationProperties.GetName(button.AutomationAnchor!))
                 .IsEqualTo($"modes:{action}");
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
    /// toolbar size at scale 1.0, and at 1.5/2.0 (the user's display) on sampled sizes: at 1.0 a
    /// missing or doubled LogicalToPhysical in <c>ComputeButtonCentres</c> is invisible, because
    /// physical equals logical there.
    /// </summary>
    [StaTheory]
    [MemberData(nameof(ToolbarSizesAndScales))]
    public void EveryToolbarSize_DrawsEachDiscExactlyOnItsPublishedTarget(double size, double scale)
    {
        DpiHelper.Initialize(scale, scale);

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
            var at = $"{size:0.#}@{scale:0.0}x/{target.Id}";

            Check.WithCustomMessage(
                    $"{at}: drawn centre ({centreX:0.###}, {centreY:0.###}) must equal "
                    + $"the published target ({expectedX:0.###}, {expectedY:0.###}) in window coordinates.")
                 .That(centreX).IsCloseTo(expectedX, 1e-6);
            Check.WithCustomMessage(
                    $"{at}: drawn centre Y {centreY:0.###} must equal the published "
                    + $"target Y {expectedY:0.###}.")
                 .That(centreY).IsCloseTo(expectedY, 1e-6);
            Check.WithCustomMessage(
                    $"{at}: the disc's width {background.Width:0.###} must equal twice "
                    + $"the published radius ({expectedDiameter:0.###}).")
                 .That(background.Width).IsCloseTo(expectedDiameter, 1e-6);
            Check.WithCustomMessage(
                    $"{at}: the disc's height {background.Height:0.###} must equal twice "
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
    private static (ModesMenuWindow Window, ModesMenuViewModel Vm) Build(
        double sizeLogical, RingLabelSource? labels = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var vm = new ModesMenuViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new ModesMenuWindow();
        if (labels is not null)
            window.Labels = labels;
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
