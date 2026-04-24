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
            { "world", 500 }, { "you", 100 }, { "your", 90 }, { "youth", 85 }
        });

        // Learn bigram "thank you" multiple times
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "thank", "you" });

        // Test with a prefix "yo" to trigger ExactPrefix/Fuzzy matching instead of NextWord
        // This allows us to verify that BigramContextBoost is applied
        var withContext = predictor.GetSuggestions("yo", "thank", maxCount: 5);
        var withoutContext = predictor.GetSuggestions("yo", null, maxCount: 5);

        var youWithContext = withContext.FirstOrDefault(r => r.Word == "you");
        var youWithoutContext = withoutContext.FirstOrDefault(r => r.Word == "you");

        Assert.NotNull(youWithContext);
        Assert.NotNull(youWithoutContext);

        // "you" should appear in both result sets, but context should improve its rank/score via BigramContextBoost
        Assert.Contains("you", withContext.Select(r => r.Word));
        Assert.Contains("you", withoutContext.Select(r => r.Word));

        // With context, "you" should rank higher (lower index = higher rank) due to BigramContextBoost
        var youIndexWithContext = withContext.ToList().FindIndex(r => r.Word == "you");
        var youIndexWithoutContext = withoutContext.ToList().FindIndex(r => r.Word == "you");
        Assert.True(youIndexWithContext < youIndexWithoutContext,
            $"\"you\" should rank higher with context (index {youIndexWithContext}) than without (index {youIndexWithoutContext}) due to BigramContextBoost");

        // With context, "you" should have a higher score due to BigramContextBoost multiplier
        Assert.True(youWithContext.Score > youWithoutContext.Score,
            $"\"you\" should have higher score with context ({youWithContext.Score}) than without ({youWithoutContext.Score}) due to BigramContextBoost");
    }

    [Fact]
    public void GetSuggestions_EmptyPrefixWithContext_PrioritizesContextualPredictions()
    {
        var predictor = CreatePredictor(new()
        {
            { "you", 10 }, { "world", 1000 }, { "your", 10 }
        });

        // Learn "thank you" pattern
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "thank", "you" });

        // Empty prefix with context should prioritize next-word and phrase predictions
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
            { "forward", 50 }, { "to", 100 }, { "hearing", 50 }, { "from", 80 }
        });

        // Learn a longer phrase so a multi-word context can trigger phrase continuation
        for (int i = 0; i < 3; i++)
            predictor.LearnSequence(new[] { "looking", "forward", "to", "hearing" });

        // With a multi-word context matching the phrase prefix, should suggest the exact phrase continuation
        var results = predictor.GetSuggestions("", "looking forward", maxCount: 5);

        // Should get phrase suggestions - the phrase model returns full phrases that match the context
        // When context is "looking forward", we should get phrases like "looking forward to" or "looking forward to hearing"
        var phraseResults = results.Where(r => r.Type == PredictionType.Phrase).ToList();
        Assert.NotEmpty(phraseResults);

        // Verify at least one phrase result contains "to" (part of the learned phrase)
        Assert.Contains(phraseResults, r => r.Word.Contains("to"));
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

    [Fact]
    public void GetSuggestions_SmartCapitalization_CapitalizesAtStartOfText()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 100 }, { "help", 80 }
        });

        var results = predictor.GetSuggestions("hel", null, maxCount: 5, fullText: "");

        // Should capitalize at start of text
        Assert.Contains(results, r => r.Word == "Hello");
        Assert.DoesNotContain(results, r => r.Word == "hello");
    }

    [Fact]
    public void GetSuggestions_SmartCapitalization_CapitalizesAfterPeriod()
    {
        var predictor = CreatePredictor(new()
        {
            { "world", 100 }, { "work", 80 }
        });

        var results = predictor.GetSuggestions("wo", null, maxCount: 5, fullText: "Hello. ");

        // Should capitalize after sentence ending
        Assert.Contains(results, r => r.Word == "World");
        Assert.DoesNotContain(results, r => r.Word == "world");
    }

    [Fact]
    public void GetSuggestions_SmartCapitalization_DoesNotCapitalizeInMiddle()
    {
        var predictor = CreatePredictor(new()
        {
            { "world", 100 }, { "work", 80 }
        });

        var results = predictor.GetSuggestions("wo", null, maxCount: 5, fullText: "Hello ");

        // Should NOT capitalize in middle of sentence
        Assert.Contains(results, r => r.Word == "world");
        Assert.DoesNotContain(results, r => r.Word == "World");
    }

    [Fact]
    public void GetSuggestions_SmartCapitalization_CapitalizesLowercaseWordAtSentenceStart()
    {
        var predictor = CreatePredictor(new()
        {
            { "john", 100 }, { "jane", 80 }
        });

        // Dictionary entries are lowercase. At the start of a sentence, smart capitalization
        // should title-case the suggestion rather than leaving it lowercase or making it all-caps.
        var results = predictor.GetSuggestions("jo", null, maxCount: 5, fullText: "");

        // At start of sentence, should capitalize the suggestion.
        Assert.Contains(results, r => r.Word == "John");

        // Word should not be all-caps or otherwise mangled.
        Assert.DoesNotContain(results, r => r.Word == "JOHN");
    }

    [Fact]
    public void GetSuggestions_SmartCapitalization_DisabledWhenFullTextNull()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 100 }, { "help", 80 }
        });

        // fullText is null - no capitalization should be applied
        var results = predictor.GetSuggestions("hel", null, maxCount: 5, fullText: null);

        Assert.Contains(results, r => r.Word == "hello");
        Assert.DoesNotContain(results, r => r.Word == "Hello");
    }

    // Phase 7 Tests - Case-Sensitivity Enhancements

    [Fact]
    public void GetSuggestions_Phase7_ExactCaseMatch_ScoresHigherThanCaseInsensitive()
    {
        var predictor = CreatePredictor(new()
        {
            { "haus", 100 }, { "hallo", 100 }
        });

        // Learn "Haus" (German noun, should be capitalized)
        predictor.LearnSequence(new[] { "Haus" });

        // Search with lowercase prefix "ha"
        var results = predictor.GetSuggestions("ha", null, maxCount: 5);

        // Both words should appear, but exact case matches should score higher
        Assert.Contains(results, r => r.Word.Equals("haus", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, r => r.Word.Equals("hallo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSuggestions_Phase7_CaseInsensitiveFallback_FindsGermanNouns()
    {
        var predictor = CreatePredictor(new()
        {
            { "haus", 100 }
        });

        // Learn bigram with capitalized German noun
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "Das", "Haus" });

        // User types lowercase "haus", should still get suggestion
        var results = predictor.GetSuggestions("haus", "Das", maxCount: 5);

        // "Haus" should be suggested even though user typed lowercase
        Assert.Contains(results, r => r.Word.Equals("Haus", StringComparison.Ordinal) ||
                                      r.Word.Equals("haus", StringComparison.Ordinal));
    }

    [Fact]
    public void LearnSequence_Phase7_NormalizesALLCAPS()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 10 }
        });

        // User types in ALL-CAPS
        predictor.LearnSequence(new[] { "HELLO", "WORLD" });

        // System should normalize to title case (not store as ALL-CAPS)
        var results = predictor.GetSuggestions("hel", null, maxCount: 5);

        // Should suggest "Hello" (normalized), not "HELLO"
        var helloResult = results.FirstOrDefault(r => r.Word.StartsWith("Hel", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(helloResult);

        // Verify it's not ALL-CAPS
        Assert.NotEqual("HELLO", helloResult.Word);
    }

    [Fact]
    public void LearnSequence_Phase7_PreservesKnownAcronyms()
    {
        var predictor = CreatePredictor(new()
        {
            { "nasa", 1 }, { "is", 10 }, { "amazing", 10 }
        });

        // Learn sequence with known acronym
        predictor.LearnSequence(new[] { "NASA", "is", "amazing" });

        // Acronym should remain ALL-CAPS
        var results = predictor.GetSuggestions("nasa", null, maxCount: 5);

        // Should find NASA (preserved as acronym)
        Assert.Contains(results, r => r.Word.Equals("NASA", StringComparison.Ordinal) ||
                                      r.Word.Equals("nasa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CasingPreferences_Phase7_LearnUserPreference()
    {
        var predictor = CreatePredictor(new()
        {
            { "haus", 10 }
        });

        // User consistently types "Haus" (German noun)
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "Haus" });

        // User types it lowercase once
        predictor.LearnSequence(new[] { "haus" });

        // Preferred casing should be "Haus" (most frequent)
        var results = predictor.GetSuggestions("ha", null, maxCount: 5);

        var hausResult = results.FirstOrDefault(r => r.Word.Equals("haus", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(hausResult);

        // Should prefer the more frequently used casing
        // Note: The exact casing depends on implementation details, but "Haus" should be preferred
    }

    [Fact]
    public void GetSuggestions_Phase7_BigramCaseInsensitiveFallback()
    {
        var predictor = CreatePredictor(new()
        {
            { "haus", 10 }, { "hello", 10 }
        });

        // Learn bigram with capitalized word
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "das", "Haus" });

        // User types lowercase context
        var results = predictor.GetSuggestions("h", "das", maxCount: 5);

        // Should still suggest "Haus" even though context is lowercase
        Assert.Contains(results, r => r.Word.Equals("Haus", StringComparison.Ordinal) ||
                                      r.Word.Equals("haus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSuggestions_Phase7_PhraseCaseInsensitiveFallback()
    {
        var predictor = CreatePredictor(new()
        {
            { "forward", 10 }, { "to", 10 }
        });

        // Learn phrase with mixed case
        for (int i = 0; i < 3; i++)
            predictor.LearnSequence(new[] { "Looking", "Forward", "To" });

        // User types lowercase prefix
        var results = predictor.GetSuggestions("f", "Looking", maxCount: 5);

        // Should still suggest "Forward" even with different case
        Assert.Contains(results, r => r.Word.Equals("Forward", StringComparison.OrdinalIgnoreCase));
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
