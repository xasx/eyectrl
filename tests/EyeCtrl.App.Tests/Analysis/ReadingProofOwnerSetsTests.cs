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
/// <c>EYECTRL_READING_PROOF_OUT</c> (a directory) writes one line per path.
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
        }

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

        public void Rest(PhysicalPoint at, double ms, double sigma)
        {
            long start = NowUs + _frameUs;
            int guard = 0;
            while (NowUs + _frameUs - start < ms * 1000 && guard++ < 2000)
                Sample(new PhysicalPoint(at.X + Gauss(sigma), at.Y + Gauss(sigma)));
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

    private sealed record Result(string Name, bool Fired, double FireMs, double Peak, double UpMs, double RingHeldMs)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{(Fired ? "FIRED" : "  -  ")} {(Fired ? FireMs.ToString("F0", CultureInfo.InvariantCulture) : ""),5} peak {Peak,4:P0} up {UpMs,5:F0} held {RingHeldMs,5:F0}  {Name}");
    }

    private sealed record Path(string Name, PhysicalPoint At, int Seed, Action<Rig, PhysicalPoint, PhysicalPoint> Script);

    /// <summary>Reads in, rests 1.2 s at P (the button shows at ~1.09 s), then runs the script. Fire time,
    /// button-up and ring-held are measured from the script's first sample.</summary>
    private static Result Run(Path path, long frameUs, int buttonDwellMs)
    {
        using var rig = new Rig(path.Seed, frameUs, buttonDwellMs);
        rig.Read(new PhysicalPoint(path.At.X - 450, path.At.Y), 3, 250);
        rig.Rest(path.At, 1200, 6);
        var offer = rig.Im.ActivationOffers!.CurrentOffer;
        Check.WithCustomMessage($"{path.Name}: an offer after the 1.2 s rest").That(offer).IsNotNull();
        int go = rig.Rows.Count;
        path.Script(rig, offer!.Point, offer.Button);
        var rows = rig.Rows.Skip(go).ToList();
        long t0 = rows[0].Us - frameUs;
        int fired = rows.FindIndex(r => r.Events.Contains("FIRED", StringComparison.Ordinal));
        int closed = rows.FindIndex(r => r.Events.Contains("CLOSED", StringComparison.Ordinal));
        double upMs = ((closed >= 0 ? rows[closed].Us : rows[^1].Us) - t0) / 1000.0;
        // The ring "held" while it sits on P and the gaze is > 150 px from P (the owner is elsewhere).
        var held = rows.Where(r => r.Visible && r.Ring.DistanceTo(offer.Point) < 1 && r.Filtered.DistanceTo(offer.Point) > 150).ToList();
        double heldMs = held.Count == 0 ? 0 : (held[^1].Us - t0) / 1000.0;
        return new Result(path.Name, fired >= 0, fired >= 0 ? (rows[fired].Us - t0) / 1000.0 : double.NaN,
            rows.Select(r => r.Dwell).DefaultIfEmpty(0).Max(), upMs, heldMs);
    }

    /// <summary>The 52 deliberate looks: on B, landings 120/160 px off B, the 5th sitting's #16 (a stop
    /// 170/190/200 px off, then onto B) and #2/#17 (400 ms on B, then a slip), a blink at mid-fill, and a
    /// 250 ms glance off and back — on both sides of the word.</summary>
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
            foreach (var slip in new[] { 165.0, 190.0, 215.0 })
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
                    r.Rest(g == "P" ? p : g == "beyond" ? Offset(b, p, 300, "beyond B") : Offset(b, p, 220, "side +"), 250, 6);
                    r.Rest(b, 800, 8);
                });
        }
    }

    /// <summary>The slip to 215 px lands outside the 200 px hit area: it misses before F3 too, and no rule
    /// that leaves the hit radius alone can fire it (reading-proof §1). It stays in the set, as the one
    /// expected miss per side.</summary>
    private static bool OutsideTheHit(string name) => name.EndsWith("slip 215", StringComparison.Ordinal);

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
        yield return new("R slow 600ms words 220-260 beside B", RightP, 9, (r, p, b) =>
        {
            for (int i = 0; i < 8; i++)
                r.Rest(Offset(b, p, i % 2 == 0 ? 220 : 260, i % 4 < 2 ? "side +" : "beyond B"), 600, 4);
        });
        yield return new("L slow 600ms line through B's row", LeftP, 9, (r, p, b) => r.Read(new PhysicalPoint(b.X - 450, b.Y + 230), 8, 600));
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

    private static List<Result> RunAll(IEnumerable<Path> paths, long frameUs, int buttonDwellMs, string file)
    {
        var results = paths.Select(p => Run(p, frameUs, buttonDwellMs)).ToList();
        var dir = Environment.GetEnvironmentVariable(OutEnvironmentVariable);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(System.IO.Path.Combine(dir, $"{file}-dwell{buttonDwellMs}.txt"), results.Select(r => r.ToString()));
        }
        return results;
    }

    private static string Table(IEnumerable<Result> results) => string.Join("\n", results.Select(r => "   " + r));

    private static double Median(IEnumerable<double> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l[l.Count / 2];
    }

    // ── The sets ─────────────────────────────────────────────────────────────

    [StaTheory]
    [InlineData(500)]
    [InlineData(600)]
    public void MustFire_EveryDeliberateLookInsideTheHitArea_Fires(int buttonDwellMs)
    {
        // Mutants: a fill that restarts on every new look (M2: the glance back and the blink lose their
        // fill, and the #16 stops reach 1.4 s+), a fill that never takes a moved look (no slip fires).
        foreach (var (frameUs, rate) in Rates)
        {
            var results = RunAll(MustFire(), frameUs, buttonDwellMs, $"must-fire-{rate}");
            Check.That(results).HasSize(52);
            var missed = results.Where(r => !r.Fired && !OutsideTheHit(r.Name)).ToList();
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: deliberate looks that did not fire:\n{Table(missed)}")
                .That(missed).IsEmpty();
            // The 215 px slip is outside the hit area: it never fires (it would take a wider hit).
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
        }
    }

    [StaTheory]
    [InlineData(500)]
    [InlineData(600)]
    public void MustNotFire_NoReadingPath_FiresTheButton(int buttonDwellMs)
    {
        // Before F3 42 / 39 of these 130 fired at dwell 500 (dry run 2's F-1). Mutants: M1 (no restart), M3
        // (a moved look credits at once), M7 (moved against B), and the wiring forwarding the masked
        // verdict (every sample a new look, so the fill never restarts) — red.
        foreach (var (frameUs, rate) in Rates)
        {
            var results = RunAll(MustNotFire(), frameUs, buttonDwellMs, $"must-not-fire-{rate}");
            Check.That(results).HasSize(130);
            var fired = results.Where(r => r.Fired).ToList();
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: reading paths that FIRED the button:\n{Table(fired)}")
                .That(fired).IsEmpty();
            // The button closes within linger + cap of the last real activity, and the ring follows it (the
            // stay's existing OfferClosed release): prototype max 1.8 / 2.0 s up, 1.7 s held.
            var longest = results.MaxBy(r => r.UpMs)!;
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: {longest}").That(longest.UpMs).IsLessOrEqualThan(2050);
            var held = results.MaxBy(r => r.RingHeldMs)!;
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: {held}").That(held.RingHeldMs).IsLessOrEqualThan(1750);
        }
    }

    [StaFact]
    public void TheR3Paths_CloseTheButtonWithin1_8s_AndReleaseTheRing()
    {
        // Dry run 2's worst case: before F3 the button and the ring on the old word were still up at 5 s.
        // Mutants: M5 (no near-miss cap), M8 (cap from the last near miss), M4 (transit samples extend) — red.
        foreach (var (frameUs, rate) in Rates)
        {
            foreach (var r in R3Paths().Select(p => Run(p, frameUs, 500)))
            {
                Check.WithCustomMessage($"{rate}: {r}").That(r.Fired).IsFalse();
                Check.WithCustomMessage($"{rate}: {r}").That(r.UpMs).IsLessOrEqualThan(1800);
                Check.WithCustomMessage($"{rate}: {r}").That(r.RingHeldMs).IsLessOrEqualThan(1800);
            }
        }
    }

    [StaTheory]
    [InlineData(500, 10)]
    [InlineData(600, 0)]
    public void Watch_TheResidualRisk_StaysWhereThePrototypeMeasuredIt(int buttonDwellMs, int atMost)
    {
        // Not a must-not-fire set: a single half-second look at a word inside the button's area is the
        // same as a look at the button (reading-proof §2). The prototype: 10 / 48 at dwell 500 (6 of the
        // 500 ms words, 4 refixation pairs), 0 / 48 at 600 — the owner's question. A ratchet, not a goal.
        foreach (var (frameUs, rate) in Rates)
        {
            var results = RunAll(Watch(), frameUs, buttonDwellMs, $"watch-{rate}");
            Check.That(results).HasSize(48);
            var fired = results.Where(r => r.Fired).ToList();
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: {fired.Count} / 48 fired:\n{Table(fired)}")
                .That(fired.Count).IsLessOrEqualThan(atMost);
            Check.WithCustomMessage($"{rate}, dwell {buttonDwellMs}: 400 ms reading and 120 px words never fire:\n{Table(fired)}")
                .That(fired.Where(r => r.Name.Contains("400ms", StringComparison.Ordinal) || r.Name.Contains("120px", StringComparison.Ordinal)))
                .IsEmpty();
        }
    }
}
