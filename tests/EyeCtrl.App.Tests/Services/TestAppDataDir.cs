namespace EyeCtrl.App.Tests.Services;

using System.Collections.Concurrent;
using System.IO;

/// <summary>
/// A throwaway app-data folder for tests that build the real DI container.
/// <para>
/// <b>Never point a container test at the real <c>%APPDATA%\EyeCtrl</c>.</b> Resolving
/// <c>CalibrationService</c> from the container loads <c>calibration.json</c>, and that load is not
/// read-only — on a hardware-id match with a stale device name it rewrites the file. Several tests here
/// resolve it without meaning to, transitively through <c>ManualSelectionDwellService</c> /
/// <c>ExternalDwellService</c> → <c>GazeEngine</c>, so "this test does not touch calibration" is not a
/// safe assumption to make by reading the test.
/// </para>
/// <para>
/// The file at stake is the one the user's only input method depends on: a bad or missing
/// <c>calibration.json</c> breaks every dwell interaction at once. <c>BuildServiceProvider</c> takes the
/// folder as a required parameter for that reason, and this is what tests pass to it.
/// </para>
/// <para>
/// <b>Cleanup is belt and braces, because neither half is sufficient alone.</b> Folders are deleted at
/// process exit, which handles every normal run; and a stale sweep on creation catches what a crashed
/// or killed test host left behind, since a process that dies does not run its exit handler. Without
/// the second, a debugger stop or a CI timeout silently accumulates directories forever — which is how
/// twelve of them came to be sitting in <c>%TEMP%</c>.
/// </para>
/// </summary>
internal static class TestAppDataDir
{
    /// <summary>Parent of every folder this class hands out, so a sweep has one place to look.</summary>
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "eyectrl-tests");

    /// <summary>
    /// How old a folder must be before the sweep will remove it.
    /// <para>
    /// A DAY, not minutes, and the margin is the point: folder names carry a GUID so parallel runs
    /// cannot collide, but a sweep is a different hazard — it deletes folders it does not own, and
    /// another test host may be midway through a run right now. Anything within a day of being written
    /// could belong to a live process (a debugged session can sit paused for hours), so only folders
    /// far older than any plausible run are touched. The cost of waiting is a few kilobytes; the cost
    /// of being wrong is deleting the app-data folder out from under a running test.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    private static readonly ConcurrentBag<string> Created = new();
    private static int _exitHookInstalled;

    /// <summary>
    /// Creates and returns a unique temporary folder for one test class.
    /// </summary>
    /// <param name="label">Short name of the calling test class, used in the folder name.</param>
    public static string Create(string label)
    {
        InstallExitHook();
        SweepStale();

        // GUID retained: it is what makes concurrent runs safe, and the sweep is written so that
        // keeping it costs nothing.
        var dir = Path.Combine(Root, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Created.Add(dir);
        return dir;
    }

    /// <summary>
    /// Whether a folder last written at <paramref name="lastWriteUtc"/> is old enough to remove.
    /// <para>
    /// Extracted so the predicate is testable without the filesystem. It is the only part of the sweep
    /// that can do damage, and "deletes a folder a running test still owns" is not a failure any suite
    /// would report clearly — it would surface as an unrelated test failing for a missing file.
    /// </para>
    /// </summary>
    internal static bool IsStale(DateTime lastWriteUtc, DateTime nowUtc) =>
        nowUtc - lastWriteUtc > StaleAfter;

    /// <summary>
    /// Removes folders left behind by runs that never got to their exit handler. Best-effort in every
    /// respect: a folder another process is using, or has deleted since being listed, is skipped
    /// rather than reported. Littering %TEMP% is a nuisance; failing a test suite over tidying is not
    /// a trade worth making.
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
    /// Deletes everything this process created. Recursive, because the folders are not empty — each
    /// holds at least the <c>settings.json</c> the container was pointed at, and deleting only the
    /// directory entry would fail.
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
