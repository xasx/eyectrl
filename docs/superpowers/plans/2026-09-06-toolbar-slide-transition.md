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

The spec specifies the dwell-target placement guard as `IsVisible && !IsSliding` (spec §"What must not run mid-slide", mutation row 3). **This plan implements `!_sliding && the transform is at its resting value` instead**, for a reason found while checking test impact:

`ToolbarTargetPlacementTests.FreshBand_IsNotArranged_SoNothingIsPublished` (`tests/EyeCtrl.App.Tests/ToolbarTargetPlacementTests.cs:32-44`) never calls `Show()`, so `IsVisible` is false there. An `IsVisible` conjunct would make that test pass by returning at the *new* guard, never reaching the `FacesAreArranged()` ordering it exists to prove — and its own doc comment (`:22-26`) states that reaching the end of `UpdateTargetPositions` without throwing *is* the evidence. The conjunct would silently turn a real guard test into a tautology.

The transform-at-rest form is also the more precise predicate: the hazard is "the transform is displacing the faces from where their targets sit", and `IsVisible` was a proxy for it. It covers everything `IsVisible` covered — the transform sits at the hidden offset for the whole hidden period, which is what round 2 established — without the test collateral.

**Consequence:** the spec's mutation row 3 ("Drop the `IsVisible` conjunct") becomes "Drop the transform-at-rest check". Task 7 uses the corrected wording. The spec needs a one-line sync afterwards; that is listed as Task 7 Step 12 rather than done silently.

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
| `tests/EyeCtrl.App.Tests/ToolbarSlideGateGuardTests.cs` (create) | Headless source ratchet pinning the re-arm gate's call site. |
| `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs` (modify, line 603) | `(Border)window.Content` → `window.StripRoot`, forced by the wrapper. |
| `tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs` (modify) | Hide-ordering test rewritten to the new contract. |

**On the ratchet file:** the spec's Testing section calls for a task-switcher-closes-mid-slide-in test in `OverlayVmBindingCoordinatorTests`. That coordinator's `OnInitialize` requires a full `EventCoordinationContext` (ten view-models, an `IOverlayManager` whose unimplemented members throw, a dispatcher, a gaze engine) and also runs `SetupBackdrop()`, which constructs a `GlassBackdropWindow`. That is a large harness for a one-line gate. This plan instead puts the *logic* coverage on `ToolbarWindow.IsOnScreen` (a full four-state truth table, Task 3) and pins the *call site* with a source ratchet in the idiom the repo already uses (`SourceScanner`, `RawGeometryLiteralTests`). Together those fail on either half of the mutation. If you would rather pay for the real harness, say so before Task 6.

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

Expected: `MidSlide_PublishesNoTargets` and `WhileHiddenWithTheBandParkedOffEdge_PublishesNoTargets` fail — `TargetsInitialized` is `true`, because nothing suppresses placement yet. `AtRest_PublishesTargets` should already pass.

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

Expected: 13 passed.

- [ ] **Step 5: Verify the existing placement guard kept its teeth**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarTargetPlacementTests"
```

Expected: 3 passed. This is the check the deviation exists for: `FreshBand_IsNotArranged_SoNothingIsPublished` must still reach `FacesAreArranged()`. Confirm by temporarily commenting out the `FacesAreArranged` guard and re-running — that test must fail (it will throw from `PointToScreen`). Restore the guard afterwards.

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

Expected: `HideToolbar_CallsSetActiveFalse_BeforeToolbarHide` fails — it asserts `order.Count == 2` and `toolbar.IsVisible == false` after one pump, and `Hide` now lands on the slide's completion. `ShowToolbar_CallsSetActiveTrue` and `ForceShow_CallsSetActiveTrue` must **pass unchanged**; if either fails, stop and report, because this plan predicts they survive.

- [ ] **Step 3: Rewrite the hide-ordering test to the new contract**

In `tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs`, replace the body of `HideToolbar_CallsSetActiveFalse_BeforeToolbarHide` from `var order = new List<string>();` to the end of the `try` block:

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

- [ ] **Step 4: Run the adapter tests**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarVisibilityAdapterTests"
```

Expected: 3 passed.

- [ ] **Step 5: Confirm the auto-hide controller suite is untouched**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarAutoHideControllerTests"
```

Expected: green, with no edits to that file. `IToolbarVisibility` did not change, which is what makes this true.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs
git commit -m "feat(toolbar): sequence hide/show around the slide"
```

---

### Task 6: Point the re-arm gate at `IsOnScreen`

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs:349`
- Test: `tests/EyeCtrl.App.Tests/ToolbarSlideGateGuardTests.cs` (create)

**Interfaces:**
- Consumes: `ToolbarWindow.IsOnScreen` (Task 3).
- Produces: nothing.

- [ ] **Step 1: Write the failing ratchet test**

Create `tests/EyeCtrl.App.Tests/ToolbarSlideGateGuardTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using System.Linq;

/// <summary>
/// Pins the call site of the toolbar's dwell re-arm gate.
///
/// <para>
/// The logic lives on <c>ToolbarWindow.IsOnScreen</c> and is covered by a four-state truth table in
/// <see cref="ToolbarSlideTests"/>. What that cannot cover is the one-line question of whether the
/// re-arm site actually READS it: <c>Toolbar.IsVisible</c> compiles, reads plausibly, and is wrong
/// once Hide() is deferred to a slide's completion — during a slide-out it is true while the band is
/// leaving. Re-arming dwell there creates gaze targets for buttons on their way off screen.
/// </para>
///
/// <para>
/// A source ratchet rather than a behavioural test, deliberately. Reaching that branch for real
/// needs a full EventCoordinationContext — ten view-models, an IOverlayManager whose unimplemented
/// members throw, a dispatcher, a gaze engine — and OnInitialize also constructs a
/// GlassBackdropWindow. That is a large harness for a one-line gate, and the repo already polices
/// this class of regression by scanning source (RawGeometryLiteralTests, RawColourLiteralTests).
/// Headless, so CI runs it.
/// </para>
/// </summary>
public class ToolbarSlideGateGuardTests
{
    private const string GatePath =
        "src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs";

    private static string GateSource() =>
        SourceScanner.StripComments(
            File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
                GatePath.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public void ReArmGate_ReadsIsOnScreen()
    {
        Check.WithCustomMessage(
                $"{GatePath} must gate toolbar dwell re-arming on Toolbar.IsOnScreen.")
            .That(GateSource().Contains("Toolbar.IsOnScreen", StringComparison.Ordinal))
            .IsTrue();
    }

    [Fact]
    public void ReArmGate_NeverReadsIsVisible()
    {
        // Comments are stripped, so a comment explaining what IsVisible USED to do cannot fail this.
        Check.WithCustomMessage(
                $"{GatePath} reads Toolbar.IsVisible. That is true during a slide-out, while the "
                + "band is leaving and the window is about to hide — re-arming dwell there creates "
                + "gaze targets for invisible buttons. Use Toolbar.IsOnScreen.")
            .That(GateSource().Contains("Toolbar.IsVisible", StringComparison.Ordinal))
            .IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideGateGuardTests"
```

Expected: `ReArmGate_ReadsIsOnScreen` fails, `ReArmGate_NeverReadsIsVisible` fails. Both, because the swap has not happened.

- [ ] **Step 3: Swap the gate**

In `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs`, replace lines 345-350's condition:

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

- [ ] **Step 4: Run the ratchet and the headless subset**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarSlideGateGuardTests"
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: 2 passed, then the whole headless subset green.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs tests/EyeCtrl.App.Tests/ToolbarSlideGateGuardTests.cs
git commit -m "fix(toolbar): re-arm dwell on IsOnScreen, not IsVisible"
```

---

### Task 7: Mutation sweep and full verification

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
Expected: `MidSlide_PublishesNoTargets` red — at the start of a slide-out the animated value is still 0, so only the flag catches it. Restore the file.

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

Expected: all green, and `git status` clean (every mutation reverted). Remember the German output: read the `Fehlgeschlagen!`/`Erfolgreich!` summary and grep `Fehler ` with a trailing space, never `Failed`. Three tests are known load-dependent flakes unrelated to this work — `HeartbeatPublisher` (<1500 ms), `MouseGazeProviderTests`, and the whole `ExternalDwellWireContractTests` family; one failure there re-runs green.

- [ ] **Step 12: Sync the spec's mutation table to the implemented guard**

In `docs/superpowers/specs/2026-09-06-toolbar-slide-transition-design.md`, update the placement-guard predicate from `IsVisible && !IsSliding` to the transform-at-rest form, and mutation rows 2-3 to match, citing `ToolbarTargetPlacementTests.cs:32-44` as the reason. Do not silently leave the spec and the code disagreeing.

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
- **No behavioural test of the task-switcher branch** — see the note under File Structure and raise it before Task 6 if you want the real harness instead.
