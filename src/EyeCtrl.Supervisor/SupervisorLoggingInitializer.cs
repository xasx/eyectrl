namespace EyeCtrl.Supervisor;

using System.IO;
using Serilog;
using Serilog.Events;

/// <summary>
/// Handles logging infrastructure initialization for the supervisor process.
/// Modeled on <c>EyeCtrl.App.Services.LoggingInitializer</c> but writes to
/// <c>supervisor-.log</c> so the supervisor's logs never collide with the app's
/// (<c>eyectrl-.log</c>) when both processes run side by side.
/// </summary>
public static class SupervisorLoggingInitializer
{
    private const int RetainedLogFileCount = 7; // Number of daily log files to keep (7-day retention)

    /// <summary>
    /// Initializes Serilog with daily rolling file logging.
    /// </summary>
    /// <param name="logsDirectory">Directory where supervisor log files should be stored (typically <c>%AppData%\EyeCtrl\logs</c>).</param>
    public static void Initialize(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDirectory, "supervisor-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedLogFileCount,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
