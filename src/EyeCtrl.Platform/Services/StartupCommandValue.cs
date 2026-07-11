namespace EyeCtrl.Platform.Services;

/// <summary>
/// Pure helper that builds the registry Run-key value string for the app's exe path.
/// Extracted from WindowsStartupRegistrar so the quoting logic is unit-testable
/// without touching the real HKCU hive (mirrors the IMouseCoordinateMapper pattern).
/// </summary>
public static class StartupCommandValue
{
    /// <summary>
    /// Wraps the exe path in double quotes if not already quoted.
    /// Returns empty string for null/empty/whitespace input.
    /// </summary>
    public static string BuildCommandValue(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return string.Empty;

        var trimmed = exePath.Trim();
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return trimmed;

        return "\"" + trimmed + "\"";
    }
}
