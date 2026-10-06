namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The modes menu's label contract (Option A, user decision 2026-10-05): short labels, drawn only
/// when EVERY one clears the arc at the drawn size (all-or-none), never a mix — and its disc
/// geometry, pinned against the targets the window publishes.
///
/// <para>
/// Both checks build the real window through the internal <c>BuildButtonsAt</c> seam, which lays
/// the panel out and rebuilds the buttons without <c>Show()</c> — no HWND, no screen, no input.
/// The labels are measured on the window's own built <see cref="DwellArcButton"/>s by
/// <see cref="ArcLabelFit.MeasureButton"/>, so the window's production fit decision
/// (<see cref="DwellArcButton.LabelInkClearance"/>) is checked against the rendered result, not
/// against itself.
/// </para>
///
/// <para>
/// This class writes <c>DpiHelper</c>'s process-global scale (1.0, so physical pixels are DIPs),
/// so it lives in the exclusive DpiHelper collection; the window's <c>StaticResource</c> lookups
/// need the shared <see cref="WpfApplicationGate"/> token dictionary.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ModesMenuLabelFitTests
{
    private const double WindowLeft = 120.0;
    private const double WindowTop = 250.0;

    private readonly ITestOutputHelper _output;

    public ModesMenuLabelFitTests(ITestOutputHelper output)
    {
        _output = output;
        DpiHelper.Initialize(1.0, 1.0);
    }

    /// <summary>Every slider size (40–120 step 4) plus off-grid sizes so the decision is computed.</summary>
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

    [StaTheory]
    [MemberData(nameof(ToolbarSizes))]
    public void EveryToolbarSize_DrawsEveryLabelInsideTheArcOrNoneAtAll(double size)
    {
        var (window, _) = Build(size);
        Layout(window);
        var buttons = window.DrawnButtons;

        Check.That(buttons).HasSize(4);

        var labelled = buttons.Values.Count(b => b.Label is not null);
        var iconOnly = buttons.Values.Count(b => b.Label is null);
        Check.WithCustomMessage(
                $"{size:0.#}: the menu mixed {labelled} labelled with {iconOnly} icon-only discs; "
                + "Option A shows all four labels or none.")
             .That(labelled == 0 || iconOnly == 0).IsTrue();

        foreach (var (id, button) in buttons)
        {
            if (labelled == 0)
            {
                Check.WithCustomMessage(
                        $"{size:0.#}/{id}: no label may be drawn when the menu hid the labels.")
                     .That(button.Label).IsNull();
                continue;
            }

            AssertFitsAndClears(size, id, button);
        }
    }

    /// <summary>
    /// Replaces revision 1's vacuous invariant (critique M3): the drawn disc's own Background
    /// ellipse must sit exactly on the target the window published — the two are separate
    /// arithmetic paths in the window, so a 1 DIP offset in drawing fails here. Runs with labels
    /// off (small sizes) and on (large sizes), since the theory covers both.
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

    /// <summary>
    /// The user's 112 (their settings.json size) and a size beyond the slider: all four short
    /// labels are drawn and pass both rules. This is the mutation anchor for "force always show
    /// labels" only in the other direction — with the old long labels restored, the drawn
    /// "Double" fails here.
    /// </summary>
    [StaTheory]
    [InlineData(112.0)]
    [InlineData(200.0)]
    public void AtTheUsersSizeAndBeyond_AllFourShortLabelsAreDrawnAndPass(double size)
    {
        var (window, _) = Build(size);
        Layout(window);
        var buttons = window.DrawnButtons;

        Check.That(buttons.Values.All(b => b.Label is not null)).IsTrue();
        Check.That(buttons["modes_left"].Label?.Text).IsEqualTo("Left");
        Check.That(buttons["modes_right"].Label?.Text).IsEqualTo("Right");
        Check.That(buttons["modes_double"].Label?.Text).IsEqualTo("Double");
        Check.That(buttons["modes_middle"].Label?.Text).IsEqualTo("Middle");

        foreach (var (id, button) in buttons)
            AssertFitsAndClears(size, id, button);
    }

    [StaFact]
    public void AtTheSmallestSliderSize_NoLabelIsDrawn()
    {
        var (window, _) = Build(40);
        Layout(window);

        Check.WithCustomMessage("At size 40 no label can clear the arc, so the whole menu is icon-only.")
             .That(window.DrawnButtons.Values.All(b => b.Label is null)).IsTrue();
    }

    private void AssertFitsAndClears(double size, string id, DwellArcButton button)
    {
        var fit = ArcLabelFit.MeasureButton(button, id);
        _output.WriteLine($"{size:0.#}: {Describe(id, fit)}");

        Check.WithCustomMessage($"{size:0.#}/{id}: the drawn label must be measured.")
             .That(fit.HasLabel).IsTrue();
        Check.WithCustomMessage($"{size:0.#}/{id}: the button must carry a drawn icon.")
             .That(fit.HasIcon).IsTrue();
        Check.WithCustomMessage($"{size:0.#}/{id}: label ink {fit.LabelInkBox} overlaps the icon {fit.OverlapsIcon}.")
             .That(fit.OverlapsIcon).IsFalse();
        Check.WithCustomMessage($"{size:0.#}/{id}: label ink {fit.LabelInkBox} overlaps a symbol {fit.OverlapsSymbol}.")
             .That(fit.OverlapsSymbol).IsFalse();
        Check.WithCustomMessage(
                $"{size:0.#}/{id}: label ink {fit.LabelInkBox} clears the arc's inner edge "
                + $"({fit.ArcInnerEdgeDip:0.#} DIP) by {fit.ClearanceDip:0.##} DIP; the assertion "
                + $"needs at least {ArcLabelFit.RequiredClearanceDip:0.#}.")
             .That(fit.ClearanceDip).IsGreaterOrEqualThan(ArcLabelFit.RequiredClearanceDip);
    }

    private static string Describe(string id, ArcLabelFitResult fit) =>
        $"{id}: face {fit.ResolvedFace}; ink {fit.LabelInkBox}; inner edge {fit.ArcInnerEdgeDip:0.#}; "
        + $"clearance {fit.ClearanceDip:0.##} DIP; overlap icon={fit.OverlapsIcon} symbol={fit.OverlapsSymbol}";

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
    /// never measured or arranged; a bare Canvas Measure/Arrange is all the labels need for
    /// <c>GetCharacterRect</c> to answer.
    /// </summary>
    private static void Layout(ModesMenuWindow window)
    {
        var canvas = (Canvas)window.FindName("ButtonCanvas");
        canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        canvas.Arrange(new Rect(new Point(0, 0), canvas.DesiredSize));
    }
}
