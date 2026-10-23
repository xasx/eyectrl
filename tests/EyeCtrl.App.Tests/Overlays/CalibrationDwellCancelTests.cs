namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// A6's window half: the gaze Cancel target's dwell wiring. Gaze is fed through the real
/// <see cref="CalibrationViewModel.GazeSampleReceived"/> path (from a fake provider), which is the
/// production route: provider → OnRawGaze → window.
///
/// <para>
/// Most tests prepare the window with <see cref="InvisibleTestWindow.Prepare"/> and start the
/// session BEFORE attaching it, so they never drive the Collecting branch's <c>Show()</c>. The one
/// test that must exercise that branch — the pin on the production registration path — uses
/// <see cref="InvisibleTestWindow.PrepareCloaked"/> and skips if DWM cannot cloak the window here.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class CalibrationDwellCancelTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void FireGaze(GazePoint point) => GazePointReceived?.Invoke(point);
    }

    private sealed class CueProbe
    {
        public int Count { get; private set; }

        public void Attach(IMessenger messenger) =>
            messenger.Register<DwellActivationMessage>(this, (_, _) => Count++);
    }

    private static readonly PhysicalPoint Screen = new(1920, 1080);

    private static CalibrationViewModel CreateVm(
        IMessenger messenger, IGazeProvider provider, CalibrationService? service = null,
        bool start = true)
    {
        var vm = new CalibrationViewModel(
            new CalibrationSettings { PointCount = 3, TargetHoldMs = 200, SettleWindowMs = 0 },
            service ?? new CalibrationService(),
            messenger,
            provider,
            Screen,
            "test-monitor",
            Path.Combine(Path.GetTempPath(), "eyectrl-cal-dwell-" + Guid.NewGuid().ToString("N") + ".json"));
        if (start) messenger.Send(new CalibrationRequestedMessage());
        return vm;
    }

    private static CalibrationViewModel CreateStartedVm(
        IMessenger messenger, IGazeProvider provider, CalibrationService? service = null) =>
        CreateVm(messenger, provider, service);

    private static CalibrationWindow CreateWindow(
        int toolbarDwellMs, IMessenger messenger, bool cloaked = false)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        // Prepared before any show: the guard's requirement and the runtime backstop's contract.
        var window = new CalibrationWindow(
            new DwellSettings { ToolbarButtonDwellMs = toolbarDwellMs }, messenger);
        return cloaked
            ? InvisibleTestWindow.PrepareCloaked(window)
            : InvisibleTestWindow.Prepare(window);
    }

    [StaFact]
    public void ADwellOnCancel_WhileCollecting_CallsTheSameCancelPathAsEsc()
    {
        var messenger = new WeakReferenceMessenger();
        var provider = new FakeGazeProvider();
        var vm = CreateStartedVm(messenger, provider);
        var window = CreateWindow(toolbarDwellMs: 700, messenger);
        try
        {
            window.SetViewModel(vm);
            window.SyncCancelTarget(vm.State);
            Check.That(window.CancelDwellDetector.Targets).HasSize(1);

            var cues = new CueProbe();
            cues.Attach(messenger);

            var centre = vm.CancelTargetCenter;
            for (var i = 1; i <= 5; i++)
                provider.FireGaze(new GazePoint(centre.X, centre.Y, i * 100_000, IsValid: true));

            Check.WithCustomMessage("half the dwell must not cancel")
                .That(vm.State).IsEqualTo(CalibrationState.Collecting);
            Check.WithCustomMessage("the target must show its dwell progress")
                .That(window.CancelDwellOutline.Outline.StrokeThickness).IsStrictlyGreaterThan(0);
            Check.That(window.CancelDwellOutline.Outline.StrokeThickness)
                .IsLessOrEqualThan(EyeCtrl.Controls.DwellOutlineOverlay.MaxThickness);

            for (var i = 6; i <= 12; i++)
                provider.FireGaze(new GazePoint(centre.X, centre.Y, i * 100_000, IsValid: true));

            Check.WithCustomMessage("a completed dwell must end the session exactly as Esc does")
                .That(vm.State).IsEqualTo(CalibrationState.Idle);
            Check.That(window.CancelDwellDetector.Targets).IsEmpty();
            Check.That(window.CancelDwellButton.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.WithCustomMessage("one post-effect cue for the one activation")
                .That(cues.Count).IsEqualTo(1);
        }
        finally
        {
            vm.Cancel();
            window.Close();
        }
    }

    [StaFact]
    public void NoCancelTarget_ExistsOutsideCollecting()
    {
        var messenger = new WeakReferenceMessenger();
        var provider = new FakeGazeProvider();
        var vm = CreateStartedVm(messenger, provider);
        var window = CreateWindow(toolbarDwellMs: 700, messenger);
        try
        {
            window.SetViewModel(vm);
            window.SyncCancelTarget(CalibrationState.Idle);
            Check.WithCustomMessage("idle owns no cancel target")
                .That(window.CancelDwellDetector.Targets).IsEmpty();
            Check.That(window.CancelDwellButton.Visibility).IsEqualTo(Visibility.Collapsed);

            window.SyncCancelTarget(vm.State); // the session is Collecting
            Check.That(window.CancelDwellDetector.Targets).HasSize(1);
            Check.That(window.CancelDwellButton.Visibility).IsEqualTo(Visibility.Visible);

            vm.Cancel(); // the published Path: State -> Idle clears the target through the handler
            Check.That(vm.State).IsEqualTo(CalibrationState.Idle);
            Check.WithCustomMessage("leaving Collecting must take the target away")
                .That(window.CancelDwellDetector.Targets).IsEmpty();
            Check.That(window.CancelDwellButton.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally
        {
            vm.Cancel();
            window.Close();
        }
    }

    [StaFact]
    public void TheRegisteredTarget_IsTheDrawnDisc_AndCarriesTheViewModelsGeometry()
    {
        var messenger = new WeakReferenceMessenger();
        var provider = new FakeGazeProvider();
        var vm = CreateStartedVm(messenger, provider);
        var window = CreateWindow(toolbarDwellMs: 700, messenger);
        try
        {
            window.SetViewModel(vm);
            window.SyncCancelTarget(vm.State);

            var target = window.CancelDwellDetector.Targets.Single();
            Check.That(target.Id).IsEqualTo(CalibrationWindow.CancelTargetId);
            Check.That(target.Center).IsEqualTo(vm.CancelTargetCenter);
            Check.That(target.Radius).IsEqualTo(vm.CancelTargetRadiusPixels);

            var logical = vm.CancelTargetCenter.ToLogical();
            Check.That(Canvas.GetLeft(window.CancelDwellButton))
                .IsEqualTo(logical.X - CalibrationCancelPlacement.HitRadiusDip);
            Check.That(Canvas.GetTop(window.CancelDwellButton))
                .IsEqualTo(logical.Y - CalibrationCancelPlacement.HitRadiusDip);
            Check.WithCustomMessage("the disc is 2 × the hit radius across")
                .That(window.CancelDwellButton.Width).IsEqualTo(2 * CalibrationCancelPlacement.HitRadiusDip);
            Check.WithCustomMessage("the disc is a circle (a corner radius at least half its width)")
                .That(window.CancelDwellButton.CornerRadius.TopLeft)
                .IsGreaterOrEqualThan(CalibrationCancelPlacement.HitRadiusDip);

            Check.WithCustomMessage("the window keeps its click-through, so the target is gaze-only")
                .That(window.IsClickThrough).IsTrue();
            Check.WithCustomMessage("the disc itself does not become a mouse target")
                .That(window.CancelDwellButton.IsHitTestVisible).IsFalse();
        }
        finally
        {
            vm.Cancel();
            window.Close();
        }
    }

    [StaTheory]
    [InlineData(700, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(1500, 1500)]
    [InlineData(2000, 2000)]
    public void TheDwell_IsTheToolbarDwellFlooredAtOneSecond(int toolbarDwellMs, int expectedDwellMs)
    {
        var messenger = new WeakReferenceMessenger();
        var provider = new FakeGazeProvider();
        var vm = CreateStartedVm(messenger, provider);
        var window = CreateWindow(toolbarDwellMs, messenger);
        try
        {
            Check.That(window.CancelDwellMs).IsEqualTo(expectedDwellMs);

            window.SetViewModel(vm);
            window.SyncCancelTarget(vm.State);
            var target = window.CancelDwellDetector.Targets.Single();

            Check.WithCustomMessage($"toolbar {toolbarDwellMs} ms -> cancel dwell {expectedDwellMs} ms")
                .That(target.DwellTimeMicroseconds)
                .IsEqualTo(expectedDwellMs * 1000L);
        }
        finally
        {
            vm.Cancel();
            window.Close();
        }
    }

    [StaFact]
    public void GazeAwayFromCancel_NeverCancels()
    {
        var messenger = new WeakReferenceMessenger();
        var provider = new FakeGazeProvider();
        var vm = CreateStartedVm(messenger, provider);
        var window = CreateWindow(toolbarDwellMs: 700, messenger);
        try
        {
            window.SetViewModel(vm);
            window.SyncCancelTarget(vm.State);

            // The screen centre is far from this session's Cancel point and outside every target's
            // band, so 3 s of gaze there neither collects nor cancels.
            for (var i = 1; i <= 30; i++)
                provider.FireGaze(new GazePoint(Screen.X / 2, Screen.Y / 2, i * 100_000, IsValid: true));

            Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
            Check.That(window.CancelDwellDetector.Targets).HasSize(1);
        }
        finally
        {
            vm.Cancel();
            window.Close();
        }
    }

    [StaFact]
    public void TheDwellSurvives_ABlinkShorterThanTheCreditedGap()
    {
        // The detector's gap rule: a 100 ms silence between delivered samples is credited, so a blink
        // does not restart the cancel dwell. Four samples either side reach 800 ms across the gap.
        var messenger = new WeakReferenceMessenger();
        var provider = new FakeGazeProvider();
        var vm = CreateStartedVm(messenger, provider);
        var window = CreateWindow(toolbarDwellMs: 1000, messenger); // 1000 ms dwell
        try
        {
            window.SetViewModel(vm);
            window.SyncCancelTarget(vm.State);
            var centre = vm.CancelTargetCenter;

            // 100 ms cadence, with one 140 ms silence after sample 5 (under the detector's 150 ms
            // restart bound, so it is credited), then back on the disc until the 1000 ms dwell ends.
            long[] microseconds = [100_000, 200_000, 300_000, 400_000, 500_000, 640_000,
                740_000, 840_000, 940_000, 1_040_000, 1_140_000];
            foreach (var t in microseconds)
                provider.FireGaze(new GazePoint(centre.X, centre.Y, t, IsValid: true));

            Check.That(vm.State).IsEqualTo(CalibrationState.Idle);
        }
        finally
        {
            vm.Cancel();
            window.Close();
        }
    }

    /// <summary>
    /// Review 01, MEDIUM 1: the production hand-off of the live dwell settings. The mutant
    /// <c>new CalibrationWindow()</c> leaves the window on its 700 ms defaults, so a user whose
    /// toolbar dwell is 1500 ms would get the 1000 ms floor instead of max(1000, toolbar).
    /// </summary>
    [StaFact]
    public void TheOverlayManager_HandsTheLiveDwellSettingsToTheCalibrationWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = 1500;
        var window = InvisibleTestWindow.Prepare(OverlayManager.CreateCalibrationWindow(settings));
        try
        {
            Check.WithCustomMessage("a 1500 ms toolbar dwell must reach the window")
                .That(window.CancelDwellMs).IsEqualTo(1500);

            // The same object the Settings tab edits, not a snapshot taken at startup.
            settings.Dwell.ToolbarButtonDwellMs = 1800;
            Check.WithCustomMessage("the window must read the LIVE settings object")
                .That(window.CancelDwellMs).IsEqualTo(1800);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Review 01, MEDIUM 2: the ONLY production call that registers and draws the Cancel target
    /// lives in <c>UpdateVisualState(Collecting)</c>. This test enters Collecting through the real
    /// State route, with the window attached first, so the clear-only mutant (never register) goes
    /// red. The window is DWM-cloaked so the branch's Show()/Activate() paint nothing, and the test
    /// skips where DWM cannot cloak it.
    /// </summary>
    [StaFact]
    public void EnteringCollecting_ThroughThePublishedStateRoute_RegistersTheCancelTarget()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var provider = new FakeGazeProvider();
        var vm = CreateVm(messenger, provider, start: false);
        var window = CreateWindow(toolbarDwellMs: 1000, messenger, cloaked: true);
        try
        {
            Assert.SkipUnless(
                InvisibleTestWindow.IsCloaked(window) == true,
                "DWM did not cloak the window here; the Collecting branch calls Show().");
            window.SetViewModel(vm);
            Check.WithCustomMessage("nothing is registered before a session exists")
                .That(window.CancelDwellDetector.Targets).IsEmpty();

            // The production route: the view model's State change drives UpdateVisualState(Collecting).
            messenger.Send(new CalibrationRequestedMessage());

            Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
            var target = window.CancelDwellDetector.Targets.Single();
            Check.WithCustomMessage("the registration path must publish the geometry the gate uses")
                .That(target.Center).IsEqualTo(vm.CancelTargetCenter);
            Check.WithCustomMessage("the disc must be drawn")
                .That(window.CancelDwellButton.Visibility).IsEqualTo(Visibility.Visible);
        }
        finally
        {
            vm.Cancel();
            window.Close();
        }
    }

    /// <summary>
    /// Review 01, LOW 2: at 150 % on a 1080p display with three points the old top-centre placement
    /// covered the progress card. The window now measures the card (as the widest line the session
    /// can show) and hands it to the placement as a keep-out.
    /// </summary>
    [StaFact]
    public void At150PercentWithThreePoints_ThePlacementKeepsTheDiscOffTheProgressCard()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var previous = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        try
        {
            DpiHelper.Update(1.5, 1.5);
            var messenger = new WeakReferenceMessenger();
            var provider = new FakeGazeProvider();
            var vm = CreateStartedVm(messenger, provider); // radius is now 72 DIP × 1.5 = 108 px
            var window = CreateWindow(toolbarDwellMs: 700, messenger);
            try
            {
                // The VM's 1920x1080 physical screen is 1280x720 DIP at 150 %. The window's own
                // full-screen size comes from the real primary display, so pin it to the same box.
                window.Width = Screen.X / 1.5;
                window.Height = Screen.Y / 1.5;
                window.SetViewModel(vm);
                window.SyncCancelTarget(vm.State);

                var target = window.CancelDwellDetector.Targets.Single();
                var keepOut = window.LastCancelKeepOut;
                Check.That(keepOut).IsNotNull();
                Check.WithCustomMessage("the disc must clear the progress card and its clearance")
                    .That(DistanceToRect(target.Center, keepOut!.Value))
                    .IsGreaterOrEqualThan(target.Radius - 1e-6);

                // The regression that motivated the keep-out: without it the chosen point sits on
                // the card. If this ever stops colliding, the test above proves nothing.
                var provisional = CalibrationCancelPlacement.GetPosition(
                    vm.TargetPositions, vm.ScreenSize, target.Radius);
                Check.WithCustomMessage("the unobstructed placement must actually collide here")
                    .That(DistanceToRect(provisional, keepOut.Value))
                    .IsStrictlyLessThan(target.Radius);
            }
            finally
            {
                vm.Cancel();
                window.Close();
            }
        }
        finally
        {
            DpiHelper.Update(previous.Item1, previous.Item2);
        }
    }

    private static double DistanceToRect(PhysicalPoint point, PhysicalRect rect)
    {
        var dx = Math.Max(Math.Max(rect.X - point.X, 0), point.X - (rect.X + rect.Width));
        var dy = Math.Max(Math.Max(rect.Y - point.Y, 0), point.Y - (rect.Y + rect.Height));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
