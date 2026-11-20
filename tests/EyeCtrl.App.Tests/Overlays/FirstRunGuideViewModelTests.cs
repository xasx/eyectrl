// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

public class FirstRunGuideViewModelTests
{
    private static FirstRunGuideViewModel Vm() =>
        new(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };

    [Fact]
    public void DwellActiveWhilePaused_IsTrue()
    {
        Check.That(Vm().DwellActiveWhilePausedForTest).IsTrue();
    }

    [Fact]
    public void PracticeTarget_UsesActivationDwell_AndFeedsBothDetectors()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 300;
        settings.Dwell.ToolbarButtonDwellMs = 900;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        vm.SetPracticeCentre(new PhysicalPoint(280, 220));
        vm.SetPracticeVisible(true);

        var activated = new List<string>();
        vm.PracticeTargetActivated += () => activated.Add("practice");

        long t = 0;
        for (var i = 0; i < 12; i++)   // 300 ms at 30 ms samples — the ACTIVATION knob, not 900
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t));
            t += 30_000;
        }
        // The activation knob fired (the cooldown suppresses a re-fire inside the 360 ms window).
        Check.That(activated).Not.IsEmpty();
    }

    [Fact]
    public void Gaze_GatesOnIsInGap_NotOnIsPaused()
    {
        var vm = Vm();
        vm.SetPracticeCentre(new PhysicalPoint(280, 220));
        vm.SetPracticeVisible(true);
        var activated = new List<string>();
        vm.PracticeTargetActivated += () => activated.Add("p");

        long t = 0;
        for (var i = 0; i < 30; i++)
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t) { IsPaused = true });
            t += 30_000;
        }
        Check.That(activated).Not.IsEmpty();
    }

    [Fact]
    public void Latch_HoldsNewChoices_UntilGazeLeaves_CentrePushArms_OnePressDispatches()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = 300;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        var fired = new List<string>();
        vm.ChoiceActivated += id => fired.Add(id);
        vm.RegisterChoice("c_yes");                           // at origin, held (Settings pattern)
        Check.That(vm.ChoiceMayDwell("c_yes")).IsFalse();

        vm.UpdateChoiceCentre("c_yes", new PhysicalPoint(400, 300));
        Check.That(vm.ChoiceMayDwell("c_yes")).IsFalse();     // gaze never left it yet

        long t = 0;
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(900, 900), t)); t += 30_000;
        for (var i = 0; i < 11; i++)                          // 300 ms at 30 ms: arm + dwell + fire
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(400, 300), t));
            t += 30_000;
        }
        Check.That(fired).HasSize(1);                         // armed, dwelled, dispatched
        Check.That(vm.DetectorForTest.Targets).IsEmpty();     // T6 ledger pin: clear BEFORE dispatch —
                                                              // a re-render (or a step change) must re-register
    }

    [Fact]
    public void SetDwellTime_ReTimesThePracticeTarget()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 900;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        vm.SetPracticeCentre(new PhysicalPoint(280, 220));
        vm.SetPracticeVisible(true);

        vm.SetDwellTime(200);
        var activated = new List<string>();
        vm.PracticeTargetActivated += () => activated.Add("p");

        long t = 0;
        for (var i = 0; i < 8; i++)   // 240 ms — under the OLD 900, over the NEW 200
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t));
            t += 30_000;
        }
        Check.That(activated).Not.IsEmpty();
        Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(200);
    }

    [Fact]
    public void PracticeProgress_Rises_WhileGazeRests()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 300;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        vm.SetPracticeCentre(new PhysicalPoint(280, 220));
        vm.SetPracticeVisible(true);

        var progress = new List<double>();
        vm.PracticeProgress += p => progress.Add(p);

        long t = 0;
        for (var i = 0; i < 6; i++)
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t));
            t += 30_000;
        }
        Check.That(progress).Not.IsEmpty();
        Check.That(progress[^1]).IsStrictlyGreaterThan(0);
    }
}
