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
/// settings say; continue past a target that collected nothing; keep the self-contained sidecar on
/// every ending; apply nothing. The named mutation for the persistence checks is restoring
/// <c>AbandonSession</c> on any of these paths — it discards the only data the probe exists to
/// collect, and these tests turn red.
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

    private readonly string _calibrationPath = Path.Combine(Dir, "cal-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _sidecarPath = Path.Combine(Dir, "side-" + Guid.NewGuid().ToString("N") + ".json");

    private CalibrationViewModel CreateVm(bool recordRawGaze = false) => new(
        _settings, _service, _messenger, _provider, Screen, "test-monitor", _calibrationPath,
        groundTruth: new ScriptedGroundTruthRecorder(_sidecarPath),
        // RecordRawGaze OFF: the probe's sidecar must not hinge on the global recorder flag.
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
    }

    [Fact]
    public async Task NormalCalibration_KeepsTheRecorderGate_ProbeBypassesIt()
    {
        // The privacy gate is preserved for the normal path: with RecordRawGaze off, a normal
        // calibration writes no sidecar. The probe is the deliberate exception, and this pins both
        // halves so a later change cannot quietly make every calibration a measurement session.
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new CalibrationRequestedMessage());
        FinishTarget(vm);
        vm.Cancel();

        Check.That(File.Exists(_sidecarPath)).IsFalse();
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
        _service.Save(_calibrationPath, existing);
        _service.Load(_calibrationPath, Screen, "test-monitor");
        var transformBefore = _service.CurrentTransform;
        var fileBefore = File.ReadAllBytes(_calibrationPath);

        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());
        for (int i = 0; i < 5; i++)
            FinishTarget(vm, timestampBase: i * 30_000_000);

        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.IsMeasureOnly).IsTrue();
        Check.That(_service.CurrentTransform).IsSameReferenceAs(transformBefore);
        Check.That(File.ReadAllBytes(_calibrationPath).SequenceEqual(fileBefore)).IsTrue();

        var sidecar = ReadSidecar(_sidecarPath);
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

        // Target 1: gaze parked far away, so the collector never arrives and the no-arrival cap ends
        // the target. The probe must move on — ending here would throw away the windows the
        // five-target decision rule needs, which is exactly what a large bias would cause.
        for (int i = 0; i < 5; i++)
            _provider.FireGaze(new GazePoint(10, 10, 1_000_000 + (i * 33_333), IsValid: true));
        Thread.Sleep(8_500);

        Check.That(vm.CurrentTargetIndex).IsEqualTo(2);
        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);

        FinishTarget(vm, timestampBase: 20_000_000);
        FinishTarget(vm, timestampBase: 30_000_000);
        FinishTarget(vm, timestampBase: 40_000_000);

        // Neutral ending: Active, no failure text, and no unsuccessful completion message.
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.ErrorText).IsNull();
        Check.That(completions).Not.IsEmpty();
        Check.That(completions.Any(c => !c.Success)).IsFalse();

        var sidecar = ReadSidecar(_sidecarPath);
        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Partial);
        Check.That(sidecar.Windows).HasSize(5);
        Check.That(sidecar.Windows[1].ArrivedAtUs).IsNull();    // the empty target is recorded, not dropped
        Check.That(File.Exists(_calibrationPath)).IsFalse();    // and nothing was applied
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
        Check.That(File.Exists(_sidecarPath)).IsTrue();
        Check.That(ReadSidecar(_sidecarPath).Outcome).IsEqualTo(GroundTruthOutcome.Abandoned);
        Check.That(File.Exists(_calibrationPath)).IsFalse();
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
}
