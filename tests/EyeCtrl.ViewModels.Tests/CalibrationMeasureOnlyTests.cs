namespace EyeCtrl.ViewModels.Tests;

using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The measure-only bias probe, end to end at the view-model layer: five targets, whatever the
/// settings say; a fixed short budget per target so a gaze-only user never waits out the normal caps;
/// continue past a target that collected nothing; keep a per-session sidecar on every ending; apply
/// nothing. The named mutation for the persistence checks is restoring <c>AbandonSession</c> on any of
/// these paths — it discards the only data the probe exists to collect, and these tests turn red.
/// </summary>
public class CalibrationMeasureOnlyTests
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

    private sealed class ThrowingSink : IGroundTruthSink
    {
        public void BeginSession(string providerType, PhysicalPoint screenSize, int settleWindowMs, GroundTruthMode mode = GroundTruthMode.Calibration) { }
        public void OnSample(int targetId, PhysicalPoint target, GazePoint sample, bool hasArrived) { }
        public void CompleteSession(GroundTruthOutcome outcome = GroundTruthOutcome.Completed) =>
            throw new IOException("sink fault");
        public void AbandonSession() { }
    }

    private static readonly PhysicalPoint Screen = new(3840, 2400);
    private static readonly string Dir = TempTestDirectory.Create("viewmodels-measure-only");

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly CalibrationService _service = new();
    private readonly FakeGazeProvider _provider = new();
    private readonly CalibrationSettings _settings = new()
    {
        // Deliberately 3: the probe must force its own five points, because the decision rule is
        // defined over five targets and a user-adjusted setting must not redefine the instrument.
        PointCount = 3,
        TargetHoldMs = 200,
        SettleWindowMs = 0,
        SamplesPerPoint = 5,
    };

    // One directory per test: the recorder puts probe sidecars next to the configured path, and a
    // shared directory would let one test's files answer another test's glob.
    private readonly string _testDir = Path.Combine(Dir, Guid.NewGuid().ToString("N"));
    private string CalibrationPath => Path.Combine(_testDir, "calibration.json");
    private string SidecarPath => Path.Combine(_testDir, "gaze-recording.groundtruth.json");

    /// <summary>The probe's own timestamped sidecar — the only one a measure-only session writes.</summary>
    private string ProbeSidecarPath() =>
        Directory.GetFiles(_testDir, "bias-probe-*.groundtruth.json").Single();

    private CalibrationViewModel CreateVm(bool recordRawGaze = false, IGroundTruthSink? sink = null) => new(
        _settings, _service, _messenger, _provider, Screen, "test-monitor", CalibrationPath,
        groundTruth: sink ?? new ScriptedGroundTruthRecorder(SidecarPath),
        // RecordRawGaze OFF by default: the probe's sidecar must not hinge on the global recorder flag.
        diagnostics: new DiagnosticsSettings { RecordRawGaze = recordRawGaze });

    /// <summary>Completes the current target at the user's pace and lets the view model advance.</summary>
    private void FinishTarget(CalibrationViewModel vm, long timestampBase = 0)
    {
        var target = vm.CurrentTargetPosition;
        for (int i = 0; i < _settings.SamplesPerPoint + 2; i++)
            _provider.FireGaze(new GazePoint(target.X, target.Y, timestampBase + (i * 33_333), IsValid: true));

        // Past the visible hold floor; the next sample is what triggers the advance.
        Thread.Sleep(_settings.TargetHoldMs + 60);
        _provider.FireGaze(new GazePoint(target.X, target.Y, timestampBase + 10_000_000, IsValid: true));
    }

    private static GroundTruthSession ReadSidecar(string path) =>
        JsonSerializer.Deserialize<GroundTruthSession>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            })!;

    // ── Starting ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Construction_StartsNothing_AndTheProbeIsFiveTargets()
    {
        var vm = await Task.Run(() => CreateVm());

        Check.That(vm.State).IsEqualTo(CalibrationState.Idle);
        Check.That(vm.IsMeasureOnly).IsFalse();

        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
        Check.That(vm.IsMeasureOnly).IsTrue();
        Check.That(vm.TotalTargets).IsEqualTo(5);
        Check.That(vm.TargetPositions).HasSize(5);

        vm.Cancel();
        Check.That(_service.IsSessionActive).IsFalse();
    }

    [Fact]
    public async Task NormalCalibration_KeepsTheRecorderGate_ProbeBypassesIt()
    {
        // The privacy gate is preserved for the normal path: with RecordRawGaze off, a COMPLETED
        // normal calibration writes no sidecar. It has to complete, not cancel — a cancel discards
        // the sidecar by either route, so a cancelled session proves nothing about the gate.
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new CalibrationRequestedMessage());
        for (int i = 0; i < _settings.PointCount; i++)
            FinishTarget(vm, timestampBase: i * 30_000_000);

        Check.That(vm.State).IsEqualTo(CalibrationState.Active);   // the fit really ran and passed
        Check.That(File.Exists(CalibrationPath)).IsTrue();
        Check.That(File.Exists(SidecarPath)).IsFalse();
        Check.That(Directory.GetFiles(_testDir, "bias-probe-*.groundtruth.json")).IsEmpty();
        Check.That(vm.IsMeasureOnly).IsFalse();
    }

    // ── Ending ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACompletedProbe_WritesTheSidecar_AndAppliesNothing()
    {
        // A pre-existing calibration is loaded first so "nothing applied" compares a real transform
        // and the real file bytes — not merely null before and after.
        var existing = new CalibrationData
        {
            Type = TransformType.Affine,
            TransformParams = [1, 0, 0, 0, 1, 0],
            CalibratedAt = DateTimeOffset.UtcNow,
            ScreenWidth = (int)Screen.X,
            ScreenHeight = (int)Screen.Y,
            MonitorDeviceName = "test-monitor",
        };
        _service.Save(CalibrationPath, existing);
        _service.Load(CalibrationPath, Screen, "test-monitor");
        var transformBefore = _service.CurrentTransform;
        var fileBefore = File.ReadAllBytes(CalibrationPath);

        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());
        for (int i = 0; i < 5; i++)
            FinishTarget(vm, timestampBase: i * 30_000_000);

        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.IsMeasureOnly).IsTrue();
        Check.That(_service.IsSessionActive).IsFalse();
        Check.That(_service.CurrentTransform).IsSameReferenceAs(transformBefore);
        Check.That(File.ReadAllBytes(CalibrationPath).SequenceEqual(fileBefore)).IsTrue();

        var sidecar = ReadSidecar(ProbeSidecarPath());
        Check.That(sidecar.Mode).IsEqualTo(GroundTruthMode.MeasureOnly);
        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Completed);
        Check.That(sidecar.Windows).HasSize(5);
        Check.That(sidecar.Windows[0].Samples).Not.IsEmpty();   // self-contained, no recorder involved
    }

    [Fact]
    public async Task APartialProbe_ContinuesPastAnEmptyTarget_AndRecordsPartial()
    {
        var completions = new List<CalibrationCompletedMessage>();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, m) => completions.Add(m));

        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        FinishTarget(vm, timestampBase: 0);

        // Target 1: gaze parked far away, so the collector never arrives and the short fixed budget
        // ends the target. The probe must move on — ending here would throw away the windows the
        // five-target decision rule needs, which is exactly what a large bias would cause.
        for (int i = 0; i < 5; i++)
            _provider.FireGaze(new GazePoint(1920, 100, 1_000_000 + (i * 33_333), IsValid: true));
        Thread.Sleep(3_400);

        Check.That(vm.CurrentTargetIndex).IsEqualTo(2);
        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);

        FinishTarget(vm, timestampBase: 20_000_000);
        FinishTarget(vm, timestampBase: 30_000_000);
        FinishTarget(vm, timestampBase: 40_000_000);

        // Neutral ending: Active, no failure text, and no unsuccessful completion message.
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.ErrorText).IsNull();
        Check.That(_service.IsSessionActive).IsFalse();
        Check.That(completions).Not.IsEmpty();
        Check.That(completions.Any(c => !c.Success)).IsFalse();

        var sidecar = ReadSidecar(ProbeSidecarPath());
        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Partial);
        Check.That(sidecar.Windows).HasSize(5);
        Check.That(sidecar.Windows[1].ArrivedAtUs).IsNull();    // the empty target is recorded, not dropped
        Check.That(File.Exists(CalibrationPath)).IsFalse();     // and nothing was applied
    }

    [Fact]
    public async Task AProbeAbandonedAtAnyTime_KeepsItsData()
    {
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        var target = vm.CurrentTargetPosition;
        for (int i = 0; i < 5; i++)
            _provider.FireGaze(new GazePoint(target.X, target.Y, i * 33_333, IsValid: true));

        vm.Cancel();

        Check.That(vm.State).IsEqualTo(CalibrationState.Idle);
        Check.That(_service.IsSessionActive).IsFalse();
        Check.That(File.Exists(ProbeSidecarPath())).IsTrue();
        Check.That(ReadSidecar(ProbeSidecarPath()).Outcome).IsEqualTo(GroundTruthOutcome.Abandoned);
        Check.That(File.Exists(CalibrationPath)).IsFalse();
    }

    [Fact]
    public async Task TheFixedBudget_EndsAnEmptyProbeInAboutFifteenSeconds()
    {
        // The coordinator's decision: a gaze-only user cannot press Esc, so measure-only does not wait
        // out the normal 8 s / 25 s caps. Each target gets 3 s after display, whatever the gaze does;
        // the whole probe is about 15 s. Gaze is on screen the whole time (so every window carries
        // samples) but far from every target's band, so no target can complete early.
        var completion = new TaskCompletionSource();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, _) => completion.TrySetResult());

        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        using var stop = new CancellationTokenSource();
        var firing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _provider.FireGaze(new GazePoint(1920, 100, 0, IsValid: true));
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        var started = Environment.TickCount64;
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
        stop.Cancel();
        try { await firing; } catch (OperationCanceledException) { }

        Check.That(elapsed.TotalSeconds).IsGreaterOrEqualThan(13.0);   // five 3 s budgets
        Check.That(elapsed.TotalSeconds).IsStrictlyLessThan(22.0);     // and not a normal cap
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.ErrorText).IsNull();

        var sidecar = ReadSidecar(ProbeSidecarPath());
        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Partial);
        Check.That(sidecar.Windows).HasSize(5);
        Check.That(sidecar.Windows).ContainsOnlyElementsThatMatch(w => w.Samples.Count > 0);
    }

    [Fact]
    public async Task TheEndingPublishesActiveExactlyOnce_WhichIsTheCoordinatorsReleaseEdge()
    {
        // OverlayMessageCoordinator suppresses fixation on Collecting/Training and releases it on
        // Active; measure-only reuses Active precisely so that release still runs. One Active per
        // session is the contract, and ticket 4's release-edge fix (it ends the reported fixation)
        // then runs exactly once.
        var states = new List<CalibrationState>();
        _messenger.Register<CalibrationStateChangedMessage>(this, (_, m) => states.Add(m.State));

        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());
        for (int i = 0; i < 5; i++)
            FinishTarget(vm, timestampBase: i * 30_000_000);

        Check.That(states.Count(s => s == CalibrationState.Active)).IsEqualTo(1);
        Check.That(states[^1]).IsEqualTo(CalibrationState.Active);
        Check.That(states.Any(s => s == CalibrationState.Failed)).IsFalse();
    }

    // ── Session-state teeth ───────────────────────────────────────────────────

    [Fact]
    public async Task ProbeThenCalibrateNow_DoesARealFitAndSave_AndClearsTheMode()
    {
        // The mode is per session, never sticky: a probe followed by "Calibrate Now" on the same
        // app-lifetime view model must fit, save and apply, not silently skip all three.
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());
        for (int i = 0; i < 5; i++)
            FinishTarget(vm, timestampBase: i * 30_000_000);
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(File.Exists(CalibrationPath)).IsFalse();

        vm.Acknowledge();
        _messenger.Send(new CalibrationRequestedMessage());
        Check.That(vm.IsMeasureOnly).IsFalse();

        for (int i = 0; i < _settings.PointCount; i++)
            FinishTarget(vm, timestampBase: 200_000_000 + (i * 30_000_000));

        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(_service.IsSessionActive).IsFalse();
        Check.That(File.Exists(CalibrationPath)).IsTrue();
        Check.That(_service.CurrentTransform).IsNotNull();
    }

    [Fact]
    public async Task Cancel_WithAThrowingSink_StillPublishesIdle()
    {
        // A sink throw must not strand a Collecting view model behind a full-screen backdrop: Idle is
        // the coordinator's only suppression release on an abandon.
        var states = new List<CalibrationState>();
        _messenger.Register<CalibrationStateChangedMessage>(this, (_, m) => states.Add(m.State));
        var vm = await Task.Run(() => CreateVm(sink: new ThrowingSink()));
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        Exception? thrown = null;
        try { vm.Cancel(); } catch (Exception ex) { thrown = ex; }

        Check.That(thrown).IsNull();
        Check.That(vm.State).IsEqualTo(CalibrationState.Idle);
        Check.That(states[^1]).IsEqualTo(CalibrationState.Idle);
        Check.That(_service.IsSessionActive).IsFalse();
    }

    [Fact]
    public async Task MeasureOnlyFault_EndsNeutral_WithAFailedSidecar_AndNoFailureMessage()
    {
        // The catch inside AdvanceFromCurrentTarget routes a measure-only fault to the SAME neutral
        // ending: sidecar written with outcome failed, Active published, no error text, no
        // unsuccessful completion. A subscriber that throws from the target-position message is the
        // deterministic way to fault that path (reviewer A's M10 mutation removes the branch and
        // let the normal failure path discard the data).
        _messenger.Register<CalibrationTargetPositionMessage>(this, (_, m) =>
        {
            if (m.TargetIndex == 1) throw new InvalidOperationException("position subscriber fault");
        });
        var completions = new List<CalibrationCompletedMessage>();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, m) => completions.Add(m));

        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        Exception? thrown = null;
        try { FinishTarget(vm, timestampBase: 0); } catch (Exception ex) { thrown = ex; }

        Check.That(thrown).IsNull();
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.ErrorText).IsNull();
        Check.That(_service.IsSessionActive).IsFalse();
        Check.That(completions.Any(c => !c.Success)).IsFalse();
        Check.That(ReadSidecar(ProbeSidecarPath()).Outcome).IsEqualTo(GroundTruthOutcome.Failed);
        Check.That(File.Exists(CalibrationPath)).IsFalse();
    }

    // ── Normal calibration: the stale-cap fix ─────────────────────────────────

    [Fact]
    public async Task NormalCalibration_StaleCapFromAnEarlierTarget_DoesNotFailTheSession()
    {
        // Before the fix, the cap armed for target 0 advanced whichever target was current when it
        // fired — target 2 here — and EndTarget threw "Gaze never reached the target", failing a
        // session that was still legitimately collecting. This pins the normal-mode behaviour change
        // the probe's ticket shipped: each target gets its own cap.
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new CalibrationRequestedMessage());
        FinishTarget(vm, timestampBase: 0);
        FinishTarget(vm, timestampBase: 20_000_000);
        Check.That(vm.CurrentTargetIndex).IsEqualTo(2);

        // Just past target 0's first-stage cap (~8 s), well short of target 2's own.
        Thread.Sleep(8_000 - (2 * (_settings.TargetHoldMs + 60)) + 200);

        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
        Check.That(vm.CurrentTargetIndex).IsEqualTo(2);
        Check.That(vm.ErrorText).IsNull();

        vm.Cancel();
    }
}
