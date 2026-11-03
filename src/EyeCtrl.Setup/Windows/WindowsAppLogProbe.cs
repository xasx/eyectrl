namespace EyeCtrl.Setup.Windows;

using System.Globalization;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// Reads C10's startup token line from the App's daily Serilog file. The line is written by
/// <c>src/EyeCtrl.App/App.xaml.cs</c>:
/// <c>Token: IL={IntegrityLevel} UIAccess={UIAccess} Elevated={Elevated} exe={ExecutablePath}</c>,
/// into <c>%APPDATA%\EyeCtrl\logs\eyectrl-YYYYMMDD.log</c> (LoggingInitializer's
/// <c>eyectrl-.log</c> with a daily rolling interval).
/// </summary>
public sealed class WindowsAppLogProbe : IAppLogProbe
{
    private const string TokenMarker = "Token: IL=";
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly SetupPaths _paths;

    /// <summary>Creates the probe over the install layout's log folder.</summary>
    public WindowsAppLogProbe(SetupPaths paths) => _paths = paths;

    /// <inheritdoc />
    public bool HasUiAccessStartupLine(string appExePath, DateTimeOffset sinceUtc)
    {
        try
        {
            if (!Directory.Exists(_paths.LogsRoot))
                return false;

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
                    if (!line.Contains(TokenMarker, StringComparison.Ordinal))
                        continue;
                    if (!line.Contains("UIAccess=True", StringComparison.Ordinal))
                        continue;
                    if (!line.Contains(expectedExe, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!TryReadTimestamp(line, out var timestamp) || timestamp >= sinceLocal)
                        return true;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
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
