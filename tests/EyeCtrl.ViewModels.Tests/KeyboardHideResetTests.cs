namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// T1b: hiding the keyboard must end its interaction context. The keyboard was the only
/// dwell-owning surface that did not reset on its context transition (the toolbar, settings,
/// note manager and manual-selection surfaces all reset), so two things survived a hide:
/// accumulated dwell — credited in full on the first sample after reopen (F3's second trigger) —
/// and <see cref="KeyboardViewModel.NearestKeyId"/>, which could latch a key from a layout the
/// next sample's positions no longer contain.
///
/// <para>
/// These tests deliberately do not rely on the gap clamp to cover for the reset. The hidden
/// interval in the discriminating test (100 ms) is SHORTER than the clamp's 150 ms threshold, so
/// only a real reset stops the pre-hide progress from being credited across it: 400 ms of gaze +
/// a 100 ms hide + one sample is exactly the 500 ms dwell under a tracker that keeps its state.
/// </para>
/// </summary>
public class KeyboardHideResetTests
{
    private const int DwellMs = 500;

    private static readonly Dictionary<string, PhysicalPoint> OneKey =
        new() { ["key_0_1"] = new PhysicalPoint(100, 100) };

    private static KeyboardViewModel NewVm(IMessenger messenger, List<string> fired)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), messenger);
        vm.KeyDwellCompleted += fired.Add;
        vm.Configure(keyDwellMs: DwellMs, cooldownMs: 400);
        return vm;
    }

    /// <summary>400 ms of continuous gaze, sampled every 50 ms so the gap clamp never restarts it.</summary>
    private static void Accumulate400ms(KeyboardViewModel vm, Dictionary<string, PhysicalPoint> keys)
    {
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        for (long t = 50_000; t <= 400_000; t += 50_000)
            vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, t);
    }

    [Fact]
    public void Hide_DiscardsAccumulatedDwell_EvenWhenTheHiddenIntervalIsUnderTheGapClamp()
    {
        var fired = new List<string>();
        var vm = NewVm(new WeakReferenceMessenger(), fired);
        var keys = OneKey;

        vm.IsVisible = true;    // the keyboard must be OPEN for a hide to be a transition
        Accumulate400ms(vm, keys);
        Check.WithCustomMessage(
                "The 400 ms of held gaze before the hide already completed a dwell — the rest of "
                + "this test cannot distinguish a reset from a tracker that simply never fired.")
            .That(fired).IsEmpty();

        vm.IsVisible = false;   // the hide; direct write so the reset cannot be message-only
        Check.WithCustomMessage(
                "Hiding did not clear NearestKeyId. A stale latch survives into the next open and "
                + "can fire against a key the next sample's positions may not even contain.")
            .That(vm.NearestKeyId).IsNull();
        vm.IsVisible = true;    // reopened 100 ms later

        // 500 ms after the dwell began, but the last pre-hide sample was at 400 ms: the 100 ms
        // hidden interval is UNDER the clamp threshold, so only the reset prevents the fire.
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 500_000);

        Check.WithCustomMessage(
                "A key fired across a hide. The pre-hide 400 ms and the 100 ms hidden interval were "
                + "added up to the 500 ms dwell, even though the hidden interval is too short for "
                + "the gap clamp to catch. Hiding must clear accumulated dwell, not pause it.")
            .That(fired).IsEmpty();
        Check.WithCustomMessage(
                "The tracker is dead after the hide — it should have re-latched the key on the "
                + "first sample after reopen, at zero progress.")
            .That(vm.NearestKeyId).IsEqualTo("key_0_1");
    }

    [Fact]
    public void HideMessage_AlsoResetsTheTracker()
    {
        var messenger = new WeakReferenceMessenger();
        var fired = new List<string>();
        var vm = NewVm(messenger, fired);

        messenger.Send(new ShowKeyboardMessage());
        Accumulate400ms(vm, OneKey);
        messenger.Send(new HideKeyboardMessage());

        Check.WithCustomMessage(
                "The production hide path (HideKeyboardMessage) did not reset the tracker. The "
                + "reset must not be reachable only by writing IsVisible directly.")
            .That(vm.NearestKeyId).IsNull();
    }
}
