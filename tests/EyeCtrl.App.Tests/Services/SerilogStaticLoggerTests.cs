namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.App.Services;
using EyeCtrl.LM;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// The bridge itself (the predictor-logger ticket's review fixup-01, F1): the factory gate only
/// proves a WARNING line through a Verbose sink, so a mapping that sent Information to Debug — or
/// an <c>IsEnabled</c> that ignored the current logger — would silently drop
/// "Using DirectML (GPU) for {Model}" from the day log (minimum Information) and leave that gate
/// green. These pin the level table, the exception, the CURRENT logger's gate, reassignment after
/// construction, and (F2) the <c>SourceContext</c> the day-log template prints.
///
/// <para>
/// Same exclusive <c>"WpfApplication"</c> collection as the factory gate: these swap the global
/// <c>Log.Logger</c>, restored in a <c>finally</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class SerilogStaticLoggerTests
{
    [Theory]
    [InlineData(LogLevel.Trace, LogEventLevel.Verbose)]
    [InlineData(LogLevel.Debug, LogEventLevel.Debug)]
    [InlineData(LogLevel.Information, LogEventLevel.Information)]
    [InlineData(LogLevel.Warning, LogEventLevel.Warning)]
    [InlineData(LogLevel.Error, LogEventLevel.Error)]
    [InlineData(LogLevel.Critical, LogEventLevel.Fatal)]
    public void EveryMelLevel_MapsToOneSerilogLevel(LogLevel mel, LogEventLevel expected)
    {
        var sink = new CapturingSink();
        var previous = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            var bridge = new SerilogStaticLogger<SmallLMPredictor>();

            // The instance method, not the IsEnabled-guarded extension: the mapping is the subject.
            bridge.Log(mel, default, "state", null, (_, _) => "mapped");

            Check.That(sink.Events).HasSize(1);
            Check.WithCustomMessage($"MEL {mel} must map to {expected}, saw {sink.Events[0].Level}.")
                .That(sink.Events[0].Level).IsEqualTo(expected);
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public void None_EmitsNothing()
    {
        var sink = new CapturingSink();
        var previous = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            var bridge = new SerilogStaticLogger<SmallLMPredictor>();

            bridge.Log(LogLevel.None, default, "state", null, (_, _) => "none");

            Check.That(sink.Events).IsEmpty();
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public void TheException_IsForwardedOnTheEvent()
    {
        var sink = new CapturingSink();
        var previous = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            var bridge = new SerilogStaticLogger<SmallLMPredictor>();
            var boom = new InvalidOperationException("boom");

            bridge.Log(LogLevel.Error, default, "state", boom, (_, _) => "with exception");

            Check.That(sink.Events).HasSize(1);
            Check.That(sink.Events[0].Exception).IsSameReferenceAs(boom);
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public void IsEnabled_FollowsTheCurrentLoggersMinimum_AndSeesReassignment()
    {
        var previous = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(new CapturingSink()).CreateLogger();
            var bridge = new SerilogStaticLogger<SmallLMPredictor>(); // constructed under Information

            Check.That(bridge.IsEnabled(LogLevel.Debug)).IsFalse();
            Check.That(bridge.IsEnabled(LogLevel.Information)).IsTrue();

            // Reassigned AFTER construction: the gate must follow the new logger's minimum.
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(new CapturingSink()).CreateLogger();
            Check.That(bridge.IsEnabled(LogLevel.Information)).IsFalse();
            Check.That(bridge.IsEnabled(LogLevel.Warning)).IsTrue();

            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(new CapturingSink()).CreateLogger();
            Check.That(bridge.IsEnabled(LogLevel.Information)).IsTrue();
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public void ReassignmentAfterConstruction_WritesToTheNewSink()
    {
        var first = new CapturingSink();
        var second = new CapturingSink();
        var previous = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(first).CreateLogger();
            var bridge = new SerilogStaticLogger<SmallLMPredictor>(); // holds no logger of its own

            Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(second).CreateLogger();
            bridge.Log(LogLevel.Information, default, "state", null, (_, _) => "after reassignment");

            Check.WithCustomMessage("the bridge wrote to the logger it was constructed under.")
                .That(first.Events).IsEmpty();
            Check.That(second.Events).HasSize(1);
            Check.That(second.Events[0].RenderMessage()).Contains("after reassignment");
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public void TheEvent_CarriesTheCategoryAsSourceContext()
    {
        // F2: the day-log template prints [{SourceContext}]; without ForContext<TCategory>() the
        // predictor's lines read "[] Using DirectML ...".
        var sink = new CapturingSink();
        var previous = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            var bridge = new SerilogStaticLogger<SmallLMPredictor>();

            bridge.Log(LogLevel.Information, default, "state", null, (_, _) => "context probe");

            Check.That(sink.Events).HasSize(1);
            Check.That(sink.Events[0].Properties).ContainsKey("SourceContext");
            var sourceContext = ((ScalarValue)sink.Events[0].Properties["SourceContext"]).Value as string;
            Check.WithCustomMessage($"SourceContext must name the category type, saw '{sourceContext}'.")
                .That(sourceContext).IsEqualTo(typeof(SmallLMPredictor).FullName);
        }
        finally { Log.Logger = previous; }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
