namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// Round-button B1: the 1.3.0 sitting build (#322, never merged) wrote two dwell keys that no
/// longer exist — <c>dwell.activationStyle</c> and <c>dwell.restFillMs</c> — and the owner's real
/// settings.json now carries them (<c>"activationStyle": "offerButton"</c>, <c>"restFillMs": 700</c>,
/// wrapup-4). Loading such a file through the REAL load path must treat both as ordinary unknown
/// JSON members: no repair, no section reset, no wipe, no backup, every other value kept — and a
/// save afterwards must succeed and load back intact. A temp file only; never the real profile.
/// <para>
/// The trap this pins (memories "a tightened guard wipes the file" / "JsonNode duplicate key
/// throws"): if the load ever rejects unknown keys, the dwell section fails standalone, the
/// drop-one repair cannot fix TWO unknown keys, and the whole dwell section reverts to defaults —
/// the owner's 500 ms activator and R 90 among them.
/// </para>
/// </summary>
public class RestActivationRetirementFileTests
{
    // Box-wide named-kernel-object rule (AGENTS.md): tests that persist never take the
    // production writer mutex — each constructs its service with a GUID-suffixed name.
    private static string TestMutexName() =>
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    private sealed class RecordingLogger : ILogger<SettingsService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // The dwell section in the shape 1.3.0 serialised it (camelCase policy, the string-enum
    // converter's camelCase member name), with the two retired keys between real ones and
    // non-default witnesses around them; plus a witness in another section and a root scalar.
    private const string File130 = """
        {
          "dwell": {
            "fixationDetectionMs": 350,
            "activationButtonDwellMs": 500,
            "activationStyle": "offerButton",
            "restFillMs": 700,
            "actionMenuDwellMs": 800,
            "fixationRadiusPixels": 90
          },
          "keyboard": { "activeLayout": "AZERTY" },
          "selectionMode": "manualSelection"
        }
        """;

    [Fact]
    public void A130SettingsFile_WithActivationStyleAndRestFillMs_LoadsWithNoRepairAndSavesBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"eyectrl_restretire_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, File130);
            var logger = new RecordingLogger();
            var service = new SettingsService(path, SettingsWriter.App, TestMutexName(), logger);

            var loaded = service.Load();

            // Every dwell value around the retired keys survives: the section was neither
            // drop-one repaired nor reset.
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(500);
            Check.That(loaded.Dwell.FixationRadiusPixels).IsEqualTo(90.0);
            Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(350);
            Check.That(loaded.Dwell.ActionMenuDwellMs).IsEqualTo(800);
            // Away from the keys: another section and a root scalar.
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.SelectionMode).IsEqualTo(SelectionMode.ManualSelection);

            // No reset, wipe or repair was even attempted: the load logged nothing at Warning or
            // above (every repair, reset and defaults path logs), wrote nothing back (the file is
            // byte-identical) and left no backup beside it.
            Check.WithCustomMessage("the load must take the intact fast path; logged: "
                    + string.Join(" | ", logger.Entries.Select(e => $"{e.Level}: {e.Message}")))
                .That(logger.Entries.Any(e => e.Level >= LogLevel.Warning)).IsFalse();
            Check.That(File.ReadAllText(path).Equals(File130, StringComparison.Ordinal)).IsTrue();
            Check.That(Directory.GetFiles(dir).Select(Path.GetFileName)).ContainsExactly("settings.json");

            // A save after the load succeeds, and what it wrote loads back with the values intact.
            service.Save(loaded);
            var reloaded = new SettingsService(path, SettingsWriter.App, TestMutexName(), logger).Load();
            Check.That(reloaded.Dwell.ActivationButtonDwellMs).IsEqualTo(500);
            Check.That(reloaded.Dwell.FixationRadiusPixels).IsEqualTo(90.0);
            Check.That(reloaded.Dwell.FixationDetectionMs).IsEqualTo(350);
            Check.That(reloaded.Dwell.ActionMenuDwellMs).IsEqualTo(800);
            Check.That(reloaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(reloaded.SelectionMode).IsEqualTo(SelectionMode.ManualSelection);
            Check.That(logger.Entries.Any(e => e.Level >= LogLevel.Warning)).IsFalse();
            Check.That(Directory.GetFiles(dir).Select(Path.GetFileName)).ContainsExactly("settings.json");

            // The properties are gone from the model, not merely unread: re-adding them would put
            // the rest style back on the serialization surface.
            Check.That(typeof(DwellSettings).GetProperty("ActivationStyle")).IsNull();
            Check.That(typeof(DwellSettings).GetProperty("RestFillMs")).IsNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
