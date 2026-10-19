namespace EyeCtrl.Core.Tests.Prediction;

using System.IO;
using System.Text.Json;
using EyeCtrl.Core.Prediction;

/// <summary>
/// German nouns are stored lowercase and get their display form from the base dictionary's
/// "casing" object. The learned casing preference still wins, and sentence-start capitalisation
/// still applies on top.
/// </summary>
public class DictionaryDisplayCasingTests : IDisposable
{
    private readonly string _dir;

    public DictionaryDisplayCasingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dict_casing_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private WordPredictor CreatePredictor()
    {
        var basePath = Path.Combine(_dir, "de.json");
        File.WriteAllText(basePath, JsonSerializer.Serialize(new
        {
            words = new Dictionary<string, int> { ["wetter"] = 100, ["wettkampf"] = 50, ["wettbewerb"] = 40 },
            casing = new Dictionary<string, string> { ["wetter"] = "Wetter" },
            abbreviations = Array.Empty<string>(),
        }));

        var provider = new FileDictionaryProvider(basePath, _dir, "de");
        return new WordPredictor(provider, "QWERTZ");
    }

    [Fact]
    public void GetSuggestions_BaseCasing_DisplaysWetter()
    {
        var predictor = CreatePredictor();

        var results = predictor.GetSuggestions("wet", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("Wetter");
    }

    [Fact]
    public void GetSuggestions_LearnedLowercasePreference_OverridesTheBaseCasing()
    {
        var predictor = CreatePredictor();
        predictor.LearnWord("wetter");

        var results = predictor.GetSuggestions("wet", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("wetter");
    }

    [Fact]
    public void GetSuggestions_WordWithoutACasingEntry_StaysAsStored()
    {
        var predictor = CreatePredictor();

        var results = predictor.GetSuggestions("wettk", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("wettkampf");
    }

    [Fact]
    public void GetSuggestions_SentenceStart_CapitalisesOnTopOfTheDisplayForm()
    {
        var predictor = CreatePredictor();

        var results = predictor.GetSuggestions("wettk", null, maxCount: 5, fullText: "Guten Morgen. ");

        Check.That(results[0].Word).IsEqualTo("Wettkampf");
    }
}
