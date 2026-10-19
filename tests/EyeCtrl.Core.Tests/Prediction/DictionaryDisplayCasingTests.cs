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

    private WordPredictor CreatePredictor(
        Dictionary<string, int>? words = null, Dictionary<string, string>? casing = null)
    {
        var basePath = Path.Combine(_dir, "de.json");
        File.WriteAllText(basePath, JsonSerializer.Serialize(new
        {
            words = words ?? new Dictionary<string, int> { ["wetter"] = 100, ["wettkampf"] = 50, ["wettbewerb"] = 40 },
            casing = casing ?? new Dictionary<string, string> { ["wetter"] = "Wetter" },
            abbreviations = Array.Empty<string>(),
        }));

        var provider = new FileDictionaryProvider(basePath, _dir, "de");
        return new WordPredictor(provider, "QWERTZ");
    }

    [Fact]
    public void GetSuggestions_BaseCasing_DoesNotDemoteTheNounInTheRanking()
    {
        // If the tier used the display form, "Wetter" would score 100 x 0.8 and "wetten"
        // 90 x 1.2, flipping the order; the tier uses the stored key.
        var predictor = CreatePredictor(new Dictionary<string, int> { ["wetter"] = 100, ["wetten"] = 90 });

        var results = predictor.GetSuggestions("wet", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("Wetter");
    }

    [Fact]
    public void GetSuggestions_BaseCasing_DisplaysWetter()
    {
        var predictor = CreatePredictor();

        var results = predictor.GetSuggestions("wet", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("Wetter");
    }

    [Fact]
    public void GetSuggestions_LearnedLowercasePreference_GivesWayToTheBaseCapital()
    {
        // The owner's history is mostly lowercase ("wetter"); the base capital still shows.
        var predictor = CreatePredictor();
        predictor.LearnWord("wetter");

        var results = predictor.GetSuggestions("wet", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("Wetter");
    }

    [Fact]
    public void GetSuggestions_LearnedNonLowercaseCasing_WinsOverTheBase()
    {
        // Any learned form other than the bare lowercase key still wins (LearnWord normalizes
        // ALL-CAPS to title case, so "WLAN" is recorded as "Wlan").
        var predictor = CreatePredictor(
            new Dictionary<string, int> { ["wlan"] = 100 },
            new Dictionary<string, string> { ["wlan"] = "WLAN" });
        predictor.LearnWord("WLAN");

        var results = predictor.GetSuggestions("wla", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("Wlan");
    }

    [Fact]
    public void GetSuggestions_LearnedLowercaseForAWordWithoutBaseCasing_StaysLowercase()
    {
        var predictor = CreatePredictor(
            new Dictionary<string, int> { ["wettkampf"] = 100 },
            new Dictionary<string, string>());
        predictor.LearnWord("wettkampf");

        var results = predictor.GetSuggestions("wettk", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("wettkampf");
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
