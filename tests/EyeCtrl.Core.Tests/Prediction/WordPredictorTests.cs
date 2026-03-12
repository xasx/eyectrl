namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class WordPredictorTests
{
    private static WordPredictor CreatePredictor(Dictionary<string, int> words)
    {
        return new WordPredictor(new InMemoryDictionaryProvider(words));
    }

    [Fact]
    public void GetSuggestions_MatchesPrefix()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 100 }, { "help", 80 }, { "world", 50 }, { "heap", 30 }
        });

        var results = predictor.GetSuggestions("hel", maxCount: 5);

        Assert.Contains("hello", results);
        Assert.Contains("help", results);
    }

    [Fact]
    public void GetSuggestions_OrderedByFrequency()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "they", 500 }, { "there", 300 }, { "then", 200 }
        });

        var results = predictor.GetSuggestions("the", maxCount: 3);

        Assert.Equal("the", results[0]);
        Assert.Equal("they", results[1]);
        Assert.Equal("there", results[2]);
    }

    [Fact]
    public void LearnWord_IncreasesFrequency()
    {
        var predictor = CreatePredictor(new()
        {
            { "test", 10 }, { "team", 100 }
        });

        // "test" starts lower than "team"
        var before = predictor.GetSuggestions("te", maxCount: 2);
        Assert.Equal("team", before[0]);

        // Learn "test" many times
        for (int i = 0; i < 200; i++)
            predictor.LearnWord("test");

        var after = predictor.GetSuggestions("te", maxCount: 2);
        Assert.Equal("test", after[0]);
    }

    [Fact]
    public void GetSuggestions_EmptyPrefix_ReturnsTopWords()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "a", 900 }, { "is", 800 }
        });

        var results = predictor.GetSuggestions("", maxCount: 3);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void GetSuggestions_FuzzyAdjacentKey_SuggestsIntendedWord()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "rhe", 1 }, { "world", 500 }
        });

        var results = predictor.GetSuggestions("rhe", maxCount: 5);
        Assert.Contains("the", results);
    }

    [Fact]
    public void GetSuggestions_ExactPrefixRanksAboveFuzzy_WhenFrequencyEqual()
    {
        var predictor = CreatePredictor(new()
        {
            { "there", 500 }, { "the", 500 }
        });

        var results = predictor.GetSuggestions("the", maxCount: 5);
        Assert.Contains("there", results);
        Assert.Contains("the", results);
    }

    [Fact]
    public void GetSuggestions_HighFrequencyFuzzyBeatsLowFrequencyExact()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 10000 }, { "rheumatic", 1 }
        });

        var results = predictor.GetSuggestions("rhe", maxCount: 5);
        Assert.Equal("the", results[0]);
    }

    [Fact]
    public void GetSuggestions_NonAdjacentTypo_StillSuggestsWithinThreshold()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }
        });

        var results = predictor.GetSuggestions("she", maxCount: 5);
        Assert.Contains("the", results);
    }

    [Fact]
    public void GetSuggestions_FarTypo_NotSuggested()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }
        });

        var results = predictor.GetSuggestions("xyz", maxCount: 5);
        Assert.DoesNotContain("the", results);
    }
}

// Test helper
public class InMemoryDictionaryProvider : IDictionaryProvider
{
    private readonly Dictionary<string, int> _words;
    public InMemoryDictionaryProvider(Dictionary<string, int> words) => _words = words;
    public Dictionary<string, int> Load() => new(_words);
    public void Save(Dictionary<string, int> words) { }
}
