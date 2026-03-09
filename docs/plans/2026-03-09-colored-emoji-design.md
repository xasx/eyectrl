# Colored Emoji Rendering Design

**Date:** 2026-03-09
**Feature:** Color emoji in the emoji keyboard using Windows' Segoe UI Emoji font

## Problem

The emoji keyboard currently renders emoji as monochrome. WPF's `TextBlock` renders text using glyph outlines and applies the `Foreground` brush uniformly. Even with Segoe UI Emoji loaded, WPF does not honor COLR color layers — so `Foreground = Brushes.White` produces white silhouettes.

## Approach

Use GDI (`System.Windows.Forms.TextRenderer.DrawText`) to render each emoji from Segoe UI Emoji onto a `System.Drawing.Bitmap`, convert to a frozen WPF `BitmapSource`, and cache it. GDI on Windows 10+ renders Segoe UI Emoji COLR glyphs in full color. The project already sets `UseWindowsForms=true` so no new dependencies are needed.

A quick Option B validation step (set `FontFamily="Segoe UI Emoji"` + remove `Foreground=White` on TextBlocks) is tried first. If still monochrome, proceed with the GDI service.

## Scope

- Category tab icons are **not** changed — small navigation symbols, monochrome is fine.
- Changed: emoji grid cells (`BuildEmojiGrid`) and skin tone selector (`ShowSkinToneSelector`).

## Components

### `EmojiRenderService` (new, `EyeCtrl.App/`)

```csharp
internal sealed class EmojiRenderService : IDisposable
{
    private readonly Dictionary<(string emoji, int size), BitmapSource> _cache = new();
    private readonly System.Drawing.Font _font48;
    private readonly System.Drawing.Font _font60;
    private bool _available = true;

    public bool IsAvailable => _available;

    public BitmapSource Get(string emoji, int size);
    private BitmapSource Render(string emoji, int size);
    public void Dispose();
}
```

**Rendering pipeline (`Render`):**
1. `new Bitmap(size, size, PixelFormat.Format32bppArgb)` with transparent fill
2. `TextRenderer.DrawText(g, emoji, font, rect, Color.White, Color.Transparent, HCenter | VCenter | NoPrefix)`
3. `bitmap.GetHbitmap(Color.Transparent)` → `Imaging.CreateBitmapSourceFromHBitmap()` → `DeleteObject(hBitmap)` in `finally`
4. `bitmapSource.Freeze()` before returning

**Standard sizes:** 40 px for emoji grid cells, 60 px for skin tone selector (those keys are `_keySize × 1.5`).

**Construction:** Wraps `new Font("Segoe UI Emoji", ...)` in try/catch; sets `_available = false` if font not found.

### `KeyboardWindow` changes

**`BuildEmojiGrid()`:** For each emoji cell replace:
```csharp
// Before
var label = new TextBlock { Text = displayEmoji, Foreground = Brushes.White, FontSize = 24, ... };
var container = new Border { ..., Child = label };
_keyVisuals[id] = (container, label);

// After
var img = new Image { Source = _emojiRenderer.Get(displayEmoji, 40), Width = 36, Height = 36 };
var container = new Border { ..., Child = img };
_keyVisuals[id] = (container, null!);  // Label unused; dwell highlighting uses Container only
```

**`ShowSkinToneSelector()`:** Same swap at 60 px size.

**`EnterEmojiMode()`:** Instantiate `_emojiRenderer` lazily alongside `_emojiData`:
```csharp
_emojiRenderer ??= new EmojiRenderService();
```
Fall back to existing TextBlock path if `!_emojiRenderer.IsAvailable`.

**`KeyboardWindow` lifecycle:** Implement `IDisposable` (or hook `Closed` event) to call `_emojiRenderer?.Dispose()`.

## `_keyVisuals` Compatibility

The tuple type `(Border Container, TextBlock Label)` is preserved. For emoji `Image` cells, `Label` is `null!`. This is safe because:
- `UpdateKeyVisual` / `ResetKeyVisual` only access `Container.Background`
- `UpdateShiftDisplay` only runs in text mode, never in emoji mode

## Error Handling & Fallback

| Failure | Behaviour |
|---------|-----------|
| Segoe UI Emoji font not found | `IsAvailable = false`; emoji cells use TextBlock fallback (monochrome, as today) |
| `GetHbitmap` / `CreateBitmapSourceFromHBitmap` throws for a single emoji | Catch, return 1×1 transparent placeholder `BitmapSource`; no crash |
| GDI handle leak | `DeleteObject(hBitmap)` always called in `finally` |

## Testing

Visual verification only — no unit tests for the GDI rendering path. The Option B quick test provides an early signal before full implementation.
