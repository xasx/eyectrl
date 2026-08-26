# Action Menu Geometry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Grow the action-menu targets from 100 to 140 DIP, push the button ring from 20 to 60 DIP clear of the cursor preview circle without resizing any of the six other surfaces that share the same radius constant, and give every dwell button a rim that reads against arbitrary desktop content.

**Architecture:** `LayoutConstants.ActionMenuButtonRadius` is today aliased by `UIConstants.ButtonRadius` and read from eight call sites across six unrelated surfaces, so raising it in place would silently resize all of them. This plan first splits that constant into a shared one (`SharedButtonRadius`, staying at 50) and an action-menu-only one (raised to 70), then moves the action menu's three call sites onto the new constant and raises the outer gap. The ring formula `CircleSize/2 + ButtonRadius + OuterGap` is already the single source of truth, so Tasks 1-4 change no rendering code at all and simply produce larger numbers. Task 5 is separate: it gives the button rim an outer halo, which is a rendering change and the only non-additive edit in the series.

**Tech Stack:** C# / .NET 10 / WPF, xUnit v3 with NFluent assertions (`Check.That(...)`, never `Assert.*`), CommunityToolkit.Mvvm.

---

## Scope note: this is plan 1 of 3

The design canvas covers three independent subsystems. Each ships working, testable software on its own, and each later one depends on the one before:

1. **Geometry and spacing** — this plan. Bigger, better-spaced targets, still using today's Unicode glyphs.
2. **Drawn icon set** — replaces the `☝ ☰ ✌ ✊ ▲ ▼ ⇅ 📌` glyphs with stroke geometry in `EyeCtrl.Controls`, requiring a new icon-capable path through `DwellArcButton.Create`.
3. **Progressive disclosure and the Help label toggle** — splits `ActionMenuSettings.VisibleActions` into a primary four plus a More layer, and adds the Help target to `EdgeButtonBarWindow`.

Do not begin 2 or 3 from this document.

## Risk warning (CLAUDE.md requires this be surfaced before editing)

`impact({target: "ActionMenuViewModel", direction: "upstream"})` returns **HIGH** risk: 20 direct dependents across 3 modules. `impact({target: "DwellArcButton", direction: "upstream"})` also returns **HIGH**: 15 impacted symbols, 2 affected execution flows (`OverlayManager.Initialize`, `ActionMenuWindow.Show`).

The mitigation for `ActionMenuViewModel` is that only two lines change (53 and 55) and both swap one named constant for another.

`DwellArcButton` is touched by **Task 5 only**, and that task is the one change in this whole series that is not additive: it alters how all three callers render. Tasks 1-4 do not go near it. If Task 5's blast radius is unwelcome, it can be lifted into its own plan without disturbing the other four — nothing in Tasks 1-4 depends on it.

## The trap this plan exists to avoid

`src/EyeCtrl.ViewModels/UIConstants.cs:14` reads:

```csharp
public const double ButtonRadius = LayoutConstants.ActionMenuButtonRadius;
```

and `UIConstants.ButtonRadius` is read from eight call sites across six surfaces that are **not** the action menu (the backdrop hide button owns two of them, window and ViewModel):

| File | Line | Surface |
|---|---|---|
| `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs` | 224 | backdrop hide button |
| `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs` | 227, 231 | ManualSelection side buttons |
| `src/EyeCtrl.App/Services/ScrollModeController.cs` | 36, 72 | scroll-stop button |
| `src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs` | 37 | activation button |
| `src/EyeCtrl.ViewModels/GlassBackdropViewModel.cs` | 37 | backdrop hide target |
| `src/EyeCtrl.ViewModels/QuickActionsGridViewModel.cs` | 113, 122 | quick-action grid buttons |
| `src/EyeCtrl.ViewModels/TaskSwitcherViewModel.cs` | 121 | task-switcher buttons |

Raising `ActionMenuButtonRadius` from 50 to 70 without Task 1 grows every one of those from 100 to 140 DIP, including their dwell hit-targets. Task 1 must land first, and Task 2's guard test exists to keep it landed.

## File structure

**Modify:**
- `src/EyeCtrl.Core/LayoutConstants.cs` — add `SharedButtonRadius`; raise `ActionMenuButtonRadius` and `ActionMenuButtonOuterGap`
- `src/EyeCtrl.ViewModels/UIConstants.cs` — repoint `ButtonRadius`; add `ActionMenuButtonRadius`
- `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:53,55` — consume the action-menu radius
- `src/EyeCtrl.App/Overlays/ActionMenuGeometry.cs:17` — consume the action-menu radius
- `src/EyeCtrl.Controls/DwellArcButton.cs:86-87` — 2 DIP rim plus an outer halo ring (Task 5)

**Modify (tests carrying the old numbers):**
- `tests/EyeCtrl.ViewModels.Tests/UIConstantsTests.cs`
- `tests/EyeCtrl.ViewModels.Tests/MagicNumberReplacementTests.cs`
- `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs:94-116, 233`
- `tests/EyeCtrl.App.Tests/Controls/DwellArcButtonTests.cs` — `AddTo_AddsAllVisualsToCanvas` pins a child count that Task 5 changes

**Create:**
- `tests/EyeCtrl.Controls.Tests/DwellArcButtonRimTests.cs`
- `tests/EyeCtrl.ViewModels.Tests/ActionMenuLayoutHelperTests.cs` — `ActionMenuLayoutHelper` has **no tests today**, and this plan changes the clamp margin it computes

## Reference numbers

Derived from `CircleSize` default 250 (`src/EyeCtrl.Core/Settings/CursorSettings.cs:33`):

| Quantity | Formula | Before | After |
|---|---|---|---|
| Action-menu button radius | constant | 50 | 70 |
| Outer gap | constant | 20 | 60 |
| Ring radius | `CircleSize/2 + ButtonRadius + OuterGap` | 195 | 255 |
| Ring radius at `CircleSize=100` | same | 120 | 180 |
| Clamp margin | `ringRadius + ButtonRadius` | 245 | 325 |
| Window size | `(ring + ButtonRadius)*2 + 20` | 510 | 670 |
| Shared button radius | constant | 50 | **50 (unchanged)** |

## Verification commands

The suite is xUnit v3 + NFluent. **`dotnet test` output on this machine is German**: a per-test failure line reads `Fehler <FullyQualifiedName>`, not `Failed`, and long names wrap across lines. Grepping for `Failed` will report a green run over a red one — read the summary line (`Fehler: N`) instead.

Full pre-commit gate for this plan:

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

`EyeCtrl.Controls.Tests` is in the gate for Task 5; Tasks 1-4 do not need it but running it costs nothing.

The App.Tests filter is required: roughly 13 `AppLaunchTests` fail in any headless session with "Toolbar_Window did not appear within 15s". Those are environmental and fail identically on clean `main`.

---

### Task 1: Split the shared button radius from the action-menu one

**Files:**
- Modify: `src/EyeCtrl.Core/LayoutConstants.cs:8-13`
- Modify: `src/EyeCtrl.ViewModels/UIConstants.cs:10-15`
- Test: `tests/EyeCtrl.ViewModels.Tests/UIConstantsTests.cs`

This task changes no observable behaviour — both constants still read 50. It exists so Task 3 can raise one without moving the other.

- [ ] **Step 1: Write the failing test**

Add to `tests/EyeCtrl.ViewModels.Tests/UIConstantsTests.cs`, inside the existing `UIConstantsTests` class:

```csharp
    /// <summary>
    /// The action menu's button radius and the radius every OTHER circular overlay uses are
    /// separate constants, and must stay separate.
    ///
    /// They were one constant until 2026-08-26: <c>UIConstants.ButtonRadius</c> aliased
    /// <c>LayoutConstants.ActionMenuButtonRadius</c>, and eight surfaces that are not the action
    /// menu read it — the activation button, the backdrop hide button, the ManualSelection side
    /// buttons, the scroll-stop button, the quick-action grid and the task switcher. Growing the
    /// action-menu target to 140 DIP through that alias would have grown all of them, hit-targets
    /// included, for a user whose only input channel is an eye tracker.
    ///
    /// This test fails if someone re-aliases them, which is the specific regression that would
    /// otherwise be invisible: every existing test still passes when the shared radius silently
    /// grows, because none of those eight surfaces pins its own size.
    /// </summary>
    [Fact]
    public void ActionMenuButtonRadius_IsSeparateFrom_TheSharedButtonRadius()
    {
        Check.That(UIConstants.ButtonRadius).IsEqualTo(50.0);
        Check.That(UIConstants.ActionMenuButtonRadius).IsEqualTo(50.0);
        Check.That(UIConstants.ButtonRadius).IsEqualTo(UIConstants.ActionMenuButtonRadius);
    }
```

Note the third assertion asserts they are currently *equal* — Task 3 flips it to `IsNotEqualTo`. That is deliberate: this task must be a pure refactor, and a test that already expected 70 would let Task 1 pass while half-done.

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuButtonRadius_IsSeparateFrom"
```

Expected: FAIL to **compile**, with `CS0117: 'UIConstants' does not contain a definition for 'ActionMenuButtonRadius'`.

- [ ] **Step 3: Add the shared constant to LayoutConstants**

In `src/EyeCtrl.Core/LayoutConstants.cs`, replace the existing `ActionMenuButtonRadius` declaration and its doc comment with both constants:

```csharp
    /// <summary>
    /// Radius of the circular dwell buttons shared by every overlay that is NOT the action menu,
    /// in logical pixels: the activation button, the backdrop hide button, the ManualSelection
    /// side buttons, the scroll-stop button, the quick-action grid and the task switcher.
    ///
    /// Split from <see cref="ActionMenuButtonRadius"/> on 2026-08-26. They held the same value
    /// through one constant until the action-menu targets grew to 140 DIP; sharing meant that
    /// change would have resized six unrelated surfaces and their dwell hit-targets. Keep them
    /// separate even while the numbers agree — see
    /// <c>UIConstantsTests.ActionMenuButtonRadius_IsSeparateFrom_TheSharedButtonRadius</c>.
    /// </summary>
    public const double SharedButtonRadius = 50.0;

    /// <summary>
    /// Radius of action menu buttons in logical pixels.
    /// Used for coordinate clamping calculations to ensure buttons stay on screen.
    /// Deliberately NOT <see cref="SharedButtonRadius"/> — see that constant for why.
    /// </summary>
    public const double ActionMenuButtonRadius = 50.0;
```

- [ ] **Step 4: Repoint UIConstants and expose the action-menu radius**

In `src/EyeCtrl.ViewModels/UIConstants.cs`, replace the `ButtonRadius` declaration and its doc comment with:

```csharp
    /// <summary>
    /// Default radius for circular buttons in logical pixels: activation button, backdrop hide
    /// button, ManualSelection side buttons, scroll-stop button, quick-action grid, task switcher.
    ///
    /// NOT the action menu — that reads <see cref="ActionMenuButtonRadius"/>. This property named
    /// the action menu among its callers until 2026-08-26, when the two were split.
    /// </summary>
    public const double ButtonRadius = LayoutConstants.SharedButtonRadius;

    /// <summary>
    /// Radius of an action-menu ring button in logical pixels. Feeds both the rendered
    /// <c>DwellArcButton</c> and the dwell hit-target, and appears twice in the ring formula:
    /// once in the ring radius and once again in the on-screen clamp margin.
    /// </summary>
    public const double ActionMenuButtonRadius = LayoutConstants.ActionMenuButtonRadius;
```

- [ ] **Step 5: Run test to verify it passes**

Run:

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~UIConstantsTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 6: Run the full gate — this refactor must move nothing**

Run:

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS. Every number in the repo is still what it was, so a failure here means the split changed a value it should not have.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Core/LayoutConstants.cs src/EyeCtrl.ViewModels/UIConstants.cs tests/EyeCtrl.ViewModels.Tests/UIConstantsTests.cs
git commit -m "refactor(layout): separate the action-menu button radius from the shared one

Eight overlays that are not the action menu read UIConstants.ButtonRadius,
which aliased LayoutConstants.ActionMenuButtonRadius. Raising the action-menu
target would have resized all of them. Both constants still read 50; only the
alias is gone.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Move the action menu onto its own radius constant

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:53,55`
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuGeometry.cs:17`
- Test: `tests/EyeCtrl.ViewModels.Tests/ActionMenuLayoutHelperTests.cs` (create)

Still no behaviour change — the constants agree at 50. This task redirects the three action-menu call sites and adds the missing coverage for `ActionMenuLayoutHelper`, so Task 3's number change has a test that actually watches it.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.ViewModels.Tests/ActionMenuLayoutHelperTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Helpers;

/// <summary>
/// Covers <see cref="ActionMenuLayoutHelper"/>, which had no tests until 2026-08-26 even though it
/// decides how far from a screen edge the menu is torn away from the point the user was looking at.
///
/// The clamp margin is <c>ringRadius + ActionMenuButtonRadius</c>, so the button radius enters the
/// figure twice — once inside the ring radius and once again as the outermost half-button. Raising
/// the radius by 20 therefore costs 40 DIP of margin, not 20, and that compounding is the thing
/// most likely to be got wrong by inspection.
/// </summary>
[Collection("DpiHelper Tests")]
public class ActionMenuLayoutHelperTests
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1080;

    [Fact]
    public void ClampMargin_CountsTheButtonRadiusTwice()
    {
        DpiHelper.Initialize(1.0, 1.0);

        // A gaze point hard against the top-left corner is clamped to exactly the margin.
        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(0, 0),
            ViewPosition.AtGaze,
            circleSize: 250,
            ScreenWidth,
            ScreenHeight);

        var ring = 250 / 2 + LayoutConstants.ActionMenuButtonRadius + LayoutConstants.ActionMenuButtonOuterGap;
        var expected = ring + LayoutConstants.ActionMenuButtonRadius;

        Check.That(clamped.X).IsEqualTo(expected);
        Check.That(clamped.Y).IsEqualTo(expected);
    }

    [Fact]
    public void ClampMargin_IsSymmetric_AtTheBottomRight()
    {
        DpiHelper.Initialize(1.0, 1.0);

        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(ScreenWidth, ScreenHeight),
            ViewPosition.AtGaze,
            circleSize: 250,
            ScreenWidth,
            ScreenHeight);

        var ring = 250 / 2 + LayoutConstants.ActionMenuButtonRadius + LayoutConstants.ActionMenuButtonOuterGap;
        var expected = ring + LayoutConstants.ActionMenuButtonRadius;

        Check.That(clamped.X).IsEqualTo(ScreenWidth - expected);
        Check.That(clamped.Y).IsEqualTo(ScreenHeight - expected);
    }

    [Fact]
    public void AGazePointWellInsideTheMargins_IsNotMoved()
    {
        DpiHelper.Initialize(1.0, 1.0);

        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(960, 540),
            ViewPosition.AtGaze,
            circleSize: 250,
            ScreenWidth,
            ScreenHeight);

        Check.That(clamped.X).IsEqualTo(960.0);
        Check.That(clamped.Y).IsEqualTo(540.0);
    }

    [Fact]
    public void AtCenter_IgnoresTheGazePoint()
    {
        DpiHelper.Initialize(1.0, 1.0);

        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(10, 10),
            ViewPosition.AtCenter,
            circleSize: 250,
            ScreenWidth,
            ScreenHeight);

        Check.That(clamped.X).IsEqualTo(ScreenWidth / 2);
        Check.That(clamped.Y).IsEqualTo(ScreenHeight / 2);
    }
}
```

If `[Collection("DpiHelper Tests")]` is not an existing collection name in `EyeCtrl.ViewModels.Tests`, drop the attribute — `DpiHelper.Initialize(1.0, 1.0)` at the top of each test is what matters. Check with:

```bash
grep -rn "Collection(\"DpiHelper" tests/EyeCtrl.ViewModels.Tests
```

- [ ] **Step 2: Run test to verify it passes against today's constants**

Run:

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuLayoutHelperTests"
```

Expected: PASS, 4 tests. These are characterisation tests written against the constants rather than against literals, so they pass now and keep passing through Task 3 — that is the point. If any fails now, the helper does not do what this plan assumes and the plan is wrong; stop and re-read `src/EyeCtrl.ViewModels/Helpers/ActionMenuLayoutHelper.cs`.

- [ ] **Step 3: Redirect the ViewModel's two call sites**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, replace lines 53 and 55:

```csharp
        EffectiveRingRadius = message.CircleSize / 2 + UIConstants.ActionMenuButtonRadius + UIConstants.ActionMenuButtonOuterGap;
        var physicalRingRadius = DpiHelper.LogicalToPhysicalX(EffectiveRingRadius);
        var physicalButtonRadius = DpiHelper.LogicalToPhysicalX(UIConstants.ActionMenuButtonRadius);
```

- [ ] **Step 4: Redirect the window geometry**

In `src/EyeCtrl.App/Overlays/ActionMenuGeometry.cs`, replace the `ButtonRadius` declaration:

```csharp
    /// <summary>
    /// Radius of an action-menu button, in logical (DIP) pixels. Reads the Core constant rather
    /// than restating it: this value and the ViewModel's dwell-target radius must agree, or the
    /// drawn button and the thing the gaze actually hits are different circles.
    /// </summary>
    internal const double ButtonRadius = LayoutConstants.ActionMenuButtonRadius;
```

Add the using directive at the top of the file if `EyeCtrl.Core` is not already imported:

```csharp
using EyeCtrl.Core;
```

- [ ] **Step 5: Run the full gate**

Run:

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS. Nothing has moved yet.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.ViewModels/ActionMenuViewModel.cs src/EyeCtrl.App/Overlays/ActionMenuGeometry.cs tests/EyeCtrl.ViewModels.Tests/ActionMenuLayoutHelperTests.cs
git commit -m "refactor(actionmenu): read the action-menu radius, and cover the clamp helper

ActionMenuLayoutHelper had no tests despite deciding how far the menu is torn
from the gaze point near a screen edge. Characterisation tests first, so the
next commit's number change is watched.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Raise the outer gap to 60 and the button radius to 70

**Files:**
- Modify: `src/EyeCtrl.Core/LayoutConstants.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/UIConstantsTests.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/MagicNumberReplacementTests.cs:11-20`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs:94-116, 233`

This is the behaviour change. Both numbers move in one commit because `ActionMenuViewModelTests` asserts the composed ring radius, so splitting them leaves a red suite in between.

- [ ] **Step 1: Update the guard test to expect the split values**

In `tests/EyeCtrl.ViewModels.Tests/UIConstantsTests.cs`, replace the body of `ActionMenuButtonRadius_IsSeparateFrom_TheSharedButtonRadius` (keep the doc comment):

```csharp
    [Fact]
    public void ActionMenuButtonRadius_IsSeparateFrom_TheSharedButtonRadius()
    {
        Check.That(UIConstants.ButtonRadius).IsEqualTo(50.0);
        Check.That(UIConstants.ActionMenuButtonRadius).IsEqualTo(70.0);
        Check.That(UIConstants.ButtonRadius).IsNotEqualTo(UIConstants.ActionMenuButtonRadius);
    }
```

And in the same file, update `ActionMenuButtonOuterGap_IsDefinedAndPositive`:

```csharp
    [Fact]
    public void ActionMenuButtonOuterGap_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ActionMenuButtonOuterGap > 0).IsTrue();
        Check.That(UIConstants.ActionMenuButtonOuterGap).IsEqualTo(60.0);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~UIConstantsTests"
```

Expected: FAIL, 2 tests. German output: two `Fehler` lines naming `ActionMenuButtonRadius_IsSeparateFrom_TheSharedButtonRadius` and `ActionMenuButtonOuterGap_IsDefinedAndPositive`, reporting actual 50 and 20 against expected 70 and 60.

- [ ] **Step 3: Raise both constants**

In `src/EyeCtrl.Core/LayoutConstants.cs`, set the two values (leave `SharedButtonRadius` at 50):

```csharp
    /// <summary>
    /// Radius of action menu buttons in logical pixels.
    /// Used for coordinate clamping calculations to ensure buttons stay on screen.
    /// Deliberately NOT <see cref="SharedButtonRadius"/> — see that constant for why.
    ///
    /// 70 since 2026-08-26 (was 50). A 140 DIP target carries a drawn icon at
    /// <c>ContainerRatio.Dominant</c> without the Unicode glyph's font-fallback lottery, and gives
    /// the ring room the old 100 DIP target did not have.
    /// </summary>
    public const double ActionMenuButtonRadius = 70.0;

    /// <summary>
    /// Gap between the cursor circle edge and the action menu button ring in logical pixels.
    /// Used in conjunction with circle size to calculate the effective ring radius for positioning.
    ///
    /// 60 since 2026-08-26 (was 20). At 20 the ring sat roughly a third of a degree of visual
    /// angle from the preview circle, so gaze resting on the thing about to be acted upon was one
    /// jitter away from arming a target. 60 DIP is roughly a degree at desk distance. This is
    /// reasoning, not measurement — it is the number to revisit first if mis-selection persists.
    /// </summary>
    public const double ActionMenuButtonOuterGap = 60.0;
```

- [ ] **Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~UIConstantsTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Update the three tests that hardcode the old ring radius**

In `tests/EyeCtrl.ViewModels.Tests/MagicNumberReplacementTests.cs`, replace `ActionMenuRingRadius_UsesNamedConstants`:

```csharp
    [Fact]
    public void ActionMenuRingRadius_UsesNamedConstants()
    {
        // The formula: circleSize/2 + ActionMenuButtonRadius + ActionMenuButtonOuterGap
        // With circleSize=100, should be: 100/2 + 70 + 60 = 180
        var circleSize = 100.0;
        var expectedRadius = circleSize / 2 + UIConstants.ActionMenuButtonRadius + UIConstants.ActionMenuButtonOuterGap;

        Check.That(expectedRadius).IsEqualTo(180.0);
    }
```

In `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs`, update the comment and expected value at lines 99 and 103:

```csharp
        // CircleSize=250 -> 250/2 + 70 + 60 = 255
```
```csharp
        Check.That(vm.EffectiveRingRadius).IsEqualTo(255);
```

At lines 112 and 116:

```csharp
        // CircleSize=100 -> 100/2 + 70 + 60 = 180
```
```csharp
        Check.That(vm.EffectiveRingRadius).IsEqualTo(180);
```

At line 233:

```csharp
        Check.That(distance >= 250 && distance <= 260).IsTrue(); // EffectiveRingRadius ~255 with default CircleSize
```

- [ ] **Step 6: Run the full gate**

Run:

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS. `ActionMenuGeometryTests.EveryActionButton_LandsFullyOnCanvas` runs at 100% and 200% DPI with and without the Back button and must stay green unassisted — `ActionMenuGeometry.WindowSize` derives from the ring radius, so the larger window is computed, not hardcoded. If it fails, `WindowSizeMargin` is too small for the new radius and Task 4 is the fix; read its failure message for the offending button id before changing anything.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Core/LayoutConstants.cs tests/EyeCtrl.ViewModels.Tests/UIConstantsTests.cs tests/EyeCtrl.ViewModels.Tests/MagicNumberReplacementTests.cs tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs
git commit -m "feat(actionmenu): 140 DIP targets, 60 DIP clear of the cursor circle

Ring radius 195 -> 255 at the default CircleSize. The clamp margin follows to
325 DIP (was 245), so near a screen edge the menu sits 80 DIP further from the
gaze point than before — the accepted cost of the larger, better-separated
targets.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Pin the two clearances the new numbers are supposed to buy

**Files:**
- Test: `tests/EyeCtrl.App.Tests/Overlays/ActionMenuGeometryTests.cs`

Tasks 1–3 change numbers. Nothing yet asserts *why* those numbers: that the ring clears the preview circle by the gap, and that the window has room for the halo ring `DwellArcButton` draws 3 DIP outside each button. Both are silent failures — a button touching the preview circle still renders, and a clipped halo just looks thin.

- [ ] **Step 1: Write the failing test**

Add to `tests/EyeCtrl.App.Tests/Overlays/ActionMenuGeometryTests.cs`, inside the existing class:

```csharp
    /// <summary>
    /// The nearest button edge must clear the cursor preview circle by exactly
    /// <see cref="LayoutConstants.ActionMenuButtonOuterGap"/>.
    ///
    /// This is what the gap constant is FOR, and nothing asserted it: the ring radius formula
    /// happens to produce the clearance, so a later edit that added a term to the formula would
    /// keep every existing test green while closing the gap the user actually looks across.
    /// </summary>
    [Theory]
    [InlineData(250.0)]
    [InlineData(100.0)]
    [InlineData(500.0)]
    public void TheNearestButtonEdge_ClearsThePreviewCircle_ByTheOuterGap(double circleSize)
    {
        DpiHelper.Initialize(1.0, 1.0);

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ActionMenuViewModel(settings, messenger);

        var center = new PhysicalPoint(1200, 800);
        vm.Receive(new ShowActionMenuMessage(
            center,
            settings.ActionMenu.VisibleActions,
            ShowBack: false,
            DwellTimeMs: 600,
            GazePoint: center,
            ManualSelectionDisplayRadius: 0,
            CircleSize: circleSize));

        var nearestButtonEdge = vm.EffectiveRingRadius - ActionMenuGeometry.ButtonRadius;
        var previewCircleEdge = circleSize / 2;

        Check.That(nearestButtonEdge - previewCircleEdge)
             .IsEqualTo(LayoutConstants.ActionMenuButtonOuterGap);
    }

    /// <summary>
    /// The window must hold each button plus whatever the button strokes beyond its own radius.
    /// <see cref="ActionMenuGeometry.WindowSizeMargin"/> is the only slack there is, and it is
    /// split between the two sides — so the test is against half of it.
    ///
    /// **Do not confuse this with the arc halo.** <c>DwellArcButton.HaloPath</c> is the PROGRESS
    /// ARC's halo, stroked at <c>radius - arcInset</c> (4) with thickness <c>arcThickness + 2</c>
    /// (6), so its outer edge lands at <c>radius - 1</c> — entirely inside the button and no
    /// concern of the window's. The overshoot that matters is the RIM halo Task 5 adds, whose
    /// painted band runs radius+1 to radius+4.
    /// </summary>
    [Fact]
    public void TheWindowMargin_HoldsTheRimHalo()
    {
        // The outermost thing DwellArcButton paints, from Task 5's RimHaloOffset + half its
        // thickness: 2.5 + 1.5 = 4 DIP past the button radius.
        const double RimHaloOvershootDip = 4.0;

        Check.That(ActionMenuGeometry.WindowSizeMargin / 2.0)
             .IsStrictlyGreaterThan(RimHaloOvershootDip);
    }
```

Add these using directives to the file if not already present:

```csharp
using EyeCtrl.Core;
```

- [ ] **Step 2: Run test to verify it fails**

Before running, temporarily set `ActionMenuButtonOuterGap` back to `20.0` in `src/EyeCtrl.Core/LayoutConstants.cs` to prove the clearance test can fail:

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TheNearestButtonEdge_ClearsThePreviewCircle"
```

Expected: PASS anyway — the formula still produces the gap, whatever the gap is. That is the correct outcome and worth seeing: this test guards the *formula*, not the value. Restore `60.0` before continuing.

To see it genuinely fail, temporarily change `ActionMenuViewModel.cs:53` to add `+ 5` to the ring radius, re-run, confirm FAIL, then revert.

- [ ] **Step 3: No implementation needed**

Both assertions hold against the code as Task 3 left it. If `TheWindowMargin_HoldsTheHaloRing` fails, raise `ActionMenuGeometry.WindowSizeMargin` from 20 to 32 and re-run the whole `ActionMenuGeometryTests` class.

- [ ] **Step 4: Run the full gate**

Run:

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Overlays/ActionMenuGeometryTests.cs
git commit -m "test(actionmenu): pin the preview-circle clearance and the halo margin

Both were silent failures: a button touching the preview circle still renders,
and a clipped halo just looks thin.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Give the button rim an outer halo

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellArcButton.cs`
- Modify: `tests/EyeCtrl.App.Tests/Controls/DwellArcButtonTests.cs` — `AddTo_AddsAllVisualsToCanvas` pins a child count that changes
- Test: `tests/EyeCtrl.Controls.Tests/DwellArcButtonRimTests.cs` (create)

**This task alone changes `DwellArcButton`, and unlike every other change in this series it is NOT additive** — it alters how all three callers render (`ActionMenuWindow.xaml.cs:187`, `EdgeButtonBarWindow.xaml.cs:83`, `GlassBackdropWindow.xaml.cs:224`). `impact({target: "DwellArcButton", direction: "upstream"})` is **HIGH**: 15 impacted symbols, 2 execution flows, both `earliest_broken_step: 1`. That is deliberate — the rim is a legibility fix every dwell button on a transparent overlay benefits from, and confining it to the action menu would mean two rim treatments in one app.

Today `bg` is stroked at `StrokeThickness = 1` in `BorderSubtle` (`DwellArcButton.cs:86-87`). Computed from the Midnight tokens, that rim sits at **2.72:1** against the `#2A2A44` fill — below the 3:1 floor WCAG 1.4.11 asks of a component boundary. Worse, these windows float over arbitrary desktop content: against a near-black window the fill itself reaches only **1.31:1**, so a 1 DIP line is the entire silhouette.

Pairing a light inner rim with a dark outer halo makes the visible feature the *edge between them*, which survives any backdrop. It is the same §4.1.3 device the progress arc and the crosshair already use — note that `DwellArcButton`'s existing `HaloPath` is the **arc's** halo, drawn at `radius - arcInset` and therefore entirely inside the button. There is no rim halo today.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/DwellArcButtonRimTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The button silhouette must read against any desktop content behind it.
///
/// <c>[StaFact]</c> because Create builds WPF shapes. No window is created.
/// </summary>
public class DwellArcButtonRimTests
{
    private const double Radius = 70;

    [StaFact]
    public void TheRim_IsTwoDipOfBorderSubtle()
    {
        var button = DwellArcButton.Create(new Point(0, 0), Radius, symbol: null);

        Check.That(button.Background.StrokeThickness).IsEqualTo(2.0);
        Check.That(button.Background.Stroke).IsSameReferenceAs(Tokens.BrushBorderSubtle);
    }

    [StaFact]
    public void TheRimHalo_SitsOutsideTheButton_AndDoesNotOverlapTheRim()
    {
        var button = DwellArcButton.Create(new Point(0, 0), Radius, symbol: null);
        var halo = button.RimHalo;

        Check.WithCustomMessage("No rim halo was built.").That(halo).IsNotNull();

        // Stroke straddles the ellipse edge, so the painted band is centre-radius +/- half.
        var haloCentreRadius = halo!.Width / 2;
        var inner = haloCentreRadius - halo.StrokeThickness / 2;
        var outer = haloCentreRadius + halo.StrokeThickness / 2;

        // The rim's outer edge is radius + 1 (2 DIP straddling radius).
        Check.WithCustomMessage($"The halo starts at {inner}, inside the rim's outer edge {Radius + 1}.")
             .That(inner).IsGreaterOrEqualThan(Radius + 1);
        Check.WithCustomMessage($"The halo reaches {outer}, past the {ActionMenuRimOvershoot} DIP budget.")
             .That(outer).IsLessOrEqualThan(Radius + ActionMenuRimOvershoot);
        Check.That(halo.Stroke).IsSameReferenceAs(Tokens.BrushStrokeHalo);
        Check.That(halo.Fill).IsNull();
    }

    /// <summary>
    /// How far past the button radius the halo may reach. The action-menu window's only slack is
    /// half of <c>ActionMenuGeometry.WindowSizeMargin</c> (10 DIP), so anything under that is safe;
    /// this pins the number so a later widening cannot silently clip the outermost buttons.
    /// </summary>
    private const double ActionMenuRimOvershoot = 4;

    [StaFact]
    public void TheRimHalo_IsPaintedBehindTheFill()
    {
        // Order matters: the halo is a full ring, and painting it after the background would lay
        // dark over the light rim's outer half, collapsing the light/dark edge pair this exists for.
        var canvas = new Canvas();
        DwellArcButton.Create(new Point(0, 0), Radius, symbol: null).AddTo(canvas);

        var halo = canvas.Children.OfType<Ellipse>().First(e => e.Fill is null);
        var fill = canvas.Children.OfType<Ellipse>().First(e => e.Fill is not null);

        Check.That(canvas.Children.IndexOf(halo)).IsStrictlyLessThan(canvas.Children.IndexOf(fill));
    }
}
```

Add `using System.Linq;`.

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~DwellArcButtonRimTests"
```

Expected: FAIL to **compile**, `CS1061: 'DwellArcButton' does not contain a definition for 'RimHalo'`.

- [ ] **Step 3: Add the halo ring and thicken the rim**

In `src/EyeCtrl.Controls/DwellArcButton.cs`, add the property beside `Background`:

```csharp
    /// <summary>
    /// A dark ring stroked just outside the button edge. Pairs with the light
    /// <see cref="Background"/> rim so the visible feature is the edge between them, which holds
    /// on any desktop content — the rim alone is 2.72:1 on the fill and the fill alone can reach
    /// 1.31:1 on a near-black window.
    /// </summary>
    public Ellipse RimHalo { get; }
```

Add the geometry constants beside `BadgeDiameterThreshold`:

```csharp
    /// <summary>Rim width in DIP, straddling the button radius (so it spans radius +/- 1).</summary>
    private const double RimThickness = 2;

    /// <summary>Halo ring width in DIP.</summary>
    private const double RimHaloThickness = 3;

    /// <summary>
    /// Gap in DIP from the button radius to the halo ring's centre line. 2.5 places the halo's
    /// painted band at radius+1 to radius+4 — starting exactly where the 2 DIP rim ends, so the
    /// two never overlap and the light/dark edge lands on the silhouette.
    /// </summary>
    private const double RimHaloOffset = 2.5;
```

Change `bg`'s stroke thickness (`DwellArcButton.cs:87`):

```csharp
            StrokeThickness = RimThickness
```

and build the halo immediately before it:

```csharp
        var rimHalo = new Ellipse
        {
            Width = dia + RimHaloOffset * 2,
            Height = dia + RimHaloOffset * 2,
            Fill = null,
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = RimHaloThickness,
        };
        Canvas.SetLeft(rimHalo, center.X - radius - RimHaloOffset);
        Canvas.SetTop(rimHalo, center.Y - radius - RimHaloOffset);
```

Add `rimHalo` to the private constructor, its assignment, and the `return`, mirroring `Background`. In `AddTo`, add it **first**:

```csharp
    public void AddTo(Canvas canvas)
    {
        canvas.Children.Add(RimHalo);       // must precede Background
        canvas.Children.Add(Background);
        canvas.Children.Add(HaloPath);      // must precede ProgressPath
        canvas.Children.Add(ProgressPath);
        if (Symbol is not null)
            canvas.Children.Add(Symbol);
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~DwellArcButtonRimTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Update the child-count guard**

`tests/EyeCtrl.App.Tests/Controls/DwellArcButtonTests.cs`'s `AddTo_AddsAllVisualsToCanvas` pins 4 children for a glyph button; it is now 5. Read the test and raise the number, updating its message to name the halo.

- [ ] **Step 6: Run the full gate**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS. Note `EyeCtrl.Controls.Tests` is in this gate — plan 1's other tasks did not need it.

Nothing in the repo guards `DwellArcButton`'s child order: `HaloZOrderTests` inspects `x:Name` order in seven XAML files and names no `.cs` file, and `HaloContrastTests` is theme colour arithmetic with zero references to this class. Step 1's third test is the only thing holding the halo behind the fill — do not delete it as redundant.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Controls/DwellArcButton.cs tests/EyeCtrl.Controls.Tests/DwellArcButtonRimTests.cs tests/EyeCtrl.App.Tests/Controls/DwellArcButtonTests.cs
git commit -m "fix(controls): pair the dwell button rim with an outer halo

The 1 DIP BorderSubtle rim is 2.72:1 on the fill, under WCAG 1.4.11's 3:1 for a
component boundary, and these buttons float over arbitrary desktop content where
the fill alone can reach 1.31:1. A light inner rim against a dark outer halo makes
the silhouette an edge rather than a line.

Applies to every DwellArcButton caller, not just the action menu.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Confirm the affected scope is only what this plan intended

**Files:** none modified.

CLAUDE.md requires `detect_changes()` before committing the branch. This runs it against `main` for the whole plan rather than per task.

- [ ] **Step 1: Run change detection against the default branch**

Run the GitNexus MCP tool:

```
detect_changes({scope: "compare", base_ref: "main"})
```

- [ ] **Step 2: Check the affected set**

Expected affected symbols: `LayoutConstants`, `UIConstants`, `ActionMenuViewModel.Receive`, `ActionMenuGeometry`, `ActionMenuLayoutHelper`, `DwellArcButton.Create`, `DwellArcButton.AddTo` and the five test classes.

`DwellArcButton`'s dependents WILL appear, and that is expected for Task 5 — the rim is meant to reach every caller. What must NOT appear is any of the eight shared-radius call sites changing size.

**Any hit naming `ActivationButtonViewModel`, `GlassBackdropViewModel`, `QuickActionsGridViewModel`, `TaskSwitcherViewModel`, `ScrollModeController` or `OverlayVmBindingCoordinator` means Task 1 leaked** — the shared radius moved after all. Stop and re-read `UIConstants.ButtonRadius`.

- [ ] **Step 3: Verify the eight shared surfaces are untouched by hand**

Run:

```bash
grep -rn "UIConstants.ButtonRadius" src --include=*.cs | grep -v "/obj/"
```

Expected: the same eight call sites as before this plan, and **no** hit in `ActionMenuViewModel.cs`.

- [ ] **Step 4: Commit nothing**

This task produces no diff. If it found one, that is a bug in Tasks 1–3, not something to commit.

---

## What this plan deliberately does not do

- **No rendering changes.** Buttons are still `DwellArcButton.Create(point, radius, symbol)` with Unicode glyphs at `ContainerRatio.Small` — 140 × 0.23 = 32 DIP, larger than today's 23. The glyphs get bigger; they do not get better. That is plan 2.
- **No icon rendering.** Task 5 changes the button's rim; the face still carries a Unicode glyph at `ContainerRatio.Small` — 140 × 0.23 = 32 DIP, larger than today's 23. The glyphs get bigger; they do not get better. That is plan 2.
- **One doc comment is left stale on purpose.** `tests/EyeCtrl.Controls.Tests/DwellArcButtonGlyphSizeTests.cs:38` says its 50 radius is both `UIConstants.ButtonRadius` and `ActionMenuGeometry.ButtonRadius`. Task 1 makes the second half wrong. The test still passes and still guards what it should — 50 is the backdrop hide button — but the comment now misnames one of its two callers. Plan 2 Task 3 Step 6 rewrites it, at the point where the action menu stops rendering a glyph at all and the sentence can be made true rather than patched. If plan 2 is deferred, fix it here instead: a comment that names the wrong constant is how the next person re-learns this the hard way.
- **No settings change.** All eight actions still ring the cursor, and they still overlap at small `CircleSize` values — `CursorSettings.CircleSize` permits 10–500.

  Neighbour chord is `2 · ringRadius · sin(π/8)`; buttons overlap once it falls below the button diameter. Today that is `CircleSize` < **121**; after this plan it is `CircleSize` < **106**. So the overlap threshold *improves*, because the gap grows by 40 while the radius grows by only 20. At `CircleSize` 100 the overlap narrows from 8 DIP to 2 DIP. At the default 250 neither overlaps: the chord is 195 DIP against a 140 DIP button.

  It is still a real defect below 106, and plan 3's five-target primary layer is what actually fixes it — five buttons have a chord of `2R·sin(36°)`, which clears 140 DIP at every permitted `CircleSize`. Do not paper over it here with a smaller radius.

## Self-review

**Spec coverage:** the canvas's geometry claims are the two constants (Tasks 1–3), the clamp-margin consequence (Task 2's tests), and the preview-circle clearance (Task 4). The icon set, the label toggle and the More layer are explicitly deferred above.

**Placeholders:** none — every step carries the literal code or command.

**Type consistency:** `SharedButtonRadius`, `ActionMenuButtonRadius` and `ActionMenuButtonOuterGap` are spelled identically in Tasks 1, 2, 3 and 4. `UIConstants.ActionMenuButtonRadius` is introduced in Task 1 Step 4 before its first use in Task 2 Step 3. `ActionMenuGeometry.WindowSizeMargin` already exists (`src/EyeCtrl.App/Overlays/ActionMenuGeometry.cs:20`) and is `internal`, reachable from `EyeCtrl.App.Tests` only if `InternalsVisibleTo` is configured — `ActionMenuGeometryTests` already reads `ActionMenuGeometry.ButtonRadius`, so it is.
