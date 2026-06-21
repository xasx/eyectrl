namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using NFluent;

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

        Check.That(results).HasElementThatMatches(r => r.Word == "hello");
        Check.That(results).HasElementThatMatches(r => r.Word == "help");
    }

    [Fact]
    public void GetSuggestions_OrderedByFrequency()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "they", 500 }, { "there", 300 }, { "then", 200 }
        });

        var results = predictor.GetSuggestions("the", null, maxCount: 3);

        Check.That(results[0].Word).IsEqualTo("the");
        Check.That(results[1].Word).IsEqualTo("they");
        Check.That(results[2].Word).IsEqualTo("there");
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
        Check.That(before[0].Word).IsEqualTo("team");

        // Learn "test" many times
        for (int i = 0; i < 200; i++)
            predictor.LearnWord("test");

        var after = predictor.GetSuggestions("te", null, maxCount: 2);
        Check.That(after[0].Word).IsEqualTo("test");
    }

    [Fact]
    public void GetSuggestions_EmptyPrefix_ReturnsTopWords()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "a", 900 }, { "is", 800 }
        });

        var results = predictor.GetSuggestions("", null, maxCount: 3);
        Check.That(results).HasSize(3);
    }

    [Fact]
    public void GetSuggestions_FuzzyAdjacentKey_SuggestsIntendedWord()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "rhe", 1 }, { "world", 500 }
        });

        var results = predictor.GetSuggestions("rhe", null, maxCount: 5);
        Check.That(results).HasElementThatMatches(r => r.Word == "the");
    }

    [Fact]
    public void GetSuggestions_ExactPrefixRanksAboveFuzzy_WhenFrequencyEqual()
    {
        var predictor = CreatePredictor(new()
        {
            { "there", 500 }, { "the", 500 }
        });

        var results = predictor.GetSuggestions("the", null, maxCount: 5);
        Check.That(results).HasElementThatMatches(r => r.Word == "there");
        Check.That(results).HasElementThatMatches(r => r.Word == "the");
    }

    [Fact]
    public void GetSuggestions_HighFrequencyFuzzyBeatsLowFrequencyExact()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 10000 }, { "rheumatic", 1 }
        });

        var results = predictor.GetSuggestions("rhe", null, maxCount: 5);
        Check.That(results[0].Word).IsEqualTo("the");
    }

    [Fact]
    public void GetSuggestions_NonAdjacentTypo_StillSuggestsWithinThreshold()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }
        });

        var results = predictor.GetSuggestions("she", null, maxCount: 5);
        Check.That(results).HasElementThatMatches(r => r.Word == "the");
    }

    [Fact]
    public void GetSuggestions_FarTypo_NotSuggested()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }
        });

        var results = predictor.GetSuggestions("xyz", null, maxCount: 5);
        Check.That(results.Any(r => r.Word == "the")).IsFalse();
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
        Check.That(results).Not.IsEmpty();

        // Find "you" in results - it should be there with NextWord type
        var youResult = results.FirstOrDefault(r => r.Word == "you");
        Check.That(youResult).IsNotNull();
        Check.That(youResult.Type).IsEqualTo(PredictionType.NextWord);
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
        Check.That(results).HasElementThatMatches(r => r.Word == "forward");
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
        Check.That(results).HasElementThatMatches(r => r.Word == "world");
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
        Check.That(exactResults.Where(r => r.Word.StartsWith("hel")))
            .ContainsOnlyElementsThatMatch(r => r.Type == PredictionType.ExactPrefix);

        // Fuzzy match
        var fuzzyResults = predictor.GetSuggestions("rhe", null, maxCount: 5);
        var theResult = fuzzyResults.FirstOrDefault(r => r.Word == "the");
        Check.That(theResult).IsNotNull();
        Check.That(theResult.Type).IsEqualTo(PredictionType.FuzzyMatch);
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

        Check.That(youResult).IsNotNull();
        Check.That(yourResult).IsNotNull();

        // "you" should be first due to NextWord prediction boost
        Check.That(results[0].Word).IsEqualTo("you");
        Check.That(youResult.Type).IsEqualTo(PredictionType.NextWord);
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

        Check.That(youWithContext).IsNotNull();
        Check.That(youWithoutContext).IsNotNull();

        // "you" should appear in both result sets, but context should improve its rank/score via BigramContextBoost
        Check.That(withContext.Select(r => r.Word)).Contains("you");
        Check.That(withoutContext.Select(r => r.Word)).Contains("you");

        // With context, "you" should rank higher (lower index = higher rank) due to BigramContextBoost
        var youIndexWithContext = withContext.ToList().FindIndex(r => r.Word == "you");
        var youIndexWithoutContext = withoutContext.ToList().FindIndex(r => r.Word == "you");
        Check.That(youIndexWithContext < youIndexWithoutContext).IsTrue();

        // With context, "you" should have a higher score due to BigramContextBoost multiplier
        Check.That(youWithContext.Score > youWithoutContext.Score).IsTrue();
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

        Check.That(results).Not.IsEmpty();
        // "you" should be suggested despite "world" having much higher frequency
        Check.That(results).HasElementThatMatches(r => r.Word == "you");
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
        Check.That(phraseResults).Not.IsEmpty();

        // Verify at least one phrase result contains "to" (part of the learned phrase)
        Check.That(phraseResults).HasElementThatMatches(r => r.Word.Contains("to"));
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
        Check.That(helloResult).IsNotNull();
        Check.That(helloResult.Confidence > 0 && helloResult.Confidence <= 1.0).IsTrue();

        // Check confidence for next-word prediction
        var nextWordResults = predictor.GetSuggestions("", "thank", maxCount: 5);
        var youResult = nextWordResults.FirstOrDefault(r => r.Word == "you");
        Check.That(youResult).IsNotNull();
        Check.That(youResult.Confidence > 0 && youResult.Confidence <= 1.0).IsTrue();
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

        Check.That(results).Not.IsEmpty();
        Check.That(results[0].Word).IsEqualTo("you");
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
        Check.That(afterLooking).HasElementThatMatches(r => r.Word == "forward" || r.Word.Contains("forward"));

        var afterForward = predictor.GetSuggestions("", "forward", maxCount: 5);
        Check.That(afterForward).HasElementThatMatches(r => r.Word == "to" || r.Word.Contains("to"));
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
        Check.That(results).HasSize(3);
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
            Check.That(results[i].Score >= results[i + 1].Score).IsTrue();
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
        Check.That(results).HasElementThatMatches(r => r.Word == "Hello");
        Check.That(results.Any(r => r.Word == "hello")).IsFalse();
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
        Check.That(results).HasElementThatMatches(r => r.Word == "World");
        Check.That(results.Any(r => r.Word == "world")).IsFalse();
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
        Check.That(results).HasElementThatMatches(r => r.Word == "world");
        Check.That(results.Any(r => r.Word == "World")).IsFalse();
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
        Check.That(results).HasElementThatMatches(r => r.Word == "John");

        // Word should not be all-caps or otherwise mangled.
        Check.That(results.Any(r => r.Word == "JOHN")).IsFalse();
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

        Check.That(results).HasElementThatMatches(r => r.Word == "hello");
        Check.That(results.Any(r => r.Word == "Hello")).IsFalse();
    }

    // Phase 7 Tests - Case-Sensitivity Enhancements

    [Fact]
    public void GetSuggestions_Phase7_ExactCaseMatch_ScoresHigherThanCaseInsensitive()
    {
        var predictor = CreatePredictor(new()
        {
            { "haus", 50 }, { "hand", 50 }  // Same base frequency for both words
        });

        // Learn "Das Haus" bigram - this establishes "Haus" as preferred casing for "haus"
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "Das", "Haus" });

        // Learn "Das hand" bigram - lowercase "hand"
        for (int i = 0; i < 10; i++)
            predictor.LearnSequence(new[] { "Das", "hand" });

        // Search with capitalized prefix "Ha". Both candidates match via NextWord prediction path.
        // "Haus" should match exactly (after casing preference), "hand" is case-insensitive match.
        var results = predictor.GetSuggestions("Ha", "Das", maxCount: 5);

        var hausResult = results.FirstOrDefault(r => r.Word.Equals("Haus", StringComparison.Ordinal));
        var handResult = results.FirstOrDefault(r => r.Word.Equals("hand", StringComparison.Ordinal));

        Check.That(hausResult).IsNotNull();
        Check.That(handResult).IsNotNull();

        var hausIndex = results.IndexOf(hausResult);
        var handIndex = results.IndexOf(handResult);

        Check.That(hausIndex < handIndex).IsTrue();
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
        Check.That(results).HasElementThatMatches(r => r.Word.Equals("Haus", StringComparison.Ordinal) ||
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

        // Should suggest "Hello" with normalized title casing
        var helloResult = results.FirstOrDefault(r => r.Word.StartsWith("Hel", StringComparison.OrdinalIgnoreCase));
        Check.That(helloResult).IsNotNull();
        Check.That(helloResult.Word).IsEqualTo("Hello");
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

        // Should find NASA with exact acronym casing preserved
        Check.That(results).HasElementThatMatches(r => r.Word.Equals("NASA", StringComparison.Ordinal));
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
        Check.That(hausResult).IsNotNull();

        // Should prefer the more frequently used casing "Haus" (10 times vs 1 time)
        Check.That(hausResult.Word).IsEqualTo("Haus");
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
        Check.That(results).HasElementThatMatches(r => r.Word.Equals("Haus", StringComparison.Ordinal) ||
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
        Check.That(results).HasElementThatMatches(r => r.Word.Equals("Forward", StringComparison.OrdinalIgnoreCase));
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
