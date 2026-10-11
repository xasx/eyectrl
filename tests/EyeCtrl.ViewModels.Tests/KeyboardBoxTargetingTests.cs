namespace EyeCtrl.ViewModels.Tests;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// <see cref="KeyboardViewModel.ProcessKeyGaze"/> with key boxes: the box decides WHICH key, and
/// nothing downstream changes. The geometry is a wide key next to a normal one — <c>wide</c> spans
/// x 0–240 (centre 120), <c>a</c> spans x 244–344 (centre 294), both y 0–100 — so a gaze at x 230
/// is inside <c>wide</c> but nearer <c>a</c>'s centre. Pure arithmetic, no WPF; the real window at
/// scale 2.0 is <c>KeyboardBoxTargetingProbeTests</c> in the App tests.
/// </summary>
public class KeyboardBoxTargetingTests
{
    private const int DwellMs = 450;
    private const long DwellUs = DwellMs * 1000L;

    private static readonly PhysicalRect WideBox = new(0, 0, 240, 100);
    private static readonly PhysicalRect ABox = new(244, 0, 100, 100);

    private static readonly Dictionary<string, PhysicalPoint> Centres =
        new() { ["wide"] = WideBox.Center, ["a"] = ABox.Center };

    private static readonly Dictionary<string, PhysicalRect> Boxes =
        new() { ["wide"] = WideBox, ["a"] = ABox };

    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    private static KeyboardViewModel NewVm(List<string> fired, IStruggleTelemetrySink? sink = null)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink);
        vm.KeyDwellCompleted += fired.Add;
        vm.Configure(keyDwellMs: DwellMs, cooldownMs: 400);
        return vm;
    }

    [Fact]
    public void GazeInsideTheWideKeyNearItsEdge_DwellsTheWideKey_AndTelemetryReportsItsCentre()
    {
        var fired = new List<string>();
        var sink = new RecordingSink();
        var vm = NewVm(fired, sink);
        var edge = new PhysicalPoint(230, 50);

        vm.ProcessKeyGaze(Centres, edge, true, 0, Boxes);
        Check.That(vm.NearestKeyId).IsEqualTo("wide");

        for (long t = 50_000; t < DwellUs; t += 50_000)
            vm.ProcessKeyGaze(Centres, edge, true, t, Boxes);
        vm.ProcessKeyGaze(Centres, edge, true, DwellUs, Boxes);

        Check.That(fired).ContainsExactly("wide");
        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("wide");
        // The CHOSEN key's centre, as before — not the box, not the gaze.
        Check.That(sink.Outcomes[0].TargetCenter).IsEqualTo(WideBox.Center);
    }

    /// <summary>
    /// The trust case: a gaze that wanders INSIDE the wide key, across the old Voronoi boundary
    /// (x 207 here), keeps one uninterrupted dwell. Under nearest-centre the wander to x 230 switched
    /// the latch to <c>a</c>, emitted no activation for <c>wide</c> and restarted the clock.
    /// </summary>
    [Fact]
    public void WanderAcrossTheOldVoronoiBoundary_InsideTheBox_DoesNotRestartTheDwell()
    {
        var fired = new List<string>();
        var vm = NewVm(fired);

        long t = 0;
        var flip = false;
        for (; t < DwellUs; t += 50_000, flip = !flip)
        {
            vm.ProcessKeyGaze(Centres, new PhysicalPoint(flip ? 230 : 150, 50), true, t, Boxes);
            Check.That(vm.NearestKeyId).IsEqualTo("wide");
        }
        vm.ProcessKeyGaze(Centres, new PhysicalPoint(230, 50), true, DwellUs, Boxes);

        Check.That(fired).ContainsExactly("wide");
    }

    [Fact]
    public void WithoutBoxes_TheSameGazeIsNearestCentre_AsBefore()
    {
        var vm = NewVm(new List<string>());
        vm.ProcessKeyGaze(Centres, new PhysicalPoint(230, 50), true, 0);
        Check.That(vm.NearestKeyId).IsEqualTo("a");
    }

    [Fact]
    public void GazeInTheGutter_StillLatchesAKey_ByNearestCentre()
    {
        var vm = NewVm(new List<string>());
        vm.ProcessKeyGaze(Centres, new PhysicalPoint(242, 50), true, 0, Boxes);
        Check.That(vm.NearestKeyId).IsEqualTo("a");
    }

    /// <summary>
    /// Leaving the wide key's box for <c>a</c>'s box changes the target and reports the abandon
    /// against the key that was being dwelled — the abandon path is unchanged.
    /// </summary>
    [Fact]
    public void MovingFromTheWideBoxIntoTheNeighbourBox_EmitsNearestChangedForTheWideKey()
    {
        var sink = new RecordingSink();
        var vm = NewVm(new List<string>(), sink);

        long t = 0;
        for (; t <= 300_000; t += 50_000)
            vm.ProcessKeyGaze(Centres, new PhysicalPoint(230, 50), true, t, Boxes);
        vm.ProcessKeyGaze(Centres, new PhysicalPoint(250, 50), true, t, Boxes);

        Check.That(vm.NearestKeyId).IsEqualTo("a");
        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.NearestChanged);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("wide");
        Check.That(sink.Outcomes[0].TargetCenter).IsEqualTo(WideBox.Center);
    }
}
