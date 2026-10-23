namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// The retired keyboard key <c>keyboard.emojiSuggestionsEnabled</c> against a REAL settings file.
/// Nothing ever read it (no settings row, no feature), so it is gone from
/// <see cref="KeyboardSettings"/> - but an install that saved the default still carries the key
/// on disk. Loading must treat it as the ordinary unknown JSON member: every other value
/// survives and nothing resets. Strictness over an inert key (or a whole-file wipe) would cost
/// the user settings that do matter.
/// </summary>
public class EmojiSuggestionsRetirementFileTests
{
    [Fact]
    public void AStoredEmojiSuggestionsKey_LoadsWithoutAThrow_AndEveryOtherValueSurvives()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_emojisuggestions_{Guid.NewGuid()}.json");
        try
        {
            // The spelling the app wrote (camelCase policy), surrounded by non-default values in
            // the same section, another section, and the keyboard section's neighbours.
            File.WriteAllText(path, """
            {
              "keyboard": {
                "emojiSuggestionsEnabled": true,
                "activeLayout": "AZERTY",
                "maxSuggestions": 3,
                "clickSoundEnabled": false,
                "emojiSkinTone": 4
              },
              "dwell": { "activationButtonDwellMs": 850 },
              "general": { "startPaused": true }
            }
            """);

            // The real load path the app uses - SettingsService.Load, not a bare JsonSerializer.
            var loaded = new SettingsService(path).Load();

            // The section carrying the retired key: every value around it survives, so the key
            // was ignored instead of costing its neighbours (the #153 drop-one verdict) or the
            // whole file (the pre-#153 wipe).
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Keyboard.MaxSuggestions).IsEqualTo(3);
            Check.That(loaded.Keyboard.ClickSoundEnabled).IsFalse();
            Check.That(loaded.Keyboard.EmojiSkinTone).IsEqualTo(4);
            // Away from the key: another section and a root scalar, both untouched.
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
            Check.That(loaded.General.StartPaused).IsTrue();

            // The property is gone from the model, not merely unread: re-adding it would put the
            // retired key back on the serialization surface.
            Check.That(typeof(KeyboardSettings).GetProperty("EmojiSuggestionsEnabled")).IsNull();
        }
        finally { File.Delete(path); }
    }
}
