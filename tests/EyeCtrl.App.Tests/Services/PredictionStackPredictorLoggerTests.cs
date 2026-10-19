namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// The LM release's predictor-logger gate: production built <see cref="EyeCtrl.LM.SmallLMPredictor"/>
/// with no logger, so "Using DirectML (GPU) for {Model}" — the owner's gate-4 line — the silent CPU
/// fallback and every load failure were dropped from the day log. This drives the REAL factory path
/// (no <c>PredictorFactoryOverride</c>), model-free: the 1.5B size is selected and NEITHER file
/// exists, so the file-absent arm still constructs the predictor, which logs
/// "No tokenizer provided for 1.5B; LM suggestions disabled" through the new bridge.
///
/// <para>
/// <b>Isolation.</b> The test swaps the global <c>Log.Logger</c>, which made the scroll-start G8
/// gate flaky under parallel runs; it therefore lives in the exclusive
/// <c>"WpfApplication"</c> collection (<c>DisableParallelization = true</c>, the same pattern as the
/// backdrop hide-before-input log gate and <c>ActivationOfferCoordinatorTests</c>), and restores the
/// previous logger in a <c>finally</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class PredictionStackPredictorLoggerTests
{
    [Fact]
    public void ARealFactoryBuild_PutsThePredictorsOwnLineOnTheSerilogSink()
    {
        var appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl_predlogger_" + Guid.NewGuid());
        Directory.CreateDirectory(appDataDir);
        try
        {
            var settings = new EyeCtrlSettings
            {
                Keyboard =
                {
                    PredictionEnabled = true,
                    MLSuggestionsEnabled = true,
                    MLModelSize = "1.5B",
                    PreferGPU = false,
                },
            };
            // A real registry over a manifest entry whose files are absent: the factory's
            // file-absent arm constructs the predictor (and only the predictor's own logger can
            // produce the line below).
            var manifest = new ModelManifest
            {
                Models =
                {
                    ["1.5B"] = new ModelEntry
                    {
                        DisplayName = "Qwen2.5 1.5B (q4f16)",
                        Url = "https://example.invalid/model.onnx",
                        Sha256 = new string('a', 64),
                        SizeBytes = 1,
                        TokenizerUrl = "https://example.invalid/tokenizer.json",
                        TokenizerSha256 = new string('b', 64),
                    },
                },
            };
            var registry = new ModelRegistryService(appDataDir, manifest, settings);

            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            try
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.Sink(sink)
                    .CreateLogger();

                var factory = new PredictionStackFactory(); // synchronous mode: the build runs inline
                factory.Rebuild(
                    settings, AppDomain.CurrentDomain.BaseDirectory, appDataDir, registry);
            }
            finally
            {
                Log.Logger = previousLogger;
            }

            const string expected = "No tokenizer provided for 1.5B; LM suggestions disabled";
            var line = sink.Events.FirstOrDefault(
                e => e.RenderMessage().Contains(expected, StringComparison.Ordinal));
            Check.WithCustomMessage(
                    "the predictor's own logger never reached Serilog (it was built without one). "
                    + "Events captured: " + string.Join(" | ", sink.Events.Select(e => e.RenderMessage())))
                .That(line).IsNotNull();
            Check.WithCustomMessage($"the line must be a Warning, saw {line!.Level}.")
                .That(line.Level).IsEqualTo(LogEventLevel.Warning);
            // The predictor's text is intact (the bridge renders the message; Serilog may quote a
            // substituted string property) and carries no category prefix — "no message-text
            // changes" is about the wording, and the factory's other bridge would have added
            // "SmallLMPredictor: " in front of it.
            Check.That(line.RenderMessage()).Contains(expected);
            Check.WithCustomMessage($"the day-log text must not gain a category prefix, saw: {line.RenderMessage()}")
                .That(line.RenderMessage().Contains("SmallLMPredictor:", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            try { Directory.Delete(appDataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
