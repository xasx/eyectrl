# Fix Zoom Mode Wrong Position — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix zoom mode actions landing at the wrong screen position by removing duplicate action execution from GazeEngine.

**Architecture:** Remove `GazeEngine.OnActionRequested` (uses wrong centroid in zoom mode), keep `App.xaml.cs` message handler as sole executor. Expose centroid from GazeEngine so App.xaml.cs can use it for non-zoom actions. Add `IsFromZoom` flag to `ActionRequestedMessage` to distinguish.

**Tech Stack:** C# / .NET / WPF / CommunityToolkit.Mvvm

**Design doc:** `docs/plans/2026-03-07-zoom-wrong-position-design.md`

---

### Task 1: Add IsFromZoom to ActionRequestedMessage

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs:13`

**Step 1: Edit the record**

Change line 13 from:
```csharp
public record ActionRequestedMessage(InteractionAction Action, ScreenPoint Point);
```
to:
```csharp
public record ActionRequestedMessage(InteractionAction Action, ScreenPoint Point, bool IsFromZoom = false);
```

**Step 2: Build to verify no breaks**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded (default parameter keeps existing call sites working)

**Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
git commit -m "feat: add IsFromZoom flag to ActionRequestedMessage"
```

---

### Task 2: Set IsFromZoom in GazeEngineAdapter

**Files:**
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:101-102`

**Step 1: Edit OnActionRequested**

Change the `OnActionRequested` method (line 101-102) from:
```csharp
private void OnActionRequested(InteractionAction action, ScreenPoint point) =>
    _dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point)));
```
to:
```csharp
private void OnActionRequested(InteractionAction action, ScreenPoint point)
{
    var isFromZoom = _interactionManager.CurrentState == InteractionState.ZoomActionMenuVisible;
    _dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point, isFromZoom)));
}
```

Note: `CurrentState` is read BEFORE `OnActionSelected` calls `ResetToFreeLook`, because `ActionRequested` fires before `ResetToFreeLook` in `InteractionManager.OnActionSelected`.

**Step 2: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/Services/GazeEngineAdapter.cs
git commit -m "feat: set IsFromZoom flag when sending ActionRequestedMessage"
```

---

### Task 3: Expose StableCentroid from GazeEngine

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs:18` (add property after `SuppressFixation`)

**Step 1: Add StableCentroid property**

After line 18 (`public bool SuppressFixation { get; set; }`), add:
```csharp
public ScreenPoint? StableCentroid => _centroidTracker.StableCentroid;
```

**Step 2: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs
git commit -m "feat: expose StableCentroid from GazeEngine"
```

---

### Task 4: Remove OnActionRequested from GazeEngine

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs:59` (remove subscription)
- Modify: `src/EyeCtrl.Core/GazeEngine.cs:94-127` (remove method)

**Step 1: Remove the event subscription**

Delete line 59:
```csharp
InteractionManager.ActionRequested += OnActionRequested;
```

**Step 2: Remove the OnActionRequested method**

Delete lines 94-127 (the entire `OnActionRequested` method).

**Step 3: Run all tests**

Run: `dotnet test EyeCtrl.slnx`
Expected: All tests pass (InteractionManagerTests don't depend on GazeEngine action handling)

**Step 4: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs
git commit -m "fix: remove duplicate action execution from GazeEngine"
```

---

### Task 5: Use centroid in App.xaml.cs for non-zoom actions

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:327-342`

**Step 1: Update ActionRequestedMessage handler**

Replace the existing handler (lines 327-342):
```csharp
messenger.Register<ActionRequestedMessage>(this, (_, msg) => Dispatcher.BeginInvoke(() =>
{
    var pt = msg.Point;
    switch (msg.Action)
    {
        case InteractionAction.LeftClick:    inputSimulator.LeftClick(pt);        break;
        case InteractionAction.RightClick:   inputSimulator.RightClick(pt);       break;
        case InteractionAction.DoubleClick:  inputSimulator.DoubleClick(pt);      break;
        case InteractionAction.DragStart:    inputSimulator.MouseDown(pt);        break;
        case InteractionAction.DragEnd:      inputSimulator.MouseUp(pt);          break;
        case InteractionAction.ScrollUp:     inputSimulator.Scroll(pt, 0, 3);    break;
        case InteractionAction.ScrollDown:   inputSimulator.Scroll(pt, 0, -3);   break;
        case InteractionAction.ScrollLeft:   inputSimulator.Scroll(pt, -3, 0);   break;
        case InteractionAction.ScrollRight:  inputSimulator.Scroll(pt, 3, 0);    break;
    }
}));
```

With:
```csharp
messenger.Register<ActionRequestedMessage>(this, (_, msg) => Dispatcher.BeginInvoke(() =>
{
    // Zoom: use the precise real-screen coordinate from GetRealScreenPoint
    // Normal: use centroid tracker for refined accuracy, fall back to fixation point
    var pt = msg.IsFromZoom ? msg.Point : (gazeEngine.StableCentroid ?? msg.Point);
    switch (msg.Action)
    {
        case InteractionAction.LeftClick:    inputSimulator.LeftClick(pt);        break;
        case InteractionAction.RightClick:   inputSimulator.RightClick(pt);       break;
        case InteractionAction.DoubleClick:  inputSimulator.DoubleClick(pt);      break;
        case InteractionAction.DragStart:    inputSimulator.MouseDown(pt);        break;
        case InteractionAction.DragEnd:      inputSimulator.MouseUp(pt);          break;
        case InteractionAction.ScrollUp:     inputSimulator.Scroll(pt, 0, 3);    break;
        case InteractionAction.ScrollDown:   inputSimulator.Scroll(pt, 0, -3);   break;
        case InteractionAction.ScrollLeft:   inputSimulator.Scroll(pt, -3, 0);   break;
        case InteractionAction.ScrollRight:  inputSimulator.Scroll(pt, 3, 0);    break;
    }
}));
```

**Step 2: Build and run all tests**

Run: `dotnet test EyeCtrl.slnx`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "fix: use correct point source for zoom vs normal actions"
```

---

### Task 6: Verify

**Step 1: Run full test suite**

Run: `dotnet test EyeCtrl.slnx`
Expected: All tests pass

**Step 2: Build publish-ready**

Run: `dotnet publish src/EyeCtrl.App/EyeCtrl.App.csproj -r win-x64 --self-contained true -p:PublishSingleFile=true`
Expected: Build succeeded

**Step 3: Manual test checklist**
- Normal mode: left click lands at fixation point (with centroid refinement)
- Zoom mode: left click lands at precise unmagnified real position
- No double-clicks in either mode
- Back button in zoom returns to free look
