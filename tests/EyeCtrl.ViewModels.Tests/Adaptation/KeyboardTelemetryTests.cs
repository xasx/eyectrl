namespace EyeCtrl.ViewModels.Tests.Adaptation;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

public class KeyboardTelemetryTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    private static readonly Dictionary<string, PhysicalPoint> TwoKeys = new()
    {
        ["A"] = new PhysicalPoint(100, 100),
        ["B"] = new PhysicalPoint(400, 100),
    };

    private static KeyboardViewModel MakeVm(RecordingSink sink, int keyDwellMs = 200, int cooldownMs = 400)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink);
        vm.Configure(keyDwellMs, cooldownMs);
        return vm;
    }

    [Fact]
    public void KeyDwellCompletes_EmitsActivated_TaggedKeyboard()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), isGazeInsideWindow: true, 0);
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), isGazeInsideWindow: true, 200_000);

        Check.That(sink.Outcomes).HasSize(1);
        var o = sink.Outcomes[0];
        Check.That(o.Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(o.Context).IsEqualTo(DwellContext.Keyboard);
        Check.That(o.TargetId).IsEqualTo("A");
        Check.That(o.TargetCenter).IsEqualTo(new PhysicalPoint(100, 100));
    }

    [Fact]
    public void NearestKeySwitchesAfterEnoughProgress_EmitsAbandoned_NearestChanged()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);        // enter A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, 100_000);  // switch to B (A at 50%)

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.NearestChanged);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("A");
    }

    [Fact]
    public void GazeLeavesKeyAreaAfterEnoughProgress_EmitsAbandoned_GazeLeft()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);          // enter A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), false, 100_000);   // gaze leaves the key area

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.GazeLeft);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("A");
    }

    [Fact]
    public void SwitchBelowThreshold_EmitsNothing()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);         // enter A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, 30_000);    // switch at ~15% (<0.3)

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void Cooldown_AfterActivation_NeverEmitsAbandoned()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink, keyDwellMs: 200, cooldownMs: 400);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 200_000);   // activates A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 230_000);   // still within cooldown
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, 260_000);   // still within cooldown

        Check.That(sink.Outcomes.TrueForAll(o => o.Kind == OutcomeKind.Activated)).IsTrue();
        Check.That(sink.Outcomes).HasSize(1);
    }

    [Fact]
    public void NullSink_StillFiresKeyDwellCompleted_NeverThrows()
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger()); // no sink
        vm.Configure(200, 400);
        string? completed = null;
        vm.KeyDwellCompleted += id => completed = id;

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 200_000);

        Check.That(completed).IsEqualTo("A");
    }
}
