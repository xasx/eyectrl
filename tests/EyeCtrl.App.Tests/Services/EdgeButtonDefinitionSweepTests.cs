namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The headless sweep over EVERY <see cref="EdgeButtonDefinition"/> construction in src. Two
/// invariants, both of which the Recapture defect violated:
///
/// <list type="number">
/// <item><description>
/// A definition never combines a non-empty <c>Symbol</c> with a <c>Label</c>. The window passes
/// the symbol whenever the definition has no icon, next to the label — and
/// <c>DwellArcButton.Create</c> refuses that pair because the label would be drawn over the
/// symbol.
/// </description></item>
/// <item><description>
/// A definition with a <c>Label</c> has an <c>Icon</c>. The label is stacked under the icon; with
/// no icon it is centred on the button, where the symbol sits.
/// </description></item>
/// </list>
///
/// <para>
/// This is a CI guard, not a runtime guard: <c>EdgeButtonBarViewModel.Validate</c> is the runtime
/// one, and it fires at the caller. Here every set is built through its own factory, so a
/// regression fails in CI rather than on the user's screen — the consequence the ticket names.
/// </para>
///
/// <para>
/// The set list covers every construction in <c>src</c>: the three edge-bar pairs, the modifier
/// bar in all three latch states plus the <c>with</c> swap, and the drag/scroll/repeat stop
/// buttons. Adding a set means adding it here.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class EdgeButtonDefinitionSweepTests
{
    public EdgeButtonDefinitionSweepTests()
    {
        // The definitions convert their vertical offsets through DpiHelper, so the sweep is a
        // DpiHelper writer and belongs in its exclusive collection.
        DpiHelper.Initialize(1.0, 1.0);
    }

    [Fact]
    public void NoDefinition_PassesASymbolAndALabelToTheWindow()
    {
        foreach (var definition in EveryDefinition())
        {
            // The window forwards the symbol ONLY when the definition has no icon, and the label
            // whenever there is one — so a legacy symbol beside an icon (ManualSelection Close)
            // is harmless. The dangerous shape is: no icon, non-empty symbol, and a label.
            var windowWouldPassSymbol =
                definition.Icon is null && !string.IsNullOrEmpty(definition.Symbol);
            var windowWouldPassLabel = definition.Label is not null;

            Check.WithCustomMessage(
                    $"Edge-button definition '{definition.Id}' reaches DwellArcButton.Create with "
                    + $"Symbol '{definition.Symbol}' beside Label '{definition.Label}'. The label "
                    + "is drawn over the symbol and Create refuses the pair.")
                 .That(windowWouldPassSymbol && windowWouldPassLabel).IsFalse();
        }
    }

    [Fact]
    public void EveryLabelledDefinition_CarriesAnIcon()
    {
        foreach (var definition in EveryDefinition())
        {
            Check.WithCustomMessage(
                    $"Edge-button definition '{definition.Id}' has Label '{definition.Label}' but "
                    + "no Icon. A labelled button must carry a drawn icon: its label is stacked "
                    + "under the icon and is otherwise drawn over the symbol.")
                 .That(definition.Label is null || definition.Icon is not null).IsTrue();
        }
    }

    private static IEnumerable<EdgeButtonDefinition> EveryDefinition()
    {
        foreach (var definition in ActionMenuEdgeButtons.Definitions(dwellMs: 600))
            yield return definition;
        foreach (var definition in KeyboardEdgeButtons.Definitions(dwellMs: 600))
            yield return definition;
        foreach (var definition in ManualSelectionEdgeButtons.Definitions(dwellMs: 600))
            yield return definition;

        var noneLatched = new HashSet<ModifierKey>();
        var allLatched = new HashSet<ModifierKey> { ModifierKey.Shift, ModifierKey.Ctrl, ModifierKey.Alt };
        foreach (var definition in ActionMenuModifierButtons.Definitions(600, noneLatched))
        {
            yield return definition;
            // The latched swap is a `with` — the second construction shape this sweep must see.
            yield return ActionMenuModifierButtons.WithLatched(definition, latched: true);
            yield return ActionMenuModifierButtons.WithLatched(definition, latched: false);
        }
        foreach (var definition in ActionMenuModifierButtons.Definitions(600, allLatched))
            yield return definition;

        yield return DragCoordinator.CreateCancelButtonDefinition(dwellMs: 600);
        yield return DragCoordinator.CreateBackButtonDefinition(dwellMs: 600);
        yield return ScrollModeController.CreateScrollStopButtonDefinition(dwellMs: 600, verticalOffset: 0);
        yield return RepeatModeController.CreateRepeatStopButtonDefinition(dwellMs: 600, verticalOffset: 0);
    }
}
