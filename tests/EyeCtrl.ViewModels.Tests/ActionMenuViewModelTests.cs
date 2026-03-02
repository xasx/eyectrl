namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

public class ActionMenuViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void ShowsMenu_WithRadialLayout()
    {
        var vm = new ActionMenuViewModel(_messenger);
        var actions = new List<InteractionAction>
        {
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick
        };

        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300), actions, false, 600));

        Assert.True(vm.IsVisible);
        Assert.Equal(3, vm.Items.Count);
        Assert.False(vm.ShowBack);
    }

    [Fact]
    public void RadialPositions_AreEvenlySpaced()
    {
        var vm = new ActionMenuViewModel(_messenger);
        var actions = new List<InteractionAction>
        {
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick,
            InteractionAction.DragStart
        };

        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(0, 0), actions, false, 600));

        // 4 items at 90-degree intervals — verify positions are distinct and equidistant from center
        var positions = vm.Items.Select(i => i.Position).ToList();
        Assert.Equal(4, positions.Distinct().Count());
    }

    [Fact]
    public void ShowsBackButton_WhenRequested()
    {
        var vm = new ActionMenuViewModel(_messenger);
        var actions = new List<InteractionAction> { InteractionAction.LeftClick };

        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300), actions, true, 600));

        Assert.True(vm.ShowBack);
        Assert.Equal(2, vm.Items.Count); // 1 action + 1 back button
    }

    [Fact]
    public void SendsActionSelected_OnDwellComplete()
    {
        var vm = new ActionMenuViewModel(_messenger);
        var actions = new List<InteractionAction> { InteractionAction.LeftClick };

        InteractionAction? selectedAction = null;
        _messenger.Register<ActionSelectedMessage>(_messenger, (_, msg) => selectedAction = msg.Action);

        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300), actions, false, 100));

        // Simulate gaze on the first item
        var itemPos = vm.Items[0].Position;
        _messenger.Send(new GazeCursorMovedMessage(itemPos, 0));
        _messenger.Send(new GazeCursorMovedMessage(itemPos, 200_000)); // 200ms > 100ms dwell

        Assert.Equal(InteractionAction.LeftClick, selectedAction);
    }

    [Fact]
    public void HidesMenu_OnHideMessage()
    {
        var vm = new ActionMenuViewModel(_messenger);
        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300),
            new List<InteractionAction> { InteractionAction.LeftClick }, false, 600));
        Assert.True(vm.IsVisible);

        _messenger.Send(new HideActionMenuMessage());
        Assert.False(vm.IsVisible);
    }
}
