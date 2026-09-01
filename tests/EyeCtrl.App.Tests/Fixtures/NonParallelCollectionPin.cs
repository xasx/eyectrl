using EyeCtrl.App.Tests.Fixtures;

// Applied at assembly level so EVERY test in EyeCtrl.App.Tests is counted, which is what gives
// the pin below its coverage. xUnit v3 honours BeforeAfterTestAttribute here as well as on a
// class or method; verified against xunit.v3 3.2.2 under the VSTest runner CI uses.
[assembly: CountRunningTests]

namespace EyeCtrl.App.Tests.Fixtures;

using System;
using System.Reflection;
using System.Threading;
using NFluent;
using Xunit;
using Xunit.v3;

/// <summary>
/// Counts how many tests in this assembly are executing at this instant, by bracketing every
/// single one of them. Two interlocked increments per test; it cannot throw, which matters
/// because a fault here would land on all ~700 tests rather than on one.
/// </summary>
public sealed class CountRunningTestsAttribute : BeforeAfterTestAttribute
{
    private static int _active;

    /// <summary>Tests running right now, anywhere in this assembly.</summary>
    internal static int Active => Volatile.Read(ref _active);

    /// <inheritdoc/>
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        => Interlocked.Increment(ref _active);

    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
        => Interlocked.Decrement(ref _active);
}

/// <summary>
/// Pins the xUnit scheduling guarantee that every DpiHelper collection in this repository rests
/// on: a collection declared <c>[CollectionDefinition(..., DisableParallelization = true)]</c>
/// runs EXCLUSIVELY — never concurrently with another collection, whether that other collection
/// is parallel or also non-parallel.
///
/// <para>
/// Why this is pinned rather than assumed. The isolation of <c>DpiHelper</c>'s process-global
/// scale is a scheduling property of the test runner, not of any code in this repository, so
/// nothing else in the suite would notice if an xUnit upgrade changed it. It has also already
/// been mis-stated once: the collection comments introduced alongside these collections asserted
/// that <c>DisableParallelization</c> "only serialises within a collection" — xUnit v2 wording —
/// and inferred from that a cross-collection exposure xUnit v3 does not actually have. That
/// wrong model came within one step of motivating either a merge of four collections or an
/// injectable-DPI refactor across ~124 call sites. A test is a cheaper place to keep this fact
/// than a comment, because a test is re-checked on every run.
/// </para>
///
/// <para>
/// How it discriminates. <see cref="CountRunningTestsAttribute"/> brackets every test in the
/// assembly, so the ~700 tests of a normal run ARE the background work. Two deliberately
/// separate non-parallel collections then each assert that nothing else is running while they
/// are. If a non-parallel collection ever overlapped the parallel phase, the sample would count
/// the parallel tests; if the two ever overlapped each other, each would count the other. Both
/// directions fail loudly.
/// </para>
///
/// <para>
/// An earlier version of this pin used four free classes sleeping 2 s as the background instead.
/// That bounded detection to a 2 s window inside a ~15 s parallel phase, so a scheduler that
/// only overlapped later would have slipped through; widening the window meant sizing a sleep
/// against a phase length that varies with core count, and a sleep longer than the phase becomes
/// the critical path and costs real wall clock. Counting every test removes the window, the
/// tuning problem, and the sleeps together.
/// </para>
///
/// <para>
/// Direction of failure. A missed overlap can only make the assertion PASS, never fail it, so
/// this cannot flake under load. It is correspondingly only as strong as the run is broad: a
/// narrowly filtered run leaves nothing to collide with and the pin passes vacuously. CI runs
/// the whole assembly, which is where it earns its keep.
/// </para>
/// </summary>
internal static class NonParallelCollectionPin
{
    private const int SampleCount = 10;
    private const int SampleIntervalMs = 10;

    /// <summary>
    /// Asserts no other test in the assembly runs while the caller does. Samples across a short
    /// window rather than once, so a test starting just after entry is still caught.
    /// </summary>
    /// <param name="collectionName">The calling collection, named in the failure message.</param>
    internal static void AssertRunsExclusively(string collectionName)
    {
        var peak = 0;
        for (var i = 0; i < SampleCount; i++)
        {
            peak = Math.Max(peak, CountRunningTestsAttribute.Active);
            Thread.Sleep(SampleIntervalMs);
        }

        Check.WithCustomMessage(
                $"Collection '{collectionName}' is declared DisableParallelization = true but "
                + $"observed {peak} tests running at once, so xUnit no longer grants non-parallel "
                + "collections an exclusive phase. Every DpiHelper collection in this repo depends "
                + "on that guarantee: the writers calling DpiHelper.Initialize sit in such "
                + "collections, and nothing else isolates the process-global scale from concurrent "
                + "readers. Do not silence this — re-establish isolation first (see "
                + "DpiHelperWriterIsolationTests for the invariant it protects).")
            .That(peak)
            .IsEqualTo(1);
    }
}

/// <summary>
/// First of two deliberately separate non-parallel collections. Two are needed, not one: a
/// single collection could only show that it does not overlap the parallel phase, and the
/// cross-collection direction is the one the DpiHelper collections actually rely on.
/// </summary>
[CollectionDefinition("NonParallelPin.First", DisableParallelization = true)]
public class NonParallelPinFirstCollection
{
}

/// <summary>Second non-parallel collection; see <see cref="NonParallelPinFirstCollection"/>.</summary>
[CollectionDefinition("NonParallelPin.Second", DisableParallelization = true)]
public class NonParallelPinSecondCollection
{
}

/// <summary>Asserts the first non-parallel collection gets an exclusive phase.</summary>
[Collection("NonParallelPin.First")]
public class NonParallelPinFirstTests
{
    /// <summary>No other test in the assembly may run alongside this collection.</summary>
    [Fact]
    public void NonParallelCollection_RunsWithNothingElse()
        => NonParallelCollectionPin.AssertRunsExclusively("NonParallelPin.First");
}

/// <summary>Asserts the second non-parallel collection gets an exclusive phase.</summary>
[Collection("NonParallelPin.Second")]
public class NonParallelPinSecondTests
{
    /// <summary>No other test in the assembly may run alongside this collection.</summary>
    [Fact]
    public void NonParallelCollection_RunsWithNothingElse()
        => NonParallelCollectionPin.AssertRunsExclusively("NonParallelPin.Second");
}
