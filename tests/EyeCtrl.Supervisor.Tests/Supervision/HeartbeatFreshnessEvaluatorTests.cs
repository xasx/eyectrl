namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class HeartbeatFreshnessEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);
    private const int HangSeconds = 20;

    [Fact]
    public void NotDead_FreshFramesAndConnected()
    {
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: Now, isConnected: true)).IsFalse();
    }

    [Fact]
    public void Dead_StaleFramesWhileConnected()
    {
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        // 21s gap > 20s threshold, still connected -> wedged poll loop.
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: Now.AddSeconds(-21), isConnected: true)).IsTrue();
    }

    [Fact]
    public void NotDead_StaleFramesButDisconnected_SoftSignalOnly()
    {
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        // Even a long gap is NOT a hard kill when disconnected — app's reconnect loop owns it.
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: Now.AddSeconds(-60), isConnected: false)).IsFalse();
    }

    [Fact]
    public void Regression_LookAway_StillProducesFrames_NotDead()
    {
        // The Tobii provider forwards INVALID samples as GazePoint(0,0,ts,false) so
        // frame arrival continues while the user looks away / blinks. Those invalid
        // samples still advance lastFrameUtc. Simulate frames arriving every 33ms:
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        var lastFrame = Now.AddMilliseconds(-33); // recent invalid-sample frame
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: lastFrame, isConnected: true)).IsFalse();
    }
}
