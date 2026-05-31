# In-App Gaze Calibration

## Motivation

EyeCtrl currently relies entirely on the Tobii eye tracker's built-in calibration. Normalized 0-1 gaze coordinates are mapped directly to physical screen pixels with no correction layer. This means:

- Residual offset/drift after the tracker's calibration cannot be corrected from within the app
- Users must switch to the Tobii system utility to recalibrate
- No per-session drift compensation
- No calibration quality feedback loop

This design adds an in-app recalibration system: a correction transform computed from user-collected gaze samples at known screen positions, applied as a pre-filter step in the gaze pipeline.

## Architecture

```
IGazeProvider → GazePoint
                    ↓
            CalibrationTransform (new)      ← CalibrationService
                    ↓
            GapInterpolator (composes IGazeFilter internally)
                    ↓
            CursorMoved → IInputSimulator
```

The calibration transform is applied **before** GapInterpolator (which internally composes the `IGazeFilter`). Geometry correction happens first, then gap-fill + smoothing.

### New Components

| Component | Layer | Role |
|-----------|-------|------|
| `ICalibrationTransform` | Core | Interface for mapping transforms (extensible for future transform types) |
| `AffineTransform` | Core | 2×3 affine matrix, least-squares (3+ points); the only v1 transform |
| `CalibrationPoint` | Core | Single sample: screen target + averaged gaze + std dev |
| `CalibrationPointCollector` | Core | Buffers raw gaze samples per target, discards initial settle window, returns mean+stdDev |
| `CalibrationData` | Core | Full dataset: points, timestamp, screen resolution+monitor id, mean error |
| `CalibrationService` | Core | Session management, training, persistence, transform factory from saved data |
| `CalibrationSettings` | Core | Settings subsection: point count, target params |
| `CalibrationViewModel` | ViewModels | MVVM state machine for calibration flow |
| `CalibrationWindow` | App | Fullscreen overlay for target display and results |
| `CalibrationTargetPositionHelper` | Core | Static method: computes target positions for N points |

### Dependency Flow

```
CalibrationViewModel
    → CalibrationService (start session, add samples, complete)
    → IGazeProvider (subscribe to gaze samples during session)
    → IMessenger (broadcast state/progress)

GazeEngine
    → CalibrationService.CurrentTransform (nullable, applied in OnGazePoint)

CalibrationWindow
    → CalibrationViewModel (DataContext binding)
```

## Transform Algorithms

v1 ships a single transform; the interface is designed so additional types can be added without plumbing changes.

```csharp
public interface ICalibrationTransform
{
    PhysicalPoint Apply(PhysicalPoint rawGaze);
    double[] GetParameters();                       // serialized as JSON array
    void SetParameters(double[] parameters);
}

public enum TransformType { Affine }
```

### AffineTransform (min 3 points)

Standard 2D affine: `x' = a*x + b*y + c`, `y' = d*x + e*y + f`. Six parameters solved via linear least squares on the overdetermined system from all calibration points. Covers translation + scale + rotation + shear — enough for most drift patterns. Uses hand-rolled normal equations and Gaussian elimination with partial pivoting; no external dependencies.

### Selection

Only affine is available in v1. The `TransformType` enum exists for future expansion (homography, polynomial) but the settings UI won't expose a transform selector until additional types ship.

## CalibrationService

### Lifecycle

```
Idle → Collecting → Training → Active
                      ↓          ↓
                   Cancelled   Failed
```

1. `StartSession(int pointCount)` — stores target positions, creates a `CalibrationPointCollector`, returns target count
2. `BeginTarget(int index)` — tells the collector to start buffering raw gaze samples for this target
3. `AddRawSample(PhysicalPoint gaze)` — forwarded to the collector; it discards samples during the settle window, then buffers
4. `EndTarget()` — collector computes mean+stdDev of buffered samples (post-settle), returns a `CalibrationPoint`
5. `CompleteSession()` — trains the affine transform, wraps in `CalibrationData` with screen resolution + monitor device name, saves to disk
6. `CancelSession()` — discards in-progress data

### CalibrationPointCollector (Core)

Lives in `EyeCtrl.Core/Calibration/`. Buffers `PhysicalPoint` samples and discards the first ~200ms (configurable settle window) to exclude saccade landing. On `End()`, returns a `CalibrationPoint` with mean, stdDev, and sample count.

Keeping this in Core (not the ViewModel) makes the buffer/averaging logic unit-testable without UI thread dependencies.

### Persistence

- File: `%APPDATA%/EyeCtrl/calibration.json` (separate from `settings.json`)
- Stored as JSON: transform type, parameters (double array), timestamp, point count, mean error, screen resolution (width×height), monitor device name
- Loaded at startup by `AppBootstrapper`; on load, compares stored resolution + monitor name against current system — mismatches cause the calibration to be discarded (logged at Warning) with `CurrentTransform` = null
- `CurrentTransform` is null when no calibration exists (passthrough mode)

### Thread safety

`CurrentTransform` is read on the gaze provider's poll thread (in `OnGazePoint`) and written on the UI thread (after `CompleteSession()`). Protected by a `volatile` field or a simple `lock` on the `CalibrationService` instance.

### Quality feedback

After training, `CalibrationData.ComputeQuality()` computes mean Euclidean error across all sample points (actual gaze vs. transformed gaze). This is **training error**, not generalization — it should be treated as a sanity check, not a precision claim. Results are categorized at screen-relative thresholds:

| Mean error | Status | UX |
|-----------|--------|----|
| < 2% of screen diagonal | Good | Green checkmark, auto-dismiss |
| ≥ 2% of screen diagonal | Poor | Orange warning, Retry / Accept buttons |

(For a 1920×1080 display, 2% ≈ 44 px — matching the spec's visual example of 45 px.)

## CalibrationSettings

Follows the existing settings convention: `ObservableObject` + `IValidatable` with `partial void OnXxxChanging` range guards and a `Validate()` returning `IReadOnlyList<string>`. Uses `ValidationHelper.ValidateRange` for numeric bounds and `ValidationHelper.ValidateHexColor` for the target color.

```csharp
public partial class CalibrationSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _pointCount = 5;           // range 3-16 (affine needs 3+)
    [ObservableProperty] private int _samplesPerPoint = 30;     // range 5-100 (per-target buffer size)
    [ObservableProperty] private int _targetHoldMs = 1000;      // range 200-3000
    [ObservableProperty] private int _settleWindowMs = 200;     // range 0-500 (discarded before averaging)
    [ObservableProperty] private int _targetRadiusPixels = 20;  // range 8-80
    [ObservableProperty] private string _targetColor = "#00FF00";

    partial void OnPointCountChanging(int value) { /* clamp 3-16 */ }
    partial void OnSamplesPerPointChanging(int value) { /* clamp 5-100 */ }
    partial void OnTargetHoldMsChanging(int value) { /* clamp 200-3000 */ }
    partial void OnSettleWindowMsChanging(int value) { /* clamp 0-500 */ }
    partial void OnTargetRadiusPixelsChanging(int value) { /* clamp 8-80 */ }
    partial void OnTargetColorChanging(string value) { /* ensure # prefix */ }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        ValidationHelper.ValidateRange(PointCount, 3, 16, nameof(PointCount), errors);
        ValidationHelper.ValidateRange(SamplesPerPoint, 5, 100, nameof(SamplesPerPoint), errors);
        ValidationHelper.ValidateRange(TargetHoldMs, 200, 3000, nameof(TargetHoldMs), errors);
        ValidationHelper.ValidateRange(SettleWindowMs, 0, 500, nameof(SettleWindowMs), errors);
        ValidationHelper.ValidateRange(TargetRadiusPixels, 8, 80, nameof(TargetRadiusPixels), errors);
        ValidationHelper.ValidateHexColor(TargetColor, nameof(TargetColor), errors);
        return errors;
    }
}
```

Added to `EyeCtrlSettings` as a new `Calibration` property alongside existing `Cursor`, `Dwell`, etc. `EyeCtrlSettings.Validate()` adds a `Calibration.Validate()` branch, and `SettingsViewModel.ResetAllSettings()` adds a `Calibration` copy line.

## CalibrationViewModel

### State machine

```
Idle
  → CalibrationRequestedMessage
    → Collecting (show overlay, position target 1)
        → target N held long enough → advance to next target
          → all targets done → Training
            → transform trained → Active (show completion screen)
            → training failed → Failed (show error, offer retry)
        → Esc / cancel → Cancelled (dismiss overlay)
```

### Key properties

| Property | Type | Purpose |
|----------|------|---------|
| `State` | `CalibrationState` enum | Current phase |
| `CurrentTargetIndex` | `int` | 0-based index of active target |
| `TotalTargets` | `int` | From settings |
| `ProgressPercent` | `double` | 0-100 |
| `CurrentTargetPosition` | `PhysicalPoint` | Where to render the dot |
| `ErrorText` | `string?` | Failure message |
| `MeanErrorPixels` | `double?` | Result quality |
| `TargetColor` | `string` | Bound from settings |

### Messages (new, added to `InteractionMessages.cs`)

- `CalibrationRequestedMessage` — toolbar/settings → CalibrationVM
- `CalibrationTargetPositionMessage(PhysicalPoint, int, int)` — VM → window
- `CalibrationStateChangedMessage(CalibrationState, double%)` — VM → EventCoordinator
- `CalibrationCompletedMessage(bool success, double? meanError)` — VM → toolbar
- `CalibrationCancelledMessage` — VM → toolbar

### Sample collection during Collecting state

For each target:
1. Position the dot at `CalibrationTargetPositionHelper.GetPositions()[i]`
2. Call `CalibrationService.BeginTarget(i)` — starts the collector's settle window
3. Subscribe to `IGazeProvider.GazePointReceived`; each raw `(X,Y)` is forwarded as a `PhysicalPoint` to `CalibrationService.AddRawSample()` (the collector handles settle discard + buffering internally)
4. After `targetHoldMs` elapses, call `CalibrationService.EndTarget()` — returns the `CalibrationPoint` for this target
5. Unsubscribe from `IGazeProvider.GazePointReceived`
6. Advance to next target or trigger training

### Interaction suppression

While in Collecting or Training states, the ViewModel sends `InteractionStateChangedMessage(Paused)` to suppress fixations, clicks, and menu activation behind the overlay. On completion/cancel, it sends `InteractionStateChangedMessage(FreeLook)`.

### Threading

Gaze samples arrive on the provider's poll thread. `CalibrationService.AddRawSample()` calling into `CalibrationPointCollector` is thread-safe (uses a lock or `ConcurrentBag`). The ViewModel's `ObservableProperty` updates for target advancement must be marshaled to the UI thread via `Application.Current.Dispatcher.BeginInvoke()`.

## CalibrationWindow

### Layout

- Fullscreen `OverlayWindow` with dark glass backdrop (same style as GlassBackdropWindow)
- Single circular target rendered at `CurrentTargetPosition`
- Top-center: "Look at the dot · 3 / 5" progress text
- Bottom-right: "Press Esc to cancel" hint
- Targets shown one at a time; completed positions may show small faded dots

### Visual states

| State | What's shown |
|-------|-------------|
| Collecting | Glass backdrop + active target (pulsing green ring) + progress text |
| Training | Glass backdrop + "Computing..." text |
| Active (good, <2% diagonal) | Green checkmark + "Calibration complete · 0.8% · Dismissing..." |
| Active (poor, ≥2% diagonal) | Orange warning + "Calibration complete · 2.3%" + Retry / Accept buttons |
| Failed / Cancelled | Dismiss immediately |

### Target animation

Active target uses a WPF `Storyboard` with a double-animation on the ring's `ScaleTransform` (e.g., 0.8 → 1.2 → 0.8 over 600ms, repeating). Provides clear visual focus guidance.

### Completion dismissal

- Good result: auto-dismiss after 3 seconds via `DispatcherTimer`
- Poor result: dwell-based Retry/Accept buttons (eye-only dismissal, no keyboard)
- Both: Esc key also dismisses

## Target Position Algorithm

`CalibrationTargetPositionHelper.GetPositions(int count, PhysicalPoint screenSize)`:

- **3 points**: top-left, top-right, bottom-center
- **4 points**: four corners
- **5 points**: four corners + center
- **6-9 points**: 3×3 grid (fill best-fit positions)
- **10-16 points**: 4×4 grid (fill best-fit positions)

All positions inset 20% from screen edges. Positions are ordered to alternate between near and far to reduce the visual jump distance between consecutive targets.

## GazeEngine Change

Inserted at the top of `OnGazePoint(GazePoint gaze)`, before `_gapInterpolator.Process()`:

```csharp
var corrected = gaze;
if (gaze.IsValid && _calibrationService?.CurrentTransform is { } t)
{
    var p = t.Apply(new PhysicalPoint(gaze.X, gaze.Y));
    corrected = gaze with { X = p.X, Y = p.Y };
}
var (filtered, isInGap) = _gapInterpolator.Process(corrected);
```

`GazeEngine` receives `CalibrationService` via constructor injection (nullable — existing tests that don't provide one pass through unchanged). The `with` expression requires `GazePoint` to be a `record` (it already is: `public record GazePoint`).

## Settings UI Integration

### New tab: "Calibration"

Added to `SettingsViewModel` tabs list. Contains:

- **Point count**: `DwellSlider`, range 3-16, default 5
- **Samples per point**: `DwellSlider`, range 5-100, default 30
- **Target hold (ms)**: `DwellSlider`, range 200-3000, default 1000
- **Calibrate Now**: Action button (new `SettingItemViewModel` subclass) — starts a calibration session
- **Clear Calibration**: Action button — removes saved calibration data
- **Last calibration**: Read-only label item (new `SettingItemViewModel` subclass) showing date and mean error, or "No calibration data"

The existing `SettingItemViewModel` currently supports only `Slider`, `Combo`, `Color`, and `Toggle` item types. Two new subclasses are needed:

- `ActionButtonSettingItem` — hosts a `DwellButton` that fires a callback (`Action? OnActivate`)
- `ReadOnlyLabelSettingItem` — displays a non-editable string (`string? DisplayText`)

Both need corresponding `DataTemplate` entries in `SettingsWindow.xaml`.

With 6 items, this tab fits on one settings page (pages hold up to 6 items).

### Toolbar integration

New toolbar button ("Calibrate") added to `ToolbarViewModel`. Icon indicates calibration status:
- No calibration: empty circle outline
- Calibration active: filled checkmark
- Poor calibration: warning triangle

Dwell on the button sends `CalibrationRequestedMessage`.

## Files Summary

### New files

| File | Layer |
|------|-------|
| `EyeCtrl.Core/Calibration/ICalibrationTransform.cs` | Core |
| `EyeCtrl.Core/Calibration/AffineTransform.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationPoint.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationPointCollector.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationData.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationService.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationTargetPositionHelper.cs` | Core |
| `EyeCtrl.Core/Settings/CalibrationSettings.cs` | Core |
| `EyeCtrl.ViewModels/CalibrationViewModel.cs` | ViewModels |
| `EyeCtrl.ViewModels/SettingItemViewModel/ActionButtonSettingItem.cs` | ViewModels |
| `EyeCtrl.ViewModels/SettingItemViewModel/ReadOnlyLabelSettingItem.cs` | ViewModels |
| `EyeCtrl.App/Overlays/CalibrationWindow.xaml` | App |
| `EyeCtrl.App/Overlays/CalibrationWindow.xaml.cs` | App |

### Modified files

| File | Change |
|------|--------|
| `EyeCtrl.Core/GazeEngine.cs` | Apply calibration transform in `OnGazePoint` before `_gapInterpolator.Process()`; accept nullable `CalibrationService` via ctor |
| `EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `Calibration` property; add `Calibration.Validate()` call in `Validate()` |
| `EyeCtrl.App/Services/AppBootstrapper.cs` | Register `CalibrationService` (singleton), `CalibrationViewModel` (singleton), `CalibrationWindow`; load saved calibration on startup |
| `EyeCtrl.App/Services/EventCoordinator.cs` | Wire calibration messages, toolbar button handler, interaction suppression during capture |
| `EyeCtrl.App/Services/OverlayManager.cs` | Initialize `CalibrationWindow` |
| `EyeCtrl.App/Services/IOverlayManager.cs` | Add `CalibrationViewModel` to `OverlayViewModels` record |
| `EyeCtrl.ViewModels/ToolbarViewModel.cs` | Add calibrate button with status indicator |
| `EyeCtrl.ViewModels/SettingsViewModel.cs` | Add Calibration tab; add `Calibration` reset in `ResetAllSettings()` |
| `EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add 5 calibration messages |
| `EyeCtrl.ViewModels/UIConstants.cs` | Add calibration-related dwell-time/radius constants if needed |
| `EyeCtrl.App/Views/SettingsWindow.xaml` | Add DataTemplates for `ActionButtonSettingItem` and `ReadOnlyLabelSettingItem` |
| `EyeCtrl.Core/Processing/InteractionManager.cs` | Accept `InteractionStateChangedMessage` from CalibrationVM to pause during capture |

## Testing Guidance

- **Core unit tests**: Test `AffineTransform.Apply()` against known inputs; verify parameter round-trips; test `CalibrationData.ComputeQuality()` (training error only, note this caveat); test insufficient points, degenerate collinear points
- **CalibrationPointCollector tests**: Verify settle-window discard, mean/stdDev accuracy, zero-samples edge case, thread safety under concurrent writes
- **Position helper tests**: Verify correct count and layout for N=3 through N=16, verify edge margin, verify alternating near-far ordering
- **ViewModel tests**: Test state transitions (Idle→Collecting→Training→Active, cancellation, failure); verify `InteractionStateChangedMessage(Paused)` sent on entry and `FreeLook` on exit
- **App tests**: Test overlay show/hide, completion screen rendering (Category=UI)
- **Persistence tests**: Verify `calibration.json` round-trips correctly, verify resolution/monitor mismatch causes discard on load, verify graceful fallback on corrupt file
