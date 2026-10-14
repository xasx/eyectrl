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
    /// Convenience for an ordinary, freshly measured sample: it sets <see cref="IsInGap"/> to
    /// <c>false</c>. It exists for the many call sites — tests, and anything synthesising a normal
    /// sample — for which that is unambiguously correct.
    /// <para>
    /// Anything relaying a sample that came FROM the engine must pass the
    /// <see cref="GazeFrame"/> it was given instead, or the gap flag is silently dropped and every
    /// dwell surface downstream starts accumulating against held positions again. That is why
    /// <c>GazeEngineAdapter</c> — the one production sender — uses the frame overload.
    /// </para>
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

    /// <summary>
    /// <c>true</c> while the interaction state is Paused. Dwell surfaces that inject OS input —
    /// keyboard, quick actions, task switcher — MUST skip their <c>DwellDetector.Update</c> for
    /// such a sample: input must not leave EyeCtrl while the user believes pause released it.
    /// The safety surfaces that deliberately stay dwellable to unpause (toolbar band, modes
    /// menu, edge bar, drag trail) opt out of the gate via
    /// <c>DwellOverlayViewModelBase.DwellActiveWhilePaused</c>.
    /// </summary>
    public bool IsPaused { get; init; }
}

/// <summary>
/// Sent by ExternalDwellService when gaze enters (<c>Active = true</c>) or leaves
/// (<c>false</c>) a registered external (cooperating-app) control. While active, the gaze
/// cursor ring is hidden so EyeCtrl's own dwell-progress feedback shows over the control instead.
/// </summary>
public record ExternalDwellActiveMessage(bool Active);

/// <summary>
/// Sent by <c>ExternalDwellService</c> whenever its registered target set changes structurally —
/// an SDK client registering, updating (moving/resizing) or unregistering a target. The D17
/// gaze-consumer inventory re-runs the preview panel's collision check on it (fix-wave F5, the
/// <c>NoteTargetsChangedMessage</c> precedent): <see cref="ExternalDwellActiveMessage"/> fires
/// only once gaze has ENTERED a target, so a client registering or moving a target under a
/// visible panel would otherwise not be re-checked until the reader was already dwelling it.
/// Sent dispatcher-marshalled, like every message the pipe service sends from its pipe threads.
/// Empty payload by design: the inventory re-reads the live rects.
/// </summary>
public record ExternalDwellTargetsChangedMessage;

/// <summary>
/// Sent by the eye-care banner when it appears (<c>Active = true</c>) and when it is dismissed or
/// auto-snoozes (<c>false</c>). The activation-offer coordinator suspends offers while it is up
/// (ticket 8 fixup, review B F3): the banner carries its own dwell targets and must not compete with
/// an activation button.
/// </summary>
public record EyeCareBannerActiveMessage(bool Active);
