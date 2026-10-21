namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

/// <summary>
/// A bigram outlives its word (forget, eviction), so the next-word category must gate on the
/// dictionary - otherwise a forgotten or evicted word keeps returning after its context.
/// </summary>
public class WordPredictorNextWordGateTests
{
    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;

        public InMemoryDictProvider(Dictionary<string, int>? words = null) => _words = words ?? [];

        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private static List<string> WordsAfter(WordPredictor predictor, string context)
        => predictor.GetSuggestions("", context, 10).Select(result => result.Word).ToList();

    [Fact]
    public void ForgottenWord_IsNotOfferedAfterItsContext()
    {
        var predictor = new WordPredictor(new InMemoryDictProvider());
        predictor.LearnWord("teh");
        predictor.LearnPair("the", "teh");
        predictor.LearnPair("the", "teh");
        Check.WithCustomMessage("control: the learned pair is offered before forgetting")
            .That(WordsAfter(predictor, "the")).Contains("teh");

        Check.That(predictor.ForgetWord("teh")).IsTrue();

        Check.That(WordsAfter(predictor, "the")).Not.Contains("teh");
    }

    [Fact]
    public void EvictedWord_IsNotOfferedAfterItsContext()
    {
        var words = new Dictionary<string, int> { ["teh"] = 1, ["the"] = 2 };
        for (var i = 0; i < WordPredictor.MaxDictionaryWords - 2; i++)
            words[$"w{i}"] = 2;

        var predictor = new WordPredictor(new InMemoryDictProvider(words));
        predictor.LearnPair("the", "teh");
        predictor.LearnPair("the", "teh");
        Check.WithCustomMessage("control: the learned pair is offered before eviction")
            .That(WordsAfter(predictor, "the")).Contains("teh");

        // The only count-1 entry ("teh") is the eviction victim.
        predictor.LearnWord("extra");

        Check.That(WordsAfter(predictor, "the")).Not.Contains("teh");
    }
}
