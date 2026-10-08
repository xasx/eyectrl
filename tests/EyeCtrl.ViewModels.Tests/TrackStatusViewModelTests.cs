namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public class TrackStatusViewModelTests
{
    [Fact]
    public void Start_WithCapabilityNone_DoesNotShow()
    {
        var provider = new FakeTrackStatusProvider { Caps = GazeProviderCapabilities.None };
        using var vm = new TrackStatusViewModel(provider);

        vm.Start();

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Start_WithEyePositionCapability_ShowsOverlay()
    {
        var provider = new FakeTrackStatusProvider { Caps = GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose };
        using var vm = new TrackStatusViewModel(provider);

        vm.Start();

        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void Stop_HidesOverlay()
    {
        var provider = new FakeTrackStatusProvider { Caps = GazeProviderCapabilities.EyePosition };
        using var vm = new TrackStatusViewModel(provider);
        vm.Start();

        vm.Stop();

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void PollLatest_WithSample_UpdatesProperties()
    {
        var provider = new FakeTrackStatusProvider
        {
            Caps = GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose,
            Sample = new TrackStatusSample(
                Left: new EyeSample(0.3, 0.4, 0.5, true),
                Right: new EyeSample(0.7, 0.4, 0.5, true),
                HeadDistanceMm: 600, HeadDistanceValid: true,
                UserPresent: true, TimestampMicroseconds: 1000)
        };
        using var vm = new TrackStatusViewModel(provider);

        vm.PollLatest();

        Check.That(vm.LeftEyeX).IsEqualTo(0.3);
        Check.That(vm.RightEyeX).IsEqualTo(0.7);
        Check.That(vm.HeadDistanceMm).IsEqualTo(600.0);
        Check.That(vm.DistanceQuality).IsEqualTo(DistanceQuality.Good);
        Check.That(vm.UserPresent).IsTrue();
    }

    [Fact]
    public void PollLatest_WhenSampleBecomesUnavailable_ClearsToNoSignal()
    {
        var provider = new FakeTrackStatusProvider
        {
            Caps = GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose,
            Sample = new TrackStatusSample(
                Left: new EyeSample(0.3, 0.4, 0.5, true),
                Right: new EyeSample(0.7, 0.4, 0.5, true),
                HeadDistanceMm: 600, HeadDistanceValid: true,
                UserPresent: true, TimestampMicroseconds: 1000)
        };
        using var vm = new TrackStatusViewModel(provider);

        // A valid sample populates the display...
        vm.PollLatest();
        Check.That(vm.LeftEyeValid).IsTrue();
        Check.That(vm.DistanceQuality).IsEqualTo(DistanceQuality.Good);

        // ...then the tracker stops providing data (e.g. disconnect). The display must clear
        // to a no-signal state, not freeze on the last-known (misleadingly "live") values.
        provider.HasData = false;
        vm.PollLatest();

        Check.That(vm.LeftEyeValid).IsFalse();
        Check.That(vm.RightEyeValid).IsFalse();
        Check.That(vm.HeadDistanceValid).IsFalse();
        Check.That(vm.UserPresent).IsFalse();
        Check.That(vm.DistanceQuality).IsEqualTo(DistanceQuality.Unknown);
    }

    /// <summary>
    /// Pins every threshold edge exactly as <c>ComputeDistanceQuality</c> reads it: Good is
    /// 450 <= mm <= 750, Fair is 350 <= mm <= 900, Poor is outside both, and an invalid
    /// reading is Unknown. Moving any edge by 1 mm must fail the row that sits on it.
    /// </summary>
    /// <param name="distanceMm">The reported head distance in millimetres.</param>
    /// <param name="valid">Whether the reading is flagged valid.</param>
    /// <param name="expected">The quality that distance must produce.</param>
    [Theory]
    [InlineData(349.9, true, DistanceQuality.Poor)]
    [InlineData(350.0, true, DistanceQuality.Fair)]
    [InlineData(449.9, true, DistanceQuality.Fair)]
    [InlineData(450.0, true, DistanceQuality.Good)]
    [InlineData(750.0, true, DistanceQuality.Good)]
    [InlineData(750.1, true, DistanceQuality.Fair)]
    [InlineData(900.0, true, DistanceQuality.Fair)]
    [InlineData(900.1, true, DistanceQuality.Poor)]
    [InlineData(600.0, false, DistanceQuality.Unknown)]
    public void PollLatest_HeadDistance_ReadsTheExactThresholdEdges(double distanceMm, bool valid, DistanceQuality expected)
    {
        var provider = new FakeTrackStatusProvider
        {
            Caps = GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose,
            Sample = new TrackStatusSample(
                Left: new EyeSample(0.3, 0.4, 0.5, valid),
                Right: new EyeSample(0.7, 0.4, 0.5, valid),
                HeadDistanceMm: distanceMm, HeadDistanceValid: valid,
                UserPresent: true, TimestampMicroseconds: 1000)
        };
        using var vm = new TrackStatusViewModel(provider);

        vm.PollLatest();

        Check.That(vm.DistanceQuality).IsEqualTo(expected);
    }

    private sealed class FakeTrackStatusProvider : IGazeProvider
    {
        public GazeProviderCapabilities Caps { get; set; } = GazeProviderCapabilities.None;
        public TrackStatusSample Sample { get; set; }
        public bool HasData { get; set; } = true;
        public GazeProviderCapabilities Capabilities => Caps;
        public bool IsConnected => true;
#pragma warning disable CS0067
        public event Action<GazePoint>? GazePointReceived;
#pragma warning restore CS0067
        public bool TryGetLatestStatus(out TrackStatusSample sample)
        {
            sample = Sample;
            return HasData && Caps != GazeProviderCapabilities.None;
        }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
