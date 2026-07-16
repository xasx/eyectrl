namespace EyeCtrl.Supervisor.Fallback;

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

/// <summary>
/// Selects and launches a fallback assistive-technology (AT) app when EyeCtrl is in a
/// crash loop (FALLEN_BACK state), so the user retains eye control. Candidates are probed
/// in priority order and the first one installed is launched ("never strand the user").
/// </summary>
public static class FallbackAtLauncher
{
    /// <summary>
    /// Iterates <paramref name="candidates"/> in order, launches and returns the first whose
    /// <see cref="FallbackCandidate.IsInstalled"/> returns true. Returns null if none installed.
    /// </summary>
    public static FallbackCandidate? LaunchFirstInstalled(IEnumerable<FallbackCandidate> candidates)
    {
        foreach (var c in candidates)
        {
            if (c.IsInstalled())
            {
                c.Launch();
                return c;
            }
        }
        return null;
    }

    /// <summary>
    /// Default ordered candidates, highest priority first:
    /// 1. TD Control (Tobii Dynavox)
    /// 2. Snap Core First (Tobii Dynavox)
    /// 3. Windows built-in Eye Control — PLACEHOLDER pending Spike S1 (see note below).
    /// </summary>
    public static IEnumerable<FallbackCandidate> BuildDefaultCandidates()
    {
        yield return new FallbackCandidate(
            id: "TDControl",
            isInstalled: () => Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Tobii Dynavox\TD Control") != null,
            launch: () => LaunchFromRegistry(@"SOFTWARE\Tobii Dynavox\TD Control"));

        yield return new FallbackCandidate(
            id: "SnapCoreFirst",
            isInstalled: () => Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Tobii Dynavox\Snap Core First") != null,
            launch: () => LaunchFromRegistry(@"SOFTWARE\Tobii Dynavox\Snap Core First"));

        // TODO(spike S1): replace with the verified Windows Eye Control launch mechanism.
        // The spec's Open Question #1 notes that `ms-cortana:eyecontrol` is unreliable on
        // Win11; a reliable vector (easeofaccess / IShellDispatch6 / GazeInput API) must be
        // found by running Spike S1 on a Win11 machine.
        //
        // Until then IsInstalled returns false so this candidate is never chosen, and the
        // spec's Goal #5 "never strand the user" guarantee rests on a Tobii AT (TD Control
        // or Snap Core First) being installed. Wire the real probe here once S1 lands.
        yield return new FallbackCandidate(
            id: "WindowsEyeControl",
            isInstalled: () => false,
            launch: () => { /* wired in Spike S1 */ });
    }

    /// <summary>
    /// Best-effort launch: reads the "Path" registry value under <paramref name="keyPath"/> and
    /// starts it if the exe exists on disk. If the value is absent or the path is stale this is a
    /// no-op — the registry key presence is the IsInstalled signal; the launch path is a refinement.
    /// NOTE: the real value name TD Control / Snap Core First use is unverified; adjust here once
    /// confirmed. See task report.
    /// </summary>
    private static void LaunchFromRegistry(string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        if (key?.GetValue("Path") is string exePath && File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
    }
}
