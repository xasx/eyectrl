// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
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

    [Fact]
    public void SetSurfaceScale_ScalesChoiceRadius_PracticeRadius_AndLatchReleaseMargin()
    {
        const double scale = 0.6;
        var vm = Vm();
        vm.SetSurfaceScale(scale);

        Check.That(vm.SurfaceScale).IsCloseTo(scale, 1e-9);

        vm.RegisterChoice("c_scaled", radiusDip: 100);
        vm.UpdateChoiceCentre("c_scaled", new PhysicalPoint(500, 500));
        vm.SetPracticeCentre(new PhysicalPoint(300, 300));
        vm.SetPracticeVisible(true);

        var chrome = vm.DetectorForTest.Targets.Single(t => t.Id == "c_scaled");
        var practice = vm.PracticeDetectorForTest.Targets.Single(t => t.Id == "guide_practice");
        var expectedChromeRadius = DpiHelper.LogicalToPhysicalX(100 * scale);
        var expectedPracticeRadius = DpiHelper.LogicalToPhysicalX(110 * scale);
        var expectedReleaseMargin = DpiHelper.LogicalToPhysicalX(SettingsWindowViewModel.LatchReleaseMarginDip * scale);

        Check.That(chrome.Radius).IsCloseTo(expectedChromeRadius, 1e-6);
        Check.That(practice.Radius).IsCloseTo(expectedPracticeRadius, 1e-6);

        // Gaze inside scaled radius + scaled release margin keeps the target held.
        var stillInsideRelease = 500 + expectedChromeRadius + expectedReleaseMargin - 2;
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(stillInsideRelease, 500), 10_000));
        Check.That(vm.ChoiceMayDwell("c_scaled")).IsFalse();

        // Gaze just past scaled radius + scaled release margin (well INSIDE the unscaled 100 + 35)
        // releases the latch, proving the hysteresis itself scaled with SurfaceScale.
        var justOutsideScaled = 500 + expectedChromeRadius + expectedReleaseMargin + 2;
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(justOutsideScaled, 500), 40_000));
        Check.That(vm.ChoiceMayDwell("c_scaled")).IsTrue();
    }

    [Fact]
    public void SetSurfaceScale_RescalesAlreadyRegisteredTargets_AndClampsAboveOne()
    {
        var vm = Vm();
        vm.RegisterChoice("c_live", radiusDip: 200);
        vm.UpdateChoiceCentre("c_live", new PhysicalPoint(600, 400));
        vm.SetPracticeCentre(new PhysicalPoint(300, 300));
        vm.SetPracticeVisible(true);

        vm.SetSurfaceScale(0.72);
        var chrome = vm.DetectorForTest.Targets.Single(t => t.Id == "c_live");
        var practice = vm.PracticeDetectorForTest.Targets.Single(t => t.Id == "guide_practice");
        Check.That(chrome.Radius).IsCloseTo(DpiHelper.LogicalToPhysicalX(200 * 0.72), 1e-6);
        Check.That(chrome.Center).IsEqualTo(new PhysicalPoint(600, 400));
        Check.That(practice.Radius).IsCloseTo(DpiHelper.LogicalToPhysicalX(110 * 0.72), 1e-6);

        // Above 1.0 clamps to 1.0 (never upscales past the 1920×1200 reference).
        vm.SetSurfaceScale(1.5);
        Check.That(vm.SurfaceScale).IsEqualTo(1.0);
        Check.That(vm.DetectorForTest.Targets.Single(t => t.Id == "c_live").Radius)
            .IsCloseTo(DpiHelper.LogicalToPhysicalX(200), 1e-6);

        // Non-positive and non-finite scales are rejected loudly.
        Check.ThatCode(() => vm.SetSurfaceScale(0)).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => vm.SetSurfaceScale(-0.5)).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => vm.SetSurfaceScale(double.NaN)).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => vm.SetSurfaceScale(double.PositiveInfinity)).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DisplayGeometryChangedMessage_RaisesEventForUsableGeometry_IgnoresUnusable_AndStopsAfterDispose()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new FirstRunGuideViewModel(messenger, dwellCooldownMs: 0,
            settings: new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };
        var received = new List<DisplayGeometry>();
        vm.DisplayGeometryChanged += g => received.Add(g);

        messenger.Send(new DisplayGeometryChangedMessage(DisplayGeometry.Unscaled));
        Check.That(received).IsEmpty();

        var usable = new DisplayGeometry(1920, 1080, 1.5, 1.5);
        messenger.Send(new DisplayGeometryChangedMessage(usable));
        Check.That(received).ContainsExactly(usable);

        vm.Dispose();
        messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1366, 768, 1.0, 1.0)));
        Check.That(received).HasSize(1);
    }
}
