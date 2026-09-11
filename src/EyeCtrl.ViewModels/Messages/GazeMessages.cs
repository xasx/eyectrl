namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>
/// Sent by GazeEngineAdapter at ~30Hz with the per-sample <see cref="GazeFrame"/>.
/// Position is in physical screen pixels (actual hardware resolution).
/// <para>
/// The frame is the single carrier so a later per-sample flag is added in one place rather than here
/// and on <c>GazeEngine.CursorMoved</c> separately; the three pass-through properties keep recipients
/// reading <c>message.Position</c> as before.
/// </para>
/// </summary>
public record GazeCursorMovedMessage(GazeFrame Frame)
{
    /// <summary>
    /// Convenience for an ordinary, freshly measured sample. Production sends the frame the engine
    /// published; this overload exists for the many call sites — tests, and anything synthesising a
    /// normal sample — for which <see cref="IsInGap"/> is unambiguously false.
    /// </summary>
    public GazeCursorMovedMessage(PhysicalPoint position, long timestampMicroseconds)
        : this(new GazeFrame(position, timestampMicroseconds, IsInGap: false)) { }

    /// <summary>Filtered gaze position in physical screen pixels.</summary>
    public PhysicalPoint Position => Frame.Position;
    /// <summary>Sample timestamp in microseconds (tracker clock).</summary>
    public long TimestampMicroseconds => Frame.TimestampMicroseconds;
    /// <summary>
    /// <c>true</c> when <see cref="Position"/> is held (blink / tracking loss) rather than measured.
    /// Recipients that accumulate dwell MUST skip their <c>DwellDetector.Update</c> for such a sample.
    /// </summary>
    public bool IsInGap => Frame.IsInGap;
}

/// <summary>
/// Sent by ExternalDwellService when gaze enters (<c>Active = true</c>) or leaves
/// (<c>false</c>) a registered external (cooperating-app) control. While active, the gaze
/// cursor ring is hidden so EyeCtrl's own dwell-progress feedback shows over the control instead.
/// </summary>
public record ExternalDwellActiveMessage(bool Active);
