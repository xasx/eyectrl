namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class InteractionManagerModifierTests
{
    private static readonly PhysicalPoint Point = new(100, 100);

    // OnActionSelected only raises in the two menu-visible states — Fixation resolves no
    // target point and returns. The menu is reached via the activation-button dwell.
    private static InteractionManager MenuVisible()
    {
        var im = new InteractionManager();
        im.OnFixationStarted(Point);
        im.OnActivationButtonDwelled(); // SelectionMode.None ⇒ ShowActionMenu, state ActionMenuVisible
        return im;
    }

    [Fact]
    public void OnActionSelected_RelaysModifiers_FromTheMenuState()
    {
        var im = MenuVisible();
        var mods = new HashSet<ModifierKey> { ModifierKey.Shift };
        (InteractionAction Action, IReadOnlySet<ModifierKey>? Modifiers)? raised = null;
        im.ActionRequested += (action, _, modifiers) => raised = (action, modifiers);

        im.OnActionSelected(InteractionAction.LeftClick, mods);

        Check.That(raised).IsNotNull();
        Check.That(raised!.Value.Action).IsEqualTo(InteractionAction.LeftClick);
        Check.That(raised.Value.Modifiers!.Contains(ModifierKey.Shift)).IsTrue();
    }

    [Fact]
    public void OnActionSelected_RelaysModifiers_FromTheManualSelectionMenuState()
    {
        var im = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        (InteractionAction Action, IReadOnlySet<ModifierKey>? Modifiers)? raised = null;
        im.ActionRequested += (action, _, modifiers) => raised = (action, modifiers);
        im.OnFixationStarted(Point);
        im.OnActivationButtonDwelled();      // ⇒ StartManualSelection, state ManualSelectionActive
        im.OnManualSelectionDwell(Point);    // ⇒ ManualSelectionActionMenuVisible

        im.OnActionSelected(InteractionAction.LeftClick, new HashSet<ModifierKey> { ModifierKey.Alt });

        Check.That(raised).IsNotNull();
        Check.That(raised!.Value.Modifiers!.Contains(ModifierKey.Alt)).IsTrue();
    }

    [Fact]
    public void RepeatModeTick_CarriesNoModifiers()
    {
        var im = new InteractionManager();
        IReadOnlySet<ModifierKey>? raised = null;
        var fired = false;
        im.ActionRequested += (_, _, modifiers) => { fired = true; raised = modifiers; };
        im.NoteGazeSample(isValid: true, timestampUs: 10_000); // liveness BEFORE entry (the file's precedent)
        im.EnterRepeatMode(InteractionAction.LeftClick);
        im.RepeatDwellMs = 0;
        im.OnFixationStarted(Point);           // arms the locked-action dwell at the point
        im.NoteGazeSample(isValid: true, timestampUs: 11_000); // elapsed 1 ms ≥ 0 ⇒ fires

        Check.That(fired).IsTrue();           // the tick really fired — no vacuous pass
        Check.That(raised).IsNull();          // repeat ticks carry null — never a modifier set
    }

    [Fact]
    public void Drag_WithNullModifiers_TransitionsUnchanged()
    {
        var im = MenuVisible();
        PhysicalPoint? dragStart = null;
        im.DragStarted += p => dragStart = p;

        im.OnActionSelected(InteractionAction.Drag);

        Check.That(dragStart).IsNotNull();    // the drag path is untouched by the new parameter
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Dragging);
    }
}
