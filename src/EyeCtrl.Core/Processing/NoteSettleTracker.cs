namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

/// <summary>
/// The Move state's settle detector (P3 D4): a small, PURE dispersion tracker — a settle is gaze
/// staying within <see cref="RadiusDip"/> DIP for <see cref="SettleMs"/> milliseconds. It is
/// deliberately NOT <see cref="FixationCentroidTracker"/>, which is velocity-based; the Move
/// state wants a "gaze came to rest" judgement, not a fixation event.
/// <para>
/// The window is anchored at its first sample: a sample farther than <see cref="RadiusDip"/>
/// from that anchor restarts the window at the new sample (so does <see cref="Invalidate"/> —
/// the caller feeds a gap or a gate-closed state as no sample plus an invalidate, which is what
/// "a gap restarts the window" means here). The window settles once its time span reaches
/// <see cref="SettleMs"/>; the centroid is the mean of the window's samples. After the caller
/// has consumed a settle it calls <see cref="Reset"/> so the next settle needs a fresh window.
/// </para>
/// <para>
/// Only VALID, non-gap, unpaused, unsuspended samples are fed (P3 D4's rule — the caller's gate
/// owns that filtering; the tracker never sees the rest, so paused and suspended samples cannot
/// advance a window by construction).
/// </para>
/// </summary>
public sealed class NoteSettleTracker
{
    /// <summary>How far the gaze may drift from the window's anchor and still count as settled, in DIP.</summary>
    public const double RadiusDip = 60.0;

    /// <summary>How long the gaze must stay inside the radius, in milliseconds.</summary>
    public const int SettleMs = 500;

    private readonly List<(PhysicalPoint Point, long TimestampUs)> _window = new();

    /// <summary>Whether the current window has settled: its span reached <see cref="SettleMs"/>
    /// with every sample inside <see cref="RadiusDip"/> of the anchor.</summary>
    public bool Settled { get; private set; }

    /// <summary>The settled window's centroid (the mean of its samples), or null while unsettled.</summary>
    public PhysicalPoint? Centroid { get; private set; }

    /// <summary>Feeds one valid, gate-open sample in DIP with its timestamp in microseconds.
    /// A sample outside <see cref="RadiusDip"/> of the window's anchor restarts the window.</summary>
    public void AddSample(PhysicalPoint dipPoint, long timestampUs)
    {
        if (_window.Count > 0 && timestampUs < _window[^1].TimestampUs)
            return; // defensive: time never flows backwards within a window

        if (_window.Count > 0 && !WithinRadius(_window[0].Point, dipPoint))
        {
            // The gaze wandered past the radius: the window restarts at this sample.
            Restart(dipPoint, timestampUs);
            return;
        }

        if (_window.Count == 0)
            Restart(dipPoint, timestampUs);
        else
            _window.Add((dipPoint, timestampUs));

        var spanUs = _window[^1].TimestampUs - _window[0].TimestampUs;
        if (spanUs >= SettleMs * MathConstants.MicrosecondsPerMillisecond)
        {
            Settled = true;
            Centroid = CentroidOf(_window);
        }
    }

    /// <summary>The no-information mark (a gap sample, or the gate closed on pause/suspension):
    /// the current window is discarded and the next sample starts fresh.</summary>
    public void Invalidate() => Restart();

    /// <summary>Clears everything, including a settle the caller has consumed.</summary>
    public void Reset()
    {
        Restart();
        Settled = false;
        Centroid = null;
    }

    private void Restart(PhysicalPoint? point = null, long timestampUs = 0)
    {
        _window.Clear();
        if (point is { } p)
            _window.Add((p, timestampUs));
        Settled = false;
        Centroid = null;
    }

    private static bool WithinRadius(PhysicalPoint anchor, PhysicalPoint p)
    {
        double dx = p.X - anchor.X;
        double dy = p.Y - anchor.Y;
        return dx * dx + dy * dy <= RadiusDip * RadiusDip;
    }

    private static PhysicalPoint CentroidOf(List<(PhysicalPoint Point, long TimestampUs)> window)
    {
        double x = 0, y = 0;
        foreach (var (p, _) in window)
        {
            x += p.X;
            y += p.Y;
        }
        return new PhysicalPoint(x / window.Count, y / window.Count);
    }
}
