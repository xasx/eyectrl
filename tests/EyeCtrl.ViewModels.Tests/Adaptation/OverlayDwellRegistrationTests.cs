namespace EyeCtrl.ViewModels.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

public class OverlayDwellRegistrationTests
{
    // internal sealed PARTIAL, matching the two existing probes in OverlayTelemetryWiringTests — a
    // private nested subclass of an ObservableObject-partial base crashes the MVVM source generator
    // with MSB6006 and NO per-file diagnostic (documented there; review round 1, blocker 5).
    internal sealed partial class TestOverlayViewModel : DwellOverlayViewModelBase
    {
        public TestOverlayViewModel(IMessenger messenger, IDwellDetectorRegistry? registry)
            : base(messenger, dwellCooldownMs: 0, DwellContext.Toolbar, sink: null, dwellRegistry: registry) { }
        protected override void OnTargetActivated(string targetId) { }
        protected override void OnTargetProgress(string targetId, double progress) { }
    }

    [Fact]
    public void Constructor_RegistersStandardRangeEntry_WithItsContext()
    {
        var registry = new DwellDetectorRegistry();
        var vm = new TestOverlayViewModel(new WeakReferenceMessenger(), registry);

        Check.That(registry.Registrations).HasSize(1);
        var entry = registry.Registrations[0];
        Check.That(entry.Context).IsEqualTo(DwellContext.Toolbar);
        Check.That(entry.MinDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMinDwellMs);
        Check.That(entry.MaxDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMaxDwellMs);
        Check.That(entry.ApplyGeometry).IsNotNull();
    }

    [Fact]
    public void Dispose_Unregisters()
    {
        var registry = new DwellDetectorRegistry();
        var vm = new TestOverlayViewModel(new WeakReferenceMessenger(), registry);
        Check.That(registry.Registrations).HasSize(1);

        vm.Dispose(); // MessengerViewModelBase.Dispose → OnDispose → Unregister

        Check.That(registry.Registrations).IsEmpty();
    }

    [Fact]
    public void Constructor_NullRegistry_NoThrow()
    {
        var vm = new TestOverlayViewModel(new WeakReferenceMessenger(), registry: null);
        Check.That(vm).IsNotNull();
    }
}
