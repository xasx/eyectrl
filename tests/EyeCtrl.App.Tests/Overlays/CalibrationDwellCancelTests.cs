namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// A6's window half: the gaze Cancel target's dwell wiring. Headless throughout — the window is
/// prepared with <see cref="InvisibleTestWindow.Prepare"/> and the sessions are started BEFORE the
/// window is attached, so no test drives the Collecting path's <c>Show()</c>. Gaze is fed through
/// the real <see cref="CalibrationViewModel.GazeSampleReceived"/> path (from a fake provider), which
/// is the production route: provider → OnRawGaze → window.
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

    private static CalibrationViewModel CreateStartedVm(
        IMessenger messenger, IGazeProvider provider, CalibrationService? service = null)
    {
        var vm = new CalibrationViewModel(
            new CalibrationSettings { PointCount = 3, TargetHoldMs = 200, SettleWindowMs = 0 },
            service ?? new CalibrationService(),
            messenger,
            provider,
            Screen,
            "test-monitor",
            Path.Combine(Path.GetTempPath(), "eyectrl-cal-dwell-" + Guid.NewGuid().ToString("N") + ".json"));
        messenger.Send(new CalibrationRequestedMessage());
        return vm;
    }

    private static CalibrationWindow CreateWindow(int toolbarDwellMs, IMessenger messenger)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        // Prepared before any show (the window is never shown by these tests; the preparation is
        // the guard's requirement and the runtime backstop's contract).
        return InvisibleTestWindow.Prepare(
            new CalibrationWindow(new DwellSettings { ToolbarButtonDwellMs = toolbarDwellMs }, messenger));
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
}
