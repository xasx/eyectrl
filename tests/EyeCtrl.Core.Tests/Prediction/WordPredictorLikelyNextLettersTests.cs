namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

/// <summary>
/// <see cref="WordPredictor.LikelyNextLetters"/> — the pure predicate behind E1's SHADOW likely-bit
/// (and, in release 3, the weighted dwell): the k letters that most often continue a typed
/// prefix, weighted by word frequency, learned words included. An empty set for a non-empty
/// prefix is the "no continuation" signal the log's <c>nocont</c> flag reads.
/// </summary>
public class WordPredictorLikelyNextLettersTests
{
    private static WordPredictor Create(Dictionary<string, int> words) => new(new InMemoryDictionaryProvider(words));

    [Fact]
    public void ReturnsTheKMostFrequentNextLetters_WeightedByWordFrequency()
    {
        var predictor = Create(new()
        {
            { "haus", 100 }, { "hand", 90 }, { "halt", 80 }, { "hell", 70 }, { "hier", 60 }, { "hose", 5 },
            { "hut", 1000 },
        });

        // After "h": u (1000, ONE word), a (100+90+80), e (70), i (60), o (5). By word COUNT a would
        // lead and u would come last: the weight is frequency, not the number of words.
        Check.That(predictor.LikelyNextLetters("h", 2)).ContainsExactly('u', 'a');
        Check.That(predictor.LikelyNextLetters("h", 5)).ContainsExactly('u', 'a', 'e', 'i', 'o');
    }

    [Fact]
    public void IsCaseInsensitive_OnThePrefix_AndReturnsLowercaseLetters()
    {
        var predictor = Create(new() { { "haus", 100 }, { "hell", 10 } });

        Check.That(predictor.LikelyNextLetters("H", 5)).ContainsExactly('a', 'e');
        Check.That(predictor.LikelyNextLetters("HA", 5)).ContainsExactly('u');
    }

    [Fact]
    public void EmptyPrefix_ReturnsTheMostFrequentFirstLetters()
    {
        var predictor = Create(new() { { "haus", 100 }, { "hand", 90 }, { "welt", 50 }, { "zug", 1 } });

        Check.That(predictor.LikelyNextLetters("", 2)).ContainsExactly('h', 'w');
    }

    [Fact]
    public void PrefixWithNoContinuation_IsEmpty_AndAWordEndIsNotALetter()
    {
        var predictor = Create(new() { { "haus", 100 } });

        Check.That(predictor.LikelyNextLetters("xq", 5)).IsEmpty();
        // "haus" itself has no next letter: the word end adds nothing.
        Check.That(predictor.LikelyNextLetters("haus", 5)).IsEmpty();
    }

    [Fact]
    public void LearnedWords_AreIncluded()
    {
        var predictor = Create(new() { { "haus", 100 } });
        predictor.LearnWord("Zebrafink");

        Check.That(predictor.LikelyNextLetters("zebra", 5)).ContainsExactly('f');
    }

    [Fact]
    public void NonLetterContinuations_AreIgnored()
    {
        var predictor = Create(new() { { "e-mail", 100 }, { "ebene", 10 } });

        Check.That(predictor.LikelyNextLetters("e", 5)).ContainsExactly('b');
    }

    [Fact]
    public void TiesBreakByLetter_SoTheSetIsDeterministic()
    {
        var predictor = Create(new() { { "ba", 10 }, { "bu", 10 }, { "be", 10 } });

        Check.That(predictor.LikelyNextLetters("b", 2)).ContainsExactly('a', 'e');
    }

    [Fact]
    public void NonPositiveK_ReturnsEmpty()
    {
        var predictor = Create(new() { { "haus", 100 } });

        Check.That(predictor.LikelyNextLetters("h", 0)).IsEmpty();
    }
}
