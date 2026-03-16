# Zoom View Bug Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix two bugs in zoom view: (1) fixation accumulating outside the zoom circle, and (2) the action menu preview showing a different screen region than what zoom displayed.

**Architecture:** Fix 1 adds a boundary check in `App.xaml.cs`'s `zoomGazeHandler`. Fix 2 threads `ZoomCenter`/`ZoomRadius` context through five layers: `ShowActionMenuMessage` → `ActionMenuViewModel` → `App.xaml.cs` → `ActionMenuWindow.Show()`, where it controls both the screen capture region and the position-indicator offset.

**Tech Stack:** C# 13, .NET 10, WPF, CommunityToolkit.Mvvm, xUnit

---

## Spec

`docs/superpowers/specs/2026-03-15-zoom-fixes-design.md`

---

## File Map

| File | Change |
|------|--------|
| `src/EyeCtrl.App/App.xaml.cs` | Task 1: boundary check; Task 5: pass ZoomCenter/ZoomRadius to Show() |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Task 2: add init-only properties to ShowActionMenuMessage |
| `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` | Task 3: store ZoomCenter/ZoomRadius from message |
| `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` | Task 4: populate ZoomCenter/ZoomRadius in zoom path |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Task 6: zoom capture + indicator offset |
| `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs` | Task 3: new tests |

---

## Chunk 1: Fix 1 — Zoom boundary check

### Task 1: Restrict fixation to inside zoom circle

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` (around line 345)

No unit test — this logic lives in a closure inside a WPF event handler; verify manually by running the app in zoom mode and confirming gaze outside the circle does not accumulate fixation progress.

- [ ] **Step 1: Locate insertion point**

  Open `src/EyeCtrl.App/App.xaml.cs`. Find `zoomGazeHandler` (around line 327). The grace-period early return is on line 345:

  ```csharp
  if ((ts - zoomStartTimestamp) < gracePeriodUs) return;
  ```

  The new boundary check goes immediately after this line.

- [ ] **Step 2: Insert boundary check**

  After the grace-period return and before `zoomFixation.Update(pt, ts)`, insert:

  ```csharp
  // Reject gaze outside the zoom circle (pt is physical px; radius is logical DIP, needs scaling)
  var physicalZoomRadius = settings.Zoom.CircleRadiusPixels * DpiHelper.ScaleX;
  var dx = pt.X - zoomVM.Center.X;
  var dy = pt.Y - zoomVM.Center.Y;
  if (dx * dx + dy * dy > physicalZoomRadius * physicalZoomRadius)
  {
      zoomFixation.Reset();
      // FixationDetector.Reset() does NOT fire FixationEnded, so reset UI manually:
      Dispatcher.BeginInvoke(() =>
      {
          zoomIndicatorVM.ResetFixation();
          zoomIndicator.AnimateReset(
              settings.Zoom.GazeIndicatorOuterDiameter,
              settings.Zoom.GazeIndicatorResetAnimationMs);
      });
      return;
  }
  ```

  The full `zoomGazeHandler` after the change should read:

  ```csharp
  zoomGazeHandler = (pt, ts) =>
  {
      if (im.CurrentState != InteractionState.ZoomSelectionActive) return;

      // Update indicator position (even during grace period)
      Dispatcher.BeginInvoke(() => {
          zoomIndicatorVM.UpdateGazePosition(pt);
          zoomIndicator.UpdatePosition(zoomIndicatorVM.IndicatorX, zoomIndicatorVM.IndicatorY);
      });

      // Record the first timestamp as zoom start
      if (!graceStarted)
      {
          zoomStartTimestamp = ts;
          graceStarted = true;
      }

      // Skip gaze data during grace period
      if ((ts - zoomStartTimestamp) < gracePeriodUs) return;

      // Reject gaze outside the zoom circle (pt is physical px; radius is logical DIP, needs scaling)
      var physicalZoomRadius = settings.Zoom.CircleRadiusPixels * DpiHelper.ScaleX;
      var dx = pt.X - zoomVM.Center.X;
      var dy = pt.Y - zoomVM.Center.Y;
      if (dx * dx + dy * dy > physicalZoomRadius * physicalZoomRadius)
      {
          zoomFixation.Reset();
          // FixationDetector.Reset() does NOT fire FixationEnded, so reset UI manually:
          Dispatcher.BeginInvoke(() =>
          {
              zoomIndicatorVM.ResetFixation();
              zoomIndicator.AnimateReset(
                  settings.Zoom.GazeIndicatorOuterDiameter,
                  settings.Zoom.GazeIndicatorResetAnimationMs);
          });
          return;
      }

      zoomFixation.Update(pt, ts);
      // Update fixation progress on indicator
      var progress = zoomFixation.GetProgress();
      Dispatcher.BeginInvoke(() => {
          zoomIndicatorVM.UpdateFixationProgress(progress);
          zoomIndicator.UpdateOuterDiameter(zoomIndicatorVM.OuterCircleDiameter);
      });
  };
  ```

- [ ] **Step 3: Build**

  ```
  dotnet build src\EyeCtrl.App
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run existing tests (no regressions)**

  ```
  dotnet test tests\EyeCtrl.ViewModels.Tests
  ```

  Expected: All tests pass.

- [ ] **Step 5: Commit**

  ```
  git add src/EyeCtrl.App/App.xaml.cs
  git commit -m "fix: reject fixation outside zoom circle boundary"
  ```

---

## Chunk 2: Fix 2 — Action menu preview matches zoom region

### Task 2: Extend ShowActionMenuMessage with ZoomCenter / ZoomRadius

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (line 8)

- [ ] **Step 1: Add init-only properties to the record body**

  Current line 8:
  ```csharp
  public record ShowActionMenuMessage(ScreenPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack, int DwellTimeMs, int ZoomDisplayRadius = 0, double CircleSize = 250);
  ```

  Replace with:
  ```csharp
  public record ShowActionMenuMessage(ScreenPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack, int DwellTimeMs, int ZoomDisplayRadius = 0, double CircleSize = 250)
  {
      /// <summary>Physical-px center of the zoom circle (zoom entry point). Null for non-zoom paths.</summary>
      public ScreenPoint? ZoomCenter { get; init; }
      /// <summary>Logical/DIP radius of the zoom circle (CircleRadiusPixels). Null for non-zoom paths.</summary>
      public int? ZoomRadius { get; init; }
  }
  ```

  These are init-only (not positional) so the single existing call site in `GazeEngineAdapter.cs` requires no changes yet.

- [ ] **Step 2: Build to verify no regressions at existing call site**

  ```
  dotnet build src\EyeCtrl.ViewModels
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

  ```
  git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
  git commit -m "feat: add ZoomCenter/ZoomRadius init-only fields to ShowActionMenuMessage"
  ```

---

### Task 3: Store ZoomCenter / ZoomRadius in ActionMenuViewModel

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs`

- [ ] **Step 1: Write failing tests**

  Add to `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs` (before the closing `}`):

  ```csharp
  [Fact]
  public void ZoomContextProperties_AreStoredFromMessage()
  {
      var vm = new ActionMenuViewModel(new DwellSettings(), _messenger);
      var zoomCenter = new ScreenPoint(400, 300);

      _messenger.Send(new ShowActionMenuMessage(
          new ScreenPoint(400, 300),
          new List<InteractionAction> { InteractionAction.LeftClick },
          showBack: true, dwellTimeMs: 600)
      {
          ZoomCenter = zoomCenter,
          ZoomRadius = 150
      });

      Assert.Equal(zoomCenter, vm.ZoomCenter);
      Assert.Equal(150, vm.ZoomRadius);
  }

  [Fact]
  public void ZoomContextProperties_AreClearedOnHide()
  {
      var vm = new ActionMenuViewModel(new DwellSettings(), _messenger);
      _messenger.Send(new ShowActionMenuMessage(
          new ScreenPoint(400, 300),
          new List<InteractionAction> { InteractionAction.LeftClick },
          showBack: true, dwellTimeMs: 600)
      {
          ZoomCenter = new ScreenPoint(400, 300),
          ZoomRadius = 150
      });

      _messenger.Send(new HideActionMenuMessage());

      Assert.Null(vm.ZoomCenter);
      Assert.Null(vm.ZoomRadius);
  }
  ```

- [ ] **Step 2: Run tests — verify they FAIL**

  ```
  dotnet test tests\EyeCtrl.ViewModels.Tests --filter "ZoomContext"
  ```

  Expected: 2 failures (properties don't exist yet).

- [ ] **Step 3: Add properties to ActionMenuViewModel**

  In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, after the existing `[ObservableProperty]` fields (around line 28), add:

  ```csharp
  public ScreenPoint? ZoomCenter { get; private set; }
  public int? ZoomRadius { get; private set; }
  ```

- [ ] **Step 4: Populate from message in Receive(ShowActionMenuMessage)**

  Inside `public void Receive(ShowActionMenuMessage message)`, after `Center = message.Center;` (line 42), add:

  ```csharp
  ZoomCenter = message.ZoomCenter;
  ZoomRadius = message.ZoomRadius;
  ```

- [ ] **Step 5: Clear on hide in Receive(HideActionMenuMessage)**

  Inside `public void Receive(HideActionMenuMessage message)`, after `IsVisible = false;`, add:

  ```csharp
  ZoomCenter = null;
  ZoomRadius = null;
  ```

- [ ] **Step 6: Run tests — verify they PASS**

  ```
  dotnet test tests\EyeCtrl.ViewModels.Tests
  ```

  Expected: All tests pass (including the 2 new ones).

- [ ] **Step 7: Commit**

  ```
  git add src/EyeCtrl.ViewModels/ActionMenuViewModel.cs
  git add tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs
  git commit -m "feat: store ZoomCenter/ZoomRadius in ActionMenuViewModel"
  ```

---

### Task 4: Populate ZoomCenter / ZoomRadius in GazeEngineAdapter

**Files:**
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` (line 101)

- [ ] **Step 1: Update the Send call to populate the new fields**

  Current line 101:
  ```csharp
  _messenger.Send(new ShowActionMenuMessage(clampedPoint, _settings.ActionMenu.VisibleActions, showBack, dwellMs, zoomDisplayRadius, circleSize));
  ```

  Replace with:
  ```csharp
  _messenger.Send(new ShowActionMenuMessage(clampedPoint, _settings.ActionMenu.VisibleActions, showBack, dwellMs, zoomDisplayRadius, circleSize)
  {
      // Zoom path only (showBack == true): pass zoom circle context for preview capture.
      // ZoomCenter = the ORIGINAL pre-zoom fixation (center of zoom circle), NOT the precise in-zoom point.
      // The precise in-zoom point is already in clampedPoint / message.Center.
      // FixationPoint is always non-null in the zoom path (it is set before zoom activation).
      // ZoomRadius is logical/DIP (CircleRadiusPixels); ActionMenuWindow converts it to physical with ScaleX.
      ZoomCenter = showBack ? _interactionManager.FixationPoint! : null,
      ZoomRadius = showBack ? _settings.Zoom.CircleRadiusPixels : null
  });
  ```

- [ ] **Step 2: Build**

  ```
  dotnet build src\EyeCtrl.App
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run all tests**

  ```
  dotnet test tests\EyeCtrl.ViewModels.Tests
  ```

  Expected: All tests pass.

- [ ] **Step 4: Commit**

  ```
  git add src/EyeCtrl.App/Services/GazeEngineAdapter.cs
  git commit -m "feat: pass zoom circle context in ShowActionMenuMessage from GazeEngineAdapter"
  ```

---

### Task 5: Pass ZoomCenter / ZoomRadius to ActionMenuWindow.Show()

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` (lines 423–430)

- [ ] **Step 1: Update the Show() call**

  Current (lines 423–430):
  ```csharp
  actionMenu.Show(actionMenuVM.Center,
      settings.ActionMenu.VisibleActions,
      actionMenuVM.ShowBack,
      actionMenuVM.ShowBack ? settings.Dwell.ZoomActionMenuDwellMs : settings.Dwell.ActionMenuDwellMs,
      actionMenuVM.EffectiveRingRadius,
      settings.Cursor.CircleSize,
      settings.Cursor.CircleColor,
      settings.Cursor.PositionIndicatorStyle);
  ```

  Replace with:
  ```csharp
  actionMenu.Show(actionMenuVM.Center,
      settings.ActionMenu.VisibleActions,
      actionMenuVM.ShowBack,
      actionMenuVM.ShowBack ? settings.Dwell.ZoomActionMenuDwellMs : settings.Dwell.ActionMenuDwellMs,
      actionMenuVM.EffectiveRingRadius,
      settings.Cursor.CircleSize,
      settings.Cursor.CircleColor,
      settings.Cursor.PositionIndicatorStyle,
      actionMenuVM.ZoomCenter,
      actionMenuVM.ZoomRadius);
  ```

  This will not compile until Task 6 adds the new parameters to `ActionMenuWindow.Show()`. That is expected — **do not build here, proceed directly to Task 6**.

---

### Task 6: Zoom capture and indicator offset in ActionMenuWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`

- [ ] **Step 1: Add optional parameters to Show() signature**

  Current signature (line 25):
  ```csharp
  public void Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor, PositionIndicatorStyle positionIndicatorStyle)
  ```

  Replace with:
  ```csharp
  public void Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor, PositionIndicatorStyle positionIndicatorStyle, ScreenPoint? zoomCenter = null, int? zoomRadius = null)
  ```

- [ ] **Step 2: Update capture center and radius**

  Current (lines 48–50):
  ```csharp
  var previewRadius = (int)(previewCircleSize / 2);
  var captureCenter = center;
  var captureRadius = previewRadius;
  ```

  Replace with:
  ```csharp
  var previewRadius = (int)(previewCircleSize / 2);
  // In zoom path: capture the same region the zoom view showed (same center, same radius)
  var captureCenter = zoomCenter ?? center;
  var captureRadius = zoomRadius.HasValue ? (int)(zoomRadius.Value * DpiHelper.ScaleX) : previewRadius;
  ```

- [ ] **Step 3: Compute indicator offset**

  Add the following block immediately after the `var captureCircleSize = previewCircleSize;` line (around line 52), before the async capture block:

  ```csharp
  // Compute where within the preview circle the precise fixation point (center) lies.
  // When not in zoom mode both offsets are 0 and the indicator stays at canvasCenter.
  double indicatorOffsetX = 0;
  double indicatorOffsetY = 0;
  if (zoomCenter.HasValue && zoomRadius.HasValue)
  {
      // scale = logical-px / physical-px  →  offset = physical-px × (logical/physical) = logical-px
      // Use captureRadius (already in physical px) so the result is in WPF logical canvas px.
      double scale = previewCircleSize / (2.0 * captureRadius);
      indicatorOffsetX = (center.X - zoomCenter.Value.X) * scale;
      indicatorOffsetY = (center.Y - zoomCenter.Value.Y) * scale;

      // Clamp so indicator stays inside the preview circle (10 px margin)
      double offsetMag = Math.Sqrt(indicatorOffsetX * indicatorOffsetX + indicatorOffsetY * indicatorOffsetY);
      double maxOffset = previewCircleSize / 2 - 10;
      if (offsetMag > maxOffset && offsetMag > 0)
      {
          indicatorOffsetX = indicatorOffsetX / offsetMag * maxOffset;
          indicatorOffsetY = indicatorOffsetY / offsetMag * maxOffset;
      }
  }
  ```

- [ ] **Step 4: Apply offset to crosshair**

  Current crosshair block (lines 80–99):
  ```csharp
  var hLine = new Line
  {
      X1 = canvasCenter.X - indicatorSize,
      Y1 = canvasCenter.Y,
      X2 = canvasCenter.X + indicatorSize,
      Y2 = canvasCenter.Y,
      ...
  };
  var vLine = new Line
  {
      X1 = canvasCenter.X,
      Y1 = canvasCenter.Y - indicatorSize,
      X2 = canvasCenter.X,
      Y2 = canvasCenter.Y + indicatorSize,
      ...
  };
  ```

  Replace with:
  ```csharp
  var hLine = new Line
  {
      X1 = canvasCenter.X - indicatorSize + indicatorOffsetX,
      Y1 = canvasCenter.Y + indicatorOffsetY,
      X2 = canvasCenter.X + indicatorSize + indicatorOffsetX,
      Y2 = canvasCenter.Y + indicatorOffsetY,
      Stroke = Brushes.Red,
      StrokeThickness = 1.5
  };
  var vLine = new Line
  {
      X1 = canvasCenter.X + indicatorOffsetX,
      Y1 = canvasCenter.Y - indicatorSize + indicatorOffsetY,
      X2 = canvasCenter.X + indicatorOffsetX,
      Y2 = canvasCenter.Y + indicatorSize + indicatorOffsetY,
      Stroke = Brushes.Red,
      StrokeThickness = 1.5
  };
  ```

- [ ] **Step 5: Apply offset to pin**

  Current pin block (lines 104–126):
  ```csharp
  Canvas.SetLeft(pinHead, canvasCenter.X - pinHeadSize / 2);
  Canvas.SetTop(pinHead, canvasCenter.Y - indicatorSize);

  var pinLine = new Line
  {
      X1 = canvasCenter.X,
      Y1 = canvasCenter.Y - indicatorSize / 2,
      X2 = canvasCenter.X,
      Y2 = canvasCenter.Y,
      ...
  };
  ```

  Replace with (shift all coordinates by the offset):
  ```csharp
  Canvas.SetLeft(pinHead, canvasCenter.X - pinHeadSize / 2 + indicatorOffsetX);
  Canvas.SetTop(pinHead, canvasCenter.Y - indicatorSize + indicatorOffsetY);

  var pinLine = new Line
  {
      X1 = canvasCenter.X + indicatorOffsetX,
      Y1 = canvasCenter.Y - indicatorSize / 2 + indicatorOffsetY,
      X2 = canvasCenter.X + indicatorOffsetX,
      Y2 = canvasCenter.Y + indicatorOffsetY,
      Stroke = Brushes.Red,
      StrokeThickness = 2
  };
  ```

- [ ] **Step 6: Build**

  ```
  dotnet build src\EyeCtrl.App
  ```

  Expected: Build succeeded, 0 errors. The Task 5 compile error is now resolved.

- [ ] **Step 7: Run all tests**

  ```
  dotnet test tests\EyeCtrl.ViewModels.Tests
  dotnet test tests\EyeCtrl.Core.Tests
  ```

  Expected: All tests pass.

- [ ] **Step 8: Commit**

  ```
  git add src/EyeCtrl.App/App.xaml.cs
  git add src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs
  git commit -m "fix: action menu preview shows zoom region with precise fixation indicator"
  ```

---

## Manual Verification Checklist

After all tasks are complete, verify in the running app (or VM):

- **Fix 1:** In zoom mode, slowly move gaze outside the zoom circle — the fixation progress ring resets to zero and does not advance while outside the circle.
- **Fix 2:** In zoom mode, fixate on a point inside the zoom view to trigger the action menu — the preview circle in the center of the action menu shows the same screen region that was visible in zoom, with the precise fixation point marked by the crosshair/pin inside the preview.
