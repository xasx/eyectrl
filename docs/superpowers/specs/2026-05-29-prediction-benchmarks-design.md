# Prediction Pipeline Benchmarks — Design Spec

**Date:** 2026-05-29
**Status:** Draft

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
| `BigramPhraseBenchmarks.cs` | BigramModel, PhraseModel | 6 |
| `SentenceCompletionBenchmarks.cs` | SentenceCompletionIndex | 4 |
| `FuzzyEditBenchmarks.cs` | FuzzyEditDistance | 5 |
| `CasingBenchmarks.cs` | CapitalizationHelper, CasingHelper, CasingPreferenceTracker | 5 |
| `OrchestratorBenchmarks.cs` | PredictionOrchestrator | 4 |
| `SmallLMBenchmarks.cs` | SmallLMPredictor, LMTokenizer | 5 |

**Total: 34 benchmarks across 7 files**

### Program.cs Change

Replace hardcoded `BenchmarkRunner.Run<PredictionBenchmarks>(args)` with `BenchmarkSwitcher.Run(args)` so all `[Benchmark]`-decorated classes in the assembly are auto-discovered. Users can still filter via CLI: `--filter *SmallLM*`.

### Shared Pattern

Every benchmark file follows the same conventions as the existing `PredictionBenchmarks.cs`:

```csharp
[SimpleJob(RuntimeMoniker.Net90)]
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

- `[SimpleJob(RuntimeMoniker.Net90)]` — targets .NET 10
- `[MemoryDiagnoser]` — measures GC allocations
- `[RankColumn]` — ranks benchmarks within the class
- Private nested fake classes for dependencies (matching existing `InMemoryDictionaryProvider` pattern)
- No mocking library — consistent with the repo's testing conventions

## Detailed Scenarios

### BigramPhraseBenchmarks

Synthetic setup: 2000 bigrams learned from 500 unique word pairs, 1500 three-word phrases from 1000 sequences.

| Benchmark | Method | Dataset | Measures |
|-----------|--------|---------|----------|
| `Bigram_GetNextWords_Cold` | `GetNextWords()` | 500 bigrams, context="hello" | First-call latency |
| `Bigram_GetNextWords_Warm` | `GetNextWords()` | Same, after all cached | Steady-state latency |
| `Bigram_ApplyDecay_Large` | `ApplyDecay(true, 30)` | 2000 bigrams, halfLifeDays=30 | Decay scan throughput |
| `Phrase_GetPhrases_ShortPrefix` | `GetPhrases("hel", 5)` | 1500 phrases | Short prefix search |
| `Phrase_GetPhrases_LongPrefix` | `GetPhrases("application", 5)` | Same | Long prefix search |
| `Phrase_ApplyDecay_Large` | `ApplyDecay(true, 30)` | 1500 phrases | Decay scan throughput |

### SentenceCompletionBenchmarks

Synthetic setup: 5000 sentences generated programmatically (combinations of common words, e.g., "the quick brown fox..."). Data regenerated in `[IterationSetup]` for `Insert_5000Sentences` to avoid cross-benchmark state leakage.

| Benchmark | Method | Measures |
|-----------|--------|----------|
| `Insert_5000Sentences` | `Insert(sentence, freq)` loop | Bulk insert throughput |
| `GetCompletions_ShortPrefix` | `GetCompletions("How do I", 5)` | Prefix matching on 5000 sentences |
| `GetCompletions_NoMatch` | `GetCompletions("xyznonexistent", 5)` | Worst-case trie traversal |
| `BoostAndDismiss` | `BoostCompletion()` + `DismissCompletion()` | Mutable operations overhead |

### FuzzyEditBenchmarks

No setup needed. Static method with inline parameters. Input sizes range from 3 to 13 chars to benchmark the O(m*n) DP table at realistic typo lengths.

| Benchmark | Input A | Input B | Measures |
|-----------|---------|---------|----------|
| `Compute_ShortStrings` | "hel" | "hlo" | Typical typo (adjacent key) |
| `Compute_MediumStrings` | "application" | "applucation" | 12-char with typo |
| `Compute_LongStrings` | "extraordinary" | "extraordinery" | 13-char with double typo |
| `Compute_Identical` | "identical" | "identical" | Best case (early exit) |
| `Compute_CompletelyDifferent` | "abcdefghij" | "klmnopqrst" | Worst case (all chars different) |

### CasingBenchmarks

| Benchmark | Method | Setup |
|-----------|--------|-------|
| `ShouldCapitalize_NoAbbrev` | `ShouldCapitalize(text)` | Varied input: ". end", "hello", "Dr. Smith", "" |
| `ShouldCapitalize_WithAbbrev` | `ShouldCapitalize(text, abbrevs)` | Same input with abbreviation set {"Dr", "Mr", "Ms"} |
| `ApplySmartCapitalization` | `ApplySmartCapitalization(word, text, abbrevs)` | word="hello", text="What is " |
| `CasingPref_RecordAndGet` | `RecordCasing()` + `GetPreferredCasing()` | 100 casing variants across 20 words |
| `CasingHelper_IsAllCaps` | `IsAllCaps(text)` | Mixed cases: "HELLO", "Hello", "HELLO WORLD", "hElLo" |

### OrchestratorBenchmarks

Uses same `InMemoryDictionaryProvider` with 10K words as existing benchmarks. No ML component (SmallLMPredictor is null).

| Benchmark | Method | Setup |
|-----------|--------|-------|
| `GetSuggestions_WordOnly_EmptyPrefix` | `GetSuggestions("", null, 5)` | WordPredictor only, no context |
| `GetSuggestions_WordOnly_WithContext` | `GetSuggestions("", "hello", 5)` | WordPredictor only, with context |
| `GetSuggestions_WithSentenceIndex` | `GetSuggestions("How ", null, 5)` | WordPredictor + SentenceCompletionIndex with 500 sentences |
| `LearnSequence_Orchestrator` | `LearnSequence(words)` | Passthrough to WordPredictor.LearnSequence |

### SmallLMBenchmarks

Reads model directory from environment variable `EYECTRL_MODEL_DIR` (default: `%APPDATA%/EyeCtrl/models`). If no `.onnx` + `.tokenizer.json` pair is found, sets `_skip = true` and all benchmarks return immediately.

| Benchmark | Method | Measures |
|-----------|--------|----------|
| `Predict_Cold` | First `Predict(context, prefix)` | Model session creation + first inference |
| `Predict_Warm` | Subsequent `Predict()` calls | Cached session inference |
| `TryGetResults_Polling` | `TryGetResults()` after Predict | Result polling overhead |
| `Tokenizer_Encode` | `Encode("The quick brown fox")` | Tokenization latency |
| `Tokenizer_Decode` | `Decode(tokenIds)` | Detokenization latency |

If `_skip == true`, benchmarks return without executing and log a warning via `Console.WriteLine`.

## Dependencies

No new NuGet packages. Existing `BenchmarkDotNet` 0.15.8 covers all needs. No additional project references needed (already references `EyeCtrl.Core`).

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| SmallLMPredictor benchmarks can't run without downloaded model | Graceful skip with clear console message; env var for path override |
| Synthetic data may not reflect real-world characteristics | Use deterministic seed-based generation; document that these are throughput baselines, not real-content accuracy tests |
| Inter-benchmark state leakage | `[IterationSetup]` where needed; each class runs in its own process (BenchmarkDotNet default) |

## Verification

After implementation:
```bash
dotnet build benchmarks/EyeCtrl.Benchmarks              # must compile 0 errors
dotnet run --project benchmarks/EyeCtrl.Benchmarks       # discovers and runs all classes
dotnet run --project benchmarks/EyeCtrl.Benchmarks -- --filter *SmallLM*  # runs ML benchmarks (skips gracefully if no model)
```
