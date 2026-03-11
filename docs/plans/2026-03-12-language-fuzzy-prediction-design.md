# Language Support & Fuzzy Word Matching Design

**Date:** 2026-03-12
**Status:** Approved

## Goals

1. Add multi-language dictionary support (en, fr, es, de, pl) with per-language user dictionaries
2. Replace exact prefix matching with a keyboard-aware fuzzy matcher that tolerates single-key gaze errors
3. Auto-detect language on first launch; make language configurable via the Keyboard settings page using dwellable radio buttons

## New Files

### `EyeCtrl.Core/Prediction/KeyboardAdjacencyMap.cs`

Static dictionary mapping each key character to its physically adjacent neighbors on the keyboard. Three variants: QWERTY, QWERTZ, AZERTY. Selected by layout name at `WordPredictor` construction time. Adjacent-key substitutions cost 0.5 in the edit distance function; all others cost 1.0.

### `EyeCtrl.Core/Prediction/FuzzyEditDistance.cs`

Keyboard-aware Levenshtein distance. Standard DP algorithm with one modification: substituting character `x` for `y` costs 0.5 if `y ∈ KeyboardAdjacencyMap[x]`, else 1.0. Insertions and deletions always cost 1.0. Returns `double`.

### `EyeCtrl.Core/Prediction/LanguageDetector.cs`

Static `Detect()` method. Reads `CultureInfo.CurrentUICulture`, maps the two-letter ISO language code to a supported pack code (`en`, `fr`, `es`, `de`, `pl`). Falls back to `"en"` if the culture is not in the supported set.

### Dictionary assets

`Resources/Dictionaries/fr.json`, `es.json`, `de.json`, `pl.json` — same `{ "word": frequency }` format as the existing `en.json`, top ~5000 words per language sourced from public-domain frequency lists. All four files added to the `.csproj` with `CopyToOutputDirectory = Always`.

## Modified Files

### `EyeCtrl.Core/Prediction/WordPredictor.cs`

`GetSuggestions(string prefix, int maxCount)` replaces the current `StartsWith`-only pass with a two-category linear scan:

**Pass 1 — Exact prefix:** collect all words where `word.StartsWith(prefix, OrdinalIgnoreCase)`. Each scores `frequency × 3.0`.

**Pass 2 — Fuzzy prefix:** for every remaining word (not already in Pass 1):
- If `word.Length >= prefix.Length`: compute `FuzzyEditDistance(prefix, word[..prefix.Length])`
- If `word.Length < prefix.Length`: compute `FuzzyEditDistance(prefix, word)`
- Accept if distance ≤ 1.5. Score multiplier:
  - Distance ≤ 0.5 → ×1.5
  - Distance ≤ 1.0 → ×1.2
  - Distance ≤ 1.5 → ×1.0

All candidates merge and sort descending by score. Top `maxCount` returned.

Constructor accepts a `string layoutName` parameter so it can select the correct adjacency map.

### `EyeCtrl.Core/Prediction/FileDictionaryProvider.cs`

Constructor gains a `string languageCode` parameter. User dictionary path becomes `user_dictionary_{languageCode}.json`. Base dictionary path continues to be passed in from the caller (no change to how it is derived).

### `EyeCtrl.Core/Settings/KeyboardSettings.cs`

`_languagePack` default changes from `"en"` to `""` (empty string = auto-detect on first launch).

### `EyeCtrl.App/App.xaml.cs` — `LoadKeyboard`

Before constructing `FileDictionaryProvider`:
1. If `settings.Keyboard.LanguagePack` is empty, call `LanguageDetector.Detect()`, assign the result to `settings.Keyboard.LanguagePack`, and call `settingsService.SaveDebounced(settings)` so the detected language persists.
2. Pass `settings.Keyboard.LanguagePack` to `FileDictionaryProvider`.
3. Pass `settings.Keyboard.ActiveLayout` to `WordPredictor` constructor for adjacency map selection.

### `EyeCtrl.App/Views/SettingsWindow.xaml.cs` — `BuildKeyboardPages`

Add `AddRadio("k_lang", "Dictionary Language", ...)` between the Word Prediction toggle and the Click Sound toggle.

Add a private `AddRadio` helper method:

```
AddRadio(string idPrefix, string label,
         string[] displayOptions, string[] codeOptions,
         Func<string> getCode, Action<string> setCode)
```

Renders a label followed by one dwellable `Border` per option. Each border is registered as a `DwellTarget` in `_controlDwell`. Selected option background: `Color.FromArgb(200, 30, 100, 30)` (green). Unselected: `BtnBase`. Activating an option calls `setCode` and refreshes all radio visuals in the group.

Display options: `["English", "French", "Spanish", "German", "Polish"]`
Code options: `["en", "fr", "es", "de", "pl"]`

Reset Keyboard Settings resets `LanguagePack` to `""` (re-triggers auto-detect on next load).

The "Reset ALL Settings" block in `BuildGeneralPages` is updated to also reset `LanguagePack` to `""`.

## Language Lifecycle

```
Startup
  └─ LanguagePack == ""?
       ├─ Yes → LanguageDetector.Detect() → set + save
       └─ No  → use stored value
  └─ LoadKeyboard(languagePack)
       └─ FileDictionaryProvider(baseDictPath, userDictPath_{lang}, lang)
       └─ WordPredictor(provider, layoutName)

Settings change (language)
  └─ ApplyAndSave → SettingsChanged → LoadKeyboard (rebuilds predictor)

App exit
  └─ _wordPredictor.SaveUserDictionary() → writes user_dictionary_{lang}.json
```

## Out of Scope

- Runtime language switching mid-word (current word buffer resets on language change, same as layout change)
- Phonetic / sounds-alike matching
- Downloading dictionaries at runtime
- More than 5000 words per language in the base dictionary
