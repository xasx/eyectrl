namespace EyeCtrl.Tests.TestSupport;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

/// <summary>
/// Throwaway directories for tests that must write to disk, with cleanup that survives a test host
/// being killed.
/// <para>
/// LINKED into more than one test project rather than copied (the precedent is
/// <c>EyeCtrl.Core.Tests</c> linking <c>GazeStreamGenerator.cs</c>). The reason is
/// <see cref="StaleAfter"/>: a second copy is a second threshold, and a sweep that deletes
/// directories it does not own is the wrong place to let two numbers drift apart.
/// </para>
/// <para>
/// <b>Everything lives under one root</b> so a sweep has a single place to look and <c>%TEMP%</c>
/// gains exactly one EyeCtrl entry however many suites run. Any process that creates a directory here
/// also sweeps the whole root, so a suite that is never run again still gets its litter cleared by a
/// suite that is.
/// </para>
/// <para>
/// <b>Cleanup is in two halves because neither is sufficient alone.</b> A process-exit hook removes
/// what this run created, which covers every normal run; a stale sweep on creation removes what an
/// earlier run abandoned, because a host that is killed — a debugger stop, a CI timeout — never
/// reaches its exit handler. The second is the one that matters: it is the killed-host case that
/// accumulates silently, and it accumulated 12 folders and 286 files before anyone looked.
/// </para>
/// </summary>
internal static class TempTestDirectory
{
    /// <summary>The single parent for every directory this hands out.</summary>
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "eyectrl-tests");

    /// <summary>
    /// How old a directory must be before the sweep will remove it.
    /// <para>
    /// A DAY, not minutes, and the margin is the point. Directory names carry a GUID so parallel runs
    /// cannot collide, but a sweep is a different hazard from a collision: it deletes directories it
    /// does not own, and another test host may be midway through a run right now. A debugged session
    /// can sit paused for hours, so only directories far older than any plausible run are touched.
    /// The cost of waiting is a few kilobytes; the cost of being wrong is deleting a directory out
    /// from under a running test, which would surface as an unrelated test failing for a missing file,
    /// intermittently, on someone else's machine.
    /// </para>
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    private static readonly ConcurrentBag<string> Created = new();
    private static int _exitHookInstalled;

    /// <summary>Creates a unique directory under <see cref="Root"/> and schedules it for cleanup.</summary>
    /// <param name="label">Short name of the owning test class, so a stray directory is traceable.</param>
    public static string Create(string label)
    {
        InstallExitHook();
        SweepStale();

        // GUID retained: it is what makes concurrent runs safe, and the age-gated sweep is written so
        // that keeping it costs nothing.
        var dir = Path.Combine(Root, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Created.Add(dir);
        return dir;
    }

    /// <summary>
    /// Whether a directory last written at <paramref name="lastWriteUtc"/> is old enough to remove.
    /// <para>
    /// Extracted so the predicate is testable without the filesystem. It is the only part of the sweep
    /// that can do damage.
    /// </para>
    /// </summary>
    public static bool IsStale(DateTime lastWriteUtc, DateTime nowUtc) =>
        nowUtc - lastWriteUtc > StaleAfter;

    /// <summary>
    /// Removes directories left behind by runs that never reached their exit handler. Best-effort in
    /// every respect: one another process holds open, or has deleted since being listed, is skipped
    /// rather than reported. Littering <c>%TEMP%</c> is a nuisance; failing a suite over tidying is
    /// not a trade worth making.
    /// </summary>
    private static void SweepStale()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var now = DateTime.UtcNow;

            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                try
                {
                    if (IsStale(Directory.GetLastWriteTimeUtc(dir), now))
                        Directory.Delete(dir, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void InstallExitHook()
    {
        if (Interlocked.Exchange(ref _exitHookInstalled, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteCreated();
    }

    /// <summary>
    /// Deletes everything this process created. Recursive, because these directories are not empty —
    /// they hold whatever the test pointed at them.
    /// </summary>
    private static void DeleteCreated()
    {
        foreach (var dir in Created)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
