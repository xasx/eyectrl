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

    /// <summary>
    /// A throwaway calibration path. The constructor argument became required when the
    /// <c>%APPDATA%</c> fallback was removed — which means these tests were
    /// previously pointed at the LIVE <c>%APPDATA%\EyeCtrl\calibration.json</c>, the file whose
    /// corruption silently breaks dwell everywhere. Per-call GUID so nothing can collide.
    /// </summary>
    private static string TempCalibrationPath() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "eyectrl-cal-" + Guid.NewGuid().ToString("N") + ".json");

    private CalibrationViewModel CreateVm()
    {
        return new CalibrationViewModel(
            _settings,
            _calibrationService,
            _messenger,
            _provider,
            new PhysicalPoint(1920, 1080),
            "test-monitor",
            TempCalibrationPath());
    }

    [Fact]
    public void MonitorHardwareId_IsNotResolvedAtConstruction()
    {
        // The view model is an app-lifetime singleton, so a hardware id read here names whichever
        // monitor was primary at startup. CalibrationService.Load prefers the id over the device
        // name, so persisting a stale one rejects a good calibration on the next launch — the exact
        // failure the id was added to prevent. It must be read when the session completes instead.
        int resolutions = 0;

        var vm = new CalibrationViewModel(
            _settings,
            _calibrationService,
            _messenger,
            _provider,
            new PhysicalPoint(1920, 1080),
            "test-monitor",
            calibrationFilePath: TempCalibrationPath(),
            monitorHardwareId: () => { resolutions++; return "AUOC5AC"; });

        Check.That(vm).IsNotNull();
        Check.That(resolutions).IsEqualTo(0);
    }

    [Fact]
    public void ReceiveCalibrationRequested_StartsSession_StateBecomesCollecting()
    {
        var vm = CreateVm();
        _messenger.Send(new CalibrationRequestedMessage());
        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
        Check.That(vm.TotalTargets).IsEqualTo(3);
        Check.That(_calibrationService.IsSessionActive).IsTrue();
    }

    [Fact]
    public void Cancel_WhileCollecting_ResetsToIdle()
    {
        var vm = CreateVm();
        _messenger.Send(new CalibrationRequestedMessage());
        vm.Cancel();
        Check.That(vm.State).IsEqualTo(CalibrationState.Idle);
        Check.That(_calibrationService.IsSessionActive).IsFalse();
    }

    [Fact]
    public void ReceiveCalibrationRequested_WhenAlreadyCollecting_IgnoresSecond()
    {
        var vm = CreateVm();
        _messenger.Send(new CalibrationRequestedMessage());
        var targetCount = vm.TotalTargets;
        _messenger.Send(new CalibrationRequestedMessage());
        Check.That(vm.TotalTargets).IsEqualTo(targetCount);
        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
    }

    private sealed class CountingGazeProvider : IGazeProvider
    {
        private Action<GazePoint>? _handlers;
        public int SubscriberCount { get; private set; }
        public event Action<GazePoint>? GazePointReceived
        {
            add { _handlers += value; SubscriberCount++; }
            remove { _handlers -= value; SubscriberCount--; }
        }
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task Cancel_WhileTargetHoldPending_StaleContinuationDoesNotUnsubscribeAgain()
    {
        var provider = new CountingGazeProvider();
        var settings = new CalibrationSettings { PointCount = 3, TargetHoldMs = 200 };
        var messenger = new WeakReferenceMessenger();
        var service = new CalibrationService();

        var vm = await Task.Run(() => new CalibrationViewModel(
            settings, service, messenger, provider,
            new PhysicalPoint(1920, 1080), "test-monitor", TempCalibrationPath()));

        messenger.Send(new CalibrationRequestedMessage());
        Check.That(provider.SubscriberCount).IsEqualTo(1);

        vm.Cancel(); // unsubscribes; the 200ms hold continuation is still pending
        Check.That(provider.SubscriberCount).IsEqualTo(0);

        // Let the stale continuation fire. With the bug it unsubscribes again -> -1.
        await Task.Delay(600, TestContext.Current.CancellationToken);

        Check.That(provider.SubscriberCount).IsEqualTo(0);
    }

    [Fact]
    public async Task CancelThenRestart_StaleContinuationDoesNotUnsubscribeNewSession()
    {
        // The exact user scenario behind the fix: Cancel then immediately restart while the old
        // session's target-hold continuation is still pending. That stale continuation must NOT
        // unsubscribe the NEW session's gaze handler (it's the same delegate instance). Session 1
        // holds briefly; session 2 holds long, so only session 1's stale continuation fires in
        // the observation window.
        var provider = new CountingGazeProvider();
        // TargetHoldMs is guarded to 200..3000: session 1 uses the floor, session 2 the ceiling.
        var settings = new CalibrationSettings { PointCount = 3, TargetHoldMs = 200 };
        var messenger = new WeakReferenceMessenger();
        var service = new CalibrationService();

        var vm = await Task.Run(() => new CalibrationViewModel(
            settings, service, messenger, provider,
            new PhysicalPoint(1920, 1080), "test-monitor", TempCalibrationPath()));

        messenger.Send(new CalibrationRequestedMessage());   // session 1: subscribe (1), 200ms hold pending
        Check.That(provider.SubscriberCount).IsEqualTo(1);

        vm.Cancel();                                          // count 0, generation advances
        settings.TargetHoldMs = 3000;                         // session 2 stays in hold past the window
        messenger.Send(new CalibrationRequestedMessage());    // session 2: subscribe (1), 3000ms hold pending
        Check.That(provider.SubscriberCount).IsEqualTo(1);

        // Past session 1's 200ms hold (its stale continuation fires) but well short of session 2's
        // 3000ms hold. With the bug, the stale continuation removes session 2's subscription -> 0.
        await Task.Delay(700, TestContext.Current.CancellationToken);

        Check.That(provider.SubscriberCount).IsEqualTo(1);    // session 2 still subscribed
        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);

        vm.Cancel(); // cleanup so the pending 5s continuation is a no-op
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
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        _messenger.Send(new CalibrationRequestedMessage());

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        stop.Cancel();
        try { await firing; } catch (OperationCanceledException) { }

        Check.That(completion).IsNotNull();
        Check.That(completion!.Success).IsFalse();
        Check.That(completion.MeanErrorPixels).IsNull();
        Check.That(vm.State).IsEqualTo(CalibrationState.Failed);
    }

    // The 2026-08-17 field report: "calibration is not usable — the points should move at the
    // user's pace." Under the old fixed timer a target advanced after TargetHoldMs whether or
    // not the user had found it, averaging samples from wherever gaze happened to be. Now: no
    // advance without gaze (however long), then advance once the user has arrived and the
    // target has its samples — past the visible floor.
    [Fact]
    public async Task Target_WaitsForTheUser_ThenAdvancesAtTheirPace()
    {
        // Constructed off the test SynchronizationContext so advances run inline on the firing
        // thread, deterministically (same pattern as FailedCalibration below).
        var vm = await Task.Run(CreateVm);

        _messenger.Send(new CalibrationRequestedMessage());
        var firstTarget = vm.CurrentTargetPosition;

        // No gaze at all: well past the old 200 ms hold, the session must still be on target 0.
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Check.That(vm.CurrentTargetIndex).IsEqualTo(0);
        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);

        // On-target gaze: arrival → settle → 30 samples → floor → advance. Three targets, then
        // the session completes (on-target gaze varies and fits near-identity → Active).
        using var stop = new CancellationTokenSource();
        var firing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && vm.State == CalibrationState.Collecting)
            {
                var pos = vm.CurrentTargetPosition;
                _provider.FireGaze(new GazePoint(pos.X, pos.Y, 0, true));
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (vm.State == CalibrationState.Collecting && DateTime.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        stop.Cancel();
        try { await firing; } catch (OperationCanceledException) { }

        Check.That(vm.CurrentTargetIndex).IsEqualTo(vm.TotalTargets);
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
    }
}
