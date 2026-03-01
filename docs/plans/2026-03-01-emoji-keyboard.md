# Emoji Keyboard Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a lazy-loaded emoji keyboard with 8 categories, recently-used tracking, global skin tone selection, and pagination — all controlled via eye-tracking dwell.

**Architecture:** The emoji view replaces the text key grid inside the existing `KeyboardWindow` when emoji mode is active. Emoji data lives in a separate JSON file loaded on first use. Only one page (~40 emojis) of WPF controls exists at any time. Category tabs use the existing `DwellDetector`; the emoji grid uses the existing nearest-key approach.

**Tech Stack:** C# / .NET 8 / WPF, System.Text.Json for data, existing DwellDetector + nearest-key dwell infrastructure.

---

### Task 1: EmojiData model and loader

**Files:**
- Create: `src/EyeCtrl.Core/Keyboard/EmojiCategory.cs`
- Create: `src/EyeCtrl.Core/Keyboard/EmojiData.cs`
- Create: `src/EyeCtrl.Core/Keyboard/EmojiDataLoader.cs`
- Test: `tests/EyeCtrl.Core.Tests/Keyboard/EmojiDataLoaderTests.cs`

**Step 1: Create the data models**

`EmojiCategory.cs`:
```csharp
namespace EyeCtrl.Core.Keyboard;

public class EmojiCategory
{
    public string Id { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Emojis { get; set; } = new();
}
```

`EmojiData.cs`:
```csharp
namespace EyeCtrl.Core.Keyboard;

public class EmojiData
{
    public List<EmojiCategory> Categories { get; set; } = new();
    public HashSet<string> SkinToneBase { get; set; } = new();
}
```

**Step 2: Create the loader**

`EmojiDataLoader.cs`:
```csharp
namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

public static class EmojiDataLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static EmojiData Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var raw = JsonSerializer.Deserialize<EmojiDataRaw>(json, Options)
                  ?? throw new InvalidOperationException($"Failed to load emoji data from {filePath}");

        return new EmojiData
        {
            Categories = raw.Categories,
            SkinToneBase = new HashSet<string>(raw.SkinToneBase ?? new List<string>())
        };
    }

    private class EmojiDataRaw
    {
        public List<EmojiCategory> Categories { get; set; } = new();
        public List<string> SkinToneBase { get; set; } = new();
    }
}
```

**Step 3: Write tests**

`tests/EyeCtrl.Core.Tests/Keyboard/EmojiDataLoaderTests.cs`:
```csharp
namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

public class EmojiDataLoaderTests : IDisposable
{
    private readonly string _tempFile;

    public EmojiDataLoaderTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose() => File.Delete(_tempFile);

    [Fact]
    public void Load_ParsesCategories()
    {
        File.WriteAllText(_tempFile, """
        {
            "categories": [
                { "id": "smileys", "icon": "😀", "label": "Smileys", "emojis": ["😀","😃","😄"] }
            ],
            "skinToneBase": ["👋","👍"]
        }
        """);

        var data = EmojiDataLoader.Load(_tempFile);

        Assert.Single(data.Categories);
        Assert.Equal("smileys", data.Categories[0].Id);
        Assert.Equal(3, data.Categories[0].Emojis.Count);
        Assert.Equal(2, data.SkinToneBase.Count);
        Assert.Contains("👋", data.SkinToneBase);
    }

    [Fact]
    public void Load_EmptySkinToneBase_ReturnsEmptySet()
    {
        File.WriteAllText(_tempFile, """
        { "categories": [], "skinToneBase": [] }
        """);

        var data = EmojiDataLoader.Load(_tempFile);

        Assert.Empty(data.SkinToneBase);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "EmojiDataLoader" -v n`
Expected: 2 tests PASS

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Keyboard/EmojiCategory.cs src/EyeCtrl.Core/Keyboard/EmojiData.cs src/EyeCtrl.Core/Keyboard/EmojiDataLoader.cs tests/EyeCtrl.Core.Tests/Keyboard/EmojiDataLoaderTests.cs
git commit -m "feat: add EmojiData model and loader"
```

---

### Task 2: RecentEmojiStore

**Files:**
- Create: `src/EyeCtrl.Core/Keyboard/RecentEmojiStore.cs`
- Test: `tests/EyeCtrl.Core.Tests/Keyboard/RecentEmojiStoreTests.cs`

**Step 1: Implement RecentEmojiStore**

```csharp
namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

public class RecentEmojiStore
{
    private readonly string _filePath;
    private readonly int _maxCount;
    private List<string> _recent;

    public RecentEmojiStore(string filePath, int maxCount = 40)
    {
        _filePath = filePath;
        _maxCount = maxCount;
        _recent = Load();
    }

    public IReadOnlyList<string> Recent => _recent;

    public void Add(string emoji)
    {
        _recent.Remove(emoji); // move to front if already present
        _recent.Insert(0, emoji);
        if (_recent.Count > _maxCount)
            _recent.RemoveAt(_recent.Count - 1);
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_recent);
        File.WriteAllText(_filePath, json);
    }

    private List<string> Load()
    {
        if (!File.Exists(_filePath)) return new List<string>();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
```

**Step 2: Write tests**

```csharp
namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

public class RecentEmojiStoreTests : IDisposable
{
    private readonly string _tempFile;

    public RecentEmojiStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"recent_emoji_test_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void Add_InsertsAtFront()
    {
        var store = new RecentEmojiStore(_tempFile);
        store.Add("😀");
        store.Add("😃");

        Assert.Equal("😃", store.Recent[0]);
        Assert.Equal("😀", store.Recent[1]);
    }

    [Fact]
    public void Add_MovesDuplicateToFront()
    {
        var store = new RecentEmojiStore(_tempFile);
        store.Add("😀");
        store.Add("😃");
        store.Add("😀"); // move to front

        Assert.Equal(2, store.Recent.Count);
        Assert.Equal("😀", store.Recent[0]);
    }

    [Fact]
    public void Add_RespectsMaxCount()
    {
        var store = new RecentEmojiStore(_tempFile, maxCount: 3);
        store.Add("A");
        store.Add("B");
        store.Add("C");
        store.Add("D");

        Assert.Equal(3, store.Recent.Count);
        Assert.DoesNotContain("A", store.Recent);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var store1 = new RecentEmojiStore(_tempFile);
        store1.Add("😀");
        store1.Add("❤️");
        store1.Save();

        var store2 = new RecentEmojiStore(_tempFile);
        Assert.Equal(2, store2.Recent.Count);
        Assert.Equal("❤️", store2.Recent[0]);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new RecentEmojiStore(_tempFile);
        Assert.Empty(store.Recent);
    }
}
```

**Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "RecentEmojiStore" -v n`
Expected: 5 tests PASS

**Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Keyboard/RecentEmojiStore.cs tests/EyeCtrl.Core.Tests/Keyboard/RecentEmojiStoreTests.cs
git commit -m "feat: add RecentEmojiStore with persistence"
```

---

### Task 3: Settings + emoji data file + layout updates

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/KeyboardSettings.cs`
- Create: `src/EyeCtrl.App/Resources/Emojis/emojis.json`
- Modify: `src/EyeCtrl.App/Resources/Layouts/qwerty.json`
- Modify: `src/EyeCtrl.App/Resources/Layouts/qwertz.json`
- Modify: `src/EyeCtrl.App/Resources/Layouts/azerty.json`
- Modify: `src/EyeCtrl.App/EyeCtrl.App.csproj`

**Step 1: Add EmojiSkinTone to KeyboardSettings**

In `src/EyeCtrl.Core/Settings/KeyboardSettings.cs`, add:
```csharp
public int EmojiSkinTone { get; set; } = 0; // 0=default, 1-5=light..dark
```

**Step 2: Create `emojis.json`**

Create `src/EyeCtrl.App/Resources/Emojis/emojis.json` with curated emoji data. The file has this structure:

```json
{
  "categories": [
    {
      "id": "smileys",
      "icon": "😀",
      "label": "Smileys & Emotion",
      "emojis": ["😀","😃","😄","😁","😆","😅","🤣","😂","🙂","😉","😊","😇","🥰","😍","🤩","😘","😗","☺️","😚","😙","🥲","😋","😛","😜","🤪","😝","🤑","🤗","🤭","🫢","🫣","🤫","🤔","🫡","🤐","🤨","😐","😑","😶","🫥","😏","😒","🙄","😬","🤥","😔","😪","🤤","😴","😷","🤒","🤕","🤢","🤮","🥵","🥶","🥴","😵","🤯","🤠","🥳","🥸","😎","🤓","🧐","😕","🫤","😟","🙁","☹️","😮","😯","😲","😳","🥺","🥹","😦","😧","😨","😰","😥","😢","😭","😱","😖","😣","😞","😓","😩","😫","🥱","😤","😡","😠","🤬","😈","👿","💀","☠️","💩","🤡","👹","👺","👻","👽","👾","🤖","😺","😸","😹","😻","😼","😽","🙀","😿","😾"]
    },
    {
      "id": "people",
      "icon": "👋",
      "label": "People & Body",
      "emojis": ["👋","🤚","🖐️","✋","🖖","🫱","🫲","🫳","🫴","👌","🤌","🤏","✌️","🤞","🫰","🤟","🤘","🤙","👈","👉","👆","🖕","👇","☝️","🫵","👍","👎","✊","👊","🤛","🤜","👏","🙌","🫶","👐","🤲","🤝","🙏","✍️","💅","🤳","💪","🦾","🦿","🦵","🦶","👂","🦻","👃","👶","👧","🧒","👦","👩","🧑","👨","👩‍🦱","🧑‍🦱","👨‍🦱","👩‍🦰","🧑‍🦰","👨‍🦰","👱‍♀️","👱","👱‍♂️","👩‍🦳","🧑‍🦳","👨‍🦳","👩‍🦲","🧑‍🦲","👨‍🦲","🧔‍♀️","🧔","🧔‍♂️","👵","🧓","👴","👲","👳‍♀️","👳","👳‍♂️","🧕","👮‍♀️","👮","👮‍♂️","👷‍♀️","👷","👷‍♂️","💂‍♀️","💂","💂‍♂️","🕵️‍♀️","🕵️","🕵️‍♂️"]
    },
    {
      "id": "animals",
      "icon": "🐾",
      "label": "Animals & Nature",
      "emojis": ["🐶","🐱","🐭","🐹","🐰","🦊","🐻","🐼","🐻‍❄️","🐨","🐯","🦁","🐮","🐷","🐽","🐸","🐵","🙈","🙉","🙊","🐒","🐔","🐧","🐦","🐤","🐣","🐥","🦆","🦅","🦉","🦇","🐺","🐗","🐴","🦄","🐝","🪱","🐛","🦋","🐌","🐞","🐜","🪰","🪲","🪳","🦟","🦗","🕷️","🦂","🐢","🐍","🦎","🦖","🦕","🐙","🦑","🦐","🦞","🦀","🐡","🐠","🐟","🐬","🐳","🐋","🦈","🐊","🐅","🐆","🦓","🦍","🦧","🐘","🦣","🦏","🦛","🐪","🐫","🦒","🐃","🐂","🐄","🌵","🎄","🌲","🌳","🌴","🪵","🌱","🌿","☘️","🍀","🎍","🎋","🍃","🍂","🍁","🪻","🌺","🌸","🌼","🌻","🌹","🪷","🌷","🌱"]
    },
    {
      "id": "food",
      "icon": "🍕",
      "label": "Food & Drink",
      "emojis": ["🍇","🍈","🍉","🍊","🍋","🍌","🍍","🥭","🍎","🍏","🍐","🍑","🍒","🍓","🫐","🥝","🍅","🫒","🥥","🥑","🍆","🥔","🥕","🌽","🌶️","🫑","🥒","🥬","🥦","🧄","🧅","🍄","🥜","🌰","🍞","🥐","🥖","🫓","🥨","🥯","🥞","🧇","🧀","🍖","🍗","🥩","🥓","🍔","🍟","🍕","🌭","🥪","🌮","🌯","🫔","🥙","🧆","🥚","🍳","🥘","🍲","🫕","🥣","🥗","🍿","🧈","🧂","🥫","🍱","🍘","🍙","🍚","🍛","🍜","🍝","🍠","🍢","🍣","🍤","🍥","🥮","🍡","🥟","🥠","🥡","🦀","🦞","🦐","🦑","🦪","🍦","🍧","🍨","🍩","🍪","🎂","🍰","🧁","🥧","🍫","🍬","🍭","🍮","🍯","🍼","🥛","☕","🫖","🍵","🍶","🍾","🍷","🍸","🍹","🍺","🍻","🥂","🥃","🫗","🥤","🧋","🧃","🧉","🧊"]
    },
    {
      "id": "activities",
      "icon": "⚽",
      "label": "Activities",
      "emojis": ["⚽","🏀","🏈","⚾","🥎","🎾","🏐","🏉","🥏","🎱","🪀","🏓","🏸","🏒","🏑","🥍","🏏","🪃","🥅","⛳","🪁","🏹","🎣","🤿","🥊","🥋","🎽","🛹","🛼","🛷","⛸️","🥌","🎿","⛷️","🏂","🪂","🏋️","🤼","🤸","🤺","⛹️","🤾","🏌️","🏇","🧘","🏄","🏊","🤽","🚣","🧗","🚵","🚴","🏆","🥇","🥈","🥉","🏅","🎖️","🏵️","🎗️","🎫","🎟️","🎪","🎭","🎨","🎬","🎤","🎧","🎼","🎹","🥁","🪘","🎷","🎺","🪗","🎸","🪕","🎻","🎲","🧩","🎯","🎳","🎮","🕹️","🎰"]
    },
    {
      "id": "travel",
      "icon": "🌍",
      "label": "Travel & Places",
      "emojis": ["🌍","🌎","🌏","🌐","🗺️","🧭","🏔️","⛰️","🌋","🗻","🏕️","🏖️","🏜️","🏝️","🏞️","🏟️","🏛️","🏗️","🧱","🪨","🪵","🛖","🏘️","🏚️","🏠","🏡","🏢","🏣","🏤","🏥","🏦","🏨","🏩","🏪","🏫","🏬","🏭","🏯","🏰","💒","🗼","🗽","⛪","🕌","🛕","🕍","⛩️","🕋","⛲","⛺","🌁","🌃","🏙️","🌄","🌅","🌆","🌇","🌉","♨️","🎠","🛝","🎡","🎢","💈","🎪","🚂","🚃","🚄","🚅","🚆","🚇","🚈","🚉","🚊","🚝","🚞","🚋","🚌","🚍","🚎","🚐","🚑","🚒","🚓","🚔","🚕","🚖","🚗","🚘","🚙","🛻","🚚","🚛","🚜"]
    },
    {
      "id": "objects",
      "icon": "💡",
      "label": "Objects",
      "emojis": ["⌚","📱","📲","💻","⌨️","🖥️","🖨️","🖱️","🖲️","💽","💾","💿","📀","🧮","🎥","🎞️","📽️","🎬","📺","📷","📸","📹","📼","🔍","🔎","🕯️","💡","🔦","🏮","🪔","📔","📕","📖","📗","📘","📙","📚","📓","📒","📃","📜","📄","📰","🗞️","📑","🔖","🏷️","💰","🪙","💴","💵","💶","💷","💸","💳","🧾","💹","✉️","📧","📨","📩","📤","📥","📦","📫","📪","📬","📭","📮","🗳️","✏️","✒️","🖊️","🖋️","📝","💼","📁","📂","🗂️","📅","📆","🗒️","🗓️","📇","📈","📉","📊","📋","📌","📍","📎","🖇️","📏","📐","✂️","🗃️","🗄️","🗑️","🔒","🔓","🔏","🔐","🔑","🗝️"]
    },
    {
      "id": "symbols",
      "icon": "❤️",
      "label": "Symbols",
      "emojis": ["❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎","💔","❤️‍🔥","❤️‍🩹","❣️","💕","💞","💓","💗","💖","💘","💝","💟","☮️","✝️","☪️","🕉️","☸️","✡️","🔯","🕎","☯️","☦️","🛐","⛎","♈","♉","♊","♋","♌","♍","♎","♏","♐","♑","♒","♓","🆔","⚛️","🉑","☢️","☣️","📴","📳","🈶","🈚","🈸","🈺","🈷️","✴️","🆚","💮","🉐","㊙️","㊗️","🈴","🈵","🈹","🈲","🅰️","🅱️","🆎","🆑","🅾️","🆘","❌","⭕","🛑","⛔","📛","🚫","💯","💢","♨️","🚷","🚯","🚳","🚱","🔞","📵","🚭","❗","❕","❓","❔","‼️","⁉️","🔅","🔆","〽️","⚠️","🚸","🔱","⚜️","🔰","♻️","✅","🈯","💹","❇️","✳️","❎","🌐","💠","Ⓜ️","🌀","💤","🏧","🚾","♿","🅿️","🛗","🈳","🈂️","🛂","🛃","🛄","🛅"]
    }
  ],
  "skinToneBase": ["👋","🤚","🖐️","✋","🖖","🫱","🫲","🫳","🫴","👌","🤌","🤏","✌️","🤞","🫰","🤟","🤘","🤙","👈","👉","👆","🖕","👇","☝️","🫵","👍","👎","✊","👊","🤛","🤜","👏","🙌","🫶","👐","🤲","🤝","🙏","✍️","💅","🤳","💪","👂","👃","👶","👧","🧒","👦","👩","🧑","👨","👵","🧓","👴","👲","🧕","🤦","🤷","🙋","🙇","🙆","🙅","💁","🙎","🙍","💆","💇","🧖","🛀","🛌","🏃","🚶","🧎","🧍","💃","🕺","🧗","🏇","🏂","🏌️","🏄","🚣","🏊","⛹️","🏋️","🚴","🚵","🤸","🤽","🤾","🤺","🤹"]
}
```

**Step 3: Add 😀 button to all three layout JSONs**

In each layout file (`qwerty.json`, `qwertz.json`, `azerty.json`), add to the bottom row (the 4th row in `letters` sub-layout), inserting before the Space key:
```json
{ "label": "😀", "output": "", "width": 1.0, "specialAction": "switchEmoji" }
```

Also add to `numbers` and `symbols` bottom rows the same button so users can switch to emoji from any sub-layout.

**Step 4: Update csproj**

In `src/EyeCtrl.App/EyeCtrl.App.csproj`, add content item for emojis:
```xml
<Content Include="Resources\Emojis\*.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  <ExcludeFromSingleFile>true</ExcludeFromSingleFile>
</Content>
```

**Step 5: Build**

Run: `dotnet build`
Expected: 0 errors

**Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Settings/KeyboardSettings.cs src/EyeCtrl.App/Resources/Emojis/emojis.json src/EyeCtrl.App/Resources/Layouts/*.json src/EyeCtrl.App/EyeCtrl.App.csproj
git commit -m "feat: add emoji data, skin tone setting, and emoji button to layouts"
```

---

### Task 4: KeyboardWindow XAML — add CategoryBar

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml`

**Step 1: Add CategoryBar StackPanel**

Update the XAML to add a `CategoryBar` between the PredictionBar and KeyRows:

```xml
<Window x:Class="EyeCtrl.App.Overlays.KeyboardWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        SizeToContent="WidthAndHeight">
    <Border Background="#F0181818" CornerRadius="8" Padding="8">
        <DockPanel>
            <!-- Prediction bar at top (hidden in emoji mode) -->
            <StackPanel x:Name="PredictionBar" Orientation="Horizontal"
                        DockPanel.Dock="Top" Margin="0,0,0,6" Height="40"/>
            <!-- Emoji category tabs (hidden in text mode) -->
            <StackPanel x:Name="CategoryBar" Orientation="Horizontal"
                        DockPanel.Dock="Top" Margin="0,0,0,6" Height="44"
                        HorizontalAlignment="Center" Visibility="Collapsed"/>
            <!-- Keyboard rows / emoji grid -->
            <StackPanel x:Name="KeyRows" VerticalAlignment="Center"/>
        </DockPanel>
    </Border>
</Window>
```

**Step 2: Build**

Run: `dotnet build`
Expected: 0 errors

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml
git commit -m "feat: add CategoryBar element to KeyboardWindow XAML"
```

---

### Task 5: Emoji mode — entry, exit, and grid rendering

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

This is the largest task. Add emoji mode state, lazy loading, grid building, and special action handling.

**Step 1: Add emoji state fields**

Add these fields to the class after the existing fields (around line 44):

```csharp
// Emoji mode state
private bool _isEmojiMode;
private EmojiData? _emojiData;
private RecentEmojiStore? _recentEmojiStore;
private string _activeEmojiCategory = "recent";
private int _emojiPage;
private int _skinTone; // 0=default, 1-5=light..dark
private const int EmojisPerPage = 40;
private const int EmojiColumns = 10;

private static readonly string[] SkinToneModifiers =
{
    "",           // 0 = default yellow
    "\U0001F3FB", // 1 = light
    "\U0001F3FC", // 2 = medium-light
    "\U0001F3FD", // 3 = medium
    "\U0001F3FE", // 4 = medium-dark
    "\U0001F3FF", // 5 = dark
};
```

**Step 2: Add `SetEmojiData` and `SetSkinTone` public methods**

```csharp
public void SetEmojiData(string emojiJsonPath, string recentEmojiPath)
{
    // Lazy: don't load yet, just store paths
    _emojiJsonPath = emojiJsonPath;
    _recentEmojiPath = recentEmojiPath;
}

public void SetSkinTone(int skinTone) => _skinTone = Math.Clamp(skinTone, 0, 5);
public int GetSkinTone() => _skinTone;
public RecentEmojiStore? GetRecentStore() => _recentEmojiStore;
```

Also add the path fields:
```csharp
private string? _emojiJsonPath;
private string? _recentEmojiPath;
```

**Step 3: Add emoji mode entry/exit**

```csharp
private void EnterEmojiMode()
{
    // Lazy-load emoji data on first entry
    if (_emojiData == null && _emojiJsonPath != null)
    {
        _emojiData = EmojiDataLoader.Load(_emojiJsonPath);
        _recentEmojiStore = new RecentEmojiStore(_recentEmojiPath ?? "recent_emojis.json");
    }
    if (_emojiData == null) return;

    _isEmojiMode = true;
    _activeEmojiCategory = _recentEmojiStore?.Recent.Count > 0 ? "recent" : _emojiData.Categories[0].Id;
    _emojiPage = 0;

    PredictionBar.Visibility = Visibility.Collapsed;
    CategoryBar.Visibility = Visibility.Visible;

    BuildCategoryTabs();
    BuildEmojiGrid();
}

private void ExitEmojiMode()
{
    _isEmojiMode = false;
    _emojiPage = 0;

    PredictionBar.Visibility = Visibility.Visible;
    CategoryBar.Visibility = Visibility.Collapsed;

    // Re-enter the text sub-layout that was active before
    RebuildKeys();
}
```

**Step 4: Add `BuildEmojiGrid` method**

```csharp
private void BuildEmojiGrid()
{
    _keyVisuals.Clear();
    _keyDefinitions.Clear();
    _keyScreenPositions.Clear();
    _currentNearestKeyId = null;
    KeyRows.Children.Clear();

    var emojis = GetCurrentPageEmojis();

    // Build emoji rows (EmojiColumns per row)
    for (int i = 0; i < emojis.Count; i += EmojiColumns)
    {
        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var rowCount = Math.Min(EmojiColumns, emojis.Count - i);

        for (int j = 0; j < rowCount; j++)
        {
            var baseEmoji = emojis[i + j];
            var displayEmoji = ApplySkinTone(baseEmoji);
            var id = $"emoji_{i + j}";

            // Store a KeyDefinition so OnKeyDwelled can find the output
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
            rowPanel.Children.Add(container);
        }

        KeyRows.Children.Add(rowPanel);
    }

    // Bottom row: pagination + ABC + skin tone
    var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    var totalEmojis = GetCategoryEmojis(_activeEmojiCategory).Count;
    var totalPages = (int)Math.Ceiling((double)totalEmojis / EmojisPerPage);

    if (totalPages > 1 && _emojiPage > 0)
        AddSpecialButton(bottomRow, "◀", "emojiPage:prev", 1.0);

    AddSpecialButton(bottomRow, "ABC", "switchText", 2.0);
    AddSpecialButton(bottomRow, "🎨", "skinTone", 1.0);

    if (totalPages > 1 && _emojiPage < totalPages - 1)
        AddSpecialButton(bottomRow, "▶", "emojiPage:next", 1.0);

    KeyRows.Children.Add(bottomRow);
}

private void AddSpecialButton(StackPanel row, string text, string action, double widthMultiplier)
{
    var id = $"special_{action}";
    _keyDefinitions[id] = new KeyDefinition { Label = text, Output = "", SpecialAction = action };

    var label = new TextBlock
    {
        Text = text,
        Foreground = Brushes.White,
        FontSize = 16,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    var container = new Border
    {
        Width = _keySize * widthMultiplier + (widthMultiplier > 1 ? (widthMultiplier - 1) * 4 : 0),
        Height = _keySize,
        Margin = new Thickness(2),
        Background = new SolidColorBrush(Color.FromArgb(200, 50, 70, 110)),
        CornerRadius = new CornerRadius(6),
        Child = label
    };

    _keyVisuals[id] = (container, label);
    row.Children.Add(container);
}
```

**Step 5: Add helper methods**

```csharp
private List<string> GetCategoryEmojis(string categoryId)
{
    if (categoryId == "recent")
        return _recentEmojiStore?.Recent.ToList() ?? new List<string>();

    return _emojiData?.Categories.FirstOrDefault(c => c.Id == categoryId)?.Emojis
           ?? new List<string>();
}

private List<string> GetCurrentPageEmojis()
{
    var all = GetCategoryEmojis(_activeEmojiCategory);
    return all.Skip(_emojiPage * EmojisPerPage).Take(EmojisPerPage).ToList();
}

private string ApplySkinTone(string emoji)
{
    if (_skinTone == 0) return emoji;
    if (_emojiData == null || !_emojiData.SkinToneBase.Contains(emoji)) return emoji;
    return emoji + SkinToneModifiers[_skinTone];
}
```

**Step 6: Extend `HandleSpecialKey` for emoji actions**

Add these cases to `HandleSpecialKey`, BEFORE the existing `switchsub:` check:

```csharp
if (lowerAction == "switchemoji")
{
    EnterEmojiMode();
    return;
}

if (lowerAction == "switchtext")
{
    ExitEmojiMode();
    return;
}

if (lowerAction.StartsWith("emojicategory:"))
{
    var catId = action[14..];
    if (_activeEmojiCategory != catId)
    {
        _activeEmojiCategory = catId;
        _emojiPage = 0;
        HighlightActiveCategory();
        BuildEmojiGrid();
    }
    return;
}

if (lowerAction == "emojipage:next")
{
    _emojiPage++;
    BuildEmojiGrid();
    return;
}

if (lowerAction == "emojipage:prev")
{
    _emojiPage = Math.Max(0, _emojiPage - 1);
    BuildEmojiGrid();
    return;
}

if (lowerAction == "skintone")
{
    ShowSkinToneSelector();
    return;
}
```

**Step 7: Modify `OnKeyDwelled` for emoji output**

In the existing `OnKeyDwelled`, after the special action check, when in emoji mode the output is the emoji itself. The existing code already handles this:
```csharp
var output = _isShifted && !string.IsNullOrEmpty(key.ShiftOutput) ? key.ShiftOutput : key.Output;
```
Since emoji KeyDefinitions have `Output = displayEmoji`, this works. But we also need to track recent emojis. Add this after `KeyActivated?.Invoke(output)`:

```csharp
// Track emoji in recents
if (_isEmojiMode && !string.IsNullOrEmpty(output))
{
    // Store base emoji (without skin tone) in recents
    var baseEmoji = _skinTone > 0 && output.EndsWith(SkinToneModifiers[_skinTone])
        ? output[..^SkinToneModifiers[_skinTone].Length]
        : output;
    _recentEmojiStore?.Add(baseEmoji);
}
```

**Step 8: Build**

Run: `dotnet build`
Expected: 0 errors

**Step 9: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: add emoji mode entry/exit and grid rendering"
```

---

### Task 6: Category tabs with DwellDetector

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

**Step 1: Add category tab DwellDetector**

Add a new field:
```csharp
private readonly DwellDetector _categoryDwellDetector = new();
private bool _categoryTargetsInitialized;
```

Wire in constructor:
```csharp
_categoryDwellDetector.TargetActivated += OnCategoryDwelled;
```

**Step 2: Implement `BuildCategoryTabs`**

```csharp
private void BuildCategoryTabs()
{
    _categoryDwellDetector.ClearTargets();
    _categoryTargetsInitialized = false;
    CategoryBar.Children.Clear();

    // Recent tab
    AddCategoryTab("recent", "🕐");

    // Category tabs
    foreach (var cat in _emojiData!.Categories)
        AddCategoryTab(cat.Id, cat.Icon);

    // Skin tone tab
    AddCategoryTab("_skinTone", "🎨");

    HighlightActiveCategory();
}

private void AddCategoryTab(string id, string icon)
{
    var tabId = $"cat_{id}";
    var btn = new Border
    {
        Width = 40,
        Height = 40,
        Margin = new Thickness(2),
        Background = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)),
        CornerRadius = new CornerRadius(6),
        Child = new TextBlock
        {
            Text = icon,
            FontSize = 20,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    CategoryBar.Children.Add(btn);
    _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new ScreenPoint(0, 0), 20, _keyDwellMs));
}

private void HighlightActiveCategory()
{
    for (int i = 0; i < CategoryBar.Children.Count; i++)
    {
        if (CategoryBar.Children[i] is Border border)
        {
            var isActive = i == GetActiveCategoryIndex();
            border.Background = new SolidColorBrush(isActive
                ? Color.FromArgb(200, 0, 100, 0)
                : Color.FromArgb(200, 40, 40, 40));
        }
    }
}

private int GetActiveCategoryIndex()
{
    if (_activeEmojiCategory == "recent") return 0;
    if (_emojiData == null) return 0;
    var idx = _emojiData.Categories.FindIndex(c => c.Id == _activeEmojiCategory);
    return idx >= 0 ? idx + 1 : 0; // +1 for Recent tab at index 0
}
```

**Step 3: Implement `OnCategoryDwelled`**

```csharp
private void OnCategoryDwelled(string targetId)
{
    if (!targetId.StartsWith("cat_")) return;
    var catId = targetId[4..];

    if (catId == "_skinTone")
    {
        ShowSkinToneSelector();
        return;
    }

    if (_activeEmojiCategory != catId)
    {
        _activeEmojiCategory = catId;
        _emojiPage = 0;
        HighlightActiveCategory();
        BuildEmojiGrid();
    }
}
```

**Step 4: Update category target positions in `UpdateGaze`**

In `UpdateGaze`, add category dwell tracking when in emoji mode:

```csharp
public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
{
    UpdateKeyScreenPositions();

    if (_isEmojiMode)
    {
        UpdateCategoryTargetPositions();
        _categoryDwellDetector.Update(screenPoint, timestampMicroseconds);
    }
    else
    {
        UpdatePredictionTargetPositions();
        _predictionDwellDetector.Update(screenPoint, timestampMicroseconds);
    }

    ProcessKeyGaze(screenPoint, timestampMicroseconds);
}
```

**Step 5: Implement `UpdateCategoryTargetPositions`**

```csharp
private void UpdateCategoryTargetPositions()
{
    if (CategoryBar.Children.Count == 0) return;

    for (int i = 0; i < CategoryBar.Children.Count; i++)
    {
        if (CategoryBar.Children[i] is not Border btn) continue;
        var catId = i == 0 ? "recent"
            : i <= _emojiData!.Categories.Count ? _emojiData.Categories[i - 1].Id
            : "_skinTone";
        var tabId = $"cat_{catId}";

        try
        {
            var pos = btn.PointToScreen(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));
            if (!_categoryTargetsInitialized)
            {
                _categoryDwellDetector.RemoveTarget(tabId);
                _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new ScreenPoint(pos.X, pos.Y), 20, _keyDwellMs));
            }
            else
            {
                _categoryDwellDetector.UpdateTargetCenter(tabId, new ScreenPoint(pos.X, pos.Y));
            }
        }
        catch { }
    }
    _categoryTargetsInitialized = true;
}
```

**Step 6: Build**

Run: `dotnet build`
Expected: 0 errors

**Step 7: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: add emoji category tabs with dwell detection"
```

---

### Task 7: Skin tone selector

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

**Step 1: Implement `ShowSkinToneSelector`**

```csharp
private void ShowSkinToneSelector()
{
    _keyVisuals.Clear();
    _keyDefinitions.Clear();
    _keyScreenPositions.Clear();
    _currentNearestKeyId = null;
    KeyRows.Children.Clear();

    var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

    string[] previews = { "👋", "👋🏻", "👋🏼", "👋🏽", "👋🏾", "👋🏿" };
    for (int i = 0; i < 6; i++)
    {
        var id = $"skin_{i}";
        _keyDefinitions[id] = new KeyDefinition { Label = previews[i], Output = "", SpecialAction = $"setSkinTone:{i}" };

        var isActive = i == _skinTone;
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
        rowPanel.Children.Add(container);
    }

    KeyRows.Children.Add(rowPanel);

    // Back button
    var backRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    AddSpecialButton(backRow, "Back", "skinToneBack", 2.0);
    KeyRows.Children.Add(backRow);
}
```

**Step 2: Add skin tone action handling**

Add to `HandleSpecialKey`, before the existing switchsub check:

```csharp
if (lowerAction.StartsWith("setskintone:"))
{
    if (int.TryParse(action[12..], out var tone))
    {
        _skinTone = Math.Clamp(tone, 0, 5);
        // Return to emoji grid with new skin tone applied
        BuildEmojiGrid();
    }
    return;
}

if (lowerAction == "skintoneback")
{
    BuildEmojiGrid();
    return;
}
```

**Step 3: Build**

Run: `dotnet build`
Expected: 0 errors

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: add skin tone selector for emoji keyboard"
```

---

### Task 8: Wire emoji data in App.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Step 1: Pass emoji paths and skin tone to KeyboardWindow**

In `LoadKeyboard()`, after `_keyboard.LoadLayout(...)`, add:

```csharp
// Emoji data paths (lazy-loaded on first use)
var emojiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");
var recentPath = Path.Combine(appDataDir, "recent_emojis.json");
_keyboard.SetEmojiData(emojiPath, recentPath);
_keyboard.SetSkinTone(_settings.Keyboard.EmojiSkinTone);
```

Note: `appDataDir` is local to `OnStartup`. Either extract it to a field or recompute it in `LoadKeyboard`. The simplest fix: make `_appDataDir` a field.

Add field:
```csharp
private string _appDataDir = null!;
```

Set in `OnStartup` where it's currently a local:
```csharp
_appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
```

And update `LoadKeyboard` to use `_appDataDir`.

**Step 2: Save recent emojis and skin tone on exit**

In `OnExit`, before `_wordPredictor?.SaveUserDictionary()`, add:

```csharp
_keyboard.GetRecentStore()?.Save();
_settings.Keyboard.EmojiSkinTone = _keyboard.GetSkinTone();
_settingsService.Save(_settings);
```

**Step 3: Build**

Run: `dotnet build`
Expected: 0 errors

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: wire emoji data loading and persistence in App"
```

---

### Task 9: Build, test, and publish

**Step 1: Run all tests**

Run: `dotnet test`
Expected: All tests pass (30 existing + 7 new emoji tests)

**Step 2: Publish**

Run: `dotnet publish src/EyeCtrl.App/EyeCtrl.App.csproj -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/`
Expected: Success

**Step 3: Verify publish output**

Check that `publish/Resources/Emojis/emojis.json` exists alongside the layout and dictionary files.

**Step 4: Commit any final fixes if needed**
