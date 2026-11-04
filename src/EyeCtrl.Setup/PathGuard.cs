namespace EyeCtrl.Setup;

using Microsoft.Win32;

/// <summary>Thrown when a privileged write leaves the allow-list. This is a programming error and
/// deliberately not caught: a path outside the list must abort the operation, not be skipped.</summary>
public sealed class PathGuardException : Exception
{
    /// <summary>Creates the exception with the rejected target.</summary>
    public PathGuardException(string message) : base(message)
    {
    }
}

/// <summary>
/// Plan §10's allow-list, in one place. The elevated steps may touch only:
/// <list type="bullet">
/// <item>the five <c>EyeCtrl*</c> folders under Program Files (and their contents);</item>
/// <item>the exact <c>Uninstall\EyeCtrl</c> key under HKLM;</item>
/// <item>HKCU <c>Run</c>/<c>RunOnce</c> values whose names start with <c>EyeCtrl</c>;</item>
/// <item><c>%APPDATA%\EyeCtrl\setup-last.json</c> — the result record;</item>
/// <item><c>%APPDATA%\EyeCtrl\logs\setup-*.log</c> — the rolling diagnostics log (S6c D9).</item>
/// </list>
/// <c>%APPDATA%\EyeCtrl</c> otherwise moves only through the S6e deletion seam.
/// </summary>
public sealed class PathGuard
{
    private readonly SetupPaths _paths;

    /// <summary>Creates the guard over the one install layout.</summary>
    public PathGuard(SetupPaths paths) => _paths = paths;

    /// <summary>Throws unless <paramref name="path"/> is one of the five Program Files trees.</summary>
    public void RequireProgramFilesPath(string path)
    {
        var full = SetupPaths.Normalize(path);
        var allowed = new[]
        {
            _paths.InstallRoot, _paths.PreviousRoot, _paths.StagingRoot,
            _paths.FailedRoot, _paths.RunRoot,
        };

        foreach (var root in allowed)
        {
            if (SetupPaths.IsUnder(root, full))
                return;
        }

        throw new PathGuardException(
            $"'{full}' is outside the EyeCtrl* Program Files folders");
    }

    /// <summary>Throws unless the key is exactly the Apps &amp; Features entry.</summary>
    public void RequireUninstallKey(string subKey)
    {
        if (!string.Equals(subKey, SetupConstants.UninstallKeyPath, StringComparison.OrdinalIgnoreCase))
            throw new PathGuardException($"'{subKey}' is not the EyeCtrl uninstall key");
    }

    /// <summary>Throws unless this is an HKCU Run/RunOnce value named <c>EyeCtrl…</c>.</summary>
    public void RequireRunValue(RegistryHive hive, string subKey, string valueName)
    {
        if (hive != RegistryHive.CurrentUser)
            throw new PathGuardException($"run values may only be written under HKCU, not {hive}");

        var isRun = string.Equals(subKey, SetupConstants.RunKeyPath, StringComparison.OrdinalIgnoreCase);
        var isRunOnce = string.Equals(subKey, SetupConstants.RunOnceKeyPath, StringComparison.OrdinalIgnoreCase);
        if (!isRun && !isRunOnce)
            throw new PathGuardException($"'{subKey}' is not the HKCU Run/RunOnce key");

        if (!valueName.StartsWith("EyeCtrl", StringComparison.OrdinalIgnoreCase))
            throw new PathGuardException($"'{valueName}' is not an EyeCtrl run value");
    }

    /// <summary>Throws unless this is exactly the one result record.</summary>
    public void RequireResultRecordPath(string path)
    {
        if (!string.Equals(SetupPaths.Normalize(path), _paths.RecordPath, StringComparison.OrdinalIgnoreCase))
            throw new PathGuardException($"'{path}' is not the setup-last.json record");
    }

    /// <summary>Throws unless the folder is exactly the app-data root the record lives in. Creating
    /// it is allowed only so the record can be written on a machine where the App never ran.</summary>
    public void RequireResultRecordDirectory(string path)
    {
        if (!string.Equals(SetupPaths.Normalize(path), _paths.AppDataRoot, StringComparison.OrdinalIgnoreCase))
            throw new PathGuardException($"'{path}' is not the EyeCtrl app-data root");
    }

    /// <summary>Throws unless this is a <c>setup-*.log</c> directly inside the app's log folder.</summary>
    public void RequireSetupLogPath(string path)
    {
        var full = SetupPaths.Normalize(path);
        if (!SetupPaths.IsUnder(_paths.LogsRoot, full)
            || string.Equals(full, _paths.LogsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new PathGuardException($"'{path}' is not a Setup log under the app log folder");
        }

        var name = Path.GetFileName(full);
        if (!name.StartsWith("setup-", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            throw new PathGuardException($"'{name}' is not a rolling Setup log");
        }

        // IsUnder already rejects a nested folder, but a '..' in the file name would normalise
        // away; the parent check keeps the rule exact.
        if (!string.Equals(Path.GetDirectoryName(full), _paths.LogsRoot, StringComparison.OrdinalIgnoreCase))
            throw new PathGuardException($"'{path}' is not directly inside the app log folder");
    }

    /// <summary>Throws unless the folder is the app-data root or its <c>logs</c> child — the only
    /// two folders the Setup log may create on a machine where the App never ran.</summary>
    public void RequireSetupLogDirectory(string path)
    {
        var full = SetupPaths.Normalize(path);
        if (!string.Equals(full, _paths.AppDataRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, _paths.LogsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new PathGuardException($"'{path}' is not the EyeCtrl app-data or log folder");
        }
    }
}
