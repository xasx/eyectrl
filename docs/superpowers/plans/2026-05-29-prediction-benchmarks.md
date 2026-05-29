# Prediction Pipeline Benchmarks — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expand `benchmarks/EyeCtrl.Benchmarks/` from 5 WordPredictor-only benchmarks to 28 benchmarks covering the full prediction pipeline across 7 classes.

**Architecture:** Add 6 new benchmark files + modify `Program.cs` to use `BenchmarkSwitcher` for auto-discovery. Each file follows the existing `[SimpleJob(RuntimeMoniker.Net10_0)][MemoryDiagnoser][RankColumn]` pattern. SmallLMBenchmarks are gated via a `Program.cs` pre-flight check that excludes the class from the run if no model/tokenizer files are present.

**Tech Stack:** BenchmarkDotNet 0.15.8, .NET 10 (net10.0 TFM). No new NuGet packages.

---

### Task 1: Program.cs — Switch to BenchmarkSwitcher with SmallLM gating

**Files:**
- Modify: `benchmarks/EyeCtrl.Benchmarks/Program.cs`
- Read for reference: `benchmarks/EyeCtrl.Benchmarks/PredictionBenchmarks.cs` (existing pattern)

- [ ] **Step 1: Replace Program.cs with BenchmarkSwitcher + model pre-flight check**

Read the existing `Program.cs` to confirm current content, then replace it:

```csharp
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Running;

namespace EyeCtrl.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance);

        if (!SmallLMModelAvailable())
        {
            config = config.AddFilter(new DisableClassFilter(nameof(SmallLMBenchmarks)));
        }

        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, config);
    }

    private static bool SmallLMModelAvailable()
    {
        var envDir = Environment.GetEnvironmentVariable("EYECTRL_MODEL_DIR");
        var modelDir = envDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "models");

        if (!Directory.Exists(modelDir))
            return false;

        var onnxFiles = Directory.GetFiles(modelDir, "*.onnx", SearchOption.TopDirectoryOnly);
        var tokenizerFiles = Directory.GetFiles(modelDir, "*.tokenizer.json", SearchOption.TopDirectoryOnly);

        return onnxFiles.Length > 0 && tokenizerFiles.Length > 0;
    }
}

/// <summary>
/// BenchmarkDotNet filter that excludes benchmark classes by name.
/// </summary>
public class DisableClassFilter : IFilter
{
    private readonly string _className;

    public DisableClassFilter(string className) => _className = className;

    public bool Predicate(BenchmarkCase benchmarkCase) =>
        benchmarkCase.Descriptor.Type.Name != _className;
}
```

- [ ] **Step 2: Build to verify compilation**

```
dotnet build benchmarks/EyeCtrl.Benchmarks --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```
git add benchmarks/EyeCtrl.Benchmarks/Program.cs
git commit -m "feat: switch benchmarks to BenchmarkSwitcher with SmallLM gating"
```

---

### Task 2: BigramPhraseBenchmarks.cs

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/BigramPhraseBenchmarks.cs`
- Read for reference: `src/EyeCtrl.Core/Prediction/BigramModel.cs` (method signatures), `src/EyeCtrl.Core/Prediction/PhraseModel.cs`

- [ ] **Step 1: Write BigramPhraseBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class BigramPhraseBenchmarks
{
    private BigramModel _bigramModel = null!;
    private PhraseModel _phraseModel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bigramModel = new BigramModel();

        // 500 distinct word pairs, each learned 2x to survive MinFrequency=2 filter
        // Context word must be actually present in learned data for GetNextWords to return results
        for (int i = 0; i < 500; i++)
        {
            var word1 = $"context{i % 25}";
            var word2 = $"target{i}";
            _bigramModel.Learn(word1, word2);
            _bigramModel.Learn(word1, word2);
        }
        // Ensure "hello" context has entries
        for (int i = 0; i < 50; i++)
        {
            _bigramModel.Learn("hello", $"world{i}");
            _bigramModel.Learn("hello", $"world{i}");
        }

        // Large dataset for ApplyDecay: 2000 bigrams
        for (int i = 0; i < 1000; i++)
        {
            _bigramModel.Learn($"decay{i % 100}", $"next{i}");
            _bigramModel.Learn($"decay{i % 100}", $"next{i}");
        }

        // PhraseModel: learn 1500 three-word phrases (1000 sequences, each 2x)
        _phraseModel = new PhraseModel();
        for (int i = 0; i < 1000; i++)
        {
            var phrase = new[] { $"hello{i % 5}", $"there{i % 5}", $"friend{i % 100}" };
            _phraseModel.LearnPhrase(phrase);
            _phraseModel.LearnPhrase(phrase);
        }
        // Ensure "hello there" prefix has entries
        for (int i = 0; i < 100; i++)
        {
            _phraseModel.LearnPhrase(new[] { "hello", "there", $"continuation{i}" });
            _phraseModel.LearnPhrase(new[] { "hello", "there", $"continuation{i}" });
        }
    }

    [Benchmark]
    public List<(string word, int frequency)> Bigram_GetNextWords()
    {
        return _bigramModel.GetNextWords("hello", 5);
    }

    [Benchmark]
    public void Bigram_ApplyDecay_Large()
    {
        _bigramModel.ApplyDecay(true, 30);
    }

    [Benchmark]
    public List<(string phrase, int frequency)> Phrase_GetPhrases()
    {
        return _phraseModel.GetPhrases("hello there", 5);
    }

    [Benchmark]
    public void Phrase_ApplyDecay_Large()
    {
        _phraseModel.ApplyDecay(true, 30);
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build benchmarks/EyeCtrl.Benchmarks --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Run to verify benchmark produces non-zero results**

```
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --filter *BigramPhrase* --job short
```

Expected: Every benchmark completes with non-null result; `Bigram_GetNextWords` should return a list with items.

- [ ] **Step 4: Commit**

```
git add benchmarks/EyeCtrl.Benchmarks/BigramPhraseBenchmarks.cs
git commit -m "feat: add BigramModel and PhraseModel benchmarks"
```

---

### Task 3: SentenceCompletionBenchmarks.cs

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/SentenceCompletionBenchmarks.cs`
- Read for reference: `src/EyeCtrl.Core/Prediction/SentenceCompletionIndex.cs`

- [ ] **Step 1: Write SentenceCompletionBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class SentenceCompletionBenchmarks
{
    private string[] _sentences = null!;
    private SentenceCompletionIndex _index = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 5000 synthetic sentences
        _sentences = new string[5000];
        var subjects = new[] { "the quick brown fox", "my old friend", "the developer", "a curious cat", "our team" };
        var verbs = new[] { "jumps over", "runs toward", "looks at", "works on", "thinks about" };
        var objects = new[] { "the lazy dog", "the distant hill", "the project plan", "the window sill", "the deadline" };

        for (int i = 0; i < 5000; i++)
        {
            _sentences[i] = $"{subjects[i % 5]} {verbs[(i / 5) % 5]} {objects[(i / 25) % 5]}";
        }

        // Pre-populated index for query benchmarks
        _index = new SentenceCompletionIndex();
        for (int i = 0; i < _sentences.Length; i++)
        {
            _index.Insert(_sentences[i], (i % 100) + 1);
        }
    }

    [IterationSetup(Target = nameof(Insert_SingleSentence))]
    public void InsertSetup()
    {
        // Fresh index per invocation; IterationSetup forces UnrollFactor=1,
        // so InvocationCount=1 below is valid (BDN requires InvocationCount
        // to be a multiple of UnrollFactor when UnrollFactor > 1).
        _index = new SentenceCompletionIndex();
    }

    [Benchmark(InvocationCount = 1)]
    public void Insert_SingleSentence()
    {
        _index.Insert("the quick brown fox jumps over the lazy dog", 10);
    }

    [Benchmark]
    public List<(string completion, int frequency)> GetCompletions_DeepMatch()
    {
        return _index.GetCompletions("the quick brown fox", 5);
    }

    [Benchmark]
    public List<(string completion, int frequency)> GetCompletions_NoMatch()
    {
        return _index.GetCompletions("xyznonexistent", 5);
    }

    [Benchmark]
    public void BoostAndDismiss()
    {
        _index.BoostCompletion("the quick brown fox jumps over the lazy dog");
        _index.DismissCompletion("the quick brown fox jumps over the lazy dog");
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build benchmarks/EyeCtrl.Benchmarks --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Run to verify**

```
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --filter *SentenceCompletion* --job short
```

Expected: `GetCompletions_DeepMatch` returns non-empty list; `GetCompletions_NoMatch` returns empty; `Insert_SingleSentence` and `BoostAndDismiss` complete without error.

- [ ] **Step 4: Commit**

```
git add benchmarks/EyeCtrl.Benchmarks/SentenceCompletionBenchmarks.cs
git commit -m "feat: add SentenceCompletionIndex benchmarks"
```

---

### Task 4: FuzzyEditBenchmarks.cs

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/FuzzyEditBenchmarks.cs`
- Read for reference: `src/EyeCtrl.Core/Prediction/FuzzyEditDistance.cs`, `src/EyeCtrl.Core/Prediction/KeyboardAdjacencyMap.cs`

- [ ] **Step 1: Write FuzzyEditBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class FuzzyEditBenchmarks
{
    private Dictionary<char, HashSet<char>> _adjacencyMap = null!;
    private readonly string _a3 = "hel";
    private readonly string _b3 = "hlo";
    private readonly string _a6 = "system";
    private readonly string _b6 = "systen";
    private readonly string _a11 = "application";
    private readonly string _b11 = "applucation";
    private readonly string _a25 = "extraordinarymeetingrooms";
    private readonly string _b25 = "extraordinerymeetingroomz";

    [GlobalSetup]
    public void Setup()
    {
        _adjacencyMap = KeyboardAdjacencyMap.For("QWERTY");
    }

    [Benchmark]
    public double Compute_3chars()
    {
        return FuzzyEditDistance.Compute(_a3, _b3, _adjacencyMap);
    }

    [Benchmark]
    public double Compute_6chars()
    {
        return FuzzyEditDistance.Compute(_a6, _b6, _adjacencyMap);
    }

    [Benchmark]
    public double Compute_11chars()
    {
        return FuzzyEditDistance.Compute(_a11, _b11, _adjacencyMap);
    }

    [Benchmark]
    public double Compute_25chars()
    {
        return FuzzyEditDistance.Compute(_a25, _b25, _adjacencyMap);
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build benchmarks/EyeCtrl.Benchmarks --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Run to verify**

```
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --filter *FuzzyEdit* --job short
```

Expected: All 4 benchmarks produce positive double values (`Compute_3chars` returns 1.5, longer strings return proportionally larger values).

- [ ] **Step 4: Commit**

```
git add benchmarks/EyeCtrl.Benchmarks/FuzzyEditBenchmarks.cs
git commit -m "feat: add FuzzyEditDistance benchmarks"
```

---

### Task 5: CasingBenchmarks.cs

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/CasingBenchmarks.cs`
- Read for reference: `src/EyeCtrl.Core/Prediction/CapitalizationHelper.cs`, `src/EyeCtrl.Core/Prediction/CasingHelper.cs`

- [ ] **Step 1: Write CasingBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class CasingBenchmarks
{
    private IReadOnlySet<string> _abbreviations = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Abbreviations include the trailing period, matching CapitalizationHelper's comparison
        _abbreviations = new HashSet<string>(StringComparer.Ordinal) { "Dr.", "Mr.", "Ms." };
    }

    [Benchmark]
    public bool ShouldCapitalize_AfterPeriod()
    {
        return CapitalizationHelper.ShouldCapitalize("The sentence ended.");
    }

    [Benchmark]
    public bool ShouldCapitalize_MidSentence()
    {
        return CapitalizationHelper.ShouldCapitalize("hello world");
    }

    [Benchmark]
    public bool ShouldCapitalize_WithAbbrev()
    {
        return CapitalizationHelper.ShouldCapitalize("I saw Dr.", _abbreviations);
    }

    [Benchmark]
    public string ApplySmartCapitalization()
    {
        return CapitalizationHelper.ApplySmartCapitalization("hello", "What is ");
    }

    [Benchmark]
    public bool CasingHelper_IsAllCaps()
    {
        return CasingHelper.IsAllCaps("HELLO WORLD");
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build benchmarks/EyeCtrl.Benchmarks --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Run to verify**

```
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --filter *Casing* --job short
```

Expected: `ShouldCapitalize_AfterPeriod` returns `true`; `ShouldCapitalize_WithAbbrev` returns `false`; `CasingHelper_IsAllCaps` returns `true`.

- [ ] **Step 4: Commit**

```
git add benchmarks/EyeCtrl.Benchmarks/CasingBenchmarks.cs
git commit -m "feat: add CapitalizationHelper and CasingHelper benchmarks"
```

---

### Task 6: OrchestratorBenchmarks.cs

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/OrchestratorBenchmarks.cs`
- Read for reference: `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs`, `src/EyeCtrl.Core/Prediction/WordPredictor.cs`, `src/EyeCtrl.Core/Prediction/IDictionaryProvider.cs`

- [ ] **Step 1: Write OrchestratorBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class OrchestratorBenchmarks
{
    private PredictionOrchestrator _orchestrator = null!;
    private PredictionOrchestrator _orchestratorWithTrie = null!;

    [GlobalSetup]
    public void Setup()
    {
        var words = new Dictionary<string, int>();
        for (int i = 0; i < 10000; i++)
        {
            var word = $"word{i}";
            words[word] = 10000 - i;
        }
        // Ensure "hel" prefix has matches
        words["hello"] = 500;
        words["help"] = 400;
        words["held"] = 300;

        var predictor = new WordPredictor(new InMemoryDictionaryProvider(words));
        // Pre-load bigrams for "hello" context — learn 2x to survive MinFrequency=2 filter
        foreach (var seq in new[] { new[] { "hello", "world" }, new[] { "hello", "there" }, new[] { "hello", "friend" } })
        {
            predictor.LearnSequence(seq);
            predictor.LearnSequence(seq);
        }

        _orchestrator = new PredictionOrchestrator(predictor, sentenceIndex: null, lmPredictor: null, mlEnabled: false);

        // Orchestrator with SentenceCompletionIndex
        var triePredictor = new WordPredictor(new InMemoryDictionaryProvider(words));
        var trieIndex = new SentenceCompletionIndex();
        for (int i = 0; i < 500; i++)
        {
            trieIndex.Insert($"How do I turn this on {i}", i + 1);
        }
        _orchestratorWithTrie = new PredictionOrchestrator(triePredictor, sentenceIndex: trieIndex, lmPredictor: null, mlEnabled: false);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_WordOnly()
    {
        return _orchestrator.GetSuggestions("hel", null, 5);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_WithContext()
    {
        return _orchestrator.GetSuggestions("", "hello", 5);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_WithSentenceIndex()
    {
        return _orchestratorWithTrie.GetSuggestions("How do I", null, 5);
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build benchmarks/EyeCtrl.Benchmarks --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Run to verify**

```
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --filter *Orchestrator* --job short
```

Expected: `GetSuggestions_WordOnly` returns non-empty list (e.g., "hello", "help", "held"); `GetSuggestions_WithContext` returns non-empty list (e.g., "world", "there", "friend"); `GetSuggestions_WithSentenceIndex` returns sentence completions.

- [ ] **Step 4: Commit**

```
git add benchmarks/EyeCtrl.Benchmarks/OrchestratorBenchmarks.cs
git commit -m "feat: add PredictionOrchestrator benchmarks"
```

---

### Task 7: SmallLMBenchmarks.cs

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/SmallLMBenchmarks.cs`
- Read for reference: `src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs`, `src/EyeCtrl.Core/Prediction/LMTokenizer.cs`, `src/EyeCtrl.Core/Prediction/ModelConstants.cs`

> **Note:** `onnxFiles[0]` and `tokenizerFiles[0]` pick the first file found via glob — they assume any .onnx + any .tokenizer.json pair is a compatible model-tokenizer pair. If the models directory has multiple incompatible models, the benchmark will fail on model loading. For benchmark purposes, a single-model directory is the expected setup. Additionally, `Tokenizer_Encode/Decode` are gated on full model availability (the Program.cs filter requires both an .onnx *and* a tokenizer), so tokenizer-only setups won't run these benchmarks either.

- [ ] **Step 1: Write SmallLMBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class SmallLMBenchmarks
{
    private SmallLMPredictor _predictor = null!;
    private LMTokenizer _tokenizer = null!;
    private bool _available;

    [GlobalSetup]
    public void Setup()
    {
        var envDir = Environment.GetEnvironmentVariable("EYECTRL_MODEL_DIR");
        var modelDir = envDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "models");

        var onnxFiles = Directory.Exists(modelDir)
            ? Directory.GetFiles(modelDir, "*.onnx", SearchOption.TopDirectoryOnly)
            : [];

        var tokenizerFiles = Directory.Exists(modelDir)
            ? Directory.GetFiles(modelDir, "*.tokenizer.json", SearchOption.TopDirectoryOnly)
            : [];

        _available = onnxFiles.Length > 0 && tokenizerFiles.Length > 0;

        if (!_available)
        {
            // No model/tokenizer — all benchmarks no-op. Left null fields; benchmarks
            // will be skipped by Program.cs filter, but guard anyway for when filter
            // is not applied (e.g., manual --filter *SmallLM*).
            return;
        }

        var tokenizerPath = tokenizerFiles[0];
        _tokenizer = new LMTokenizer(tokenizerPath);

        // Use the smallest model by default; first .onnx file found
        var modelPath = onnxFiles[0];

        _predictor = new SmallLMPredictor(
            modelsDirectory: modelDir,
            modelSize: "135M",     // any valid ModelConstants key or non-null/non-"Off" clears the guard
            preferGpu: false,
            logger: null,
            tokenizer: _tokenizer,
            modelPath: modelPath);   // explicit path overrides ModelConstants resolution
    }

    [Benchmark]
    public long[] Tokenizer_Encode()
    {
        if (!_available) return [];
        return _tokenizer.Encode("The quick brown fox jumps over the lazy dog");
    }

    [Benchmark]
    public string Tokenizer_Decode()
    {
        if (!_available) return string.Empty;
        var tokenIds = _tokenizer.Encode("The quick brown fox jumps over the lazy dog");
        return _tokenizer.Decode(tokenIds);
    }

    [Benchmark]
    public IReadOnlyList<PredictionResult>? PredictAndWait()
    {
        if (!_available || !_predictor.IsAvailable) return null;

        _predictor.Predict("The quick brown fox", null);

        // Spin-wait for inference to complete (5s timeout)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<PredictionResult>? results;
        while ((results = _predictor.TryGetResults()) == null)
        {
            if (DateTime.UtcNow > deadline)
                return null;
            Thread.Sleep(1);
        }

        return results;
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build benchmarks/EyeCtrl.Benchmarks --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Run without model (should skip entirely)**

```
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --filter *SmallLM*
```

Expected: "No benchmarks found" or zero benchmark rows (Program.cs filter excludes the class).

- [ ] **Step 4: Commit**

```
git add benchmarks/EyeCtrl.Benchmarks/SmallLMBenchmarks.cs
git commit -m "feat: add SmallLMPredictor and LMTokenizer benchmarks with graceful skip"
```

---

### Task 8: Full verification — build and run all benchmarks

**Files:** None (verification only)

- [ ] **Step 1: Build everything**

```
dotnet build benchmarks/EyeCtrl.Benchmarks
```

Expected: 0 errors, 0 warnings from the benchmark project (warnings from EyeCtrl.Core are pre-existing XML-doc warnings and can be ignored).

- [ ] **Step 2: Run all benchmarks (short job for speed)**

```
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --job short
```

Expected: All 25 non-SmallLM benchmarks (5 existing + 4 + 4 + 4 + 5 + 3 = 25) execute and produce numbers. No crashes, no `InvalidOperationException`, no null-reference exceptions. SmallLMBenchmarks is excluded by the filter (if no model present) or runs (if model present).

- [ ] **Step 3: Verify benchmark results are sane**

Spot-check the output table:
- `Bigram_GetNextWords` mean should be in µs range, not 0 ns
- `FuzzyEditDistance.Compute_3chars` mean should be in ns–µs range, returning ~1.5
- `CasingHelper_IsAllCaps` should return `true`
- `Orchestrator.GetSuggestions_WordOnly` should return a list of 5 strings (not empty)

- [ ] **Step 4: Final commit**

```
git add -A
git commit -m "feat: complete prediction pipeline benchmarks (7 classes, 28 benchmarks)"
```

---

## Self-Review

**1. Spec coverage:** All sections covered — BigramPhrase (Task 2), SentenceCompletion (Task 3), FuzzyEdit (Task 4), Casing (Task 5), Orchestrator (Task 6), SmallLM (Task 7), Program.cs/Net10_0/gating (Task 1), verification (Task 8).

**2. Placeholder scan:** No TBD, TODO, or vague instructions. Every step has complete code or exact commands with expected output.

**3. Runtime-behavior review (post-type-check):**
- `ShouldCapitalize("The sentence ended.")` → returns `true` (last word "ended." is >4 chars, bypasses abbreviation heuristic)
- Orchestrator bigrams learned 2× → survive `MinFrequency=2` filter
- `SmallLMPredictor` constructor receives `modelSize: "135M"` → passes the null/empty/"Off" guard, then `modelPath` takes over
- SentenceCompletion foreach loop uses `for` with index variable `i`
- `Program.cs` imports `BenchmarkDotNet.Filters` for `IFilter`

**4. Task 8 benchmark count:** 25 non-SmallLM benchmarks (5 existing + 4 BigramPhrase + 4 SentenceCompletion + 4 FuzzyEdit + 5 Casing + 3 Orchestrator).
