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
        Check.That(vm.DistanceQuality).IsEqualTo("Good");
        Check.That(vm.UserPresent).IsTrue();
    }

    private sealed class FakeTrackStatusProvider : IGazeProvider
    {
        public GazeProviderCapabilities Caps { get; set; } = GazeProviderCapabilities.None;
        public TrackStatusSample Sample { get; set; }
        public GazeProviderCapabilities Capabilities => Caps;
        public bool IsConnected => true;
        public event Action<GazePoint>? GazePointReceived;
        public bool TryGetLatestStatus(out TrackStatusSample sample)
        {
            sample = Sample;
            return Caps != GazeProviderCapabilities.None;
        }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
