namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

/// <summary>
/// <see cref="WordPredictor.ApplyLearnedDelta"/> — the background prediction-stack swap's carry
/// mechanism. The delta is (state at swap) − (snapshot at rebuild) taken on the OLD stack and
/// applied to the NEW one: positive deltas only, all four state kinds, nothing ever lowered.
/// </summary>
public class WordPredictorDeltaTests
{
    private static WordPredictor NewPredictor()
        => new(new InMemoryDictionaryProvider(new Dictionary<string, int>()), "QWERTY");

    [Fact]
    public void Delta_AppliesNewWords_Additively_AndNeverLowers()
    {
        var oldP = NewPredictor();
        var newP = NewPredictor();

        var before = oldP.CaptureLearnedState();
        oldP.LearnWord("quokka");
        oldP.LearnWord("quokka");
        oldP.LearnWord("zebra");   // base word? no — fresh predictor, all words are new

        newP.ApplyLearnedDelta(before, oldP.CaptureLearnedState());

        var freqs = newP.WordFrequencies;
        Check.That(freqs.GetValueOrDefault("quokka")).IsEqualTo(2);
        Check.That(freqs.GetValueOrDefault("zebra")).IsEqualTo(1);
    }

    [Fact]
    public void Delta_AppliesBigramsAndPhrases_WithFrequencies()
    {
        var oldP = NewPredictor();
        var newP = NewPredictor();

        var before = oldP.CaptureLearnedState();
        oldP.LearnSequence(new[] { "quokka", "surfs", "alot" });

        newP.ApplyLearnedDelta(before, oldP.CaptureLearnedState());

        // Bigram: quokka -> surfs (and surfs -> alot) carried over.
        var bigrams = newP.CaptureLearnedState().Bigrams;
        Check.That(bigrams["quokka"]["surfs"]).IsEqualTo(1);
        Check.That(bigrams["surfs"]["alot"]).IsEqualTo(1);

        // Phrase: "quokka surfs" -> "quokka surfs alot" carried over.
        var phrases = newP.CaptureLearnedState().Phrases;
        Check.That(phrases["quokka surfs"]["quokka surfs alot"]).IsEqualTo(1);
    }

    [Fact]
    public void Delta_AppliesCasingPreferences()
    {
        var oldP = NewPredictor();
        var newP = NewPredictor();

        var before = oldP.CaptureLearnedState();
        // RecordCasing counts variants from the raw word; typing NASA twice yields the variant.
        oldP.LearnSequence(new[] { "NASA" });
        oldP.LearnSequence(new[] { "NASA" });

        newP.ApplyLearnedDelta(before, oldP.CaptureLearnedState());

        var casing = newP.CaptureLearnedState().Casing;
        Check.That(casing["nasa"]["NASA"]).IsEqualTo(2);
    }

    [Fact]
    public void Delta_WithNoLearning_IsANoOp()
    {
        var oldP = NewPredictor();
        var newP = NewPredictor();
        oldP.LearnWord("seed");

        var before = oldP.CaptureLearnedState();
        // No learning between snapshot and swap.
        newP.ApplyLearnedDelta(before, oldP.CaptureLearnedState());

        Check.That(newP.WordFrequencies.ContainsKey("seed")).IsFalse();
    }
}
