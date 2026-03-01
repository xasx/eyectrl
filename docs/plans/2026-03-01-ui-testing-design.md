# UI Testing with FlaUI — Design Document

**Date:** 2026-03-01

## Goal

Add automated UI/E2E tests for the WPF overlay app using FlaUI. Tests run the
actual application, interact with overlays, and verify behavior — catching
regressions without manual testing.

## Architecture

**Framework:** FlaUI.UIA3 + xUnit (same runner as existing tests)

**Test mode:** The app accepts a `--test-mode` CLI flag that:
- Uses `MouseGazeProvider` instead of Tobii (tests work on any machine)
- Skips the splash screen delay
- Tests can drive gaze input by moving the mouse cursor

**Test fixture:** A shared `AppFixture` (IAsyncLifetime) that:
1. Builds/locates the app binary
2. Launches `EyeCtrl.App.exe --test-mode` via `FlaUI.Core.Application`
3. Waits for overlays to appear
4. Provides helpers to find windows, simulate dwell, take screenshots
5. Kills the process on teardown

## Element Identification

FlaUI finds elements via `AutomationProperties.AutomationId`.

**Naming convention:** `OverlayName_ElementType_Name`

Examples:
- `Toolbar_Button_Pause`, `Toolbar_Button_Zoom`, `Toolbar_Button_Keyboard`,
  `Toolbar_Button_Settings`
- `Settings_Tab_Cursor`, `Settings_Tab_Dwell`, `Settings_Panel_Content`
- `Keyboard_Key_A`, `Keyboard_Key_Space`, `Keyboard_Prediction_0`
- `CursorCircle_Ellipse_Main`

Set on dynamic code-behind elements:
```csharp
System.Windows.Automation.AutomationProperties.SetAutomationId(border, "Toolbar_Button_Pause");
```

## Phase 1 Test Scenarios

### 1. App launches and shows overlays
- Launch app in test mode
- Verify cursor circle window exists and is visible
- Verify toolbar window exists with correct button count

### 2. Toolbar buttons exist
- Find all 4 toolbar buttons by AutomationId
- Verify labels: ⏸ ⊕ ⌨ ⚙

### 3. Pause toggle
- Move mouse to pause button position, hold (simulate dwell)
- Verify cursor circle hides
- Verify toolbar pause button changes to ▶
- Dwell again → circle reappears, button shows ⏸

### 4. Settings window opens
- Dwell on ⚙ button
- Verify settings window appears
- Verify sidebar has 7 tab buttons
- Verify content panel is visible

### 5. Settings tab navigation
- Click/dwell on each tab
- Verify content panel changes (different AutomationIds per tab content)

### 6. Keyboard opens
- Dwell on ⌨ button
- Verify keyboard window appears
- Verify keys are visible

### 7. Screenshot regression
- Capture baseline screenshots of toolbar, keyboard, settings
- On subsequent runs, compare pixel-wise (with tolerance for anti-aliasing)
- Store baselines in `tests/EyeCtrl.App.Tests/baselines/`

## Phase 2 (Future)

- Full gaze interaction flow: fixation → activation button → action menu → click
- Zoom mode activation and navigation
- Keyboard typing into external app (Notepad)
- Settings live-apply (change circle color → verify circle updates)
- Multi-monitor scenarios

## Test Project Structure

```
tests/EyeCtrl.App.Tests/
├── EyeCtrl.App.Tests.csproj
├── Fixtures/
│   └── AppFixture.cs          -- Launch/teardown, helper methods
├── Tests/
│   ├── AppLaunchTests.cs      -- Scenario 1-2
│   ├── ToolbarTests.cs        -- Scenario 3
│   ├── SettingsTests.cs       -- Scenario 4-5
│   ├── KeyboardTests.cs       -- Scenario 6
│   └── VisualRegressionTests.cs -- Scenario 7
└── baselines/                 -- Screenshot baselines
```

## Implementation Notes

- FlaUI needs the app to run as a real process (not in-process)
- `WS_EX_TOOLWINDOW` windows may not appear in default automation tree —
  use `TreeScope.Descendants` with `ConditionFactory.ByAutomationId()`
- Dwell simulation: move mouse to element center, `Thread.Sleep(dwellTimeMs + margin)`
- Tests should be marked `[Trait("Category", "UI")]` so they can be run
  separately from fast unit tests
- CI: these tests need a Windows machine with a display (or virtual display)
