namespace EyeCtrl.Setup.Windows;

using System.Globalization;
using EyeCtrl.Core.Input;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// Reads C10's startup token line from the App's daily Serilog file. The line is written by
/// <c>src/EyeCtrl.App/App.xaml.cs</c> from the shared contract in
/// <see cref="AppStartupTokenLog"/> into <c>%APPDATA%\EyeCtrl\logs\eyectrl-YYYYMMDD.log</c>
/// (LoggingInitializer's <c>eyectrl-.log</c> with a daily rolling interval).
/// </summary>
public sealed class WindowsAppLogProbe : IAppLogProbe
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly SetupPaths _paths;

    /// <summary>Creates the probe over the install layout's log folder.</summary>
    public WindowsAppLogProbe(SetupPaths paths) => _paths = paths;

    /// <inheritdoc />
    public AppStartupTokenLine ReadStartupTokenLine(string appExePath, DateTimeOffset sinceUtc)
    {
        try
        {
            if (!Directory.Exists(_paths.LogsRoot))
                return AppStartupTokenLine.None;

            var sinceLocal = sinceUtc.ToLocalTime().DateTime;
            var expectedExe = "exe=" + appExePath;

            foreach (var file in Directory.EnumerateFiles(_paths.LogsRoot, "eyectrl-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (!line.Contains(AppStartupTokenLog.TokenMarker, StringComparison.Ordinal))
                        continue;
                    if (!line.Contains(expectedExe, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // F9: fail closed. An unparseable timestamp cannot prove the line came from
                    // this launch, so it is not evidence.
                    if (!TryReadTimestamp(line, out var timestamp))
                        continue;
                    if (timestamp < sinceLocal)
                        continue;

                    // fixup-09 (the first real install): Serilog's {Message:lj} writes a bool as
                    // lowercase "true"/"false", so the value is read as a bool, not matched as one
                    // spelling of it. Anything that is not a bool is not evidence.
                    if (!TryReadUiAccess(line, out var uiAccess))
                        continue;

                    return uiAccess
                        ? AppStartupTokenLine.UiAccessTrue
                        : AppStartupTokenLine.UiAccessFalse;
                }
            }
        }
        catch (Exception)
        {
            return AppStartupTokenLine.None;
        }

        return AppStartupTokenLine.None;
    }

    private static bool TryReadUiAccess(string line, out bool uiAccess)
    {
        uiAccess = false;
        var index = line.IndexOf(AppStartupTokenLog.UiAccessMarker, StringComparison.Ordinal);
        if (index < 0)
            return false;

        var start = index + AppStartupTokenLog.UiAccessMarker.Length;
        var end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
            end++;

        return bool.TryParse(line.AsSpan(start, end - start), out uiAccess);
    }

    private static bool TryReadTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;
        if (line.Length < TimestampFormat.Length)
            return false;

        return DateTime.TryParseExact(
            line[..TimestampFormat.Length], TimestampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }
}
