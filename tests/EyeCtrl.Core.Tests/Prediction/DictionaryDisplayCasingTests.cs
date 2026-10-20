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
    public void GetSuggestions_GenuinelyDifferentLearnedCasing_WinsOverTheBase()
    {
        // Mixed case survives LearnWord as typed (it is not ALL-CAPS), so it outranks the base.
        var predictor = CreatePredictor();
        predictor.LearnWord("WeTter");

        var results = predictor.GetSuggestions("wet", null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("WeTter");
    }

    [Fact]
    public void GetSuggestions_BaseAcronyms_SurviveOneSelection()
    {
        // LearnWord title-cases ALL-CAPS words that are not in KnownAcronyms ("DDR" -> "Ddr");
        // that normalized learned form must not hide the base acronym on the next render.
        var words = new Dictionary<string, int>
        {
            ["ddr"] = 100, ["ki"] = 90, ["ok"] = 80, ["uk"] = 70, ["e-mail"] = 60,
        };
        var casing = new Dictionary<string, string>
        {
            ["ddr"] = "DDR", ["ki"] = "KI", ["ok"] = "OK", ["uk"] = "UK", ["e-mail"] = "E-Mail",
        };
        var predictor = CreatePredictor(words, casing);

        var cases = new (string Prefix, string Expected)[]
        {
            ("dd", "DDR"), ("ki", "KI"), ("ok", "OK"), ("uk", "UK"), ("e-m", "E-Mail"),
        };

        foreach (var (prefix, expected) in cases)
        {
            var before = predictor.GetSuggestions(prefix, null, maxCount: 5);
            Check.WithCustomMessage($"before selecting '{prefix}'").That(before[0].Word).IsEqualTo(expected);

            predictor.LearnWord(before[0].Word);

            var after = predictor.GetSuggestions(prefix, null, maxCount: 5);
            Check.WithCustomMessage($"after selecting '{expected}'").That(after[0].Word).IsEqualTo(expected);
        }
    }

    [Fact]
    public void GetSuggestions_NextWordWithBigrams_CapitalisedNounKeepsItsRank()
    {
        // The next-word branch gets its case tier from the stored key too: with the display form
        // "Wetter" it would score 5000 x 0.8 and lose to "wetten" (4500 x 1.2).
        var predictor = CreatePredictor(new Dictionary<string, int> { ["wetter"] = 100, ["wetten"] = 90 });
        for (var i = 0; i < 10; i++)
        {
            predictor.LearnSequence(["das", "wetter"]);
            predictor.LearnSequence(["das", "wetten"]);
        }

        var results = predictor.GetSuggestions("we", context: "das", maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("Wetter");
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
