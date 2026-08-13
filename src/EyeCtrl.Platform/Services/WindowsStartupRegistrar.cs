namespace EyeCtrl.Platform.Services;

using EyeCtrl.Core.Interfaces;
using Microsoft.Win32;

/// <summary>
/// Registers/unregisters the app for Windows startup via the HKCU Run key.
/// Uses Environment.ProcessPath (the stable single-file bundle apphost path),
/// never Assembly.Location (which points into the varying temp extraction dir).
/// </summary>
public class WindowsStartupRegistrar : IStartupRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Production value name under the Run key.</summary>
    public const string DefaultValueName = "EyeCtrl";

    private readonly string _exePath;
    private readonly string _valueName;

    /// <summary>Registrar for the production value name.</summary>
    public WindowsStartupRegistrar() : this(null)
    {
    }

    /// <summary>
    /// Registrar for a specific Run value name.
    /// </summary>
    /// <param name="valueName">
    /// Value name under the Run key. Defaults to <see cref="DefaultValueName"/>. A scoped instance
    /// passes a suffixed name, which is what stops a test run from deleting the default instance's
    /// autostart entry — every launch calls <see cref="SetStartOnBoot"/>, and a <c>false</c> deletes.
    /// </param>
    public WindowsStartupRegistrar(string? valueName)
    {
        _exePath = Environment.ProcessPath ?? string.Empty;
        _valueName = valueName ?? DefaultValueName;
    }

    /// <summary>
    /// The Run value name this registrar reads and writes. Read back by the app's phase-2
    /// instance-scope check.
    /// </summary>
    public string EffectiveValueName => _valueName;

    public void SetStartOnBoot(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key == null) return;

        if (enabled)
        {
            var commandValue = StartupCommandValue.BuildCommandValue(_exePath);
            if (!string.IsNullOrEmpty(commandValue))
                key.SetValue(_valueName, commandValue);
        }
        else
        {
            if (key.GetValue(_valueName) != null)
                key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }

    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(_valueName) != null;
        }
    }
}
