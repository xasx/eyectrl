# Keyboard Modifier Keys Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Ctrl, Alt, Win, and Caps Lock modifier keys to the on-screen keyboard with visual highlighting, combinable sticky/toggle behavior, and VK-code-based key combination execution.

**Architecture:** Extend the existing `SpecialAction` string convention with `modifier:X` and `toggle:X` prefixes. Track active modifiers as a `HashSet<ModifierKey>` in `KeyboardViewModel`. Route modified key presses through a new `SendModifiedKeyPress` method in `Win32InputSimulator` that wraps keystrokes in modifier key-down/key-up sequences. Visual highlighting uses a solid accent color on active modifier keys.

**Tech Stack:** C# / .NET 8 / WPF, Win32 SendInput P/Invoke, xUnit

---

### File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `src/EyeCtrl.Core/Keyboard/ModifierKey.cs` | Create | Enum: Shift, Ctrl, Alt, Win, CapsLock |
| `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` | Modify | Replace `IsShifted` with `ActiveModifiers` HashSet, modifier activation/clearing logic |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Modify | Add `ActiveModifiers` to `KeyActivatedMessage` |
| `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs` | Modify | Add `SendModifiedKeyPress`, `VkKeyScanW` P/Invoke |
| `src/EyeCtrl.Platform/Input/NativeMethods.cs` | Modify | Add `VkKeyScanW` import |
| `src/EyeCtrl.Core/Interfaces/IInputSimulator.cs` | Modify | Add `SendModifiedKeyPress` to interface |
| `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` | Modify | Modifier highlight rendering, handle new specialAction values |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Modify | Route modified key presses through `SendModifiedKeyPress` |
| `src/EyeCtrl.App/Resources/Layouts/qwerty.json` | Modify | Add modifier column + Shift to all sub-layouts |
| `src/EyeCtrl.App/Resources/Layouts/azerty.json` | Modify | Same |
| `src/EyeCtrl.App/Resources/Layouts/qwertz.json` | Modify | Same |
| `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs` | Modify | Update Shift tests, add modifier combination tests |

---

### Task 1: ModifierKey Enum

**Files:**
- Create: `src/EyeCtrl.Core/Keyboard/ModifierKey.cs`

- [ ] **Step 1: Create ModifierKey enum**

```csharp
namespace EyeCtrl.Core.Keyboard;

public enum ModifierKey
{
    Shift,
    Ctrl,
    Alt,
    Win,
    CapsLock
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Keyboard/ModifierKey.cs
git commit -m "feat: add ModifierKey enum"
```

---

### Task 2: KeyboardViewModel — Replace IsShifted with ActiveModifiers

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs`

- [ ] **Step 1: Write failing tests for modifier state management**

Add these tests to `KeyboardViewModelTests.cs`. Replace the existing `IsShifted_CanBeToggled` test:

```csharp
[Fact]
public void ToggleModifier_AddsAndRemovesFromActiveModifiers()
{
    var vm = CreateViewModel();
    Assert.Empty(vm.ActiveModifiers);

    vm.ToggleModifier(ModifierKey.Ctrl);
    Assert.Contains(ModifierKey.Ctrl, vm.ActiveModifiers);

    vm.ToggleModifier(ModifierKey.Ctrl);
    Assert.DoesNotContain(ModifierKey.Ctrl, vm.ActiveModifiers);
}

[Fact]
public void ToggleModifier_MultipleCombine()
{
    var vm = CreateViewModel();

    vm.ToggleModifier(ModifierKey.Ctrl);
    vm.ToggleModifier(ModifierKey.Shift);
    Assert.Contains(ModifierKey.Ctrl, vm.ActiveModifiers);
    Assert.Contains(ModifierKey.Shift, vm.ActiveModifiers);
}

[Fact]
public void IsShifted_TrueWhenShiftActive()
{
    var vm = CreateViewModel();
    Assert.False(vm.IsShifted);

    vm.ToggleModifier(ModifierKey.Shift);
    Assert.True(vm.IsShifted);
}

[Fact]
public void IsShifted_TrueWhenCapsLockActive()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.CapsLock);
    Assert.True(vm.IsShifted);
}

[Fact]
public void ActivateKey_ClearsOneShotModifiers()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.Ctrl);
    vm.ToggleModifier(ModifierKey.Shift);
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.ActivateKey("c", shiftOutput: "C");

    Assert.Empty(vm.ActiveModifiers);
}

[Fact]
public void ActivateKey_CapsLockSurvivesClearing()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.CapsLock);
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.ActivateKey("a", shiftOutput: "A");

    Assert.Equal("A", sentKey);
    Assert.Contains(ModifierKey.CapsLock, vm.ActiveModifiers);
}

[Fact]
public void ActivateKey_OneShotShiftCleared()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.Shift);
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.ActivateKey("a", shiftOutput: "A");

    Assert.Equal("A", sentKey);
    Assert.DoesNotContain(ModifierKey.Shift, vm.ActiveModifiers);
}

[Fact]
public void HandleBackspace_ClearsOneShotModifiers()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.Ctrl);
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

    vm.HandleBackspace();

    Assert.Empty(vm.ActiveModifiers);
}

[Fact]
public void HandleEnter_ClearsOneShotModifiers()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.Ctrl);
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

    vm.HandleEnter();

    Assert.Empty(vm.ActiveModifiers);
}

[Fact]
public void HandleSpace_ClearsOneShotModifiers()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.Ctrl);
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

    vm.HandleSpace();

    Assert.Empty(vm.ActiveModifiers);
}

[Fact]
public void HandleTab_ClearsOneShotModifiers()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.Ctrl);
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

    vm.HandleTab();

    Assert.Empty(vm.ActiveModifiers);
}

[Fact]
public void SwitchSubLayout_DoesNotClearModifiers()
{
    var vm = CreateViewModel();
    var layout = new KeyboardLayout
    {
        Name = "Test",
        DefaultSubLayout = "letters",
        SubLayouts = new()
        {
            ["letters"] = new SubLayout { Label = "ABC", Rows = new() },
            ["numbers"] = new SubLayout { Label = "123", Rows = new() }
        }
    };
    vm.SetLayout(layout);

    vm.ToggleModifier(ModifierKey.Ctrl);
    vm.SwitchSubLayout("numbers");

    Assert.Contains(ModifierKey.Ctrl, vm.ActiveModifiers);
}
```

Add the required using at the top of the test file:

```csharp
using EyeCtrl.Core.Keyboard;
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests/ --filter "ToggleModifier|IsShifted_TrueWhen|ClearsOneShot|CapsLockSurvives|OneShotShiftCleared|DoesNotClearModifiers"`
Expected: FAIL — `ToggleModifier` method and `ActiveModifiers` property don't exist

- [ ] **Step 3: Implement ActiveModifiers in KeyboardViewModel**

In `KeyboardViewModel.cs`, make these changes:

1. Add using at top:
```csharp
using EyeCtrl.Core.Keyboard;
```

2. Replace the `IsShifted` observable property with:
```csharp
    private readonly HashSet<ModifierKey> _activeModifiers = new();

    public IReadOnlySet<ModifierKey> ActiveModifiers => _activeModifiers;

    public bool IsShifted => _activeModifiers.Contains(ModifierKey.Shift)
                          || _activeModifiers.Contains(ModifierKey.CapsLock);
```

3. Add `ToggleModifier` method:
```csharp
    public void ToggleModifier(ModifierKey modifier)
    {
        if (!_activeModifiers.Remove(modifier))
            _activeModifiers.Add(modifier);
        OnPropertyChanged(nameof(ActiveModifiers));
        OnPropertyChanged(nameof(IsShifted));
    }
```

4. Add `ClearOneShotModifiers` private method:
```csharp
    private void ClearOneShotModifiers()
    {
        _activeModifiers.Remove(ModifierKey.Shift);
        _activeModifiers.Remove(ModifierKey.Ctrl);
        _activeModifiers.Remove(ModifierKey.Alt);
        _activeModifiers.Remove(ModifierKey.Win);
        OnPropertyChanged(nameof(ActiveModifiers));
        OnPropertyChanged(nameof(IsShifted));
    }
```

5. Update `ActivateKey` to use `ActiveModifiers` and clear one-shot modifiers:
```csharp
    public void ActivateKey(string output, string? shiftOutput)
    {
        var actualOutput = IsShifted && !string.IsNullOrEmpty(shiftOutput)
            ? shiftOutput
            : output;

        CurrentInput += actualOutput;
        _predictor?.LearnWord(actualOutput);
        _messenger.Send(new KeyActivatedMessage(actualOutput, new HashSet<ModifierKey>(_activeModifiers)));
        ClearOneShotModifiers();
    }
```

6. Update `HandleBackspace` to clear one-shot modifiers:
```csharp
    public void HandleBackspace()
    {
        if (CurrentInput.Length > 0)
            CurrentInput = CurrentInput[..^1];
        _messenger.Send(new KeyActivatedMessage("\b", new HashSet<ModifierKey>(_activeModifiers)));
        ClearOneShotModifiers();
    }
```

7. Update `HandleEnter`:
```csharp
    public void HandleEnter()
    {
        CurrentInput = "";
        _messenger.Send(new KeyActivatedMessage("\n", new HashSet<ModifierKey>(_activeModifiers)));
        ClearOneShotModifiers();
    }
```

8. Update `HandleSpace`:
```csharp
    public void HandleSpace()
    {
        if (CurrentInput.Length > 0)
            _predictor?.LearnWord(CurrentInput);
        CurrentInput = "";
        _messenger.Send(new KeyActivatedMessage(" ", new HashSet<ModifierKey>(_activeModifiers)));
        ClearOneShotModifiers();
    }
```

9. Update `HandleTab`:
```csharp
    public void HandleTab()
    {
        _messenger.Send(new KeyActivatedMessage("\t", new HashSet<ModifierKey>(_activeModifiers)));
        ClearOneShotModifiers();
    }
```

10. Update `SwitchSubLayout` — remove the `IsShifted = false` line (modifiers persist across sub-layout switches):
```csharp
    public void SwitchSubLayout(string subLayoutId)
    {
        if (_layout == null || !_layout.SubLayouts.ContainsKey(subLayoutId)) return;
        if (ActiveSubLayoutId == subLayoutId) return;
        ActiveSubLayoutId = subLayoutId;
    }
```

- [ ] **Step 4: Update KeyActivatedMessage to carry modifiers**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, change:

```csharp
public record KeyActivatedMessage(string Key);
```

to:

```csharp
public record KeyActivatedMessage(string Key, IReadOnlySet<ModifierKey>? Modifiers = null);
```

Add using at top:
```csharp
using EyeCtrl.Core.Keyboard;
```

- [ ] **Step 5: Fix existing tests that set IsShifted directly**

In `KeyboardViewModelTests.cs`, update the old tests that set `vm.IsShifted = true` to use `vm.ToggleModifier(ModifierKey.Shift)` instead:

Replace `ActivateKey_UsesShiftOutputWhenShifted`:
```csharp
[Fact]
public void ActivateKey_UsesShiftOutputWhenShifted()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.Shift);
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.ActivateKey("a", shiftOutput: "A");

    Assert.Equal("A", sentKey);
    Assert.Equal("A", vm.CurrentInput);
    Assert.False(vm.IsShifted); // one-shot cleared
}
```

Replace `ActivateKey_NoAutoUnshiftWhenNoShiftOutput` — Shift is now always cleared (one-shot behavior), so this test changes:
```csharp
[Fact]
public void ActivateKey_ShiftAlwaysClearedOneShot()
{
    var vm = CreateViewModel();
    vm.ToggleModifier(ModifierKey.Shift);
    string? sentKey = null;
    _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

    vm.ActivateKey("1", shiftOutput: null);

    Assert.Equal("1", sentKey);
    Assert.False(vm.IsShifted); // one-shot: always cleared
}
```

Remove `IsShifted_CanBeToggled` test (replaced by `ToggleModifier_AddsAndRemovesFromActiveModifiers`).

- [ ] **Step 6: Run all tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests/`
Expected: All tests pass

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs
git commit -m "feat: replace IsShifted with ActiveModifiers HashSet in KeyboardViewModel"
```

---

### Task 3: KeyActivatedMessage Modifiers — Update EventCoordinator

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Modify: `src/EyeCtrl.Core/Interfaces/IInputSimulator.cs`
- Modify: `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs`
- Modify: `src/EyeCtrl.Platform/Input/NativeMethods.cs`

- [ ] **Step 1: Add VkKeyScanW to NativeMethods**

In `src/EyeCtrl.Platform/Input/NativeMethods.cs`, add after the `GetSystemMetrics` import:

```csharp
    [LibraryImport("user32.dll")]
    internal static partial short VkKeyScanW(char ch);
```

- [ ] **Step 2: Add VK constants for modifier keys to NativeMethods**

In `NativeMethods.cs`, add after the `KEYEVENTF_UNICODE` constant:

```csharp
    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_MENU = 0x12;    // Alt
    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_BACK = 0x08;
    internal const ushort VK_TAB = 0x09;
    internal const ushort VK_RETURN = 0x0D;
    internal const ushort VK_SPACE = 0x20;
```

- [ ] **Step 3: Add SendModifiedKeyPress to IInputSimulator**

In `src/EyeCtrl.Core/Interfaces/IInputSimulator.cs`, add:

```csharp
using EyeCtrl.Core.Keyboard;
```

And add to the interface:

```csharp
    void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode);
```

- [ ] **Step 4: Implement SendModifiedKeyPress in Win32InputSimulator**

In `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs`, add using:

```csharp
using EyeCtrl.Core.Keyboard;
```

Add this method:

```csharp
    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode)
    {
        // Build list of modifier VK codes to press
        var modifierVks = new List<ushort>();
        if (modifiers.Contains(ModifierKey.Ctrl))
            modifierVks.Add(NativeMethods.VK_CONTROL);
        if (modifiers.Contains(ModifierKey.Alt))
            modifierVks.Add(NativeMethods.VK_MENU);
        if (modifiers.Contains(ModifierKey.Win))
            modifierVks.Add(NativeMethods.VK_LWIN);
        if (modifiers.Contains(ModifierKey.Shift) || modifiers.Contains(ModifierKey.CapsLock))
            modifierVks.Add(NativeMethods.VK_SHIFT);

        // Build input array: modifier downs + key down + key up + modifier ups (reverse)
        var inputs = new NativeMethods.INPUT[modifierVks.Count * 2 + 2];
        int idx = 0;

        // Modifier key-downs
        foreach (var vk in modifierVks)
            inputs[idx++] = CreateKeyboardInput(vk, 0);

        // Main key
        inputs[idx++] = CreateKeyboardInput(virtualKeyCode, 0);
        inputs[idx++] = CreateKeyboardInput(virtualKeyCode, NativeMethods.KEYEVENTF_KEYUP);

        // Modifier key-ups (reverse order)
        for (int i = modifierVks.Count - 1; i >= 0; i--)
            inputs[idx++] = CreateKeyboardInput(modifierVks[i], NativeMethods.KEYEVENTF_KEYUP);

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>
    /// Convert a character to its virtual key code using the current keyboard layout.
    /// Returns 0 if the character cannot be mapped.
    /// </summary>
    public static ushort CharToVirtualKey(char c)
    {
        short result = NativeMethods.VkKeyScanW(c);
        if (result == -1) return 0;
        return (ushort)(result & 0xFF); // low byte is the VK code
    }
```

- [ ] **Step 5: Update EventCoordinator to route modified key presses**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, update the message registration (around line 174):

```csharp
        _context.Messenger.Register<EventCoordinator, KeyActivatedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() => HandleKeyActivation(msg.Key, msg.Modifiers)));
```

Replace `HandleKeyActivation` method:

```csharp
    private void HandleKeyActivation(string key, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (_context == null) return;

        var hasSystemModifiers = modifiers != null &&
            (modifiers.Contains(ModifierKey.Ctrl) || modifiers.Contains(ModifierKey.Alt) || modifiers.Contains(ModifierKey.Win));

        if (hasSystemModifiers)
        {
            // VK code path: wrap keystroke in modifier down/up
            ushort vk = key switch
            {
                "\b" => NativeMethods.VK_BACK,
                "\n" => NativeMethods.VK_RETURN,
                "\t" => NativeMethods.VK_TAB,
                " " => NativeMethods.VK_SPACE,
                _ => key.Length == 1 ? Win32InputSimulator.CharToVirtualKey(key[0]) : (ushort)0
            };

            if (vk != 0)
                _context.InputSimulator.SendModifiedKeyPress(modifiers!, vk);
        }
        else
        {
            // Simple path: no system modifiers, just send text or VK
            switch (key)
            {
                case "\b": _context.InputSimulator.SendKeyPress(BackspaceVirtualKeyCode); break;
                case "\n": _context.InputSimulator.SendKeyPress(EnterVirtualKeyCode); break;
                case "\t": _context.InputSimulator.SendKeyPress(TabVirtualKeyCode); break;
                default: _context.InputSimulator.SendText(key); break;
            }
        }
    }
```

Add using at top of EventCoordinator.cs:

```csharp
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Platform.Input;
```

Note: `NativeMethods` is internal to the Platform project. To use `NativeMethods.VK_BACK` etc. from EventCoordinator, define the VK constants locally in EventCoordinator or expose them via `Win32InputSimulator`. The simplest approach: use the `Win32InputSimulator.CharToVirtualKey` for character keys, and define local constants in EventCoordinator for special keys. The existing `BackspaceVirtualKeyCode`, `EnterVirtualKeyCode`, `TabVirtualKeyCode` constants already exist. Add:

```csharp
    private const ushort SpaceVirtualKeyCode = 0x20;
```

And update the VK code path to use these local constants instead of `NativeMethods`:

```csharp
            ushort vk = key switch
            {
                "\b" => (ushort)BackspaceVirtualKeyCode,
                "\n" => (ushort)EnterVirtualKeyCode,
                "\t" => (ushort)TabVirtualKeyCode,
                " " => SpaceVirtualKeyCode,
                _ => key.Length == 1 ? Win32InputSimulator.CharToVirtualKey(key[0]) : (ushort)0
            };
```

Remove the `using EyeCtrl.Platform.Input;` — only `using EyeCtrl.Core.Keyboard;` is needed since `CharToVirtualKey` is a public static method on `Win32InputSimulator` which is already available via the `IInputSimulator` reference. Actually, `CharToVirtualKey` is on the concrete class. Make it a standalone static utility or add it to the interface. Simpler: just add `CharToVirtualKey` as a static method on `Win32InputSimulator` and reference the concrete type. Since `EventCoordinator` already references the Platform project (it creates `Win32InputSimulator`), this is fine.

- [ ] **Step 6: Build the solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded

- [ ] **Step 7: Run all tests**

Run: `dotnet test EyeCtrl.slnx`
Expected: All tests pass

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Platform/Input/NativeMethods.cs src/EyeCtrl.Platform/Input/Win32InputSimulator.cs src/EyeCtrl.Core/Interfaces/IInputSimulator.cs src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat: add SendModifiedKeyPress and route modifier key combinations"
```

---

### Task 4: KeyboardWindow — Handle Modifier SpecialActions and Visual Highlighting

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

- [ ] **Step 1: Add modifier highlight color constant**

In `KeyboardWindow.xaml.cs`, add after the existing color constants:

```csharp
    private static readonly Color KeyColorModifierActive = Color.FromArgb(230, 80, 180, 255);
```

- [ ] **Step 2: Add using for ModifierKey**

```csharp
using EyeCtrl.Core.Keyboard;
```

- [ ] **Step 3: Update OnKeyDwellCompleted to handle modifier and toggle actions**

Replace the `"shift"` handling block in `OnKeyDwellCompleted` with handling for all modifier/toggle patterns:

```csharp
        // Modifier keys
        if (lowerAction == "shift" || lowerAction == "modifier:shift")
        {
            _viewModel.ToggleModifier(ModifierKey.Shift);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "modifier:ctrl")
        {
            _viewModel.ToggleModifier(ModifierKey.Ctrl);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "modifier:alt")
        {
            _viewModel.ToggleModifier(ModifierKey.Alt);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "modifier:win")
        {
            _viewModel.ToggleModifier(ModifierKey.Win);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "toggle:capslock")
        {
            _viewModel.ToggleModifier(ModifierKey.CapsLock);
            UpdateModifierVisuals();
            return;
        }
```

- [ ] **Step 4: Update the firing-key handlers to refresh modifier visuals after clearing**

After each call to `_viewModel.ActivateKey`, `_viewModel.HandleBackspace`, `_viewModel.HandleEnter`, `_viewModel.HandleSpace`, and `_viewModel.HandleTab` in `OnKeyDwellCompleted`, add `UpdateModifierVisuals();`. For example, update the normal key block:

```csharp
        if (string.IsNullOrEmpty(key.SpecialAction))
        {
            var output = _viewModel.IsShifted && !string.IsNullOrEmpty(key.ShiftOutput)
                ? key.ShiftOutput
                : key.Output;

            _viewModel.ActivateKey(key.Output, key.ShiftOutput);
            UpdateModifierVisuals();

            if (_viewModel.IsEmojiMode)
                _emojiManager?.TrackEmojiInRecents(output);

            UpdatePredictions();
            return;
        }
```

And for backspace:
```csharp
        if (lowerAction == "backspace")
        {
            _viewModel.HandleBackspace();
            UpdateModifierVisuals();
            UpdatePredictions();
            return;
        }
```

And for enter:
```csharp
        if (lowerAction == "enter")
        {
            _viewModel.HandleEnter();
            UpdateModifierVisuals();
            UpdatePredictions();
            return;
        }
```

And for tab:
```csharp
        if (lowerAction == "tab")
        {
            _viewModel.HandleTab();
            UpdateModifierVisuals();
            return;
        }
```

And for space:
```csharp
        if (lowerAction == "space")
        {
            _viewModel.HandleSpace();
            UpdateModifierVisuals();
            UpdatePredictions();
            return;
        }
```

- [ ] **Step 5: Add UpdateModifierVisuals method**

```csharp
    private void UpdateModifierVisuals()
    {
        if (_viewModel == null) return;

        foreach (var (id, (container, _)) in _keyVisuals)
        {
            if (!_keyDefinitions.TryGetValue(id, out var key)) continue;
            var action = key.SpecialAction?.ToLowerInvariant();
            if (action == null) continue;

            ModifierKey? modifier = action switch
            {
                "shift" or "modifier:shift" => ModifierKey.Shift,
                "modifier:ctrl" => ModifierKey.Ctrl,
                "modifier:alt" => ModifierKey.Alt,
                "modifier:win" => ModifierKey.Win,
                "toggle:capslock" => ModifierKey.CapsLock,
                _ => null
            };

            if (modifier == null) continue;

            var isActive = _viewModel.ActiveModifiers.Contains(modifier.Value);
            container.Background = new SolidColorBrush(isActive ? KeyColorModifierActive : GetKeyBaseColor(key));
        }

        UpdateShiftDisplay();
    }
```

- [ ] **Step 6: Update GetKeyBaseColor to classify modifier keys as special**

The existing `GetKeyBaseColor(KeyDefinition)` already returns `KeyColorSpecial` for any key with a non-empty `SpecialAction`. The new modifier/toggle actions will automatically get that base color. No change needed.

- [ ] **Step 7: Update OnViewModelPropertyChanged to handle ActiveModifiers**

Add a case for `ActiveModifiers` in the property changed handler:

```csharp
            case nameof(KeyboardViewModel.ActiveModifiers):
                UpdateModifierVisuals();
                break;
```

Remove the existing `case nameof(KeyboardViewModel.IsShifted):` since `UpdateModifierVisuals` already calls `UpdateShiftDisplay`.

- [ ] **Step 8: Update ResetKeyVisual to respect active modifier state**

When a modifier key's dwell progress resets (gaze moves away), it should show the active highlight if the modifier is still active, not the default base color:

```csharp
    private void ResetKeyVisual(string targetId)
    {
        if (!_keyVisuals.TryGetValue(targetId, out var visuals)) return;

        if (_viewModel != null && _keyDefinitions.TryGetValue(targetId, out var key))
        {
            var action = key.SpecialAction?.ToLowerInvariant();
            ModifierKey? modifier = action switch
            {
                "shift" or "modifier:shift" => ModifierKey.Shift,
                "modifier:ctrl" => ModifierKey.Ctrl,
                "modifier:alt" => ModifierKey.Alt,
                "modifier:win" => ModifierKey.Win,
                "toggle:capslock" => ModifierKey.CapsLock,
                _ => null
            };

            if (modifier != null && _viewModel.ActiveModifiers.Contains(modifier.Value))
            {
                visuals.Container.Background = new SolidColorBrush(KeyColorModifierActive);
                return;
            }
        }

        visuals.Container.Background = new SolidColorBrush(GetKeyBaseColor(targetId));
    }
```

- [ ] **Step 9: Build and run tests**

Run: `dotnet build EyeCtrl.slnx && dotnet test EyeCtrl.slnx`
Expected: Build succeeded, all tests pass

- [ ] **Step 10: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: handle modifier specialActions and visual highlighting in KeyboardWindow"
```

---

### Task 5: Update JSON Layouts — QWERTY

**Files:**
- Modify: `src/EyeCtrl.App/Resources/Layouts/qwerty.json`

- [ ] **Step 1: Update letters sub-layout**

Add modifier column (Win, Caps, Alt, Ctrl) as first key in each row. Change `"shift"` to `"modifier:shift"`. Add Shift to row 3 after Alt:

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, change existing Shift's specialAction from `"shift"` to `"modifier:shift"`
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 2: Update numbers sub-layout**

Add modifier column as first key in each row. Add Shift in row 3:

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, then `{ "label": "Shift", "output": "", "width": 1.2, "specialAction": "modifier:shift" }` (before the existing keys)
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 3: Update symbols sub-layout**

Same pattern as numbers: modifier column on left, Shift in row 3.

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, then `{ "label": "Shift", "output": "", "width": 1.2, "specialAction": "modifier:shift" }`
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 4: Verify JSON is valid**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded (layout is loaded at runtime, but build verifies no project-level issues)

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Resources/Layouts/qwerty.json
git commit -m "feat: add modifier keys to QWERTY layout"
```

---

### Task 6: Update JSON Layouts — AZERTY

**Files:**
- Modify: `src/EyeCtrl.App/Resources/Layouts/azerty.json`

- [ ] **Step 1: Update letters sub-layout**

Same modifier column pattern. Change `"shift"` to `"modifier:shift"`.

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, change existing Shift's specialAction from `"shift"` to `"modifier:shift"`
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 2: Update numbers sub-layout**

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, then `{ "label": "Shift", "output": "", "width": 1.2, "specialAction": "modifier:shift" }`
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 3: Update symbols sub-layout**

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, then `{ "label": "Shift", "output": "", "width": 1.2, "specialAction": "modifier:shift" }`
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Resources/Layouts/azerty.json
git commit -m "feat: add modifier keys to AZERTY layout"
```

---

### Task 7: Update JSON Layouts — QWERTZ

**Files:**
- Modify: `src/EyeCtrl.App/Resources/Layouts/qwertz.json`

- [ ] **Step 1: Update letters sub-layout**

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, change existing Shift's specialAction from `"shift"` to `"modifier:shift"`
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 2: Update numbers sub-layout**

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, then `{ "label": "Shift", "output": "", "width": 1.2, "specialAction": "modifier:shift" }`
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 3: Update symbols sub-layout**

Row 1: prepend `{ "label": "Win", "output": "", "width": 1.2, "specialAction": "modifier:win" }`
Row 2: prepend `{ "label": "Caps", "output": "", "width": 1.2, "specialAction": "toggle:capslock" }`
Row 3: prepend `{ "label": "Alt", "output": "", "width": 1.2, "specialAction": "modifier:alt" }`, then `{ "label": "Shift", "output": "", "width": 1.2, "specialAction": "modifier:shift" }`
Row 4: prepend `{ "label": "Ctrl", "output": "", "width": 1.2, "specialAction": "modifier:ctrl" }`

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Resources/Layouts/qwertz.json
git commit -m "feat: add modifier keys to QWERTZ layout"
```

---

### Task 8: Update Layout Loader Tests

**Files:**
- Modify: `tests/EyeCtrl.Core.Tests/Keyboard/KeyboardLayoutLoaderTests.cs`

- [ ] **Step 1: Verify existing layout loader tests still pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests/ --filter "KeyboardLayout"`
Expected: All pass — the loader doesn't validate specialAction values, it just deserializes strings

- [ ] **Step 2: Add test for modifier key deserialization**

```csharp
[Fact]
public void Load_ParsesModifierSpecialActions()
{
    var layout = KeyboardLayoutLoader.Load(GetLayoutPath("qwerty.json"));

    var lettersRows = layout.SubLayouts["letters"].Rows;

    // First key of each row is the modifier column
    Assert.Equal("modifier:win", lettersRows[0][0].SpecialAction);
    Assert.Equal("toggle:capslock", lettersRows[1][0].SpecialAction);
    Assert.Equal("modifier:alt", lettersRows[2][0].SpecialAction);
    Assert.Equal("modifier:ctrl", lettersRows[3][0].SpecialAction);

    // Shift is second key in row 3 (after Alt)
    Assert.Equal("modifier:shift", lettersRows[2][1].SpecialAction);
}
```

Adapt `GetLayoutPath` to use whatever helper the test file already uses to locate layout files. If it uses embedded resources or a relative path, match that pattern.

- [ ] **Step 3: Run test**

Run: `dotnet test tests/EyeCtrl.Core.Tests/ --filter "ParsesModifierSpecialActions"`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Keyboard/KeyboardLayoutLoaderTests.cs
git commit -m "test: verify modifier key deserialization in layout loader"
```

---

### Task 9: Integration Smoke Test — Build and Verify

**Files:** (none new)

- [ ] **Step 1: Full solution build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 warnings related to modifier changes

- [ ] **Step 2: Run all tests**

Run: `dotnet test EyeCtrl.slnx`
Expected: All tests pass

- [ ] **Step 3: Launch the app and test modifier keys manually**

Run: `dotnet run --project src/EyeCtrl.App/`

Manual verification checklist:
1. Open keyboard (via toolbar ⌨ button)
2. Verify modifier column appears on left: Win, Caps, Alt, Ctrl
3. Dwell on Ctrl → verify it highlights with bright accent color
4. Dwell on a letter → verify Ctrl+letter is sent and Ctrl deactivates
5. Dwell on Caps → verify highlight stays on after pressing a letter
6. Dwell on Caps again → verify it deactivates
7. Switch to numbers sub-layout → verify modifier column is present
8. Switch to symbols sub-layout → verify modifier column is present
9. Activate Ctrl → switch to numbers → press 1 → verify Ctrl+1 is sent
10. Activate Shift → press any key → verify Shift clears (one-shot)

- [ ] **Step 4: Commit any fixes found during testing**

```bash
git add -A
git commit -m "fix: address issues found during modifier key integration testing"
```
