namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;

/// <summary>
/// The gaze-off clock behind C1's un-yield (#244, owner decision 24.10): the keyboard may move
/// back toward its docked edge once the toolbar that forced the yield has gone, but only after the
/// gaze has been continuously OUTSIDE the area the keyboard occupies AND is about to occupy
/// (<see cref="KeepClearRect"/>) for <see cref="OffThresholdUs"/>. A sample inside either rect
/// resets the run, and a silence longer than <see cref="MaxContinuousSampleGapUs"/> restarts it: a
/// missing sample is not evidence that the gaze is away.
/// </summary>
internal sealed class KeyboardUnYieldGate
{
    // The owner's choice (24.10), not tuned on hardware: long enough that a glance off the
    // keyboard is not a move, short enough that the keyboard returns promptly once the gaze
    // settles away. Deliberately not a setting.
    internal const long OffThresholdUs = 300 * MathConstants.MicrosecondsPerMillisecond;

    // The same bound as KeyboardViewModel.MaxContinuousSampleGapUs, which credits a stall of
    // exactly 150 ms as held gaze and restarts beyond it. A blink or tracker dropout must not
    // count as "away", so the off-run restarts rather than covering the silence.
    internal const long MaxContinuousSampleGapUs = 150 * MathConstants.MicrosecondsPerMillisecond;

    // When the current unbroken outside-run began; long.MinValue means "no run".
    private long _offSinceUs = long.MinValue;
    private long _lastSampleUs = long.MinValue;

    /// <summary>
    /// The area the gaze must stay out of before the keyboard may un-yield: the window's current
    /// rect UNION the rect it is about to occupy. The move is vertical and goes toward the docked
    /// edge by exactly the yield it gives back, so extending the current rect toward that edge is
    /// the union. Review 01 F1: the vacated band — the strip the toolbar just left — lies outside
    /// the CURRENT rect but inside the destination; gating on the current rect alone let the
    /// keyboard land on a resting gaze there, and the landing fired a key 462 ms later.
    /// </summary>
    /// <param name="currentRectPx">The window's on-screen rect in physical px.</param>
    /// <param name="dockEdge">The keyboard's dock edge ("top" or "bottom", any case).</param>
    /// <param name="appliedYieldDip">The yield the window currently has applied, in DIP.</param>
    /// <param name="currentYieldDip">The yield the toolbar state now asks for, in DIP.</param>
    /// <param name="pixelsPerDip">The window's physical px per DIP (2.0 at 200 %).</param>
    internal static PhysicalRect KeepClearRect(
        PhysicalRect currentRectPx, string dockEdge, double appliedYieldDip, double currentYieldDip,
        double pixelsPerDip)
    {
        // Only a SMALLER current yield is an un-yield (Place's guard): an equal or larger one
        // leaves the window where it is, so the window rect is already the whole story.
        var giveBackDip = Math.Abs(appliedYieldDip) - Math.Abs(currentYieldDip);
        if (giveBackDip <= 0) return currentRectPx;

        var giveBackPx = giveBackDip * pixelsPerDip;
        return string.Equals(dockEdge, "top", StringComparison.OrdinalIgnoreCase)
            ? new PhysicalRect(currentRectPx.X, currentRectPx.Y - giveBackPx,
                currentRectPx.Width, currentRectPx.Height + giveBackPx)
            : new PhysicalRect(currentRectPx.X, currentRectPx.Y,
                currentRectPx.Width, currentRectPx.Height + giveBackPx);
    }

    /// <summary>
    /// The window's physical px per DIP, from the rect <c>PointToScreen</c> produced over the
    /// window's DIP height. Deriving it from the rect (rather than a second DPI source) guarantees
    /// the DIP yield is converted by the SAME transform chain the rect came from — 2.0 at 200 %.
    /// The caller only calls this once it knows <paramref name="actualHeightDip"/> is positive.
    /// </summary>
    internal static double PixelsPerDip(PhysicalRect windowRectPx, double actualHeightDip) =>
        windowRectPx.Height / actualHeightDip;

    /// <summary>
    /// Feed one real sample (the coordinator withholds gap-held ones). True once the gaze has been
    /// continuously outside the keep-clear area for <see cref="OffThresholdUs"/>.
    /// </summary>
    internal bool Update(bool gazeOutside, long timestampUs)
    {
        if (!gazeOutside)
        {
            Reset();
            return false;
        }

        // A first sample, a silence beyond the continuous-sample bound, or a clock that went
        // backwards all start a fresh run: only a continuous stream measures "the gaze is away".
        if (_lastSampleUs == long.MinValue
            || timestampUs < _lastSampleUs
            || timestampUs - _lastSampleUs > MaxContinuousSampleGapUs)
        {
            _offSinceUs = timestampUs;
        }

        _lastSampleUs = timestampUs;
        return timestampUs - _offSinceUs >= OffThresholdUs;
    }

    /// <summary>Starts over: the next outside sample begins a fresh off-run.</summary>
    internal void Reset()
    {
        _offSinceUs = long.MinValue;
        _lastSampleUs = long.MinValue;
    }
}
