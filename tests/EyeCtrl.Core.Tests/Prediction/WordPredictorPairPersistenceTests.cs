namespace EyeCtrl.Core.Tests.Prediction;

using System.IO;
using System.Text.Json;
using EyeCtrl.Core.Prediction;

public class WordPredictorPairPersistenceTests : IDisposable
{
    private readonly string _dir;

    public WordPredictorPairPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eyectrl_pairs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private FileDictionaryProvider Provider()
        => new(Path.Combine(_dir, "base_dictionary_en.json"), _dir, "en");

    [Fact]
    public void LearnedPair_SurvivesSaveAndReload()
    {
        var predictor = new WordPredictor(Provider());
        predictor.LearnWord("möchte");
        predictor.LearnPair("ich", "möchte");
        predictor.LearnPair("ich", "möchte");
        predictor.SaveUserDictionary();

        var reloaded = new WordPredictor(Provider());
        var words = reloaded.GetSuggestions("", "Ich", 10).Select(result => result.Word).ToList();

        Check.That(words).Contains("möchte");
    }

    [Fact]
    public void MixedCaseLegacyBigramFile_LoadsAndMerges()
    {
        var bigramPath = Path.Combine(_dir, "user_dictionary_en_bigrams.json");
        File.WriteAllText(bigramPath, JsonSerializer.Serialize(
            new Dictionary<string, Dictionary<string, int>>
            {
                ["Ich"] = new() { ["möchte"] = 1 },
                ["ich"] = new() { ["Möchte"] = 1 },
            }));
        // The next-word category gates on the dictionary (a bigram can outlive its word), so the
        // words this otherwise-legacy file references must be known to the predictor.
        File.WriteAllText(Path.Combine(_dir, "user_dictionary_en.json"),
            JsonSerializer.Serialize(new Dictionary<string, int> { ["ich"] = 1, ["möchte"] = 1 }));

        var predictor = new WordPredictor(Provider());
        var words = predictor.GetSuggestions("", "ICH", 10).Select(result => result.Word).ToList();

        // The two legacy keys merge into one normalized key and reach MinFrequency.
        Check.That(words).Contains("möchte");
    }
}
