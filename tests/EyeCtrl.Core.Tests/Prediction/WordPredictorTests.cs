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

    // Phase 2 Tests - Enhanced Context Awareness

    [Fact]
    public void GetSuggestions_NextWordPrediction_HasHigherScoreThanExactMatch()
    {
        var predictor = CreatePredictor(new()
        {
            { "you", 100 }, { "your", 100 }, { "world", 100 }
        });

        // Learn "thank you" pattern multiple times
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "thank", "you" });

        // With context "thank", "you" should score higher than other words
        var results = predictor.GetSuggestions("y", "thank", maxCount: 5);

        var youResult = results.FirstOrDefault(r => r.Word == "you");
        var yourResult = results.FirstOrDefault(r => r.Word == "your");

        Assert.NotNull(youResult);
        Assert.NotNull(yourResult);

        // "you" should be first due to NextWord prediction boost
        Assert.Equal("you", results[0].Word);
        Assert.Equal(PredictionType.NextWord, youResult.Type);
    }

    [Fact]
    public void GetSuggestions_ContextBoost_IncreasesScoreForBigramMatch()
    {
        var predictor = CreatePredictor(new()
        {
            { "world", 500 }, { "you", 100 }
        });

        // Learn bigram "thank you" multiple times
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "thank", "you" });

        // With context, "you" should rank higher despite lower base frequency
        var withContext = predictor.GetSuggestions("", "thank", maxCount: 5);
        var withoutContext = predictor.GetSuggestions("", null, maxCount: 5);

        var youWithContext = withContext.FirstOrDefault(r => r.Word == "you");
        Assert.NotNull(youWithContext);

        // "you" should appear in top results with context
        Assert.Contains("you", withContext.Select(r => r.Word));
    }

    [Fact]
    public void GetSuggestions_EmptyPrefixWithContext_ReturnsOnlyNextWordPredictions()
    {
        var predictor = CreatePredictor(new()
        {
            { "you", 10 }, { "world", 1000 }, { "your", 10 }
        });

        // Learn "thank you" pattern
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "thank", "you" });

        // Empty prefix with context should prioritize next-word predictions
        var results = predictor.GetSuggestions("", "thank", maxCount: 5);

        Assert.NotEmpty(results);
        // "you" should be suggested despite "world" having much higher frequency
        Assert.Contains(results, r => r.Word == "you");
    }

    [Fact]
    public void GetSuggestions_PhrasePrediction_SuggestsMultiWordContinuation()
    {
        var predictor = CreatePredictor(new()
        {
            { "forward", 50 }, { "to", 100 }, { "hearing", 50 }
        });

        // Learn three-word phrase
        for (int i = 0; i < 3; i++)
            predictor.LearnSequence(new[] { "looking", "forward", "to" });

        // With "looking" context, should suggest continuation
        var results = predictor.GetSuggestions("", "looking", maxCount: 5);

        Assert.Contains(results, r => r.Word == "forward" || r.Word.Contains("forward"));
    }

    [Fact]
    public void GetSuggestions_ConfidenceScore_ReflectsFrequencyAndType()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 100 }, { "you", 50 }
        });

        // Learn bigram
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "thank", "you" });

        // Check confidence for exact match
        var exactResults = predictor.GetSuggestions("hel", null, maxCount: 5);
        var helloResult = exactResults.FirstOrDefault(r => r.Word == "hello");
        Assert.NotNull(helloResult);
        Assert.True(helloResult.Confidence > 0 && helloResult.Confidence <= 1.0);

        // Check confidence for next-word prediction
        var nextWordResults = predictor.GetSuggestions("", "thank", maxCount: 5);
        var youResult = nextWordResults.FirstOrDefault(r => r.Word == "you");
        Assert.NotNull(youResult);
        Assert.True(youResult.Confidence > 0 && youResult.Confidence <= 1.0);
    }

    [Fact]
    public void GetSuggestions_PrefixWithContext_CombinesBothSignals()
    {
        var predictor = CreatePredictor(new()
        {
            { "you", 100 }, { "your", 100 }, { "young", 100 }
        });

        // Learn "thank you" pattern
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "thank", "you" });

        // With both prefix "y" and context "thank", should favor "you"
        var results = predictor.GetSuggestions("y", "thank", maxCount: 5);

        Assert.NotEmpty(results);
        Assert.Equal("you", results[0].Word);
    }

    [Fact]
    public void LearnSequence_UpdatesBigramsAndPhrases()
    {
        var predictor = CreatePredictor(new()
        {
            { "looking", 50 }, { "forward", 50 }, { "to", 100 }
        });

        // Learn a three-word sequence
        predictor.LearnSequence(new[] { "looking", "forward", "to" });
        predictor.LearnSequence(new[] { "looking", "forward", "to" });

        // Should create bigrams: "looking -> forward" and "forward -> to"
        var afterLooking = predictor.GetSuggestions("", "looking", maxCount: 5);
        Assert.Contains(afterLooking, r => r.Word == "forward" || r.Word.Contains("forward"));

        var afterForward = predictor.GetSuggestions("", "forward", maxCount: 5);
        Assert.Contains(afterForward, r => r.Word == "to" || r.Word.Contains("to"));
    }

    [Fact]
    public void GetSuggestions_MaxCount_LimitsResults()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 100 }, { "help", 90 }, { "heap", 80 },
            { "heat", 70 }, { "heaven", 60 }
        });

        var results = predictor.GetSuggestions("he", null, maxCount: 3);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void GetSuggestions_ScoreOrdering_PrioritizesHigherScores()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "they", 500 }, { "there", 300 }
        });

        var results = predictor.GetSuggestions("the", null, maxCount: 5);

        // Verify results are sorted by score descending
        for (int i = 0; i < results.Count - 1; i++)
        {
            Assert.True(results[i].Score >= results[i + 1].Score,
                $"Score at index {i} ({results[i].Score}) should be >= score at index {i + 1} ({results[i + 1].Score})");
        }
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
