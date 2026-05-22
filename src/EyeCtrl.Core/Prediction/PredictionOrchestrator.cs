namespace EyeCtrl.Core.Prediction;

public class PredictionOrchestrator : IDisposable
{
    private readonly WordPredictor _wordPredictor;
    private readonly SentenceCompletionIndex? _sentenceIndex;
    private readonly SmallLMPredictor? _lmPredictor;
    private readonly bool _mlEnabled;
    private string? _lastLmPrefix;

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
                var firstWord = completion.Split(' ')[0];
                allResults.Add(new PredictionResult(
                    firstWord,
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

            if (prefix != _lastLmPrefix && ShouldTriggerLm(sorted, context, prefix))
            {
                _lastLmPrefix = prefix;
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

    public void LearnWord(string word)
    {
        _wordPredictor.LearnWord(word);
    }

    public void LearnSequence(string[] words)
    {
        _wordPredictor.LearnSequence(words);
    }

    public void SaveUserDictionary()
    {
        _wordPredictor.SaveUserDictionary();
    }

    public WordPredictor WordPredictor => _wordPredictor;

    public void Dispose()
    {
        _lmPredictor?.Dispose();
    }
}
