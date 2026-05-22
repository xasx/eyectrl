# ML-Enhanced Prediction Engine

## Motivation

The current prediction system (bigram + phrase + exact prefix + fuzzy edit distance) works well for short-range completions but cannot handle:
- Long sentence completions (proverbs, idioms, common phrases)
- Intelligent continuation when n-gram chains are interrupted
- Semantic understanding beyond immediate word neighbors

This design adds a dual-engine prediction architecture with a sentence completion trie and an ONNX language model, while preserving the existing bigram/phrase/fuzzy system and its gaze-error correction capabilities.

## Architecture

```
User types prefix → PredictionOrchestrator
                         ├─ [sync, <1ms] BigramModel (next-word)
                         ├─ [sync, <1ms] SentenceCompletionIndex (trie)
                         ├─ [sync, <1ms] WordPredictor (exact prefix + fuzzy)
                         │
                         └─ [async, GPU/CPU] SmallLMPredictor (ONNX + DirectML)
                              returns for slots 4-5, replaces lowest-tier results

                         → Merge by priority, deduplicate
                         → UI updates incrementally (two-phase)
```

### New Components

| Component | Role |
|-----------|------|
| `PredictionOrchestrator` | Merges results from all engines, handles async LM results, two-phase UI updates |
| `SentenceCompletionIndex` | Pre-built compact trie from text corpus; maps word prefixes to full sentence completions |
| `SmallLMPredictor` | ONNX Runtime hosting a quantized transformer model for semantic continuation |
| `CorpusBuilder` | Standalone console tool that processes raw text corpora → filtered sentences → serialized trie |

Existing `WordPredictor` becomes an internal engine behind `PredictionOrchestrator`.

## Merge Priority

Results are merged by score-weighted priority with the following ordering:

1. **NextWord** (bigram, highest confidence)
2. **SentenceCompletion** (trie, high confidence)
3. **ExactPrefix** (existing dictionary matches)
4. **FuzzyMatch** (gaze-error correction — keyboard-aware edit distance)
5. **LMSuggestion** (ONNX model, lowest priority)

Fuzzy matches rank above LM suggestions because a fuzzy match on what the user actually looked at is more reliable than an LM guessing from context alone.

### Two-Phase Display

**Phase 1** (sync, <1ms): Trie + bigram + prefix + fuzzy results populate slots 1-5. Slot 5 may show a loading indicator if LM is running.

**Phase 2** (async, 10-80ms GPU / 150-400ms CPU): LM results replace lowest-tier results in slots 4-5. Results swap in with a brief highlight.

**Deduplication**: LM results are checked case-insensitively against all existing predictions. If an LM result duplicates a trie/exact result, it is skipped (trie already scored it higher).

**Cancellation**: Each LM request gets a monotonic sequence number. On arrival, if the sequence number is behind the latest issued request (user typed more), the result is discarded.

## Sentence Completion Index

### Structure
A compact trie (`Trie<char>`) where nodes store complete sentences at leaf positions, frequency scores, and intermediate nodes know their subtree weight. Maximum depth: 15 words / ~100 chars.

### Runtime Lookup
- Walks the trie for the typed word prefix
- Returns all leaf completions under that node, sorted by frequency
- Only triggers when the current node has children; returns empty immediately otherwise
- Results typed as `PredictionType.SentenceCompletion`, scored by `log(frequency) * 3.0`

### Learning
- User selection increments frequency in user dictionary
- User can dismiss over-used completions (decrements)

## Corpus Builder Tool

Standalone console project (`tools/CorpusBuilder/`), runs at build time.

**Pipeline:**
1. Source corpora (books, Wikipedia, quotation databases) — configurable by language
2. Sentence splitting on `. ! ? ;`
3. Quality filter: min 4 words, max 15 words, <30% special characters, low digit ratio
4. Deduplication — group identical sentences, count frequency
5. Trie construction and serialization to `completions_{lang}.bin` (~5-15MB)

## Small LM Predictor (ONNX + DirectML)

### Model

Three model tiers, user-configurable. All quantized INT4, all Apache 2.0 or similarly permissively licensed.

| Tier | Model | VRAM | Disk | Multilingual | Recommended for |
|------|-------|------|------|--------------|-----------------|
| Small | SmolLM2 135M | ~200MB | ~70MB | Weak (English primarily) | Minimum, low-end machines |
| Medium (default) | SmolLM2 360M / Qwen2.5-0.5B | ~500MB | ~180MB | Moderate | General use, balanced quality |
| Large (optional) | Qwen2.5-1.5B / Phi-3.5-mini | ~1-1.5GB | ~800MB | Strong (80+ languages) | Power users, multilingual quality |

The large model is optional — not bundled by default. Users download it via the settings UI if they want it. The app detects available models at runtime and presents the choice.

### Runtime

**Execution providers** (priority order):
1. DirectML (GPU) — works with NVIDIA, AMD, Intel GPUs via Windows 10+ D3D12
2. CPU — fallback for machines without capable GPUs

**Detection at startup**: Try creating a DirectML session with a small test model. If it fails, fall back to CPU silently. Log which EP was selected.

**Latency (5-8 token generation):**

| Model | CPU | DirectML (integrated) | DirectML (discrete) |
|-------|-----|-----------------------|---------------------|
| 135M  | 150-400ms | 30-80ms | 10-40ms |
| 360M  | 400-1000ms | 80-200ms | 30-80ms |
| 1.5B  | 1-3s | 200-500ms | 80-200ms |

### Inference Flow

1. Trie returns empty or low-confidence results
2. LM inference queued on background thread (one at a time, latest wins)
3. Build prompt from last ~200 chars of typed text
4. Run encoder once, cache KV states (reused when prefix grows within same sentence)
5. Autoregressive decode until 8 tokens or EOS
6. Post-process: strip prompt, extract completion matching current prefix
7. Fire `WeakReferenceMessenger` message → UI updates predictions

### Optimizations

- **Debounce**: Only trigger on word boundaries (after space/punctuation), not every character
- **KV cache**: Reuse encoder output when prefix grows within same sentence
- **Skip trigger**: Don't run if trie already returned 5+ high-confidence results
- **Session options**: `enable_mem_pattern = true`, `graph_optimization_level = ORT_ENABLE_ALL`

### Model Download

Small and medium models (135M, 360M) are bundled with the app and pre-loaded into `%APPDATA%/EyeCtrl/models/`. The large model (1.5B) and any future models are downloaded on demand from HuggingFace.

**Settings UI flow:**
```
Settings → Prediction → Model → [Dropdown: 135M / 360M (default) / 1.5B / Off]

When user selects 1.5B:
  → Check if model file exists locally
  → If missing: show download dialog with size and ETA
    "Download Qwen2.5-1.5B (~800 MB). This model supports 80+ languages."
    [Download] [Cancel]
  → Progress bar, resumable (HTTP Range headers)
  → On completion: model loads, prediction engine restarts
```

**Hosting**: Models served from HuggingFace repos (`huggingface.co/{org}/{repo}/resolve/main/model.onnx`). The app stores a small JSON manifest mapping model tiers to URLs and expected file hashes (SHA-256), validated after download.

**File layout:**
```
%APPDATA%/EyeCtrl/models/
    smollm2-135m.onnx      (~70 MB, bundled)
    smollm2-360m.onnx      (~180 MB, bundled)
    qwen2.5-1.5b.onnx      (~800 MB, downloaded on demand)
    models.json            (manifest with URLs and hashes)
```

**On startup**: Scan `models/` directory, populate the settings dropdown with only the models actually available on disk. If a user selects a model that was later deleted, fall back to the bundled default and show a notification.

### Graceful Degradation

Runtime detection — `SmallLMPredictor` is always compiled (ONNX Runtime is a managed NuGet package, no native SDK registration needed at build time). At startup, the orchestrator attempts to load the ONNX model file and create a session. If the model file is missing or the session fails, the LM engine is disabled gracefully. App works without it; prediction bar shows fewer suggestions. Settings toggle: "AI-powered predictions" (default on if a model file is detected at startup).

### NuGet Dependency

`Microsoft.ML.OnnxRuntime.DirectML` — ONNX Runtime with DirectML execution provider.

## New PredictionType Values

```csharp
public enum PredictionType
{
    ExactPrefix,         // existing
    FuzzyMatch,          // existing
    NextWord,            // existing
    Phrase,              // existing
    SentenceCompletion,  // NEW — from trie index
    LMSuggestion,        // NEW — from ONNX LM
}
```

Existing consumers handle unknown values gracefully (default case in switch expressions or unhandled enum values).

## Settings

New `KeyboardSettings` properties:

```csharp
public bool MLSuggestionsEnabled { get; set; } = true;
public string MLModelSize { get; set; } = "360M"; // "135M" | "360M" | "1.5B" | "Off"
public bool ShowLMLoadingIndicator { get; set; } = true;
public bool PreferGPU { get; set; } = true; // DirectML vs CPU
```

## ViewModel Changes

- `KeyboardViewModel.GetSuggestionsWithContext()` becomes async-aware
- New field `_pendingLmResults` — stored separately, applied on next poll
- `PredictionOrchestrator` injected via DI instead of raw `WordPredictor`
- Same return type: `List<PredictionResult>` — just richer results

## Migration

- Existing user dictionaries (bigrams, phrases, casing preferences) are unaffected — continue to persist and learn
- Trie binary ships as a resource file alongside dictionaries, not user-mutable
- Corpus source is a build-time concern, not a runtime setting
- No breaking changes to existing `PredictionResult` consumers

## Dependencies

- `Microsoft.ML.OnnxRuntime.DirectML` for GPU-accelerated ONNX inference
- `tools/CorpusBuilder/` — new standalone console project for corpus processing
- No new external services or cloud dependencies
