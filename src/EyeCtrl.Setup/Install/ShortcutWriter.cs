namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The shell shortcuts the install owns: one for the App in the machine's common Start menu
/// programs folder and one on its common desktop. Both point at the STABLE installed App (never at
/// a run copy), are ensured by every committed swap, and are removed by uninstall. A missing
/// shortcut is recreated by the next swap or repair; an existing one is never overwritten, so an
/// owner-customised link survives. Like the Apps &amp; Features entry, every write is best-effort:
/// the swap is already committed, so a failure is a warning, never fatal.
/// </summary>
public sealed class ShortcutWriter
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IShortcutFileWriter _links;
    private readonly PathGuard _guard;
    private readonly ISetupLog _log;

    /// <summary>Creates the writer.</summary>
    public ShortcutWriter(
        SetupPaths paths, IFileSystem fileSystem, IShortcutFileWriter links, PathGuard guard,
        ISetupLog log)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _links = links;
        _guard = guard;
        _log = log;
    }

    /// <summary>Creates each shortcut that is missing. The two writes are independent: one
    /// failing never stops the other.</summary>
    public void Ensure()
    {
        EnsureOne(_paths.StartMenuShortcutPath, "Start menu");
        EnsureOne(_paths.DesktopShortcutPath, "desktop");
    }

    /// <summary>Removes both shortcuts (uninstall). Missing is silent; a failure is a warning, so
    /// the uninstall never turns a removed program into a failed uninstall over a .lnk.</summary>
    public void Remove()
    {
        RemoveOne(_paths.StartMenuShortcutPath, "Start menu");
        RemoveOne(_paths.DesktopShortcutPath, "desktop");
    }

    private void EnsureOne(string linkPath, string where)
    {
        _guard.RequireShortcutPath(linkPath);
        try
        {
            if (_fileSystem.FileExists(linkPath))
                return;

            _links.Write(linkPath, _paths.AppExePath);
            _log.Info($"Created the {where} shortcut.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not create the {where} shortcut: {ex.GetType().Name}");
        }
    }

    private void RemoveOne(string linkPath, string where)
    {
        _guard.RequireShortcutPath(linkPath);
        try
        {
            if (!_fileSystem.FileExists(linkPath))
                return;

            _fileSystem.DeleteFile(linkPath);
            _log.Info($"Uninstall removed the {where} shortcut.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not remove the {where} shortcut: {ex.GetType().Name}");
        }
    }
}
