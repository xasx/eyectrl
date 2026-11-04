namespace EyeCtrl.Setup;

/// <summary>
/// The working directory Setup runs from and gives every child: <c>%SystemRoot%\System32</c>,
/// never one of the folders a swap renames. The S6f review measured both halves of the lock:
/// a live process whose cwd is a folder blocks renaming that folder, and a ShellExecute child
/// with no <c>WorkingDirectory</c> inherits its caller's cwd. Setup therefore sets its own cwd at
/// entry (which covers the run copy and every elevated phase, since they are the same exe) and
/// passes this folder explicitly on every start, the App start included.
/// </summary>
public static class SetupWorkingDirectory
{
    /// <summary>The neutral folder, resolved once. It is never inside <c>Program Files\EyeCtrl*</c>.</summary>
    public static string Neutral { get; } = Resolve();

    /// <summary>
    /// Sets this process's current directory to <see cref="Neutral"/>. Called first thing at
    /// entry, before anything is parsed or started. A failure is harmless: every start still
    /// carries the directory explicitly.
    /// </summary>
    public static bool Enter() =>
        Enter(Neutral, path => Environment.CurrentDirectory = path);

    /// <summary>The seam form: a test supplies the setter, so the host's own cwd never moves.</summary>
    internal static bool Enter(string neutral, Action<string> setCurrentDirectory)
    {
        try
        {
            setCurrentDirectory(neutral);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Resolve()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate)
                    && Path.IsPathFullyQualified(candidate)
                    && !IsInsideAnEyeCtrlFolder(candidate))
                {
                    return SetupPaths.Normalize(candidate);
                }
            }
            catch (Exception)
            {
                // Try the next candidate.
            }
        }

        // Unreachable on Windows; a working directory is still better than failing the install.
        return @"C:\";
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return TryGet(() => Environment.SystemDirectory);
        yield return TryGet(() => Environment.GetFolderPath(Environment.SpecialFolder.System));
        yield return TryGet(() => Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        yield return TryGet(Path.GetTempPath);
        yield return TryGet(() => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    private static string? TryGet(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsInsideAnEyeCtrlFolder(string path)
    {
        foreach (var segment in path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var isEyeCtrl = segment.StartsWith(
                SetupConstants.InstallFolderName, StringComparison.OrdinalIgnoreCase);
            var isOneOfTheFolders = segment.Length == SetupConstants.InstallFolderName.Length
                || segment[SetupConstants.InstallFolderName.Length] == '.';
            if (isEyeCtrl && isOneOfTheFolders)
                return true;
        }

        return false;
    }
}
