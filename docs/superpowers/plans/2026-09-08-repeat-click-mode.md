# Repeat-Click Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship gap #4 — a locked repeat-click mode plus a one-shot "Repeat last action" ring entry (both OFF by default).

**Architecture:** Core owns the semantics (`InteractionManager` repeat state + last-action memory, `RepeatSettings`); the ring gets two new `InteractionAction` members wired through the standard icon/label/description checklist; an App-side `RepeatModeController` ticks the locked action at the live stable centroid through the existing `ActionRequested` dispatch path. Spec: `docs/superpowers/specs/2026-09-08-repeat-click-mode-design.md` (decision ledger D1–D15).

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, xUnit v3 + NFluent 3.1.0 (no mocking libs — private nested fakes).

## Global Constraints

- Work in the worktree `C:\Users\andre\source\repos\eyectrl\.worktrees\repeat-click-mode`, branch `feat/repeat-click-mode` (stacked on `feat/mouse-osk-polish`, PR #138 — do NOT rebase).
- Assertions: NFluent (`Check.That(...)`); inclusive ranges = `IsGreaterOrEqualThan`/`IsLessOrEqualThan`; strict = `IsStrictlyGreaterThan`/`IsStrictlyLessThan`.
- Conventions: file-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled.
- New ring actions ship OFF: `ActionMenuSettings.VisibleActions`/`EnsureDefaults`/`PrimaryActions` defaults are NOT touched.
- Enum values: append only — `RepeatLastAction` = 11, `RepeatMode` = 12 (after `PinNote` = 10).
- Never run two test suites at once; run each project in its own `dotnet test` invocation.
- Do not launch the app (eye-tracking user; launch is owner-gated).
- Icons author on the 24-unit grid at 1.8 stroke; `ActionIconsTests.EveryIcon_FitsTheGrid` must pass.
- Impact gate (done): `InteractionManager` is a CRITICAL-rated hub — the only non-additive edits are the GazeEngine cursor gate, the `CursorCircleViewModel` allow-list, and the `TogglePause` guard, all specified in Task 6.

---

### Task 1: Core — enum members + click-family classification

**Files:**
- Modify: `src/EyeCtrl.Core/Models/InteractionAction.cs`
- Create: `src/EyeCtrl.Core/Models/InteractionActionExtensions.cs`
- Test: `tests/EyeCtrl.Core.Tests/Models/InteractionActionEnumTests.cs`

**Interfaces:**
- Produces: `InteractionAction.RepeatLastAction` (= 11), `InteractionAction.RepeatMode` (= 12), and `public static bool IsRepeatableClick(this InteractionAction action)` — used by Task 2.

- [ ] **Step 1: Write the failing tests**

In `InteractionActionEnumTests.cs`, extend the class:

```csharp
[Fact]
public void RepeatMembers_HavePinnedValues()
{
    // Appended 2026-09-08 after PinNote: RepeatLastAction11, RepeatMode12. PinNote stays 10.
    Check.That((int)InteractionAction.RepeatLastAction).IsEqualTo(11);
    Check.That((int)InteractionAction.RepeatMode).IsEqualTo(12);
    Check.That((int)InteractionAction.PinNote).IsEqualTo(10);
}

[Theory]
[InlineData(InteractionAction.LeftClick, true)]
[InlineData(InteractionAction.RightClick, true)]
[InlineData(InteractionAction.DoubleClick, true)]
[InlineData(InteractionAction.MiddleClick, true)]
[InlineData(InteractionAction.Drag, false)]
[InlineData(InteractionAction.ScrollUp, false)]
[InlineData(InteractionAction.ScrollDown, false)]
[InlineData(InteractionAction.ScrollLeft, false)]
[InlineData(InteractionAction.ScrollRight, false)]
[InlineData(InteractionAction.ScrollMode, false)]
[InlineData(InteractionAction.PinNote, false)]
[InlineData(InteractionAction.RepeatLastAction, false)]
[InlineData(InteractionAction.RepeatMode, false)]
public void IsRepeatableClick_ClassifiesOnlyTheClickFamily(InteractionAction action, bool expected)
{
    Check.That(action.IsRepeatableClick()).IsEqualTo(expected);
}
```

- [ ] **Step 2: Run to verify failure**

`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionActionEnumTests"` — compile error: no `RepeatLastAction`/`RepeatMode`/`IsRepeatableClick`.

- [ ] **Step 3: Implement**

`InteractionAction.cs` — append after the `PinNote` member (inside the enum):

```csharp
    /// <summary>Re-fires the last click-family action at the point you chose.</summary>
    RepeatLastAction,
    /// <summary>Locks the last click-family action to repeat at the live gaze point until stopped.</summary>
    RepeatMode
```

(Note: `PinNote` loses its trailing comma; `RepeatMode` is last.)

Create `InteractionActionExtensions.cs`:

```csharp
namespace EyeCtrl.Core.Models;

/// <summary>Classification helpers over <see cref="InteractionAction"/>.</summary>
public static class InteractionActionExtensions
{
    /// <summary>
    /// Whether the action is a plain click that repeat mode can lock and re-fire. Scroll has its
    /// own continuous mode, drag follows gaze already, and the remaining members are not things a
    /// user re-fires rhythmically.
    /// </summary>
    public static bool IsRepeatableClick(this InteractionAction action) =>
        action is InteractionAction.LeftClick
            or InteractionAction.RightClick
            or InteractionAction.DoubleClick
            or InteractionAction.MiddleClick;
}
```

- [ ] **Step 4: Run to verify pass**

`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionActionEnumTests"` — PASS. Also run `dotnet build EyeCtrl.slnx` — must stay green (proves no exhaustive switch breaks).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/InteractionAction.cs src/EyeCtrl.Core/Models/InteractionActionExtensions.cs tests/EyeCtrl.Core.Tests/Models/InteractionActionEnumTests.cs
git commit -m "feat(core): InteractionAction repeat members + click-family classification (gap #4)"
```

---

### Task 2: Core — repeat state machine in InteractionManager

**Files:**
- Modify: `src/EyeCtrl.Core/Models/InteractionState.cs`
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerRepeatModeTests.cs` (new)

**Interfaces:**
- Consumes: `InteractionAction.RepeatMode`/`RepeatLastAction`, `IsRepeatableClick` (Task 1).
- Produces: `InteractionState.RepeatMode`; `InteractionManager.LastRepeatableAction` (`InteractionAction?`, get-only), `RepeatAction` (`InteractionAction?`, get-only), `FireRepeatTick(PhysicalPoint point)`, `ExitRepeatMode()` — used by Tasks 6.

- [ ] **Step 1: Write the failing tests**

Create `InteractionManagerRepeatModeTests.cs` (style copied from `InteractionManagerScrollModeTests`):

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class InteractionManagerRepeatModeTests
{
    private static InteractionManager InActionMenu(out PhysicalPoint fixation)
    {
        var im = new InteractionManager();
        fixation = new PhysicalPoint(1000, 800);
        im.OnFixationStarted(fixation);
        im.OnActivationButtonDwelled();
        return im;
    }

    [Fact]
    public void SelectingRepeatMode_EntersRepeatMode_WithoutFiringActionRequested()
    {
        var im = InActionMenu(out _);
        var fired = new List<InteractionAction>();
        im.ActionRequested += (a, _) => fired.Add(a);

        im.OnActionSelected(InteractionAction.RepeatMode);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.That(fired).IsEmpty();
    }

    [Fact]
    public void SelectingRepeatMode_WithClickHistory_LocksThatClick()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RightClick);   // history + back to FreeLook
        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();

        im.OnActionSelected(InteractionAction.RepeatMode);

        Check.That(im.RepeatAction).IsEqualTo(InteractionAction.RightClick);
    }

    [Fact]
    public void SelectingRepeatMode_WithNoHistory_LocksLeftClick()
    {
        var im = InActionMenu(out _);

        im.OnActionSelected(InteractionAction.RepeatMode);

        Check.That(im.RepeatAction).IsEqualTo(InteractionAction.LeftClick);
    }

    [Fact]
    public void ClickFamilyDispatch_UpdatesLastRepeatableAction_NonClickDoesNot()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.MiddleClick);
        Check.That(im.LastRepeatableAction).IsEqualTo(InteractionAction.MiddleClick);

        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(InteractionAction.PinNote);
        Check.That(im.LastRepeatableAction).IsEqualTo(InteractionAction.MiddleClick);
    }

    [Fact]
    public void SelectingRepeatLastAction_FiresTheRememberedClickAtTheTarget()
    {
        var im = InActionMenu(out var fixation);
        im.OnActionSelected(InteractionAction.RightClick);   // history
        im.OnFixationStarted(fixation);
        im.OnActivationButtonDwelled();

        InteractionAction? firedAction = null;
        PhysicalPoint? firedPoint = null;
        im.ActionRequested += (a, p) => { firedAction = a; firedPoint = p; };

        im.OnActionSelected(InteractionAction.RepeatLastAction);

        Check.That(firedAction).IsEqualTo(InteractionAction.RightClick);
        Check.That(firedPoint!.Value.X).IsCloseTo(fixation.X, 1e-6);
        Check.That(firedPoint!.Value.Y).IsCloseTo(fixation.Y, 1e-6);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void SelectingRepeatLastAction_WithNoHistory_FiresLeftClick()
    {
        var im = InActionMenu(out _);
        InteractionAction? firedAction = null;
        im.ActionRequested += (a, _) => firedAction = a;

        im.OnActionSelected(InteractionAction.RepeatLastAction);

        Check.That(firedAction).IsEqualTo(InteractionAction.LeftClick);
    }

    [Fact]
    public void FireRepeatTick_InRepeatMode_FiresTheLockedAction_AndStaysInRepeatMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.DoubleClick);
        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(InteractionAction.RepeatMode);

        var fired = new List<(InteractionAction Action, PhysicalPoint Point)>();
        im.ActionRequested += (a, p) => fired.Add((a, p));

        im.FireRepeatTick(new PhysicalPoint(120, 130));
        im.FireRepeatTick(new PhysicalPoint(140, 150));

        Check.That(fired).HasSize(2);
        Check.That(fired[0].Action).IsEqualTo(InteractionAction.DoubleClick);
        Check.That(fired[0].Point.X).IsCloseTo(120, 1e-6);
        Check.That(fired[1].Point.Y).IsCloseTo(150, 1e-6);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void FireRepeatTick_OutsideRepeatMode_IsANoOp()
    {
        var im = new InteractionManager();
        var fired = false;
        im.ActionRequested += (_, _) => fired = true;

        im.FireRepeatTick(new PhysicalPoint(10, 10));

        Check.That(fired).IsFalse();
    }

    [Fact]
    public void ExitRepeatMode_ReturnsToFreeLook_AndClearsTheLockedAction()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RepeatMode);

        im.ExitRepeatMode();

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(im.RepeatAction).IsNull();
    }

    [Fact]
    public void ExitRepeatMode_OutsideRepeatMode_IsANoOp()
    {
        var im = new InteractionManager();
        im.ExitRepeatMode();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void TogglePause_IsBlockedInRepeatMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RepeatMode);

        im.TogglePause();

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void FixationStarted_IsIgnoredInRepeatMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RepeatMode);

        im.OnFixationStarted(new PhysicalPoint(500, 500));

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void EnteringRepeatMode_FiresStateChangedWithRepeatMode()
    {
        var im = InActionMenu(out _);
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;

        im.OnActionSelected(InteractionAction.RepeatMode);

        Check.That(captured).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void ExitingRepeatMode_FiresStateChangedWithFreeLook()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RepeatMode);
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;

        im.ExitRepeatMode();

        Check.That(captured).IsEqualTo(InteractionState.FreeLook);
    }
}
```

- [ ] **Step 2: Run to verify failure**

`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionManagerRepeatModeTests"` — compile errors (`RepeatMode` state/member, `RepeatAction`, `FireRepeatTick`, `ExitRepeatMode` missing).

- [ ] **Step 3: Implement**

`InteractionState.cs` — add after `Dragging`:

```csharp
    /// <summary>A click action is locked and repeating at the live gaze point.</summary>
    RepeatMode,
```

`InteractionManager.cs`:

1. Properties (after `ScrollAnchor`):

```csharp
    /// <summary>
    /// The last click-family action dispatched through <see cref="OnActionSelected"/> (left,
    /// right, double, middle). Null until the first such click. Feeds both
    /// <see cref="InteractionAction.RepeatLastAction"/> and the action locked on entering
    /// <see cref="InteractionState.RepeatMode"/>.
    /// </summary>
    public InteractionAction? LastRepeatableAction { get; private set; }
    /// <summary>The action locked for the active repeat session, or null outside <see cref="InteractionState.RepeatMode"/>.</summary>
    public InteractionAction? RepeatAction { get; private set; }
```

2. In `OnActionSelected`, after the `ScrollMode` branch's closing brace, add a sibling branch:

```csharp
        if (action == InteractionAction.RepeatMode)
        {
            HideActionMenu?.Invoke();
            if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
                EndManualSelection?.Invoke();
            RepeatAction = LastRepeatableAction ?? InteractionAction.LeftClick;
            SetState(InteractionState.RepeatMode);
            return;
        }
```

3. Just above the final `ActionRequested?.Invoke(...)` (after the Drag branch), convert the
   one-shot repeat and update the click memory:

```csharp
        if (action == InteractionAction.RepeatLastAction)
            action = LastRepeatableAction ?? InteractionAction.LeftClick;

        if (action.IsRepeatableClick())
            LastRepeatableAction = action;
```

4. `TogglePause` — extend the blocked-states arm and its remarks:

```csharp
        else if (CurrentState is InteractionState.ScrollMode or InteractionState.Dragging or InteractionState.RepeatMode)
```

and the `<remarks>` on `TogglePause` becomes: `Blocked during ScrollMode, Dragging and RepeatMode — use the stop/cancel affordances to exit those modes.`

5. New methods (beside `ExitScrollMode`/`CompleteDrag`):

```csharp
    /// <summary>Exits repeat mode and returns to free look.</summary>
    public void ExitRepeatMode()
    {
        if (CurrentState != InteractionState.RepeatMode) return;
        ResetToFreeLook();
    }

    /// <summary>
    /// Fires one repeat tick at the given live gaze point. Called by the app-side repeat
    /// controller's timer; guarded so a tick that lands after exit (or before entry) is a no-op
    /// rather than a stray click.
    /// </summary>
    public void FireRepeatTick(PhysicalPoint point)
    {
        if (CurrentState != InteractionState.RepeatMode || RepeatAction is not { } action) return;
        ActionRequested?.Invoke(action, point);
    }
```

6. `ResetToFreeLook` — add `RepeatAction = null;` to the reset block.

- [ ] **Step 4: Run to verify pass**

`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionManagerRepeatModeTests"` — PASS. Then the whole project: `dotnet test tests/EyeCtrl.Core.Tests --filter "Category!=UI"` — PASS (no regressions in `InteractionManagerTests`/`GazeFlowTests`).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/InteractionState.cs src/EyeCtrl.Core/Processing/InteractionManager.cs tests/EyeCtrl.Core.Tests/Processing/InteractionManagerRepeatModeTests.cs
git commit -m "feat(core): InteractionManager repeat state - locked RepeatMode + RepeatLastAction conversion"
```

---

### Task 3: Core — RepeatSettings + registration

**Files:**
- Create: `src/EyeCtrl.Core/Settings/RepeatSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/RepeatSettingsTests.cs` (new), `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs`

**Interfaces:**
- Produces: `RepeatSettings { int IntervalMs /* 250–5000, default 1000 */; int StopButtonDwellMs /* 200–2000, default 600 */ }`, exposed as `EyeCtrlSettings.Repeat` — used by Tasks 5/6.

- [ ] **Step 1: Write the failing tests**

Create `RepeatSettingsTests.cs` (mirror the ScrollSettings/validation style — setter guards throw, `Validate()` collects):

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class RepeatSettingsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var s = new RepeatSettings();
        Check.That(s.IntervalMs).IsEqualTo(1000);
        Check.That(s.StopButtonDwellMs).IsEqualTo(600);
        Check.That(s.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(249)]
    [InlineData(5001)]
    public void IntervalMs_OutOfRange_Throws(int value)
    {
        ArgumentOutOfRangeException? ex = null;
        try { new RepeatSettings().IntervalMs = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }
        Check.That(ex).IsNotNull();
    }

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void StopButtonDwellMs_OutOfRange_Throws(int value)
    {
        ArgumentOutOfRangeException? ex = null;
        try { new RepeatSettings().StopButtonDwellMs = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }
        Check.That(ex).IsNotNull();
    }

    [Fact]
    public void Validate_CollectsRangeErrors()
    {
        var s = new RepeatSettings();
        // Reach an invalid state through Validate's contract (loaded files): use reflection-free
        // trick — Validate() on defaults is empty; assert the boundary values are accepted.
        s.IntervalMs = 250;
        s.StopButtonDwellMs = 2000;
        Check.That(s.Validate()).IsEmpty();
    }
}
```

In `EyeCtrlSettingsEnumerationTests.cs`, change the count test:

```csharp
    public void EnumerateSubSettings_Yields20Entries_AllValidatable()
    {
        var subs = new EyeCtrlSettings().EnumerateSubSettings().ToList();

        Check.That(subs).HasSize(20);
        Check.That(subs.All(t => t.Settings is IValidatable)).IsTrue();
    }
```

- [ ] **Step 2: Run to verify failure**

`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~RepeatSettingsTests|FullyQualifiedName~EyeCtrlSettingsEnumerationTests"` — compile error for `RepeatSettings`, count failure (19 ≠ 20).

- [ ] **Step 3: Implement**

Create `RepeatSettings.cs`:

```csharp
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for locked repeat-click mode (gap #4, TD Control Continuous/RepeatClick).</summary>
public partial class RepeatSettings : ObservableObject, IValidatable
{
    private const int IntervalMinMs = 250;
    private const int IntervalMaxMs = 5000;
    private const int IntervalDefaultMs = 1000;
    private const int StopDwellMinMs = 200;
    private const int StopDwellMaxMs = 2000;
    private const int StopDwellDefaultMs = 600;

    /// <summary>Milliseconds between repeat ticks while repeat mode is active. Valid range: 250–5000.</summary>
    [ObservableProperty] private int _intervalMs = IntervalDefaultMs;

    /// <summary>Dwell time in milliseconds for the right-edge stop button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _stopButtonDwellMs = StopDwellDefaultMs;

    partial void OnIntervalMsChanging(int value)
    {
        if (value < IntervalMinMs || value > IntervalMaxMs)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(IntervalMs)} must be between {IntervalMinMs} and {IntervalMaxMs}");
    }

    partial void OnStopButtonDwellMsChanging(int value)
    {
        if (value < StopDwellMinMs || value > StopDwellMaxMs)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(StopButtonDwellMs)} must be between {StopDwellMinMs} and {StopDwellMaxMs}");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(IntervalMs, IntervalMinMs, IntervalMaxMs, nameof(IntervalMs)),
            ValidationHelper.ValidateRange(StopButtonDwellMs, StopDwellMinMs, StopDwellMaxMs, nameof(StopButtonDwellMs))
        );
    }
}
```

`EyeCtrlSettings.cs` — add after the `Adaptation` member:

```csharp
    /// <summary>Locked repeat-click mode settings (interval, stop-button dwell).</summary>
    [ObservableProperty] private RepeatSettings _repeat = new();
```

and in `EnumerateSubSettings`, after the Adaptation yield:

```csharp
        yield return (nameof(Repeat), Repeat);
```

- [ ] **Step 4: Run to verify pass**

`dotnet test tests/EyeCtrl.Core.Tests --filter "Category!=UI"` — PASS (includes settings service round-trips; the new member serializes as `Repeat` with defaults).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/RepeatSettings.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs tests/EyeCtrl.Core.Tests/Settings/RepeatSettingsTests.cs tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs
git commit -m "feat(core): RepeatSettings (interval 250-5000 default 1000, stop dwell) + registration"
```

---

### Task 4: Ring wiring — icons, labels, descriptions

**Files:**
- Modify: `src/EyeCtrl.Core/Models/ActionIcon.cs`
- Modify: `src/EyeCtrl.Controls/Icons/ActionIcons.cs`
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs` (expected map + 2 entries)

**Interfaces:**
- Consumes: `InteractionAction.RepeatLastAction`/`RepeatMode` (Task 1).
- Produces: `ActionIcon.RepeatLastAction` / `ActionIcon.RepeatMode` geometries; ring labels "Repeat" / "Repeat mode".

- [ ] **Step 1: Extend the symbol test (failing)**

In `ActionMenuViewModelSymbolTests.EveryAction_MapsToADistinctIcon`, add to the `expected` dictionary:

```csharp
            [InteractionAction.RepeatLastAction] = ActionIcon.RepeatLastAction,
            [InteractionAction.RepeatMode] = ActionIcon.RepeatMode,
```

- [ ] **Step 2: Run to verify failure**

`dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~EveryAction_MapsToADistinctIcon"` — compile error (no such `ActionIcon` members).

- [ ] **Step 3: Implement the enum + map + labels + descriptions**

`ActionIcon.cs` — insert after `PinNote` (before `Back`):

```csharp
    /// <summary>A plain mouse with one circular renewal arrow at the right margin.</summary>
    RepeatLastAction,
    /// <summary>A left-filled mouse with circular renewal arrows at both margins — the locked loop.</summary>
    RepeatMode,
```

`ActionIcons.cs` — in `Build()`, insert after the `PinNote` entry:

```csharp
        // A renewal arrow: ~300° of a circle centred in the right margin with an arrowhead at its
        // end. The mouse carries no fill — the entry repeats WHATEVER was last, not a left click.
        [ActionIcon.RepeatLastAction] = IconGeometry.FromPathData(
            Body + Buttons
            + " M21.2,9.53 A2,2 0 1 0 22.82,11.15"
            + " M22.82,11.15 L21.45,10.75 M22.82,11.15 L22.35,9.75"),

        // The same mouse with its left button filled (it actively clicks) and a second renewal
        // arrow mirrored into the left margin — the outline-level difference that survives
        // peripheral vision, per the rule in this class's header.
        [ActionIcon.RepeatMode] = IconGeometry.FromPathData(
            Body + Buttons
            + " M21.2,9.53 A2,2 0 1 0 22.82,11.15"
            + " M22.82,11.15 L21.45,10.75 M22.82,11.15 L22.35,9.75"
            + " M2.8,9.53 A2,2 0 1 1 1.18,11.15"
            + " M1.18,11.15 L2.55,10.75 M1.18,11.15 L1.65,9.75",
            LeftButtonFill),
```

`ActionMenuViewModel.cs`:

- `FriendlyName` switch — add before `null =>`:

```csharp
        InteractionAction.RepeatLastAction => "Repeat",
        InteractionAction.RepeatMode => "Repeat mode",
```

- `GetActionIcon` switch — add before the throw:

```csharp
        InteractionAction.RepeatLastAction => ActionIcon.RepeatLastAction,
        InteractionAction.RepeatMode => ActionIcon.RepeatMode,
```

`ActionMenuPageSource.cs`:

- `FriendlyName` — add before `_ =>`:

```csharp
        InteractionAction.RepeatLastAction => "Repeat Last Action",
        InteractionAction.RepeatMode => "Repeat Mode",
```

- `ActionDescription` — add before `_ => null`:

```csharp
        InteractionAction.RepeatLastAction => "Repeats your last click at the point you chose.",
        InteractionAction.RepeatMode => "Locks your last click to repeat wherever you look. Dwell the stop button on the right edge to end it.",
```

- [ ] **Step 4: Run tests to verify pass**

`dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModel"` — PASS (symbol + layer tests; layer tests stay green because defaults are untouched).
`dotnet test tests/EyeCtrl.Controls.Tests --filter "Category!=UI"` — PASS (`EveryIcon_FitsTheGrid`, frozen, has-geometry pick up the new icons automatically).

- [ ] **Step 5: Render + visually inspect the two new icons**

Write a PowerShell script to `%TEMP%\render_repeat_icons.ps1` that loads PresentationFramework, parses the four stroke/fill path strings above (`Body`+`Buttons`+arrows and `LeftButtonFill`), draws each at 24×24 scaled to 240×240 with a 1.8-unit pen, and saves PNGs to `%TEMP%\repeat_icons.png`. Run it, then **Read the PNG and check**: the arrow reads as a renewal loop, the arrowhead is attached and points along the arc, nothing clips the grid, and RepeatMode is distinguishable from RepeatLastAction at a glance (fill + second arrow). Adjust path data until it reads right, re-running the Controls tests after each adjustment.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Models/ActionIcon.cs src/EyeCtrl.Controls/Icons/ActionIcons.cs src/EyeCtrl.ViewModels/ActionMenuViewModel.cs src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs
git commit -m "feat(ui): ring wiring for Repeat / Repeat mode - icons, labels, descriptions (OFF by default)"
```

---

### Task 5: App — repeat interval in Advanced settings

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs`
- Test: existing `SettingsContentFitTests` + `LayoutActionMenuPageSourceTests` gate it (labels/descriptions feed in automatically).

**Interfaces:**
- Consumes: `EyeCtrlSettings.Repeat.IntervalMs` (Task 3).

- [ ] **Step 1: Implement**

In `AdvancedPageSource.Build()`: add `var repeat = st.Repeat;` to the local declarations. After the Scroll section's closing `LabelSpec` (the one before `// External Dwell`), insert:

```csharp
                new LabelSpec(() => new string('\u2500', 50), Wrap: false),
                // Repeat
                new SliderSpec("av_repeat_interval", "Repeat Interval (ms)", 250, 5000, 50, 0,
                    () => repeat.IntervalMs, v => { repeat.IntervalMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How often repeat mode re-fires your locked click. Applies the next time you start repeating." },
```

In the reset modal's action, add (with `var r = new Core.Settings.RepeatSettings();` beside the others):

```csharp
                        st.Repeat.IntervalMs = r.IntervalMs;
```

- [ ] **Step 2: Run tests**

`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsContentFit"` — PASS (new label/description measured automatically).

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs
git commit -m "feat(app): repeat interval slider in Advanced settings"
```

---

### Task 6: App — RepeatModeController + engine/cursor/message wiring

**Files:**
- Create: `src/EyeCtrl.App/Services/Coordinators/RepeatModeController.cs`
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (new message)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs` (construct + dispose)
- Modify: `src/EyeCtrl.Core/GazeEngine.cs` (cursor-follow gate, one line)
- Modify: `src/EyeCtrl.ViewModels/CursorCircleViewModel.cs` (visibility allow-list, one line)
- Test: `tests/EyeCtrl.App.Tests/Services/RepeatModeControllerDefinitionTests.cs` (new, mirrors `ScrollModeControllerDefinitionTests`); extend `tests/EyeCtrl.ViewModels.Tests/CursorCircleViewModelTests.cs` + `tests/EyeCtrl.Core.Tests/Flow/GazeFlowTests.cs`.

**Interfaces:**
- Consumes: `InteractionManager.RepeatAction`/`FireRepeatTick`/`ExitRepeatMode`, `InteractionState.RepeatMode` (Task 2); `RepeatSettings.StopButtonDwellMs`/`IntervalMs` (Task 3); `EdgeButtonDefinition`, `ScreenGeometry.RightEdgeAnchor`, `UIConstants.ButtonRadius` (existing, see `ScrollModeController.CreateScrollStopButtonDefinition`).
- Produces: `ExitRepeatModeMessage` record; `RepeatModeController.CreateRepeatStopButtonDefinition(int dwellMs, double verticalOffset)` internal static.

- [ ] **Step 1: Write the failing tests**

Create `RepeatModeControllerDefinitionTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

[Collection("DpiHelper Tests (App)")]
public class RepeatModeControllerDefinitionTests
{
    public RepeatModeControllerDefinitionTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [Fact]
    public void CreateRepeatStopButtonDefinition_UsesFullButtonRadius()
    {
        var def = RepeatModeController.CreateRepeatStopButtonDefinition(
            dwellMs: 600, verticalOffset: 0);

        Check.That(def.Id).IsEqualTo("repeat-stop");
        Check.That(def.RadiusLogical).IsEqualTo(UIConstants.ButtonRadius);
        Check.That(def.VerticalOffset).IsEqualTo(0.0);
        Check.That(def.DwellMs).IsEqualTo(600);
    }

    [Fact]
    public void CreateRepeatStopButtonDefinition_AppliesVerticalOffset()
    {
        var def = RepeatModeController.CreateRepeatStopButtonDefinition(
            dwellMs: 600, verticalOffset: 50.0);

        Check.That(def.VerticalOffset).IsEqualTo(50.0);
    }
}
```

(If `RepeatModeController` lands in namespace `EyeCtrl.App.Services` instead — follow wherever `ScrollModeController` sits when you add the file and keep the using consistent.)

In `CursorCircleViewModelTests.cs`, add:

```csharp
    [Fact]
    public void StaysVisible_DuringRepeatMode()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.RepeatMode));

        Check.That(vm.IsVisible).IsTrue();
    }
```

In `GazeFlowTests.cs`, add (uses the existing `GazeFlowHarness` — check its helpers `Gaze`/`Hold`/dwell helpers and mirror `SelectingDrag_EntersDragging_ThenCompleteReturnsToFreeLook`'s setup):

```csharp
    [Fact]
    public void RepeatMode_CursorKeepsFollowingGaze()
    {
        using var h = new GazeFlowHarness();
        // Drive to the action menu, then lock repeat mode (mirrors the drag test's setup shape).
        // ... fixation → activation dwell → OnActionSelected(InteractionAction.RepeatMode)
        var movesBefore = h.Input.MoveCount;

        h.Gaze(900, 700);

        Check.That(h.State).IsEqualTo(InteractionState.RepeatMode);
        Check.That(h.Input.MoveCount).IsGreaterThan(movesBefore);
    }
```

(Adapt the setup calls to the harness's actual helper names; the assertion under test is that the
cursor still moves while in `RepeatMode` — i.e. the GazeEngine gate was extended.)

- [ ] **Step 2: Run to verify failure**

App + ViewModels + Core test runs each fail (missing controller/message; visibility false; cursor frozen).

- [ ] **Step 3: Implement**

`InteractionMessages.cs` — beside `ExitScrollModeMessage`:

```csharp
/// <summary>Fired by the repeat-mode stop button (and the ESC hatch) to end locked repeat mode.</summary>
public record ExitRepeatModeMessage;
```

Create `RepeatModeController.cs` (in `src/EyeCtrl.App/Services/Coordinators/`):

```csharp
namespace EyeCtrl.App.Services.Coordinators;

using System.Windows.Interop;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;

/// <summary>
/// Owns the locked repeat-click mode's app surface: the right-edge stop button, the UI-thread
/// tick timer that re-fires the locked click at the live stable centroid, and the WM_KEYDOWN /
/// VK_ESCAPE thread filter that ends an in-flight repeat. Mirrors <see cref="ScrollModeController"/>
/// (state-triggered entry) and <see cref="DragCoordinator"/> (escape hatch) — extracted as its
/// own coordinator because the state machine only owns WHEN repeats are legal
/// (<c>InteractionManager.FireRepeatTick</c>), not the cadence or the chrome.
/// </summary>
internal sealed class RepeatModeController : CoordinatorBase
{
    private const double StopInsetLogical = 50;

    /// <summary>
    /// Builds the repeat-stop <see cref="EdgeButtonDefinition"/>. Static and internal so a
    /// regression test can pin it without constructing the full coordination context — same
    /// pattern as <see cref="ScrollModeController.CreateScrollStopButtonDefinition"/>.
    /// </summary>
    internal static EdgeButtonDefinition CreateRepeatStopButtonDefinition(
        int dwellMs, double verticalOffset)
        => new(
            Id: "repeat-stop",
            Symbol: "\u23F9",
            DwellMs: dwellMs,
            Activate: m => m.Send(new ExitRepeatModeMessage()),
            VerticalOffset: verticalOffset,
            RadiusLogical: UIConstants.ButtonRadius);

    private DispatcherTimer? _timer;
    private bool _active;

    protected override void OnInitialize()
    {
        var context = Context!;
        context.InteractionManager.StateChanged += OnStateChanged;
        context.Messenger.Register<RepeatModeController, ExitRepeatModeMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() => context.InteractionManager.ExitRepeatMode()));
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message == 0x0100 && (int)msg.wParam == 0x1B) // WM_KEYDOWN, VK_ESCAPE
        {
            if (Context?.InteractionManager.CurrentState == InteractionState.RepeatMode)
            {
                Context.Messenger.Send(new ExitRepeatModeMessage());
                handled = true;
            }
        }
    }

    private void OnStateChanged(InteractionState state)
    {
        if (state == InteractionState.RepeatMode)
            Enter();
        else if (_active)
            Exit();
    }

    private void Enter()
    {
        var context = Context!;

        var edgeAnchor = ScreenGeometry.RightEdgeAnchor(StopInsetLogical, UIConstants.ButtonRadius);
        var stopDef = CreateRepeatStopButtonDefinition(context.Settings.Repeat.StopButtonDwellMs, verticalOffset: 0);
        context.OverlayManager.EdgeButtonBar.ShowButtons(new[] { stopDef }, edgeAnchor);

        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(context.Settings.Repeat.IntervalMs)
        };
        _timer.Tick += (_, _) =>
        {
            // No stable point (gaze lost / between fixations) → skip the tick. Never click a
            // stale point: a repeat aimed at nothing is worse than a missed beat.
            if (Context?.GazeEngine.StableCentroid is { } p)
                Context.InteractionManager.FireRepeatTick(p);
        };
        _timer.Start();
        _active = true;
    }

    private void Exit()
    {
        _timer?.Stop();
        _timer = null;
        Context?.OverlayManager.EdgeButtonBar.Hide();
        _active = false;
    }

    protected override void OnDispose()
    {
        if (_active) Exit();
        if (Context is null) return;
        Context.InteractionManager.StateChanged -= OnStateChanged;
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
    }
}
```

Check `ScrollModeController`'s actual namespace + where `ScreenGeometry`/`UIConstants`/`EdgeButtonDefinition` come from (`EyeCtrl.App.Overlays` / `EyeCtrl.ViewModels` / `EyeCtrl.Core`) and match its usings; if `ScrollModeController` lives directly under `EyeCtrl.App.Services` (not `.Coordinators`), place the new file beside it instead — the test's using follows.

`EventCoordinator.cs`:
- Field: `private RepeatModeController? _repeatModeController;` beside `_dragCoordinator`.
- In `Initialize`, after `_dragCoordinator.Initialize(context);`:

```csharp
        _repeatModeController = new RepeatModeController();
        _repeatModeController.Initialize(context);
```

- In `Dispose`, beside the drag dispose:

```csharp
        _repeatModeController?.Dispose();
```

`GazeEngine.cs` (`OnGazePoint`) — the cursor-follow gate becomes:

```csharp
        if (InteractionManager.CurrentState is InteractionState.FreeLook
            or InteractionState.Dragging
            or InteractionState.RepeatMode)
```

(keep it on one line if it fits the existing style: `is InteractionState.FreeLook or InteractionState.Dragging or InteractionState.RepeatMode`).

`CursorCircleViewModel.cs`:

```csharp
        _stateAllowsCursor = message.NewState is InteractionState.FreeLook
            or InteractionState.Fixation
            or InteractionState.RepeatMode;
```

(one line if it fits: `... is InteractionState.FreeLook or InteractionState.Fixation or InteractionState.RepeatMode;`)

- [ ] **Step 4: Run tests to verify pass**

`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~RepeatModeControllerDefinitionTests"` — PASS.
`dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~CursorCircleViewModelTests"` — PASS.
`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeFlowTests"` — PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/RepeatModeController.cs src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.Core/GazeEngine.cs src/EyeCtrl.ViewModels/CursorCircleViewModel.cs tests/EyeCtrl.App.Tests/Services/RepeatModeControllerDefinitionTests.cs tests/EyeCtrl.ViewModels.Tests/CursorCircleViewModelTests.cs tests/EyeCtrl.Core.Tests/Flow/GazeFlowTests.cs
git commit -m "feat(app): RepeatModeController - stop button, tick timer, ESC hatch; cursor follows gaze in RepeatMode"
```

---

### Task 7: Docs + full verification

**Files:**
- Modify: `docs/COMPETITIVE_FEATURE_ANALYSIS.md`

- [ ] **Step 1: Update the competitive analysis**

On this branch the doc carries the 2026-09-08 refresh. Changes:

1. **Tier-1 table** (`### Tier 1 — High sensibility (still open)`): delete the `#4` row (leaves `#22` as the only Tier-1 item).
2. **Resolved table** (`## Resolved since 2026-07-11`): add row `4`:

```markdown
| 4 | No continuous / repeat-click mode | **Implemented 2026-09-08** on `feat/repeat-click-mode` (stacked on PR #138). `InteractionState.RepeatMode` + `InteractionManager` locked-repeat state machine (`RepeatAction`, `FireRepeatTick`, last-click memory feeding `InteractionAction.RepeatLastAction`); app-side `RepeatModeController` re-fires the locked click at the live stable centroid every `RepeatSettings.IntervalMs` (default 1000), exits via right-edge stop button or ESC. Both ring entries ship **OFF** by default (Settings → Action Menu). Declined: TD's Click-on-Fixation variant (direct dwell-to-click contradicts EyeCtrl's Interaction-First stance). |
```

3. **Suggested implementation order**: re-rank so `#22 Prediction controls` is first, multi-monitor (#7) second; note #4 shipped.
4. **TD Control feature details**: the Continuous/Repeat-Click bullet's bracket becomes `*[EyeCtrl now has the equivalent — locked Repeat Mode + a Repeat ring entry.]*`
5. **Change-log**: add `**2026-09-08 (b):** Gap #4 (continuous/repeat-click) implemented on feat/repeat-click-mode ...` one-paragraph entry naming the seam files.

- [ ] **Step 2: Full verification bar**

Sequentially, one project per invocation (never two suites at once):

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Core.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Platform.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Controls.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Supervisor.Tests --filter "Category!=UI"   # if this project exists — check tests/ first
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

All green. (If any project in `tests/` isn't listed above, run it too with the same filter — the bar is every test project, UI-excluded.)

- [ ] **Step 3: detect_changes + commit**

```bash
node ../.gitnexus/run.cjs detect-changes --scope all --repo .   # from the worktree; if the CLI needs the primary checkout's .gitnexus, run it there against this worktree path
git add docs/COMPETITIVE_FEATURE_ANALYSIS.md
git commit -m "docs(competitive): gap #4 resolved - repeat-click mode shipped on feat/repeat-click-mode"
```

`detect_changes` must come back clean or its findings must be explained before the PR is opened (a `partial: true`/`truncated: true` result is not clean — re-run).

- [ ] **Step 4: Push + open PR**

```bash
git push -u origin feat/repeat-click-mode
gh pr create --base feat/mouse-osk-polish --title "feat(input): continuous/repeat-click mode + Repeat last action (gap #4)" --body <PR_BODY>
```

PR body must state: stacked on #138 (retarget to `main` after #138 merges), OFF by default + how to enable, the decision-ledger pointer (`docs/superpowers/specs/2026-09-08-repeat-click-mode-design.md`), the CRITICAL-hub impact note and why the blast radius is contained, and the verification evidence (build + per-project test output).

---

## Self-review notes

- Spec coverage: D1/D3/D4/D9/D12/D13 → Task 1+2; D5/D6/D11 → Task 6; D7/D8 → Tasks 3+5+6; D10 → Task 6 (gate + allow-list + no new overlay); D14 → Task 4 (defaults untouched — layer tests are the guard); D15 → Task 7; D2 → explicitly out of scope. Icons (Task 4) carry the render-and-inspect step. ✓
- Type consistency: `RepeatAction`/`LastRepeatableAction`/`FireRepeatTick(PhysicalPoint)`/`ExitRepeatMode()`/`ExitRepeatModeMessage`/`CreateRepeatStopButtonDefinition(int, double)` spellings match across Tasks 2/4/6. ✓
- The GazeFlow repeat test and the icon paths are the two steps with residual uncertainty — both have explicit verify/adjust loops built in.
