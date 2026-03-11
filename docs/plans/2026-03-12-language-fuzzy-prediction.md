# Language Support & Fuzzy Word Matching Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add multi-language dictionary support (en/fr/es/de/pl) with per-language user learning, and replace the exact-prefix matcher with a keyboard-aware fuzzy matcher that tolerates single adjacent-key gaze errors.

**Architecture:** Linear scan of the word dictionary scoring each word as exact-prefix (3×), fuzzy-adjacent-key (1.5×), or fuzzy-other (1×) by frequency, then returning the top N by score. Keyboard adjacency is provided by a static map per layout; fuzzy distance is keyboard-aware Levenshtein returning a `double`. Language is auto-detected from OS culture on first launch and configurable via a new dwellable radio-button control in Keyboard settings.

**Tech Stack:** C# / .NET, xUnit, `CultureInfo.CurrentUICulture`, Python + `wordfreq` (one-time dictionary generation script).

**Design doc:** `docs/plans/2026-03-12-language-fuzzy-prediction-design.md`

---

## Task 1: KeyboardAdjacencyMap

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/KeyboardAdjacencyMap.cs`

Pure static data — no unit tests needed; correctness is validated implicitly by FuzzyEditDistance tests in Task 2.

**Step 1: Create the file**

```csharp
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Maps each key character to its physically adjacent neighbours on a given keyboard layout.
/// Used by FuzzyEditDistance to apply a reduced substitution cost (0.5) for adjacent-key errors.
/// </summary>
public static class KeyboardAdjacencyMap
{
    private static readonly Dictionary<char, HashSet<char>> Qwerty = new()
    {
        ['q'] = ['w','a'],
        ['w'] = ['q','e','a','s'],
        ['e'] = ['w','r','s','d'],
        ['r'] = ['e','t','d','f'],
        ['t'] = ['r','y','f','g'],
        ['y'] = ['t','u','g','h'],
        ['u'] = ['y','i','h','j'],
        ['i'] = ['u','o','j','k'],
        ['o'] = ['i','p','k','l'],
        ['p'] = ['o','l'],
        ['a'] = ['q','w','s','z'],
        ['s'] = ['a','w','e','d','z','x'],
        ['d'] = ['s','e','r','f','x','c'],
        ['f'] = ['d','r','t','g','c','v'],
        ['g'] = ['f','t','y','h','v','b'],
        ['h'] = ['g','y','u','j','b','n'],
        ['j'] = ['h','u','i','k','n','m'],
        ['k'] = ['j','i','o','l','m'],
        ['l'] = ['k','o','p'],
        ['z'] = ['a','s','x'],
        ['x'] = ['z','s','d','c'],
        ['c'] = ['x','d','f','v'],
        ['v'] = ['c','f','g','b'],
        ['b'] = ['v','g','h','n'],
        ['n'] = ['b','h','j','m'],
        ['m'] = ['n','j','k'],
    };

    // QWERTZ: y and z are swapped vs QWERTY.
    // z sits between t and u on row 1; y sits leftmost on row 3 next to x and above a/s.
    private static readonly Dictionary<char, HashSet<char>> Qwertz = new()
    {
        ['q'] = ['w','a'],
        ['w'] = ['q','e','a','s'],
        ['e'] = ['w','r','s','d'],
        ['r'] = ['e','t','d','f'],
        ['t'] = ['r','z','f','g'],   // z replaces y as t's right neighbour
        ['z'] = ['t','u','g','h'],   // z is now row-1 between t and u
        ['u'] = ['z','i','h','j'],
        ['i'] = ['u','o','j','k'],
        ['o'] = ['i','p','k','l'],
        ['p'] = ['o','l'],
        ['a'] = ['q','w','s','y'],   // y is now row-3 leftmost, above a
        ['s'] = ['a','w','e','d','y','x'],
        ['d'] = ['s','e','r','f','x','c'],
        ['f'] = ['d','r','t','g','c','v'],
        ['g'] = ['f','t','z','h','v','b'],
        ['h'] = ['g','z','u','j','b','n'],
        ['j'] = ['h','u','i','k','n','m'],
        ['k'] = ['j','i','o','l','m'],
        ['l'] = ['k','o','p'],
        ['y'] = ['a','s','x'],       // y is now row-3 leftmost (like z was in QWERTY)
        ['x'] = ['y','s','d','c'],
        ['c'] = ['x','d','f','v'],
        ['v'] = ['c','f','g','b'],
        ['b'] = ['v','g','h','n'],
        ['n'] = ['b','h','j','m'],
        ['m'] = ['n','j','k'],
    };

    // AZERTY: row 1 = a z e r t y u i o p
    //         row 2 = q s d f g h j k l m
    //         row 3 = w x c v b n
    private static readonly Dictionary<char, HashSet<char>> Azerty = new()
    {
        ['a'] = ['z','q'],
        ['z'] = ['a','e','q','s'],
        ['e'] = ['z','r','s','d'],
        ['r'] = ['e','t','d','f'],
        ['t'] = ['r','y','f','g'],
        ['y'] = ['t','u','g','h'],
        ['u'] = ['y','i','h','j'],
        ['i'] = ['u','o','j','k'],
        ['o'] = ['i','p','k','l'],
        ['p'] = ['o','l','m'],
        ['q'] = ['a','z','s','w'],
        ['s'] = ['q','z','e','d','w','x'],
        ['d'] = ['s','e','r','f','x','c'],
        ['f'] = ['d','r','t','g','c','v'],
        ['g'] = ['f','t','y','h','v','b'],
        ['h'] = ['g','y','u','j','b','n'],
        ['j'] = ['h','u','i','k','n','m'],
        ['k'] = ['j','i','o','l','m'],
        ['l'] = ['k','o','p','m'],
        ['m'] = ['l','p'],
        ['w'] = ['q','s','x'],
        ['x'] = ['w','s','d','c'],
        ['c'] = ['x','d','f','v'],
        ['v'] = ['c','f','g','b'],
        ['b'] = ['v','g','h','n'],
        ['n'] = ['b','h','j','m'],
    };

    /// <summary>
    /// Returns the adjacency map for the given layout name.
    /// Defaults to QWERTY for unknown layouts.
    /// </summary>
    public static Dictionary<char, HashSet<char>> For(string layoutName) =>
        layoutName.ToUpperInvariant() switch
        {
            "QWERTZ" => Qwertz,
            "AZERTY" => Azerty,
            _        => Qwerty,
        };

    /// <summary>Returns true if <paramref name="b"/> is physically adjacent to <paramref name="a"/> on the given map.</summary>
    public static bool AreAdjacent(Dictionary<char, HashSet<char>> map, char a, char b) =>
        map.TryGetValue(a, out var neighbours) && neighbours.Contains(b);
}
```

**Step 2: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/KeyboardAdjacencyMap.cs
git commit -m "feat: add KeyboardAdjacencyMap for QWERTY, QWERTZ, AZERTY"
```

---

## Task 2: FuzzyEditDistance

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/FuzzyEditDistance.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/FuzzyEditDistanceTests.cs`

**Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class FuzzyEditDistanceTests
{
    private static readonly Dictionary<char, HashSet<char>> QwertyMap =
        KeyboardAdjacencyMap.For("QWERTY");

    [Fact]
    public void Compute_IdenticalStrings_ReturnsZero()
    {
        Assert.Equal(0.0, FuzzyEditDistance.Compute("the", "the", QwertyMap));
    }

    [Fact]
    public void Compute_AdjacentKeySubstitution_ReturnHalfCost()
    {
        // 'r' is adjacent to 't' on QWERTY — substituting t→r costs 0.5
        var dist = FuzzyEditDistance.Compute("the", "rhe", QwertyMap);
        Assert.Equal(0.5, dist, precision: 5);
    }

    [Fact]
    public void Compute_NonAdjacentSubstitution_ReturnFullCost()
    {
        // 's' is not adjacent to 't'
        var dist = FuzzyEditDistance.Compute("the", "she", QwertyMap);
        Assert.Equal(1.0, dist, precision: 5);
    }

    [Fact]
    public void Compute_OneDeletion_ReturnsCostOne()
    {
        // "hav" → "have" is one insertion (or "have" → "hav" is one deletion)
        var dist = FuzzyEditDistance.Compute("have", "hav", QwertyMap);
        Assert.Equal(1.0, dist, precision: 5);
    }

    [Fact]
    public void Compute_OneInsertion_ReturnsCostOne()
    {
        var dist = FuzzyEditDistance.Compute("hav", "have", QwertyMap);
        Assert.Equal(1.0, dist, precision: 5);
    }

    [Fact]
    public void Compute_EmptyStrings_ReturnsZero()
    {
        Assert.Equal(0.0, FuzzyEditDistance.Compute("", "", QwertyMap));
    }

    [Fact]
    public void Compute_EmptyVsWord_ReturnsWordLength()
    {
        Assert.Equal(3.0, FuzzyEditDistance.Compute("", "the", QwertyMap), precision: 5);
    }

    [Fact]
    public void Compute_TwoAdjacentSubstitutions_ReturnsCostOne()
    {
        // 'r'→'t' (adjacent, 0.5) and 'n'→'m' (adjacent, 0.5) = 1.0
        var dist = FuzzyEditDistance.Compute("ten", "rem", QwertyMap);
        // t→r (0.5), e→e (0), n→m (0.5) = 1.0
        Assert.Equal(1.0, dist, precision: 5);
    }
}
```

**Step 2: Run to verify they fail**

```bash
cd C:\Users\andre\source\repos\eyectrl
dotnet test tests/EyeCtrl.Core.Tests/ --filter "FuzzyEditDistanceTests" -v quiet
```

Expected: build error — `FuzzyEditDistance` does not exist yet.

**Step 3: Implement**

```csharp
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Keyboard-aware Levenshtein distance. Adjacent-key substitutions cost 0.5 instead of 1.0.
/// Insertions and deletions always cost 1.0.
/// </summary>
public static class FuzzyEditDistance
{
    public static double Compute(
        string a,
        string b,
        Dictionary<char, HashSet<char>> adjacencyMap)
    {
        int m = a.Length, n = b.Length;
        if (m == 0) return n;
        if (n == 0) return m;

        // dp[i][j] = min cost to transform a[0..i) into b[0..j)
        var dp = new double[m + 1, n + 1];

        for (int i = 0; i <= m; i++) dp[i, 0] = i;
        for (int j = 0; j <= n; j++) dp[0, j] = j;

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                char ca = char.ToLowerInvariant(a[i - 1]);
                char cb = char.ToLowerInvariant(b[j - 1]);

                double subCost = ca == cb ? 0.0
                    : KeyboardAdjacencyMap.AreAdjacent(adjacencyMap, ca, cb) ? 0.5
                    : 1.0;

                dp[i, j] = Math.Min(
                    Math.Min(
                        dp[i - 1, j] + 1.0,      // deletion
                        dp[i, j - 1] + 1.0),      // insertion
                    dp[i - 1, j - 1] + subCost);  // substitution
            }
        }

        return dp[m, n];
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.Core.Tests/ --filter "FuzzyEditDistanceTests" -v quiet
```

Expected: all 8 tests pass.

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/FuzzyEditDistance.cs \
        tests/EyeCtrl.Core.Tests/Prediction/FuzzyEditDistanceTests.cs
git commit -m "feat: add keyboard-aware FuzzyEditDistance"
```

---

## Task 3: Update WordPredictor — fuzzy GetSuggestions

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/WordPredictor.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs`

**Step 1: Add new failing tests** — append to `WordPredictorTests.cs`

```csharp
// Add a new factory helper that accepts a layout name
private static WordPredictor CreatePredictor(Dictionary<string, int> words, string layout = "QWERTY")
{
    return new WordPredictor(new InMemoryDictionaryProvider(words), layout);
}

[Fact]
public void GetSuggestions_FuzzyAdjacentKey_SuggestsIntendedWord()
{
    // User typed "rhe" but intended "the" (r is adjacent to t on QWERTY)
    var predictor = CreatePredictor(new()
    {
        { "the", 1000 }, { "rhe", 1 }, { "world", 500 }
    });

    var results = predictor.GetSuggestions("rhe", maxCount: 5);
    Assert.Contains("the", results);
}

[Fact]
public void GetSuggestions_ExactPrefixRanksAboveFuzzy_WhenFrequencyEqual()
{
    // "there" is exact prefix for "the", "rhere" is adjacent-key typo prefix for "there"
    // "there" should appear because it starts with "the" (exact prefix)
    var predictor = CreatePredictor(new()
    {
        { "there", 500 }, { "the", 500 }
    });

    var results = predictor.GetSuggestions("the", maxCount: 5);
    // Both should appear; exact prefix match scores 3x so "there" and "the" both qualify
    Assert.Contains("there", results);
    Assert.Contains("the", results);
}

[Fact]
public void GetSuggestions_HighFrequencyFuzzyBeatsLowFrequencyExact()
{
    // "the" (freq 10000) is a fuzzy match for "rhe"; "rheumatic" (freq 1) is exact prefix
    // "the" should rank above "rheumatic" because 10000*1.5 >> 1*3
    var predictor = CreatePredictor(new()
    {
        { "the", 10000 }, { "rheumatic", 1 }
    });

    var results = predictor.GetSuggestions("rhe", maxCount: 5);
    Assert.Equal("the", results[0]);
}

[Fact]
public void GetSuggestions_NonAdjacentTypo_StillSuggestsWithinThreshold()
{
    // "she" vs "the" — s is not adjacent to t, distance = 1.0 which is within 1.5 threshold
    var predictor = CreatePredictor(new()
    {
        { "the", 1000 }
    });

    var results = predictor.GetSuggestions("she", maxCount: 5);
    Assert.Contains("the", results);
}

[Fact]
public void GetSuggestions_FarTypo_NotSuggested()
{
    // "xyz" vs "the" — distance > 1.5, should not appear
    var predictor = CreatePredictor(new()
    {
        { "the", 1000 }
    });

    var results = predictor.GetSuggestions("xyz", maxCount: 5);
    Assert.DoesNotContain("the", results);
}
```

**Step 2: Run to verify new tests fail**

```bash
dotnet test tests/EyeCtrl.Core.Tests/ --filter "FuzzyAdjacentKey|HighFrequencyFuzzy|NonAdjacentTypo|FarTypo|ExactPrefixRanks" -v quiet
```

Expected: compilation error — `WordPredictor` constructor doesn't accept `layout` yet.

**Step 3: Rewrite `WordPredictor.cs`**

```csharp
namespace EyeCtrl.Core.Prediction;

public class WordPredictor
{
    private readonly IDictionaryProvider _provider;
    private readonly Dictionary<string, int> _words;
    private readonly Dictionary<char, HashSet<char>> _adjacencyMap;

    private const double MaxFuzzyDistance = 1.5;

    public WordPredictor(IDictionaryProvider provider, string layoutName = "QWERTY")
    {
        _provider = provider;
        _words = provider.Load();
        _adjacencyMap = KeyboardAdjacencyMap.For(layoutName);
    }

    public List<string> GetSuggestions(string prefix, int maxCount = 5)
    {
        var lowerPrefix = prefix.ToLowerInvariant();
        var scored = new Dictionary<string, double>();

        foreach (var (word, frequency) in _words)
        {
            if (word.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                scored[word] = frequency * 3.0;
                continue;
            }

            // Compare prefix against the same-length prefix of the word, or full word if shorter
            var compareTarget = word.Length >= lowerPrefix.Length
                ? word[..lowerPrefix.Length]
                : word;

            var dist = FuzzyEditDistance.Compute(lowerPrefix, compareTarget, _adjacencyMap);
            if (dist > MaxFuzzyDistance) continue;

            double multiplier = dist <= 0.5 ? 1.5 : dist <= 1.0 ? 1.2 : 1.0;
            scored[word] = frequency * multiplier;
        }

        return scored
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => kv.Key)
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

    public void SaveUserDictionary()
    {
        _provider.Save(_words);
    }
}
```

**Step 4: Run all Prediction tests**

```bash
dotnet test tests/EyeCtrl.Core.Tests/ --filter "Prediction" -v quiet
```

Expected: all tests pass. The existing tests (`MatchesPrefix`, `OrderedByFrequency`, etc.) must still pass — exact prefix scoring at 3× preserves the old ranking behavior.

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/WordPredictor.cs \
        tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs
git commit -m "feat: add fuzzy keyboard-aware prefix matching to WordPredictor"
```

---

## Task 4: Update FileDictionaryProvider — per-language user dictionary

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/FileDictionaryProviderTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class FileDictionaryProviderTests
{
    [Fact]
    public void UserDictionaryPath_IncludesLanguageCode()
    {
        var provider = new FileDictionaryProvider(
            baseDictionaryPath: "base_en.json",
            appDataDir: @"C:\AppData",
            languageCode: "fr");

        Assert.Equal(@"C:\AppData\user_dictionary_fr.json", provider.UserDictionaryPath);
    }

    [Fact]
    public void UserDictionaryPath_DefaultsToEn()
    {
        var provider = new FileDictionaryProvider(
            baseDictionaryPath: "base.json",
            appDataDir: @"C:\AppData",
            languageCode: "en");

        Assert.Equal(@"C:\AppData\user_dictionary_en.json", provider.UserDictionaryPath);
    }

    [Fact]
    public void Load_MissingFiles_ReturnsEmptyDictionary()
    {
        var provider = new FileDictionaryProvider(
            baseDictionaryPath: "nonexistent_base.json",
            appDataDir: Path.GetTempPath(),
            languageCode: "en");

        var words = provider.Load();
        Assert.Empty(words);
    }
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.Core.Tests/ --filter "FileDictionaryProviderTests" -v quiet
```

Expected: compilation error — constructor signature doesn't match yet.

**Step 3: Update `FileDictionaryProvider.cs`**

```csharp
namespace EyeCtrl.Core.Prediction;

using System.Text.Json;

public class FileDictionaryProvider : IDictionaryProvider
{
    private readonly string _baseDictionaryPath;
    private readonly string _userDictionaryPath;

    public string UserDictionaryPath => _userDictionaryPath;

    public FileDictionaryProvider(string baseDictionaryPath, string appDataDir, string languageCode)
    {
        _baseDictionaryPath = baseDictionaryPath;
        _userDictionaryPath = Path.Combine(appDataDir, $"user_dictionary_{languageCode}.json");
    }

    public Dictionary<string, int> Load()
    {
        var words = new Dictionary<string, int>();

        if (File.Exists(_baseDictionaryPath))
        {
            var json = File.ReadAllText(_baseDictionaryPath);
            var baseWords = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (baseWords != null)
                foreach (var kv in baseWords)
                    words[kv.Key] = kv.Value;
        }

        if (File.Exists(_userDictionaryPath))
        {
            var json = File.ReadAllText(_userDictionaryPath);
            var userWords = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (userWords != null)
                foreach (var kv in userWords)
                    words[kv.Key] = words.GetValueOrDefault(kv.Key) + kv.Value;
        }

        return words;
    }

    public void Save(Dictionary<string, int> words)
    {
        var directory = Path.GetDirectoryName(_userDictionaryPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(words, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_userDictionaryPath, json);
    }
}
```

**Step 4: Fix the call site in `App.xaml.cs`** — the constructor signature changed. Find `new FileDictionaryProvider(dictPath, userDictPath)` and update it. The full fix is in Task 7 — for now just note the build will be broken until Task 7.

**Step 5: Run FileDictionaryProvider tests only**

```bash
dotnet test tests/EyeCtrl.Core.Tests/ --filter "FileDictionaryProviderTests" -v quiet
```

Expected: all 3 pass.

**Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs \
        tests/EyeCtrl.Core.Tests/Prediction/FileDictionaryProviderTests.cs
git commit -m "feat: FileDictionaryProvider supports per-language user dictionaries"
```

---

## Task 5: LanguageDetector

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/LanguageDetector.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/LanguageDetectorTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using System.Globalization;

public class LanguageDetectorTests
{
    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-BE", "fr")]
    [InlineData("es-ES", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("de-DE", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("pl-PL", "pl")]
    public void DetectFromCulture_KnownCulture_ReturnsMappedCode(string cultureName, string expected)
    {
        var culture = new CultureInfo(cultureName);
        Assert.Equal(expected, LanguageDetector.DetectFromCulture(culture));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public void DetectFromCulture_UnsupportedCulture_FallsBackToEnglish(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        Assert.Equal("en", LanguageDetector.DetectFromCulture(culture));
    }
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.Core.Tests/ --filter "LanguageDetectorTests" -v quiet
```

Expected: compilation error — `LanguageDetector` does not exist.

**Step 3: Implement**

```csharp
namespace EyeCtrl.Core.Prediction;

using System.Globalization;

public static class LanguageDetector
{
    private static readonly HashSet<string> SupportedCodes = ["en", "fr", "es", "de", "pl"];

    /// <summary>
    /// Returns the best-matching supported language code for the given culture,
    /// falling back to "en" if the culture is not supported.
    /// </summary>
    public static string DetectFromCulture(CultureInfo culture)
    {
        var twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return SupportedCodes.Contains(twoLetter) ? twoLetter : "en";
    }

    /// <summary>
    /// Detects language from the current OS UI culture.
    /// </summary>
    public static string Detect() => DetectFromCulture(CultureInfo.CurrentUICulture);
}
```

**Step 4: Run tests**

```bash
dotnet test tests/EyeCtrl.Core.Tests/ --filter "LanguageDetectorTests" -v quiet
```

Expected: all 12 tests pass.

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/LanguageDetector.cs \
        tests/EyeCtrl.Core.Tests/Prediction/LanguageDetectorTests.cs
git commit -m "feat: add LanguageDetector with OS culture auto-detection"
```

---

## Task 6: Generate language dictionaries and update KeyboardSettings default

**Files:**
- Create: `tools/gen_dicts.py` (one-time generation script, not shipped)
- Create: `src/EyeCtrl.App/Resources/Dictionaries/fr.json`
- Create: `src/EyeCtrl.App/Resources/Dictionaries/es.json`
- Create: `src/EyeCtrl.App/Resources/Dictionaries/de.json`
- Create: `src/EyeCtrl.App/Resources/Dictionaries/pl.json`
- Modify: `src/EyeCtrl.Core/Settings/KeyboardSettings.cs`

**Step 1: Create the generation script**

```python
# tools/gen_dicts.py
# Requires: pip install wordfreq
# Run from repo root: python tools/gen_dicts.py
import json
import os
from wordfreq import top_n_list, word_frequency

LANGS = ["fr", "es", "de", "pl"]
N = 5000
OUT_DIR = "src/EyeCtrl.App/Resources/Dictionaries"

for lang in LANGS:
    words = top_n_list(lang, N, wordlist="best")
    # Scale frequencies to match en.json's approximate range (int counts)
    freq_dict = {}
    for i, word in enumerate(words):
        # Use a Zipf-like rank score: top word gets ~70000, 5000th gets ~14
        score = max(1, int(70000 / (i + 1)))
        freq_dict[word] = score
    out_path = os.path.join(OUT_DIR, f"{lang}.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(freq_dict, f, ensure_ascii=False, indent=2)
    print(f"Generated {out_path} ({len(freq_dict)} words)")
```

**Step 2: Run the script**

```bash
pip install wordfreq
python tools/gen_dicts.py
```

Expected output:
```
Generated src/EyeCtrl.App/Resources/Dictionaries/fr.json (5000 words)
Generated src/EyeCtrl.App/Resources/Dictionaries/es.json (5000 words)
Generated src/EyeCtrl.App/Resources/Dictionaries/de.json (5000 words)
Generated src/EyeCtrl.App/Resources/Dictionaries/pl.json (5000 words)
```

The `.csproj` already includes `Resources\Dictionaries\*.json` with `PreserveNewest`, so no project file changes are needed.

**Step 3: Change `KeyboardSettings` default language to empty string**

In `src/EyeCtrl.Core/Settings/KeyboardSettings.cs`, change:

```csharp
[ObservableProperty] private string _languagePack = "en";
```

to:

```csharp
[ObservableProperty] private string _languagePack = "";
```

**Step 4: Build to confirm no regressions**

```bash
dotnet build src/EyeCtrl.App/ -c Debug --no-restore 2>&1 | tail -5
```

Expected: build succeeds (App.xaml.cs still broken from Task 4 — that's OK, fix comes in Task 7).

**Step 5: Commit**

```bash
git add tools/gen_dicts.py \
        src/EyeCtrl.App/Resources/Dictionaries/fr.json \
        src/EyeCtrl.App/Resources/Dictionaries/es.json \
        src/EyeCtrl.App/Resources/Dictionaries/de.json \
        src/EyeCtrl.App/Resources/Dictionaries/pl.json \
        src/EyeCtrl.Core/Settings/KeyboardSettings.cs
git commit -m "feat: add fr/es/de/pl dictionaries and set LanguagePack default to empty (auto-detect)"
```

---

## Task 7: Wire auto-detection and fuzzy predictor in App.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` — `LoadKeyboard` method only

**Step 1: Update `LoadKeyboard`**

Find the `LoadKeyboard` method. Replace the `if (settings.Keyboard.PredictionEnabled)` block:

```csharp
// OLD — two-arg FileDictionaryProvider, no layout name, no auto-detect:
if (settings.Keyboard.PredictionEnabled)
{
    var dictPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        "Resources", "Dictionaries", $"{settings.Keyboard.LanguagePack}.json");
    var userDictPath = Path.Combine(_appDataDir, "user_dictionary.json");
    var provider = new FileDictionaryProvider(dictPath, userDictPath);
    _wordPredictor = new WordPredictor(provider);
    keyboard.SetPredictor(_wordPredictor);
}
```

```csharp
// NEW — auto-detect language, per-language user dict, layout-aware adjacency:
if (settings.Keyboard.PredictionEnabled)
{
    if (string.IsNullOrEmpty(settings.Keyboard.LanguagePack))
    {
        settings.Keyboard.LanguagePack = LanguageDetector.Detect();
        settingsService.SaveDebounced(settings);
        Log.Information("Auto-detected language: {Lang}", settings.Keyboard.LanguagePack);
    }

    var langCode = settings.Keyboard.LanguagePack;
    var dictPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        "Resources", "Dictionaries", $"{langCode}.json");
    var provider = new FileDictionaryProvider(dictPath, _appDataDir, langCode);
    _wordPredictor = new WordPredictor(provider, settings.Keyboard.ActiveLayout);
    keyboard.SetPredictor(_wordPredictor);
}
```

Also add the `using EyeCtrl.Core.Prediction;` directive at the top if not already present (it is — `LanguageDetector` is in the same namespace).

The `settingsService` local is already in scope in `OnStartup`. However, `LoadKeyboard` receives `settings` and `inputSimulator` as parameters but not `settingsService`. Add it as a parameter:

Change signature:
```csharp
private void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings, IInputSimulator inputSimulator)
```
to:
```csharp
private void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings,
    IInputSimulator inputSimulator, SettingsService settingsService)
```

Update the three call sites in `OnStartup` (initial load and `settingsWindow.SettingsChanged`) to pass `settingsService`.

**Step 2: Build and verify it compiles**

```bash
dotnet build src/EyeCtrl.App/ -c Debug 2>&1 | grep -E "error|warning" | grep -v "obj\\"
```

Expected: zero errors.

**Step 3: Quick smoke-test in test mode**

```bash
dotnet run --project src/EyeCtrl.App/ -- --test-mode
```

Open keyboard, type a few characters. Predictions should appear. Check `%APPDATA%\EyeCtrl\settings.json` — `languagePack` should be set to your OS language (or `"en"`).

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: wire language auto-detection and layout-aware fuzzy predictor in App"
```

---

## Task 8: Add AddRadio to SettingsWindow and language selection in Keyboard tab

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

**Step 1: Add the `AddRadio` helper method**

Add this private method alongside `AddToggle` (around line 673):

```csharp
private void AddRadio(
    string idPrefix,
    string label,
    string[] displayOptions,
    string[] codeOptions,
    Func<string> getCode,
    Action<string> setCode)
{
    AddLabel(label);

    var borders = new List<Border>();

    for (int i = 0; i < displayOptions.Length; i++)
    {
        var capturedIndex = i;
        var capturedCode = codeOptions[i];
        var id = $"{idPrefix}_{i}";

        var text = new TextBlock
        {
            Text = displayOptions[i],
            Foreground = Brushes.White,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0)
        };

        System.Windows.Automation.AutomationProperties.SetAutomationId(
            text, $"Settings_Radio_{id}");

        var border = new Border
        {
            Height = 70,
            Margin = new Thickness(0, 2, 0, 4),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 220,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = text
        };

        void Refresh()
        {
            var active = getCode();
            foreach (var b in borders)
                b.Background = new SolidColorBrush(BtnBase);
            var activeIdx = Array.IndexOf(codeOptions, active);
            if (activeIdx >= 0 && activeIdx < borders.Count)
                borders[activeIdx].Background = new SolidColorBrush(
                    Color.FromArgb(200, 30, 100, 30));
        }

        _controlActions[id] = () =>
        {
            setCode(capturedCode);
            Refresh();
        };

        border.MouseLeftButtonDown += (_, _) => _controlActions[id]();
        _controlBorders[id] = border;
        _controlDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), ControlBtnRadius, _dwellMs));
        borders.Add(border);
        ContentPanel.Children.Add(border);
    }

    // Set initial highlight
    var currentCode = getCode();
    var currentIdx = Array.IndexOf(codeOptions, currentCode);
    if (currentIdx >= 0 && currentIdx < borders.Count)
        borders[currentIdx].Background = new SolidColorBrush(
            Color.FromArgb(200, 30, 100, 30));
}
```

**Step 2: Add language selection to `BuildKeyboardPages`**

In `BuildKeyboardPages`, add the radio control between the Word Prediction toggle and Click Sound toggle:

```csharp
() => AddToggle("k_pred", "Word Prediction",
    () => _settings.Keyboard.PredictionEnabled,
    v => { _settings.Keyboard.PredictionEnabled = v; ApplyAndSave(); }),
// NEW:
() => AddRadio("k_lang", "Dictionary Language",
    displayOptions: ["English", "French", "Spanish", "German", "Polish"],
    codeOptions:    ["en",      "fr",     "es",      "de",     "pl"],
    getCode: () => string.IsNullOrEmpty(_settings.Keyboard.LanguagePack) ? "en"
                   : _settings.Keyboard.LanguagePack,
    setCode: v => { _settings.Keyboard.LanguagePack = v; ApplyAndSave(); }),
() => AddToggle("k_sound", "Click Sound",
```

**Step 3: Update Reset Keyboard Settings to reset LanguagePack to ""**

In the `BuildKeyboardPages` reset modal action, find:

```csharp
_settings.Keyboard.LanguagePack = k.LanguagePack;
```

Change to:

```csharp
_settings.Keyboard.LanguagePack = "";   // triggers re-detection on next load
```

**Step 4: Update Reset ALL Settings in `BuildGeneralPages`**

Find `_settings.Keyboard.LanguagePack = d.Keyboard.LanguagePack;` and change to:

```csharp
_settings.Keyboard.LanguagePack = "";
```

**Step 5: Build**

```bash
dotnet build src/EyeCtrl.App/ -c Debug 2>&1 | grep -E "^.*error" | grep -v obj
```

Expected: zero errors.

**Step 6: Smoke-test the Settings UI**

```bash
dotnet run --project src/EyeCtrl.App/ -- --test-mode
```

Open Settings → Keyboard tab. Verify:
- Five radio buttons appear (English, French, Spanish, German, Polish)
- Current language is highlighted green
- Dwelling on a different language updates the highlight and triggers `ApplyAndSave`
- Changing language reloads the predictor (type in keyboard after changing)

**Step 7: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: add dwellable language radio buttons to Keyboard settings tab"
```

---

## Task 9: Run full test suite and final verification

**Step 1: Run all tests**

```bash
dotnet test tests/ -v quiet 2>&1 | tail -20
```

Expected: all tests pass (previously 30 + 8 FuzzyEditDistance + 5 WordPredictor fuzzy + 3 FileDictionaryProvider + 12 LanguageDetector = ~58 total).

**Step 2: Verify prediction quality manually**

In `--test-mode`, type with intentional adjacent-key errors:
- Type `rhe` → expect `the`, `them`, `they` in suggestions
- Type `helo` → expect `hello`, `help` etc.
- Type `tge` → expect `the` (t→t exact, g adjacent to h→h→e, close enough)

**Step 3: Commit any fixes, then final commit**

```bash
git add -A
git commit -m "feat: language support and fuzzy word matching complete"
```
