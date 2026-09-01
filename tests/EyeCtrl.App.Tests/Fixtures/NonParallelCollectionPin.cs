namespace EyeCtrl.App.Tests.Fixtures;

using System;
using System.Threading;
using NFluent;
using Xunit;

/// <summary>
/// Counts the witness-instrumented tests executing at this instant. Only the classes in this
/// file take part, so the count is a LOWER bound on the assembly's real concurrency — which is
/// exactly what the pin below needs. An overlap this witness fails to see can only make the
/// assertion pass, never fail it, so the pin cannot flake under load.
/// </summary>
internal sealed class ConcurrencyWitness : IDisposable
{
    private static int _active;

    /// <summary>How many witness-holding tests are running right now.</summary>
    internal static int Active => Volatile.Read(ref _active);

    /// <summary>Enters the counted region.</summary>
    internal ConcurrencyWitness() => Interlocked.Increment(ref _active);

    /// <summary>Leaves the counted region.</summary>
    public void Dispose() => Interlocked.Decrement(ref _active);
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
/// How it discriminates. Four free (parallel) classes hold a witness open for
/// <see cref="BackgroundHoldMs"/>, so the parallel phase always has instrumented work in flight.
/// Two separate non-parallel collections then each sample the witness while holding it. If a
/// non-parallel collection ever overlapped the parallel phase, the background holders would
/// still be counted and the sample would exceed one. If the two non-parallel collections ever
/// overlapped each other, each would count the other. Both directions therefore fail loudly.
/// </para>
///
/// <para>
/// Cost. The background holders are ordinary parallel tests running alongside ~700 others, so
/// they add no measurable wall clock; only the two sampling windows land in the serialized
/// phase, at roughly 300 ms each.
/// </para>
/// </summary>
internal static class NonParallelCollectionPin
{
    /// <summary>
    /// How long each free class keeps a witness open. Must comfortably exceed the sampling
    /// window below, so a scheduler that overlapped the phases would still have background work
    /// in flight when a sample is taken.
    /// </summary>
    internal const int BackgroundHoldMs = 2000;

    private const int SampleCount = 10;
    private const int SampleIntervalMs = 30;

    /// <summary>
    /// Holds a witness open for <see cref="BackgroundHoldMs"/> so the parallel phase has
    /// instrumented work the pin can detect.
    /// </summary>
    internal static void HoldOpen()
    {
        using var witness = new ConcurrencyWitness();
        Thread.Sleep(BackgroundHoldMs);
    }

    /// <summary>
    /// Asserts nothing else instrumented runs while the caller does. Samples repeatedly rather
    /// than once, so a partial overlap is caught and not only a start-aligned one.
    /// </summary>
    /// <param name="collectionName">The calling collection, named in the failure message.</param>
    internal static void AssertRunsExclusively(string collectionName)
    {
        using var witness = new ConcurrencyWitness();

        var peak = 0;
        for (var i = 0; i < SampleCount; i++)
        {
            peak = Math.Max(peak, ConcurrencyWitness.Active);
            Thread.Sleep(SampleIntervalMs);
        }

        Check.WithCustomMessage(
                $"Collection '{collectionName}' is declared DisableParallelization = true but "
                + $"observed {peak} instrumented tests running at once, so xUnit no longer grants "
                + "non-parallel collections an exclusive phase. Every DpiHelper collection in this "
                + "repo depends on that guarantee: the writers calling DpiHelper.Initialize sit in "
                + "such collections, and nothing else isolates the process-global scale from "
                + "concurrent readers. Do not silence this — re-establish isolation first (see "
                + "DpiHelperWriterIsolationTests for the invariant it protects).")
            .That(peak)
            .IsEqualTo(1);
    }
}

/// <summary>Free (parallel) holder: keeps instrumented work in flight during the parallel phase.</summary>
public class NonParallelPinBackgroundOneTests
{
    /// <summary>Holds a witness open; asserts nothing itself.</summary>
    [Fact]
    public void HoldsTheParallelPhaseOpen() => NonParallelCollectionPin.HoldOpen();
}

/// <summary>Free (parallel) holder: keeps instrumented work in flight during the parallel phase.</summary>
public class NonParallelPinBackgroundTwoTests
{
    /// <summary>Holds a witness open; asserts nothing itself.</summary>
    [Fact]
    public void HoldsTheParallelPhaseOpen() => NonParallelCollectionPin.HoldOpen();
}

/// <summary>Free (parallel) holder: keeps instrumented work in flight during the parallel phase.</summary>
public class NonParallelPinBackgroundThreeTests
{
    /// <summary>Holds a witness open; asserts nothing itself.</summary>
    [Fact]
    public void HoldsTheParallelPhaseOpen() => NonParallelCollectionPin.HoldOpen();
}

/// <summary>Free (parallel) holder: keeps instrumented work in flight during the parallel phase.</summary>
public class NonParallelPinBackgroundFourTests
{
    /// <summary>Holds a witness open; asserts nothing itself.</summary>
    [Fact]
    public void HoldsTheParallelPhaseOpen() => NonParallelCollectionPin.HoldOpen();
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
    /// <summary>Nothing else instrumented may run alongside this collection.</summary>
    [Fact]
    public void NonParallelCollection_RunsWithNothingElse()
        => NonParallelCollectionPin.AssertRunsExclusively("NonParallelPin.First");
}

/// <summary>Asserts the second non-parallel collection gets an exclusive phase.</summary>
[Collection("NonParallelPin.Second")]
public class NonParallelPinSecondTests
{
    /// <summary>Nothing else instrumented may run alongside this collection.</summary>
    [Fact]
    public void NonParallelCollection_RunsWithNothingElse()
        => NonParallelCollectionPin.AssertRunsExclusively("NonParallelPin.Second");
}
