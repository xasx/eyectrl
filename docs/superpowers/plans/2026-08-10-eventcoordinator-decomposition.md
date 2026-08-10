# EventCoordinator Decomposition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the 1,654-line `EventCoordinator` god-object into a thin orchestrator plus seven focused sub-coordinators, preserving its concrete public surface (8 members) so no production caller changes.

**Architecture:** Extraction-only refactor. Each sub-coordinator is an `internal sealed … : IDisposable` class in `src/EyeCtrl.App/Services/Coordinators/` that takes the existing `EventCoordinationContext` (verbatim moves — no `_context.X` rewriting) plus a tiny shared `ModalInteractionState` for the two cross-cluster flags. `EventCoordinator` becomes a façade that constructs/disposes the seven coordinators and forwards its 8 public members. Order: Drag → QuickActions → InputDispatcher → Toolbar → Keyboard → OverlayBinding → SettingsUi → cleanup.

**Tech Stack:** C# 13 / .NET 10, WPF, `CommunityToolkit.Mvvm` messenger, xUnit v3 + NFluent (no mocking lib — private nested fakes).

## Global Constraints

- **Repo conventions** (from `AGENTS.md`): file-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled, no `#region`, NFluent (not xUnit asserts) — `Check.That(x).IsEqualTo(y)` etc.
- **Test runner**: xUnit v3 stand-alone `Exe` test projects; global `Using` for `Xunit` + `NFluent` come from `tests/Directory.Build.props` (do not redeclare).
- **No production caller may change.** `App.xaml.cs:42` holds the concrete `EventCoordinator` and calls `SetModelServices` (:225), `ForceShowToolbar` (:236), `SetRestartCoordinator` (:306) — these 3 concrete-only members plus the 4 `IEventCoordinator` members (`WordPredictor`, `Initialize`, `LoadKeyboard`, `Dispose`) plus the concrete `Orchestrator` property (8 total) must all survive on `EventCoordinator`.
- **No mocking library.** Migrated tests reuse the existing private nested fakes (`FakeInputSimulator`, `FakeWindowActionManager`, `FakeInstalledAppProvider`).
- **Pipe-contention caveat**: do not run two `dotnet test` projects concurrently on this box.
- **Branch**: start from `main` *after* the `feat/architecture-layering-test` PR (the assessment + layering gate) merges, so the gate is on `main` and guards the refactor. If starting before, branch from `feat/architecture-layering-test`.
- **Per-task gate**: after every task, `dotnet build EyeCtrl.slnx` is clean (0 errors) and `dotnet test --filter "Category!=UI"` is green. German-locale output: `0 Fehler` = 0 errors, `Bestanden!` = passed.
- **Plan vs. design doc**: where this plan differs from `docs/EVENTCOORDINATOR_DECOMPOSITION_PLAN.md`, **this plan is authoritative** — it assigns members the design doc left unmentioned (`StateChangedMessage`, the four ManualSelection registrations, the `TaskSwitcherVM` lambda) and corrects the cluster home of `ShowActionMenu`/`HideActionMenu`/`BackdropDismiss` (overlay glue → T6, not InputDispatcher/cluster 2).

## How to read the move steps

This is an **extraction refactor**, not new feature code. The methods being moved already exist and
work verbatim. So:

- A shell code block's `// … moved members …` or `/* body moved in Step N */` marker means: **paste
  the body verbatim from the cited `EventCoordinator.cs` line range in the task's move table.** The
  *only* textual changes are the ones the table/list enumerates (visibility `private`→`internal`, a
  field renamed like `_quickActionsSummon`→`_summon`, or a shared-flag rewrite like
  `_keyboardVisible`→`_modal.KeyboardVisible`). Do not invent new logic.
- Coordinator constructors take the **whole `EventCoordinationContext`** (this plan's deliberate
  first-pass choice, endorsed by the design doc), so `_context.X` references inside moved methods
  stay valid without rewriting. Narrowing constructors to per-coordinator slices is explicit
  out-of-scope polish.
- Every move table cites exact current line numbers from `EventCoordinator.cs` (verified against
  `fde511c`, unchanged on this branch). If a line range has drifted, locate the method by name.
- **MANDATORY — every `Initialize` shell assigns `_context = context ?? throw …` before the moved
  body.** The moved `Setup*`/`On*` bodies begin with `if (_context == null) return;`, so a shell that
  only writes `/* body */` without the assignment produces coordinators whose every method silently
  no-ops. That passes the build *and* the headless `Category!=UI` gate (which never drives these
  paths) and ships a dead app. Do not rely on the moved body to set `_context` — it never does.
- **Usings**: shells list only the obviously-needed `using`s. When you paste a moved body, add
  whatever `using` the compiler flags (`Serilog`, `EyeCtrl.Platform.Input`, `EyeCtrl.Core.Calibration`,
  `EyeCtrl.ViewModels`, `System.Windows.Forms`, etc.). This is mechanical and compiler-caught.

## File Structure

**New folder:** `src/EyeCtrl.App/Services/Coordinators/`

| File (Create) | Responsibility |
|---|---|
| `Coordinators/DragCoordinator.cs` | Drag start/drop/cancel, trail overlay, escape-key hook (cluster 4) |
| `Coordinators/QuickActionsCoordinator.cs` | Hot-edge summon controller + edge resolution + settings reactivity (cluster 5) |
| `Coordinators/InputActionDispatcher.cs` | Message → input/window action translation (cluster 2) |
| `Coordinators/ToolbarLifecycleCoordinator.cs` | Auto-hide + visibility adapter + per-sample gaze routing (cluster 6) |
| `Coordinators/KeyboardCoordinator.cs` | Keyboard visibility + LoadKeyboard + skin tone (cluster 3) |
| `Coordinators/OverlayBindingCoordinator.cs` | VM.PropertyChanged → overlay window updates + calibration/backdrop/highlight (cluster 1) |
| `Coordinators/SettingsUiCoordinator.cs` | SettingsWindow lifecycle + OnSettingsChanged fan-out (cluster 7) |
| `Coordinators/ModalInteractionState.cs` | Shared cross-cluster flags (`KeyboardVisible`, `SettingsOpen`) |

**Modified:** `src/EyeCtrl.App/Services/EventCoordinator.cs` (shrinks from 1,654 → ~150 lines across the 8 tasks). **Tests:** `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs` loses migrated cases; new `Coordinators/<Name>CoordinatorTests.cs` files gain them.

## Shared types (defined in Task 4, consumed by Tasks 5–7)

```csharp
// Coordinators/ModalInteractionState.cs
namespace EyeCtrl.App.Services.Coordinators;

/// <summary>The two cross-cluster modal flags, lifted out of EventCoordinator so the coordinator
/// that opens a modal (keyboard, settings) and the coordinators that react (gaze routing, cursor
/// binding) share one source of truth.</summary>
internal sealed class ModalInteractionState
{
    public volatile bool KeyboardVisible;
    public volatile bool SettingsOpen;
}
```

**Suspend seams** (each owning coordinator exposes these; consumed by KeyboardCoordinator + SettingsUiCoordinator):
- `ToolbarLifecycleCoordinator.SetAutoHideSuspended(bool suspended)` → wraps `_toolbarAutoHide?.SetSuspended(suspended)`.
- `QuickActionsCoordinator.SetSummonSuspended(bool suspended)` → wraps `_quickActionsSummon?.SetSuspended(suspended)`.

---

## Task 0: Branch and baseline

**Files:** none (git only).

- [ ] **Step 1: Create the feature branch** (from `main` after the layering-gate PR merges; else from `feat/architecture-layering-test`).

```bash
git checkout main && git pull
git worktree add ../../.worktrees/ec-decompose -b refactor/eventcoordinator-decomposition
cd ../../.worktrees/ec-decompose   # adjust path to the new worktree
```

- [ ] **Step 2: Build + test baseline.**

```bash
dotnet build EyeCtrl.slnx
dotnet test --filter "Category!=UI"
```
Expected: 0 errors; all tests pass. Record the total (≈1,667 `[Fact]` on this branch) — each task must keep it green.

---

## Task 1: DragCoordinator (cluster 4)

**Why first:** fewest cross-cluster dependencies — drag is self-contained (`_dragGrabOrigin`, `_dragTrailOverlay`, `_disposeEscapeHook` are all drag-only), with clean events (`InteractionManager.DragStarted`) and its own escape-key hook.

**Files:**
- Create: `src/EyeCtrl.App/Services/Coordinators/DragCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs` (remove cluster-4 members + their `Setup`/`Dispose` lines)
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/DragCoordinatorTests.cs` (new — there are no existing drag reflection tests, so this adds the coverage the cluster deserves)

**Interfaces:**
- Consumes: `EventCoordinationContext` (whole), `InteractionManager`, `IOverlayManager`, `DragTrailViewModel`, `IMessenger`, `Dispatcher`, `IInputSimulator`, `EyeCtrlSettings`.
- Produces: `DragCoordinator` (internal sealed, `IDisposable`); `EventCoordinator` gains a `_dragCoordinator` field.

- [ ] **Step 1: Create the coordinator shell.**

```csharp
// src/EyeCtrl.App/Services/Coordinators/DragCoordinator.cs
namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

internal sealed class DragCoordinator : IDisposable
{
    private const double RightEdgeInsetLogical = 30;

    private EventCoordinationContext? _context;
    private DragTrailOverlay? _dragTrailOverlay;
    private PhysicalPoint _dragGrabOrigin;
    private Action? _disposeEscapeHook;
    private bool _disposed;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _context.InteractionManager.DragStarted += OnDragStarted;
        _context.DragTrailVM.PropertyChanged += OnDragTrailVMPropertyChanged;
        _context.Messenger.Register<DragCoordinator, DragDropMessage>(this, (_, m) =>
            _context.Dispatcher.BeginInvoke(() => OnDragDrop(m)));
        _context.Messenger.Register<DragCoordinator, DragCancelMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(OnDragCancel));
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
        _disposeEscapeHook = () => ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
    }

    // … methods moved in Step 2 …

    public void Dispose()
    {
        if (_disposed || _context is null) { _disposed = true; return; }
        _context.InteractionManager.DragStarted -= OnDragStarted;
        _context.DragTrailVM.PropertyChanged -= OnDragTrailVMPropertyChanged;
        _disposeEscapeHook?.Invoke();
        CloseDragOverlay();
        _context.Messenger.UnregisterAll(this);
        _disposed = true;
    }
}
```

- [ ] **Step 2: Move these members verbatim from `EventCoordinator.cs` into `DragCoordinator`** (change only `private` → keep; they already reference `_context`):

| Member | Source lines | Notes |
|---|---|---|
| `OnThreadFilterMessage` | 895–905 | unchanged |
| `OnDragStarted` | 907–926 | unchanged |
| `OnDragDrop` | 928–934 | move verbatim; keep the `_context?.` null-conditionals as-is |
| `OnDragCancel` | 936–942 | move verbatim |
| `CloseDragOverlay` | 944–948 | unchanged |
| `OnDragTrailVMPropertyChanged` | 950–981 | unchanged |
| const `RightEdgeInsetLogical` | 37 | **duplicate** into DragCoordinator (the shell already declares it). The EC copy **stays** — also used at `:1125` (`OnManualSelectionVisibilityChanged`, moves in T6). Don't delete EC's const until T6. |

Also move the three fields `_dragTrailOverlay`, `_dragGrabOrigin`, `_disposeEscapeHook` (EventCoordinator.cs:63–65).

- [ ] **Step 3: Wire the orchestrator.** In `EventCoordinator.cs`:
  - Add field: `private DragCoordinator? _dragCoordinator;`
  - In `Initialize` (≈ :108): replace `SetupDragHandlers();` with `_dragCoordinator = new DragCoordinator(); _dragCoordinator.Initialize(context);`
  - In `Dispose` (≈ :1636–1639): replace the drag-cleanup block with `_dragCoordinator?.Dispose();`
  - Delete `SetupDragHandlers` (872–893). **Do NOT delete `RightEdgeInsetLogical`** — duplicated into DragCoordinator, but EC still needs its own copy at `:1125` until T6.

- [ ] **Step 4: Write the new test.** Create `tests/EyeCtrl.App.Tests/Services/Coordinators/DragCoordinatorTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using Xunit;

public class DragCoordinatorTests
{
    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = new DragCoordinator();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized
        // coordinator must still dispose cleanly (no NullRef on _context).
        new DragCoordinator().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = new DragCoordinator();
        c.Dispose(); c.Dispose();
    }
}
```

*(Behavioral drag tests — e.g. `DragStarted_Raises_MouseDown_And_ShowsTrail` — require a fake `IInputSimulator` + a stand-in `DragTrailOverlay`; add them as a follow-up by reusing `EventCoordinatorTests.FakeInputSimulator` lifted into a shared `TestFakes.cs`. The contract tests above are the per-coordinator minimum the plan's acceptance criterion requires.)*

- [ ] **Step 5: Build + test.**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Architecture.Tests --filter "Category!=UI"
```
Expected: 0 errors; all green (incl. the 14 layering tests — `DragCoordinator` is internal to `EyeCtrl.App`, no cross-project invariant moves).

- [ ] **Step 6: Commit.**

```bash
git add src/EyeCtrl.App/Services/Coordinators/DragCoordinator.cs \
        src/EyeCtrl.App/Services/EventCoordinator.cs \
        tests/EyeCtrl.App.Tests/Services/Coordinators/DragCoordinatorTests.cs
git commit -m "refactor(eventcoordinator): extract DragCoordinator (cluster 4)"
```

---

## Task 2: QuickActionsCoordinator (cluster 5)

**Files:**
- Create: `Coordinators/QuickActionsCoordinator.cs`
- Modify: `EventCoordinator.cs` (remove cluster-5 members + fields)
- Test: modify `EventCoordinatorTests.cs` → migrate the reflection-based QuickActions test to a direct test.

**Interfaces:**
- Consumes: `EventCoordinationContext`, `QuickActionsSummonController`, `QuickActionsGridViewModel`, `IQuickActionsStore`, `EyeCtrlSettings`, `GazeEngine`, `Dispatcher`.
- Produces: `QuickActionsCoordinator` with public `SetSummonSuspended(bool)` (the suspend seam consumed by Tasks 5 & 7).

- [ ] **Step 1: Create the shell.**

```csharp
// Coordinators/QuickActionsCoordinator.cs
namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

internal sealed class QuickActionsCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private QuickActionsSummonController? _summon;
    private Action<GazePoint>? _gazeHandler;
    private PropertyChangedEventHandler? _settingsHandler;
    private bool _disposed;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
        /* … SetupQuickActionsSummon body, moved verbatim in Step 2 … */
    }

    /// <summary>Suspend/resume the hot-edge summon (called by Keyboard/Settings coordinators when a
    /// modal opens). Wraps _summon.SetSuspended.</summary>
    public void SetSummonSuspended(bool suspended) => _summon?.SetSuspended(suspended);

    // Test seams (production never calls these): inject a pre-built controller so the suspend/resume
    // path can be exercised without the full QuickActionsGridVM/GazeEngine creation path, and read
    // back the suspended state without reflection.
    internal void InjectSummonForTesting(QuickActionsSummonController summon) => _summon = summon;
    internal bool IsSummonSuspended => _summon?.IsSuspended ?? true;

    // … moved members …

    public void Dispose()
    {
        if (_disposed || _context is null) { _disposed = true; return; }
        if (_gazeHandler != null) _context.GazeEngine.GazeSampled -= _gazeHandler;
        if (_settingsHandler != null) _context.Settings.QuickActions.PropertyChanged -= _settingsHandler;
        _context.Messenger.UnregisterAll(this);
        _disposed = true;
    }
}
```

- [ ] **Step 2: Move verbatim** into `QuickActionsCoordinator`:
  - `UpdateQuickActionsEffectiveEdge` (700–704), `AttachQuickActionsSettingsHandler` (706–735), `SetupQuickActionsSummon` (737–840), `ComputeActualSummonEdge` (842–852).
  - Fields `_quickActionsSummon` (60), `_quickActionsGazeHandler` (61), `_quickActionsSettingsHandler` (62).
  - In the moved `SetupQuickActionsSummon`, replace the `_quickActionsSummon` field references with `_summon` (the new field name). (Quick-actions setup does not touch the toolbar.)
  - **Do NOT move the `QuickActionSelectionMessage` registration (137–149)** — its body calls `ExecuteQuickAction`, which lives on `InputActionDispatcher` (T3). Leave it on EC; T3 owns it.

- [ ] **Step 3: Wire orchestrator.** In `EventCoordinator.cs`:
  - Field `private QuickActionsCoordinator? _quickActions;`
  - `Initialize` (≈ :106): replace `SetupQuickActionsSummon();` → `_quickActions = new QuickActionsCoordinator(); _quickActions.Initialize(context);`. (The `QuickActionSelectionMessage` reg at 137–149 **stays on EC** — T3 moves it with `InputActionDispatcher`.)
  - `Dispose` (≈ :1598–1604): replace the quick-actions cleanup block with `_quickActions?.Dispose();`.
  - In `ShowSettingsWindow`/`OnSettingsWindowClosed`/`SetKeyboardVisible`: replace `_quickActionsSummon?.SetSuspended(x)` → `_quickActions?.SetSummonSuspended(x)`. (These three call-sites stay in EventCoordinator until Tasks 5 & 7 extract them; for now the orchestrator forwards.)

- [ ] **Step 4: Migrate the test.** In `EventCoordinatorTests.cs`, delete `QuickActionsEnabledSetting_SuspendsAndResumesSummonController` (114–159) and the helpers `InvokeSetupQuickActionsSummon`, `InvokeQuickActionsSettingsHandler`, `GetQuickActionsSuspended`. Add `internal bool IsSuspended => _suspended;` to `QuickActionsSummonController` (the existing `InternalsVisibleTo("EyeCtrl.App.Tests")` at `EyeCtrl.App.csproj:61` makes it visible). Create `Coordinators/QuickActionsCoordinatorTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Settings;
using Xunit;

public class QuickActionsCoordinatorTests
{
    [Fact]
    public void Enabled_Toggle_SuspendsAndResumesSummon()
    {
        var settings = new EyeCtrlSettings { QuickActions = { Enabled = false } };
        var controller = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var coord = new QuickActionsCoordinator();
        coord.InjectSummonForTesting(controller);           // bypass the GridVM/GazeEngine creation path
        coord.Initialize(TestFakes.MakeContext(settings));  // SetupQuickActionsSummon sees _summon != null
                                                            // → SetSuspended(!Enabled = true)
        Check.That(controller.IsSuspended).IsTrue();

        settings.QuickActions.Enabled = true;               // [ObservableProperty] raises PropertyChanged
                                                            // synchronously → the settings handler → SetSuspended(false)
        Check.That(controller.IsSuspended).IsFalse();
    }
}
```

> The old test's reflection (`GetMethod("SetupQuickActionsSummon")`, `GetField("_quickActionsSettingsHandler")`, `GetField("_suspended")` on the controller) is replaced by: the `InjectSummonForTesting` seam, the settings handler driven by **setting the property** (`[ObservableProperty]` raises the change itself — there is no `OnPropertyChange` method), and a typed `IsSuspended` accessor. No reflection remains.

- [ ] **Step 5: Lift shared test helpers.** Move `MakeContext`/`MakeContextWithWindowManager`/the three fakes **and the `record ModifiedKeyPress` (:410, the assertions read it)** from `EventCoordinatorTests.cs` into a new `tests/EyeCtrl.App.Tests/Services/TestFakes.cs` (static class). **Critical (B7):** in `TestFakes.MakeContext`, change `Messenger: null!` → `Messenger: new WeakReferenceMessenger()` — migrated tests call `coordinator.Initialize(ctx)`, which `Register`s on the messenger and NREs on null. Add a `MakeContext(EyeCtrlSettings settings)` overload for the QuickActions test. Update `EventCoordinatorTests.cs` to call `TestFakes.MakeContext(...)`.

- [ ] **Step 6: Build + test + commit.**

```bash
dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"
git commit -am "refactor(eventcoordinator): extract QuickActionsCoordinator (cluster 5)"
```

---

## Task 3: InputActionDispatcher (cluster 2)

**Files:** Create `Coordinators/InputActionDispatcher.cs`; modify `EventCoordinator.cs`; migrate 7 tests out of `EventCoordinatorTests.cs`.

**Interfaces:**
- Consumes: `EventCoordinationContext`, `IInputSimulator`, `IWindowManager`, `IWindowsAppLauncher`, `GazeEngine` (`StableCentroid`), `INoteManager`, `TargetHighlightOverlayViewModel`, `IOverlayManager`.
- Produces: `InputActionDispatcher` exposing `ExecuteQuickAction(QuickAction, IntPtr?)` and the four `Execute*`/`Handle*` methods as `internal` (test-callable without reflection).

- [ ] **Step 1: Create the shell.**

```csharp
// Coordinators/InputActionDispatcher.cs
namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

internal sealed class InputActionDispatcher : IDisposable
{
    private EventCoordinationContext? _context;
    private const int DefaultScrollAmount = 3;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
        var m = context.Messenger;
        m.Register<InputActionDispatcher, KeyActivatedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleKeyActivation(msg.Key, msg.Modifiers)));
        m.Register<InputActionDispatcher, PredictionSelectedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandlePredictionSelected(msg.DeleteCount, msg.Word)));
        m.Register<InputActionDispatcher, SpecialKeyMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleSpecialKey(msg.KeyName, msg.Modifiers)));
        m.Register<InputActionDispatcher, ActionRequestedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleActionRequested(msg)));
        m.Register<InputActionDispatcher, QuickActionSelectionMessage>(this, (_, msg) =>
        {
            IntPtr? hwnd = msg.Action.Type == QuickActionType.WindowAction
                ? context.WindowManager.TryGetEligibleForegroundWindow() : null;
            context.Dispatcher.BeginInvoke(() => ExecuteQuickAction(msg.Action, hwnd));
        });
    }

    // … moved members (now internal, not private) …

    public void Dispose() => _context?.Messenger.UnregisterAll(this);
}
```

- [ ] **Step 2: Move verbatim, changing visibility `private` → `internal`:**
  - `HandleTaskSwitcherSelection` (152–159) [also move the `TaskSwitcherSelectionMessage` registration from `Initialize` 135–136]
  - `ExecuteQuickAction` (165–204), `ExecuteKeyboardShortcut` (211–264), `ExecuteMediaControl` (270–282), `ExecuteWindowAction` (296–329)
  - `HandleKeyActivation` (1319–1388), `HandlePredictionSelected` (1390–1397), `HandleSpecialKey` (1399–1448), `HandleActionRequested` (1450–1488)
  - const `DefaultScrollAmount` (35), `BackspaceVirtualKeyCode`/`EnterVirtualKeyCode`/`TabVirtualKeyCode`/`SpaceVirtualKeyCode` (31–34)
  - The four message registrations currently in `EventCoordinator.Initialize`/`SetupMessageHandlers` for KeyActivated (446–447), PredictionSelected (450–451), SpecialKey (454–455), ActionRequested (458–459) — remove from EC (they're now in `InputActionDispatcher.Initialize` above).

- [ ] **Step 3: Wire orchestrator.** Field `_inputDispatcher`; construct + `Initialize` in `EC.Initialize`; `_inputDispatcher?.Dispose()` in `EC.Dispose`. Remove the 4 now-duplicated message registrations from `EC.SetupMessageHandlers` and the inline `TaskSwitcherSelectionMessage`/`QuickActionSelectionMessage` registrations (Task 2 already moved the latter).

- [ ] **Step 4: Migrate the 7 tests.** Delete from `EventCoordinatorTests.cs`: `ExecuteKeyboardShortcut_…` (78–91), `ExecuteMediaControl_…` (93–112), `ExecuteWindowAction_RoutesPayload…` (254–267), `ExecuteWindowAction_NullHwnd_NoOps` (269–281), `ExecuteWindowAction_RejectedPayload_NoOps` (283–306), `ExecuteWindowAction_DoesNotQueryForegroundItself` (308–321), `ExecuteQuickAction_WindowAction_ForwardsTheResolvedHwnd` (323–342). Delete helpers `ExecuteKeyboardShortcut`, `InvokeExecuteWindowAction`, `SetContext`. Create `Coordinators/InputActionDispatcherTests.cs`.

  The first test, written out:

```csharp
public class InputActionDispatcherTests
{
    [Fact]
    public void ExecuteKeyboardShortcut_CharacterRequiringShift_MergesRequiredModifier()
    {
        var input = new TestFakes.FakeInputSimulator();
        var d = new InputActionDispatcher();
        d.Initialize(TestFakes.MakeContext(input));
        d.ExecuteKeyboardShortcut("Ctrl+!");   // internal, directly callable
        Check.That(input.LastModifiedKeyPress!.VirtualKeyCode).IsEqualTo((ushort)0x31);
        Check.That(input.LastModifiedKeyPress.Modifiers).Contains(ModifierKey.Shift);
        Check.That(input.LastModifiedKeyPress.Modifiers).Contains(ModifierKey.Ctrl);
    }
}
```

  The remaining six migrate by this **mechanical rule** (apply to each named test — every `Check.That(...)` assertion stays verbatim, only the arrange/act lines change):

  | Test | Arrange change | Act change |
  |---|---|---|
  | `ExecuteMediaControl_EveryPreset_ProducesAnExtendedKeyPress` | `var d = new InputActionDispatcher(); d.Initialize(TestFakes.MakeContext(new TestFakes.FakeInputSimulator()));` inside the loop | `typeof(EventCoordinator).GetMethod("ExecuteMediaControl")…Invoke(coordinator, new[]{command})` → `d.ExecuteMediaControl(command)` |
  | `ExecuteWindowAction_RoutesPayloadToTheRightOp` | `var d = new InputActionDispatcher(); d.Initialize(TestFakes.MakeContextWithWindowManager(fake));` | `InvokeExecuteWindowAction(coord,…)` → `d.ExecuteWindowAction(payload, hwnd)` |
  | `ExecuteWindowAction_NullHwnd_NoOps` | same as above | same as above |
  | `ExecuteWindowAction_RejectedPayload_NoOps` | same as above | same as above |
  | `ExecuteWindowAction_DoesNotQueryForegroundItself` | same as above | same as above |
  | `ExecuteQuickAction_WindowAction_ForwardsTheResolvedHwnd` | same as above | `typeof(EventCoordinator).GetMethod("ExecuteQuickAction")…Invoke(coord, new[]{ action, hwnd })` → `d.ExecuteQuickAction(action, hwnd)` |

- [ ] **Step 5: Build + test + commit.**

```bash
dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"
git commit -am "refactor(eventcoordinator): extract InputActionDispatcher (cluster 2)"
```

---

## Task 4: ToolbarLifecycleCoordinator (cluster 6) + introduce `ModalInteractionState`

**This task creates the shared state type consumed by Tasks 5–7.** It also extracts the toolbar auto-hide controller, visibility adapter, toolbar-rect capture, and the per-sample gaze routing (which reads the two modal flags).

**Files:** Create `Coordinators/ModalInteractionState.cs`, `Coordinators/ToolbarLifecycleCoordinator.cs`; modify `EventCoordinator.cs`.

**Interfaces:**
- Consumes: `EventCoordinationContext`, `ModalInteractionState` (reads `.KeyboardVisible`/`.SettingsOpen` in the gaze-routing early-out), `IOverlayManager`, `GazeEngine`, `CalibrationService`, `EyeCtrlSettings`, `Dispatcher`.
- Produces: `ToolbarLifecycleCoordinator` with `SetAutoHideSuspended(bool)` (wraps `ToolbarAutoHideController.SetSuspended`), `SetActive(bool)` (wraps `ToolbarVisibilityAdapter.SetActive`), `SetPaused(bool)` (wraps `ToolbarAutoHideController.SetPaused`), `UpdateEnabled(bool)`, `UpdateEdge(string)`, `ReassertVisibility()`, `PositionIndicator(string)`, **`RefreshToolbarRect()`** (no-arg; the `DispatcherPriority.Loaded` deferral + `CaptureToolbarRect` live inside it), `ForceShow()` / `ResetToVisible()` (used by `EC.ForceShowToolbar`). Owns the `StateChangedMessage` registration (paused → `SetPaused`).

- [ ] **Step 1: Create `ModalInteractionState.cs`** (code in the Shared types section above).

- [ ] **Step 2: Create `ToolbarLifecycleCoordinator` shell.**

```csharp
// Coordinators/ToolbarLifecycleCoordinator.cs
namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

internal sealed class ToolbarLifecycleCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private readonly ModalInteractionState _modal;
    private ToolbarAutoHideController? _autoHide;
    private ToolbarVisibilityAdapter? _visibility;
    private Action<GazePoint>? _autoHideGazeHandler;

    public ToolbarLifecycleCoordinator(ModalInteractionState modal) => _modal = modal;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
        /* … SetupToolbarAutoHide body, moved verbatim in Step 2 (NOT SetupGazeRouting — stays on EC, see Step 3) … */
    }

    public void SetAutoHideSuspended(bool suspended) => _autoHide?.SetSuspended(suspended);
    public void SetActive(bool active) => _visibility?.SetActive(active);
    public void SetPaused(bool paused) => _autoHide?.SetPaused(paused);
    public void ForceShow() { _visibility?.ForceShow(); _autoHide?.ResetToVisible(); }
    public void UpdateEnabled(bool enabled) => _autoHide?.UpdateEnabled(enabled);
    public void UpdateEdge(string edge) => _autoHide?.UpdateEdge(edge);
    public void ReassertVisibility() => _autoHide?.ReassertVisibility();
    public void PositionIndicator(string edge) => _visibility?.PositionIndicator(edge);
    // RefreshToolbarRect (moved, 1298–1305) is the public no-arg seam — deferral + CaptureToolbarRect live inside it.

    // … moved members …

    public void Dispose()
    {
        if (_autoHideGazeHandler != null && _context is not null) _context.GazeEngine.GazeSampled -= _autoHideGazeHandler;
        _context?.Messenger.UnregisterAll(this);
    }
}
```

- [ ] **Step 3: Move verbatim** into `ToolbarLifecycleCoordinator`:
  - `SetupToolbarAutoHide` (640–698), `CaptureToolbarRect` (854–863), `RefreshToolbarRect` (1298–1305 — now the public no-arg seam).
  - The `StateChangedMessage` registration (603–604) — move into `Initialize`; its body `_toolbarAutoHide?.SetPaused(...)` → `_autoHide?.SetPaused(...)` (or call the `SetPaused` seam).
  - Fields `_toolbarAutoHide` (**:57**), `_toolbarVisibility` (**:58**), `_autoHideGazeHandler` (**:59**). **Do NOT move `_scrollModeController` (:56)** — it stays on EC (constructed :100, disposed :1646); a literal line cut at 56 would silently delete scroll-mode wiring.
  - const access to `DpiHelper`, `System.Windows.SystemParameters` stays.

> **`SetupGazeRouting` + `_gazeMovedHandler` STAY on `EventCoordinator`** — do NOT move them in T4. `SetupGazeRouting` (607–638) routes per-sample gaze to the toolbar AND the keyboard AND the settings window; the latter two are owned by T5/T7, which don't exist yet. Moving it here would couple T4 to not-yet-extracted coordinators. T8 folds it in. Keep EC's `_gazeMovedHandler` field (:46), the `SetupGazeRouting` method, and its `Dispose` unsubscribe (:1623–1626) through T7; its reads of `_keyboardVisible`/`_settingsOpen` stay as EC fields until T8 rewrites them to `_modal.*`.

- [ ] **Step 4: Wire orchestrator.** In `EventCoordinator.cs`:
  - Add `private readonly ModalInteractionState _modal = new();` (created once on EC, before any coordinator).
  - Field `private ToolbarLifecycleCoordinator? _toolbar;`
  - `Initialize` (≈ :105): replace `SetupToolbarAutoHide();` → `_toolbar = new ToolbarLifecycleCoordinator(_modal); _toolbar.Initialize(context);`. Remove the `StateChangedMessage` registration (603–604) from `SetupMessageHandlers` — it moved into `_toolbar.Initialize`.
  - `EC.ForceShowToolbar` (84–93): body → `if (_context == null) return; _context.Dispatcher.BeginInvoke(() => _toolbar?.ForceShow());`
  - Replace every remaining `_toolbarAutoHide?.X` / `_toolbarVisibility?.X` on EC with the `_toolbar?.X` seam. At T4 those live in: `OnSettingsChanged` (1280–1284: `UpdateEnabled`/`UpdateEdge`/`PositionIndicator`); `ShowSettingsWindow`/`OnSettingsWindowClosed`/`SetKeyboardVisible` (`SetSuspended`→`SetAutoHideSuspended`); and the **calibration handler** (564 `SetActive(false)`, 583/594 `ReassertVisibility()`) — the calibration handler itself stays on EC until T6, but its toolbar refs flip to seams now.
  - `OnSettingsChanged`'s toolbar-rect refresh: replace the inline deferred `CaptureToolbarRect()`/`UpdateToolbarRect(...)` block with `_toolbar?.RefreshToolbarRect()` (no-arg — the `DispatcherPriority.Loaded` deferral + capture now live inside it, so EC must not rebuild a `PhysicalRect` itself).

- [ ] **Step 5: Build + test + commit.**

```bash
dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"
git commit -am "refactor(eventcoordinator): extract ToolbarLifecycleCoordinator + ModalInteractionState (cluster 6)"
```

---

## Task 5: KeyboardCoordinator (cluster 3)

**Consumes the suspend seams from Tasks 2 & 4** (`_quickActions.SetSummonSuspended`, `_toolbar.SetAutoHideSuspended`) and **writes `_modal.KeyboardVisible`**.

**Files:** Create `Coordinators/KeyboardCoordinator.cs`; modify `EventCoordinator.cs`.

**Interfaces:**
- Consumes: `EventCoordinationContext`, `ModalInteractionState`, `QuickActionsCoordinator`, `ToolbarLifecycleCoordinator`, `PredictionStackFactory`, `SettingsService`, `ModelRegistryService`.
- Produces: `KeyboardCoordinator` owning `LoadKeyboard(...)`, `SetKeyboardVisible(bool,bool)`, `OnEmojiSkinToneChanged(int)`, the `WordPredictor`/`Orchestrator` properties (EC re-exposes these), and **`internal bool ReloadIfChanged(...)`** (a seam consumed by T7: encapsulates the `_keyboardSnapshot` compare so SettingsUi doesn't read a field it doesn't own).

- [ ] **Step 1: Shell.**

```csharp
// Coordinators/KeyboardCoordinator.cs
namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

internal sealed class KeyboardCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private readonly ModalInteractionState _modal;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly PredictionStackFactory _predictionFactory = new();
    private KeyboardStackSnapshot? _keyboardSnapshot;
    private string? _appDataDir;

    public WordPredictor? WordPredictor => _predictionFactory.Current?.WordPredictor;
    public PredictionOrchestrator? Orchestrator => _predictionFactory.Current;

    public KeyboardCoordinator(ModalInteractionState modal,
        QuickActionsCoordinator quickActions, ToolbarLifecycleCoordinator toolbar)
    { _modal = modal; _quickActions = quickActions; _toolbar = toolbar; }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
        /* … KeyboardToggled/Show/Hide registrations + skin-tone hookup, moved in Step 2 … */
    }

    public void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings,
        IInputSimulator inputSimulator, SettingsService settingsService, string appDataDir) { /* body of EC.LoadKeyboard */ }

    /// <summary>Seam consumed by SettingsUiCoordinator (T7): reload the keyboard iff the
    /// layout-affecting settings changed, so T7 doesn't read `_keyboardSnapshot`. Returns true if reloaded.</summary>
    internal bool ReloadIfChanged(EyeCtrlSettings settings, IInputSimulator input, SettingsService ss)
    {
        var snap = KeyboardStackSnapshot.Capture(settings);
        if (_keyboardSnapshot == snap) return false;
        LoadKeyboard(_context!.OverlayManager.Keyboard, settings, input, ss, _appDataDir!);
        return true;
    }

    // … moved members …

    public void Dispose()
    {
        _predictionFactory.Dispose();
        if (_context is not null)
        {
            _context.OverlayManager.Keyboard.SkinToneChanged -= OnEmojiSkinToneChanged; // the hookup moves here from EC :109
            _context.Messenger.UnregisterAll(this);
        }
    }
}
```

- [ ] **Step 2: Move verbatim**, with targeted edits:
  - `SetKeyboardVisible` (1543–1569): replace `_keyboardVisible = visible` → `_modal.KeyboardVisible = visible`; replace `_toolbarAutoHide?.SetSuspended(visible)` → `_toolbar.SetAutoHideSuspended(visible)`; replace `_quickActionsSummon?.SetSuspended(visible)` → `_quickActions.SetSummonSuspended(visible)`.
  - `KeyboardToggledMessage` handler (438–439) reads `!_keyboardVisible` → `!_modal.KeyboardVisible` (the `:439` read — completes the flag-rewrite set: reads {439, 621, 629, 1025}, writes {1212, 1252, 1312, 1549}).
  - `OnEmojiSkinToneChanged` (1571–1577) — unchanged (writes `_keyboardSnapshot`, settings).
  - `HandleKeyActivation`/`HandlePredictionSelected`/`HandleSpecialKey` — **NO**, those moved to InputActionDispatcher (Task 3). KeyboardCoordinator owns only visibility + LoadKeyboard + skin tone.
  - Fields `_predictionFactory` (47), `_keyboardSnapshot` (48), `_appDataDir` (51), `_keyboardVisible` (41 — REMOVED, now `_modal.KeyboardVisible`).
  - The keyboard-related message registrations: `KeyboardToggledMessage` (438–439), `ShowKeyboardRequestMessage` (535–540), `HideKeyboardRequestMessage` (543–548) — move into `Initialize`.
  - The `SkinToneChanged += OnEmojiSkinToneChanged` hookup (109) — move into `Initialize`.

- [ ] **Step 3: Wire orchestrator.** In `EventCoordinator.cs`:
  - Field `private KeyboardCoordinator? _keyboard;`
  - `Initialize` (≈ :104): `_keyboard = new KeyboardCoordinator(_modal, _quickActions!, _toolbar!); _keyboard.Initialize(context);`. **Construct coordinators in task order (1→7): Drag, QuickActions, InputDispatcher, Toolbar, Keyboard, OverlayBinding, SettingsUi** — this satisfies every ctor dependency (Keyboard needs QuickActions T2 + Toolbar T4; OverlayBinding needs Toolbar T4; SettingsUi needs Keyboard T5). All `Initialize` calls complete before `gazeEngine.Start()`, so "property handlers wired before first sample" holds regardless of internal order.
  - Re-expose on EC: `public WordPredictor? WordPredictor => _keyboard?.WordPredictor;` and `public PredictionOrchestrator? Orchestrator => _keyboard?.Orchestrator;` (replaces the direct `_predictionFactory` fields on EC).
  - `EC.LoadKeyboard` (1490–1537) → **keep the four `ArgumentNullException.ThrowIfNull` guards (:1493–1496) and the `_appDataDir` `InvalidOperationException` (:1498)** in EC's forwarder. `LoadKeyboard_ThrowsArgumentNullException_WhenKeyboardIsNull` calls this on an **uninitialized** EC and expects `ArgumentNullException`; if EC just did `_keyboard!.LoadKeyboard(...)`, `_keyboard` is null → NRE, not `ArgumentNullException` → red. After the guards, forward: `_keyboard!.LoadKeyboard(keyboard, settings, inputSimulator, settingsService, _appDataDir!);` (`_appDataDir` stays on EC, set at `Initialize` :98).
  - `EC.SetModelServices` (73–78): signature unchanged (App calls it with 3 args). `_modelRegistry` is genuinely dual-read (LoadKeyboard :1527 + the settings UI); `_modelManagementService` is read by the settings UI (T7). `_downloadService` is **write-only dead** (set :76, never read) — silently drop it. EC forwards: `_keyboard!.SetModelServices(modelRegistry, modelManagementService);` (and T7 adds `_settingsUi!.SetModelServices(modelRegistry, modelManagementService);`).

- [ ] **Step 4: Build + test + commit.**

```bash
dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"
git commit -am "refactor(eventcoordinator): extract KeyboardCoordinator (cluster 3)"
```

---

## Task 6: OverlayBindingCoordinator (cluster 1)

The largest cluster — pure VM→window glue plus calibration/backdrop/target-highlight message handling.

**Files:** Create `Coordinators/OverlayBindingCoordinator.cs`; modify `EventCoordinator.cs`.

**Interfaces:**
- Consumes: `EventCoordinationContext`, `ModalInteractionState` (cursor show guard reads `.SettingsOpen`), `ToolbarLifecycleCoordinator` (`ReassertVisibility` in the calibration handler), `IOverlayManager`, all overlay VMs, `GazeEngine.SuppressFixation`.
- Produces: `OverlayBindingCoordinator` (VM bindings + calibration/backdrop/highlight message wiring).

- [ ] **Step 1: Shell.**

```csharp
// Coordinators/OverlayBindingCoordinator.cs
namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

internal sealed class OverlayBindingCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private readonly ModalInteractionState _modal;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private GlassBackdropWindow? _glassBackdrop; // via _context.OverlayManager.GlassBackdrop

    public OverlayBindingCoordinator(ModalInteractionState modal, ToolbarLifecycleCoordinator toolbar)
    { _modal = modal; _toolbar = toolbar; }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
        /* … SetupPropertyChangedHandlers + SetupBackdrop + the 4 ManualSelection + calibration/action-menu/highlight/backdrop message regs, moved in Step 2 … */
    }

    // … moved members …

    public void Dispose()
    {
        if (_context is null) return;
        // mirror SetupPropertyChangedHandlers: unsubscribe the 8 VM PropertyChanges + TaskSwitcherVM
        _context.Messenger.UnregisterAll(this);
        _glassBackdrop?.Hide(); _glassBackdrop = null;
    }
}
```

- [ ] **Step 2: Move verbatim**, with targeted edits:
  - `SetupPropertyChangedHandlers` (341–375) and all eight `On*VMPropertyChanged` handlers (1014–1204): the cursor show guard at 1025 (`!_settingsOpen`) → `!_modal.SettingsOpen`.
  - const `ActivationButtonOffsetPixels` (30) — sole consumer is `OnActivationVMPropertyChanged` (:1045), which moves here.
  - const `RightEdgeInsetLogical` (37) — **now moves here** (consumer `OnManualSelectionVisibilityChanged` :1125 moves here); delete EC's copy (T1 deliberately left it until now).
  - `SetupBackdrop` (865–871), `ShowBackdropIfEnabled` (983–1004), `HideBackdrop` (1006–1010).
  - The `CalibrationStateChangedMessage` registration (551–599): **targeted edits** — line 564 `_toolbarVisibility?.SetActive(false)` → `_toolbar.SetActive(false)`; lines 583 & 594 `_toolbarAutoHide?.ReassertVisibility()` → `_toolbar.ReassertVisibility()`. (Different methods on different objects — T4 exposes both.)
  - The four ManualSelection registrations (387–435): `ManualSelectionDwellCompleted` (387–400), `ResetManualSelectionIndicator` (403–416), `ManualSelectionClose` (419–420), `ManualSelectionRecapture` (423–435 — calls `HideBackdrop` :428, now in this coordinator). Move into `Initialize`.
  - `ShowActionMenuMessage`/`HideActionMenuMessage`/`BackdropDismissMessage` registrations (472–532) — move into `Initialize`. *(Diverges from the design doc, which grouped these under cluster 2 — they're overlay glue, so cluster 1/T6 is correct.)*
  - The `TaskSwitcherVM.PropertyChanged` lambda (118–134) — overlay glue; move into `Initialize` and **subscribe/unsubscribe it by name** in `Dispose` (today it's an anonymous lambda never unsubscribed — fix that as you move it).

- [ ] **Step 3: Wire orchestrator.** Field `_overlay`; construct (needs `_modal` + `_toolbar`) + Initialize in `EC.Initialize` in task order (6th — after Toolbar). ("Property handlers before gaze starts" needs only that `Initialize` runs before `gazeEngine.Start()`, which it does — not that OverlayBinding is first.); `_overlay?.Dispose()` in `EC.Dispose`. Remove `SetupPropertyChangedHandlers`/`SetupBackdrop` calls + the moved message registrations from EC.

- [ ] **Step 4: Build + test + commit.**

```bash
dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"
git commit -am "refactor(eventcoordinator): extract OverlayBindingCoordinator (cluster 1)"
```

---

## Task 7: SettingsUiCoordinator (cluster 7)

**Consumes both suspend seams** and **writes `_modal.SettingsOpen`**.

**Files:** Create `Coordinators/SettingsUiCoordinator.cs`; modify `EventCoordinator.cs`.

**Interfaces:**
- Consumes: `EventCoordinationContext`, `ModalInteractionState`, `QuickActionsCoordinator`, `ToolbarLifecycleCoordinator`, `KeyboardCoordinator` (for `LoadKeyboard` on settings change), `ModelRegistryService`/`ModelManagementService`/`AppRestartCoordinator`, `SettingsService`, `CalibrationService`.
- Produces: `SettingsUiCoordinator` with `SetModelServices(...)`, `SetRestartCoordinator(...)`, `ShowSettingsWindow()` (EC re-exposes the latter two).

- [ ] **Step 1: Shell.**

```csharp
// Coordinators/SettingsUiCoordinator.cs
namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

internal sealed class SettingsUiCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private readonly ModalInteractionState _modal;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly KeyboardCoordinator _keyboard;
    private SettingsWindow? _settingsWindow;
    private ModelRegistryService? _modelRegistry;
    private ModelManagementService? _modelManagementService;
    private AppRestartCoordinator? _restartCoordinator;

    public SettingsUiCoordinator(ModalInteractionState modal, QuickActionsCoordinator quickActions,
        ToolbarLifecycleCoordinator toolbar, KeyboardCoordinator keyboard)
    { _modal = modal; _quickActions = quickActions; _toolbar = toolbar; _keyboard = keyboard; }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
        /* … SettingsRequested/CloseSettings registrations, moved in Step 2 … */
    }
    public void SetModelServices(ModelRegistryService r, ModelDownloadService d, ModelManagementService m) { _modelRegistry = r; _modelManagementService = m; }
    public void SetRestartCoordinator(AppRestartCoordinator c) => _restartCoordinator = c;

    // … ShowSettingsWindow / OnSettingsChanged / OnSettingsWindowClosed / UpdateTrackStatusVisibility …

    public void Dispose() { /* close _settingsWindow if open; UnregisterAll */ }
}
```

- [ ] **Step 2: Move verbatim**, with targeted edits:
  - `ShowSettingsWindow` (1208–1261): replace `_settingsOpen = true/false` → `_modal.SettingsOpen = true/false`; `_toolbarAutoHide?.SetSuspended` → `_toolbar.SetAutoHideSuspended`; `_quickActionsSummon?.SetSuspended` → `_quickActions.SetSummonSuspended`. The `_modelRegistry`/`_modelManagementService`/`_restartCoordinator` null-guards (:1239–1244) move with it. (`_appDataDir` is **not** touched here — only `LoadKeyboard` reads it; N4: don't say it moves.)
  - Field `_toolbarSnapshot` (:49) — read in `OnSettingsChanged` (:1273, :1286) to gate toolbar re-init; add it to this coordinator's fields.
  - `OnSettingsChanged` (1263–1296): the **keyboard-rebuild branch (1289–1293)** reads `_keyboardSnapshot` — that field is owned by `KeyboardCoordinator` (T5). Replace the branch with `_keyboard.ReloadIfChanged(s, _context.InputSimulator, _context.SettingsService)` (new T5 seam: compares KeyboardCoordinator's own `_keyboardSnapshot`, reloads if changed, returns whether it did). The **toolbar branch (1272–1287)** calls `_toolbar.UpdateEnabled/UpdateEdge/PositionIndicator` + `_toolbar.RefreshToolbarRect()` (no-arg).
  - `OnSettingsWindowClosed` (1307–1317): same `_modal.SettingsOpen`/suspend edits.
  - `UpdateTrackStatusVisibility` (331–339) + its `TrackStatus.PropertyChanged` hookup (112–116).
  - `SettingsRequestedMessage`/`CloseSettingsMessage` registrations (442–469) — move into `Initialize`.

- [ ] **Step 3: Wire orchestrator.** Field `_settingsUi`; construct (needs `_modal`, `_quickActions`, `_toolbar`, `_keyboard`) + Initialize in `EC.Initialize` (last); `_settingsUi?.Dispose()` in `EC.Dispose` (first — close settings before tearing down). `EC.SetModelServices` → `_settingsUi!.SetModelServices(...) + _keyboard!.SetModelServices(...)` (model registry is needed by both LoadKeyboard and the settings model-listing UI — split or share; share: SettingsUi holds registry for the UI, Keyboard holds it for LoadKeyboard, EC.SetModelServices forwards to both). `EC.SetRestartCoordinator` → `_settingsUi!.SetRestartCoordinator(...)`.

- [ ] **Step 4: Build + test + commit.**

```bash
dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"
git commit -am "refactor(eventcoordinator): extract SettingsUiCoordinator (cluster 7)"
```

---

## Task 8: Final cleanup — `EventCoordinator` ≤ 200 lines

After Tasks 1–7, `EventCoordinator.cs` holds the orchestrator shell: fields (the 7 coordinator refs + `_modal` + `_context` + `_appDataDir` + `_scrollModeController` + `_disposed`), the 8 public members (forwarding), `Initialize` (construct + Init each coordinator in task order), `Dispose` (dispose in reverse), and the leftover gaze-routing lambda from T4. T8 folds that lambda in and clears the last dead fields.

- [ ] **Step 1: Fold the leftover gaze routing into `ToolbarLifecycleCoordinator`.** Move `_gazeMovedHandler` (the lambda routing gaze to toolbar/keyboard/settings-window, deferred in T4) from EC into `ToolbarLifecycleCoordinator`. Its reads of `_keyboardVisible`/`_settingsOpen` become `_modal.KeyboardVisible`/`_modal.SettingsOpen`. The handler routes to `_context.OverlayManager.Toolbar.UpdateGaze` and `_context.OverlayManager.Keyboard.UpdateGaze` (both reachable via the context — no coordinator involved) and to `_settingsWindow.UpdateGaze`. The last is owned by `SettingsUiCoordinator` — expose **`internal void UpdateGazeIfVisible(PhysicalPoint point, long timestamp)`** on `SettingsUiCoordinator` (no-op if `_settingsWindow` is null/closed) and call it from the gaze routing. (A `Func<bool>` does **not** suffice — you need the window itself to call `UpdateGaze`.) Field `_toolbarInitialized` (:43) moves with the gaze routing. EC's `SetupGazeRouting` then deletes entirely.

- [ ] **Step 2: Delete dead fields/consts** from EC: `_predictionFactory`, `_keyboardSnapshot`, `_keyboardVisible`, `_settingsOpen`, `_settingsWindow`, `_toolbarAutoHide`, `_toolbarVisibility`, `_autoHideGazeHandler`, `_quickActionsSummon`, `_quickActionsGazeHandler`, `_quickActionsSettingsHandler`, `_dragTrailOverlay`, `_dragGrabOrigin`, `_disposeEscapeHook`, `_glassBackdrop`, `_modelRegistry`/`_downloadService`/`_modelManagementService` (moved to Keyboard/Settings), `_restartCoordinator`, all the VK consts, `RightEdgeInsetLogical`. Run `dotnet build` — any remaining reference is a missed move; fix it.

- [ ] **Step 3: Verify the public surface.** `EventCoordinator` must still expose exactly: `WordPredictor`, `Orchestrator`, `Initialize`, `LoadKeyboard`, `Dispose`, `SetModelServices`, `SetRestartCoordinator`, `ForceShowToolbar` (all forwarding). Confirm `App.xaml.cs` compiles unchanged (`git diff src/EyeCtrl.App/App.xaml.cs` → empty).

- [ ] **Step 4: Update `ARCHITECTURE.md`.** In §4.5 (Action Selection) and §4.10 (Settings flow), update the flow references that name `EventCoordinator` internals to point at the new coordinator names.

- [ ] **Step 5: Full gate.**

```bash
dotnet build EyeCtrl.slnx
dotnet test --filter "Category!=UI"
dotnet test tests/EyeCtrl.Architecture.Tests --filter "Category!=UI"   # 14 layering tests still green
wc -l src/EyeCtrl.App/Services/EventCoordinator.cs                    # assert ≤ 200
```
Expected: 0 errors; all tests green; `EventCoordinator.cs` ≤ 200 lines; no coordinator > 350 lines.

- [ ] **Step 6: Commit + PR.**

```bash
git commit -am "refactor(eventcoordinator): collapse to thin orchestrator; update ARCHITECTURE.md"
```
Open a PR for `refactor/eventcoordinator-decomposition` → `main` (squash-merge the 8 task commits if your workflow prefers).

---

## Acceptance criteria (carried from the design doc)

- [ ] `EventCoordinator.cs` ≤ 200 lines.
- [ ] No sub-coordinator file > 350 lines.
- [ ] The 8 concrete `EventCoordinator` public members preserved — `git diff src/EyeCtrl.App/App.xaml.cs` is empty.
- [ ] All existing test *cases* keep passing; reflection-based cases migrated to call coordinators directly.
- [ ] Each sub-coordinator has ≥1 unit test exercising its primary path.
- [ ] The 14 layering tests stay green.
- [ ] `ARCHITECTURE.md` §4.5/§4.10 flow references updated.

## Risks (monitor per task)

- **Messenger `Register` owner change**: each coordinator registers with *itself* as recipient; its `Dispose` calls `Messenger.UnregisterAll(this)`. A missed `UnregisterAll` → leaked handler. After each task, grep `Register<` and confirm every recipient has a matching `UnregisterAll` in its `Dispose`.
- **`Initialize` order**: construct coordinators in **task order (1→7): Drag, QuickActions, InputDispatcher, Toolbar, Keyboard, OverlayBinding, SettingsUi** — topologically valid (OverlayBinding needs Toolbar T4; Keyboard needs QuickActions T2 + Toolbar T4; SettingsUi needs Keyboard T5) and all run before `gazeEngine.Start()` (which `App.OnStartup` calls after `Initialize`). "Property handlers before first sample" is satisfied by being inside `Initialize`, not by being first.
- **Shared flags**: every read/write of the old `_keyboardVisible`/`_settingsOpen` fields must become `_modal.KeyboardVisible`/`_modal.SettingsOpen`. After Task 7, grep `eventcoordinator` for the old field names — zero hits expected.
