namespace EyeCtrl.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using EyeCtrl.Core.Models;

/// <summary>How the running build resolved its per-user profile.</summary>
public enum ProfileMode
{
    /// <summary>A Release exe running from a folder directly under <c>%ProgramFiles%</c> whose
    /// name starts with <c>EyeCtrl</c> (today <c>EyeCtrl</c> and <c>EyeCtrl.previous</c>, and the
    /// swap folders <c>EyeCtrl.run</c>/<c>EyeCtrl.staging</c>/<c>EyeCtrl.failed</c>).</summary>
    Installed,

    /// <summary>Everything else: a Debug build anywhere, or any exe outside the installed
    /// location (an agent worktree's <c>bin\</c>, a copied exe, the update inbox).</summary>
    Dev,

    /// <summary>Started with <c>--real-profile</c>: the real profile by explicit request.</summary>
    RealByFlag,
}

/// <summary>
/// The ONE resolver for where per-user data lives and which machine-global names (mutexes,
/// events, pipes) this build may take.
/// <para>
/// Between 14 and 18 November 2026, Debug builds from agent worktrees ran five times against
/// the owner's real <c>%APPDATA%\EyeCtrl</c>: they wrote settings keys, rewrote the owner's
/// purged dictionaries and pushed real logs out of the 7-day retention. The owner did not
/// start them. The owner approved this guard on 19.11: <b>a dev build refuses the real profile
/// unless it is started with an explicit flag.</b>
/// </para>
/// <para>
/// The same split applies to every machine-global name the processes share. A dev build that
/// took the App single-instance mutex could make the owner's real EyeCtrl silently refuse to
/// start — and the owner controls this PC only through it. A dev Supervisor holding extra
/// <c>eyectrl_watch</c> pipe instances could receive the real App's connects, drop them at the
/// identity binder, and starve the real Supervisor's heartbeat channel into a force-kill. So
/// a dev build takes a <c>.Dev</c> suffix on every shared mutex, event and pipe name: dev
/// pairs find each other, installed pairs keep exclusivity, and neither population can
/// interfere with the other. Installed names are unchanged.
/// </para>
/// <para>
/// <b>The App, the Supervisor and Setup must all resolve the same profile.</b> They do,
/// because each resolves from its own exe path under the same rules, and every parent that
/// launches another EyeCtrl process passes <see cref="RealProfileArgument"/> through only if
/// it was itself started with it.
/// </para>
/// <para>
/// <b>This is the only file in the tree</b> that may name <c>SpecialFolder.ApplicationData</c>,
/// <c>SpecialFolder.LocalApplicationData</c> or a literal <c>Local\EyeCtrl…</c> /
/// <c>Global\EyeCtrl…</c> kernel-object prefix or the <c>eyectrl_watch</c> pipe name — the
/// ratchet test scans the sources for exactly that.
/// </para>
/// </summary>
public sealed class ProfileLocation
{
    /// <summary>The command-line flag that lets a non-installed build use the real profile.</summary>
    public const string RealProfileArgument = "--real-profile";

    /// <summary>Resolved for the current process, once per process (the mutex names and the
    /// profile dir must never change mid-run).</summary>
    public static ProfileLocation Current { get; } = Resolve(
        Environment.ProcessPath,
        IsDebugBuild,
        Environment.GetCommandLineArgs(),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// Whether this assembly was built in the Debug configuration. A Debug build is ALWAYS a
    /// dev build — even one sitting in the installed folder — so an accidentally-copied Debug
    /// binary can never touch the owner's profile.
    /// </summary>
    public static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    private ProfileLocation(
        ProfileMode mode,
        string folderName,
        string reason,
        string programFilesRoot,
        string roamingRoot,
        string localAppDataRoot)
    {
        Mode = mode;
        FolderName = folderName;
        Reason = reason;
        ProgramFilesRoot = programFilesRoot;
        RoamingRoot = roamingRoot;
        LocalAppDataRoot = localAppDataRoot;
        Directory = Path.Combine(roamingRoot, folderName);
    }

    /// <summary>How this build resolved. <see cref="ProfileMode.Dev"/> never touches the real
    /// profile or an installed name.</summary>
    public ProfileMode Mode { get; }

    /// <summary>True for every non-installed, non-flagged build — the population that must be
    /// fenced off from everything the installed build shares.</summary>
    public bool IsDev => Mode == ProfileMode.Dev;

    /// <summary>The profile folder name: <c>EyeCtrl</c> (installed / real-by-flag) or
    /// <c>EyeCtrl.Dev</c>.</summary>
    public string FolderName { get; }

    /// <summary>The per-user profile directory: <c>%APPDATA%\EyeCtrl</c> or
    /// <c>%APPDATA%\EyeCtrl.Dev</c>.</summary>
    public string Directory { get; }

    /// <summary>One plain-language clause for the startup log line.</summary>
    public string Reason { get; }

    /// <summary>The token for the startup log line: <c>installed</c>, <c>dev</c> or
    /// <c>real-by-flag</c>.</summary>
    public string ModeToken => Mode switch
    {
        ProfileMode.Installed => "installed",
        ProfileMode.Dev => "dev",
        ProfileMode.RealByFlag => "real-by-flag",
        _ => Mode.ToString().ToLowerInvariant(),
    };

    /// <summary>The OS roots, unsplit. Consumers that need the raw roots (the update inbox lives
    /// under <see cref="LocalAppDataRoot"/> regardless of build) read them here instead of naming
    /// the OS folders themselves — the ratchet allows those names only in this file.</summary>
    public string ProgramFilesRoot { get; }

    /// <summary><c>%APPDATA%</c>, without the EyeCtrl folder.</summary>
    public string RoamingRoot { get; }

    /// <summary><c>%LOCALAPPDATA%</c>, without the EyeCtrl folders. The update inbox
    /// (<c>%LOCALAPPDATA%\EyeCtrl\updates</c>) is machine-real for every build: it holds the
    /// packages the INSTALLED product installs, and a dev build must neither read state from it
    /// nor write notices into it.</summary>
    public string LocalAppDataRoot { get; }

    /// <summary>The suffix every machine-global name gets in a dev build: empty, or
    /// <c>.Dev</c>.</summary>
    public string NameSuffix => IsDev ? ".Dev" : string.Empty;

    // ---- Machine-global names (base literals live here and nowhere else) ----------------

    /// <summary>The App single-instance mutex.</summary>
    public string AppSingletonMutexName => @"Local\EyeCtrl.App.Singleton" + NameSuffix;

    /// <summary>The Supervisor single-instance mutex.</summary>
    public string SupervisorSingletonMutexName => @"Local\EyeCtrl.Supervisor.Singleton" + NameSuffix;

    /// <summary>The cross-process settings writer mutex (App writers, Supervisor guarded readers).</summary>
    public string SettingsWriterMutexName => @"Local\EyeCtrl.Settings.Writer" + NameSuffix;

    /// <summary>The Setup session mutex (Setup verbs and the App's pre-hand-off probe).</summary>
    public string SetupSessionMutexName => @"Local\EyeCtrl.Setup.Session" + NameSuffix;

    /// <summary>The restart-handoff ready event (parent creates, child sets).</summary>
    public string RestartReadyEventName => @"Local\EyeCtrl.App.RestartReady" + NameSuffix;

    /// <summary>The heartbeat channel between the App and the Supervisor.</summary>
    public string HeartbeatPipeName => "eyectrl_watch" + NameSuffix;

    /// <summary>The settings-request channel between the Supervisor and the App. The base name is
    /// the wire contract in <see cref="SettingRequestChannel"/>; the suffix is this build's.</summary>
    public string SettingsRequestPipeName => SettingRequestChannel.PipeName + NameSuffix;

    // ---- Resolution ---------------------------------------------------------------------

    /// <summary>
    /// Pure resolution over injectable inputs — the whole behaviour table in one function, so
    /// tests can drive every row and every edge case without touching the machine.
    /// </summary>
    /// <param name="exePath">The running exe's full path (null/empty resolves dev).</param>
    /// <param name="isDebugBuild">The build configuration of the running assembly.</param>
    /// <param name="args">The process command line (argv; the program name may or may not be
    /// element 0 — only token membership matters).</param>
    /// <param name="programFilesRoot">The <c>%ProgramFiles%</c> root.</param>
    /// <param name="roamingRoot">The <c>%APPDATA%</c> root.</param>
    /// <param name="localAppDataRoot">The <c>%LOCALAPPDATA%</c> root.</param>
    public static ProfileLocation Resolve(
        string? exePath,
        bool isDebugBuild,
        IReadOnlyList<string> args,
        string programFilesRoot,
        string roamingRoot,
        string localAppDataRoot)
    {
        if (args is null) throw new ArgumentNullException(nameof(args));

        // The flag is the explicit override and wins outright: "any other build started with
        // --real-profile" gets the real profile, and an installed build never passes it.
        if (HasRealProfileFlag(args))
        {
            return new ProfileLocation(
                ProfileMode.RealByFlag, "EyeCtrl", "started with " + RealProfileArgument,
                programFilesRoot, roamingRoot, localAppDataRoot);
        }

        // A Debug build is always a dev build, wherever it sits — the installed-location rule
        // below would otherwise let an accidentally-copied Debug binary pass as installed.
        if (isDebugBuild)
        {
            return new ProfileLocation(
                ProfileMode.Dev, "EyeCtrl.Dev", "debug build",
                programFilesRoot, roamingRoot, localAppDataRoot);
        }

        if (IsInEyeCtrlFolderUnderProgramFiles(exePath, programFilesRoot, out var folderName))
        {
            return new ProfileLocation(
                ProfileMode.Installed, "EyeCtrl", $"release exe in '{folderName}' directly under Program Files",
                programFilesRoot, roamingRoot, localAppDataRoot);
        }

        var reason = string.IsNullOrWhiteSpace(exePath)
            ? "exe path unknown"
            : $"exe at '{TrimForReason(exePath)}' is not an installed location";
        return new ProfileLocation(
            ProfileMode.Dev, "EyeCtrl.Dev", reason,
            programFilesRoot, roamingRoot, localAppDataRoot);
    }

    /// <summary>True when the flag appears as an exact command-line token (case-insensitive, like
    /// every other EyeCtrl flag).</summary>
    public static bool HasRealProfileFlag(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], RealProfileArgument, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The installed-location rule: the exe's PARENT directory is the Program Files root itself,
    /// and that parent's one folder name starts with "EyeCtrl". Comparisons are ordinal
    /// case-insensitive and trailing separators on the root change nothing.
    /// </summary>
    /// <summary>
    /// The installed-location rule: the exe's PARENT directory is the Program Files root itself,
    /// and that parent's one folder name starts with "EyeCtrl". Comparisons are ordinal
    /// case-insensitive and trailing separators on the root change nothing.
    /// </summary>
    private static bool IsInEyeCtrlFolderUnderProgramFiles(
        string? exePath, string programFilesRoot, out string folderName)
    {
        folderName = string.Empty;
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(programFilesRoot))
            return false;

        string parent;
        try
        {
            parent = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? string.Empty;
        }
        catch
        {
            return false; // an unparseable path is not an installed location
        }
        if (string.IsNullOrEmpty(parent))
            return false;

        var root = NormalizeRoot(programFilesRoot);
        if (!parent.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            parent += Path.DirectorySeparatorChar;
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!parent.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            || parent.Length <= rootWithSeparator.Length)
            return false;

        // Between "C:\Program Files\" and the exe there must be exactly ONE folder segment.
        var remainder = parent.Substring(rootWithSeparator.Length);
        var trimmed = remainder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
            return false;

        folderName = trimmed;
        return folderName.StartsWith("EyeCtrl", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            var full = Path.GetFullPath(root);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string TrimForReason(string path)
    {
        const int Max = 120;
        return path.Length <= Max ? path : path.Substring(0, Max) + "…";
    }
}
