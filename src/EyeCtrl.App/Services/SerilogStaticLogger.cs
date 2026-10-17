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
/// captures the logger instance when <c>CreateLogger</c> is asked for it: a logger created
/// BEFORE a swap keeps writing to the old sink (a factory built earlier but asked for its logger
/// later does see the new one). This bridge reads the CURRENT logger on EVERY call, so a swap is
/// always seen. The repository's own
/// <c>PredictionStackFactory.SerilogLoggerProvider&lt;T&gt;</c> bridge is deliberately
/// Warning-and-up and prefixes every message with the category name — unusable for a predictor
/// whose Information lines ("Using DirectML (GPU) for {Model}") are the point and whose day-log
/// text must read exactly as its template renders it. This bridge therefore maps the level
/// one-to-one, forwards the exception, writes the RENDERED message with no prefix, and asks
/// Serilog whether the level is enabled.
/// </para>
/// <para>
/// Calls are written through <c>Log.ForContext&lt;TCategory&gt;()</c>, re-evaluated on EVERY call
/// (so a reassignment is still seen): the day-log template prints <c>[{SourceContext}]</c>, and
/// without this the predictor's lines would show an empty <c>[]</c>. The message text does not
/// change.
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
        // Per call: the static logger may have been reassigned since construction, and the
        // context must come from the CURRENT pipeline.
        global::Serilog.Log.ForContext<TCategory>().Write(
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
