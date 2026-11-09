namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// F2's <see cref="DiagnosticsSettings.LogRingEvents"/>: a NEW settings-file key
/// (<c>diagnostics.logRingEvents</c>, default false, no settings row) that turns on the ring's event
/// lines without <c>--ring-arm</c>. An old file without it loads unchanged.
/// </summary>
public class LogRingEventsSettingTests
{
    [Fact]
    public void LogRingEvents_DefaultsToFalse_AndValidates()
    {
        var diagnostics = new DiagnosticsSettings();

        Check.That(diagnostics.LogRingEvents).IsFalse();
        Check.That(diagnostics.Validate()).IsEmpty();
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKey_LoadsFalse_AndKeepsEveryOtherValue()
    {
        // Mutation: a default of true, or a validation that rejects the missing key (Load then falls
        // back to defaults and the other values are lost) → red.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_logringevents_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "dwell": { "activationButtonDwellMs": 900 },
              "diagnostics": { "recordRawGaze": true, "measureUiInstruments": true }
            }
            """);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Diagnostics.LogRingEvents).IsFalse();
            Check.That(loaded.Diagnostics.RecordRawGaze).IsTrue();
            Check.That(loaded.Diagnostics.MeasureUiInstruments).IsTrue();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(900);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheKey_IsReadFromTheFile_UnderItsCamelCaseName()
    {
        // The sitting prep writes the key by hand: its spelling is the contract.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_logringevents_on_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "diagnostics": { "logRingEvents": true }
            }
            """);

            Check.That(new SettingsService(path).Load().Diagnostics.LogRingEvents).IsTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheValue_RoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_logringevents_rt_{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            var settings = new EyeCtrlSettings();
            settings.Diagnostics.LogRingEvents = true;
            service.Save(settings);

            Check.That(File.ReadAllText(path)).Contains("\"logRingEvents\": true");
            Check.That(service.Load().Diagnostics.LogRingEvents).IsTrue();
        }
        finally { File.Delete(path); }
    }
}
