# Toolbar Slide Transition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the docked toolbar retract into its docked screen edge when auto-hide fires, and slide back out of that edge when summoned, instead of vanishing and reappearing between two frames.

**Architecture:** A `TranslateTransform` on `ToolbarWindow`'s root content, animated 200 ms per transition by a single `DoubleAnimation` on whichever axis the docked edge selects. `ToolbarWindow` owns *how* to slide (direction, clock lifetime, cancellation, two state flags); `ToolbarVisibilityAdapter` keeps owning *when*. The auto-hide state machine, `IToolbarVisibility`, and every settings surface are untouched.

**Tech Stack:** C# / .NET 10 / WPF. Tests are xUnit v3 with NFluent (`Check.That`), `Xunit.StaFact` for STA tests. Solution file is `EyeCtrl.slnx`.

**Spec:** `docs/superpowers/specs/2026-09-06-toolbar-slide-transition-design.md` (reviewed; 14 findings applied across two rounds). Read it before Task 1 — this plan argues from it and does not restate its rationale.

## Global Constraints

- **Never launch the app, run UI-driving automation, or simulate input.** The user drives this PC solely by eye gaze; seizing the cursor or keyboard removes their only input channel. Ask for an explicit go-ahead before any such run, and keep it short if granted.
- **Headless test command:** `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`. This is what CI runs (`.github/workflows/ci.yml:84`), so `Category=UI` tests are verified locally only.
- **`dotnet test` output is German on this machine.** Per-test failures print as `Fehler <FullyQualifiedName>`, *not* `Failed`/`Fehlgeschlagen`, and long names wrap across lines. Grepping for `Failed` reports a false green. Read the summary line (`Fehlgeschlagen! – Fehler: N`) and grep for `Fehler ` with a trailing space.
- **Slide duration is 200 ms; easing is `CubicEase`** — `EasingMode.EaseOut` for slide-in, `EasingMode.EaseIn` for slide-out. Both are `private const` / literals in `ToolbarWindow`. **No new setting**: no `ToolbarSettings` field, no `EnumerateSubSettings` entry, no settings row, no migration, and `SystemParameters.ClientAreaAnimation` is deliberately not read.
- **Dwell arms at the *start* of the slide-in and is disarmed for the whole slide-out.** This asymmetry is a decided requirement, not an oversight (spec Goals + open question 1).
- **Commit after every task.** Do not batch.

## Deviation from the spec — needs sign-off before Task 4

The spec specifies the dwell-target placement guard as `IsVisible && !IsSliding` (spec §"What must not run mid-slide", mutation row 3). **This plan implements `!_sliding && the transform is at its resting value` instead**, on precision grounds:

The hazard is "the transform is displacing the faces from where their dwell targets sit". `IsVisible` is a *proxy* for that; the transform's own value is the thing itself. The transform form covers both states the spec's form covered — the hidden period (the base value sits parked at the offset for its whole duration, which is what spec review round 2 established) and a live slide (the flag) — and additionally covers one the spec's form gets *wrong*: a band that is visible but displaced, where `IsVisible && !IsSliding` would publish rest-plus-offset and latch it.

**An earlier draft of this section justified the deviation differently and was wrong.** It claimed an `IsVisible` conjunct would gut `ToolbarTargetPlacementTests.FreshBand_IsNotArranged_SoNothingIsPublished` by returning before the `FacesAreArranged()` ordering that test exists to prove. That test has no such teeth to lose: `UpdateTargetPositions` wraps each `PointToScreen` in `try { … } catch { return; }` (`ToolbarWindow.xaml.cs:277-287`), so an unarranged face's throw is swallowed and `_targetsInitialized` never latches — the test stays green whichever guard returns, and green even with `FacesAreArranged` deleted entirely. Verified by running it. Do not restore that argument.

**Consequences:** the spec's mutation row 3 ("Drop the `IsVisible` conjunct") becomes "Drop the transform-at-rest check", which Task 7 uses. The spec needs a one-line sync, listed as Task 7 Step 12 rather than done silently — and that sync must carry the precision argument, not the refuted one.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/EyeCtrl.App/Overlays/OverlayPositioning.cs` (modify) | Gains `SlideVectorFor` — the pure direction table. Joins `PlaceAtEdge` because it is the same domain (edge-keyed overlay geometry) and because this class's tests are plain `[Fact]`, which is what makes the geometry headless *by precedent* rather than by hoping WPF's type initialisers are benign. **This is where the spec's Wiring section says `ToolbarWindow.xaml.cs`; the function is unchanged, only better placed.** |
| `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml` (modify) | Clipping `Grid` wrapper, named root `Border`, the `TranslateTransform`. |
| `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (modify) | Slide edge, both flags, `IsOnScreen`, `CancelSlide`, `SlideOutThenHide`, `ShowThenSlideIn`, `ShowAtRest`, the completion seam, the placement guard. |
| `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs` (modify) | Re-sequenced `ShowToolbar` / `HideToolbar` / `ForceShow`. |
| `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs` (modify, line 349) | Re-arm gate reads `IsOnScreen`. |
| `tests/EyeCtrl.App.Tests/Overlays/ToolbarSlideGeometryTests.cs` (create) | Headless direction table. |
| `tests/EyeCtrl.App.Tests/ToolbarSlideTests.cs` (create) | STA behaviour: flags, sequencing, cancellation, placement guard. |
| `tests/EyeCtrl.App.Tests/TaskSwitcherDwellPositionTests.cs` (modify, line 166) | The **existing** source pin on the re-arm gate. It asserts the gate reads `Toolbar.IsVisible`, so Task 6 breaks it; Task 6 updates it to `Toolbar.IsOnScreen` and widens it. |
| `tests/EyeCtrl.App.Tests/Services/Coordinators/TaskSwitcherReArmTests.cs` (create) | Behavioural cover for the re-arm gate — the only thing that catches a polarity inversion (Task 7). |
| `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs` (modify, line 603) | `(Border)window.Content` → `window.StripRoot`, forced by the wrapper. |
| `tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs` (modify) | Hide-ordering test rewritten to the new contract. |

**On the re-arm gate's coverage.** The spec's Testing section calls for a task-switcher-closes-mid-slide-in behavioural test in `OverlayVmBindingCoordinatorTests`, on the stated grounds that the gate has **zero coverage today**. That premise is false, and this plan previously repeated it: `TaskSwitcherDwellPositionTests.Switcher_DeactivatesToolbarDwell_WhileCoveringIt` (`:148-167`) is a pre-existing source pin over that exact method, added after the 2026-08-18 field report, and it asserts the gate reads `Toolbar.IsVisible`. So the gate is covered, by a pin rather than a behavioural test — and Task 6 **breaks that pin** unless it updates it.

**The behavioural test is IN scope** — Task 7 — because the pin cannot catch a *polarity* inversion: `if (!Context.OverlayManager.Toolbar.IsOnScreen)` satisfies both the pin and the `IsOnScreen` truth table while doing the exact opposite. Coverage is therefore three-layered and each layer catches something the others cannot: the truth table (Task 3) covers the property's logic, the pin (Task 6) covers which symbol the call site reads, and the behavioural tests (Task 7) cover polarity.

Task 7 also records the cost history, because this plan mis-estimated it three times running and each estimate was refuted by a single grep. Short version: `TestFakes.MakeContext` already exists, so the harness is ~60 lines rather than prohibitive.

---

### Task 1: The slide direction table

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/OverlayPositioning.cs`
- Test: `tests/EyeCtrl.App.Tests/Overlays/ToolbarSlideGeometryTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `OverlayPositioning.SlideVectorFor(string? edge, double width, double height) → System.Windows.Vector`. Returns the offset that puts a docked strip fully clear of its own window. `(0,0)` means "no slide".

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Overlays/ToolbarSlideGeometryTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;

/// <summary>
/// <see cref="OverlayPositioning.SlideVectorFor"/> is the entire direction table of the toolbar's
/// slide transition, and it is pure — no window, no dispatcher, no STA thread. Plain [Fact]s for
/// the same reason <see cref="OverlayPositioningTests"/> are: nothing here touches WPF beyond a
/// struct, so these run in CI's headless subset (.github/workflows/ci.yml:84 filters Category!=UI).
/// </summary>
public class ToolbarSlideGeometryTests
{
    private const double W = 900;
    private const double H = 140;

    [Fact]
    public void TopDock_RetractsUpward_ByTheFullHeight()
    {
        var v = OverlayPositioning.SlideVectorFor("top", W, H);
        Check.That(v.X).IsEqualTo(0d);
        Check.That(v.Y).IsEqualTo(-H);
    }

    [Fact]
    public void BottomDock_RetractsDownward_ByTheFullHeight()
    {
        var v = OverlayPositioning.SlideVectorFor("bottom", W, H);
        Check.That(v.X).IsEqualTo(0d);
        Check.That(v.Y).IsEqualTo(H);
    }

    [Fact]
    public void LeftDock_RetractsLeftward_ByTheFullWidth()
    {
        var v = OverlayPositioning.SlideVectorFor("left", W, H);
        Check.That(v.X).IsEqualTo(-W);
        Check.That(v.Y).IsEqualTo(0d);
    }

    [Fact]
    public void RightDock_RetractsRightward_ByTheFullWidth()
    {
        var v = OverlayPositioning.SlideVectorFor("right", W, H);
        Check.That(v.X).IsEqualTo(W);
        Check.That(v.Y).IsEqualTo(0d);
    }

    /// <summary>
    /// PascalCase is what ToolbarSettings.DockedEdge actually stores ("Top"/"Bottom"/"Left"/
    /// "Right"), so case-insensitivity is a real requirement, not politeness — the same reason
    /// OverlayPositioningTests.EdgeCase_IsInsensitive_AndPascalCaseMatches exists.
    /// </summary>
    [Fact]
    public void EdgeMatching_IsCaseInsensitive()
    {
        Check.That(OverlayPositioning.SlideVectorFor("Top", W, H).Y).IsEqualTo(-H);
        Check.That(OverlayPositioning.SlideVectorFor("RIGHT", W, H).X).IsEqualTo(W);
    }

    /// <summary>
    /// Null is the UNDOCKED case and must NOT fall back to "top" the way PlaceAtEdge does: a
    /// floating toolbar has no edge to retract into, and animating it upward would be a bug.
    /// This asymmetry with its neighbour in the same class is deliberate.
    /// </summary>
    [Fact]
    public void NullEdge_MeansNoSlide_NotTop()
    {
        Check.That(OverlayPositioning.SlideVectorFor(null, W, H)).IsEqualTo(default(System.Windows.Vector));
    }

    [Fact]
    public void UnrecognisedEdge_MeansNoSlide()
    {
        Check.That(OverlayPositioning.SlideVectorFor("diagonal", W, H)).IsEqualTo(default(System.Windows.Vector));
    }

    /// <summary>
    /// An unmeasured window (SizeToContent, never laid out) reports zero extent. A zero vector is
    /// the graceful answer — callers degrade to an instant show/hide rather than special-casing it.
    /// </summary>
    [Fact]
    public void ZeroExtent_MeansNoSlide()
    {
        Check.That(OverlayPositioning.SlideVectorFor("top", 0, 0)).IsEqualTo(default(System.Windows.Vector));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideGeometryTests"
```

Expected: compile error — `SlideVectorFor` does not exist on `OverlayPositioning`. A compile failure is the correct "fails first" here; do not proceed until you have seen it.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.App/Overlays/OverlayPositioning.cs`, add after `PlaceAtEdge`:

```csharp
    /// <summary>
    /// The offset that puts a docked strip fully clear of its own window, so animating the strip
    /// toward it reads as retracting into the docked edge. Case-insensitive, matching
    /// <see cref="PlaceAtEdge"/> — <c>ToolbarSettings.DockedEdge</c> stores PascalCase.
    ///
    /// <para>
    /// Deliberately does NOT share <see cref="PlaceAtEdge"/>'s fall-back-to-top behaviour.
    /// An unrecognised or <see langword="null"/> edge returns a ZERO vector, which callers read as
    /// "no slide" and degrade to an instant show/hide. Null is the undocked case, and a floating
    /// toolbar has no edge to retract into — falling back to "top" there would animate it upward
    /// for no reason. Do not "fix" this to match its neighbour.
    /// </para>
    ///
    /// <para>
    /// Returns DIPs, not physical pixels: the consumer is a <c>TranslateTransform</c>, and the
    /// extents passed in are <c>ActualWidth</c>/<c>ActualHeight</c>. No <c>DpiHelper</c>
    /// conversion belongs anywhere near this — unlike the auto-hide hit-test rect, which is
    /// physical.
    /// </para>
    /// </summary>
    public static Vector SlideVectorFor(string? edge, double width, double height) =>
        edge?.ToLowerInvariant() switch
        {
            "top" => new Vector(0, -height),
            "bottom" => new Vector(0, height),
            "left" => new Vector(-width, 0),
            "right" => new Vector(width, 0),
            _ => default,
        };
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideGeometryTests"
```

Expected: 8 passed. Note `ZeroExtent_MeansNoSlide` passes because `new Vector(0, -0)` equals `default` — `-0.0 == 0.0` is true in IEEE 754. If it fails, you have a real bug elsewhere.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/OverlayPositioning.cs tests/EyeCtrl.App.Tests/Overlays/ToolbarSlideGeometryTests.cs
git commit -m "feat(toolbar): add the slide direction table"
```

---

### Task 2: The clipping wrapper and the named root

This task changes no behaviour. Its whole deliverable is that the visual tree can carry a transform without clipping the overhang, plus the one existing test that breaks.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml`
- Modify: `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs:603`

**Interfaces:**
- Consumes: nothing.
- Produces: `ToolbarWindow.StripRoot` (`Border`) and `ToolbarWindow.SlideTransform` (`TranslateTransform`), both generated fields from `x:Name`. Task 3 animates `SlideTransform`.

- [ ] **Step 1: Rewrite the XAML**

Replace the whole body of `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml`, keeping every existing attribute and comment on the inner elements:

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.ToolbarWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        AutomationProperties.AutomationId="Toolbar_Window"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        SizeToContent="WidthAndHeight">
    <!-- ClipToBounds is explicit rather than relying on the HWND to clip the slide overhang.
         An HWND very likely would, but that is a claim about layered-window composition that
         cannot be asserted in a headless test; one element makes the clip a property of the
         visual tree instead. The Grid measures to the Border's desired size, so SizeToContent
         is unaffected — RenderTransform does not participate in measure. -->
    <Grid ClipToBounds="True">
        <!-- x:Name is required, not stylistic: ToolbarButtonVisualTests reaches this element as
             (Border)window.Content and reads its Padding. Wrapping it changes window.Content to
             the Grid, so the test uses this generated field instead — and a rename now fails the
             build rather than silently changing what the test measures. -->
        <Border x:Name="StripRoot"
                Background="{StaticResource BrushSurfaceDark}"
                CornerRadius="{StaticResource StandardCornerRadius}" Padding="4">
            <Border.RenderTransform>
                <TranslateTransform x:Name="SlideTransform"/>
            </Border.RenderTransform>
            <StackPanel x:Name="StripWrapper" Orientation="Horizontal" VerticalAlignment="Center">
                <!-- Chrome, not a target: the provider indicator is not dwellable, so it must not sit
                     inside the button band where it reads as a broken button. Empty until a provider
                     is known: an empty StackPanel's CONTENT measures to zero, but a Margin on the
                     panel itself would NOT (that was the earlier bug here) — so the leading/trailing
                     spacing lives on the indicator's own children (AddIndicatorElement) rather than
                     on this panel, and an absent indicator costs nothing. -->
                <StackPanel x:Name="IndicatorPanel" Orientation="Horizontal"
                            VerticalAlignment="Center"/>
                <!-- DwellArcButton positions itself with Canvas.SetLeft/SetTop, so the button band is
                     a Canvas with an explicitly computed size — a Canvas measures to zero otherwise,
                     and this window is SizeToContent. -->
                <Canvas x:Name="ButtonCanvas"/>
            </StackPanel>
        </Border>
    </Grid>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Run the toolbar tests to see the expected breakage**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarButtonVisualTests"
```

Expected: `StripInsets_AreSymmetric_WhenNoProviderIsSet` fails with `InvalidCastException` (`Grid` cannot be cast to `Border`). Remember the output is German — look for `Fehler ` lines, not `Failed`. Every other test in the class must still pass; if any other test fails, stop and report it rather than fixing it here — it means the wrapper's blast radius is wider than this plan believes.

- [ ] **Step 3: Fix the one broken test**

In `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs`, line 603, replace:

```csharp
        var root = (Border)window.Content;
```

with:

```csharp
        // window.Content is the clipping Grid that carries the slide transform; the strip itself
        // (and its Padding, which the inset arithmetic below needs) is the named Border inside it.
        var root = window.StripRoot;
```

- [ ] **Step 4: Run the full headless subset plus the toolbar UI tests**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Toolbar"
```

Expected: both green. The second command is what proves the wrapper broke nothing else — `ToolbarTargetPlacementTests` casts `window.Content` to `FrameworkElement`, which a `Grid` satisfies, and `ToolbarOrientationTests` uses the `StripWrapper`/`IndicatorPanel` generated fields, whose names are unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs
git commit -m "refactor(toolbar): wrap the strip in a clipping Grid and name the root"
```

---

### Task 3: Slide mechanics on the window

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`
- Test: `tests/EyeCtrl.App.Tests/ToolbarSlideTests.cs` (create)

**Interfaces:**
- Consumes: `OverlayPositioning.SlideVectorFor` (Task 1), `StripRoot` / `SlideTransform` (Task 2).
- Produces, all on `ToolbarWindow`:
  - `internal void SlideOutThenHide(Action onHidden)`
  - `internal void ShowThenSlideIn()`
  - `internal void ShowAtRest()`
  - `internal void CancelSlide()`
  - `internal void CompleteSlideForTest()` — test seam; runs the pending completion under the generation check
  - `public bool IsSliding { get; }`, `public bool IsSlidingOut { get; }`, `public bool IsOnScreen { get; }`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/ToolbarSlideTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.Windows;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The toolbar's slide transition: the two state flags, the sequencing, and cancellation.
///
/// <para>
/// A test STA thread has no message pump, so an animation clock never advances and never raises
/// Completed here. That is the same constraint that produced the EndFlash seam
/// (ToolbarWindow.xaml.cs, ToolbarBand.EndFlash), and the completion path is exercised the same
/// way: driven directly through <c>CompleteSlideForTest</c> rather than waited on. Driving it by
/// hand is also what makes the generation guard observable at all — see
/// <see cref="ForceShow_MidSlideOut_DefusesThePendingHide"/>.
/// </para>
///
/// <para>
/// Category=UI because these construct and Show real windows; CI runs Category!=UI
/// (.github/workflows/ci.yml:84), so these are verified locally.
/// </para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("WpfApplication")]
public class ToolbarSlideTests
{
    /// <summary>A shown, laid-out, top-docked toolbar — the state every slide starts from.</summary>
    private static EyeCtrl.App.Overlays.ToolbarWindow NewDockedToolbar()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        window.Show();
        window.SetMode(isDocked: true, edge: "Top");
        return window;
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void AtRest_IsOnScreen_AndNotSliding()
    {
        var window = NewDockedToolbar();
        try
        {
            Check.That(window.IsSliding).IsFalse();
            Check.That(window.IsSlidingOut).IsFalse();
            Check.That(window.IsOnScreen).IsTrue();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The directional half of the gate. During a slide-OUT the band is leaving and the window is
    /// about to hide, so arming dwell against the published coordinates is unsafe.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void DuringSlideOut_IsNotOnScreen()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });

            Check.That(window.IsSliding).IsTrue();
            Check.That(window.IsSlidingOut).IsTrue();
            Check.That(window.IsOnScreen).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The other half, and the one a single-flag implementation gets wrong. During a slide-IN the
    /// band is visible and staying, dwell is armed by design, and IsOnScreen must say so — because
    /// the slide-in's completion no longer re-arms anything, so a gate that reported false here
    /// would let a task-switcher open/close cycle leave the toolbar visible, at rest, and armed by
    /// nothing.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void DuringSlideIn_IsOnScreen_IsTrue()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();          // now hidden, transform parked at the offset

            window.ShowThenSlideIn();

            Check.That(window.IsSliding).IsTrue();
            Check.That(window.IsSlidingOut).IsFalse();
            Check.That(window.IsOnScreen).IsTrue();
        }
        finally { window.Close(); }
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void WhenHidden_IsNotOnScreen()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            Check.That(window.IsVisible).IsFalse();
            Check.That(window.IsOnScreen).IsFalse();
            Check.That(window.IsSliding).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The clock is attached before Show(), so no frame can be painted with the band at its rest
    /// position. Asserted through IsSliding at the moment IsVisibleChanged fires — that event is
    /// raised synchronously inside Show(), and it needs no clock tick, which a pumpless STA thread
    /// could not deliver anyway.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SlideIn_AttachesTheClock_BeforeShow()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            bool? slidingWhenShown = null;
            void OnVisibleChanged(object s, DependencyPropertyChangedEventArgs e)
            {
                if (window.IsVisible) slidingWhenShown = window.IsSliding;
            }
            window.IsVisibleChanged += OnVisibleChanged;

            window.ShowThenSlideIn();
            window.IsVisibleChanged -= OnVisibleChanged;

            Check.That(slidingWhenShown.HasValue).IsTrue();   // the event fired at all
            Check.That(slidingWhenShown!.Value).IsTrue();     // and the clock was already attached
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The docked edge selects the animation AXIS, not just the sign. A left-docked toolbar must
    /// retract horizontally; nothing else in this class would notice if both edges animated Y,
    /// because the default fixture is top-docked.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SideDock_SlidesAlongX_NotY()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        try
        {
            window.Show();
            window.SetMode(isDocked: true, edge: "Left");
            window.UpdateLayout();

            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            Check.That(window.SlideTransform.Y).IsEqualTo(0d);
            Check.That(window.SlideTransform.X).IsStrictlyLessThan(0d);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Already on screen and at rest: arm dwell, do not animate. Unreachable from the controller
    /// (it calls ShowToolbar only from Hidden) but reached by ToolbarVisibilityAdapterTests, and a
    /// gratuitous jump-to-offset-then-slide-back would be a real glitch if a future caller did it.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SlideIn_OnAnAlreadyVisibleToolbar_ShortCircuits()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            Check.That(window.IsOnScreen).IsTrue();

            window.ShowThenSlideIn();

            Check.That(window.IsSliding).IsFalse();
            Check.That(window.SlideTransform.X).IsEqualTo(0d);
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);
        }
        finally { window.Close(); }
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void SlideOutCompletion_HidesTheWindow_AndRunsItsCallback()
    {
        var window = NewDockedToolbar();
        try
        {
            var ran = false;
            window.SlideOutThenHide(() => ran = true);

            Check.That(window.IsVisible).IsTrue();   // still visible mid-slide
            Check.That(ran).IsFalse();

            window.CompleteSlideForTest();

            Check.That(window.IsVisible).IsFalse();
            Check.That(ran).IsTrue();
            Check.That(window.IsSliding).IsFalse();
            Check.That(window.IsSlidingOut).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Already hidden: no animation, and the callback runs synchronously. SetPaused and
    /// ReassertVisibility both re-assert a hide on an already-hidden toolbar, and animating an
    /// invisible window would delay the pause indicator by the slide duration — latency on the one
    /// path whose whole job is telling a paused user that the app is paused.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SlideOut_OnAnAlreadyHiddenToolbar_ShortCircuits()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            var ran = false;
            window.SlideOutThenHide(() => ran = true);

            Check.That(ran).IsTrue();                 // synchronous, no clock
            Check.That(window.IsSliding).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The flagship interleaving: a modal opens mid-slide-out, so ForceShow must both stop the
    /// animation and defuse its pending Hide(). The flags assertion is the one that catches a
    /// CancelSlide that forgets to clear them — show/transform/no-hide all pass without it, and
    /// the stuck-true flags would leave IsOnScreen false forever.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void ForceShow_MidSlideOut_DefusesThePendingHide()
    {
        var window = NewDockedToolbar();
        try
        {
            var ran = false;
            window.SlideOutThenHide(() => ran = true);

            window.ShowAtRest();

            // The superseded slide's completion must be a no-op.
            window.CompleteSlideForTest();

            Check.That(window.IsVisible).IsTrue();
            Check.That(ran).IsFalse();
            Check.That(window.SlideTransform.X).IsEqualTo(0d);
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);
            Check.That(window.IsSliding).IsFalse();
            Check.That(window.IsSlidingOut).IsFalse();
            Check.That(window.IsOnScreen).IsTrue();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// ForceShow after a slide-out has fully COMPLETED. The transform's base value is parked at
    /// the hidden offset at this point, so ForceShow's own zeroing write is the only thing that
    /// brings the band back — delete it and the toolbar shows permanently off-screen with no
    /// visible cause.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void ForceShow_AfterACompletedSlideOut_ParksTheBandAtRest()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();
            Check.That(window.SlideTransform.Y).IsNotEqualTo(0d);   // parked off-edge

            window.ShowAtRest();

            Check.That(window.IsVisible).IsTrue();
            Check.That(window.SlideTransform.X).IsEqualTo(0d);
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A floating toolbar has no edge to retract into, so both transitions degrade to an instant
    /// show/hide rather than animating.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void Floating_DoesNotSlide()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        try
        {
            window.Show();
            window.SetMode(isDocked: false, edge: "Top");

            var ran = false;
            window.SlideOutThenHide(() => ran = true);

            Check.That(ran).IsTrue();
            Check.That(window.IsSliding).IsFalse();
            Check.That(window.IsVisible).IsFalse();
        }
        finally { window.Close(); }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideTests"
```

Expected: compile errors — none of `SlideOutThenHide`, `ShowThenSlideIn`, `ShowAtRest`, `CompleteSlideForTest`, `IsSliding`, `IsSlidingOut`, `IsOnScreen` exist yet.

- [ ] **Step 3: Add the slide state and geometry to `ToolbarWindow`**

In `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, add these usings if absent:

```csharp
using System.Windows.Media.Animation;
```

Add the fields next to the existing `_vertical` / `_providerType` declarations:

```csharp
    // ── Slide transition ──
    // docs/superpowers/specs/2026-09-06-toolbar-slide-transition-design.md

    /// <summary>How long one slide takes. Matches TargetHighlightWindow's 200 ms fades.</summary>
    private const int SlideDurationMs = 200;

    /// <summary>
    /// The docked edge the band retracts into, lower-cased, or null when floating. Deliberately
    /// NOT derived from <see cref="_vertical"/>: SetMode normalises an undocked toolbar's edge to
    /// "top" for the strip's AXIS, which is the right answer there and the wrong one for a slide —
    /// a floating toolbar has no edge to retract into.
    /// </summary>
    private string? _slideEdge;

    /// <summary>
    /// Bumped on every slide start and every cancellation, so a superseded slide's completion is
    /// a no-op. Insurance rather than necessity — Timeline.Completed documents that stopping a
    /// timeline does not raise it — but the asymmetry of being wrong is a Hide() landing on a
    /// just-force-shown toolbar, which strands a gaze-only user with no controls.
    /// </summary>
    private int _slideGeneration;
    private Action? _pendingSlideCompletion;
    private int _pendingSlideGeneration;
    private bool _sliding;
    private bool _slidingOut;

    /// <summary>Whether the transform is in motion, in either direction.</summary>
    public bool IsSliding => _sliding;

    /// <summary>Whether the band is on its way to being hidden.</summary>
    public bool IsSlidingOut => _slidingOut;

    /// <summary>
    /// Visible and not on its way out — i.e. arming dwell against the published target coordinates
    /// is safe. TRUE during a slide-IN (dwell arms at its start by design), false during a
    /// slide-out, which ends in Hide().
    ///
    /// <para>
    /// <c>!IsSlidingOut</c>, not <c>!IsSliding</c>, and the difference is load-bearing. Because
    /// arming happens at the start of a slide-in, the slide-in's completion no longer repairs dwell
    /// state that something else cleared mid-slide. A gate that reported false during a slide-in
    /// would let a task-switcher open-then-close inside those 200 ms leave the toolbar visible, at
    /// rest, and dwellable by nothing.
    /// </para>
    /// </summary>
    public bool IsOnScreen => IsVisible && !_slidingOut;

    /// <summary>Whether the docked edge slides along Y (top/bottom) rather than X (left/right).</summary>
    private bool SlideAxisIsY => _slideEdge is "top" or "bottom";

    /// <summary>
    /// The offset that puts the band fully clear of the window, or a zero vector when there is
    /// nothing to slide — floating, unrecognised edge, or a window never measured.
    /// </summary>
    private Vector SlideOffset() =>
        OverlayPositioning.SlideVectorFor(_slideEdge, ActualWidth, ActualHeight);
```

Add `using System.Windows;` is already present (the file uses `Point`/`Thickness`); `Vector` comes from it.

- [ ] **Step 4: Record the slide edge in `SetMode`**

In `SetMode`, immediately after the existing `wantVertical` line, add:

```csharp
        // Null when floating: `key` is "top" there for the strip's axis, which must not be read
        // as an edge to retract into.
        _slideEdge = isDocked ? key : null;
```

- [ ] **Step 5: Add the slide engine**

Append to `ToolbarWindow`:

```csharp
    /// <summary>
    /// Stops any slide and returns the flags to a known state. Unconditional and idempotent.
    ///
    /// <para>
    /// Clears the sliding flags itself, and it MUST: the generation bump on the first line is
    /// exactly what stops a cancelled slide's completion from running, so completion cannot be the
    /// thing that clears them. Leave them set and ForceShow-mid-slide strands the user with a
    /// visible, at-rest toolbar whose re-arm gate reads false forever.
    /// </para>
    ///
    /// <para>
    /// Unconditional purely as simplification — under FillBehavior.Stop a finished clock has
    /// already stopped affecting the property, so there is no stale clock to remove. A condition
    /// here would be one more thing to get wrong.
    /// </para>
    /// </summary>
    internal void CancelSlide()
    {
        _slideGeneration++;
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        _sliding = false;
        _slidingOut = false;
    }

    /// <summary>
    /// Cancels any slide, parks the band at rest, and shows the window with no animation. The
    /// recovery paths use this — tray fallback, modal suspend-recover, dock toggle — and every one
    /// exists because the user needs the toolbar now.
    ///
    /// <para>
    /// Zeroes BOTH axes. A slide only ever animates one, so an offset parked on the other by a
    /// previous docked edge is possible in principle; ordering happens to prevent it today
    /// (opening settings force-shows before SetMode changes the edge), which is a property of two
    /// unrelated call sites lining up rather than of this code.
    /// </para>
    /// </summary>
    internal void ShowAtRest()
    {
        CancelSlide();
        SlideTransform.X = 0;
        SlideTransform.Y = 0;
        Show();
    }

    /// <summary>
    /// Slides the band out along its docked edge, then hides the window and invokes
    /// <paramref name="onHidden"/>. Short-circuits to the completion effects, synchronously, when
    /// the window is already hidden or there is nothing to slide.
    /// </summary>
    internal void SlideOutThenHide(Action onHidden)
    {
        var offset = SlideOffset();
        if (!IsVisible || offset == default)
        {
            CancelSlide();
            HideSafely();
            onHidden();
            return;
        }

        StartSlide(to: SlideAxisIsY ? offset.Y : offset.X, slidingOut: true, onDone: () =>
        {
            HideSafely();
            onHidden();
        });
    }

    /// <summary>
    /// Shows the window with the band sliding in from its docked edge. The clock is attached
    /// BEFORE Show(), which makes the off-edge first frame a property of the property's value
    /// rather than of dispatcher scheduling. Short-circuits to a plain Show when the toolbar is
    /// already on screen or there is nothing to slide.
    /// </summary>
    internal void ShowThenSlideIn()
    {
        var offset = SlideOffset();
        if (IsOnScreen || offset == default)
        {
            CancelSlide();
            SlideTransform.X = 0;
            SlideTransform.Y = 0;
            Show();
            return;
        }

        // From the off-edge offset to rest. Note the base value written inside StartSlide is the
        // DESTINATION (0), not this starting value — see StartSlide.
        if (SlideAxisIsY) SlideTransform.Y = offset.Y; else SlideTransform.X = offset.X;
        StartSlide(to: 0, slidingOut: false, onDone: null);
        Show();
    }

    /// <summary>
    /// Runs one slide on the single axis the docked edge selects, from the transform's current
    /// value to <paramref name="to"/>.
    ///
    /// <para>
    /// The base value is written to the DESTINATION before the clock is attached, and the clock
    /// uses FillBehavior.Stop. That pairing is what makes the slide snap-free: Stop reverts the
    /// property to its base value when the clock detaches, and the base value is already where the
    /// slide ends. Writing the STARTING value as the base instead would snap the band back on the
    /// final tick. HoldEnd would avoid that snap and introduce a worse one — a held clock silently
    /// outranks every later direct assignment, so a subsequent write of 0 would be ignored and the
    /// toolbar would stay off-screen permanently with no visible cause.
    /// </para>
    /// </summary>
    private void StartSlide(double to, bool slidingOut, Action? onDone)
    {
        var axis = SlideAxisIsY ? TranslateTransform.YProperty : TranslateTransform.XProperty;

        // Read the current (animated, if a clock is live) value BEFORE cancelling: removing a clock
        // reverts the property to its base value, which would lose a reversal's starting point.
        var from = SlideAxisIsY ? SlideTransform.Y : SlideTransform.X;

        CancelSlide();

        // The cross-axis is never animated; zero it so a previous edge cannot leave the band
        // displaced along it.
        if (SlideAxisIsY) SlideTransform.X = 0; else SlideTransform.Y = 0;

        // Base value == destination. See this method's doc comment.
        if (SlideAxisIsY) SlideTransform.Y = to; else SlideTransform.X = to;

        _sliding = true;
        _slidingOut = slidingOut;
        var generation = ++_slideGeneration;

        void Complete()
        {
            _sliding = false;
            _slidingOut = false;
            onDone?.Invoke();
        }

        _pendingSlideCompletion = Complete;
        _pendingSlideGeneration = generation;

        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(SlideDurationMs))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase
            {
                // Enter decelerating, leave accelerating.
                EasingMode = slidingOut ? EasingMode.EaseIn : EasingMode.EaseOut,
            },
        };
        anim.Completed += (_, _) =>
        {
            if (generation != _slideGeneration) return;
            Complete();
        };

        SlideTransform.BeginAnimation(axis, anim);
    }

    /// <summary>
    /// Test seam: runs the pending slide's completion exactly as the animation's Completed handler
    /// would, generation check included. A window built in a test runs on an STA thread with no
    /// message pump, so the clock never advances and Completed never fires there. Mirrors
    /// <see cref="EndFlash"/>, which exists for the same reason.
    ///
    /// <para>
    /// Driving completion by hand is also the only way the generation guard is observable: the
    /// guard protects against a Completed the framework documents will not raise, so a test that
    /// waited for a real clock could never tell "the guard worked" from "nothing fired".
    /// </para>
    /// </summary>
    internal void CompleteSlideForTest()
    {
        if (_pendingSlideCompletion is null) return;
        if (_pendingSlideGeneration != _slideGeneration) return;
        _pendingSlideCompletion();
    }

    /// <summary>
    /// Window.Hide() guarded against the InvalidOperationException it is documented to throw on a
    /// window that is closing or has closed
    /// (learn.microsoft.com/dotnet/api/system.windows.window.hide). No boolean covers that case:
    /// IsVisible stays true through Closing and WPF exposes no public "is closing" flag, so
    /// catching the documented exception at the call is the only guard that matches the condition.
    /// Reachable only if a slide's completion outlives the window during shutdown, which is
    /// doubtful under ShutdownMode="OnExplicitShutdown" — hence a catch, not a lifecycle mechanism.
    /// </summary>
    private void HideSafely()
    {
        try { Hide(); }
        catch (InvalidOperationException) { }
    }
```

- [ ] **Step 6: Run the new tests**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideTests"
```

Expected: 12 passed. If `ForceShow_AfterACompletedSlideOut_ParksTheBandAtRest` or `SideDock_SlidesAlongX_NotY` fails because the transform reads `0` where a non-zero offset was expected, the window was never laid out and `SlideOffset()` returned a zero vector — add `window.UpdateLayout()` inside `NewDockedToolbar` after `Show()` and re-run. Report it if you make that change; it means `Show()` alone does not arrange on a pumpless STA thread, which is worth knowing for the rest of the plan.

- [ ] **Step 7: Run the whole toolbar suite for regressions**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Toolbar"
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: both green.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs tests/EyeCtrl.App.Tests/ToolbarSlideTests.cs
git commit -m "feat(toolbar): slide mechanics, two-flag state, and the completion seam"
```

---

### Task 4: The dwell-target placement guard

**Read the "Deviation from the spec" section at the top of this plan before starting.** It changes what you implement here.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (`UpdateTargetPositions`)
- Test: `tests/EyeCtrl.App.Tests/ToolbarSlideTests.cs` (add)

**Interfaces:**
- Consumes: `_sliding` and `SlideTransform` (Task 3).
- Produces: no new members. `UpdateTargetPositions` gains a precondition.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EyeCtrl.App.Tests/ToolbarSlideTests.cs`:

```csharp
    /// <summary>
    /// Placement publishes each button's centre through PointToScreen, which INCLUDES the render
    /// transform — and UpdateButtonScreenPosition creates a target on its first call, so one bad
    /// publish is permanent. This is the failure that shipped once and left no toolbar button
    /// dwellable at all, arrived at from a new direction.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void MidSlide_PublishesNoTargets()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            window.SlideOutThenHide(() => { });
            Check.That(window.IsSliding).IsTrue();

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The transform stays parked at the hidden offset for the WHOLE hidden period, not merely
    /// while a clock is live — so a placement while hidden would publish rest-plus-offset for every
    /// button and latch it. Note this state is safe in the pre-slide code (no transform exists);
    /// the transform is what makes it hazardous.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void WhileHiddenWithTheBandParkedOffEdge_PublishesNoTargets()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            Check.That(window.IsSliding).IsFalse();                  // not the guard under test
            Check.That(window.SlideTransform.Y).IsNotEqualTo(0d);    // the guard under test

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The slide-IN half of the guard, and the only state where the `_sliding` flag is the sole
    /// thing suppressing placement — which makes this the test that gives mutation row 2 its teeth.
    ///
    /// <para>
    /// Why this state and not the slide-out: on a pumpless STA thread an animation clock never
    /// ticks, so a property read after BeginAnimation returns the BASE value, and the base value is
    /// always written to the slide's destination. For a slide-out that destination is the off-edge
    /// offset, so the transform already reads non-zero and the at-rest check alone suppresses
    /// placement. For a slide-IN the destination is 0, so the transform reads at-rest while the
    /// band is in fact mid-flight, and only the flag is left. Measured, not reasoned: an earlier
    /// draft of this plan had this backwards and mutation row 2 could not fail.
    /// </para>
    ///
    /// <para>
    /// The spec requires placement suppression "in both directions"; without this test only the
    /// slide-out direction was covered.
    /// </para>
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void MidSlideIn_PublishesNoTargets()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            window.ShowThenSlideIn();

            Check.That(window.IsSliding).IsTrue();
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);   // base value == destination == rest

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>Once the band is back at rest, placement proceeds as before.</summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void AtRest_PublishesTargets()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            Check.That(window.FacesAreArranged()).IsTrue();

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsTrue();
        }
        finally { window.Close(); }
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideTests"
```

Expected: `MidSlide_PublishesNoTargets`, `MidSlideIn_PublishesNoTargets` and `WhileHiddenWithTheBandParkedOffEdge_PublishesNoTargets` all fail — `TargetsInitialized` is `true`, because nothing suppresses placement yet. `AtRest_PublishesTargets` should already pass.

- [ ] **Step 3: Add the guard**

In `UpdateTargetPositions`, insert immediately after the `DataContext` check and **before** the existing `FacesAreArranged()` guard:

```csharp
        // Not while the transform is displacing the faces. PointToScreen includes the render
        // transform, and UpdateButtonScreenPosition CREATES a target on its first call, so one bad
        // publish is permanent — the same permanence that made the collapsed-centre bug
        // unrecoverable short of a restart.
        //
        // The at-rest test covers more than the slide: the base value sits at the hidden offset for
        // the entire hidden period, not just while a clock is live. Deliberately phrased as "is the
        // transform at rest" rather than "is the window visible": the visibility form would make
        // ToolbarTargetPlacementTests.FreshBand_IsNotArranged_SoNothingIsPublished return HERE
        // instead of at the FacesAreArranged guard it exists to prove, silently turning that test
        // into a tautology.
        if (_sliding || SlideTransform.X != 0 || SlideTransform.Y != 0)
            return;
```

- [ ] **Step 4: Run the new tests**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideTests"
```

Expected: 16 passed (12 from Task 3, 4 added here).

- [ ] **Step 5: Confirm the existing placement suite still passes**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarTargetPlacementTests"
```

Expected: 3 passed.

**Do not try to prove that suite still has teeth by deleting the `FacesAreArranged` guard.** An earlier draft of this plan asked for exactly that and predicted a red test; it stays green. `UpdateTargetPositions` wraps each `PointToScreen` in `try { … } catch { return; }` (`ToolbarWindow.xaml.cs:277-287`), so an unarranged face's throw is swallowed and `_targetsInitialized` never latches — `FreshBand_IsNotArranged_SoNothingIsPublished` cannot tell which guard returned, or whether any did. Verified by running it. The new guard's teeth come from Task 7 rows 2 and 3 instead.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs tests/EyeCtrl.App.Tests/ToolbarSlideTests.cs
git commit -m "fix(toolbar): never publish dwell targets while the band is displaced"
```

---

### Task 5: Re-sequence the visibility adapter

**Files:**
- Modify: `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs`
- Modify: `tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs`

**Interfaces:**
- Consumes: `SlideOutThenHide`, `ShowThenSlideIn`, `ShowAtRest` (Task 3).
- Produces: no signature change. `IToolbarVisibility` is untouched, so `ToolbarAutoHideController` and its tests are unaffected.

- [ ] **Step 1: Rewrite the adapter's three methods**

In `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs`, replace `ShowToolbar`, `HideToolbar` and `ForceShow`:

```csharp
    public void ShowToolbar() => _dispatcher.BeginInvoke(() =>
    {
        // Indicator first, so it is not underneath the incoming band.
        _overlays.PauseIndicator.Hide();
        // Clock attached, then Show, then arm — arming is SYNCHRONOUS and deliberately not
        // deferred to the slide's completion: a summon already costs 400 ms of dwell, and the
        // targets are at their correct resting coordinates throughout, so up to 200 ms where a
        // button is drawn short of its target is the cheaper side of the trade. See the spec's
        // open question 1. SetActive follows Show for the same reason HideToolbar's precedes
        // Hide: the window reaches its safe configuration before dwell state changes.
        _overlays.Toolbar.ShowThenSlideIn();
        _toolbarVm.SetActive(true);
    });

    public void HideToolbar(bool paused) => _dispatcher.BeginInvoke(() =>
    {
        // Deactivate dwell BEFORE the band starts leaving. The slide widens the window between
        // this and the actual Hide() from one dispatcher tick to the slide duration, which makes
        // this ordering more load-bearing than it was, not less.
        _toolbarVm.SetActive(false);
        _overlays.Toolbar.SlideOutThenHide(() =>
        {
            // After the band has cleared, so the indicator reads as a handover rather than a swap.
            if (paused)
            {
                _overlays.PauseIndicator.Show();
            }
            else
            {
                _overlays.PauseIndicator.Hide();
            }
        });
    });

    public void ForceShow() => _dispatcher.BeginInvoke(() =>
    {
        // Instant, and it must stay instant: every caller — tray fallback, modal suspend-recover,
        // dock toggle — exists because the user needs the toolbar now. ShowAtRest also cancels any
        // in-flight slide and defuses its pending Hide().
        _overlays.Toolbar.ShowAtRest();
        _overlays.PauseIndicator.Hide();
        _toolbarVm.SetActive(true);
    });
```

- [ ] **Step 2: Run the adapter tests to see the expected breakage**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarVisibilityAdapterTests"
```

Expected: **all three still pass.** That is not a mistake in this step, and an earlier draft of this plan predicted a failure here and was wrong. `ToolbarVisibilityAdapterTests` constructs `new ToolbarWindow()` and never calls `SetMode` (`:57`), so `_slideEdge` is null, `SlideOffset()` returns a zero vector, and `SlideOutThenHide` short-circuits to a *synchronous* hide — the old assertions all still hold, because no slide ever runs.

That is the finding, not a pass: **the test as written cannot observe the new sequencing at all.** Step 3 both docks the toolbar (so a slide actually happens) and rewrites the assertions. Verified empirically — without the `SetMode` line, Step 3's rewritten test fails against a correct implementation, because `order` already holds both entries after one pump.

- [ ] **Step 3: Dock the test toolbar, then rewrite the hide-ordering test**

First, in `tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs`, inside `HideToolbar_CallsSetActiveFalse_BeforeToolbarHide`, add a `SetMode` call after the existing `Check.That(toolbar.IsVisible).IsTrue();`:

```csharp
            // Dock it: the slide only runs on a docked toolbar. Without this the window is
            // floating, SlideVectorFor returns a zero vector, and SlideOutThenHide short-circuits
            // to a synchronous hide — which would make every assertion below pass while observing
            // none of the sequencing this test exists for. Initialize() is not needed; a
            // SizeToContent window is measured enough after Show() for a real extent.
            toolbar.SetMode(isDocked: true, edge: "Top");
```

Then replace the body from `var order = new List<string>();` to the end of the `try` block:

```csharp
            // Record the ordered sequence of side effects. TargetProgress(id,0) fires synchronously
            // inside SetActive(false)'s ResetAllState; IsVisibleChanged fires synchronously inside
            // Window.Hide(). Capturing both into one list preserves their relative order — the
            // load-bearing assertion of this test.
            var order = new List<string>();
            vm.ProgressReset += () => order.Add("SetActive(false)");
            toolbar.IsVisibleChanged += (_, _) =>
            {
                if (!toolbar.IsVisible) order.Add("Hide");
            };

            adapter.HideToolbar(paused: false);
            PumpDispatcher();

            // The contract moved but did not weaken: dwell is deactivated when the band STARTS
            // leaving, and the window hides when the slide finishes. A test STA thread has no
            // message pump, so the animation clock never advances here — the slide's completion is
            // driven through the seam, exactly as ToolbarSlideTests does.
            Check.That(order).ContainsExactly("SetActive(false)");
            Check.That(toolbar.IsVisible).IsTrue();          // still on screen, mid-slide
            Check.That(vm.LastProgress).IsEqualTo(0.0);      // dwell already reset

            toolbar.CompleteSlideForTest();
            PumpDispatcher();

            Check.That(order).ContainsExactly("SetActive(false)", "Hide");
            Check.That(toolbar.IsVisible).IsFalse();
```

Also update the class's XML doc: replace the sentence "HideToolbar must deactivate dwell (SetActive(false)) BEFORE the toolbar window hides, so a gaze sample arriving between Hide and the next dispatcher tick can't accumulate against the now-hidden targets." with:

```csharp
/// <c>HideToolbar</c> must deactivate dwell (SetActive(false)) BEFORE the band starts sliding out,
/// and the window hides only when that slide completes — so no gaze sample can accumulate against
/// targets whose buttons are leaving or already gone. The slide widened that window from one
/// dispatcher tick to 200 ms, which is why the ordering matters more now, not less.
```

- [ ] **Step 4: Add the paused-branch test**

The pause indicator now shows from *inside* the slide's completion callback, which is new code on the one path whose whole job is telling a paused user the app is paused. Nothing else pins it. Add to `ToolbarVisibilityAdapterTests`:

```csharp
    /// <summary>
    /// The indicator is a handover, not a swap: it must appear only once the band has cleared.
    /// This is the only test covering HideToolbar's paused:true branch, and the show call now
    /// lives inside the slide's completion callback.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void HideToolbar_WhenPaused_ShowsTheIndicator_OnlyAfterTheSlideCompletes()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);

        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        try
        {
            toolbar.Show();
            PumpDispatcher();
            toolbar.SetMode(isDocked: true, edge: "Top");

            adapter.HideToolbar(paused: true);
            PumpDispatcher();

            Check.That(indicator.IsVisible).IsFalse();   // band still on its way out

            toolbar.CompleteSlideForTest();
            PumpDispatcher();

            Check.That(indicator.IsVisible).IsTrue();
        }
        finally
        {
            toolbar.Close();
            indicator.Close();
        }
    }
```

- [ ] **Step 5: Run the adapter tests**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarVisibilityAdapterTests"
```

Expected: 4 passed.

- [ ] **Step 6: Confirm the auto-hide controller suite is untouched**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarAutoHideControllerTests"
```

Expected: green, with no edits to that file. `IToolbarVisibility` did not change, which is what makes this true.

Note one spec Testing bullet deliberately left unimplemented: it asks for the already-hidden short-circuit to be driven from *both* controller call sites (`SetPaused` and `ReassertVisibility`). Both reach the same `IToolbarVisibility.HideToolbar`, and `ToolbarSlideTests.SlideOut_OnAnAlreadyHiddenToolbar_ShortCircuits` covers the behaviour at the window. Duplicating it per controller path would test the controller, which this work does not change. Waived knowingly, not overlooked.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs
git commit -m "feat(toolbar): sequence hide/show around the slide"
```

---

### Task 6: Point the re-arm gate at `IsOnScreen`

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs:349`
- Modify: `tests/EyeCtrl.App.Tests/TaskSwitcherDwellPositionTests.cs:166`

**Interfaces:**
- Consumes: `ToolbarWindow.IsOnScreen` (Task 3).
- Produces: nothing.

**Read the "On the re-arm gate's coverage" note under File Structure first.** The gate already has a source pin, this task breaks it, and updating that pin *is* the test work here — no new test file.

- [ ] **Step 1: See the existing pin fail before you touch it**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TaskSwitcherDwellPositionTests"
```

Expected: green (it pins the current `Toolbar.IsVisible`). Read `tests/EyeCtrl.App.Tests/TaskSwitcherDwellPositionTests.cs:141-167` now, so you know what you are about to break and why it exists: it was added after a 2026-08-18 field report where a top-row switcher tile dwell also fired the toolbar button beneath it.

- [ ] **Step 2: Swap the gate**

In `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs`, replace lines 345-350:

```csharp
                    Context.OverlayManager.TaskSwitcher.HideOverlay();
                    // Reactivate only if the toolbar is actually on screen: auto-hide may
                    // have hidden it while the switcher was up, and activating a hidden
                    // toolbar creates gaze targets for invisible buttons — the exact hazard
                    // SetActive's contract exists to prevent. IsOnScreen, not IsVisible:
                    // once Hide() is deferred to a slide's completion, IsVisible is still true
                    // during a slide-out, while the band is leaving. It is deliberately TRUE
                    // during a slide-IN, where the band is visible and staying and dwell is
                    // armed by design — and re-arming here is the only repair for that case,
                    // since the slide-in's completion no longer touches dwell state.
                    if (Context.OverlayManager.Toolbar.IsOnScreen)
                        Context.ToolbarVM.SetActive(true);
```

- [ ] **Step 3: Watch the existing pin go red**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TaskSwitcherDwellPositionTests"
```

Expected: `Switcher_DeactivatesToolbarDwell_WhileCoveringIt` fails on its second assertion ("Hiding the switcher must NOT unconditionally reactivate the toolbar…"), because the body no longer contains `Toolbar.IsVisible`. This is the breakage the plan predicts; do not proceed until you have seen it.

- [ ] **Step 4: Update and widen the pin**

In `tests/EyeCtrl.App.Tests/TaskSwitcherDwellPositionTests.cs`, replace the second `Check.WithCustomMessage(...)` block (`:161-166`) with:

```csharp
        Check.WithCustomMessage(
                "Hiding the switcher must NOT unconditionally reactivate the toolbar — "
                + "auto-hide may have hidden it while the switcher was up, and activating a "
                + "hidden toolbar creates gaze targets for invisible buttons. Guard the "
                + "SetActive(true) on Toolbar.IsOnScreen.")
            .That(body.Contains("Toolbar.IsOnScreen") && body.Contains("ToolbarVM.SetActive(true)")).IsTrue();
        Check.WithCustomMessage(
                "Toolbar.IsVisible is true during a slide-out, while the band is leaving and the "
                + "window is about to hide — re-arming dwell there is the very hazard this pin "
                + "exists to prevent. Use Toolbar.IsOnScreen, which is false then and true during "
                + "a slide-in.")
            .That(body.Contains("Toolbar.IsVisible")).IsFalse();
```

Also update the method's doc comment (`:141-146`) so the last sentence reads:

```csharp
    /// conditional on the toolbar actually being on screen — Toolbar.IsOnScreen, not IsVisible,
    /// which stays true through a slide-out (2026-09-06 slide transition).
```

- [ ] **Step 5: Run the pin and the whole headless subset**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TaskSwitcherDwellPositionTests"
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: the pin green, then the entire headless subset green. That second command is the one that matters: it is CI's own filter, and this task is the only one in the plan that touches a file CI's subset covers.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs tests/EyeCtrl.App.Tests/TaskSwitcherDwellPositionTests.cs
git commit -m "fix(toolbar): re-arm dwell on IsOnScreen, not IsVisible"
```

---

### Task 7: Behavioural harness for the task-switcher re-arm gate

This closes the polarity gap: a source pin cannot tell `IsOnScreen` from `!IsOnScreen`. Two behavioural tests can.

**On cost, since this plan has now been wrong about it three times.** It first called the harness prohibitive, naming a `GlassBackdropWindow` construction that does not happen — `SetupBackdrop` only assigns a field. Plan review corrected that to ~80-120 lines. Both missed that `tests/EyeCtrl.App.Tests/Services/TestFakes.cs:22-52` **already builds an `EventCoordinationContext`**, with a `record with` pattern for overriding individual members, and that all eight view-models `OnInitialize` subscribes to take a plain `(EyeCtrlSettings, IMessenger)` shape. The real cost is roughly 60 lines. The lesson worth keeping: three successive estimates of "too expensive to test" were each refuted by one grep.

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs` (Step 3 only, and only if needed)
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/TaskSwitcherReArmTests.cs` (create)

**Interfaces:**
- Consumes: `ToolbarWindow.IsOnScreen`, `SlideOutThenHide`, `ShowThenSlideIn`, `CompleteSlideForTest` (Task 3); `TestFakes.MakeContext` and `ExposedDwellToolbarViewModel` (both existing).
- Produces: nothing new in production code, unless Step 3 applies.

- [ ] **Step 1: Write the tests**

Create `tests/EyeCtrl.App.Tests/Services/Coordinators/TaskSwitcherReArmTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The task-switcher close handler's dwell re-arm gate, driven behaviourally.
///
/// <para>
/// <c>TaskSwitcherDwellPositionTests</c> pins the gate's SYMBOL and <c>ToolbarSlideTests</c> pins
/// <c>IsOnScreen</c>'s truth table, and between them one thing stays invisible: polarity.
/// <c>if (!Toolbar.IsOnScreen)</c> satisfies both while doing the exact opposite — re-arming dwell
/// for a departing band and refusing to re-arm an arriving one. These two tests are the only thing
/// that catches that, which is why they are worth a harness.
/// </para>
///
/// <para>
/// Dwell state is observed the way <c>ToolbarVisibilityAdapterTests</c> observes it — through the
/// real dwell machinery, since <c>ToolbarViewModel.SetActive</c> is non-virtual — via
/// <c>ExposedDwellToolbarViewModel</c>, which that file already declares at namespace scope.
/// </para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("WpfApplication")]
public class TaskSwitcherReArmTests
{
    [StaFact]
    [Trait("Category", "UI")]
    public void SwitcherClosing_DuringSlideIn_ReArmsToolbarDwell()
    {
        using var h = new Harness();

        // The band is arriving: visible, staying, dwell armed by design.
        h.Toolbar.SlideOutThenHide(() => { });
        h.Toolbar.CompleteSlideForTest();
        h.Toolbar.ShowThenSlideIn();
        Check.That(h.Toolbar.IsOnScreen).IsTrue();

        // The switcher disarmed dwell when it opened; closing it must repair that. Nothing else
        // will — the slide-in's completion no longer touches dwell state.
        h.ToolbarVm.SetActive(false);
        h.CloseSwitcher();

        Check.That(h.DwellAccumulates()).IsTrue();
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void SwitcherClosing_DuringSlideOut_DoesNotReArmToolbarDwell()
    {
        using var h = new Harness();

        // The band is leaving and the window is about to hide.
        h.Toolbar.SlideOutThenHide(() => { });
        Check.That(h.Toolbar.IsOnScreen).IsFalse();

        h.ToolbarVm.SetActive(false);
        h.CloseSwitcher();

        Check.That(h.DwellAccumulates()).IsFalse();
    }

    /// <summary>
    /// A docked, shown toolbar plus a coordinator initialised over a real context. The switcher VM
    /// is set visible BEFORE Initialize so no show-branch ever runs — this harness only exercises
    /// the close branch, which is the one carrying the gate.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly OverlayVmBindingCoordinator _coordinator;
        private readonly TaskSwitcherViewModel _switcherVm;
        private readonly TaskSwitcherWindow _switcherWindow;

        internal ToolbarWindow Toolbar { get; }
        internal ExposedDwellToolbarViewModel ToolbarVm { get; }

        internal Harness()
        {
            WpfApplicationGate.EnsureApplicationWithTokens();

            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();

            ToolbarVm = new ExposedDwellToolbarViewModel(settings, messenger);
            Toolbar = new ToolbarWindow();
            Toolbar.Initialize(ToolbarTestHelpers.ButtonSize, settings.Dwell.ToolbarButtonDwellMs);
            Toolbar.DataContext = ToolbarVm;
            Toolbar.BindToViewModel();
            Toolbar.Show();
            Toolbar.SetMode(isDocked: true, edge: "Top");

            _switcherWindow = new TaskSwitcherWindow();
            _switcherVm = new TaskSwitcherViewModel(windowManager: null!, settings, messenger);

            // Visible before Initialize: the coordinator subscribes inside Initialize, so this
            // assignment raises nothing and the show branch never runs.
            _switcherVm.IsVisible = true;

            _coordinator = new OverlayVmBindingCoordinator(new ModalInteractionState());
            _coordinator.Initialize(TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                CursorVM = new CursorCircleViewModel(settings, messenger),
                ActivationVM = new ActivationButtonViewModel(settings, messenger),
                ManualSelectionVM = new ManualSelectionOverlayViewModel(settings, messenger),
                ManualSelectionIndicatorVM = new ManualSelectionGazeIndicatorViewModel(messenger),
                ActionMenuVM = new ActionMenuViewModel(settings, messenger),
                BackdropVM = new GlassBackdropViewModel(settings, messenger),
                ToolbarVM = ToolbarVm,
                TaskSwitcherVM = _switcherVm,
                OverlayManager = new FakeOverlays(Toolbar, _switcherWindow),
                Dispatcher = Dispatcher.CurrentDispatcher,
                Settings = settings,
            });

            ToolbarVm.DwellDetector.AddTarget(new DwellTarget(
                "Close", new PhysicalPoint(100, 100), radius: 50, dwellTimeMs: 1000));
        }

        /// <summary>Flips the switcher to hidden and drains the handler's BeginInvoke.</summary>
        internal void CloseSwitcher()
        {
            _switcherVm.IsVisible = false;
            Pump();
        }

        /// <summary>
        /// Whether toolbar dwell is live: two gaze updates on a positioned target — the first
        /// records entry and emits nothing, the second accumulates. Routed through Receive, the
        /// view-model's own dwell gate, so progress reaching the target proves SetActive(true) ran.
        /// </summary>
        internal bool DwellAccumulates()
        {
            ToolbarVm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 0));
            ToolbarVm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 500_000));
            return ToolbarVm.LastProgress > 0;
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new DispatcherOperationCallback(_ => { frame.Continue = false; return null; }),
                null);
            Dispatcher.PushFrame(frame);
        }

        public void Dispose()
        {
            _coordinator.Dispose();
            _switcherVm.Dispose();
            _switcherWindow.Close();
            Toolbar.Close();
        }
    }

    /// <summary>
    /// Only the three members the close branch touches. GlassBackdrop returns null deliberately:
    /// SetupBackdrop merely assigns it to a field and every consumer null-guards, so no backdrop
    /// window need exist. Every other member throws, so an accidental reach fails loudly rather
    /// than returning null.
    /// </summary>
    private sealed class FakeOverlays : IOverlayManager
    {
        private readonly ToolbarWindow _toolbar;
        private readonly TaskSwitcherWindow _switcher;

        internal FakeOverlays(ToolbarWindow toolbar, TaskSwitcherWindow switcher)
        {
            _toolbar = toolbar;
            _switcher = switcher;
        }

        public ToolbarWindow Toolbar => _toolbar;
        public TaskSwitcherWindow TaskSwitcher => _switcher;
        public GlassBackdropWindow GlassBackdrop => null!;

        public PauseIndicatorWindow PauseIndicator => throw new NotImplementedException();
        public CursorCircleWindow CursorCircle => throw new NotImplementedException();
        public ActivationButtonWindow ActivationButton => throw new NotImplementedException();
        public ManualSelectionOverlayWindow ManualSelectionOverlay => throw new NotImplementedException();
        public ActionMenuWindow ActionMenu => throw new NotImplementedException();
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public ManualSelectionGazeIndicatorWindow ManualSelectionIndicator => throw new NotImplementedException();
        public CalibrationWindow Calibration => throw new NotImplementedException();
        public TargetHighlightWindow TargetHighlight => throw new NotImplementedException();
        public EdgeButtonBarWindow EdgeButtonBar => throw new NotImplementedException();
        public TrackStatusWindow TrackStatus => throw new NotImplementedException();
        public QuickActionsGridWindow QuickActionsGrid => throw new NotImplementedException();

        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings,
            EyeCtrl.Core.Enums.GazeProviderType providerType) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2: Build and run**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TaskSwitcherReArmTests"
```

Get it compiling first. If `IOverlayManager` carries members this fake omits, the compiler names them — add each as a throwing property, and do not silently stub one to return null.

Once it compiles, both tests should **pass immediately**: the gate is already correct after Task 6. That is expected and is not a TDD violation — these tests exist to catch a *polarity* regression, and Step 4 is where they earn their place. If either fails here, the gate's polarity is already wrong, and that is a genuine finding to report rather than patch.

- [ ] **Step 3: Only if Step 2's tests cannot observe the gate**

If the handler's `Context.Dispatcher.BeginInvoke` does not drain under `Pump()`, extract the lambda body so the test can drive it directly. In `OverlayVmBindingCoordinator`, replace the `BeginInvoke` lambda with a call to a new internal method:

```csharp
    /// <summary>
    /// Applies a task-switcher visibility change. internal, not private: EyeCtrl.App.Tests drives
    /// this directly (TaskSwitcherReArmTests), mirroring
    /// ToolbarLifecycleCoordinator.InjectAutoHideForTesting rather than reaching in by reflection.
    /// </summary>
    internal void ApplyTaskSwitcherVisibility(bool visible)
    {
        if (Context is null) return;
        if (visible)
        {
            Context.ToolbarVM.SetActive(false);
            Context.OverlayManager.TaskSwitcher.ShowFullScreen();
        }
        else
        {
            Context.OverlayManager.TaskSwitcher.HideOverlay();
            if (Context.OverlayManager.Toolbar.IsOnScreen)
                Context.ToolbarVM.SetActive(true);
        }
    }
```

Then have `Harness.CloseSwitcher` call `_coordinator.ApplyTaskSwitcherVisibility(false)` instead of setting the VM property. **Skip this step entirely if Step 2 already works** — `Pump()` exists precisely so it should, and the property-driven version tests one more link of the real chain. Record which way it went.

- [ ] **Step 4: Prove the teeth — the polarity mutation**

Invert the gate in `OverlayVmBindingCoordinator.cs`: `if (!Context.OverlayManager.Toolbar.IsOnScreen)`. Run both:

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TaskSwitcherReArmTests"
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TaskSwitcherDwellPositionTests"
```

Expected: **both** `TaskSwitcherReArmTests` red, and `TaskSwitcherDwellPositionTests` still **green**. That contrast is the whole justification for this task — the source pin cannot see an inversion and these tests can. If the pin also goes red, say so; it would mean the pin is stronger than this plan credits.

Restore: `git checkout src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs`

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs tests/EyeCtrl.App.Tests/Services/Coordinators/TaskSwitcherReArmTests.cs
git commit -m "test(toolbar): behavioural cover for the task-switcher re-arm gate"
```

---

### Task 8: Mutation sweep and full verification

Read-only review does not substitute for this. Five rounds of read-only review on this codebase once missed three one-line edits that broke a feature with the suite green. **Break it and watch it fail — never reason that it would.**

For each row: make the edit, run the named test, confirm it goes **red**, then `git checkout` the file to restore it. Record the observed result per row; if any row stays green, that is a finding — stop and report it rather than moving on.

**Files:**
- Modify (temporarily, reverted each time): the files named per row
- Modify: `docs/superpowers/specs/2026-09-06-toolbar-slide-transition-design.md` (Step 12 only)

- [ ] **Step 1: Row 1 — flip a sign in `SlideVectorFor`**

Change `"top" => new Vector(0, -height)` to `new Vector(0, height)` in `OverlayPositioning.cs`. Run:
`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideGeometryTests"`
Expected: `TopDock_RetractsUpward_ByTheFullHeight` red. Restore: `git checkout src/EyeCtrl.App/Overlays/OverlayPositioning.cs`

- [ ] **Step 2: Row 2 — drop `_sliding` from the placement guard**

In `UpdateTargetPositions`, change the guard to `if (SlideTransform.X != 0 || SlideTransform.Y != 0)`. Run:
`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideTests"`
Expected: **`MidSlideIn_PublishesNoTargets` red**, and `MidSlide_PublishesNoTargets` still **green**.

Both halves of that expectation are load-bearing, and an earlier draft of this plan had them backwards — it pointed this row at `MidSlide_PublishesNoTargets` and asserted "at the start of a slide-out the animated value is still 0". It is not. On a pumpless STA thread the clock never ticks, so a read after `BeginAnimation` returns the **base** value, and `StartSlide` writes the base to the slide's *destination*: off-edge for a slide-out (non-zero, so the surviving transform check catches it) and `0` for a slide-in (at-rest, so only the flag is left). Measured with a probe, then confirmed by running the mutation — with the old mapping this row could not fail. Restore the file.

- [ ] **Step 3: Row 3 — drop the transform-at-rest check**

Change the guard to `if (_sliding)`. Run the same filter.
Expected: `WhileHiddenWithTheBandParkedOffEdge_PublishesNoTargets` red. Restore.

- [ ] **Step 4: Row 4 — defer arming to the slide-in's completion**

Delete the `_toolbarVm.SetActive(true)` line from `ToolbarVisibilityAdapter.ShowToolbar`. Run:
`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarVisibilityAdapterTests"`
Expected: `ShowToolbar_CallsSetActiveTrue` red.

Deletion rather than a faithful move, and the reason is worth understanding rather than working around: the mutation the spec names is "defer arming to the slide's completion", but on a pumpless STA thread no clock ever completes, so deferring and deleting are *observationally identical* — both leave dwell disarmed after `ShowToolbar` returns. Deleting one line gets the same red for a fraction of the edit. Restore: `git checkout src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs`

- [ ] **Step 5: Row 5 — define `IsOnScreen` as `IsVisible && !IsSliding`**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideTests"`
Expected: `DuringSlideIn_IsOnScreen_IsTrue` red. This is the stranding guard. Restore.

- [ ] **Step 6: Row 6 — revert `IsOnScreen` to plain `IsVisible`**

Run the same filter.
Expected: `DuringSlideOut_IsNotOnScreen` red. Rows 5 and 6 are a matched pair: they mutate one expression in opposite directions, and passing both is what proves the gate is *directional* rather than merely non-constant. Restore.

- [ ] **Step 7: Row 7 — stop clearing the flags in `CancelSlide`**

Delete `_sliding = false; _slidingOut = false;` from `CancelSlide`. Run:
`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideTests"`
Expected: `ForceShow_MidSlideOut_DefusesThePendingHide` red on the flags assertions. Note that the show/transform/no-hide assertions in that same test still pass — which is exactly why the flags assertions had to be added. Restore.

- [ ] **Step 8: Row 8 — delete the base-zero write in `ShowAtRest`**

Delete `SlideTransform.X = 0; SlideTransform.Y = 0;` from `ShowAtRest`. Run the same filter.
Expected: `ForceShow_AfterACompletedSlideOut_ParksTheBandAtRest` red. Restore.

- [ ] **Step 9: Row 9 — remove the generation guard**

In `StartSlide`, change `anim.Completed` to call `Complete()` unconditionally **and** delete the `_pendingSlideGeneration != _slideGeneration` check in `CompleteSlideForTest`. Run the same filter.
Expected: `ForceShow_MidSlideOut_DefusesThePendingHide` red — the superseded completion now hides a visible toolbar. Restore.

- [ ] **Step 10: Row 10 — swap `FillBehavior.Stop` for `HoldEnd` *and* write the starting value as the base**

In `StartSlide`, set `FillBehavior = FillBehavior.HoldEnd` and change the base-value write from `to` to `from`. Run the full slide filter.
Expected: at least one test red (a second slide, or `ForceShow_AfterACompletedSlideOut_ParksTheBandAtRest`). **Note:** `HoldEnd` *alone*, with the destination still written as the base, is a behavioural no-op and would stay green — that is why this row mutates both, and why the spec deleted the single-mutation version. Restore.

- [ ] **Step 11: Full verification**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Toolbar"
git status --porcelain
```

Expected: all green, and `git status` clean (every mutation reverted). Measured baselines from a trial run of Tasks 1-6: headless subset **788/788**, `FullyQualifiedName~Toolbar` **131 passed / 5 skipped**, `ToolbarSlideTests` **16**, `ToolbarSlideGeometryTests` **8**. Task 7 adds 2 more (`TaskSwitcherReArmTests`) and Task 5 Step 4 adds 1 adapter test, neither of which was in that trial — so expect `~Toolbar` at **134 passed / 5 skipped**. Treat any other discrepancy as a finding, not noise. Remember the German output: read the `Fehlgeschlagen!`/`Erfolgreich!` summary and grep `Fehler ` with a trailing space, never `Failed`. Three tests are known load-dependent flakes unrelated to this work — `HeartbeatPublisher` (<1500 ms), `MouseGazeProviderTests`, and the whole `ExternalDwellWireContractTests` family; one failure there re-runs green.

- [ ] **Step 12: Sync the spec to what was actually built**

In `docs/superpowers/specs/2026-09-06-toolbar-slide-transition-design.md`:

1. Change the placement-guard predicate from `IsVisible && !IsSliding` to the transform-at-rest form, justified by **precision** (the transform's value is the hazard; `IsVisible` was a proxy that also mishandles a visible-but-displaced band). Do **not** cite `ToolbarTargetPlacementTests.cs:32-44` — that argument was refuted; see the deviation section at the top of this plan.
2. Update mutation rows 2-3 to the corrected mapping from Step 2 above, including why the slide-*in* is the state that isolates the flag.
3. Correct the Testing section's claim that the re-arm gate "has *zero* coverage today". It has a pre-existing source pin at `TaskSwitcherDwellPositionTests.cs:148-167`. That claim came from spec review round 1 and was accepted without checking for coverage outside the file the reviewer cited.
4. Correct the harness-cost figure and its outcome: `SetupBackdrop` assigns a field and does not construct a `GlassBackdropWindow`, `TestFakes.MakeContext` already builds a context, and the harness came in at ~60 lines — so it was **built** (Task 7), not declined. Replace the "declined on proportionality" reasoning with the three-layer coverage argument (truth table / symbol pin / polarity), and delete the polarity gap from the residual-risk list, since Task 7 closes it.

- [ ] **Step 13: Commit**

```bash
git add docs/superpowers/specs/2026-09-06-toolbar-slide-transition-design.md
git commit -m "docs(spec): sync the placement guard to the implemented predicate"
```

---

## What this plan does not do

- **No visual check.** Nothing here confirms the slide *looks* right; the tests pin direction, state, and sequencing, not appearance. A visual check needs the app running, which needs the user's explicit go-ahead.
- **No settings surface**, per the spec's non-goals.
- **No slide for the pause indicator** (spec open question 2, deferred).
- **The `IsOnScreen` truth table is four tests, not one.** The spec asks for "four assertions in one test so the directional asymmetry cannot be half-broken". Four separate tests were verified to catch both halves independently (mutation rows 5 and 6 each redden their own test), so the split is safe — but it is a deviation from the shape the spec specifies, recorded here rather than left as an accident.
