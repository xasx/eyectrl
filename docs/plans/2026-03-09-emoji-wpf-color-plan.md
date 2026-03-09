# Emoji.Wpf Color Emoji Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the failed GDI `EmojiRenderService` with `Emoji.Wpf.TextBlock` to render Segoe UI Emoji in full color in the keyboard overlay.

**Architecture:** Add the `Emoji.Wpf` NuGet package; delete `EmojiRenderService.cs`; replace `Image` / fallback `TextBlock` conditionals in `BuildEmojiGrid` and `ShowSkinToneSelector` with a single `Emoji.Wpf.TextBlock`. No caching, no GDI, no `IDisposable`.

**Tech Stack:** .NET 10 WPF, Emoji.Wpf v0.3.4 (WTFPL), `net10.0-windows`

---

### Task 1: Update project file

**Files:**
- Modify: `src/EyeCtrl.App/EyeCtrl.App.csproj`

**Step 1: Add Emoji.Wpf, remove System.Drawing.Common**

Open `src/EyeCtrl.App/EyeCtrl.App.csproj` and make two edits in the `<ItemGroup>` that contains PackageReferences:

Remove this line:
```xml
<PackageReference Include="System.Drawing.Common" Version="10.0.3" />
```

Add this line in its place:
```xml
<PackageReference Include="Emoji.Wpf" Version="0.3.4" />
```

**Step 2: Restore packages**

```bash
dotnet restore src/EyeCtrl.App/EyeCtrl.App.csproj
```

Expected: `Restored … EyeCtrl.App.csproj` with no errors. Emoji.Wpf and its two
transitive deps (Stfu, JeremyAnsel.HLSL.Targets) should appear in the output.

**Step 3: Build to confirm NuGet resolves**

```bash
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj -c Release --no-restore 2>&1 | tail -5
```

Expected: `0 Fehler` / `0 Error(s)`.

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/EyeCtrl.App.csproj
git commit -m "chore(deps): add Emoji.Wpf 0.3.4, drop System.Drawing.Common"
```

---

### Task 2: Delete EmojiRenderService

**Files:**
- Delete: `src/EyeCtrl.App/EmojiRenderService.cs`

**Step 1: Delete the file**

```bash
git rm src/EyeCtrl.App/EmojiRenderService.cs
```

**Step 2: Build — expected compile errors**

```bash
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj -c Release 2>&1 | grep -i error
```

Expected: errors about `EmojiRenderService` not found and `System.Drawing` types
missing — these will be fixed in Task 3. Do NOT commit yet.

---

### Task 3: Update KeyboardWindow.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

**Step 1: Add using alias at the top of the file**

After the existing `using` directives (around line 1–15), add:

```csharp
using EmojiTextBlock = Emoji.Wpf.TextBlock;
```

This alias avoids ambiguity with `System.Windows.Controls.TextBlock` which is
already in scope via WPF's default namespaces.

**Step 2: Remove System.Drawing usings (if present)**

Delete any `using System.Drawing;` or `using System.Drawing.Imaging;` lines that
exist only because of EmojiRenderService. Check the top of the file — if they
are not needed by other code, remove them.

**Step 3: Remove _emojiRenderer field**

Find and delete this line (around line 45):
```csharp
private EmojiRenderService? _emojiRenderer;
```

**Step 4: Remove Closed dispose hook**

Find and delete this line in the constructor (around line 70):
```csharp
Closed += (_, _) => _emojiRenderer?.Dispose();
```

**Step 5: Remove lazy-init in EnterEmojiMode()**

Find and delete this line in `EnterEmojiMode()` (around line 594):
```csharp
_emojiRenderer ??= new EmojiRenderService();
```

**Step 6: Replace emoji child in BuildEmojiGrid()**

Find the block starting around line 640:
```csharp
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
    // Fallback: monochrome TextBlock (Segoe UI Emoji not available)
    emojiChild = new TextBlock
    {
        Text = displayEmoji,
        Foreground = Brushes.White,
        FontSize = 24,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
```

Replace it entirely with:
```csharp
var emojiChild = new EmojiTextBlock
{
    Text = displayEmoji,
    FontSize = 28,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment   = VerticalAlignment.Center,
};
```

**Step 7: Replace skin child in ShowSkinToneSelector()**

Find the block starting around line 885:
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
        VerticalAlignment   = VerticalAlignment.Center,
    };
}
```

Replace it entirely with:
```csharp
var skinChild = new EmojiTextBlock
{
    Text = previews[i],
    FontSize = 36,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment   = VerticalAlignment.Center,
};
```

**Step 8: Verify null guard in UpdateShiftDisplay() is still present**

Find `UpdateShiftDisplay()` and confirm it still contains:
```csharp
if (label == null) continue;
```

Emoji cells store `(container, null!)` in `_keyVisuals` — this guard must stay.
Do not remove it.

**Step 9: Build — should be clean**

```bash
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj -c Release 2>&1 | tail -5
```

Expected: `0 Fehler` / `0 Error(s)`, no references to `EmojiRenderService` or
`System.Drawing` in the errors.

**Step 10: Run all tests**

```bash
dotnet test --no-restore -c Release 2>&1 | tail -10
```

Expected: same pass/fail counts as before this change — 117 unit tests passing,
6 pre-existing App integration test failures unchanged.

**Step 11: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat(emoji): replace EmojiRenderService with Emoji.Wpf.TextBlock for color rendering"
```

---

### Task 4: Push and verify visually

**Step 1: Push to origin**

```bash
git push origin main
```

**Step 2: Ask the user to verify**

Ask the user to run the app and open the emoji keyboard. Confirm:
- Emoji grid tiles show full-color Segoe UI Emoji glyphs
- Skin tone selector shows colored hand variants
- Text mode, toolbar, and action menu are unaffected
