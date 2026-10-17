namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;

/// <summary>
/// The 0.5B option's retirement against a REAL settings file (the handoff's rule: a settings change
/// gets a real old-file round trip). The manifest entry is gone, but an install may still store
/// <c>"mlModelSize": "0.5B"</c> — <see cref="KeyboardSettings.MLModelSize"/> has no setter guard and
/// no <c>Validate()</c> entry (custom model ids are data, not a closed set; pinned at the unit level
/// by <c>KeyboardSettingsTests.MLModelSize_AnyValue_Accepted</c>), so the load must keep it and every
/// witness around it. <para>
/// <b>What actually kills the mutants, measured (review 02):</b> a guard or <c>Validate()</c> entry
/// today reverts ONLY this property — the #153 drop-one repair takes it to <c>"360M"</c> — and every
/// witness (layout, suggestions count, the two flags, dwell, language, startPaused, opacity) stays
/// intact. So the teeth are the two <c>MLModelSize == "0.5B"</c> assertions; the witnesses catch the
/// blunter failure a whole-section reset would be, which neither realistic mutant shape causes.
/// </para>
/// </summary>
public class MlModelSizeRetirementFileTests
{
    [Fact]
    public void AStoredRetiredModelSize_LoadsUntouched_WithEveryWitnessIntact_AndRoundTrips()
    {
        var path = TempFile();
        try
        {
            // The spellings the app itself writes (camelCase policy): mlModelSize,
            // mlSuggestionsEnabled, preferGPU.
            File.WriteAllText(path, """
            {
              "keyboard": {
                "activeLayout": "AZERTY",
                "maxSuggestions": 3,
                "mlModelSize": "0.5B",
                "mlSuggestionsEnabled": false,
                "preferGPU": false
              },
              "dwell": { "activationButtonDwellMs": 850 },
              "general": { "uiLanguage": "german", "startPaused": true },
              "backdrop": { "enabled": false, "type": "solidDarken", "opacity": 0.45 }
            }
            """);

            var service = new SettingsService(path);
            var loaded = service.Load();

            // The retired id itself: kept as data, not reset to the default ("360M").
            Check.That(loaded.Keyboard.MLModelSize).IsEqualTo("0.5B");
            // The keyboard section's other values - the proof the section was not reset.
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Keyboard.MaxSuggestions).IsEqualTo(3);
            Check.That(loaded.Keyboard.MLSuggestionsEnabled).IsFalse();
            Check.That(loaded.Keyboard.PreferGPU).IsFalse();
            // And the file as a whole.
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
            Check.That(loaded.General.UiLanguage).IsEqualTo(UiLanguage.German);
            Check.That(loaded.General.StartPaused).IsTrue();
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);

            // A save (which validates first) keeps the stored id too: retiring an option must not
            // rewrite or drop the value on installs that chose it.
            service.Save(loaded);
            Check.That(service.Load().Keyboard.MLModelSize).IsEqualTo("0.5B");
        }
        finally { File.Delete(path); }
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"eyectrl_mlsize_{Guid.NewGuid()}.json");
}
