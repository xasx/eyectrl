namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class DwellTargetUpdateTests
{
    [Fact]
    public void UpdateTargetGeometry_EnlargingMidDwell_PreservesProgress()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(100, 100), 0);        // enter
        d.Update(new PhysicalPoint(100, 100), 150_000);  // 50% progress
        d.UpdateTargetGeometry("t", 60);                 // enlarge radius; must preserve EnteredTimestamp
        d.Update(new PhysicalPoint(100, 100), 300_000);  // reaches 300 ms → activates (progress not reset)

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_GrowsTheCircularHitArea()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 100));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        // (140,100) is 40 px out — outside the base radius of 30.
        d.Update(new PhysicalPoint(140, 100), 0);
        d.Update(new PhysicalPoint(140, 100), 200_000);
        Check.That(activated).IsFalse();

        d.UpdateTargetGeometry("t", 20); // radius 30 → 50, now covers the point
        d.Update(new PhysicalPoint(140, 100), 300_000);
        d.Update(new PhysicalPoint(140, 100), 500_000);

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_GrowsRectangularBounds_NotJustTheRadius()
    {
        // Regression: HitTest ignores Radius whenever Bounds is set, so a radius-only update was a
        // silent no-op for every rect target (ExternalDwellService registers rects). The Phase-1b
        // target-enlargement knob would have appeared to work while doing nothing on those surfaces.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("r", new PhysicalRect(100, 100, 40, 40), dwellTimeMs: 100));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        // (150,120) is 10 px right of the rect's right edge (x = 140).
        d.Update(new PhysicalPoint(150, 120), 0);
        d.Update(new PhysicalPoint(150, 120), 200_000);
        Check.That(activated).IsFalse();

        d.UpdateTargetGeometry("r", 20); // outset 20 px per side → right edge 160
        d.Update(new PhysicalPoint(150, 120), 300_000);
        d.Update(new PhysicalPoint(150, 120), 500_000);

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_IsIdempotent_NotCumulative()
    {
        // Phase 1b re-applies a computed margin every tick; compounding would inflate targets without
        // bound. The margin is always relative to the as-registered extents.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 100));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        for (int i = 0; i < 10; i++)
            d.UpdateTargetGeometry("t", 10); // radius must settle at 40, not 30 + 10*10

        // 45 px out: outside radius 40, so ten applications did not compound to 130.
        d.Update(new PhysicalPoint(145, 100), 0);
        d.Update(new PhysicalPoint(145, 100), 200_000);
        Check.That(activated).IsFalse();

        // 35 px out: inside radius 40, so the single margin did apply.
        d.Update(new PhysicalPoint(135, 100), 300_000);
        d.Update(new PhysicalPoint(135, 100), 500_000);
        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_ZeroMargin_RestoresAsRegisteredGeometry()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 100));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.UpdateTargetGeometry("t", 40); // grow to 70
        d.UpdateTargetGeometry("t", 0);  // back to the registered 30

        d.Update(new PhysicalPoint(150, 100), 0); // 50 px out — outside the restored radius
        d.Update(new PhysicalPoint(150, 100), 200_000);

        Check.That(activated).IsFalse();
    }

    [Fact]
    public void UpdateTargetGeometry_NegativeMarginBeyondSize_ClampsInsteadOfInverting()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("r", new PhysicalRect(100, 100, 40, 40), dwellTimeMs: 100));

        d.UpdateTargetGeometry("r", -500); // must not produce a negative-extent rect

        bool activated = false;
        d.TargetActivated += _ => activated = true;
        d.Update(new PhysicalPoint(120, 120), 0); // the former center
        d.Update(new PhysicalPoint(120, 120), 200_000);

        // Degenerate (zero-area) rect at the center still hit-tests its own corner point; the contract
        // being pinned is "no inversion / no crash", not a particular hit outcome.
        Check.That(activated).IsTrue();
    }

    [Fact]
    public void SetEffectiveDwellTime_ReEvaluatesWithoutResettingProgress()
    {
        // Restored from the simplification pass's deletion (UpdateTargetDwellTime_ReEvaluatesWithout-
        // ResettingProgress): shortening mid-dwell must NOT clear EnteredTimestamp — the next Update
        // re-evaluates elapsed against the NEW threshold, so 160 ms of a 100 ms dwell activates.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(100, 100), 0);        // enter (EnteredTimestamp = 0)
        d.Update(new PhysicalPoint(100, 100), 150_000);  // 150 ms of 300 ms — not yet activated
        Check.That(activated).IsFalse();

        d.SetEffectiveDwellTime(_ => 100);               // shorten to 100 ms; must NOT reset the timer
        d.Update(new PhysicalPoint(100, 100), 160_000);  // elapsed 160 ms >= new 100 ms → activates

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void SetEffectiveDwellTime_IsIdempotent_ComposesAgainstBaseline()
    {
        // The map receives the BASELINE each application, so re-applying the same map can never
        // compound (the ApplyHitAreaMargin discipline — a router may re-apply every Changed).
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 10, dwellTimeMs: 1000));
        Func<int, int> half = ms => ms / 2;

        d.SetEffectiveDwellTime(half);
        d.SetEffectiveDwellTime(half);
        d.SetEffectiveDwellTime(half);

        d.Update(new PhysicalPoint(0, 0), 0);
        d.Update(new PhysicalPoint(0, 0), 490_000);
        Check.That(d.GetProgress("t") > 0.9).IsTrue(); // ~490/500, not 490/125 — no compounding
    }

    [Fact]
    public void SetEffectiveDwellTime_TargetAddedAfterMap_IsBornEffective()
    {
        // Surfaces recreate targets per show; a target born while a fill is held must never serve the
        // raw baseline (spec §5 "surfaces read effective values at (re)creation").
        var d = new DwellDetector();
        d.SetEffectiveDwellTime(ms => ms / 2);
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 10, dwellTimeMs: 1000));

        d.Update(new PhysicalPoint(0, 0), 0);
        d.Update(new PhysicalPoint(0, 0), 450_000);
        bool activated = false;
        d.TargetActivated += _ => activated = true;
        d.Update(new PhysicalPoint(0, 0), 500_000); // 500 ms >= effective 500 ms (not the 1000 baseline)

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void SetEffectiveDwellTime_NullMap_RestoresExactBaseline()
    {
        var d = new DwellDetector();
        var t = new DwellTarget("t", new PhysicalPoint(0, 0), radius: 10, dwellTimeMs: 1000);
        d.AddTarget(t);
        d.SetEffectiveDwellTime(ms => ms / 2);

        d.SetEffectiveDwellTime(null);

        d.Update(new PhysicalPoint(0, 0), 0);
        d.Update(new PhysicalPoint(0, 0), 600_000); // 600 < 1000 baseline → still inside, not activated
        Check.That(d.GetProgress("t")).IsCloseTo(0.6, 0.01);
    }

    [Fact]
    public void SetEffectiveGeometry_EnlargeMidDwell_PreservesProgress()
    {
        // §7.3/C4 enlarge: the grown hit area admits a gaze point only the margin covers while the
        // accumulation already in progress continues — 35 px is outside the base 30, inside the
        // effective 45, and progress must still run against the ORIGINAL EnteredTimestamp.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(120, 100), 0);        // 20 px — inside the base 30: enter
        d.Update(new PhysicalPoint(120, 100), 150_000);  // half done
        Check.That(d.GetProgress("t")).IsCloseTo(0.5, 0.01);

        d.SetEffectiveGeometry(ext => ext * 1.5);        // 30 → 45
        d.Update(new PhysicalPoint(135, 100), 290_000);  // 35 px — margin-only point; progress continues
        Check.That(activated).IsFalse();
        Check.That(d.GetProgress("t")).IsCloseTo(290.0 / 300.0, 0.01);

        d.Update(new PhysicalPoint(135, 100), 300_000);
        Check.That(activated).IsTrue();
    }

    [Fact]
    public void SetEffectiveGeometry_IsIdempotent_ComposesAgainstBaseExtent()
    {
        // The map must receive the BASE extent each application (never the live Radius), so
        // re-applying can never compound. The recorded arguments pin the contract directly: an
        // additive map alone cannot distinguish base from live input — under the live-extent
        // mutation the margin is still +10 and ApplyHitAreaMargin recomposes against _baseRadius,
        // so Radius settles at 50 either way (round-1 finding 3). The arguments can't lie.
        var d = new DwellDetector();
        var t = new DwellTarget("t", new PhysicalPoint(0, 0), radius: 40, dwellTimeMs: 1000);
        d.AddTarget(t);
        var seen = new double[3];
        int i = 0;
        Func<double, double> grow = ext => { seen[i++] = ext; return ext + 10; };

        d.SetEffectiveGeometry(grow);
        d.SetEffectiveGeometry(grow);
        d.SetEffectiveGeometry(grow);

        Check.That(seen).ContainsExactly(40.0, 40.0, 40.0); // the BASE each time, not the live 50
        Check.That(t.Radius).IsEqualTo(50.0); // 40 + 10, not 40 + 30
    }

    [Fact]
    public void SetEffectiveGeometry_TargetAddedAfterMap_IsBornEffective()
    {
        // Surfaces recreate targets per show; a target born while a fill is held must never serve
        // the as-registered geometry (spec §5 "surfaces read effective values at (re)creation").
        var d = new DwellDetector();
        d.SetEffectiveGeometry(ext => ext * 2); // 30 → 60
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 30, dwellTimeMs: 1000));

        d.Update(new PhysicalPoint(50, 0), 0);      // 50 > 30 base, < 60 effective — enters immediately
        d.Update(new PhysicalPoint(50, 0), 999_000);
        Check.That(d.GetProgress("t")).IsCloseTo(0.999, 0.001);
    }

    [Fact]
    public void SetEffectiveGeometry_NullMap_RestoresAsRegisteredGeometry()
    {
        var d = new DwellDetector();
        var t = new DwellTarget("t", new PhysicalPoint(0, 0), radius: 30, dwellTimeMs: 1000);
        d.AddTarget(t);
        d.SetEffectiveGeometry(ext => ext * 2);

        d.SetEffectiveGeometry(null);

        Check.That(t.Radius).IsEqualTo(30.0);
        d.Update(new PhysicalPoint(50, 0), 0); // outside the restored 30 — never enters
        d.Update(new PhysicalPoint(50, 0), 500_000);
        Check.That(d.GetProgress("t")).IsEqualTo(0.0);
    }

    [Fact]
    public void SetEffectiveGeometry_RectTarget_GrowsUniformly_FromTheMinHalfExtent()
    {
        // §13.3 unit mismatch, rect side: the reference extent is the SMALLER half-extent (gaze
        // error is isotropic; the narrow dimension binds), and the margin is uniform per side.
        var d = new DwellDetector();
        var r = new DwellTarget("r", new PhysicalRect(0, 0, 40, 80), dwellTimeMs: 1000);
        d.AddTarget(r);
        Check.That(r.BaseHitExtent).IsEqualTo(20.0); // min(20, 40)

        d.SetEffectiveGeometry(ext => ext * 1.5); // reference 20 → 30 ⇒ margin +10 on every side

        Check.That(r.Bounds).IsNotNull();
        var b = r.Bounds!.Value; // PhysicalRect is a readonly record struct — unwrap the Nullable once
        Check.That(b.X).IsCloseTo(-10.0, 0.001);
        Check.That(b.Y).IsCloseTo(-10.0, 0.001);
        Check.That(b.Width).IsCloseTo(60.0, 0.001);
        Check.That(b.Height).IsCloseTo(100.0, 0.001);
        Check.That(r.HitTest(new PhysicalPoint(45, 40))).IsTrue();   // outside base width, inside grown
        Check.That(r.HitTest(new PhysicalPoint(105, 40))).IsFalse(); // beyond grown
    }

    [Fact]
    public void SetEffectiveGeometry_ShrinkOut_MidDwell_EndsAsOrdinaryGazeLeft()
    {
        // §7.3/C4 shrink (recovery ramps the fill down): a gaze point that lived only in the margin
        // falls outside on restore and the dwell ends as a normal gaze-left — no activation, and
        // the accumulation is discarded (progress 0), never frozen.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 30, dwellTimeMs: 1000));
        d.SetEffectiveGeometry(ext => ext * 2); // 60
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(50, 0), 0);       // margin-only point: inside 60, outside base 30
        d.Update(new PhysicalPoint(50, 0), 500_000); // half done
        Check.That(d.GetProgress("t")).IsCloseTo(0.5, 0.01);

        d.SetEffectiveGeometry(null);               // restore: gaze now outside
        d.Update(new PhysicalPoint(50, 0), 600_000);

        Check.That(activated).IsFalse();
        Check.That(d.GetProgress("t")).IsEqualTo(0.0); // gaze-left reset, not a frozen 0.6
    }

    [Fact]
    public void RebaseTarget_NewRect_ResetsBaseExtents_LaterIdentityMapKeepsTheNewSize()
    {
        // Round-1 blocker, pinned at the Core seam: ExternalDwellService's UpdateMessage rebases a
        // live rect target; a LATER map application — including the null/disabled margin-0 restore,
        // which recomputes Bounds from the base half-extents — must recompute from the NEW base,
        // not revert the SDK-pushed size to the stale one.
        var d = new DwellDetector();
        var r = new DwellTarget("r", new PhysicalRect(0, 0, 40, 80), dwellTimeMs: 1000);
        d.AddTarget(r);

        d.RebaseTarget("r", new PhysicalRect(0, 0, 400, 100));

        d.SetEffectiveGeometry(null); // the disabled/cleared restore path — margin 0
        Check.That(r.Bounds).IsNotNull();
        var b = r.Bounds!.Value; // PhysicalRect is a readonly record struct — unwrap the Nullable once
        Check.That(b.Width).IsCloseTo(400.0, 0.001);
        Check.That(b.Height).IsCloseTo(100.0, 0.001);
    }

    [Fact]
    public void RebaseTarget_WhileAMarginIsHeld_ReappliesItOnTheNewBase()
    {
        var d = new DwellDetector();
        var r = new DwellTarget("r", new PhysicalRect(0, 0, 40, 80), dwellTimeMs: 1000);
        d.AddTarget(r);
        d.SetEffectiveGeometry(ext => ext + 10); // min half-extent 20 ⇒ margin 10 per side

        d.RebaseTarget("r", new PhysicalRect(0, 0, 400, 100)); // new min half-extent 50

        Check.That(r.Bounds).IsNotNull();
        var b = r.Bounds!.Value;
        Check.That(b.Width).IsCloseTo(420.0, 0.001);  // 400 + 2×10 — the margin rides the NEW base
        Check.That(b.Height).IsCloseTo(120.0, 0.001); // 100 + 2×10
    }
}
