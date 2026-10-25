namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// The landing guard (fixup 02) loads ON from a settings file written before the key existed: it is
/// a NEW property with a default initialiser, so an old settings.json must neither fail nor
/// silently turn the protection off. Sibling values prove the section loaded normally.
/// </summary>
public class LandingGuardDefaultFileTests
{
    [Fact]
    public void AnOldSettingsFileWithoutTheKey_LoadsTheGuardOn_AndKeepsEveryOtherValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_landingguard_{Guid.NewGuid()}.json");
        try
        {
            // The spelling the app writes (camelCase policy), with no landingGuardAfterUnYield.
            File.WriteAllText(path, """
            {
              "keyboard": {
                "activeLayout": "AZERTY",
                "clickSoundEnabled": false,
                "showLMLoadingIndicator": false
              },
              "dwell": { "activationButtonDwellMs": 850 }
            }
            """);

            // The real load path the app uses - SettingsService.Load, not a bare JsonSerializer.
            var loaded = new SettingsService(path).Load();

            Check.WithCustomMessage("an old settings file must load the new guard ON")
                .That(loaded.Keyboard.LandingGuardAfterUnYield).IsTrue();
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Keyboard.ClickSoundEnabled).IsFalse();
            Check.That(loaded.Keyboard.ShowLMLoadingIndicator).IsFalse();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
        }
        finally { File.Delete(path); }
    }
}
