namespace EyeCtrl.LM;

using System.Globalization;
using Microsoft.Win32;

/// <summary>
/// Reads Windows' per-app graphics preference. An explicit "Power saving" choice (1) must win
/// over the high-performance adapter ranking; anything else leaves the ranking in charge.
/// Read-only: EyeCtrl never writes this key.
/// </summary>
internal static class WindowsGpuPreference
{
    internal const int PowerSaving = 1;

    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>The preference Windows stores for this executable, or null when unknown.</summary>
    internal static int? ForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return null;

            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return ParseGpuPreference(key?.GetValue(executable) as string);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <c>GpuPreference</c> out of Windows' semicolon-separated value data, for example
    /// <c>SwapEffectUpgradeEnable=1;GpuPreference=2;</c>. Missing, malformed or non-numeric
    /// values return null.
    /// </summary>
    internal static int? ParseGpuPreference(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;

        foreach (var pair in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0) continue;
            if (!pair[..equals].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)) continue;

            return int.TryParse(pair[(equals + 1)..].Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var preference)
                ? preference
                : null;
        }

        return null;
    }
}
