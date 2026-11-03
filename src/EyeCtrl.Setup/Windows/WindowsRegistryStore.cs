namespace EyeCtrl.Setup.Windows;

using EyeCtrl.Setup.Abstractions;
using Microsoft.Win32;

/// <summary>The thin registry implementation. HKLM uses the 64-bit view (the Apps &amp; Features
/// entry must be the one a 64-bit Windows shows); HKCU uses the default view.</summary>
public sealed class WindowsRegistryStore : IRegistryStore
{
    /// <inheritdoc />
    public int? ReadDword(RegistryHive hive, string subKey, string valueName)
    {
        using var key = OpenBase(hive).OpenSubKey(subKey, writable: false);
        return key?.GetValue(valueName) is int value ? value : null;
    }

    /// <inheritdoc />
    public string? ReadString(RegistryHive hive, string subKey, string valueName)
    {
        using var key = OpenBase(hive).OpenSubKey(subKey, writable: false);
        return key?.GetValue(valueName) as string;
    }

    /// <inheritdoc />
    public void WriteString(RegistryHive hive, string subKey, string valueName, string value)
    {
        using var key = OpenBase(hive).CreateSubKey(subKey, writable: true)
            ?? throw new IOException($"Could not open {hive}\\{subKey}");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void WriteDword(RegistryHive hive, string subKey, string valueName, int value)
    {
        using var key = OpenBase(hive).CreateSubKey(subKey, writable: true)
            ?? throw new IOException($"Could not open {hive}\\{subKey}");
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    /// <inheritdoc />
    public void DeleteValue(RegistryHive hive, string subKey, string valueName)
    {
        using var key = OpenBase(hive).OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <inheritdoc />
    public void DeleteKey(RegistryHive hive, string subKey)
    {
        using var baseKey = OpenBase(hive);
        if (baseKey.OpenSubKey(subKey, writable: false) is not null)
            baseKey.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadValueNames(RegistryHive hive, string subKey)
    {
        using var key = OpenBase(hive).OpenSubKey(subKey, writable: false);
        return key?.GetValueNames() ?? [];
    }

    private static RegistryKey OpenBase(RegistryHive hive) =>
        hive == RegistryHive.LocalMachine
            ? RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)
            : RegistryKey.OpenBaseKey(hive, RegistryView.Default);
}
