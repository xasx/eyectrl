# Simplification Pass 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Consolidate the overlay-positioning and service-lifecycle boilerplate (survey cluster 4) and delete the eight verified test-only members (cluster 5) ÔÇö net roughly ÔêÆ450 to ÔêÆ550 lines with canonical implementations for the DPI-sensitive positioning code.

**Architecture:** Three independent areas: (1) a new `OverlayPositioning` helper beside `OverlayWindow` absorbing the screen-edge switch and fullscreen-coverage math, plus a `TryGetScreenCenter` helper for the PointToScreen loops that already swallow `InvalidOperationException`; (2) a `CoordinatorBase` absorbing the identical Initialize/Dispose skeleton of the 9 coordinators, a shared `NewlineJsonPipeServer` in `EyeCtrl.Platform` for the two pipe servers, and a `DispatchSafe` collapse in `ManualSelectionDwellService`; (3) pure-minus deletions of eight test-only members, two of which require mechanical test rewrites onto production APIs.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm 8.4, xUnit v3 + NFluent 3.1, System.IO.Pipes.AccessControl.

## Global Constraints

- Worktree `.worktrees/simplify-pass-2`, branch `refactor/simplify-pass-2`, from `origin/main` @ `4c84833a`. Never commit on `main`.
- NFluent assertions only (`Check.That(...)`); no xUnit `Assert.*`.
- **Never run two test suites at the same time** on this box. Sequential `dotnet test` only. Run `EyeCtrl.App.Tests` as a whole project, never a filtered class (`ExternalDwellServiceTests` fails in isolation).
- Commits: repo conventional style (`refactor(scope): lowercase imperative`).
- **Behavior-preservation rules discovered by the deep-dive (these are binding):**
  - `TaskSwitcherWindow.UpdateTilePositions` must keep a method with that exact name and signature containing the literal `PointToScreen` inline, and must NOT contain `LogicalToPhysicalX(center` ÔÇö `TaskSwitcherDwellPositionTests` pins this by regex on the source. Do not migrate it to any helper.
  - `ToolbarWindow.UpdateTargetPositions` uses a **bare `catch`** (not `InvalidOperationException`-only) plus a dedicated zero-width second pass. Leave its try/catch shape alone; only the edge switch in `SetMode` is in scope.
  - `QuickActionsGridWindow.PositionOnEdge` currently does nothing on an unknown edge; the helper's default arm lands it at top. Accepted change (invalid edge = corrupted settings; a visible window beats an invisible one) ÔÇö stated here so no executor "fixes" it back.
  - Edge switches are unified on honoring `WorkArea.Left/Top` on BOTH axes (today only QuickActions honors the origin, and only on its centered axis). Identical on a single monitor; deliberate multi-monitor correction. QuickActionsGridWindow's XML doc at :108 ("Unknown values leave the position unchanged") goes stale under the unknown-edge fallback and is reworded in Task 1 Step 5 item 2.
  - `ScrollOverlayWindow` currently uses `Screen.PrimaryScreen!` without a null guard; migrating it to the guarded helper is an accepted (strictly safer) change.
- Pipe-name rule (AGENTS.md): any pipe-using service keeps its production name in a `DefaultPipeName`-style const and takes an injectable name; tests use GUID suffixes. `HeartbeatPipeServer` gains the injectable name it currently lacks.
- `benchmarks/EyeCtrl.Benchmarks` is a standalone project not in the solution ÔÇö after Task 6, build it explicitly (`dotnet build benchmarks/EyeCtrl.Benchmarks`) in addition to the solution.
- Before each commit: GitNexus `detect_changes()` (scope `unstaged`), or note why skipped (test-only).

---

### Task 0: Create the worktree

- [ ] **Step 1: Create worktree + branch**

```bash
cd /c/Users/andre/source/repos/eyectrl
if [ ! -d .worktrees/simplify-pass-2 ]; then
    git worktree add .worktrees/simplify-pass-2 -b refactor/simplify-pass-2 origin/main
fi
cd .worktrees/simplify-pass-2
git status -sb   # expect: ## refactor/simplify-pass-2 at 4c84833a, clean
```

---

### Task 1: `OverlayPositioning` ÔÇö edge placement + fullscreen coverage

**Files:**
- Create: `src/EyeCtrl.App/Overlays/OverlayPositioning.cs`
- Create: `tests/EyeCtrl.App.Tests/Overlays/OverlayPositioningTests.cs`
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (SetMode, 145-183), `src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml.cs` (PositionOnEdge, 109-134), `src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml.cs` (SetEdge, 24-66), `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` (RepositionOnScreen, 142-159), `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs` (Show, 83-94), `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml.cs` (Show, 18-50)

**Interfaces:**
- Produces: `internal static class OverlayPositioning` with `(double Left, double Top) PlaceAtEdge(Rect workArea, string edge, double width, double height)` and `void CoverPrimaryScreen(Window window)`.

- [ ] **Step 1: Write the failing tests**

`tests/EyeCtrl.App.Tests/Overlays/OverlayPositioningTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;

public class OverlayPositioningTests
{
    private static readonly Rect Area = new(0, 0, 1920, 1040);

    [Fact]
    public void TopEdge_CentersHorizontallyAtWorkAreaTop()
    {
        var (left, top) = OverlayPositioning.PlaceAtEdge(Area, "top", 400, 60);
        Check.That(left).IsEqualTo((1920 - 400) / 2d);
        Check.That(top).IsEqualTo(0d);
    }

    [Fact]
    public void BottomEdge_CentersHorizontallyAtWorkAreaBottom()
    {
        var (left, top) = OverlayPositioning.PlaceAtEdge(Area, "bottom", 400, 60);
        Check.That(left).IsEqualTo((1920 - 400) / 2d);
        Check.That(top).IsEqualTo(1040d - 60d);
    }

    [Fact]
    public void SideEdges_CenterVertically()
    {
        var (leftL, topL) = OverlayPositioning.PlaceAtEdge(Area, "left", 60, 400);
        Check.That(leftL).IsEqualTo(0d);
        Check.That(topL).IsEqualTo((1040 - 400) / 2d);

        var (leftR, topR) = OverlayPositioning.PlaceAtEdge(Area, "right", 60, 400);
        Check.That(leftR).IsEqualTo(1920d - 60d);
        Check.That(topR).IsEqualTo((1040 - 400) / 2d);
    }

    [Fact]
    public void EdgeCase_IsInsensitive_AndPascalCaseMatches()
    {
        Check.That(OverlayPositioning.PlaceAtEdge(Area, "TOP", 100, 50).Top).IsEqualTo(0d);
        // QuickActionsGridWindow passes PascalCase values ("Left"/"Right"/"Top"/"Bottom").
        Check.That(OverlayPositioning.PlaceAtEdge(Area, "Bottom", 100, 50).Top).IsEqualTo(1040d - 50d);
    }

    [Fact]
    public void UnknownEdge_FallsBackToTop()
    {
        var (left, top) = OverlayPositioning.PlaceAtEdge(Area, "diagonal", 400, 60);
        Check.That(left).IsEqualTo((1920 - 400) / 2d);
        Check.That(top).IsEqualTo(0d);
    }

    [Fact]
    public void NonZeroWorkAreaOrigin_IsHonored()
    {
        var area = new Rect(1920, 0, 1920, 1040); // secondary monitor to the right
        var (left, top) = OverlayPositioning.PlaceAtEdge(area, "top", 400, 60);
        Check.That(left).IsEqualTo(1920d + (1920d - 400d) / 2d);
        Check.That(top).IsEqualTo(0d);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~OverlayPositioningTests"`
Expected: compile error ÔÇö `OverlayPositioning` does not exist.

- [ ] **Step 3: Create the helper**

`src/EyeCtrl.App/Overlays/OverlayPositioning.cs`:

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.Core.Services;

/// <summary>
/// The one canonical implementation of the overlays' screen-edge placement and
/// fullscreen-coverage math. Every overlay previously hand-rolled a WorkArea switch here;
/// the DPI/positioning rules have field-bug history, so they now live in exactly one place.
/// </summary>
internal static class OverlayPositioning
{
    /// <summary>
    /// Where a <paramref name="width"/>├ù<paramref name="height"/> window sits against the
    /// given work area for an edge name. Edge matching is case-insensitive ("top"/"Top");
    /// unknown values fall back to top. The work area's <c>Left</c>/<c>Top</c> origin is
    /// honored (multi-monitor), which only QuickActionsGridWindow did before unification.
    /// </summary>
    public static (double Left, double Top) PlaceAtEdge(Rect workArea, string edge, double width, double height)
    {
        switch (edge?.ToLowerInvariant())
        {
            case "bottom":
                return (workArea.Left + (workArea.Width - width) / 2, workArea.Bottom - height);
            case "left":
                return (workArea.Left, workArea.Top + (workArea.Height - height) / 2);
            case "right":
                return (workArea.Right - width, workArea.Top + (workArea.Height - height) / 2);
            default:
                return (workArea.Left + (workArea.Width - width) / 2, workArea.Top);
        }
    }

    /// <summary>
    /// Sizes <paramref name="window"/> to cover the whole primary monitor (bounds, not
    /// WorkArea ÔÇö the backdrop must cover the taskbar too). Null-guarded: a missing primary
    /// screen leaves the window's geometry untouched rather than throwing.
    /// </summary>
    public static void CoverPrimaryScreen(Window window)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null)
            return;

        window.Left = screen.Bounds.X / DpiHelper.ScaleX;
        window.Top = screen.Bounds.Y / DpiHelper.ScaleY;
        window.Width = screen.Bounds.Width / DpiHelper.ScaleX;
        window.Height = screen.Bounds.Height / DpiHelper.ScaleY;
    }
}
```

- [ ] **Step 4: Run the new tests ÔÇö green**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~OverlayPositioningTests"` ÔÇö expect 6/6.

- [ ] **Step 5: Migrate the four edge sites**

Each migration replaces ONLY the Left/Top assignment; per-site extras stay at the call site.

1. `ToolbarWindow.SetMode` (157-181): the switch becomes

```csharp
var (left, top) = OverlayPositioning.PlaceAtEdge(screen, edge, ActualWidth, ActualHeight);
Left = left;
Top = top;
ButtonPanel.Orientation = edge.ToLowerInvariant() is "left" or "right"
    ? Orientation.Vertical
    : ButtonPanel.Orientation;
```

Keep the surrounding code intact: the `UpdateLayout()` call, and ÔÇö IMPORTANT ÔÇö the existing `if (isDocked) { ... }` BLOCK shape with the switch inside it, followed by the UNCONDITIONAL `_targetsInitialized = false;` (line 183). There is no early return today and none may be introduced: a floating-mode SetMode still resets the latch so UpdateGaze re-syncs targets. Keep the `screen` local and pass it to the helper.

2. `QuickActionsGridWindow.PositionOnEdge` (109-134): the three placement branches collapse to one call using the existing fallback sizing at lines 112-113 verbatim (`double gridW = ActualWidth > 0 ? ActualWidth : Width; var gridH = ...;` ÔÇö keep the file's own `gridW`/`gridH` names), then `(Left, Top) = OverlayPositioning.PlaceAtEdge(SystemParameters.WorkArea, edge, gridW, gridH);`. Also reword the method's XML doc at :108, which currently promises unknown edges leave the position unchanged (no longer true under the helper's top fallback).

3. `PauseIndicatorWindow.SetEdge` (24-66): keep the Width/Height orientation sizing (the `BarThicknessLogical` logic) exactly as is; each arm's final `Left = ...; Top = ...;` pair is replaced by the helper call with the already-computed width/height. The default arm keeps its "top bar" sizing and calls the helper with `"top"`.

4. `KeyboardWindow.RepositionOnScreen` (142-159): keep the `if (ActualWidth <= 0 || ActualHeight <= 0) return;` early-out, then:

```csharp
var edge = string.Equals(_screenEdge, "top", StringComparison.OrdinalIgnoreCase) ? "top" : "bottom";
(Left, Top) = OverlayPositioning.PlaceAtEdge(SystemParameters.WorkArea, edge, ActualWidth, ActualHeight);
```

(keyboard keeps its bottom-is-default semantics, as today.)

- [ ] **Step 6: Migrate the two coverage sites**

1. `GlassBackdropWindow.Show` (83-94): replace the four `screen.Bounds ... / DpiHelper.ScaleX/Y` lines with `OverlayPositioning.CoverPrimaryScreen(this);`, but KEEP the null-screen early return as `if (System.Windows.Forms.Screen.PrimaryScreen == null) return;` BEFORE it ÔÇö today a null primary screen makes Show a complete no-op (it returns before `base.Show()`), and the guarded helper alone would show the window at stale geometry. The `base.Show(); EnsureHandle();` tail stays.
2. `ScrollOverlayWindow.Show` (18-50): the `var screen = ...PrimaryScreen!;` line and the four bounds assignments (23-26) are replaced by `OverlayPositioning.CoverPrimaryScreen(this);`. The subsequent `OverlayCanvas` sizing (which uses the window's now-set Width/Height) and the `base.Show(); Topmost = true;` tail stay.

- [ ] **Step 7: Build, run App tests, drift guards**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: green, including `ToolbarButtonDriftTests` (button ids untouched) and `TrackStatusFitTests` (that XAML is untouched).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor(overlays): one canonical edge-placement and screen-coverage helper"
```

---

### Task 2: `TryGetScreenCenter` for the PointToScreen loops

Scope: only the sites that already swallow `InvalidOperationException`. `ToolbarWindow` (bare catch + zero-width latch ÔÇö out of scope), `TaskSwitcherWindow` (no catch + drift-test pin ÔÇö out of scope), `QuickActionsGridWindow` (no catch ÔÇö out of scope; adding a swallow would be a behavior change) stay as they are.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/OverlayPositioning.cs` (add the helper)

OUT OF SCOPE: `KeyboardWindow.IsGazeInsideKeyArea` (392-405) looks like this pattern but calls `PointToScreen(new Point(0, 0))` ÔÇö the panel's top-LEFT corner for a bounds test, not a center. Do not migrate it.
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` (UpdateKeyScreenPositions 371-385, UpdatePredictionTargetPositions 407-437), `src/EyeCtrl.App/Overlays/EmojiGridManager.cs` (UpdateCategoryTargetPositions 267-300)

- [ ] **Step 1: Add the helper to `OverlayPositioning`**

```csharp
/// <summary>
/// A child element's centre in physical screen pixels ÔÇö the input every dwell-detector
/// push loop needs. False (with <c>center</c> defaulted) when the element is not yet
/// rendered or not in the visual tree, the same condition the per-window try/catch
/// swallowed before. The result is PHYSICAL: PointToScreen already returns physical
/// pixels, so no DpiHelper conversion may be applied to it.
/// </summary>
public static bool TryGetScreenCenter(System.Windows.FrameworkElement element, out System.Windows.Point center)
{
    try
    {
        center = element.PointToScreen(
            new System.Windows.Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return true;
    }
    catch (InvalidOperationException)
    {
        center = default;
        return false;
    }
}
```

- [ ] **Step 2: Migrate the three in-scope loops**

1. `KeyboardWindow.UpdateKeyScreenPositions` (371-385): the per-key `try { var pos = ...PointToScreen...; _keyScreenPositions[id] = ...; } catch (InvalidOperationException) { }` becomes `if (OverlayPositioning.TryGetScreenCenter(container, out var pos)) _keyScreenPositions[id] = new PhysicalPoint(pos.X, pos.Y);` (adjust to the loop's actual variable names; keep the loop structure and any existing comments about latching).
2. `KeyboardWindow.UpdatePredictionTargetPositions` (407-437): same substitution (it is a single pass); the `AddTarget` vs `UpdateTargetCenter` latch logic and the radius math (`DpiHelper.LogicalToPhysicalX(halfDiag)`) stay untouched.
3. `EmojiGridManager.UpdateCategoryTargetPositions` (267-300): same substitution; keep the `_categoryTargetsInitialized` latch and the `Math.Sqrt(2)` radius line (285).

Note: today's `catch (InvalidOperationException)` in these loops also covered the `AddTarget`/`UpdateTargetCenter` calls inside the same try. The helper narrows the swallow to the `PointToScreen` call itself; those calls do not throw when the element was renderable, so this is inert ÔÇö but keep any pre-existing try/catch that ALSO wrapped detector calls if the loop shape needs it.

- [ ] **Step 3: Build, test, commit**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
git add -A
git commit -m "refactor(overlays): one PointToScreen-center helper for the dwell sync loops"
```

Expected: green. `TaskSwitcherDwellPositionTests` must be untouched by construction (no TaskSwitcher edit).

---

### Task 3: `CoordinatorBase` for the 9 coordinators

**Files:**
- Create: `src/EyeCtrl.App/Services/Coordinators/CoordinatorBase.cs`
- Modify (all in `src/EyeCtrl.App/Services/Coordinators/`): `DragCoordinator.cs`, `KeyboardCoordinator.cs`, `KeyboardInteractionDispatcher.cs`, `OverlayMessageCoordinator.cs`, `OverlayVmBindingCoordinator.cs`, `QuickActionDispatcher.cs`, `QuickActionsCoordinator.cs`, `SettingsUiCoordinator.cs`, `ToolbarLifecycleCoordinator.cs`

**Interfaces:**
- Produces: `internal abstract class CoordinatorBase : IDisposable` with `public void Initialize(EventCoordinationContext context)`, `protected EventCoordinationContext? Context`, `protected abstract void OnInitialize()`, sealed-template `Dispose()` calling `protected virtual void OnDispose()` then `Context?.Messenger.UnregisterAll(this)`.

- [ ] **Step 1: Create the base**

```csharp
namespace EyeCtrl.App.Services.Coordinators;

/// <summary>
/// The lifecycle skeleton all nine coordinators shared by hand: a nullable context set
/// exactly once by a null-checked <see cref="Initialize"/>, an idempotent Dispose that
/// runs per-class cleanup first (<see cref="OnDispose"/>) and unregisters from the
/// messenger last ÔÇö skipped when never initialized. OnDispose runs even for a
/// coordinator that was never initialized: two of the nine (Keyboard, SettingsUi) own
/// resources (a prediction factory, a settings window) whose teardown must not depend
/// on Initialize having run.
/// </summary>
internal abstract class CoordinatorBase : IDisposable
{
    protected EventCoordinationContext? Context { get; private set; }
    private bool _disposed;

    public void Initialize(EventCoordinationContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        OnInitialize();
    }

    protected abstract void OnInitialize();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        OnDispose();
        Context?.Messenger.UnregisterAll(this);
    }

    protected virtual void OnDispose() { }
}
```

- [ ] **Step 2: Migrate the six standard coordinators**

For `DragCoordinator`, `KeyboardInteractionDispatcher`, `OverlayMessageCoordinator`, `QuickActionDispatcher`, `QuickActionsCoordinator`, `ToolbarLifecycleCoordinator`:
1. Base list becomes `: CoordinatorBase`.
2. Delete the `private EventCoordinationContext? _context;` and `private bool _disposed;` fields.
3. `Initialize(...)` is deleted (inherited); its body becomes `protected override void OnInitialize()`, whose FIRST statement is `var context = Context!;` ÔÇö five of the nine bodies reference the old `context` PARAMETER local (not `_context`), e.g. `KeyboardCoordinator.cs:56,60`, which no longer exists in the override. Inside the body, rewrite BOTH `_context.` and bare `context.` member accesses to `context.` (the local), which also silences the CS8602 the nullable `Context` property would otherwise emit on every access.
4. THEN rename every remaining `_context` reference FILE-WIDE (there are ~330 across the nine files ÔÇö `OverlayVmBindingCoordinator` alone has ~90, and most are in domain methods, NOT Initialize): `_context.` ÔåÆ `Context.`, `_context?.` ÔåÆ `Context?.`, `_context == null` ÔåÆ `Context is null`. The `var context = Context!;` local is scoped to OnInitialize ONLY ÔÇö do not let it leak. The rename is safe for the drift pins: `TaskSwitcherDwellPositionTests` matches `ToolbarVM.SetActive(false)` and the `OnTaskSwitcherVMPropertyChanged` name, both of which survive.
5. `Dispose()` is deleted where it matches the skeleton exactly (`if (_disposed || _context is null) { _disposed = true; return; } ... UnregisterAll ... _disposed = true;`). For `QuickActionsCoordinator`, `DragCoordinator`, `ToolbarLifecycleCoordinator`, whose Dispose has extra unsubscribes BEFORE `UnregisterAll`, those extra statements move into a `protected override void OnDispose()` (in their original order); the skeleton lines themselves are deleted.

- [ ] **Step 3: Migrate the three deviators**

0. In `DragCoordinator` and `OverlayVmBindingCoordinator`, the OnDispose overrides MUST begin with `if (Context is null) return;` ÔÇö their cleanup dereferences context members (`Context.InteractionManager`, `Context.CursorVM`, ÔÇª) and `Dispose_CanBeCalledBeforeInitialize_WithoutThrowing` exists for both (DragCoordinatorTests:17, OverlayVmBindingCoordinatorTests:22). Without the guard the base's OnDispose-even-when-uninitialized rule NREs there.
1. `KeyboardCoordinator` (Dispose 227-243): `_predictionFactory.Dispose()` (unconditional) moves first into `OnDispose()`, then the SkinToneChanged unhook and any other cleanup. The old `if (_context is not null)` guard becomes an explicit `if (Context is not null)` around the SkinToneChanged unhook ÔÇö the member is `Context.OverlayManager.Keyboard.SkinToneChanged` (KeyboardCoordinator.cs:238), a chain that cannot use `?.` in a compound `-=` assignment. The factory dispose stays outside the guard (it is field-initialized, not context-dependent).
2. `SettingsUiCoordinator` (Dispose 212-237): the `_settingsWindow` close (with Closed-handler detach and suppressed exception) moves into `OnDispose()`, before the context cleanup ÔÇö preserving today's order where the window closes even when uninitialized. Its OnDispose ENDS with the TrackStatus unhook the base does not do: `if (Context is not null) Context.Settings.TrackStatus.PropertyChanged -= OnTrackStatusSettingsPropertyChanged;` (today at SettingsUiCoordinator.cs:234 ÔÇö omitting it leaks the handler).
3. `OverlayVmBindingCoordinator` (Dispose 361-380): the 9 PropertyChanged unsubscribes + `_glassBackdrop` hide move into `OnDispose()`. It never registers with the messenger, so the base's `Context?.Messenger.UnregisterAll(this)` is a harmless no-op ÔÇö leave it.

- [ ] **Step 4: Build and run the coordinator tests**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: green ÔÇö the nine coordinator test files construct coordinators with plain `new` and assert ctor null-args; nothing reflects over fields. Also: `OnTaskSwitcherVMPropertyChanged` in `OverlayVmBindingCoordinator` must KEEP its exact name ÔÇö `TaskSwitcherDwellPositionTests.Switcher_DeactivatesToolbarDwell_WhileCoveringIt` scans that file by method name. (verified: the classes' docs say "no reflection"). If any test calls `Initialize(null)` expecting `ArgumentNullException`, it still passes (base throws).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(coordinators): one Initialize/Dispose skeleton for all nine"
```

---

### Task 4: Shared `NewlineJsonPipeServer` (Platform)

The riskiest task: the two servers differ in pipe construction (ACL vs plain), line parsing, identity binding, and per-connection teardown. The helper parameterizes exactly those; everything identical (Start idempotence, accept loop, UTF-8 line framing with malformed-line tolerance, connection tracking, drain-on-dispose) is written once. If during implementation the seam does not fit, STOP and report BLOCKED with specifics rather than distorting either server's semantics.

**Files:**
- Create: `src/EyeCtrl.Platform/Ipc/NewlineJsonPipeServer.cs`
- Create: `tests/EyeCtrl.Supervisor.Tests/Ipc/HeartbeatPipeServerTests.cs` (Step 0)
- Modify: `src/EyeCtrl.App/Services/ExternalDwellService.cs`, `src/EyeCtrl.Supervisor/Ipc/HeartbeatPipeServer.cs`

**Interfaces:**
- Produces: `public sealed class NewlineJsonPipeServer : IDisposable` in `EyeCtrl.Platform.Ipc` with ctor `(string pipeName, Func<NamedPipeServerStream> createPipe, LineHandler handleLine, Action<NamedPipeServerStream>? onConnected = null, Action<NamedPipeServerStream>? onDisconnected = null, Action<Exception, string?>? log = null)` where `public delegate bool LineHandler(string line, NamedPipeServerStream pipe)` (return false = malformed, skip the line and keep the connection) and the log callback receives the offending line (or null) so callers keep their per-line diagnostics. Members: `void Start()`, `void Dispose()`.

- [ ] **Step 0: Write the missing HeartbeatPipeServer smoke test first**

`HeartbeatPipeServer` has ZERO tests today ÔÇö the riskiest rewrite in this plan is unpinned. Two preparation moves FIRST (both trivial, zero-risk, and required for an isolated test):

a. Add the injectable pipe name and keep the binder: change the ctor to `public HeartbeatPipeServer(ClientIdentityBinder binder, string? pipeName = null)` ÔÇö the binder parameter is MANDATORY and already required by `Program.cs:471` (`new HeartbeatPipeServer(binder)`); only the optional `pipeName` (defaulting to the `PipeName` const) is new. Spell it out fully: add `private readonly string _pipeName;`, assign `_pipeName = pipeName ?? PipeName;` in the ctor, and REPLACE the hardcoded `PipeName` const use at `HeartbeatPipeServer.cs:90` (the pipe construction) with `_pipeName` ÔÇö without that last step the smoke test binds the production `eyectrl_watch` name.
b. Then add `tests/EyeCtrl.Supervisor.Tests/Ipc/HeartbeatPipeServerTests.cs`, green against the CURRENT implementation: GUID-suffixed pipe name (`"eyectrl_watch_test_" + Guid.NewGuid().ToString("N")`, per AGENTS.md ÔÇö never the production `eyectrl_watch`, which a live Supervisor or sibling worktree can steal); `new HeartbeatPipeServer(ClientIdentityBinder.ForAdoption(Environment.ProcessPath!), pipeName)` ÔÇö the adoption binder matching the test process's own exe path is what makes a same-process client resolve a NON-NULL identity (the default binder matches the app's exe and would resolve null here); `Start()`; connect a `NamedPipeClientStream`; write ONE valid heartbeat line (copy the minimal valid JSON from `HeartbeatPayloadParserTests`) + newline; flush; poll with a 2s deadline until `HeartbeatReceived` fired; assert payload non-null AND identity non-null. Then send a SECOND line and assert the identity object is the SAME ÔÇö this is what pins the per-connection caching the plan calls its top design risk. Write the payload builder and poll helper concretely ÔÇö no placeholders.

Run: `dotnet test tests/EyeCtrl.Supervisor.Tests` ÔÇö the new test must pass against the CURRENT server before proceeding. After Step 3's rewrite, run it again unchanged.

- [ ] **Step 1: Create the helper**

```csharp
namespace EyeCtrl.Platform.Ipc;

using System.IO.Pipes;
using System.Text;

/// <summary>
/// The shared shape of EyeCtrl's two pipe servers (ExternalDwellService in the App,
/// HeartbeatPipeServer in the Supervisor): an idempotent Start, an accept loop over
/// multi-instance pipes, per-connection UTF-8 newline-JSON read loops that skip
/// malformed lines without dropping the connection, connection tracking under a lock,
/// and a Dispose that cancels, drains and disposes every connection. Callers own pipe
/// construction (plain vs ACL-secured), line parsing, and any per-connection teardown.
/// </summary>
public sealed class NewlineJsonPipeServer : IDisposable
{
    public delegate bool LineHandler(string line, NamedPipeServerStream pipe);

    private readonly string _pipeName;
    private readonly Func<NamedPipeServerStream> _createPipe;
    private readonly LineHandler _handleLine;
    private readonly Action<NamedPipeServerStream>? _onConnected;
    private readonly Action<NamedPipeServerStream>? _onDisconnected;
    private readonly Action<Exception, string?>? _log;
    private readonly object _stateLock = new();
    private readonly List<NamedPipeServerStream> _connections = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public NewlineJsonPipeServer(
        string pipeName,
        Func<NamedPipeServerStream> createPipe,
        LineHandler handleLine,
        Action<NamedPipeServerStream>? onConnected = null,
        Action<NamedPipeServerStream>? onDisconnected = null,
        Action<Exception, string?>? log = null)
    {
        _pipeName = pipeName;
        _createPipe = createPipe;
        _handleLine = handleLine;
        _onConnected = onConnected;
        _onDisconnected = onDisconnected;
        _log = log;
    }

    public void Start()
    {
        if (_cts != null)
            return;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = _createPipe();
            }
            catch (Exception ex)
            {
                // Pipe creation failed (e.g. ACL). Abort the loop, matching
                // HeartbeatPipeServer: retrying a construction that cannot succeed
                // would spin.
                _log?.Invoke(ex, null);
                break;
            }

            try
            {
                await pipe.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke(ex, null);
                pipe.Dispose();
                continue;
            }

            lock (_stateLock)
                _connections.Add(pipe);
            try
            {
                // The caller builds its per-connection state (writer, identity, bookkeeping)
                // here ÔÇö before the first line can arrive. Without this seam the line handler
                // could never map a pipe to its state.
                _onConnected?.Invoke(pipe);
            }
            catch (Exception ex)
            {
                _log?.Invoke(ex, null);
            }
            _ = HandleConnectionAsync(pipe, token);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break; // read failure: drop this connection
                }
                if (line == null)
                    break;

                try
                {
                    // Malformed line -> handler returns false -> logged with the line, skipped,
                    // connection kept.
                    if (!_handleLine(line, pipe))
                        _log?.Invoke(new FormatException("malformed pipe line"), line);
                }
                catch (Exception ex)
                {
                    _log?.Invoke(ex, line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.Invoke(ex, null);
        }
        finally
        {
            lock (_stateLock)
                _connections.Remove(pipe);
            try
            {
                _onDisconnected?.Invoke(pipe);
            }
            catch (Exception ex)
            {
                _log?.Invoke(ex, null);
            }
            try
            {
                pipe.Dispose();
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        lock (_stateLock)
        {
            foreach (var c in _connections)
            {
                try
                {
                    c.Dispose();
                }
                catch
                {
                }
            }
            _connections.Clear();
        }
        _cts?.Dispose();
    }
}
```

Note the `LineHandler` contract: the CALLEE parses; a parse failure is signalled by returning false (not throwing), and a handler-side exception is caught and logged without dropping the connection. The two sides use the two mechanisms differently: ExternalDwell signals malformed lines by letting `JsonException` ESCAPE the handler (helper's per-line catch logs it with the line and keeps the connection ÔÇö the `"bad pipe message: {Line}"` diagnostic is preserved); `return false` is the Heartbeat side's mechanism (its parser returns null rather than throwing). Losing the generic `"connection handler error"` wrapper message is acceptable (it never fired on a distinguishable condition).

- [ ] **Step 2: Migrate `ExternalDwellService`**

1. Replace `_cts` and DELETE BOTH METHODS WHOLE ÔÇö `AcceptLoopAsync` (213-231) and `HandleConnectionAsync` (233-263) ÔÇö moving the read-loop framing into the helper and the `finally` block (248-262) into `TeardownConnection` (item 3). The service KEEPS its `_connections` list and its `_stateLock` (both still used below); delete the `_cts` field (:59) and its Dispose references (:348, :355). Declare the pipeÔåÆconnection map next to them: `private readonly ConcurrentDictionary<NamedPipeServerStream, PipeConnection> _connectionsByPipe = new();` (concurrent: `onConnected`/`TeardownConnection` write it while `handleLine` reads it ÔÇö a plain Dictionary would be a read-during-write hazard across simultaneous connections). The service's `NewlineJsonPipeServer` field is constructed in the service's ctor:

```csharp
_pipeServer = new NewlineJsonPipeServer(
    _pipeName,
    () => new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
    onConnected: pipe =>
    {
        // Mirror today's accept-time construction (ExternalDwellService.cs:227-228): the
        // PipeConnection owns the outbound writer SendActivated uses, and it must exist before
        // the first line arrives.
        var conn = new PipeConnection(pipe, ex => _logger.LogWarning(ex, "pipe write failed"));
        lock (_stateLock)
            _connections.Add(conn);
        _connectionsByPipe[pipe] = conn;
    },
    handleLine: (line, pipe) =>
    {
        // TryGetValue, not the indexer: if onConnected ever failed (the helper logs and
        // continues), the connection has no state ÔÇö treat its lines as handled no-ops rather
        // than throwing into the per-line catch forever.
        if (!_connectionsByPipe.TryGetValue(pipe, out var conn))
            return true;
        var msg = JsonSerializer.Deserialize<WireMessage>(line, WireJson.Options);
        if (msg is null)
            return true; // deserialized null: today's HandleMessage silently no-ops on it
        HandleMessage(msg, conn);
        return true;
    },
    onDisconnected: pipe =>
    {
        if (_connectionsByPipe.TryRemove(pipe, out var conn))
            TeardownConnection(conn);
    },
    log: (ex, line) => _logger.LogWarning(ex, "bad pipe message: {Line}", line));
```

(Adjust names to the real ones: the existing `PipeConnection` wrapper, its collection, and the per-target teardown in today's `finally` block move into `TeardownConnection`/`_connectionsByPipe` bookkeeping. The garbage-line test in `ExternalDwellWireContractTests` pins that a malformed line is logged and skipped with the connection kept alive ÔÇö the `return false` path preserves it. `_cts`-based `Start()` becomes `_pipeServer.Start()`.)

2. `Dispose()` (342-357) keeps its service-specific statements (CursorMoved unhook, ReleaseSuppression, dispatcher feedback close), calls `_pipeServer.Dispose();`, AND KEEPS its own lock-and-clear of `_connections` and `_connByTarget` ÔÇö the ONLY two collections today's Dispose clears (the per-target `_targetsById`/`_hwndById`/detector teardown lives in the per-connection `finally`, which is `TeardownConnection` now ÔÇö see item 3). Also delete the now-unused `_cts` field (:59) and its two Dispose references (:348, :355) once `_pipeServer.Start()`/`_pipeServer.Dispose()` replace them.

3. `TeardownConnection(conn)` is today's `finally` block (248-262) with one change of address: `onDisconnected` above removes the `_connectionsByPipe` entry itself (`TryRemove` ÔÇö no lock needed on the ConcurrentDictionary), so `TeardownConnection` does the rest verbatim: remove `conn` from `_connections` under `_stateLock`, remove per-target state (`_connByTarget`/`_targetsById`/`_hwndById`/detector targets), and END with `conn.Dispose()` ÔÇö `PipeConnection.Dispose` disposes the outbound `StreamWriter`, which must not leak.

- [ ] **Step 3: Migrate `HeartbeatPipeServer`**

1. The injectable pipe name and binder-preserving ctor were already added in Step 0a ÔÇö verify nothing else in this step re-declares them.
2. Replace its Start/AcceptLoopAsync/HandleConnectionAsync/Dispose (73-175) with a `NewlineJsonPipeServer` field. Identity resolution moves to `onConnected` and is CACHED PER PIPE ÔÇö mandatory for CORRECTNESS, not cost: `ClientIdentityBinder.Resolve` re-reads live OS state (GetNamedPipeClientProcessId ÔåÆ OpenProcess ÔåÆ QueryFullProcessImageName ÔåÆ GetProcessTimes), so an exiting client resolves to null on a later line where today's per-connection latch held a valid identity; `Program.cs:288` gates the clean-exit path on `ClientVerified`, and the `ShuttingDown` payload is precisely the last line of a dying process ÔÇö per-line resolution could turn a clean exit into an unverified one and trip crash recovery:

```csharp
private readonly ConditionalWeakTable<NamedPipeServerStream, ClientIdentity> _identityByPipe = new();

_pipeServer = new NewlineJsonPipeServer(
    _pipeName,
    () => NamedPipeServerStreamAcl.Create(
        _pipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
        inBufferSize: 0, outBufferSize: 0,
        new NamedPipeSecurityBuilder().BuildCurrentUserOnly()),
    onConnected: pipe =>
    {
        try { _identityByPipe.Add(pipe, _binder.Resolve(pipe.SafePipeHandle)); }
        catch { /* unresolvable identity = null identity, same as today's latch */ }
    },
    handleLine: (line, pipe) =>
    {
        var payload = HeartbeatPayloadParser.Parse(line);
        if (payload is null)
            return false;
        _identityByPipe.TryGetValue(pipe, out var identity);
        HeartbeatReceived?.Invoke(payload, identity);
        return true;
    });
```

Keep the `HeartbeatReceived` event's public shape. The `_pipeServer` field is DECLARED at class level but ASSIGNED IN THE CTOR, after `_pipeName` and `_binder` ÔÇö as a field initializer it would be CS0236 (it reads instance fields). Add `using System.Runtime.CompilerServices;` for the `ConditionalWeakTable`. Note: `_identityByPipe.Add(pipe, _binder.Resolve(...))` emits CS8604 (`Resolve` returns `ClientIdentity?`) ÔÇö harmless here (not error-elevated in Supervisor, and the surrounding catch converts a throw to the same null-identity outcome as today); do NOT "fix" it by dropping the entry. Dispose becomes `_pipeServer.Dispose();`.

- [ ] **Step 4: Run the wire tests (App project, whole project)**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Supervisor.Tests
```

Expected: green ÔÇö `ExternalDwellWireContractTests` (real DwellClient + raw client + garbage-line framing) and `ExternalDwellServiceTests` (GUID pipes) are the behavioral pins; `HeartbeatPayloadParserTests` is untouched.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(ipc): one newline-JSON pipe server behind dwell and heartbeat"
```

---

### Task 5: `ManualSelectionDwellService.DispatchSafe`

**Files:**
- Modify: `src/EyeCtrl.App/Services/ManualSelectionDwellService.cs` (helper 156-165; wrappers 171-196, 203-214, 232-242, 247-257, 262-272)

- [ ] **Step 1: Replace the helper**

Rename `InvokeOnDispatcherIfNeeded` to `DispatchSafe(Action action, string errorContext)`:

```csharp
private void DispatchSafe(Action action, string errorContext)
{
    void Run()
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Context}", errorContext);
        }
    }

    if (_dispatcher.CheckAccess())
        Run();
    else
        _dispatcher.BeginInvoke(Run);
}
```

- [ ] **Step 2: Collapse the five wrappers**

Each becomes `DispatchSafe(() => { ... }, "<its exact existing message>");` with the body preserved verbatim ÔÇö most are a single statement, but `OnDwellActivated` (171-196) has a five-statement body with an early `return` and needs a block lambda. Keep the five message strings verbatim: `"Error in ManualSelection dwell activated handler"`, `"Error in ManualSelection dwell progress handler"`, `"Error resetting ManualSelection indicator"`, `"Error updating ManualSelection indicator position"`, `"Error updating ManualSelection indicator progress"`.

- [ ] **Step 3: Build, test, commit**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
git add -A
git commit -m "refactor(manualselection): one dispatch-and-log wrapper for the five handlers"
```

---

### Task 6: Delete the eight test-only members

All eight re-verified pure-minus at `4c84833a` (zero src/ callers; only their own tests, plus two benchmarks). Two require test REWRITES rather than deletions because they are the only assertion path for live behavior.

**Files (source):**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` (delete `SetPredictor`, 171-175)
- Modify: `src/EyeCtrl.Core/Prediction/SentenceCompletionIndex.cs` (delete `BoostCompletion` 73ff, `DismissCompletion` 102ff)
- Modify: `src/EyeCtrl.Core/Prediction/ModelDownloadService.cs` (delete `GetAvailableModels` 62ff, `CancelDownload` 131ff)
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs` (delete `UpdateTargetDwellTime` 118ff)
- Modify: `src/EyeCtrl.Core/Prediction/CapitalizationHelper.cs` (delete both `ApplySmartCapitalization` overloads, 104ff + 117ff)
- Modify: `src/EyeCtrl.Core/Prediction/CasingHelper.cs` (delete `AddAcronym` 85ff)
- Modify: `src/EyeCtrl.Core/Prediction/CasingPreferenceTracker.cs` (delete `GetVariants` 81ff)
- Modify: `AGENTS.md` (Step 5: benchmark count)

**Files (tests/benchmarks):**
- `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs` ÔÇö REWRITE, not delete
- `tests/EyeCtrl.Core.Tests/Prediction/SentenceCompletionIndexTests.cs` ÔÇö delete `BoostCompletion_IncreasesFrequency` (142), `DismissCompletion_DecreasesFrequency` (154)
- `benchmarks/EyeCtrl.Benchmarks/SentenceCompletionBenchmarks.cs` ÔÇö delete the ONE benchmark method using Boost/Dismiss, `BoostAndDismiss()` (64-69)
- `tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs` ÔÇö delete `GetAvailableModels_NoModelsDirectory_ReturnsEmpty` (8), `GetAvailableModels_WithValidFiles_ReturnsModelSizes` (24), `GetAvailableModels_WithOnnxButNoTokenizer_ExcludesModel` (125), `CancelDownload_CancelsTokenizerDownload` (276), AND the `EndlessStream` helper (246-274) whose only use is inside `CancelDownload_CancelsTokenizerDownload`
- `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs` ÔÇö delete ONLY the one `[Fact]` `UpdateTargetDwellTime_ReEvaluatesWithoutResettingProgress` (lines 8-25). The file has SIX other `[Fact]`s covering the LIVE `UpdateTargetGeometry` API ÔÇö they stay, and the file stays.
- `tests/EyeCtrl.Core.Tests/Prediction/CapitalizationHelperTests.cs` ÔÇö delete the 8 `ApplySmartCapitalization_*` tests (198, 208, 218, 228, 238, 315, 328, 352)
- `benchmarks/EyeCtrl.Benchmarks/CasingBenchmarks.cs` ÔÇö delete the benchmark method calling `ApplySmartCapitalization` (the method spanning 39-43; ~42 is its call line)
- `tests/EyeCtrl.Core.Tests/Prediction/CasingHelperTests.cs` ÔÇö delete `AddAcronym_CustomAcronym_BecomesKnown` (84)
- `tests/EyeCtrl.Core.Tests/Prediction/CasingPreferenceTrackerTests.cs` ÔÇö REWRITE two tests, delete one

- [ ] **Step 1: Rewrite the `SetPredictor` tests onto `SetOrchestrator`**

`SetPredictor(p)` is exactly `SetOrchestrator(p != null ? new PredictionOrchestrator(p) : null)`. In `KeyboardViewModelTests.cs`, replace every `vm.SetPredictor(wp)` with `vm.SetOrchestrator(new PredictionOrchestrator(wp));` ÔÇö 9 call sites (lines 542, 561, 586, 609, 628, 661, 685, 702, AND 730 ÔÇö the last inside `GetSuggestionsWithContext_PassesFullTextForSmartCapitalization`); there are NO `SetPredictor(null)` call sites. `GetSuggestionsWithContext_ReturnsEmptyWhenNoPredictorSet` (the 10th test) never calls SetPredictor and is untouched. The test file has no `using EyeCtrl.Core.Prediction;` (it fully qualifies `WordPredictor`), so add that using or fully qualify `PredictionOrchestrator`. Equivalence is exact: every site constructs a fresh instance, so `SetOrchestrator`'s `ReferenceEquals` early-return can never fire. All 10 tests survive with unchanged assertions. Then delete `SetPredictor` from the VM. Run: `dotnet test tests/EyeCtrl.ViewModels.Tests` ÔÇö green.

- [ ] **Step 2: Rewrite the two `GetVariants`-asserting tests onto `GetAllPreferences`**

In `CasingPreferenceTrackerTests.cs`: `RecordCasing_MultipleVariants_TracksFrequencies` (19) and `RecordCasing_CaseInsensitiveKey_AllVariantsMappedTogether` (85) currently assert via `GetVariants(word)`. `GetAllPreferences()` (line 99, production API returning `Dictionary<string, Dictionary<string, int>>`) exposes the same data keyed by the lowercased word ÔÇö rewrite each assertion to read the variants dictionary out of `GetAllPreferences()["<key>"]` (the same key `GetVariants` used internally; check lines 81-98 for the exact casing key before writing). Delete `GetVariants_NoPreference_ReturnsEmptyDictionary` (100). Then delete `GetVariants`. Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CasingPreferenceTracker"` ÔÇö green.

- [ ] **Step 3: Delete the remaining pure-minus members and their tests**

Straight deletions per the file list above (source member + its listed tests + the benchmark methods ÔÇö `SentenceCompletionBenchmarks` has ONE method using Boost/Dismiss, `BoostAndDismiss()` at 64-69, not two). After each project's deletions, run that project's suite; at the end, also build the standalone benchmarks project:

```bash
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
dotnet build benchmarks/EyeCtrl.Benchmarks
```

Expected: all green. Also reword the doc comment at `ModelDownloadService.cs:142` whose `<see cref="CancelDownload"/>` would dangle (CS1574 ÔÇö not build-breaking since only CS1591 is error-elevated, but stale). NOTE: `WordPredictor`'s live capitalization path uses `ShouldCapitalize`/`ApplyCapitalization` ÔÇö verify those are untouched by the `ApplySmartCapitalization` deletion (they are separate members in the same file; delete ONLY the two named overloads and any private helper used exclusively by them ÔÇö grep each private helper's other callers before removing).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(core,viewmodels): delete eight members only tests ever called"
```

- [ ] **Step 5: Update AGENTS.md's benchmark count**

Task 6 removes two `[Benchmark]` methods (`BoostAndDismiss`, one Casing benchmark), so AGENTS.md's "74 benchmarks total" becomes **72** (18 classes unchanged ÔÇö both classes retain other benchmarks); update the count in the same commit or a follow-up `docs:` commit on this branch.

---

### Task 7: Final gate

- [ ] **Step 1: Full build + full non-UI test run** (sequential):

```bash
dotnet build EyeCtrl.slnx
dotnet test --filter "Category!=UI"
dotnet build benchmarks/EyeCtrl.Benchmarks
```

- [ ] **Step 2: Blast radius** ÔÇö GitNexus `detect_changes({scope: "compare", base_ref: "main"})`. Expected changed symbols: the overlay windows' positioning methods, the 9 coordinators, the two pipe servers, MSDS handlers, the 8 deleted members. Nothing outside overlays/coordinators/IPC/prediction-test areas; any other production execution flow appearing = stop and investigate.

- [ ] **Step 3: Report** ÔÇö lines per commit, test counts, deviations (Task 4's identity-binding delta is the known candidate), leftover deferred minors.

---

## Self-review notes

- Scope honestly reduced vs. the survey after the deep-dive: fade-then-hide (only 2 true sites, `new`-hidden vs. not, one generation-guarded ÔÇö a helper would be longer than the duplication), `EnsurePositioned` latch (2 sites with different anchor math), ToolbarWindow's bare-catch sync loop, TaskSwitcher/QuickActions no-catch loops, and the `NoteManager`/centering sites (Y-math differs everywhere) are all OUT ÔÇö forcing them into helpers would trade real divergence for cosmetic line counts. The plan says which sites are excluded and why, so no executor "finishes" them.
- Two intentional behavior changes, both stated in Global Constraints: unknown-edge fallback (no-op ÔåÆ top) and WorkArea-origin honoring for the three windows that ignored it; plus ScrollOverlayWindow gaining the missing null guard. Everything else is behavior-preserving by construction, pinned by: OverlayPositioningTests (new), ExternalDwellWireContractTests, the coordinator test files, ToolbarButtonDriftTests, TaskSwitcherDwellPositionTests (untouched by scope).
- Task 4 carries the only design risk (identity-resolution per-line vs per-connection; PipeConnection bookkeeping); the plan names the mitigation (cache per pipe) and authorizes the executor to report BLOCKED rather than distort semantics.
- Task 6 deletes benchmark methods ÔÇö the standalone benchmarks project is built explicitly in Task 6/7 so a broken benchmark cannot hide behind the solution build.
- **Review round 1 (2026-08-29, external): verdict EXECUTE WITH FIXES, 20 findings (4 blockers, 5 major, 11 minor), all verified against the code and folded in.** The load-bearing corrections: `PlaceAtEdge` mis-parenthesized the horizontal centering (the helper failed its own test); `IsGazeInsideKeyArea` computes a top-LEFT corner, not a center (now out of scope); the pipe helper needed an `onConnected` seam for per-connection state (PipeConnection, cached identity) and the service keeps its own Dispose clear; `DragCoordinator`/`OverlayVmBindingCoordinator` OnDispose need a `Context is null` guard (Dispose-before-Initialize tests exist); ToolbarWindow's `if (isDocked)` block shape must be preserved (no early return ÔÇö floating SetMode still resets the target latch); GlassBackdrop keeps its null-screen no-op; identity caching upgraded from "if needed" to mandatory-for-correctness (dying-client `ShuttingDown` line); HeartbeatPipeServer gets a smoke test BEFORE the rewrite (it had zero tests).
- **Review round 3 (2026-08-30, external): verdict EXECUTE WITH FIXES, 12 findings (1 blocker, 2 major, 9 minor), all verified and folded in ÔÇö all 21 round-2 fixes confirmed APPLIED-CORRECT, no patch damage to passing tasks.** Same failure species one last time: Task 3's migration renamed `_context` only inside OnInitialize while ~330 references live file-wide (now an explicit rename step); Task 4 item 1 still said "and the connection list" (contradicting the Dispose that keeps it); `_connectionsByPipe` was undeclared and read outside any lock (now a declared ConcurrentDictionary with TryGetValue guards in both lambdas, and onDisconnected TryRemoves before teardown). Minors: Step 0a spells out the `_pipeName` field + :90 use; `_pipeServer` assigned in the ctor (CS0236); the garbage-line note names each side's actual mechanism; finally span 248-262; delete both pipe methods whole; AGENTS.md in Files (74 ÔåÆ 72); Toolbar line refs 145-183/183; `double gridW` verbatim; CS8604 note on `Resolve`'s nullability. Round-2 detail (for the record): call sites un-re-verified after signature changes ÔÇö ExternalDwell's 1-arg log lambda and unpopulated `_connectionsByPipe`; DwellTargetUpdateTests held six LIVE UpdateTargetGeometry facts (delete one Fact, not the file); a ninth SetPredictor site at :730; the HeartbeatPipeServer ctor keeps its mandatory binder; `var context = Context!;` for the old parameter local; SettingsUi's TrackStatus unhook; the smoke test uses ForAdoption + GUID name + two-lines-same-identity; per-target teardown is TeardownConnection ending in `conn.Dispose()`; SnapshotConnections and EndlessStream dropped as dead API.
