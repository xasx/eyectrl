# KeyboardWindow Refactoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the 917-line `KeyboardWindow.xaml.cs` into three focused pieces — an expanded `KeyboardViewModel` (state + logic), a new `EmojiGridManager` (emoji rendering), and a slimmed `KeyboardWindow` (orchestration + rendering) — with no visual or behavioral changes.

**Architecture:** Move nearest-key gaze tracking, text special-key handling, prediction integration, and state management into `KeyboardViewModel`. Extract all emoji rendering, categories, pagination, and skin tones into `EmojiGridManager`. Leave text keyboard rendering, visual feedback, prediction bar rendering, and gaze routing in `KeyboardWindow`.

**Tech Stack:** C# / .NET 8 / WPF, CommunityToolkit.Mvvm (ObservableObject, [ObservableProperty], IMessenger)

**Spec:** `docs/superpowers/specs/2026-04-11-keyboard-window-refactoring-design.md`

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` | Expand | State, gaze tracking, text special keys, prediction, key activation |
| `src/EyeCtrl.App/Overlays/EmojiGridManager.cs` | Create | Emoji grid rendering, categories, pagination, skin tones, category dwell |
| `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` | Slim | Orchestration, text key rendering, visual feedback, prediction bar, gaze routing |
| `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs` | Expand | Tests for gaze tracking, special keys, prediction, key activation |

---

### Task 1: Expand KeyboardViewModel with gaze tracking

Move the nearest-key gaze tracking algorithm (`ProcessKeyGaze`) and its state into `KeyboardViewModel`. This is the core logic extraction — the ViewModel becomes the brain, the window becomes the eyes and hands.

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs`

- [ ] **Step 1: Write failing test for nearest-key detection**

In `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs`, add:

```csharp
[Fact]
public void ProcessKeyGaze_FindsNearestKey()
{
    var vm = CreateViewModel();
    vm.Configure(keyDwellMs: 500, cooldownMs: 200);

    var positions = new Dictionary<string, PhysicalPoint>
    {
        ["key_0_0"] = new(100, 100),
        ["key_0_1"] = new(200, 100),
    };

    // Gaze at (110, 105) — closest to key_0_0
    vm.ProcessKeyGaze(positions, new PhysicalPoint(110, 105), isGazeInsideWindow: true, timestampUs: 0);

    Assert.Equal("key_0_0", vm.NearestKeyId);
}
```

Also add the helper at the bottom of the test class:

```csharp
private KeyboardViewModel CreateViewModel()
{
    return new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
}
```

Add `using EyeCtrl.Core.Models;` to the test file's usings.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "ProcessKeyGaze_FindsNearestKey" -v n`
Expected: FAIL — `ProcessKeyGaze` method doesn't exist yet (or has wrong signature).

- [ ] **Step 3: Write failing test for dwell accumulation**

```csharp
[Fact]
public void ProcessKeyGaze_AccumulatesDwellProgress()
{
    var vm = CreateViewModel();
    vm.Configure(keyDwellMs: 500, cooldownMs: 200);

    var positions = new Dictionary<string, PhysicalPoint>
    {
        ["key_0_0"] = new(100, 100),
    };

    // First frame: enter key
    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
    Assert.Equal(0.0, vm.NearestKeyProgress); // just entered, no progress yet

    // Second frame: 250ms later = 50% progress
    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 250_000);
    Assert.Equal(0.5, vm.NearestKeyProgress, precision: 2);
}
```

- [ ] **Step 4: Write failing test for dwell activation callback**

```csharp
[Fact]
public void ProcessKeyGaze_FiresCallbackOnDwellComplete()
{
    var vm = CreateViewModel();
    vm.Configure(keyDwellMs: 500, cooldownMs: 200);

    string? activatedKeyId = null;
    vm.KeyDwellCompleted += id => activatedKeyId = id;

    var positions = new Dictionary<string, PhysicalPoint>
    {
        ["key_0_0"] = new(100, 100),
    };

    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 500_000);

    Assert.Equal("key_0_0", activatedKeyId);
}
```

- [ ] **Step 5: Write failing test for cooldown after activation**

```csharp
[Fact]
public void ProcessKeyGaze_CooldownPreventsReactivation()
{
    var vm = CreateViewModel();
    vm.Configure(keyDwellMs: 500, cooldownMs: 200);

    string? activatedKeyId = null;
    int activationCount = 0;
    vm.KeyDwellCompleted += id => { activatedKeyId = id; activationCount++; };

    var positions = new Dictionary<string, PhysicalPoint>
    {
        ["key_0_0"] = new(100, 100),
    };

    // Activate
    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 500_000);
    Assert.Equal(1, activationCount);

    // During cooldown (100ms after activation, cooldown is 200ms)
    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 600_000);
    Assert.Null(vm.NearestKeyId); // reset during cooldown

    // After cooldown (250ms after activation)
    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 750_000);
    Assert.Equal("key_0_0", vm.NearestKeyId); // tracking again
}
```

- [ ] **Step 6: Write failing test for gaze-outside-window resets tracking**

```csharp
[Fact]
public void ProcessKeyGaze_ResetsWhenGazeOutsideWindow()
{
    var vm = CreateViewModel();
    vm.Configure(keyDwellMs: 500, cooldownMs: 200);

    var positions = new Dictionary<string, PhysicalPoint>
    {
        ["key_0_0"] = new(100, 100),
    };

    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
    Assert.Equal("key_0_0", vm.NearestKeyId);

    // Gaze moves outside window
    vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), isGazeInsideWindow: false, 100_000);
    Assert.Null(vm.NearestKeyId);
}
```

- [ ] **Step 7: Implement ProcessKeyGaze in KeyboardViewModel**

Replace the entire contents of `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` with:

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class KeyboardViewModel : ObservableObject,
    IRecipient<ShowKeyboardMessage>,
    IRecipient<HideKeyboardMessage>,
    IDisposable
{
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;
    private bool _disposed;

    // Gaze tracking state
    private long _nearestKeyEnteredTimestamp;
    private long _keyCooldownEndTimestamp;
    private int _keyDwellMs;
    private int _cooldownMs;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isShifted;
    [ObservableProperty] private bool _isEmojiMode;
    [ObservableProperty] private string? _nearestKeyId;
    [ObservableProperty] private double _nearestKeyProgress;

    public event Action<string>? KeyDwellCompleted;

    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
        messenger.RegisterAll(this);
    }

    public void Configure(int keyDwellMs, int cooldownMs)
    {
        _keyDwellMs = keyDwellMs;
        _cooldownMs = cooldownMs;
    }

    public void Receive(ShowKeyboardMessage message) => IsVisible = true;
    public void Receive(HideKeyboardMessage message) => IsVisible = false;

    /// <summary>
    /// Nearest-key dwell tracking: finds the closest key to the gaze point and
    /// tracks dwell time directly. Eliminates dead zones between keys and avoids
    /// circular hit-area overlap issues with rectangular keyboard keys.
    /// </summary>
    public void ProcessKeyGaze(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, bool isGazeInsideWindow, long timestampMicroseconds)
    {
        if (keyScreenPositions.Count == 0) return;

        if (!isGazeInsideWindow)
        {
            if (NearestKeyId != null)
            {
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            return;
        }

        // Post-activation cooldown
        if (timestampMicroseconds < _keyCooldownEndTimestamp)
        {
            if (NearestKeyId != null)
            {
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            return;
        }

        // Find nearest key
        string? nearestId = null;
        double nearestDist = double.MaxValue;

        foreach (var (id, center) in keyScreenPositions)
        {
            double dx = gazePoint.X - center.X;
            double dy = gazePoint.Y - center.Y;
            double dist = dx * dx + dy * dy;
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }

        if (nearestId != NearestKeyId)
        {
            NearestKeyProgress = 0;
            NearestKeyId = nearestId;
            _nearestKeyEnteredTimestamp = timestampMicroseconds;
        }
        else if (nearestId != null)
        {
            long elapsed = timestampMicroseconds - _nearestKeyEnteredTimestamp;
            NearestKeyProgress = Math.Min(1.0, (double)elapsed / (_keyDwellMs * MathConstants.MicrosecondsPerMillisecond));

            if (elapsed >= _keyDwellMs * MathConstants.MicrosecondsPerMillisecond)
            {
                NearestKeyProgress = 0;
                NearestKeyId = null;
                _keyCooldownEndTimestamp = timestampMicroseconds + (_cooldownMs * MathConstants.MicrosecondsPerMillisecond);
                KeyDwellCompleted?.Invoke(nearestId);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
```

Note: This removes the methods that the window was ignoring (`ToggleShift`, `OnKeyActivated`, `ToggleEmojiMode`, `NextEmojiPage`, `PreviousEmojiPage`, `CalculateEmojiPageCount`). Those were dead code — the window had its own implementation. The ViewModel will gain proper versions of the ones it needs in later tasks.

- [ ] **Step 8: Run all gaze-tracking tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "ProcessKeyGaze" -v n`
Expected: All 5 new tests PASS.

- [ ] **Step 9: Verify existing tests still pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests -v n`
Expected: Existing tests `ShowsKeyboard_OnShowMessage`, `HidesKeyboard_OnHideMessage`, `TogglesShift_OnShiftActivation` pass. Tests that relied on removed methods (`SendsKeyActivated_OnKeyDwell`, `SendsShiftedKey_WhenShifted`, `EmojiMode_TogglesOnAndOff`, `EmojiPagination_CalculatesCorrectPageCount`) will fail. Update them:

Remove these tests (they tested dead code that will be replaced with real implementations in later tasks):
- `SendsKeyActivated_OnKeyDwell`
- `SendsShiftedKey_WhenShifted`
- `EmojiMode_TogglesOnAndOff`
- `EmojiPagination_CalculatesCorrectPageCount`

Also update `TogglesShift_OnShiftActivation` — `ToggleShift()` was removed; shift is now toggled via the `IsShifted` property setter directly. Replace with:

```csharp
[Fact]
public void IsShifted_CanBeToggled()
{
    var vm = CreateViewModel();
    Assert.False(vm.IsShifted);

    vm.IsShifted = true;
    Assert.True(vm.IsShifted);

    vm.IsShifted = false;
    Assert.False(vm.IsShifted);
}
```

- [ ] **Step 10: Run all ViewModel tests to verify**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests -v n`
Expected: All tests PASS.

- [ ] **Step 11: Verify solution builds**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeds. (KeyboardWindow still compiles — it still has its own `ProcessKeyGaze` which we haven't removed yet. The ViewModel is self-contained.)

- [ ] **Step 12: Commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs
git commit -m "refactor: expand KeyboardViewModel with nearest-key gaze tracking"
```

---

### Task 2: Add text special-key handling and prediction to KeyboardViewModel

Add the text-domain special key methods, prediction integration, current input tracking, and key activation logic to the ViewModel.

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs`

- [ ] **Step 1: Write failing tests for text special key methods**

```csharp
[Fact]
public void HandleBackspace_RemovesLastCharAndSendsMessage()
{
    var vm = CreateViewModel();
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.CurrentInput = "hel";
    vm.HandleBackspace();

    Assert.Equal("he", vm.CurrentInput);
    Assert.Equal("\b", sentKey);
}

[Fact]
public void HandleBackspace_DoesNothingWhenEmpty()
{
    var vm = CreateViewModel();
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.CurrentInput = "";
    vm.HandleBackspace();

    Assert.Equal("", vm.CurrentInput);
    Assert.Equal("\b", sentKey);
}

[Fact]
public void HandleEnter_ClearsInputAndSendsMessage()
{
    var vm = CreateViewModel();
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.CurrentInput = "hello";
    vm.HandleEnter();

    Assert.Equal("", vm.CurrentInput);
    Assert.Equal("\n", sentKey);
}

[Fact]
public void HandleSpace_ClearsInputAndSendsMessage()
{
    var vm = CreateViewModel();
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.CurrentInput = "hello";
    vm.HandleSpace();

    Assert.Equal("", vm.CurrentInput);
    Assert.Equal(" ", sentKey);
}

[Fact]
public void HandleTab_SendsMessage()
{
    var vm = CreateViewModel();
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.HandleTab();

    Assert.Equal("\t", sentKey);
}
```

- [ ] **Step 2: Write failing tests for key activation**

```csharp
[Fact]
public void ActivateKey_SendsOutputAndAppendsToInput()
{
    var vm = CreateViewModel();
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.ActivateKey("a", shiftOutput: null);

    Assert.Equal("a", sentKey);
    Assert.Equal("a", vm.CurrentInput);
}

[Fact]
public void ActivateKey_UsesShiftOutputWhenShifted()
{
    var vm = CreateViewModel();
    vm.IsShifted = true;
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.ActivateKey("a", shiftOutput: "A");

    Assert.Equal("A", sentKey);
    Assert.Equal("A", vm.CurrentInput);
    Assert.False(vm.IsShifted); // auto-unshift
}

[Fact]
public void ActivateKey_NoAutoUnshiftWhenNoShiftOutput()
{
    var vm = CreateViewModel();
    vm.IsShifted = true;
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.ActivateKey("1", shiftOutput: null);

    Assert.Equal("1", sentKey);
    Assert.True(vm.IsShifted); // no unshift for non-letter keys
}
```

- [ ] **Step 3: Write failing test for prediction integration**

```csharp
[Fact]
public void GetSuggestions_DelegatesToPredictor()
{
    var vm = CreateViewModel();
    // No predictor set — returns empty
    var suggestions = vm.GetSuggestions();
    Assert.Empty(suggestions);
}

[Fact]
public void HandlePredictionSelected_ClearsInputAndSendsMessage()
{
    var vm = CreateViewModel();
    vm.CurrentInput = "hel";

    PredictionSelectedMessage? sentMsg = null;
    _messenger.Register<PredictionSelectedMessage>(_messenger, (_, msg) => sentMsg = msg);

    vm.HandlePredictionSelected("hello");

    Assert.Equal("", vm.CurrentInput);
    Assert.NotNull(sentMsg);
    Assert.Equal("hello", sentMsg!.Word);
    Assert.Equal(3, sentMsg.DeleteCount);
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "Handle|ActivateKey|GetSuggestions" -v n`
Expected: FAIL — methods don't exist yet.

- [ ] **Step 5: Implement text special keys, key activation, and prediction in KeyboardViewModel**

Add these fields and properties to `KeyboardViewModel`:

```csharp
[ObservableProperty] private string _currentInput = "";
[ObservableProperty] private string _activeSubLayoutId = "letters";

private WordPredictor? _predictor;
private KeyboardLayout? _layout;
```

Add `using EyeCtrl.Core.Keyboard;` and `using EyeCtrl.Core.Prediction;` to the usings.

Add these methods:

```csharp
public void SetLayout(KeyboardLayout layout)
{
    _layout = layout;
    ActiveSubLayoutId = layout.DefaultSubLayout;
}

public void SetPredictor(WordPredictor? predictor) => _predictor = predictor;

public void ActivateKey(string output, string? shiftOutput)
{
    var actualOutput = IsShifted && !string.IsNullOrEmpty(shiftOutput)
        ? shiftOutput
        : output;

    if (IsShifted && !string.IsNullOrEmpty(shiftOutput))
        IsShifted = false;

    CurrentInput += actualOutput;
    _predictor?.LearnWord(actualOutput);
    _messenger.Send(new KeyActivatedMessage(actualOutput));
}

public void HandleBackspace()
{
    if (CurrentInput.Length > 0)
        CurrentInput = CurrentInput[..^1];
    _messenger.Send(new KeyActivatedMessage("\b"));
}

public void HandleEnter()
{
    CurrentInput = "";
    _messenger.Send(new KeyActivatedMessage("\n"));
}

public void HandleSpace()
{
    if (CurrentInput.Length > 0)
        _predictor?.LearnWord(CurrentInput);
    CurrentInput = "";
    _messenger.Send(new KeyActivatedMessage(" "));
}

public void HandleTab()
{
    _messenger.Send(new KeyActivatedMessage("\t"));
}

public void SwitchSubLayout(string subLayoutId)
{
    if (_layout == null || !_layout.SubLayouts.ContainsKey(subLayoutId)) return;
    if (ActiveSubLayoutId == subLayoutId) return;
    ActiveSubLayoutId = subLayoutId;
    IsShifted = false;
}

public List<string> GetSuggestions()
{
    if (_predictor == null) return new List<string>();
    return _predictor.GetSuggestions(CurrentInput, maxCount: 5);
}

public void HandlePredictionSelected(string word)
{
    _predictor?.LearnWord(word);
    var deleteCount = CurrentInput.Length;
    CurrentInput = "";
    _messenger.Send(new PredictionSelectedMessage(word, deleteCount));
}
```

- [ ] **Step 6: Run all tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests -v n`
Expected: All tests PASS.

- [ ] **Step 7: Verify solution builds**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs
git commit -m "refactor: add text special keys, key activation, and prediction to KeyboardViewModel"
```

---

### Task 3: Create EmojiGridManager

Extract all emoji rendering, category management, pagination, and skin tone logic from `KeyboardWindow` into a new `EmojiGridManager` class.

**Files:**
- Create: `src/EyeCtrl.App/Overlays/EmojiGridManager.cs`

- [ ] **Step 1: Create EmojiGridManager.cs**

Create `src/EyeCtrl.App/Overlays/EmojiGridManager.cs` with all emoji logic extracted from `KeyboardWindow`:

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EmojiTextBlock = Emoji.Wpf.TextBlock;

public class EmojiGridManager
{
    private const int EmojisPerPage = 40;
    private const int EmojiColumns = 10;

    private static readonly Color KeyColorNormal = Color.FromArgb(200, 50, 50, 50);
    private static readonly Color KeyColorSwitch = Color.FromArgb(200, 50, 70, 110);
    private static readonly Color KeyColorHighlight = Color.FromArgb(200, 0, 100, 0);
    private static readonly Color CategoryBarColor = Color.FromArgb(200, 40, 40, 40);
    private static readonly Color CategoryBarActiveColor = Color.FromArgb(200, 0, 100, 0);
    private static readonly Color EmojiGridBackgroundColor = Color.FromArgb(200, 50, 50, 50);

    private static readonly string[] SkinToneModifiers =
    {
        "",           // 0 = default yellow
        "\U0001F3FB", // 1 = light
        "\U0001F3FC", // 2 = medium-light
        "\U0001F3FD", // 3 = medium
        "\U0001F3FE", // 4 = medium-dark
        "\U0001F3FF", // 5 = dark
    };

    private readonly StackPanel _keyRows;
    private readonly StackPanel _categoryBar;
    private readonly Action<string, KeyDefinition, Border, TextBlock?> _registerKeyVisual;
    private readonly Func<double> _getKeySize;
    private readonly Action _clearKeyState;

    private readonly DwellDetector _categoryDwellDetector = new();
    private bool _categoryTargetsInitialized;

    private EmojiData? _emojiData;
    private RecentEmojiStore? _recentEmojiStore;
    private string _activeEmojiCategory = "recent";
    private int _emojiPage;
    private int _skinTone;
    private string? _emojiJsonPath;
    private string? _recentEmojiPath;
    private int _keyDwellMs;

    public EmojiGridManager(
        StackPanel keyRows,
        StackPanel categoryBar,
        Action<string, KeyDefinition, Border, TextBlock?> registerKeyVisual,
        Func<double> getKeySize,
        Action clearKeyState)
    {
        _keyRows = keyRows;
        _categoryBar = categoryBar;
        _registerKeyVisual = registerKeyVisual;
        _getKeySize = getKeySize;
        _clearKeyState = clearKeyState;
        _categoryDwellDetector.TargetActivated += OnCategoryDwelled;
    }

    public event Action? ExitRequested;
    public RecentEmojiStore? RecentStore => _recentEmojiStore;
    public int SkinTone => _skinTone;

    public void SetEmojiData(string emojiJsonPath, string recentEmojiPath)
    {
        _emojiJsonPath = emojiJsonPath;
        _recentEmojiPath = recentEmojiPath;
    }

    public void SetSkinTone(int skinTone) => _skinTone = Math.Clamp(skinTone, 0, 5);
    public void SetKeyDwellMs(int keyDwellMs) => _keyDwellMs = keyDwellMs;
    public void SetCooldown(int cooldownMs) => _categoryDwellDetector.SetCooldown(cooldownMs);

    public void Enter()
    {
        if (_emojiData == null && _emojiJsonPath != null)
        {
            _emojiData = EmojiDataLoader.Load(_emojiJsonPath);
            _recentEmojiStore = new RecentEmojiStore(_recentEmojiPath ?? "recent_emojis.json");
        }
        if (_emojiData == null) return;

        _activeEmojiCategory = _recentEmojiStore?.Recent.Count > 0 ? "recent" : _emojiData.Categories[0].Id;
        _emojiPage = 0;

        _categoryBar.Visibility = Visibility.Visible;

        BuildCategoryTabs();
        BuildGrid();
    }

    public void Exit()
    {
        _emojiPage = 0;
        _categoryBar.Visibility = Visibility.Collapsed;
    }

    public void BuildGrid()
    {
        _clearKeyState();
        _keyRows.Children.Clear();

        var emojis = GetCurrentPageEmojis();
        var keySize = _getKeySize();

        for (int i = 0; i < emojis.Count; i += EmojiColumns)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var rowCount = Math.Min(EmojiColumns, emojis.Count - i);

            for (int j = 0; j < rowCount; j++)
            {
                var baseEmoji = emojis[i + j];
                var displayEmoji = ApplySkinTone(baseEmoji);
                var id = $"emoji_{i + j}";
                var keyDef = new KeyDefinition { Label = displayEmoji, Output = displayEmoji };

                var emojiChild = new EmojiTextBlock
                {
                    Text = displayEmoji,
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var container = new Border
                {
                    Width = keySize,
                    Height = keySize,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(EmojiGridBackgroundColor),
                    CornerRadius = new CornerRadius(6),
                    Child = emojiChild
                };

                _registerKeyVisual(id, keyDef, container, null!);
                rowPanel.Children.Add(container);
            }

            _keyRows.Children.Add(rowPanel);
        }

        // Bottom row: pagination + ABC + skin tone
        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var totalEmojis = GetCategoryEmojis(_activeEmojiCategory).Count;
        var totalPages = (int)Math.Ceiling((double)totalEmojis / EmojisPerPage);

        if (totalPages > 1 && _emojiPage > 0)
            AddSpecialButton(bottomRow, "\u25C0", "emojiPage:prev", 1.0);

        AddSpecialButton(bottomRow, "ABC", "switchText", 2.0);
        AddSpecialButton(bottomRow, "\uD83C\uDFA8", "skinTone", 1.0);

        if (totalPages > 1 && _emojiPage < totalPages - 1)
            AddSpecialButton(bottomRow, "\u25B6", "emojiPage:next", 1.0);

        _keyRows.Children.Add(bottomRow);
    }

    public void HandleAction(string action)
    {
        var lowerAction = action.ToLowerInvariant();

        if (lowerAction == "switchtext")
        {
            ExitRequested?.Invoke();
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
                BuildGrid();
            }
            return;
        }

        if (lowerAction == "emojipage:next")
        {
            _emojiPage++;
            BuildGrid();
            return;
        }

        if (lowerAction == "emojipage:prev")
        {
            _emojiPage = Math.Max(0, _emojiPage - 1);
            BuildGrid();
            return;
        }

        if (lowerAction == "skintone")
        {
            ShowSkinToneSelector();
            return;
        }

        if (lowerAction.StartsWith("setskintone:"))
        {
            if (int.TryParse(action[12..], out var tone))
            {
                _skinTone = Math.Clamp(tone, 0, 5);
                BuildGrid();
            }
            return;
        }

        if (lowerAction == "skintoneback")
        {
            BuildGrid();
        }
    }

    public void TrackEmojiInRecents(string output)
    {
        if (string.IsNullOrEmpty(output)) return;
        var baseEmoji = _skinTone > 0 && output.EndsWith(SkinToneModifiers[_skinTone])
            ? output[..^SkinToneModifiers[_skinTone].Length]
            : output;
        _recentEmojiStore?.Add(baseEmoji);
    }

    public void UpdateCategoryTargets(PhysicalPoint gazePoint, long timestampMicroseconds)
    {
        UpdateCategoryTargetPositions();
        _categoryDwellDetector.Update(gazePoint, timestampMicroseconds);
    }

    private void UpdateCategoryTargetPositions()
    {
        if (_categoryBar.Children.Count == 0 || _emojiData == null) return;

        for (int i = 0; i < _categoryBar.Children.Count; i++)
        {
            if (_categoryBar.Children[i] is not Border btn) continue;
            var catId = i == 0 ? "recent"
                : i <= _emojiData.Categories.Count ? _emojiData.Categories[i - 1].Id
                : "_skinTone";
            var tabId = $"cat_{catId}";

            try
            {
                var pos = btn.PointToScreen(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));
                if (!_categoryTargetsInitialized)
                {
                    _categoryDwellDetector.RemoveTarget(tabId);
                    _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new PhysicalPoint(pos.X, pos.Y), DpiHelper.LogicalToPhysicalX(20), _keyDwellMs));
                }
                else
                {
                    _categoryDwellDetector.UpdateTargetCenter(tabId, new PhysicalPoint(pos.X, pos.Y));
                }
            }
            catch (InvalidOperationException)
            {
                // Element not yet rendered or not in visual tree
            }
        }
        _categoryTargetsInitialized = true;
    }

    private void BuildCategoryTabs()
    {
        _categoryDwellDetector.ClearTargets();
        _categoryTargetsInitialized = false;
        _categoryBar.Children.Clear();

        AddCategoryTab("recent", "\uD83D\uDD50");

        foreach (var cat in _emojiData!.Categories)
            AddCategoryTab(cat.Id, cat.Icon);

        AddCategoryTab("_skinTone", "\uD83C\uDFA8");

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
            Background = new SolidColorBrush(CategoryBarColor),
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

        _categoryBar.Children.Add(btn);
        _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new PhysicalPoint(0, 0), DpiHelper.LogicalToPhysicalX(20), _keyDwellMs));
    }

    private void HighlightActiveCategory()
    {
        for (int i = 0; i < _categoryBar.Children.Count; i++)
        {
            if (_categoryBar.Children[i] is Border border)
            {
                var isActive = i == GetActiveCategoryIndex();
                border.Background = new SolidColorBrush(isActive ? CategoryBarActiveColor : CategoryBarColor);
            }
        }
    }

    private int GetActiveCategoryIndex()
    {
        if (_activeEmojiCategory == "recent") return 0;
        if (_emojiData == null) return 0;
        var idx = _emojiData.Categories.FindIndex(c => c.Id == _activeEmojiCategory);
        return idx >= 0 ? idx + 1 : 0;
    }

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
            BuildGrid();
        }
    }

    private void ShowSkinToneSelector()
    {
        _clearKeyState();
        _keyRows.Children.Clear();
        var keySize = _getKeySize();

        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

        string[] previews = { "\uD83D\uDC4B", "\uD83D\uDC4B\uD83C\uDFFB", "\uD83D\uDC4B\uD83C\uDFFC", "\uD83D\uDC4B\uD83C\uDFFD", "\uD83D\uDC4B\uD83C\uDFFE", "\uD83D\uDC4B\uD83C\uDFFF" };
        for (int i = 0; i < 6; i++)
        {
            var id = $"skin_{i}";
            var keyDef = new KeyDefinition { Label = previews[i], Output = "", SpecialAction = $"setSkinTone:{i}" };

            var isActive = i == _skinTone;
            var skinChild = new EmojiTextBlock
            {
                Text = previews[i],
                FontSize = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var container = new Border
            {
                Width = keySize * 1.5,
                Height = keySize * 1.5,
                Margin = new Thickness(4),
                Background = new SolidColorBrush(isActive ? KeyColorHighlight : KeyColorNormal),
                CornerRadius = new CornerRadius(8),
                Child = skinChild
            };

            _registerKeyVisual(id, keyDef, container, null!);
            rowPanel.Children.Add(container);
        }

        _keyRows.Children.Add(rowPanel);

        var backRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        AddSpecialButton(backRow, "Back", "skinToneBack", 2.0);
        _keyRows.Children.Add(backRow);
    }

    private void AddSpecialButton(StackPanel row, string text, string action, double widthMultiplier)
    {
        var keySize = _getKeySize();
        var id = $"special_{action}";
        var keyDef = new KeyDefinition { Label = text, Output = "", SpecialAction = action };

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
            Width = keySize * widthMultiplier + (widthMultiplier > 1 ? (widthMultiplier - 1) * 4 : 0),
            Height = keySize,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(KeyColorSwitch),
            CornerRadius = new CornerRadius(6),
            Child = label
        };

        _registerKeyVisual(id, keyDef, container, label);
        row.Children.Add(container);
    }

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
}
```

- [ ] **Step 2: Verify solution builds**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeds. `EmojiGridManager` compiles alongside the unchanged `KeyboardWindow`.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/EmojiGridManager.cs
git commit -m "refactor: extract EmojiGridManager from KeyboardWindow"
```

---

### Task 4: Rewrite KeyboardWindow to use KeyboardViewModel and EmojiGridManager

Replace the monolithic `KeyboardWindow` with the slimmed orchestrator version. This wires up the ViewModel and EmojiGridManager, removes all duplicated state, and eliminates `HandleSpecialKey`.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

- [ ] **Step 1: Replace KeyboardWindow.xaml.cs**

Replace the entire contents of `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` with:

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class KeyboardWindow : OverlayWindow
{
    private const double BaseKeySize = 70;

    private static readonly Color KeyColorNormal = Color.FromArgb(200, 50, 50, 50);
    private static readonly Color KeyColorSpecial = Color.FromArgb(200, 60, 60, 80);
    private static readonly Color KeyColorSwitch = Color.FromArgb(200, 50, 70, 110);
    private static readonly Color PredictionBarColor = Color.FromArgb(200, 40, 80, 40);

    private readonly DwellDetector _predictionDwellDetector = new();
    private readonly Dictionary<string, (Border Container, TextBlock Label)> _keyVisuals = new();
    private readonly Dictionary<string, KeyDefinition> _keyDefinitions = new();
    private readonly Dictionary<string, PhysicalPoint> _keyScreenPositions = new();

    private KeyboardViewModel? _viewModel;
    private EmojiGridManager? _emojiManager;
    private KeyboardSoundService? _soundService;

    private KeyboardLayout? _layout;
    private double _keySize;
    private int _predictionDwellMs;
    private string _screenEdge = "bottom";
    private bool _predictionTargetsInitialized;
    private string? _previousNearestKeyId;

    public KeyboardWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RepositionOnScreen();
        _predictionDwellDetector.TargetActivated += OnPredictionDwelled;
    }

    public void SetViewModel(KeyboardViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.KeyDwellCompleted += OnKeyDwellCompleted;

        _emojiManager = new EmojiGridManager(
            KeyRows,
            CategoryBar,
            RegisterKeyVisual,
            () => _keySize,
            ClearKeyState);
        _emojiManager.ExitRequested += () =>
        {
            _viewModel.IsEmojiMode = false;
            ExitEmojiMode();
        };
    }

    public void LoadLayout(KeyboardLayout layout, double keySizeMultiplier, int keyDwellMs, int predictionDwellMs, int cooldownMs)
    {
        _layout = layout;
        _keySize = BaseKeySize * keySizeMultiplier;
        _predictionDwellMs = predictionDwellMs;

        _predictionDwellDetector.SetCooldown(cooldownMs);

        _viewModel?.SetLayout(layout);
        _viewModel?.Configure(keyDwellMs, cooldownMs);

        _emojiManager?.SetKeyDwellMs(keyDwellMs);
        _emojiManager?.SetCooldown(cooldownMs);

        RebuildKeys();
    }

    public void SetPredictor(WordPredictor predictor) => _viewModel?.SetPredictor(predictor);

    public void SetScreenEdge(string edge)
    {
        _screenEdge = edge;
        RepositionOnScreen();
    }

    public void SetEmojiData(string emojiJsonPath, string recentEmojiPath)
    {
        _emojiManager?.SetEmojiData(emojiJsonPath, recentEmojiPath);
    }

    public void SetSoundService(KeyboardSoundService svc) => _soundService = svc;
    public void SetSkinTone(int skinTone) => _emojiManager?.SetSkinTone(skinTone);
    public int GetSkinTone() => _emojiManager?.SkinTone ?? 0;
    public RecentEmojiStore? GetRecentStore() => _emojiManager?.RecentStore;

    private void RepositionOnScreen()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var screen = SystemParameters.WorkArea;
        switch (_screenEdge.ToLowerInvariant())
        {
            case "top":
                Left = (screen.Width - ActualWidth) / 2;
                Top = 0;
                break;
            case "bottom":
            default:
                Left = (screen.Width - ActualWidth) / 2;
                Top = screen.Height - ActualHeight;
                break;
        }
    }

    private void RebuildKeys()
    {
        if (_layout == null || _viewModel == null) return;
        if (!_layout.SubLayouts.TryGetValue(_viewModel.ActiveSubLayoutId, out var subLayout)) return;

        ClearKeyState();
        KeyRows.Children.Clear();

        for (int rowIdx = 0; rowIdx < subLayout.Rows.Count; rowIdx++)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            for (int keyIdx = 0; keyIdx < subLayout.Rows[rowIdx].Count; keyIdx++)
            {
                var key = subLayout.Rows[rowIdx][keyIdx];
                var id = $"key_{rowIdx}_{keyIdx}";

                var label = new TextBlock
                {
                    Text = key.Label,
                    Foreground = Brushes.White,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var container = new Border
                {
                    Width = _keySize * key.Width + (key.Width > 1 ? (key.Width - 1) * 4 : 0),
                    Height = _keySize,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(GetKeyBaseColor(key)),
                    CornerRadius = new CornerRadius(6),
                    Child = label
                };

                System.Windows.Automation.AutomationProperties.SetAutomationId(label, $"Keyboard_Key_{id}");

                RegisterKeyVisual(id, key, container, label);
                rowPanel.Children.Add(container);
            }

            KeyRows.Children.Add(rowPanel);
        }
    }

    public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds)
    {
        if (_viewModel == null) return;

        UpdateKeyScreenPositions();

        if (_viewModel.IsEmojiMode)
        {
            _emojiManager?.UpdateCategoryTargets(screenPoint, timestampMicroseconds);
        }
        else
        {
            UpdatePredictionTargetPositions();
            _predictionDwellDetector.Update(screenPoint, timestampMicroseconds);
        }

        _viewModel.ProcessKeyGaze(_keyScreenPositions, screenPoint, IsGazeInsideKeyboard(screenPoint), timestampMicroseconds);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(KeyboardViewModel.NearestKeyId):
                if (_previousNearestKeyId != null)
                    ResetKeyVisual(_previousNearestKeyId);
                _previousNearestKeyId = _viewModel?.NearestKeyId;
                break;
            case nameof(KeyboardViewModel.NearestKeyProgress):
                if (_viewModel?.NearestKeyId != null)
                    UpdateKeyVisual(_viewModel.NearestKeyId, _viewModel.NearestKeyProgress);
                break;
            case nameof(KeyboardViewModel.IsShifted):
                UpdateShiftDisplay();
                break;
            case nameof(KeyboardViewModel.ActiveSubLayoutId):
                RebuildKeys();
                break;
        }
    }

    private void OnKeyDwellCompleted(string keyId)
    {
        _soundService?.PlayKeyClick();
        if (!_keyDefinitions.TryGetValue(keyId, out var key) || _viewModel == null) return;

        if (string.IsNullOrEmpty(key.SpecialAction))
        {
            // Compute actual output before ActivateKey changes IsShifted
            var output = _viewModel.IsShifted && !string.IsNullOrEmpty(key.ShiftOutput)
                ? key.ShiftOutput
                : key.Output;

            _viewModel.ActivateKey(key.Output, key.ShiftOutput);

            if (_viewModel.IsEmojiMode)
                _emojiManager?.TrackEmojiInRecents(output);

            UpdatePredictions();
            return;
        }

        var action = key.SpecialAction;
        var lowerAction = action.ToLowerInvariant();

        // Mode switching
        if (lowerAction == "switchemoji")
        {
            _viewModel.IsEmojiMode = true;
            EnterEmojiMode();
            return;
        }

        // Text-domain actions
        if (lowerAction == "shift")
        {
            _viewModel.IsShifted = !_viewModel.IsShifted;
            return;
        }

        if (lowerAction == "backspace")
        {
            _viewModel.HandleBackspace();
            UpdatePredictions();
            return;
        }

        if (lowerAction == "enter")
        {
            _viewModel.HandleEnter();
            UpdatePredictions();
            return;
        }

        if (lowerAction == "tab")
        {
            _viewModel.HandleTab();
            return;
        }

        if (lowerAction == "space")
        {
            _viewModel.HandleSpace();
            UpdatePredictions();
            return;
        }

        if (lowerAction.StartsWith("switchsub:"))
        {
            _viewModel.SwitchSubLayout(action[10..]);
            return;
        }

        // Emoji-domain actions — delegate to manager
        _emojiManager?.HandleAction(action);
    }

    private void EnterEmojiMode()
    {
        PredictionBar.Visibility = Visibility.Collapsed;
        _emojiManager?.Enter();
    }

    private void ExitEmojiMode()
    {
        PredictionBar.Visibility = Visibility.Visible;
        RebuildKeys();
    }

    private void UpdateKeyScreenPositions()
    {
        foreach (var (id, (container, _)) in _keyVisuals)
        {
            try
            {
                var pos = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
                _keyScreenPositions[id] = new PhysicalPoint(pos.X, pos.Y);
            }
            catch (InvalidOperationException)
            {
                // Window not yet rendered or element not in visual tree
            }
        }
    }

    private bool IsGazeInsideKeyboard(PhysicalPoint gazePoint)
    {
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            return gazePoint.X >= topLeft.X && gazePoint.X <= topLeft.X + ActualWidth
                && gazePoint.Y >= topLeft.Y && gazePoint.Y <= topLeft.Y + ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void UpdatePredictionTargetPositions()
    {
        if (PredictionBar.Children.Count == 0) return;

        for (int i = 0; i < PredictionBar.Children.Count; i++)
        {
            var predId = $"pred_{i}";
            if (PredictionBar.Children[i] is not Border btn) continue;

            try
            {
                var pos = btn.PointToScreen(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));
                if (!_predictionTargetsInitialized)
                {
                    _predictionDwellDetector.RemoveTarget(predId);
                    _predictionDwellDetector.AddTarget(new DwellTarget(predId, new PhysicalPoint(pos.X, pos.Y), DpiHelper.LogicalToPhysicalX(40), _predictionDwellMs));
                }
                else
                {
                    _predictionDwellDetector.UpdateTargetCenter(predId, new PhysicalPoint(pos.X, pos.Y));
                }
            }
            catch
            {
                // Not yet rendered
            }
        }
        _predictionTargetsInitialized = true;
    }

    public void UpdatePredictions()
    {
        if (_viewModel == null) return;

        _predictionDwellDetector.ClearTargets();
        _predictionTargetsInitialized = false;
        PredictionBar.Children.Clear();

        var suggestions = _viewModel.GetSuggestions();

        for (int i = 0; i < suggestions.Count; i++)
        {
            var word = suggestions[i];
            var predId = $"pred_{i}";

            var predLabel = new TextBlock
            {
                Text = word,
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(predLabel, $"Keyboard_Prediction_{i}");

            var btn = new Border
            {
                MinWidth = 80,
                Height = 50,
                Margin = new Thickness(3),
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(PredictionBarColor),
                CornerRadius = new CornerRadius(4),
                Child = predLabel
            };

            PredictionBar.Children.Add(btn);
            _predictionDwellDetector.AddTarget(new DwellTarget(predId, new PhysicalPoint(0, 0), DpiHelper.LogicalToPhysicalX(40), _predictionDwellMs));
        }
    }

    private void OnPredictionDwelled(string targetId)
    {
        if (_viewModel == null || !targetId.StartsWith("pred_")) return;

        if (int.TryParse(targetId.AsSpan(5), out var index))
        {
            var suggestions = _viewModel.GetSuggestions();
            if (index < suggestions.Count)
            {
                _viewModel.HandlePredictionSelected(suggestions[index]);
                UpdatePredictions();
            }
        }
    }

    private void UpdateKeyVisual(string targetId, double progress)
    {
        if (!_keyVisuals.TryGetValue(targetId, out var visuals)) return;

        var baseColor = GetKeyBaseColor(targetId);
        var highlightColor = Color.FromArgb(200, 0, (byte)(100 + 155 * progress), 0);
        var blended = Color.FromArgb(
            200,
            (byte)(baseColor.R * (1 - progress) + highlightColor.R * progress),
            (byte)(baseColor.G * (1 - progress) + highlightColor.G * progress),
            (byte)(baseColor.B * (1 - progress) + highlightColor.B * progress));

        visuals.Container.Background = new SolidColorBrush(blended);
    }

    private void ResetKeyVisual(string targetId)
    {
        if (!_keyVisuals.TryGetValue(targetId, out var visuals)) return;
        visuals.Container.Background = new SolidColorBrush(GetKeyBaseColor(targetId));
    }

    private Color GetKeyBaseColor(string keyId)
    {
        var key = _keyDefinitions.GetValueOrDefault(keyId);
        return key != null ? GetKeyBaseColor(key) : KeyColorNormal;
    }

    private static Color GetKeyBaseColor(KeyDefinition key)
    {
        var specialAction = key.SpecialAction;
        var isSwitchSub = specialAction?.StartsWith("switchSub:") ?? false;
        var isSpecial = !string.IsNullOrEmpty(specialAction);
        return isSwitchSub ? KeyColorSwitch : isSpecial ? KeyColorSpecial : KeyColorNormal;
    }

    private void UpdateShiftDisplay()
    {
        if (_viewModel == null) return;
        foreach (var (id, (_, label)) in _keyVisuals)
        {
            if (label == null) continue;
            if (_keyDefinitions.TryGetValue(id, out var key) && string.IsNullOrEmpty(key.SpecialAction))
            {
                label.Text = _viewModel.IsShifted && !string.IsNullOrEmpty(key.ShiftOutput)
                    ? key.ShiftOutput
                    : key.Label;
            }
        }
    }

    private void RegisterKeyVisual(string id, KeyDefinition keyDef, Border container, TextBlock? label)
    {
        _keyDefinitions[id] = keyDef;
        _keyVisuals[id] = (container, label!);
    }

    private void ClearKeyState()
    {
        _keyVisuals.Clear();
        _keyDefinitions.Clear();
        _keyScreenPositions.Clear();
    }
}
```

- [ ] **Step 2: Verify solution builds**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeds. The new `KeyboardWindow` uses `SetViewModel()` which needs to be called by the wiring code — we fix that in the next step.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "refactor: rewrite KeyboardWindow as thin orchestrator using ViewModel and EmojiGridManager"
```

---

### Task 5: Wire SetViewModel in the application startup

The new `KeyboardWindow` requires `SetViewModel()` to be called before `LoadLayout()`. Update the overlay manager to pass the ViewModel through.

**Files:**
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs` (find where `Keyboard.DataContext = viewModels.Keyboard` is set)

- [ ] **Step 1: Read the current OverlayManager initialization code**

Read `src/EyeCtrl.App/Services/OverlayManager.cs` to find where KeyboardWindow is initialized. Look for the line `Keyboard.DataContext = viewModels.Keyboard;` (line ~66 from earlier grep).

- [ ] **Step 2: Add SetViewModel call after DataContext assignment**

After the line `Keyboard.DataContext = viewModels.Keyboard;`, add:

```csharp
Keyboard.SetViewModel(viewModels.Keyboard);
```

- [ ] **Step 3: Verify solution builds**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeds.

- [ ] **Step 4: Run all tests**

Run: `dotnet test EyeCtrl.slnx -v n`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/OverlayManager.cs
git commit -m "refactor: wire KeyboardViewModel into KeyboardWindow via SetViewModel"
```

---

### Task 6: Clean up dead code and verify end-to-end

Remove the `UIConstants.EmojisPerPage` constant (now only used in `EmojiGridManager` which has its own), remove any dead references in the existing `KeyboardViewModel` tests, and verify the entire solution builds and passes.

**Files:**
- Modify: `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs` (if any cleanup needed)

- [ ] **Step 1: Check for `UIConstants.EmojisPerPage` usage**

Search for `UIConstants.EmojisPerPage` across the codebase. If it's only used in old `KeyboardViewModel` code (which we removed), consider whether `UIConstants` still needs it.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test EyeCtrl.slnx -v n`
Expected: All tests pass.

- [ ] **Step 3: Verify the solution builds in Release mode**

Run: `dotnet build EyeCtrl.slnx -c Release`
Expected: Build succeeds with no warnings related to our changes.

- [ ] **Step 4: Commit if any cleanup was needed**

```bash
git add -A
git commit -m "refactor: clean up dead code from keyboard refactoring"
```

---

### Task 7: Manual smoke test

Launch the app and verify all keyboard functionality works identically to before.

- [ ] **Step 1: Run the app**

Run: `dotnet run --project src/EyeCtrl.App`
(Or if published: run the published executable)

- [ ] **Step 2: Verify text keyboard**

Test checklist:
- Keyboard appears when triggered
- Keys highlight on gaze dwell
- Key activation works (typing characters)
- Shift toggles and auto-unshifts after typing a letter
- Backspace, Enter, Space, Tab all work
- Sub-layout switching works (numbers/symbols)
- Word predictions appear and can be selected via dwell
- Keyboard click sound plays

- [ ] **Step 3: Verify emoji mode**

Test checklist:
- Emoji mode activates via the emoji key
- Category tabs appear and can be switched via dwell
- Emoji grid displays correctly
- Pagination works (next/prev page)
- Skin tone selector appears and applies
- "ABC" button returns to text mode
- Selected emojis appear in recent category

- [ ] **Step 4: Verify screen positioning**

Test checklist:
- Keyboard positions at bottom edge (default)
- Keyboard positions at top edge (if configured)

- [ ] **Step 5: Note any issues found**

If any issues are found, fix them and add a regression test before committing.
