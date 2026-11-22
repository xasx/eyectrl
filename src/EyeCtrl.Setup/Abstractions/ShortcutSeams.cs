namespace EyeCtrl.Setup.Abstractions;

/// <summary>
/// Writes one .lnk shell shortcut behind a seam, so no test ever touches a real Start menu or
/// desktop. The production implementation drives the shell COM API; tests record the calls.
/// </summary>
public interface IShortcutFileWriter
{
    /// <summary>Creates or overwrites the shortcut whose link points at the given exe.</summary>
    void Write(string linkPath, string targetExePath);
}
