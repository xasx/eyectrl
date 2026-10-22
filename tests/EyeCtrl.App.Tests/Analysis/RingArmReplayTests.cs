namespace EyeCtrl.App.Tests.Analysis;

using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;
using static EyeCtrl.App.Tests.Analysis.RingPinModeReplayTests;

/// <summary>
/// Ring-stutter investigation, phase 3 (scratch, not for main). One engine run per config records the
/// event trace the adapter would deliver — Core's pin/release/settle/state/offer events of a sample in
/// order, then that sample's cursor frame — and every arm is a <see cref="RingModel"/> replayed over the
/// same trace. The model mirrors <see cref="CursorCircleViewModel"/> line for line; the equivalence
/// tests prove its today / run-only / never arms draw exactly what three REAL VMs draw on the same
/// stream, so the two new knobs — a glide-out, and the hybrid's hold at P — are the only differences.
/// Env-gated on <c>EYECTRL_GAZE_RECORDING</c>; <c>EYECTRL_RING_ARMS_OUT</c> writes the markdown.
/// </summary>
public class RingArmReplayTests(ITestOutputHelper output)
{
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";
    private const string ReportEnvironmentVariable = "EYECTRL_RING_ARMS_OUT";
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalRect ToolbarRect = new(914, 0, 2012, 280);

    // ── The trace ──

    internal enum Kind { CorePin, CoreRelease, Settled, State, Cursor, OfferShown, OfferClosed }

    internal readonly record struct Ev(Kind Kind, PhysicalPoint Point = default, InteractionState State = default,
        GazeFrame Frame = default, ActivationOffer? Offer = null);

    internal sealed record SampleRow(long Us, GazePoint Raw, PhysicalPoint Filtered, InteractionState State,
        ActivationOffer? Offer, int FirstEvent, int EventCount);

    internal sealed record Trace(Config Config, List<SampleRow> Samples, List<Ev> Events, int Offers, int Fires,
        Replay LiveVms);

    /// <summary>One arm's drawn ring per sample, plus the hybrid's hold ledger when it has one.</summary>
    internal sealed record ArmTrace(string Name, PhysicalPoint[] Drawn, bool[] Visible, bool[] Pinned,
        HoldLedger? Holds = null, bool[]? Holding = null);

    internal sealed class HoldLedger
    {
        public int Holds { get; set; }
        public int Spurious { get; set; }
        public List<double> DurationsMs { get; } = new();
    }

    internal static Trace Record(IReadOnlyList<GazePoint> samples, Config config)
    {
        // The live, real-VM replay of phase 2 runs first; the trace run below is the same deterministic
        // engine walk, so its samples line up one for one (asserted).
        var live = Run(samples, config);

        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationRadiusPixels = config.RadiusPx;
        settings.Dwell.FixationDetectionMs = config.OnsetMs;
        settings.Dwell.ActivationButtonDwellMs = config.ButtonDwellMs;
        settings.Cursor.CircleSize = config.RingSizeDip;
        settings.SelectionMode = SelectionMode.ManualSelection;

        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);
        var im = engine.InteractionManager;
        im.SelectionMode = SelectionMode.ManualSelection;

        var events = new List<Ev>(samples.Count * 2);
        var state = InteractionState.FreeLook;
        ActivationOffer? liveOffer = null, pendingFire = null;
        long? closeManualSelectionAtUs = null;
        PhysicalPoint filtered = default;
        int offers = 0, fires = 0;

        im.PinRing += p => events.Add(new Ev(Kind.CorePin, Point: p));
        im.ReleaseRing += () => events.Add(new Ev(Kind.CoreRelease));
        im.FixationSettled += p => events.Add(new Ev(Kind.Settled, Point: p));
        im.StateChanged += s => { state = s; events.Add(new Ev(Kind.State, State: s)); };
        im.ActivationOfferShown += o => { liveOffer = o; offers++; events.Add(new Ev(Kind.OfferShown, Offer: o)); };
        im.ActivationOfferClosed += () => { liveOffer = null; events.Add(new Ev(Kind.OfferClosed)); };
        im.ActivationOfferFired += (o, _) => { pendingFire = o; fires++; };
        engine.CursorMoved += frame => { filtered = frame.Position; events.Add(new Ev(Kind.Cursor, Frame: frame)); };

        var visibility = new ToolbarVisibilityStub();
        var autoHide = new ToolbarAutoHideController(
            enabled: true, edge: "Top", screenW: Screen.Width, screenH: Screen.Height,
            toolbarRect: ToolbarRect, hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        autoHide.Attach(visibility);
        bool? lastVisible = null;

        var rows = new List<SampleRow>(samples.Count);
        engine.Start();
        foreach (var sample in samples)
        {
            long us = sample.TimestampMicroseconds;
            int first = events.Count;
            autoHide.Feed(sample, us);
            if (lastVisible != visibility.Visible)
            {
                lastVisible = visibility.Visible;
                im.SetActivationChromeZones(visibility.Visible ? new[] { ToolbarRect } : Array.Empty<PhysicalRect>());
            }
            provider.Raise(sample);
            if (pendingFire is { } fired)
            {
                pendingFire = null;
                if (im.OnActivationButtonDwelled(fired) == ActivationCompletionResult.Accepted)
                    closeManualSelectionAtUs = us + ManualSelectionVisitUs;
            }
            if (closeManualSelectionAtUs is { } closeAt && us >= closeAt)
            {
                closeManualSelectionAtUs = null;
                im.OnManualSelectionBack();
            }
            rows.Add(new SampleRow(us, sample, filtered, state, liveOffer, first, events.Count - first));
        }

        Check.That(rows.Count).IsEqualTo(live.Rows.Count);
        return new Trace(config, rows, events, offers, fires, live);
    }

    // ── The ring model: CursorCircleViewModel, line for line, plus the two knobs ──

    internal sealed class RingModel
    {
        /// <summary>0 = the VM's snap; otherwise the release eases to the (moving) gaze over this many ms.</summary>
        public int GlideOutMs { get; init; }

        private PhysicalPoint _latestGaze;
        private InteractionState _lastState = InteractionState.FreeLook;
        private bool _stateAllowsCursor = true;
        private bool _gliding;
        private PhysicalPoint _glideFrom, _glideTo;
        private long _glideStartUs = -1;
        private bool _glidingOut;
        private PhysicalPoint _outFrom;
        private long _outStartUs = -1;
        private CursorCircleViewModel.RingMove _moveTo;

        public PhysicalPoint Position { get; private set; }
        public PhysicalPoint Drawn { get; private set; }
        public bool IsVisible { get; private set; } = true;
        public bool IsPinned { get; private set; }

        // [ObservableProperty] notifies only on a change; the drawn ring is whichever channel last changed.
        private void SetPosition(PhysicalPoint p)
        {
            if (Position == p) return;
            Position = p;
            Drawn = p;
        }

        private void SetMoveTo(CursorCircleViewModel.RingMove move)
        {
            if (_moveTo == move) return;
            _moveTo = move;
            Drawn = move.Target;
        }

        public void Cursor(GazeFrame frame)
        {
            _latestGaze = frame.Position;
            if (_gliding) { AdvanceGlide(frame.TimestampMicroseconds); return; }
            if (_glidingOut) { AdvanceGlideOut(frame.TimestampMicroseconds); return; }
            if (IsVisible && !IsPinned) SetPosition(frame.Position);
        }

        /// <summary>The VM's pin. <paramref name="force"/> is the hybrid's hold, which may start in
        /// FreeLook (the run at P has ended by the time the eye reaches the button).</summary>
        public void Pin(PhysicalPoint point, bool force = false)
        {
            if (!force && _lastState != InteractionState.Fixation) return;
            // The VM glides from Position; a glide-out in flight has moved the drawn ring off Position,
            // so a glide-out arm starts from where the ring is actually drawn.
            _glideFrom = _glidingOut ? Drawn : Position;
            _glidingOut = false;
            _glideTo = point;
            _glideStartUs = -1;
            _gliding = true;
            IsPinned = true;
        }

        /// <summary>The hybrid's hold: the ring stops exactly where it is drawn, with no glide, and stays
        /// there (pinned) until released.</summary>
        public void HoldInPlace()
        {
            _gliding = false;
            _glidingOut = false;
            IsPinned = true;
            SetPosition(Drawn);
        }

        public void Release()
        {
            bool wasPinned = IsPinned;
            _gliding = false;
            IsPinned = false;
            if (GlideOutMs > 0 && wasPinned && IsVisible)
            {
                _outFrom = Drawn;
                _outStartUs = -1;
                _glidingOut = true;
                return;
            }
            _glidingOut = false;
            SetPosition(_latestGaze);
        }

        public void State(InteractionState state)
        {
            _lastState = state;
            if (state is not (InteractionState.Fixation or InteractionState.FreeLook))
            {
                _gliding = false;
                _glidingOut = false;
                IsPinned = false;
                SetPosition(_latestGaze);
            }
            _stateAllowsCursor = state is InteractionState.FreeLook or InteractionState.Fixation;
            bool wasVisible = IsVisible;
            bool nowVisible = _stateAllowsCursor;
            if (!wasVisible && nowVisible && !IsPinned)
                SetPosition(_latestGaze);
            IsVisible = nowVisible;
        }

        private void AdvanceGlide(long us)
        {
            if (_glideStartUs < 0)
            {
                _glideStartUs = us;
                SetMoveTo(new CursorCircleViewModel.RingMove(_glideFrom, Glide: true));
                return;
            }
            double elapsedMs = (us - _glideStartUs) / 1000.0;
            if (elapsedMs >= RingGlide.DurationMs)
            {
                _gliding = false;
                SetPosition(_glideTo);
                return;
            }
            SetMoveTo(new CursorCircleViewModel.RingMove(RingGlide.At(_glideFrom, _glideTo, elapsedMs), Glide: true));
        }

        // The same smoothstep as RingGlide, over GlideOutMs, toward the gaze as it is NOW (a moving target):
        // the ring lands on the eye, not on where the eye was when the release fired.
        private void AdvanceGlideOut(long us)
        {
            if (_outStartUs < 0)
            {
                _outStartUs = us;
                SetMoveTo(new CursorCircleViewModel.RingMove(_outFrom, Glide: true));
                return;
            }
            double elapsedMs = (us - _outStartUs) / 1000.0;
            if (elapsedMs >= GlideOutMs)
            {
                _glidingOut = false;
                SetPosition(_latestGaze);
                return;
            }
            double t = elapsedMs / GlideOutMs;
            double eased = t * t * (3 - (2 * t));
            SetMoveTo(new CursorCircleViewModel.RingMove(new PhysicalPoint(
                _outFrom.X + ((_latestGaze.X - _outFrom.X) * eased),
                _outFrom.Y + ((_latestGaze.Y - _outFrom.Y) * eased)), Glide: true));
        }
    }

    // ── The arms ──

    internal enum Pinning { Today, RunOnly, Never, Hybrid }

    /// <summary>How the hybrid's hold starts.
    /// <list type="bullet">
    /// <item><see cref="Freeze"/> (the hybrid): any entry of the gaze into a live button's hit area stops
    /// the ring where it is drawn; and a run that ends with the gaze inside the offer's no-move zone
    /// (hit radius + R around B) keeps its pin for up to <see cref="ArmSpec.ApproachMs"/> while the eye
    /// crosses into the hit area, so a ring pinned at P stays at P.</item>
    /// <item><see cref="KeepPin"/> (rejected, too narrow): only a run that ends with the gaze already IN the
    /// hit area keeps its pin; the filter crosses P→B in 2–3 samples, so it rarely fires.</item>
    /// <item><see cref="BackToP"/> (rejected): any entry pins the ring at the offer's P, which pulls a ring
    /// that was following the gaze BACK to P.</item>
    /// </list></summary>
    internal enum HoldStyle { Freeze, KeepPin, BackToP }

    internal sealed record ArmSpec(string Name, Pinning Pinning, int GlideOutMs = 0, int GraceMs = 0, double ExitMarginPx = 0,
        HoldStyle Hold = HoldStyle.Freeze, int ApproachMs = 150, int ProvenMs = 0);

    internal static ArmTrace Play(Trace trace, ArmSpec spec)
    {
        var model = new RingModel { GlideOutMs = spec.GlideOutMs };
        int n = trace.Samples.Count;
        var drawn = new PhysicalPoint[n];
        var visible = new bool[n];
        var pinned = new bool[n];
        var holdingAt = new bool[n];
        bool hybrid = spec.Pinning == Pinning.Hybrid;
        var ledger = hybrid ? new HoldLedger() : null;
        double radiusPx = trace.Config.RadiusPx;

        var previousState = InteractionState.FreeLook;
        ActivationOffer? offer = null;
        bool holding = false;
        bool pendingRunEndRelease = false;
        long? approachUntilUs = null;
        long? outsideSinceUs = null;
        long holdStartUs = 0;
        int holdInHitSamples = 0;

        void StartHold(long us, bool inPlace)
        {
            holding = true;
            approachUntilUs = null;
            outsideSinceUs = null;
            holdStartUs = us;
            holdInHitSamples = 0;
            ledger!.Holds++;
            if (inPlace) model.HoldInPlace();
        }

        void EndHold(long us, bool release)
        {
            holding = false;
            outsideSinceUs = null;
            ledger!.DurationsMs.Add((us - holdStartUs) / 1000.0);
            // A hold the gaze only passed through: under 150 ms of it in the hit area.
            if (holdInHitSamples * 30 < 150) ledger.Spurious++;
            if (release) model.Release();
        }

        for (int i = 0; i < n; i++)
        {
            var row = trace.Samples[i];
            for (int k = row.FirstEvent; k < row.FirstEvent + row.EventCount; k++)
            {
                var e = trace.Events[k];
                switch (e.Kind)
                {
                    case Kind.CorePin when spec.Pinning == Pinning.Today:
                        model.Pin(e.Point);
                        break;
                    case Kind.CoreRelease when spec.Pinning == Pinning.Today:
                        model.Release();
                        break;
                    case Kind.Settled when spec.Pinning is Pinning.RunOnly or Pinning.Hybrid:
                        // The hybrid ignores a settle inside a live offer's no-move zone (near miss).
                        bool nearOffer = hybrid && offer is { } so
                            && e.Point.DistanceTo(so.Button) <= so.HitRadiusPx + radiusPx;
                        if (!holding && approachUntilUs is null && !nearOffer) model.Pin(e.Point);
                        break;
                    case Kind.State:
                        if (spec.Pinning is Pinning.RunOnly or Pinning.Hybrid
                            && previousState == InteractionState.Fixation && e.State != InteractionState.Fixation
                            && !holding && approachUntilUs is null)
                        {
                            // The hybrid defers the run-end release to this sample's cursor frame, where the
                            // landing gaze is known. Releasing there, before the frame is applied, is the order
                            // the VM sees anyway (release first, then the cursor message).
                            if (hybrid && spec.Hold != HoldStyle.BackToP && e.State == InteractionState.FreeLook)
                                pendingRunEndRelease = true;
                            else
                                model.Release();
                        }
                        previousState = e.State;
                        model.State(e.State);
                        if (!model.IsVisible)
                        {
                            pendingRunEndRelease = false;
                            approachUntilUs = null;
                            // A hiding state clears the pin in the VM; the hold goes with it.
                            if (holding) EndHold(row.Us, release: false);
                        }
                        break;
                    case Kind.OfferShown:
                        offer = e.Offer;
                        break;
                    case Kind.OfferClosed:
                        offer = null;
                        if (holding) EndHold(row.Us, release: true);
                        if (approachUntilUs is not null) { approachUntilUs = null; model.Release(); }
                        break;
                    case Kind.Cursor:
                        PhysicalPoint g = e.Frame.Position;
                        if (pendingRunEndRelease)
                        {
                            pendingRunEndRelease = false;
                            double d0 = offer is { } po ? g.DistanceTo(po.Button) : double.MaxValue;
                            if (offer is not null && model.IsPinned && d0 <= offer.HitRadiusPx)
                                StartHold(row.Us, inPlace: false);           // lands on the button: keep the pin
                            else if (spec.Hold == HoldStyle.Freeze && offer is not null && model.IsPinned
                                     && spec.ApproachMs > 0 && d0 <= offer.HitRadiusPx + radiusPx)
                                approachUntilUs = row.Us + (spec.ApproachMs * 1000L);  // heading for it: wait
                            else
                                model.Release();
                        }
                        else if (approachUntilUs is { } until && offer is { } ao)
                        {
                            double da = g.DistanceTo(ao.Button);
                            if (da <= ao.HitRadiusPx)
                                StartHold(row.Us, inPlace: false);
                            else if (da > ao.HitRadiusPx + radiusPx || row.Us >= until)
                            {
                                approachUntilUs = null;
                                model.Release();
                            }
                        }

                        if (hybrid && offer is { } o && model.IsVisible)
                        {
                            double d = g.DistanceTo(o.Button);
                            if (holding)
                            {
                                if (d <= o.HitRadiusPx + spec.ExitMarginPx)
                                    outsideSinceUs = null;
                                else
                                {
                                    outsideSinceUs ??= row.Us;
                                    bool proven = holdInHitSamples * 30 >= spec.ProvenMs;
                                    // Edge-tolerant hold: an unproven hold ends at once only when the gaze
                                    // leaves the no-move zone; inside it, the grace applies as when proven.
                                    if ((!proven && d > o.HitRadiusPx + radiusPx)
                                        || row.Us - outsideSinceUs.Value >= spec.GraceMs * 1000L)
                                        EndHold(row.Us, release: true);
                                }
                            }
                            else if (approachUntilUs is null && d <= o.HitRadiusPx)
                            {
                                if (spec.Hold == HoldStyle.Freeze)
                                    StartHold(row.Us, inPlace: true);
                                else if (spec.Hold == HoldStyle.BackToP)
                                {
                                    StartHold(row.Us, inPlace: false);
                                    model.Pin(o.Point, force: true);
                                }
                            }
                        }
                        if (holding && offer is { } ho && g.DistanceTo(ho.Button) <= ho.HitRadiusPx)
                            holdInHitSamples++;
                        model.Cursor(e.Frame);
                        break;
                }
            }
            drawn[i] = model.Drawn;
            visible[i] = model.IsVisible;
            pinned[i] = model.IsPinned;
            holdingAt[i] = holding || approachUntilUs is not null;
        }

        return new ArmTrace(spec.Name, drawn, visible, pinned, ledger, holdingAt);
    }

    // ── Metrics ──

    internal sealed record ArmMetrics(
        double VisibleSeconds, double PinnedShare,
        double SepMean, double SepP50, double SepP95, double Over50, double Over100,
        double JumpsPerS, double CatchUpsPerS, int CatchUps, double CatchUpMedianPx,
        double CatchUpPeakStepP50, double CatchUpPeakStepP95, double AbruptPerS,
        double FixStill, double FixStepP50, double FixStepP90,
        double DanceS, double DanceSettledS, double DanceSettledSpeed, int DanceSettledFlips, double DanceOverShare);

    /// <summary>A catch-up whose largest single-sample step is this large reads as a teleport, glided or not.</summary>
    internal const double AbruptStepPx = 150;

    internal static ArmMetrics Measure(Trace trace, ArmTrace arm, double ringRadiusPx)
    {
        var rows = trace.Samples;
        long visibleUs = 0, pinnedUs = 0, fixUs = 0, fixStillUs = 0, danceUs = 0, danceOverUs = 0, danceSettledUs = 0;
        double danceSettledPath = 0;
        int danceSettledFlips = 0, jumps = 0, catchUps = 0;
        var seps = new List<double>();
        var fixSteps = new List<double>();
        var catchUpSizes = new List<double>();
        var catchUpPeaks = new List<double>();
        int start = -1;
        double peak = 0;
        long? visitStartUs = null;
        int teleportEvents = 0;

        // A button visit, as the dwell sees one: the in-hit samples of one offer, plus the excursions
        // between two of them that last no longer than the engine's 500 ms outside-pause budget (the dwell
        // survives those). A ring that dances at the hit area's edge is counted; the eye leaving is not.
        var visit = new bool[rows.Count];
        {
            long budgetUs = ActivationOfferEngine.DefaultOutsidePauseBudgetMs * 1000L;
            int lastHit = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!(arm.Visible[i] && rows[i].Offer is { } o && rows[i].Filtered.DistanceTo(o.Button) <= o.HitRadiusPx))
                    continue;
                visit[i] = true;
                if (lastHit >= 0 && rows[lastHit].Offer!.Id == rows[i].Offer!.Id && rows[i].Us - rows[lastHit].Us <= budgetUs)
                    for (int j = lastHit + 1; j < i; j++) visit[j] = true;
                lastHit = i;
            }
        }

        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var prev = rows[i - 1];
            long dt = Math.Min(row.Us - prev.Us, 66_000);
            if (dt <= 0) continue;
            bool visible = arm.Visible[i] && arm.Visible[i - 1];
            double step = visible ? arm.Drawn[i - 1].DistanceTo(arm.Drawn[i]) : 0;
            bool inDance = visible && visit[i];

            if (visible)
            {
                visibleUs += dt;
                if (arm.Pinned[i]) pinnedUs += dt;
                if (row.Raw.IsValid) seps.Add(arm.Drawn[i].DistanceTo(new PhysicalPoint(row.Raw.X, row.Raw.Y)));
                if (row.State == InteractionState.Fixation)
                {
                    fixUs += dt;
                    if (step < 2) fixStillUs += dt;
                    fixSteps.Add(step);
                }
                if (inDance)
                {
                    danceUs += dt;
                    if (arm.Drawn[i].DistanceTo(row.Offer!.Button) < ringRadiusPx + ButtonDrawnRadiusPx) danceOverUs += dt;
                    visitStartUs ??= row.Us;
                    if (row.Us - visitStartUs.Value >= 150_000)
                    {
                        danceSettledUs += dt;
                        danceSettledPath += step;
                        if (arm.Pinned[i] != arm.Pinned[i - 1]) danceSettledFlips++;
                    }
                }
            }
            if (!inDance) visitStartUs = null;

            bool moving = visible && step >= StepPx;
            if (moving)
            {
                if (start < 0) { start = i; peak = 0; }
                peak = Math.Max(peak, step);
            }
            if (start >= 0 && (!moving || i == rows.Count - 1))
            {
                int end = moving ? i : i - 1;
                double ringNet = arm.Drawn[start - 1].DistanceTo(arm.Drawn[end]);
                if (ringNet >= JumpPx)
                {
                    jumps++;
                    double gazeNet = rows[start - 1].Filtered.DistanceTo(rows[end].Filtered);
                    if (gazeNet < 0.5 * ringNet)
                    {
                        catchUps++;
                        catchUpSizes.Add(ringNet);
                        catchUpPeaks.Add(peak);
                        if (peak >= AbruptStepPx) teleportEvents++;
                    }
                }
                start = -1;
            }
        }

        double s = visibleUs / 1e6;
        return new ArmMetrics(
            s, Ratio(pinnedUs, visibleUs),
            seps.Count == 0 ? 0 : seps.Average(), Pct(seps, 0.5), Pct(seps, 0.95),
            seps.Count == 0 ? 0 : seps.Count(x => x > 50) / (double)seps.Count,
            seps.Count == 0 ? 0 : seps.Count(x => x > 100) / (double)seps.Count,
            s <= 0 ? 0 : jumps / s, s <= 0 ? 0 : catchUps / s, catchUps, Pct(catchUpSizes, 0.5),
            Pct(catchUpPeaks, 0.5), Pct(catchUpPeaks, 0.95), s <= 0 ? 0 : teleportEvents / s,
            Ratio(fixStillUs, fixUs), Pct(fixSteps, 0.5), Pct(fixSteps, 0.9),
            danceUs / 1e6, danceSettledUs / 1e6, danceSettledUs == 0 ? 0 : danceSettledPath / (danceSettledUs / 1e6),
            danceSettledFlips, Ratio(danceOverUs, danceUs));
    }

    private static double Ratio(long a, long b) => b == 0 ? 0 : (double)a / b;

    private static double Pct(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s[(int)Math.Round(p * (s.Count - 1))];
    }

    // ── Tests ──

    internal static readonly ArmSpec Today = new("today", Pinning.Today);
    internal static readonly ArmSpec RunOnly = new("run-only", Pinning.RunOnly);
    internal static readonly ArmSpec Never = new("never", Pinning.Never);
    /// <summary>The grace is the engine's own outside-pause budget: the hold lasts exactly as long as the
    /// button dwell would survive an excursion (ActivationOfferEngine.DefaultOutsidePauseBudgetMs).</summary>
    internal const int HybridGraceMs = ActivationOfferEngine.DefaultOutsidePauseBudgetMs;
    internal const int GlideOutMs = RingGlide.DurationMs;
    internal static readonly ArmSpec GlideOut = new("glide-out", Pinning.Today, GlideOutMs: GlideOutMs);
    /// <summary>The grace protects only a hold that has proven itself: a saccade crossing the 300 px hit
    /// area spends at most one or two samples in it, a deliberate look at least three (90 ms).</summary>
    internal const int HybridProvenMs = 90;
    internal const int HybridApproachMs = 150;
    internal static readonly ArmSpec Hybrid = new("hybrid", Pinning.Hybrid, GraceMs: HybridGraceMs, ApproachMs: HybridApproachMs, ProvenMs: HybridProvenMs);
    internal static readonly ArmSpec HybridGlideOut = new("hybrid + glide-out", Pinning.Hybrid, GlideOutMs: GlideOutMs, GraceMs: HybridGraceMs, ApproachMs: HybridApproachMs, ProvenMs: HybridProvenMs);

    /// <summary>The scripted chain: four holds 400 px apart (a live offer chains), a swallowed settle beside
    /// the last P, a far hold; with <paramref name="withButtonVisit"/>, a 700 ms dwell on the last offer's
    /// button (it fires; manual selection hides the ring for 2.33 s) and a hold long enough for the ring to
    /// reappear. Deterministic (seeded).</summary>
    internal static List<GazePoint> ScriptedSamples(bool withButtonVisit)
    {
        var samples = new List<GazePoint>();
        long us = 1_000_000;
        var rng = new Random(7);
        void Hold(double x, double y, int ms)
        {
            for (int t = 0; t < ms; t += 30, us += 30_000)
                samples.Add(new GazePoint(x + rng.NextDouble() * 6 - 3, y + rng.NextDouble() * 6 - 3, us, true));
        }
        Hold(1200, 800, 900);
        Hold(1600, 800, 900);
        Hold(2000, 800, 900);
        Hold(2400, 800, 1500);
        if (withButtonVisit)
        {
            // P = (2400, 800) → B ≈ (2181, 1019) on the diagonal toward the screen centre. Look at the button
            // long enough to dwell (500 ms at the session-end config), wobbling inside the hit area.
            for (int t = 0; t < 700; t += 30, us += 30_000)
                samples.Add(new GazePoint(2181 + (t / 30 % 2 == 0 ? 60 : -60), 1019 + rng.NextDouble() * 40 - 20, us, true));
        }
        Hold(2330, 870, 600);
        Hold(1000, 1700, 1200);
        // Long enough for the fired offer's manual-selection visit (2.33 s) to close, so the ring's
        // reappear snap is part of the equivalence check.
        if (withButtonVisit) Hold(1500, 1300, 1500);
        return samples;
    }

    internal static void CheckEquivalent(Trace trace)
    {
        // The model's today / run-only / never must draw exactly what the real VMs drew.
        foreach (var (spec, mode) in new[] { (Today, Mode.Today), (RunOnly, Mode.RunOnly), (Never, Mode.Never) })
        {
            var arm = Play(trace, spec);
            int mismatches = 0;
            for (int i = 0; i < trace.Samples.Count; i++)
            {
                var live = trace.LiveVms.Rows[i];
                if (live.Drawn[(int)mode] != arm.Drawn[i] || live.Visible[(int)mode] != arm.Visible[i]
                    || live.Pinned[(int)mode] != arm.Pinned[i])
                    mismatches++;
            }
            Check.WithCustomMessage($"{spec.Name}: samples where the model and the real VM disagree")
                .That(mismatches).IsEqualTo(0);
        }
    }

    [Fact]
    public void Model_DrawsExactlyWhatTheRealVmDraws_OnTheScriptedChain()
    {
        CheckEquivalent(Record(ScriptedSamples(withButtonVisit: true), SessionEnd));
    }

    [Fact]
    public void Hybrid_HoldsAtP_WhileTheGazeIsOnTheButton_WhereRunOnlyDances()
    {
        var trace = Record(ScriptedSamples(withButtonVisit: true), SessionEnd);
        double ring = SessionEnd.RingSizeDip;
        var runOnly = Measure(trace, Play(trace, RunOnly), ring);
        var never = Measure(trace, Play(trace, Never), ring);
        var hybridArm = Play(trace, Hybrid);
        var hybrid = Measure(trace, hybridArm, ring);

        Check.That(hybrid.DanceSettledS).IsStrictlyGreaterThan(0.2);
        // Mutation: drop the hold (never start one) → the hybrid is run-only and this reds.
        Check.That(hybridArm.Holds!.Holds).IsStrictlyGreaterThan(0);
        Check.That(hybrid.DanceSettledSpeed).IsEqualTo(0.0);
        Check.That(hybrid.DanceOverShare).IsEqualTo(0.0);
        Check.That(never.DanceSettledSpeed).IsStrictlyGreaterThan(100.0);
        Check.That(runOnly.DanceOverShare).IsStrictlyGreaterThan(0.5);
    }

    /// <summary>The freeze chain: a hold that makes an offer at P = (2400, 800) (B ≈ (2181, 1019)), a brief look
    /// elsewhere (no settle; the linger keeps the offer), then a 600 ms wobble on the button, so the hybrid's
    /// hold starts by freezing a FOLLOWING ring. Deterministic (seeded).</summary>
    internal static List<GazePoint> ScriptedFromElsewhere()
    {
        var samples = new List<GazePoint>();
        long us = 1_000_000;
        var rng = new Random(11);
        void Hold(double x, double y, int ms)
        {
            for (int t = 0; t < ms; t += 30, us += 30_000)
                samples.Add(new GazePoint(x + rng.NextDouble() * 6 - 3, y + rng.NextDouble() * 6 - 3, us, true));
        }
        Hold(2400, 800, 1200);
        Hold(1700, 1500, 240);
        for (int t = 0; t < 600; t += 30, us += 30_000)
            samples.Add(new GazePoint(2181 + (t / 30 % 2 == 0 ? 50 : -50), 1019 + rng.NextDouble() * 30 - 15, us, true));
        Hold(1000, 1700, 900);
        return samples;
    }

    [Fact]
    public void Hybrid_FreezesAFollowingRing_WhenTheGazeReachesTheButtonFromElsewhere()
    {
        // The eye leaves P for a far point, then comes to the button while the offer still lingers: the
        // ring is following (no pin to keep), so the hybrid must freeze it on entry. Mutation: ignore the
        // in-place hold → the ring rides the gaze over the button and this reds.
        var trace = Record(ScriptedFromElsewhere(), SessionEnd);
        var arm = Play(trace, Hybrid);
        var hybrid = Measure(trace, arm, SessionEnd.RingSizeDip);
        var never = Measure(trace, Play(trace, Never), SessionEnd.RingSizeDip);

        Check.That(hybrid.DanceSettledS).IsStrictlyGreaterThan(0.2);
        Check.That(never.DanceSettledSpeed).IsStrictlyGreaterThan(100.0);
        Check.That(hybrid.DanceSettledSpeed).IsEqualTo(0.0);
        Check.That(hybrid.DanceSettledFlips).IsEqualTo(0);
    }

    [Fact]
    public void GlideOut_SpreadsTheRelease_TodaySnaps()
    {
        var trace = Record(ScriptedSamples(withButtonVisit: false), SessionEnd);
        double ring = SessionEnd.RingSizeDip;
        var today = Measure(trace, Play(trace, Today), ring);
        var glide = Measure(trace, Play(trace, GlideOut), ring);

        // The final hold is ~1700 px from the last P: today teleports it in one sample; the glide-out
        // spreads it so no single step carries more than ~35 % of it (smoothstep's middle frames).
        Check.That(today.CatchUpPeakStepP95).IsStrictlyGreaterThan(1000.0);
        Check.That(glide.CatchUpPeakStepP95).IsStrictlyLessThan(0.5 * today.CatchUpPeakStepP95);
    }

    [Fact]
    public void RealRecording_RingArms_ReportIsProduced()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set " + RecordingEnvironmentVariable + " to the frozen snapshot copy.");

        var samples = ReadRecording(path!);
        var report = new StringBuilder();
        foreach (var config in new[] { SessionEnd, SessionStart })
        {
            var trace = Record(samples, config);
            CheckEquivalent(trace);
            foreach (var spec in new[] { Today, RunOnly })
                Check.That(CountDifferences(
                    PlayThroughVm(trace, spec, m => new CursorCircleViewModel(new EyeCtrlSettings(), m)),
                    Play(trace, spec))).IsEqualTo(0);
            report.Append(Render(trace));
        }

        output.WriteLine(report.ToString());
        var outPath = Environment.GetEnvironmentVariable(ReportEnvironmentVariable);
        if (!string.IsNullOrEmpty(outPath))
            File.WriteAllText(outPath, report.ToString());
    }

    // ── The oracle for the look build ──

    /// <summary>
    /// Sends a recorded trace to <paramref name="messenger"/> as the messages a ring VM receives in the
    /// product, in the adapter's order, for the given arm's Core pin policy:
    /// <list type="bullet">
    /// <item>today (<c>OfferFollowing</c>): Core's own <c>PinRing</c>/<c>ReleaseRing</c> as recorded;</item>
    /// <item>run-only and hybrid (<c>RunOnly</c>): a <see cref="PinRingMessage"/> at every accepted settle,
    /// at the settle's place in the stream (right after <c>FixationSettled</c>, BEFORE that settle's offer
    /// publish), and a <see cref="ReleaseRingMessage"/> on every exit from Fixation, before its
    /// <see cref="StateChangedMessage"/>. Nothing from the offer lifecycle pins or releases.</item>
    /// </list>
    /// Every arm also gets the offer's <see cref="ShowActivationButtonMessage"/> (B, hit radius) and
    /// <see cref="HideActivationButtonMessage"/>, the <see cref="StateChangedMessage"/>s and the
    /// <see cref="GazeCursorMovedMessage"/> per sample. <paramref name="afterSample"/> runs once each
    /// sample's messages are delivered. The hybrid's hold is NOT synthesized here: it is the VM's job.
    /// </summary>
    internal static void Feed(Trace trace, ArmSpec spec, IMessenger messenger,
        Action<int>? afterSample = null)
    {
        bool runOnlyPolicy = spec.Pinning is Pinning.RunOnly or Pinning.Hybrid;
        var previous = InteractionState.FreeLook;
        for (int i = 0; i < trace.Samples.Count; i++)
        {
            var row = trace.Samples[i];
            for (int k = row.FirstEvent; k < row.FirstEvent + row.EventCount; k++)
            {
                var e = trace.Events[k];
                switch (e.Kind)
                {
                    case Kind.CorePin when spec.Pinning == Pinning.Today:
                        messenger.Send(new PinRingMessage(e.Point));
                        break;
                    case Kind.CoreRelease when spec.Pinning == Pinning.Today:
                        messenger.Send(new ReleaseRingMessage());
                        break;
                    case Kind.Settled when runOnlyPolicy:
                        messenger.Send(new PinRingMessage(e.Point));
                        break;
                    case Kind.State:
                        if (runOnlyPolicy && previous == InteractionState.Fixation && e.State != InteractionState.Fixation)
                            messenger.Send(new ReleaseRingMessage());
                        previous = e.State;
                        messenger.Send(new StateChangedMessage(e.State));
                        break;
                    case Kind.OfferShown:
                        messenger.Send(new ShowActivationButtonMessage(e.Offer!.Button, e.Offer.HitRadiusPx));
                        break;
                    case Kind.OfferClosed:
                        messenger.Send(new HideActivationButtonMessage());
                        break;
                    case Kind.Cursor:
                        messenger.Send(new GazeCursorMovedMessage(e.Frame));
                        break;
                }
            }
            afterSample?.Invoke(i);
        }
    }

    /// <summary>Plays a trace through a real ring VM built by <paramref name="createVm"/> on a fresh messenger
    /// (via <see cref="Feed"/>) and returns what it drew per sample — the same shape <see cref="Play"/>
    /// returns, so the two compare with <see cref="CountDifferences"/>.</summary>
    internal static ArmTrace PlayThroughVm(Trace trace, ArmSpec spec,
        Func<IMessenger, CursorCircleViewModel> createVm)
    {
        var messenger = new StrongReferenceMessenger();
        var vm = createVm(messenger);
        PhysicalPoint drawn = default;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CursorCircleViewModel.Position)) drawn = vm.Position;
            else if (e.PropertyName == nameof(CursorCircleViewModel.MoveTo)) drawn = vm.MoveTo.Target;
        };
        int n = trace.Samples.Count;
        var d = new PhysicalPoint[n];
        var v = new bool[n];
        var p = new bool[n];
        Feed(trace, spec, messenger, i => { d[i] = drawn; v[i] = vm.IsVisible; p[i] = vm.IsPinned; });
        return new ArmTrace(spec.Name + " (VM)", d, v, p);
    }

    /// <summary>Samples on which two arm traces disagree on the drawn position (exactly), visibility or pin.</summary>
    internal static int CountDifferences(ArmTrace a, ArmTrace b)
    {
        int n = Math.Min(a.Drawn.Length, b.Drawn.Length), diffs = Math.Abs(a.Drawn.Length - b.Drawn.Length);
        for (int i = 0; i < n; i++)
            if (a.Drawn[i] != b.Drawn[i] || a.Visible[i] != b.Visible[i] || a.Pinned[i] != b.Pinned[i])
                diffs++;
        return diffs;
    }

    [Fact]
    public void Feed_IntoTheRealVm_MatchesTheModel_ForTodayAndRunOnly()
    {
        // The oracle's own check: today's VM fed by Feed draws exactly what the model draws for the two arms
        // the VM already supports (the hybrid hold is the look build's to add). Mutation: send the run-only
        // release after the state message, or pin at the offer instead of the settle → this reds.
        // The plain chain carries a VISIBLE swallowed settle (the visit chain's is hidden by manual selection).
        foreach (var samples in new[] { ScriptedSamples(withButtonVisit: false), ScriptedSamples(withButtonVisit: true), ScriptedFromElsewhere() })
        {
            var trace = Record(samples, SessionEnd);
            foreach (var spec in new[] { Today, RunOnly })
            {
                var vm = PlayThroughVm(trace, spec, m => new CursorCircleViewModel(new EyeCtrlSettings(), m));
                Check.WithCustomMessage(spec.Name + ": samples where Feed→VM and the model disagree")
                    .That(CountDifferences(vm, Play(trace, spec))).IsEqualTo(0);
            }
        }
    }

    // ── Rendering ──

    private static string Render(Trace trace)
    {
        var inv = CultureInfo.InvariantCulture;
        double ring = trace.Config.RingSizeDip;
        var sb = new StringBuilder();
        double minutes = (trace.Samples[^1].Us - trace.Samples[0].Us) / 60e6;
        sb.AppendLine(inv, $"## {trace.Config.Label}");
        sb.AppendLine();
        sb.AppendLine(inv, $"{trace.Samples.Count} samples, {minutes:0.00} min; offers {trace.Offers}, fired {trace.Fires}. Model ≡ real VM for today / run-only / never (0 differing samples).");
        sb.AppendLine();

        var arms = new[] { Today, RunOnly, Never, GlideOut, Hybrid, HybridGlideOut }
            .Select(spec => (spec, arm: Play(trace, spec))).ToList();
        var ms = arms.Select(a => (a.spec, a.arm, m: Measure(trace, a.arm, ring))).ToList();

        sb.AppendLine("| Metric | " + string.Join(" | ", ms.Select(x => x.spec.Name)) + " |");
        sb.AppendLine("| --- |" + string.Concat(ms.Select(_ => " --- |")));
        void Line(string name, Func<ArmMetrics, string> f) =>
            sb.AppendLine("| " + name + " | " + string.Join(" | ", ms.Select(x => f(x.m))) + " |");
        Line("Pinned share of visible time", m => P(m.PinnedShare));
        Line("Separation mean / p50 / p95 (px)", m => string.Format(inv, "{0:0} / {1:0} / {2:0}", m.SepMean, m.SepP50, m.SepP95));
        Line("> 50 px from gaze", m => P(m.Over50));
        Line("> 100 px from gaze", m => P(m.Over100));
        Line("Jumps ≥ 50 px per visible s", m => m.JumpsPerS.ToString("0.00", inv));
        Line("Catch-up jumps per visible s (n)", m => string.Format(inv, "{0:0.00} ({1})", m.CatchUpsPerS, m.CatchUps));
        Line("Catch-up size median (px)", m => m.CatchUpMedianPx.ToString("0", inv));
        Line("Catch-up peak single step p50 / p95 (px)", m => string.Format(inv, "{0:0} / {1:0}", m.CatchUpPeakStepP50, m.CatchUpPeakStepP95));
        Line("Teleport catch-ups (peak step ≥ 150 px) per visible s", m => m.AbruptPerS.ToString("0.00", inv));
        Line("Ring still in Fixation", m => P(m.FixStill));
        Line("Fixation step p50 / p90 (px)", m => string.Format(inv, "{0:0.0} / {1:0.0}", m.FixStepP50, m.FixStepP90));
        Line("Button visits (≤ 500 ms excursions): total / settled (s)", m => string.Format(inv, "{0:0.0} / {1:0.0}", m.DanceS, m.DanceSettledS));
        Line("**Dance:** ring speed once settled (px/s)", m => m.DanceSettledSpeed.ToString("0", inv));
        Line("**Dance:** pin flips once settled", m => m.DanceSettledFlips.ToString(inv));
        Line("**Dance:** ring over the drawn button", m => P(m.DanceOverShare));
        sb.AppendLine();
        foreach (var (spec, arm, _) in ms.Where(x => x.arm.Holds is not null))
        {
            var h = arm.Holds!;
            sb.AppendLine(inv, $"- **{spec.Name}** holds: {h.Holds} ({h.Spurious} spurious, < 150 ms in the hit area); duration median {Pct(h.DurationsMs, 0.5):0} ms, p95 {Pct(h.DurationsMs, 0.95):0} ms.");
        }
        sb.AppendLine();

        // Sensitivity: the hybrid's grace and exit hysteresis, and the glide-out's duration.
        sb.AppendLine("### Sensitivity");
        sb.AppendLine();
        sb.AppendLine("| Arm | > 100 px | Catch-ups / s | Peak step p95 | Teleport catch-ups / s | Still in Fixation | Dance px/s | Dance flips | Over button | Holds (spurious) |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        var variants = new List<ArmSpec>();
        // One knob at a time around the chosen hybrid (grace 500 ms once proven 90 ms, approach 150 ms).
        foreach (int grace in new[] { 0, 250, 500, 750 })
            foreach (double margin in new[] { 0.0, 50.0 })
                variants.Add(new ArmSpec($"hybrid grace {grace} ms, exit +{margin:0} px", Pinning.Hybrid, GraceMs: grace, ExitMarginPx: margin, ApproachMs: HybridApproachMs, ProvenMs: HybridProvenMs));
        foreach (int approach in new[] { 0, 150, 300 })
            variants.Add(new ArmSpec($"hybrid approach window {approach} ms", Pinning.Hybrid, GraceMs: HybridGraceMs, ApproachMs: approach, ProvenMs: HybridProvenMs));
        foreach (int proven in new[] { 0, 90, 150, 300 })
            variants.Add(new ArmSpec($"hybrid grace only once proven {proven} ms", Pinning.Hybrid, GraceMs: HybridGraceMs, ApproachMs: HybridApproachMs, ProvenMs: proven));
        variants.Add(new ArmSpec("REJECTED: hybrid keep-pin only, grace 500 ms", Pinning.Hybrid, GraceMs: 500, Hold: HoldStyle.KeepPin));
        variants.Add(new ArmSpec("REJECTED: hybrid back-to-P, grace 500 ms", Pinning.Hybrid, GraceMs: 500, Hold: HoldStyle.BackToP));
        variants.Add(new ArmSpec("REJECTED: hybrid back-to-P, grace 0 ms", Pinning.Hybrid, GraceMs: 0, Hold: HoldStyle.BackToP));
        foreach (int glide in new[] { 80, 120, 200 })
            variants.Add(new ArmSpec($"glide-out {glide} ms", Pinning.Today, GlideOutMs: glide));
        foreach (int glide in new[] { 80, 120, 200 })
            variants.Add(new ArmSpec($"hybrid + glide-out {glide} ms", Pinning.Hybrid, GlideOutMs: glide, GraceMs: HybridGraceMs, ApproachMs: HybridApproachMs, ProvenMs: HybridProvenMs));
        foreach (var spec in variants)
        {
            var arm = Play(trace, spec);
            var m = Measure(trace, arm, ring);
            string holds = arm.Holds is { } h ? string.Format(inv, "{0} ({1})", h.Holds, h.Spurious) : "—";
            sb.AppendLine(inv, $"| {spec.Name} | {P(m.Over100)} | {m.CatchUpsPerS:0.00} | {m.CatchUpPeakStepP95:0} | {m.AbruptPerS:0.00} | {P(m.FixStill)} | {m.DanceSettledSpeed:0} | {m.DanceSettledFlips} | {P(m.DanceOverShare)} | {holds} |");
        }
        sb.AppendLine();
        return sb.ToString();

        string P(double share) => (share * 100).ToString("0.0", inv) + " %";
    }

    internal static List<GazePoint> ReadRecording(string path)
    {
        var samples = new List<GazePoint>(20_000);
        foreach (var line in File.ReadLines(path))
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#') continue;
            var parts = span.ToString().Split(',', 4);
            if (parts.Length < 4) continue;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
                samples.Add(new GazePoint(x, y, t, parts[3].Trim() == "1"));
        }
        return samples;
    }

    private sealed class ToolbarVisibilityStub : IToolbarVisibility
    {
        public bool Visible { get; private set; } = true;
        public void ShowToolbar() => Visible = true;
        public void HideToolbar(bool paused) => Visible = false;
        public void ForceShow() => Visible = true;
        public void SetPauseIndicator(bool visible) { }
        public void PositionIndicator(string edge) { }
        public void SetActive(bool active) { }
    }
}
