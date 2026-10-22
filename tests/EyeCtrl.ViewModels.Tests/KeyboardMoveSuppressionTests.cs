namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// C1: every window move that still happens while the keyboard is open (a yield to a toolbar,
/// a Flip, a DPI change) must arm the SAME D13 spatial suppression a sub-layout swap uses — a
/// resting gaze re-mapped onto another key must not dwell. The mechanism is shared
/// (<see cref="KeyboardViewModel.ArmDwellSuppression"/>), not a second one: the radius is still
/// half a key, measured against the pre-move centres, and the anchor is still the last gaze
/// point inside the key area.
/// </summary>
public class KeyboardMoveSuppressionTests
{
    private const int DwellMs = 400;

    // Pitch 200 units → the suppression radius is 100 (half of d2).
    private static readonly Dictionary<string, PhysicalPoint> Grid =
        new() { ["key_0_0"] = new(100, 100), ["key_0_1"] = new(300, 100) };

    private static KeyboardViewModel Vm()
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.Configure(DwellMs, 0);
        return vm;
    }

    private static List<string> RecordFires(KeyboardViewModel vm)
    {
        var fired = new List<string>();
        vm.KeyDwellCompleted += id => fired.Add(id);
        return fired;
    }

    [Fact]
    public void MoveSuppression_RestingGaze_NeverDwells()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // gaze rests on key_0_0
        vm.ArmDwellSuppression();                                        // the window moved under it

        // Hold far past the dwell; break on any fire so the failure is loud rather than a
        // double-fire through the zero cooldown.
        for (long t = 1_000; t < 3_001_000; t += 50_000)
        {
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);
            Check.WithCustomMessage("a key fired while the gaze rested where the move landed it")
                .That(fired).IsEmpty();
        }
    }

    [Fact]
    public void MoveSuppression_GazeBeyondHalfAKey_DwellsNormally()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);
        vm.ArmDwellSuppression();

        // 120 units away = 0.6 pitch > the 100-unit radius — the suppression clears and the
        // key under the new gaze dwells normally.
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(220, 100), true, 1_000);
        for (long t = 50_000; t < 2_050_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(220, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_1");
    }

    [Fact]
    public void MoveSuppression_JitterInsideHalfAKey_StillSuppresses()
    {
        // 0.4 pitch (80 units) of wobble must stay inside the radius — this is the pair that
        // catches a radius computed from d1 instead of d2 (a gaze resting dead-centre has
        // d1 = 0, so the radius would collapse and even the wobble would fire).
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);
        vm.ArmDwellSuppression();

        for (long t = 1_000; t < 2_001_000; t += 50_000)
        {
            var wobble = (t / 50_000) % 2 == 0 ? 80 : -80;
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100 + wobble, 100), true, t);
            Check.WithCustomMessage("jitter inside the suppression radius fired a key")
                .That(fired).IsEmpty();
        }
    }

    [Fact]
    public void MoveSuppression_NoGazeOnTheGrid_ArmsNothing()
    {
        // A move while the gaze is on the prediction bar (outside the key area) must not
        // suppress the last key the user looked at — same rule as the swap path (H3).
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(500, 500), false, 0);   // on the bar

        vm.ArmDwellSuppression();

        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 1_000);
        for (long t = 50_000; t < 2_050_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_0");
    }

    // ---- O9: a move keeps a live swap anchor only while the gaze is still INSIDE its radius ------------

    /// <summary>
    /// A swap arms an anchor at the gaze (<see cref="KeyboardViewModel.ArmDwellSuppression"/>), the gaze then
    /// travels during the key cooldown (which skips the clear check), and a window move calls
    /// <see cref="KeyboardViewModel.ArmDwellSuppressionForMove"/> with the gaze at <paramref name="restX"/>.
    /// The anchor's radius is 100 (half of d2 = 200). Just inside (99) and exactly on (100) the radius the
    /// anchor is KEPT, so it ends as soon as the gaze leaves its radius; just outside (101) it is stale and the
    /// move re-anchors at the resting gaze, which then dwells nothing.
    /// </summary>
    [Theory]
    [InlineData(199, true, 205)]    // 99 from the anchor: kept (the gaze then steps to 105 from it: dwells)
    [InlineData(200, true, 206)]    // exactly on the radius: kept (the clear check is strictly greater)
    [InlineData(201, false, 207)]   // 101 from the anchor: stale, re-anchored at the resting gaze
    public void AMoveInsideTheCooldown_KeepsALiveSwapAnchorOnlyWhileTheGazeIsInsideItsRadius(
        int restX, bool anchorKept, int stepX)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.Configure(DwellMs, cooldownMs: 2_000);
        var fired = RecordFires(vm);
        var grid = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100), ["key_0_1"] = new(300, 100),
        };

        // key_0_0 fires and starts the 2 s cooldown (until 2.4 s).
        for (long t = 0; t <= 500_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(grid, new PhysicalPoint(100, 100), true, t);
        Check.WithCustomMessage("arrange: the first key must fire to start the cooldown").That(fired).HasSize(1);

        vm.ArmDwellSuppression();                                              // the swap's anchor, at (100,100)
        vm.ProcessKeyGaze(grid, new PhysicalPoint(restX, 100), true, 600_000); // the gaze travels inside the cooldown
        vm.ArmDwellSuppressionForMove();                                       // the window moves under it

        if (anchorKept)
        {
            // The gaze steps just past the kept anchor's radius: the anchor clears and the key under the gaze
            // dwells normally. A move that re-anchored at the resting gaze instead would protect the step.
            for (long t = 2_500_000; t < 5_500_000 && fired.Count == 1; t += 50_000)
                vm.ProcessKeyGaze(grid, new PhysicalPoint(stepX, 100), true, t);
            Check.WithCustomMessage("the live anchor was dropped by the move: the gaze that left its radius was still held")
                .That(fired).ContainsExactly("key_0_0", "key_0_1");
        }
        else
        {
            // The stale anchor was replaced: the resting gaze is protected where the move landed it.
            for (long t = 2_500_000; t < 5_500_000; t += 50_000)
            {
                vm.ProcessKeyGaze(grid, new PhysicalPoint(restX, 100), true, t);
                Check.WithCustomMessage("a stale anchor survived the move: a key fired under the resting gaze")
                    .That(fired).HasSize(1);
            }
        }
    }
}
