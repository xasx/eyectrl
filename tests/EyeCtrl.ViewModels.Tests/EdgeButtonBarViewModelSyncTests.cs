namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (ViewModels)")]
public class EdgeButtonBarViewModelSyncTests
{
    private static EyeCtrlSettings TestSettings() => new();

    private static EdgeButtonBarViewModel CreateVm()
        => new(TestSettings(), new FakeMessenger());

    private static EdgeButtonDefinition Def(string id, double offset = 0) =>
        new(id, "X", 50, _ => { }, VerticalOffset: offset);

    [Fact]
    public void ShowButtons_ThreeDefinitions_FiresButtonSetChanged_Once()
    {
        var vm = CreateVm();
        int changes = 0;
        vm.ButtonSetChanged += () => changes++;

        vm.ShowButtons(new[] { Def("a"), Def("b"), Def("c") }, new PhysicalPoint(1000, 500));

        Check.That(changes).IsEqualTo(1);
        Check.That(vm.Buttons).HasSize(3);
    }

    [Fact]
    public void AddButton_FiresButtonSetChanged_Once()
    {
        var vm = CreateVm();
        vm.ShowButtons(new[] { Def("a") }, new PhysicalPoint(1000, 500));
        int changes = 0;
        vm.ButtonSetChanged += () => changes++;

        vm.AddButton(Def("b"));

        Check.That(changes).IsEqualTo(1);
        Check.That(vm.Buttons).HasSize(2);
    }

    [Fact]
    public void RemoveButton_FiresButtonSetChanged_Once()
    {
        var vm = CreateVm();
        vm.ShowButtons(new[] { Def("a"), Def("b") }, new PhysicalPoint(1000, 500));
        int changes = 0;
        vm.ButtonSetChanged += () => changes++;

        vm.RemoveButton("b");

        Check.That(changes).IsEqualTo(1);
        Check.That(vm.Buttons).HasSize(1);
    }

    [Fact]
    public void Hide_FiresButtonSetChanged_Once()
    {
        var vm = CreateVm();
        vm.ShowButtons(new[] { Def("a") }, new PhysicalPoint(1000, 500));
        int changes = 0;
        vm.ButtonSetChanged += () => changes++;

        vm.Hide();

        Check.That(changes).IsEqualTo(1);
        Check.That(vm.IsVisible).IsFalse();
    }

    private sealed class FakeMessenger : IMessenger
    {
        public void Send<TMessage>(TMessage message, string token) where TMessage : class { }
        public void Send<TMessage>(TMessage message) where TMessage : class { }
        public TMessage Send<TMessage, TToken>(TMessage message, TToken token)
            where TMessage : class where TToken : IEquatable<TToken> => message;

        public void RegisterAll(object recipient) { }
        public void RegisterAll(object recipient, string token) { }
        public void Register<TMessage>(object recipient, Action<TMessage> action)
            where TMessage : class { }
        public void Register<TMessage>(object recipient, string token, Action<TMessage> action)
            where TMessage : class { }
        public void Register<TRecipient, TMessage, TToken>(TRecipient recipient, TToken token, MessageHandler<TRecipient, TMessage> handler)
            where TRecipient : class where TMessage : class where TToken : IEquatable<TToken> { }
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
