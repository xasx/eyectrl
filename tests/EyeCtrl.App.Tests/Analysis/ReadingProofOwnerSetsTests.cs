namespace EyeCtrl.App.Tests.Analysis;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Startup;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// F3, rule KE ("one look fills the button"): the reading-proof investigation's must-fire, must-not-fire
/// and watch sets (<c>reading-proof/evidence/ReadingProofTests.cs.txt</c>, same scripts and seeds) as the
/// owner's safety net, through the App's chain at the owner's settings: a real <see cref="GazeEngine"/>
/// with the App's parsed launch arm (<c>RingArmArgument.Parse([])</c>), a real
/// <see cref="GazeEngineAdapter"/> on the current dispatcher, a real <see cref="CursorCircleViewModel"/>
/// and <see cref="ActivationButtonViewModel"/>, the dispatcher pumped after every sample, at 33 ms (the
/// owner's tracker grid) and at 90 Hz. Nothing is shown; EyeCtrl is not launched.
/// <para>
/// The owner's pending A/B is the button dwell („Aktivator (ms)" 500 or 600), a settings value only, so
/// every set runs at both. <c>DpiHelper</c> is left alone: it feeds only the post-fire menu and
/// magnifier layout, never the offer engine (configured at scale 2.0 below).
/// </para>
/// <para>
/// F3 fixup-01 (KE-D2, <c>reading-proof-2</c>): the reading sets split into paths that rest on the drawn
/// disc (a reader walking through the opaque button: pinned counts) and paths off it (must stay 0), and
/// review-01's probe groups A–G plus the offset pursuits H run as known-behaviour rows at the prototype's
/// numbers. Every row equals the prototype's <c>D80_J60_cap2_F5.tsv</c>.
/// </para>
/// <para>
/// <c>EYECTRL_READING_PROOF_OUT</c> (a directory) writes the rows in the reading-proof-2 harness's TSV format.
/// </para>
/// </summary>
public class ReadingProofOwnerSetsTests
{
    private const string OutEnvironmentVariable = "EYECTRL_READING_PROOF_OUT";
    private const double OwnerScale = 2.0;
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly RingArmLaunch AppLaunch = RingArmArgument.Parse(Array.Empty<string>());
    private static readonly PhysicalPoint RightP = new(2600, 700);   // B down-left
    private static readonly PhysicalPoint LeftP = new(1300, 700);    // B down-right
    private static readonly (long FrameUs, string Rate)[] Rates = [(33_000L, "33ms"), (11_111L, "90Hz")];

    /// <summary>The engine-relevant values of the owner's settings copy
    /// (<c>C:\t\gxsit\sitting-2026-11-09-settings.json</c>), as <see cref="RingStayOwnerScenarioTests"/>
    /// builds them: One-Euro, R 80, onset 200, show after 1000, pause grace 500, ring 200, manual
    /// selection; adaptation off. The button dwell is the A/B lever.</summary>
    private static EyeCtrlSettings OwnerSettings(int buttonDwellMs)
    {
        var s = new EyeCtrlSettings();
        s.Cursor.FilterMode = GazeFilterMode.OneEuro;
        s.Cursor.SmoothingFactor = 0.6;
        s.Cursor.OneEuroMinCutoff = 1.5;
        s.Cursor.OneEuroBetaPerDiagonal = 20;
        s.Cursor.MaxGapInterpolationMs = 150;
        s.Cursor.DeadbandRadiusPx = 4.5;
        s.Cursor.DeadbandConfirmSamples = 2;
        s.Cursor.CircleSize = 200;
        s.Dwell.FixationDetectionMs = 200;
        s.Dwell.ActivationButtonDwellMs = buttonDwellMs;
        s.Dwell.FixationRadiusPixels = 80;
        s.Dwell.VelocityThresholdPxPerSec = 100;
        s.Dwell.DwellPauseGraceMs = 500;
        s.Dwell.OfferShowAfterMs = 1000;
        s.SelectionMode = SelectionMode.ManualSelection;
        s.Adaptation.Enabled = false;
        return s;
    }

    private sealed class StubControlFinder : EyeCtrl.Core.Interfaces.IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }

    private sealed record Row(long Us, PhysicalPoint Ring, bool Visible, double Dwell, string Events, PhysicalPoint Filtered);

    private sealed class Rig : IDisposable
    {
        private readonly TestFakes.FakeGazeProvider _provider = new();
        private readonly GazeEngine _engine;
        private readonly GazeEngineAdapter _adapter;
        private readonly WeakReferenceMessenger _messenger = new();
        private readonly Random _rng;
        private readonly List<string> _pending = new();
        private readonly long _frameUs;
        private PhysicalPoint _drawn;
        private PhysicalPoint _filtered;

        public CursorCircleViewModel Ring { get; }
        public ActivationButtonViewModel Button { get; }
        public InteractionManager Im => _engine.InteractionManager;
        public List<Row> Rows { get; } = new();
        public long NowUs { get; private set; }

        public Rig(int seed, long frameUs, int buttonDwellMs)
        {
            _rng = new Random(seed);
            _frameUs = frameUs;
            var settings = OwnerSettings(buttonDwellMs);
            _engine = new GazeEngine(_provider, new TestFakes.FakeInputSimulator(), settings);
            _engine.InteractionManager.RingPinPolicy = AppLaunch.PinPolicy;              // App.xaml.cs
            _engine.ConfigureActivationOffers(Screen, OwnerScale);
            Ring = new CursorCircleViewModel(settings, _messenger) { RingHoldPolicy = AppLaunch.HoldPolicy };
            Button = new ActivationButtonViewModel(_messenger);
            _adapter = new GazeEngineAdapter(_engine, settings, _messenger, Dispatcher.CurrentDispatcher,
                NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(), screenDip: () => (1920, 1200));
            Ring.PropertyChanged += OnRingChanged;
            _messenger.Register<Rig, EyeCtrl.ViewModels.Messages.GazeCursorMovedMessage>(this, (_, m) => _filtered = m.Position);
            Im.ActivationOfferFired += (_, _) => _pending.Add("FIRED");
            Im.ActivationOfferClosed += () => _pending.Add("CLOSED");
            Im.FixationEnded += () =>
            {
                if (ScriptStartUs >= 0 && OfferedRunEndUs < 0 && NowUs >= ScriptStartUs)
                    OfferedRunEndUs = NowUs;
            };
        }

        /// <summary>The first sample of the path's script; -1 before it starts.</summary>
        public long ScriptStartUs { get; set; } = -1;

        /// <summary>The offered run's end: the first run end at or after the script's start. -1: none.</summary>
        public long OfferedRunEndUs { get; private set; } = -1;

        private void OnRingChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CursorCircleViewModel.Position)) _drawn = Ring.Position;
            else if (e.PropertyName == nameof(CursorCircleViewModel.MoveTo)) _drawn = Ring.MoveTo.Target;
        }

        private double Gauss(double sigma)
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        public void Sample(PhysicalPoint p, bool valid = true)
        {
            NowUs += _frameUs;
            _provider.Raise(new GazePoint(p.X, p.Y, NowUs, valid));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Rows.Add(new Row(NowUs, _drawn, Ring.IsVisible, Im.ActivationOffers?.DwellProgress ?? 0,
                string.Join("; ", _pending), _filtered));
            _pending.Clear();
        }

        /// <summary>Every fixation target the scripts rested on, for the on / off the disc split.</summary>
        public List<PhysicalPoint> Targets { get; } = new();

        /// <summary>The last sample of a scripted look AT the button: a rest whose target lies inside the live
        /// offer's hit area, once the rest is <see cref="ActivationOfferEngine.LookMatureMs"/> old (the engine's
        /// "real activity"; a saccade's transit is not a rest). Recorded from the script, not read from the
        /// engine, so a near miss the engine wrongly counted as real activity cannot move it. -1: none.</summary>
        public long LastRealLookUs { get; set; } = -1;

        public void Rest(PhysicalPoint at, double ms, double sigma)
        {
            Targets.Add(at);
            long start = NowUs + _frameUs;
            int guard = 0;
            while (NowUs + _frameUs - start < ms * 1000 && guard++ < 2000)
            {
                Sample(new PhysicalPoint(at.X + Gauss(sigma), at.Y + Gauss(sigma)));
                var offer = Im.ActivationOffers?.CurrentOffer;
                if (offer is not null && at.DistanceTo(offer.Button) <= offer.HitRadiusPx
                    && NowUs - start >= ActivationOfferEngine.LookMatureMs * 1000L)
                    LastRealLookUs = NowUs;
            }
        }

        public void Saccade(PhysicalPoint from, PhysicalPoint to)
            => Sample(new PhysicalPoint((from.X + to.X) / 2, (from.Y + to.Y) / 2));

        public void Blink(double ms)
        {
            long start = NowUs + _frameUs;
            int guard = 0;
            while (NowUs + _frameUs - start < ms * 1000 && guard++ < 2000)
                Sample(new PhysicalPoint(0, 0), valid: false);
        }

        public void Read(PhysicalPoint from, int words, double ms, double dx = 150)
        {
            for (int i = 0; i < words; i++)
                Rest(new PhysicalPoint(from.X + (dx * i), from.Y), ms, 4);
        }

        public void Dispose()
        {
            _messenger.UnregisterAll(this);
            _adapter.Dispose();
            _engine.Dispose();
            Ring.Dispose();
        }
    }

    private static PhysicalPoint Offset(PhysicalPoint b, PhysicalPoint p, double dist, string dir)
    {
        double ux = (b.X - p.X) / b.DistanceTo(p), uy = (b.Y - p.Y) / b.DistanceTo(p);
        return dir switch
        {
            "toward P" => new PhysicalPoint(b.X - (ux * dist), b.Y - (uy * dist)),
            "beyond B" => new PhysicalPoint(b.X + (ux * dist), b.Y + (uy * dist)),
            "side +" => new PhysicalPoint(b.X - (uy * dist), b.Y + (ux * dist)),
            "side -" => new PhysicalPoint(b.X + (uy * dist), b.Y - (ux * dist)),
            "down" => new PhysicalPoint(b.X, b.Y + dist),
            _ => b,
        };
    }

    /// <summary>One path's outcome. <paramref name="OnDisc"/>: the script rested at least once within the drawn
    /// disc's radius (100 px at scale 2.0) of B — a reader walking through the opaque button.</summary>
    private sealed record Result(string Set, string Name, bool Fired, double FireMs, double Peak, double UpMs, double RingHeldMs, bool OnDisc,
        double LastRealLookMs = 0)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{(Fired ? "FIRED" : "  -  ")} {(Fired ? FireMs.ToString("F0", CultureInfo.InvariantCulture) : ""),5} peak {Peak,4:P0} up {UpMs,5:F0} held {RingHeldMs,5:F0} {(OnDisc ? "disc" : "    ")}  {Name}");

        /// <summary>The reading-proof-2 harness's TSV row (its last-but-one column, a prototype-only
        /// diagnostic, is written as -1).</summary>
        public string Tsv(string rate, int dwell) => string.Create(CultureInfo.InvariantCulture,
            $"{rate}\t{dwell}\t{Set}\t{(Fired ? 1 : 0)}\t{(Fired ? FireMs : -1):F0}\t{Peak:F2}\t{UpMs:F0}\t{RingHeldMs:F0}\t{(OnDisc ? 1 : 0)}\t-1\t{Name}");
    }

    private const double DrawnDiscRadiusPx = LayoutConstants.SharedButtonRadius * OwnerScale;

    private sealed record Path(string Name, PhysicalPoint At, int Seed, Action<Rig, PhysicalPoint, PhysicalPoint> Script);

    /// <summary>Reads in, rests 1.2 s at P (the button shows at ~1.09 s), then runs the script. Fire time,
    /// button-up and ring-held are measured from the script's first sample.</summary>
    private static Result Run(string set, Path path, long frameUs, int buttonDwellMs)
    {
        using var rig = new Rig(path.Seed, frameUs, buttonDwellMs);
        rig.Read(new PhysicalPoint(path.At.X - 450, path.At.Y), 3, 250);
        rig.Rest(path.At, 1200, 6);
        var offer = rig.Im.ActivationOffers!.CurrentOffer;
        Check.WithCustomMessage($"{path.Name}: an offer after the 1.2 s rest").That(offer).IsNotNull();
        int go = rig.Rows.Count;
        int targetsBefore = rig.Targets.Count;
        rig.ScriptStartUs = rig.NowUs + frameUs;
        path.Script(rig, offer!.Point, offer.Button);
        var rows = rig.Rows.Skip(go).ToList();
        long t0 = rows[0].Us - frameUs;
        int fired = rows.FindIndex(r => r.Events.Contains("FIRED", StringComparison.Ordinal));
        int closed = rows.FindIndex(r => r.Events.Contains("CLOSED", StringComparison.Ordinal));
        double upMs = ((closed >= 0 ? rows[closed].Us : rows[^1].Us) - t0) / 1000.0;
        // The ring "held" while it sits on P and the gaze is > 150 px from P (the owner is elsewhere).
        var held = rows.Where(r => r.Visible && r.Ring.DistanceTo(offer.Point) < 1 && r.Filtered.DistanceTo(offer.Point) > 150).ToList();
        double heldMs = held.Count == 0 ? 0 : (held[^1].Us - t0) / 1000.0;
        bool onDisc = rig.Targets.Skip(targetsBefore).Any(t => t.DistanceTo(offer.Button) <= DrawnDiscRadiusPx);
        // The last real activity: the offered run's end or the last mature scripted look at the button,
        // whichever is later.
        double lastRealMs = Math.Max(rig.OfferedRunEndUs, rig.LastRealLookUs) is var last && last > t0 ? (last - t0) / 1000.0 : 0;
        return new Result(set, path.Name, fired >= 0, fired >= 0 ? (rows[fired].Us - t0) / 1000.0 : double.NaN,
            rows.Select(r => r.Dwell).DefaultIfEmpty(0).Max(), upMs, heldMs, onDisc, lastRealMs);
    }

    /// <summary>The 52 deliberate looks: on B, landings 120/160 px off B, the 5th sitting's #16 (a stop
    /// 170/190/200 px off, then onto B) and #2/#17 (400 ms on B, then a slip), a blink at mid-fill, and a
    /// 250 ms glance off and back — on both sides of the word.
    /// <para>
    /// Placement B (7th sitting, 2026-11-12): B is 410 px from P and the hit 265 px (310 / 200 before). The
    /// distances that were placed relative to the hit moved out by the same 65 px, so each keeps its role:
    /// the "just outside the hit" slip 215 → 280, the glance off 220 / 300 → 285 / 365, R3's near-miss words
    /// 220–260 → 285–325 and its line 230 → 295 px below B, and D's near misses 215 / 240 → 280 / 305. The
    /// owner's measured distances (landings, #16's stops, the 165 / 190 slips) are unchanged.
    /// </para></summary>
    private static IEnumerable<Path> MustFire()
    {
        foreach (var (pAt, side) in new[] { (RightP, "R"), (LeftP, "L") })
        {
            foreach (var sigma in new[] { 8.0, 20.0 })
                yield return new($"{side} on B σ{sigma}", pAt, 1, (r, p, b) => { r.Saccade(p, b); r.Rest(b, 1200, sigma); });
            foreach (var dir in new[] { "down", "beyond B", "side +", "side -" })
                foreach (var dist in new[] { 120.0, 160.0 })
                    yield return new($"{side} land {dist} {dir}", pAt, 2, (r, p, b) => { var q = Offset(b, p, dist, dir); r.Saccade(p, q); r.Rest(q, 1200, 8); });
            foreach (var dir in new[] { "down", "side +", "beyond B" })
                foreach (var dist in new[] { 170.0, 190.0, 200.0 })
                    yield return new($"{side} #16 stop {dist} {dir} 650ms → B", pAt, 3, (r, p, b) =>
                    {
                        var q = Offset(b, p, dist, dir); r.Saccade(p, q); r.Rest(q, 650, 10);
                        r.Rest(Offset(b, p, 40, dir), 1000, 10);
                    });
            foreach (var slip in new[] { 165.0, 190.0, 280.0 })
                yield return new($"{side} #2/#17 400ms on B then slip {slip}", pAt, 4, (r, p, b) =>
                {
                    var q = Offset(b, p, 40, "down"); r.Saccade(p, q); r.Rest(q, 400, 10);
                    r.Rest(Offset(b, p, slip, "down"), 1200, 10);
                });
            yield return new($"{side} blink 150 at mid-fill", pAt, 5, (r, p, b) => { r.Saccade(p, b); r.Rest(b, 250, 8); r.Blink(150); r.Rest(b, 800, 8); });
            foreach (var g in new[] { "P", "beyond", "side" })
                yield return new($"{side} 250ms glance to {g}, back", pAt, 6, (r, p, b) =>
                {
                    r.Saccade(p, b); r.Rest(b, 250, 8);
                    r.Rest(g == "P" ? p : g == "beyond" ? Offset(b, p, 365, "beyond B") : Offset(b, p, 285, "side +"), 250, 6);
                    r.Rest(b, 800, 8);
                });
        }
    }

    /// <summary>The slip to 280 px lands outside the 265 px hit area (215 / 200 before the 7th sitting): it misses
    /// before F3 too, and no rule
    /// that leaves the hit radius alone can fire it (reading-proof §1). It stays in the set, as the one
    /// expected miss per side.</summary>
    private static bool OutsideTheHit(string name) => name.EndsWith("slip 280", StringComparison.Ordinal);

    /// <summary>The 130 reading paths: after the rest the eye drops to a line +70…+280 px below, starting at
    /// P −450…+150, and reads rightward (150 px words at 200/250/300 ms), on both sides; the same line read
    /// on or back; and dry run 2's two slow R3 paths.</summary>
    private static IEnumerable<Path> MustNotFire()
    {
        foreach (var (pAt, side) in new[] { (RightP, "R"), (LeftP, "L") })
        {
            foreach (var dy in new[] { 70.0, 140.0, 210.0, 280.0 })
                foreach (var dx in new[] { -450.0, -300.0, -150.0, 0.0, 150.0 })
                    foreach (var ms in new[] { 200.0, 250.0, 300.0 })
                        yield return new($"{side} read line +{dy} from P{dx:+0;-0;+0} {ms}ms", pAt, 7,
                            (r, p, _) => r.Read(new PhysicalPoint(p.X + dx, p.Y + dy), 10, ms));
            foreach (var ms in new[] { 250.0, 300.0 })
            {
                yield return new($"{side} same line on {ms}ms", pAt, 8, (r, p, _) => r.Read(new PhysicalPoint(p.X + 150, p.Y), 10, ms));
                yield return new($"{side} same line back {ms}ms", pAt, 8, (r, p, _) => r.Read(new PhysicalPoint(p.X - 150, p.Y), 10, ms, dx: -150));
            }
        }
        foreach (var r3 in R3Paths())
            yield return r3;
    }

    /// <summary>Dry run 2's R3: slow re-reading just outside the hit area kept the button (and the ring) up
    /// for 5 s and more.</summary>
    private static IEnumerable<Path> R3Paths()
    {
        yield return new("R slow 600ms words 285-325 beside B", RightP, 9, (r, p, b) =>
        {
            for (int i = 0; i < 8; i++)
                r.Rest(Offset(b, p, i % 2 == 0 ? 285 : 325, i % 4 < 2 ? "side +" : "beyond B"), 600, 4);
        });
        yield return new("L slow 600ms line through B's row", LeftP, 9, (r, p, b) => r.Read(new PhysicalPoint(b.X - 450, b.Y + 295), 8, 600));
    }

    /// <summary>The residual risk, not a must-not-fire set: slow reading (400/500 ms words) on lines under the
    /// word, refixation pairs (two looks 40 px apart: one fixation) and 120 px words.</summary>
    private static IEnumerable<Path> Watch()
    {
        foreach (var (pAt, side) in new[] { (RightP, "R"), (LeftP, "L") })
        {
            foreach (var dy in new[] { 140.0, 210.0 })
                foreach (var dx in new[] { -300.0, -150.0, 0.0 })
                {
                    foreach (var ms in new[] { 400.0, 500.0 })
                        yield return new($"WATCH {side} read line +{dy} from P{dx:+0;-0;+0} {ms}ms", pAt, 10,
                            (r, p, _) => r.Read(new PhysicalPoint(p.X + dx, p.Y + dy), 8, ms));
                    yield return new($"WATCH {side} read line +{dy} from P{dx:+0;-0;+0} 250ms 120px words", pAt, 10,
                        (r, p, _) => r.Read(new PhysicalPoint(p.X + dx, p.Y + dy), 12, 250, dx: 120));
                    yield return new($"WATCH {side} line +{dy} from P{dx:+0;-0;+0} refixation pairs (2x250ms, 40px)", pAt, 10,
                        (r, p, _) =>
                        {
                            for (int i = 0; i < 6; i++)
                            {
                                r.Rest(new PhysicalPoint(p.X + dx + (150 * i), p.Y + dy), 250, 4);
                                r.Rest(new PhysicalPoint(p.X + dx + (150 * i) + 40, p.Y + dy), 250, 4);
                            }
                        });
                }
        }
    }

    /// <summary>F3 review-01's probe paths (groups A–G) and reading-proof-2's offset pursuits (H), with the
    /// harness's scripts and seeds (<c>reading-proof-2/evidence/ReadingProof2Tests.cs.txt</c>).</summary>
    private static IEnumerable<Path> ProbePaths()
    {
        foreach (var (pAt, side) in new[] { (RightP, "R"), (LeftP, "L") })
        {
            // A: reading with word steps near R (the sets use 150 px; watch 120 px) — small text.
            foreach (var dx in new[] { 80.0, 90.0, 100.0, 110.0 })
                foreach (var ms in new[] { 200.0, 250.0, 300.0 })
                    foreach (var dy in new[] { 70.0, 140.0 })
                        foreach (var x0 in new[] { -300.0, 0.0 })
                            yield return new($"A {side} read {dx}px words {ms}ms line +{dy} from P{x0:+0;-0;+0}", pAt, 11,
                                (r, p, _) => r.Read(new PhysicalPoint(p.X + x0, p.Y + dy), 14, ms, dx));
            // B: land off-centre inside the hit, then correct onto B.
            foreach (var dir in new[] { "down", "side +", "beyond B" })
                foreach (var dist in new[] { 120.0, 150.0, 180.0 })
                    foreach (var stay in new[] { 200.0, 300.0, 400.0 })
                        yield return new($"B {side} land {dist} {dir} {stay}ms then correct onto B", pAt, 12, (r, p, b) =>
                        {
                            var q = Offset(b, p, dist, dir); r.Saccade(p, q); r.Rest(q, stay, 8);
                            r.Saccade(q, b); r.Rest(b, 1500, 8);
                        });
            // C: noisy gaze on B / on a landing.
            foreach (var sigma in new[] { 30.0, 45.0, 60.0 })
            {
                yield return new($"C {side} on B sigma {sigma}", pAt, 13, (r, p, b) => { r.Saccade(p, b); r.Rest(b, 2500, sigma); });
                yield return new($"C {side} land 140 down sigma {sigma}", pAt, 13, (r, p, b) => { var q = Offset(b, p, 140, "down"); r.Saccade(p, q); r.Rest(q, 2500, sigma); });
            }
            // D: going for the button but stuck just outside the hit (a near miss), then back onto B.
            foreach (var slip in new[] { 280.0, 305.0 })
                foreach (var hold in new[] { 1000.0, 2000.0, 3000.0 })
                    yield return new($"D {side} 400ms on B, near miss {slip} for {hold}ms, back onto B", pAt, 14, (r, p, b) =>
                    {
                        var q = Offset(b, p, 40, "down"); r.Saccade(p, q); r.Rest(q, 400, 10);
                        r.Rest(Offset(b, p, slip, "down"), hold, 10);
                        r.Rest(b, 1500, 10);
                    });
            // E: hopping between spots inside the hit (100 px hops).
            foreach (var hop in new[] { 300.0, 400.0, 450.0 })
                yield return new($"E {side} hops 100px inside the hit every {hop}ms", pAt, 15, (r, p, b) =>
                {
                    var spots = new[] { Offset(b, p, 50, "side +"), Offset(b, p, 50, "side -"), Offset(b, p, 60, "down") };
                    for (int i = 0; i < 9; i++) r.Rest(spots[i % 3], hop, 8);
                });
            // G: the eye follows the rising fill line (the disc is 100 DIP = 200 px; the fill rises from its
            // bottom). Closed loop on the engine's progress: pursuit, and steps every 150 / 250 ms.
            yield return new($"G {side} pursues the fill line", pAt, 17, (r, p, b) =>
            {
                r.Saccade(p, new PhysicalPoint(b.X, b.Y + 95));
                for (int i = 0; i < 300 && r.Rows.Count < 100000; i++)
                {
                    double prog = r.Im.ActivationOffers?.DwellProgress ?? 0;
                    r.Rest(new PhysicalPoint(b.X, b.Y + 95 - (190 * prog)), 1, 4);
                }
            });
            foreach (var step in new[] { 150.0, 250.0 })
                yield return new($"G {side} steps to the fill line every {step}ms", pAt, 17, (r, p, b) =>
                {
                    r.Saccade(p, new PhysicalPoint(b.X, b.Y + 95));
                    for (int i = 0; i < 16; i++)
                    {
                        double prog = r.Im.ActivationOffers?.DwellProgress ?? 0;
                        r.Rest(new PhysicalPoint(b.X, b.Y + 95 - (190 * prog)), step, 6);
                    }
                });
            // H: the fill pursuit with a tracker OFFSET (the owner's reported gaze is often 150+ px off): the
            // pursued line is shifted sideways / down, so the looks are not on the disc.
            foreach (var (ox, oy) in new[] { (60.0, 0.0), (120.0, 0.0), (0.0, 60.0), (90.0, 90.0) })
            {
                yield return new($"H {side} pursues the fill line offset ({ox},{oy})", pAt, 18, (r, p, b) =>
                {
                    r.Saccade(p, new PhysicalPoint(b.X + ox, b.Y + oy + 95));
                    for (int i = 0; i < 300 && r.Rows.Count < 100000; i++)
                    {
                        double prog = r.Im.ActivationOffers?.DwellProgress ?? 0;
                        r.Rest(new PhysicalPoint(b.X + ox, b.Y + oy + 95 - (190 * prog)), 1, 4);
                    }
                });
                yield return new($"H {side} steps to the fill line every 150ms offset ({ox},{oy})", pAt, 18, (r, p, b) =>
                {
                    r.Saccade(p, new PhysicalPoint(b.X + ox, b.Y + oy + 95));
                    for (int i = 0; i < 16; i++)
                    {
                        double prog = r.Im.ActivationOffers?.DwellProgress ?? 0;
                        r.Rest(new PhysicalPoint(b.X + ox, b.Y + oy + 95 - (190 * prog)), 150, 6);
                    }
                });
            }
            // I (F3 review-02 N1): reading 150 px words with 100 ms of lost samples at every word change (the
            // tracker drops samples during saccades), then the next word. The script of review-02's
            // F3Review2ProbeTests, gap 100 ms only.
            foreach (var dy in new[] { 70.0, 140.0, 210.0 })
                foreach (var x0 in new[] { -450.0, -300.0, -150.0, 0.0 })
                    foreach (var ms in new[] { 250.0, 300.0 })
                        yield return new($"I {side} read line +{dy} from P{x0:+0;-0;+0} {ms}ms gap 100ms", pAt, 21,
                            (r, p, _) =>
                            {
                                for (int i = 0; i < 10; i++)
                                {
                                    if (i > 0) r.Blink(100);
                                    r.Rest(new PhysicalPoint(p.X + x0 + (150 * i), p.Y + dy), i > 0 ? ms - 100 : ms, 4);
                                }
                            });
            // F: an offset jump mid-fill (a tracker bias step) of 120 px.
            foreach (var at in new[] { 200.0, 300.0, 400.0 })
                yield return new($"F {side} on B {at}ms then 120px step, stay", pAt, 16, (r, p, b) =>
                {
                    var a = Offset(b, p, 60, "side +"); var c = Offset(b, p, 60, "side -");
                    r.Saccade(p, a); r.Rest(a, at, 8); r.Rest(c, 1500, 8);
                });
        }
    }

    private static List<Result> RunAll(string set, IEnumerable<Path> paths, long frameUs, string rate, int buttonDwellMs)
    {
        var results = paths.Select(p => Run(set == "P" ? "P" + p.Name[0] : set, p, frameUs, buttonDwellMs)).ToList();
        var dir = Environment.GetEnvironmentVariable(OutEnvironmentVariable);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(System.IO.Path.Combine(dir, $"{set}-{rate}-dwell{buttonDwellMs}.tsv"),
                results.Select(r => r.Tsv(rate, buttonDwellMs)));
        }
        return results;
    }

    private static string Table(IEnumerable<Result> results) => string.Join("\n", results.Select(r => "   " + r));

    private static double Median(IEnumerable<double> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l[l.Count / 2];
    }

    private static long FrameOf(string rate) => rate == "33ms" ? 33_000L : 11_111L;

    // ── The sets ─────────────────────────────────────────────────────────────

    [StaTheory]
    [InlineData(500)]
    [InlineData(600)]
    public void MustFire_EveryDeliberateLookInsideTheHitArea_Fires(int buttonDwellMs)
    {
        // Mutants: a fill that restarts on every new look (the glance back and the blink lose their fill), a
        // fill that never takes a moved look (no slip fires), a disc as wide as the hit (the slip keeps its
        // old fill and fires at once).
        foreach (var (frameUs, rate) in Rates)
        {
            var results = RunAll("MF", MustFire(), frameUs, rate, buttonDwellMs);
            Check.That(results).HasSize(52);
            var missed = results.Where(r => !r.Fired && !OutsideTheHit(r.Name)).ToList();
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: deliberate looks that did not fire:\n{Table(missed)}")
                .That(missed).IsEmpty();
            // The 280 px slip is outside the hit area: it never fires (it would take a wider hit).
            Check.That(results.Count(r => r.Fired)).IsEqualTo(50);

            // The prototype's medians (561 / 522 ms at dwell 500; 660 / 622 at 600): a deliberate look still
            // fires at the dwell plus the saccade's sample or two.
            double median = Median(results.Where(r => r.Fired).Select(r => r.FireMs));
            double limit = buttonDwellMs + (frameUs == 33_000 ? 70 : 30);
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: median fire {median:F0} ms\n{Table(results)}")
                .That(median).IsLessOrEqualThan(limit);
            // A 250 ms glance off and back keeps its fill (KE's same-spot return): well under a restart.
            foreach (var g in results.Where(r => r.Name.Contains("glance", StringComparison.Ordinal)))
                Check.WithCustomMessage(g.ToString()).That(g.FireMs).IsLessOrEqualThan(buttonDwellMs + 420);
            // The slip off the disc (165 / 190 px below B) is a jump to a new look: it still restarts the
            // fill (1122 ms at 33 ms / 500), so a disc widened past the slip would be caught.
            foreach (var s in results.Where(r => r.Name.Contains("slip 165", StringComparison.Ordinal) || r.Name.Contains("slip 190", StringComparison.Ordinal)))
                Check.WithCustomMessage(s.ToString()).That(s.FireMs).IsStrictlyGreaterThan(buttonDwellMs + 400);
        }
    }

    [StaTheory]
    [InlineData(500, 3, 5)]
    [InlineData(600, 1, 1)]
    public void MustNotFire_NoReadingPathOffTheDisc_FiresTheButton(int buttonDwellMs, int onDisc33, int onDisc90)
    {
        // Before F3 42 / 39 of these 130 fired at dwell 500 (dry run 2's F-1). Off the drawn disc nothing may
        // fire. On it (a scripted reader walking THROUGH the opaque button), KE-D2 fills from a look on the
        // face, and the counts are pinned so a change shows. Since the 7th sitting's placement B (B 475 px
        // from P, hit 265) the lines under the word cross the disc less: 21 paths on it (48 at 310 px), and
        // 3 / 5 fire at dwell 500, 1 / 1 at 600 (13 / 10 and 5 / 5 before). Mutants: no restart, a moved look
        // credits at once, "moved" against B, the masked verdict, a disc as wide as the hit or the rim — red.
        foreach (var (frameUs, rate) in Rates)
        {
            var results = RunAll("NF", MustNotFire(), frameUs, rate, buttonDwellMs);
            Check.That(results).HasSize(130);
            Check.That(results.Count(r => r.OnDisc)).IsEqualTo(21);
            var offDisc = results.Where(r => r.Fired && !r.OnDisc).ToList();
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: reading paths OFF the disc that FIRED the button:\n{Table(offDisc)}")
                .That(offDisc).IsEmpty();
            var onDisc = results.Where(r => r.Fired && r.OnDisc).ToList();
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: {onDisc.Count} on-disc reading fires:\n{Table(onDisc)}")
                .That(onDisc.Count).IsEqualTo(rate == "33ms" ? onDisc33 : onDisc90);
            // The button closes within linger + cap of the last real activity, and the ring follows it (the
            // stay's existing OfferClosed release). Primary pin (7th sitting, placement B): per path, against
            // the last real activity the rig recorded (the offered run's end, or the last mature scripted look
            // inside the hit), so it holds at any placement. Mutants: no near-miss cap, the cap from the last
            // near miss — red.
            var unfired = results.Where(r => !r.Fired).ToList();
            double capMs = ActivationOfferEngine.ProductOfferExpiryMs + ActivationOfferEngine.NearMissCapMs + (frameUs / 1000.0);
            var overCap = unfired.Where(r => r.UpMs > r.LastRealLookMs + capMs).ToList();
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: up longer than the last real activity + {capMs:F0} ms:\n"
                    + string.Join("\n", overCap.Select(r => $"   last real {r.LastRealLookMs:F0} {r}")))
                .That(overCap).IsEmpty();
            // The absolute ceiling, so a runaway hold is still caught: the measured maximum rounded up to 50 ms,
            // 2311 ms up / 2046 ms held (2000 / 1716 at 310 px, pinned 2050 / 1750). The farther hit is crossed
            // later on the +140…+280 lines under the word, and a mature look there is real activity; the real
            // sittings' stay-read time went down (12.11: 104 → 77 s, 10.11: 57 → 40 s).
            var longest = unfired.MaxBy(r => r.UpMs)!;
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: {longest}").That(longest.UpMs).IsLessOrEqualThan(2350);
            var held = unfired.MaxBy(r => r.RingHeldMs)!;
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: {held}").That(held.RingHeldMs).IsLessOrEqualThan(2050);
        }
    }

    [StaFact]
    public void TheR3Paths_CloseTheButtonWithin1_8s_AndReleaseTheRing()
    {
        // Dry run 2's worst case: before F3 the button and the ring on the old word were still up at 5 s.
        // KE-D2 keeps F3's 1.55 s. Mutants: no near-miss cap, the cap from the last near miss, transit
        // samples extend, cap2 armed by the offered run (R3 → 2.0 s), the exemption not tied to one look — red.
        foreach (var (frameUs, rate) in Rates)
        {
            foreach (var r in R3Paths().Select(p => Run("NF", p, frameUs, 500)))
            {
                Check.WithCustomMessage($"{rate}: {r}").That(r.Fired).IsFalse();
                Check.WithCustomMessage($"{rate}: {r}").That(r.UpMs).IsLessOrEqualThan(1600);
                Check.WithCustomMessage($"{rate}: {r}").That(r.RingHeldMs).IsLessOrEqualThan(1600);
            }
        }
    }

    [StaTheory]
    [InlineData(500, 0, 3, 0, 3)]
    [InlineData(600, 0, 0, 0, 0)]
    public void Watch_TheResidualRisk_StaysWhereThePrototypeMeasuredIt(int buttonDwellMs, int onDisc33, int offDisc33, int onDisc90, int offDisc90)
    {
        // Not a must-not-fire set: a single half-second look at a word inside the button's area is the same as
        // a look at the button. Pinned, so a change shows. Since the 7th sitting's placement B (475 px / hit
        // 265) no watch line crosses the disc, and 0 + 3 / 0 + 3 fire at dwell 500, none at 600 (11 + 4 /
        // 12 + 4 and 6 + 0 / 6 + 0 at 310 px / hit 200).
        foreach (var (frameUs, rate) in Rates)
        {
            var results = RunAll("W", Watch(), frameUs, rate, buttonDwellMs);
            Check.That(results).HasSize(48);
            var fired = results.Where(r => r.Fired).ToList();
            int on = fired.Count(r => r.OnDisc), off = fired.Count(r => !r.OnDisc);
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: {on} on / {off} off the disc fired:\n{Table(fired)}")
                .That((on, off)).IsEqualTo(rate == "33ms" ? (onDisc33, offDisc33) : (onDisc90, offDisc90));
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: 400 ms reading never fires off the disc:\n{Table(fired)}")
                .That(fired.Where(r => !r.OnDisc && r.Name.Contains("400ms", StringComparison.Ordinal))).IsEmpty();
        }
    }

    [StaTheory]
    [InlineData(500, "33ms", 0, 3, 16, 0)]
    [InlineData(500, "90Hz", 0, 4, 12, 0)]
    [InlineData(600, "33ms", 0, 3, 16, 0)]
    [InlineData(600, "90Hz", 0, 3, 16, 0)]
    public void Probes_TheReviewsGroups_AtKeD2sNumbers(int buttonDwellMs, string rate, int smallTextOnDisc, int smallTextOffDisc, int offsetPursuits, int hiddenSaccadesOnDisc)
    {
        // F3 review-01's probe groups and reading-proof-2's offset pursuits, pinned at KE-D2's prototype
        // numbers. F3 (before this fixup): G 2 / 6 at 90 Hz and at 600, H 12 / 4 / 0 / 0, D 4 / 12, and B / E / F
        // about 0.5 s slower. Mutants: no disc rule (B / E / G slow again), contiguity on the filtered point
        // (small text +5), a jump threshold of 30 px or a 20 ms window (H loses rows), F5 off — red.
        // Placement B (7th sitting: 475 px, hit 265): small text A 9 + 8 / 7 + 5 / 3 + 3 / 2 + 2 → 0 + 3 / 0 + 4 /
        // 0 + 3 / 0 + 3 (the lines no longer cross the disc), I on the disc 3 / 3 / 0 / 0 → 0 (none of its
        // lines cross it), B and G one sample later (below).
        long frameUs = FrameOf(rate);
        var results = RunAll("P", ProbePaths(), frameUs, rate, buttonDwellMs);
        Check.That(results).HasSize(256);
        List<Result> G(char g) => results.Where(r => r.Set == "P" + g).ToList();
        // The base's speed: 561 / 522 at dwell 500 since placement B (528 / 511.1 at 310 px). The script's one
        // transit sample of the now 475 px saccade no longer credits, so B and G fire one sample later.
        double baseSpeed = buttonDwellMs + (frameUs == 33_000 ? 61 : 23);

        // A, small text: off the disc exactly F3's count; on it, pinned.
        var a = G('A');
        Check.WithCustomMessage($"A small text, {rate} / {buttonDwellMs}:\n{Table(a.Where(r => r.Fired))}")
            .That((a.Count(r => r.Fired && r.OnDisc), a.Count(r => r.Fired && !r.OnDisc)))
            .IsEqualTo((smallTextOnDisc, smallTextOffDisc));

        // B, land off-centre then correct onto B; E, hops on the disc; G, follow the fill: all fire, at the
        // base's speed (median).
        foreach (var g in new[] { 'B', 'E', 'G' })
        {
            var rows = G(g);
            Check.WithCustomMessage($"{g}, {rate} / {buttonDwellMs}:\n{Table(rows)}").That(rows.All(r => r.Fired)).IsTrue();
            Check.WithCustomMessage($"{g}, {rate} / {buttonDwellMs}:\n{Table(rows)}")
                .That(Median(rows.Select(r => r.FireMs))).IsLessOrEqualThan(baseSpeed);
        }

        // C, noisy gaze, and F, a 120 px tracker step mid-fill: all fire (F at the base's 561 / 522 ms).
        Check.That(G('C').All(r => r.Fired)).IsTrue();
        var f = G('F');
        Check.WithCustomMessage($"F, {rate} / {buttonDwellMs}:\n{Table(f)}").That(f.All(r => r.Fired)).IsTrue();
        Check.WithCustomMessage($"F, {rate} / {buttonDwellMs}:\n{Table(f)}")
            .That(Median(f.Select(r => r.FireMs))).IsLessOrEqualThan(buttonDwellMs + (frameUs == 33_000 ? 61 : 23));

        // D (review F2), held just outside the hit after a look at B, then back: cap2 keeps the button up.
        var d = G('D');
        Check.WithCustomMessage($"D, {rate} / {buttonDwellMs}:\n{Table(d)}").That(d.All(r => r.Fired)).IsTrue();

        // H, the fill followed with a tracker offset (off the disc): the raw-jump rule keeps the fill.
        var h = G('H');
        Check.WithCustomMessage($"H, {rate} / {buttonDwellMs}:\n{Table(h)}").That(h.Count(r => r.Fired)).IsEqualTo(offsetPursuits);
        Check.That(h.Where(r => r.Name.Contains("pursues", StringComparison.Ordinal)).All(r => r.Fired)).IsTrue();

        // I (review-02 N1), reading with 100 ms of lost samples at each word change: the saccade hidden by the
        // gap is still a jump, so nothing fires off the disc (KE-D2 before the fix: 7 / 7 / 2 / 2 of 34); on
        // the disc the count is pinned (3 / 3 / 0 / 0 of 14 at 310 px; at placement B's 475 px none of these
        // lines crosses the disc). Mutant: drop the last point before the gap → red.
        var i = G('I');
        Check.That(i).HasSize(48);
        Check.That(i.Count(r => r.OnDisc)).IsEqualTo(0);
        Check.WithCustomMessage($"I, {rate} / {buttonDwellMs}:\n{Table(i.Where(r => r.Fired))}")
            .That((i.Count(r => r.Fired && r.OnDisc), i.Count(r => r.Fired && !r.OnDisc)))
            .IsEqualTo((hiddenSaccadesOnDisc, 0));
    }
}
