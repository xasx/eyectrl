# Keyboard Modifier Keys Design

**Date:** 2026-04-13
**Status:** Approved

## Summary

Add Ctrl, Alt, Win, and Caps Lock modifier keys to the on-screen keyboard. Modifier keys are displayed as a left-side column across all sub-layouts and all keyboard layouts (QWERTY, AZERTY, QWERTZ). Active modifiers are visually highlighted with a solid color until the key combination completes. Shift behavior changes from semi-sticky to consistent one-shot.

## Requirements

- Support Ctrl, Alt, Win (one-shot sticky) and Caps Lock (persistent toggle)
- Modifiers are combinable (e.g., Ctrl+Shift+C)
- Modifiers highlighted with solid color when active
- Add to all sub-layouts (letters, numbers, symbols) in all keyboard layouts
- Shift also added to number/symbol sub-layouts
- Shift changes from semi-sticky to consistent one-shot behavior

## Layout

Modifier keys form a left-side column, one per row. Order top-to-bottom: Win, Caps, Alt, Ctrl. Shift remains in its current row 3 position (after Alt).

```
[Win ]  Q  W  E  R  T  Y  U  I  O  P
[Caps]  A  S  D  F  G  H  J  K  L
[Alt ]  ⇧  Z  X  C  V  B  N  M  ⌫
[Ctrl]  123 #+= 😀   Space    ⏎  ⇥
```

Numbers and symbols sub-layouts get the same left column plus Shift in row 3.

## SpecialAction Values

- `"modifier:shift"` — one-shot Shift (replaces `"shift"`)
- `"modifier:ctrl"` — one-shot Ctrl
- `"modifier:alt"` — one-shot Alt
- `"modifier:win"` — one-shot Win
- `"toggle:capslock"` — persistent toggle Caps Lock

The old `"shift"` value is handled as an alias for `"modifier:shift"` for backwards compatibility.

## Modifier State Management

### Data Model

New enum in Core:

```csharp
enum ModifierKey { Shift, Ctrl, Alt, Win, CapsLock }
```

In `KeyboardViewModel`, replace `IsShifted` boolean with `HashSet<ModifierKey> ActiveModifiers`.

`IsShifted` becomes a computed property: returns true when `ActiveModifiers` contains `Shift` or `CapsLock`.

### Activation Rules

- **One-shot modifiers** (Shift, Ctrl, Alt, Win): Dwell to activate (add to set). Dwell again to deactivate (remove from set). Multiple one-shot modifiers can be active simultaneously.
- **Toggle modifier** (CapsLock): Dwell to activate, dwell again to deactivate. Never auto-cleared.
- **After a non-modifier key fires:** All one-shot modifiers are cleared. CapsLock stays.

### What Counts as a Firing Key

- Keys that produce output or send VK codes (letters, numbers, symbols, backspace, enter, tab, space) → **clear one-shot modifiers**
- Internal actions (modifier keys, sub-layout switches, emoji toggle) → **do not clear modifiers**

This means modifiers persist across sub-layout switches: activate Ctrl → switch to numbers → press 1 → sends Ctrl+1.

## Key Combination Execution

### Simple Path (only Shift and/or CapsLock active)

Use `shiftOutput` from JSON and send via `SendText()`. Clear Shift from active set. CapsLock stays.

### VK Code Path (Ctrl, Alt, or Win active)

1. `SendKeyDown` for each active system modifier (`VK_CONTROL`, `VK_MENU`, `VK_LWIN`)
2. If Shift is also active, `SendKeyDown(VK_SHIFT)`
3. Convert the key's base output character to a virtual key code via Win32 `VkKeyScanW`
4. `SendKeyDown(vk)`, `SendKeyUp(vk)`
5. Release all modifiers in reverse order via `SendKeyUp`
6. Clear all one-shot modifiers from `ActiveModifiers`

Special keys with modifiers work naturally (e.g., Ctrl+Backspace sends `VK_CONTROL` down + `VK_BACK` down/up + `VK_CONTROL` up).

### New Platform Method

`Win32InputSimulator` gets `SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode)` to encapsulate the modifier-down → key → modifier-up sequence.

### CapsLock

CapsLock is purely a virtual state in our keyboard. It does not send `VK_CAPITAL` or toggle the system Caps Lock. It determines whether to show/use `shiftOutput` labels.

## Visual Highlighting

- **Inactive modifier:** Same dark styling as other special keys — `Color.FromArgb(200, 60, 60, 80)`
- **Active modifier:** Bright solid color — `Color.FromArgb(230, 80, 180, 255)` — a saturated accent visible at a glance, distinct from the green dwell indicator
- **Dwell in progress:** Same green interpolation as other keys. On dwell completion, snaps to active/inactive state.

All five modifiers use the same highlight color when active.

Label updates: letter keys show uppercase labels when `IsShifted` is true (Shift or CapsLock active), using existing `UpdateShiftDisplay()` logic.

## Shift Behavior Changes

Current Shift is semi-sticky (auto-deactivates after letters, stays on for symbols). This changes to **consistent one-shot**:

- Dwell on Shift → activates (highlighted)
- Press any non-modifier key → combination fires, Shift clears
- No exceptions — Shift always clears after one key
- For consecutive uppercase letters, use Caps Lock instead
- Shift combines with other modifiers (e.g., Ctrl+Shift+C)

The `"shift"` specialAction is renamed to `"modifier:shift"` in JSON. The old `"shift"` is accepted as an alias.

## Files Affected

- `src/EyeCtrl.Core/Keyboard/KeyDefinition.cs` — no structural changes (uses existing `SpecialAction`)
- `src/EyeCtrl.Core/Keyboard/ModifierKey.cs` — new enum
- `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` — replace `IsShifted` with `ActiveModifiers`, modifier activation/clearing logic
- `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` — modifier highlight rendering, handle new specialAction values
- `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs` — new `SendModifiedKeyPress` method, `VkKeyScanW` import
- `src/EyeCtrl.App/Resources/Layouts/qwerty.json` — add modifier column + Shift to all sub-layouts
- `src/EyeCtrl.App/Resources/Layouts/azerty.json` — same
- `src/EyeCtrl.App/Resources/Layouts/qwertz.json` — same
- `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` — `KeyActivatedMessage` gains modifier context
- `tests/` — update Shift tests, add modifier combination tests
