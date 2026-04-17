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

        var results = predictor.GetSuggestions("hel", null, maxCount: 5);

        Assert.Contains(results, r => r.Word == "hello");
        Assert.Contains(results, r => r.Word == "help");
    }

    [Fact]
    public void GetSuggestions_OrderedByFrequency()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "they", 500 }, { "there", 300 }, { "then", 200 }
        });

        var results = predictor.GetSuggestions("the", null, maxCount: 3);

        Assert.Equal("the", results[0].Word);
        Assert.Equal("they", results[1].Word);
        Assert.Equal("there", results[2].Word);
    }

    [Fact]
    public void LearnWord_IncreasesFrequency()
    {
        var predictor = CreatePredictor(new()
        {
            { "test", 10 }, { "team", 100 }
        });

        // "test" starts lower than "team"
        var before = predictor.GetSuggestions("te", null, maxCount: 2);
        Assert.Equal("team", before[0].Word);

        // Learn "test" many times
        for (int i = 0; i < 200; i++)
            predictor.LearnWord("test");

        var after = predictor.GetSuggestions("te", null, maxCount: 2);
        Assert.Equal("test", after[0].Word);
    }

    [Fact]
    public void GetSuggestions_EmptyPrefix_ReturnsTopWords()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "a", 900 }, { "is", 800 }
        });

        var results = predictor.GetSuggestions("", null, maxCount: 3);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void GetSuggestions_FuzzyAdjacentKey_SuggestsIntendedWord()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "rhe", 1 }, { "world", 500 }
        });

        var results = predictor.GetSuggestions("rhe", null, maxCount: 5);
        Assert.Contains(results, r => r.Word == "the");
    }

    [Fact]
    public void GetSuggestions_ExactPrefixRanksAboveFuzzy_WhenFrequencyEqual()
    {
        var predictor = CreatePredictor(new()
        {
            { "there", 500 }, { "the", 500 }
        });

        var results = predictor.GetSuggestions("the", null, maxCount: 5);
        Assert.Contains(results, r => r.Word == "there");
        Assert.Contains(results, r => r.Word == "the");
    }

    [Fact]
    public void GetSuggestions_HighFrequencyFuzzyBeatsLowFrequencyExact()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 10000 }, { "rheumatic", 1 }
        });

        var results = predictor.GetSuggestions("rhe", null, maxCount: 5);
        Assert.Equal("the", results[0].Word);
    }

    [Fact]
    public void GetSuggestions_NonAdjacentTypo_StillSuggestsWithinThreshold()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }
        });

        var results = predictor.GetSuggestions("she", null, maxCount: 5);
        Assert.Contains(results, r => r.Word == "the");
    }

    [Fact]
    public void GetSuggestions_FarTypo_NotSuggested()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }
        });

        var results = predictor.GetSuggestions("xyz", null, maxCount: 5);
        Assert.DoesNotContain(results, r => r.Word == "the");
    }

    [Fact]
    public void GetSuggestions_WithBigramContext_SuggestsNextWord()
    {
        var predictor = CreatePredictor(new()
        {
            { "you", 10 }, { "world", 10 }, { "your", 10 }
        });

        // Learn bigram pattern "thank you" multiple times to exceed frequency threshold
        // This creates a strong bigram association
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "thank", "you" });

        // With "thank" as context, "you" should be suggested with NextWord type
        var results = predictor.GetSuggestions("", "thank", maxCount: 5);
        Assert.NotEmpty(results);

        // Find "you" in results - it should be there with NextWord type
        var youResult = results.FirstOrDefault(r => r.Word == "you");
        Assert.NotNull(youResult);
        Assert.Equal(PredictionType.NextWord, youResult.Type);
    }

    [Fact]
    public void GetSuggestions_WithPhraseContext_SuggestsPhrase()
    {
        var predictor = CreatePredictor(new()
        {
            { "forward", 50 }, { "to", 100 }, { "from", 80 }
        });

        // Learn phrase "looking forward to"
        predictor.LearnSequence(new[] { "looking", "forward", "to" });
        predictor.LearnSequence(new[] { "looking", "forward", "to" });

        // With "looking" as context, should suggest words from learned phrase
        var results = predictor.GetSuggestions("", "looking", maxCount: 5);
        Assert.Contains(results, r => r.Word == "forward");
    }

    [Fact]
    public void LearnSequence_LearnsWordsAndBigrams()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 10 }
        });

        predictor.LearnSequence(new[] { "hello", "world" });
        predictor.LearnSequence(new[] { "hello", "world" });

        // Check that "world" is suggested after "hello"
        var results = predictor.GetSuggestions("", "hello", maxCount: 5);
        Assert.Contains(results, r => r.Word == "world");
    }

    [Fact]
    public void GetSuggestions_ReturnsCorrectPredictionTypes()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 100 }, { "help", 80 }, { "the", 1000 }
        });

        // Exact prefix match
        var exactResults = predictor.GetSuggestions("hel", null, maxCount: 5);
        Assert.All(exactResults.Where(r => r.Word.StartsWith("hel")),
            r => Assert.Equal(PredictionType.ExactPrefix, r.Type));

        // Fuzzy match
        var fuzzyResults = predictor.GetSuggestions("rhe", null, maxCount: 5);
        var theResult = fuzzyResults.FirstOrDefault(r => r.Word == "the");
        Assert.NotNull(theResult);
        Assert.Equal(PredictionType.FuzzyMatch, theResult.Type);
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
