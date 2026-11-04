namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;
using Microsoft.Win32;

/// <summary>
/// The hand-written Apps &amp; Features entry (plan §10 item 9). It is written only by an elevated
/// phase, points at the STABLE installed Setup (never at a run copy, which is deleted), and is
/// removed by uninstall.
/// </summary>
public sealed class AppsAndFeaturesWriter
{
    private readonly SetupPaths _paths;
    private readonly IRegistryStore _registry;
    private readonly PathGuard _guard;
    private readonly IFileSystem _fileSystem;
    private readonly ISetupLog _log;

    /// <summary>Creates the writer.</summary>
    public AppsAndFeaturesWriter(
        SetupPaths paths, IRegistryStore registry, PathGuard guard, IFileSystem fileSystem,
        ISetupLog log)
    {
        _paths = paths;
        _registry = registry;
        _guard = guard;
        _fileSystem = fileSystem;
        _log = log;
    }

    /// <summary>The unquoted exe path followed by the verb — the value Windows runs.</summary>
    public static string UninstallCommand(string setupExePath) => $"\"{setupExePath}\" uninstall";

    /// <summary>The quiet variant, which always keeps the data.</summary>
    public static string QuietUninstallCommand(string setupExePath) => $"\"{setupExePath}\" uninstall --quiet";

    /// <summary>Writes/refreshes the entry for the version now installed.</summary>
    public void Register(string? version)
    {
        _guard.RequireUninstallKey(SetupConstants.UninstallKeyPath);
        try
        {
            var key = SetupConstants.UninstallKeyPath;
            _registry.WriteString(RegistryHive.LocalMachine, key, "DisplayName", "EyeCtrl");
            _registry.WriteString(RegistryHive.LocalMachine, key, "DisplayVersion",
                string.IsNullOrWhiteSpace(version) ? "unknown" : version);
            _registry.WriteString(RegistryHive.LocalMachine, key, "Publisher", "EyeCtrl");
            _registry.WriteString(RegistryHive.LocalMachine, key, "InstallLocation", _paths.InstallRoot);
            _registry.WriteString(RegistryHive.LocalMachine, key, "DisplayIcon",
                _paths.AppExePath + ",0");
            _registry.WriteDword(RegistryHive.LocalMachine, key, "NoModify", 1);
            _registry.WriteDword(RegistryHive.LocalMachine, key, "NoRepair", 1);
            _registry.WriteDword(RegistryHive.LocalMachine, key, "EstimatedSize", EstimateSizeKb());
            _registry.WriteString(RegistryHive.LocalMachine, key, "UninstallString",
                UninstallCommand(_paths.InstalledSetupExePath));
            _registry.WriteString(RegistryHive.LocalMachine, key, "QuietUninstallString",
                QuietUninstallCommand(_paths.InstalledSetupExePath));
        }
        catch (Exception ex)
        {
            // A missing uninstall entry does not make the installed App unstartable. Repair
            // rewrites it; the swap is already committed.
            _log.Warn($"Could not write the Apps & Features entry: {ex.GetType().Name}");
        }
    }

    /// <summary>Removes the entry.</summary>
    public void Remove()
    {
        _guard.RequireUninstallKey(SetupConstants.UninstallKeyPath);
        try
        {
            _registry.DeleteKey(RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not remove the Apps & Features entry: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// S6c D7: the size Windows shows is in KB. The installed folder is the whole product (App,
    /// Supervisor, Setup and their data); a folder that cannot be measured still gets 1 KB so the
    /// value is present.
    /// </summary>
    private int EstimateSizeKb()
    {
        long bytes = 0;
        try
        {
            foreach (var file in _fileSystem.EnumerateFiles(_paths.InstallRoot, recursive: true))
                bytes += _fileSystem.GetFileLength(file);
        }
        catch (Exception)
        {
            // A locked or vanished folder: report the minimum rather than nothing.
        }

        return bytes <= 0 ? 1 : (int)Math.Min(int.MaxValue, (bytes + 1023) / 1024);
    }
}
