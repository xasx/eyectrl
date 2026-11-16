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

    // The boundary (next-word) request the last render issued: the context and the committed text
    // it was issued for. A late answer is only used while the boundary is still exactly this one.
    private string? _nextWordContext;
    private string? _nextWordCommitted;

    /// <summary>
    /// True when the last render happened at a word boundary and reserved slot 0 for the language
    /// model's next word: the conventional words were laid out from slot 1 on, and slot 0 is the
    /// quiet empty placeholder until <see cref="TryTakeNextWord"/> fills it. False at every other
    /// render, so a reservation can never outlive the boundary that opened it.
    /// </summary>
    public bool NextWordSlotReserved { get; private set; }

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
    /// <param name="committedText">
    /// The committed text before the current word (the keyboard's mirror, trailing space and all).
    /// At a boundary it is what the trie is keyed on and what the language model is prompted with;
    /// typed prefixes ignore it.
    /// </param>
    /// <param name="nextWordEnabled">
    /// The owner's boundary feature switch. False leaves a boundary exactly as it is today: the
    /// bigram/phrase sources only, no trie boundary query, no LM request, no reserved slot.
    /// </param>
    /// <returns>A ranked list of <see cref="PredictionResult"/> instances.</returns>
    public List<PredictionResult> GetSuggestions(string prefix, string? context, int maxCount,
        string? fullText = null, string? committedText = null, bool nextWordEnabled = true)
    {
        if (maxCount <= 0) return [];

        if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(context))
        {
            ClearNextWord();
            return [];
        }

        // Every render starts by dropping the previous boundary's reservation: a typed prefix (or
        // any other state) must never leave a slot held for an answer that is no longer wanted.
        // A boundary render re-opens one below.
        ClearNextWord();

        // A word boundary: nothing typed yet, but there is a previous word. That is the state a
        // Space, a pick, a Wort löschen back to the boundary, or a Backspace re-open leaves.
        var boundary = string.IsNullOrEmpty(prefix);
        var reserved = boundary && nextWordEnabled && _mlEnabled && _lmPredictor is { IsAvailable: true };
        // One slot is held back for the LM's next word; the conventional sources fill the rest.
        var limit = reserved ? Math.Max(0, maxCount - 1) : maxCount;

        var allResults = new List<PredictionResult>();

        var wpResults = _wordPredictor.GetSuggestions(prefix, context, limit, fullText);
        allResults.AddRange(wpResults);

        if (_sentenceIndex != null && !string.IsNullOrWhiteSpace(prefix))
        {
            // The trie stores completions with the matched prefix stripped (Insert indexes
            // words[prefixLen..]), but accepting a suggestion REPLACES the typed word: the accept
            // path deletes CurrentInput.Length characters before inserting. Handing back the bare
            // remainder would erase what the user typed — "how" + "was sightseeing?" would land as
            // "was sightseeing?" — so re-attach the prefix to make the suggestion self-contained.
            var typedPrefix = string.Join(' ', prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var trieCompletions = _sentenceIndex.GetCompletions(prefix, limit);
            foreach (var (completion, frequency) in trieCompletions)
            {
                // Keep only completions whose words are all in the dictionary. The trie was
                // learned from sentences and carries names the predictor cannot judge ("ist tom
                // taub?"). Filter only - do not fetch more to refill.
                if (!CompletionWordsAreKnown(completion)) continue;

                allResults.Add(new PredictionResult(
                    $"{typedPrefix} {completion}",
                    Math.Log(Math.Max(1, frequency)) * 3.0,
                    PredictionType.SentenceCompletion,
                    Math.Min(1.0, Math.Log(Math.Max(1, frequency)) / 10.0)));
            }
        }
        else if (_sentenceIndex != null && boundary && nextWordEnabled)
        {
            AddBoundaryTrieResults(allResults, committedText, context);
        }

        var merged = new Dictionary<string, PredictionResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in allResults)
        {
            if (!merged.ContainsKey(result.Word) || merged[result.Word].Score < result.Score)
                merged[result.Word] = result;
        }

        PredictionResult? lmCompletion = null;

        if (boundary)
        {
            // A boundary render owns its own request (the reset above already dropped any earlier
            // one); when the LM is available, slot 0 is held for the answer this render asks for.
            if (reserved)
            {
                var prompt = NextWordPrompt(committedText, context);
                if (prompt.Length > 0)
                {
                    _nextWordContext = context;
                    _nextWordCommitted = CommittedKey(committedText);
                    _lmPredictor!.PredictNextWord(prompt);
                    NextWordSlotReserved = true;
                }
            }
        }
        else if (_mlEnabled && _lmPredictor != null && _lmPredictor.IsAvailable)
        {
            // Results belong to the request the predictor last ran. Only merge them when that
            // request is still the current context (the prefix moves on between keystrokes -
            // that is when results arrive), and only words that continue the CURRENT prefix
            // (and are longer): a stale completion must never surface under a new word.
            var lmResults = _lmPredictor.TryGetResults();
            if (lmResults != null && lmResults.Count > 0 && context == _lastLmContext)
            {
                foreach (var lmResult in lmResults)
                {
                    if (!lmResult.Word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (lmResult.Word.Length <= prefix.Length) continue;
                    if (!_wordPredictor.KnowsWord(lmResult.Word)) continue;
                    // Known words show the dictionary's display casing (German nouns).
                    var displayed = lmResult with { Word = _wordPredictor.GetDisplayWord(lmResult.Word) };
                    lmCompletion ??= displayed;
                    if (!merged.ContainsKey(displayed.Word))
                        merged[displayed.Word] = displayed;
                }
            }

            if ((prefix != _lastLmPrefix || context != _lastLmContext) && ShouldTriggerLm(context, prefix))
            {
                _lastLmPrefix = prefix;
                _lastLmContext = context;
                _lmPredictor.Predict(context!, prefix);
            }
        }

        var sorted = Rank(merged.Values, prefix, limit);

        // One fresh, matching completion gets the slot right after the top dictionary match,
        // replacing the lowest-ranked entry when the bar is full. It starts with the prefix, so
        // this keeps the prefix-first rule intact.
        if (lmCompletion != null
            && !sorted.Any(r => r.Word.Equals(lmCompletion.Word, StringComparison.OrdinalIgnoreCase)))
        {
            var insertAt = Math.Min(1, sorted.Count);
            if (sorted.Count >= limit) sorted.RemoveAt(sorted.Count - 1);
            sorted.Insert(Math.Min(insertAt, sorted.Count), lmCompletion);
        }

        return sorted;
    }

    /// <summary>
    /// Fills slot 0 with the language model's next word for the boundary the last render issued.
    /// Returns null while the answer has not arrived (the caller keeps the slot empty), an empty
    /// list when it arrived and nothing usable came of it (or the boundary has moved on, which
    /// also clears the reservation), and one <see cref="PredictionResult"/> otherwise.
    /// <para>
    /// The answer is used only while the boundary is byte-identical to the one the request was
    /// issued for, so a Space, a pick or any other move that lands somewhere else cannot surface
    /// a word for a different sentence position.
    /// </para>
    /// </summary>
    /// <param name="context">The previous word right now (the bigram context of the boundary).</param>
    /// <param name="committedText">The committed text right now.</param>
    /// <param name="smartCapitalization">Whether a sentence start should capitalise the word.</param>
    public IReadOnlyList<PredictionResult>? TryTakeNextWord(string? context, string? committedText,
        bool smartCapitalization)
    {
        if (!NextWordSlotReserved || _lmPredictor is null) return null;

        if (!string.Equals(context, _nextWordContext, StringComparison.Ordinal)
            || !string.Equals(CommittedKey(committedText), _nextWordCommitted, StringComparison.Ordinal))
        {
            // The boundary moved on: the reserved slot must never show a word for it.
            ClearNextWord();
            return [];
        }

        var results = _lmPredictor.TryGetResults();
        if (results == null) return null;   // still running

        ClearNextWord();
        if (results.Count == 0) return [];

        var word = results[0].Word;
        if (!_wordPredictor.KnowsWord(word)) return [];
        var displayed = _wordPredictor.GetDisplayWord(word);
        if (smartCapitalization
            && CapitalizationHelper.ShouldCapitalize(committedText ?? "", _wordPredictor.Abbreviations))
        {
            // D6: a boundary word at a sentence start is capitalised like the dictionary's words
            // are. The rule is the shipped one, misfires included: fixing the "gut." abbreviation
            // heuristic is a follow-up, not part of F7.
            displayed = CapitalizationHelper.ApplyCapitalization(displayed, true);
        }

        return [results[0] with { Word = displayed }];
    }

    /// <summary>Drops any pending boundary request (sensitive field, hide, a non-boundary render).</summary>
    public void ClearNextWord()
    {
        NextWordSlotReserved = false;
        _nextWordContext = null;
        _nextWordCommitted = null;
    }

    /// <summary>
    /// The trie's next-word candidates for a boundary: each of the last one to three words is used
    /// as a key, and every completion contributes its FIRST word. Hits are deduped and gated on the
    /// dictionary, like every other completion the trie contributes.
    /// </summary>
    private void AddBoundaryTrieResults(List<PredictionResult> allResults, string? committedText, string? context)
    {
        var text = string.IsNullOrWhiteSpace(committedText) ? context : committedText;
        if (string.IsNullOrWhiteSpace(text)) return;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        for (var take = Math.Min(3, words.Length); take >= 1; take--)
        {
            var key = string.Join(' ', words[^take..]);
            if (key.Length == 0) continue;

            foreach (var (completion, frequency) in _sentenceIndex!.GetCompletions(key, 8))
            {
                var first = FirstWordOf(completion);
                if (first is null) continue;
                if (!_wordPredictor.KnowsWord(first)) continue;
                if (!seen.Add(first)) continue;

                allResults.Add(new PredictionResult(
                    first,
                    Math.Log(Math.Max(1, frequency)) * 3.0,
                    PredictionType.SentenceCompletion,
                    Math.Min(1.0, Math.Log(Math.Max(1, frequency)) / 10.0)));

                if (++added >= 8) return;
            }
        }
    }

    /// <summary>The first whitespace-delimited word of a trie completion, boundary punctuation trimmed.</summary>
    private static string? FirstWordOf(string completion)
    {
        var text = completion.TrimStart();
        if (text.Length == 0) return null;
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var word = TrimTokenBoundaries(text[..end]);
        return word.Length == 0 ? null : word;
    }

    /// <summary>
    /// The text a next-word request is built from: the committed text (which already ends in the
    /// previous word plus a space) with its trailing separators trimmed. Falls back to the context
    /// when the caller has no mirror text, so a boundary never loses its one known word.
    /// </summary>
    private static string NextWordPrompt(string? committedText, string? context)
    {
        var text = string.IsNullOrWhiteSpace(committedText) ? context : committedText;
        return text is null ? "" : text.TrimEnd();
    }

    /// <summary>The staleness key for a boundary: the committed text, trailing whitespace trimmed.</summary>
    private static string CommittedKey(string? committedText) => committedText?.TrimEnd() ?? "";


    /// <summary>
    /// True when every word of a trie completion is known to the dictionary. Tokens are split on
    /// whitespace, boundary punctuation is trimmed (inner apostrophes kept), tokens that become
    /// empty are ignored, and digits make a token unknown.
    /// </summary>
    private bool CompletionWordsAreKnown(string completion)
    {
        foreach (var token in completion.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = TrimTokenBoundaries(token);
            if (word.Length == 0) continue;
            if (word.Any(char.IsDigit)) return false;
            if (!_wordPredictor.KnowsWord(word.ToLowerInvariant())) return false;
        }

        return true;
    }

    private static string TrimTokenBoundaries(string token)
    {
        var start = 0;
        while (start < token.Length && !char.IsLetterOrDigit(token[start])) start++;
        var end = token.Length;
        while (end > start && !char.IsLetterOrDigit(token[end - 1])) end--;
        return token[start..end];
    }

    /// <summary>
    /// Every suggestion whose first word starts with the typed prefix comes before every
    /// suggestion that doesn't, then the existing score order within each group. This is the
    /// property the user sees: a frequent near-miss may never outrank a candidate that respects
    /// the letters actually typed. For a space-free prefix "starts with the prefix" is the same
    /// as "first word starts with the prefix"; a multi-word prefix (sentence completion typing)
    /// compares the whole suggestion, which is the typed text too.
    /// </summary>
    internal static List<PredictionResult> Rank(
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

    /// <summary>Delegates to the underlying <see cref="WordPredictor"/> to learn one typed pair.</summary>
    /// <param name="previousWord">The word before the committed one.</param>
    /// <param name="word">The first committed word.</param>
    public void LearnPair(string previousWord, string word)
    {
        _wordPredictor.LearnPair(previousWord, word);
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
