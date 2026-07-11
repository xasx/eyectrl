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
    private const string ValueName = "EyeCtrl";

    private readonly string _exePath;

    public WindowsStartupRegistrar()
    {
        _exePath = Environment.ProcessPath ?? string.Empty;
    }

    public void SetStartOnBoot(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key == null) return;

        if (enabled)
        {
            var commandValue = StartupCommandValue.BuildCommandValue(_exePath);
            if (!string.IsNullOrEmpty(commandValue))
                key.SetValue(ValueName, commandValue);
        }
        else
        {
            if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }
    }
}
