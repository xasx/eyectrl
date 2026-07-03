namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels;

public class MessengerViewModelBaseTests
{
    internal sealed record PingMessage;

    internal sealed class TestVm : MessengerViewModelBase, IRecipient<PingMessage>
    {
        public int Received;
        public int Cleaned;
        public TestVm(IMessenger messenger) : base(messenger) { }
        public void Receive(PingMessage message) => Received++;
        protected override void OnDispose() => Cleaned++;
    }

    [Fact]
    public void Construction_RegistersAllRecipients()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestVm(messenger);

        messenger.Send(new PingMessage());

        Check.That(vm.Received).IsEqualTo(1);
    }

    [Fact]
    public void Dispose_Unregisters_AndIsIdempotent()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestVm(messenger);

        vm.Dispose();
        vm.Dispose();

        messenger.Send(new PingMessage());
        Check.That(vm.Received).IsEqualTo(0);
        Check.That(vm.Cleaned).IsEqualTo(1);
    }
}
