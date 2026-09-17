namespace EyeCtrl.ViewModels.Tests;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The key tracker is <b>timestamp</b>-based (<c>KeyboardViewModel.cs:152</c>:
/// <c>elapsed = timestampUs - _nearestKeyEnteredTimestamp</c>), so withholding samples does not
/// stop its clock. <c>ToolbarLifecycleCoordinator.cs:118-124</c> withholds gap-held samples and
/// its comment states the intent — "a blink over a key would otherwise type it" — but withholding
/// is not a pause: on the first real sample after the gap, the ENTIRE gap is credited at once and
/// the key fires, with no progress animation leading up to it.
///
/// <para><b>These tests are RED on today's code, by design.</b> They are the bug report. The
/// measured behaviour they pin: dwell 450 ms, gap 449 ms → no activation (progress 1.00), gap
/// 451 ms → activation. Any eyes-closed interval longer than the dwell time types the key under
/// the gaze. A clamp on credited elapsed time is what turns them green.</para>
///
/// <para>Pure arithmetic — no WPF, no window, no DPI, 38 ms. Deliberately so: this defect is
/// immune to the DIP/physical fixture problem that made the gate bug invisible to 3623 tests, so
/// there is no excuse for it to be untested.</para>
/// </summary>
public class KeyboardGapCreditTests
{
    private const int DwellMs = 450;

    private static readonly Dictionary<string, PhysicalPoint> OneKey =
        new() { ["key_0_1"] = new PhysicalPoint(100, 100) };

    private static KeyboardViewModel NewVm(List<string> fired)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.KeyDwellCompleted += fired.Add;
        vm.Configure(keyDwellMs: DwellMs, cooldownMs: 400);
        return vm;
    }

    /// <summary>A blink long enough to exceed the dwell must not type the key under the gaze.</summary>
    [Fact]
    public void BlinkGapLongerThanDwell_DoesNotActivateTheKey()
    {
        var fired = new List<string>();
        var vm = NewVm(fired);

        // Gaze lands on the key. Zero dwell accumulated.
        vm.ProcessKeyGaze(OneKey, new PhysicalPoint(100, 100), true, 0);

        // A 3 s blink: the coordinator withholds every gap-held sample, so the keyboard receives
        // NOTHING. Then one real sample arrives with the same key still nearest.
        vm.ProcessKeyGaze(OneKey, new PhysicalPoint(100, 100), true, 3_000_000);

        Check.WithCustomMessage(
                "A 3 s blink with ZERO samples delivered typed the key. Withholding samples does "
                + "not stop a timestamp-based clock — the whole blink was credited on the first "
                + "sample after it, with no dwell progress ever shown.")
            .That(fired).IsEmpty();
    }

    /// <summary>Nothing resets the tracker on hide, so the hidden interval is credited on reopen.</summary>
    [Fact]
    public void HiddenInterval_DoesNotActivateAKeyOnReopen()
    {
        var fired = new List<string>();
        var vm = NewVm(fired);

        vm.ProcessKeyGaze(OneKey, new PhysicalPoint(100, 100), true, 0);
        vm.IsVisible = false;    // SetKeyboardVisible(false) -> Keyboard.Hide(); resets nothing
        vm.IsVisible = true;     // reopened 30 s later

        vm.ProcessKeyGaze(OneKey, new PhysicalPoint(100, 100), true, 30_000_000);

        Check.WithCustomMessage(
                "A key activated on the first sample after the keyboard was reopened: 30 s of "
                + "hidden time was credited as dwell. NearestKeyId and _nearestKeyEnteredTimestamp "
                + "survive the hide — the keyboard is the only dwell-owning surface that does not "
                + "reset on its context transition.")
            .That(fired).IsEmpty();
    }

    /// <summary>
    /// Pins the mechanism rather than one magic number: before the clamp the credit was the FULL
    /// gap, so the activation threshold was exactly the dwell time (449 ms → no fire, 451 ms →
    /// fire, 3 s → fire). After the clamp NO gap fires at any length — the pre-clamp expectation
    /// is kept in the message rather than in the assertion, so the bug report's measured
    /// characterisation still survives in the tree.
    /// </summary>
    [Theory]
    [InlineData(DwellMs - 1, false)]
    [InlineData(DwellMs + 1, true)]
    [InlineData(3000, true)]
    public void NoGapFires_HoweverLong(int gapMs, bool firedBeforeClamp)
    {
        var fired = new List<string>();
        var vm = NewVm(fired);
        vm.ProcessKeyGaze(OneKey, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(OneKey, new PhysicalPoint(100, 100), true, gapMs * 1000L);

        Check.WithCustomMessage(
                $"gap={gapMs} ms against a {DwellMs} ms dwell. Before the clamp this fired="
                + $"{firedBeforeClamp}, which is what made a blink indistinguishable from a dwell; "
                + "after it, no gap may fire at any length.")
            .That(fired.Count > 0).IsFalse();
    }
}
