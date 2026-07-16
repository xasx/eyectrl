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
    /// 1. TD Control — verified registry layout (Spike S1, resolved on the target machine).
    /// 2. Snap Core First (same Tobii Dynavox publisher convention, best-effort probe).
    /// 3. Windows built-in Eye Control — no reliable programmatic launch vector (Spike S1);
    ///    falls back to opening the Settings page as a documented manual step.
    /// </summary>
    public static IEnumerable<FallbackCandidate> BuildDefaultCandidates()
    {
        // Spike S1 (resolved): TD Control is installed as "Computer Control" under WOW6432Node
        // (it's a 32-bit MSI). The registry value names are ApplicationPath and LauncherPath
        // (NOT a guessed "Path"). LauncherPath is the proper entry — the Launcher bootstraps
        // the full app; ApplicationPath is the inner exe. Confirmed on the dev machine:
        //   HKLM\SOFTWARE\WOW6432Node\Tobii Dynavox\Computer Control
        //     LauncherPath    = C:\Program Files (x86)\Tobii Dynavox\Computer Control\Tdx.ComputerControl.Launcher.exe
        //     ApplicationPath = ...\Tdx.ComputerControl.exe
        yield return new FallbackCandidate(
            id: "TDControl",
            isInstalled: () => GetRegistryValue(TdControlKey, LauncherValue) is not null,
            launch: () => LaunchExe(GetRegistryValue(TdControlKey, LauncherValue)
                                    ?? GetRegistryValue(TdControlKey, ApplicationValue)));

        // Best-effort: same Tobii Dynavox publisher, likely the same MSI layout (value names
        // unverified — adjust if a Snap Core First install is available to probe).
        yield return new FallbackCandidate(
            id: "SnapCoreFirst",
            isInstalled: () => Registry.LocalMachine.OpenSubKey(SnapCoreFirstKey) != null,
            launch: () => LaunchFromRegistry(SnapCoreFirstKey));

        // Spike S1 (resolved): Windows Eye Control has NO reliable programmatic launch vector.
        //   - ms-settings:easeofaccess-eyecontrol only OPENS the Settings page (does not toggle/launch the overlay).
        //   - The GazeInput UWP API (Windows.Devices.Input.Preview) CONSUMES gaze; it does not toggle the OS feature.
        //   - ms-cortana:eyecontrol is unreliable on Win11 (Cortana deprecated/removed).
        // As a last-resort "never strand the user" affordance, open the Settings page so an
        // attendant can enable Eye Control manually. Goal #5's guarantee otherwise rests on a
        // Tobii AT being installed (TD Control above is the realistic case on a Tobii machine).
        yield return new FallbackCandidate(
            id: "WindowsEyeControl",
            isInstalled: () => true, // the Settings page always exists on Win10 1709+/Win11
            launch: () => Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-eyecontrol") { UseShellExecute = true }));
    }

    // --- Registry key/value constants (verified via Spike S1) ---
    private const string TdControlKey = @"SOFTWARE\WOW6432Node\Tobii Dynavox\Computer Control";
    private const string SnapCoreFirstKey = @"SOFTWARE\WOW6432Node\Tobii Dynavox\Snap Core First";
    private const string LauncherValue = "LauncherPath";
    private const string ApplicationValue = "ApplicationPath";

    private static string? GetRegistryValue(string keyPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    /// <summary>Best-effort launch of an exe path read from the registry; no-op if absent/stale.</summary>
    private static void LaunchExe(string? exePath)
    {
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
    }

    /// <summary>
    /// Best-effort launch via a registry key: reads a candidate exe value under <paramref name="keyPath"/>
    /// (LauncherPath, then ApplicationPath) and starts it if present on disk. Used for Snap Core First
    /// whose exact value names are unverified.
    /// </summary>
    private static void LaunchFromRegistry(string keyPath)
    {
        var exePath = GetRegistryValue(keyPath, LauncherValue) ?? GetRegistryValue(keyPath, ApplicationValue);
        LaunchExe(exePath);
    }
}
