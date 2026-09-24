namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

/// <summary>
/// Issue #159: GetSuggestions' optional maxCount must default to the same constant the
/// settings range-check, the load clamp and the Keyboard tab's slider all use. A hand-copied
/// literal in the signature is exactly the silent divergence the issue filed — the production
/// path passes the setting explicitly, so a stale default would only surface when a future
/// caller omits the argument. Reflection keeps the pin honest: reverting the default to a
/// literal (or deleting it) fails here as soon as it disagrees with the ceiling.
/// </summary>
public class WordPredictorMaxSuggestionsDefaultTests
{
    [Fact]
    public void GetSuggestions_MaxCountDefault_IsTheSettingsCeiling()
    {
        var method = typeof(WordPredictor).GetMethod(
            nameof(WordPredictor.GetSuggestions),
            [typeof(string), typeof(string), typeof(int), typeof(string)]);

        Check.That(method).IsNotNull();

        var maxCount = method!.GetParameters().Single(p => p.Name == "maxCount");
        Check.That(maxCount.HasDefaultValue).IsTrue();
        Check.That((int)maxCount.DefaultValue!).IsEqualTo(KeyboardSettings.MaxSuggestionsCeiling);
    }
}
