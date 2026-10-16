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

/// <summary>
/// Ring-stutter investigation, phase 2 (scratch, not for main): the frozen recording replayed through the
/// REAL <see cref="GazeEngine"/> and <see cref="InteractionManager"/>, with three REAL
/// <see cref="CursorCircleViewModel"/>s fed from the same event stream in the adapter's order — Core's
/// pin/release/state events of a sample first, then that sample's cursor message — so each mode's drawn
/// ring is exactly what the VM would write. The modes differ only in which pin/release messages reach
/// their VM:
/// <list type="bullet">
/// <item><b>today</b>: Core's <c>PinRing</c>/<c>ReleaseRing</c> (offer-following pin, chain hops, linger,
/// snap out, chrome pins).</item>
/// <item><b>run-only</b>: a pin at every accepted settle (<c>FixationSettled</c>), a release on every exit
/// from Fixation — pinned exactly while the run holds.</item>
/// <item><b>never</b>: no pin or release messages; the ring follows the filtered gaze whenever shown.</item>
/// </list>
/// Env-gated on <c>EYECTRL_GAZE_RECORDING</c>; <c>EYECTRL_RING_REPLAY_OUT</c> writes the markdown.
/// </summary>
public class RingPinModeReplayTests(ITestOutputHelper output)
{
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";
    private const string ReportEnvironmentVariable = "EYECTRL_RING_REPLAY_OUT";
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    // The session's toolbar rect (log, 18:31:50: X 914, W 2011.68, H 280).
    private static readonly PhysicalRect ToolbarRect = new(914, 0, 2012, 280);

    /// <summary>A move of at least this many px in one sample counts toward a jump.</summary>
    internal const double StepPx = 15;
    /// <summary>A jump's net displacement must reach this.</summary>
    internal const double JumpPx = 50;
    /// <summary>The session's mean manual-selection visit: 79.2 s over 34 entries.</summary>
    internal const long ManualSelectionVisitUs = 2_330_000;
    /// <summary>The drawn activation button's radius: the shared 50 DIP button at ScaleX 2.0.</summary>
    internal const double ButtonDrawnRadiusPx = 100;

    internal sealed record Config(string Label, double RadiusPx, int OnsetMs, int ButtonDwellMs, double RingSizeDip);

    internal static readonly Config SessionEnd = new("session end: R80, onset 200 ms, button 500 ms", 80, 200, 500, 200);
    internal static readonly Config SessionStart = new("session start: R105, onset 300 ms, button 700 ms", 105, 300, 700, 200);

    internal enum Mode { Today, RunOnly, Never }

    /// <summary>One mode's VM, its drawn position and its event ledger.</summary>
    internal sealed class ModeProbe
    {
        public Mode Mode { get; }
        public IMessenger Messenger { get; } = new StrongReferenceMessenger();
        public CursorCircleViewModel Vm { get; }
        public PhysicalPoint Drawn { get; private set; }
        public InteractionState LastStateSent { get; private set; } = InteractionState.FreeLook;
        public List<double> GlideInPx { get; } = new();
        public List<double> HopPx { get; } = new();
        public List<double> SnapPx { get; } = new();
        public List<long> HopUs { get; } = new();

        public ModeProbe(Mode mode, EyeCtrlSettings settings)
        {
            Mode = mode;
            Vm = new CursorCircleViewModel(settings, Messenger);
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CursorCircleViewModel.Position)) Drawn = Vm.Position;
                else if (e.PropertyName == nameof(CursorCircleViewModel.MoveTo)) Drawn = Vm.MoveTo.Target;
            };
        }

        public void State(InteractionState state)
        {
            LastStateSent = state;
            Messenger.Send(new StateChangedMessage(state));
        }

        public void Pin(PhysicalPoint point, long us)
        {
            // The VM accepts a pin only in Fixation (its _lastState); mirror that to classify.
            if (LastStateSent != InteractionState.Fixation) return;
            double distance = Drawn.DistanceTo(point);
            if (Vm.IsPinned) { HopPx.Add(distance); HopUs.Add(us); }
            else GlideInPx.Add(distance);
            Messenger.Send(new PinRingMessage(point));
        }

        public void Release()
        {
            bool wasPinned = Vm.IsPinned;
            var before = Drawn;
            Messenger.Send(new ReleaseRingMessage());
            if (wasPinned && Vm.IsVisible) SnapPx.Add(before.DistanceTo(Drawn));
        }

        public void Cursor(GazeFrame frame) => Messenger.Send(new GazeCursorMovedMessage(frame));
    }

    /// <summary>One sample's outcome, per mode.</summary>
    internal readonly record struct Row(
        long Us, GazePoint Raw, PhysicalPoint Filtered, InteractionState State,
        ActivationOffer? Offer, PhysicalPoint[] Drawn, bool[] Visible, bool[] Pinned);

    internal sealed record Replay(Config Config, List<Row> Rows, ModeProbe[] Probes, int Offers, int Fires, int Settles);

    [Fact]
    public void RealRecording_RingPinModes_ReportIsProduced()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set " + RecordingEnvironmentVariable + " to the frozen snapshot copy.");

        var samples = ReadSamples(path!);
        var report = new StringBuilder();
        foreach (var config in new[] { SessionEnd, SessionStart })
            report.Append(Render(Run(samples, config)));

        output.WriteLine(report.ToString());
        var outPath = Environment.GetEnvironmentVariable(ReportEnvironmentVariable);
        if (!string.IsNullOrEmpty(outPath))
            File.WriteAllText(outPath, report.ToString());
    }

    [Fact]
    public void ScriptedChain_TodayHopsAndLags_RunOnlyFollows_NeverNeverPins()
    {
        // Teeth: three 900 ms holds 400 px apart along a line, then a 1.5 s hold. Today must hop
        // between them (a live offer chains), run-only must pin and release per hold, never must not
        // pin at all; and today must sit further from the gaze than never.
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
        // A hold beside the live P, on P's side of its button (B = P + (−219, +219), on the diagonal
        // toward the screen centre): 99 px from P, 211 px from B, inside the 150 + R no-move zone, so
        // the engine swallows the settle and Core does not move the ring — run-only must still pin.
        // 600 ms: it settles (swallowed), then ends before the old offer's 750 ms linger runs out, so the
        // engine's pending re-offer is cleared by the run's end and no offer is ever made for it.
        Hold(2330, 870, 600);
        Hold(1000, 1700, 1200);

        var replay = Run(samples, SessionEnd);
        var today = Measure(replay, Mode.Today);
        var runOnly = Measure(replay, Mode.RunOnly);
        var never = Measure(replay, Mode.Never);

        // Run-only pins on every accepted settle and nowhere else (mutation: feed it Core's PinRing →
        // the swallowed settle is missed and the count falls short).
        var runOnlyProbe = replay.Probes[(int)Mode.RunOnly];
        Check.That(runOnlyProbe.GlideInPx.Count + runOnlyProbe.HopPx.Count).IsEqualTo(replay.Settles);
        Check.That(replay.Settles).IsStrictlyGreaterThan(replay.Offers);
        Check.That(replay.Probes[(int)Mode.Today].HopPx.Count).IsStrictlyGreaterThan(0);
        Check.That(replay.Probes[(int)Mode.RunOnly].HopPx.Count).IsEqualTo(0);
        Check.That(replay.Probes[(int)Mode.Never].GlideInPx.Count).IsEqualTo(0);
        Check.That(never.PinnedShare).IsEqualTo(0.0);
        Check.That(runOnly.PinnedShare).IsStrictlyGreaterThan(0.0);
        Check.That(today.PinnedShare).IsStrictlyGreaterThan(runOnly.PinnedShare);
        Check.That(today.SeparationMeanPx).IsStrictlyGreaterThan(never.SeparationMeanPx);
        Check.That(today.CatchUpJumps).IsStrictlyGreaterThan(never.CatchUpJumps);
    }

    internal static Replay Run(IReadOnlyList<GazePoint> samples, Config config)
    {
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

        var probes = new[]
        {
            new ModeProbe(Mode.Today, settings),
            new ModeProbe(Mode.RunOnly, settings),
            new ModeProbe(Mode.Never, settings),
        };
        var today = probes[(int)Mode.Today];
        var runOnly = probes[(int)Mode.RunOnly];

        long currentUs = 0;
        var state = InteractionState.FreeLook;
        ActivationOffer? liveOffer = null;
        ActivationOffer? pendingFire = null;
        long? closeManualSelectionAtUs = null;
        PhysicalPoint filtered = default;
        int offers = 0, fires = 0, settles = 0;

        // Adapter order: Core's events of a sample are posted before that sample's cursor message.
        im.PinRing += p => today.Pin(p, currentUs);
        im.ReleaseRing += () => today.Release();
        im.FixationSettled += p => { settles++; runOnly.Pin(p, currentUs); };
        im.StateChanged += s =>
        {
            if (state == InteractionState.Fixation && s != InteractionState.Fixation)
                runOnly.Release();
            state = s;
            foreach (var probe in probes) probe.State(s);
        };
        im.ActivationOfferShown += o => { liveOffer = o; offers++; };
        im.ActivationOfferClosed += () => liveOffer = null;
        im.ActivationOfferFired += (o, _) => { pendingFire = o; fires++; };
        engine.CursorMoved += frame =>
        {
            filtered = frame.Position;
            foreach (var probe in probes) probe.Cursor(frame);
        };

        var visibility = new RecordingToolbarVisibility();
        var autoHide = new ToolbarAutoHideController(
            enabled: true, edge: "Top", screenW: Screen.Width, screenH: Screen.Height,
            toolbarRect: ToolbarRect, hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        autoHide.Attach(visibility);
        bool? lastVisible = null;

        var rows = new List<Row>(samples.Count);
        engine.Start();
        foreach (var sample in samples)
        {
            currentUs = sample.TimestampMicroseconds;
            autoHide.Feed(sample, currentUs);
            if (lastVisible != visibility.Visible)
            {
                lastVisible = visibility.Visible;
                im.SetActivationChromeZones(visibility.Visible ? new[] { ToolbarRect } : Array.Empty<PhysicalRect>());
            }

            provider.Raise(sample);

            // The completion, as the app does it on the dispatcher: accept and start manual selection,
            // which hides the ring; the zoom is closed after the session's mean visit.
            if (pendingFire is { } fired)
            {
                pendingFire = null;
                if (im.OnActivationButtonDwelled(fired) == ActivationCompletionResult.Accepted)
                    closeManualSelectionAtUs = currentUs + ManualSelectionVisitUs;
            }
            if (closeManualSelectionAtUs is { } closeAt && currentUs >= closeAt)
            {
                closeManualSelectionAtUs = null;
                im.OnManualSelectionBack();
            }

            rows.Add(new Row(
                currentUs, sample, filtered, state, liveOffer,
                probes.Select(p => p.Drawn).ToArray(),
                probes.Select(p => p.Vm.IsVisible).ToArray(),
                probes.Select(p => p.Vm.IsPinned).ToArray()));
        }

        return new Replay(config, rows, probes, offers, fires, settles);
    }

    internal sealed record Metrics(
        double VisibleSeconds,
        double PinnedShare,
        double SeparationMeanPx,
        double SeparationP50Px,
        double SeparationP95Px,
        double ShareOver50Px,
        double ShareOver100Px,
        int Jumps,
        int CatchUpJumps,
        double CatchUpJumpMedianPx,
        double FixationStillShare,
        double DanceSeconds,
        double DanceRingSpeedPxPerS,
        double DanceRingOverButtonShare,
        int DanceJumps,
        int DanceCatchUpJumps,
        double DanceSettledSeconds,
        double DanceSettledRingSpeedPxPerS,
        int DanceSettledPinFlips,
        double FixationStepP50Px,
        double FixationStepP90Px)
    {
        public double JumpsPerVisibleSecond => VisibleSeconds <= 0 ? 0 : Jumps / VisibleSeconds;
        public double CatchUpsPerVisibleSecond => VisibleSeconds <= 0 ? 0 : CatchUpJumps / VisibleSeconds;
    }

    internal static Metrics Measure(Replay replay, Mode mode)
    {
        int m = (int)mode;
        var rows = replay.Rows;
        double ringRadiusPx = replay.Config.RingSizeDip; // diameter in DIP × 2 / 2
        long visibleUs = 0, pinnedUs = 0, fixationUs = 0, fixationStillUs = 0;
        long danceUs = 0, danceOverUs = 0;
        double dancePath = 0;
        var separations = new List<double>();
        int jumps = 0, catchUps = 0, danceJumps = 0, danceCatchUps = 0;
        var catchUpSizes = new List<double>();

        // Jump detection: a maximal stretch of visible samples whose per-sample ring step is ≥ StepPx,
        // kept when its net displacement is ≥ JumpPx. Catch-up: the gaze moved less than half as far
        // as the ring across [stretch start − 1, stretch end] — the ring moved while the eye did not.
        int start = -1;
        bool startedInDance = false;
        // The settled part of a button visit: from 150 ms after the gaze entered the hit area, so the
        // arrival saccade itself (which every following ring makes) is not counted as dancing.
        long? visitStartUs = null;
        long danceSettledUs = 0;
        double danceSettledPath = 0;
        int danceSettledFlips = 0;
        var fixationSteps = new List<double>();

        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var prev = rows[i - 1];
            long dt = Math.Min(row.Us - prev.Us, 66_000);
            if (dt <= 0) continue;
            bool visible = row.Visible[m] && prev.Visible[m];
            double step = visible ? prev.Drawn[m].DistanceTo(row.Drawn[m]) : 0;

            bool inDance = visible && row.Offer is { } offer
                && row.Filtered.DistanceTo(offer.Button) <= offer.HitRadiusPx;

            if (visible)
            {
                visibleUs += dt;
                if (row.Pinned[m]) pinnedUs += dt;
                if (row.Raw.IsValid)
                    separations.Add(row.Drawn[m].DistanceTo(new PhysicalPoint(row.Raw.X, row.Raw.Y)));
                if (row.State == InteractionState.Fixation)
                {
                    fixationUs += dt;
                    if (step < 2) fixationStillUs += dt;
                    fixationSteps.Add(step);
                }
                if (inDance)
                {
                    danceUs += dt;
                    dancePath += step;
                    if (row.Drawn[m].DistanceTo(row.Offer!.Button) < ringRadiusPx + ButtonDrawnRadiusPx)
                        danceOverUs += dt;
                    visitStartUs ??= row.Us;
                    if (row.Us - visitStartUs.Value >= 150_000)
                    {
                        danceSettledUs += dt;
                        danceSettledPath += step;
                        if (row.Pinned[m] != prev.Pinned[m]) danceSettledFlips++;
                    }
                }
            }
            if (!inDance) visitStartUs = null;

            bool moving = visible && step >= StepPx;
            if (moving && start < 0) { start = i; startedInDance = inDance; }
            bool last = i == rows.Count - 1;
            if (start >= 0 && (!moving || last))
            {
                int end = moving ? i : i - 1;
                var ringFrom = rows[start - 1].Drawn[m];
                var ringTo = rows[end].Drawn[m];
                double ringNet = ringFrom.DistanceTo(ringTo);
                if (ringNet >= JumpPx)
                {
                    jumps++;
                    double gazeNet = rows[start - 1].Filtered.DistanceTo(rows[end].Filtered);
                    bool catchUp = gazeNet < 0.5 * ringNet;
                    if (catchUp) { catchUps++; catchUpSizes.Add(ringNet); }
                    if (startedInDance) { danceJumps++; if (catchUp) danceCatchUps++; }
                }
                start = -1;
            }
        }

        separations.Sort();
        double Pct(List<double> xs, double p) => xs.Count == 0 ? 0 : xs[(int)Math.Round(p * (xs.Count - 1))];
        catchUpSizes.Sort();
        double visibleS = visibleUs / 1e6;
        return new Metrics(
            visibleS,
            visibleUs == 0 ? 0 : (double)pinnedUs / visibleUs,
            separations.Count == 0 ? 0 : separations.Average(),
            Pct(separations, 0.5),
            Pct(separations, 0.95),
            separations.Count == 0 ? 0 : separations.Count(s => s > 50) / (double)separations.Count,
            separations.Count == 0 ? 0 : separations.Count(s => s > 100) / (double)separations.Count,
            jumps,
            catchUps,
            Pct(catchUpSizes, 0.5),
            fixationUs == 0 ? 0 : (double)fixationStillUs / fixationUs,
            danceUs / 1e6,
            danceUs == 0 ? 0 : dancePath / (danceUs / 1e6),
            danceUs == 0 ? 0 : (double)danceOverUs / danceUs,
            danceJumps,
            danceCatchUps,
            danceSettledUs / 1e6,
            danceSettledUs == 0 ? 0 : danceSettledPath / (danceSettledUs / 1e6),
            danceSettledFlips,
            PctOf(fixationSteps, 0.5),
            PctOf(fixationSteps, 0.9));
    }

    private static double PctOf(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s[(int)Math.Round(p * (s.Count - 1))];
    }

    /// <summary>For today's hops: how long after the eye left the old P (the last Fixation exit before
    /// the hop) the ring started to move.</summary>
    internal static List<double> HopLagsMs(Replay replay)
    {
        var lags = new List<double>();
        var hops = replay.Probes[(int)Mode.Today].HopUs;
        int h = 0;
        long? lastExitUs = null;
        var previous = InteractionState.FreeLook;
        foreach (var row in replay.Rows)
        {
            if (previous == InteractionState.Fixation && row.State != InteractionState.Fixation)
                lastExitUs = row.Us;
            previous = row.State;
            while (h < hops.Count && hops[h] <= row.Us)
            {
                if (lastExitUs is { } exit && hops[h] - exit < 2_000_000)
                    lags.Add((hops[h] - exit) / 1000.0);
                h++;
            }
        }
        lags.Sort();
        return lags;
    }

    private static string Render(Replay replay)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var metrics = Enum.GetValues<Mode>().Select(mode => (mode, Measure(replay, mode))).ToList();
        double minutes = (replay.Rows[^1].Us - replay.Rows[0].Us) / 60e6;

        string Q(List<double> xs)
        {
            if (xs.Count == 0) return "—";
            var s = xs.OrderBy(x => x).ToList();
            double P(double p) => s[(int)Math.Round(p * (s.Count - 1))];
            return string.Format(inv, "n {0}: median {1:0}, p75 {2:0}, p95 {3:0}, max {4:0}", s.Count, P(0.5), P(0.75), P(0.95), s[^1]);
        }

        sb.AppendLine(inv, $"## {replay.Config.Label}");
        sb.AppendLine();
        int t = (int)Mode.Today;
        long visUs = 0, offerUs = 0;
        for (int i = 1; i < replay.Rows.Count; i++)
        {
            var r = replay.Rows[i];
            long dt = Math.Min(r.Us - replay.Rows[i - 1].Us, 66_000);
            if (dt <= 0 || !(r.Visible[t] && replay.Rows[i - 1].Visible[t])) continue;
            visUs += dt;
            if (r.Offer is not null) offerUs += dt;
        }
        sb.AppendLine(inv, $"{replay.Rows.Count} samples, {minutes:0.00} min; offers shown {replay.Offers} ({replay.Offers / minutes:0.0}/min), fired {replay.Fires}; an offer is alive for {(visUs == 0 ? 0 : 100.0 * offerUs / visUs):0.0} % of visible time (the session's PINNED proxy).");
        sb.AppendLine();
        sb.AppendLine("| Metric | today | run-only | never |");
        sb.AppendLine("| --- | --- | --- | --- |");
        void Line(string name, Func<Metrics, string> f) =>
            sb.AppendLine("| " + name + " | " + string.Join(" | ", metrics.Select(x => f(x.Item2))) + " |");
        Line("Visible time (s)", x => x.VisibleSeconds.ToString("0", inv));
        Line("Pinned share of visible time", x => (x.PinnedShare * 100).ToString("0.0", inv) + " %");
        Line("Ring–gaze separation mean / p50 / p95 (px)", x => string.Format(inv, "{0:0} / {1:0} / {2:0}", x.SeparationMeanPx, x.SeparationP50Px, x.SeparationP95Px));
        Line("Share of time > 50 px from gaze", x => (x.ShareOver50Px * 100).ToString("0.0", inv) + " %");
        Line("Share of time > 100 px from gaze", x => (x.ShareOver100Px * 100).ToString("0.0", inv) + " %");
        Line("Jumps (≥ 50 px) per visible s", x => x.JumpsPerVisibleSecond.ToString("0.00", inv));
        Line("Catch-up jumps (ring moves, eye still) per visible s", x => x.CatchUpsPerVisibleSecond.ToString("0.00", inv) + " (" + x.CatchUpJumps.ToString(inv) + ")");
        Line("Catch-up jump median size (px)", x => x.CatchUpJumpMedianPx.ToString("0", inv));
        Line("Ring still (< 2 px/sample) while in Fixation", x => (x.FixationStillShare * 100).ToString("0.0", inv) + " %");
        Line("Ring step per sample in Fixation p50 / p90 (px)", x => string.Format(inv, "{0:0.0} / {1:0.0}", x.FixationStepP50Px, x.FixationStepP90Px));
        Line("Gaze on a live button (s)", x => x.DanceSeconds.ToString("0.0", inv));
        Line("… ring speed while gaze on button (px/s)", x => x.DanceRingSpeedPxPerS.ToString("0", inv));
        Line("… ring over the button (share)", x => (x.DanceRingOverButtonShare * 100).ToString("0.0", inv) + " %");
        Line("… jumps starting with gaze on button (catch-up)", x => string.Format(inv, "{0} ({1})", x.DanceJumps, x.DanceCatchUpJumps));
        Line("… settled on button (≥ 150 ms into the visit, s)", x => x.DanceSettledSeconds.ToString("0.0", inv));
        Line("… ring speed once settled on button (px/s)", x => x.DanceSettledRingSpeedPxPerS.ToString("0", inv));
        Line("… pin/release flips once settled on button", x => x.DanceSettledPinFlips.ToString(inv));
        sb.AppendLine();
        foreach (var probe in replay.Probes)
        {
            sb.AppendLine(inv, $"- **{probe.Mode}**: glide-ins {Q(probe.GlideInPx)}; hops {Q(probe.HopPx)}; snaps {Q(probe.SnapPx)}");
        }
        sb.AppendLine(inv, $"- **today, eye leaves P → hop starts (ms)**: {Q(HopLagsMs(replay))}");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Parses the recorder's format locally: <c>#</c> header/trailer lines, then
    /// <c>x,y,timestampUs,isValid</c>.</summary>
    private static List<GazePoint> ReadSamples(string path)
    {
        var samples = new List<GazePoint>(20_000);
        foreach (var line in File.ReadLines(path))
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#')
                continue;
            var parts = span.ToString().Split(',', 4);
            if (parts.Length < 4)
                continue;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
            {
                samples.Add(new GazePoint(x, y, t, parts[3].Trim() == "1"));
            }
        }
        return samples;
    }

    private sealed class RecordingToolbarVisibility : IToolbarVisibility
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
