namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Emission thresholds shared by every dwell surface. These must be identical across surfaces:
/// Phase-1b scoring compares abandonment rates *between* contexts, so a surface that emitted at a
/// different progress cut-off would look systematically better or worse than its peers for a reason
/// that has nothing to do with the user.
/// </summary>
public static class TelemetryThresholds
{
    /// <summary>
    /// Minimum dwell fraction [0..1] an abandoned target must have reached to be reported. Below
    /// this, gaze merely passed over the target — not an attempt the user gave up on.
    /// </summary>
    public const double AbandonProgress = 0.3;

    /// <summary>
    /// Backward timestamp jump (microseconds) beyond which a sink treats the gaze clock as having
    /// restarted rather than as an out-of-order delivery. The tracker clock is monotonic with an
    /// arbitrary epoch (see <c>TobiiStreamEngineProvider</c>), so a device reconnect can move it
    /// backwards; without a resync the monotonic drop-filter would discard every later event for the
    /// rest of the session. Well above any plausible cross-thread marshaling skew (milliseconds).
    /// </summary>
    public const long ClockRestartMicroseconds = 1_000_000; // 1 s
}
