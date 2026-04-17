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

```csharp
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// N-gram language model for next-word prediction.
/// Stores bigram frequencies: P(word2 | word1).
/// </summary>
public class BigramModel
{
    private readonly Dictionary<string, Dictionary<string, int>> _bigrams = new();
    private readonly int _minFrequency = 2; // Filter out single occurrences

    public void Learn(string word1, string word2)
    {
        var key = word1.ToLowerInvariant();
        if (!_bigrams.ContainsKey(key))
            _bigrams[key] = new Dictionary<string, int>();

        var followers = _bigrams[key];
        followers[word2.ToLowerInvariant()] = followers.GetValueOrDefault(word2.ToLowerInvariant()) + 1;
    }

    public List<(string word, int frequency)> GetNextWords(string previousWord, int maxCount)
    {
        if (string.IsNullOrEmpty(previousWord)) return new List<(string, int)>();

        if (!_bigrams.TryGetValue(previousWord.ToLowerInvariant(), out var followers))
            return new List<(string, int)>();

        return followers
            .Where(kv => kv.Value >= _minFrequency)
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

```csharp
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Tracks multi-word phrase patterns (2-4 words).
/// Used for suggesting common phrases like "looking forward to".
/// </summary>
public class PhraseModel
{
    private readonly Dictionary<string, Dictionary<string, int>> _phrases = new();
    private const int MinPhraseLength = 2;
    private const int MaxPhraseLength = 4;

    public void LearnPhrase(string[] words)
    {
        if (words.Length < MinPhraseLength) return;

        for (int len = MinPhraseLength; len <= Math.Min(MaxPhraseLength, words.Length); len++)
        {
            for (int i = 0; i <= words.Length - len; i++)
            {
                var prefix = string.Join(" ", words.Skip(i).Take(len - 1)).ToLowerInvariant();
                var fullPhrase = string.Join(" ", words.Skip(i).Take(len)).ToLowerInvariant();

                if (!_phrases.ContainsKey(prefix))
                    _phrases[prefix] = new Dictionary<string, int>();

                _phrases[prefix][fullPhrase] = _phrases[prefix].GetValueOrDefault(fullPhrase) + 1;
            }
        }
    }

    public List<(string phrase, int frequency)> GetPhrases(string prefix, int maxCount)
    {
        if (!_phrases.TryGetValue(prefix.ToLowerInvariant(), out var matches))
            return new List<(string, int)>();

        return matches
            .Where(kv => kv.Value >= 2) // Min 2 occurrences
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

#### 1.4 Update IDictionaryProvider Interface
**Modified file:** `src/EyeCtrl.Core/Prediction/IDictionaryProvider.cs`

Add methods for bigrams and phrases:
```csharp
public interface IDictionaryProvider
{
    Dictionary<string, int> Load();
    void Save(Dictionary<string, int> words);

    // New methods for bigrams and phrases
    Dictionary<string, Dictionary<string, int>>? LoadBigrams();
    void SaveBigrams(Dictionary<string, Dictionary<string, int>> bigrams);

    Dictionary<string, Dictionary<string, int>>? LoadPhrases();
    void SavePhrases(Dictionary<string, Dictionary<string, int>> phrases);
}
```

**Modified file:** `src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs`

Implement new methods:
```csharp
public Dictionary<string, Dictionary<string, int>>? LoadBigrams()
{
    var bigramPath = _userDictionaryPath.Replace(".json", "_bigrams.json");
    if (!File.Exists(bigramPath)) return null;

    var json = File.ReadAllText(bigramPath);
    return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
}

public void SaveBigrams(Dictionary<string, Dictionary<string, int>> bigrams)
{
    var bigramPath = _userDictionaryPath.Replace(".json", "_bigrams.json");
    var directory = Path.GetDirectoryName(bigramPath);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    var json = JsonSerializer.Serialize(bigrams, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(bigramPath, json);
}

public Dictionary<string, Dictionary<string, int>>? LoadPhrases()
{
    var phrasePath = _userDictionaryPath.Replace(".json", "_phrases.json");
    if (!File.Exists(phrasePath)) return null;

    var json = File.ReadAllText(phrasePath);
    return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
}

public void SavePhrases(Dictionary<string, Dictionary<string, int>> phrases)
{
    var phrasePath = _userDictionaryPath.Replace(".json", "_phrases.json");
    var directory = Path.GetDirectoryName(phrasePath);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    var json = JsonSerializer.Serialize(phrases, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(phrasePath, json);
}
```

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
    private readonly BigramModel _bigramModel;
    private readonly PhraseModel _phraseModel;

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
        _bigramModel = new BigramModel();
        _phraseModel = new PhraseModel();

        // Load persisted bigrams and phrases
        var bigrams = provider.LoadBigrams();
        if (bigrams != null)
            _bigramModel.LoadFromDictionary(bigrams);

        var phrases = provider.LoadPhrases();
        if (phrases != null)
            _phraseModel.LoadFromDictionary(phrases);
    }

    public List<PredictionResult> GetSuggestions(
        string prefix,
        string? previousWord = null,
        int maxCount = 5)
    {
        var scored = new List<PredictionResult>();
        var lowerPrefix = prefix.ToLowerInvariant();

        // Category 1: Next-word predictions (when prefix is empty and we have context)
        if (string.IsNullOrEmpty(lowerPrefix) && !string.IsNullOrEmpty(previousWord))
        {
            var nextWords = _bigramModel.GetNextWords(previousWord, maxCount * 2);
            foreach (var (word, freq) in nextWords)
            {
                var baseFreq = _words.GetValueOrDefault(word, 1);
                var score = baseFreq * freq * NextWordMultiplier;
                var confidence = Math.Min(1.0, freq / 100.0); // Normalize to 0-1

                scored.Add(new PredictionResult(word, score, PredictionType.NextWord, confidence));
            }
        }

        // Category 2: Phrase predictions
        if (!string.IsNullOrEmpty(previousWord))
        {
            var contextPhrase = previousWord;
            var phrases = _phraseModel.GetPhrases(contextPhrase, maxCount);
            foreach (var (phrase, freq) in phrases)
            {
                if (phrase.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var score = freq * PhraseMultiplier;
                    var confidence = Math.Min(1.0, freq / 50.0);
                    scored.Add(new PredictionResult(phrase, score, PredictionType.Phrase, confidence));
                }
            }
        }

        // Category 3: Exact prefix matches
        foreach (var (word, frequency) in _words)
        {
            if (word.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var score = frequency * ExactPrefixMultiplier;
                var confidence = 1.0; // Exact match = high confidence

                // Context boost for bigram relevance
                if (!string.IsNullOrEmpty(previousWord))
                {
                    var nextWords = _bigramModel.GetNextWords(previousWord, 100);
                    if (nextWords.Any(nw => nw.word == word))
                        score *= BigramContextBoost;
                }

                scored.Add(new PredictionResult(word, score, PredictionType.ExactPrefix, confidence));
            }
        }

        // Category 4: Fuzzy matches (only if we have a prefix)
        if (!string.IsNullOrEmpty(lowerPrefix))
        {
            foreach (var (word, frequency) in _words)
            {
                // Skip if already added as exact match
                if (word.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var compareTarget = word.Length >= lowerPrefix.Length
                    ? word[..lowerPrefix.Length]
                    : word;

                var dist = FuzzyEditDistance.Compute(lowerPrefix, compareTarget, _adjacencyMap);
                if (dist > MaxFuzzyDistance) continue;

                double multiplier = dist <= 0.5 ? 1.5 : dist <= 1.0 ? 1.2 : 1.0;
                var score = frequency * multiplier;
                var confidence = Math.Max(0.0, 1.0 - (dist / MaxFuzzyDistance));

                // Context boost
                if (!string.IsNullOrEmpty(previousWord))
                {
                    var nextWords = _bigramModel.GetNextWords(previousWord, 100);
                    if (nextWords.Any(nw => nw.word == word))
                        score *= BigramContextBoost;
                }

                scored.Add(new PredictionResult(word, score, PredictionType.FuzzyMatch, confidence));
            }
        }

        // Return top results
        return scored
            .OrderByDescending(r => r.Score)
            .Take(maxCount)
            .ToList();
    }

    public void LearnWord(string word)
    {
        var lower = word.ToLowerInvariant();
        if (_words.ContainsKey(lower))
            _words[lower]++;
        else
            _words[lower] = 1;
    }

    public void LearnBigram(string word1, string word2)
    {
        if (string.IsNullOrWhiteSpace(word1) || string.IsNullOrWhiteSpace(word2))
            return;

        _bigramModel.Learn(word1, word2);
    }

    public void LearnPhrase(string[] words)
    {
        if (words.Length < 2) return;
        _phraseModel.LearnPhrase(words);
    }

    public void SaveUserDictionary()
    {
        _provider.Save(_words);
        _provider.SaveBigrams(_bigramModel.GetAllBigrams());
        _provider.SavePhrases(_phraseModel.GetAllPhrases());
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

```csharp
// Add tracking for previous word
private string _lastCompletedWord = "";
private readonly List<string> _recentWords = new(10);

public void ActivateKey(string output, string? shiftOutput)
{
    var actualOutput = IsShifted && !string.IsNullOrEmpty(shiftOutput)
        ? shiftOutput
        : output;

    if (!HasSystemModifiers)
    {
        CurrentInput += actualOutput;
        _predictor?.LearnWord(actualOutput);
    }
    _messenger.Send(new KeyActivatedMessage(actualOutput, ModifiersSnapshot));
    ClearOneShotModifiers();
}

public void HandleSpace()
{
    if (!HasSystemModifiers)
    {
        if (CurrentInput.Length > 0)
        {
            _predictor?.LearnWord(CurrentInput);

            // Learn bigram
            if (!string.IsNullOrEmpty(_lastCompletedWord))
                _predictor?.LearnBigram(_lastCompletedWord, CurrentInput);

            _lastCompletedWord = CurrentInput;
            _recentWords.Add(CurrentInput);
            if (_recentWords.Count > 10)
                _recentWords.RemoveAt(0);

            // Learn phrase if we have enough words
            if (_recentWords.Count >= 2)
                _predictor?.LearnPhrase(_recentWords.TakeLast(4).ToArray());

            CurrentInput = "";
        }
    }
    _messenger.Send(new KeyActivatedMessage(" ", ModifiersSnapshot));
    ClearOneShotModifiers();
}

public List<PredictionResult> GetSuggestionsWithContext()
{
    if (_predictor == null) return new List<PredictionResult>();

    var previousWord = string.IsNullOrEmpty(CurrentInput) ? _lastCompletedWord : null;
    return _predictor.GetSuggestions(CurrentInput, previousWord, maxCount: 5);
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

### Phase 7: Advanced Features (Future)
- Time-based pattern tracking
- Decay mechanism for old patterns
- Trie-based prefix search for very large dictionaries
- Cloud sync for personal dictionaries

### Phase 8: Machine Learning (Future)
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
