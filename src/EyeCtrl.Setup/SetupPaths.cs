namespace EyeCtrl.Setup;

/// <summary>
/// The five install folders under Program Files, plus the user-data paths Setup only reads (and
/// the one result record it may write). All path decisions in the engine go through this type, so
/// the path guard and the tests agree on exactly one layout.
/// </summary>
public sealed class SetupPaths
{
    /// <summary>Creates the layout from the three OS roots the composition supplies.</summary>
    /// <param name="programFilesRoot">Usually <c>C:\Program Files</c>.</param>
    /// <param name="appDataRoot">Usually <c>%APPDATA%</c>; EyeCtrl's folder is appended.</param>
    /// <param name="localAppDataRoot">Usually <c>%LOCALAPPDATA%</c>; the inbox is appended.</param>
    public SetupPaths(string programFilesRoot, string appDataRoot, string localAppDataRoot)
    {
        ProgramFilesRoot = Normalize(programFilesRoot);
        AppDataRoot = Normalize(Path.Combine(appDataRoot, "EyeCtrl"));
        LocalAppDataRoot = Normalize(localAppDataRoot);
        InboxRoot = Normalize(Path.Combine(localAppDataRoot, "EyeCtrl", "updates"));

        InstallRoot = Path.Combine(ProgramFilesRoot, SetupConstants.InstallFolderName);
        PreviousRoot = Path.Combine(ProgramFilesRoot, SetupConstants.PreviousFolderName);
        StagingRoot = Path.Combine(ProgramFilesRoot, SetupConstants.StagingFolderName);
        FailedRoot = Path.Combine(ProgramFilesRoot, SetupConstants.FailedFolderName);
        RunRoot = Path.Combine(ProgramFilesRoot, SetupConstants.RunFolderName);
    }

    /// <summary>The Program Files root (no trailing separator).</summary>
    public string ProgramFilesRoot { get; }

    /// <summary><c>%APPDATA%\EyeCtrl</c>.</summary>
    public string AppDataRoot { get; }

    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    public string LocalAppDataRoot { get; }

    /// <summary><c>%LOCALAPPDATA%\EyeCtrl\updates</c> — the S6b inbox.</summary>
    public string InboxRoot { get; }

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
