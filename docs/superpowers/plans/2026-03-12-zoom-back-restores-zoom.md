# Zoom Back Restores Zoom View — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the Back button in the zoom action menu so it restores the zoom view fresh, by routing `HideZoom` through the message bus instead of handling it directly in `App.xaml.cs`.

**Architecture:** Add `ZoomHiddenMessage` to the message bus. `GazeEngineAdapter` subscribes to `InteractionManager.HideZoom` and sends the message. `ZoomOverlayViewModel` receives it and sets `IsVisible = false`, making the subsequent `StartZoomMessage` (sent by Back navigation) correctly transition `false → true` and trigger the full zoom re-init in `App.xaml.cs`.

**Tech Stack:** C# / .NET, CommunityToolkit.Mvvm (`WeakReferenceMessenger`, `ObservableProperty`), xUnit

**Spec:** `docs/superpowers/specs/2026-03-12-zoom-back-restores-zoom-design.md`

---

## Chunk 1: ViewModel layer — message + tests + implementation

### Task 1: Add `ZoomHiddenMessage` and write failing ViewModel tests

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ZoomOverlayViewModelTests.cs`

- [ ] **Step 1: Add `ZoomHiddenMessage` to the messages file**

  Open `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` and add after `public record EndZoomMessage;`:

  ```csharp
  public record ZoomHiddenMessage;
  ```

- [ ] **Step 2: Write two failing tests in `ZoomOverlayViewModelTests.cs`**

  Append these two tests to the class (before the closing `}`):

  ```csharp
  [Fact]
  public void HidesZoom_OnZoomHiddenMessage()
  {
      var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);
      _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));
      Assert.True(vm.IsVisible);

      _messenger.Send(new ZoomHiddenMessage());

      Assert.False(vm.IsVisible);
  }

  [Fact]
  public void ReshowsZoom_AfterZoomHiddenThenStartZoom()
  {
      // Simulates: zoom shown → fixation detected (ZoomHidden) → Back pressed (StartZoom again)
      var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);
      _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));
      _messenger.Send(new ZoomHiddenMessage());
      Assert.False(vm.IsVisible);

      _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));

      Assert.True(vm.IsVisible);
  }
  ```

- [ ] **Step 3: Run tests to confirm they fail**

  ```
  dotnet test tests/EyeCtrl.ViewModels.Tests/ --filter "ZoomOverlay" -v
  ```

  Expected: both new tests fail with something like `ZoomHiddenMessage is not defined` or the ViewModel not implementing the recipient.

### Task 2: Implement `IRecipient<ZoomHiddenMessage>` in `ZoomOverlayViewModel`

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs`

- [ ] **Step 1: Add `IRecipient<ZoomHiddenMessage>` to the class declaration**

  Change:
  ```csharp
  public partial class ZoomOverlayViewModel : ObservableObject,
      IRecipient<StartZoomMessage>,
      IRecipient<EndZoomMessage>,
      IRecipient<GazeCursorMovedMessage>
  ```
  To:
  ```csharp
  public partial class ZoomOverlayViewModel : ObservableObject,
      IRecipient<StartZoomMessage>,
      IRecipient<EndZoomMessage>,
      IRecipient<ZoomHiddenMessage>,
      IRecipient<GazeCursorMovedMessage>
  ```

- [ ] **Step 2: Add the `Receive(ZoomHiddenMessage)` method**

  Add after `Receive(EndZoomMessage)` (around line 78):

  ```csharp
  public void Receive(ZoomHiddenMessage message)
  {
      IsVisible = false;
      _dwellDetector.ClearTargets();
      CloseButtonProgress = 0;
      RecaptureButtonProgress = 0;
  }
  ```

- [ ] **Step 3: Run tests to confirm they pass**

  ```
  dotnet test tests/EyeCtrl.ViewModels.Tests/ --filter "ZoomOverlay" -v
  ```

  Expected: all `ZoomOverlayViewModelTests` pass, including the two new ones.

- [ ] **Step 4: Run the full test suite to confirm nothing is broken**

  ```
  dotnet test --filter "EyeCtrl.ViewModels.Tests|EyeCtrl.Core.Tests" -v
  ```

  Expected: all tests pass.

- [ ] **Step 5: Commit**

  ```
  git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
  git add src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs
  git add tests/EyeCtrl.ViewModels.Tests/ZoomOverlayViewModelTests.cs
  git commit -m "feat: add ZoomHiddenMessage; ZoomOverlayViewModel hides on zoom fixation"
  ```

---

## Chunk 2: Adapter + App wiring — route HideZoom, remove direct handler

### Task 3: Subscribe to `HideZoom` in `GazeEngineAdapter`

**Files:**
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`

- [ ] **Step 1: Subscribe to `HideZoom` in the constructor**

  In `GazeEngineAdapter`'s constructor, in the block that subscribes to `_interactionManager` events (around line 38–44), add:

  ```csharp
  _interactionManager.HideZoom += OnHideZoom;
  ```

- [ ] **Step 2: Add the `OnHideZoom` method**

  Add after `OnEndZoom()` (around line 109):

  ```csharp
  private void OnHideZoom() =>
      _dispatcher.BeginInvoke(() => _messenger.Send(new ZoomHiddenMessage()));
  ```

- [ ] **Step 3: Unsubscribe in `Dispose()`**

  In the `Dispose()` method, add:

  ```csharp
  _interactionManager.HideZoom -= OnHideZoom;
  ```

### Task 4: Remove the direct `im.HideZoom` handler from `App.xaml.cs`

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Delete the direct `im.HideZoom` handler**

  Find and remove this block (around line 369–374):

  ```csharp
  im.HideZoom += () => Dispatcher.BeginInvoke(() =>
  {
      zoomOverlay.Hide();
      zoomSideButtons.Hide();
      zoomIndicator.Hide();
  });
  ```

  This is now handled via `ZoomHiddenMessage → ZoomOverlayViewModel.IsVisible = false → PropertyChanged` in the existing `zoomVM.PropertyChanged` handlers which already call `zoomOverlay.Hide()`, `zoomIndicator.Hide()`, `zoomSideButtons.Hide()`, and unsubscribe the gaze handler.

- [ ] **Step 2: Build the solution**

  ```
  dotnet build EyeCtrl.slnx
  ```

  Expected: no errors, no warnings related to the change.

- [ ] **Step 3: Run the full test suite**

  ```
  dotnet test --filter "EyeCtrl.ViewModels.Tests|EyeCtrl.Core.Tests" -v
  ```

  Expected: all tests pass.

- [ ] **Step 4: Commit**

  ```
  git add src/EyeCtrl.App/Services/GazeEngineAdapter.cs
  git add src/EyeCtrl.App/App.xaml.cs
  git commit -m "fix: route HideZoom through message bus; back button now restores zoom view"
  ```

---

## Chunk 3: Manual smoke test

### Task 5: Verify the fix end-to-end in test mode

- [ ] **Step 1: Launch in test mode (mouse acts as gaze)**

  ```
  dotnet run --project src/EyeCtrl.App -- --test-mode
  ```

- [ ] **Step 2: Verify Back restores zoom**

  1. Enable zoom in toolbar (⊕ button)
  2. Hover over a target area — activation button appears
  3. Dwell activation button — zoom overlay appears
  4. Move mouse to a point inside the zoom circle and hold — fixation detected, zoom hides, action menu appears with a Back (↩) button
  5. Dwell the Back button — **zoom overlay should reappear at the same position, with the gaze indicator fresh and the grace period restarting**
  6. Confirm you can fixate again in zoom and get the action menu a second time

  **Expected before fix:** zoom does not reappear after Back, user is stuck.
  **Expected after fix:** zoom reappears identically to first activation.

- [ ] **Step 3: Verify Recapture still works**

  With zoom visible, dwell the Recapture button (lower side button) — zoom should re-show with a fresh FixationDetector.

- [ ] **Step 4: Verify normal (non-zoom) action menu is unaffected**

  Disable zoom, dwell activation button — action menu appears without Back button, action executes normally.
