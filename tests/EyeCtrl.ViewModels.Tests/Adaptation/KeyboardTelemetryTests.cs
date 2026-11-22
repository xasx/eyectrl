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

    // 300, not 200: the keyboard family's floor (AdaptationDwellRanges.KeyboardMinDwellMs) clamps
    // any configured baseline below it, so a 200 here would silently become 300 and every sample
    // count in this file would be measuring the clamp rather than the telemetry.
    private static KeyboardViewModel MakeVm(RecordingSink sink, int keyDwellMs = 300, int cooldownMs = 400)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink);
        vm.Configure(keyDwellMs, cooldownMs);
        return vm;
    }

    /// <summary>
    /// Feeds a continuous stream (one sample per 50 ms) up to <paramref name="throughUs"/>. The
    /// tracker restarts a dwell whenever the stream stops for more than 150 ms (the F3 gap clamp),
    /// so sparse sample pairs no longer model a continuous hold.
    /// </summary>
    private static void HoldGaze(KeyboardViewModel vm, PhysicalPoint at, long throughUs)
    {
        for (long t = 50_000; t <= throughUs; t += 50_000)
            vm.ProcessKeyGaze(TwoKeys, at, isGazeInsideWindow: true, t);
    }

    [Fact]
    public void KeyDwellCompletes_EmitsActivated_TaggedKeyboard()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), isGazeInsideWindow: true, 0);
        HoldGaze(vm, new PhysicalPoint(100, 100), 300_000);

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
        // A 450 ms dwell so the pause's 300 ms expiry lands before B could fire; the switch itself
        // only PAUSES A (O3t), and the abandon is written once the pause expires.
        var sink = new RecordingSink();
        var vm = MakeVm(sink, keyDwellMs: 450);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);        // enter A
        for (var t = 150_000L; t <= 500_000; t += 50_000)
            vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, t);     // switch to B at 150 ms (A at 33 %)

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.NearestChanged);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("A");
    }

    [Fact]
    public void GazeLeavesKeyAreaAfterEnoughProgress_EmitsAbandoned_GazeLeft()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink, keyDwellMs: 450);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);          // enter A
        for (var t = 150_000L; t <= 500_000; t += 50_000)
            vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), false, t);     // off the key area at 33 %

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.GazeLeft);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("A");
    }

    [Fact]
    public void SwitchBelowThreshold_EmitsNothing()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink, keyDwellMs: 450);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);         // enter A
        for (var t = 30_000L; t <= 350_000; t += 50_000)
            vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, t);     // switch at ~7 % (< the abandon cut-off)

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void Cooldown_AfterActivation_NeverEmitsAbandoned()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink, keyDwellMs: 300, cooldownMs: 400);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);
        HoldGaze(vm, new PhysicalPoint(100, 100), 300_000);                       // activates A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 330_000);   // still within cooldown
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, 360_000);   // still within cooldown

        Check.That(sink.Outcomes.TrueForAll(o => o.Kind == OutcomeKind.Activated)).IsTrue();
        Check.That(sink.Outcomes).HasSize(1);
    }

    [Fact]
    public void NullSink_StillFiresKeyDwellCompleted_NeverThrows()
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger()); // no sink
        vm.Configure(300, 400);
        string? completed = null;
        vm.KeyDwellCompleted += id => completed = id;

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);
        HoldGaze(vm, new PhysicalPoint(100, 100), 300_000);

        Check.That(completed).IsEqualTo("A");
    }
}
