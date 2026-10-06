namespace EyeCtrl.App.Tests.Overlays;

using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The edge bar after the labels were dropped (user decision 2026-10-06): every icon-only edge
/// button's real drawn ink clears the arc at radius 50, each still exposes its UIA name through
/// <see cref="DwellArcButton.AutomationAnchor"/>, and the one labelled edge surface left — the
/// Shift/Ctrl/Alt modifier bar — keeps its label fit.
///
/// <para>
/// Every check measures the real window through <see cref="ArcLabelFit"/> (no <c>Show()</c>, no
/// HWND, no input). The class is a DpiHelper writer (the window's physical anchor conversion
/// reads the scale), so it lives in the exclusive App collection.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class EdgeButtonFitTests
{
    private readonly ITestOutputHelper _output;

    public EdgeButtonFitTests(ITestOutputHelper output)
    {
        _output = output;
        DpiHelper.Initialize(1.0, 1.0);
    }

    /// <summary>
    /// The icon-only edge buttons, at their production radius (<see cref="UIConstants.ButtonRadius"/>
    /// = 50): the real ink — flattened outlines, not box corners — stays at least +2 DIP inside
    /// the progress arc's inner edge.
    /// </summary>
    [StaTheory]
    [InlineData("ActionMenu Close")]
    [InlineData("ActionMenu Help")]
    [InlineData("Keyboard Close")]
    [InlineData("Keyboard Flip")]
    [InlineData("ManualSelection Close")]
    [InlineData("ManualSelection Recapture")]
    public void IconOnlyEdgeButtons_FitTheArcAtRadius50(string name)
    {
        var fit = ArcLabelFit.Measure(Definition(name));

        _output.WriteLine(Describe(name, fit));

        // Vacuity: these are exactly the icon-only buttons the redesign applies to.
        Check.That(fit.HasIcon).IsTrue();
        Check.That(fit.HasLabel).IsFalse();

        Check.WithCustomMessage(
                $"{name}: icon ink clears the arc's inner edge ({fit.ArcInnerEdgeDip:0.#} DIP) by "
                + $"{fit.IconClearanceDip:0.##} DIP; the assertion needs at least "
                + $"{ArcLabelFit.RequiredClearanceDip:0.#}.")
             .That(fit.IconClearanceDip).IsGreaterOrEqualThan(ArcLabelFit.RequiredClearanceDip);
    }

    /// <summary>
    /// The accessibility half of dropping the labels: an icon-only edge button still carries its
    /// <see cref="EyeCtrl.ViewModels.EdgeButtonDefinition.AccessibleName"/> on the invisible
    /// anchor, so UIA can name it.
    /// </summary>
    [StaTheory]
    [InlineData("ActionMenu Close", "Close")]
    [InlineData("ActionMenu Help", "Help")]
    [InlineData("Keyboard Close", "Close")]
    [InlineData("Keyboard Flip", "Flip")]
    [InlineData("ManualSelection Close", "Close")]
    [InlineData("ManualSelection Recapture", "Recapture")]
    public void IconOnlyEdgeButtons_ExposeTheirUiaName(string name, string expected)
    {
        var (button, _) = ArcLabelFit.BuildButton(Definition(name));

        Check.WithCustomMessage($"{name}: an icon-only button needs an automation anchor for UIA.")
             .That(button.AutomationAnchor).IsNotNull();
        Check.WithCustomMessage($"{name}: the anchor must publish '{expected}'.")
             .That(AutomationProperties.GetName(button.AutomationAnchor!)).IsEqualTo(expected);
    }

    /// <summary>
    /// The labelled surface that remains: the modifier bar's Shift/Ctrl/Alt (and their latched
    /// variants) keep their stacked labels, so each must still fit the arc and clear its icon.
    /// </summary>
    [StaTheory]
    [InlineData("Modifier Shift")]
    [InlineData("Modifier Ctrl")]
    [InlineData("Modifier Alt")]
    [InlineData("Modifier Shift latched")]
    [InlineData("Modifier Ctrl latched")]
    [InlineData("Modifier Alt latched")]
    public void EveryLabelledEdgeButton_FitsTheArcAndClearsItsIcon(string name)
    {
        var fit = ArcLabelFit.Measure(Definition(name));

        _output.WriteLine(Describe(name, fit));

        // Vacuity: if the fixture stopped labelling these buttons, the assertions below would be
        // about nothing.
        Check.That(fit.HasLabel).IsTrue();
        Check.That(fit.HasIcon).IsTrue();
        Check.That(fit.HasSymbol).IsFalse();

        Check.That(fit.OverlapsIcon).IsFalse();
        Check.That(fit.OverlapsSymbol).IsFalse();

        Check.WithCustomMessage(
                $"{name}: label ink {fit.LabelInkBox} clears the arc's inner edge "
                + $"({fit.ArcInnerEdgeDip:0.#} DIP) by {fit.ClearanceDip:0.##} DIP; the assertion "
                + $"needs at least {ArcLabelFit.RequiredClearanceDip:0.#}.")
             .That(fit.ClearanceDip).IsGreaterOrEqualThan(ArcLabelFit.RequiredClearanceDip);
    }

    private static string Describe(string name, ArcLabelFitResult fit) =>
        $"{name}: face {fit.ResolvedFace}; ink {fit.LabelInkBox}; inner edge {fit.ArcInnerEdgeDip:0.#}; "
        + $"label clearance {fit.ClearanceDip:0.##} DIP; icon clearance {fit.IconClearanceDip:0.##} DIP; "
        + $"overlap icon={fit.OverlapsIcon} symbol={fit.OverlapsSymbol}";

    private static EdgeButtonDefinition Definition(string name) => name switch
    {
        "ActionMenu Close" => ActionMenuEdgeButtons.Definitions(600)
            .Single(d => d.Id == ActionMenuEdgeButtons.CloseId),
        "ActionMenu Help" => ActionMenuEdgeButtons.Definitions(600)
            .Single(d => d.Id == ActionMenuEdgeButtons.HelpId),
        "Keyboard Close" => KeyboardEdgeButtons.Definitions(600)
            .Single(d => d.Id == KeyboardEdgeButtons.CloseId),
        "Keyboard Flip" => KeyboardEdgeButtons.Definitions(600)
            .Single(d => d.Id == KeyboardEdgeButtons.FlipId),
        "ManualSelection Close" => ManualSelectionEdgeButtons.Definitions(600)
            .Single(d => d.Id == ManualSelectionEdgeButtons.CloseId),
        "ManualSelection Recapture" => ManualSelectionEdgeButtons.Definitions(600)
            .Single(d => d.Id == ManualSelectionEdgeButtons.RecaptureId),
        "Modifier Shift" => Modifier("ActionMenu_mod_Shift", latched: false),
        "Modifier Ctrl" => Modifier("ActionMenu_mod_Ctrl", latched: false),
        "Modifier Alt" => Modifier("ActionMenu_mod_Alt", latched: false),
        "Modifier Shift latched" => Modifier("ActionMenu_mod_Shift", latched: true),
        "Modifier Ctrl latched" => Modifier("ActionMenu_mod_Ctrl", latched: true),
        "Modifier Alt latched" => Modifier("ActionMenu_mod_Alt", latched: true),
        _ => throw new KeyNotFoundException($"Unknown edge button '{name}'."),
    };

    private static EdgeButtonDefinition Modifier(string id, bool latched)
    {
        IReadOnlySet<ModifierKey> latchedSet = latched
            ? new HashSet<ModifierKey> { ModifierKey.Shift, ModifierKey.Ctrl, ModifierKey.Alt }
            : new HashSet<ModifierKey>();
        return ActionMenuModifierButtons.Definitions(600, latchedSet).Single(d => d.Id == id);
    }
}
