namespace EyeCtrl.Tests.TestSupport;

using System;
using System.Collections.Concurrent;
using System.Reflection;
using EyeCtrl.Core.Services;
using Xunit.v3;

/// <summary>
/// Fails the first test that STARTS with <see cref="DpiHelper"/>'s process-global scale away from
/// its 1.0 default, naming the test and the value it observed. It is the effect half of
/// <see cref="DpiHelperRestoreAttribute"/>: the restore hook makes a leak harmless, and this
/// observer makes a restore that stopped working loud — so a mutation that disables or conditions
/// the reset reddens the suite instead of silently feeding every later geometry assertion a
/// corrupted scale.
///
/// <para>
/// Why it observes in <c>Before</c> and throws in <c>After</c>, never at the point of observation.
/// A throw from <c>Before</c> aborts xUnit's pre-invoke loop, so every attribute later in the list
/// also never runs its <c>After</c> half — measured: it leaves
/// <c>CountRunningTestsAttribute</c>'s interlocked counter incremented and reddens both
/// <c>NonParallelPin</c> tests on top of the real finding. Recording in <c>Before</c> and throwing
/// from <c>After</c> keeps each attribute's Before/After pair intact, because xUnit's
/// <c>RunAfterAttributes</c> aggregates each exception and still runs the rest of the list.
/// </para>
///
/// <para>
/// The <c>After</c> half resets the scale to 1.0 before throwing, so a restore that failed once
/// does not also fail every later test on the residue. Each test that does start corrupted is
/// reported as its own failure — a class that keeps leaking reports each affected test, which is
/// what you want to see, and a one-off leak stops at its victim. The observation is keyed by the
/// test's unique id rather than instance state because one attribute instance serves every test of
/// a method and theory rows can run concurrently.
/// </para>
///
/// <para>
/// It assumes one thing of the suite, stated for the next maintainer: a test class constructor must
/// leave the scale at 1.0. A constructor that pinned a non-default base scale would be reported as
/// a victim here, and the remedy is to set that scale in the test body, which runs after
/// <c>Before</c>. No class in this repository does so today. The observer itself is pinned by
/// presence (declaration, reflection, collection) and by the mutations recorded with it; disabling
/// it silently leaves the restore protecting the suite but removes the detection. That seam is
/// deliberate — a detector for the detector would recurse; the top layer stays documented.
/// </para>
/// </summary>
public sealed class DpiHelperScaleObserverAttribute : BeforeAfterTestAttribute
{
    private static readonly ConcurrentDictionary<string, (double X, double Y)> CorruptedStarts = new();

    /// <inheritdoc/>
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        var x = DpiHelper.ScaleX;
        var y = DpiHelper.ScaleY;
        if (x == 1.0 && y == 1.0) return;

        CorruptedStarts[test.UniqueID] = (x, y);
    }

    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!CorruptedStarts.TryRemove(test.UniqueID, out var observed)) return;

        // Reset before throwing: one failed restore must not fail every later test too.
        DpiHelper.Initialize(1.0, 1.0);

        throw new InvalidOperationException(
            $"{methodUnderTest.DeclaringType?.FullName}.{methodUnderTest.Name} started with "
            + $"DpiHelper at ({observed.X}, {observed.Y}) instead of (1, 1), so the per-test restore "
            + "did not run for a previous test and this test executed on a corrupted scale — any "
            + "geometry it asserts is suspect. Check DpiHelperRestoreAttribute.After: it must reset "
            + "1.0 unconditionally, for every test. If this is the false positive for a class whose "
            + "constructor sets a non-default scale, move that pin into the test body.");
    }
}
