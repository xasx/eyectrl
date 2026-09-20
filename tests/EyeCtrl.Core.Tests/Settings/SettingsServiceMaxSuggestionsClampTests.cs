namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json.Nodes;
using EyeCtrl.Core.Settings;

/// <summary>
/// MaxSuggestions is a brand-new, hand-editable int — the likeliest single value a user mistypes
/// in settings.json. Load's validation branch answers an out-of-range value by discarding the
/// WHOLE file (issue #153's family), so the value is clamped on load instead, the same bargain
/// ClampCursorFilter/SanitizeLanguagePack already make. New file (not appended to
/// SettingsServiceTests) because the live edge-toggle branch appends its own tests at that
/// file's end.
/// </summary>
public class SettingsServiceMaxSuggestionsClampTests
{
    [Fact]
    public void Load_ClampsOutOfRangeMaxSuggestions_InsteadOfResettingEverything()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            var service = new SettingsService(tempPath);
            service.Save(settings);

            // Hand-edit the file the way a user would: one out-of-range count.
            var root = JsonNode.Parse(File.ReadAllText(tempPath))!.AsObject();
            root["keyboard"]!["maxSuggestions"] = 9;
            File.WriteAllText(tempPath, root.ToJsonString());

            var loaded = service.Load();
            Check.That(loaded.Keyboard.MaxSuggestions).IsEqualTo(5);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_ClampsZeroMaxSuggestions_ToTheFloorOfOne()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            var service = new SettingsService(tempPath);
            service.Save(settings);

            var root = JsonNode.Parse(File.ReadAllText(tempPath))!.AsObject();
            root["keyboard"]!["maxSuggestions"] = 0;
            File.WriteAllText(tempPath, root.ToJsonString());

            var loaded = service.Load();
            Check.That(loaded.Keyboard.MaxSuggestions).IsEqualTo(1);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
        }
        finally { File.Delete(tempPath); }
    }
}
