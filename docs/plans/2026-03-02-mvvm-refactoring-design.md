# MVVM Refactoring Design

**Date:** 2026-03-02
**Status:** Approved
**Scope:** Full MVVM + DI conversion of EyeCtrl

## Motivation

The current architecture uses a hand-wired event-driven mediator pattern with all logic in WPF code-behind. While Core is clean and testable, the UI layer has pain points:

- **KeyboardWindow.xaml.cs (908 lines)** and **SettingsWindow.xaml.cs (1008 lines)** mix rendering, state management, and processing logic
- **Zero data binding** — every UI update is imperative code
- **Dwell/gaze logic trapped in WPF Windows** — untestable without instantiating UI
- **Mutable settings with no change notification** — relies on manual `ApplyAndSave()` discipline
- **Win32 interop boilerplate** duplicated across 7 overlay files
- **Filter recreation bug** — changing filter mode at runtime doesn't take effect

**Goals:** Extensibility, testability, and maintainability through full MVVM consistency.

## Technology Stack

| Package | Purpose |
|---------|---------|
| `CommunityToolkit.Mvvm` | Source generators (`[ObservableProperty]`, `[RelayCommand]`), `ObservableObject`, `WeakReferenceMessenger` |
| `Microsoft.Extensions.DependencyInjection` | DI container for composition root |

No other frameworks. CommunityToolkit.Mvvm provides everything needed — no Prism, Caliburn, or ReactiveUI.

## Project Structure

```
EyeCtrl.Core             (unchanged — models, interfaces, processing)
EyeCtrl.ViewModels       (NEW — all ViewModels, references Core + CommunityToolkit.Mvvm)
EyeCtrl.Platform          (unchanged — Win32, Tobii implementations)
EyeCtrl.App              (WPF views — references Core, ViewModels, Platform)
tests/EyeCtrl.Core.Tests       (unchanged — existing 30 unit tests)
tests/EyeCtrl.ViewModels.Tests (NEW — ViewModel unit tests)
tests/EyeCtrl.Platform.Tests   (unchanged)
```

`EyeCtrl.ViewModels` references only `EyeCtrl.Core` and `CommunityToolkit.Mvvm` — no WPF dependency. ViewModels are fully testable without instantiating windows.

## DI Container Setup

`App.xaml.cs` becomes primarily a composition root:

1. Build `ServiceCollection` with registrations
2. Build `ServiceProvider`
3. Resolve root services to start the app

**Registration categories:**
- **Core services** (singletons): `IGazeProvider`, `IGazeFilter`, `IInputSimulator`, `GazeEngine`, `InteractionManager`, `SettingsService`, `EyeCtrlSettings`
- **ViewModels** (singletons): one per overlay — `CursorCircleViewModel`, `ActivationButtonViewModel`, etc.
- **Views/Windows** (singletons): all overlay windows — created once, shown/hidden
- **Adapter** (singleton): `GazeEngineAdapter` — bridges Core events to MVVM messages

## Messaging Architecture

### Message Types

```
Core → ViewModel direction:
  GazeCursorMovedMessage(ScreenPoint Position)
  ShowActivationButtonMessage(ScreenPoint Position)
  HideActivationButtonMessage
  ShowActionMenuMessage(ScreenPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack)
  HideActionMenuMessage
  StateChangedMessage(InteractionState NewState)
  StartZoomMessage(ScreenPoint Center, double Radius, double Magnification)
  EndZoomMessage
  ShowKeyboardMessage
  HideKeyboardMessage
  ShowSettingsMessage
  ActionRequestedMessage(InteractionAction Action)

ViewModel → Core direction:
  ActionSelectedMessage(InteractionAction Action)
  BackSelectedMessage
  PauseToggledMessage
  ZoomToggledMessage
  KeyboardToggledMessage
  SettingsRequestedMessage
  KeyActivatedMessage(string Key)
  PredictionSelectedMessage(string Word)
  SettingsChangedMessage
  ActivationCompletedMessage
```

### Thread Marshaling: GazeEngineAdapter

`GazeEngineAdapter` lives in `EyeCtrl.App` (has access to `Dispatcher`). It:
1. Subscribes to all `GazeEngine` / `InteractionManager` C# events
2. Marshals to UI thread via `Dispatcher.BeginInvoke`
3. Sends corresponding messages via `WeakReferenceMessenger`

This keeps ViewModels thread-unaware — all messages arrive on the UI thread.

### What Stays as Direct Calls

Not everything is a message. The DI container enables constructor injection:
- `SettingsViewModel` takes `SettingsService` directly (calls `SaveAsync()`)
- ViewModels that need `EyeCtrlSettings` take it as constructor param (read current values)

## ViewModel Designs

### CursorCircleViewModel
- **Properties:** `ScreenPoint Position`, `double Size`, `Color CircleColor`, `double Opacity`
- **Receives:** `GazeCursorMovedMessage` → updates `Position`
- **View:** Applies `Position` to `Canvas.SetLeft/Top` (30Hz — imperative update in PropertyChanged handler)

### ActivationButtonViewModel
- **Properties:** `ScreenPoint Position`, `double DwellProgress`, `bool IsVisible`, `double Size`
- **Owns:** `DwellDetector` (moved from App.xaml.cs)
- **Receives:** `ShowActivationButtonMessage`, `HideActivationButtonMessage`, `GazeCursorMovedMessage`
- **Sends:** `ActivationCompletedMessage`

### ActionMenuViewModel
- **Properties:** `ScreenPoint Center`, `ObservableCollection<ActionMenuItemViewModel> Items`, `bool IsVisible`, `bool ShowBack`
- **ActionMenuItemViewModel:** `InteractionAction Action`, `string Symbol`, `double DwellProgress`, `ScreenPoint Position`
- **Owns:** `DwellDetector`
- **Receives:** `ShowActionMenuMessage`, `GazeCursorMovedMessage`
- **Sends:** `ActionSelectedMessage`, `BackSelectedMessage`
- **Extracted logic:** Radial position calculation, dwell tracking

### ToolbarViewModel
- **Properties:** `ObservableCollection<ToolbarButtonViewModel> Buttons`, `bool IsPaused`, `bool IsZooming`
- **ToolbarButtonViewModel:** `string Symbol`, `double DwellProgress`, `IRelayCommand Command`
- **Owns:** `DwellDetector`
- **Receives:** `GazeCursorMovedMessage`, `StateChangedMessage`
- **Sends:** `PauseToggledMessage`, `ZoomToggledMessage`, `KeyboardToggledMessage`, `SettingsRequestedMessage`

### KeyboardViewModel (currently 908 lines in code-behind)
- **Properties:** `ObservableCollection<KeyViewModel> Keys`, `ObservableCollection<string> Predictions`, `bool IsShifted`, `bool IsEmojiMode`, `string CurrentCategory`, `int EmojiPage`, `bool IsVisible`
- **Owns:** `NearestKeyTracker` (extracted to Core — the 55-line geometry algorithm), `DwellDetector` for prediction/category bars
- **Receives:** `GazeCursorMovedMessage`, `ShowKeyboardMessage`, `HideKeyboardMessage`
- **Sends:** `KeyActivatedMessage`, `PredictionSelectedMessage`
- **Extracted to Core:** `NearestKeyTracker` — nearest-key geometry algorithm, now unit-testable
- **Extracted logic:** Keyboard layout state machine (shift, emoji, sub-layouts), emoji category/pagination

### SettingsViewModel (currently 1008 lines in code-behind)
- **Properties:** `ObservableCollection<SettingsTabViewModel> Tabs`, `SettingsTabViewModel ActiveTab`, `ObservableCollection<SettingItemViewModel> CurrentPageItems`, `int CurrentPage`, `int PageCount`
- **SettingItemViewModel subclasses:** `SliderSettingViewModel`, `ComboSettingViewModel`, `ToggleSettingViewModel`, `ColorSettingViewModel`
- **Owns:** 4 `DwellDetector` instances (tabs, controls, pagination, modal)
- **Receives:** `GazeCursorMovedMessage`, `ShowSettingsMessage`
- **Sends:** `SettingsChangedMessage`
- **Extracted logic:** Settings page definitions (data-driven), pagination, reset-all (reflection-based, not manual field copy)

### ZoomOverlayViewModel
- **Properties:** `ScreenPoint Center`, `double Radius`, `double Magnification`, `bool IsVisible`
- **Receives:** `StartZoomMessage`, `EndZoomMessage`
- **View code-behind:** Screen capture (`BitBlt`, `CreateBitmapSourceFromHBitmap`) stays in view — inherently Win32/GDI

## Settings Architecture

Settings classes inherit from `ObservableObject`:

```csharp
public partial class CursorSettings : ObservableObject
{
    [ObservableProperty] private double _circleSize = 40;
    [ObservableProperty] private string _circleColor = "#00FF00";
    [ObservableProperty] private double _circleOpacity = 0.5;
    [ObservableProperty] private string _gazeFilterMode = "OneEuro";
}
```

**Change propagation:**
- `[ObservableProperty]` source generator creates properties with `INotifyPropertyChanged`
- `SettingsService` observes changes and debounce-saves (no more manual `ApplyAndSave()`)
- Cross-cutting changes (e.g., filter mode) send `SettingChangedMessage` for `GazeEngine` to react

**Reset logic:** Reflection-based or generated mapping, replacing the 25-line manual field copy.

**Filter recreation bug fix:** `GazeEngine` subscribes to settings changes and recreates the `IGazeFilter` when `GazeFilterMode` changes at runtime.

## OverlayWindow Base Class

Eliminates Win32 interop duplication across 7 overlay files:

```csharp
public class OverlayWindow : Window
{
    public bool IsClickThrough { get; init; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var flags = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (IsClickThrough) flags |= WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | flags);
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TRANSPARENT = 0x00000020;
}
```

## Testing Strategy

### New Test Project: `EyeCtrl.ViewModels.Tests`

| Logic | Currently in | Moves to | Test examples |
|-------|-------------|----------|---------------|
| Nearest-key tracking | KeyboardWindow.cs | `NearestKeyTracker` in Core | "gaze at Y, keys at X → nearest is Z" |
| Keyboard state machine | KeyboardWindow.cs | `KeyboardViewModel` | "shift → uppercase; shift again → lowercase" |
| Emoji pagination | KeyboardWindow.cs | `KeyboardViewModel` | "80 emojis, 20/page → 4 pages" |
| Radial layout | ActionMenuWindow.cs | `ActionMenuViewModel` / Core | "5 actions → 72 degree intervals" |
| Settings page definitions | SettingsWindow.cs | `SettingsViewModel` | "Cursor tab has 4 settings" |
| Settings reset | SettingsWindow.cs | `SettingsViewModel` | "reset → all match defaults" |
| Dwell per overlay | 5 code-behind files | ViewModels | "gaze on button 800ms → activated" |
| Toolbar mode transitions | ToolbarWindow.cs | `ToolbarViewModel` | "pause → IsPaused=true, play symbol" |

Existing 30 Core tests remain unchanged. ViewModels tested with real `WeakReferenceMessenger` — send message, assert state change. No WPF window instantiation needed.

## Migration Order

Since the app is not yet in daily use, we can refactor cleanly in sequence:

1. **Infrastructure** — Create `EyeCtrl.ViewModels` project, add NuGet packages, create `OverlayWindow` base class, set up DI container shell in App.xaml.cs
2. **Settings conversion** — Convert settings POCOs to `ObservableObject`, update `SettingsService`
3. **Simple overlays** — CursorCircle, ActivationButton, ZoomOverlay, Splash (smallest, prove the pattern)
4. **Medium overlays** — ActionMenu, Toolbar (moderate complexity, own DwellDetectors)
5. **Complex overlays** — Keyboard, Settings (the 900+ line monsters, biggest payoff)
6. **GazeEngineAdapter** — Replace App.xaml.cs event wiring with messaging
7. **Cleanup** — Remove dead code, verify all tests pass, fix filter recreation bug

Each step is independently compilable and testable.
