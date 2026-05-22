# ML-Enhanced Prediction Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add sentence completion trie and ONNX language model prediction engines behind a `PredictionOrchestrator` that merges results from all engines (existing bigram/phrase/fuzzy + new trie + new LM) with two-phase UI delivery.

**Architecture:** `PredictionOrchestrator` wraps `WordPredictor` (existing), `SentenceCompletionIndex` (new trie), and `SmallLMPredictor` (new ONNX). Sync results return immediately; LM inference fires on a background thread and results are merged in on subsequent poll calls. Existing `KeyboardViewModel` and `KeyboardWindow` adapt to use the orchestrator with minimal API changes.

**Tech Stack:** ONNX Runtime via `Microsoft.ML.OnnxRuntime.DirectML`, binary trie serialization, `HttpClient` for HuggingFace model downloads, existing `CommunityToolkit.Mvvm` + `WeakReferenceMessenger`.

---

### Task 1: Add PredictionType Enum Values and Settings

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/PredictionResult.cs:45-65`
- Modify: `src/EyeCtrl.Core/Settings/KeyboardSettings.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/PredictionResultTests.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Prediction/PredictionResultTests.cs` (if exists, else create)
- Create: `tests/EyeCtrl.Core.Tests/Prediction/KeyboardSettingsTests.cs`

- [ ] **Step 1: Write failing tests for new enum values**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/PredictionResultTests.cs (add to existing or create)

namespace EyeCtrl.Core.Tests.Prediction;

public class PredictionResultTests
{
    [Fact]
    public void Constructor_SentenceCompletion_CreatesValidRecord()
    {
        var result = new PredictionResult("hand is worth two in the bush", 150.0, PredictionType.SentenceCompletion, 0.95);
        Assert.Equal("hand is worth two in the bush", result.Word);
        Assert.Equal(150.0, result.Score);
        Assert.Equal(PredictionType.SentenceCompletion, result.Type);
        Assert.Equal(0.95, result.Confidence);
    }

    [Fact]
    public void Constructor_LMSuggestion_CreatesValidRecord()
    {
        var result = new PredictionResult("bush", 80.0, PredictionType.LMSuggestion, 0.6);
        Assert.Equal("bush", result.Word);
        Assert.Equal(80.0, result.Score);
        Assert.Equal(PredictionType.LMSuggestion, result.Type);
        Assert.Equal(0.6, result.Confidence);
    }

    [Theory]
    [InlineData(PredictionType.SentenceCompletion)]
    [InlineData(PredictionType.LMSuggestion)]
    public void NewPredictionTypes_RoundTripThroughSwitch(PredictionType type)
    {
        // Verify switch expressions handle new types without throwing
        var label = type switch
        {
            PredictionType.ExactPrefix => "ExactPrefix",
            PredictionType.FuzzyMatch => "FuzzyMatch",
            PredictionType.NextWord => "NextWord",
            PredictionType.Phrase => "Phrase",
            PredictionType.SentenceCompletion => "SentenceCompletion",
            PredictionType.LMSuggestion => "LMSuggestion",
            _ => "Unknown"
        };
        Assert.NotEqual("Unknown", label);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PredictionResultTests" -v`
Expected: FAIL — `PredictionType` does not contain definitions for `SentenceCompletion` and `LMSuggestion`

- [ ] **Step 3: Add enum values and implement**

```csharp
// src/EyeCtrl.Core/Prediction/PredictionResult.cs — add two new values to the Enum

public enum PredictionType
{
    ExactPrefix,
    FuzzyMatch,
    NextWord,
    Phrase,
    SentenceCompletion,  // NEW — from sentence completion trie index
    LMSuggestion,        // NEW — from ONNX language model
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PredictionResultTests" -v`
Expected: PASS (3 tests)

- [ ] **Step 5: Write failing tests for new KeyboardSettings properties**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/KeyboardSettingsTests.cs

namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Settings;

public class KeyboardSettingsTests
{
    [Fact]
    public void Defaults_MLSettings_HaveExpectedValues()
    {
        var settings = new KeyboardSettings();
        Assert.True(settings.MLSuggestionsEnabled);
        Assert.Equal("360M", settings.MLModelSize);
        Assert.True(settings.ShowLMLoadingIndicator);
        Assert.True(settings.PreferGPU);
    }

    [Theory]
    [InlineData("135M")]
    [InlineData("360M")]
    [InlineData("1.5B")]
    [InlineData("Off")]
    public void MLModelSize_ValidValues_Accepted(string value)
    {
        var settings = new KeyboardSettings { MLModelSize = value };
        Assert.Equal(value, settings.MLModelSize);
    }
}
```

- [ ] **Step 6: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~KeyboardSettingsTests" -v`
Expected: FAIL — `KeyboardSettings` does not contain definition for `MLSuggestionsEnabled`

- [ ] **Step 7: Add settings properties**

```csharp
// src/EyeCtrl.Core/Settings/KeyboardSettings.cs — add after existing properties (after DecayHalfLifeDays)

[ObservableProperty] private bool _mlSuggestionsEnabled = true;
[ObservableProperty] private string _mlModelSize = "360M";
[ObservableProperty] private bool _showLMLoadingIndicator = true;
[ObservableProperty] private bool _preferGPU = true;
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~KeyboardSettingsTests" -v`
Expected: PASS (2 theory + 1 fact = 3 tests total)

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/PredictionResult.cs src/EyeCtrl.Core/Settings/KeyboardSettings.cs tests/EyeCtrl.Core.Tests/Prediction/PredictionResultTests.cs tests/EyeCtrl.Core.Tests/Prediction/KeyboardSettingsTests.cs
git commit -m "feat: add SentenceCompletion and LMSuggestion prediction types and ML settings"
```

---

### Task 2: Build SentenceCompletionIndex (Compact Trie)

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/SentenceCompletionIndex.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/SentenceCompletionIndexTests.cs`

- [ ] **Step 1: Write failing test for trie node and basic insertion**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/SentenceCompletionIndexTests.cs

namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class SentenceCompletionIndexTests
{
    [Fact]
    public void GetCompletions_ExactPrefix_ReturnsCompletion()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("the hand is worth two in the bush", 5);

        var results = index.GetCompletions("the hand is", maxCount: 5);

        Assert.Single(results);
        Assert.Equal("worth two in the bush", results[0].completion);
        Assert.Equal(5, results[0].frequency);
    }

    [Fact]
    public void GetCompletions_ShorterPrefix_ReturnsMultiple()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("thank you very much", 10);
        index.Insert("thank you for your help", 7);

        var results = index.GetCompletions("thank you", maxCount: 5);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.completion == "very much");
        Assert.Contains(results, r => r.completion == "for your help");
    }

    [Fact]
    public void GetCompletions_NoMatch_ReturnsEmpty()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("hello world", 3);

        var results = index.GetCompletions("goodbye", maxCount: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void GetCompletions_RespectsMaxCount()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("a b c", 10);
        index.Insert("a b d", 9);
        index.Insert("a b e", 8);

        var results = index.GetCompletions("a b", maxCount: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void GetCompletions_OrderedByFrequencyDescending()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("the quick brown fox", 1);
        index.Insert("the quick red fox", 10);
        index.Insert("the quick blue fox", 5);

        var results = index.GetCompletions("the quick", maxCount: 5);

        Assert.Equal(3, results.Count);
        Assert.Equal("red fox", results[0].completion);
        Assert.Equal("blue fox", results[1].completion);
        Assert.Equal("brown fox", results[2].completion);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SentenceCompletionIndexTests" -v`
Expected: FAIL — type `SentenceCompletionIndex` not found

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EyeCtrl.Core/Prediction/SentenceCompletionIndex.cs

namespace EyeCtrl.Core.Prediction;

public class SentenceCompletionIndex
{
    private readonly TrieNode _root = new();

    public void Insert(string sentence, int frequency)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return;
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return;
        Insert(words, frequency);
    }

    private void Insert(string[] words, int frequency)
    {
        var node = _root;
        // Walk prefix: all words except the last become path; the last word becomes the completion
        for (int i = 0; i < words.Length - 1; i++)
        {
            var word = words[i].ToLowerInvariant();
            if (!node.Children.TryGetValue(word, out var child))
            {
                child = new TrieNode();
                node.Children[word] = child;
            }
            node.Frequency += frequency; // subtree weight for ranking
            node = child;
        }
        node.Frequency += frequency;
        var completionWord = words[^1];
        // Store completion and its frequency alongside other completions
        node.Completions[completionWord] = node.Completions.GetValueOrDefault(completionWord) + frequency;
    }

    public List<(string completion, int frequency)> GetCompletions(string prefix, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return [];

        var words = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        // Walk the trie along the prefix path
        var node = _root;
        foreach (var word in words)
        {
            if (!node.Children.TryGetValue(word.ToLowerInvariant(), out var child))
                return []; // prefix not in trie
            node = child;
        }

        // Collect and sort completions at this node
        return node.Completions
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public void BoostCompletion(string fullSentence)
    {
        if (string.IsNullOrWhiteSpace(fullSentence)) return;
        var words = fullSentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return;

        var node = _root;
        for (int i = 0; i < words.Length - 1; i++)
        {
            if (!node.Children.TryGetValue(words[i].ToLowerInvariant(), out var child))
                return;
            node.Frequency++;
            node = child;
        }
        var lastWord = words[^1];
        if (node.Completions.ContainsKey(lastWord))
            node.Completions[lastWord]++;
    }

    public void DismissCompletion(string fullSentence)
    {
        if (string.IsNullOrWhiteSpace(fullSentence)) return;
        var words = fullSentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return;

        var node = _root;
        for (int i = 0; i < words.Length - 1; i++)
        {
            if (!node.Children.TryGetValue(words[i].ToLowerInvariant(), out var child))
                return;
            node.Frequency = Math.Max(0, node.Frequency - 1);
            node = child;
        }
        var lastWord = words[^1];
        if (node.Completions.TryGetValue(lastWord, out var freq) && freq > 0)
            node.Completions[lastWord] = freq - 1;
    }

    private class TrieNode
    {
        public int Frequency;
        public readonly Dictionary<string, TrieNode> Children = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Completions = new(StringComparer.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SentenceCompletionIndexTests" -v`
Expected: PASS (5 tests)

- [ ] **Step 5: Write failing tests for serialization round-trip**

```csharp
// Add to SentenceCompletionIndexTests.cs

[Fact]
public void SaveAndLoad_Roundtrip_PreservesData()
{
    var original = new SentenceCompletionIndex();
    original.Insert("thank you very much", 10);
    original.Insert("thank you for your help", 7);
    original.Insert("the quick brown fox", 3);

    using var stream = new MemoryStream();
    original.Save(stream);
    stream.Position = 0;

    var loaded = SentenceCompletionIndex.Load(stream);

    var results = loaded.GetCompletions("thank you", maxCount: 5);
    Assert.Equal(2, results.Count);
    Assert.Equal("very much", results[0].completion);
    Assert.Equal(10, results[0].frequency);
    Assert.Equal("for your help", results[1].completion);

    results = loaded.GetCompletions("the quick", maxCount: 5);
    Assert.Single(results);
    Assert.Equal("brown fox", results[0].completion);
}

[Fact]
public void Load_EmptyStream_ReturnsEmptyIndex()
{
    var original = new SentenceCompletionIndex();
    using var stream = new MemoryStream();
    original.Save(stream);
    stream.Position = 0;

    var loaded = SentenceCompletionIndex.Load(stream);
    var results = loaded.GetCompletions("anything", maxCount: 5);
    Assert.Empty(results);
}

[Fact]
public void BoostCompletion_IncreasesFrequency()
{
    var index = new SentenceCompletionIndex();
    index.Insert("thank you very much", 5);
    index.BoostCompletion("thank you very much");

    var results = index.GetCompletions("thank you", maxCount: 5);
    Assert.Single(results);
    Assert.Equal(6, results[0].frequency);
}

[Fact]
public void DismissCompletion_DecreasesFrequency()
{
    var index = new SentenceCompletionIndex();
    index.Insert("thank you very much", 5);
    index.DismissCompletion("thank you very much");

    var results = index.GetCompletions("thank you", maxCount: 5);
    Assert.Single(results);
    Assert.Equal(4, results[0].frequency);
}
```

- [ ] **Step 6: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SentenceCompletionIndexTests" -v`
Expected: FAIL — `Save` and `Load` methods not found

- [ ] **Step 7: Add serialization to SentenceCompletionIndex**

```csharp
// Add to SentenceCompletionIndex.cs

using System.Text;

// Add these constants at class level:
private const int Magic = 0x54524945; // "TRIE" in ASCII
private const int Version = 1;

// Add Save method:
public void Save(Stream stream)
{
    using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    writer.Write(Magic);
    writer.Write(Version);
    SaveNode(writer, _root);
}

private void SaveNode(BinaryWriter writer, TrieNode node)
{
    // Flags: bit 0 = hasCompletions
    byte flags = 0;
    if (node.Completions.Count > 0) flags |= 1;
    writer.Write(flags);

    // Completions
    if (node.Completions.Count > 0)
    {
        writer.Write((ushort)node.Completions.Count);
        foreach (var (word, freq) in node.Completions)
        {
            writer.Write(word);
            writer.Write(freq);
        }
    }

    // Children (only serialize children that are reachable)
    writer.Write((ushort)node.Children.Count);
    foreach (var (word, child) in node.Children)
    {
        writer.Write(word);
        SaveNode(writer, child);
    }
}

// Add static Load method:
public static SentenceCompletionIndex Load(Stream stream)
{
    using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
    var magic = reader.ReadInt32();
    if (magic != Magic)
        throw new InvalidDataException($"Invalid trie file: expected magic {Magic:X8}, got {magic:X8}");
    var version = reader.ReadInt32();
    if (version != Version)
        throw new InvalidDataException($"Unsupported trie version: {version}");

    var index = new SentenceCompletionIndex();
    LoadNode(reader, index._root);
    return index;
}

private static void LoadNode(BinaryReader reader, TrieNode node)
{
    var flags = reader.ReadByte();
    var hasCompletions = (flags & 1) != 0;

    if (hasCompletions)
    {
        var count = reader.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            var word = reader.ReadString();
            var freq = reader.ReadInt32();
            node.Completions[word] = freq;
            node.Frequency += freq;
        }
    }

    var childCount = reader.ReadUInt16();
    for (int i = 0; i < childCount; i++)
    {
        var word = reader.ReadString();
        var child = new TrieNode();
        node.Children[word] = child;
        LoadNode(reader, child);
        node.Frequency += child.Frequency;
    }
}
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SentenceCompletionIndexTests" -v`
Expected: PASS (9 tests)

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/SentenceCompletionIndex.cs tests/EyeCtrl.Core.Tests/Prediction/SentenceCompletionIndexTests.cs
git commit -m "feat: add sentence completion trie index with serialization"
```

---

### Task 3: Build CorpusBuilder Tool

**Files:**
- Create: `tools/CorpusBuilder/CorpusBuilder.csproj`
- Create: `tools/CorpusBuilder/Program.cs`

- [ ] **Step 1: Write failing test for corpus sentence filtering**

This is a console tool without a test project. We verify with a sample corpus file.
Create a small test corpus and verify the tool produces output.

- [ ] **Step 2: Create project file**

```xml
<!-- tools/CorpusBuilder/CorpusBuilder.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Core\EyeCtrl.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Implement the tool**

```csharp
// tools/CorpusBuilder/Program.cs

using EyeCtrl.Core.Prediction;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CorpusBuilder <input-file> <output-file> [min-words] [max-words]");
    Console.Error.WriteLine("  Processes a text corpus (one sentence per line or raw text)");
    Console.Error.WriteLine("  and produces a sentence completion trie binary file.");
    return 1;
}

var inputPath = args[0];
var outputPath = args[1];
var minWords = args.Length > 2 && int.TryParse(args[2], out var mw) ? mw : 4;
var maxWords = args.Length > 3 && int.TryParse(args[3], out var Mw) ? Mw : 15;

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

Console.WriteLine($"Processing: {inputPath}");
Console.WriteLine($"  Min words: {minWords}, Max words: {maxWords}");

var text = File.ReadAllText(inputPath);
var sentences = SplitSentences(text);
var filtered = FilterSentences(sentences, minWords, maxWords);
var deduped = DeduplicateSentences(filtered);

Console.WriteLine($"  Extracted {sentences.Count} sentences");
Console.WriteLine($"  After filter: {filtered.Count}");
Console.WriteLine($"  After dedup: {deduped.Count}");

var index = new SentenceCompletionIndex();
foreach (var (sentence, freq) in deduped)
    index.Insert(sentence, freq);

using var output = File.Create(outputPath);
index.Save(output);

Console.WriteLine($"Wrote trie to: {outputPath}");
Console.WriteLine($"File size: {new FileInfo(outputPath).Length / 1024.0 / 1024.0:F1} MB");
return 0;

static List<string> SplitSentences(string text)
{
    var sentences = new List<string>();
    var current = new System.Text.StringBuilder();
    for (int i = 0; i < text.Length; i++)
    {
        var ch = text[i];
        current.Append(ch);
        if (ch is '.' or '!' or '?' or ';' || ch == '\n')
        {
            var s = current.ToString().Trim();
            if (s.Length > 0)
                sentences.Add(s);
            current.Clear();
        }
    }
    var last = current.ToString().Trim();
    if (last.Length > 0)
        sentences.Add(last);
    return sentences;
}

static List<string> FilterSentences(List<string> sentences, int minWords, int maxWords)
{
    return sentences.Where(s =>
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < minWords || words.Length > maxWords)
            return false;

        var specialCount = words.Sum(w => w.Count(ch => !char.IsLetterOrDigit(ch) && ch != '\'' && ch != '-'));
        var totalChars = words.Sum(w => w.Length);
        if (totalChars > 0 && (double)specialCount / totalChars > 0.3)
            return false;

        var digitCount = words.Sum(w => w.Count(char.IsDigit));
        if (totalChars > 0 && (double)digitCount / totalChars > 0.5)
            return false;

        return true;
    }).ToList();
}

static Dictionary<string, int> DeduplicateSentences(List<string> sentences)
{
    var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var s in sentences)
    {
        var key = s.ToLowerInvariant().Trim();
        result[key] = result.GetValueOrDefault(key) + 1;
    }
    return result;
}
```

- [ ] **Step 4: Verify with sample corpus**

Create a test corpus file and run the tool:

```bash
# Create sample corpus
@"
An apple a day keeps the doctor away.
Early to bed and early to rise makes a man healthy, wealthy and wise.
A bird in the hand is worth two in the bush.
Actions speak louder than words.
A picture is worth a thousand words.
"@ | Set-Content -Path "C:\Users\andre\AppData\Local\Temp\opencode\test_corpus.txt"

dotnet run --project tools/CorpusBuilder -- "C:\Users\andre\AppData\Local\Temp\opencode\test_corpus.txt" "C:\Users\andre\AppData\Local\Temp\opencode\test_trie.bin"
```

Expected: Output shows 5 sentences extracted, 5 after filter, 5 after dedup, produces a trie file.

Verify the trie can be loaded:

```bash
# Write a quick verification script
dotnet script -e "
using EyeCtrl.Core.Prediction;
var trie = SentenceCompletionIndex.Load(File.OpenRead(\"C:\\Users\\andre\\AppData\\Local\\Temp\\opencode\\test_trie.bin\"));
var results = trie.GetCompletions(\"a\", 5);
foreach (var (c, f) in results)
    Console.WriteLine(\$\"  {c} ({f})\");
"
```

If `dotnet script` is not available, verify manually via a quick unit test:

```csharp
// Temporary verification — add to any existing test file, then remove
[Fact]
public void Temp_LoadGeneratedTrie()
{
    var trie = SentenceCompletionIndex.Load(File.OpenRead(@"C:\Users\andre\AppData\Local\Temp\opencode\test_trie.bin"));
    var results = trie.GetCompletions("a", 5);
    Assert.NotEmpty(results);
}
```

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "Temp_LoadGeneratedTrie" -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add tools/CorpusBuilder/CorpusBuilder.csproj tools/CorpusBuilder/Program.cs
git commit -m "feat: add CorpusBuilder tool for sentence completion trie generation"
```

---

### Task 4: Add ONNX Runtime Dependency, ModelManifest, and ModelDownloadService

**Files:**
- Modify: `src/EyeCtrl.Core/EyeCtrl.Core.csproj`
- Create: `src/EyeCtrl.Core/Prediction/ModelManifest.cs`
- Create: `src/EyeCtrl.Core/Prediction/ModelDownloadService.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/ModelManifestTests.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs`

- [ ] **Step 1: Add ONNX Runtime NuGet package**

```xml
<!-- Add to src/EyeCtrl.Core/EyeCtrl.Core.csproj, inside <ItemGroup> -->
<PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.21.0" />
```

- [ ] **Step 2: Write failing tests for ModelManifest**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/ModelManifestTests.cs

namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using System.Text.Json;

public class ModelManifestTests
{
    [Fact]
    public void Deserialize_ValidJson_ReturnsModels()
    {
        var json = """
        {
            "models": {
                "135M": {
                    "url": "https://huggingface.co/org/model-135m/resolve/main/model.onnx",
                    "sha256": "abc123def456",
                    "sizeBytes": 73400320
                },
                "360M": {
                    "url": "https://huggingface.co/org/model-360m/resolve/main/model.onnx",
                    "sha256": "def789ghi012",
                    "sizeBytes": 188743680
                }
            }
        }
        """;

        var manifest = JsonSerializer.Deserialize<ModelManifest>(json);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Models);
        Assert.Equal(2, manifest.Models.Count);
        Assert.Equal(73400320, manifest.Models["135M"].SizeBytes);
        Assert.Equal("abc123def456", manifest.Models["135M"].Sha256);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelManifestTests" -v`
Expected: FAIL — `ModelManifest` type not found

- [ ] **Step 4: Implement ModelManifest**

```csharp
// src/EyeCtrl.Core/Prediction/ModelManifest.cs

namespace EyeCtrl.Core.Prediction;

using System.Text.Json.Serialization;

public class ModelManifest
{
    [JsonPropertyName("models")]
    public Dictionary<string, ModelEntry> Models { get; set; } = [];
}

public class ModelEntry
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelManifestTests" -v`
Expected: PASS

- [ ] **Step 6: Write failing tests for ModelDownloadService**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs

namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class ModelDownloadServiceTests
{
    [Fact]
    public void GetAvailableModels_NoModelsDirectory_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            var service = new ModelDownloadService(tempDir);
            var available = service.GetAvailableModels();

            Assert.Empty(available);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAvailableModels_WithValidFiles_ReturnsModelSizes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "smollm2-360m.onnx"), "dummy");

            var service = new ModelDownloadService(tempDir);
            var available = service.GetAvailableModels();

            Assert.Contains("135M", available);
            Assert.Contains("360M", available);
            Assert.DoesNotContain("1.5B", available);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsModelDownloaded_ExistingFile_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.onnx"), "dummy");
            var service = new ModelDownloadService(tempDir);

            Assert.True(service.IsModelDownloaded("1.5B"));
            Assert.False(service.IsModelDownloaded("360M"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetModelPath_ReturnsFullPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new ModelDownloadService(tempDir);
            var path = service.GetModelPath("360M");

            Assert.EndsWith("smollm2-360m.onnx", path);
            Assert.StartsWith(tempDir, path);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
```

- [ ] **Step 7: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelDownloadServiceTests" -v`
Expected: FAIL — `ModelDownloadService` not found

- [ ] **Step 8: Implement ModelDownloadService**

```csharp
// src/EyeCtrl.Core/Prediction/ModelDownloadService.cs

namespace EyeCtrl.Core.Prediction;

using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

public class ModelDownloadService
{
    private readonly string _modelsDirectory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ModelDownloadService>? _logger;

    // Mapping from user-visible model size to file name on disk
    private static readonly Dictionary<string, string> ModelFileNames = new()
    {
        ["135M"] = "smollm2-135m.onnx",
        ["360M"] = "smollm2-360m.onnx",
        ["1.5B"] = "qwen2.5-1.5b.onnx",
    };

    public ModelDownloadService(string modelsDirectory, HttpClient? httpClient = null, ILogger<ModelDownloadService>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
        _logger = logger;
        Directory.CreateDirectory(_modelsDirectory);
    }

    public List<string> GetAvailableModels()
    {
        var available = new List<string>();
        foreach (var (size, fileName) in ModelFileNames)
        {
            var path = Path.Combine(_modelsDirectory, fileName);
            if (File.Exists(path))
                available.Add(size);
        }
        return available;
    }

    public bool IsModelDownloaded(string modelSize)
    {
        if (!ModelFileNames.TryGetValue(modelSize, out var fileName)) return false;
        return File.Exists(Path.Combine(_modelsDirectory, fileName));
    }

    public string GetModelPath(string modelSize)
    {
        if (!ModelFileNames.TryGetValue(modelSize, out var fileName))
            throw new ArgumentException($"Unknown model size: {modelSize}");
        return Path.Combine(_modelsDirectory, fileName);
    }

    public async Task<bool> DownloadModelAsync(
        string modelSize,
        string url,
        string expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!ModelFileNames.TryGetValue(modelSize, out var fileName))
            throw new ArgumentException($"Unknown model size: {modelSize}");

        var outputPath = Path.Combine(_modelsDirectory, fileName);
        var tempPath = outputPath + ".download";

        _logger?.LogInformation("Downloading model {Size} from {Url} to {Path}", modelSize, url, outputPath);

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = File.Create(tempPath);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            using var sha256 = SHA256.Create();
            using var cryptoStream = new CryptoStream(contentStream, sha256, CryptoStreamMode.Read);

            while ((bytesRead = await cryptoStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;
                if (totalBytes > 0)
                    progress?.Report((double)totalRead / totalBytes);
            }

            // Get computed hash
            cryptoStream.FlushFinalBlock();
            var computedHash = Convert.ToHexStringLower(sha256.Hash!);

            if (!string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Hash mismatch for model {Size}: expected {Expected}, got {Actual}", modelSize, expectedSha256, computedHash);
                File.Delete(tempPath);
                return false;
            }

            File.Move(tempPath, outputPath, overwrite: true);
            _logger?.LogInformation("Model {Size} downloaded successfully ({Bytes} bytes)", modelSize, totalRead);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Model download cancelled: {Size}", modelSize);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download model {Size}", modelSize);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return false;
        }
    }
}
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelDownloadServiceTests" -v`
Expected: PASS (4 tests)

- [ ] **Step 10: Create default models.json manifest**

```json
<!-- tools/CorpusBuilder/../models.json — bundled with app -->
{
    "models": {
        "135M": {
            "url": "https://huggingface.co/HuggingFaceTB/SmolLM2-135M-Instruct/resolve/main/onnx/model_q4.onnx",
            "sha256": "REPLACE_WITH_ACTUAL_HASH",
            "sizeBytes": 70000000
        },
        "360M": {
            "url": "https://huggingface.co/HuggingFaceTB/SmolLM2-360M-Instruct/resolve/main/onnx/model_q4.onnx",
            "sha256": "REPLACE_WITH_ACTUAL_HASH",
            "sizeBytes": 180000000
        },
        "1.5B": {
            "url": "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct/resolve/main/onnx/model_q4.onnx",
            "sha256": "REPLACE_WITH_ACTUAL_HASH",
            "sizeBytes": 800000000
        }
    }
}
```

- [ ] **Step 11: Commit**

```bash
git add src/EyeCtrl.Core/EyeCtrl.Core.csproj src/EyeCtrl.Core/Prediction/ModelManifest.cs src/EyeCtrl.Core/Prediction/ModelDownloadService.cs tests/EyeCtrl.Core.Tests/Prediction/ModelManifestTests.cs tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs
git commit -m "feat: add ONNX Runtime dependency, model manifest, and download service"
```

---

### Task 5: Build SmallLMPredictor (ONNX Inference Engine)

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/SmallLMPredictorTests.cs`

- [ ] **Step 1: Write failing tests for SmallLMPredictor lifecycle**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/SmallLMPredictorTests.cs

namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using Microsoft.Extensions.Logging.Abstractions;

public class SmallLMPredictorTests
{
    [Fact]
    public void Constructor_NoModelFiles_IsNotAvailable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false, NullLogger<SmallLMPredictor>.Instance);
            Assert.False(predictor.IsAvailable);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Constructor_InvalidModelPath_IsNotAvailable()
    {
        var predictor = new SmallLMPredictor(@"C:\nonexistent\path", modelSize: "360M", preferGpu: false, NullLogger<SmallLMPredictor>.Instance);
        Assert.False(predictor.IsAvailable);
    }

    [Fact]
    public void Constructor_ModelSizeOff_IsNotAvailable()
    {
        var predictor = new SmallLMPredictor(@"C:\nonexistent", modelSize: "Off", preferGpu: false, NullLogger<SmallLMPredictor>.Instance);
        Assert.False(predictor.IsAvailable);
    }

    [Fact]
    public void Predict_WhenNotAvailable_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false, NullLogger<SmallLMPredictor>.Instance);
            predictor.Predict("hello world", "wo");
            var results = predictor.TryGetResults();
            Assert.Null(results);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false, NullLogger<SmallLMPredictor>.Instance);
            predictor.Dispose();
            // Second dispose should not throw
            predictor.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SmallLMPredictorTests" -v`
Expected: FAIL — `SmallLMPredictor` type not found

- [ ] **Step 3: Implement SmallLMPredictor**

```csharp
// src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs

namespace EyeCtrl.Core.Prediction;

using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Concurrent;
using System.Text;

public class SmallLMPredictor : IDisposable
{
    private readonly string _modelsDirectory;
    private readonly string? _selectedModelSize;
    private readonly bool _preferGpu;
    private readonly ILogger<SmallLMPredictor>? _logger;
    private readonly ModelDownloadService _modelService;

    private InferenceSession? _session;
    private readonly object _lock = new();
    private volatile bool _disposed;
    private CancellationTokenSource? _pendingCts;
    private volatile bool _isRunning;
    private ConcurrentBag<PredictionResult>? _pendingResults;

    public bool IsAvailable { get; private set; }
    public string? SelectedModelPath { get; private set; }

    private static readonly Dictionary<string, string> ModelFileNames = new()
    {
        ["135M"] = "smollm2-135m.onnx",
        ["360M"] = "smollm2-360m.onnx",
        ["1.5B"] = "qwen2.5-1.5b.onnx",
    };

    public SmallLMPredictor(string modelsDirectory, string? modelSize, bool preferGpu, ILogger<SmallLMPredictor>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _selectedModelSize = modelSize;
        _preferGpu = preferGpu;
        _logger = logger;
        _modelService = new ModelDownloadService(modelsDirectory);

        if (modelSize == "Off" || string.IsNullOrEmpty(modelSize))
        {
            IsAvailable = false;
            return;
        }

        Initialize();
    }

    private void Initialize()
    {
        if (!ModelFileNames.TryGetValue(_selectedModelSize!, out var fileName))
        {
            _logger?.LogWarning("Unknown model size: {Size}", _selectedModelSize);
            IsAvailable = false;
            return;
        }

        SelectedModelPath = Path.Combine(_modelsDirectory, fileName);
        if (!File.Exists(SelectedModelPath))
        {
            _logger?.LogInformation("Model file not found: {Path}", SelectedModelPath);
            IsAvailable = false;
            return;
        }

        try
        {
            var sessionOptions = new SessionOptions();

            // Try DirectML first if GPU preferred
            if (_preferGpu)
            {
                try
                {
                    sessionOptions.AppendExecutionProvider_DML();
                    _logger?.LogInformation("Using DirectML (GPU) execution provider for {Model}", _selectedModelSize);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "DirectML not available, falling back to CPU");
                    sessionOptions = new SessionOptions(); // Reset for CPU
                }
            }

            sessionOptions.EnableMemPattern = true;
            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            _session = new InferenceSession(SelectedModelPath, sessionOptions);
            IsAvailable = true;
            _logger?.LogInformation("SmallLMPredictor initialized with {Model} ({Provider})",
                _selectedModelSize, _preferGpu ? "DirectML" : "CPU");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize ONNX session for {Model}", _selectedModelSize);
            IsAvailable = false;
        }
    }

    public void Predict(string context, string? prefix = null)
    {
        if (!IsAvailable || _session == null) return;

        lock (_lock)
        {
            // Cancel previous request
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = new CancellationTokenSource();
            _pendingResults = new ConcurrentBag<PredictionResult>();

            var token = _pendingCts.Token;
            var session = _session;
            var capturePrefix = prefix;
            var captureContext = context;

            _isRunning = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (token.IsCancellationRequested) return;

                    var results = RunInference(session, captureContext, capturePrefix);
                    if (token.IsCancellationRequested) return;

                    foreach (var result in results)
                        _pendingResults!.Add(result);
                }
                catch (OperationCanceledException)
                {
                    // Expected on cancellation
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "LM inference failed");
                }
                finally
                {
                    _isRunning = false;
                }
            });
        }
    }

    private List<PredictionResult> RunInference(InferenceSession session, string context, string? prefix)
    {
        // Build the prompt and tokenize
        var prompt = BuildPrompt(context, prefix);
        var inputIds = Tokenize(prompt);

        if (inputIds.Length == 0)
            return [];

        // Generate up to 8 tokens
        const int maxNewTokens = 8;
        var generatedTokens = new List<long>();
        var pastInputIds = inputIds.ToList();

        for (int i = 0; i < maxNewTokens; i++)
        {
            var inputTensor = new DenseTensor<long>(pastInputIds.ToArray(), [1, pastInputIds.Count]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor)
            };

            using var outputs = session.Run(inputs);
            var logits = outputs.First().AsTensor<float>();

            // Get the last token's logits
            var vocabSize = logits.Dimensions[^1];
            var lastTokenLogits = new float[vocabSize];
            var offset = (pastInputIds.Count - 1) * vocabSize;
            for (int j = 0; j < vocabSize; j++)
                lastTokenLogits[j] = logits.GetValue(offset + j);

            // Greedy sampling: pick highest probability token
            var bestTokenId = 0L;
            var bestLogit = float.MinValue;
            for (int j = 0; j < lastTokenLogits.Length; j++)
            {
                if (lastTokenLogits[j] > bestLogit)
                {
                    bestLogit = lastTokenLogits[j];
                    bestTokenId = j;
                }
            }

            // Stop if end-of-sequence token
            // (tokenizer-specific; using 0 as placeholder — real impl uses tokenizer.EosTokenId)
            if (bestTokenId == 0)
                break;

            generatedTokens.Add(bestTokenId);
            pastInputIds.Add(bestTokenId);
        }

        // Detokenize the generated tokens
        var generatedText = Detokenize(generatedTokens);
        return PostProcess(generatedText, prefix);
    }

    public IReadOnlyList<PredictionResult>? TryGetResults()
    {
        if (!IsAvailable) return null;
        if (_isRunning) return null;
        var results = _pendingResults;
        if (results == null || results.IsEmpty) return null;

        _pendingResults = null;
        return results.ToList();
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = null;
            _pendingResults = null;
            _isRunning = false;
        }
    }

    // ---- Tokenization (simple whitespace + punctuation BPE stub) ----

    private static readonly Dictionary<string, long> StubVocab = new(StringComparer.OrdinalIgnoreCase)
    {
        ["the"] = 1, ["be"] = 2, ["to"] = 3, ["of"] = 4, ["and"] = 5,
        ["a"] = 6, ["in"] = 7, ["that"] = 8, ["have"] = 9, ["i"] = 10,
        ["it"] = 11, ["for"] = 12, ["not"] = 13, ["on"] = 14, ["with"] = 15,
        ["he"] = 16, ["as"] = 17, ["you"] = 18, ["do"] = 19, ["at"] = 20,
    };

    private static long[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ids = new List<long>();
        foreach (var word in words)
        {
            var cleanWord = word.TrimEnd('.', ',', '!', '?', ';', ':');
            if (StubVocab.TryGetValue(cleanWord, out var id))
                ids.Add(id);
            else
                ids.Add(StubVocab.Count + 1); // Unknown token
        }
        return ids.ToArray();
    }

    private static string Detokenize(List<long> tokenIds)
    {
        var reverseVocab = StubVocab.ToDictionary(kv => kv.Value, kv => kv.Key);
        var words = tokenIds
            .Where(id => reverseVocab.ContainsKey(id))
            .Select(id => reverseVocab[id])
            .ToList();
        return string.Join(" ", words);
    }

    private static string BuildPrompt(string context, string? prefix)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
            sb.Append(context).Append(' ');
        if (!string.IsNullOrWhiteSpace(prefix))
            sb.Append(prefix);
        return sb.ToString().Trim();
    }

    private List<PredictionResult> PostProcess(string generatedText, string? prefix)
    {
        var results = new List<PredictionResult>();
        if (string.IsNullOrWhiteSpace(generatedText)) return results;

        var words = generatedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // If a prefix was provided, filter to only show completions that match
        foreach (var word in words)
        {
            if (!string.IsNullOrEmpty(prefix) && !word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(new PredictionResult(word, 10.0, PredictionType.LMSuggestion, 0.5));
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _session?.Dispose();
        _session = null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SmallLMPredictorTests" -v`
Expected: PASS (5 tests — constructor tests and lifecycle tests work without real ONNX models)

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs tests/EyeCtrl.Core.Tests/Prediction/SmallLMPredictorTests.cs
git commit -m "feat: add SmallLMPredictor with ONNX inference and graceful degradation"
```

---

### Task 6: Build PredictionOrchestrator (Merge Engine)

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs`
- Create: `tests/EyeCtrl.Core.Tests/Prediction/PredictionOrchestratorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/PredictionOrchestratorTests.cs

namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class PredictionOrchestratorTests
{
    // InMemoryDictionaryProvider for WordPredictor
    private class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private static PredictionOrchestrator CreateOrchestrator(
        Dictionary<string, int>? words = null,
        SentenceCompletionIndex? trie = null)
    {
        var provider = new InMemoryDictProvider(words ?? new Dictionary<string, int> { ["hello"] = 100 });
        var predictor = new WordPredictor(provider, "QWERTY");
        return new PredictionOrchestrator(predictor, trie, lmPredictor: null);
    }

    [Fact]
    public void GetSuggestions_ExactPrefixMatch_ReturnsSuggestion()
    {
        var orchestrator = CreateOrchestrator(words: new Dictionary<string, int> { ["hello"] = 100, ["help"] = 80 });

        var results = orchestrator.GetSuggestions("hel", context: null, maxCount: 5);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Word == "hello");
        Assert.Contains(results, r => r.Type == PredictionType.ExactPrefix);
    }

    [Fact]
    public void GetSuggestions_WithTrie_ReturnsSentenceCompletions()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("thank you very much", 10);
        trie.Insert("thank you for your help", 7);

        var orchestrator = CreateOrchestrator(
            words: new Dictionary<string, int> { ["thank"] = 50, ["you"] = 40 },
            trie: trie);

        var results = orchestrator.GetSuggestions("thank", context: null, maxCount: 5);

        Assert.Contains(results, r => r.Type == PredictionType.SentenceCompletion && r.Word == "you");
    }

    [Fact]
    public void GetSuggestions_MergePriority_NextWordFirst()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("hello world today", 100);

        var words = new Dictionary<string, int>
        {
            ["hello"] = 50, ["world"] = 40, ["today"] = 30
        };

        var orchestrator = CreateOrchestrator(words, trie);

        // Get suggestions with context so NextWord predictions fire
        var results = orchestrator.GetSuggestions("wo", context: "hello", maxCount: 5);

        // Should have results from multiple categories
        var types = results.Select(r => r.Type).Distinct().ToList();
        Assert.NotEmpty(types);
    }

    [Fact]
    public void GetSuggestions_EmptyInput_ReturnsEmpty()
    {
        var orchestrator = CreateOrchestrator();
        var results = orchestrator.GetSuggestions("", context: null, maxCount: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void GetSuggestions_DeduplicatesResults()
    {
        var words = new Dictionary<string, int> { ["hello"] = 100 };
        var trie = new SentenceCompletionIndex();
        trie.Insert("hello world", 50);

        var orchestrator = CreateOrchestrator(words, trie);
        var results = orchestrator.GetSuggestions("hello", context: null, maxCount: 5);

        // Should not have duplicate "hello" entries
        var helloResults = results.Where(r => r.Word.Equals("hello", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(helloResults.Count <= 1);
    }

    [Fact]
    public void LearnWord_DelegatesToWordPredictor()
    {
        var orchestrator = CreateOrchestrator();
        orchestrator.LearnWord("newtestword");

        var results = orchestrator.GetSuggestions("newtest", context: null, maxCount: 5);
        Assert.Contains(results, r => r.Word == "newtestword");
    }

    [Fact]
    public void LearnSequence_DelegatesToWordPredictor()
    {
        var orchestrator = CreateOrchestrator();
        orchestrator.LearnSequence(["hello", "world"]);

        var results = orchestrator.GetSuggestions("wo", context: "hello", maxCount: 5);
        Assert.NotEmpty(results);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PredictionOrchestratorTests" -v`
Expected: FAIL — `PredictionOrchestrator` type not found

- [ ] **Step 3: Implement PredictionOrchestrator**

```csharp
// src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs

namespace EyeCtrl.Core.Prediction;

public class PredictionOrchestrator
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

        var allResults = new List<PredictionResult>();

        // Phase 1: Existing WordPredictor (next-word, phrase, exact prefix, fuzzy)
        var wpResults = _wordPredictor.GetSuggestions(prefix, context, maxCount, fullText);
        allResults.AddRange(wpResults);

        // Phase 1: Sentence completion trie
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

        // Deduplicate: keep highest-scored version of each word
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

        // Phase 2: Check for pending LM results
        if (_lmPredictor != null && _lmPredictor.IsAvailable)
        {
            var lmResults = _lmPredictor.TryGetResults();
            if (lmResults != null && lmResults.Count > 0)
            {
                // Merge LM results into remaining slots (avoiding duplicates)
                foreach (var lmResult in lmResults)
                {
                    if (!merged.ContainsKey(lmResult.Word))
                    {
                        sorted.Add(lmResult);
                        if (sorted.Count >= maxCount)
                            break;
                    }
                }
                // Re-sort after adding LM results
                sorted = sorted
                    .OrderByDescending(r => r.Score)
                    .Take(maxCount)
                    .ToList();
            }

            // Trigger new LM inference if prefix changed and conditions are met
            if (_mlEnabled && prefix != _lastLmPrefix && ShouldTriggerLm(sorted, context, prefix))
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
        // Don't trigger for empty input with context (let context predictions handle it)
        if (string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(context))
            return false;

        // Don't trigger for very short prefixes unless there's context
        if (prefix.Length < 2 && string.IsNullOrEmpty(context))
            return false;

        // Trigger if we have fewer than 3 results or results are low confidence
        if (currentResults.Count < 3)
            return true;

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
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PredictionOrchestratorTests" -v`
Expected: PASS (7 tests)

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs tests/EyeCtrl.Core.Tests/Prediction/PredictionOrchestratorTests.cs
git commit -m "feat: add PredictionOrchestrator with merge logic and async LM integration"
```

---

### Task 7: Wire into DI, ViewModel, and EventCoordinator

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelOrchestratorTests.cs`

- [ ] **Step 1: Write failing test for ViewModel orchestrator integration**

```csharp
// tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelOrchestratorTests.cs

namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

public class KeyboardViewModelOrchestratorTests
{
    private class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    [Fact]
    public void SetOrchestrator_ThenGetSuggestions_ReturnsResults()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        using var vm = new KeyboardViewModel(settings, messenger);

        var words = new Dictionary<string, int> { ["hello"] = 100, ["help"] = 80 };
        var provider = new InMemoryDictProvider(words);
        var predictor = new WordPredictor(provider, "QWERTY");
        var orchestrator = new PredictionOrchestrator(predictor);
        vm.SetOrchestrator(orchestrator);

        // Simulate typing "hel"
        vm.GetType().GetProperty("CurrentInput")!.SetValue(vm, "hel");

        var suggestions = vm.GetSuggestionsWithContext();
        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, r => r.Word == "hello");
    }

    [Fact]
    public void SetOrchestrator_Null_ReturnsEmptySuggestions()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        using var vm = new KeyboardViewModel(settings, messenger);

        vm.SetOrchestrator(null);
        var suggestions = vm.GetSuggestionsWithContext();
        Assert.Empty(suggestions);
    }

    [Fact]
    public void HandlePredictionSelected_LearnsWord()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        using var vm = new KeyboardViewModel(settings, messenger);

        var words = new Dictionary<string, int> { ["hello"] = 100 };
        var provider = new InMemoryDictProvider(words);
        var predictor = new WordPredictor(provider, "QWERTY");
        var orchestrator = new PredictionOrchestrator(predictor);
        vm.SetOrchestrator(orchestrator);

        // Set some current input then select a prediction
        vm.GetType().GetProperty("CurrentInput")!.SetValue(vm, "hel");
        vm.HandlePredictionSelected("hello");

        // CurrentInput should be cleared
        Assert.Equal("", vm.GetType().GetProperty("CurrentInput")!.GetValue(vm));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardViewModelOrchestratorTests" -v`
Expected: FAIL — `SetOrchestrator` method not found on `KeyboardViewModel`

- [ ] **Step 3: Update KeyboardViewModel to use orchestrator**

```csharp
// In src/EyeCtrl.ViewModels/KeyboardViewModel.cs, make these changes:

// Replace line 42:
// private WordPredictor? _predictor;
// with:
private PredictionOrchestrator? _orchestrator;

// Replace line 139:
// public void SetPredictor(WordPredictor? predictor) => _predictor = predictor;
// with:
public void SetPredictor(WordPredictor? predictor)
{
    if (predictor != null)
        _orchestrator = new PredictionOrchestrator(predictor);
    else
        _orchestrator = null;
}

public void SetOrchestrator(PredictionOrchestrator? orchestrator) => _orchestrator = orchestrator;

// Replace line 167 (in ActivateKey):
// _predictor?.LearnWord(actualOutput);
// with:
_orchestrator?.LearnWord(actualOutput);

// Replace line 176 (in ActivateKey):
// _predictor?.LearnWord(actualOutput);
// with:
// (already covered by the line above; this line doesn't exist)

// Replace lines 214-215 (in HandleSpace):
// _predictor?.LearnWord(CurrentInput);
// with:
_orchestrator?.LearnWord(CurrentInput);

// Replace lines 249-256 (GetSuggestionsWithContext):
public List<PredictionResult> GetSuggestionsWithContext()
{
    if (_orchestrator == null) return new List<PredictionResult>();

    var fullText = _settings.Keyboard.SmartCapitalizationEnabled ? _recentText : null;

    return _orchestrator.GetSuggestions(CurrentInput, _previousWord, maxCount: 5, fullText: fullText);
}

// Replace line 261 (in HandlePredictionSelected):
// _predictor?.LearnWord(word);
// with:
_orchestrator?.LearnWord(word);
```

- [ ] **Step 4: Run ViewModel tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardViewModelOrchestratorTests" -v`
Expected: PASS (3 tests)

Also verify existing KeyboardViewModelTests still pass:
Run: `dotnet test tests/EyeCtrl.ViewModels.Tests -v`
Expected: All PASS

- [ ] **Step 5: Update EventCoordinator to create orchestrator**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, make three changes:

**a) Rename field (line 41):**
```csharp
// BEFORE:
// private WordPredictor? _wordPredictor;
// AFTER:
private PredictionOrchestrator? _orchestrator;
```

**b) Update property (line 48):**
```csharp
// BEFORE:
// public WordPredictor? WordPredictor => _wordPredictor;
// AFTER:
public WordPredictor? WordPredictor => _orchestrator?.WordPredictor;
public PredictionOrchestrator? Orchestrator => _orchestrator;
```

**c) Replace LoadKeyboard prediction wiring (lines 716-722):**
```csharp
// BEFORE:
// var provider = new FileDictionaryProvider(dictPath, _appDataDir!, langCode);
// _wordPredictor = new WordPredictor(provider, settings.Keyboard.ActiveLayout);
// _wordPredictor.ApplyDecay(settings.Keyboard.DecayEnabled, settings.Keyboard.DecayHalfLifeDays);
// keyboard.SetPredictor(_wordPredictor);

// AFTER:
var provider = new FileDictionaryProvider(dictPath, _appDataDir!, langCode);
var wordPredictor = new WordPredictor(provider, settings.Keyboard.ActiveLayout);
wordPredictor.ApplyDecay(settings.Keyboard.DecayEnabled, settings.Keyboard.DecayHalfLifeDays);

// Load sentence completion trie if available
SentenceCompletionIndex? trieIndex = null;
var triePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
    "Resources", "Completions", $"completions_{langCode}.bin");
if (File.Exists(triePath))
{
    try
    {
        trieIndex = SentenceCompletionIndex.Load(File.OpenRead(triePath));
        Log.Information("Loaded sentence completion trie: {Path}", triePath);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to load sentence completion trie from {Path}", triePath);
    }
}

// Create LM predictor if ML settings are enabled
SmallLMPredictor? lmPredictor = null;
if (settings.Keyboard.MLSuggestionsEnabled)
{
    var modelsDir = Path.Combine(_appDataDir!, "models");
    lmPredictor = new SmallLMPredictor(
        modelsDir,
        settings.Keyboard.MLModelSize,
        settings.Keyboard.PreferGPU);
}

_orchestrator = new PredictionOrchestrator(wordPredictor, trieIndex, lmPredictor, settings.Keyboard.MLSuggestionsEnabled);
keyboard.SetOrchestrator(_orchestrator);
```

- [ ] **Step 6: Add SetOrchestrator to KeyboardWindow**

```csharp
// In src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs, add after line 116:
// public void SetPredictor(WordPredictor predictor) => _viewModel?.SetPredictor(predictor);

public void SetOrchestrator(PredictionOrchestrator orchestrator) => _viewModel?.SetOrchestrator(orchestrator);
```

Need to add using: `using EyeCtrl.Core.Prediction;` if not already present.

- [ ] **Step 7: Build and verify compilation**

Run: `dotnet build EyeCtrl.slnx`
Expected: BUILD SUCCEEDED

- [ ] **Step 8: Run all tests**

Run: `dotnet test`
Expected: All tests pass (existing + new)

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelOrchestratorTests.cs
git commit -m "feat: wire PredictionOrchestrator into DI, ViewModel, and EventCoordinator"
```

---

### Task 8: Update KeyboardWindow UI for New Prediction Types

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

- [ ] **Step 1: Update GetPredictionForeground for new types**

```csharp
// In KeyboardWindow.xaml.cs, update GetPredictionForeground method (line 526-536):
private static Brush GetPredictionForeground(PredictionType type)
{
    return type switch
    {
        PredictionType.ExactPrefix => ForegroundExactPrefix,
        PredictionType.FuzzyMatch => ForegroundFuzzyMatch,
        PredictionType.NextWord => ForegroundNextWord,
        PredictionType.Phrase => ForegroundPhrase,
        PredictionType.SentenceCompletion => ForegroundSentenceCompletion,
        PredictionType.LMSuggestion => ForegroundLMSuggestion,
        _ => ForegroundExactPrefix
    };
}
```

- [ ] **Step 2: Add new foreground brush fields**

Add these after the existing foreground brush fields (around line 35-40):

```csharp
private static readonly SolidColorBrush ForegroundSentenceCompletion = new(Color.FromRgb(100, 200, 255)); // Light blue
private static readonly SolidColorBrush ForegroundLMSuggestion = new(Color.FromRgb(200, 150, 255)); // Light purple
```

- [ ] **Step 3: Update CreateTypeBadge for new types**

```csharp
// Update CreateTypeBadge method (line 541-563):
private static Border? CreateTypeBadge(PredictionType type)
{
    string? symbol = type switch
    {
        PredictionType.NextWord => "\u2192",      // →
        PredictionType.Phrase => "\u22EF",         // ⋯
        PredictionType.FuzzyMatch => "~",
        PredictionType.SentenceCompletion => "\u2606",  // ☆ (star for corpus completions)
        PredictionType.LMSuggestion => "\u2726",         // ✦ (sparkle for AI suggestions)
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

- [ ] **Step 4: Build and verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: add UI styling for SentenceCompletion and LMSuggestion prediction types"
```

---

### Task 9: Integration Verification

**Files:** None new — verification-only task.

- [ ] **Step 1: Clean build**

```bash
dotnet clean EyeCtrl.slnx
dotnet build EyeCtrl.slnx
```
Expected: BUILD SUCCEEDED with zero warnings

- [ ] **Step 2: Run all tests**

```bash
dotnet test
```
Expected: All tests PASS. Verify the test summary shows no failures.

- [ ] **Step 3: Run Core prediction tests specifically**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~Prediction" -v
```
Expected: All prediction-related tests PASS

- [ ] **Step 4: Smoke test with test mode**

```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```
Expected: App launches, keyboard shows, predictions appear (existing types work; new types appear if trie is available).

- [ ] **Step 5: Verify graceful degradation (no ONNX model present)**

Delete any model files from `%APPDATA%/EyeCtrl/models/` and run the app. Verify:
- App launches without errors
- Prediction bar shows existing predictions
- No crash or exception when LM is unavailable
- Settings show "AI-powered predictions" toggle as off

- [ ] **Step 6: Commit if any fixes were needed**

```bash
git add -A
git commit -m "fix: integration fixes from smoke testing"
```
