namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The action menu's right-edge buttons. Close is the point of this task: until 2026-08-26 the
/// menu was the one overlay context that put nothing on the bar, so an accidental open could only
/// be resolved by committing an action.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ActionMenuEdgeButtonsTests
{
    [Fact]
    public void TheBarCarries_CloseAndHelp()
    {
        var defs = ActionMenuEdgeButtons.Definitions(dwellMs: 600);

        Check.That(defs.Select(d => d.Id)).ContainsExactly("ActionMenu_close", "ActionMenu_help");
        Check.That(defs.Select(d => d.Icon)).ContainsExactly(ActionIcon.Close, ActionIcon.Help);
        // Icon-only since 2026-10-06: the names that used to sit under the icons survive as UIA
        // names (pinned on the built buttons by EdgeButtonFitTests).
        Check.That(defs.All(d => d.Label is null)).IsTrue();
        Check.That(defs.Select(d => d.AccessibleName)).ContainsExactly("Close", "Help");
    }

    [Fact]
    public void TheyUseTheManualSelectionSlots()
    {
        // Same two slots the manual-selection close/recapture pair occupies, so the bar's layout
        // is one convention rather than one per context.
        var defs = ActionMenuEdgeButtons.Definitions(dwellMs: 600);

        Check.That(defs[0].VerticalOffset).IsStrictlyLessThan(0);
        Check.That(defs[1].VerticalOffset).IsEqualTo(-defs[0].VerticalOffset);
        Check.That(defs.Select(d => d.RadiusLogical)).ContainsExactly(
            UIConstants.ButtonRadius, UIConstants.ButtonRadius);
    }

    [Fact]
    public void Close_GoesThroughTheStateMachine()
    {
        // NOT HideActionMenuMessage. That hides the overlay while InteractionManager stays in
        // ActionMenuVisible / ManualSelectionActionMenuVisible, so gaze does nothing until
        // something else resets the state — the menu is gone and the app is wedged. Back already
        // routes this way (GazeEngineAdapter maps BackSelectedMessage to OnManualSelectionBack);
        // Close takes the same shape.
        var messenger = new WeakReferenceMessenger();
        var dismissed = 0;
        messenger.Register<ActionMenuDismissedMessage>(this, (_, _) => dismissed++);

        ActionMenuEdgeButtons.Definitions(dwellMs: 600)
            .Single(d => d.Id == "ActionMenu_close").Activate(messenger);

        Check.That(dismissed).IsEqualTo(1);
    }

    [Fact]
    public void Help_SendsTheLabelToggle()
    {
        var messenger = new WeakReferenceMessenger();
        var toggled = 0;
        messenger.Register<ToggleActionMenuLabelsMessage>(this, (_, _) => toggled++);

        ActionMenuEdgeButtons.Definitions(dwellMs: 600)
            .Single(d => d.Id == "ActionMenu_help").Activate(messenger);

        Check.That(toggled).IsEqualTo(1);
    }

    [Fact]
    public void AMenuTeardown_MustNotStealTheBarFromADragInProgress()
    {
        // The edge bar is shared with the drag and scroll-mode contexts, and the menu's teardown
        // is DEFERRED while theirs are not: InteractionManager.OnActionSelected fires
        // HideActionMenu before DragStarted, but HideActionMenu goes through
        // GazeEngineAdapter.OnHideActionMenu's Dispatcher.BeginInvoke, so it arrives AFTER
        // DragCoordinator.OnDragStarted has already called ShowButtons.
        //
        // OverlayMessageCoordinator therefore hides the bar only while the menu still owns it,
        // and this pins the predicate it asks: once another context has taken the bar, the menu's
        // Close definition is gone and the teardown must not fire.
        var settings = new EyeCtrl.Core.Settings.EyeCtrlSettings();
        var bar = new EdgeButtonBarViewModel(settings, new WeakReferenceMessenger());
        var anchor = new PhysicalPoint(1800, 500);

        bar.ShowButtons(ActionMenuEdgeButtons.Definitions(dwellMs: 600), anchor);
        Check.WithCustomMessage("The menu should own the bar right after it opens")
             .That(bar.DefinitionFor(ActionMenuEdgeButtons.CloseId)).IsNotNull();

        // A drag takes the bar over, exactly as DragCoordinator.OnDragStarted does.
        bar.ShowButtons(
            new[]
            {
                new EdgeButtonDefinition("Drag_cancel", "✕", 600,
                    _ => { }, VerticalOffset: 30),
            },
            anchor);

        Check.WithCustomMessage("The drag's cancel button would be wiped by the menu's late "
                               + "teardown — the only way out of a drag for this user.")
             .That(bar.DefinitionFor(ActionMenuEdgeButtons.CloseId)).IsNull();
        Check.That(bar.DefinitionFor("Drag_cancel")).IsNotNull();
    }
}
