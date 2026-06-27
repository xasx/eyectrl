# Gaze Scroll Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a gaze-joystick scroll mode to EyeCtrl — action menu entry, gaze-offset-driven 2D scrolling, stop button on the right edge.

**Architecture:** A new `InteractionState.ScrollMode` gates cursor movement (existing `GazeEngine` pattern — cursor only moves in `FreeLook`). Entry is handled **inside `InteractionManager.OnActionSelected`** (before its reset-to-free-look), NOT via the async `ActionRequestedMessage` path. Pure-math `ScrollEngine` in Core converts gaze offset to fractional velocity. A 60Hz App-layer worker lerps, accumulates fractional notches, and calls `IInputSimulator.Scroll()`. A `ScrollOverlayWindow` + `ScrollOverlayViewModel` provide visual feedback (anchor dot + ring thickness) and stop-button dwell detection. The VM is WPF-free (lives in the `EyeCtrl.ViewModels` library); all screen geometry is computed in the App layer and passed in.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit + NFluent

---

### Task 1: Core enum changes

**Files:**
- Modify: `src/EyeCtrl.Core/Models/InteractionState.cs`
- Modify: `src/EyeCtrl.Core/Models/InteractionAction.cs`

- [ ] **Step 1: Add `ScrollMode` to InteractionState**

```csharp
namespace EyeCtrl.Core.Models;

public enum InteractionState
{
    FreeLook,
    Fixation,
    ActionMenuVisible,
    ManualSelectionActive,
    ManualSelectionActionMenuVisible,
    ScrollMode,          // <-- new
    Paused
}
```

- [ ] **Step 2: Add `ScrollMode` to InteractionAction**

```csharp
namespace EyeCtrl.Core.Models;

public enum InteractionAction
{
    LeftClick,
    RightClick,
    DoubleClick,
    DragStart,
    DragEnd,
    ScrollUp,
    ScrollDown,
    ScrollLeft,
    ScrollRight,
    ScrollMode,          // <-- new
    PinNote
}
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Models/InteractionState.cs src/EyeCtrl.Core/Models/InteractionAction.cs
git commit -m "feat: add ScrollMode to InteractionState and InteractionAction enums"
```

---

### Task 2: ScrollEngine — pure math + unit tests

**Files:**
- Create: `src/EyeCtrl.Core/Processing/ScrollEngine.cs`
- Create: `tests/EyeCtrl.Core.Tests/Processing/ScrollEngineTests.cs`

`ScrollEngine` is stateless (no fields). It converts a gaze offset + sensitivity into fractional velocity in notches/sec. All coordinates in physical pixels.

- [ ] **Step 1: Write the failing unit tests**

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class ScrollEngineTests
{
    [Fact]
    public void InsideDeadZone_ReturnsZero()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1010, 1005); // ~11px from anchor, under default 30px dead zone
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void StraightUp_ProducesVerticalOnly()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1000, 700); // 300px above anchor
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsStrictlyGreaterThan(0); // positive Y = up in scroll semantics (spec: scroll up)
    }

    [Fact]
    public void Diagonal_ProducesBothAxes()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1400, 700); // 400px right, 300px up
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        Check.That(vx).IsStrictlyGreaterThan(0);
        Check.That(vy).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void SensitivityMultiplierScalesVelocity()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1000, 900); // 100px above — stays under the 60 notches/sec cap so sensitivity doubles exactly
        var (vx1, vy1) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        var (vx2, vy2) = ScrollEngine.ComputeVelocity(gaze, anchor, 2.0);
        Check.That(vx1).IsCloseTo(0, 1e-6);
        Check.That(vy2).IsCloseTo(vy1 * 2, 1e-3);
    }

    [Fact]
    public void NegativeOffset_ProducesNegativeVelocity()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1000, 1400); // 400px below
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsStrictlyLessThan(0);
    }

    [Fact]
    public void GazeAtAnchor_ReturnsZero()
    {
        var pt = new PhysicalPoint(1000, 1000);
        var (vx, vy) = ScrollEngine.ComputeVelocity(pt, pt, 1.0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void GazeInsideStopZone_ReturnsZero()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var stopCenter = new PhysicalPoint(2000, 600);
        var gaze = new PhysicalPoint(2020, 610); // inside 50px stop zone radius
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0, stopCenter, 50);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ScrollEngineTests" --no-restore
```

Expected: build error — ScrollEngine does not exist.

- [ ] **Step 3: Write the minimal ScrollEngine implementation**

```csharp
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public static class ScrollEngine
{
    public const double DeadZoneRadiusPixels = 30.0;
    public const double MaxVelocityNotchesPerSec = 60.0;
    public const double DefaultPixelsToNotchesFactor = 0.15; // 100px offset → 15 notches/sec at 1x

    public static (double vx, double vy) ComputeVelocity(
        PhysicalPoint gaze,
        PhysicalPoint anchor,
        double sensitivity,
        PhysicalPoint? stopZoneCenter = null,
        double stopZoneRadiusPx = 50.0)
    {
        // Stop zone check: if gaze is near the stop button, force zero
        if (stopZoneCenter.HasValue)
        {
            double sx = gaze.X - stopZoneCenter.Value.X;
            double sy = gaze.Y - stopZoneCenter.Value.Y;
            if (Math.Sqrt(sx * sx + sy * sy) <= stopZoneRadiusPx)
                return (0, 0);
        }

        double dx = gaze.X - anchor.X;
        double dy = anchor.Y - gaze.Y; // invert: screen Y increases downward, scroll-up = negative screen Y
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance <= DeadZoneRadiusPixels)
            return (0, 0);

        double effective = distance - DeadZoneRadiusPixels;
        double magnitude = effective * DefaultPixelsToNotchesFactor * sensitivity;
        magnitude = Math.Min(magnitude, MaxVelocityNotchesPerSec);

        double vx = (dx / distance) * magnitude;
        double vy = (dy / distance) * magnitude;

        return (vx, vy);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ScrollEngineTests" --no-restore
```

Expected: 7/7 pass

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Processing/ScrollEngine.cs tests/EyeCtrl.Core.Tests/Processing/ScrollEngineTests.cs
git commit -m "feat: ScrollEngine with dead zone, sensitivity, stop zone"
```

---

### Task 3: ScrollSettings — settings object

**Files:**
- Create: `src/EyeCtrl.Core/Settings/ScrollSettings.cs`
- Create: `tests/EyeCtrl.Core.Tests/Settings/ScrollSettingsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class ScrollSettingsTests
{
    [Fact]
    public void DefaultSensitivityIsOne()
    {
        var s = new ScrollSettings();
        Check.That(s.Sensitivity).IsCloseTo(1.0, 1e-6);
    }

    [Fact]
    public void SensitivityClampedToRange()
    {
        var s = new ScrollSettings();
        Check.ThatCode(() => s.Sensitivity = 3.0).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => s.Sensitivity = 0.1).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidSensitivityRangeDoesNotThrow()
    {
        var s = new ScrollSettings();
        s.Sensitivity = 0.5;
        Check.That(s.Sensitivity).IsCloseTo(0.5, 1e-6);
        s.Sensitivity = 2.0;
        Check.That(s.Sensitivity).IsCloseTo(2.0, 1e-6);
    }

    [Fact]
    public void ValidatePassesForDefaults()
    {
        var s = new ScrollSettings();
        Check.That(s.Validate()).IsEmpty();
    }

    // ValidateFailsForOutOfRange is intentionally omitted — the Sensitivity setter
    // (OnSensitivityChanging) already throws ArgumentOutOfRangeException on bad values.
    // The Validate() method is belt-and-suspenders for the live property path.
}
```

- [ ] **Step 2: Verify the test fails**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ScrollSettingsTests" --no-restore
```

- [ ] **Step 3: Write ScrollSettings**

```csharp
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ScrollSettings : ObservableObject, IValidatable
{
    private const double SensitivityMin = 0.5;
    private const double SensitivityMax = 2.0;
    private const double SensitivityDefault = 1.0;

    [ObservableProperty] private double _sensitivity = SensitivityDefault;

    partial void OnSensitivityChanging(double value)
    {
        if (value < SensitivityMin || value > SensitivityMax)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Sensitivity must be between {SensitivityMin} and {SensitivityMax}");
    }

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(Sensitivity, SensitivityMin, SensitivityMax, nameof(Sensitivity))
        );
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ScrollSettingsTests" --no-restore
```

Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ScrollSettings.cs tests/EyeCtrl.Core.Tests/Settings/ScrollSettingsTests.cs
git commit -m "feat: add ScrollSettings with sensitivity range validation"
```

---

### Task 4: EyeCtrlSettings + ActionMenuSettings integration

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`

- [ ] **Step 1: Add `_scroll` property to EyeCtrlSettings**

Add after the existing `_externalDwell` field (i.e. next to the `[ObservableProperty] private ExternalDwellSettings _externalDwell` line):

```csharp
[ObservableProperty] private ScrollSettings _scroll = new();
```

Add validate block in `EyeCtrlSettings.Validate()` after the ExternalDwell block:

```csharp
if (Scroll is null)
    errors.Add("Scroll settings must not be null.");
else
    errors.AddRange(Scroll.Validate());
```

- [ ] **Step 2: Add ScrollMode to ActionMenuSettings defaults**

In `VisibleActions` initializer, add `InteractionAction.ScrollMode`:

```csharp
[ObservableProperty] private List<InteractionAction> _visibleActions = new()
{
    InteractionAction.LeftClick, InteractionAction.RightClick,
    InteractionAction.DoubleClick, InteractionAction.DragStart,
    InteractionAction.ScrollUp, InteractionAction.ScrollDown,
    InteractionAction.ScrollMode,     // <-- new
    InteractionAction.PinNote
};
```

In `EnsureDefaults()`, add to the defaults array:

```csharp
InteractionAction.ScrollMode,
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs src/EyeCtrl.Core/Settings/ActionMenuSettings.cs
git commit -m "feat: wire ScrollSettings into EyeCtrlSettings and add ScrollMode to action menu"
```

---

### Task 5: InteractionManager — ScrollMode entry (inside OnActionSelected) + exit

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- Create: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerScrollModeTests.cs`

> **Why entry lives in `OnActionSelected`:** when the user picks an action, `OnActionSelected` fires `ActionRequested` and then **synchronously** calls `ResetToFreeLook()`. The `ActionRequestedMessage` only reaches `EventCoordinator` later (two `BeginInvoke` hops), by which time the state is already `FreeLook`. So scroll mode MUST be entered inside `OnActionSelected`, before the reset — not from `EventCoordinator`. The anchor is the same `targetPoint` `OnActionSelected` already computes (the fixation/snapped point that opened the menu), which is exactly the anchor the spec wants.

- [ ] **Step 1: Write the failing tests**

Tests drive the manager into `ActionMenuVisible` through the real entry path (fixation → activation dwell → action selected), then select `ScrollMode`.

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class InteractionManagerScrollModeTests
{
    private static InteractionManager InActionMenu(out PhysicalPoint fixation)
    {
        var im = new InteractionManager(); // SelectionMode.None by default
        fixation = new PhysicalPoint(1000, 800);
        im.OnFixationStarted(fixation);        // FreeLook -> Fixation
        im.OnActivationButtonDwelled();        // Fixation -> ActionMenuVisible (None branch)
        return im;
    }

    [Fact]
    public void SelectingScrollMode_EntersScrollModeWithFixationAnchor()
    {
        var im = InActionMenu(out var fixation);
        im.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);
        Check.That(im.ScrollAnchor).IsNotNull();
        Check.That(im.ScrollAnchor!.Value.X).IsCloseTo(fixation.X, 1e-6);
        Check.That(im.ScrollAnchor!.Value.Y).IsCloseTo(fixation.Y, 1e-6);
    }

    [Fact]
    public void SelectingScrollMode_DoesNotFireActionRequested()
    {
        var im = InActionMenu(out _);
        bool fired = false;
        im.ActionRequested += (_, _) => fired = true;
        im.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(fired).IsFalse(); // ScrollMode is a mode switch, not a click action
    }

    [Fact]
    public void ExitScrollMode_ReturnsToFreeLook()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.ScrollMode);
        im.ExitScrollMode();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(im.ScrollAnchor).IsNull();
    }

    [Fact]
    public void FixationStarted_IsIgnoredInScrollMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.ScrollMode);
        im.OnFixationStarted(new PhysicalPoint(500, 500));
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);
    }

    [Fact]
    public void EnteringScrollMode_FiresStateChangedWithScrollMode()
    {
        var im = InActionMenu(out _);
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;
        im.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(captured).IsNotNull();
        Check.That(captured!.Value).IsEqualTo(InteractionState.ScrollMode);
    }

    [Fact]
    public void ExitScrollMode_FiresStateChangedWithFreeLook()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.ScrollMode);
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;
        im.ExitScrollMode();
        Check.That(captured).IsNotNull();
        Check.That(captured!.Value).IsEqualTo(InteractionState.FreeLook);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionManagerScrollModeTests" --no-restore
```

- [ ] **Step 3: Add ScrollMode support to InteractionManager**

Add a `ScrollAnchor` property:

```csharp
/// <summary>Anchor point for the active gaze-scroll session, or null when not scrolling.</summary>
public PhysicalPoint? ScrollAnchor { get; private set; }
```

Intercept `ScrollMode` inside `OnActionSelected`, **after** the `targetPoint` null-check but **before** `HideActionMenu`/`ActionRequested`/`ResetToFreeLook`:

```csharp
public void OnActionSelected(InteractionAction action)
{
    PhysicalPoint? targetPoint = CurrentState switch
    {
        // ...unchanged switch...
    };

    if (targetPoint == null) return;

    // Scroll mode is a mode switch, not a click: enter it instead of firing an action,
    // and do NOT reset to free look (that would drop us out of scroll mode immediately).
    if (action == InteractionAction.ScrollMode)
    {
        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
            EndManualSelection?.Invoke();
        ScrollAnchor = targetPoint;
        SetState(InteractionState.ScrollMode);
        return;
    }

    HideActionMenu?.Invoke();
    if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
        EndManualSelection?.Invoke();

    ActionRequested?.Invoke(action, targetPoint.Value);
    ResetToFreeLook();
}
```

Add `ExitScrollMode` (place near `OnActionMenuDismissed`):

```csharp
/// <summary>Exits gaze-scroll mode and returns to free look.</summary>
public void ExitScrollMode()
{
    if (CurrentState != InteractionState.ScrollMode) return;
    ScrollAnchor = null;
    ResetToFreeLook();
}
```

Clear the anchor in `ResetToFreeLook` for safety:

```csharp
private void ResetToFreeLook()
{
    FixationPoint = null;
    ManualSelectionDwellPoint = null;
    AutoSelectionTarget = null;
    ScrollAnchor = null;       // <-- new
    SetState(InteractionState.FreeLook);
}
```

> **No change needed to `OnFixationStarted`** — it already early-returns unless `CurrentState == FreeLook`, so fixation is naturally ignored in `ScrollMode`. The `FixationStarted_IsIgnoredInScrollMode` test passes without modification.

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionManagerScrollModeTests" --no-restore
```

Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Processing/InteractionManager.cs tests/EyeCtrl.Core.Tests/Processing/InteractionManagerScrollModeTests.cs
git commit -m "feat: enter ScrollMode from OnActionSelected (pre-reset), add ExitScrollMode"
```

---

### Task 6: REMOVED — no EventCoordinator action routing needed

> **Intentionally empty.** An earlier draft routed `ScrollMode` through `EventCoordinator.HandleActionRequested`. That cannot work: by the time the `ActionRequestedMessage` is handled (two `BeginInvoke` hops later), `OnActionSelected` has already called `ResetToFreeLook()`, so the state is no longer `ActionMenuVisible`. Entry is therefore handled directly in `InteractionManager.OnActionSelected` (Task 5), and the lifecycle reacts to `StateChanged → ScrollMode` (Task 10). Do **not** add a `ScrollMode` case to `HandleActionRequested` — `ScrollMode` never produces an `ActionRequestedMessage`. Skip this task.

---

### Task 7: ScrollOverlayViewModel — Option D feedback + stop button dwell

**Files:**
- Create: `src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/ScrollOverlayViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

public class ScrollOverlayViewModelTests
{
    // Fresh messenger per VM to avoid cross-test leakage from the shared static default.
    private static ScrollOverlayViewModel NewVm() =>
        new(new ScrollSettings(), new WeakReferenceMessenger());

    private static readonly PhysicalPoint Anchor = new(1000, 800);
    private static readonly PhysicalPoint StopCenter = new(2000, 600);

    [Fact]
    public void GazeAtAnchor_DotIsCentered_NoScrollVelocity()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
        vm.Receive(new GazeCursorMovedMessage(Anchor, 0));
        Check.That(vm.AnchorDotOffsetX).IsCloseTo(0, 1e-3);
        Check.That(vm.AnchorDotOffsetY).IsCloseTo(0, 1e-3);
        Check.That(vm.ScrollVelocityX).IsCloseTo(0, 1e-6);
        Check.That(vm.ScrollVelocityY).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void TargetVelocityChanged_FiresWithComputedVelocity()
    {
        var vm = NewVm();
        double cvx = double.NaN, cvy = double.NaN;
        vm.TargetVelocityChanged += (x, y) => { cvx = x; cvy = y; };
        vm.EnterScrollMode(Anchor, StopCenter);
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(1000, 600), 0)); // 200px above anchor
        Check.That(cvy).IsStrictlyGreaterThan(0); // looking up -> scroll up
        Check.That(cvx).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void StopButtonDwell_TriggersExit()
    {
        var vm = NewVm();
        bool exitCalled = false;
        vm.ExitRequested += () => exitCalled = true;

        vm.EnterScrollMode(Anchor, StopCenter);
        var stopPos = vm.StopButtonCenter; // == StopCenter
        vm.Receive(new GazeCursorMovedMessage(stopPos, 0));
        vm.Receive(new GazeCursorMovedMessage(stopPos, 1_000_000)); // 1s later, > 600ms dwell
        Check.That(exitCalled).IsTrue();
    }

    [Fact]
    public void EnterScrollMode_SetsAnchorAndStopCenter()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
        Check.That(vm.Anchor).IsEqualTo(Anchor);
        Check.That(vm.StopButtonCenter).IsEqualTo(StopCenter);
        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void ExitScrollMode_ClearsState()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
        vm.ExitScrollMode();
        Check.That(vm.IsVisible).IsFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ScrollOverlayViewModelTests" --no-restore
```

- [ ] **Step 3: Write ScrollOverlayViewModel**

> **Layering:** `EyeCtrl.ViewModels` targets `net10.0` with **no `UseWPF`** — `System.Windows.SystemParameters` is unavailable here. The stop-button center (a screen-edge position) is therefore computed in the App layer and passed into `EnterScrollMode`. The VM stays WPF-free.

```csharp
namespace EyeCtrl.ViewModels;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ScrollOverlayViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private const string StopButtonTargetId = "scroll-stop";
    private const int StopButtonDwellMs = 600;       // DwellTarget dwell param is int (ms)
    private const double StopButtonRadiusPx = 30;
    private const double StopZoneRadiusPx = 50;
    private const double MaxOffsetPx = 200.0;         // gaze offset that puts the dot at the circle edge

    private readonly DwellDetector _dwellDetector;
    private readonly IMessenger _messenger;
    private readonly ScrollSettings _scrollSettings;
    private bool _disposed;

    private PhysicalPoint _anchor;
    private PhysicalPoint _stopButtonCenter;

    /// <summary>Cursor circle anchor dot offset X (-1 to 1, fraction of circle radius).</summary>
    [ObservableProperty] private double _anchorDotOffsetX;
    /// <summary>Cursor circle anchor dot offset Y (-1 to 1, fraction of circle radius).</summary>
    [ObservableProperty] private double _anchorDotOffsetY;
    /// <summary>Scroll velocity in notches/sec (for ring thickness visualization).</summary>
    [ObservableProperty] private double _scrollVelocityX;
    [ObservableProperty] private double _scrollVelocityY;
    /// <summary>Dwell progress on the stop button (0.0-1.0).</summary>
    [ObservableProperty] private double _stopButtonProgress;
    /// <summary>Whether the scroll overlay is visible.</summary>
    [ObservableProperty] private bool _isVisible;
    /// <summary>Current scroll velocity magnitude for visualization scaling.</summary>
    [ObservableProperty] private double _velocityMagnitude;

    public PhysicalPoint Anchor => _anchor;
    public PhysicalPoint StopButtonCenter => _stopButtonCenter;

    /// <summary>Fired when the user dwells on the stop button.</summary>
    public event Action? ExitRequested;
    /// <summary>Fired each gaze sample with the freshly computed target velocity (notches/sec). The
    /// scroll worker subscribes to this — single source of truth, no cross-callback staleness.</summary>
    public event Action<double, double>? TargetVelocityChanged;
    /// <summary>Fired each gaze sample after feedback properties are updated, so the window can repaint.</summary>
    public event Action? FeedbackChanged;

    public ScrollOverlayViewModel(ScrollSettings scrollSettings, IMessenger messenger)
    {
        _scrollSettings = scrollSettings;
        _messenger = messenger;
        _dwellDetector = new DwellDetector();
        _dwellDetector.TargetActivated += _ => ExitRequested?.Invoke();
        _dwellDetector.TargetProgress += (_, progress) => StopButtonProgress = progress;
        messenger.RegisterAll(this);
    }

    /// <param name="anchor">Scroll anchor (the fixation that opened the menu), physical px.</param>
    /// <param name="stopButtonCenter">Stop-button center, computed by the App layer, physical px.</param>
    public void EnterScrollMode(PhysicalPoint anchor, PhysicalPoint stopButtonCenter)
    {
        _anchor = anchor;
        _stopButtonCenter = stopButtonCenter;
        IsVisible = true;
        AnchorDotOffsetX = 0;
        AnchorDotOffsetY = 0;
        ScrollVelocityX = 0;
        ScrollVelocityY = 0;
        VelocityMagnitude = 0;
        StopButtonProgress = 0;

        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(
            StopButtonTargetId,
            _stopButtonCenter,
            StopButtonRadiusPx,
            StopButtonDwellMs));
    }

    public void ExitScrollMode()
    {
        IsVisible = false;
        _dwellDetector.ClearTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (!IsVisible) return;

        // Feed gaze to dwell detector for the stop button (updates StopButtonProgress via callback).
        _dwellDetector.Update(message.Position, message.TimestampMicroseconds);

        // Compute scroll velocity (zero inside dead zone or stop zone).
        var (vx, vy) = ScrollEngine.ComputeVelocity(
            message.Position, _anchor, _scrollSettings.Sensitivity,
            _stopButtonCenter, StopZoneRadiusPx);

        // Option D visual feedback.
        double dx = message.Position.X - _anchor.X;
        double dy = _anchor.Y - message.Position.Y; // invert Y (screen Y grows downward)
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist > 1.0)
        {
            AnchorDotOffsetX = Math.Clamp(dx / MaxOffsetPx, -1.0, 1.0);
            AnchorDotOffsetY = Math.Clamp(dy / MaxOffsetPx, -1.0, 1.0);
        }
        else
        {
            AnchorDotOffsetX = 0;
            AnchorDotOffsetY = 0;
        }

        ScrollVelocityX = vx;
        ScrollVelocityY = vy;
        VelocityMagnitude = Math.Sqrt(vx * vx + vy * vy);

        // Push to the worker and repaint — both synchronous, so no stale-read race.
        TargetVelocityChanged?.Invoke(vx, vy);
        FeedbackChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ScrollOverlayViewModelTests" --no-restore
```

Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs tests/EyeCtrl.ViewModels.Tests/ScrollOverlayViewModelTests.cs
git commit -m "feat: ScrollOverlayViewModel with Option D feedback and stop button dwell"
```

---

### Task 8: ScrollOverlayWindow — XAML + code-behind

**Files:**
- Create: `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml.cs`

- [ ] **Step 1: Write ScrollOverlayWindow.xaml**

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.ScrollOverlayWindow"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
                        Title="Scroll Overlay"
                        Topmost="True"
                        ShowInTaskbar="False"
                        WindowStyle="None"
                        AllowsTransparency="True"
                        Background="Transparent"
                        IsHitTestVisible="False"
                        Width="{x:Static SystemParameters.PrimaryScreenWidth}"
                        Height="{x:Static SystemParameters.PrimaryScreenHeight}"
                         Left="0" Top="0">
    <Canvas x:Name="OverlayCanvas" Width="{x:Static SystemParameters.PrimaryScreenWidth}"
            Height="{x:Static SystemParameters.PrimaryScreenHeight}">
        <!-- Anchor indicator: crosshair at anchor position -->
        <Line x:Name="AnchorCrosshairH" Stroke="White" StrokeThickness="1" Opacity="0.5"/>
        <Line x:Name="AnchorCrosshairV" Stroke="White" StrokeThickness="1" Opacity="0.5"/>

        <!-- Cursor circle with anchor dot and ring thickness -->
        <Ellipse x:Name="CursorCircle" Stroke="White" StrokeThickness="2"
                 Width="80" Height="80" Opacity="0.7"/>
        <Ellipse x:Name="AnchorDot" Fill="White" Width="8" Height="8" Opacity="0.9"/>
        <Ellipse x:Name="VelocityRing" Stroke="#4af" StrokeThickness="2" Width="80" Height="80" Opacity="0.8"/>

        <!-- Stop button on the right edge -->
        <Ellipse x:Name="StopButton" Fill="#c00" Width="50" Height="50" Opacity="0.7"/>
        <Path x:Name="StopButtonProgressArc" Stroke="#fff" StrokeThickness="4"
              Width="50" Height="50" Opacity="0.9"/>
        <TextBlock x:Name="StopLabel" Text="⏹" Foreground="White"
                   FontSize="24" TextAlignment="Center" VerticalAlignment="Center"/>
    </Canvas>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Write ScrollOverlayWindow.xaml.cs**

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.ViewModels;

public partial class ScrollOverlayWindow : OverlayWindow
{
    public ScrollOverlayWindow()
    {
        InitializeComponent();
    }

    public void Show(ScrollOverlayViewModel vm)
    {
        DataContext = vm;

        // Position elements
        var anchor = vm.Anchor;

        // Anchor crosshair
        AnchorCrosshairH.X1 = anchor.X - 15; AnchorCrosshairH.Y1 = anchor.Y;
        AnchorCrosshairH.X2 = anchor.X + 15; AnchorCrosshairH.Y2 = anchor.Y;
        AnchorCrosshairV.X1 = anchor.X; AnchorCrosshairV.Y1 = anchor.Y - 15;
        AnchorCrosshairV.X2 = anchor.X; AnchorCrosshairV.Y2 = anchor.Y + 15;

        // Cursor circle at anchor
        Canvas.SetLeft(CursorCircle, anchor.X - 40);
        Canvas.SetTop(CursorCircle, anchor.Y - 40);
        Canvas.SetLeft(AnchorDot, anchor.X - 4);
        Canvas.SetTop(AnchorDot, anchor.Y - 4);
        Canvas.SetLeft(VelocityRing, anchor.X - 40);
        Canvas.SetTop(VelocityRing, anchor.Y - 40);

        // Stop button on right edge
        var stopCenter = vm.StopButtonCenter;
        Canvas.SetLeft(StopButton, stopCenter.X - 25);
        Canvas.SetTop(StopButton, stopCenter.Y - 25);
        Canvas.SetLeft(StopButtonProgressArc, stopCenter.X - 25);
        Canvas.SetTop(StopButtonProgressArc, stopCenter.Y - 25);
        Canvas.SetLeft(StopLabel, stopCenter.X - 12);
        Canvas.SetTop(StopLabel, stopCenter.Y - 14);

        Show();
        Topmost = true;
    }

    public void UpdateFeedback(double dotOffsetX, double dotOffsetY,
        double velMagnitude, double stopProgress)
    {
        double anchorX = Canvas.GetLeft(CursorCircle) + 40;
        double anchorY = Canvas.GetTop(CursorCircle) + 40;
        double circleRadius = 40;
        double dotX = anchorX + dotOffsetX * circleRadius * 0.6;
        double dotY = anchorY + dotOffsetY * circleRadius * 0.6;
        Canvas.SetLeft(AnchorDot, dotX - 4);
        Canvas.SetTop(AnchorDot, dotY - 4);

        double thickness = 2 + velMagnitude * 0.2;
        VelocityRing.StrokeThickness = Math.Min(thickness, 12);

        if (stopProgress > 0)
        {
            StopButtonProgressArc.Visibility = Visibility.Visible;
            // Arc from top (12 o'clock) clockwise proportional to dwell progress.
            // StartPoint = (25, 0) relative to the 50x50 Path element, i.e. top-center.
            double angleDeg = stopProgress * 360;
            double angleRad = (angleDeg - 90) * Math.PI / 180; // -90° so 0° = top
            double endX = 25 + 25 * Math.Cos(angleRad);
            double endY = 25 + 25 * Math.Sin(angleRad);
            var arc = new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(25, 25),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = angleDeg > 180
            };
            var fig = new PathFigure { StartPoint = new Point(25, 0) };
            fig.Segments.Add(arc);
            StopButtonProgressArc.Data = new PathGeometry { Figures = { fig } };
        }
        else
        {
            StopButtonProgressArc.Visibility = Visibility.Hidden;
        }
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml.cs
git commit -m "feat: ScrollOverlayWindow with anchor crosshair, cursor circle feedback, stop button"
```

---

### Task 9: ScrollWorker — 60Hz lerp + fractional accumulation + IInputSimulator.Scroll

**Files:**
- Create: `src/EyeCtrl.App/Services/ScrollWorker.cs`

- [ ] **Step 1: Write ScrollWorker**

> **Threading:** this uses a `DispatcherTimer`, so `OnTick` and `UpdateTargetVelocity` both run on the **UI thread** (gaze is already marshaled there). `SendInput` therefore fires on the UI thread at ~60Hz — fine for this volume. The fields are single-threaded by construction; the `lock` is retained only as cheap defensive insurance against a future off-thread caller. The project logs via **Serilog's static `Log`** (matching `EventCoordinator`), not `Microsoft.Extensions.Logging` — `EventCoordinationContext` exposes no logger factory.

```csharp
namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Serilog;

public class ScrollWorker : IDisposable
{
    private const int TickIntervalMs = 16; // ~60Hz
    private readonly IInputSimulator _inputSimulator;
    private readonly DispatcherTimer _timer;

    private PhysicalPoint _anchor;
    private double _targetVx;
    private double _targetVy;
    private double _currentVx;
    private double _currentVy;
    private double _remainderX;
    private double _remainderY;
    private bool _running;

    private readonly object _lock = new();

    public ScrollWorker(IInputSimulator inputSimulator)
    {
        _inputSimulator = inputSimulator;
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(TickIntervalMs)
        };
        _timer.Tick += OnTick;
    }

    public void Start(PhysicalPoint anchor)
    {
        lock (_lock)
        {
            _anchor = anchor;
            _targetVx = 0;
            _targetVy = 0;
            _currentVx = 0;
            _currentVy = 0;
            _remainderX = 0;
            _remainderY = 0;
            _running = true;
        }
        _timer.Start();
        Log.Debug("ScrollWorker started at anchor ({X:F0}, {Y:F0})", anchor.X, anchor.Y);
    }

    public void Stop()
    {
        _timer.Stop();
        lock (_lock)
        {
            _running = false;
        }
        Log.Debug("ScrollWorker stopped");
    }

    public void UpdateTargetVelocity(double vx, double vy)
    {
        lock (_lock)
        {
            _targetVx = vx;
            _targetVy = vy;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (!_running) return;

            // Lerp current velocity toward target
            const double lerpFactor = 0.3;
            _currentVx += (_targetVx - _currentVx) * lerpFactor;
            _currentVy += (_targetVy - _currentVy) * lerpFactor;

            // If target is zero and we're close, snap to zero
            if (Math.Abs(_targetVx) < 0.01 && Math.Abs(_currentVx) < 0.01) _currentVx = 0;
            if (Math.Abs(_targetVy) < 0.01 && Math.Abs(_currentVy) < 0.01) _currentVy = 0;

            // Convert notches/sec → fractional notches per tick
            double deltaX = _currentVx * TickIntervalMs / 1000.0 + _remainderX;
            double deltaY = _currentVy * TickIntervalMs / 1000.0 + _remainderY;

            int intDeltaX = (int)Math.Truncate(deltaX);
            int intDeltaY = (int)Math.Truncate(deltaY);

            _remainderX = deltaX - intDeltaX;
            _remainderY = deltaY - intDeltaY;

            if (intDeltaX != 0 || intDeltaY != 0)
            {
                _inputSimulator.Scroll(_anchor, intDeltaX, intDeltaY);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTick;
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/EyeCtrl.App/Services/ScrollWorker.cs
git commit -m "feat: ScrollWorker — 60Hz lerp, fractional notch accumulation, IInputSimulator.Scroll"
```

---

### Task 10: EventCoordinator — scroll mode lifecycle

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

This handles entering/exiting scroll mode: starts/stops the ScrollWorker, shows/hides the ScrollOverlayWindow, and routes gaze to the ScrollOverlayViewModel.

- [ ] **Step 1: Add scroll mode fields and wiring to EventCoordinator**

Add fields at the top of the class:

```csharp
private ScrollOverlayViewModel? _scrollOverlayVm;
private ScrollOverlayWindow? _scrollOverlayWindow;
private ScrollWorker? _scrollWorker;
private bool _scrollModeActive;
```

Add a handler for `InteractionManager` state changes (subscribed in `Initialize`):

```csharp
_context.InteractionManager.StateChanged += OnInteractionStateChanged;
```

```csharp
private void OnInteractionStateChanged(InteractionState state)
{
    if (state == InteractionState.ScrollMode)
    {
        OnScrollModeEntered();
    }
    else if (_scrollModeActive)
    {
        // Tear down on ANY exit from ScrollMode, not just FreeLook — e.g. TogglePause
        // transitions ScrollMode -> Paused directly, which must still stop the worker.
        OnScrollModeExited();
    }
}

private void OnScrollModeEntered()
{
    if (_context == null) return;

    var anchor = _context.InteractionManager.ScrollAnchor!.Value;

    // Pin OS cursor at anchor (GazeEngine stops MoveCursor outside FreeLook on its own).
    _context.InputSimulator.MoveCursor(anchor);

    // Compute the stop-button center here in the App layer (the VM is WPF-free).
    // Right edge, vertically centered. Screen.Bounds is already physical pixels.
    var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
    var stopButtonCenter = new PhysicalPoint(
        bounds.Width - DpiHelper.LogicalToPhysicalX(50),
        bounds.Height / 2.0);

    // Initialize overlay VM
    _scrollOverlayVm = new ScrollOverlayViewModel(_context.Settings.Scroll, _context.Messenger);
    _scrollOverlayVm.EnterScrollMode(anchor, stopButtonCenter);
    _scrollOverlayVm.ExitRequested += OnScrollExitRequested;

    _scrollOverlayWindow = new ScrollOverlayWindow();
    _scrollOverlayWindow.Show(_scrollOverlayVm);

    // Start scroll worker (Serilog static logging — no logger param).
    _scrollWorker = new ScrollWorker(_context.InputSimulator);
    _scrollWorker.Start(anchor);

    // Single source of truth: the VM computes velocity + feedback in Receive and raises these
    // synchronously, so the worker and window never read a stale value from a separate callback.
    _scrollOverlayVm.TargetVelocityChanged += OnScrollTargetVelocityChanged;
    _scrollOverlayVm.FeedbackChanged += OnScrollFeedbackChanged;

    _scrollModeActive = true;
}

private void OnScrollExitRequested() => _context?.InteractionManager.ExitScrollMode();

private void OnScrollTargetVelocityChanged(double vx, double vy)
    => _scrollWorker?.UpdateTargetVelocity(vx, vy);

private void OnScrollFeedbackChanged()
{
    if (_scrollOverlayWindow == null || _scrollOverlayVm == null) return;
    _scrollOverlayWindow.UpdateFeedback(
        _scrollOverlayVm.AnchorDotOffsetX,
        _scrollOverlayVm.AnchorDotOffsetY,
        _scrollOverlayVm.VelocityMagnitude,
        _scrollOverlayVm.StopButtonProgress);
}

private void OnScrollModeExited()
{
    _scrollWorker?.Stop();
    _scrollWorker?.Dispose();
    _scrollWorker = null;

    if (_scrollOverlayVm != null)
    {
        _scrollOverlayVm.TargetVelocityChanged -= OnScrollTargetVelocityChanged;
        _scrollOverlayVm.FeedbackChanged -= OnScrollFeedbackChanged;
        _scrollOverlayVm.ExitRequested -= OnScrollExitRequested;
        _scrollOverlayVm.ExitScrollMode();
        _scrollOverlayVm.Dispose();
        _scrollOverlayVm = null;
    }

    _scrollOverlayWindow?.Close();
    _scrollOverlayWindow = null;

    _scrollModeActive = false;
}
```

No change to the gaze handler is needed. The VM receives gaze via `GazeCursorMovedMessage` (sent by `GazeEngineAdapter`, **not** EventCoordinator) through its `IRecipient` registration, computes velocity + feedback in `Receive`, and raises `TargetVelocityChanged`/`FeedbackChanged` — which the handlers above forward to the worker and window. There is no separate per-gaze polling in EventCoordinator (the earlier draft's stale-read coupling is gone).

> **Dispose:** unsubscribe the state handler and tear down any active scroll session. Add to `EventCoordinator.Dispose()`:
> ```csharp
> _context.InteractionManager.StateChanged -= OnInteractionStateChanged;
> if (_scrollModeActive) OnScrollModeExited();
> ```

- [ ] **Step 2: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat: EventCoordinator scroll mode lifecycle — worker, overlay, gaze routing"
```

---

### Task 11: Settings UI wiring — SettingsViewModel

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs`

> **No `AppBootstrapper` / `OverlayManager` change.** `ScrollSettings` is a sub-object of the already-constructed `EyeCtrlSettings` (no DI registration needed), and the scroll overlay is created transiently in EventCoordinator per session (Task 10) rather than being a persistent managed overlay. Nothing to register.

 - [ ] **Step 1: Confirm no OverlayManager/AppBootstrapper change** — `ScrollOverlayWindow`/`ViewModel` are created transiently in EventCoordinator (Task 10) per scroll session. `OverlayManager` only owns persistent overlays.

- [ ] **Step 2: Add sensitivity slider to SettingsViewModel**

In `BuildTabs()`, add a scroll tab:

```csharp
var s = _settings.Scroll;
Tabs.Add(new SettingsTabViewModel("Scroll", "\uD83D\uDDA5", new List<SettingItemViewModel>
{
    new SliderSettingViewModel("scrollSensitivity", "Sensitivity", s.Sensitivity, 0.5, 2.0, 0.1, v => s.Sensitivity = v),
}));
```

In `ResetAllSettings()`, add:

```csharp
CopySettings(defaults.Scroll, _settings.Scroll);
```

 - [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingsViewModel.cs
git commit -m "feat: add Scroll tab (sensitivity slider) to SettingsViewModel"
```
