namespace EyeCtrl.App.Services;

using Microsoft.Extensions.Logging;
using Serilog.Events;

/// <summary>
/// A MEL <see cref="ILogger{TCategoryName}"/> over Serilog's STATIC pipeline: every call is
/// forwarded to whatever <c>Serilog.Log.Logger</c> is at that MOMENT, so a reassignment (the app
/// configures it at startup; tests swap it) is seen immediately.
///
/// <para>
/// <b>Why not the alternatives.</b> <c>Serilog.Extensions.Logging.SerilogLoggerFactory</c>
/// captures the logger instance at construction: a predictor built before a swap would keep
/// writing to the old sink. The repository's own
/// <c>PredictionStackFactory.SerilogLoggerProvider&lt;T&gt;</c> bridge is deliberately
/// Warning-and-up and prefixes every message with the category name — unusable for a predictor
/// whose Information lines ("Using DirectML (GPU) for {Model}") are the point and whose day-log
/// text must read exactly as its template renders it. This bridge therefore maps the level
/// one-to-one, forwards the exception, writes the RENDERED message with no prefix, and asks
/// Serilog whether the level is enabled.
/// </para>
/// </summary>
internal sealed class SerilogStaticLogger<TCategory> : ILogger<TCategory>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && global::Serilog.Log.Logger.IsEnabled(ToSerilogLevel(logLevel));

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.None)
            return;
        global::Serilog.Log.Write(
            ToSerilogLevel(logLevel), exception, "{Message}", formatter(state, exception));
    }

    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
