namespace EyeCtrl.App.Tests.Services;

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
/// </summary>
internal static class TestAppDataDir
{
    /// <summary>
    /// Creates and returns a unique temporary folder for one test class.
    /// </summary>
    /// <param name="label">Short name of the calling test class, used in the folder name.</param>
    public static string Create(string label)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "eyectrl-tests", label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
