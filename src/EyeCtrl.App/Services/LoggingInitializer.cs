namespace EyeCtrl.App.Services;

using System.Globalization;
using System.IO;
using Serilog;
using Serilog.Events;

/// <summary>
/// Handles logging infrastructure initialization.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public static class LoggingInitializer
{
    private const int RetainedLogFileCount = 7; // Number of daily log files to keep
    private const long MaxLogSizeBytes = 50 * 1024 * 1024; // 50 MB/day before a size roll

    /// <summary>
    /// Initializes Serilog with rolling file logging.
    /// </summary>
    /// <param name="logsDirectory">Directory where log files should be kept</param>
    public static void Initialize(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDirectory, "eyectrl-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedLogFileCount,
                // Size cap: a pathological flapping state machine or a stuck retry loop logs
                // per-action at Information; on a machine nobody reboots the day's file could
                // grow without bound otherwise. The count limit bounds both time and size rolls.
                fileSizeLimitBytes: MaxLogSizeBytes,
                rollOnFileSizeLimit: true,
                // Invariant, explicitly: Serilog has an IFormatProvider parameter for the
                // properties it renders into the template, and a log file should not change
                // shape with the machine's locale (CA1305 is an error here since T02).
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
