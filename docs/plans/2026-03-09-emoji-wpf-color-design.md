# Colored Emoji via Emoji.Wpf — Design

**Date:** 2026-03-09
**Status:** Approved

## Problem

The EyeCtrl emoji keyboard shows monochrome emoji. Two previous attempts to fix
this via GDI failed:

1. `Graphics.FromImage(bitmap).GetHdc()` — GDI+ DIB-backed DC, does not
   activate COLR color-font rendering.
2. `CreateCompatibleDC(GetDC(NULL))` + `CreateCompatibleBitmap` (screen-
   compatible DC via P/Invoke) — also produced monochrome output.

Root cause: GDI's COLR rendering path for Segoe UI Emoji is not reliably
activated from within a WPF application process, regardless of DC type.

## Solution

Replace the custom `EmojiRenderService` (GDI/P/Invoke) with
[`Emoji.Wpf`](https://github.com/samhocevar/emoji.wpf), which renders emoji
directly into WPF's `DrawingContext` using the font's built-in color layers.
`Emoji.Wpf.TextBlock` inherits from `System.Windows.Controls.TextBlock` and is
a drop-in replacement — no bitmap caching, no GDI, no `IDisposable`.

## Dependencies

| Change | Detail |
|--------|--------|
| Add | `Emoji.Wpf` v0.3.4 (WTFPL licence) |
| Remove | `System.Drawing.Common` v10.0.3 (was added only for EmojiRenderService) |
| Keep | `UseWindowsForms=true` (still needed for NotifyIcon system tray) |

`Emoji.Wpf` targets `netcoreapp3.1`, which is forward-compatible with
`net10.0-windows`.

## Files Changed

### Deleted
- `src/EyeCtrl.App/EmojiRenderService.cs` — entire file removed

### Modified: `src/EyeCtrl.App/EyeCtrl.App.csproj`
- Add `Emoji.Wpf` PackageReference
- Remove `System.Drawing.Common` PackageReference

### Modified: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

**Using alias** (avoids conflict with `System.Windows.Controls.TextBlock`):
```csharp
using EmojiTextBlock = Emoji.Wpf.TextBlock;
```

**Remove `_emojiRenderer` plumbing (3 sites):**
```csharp
// field
private EmojiRenderService? _emojiRenderer;

// constructor
Closed += (_, _) => _emojiRenderer?.Dispose();

// EnterEmojiMode()
_emojiRenderer ??= new EmojiRenderService();
```

**`BuildEmojiGrid()` — replace Image/TextBlock conditional:**
```csharp
// Before: if (_emojiRenderer?.IsAvailable == true) { new Image ... } else { new TextBlock ... }
// After:
var emojiChild = new EmojiTextBlock
{
    Text = displayEmoji,
    FontSize = 28,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment   = VerticalAlignment.Center,
};
```
`_keyVisuals[id] = (container, null!)` unchanged — emoji display element is
not a key label and must not be touched by `UpdateShiftDisplay`.

**`ShowSkinToneSelector()` — same pattern:**
```csharp
var skinChild = new EmojiTextBlock
{
    Text = previews[i],
    FontSize = 36,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment   = VerticalAlignment.Center,
};
```

**`UpdateShiftDisplay()` null guard** — `if (label == null) continue;` stays.

## Testing

| Check | How |
|-------|-----|
| Build | `dotnet build` — 0 errors |
| Color emoji | Open keyboard → emoji tab → tiles show Segoe UI Emoji in color |
| Skin tone selector | 6 hand variants render in color |
| Regression | Text mode, dwell detection, toolbar, action menu unaffected |
