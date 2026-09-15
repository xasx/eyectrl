namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The pause gate in <see cref="DwellOverlayViewModelBase"/>: while the interaction state is
/// Paused, dwell surfaces that inject OS input (keyboard, quick actions grid, task switcher)
/// must not accumulate dwell — input must not leave EyeCtrl while the user believes pause
/// released it to the OS. The safety surfaces (toolbar band, modes menu, edge bar, drag trail)
/// opt in via <c>DwellActiveWhilePaused</c> because their dwell is the way OUT of pause or
/// another stuck state.
/// </summary>
public class DwellOverlayPauseGateTests
{
    private static GazeCursorMovedMessage Sample(long timestampUs, bool paused) =>
        new(new PhysicalPoint(50, 50), timestampUs) { IsPaused = paused };

    [Fact]
    public void PausedSamples_DoNotAccumulateDwell_OnGatedSurfaces()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new TestDwellVm(messenger, dwellActiveWhilePaused: false);
        vm.IsVisible = true;
        vm.AddTargetAt("go");

        // A full dwell time of paused samples over the target's centre.
        messenger.Send(Sample(0, paused: true));
        messenger.Send(Sample(900_000, paused: true));

        Check.That(vm.Activated).IsEmpty();
    }

    [Fact]
    public void UnpausedSamples_AccumulateDwell_AsBefore()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new TestDwellVm(messenger, dwellActiveWhilePaused: false);
        vm.IsVisible = true;
        vm.AddTargetAt("go");

        messenger.Send(Sample(0, paused: false));
        messenger.Send(Sample(900_000, paused: false));

        Check.That(vm.Activated).ContainsExactly("go");
    }

    [Fact]
    public void PausedSamples_StillAccumulate_OnSurfacesThatOptedIn()
    {
        // The unpause affordances (toolbar band, modes menu, edge bar, drag trail) must keep
        // dwelling while paused, or pause becomes a one-way door for a gaze-only user.
        var messenger = new WeakReferenceMessenger();
        using var vm = new TestDwellVm(messenger, dwellActiveWhilePaused: true);
        vm.IsVisible = true;
        vm.AddTargetAt("go");

        messenger.Send(Sample(0, paused: true));
        messenger.Send(Sample(900_000, paused: true));

        Check.That(vm.Activated).ContainsExactly("go");
    }

    [Fact]
    public void HiddenSurfaces_NeverAccumulate_EvenWhenUnpaused()
    {
        // The pre-existing visibility gate must survive the new pause condition.
        var messenger = new WeakReferenceMessenger();
        using var vm = new TestDwellVm(messenger, dwellActiveWhilePaused: true);
        vm.IsVisible = false;
        vm.AddTargetAt("go");

        messenger.Send(Sample(0, paused: false));
        messenger.Send(Sample(900_000, paused: false));

        Check.That(vm.Activated).IsEmpty();
    }

    // internal (not private): ObservableRecipient's RegisterAll source generator emits an
    // extension method taking this type, which cannot be less accessible than its parameter.
    internal sealed class TestDwellVm : DwellOverlayViewModelBase
    {
        private readonly bool _dwellActiveWhilePaused;

        public TestDwellVm(IMessenger messenger, bool dwellActiveWhilePaused)
            : base(messenger, dwellCooldownMs: 0)
        {
            _dwellActiveWhilePaused = dwellActiveWhilePaused;
        }

        public List<string> Activated { get; } = new();

        protected override bool DwellActiveWhilePaused => _dwellActiveWhilePaused;
        protected override void OnTargetActivated(string targetId) => Activated.Add(targetId);
        protected override void OnTargetProgress(string targetId, double progress) { }

        public void AddTargetAt(string id) =>
            DwellDetector.AddTarget(new DwellTarget(id, new PhysicalRect(0, 0, 100, 100), dwellTimeMs: 800));
    }
}
