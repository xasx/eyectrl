namespace EyeCtrl.Supervisor.Supervision;

using System;

public sealed class HeartbeatFreshnessEvaluator
{
    private readonly int _hangThresholdSeconds;

    public HeartbeatFreshnessEvaluator(int hangThresholdSeconds)
    {
        _hangThresholdSeconds = hangThresholdSeconds;
    }

    /// <summary>
    /// True only when the SDK has STOPPED delivering frames while still reporting connected.
    /// Keys off frame ARRIVAL (valid or invalid samples both advance lastFrameUtc), so a
    /// user looking away — which still produces invalid GazePoint(0,0,ts,false) frames —
    /// does NOT trip this. A disconnected tracker is a soft signal (handled by the app's
    /// own reconnect loop) and never returns true here.
    /// </summary>
    public bool IsGazeStreamDead(DateTime nowUtc, DateTime lastFrameUtc, bool isConnected)
    {
        if (!isConnected) return false; // soft signal — not our kill to make
        return (nowUtc - lastFrameUtc).TotalSeconds > _hangThresholdSeconds;
    }
}
