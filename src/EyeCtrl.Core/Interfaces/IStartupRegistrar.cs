namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// Registers/unregisters the application for Windows startup.
/// Implementations write/remove a startup entry (e.g. registry Run key)
/// using the current process path.
/// </summary>
public interface IStartupRegistrar
{
    /// <summary>Sets whether the app starts on Windows boot.</summary>
    /// <param name="enabled">True to register; false to unregister.</param>
    void SetStartOnBoot(bool enabled);

    /// <summary>True if a startup entry currently exists for the app.</summary>
    bool IsRegistered { get; }
}
