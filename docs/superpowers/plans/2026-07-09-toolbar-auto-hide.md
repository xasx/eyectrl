# Toolbar Auto-Hide on Gaze — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto-hide the docked toolbar when gaze isn't on it, and summon it back by looking past the docked screen edge — only in eye-tracker mode, with a thin edge-bar pause indicator and a tray fallback.

**Architecture:** A new `ToolbarAutoHideController` in the App layer subscribes to the raw `GazeEngine.GazeSampled` stream, runs a two-state hysteresis machine on the gaze thread against cached geometry, and dispatches `Toolbar.Hide()/Show()` (plus a new `PauseIndicatorWindow` edge bar) to the UI thread only on state transitions. Mode gating comes from a new `App.ActiveProviderType` static. A tray "Show toolbar" item is the hardware-independent recovery path.

**Tech Stack:** C# 13 / .NET 10, WPF (`net10.0-windows`), CommunityToolkit.Mvvm, xUnit 2.9 + NFluent 3.1.0, Serilog.

**Spec:** `docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md` (Validated — the off-screen-valid summon assumption was verified on the PCEye 5 via `samples/OffScreenGazeProbe`).

**Branch:** `feature/toolbar-auto-hide` (already created; spec + probe committed).

---

## File Structure

**Core (`EyeCtrl.Core`):**
- `src/EyeCtrl.Core/Models/PhysicalRect.cs` — **modify**: add `Contains(double x, double y)` hit-test.
- `tests/EyeCtrl.Core.Tests/Models/PhysicalRectContainsTests.cs` — **create**: unit tests for the new method.

**App (`EyeCtrl.App`):**
- `src/EyeCtrl.App/App.xaml.cs` — **modify**: add `App.ActiveProviderType` static property; set it in `OnStartup`; construct `TrayIconManager` with the controller's force-show callback.
- `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs` — **create**: the state machine + gaze subscription (the heart of the feature).
- `src/EyeCtrl.App/Services/IToolbarVisibility.cs` — **create**: the injected seam (`Show()`/`Hide()` for toolbar + edge bar) the controller depends on; implemented by a real adapter and a test fake.
- `src/EyeCtrl.App/Services/EventCoordinator.cs` — **modify**: construct + wire the controller in `Initialize`; suspend/resume it on keyboard/settings/calibration transitions; refresh on settings change; dispose it.
- `src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml(.cs)` — **create**: the thin edge-bar overlay (single colored rectangle, click-through, docked-edge positioning).
- `src/EyeCtrl.App/Overlays/OverlayManager.cs` + `IOverlayManager.cs` — **modify**: construct + expose `PauseIndicator`.
- `src/EyeCtrl.App/Services/TrayIconManager.cs` — **modify**: add the "Show toolbar" menu item (conditionally visible).

**App tests (`EyeCtrl.App.Tests` — if present; else a new test class alongside):**
- `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs` — **create**: state-machine unit tests with fakes.

**Sample cleanup (final task):**
- `samples/OffScreenGazeProbe/` — **delete** after the feature is validated end-to-end (probe was only needed to verify the assumption, now done).

---

## Conventions (from AGENTS.md — follow exactly)

- File-scoped namespaces. Allman braces. `_camelCase` fields. Nullable enabled.
- Tests: xUnit + **NFluent 3.1.0** (`Check.That(x).IsEqualTo(y)`, etc.). No mocking library — private nested fake classes. Test classes named `XxxTests`, methods `Method_Condition_Effect`. Namespaces mirror source.
- Floating point: `Check.That(actual).IsCloseTo(expected, 1e-N)` uses **absolute delta**, not decimal places.
- No `#region` blocks.
- Commit only what each task touches. Don't run the formatter (none configured).

---

## Task 1: Add `PhysicalRect.Contains` hit-test (Core)

This is the `isOnToolbar` primitive the controller depends on. It's pure and trivially testable, so it goes first — the controller (Task 4) and its tests (Task 5) build on it.

**Files:**
- Modify: `src/EyeCtrl.Core/Models/PhysicalRect.cs`
- Test: `tests/EyeCtrl.Core.Tests/Models/PhysicalRectContainsTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Models/PhysicalRectContainsTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using NFluent;

public class PhysicalRectContainsTests
{
    // Rect at (100,200) size 50x40 → spans x[100,150], y[200,240]
    private static readonly PhysicalRect Rect = new(100, 200, 50, 40);

    [Fact]
    public void Contains_InteriorPoint_True()
    {
        Check.That(Rect.Contains(125, 220)).IsTrue();
    }

    [Fact]
    public void Contains_TopLeftCorner_True_Inclusive()
    {
        Check.That(Rect.Contains(100, 200)).IsTrue();
    }

    [Fact]
    public void Contains_BottomRightCorner_True_Inclusive()
    {
        Check.That(Rect.Contains(150, 240)).IsTrue();
    }

    [Fact]
    public void Contains_PointLeftOfRect_False()
    {
        Check.That(Rect.Contains(99.9, 220)).IsFalse();
    }

    [Fact]
    public void Contains_PointAboveRect_False()
    {
        Check.That(Rect.Contains(125, 199.9)).IsFalse();
    }

    [Fact]
    public void Contains_PointRightOfRect_False()
    {
        Check.That(Rect.Contains(150.1, 220)).IsFalse();
    }

    [Fact]
    public void Contains_PointBelowRect_False()
    {
        Check.That(Rect.Contains(125, 240.1)).IsFalse();
    }

    [Fact]
    public void Contains_ZeroSizedRect_ContainsOnlyItsOrigin()
    {
        var zero = new PhysicalRect(10, 10, 0, 0);
        Check.That(zero.Contains(10, 10)).IsTrue();
        Check.That(zero.Contains(10.1, 10)).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PhysicalRectContainsTests"`
Expected: FAIL with compile error `'PhysicalRect' does not contain a definition for 'Contains'`.

- [ ] **Step 3: Write minimal implementation**

Modify `src/EyeCtrl.Core/Models/PhysicalRect.cs` — add the method inside the record struct body (after the `Center` property):

```csharp
    /// <summary>
    /// Whether the given point (in the same coordinate space as the rectangle) lies inside
    /// the rectangle, inclusive of all four edges.
    /// </summary>
    public bool Contains(double x, double y)
        => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PhysicalRectContainsTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Run the full Core test suite to confirm no regressions**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "Category!=UI"`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Models/PhysicalRect.cs tests/EyeCtrl.Core.Tests/Models/PhysicalRectContainsTests.cs
git commit -m "feat(core): add PhysicalRect.Contains hit-test"
```

---

## Task 2: Add `App.ActiveProviderType` static property

Mode gating for the controller. Mirrors the existing `App.IsTestMode` precedent exactly.

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Add the property declaration**

In `src/EyeCtrl.App/App.xaml.cs`, immediately after the `IsTestMode` property (currently around line 46), add:

```csharp
    /// <summary>
    /// The resolved gaze provider type for this run (Auto resolves to the concrete type at startup).
    /// Used to gate eye-tracker-only features (e.g. toolbar auto-hide). Set in OnStartup.
    /// </summary>
    public static GazeProviderType ActiveProviderType { get; private set; }
```

Ensure `EyeCtrl.Core.Enums` is imported at the top of the file (it likely already is via other usings — verify; if `GazeProviderType` is unresolved, add `using EyeCtrl.Core.Enums;`).

- [ ] **Step 2: Set the property in OnStartup**

In `OnStartup`, the local is currently computed at the line `var activeProviderType = GazeProviderFactory.GetProviderType(gazeProvider);`. Immediately after that line, add the assignment:

```csharp
            ActiveProviderType = activeProviderType;
```

(Leave the existing local in place — it's still passed to `InitializeOverlays`. The new property just publishes the same value durably.)

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/EyeCtrl.App -c Debug --nologo`
Expected: build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(app): expose resolved provider type as App.ActiveProviderType"
```

---

## Task 3: Create `PauseIndicatorWindow` overlay (the edge bar)

A minimal click-through overlay: a single colored rectangle positioned along the docked edge. Created before the controller because the controller (Task 4) commands its `Show()`/`Hide()`.

**Files:**
- Create: `src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml.cs`

- [ ] **Step 1: Write the XAML**

Create `src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml`:

```xml
<overlay:OverlayWindow x:Class="EyeCtrl.App.Overlays.PauseIndicatorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlay="clr-namespace:EyeCtrl.App.Overlays"
        Title="PauseIndicator"
        SizeToContent="WidthAndHeight"
        Topmost="True"
        ShowInTaskbar="False"
        IsClickThrough="True"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent">
    <Border x:Name="Bar" Background="#C0204060" Height="8" Width="200"/>
</overlay:OverlayWindow>
```

`IsClickThrough="True"` keeps the `OverlayWindow` default (`OverlayWindow.OnSourceInitialized` → `SetOverlayInteraction(clickThrough:true, focusable:false)`), so the bar is a passive indicator that never steals input.

- [ ] **Step 2: Write the code-behind**

Create `src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml.cs`:

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;

/// <summary>
/// Thin edge-bar shown when the toolbar is auto-hidden AND the app is paused, so the user
/// isn't left with no feedback (the toolbar — and its resume button — is off-screen).
/// Sits along the docked edge; doubles as a "look here to summon" hint. Click-through.
/// </summary>
public partial class PauseIndicatorWindow : OverlayWindow
{
    private const double BarThicknessLogical = 8;

    public PauseIndicatorWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Positions the bar along the given screen edge. Call after the window has a handle
    /// (e.g. on first Show). Uses SystemParameters.WorkArea in logical units, matching
    /// ToolbarWindow.SetMode's coordinate space.
    /// </summary>
    public void SetEdge(string edge)
    {
        var screen = SystemParameters.WorkArea;
        // Force a measure so ActualWidth/ActualHeight are valid.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Arrange(new Rect(0, 0, DesiredSize.Width, DesiredSize.Height));

        switch (edge.ToLowerInvariant())
        {
            case "top":
                Width = screen.Width;
                Height = BarThicknessLogical;
                Left = 0;
                Top = 0;
                break;
            case "bottom":
                Width = screen.Width;
                Height = BarThicknessLogical;
                Left = 0;
                Top = screen.Height - BarThicknessLogical;
                break;
            case "left":
                Width = BarThicknessLogical;
                Height = screen.Height;
                Left = 0;
                Top = 0;
                break;
            case "right":
                Width = BarThicknessLogical;
                Height = screen.Height;
                Left = screen.Width - BarThicknessLogical;
                Top = 0;
                break;
            default:
                Width = screen.Width;
                Height = BarThicknessLogical;
                Left = 0;
                Top = 0;
                break;
        }
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/EyeCtrl.App -c Debug --nologo`
Expected: build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml.cs
git commit -m "feat(app): add PauseIndicatorWindow edge-bar overlay"
```

---

## Task 4: Register `PauseIndicator` in the OverlayManager

Expose the new window through `OverlayManager`/`IOverlayManager` so the controller and EventCoordinator can reach it.

**Files:**
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs`
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs`

- [ ] **Step 1: Add the property to the interface**

In `src/EyeCtrl.App/Services/IOverlayManager.cs`, add (alongside the other overlay properties — find the `Toolbar` property declaration and add immediately after):

```csharp
    /// <summary>Thin edge-bar pause indicator; shown when toolbar is hidden + paused.</summary>
    PauseIndicatorWindow PauseIndicator { get; }
```

- [ ] **Step 2: Add the backing property + construction**

In `src/EyeCtrl.App/Services/OverlayManager.cs`:

(a) Add the auto-property in the property block (after `public ToolbarWindow Toolbar { get; private set; } = null!;`):

```csharp
        public PauseIndicatorWindow PauseIndicator { get; private set; } = null!;
```

(b) In `Initialize(...)`, after the line `EdgeButtonBar = new EdgeButtonBarWindow();` add:

```csharp
        PauseIndicator = new PauseIndicatorWindow();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/EyeCtrl.App -c Debug --nologo`
Expected: build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/Services/OverlayManager.cs
git commit -m "feat(app): register PauseIndicator in OverlayManager"
```

---

## Task 5: Create the `IToolbarVisibility` seam

The controller's only output is window visibility. Inject it behind an interface so the controller is unit-testable without a real window or dispatcher.

**Files:**
- Create: `src/EyeCtrl.App/Services/IToolbarVisibility.cs`

- [ ] **Step 1: Write the interface**

Create `src/EyeCtrl.App/Services/IToolbarVisibility.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>
/// The toolbar-visibility seam used by <see cref="ToolbarAutoHideController"/>.
/// Abstracts <c>OverlayManager.Toolbar</c> + <c>PauseIndicator</c> so the controller's
/// state machine is testable without WPF windows or a dispatcher.
/// </summary>
/// <remarks>
/// All methods must marshal onto the UI thread themselves (the controller calls them
/// from the gaze thread). Implementations: a real adapter over OverlayManager, and a
/// recorded-call fake in tests.
/// </remarks>
public interface IToolbarVisibility
{
    /// <summary>Shows the toolbar (and hides the pause indicator if it was up).</summary>
    void ShowToolbar();

    /// <summary>Hides the toolbar. Shows the pause indicator iff <paramref name="paused"/> is true.</summary>
    /// <param name="paused">Whether the app is currently paused (controls the edge bar).</param>
    void HideToolbar(bool paused);

    /// <summary>Force-shows the toolbar and hides the pause indicator (tray fallback / suspend-recover).</summary>
    void ForceShow();

    /// <summary>Repositions the pause indicator along the given docked edge ("Top"/"Bottom"/"Left"/"Right").</summary>
    void PositionIndicator(string edge);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/EyeCtrl.App -c Debug --nologo`
Expected: build succeeds, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Services/IToolbarVisibility.cs
git commit -m "feat(app): add IToolbarVisibility seam for the auto-hide controller"
```

---

## Task 6: Create `ToolbarAutoHideController` + tests (TDD)

The heart of the feature. Pure state-machine logic over injected state, so it's fully unit-testable. The gaze-thread decision method takes the current sample + injected flags and returns a decision enum; the wiring (subscribe to `GazeSampled`, dispatcher hops) is a thin shell around it.

**Files:**
- Create: `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs`
- Create: `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs` (create the `Services` dir if missing; create the test project reference if the project doesn't exist — see Step 0)

- [ ] **Step 0: Ensure the App test project exists and references EyeCtrl.App**

Run: `ls tests/EyeCtrl.App.Tests/*.csproj`
If it exists, skip to Step 1. If it does NOT exist, create it by copying the pattern from `tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj` (a net10.0 test project with `<ProjectReference>` to the project under test). Verify it references `EyeCtrl.App.csproj` and `EyeCtrl.Core.csproj`, and has the global NFluent using via `tests/Directory.Build.props`. Run `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` to confirm it builds and discovers zero tests.

- [ ] **Step 1: Write the failing state-machine tests**

Create `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using NFluent;

public class ToolbarAutoHideControllerTests
{
    // Screen 1920x1080 (physical px). Toolbar docked TOP, rect at top-center 50px tall.
    private const double ScreenW = 1920;
    private const double ScreenH = 1080;
    private static readonly PhysicalRect ToolbarRect = new(810, 0, 300, 50); // top edge
    private const string Edge = "Top";
    private const long TicksPerMs = 1000; // GazePoint.TimestampMicroseconds

    // ── Enablement ──

    [Fact]
    public void Disabled_WhenProviderIsMouse_NeverActs()
    {
        var sut = NewController(enabled: false);
        var vis = new FakeVisibility();
        sut.Attach(vis);

        // Feed 10s of off-toolbar gaze — must not hide.
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 10_000_000);

        Check.That(vis.HideCalls).IsEqualTo(0);
        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    // ── HIDE transition ──

    [Fact]
    public void Hides_AfterHideDelay_OfGazeOffToolbar()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 500, valid: true), 0);                         // on-screen, off-toolbar
        sut.Feed(CreateSample(960, 500, valid: true), 4_999_000);                 // just under 5s
        Check.That(vis.HideCalls).IsEqualTo(0);                                   // not yet
        sut.Feed(CreateSample(960, 500, valid: true), 5_001_000);                 // past 5s
        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void DoesNotHide_WhileGazeOnToolbar()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        // Gaze resting on the toolbar for 10s — never hides.
        for (long t = 0; t <= 10_000_000; t += 500_000)
            sut.Feed(CreateSample(960, 25, valid: true), t); // (960,25) inside the toolbar rect

        Check.That(vis.HideCalls).IsEqualTo(0);
    }

    [Fact]
    public void GazeBackOnToolbar_ResetsHideCountdown()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 500, valid: true), 0);        // off-toolbar
        sut.Feed(CreateSample(960, 500, valid: true), 4_000_000);// 4s in
        sut.Feed(CreateSample(960, 25, valid: true), 4_100_000); // back on toolbar — reset
        sut.Feed(CreateSample(960, 500, valid: true), 9_000_000);// 4.9s since reset — not yet
        Check.That(vis.HideCalls).IsEqualTo(0);
        sut.Feed(CreateSample(960, 500, valid: true), 9_200_000);// past 5s since reset
        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    // ── SUMMON transition (directional, off-screen valid coords only) ──

    [Fact]
    public void Summons_OnTopEdge_WhenGazePastTop()
    {
        var sut = NewController(); // Edge = Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);      // start from HIDDEN

        // Valid sample past the top edge (Y < -50 margin)
        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 401_000); // past 400ms summon delay

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void DoesNotSummon_OnWrongEdge_BottomDockedLookingTop()
    {
        var sut = NewController(edge: "Bottom"); // docked BOTTOM
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        // Looking past the TOP edge (Y < -50) — wrong direction for a bottom-docked toolbar.
        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void Summons_OnBottomEdge_WhenGazePastBottom()
    {
        var sut = NewController(edge: "Bottom");
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(960, ScreenH + 100, valid: true), 0);
        sut.Feed(CreateSample(960, ScreenH + 100, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void Summons_OnLeftEdge_WhenGazePastLeft()
    {
        var sut = NewController(edge: "Left");
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(-100, 540, valid: true), 0);
        sut.Feed(CreateSample(-100, 540, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void Summons_OnRightEdge_WhenGazePastRight()
    {
        var sut = NewController(edge: "Right");
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(ScreenW + 100, 540, valid: true), 0);
        sut.Feed(CreateSample(ScreenW + 100, 540, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void SummonRequires_SustainedOffScreen_SingleSampleDoesNotSummon()
    {
        var sut = NewController(); // Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(960, -100, valid: true), 0);       // one off-screen sample
        sut.Feed(CreateSample(960, 500, valid: true), 100_000);  // back on-screen immediately
        sut.Feed(CreateSample(960, 500, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void InvalidSample_DoesNotSummon()
    {
        var sut = NewController(); // Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(0, 0, valid: false), 0);
        sut.Feed(CreateSample(0, 0, valid: false), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    // ── Blink does not reset the hide countdown ──

    [Fact]
    public void Blink_InvalidSample_DoesNotResetHideCountdown()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 4_000_000); // 4s
        sut.Feed(CreateSample(0, 0, valid: false), 4_100_000);    // blink — ignored, no reset
        sut.Feed(CreateSample(960, 500, valid: true), 5_001_000); // past 5s total
        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    // ── Pause indicator ──

    [Fact]
    public void HideWhilePaused_ShowsIndicator_HideWhileNotPaused_DoesNot()
    {
        var sut = NewController();
        var visPaused = new FakeVisibility();
        sut.Attach(visPaused);
        sut.SetPaused(true);
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 5_001_000);
        Check.That(visPaused.LastHidePaused).IsTrue();  // indicator shown

        var sut2 = NewController();
        var visNotPaused = new FakeVisibility();
        sut2.Attach(visNotPaused);
        sut2.SetPaused(false);
        sut2.Feed(CreateSample(960, 500, valid: true), 0);
        sut2.Feed(CreateSample(960, 500, valid: true), 5_001_000);
        Check.That(visNotPaused.LastHidePaused).IsFalse(); // no indicator
    }

    [Fact]
    public void SummonWhilePaused_HidesIndicator()
    {
        var sut = NewController(); // Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.SetPaused(true);
        sut.HideForTest(vis);
        Check.That(vis.LastHidePaused).IsTrue();

        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 401_000);
        Check.That(vis.ShowCalls).IsEqualTo(1);         // toolbar shown, indicator hidden
    }

    // ── Suspension (keyboard / settings / calibration) ──

    [Fact]
    public void Suspended_DoesNotHide_AndForceShowsIfHidden()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis); // currently hidden

        sut.SetSuspended(true); // e.g. settings window opened
        Check.That(vis.ForceShowCalls).IsEqualTo(1); // recovered from hidden

        // Now feed lots of off-toolbar gaze while suspended — no further hides.
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 10_000_000);
        Check.That(vis.HideCalls).IsEqualTo(1); // only the initial HideForTest
    }

    [Fact]
    public void UnsuspendedAfterIdle_DoesNotInstantlyHide()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.SetSuspended(true);
        sut.SetSuspended(false);             // just unsuspended, toolbar visible
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 1_000_000); // only 1s
        Check.That(vis.HideCalls).IsEqualTo(0);
    }

    // ── helpers ──

    private static ToolbarAutoHideController NewController(bool enabled = true, string edge = Edge)
        => new(enabled, edge, ScreenW, ScreenH, ToolbarRect,
               hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);

    private static GazePoint CreateSample(double x, double y, bool valid)
        => new(x, y, 0, valid);

    // ── fake ──
    private sealed class FakeVisibility : IToolbarVisibility
    {
        public int ShowCalls;
        public int HideCalls;
        public int ForceShowCalls;
        public bool? LastHidePaused;

        public void ShowToolbar() => ShowCalls++;
        public void HideToolbar(bool paused) { HideCalls++; LastHidePaused = paused; }
        public void ForceShow() => ForceShowCalls++;
        public void PositionIndicator(string edge) { }
    }
}
```

Note the test-only helpers `Attach`, `Feed`, `HideForTest`, `SetPaused`, `SetSuspended` — these are the controller's test surface, defined in Step 3.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarAutoHideControllerTests"`
Expected: FAIL with compile errors (type `ToolbarAutoHideController` not found).

- [ ] **Step 3: Write the controller implementation**

Create `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs`:

```csharp
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// Auto-hides the docked toolbar when gaze isn't on it, and summons it back by looking past the
/// docked screen edge. Runs a two-state hysteresis machine on the gaze thread against cached
/// geometry; dispatches visibility changes to the UI thread only on transitions.
///
/// The gaze-thread decision logic is split into <see cref="Decide"/>, a pure method over injected
/// state, so the state machine is unit-testable without WPF or a dispatcher.
///
/// See docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md.
/// </summary>
public sealed class ToolbarAutoHideController
{
    // ── Tunables (defaults from the spec) ──
    private readonly long _hideDelayUs;     // run of "gaze off toolbar" before hiding
    private readonly long _summonDelayUs;   // run of "gaze past docked edge" before summoning
    private readonly double _offScreenMarginPx;
    private readonly Func<bool>? _calibrationActive;

    // ── Geometry (cached, physical px) ──
    private readonly double _screenW;
    private readonly double _screenH;
    private PhysicalRect _toolbarRect;
    private string _edge; // "Top"/"Bottom"/"Left"/"Right" — settable (changes on DockedEdge edit)
    private readonly bool _enabled;

    // ── Runtime state (gaze-thread) ──
    private enum State { Visible, Hidden }
    private State _state = State.Visible;
    private long _runStartUs;          // start of the current condition run (hide or summon)
    private bool _suspended;
    private bool _paused;

    // ── Output seam ──
    private IToolbarVisibility? _visibility;

    public ToolbarAutoHideController(bool enabled, string edge, double screenW, double screenH,
        PhysicalRect toolbarRect, int hideDelayMs, int summonDelayMs, double offScreenMarginPx,
        Func<bool>? calibrationActive = null)
    {
        _enabled = enabled;
        _edge = edge;
        _screenW = screenW;
        _screenH = screenH;
        _toolbarRect = toolbarRect;
        _hideDelayUs = hideDelayMs * 1000L;
        _summonDelayUs = summonDelayMs * 1000L;
        _offScreenMarginPx = offScreenMarginPx;
        _calibrationActive = calibrationActive;
    }

    /// <summary>Binds the output seam. Real wiring passes an OverlayManager adapter; tests pass a fake.</summary>
    public void Attach(IToolbarVisibility visibility)
    {
        _visibility = visibility;
        visibility.PositionIndicator(_edge);
    }

    // ── Configuration setters (called from EventCoordinator on the UI thread) ──

    public void SetPaused(bool paused) => _paused = paused;

    public void SetSuspended(bool suspended)
    {
        if (suspended == _suspended) return;
        _suspended = suspended;
        if (suspended)
        {
            // Recover from hidden so the user isn't stranded while a modal is up.
            if (_state == State.Hidden && _enabled)
                _visibility?.ForceShow();
            _state = State.Visible;
            _runStartUs = 0;
        }
        else
        {
            // Just unsuspended: reset the hide countdown so it doesn't instantly hide.
            _runStartUs = 0;
        }
    }

    /// <summary>Refreshes the cached toolbar rect (call on the UI thread after layout/dock changes).</summary>
    public void UpdateToolbarRect(PhysicalRect rect) => _toolbarRect = rect;

    /// <summary>Refreshes the active docked edge (call when DockedEdge changes in settings).</summary>
    public void UpdateEdge(string edge) => _edge = edge;

    // ── The pure decision (gaze thread) ──

    /// <summary>
    /// Feeds one raw gaze sample and applies any state transition. Returns the resulting state.
    /// Pure with respect to external effects — only touches <see cref="_visibility"/> on transitions.
    /// </summary>
    public State Feed(GazePoint sample, long timestampUs)
    {
        if (!_enabled || _suspended || _visibility == null)
            return _state;
        if (_calibrationActive?.Invoke() == true)
            return _state; // suspended during calibration

        switch (_state)
        {
            case State.Visible:
                if (sample.IsValid && _toolbarRect.Contains(sample.X, sample.Y))
                {
                    _runStartUs = timestampUs; // gaze on toolbar — reset hide countdown
                }
                else if (!sample.IsValid)
                {
                    // Blink: ignored, does NOT reset the hide countdown (per spec).
                }
                else if (timestampUs - _runStartUs >= _hideDelayUs)
                {
                    _state = State.Hidden;
                    _visibility.HideToolbar(_paused);
                }
                break;

            case State.Hidden:
                if (IsPastDockedEdge(sample))
                {
                    if (_runStartUs == 0) _runStartUs = timestampUs;
                    if (timestampUs - _runStartUs >= _summonDelayUs)
                    {
                        _state = State.Visible;
                        _runStartUs = timestampUs;
                        _visibility.ShowToolbar();
                    }
                }
                else
                {
                    _runStartUs = 0; // condition broken — reset summon countdown
                }
                break;
        }

        return _state;
    }

    private bool IsPastDockedEdge(GazePoint s)
    {
        if (!s.IsValid) return false; // invalid samples never summon (per spec)
        return _edge.ToLowerInvariant() switch
        {
            "top"    => s.Y < -_offScreenMarginPx,
            "bottom" => s.Y > _screenH + _offScreenMarginPx,
            "left"   => s.X < -_offScreenMarginPx,
            "right"  => s.X > _screenW + _offScreenMarginPx,
            _        => s.Y < -_offScreenMarginPx,
        };
    }

    // ── Test-only helpers (used by ToolbarAutoHideControllerTests) ──
    // (InternalsVisibleTo is not declared from EyeCtrl.App; these are public test hooks instead.
    //  Acceptable for a controller whose entire purpose is testable state transitions.)

    public void SetPausedForTest(bool paused) => _paused = paused;
    public void HideForTest(IToolbarVisibility vis)
    {
        _state = State.Hidden;
        vis.HideToolbar(_paused);
    }

    // Expose state name for any future assertion diagnostics.
    public string StateName => _state.ToString();
}
```

- [ ] **Step 4: Reconcile test API vs implementation**

The tests call `SetPaused`, `SetSuspended`, `HideForTest`. The implementation has `SetPaused`, `SetSuspended`, `HideForTest`. The tests reference `SetPaused` (not `SetPausedForTest`) — so the **public test hook is `SetPaused`**. Remove the duplicate `SetPausedForTest` from the implementation (it was offered as an alternative; keep `SetPaused` which the tests use). The constructor also takes an optional `Func<bool>? calibrationActive` (Task 7 passes a live `IsSessionActive` check; the test helper `NewController` omits it, relying on the `null` default). Confirm the test's `NewController` constructor args match: `(bool enabled, string edge, double screenW, double screenH, PhysicalRect toolbarRect, int hideDelayMs, int summonDelayMs, double offScreenMarginPx)` plus the optional `calibrationActive`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarAutoHideControllerTests"`
Expected: PASS (all ~16 tests). If any fail, fix the controller logic (not the tests) — the tests encode the spec.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/ToolbarAutoHideController.cs tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs
git commit -m "feat(app): add ToolbarAutoHideController state machine + tests"
```

---

## Task 7: Wire the controller into `EventCoordinator`

Construct the controller, subscribe it to `GazeSampled`, feed it pause/suspend/geometry changes, and dispose it. This is where the pure state machine meets the live app.

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs` (create — the real `IToolbarVisibility` over `OverlayManager`)

- [ ] **Step 1: Create the real visibility adapter**

Create `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;

/// <summary>
/// Real <see cref="IToolbarVisibility"/> over <see cref="IOverlayManager"/>. Marshals every
/// call onto the WPF dispatcher (the controller calls from the gaze thread).
/// </summary>
public sealed class ToolbarVisibilityAdapter : IToolbarVisibility
{
    private readonly IOverlayManager _overlays;
    private readonly Dispatcher _dispatcher;

    public ToolbarVisibilityAdapter(IOverlayManager overlays, Dispatcher dispatcher)
    {
        _overlays = overlays;
        _dispatcher = dispatcher;
    }

    public void ShowToolbar() => _dispatcher.BeginInvoke(() =>
    {
        _overlays.Toolbar.Show();
        _overlays.PauseIndicator.Hide();
    });

    public void HideToolbar(bool paused) => _dispatcher.BeginInvoke(() =>
    {
        _overlays.Toolbar.Hide();
        if (paused)
        {
            _overlays.PauseIndicator.Show();
        }
        else
        {
            _overlays.PauseIndicator.Hide();
        }
    });

    public void ForceShow() => _dispatcher.BeginInvoke(() =>
    {
        _overlays.Toolbar.Show();
        _overlays.PauseIndicator.Hide();
    });

    public void PositionIndicator(string edge) => _dispatcher.BeginInvoke(() =>
        _overlays.PauseIndicator.SetEdge(edge));
}
```

- [ ] **Step 2: Add controller fields + construction to EventCoordinator**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`:

(a) Add fields near the other private fields (after `_scrollModeController`, ~line 55):

```csharp
    private ToolbarAutoHideController? _toolbarAutoHide;
    private ToolbarVisibilityAdapter? _toolbarVisibility;
```

(b) In `Initialize(...)`, after the `SetupGazeRouting();` call (the last line of the method body, before the closing brace), add the controller construction + subscription:

```csharp
        SetupToolbarAutoHide();
```

(c) Add the `SetupToolbarAutoHide` method (place it near `SetupGazeRouting`, ~line 362):

```csharp
    private void SetupToolbarAutoHide()
    {
        if (_context == null) return;

        var s = _context.Settings.Toolbar;
        bool enabled = EyeCtrl.App.App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse && s.IsDocked;

        // Cache the toolbar rect in physical px from the live window.
        var toolbarRect = CaptureToolbarRect();

        var controller = new ToolbarAutoHideController(
            enabled: enabled,
            edge: s.DockedEdge,
            screenW: SystemParameters.PrimaryScreenWidth * EyeCtrl.Core.Services.DpiHelper.ScaleX,
            screenH: SystemParameters.PrimaryScreenHeight * EyeCtrl.Core.Services.DpiHelper.ScaleY,
            toolbarRect: toolbarRect,
            hideDelayMs: 5000,
            summonDelayMs: 400,
            offScreenMarginPx: 50,
            calibrationActive: () => _context.CalibrationService.IsSessionActive);

        var vis = new ToolbarVisibilityAdapter(_context.OverlayManager, _context.Dispatcher);
        controller.Attach(vis);

        _toolbarAutoHide = controller;
        _toolbarVisibility = vis;

        if (enabled)
        {
            _context.GazeEngine.GazeSampled += sample => controller.Feed(sample, sample.TimestampMicroseconds);
        }
    }

    private EyeCtrl.Core.Models.PhysicalRect CaptureToolbarRect()
    {
        var t = _context!.OverlayManager.Toolbar;
        // Convert logical window bounds (WPF DIPs) to physical px.
        double x = EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalX(t.Left);
        double y = EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalY(t.Top);
        double w = EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalX(t.ActualWidth);
        double h = EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalY(t.ActualHeight);
        return new EyeCtrl.Core.Models.PhysicalRect(x, y, w, h);
    }
```

- [ ] **Step 3: Feed pause + suspend changes**

(a) Pause: find `OnStateChanged`-style handling. Subscribe the controller to pause via the existing `StateChangedMessage` handler. In `SetupMessageHandlers()` add (or extend the existing `StateChangedMessage` registration):

```csharp
        _context.Messenger.Register<Core.Messages.StateChangedMessage>(this, (_, msg) =>
        {
            _toolbarAutoHide?.SetPaused(msg.NewState == Core.InteractionState.Paused);
        });
```

(If `StateChangedMessage` is already registered elsewhere, add the `SetPaused` call to that existing handler rather than double-registering. Search `StateChangedMessage` in the file to find it.)

(b) Suspend on keyboard: in `SetKeyboardVisible(bool visible, bool manageSuppression)` (line ~1003), at the top of the method add:

```csharp
        _toolbarAutoHide?.SetSuspended(visible);
```

(c) Suspend on settings window: in `ShowSettingsWindow` where `_settingsOpen = true;` (line ~705) add after it:

```csharp
        _toolbarAutoHide?.SetSuspended(true);
```

And in `OnSettingsWindowClosed` where `_settingsOpen = false;` (line ~774) add after it:

```csharp
        _toolbarAutoHide?.SetSuspended(false);
```

(d) Suspend during calibration: `CalibrationService` has **no session-start/end event** — it exposes only the polling property `IsSessionActive` (`_targetPositions != null`, set in `StartSession`/`CompleteSession`/`CancelSession`). There is no clean subscription hook. The controller's `Feed` method (Task 6) therefore checks calibration indirectly: pass a `Func<bool>? CalibrationActive` to the controller constructor and short-circuit `Feed` when it returns true. Add this to the constructor signature in Task 6 (the test `NewController` helper passes `null`), and in `SetupToolbarAutoHide` pass `() => _context!.CalibrationService.IsSessionActive`. Update Task 6's constructor + the `Feed` guard:

```csharp
    // In the constructor, add:
    private readonly Func<bool>? _calibrationActive;
    // (add parameter: Func<bool>? calibrationActive = null)

    // At the top of Feed:
    if (!_enabled || _suspended || _visibility == null) return _state;
    if (_calibrationActive?.Invoke() == true) return _state;  // suspended during calibration
```

This keeps calibration suspension out of the messy subscription path and testable (a test can pass `() => true` to exercise the calibration-suspended branch).

- [ ] **Step 4: Refresh geometry on settings change**

In `OnSettingsChanged(EyeCtrlSettings s)` (line ~743), inside the `if (_toolbarSnapshot != toolbarSnapshot)` block (after the existing `Toolbar.Initialize`/`SetMode` calls), add:

```csharp
            _toolbarAutoHide?.UpdateEdge(s.Toolbar.DockedEdge);
            _toolbarVisibility?.PositionIndicator(s.Toolbar.DockedEdge);
            _dispatcherBeginInvokeRefreshRect();
```

Where `_dispatcherBeginInvokeRefreshRect` is a small helper that enqueues a rect refresh after the toolbar re-layout completes:

```csharp
    private void _dispatcherBeginInvokeRefreshRect()
    {
        if (_context == null || _toolbarAutoHide == null) return;
        _context.Dispatcher.BeginInvoke(new Action(() =>
        {
            _toolbarAutoHide.UpdateToolbarRect(CaptureToolbarRect());
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
```

(`DispatcherPriority.Loaded` ensures the toolbar has re-measured before we read `ActualWidth/Height`.)

- [ ] **Step 5: Dispose the controller**

In `Dispose()` (line ~1039), before the existing cleanup, add:

```csharp
        // Controller holds a GazeSampled subscription; nothing else to dispose, but null it.
        _toolbarAutoHide = null;
        _toolbarVisibility = null;
```

(The `GazeSampled` subscription is a delegate capture over the controller; once the controller is unreferenced and the GazeEngine is disposed, the subscription is inert. No explicit unsubscribe is needed because the controller + GazeEngine are both app-lifetime singletons — same pattern as the eye-care subscription in App.xaml.cs.)

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build src/EyeCtrl.App -c Debug --nologo`
Expected: build succeeds, 0 errors.

- [ ] **Step 7: Run all non-UI tests to confirm no regressions**

Run: `dotnet test --filter "Category!=UI"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat(app): wire ToolbarAutoHideController into EventCoordinator"
```

---

## Task 8: Add the tray "Show toolbar" fallback item

Hardware-independent recovery path. Conditionally visible only when auto-hide is active.

**Files:**
- Modify: `src/EyeCtrl.App/Services/TrayIconManager.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs` (pass the force-show callback)

- [ ] **Step 1: Add the menu item to TrayIconManager**

In `src/EyeCtrl.App/Services/TrayIconManager.cs`, change the constructor signature to accept an optional force-show callback, and add the item. Modify the constructor:

```csharp
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem? _showToolbarMenuItem;
    private readonly Icon? _customIcon;
    private readonly Dispatcher _dispatcher;
    private readonly InteractionManager _interactionManager;
    private readonly IMessenger _messenger;
    private bool _disposed;

    public TrayIconManager(InteractionManager interactionManager, Dispatcher dispatcher, IMessenger messenger,
        Action shutdownCallback, Action? showToolbarCallback = null)
    {
        _dispatcher = dispatcher;
        _interactionManager = interactionManager;
        _messenger = messenger;

        _pauseMenuItem = new ToolStripMenuItem("Pause");
        _pauseMenuItem.Click += (_, _) =>
        {
            interactionManager.TogglePause();
            _pauseMenuItem.Text = interactionManager.CurrentState == InteractionState.Paused ? "Resume" : "Pause";
        };

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => dispatcher.BeginInvoke(() =>
            _messenger.Send(new SettingsRequestedMessage()));

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => dispatcher.BeginInvoke(shutdownCallback);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(settingsItem);
        if (showToolbarCallback != null)
        {
            _showToolbarMenuItem = new ToolStripMenuItem("Show toolbar");
            _showToolbarMenuItem.Click += (_, _) => dispatcher.BeginInvoke(showToolbarCallback);
            menu.Items.Add(_showToolbarMenuItem);
            menu.Items.Add(new ToolStripSeparator());
        }
        else
        {
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(exitItem);
```

(The rest of the constructor — icon loading, `_trayIcon` creation, `StateChanged` subscription — stays unchanged.)

- [ ] **Step 2: Pass the callback from App.xaml.cs**

In `App.xaml.cs`, the `TrayIconManager` is constructed around line 131:

```csharp
        _trayIconManager = new TrayIconManager(im, Dispatcher, messenger, () => Shutdown());
```

Change it to pass a force-show callback that commands the controller:

```csharp
        bool autoHideActive = ActiveProviderType != Core.Enums.GazeProviderType.Mouse && settings.Toolbar.IsDocked;
        _trayIconManager = new TrayIconManager(
            im, Dispatcher, messenger,
            shutdownCallback: () => Shutdown(),
            showToolbarCallback: autoHideActive
                ? () => _eventCoordinator?.ForceShowToolbar()
                : null);
```

This requires a `ForceShowToolbar()` method on `EventCoordinator` (Step 3).

- [ ] **Step 3: Add `ForceShowToolbar` to EventCoordinator**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, add a public method:

```csharp
    /// <summary>Tray fallback: force-show the toolbar regardless of auto-hide state.</summary>
    public void ForceShowToolbar()
    {
        if (_context == null) return;
        _context.Dispatcher.BeginInvoke(() =>
        {
            _toolbarVisibility?.ForceShow();
            // Also flip the controller into Visible so it doesn't immediately re-hide.
            _toolbarAutoHide?.ResetToVisible();
        });
    }
```

And add the `ResetToVisible` method to `ToolbarAutoHideController`:

```csharp
    public void ResetToVisible()
    {
        _state = State.Visible;
        _runStartUs = 0;
    }
```

- [ ] **Step 4: Build + test**

Run: `dotnet build src/EyeCtrl.App -c Debug --nologo`
Expected: build succeeds, 0 errors.

Run: `dotnet test --filter "Category!=UI"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/TrayIconManager.cs src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat(app): add tray 'Show toolbar' fallback item"
```

---

## Task 9: Manual smoke test + final cleanup

Verify end-to-end behavior on the PCEye 5, then remove the now-obsolete probe sample.

**Files:**
- Delete: `samples/OffScreenGazeProbe/` (entire directory)

- [ ] **Step 1: Build the app**

Run: `dotnet build src/EyeCtrl.App -c Debug --nologo`
Expected: build succeeds.

- [ ] **Step 2: Run the app against the eye tracker and smoke-test**

Run: `dotnet run --project src/EyeCtrl.App`
Manually verify (on the PCEye 5):
1. Toolbar starts visible.
2. Look away from the toolbar (elsewhere on screen) for ~5s → toolbar hides. If paused, the thin edge bar appears along the docked edge; if not paused, nothing.
3. Look past the docked edge (e.g. below the screen for a bottom-docked toolbar) → toolbar reappears within ~400ms.
4. Open Settings while hidden → toolbar force-shows (suspend). Close Settings → auto-hide resumes.
5. Right-click the tray icon → "Show toolbar" appears → click it → toolbar shows.
6. Look past a NON-docked edge → no summon (e.g. looking left doesn't summon a bottom-docked toolbar).

- [ ] **Step 3: Delete the probe sample**

The assumption is verified; the sample is obsolete. Run:

```bash
git rm -r samples/OffScreenGazeProbe
```

- [ ] **Step 4: Commit**

```bash
git commit -m "chore: remove OffScreenGazeProbe sample (assumption verified)"

# Plus a docs note if the smoke test surfaced any tuning (e.g. HIDE_DELAY feel):
git commit --allow-empty -m "docs: toolbar auto-hide shipped — tune HIDE_DELAY per smoke test"
```

- [ ] **Step 5: Final detect_changes before opening a PR**

Run GitNexus `detect_changes` against `main` to confirm the blast radius is as expected (the controller, the new overlays, EventCoordinator wiring, App.xaml.cs, TrayIconManager, PhysicalRect). Resolve any unexpected scope before opening the PR.

```bash
git checkout main
git merge --no-ff feature/toolbar-auto-hide   # or open a PR instead
```

---

## Notes for the implementer

- **TDD discipline:** Tasks 1 and 6 are pure-logic TDD. Tasks 2–5, 7–8 are wiring (build-and-smoke). The state machine must pass all its tests before Task 7 wires it — never wire untested state logic.
- **Threading:** The controller's `Feed` runs on the gaze thread (it's the `GazeSampled` handler). It reads only its own fields + the injected `PhysicalRect` (a value type). The only cross-thread hop is inside `ToolbarVisibilityAdapter`, which marshals to the dispatcher. Do NOT read WPF window properties from `Feed`.
- **NFluent float:** `Check.That(x).IsCloseTo(y, 1e-6)` is absolute delta — don't use it like xUnit's precision:N.
- **State enum:** the controller's `State` is `public` (tests assert via `StateName`). Keep it that way.
- **The `_paused` setter the tests use is `SetPaused`** (not `SetPausedForTest`); pick one and delete the other from the controller to avoid ambiguity. The tests in Task 6 call `SetPaused`, so keep that.
