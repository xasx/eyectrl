namespace EyeCtrl.Core.Tests;

using System.Globalization;
using System.Text.Json.Nodes;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Exclusive collection for the tests here that activate a process-global German table — the
/// App.Tests convention ("German-path tests use injected tables or an exclusive collection"),
/// applied to Core.Tests because <see cref="CalibrationService"/> has no table-injection seam.
/// </summary>
[CollectionDefinition("StringTable Tests (Core)", DisableParallelization = true)]
public class StringTableCoreCollection
{
}

/// <summary>
/// T07 fixup-02: the load-rejection LOG arguments must stay invariant English even while the
/// Settings surface renders German. Logs are diagnostics, not the user's screen; the page's own
/// sentence may speak German, the support log may not.
/// </summary>
[Collection("StringTable Tests (Core)")]
public class CalibrationLogEnglishTests
{
    [Fact]
    public void MonitorMismatchLog_StaysInvariantEnglish_WhileThePageSentenceIsGerman()
    {
        StringTable.Activate(BuildGermanSettingsTable());
        try
        {
            var logger = new RecordingLogger();
            var service = new CalibrationService(logger);
            var path = SaveCalibrationWithoutAMonitorName();
            try
            {
                service.Load(path, new PhysicalPoint(3840, 2400), "\\\\.\\DISPLAY2");

                // Self-check: the German surface really rendered, so the log assertion below is
                // not passing vacuously on an English table.
                Check.That(service.LastLoadRejection).Contains("keine");

                Check.WithCustomMessage(
                        "the log must stay English; it read: " + string.Join(" | ", logger.Messages))
                    .That(logger.Messages.Any(m => m.Contains("(none)", StringComparison.Ordinal)))
                    .IsTrue();
                Check.WithCustomMessage(
                        "a German \"keine\" leaked into the log: " + string.Join(" | ", logger.Messages))
                    .That(logger.Messages.Any(m => m.Contains("keine", StringComparison.Ordinal)))
                    .IsFalse();
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The shipped English table plus a complete German <c>settings</c> surface, where only the
    /// "(none)" word differs — the minimum needed for the surface to render German at all.
    /// </summary>
    private static StringTable BuildGermanSettingsTable()
    {
        var englishJson = ReadEmbeddedEnglishJson();
        var englishNode = (JsonObject)JsonNode.Parse(englishJson)!;
        var german = new JsonObject();
        foreach (var (key, node) in englishNode)
        {
            if (key.StartsWith("_", StringComparison.Ordinal)) continue;
            if (node is not JsonObject entry) continue;
            if (entry["where"]?.GetValue<string>() != "settings") continue;

            german[key] = new JsonObject
            {
                ["text"] = key == "settings.calibration.none"
                    ? JsonValue.Create("keine")
                    : entry["text"]?.DeepClone(),
            };
        }

        return StringTable.Create(
            UiLanguage.German,
            CultureInfo.GetCultureInfo("de-DE"),
            StringTableContent.ParseEnglish(englishJson),
            StringTableContent.ParseGerman(german.ToJsonString()),
            germanSurfaces: ["settings"],
            warn: null);
    }

    private static string ReadEmbeddedEnglishJson()
    {
        using var stream = typeof(StringTable).Assembly
            .GetManifestResourceStream(StringTable.EmbeddedResourceName)
            ?? throw new InvalidOperationException("the embedded English table is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Valid calibration data whose saved monitor name and id are both empty.</summary>
    private static string SaveCalibrationWithoutAMonitorName()
    {
        var screen = new PhysicalPoint(3840, 2400);
        var service = new CalibrationService();
        service.StartSession(3, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (var i = 0; i < 3; i++)
        {
            service.BeginTarget(i);
            for (var j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }

        var data = service.CompleteSession(screen, "");
        var path = Path.GetTempFileName();
        service.Save(path, data);
        return path;
    }

    private sealed class RecordingLogger : ILogger<CalibrationService>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
