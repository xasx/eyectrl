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
            GapInterpolator
                    ↓
            IGazeFilter (OneEuro/Direct/Smoothed)
                    ↓
            CursorMoved → IInputSimulator
```

The calibration transform is applied **before** any temporal filtering — geometry correction happens first, then smoothing.

### New Components

| Component | Layer | Role |
|-----------|-------|------|
| `ICalibrationTransform` | Core | Interface for mapping transforms |
| `AffineTransform` | Core | 2×3 affine matrix, least-squares (3+ points) |
| `HomographyTransform` | Core | 3×3 homography, DLT+SVD (4+ points) |
| `PolynomialTransform` | Core | 2nd-order bivariate polynomial (6+ points) |
| `CalibrationPoint` | Core | Single sample: screen target + averaged gaze + std dev |
| `CalibrationData` | Core | Full dataset: points, timestamp, transform, mean error |
| `CalibrationService` | Core | Session management, training, persistence |
| `CalibrationSettings` | Core | Settings subsection: point count, transform type, target params |
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
    → CalibrationService.CurrentTransform (nullable, applied in ProcessGaze)

CalibrationWindow
    → CalibrationViewModel (DataContext binding)
```

## Transform Algorithms

All three transforms implement `ICalibrationTransform`:

```csharp
public interface ICalibrationTransform
{
    PhysicalPoint Apply(PhysicalPoint rawGaze);
    byte[] Serialize();
    void Deserialize(byte[] data);
    TransformType Type { get; }
}

public enum TransformType { Affine, Homography, Polynomial }
```

### AffineTransform (min 3 points)

Standard 2D affine: `x' = a·x + b·y + c`, `y' = d·x + e·y + f`. Six parameters solved via linear least squares. Covers translation + scale + rotation + shear. Fastest to compute.

### HomographyTransform (min 4 points)

3×3 perspective transform matrix with h33=1 (8 degrees of freedom). Solved via Direct Linear Transform + SVD. Handles slight perspective distortion from eye tracker angle. Overkill for most setups but included for completeness.

### PolynomialTransform (min 6 points)

Bivariate 2nd-order polynomial: 12 coefficients for `x' = f(x,y)`, `y' = g(x,y)` where f/g are quadratics. Solved via least squares on the design matrix. Handles non-linear screen curvature correction.

### Selection

The default is `Affine` (3 points, fast, corrects most drift). The user can change via Settings.

## CalibrationService

### Lifecycle

```
Idle → Collecting → Training → Active
                      ↓          ↓
                   Cancelled   Failed
```

1. `StartSession(int pointCount)` — returns `CalibrationData` with target positions computed
2. `AddSample(PhysicalPoint target, PhysicalPoint gaze)` — records an averaged sample for the current target
3. `CompleteSession(TransformType type)` — trains the transform, serializes params into `CalibrationData.TransformParams`, computes mean error, saves to disk
4. `CancelSession()` — discards in-progress data

### Persistence

- File: `%APPDATA%/EyeCtrl/calibration.json` (separate from `settings.json`)
- Stored as JSON: transform type, serialized params (base64), timestamp, point count, mean error
- Loaded at startup by `AppBootstrapper`
- `CurrentTransform` is null when no calibration exists (passthrough mode)

### Quality feedback

After training, `CalibrationData.ComputeQuality()` computes mean Euclidean error across all sample points (actual gaze vs. transformed gaze). Results are categorized:

| Mean error | Status | UX |
|-----------|--------|----|
| < 5 px | Good | Green checkmark, auto-dismiss |
| ≥ 5 px | Poor | Orange warning, Retry / Accept buttons |

## CalibrationSettings

```csharp
public partial class CalibrationSettings : ObservableValidator, IValidatable
{
    [ObservableProperty] private int _pointCount = 5;           // range 1-16
    [ObservableProperty] private TransformType _transformType = TransformType.Affine;
    [ObservableProperty] private int _samplesPerPoint = 30;     // gaze samples averaged per target
    [ObservableProperty] private int _targetHoldMs = 1000;      // display duration per target
    [ObservableProperty] private int _targetRadiusPixels = 20;  // target dot radius
    [ObservableProperty] private string _targetColor = "#00FF00";
}
```

Added to `EyeCtrlSettings` as a new `Calibration` property alongside existing `Cursor`, `Dwell`, etc.

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
1. Position the dot at `CalibrationLayout.GetTargetPositions()[i]`
2. Wait `targetHoldMs` (show fill animation or progress ring)
3. During hold, subscribe to `IGazeProvider.GazePointReceived` and buffer samples
4. After hold completes, compute mean and std dev of buffered samples
5. Call `CalibrationService.AddSample(target, meanGaze)`
6. Advance to next target or trigger training

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
| Training | Glass backdrop + "Computing..." spinner/text |
| Active (good) | Green checkmark + "Calibration complete · 0.8 px · Dismissing..." |
| Active (poor) | Orange warning + "Calibration complete · 45 px" + Retry / Accept buttons |
| Failed / Cancelled | Dismiss immediately |

### Target animation

Active target uses a WPF `Storyboard` with a double-animation on the ring's `ScaleTransform` (e.g., 0.8 → 1.2 → 0.8 over 600ms, repeating). Provides clear visual focus guidance.

### Completion dismissal

- Good result: auto-dismiss after 3 seconds via `DispatcherTimer`
- Poor result: dwell-based Retry/Accept buttons (eye-only dismissal, no keyboard)
- Both: Esc key also dismisses

## Target Position Algorithm

`CalibrationTargetPositionHelper.GetPositions(int count, PhysicalPoint screenSize)`:

- **1 point**: screen center
- **2 points**: top-left and bottom-right quadrants
- **3 points**: top-left, top-right, bottom-center
- **4 points**: four corners
- **5 points**: four corners + center
- **6-9 points**: 3×3 grid (fill best-fit positions)
- **10-16 points**: 4×4 grid (fill best-fit positions)

All positions inset 10% from screen edges. Positions are ordered to alternate between near and far to reduce the visual jump distance between consecutive targets.

## GazeEngine Change

Minimal change — one line in `ProcessGaze()`:

```csharp
// Before GapInterpolator and filter:
var calibratedGaze = _calibrationService?.CurrentTransform?.Apply(rawGaze) ?? rawGaze;
```

`GazeEngine` receives `CalibrationService` via constructor injection (nullable — existing tests that don't provide one pass through unchanged).

## Settings UI Integration

### New tab: "Calibration"

Added to `SettingsViewModel` tabs list. Contains:

- **Point count**: `DwellSlider`, range 1-16, default 5
- **Transform type**: `DwellComboBox`, options: Affine / Homography / Polynomial
- **Samples per point**: `DwellSlider`, range 5-100, default 30
- **Target hold (ms)**: `DwellSlider`, range 200-3000, default 1000
- **Calibrate Now**: `DwellButton` — starts a calibration session
- **Clear Calibration**: `DwellButton` — removes saved calibration data
- **Last calibration**: Read-only stats line showing date and mean error (or "No calibration data")

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
| `EyeCtrl.Core/Calibration/HomographyTransform.cs` | Core |
| `EyeCtrl.Core/Calibration/PolynomialTransform.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationPoint.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationData.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationService.cs` | Core |
| `EyeCtrl.Core/Calibration/CalibrationTargetPositionHelper.cs` | Core |
| `EyeCtrl.Core/Settings/CalibrationSettings.cs` | Core |
| `EyeCtrl.ViewModels/CalibrationViewModel.cs` | ViewModels |
| `EyeCtrl.App/Overlays/CalibrationWindow.xaml` | App |
| `EyeCtrl.App/Overlays/CalibrationWindow.xaml.cs` | App |

### Modified files

| File | Change |
|------|--------|
| `EyeCtrl.Core/GazeEngine.cs` | Apply calibration transform before filter |
| `EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `Calibration` property |
| `EyeCtrl.App/Services/AppBootstrapper.cs` | Register CalibrationService, CalibrationViewModel, CalibrationWindow |
| `EyeCtrl.App/Services/EventCoordinator.cs` | Wire calibration messages, toolbar button handler |
| `EyeCtrl.App/Services/OverlayManager.cs` | Initialize CalibrationWindow |
| `EyeCtrl.App/Services/IOverlayManager.cs` | Add CalibrationWindow to OverlayViewModels |
| `EyeCtrl.ViewModels/ToolbarViewModel.cs` | Add calibrate button |
| `EyeCtrl.ViewModels/SettingsViewModel.cs` | Add Calibration tab |
| `EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add 5 calibration messages |

## Testing Guidance

- **Core unit tests**: Test each transform's `Apply()` against known inputs, verify serialization round-trips, test `CalibrationData.ComputeQuality()`, test edge cases (insufficient points, degenerate inputs)
- **Position helper tests**: Verify correct count and layout for N=1 through N=16, verify edge margin
- **ViewModel tests**: Test state transitions, sample collection, cancellation
- **App tests**: Test overlay show/hide, completion screen rendering (Category=UI)
