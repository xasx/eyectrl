namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;

public class QuickActionsSummonControllerTests
{
    private QuickActionsSummonController CreateController(
        string edge = "Left", double screenW = 1920, double screenH = 1080,
        int summonMs = 400, int dismissMs = 5000, Func<bool>? calibrationActive = null)
    {
        return new QuickActionsSummonController(
            edge, screenW, screenH, summonMs, dismissMs,
            calibrationActive: calibrationActive ?? (() => false));
    }

    // NOTE: Feed takes a GazePoint (packs X, Y, TimestampMicroseconds, IsValid),
    // matching ToolbarAutoHideController.Feed(GazePoint, long). Tests build the
    // GazePoint with a distinct wall-clock timestampUs arg.
    private static GazePoint Pt(double x, double y, long tsUs, bool valid = true)
        => new(x, y, tsUs, valid);

    [Fact]
    public void Constructor_StateIsIdle()
    {
        var c = CreateController();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_ValidGazePastLeftEdge_SummonsAfterThreshold()
    {
        var c = CreateController(edge: "Left", screenW: 1920, summonMs: 400);
        int? toggles = null;
        c.SummonRequested += () => toggles = (toggles ?? 0) + 1;

        // Gaze at (-10, 540) = past left edge, valid
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(toggles).IsNull();

        // Enough time passes
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(toggles).IsEqualTo(1);
    }

    [Fact]
    public void Feed_InvalidGaze_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        int? toggles = null;
        c.SummonRequested += () => toggles = (toggles ?? 0) + 1;

        c.Feed(Pt(-10, 540, 0, valid: false), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000, valid: false), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(toggles).IsNull();
    }

    [Fact]
    public void Feed_GazeInsideScreen_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        c.Feed(Pt(100, 540, 0), timestampUs: 0);
        c.Feed(Pt(100, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_GazePastWrongEdge_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        // Past right edge
        c.Feed(Pt(1930, 540, 0), timestampUs: 0);
        c.Feed(Pt(1930, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_DismissTimeout_FiresAfterGazeAway()
    {
        var c = CreateController(edge: "Left", summonMs: 400, dismissMs: 5000);
        int? dismissals = 0;
        int? summons = 0;
        c.SummonRequested += () => summons++;
        c.DismissRequested += () => dismissals++;

        // Summon
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(summons).IsEqualTo(1);

        // Gaze returns inside — after dismiss timeout, controller fires DismissRequested + goes Idle
        c.Feed(Pt(100, 540, 600_000), timestampUs: 600_000);
        c.Feed(Pt(100, 540, 6_000_000), timestampUs: 6_000_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(dismissals).IsEqualTo(1);
    }

    [Fact]
    public void Feed_BlinkDuringSummonRun_ResetsRun()
    {
        var c = CreateController(edge: "Left", summonMs: 400);

        // Start summon run
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 200_000), timestampUs: 200_000);

        // Blink (invalid)
        c.Feed(Pt(-10, 540, 250_000, valid: false), timestampUs: 250_000);

        // Resume — run should restart, not continue
        c.Feed(Pt(-10, 540, 300_000), timestampUs: 300_000);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        // Only 200ms accumulated after blink, not enough for 400ms threshold
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void SetSuspended_PreventsSummonAndForcesIdle()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        // Summon first
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        c.SetSuspended(true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);

        // Should not re-summon while suspended
        c.Feed(Pt(-10, 540, 600_000), timestampUs: 600_000);
        c.Feed(Pt(-10, 540, 1_100_000), timestampUs: 1_100_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void CalibrationActive_Gate_PreventsSummon()
    {
        bool calibrationActive = true;
        var c = CreateController(calibrationActive: () => calibrationActive);
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void EnterSummoned_SetsState()
    {
        var c = CreateController();
        c.EnterSummoned();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
    }

    [Fact]
    public void ResetToIdle_SetsState()
    {
        var c = CreateController();
        c.EnterSummoned();
        c.ResetToIdle();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void UpdateEdge_WhileSummoned_DismissesAndSwapsEdge()
    {
        var c = CreateController(edge: "Left", screenW: 1920, summonMs: 400);
        int dismissals = 0;
        c.DismissRequested += () => dismissals++;

        // Summon via left edge
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // Swap to right edge while summoned → should dismiss + fire event
        c.UpdateEdge("Right");
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(dismissals).IsEqualTo(1);

        // Left edge no longer summons (proves the swap took effect)
        c.Feed(Pt(-10, 540, 600_000), timestampUs: 600_000);
        c.Feed(Pt(-10, 540, 1_100_000), timestampUs: 1_100_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);

        // Right edge now summons
        c.Feed(Pt(1930, 540, 1_200_000), timestampUs: 1_200_000);
        c.Feed(Pt(1930, 540, 1_700_000), timestampUs: 1_700_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
    }

    [Fact]
    public void Feed_BlinkWhileSummoned_Dismisses()
    {
        var c = CreateController(edge: "Left", summonMs: 400, dismissMs: 5000);
        int dismissals = 0;
        c.DismissRequested += () => dismissals++;

        // Summon first
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // Blink (invalid sample) while summoned → immediate dismiss
        c.Feed(Pt(-10, 540, 600_000, valid: false), timestampUs: 600_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(dismissals).IsEqualTo(1);
    }
}
