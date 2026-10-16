namespace EyeCtrl.Core.Tests.Settings;

using System.Globalization;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;

/// <summary>
/// The widened 0.0-0.95 opacity range against a REAL settings file (the handoff's rule: every
/// settings change gets a real old-file round trip). The P14 pattern: a hand-written file carrying
/// non-default witnesses — dwell 850, AZERTY, the German language, StartPaused, and the backdrop's
/// own non-defaults (enabled false, Solid darken) — loaded through <see cref="SettingsService"/>,
/// so a value that failed to load would show as the witnesses reverting, not just as an opacity
/// number. Widening a guard is safe for existing files (0.1-0.9 stays valid); the new end values
/// are what these rows pin.
/// </summary>
public class BackdropOpacityRangeFileTests
{
    [Theory]
    [InlineData("0.0")]
    [InlineData("0")]      // the integer zero, the way a hand-edit writes "none"
    [InlineData("0.95")]
    public void AHandWrittenEndValue_LoadsAsWritten_WithEveryWitnessIntact(string rawValue)
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, WitnessFile(rawValue));

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Opacity)
                .IsEqualTo(double.Parse(rawValue, CultureInfo.InvariantCulture));
            AssertWitnesses(loaded);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AValueAboveTheRange_CostsOnlyTheOpacity_WithEveryWitnessIntact()
    {
        // 0.96 is outside the widened ceiling: the #153 drop-one verdict costs exactly the one
        // invalid property (the section's sibling values survive), never the file.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, WitnessFile("0.96"));
            string before = File.ReadAllText(path);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.3);                    // dropped -> default
            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.SolidDarken);  // the section's sibling
            AssertWitnesses(loaded);
            Check.That(File.ReadAllText(path)).IsEqualTo(before);                  // repairs are in-memory (D6)
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.95)]
    public void SaveThenLoad_RoundTripsTheEndValue(double opacity)
    {
        var path = TempFile();
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Backdrop.Enabled = false;
            settings.Backdrop.Type = BackdropType.SolidDarken;
            settings.Backdrop.Opacity = opacity;
            settings.Dwell.ActivationButtonDwellMs = 850;
            settings.Keyboard.ActiveLayout = "AZERTY";
            settings.Keyboard.LanguagePack = "de";
            settings.Keyboard.MaxSuggestions = 3;
            settings.General.UiLanguage = UiLanguage.German;
            settings.General.StartPaused = true;

            var service = new SettingsService(path);
            service.Save(settings);          // validates first: the widened range must pass Save too

            var loaded = service.Load();

            Check.That(loaded.Backdrop.Opacity).IsEqualTo(opacity);
            AssertWitnesses(loaded);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The witnesses every row asserts: any of them reverting means more than the opacity
    /// was lost, which is the failure this file-level round trip exists to catch.</summary>
    private static void AssertWitnesses(EyeCtrlSettings loaded)
    {
        Check.That(loaded.Backdrop.Enabled).IsFalse();
        Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.SolidDarken);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
        Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
        Check.That(loaded.Keyboard.LanguagePack).IsEqualTo("de");
        Check.That(loaded.Keyboard.MaxSuggestions).IsEqualTo(3);
        Check.That(loaded.General.UiLanguage).IsEqualTo(UiLanguage.German);
        Check.That(loaded.General.StartPaused).IsTrue();
    }

    private static string WitnessFile(string rawOpacity) =>
        $$"""
        {
          "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 500 },
          "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
          "general": { "uiLanguage": "german", "startPaused": true },
          "backdrop": { "enabled": false, "type": "solidDarken", "opacity": {{rawOpacity}} }
        }
        """;

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"eyectrl_opacityrange_{Guid.NewGuid()}.json");
}
