# Intelligent Word Prediction - Implementation Plan

**Date:** 2026-04-17
**Status:** Approved
**Author:** Design based on user requirements

## Executive Summary

This plan details the implementation of intelligent word prediction enhancements for EyeCtrl's on-screen keyboard. The focus is on context-aware predictions using n-grams, personal vocabulary learning, and configurable UI enhancements.

## Requirements Summary

### Must-Have Features (Priority 1)
1. ✅ **N-gram context predictions** - Previous word context (e.g., "how are" → "you")
2. ✅ **Recent typing patterns** - Track commonly used phrases
3. ✅ **Personal dictionary expansion** - Learn new words automatically
4. ✅ **Phrase/bigram learning** - Multi-word sequences like "thank you"
5. ✅ **Next-word prediction** - Show predictions when current input is empty
6. ✅ **Smart capitalization** - Sentence boundaries & proper nouns (configurable)

### Nice-to-Have Features (Priority 2)
1. 🔵 **Time of day patterns** - Usage pattern tracking (future enhancement)
2. 🔵 **Decay mechanism** - Forgetting old patterns over time

### Performance Constraints
- **Memory**: Be mindful of usage (up to ~100MB acceptable)
- **Speed**: As fast as possible, target <10ms for predictions
- **Dictionary size**: Support up to ~100MB total

### Configurable UI Features
- **Confidence scores** - Subtle background shading
- **Visual styling by type** - Foreground color or badge for exact/fuzzy/next-word
- **Emoji suggestions** - Context-based, toggleable

### Explicitly Excluded
- ❌ Auto-correction on space press
- ❌ Abbreviation expansion ("don't" → "do not")
- ❌ Emoji expansion (just suggestions)
- ❌ Keyboard shortcuts for prediction selection

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                   KeyboardViewModel                      │
│  - Tracks CurrentInput & LastCompletedWord              │
│  - Calls predictor with context                         │
└──────────────────────┬──────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────┐
│                    WordPredictor                         │
│  - GetSuggestions(prefix, previousWord, context)        │
│  - Returns List<PredictionResult> with metadata         │
└──┬──────────────┬──────────────┬────────────────────────┘
   │              │              │
   ▼              ▼              ▼
┌────────┐  ┌──────────┐  ┌─────────────┐
│ Unigram│  │  Bigram  │  │   Phrase    │
│  Model │  │  Model   │  │   Model     │
└────────┘  └──────────┘  └─────────────┘
```

## Implementation Phases

### Phase 1: Core Data Structures & Models (Foundation)

#### 1.1 Create PredictionResult Model
**New file:** `src/EyeCtrl.Core/Prediction/PredictionResult.cs`

```csharp
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Represents a word prediction with metadata for ranking and UI display.
/// </summary>
public class PredictionResult
{
    public string Word { get; init; } = string.Empty;
    public double Score { get; init; }
    public PredictionType Type { get; init; }
    public double Confidence { get; init; } // 0.0 to 1.0

    public PredictionResult(string word, double score, PredictionType type, double confidence)
    {
        Word = word;
        Score = score;
        Type = type;
        Confidence = confidence;
    }
}

public enum PredictionType
{
    ExactPrefix,      // Direct prefix match
    FuzzyMatch,       // Keyboard-aware fuzzy match
    NextWord,         // Bigram-based next word
    Phrase,           // Multi-word phrase suggestion
    Recency           // Recently used word boost
}
```

**Tests:** `tests/EyeCtrl.Core.Tests/Prediction/PredictionResultTests.cs`

#### 1.2 Create BigramModel
**New file:** `src/EyeCtrl.Core/Prediction/BigramModel.cs`

**✅ IMPLEMENTED** - Case-sensitive storage for German language support

```csharp
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// N-gram language model for next-word prediction.
/// Stores bigram frequencies: P(word2 | word1).
/// Case-sensitive storage to preserve proper casing (e.g., German nouns).
/// </summary>
public class BigramModel
{
    private readonly Dictionary<string, Dictionary<string, int>> _bigrams = new();
    private const int MinFrequency = 2; // Filter out single occurrences

    public void Learn(string word1, string word2)
    {
        // Case-sensitive storage - preserves original casing
        if (!_bigrams.ContainsKey(word1))
            _bigrams[word1] = new Dictionary<string, int>();

        var followers = _bigrams[word1];
        followers[word2] = followers.GetValueOrDefault(word2) + 1;
    }

    public List<(string word, int frequency)> GetNextWords(string previousWord, int maxCount)
    {
        if (string.IsNullOrEmpty(previousWord)) return new List<(string, int)>();

        // Case-sensitive lookup
        if (!_bigrams.TryGetValue(previousWord, out var followers))
            return new List<(string, int)>();

        return followers
            .Where(kv => kv.Value >= MinFrequency)
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public Dictionary<string, Dictionary<string, int>> GetAllBigrams() => _bigrams;

    public void LoadFromDictionary(Dictionary<string, Dictionary<string, int>> data)
    {
        _bigrams.Clear();
        foreach (var (word1, followers) in data)
            _bigrams[word1] = new Dictionary<string, int>(followers);
    }
}
```

**Tests:** `tests/EyeCtrl.Core.Tests/Prediction/BigramModelTests.cs`

#### 1.3 Create PhraseModel
**New file:** `src/EyeCtrl.Core/Prediction/PhraseModel.cs`

**✅ IMPLEMENTED** - Case-sensitive with performance optimization (MaxPhraseLength=7)

```csharp
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Tracks multi-word phrase patterns (2-7 words).
/// Used for suggesting common phrases like "looking forward to".
/// </summary>
public class PhraseModel
{
    private readonly Dictionary<string, Dictionary<string, int>> _phrases = new();
    private const int MinPhraseLength = 2;
    private const int MaxPhraseLength = 7;
    private const int MinFrequency = 2;

    /// <summary>
    /// Learns a phrase by extracting all sub-sequences of length 2-7.
    /// Optimized with index-based iteration to minimize allocations.
    /// </summary>
    public void LearnPhrase(string[] words)
    {
        if (words.Length < MinPhraseLength) return;

        // Extract all sub-phrases of length 2 to MaxPhraseLength
        for (int len = MinPhraseLength; len <= Math.Min(MaxPhraseLength, words.Length); len++)
        {
            for (int i = 0; i <= words.Length - len; i++)
            {
                // Build prefix and full phrase using index-based iteration to avoid allocations
                var prefix = BuildJoinedString(words, i, len - 1);
                var fullPhrase = BuildJoinedString(words, i, len);

                if (!_phrases.ContainsKey(prefix))
                    _phrases[prefix] = new Dictionary<string, int>();

                _phrases[prefix][fullPhrase] = _phrases[prefix].GetValueOrDefault(fullPhrase) + 1;
            }
        }
    }

    /// <summary>
    /// Builds a space-joined string from a segment of the words array without intermediate allocations.
    /// Uses string.Create for optimal performance.
    /// </summary>
    private static string BuildJoinedString(string[] words, int startIndex, int count)
    {
        if (count == 0) return string.Empty;
        if (count == 1) return words[startIndex];

        // Calculate total length to pre-allocate the right size
        int totalLength = words[startIndex].Length;
        for (int i = 1; i < count; i++)
            totalLength += 1 + words[startIndex + i].Length; // +1 for space

        return string.Create(totalLength, (words, startIndex, count), (span, state) =>
        {
            var (w, start, cnt) = state;
            int pos = 0;

            for (int i = 0; i < cnt; i++)
            {
                if (i > 0)
                    span[pos++] = ' ';

                var word = w[start + i];
                word.AsSpan().CopyTo(span[pos..]);
                pos += word.Length;
            }
        });
    }

    public List<(string phrase, int frequency)> GetPhrases(string prefix, int maxCount)
    {
        if (string.IsNullOrEmpty(prefix)) return new List<(string, int)>();

        // Case-sensitive lookup
        if (!_phrases.TryGetValue(prefix, out var matches))
            return new List<(string, int)>();

        return matches
            .Where(kv => kv.Value >= MinFrequency)
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public Dictionary<string, Dictionary<string, int>> GetAllPhrases() => _phrases;

    public void LoadFromDictionary(Dictionary<string, Dictionary<string, int>> data)
    {
        _phrases.Clear();
        foreach (var (prefix, continuations) in data)
            _phrases[prefix] = new Dictionary<string, int>(continuations);
    }
}
```

**Tests:** `tests/EyeCtrl.Core.Tests/Prediction/PhraseModelTests.cs`

#### 1.4 Update FileDictionaryProvider with Persistence Methods
**Modified file:** `src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs`

**✅ IMPLEMENTED** - Instance methods (not extensions) for bigram/phrase persistence with error logging

```csharp
public class FileDictionaryProvider : IDictionaryProvider
{
    private readonly string _userDictionaryPath;
    private readonly ILogger<FileDictionaryProvider>? _logger;

    public FileDictionaryProvider(string basePath, string userPath, string language,
        ILogger<FileDictionaryProvider>? logger = null)
    {
        // ... existing constructor code ...
        _logger = logger;
    }

    // Generates path like "user_dictionary_en_bigrams.json"
    private string GetBigramPath()
    {
        var directory = Path.GetDirectoryName(_userDictionaryPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(_userDictionaryPath);
        var bigramFileName = fileNameWithoutExtension + "_bigrams.json";
        return string.IsNullOrEmpty(directory) ? bigramFileName : Path.Combine(directory, bigramFileName);
    }

    // Generates path like "user_dictionary_en_phrases.json"
    private string GetPhrasePath()
    {
        var directory = Path.GetDirectoryName(_userDictionaryPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(_userDictionaryPath);
        var phraseFileName = fileNameWithoutExtension + "_phrases.json";
        return string.IsNullOrEmpty(directory) ? phraseFileName : Path.Combine(directory, phraseFileName);
    }

    public Dictionary<string, Dictionary<string, int>> LoadBigrams()
    {
        var path = GetBigramPath();
        if (!File.Exists(path)) return [];

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
            return data ?? [];
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to deserialize bigrams from {Path}", path);
            return [];
        }
    }

    public void SaveBigrams(Dictionary<string, Dictionary<string, int>> bigrams)
    {
        var path = GetBigramPath();
        var json = JsonSerializer.Serialize(bigrams, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public Dictionary<string, Dictionary<string, int>> LoadPhrases()
    {
        var path = GetPhrasePath();
        if (!File.Exists(path)) return [];

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
            return data ?? [];
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to deserialize phrases from {Path}", path);
            return [];
        }
    }

    public void SavePhrases(Dictionary<string, Dictionary<string, int>> phrases)
    {
        var path = GetPhrasePath();
        var json = JsonSerializer.Serialize(phrases, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
```

**Tests:** `tests/EyeCtrl.Core.Tests/Prediction/FileDictionaryProviderTests.cs`

### Phase 2: Enhanced WordPredictor (Core Intelligence)

#### 2.1 Update WordPredictor with Context Awareness
**Modified file:** `src/EyeCtrl.Core/Prediction/WordPredictor.cs`

Major refactoring to support multiple prediction sources:

```csharp
namespace EyeCtrl.Core.Prediction;

public class WordPredictor
{
    private readonly IDictionaryProvider _provider;
    private readonly Dictionary<string, int> _words;
    private readonly Dictionary<char, HashSet<char>> _adjacencyMap;
    private readonly BigramModel _bigrams;
    private readonly PhraseModel _phrases;

    private const double MaxFuzzyDistance = 1.5;

    // Scoring weights
    private const double ExactPrefixMultiplier = 3.0;
    private const double NextWordMultiplier = 5.0;
    private const double PhraseMultiplier = 4.0;
    private const double BigramContextBoost = 1.3;

    public WordPredictor(IDictionaryProvider provider, string layoutName = "QWERTY")
    {
        _provider = provider;
        _words = provider.Load();
        _adjacencyMap = KeyboardAdjacencyMap.For(layoutName);
        _bigrams = new BigramModel();
        _phrases = new PhraseModel();

        // Load bigrams and phrases if provider supports extensions
        if (provider is FileDictionaryProvider fileProvider)
        {
            var bigramData = fileProvider.LoadBigrams();
            if (bigramData.Count > 0)
                _bigrams.LoadFromDictionary(bigramData);

            var phraseData = fileProvider.LoadPhrases();
            if (phraseData.Count > 0)
                _phrases.LoadFromDictionary(phraseData);
        }
    }

    public List<PredictionResult> GetSuggestions(string prefix, string? context = null, int maxCount = 5)
    {
        // Use case-insensitive dictionary to prevent duplicate suggestions with different casing
        var scored = new Dictionary<string, (double score, PredictionType type, string displayWord, double? fuzzyDistance)>(StringComparer.OrdinalIgnoreCase);
        var hasContext = !string.IsNullOrWhiteSpace(context);

        // Precompute context boost set once (performance optimization)
        HashSet<string>? contextBoostWords = null;
        if (hasContext)
        {
            var nextWordsList = _bigrams.GetNextWords(context!, 100);
            contextBoostWords = new HashSet<string>(
                nextWordsList.Select(nw => nw.word.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }

        // Category 1: Next-word predictions (when we have context)
        if (hasContext)
        {
            // Check bigram predictions (case-sensitive)
            var nextWords = _bigrams.GetNextWords(context!, maxCount * 2);
            foreach (var (word, frequency) in nextWords)
            {
                if (string.IsNullOrEmpty(prefix) || word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var baseFreq = _words.GetValueOrDefault(word.ToLowerInvariant(), 1);
                    var score = baseFreq * frequency * NextWordMultiplier;
                    var key = word.ToLowerInvariant();

                    // Keep higher-cased version for display (preserve original casing from bigrams)
                    if (!scored.ContainsKey(key) || scored[key].score < score)
                        scored[key] = (score, PredictionType.NextWord, word, null);
                }
            }

            // Category 2: Phrase predictions (case-sensitive)
            var phrases = _phrases.GetPhrases(context!, maxCount * 2);
            foreach (var (phrase, frequency) in phrases)
            {
                // Return the remaining phrase after the current context
                var phrasePrefix = context! + " ";
                var phraseSuggestion = phrase;

                if (phrase.StartsWith(phrasePrefix, StringComparison.Ordinal))
                    phraseSuggestion = phrase[phrasePrefix.Length..].TrimStart();

                if (!string.IsNullOrWhiteSpace(phraseSuggestion) &&
                    (string.IsNullOrEmpty(prefix) || phraseSuggestion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    var score = frequency * PhraseMultiplier;
                    var key = phraseSuggestion.ToLowerInvariant();

                    if (!scored.ContainsKey(key) || scored[key].score < score)
                        scored[key] = (score, PredictionType.Phrase, phraseSuggestion, null);
                }
            }
        }

        // Category 3: Exact prefix matches
        // Skip if prefix is empty AND we have context (context predictions take priority)
        var shouldIncludeStandardMatching = !string.IsNullOrEmpty(prefix) || !hasContext;

        if (shouldIncludeStandardMatching)
        {
            foreach (var (word, frequency) in _words)
            {
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var score = frequency * ExactPrefixMultiplier;

                    // Context boost for bigram relevance (using precomputed set)
                    if (hasContext && contextBoostWords != null && contextBoostWords.Contains(word))
                        score *= BigramContextBoost;

                    if (!scored.ContainsKey(word) || scored[word].score < score)
                        scored[word] = (score, PredictionType.ExactPrefix, word, null);
                    continue;
                }

                // Skip fuzzy matching for words already in scored (from bigram/phrase)
                if (scored.ContainsKey(word))
                    continue;

                // Category 4: Fuzzy matches (only if we have a non-empty prefix)
                if (string.IsNullOrEmpty(prefix))
                    continue;

                var wordPrefix = word.Length >= prefix.Length
                    ? word[..prefix.Length]
                    : word;

                var dist = FuzzyEditDistance.Compute(prefix.ToLowerInvariant(), wordPrefix.ToLowerInvariant(), _adjacencyMap);
                if (dist > MaxFuzzyDistance) continue;

                double multiplier = dist <= 0.5 ? 1.5 : dist <= 1.0 ? 1.2 : 1.0;
                var fuzzyScore = frequency * multiplier;

                // Context boost for bigram relevance (using precomputed set)
                if (hasContext && contextBoostWords != null && contextBoostWords.Contains(word))
                    fuzzyScore *= BigramContextBoost;

                if (!scored.ContainsKey(word))
                    scored[word] = (fuzzyScore, PredictionType.FuzzyMatch, word, dist);
            }
        }

        // Convert to PredictionResult and sort
        var results = scored
            .Select(kv =>
            {
                // Type-specific confidence calculation
                double confidence = kv.Value.type switch
                {
                    PredictionType.NextWord => Math.Min(1.0, kv.Value.score / 500.0),
                    PredictionType.Phrase => Math.Min(1.0, kv.Value.score / 400.0),
                    PredictionType.ExactPrefix => 1.0,
                    PredictionType.FuzzyMatch => kv.Value.fuzzyDistance.HasValue
                        ? Math.Max(0.0, 1.0 - (kv.Value.fuzzyDistance.Value / MaxFuzzyDistance))
                        : 0.5,
                    _ => Math.Min(1.0, kv.Value.score / 1000.0)
                };
                return new PredictionResult(kv.Value.displayWord, kv.Value.score, kv.Value.type, confidence);
            })
            .OrderByDescending(r => r.Score)
            .Take(maxCount)
            .ToList();

        return results;
    }

    public void LearnWord(string word)
    {
        var lower = word.ToLowerInvariant();
        if (_words.ContainsKey(lower))
            _words[lower]++;
        else
            _words[lower] = 1;
    }

    public void LearnSequence(string[] words)
    {
        // Learn individual words
        foreach (var word in words)
            LearnWord(word);

        // Learn bigrams
        for (int i = 0; i < words.Length - 1; i++)
            _bigrams.Learn(words[i], words[i + 1]);

        // Learn phrases (2-7 word sequences)
        if (words.Length >= 2)
            _phrases.LearnPhrase(words);
    }

    public void SaveUserDictionary()
    {
        _provider.Save(_words);

        // Save bigrams and phrases if provider supports extensions
        if (_provider is FileDictionaryProvider fileProvider)
        {
            fileProvider.SaveBigrams(_bigrams.GetAllBigrams());
            fileProvider.SavePhrases(_phrases.GetAllPhrases());
        }
    }
}
```

**Tests:** Update `tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs` with new scenarios

### Phase 3: Smart Capitalization

#### 3.1 Create CapitalizationHelper
**New file:** `src/EyeCtrl.Core/Prediction/CapitalizationHelper.cs`

```csharp
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Handles automatic capitalization for sentence boundaries and proper nouns.
/// </summary>
public static class CapitalizationHelper
{
    private static readonly HashSet<char> SentenceEnders = new() { '.', '!', '?' };

    public static bool ShouldCapitalize(string recentText)
    {
        if (string.IsNullOrWhiteSpace(recentText))
            return true; // Start of text

        var trimmed = recentText.TrimEnd();
        if (trimmed.Length == 0)
            return true;

        // Check for sentence ending
        var lastChar = trimmed[^1];
        if (SentenceEnders.Contains(lastChar))
            return true;

        return false;
    }

    public static string ApplyCapitalization(string word, bool shouldCapitalize)
    {
        if (!shouldCapitalize || string.IsNullOrEmpty(word))
            return word;

        return char.ToUpperInvariant(word[0]) + word[1..];
    }

    public static bool IsProperNoun(string word, Dictionary<string, int> properNouns)
    {
        return properNouns.ContainsKey(word.ToLowerInvariant());
    }
}
```

### Phase 4: Settings & Configuration

#### 4.1 Add Prediction Settings
**Modified file:** `src/EyeCtrl.Core/Settings/KeyboardSettings.cs`

```csharp
[ObservableProperty] private bool _predictionEnabled = true;
[ObservableProperty] private bool _nextWordPredictionEnabled = true;  // NEW
[ObservableProperty] private bool _smartCapitalizationEnabled = true; // NEW
[ObservableProperty] private bool _showPredictionConfidence = false;  // NEW
[ObservableProperty] private bool _showPredictionTypes = false;       // NEW
[ObservableProperty] private bool _emojiSuggestionsEnabled = false;   // NEW
```

### Phase 5: ViewModel & UI Integration

#### 5.1 Update KeyboardViewModel
**Modified file:** `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`

**✅ IMPLEMENTED** - Context tracking with `_previousWord` field

```csharp
// Add tracking for previous word
private string? _previousWord;

public void HandleSpace()
{
    if (!HasSystemModifiers)
    {
        if (CurrentInput.Length > 0)
        {
            _predictor?.LearnWord(CurrentInput);
            _previousWord = CurrentInput;  // Track for context
        }
        CurrentInput = "";
    }
    _messenger.Send(new KeyActivatedMessage(" ", ModifiersSnapshot));
    ClearOneShotModifiers();
}

public void HandleEnter()
{
    if (!HasSystemModifiers)
    {
        CurrentInput = "";
        _previousWord = null;  // Clear context on new sentence
    }
    _messenger.Send(new KeyActivatedMessage("\n", ModifiersSnapshot));
    ClearOneShotModifiers();
}

public void HandlePredictionSelected(string word)
{
    _predictor?.LearnWord(word);
    var deleteCount = CurrentInput.Length;
    CurrentInput = "";
    _previousWord = word;  // Track selected prediction as context
    _messenger.Send(new PredictionSelectedMessage(word, deleteCount));
}

public List<string> GetSuggestions()
{
    if (_predictor == null) return new List<string>();
    // Pass context for bigram/phrase predictions
    return _predictor.GetSuggestions(CurrentInput, _previousWord, maxCount: 5)
        .Select(r => r.Word)
        .ToList();
}
```

#### 5.2 Update KeyboardWindow for Styled Predictions
**Modified file:** `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

Add visual styling based on prediction type:

```csharp
public void UpdatePredictions()
{
    if (_viewModel == null) return;

    _predictionDwellDetector.ClearTargets();
    _predictionTargetsInitialized = false;
    _predictionBrushes.Clear();
    PredictionBar.Children.Clear();

    var suggestions = _viewModel.GetSuggestionsWithContext();

    for (int i = 0; i < suggestions.Count; i++)
    {
        var result = suggestions[i];
        var predId = $"pred_{i}";

        var predLabel = new TextBlock
        {
            Text = result.Word,
            Foreground = GetPredictionForeground(result.Type),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var baseColor = PredictionBarColor;
        if (_settings?.Keyboard.ShowPredictionConfidence == true)
        {
            // Adjust opacity based on confidence
            var alpha = (byte)(150 + (result.Confidence * 80));
            baseColor = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        }

        var brush = new SolidColorBrush(baseColor);
        _predictionBrushes.Add(brush);

        var btn = new Border
        {
            MinWidth = 120,
            Height = _keySize,
            Margin = new Thickness(3),
            Padding = new Thickness(16, 0, 16, 0),
            Background = brush,
            CornerRadius = new CornerRadius(6),
            Child = predLabel
        };

        // Add type badge if enabled
        if (_settings?.Keyboard.ShowPredictionTypes == true)
        {
            var badge = CreateTypeBadge(result.Type);
            if (badge != null)
            {
                var stack = new StackPanel { Orientation = Orientation.Horizontal };
                stack.Children.Add(predLabel);
                stack.Children.Add(badge);
                btn.Child = stack;
            }
        }

        PredictionBar.Children.Add(btn);
        _predictionDwellDetector.AddTarget(
            new DwellTarget(predId, new PhysicalPoint(0, 0),
                DpiHelper.LogicalToPhysicalX(_keySize / 2), _predictionDwellMs));
    }
}

private static Brush GetPredictionForeground(PredictionType type)
{
    return type switch
    {
        PredictionType.ExactPrefix => Brushes.White,
        PredictionType.FuzzyMatch => new SolidColorBrush(Color.FromRgb(200, 200, 255)),
        PredictionType.NextWord => new SolidColorBrush(Color.FromRgb(200, 255, 200)),
        PredictionType.Phrase => new SolidColorBrush(Color.FromRgb(255, 255, 200)),
        _ => Brushes.White
    };
}

private static Border? CreateTypeBadge(PredictionType type)
{
    string symbol = type switch
    {
        PredictionType.NextWord => "→",
        PredictionType.Phrase => "⋯",
        PredictionType.FuzzyMatch => "~",
        _ => null
    };

    if (symbol == null) return null;

    return new Border
    {
        Child = new TextBlock
        {
            Text = symbol,
            FontSize = 12,
            Foreground = Brushes.Gray,
            Margin = new Thickness(4, 0, 0, 0)
        }
    };
}
```

### Phase 6: Expand Base Dictionaries

#### 6.1 Dictionary Expansion Strategy
- Target: Expand from ~600 to 5,000-10,000 words per language
- Source: Public domain frequency lists (e.g., OpenSubtitles, Google N-grams)
- Quality control: Manual review of top 10K, filter profanity/errors
- Format: Maintain existing JSON structure `{"word": frequency}`

**Files to update:**
- `src/EyeCtrl.App/Resources/Dictionaries/en.json`
- `src/EyeCtrl.App/Resources/Dictionaries/fr.json`
- `src/EyeCtrl.App/Resources/Dictionaries/es.json`
- `src/EyeCtrl.App/Resources/Dictionaries/de.json`
- `src/EyeCtrl.App/Resources/Dictionaries/pl.json`

## Testing Strategy

### Unit Tests
1. `BigramModelTests.cs` - Learn, retrieve, serialize
2. `PhraseModelTests.cs` - Multi-word learning, retrieval
3. `CapitalizationHelperTests.cs` - Sentence detection, capitalization
4. `WordPredictorTests.cs` - Context-aware predictions, scoring
5. `PredictionResultTests.cs` - Model validation

### Integration Tests
1. End-to-end prediction flow with keyboard
2. Persistence of bigrams/phrases across restarts
3. Settings integration
4. Performance benchmarks (<10ms target)

### Performance Tests
1. Prediction latency with 10K words
2. Memory usage with full dictionaries
3. Bigram/phrase storage size limits

## Migration & Backward Compatibility

### Existing User Data
- Current user dictionaries remain compatible
- New bigram/phrase files created on first use
- Old settings load correctly (new fields have defaults)

### Settings Migration
- All new settings have safe defaults (features off by default except basics)
- No breaking changes to existing `KeyboardSettings`

## Implementation Order

### Sprint 1: Foundation (1-2 days)
- [ ] PredictionResult model
- [ ] BigramModel + tests
- [ ] PhraseModel + tests
- [ ] Update IDictionaryProvider interface
- [ ] FileDictionaryProvider implementation

### Sprint 2: Core Intelligence (2-3 days)
- [ ] Refactor WordPredictor with context
- [ ] CapitalizationHelper
- [ ] Update KeyboardViewModel with word tracking
- [ ] Comprehensive unit tests

### Sprint 3: UI Integration (2 days)
- [ ] Settings additions
- [ ] KeyboardWindow visual styling
- [ ] Prediction confidence display
- [ ] Type badges

### Sprint 4: Polish & Performance (1-2 days)
- [ ] Performance optimization
- [ ] Dictionary expansion (or separate sprint)
- [ ] Integration tests
- [ ] Documentation

## Performance Considerations

### Memory Management
- Lazy-load bigram/phrase models
- Cap phrase storage at 1000 entries
- Periodic cleanup of low-frequency bigrams (< 2 occurrences)

### Speed Optimization
- Current fuzzy matching is O(n) - acceptable for 10K words
- Future: Consider trie structure if needed (Phase 7)
- Bigram lookup is O(1) with dictionary
- Target: <10ms for GetSuggestions call

### Storage
- Base dictionaries: ~5-10MB each (5 languages = ~50MB)
- User dictionary: ~100KB typical
- Bigrams: ~500KB-1MB per language
- Phrases: ~200-500KB per language
- **Total: ~60-70MB well under 100MB limit**

## Future Enhancements (Post-MVP)

### Phase 7: Case-Sensitivity Enhancements (High Priority)

**Context:** Current implementation uses case-sensitive storage for German language support (nouns must be capitalized), but this creates usability challenges when predictions don't match user's casing expectations.

**Goals:**
1. Preserve proper casing for German nouns and other language-specific requirements
2. Improve flexibility by offering case-insensitive fallback suggestions
3. Learn from user corrections to improve stored casing over time
4. Handle ALL-CAPS input gracefully

#### 7.1 Case-Insensitive Fallback Matching

**Problem:** If user types "haus" but system only has "Haus" (German noun), no suggestion appears.

**Solution:** Implement tiered matching with priority scoring:

```csharp
public List<PredictionResult> GetSuggestions(string prefix, string? previousWord, int maxCount = 5)
{
    var scored = new Dictionary<string, (double score, PredictionType type)>();

    // Tier 1: Exact case match (highest priority)
    foreach (var (word, frequency) in nextWords)
    {
        if (word.StartsWith(prefix, StringComparison.Ordinal))
        {
            scored[word] = (frequency * BigramBoost * 1.2, PredictionType.NextWord);
        }
    }

    // Tier 2: Case-insensitive match (lower priority)
    foreach (var (word, frequency) in nextWords)
    {
        if (!scored.ContainsKey(word) &&
            word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            scored[word] = (frequency * BigramBoost * 0.8, PredictionType.NextWord);
        }
    }

    // Continue with other prediction types...
}
```

**Benefits:**
- User typing "haus" still sees "Haus" suggestion (just with lower priority)
- Exact case matches get preference when available
- Works for all languages, not just German

#### 7.2 ALL-CAPS Normalization

**Problem:** User types "HELLO" but system should suggest normal casing "Hello" or "hello".

**Solution:** Detect ALL-CAPS input and apply smart normalization:

```csharp
public static class CasingHelper
{
    public static bool IsAllCaps(string text)
    {
        return text.Length > 1 && text.All(char.IsUpper);
    }

    public static string NormalizeAllCaps(string word)
    {
        if (!IsAllCaps(word)) return word;

        // Convert to title case (first letter upper, rest lower)
        // Unless it's a known acronym
        if (IsKnownAcronym(word)) return word;

        return char.ToUpper(word[0]) + word[1..].ToLower();
    }

    private static bool IsKnownAcronym(string word)
    {
        // Common acronyms that should stay all-caps
        var acronyms = new HashSet<string> { "USA", "UK", "NATO", "FBI", "CIA", "NASA" };
        return acronyms.Contains(word);
    }
}
```

**Implementation:**
- Apply during prefix matching: `var normalizedPrefix = CasingHelper.NormalizeAllCaps(prefix);`
- Store normalized version in BigramModel/PhraseModel
- Handle in `LearnSequence()` before learning patterns

#### 7.3 Learning from User Corrections

**Problem:** System learned "haus" but user always types "Haus". System should adapt.

**Solution:** Track casing frequency and update preferences:

```csharp
public class CasingPreferenceTracker
{
    // Tracks how often each casing variant is used
    private readonly Dictionary<string, Dictionary<string, int>> _casingVariants = new();

    public void RecordCasing(string word)
    {
        var lowerKey = word.ToLowerInvariant();

        if (!_casingVariants.ContainsKey(lowerKey))
            _casingVariants[lowerKey] = new Dictionary<string, int>();

        var variants = _casingVariants[lowerKey];
        variants[word] = variants.GetValueOrDefault(word) + 1;
    }

    public string GetPreferredCasing(string word)
    {
        var lowerKey = word.ToLowerInvariant();

        if (!_casingVariants.TryGetValue(lowerKey, out var variants))
            return word;

        // Return most frequently used casing
        return variants.OrderByDescending(kv => kv.Value).First().Key;
    }
}
```

**Integration Points:**
- Call `RecordCasing()` in `KeyboardViewModel.HandleSpace()` when word is completed
- Use `GetPreferredCasing()` in BigramModel/PhraseModel when returning predictions
- Persist casing preferences to `user_dictionary_{lang}_casing.json`

#### 7.4 Language-Specific Abbreviations

**Problem:** Current `CommonAbbreviations` in `CapitalizationHelper` is English-only.

**Solution:** Create language-specific abbreviation sets:

```csharp
public static class LanguageAbbreviations
{
    private static readonly Dictionary<string, HashSet<string>> AbbreviationsByLanguage = new()
    {
        ["en"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mrs", "ms", "dr", "prof", "sr", "jr",
            "etc", "vs", "i.e", "e.g", "a.m", "p.m",
            "st", "ave", "blvd", "dept", "inc", "ltd", "co"
        },
        ["de"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "z.b", "bzw", "usw", "u.a", "d.h", "v.a",
            "dr", "prof", "hrsg", "bd", "jh", "s", "str"
        },
        ["fr"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "m", "mme", "mlle", "dr", "prof",
            "etc", "av", "bd", "r", "c.-à-d", "p.ex"
        },
        ["es"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sr", "sra", "srta", "dr", "dra",
            "etc", "p.ej", "av", "c", "n", "s.a"
        }
    };

    public static HashSet<string> GetAbbreviations(string languageCode)
    {
        return AbbreviationsByLanguage.TryGetValue(languageCode, out var abbrevs)
            ? abbrevs
            : AbbreviationsByLanguage["en"]; // Fallback to English
    }
}
```

**Changes to CapitalizationHelper:**
```csharp
public static bool ShouldCapitalize(string currentText, string languageCode = "en")
{
    // ... existing logic ...

    var abbreviations = LanguageAbbreviations.GetAbbreviations(languageCode);
    if (abbreviations.Contains(lastWordTrimmed))
        return false; // Don't capitalize after abbreviation

    // ... rest of logic ...
}
```

#### Implementation Timeline

**Phase 7.1: Case-Insensitive Fallback (1-2 days)**
- Update WordPredictor with tiered matching
- Add tests for case-insensitive fallback
- Ensure German nouns still get priority with exact case

**Phase 7.2: ALL-CAPS Normalization (1 day)**
- Implement CasingHelper utility
- Integrate with LearnSequence
- Add tests for acronym detection

**Phase 7.3: Casing Preference Learning (2 days)**
- Implement CasingPreferenceTracker
- Add persistence layer
- Integrate with BigramModel/PhraseModel
- Add tests for preference tracking and updates

**Phase 7.4: Language-Specific Abbreviations (1 day)**
- Create LanguageAbbreviations utility
- Update CapitalizationHelper
- Add abbreviations for de, fr, es
- Add tests for each language

**Total Estimate: 5-6 days**

#### Testing Strategy

1. **Case Matching Tests**
   - Exact case match gets higher score than case-insensitive
   - Case-insensitive fallback works when exact match unavailable
   - German nouns (Haus) suggested even when user types lowercase (haus)

2. **ALL-CAPS Tests**
   - "HELLO" normalized to "Hello"
   - Acronyms preserved: "USA" stays "USA"
   - ALL-CAPS detection with single letter excluded

3. **Casing Preference Tests**
   - User typing "Haus" 10 times updates preference
   - Next suggestion uses preferred casing
   - Preferences persist across sessions

4. **Language Abbreviation Tests**
   - English abbreviations don't trigger capitalization
   - German "z.B." handled correctly
   - French "M." doesn't capitalize next word
   - Spanish abbreviations work correctly

#### Migration Strategy

- All changes are backward compatible
- Existing casing data continues to work
- New casing preferences file created on first use
- No settings changes required (all enhancements automatic)

### Phase 8: Advanced Features (Future)
- Time-based pattern tracking
- Decay mechanism for old patterns
- Trie-based prefix search for very large dictionaries
- Cloud sync for personal dictionaries

### Phase 9: Machine Learning (Future)
- Transformer-based next-word prediction
- Personalized model fine-tuning
- Multi-word context (trigrams+)

## Success Metrics

### Quantitative
- Prediction acceptance rate > 30%
- Average prediction position < 2.0
- Prediction latency < 10ms (99th percentile)
- Memory usage < 100MB

### Qualitative
- User reports faster typing
- Fewer gaze dwells per sentence
- Positive feedback on contextual suggestions

## Risk Mitigation

### Technical Risks
- **Performance degradation**: Implement performance tests early
- **Memory bloat**: Add storage limits and cleanup mechanisms
- **Complexity**: Phase implementation, thorough testing

### User Experience Risks
- **Overwhelming predictions**: Make features configurable
- **Inaccurate suggestions**: Start conservative, iterate based on feedback
- **Privacy concerns**: Keep all data local, document clearly

## Conclusion

This implementation plan delivers intelligent, context-aware word prediction while respecting performance constraints and user preferences. The phased approach allows for iterative development and testing, with clear success metrics and risk mitigation strategies.
