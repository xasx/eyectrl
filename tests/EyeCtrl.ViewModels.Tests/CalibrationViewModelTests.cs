namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class CalibrationViewModelTests
{
    private sealed class FakeGazeProviderForTests : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void FireGaze(GazePoint p) => GazePointReceived?.Invoke(p);
    }

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly CalibrationService _calibrationService = new();
    private readonly FakeGazeProviderForTests _provider = new();
    private readonly CalibrationSettings _settings = new() { PointCount = 3, TargetHoldMs = 200 };

    private CalibrationViewModel CreateVm()
    {
        return new CalibrationViewModel(
            _settings,
            _calibrationService,
            _messenger,
            _provider,
            new PhysicalPoint(1920, 1080),
            "test-monitor");
    }

    [Fact]
    public void ReceiveCalibrationRequested_StartsSession_StateBecomesCollecting()
    {
        var vm = CreateVm();
        _messenger.Send(new CalibrationRequestedMessage());
        Assert.Equal(CalibrationState.Collecting, vm.State);
        Assert.Equal(3, vm.TotalTargets);
        Assert.True(_calibrationService.IsSessionActive);
    }

    [Fact]
    public void Cancel_WhileCollecting_ResetsToIdle()
    {
        var vm = CreateVm();
        _messenger.Send(new CalibrationRequestedMessage());
        vm.Cancel();
        Assert.Equal(CalibrationState.Idle, vm.State);
        Assert.False(_calibrationService.IsSessionActive);
    }

    [Fact]
    public void ReceiveCalibrationRequested_WhenAlreadyCollecting_IgnoresSecond()
    {
        var vm = CreateVm();
        _messenger.Send(new CalibrationRequestedMessage());
        var targetCount = vm.TotalTargets;
        _messenger.Send(new CalibrationRequestedMessage());
        Assert.Equal(targetCount, vm.TotalTargets);
        Assert.Equal(CalibrationState.Collecting, vm.State);
    }
}
