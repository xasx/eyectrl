# Provider Indicator Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Show a read-only emoji indicator on the left of the toolbar identifying the active gaze provider (👁 Tobii, 🖱 Mouse simulator).

**Architecture:** `ToolbarWindow` stores the provider state and re-adds the indicator on every `Initialize()` call (so settings changes don't wipe it). `App.xaml.cs` calls `SetProviderIndicator(bool)` once at startup — no further tracking needed.

**Tech Stack:** C# 12, WPF, .NET 8

---

## Task 1: Add provider indicator to ToolbarWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`

No unit tests — this is pure WPF visual code with no testable logic.

**Step 1: Add backing field**

After the existing fields at the top of the class, add:

```csharp
private bool? _providerIsTobii;
```

**Step 2: Add private `AddIndicatorElement` helper**

Add after the existing `AddButton` method:

```csharp
private void AddIndicatorElement(bool isTobii)
{
    var symbol = isTobii ? "\U0001F441" : "\U0001F5B1"; // 👁 or 🖱

    var label = new TextBlock
    {
        Text = symbol,
        Foreground = Brushes.White,
        FontSize = _buttonSize * 0.4,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    var border = new Border
    {
        Width = _buttonSize,
        Height = _buttonSize,
        Margin = new Thickness(2),
        Background = new SolidColorBrush(Color.FromArgb(120, 40, 40, 40)),
        CornerRadius = new CornerRadius(4),
        Child = label
    };

    ButtonPanel.Children.Insert(0, border);
}
```

Note: `Color.FromArgb(120, 40, 40, 40)` is slightly darker/more transparent than the button background `Color.FromArgb(180, 60, 60, 60)` to visually distinguish the non-interactive indicator.

**Step 3: Add public `SetProviderIndicator` method**

Add after `AddIndicatorElement`:

```csharp
public void SetProviderIndicator(bool isTobii)
{
    _providerIsTobii = isTobii;
    AddIndicatorElement(isTobii);
}
```

**Step 4: Re-add indicator at end of `Initialize`**

In the existing `Initialize` method, after the `foreach` loop that calls `AddButton`, add:

```csharp
if (_providerIsTobii.HasValue)
    AddIndicatorElement(_providerIsTobii.Value);
```

The full end of `Initialize` should look like:

```csharp
foreach (var (id, symbol) in buttons)
    AddButton(id, symbol);

if (_providerIsTobii.HasValue)
    AddIndicatorElement(_providerIsTobii.Value);
```

This ensures that when `Initialize` is called again (e.g., from Settings changed), the indicator is automatically restored at position 0 (left of all buttons).

**Step 5: Build**

```
dotnet build EyeCtrl.slnx
```
Expected: Build succeeded, 0 errors.

**Step 6: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs
git commit -m "feat: add provider indicator to toolbar (shows Tobii or mouse simulator)"
```

---

## Task 2: Call SetProviderIndicator from App.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Step 1: Resolve the provider type**

In `OnStartup`, after the `_serviceProvider = services.BuildServiceProvider();` line, the services are resolved. The `IGazeProvider` singleton is already registered. To determine which provider was created, resolve it and check the type.

After this existing block (around line 118):
```csharp
var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
var messenger = _serviceProvider.GetRequiredService<IMessenger>();
var inputSimulator = _serviceProvider.GetRequiredService<IInputSimulator>();
```

Add:
```csharp
var gazeProvider = _serviceProvider.GetRequiredService<IGazeProvider>();
bool isTobii = gazeProvider is TobiiInteractionLibProvider;
```

**Step 2: Call SetProviderIndicator after toolbar.Initialize**

Find the existing line (around line 152):
```csharp
toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs);
```

Add immediately after it:
```csharp
toolbar.SetProviderIndicator(isTobii);
```

That's all — no change needed in the `SettingsChanged` handler because `ToolbarWindow.Initialize` now auto-restores the indicator.

**Step 3: Build and run all tests**

```
dotnet test EyeCtrl.slnx
```
Expected: 70 Core tests pass, 29 ViewModel tests pass. App.Tests may have 4 pre-existing flaky WPF timeout failures — those are unrelated to this change and acceptable.

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: wire provider indicator in App.xaml.cs"
```
