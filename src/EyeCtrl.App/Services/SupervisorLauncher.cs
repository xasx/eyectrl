namespace EyeCtrl.App.Services;

using System;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Locates and starts the EyeCtrl supervisor.
///
/// Resolution follows the same shape as the supervisor's `ResolveAppExePath`
/// (`Program.cs:597-608`): an environment override wins, otherwise the exe is assumed to
/// sit beside us in the same install directory. One install layout therefore works in both
/// directions, and one env var pair documents both.
///
/// It is NOT an exact mirror, and the difference is load-bearing: `ResolveAppExePath`
/// returns the sibling path unconditionally — it never checks `File.Exists` on it, because
/// the supervisor has to launch *something* and lets `Process.Start` fail loudly. Here the
/// sibling IS existence-checked and a missing exe yields <see langword="null"/>, because
/// <see cref="IsExecutablePresent"/> is a question Task 13 asks before offering the user a choice.
/// Offering to start a supervisor that is not installed would be worse than not offering.
/// </summary>
public static class SupervisorLauncher
{
    /// <summary>Environment override for the supervisor exe path. Mirrors EYECTRL_APP_EXE.</summary>
    public const string ExeEnvVar = "EYECTRL_SUPERVISOR_EXE";

    /// <summary>Default file name, expected beside the app exe.</summary>
    public const string ExeFileName = "EyeCtrl.Supervisor.exe";

    /// <summary>
    /// Resolves the supervisor exe, or <see langword="null"/> when none is installed.
    /// </summary>
    /// <param name="baseDirectory">
    /// Directory to look in. Defaults to <see cref="AppContext.BaseDirectory"/>; tests pass
    /// a temp directory.
    /// </param>
    /// <returns>Full path to the supervisor exe, or null.</returns>
    public static string? ResolveExePath(string? baseDirectory = null)
    {
        var env = Environment.GetEnvironmentVariable(ExeEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        // A stale override must not disable an installed supervisor, so fall through
        // rather than returning null when the env path does not exist.
        var sibling = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, ExeFileName);
        return File.Exists(sibling) ? Path.GetFullPath(sibling) : null;
    }

    /// <summary>
    /// True when a supervisor executable is present on disk and could therefore be started.
    /// </summary>
    /// <remarks>
    /// Named for what it actually checks. It was called <c>IsInstalled</c>, and the gap between
    /// "the file is there" and "supervision is installed" is exactly where a defect lived: the
    /// restart path read it as the latter and handed launch-at-logon to a supervisor that owned
    /// no launch registration. Presence of the exe says nothing about anything being registered
    /// to run it — see <see cref="Startup.SupervisorLaunchRegistration"/> for that question.
    /// </remarks>
    public static bool IsExecutablePresent => ResolveExePath() is not null;

    /// <summary>
    /// Starts the supervisor. Returns false if none is installed or the launch threw —
    /// callers must treat that as "no supervisor" and fall back, never as fatal.
    /// </summary>
    /// <returns><see langword="true"/> if a process was started.</returns>
    public static bool TryLaunch()
    {
        var exePath = ResolveExePath();
        if (exePath is null)
        {
            return false;
        }

        try
        {
            return Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false }) is not null;
        }
        catch
        {
            return false;
        }
    }
}
