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

    [Fact]
    public async Task FailedCalibration_SetsStateFailed_AndSendsFailedCompletion()
    {
        // A sensor stuck on a constant value makes every target's gaze identical, so
        // CalibrationService.CompleteSession rejects the calibration. The VM must surface that
        // as Failed + CalibrationCompletedMessage(false, null) — not a silent success.
        // (That message is sent only from CompleteSession's catch, so awaiting it pins this path.)
        _settings.SettleWindowMs = 0; // count samples as soon as they arrive

        CalibrationCompletedMessage? completion = null;
        var done = new TaskCompletionSource();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, m) =>
        {
            completion = m;
            done.TrySetResult();
        });

        // Create the VM off any test SynchronizationContext so its per-target continuations run
        // inline on the thread pool (deterministic), not pumped through xUnit's context.
        var vm = await Task.Run(CreateVm);

        // Stream constant gaze so each target collects identical samples regardless of the
        // async per-target timing. The collected gaze never varies → calibration is rejected.
        using var stop = new CancellationTokenSource();
        var firing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _provider.FireGaze(new GazePoint(1, 1, 0, true));
                await Task.Delay(2);
            }
        });

        _messenger.Send(new CalibrationRequestedMessage());

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        try { await firing; } catch (OperationCanceledException) { }

        Assert.NotNull(completion);
        Assert.False(completion!.Success);
        Assert.Null(completion.MeanErrorPixels);
        Assert.Equal(CalibrationState.Failed, vm.State);
    }
}
