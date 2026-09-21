namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

/// <summary>
/// ForgetWord — the dictionary-management half of prediction controls (#22): removing a learned
/// word from the live predictor. The removal must hit the in-memory dictionary (so suggestions
/// drop it immediately) and be case-insensitive the way LearnWord's storage is.
/// </summary>
public class WordPredictorForgetWordTests
{
    private static WordPredictor Create() => new(new InMemoryDictionaryProvider(new()
    {
        { "hello", 100 }, { "help", 90 }
    }));

    [Fact]
    public void ForgetWord_RemovesALearnedWordFromSuggestions()
    {
        var predictor = Create();
        predictor.LearnWord("zeppelin");
        Check.That(predictor.GetSuggestions("zep")).HasElementThatMatches(s => s.Word == "zeppelin");

        Check.That(predictor.ForgetWord("zeppelin")).IsTrue();

        Check.That(predictor.GetSuggestions("zep").Select(s => s.Word)).Not.Contains("zeppelin");
    }

    [Fact]
    public void ForgetWord_UnknownWord_ReturnsFalse()
    {
        var predictor = Create();
        Check.That(predictor.ForgetWord("neverlearned")).IsFalse();
    }

    [Fact]
    public void ForgetWord_BaseDictionaryWord_CanAlsoBeRemoved()
    {
        // The user dictionary file stores the merged dict, so a "learned words" list is
        // user-file keys minus base keys — but a base word the user wants gone must be
        // forgettable too, or the page could not honour the delete.
        var predictor = Create();
        Check.That(predictor.ForgetWord("HELLO")).IsTrue(); // storage is lowercase
        Check.That(predictor.GetSuggestions("hel").Select(s => s.Word)).Not.Contains("hello");
    }
}
