namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// A6's view-model half of the gaze Cancel target: the geometry it computes for the overlay, the
/// gaze stream it forwards to the overlay's detector, and the rule that samples inside the Cancel
/// hit area do not enter the active target's collection.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class CalibrationCancelTargetTests
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

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly FakeGazeProvider _provider = new();

    private static string TempCalibrationPath() =>
        Path.Combine(Path.GetTempPath(), "eyectrl-cal-cancel-" + Guid.NewGuid().ToString("N") + ".json");

    private CalibrationViewModel StartSession(
        PhysicalPoint screen, int pointCount, TimeProvider? timeProvider = null,
        CalibrationService? service = null, CalibrationSettings? settings = null)
    {
        settings ??= new CalibrationSettings
        {
            PointCount = pointCount,
            TargetHoldMs = 200,
            SettleWindowMs = 0,
            SamplesPerPoint = 5,
        };
        var vm = new CalibrationViewModel(
            settings, service ?? new CalibrationService(),
            _messenger, _provider, screen, "test-monitor", TempCalibrationPath(),
            timeProvider: timeProvider);
        _messenger.Send(new CalibrationRequestedMessage());
        return vm;
    }

    [Fact]
    public void TheGeometry_IsThePlacementHelperResult_WithTheRadiusFlowedFromTheDipFloor()
    {
        DpiHelper.Update(2.0, 2.0);
        var screen = new PhysicalPoint(1920, 1080);
        var vm = StartSession(screen, pointCount: 5);
        try
        {
            Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
            Check.That(vm.CancelTargetRadiusPixels)
                .IsEqualTo(CalibrationCancelPlacement.HitRadiusDip * 2.0);
            Check.That(vm.CancelTargetCenter).IsEqualTo(
                CalibrationCancelPlacement.GetPosition(vm.TargetPositions, screen, vm.CancelTargetRadiusPixels));

            // And the contract that matters: one radius in from every screen edge, radius + gap clear
            // of every target.
            var radius = vm.CancelTargetRadiusPixels;
            Check.That(vm.CancelTargetCenter.X).IsGreaterOrEqualThan(radius - 1e-6);
            Check.That(vm.CancelTargetCenter.X).IsLessOrEqualThan(screen.X - radius + 1e-6);
            Check.That(vm.CancelTargetCenter.Y).IsGreaterOrEqualThan(radius - 1e-6);
            Check.That(vm.CancelTargetCenter.Y).IsLessOrEqualThan(screen.Y - radius + 1e-6);
            var nearest = vm.TargetPositions.Min(p => Distance(vm.CancelTargetCenter, p));
            Check.That(nearest)
                .IsGreaterOrEqualThan(radius + CalibrationCancelPlacement.MinimumTargetGapPx - 1e-6);
        }
        finally
        {
            vm.Cancel();
            // The collection doc's rule: a writer restores the process-global scale, or the next
            // test in the assembly starts on someone else's 2.0.
            DpiHelper.Update(1.0, 1.0);
        }
    }

    [Fact]
    public void EveryValidSample_IsForwardedToTheOverlaysDwellFeed_InvalidSamplesAreNot()
    {
        var vm = StartSession(new PhysicalPoint(1920, 1080), pointCount: 3);
        try
        {
            var forwarded = new List<GazePoint>();
            vm.GazeSampleReceived += forwarded.Add;

            _provider.FireGaze(new GazePoint(10, 20, 1_000, IsValid: true));
            _provider.FireGaze(new GazePoint(0, 0, 2_000, IsValid: false));
            _provider.FireGaze(new GazePoint(30, 40, 3_000, IsValid: true));

            Check.That(forwarded).HasSize(2);
            Check.That(forwarded[0]).IsEqualTo(new GazePoint(10, 20, 1_000, IsValid: true));
            Check.That(forwarded[1]).IsEqualTo(new GazePoint(30, 40, 3_000, IsValid: true));
        }
        finally
        {
            vm.Cancel();
        }
    }

    [Fact]
    public void AFaultingDwellSubscriber_DoesNotEndTheSession()
    {
        var vm = StartSession(new PhysicalPoint(1920, 1080), pointCount: 3);
        try
        {
            vm.GazeSampleReceived += _ => throw new InvalidOperationException("the overlay broke");
            _provider.FireGaze(new GazePoint(100, 100, 1_000, IsValid: true));

            Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
        }
        finally
        {
            vm.Cancel();
        }
    }

    /// <summary>
    /// The ticket's "must not distort collection" rule, driven as behaviour rather than asserted
    /// as a predicate: on a small display the 16-target layout puts the Cancel disc close enough to
    /// a band that the two overlap. A sample in that overlap belongs to the way out, so the active
    /// target must collect NOTHING from it (the same sample outside the Cancel disc would arrive
    /// and count, which the premise check pins).
    /// </summary>
    [Fact]
    public void SamplesInsideTheCancelHitArea_AreExcludedFromTheActiveTargetsCollection()
    {
        var screen = new PhysicalPoint(1024, 768);
        var time = new FakeTimeProvider();
        var service = new CalibrationService(timeProvider: time);
        var settings = new CalibrationSettings
        {
            PointCount = 16,
            TargetHoldMs = 200,
            SettleWindowMs = 0,
            SamplesPerPoint = 5,
        };
        var vm = StartSession(screen, pointCount: 16, timeProvider: time, service: service, settings: settings);
        try
        {
            var cancel = vm.CancelTargetCenter;
            var radius = vm.CancelTargetRadiusPixels;
            var band = CalibrationGeometry.BandRadiusPx(screen);

            // The overlap can only be tested on the target whose band the Cancel disc reaches.
            var nearIndex = 0;
            var nearDistance = double.MaxValue;
            for (var i = 0; i < vm.TargetPositions.Count; i++)
            {
                var distance = Distance(cancel, vm.TargetPositions[i]);
                if (distance < nearDistance)
                {
                    nearDistance = distance;
                    nearIndex = i;
                }
            }

            Check.WithCustomMessage(
                    $"the premise needs the Cancel disc ({radius} px) to overlap the nearest band "
                    + $"({band} px): nearest target {nearDistance:0.#} px away")
                .That(nearDistance).IsStrictlyLessThan(radius + band);

            AdvanceToTarget(vm, service, time, settings, nearIndex);

            var target = vm.CurrentTargetPosition;
            var approach = Distance(cancel, target);
            Check.That(approach).IsStrictlyGreaterThan(0);

            // A point in BOTH circles: 1 px inside the band along the line to the Cancel centre.
            var fraction = (band - 1) / approach;
            var overlap = new PhysicalPoint(
                target.X + ((cancel.X - target.X) * fraction),
                target.Y + ((cancel.Y - target.Y) * fraction));
            Check.WithCustomMessage("the probe sample must lie inside the Cancel hit area")
                .That(Distance(cancel, overlap)).IsLessOrEqualThan(radius);
            Check.WithCustomMessage("the probe sample must lie inside the target's band")
                .That(Distance(target, overlap)).IsLessOrEqualThan(band);

            _provider.FireGaze(new GazePoint(overlap.X, overlap.Y, 0, IsValid: true));
            _provider.FireGaze(new GazePoint(overlap.X, overlap.Y, 0, IsValid: true));

            Check.WithCustomMessage(
                    "a sample in the Cancel hit area must not reach the collector, not even as an arrival")
                .That(service.HasArrivedAtTarget).IsFalse();
            Check.WithCustomMessage("the Cancel target must collect nothing")
                .That(service.CurrentTargetInBandSamples).IsEqualTo(0);
            Check.That(vm.CurrentTargetIndex).IsEqualTo(nearIndex);

            // Control: one pixel past the Cancel disc on the same line DOES reach the collector —
            // so the zero above is the exclusion, not a session that cannot collect at all.
            var outside = new PhysicalPoint(
                target.X + ((cancel.X - target.X) * ((radius + 4) / approach)),
                target.Y + ((cancel.Y - target.Y) * ((radius + 4) / approach)));
            Check.That(Distance(cancel, outside)).IsStrictlyGreaterThan(radius);
            Check.That(Distance(target, outside)).IsLessOrEqualThan(band);
            _provider.FireGaze(new GazePoint(outside.X, outside.Y, 0, IsValid: true));
            Check.That(service.HasArrivedAtTarget).IsTrue();
        }
        finally
        {
            vm.Cancel();
        }
    }

    [Fact]
    public void TheOverlaysRefinedGeometry_IsWhatTheCollectionGateExcludes()
    {
        var service = new CalibrationService();
        var vm = StartSession(new PhysicalPoint(1920, 1080), pointCount: 3, service: service);
        try
        {
            var target = vm.CurrentTargetPosition;
            var radius = vm.CancelTargetRadiusPixels;
            var away = vm.CancelTargetCenter;

            // The overlay publishes a refined centre once it has measured its progress card. The
            // gate must exclude the REFINED circle, not the provisional one.
            vm.SetCancelTarget(target, radius);
            Check.That(vm.CancelTargetCenter).IsEqualTo(target);
            _provider.FireGaze(new GazePoint(target.X, target.Y, 0, IsValid: true));
            Check.WithCustomMessage("a sample in the published Cancel circle must not reach the collector")
                .That(service.HasArrivedAtTarget).IsFalse();

            vm.SetCancelTarget(away, radius);
            _provider.FireGaze(new GazePoint(target.X, target.Y, 0, IsValid: true));
            Check.WithCustomMessage("with the circle away again the same sample is collected")
                .That(service.HasArrivedAtTarget).IsTrue();
        }
        finally
        {
            vm.Cancel();
        }
    }

    private void AdvanceToTarget(
        CalibrationViewModel vm, CalibrationService service, FakeTimeProvider time,
        CalibrationSettings settings, int targetIndex)
    {
        while (vm.CurrentTargetIndex < targetIndex)
        {
            var target = vm.CurrentTargetPosition;
            _provider.FireGaze(new GazePoint(target.X, target.Y, 0, IsValid: true)); // arrival
            time.Advance(TimeSpan.FromMilliseconds(settings.TargetHoldMs + 50));
            for (var i = 0; i < settings.SamplesPerPoint; i++)
                _provider.FireGaze(new GazePoint(target.X, target.Y, 0, IsValid: true));
        }

        Check.WithCustomMessage($"could not advance to target {targetIndex}")
            .That(vm.CurrentTargetIndex).IsEqualTo(targetIndex);
        Check.That(service.IsCollecting).IsTrue();
    }

    private static double Distance(PhysicalPoint a, PhysicalPoint b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
