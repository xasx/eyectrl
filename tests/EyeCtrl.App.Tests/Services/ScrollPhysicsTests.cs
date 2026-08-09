namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using NFluent;
using Xunit;

public class ScrollPhysicsTests
{
    [Fact]
    public void Tick_Stationary_ProducesNoDelta()
    {
        double vx = 0, vy = 0, rx = 0, ry = 0;

        ScrollPhysics.Tick(ref vx, ref vy, 0, 0, ref rx, ref ry, 16, out int dx, out int dy);

        Check.That(dx).IsEqualTo(0);
        Check.That(dy).IsEqualTo(0);
        Check.That(vx).IsEqualTo(0);
    }

    [Fact]
    public void Tick_ConvergesTowardTarget()
    {
        double vx = 0, vy = 0, rx = 0, ry = 0;

        ScrollPhysics.Tick(ref vx, ref vy, 100, 0, ref rx, ref ry, 16, out int dx, out int dy);

        // One tick moves velocity 30% toward target (0 → 30 px/s).
        Check.That(vx).IsStrictlyGreaterThan(0);
        Check.That(vx).IsStrictlyLessThan(101);
        // 30 px/s * 16ms / 1000 = 0.48px — sub-pixel, truncated to 0 this tick.
        Check.That(dx).IsEqualTo(0);
        Check.That(rx).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Tick_AfterManyTicks_EmitsScrollDelta()
    {
        double vx = 0, vy = 0, rx = 0, ry = 0;
        int totalDx = 0;

        for (int i = 0; i < 50; i++)
        {
            ScrollPhysics.Tick(ref vx, ref vy, 100, 0, ref rx, ref ry, 16, out int dx, out int dy);
            totalDx += dx;
        }

        // After convergence, velocity ~100 px/s → ~1.6 px/tick → should emit pixels.
        Check.That(totalDx).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Tick_Deadzone_SnapsToZero()
    {
        double vx = 0.005, vy = 0.005, rx = 0, ry = 0;

        ScrollPhysics.Tick(ref vx, ref vy, 0, 0, ref rx, ref ry, 16, out int dx, out int dy);

        Check.That(vx).IsEqualTo(0);
        Check.That(vy).IsEqualTo(0);
        Check.That(dx).IsEqualTo(0);
    }

    [Fact]
    public void Tick_Remainder_AccumulatesSubPixel()
    {
        double vx = 0.5, vy = 0, rx = 0, ry = 0;

        // 0.5 px/s * 16ms / 1000 = 0.008 px per tick — sub-pixel, should accumulate.
        ScrollPhysics.Tick(ref vx, ref vy, 0.5, 0, ref rx, ref ry, 16, out int dx, out int dy);

        Check.That(dx).IsEqualTo(0);
        Check.That(rx).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Tick_LerpFactor_Respected()
    {
        double vx1 = 0, vy1 = 0, rx1 = 0, ry1 = 0;
        double vx2 = 0, vy2 = 0, rx2 = 0, ry2 = 0;

        ScrollPhysics.Tick(ref vx1, ref vy1, 100, 0, ref rx1, ref ry1, 16, out _, out _, lerpFactor: 0.3);
        ScrollPhysics.Tick(ref vx2, ref vy2, 100, 0, ref rx2, ref ry2, 16, out _, out _, lerpFactor: 0.5);

        // Higher lerp factor → faster approach to target.
        Check.That(vx2).IsStrictlyGreaterThan(vx1);
    }

    [Fact]
    public void Tick_YAxis_Independent()
    {
        double vx = 0, vy = 0, rx = 0, ry = 0;

        ScrollPhysics.Tick(ref vx, ref vy, 0, 200, ref rx, ref ry, 16, out int dx, out int dy);

        // X target is 0 → vx stays 0; Y target is 200 → vy increases.
        Check.That(dx).IsEqualTo(0);
        Check.That(vx).IsEqualTo(0);
        Check.That(vy).IsStrictlyGreaterThan(0);
    }
}
