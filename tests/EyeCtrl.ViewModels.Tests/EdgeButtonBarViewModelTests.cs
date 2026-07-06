namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class EdgeButtonBarViewModelTests
{
    private static EyeCtrlSettings TestSettings() => new();

    private static EdgeButtonBarViewModel CreateVm(out FakeMessenger messenger)
    {
        messenger = new FakeMessenger();
        var settings = TestSettings();
        return new EdgeButtonBarViewModel(settings, messenger);
    }

    private static EdgeButtonDefinition CancelDef(double offset = 30) =>
        new("Drag_cancel", "\u2715", 50, m => m.Send(new DragCancelMessage()), offset);

    [Fact]
    public void ShowButtons_AddsStatesAndSetsVisible()
    {
        var vm = CreateVm(out _);

        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Buttons).HasSize(1);
        Check.That(vm.Buttons[0].Id).IsEqualTo("Drag_cancel");
    }

    [Fact]
    public void ShowButtons_PropagatesPerButtonRadiusToState()
    {
        var vm = CreateVm(out _);
        var bigDef = new EdgeButtonDefinition(
            "ManualSelection_close", "✕", 50, m => m.Send(new ManualSelectionCloseMessage()),
            VerticalOffset: -60, RadiusLogical: 50);

        vm.ShowButtons(new[] { bigDef }, new PhysicalPoint(1000, 500));

        Check.That(vm.Buttons[0].RadiusLogical).IsEqualTo(50.0);
    }

    [Fact]
    public void CancelDef_UsesDefaultDragRadius()
    {
        var vm = CreateVm(out _);

        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        Check.That(vm.Buttons[0].RadiusLogical).IsEqualTo(UIConstants.DragButtonRadius);
    }

    [Fact]
    public void Hide_ClearsButtonsAndVisibility()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        vm.Hide();

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Buttons).IsEmpty();
    }

    [Fact]
    public void OnTargetActivated_InvokesActivateCallback()
    {
        var vm = CreateVm(out var messenger);
        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        vm.SimulateTargetActivated("Drag_cancel");

        Check.That(messenger.Sent.OfType<DragCancelMessage>()).HasSize(1);
    }

    [Fact]
    public void OnTargetProgress_UpdatesButtonState()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        vm.SimulateTargetProgress("Drag_cancel", 0.7);

        Check.That(vm.Buttons[0].DwellProgress).IsEqualTo(0.7);
    }

    [Fact]
    public void ShowButtons_SeedsTargetCenterFromAnchorPlusOffset()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef(offset: 30) },
                       new PhysicalPoint(1000, 500));

        var center = vm.GetTargetCenter("Drag_cancel");

        Check.That(center.Y).IsEqualTo(530.0);
        Check.That(center.X).IsEqualTo(1000.0);
    }

    [Fact]
    public void AddButton_DoesNotMoveSurvivingButton_PreservesProgress()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef(offset: 30) }, new PhysicalPoint(1000, 500));
        vm.SimulateTargetProgress("Drag_cancel", 0.4);
        var cancelCenterBefore = vm.GetTargetCenter("Drag_cancel");

        var back = new EdgeButtonDefinition("Drag_back", "\u21A9", 50,
            m => { }, VerticalOffset: -30);
        vm.AddButton(back);

        Check.That(vm.GetTargetCenter("Drag_cancel")).IsEqualTo(cancelCenterBefore);
        Check.That(vm.Buttons).HasSize(2);
        Check.That(vm.Buttons.Single(b => b.Id == "Drag_cancel").DwellProgress).IsEqualTo(0.4);
    }

    [Fact]
    public void RemoveButton_RemovesOnlyThatButton()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef(offset: 30) }, new PhysicalPoint(1000, 500));
        var back = new EdgeButtonDefinition("Drag_back", "\u21A9", 50, _ => { }, VerticalOffset: -30);
        vm.AddButton(back);

        vm.RemoveButton("Drag_back");

        Check.That(vm.Buttons.Select(b => b.Id)).ContainsExactly("Drag_cancel");
    }

    private sealed class FakeMessenger : IMessenger
    {
        public List<object> Sent { get; } = new();

        public void Send<TMessage>(TMessage message, string token) where TMessage : class
            => Sent.Add(message!);
        public void Send<TMessage>(TMessage message) where TMessage : class
            => Sent.Add(message!);
        public TMessage Send<TMessage, TToken>(TMessage message, TToken token)
            where TMessage : class where TToken : IEquatable<TToken>
        {
            Sent.Add(message!);
            return message;
        }

        public void RegisterAll(object recipient) { }
        public void RegisterAll(object recipient, string token) { }
        public void Register<TMessage>(object recipient, Action<TMessage> action)
            where TMessage : class { }
        public void Register<TMessage>(object recipient, string token, Action<TMessage> action)
            where TMessage : class { }
        public void Register<TRecipient, TMessage, TToken>(TRecipient recipient, TToken token, MessageHandler<TRecipient, TMessage> handler)
            where TRecipient : class where TMessage : class where TToken : IEquatable<TToken>
        {
        }
        public void UnregisterAll(object recipient) { }
        public void UnregisterAll(object recipient, string token) { }
        public void UnregisterAll<TToken>(object recipient, TToken token)
            where TToken : IEquatable<TToken> { }
        public void Unregister<TMessage>(object recipient) where TMessage : class { }
        public void Unregister<TMessage>(object recipient, string token) where TMessage : class { }
        public void Unregister<TMessage, TToken>(object recipient, TToken token)
            where TMessage : class where TToken : IEquatable<TToken> { }
        public bool IsRegistered<TMessage>(object recipient) where TMessage : class => false;
        public bool IsRegistered<TMessage>(object recipient, string token) where TMessage : class => false;
        public bool IsRegistered<TMessage, TToken>(object recipient, TToken token)
            where TMessage : class where TToken : IEquatable<TToken> => false;
        public void Cleanup() { }
        public void Reset() { }
    }
}
