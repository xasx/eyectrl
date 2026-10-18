namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Orchestrates word prediction by combining multiple sources: word predictor,
/// sentence completion trie, and the ONNX language model. Merges and ranks results.
/// </summary>
public class PredictionOrchestrator : IDisposable
{
    private readonly WordPredictor _wordPredictor;
    private readonly SentenceCompletionIndex? _sentenceIndex;
    private readonly ILanguageModelPredictor? _lmPredictor;
    private readonly bool _mlEnabled;
    private string? _lastLmPrefix;
    private string? _lastLmContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="PredictionOrchestrator"/> class.
    /// </summary>
    /// <param name="wordPredictor">The core word prediction engine.</param>
    /// <param name="sentenceIndex">Optional sentence completion trie index.</param>
    /// <param name="lmPredictor">Optional ONNX language model predictor.</param>
    /// <param name="mlEnabled">Whether ML-based suggestions are enabled.</param>
    public PredictionOrchestrator(
        WordPredictor wordPredictor,
        SentenceCompletionIndex? sentenceIndex = null,
        ILanguageModelPredictor? lmPredictor = null,
        bool mlEnabled = true)
    {
        _wordPredictor = wordPredictor;
        _sentenceIndex = sentenceIndex;
        _lmPredictor = lmPredictor;
        _mlEnabled = mlEnabled;
    }

    /// <summary>
    /// Returns merged and ranked word predictions from all available sources.
    /// Also triggers asynchronous LM inference when conventional sources produce low-confidence results.
    /// </summary>
    /// <param name="prefix">The current word prefix being typed.</param>
    /// <param name="context">The previous word for bigram context, or null.</param>
    /// <param name="maxCount">Maximum number of suggestions to return.</param>
    /// <param name="fullText">Full text typed so far for smart capitalization.</param>
    /// <returns>A ranked list of <see cref="PredictionResult"/> instances.</returns>
    public List<PredictionResult> GetSuggestions(string prefix, string? context, int maxCount, string? fullText = null)
    {
        if (maxCount <= 0) return [];

        if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(context)) return [];

        var allResults = new List<PredictionResult>();

        var wpResults = _wordPredictor.GetSuggestions(prefix, context, maxCount, fullText);
        allResults.AddRange(wpResults);

        if (_sentenceIndex != null && !string.IsNullOrWhiteSpace(prefix))
        {
            // The trie stores completions with the matched prefix stripped (Insert indexes
            // words[prefixLen..]), but accepting a suggestion REPLACES the typed word: the accept
            // path deletes CurrentInput.Length characters before inserting. Handing back the bare
            // remainder would erase what the user typed — "how" + "was sightseeing?" would land as
            // "was sightseeing?" — so re-attach the prefix to make the suggestion self-contained.
            var typedPrefix = string.Join(' ', prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var trieCompletions = _sentenceIndex.GetCompletions(prefix, maxCount);
            foreach (var (completion, frequency) in trieCompletions)
            {
                allResults.Add(new PredictionResult(
                    $"{typedPrefix} {completion}",
                    Math.Log(Math.Max(1, frequency)) * 3.0,
                    PredictionType.SentenceCompletion,
                    Math.Min(1.0, Math.Log(Math.Max(1, frequency)) / 10.0)));
            }
        }

        var merged = new Dictionary<string, PredictionResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in allResults)
        {
            if (!merged.ContainsKey(result.Word) || merged[result.Word].Score < result.Score)
                merged[result.Word] = result;
        }

        PredictionResult? lmCompletion = null;

        if (_mlEnabled && _lmPredictor != null && _lmPredictor.IsAvailable)
        {
            // Results belong to the request the predictor last ran. Only merge them when that
            // request is still the current input, and only words that continue the CURRENT
            // prefix (and are longer): a stale completion must never surface under a new word.
            var lmResults = _lmPredictor.TryGetResults();
            if (lmResults != null && lmResults.Count > 0
                && context == _lastLmContext && prefix == _lastLmPrefix)
            {
                foreach (var lmResult in lmResults)
                {
                    if (!lmResult.Word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (lmResult.Word.Length <= prefix.Length) continue;
                    lmCompletion ??= lmResult;
                    if (!merged.ContainsKey(lmResult.Word))
                        merged[lmResult.Word] = lmResult;
                }
            }

            if ((prefix != _lastLmPrefix || context != _lastLmContext) && ShouldTriggerLm(context, prefix))
            {
                _lastLmPrefix = prefix;
                _lastLmContext = context;
                _lmPredictor.Predict(context!, prefix);
            }
        }

        var sorted = Rank(merged.Values, prefix, maxCount);

        // One fresh, matching completion gets the slot right after the top dictionary match,
        // replacing the lowest-ranked entry when the bar is full. It starts with the prefix, so
        // this keeps the prefix-first rule intact.
        if (lmCompletion != null
            && !sorted.Any(r => r.Word.Equals(lmCompletion.Word, StringComparison.OrdinalIgnoreCase)))
        {
            var insertAt = Math.Min(1, sorted.Count);
            if (sorted.Count >= maxCount) sorted.RemoveAt(sorted.Count - 1);
            sorted.Insert(Math.Min(insertAt, sorted.Count), lmCompletion);
        }

        return sorted;
    }

    /// <summary>
    /// Every suggestion whose first word starts with the typed prefix comes before every
    /// suggestion that doesn't, then the existing score order within each group. This is the
    /// property the user sees: a frequent near-miss may never outrank a candidate that respects
    /// the letters actually typed. For a space-free prefix "starts with the prefix" is the same
    /// as "first word starts with the prefix"; a multi-word prefix (sentence completion typing)
    /// compares the whole suggestion, which is the typed text too.
    /// </summary>
    private static List<PredictionResult> Rank(
        IEnumerable<PredictionResult> results, string prefix, int maxCount)
        => results
            .OrderByDescending(result => string.IsNullOrEmpty(prefix)
                || result.Word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(result => result.Score)
            .Take(maxCount)
            .ToList();

    /// <summary>
    /// Only ask the model when it can continue a word in a sentence: a context AND a typed
    /// prefix. The old average-confidence heuristic kept it silent whenever the dictionary had
    /// three exact matches, even though those say nothing about the NEXT letters.
    /// </summary>
    private static bool ShouldTriggerLm(string? context, string prefix)
        => !string.IsNullOrEmpty(context) && !string.IsNullOrEmpty(prefix);

    /// <summary>Delegates to the underlying <see cref="WordPredictor"/> to learn a single word.</summary>
    /// <param name="word">The word to learn.</param>
    public void LearnWord(string word)
    {
        _wordPredictor.LearnWord(word);
    }

    /// <summary>Delegates to the underlying <see cref="WordPredictor"/> to learn a sequence of words.</summary>
    /// <param name="words">The words in order as typed.</param>
    public void LearnSequence(string[] words)
    {
        _wordPredictor.LearnSequence(words);
    }

    /// <summary>Delegates to the underlying <see cref="WordPredictor"/> to persist the learned dictionary.</summary>
    public void SaveUserDictionary()
    {
        _wordPredictor.SaveUserDictionary();
    }

    /// <summary>The underlying word prediction engine.</summary>
    public WordPredictor WordPredictor => _wordPredictor;

    /// <summary>The language model this orchestrator was built with, or null. Diagnostic/
    /// test surface, mirroring <see cref="WordPredictor"/>: it lets tests assert an LM was
    /// actually constructed without poking at prediction behaviour.</summary>
    public ILanguageModelPredictor? LanguageModel => _lmPredictor;

    /// <summary>Disposes the language model predictor if one was provided.</summary>
    public void Dispose()
    {
        _lmPredictor?.Dispose();
    }
}
