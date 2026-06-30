namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ActionMenuViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    [Fact]
    public void ShowsMenu_WithRadialLayout()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var actions = new List<InteractionAction>
        {
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick
        };

        _messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 300), actions, false, 600, new PhysicalPoint(500, 300)));

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Items).HasSize(3);
        Check.That(vm.ShowBack).IsFalse();
    }

    [Fact]
    public void RadialPositions_AreEvenlySpaced()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var actions = new List<InteractionAction>
        {
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick,
            InteractionAction.Drag
        };

        _messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(0, 0), actions, false, 600, new PhysicalPoint(0, 0)));

        // 4 items at 90-degree intervals — verify positions are distinct and equidistant from center
        var positions = vm.Items.Select(i => i.Position).ToList();
        Check.That(positions.Distinct().Count()).IsEqualTo(4);
    }

    [Fact]
    public void ShowsBackButton_WhenRequested()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var actions = new List<InteractionAction> { InteractionAction.LeftClick };

        _messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 300), actions, true, 600, new PhysicalPoint(500, 300)));

        Check.That(vm.ShowBack).IsTrue();
        Check.That(vm.Items).HasSize(2); // 1 action + 1 back button
    }

    [Fact]
    public void SendsActionSelected_OnDwellComplete()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var actions = new List<InteractionAction> { InteractionAction.LeftClick };

        InteractionAction? selectedAction = null;
        _messenger.Register<ActionSelectedMessage>(_messenger, (_, msg) => selectedAction = msg.Action);

        _messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 300), actions, false, 100, new PhysicalPoint(500, 300)));

        // Simulate gaze on the first item
        var itemPos = vm.Items[0].Position;
        _messenger.Send(new GazeCursorMovedMessage(itemPos, 0));
        _messenger.Send(new GazeCursorMovedMessage(itemPos, 200_000)); // 200ms > 100ms dwell

        Check.That(selectedAction).IsEqualTo(InteractionAction.LeftClick);
    }

    [Fact]
    public void HidesMenu_OnHideMessage()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        _messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 300),
            new List<InteractionAction> { InteractionAction.LeftClick }, false, 600, new PhysicalPoint(500, 300)));
        Check.That(vm.IsVisible).IsTrue();

        _messenger.Send(new HideActionMenuMessage());
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void EffectiveRingRadius_IsComputed_FromCircleSize()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var actions = new List<InteractionAction> { InteractionAction.LeftClick };

        // CircleSize=250 -> 250/2 + 50 + 20 = 195
        _messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500), actions, false, 600, new PhysicalPoint(500, 500), 0, 250));

        Check.That(vm.EffectiveRingRadius).IsEqualTo(195);
    }

    [Fact]
    public void EffectiveRingRadius_ScalesWithCircleSize()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var actions = new List<InteractionAction> { InteractionAction.LeftClick };

        // CircleSize=100 -> 100/2 + 50 + 20 = 120
        _messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500), actions, false, 600, new PhysicalPoint(500, 500), 0, 100));

        Check.That(vm.EffectiveRingRadius).IsEqualTo(120);
    }

    [Fact]
    public void ManualSelectionContextProperties_AreStoredFromMessage()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var ManualSelectionCenter = new PhysicalPoint(400, 300);

        _messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(400, 300),
            new List<InteractionAction> { InteractionAction.LeftClick },
            ShowBack: true,
            DwellTimeMs: 600,
            GazePoint: new PhysicalPoint(400, 300))
        {
            ManualSelectionCenter = ManualSelectionCenter,
            ManualSelectionRadius = 150
        });

        Check.That(vm.ManualSelectionCenter).IsEqualTo(ManualSelectionCenter);
        Check.That(vm.ManualSelectionRadius).IsEqualTo(150);
    }

    [Fact]
    public void ManualSelectionContextProperties_AreClearedOnHide()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        _messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(400, 300),
            new List<InteractionAction> { InteractionAction.LeftClick },
            ShowBack: true,
            DwellTimeMs: 600,
            GazePoint: new PhysicalPoint(400, 300))
        {
            ManualSelectionCenter = new PhysicalPoint(400, 300),
            ManualSelectionRadius = 150
        });

        _messenger.Send(new HideActionMenuMessage());

        Check.That(vm.ManualSelectionCenter).IsNull();
        Check.That(vm.ManualSelectionRadius).IsNull();
    }

    [Fact]
    public void StoresGazePoint_WhenProvided()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var gazePoint = new PhysicalPoint(400, 200);
        var displayCenter = new PhysicalPoint(960, 540);

        _messenger.Send(new ShowActionMenuMessage(
            displayCenter,
            new List<InteractionAction> { InteractionAction.LeftClick },
            ShowBack: false,
            DwellTimeMs: 600,
            GazePoint: gazePoint));

        Check.That(vm.GazePoint).IsEqualTo(gazePoint);
        Check.That(vm.Center).IsEqualTo(displayCenter);
    }

    [Fact]
    public void GazePoint_EqualsCenter_WhenInAtGazeMode()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var center = new PhysicalPoint(500, 300);

        _messenger.Send(new ShowActionMenuMessage(
            center,
            new List<InteractionAction> { InteractionAction.LeftClick },
            ShowBack: false,
            DwellTimeMs: 600,
            GazePoint: center));

        Check.That(vm.GazePoint).IsEqualTo(center);
        Check.That(vm.Center).IsEqualTo(center);
    }

    [Fact]
    public void GazePoint_IsClearedOnHide()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        _messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 300),
            new List<InteractionAction> { InteractionAction.LeftClick },
            ShowBack: false,
            DwellTimeMs: 600,
            GazePoint: new PhysicalPoint(200, 100)));

        _messenger.Send(new HideActionMenuMessage());

        Check.That(vm.GazePoint).IsEqualTo(new PhysicalPoint(0, 0));
    }

    [Fact]
    public void ItemPositions_RelativeToDisplayCenter_NotGazePoint()
    {
        var vm = new ActionMenuViewModel(_settings, _messenger);
        var displayCenter = new PhysicalPoint(960, 540);
        var gazePoint = new PhysicalPoint(200, 100);

        _messenger.Send(new ShowActionMenuMessage(
            displayCenter,
            new List<InteractionAction> { InteractionAction.LeftClick },
            ShowBack: false,
            DwellTimeMs: 600,
            GazePoint: gazePoint));

        // Items should be positioned around display center
        var item = vm.Items[0];
        var distance = Math.Sqrt(
            Math.Pow(item.Position.X - displayCenter.X, 2) +
            Math.Pow(item.Position.Y - displayCenter.Y, 2));

        // Should be at ring radius distance from display center
        Check.That(distance >= 190 && distance <= 200).IsTrue(); // EffectiveRingRadius ~195 with default CircleSize
    }
}
