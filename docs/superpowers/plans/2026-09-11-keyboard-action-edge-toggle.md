# Keyboard Action + Edge-Bar Top/Bottom Flip — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an action-menu "Keyboard" action that shows the gaze keyboard, and a Close/Flip pair on the shared right-edge bar that tracks the keyboard's Top/Bottom position, with the toolbar un-pinned while the keyboard is up and the keyboard yielding to a summoned toolbar.

**Architecture:** Feature A rides the existing `InteractionAction` ring chain (enum → settings lists → icon map → `PinNote`-shaped intercept in `KeyboardInteractionDispatcher` → coordinator). Feature B adds three mechanisms: a flip handler in `KeyboardCoordinator` (pure flip + apply + snapshot refresh), an edge-detected `VisibilityChanged` event on `ToolbarVisibilityAdapter` driving `KeyboardWindow.SetToolbarOccupancy`, and a `KeyboardEdgeButtons` context set on the shared `EdgeButtonBar` that tracks the keyboard's edge. The un-pin (removing the keyboard path's auto-hide suspension) lands LAST, after the edge bar gives the keyboard its own escape.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (WeakReferenceMessenger), xUnit v3 + NFluent 3.1.0.

**Spec:** `docs/superpowers/specs/2026-09-10-keyboard-action-and-edge-toggle-design.md` (same worktree). Ledger numbers cited below refer to §5 of that spec.

## Global Constraints

- Work in the worktree `.worktrees/keyboard-action-edge-toggle` (branch `feat/keyboard-action-edge-toggle`); every command below runs from its root.
- Assertions are **NFluent** (`Check.That(...).IsEqualTo(...)`) — xUnit `Assert.*` is not used. Tests get global `Xunit` + `NFluent` usings from `tests/Directory.Build.props`.
- No mocking library — private nested fake classes or real instances, matching sibling tests.
- **Never launch `EyeCtrl.App`** and never run `Category=UI` tests — they seize the owner's only input channel. UI tests are owner-run.
- Never run two test suites at once on this box (named-pipe contention); one project per `dotnet test` invocation.
- Screen-edge strings are exactly `"Top"` / `"Bottom"` (case-sensitive validation; `SettingsService.Save` throws otherwise and the exit-time save is unguarded).
- Code style: file-scoped namespaces, Allman braces, `_camelCase` fields, `var` for obvious types.
- Commit after every task; re-check `git status` first — other sessions share this repo and only this worktree's files belong in our commits.

## File Structure

| File | Change |
|---|---|
| `src/EyeCtrl.Core/Models/InteractionAction.cs` | add `Keyboard` member |
| `src/EyeCtrl.Core/Models/ActionIcon.cs` | add `Keyboard` + `KeyboardEdge` members |
| `src/EyeCtrl.Controls/Icons/ActionIcons.cs` | two glyph paths; fix "all mice" class doc |
| `src/EyeCtrl.Controls/Icons/ToolbarIconKey.cs` | doc-comment truth fix |
| `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` | icon + friendly-name cases |
| `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs` | defaults + `KeyboardActionMigrated` one-shot |
| `src/EyeCtrl.App/App.xaml.cs` | call `EnsureKeyboardActionOnce()` in the migration block |
| `src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs` | friendly name + description cases |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | three new records |
| `src/EyeCtrl.App/Services/Coordinators/KeyboardInteractionDispatcher.cs` | intercept `Keyboard` |
| `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs` | handlers, flip, edge-bar lifecycle, un-pin, yield wiring |
| `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs` | edge-detect + `VisibilityChanged` |
| `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs` | re-expose event |
| `src/EyeCtrl.App/Services/KeyboardYield.cs` | NEW — pure yield-offset math |
| `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` | `SetToolbarOccupancy` + yield in `RepositionOnScreen` |
| `src/EyeCtrl.App/Services/ScreenGeometry.cs` | `KeyboardEdgeBarAnchor` variants |
| `src/EyeCtrl.App/Services/KeyboardEdgeButtons.cs` | NEW — edge-bar context set |
| `docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md` | supersession note |
| tests (see tasks) | extend `ActionMenuSettingsMigrationTests`, `ActionMenuViewModelSymbolTests`, `KeyboardCoordinatorTests`, `ToolbarVisibilityAdapterTests`; create `KeyboardEdgeButtonsTests`, `KeyboardYieldTests` |

---

### Task 1: Core enum, icons, ring mapping

**Files:**
- Modify: `src/EyeCtrl.Core/Models/InteractionAction.cs`
- Modify: `src/EyeCtrl.Core/Models/ActionIcon.cs`
- Modify: `src/EyeCtrl.Controls/Icons/ActionIcons.cs`
- Modify: `src/EyeCtrl.Controls/Icons/ToolbarIconKey.cs` (doc comment only)
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `InteractionAction.Keyboard`; `ActionIcon.Keyboard`, `ActionIcon.KeyboardEdge` (used by Tasks 4, 9); `ActionMenuViewModel` renders them.

- [ ] **Step 1: Write the failing tests**

In `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs`, extend the hand-maintained map inside `EveryAction_MapsToADistinctIcon` with two entries and add a new fact:

```csharp
[InteractionAction.Keyboard] = ActionIcon.Keyboard,
```

(also add `[InteractionAction.KeyboardEdge]`? **No** — `KeyboardEdge` is an `ActionIcon` member for the edge bar, not an `InteractionAction`; only add the `Keyboard` line to the map.)

Add after `Receive_ShowActionMenu_ScrollModeItemHasItsIcon`:

```csharp
[Fact]
public void Receive_ShowActionMenu_KeyboardItemHasItsIconAndLabel()
{
    // Keyboard ships layer-2 by default (PrimaryActions keeps the four defaults), and a
    // one-action message partitions it into the SECONDARY set — layer one would hold only
    // the More target, and Items.Single() would return More (the trap the expected-map fact
    // documents). Make it primary so Items.Single() is the action itself.
    var settings = new EyeCtrlSettings();
    settings.ActionMenu.PrimaryActions = new List<InteractionAction> { InteractionAction.Keyboard };
    var messenger = new WeakReferenceMessenger();
    using var vm = new ActionMenuViewModel(settings, messenger);

    messenger.Send(new ShowActionMenuMessage(
        new PhysicalPoint(500, 500),
        new List<InteractionAction> { InteractionAction.Keyboard },
        ShowBack: false,
        DwellTimeMs: 600,
        new PhysicalPoint(500, 500)));

    var item = vm.Items.Single();
    Check.That(item.Icon).IsEqualTo(ActionIcon.Keyboard);
    Check.That(item.Label).IsEqualTo("Keyboard");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelSymbolTests"`
Expected: **compile error** — `InteractionAction` does not contain a definition for `Keyboard` (the failing state for a missing enum member).

- [ ] **Step 3: Implement**

`src/EyeCtrl.Core/Models/InteractionAction.cs` — append before the closing brace:

```csharp
    /// <summary>Show the on-screen gaze keyboard (idempotent — it never hides it).</summary>
    Keyboard
```

`src/EyeCtrl.Core/Models/ActionIcon.cs` — append before the closing brace:

```csharp
    /// <summary>A keyboard with key rows and a space bar — the ring's show-keyboard action.</summary>
    Keyboard,
    /// <summary>A keyboard with up and down chevrons — flips the keyboard between screen edges.</summary>
    KeyboardEdge,
```

`src/EyeCtrl.Controls/Icons/ActionIcons.cs` — in `Build()`, after the `PinNote` entry:

```csharp
        // A keyboard: rounded body, one dash row, a second short row, and a centred space bar.
        // The first non-mouse ACTION icon — see the revised class doc above.
        [ActionIcon.Keyboard] = IconGeometry.FromPathData(
            "M6,7.5 L18,7.5 A2.5,2.5 0 0 1 20.5,10 L20.5,15 A2.5,2.5 0 0 1 18,17.5 L6,17.5 A2.5,2.5 0 0 1 3.5,15 L3.5,10 A2.5,2.5 0 0 1 6,7.5 Z"
            + " M6.8,10.7 L9.2,10.7 M10.7,10.7 L13.3,10.7 M14.8,10.7 L17.2,10.7"
            + " M6.8,13.2 L9.2,13.2 M14.8,13.2 L17.2,13.2"
            + " M9.7,15.7 L14.3,15.7"),

        // A compact keyboard with chevrons above and below: the edge bar's flip-to-other-edge
        // affordance. Chevrons change the outline from the plain Keyboard glyph, per the
        // peripheral-vision rule in the class doc.
        [ActionIcon.KeyboardEdge] = IconGeometry.FromPathData(
            "M7,10.2 L16.2,10.2 A2,2 0 0 1 18.2,12.2 L18.2,14.2 A2,2 0 0 1 16.2,16.2 L7,16.2 A2,2 0 0 1 5,14.2 L5,12.2 A2,2 0 0 1 7,10.2 Z"
            + " M7.8,12 L9.8,12 M10.8,12 L12.8,12 M13.8,12 L15.4,12"
            + " M7.8,14.4 L15.4,14.4"
            + " M9.6,7.4 L11.6,5.4 L13.6,7.4 M9.6,19 L11.6,21 L13.6,19"),
```

Same file — replace the class-doc paragraph that begins `Every action is a mouse action`
(≈ lines 17-24) with exactly this (the modifiers/DoubleClick sentences are kept verbatim
inside it; do not duplicate them elsewhere):

```csharp
/// <para>
/// <b>Most actions are mouse actions, so most icons are mice.</b> The deliberate exceptions —
/// <see cref="ActionIcon.More"/> (navigation, not a click), <see cref="ActionIcon.Keyboard"/>
/// and <see cref="ActionIcon.KeyboardEdge"/> (keyboard commands, not mouse actions) — break
/// the mouse silhouette on purpose: drawing them as mice would say they perform a click.
/// Modifiers sit in the margins and never on the body: scroll variants take chevrons at the
/// right, drag takes motion bars at the left. <see cref="ActionIcon.DoubleClick"/> is the
/// other exception — it must differ from <see cref="ActionIcon.LeftClick"/> in outline
/// rather than in detail, because with labels hidden a mark inside the same silhouette is
/// invisible in peripheral vision, which is where a gaze user finds a target before looking
/// straight at it.
/// </para>
```

`src/EyeCtrl.Controls/Icons/ToolbarIconKey.cs` — find the class-doc sentence stating `ActionIcon`'s set "is deliberately all mice" and reword to "was authored as all mice until the keyboard glyphs joined it" so it no longer claims a falsehood.

`src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` — in `FriendlyName`, add before the `null` arm:

```csharp
        InteractionAction.Keyboard => "Keyboard",
```

In `GetActionIcon`, add before the throw arm:

```csharp
        InteractionAction.Keyboard => ActionIcon.Keyboard,
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelSymbolTests"`
Expected: PASS (all facts in the class).

- [ ] **Step 5: Render-check the glyphs (visual gate)**

Build the solution (`dotnet build EyeCtrl.slnx`), then ask the owner to run the app OR defer to the owner-run UI pass in Task 12 — the glyphs must be seen at real size before merge (spec R1). Do not launch the app yourself.

- [ ] **Step 6: Commit**

```bash
git status   # only the six files above
git add src/EyeCtrl.Core/Models/InteractionAction.cs src/EyeCtrl.Core/Models/ActionIcon.cs src/EyeCtrl.Controls/Icons/ActionIcons.cs src/EyeCtrl.Controls/Icons/ToolbarIconKey.cs src/EyeCtrl.ViewModels/ActionMenuViewModel.cs tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs
git commit -m "feat(ring): InteractionAction.Keyboard with drawn glyph and ring mapping"
```

---

### Task 2: Settings defaults + one-shot migration

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs` (migration block, ~:523-529)
- Test: `tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsMigrationTests.cs`

**Interfaces:**
- Consumes: `InteractionAction.Keyboard` (Task 1).
- Produces: `ActionMenuSettings.KeyboardActionMigrated` (bool, `[ObservableProperty]`), `EnsureKeyboardActionOnce()` — called once at startup; default `VisibleActions` contains `Keyboard`, default `PrimaryActions` does not.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsMigrationTests.cs`:

```csharp
public class ActionMenuSettingsKeyboardActionTests
{
    [Fact]
    public void Defaults_ContainKeyboard_OnTheSecondLayer()
    {
        var s = new ActionMenuSettings();

        Check.That(s.VisibleActions).Contains(InteractionAction.Keyboard);
        Check.That(s.PrimaryActions).Not.Contains(InteractionAction.Keyboard);
    }

    [Fact]
    public void EnsureKeyboardActionOnce_AddsKeyboard_WhenAbsent_AndSetsFlag()
    {
        var s = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
        };

        s.EnsureKeyboardActionOnce();

        Check.That(s.VisibleActions).Contains(InteractionAction.Keyboard);
        Check.That(s.KeyboardActionMigrated).IsTrue();
    }

    [Fact]
    public void EnsureKeyboardActionOnce_IsNoOp_WhenFlagAlreadySet()
    {
        // The user-owned-list rule: once migrated, the list is never touched again — even
        // if the user then removed Keyboard, a later call must not re-add it.
        var s = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
            KeyboardActionMigrated = true,
        };

        s.EnsureKeyboardActionOnce();

        Check.That(s.VisibleActions).Not.Contains(InteractionAction.Keyboard);
    }

    [Fact]
    public void EnsureKeyboardActionOnce_DoesNotDuplicate_WhenAlreadyPresent()
    {
        var s = new ActionMenuSettings();

        s.EnsureKeyboardActionOnce();

        Check.That(s.VisibleActions.Count(a => a == InteractionAction.Keyboard)).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ActionMenuSettingsKeyboardActionTests"`
Expected: compile error — no `KeyboardActionMigrated` / `EnsureKeyboardActionOnce`.

- [ ] **Step 3: Implement**

`src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`:

In the `_visibleActions` initializer and in the `EnsureDefaults()` defaults array, append `InteractionAction.Keyboard` after `InteractionAction.PinNote`.

After the `_primaryMigrated` field's property block (next to `MigratePrimaryOnce`), add:

```csharp
    /// <summary>
    /// Whether <see cref="EnsureKeyboardActionOnce"/> has already run for this settings file.
    /// Absent from every pre-2026-09 file (so it loads false) and set on the first migration.
    /// Same pattern and reason as <see cref="DefaultsMigrated"/>: the action did not exist
    /// when any existing file was written, so the one-shot add cannot re-add anything the
    /// user deliberately removed.
    /// </summary>
    [ObservableProperty] private bool _keyboardActionMigrated;

    /// <summary>
    /// Adds <see cref="InteractionAction.Keyboard"/> to <see cref="VisibleActions"/> exactly
    /// once per settings file. The startup caller must use this rather than editing the list —
    /// see <see cref="KeyboardActionMigrated"/>.
    /// </summary>
    public void EnsureKeyboardActionOnce()
    {
        if (KeyboardActionMigrated) return;
        if (VisibleActions is null) VisibleActions = new List<InteractionAction>();
        if (!VisibleActions.Contains(InteractionAction.Keyboard))
            VisibleActions.Add(InteractionAction.Keyboard);
        KeyboardActionMigrated = true;
    }
```

`src/EyeCtrl.App/App.xaml.cs` — in the startup migration block (~:523-529, after `settings.ActionMenu.MigratePrimaryOnce();`):

```csharp
        settings.ActionMenu.EnsureKeyboardActionOnce();
```

- [ ] **Step 4: Update the four existing tests the new default breaks**

Adding `Keyboard` to the default `VisibleActions` (layer 2 — not primary) changes what four
existing facts see. All four edits belong in THIS task's commit:

1. `tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsPrimaryTests.cs:47-51` —
   `SecondaryActions` `ContainsExactly(Drag, ScrollUp, ScrollDown, PinNote)` gains
   `InteractionAction.Keyboard` appended LAST (the derived list preserves `VisibleActions`
   order, and Keyboard is appended last there).
2. `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLayerTests.cs:80-83` — the
   second-layer action list `ContainsExactly(...)` likewise appends
   `InteractionAction.Keyboard` last.
3. `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLayerTests.cs:90-101` —
   `TheSecondLayer_ReusesTheSameFiveSlots` asserts `layerTwoPositions
   ContainsExactly(layerOnePositions)`. That symmetry premise is knowingly broken by the
   5→6 growth (spec ledger #3): layer one is 5 slots (4 primary + More), layer two becomes
   6 (5 actions + Back), and the angle step changes, so Back no longer lands where More
   was. Rewrite the fact as a count + sanity guard, renaming it:

```csharp
    [Fact]
    public void TheSecondLayer_GrowsToSixSlots_WithBackStillOnTheRing()
    {
        // Since the Keyboard action joined the default second layer (spec ledger #3), the
        // layers hold different slot counts (5 vs 6) and the old "Back lands exactly where
        // More was" symmetry is knowingly given up. Guard the weaker but still load-bearing
        // property: every layer-two target is a real on-ring position and the Back target
        // still exists.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);
        var layerTwo = vm.Items.ToList();

        Check.That(layerTwo).HasSize(6);
        Check.That(layerTwo.Count(i => i.Kind == ActionMenuItemKind.BackToPrimary)).IsEqualTo(1);
        Check.That(layerTwo.Select(i => i.Position)).ContainsOnlyElementsThatMatch(
            p => !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsInfinity(p.X) && !double.IsInfinity(p.Y));
    }
```

(Reuse the file's existing `Defaults()` / `Show()` helpers; keep the original fact's
`layerOne` extraction only if still needed.)

4. `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLayerTests.cs:198` —
   `itemsSeenByTheRedrawHandler` `IsEqualTo(5)` becomes `IsEqualTo(6)`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ActionMenuSettings"`
Expected: PASS (new class + all pre-existing migration facts, including the updated
`SecondaryActions` fact). Then
`dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelLayerTests"`
Expected: PASS (updated facts).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ActionMenuSettings.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsMigrationTests.cs tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsPrimaryTests.cs tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLayerTests.cs
git commit -m "feat(settings): Keyboard action on by default (layer 2) with one-shot migration"
```

---

### Task 3: Settings page rows

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs`
- Test: existing `SettingsRowDescriptionCompletenessTests` (locate with
  `grep -rn "SettingsRowDescriptionCompletenessTests" tests/ --include="*.cs" | grep -v obj`)

**Interfaces:**
- Consumes: `InteractionAction.Keyboard` (Task 1).
- Produces: settings rows render with a description; no new API.

- [ ] **Step 1: Run the completeness gate to see it fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsRowDescriptionCompleteness"`
Expected: FAIL naming the bare `am_Keyboard` row (description null via the `_ => null` arm).

- [ ] **Step 2: Implement**

`ActionMenuPageSource.FriendlyName` — add before the fallback arm:

```csharp
        InteractionAction.Keyboard => "Keyboard",
```

`ActionMenuPageSource.ActionDescription` — add before the `_ => null` arm:

```csharp
        InteractionAction.Keyboard => "Opens the gaze keyboard for typing.",
```

- [ ] **Step 3: Run the gate to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsRowDescriptionCompleteness"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs
git commit -m "feat(settings): Keyboard action rows - friendly name and description"
```

---

### Task 4: Messages, show handler, dispatcher intercept (Feature A end-to-end)

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardInteractionDispatcher.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs`

**Interfaces:**
- Consumes: nothing new from earlier tasks (messages are self-contained).
- Produces: `KeyboardShowRequestedMessage`, `KeyboardHideRequestedMessage` (used by Task 10), `KeyboardEdgeToggledMessage` (used by Tasks 5, 9); `KeyboardCoordinator.ShouldShow(bool)` internal static seam.

- [ ] **Step 1: Write the failing tests**

Append to `KeyboardCoordinatorTests`:

```csharp
    [Fact]
    public void ShouldShow_IsFalse_WhenAlreadyVisible_AndTrueOtherwise()
    {
        // The ring's Keyboard action is an idempotent SHOW (spec ledger #1): the guard is the
        // whole seam — the handler itself touches WPF windows and is not headless-reachable
        // (same reason ApplyEmojiSkinTone was extracted).
        Check.That(KeyboardCoordinator.ShouldShow(alreadyVisible: true)).IsFalse();
        Check.That(KeyboardCoordinator.ShouldShow(alreadyVisible: false)).IsTrue();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"`
Expected: compile error — no `ShouldShow` on `KeyboardCoordinator`.

- [ ] **Step 3: Implement**

`src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` — after `public record KeyboardToggledMessage;`:

```csharp
/// <summary>Show the gaze keyboard (idempotent — never hides). Sent by the action-menu ring's
/// Keyboard action; handled with the toolbar toggle's suppression semantics (spec ledger #1-#2).</summary>
public record KeyboardShowRequestedMessage;
/// <summary>Hide the gaze keyboard and release fixation suppression. Sent by the keyboard edge
/// bar's Close button (spec ledger #13) — NOT the text-field seam message.</summary>
public record KeyboardHideRequestedMessage;
/// <summary>Flip the keyboard between the Top and Bottom screen edges. Sent by the keyboard edge
/// bar's Flip button (spec ledger #15).</summary>
public record KeyboardEdgeToggledMessage;
```

`KeyboardCoordinator.cs` — in `OnInitialize`, after the `KeyboardToggledMessage` registration:

```csharp
        // Ring / edge-bar show: idempotent (never hides), with the toolbar toggle's
        // suppression semantics — a show that leaves free-look active would let the next
        // fixation pop an action menu over the keyboard.
        messenger.Register<KeyboardCoordinator, KeyboardShowRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() =>
            {
                if (ShouldShow(_modal.KeyboardVisible))
                    SetKeyboardVisible(true, manageSuppression: true);
            }));

        // Edge-bar Close: the keyboard's own escape — hides AND releases suppression so the
        // ring comes back (spec ledger #13).
        messenger.Register<KeyboardCoordinator, KeyboardHideRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(false, manageSuppression: true)));
```

Next to `ApplyEmojiSkinTone` (the extracted-static precedent):

```csharp
    /// <summary>The ring action's idempotence: already visible means the activation is a no-op.
    /// Split out so the guard is headless-testable — the handler itself touches the real
    /// KeyboardWindow (same reason as <see cref="ApplyEmojiSkinTone"/>).</summary>
    internal static bool ShouldShow(bool alreadyVisible) => !alreadyVisible;
```

`KeyboardInteractionDispatcher.cs` — in `HandleActionRequested`, immediately after the `PinNote` block (before the `switch`):

```csharp
        // The ring's keyboard action: no click point, no input simulation — just the show
        // message. Falls through to the shared target-highlight cleanup below (unlike PinNote,
        // which duplicates it before its return).
        if (msg.Action == InteractionAction.Keyboard)
            Context.Messenger.Send(new KeyboardShowRequestedMessage());
```

The `switch` below needs no case (`Keyboard` matches nothing there).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"`
Expected: PASS (existing facts + the new one).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs src/EyeCtrl.App/Services/Coordinators/KeyboardInteractionDispatcher.cs tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs
git commit -m "feat(keyboard): ring action shows the keyboard - idempotent show/hide message pair"
```

---

### Task 5: Flip core — pure flip + apply seam

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs`

**Interfaces:**
- Consumes: `KeyboardEdgeToggledMessage` (Task 4).
- Produces: `KeyboardCoordinator.FlipScreenEdge(string)` and `ApplyKeyboardEdge(EyeCtrlSettings, SettingsService)` internal statics — `ApplyKeyboardEdge` returns `(string Flipped, KeyboardStackSnapshot Snapshot)`. `HandleKeyboardEdgeToggled()` (private, Context-bound) is completed in Task 10 when the edge-bar rebuild joins it.

- [ ] **Step 1: Write the failing tests**

Append to `KeyboardCoordinatorTests` (reuse the file's `TempSettingsFile` nested class):

```csharp
    [Fact]
    public void FlipScreenEdge_FlipsBothWays_AndNormalizesToCanonicalCase()
    {
        // Comparison is case-insensitive but the OUTPUT is canonical: validation is a
        // case-sensitive membership test and the unguarded exit-time save turns a stray
        // casing into an app crash (spec B1 2a).
        Check.That(KeyboardCoordinator.FlipScreenEdge("Top")).IsEqualTo("Bottom");
        Check.That(KeyboardCoordinator.FlipScreenEdge("Bottom")).IsEqualTo("Top");
        Check.That(KeyboardCoordinator.FlipScreenEdge("top")).IsEqualTo("Bottom");
        Check.That(KeyboardCoordinator.FlipScreenEdge("garbage")).IsEqualTo("Top");
    }

    [Fact]
    public void ApplyKeyboardEdge_MutatesSettings_PersistsAndReturnsFreshSnapshot()
    {
        var settings = new EyeCtrlSettings { Keyboard = { ScreenEdge = "Bottom" } };
        using var temp = new TempSettingsFile();

        var (flipped, snapshot) = KeyboardCoordinator.ApplyKeyboardEdge(settings, temp.Service);

        Check.That(flipped).IsEqualTo("Top");
        Check.That(settings.Keyboard.ScreenEdge).IsEqualTo("Top");
        // The phantom-rebuild guard (spec C5): the snapshot must reflect the write, or the
        // next settings-apply rebuilds the whole keyboard + prediction stack for nothing.
        Check.That(snapshot).IsEqualTo(KeyboardStackSnapshot.Capture(settings));
    }
```

Note: `ApplyKeyboardEdge` covers settings mutation, persistence, and the snapshot refresh.
The spec's §6 line about asserting the explicit `SetScreenEdge` window call is a **recorded
deviation**: the call lives in the Context-bound handler and touches a real `KeyboardWindow`,
so it is not headless-assertable — it is covered by Task 10 review, Task 12's owner pass
(flip moves the visible keyboard), and the flip handler code being four lines long.

Note: if `EyeCtrlSettings.Keyboard` is not object-initializable inline, construct then assign: `var settings = new EyeCtrlSettings(); settings.Keyboard.ScreenEdge = "Bottom";` — follow whichever compiles.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"`
Expected: compile error — no `FlipScreenEdge` / `ApplyKeyboardEdge`.

- [ ] **Step 3: Implement**

In `KeyboardCoordinator`, next to `ApplyEmojiSkinTone`:

```csharp
    /// <summary>
    /// Pure flip: "Top" (any case) → "Bottom", anything else → "Top". The OUTPUT is always
    /// canonical — <c>KeyboardSettings.Validate</c> is a case-sensitive membership test,
    /// <c>SettingsService.Save</c> throws on failure, and <c>App.OnExit</c>'s final save is
    /// unguarded (spec B1 2a).
    /// </summary>
    internal static string FlipScreenEdge(string edge) =>
        string.Equals(edge, "Top", StringComparison.OrdinalIgnoreCase) ? "Bottom" : "Top";

    /// <summary>
    /// The persistable half of an edge flip: write the canonical flipped value, save debounced,
    /// and re-capture the reload snapshot AFTER the write (spec C5 — same discipline as
    /// <see cref="ApplyEmojiSkinTone"/>, whose remarks document the phantom-rebuild failure).
    /// The window-apply half (SetScreenEdge + edge-bar rebuild) lives in the Context-bound
    /// handler.
    /// </summary>
    internal static (string Flipped, KeyboardStackSnapshot Snapshot) ApplyKeyboardEdge(
        EyeCtrlSettings settings, SettingsService settingsService)
    {
        var flipped = FlipScreenEdge(settings.Keyboard.ScreenEdge);
        settings.Keyboard.ScreenEdge = flipped;
        settingsService.SaveDebounced(settings);
        return (flipped, KeyboardStackSnapshot.Capture(settings));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs
git commit -m "feat(keyboard): pure edge flip with canonical output and snapshot-refreshing apply seam"
```

---

### Task 6: Toolbar visibility transitions become observable

**Files:**
- Modify: `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs` (UI category — **owner-run**; see Global Constraints)

**Interfaces:**
- Consumes: nothing new.
- Produces: `ToolbarVisibilityAdapter.VisibilityChanged` (`event Action<bool>?`, fired on the UI thread inside the dispatcher lambdas, once per real transition — re-asserts deduplicated); `ToolbarLifecycleCoordinator.ToolbarVisibilityChanged` (`event Action<bool>?`, forwarding) — consumed by Task 8.

- [ ] **Step 1: Write the failing test (UI-gated)**

Append to `ToolbarVisibilityAdapterTests` using the file's existing helpers (`EnsureApplicationWithTokens`, `FakeOverlayManager`, `PumpDispatcher` — check their exact signatures in the file and mirror `HideToolbar_CallsSetActiveFalse_BeforeToolbarHide`'s setup):

```csharp
    /// <summary>
    /// Spec B3 #5: VisibilityChanged fires once per real transition and not on re-asserts —
    /// SetPaused/ReassertVisibility call HideToolbar repeatedly, so an un-edge-detected event
    /// would fire continuously and the keyboard would churn. Also pins the UI-thread contract
    /// (fired inside the dispatcher lambda: a gaze-thread send would write window Left/Top off
    /// the UI thread, which this app turns into a process kill).
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void VisibilityChanged_FiresOncePerTransition_NotOnReasserts_AndOnUiThread()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);
        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        var fired = new List<(int ThreadId, bool Visible)>();
        adapter.VisibilityChanged += visible => fired.Add((Environment.CurrentManagedThreadId, visible));

        try
        {
            toolbar.Show();
            PumpDispatcher();
            adapter.HideToolbar(paused: false);
            PumpDispatcher();
            adapter.HideToolbar(paused: false);   // re-assert — must NOT fire again
            PumpDispatcher();
            adapter.ShowToolbar();
            PumpDispatcher();
            adapter.ForceShow();                  // already visible — must NOT fire
            PumpDispatcher();

            Check.That(fired).HasSize(2);
            Check.That(fired[0].Visible).IsFalse();
            Check.That(fired[1].Visible).IsTrue();
            Check.That(fired.Select(f => f.ThreadId)).ContainsOnlyElementsThatMatch(
                id => id == Environment.CurrentManagedThreadId);
        }
        finally
        {
            toolbar.Close();
            indicator.Close();
        }
    }
```

(If `ExposedDwellToolbarViewModel` differs in this file's latest form, reuse whatever VM helper the neighboring facts construct — the point is a real `ToolbarViewModel`.)

Append a second UI fact for the spec's summoned-dwellability promise (spec §6): a toolbar
that was hidden and then shown while the keyboard is up must accumulate dwell on its
buttons (the messenger + `SetActive(true)` chain — spec B2 #3; `KeyboardVisible` must not
gate it):

```csharp
    [StaFact]
    [Trait("Category", "UI")]
    public void SummonedToolbar_AccumulatesDwell_WhileKeyboardVisible()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);
        var modal = new ModalInteractionState { KeyboardVisible = true };
        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        var progress = new List<double>();
        vm.DwellDetector.TargetProgress += (id, p) => progress.Add(p);

        try
        {
            toolbar.Hide();
            PumpDispatcher();
            adapter.ShowToolbar();      // the summon transition — must reactivate dwell
            PumpDispatcher();

            vm.DwellDetector.AddTarget(new DwellTarget("Keyboard", new PhysicalPoint(100, 100),
                radius: 50, dwellTimeMs: 1000));
            vm.Receive(MakeGazeMessage(100, 100, 0));          // enter
            vm.Receive(MakeGazeMessage(100, 100, 500_000));    // 50% of the dwell

            Check.That(progress).HasElementThatMatches(p => p > 0.4);
        }
        finally
        {
            modal.KeyboardVisible = false;
            toolbar.Close();
            indicator.Close();
        }
    }
```

(Match the neighboring facts' exact helper signatures — `MakeGazeMessage` and the progress
observation style in `HideToolbar_CallsSetActiveFalse_BeforeToolbarHide` are the models;
if `DwellDetector.TargetProgress` is not subscribable on the exposed helper, observe
progress the way that sibling fact does.)

- [ ] **Step 2: Run test to verify it fails**

Owner-run (UI): `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarVisibilityAdapterTests.VisibilityChanged"`
Expected: compile error — no `VisibilityChanged` on the adapter. (Non-owner executors: proceed to implementation; this gate is executed in Task 12's owner pass.)

- [ ] **Step 3: Implement**

`ToolbarVisibilityAdapter.cs`:

```csharp
    // Edge-detect + event (spec B3 #5). _lastVisible seeds true because App.ShowOverlays
    // shows the toolbar directly at startup without passing through this adapter — the
    // adapter never sees that transition, and seeding false would emit a spurious "true"
    // on the first re-assert.
    private bool _lastVisible = true;

    /// <summary>
    /// Fires once per real show/hide transition, ON the UI thread (inside each dispatcher
    /// lambda, after the window calls). Re-asserts — SetPaused/ReassertVisibility call the
    /// hide paths repeatedly — are deduplicated. NOTE: not every driver funnels through the
    /// adapter (App.ShowOverlays shows the toolbar directly), so consumers must not treat
    /// silence as "hidden" (spec B3 #7).
    /// </summary>
    public event Action<bool>? VisibilityChanged;

    private void OnVisibilityChanged(bool visible)
    {
        if (_lastVisible == visible) return;
        _lastVisible = visible;
        VisibilityChanged?.Invoke(visible);
    }
```

In `ShowToolbar`'s lambda, append `_ = 0; OnVisibilityChanged(true);` — no: append exactly `OnVisibilityChanged(true);` after `_toolbarVm.SetActive(true);`. In `HideToolbar`'s lambda, append `OnVisibilityChanged(false);` after the `else { _overlays.PauseIndicator.Hide(); }` block. In `ForceShow`'s lambda, append `OnVisibilityChanged(true);` after `_toolbarVm.SetActive(true);`. `SetActive` and `PositionIndicator` fire nothing (no visibility change).

`ToolbarLifecycleCoordinator.cs` — add a forwarding event, and wire it inside
`SetupToolbarAutoHide()` where `_visibility = vis;` is assigned (after
`controller.Attach(vis);` — note the assignment lives in that method, not `Initialize`):

```csharp
    /// <summary>Re-exposes the visibility adapter's edge-detected transition event (spec B3 #5),
    /// so KeyboardCoordinator can yield the keyboard without referencing the adapter. Fires on
    /// the UI thread.</summary>
    public event Action<bool>? ToolbarVisibilityChanged;
```

and at the wiring point:

```csharp
        vis.VisibilityChanged += visible => ToolbarVisibilityChanged?.Invoke(visible);
```

- [ ] **Step 4: Build + run the coordinator suite (headless part)**

Run: `dotnet build EyeCtrl.slnx` then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarLifecycleCoordinatorTests"`
Expected: build PASS; tests PASS (the new event is additive).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterTests.cs
git commit -m "feat(toolbar): edge-detected UI-thread VisibilityChanged event on the visibility adapter"
```

---

### Task 7: Keyboard yield math + window occupancy

**Files:**
- Create: `src/EyeCtrl.App/Services/KeyboardYield.cs`
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`
- Test: Create `tests/EyeCtrl.App.Tests/Services/KeyboardYieldTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `KeyboardYield.VerticalOffset(string keyboardEdge, bool toolbarOccupied, string toolbarEdge, double extentDip, double keyboardTopDip, double keyboardHeightDip, double workAreaTopDip, double workAreaBottomDip)` → `double` (downward shift in DIP); `KeyboardWindow.SetToolbarOccupancy(bool occupied, string edge, double extentDip)` — consumed by Task 8.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Services/KeyboardYieldTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;

public class KeyboardYieldTests
{
    // Screen ≈ 1920x1040 DIP work area; keyboard 1000x400 docked Bottom (Top = 640) or Top
    // (Top = 0); toolbar extent 132 DIP. Same-edge yield = one toolbar height toward center.

    [Fact]
    public void NoYield_WhenToolbarAbsent_OrEdgeDiffers()
    {
        Check.That(KeyboardYield.VerticalOffset("bottom", false, "bottom", 132, 640, 400, 0, 1040)).IsEqualTo(0);
        Check.That(KeyboardYield.VerticalOffset("bottom", true, "top", 132, 640, 400, 0, 1040)).IsEqualTo(0);
        Check.That(KeyboardYield.VerticalOffset("top", true, "left", 132, 0, 400, 0, 1040)).IsEqualTo(0);
    }

    [Fact]
    public void TopEdge_YieldsDown_ByExtent()
    {
        Check.That(KeyboardYield.VerticalOffset("top", true, "top", 132, 0, 400, 0, 1040)).IsEqualTo(132);
    }

    [Fact]
    public void BottomEdge_YieldsUp_ByExtent()
    {
        Check.That(KeyboardYield.VerticalOffset("bottom", true, "bottom", 132, 640, 400, 0, 1040)).IsEqualTo(-132);
    }

    [Fact]
    public void CaseInsensitiveEdgeComparison()
    {
        Check.That(KeyboardYield.VerticalOffset("Top", true, "TOP", 132, 0, 400, 0, 1040)).IsEqualTo(132);
    }

    [Fact]
    public void Clamps_ExtremeExtent_ToKeepKeyboardInWorkArea()
    {
        // A pathological toolbar measurement must not push the keyboard off-screen (spec B3 #6):
        // top-docked keyboard, absurd 5000 DIP extent → clamped to (workAreaBottom - height - top).
        Check.That(KeyboardYield.VerticalOffset("top", true, "top", 5000, 0, 400, 0, 1040)).IsEqualTo(640);

        // Bottom-docked, absurd extent → clamped to work-area top.
        Check.That(KeyboardYield.VerticalOffset("bottom", true, "bottom", 5000, 640, 400, 0, 1040)).IsEqualTo(-640);
    }

    [Fact]
    public void NegativeExtent_IsClampedToZero()
    {
        Check.That(KeyboardYield.VerticalOffset("top", true, "top", -50, 0, 400, 0, 1040)).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardYieldTests"`
Expected: compile error — `KeyboardYield` does not exist.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.App/Services/KeyboardYield.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>
/// Pure math for the keyboard's yield to a summoned toolbar (spec B3 #6, ledger #5): when the
/// toolbar is on screen at the SAME vertical edge as the keyboard, the keyboard shifts one
/// toolbar-extent toward screen center; otherwise no shift. Extracted from
/// <c>KeyboardWindow.RepositionOnScreen</c> so the math is headless-testable.
/// </summary>
internal static class KeyboardYield
{
    /// <param name="keyboardEdge">The keyboard's ScreenEdge string ("Top"/"Bottom", any case).</param>
    /// <param name="toolbarOccupied">Whether the toolbar window is on screen.</param>
    /// <param name="toolbarEdge">The toolbar's effective edge; "none" never matches.</param>
    /// <param name="extentDip">Toolbar thickness in DIP (window ActualHeight).</param>
    /// <param name="keyboardTopDip">The keyboard's un-yielded Top from PlaceAtEdge.</param>
    /// <param name="keyboardHeightDip">Keyboard window height in DIP.</param>
    /// <param name="workAreaTopDip">Work-area top in DIP.</param>
    /// <param name="workAreaBottomDip">Work-area bottom in DIP.</param>
    /// <returns>Downward shift in DIP (negative = upward), clamped so the keyboard stays in the work area.</returns>
    internal static double VerticalOffset(
        string keyboardEdge, bool toolbarOccupied, string toolbarEdge,
        double extentDip, double keyboardTopDip, double keyboardHeightDip,
        double workAreaTopDip, double workAreaBottomDip)
    {
        if (!toolbarOccupied || extentDip <= 0) return 0;

        var kb = keyboardEdge?.ToLowerInvariant() ?? "bottom";
        var tb = toolbarEdge?.ToLowerInvariant() ?? "none";
        if (!string.Equals(kb, tb, StringComparison.Ordinal)) return 0;

        var desired = kb == "top" ? extentDip : -extentDip;
        var newTop = keyboardTopDip + desired;
        var maxTop = workAreaBottomDip - keyboardHeightDip;
        if (newTop < workAreaTopDip) desired += workAreaTopDip - newTop;
        if (newTop > maxTop) desired -= newTop - maxTop;
        return desired;
    }
}
```

`src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` — the window lives in
`EyeCtrl.App.Overlays` and does not import `EyeCtrl.App.Services`; add to its using block:

```csharp
using EyeCtrl.App.Services;   // KeyboardYield
```

Add fields near `_screenEdge`:

```csharp
    // Toolbar occupancy for the yield (spec B3 #6): when the toolbar is on screen at this
    // keyboard's edge, RepositionOnScreen shifts the window by its extent toward center.
    private bool _toolbarOccupied;
    private string _toolbarEdge = "none";
    private double _toolbarExtentDip;
```

Add the public seam (near `SetScreenEdge`):

```csharp
    /// <summary>
    /// Sets the toolbar occupancy the yield consumes (spec B3 #6). Extent is the toolbar
    /// window's ActualHeight in DIP — both windows position in DIP via PlaceAtEdge; do NOT
    /// pass the coordinator's cached physical-px rect.
    /// </summary>
    public void SetToolbarOccupancy(bool occupied, string edge, double extentDip)
    {
        _toolbarOccupied = occupied;
        _toolbarEdge = occupied ? edge : "none";
        _toolbarExtentDip = extentDip;
        RepositionOnScreen();
    }
```

Rewrite `RepositionOnScreen`:

```csharp
    private void RepositionOnScreen()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var edge = string.Equals(_screenEdge, "top", StringComparison.OrdinalIgnoreCase) ? "top" : "bottom";
        (Left, Top) = OverlayPositioning.PlaceAtEdge(SystemParameters.WorkArea, edge, ActualWidth, ActualHeight);

        var work = SystemParameters.WorkArea;
        Top += KeyboardYield.VerticalOffset(
            edge, _toolbarOccupied, _toolbarEdge, _toolbarExtentDip,
            Top, ActualHeight, work.Top, work.Bottom);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardYieldTests"`
Expected: PASS (6 facts). Then `dotnet build EyeCtrl.slnx` — PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/KeyboardYield.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs tests/EyeCtrl.App.Tests/Services/KeyboardYieldTests.cs
git commit -m "feat(keyboard): pure yield math + SetToolbarOccupancy shifts the window aside"
```

---

### Task 8: Coordinator yield wiring (subscribe + initial state)

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs`

**Interfaces:**
- Consumes: `ToolbarLifecycleCoordinator.ToolbarVisibilityChanged` (Task 6), `KeyboardWindow.SetToolbarOccupancy` (Task 7), `KeyboardEdgeButtons` NOT yet (Task 10).
- Produces: live yield wiring; no new API.

- [ ] **Step 1: Implement**

In `KeyboardCoordinator.OnInitialize`, after the messenger registrations (the `_toolbar` field is the `ToolbarLifecycleCoordinator` ctor dependency — this is its new dereference site, replacing the suspension call removed in Task 11):

```csharp
        // Yield wiring (spec B3 #7): react to real toolbar transitions, and on keyboard show
        // query the window directly — the startup show bypasses the adapter, so a tracked
        // adapter flag can read "not occupied" while the toolbar is on screen.
        _toolbar.ToolbarVisibilityChanged += OnToolbarVisibilityChanged;
```

Add the handler + the initial-state helper:

```csharp
    private void OnToolbarVisibilityChanged(bool toolbarVisible)
    {
        if (Context is null) return;
        Context.Dispatcher.BeginInvoke(() => ApplyToolbarOccupancy(toolbarVisible));
    }

    private void ApplyToolbarOccupancy(bool toolbarVisible)
    {
        if (Context is null) return;
        var toolbar = Context.OverlayManager.Toolbar;
        if (!toolbarVisible || !toolbar.IsVisible)
        {
            Context.OverlayManager.Keyboard.SetToolbarOccupancy(false, "none", 0);
            return;
        }

        // Undocked/floating toolbars raise no transitions and never yield (ledger #8);
        // "none" matches no keyboard edge in KeyboardYield.
        var edge = Context.Settings.Toolbar.IsDocked ? Context.Settings.Toolbar.DockedEdge : "none";
        Context.OverlayManager.Keyboard.SetToolbarOccupancy(true, edge, toolbar.ActualHeight);
    }

    private void ApplyInitialToolbarOccupancy()
    {
        if (Context is null) return;
        ApplyToolbarOccupancy(Context.OverlayManager.Toolbar.IsVisible);
    }
```

In `SetKeyboardVisible`, inside the `if (visible)` branch, after `Context.OverlayManager.Keyboard.Show();`:

```csharp
            ApplyInitialToolbarOccupancy();
```

In `OnDispose` (the existing dispose path that unsubscribes `SkinToneChanged`), add:

```csharp
        _toolbar.ToolbarVisibilityChanged -= OnToolbarVisibilityChanged;
```

- [ ] **Step 2: Build + run coordinator tests**

Run: `dotnet build EyeCtrl.slnx` then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"`
Expected: build PASS, tests PASS (additive wiring; no headless assertion — the transition math is covered by `KeyboardYieldTests`, the adapter event by Task 6's UI test).

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs
git commit -m "feat(keyboard): yield wiring - toolbar transitions and show-time initial occupancy"
```

---

### Task 9: Edge-bar geometry + button definitions

**Files:**
- Modify: `src/EyeCtrl.App/Services/ScreenGeometry.cs`
- Create: `src/EyeCtrl.App/Services/KeyboardEdgeButtons.cs`
- Test: Create `tests/EyeCtrl.App.Tests/Services/KeyboardEdgeButtonsTests.cs`

**Interfaces:**
- Consumes: `ActionIcon.Close` (existing), `ActionIcon.KeyboardEdge` (Task 1), `KeyboardHideRequestedMessage`/`KeyboardEdgeToggledMessage` (Task 4), `EdgeButtonDefinition` (existing).
- Produces: `ScreenGeometry.KeyboardEdgeBarAnchor(bool keyboardAtTop, double logicalRadius, double logicalWindowMargin = EdgeButtonWindowMarginLogical)` + pure overload `(bool, double, double, double, double)`; `KeyboardEdgeButtons.CloseId`/`FlipId` consts, `KeyboardEdgeButtons.Definitions(int dwellMs, bool keyboardAtTop)`; `KeyboardEdgeButtons.RightEdgeInsetLogical` const — consumed by Task 10.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Services/KeyboardEdgeButtonsTests.cs` (model:
`ActionMenuEdgeButtonsTests.cs` in the same folder — same usings). `DpiHelper` seeds at
scale 1.0 and the pure overload multiplies through it, so the anchor facts pin 2.0 inside
the `DpiHelper Tests (App)` collection exactly as `ScreenGeometryTests` does:

```csharp
namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (App)")]
public class KeyboardEdgeButtonsTests
{
    public KeyboardEdgeButtonsTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [Fact]
    public void Anchor_TracksTheKeyboardEdge_InsteadOfScreenCenter()
    {
        // Ledger #15 mechanism B: keyboard Top → anchor at 25% height; Bottom → 75%.
        // Pinned at 2.0 scale (the ScreenGeometryTests pattern), 1920x1080 logical.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var top = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: true, logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920, logicalScreenHeight: 1080);
            var bottom = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: false, logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920, logicalScreenHeight: 1080);

            // Y = (1080 * 2) * fraction; X = 3840 - (30 + 50 + 10) * 2 = 3660.
            Check.That(top.Y).IsCloseTo(540.0, 1e-9);
            Check.That(bottom.Y).IsCloseTo(1620.0, 1e-9);
            Check.That(top.X).IsCloseTo(3660.0, 1e-9);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void Definitions_FlipTakesTheKeyboardFacingSlot_InBothDirections()
    {
        foreach (var (keyboardAtTop, flipSign) in new[] { (true, -1.0), (false, 1.0) })
        {
            var defs = KeyboardEdgeButtons.Definitions(600, keyboardAtTop);
            var flip = defs.Single(d => d.Id == KeyboardEdgeButtons.FlipId);
            var close = defs.Single(d => d.Id == KeyboardEdgeButtons.CloseId);

            Check.That(Math.Sign(flip.VerticalOffset)).IsEqualTo(flipSign);
            Check.That(Math.Sign(close.VerticalOffset)).IsEqualTo(-flipSign);
            Check.That(flip.DwellMs).IsEqualTo(600);
            Check.That(close.DwellMs).IsEqualTo(600);
            Check.That(flip.Icon).IsEqualTo(ActionIcon.KeyboardEdge);
            Check.That(close.Icon).IsEqualTo(ActionIcon.Close);
            Check.That(flip.Label).IsEqualTo("Flip");
            Check.That(close.Label).IsEqualTo("Close");
        }
    }

    [Fact]
    public void CloseActivation_SendsHideMessage()
    {
        var messenger = new WeakReferenceMessenger();
        KeyboardHideRequestedMessage? received = null;
        messenger.Register<object?, KeyboardHideRequestedMessage>(this, (_, m) => received = m);

        var close = KeyboardEdgeButtons.Definitions(600, keyboardAtTop: false)
            .Single(d => d.Id == KeyboardEdgeButtons.CloseId);
        close.Activate(messenger);

        Check.That(received).IsNotNull();
    }

    [Fact]
    public void FlipActivation_SendsEdgeToggledMessage()
    {
        var messenger = new WeakReferenceMessenger();
        KeyboardEdgeToggledMessage? received = null;
        messenger.Register<object?, KeyboardEdgeToggledMessage>(this, (_, m) => received = m);

        var flip = KeyboardEdgeButtons.Definitions(600, keyboardAtTop: true)
            .Single(d => d.Id == KeyboardEdgeButtons.FlipId);
        flip.Activate(messenger);

        Check.That(received).IsNotNull();
    }
}
```

(The `DpiHelper Tests (App)` collection serializes these against any test that writes the
scale; the ctor reset plus try/finally restore keep the ambient state at 1.0 for the
non-anchor facts.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardEdgeButtonsTests"`
Expected: compile error — no `KeyboardEdgeBarAnchor` / `KeyboardEdgeButtons`.

- [ ] **Step 3: Implement**

`src/EyeCtrl.App/Services/ScreenGeometry.cs` — append inside the class:

```csharp
    /// <summary>
    /// Anchor for the keyboard's edge-bar pair, tracking the keyboard's edge (spec ledger #15,
    /// mechanism B): keyboard Top → 25% of screen height, Bottom → 75%. Same right-edge inset
    /// arithmetic as <see cref="RightEdgeAnchor"/>, only the vertical position differs.
    /// </summary>
    public static PhysicalPoint KeyboardEdgeBarAnchor(
        bool keyboardAtTop,
        double logicalRadius,
        double logicalWindowMargin = EdgeButtonWindowMarginLogical)
        => KeyboardEdgeBarAnchor(
            keyboardAtTop, logicalRadius, logicalWindowMargin,
            SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

    /// <summary>Pure-arithmetic overload, exposed for the same reason as RightEdgeAnchor's.</summary>
    internal static PhysicalPoint KeyboardEdgeBarAnchor(
        bool keyboardAtTop,
        double logicalRadius,
        double logicalWindowMargin,
        double logicalScreenWidth,
        double logicalScreenHeight)
    {
        var fraction = keyboardAtTop ? 0.25 : 0.75;
        var w = DpiHelper.LogicalToPhysicalX(logicalScreenWidth);
        var h = DpiHelper.LogicalToPhysicalY(logicalScreenHeight);
        var rightOffsetLogical = KeyboardEdgeButtons.RightEdgeInsetLogical + logicalRadius + logicalWindowMargin;
        return new PhysicalPoint(w - DpiHelper.LogicalToPhysicalX(rightOffsetLogical), h * fraction);
    }
```

Create `src/EyeCtrl.App/Services/KeyboardEdgeButtons.cs` (mirror `ActionMenuEdgeButtons` verbatim in structure):

```csharp
namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The right-edge pair shown while the keyboard is up: Close, which dismisses it and restores
/// fixation, and Flip, which moves it to the opposite screen edge (spec B4, ledgers #6/#12/#15).
///
/// The bar TRACKS the keyboard (ledger #15, mechanism B): Flip always takes the keyboard-facing
/// slot — top when the keyboard is at Top, bottom when at Bottom — and the anchor sits toward
/// the keyboard's edge (25%/75% height), not at screen center. On a flip the vacated positions
/// carry no dwell target, so a gaze resting where Flip just was hits nothing.
///
/// Both keep their labels at all times (the ActionMenuEdgeButtons convention: the escape hatch
/// cannot rely on being recognised).
/// </summary>
internal static class KeyboardEdgeButtons
{
    internal const string CloseId = "Keyboard_close";
    internal const string FlipId = "Keyboard_flip";

    /// <summary>Right-edge inset, same value the other edge-bar callers reserve (their private
    /// RightEdgeInsetLogical = 30). Ours so ScreenGeometry's keyboard variant can use it.</summary>
    internal const double RightEdgeInsetLogical = 30;

    /// <param name="dwellMs">Keyboard key dwell — the bar is part of the keyboard session.</param>
    /// <param name="keyboardAtTop">The keyboard's current edge; selects slot order and anchor.</param>
    internal static IReadOnlyList<EdgeButtonDefinition> Definitions(int dwellMs, bool keyboardAtTop)
    {
        var offset = DpiHelper.LogicalToPhysicalY(
            UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap / 2);

        var flipOffset = keyboardAtTop ? -offset : offset;
        var closeOffset = keyboardAtTop ? offset : -offset;

        return new[]
        {
            new EdgeButtonDefinition(
                FlipId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ViewModels.Messages.KeyboardEdgeToggledMessage()),
                VerticalOffset: flipOffset,
                RadiusLogical: UIConstants.ButtonRadius,
                Icon: ActionIcon.KeyboardEdge,
                Label: "Flip"),
            new EdgeButtonDefinition(
                CloseId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ViewModels.Messages.KeyboardHideRequestedMessage()),
                VerticalOffset: closeOffset,
                RadiusLogical: UIConstants.ButtonRadius,
                Icon: ActionIcon.Close,
                Label: "Close"),
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardEdgeButtonsTests"`
Expected: PASS (4 facts). If the anchor expectations fight the host DPI, fix the expectations per the note in Step 1 — the fractions are the contract.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/ScreenGeometry.cs src/EyeCtrl.App/Services/KeyboardEdgeButtons.cs tests/EyeCtrl.App.Tests/Services/KeyboardEdgeButtonsTests.cs
git commit -m "feat(edge-bar): keyboard-tracking anchor + Close/Flip definitions on the shared bar"
```

---

### Task 10: Edge-bar lifecycle + flip handler completion

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs`

**Interfaces:**
- Consumes: `KeyboardEdgeButtons` (Task 9), `ScreenGeometry.KeyboardEdgeBarAnchor` (Task 9), `ApplyKeyboardEdge`/`FlipScreenEdge` (Task 5), `UIConstants.ButtonRadius`, `EdgeButtonBarViewModel` (`EyeCtrl.ViewModels`).
- Produces: complete `HandleKeyboardEdgeToggled`; the edge bar shows/hides with the keyboard; `KeyboardCoordinator.ShouldShowKeyboardEdgeBar(EdgeButtonBarViewModel?)` / `ShouldHideKeyboardEdgeBar(EdgeButtonBarViewModel?)` internal static guard seams (tested headlessly in Task 10's guard fact).

- [ ] **Step 1: Implement**

`KeyboardCoordinator.cs` — `UIConstants` lives in `EyeCtrl.ViewModels`, which this file
does not import; add to its using block:

```csharp
using EyeCtrl.ViewModels;   // UIConstants (edge-bar anchor radius)
```

In `OnInitialize`, register the flip message:

```csharp
        // Edge-bar Flip (spec B1/B4): flip, apply to the window, persist, refresh the
        // snapshot, and rebuild the bar at the keyboard's new edge (ledger #15).
        messenger.Register<KeyboardCoordinator, KeyboardEdgeToggledMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(HandleKeyboardEdgeToggled));
```

Add the lifecycle methods (near `SetKeyboardVisible`):

```csharp
    private void HandleKeyboardEdgeToggled()
    {
        if (Context is null) return;
        var (flipped, snapshot) = ApplyKeyboardEdge(Context.Settings, Context.SettingsService);
        Context.OverlayManager.Keyboard.SetScreenEdge(flipped);
        _keyboardSnapshot = snapshot;
        ShowKeyboardEdgeBar();
    }

    private void ShowKeyboardEdgeBar()
    {
        if (Context is null) return;
        var bar = Context.OverlayManager.EdgeButtonBar;

        // Reverse-overlap guard (spec B4 #10, the OverlayMessageCoordinator hand-off dance):
        // if the bar is currently showing ANOTHER context's definitions (scroll Stop, drag
        // Cancel, the menu pair), do not clobber them — the keyboard stays closable via the
        // toolbar Keyboard button until that context releases the bar. Skipping is safe in
        // both directions: showing only when the bar is free-or-ours, hiding only when ours.
        if (!ShouldShowKeyboardEdgeBar(bar.EdgeBarVm))
        {
            Log.Debug("Edge bar busy with another context's buttons; keyboard pair skipped");
            return;
        }

        var atTop = string.Equals(Context.Settings.Keyboard.ScreenEdge, "Top", StringComparison.OrdinalIgnoreCase);
        bar.ShowButtons(
            KeyboardEdgeButtons.Definitions(Context.Settings.Dwell.KeyboardKeyDwellMs, atTop),
            ScreenGeometry.KeyboardEdgeBarAnchor(atTop, UIConstants.ButtonRadius));
    }

    private void HideKeyboardEdgeBar()
    {
        // Guarded hand-off (spec B4 #10): the bar is shared — only clear it while the
        // keyboard's definitions still own it, mirroring OverlayMessageCoordinator's
        // ActionMenu Close guard.
        var bar = Context?.OverlayManager.EdgeButtonBar;
        if (ShouldHideKeyboardEdgeBar(bar?.EdgeBarVm))
            bar!.Hide();
    }

    /// <summary>The bar is claimable for the keyboard pair when it holds no definitions at
    /// all (the common case) or already ours (a flip rebuild).</summary>
    internal static bool ShouldShowKeyboardEdgeBar(EdgeButtonBarViewModel? barVm) =>
        barVm is null
        || barVm.Buttons.Count == 0
        || barVm.DefinitionFor(KeyboardEdgeButtons.CloseId) is not null;

    /// <summary>The keyboard's hide may clear the bar only while the keyboard pair still
    /// owns it — another context's stop/cancel buttons must survive.</summary>
    internal static bool ShouldHideKeyboardEdgeBar(EdgeButtonBarViewModel? barVm) =>
        barVm?.DefinitionFor(KeyboardEdgeButtons.CloseId) is not null;
```

(`EdgeButtonBarViewModel` is `EyeCtrl.ViewModels` — covered by the using added above.
`Log` is Serilog's static, already used in this file.)

In `SetKeyboardVisible`'s `if (visible)` branch, after `ApplyInitialToolbarOccupancy();` (from Task 8):

```csharp
            ShowKeyboardEdgeBar();
```

In the `else` branch, before `Context.Messenger.Send(new HideKeyboardMessage());`:

```csharp
            HideKeyboardEdgeBar();
```

- [ ] **Step 2: Write the failing guard tests**

Append to `tests/EyeCtrl.App.Tests/Services/KeyboardEdgeButtonsTests.cs` (needs
`using EyeCtrl.Core.Settings;` for `EyeCtrlSettings`):

```csharp
    [Fact]
    public void GuardTests_KeyboardPairClaimsFreeBar_ButNeverClobbersOrClearsOthers()
    {
        // The shared-bar hand-off (spec B4 #10 / §6): show only when the bar is free or
        // already ours; hide only while ours. Real EdgeButtonBarViewModel, no WPF windows.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var barVm = new EdgeButtonBarViewModel(settings, messenger);

        // Free bar (no definitions): claimable.
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(barVm)).IsTrue();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(barVm)).IsFalse();

        // Our pair: re-claimable (flip rebuild) and clearable (keyboard hide).
        barVm.ShowButtons(
            KeyboardEdgeButtons.Definitions(600, keyboardAtTop: false),
            new PhysicalPoint(1800, 800));
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(barVm)).IsTrue();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(barVm)).IsTrue();

        // Another context's button (scroll stop shape): never clobbered, never cleared.
        barVm.ShowButtons(
            new[] { new EdgeButtonDefinition(
                "scroll_stop", Symbol: string.Empty, 600,
                _ => { }, VerticalOffset: 0) },
            new PhysicalPoint(1800, 800));
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(barVm)).IsFalse();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(barVm)).IsFalse();

        // Hidden bar (Hide clears everything): claimable again.
        barVm.Hide();
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(barVm)).IsTrue();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(barVm)).IsFalse();

        // Null-tolerant (window not constructed).
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(null)).IsTrue();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(null)).IsFalse();
    }
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardEdgeButtonsTests"`
Expected: guard fact PASS after Step 1's seams exist (write the guard test first if you
prefer strict red-green — it fails to compile against the pre-Task-10 coordinator).

- [ ] **Step 4: Build + run coordinator + edge-button tests**

Run: `dotnet build EyeCtrl.slnx`, then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"` and `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardEdgeButtonsTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs tests/EyeCtrl.App.Tests/Services/KeyboardEdgeButtonsTests.cs
git commit -m "feat(keyboard): edge-bar lifecycle - shows with keyboard, rebuilds on flip, guarded hand-off"
```

---

### Task 11: Un-pin the toolbar + comment/doc repairs

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs` (comment at ~:109)
- Modify: `docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md` (~:140 and ~:166)
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs` (rewrite one fact)
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/QuickActionsCoordinatorTests.cs` (comment at ~:53)

**Interfaces:**
- Consumes: everything above (the edge bar exists — the keyboard has its own escape).
- Produces: the behavior change itself; no new API.

- [ ] **Step 1: Rewrite the failing test first**

Replace `ApplyModalKeyboardState_FansOutToBothControllers_AndTheSharedFlag` (KeyboardCoordinatorTests.cs:70-102) with:

```csharp
    [Fact]
    public void ApplyModalKeyboardState_FansOutToQuickActionsSummon_AndPublishesTheSharedFlag()
    {
        // Spec B2 (ledger #9): showing the keyboard suspends ONLY the quick-actions hot-edge
        // summon and publishes the shared ModalInteractionState flag the gaze-thread early-out
        // reads. The toolbar auto-hide suspension is deliberately gone — the toolbar un-pins
        // while the keyboard is up, and the keyboard's own escape is the edge-bar Close.
        var modal = new ModalInteractionState();

        var summon = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var quickActions = new QuickActionsCoordinator();
        quickActions.InjectSummonForTesting(summon);

        var autoHide = new ToolbarAutoHideController(
            enabled: true, edge: "bottom", screenW: 1920, screenH: 1080,
            toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
            hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        var toolbar = new ToolbarLifecycleCoordinator(modal);
        toolbar.InjectAutoHideForTesting(autoHide);

        var keyboard = new KeyboardCoordinator(modal, quickActions, toolbar);

        keyboard.ApplyModalKeyboardState(true);
        Check.That(modal.KeyboardVisible).IsTrue();
        Check.That(quickActions.IsSummonSuspended).IsTrue();
        Check.That(autoHide.IsSuspended).IsFalse();

        keyboard.ApplyModalKeyboardState(false);
        Check.That(modal.KeyboardVisible).IsFalse();
        Check.That(quickActions.IsSummonSuspended).IsFalse();
        Check.That(autoHide.IsSuspended).IsFalse();
    }
```

(`ToolbarAutoHideController` already exposes `internal bool IsSuspended => _suspended;`
at ToolbarAutoHideController.cs:88 — use it; no new seam needed.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ApplyModalKeyboardState"`
Expected: FAIL on the first `autoHide.IsSuspended` assertion — with production unchanged,
`ApplyModalKeyboardState(true)` still suspends the controller.

- [ ] **Step 3: Implement**

`KeyboardCoordinator.ApplyModalKeyboardState` — delete the line

```csharp
        _toolbar.SetAutoHideSuspended(visible);
```

Rewrite its doc comment (KeyboardCoordinator.cs ~:177-187) to:

```csharp
    /// <summary>
    /// Publishes a keyboard-visibility change to the quick-actions summon-suspend seam and the
    /// shared <see cref="ModalInteractionState.KeyboardVisible"/> flag that the gaze-routing
    /// early-out in <see cref="ToolbarLifecycleCoordinator"/> reads on the gaze thread.
    /// The toolbar auto-hide is deliberately NOT suspended anymore (spec B2, ledger #9): the
    /// toolbar un-pins while the keyboard is up and edge-summons on demand; the keyboard's own
    /// escape is the edge-bar Close. Suspending before publishing the flag still matters — no
    /// gaze sample may observe <c>KeyboardVisible</c> while the quick-actions summon is live.
    /// </summary>
```

Fix the ctor rationale comment (~:41-42) — "All three are dereferenced unconditionally by ApplyModalKeyboardState" is now false; replace with:

```csharp
        // _modal is read by ApplyModalKeyboardState; _quickActions by the same; _toolbar by the
        // yield subscription in OnInitialize (ToolbarVisibilityChanged). All three are live
        // dependencies, so reject nulls at the ctor rather than at first keyboard toggle.
```

`ToolbarAutoHideController.cs` ~:109 — the comment "Recover from hidden so the user isn't stranded while a modal is up" now covers only the settings modal; reword to "…while the Settings window is up (the keyboard no longer suspends auto-hide — its escape is the edge-bar Close; see the 2026-09-10 keyboard spec)".

`docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md` — under the title, add:

```markdown
> **Superseded in part (2026-09-10):** keyboard-open no longer suspends auto-hide or
> force-shows the toolbar — see `2026-09-10-keyboard-action-and-edge-toggle-design.md` B2
> (ledger #9). The Settings-window behavior described below is unchanged.
```

and soften the two body sentences it invalidates (~:140, ~:166) by prefixing each with "(Settings window only since 2026-09-10:)".

`QuickActionsCoordinatorTests.cs` ~:53 — the comment cites
`KeyboardCoordinatorTests.ApplyModalKeyboardState_FansOutTo...` (a truncated prefix that
still matches the new name — updating it is OPTIONAL polish, not a correctness edit).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ApplyModalKeyboardState"` then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~QuickActionsCoordinatorTests"`
Expected: PASS both.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs src/EyeCtrl.App/Services/ToolbarAutoHideController.cs docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs tests/EyeCtrl.App.Tests/Services/Coordinators/QuickActionsCoordinatorTests.cs
git commit -m "feat(toolbar): un-pin while keyboard is up - edge-bar Close is the escape; repair orphaned comments/docs"
```

---

### Task 12: Full verification + owner UI pass

**Files:** none new.

- [ ] **Step 1: Build the solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: PASS, zero warnings introduced by our files.

- [ ] **Step 2: Run every non-UI suite, one project per invocation**

```bash
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.Platform.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Benchmarks.Tests
```

Expected: all PASS. Known box quirks: the German locale can break `EyeCareHooks` UI tests (decimal comma) — those are `Category=UI` and excluded; `SettingsRenderMapping` flakes under full-solution runs are pre-existing and not ours (re-run once before investigating).

- [ ] **Step 3: Owner UI pass (owner-run; do NOT run these yourself)**

Ask the owner to run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"` and to exercise, on a real tracker (the Mouse provider cannot summon — spec C3):
1. Ring → More → **Keyboard** shows the keyboard (idempotent when already visible).
2. The edge bar appears with the keyboard; **Flip** moves it Top⇄Bottom and the bar re-anchors (Flip stays keyboard-facing).
3. **Close** dismisses the keyboard and the ring opens again immediately.
4. With the keyboard up, the toolbar auto-hides; gaze to its edge summons it; summoned onto the keyboard's edge, the keyboard slides aside and returns.
5. Glyph render check: ring Keyboard icon and edge-bar Flip icon at real size (spec R1).

- [ ] **Step 4: Final commit (if the owner pass produced fixes, they carry their own commits)**

```bash
git status
git log --oneline -12   # one commit per task, clean history
```

---

## Self-Review (performed after writing — findings already folded in)

- **Spec coverage:** A: §3.2 items 1-8 → Tasks 1,1,1,2,4,4,4,3 ✓. B1 → Tasks 4/5/10 ✓. B2 → Task 11 (+ ToolbarIconKey/ActionIcons docs in Task 1) ✓. B3 #5-#8 → Tasks 6,7,8 ✓. B4 #9-#12 → Tasks 9,10 ✓. §6 breakage inventory → Task 11 rewrites the fan-out test + QuickActions comment; `ToolbarViewModelTests`, band tests, `AppLaunchTests` untouched by design ✓. Ledger #15 → Task 9 slot math + Task 10 rebuild ✓.
- **Ordering invariant:** the un-pin (Task 11) lands only after the edge bar (Tasks 9-10) gives the keyboard its escape; `_toolbar` is re-dereferenced (Task 8) before its old dereference is removed (Task 11).
- **Type consistency:** `KeyboardEdgeButtons.Definitions(int, bool)` used in Task 10 matches Task 9's definition; `ApplyKeyboardEdge` tuple consumed in Task 10; `SetToolbarOccupancy(bool, string, double)` consistent across Tasks 7/8.

---

## Post-execution addendum — reverse-overlap trace (external implementation review)

Spec B4 #10 required the reverse overlap (keyboard opened mid-scroll/mid-drag while the
stop/cancel button owns the bar) to be traced in the plan; this plan omitted it, and an
external implementation review caught the consequence: the keyboard's Close/Flip could be
permanently lost for the session. The full trace now lives in the spec (B4 #10,
"Reverse-overlap trace"); the resolution is a retry claim — `KeyboardCoordinator
._keyboardPairWanted` plus `ShouldRetryKeyboardEdgeBar` re-claiming on
`EdgeButtonBarViewModel.ButtonSetChanged` — committed alongside this addendum. The
reachable path is the toolbar's Keyboard toggle during scroll/drag mode; both owning
contexts end with an unguarded `EdgeButtonBar.Hide()`.
