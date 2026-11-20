namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// The Move state's settle tracker (P3 D4), unit-pinned at the boundaries the plan names: the
/// radius (just under, at and just over 80 DIP of the RUNNING centroid — sitting 10 S2), the
/// time window (499 ms vs 500 ms), the gap restart (a gap resets the window, exactly as it
/// always has), and the pause/suspension rule — which lives in the FEED: only valid, gate-open
/// samples ever reach <see cref="NoteSettleTracker.AddSample"/>, so the tests pin both the
/// boundary arithmetic and the discipline that an <see cref="NoteSettleTracker.Invalidate"/>
/// (what the caller feeds for a gap or a closed gate) restarts the window.
/// </summary>
public class NoteSettleTrackerTests
{
    private const long Ms = 1_000; // µs per ms

    [Fact]
    public void FreshTracker_IsNotSettled_AndHasNoCentroid()
    {
        var tracker = new NoteSettleTracker();
        Check.That(tracker.Settled).IsFalse();
        Check.That(tracker.Centroid).IsNull();
        Check.That(tracker.Anchor).IsNull();
        Check.That(tracker.Progress).IsEqualTo(0.0);
    }

    /// <summary>Sitting 9's visible settle ring reads these two: the window's anchor (where the
    /// ring is drawn) and its progress toward the 500 ms (how far the arc fills).</summary>
    [Fact]
    public void Progress_TracksTheWindowSpan_UpToTheSettle()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        Check.That(tracker.Progress).IsEqualTo(0.0);

        tracker.AddSample(new PhysicalPoint(101, 100), 250 * Ms);
        Check.WithCustomMessage("half the settle time is half the ring")
            .That(tracker.Progress).IsCloseTo(0.5, 1e-9);

        tracker.AddSample(new PhysicalPoint(100, 101), 400 * Ms);
        Check.That(tracker.Progress).IsCloseTo(0.8, 1e-9);

        tracker.AddSample(new PhysicalPoint(100, 100), 500 * Ms);
        Check.That(tracker.Settled).IsTrue();
        Check.WithCustomMessage("a settled window's ring is full")
            .That(tracker.Progress).IsCloseTo(1.0, 1e-9);
    }

    [Fact]
    public void Progress_StaysFull_WhileTheSettledWindowIsHeld()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(100, 100), 900 * Ms);

        Check.That(tracker.Settled).IsTrue();
        Check.WithCustomMessage("progress never overshoots the ring")
            .That(tracker.Progress).IsEqualTo(1.0);
    }

    [Fact]
    public void Anchor_IsTheWindowsRunningCentroid_AndFollowsARestart()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        Check.That(tracker.Anchor).IsEqualTo(new PhysicalPoint(100, 100));

        // √(20² + 30²) ≈ 36 DIP from the lone sample: inside the 80 DIP radius, so the sample
        // joins and the anchor becomes the running centroid (110, 115) — not the first sample.
        tracker.AddSample(new PhysicalPoint(120, 130), 100 * Ms);
        Check.WithCustomMessage("the anchor is the window's RUNNING centroid, not its first sample")
            .That(tracker.Anchor).IsEqualTo(new PhysicalPoint(110, 115));

        // √(90² + 15²) ≈ 91 DIP from (110, 115): past the radius, so the window restarts at the
        // new sample — which is alone, and so is its own anchor again.
        tracker.AddSample(new PhysicalPoint(200, 100), 200 * Ms);
        Check.That(tracker.Anchor).IsEqualTo(new PhysicalPoint(200, 100));
        Check.WithCustomMessage("a restart drops the progress to zero")
            .That(tracker.Progress).IsEqualTo(0.0);

        tracker.Invalidate();
        Check.That(tracker.Anchor).IsNull();
        Check.That(tracker.Progress).IsEqualTo(0.0);
    }

    [Fact]
    public void DispersionJustUnderTheRadius_Settles_AtTheCentroid()
    {
        var tracker = new NoteSettleTracker();
        // Jitter inside the 80 DIP radius of the running centroid: 59 DIP at the widest.
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(159, 100), 100 * Ms);
        tracker.AddSample(new PhysicalPoint(120, 150), 200 * Ms);
        tracker.AddSample(new PhysicalPoint(140, 130), 500 * Ms);

        Check.That(tracker.Settled).IsTrue();
        Check.That(tracker.Centroid).IsNotNull();
    }

    [Fact]
    public void DispersionJustOverTheRadius_RestartsTheWindow_AndDoesNotSettle()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        // 81 DIP from the (single-sample) centroid: past the radius, the window restarts HERE —
        // so no matter how long the gaze then stays, the pre-jump samples cannot count toward
        // the 500 ms.
        tracker.AddSample(new PhysicalPoint(181, 100), 100 * Ms);
        tracker.AddSample(new PhysicalPoint(181, 100), 550 * Ms); // only 450 ms since the restart

        Check.That(tracker.Settled).IsFalse();
        Check.That(tracker.Centroid).IsNull();
    }

    [Fact]
    public void AfterARadiusRestart_TheNewWindow_CanSettleOnItsOwn()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(200, 100), 100 * Ms); // 100 DIP past the radius: restart here
        tracker.AddSample(new PhysicalPoint(210, 110), 400 * Ms); // ≈ 14 DIP from (200, 100): joins
        tracker.AddSample(new PhysicalPoint(205, 105), 600 * Ms); // 500 ms past the restart

        Check.That(tracker.Settled).IsTrue();
    }

    /// <summary>Sitting 10 S2's point: the first sample was an outlier of the owner's real
    /// rest, and the first-sample rule restarted the window the moment the gaze drifted toward
    /// where it actually sat. Here the first sample sits at (0, 0) while the gaze settles in
    /// around (100, 0): every step is inside 80 DIP of the centroid built so far (the widest
    /// step is the first, 70 DIP), so the window runs to its settle — the OLD rule would have
    /// restarted at the 100 DIP step (and the 60 DIP radius at the very first step).</summary>
    [Fact]
    public void AWindowWhoseFirstSampleIsOffCentre_StillSettles_OnTheRunningCentroid()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(0, 0), 0 * Ms);
        // Drift right: 70, 70, 70 — judged against centroids 0, 35, 46.67 (all ≤ 80 DIP away).
        for (var i = 1; i <= 3; i++)
            tracker.AddSample(new PhysicalPoint(70, 0), i * 33 * Ms);
        // Cluster at (100, 0): judged against centroids 52.5, 62, …, 87.3 — every step ≤ 80 DIP,
        // though the samples sit 100 DIP from the window's FIRST sample.
        for (var i = 4; i <= 14; i++)
            tracker.AddSample(new PhysicalPoint(100, 0), i * 33 * Ms);
        Check.WithCustomMessage("462 ms is not the settle yet").That(tracker.Settled).IsFalse();

        tracker.AddSample(new PhysicalPoint(100, 0), 500 * Ms);
        Check.WithCustomMessage("the window survived its off-centre first sample and settled")
            .That(tracker.Settled).IsTrue();
        Check.WithCustomMessage("the settled centroid is the window's mean: (3·70 + 12·100) / 16")
            .That(tracker.Centroid!.Value.X).IsCloseTo(88.125, 1e-9);
        Check.That(tracker.Centroid!.Value.Y).IsCloseTo(0.0, 1e-9);
    }

    /// <summary>The radius is judged at the running centroid, pinned at all three sides of the
    /// 80 DIP boundary (the anchor asserts read whether the sample joined or restarted).</summary>
    [Fact]
    public void TheRadius_AtTheRunningCentroid_79Joins_80Joins_81Restarts()
    {
        var justUnder = new NoteSettleTracker();
        justUnder.AddSample(new PhysicalPoint(0, 0), 0 * Ms);
        justUnder.AddSample(new PhysicalPoint(79, 0), 33 * Ms);
        Check.WithCustomMessage("79 ≤ 80: the sample joined, so the anchor is the pair's centroid")
            .That(justUnder.Anchor).IsEqualTo(new PhysicalPoint(39.5, 0));

        var atTheBoundary = new NoteSettleTracker();
        atTheBoundary.AddSample(new PhysicalPoint(0, 0), 0 * Ms);
        atTheBoundary.AddSample(new PhysicalPoint(80, 0), 33 * Ms);
        Check.WithCustomMessage("exactly the radius still joins (the old ≤ holds)")
            .That(atTheBoundary.Anchor).IsEqualTo(new PhysicalPoint(40, 0));

        var justOver = new NoteSettleTracker();
        justOver.AddSample(new PhysicalPoint(0, 0), 0 * Ms);
        justOver.AddSample(new PhysicalPoint(81, 0), 33 * Ms);
        Check.WithCustomMessage("81 > 80: the window restarts at the new sample")
            .That(justOver.Anchor).IsEqualTo(new PhysicalPoint(81, 0));
        Check.That(justOver.Progress).IsEqualTo(0.0);
    }

    /// <summary>S2's negative half: once the samples have pulled the centroid to (30, 0), a
    /// sample 90 DIP from it is past the 80 DIP radius and restarts the window at itself — which
    /// then settles on its own 500 ms.</summary>
    [Fact]
    public void ASampleBeyondEightyDipOfTheCentroid_RestartsTheWindow()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(0, 0), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(60, 0), 33 * Ms); // 60 ≤ 80 of the lone sample: joins
        tracker.AddSample(new PhysicalPoint(120, 0), 66 * Ms); // 90 DIP from the centroid (30, 0)

        Check.WithCustomMessage("the window restarted at the out-of-radius sample")
            .That(tracker.Anchor).IsEqualTo(new PhysicalPoint(120, 0));
        Check.That(tracker.Progress).IsEqualTo(0.0);
        Check.That(tracker.Settled).IsFalse();

        tracker.AddSample(new PhysicalPoint(120, 0), 566 * Ms); // 500 ms past the restart
        Check.That(tracker.Settled).IsTrue();
    }

    [Fact]
    public void AGapSample_Invalidate_RestartsTheWindow()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(101, 100), 300 * Ms);

        // A gap (or a closed gate) arrives: the window is discarded — the 300 ms already spent
        // must not count toward the next settle. Sitting 10: the gap rule is unchanged.
        tracker.Invalidate();
        Check.That(tracker.Settled).IsFalse();

        tracker.AddSample(new PhysicalPoint(100, 100), 400 * Ms);
        tracker.AddSample(new PhysicalPoint(100, 100), 700 * Ms); // only 300 ms since the restart

        Check.That(tracker.Settled).IsFalse();
        Check.That(tracker.Centroid).IsNull();
    }

    [Fact]
    public void Reset_ClearsAConsumedSettle_SoANextOneNeedsAFullWindow()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(100, 100), 500 * Ms);
        Check.That(tracker.Settled).IsTrue();

        tracker.Reset();

        Check.That(tracker.Settled).IsFalse();
        Check.That(tracker.Centroid).IsNull();

        // Held gaze continues: the new window starts from zero — it cannot settle until a full
        // 500 ms of NEW samples has passed.
        tracker.AddSample(new PhysicalPoint(100, 100), 600 * Ms);
        Check.That(tracker.Settled).IsFalse();
        tracker.AddSample(new PhysicalPoint(100, 100), 1_100 * Ms);
        Check.That(tracker.Settled).IsTrue();
    }

    /// <summary>The pause/suspension half of P3 D4's rule, pinned at the tracker's own seam: a
    /// closed gate is fed as <see cref="NoteSettleTracker.Invalidate"/> plus nothing — so samples
    /// "before" the pause can never complete a settle across it. This is the same contract the
    /// manager's Receive obeys (gap → Invalidate; paused/suspended → Invalidate; recovery → the
    /// fresh window starts with the first clear sample).</summary>
    [Fact]
    public void AGateClosedMidWindow_DiscardsTheProgress_AcrossTheClosure()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(101, 100), 450 * Ms); // nearly settled

        tracker.Invalidate(); // the gate closes (paused / suspender / gap)

        tracker.AddSample(new PhysicalPoint(101, 100), 5_000 * Ms); // gate reopens: fresh window
        Check.That(tracker.Settled).IsFalse();
        tracker.AddSample(new PhysicalPoint(100, 100), 5_500 * Ms);
        Check.That(tracker.Settled).IsTrue();
    }
}
