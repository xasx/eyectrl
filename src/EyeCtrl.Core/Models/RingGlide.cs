namespace EyeCtrl.Core.Models;

/// <summary>
/// The pin edge's glide: a pure, hand-rolled easing of the drawn ring from where it was to the settle
/// centroid over <see cref="DurationMs"/>. No WPF animation is involved — the caller writes each
/// interpolated frame's position directly on the sample clock — so a pump-less read observes the drawn
/// value and no held animation can outrank a later write.
///
/// <para>
/// The trigger is the pin edge only, never a jump size: every FreeLook saccade is a large jump and must
/// follow gaze within one sample. A glide is a short ease-in-out so the ring visibly arrives rather
/// than teleports; the release is a snap, because by then the eye has usually landed somewhere else
/// (saccadic suppression hides the jump, a trailing glide would sweep behind it).
/// </para>
///
/// <para>
/// The clock is the caller's — the sample timestamps in the ViewModel, hand-fed by tests — so the
/// interpolation is deterministic and headless-testable.
/// </para>
/// </summary>
public static class RingGlide
{
    /// <summary>How long a glide takes, in milliseconds. The user's chosen "about 120 ms".</summary>
    public const int DurationMs = 120;

    /// <summary>
    /// The drawn position <paramref name="elapsedMs"/> into a glide from <paramref name="from"/> to
    /// <paramref name="to"/>, using a smoothstep ease (monotone on the interval, zero velocity at both
    /// ends, so no abrupt start or stop). Exactly <paramref name="from"/> at or before 0 ms and exactly
    /// <paramref name="to"/> at or after <see cref="DurationMs"/>, so the glide ends on the pin.
    /// </summary>
    public static PhysicalPoint At(PhysicalPoint from, PhysicalPoint to, double elapsedMs)
    {
        if (elapsedMs <= 0) return from;
        if (elapsedMs >= DurationMs) return to;

        double t = elapsedMs / DurationMs;
        double eased = t * t * (3 - (2 * t));
        return new PhysicalPoint(
            from.X + ((to.X - from.X) * eased),
            from.Y + ((to.Y - from.Y) * eased));
    }
}
