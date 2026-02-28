# Emoji Keyboard Design

## Overview

Add an emoji keyboard to EyeCtrl, accessible via a 😀 button on the regular keyboard's bottom row. Features 8 standard categories, a recently-used section, and global skin tone selection — all optimized for eye-tracking dwell interaction and memory efficiency.

## Layout & Navigation

The regular keyboard's bottom row gains a `😀` switchSub button. Dwelling it enters emoji mode. An `ABC` button in emoji mode returns to the text keyboard.

The emoji view has two zones inside the existing `KeyboardWindow`:

```
┌──────────────────────────────────────────────────┐
│ 🕐  😀  👋  🐾  🍕  ⚽  🌍  💡  ❤️   🎨 │  ← category tabs
├──────────────────────────────────────────────────┤
│                                                  │
│  😀 😃 😄 😁 😆 😅 🤣 😂 🙂 😉              │  ← emoji grid
│  😊 😇 🥰 😍 🤩 😘 😗 ☺️ 😚 😙              │    (nearest-key
│  🥲 😋 😛 😜 🤪 😝 🤑 🤗 🤭 🫢              │     dwell, same
│  🫣 🤫 🤔 🫡 🤐 🤨 😐 😑 😶 🫥              │     as text keys)
│                                                  │
│                 [ ABC ]  [ 🎨 ]                  │  ← back + skin tone
└──────────────────────────────────────────────────┘
```

### Category Tabs (top row)

Dwellable buttons using the existing DwellDetector (circular targets, few items):

| Icon | Category | ~Count |
|------|----------|--------|
| 🕐 | Recently Used | up to 40 |
| 😀 | Smileys & Emotion | ~80 |
| 👋 | People & Body | ~60 |
| 🐾 | Animals & Nature | ~50 |
| 🍕 | Food & Drink | ~50 |
| ⚽ | Activities | ~40 |
| 🌍 | Travel & Places | ~50 |
| 💡 | Objects | ~50 |
| ❤️ | Symbols | ~50 |

The active category tab is highlighted (brighter background). The 🎨 button at the end opens the skin tone selector.

### Emoji Grid (center)

10 columns × 4 rows = 40 emojis per page. Uses the existing nearest-key dwell approach — every point on the grid maps to the nearest emoji center, no dead zones.

When a category has more than 40 emojis, pagination arrows (◀ ▶) appear at the edges of the grid. Dwelling them moves to the next/previous page.

### Bottom Row

- **ABC** button: returns to the text keyboard (same as existing switchSub mechanism)
- **🎨** button: opens skin tone selector

## Skin Tone Selection

A global skin tone preference for simplicity with eye tracking:

1. Dwell the 🎨 button
2. The emoji grid is temporarily replaced with 6 large options: 👋 👋🏻 👋🏼 👋🏽 👋🏾 👋🏿
3. Dwell your choice — it becomes the default for all skin-tone-capable emojis
4. The grid returns with emojis rendered in the chosen tone
5. Preference persisted in the existing `settings.json` as `Keyboard.EmojiSkinTone` (0-5, where 0 = default yellow)

At render time, if an emoji is in the `skinToneSupported` set and the user has a non-zero skin tone preference, the skin tone modifier character (U+1F3FB through U+1F3FF) is appended to the base emoji.

## Data Architecture

### `Resources/Emojis/emojis.json` (~80KB)

```json
{
  "categories": [
    {
      "id": "smileys",
      "icon": "😀",
      "label": "Smileys & Emotion",
      "emojis": ["😀", "😃", "😄", "😁", "😆", ...]
    },
    ...
  ],
  "skinToneBase": ["👋", "👍", "✊", "👏", "🤝", ...]
}
```

- `categories`: ordered list with id, icon, label, and emoji array
- `skinToneBase`: set of base emojis that support skin tone modifiers. Stored once as base characters; the modifier is appended at render time.

### Memory Model

- **Not loaded at startup**. The JSON is parsed only when the user first enters emoji mode.
- Once loaded, the parsed data is a list of string arrays — lightweight.
- Only **one page** (~40) of WPF `Border`/`TextBlock` controls exists at a time.
- Switching categories or pages clears `KeyRows.Children` and rebuilds — same pattern as `RebuildKeys()`.
- Category tabs (~10 controls) persist while in emoji mode.

### Recently Used (`AppData/EyeCtrl/recent_emojis.json`)

```json
["😂", "❤️", "👍", "🎉", ...]
```

- Stores up to 40 base emojis (without skin tone — tone applied at render)
- New selections are prepended; duplicates are moved to front
- Loaded on first emoji access, saved after each selection
- Shown as the 🕐 category tab (first tab, default when entering emoji mode)

## Integration with Existing KeyboardWindow

The emoji view is **not** a new window. It replaces the content inside `KeyboardWindow`:

- `KeyRows.Children` is cleared and rebuilt with emoji grid (same as `RebuildKeys()`)
- `PredictionBar` is hidden in emoji mode (word prediction doesn't apply)
- Category tabs are added to a new `StackPanel` or reuse `PredictionBar` position
- The existing nearest-key dwell tracking (`ProcessKeyGaze`) works unchanged for the emoji grid
- `KeyActivated` fires with the emoji string (e.g. `"👋🏽"`) — `App.xaml.cs` sends it via `_inputSimulator.SendText(text)`

### New State in KeyboardWindow

- `_isEmojiMode: bool` — toggles between text keyboard and emoji view
- `_emojiData: EmojiData?` — lazy-loaded, null until first use
- `_activeEmojiCategory: string` — current category id
- `_emojiPage: int` — current page within category
- `_recentEmojis: List<string>` — recently used, loaded from disk

### New Special Actions

- `switchEmoji` — enters emoji mode (on the 😀 button)
- `switchText` — returns to text keyboard (on the ABC button)
- `emojiCategory:<id>` — switches to a category
- `emojiPage:next` / `emojiPage:prev` — pagination
- `skinTone` — opens skin tone selector

### Modified Keyboard Layout JSON

Each layout (QWERTY, QWERTZ, AZERTY) gets the 😀 button added to its bottom row:

```json
{ "label": "😀", "output": "", "width": 1.0, "specialAction": "switchEmoji" }
```

## Files Changed/Added

### New Files
- `src/EyeCtrl.Core/Keyboard/EmojiData.cs` — model for parsed emoji data
- `src/EyeCtrl.Core/Keyboard/EmojiDataLoader.cs` — lazy JSON loader
- `src/EyeCtrl.Core/Keyboard/RecentEmojiStore.cs` — recent list persistence
- `src/EyeCtrl.App/Resources/Emojis/emojis.json` — curated emoji data

### Modified Files
- `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` — emoji mode rendering, category tabs, skin tone selector
- `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml` — may need additional named elements for category bar
- `src/EyeCtrl.Core/Settings/KeyboardSettings.cs` — add `EmojiSkinTone` property
- `src/EyeCtrl.App/Resources/Layouts/qwerty.json` — add 😀 button
- `src/EyeCtrl.App/Resources/Layouts/qwertz.json` — add 😀 button
- `src/EyeCtrl.App/Resources/Layouts/azerty.json` — add 😀 button
- `src/EyeCtrl.App/EyeCtrl.App.csproj` — include emojis.json as content
