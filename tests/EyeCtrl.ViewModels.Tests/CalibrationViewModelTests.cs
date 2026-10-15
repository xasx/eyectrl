namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

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
    /// One throwaway folder for this whole class's calibration files, cleaned up at process exit and
    /// swept if a killed host ever leaves it behind.
    /// <para>
    /// It used to be a bare <c>%TEMP%\eyectrl-cal-{guid}.json</c> per call with nothing ever deleting
    /// them, and a test that completes a calibration writes that file: 286 of them had accumulated
    /// since 2026-08-17. One folder rather than one per call, because the folder is the unit of
    /// cleanup and a file per view model would mean hundreds of directories.
    /// </para>
    /// </summary>
    private static readonly string CalibrationDir = TempTestDirectory.Create("viewmodels-calibration");

    /// <summary>
    /// A throwaway calibration path. The constructor argument became required when the
    /// <c>%APPDATA%</c> fallback was removed — which means these tests were
    /// previously pointed at the LIVE <c>%APPDATA%\EyeCtrl\calibration.json</c>, the file whose
    /// corruption silently breaks dwell everywhere. Per-call GUID so nothing can collide.
    /// </summary>
    private static string TempCalibrationPath() => System.IO.Path.Combine(
        CalibrationDir, "cal-" + Guid.NewGuid().ToString("N") + ".json");

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
        // A sensor stuck on a constant value: gaze never reaches any target. The VM must surface that
        // as Failed + CalibrationCompletedMessage(false, null) — not a silent success.
        //
        // WHERE this fails moved on 2026-09-13 and the test is deliberately agnostic about it. It used
        // to run all five targets and be rejected at the fit, because identical gaze at every target
        // gives no variation. With in-band collection the FIRST target already fails: gaze at (1,1) is
        // nowhere near it, so nothing is collected and EndTarget throws. That is the better failure —
        // the user learns the tracker is not following them at target one rather than at the end — but
        // it travels a different code path, and that path was not sending this message at all. Pinning
        // the message rather than the path is what caught that.
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

    [Fact]
    public async Task AQualityGateRefusal_ReachesTheScreen_FromTheTypedFailure()
    {
        // The completion path: every fit refusal lands in CompleteSession's catch, and the per-target
        // advice is the whole reason the gate names targets. The screen must show the renderer's
        // sentence for the typed code — not the code, and not raw exception text.
        _settings.SettleWindowMs = 0;
        _settings.SamplesPerPoint = 10;

        var failure = new TaskCompletionSource<CalibrationCompletedMessage>();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, m) =>
        {
            if (!m.Success) failure.TrySetResult(m);
        });

        // Off any test SynchronizationContext so per-target advances run inline on the firing thread.
        var vm = await Task.Run(CreateVm);
        using var stop = new CancellationTokenSource();
        var firing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var target = vm.CurrentTargetPosition;
                // Alternating corners of an 80 px box around the target: always inside the 120 px
                // band, so collection and the fit succeed, while each target's spread of 80 px on
                // both axes (113 px radial) trips the scatter gate.
                _provider.FireGaze(new GazePoint(target.X + 80, target.Y + 80, 0, true));
                _provider.FireGaze(new GazePoint(target.X - 80, target.Y - 80, 0, true));
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        _messenger.Send(new CalibrationRequestedMessage());
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        stop.Cancel();
        try { await firing; } catch (OperationCanceledException) { }

        Check.That(vm.State).IsEqualTo(CalibrationState.Failed);
        Check.That(vm.Failure).IsNotNull();
        Check.That(vm.Failure!.Code).IsEqualTo(CalibrationFailureCode.Quality);
        // The words come from the one renderer over the typed failure.
        Check.That(vm.ErrorText).IsEqualTo(CalibrationFailureText.Describe(vm.Failure));
        Check.That(vm.ErrorText!).Contains("Calibration not accepted");
    }

    [Fact]
    public async Task ASaveFault_ReachesTheScreen_AsTheGenericUnexpectedSentence()
    {
        // A fault after the fit is not one of the typed calibration failures — here the calibration
        // path is a directory, so the atomic rename throws. It must reach the screen as the renderer's
        // generic sentence, never as a raw exception message (which for a file fault carries a path).
        _settings.SettleWindowMs = 0;
        _settings.SamplesPerPoint = 10;

        var directoryAsPath = System.IO.Path.Combine(CalibrationDir, "fault-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directoryAsPath);

        var failure = new TaskCompletionSource<CalibrationCompletedMessage>();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, m) =>
        {
            if (!m.Success) failure.TrySetResult(m);
        });

        var vm = await Task.Run(() => new CalibrationViewModel(
            _settings, _calibrationService, _messenger, _provider,
            new PhysicalPoint(1920, 1080), "test-monitor", directoryAsPath));

        using var stop = new CancellationTokenSource();
        var firing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var target = vm.CurrentTargetPosition;
                _provider.FireGaze(new GazePoint(target.X + 10, target.Y - 5, 0, true));
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        _messenger.Send(new CalibrationRequestedMessage());
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        stop.Cancel();
        try { await firing; } catch (OperationCanceledException) { }

        Check.That(vm.State).IsEqualTo(CalibrationState.Failed);
        Check.That(vm.Failure).IsNotNull();
        Check.That(vm.Failure!.Code).IsEqualTo(CalibrationFailureCode.Unexpected);
        Check.That(vm.ErrorText).IsEqualTo("Something went wrong while calibrating. Please try again.");
    }

    [Fact]
    public void Failure_Changing_RaisesPropertyChangedForItselfAndForTheComputedErrorText()
    {
        // ErrorText is computed from Failure, so a view that binds it (T08 may) only updates if the
        // setter notifies for it. The window currently pulls it on a state change, which hides this.
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Failure = CalibrationFailure.Unexpected;

        Check.That(raised).HasElementThatMatches(p => p == nameof(CalibrationViewModel.Failure));
        Check.That(raised).HasElementThatMatches(p => p == nameof(CalibrationViewModel.ErrorText));
        Check.That(vm.ErrorText).IsEqualTo("Something went wrong while calibrating. Please try again.");
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

    // ── The clocks behind the pacing rules ────────────────────────────────────
    // Both run on the injected TimeProvider, so a FakeTimeProvider makes their bounds exact. The
    // measure-only harness (CalibrationMeasureOnlyTests) drives the probe's clocks; these two pin
    // the NORMAL path's settle and hold, which the wiring now also routes through the injection.

    [Fact]
    public async Task SettleWindow_DiscardsSamplesThatArriveRightAfterArrival()
    {
        // The settle setting reaches the collector (BeginTarget gets the SETTING's value, not 0): a
        // sample inside the settle window does not count, and one just past it does. A mutant that
        // ignores the setting counts the early sample and turns the first assertion red.
        var time = new FakeTimeProvider();
        var service = new CalibrationService(timeProvider: time);
        var settings = new CalibrationSettings
        {
            PointCount = 3, TargetHoldMs = 200, SettleWindowMs = 300, SamplesPerPoint = 5,
        };

        var vm = await Task.Run(() => new CalibrationViewModel(
            settings, service, _messenger, _provider,
            new PhysicalPoint(1920, 1080), "test-monitor", TempCalibrationPath(),
            timeProvider: time));
        _messenger.Send(new CalibrationRequestedMessage());

        void FireOnTarget()
        {
            var target = vm.CurrentTargetPosition;
            _provider.FireGaze(new GazePoint(target.X, target.Y, 0, IsValid: true));
        }

        FireOnTarget();                                    // arrival: the saccade landing, discarded
        time.Advance(TimeSpan.FromMilliseconds(100));
        FireOnTarget();                                    // inside the settle window: discarded too
        Check.That(vm.TargetProgress).IsEqualTo(0.0);

        time.Advance(TimeSpan.FromMilliseconds(250));      // 350 ms after arrival: past the settle
        FireOnTarget();                                    // the first counted sample
        Check.That(vm.TargetProgress).IsEqualTo(0.2);

        vm.Cancel();
    }

    [Fact]
    public async Task HoldFloor_HoldsACompletedTargetUntilTheMinimumDisplayTime()
    {
        // A target that already has its samples must not advance before TargetHoldMs of the injected
        // clock: the floor is what keeps the dot readable. A mutant that drops the hold advances on
        // the completion sample itself and turns the first index assertion red.
        var time = new FakeTimeProvider();
        var service = new CalibrationService(timeProvider: time);
        var settings = new CalibrationSettings
        {
            PointCount = 3, TargetHoldMs = 1_000, SettleWindowMs = 0, SamplesPerPoint = 5,
        };

        var vm = await Task.Run(() => new CalibrationViewModel(
            settings, service, _messenger, _provider,
            new PhysicalPoint(1920, 1080), "test-monitor", TempCalibrationPath(),
            timeProvider: time));
        _messenger.Send(new CalibrationRequestedMessage());

        for (int i = 0; i < settings.SamplesPerPoint + 2; i++)
        {
            var target = vm.CurrentTargetPosition;
            _provider.FireGaze(new GazePoint(target.X, target.Y, 0, IsValid: true));
        }

        Check.That(vm.CurrentTargetIndex).IsEqualTo(0);    // complete, but inside the hold floor
        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);

        time.Advance(TimeSpan.FromMilliseconds(1_000));
        var second = vm.CurrentTargetPosition;
        _provider.FireGaze(new GazePoint(second.X, second.Y, 0, IsValid: true));

        Check.That(vm.CurrentTargetIndex).IsEqualTo(1);    // the floor passed; that sample advanced it

        vm.Cancel();
    }

    // ── Clearing the calibration ──
    // This is the only code that deletes calibration.json, and it did so behind a bare `catch { }`.
    // Afterwards a deliberately cleared calibration and one that failed to load look identical — gaze
    // just runs raw — so the deletion has to leave a trace or "where did my calibration go?" is
    // unanswerable from the logs.

    private CalibrationViewModel CreateVmWithLogger(string path, FakeLogger<CalibrationViewModel> logger) =>
        new(_settings, _calibrationService, _messenger, _provider,
            new PhysicalPoint(1920, 1080), "test-monitor", path,
            monitorHardwareId: null, logger: logger);

    [Fact]
    public void ClearCalibration_DeletesTheFile_AndLogsAtInformation()
    {
        var path = TempCalibrationPath();
        File.WriteAllText(path, "{}");
        var logger = new FakeLogger<CalibrationViewModel>();
        var vm = CreateVmWithLogger(path, logger);

        vm.ClearCalibration();

        Check.That(File.Exists(path)).IsFalse();
        var line = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Information && e.Message.Contains(path));
        Check.That(line.Message).IsNotNull();
        Check.That(line.Message).Contains("deleted");
    }

    [Fact]
    public void ClearCalibration_WithNoPersistedFile_StillLogs()
    {
        // The "nothing to delete" case is worth a line of its own: it distinguishes "the user cleared
        // it" from "there was never a file", which is exactly the ambiguity being fixed.
        var path = TempCalibrationPath();
        var logger = new FakeLogger<CalibrationViewModel>();
        var vm = CreateVmWithLogger(path, logger);

        vm.ClearCalibration();

        Check.That(logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("no persisted file")))
            .IsTrue();
    }

    [Fact]
    public void ClearCalibration_WithoutALogger_DoesNotThrow()
    {
        // The logger is optional (the ViewModels layer has no logging of its own); tests and any
        // caller that omits it must still be able to clear.
        var vm = CreateVm();
        Check.ThatCode(() => vm.ClearCalibration()).DoesNotThrow();
    }

    private sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
