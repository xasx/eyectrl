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
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// The measure-only bias probe, end to end at the view-model layer: five targets whatever the
/// settings say; a fixed 5 s window per target — in-band gaze does NOT shorten it, because a short
/// window at low calibration settings leaves the estimator no cluster to find; continue past a target
/// that collected nothing; a per-session sidecar on every ending; apply nothing. The named mutation
/// for the persistence checks is restoring <c>AbandonSession</c> on any of these paths — it discards
/// the only data the probe exists to collect, and these tests turn red.
/// <para>
/// <b>One fake clock, exact windows.</b> The view model's timers (the per-target safety caps, the
/// ring, the hold floor) and the collector's settle gate run on an injected <see cref="TimeProvider"/>,
/// and this harness drives a <see cref="FakeTimeProvider"/>: a target's 5 s budget is 5 s of test
/// time, whatever the machine is doing. This replaces the real-time firing loop the earlier fix kept,
/// whose window bounds depended on where a loaded runner's stall happened to fall (the CI flakes: a
/// 5 s window read as 3844–4219 ms, and the upper bound as 5719 ms). The harness's gaze timestamps —
/// which are what the sidecar's window bounds ARE — come from the same fake clock, so there is no
/// second timeline for a stall to stretch.
/// </para>
/// <para>
/// "Exact" here is exact to the pump's 25 ms step, and no finer: a step advances the clock and then
/// runs the callbacks due at its end, so the cap's continuation reads the clock at the step's end —
/// any budget in (4975, 5000] ms fires on the 5000 ms step and the window reads 5000.0. That is still
/// about 28× tighter than the 4700–5400 band the real-time harness needed (700 ms wide).
/// </para>
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

    /// <summary>
    /// The one clock the session runs on: the view model's caps, hold floor and ring, and the
    /// service's collector (the settle gate) all read this instance, and every sample the harness
    /// fires carries its elapsed time — so there is no second timeline for a slow runner to stretch.
    /// It counts timer creations; see <see cref="ArmingTimeProvider"/> for why the pump needs that.
    /// </summary>
    private readonly ArmingTimeProvider _time = new();

    /// <summary>The fake clock's reading when this test began; <see cref="ElapsedMs"/> is relative to it.</summary>
    private readonly DateTimeOffset _epoch;

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly CalibrationService _service;
    private readonly FakeGazeProvider _provider = new();

    public CalibrationMeasureOnlyTests()
    {
        _epoch = _time.GetUtcNow();
        _service = new CalibrationService(timeProvider: _time);
    }

    /// <summary>Fake-clock milliseconds since this test began — the timeline every window is exact in.</summary>
    private long ElapsedMs => (long)(_time.GetUtcNow() - _epoch).TotalMilliseconds;
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

    /// <summary>
    /// A <see cref="FakeTimeProvider"/> that counts the timers created on it — how the pump knows a
    /// target's safety cap is ARMED. Production arms it (<c>Task.Delay</c> → <c>CreateTimer</c>) as
    /// the LAST statement of the target's setup, after <c>CurrentTargetIndex</c> was already
    /// incremented and the edge sample delivered. A pump that watched only the index could see the
    /// increment mid-transition, advance the next window's clock, and then leave the new cap due
    /// later than the pump expected — the one nondeterminism a fake clock must not have.
    /// </summary>
    private sealed class ArmingTimeProvider : FakeTimeProvider
    {
        private int _timersCreated;

        /// <summary>Timers created so far. One per target: the measure-only cap never extends.</summary>
        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            // AFTER the real timer exists: a reader that sees the count may advance the clock knowing
            // the cap is armed with its full budget from this instant.
            Interlocked.Increment(ref _timersCreated);
            return timer;
        }
    }

    private CalibrationViewModel CreateVm(bool recordRawGaze = false, IGroundTruthSink? sink = null) => new(
        _settings, _service, _messenger, _provider, Screen, "test-monitor", CalibrationPath,
        groundTruth: sink ?? new ScriptedGroundTruthRecorder(
            SidecarPath, utcNow: () => _time.GetUtcNow().UtcDateTime),
        // RecordRawGaze OFF by default: the probe's sidecar must not hinge on the global recorder flag.
        diagnostics: new DiagnosticsSettings { RecordRawGaze = recordRawGaze },
        timeProvider: _time);

    /// <summary>Advances the fake clock — the only timeline the timers under test can see.</summary>
    private void AdvanceMs(long ms) => _time.Advance(TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// A gaze sample at the fake clock's current reading. <paramref name="onTarget"/> decides whether
    /// it sits in the current target's band; its timestamp IS the sidecar's window timeline.
    /// </summary>
    private GazePoint GazeAt(CalibrationViewModel vm, bool onTarget, bool valid = true)
    {
        var target = vm.CurrentTargetPosition;
        var at = onTarget ? new PhysicalPoint(target.X + 5, target.Y - 5) : new PhysicalPoint(1920, 100);
        return new GazePoint(at.X, at.Y, ElapsedMs * 1000, IsValid: valid);
    }

    private void Fire(CalibrationViewModel vm, bool onTarget, bool valid = true) =>
        _provider.FireGaze(GazeAt(vm, onTarget, valid));

    /// <summary>
    /// Bounded real-time wait for an asynchronous transition (a timer's continuation can run on a
    /// pool thread; the fake clock is frozen meanwhile, so nothing about the measured windows depends
    /// on when exactly it lands). The bound is a generous failsafe — it only ever covers a pool
    /// continuation landing, never a measured value, so a starved runner cannot make it a verdict.
    /// A correct build takes milliseconds; a broken one fails with a sentence instead of hanging.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 30_000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"Waited {timeoutMs} ms: {what}");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Completes the current NORMAL-calibration target at the user's pace and advances.</summary>
    private async Task FinishTarget(CalibrationViewModel vm)
    {
        int index = vm.CurrentTargetIndex;

        // Arrival (discarded) plus enough counted samples (SettleWindowMs is 0 in the settings that
        // use this helper, so the second sample on counts).
        for (int i = 0; i < _settings.SamplesPerPoint + 2; i++)
            Fire(vm, onTarget: true);

        // Past the visible hold floor; the next sample is what triggers the advance.
        AdvanceMs(_settings.TargetHoldMs + 60);
        Fire(vm, onTarget: true);

        await WaitUntil(
            () => vm.CurrentTargetIndex != index,
            $"target {index} (complete, past its hold floor) never advanced");
    }

    /// <summary>
    /// Drives a whole probe on the fake clock: <paramref name="aimAtTarget"/> decides, per target
    /// index, whether gaze sits on the target (so the collector sees in-band samples) or far away
    /// (so the target ends empty at its cap). Returns the VM and its sidecar when the session ends.
    /// Each target runs exactly its fixed budget — 200 × 25 ms of fake time — and the loop stops at
    /// the deadline, so what advances the target is its own cap firing on the clock, never the
    /// harness's pace.
    /// </summary>
    /// <param name="stallMs">
    /// Suppresses the 25 ms sample loop for the last <paramref name="stallMs"/> of every window —
    /// the stand-in for the loaded runner whose sample source stalls across the target's end. The
    /// window must still read its full budget: only the provider's edge samples describe its bounds
    /// (see the V4 regression test). Zero is the normal harness.
    /// </param>
    private async Task<(CalibrationViewModel Vm, GroundTruthSession Sidecar)> RunProbe(
        Func<int, bool> aimAtTarget, int timeoutSeconds = 30, int stallMs = 0)
    {
        const long budgetMs = 5_000;   // the probe's fixed per-target budget, as the spec fixes it

        // A fresh recipient object per call, so a test may subscribe to the completion message itself
        // without the messenger reporting a duplicate registration.
        var signal = new TaskCompletionSource();
        var probe = new object();
        _messenger.Register<CalibrationCompletedMessage>(probe, (_, _) => signal.TrySetResult());

        var vm = await Task.Run(() => CreateVm());

        // One sample on each of the target's edges, delivered by the provider at the exact instants the
        // view model subscribes and unsubscribes — see FakeGazeProvider.SampleOnSubscribe. The sidecar
        // window is defined by its first and last SAMPLE (GroundTruthWindow.DisplayedFromUs/EndedAtUs),
        // and these carry the fake clock's reading, so the bounds are the target's own display/advance
        // instants exactly, wherever the sample loop happened to be.
        GazePoint EdgeSample()
        {
            var target = vm.CurrentTargetPosition;
            long us = ElapsedMs * 1000;
            return aimAtTarget(vm.CurrentTargetIndex)
                ? new GazePoint(target.X + 5, target.Y - 5, us, IsValid: true)
                : new GazePoint(1920, 100, us, IsValid: true);
        }
        _provider.SampleOnSubscribe = EdgeSample;
        _provider.SampleOnUnsubscribe = EdgeSample;

        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        for (int index = 0; ; index++)
        {
            // The deadline is the arm time + the budget. The arm happened at the current fake time:
            // the wait below does not let the clock move until the target's cap exists, so the budget
            // this target's own timer carries runs from exactly here.
            long windowEnd = ElapsedMs + budgetMs;
            while (ElapsedMs < windowEnd)
            {
                AdvanceMs(25);
                if (ElapsedMs < windowEnd - stallMs)
                    Fire(vm, aimAtTarget(index));
            }

            // Either the session ended, or the next target is SHOWING — and its own cap is ARMED
            // (see ArmingTimeProvider). Waiting for the arm, not just the index, is what keeps the
            // frozen clock from overtaking a transition still mid-flight on the view model's thread.
            // A timer continuation may hop a pool thread; the clock stays frozen until it lands.
            await WaitUntil(
                () => vm.State != CalibrationState.Collecting
                    || (vm.CurrentTargetIndex != index && _time.TimersCreated >= index + 2),
                $"target {index}'s {budgetMs} ms budget never ended "
                + $"(state {vm.State}, index {vm.CurrentTargetIndex})",
                timeoutMs: timeoutSeconds * 1000);
            if (vm.State != CalibrationState.Collecting) break;
        }

        // The edge hooks belong to this probe; a test that continues with a normal calibration in the
        // same instance (ProbeThenCalibrateNow) must not get edge samples injected into it.
        _provider.SampleOnSubscribe = null;
        _provider.SampleOnUnsubscribe = null;

        await signal.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), TestContext.Current.CancellationToken);
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
            await FinishTarget(vm);

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

        for (int i = 0; i < 5; i++)
            Fire(vm, onTarget: true);

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
        //
        // On the fake clock this is the exact claim, not a 23–32 s band: five budgets of exactly 5 s
        // are exactly 25 s — and it is the 25 s that separates the probe's budget from a normal cap
        // (5 x 8 s) or a full extension (5 x 25 s), which is what the old band was really pinning.
        // Exact to the pump's 25 ms step, like every window here (see the class docs).
        var (vm, sidecar) = await RunProbe(_ => false, timeoutSeconds: 40);

        Check.That(ElapsedMs).IsEqualTo(25_000);
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.ErrorText).IsNull();

        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Partial);
        Check.That(sidecar.Windows).HasSize(5);
        Check.That(sidecar.Windows).ContainsOnlyElementsThatMatch(w => w.Samples.Count > 0);
    }

    /// <summary>
    /// V4: the 5 s window is FIXED, not a maximum. Gaze sits 5 px from each target, so at
    /// the slider minimums the collector completes after ~5 samples — under the early-advance rule
    /// that window would be ~160 ms with no post-guard sample at all, and every small-bias probe would
    /// come back "no steady holds". The window must be exactly the 5 s budget, and a target that
    /// arrived but is not complete must still advance at its cap, not extend to 25 s.
    /// <para>
    /// The window is the sidecar's sample span: <c>DisplayedFromUs</c> and <c>EndedAtUs</c> are the
    /// target's first and last sample (GroundTruth.cs), so a real tracker's period — ~11 ms at 90 Hz —
    /// is the only gap between them and the target's display/advance edges. The harness preserves that
    /// by delivering one sample on each edge from the provider's subscribe/unsubscribe hooks; on the
    /// fake clock the span is therefore exact to the pump's 25 ms step (class docs) — not the 4.7–5.4 s
    /// band a loaded runner forced, and not the 3.8 s a stalled loop produced (see the stall regression
    /// below).
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

        var (vm, sidecar) = await RunProbe(_ => true, timeoutSeconds: 40);

        AssertFixedFiveSecondWindows(vm, sidecar);
    }

    /// <summary>
    /// The CI failure this pins, deterministically: the sample loop stalls for the last 1.5 s of
    /// every target, so it is still stalled when the target advances — the runner's stall (measured
    /// at 0.8–1.2 s, from the same position) cut the windows to 3.8–4.2 s. The target still was
    /// displayed for its full budget, so the window must still read 5 s.
    /// <para>
    /// On the fake clock it pins one more thing: production detaches the gaze handler BEFORE
    /// incrementing <c>CurrentTargetIndex</c>, so the stall's end edge — the provider's unsubscribe
    /// sample, taken at the 5 s budget — is recorded into the OLD window and gives it its full span.
    /// Swapped (increment then detach), that edge would land in the next target's window and this
    /// one would close at its last loop sample, about 3500 ms.
    /// </para>
    /// </summary>
    [Fact]
    public async Task V4_MeasureOnlyWindow_Holds_WhenTheSampleSourceStalls()
    {
        _settings = new CalibrationSettings
        {
            PointCount = 5, TargetHoldMs = 1000, SettleWindowMs = 300, SamplesPerPoint = 15,
        };

        var (vm, sidecar) = await RunProbe(_ => true, timeoutSeconds: 40, stallMs: 1_500);

        AssertFixedFiveSecondWindows(vm, sidecar);
    }

    private static void AssertFixedFiveSecondWindows(
        CalibrationViewModel vm, GroundTruthSession sidecar)
    {
        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(sidecar.Windows).HasSize(5);
        foreach (var window in sidecar.Windows)
        {
            // Exact on the fake clock: the window IS the target's display-to-advance span, so a
            // shortened or extended budget shows up as a number, not as a slow-runner race.
            Check.That(WindowMs(window)).IsEqualTo(5_000.0);
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
        // It now tracks the window itself: exactly 1.5 s into a 5 s target it reads 0.3.
        _settings = new CalibrationSettings
        {
            PointCount = 5, TargetHoldMs = 1000, SettleWindowMs = 300, SamplesPerPoint = 15,
        };
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        AdvanceMs(1_500);
        Fire(vm, onTarget: true, valid: true);

        Check.That(vm.CurrentTargetIndex).IsEqualTo(0);   // still the first target: the window is 5 s
        Check.That(vm.IsOnTarget).IsTrue();
        // Exact on the fake clock: in-band counting read 1.0 here, a stale divisor (3 s) reads 0.5.
        Check.That(vm.TargetProgress).IsCloseTo(0.3, 1e-9);

        vm.Cancel();
    }

    [Fact]
    public async Task MeasureOnlyRing_KeepsMovingWhileGazeIsLost()
    {
        // Review A's FU10-2: the ring shows time, so a blink or lost tracking must not freeze it. Only
        // INVALID samples arrive here; the old ring sat below the validity return and read 0 throughout.
        var vm = await Task.Run(() => CreateVm());
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        AdvanceMs(1_000);
        Fire(vm, onTarget: false, valid: false);

        Check.That(vm.CurrentTargetIndex).IsEqualTo(0);
        Check.That(vm.TargetProgress).IsCloseTo(0.2, 1e-9);
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
            groundTruth: new ScriptedGroundTruthRecorder(
                SidecarPath, utcNow: () => _time.GetUtcNow().UtcDateTime),
            diagnostics: new DiagnosticsSettings { RecordRawGaze = false },
            timeProvider: _time));
        _messenger.Send(new MeasureOnlyCalibrationRequestedMessage());

        Check.That(provider.DeliveredOnSubscribe).IsEqualTo(1);
        Check.That(vm.TargetProgress).IsStrictlyLessThan(0.05);   // the old order read 1.0

        // Nothing else fires, so target 0's own 5 s cap advances it, and the re-subscribe delivers again.
        AdvanceMs(5_000);
        await WaitUntil(
            () => provider.DeliveredOnSubscribe == 2,
            "target 0's cap never advanced to target 1");

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
            await FinishTarget(vm);

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
        var completions = new List<CalibrationCompletedMessage>();
        _messenger.Register<CalibrationCompletedMessage>(this, (_, m) => completions.Add(m));

        // The pump drives target 0 through its own 5 s cap, which advances it into the faulting send.
        var (vm, sidecar) = await RunProbe(_ => true, timeoutSeconds: 15);

        Check.That(vm.State).IsEqualTo(CalibrationState.Active);
        Check.That(vm.ErrorText).IsNull();
        Check.That(_service.IsSessionActive).IsFalse();
        Check.That(completions.Any(c => !c.Success)).IsFalse();
        Check.That(sidecar.Outcome).IsEqualTo(GroundTruthOutcome.Failed);
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
        var sessionStartMs = ElapsedMs;
        _messenger.Send(new CalibrationRequestedMessage());
        await FinishTarget(vm);
        await FinishTarget(vm);
        Check.That(vm.CurrentTargetIndex).IsEqualTo(2);

        // Just past target 0's first-stage cap (~8 s after it was shown), well short of target 2's
        // own — on the fake clock this is exact rather than a sleep long enough to hope.
        AdvanceMs(8_200 - (ElapsedMs - sessionStartMs));

        Check.That(vm.State).IsEqualTo(CalibrationState.Collecting);
        Check.That(vm.CurrentTargetIndex).IsEqualTo(2);
        Check.That(vm.ErrorText).IsNull();

        vm.Cancel();
    }
}
