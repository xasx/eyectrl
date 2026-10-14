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
/// The measure-only bias probe, end to end at the view-model layer: five targets whatever the
/// settings say; a fixed 5 s window per target — in-band gaze does NOT shorten it, because a short
/// window at low calibration settings leaves the estimator no cluster to find; continue past a target
/// that collected nothing; a per-session sidecar on every ending; apply nothing. The named mutation
/// for the persistence checks is restoring <c>AbandonSession</c> on any of these paths — it discards
/// the only data the probe exists to collect, and these tests turn red.
/// </summary>
public class CalibrationMeasureOnlyTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        private Action<GazePoint>? _handlers;

        /// <summary>
        /// Serialises every handler invocation the way production's single tracker thread does: the
        /// edge samples run on the thread that (un)subscribes (the cap's continuation), the firing
        /// loop delivers on a pool thread of its own, and OnRawGaze's downstream — the sidecar's open
        /// window and CalibrationService.AddRawSample — is documented as gaze-thread-only
        /// (ScriptedGroundTruthRecorder remarks). <see cref="Monitor"/> is re-entrant, so the
        /// thread-static guard inside keeps its meaning.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Delivered synchronously on the thread that ATTACHES a gaze handler — for the view model
        /// that is the instant a target is displayed, so the sample lands on the window's first edge.
        /// <para>
        /// The sidecar's window bounds are the first and last SAMPLE of a target, which makes them
        /// whatever the firing loop managed to deliver. A loaded CI runner stalls that loop by a
        /// second or more, which shortened the measured windows to 3.8–4.2 s (this flake) even though
        /// the target was displayed for its full 5 s. One sample on each edge makes the bounds the
        /// target's own edges.
        /// </para>
        /// </summary>
        public Func<GazePoint>? SampleOnSubscribe { get; set; }

        /// <summary>Delivered synchronously just before a gaze handler DETACHES — the target's end edge.</summary>
        public Func<GazePoint>? SampleOnUnsubscribe { get; set; }

        public event Action<GazePoint>? GazePointReceived
        {
            add
            {
                _handlers += value;
                if (SampleOnSubscribe is { } sample) DeliverEdgeSample(value, sample);
            }
            remove
            {
                // Only on a real detach: -= also runs from OnDispose and Cancel paths where the view
                // model is not collecting, and a spurious edge sample there would be recorded as if
                // the last target had just ended.
                bool attached = _handlers?.GetInvocationList().Contains(value) == true;
                if (attached && SampleOnUnsubscribe is { } sample) DeliverEdgeSample(value, sample);
                _handlers -= value;
            }
        }

        /// <summary>
        /// Delivers an edge sample, but never one from inside another edge sample: the handler runs
        /// synchronously here, so a broken production build that (un)subscribes from inside its own
        /// gaze callback would otherwise recurse without bound, and a StackOverflowException takes
        /// the whole test host with it — every other result is lost and CI reports a catastrophic
        /// failure instead of the one red test. The nested delivery is skipped, not deferred: it can
        /// only arrive through a transition no correct build makes from inside a gaze callback — a
        /// nested <c>-=</c> would re-enter the same edge, a nested <c>+=</c> would be the next
        /// target's start — and the ordinary sample stream re-supplies the boundary within a period.
        /// </summary>
        [ThreadStatic] private static bool _deliveringEdgeSample;

        private void DeliverEdgeSample(Action<GazePoint>? handler, Func<GazePoint> sample)
        {
            if (_deliveringEdgeSample) return;
            _deliveringEdgeSample = true;
            try
            {
                lock (_gate) { handler?.Invoke(sample()); }
            }
            finally { _deliveringEdgeSample = false; }
        }

        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void FireGaze(GazePoint p)
        {
            lock (_gate) { _handlers?.Invoke(p); }
        }
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
    private CalibrationSettings _settings = new()
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

    /// <summary>Completes the current NORMAL-calibration target at the user's pace and advances.</summary>
    private void FinishTarget(CalibrationViewModel vm, long timestampBase = 0)
    {
        var target = vm.CurrentTargetPosition;
        for (int i = 0; i < _settings.SamplesPerPoint + 2; i++)
            _provider.FireGaze(new GazePoint(target.X, target.Y, timestampBase + (i * 33_333), IsValid: true));

        // Past the visible hold floor; the next sample is what triggers the advance.
        Thread.Sleep(_settings.TargetHoldMs + 60);
        _provider.FireGaze(new GazePoint(target.X, target.Y, timestampBase + 10_000_000, IsValid: true));
    }

    /// <summary>
    /// Drives a whole probe with real-time gaze: <paramref name="aimAtTarget"/> decides, per current
    /// target index, whether gaze sits on the target (so the collector completes it) or far away (so
    /// the target ends empty at its cap). Returns the VM and its sidecar when the session ends.
    /// </summary>
    /// <param name="stallMs">
    /// Injected stall in the sample loop, once per target ~3.5 s in: the deterministic stand-in for
    /// the loaded-runner stall that truncated the measured windows (see the V4 regression test). Zero
    /// is the normal harness.
    /// </param>
    private async Task<(CalibrationViewModel Vm, GroundTruthSession Sidecar)> RunProbe(
        Func<int, bool> aimAtTarget, int timeoutSeconds = 30, int stallMs = 0)
    {
        // A fresh recipient object per call, so a test may subscribe to the completion message itself
        // without the messenger reporting a duplicate registration.
        var signal = new TaskCompletionSource();
        var probe = new object();
        _messenger.Register<CalibrationCompletedMessage>(probe, (_, _) => signal.TrySetResult());

        var vm = await Task.Run(() => CreateVm());

        // One sample on each of the target's edges, delivered by the provider at the exact moments the
        // view model subscribes and unsubscribes — see FakeGazeProvider.SampleOnSubscribe. The sidecar
        // window is defined by its first and last SAMPLE (GroundTruthWindow.DisplayedFromUs/EndedAtUs),
        // so without these the bounds are wherever the 25 ms loop happened to be.
        long t0 = Environment.TickCount64;
        GazePoint EdgeSample()
        {
            var target = vm.CurrentTargetPosition;
            long us = (Environment.TickCount64 - t0) * 1000;
            return aimAtTarget(vm.CurrentTargetIndex)
                ? new GazePoint(target.X + 5, target.Y - 5, us, IsValid: true)
                : new GazePoint(1920, 100, us, IsValid: true);
        }
        _provider.SampleOnSubscribe = EdgeSample;
        _provider.SampleOnUnsubscribe = EdgeSample;

        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        using var stop = new CancellationTokenSource();
        int stalledFor = -1;
        long targetStart = Environment.TickCount64;
        int lastIndex = 0;
        var firing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                int index = vm.CurrentTargetIndex;
                if (index != lastIndex) { lastIndex = index; targetStart = Environment.TickCount64; }

                if (stallMs > 0 && index > stalledFor && Environment.TickCount64 - targetStart >= 4_000)
                {
                    // Starts 4 s into the target and runs past the 5 s advance, so the loop's last
                    // sample falls ~1.5 s short of the end — the CI shape exactly.
                    stalledFor = index;
                    Thread.Sleep(stallMs);
                }

                var target = vm.CurrentTargetPosition;
                long us = (Environment.TickCount64 - t0) * 1000;
                var point = aimAtTarget(index)
                    ? new GazePoint(target.X + 5, target.Y - 5, us, IsValid: true)
                    : new GazePoint(1920, 100, us, IsValid: true);
                _provider.FireGaze(point);
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        await signal.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), TestContext.Current.CancellationToken);
        stop.Cancel();
        try { await firing; } catch (OperationCanceledException) { }

        // The edge hooks belong to this probe; a test that continues with a normal calibration in the
        // same instance (ProbeThenCalibrateNow) must not get edge samples injected into it.
        _provider.SampleOnSubscribe = null;
        _provider.SampleOnUnsubscribe = null;

        return (vm, ReadSidecar(ProbeSidecarPath()));
    }

    private static GroundTruthSession ReadSidecar(string path) =>
        JsonSerializer.Deserialize<GroundTruthSession>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            })!;

    private static double WindowMs(GroundTruthWindow window) =>
        (window.EndedAtUs - window.DisplayedFromUs) / 1000.0;

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

        var states = new List<CalibrationState>();
        _messenger.Register<CalibrationStateChangedMessage>(this, (_, m) => states.Add(m.State));

        var (vm, sidecar) = await RunProbe(_ => true);

        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.IsMeasureOnly).IsTrue();
        Check.That(_service.IsSessionActive).IsFalse();
        Check.That(_service.CurrentTransform).IsSameReferenceAs(transformBefore);
        Check.That(File.ReadAllBytes(CalibrationPath).SequenceEqual(fileBefore)).IsTrue();

        // One Active per session: the coordinator's suppression-release edge runs exactly once.
        Check.That(states.Count(s => s == CalibrationState.Active)).IsEqualTo(1);
        Check.That(states[^1]).IsEqualTo(CalibrationState.Active);
        Check.That(states.Any(s => s == CalibrationState.Failed)).IsFalse();

        Check.That(sidecar.Mode).IsEqualTo(GroundTruthMode.MeasureOnly);
        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Completed);
        Check.That(sidecar.Windows).HasSize(5);
        Check.That(sidecar.Windows[0].Samples).Not.IsEmpty();   // self-contained, no recorder involved
    }

    [Fact]
    public async Task APartialProbe_ContinuesPastAnEmptyTarget_AndRecordsPartial()
    {
        // Target 1 gets no gaze at all: the probe must move on — ending here would throw away the
        // windows the five-target decision rule needs, which is exactly what a large bias would cause.
        var completions = new List<CalibrationCompletedMessage>();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, m) => completions.Add(m));

        var (vm, sidecar) = await RunProbe(index => index != 1);

        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.ErrorText).IsNull();
        Check.That(_service.IsSessionActive).IsFalse();
        Check.That(completions).Not.IsEmpty();
        Check.That(completions.Any(c => !c.Success)).IsFalse();

        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Partial);
        Check.That(sidecar.Windows).HasSize(5);
        Check.That(sidecar.Windows[1].ArrivedAtUs).IsNull();    // the empty target is recorded, not dropped
        Check.That(sidecar.Windows[1].Samples).Not.IsEmpty();   // and keeps the samples it did see
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
    public async Task TheFixedBudget_EndsAnEmptyProbeInAboutTwentyFiveSeconds()
    {
        // A gaze-only user cannot press Esc, so measure-only does not wait out the normal 8 s / 25 s
        // caps: each target gets 5 s after display. Gaze is on screen the whole time (so every window
        // carries samples) but far from every target's band, so no target can complete early.
        var started = Environment.TickCount64;
        var (vm, sidecar) = await RunProbe(_ => false, timeoutSeconds: 40);
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        Check.That(elapsed.TotalSeconds).IsGreaterOrEqualThan(23.0);   // five 5 s budgets
        Check.That(elapsed.TotalSeconds).IsStrictlyLessThan(32.0);     // and not a normal cap (5 x 8 s)
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.ErrorText).IsNull();

        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Partial);
        Check.That(sidecar.Windows).HasSize(5);
        Check.That(sidecar.Windows).ContainsOnlyElementsThatMatch(w => w.Samples.Count > 0);
    }

    /// <summary>
    /// V4: the 5 s window is FIXED, not a maximum. Real-time gaze sits 5 px from each target, so at
    /// the slider minimums the collector completes after ~5 samples — under the early-advance rule
    /// that window would be ~160 ms with no post-guard sample at all, and every small-bias probe would
    /// come back "no steady holds". The window must still be ~5 s, and a target that arrived but is
    /// not complete must still advance at its cap, not extend to 25 s.
    /// <para>
    /// The window is the sidecar's sample span: <c>DisplayedFromUs</c> and <c>EndedAtUs</c> are the
    /// target's first and last sample (GroundTruth.cs), so a real tracker's period — ~11 ms at 90 Hz —
    /// is the only gap between them and the target's display/advance edges. The harness preserves that
    /// by delivering one sample on each edge from the provider's subscribe/unsubscribe hooks; without
    /// them the span is the firing loop's cadence, which a loaded runner stretches by seconds (see the
    /// stall regression below).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1000, 300, 15)]   // shipped defaults
    [InlineData(200, 0, 5)]       // slider minimums
    public async Task V4_MeasureOnlyWindow_IsAFixedFiveSeconds_WhenGazeIsOnTarget(
        int holdMs, int settleMs, int samplesPerPoint)
    {
        _settings = new CalibrationSettings
        {
            PointCount = 5, TargetHoldMs = holdMs, SettleWindowMs = settleMs, SamplesPerPoint = samplesPerPoint,
        };

        var started = Environment.TickCount64;
        var (vm, sidecar) = await RunProbe(_ => true, timeoutSeconds: 40);
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        AssertFixedFiveSecondWindows(vm, sidecar, elapsed);
    }

    /// <summary>
    /// The CI failure this pins, deterministically: the sample loop stalls for 1.5 s from 4 s into
    /// each target, so it is still stalled when the target advances — the runner's stall (measured at
    /// 0.8–1.2 s, from the same position) cut the windows to 3.8–4.2 s. The target still was displayed
    /// for its full budget, so the window must still read 5 s.
    /// </summary>
    [Fact]
    public async Task V4_MeasureOnlyWindow_Holds_WhenTheSampleSourceStalls()
    {
        _settings = new CalibrationSettings
        {
            PointCount = 5, TargetHoldMs = 1000, SettleWindowMs = 300, SamplesPerPoint = 15,
        };

        var started = Environment.TickCount64;
        var (vm, sidecar) = await RunProbe(_ => true, timeoutSeconds: 40, stallMs: 1_500);
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        AssertFixedFiveSecondWindows(vm, sidecar, elapsed);
    }

    private static void AssertFixedFiveSecondWindows(
        CalibrationViewModel vm, GroundTruthSession sidecar, TimeSpan elapsed)
    {
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(elapsed.TotalSeconds).IsGreaterOrEqualThan(23.0);
        Check.That(elapsed.TotalSeconds).IsStrictlyLessThan(32.0);   // an extension (25 s) would exceed this
        Check.That(sidecar.Windows).HasSize(5);
        foreach (var window in sidecar.Windows)
        {
            Check.That(WindowMs(window)).IsGreaterOrEqualThan(4_700.0);
            Check.That(WindowMs(window)).IsStrictlyLessThan(5_400.0);
            // Enough samples after the onset guard for a 5-sample cluster at any calibration setting.
            long guardEnd = window.DisplayedFromUs + 300_000;
            int eligible = window.Samples.Count(s => s.Valid && s.TimestampUs >= guardEnd && s.TimestampUs <= window.EndedAtUs);
            Check.That(eligible).IsGreaterOrEqualThan(30);
        }
    }

    [Fact]
    public async Task MeasureOnlyRing_ShowsTheElapsedShareOfTheWindow_NotSaturatedSamples()
    {
        // Holistic review N2: the ring counted in-band samples against SamplesPerPoint, so at shipped
        // defaults it was full after ~0.5 s and then sat full for the rest of the fixed 5 s window.
        // It now tracks the window itself: about 1.5 s into a 5 s target it reads about 0.3.
        _settings = new CalibrationSettings
        {
            PointCount = 5, TargetHoldMs = 1000, SettleWindowMs = 300, SamplesPerPoint = 15,
        };
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        long t0 = Environment.TickCount64;
        await FireForAbout(1_500, vm, t0, onTarget: true, valid: true);
        double elapsedMs = FireOnceAndMeasure(vm, t0, onTarget: true, valid: true);
        SkipIfTheThreadStalledToTheCap(elapsedMs);

        Check.That(vm.CurrentTargetIndex).IsEqualTo(0);   // still the first target: the window is 5 s
        Check.That(vm.IsOnTarget).IsTrue();
        AssertRingMatchesTheWindowClock(vm, elapsedMs);   // in-band counting read 1.0 here

        vm.Cancel();
    }

    [Fact]
    public async Task MeasureOnlyRing_KeepsMovingWhileGazeIsLost()
    {
        // Review A's FU10-2: the ring shows time, so a blink or lost tracking must not freeze it. Only
        // INVALID samples arrive here; the old ring sat below the validity return and read 0 throughout.
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        long t0 = Environment.TickCount64;
        await FireForAbout(1_000, vm, t0, onTarget: false, valid: false);
        double elapsedMs = FireOnceAndMeasure(vm, t0, onTarget: false, valid: false);
        SkipIfTheThreadStalledToTheCap(elapsedMs);

        Check.That(vm.CurrentTargetIndex).IsEqualTo(0);
        AssertRingMatchesTheWindowClock(vm, elapsedMs);
        Check.That(vm.TargetProgress).IsStrictlyGreaterThan(0.0);

        vm.Cancel();
    }

    [Fact]
    public async Task MeasureOnlyRing_ASampleArrivingAsTheHandlerAttaches_IsTimedAgainstTheNewTarget()
    {
        // Review A's FU10-1: MoveToNextTarget attached the gaze handler BEFORE starting the target's
        // clock, so a tracker-thread sample landing in that gap was timed against the previous start —
        // for the first target the field's default 0, for later ones ~5 s ago — and the ring flashed
        // full on a brand-new dot. This provider delivers a sample the instant the handler attaches.
        //
        // Target 1 is the realistic case (review A's FU11-3): its predecessor's clock is ~5 s old
        // there, and a clock set once per session instead of per target reads full on every dot after
        // the first — which target 0 alone cannot see, since both clocks start together.
        var provider = new FiresOnSubscribeProvider();
        var vm = await Task.Run(() => new CalibrationViewModel(
            _settings, _service, _messenger, provider, Screen, "test-monitor", CalibrationPath,
            groundTruth: new ScriptedGroundTruthRecorder(SidecarPath),
            diagnostics: new DiagnosticsSettings { RecordRawGaze = false }));
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        Check.That(provider.DeliveredOnSubscribe).IsEqualTo(1);
        Check.That(vm.TargetProgress).IsStrictlyLessThan(0.05);   // the old order read 1.0

        // Nothing else fires, so target 0's own 5 s cap advances it, and the re-subscribe delivers again.
        var deadline = Environment.TickCount64 + 10_000;
        while (provider.DeliveredOnSubscribe < 2 && Environment.TickCount64 < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Check.That(provider.DeliveredOnSubscribe).IsEqualTo(2);
        Check.That(vm.CurrentTargetIndex).IsEqualTo(1);
        Check.That(vm.TargetProgress).IsStrictlyLessThan(0.05);   // a per-session clock reads 1.0 here

        vm.Cancel();
    }

    /// <summary>Delivers one valid, off-target sample synchronously the moment a handler attaches.</summary>
    private sealed class FiresOnSubscribeProvider : IGazeProvider
    {
        private Action<GazePoint>? _handlers;
        private int _delivered;

        /// <summary>Counted AFTER the handler returns, so a reader never sees a delivery still in flight.</summary>
        public int DeliveredOnSubscribe => Volatile.Read(ref _delivered);

        public event Action<GazePoint>? GazePointReceived
        {
            add
            {
                _handlers += value;
                value?.Invoke(new GazePoint(1920, 100, 1_000, IsValid: true));
                Interlocked.Increment(ref _delivered);
            }
            remove => _handlers -= value;
        }

        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    /// <summary>Fires gaze every 25 ms for about <paramref name="durationMs"/>, in real time.</summary>
    private async Task FireForAbout(int durationMs, CalibrationViewModel vm, long t0, bool onTarget, bool valid)
    {
        while (Environment.TickCount64 - t0 < durationMs)
        {
            FireOne(vm, t0, onTarget, valid);
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private void FireOne(CalibrationViewModel vm, long t0, bool onTarget, bool valid)
    {
        var target = vm.CurrentTargetPosition;
        var at = onTarget ? new PhysicalPoint(target.X + 5, target.Y - 5) : new PhysicalPoint(1920, 100);
        _provider.FireGaze(new GazePoint(at.X, at.Y, (Environment.TickCount64 - t0) * 1000, IsValid: valid));
    }

    /// <summary>
    /// Fires one more sample and returns the elapsed time measured right AFTER it, so the ring value it
    /// produced can be compared with the clock no matter how long the loop before it was stalled.
    /// </summary>
    private double FireOnceAndMeasure(CalibrationViewModel vm, long t0, bool onTarget, bool valid)
    {
        FireOne(vm, t0, onTarget, valid);
        return Environment.TickCount64 - t0;
    }

    /// <summary>
    /// A stall long enough to reach the 5 s cap advances the target, so that run is skipped rather than
    /// misjudged. Called BEFORE the test's own "still on target 0" check (review A's FU11-1): after it,
    /// a stall past the cap failed the index check and the skip was unreachable.
    /// </summary>
    private static void SkipIfTheThreadStalledToTheCap(double elapsedMs)
    {
        if (elapsedMs >= 4_500)
            Assert.Skip($"The test thread stalled to {elapsedMs:0} ms — too close to the 5 s cap to judge the ring.");
    }

    /// <summary>
    /// The ring must be the elapsed share of the fixed window: exact up to tick resolution and the few
    /// milliseconds between the target's own clock start and <c>t0</c>, so ±0.05 (250 ms). Immune to a
    /// thread-pool stall (review A's FU10-3), and a stale divisor fails it — 1.5 s over 3 s reads 0.5,
    /// not 0.3.
    /// </summary>
    private static void AssertRingMatchesTheWindowClock(CalibrationViewModel vm, double elapsedMs)
    {
        SkipIfTheThreadStalledToTheCap(elapsedMs);
        Check.That(Math.Abs(vm.TargetProgress - (elapsedMs / 5_000.0))).IsStrictlyLessThan(0.05);
    }

    // ── Session-state teeth ───────────────────────────────────────────────────

    [Fact]
    public async Task ProbeThenCalibrateNow_DoesARealFitAndSave_AndClearsTheMode()
    {
        // The mode is per session, never sticky: a probe followed by "Calibrate Now" on the same
        // app-lifetime view model must fit, save and apply, not silently skip all three.
        var (vm, _) = await RunProbe(_ => true);
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
        // lets the normal failure path discard the data).
        _messenger.Register<CalibrationTargetPositionMessage>(this, (_, m) =>
        {
            if (m.TargetIndex == 1) throw new InvalidOperationException("position subscriber fault");
        });
        var completion = new TaskCompletionSource();
        var completions = new List<CalibrationCompletedMessage>();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, m) =>
        {
            completions.Add(m);
            completion.TrySetResult();
        });

        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        // The target's own 5 s cap is what advances it into the faulting send.
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

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
