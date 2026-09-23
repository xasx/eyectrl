namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

public class DwellActivationMessagingTests
{
    // internal (not private): these implement/inherited IRecipient<T>, so the MVVM
    // IMessengerRegisterAllGenerator emits code referencing them and needs at least
    // internal visibility (same quirk as OverlayTelemetryWiringTests.ProbeOverlayViewModel).
    /// <summary>Records every DwellActivationMessage and what the VM's effect flag said at send time.</summary>
    internal sealed class CueProbe(Func<bool> effectFlagAtSend) : IRecipient<DwellActivationMessage>
    {
        public int Count { get; private set; }
        public List<bool> EffectRanAtSend { get; } = [];
        public void Receive(DwellActivationMessage message)
        {
            Count++;
            EffectRanAtSend.Add(effectFlagAtSend());
        }
    }

    internal class AnnouncingProbe(IMessenger messenger, Action effect) : DwellOverlayViewModelBase(messenger, dwellCooldownMs: 0)
    {
        public bool EffectRan { get; private set; }
        public void ArmAndLook()
        {
            DwellDetector.AddTarget(new DwellTarget("t1", new PhysicalPoint(50, 50), 30, 25));
            IsVisible = true;
            for (var i = 1; i <= 20; i++)
                Receive(new GazeCursorMovedMessage(new PhysicalPoint(50, 50), i * 2_000));
        }
        protected override void OnTargetActivated(string targetId) { EffectRan = true; effect(); }
        protected override void OnTargetProgress(string targetId, double progress) { }
    }

    internal sealed class SilentProbe(IMessenger messenger) : AnnouncingProbe(messenger, () => { })
    {
        protected override bool AnnounceDwellActivation => false;
    }

    [Fact]
    public void Activation_PublishesTheMessage_AfterTheEffectRan()
    {
        var messenger = new WeakReferenceMessenger();
        var effectRan = false;
        var vm = new AnnouncingProbe(messenger, () => effectRan = true);
        var probe = new CueProbe(() => effectRan);
        messenger.Register(probe);

        vm.ArmAndLook();

        Check.That(probe.Count).IsEqualTo(1);
        Check.That(probe.EffectRanAtSend.Count).IsEqualTo(1);
        Check.That(probe.EffectRanAtSend[0]).IsTrue();
    }

    [Fact]
    public void OptedOutSurface_PublishesNothing()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new SilentProbe(messenger);
        var probe = new CueProbe(() => true);
        messenger.Register(probe);

        vm.ArmAndLook();

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void DisposedViewModel_PublishesNothing()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new AnnouncingProbe(messenger, () => { });
        var probe = new CueProbe(() => true);
        messenger.Register(probe);
        ((IDisposable)vm).Dispose();

        vm.ArmAndLook();

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void TheAnnounceOptOutSet_IsExactlyTheTwoNoEffectSurfaces()
    {
        // DeclaredOnly: an override declares its own getter; the inherited default does not.
        // Forces any future opt-out to touch this pin consciously (spec Testing).
        var optOuts = typeof(DwellOverlayViewModelBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && typeof(DwellOverlayViewModelBase).IsAssignableFrom(t))
            .Where(t => t.GetMethod("get_AnnounceDwellActivation",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly) is not null)
            .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Check.That(string.Join(",", optOuts))
            .IsEqualTo("ManualSelectionOverlayViewModel,ScrollOverlayViewModel");
    }
}
