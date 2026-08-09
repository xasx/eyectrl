# Window Actions → Quick Actions Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move Minimize / Maximize-Restore / Close from the fixed toolbar's hardcoded window-actions region to the user-customizable Quick Actions toolbar, so each is opt-in and user-placed.

**Architecture:** All Core/Platform work from the current branch is retained unchanged (`IWindowManager`'s four methods, `ForegroundWindowEligibility`, `WindowStateOps`, the `[LibraryImport]` P/Invokes, the `WindowAction` enum). Only the *delivery surface* changes: a new `QuickActionType.WindowAction` whose `Payload` is a `WindowAction` enum name (mirroring how `MediaControl` carries a bare command string), dispatched from `EventCoordinator.ExecuteQuickAction`. The three fixed toolbar buttons, the `ShowWindowActions` setting, and `WindowActionRequestedMessage` are removed.

**Tech Stack:** C# / .NET 10 / WPF, CommunityToolkit.Mvvm messaging, xUnit v3 + NFluent (`Check.That`), solution file `EyeCtrl.slnx`.

---

## Decision record — supersedes §4 of the B2 spec

`docs/superpowers/specs/2026-08-05-window-management-toolbar-region-design.md` chose the fixed toolbar over the action menu. That comparison **never evaluated the Quick Actions toolbar** — the term appears zero times in the approved spec and zero times in its implementation plan. `QuickAction`'s own XML doc calls that surface "the Quick Actions toolbar", and it has a design spec of its own (`2026-07-13-quick-actions-toolbar-design.md`). The spec's blind spot is visible in its scope section, which defers Show Desktop to "its own future toolbar button" while `QuickActionPresets.KeyboardShortcuts` already ships `("Show Desktop", "Win+D")` as a stock preset.

**Why Quick Actions is the correct surface:**

1. **Close becomes opt-in.** The B2 spec defends no-confirm Close with five mechanisms (stable position, auto-hide, target-lifecycle contract, `WM_CLOSE`, shell/self rejection). On a customizable surface that entire argument is replaced by: a user who does not want a no-confirm Close does not add one. For a gaze-only daily driver this is categorically stronger.
2. **The hidden-can't-dwell property comes free.** `QuickActionsGridViewModel` does not override `Receive`, so it inherits `DwellOverlayViewModelBase`'s `if (IsVisible)` gate and maintains `IsVisible` on show/hide. `ToolbarViewModel` overrides that gate and drops it — which is why §4's target-lifecycle contract had to be hand-built.
3. **The extension point already exists** — `QuickActionType` + `QuickActionPresets` + `QuickActionsStore` + a gaze-authorable settings editor + `ExecuteQuickAction`'s dispatch switch with try/catch and logging.

**Retained from the B2 work regardless of surface:** the §4 target-lifecycle contract (`ToolbarViewModel.SetActive`, `ToolbarVisibilityAdapter`'s `SetActive(false)`-before-`Hide` ordering, and `ToolbarAutoHideController.ReassertVisibility`). A hidden toolbar firing Pause or Settings is a genuine pre-existing bug; that fix stands on its own merits and is **not** removed by this plan.

**Close stays no-confirm.** Every other quick action fires on dwell with no confirm, and adding Close is now a deliberate user act. Consistency wins; the opt-in gate is the safety mechanism.

---

## File structure

**Modify — Core (pure, no framework):**
- `src/EyeCtrl.Core/Models/QuickAction.cs` — add `WindowAction` to `QuickActionType`
- `src/EyeCtrl.Core/Models/QuickActionPresets.cs` — add the `WindowActions` preset table
- `src/EyeCtrl.Core/Settings/ToolbarSettings.cs` — remove `ShowWindowActions`

**Delete — Core:**
- `src/EyeCtrl.Core/Models/WindowActionButtons.cs` — the toolbar-only id/glyph table (also in the wrong project; glyph tables live in `EyeCtrl.ViewModels`, cf. `SelectionModeGlyphs.cs`)

**Modify — ViewModels:**
- `src/EyeCtrl.ViewModels/QuickActionTileViewModel.cs` — grid tile glyph per window action
- `src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs` — `AddWindowAction`
- `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` — remove the region, `SetWindowActionsVisible`, `WindowActionIds`
- `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` — remove `WindowActionRequestedMessage`

**Modify — App:**
- `src/EyeCtrl.App/Services/EventCoordinator.cs` — `ExecuteWindowAction` + eager HWND resolution; remove the `WindowActionRequestedMessage` registration, `HandleWindowAction`, and the `ShowWindowActions` reconciliation
- `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` — revert `Initialize` to two parameters
- `src/EyeCtrl.App/Services/OverlayManager.cs` — update the `Initialize` call site
- `src/EyeCtrl.App/Services/SettingsSnapshots.cs` — remove `ShowWindowActions` from `ToolbarSnapshot`
- `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` — remove the toolbar toggle + reset line; add the Window Action type-picker entry, picker page, and `QaTypeGlyph` case

**Unchanged (retained):** `IWindowManager`, `Win32WindowManager`, `ForegroundWindowEligibility`, `WindowStateOps`, `WindowAction`, `NativeMethods`, `ToolbarVisibilityAdapter`, `ToolbarAutoHideController`, `ToolbarViewModel.SetActive`.

---

### Task 1: Core — `QuickActionType.WindowAction` + preset table

**Files:**
- Modify: `src/EyeCtrl.Core/Models/QuickAction.cs:4-19`
- Modify: `src/EyeCtrl.Core/Models/QuickActionPresets.cs:30-41`
- Test: `tests/EyeCtrl.Platform.Tests/Models/QuickActionPresetsTests.cs`

- [ ] **Step 1: Write the failing test**

Append inside the existing test class in `tests/EyeCtrl.Platform.Tests/Models/QuickActionPresetsTests.cs`:

```csharp
    // Every WindowActions payload must round-trip to a WindowAction enum member, because
    // EventCoordinator.ExecuteWindowAction parses the payload with Enum.TryParse<WindowAction>.
    // A typo here would ship a preset that silently no-ops (logs a warning, does nothing).
    [Fact]
    public void WindowActions_PayloadsAllParseToWindowActionEnum()
    {
        Check.That(QuickActionPresets.WindowActions).Not.IsEmpty();

        foreach (var (_, payload) in QuickActionPresets.WindowActions)
            Check.That(Enum.TryParse<WindowAction>(payload, out _)).IsTrue();
    }

    [Fact]
    public void WindowActions_CoversEveryWindowActionMember()
    {
        // Drift guard: adding a WindowAction member without a preset would leave it unreachable
        // from the only surface that can fire it.
        var covered = QuickActionPresets.WindowActions.Select(w => w.Payload).ToArray();

        foreach (var member in Enum.GetNames<WindowAction>())
            Check.That(covered).Contains(member);
    }
```

Ensure the file has `using EyeCtrl.Core.Models;` at the top (it already does for `QuickActionPresets`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~QuickActionPresetsTests"`
Expected: FAIL — compile error, `QuickActionPresets` does not contain `WindowActions`.

- [ ] **Step 3: Add the enum member**

In `src/EyeCtrl.Core/Models/QuickAction.cs`, add as the last member of `QuickActionType` (append, do not reorder — the enum is persisted by name in `quickactions.json`, but appending keeps numeric values stable for any legacy numeric payload):

```csharp
    /// <summary>Minimize, maximize/restore, or close the foreground window.</summary>
    WindowAction
```

- [ ] **Step 4: Add the preset table**

In `src/EyeCtrl.Core/Models/QuickActionPresets.cs`, add after the `MediaControls` property:

```csharp
    /// <summary>
    /// Foreground-window management actions: display label + payload. The payload is the
    /// <see cref="WindowAction"/> enum member name, parsed by
    /// <c>EventCoordinator.ExecuteWindowAction</c> (covered by tests).
    /// </summary>
    public static IReadOnlyList<(string Label, string Payload)> WindowActions { get; } = new[]
    {
        ("Minimize Window", "Minimize"),
        ("Maximize / Restore", "MaximizeRestore"),
        ("Close Window", "Close"),
    };
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~QuickActionPresetsTests"`
Expected: PASS (`Fehler: 0`)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Models/QuickAction.cs src/EyeCtrl.Core/Models/QuickActionPresets.cs tests/EyeCtrl.Platform.Tests/Models/QuickActionPresetsTests.cs
git commit -m "feat(quick-actions): add WindowAction type + preset table"
```

---

### Task 2: ViewModels — grid tile glyphs

**Files:**
- Modify: `src/EyeCtrl.ViewModels/QuickActionTileViewModel.cs:27-49`
- Test: `tests/EyeCtrl.ViewModels.Tests/QuickActionTileViewModelTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.ViewModels.Tests/QuickActionTileViewModelTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public class QuickActionTileViewModelTests
{
    // BMP glyphs only — the same constraint the toolbar region used (Supplementary-Plane
    // characters render as tofu in the WPF default font).
    [Theory]
    [InlineData("Minimize", "▁")]
    [InlineData("MaximizeRestore", "□")]
    [InlineData("Close", "✕")]
    public void Icon_WindowAction_UsesPerPayloadGlyph(string payload, string expected)
    {
        var action = new QuickAction
        {
            Type = QuickActionType.WindowAction,
            Label = "test",
            Payload = payload
        };

        var vm = new QuickActionTileViewModel(action);

        Check.That(vm.Icon).IsEqualTo(expected);
    }

    [Fact]
    public void Icon_WindowAction_UnknownPayload_FallsBackToGenericGlyph()
    {
        var action = new QuickAction
        {
            Type = QuickActionType.WindowAction,
            Label = "test",
            Payload = "NotAWindowAction"
        };

        var vm = new QuickActionTileViewModel(action);

        Check.That(vm.Icon).IsEqualTo("▣");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~QuickActionTileViewModelTests"`
Expected: FAIL — `Icon` returns `"⚡"` (the `_ =>` fallback) for all four cases.

- [ ] **Step 3: Add the glyph branch**

In `src/EyeCtrl.ViewModels/QuickActionTileViewModel.cs`, insert this case into the `Icon` switch, immediately after the `MediaControl` arm and before the final `_ =>`:

```csharp
            QuickActionType.WindowAction => action.Payload switch
            {
                "Minimize" => "▁",         // ▁
                "MaximizeRestore" => "□",  // □
                "Close" => "✕",            // ✕
                _ => "▣"                   // ▣ fallback
            },
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~QuickActionTileViewModelTests"`
Expected: PASS — 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/QuickActionTileViewModel.cs tests/EyeCtrl.ViewModels.Tests/QuickActionTileViewModelTests.cs
git commit -m "feat(quick-actions): window-action tile glyphs"
```

---

### Task 3: ViewModels — editor add-path

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs:44-50`
- Test: `tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

Append inside the existing test class in `tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs`. The file already declares the fakes and helper this test uses — `FakeStore` (`:9-15`, exposing a `Saved` list, **not** a save counter), `FakeApps` (`:17-22`), and `Vm(FakeStore, IInstalledAppProvider?)` (`:24-25`). Do not declare new fakes; the block below uses the existing ones and mirrors `AddMediaControl_CreatesMediaAction` (`:42-49`).

```csharp
    [Fact]
    public void AddWindowAction_CreatesAndPersistsWindowAction()
    {
        var store = new FakeStore();
        Vm(store).AddWindowAction("Close Window", "Close");

        // Add() persists immediately, like every other add-path — asserting through store.Saved
        // (not the VM collection) proves the write reached the store.
        Check.That(store.Saved).HasSize(1);
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.WindowAction);
        Check.That(store.Saved[0].Label).IsEqualTo("Close Window");
        Check.That(store.Saved[0].Payload).IsEqualTo("Close");
        Check.That(store.Saved[0].Enabled).IsTrue();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SettingsQuickActionsEditorViewModelTests"`
Expected: FAIL — compile error, no `AddWindowAction`.

- [ ] **Step 3: Add the method**

In `src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs`, add after `AddMediaControl`:

```csharp
    /// <summary>Adds a window-action (payload is the WindowAction enum name) and saves.</summary>
    public void AddWindowAction(string label, string payload)
        => Add(QuickActionType.WindowAction, label, payload);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SettingsQuickActionsEditorViewModelTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs
git commit -m "feat(quick-actions): editor add-path for window actions"
```

---

### Task 4: App — dispatch with synchronous foreground resolution

This task preserves the property the B2 spec called BLOCKER A.1: the foreground HWND is resolved **on the dwell-completion tick**, not after a dispatcher hop. The existing `QuickActionSelectionMessage` registration at `EventCoordinator.cs:137-138` uses `BeginInvoke`, which would reintroduce the gap. The fix resolves the HWND eagerly **only** for window actions and passes it into the executor — every other action type keeps the deferred hop unchanged.

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:137-138` (registration), `:195-231` (`ExecuteQuickAction`)
- Test: `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs`

- [ ] **Step 1: Write the failing test**

Append inside the existing `EventCoordinatorTests` class. `FakeWindowActionManager` and the `SetContext` / `MakeContextWithWindowManager` helpers already exist in this file from the toolbar work — reuse them.

```csharp
    private static void InvokeExecuteWindowAction(EventCoordinator coordinator, string payload, IntPtr? hwnd)
    {
        typeof(EventCoordinator)
            .GetMethod("ExecuteWindowAction", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(coordinator, new object?[] { payload, hwnd });
    }

    [Theory]
    [InlineData("Minimize", WindowAction.Minimize)]
    [InlineData("MaximizeRestore", WindowAction.MaximizeRestore)]
    [InlineData("Close", WindowAction.Close)]
    public void ExecuteWindowAction_RoutesPayloadToTheRightOp(string payload, WindowAction expected)
    {
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, payload, new IntPtr(0x1234));

        Check.That(fake.LastOp).IsEqualTo(expected);
    }

    [Fact]
    public void ExecuteWindowAction_NullHwnd_NoOps()
    {
        // Fail-closed: the caller resolved no eligible foreground window.
        var fake = new FakeWindowActionManager { EligibleHwnd = null };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, "Close", null);

        Check.That(fake.LastOp).IsNull();
    }

    [Fact]
    public void ExecuteWindowAction_UnparseablePayload_NoOps()
    {
        // A corrupt quickactions.json must not fire an arbitrary op.
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, "NotAWindowAction", new IntPtr(0x1234));

        Check.That(fake.LastOp).IsNull();
    }

    [Fact]
    public void ExecuteWindowAction_DoesNotQueryForegroundItself()
    {
        // The HWND is resolved by the message recipient on the dwell tick and passed in, so the
        // executor must never re-query — a re-query after the dispatcher hop is exactly the
        // foreground-change gap this design closes.
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, "Close", new IntPtr(0x1234));

        Check.That(fake.EligibilityQueries).IsEqualTo(0);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --no-build --filter "FullyQualifiedName~ExecuteWindowAction"`
Expected: FAIL — compile error, no `ExecuteWindowAction` method.

- [ ] **Step 3: Add the executor**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, add after `ExecuteMediaControl`:

```csharp
    /// <summary>
    /// Applies a window-management quick action to <paramref name="hwnd"/>. The HWND is resolved by
    /// the <see cref="QuickActionSelectionMessage"/> recipient synchronously on the dwell-completion
    /// tick and passed in here — this method must NEVER re-query the foreground, or a foreground
    /// change during the dispatcher hop could redirect Close to a different window (B2 spec §4
    /// BLOCKER A.1). Mirrors how TaskSwitcherSelectionMessage carries its HWND.
    /// </summary>
    /// <param name="payload">The <see cref="WindowAction"/> enum member name from the stored action.</param>
    /// <param name="hwnd">The eligible foreground window, or null if none was eligible.</param>
    /// <remarks>
    /// Exception safety is INHERITED from the caller: this runs inside <c>ExecuteQuickAction</c>'s
    /// try/catch. Both failure paths here return after logging rather than throwing, so that catch
    /// is not exercised today — but if this method is ever called from anywhere else, it needs its
    /// own guard, or a Win32 fault would escape onto the dispatcher.
    /// </remarks>
    private void ExecuteWindowAction(string payload, IntPtr? hwnd)
    {
        if (_context == null) return;

        if (!Enum.TryParse<WindowAction>(payload, out var windowAction))
        {
            Log.Warning("QuickAction: unknown window action payload '{Payload}'", payload);
            return;
        }

        if (hwnd is null)
        {
            // Foreground was the shell, this process, zero, or an invalid/destroyed window.
            Log.Information("Window-action {Action} skipped: no eligible foreground window", windowAction);
            return;
        }

        switch (windowAction)
        {
            case WindowAction.Minimize:
                _context.WindowManager.Minimize(hwnd.Value);
                break;
            case WindowAction.MaximizeRestore:
                _context.WindowManager.MaximizeOrRestore(hwnd.Value);
                break;
            case WindowAction.Close:
                if (!_context.WindowManager.CloseWindow(hwnd.Value))
                    Log.Information("Window-action Close: PostMessage(WM_CLOSE) returned false (UIPI-blocked by elevated foreground?)");
                break;
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ExecuteWindowAction"`
Expected: PASS — 6 tests.

- [ ] **Step 5: Wire the eager resolution and the dispatch case**

Replace the `QuickActionSelectionMessage` registration at `src/EyeCtrl.App/Services/EventCoordinator.cs:137-138`:

```csharp
        _context.Messenger.Register<EventCoordinator, QuickActionSelectionMessage>(this, (_, msg) =>
        {
            // Window actions resolve the foreground HWND SYNCHRONOUSLY, on the same tick as the
            // dwell completion (B2 spec §4 BLOCKER A.1) — the quick-actions dwell chain already
            // runs on the UI dispatcher, so a deferred re-query could target a window the user
            // was never looking at. The HWND then rides to the executor, exactly as
            // TaskSwitcherSelectionMessage carries its own. Every other action type is
            // window-independent and keeps the deferred hop unchanged.
            IntPtr? hwnd = msg.Action.Type == QuickActionType.WindowAction
                ? _context.WindowManager.TryGetEligibleForegroundWindow()
                : null;
            _context.Dispatcher.BeginInvoke(() => ExecuteQuickAction(msg.Action, hwnd));
        });
```

Change the `ExecuteQuickAction` signature and add the dispatch case. The signature at `:195` becomes:

```csharp
    private void ExecuteQuickAction(QuickAction action, IntPtr? windowActionHwnd = null)
```

and inside its `switch (action.Type)`, add before `default:`:

```csharp
                case QuickActionType.WindowAction:
                    ExecuteWindowAction(action.Payload, windowActionHwnd);
                    break;
```

- [ ] **Step 6: Build and run the full App.Tests headless subset**

Run: `dotnet build EyeCtrl.slnx -c Debug` then `dotnet test tests/EyeCtrl.App.Tests --no-build --filter "Category!=UI"`
Expected: build `0 Fehler`; tests `Fehler: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs
git commit -m "feat(quick-actions): dispatch window actions with synchronous foreground resolution"
```

---

### Task 5: App — settings editor UI

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:88` (`QaView`), `:1023-1032` (`QaTypeGlyph`), `:1034-1041` (`BuildQuickActionsPages`), `:1103-1116` (`BuildQaTypePickerPages`)

This task is WPF view wiring with no headless test seam (the file has no unit tests); it is verified by Task 8's manual check.

- [ ] **Step 1: Add the picker view state**

At `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:88`, extend the enum:

```csharp
    private enum QaView { List, PickType, PickShortcut, PickMedia, PickApp, PickWindowAction }
```

- [ ] **Step 2: Add the type glyph**

In `QaTypeGlyph`, add before the `_ =>` arm:

```csharp
        QuickActionType.WindowAction => "▣",            // ▣
```

- [ ] **Step 3: Route the new view**

In `BuildQuickActionsPages`, add before the `_ =>` arm:

```csharp
        QaView.PickWindowAction => BuildQaWindowActionPages(),
```

- [ ] **Step 4: Add the type-picker entry**

In `BuildQaTypePickerPages`, add after the `qa_type_app` button:

```csharp
        page.Add(() => Add(new DwellButton(_viewModel, "qa_type_window", "▣  Window Action",
            () => GoToQaView(QaView.PickWindowAction), BtnBase, cat, r, _dwellMs, width: 420, height: 90)));
```

- [ ] **Step 5: Add the picker page**

Add after `BuildQaMediaPages`:

```csharp
    private List<List<Action>> BuildQaWindowActionPages()
    {
        var editor = _viewModel.QuickActionsEditor;

        return ChunkPickerPages(QuickActionPresets.WindowActions.Count, "qa_win", i =>
        {
            var (label, payload) = QuickActionPresets.WindowActions[i];
            return (label, () => { editor.AddWindowAction(label, payload); GoToQaView(QaView.List); });
        });
    }
```

- [ ] **Step 6: Build**

Run: `dotnet build EyeCtrl.slnx -c Debug`
Expected: `0 Fehler`.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat(quick-actions): settings editor picker for window actions"
```

---

### Task 6: Remove the toolbar window-actions region

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`, `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Delete: `src/EyeCtrl.Core/Models/WindowActionButtons.cs`
- Modify tests: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`, `tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs`, `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs`
- Delete tests: `tests/EyeCtrl.App.Tests/Overlays/ToolbarWindowRebindTests.cs`

**Do NOT touch** `ToolbarViewModel.SetActive`, `ToolbarVisibilityAdapter`, `ToolbarAutoHideController.ReassertVisibility`, or their tests — the target-lifecycle contract is retained as an independent bug fix.

- [ ] **Step 1: Update the toolbar-count tests first (they define "done")**

In `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`, replace the `ShowWindowActions` Theory with:

```csharp
    [Fact]
    public void InitializesWith8Buttons()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Check.That(vm.Buttons.Count).IsEqualTo(8);
    }

    [Fact]
    public void ToolbarViewModel_HasNoWindowActionButtons()
    {
        // Window actions moved to the Quick Actions toolbar (2026-08-09 migration). A regression
        // that re-adds them here would restore a non-removable no-confirm Close.
        var vm = new ToolbarViewModel(_settings, new WeakReferenceMessenger());
        Check.That(vm.Buttons.Select(b => b.Id))
             .Not.Contains(new[] { "Minimize", "MaximizeRestore", "Close" });
    }
```

Delete every test in this file whose name starts with `SetWindowActionsVisible_`, plus `OnTargetActivated_WindowButton_SendsWindowActionMessage` and `ToolbarViewModel_ContainsWindowActionButtons`. Keep `SetActive_False_HiddenToolbarDoesNotDwell` and `SetActive_True_AfterHide_DoesNotInstantlyActivate`.

In `tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs`: rename `Toolbar_HasEightButtons` → `Toolbar_HasFiveButtons`, assert `IsEqualTo(5)`, and drop `Toolbar_Button_Minimize` / `_MaximizeRestore` / `_Close` from `expectedIds`, leaving Pause, SelectionMode, Keyboard, Settings, **ResetAdaptation**.

> **Do NOT `git checkout main --` this file.** `main`'s version is stale: it asserts `IsEqualTo(4)` with a `Toolbar_HasFourButtons` name and omits `Toolbar_Button_ResetAdaptation` from `expectedIds`, while `main`'s own `ToolbarWindow.Initialize` renders five buttons. The drift is invisible because `AppLaunchTests` needs a live app window and is excluded from the headless subset (`Category!=UI`) that CI runs. Write the corrected 5-button expectation, don't restore the old one.

In `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs`, delete `HandleWindowAction_RoutesToTheRightOp`, `HandleWindowAction_IneligibleForeground_NoOpsAndLogs`, `HandleWindowAction_QueriesForegroundOnce_Synchronously`, `WindowActionRecipient_IsSynchronous_FiresBeforeSendReturns`, and `InvokeHandleWindowAction`. **Keep** `FakeWindowActionManager` and `MakeContextWithWindowManager` — Task 4's tests use them.

Delete `tests/EyeCtrl.App.Tests/Overlays/ToolbarWindowRebindTests.cs` entirely (it exists only to test add/remove of the region).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"`
Expected: FAIL — both new tests. The ctor still appends the region at this point (`ShowWindowActions` defaults to true and Step 3 hasn't run yet), so `InitializesWith8Buttons` sees **11**, and `HasNoWindowActionButtons` finds all three ids. The `8` in the test name is the post-Step-3 target, not the current count — that mismatch is expected here and is exactly what Step 3 fixes.

- [ ] **Step 3: Strip the ViewModel**

In `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`:
- Delete the `if (settings.Toolbar.ShowWindowActions) buttons = buttons.Concat(WindowActionButtons.All).ToArray();` line and the comment block above it that mentions the window-actions region.
- Delete the `WindowActionIds` field, the entire `SetWindowActionsVisible` method and its XML doc.
- Delete the three `case "Minimize":` / `case "MaximizeRestore":` / `case "Close":` arms from `OnTargetActivated`.
- Remove the now-unused `using EyeCtrl.Core.Models;` **only if** nothing else in the file uses it (`PhysicalPoint`, `SelectionMode` and `DwellTarget` do — so keep it).

- [ ] **Step 4: Strip the View**

In `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`:
- Revert the signature to `public void Initialize(double buttonSize, int dwellTimeMs)`.
- Delete the `if (showWindowActions) buttons = buttons.Concat(WindowActionButtons.All).ToArray();` line and adjust the comment above the array back to `// id + symbol (Unicode)`.

**Keep** the `UpdateLayout()` call in `SetMode` and the `ActualWidth == 0` latch guard in `UpdateTargetPositions` — both are general correctness fixes, not region-specific.

- [ ] **Step 5: Strip the message and recipient**

- In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, delete `WindowActionRequestedMessage` and its XML doc.
- In `src/EyeCtrl.App/Services/EventCoordinator.cs`, delete the `WindowActionRequestedMessage` registration (the block at `:139-145`) and the whole `HandleWindowAction` method.
- Delete the file `src/EyeCtrl.Core/Models/WindowActionButtons.cs`.

- [ ] **Step 6: Build and run tests**

Run: `dotnet build EyeCtrl.slnx -c Debug` then `dotnet test tests/EyeCtrl.ViewModels.Tests --no-build`
Expected: build `0 Fehler`; tests `Fehler: 0`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(toolbar): remove the window-actions region (moved to Quick Actions)"
```

---

### Task 7: Remove the `ShowWindowActions` setting

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ToolbarSettings.cs:14-15`
- Modify: `src/EyeCtrl.App/Services/SettingsSnapshots.cs:40-43`, `src/EyeCtrl.App/Services/OverlayManager.cs:107`, `src/EyeCtrl.App/Services/EventCoordinator.cs` (reconciliation block), `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`
- Modify: `tests/EyeCtrl.App.Tests/Services/SettingsSnapshotsTests.cs`

- [ ] **Step 1: Update the snapshot test first**

In `tests/EyeCtrl.App.Tests/Services/SettingsSnapshotsTests.cs`, delete the `ToolbarSettings_ShowWindowActions_DefaultsTrue` test and the three appended lines in the snapshot-invalidation test that flip `s.Toolbar.ShowWindowActions` (keep the `ToolbarButtonDwellMs` assertions above them).

- [ ] **Step 2: Run the guard (this step expects PASS, not FAIL)**

This task is a pure deletion, so it breaks the usual red-green rhythm — there is no new behaviour to drive out with a failing test. The run below is a *guard*, not a red step.

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsSnapshotsTests"`
Expected: PASS. A FAIL here means a leftover `ShowWindowActions` reference in the test file — fix it before continuing to Step 3.

- [ ] **Step 3: Remove the setting and every consumer**

- `src/EyeCtrl.Core/Settings/ToolbarSettings.cs`: delete the `_showWindowActions` `[ObservableProperty]` field and its XML doc.
- `src/EyeCtrl.App/Services/SettingsSnapshots.cs`: revert `ToolbarSnapshot` to `(double Size, bool IsDocked, string DockedEdge, int ToolbarButtonDwellMs)` and drop the argument from `Capture`.
- `src/EyeCtrl.App/Services/OverlayManager.cs:107`: `Toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs);`
- `src/EyeCtrl.App/Services/EventCoordinator.cs`: in `OnSettingsChanged`, drop the third argument from the `Toolbar.Initialize` call and delete the `_context.ToolbarVM.SetWindowActionsVisible(...)` line plus its comment block. Keep `SetMode`, `UpdateEnabled`, `UpdateEdge`, `PositionIndicator`, `RefreshToolbarRect`.
- `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`: delete the `t_winactions` `DwellToggle` block and the `_settings.Toolbar.ShowWindowActions = t.ShowWindowActions;` line in the toolbar reset path.

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build EyeCtrl.slnx -c Debug`
Expected: `0 Fehler`. A compile error here names any consumer missed above — fix and rebuild.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(settings): remove ShowWindowActions (region no longer exists)"
```

---

### Task 8: Full verification and documentation

- [ ] **Step 1: Run every suite**

```bash
dotnet build EyeCtrl.slnx -c Debug
dotnet test tests/EyeCtrl.Core.Tests --no-build
dotnet test tests/EyeCtrl.ViewModels.Tests --no-build
dotnet test tests/EyeCtrl.Platform.Tests --no-build
dotnet test tests/EyeCtrl.App.Tests --no-build --filter "Category!=UI"
```

Expected: build `0 Fehler`; every run ends `Bestanden!` with `Fehler: 0`. **Note:** `dotnet test` output on this machine is German — per-test failures read `Fehler <FQN>`, and passing two project paths in one command fails with MSBuild error MSB1008, so run each project separately as above.

- [ ] **Step 2: Confirm no orphaned references**

```bash
grep -rn "ShowWindowActions\|WindowActionRequestedMessage\|WindowActionButtons\|SetWindowActionsVisible" src/ tests/ --include=*.cs
```
Expected: no output.

- [ ] **Step 3: Mark the superseded spec**

Add at the top of `docs/superpowers/specs/2026-08-05-window-management-toolbar-region-design.md`, immediately under the `# Design:` heading:

```markdown
> **SUPERSEDED (2026-08-09), §4 placement only.** The three actions moved to the user-customizable
> Quick Actions toolbar — see `docs/superpowers/plans/2026-08-09-window-actions-quick-actions-migration.md`.
> This spec's §1–§3 and §7–§8 (eligibility, Win32 ops, `WM_CLOSE` semantics, the shell/self guard)
> remain accurate and are implemented as specified. §4 (toolbar region), §5 (`ShowWindowActions`),
> and §6 are obsolete. The surface comparison in §2 never evaluated Quick Actions.
```

- [ ] **Step 4: Manual smoke check (requires the real app — ASK THE USER FIRST)**

This drives the real UI and takes over the user's only input channel. Do not launch without an explicit go-ahead.

1. Settings → Quick Actions → Add action → Window Action → add all three.
2. Confirm the three tiles render with `▁ □ ✕` glyphs and correct labels.
3. Summon the grid over a normal window; dwell Minimize, then Maximize/Restore twice (maximize → restore).
4. Dwell Close on a scratch window (e.g. Notepad with unsaved text) — confirm the save-changes prompt appears rather than a silent kill.
5. Summon the grid with the desktop focused — confirm no action fires and the log records "no eligible foreground window".
6. Confirm the toolbar now shows five buttons and no window controls.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs(window-mgmt): mark the B2 toolbar-region placement superseded"
```

---

## Self-review notes

- **Spec coverage:** every §9 row of the B2 spec that concerned the toolbar region maps to Task 6 or 7 (removal); every retained row (eligibility, Win32 ops, P/Invokes) is explicitly listed as unchanged in the File Structure section.
- **Type consistency:** `AddWindowAction(string label, string payload)` (Task 3) matches its call in Task 5; `ExecuteWindowAction(string payload, IntPtr? hwnd)` (Task 4 Step 3) matches both its test helper (Step 1) and its call site (Step 5); `QuickActionPresets.WindowActions` returns `(string Label, string Payload)` and is destructured as `(label, payload)` in Task 5.
- **Known risk:** Task 4's eager-resolution change alters the shape of `ExecuteQuickAction`. The optional parameter keeps every existing call site compiling; verify no other caller passes positional arguments after the first.

**Review round 1 corrections (2026-08-09) — applied:**
- Task 3's test invented `FakeQuickActionsStore`, `FakeInstalledAppProvider` and a `SaveCount` member; none exist. Rewritten against the file's real `FakeStore` / `FakeApps` / `Vm()` helper, asserting through `store.Saved`.
- Task 6's AppLaunchTests instruction said "revert to the pre-feature expectations". `main`'s version is itself stale (asserts 4, omits `ResetAdaptation`, while `main`'s View renders 5) and the drift is hidden because the test is `Category=UI` and never runs in CI. Rewritten to write a corrected 5-button expectation with an explicit "do not `git checkout main --` this file" warning.
- Task 7 Step 2 read "verify it fails / Expected: PASS". Relabelled as an explicit guard step with the reason stated.
- Task 6 Step 2's expected-failure note now explains why `InitializesWith8Buttons` sees 11 at that point.
- `ExecuteWindowAction` gained a `<remarks>` recording that its exception safety is inherited from `ExecuteQuickAction`'s try/catch.
