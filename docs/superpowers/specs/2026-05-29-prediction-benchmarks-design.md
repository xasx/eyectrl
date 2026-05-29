# Prediction Pipeline Benchmarks — Design Spec

**Date:** 2026-05-29
**Status:** Draft (revised after code review)

## Goal

Expand `benchmarks/EyeCtrl.Benchmarks/` to cover the full prediction pipeline, not just `WordPredictor`. When complete, `dotnet run --project benchmarks/EyeCtrl.Benchmarks` will discover and run all benchmark classes, giving a comprehensive view of prediction latency and memory usage.

## Non-Goals

- Gaze processing pipeline (OneEuroFilter, FixationDetector, DwellDetector, etc.) — deferred to a future spec
- Replacing BenchmarkDotNet with another framework
- Adding benchmarks to the main solution file (stays standalone per AGENTS.md)

## Architecture

### File Layout

All new files live in `benchmarks/EyeCtrl.Benchmarks/`, file-scoped namespace `EyeCtrl.Benchmarks`.

| File | Component(s) | Benchmark count |
|------|-------------|----------------|
| `PredictionBenchmarks.cs` | WordPredictor | 5 (existing, unchanged) |
| `BigramPhraseBenchmarks.cs` | BigramModel, PhraseModel | 4 |
| `SentenceCompletionBenchmarks.cs` | SentenceCompletionIndex | 4 |
| `FuzzyEditBenchmarks.cs` | FuzzyEditDistance | 4 |
| `CasingBenchmarks.cs` | CapitalizationHelper, CasingHelper, CasingPreferenceTracker | 5 |
| `OrchestratorBenchmarks.cs` | PredictionOrchestrator | 3 |
| `SmallLMBenchmarks.cs` | SmallLMPredictor, LMTokenizer | 3 |

**Total: 28 benchmarks across 7 files** (5 existing + 23 new)

### Program.cs Change

Replace hardcoded `BenchmarkRunner.Run<PredictionBenchmarks>(args)` with `BenchmarkSwitcher.Run(args)` so all `[Benchmark]`-decorated classes in the assembly are auto-discovered. Users can still filter via CLI: `--filter *SmallLM*`.

### Shared Pattern

Every benchmark file follows the same conventions as the existing `PredictionBenchmarks.cs`:

```csharp
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class FooBenchmarks
{
    // Fields initialized in GlobalSetup
    private Foo _foo = null!;

    [GlobalSetup]
    public void Setup() { /* synthetic data */ }

    [Benchmark]
    public SomeType BenchmarkName() { /* ... */ }
}
```

- `[SimpleJob(RuntimeMoniker.Net10_0)]` — targets .NET 10 (matching `net10.0` TFM in csproj)
- `[MemoryDiagnoser]` — measures GC allocations
- `[RankColumn]` — ranks benchmarks within the class
- Reuse the existing public `InMemoryDictionaryProvider` class from `PredictionBenchmarks.cs` where needed
- No mocking library — consistent with the repo's testing conventions

## Detailed Scenarios

### BigramPhraseBenchmarks

Synthetic setup: learn each pair at least 2x so results survive the `MinFrequency = 2` filter in `GetNextWords`/`GetPhrases`. Example: 500 distinct word pairs learned twice each = 1000 calls to `Learn()`, producing 500 bigrams with frequency >= 2. Query contexts (`"hello"`, `"application"`) must be keys actually present in the learned data.

`GetPhrases(prefix, maxCount)` is an exact dictionary key lookup (`_phrases.TryGetValue(prefix)`), not character-prefix matching. The prefix is a space-separated word sequence. Cost driver is the number of continuations under that key, not character length. Setup must ensure the queried prefix string is a real stored key.

| Benchmark | Method | Dataset | Measures |
|-----------|--------|---------|----------|
| `Bigram_GetNextWords` | `GetNextWords("hello", 5)` | 500 bigrams (each pair learned 2x), context="hello" | Dictionary lookup + LINQ sort |
| `Bigram_ApplyDecay_Large` | `ApplyDecay(true, 30)` | 2000 bigrams, halfLifeDays=30 | Full bigram scan + decay |
| `Phrase_GetPhrases` | `GetPhrases("hello there", 5)` | 1500 phrases, prefix matches real key | Dictionary lookup + sort |
| `Phrase_ApplyDecay_Large` | `ApplyDecay(true, 30)` | 1500 phrases | Full phrase scan + decay |

### SentenceCompletionBenchmarks

Synthetic setup: 5000 sentences generated programmatically in `[GlobalSetup]` (combinations of common words). For the insert benchmark, use `[IterationSetup]` to build a fresh empty index before each invocation, and `InvocationCount = 1` to prevent unbounded trie growth within a single measured iteration.

`GetCompletions` splits the prefix into words and walks the trie depth-first. A nonexistent prefix's first word is not found in the root `Children`, so it returns `[]` immediately at depth 0 — this is the best case (early termination), not worst case. The worst case is a deep, fully-matching prefix with many completions to sort.

| Benchmark | Method | Setup | Measures |
|-----------|--------|-------|----------|
| `Insert_SingleSentence` | `Insert(sentence, 1)` | Fresh empty index per invocation (`InvocationCount=1`) | Single-insert cost (walk + node allocation) |
| `GetCompletions_DeepMatch` | `GetCompletions("the quick brown fox", 5)` | 5000 sentences, prefix deeply matches real data | Deep trie walk + sort over many completions |
| `GetCompletions_NoMatch` | `GetCompletions("xyznonexistent", 5)` | Same dataset | Early termination (depth 0 miss) |
| `BoostAndDismiss` | `BoostCompletion()` + `DismissCompletion()` | Pre-populated index with target sentence present | Mutable operations overhead |

### FuzzyEditBenchmarks

`FuzzyEditDistance.Compute(string a, string b, Dictionary<char, HashSet<char>> adjacencyMap)` — three args, not two. Build the QWERTY adjacency map via `KeyboardAdjacencyMap.For("QWERTY")` in `[GlobalSetup]`.

The method always allocates a full `(m+1)×(n+1)` DP table and runs the double loop regardless of content (no early exit). Runtime is purely O(m*n) on string lengths; identical vs. different content has negligible difference. Benchmarks vary only by length.

| Benchmark | Input A | Input B | Measures |
|-----------|---------|---------|----------|
| `Compute_3chars` | "hel" | "hlo" | 3-char typical typo (adjacent key) |
| `Compute_6chars` | "system" | "systen" | 6-char adjacent-key typo |
| `Compute_12chars` | "application" | "applucation" | 12-char with typo |
| `Compute_25chars` | "extraordinarymeetingrooms" | "extraordinerymeetingroomz" | 25-char with multiple typos |

### CasingBenchmarks

CapitalizationHelper checks the *last word* of the trimmed text (after period). Inputs must end with the abbreviation for the abbreviation branch to be reached, and abbreviations include the trailing period (e.g. `"Dr."`, not `"Dr"`).

| Benchmark | Method | Setup |
|-----------|--------|-------|
| `ShouldCapitalize_AfterPeriod` | `ShouldCapitalize("I saw it.")` | Input ends with a period → returns true |
| `ShouldCapitalize_MidSentence` | `ShouldCapitalize("hello world")` | No terminal punctuation → returns false |
| `ShouldCapitalize_WithAbbrev` | `ShouldCapitalize("I saw Dr.", abbrevs)` | Input ends with "Dr."; abbreviation set `{"Dr.", "Mr.", "Ms."}` → returns false |
| `ApplySmartCapitalization` | `ApplySmartCapitalization("hello", "What is ")` | Standard capitalization path |
| `CasingHelper_IsAllCaps` | `IsAllCaps("HELLO WORLD")` | Two-word all-caps string (most representative input) |

### OrchestratorBenchmarks

Uses same `InMemoryDictionaryProvider` with 10K words as existing benchmarks. No ML component (SmallLMPredictor is null).

`PredictionOrchestrator.GetSuggestions("", null, 5)` returns `[]` immediately via early-return guard (`if (IsNullOrEmpty(prefix) && IsNullOrEmpty(context))`). Benchmarks provide non-empty prefix or context.

| Benchmark | Method | Setup |
|-----------|--------|-------|
| `GetSuggestions_WordOnly` | `GetSuggestions("hel", null, 5)` | WordPredictor only, prefix only |
| `GetSuggestions_WithContext` | `GetSuggestions("", "hello", 5)` | WordPredictor only, context only |
| `GetSuggestions_WithSentenceIndex` | `GetSuggestions("How do I", null, 5)` | WordPredictor + SentenceCompletionIndex with 500 sentences |

### SmallLMBenchmarks

`SmallLMPredictor` requires three things to be available:
1. An ONNX model file (resolved via `ModelConstants.ModelFileNames` from a `modelSize` key like `"135M"`, or passed directly as `modelPath`)
2. A `LMTokenizer` instance (requires a tokenizer JSON file on disk)
3. `IsAvailable` returns `true` only when both are loaded

`Predict(context, prefix)` returns `void` — it cancels a prior CTS, allocates a result bag, and queues inference on the ThreadPool. `TryGetResults()` returns `null` while inference is still running (`_inferenceCount > 0`). To measure actual inference time, the benchmark must spin-wait (with timeout) for `TryGetResults()` to return non-null after calling `Predict()`.

Model discovery:
- Default model directory: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "models")`
- Override via environment variable `EYECTRL_MODEL_DIR`
- Construct `SmallLMPredictor` with a valid `modelSize` (e.g. `"135M"`) or `modelPath`
- Construct `LMTokenizer(tokenizerJsonPath)` with the matching tokenizer JSON
- Check `IsAvailable` in `[GlobalSetup]`; if false, skip all benchmarks

Skip mechanism: gate at the class level. If `IsAvailable` is false after `[GlobalSetup]`, construct a no-op predictor. Do not emit benchmark rows when the model is absent. Do not write to `Console` inside `[Benchmark]` bodies.

| Benchmark | Method | Measures |
|-----------|--------|----------|
| `Tokenizer_Encode` | `Encode("The quick brown fox jumps over the lazy dog")` | Tokenization latency (independent of model — needs only tokenizer JSON) |
| `Tokenizer_Decode` | `Decode(tokenIds)` | Detokenization latency |
| `PredictAndWait` | `Predict(context, null);` then spin-wait (with 5s timeout) for `TryGetResults()` non-null | End-to-end: background inference + result retrieval |

## Dependencies

No new NuGet packages. Existing `BenchmarkDotNet` 0.15.8 covers all needs. `Microsoft.ML.OnnxRuntime`, `Microsoft.Extensions.Logging`, and `Tokenizers.DotNet` (required by `SmallLMPredictor`/`LMTokenizer`) flow transitively through the `EyeCtrl.Core` project reference.

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| SmallLMPredictor benchmarks can't run without downloaded model | Check `IsAvailable` in `[GlobalSetup]`; if false, gate the class so no rows are emitted |
| Synthetic data may not reflect real-world characteristics | Use deterministic seed-based generation; document that these are throughput baselines, not real-content accuracy tests |
| Inter-benchmark state leakage | `[IterationSetup]` + `InvocationCount=1` where needed; each class runs in its own process (BenchmarkDotNet default) |
| `MinFrequency=2` filter drops learned data before benchmark runs | Setup learns each pair at least 2x; documented in the spec |

## Verification

After implementation:
```
dotnet build benchmarks/EyeCtrl.Benchmarks              # must compile 0 errors
dotnet run --project benchmarks/EyeCtrl.Benchmarks       # discovers and runs all classes
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --filter *SmallLM*  # ML benchmarks (skip if no model)
```
