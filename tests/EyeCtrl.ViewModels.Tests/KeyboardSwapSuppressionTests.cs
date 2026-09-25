namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Spec D13 (spatial): after ANY sub-layout swap (manual or auto-return), while the gaze
/// stays within ~half a key width of its pre-swap position, nothing dwells. The radius comes
/// from the OLD grid's centres (post-swap centres are unarranged at the first samples and are
/// never consulted — plan review B1). The anchor is only set when the gaze was on the key
/// area (a prediction pick happens on the bar — its swap suppresses nothing — plan review
/// H3). Hiding clears the suppression.
/// </summary>
public class KeyboardSwapSuppressionTests
{
    private const int DwellMs = 400;

    private static readonly Dictionary<string, PhysicalPoint> Grid =
        new() { ["key_0_0"] = new(100, 100), ["key_0_1"] = new(300, 100) };

    private static KeyboardLayout Layout() => new()
    {
        Name = "T",
        DefaultSubLayout = "letters",
        SubLayouts =
        {
            ["letters"] = new SubLayout
            {
                Label = "ABC",
                Rows = new() { new() { new KeyDefinition { Label = "a", Output = "a" } } },
            },
            ["accents"] = new SubLayout
            {
                Label = "Acc",
                AutoReturn = true,
                Rows = new() { new() { new KeyDefinition { Label = "é", Output = "é", ShiftOutput = "É" } } },
            },
        },
    };

    private static KeyboardViewModel Vm(WeakReferenceMessenger? messenger = null)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), messenger ?? new WeakReferenceMessenger());
        vm.SetLayout(Layout());
        vm.Configure(DwellMs, 0);
        return vm;
    }

    private static List<string> RecordFires(KeyboardViewModel vm)
    {
        var fired = new List<string>();
        vm.KeyDwellCompleted += id => fired.Add(id);
        return fired;
    }

    private static void RestGaze(KeyboardViewModel vm, Dictionary<string, PhysicalPoint> keys,
        PhysicalPoint at, ref long ts, List<string> fired, string expectNoFireFor)
    {
        // Hold the gaze far past the dwell time; BREAK on any fire so the no-fire cases fail
        // loudly instead of double-firing through the zero cooldown (plan review B2).
        for (long t = ts; t < ts + 3_000_000; t += 50_000)
        {
            vm.ProcessKeyGaze(keys, at, true, t);
            Check.WithCustomMessage($"{expectNoFireFor} fired while resting")
                .That(fired).IsEmpty();
        }
        ts += 3_100_000;
    }

    [Fact]
    public void ManualSwap_RestingGaze_NeverDwells()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // gaze rests on key_0_0
        vm.SwitchSubLayout("accents");                                   // manual swap under the gaze

        long ts = 1_000;
        RestGaze(vm, Grid, new PhysicalPoint(100, 100), ref ts, fired, "the landing key");
    }

    [Fact]
    public void GazeBeyondRadius_DwellsNormally()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);
        vm.SwitchSubLayout("accents");

        // 200 units away = a full key pitch > the ~100-unit radius (half the pitch) — cleared.
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(300, 100), true, 1_000);
        long ts = 50_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(300, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_1");
    }

    [Fact]
    public void AutoReturnSwap_SuppressesTheLandingSpot()
    {
        var vm = Vm();
        vm.SwitchSubLayout("accents");
        var fired = RecordFires(vm);

        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // rest on key_0_0
        long ts = 50_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);
        Check.That(fired).ContainsExactly("key_0_0");

        vm.ActivateKey("é", "É");   // the window's role after KeyDwellCompleted → auto-return
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");

        // The letters grid's key_0_0 sits where accents' key_0_0 was (same positional id, same
        // spot): the resting gaze must not latch it (plan review B3's same-id case).
        ts += 100_000;
        for (long t = ts; t < ts + 3_000_000; t += 50_000)
        {
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);
            Check.WithCustomMessage("the letters key under the swap fired while resting")
                .That(fired).ContainsExactly("key_0_0");
        }
    }

    [Fact]
    public void RadiusDiscriminates_JitterStaysIn_OvershootClears()
    {
        // Plan-r2 I3: with the pitch at 200 units, the radius is 100 (half of d2=200²).
        // Jitter of 0.4 pitch (80 units) must stay suppressed; 0.6 pitch (120 units) must
        // clear. This is the pair that catches the 0.25*d2 → 0.25*d1 slip (d1 = 0 for a
        // gaze resting dead-centre — under that mutation the radius collapses to 0 and even
        // the 80-unit jitter clears, firing a key).
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // dead-centre anchor
        vm.SwitchSubLayout("accents");

        // 0.4 pitch of wobble, never leaving the radius: nothing fires.
        long ts = 1_000;
        for (long t = ts; t < ts + 2_000_000; t += 50_000)
        {
            var wobble = (t / 50_000) % 2 == 0 ? 80 : -80;
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100 + wobble, 100), true, t);
            Check.WithCustomMessage("jitter inside the radius fired a key")
                .That(fired).IsEmpty();
        }

        // 0.6 pitch away: past the radius — the nearest key (key_0_1) dwells normally.
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(220, 100), true, ts + 2_050_000);
        for (long t = ts + 2_100_000; t < ts + 4_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(220, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_1");
    }

    [Fact]
    public void UnarrangedPostSwapCentres_DoNotDefeatTheSuppression()
    {
        // Plan review B1: before WPF arranges the new rows, every centre reports the same
        // point. The spatial rule must not care — it never reads post-swap positions. The
        // gaze rests on key_0_1's spot: under the REV-1 key-latch design the collapsed dict
        // latched key_0_0 (first entry) and the suppression then cleared on the first
        // arranged sample, firing key_0_1 — resting here makes this test FAIL against that
        // bug (plan-r2 I4); the spatial rule keeps it suppressed either way.
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(300, 100), true, 0);   // rest on key_0_1
        vm.SwitchSubLayout("accents");

        var collapsed = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(0, 0), ["key_0_1"] = new(0, 0),
        };
        long ts = 1_000;
        for (long t = ts; t < ts + 1_000_000; t += 50_000)
        {
            vm.ProcessKeyGaze(collapsed, new PhysicalPoint(300, 100), true, t);
            Check.That(fired).IsEmpty();
        }

        // Real (arranged) centres arrive; the gaze is still resting — still nothing.
        RestGaze(vm, Grid, new PhysicalPoint(300, 100), ref ts, fired, "the landing key after arrangement");
    }

    [Fact]
    public void NoPriorGaze_NoSuppression()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.SwitchSubLayout("accents");   // swap with no in-window gaze yet: anchor stays null

        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);
        long ts = 50_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_0");   // normal dwell, nothing suppressed
    }

    [Fact]
    public void GazeLeavingTheKeyArea_BeforeASwap_NullsTheAnchor()
    {
        // Plan review H3: a prediction is picked with the gaze on the bar (outside the key
        // area). The next swap must not suppress the last key the user looked at.
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // gaze on the grid
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(500, 500), false, 50_000); // gaze on the bar: outside

        vm.SwitchSubLayout("accents");   // anchor must be null — gaze was not on the grid

        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 100_000);
        long ts = 150_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_0");   // the "next key the user wants" dwells
    }

    [Fact]
    public void HidingClearsTheSuppression()
    {
        // L5: a hide right after a swap must not carry the suppression into the next open.
        // Seam per KeyboardHideResetTests (:67-79, :105-109): the keyboard must be OPEN for
        // a hide to be a transition, and the production path is HideKeyboardMessage (a
        // direct IsVisible=false write from hidden does nothing) — plan-r2 re-opened L5.
        var messenger = new WeakReferenceMessenger();
        var vm = Vm(messenger);
        var fired = RecordFires(vm);
        vm.IsVisible = true;   // the keyboard must be OPEN for the hide to be a transition
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);
        vm.SwitchSubLayout("accents");

        messenger.Send(new HideKeyboardMessage());   // the production hide path
        Check.That(vm.IsVisible).IsFalse();

        vm.IsVisible = true;   // reopen
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 1_000);
        long ts = 50_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_0");   // fresh session: nothing suppressed
    }
}
