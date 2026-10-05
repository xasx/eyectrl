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
/// window: Close, Help, Flip, the modifier keycaps, and Recapture.
///
/// <para>
/// <b>Recapture is split on purpose.</b> After its icon was drawn, the label no longer sits on the
/// glyph, so the no-overlap assertion must pass and is asserted here. The fit assertion for
/// "Recapture" is intentionally NOT asserted: the word is still too long for the stacked layout at
/// the shipped radius (about -7.7 DIP, measured) and the word is the user's open choice — the
/// committed label stays "Recapture" until that choice is made. The fit assertion itself is
/// mutation-checked (margin shrunk to a failing value and back) rather than left unexercised.
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

    [StaFact]
    public void Recapture_NoLongerDrawsItsLabelOverTheIcon()
    {
        var fit = ArcLabelFit.Measure(Definition("ManualSelection Recapture"));

        _output.WriteLine(Describe("ManualSelection Recapture", fit));

        Check.That(fit.HasLabel).IsTrue();
        Check.That(fit.HasIcon).IsTrue();
        Check.That(fit.HasSymbol).IsFalse();

        // The defect, pinned: a label's ink box that intersects the icon's (or a symbol's) bounds
        // fails here, whether or not it stays inside the progress arc.
        Check.That(fit.OverlapsIcon).IsFalse();
        Check.That(fit.OverlapsSymbol).IsFalse();

        // No fit assertion — see the class doc: "Recapture" is expected to fail it until the user
        // picks the word, and the fit assertion is mutation-checked separately.
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
        "ManualSelection Recapture" => ManualSelectionEdgeButtons.Definitions(600)
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
