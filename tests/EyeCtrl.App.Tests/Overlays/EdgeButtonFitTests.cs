namespace EyeCtrl.App.Tests.Overlays;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// Every labelled edge button, measured through <see cref="ArcLabelFit"/> against the real
/// window: Close, Help, Flip, the modifier keycaps, and Recapture — whose on-screen label is
/// "Retake".
///
/// <para>
/// <b>The Recapture word.</b> After its icon was drawn, the button's label no longer sat on the
/// glyph, but the old word "Recapture" still crossed the arc's inner edge when stacked under the
/// icon (measured at about -8.2 DIP, against the +2 rule). The user chose "Retake" (+4.5 DIP
/// measured); the dedicated test below asserts both rules for it, exactly like the theory does
/// for every other labelled button.
/// </para>
///
/// <para>
/// The class is a DpiHelper writer (the window's physical anchor conversion reads the scale), so
/// it lives in the exclusive App collection.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class EdgeButtonLabelFitTests
{
    private readonly ITestOutputHelper _output;

    public EdgeButtonLabelFitTests(ITestOutputHelper output)
    {
        _output = output;
        DpiHelper.Initialize(1.0, 1.0);
    }

    [StaTheory]
    [InlineData("ActionMenu Close")]
    [InlineData("ActionMenu Help")]
    [InlineData("Keyboard Close")]
    [InlineData("Keyboard Flip")]
    [InlineData("ManualSelection Close")]
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

    /// <summary>
    /// Recapture's half of the fit contract, split from the theory only because its label is the
    /// word that changed: "Retake" must both clear the arc by +2 DIP and keep off the icon, the
    /// two rules the old "Recapture" label failed (the second while drawn over the ↻ glyph, the
    /// first at about -8.2 DIP after the icon existed).
    /// </summary>
    [StaFact]
    public void Retake_FitsTheArcAndNoLongerDrawsOverTheIcon()
    {
        const string name = "ManualSelection Retake";
        var fit = ArcLabelFit.Measure(Definition(name));

        _output.WriteLine(Describe(name, fit));

        Check.That(fit.HasLabel).IsTrue();
        Check.That(fit.HasIcon).IsTrue();
        Check.That(fit.HasSymbol).IsFalse();

        // The overlap defect, pinned: the label's ink box intersecting the icon's (or a symbol's)
        // bounds fails here, whether or not it stays inside the progress arc.
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
        + $"clearance {fit.ClearanceDip:0.##} DIP; overlap icon={fit.OverlapsIcon} symbol={fit.OverlapsSymbol}";

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
        "ManualSelection Retake" => ManualSelectionEdgeButtons.Definitions(600)
            .Single(d => d.Id == ManualSelectionEdgeButtons.RecaptureId),
        "Modifier Shift" => Modifier("ActionMenu_mod_Shift", latched: false),
        "Modifier Ctrl" => Modifier("ActionMenu_mod_Ctrl", latched: false),
        "Modifier Alt" => Modifier("ActionMenu_mod_Alt", latched: false),
        "Modifier Shift latched" => Modifier("ActionMenu_mod_Shift", latched: true),
        "Modifier Ctrl latched" => Modifier("ActionMenu_mod_Ctrl", latched: true),
        "Modifier Alt latched" => Modifier("ActionMenu_mod_Alt", latched: true),
        _ => throw new KeyNotFoundException($"Unknown labelled edge button '{name}'."),
    };

    private static EdgeButtonDefinition Modifier(string id, bool latched)
    {
        IReadOnlySet<ModifierKey> latchedSet = latched
            ? new HashSet<ModifierKey> { ModifierKey.Shift, ModifierKey.Ctrl, ModifierKey.Alt }
            : new HashSet<ModifierKey>();
        return ActionMenuModifierButtons.Definitions(600, latchedSet).Single(d => d.Id == id);
    }
}
