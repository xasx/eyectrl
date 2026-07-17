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
    /// Builds candidates in the caller's configured priority order (the user's
    /// <c>SupervisionSettings.FallbackAtOrder</c>). Unknown ids are skipped, so a stale or
    /// mistyped settings entry is ignored rather than throwing. Presence in the list = enabled;
    /// order = priority.
    /// </summary>
    public static IEnumerable<FallbackCandidate> BuildCandidates(IEnumerable<string> order)
    {
        foreach (var id in order)
        {
            var candidate = CreateCandidate(id);
            if (candidate != null) yield return candidate;
        }
    }

    /// <summary>The default ordered candidates (TD Control → Snap Core First → Windows Eye Control).</summary>
    public static IEnumerable<FallbackCandidate> BuildDefaultCandidates() =>
        BuildCandidates(new[] { "TDControl", "SnapCoreFirst", "WindowsEyeControl" });

    /// <summary>Maps a fallback-AT id to its install-probe + launch action, or null for an unknown id.</summary>
    private static FallbackCandidate? CreateCandidate(string id) => id switch
    {
        // Spike S1 (resolved): TD Control is installed as "Computer Control" under WOW6432Node
        // (it's a 32-bit MSI). Value names are ApplicationPath and LauncherPath (NOT a guessed
        // "Path"). LauncherPath is the proper entry — the Launcher bootstraps the full app.
        //   HKLM\SOFTWARE\WOW6432Node\Tobii Dynavox\Computer Control
        //     LauncherPath    = ...\Tdx.ComputerControl.Launcher.exe
        //     ApplicationPath = ...\Tdx.ComputerControl.exe
        "TDControl" => new FallbackCandidate(
            id: "TDControl",
            isInstalled: () => GetRegistryValue(TdControlKey, LauncherValue) is not null,
            launch: () => LaunchExe(GetRegistryValue(TdControlKey, LauncherValue)
                                    ?? GetRegistryValue(TdControlKey, ApplicationValue))),

        // Best-effort: same Tobii Dynavox publisher, likely the same MSI layout (value names
        // unverified — adjust if a Snap Core First install is available to probe).
        "SnapCoreFirst" => new FallbackCandidate(
            id: "SnapCoreFirst",
            isInstalled: () => SubKeyExists(SnapCoreFirstKey),
            launch: () => LaunchFromRegistry(SnapCoreFirstKey)),

        // Spike S1 (resolved): Windows Eye Control has NO reliable programmatic launch vector.
        //   - ms-settings:easeofaccess-eyecontrol only OPENS the Settings page (does not toggle the overlay).
        //   - The GazeInput UWP API (Windows.Devices.Input.Preview) CONSUMES gaze; it does not toggle the OS feature.
        //   - ms-cortana:eyecontrol is unreliable on Win11 (Cortana deprecated/removed).
        // As a last-resort "never strand the user" affordance, open the Settings page so an
        // attendant can enable Eye Control manually. Goal #5's automatic guarantee otherwise rests
        // on a Tobii AT being installed (TD Control above is the realistic case on a Tobii machine).
        "WindowsEyeControl" => new FallbackCandidate(
            id: "WindowsEyeControl",
            isInstalled: () => true, // the Settings page always exists on Win10 1709+/Win11
            launch: () => Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-eyecontrol") { UseShellExecute = true })),

        _ => null // unknown id — skip it (defensive: settings may contain a stale/typo'd entry)
    };

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

    /// <summary>True if the registry sub-key exists. Disposes the opened key handle (the previous
    /// <c>OpenSubKey(...) != null</c> probe leaked it on every FALLEN_BACK probe).</summary>
    private static bool SubKeyExists(string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key != null;
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
