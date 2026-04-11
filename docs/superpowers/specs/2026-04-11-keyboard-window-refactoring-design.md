# KeyboardWindow Refactoring Design

## Problem

`KeyboardWindow.xaml.cs` is 917 lines with 5 intermixed responsibilities and 37+ fields. An existing `KeyboardViewModel` (91 lines) is essentially dead code — the window duplicates all its state. Future bugfixes and feature changes are harder than they need to be.

## Goal

Refactor into three focused pieces following the same pattern as `SettingsWindow` / `SettingsWindowViewModel`:
- Expanded `KeyboardViewModel` for state and logic
- New `EmojiGridManager` for emoji rendering and state
- Slimmed `KeyboardWindow` that only does rendering and gaze routing

External API unchanged (`App.xaml.cs` unaffected). This is primarily a refactor, but it also includes limited user-visible behavior/UI updates, including prediction/category tab sizing adjustments and dwell progress animations.

## Design Decisions

Agreed during brainstorming:

1. **ViewModel + extracted components** (option C): Move state/logic to ViewModel AND extract emoji mode into its own class
2. **Build on existing KeyboardViewModel** (option A): Expand the 91-line ViewModel rather than rewrite
3. **Separate rendering, shared gaze engine** (option A): EmojiGridManager writes into the same `KeyRows` panel and reuses the nearest-key dwell tracking from the ViewModel
4. **Split special key routing by domain** (option A): No central `HandleSpecialKey` — text actions in ViewModel, emoji actions in EmojiGridManager, mode-switching in ViewModel

## Component Design

### 1. KeyboardViewModel (expand from ~91 to ~200-250 lines)

**Location:** `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` (existing file)

Single source of truth for all keyboard state. Exposes observable properties so the view reacts to changes.

**State it owns:**
- Text keyboard: `IsShifted`, `CurrentInput`, `ActiveSubLayoutId`
- Mode: `IsEmojiMode` (already exists)
- Gaze: `NearestKeyId`, `NearestKeyProgress` (already exist as observable properties)
- Configuration: key dwell time, cooldown time

**Logic it owns:**
- **Nearest-key gaze tracking** — the `ProcessKeyGaze` algorithm moves here. Takes a dictionary of key screen positions + gaze point + timestamp. Updates `NearestKeyId` and `NearestKeyProgress`. Fires a callback on dwell completion.
- **Text special key handling** — direct methods: `ToggleShift()`, `HandleBackspace()`, `HandleEnter()`, `HandleSpace()`, `HandleTab()`, `SwitchSubLayout(string id)`. No string routing.
- **Key activation** — expands existing `OnKeyActivated()` with real logic: shift handling, prediction learning, messenger dispatch.
- **Prediction integration** — manages `WordPredictor` reference, current input tracking, exposes `GetSuggestions()`.
- **Cooldown tracking** — manages post-activation cooldown timer.

**What it does NOT do:**
- Create or manipulate WPF elements
- Hold references to UI panels
- Know about `EmojiGridManager`

### 2. EmojiGridManager (new, ~250-300 lines)

**Location:** `src/EyeCtrl.App/Overlays/EmojiGridManager.cs`

Owns everything emoji — extracted from KeyboardWindow, not new logic.

**Constructor takes:**
- `StackPanel keyRows` — the shared panel to render into
- `StackPanel categoryBar` — the category tab bar
- `Action<string, KeyDefinition, Border, TextBlock?> registerKeyVisual` — callback to populate the window's `_keyVisuals`, `_keyDefinitions` dictionaries (window still owns them; manager populates via callback)
- `Func<double> getKeySize` — key size accessor
- `Action clearKeyState` — callback to clear the window's `_keyVisuals`, `_keyDefinitions`, `_keyScreenPositions` dictionaries and reset `_currentNearestKeyId` (same cleanup that `BuildEmojiGrid` and `RebuildKeys` currently do inline)

**State it owns:**
- `EmojiData`, `RecentEmojiStore`
- `ActiveEmojiCategory`, `EmojiPage`, `SkinTone`
- `DwellDetector` for category tabs (owns the instance)
- Category target positions and initialization flag

**Methods:**
- `Enter()` — loads emoji data if needed, sets initial category, builds tabs + grid
- `Exit()` — resets state, hides category bar
- `BuildGrid()` — clears and rebuilds emoji grid into `keyRows`
- `BuildCategoryTabs()` — builds category tab bar
- `ShowSkinToneSelector()` — builds skin tone selection grid
- `UpdateCategoryTargets(PhysicalPoint gaze, long timestamp)` — updates category dwell detector positions and processes gaze
- `HandleAction(string action)` — handles emoji-domain special actions: `switchText`, `emojiCategory:*`, `emojiPage:*`, `skinTone`, `setSkinTone:*`, `skinToneBack`
- `ApplySkinTone(string emoji)` — applies current skin tone modifier
- `GetCategoryEmojis(string categoryId)` — returns emoji list for a category

**What it does NOT do:**
- Handle text keyboard rendering
- Know about the ViewModel directly
- Manage nearest-key gaze tracking (that stays in ViewModel)

### 3. KeyboardWindow (slim from 917 to ~250-300 lines)

**Location:** `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` (existing file)

Becomes an orchestrator and renderer.

**Keeps:**
- `RebuildKeys()` — text keyboard rendering (builds WPF elements into `KeyRows`)
- `UpdateGaze()` — public entry point, routes to ViewModel + emoji manager or prediction detector
- `UpdateKeyScreenPositions()` — collects screen positions from visuals
- `UpdateKeyVisual()` / `ResetKeyVisual()` — color blending for dwell feedback
- `GetKeyBaseColor()` — key color lookup
- `RepositionOnScreen()` — window positioning
- Prediction bar rendering (`UpdatePredictions()`, `OnPredictionDwelled()`)
- Prediction dwell detector ownership and target position updates

**Drops:**
- All emoji rendering code → `EmojiGridManager`
- All state management → `KeyboardViewModel`
- `HandleSpecialKey()` → split across ViewModel and EmojiGridManager
- `ProcessKeyGaze()` → `KeyboardViewModel`
- Direct field-based state tracking (37 fields → references to ViewModel + EmojiGridManager)

**Wiring:**
- Creates `KeyboardViewModel` and `EmojiGridManager` in constructor
- Subscribes to ViewModel property changes for visual updates (e.g., `NearestKeyProgress` changed → update key color)
- On key dwell complete (callback from ViewModel):
  - Look up `KeyDefinition` for the activated key
  - If no special action → tell ViewModel to handle regular key
  - If text special action (shift, backspace, etc.) → call ViewModel method directly
  - If emoji special action → call `EmojiGridManager.HandleAction()`
  - If mode switch → tell ViewModel, then enter/exit emoji mode
- On `IsEmojiMode` changed → toggle prediction bar / category bar visibility, call `EmojiGridManager.Enter()` or `Exit()`

## Data Flow

```
Gaze input (from App.xaml.cs)
  -> KeyboardWindow.UpdateGaze(screenPoint, timestamp)
    -> UpdateKeyScreenPositions() [view: collect positions from rendered elements]
    -> If emoji mode:
        -> EmojiGridManager.UpdateCategoryTargets(gaze, timestamp)
    -> Else:
        -> UpdatePredictionTargetPositions() + prediction dwell update
    -> KeyboardViewModel.ProcessKeyGaze(keyPositions, gazePoint, timestamp)
      -> Finds nearest key, tracks dwell time
      -> Updates NearestKeyId, NearestKeyProgress (observable)
      -> On dwell complete: fires OnKeyActivated callback with key ID
  -> KeyboardWindow reacts to ViewModel property changes:
    -> NearestKeyProgress changed -> UpdateKeyVisual(id, progress)
    -> NearestKeyId changed -> ResetKeyVisual(oldId)
    -> IsShifted changed -> UpdateShiftDisplay()
    -> IsEmojiMode changed -> toggle UI, enter/exit EmojiGridManager
```

## XAML Changes

None. The XAML is already minimal (`KeyRows`, `PredictionBar`, `CategoryBar` panels).

## Public API Preserved

These methods on `KeyboardWindow` keep the same signatures so `App.xaml.cs` doesn't change:
- `LoadLayout(KeyboardLayout, double, int, int, int)`
- `SetPredictor(WordPredictor)`
- `SetScreenEdge(string)`
- `SetEmojiData(string, string)`
- `SetSoundService(KeyboardSoundService)`
- `SetSkinTone(int)` / `GetSkinTone()` / `GetRecentStore()`
- `UpdateGaze(PhysicalPoint, long)`
- `UpdatePredictions()`

## File Summary

| File | Action | Estimated Lines |
|------|--------|-----------------|
| `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` | Expand | ~200-250 |
| `src/EyeCtrl.App/Overlays/EmojiGridManager.cs` | New | ~250-300 |
| `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` | Slim | ~250-300 |
| `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs` | Expand | Existing + new tests for gaze logic, special keys, prediction |

## Testing

- **KeyboardViewModel**: Unit test gaze tracking (nearest key detection, dwell accumulation, cooldown), special key methods (shift toggle, backspace, enter, space, sub-layout switching), key activation (shift + output, auto-unshift, prediction learning).
- **EmojiGridManager**: Logic methods are testable (page calculation, skin tone application, category lookup). Grid building creates WPF elements — harder to unit test but can be integration-tested.
- **KeyboardWindow**: Existing behavior verified by running the app — no visual changes expected.

## Risk

Low. This is a structural refactoring — all logic is preserved, just relocated. The main risk is wiring errors (callbacks not connected, property change notifications missed). Mitigated by:
- Expanding existing unit tests to cover moved logic
- Building and running the app after each component extraction
- Keeping the public API identical
