# Model Download & Management UI

## Motivation

The existing `ModelDownloadService` and `ModelManifest` exist as dead code — not wired to any UI or bootstrapper. Models must be manually placed in `%APPDATA%/EyeCtrl/models/`. The hardcoded `ModelConstants` supports only 3 model sizes. Users need a dwell-safe way to:

1. Browse, download, and select from a curated list of ONNX language models
2. Add arbitrary models from any HuggingFace repo
3. Delete downloaded models (with safeguards to prevent accidents)
4. See download progress and model status at a glance

This extends and replaces the "Model Download" section of the ML prediction engine design (2026-05-22).

## Architecture

```
Resources/models.json                    (bundled manifest, curated list)
%APPDATA%/EyeCtrl/models/
    user-models.json                     (custom models added by user)
    smollm2-135m.onnx                    (downloaded/bundled model files)
    smollm2-135m.tokenizer.json
    ...
```

### New Components

| Component | Role |
|-----------|------|
| `ModelRegistry` | Data model for a single model entry (id, display name, size, status, URL, SHA256, source) |
| `ModelRegistryService` | Merges bundled manifest + user registry; CRUD for custom models; selection state |
| `Models.json` resource | Bundled manifest with curated models, URLs, and expected hashes |

### Modified Components

| Component | Change |
|-----------|--------|
| `ModelDownloadService` | Add `GetTokenizerUrl(modelUrl)` — derives the *remote* tokenizer URL from the model URL by convention. Complements the existing `GetTokenizerFileName()` (local path) and `DownloadTokenizerAsync()` (download). |
| `ModelConstants` | Replace hardcoded 3-entry dict with runtime population from `ModelRegistryService` |
| `KeyboardSettings` | Add `CustomModels` list for user-added model entries |
| `EventCoordinator` | Accept `ModelRegistryService` for model path resolution |
| `SettingsWindow` | Add "Models" tab (new `BuildModelsPages()`) |
| `SettingsViewModel` | Add Models tab definition |

## Curated Model List

Bundled in `src/EyeCtrl.App/Resources/models.json`:

| Id | Display Name | Size |
|----|-------------|------|
| 135M | SmolLM2 135M | ~70 MB |
| 360M | SmolLM2 360M | ~180 MB |
| 0.5B | Qwen2.5 0.5B | ~250 MB |
| 1.5B | Qwen2.5 1.5B | ~800 MB |
| 3.8B | Phi-3.5-mini | ~2 GB |

All Apache 2.0 or MIT. URLs point to HuggingFace repos. SHA256 hashes validate downloads **when present**; the shipped `models.json` currently leaves them empty, so downloads are trusted on HTTPS transport security alone (see Integrity note below).

### Manifest format

```json
{
    "models": {
        "135M": {
            "displayName": "SmolLM2 135M",
            "url": "https://huggingface.co/HuggingFaceTB/SmolLM2-135M-Instruct/resolve/main/onnx/model.onnx",
            "sha256": "...",
            "sizeBytes": 73400320,
            "tokenizerUrl": "https://huggingface.co/HuggingFaceTB/SmolLM2-135M-Instruct/resolve/main/onnx/tokenizer.json",
            "tokenizerSha256": "..."
        }
    }
}
```

`ModelEntry` gains `displayName`, `tokenizerUrl`, and `tokenizerSha256` fields. The `source` field ("bundled" vs "custom") is stored in the merged runtime `ModelRegistry` entry (not in the manifest JSON — bundled manifests are always `source: "bundled"`; `user-models.json` entries are always `source: "custom"`).

**Integrity:** The `sha256` / `tokenizerSha256` in the manifest are verified post-download **only when non-empty**. An empty value skips verification by design — required for custom models (hash unknown ahead of time) and accepted for bundled models whose manifest omits a hash. The currently shipped `models.json` leaves all hashes empty, so today every download relies on HTTPS transport security; populate the manifest hashes to enable end-to-end content verification.

## Models Tab Layout

Added as a 9th tab in `SettingsWindow.xaml.cs` tab definitions (`("Models", "⬡", "Models", BuildModelsPages)`).

### Page 1: Model management

```
┌──────────────────────────────────────────────────────┐
│                                                      │
│  Active Model                                        │
│  [◀ SmolLM2 360M ▶  ✓]           ← debounced select │
│                                                      │
│  ────────────────────────────────────────────────    │
│                                                      │
│  SmolLM2 135M          70 MB      [Download]         │
│  Qwen2.5 0.5B         250 MB      [Download]         │
│  Qwen2.5 1.5B         800 MB      [██████░░] ✕      │
│  Phi-3.5-mini           2 GB      [Download]         │
│                                                      │
│              [＋ Add Custom Model]                    │
│                                                      │
│  ────────────────────────────────────────────────    │
│                                                      │
│         ← [1/2] →          [Manage models...]        │
└──────────────────────────────────────────────────────┘
```

**Active model combo** cycles only through *downloaded* models (files present on disk). Undownloaded models are not selectable — they must be downloaded first. If no models are downloaded, the combo is replaced with "No models downloaded. Download one below." and the active model defaults to "Off" (ML suggestions disabled).

### Page 2: Manage downloaded models

Only appears when at least one model has its file downloaded (curated or custom) and is not the active model. The "Manage models..." button on page 1 is **hidden** when there are no models to manage (all downloaded models are active, or none are downloaded). When shown but the list is empty, page 2 displays "No models to manage."

```
┌──────────────────────────────────────────────────────┐
│                                                      │
│  Manage Downloaded Models                            │
│                                                      │
│  SmolLM2 135M          70 MB      [🗑 Delete]        │
│  Qwen2.5 0.5B         250 MB      [🗑 Delete]        │
│  My Qwen 7B              4 GB      [🗑 Delete]        │
│                                                      │
│  ────────────────────────────────────────────────    │
│                                                      │
│         ← [2/2] →          [Back to models]          │
└──────────────────────────────────────────────────────┘
```

Delete buttons use an amber/orange color (not red — red signals imminent danger in gaze UX).

### Dwell Safety Rules

1. **No delete on the main list.** Delete is only on a separate "manage" page, reached by dwelling "Manage models...".
2. **Confirmation modal for every delete.** Deleting a model triggers `ShowModal("Delete {name}? This cannot be undone.", onYes)`.
3. **Active model cannot be deleted.** It is excluded from the manage page. If the user wants to delete the active model, they must first switch to another model on page 1.
4. **Curated model entries persist.** Curated models (from the bundled manifest) *appear* on the manage page when downloaded, and their files can be deleted to free disk space. The entry remains in the curated list and reverts to showing "Download" — the user can re-download later. Custom models are fully removed (entry + file) since they have no manifest entry to fall back to.
5. **Download is safe.** Downloading a model does not switch to it — it just adds the file. The user must explicitly select it from the active model combo.

## Debounced Model Loading

Dwelling on the active model combo changes the selection instantly (visual feedback), but model loading is debounced:

```
User cycles: 360M → 135M → 0.5B → 1.5B
                                     ↑
                             2-second debounce
                                     ↓
                            Load Qwen2.5-1.5B
```

**States:**

| Combo row indicator | Meaning |
|---------------------|---------|
| `✓` (green check) | Model loaded and ready |
| `○` (pulsing dot) | Waiting for debounce (user may still be cycling) |
| `⟳` (spinning) | Loading in progress |

The debounce delay is 2 seconds, stored as a constant in `ModelRegistryService`. If the user cycles again before the debounce fires, the timer resets. Failure to load shows `⚠` and reverts to the previously loaded model. If no previous model exists (first launch, or previous model was deleted), the app enters `IsAvailable = false` — LM predictions are disabled — and the combo shows `⚠` "No model loaded. Select another model."

## Custom Model Flow

### Add Custom Model modal

Dwelling "＋ Add Custom Model" opens a modal:

```
┌─ Add Custom Model ───────────────────────────┐
│                                                │
│  Display Name: [___________________________]   │
│                                                │
│  Repo URL:     [___________________________]   │
│                         [ 📋 Paste from URL ]  │
│                                                │
│  The tokenizer will be downloaded              │
│  automatically from the same repo.             │
│                                                │
│            [ ✓ Download ]  [ ✕ Cancel ]        │
└────────────────────────────────────────────────┘
```

### Text entry

The two fields are dwell-activated. Dwelling on a field shows the keyboard overlay for text input (see Keyboard Integration below). The fields are standard WPF `TextBox` elements with `IsReadOnly=true` — text is entered via the keyboard overlay's key events directed at the focused field.

### Paste button

Dwelling "📋 Paste from URL" reads the clipboard, populates the URL field, and auto-fills the Display Name if empty.

**Auto-name derivation:**

```
URL:  https://huggingface.co/HuggingFaceTB/SmolLM2-135M-Instruct/resolve/main/model.onnx
      └─ org ─┘└────────── repo ───────────┘└───── path ──────────────────────────┘

Derived name: "SmolLM2-135M-Instruct"
Rule: extract the second-to-last path segment before "/resolve/"
```

The derived name is editable — dwell on Display Name to modify it.

### Validation

- Both fields must be non-empty
- URL must start with `https://`
- Query parameters (`?download=true`) are stripped before validation
- The URL path after stripping query params must end with `.onnx`
- If the URL cannot be parsed as a valid URI, show "Invalid URL format"
- Errors show inline in the modal (modal stays open so user can fix)

### Filename Sanitization

Custom model files are saved to `%APPDATA%/EyeCtrl/models/{sanitized-name}.onnx`. Sanitization rules:

1. Replace any character that is not `[A-Za-z0-9._-]` with `_`
2. Collapse multiple consecutive `_` into a single `_`
3. Trim leading/trailing `_` and `.`
4. If the result is empty or only `.`, fall back to `custom_model`
5. **Collision check:** If the sanitized filename already exists and belongs to the *same* model (same URL), the existing file is reused — the download overwrites it. If it belongs to a *different* model (different URL), append `_2`, `_3`, etc. until unique.
6. Limits: max 64 characters for the sanitized name (before extension).

### user-models.json Format

Custom models added by the user are persisted in `%APPDATA%/EyeCtrl/models/user-models.json`:

```json
{
    "models": {
        "My_Qwen_7B": {
            "displayName": "My Qwen 7B",
            "url": "https://huggingface.co/org/repo/resolve/main/model.onnx",
            "sha256": "abc123...",
            "sizeBytes": 4294967296,
            "tokenizerUrl": "https://huggingface.co/org/repo/resolve/main/tokenizer.json",
            "tokenizerSha256": "def456..."
        }
    }
}
```

The key is the unique model ID (sanitized display name). All fields mirror the bundled manifest's `ModelEntry` structure plus `source: "custom"` at runtime.

### Orphaned Temp Files

On app startup, `ModelRegistryService` scans the models directory for any `*.download` temp files left over from a previous crash or unexpected exit. Orphaned temp files are deleted. This prevents stale partial downloads from accumulating disk space.

### Download

**Pre-download checks:**
1. Available disk space on the `models/` directory's drive is checked against the model's `sizeBytes` (from manifest for curated, from HTTP `Content-Length` header for custom). Minimum free space required: `sizeBytes * 1.2` (20% margin).
2. If the HTTP response omits `Content-Length` (chunked transfer), the disk space check is skipped with a warning log. Insufficient space will surface as a runtime error during download.
3. If space is insufficient, the download is blocked with: "Not enough disk space. Free up at least {required} and try again." The modal stays open.

**Download flow:**
On confirm:
1. Model downloaded to `%APPDATA%/EyeCtrl/models/{sanitized-name}.onnx`
2. Tokenizer downloaded to same directory as `{sanitized-name}.tokenizer.json`
3. SHA256 computed post-download and stored in `user-models.json`
4. Entry added to `user-models.json`: `{ id, displayName, url, sha256, sizeBytes, source: "custom" }`
5. After download, modal closes, the Models tab refreshes

**Progress:** During download the action button is replaced with a progress bar and a cancel button (✕). This applies to both curated model downloads (page 1) and custom model downloads (modal). Canceling deletes the partial `.download` temp file.

**Concurrency:** Only one download runs at a time. While any download is active, all other "Download" buttons (curated and custom) are disabled and grayed out. The user must wait for the current download to complete or cancel it before starting another.

### Error handling

| Error | UX |
|-------|-----|
| Insufficient disk space | "Not enough disk space. Free up at least {required} and try again." Modal stays open. |
| Network failure | "Download failed: could not connect. Check the URL and try again." Modal stays open. |
| Hash mismatch (curated only) | "File integrity check failed. The download may be corrupted." Retry or cancel. |
| Invalid ONNX | Detected when user tries to activate the model. "This model could not be loaded. It may not be a valid ONNX file." |

## Keyboard Integration

When a text field in the modal receives focus (user dwells on the field), the keyboard overlay appears. When the modal closes, the keyboard hides.

This uses the existing keyboard lifecycle managed by `EventCoordinator`. A new message type orchestrates this:

```
Dwell on text field
    → settings window sends ShowKeyboardRequestMessage
    → EventCoordinator shows keyboard, redirects key events to the active text field
    → User dwells on keyboard keys to type
    → Modal close → settings window sends HideKeyboardRequestMessage
```

The keyboard position is determined by available screen space — it docks to the edge opposite the settings window to avoid overlap.

## Tokenizer URL Convention

### Curated models

Tokenizer URLs are explicitly specified in the bundled `models.json` manifest alongside the model URL and SHA256. The manifest MUST include a `tokenizerUrl` field for every curated model. This avoids fragile filename derivation for curated entries.

### Custom models

The custom model modal auto-derives the tokenizer URL from the model URL by filename replacement:

```
Model URL:      .../repo/resolve/main/onnx/model.onnx
Tokenizer URL:  .../repo/resolve/main/onnx/tokenizer.json
```

Rule: replace the last path segment's filename (`model.onnx`) with `tokenizer.json`.

If the derived URL returns a 404 or the download fails, the user is prompted in a follow-up modal:

```
┌─ Tokenizer Not Found ─────────────────────┐
│                                             │
│  The tokenizer could not be downloaded      │
│  automatically.                             │
│                                             │
│  Tokenizer URL:  [______________________]   │
│                         [ 📋 Paste ]        │
│                                             │
│          [ Download ]  [ ✕ Cancel ]         │
└─────────────────────────────────────────────┘
```

The user must provide a valid `tokenizer.json` URL or cancel. Canceling aborts the entire custom model add — no partial entry is created. The `SmallLMPredictor` requires a tokenizer; models without one are not useful.

### Existing curated model entries gain `tokenizerUrl`

`ModelEntry` gains a `tokenizerUrl` field (and `tokenizerSha256` to validate it). `user-models.json` entries also store `tokenizerUrl`.

## Backward Compatibility

- Existing `ModelConstants.cs` mapping is replaced by `ModelRegistryService.GetModelFileName()` which looks up the merged registry
- Existing `SmallLMPredictor` still works — it receives a models directory and model size string; the only change is that the size string may now be a custom model ID
- `user-models.json` is a new file; absence is treated as empty (no custom models)
- The bundled `models.json` manifest is a new resource file; previous behavior required manual model placement — this is additive, not breaking

## Testing

### Unit tests (EyeCtrl.Core.Tests)

- `ModelRegistryServiceTests`: merge bundled + user registries, add/remove custom models, filename derivation
- `ModelDownloadServiceTests`: existing tests extended for `GetTokenizerUrl()` derivation
- `ModelManifestTests`: existing tests extended for `displayName` field

### ViewModel tests (EyeCtrl.ViewModels.Tests)

- Models tab page builder produces correct page structure
- Debounce timer resets on rapid cycling
- Custom model validation rules

### UI tests (EyeCtrl.App.Tests)

- Models tab appears in settings window (8→9 tabs)
- Dwell on combo cycles models
- Dwell on "Manage models..." navigates to manage page
- Delete requires confirmation modal
- Active model excluded from delete list

## Dependencies

No new NuGet packages. Existing: `Microsoft.ML.OnnxRuntime.DirectML`, `System.Text.Json`, `CommunityToolkit.Mvvm`.
