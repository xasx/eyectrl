namespace EyeCtrl.App.Services;

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

    /// <summary>
    /// Initializes Serilog with rolling file logging.
    /// </summary>
    /// <param name="logsDirectory">Directory where log files should be stored</param>
    public static void Initialize(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDirectory, "eyectrl-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedLogFileCount,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
