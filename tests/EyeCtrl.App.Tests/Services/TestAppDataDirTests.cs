namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;

/// <summary>
/// The sweep predicate, and the shape of what <see cref="TestAppDataDir"/> hands out.
/// <para>
/// Worth testing despite being test infrastructure, because the sweep deletes directories it does not
/// own. If its age rule were wrong it would remove an app-data folder out from under a concurrently
/// running test host, and that failure does not announce itself — it surfaces as some unrelated test
/// failing for a missing file, on another machine, intermittently.
/// </para>
/// </summary>
public class TestAppDataDirTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AFreshFolder_IsNeverStale()
    {
        // The case that matters: a folder written seconds ago belongs to a live run.
        Check.That(TestAppDataDir.IsStale(Now.AddSeconds(-1), Now)).IsFalse();
        Check.That(TestAppDataDir.IsStale(Now, Now)).IsFalse();
    }

    [Theory]
    [InlineData(1)]      // a minute into a run
    [InlineData(60)]     // an hour — a long suite, or a machine under load
    [InlineData(23 * 60)] // most of a day — a debugger paused overnight
    public void AFolderFromAPlausiblyLiveRun_IsNotStale(int minutesAgo)
    {
        // A debugged test host can sit paused for hours with its folder open, so "old" has to mean
        // far older than any run could plausibly still be going.
        Check.That(TestAppDataDir.IsStale(Now.AddMinutes(-minutesAgo), Now)).IsFalse();
    }

    [Fact]
    public void AFolderExactlyAtTheThreshold_IsNotYetStale()
    {
        // Strictly greater-than, so the boundary falls on the safe side. An off-by-one here deletes
        // folders a day old, which is exactly the age a long-running CI agent's folder might be.
        Check.That(TestAppDataDir.IsStale(Now - TestAppDataDir.StaleAfter, Now)).IsFalse();
        Check.That(TestAppDataDir.IsStale(Now - TestAppDataDir.StaleAfter - TimeSpan.FromSeconds(1), Now))
             .IsTrue();
    }

    [Fact]
    public void AFolderFromLastWeek_IsStale()
    {
        // The litter this exists to remove: left by a run whose process was killed before its exit
        // handler could fire.
        Check.That(TestAppDataDir.IsStale(Now.AddDays(-7), Now)).IsTrue();
    }

    [Fact]
    public void AFolderWithAFutureTimestamp_IsNotStale()
    {
        // Clock skew on a shared CI volume, or a restored backup. Refusing to judge it is the safe
        // reading: a future timestamp is no evidence that the folder is abandoned.
        Check.That(TestAppDataDir.IsStale(Now.AddDays(1), Now)).IsFalse();
    }

    [Fact]
    public void Create_ReturnsADistinctExistingFolder_UnderTheSharedRoot()
    {
        // The GUID is what makes parallel runs safe, so two calls must never collide — and the shared
        // root is what gives the sweep one place to look.
        var a = TestAppDataDir.Create("selftest");
        var b = TestAppDataDir.Create("selftest");

        Check.That(a).IsNotEqualTo(b);
        Check.That(Directory.Exists(a)).IsTrue();
        Check.That(Directory.Exists(b)).IsTrue();

        var root = Path.Combine(Path.GetTempPath(), "eyectrl-tests");
        Check.That(Path.GetDirectoryName(a)).IsEqualTo(root);
        Check.That(Path.GetDirectoryName(b)).IsEqualTo(root);
    }
}
