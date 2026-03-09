# Colored Emoji Rendering Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace monochrome WPF TextBlock emoji rendering with GDI-based color rendering using Windows' built-in Segoe UI Emoji font.

**Architecture:** Try a quick WPF-only fix first (Task 1). If still monochrome, introduce `EmojiRenderService` — it uses `System.Windows.Forms.TextRenderer` (GDI) to render each emoji from Segoe UI Emoji to a `System.Drawing.Bitmap`, fixes the GDI alpha channel issue, converts to a frozen `BitmapSource`, and caches per `(emoji, size)`. `KeyboardWindow` replaces emoji `TextBlock` cells with `Image` controls in `BuildEmojiGrid` and `ShowSkinToneSelector` only. Category tab icons are unchanged.

**Tech Stack:** `System.Drawing`, `System.Windows.Forms.TextRenderer`, `System.Runtime.InteropServices.Marshal`, WPF `BitmapSource`, WPF `Image` control. No new NuGet packages.

---

### Task 1: Option B Quick Test — WPF font fix

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

This might be all that's needed. Worth 2 minutes to check before building the GDI service.

**Step 1: Edit the emoji TextBlock in `BuildEmojiGrid`**

Find the block that builds emoji label TextBlocks (look for `Text = displayEmoji, Foreground = Brushes.White, FontSize = 24`). Change it to:

```csharp
var label = new TextBlock
{
    Text = displayEmoji,
    FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"),
    FontSize = 24,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center
};
```

(Remove `Foreground = Brushes.White` — no new usings needed, FontFamily is already in scope.)

**Step 2: Build**

```
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
```

Expected: 0 errors.

**Step 3: Run and visually check**

Launch the app, open keyboard, switch to emoji mode (🎨 button on the keyboard).

- **If emojis show in color** → commit and STOP. Skip Tasks 2–6.

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "fix: render color emoji via Segoe UI Emoji in WPF TextBlock"
```

- **If still monochrome** → revert and continue to Task 2:

```bash
git checkout src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
```

---

### Task 2: Create `EmojiRenderService`

**Files:**
- Create: `src/EyeCtrl.App/EmojiRenderService.cs`

No unit tests — rendering output is visual only. The service is covered by Task 6 visual verification.

**Step 1: Create the file**

```csharp
namespace EyeCtrl.App;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using TextRenderer = System.Windows.Forms.TextRenderer;
using TextFormatFlags = System.Windows.Forms.TextFormatFlags;

/// <summary>
/// Renders emoji glyphs from Segoe UI Emoji via GDI (TextRenderer) to produce
/// full-color BitmapSource images for use in WPF Image controls.
///
/// WPF TextBlock does not render COLR color layers from Segoe UI Emoji.
/// GDI (via TextRenderer) does — but writes alpha=0 for all pixels.
/// This service fixes the alpha channel and caches results per (emoji, size).
/// </summary>
internal sealed class EmojiRenderService : IDisposable
{
    private readonly Dictionary<(string emoji, int size), BitmapSource> _cache = new();
    private bool _disposed;

    public bool IsAvailable { get; private init; }

    public EmojiRenderService()
    {
        try
        {
            // Probe font availability
            using var probe = new Font("Segoe UI Emoji", 10, GraphicsUnit.Pixel);
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    /// <summary>Returns a cached, frozen BitmapSource for the given emoji at the given pixel size.</summary>
    public BitmapSource Get(string emoji, int pixelSize)
    {
        var key = (emoji, pixelSize);
        if (!_cache.TryGetValue(key, out var src))
            _cache[key] = src = Render(emoji, pixelSize);
        return src;
    }

    private BitmapSource Render(string emoji, int pixelSize)
    {
        // Font size: ~75% of bitmap size leaves a small margin
        using var font = new Font("Segoe UI Emoji", pixelSize * 0.75f, GraphicsUnit.Pixel);

        // Render on a solid dark background — GDI writes alpha=0 for all pixels,
        // so we make the whole bitmap fully opaque after rendering.
        using var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // Fill with the standard tile background color (matches Border background)
            g.Clear(Color.FromArgb(50, 50, 50));

            var rect = new Rectangle(0, 0, pixelSize, pixelSize);
            TextRenderer.DrawText(g, emoji, font, rect,
                foreColor: Color.White,
                backColor: Color.FromArgb(50, 50, 50),
                flags: TextFormatFlags.HorizontalCenter
                     | TextFormatFlags.VerticalCenter
                     | TextFormatFlags.NoPrefix
                     | TextFormatFlags.PreserveGraphicsClipping);
        }

        // Fix GDI alpha channel: GDI renders correct RGB but sets alpha=0 for all pixels.
        // Set alpha=255 for every pixel so the bitmap is fully opaque.
        FixAlphaChannel(bitmap, pixelSize);

        // Convert to WPF BitmapSource via LockBits (preserves all channels correctly)
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, pixelSize, pixelSize),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        BitmapSource result;
        try
        {
            result = BitmapSource.Create(
                pixelSize, pixelSize,
                dpiX: 96, dpiY: 96,
                pixelFormat: System.Windows.Media.PixelFormats.Bgra32,
                palette: null,
                buffer: bitmapData.Scan0,
                bufferSize: Math.Abs(bitmapData.Stride) * pixelSize,
                stride: bitmapData.Stride);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        result.Freeze(); // required for cross-thread use and performance
        return result;
    }

    private static void FixAlphaChannel(Bitmap bitmap, int pixelSize)
    {
        var bmpData = bitmap.LockBits(
            new Rectangle(0, 0, pixelSize, pixelSize),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            int byteCount = Math.Abs(bmpData.Stride) * pixelSize;
            var bytes = new byte[byteCount];
            Marshal.Copy(bmpData.Scan0, bytes, 0, byteCount);

            // BGRA layout: alpha is at offset 3 of each 4-byte pixel
            for (int i = 3; i < byteCount; i += 4)
                bytes[i] = 255;

            Marshal.Copy(bytes, 0, bmpData.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Clear();
    }
}
```

**Step 2: Build**

```
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/EmojiRenderService.cs
git commit -m "feat: add EmojiRenderService — GDI color emoji via Segoe UI Emoji"
```

---

### Task 3: Wire `EmojiRenderService` lifecycle in `KeyboardWindow`

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

**Step 1: Add the field**

In the "Emoji mode state" field group (alongside `_emojiData`, `_recentEmojiStore`), add:

```csharp
private EmojiRenderService? _emojiRenderer;
```

**Step 2: Lazy-init in `EnterEmojiMode()`**

At the top of `EnterEmojiMode()`, just after the `_emojiData` null check block, add:

```csharp
_emojiRenderer ??= new EmojiRenderService();
```

**Step 3: Dispose on window close**

At the end of the `KeyboardWindow` constructor, add:

```csharp
Closed += (_, _) => _emojiRenderer?.Dispose();
```

**Step 4: Build**

```
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
```

Expected: 0 errors.

**Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: wire EmojiRenderService lifecycle in KeyboardWindow"
```

---

### Task 4: Update `BuildEmojiGrid` — replace TextBlock with Image

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

**Step 1: Replace the emoji TextBlock + container block**

Find this block in `BuildEmojiGrid` (inside the inner `for` loop):

```csharp
_keyDefinitions[id] = new KeyDefinition { Label = displayEmoji, Output = displayEmoji };

var label = new TextBlock
{
    Text = displayEmoji,
    Foreground = Brushes.White,
    FontSize = 24,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center
};

var container = new Border
{
    Width = _keySize,
    Height = _keySize,
    Margin = new Thickness(2),
    Background = new SolidColorBrush(Color.FromArgb(200, 50, 50, 50)),
    CornerRadius = new CornerRadius(6),
    Child = label
};

_keyVisuals[id] = (container, label);
```

Replace with:

```csharp
_keyDefinitions[id] = new KeyDefinition { Label = displayEmoji, Output = displayEmoji };

UIElement emojiChild;
if (_emojiRenderer?.IsAvailable == true)
{
    emojiChild = new Image
    {
        Source = _emojiRenderer.Get(displayEmoji, 40),
        Width = 36,
        Height = 36,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
else
{
    // Fallback: monochrome TextBlock (font not available)
    emojiChild = new TextBlock
    {
        Text = displayEmoji,
        Foreground = Brushes.White,
        FontSize = 24,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

var container = new Border
{
    Width = _keySize,
    Height = _keySize,
    Margin = new Thickness(2),
    Background = new SolidColorBrush(Color.FromArgb(200, 50, 50, 50)),
    CornerRadius = new CornerRadius(6),
    Child = emojiChild
};

// Label is null for Image cells — safe because UpdateKeyVisual/ResetKeyVisual
// only use Container.Background, and UpdateShiftDisplay only runs in text mode.
_keyVisuals[id] = (container, null!);
```

**Step 2: Guard against null label in `UpdateShiftDisplay`**

Even though `UpdateShiftDisplay` only runs in text mode, add a defensive null check to prevent any future regression:

Find `UpdateShiftDisplay()` and add a guard at the top of the foreach body:

```csharp
private void UpdateShiftDisplay()
{
    foreach (var (id, (_, label)) in _keyVisuals)
    {
        if (label == null) continue; // Image-based emoji cells have no TextBlock
        if (_keyDefinitions.TryGetValue(id, out var key) && string.IsNullOrEmpty(key.SpecialAction))
        {
            label.Text = _isShifted && !string.IsNullOrEmpty(key.ShiftOutput)
                ? key.ShiftOutput
                : key.Label;
        }
    }
}
```

**Step 3: Build**

```
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
```

Expected: 0 errors.

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: render emoji grid as color Images via EmojiRenderService"
```

---

### Task 5: Update `ShowSkinToneSelector` — replace TextBlock with Image

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

**Step 1: Replace the skin-tone TextBlock + container block**

Find this block in `ShowSkinToneSelector` (inside the `for (int i = 0; i < 6; i++)` loop):

```csharp
var label = new TextBlock
{
    Text = previews[i],
    FontSize = 32,
    Foreground = Brushes.White,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center
};

var container = new Border
{
    Width = _keySize * 1.5,
    Height = _keySize * 1.5,
    Margin = new Thickness(4),
    Background = new SolidColorBrush(isActive
        ? Color.FromArgb(200, 0, 100, 0)
        : Color.FromArgb(200, 50, 50, 50)),
    CornerRadius = new CornerRadius(8),
    Child = label
};

_keyVisuals[id] = (container, label);
```

Replace with:

```csharp
UIElement skinChild;
if (_emojiRenderer?.IsAvailable == true)
{
    skinChild = new Image
    {
        Source = _emojiRenderer.Get(previews[i], 60),
        Width = 52,
        Height = 52,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
else
{
    skinChild = new TextBlock
    {
        Text = previews[i],
        FontSize = 32,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

var container = new Border
{
    Width = _keySize * 1.5,
    Height = _keySize * 1.5,
    Margin = new Thickness(4),
    Background = new SolidColorBrush(isActive
        ? Color.FromArgb(200, 0, 100, 0)
        : Color.FromArgb(200, 50, 50, 50)),
    CornerRadius = new CornerRadius(8),
    Child = skinChild
};

_keyVisuals[id] = (container, null!);
```

**Step 2: Build**

```
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: render skin tone selector as color Images via EmojiRenderService"
```

---

### Task 6: Run tests and visual verification

**Step 1: Run all tests**

```
dotnet test
```

Expected: All 30 tests pass. No regressions.

**Step 2: Build release**

```
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj -c Release
```

Expected: 0 errors.

**Step 3: Visual verification checklist**

Launch the app. Open keyboard → switch to emoji mode:

- [ ] Emoji grid cells show full-color emoji (not white silhouettes)
- [ ] Dwell highlight (green fade on Border background) still visible around emoji
- [ ] Paging (◀ / ▶) works, new page renders in color
- [ ] Skin tone selector shows 6 colored hand emoji previews
- [ ] Selecting a skin tone applies it; emoji grid re-renders in color with skin tone
- [ ] Switching back to text (ABC) and re-entering emoji mode: still colored
- [ ] Category tab icons still look correct (unchanged, still TextBlock)
- [ ] No visible crash or freeze during emoji grid render (first open may be slightly slow due to initial rendering)
- [ ] App closes cleanly (no GDI handle leaks)
