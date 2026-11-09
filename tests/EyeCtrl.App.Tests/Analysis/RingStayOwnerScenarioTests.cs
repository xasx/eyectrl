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
/// F2's stay zone through the App's chain at the owner's settings (5th sitting, 2026-11-09): a real
/// <see cref="GazeEngine"/> with the App's parsed launch arm (<c>RingArmArgument.Parse([])</c>: pin
/// RunOnly, hold HybridFreeze — never the class defaults), a real <see cref="GazeEngineAdapter"/> on the
/// current dispatcher, a real <see cref="CursorCircleViewModel"/> and <see cref="ActivationButtonViewModel"/>,
/// the dispatcher pumped after every sample, and the drawn ring recorded per sample. The scripts and seeds
/// are the ring-sticky investigation's (<c>ring-sticky/evidence/RingStickyInvestigationTests.cs.txt</c>),
/// so the per-sample rows line up with its "before" CSVs. Nothing is shown; EyeCtrl is not launched.
/// <para>
/// <c>EYECTRL_RING_STAY_OUT</c> (a directory) writes the per-sample CSVs.
/// </para>
/// </summary>
public class RingStayOwnerScenarioTests
{
    private const string OutEnvironmentVariable = "EYECTRL_RING_STAY_OUT";
    private const double OwnerScale = 2.0;
    private const long FrameUs = 33_000;
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint Word = new(2600, 700);
    private static readonly RingArmLaunch AppLaunch = RingArmArgument.Parse(Array.Empty<string>());

    /// <summary>The engine-relevant values of the owner's settings copy
    /// (<c>C:\t\gxsit\sitting-2026-11-09-settings.json</c>, sha256 851107CE…): One-Euro, R 80, onset 200,
    /// button dwell 500, show after 1000, pause grace 500, ring 200, manual selection; adaptation off.</summary>
    private static EyeCtrlSettings OwnerSettings()
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
        s.Dwell.ActivationButtonDwellMs = 500;
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

    internal sealed record Row(long Us, string Phase, PhysicalPoint Gaze, bool Valid, PhysicalPoint Ring, bool Pinned,
        bool Visible, InteractionState State, bool ButtonUp, double Dwell, string Events, PhysicalPoint Filtered);

    private sealed class Rig : IDisposable
    {
        private readonly TestFakes.FakeGazeProvider _provider = new();
        private readonly GazeEngine _engine;
        private readonly GazeEngineAdapter _adapter;
        private readonly WeakReferenceMessenger _messenger = new();
        private readonly Random _rng;
        private readonly List<string> _pending = new();
        private PhysicalPoint _drawn;
        private PhysicalPoint _filtered;

        public CursorCircleViewModel Ring { get; }
        public ActivationButtonViewModel Button { get; }
        public InteractionManager Im => _engine.InteractionManager;
        public List<Row> Rows { get; } = new();
        public long NowUs { get; private set; }
        public string Phase { get; set; } = "";

        private readonly long _frameUs;

        public Rig(int seed, long frameUs)
        {
            _rng = new Random(seed);
            _frameUs = frameUs;
            var settings = OwnerSettings();
            _engine = new GazeEngine(_provider, new TestFakes.FakeInputSimulator(), settings);
            _engine.InteractionManager.RingPinPolicy = AppLaunch.PinPolicy;              // App.xaml.cs
            _engine.ConfigureActivationOffers(Screen, OwnerScale);
            Ring = new CursorCircleViewModel(settings, _messenger) { RingHoldPolicy = AppLaunch.HoldPolicy };
            Button = new ActivationButtonViewModel(_messenger, settings);
            _adapter = new GazeEngineAdapter(_engine, settings, _messenger, Dispatcher.CurrentDispatcher,
                NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(), screenDip: () => (1920, 1200));

            Ring.PropertyChanged += OnRingChanged;
            Ring.RingStayStarted += _ => _pending.Add("StayStart");
            Ring.RingStayEnded += e => _pending.Add($"StayEnd {e.Reason}");
            Ring.RingHoldStarted += e => _pending.Add($"HoldStart {e.Kind}");
            Ring.RingHoldEnded += e => _pending.Add($"HoldEnd {e.Reason}");
            Ring.RingReleased += e => _pending.Add($"RELEASE {e.Cause}");
            Ring.RingPinProcessed += e => _pending.Add(e.Applied ? "PIN" : e.IgnoredNearOffer ? "pin near-miss" : "pin refused");
            _messenger.Register<Rig, EyeCtrl.ViewModels.Messages.GazeCursorMovedMessage>(this, (_, m) => _filtered = m.Position);
            Im.ActivationOfferShown += o => _pending.Add($"SHOWN #{o.Id}");
            Im.ActivationOfferFired += (o, _) => _pending.Add($"FIRED #{o.Id}");
            Im.ActivationOfferExpired += (o, _, _) => _pending.Add($"EXPIRED #{o.Id}");
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
            Rows.Add(new Row(NowUs, Phase, p, valid, _drawn, Ring.IsPinned, Ring.IsVisible, Im.CurrentState,
                Button.IsVisible, Im.ActivationOffers?.DwellProgress ?? 0, string.Join("; ", _pending), _filtered));
            _pending.Clear();
        }

        public void Rest(PhysicalPoint at, double ms, double sigma)
        {
            long start = NowUs + _frameUs;
            while (NowUs + _frameUs - start < ms * 1000)
                Sample(new PhysicalPoint(at.X + Gauss(sigma), at.Y + Gauss(sigma)));
        }

        public void Saccade(PhysicalPoint from, PhysicalPoint to, int midSamples)
        {
            for (int i = 1; i <= midSamples; i++)
            {
                double f = i / (double)(midSamples + 1);
                Sample(new PhysicalPoint(from.X + ((to.X - from.X) * f), from.Y + ((to.Y - from.Y) * f)));
            }
        }

        public void Blink(double ms)
        {
            long start = NowUs + _frameUs;
            while (NowUs + _frameUs - start < ms * 1000)
                Sample(new PhysicalPoint(0, 0), valid: false);
        }

        public void Read(PhysicalPoint from, int words)
        {
            for (int i = 0; i < words; i++)
                Rest(new PhysicalPoint(from.X + (150 * i), from.Y), 250, 4);
        }

        public void Dispose()
        {
            _messenger.UnregisterAll(this);
            _adapter.Dispose();
            _engine.Dispose();
            Ring.Dispose();
        }
    }

    internal sealed record Outcome(string Name, PhysicalPoint P, PhysicalPoint B, List<Row> Rows)
    {
        public bool Fired => Rows.Any(r => r.Events.Contains("FIRED", StringComparison.Ordinal));
        public int FiredAt => Rows.FindIndex(r => r.Events.Contains("FIRED", StringComparison.Ordinal));

        /// <summary>The first sample, while the button is up and the ring visible, on which the ring is more
        /// than 20 px from P; -1 when it never left.</summary>
        public int RingLeftP => Rows.FindIndex(r => r.ButtonUp && r.Visible && r.Ring.DistanceTo(P) > 20);
    }

    private static PhysicalPoint Offset(PhysicalPoint b, PhysicalPoint p, double dist, string dir)
    {
        double ux = (b.X - p.X) / b.DistanceTo(p), uy = (b.Y - p.Y) / b.DistanceTo(p);   // unit P->B
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

    /// <summary>The investigation's script: read 3 words, rest 1.2 s at the word, one mid-saccade sample
    /// toward B, then <paramref name="onButton"/>, then 400 ms far away.</summary>
    private static Outcome Run(string name, int seed, Action<Rig, PhysicalPoint, PhysicalPoint> onButton,
        long frameUs = FrameUs)
    {
        using var rig = new Rig(seed, frameUs);
        rig.Phase = "read";
        rig.Read(new PhysicalPoint(1800, 700), 3);
        rig.Phase = "rest P";
        rig.Rest(Word, 1200, 6);
        var offer = rig.Im.ActivationOffers!.CurrentOffer;
        Check.WithCustomMessage($"{name}: an offer after the 1.2 s rest").That(offer).IsNotNull();
        var p = offer!.Point;
        var b = offer.Button;
        rig.Phase = "saccade";
        rig.Saccade(Word, b, 1);
        onButton(rig, p, b);
        rig.Phase = "after";
        rig.Rest(new PhysicalPoint(1200, 1600), 400, 4);
        var outcome = new Outcome(name, p, b, rig.Rows.ToList());
        Dump(outcome);
        return outcome;
    }

    private static void Dump(Outcome o)
    {
        var dir = Environment.GetEnvironmentVariable(OutEnvironmentVariable);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder("us,phase,gazeX,gazeY,valid,gaze_to_B,filtered_to_B,ringX,ringY,ring_to_P,ring_to_B,pinned,visible,state,button,dwell,events\n");
        foreach (var r in o.Rows)
            sb.AppendLine(FormattableString.Invariant(
                $"{r.Us},{r.Phase},{r.Gaze.X:F1},{r.Gaze.Y:F1},{r.Valid},{r.Gaze.DistanceTo(o.B):F1},{r.Filtered.DistanceTo(o.B):F1},{r.Ring.X:F1},{r.Ring.Y:F1},{r.Ring.DistanceTo(o.P):F1},{r.Ring.DistanceTo(o.B):F1},{r.Pinned},{r.Visible},{r.State},{r.ButtonUp},{r.Dwell:F2},\"{r.Events}\""));
        File.WriteAllText(Path.Combine(dir, o.Name + ".csv"), sb.ToString());
    }

    private static string Trace(Outcome o, int around)
    {
        var sb = new StringBuilder($"{o.Name}: P=({o.P.X:F0},{o.P.Y:F0}) B=({o.B.X:F0},{o.B.Y:F0})\n");
        for (int k = Math.Max(0, around - 8); k <= Math.Min(o.Rows.Count - 1, around + 2); k++)
        {
            var x = o.Rows[k];
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  [{k}] {x.Phase,-12} gaze→B {x.Gaze.DistanceTo(o.B),4:F0} filt→B {x.Filtered.DistanceTo(o.B),4:F0} ring→P {x.Ring.DistanceTo(o.P),4:F0} {(x.Visible ? "vis" : "hid")} {(x.ButtonUp ? "btn" : "   ")} {x.Dwell,4:P0} {x.Events}"));
        }
        return sb.ToString();
    }

    private static void StaysAtP(Outcome o)
    {
        int left = o.RingLeftP;
        Check.WithCustomMessage("the ring left the word while the button was up:\n" + (left >= 0 ? Trace(o, left) : ""))
            .That(left).IsEqualTo(-1);
    }

    private static void FiresWithTheRingHidden(Outcome o)
    {
        Check.WithCustomMessage(o.Name + ": the button fires").That(o.Fired).IsTrue();
        // Guard 6: the fire's Hide releases the ring onto the gaze at B, and the manual selection's state
        // change hides it before the next sample: no recorded sample shows it visible after the fire.
        int shown = o.Rows.FindIndex(o.FiredAt, r => r.Visible);
        Check.WithCustomMessage("a visible ring after the fire:\n" + (shown >= 0 ? Trace(o, shown) : ""))
            .That(shown).IsEqualTo(-1);
    }

    // ── The scenarios ────────────────────────────────────────────────────────

    [StaFact]
    public void OnTheButton_WithJitter_TheRingStaysOnTheWord_AndTheButtonFires()
    {
        foreach (var sigma in new[] { 8.0, 20.0, 35.0 })
        {
            var o = Run($"base-s{sigma:0}", 1, (rig, _, b) => { rig.Phase = "on B"; rig.Rest(b, 700, sigma); });
            StaysAtP(o);
            FiresWithTheRingHidden(o);
        }
    }

    [StaFact]
    public void OnTheRim_150To270PxFromB_TheRingStays()
    {
        // Today (hit 150, keep T, hit-keyed hold exits) every landing 160-190 px snapped the ring onto the
        // button 560-660 ms into the dwell, and 220 px snapped at once. Now: the zone is hit + R = 280.
        // (Toward P, 230-270 px from B is 40-80 px from the word: a settle there re-pins by design.)
        foreach (var dir in new[] { "down", "beyond B", "side +", "side -" })
            foreach (var dist in new[] { 150.0, 190.0, 230.0, 270.0 })
            {
                var o = Run($"rim-{dist:0}-{dir.Replace(' ', '_')}", 2,
                    (rig, p, b) => { rig.Phase = "on B"; rig.Rest(Offset(b, p, dist, dir), 700, 12); });
                StaysAtP(o);
            }
    }

    [StaFact]
    public void TravellingFromPToB_InTwoSteps_TheRingStays_AndFires()
    {
        var o = Run("two-step", 6, (rig, p, b) =>
        {
            rig.Phase = "step 40%";
            rig.Rest(new PhysicalPoint(p.X + ((b.X - p.X) * 0.4), p.Y + ((b.Y - p.Y) * 0.4)), 150, 8);
            rig.Phase = "step 75%";
            rig.Rest(new PhysicalPoint(p.X + ((b.X - p.X) * 0.75), p.Y + ((b.Y - p.Y) * 0.75)), 150, 8);
            rig.Phase = "on B";
            rig.Rest(b, 700, 12);
        });
        StaysAtP(o);
        FiresWithTheRingHidden(o);
    }

    [StaFact]
    public void ABlinkOnTheButton_TheRingStays_AndFires()
    {
        foreach (var dist in new[] { 0.0, 120.0 })
        {
            var o = Run($"blink-{dist:0}", 4, (rig, p, b) =>
            {
                var at = Offset(b, p, dist, "down");
                rig.Phase = "on B";
                rig.Rest(at, 250, 12);
                rig.Phase = "blink";
                rig.Blink(150);
                rig.Phase = "on B again";
                rig.Rest(at, 600, 12);
            });
            StaysAtP(o);
            FiresWithTheRingHidden(o);
        }
    }

    [StaFact]
    public void A250MsGlanceOff_TheRingStays_AndFires()
    {
        foreach (var target in new[] { "word", "away", "side" })
        {
            var o = Run(target == "away" ? "glance-away" : $"glance-{target}", 5, (rig, p, b) =>
            {
                rig.Phase = "on B";
                rig.Rest(b, 250, 12);
                rig.Phase = "glance off";
                var g = target switch { "word" => p, "away" => Offset(b, p, 300, "beyond B"), _ => Offset(b, p, 200, "side +") };
                rig.Rest(g, 250, 8);
                rig.Phase = "on B again";
                rig.Rest(b, 600, 12);
            });
            StaysAtP(o);
            FiresWithTheRingHidden(o);
        }
    }

    [StaFact]
    public void AClearLeave_ReleasesTheRing_HalfASecondAfterTheGazeLeftTheZone()
    {
        // 250 ms on B, then clearly elsewhere (1000 px away): the ring stays for the grace, then follows.
        var o = Run("clear-leave", 5, (rig, _, b) =>
        {
            rig.Phase = "on B";
            rig.Rest(b, 250, 12);
            rig.Phase = "leave";
            rig.Rest(new PhysicalPoint(1400, 1500), 1000, 6);
        });
        double zone = LayoutConstants.ActivationButtonHitRadiusPx + 80;
        bool InZone(PhysicalPoint g) => g.DistanceTo(o.B) <= zone
            || RingArmReplayTests.DistanceToSegment(g, o.P, o.B) <= 80 * CursorCircleViewModel.HybridStayCorridorFactor;
        int leaveStart = o.Rows.FindIndex(r => r.Phase == "leave");
        int outside = o.Rows.FindIndex(leaveStart, r => !InZone(r.Filtered));
        int released = o.Rows.FindIndex(leaveStart, r => r.Events.Contains("RELEASE", StringComparison.Ordinal));
        Check.WithCustomMessage(Trace(o, Math.Max(released, leaveStart))).That(released).IsStrictlyGreaterThan(outside);
        long ms = (o.Rows[released].Us - o.Rows[outside].Us) / 1000;
        Check.WithCustomMessage($"released {ms} ms after the gaze left the zone\n" + Trace(o, released))
            .That(ms).IsGreaterOrEqualThan((long)CursorCircleViewModel.HybridStayGraceMs);
        Check.That(ms).IsStrictlyLessThan(CursorCircleViewModel.HybridStayGraceMs + (FrameUs / 1000) + 1);
        Check.That(o.Rows[released].Events).Contains("StayEnd Left");
        Check.That(o.Rows[released].Ring.DistanceTo(o.P)).IsStrictlyGreaterThan(500);   // it follows the gaze
        Check.That(o.Fired).IsFalse();
    }

    [StaFact]
    public void TheLog16Shape_AFixationFormedBesideB_ThenOntoB_FiresWithTheRingAtP()
    {
        // 5th sitting offer #16: the eye stopped 170-200 px off B (the fixation formed there), then moved
        // onto the button. Today: `HoldEnd Unproven` snapped the ring ~474 px onto the button, then froze.
        foreach (var (dist, dir) in new[] { (200.0, "down"), (190.0, "side +"), (170.0, "beyond B") })
        {
            var o = Run($"log16-{dist:0}-{dir.Replace(' ', '_')}", 8, (rig, p, b) =>
            {
                rig.Phase = "stop near B";
                rig.Rest(Offset(b, p, dist, dir), 650, 10);
                rig.Phase = "on B";
                rig.Rest(Offset(b, p, 40, dir), 700, 12);
            });
            StaysAtP(o);
            FiresWithTheRingHidden(o);
        }
    }

    [StaFact]
    public void TheLog2Shape_ASlipAt86PercentFill_NoSnap_TheFillGoesOn()
    {
        // 5th sitting offers #2/#17: 400 ms on B (86 % fill), then a slip 165-215 px below B. Today:
        // `HoldEnd Left` snapped the ring onto the button and the engine reset the fill. Now the ring stays;
        // inside the 200-px hit (165, 190) the fill goes on and fires; at 215 the button's own 500 ms pause
        // rule applies (the ring still stays).
        foreach (var slip in new[] { 165.0, 190.0, 215.0 })
        {
            var o = Run($"log2-slip{slip:0}", 10, (rig, p, b) =>
            {
                rig.Phase = "on B";
                rig.Rest(Offset(b, p, 40, "down"), 400, 10);
                rig.Phase = "slipped edge";
                rig.Rest(Offset(b, p, slip, "down"), 900, 10);
            });
            StaysAtP(o);
            if (slip < LayoutConstants.ActivationButtonHitRadiusPx)
                FiresWithTheRingHidden(o);
        }
    }

    [StaFact]
    public void Hit200_ALookLanding180PxFromB_NowFires()
    {
        // At the 150-px hit a look landing 180 px from the centre never credited the fill. Mutant: hit back
        // to 150 → no fire.
        var o = Run("land180-down", 2, (rig, p, b) => { rig.Phase = "on B"; rig.Rest(Offset(b, p, 180, "down"), 700, 8); });
        StaysAtP(o);
        FiresWithTheRingHidden(o);
    }

    [StaFact]
    public void TheInvestigationsOtherTimelines_TheRingStays()
    {
        // The ring-sticky investigation's remaining per-sample timelines (same scripts and seeds), so each of
        // its "before" CSVs has an "after" twin: heavy jitter 110 px off-centre, a 160-px landing, and a
        // 180-px landing for 300 ms before the eye moves onto the button.
        var edge = Run("edge-s35", 7, (rig, p, b) => { rig.Phase = "on B"; rig.Rest(Offset(b, p, 110, "down"), 900, 35); });
        StaysAtP(edge);
        var land160 = Run("land160-down", 2, (rig, p, b) => { rig.Phase = "on B"; rig.Rest(Offset(b, p, 160, "down"), 700, 12); });
        StaysAtP(land160);
        FiresWithTheRingHidden(land160);
        var land180 = Run("land180-down-then-B", 3, (rig, p, b) =>
        {
            rig.Phase = "land off B";
            rig.Rest(Offset(b, p, 180, "down"), 300, 12);
            rig.Phase = "on B";
            rig.Rest(Offset(b, p, 40, "down"), 700, 12);
        });
        StaysAtP(land180);
        FiresWithTheRingHidden(land180);
    }

    [StaFact]
    public void At90Hz_TheRimLandingsAndTheLog16Shape_Stay()
    {
        // The investigation's 90 Hz grid: there the 160-px landing snapped the ring 440 px onto the button
        // 599 ms into the dwell. Same scripts and seeds at 11.1 ms frames.
        foreach (var dist in new[] { 160.0, 190.0, 250.0 })
            StaysAtP(Run($"90hz-land{dist:0}-down", 2,
                (rig, p, b) => { rig.Phase = "on B"; rig.Rest(Offset(b, p, dist, "down"), 700, 12); }, frameUs: 11_111));
        var log16 = Run("90hz-log16-200-down", 8, (rig, p, b) =>
        {
            rig.Phase = "stop near B";
            rig.Rest(Offset(b, p, 200, "down"), 650, 10);
            rig.Phase = "on B";
            rig.Rest(Offset(b, p, 40, "down"), 700, 12);
        }, frameUs: 11_111);
        StaysAtP(log16);
        FiresWithTheRingHidden(log16);
    }

    [StaFact]
    public void TheButtonsPlace_AtRing200_IsUnchangedByTheBiggerHit()
    {
        // The placement floor is hit + R + 30 = 310 px from P, exactly the owner's ring-edge offset at ring
        // 200 ((100 + 55) DIP × 2.0): B does not move. (At smaller rings the floor now pushes B to 310 px.)
        var o = Run("place", 1, (rig, _, b) => { rig.Phase = "on B"; rig.Rest(b, 100, 4); });
        Check.That(o.B.DistanceTo(o.P)).IsCloseTo(310.0, 1e-6);
    }
}
