namespace EyeCtrl.Setup;

using EyeCtrl.Core.Services;

/// <summary>
/// The five install folders under Program Files, plus the user-data paths Setup only reads (and
/// the one result record it may write), plus the two machine shell folders the shortcuts live in.
/// All path decisions in the engine go through this type, so the path guard and the tests agree on
/// exactly one layout.
/// </summary>
public sealed class SetupPaths
{
    /// <summary>Creates the layout from the three OS roots the composition supplies, with the
    /// machine's real shortcut folders (see <see cref="MachineShortcutLocations.Resolve"/>).</summary>
    /// <param name="programFilesRoot">Usually <c>C:\Program Files</c>.</param>
    /// <param name="appDataRoot">Usually <c>%APPDATA%</c>; the profile folder is appended.</param>
    /// <param name="localAppDataRoot">Usually <c>%LOCALAPPDATA%</c>; the inbox is appended.</param>
    /// <param name="profileFolderName">The profile folder under the roaming root —
    /// <c>EyeCtrl</c> (default, unchanged for the tests' fake roots) or <c>EyeCtrl.Dev</c> for a
    /// dev build. Production passes the resolver's folder via <see cref="FromProfile"/>.</param>
    public SetupPaths(string programFilesRoot, string appDataRoot, string localAppDataRoot, string? profileFolderName = null)
        : this(programFilesRoot, appDataRoot, localAppDataRoot, profileFolderName, profile: null,
            shortcuts: MachineShortcutLocations.Resolve())
    {
    }

    /// <summary>The full layout for the exactness-critical tests: the machine shortcut roots are
    /// injected instead of resolved from the OS, so the PathGuard's exact allow-list entries and
    /// the shortcut tests' exact paths are asserted against known fake values.</summary>
    /// <param name="shortcuts">The two machine shell folders the shortcuts live in.</param>
    public SetupPaths(
        string programFilesRoot, string appDataRoot, string localAppDataRoot,
        string? profileFolderName, MachineShortcutLocations shortcuts)
        : this(programFilesRoot, appDataRoot, localAppDataRoot, profileFolderName, profile: null, shortcuts)
    {
    }

    private SetupPaths(
        string programFilesRoot, string appDataRoot, string localAppDataRoot,
        string? profileFolderName, ProfileLocation? profile, MachineShortcutLocations shortcuts)
    {
        ProgramFilesRoot = Normalize(programFilesRoot);
        AppDataRoot = Normalize(Path.Combine(appDataRoot, profileFolderName ?? "EyeCtrl"));
        LocalAppDataRoot = Normalize(localAppDataRoot);
        InboxRoot = Normalize(Path.Combine(localAppDataRoot, "EyeCtrl", "updates"));
        CommonProgramsRoot = Normalize(shortcuts.CommonProgramsRoot);
        CommonDesktopRoot = Normalize(shortcuts.CommonDesktopRoot);

        InstallRoot = Path.Combine(ProgramFilesRoot, SetupConstants.InstallFolderName);
        PreviousRoot = Path.Combine(ProgramFilesRoot, SetupConstants.PreviousFolderName);
        StagingRoot = Path.Combine(ProgramFilesRoot, SetupConstants.StagingFolderName);
        FailedRoot = Path.Combine(ProgramFilesRoot, SetupConstants.FailedFolderName);
        RunRoot = Path.Combine(ProgramFilesRoot, SetupConstants.RunFolderName);
        Profile = profile;
    }

    /// <summary>
    /// The layout for a resolved profile (see
    /// <see cref="EyeCtrl.Core.Services.ProfileLocation"/>): the same three OS roots the resolver
    /// read, with the build's own profile folder — <c>EyeCtrl.Dev</c> for a dev build, so a dev
    /// Setup's record, logs and notice reads stay out of the owner's profile. Setup passes the
    /// SETUP rule's resolution (<c>ProfileLocation.CurrentSetup</c>), which is the installed
    /// product's real profile for every Release Setup. The inbox root is deliberately NOT split:
    /// it holds the packages the installed product installs.
    /// </summary>
    public static SetupPaths FromProfile(ProfileLocation profile) => new(
        profile.ProgramFilesRoot,
        profile.RoamingRoot,
        profile.LocalAppDataRoot,
        profile.FolderName,
        profile: profile,
        shortcuts: MachineShortcutLocations.Resolve());

    /// <summary>
    /// The resolver instance this layout was built from, or null for a layout built from raw
    /// strings (tests). <see cref="FromProfile"/> keeps it so a behavioural test can pin WHICH
    /// resolution fed the Setup paths — object identity, because in a Debug test host the App
    /// rule and the Setup rule produce equal values.
    /// </summary>
    public ProfileLocation? Profile { get; }

    /// <summary>The Program Files root (no trailing separator).</summary>
    public string ProgramFilesRoot { get; }

    /// <summary><c>%APPDATA%\EyeCtrl</c>.</summary>
    public string AppDataRoot { get; }

    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    public string LocalAppDataRoot { get; }

    /// <summary><c>%LOCALAPPDATA%\EyeCtrl\updates</c> — the S6b inbox.</summary>
    public string InboxRoot { get; }

    /// <summary>Usually <c>C:\ProgramData\Microsoft\Windows\Start Menu\Programs</c> — the machine's
    /// common Start menu programs folder, which only an elevated token may write.</summary>
    public string CommonProgramsRoot { get; }

    /// <summary>Usually <c>C:\Users\Public\Desktop</c> — the machine's common desktop, which only
    /// an elevated token may write.</summary>
    public string CommonDesktopRoot { get; }

    /// <summary><c>C:\Program Files\EyeCtrl</c>.</summary>
    public string InstallRoot { get; }

    /// <summary><c>C:\Program Files\EyeCtrl.previous</c>.</summary>
    public string PreviousRoot { get; }

    /// <summary><c>C:\Program Files\EyeCtrl.staging</c>.</summary>
    public string StagingRoot { get; }

    /// <summary><c>C:\Program Files\EyeCtrl.failed</c>.</summary>
    public string FailedRoot { get; }

    /// <summary><c>C:\Program Files\EyeCtrl.run</c>.</summary>
    public string RunRoot { get; }

    /// <summary>The installed App.</summary>
    public string AppExePath => Path.Combine(InstallRoot, SetupConstants.AppExeName);

    /// <summary>The installed Supervisor.</summary>
    public string SupervisorExePath => Path.Combine(InstallRoot, SetupConstants.SupervisorExeName);

    /// <summary>The installed Setup — the path Apps &amp; Features must keep pointing at.</summary>
    public string InstalledSetupExePath => Path.Combine(InstallRoot, SetupConstants.SetupExeName);

    /// <summary>The previous version's Setup (used to create a repair run copy when the current
    /// install folder is missing).</summary>
    public string PreviousSetupExePath => Path.Combine(PreviousRoot, SetupConstants.SetupExeName);

    /// <summary>The Start menu shortcut, in the machine's common programs folder.</summary>
    public string StartMenuShortcutPath => Path.Combine(CommonProgramsRoot, SetupConstants.ShortcutName);

    /// <summary>The desktop shortcut, on the machine's common desktop.</summary>
    public string DesktopShortcutPath => Path.Combine(CommonDesktopRoot, SetupConstants.ShortcutName);

    /// <summary><c>%APPDATA%\EyeCtrl\setup-last.json</c> — the one file the guard lets Setup write
    /// under app data.</summary>
    public string RecordPath => Path.Combine(AppDataRoot, "setup-last.json");

    /// <summary><c>%APPDATA%\EyeCtrl\settings.json</c> — read-only for the supervision plan.</summary>
    public string SettingsPath => Path.Combine(AppDataRoot, "settings.json");

    /// <summary><c>%APPDATA%\EyeCtrl\logs</c> — read-only for C10's startup token line, and the one
    /// folder Setup appends its own <c>setup-*.log</c> to (S6c D9).</summary>
    public string LogsRoot => Path.Combine(AppDataRoot, "logs");

    /// <summary>The rolling Setup log for <paramref name="now"/>: <c>logs\setup-yyyyMMdd.log</c>.</summary>
    public string SetupLogPath(DateTimeOffset now) =>
        Path.Combine(LogsRoot, $"setup-{now:yyyyMMdd}.log");

    /// <summary>The private run folder for one id.</summary>
    public string RunFolder(string runId) => Path.Combine(RunRoot, runId);

    /// <summary>The run copy of Setup for one id.</summary>
    public string RunSetupExePath(string runId) =>
        Path.Combine(RunFolder(runId), SetupConstants.SetupExeName);

    /// <summary>True when <paramref name="path"/> is one of the four folders a swap renames. A
    /// process running from one of these cannot remain alive while that folder is renamed.</summary>
    public bool IsInRenamedFolder(string path) =>
        IsUnderInstallRoot(path)
        || IsUnder(PreviousRoot, path)
        || IsUnder(StagingRoot, path)
        || IsUnder(FailedRoot, path);

    /// <summary>True when <paramref name="path"/> is the install root or somewhere below it.</summary>
    public bool IsUnderInstallRoot(string path) => IsUnder(InstallRoot, path);

    /// <summary>True when <paramref name="path"/> is under the given root (the root itself counts).</summary>
    public static bool IsUnder(string root, string path)
    {
        var fullRoot = Normalize(root);
        var fullPath = Normalize(path);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalises a path and removes a trailing separator (except for a drive root).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0)
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }
}

/// <summary>
/// The two machine shell folders the install's shortcuts live in. They deliberately do NOT
/// follow the profile: the Start menu and the desktop are per-machine (like Program Files and
/// the HKLM uninstall entry), not per-user, so every Setup — dev included — installs the real
/// machine shortcuts. <see cref="SetupPaths"/>' convenience ctors and <see
/// cref="SetupPaths.FromProfile"/> resolve the machine's real folders; the exactness-critical
/// tests inject fakes.
/// </summary>
public readonly record struct MachineShortcutLocations(string CommonProgramsRoot, string CommonDesktopRoot)
{
    /// <summary>The machine's real shell folders; throws when the OS cannot answer (the same
    /// failure model the composition's old <c>RequireFolder</c> had).</summary>
    public static MachineShortcutLocations Resolve() => new(
        RequireShellFolder(Environment.SpecialFolder.CommonPrograms),
        RequireShellFolder(Environment.SpecialFolder.CommonDesktopDirectory));

    private static string RequireShellFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"The OS folder '{folder}' is unavailable.");
        return path;
    }
}
