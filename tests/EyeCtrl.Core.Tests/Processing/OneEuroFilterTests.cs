namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class OneEuroFilterTests
{
    [Fact]
    public void Filter_FirstPoint_ReturnedDirectly()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        var result = filter.Filter(new GazePoint(500.0, 300.0, 0, true));
        Check.That(result.X).IsCloseTo(500.0, 0.1);
        Check.That(result.Y).IsCloseTo(300.0, 0.1);
    }

    /// <summary>
    /// Also the constraint that rules out one candidate value of <c>OneEuroFilter.JumpFloorPixels</c>.
    /// This test's step is 400 px per axis, and the innovation on its THIRD sample is 59.0 px — so any
    /// jump floor at or below 59 keeps that sample on the fast path and this test green, while a floor
    /// of 60 drops it onto the slow adaptive branch and the output no longer reaches ±5 px in ten
    /// samples. Swept one value per rebuild: passes at 15/20/25/30/40/50/55/58/59, fails at 60.
    /// <para>
    /// Worth stating plainly, because the obvious reading is backwards: the single value this test
    /// forbids is the debate's own "or a fixed ~60 px" alternative, and everything below it is left
    /// open. The shipped 15 is therefore chosen from the quiet-stream sigma — it is not forced by this
    /// test, and it is not untouchable. That matters, because the floor governs exactly one regime in
    /// practice (an estimator with nothing in it yet), and how that regime is handled is live design.
    /// </para>
    /// </summary>
    [Fact]
    public void Filter_StationaryGaze_ConvergesToTarget()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(100.0, 100.0, 0, true));

        PhysicalPoint result = new(0, 0);
        long timestamp = 33_000;
        for (int i = 0; i < 10; i++)
        {
            result = filter.Filter(new GazePoint(500.0, 500.0, timestamp, true));
            timestamp += 33_000;
        }

        Check.That(result.X >= 495.0 && result.X <= 505.0).IsTrue();
        Check.That(result.Y >= 495.0 && result.Y <= 505.0).IsTrue();
    }

    [Fact]
    public void Filter_HighSpeedMovement_LessSmoothing()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(100.0, 100.0, 0, true));

        var result = filter.Filter(new GazePoint(800.0, 800.0, 33_000, true));

        Check.That(result.X > 500.0).IsTrue();
        Check.That(result.Y > 500.0).IsTrue();
    }

    [Fact]
    public void Filter_InvalidGaze_ReturnsLastFiltered()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(200.0, 300.0, 0, true));
        var result = filter.Filter(new GazePoint(0, 0, 33_000, false));
        Check.That(result.X).IsCloseTo(200.0, 0.1);
        Check.That(result.Y).IsCloseTo(300.0, 0.1);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        filter.Reset();
        var result = filter.Filter(new GazePoint(800.0, 900.0, 100_000, true));
        Check.That(result.X).IsCloseTo(800.0, 0.1);
        Check.That(result.Y).IsCloseTo(900.0, 0.1);
    }
}
