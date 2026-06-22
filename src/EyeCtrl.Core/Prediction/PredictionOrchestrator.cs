namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Orchestrates word prediction by combining multiple sources: word predictor,
/// sentence completion trie, and the ONNX language model. Merges and ranks results.
/// </summary>
public class PredictionOrchestrator : IDisposable
{
    private readonly WordPredictor _wordPredictor;
    private readonly SentenceCompletionIndex? _sentenceIndex;
    private readonly SmallLMPredictor? _lmPredictor;
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
        SmallLMPredictor? lmPredictor = null,
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
            var trieCompletions = _sentenceIndex.GetCompletions(prefix, maxCount);
            foreach (var (completion, frequency) in trieCompletions)
            {
                allResults.Add(new PredictionResult(
                    completion,
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

        var sorted = merged.Values
            .OrderByDescending(r => r.Score)
            .Take(maxCount)
            .ToList();

        if (_mlEnabled && _lmPredictor != null && _lmPredictor.IsAvailable)
        {
            var lmResults = _lmPredictor.TryGetResults();
            if (lmResults != null && lmResults.Count > 0)
            {
                foreach (var lmResult in lmResults)
                {
                    if (!merged.ContainsKey(lmResult.Word))
                    {
                        merged[lmResult.Word] = lmResult;
                        sorted.Add(lmResult);
                        if (sorted.Count >= maxCount) break;
                    }
                }
                sorted = sorted.OrderByDescending(r => r.Score).Take(maxCount).ToList();
            }

            if ((prefix != _lastLmPrefix || context != _lastLmContext) && ShouldTriggerLm(sorted, context, prefix))
            {
                _lastLmPrefix = prefix;
                _lastLmContext = context;
                var lmContext = context ?? "";
                _lmPredictor.Predict(lmContext, prefix);
            }
        }

        return sorted;
    }

    private static bool ShouldTriggerLm(List<PredictionResult> currentResults, string? context, string prefix)
    {
        if (string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(context)) return false;
        if (prefix.Length < 2 && string.IsNullOrEmpty(context)) return false;
        if (currentResults.Count < 3) return true;
        var avgConfidence = currentResults.Average(r => r.Confidence);
        return avgConfidence < 0.5;
    }

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

    /// <summary>Disposes the language model predictor if one was provided.</summary>
    public void Dispose()
    {
        _lmPredictor?.Dispose();
    }
}
