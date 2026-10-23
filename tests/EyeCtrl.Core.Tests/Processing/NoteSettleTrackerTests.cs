namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// The Move state's settle tracker (P3 D4), unit-pinned at the boundaries the plan names: the
/// radius (just under and just over 60 DIP), the time window (499 ms vs 500 ms), the gap
/// restart, and the pause/suspension rule — which lives in the FEED: only valid, gate-open
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
    }

    [Fact]
    public void DispersionJustUnderTheRadius_Settles_AtTheCentroid()
    {
        var tracker = new NoteSettleTracker();
        // All samples inside a ~59 DIP span of the anchor: jitter well under the 60 DIP radius.
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
        // 61 DIP from the anchor: past the radius, the window restarts HERE — so no matter how
        // long the gaze then stays, the pre-jump samples cannot count toward the 500 ms.
        tracker.AddSample(new PhysicalPoint(161, 100), 100 * Ms);
        tracker.AddSample(new PhysicalPoint(161, 100), 550 * Ms); // only 450 ms since the restart

        Check.That(tracker.Settled).IsFalse();
        Check.That(tracker.Centroid).IsNull();
    }

    [Fact]
    public void AfterARadiusRestart_TheNewWindow_CanSettleOnItsOwn()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(161, 100), 100 * Ms); // restart at the new spot
        tracker.AddSample(new PhysicalPoint(150, 110), 400 * Ms);
        tracker.AddSample(new PhysicalPoint(155, 105), 600 * Ms); // 500 ms past the restart

        Check.That(tracker.Settled).IsTrue();
    }

    [Fact]
    public void FourHundredNinetyNineMs_DoesNotSettle()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(101, 100), 499 * Ms);

        Check.That(tracker.Settled).IsFalse();
        Check.That(tracker.Centroid).IsNull();
    }

    [Fact]
    public void FiveHundredMs_Settles_WithTheWindowMean()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(110, 100), 250 * Ms);
        tracker.AddSample(new PhysicalPoint(100, 110), 500 * Ms);

        Check.That(tracker.Settled).IsTrue();
        Check.That(tracker.Centroid).IsNotNull();
        Check.That(tracker.Centroid!.Value.X).IsCloseTo((100.0 + 110 + 100) / 3, 1e-9);
        Check.That(tracker.Centroid!.Value.Y).IsCloseTo((100.0 + 100 + 110) / 3, 1e-9);
    }

    [Fact]
    public void AGapSample_Invalidate_RestartsTheWindow()
    {
        var tracker = new NoteSettleTracker();
        tracker.AddSample(new PhysicalPoint(100, 100), 0 * Ms);
        tracker.AddSample(new PhysicalPoint(101, 100), 300 * Ms);

        // A gap (or a closed gate) arrives: the window is discarded — the 300 ms already spent
        // must not count toward the next settle.
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
