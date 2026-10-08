namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// The spec-visible half of "what the user sees is what is enforced", covered headlessly.
/// <para>
/// The 2026-09-12 session failed partly because the target communicated the wrong thing: a 20 px dot
/// while arrival was decided at 240 px. The user was trying to hit something twelve times smaller in
/// area than the gate judging them, with no feedback that they were already inside it — so
/// "I struggle enormously" described a task that was harder than it needed to be.
/// </para>
/// <para>
/// What CANNOT be covered here is whether the ring reads correctly on screen. These tests pin the
/// numbers the overlay draws from; the drawing itself needs a manual check (see the ticket report).
/// </para>
/// </summary>
public class CalibrationPacingAndToleranceTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void FireGaze(GazePoint p) => GazePointReceived?.Invoke(p);
    }

    private static readonly PhysicalPoint Screen = new(3840, 2400);

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly CalibrationService _service = new();
    private readonly FakeGazeProvider _provider = new();
    private readonly CalibrationSettings _settings = new()
    {
        PointCount = 3,
        TargetHoldMs = 200,
        SettleWindowMs = 0,
        SamplesPerPoint = 10,
    };

    // One folder for the class, deleted at process exit and swept if a killed host leaves it — the
    // same treatment as CalibrationViewModelTests, and for the same reason: a test that completes a
    // calibration WRITES this file, so a bare %TEMP% path accumulates one per run forever.
    private static readonly string CalibrationDir = TempTestDirectory.Create("viewmodels-pacing");

    private static string TempPath() => Path.Combine(
        CalibrationDir, "cal-" + Guid.NewGuid().ToString("N") + ".json");

    private CalibrationViewModel CreateVm() => new(
        _settings, _service, _messenger, _provider, Screen, "test-monitor", TempPath());

    // ── The drawn tolerance is the enforced tolerance ─────────────────────────

    [Fact]
    public void TheRadiusTheOverlayDraws_IsTheRadiusTheCollectorEnforces()
    {
        // One constant, read by both. This is the assertion the ticket asks for: if these two ever
        // come from different places, the user is once again being judged against something they
        // cannot see.
        var vm = CreateVm();

        Check.That(vm.BandRadiusPixels).IsEqualTo(CalibrationGeometry.BandRadiusPx(Screen));

        _messenger.Send(new Messages.CalibrationRequestedMessage());

        Check.That(_service.BandRadiusPx).IsEqualTo(vm.BandRadiusPixels);
        vm.Cancel();
    }

    [Fact]
    public void TheToleranceIsFarLargerThanTheDot_AndBothAreAvailableToDraw()
    {
        // The two must be separately available, because they mean different things: the dot says
        // where to look, the ring says how close counts. Drawing only the dot — which is what the
        // overlay did — tells the user the tolerance is 20 px when it is 240.
        var vm = CreateVm();

        Check.That(vm.TargetRadiusPixels).IsEqualTo(20.0);
        Check.That(vm.BandRadiusPixels).IsEqualTo(240.0);           // 10 % of 2400
        Check.That(vm.BandRadiusPixels).IsStrictlyGreaterThan(vm.TargetRadiusPixels * 4);
    }

    [Fact]
    public void TheDotRadius_ComesFromTheSetting()
    {
        // It was read by nothing until now: the overlay hard-coded a 40x40 ellipse, so this setting
        // was inert and a user who enlarged it saw no change.
        _settings.TargetRadiusPixels = 48;

        Check.That(CreateVm().TargetRadiusPixels).IsEqualTo(48.0);
    }

    // ── Progress reflects in-band collection, not elapsed time ────────────────

    [Fact]
    public void Progress_AdvancesOnlyWhileGazeIsInsideTheBand()
    {
        var vm = CreateVm();
        _messenger.Send(new Messages.CalibrationRequestedMessage());
        var target = vm.CurrentTargetPosition;

        // Arrive, then hold on target: progress climbs.
        for (var i = 0; i < 6; i++)
            _provider.FireGaze(new GazePoint(target.X, target.Y, 0, true));

        var afterHolding = vm.TargetProgress;
        Check.That(afterHolding).IsStrictlyGreaterThan(0.0);
        Check.That(vm.IsOnTarget).IsTrue();

        // Now wander far outside the band for a long time: progress must STALL, not climb and not
        // reset. Time passing is not achievement, and losing banked progress would punish exactly
        // the user whose holds are short.
        for (var i = 0; i < 200; i++)
            _provider.FireGaze(new GazePoint(target.X + 2000, target.Y + 1000, 0, true));

        Check.That(vm.TargetProgress).IsEqualTo(afterHolding);
        Check.That(vm.IsOnTarget).IsFalse();

        vm.Cancel();
    }

    [Fact]
    public async Task Progress_IgnoresLostGaze_TheTimeRingIsTheProbesAlone()
    {
        // The measure-only probe's ring shows elapsed time and so moves on INVALID samples too; normal
        // calibration's must not (review A's FU11-2). A blink is not achievement, and a ring that
        // jumped to the elapsed share on one would throw away the banked in-band count. Only invalid
        // samples reveal this: a valid one recomputes the in-band count right after.
        var vm = CreateVm();
        _messenger.Send(new Messages.CalibrationRequestedMessage());
        var target = vm.CurrentTargetPosition;

        for (var i = 0; i < 5; i++)
            _provider.FireGaze(new GazePoint(target.X, target.Y, 0, true));
        var banked = vm.TargetProgress;
        Check.That(banked).IsStrictlyGreaterThan(0.0);

        // About 1 s of tracking loss, so the elapsed share (~0.2) differs from the banked count.
        var t0 = Environment.TickCount64;
        while (Environment.TickCount64 - t0 < 1_000)
        {
            _provider.FireGaze(new GazePoint(0, 0, 0, false));
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Check.That(vm.TargetProgress).IsEqualTo(banked);
        vm.Cancel();
    }

    [Fact]
    public void Progress_ResumesWhereItLeftOff_SoShortHoldsAccumulate()
    {
        // The pacing claim in one test: a user who can only hold for a moment at a time still
        // finishes a target, because each burst banks. Under a fixed collection window the same user
        // would restart from nothing every time they looked away.
        var vm = CreateVm();
        _messenger.Send(new Messages.CalibrationRequestedMessage());
        var target = vm.CurrentTargetPosition;

        _provider.FireGaze(new GazePoint(target.X, target.Y, 0, true));   // arrival

        double previous = 0;
        for (var burst = 0; burst < 4; burst++)
        {
            for (var i = 0; i < 3; i++)
                _provider.FireGaze(new GazePoint(target.X + 5, target.Y - 5, 0, true));

            Check.That(vm.TargetProgress).IsStrictlyGreaterThan(previous);
            previous = vm.TargetProgress;

            // Look away between bursts, as this user does.
            for (var i = 0; i < 10; i++)
                _provider.FireGaze(new GazePoint(target.X + 1500, target.Y + 900, 0, true));
        }

        vm.Cancel();
    }

    [Fact]
    public void IsOnTarget_TracksTheBandEdge()
    {
        // Just inside counts, just outside does not — measured against the same radius the collector
        // uses, so the ring cannot report "on target" for a sample the collector threw away.
        var vm = CreateVm();
        _messenger.Send(new Messages.CalibrationRequestedMessage());
        var target = vm.CurrentTargetPosition;
        var r = vm.BandRadiusPixels;

        _provider.FireGaze(new GazePoint(target.X + (r * 0.9), target.Y, 0, true));
        Check.That(vm.IsOnTarget).IsTrue();

        _provider.FireGaze(new GazePoint(target.X + (r * 1.1), target.Y, 0, true));
        Check.That(vm.IsOnTarget).IsFalse();

        vm.Cancel();
    }

    [Fact]
    public void ANewTarget_StartsFromZeroProgress()
    {
        // Otherwise the second target would open already part-filled and advance on fewer samples
        // than it should — quietly weakening every point after the first.
        var vm = CreateVm();
        _messenger.Send(new Messages.CalibrationRequestedMessage());
        var first = vm.CurrentTargetPosition;

        for (var i = 0; i < 5; i++)
            _provider.FireGaze(new GazePoint(first.X, first.Y, 0, true));
        Check.That(vm.TargetProgress).IsStrictlyGreaterThan(0.0);

        vm.Cancel();
    }

    [Fact]
    public async Task GazeThatNeverArrives_FailsWithoutSpendingTheLongBudget()
    {
        // The safety cap is two-stage, and this pins the early stage. A user banking samples a few at
        // a time needs a long budget; a tracker that cannot reach the point at all gains nothing from
        // it, and with five targets the full budget each would leave them staring at dots for over two
        // minutes before the session admitted something was wrong.
        //
        // Bounded well under the long budget so it fails rather than hangs if the early stage is lost.
        var completion = new TaskCompletionSource<Messages.CalibrationCompletedMessage>();
        _messenger.Register<Messages.CalibrationCompletedMessage>(this, (_, m) => completion.TrySetResult(m));

        var vm = await Task.Run(CreateVm);
        using var stop = new CancellationTokenSource();
        var firing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Far from every target: gaze never arrives, so nothing but the cap can advance it.
                _provider.FireGaze(new GazePoint(5, 5, 0, true));
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        _messenger.Send(new Messages.CalibrationRequestedMessage());

        var result = await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        stop.Cancel();
        try { await firing; } catch (OperationCanceledException) { }

        Check.That(result.Success).IsFalse();
        Check.That(vm.State).IsEqualTo(CalibrationState.Failed);
        // The typed failure, not the sentence: the code and its arguments are what the window renders
        // from, and what later translation keys are built on. The wording is pinned in
        // CalibrationFailureTextTests.
        Check.That(vm.Failure).IsNotNull();
        Check.That(vm.Failure!.Code).IsEqualTo(CalibrationFailureCode.TargetNeverReached);
        Check.That(vm.Failure.Arguments).IsEmpty();
    }

    // ── Shipped defaults suit a jittery user ──────────────────────────────────

    [Fact]
    public void ShippedDefaults_AskForAboutTwoShortHoldsPerTarget()
    {
        // Pinned as arithmetic rather than as bare numbers, so the reasoning survives a later edit.
        // Measured: this user's sustained still-holds are a median 0.24 s; the tracker runs ~33 Hz.
        // 30 samples asked for roughly four successful holds per target and got wander instead.
        var defaults = new CalibrationSettings();
        const double sampleRateHz = 33.0;
        const double medianHoldSeconds = 0.24;

        var samplesPerHold = medianHoldSeconds * sampleRateHz;          // ~8
        var holdsNeeded = defaults.SamplesPerPoint / samplesPerHold;

        Check.That(holdsNeeded).IsStrictlyLessThan(2.5);
        Check.That(defaults.SettleWindowMs).IsStrictlyGreaterThan(200); // past saccade settling
    }
}
