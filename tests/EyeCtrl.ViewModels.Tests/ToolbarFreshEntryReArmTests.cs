namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

/// <summary>
/// T1's second surface: the toolbar's re-arm at task-switcher close. <c>SetActive(true)</c> used
/// to be a bare flag flip, and reopenings came 1.3–1.9 s after a close with the gaze still in the
/// toolbar band — a button under the resting gaze could fire again by itself. The switcher-close
/// call site now arms the same (A)+(B) landing guard with the gaze at the re-arm moment; every
/// other <c>SetActive(true)</c> caller keeps the old immediate arm.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ToolbarFreshEntryReArmTests
{
    private static readonly PhysicalPoint ButtonDip = new(1150, 60);
    private static readonly PhysicalPoint AwayDip = new(1150, 400);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void FreshEntryReArm_GazeRestingOnAButton_AccumulatesOnlyAfterLeaveAndReEntry(double scale)
    {
        using var h = new Harness(scale);

        // The switcher opened: the toolbar was disarmed, and the gaze stayed on the button
        // that opened it (the 2026-11 field log pattern).
        h.Vm.SetActive(false);
        h.Hold(h.Button, 0, 500_000);

        // The switcher closed: fresh-entry re-arm with the gaze at this moment on the button.
        h.Vm.SetActive(true, freshEntry: true);
        h.Hold(h.Button, 530_000, 3_000_000);

        Check.WithCustomMessage(
                "the button under the resting gaze must not accumulate after a fresh-entry re-arm")
            .That(h.Activated).IsEmpty();

        // Leave the button (> 50 DIP) and come back: a fresh entry, normal dwell.
        h.Hold(h.Away, 3_030_000, 3_300_000);
        h.Hold(h.Button, 3_330_000, 4_300_000);

        Check.That(h.Activated).Contains("TaskSwitcher");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void OtherReArmPaths_KeepTheirImmediateArm(double scale)
    {
        using var h = new Harness(scale);

        h.Vm.SetActive(false);
        h.Hold(h.Button, 0, 300_000);

        // Every other SetActive(true) caller (auto-hide show, calibration, modes-menu close)
        // must keep the old behaviour: dwell is live from the first sample.
        h.Vm.SetActive(true);
        h.Hold(h.Button, 330_000, 1_300_000);

        Check.That(h.Activated).Contains("TaskSwitcher");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void FreshEntryReArm_AnchorIsTheReArmSample_NotOnlyTheEntryGate(double scale)
    {
        using var h = new Harness(scale);

        h.Vm.SetActive(false);
        h.Hold(h.Button, 0, 300_000);
        h.Vm.SetActive(true, freshEntry: true);

        // A re-layout moves the button 65 DIP (its r60 face covers the gaze at its new left rim)
        // while the gaze has moved only 10 DIP, so (B) alone would call this an entry — the
        // previous sample sits outside the moved face. (A) is what keeps it inert: the gaze never
        // left the spot it had at re-arm.
        var moved = new PhysicalPoint(h.Button.X + 65 * scale, h.Button.Y);
        h.Vm.UpdateButtonScreenPosition("TaskSwitcher", moved);
        h.Hold(new PhysicalPoint(h.Button.X + 10 * scale, h.Button.Y), 330_000, 3_000_000);

        Check.WithCustomMessage(
                "a re-layout under the resting gaze must not be treated as a fresh entry")
            .That(h.Activated).IsEmpty();
    }

    private sealed class Harness : IDisposable
    {
        private readonly WeakReferenceMessenger _messenger = new();

        internal ToolbarViewModel Vm { get; }
        internal List<string> Activated { get; } = new();
        internal PhysicalPoint Button { get; }
        internal PhysicalPoint Away { get; }

        internal Harness(double scale)
        {
            DpiHelper.Initialize(scale, scale);
            Vm = new ToolbarViewModel(new EyeCtrlSettings(), _messenger);
            Vm.ButtonActivated += id => Activated.Add(id);

            Button = new PhysicalPoint(ButtonDip.X * scale, ButtonDip.Y * scale);
            Away = new PhysicalPoint(AwayDip.X * scale, AwayDip.Y * scale);
            Vm.UpdateButtonScreenPosition("TaskSwitcher", Button);
        }

        internal void Hold(PhysicalPoint p, long fromUs, long toUs) =>
            DwellDrive.Hold(_messenger, p, fromUs, toUs);

        public void Dispose()
        {
            Vm.Dispose();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }
}
