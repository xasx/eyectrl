# LM Tokenizer Integration Design

**Date:** 2026-05-24
**Status:** Draft

## Overview

Replace the hardcoded 20-word stub vocabulary in `SmallLMPredictor` with a proper HuggingFace tokenizer, loaded from `tokenizer.json` downloaded alongside ONNX models.

## New dependency

Add NuGet package `HuggingFace.Tokenizers` to `EyeCtrl.Core.csproj` (official Rust-based HF tokenizers binding, supports all HF tokenizer types including BPE used by SmolLM2 and Qwen2.5).

## New class: `LMTokenizer` (`EyeCtrl.Core/Prediction/LMTokenizer.cs`)

Thin wrapper around `HuggingFace.Tokenizers.Tokenizer`. IDisposable.

```
LMTokenizer(string tokenizerJsonPath)
```

| Member | Type | Purpose |
|--------|------|---------|
| `Encode(string)` | `long[]` | Tokenize text to token IDs |
| `Decode(long[])` | `string` | Convert token IDs back to text |
| `VocabSize` | `int` (get) | Vocabulary size for logits dimension |
| `BosTokenId` | `long` (get) | Beginning-of-sequence token ID |
| `EosTokenId` | `long` (get) | End-of-sequence token ID |

**Error handling:** Constructor throws if `tokenizer.json` is missing, unreadable, or fails to load. Caller (EventCoordinator) catches and logs, disabling LM.

**IDisposable:** Disposes the underlying `HuggingFace.Tokenizers.Tokenizer`.

## Manifest changes (`ModelManifest.cs`)

`ModelEntry` gains one field:

```csharp
[JsonPropertyName("tokenizerSha256")]
public string TokenizerSha256 { get; set; } = "";
```

The tokenizer URL is inferred from the model URL by replacing the ONNX filename with `tokenizer.json`. Example:
- Model URL: `https://huggingface.co/org/model-135m/resolve/main/model.onnx`
- Tokenizer URL: `https://huggingface.co/org/model-135m/resolve/main/tokenizer.json`

## Download service changes (`ModelDownloadService.cs`)

New method:

```csharp
public async Task<bool> DownloadTokenizerAsync(
    string modelSize,
    string url,
    string expectedSha256,
    IProgress<double>? progress = null,
    CancellationToken cancellationToken = default)
```

Downloads `tokenizer.json` to `{modelsDir}/{tokenizerFileName}` where `tokenizerFileName` follows the convention `<onnx base>.tokenizer.json` (e.g., `smollm2-135m.tokenizer.json`).

Same SHA256 validation and progress reporting pattern as `DownloadModelAsync`.

`GetTokenizerPath(modelSize)` returns the expected local path.

`IsModelDownloaded(modelSize)` also checks that the tokenizer file exists alongside the ONNX file.

## `SmallLMPredictor` changes

**Constructor** gains optional `LMTokenizer? tokenizer = null` parameter. If null and model size is not "Off", initialization fails (IsAvailable = false).

**Removed:** `StubVocab` dictionary (line 222-228), `Tokenize()` method (line 230-243), `Detokenize()` method (line 246-253). `BuildPrompt()` stays — it concatenates context + prefix into the prompt string before tokenization.

**`RunInference` changes:**
- Tokenize prompt: `var inputIds = _tokenizer!.Encode(prompt).ToList();`
- Prepend BOS: `inputIds.Insert(0, _tokenizer.BosTokenId);`
- Vocab size for logits slicing: `_tokenizer.VocabSize` instead of `lastTokenLogits.Length`
- Stop generation on EOS: replace `if (bestTokenId == 0) break;` with `if (bestTokenId == _tokenizer.EosTokenId) break;`
- Decode: `var generatedText = _tokenizer.Decode(generatedTokens.ToArray());` instead of `Detokenize(generatedTokens)`

**`Dispose`** disposes `_tokenizer`.

**Score/confidence** for `LMSuggestion` results uses actual softmax probabilities from the ONNX logits:

- During greedy generation, compute softmax over the full vocab logits for the chosen token
- Store the probability of each chosen token during the generation loop
- Sequence-level confidence = geometric mean of per-token probabilities (or product, scaled by token count)
- Each `LMSuggestion` prediction gets `Score = avgProb * 100` and `Confidence = avgProb`

For multi-word suggestion results, the dominant (first/highest-probability) token's probability is used for the word. If prefix-filtering produces multiple words, each gets its own probability-derived score.

## Wiring in `EventCoordinator.cs`

In the LM predictor creation block (line ~740):

```csharp
// Existing model setup
var modelsDir = Path.Combine(_appDataDir!, "models");
var tokenizerFileName = Path.GetFileNameWithoutExtension(selectedOnnxFile) + ".tokenizer.json";
var tokenizerPath = Path.Combine(modelsDir, tokenizerFileName);

LMTokenizer? tokenizer = null;
try
{
    if (File.Exists(tokenizerPath))
        tokenizer = new LMTokenizer(tokenizerPath);
}
catch (Exception ex)
{
    Log.Warning(ex, "Failed to load tokenizer from {Path}", tokenizerPath);
}

lmPredictor = new SmallLMPredictor(modelsDir, settings.Keyboard.MLModelSize, settings.Keyboard.PreferGPU, tokenizer);
```

## Test impact

- `SmallLMPredictorTests` changes: tests that exercise tokenization need a real `LMTokenizer` with a `tokenizer.json` fixture (or a fake `LMTokenizer`).
- New `LMTokenizerTests`: test load, encode, decode, vocab size, special token IDs, and error on missing file.
- `ModelManifestTests`: add `tokenizerSha256` field to test JSON.
- `ModelDownloadServiceTests`: add download and SHA256 validation tests for tokenizer.

## Rollout

1. Ship `tokenizer.json` URLs in the model manifest alongside existing ONNX URLs.
2. Existing users who already downloaded ONNX files will need to re-download (or manually get `tokenizer.json`) — the app handles this gracefully: `SmallLMPredictor` initializes without tokenizer and `IsAvailable` returns false.
