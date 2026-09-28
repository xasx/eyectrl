namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

/// <summary>
/// The pin edge's glide easing (ticket 7). The ViewModel drives this per sample frame; the pure
/// function is pinned here so the monotonicity and the exact endpoints cannot drift under a WPF
/// animation's replacement.
/// </summary>
public class RingGlideTests
{
    private static readonly PhysicalPoint From = new(100, 200);
    private static readonly PhysicalPoint To = new(400, 600);

    [Fact]
    public void At_ClampsToTheEndpoints()
    {
        // Mutation: clamp only one end (e.g. drop the elapsed >= DurationMs branch) → the final frame
        // overshoots the pin instead of landing exactly on it.
        Check.That(RingGlide.At(From, To, -1)).IsEqualTo(From);
        Check.That(RingGlide.At(From, To, 0)).IsEqualTo(From);
        Check.That(RingGlide.At(From, To, RingGlide.DurationMs)).IsEqualTo(To);
        Check.That(RingGlide.At(From, To, RingGlide.DurationMs + 500)).IsEqualTo(To);
    }

    [Fact]
    public void At_IsStrictlyMonotoneTowardTheTarget()
    {
        // Mutation: a non-monotone ease (e.g. a sine with a back-swing, or overshoot easing) → some
        // frame moves backwards and this reds.
        double previousX = From.X;
        for (double t = 10; t < RingGlide.DurationMs; t += 10)
        {
            var p = RingGlide.At(From, To, t);
            Check.WithCustomMessage($"frame at {t} ms: {p.X} must advance past {previousX}")
                 .That(p.X).IsStrictlyGreaterThan(previousX);
            Check.WithCustomMessage($"frame at {t} ms: {p.X} must stay short of the pin")
                 .That(p.X).IsStrictlyLessThan(To.X);
            previousX = p.X;
        }
    }

    [Fact]
    public void At_IsEvenAndSharedAcrossBothAxes()
    {
        // The same eased fraction applies to X and Y: a per-axis divergence would bend the glide
        // instead of moving it straight to the pin.
        foreach (double t in new[] { 20.0, 40.0, 80.0, 110.0 })
        {
            var p = RingGlide.At(From, To, t);
            double fractionX = (p.X - From.X) / (To.X - From.X);
            double fractionY = (p.Y - From.Y) / (To.Y - From.Y);
            Check.That(fractionX).IsCloseTo(fractionY, 1e-12);
        }
    }

    [Fact]
    public void At_StartsSlow_SoTheGlideDoesNotLurch()
    {
        // Smoothstep at the quarter point is 0.15625: less than the linear 0.25 means an eased start.
        // Mutation: linear interpolation → the quarter-point fraction becomes 0.25 and this reds.
        var quarter = RingGlide.At(From, To, RingGlide.DurationMs / 4.0);
        double fraction = (quarter.X - From.X) / (To.X - From.X);
        Check.That(fraction).IsEqualTo(0.15625);
    }
}
