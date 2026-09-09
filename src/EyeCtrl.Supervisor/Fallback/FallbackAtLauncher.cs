namespace EyeCtrl.Supervisor.Fallback;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Serilog;

/// <summary>
/// Selects and launches a fallback assistive-technology (AT) app when EyeCtrl is in a
/// crash loop (FALLEN_BACK state), so the user retains eye control. Candidates are probed
/// in priority order and the first one installed is launched ("never strand the user").
/// </summary>
public static class FallbackAtLauncher
{
    /// <summary>
    /// Iterates <paramref name="candidates"/> in order and returns the first one that actually
    /// started. A candidate whose probe passes but whose launch does nothing is skipped, not
    /// committed to. Returns null when nothing started.
    /// </summary>
    /// <remarks>
    /// The fall-through is the point. Previously this returned the first candidate whose
    /// <see cref="FallbackCandidate.IsInstalled"/> probe passed, regardless of what the launch
    /// did — and the probes are weaker than the launches: they read registry keys that survive
    /// an uninstall, while the launch requires an exe on disk. A stale key therefore consumed
    /// the whole fallback and skipped the remaining candidates, including the terminal one that
    /// always works. Since this runs only after the app has crashed past MaxRetries, that made
    /// the last-resort path fail precisely when it was the last resort.
    /// <para>
    /// A candidate that throws is treated as one that did not launch. The supervisor registers
    /// no global exception handler, so letting a Process.Start failure escape here would take
    /// down the watchdog while the user is already without an input channel.
    /// </para>
    /// </remarks>
    public static FallbackCandidate? LaunchFirstInstalled(IEnumerable<FallbackCandidate> candidates)
    {
        foreach (var c in candidates)
        {
            if (!Probe(c)) continue;
            if (TryLaunch(c)) return c;

            Log.Warning(
                "Fallback AT {Id} reported installed but did not launch; trying the next candidate.", c.Id);
        }
        return null;
    }

    private static bool Probe(FallbackCandidate c)
    {
        try
        {
            return c.IsInstalled();
        }
        catch (Exception ex)
        {
            // A probe reads the registry; denied access or a malformed value must not end the
            // session. Treat it as "not available" and move on.
            Log.Warning(ex, "Fallback AT {Id} install probe threw; treating as not installed.", c.Id);
            return false;
        }
    }

    private static bool TryLaunch(FallbackCandidate c)
    {
        try
        {
            return c.Launch();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Fallback AT {Id} threw while launching; trying the next candidate.", c.Id);
            return false;
        }
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
        // Probe and launch MUST resolve through the same path, or the probe promises something
        // the launch cannot deliver. The old probe asked only whether the LauncherPath VALUE
        // existed; the launch additionally required File.Exists. A key left behind by an
        // uninstall, or an upgrade that moved the exe, satisfied the first and silently failed
        // the second — and consumed the fallback on the way past.
        "TDControl" => new FallbackCandidate(
            id: "TDControl",
            isInstalled: () => ResolveInstalledExe(TdControlKey) is not null,
            launch: () => LaunchExe(ResolveInstalledExe(TdControlKey))),

        // Best-effort: same Tobii Dynavox publisher, likely the same MSI layout (value names
        // unverified — adjust if a Snap Core First install is available to probe). Same
        // resolver as TD Control, so the same probe/launch agreement holds. The previous probe
        // was weaker still — merely that the SUB-KEY existed — which passed even when neither
        // value name matched.
        "SnapCoreFirst" => new FallbackCandidate(
            id: "SnapCoreFirst",
            isInstalled: () => ResolveInstalledExe(SnapCoreFirstKey) is not null,
            launch: () => LaunchExe(ResolveInstalledExe(SnapCoreFirstKey))),

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
            launch: OpenEyeControlSettingsPage),

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

    /// <summary>
    /// Resolves a launchable exe under <paramref name="keyPath"/> — LauncherPath first (it
    /// bootstraps the full app), then ApplicationPath — returning only a path that exists on
    /// disk, or null.
    /// </summary>
    /// <remarks>
    /// The single source of truth for both the install probe and the launch. Sharing it is what
    /// makes "installed" and "launchable" the same question: a registry value pointing at an exe
    /// that is no longer there now reports not-installed, so the launcher moves to the next
    /// candidate instead of committing to one that cannot start. Note it falls back to
    /// ApplicationPath when LauncherPath is stale, which the old probe never did — it asked
    /// about LauncherPath alone and then launched either.
    /// </remarks>
    private static string? ResolveInstalledExe(string keyPath)
    {
        foreach (var valueName in new[] { LauncherValue, ApplicationValue })
        {
            var path = GetRegistryValue(keyPath, valueName);
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Starts an exe. Returns true only if a process was actually started.</summary>
    private static bool LaunchExe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
        return Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true }) is not null;
    }

    /// <summary>
    /// Opens the Eye Control settings page so an attendant can enable it by hand. Returns true
    /// when the shell accepted the request — the shell handler for an ms-settings: URI reports
    /// no process of its own, so a clean return is the only success signal available.
    /// </summary>
    private static bool OpenEyeControlSettingsPage()
    {
        Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-eyecontrol") { UseShellExecute = true });
        return true;
    }
}
