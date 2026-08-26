# Action Menu Progressive Disclosure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show four primary actions plus a More target instead of all eight at once, and put the action names behind one dwell on a Help target parked on the right-edge bar.

**Architecture:** Both halves are ViewModel state, not new plumbing. `ActionMenuViewModel` gains a layer (primary / secondary) that it rebuilds its own items from — the gaze engine still sends one `ShowActionMenuMessage` and never learns there are two layers. Labels are a `ShowLabels` flag toggled by a message the edge bar's Help button sends. `ActionMenuSettings` gains a `PrimaryActions` list that partitions the existing `VisibleActions`, migrated once so an existing settings file keeps working.

**Tech Stack:** C# / .NET 10 / WPF, CommunityToolkit.Mvvm (`ObservableObject`, `IMessenger`), xUnit v3 with NFluent assertions (`Check.That(...)`, never `Assert.*`).

---

**Prerequisites, both merged:**
1. [2026-08-26-action-menu-geometry.md](2026-08-26-action-menu-geometry.md) — 140 DIP targets, 60 DIP gap
2. [2026-08-26-action-menu-icons.md](2026-08-26-action-menu-icons.md) — the `ActionIcon` enum and `ActionIcons` registry this plan adds two entries to

## Risk warning (CLAUDE.md requires this be surfaced before editing)

`impact({target: "ActionMenuViewModel", direction: "upstream"})` returns **HIGH**: 20 direct dependents across 3 modules. This plan changes its item-building logic substantially — more than plans 1 and 2 together.

`impact({target: "DwellArcButton", direction: "upstream"})` returns **HIGH**: 15 impacted symbols, 2 execution flows. Task 5 adds a label to it, and as in plan 2 the change is additive: buttons built without a label render exactly as before.

Additional risk this plan carries that the others did not: **`EdgeButtonBarViewModel` is shared with the drag and scroll-mode contexts.** `DragCoordinator` and `ScrollModeController` both call `ShowButtons`, which *clears* the bar. Task 6 must add the Help button without stealing the bar from a drag in progress; its first test pins that.

## The two decisions worth disagreeing with before you build

**1. On the second layer, the nav slot is layer-back — so the zoom Back is unreachable from there.** Today `ShowBack: true` (arriving from zoom or manual selection) puts a Back item in the ring that sends `BackSelectedMessage`. With two layers there is one nav slot and two things it could mean. This plan gives layer 2's slot to "return to layer 1", so leaving zoom from layer 2 takes two dwells: back to layer 1, then Back. The alternative — six slots on layer 2 — costs every layer-2 target its spacing.

**2. Help toggles labels for the rest of the menu session, and does not survive it.** `HideActionMenuMessage` resets it. A persisted preference would be a settings-level choice and is not what the canvas asked for; a momentary reveal (names only while dwelling Help) was rejected because you cannot read a label while dwelling something else.

Both are recorded in the artboard annotations as open questions. If the user has since decided differently, change them here before writing code rather than building and reworking.

**A third question is settled rather than open.** Whether the menu may take the right-edge bar on the `ShowBack` path was traced through `InteractionManager` on 2026-08-27: manual selection tears its buttons down first, so it may. Task 6 Step 8 carries the trace.

## File structure

**Create:**
- `tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsPrimaryTests.cs`
- `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLayerTests.cs`
- `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLabelTests.cs`
- `tests/EyeCtrl.App.Tests/Services/ActionMenuEdgeButtonsTests.cs`

**Modify:**
- `src/EyeCtrl.Core/Models/ActionIcon.cs` — add `More`, `Help`
- `src/EyeCtrl.Controls/Icons/ActionIcons.cs` — geometry for both
- `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs` — `PrimaryActions` + its one-time migration
- `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs` — `Label`, `Kind`
- `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` — layers, labels
- `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` — `ToggleActionMenuLabelsMessage`
- `src/EyeCtrl.ViewModels/EdgeButtonDefinition.cs` — optional `Icon`
- `src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs` — render an icon when named
- `src/EyeCtrl.Controls/DwellArcButton.cs` — optional label under the icon
- `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` — pass label and icon size
- `src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs` — show/hide Help with the menu
- `src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs` — choose the primary four

## Sizing

| State | Icon | Label | Ratio |
|---|---|---|---|
| Resting (labels off) | 63 DIP | — | `ContainerRatio.Dominant` (0.45 × 140) |
| Labels on | 44 DIP | 18 DIP `Tokens.FontSizeSubtitle`, SemiBold | `ContainerRatio.Medium` (0.32 × 140 = 44.8) |
| Help target on the edge bar | 32 DIP | 14 DIP `Tokens.FontSizeBodyLarge` | `ContainerRatio.Medium` (0.32 × 100), because it carries a label too |

Label colour is `Tokens.BrushTextPrimary` on `Tokens.BrushSurfaceMedium` — 10.3:1, computed from the Midnight hex values.

Ring spacing at five targets is `2R·sin(36°)`; at the minimum permitted `CircleSize` of 10 that is 159 DIP against a 140 DIP button, so **five targets never overlap at any `CircleSize`**. This is the plan that closes the overlap hole plan 1 documented.

## Verification commands

**`dotnet test` output on this machine is German**: failures read `Fehler <FullyQualifiedName>`, not `Failed`. Grepping for `Failed` reports green over red — read the summary (`Fehler: N`).

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Roughly 13 `AppLaunchTests` fail headless with "Toolbar_Window did not appear within 15s" — environmental, identical on clean `main`.

---

### Task 1: Partition the visible actions into a primary four

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsPrimaryTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsPrimaryTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The primary/secondary split, and the migration that has to leave an existing settings file
/// working.
///
/// The migration is the part with teeth. <c>VisibleActions</c> is user-owned — the Settings
/// Action Menu tab edits it, and <c>EnsureDefaultsOnce</c> exists precisely because a
/// per-startup default re-add made that tab lie across restarts. The primary list inherits that
/// hazard exactly: a migration that ran every startup would re-promote an action the user
/// demoted, every time they relaunched.
/// </summary>
public class ActionMenuSettingsPrimaryTests
{
    [Fact]
    public void AFreshSettings_HasFourPrimaryActions()
    {
        var settings = new ActionMenuSettings();

        Check.That(settings.PrimaryActions).ContainsExactly(
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick,
            InteractionAction.ScrollMode);
    }

    [Fact]
    public void EveryPrimaryAction_IsAlsoVisible()
    {
        var settings = new ActionMenuSettings();

        Check.That(settings.VisibleActions).Contains(settings.PrimaryActions);
    }

    [Fact]
    public void SecondaryActions_AreTheVisibleOnesThatAreNotPrimary()
    {
        var settings = new ActionMenuSettings();

        Check.That(settings.SecondaryActions).ContainsExactly(
            InteractionAction.Drag,
            InteractionAction.ScrollUp,
            InteractionAction.ScrollDown,
            InteractionAction.PinNote);
    }

    [Fact]
    public void SecondaryActions_NeverIncludeAHiddenAction()
    {
        var settings = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
            PrimaryActions = new List<InteractionAction> { InteractionAction.LeftClick },
        };

        // ScrollLeft/ScrollRight ship OFF. Turning an action off in the Settings tab must remove
        // it from the ring entirely, not push it onto the second layer.
        Check.That(settings.SecondaryActions).IsEmpty();
    }

    [Fact]
    public void APrimaryActionThatIsNotVisible_IsDroppedFromTheRing()
    {
        var settings = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
            PrimaryActions = new List<InteractionAction>
            {
                InteractionAction.LeftClick,
                InteractionAction.PinNote,   // primary, but switched off
            },
        };

        Check.That(settings.EffectivePrimaryActions).ContainsExactly(InteractionAction.LeftClick);
    }

    [Fact]
    public void MigratePrimaryOnce_PopulatesAnOldFile()
    {
        // An existing settings file has VisibleActions but no PrimaryActions.
        var settings = new ActionMenuSettings
        {
            PrimaryActions = new List<InteractionAction>(),
            PrimaryMigrated = false,
        };

        settings.MigratePrimaryOnce();

        Check.That(settings.PrimaryActions).ContainsExactly(
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick,
            InteractionAction.ScrollMode);
        Check.That(settings.PrimaryMigrated).IsTrue();
    }

    [Fact]
    public void MigratePrimaryOnce_LeavesADeliberatelyEmptiedListAlone()
    {
        // This is the whole reason the flag exists: a user who demoted every action to the second
        // layer must not have four re-promoted on the next launch.
        var settings = new ActionMenuSettings
        {
            PrimaryActions = new List<InteractionAction>(),
            PrimaryMigrated = true,
        };

        settings.MigratePrimaryOnce();

        Check.That(settings.PrimaryActions).IsEmpty();
    }

    [Fact]
    public void Validate_RejectsMoreThanFourPrimaryActions()
    {
        var settings = new ActionMenuSettings
        {
            PrimaryActions = new List<InteractionAction>
            {
                InteractionAction.LeftClick, InteractionAction.RightClick,
                InteractionAction.DoubleClick, InteractionAction.ScrollMode,
                InteractionAction.Drag,
            },
        };

        // Four plus More fills the five slots. A fifth primary makes six, which drops the
        // neighbour gap from 160 DIP to 115 at the default CircleSize — still clear of each
        // other, but past the point where the ring can be aimed at rather than read.
        Check.That(settings.Validate()).Not.IsEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ActionMenuSettingsPrimaryTests"
```

Expected: FAIL to **compile**, `CS0117: 'ActionMenuSettings' does not contain a definition for 'PrimaryActions'`.

- [ ] **Step 3: Add the primary list, the derived views and the migration**

In `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`, add after the existing `DefaultsMigrated` property:

```csharp
    /// <summary>
    /// The actions shown on the ring's FIRST layer, alongside a More target that opens the rest.
    /// A subset of <see cref="VisibleActions"/>; anything visible but not listed here lands on the
    /// second layer.
    ///
    /// Capped at four by <see cref="Validate"/>. The cap buys SEPARATION, not the absence of
    /// overlap — at the default CircleSize the neighbour gap is 160 DIP at five slots, 115 at
    /// six and 34 at nine, and six only actually overlap below CircleSize 20. Four plus More is
    /// the point at which a ring can be aimed at without scanning it.
    /// </summary>
    [ObservableProperty] private List<InteractionAction> _primaryActions = new()
    {
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.DoubleClick, InteractionAction.ScrollMode
    };

    /// <summary>
    /// Whether <see cref="MigratePrimaryOnce"/> has already run for this settings file. Absent
    /// from every pre-2026-08-26 file (so it loads false) and set on the first migration.
    ///
    /// Exactly the <see cref="DefaultsMigrated"/> pattern, for exactly its reason: the primary
    /// list is user-owned once the Settings tab can edit it, and a per-startup migration would
    /// re-promote an action the user deliberately demoted every time they relaunched.
    /// </summary>
    [ObservableProperty] private bool _primaryMigrated;

    /// <summary>The maximum number of primary actions. Four, plus the More target, fills the ring.</summary>
    public const int MaxPrimaryActions = 4;

    /// <summary>
    /// <see cref="PrimaryActions"/> restricted to what is actually switched on. A primary action
    /// the user turned off in the Settings tab must vanish from the ring, not linger on layer one.
    /// </summary>
    public IReadOnlyList<InteractionAction> EffectivePrimaryActions =>
        (PrimaryActions ?? new List<InteractionAction>())
            .Where(a => VisibleActions?.Contains(a) == true)
            .ToList();

    /// <summary>
    /// Everything visible that is not primary — the ring's second layer, reached through More.
    /// Derived rather than stored so it cannot drift out of sync with the two lists it spans.
    /// </summary>
    public IReadOnlyList<InteractionAction> SecondaryActions =>
        (VisibleActions ?? new List<InteractionAction>())
            .Where(a => PrimaryActions?.Contains(a) != true)
            .ToList();

    /// <summary>
    /// Populates <see cref="PrimaryActions"/> exactly once per settings file, then never touches
    /// it again. The startup caller must use this rather than assigning the defaults directly —
    /// see <see cref="PrimaryMigrated"/>.
    /// </summary>
    public void MigratePrimaryOnce()
    {
        if (PrimaryMigrated) return;

        PrimaryActions = new List<InteractionAction>
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.DoubleClick, InteractionAction.ScrollMode
        };
        PrimaryMigrated = true;
    }
```

Add `using System.Linq;` to the file's using block.

Replace the empty `Validate()` body:

```csharp
    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        var primary = PrimaryActions ?? new List<InteractionAction>();

        if (primary.Count > MaxPrimaryActions)
            errors.Add($"{nameof(PrimaryActions)} must hold at most {MaxPrimaryActions} actions; "
                       + $"found {primary.Count}. Four plus the More target fills the ring's five slots.");

        if (primary.Count != primary.Distinct().Count())
            errors.Add($"{nameof(PrimaryActions)} must not repeat an action.");

        return errors;
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ActionMenuSettingsPrimaryTests"
```

Expected: PASS, 8 tests.

- [ ] **Step 5: Call the migration where EnsureDefaultsOnce is already called**

```bash
grep -rn "EnsureDefaultsOnce" src --include=*.cs | grep -v "/obj/"
```

At each call site, add the new migration immediately after:

```csharp
        settings.ActionMenu.MigratePrimaryOnce();
```

Match the receiver name to whatever the surrounding line uses.

- [ ] **Step 6: Run the full gate**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS. `EyeCtrlSettingsEnumerationTests` guards that every *sub-settings object* is enumerated — this task adds properties to an existing one, so it is unaffected. If it fails, something else moved.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ActionMenuSettings.cs tests/EyeCtrl.Core.Tests/Settings/ActionMenuSettingsPrimaryTests.cs
git commit -m "feat(settings): split the action menu into a primary four and a second layer

Migrated once per settings file, on the EnsureDefaultsOnce pattern and for its
reason: the list is user-owned, and a per-startup migration would re-promote a
demoted action on every launch.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Draw the More and Help icons

**Files:**
- Modify: `src/EyeCtrl.Core/Models/ActionIcon.cs`
- Modify: `src/EyeCtrl.Controls/Icons/ActionIcons.cs`

Plan 2's `ActionIconsTests.EveryActionIcon_HasGeometry` iterates `Enum.GetValues<ActionIcon>()`, so adding an enum member without geometry fails it. That is the test; no new one is needed.

- [ ] **Step 1: Run the existing test to verify it fails after the enum grows**

Add to `src/EyeCtrl.Core/Models/ActionIcon.cs`, after `Back`:

```csharp
    /// <summary>Three dots — opens the ring's second layer. Navigation, not an action.</summary>
    More,
    /// <summary>A question mark — toggles the action names on. Navigation, not an action.</summary>
    Help,
```

Run:

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ActionIconsTests"
```

Expected: FAIL with `KeyNotFoundException` from `ActionIcons.For`, reported against `EveryActionIcon_HasGeometry`.

- [ ] **Step 2: Add both geometries**

In `src/EyeCtrl.Controls/Icons/ActionIcons.cs`, add to the `Build()` dictionary before its closing brace:

```csharp
        // Fill-only: three dots, the one icon in the set that is not a mouse. More is navigation,
        // and drawing it as a mouse would say it performs a click.
        [ActionIcon.More] = Make(null,
            "M3.7,12 A1.9,1.9 0 1 1 7.5,12 A1.9,1.9 0 1 1 3.7,12 Z"
            + " M10.1,12 A1.9,1.9 0 1 1 13.9,12 A1.9,1.9 0 1 1 10.1,12 Z"
            + " M16.5,12 A1.9,1.9 0 1 1 20.3,12 A1.9,1.9 0 1 1 16.5,12 Z"),

        // The hook and stem are stroked; the dot is the only filled part.
        [ActionIcon.Help] = Make(
            "M8.6,8.6 A3.5,3.5 0 1 1 13.3,11.9 C12.2,12.3 11.6,13.1 11.6,14.2 L11.6,15.2",
            "M10.3,18.6 A1.3,1.3 0 1 1 12.9,18.6 A1.3,1.3 0 1 1 10.3,18.6 Z"),
```

- [ ] **Step 3: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ActionIconsTests"
```

Expected: PASS, 5 tests. `EveryIcon_FitsTheGrid` now covers both new icons.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Models/ActionIcon.cs src/EyeCtrl.Controls/Icons/ActionIcons.cs
git commit -m "feat(controls): add the More and Help icons

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Give menu items a kind and a name

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs`

`IsBackButton` is a bool, and the ring now has three non-action affordances (the zoom Back, the layer Back, and More) that a bool cannot tell apart. This task replaces it with an enum before Task 4 needs the distinction.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLayerTests.cs` with only the first test for now; Task 4 fills in the rest:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The ring's two layers and the three non-action affordances that navigate between them.
///
/// <c>IsBackButton</c> was a bool until 2026-08-26, which was enough while Back was the only
/// non-action item. It is not enough now: the zoom Back leaves the menu, the layer Back returns
/// to layer one, and More opens layer two. All three are "not an action", and a bool would let
/// two of them be mistaken for each other at the one place that decides what a dwell DOES.
/// </summary>
public class ActionMenuViewModelLayerTests
{
    private static EyeCtrlSettings Defaults() => new();

    private static void Show(IMessenger messenger, bool showBack = false) =>
        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(900, 500),
            Defaults().ActionMenu.VisibleActions,
            ShowBack: showBack,
            DwellTimeMs: 600,
            new PhysicalPoint(900, 500),
            CircleSize: 250));

    [Fact]
    public void AnActionItem_IsKindAction()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);

        Show(messenger);

        var leftClick = vm.Items.Single(i => i.Action == InteractionAction.LeftClick);
        Check.That(leftClick.Kind).IsEqualTo(ActionMenuItemKind.Action);
    }

    [Fact]
    public void TheZoomBackItem_IsKindExitMenu()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);

        Show(messenger, showBack: true);

        Check.That(vm.Items.Any(i => i.Kind == ActionMenuItemKind.ExitMenu)).IsTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelLayerTests"
```

Expected: FAIL to **compile**, `CS0246: The type or namespace name 'ActionMenuItemKind' could not be found`.

- [ ] **Step 3: Replace the bool with a kind, and add the label**

Replace `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs` in full:

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>
/// What a dwell on a ring item does. Replaced a bool <c>IsBackButton</c> on 2026-08-26: the ring
/// gained a second layer, and with it three distinct non-action affordances that a bool cannot
/// separate.
/// </summary>
public enum ActionMenuItemKind
{
    /// <summary>Performs its <see cref="ActionMenuItemViewModel.Action"/> and closes the menu.</summary>
    Action,
    /// <summary>Opens the ring's second layer. Does not close the menu.</summary>
    OpenMore,
    /// <summary>Returns to the ring's first layer. Does not close the menu.</summary>
    BackToPrimary,
    /// <summary>Leaves the menu entirely — the Back that returns from zoom or manual selection.</summary>
    ExitMenu,
}

public partial class ActionMenuItemViewModel : ObservableObject
{
    public string Id { get; }
    public InteractionAction? Action { get; }
    public ActionIcon Icon { get; }

    /// <summary>
    /// The item's name, shown only while the Help target has labels switched on. Always populated
    /// — a label that only exists when shown is a label that is never tested.
    /// </summary>
    public string Label { get; }

    public ActionMenuItemKind Kind { get; }
    public PhysicalPoint Position { get; }

    [ObservableProperty] private double _dwellProgress;

    public ActionMenuItemViewModel(
        string id,
        InteractionAction? action,
        ActionIcon icon,
        string label,
        PhysicalPoint position,
        ActionMenuItemKind kind = ActionMenuItemKind.Action)
    {
        Id = id;
        Action = action;
        Icon = icon;
        Label = label;
        Position = position;
        Kind = kind;
    }
}
```

- [ ] **Step 4: Update the two places that construct or read the old shape**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, `Receive(ShowActionMenuMessage)` builds items and `OnTargetActivated` reads `IsBackButton`. Task 4 rewrites both wholesale — for now, make the file compile by replacing the construction:

```csharp
            var itemVm = new ActionMenuItemViewModel(
                id, action, icon, FriendlyName(action), position,
                isBack ? ActionMenuItemKind.ExitMenu : ActionMenuItemKind.Action);
```

and `OnTargetActivated`'s branch:

```csharp
        if (item.Kind == ActionMenuItemKind.ExitMenu)
            Messenger.Send(new BackSelectedMessage());
        else if (item.Action.HasValue)
            Messenger.Send(new ActionSelectedMessage(item.Action.Value));
```

Add the name table to the same class:

```csharp
    /// <summary>
    /// The user-facing name for an action. Duplicated from <c>ActionMenuPageSource.FriendlyName</c>
    /// rather than shared: that one lives in EyeCtrl.App and names a settings row, this one names
    /// a ring target, and they are free to diverge — a settings row has room for "Scroll Mode"
    /// where the ring wants "Scroll".
    /// </summary>
    private static string FriendlyName(InteractionAction? action) => action switch
    {
        InteractionAction.LeftClick => "Left click",
        InteractionAction.RightClick => "Right click",
        InteractionAction.DoubleClick => "Double click",
        InteractionAction.Drag => "Drag",
        InteractionAction.ScrollUp => "Scroll up",
        InteractionAction.ScrollDown => "Scroll down",
        InteractionAction.ScrollLeft => "Scroll left",
        InteractionAction.ScrollRight => "Scroll right",
        InteractionAction.ScrollMode => "Scroll",
        InteractionAction.PinNote => "Pin note",
        null => "Back",
        _ => action.Value.ToString(),
    };
```

Also update `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs`'s `TheBackItem_TakesTheBackIcon`, which reads `IsBackButton`:

```csharp
        var back = vm.Items.Single(i => i.Kind == ActionMenuItemKind.ExitMenu);
```

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModel"
```

Expected: PASS.

- [ ] **Step 6: Run the full gate and commit**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

```bash
git add src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs src/EyeCtrl.ViewModels/ActionMenuViewModel.cs tests/EyeCtrl.ViewModels.Tests/
git commit -m "refactor(actionmenu): give ring items a kind and a name

IsBackButton was a bool; the ring is about to have three distinct non-action
affordances, and a bool cannot separate them at the place that decides what a
dwell does.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Build the ring in two layers

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLayerTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLayerTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void TheFirstLayer_ShowsFourActionsAndMore()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);

        Show(messenger);

        Check.That(vm.Items.Count).IsEqualTo(5);
        Check.That(vm.Items.Where(i => i.Kind == ActionMenuItemKind.Action).Select(i => i.Action))
             .ContainsExactly(
                 InteractionAction.LeftClick, InteractionAction.RightClick,
                 InteractionAction.DoubleClick, InteractionAction.ScrollMode);
        Check.That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.OpenMore)).IsEqualTo(1);
    }

    [Fact]
    public void DwellingMore_OpensTheSecondLayerWithoutClosingTheMenu()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);

        var more = vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore);
        vm.ActivateForTest(more.Id);

        Check.WithCustomMessage("More closed the menu").That(vm.IsVisible).IsTrue();
        Check.That(vm.Items.Where(i => i.Kind == ActionMenuItemKind.Action).Select(i => i.Action))
             .ContainsExactly(
                 InteractionAction.Drag, InteractionAction.ScrollUp,
                 InteractionAction.ScrollDown, InteractionAction.PinNote);
        Check.That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.BackToPrimary)).IsEqualTo(1);
    }

    [Fact]
    public void TheSecondLayer_ReusesTheSameFiveSlots()
    {
        // A slot's gaze path must be identical on both layers, so Back lands exactly where More
        // was. Anything else makes the user re-aim for a target they just looked at.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);

        var layerOnePositions = vm.Items.Select(i => i.Position).ToList();
        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);
        var layerTwoPositions = vm.Items.Select(i => i.Position).ToList();

        Check.That(layerTwoPositions).ContainsExactly(layerOnePositions);
    }

    [Fact]
    public void DwellingBackOnTheSecondLayer_ReturnsToTheFirst()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);
        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.BackToPrimary).Id);

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.OpenMore)).IsEqualTo(1);
    }

    [Fact]
    public void ReopeningTheMenu_AlwaysStartsOnTheFirstLayer()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);
        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        messenger.Send(new HideActionMenuMessage());
        Show(messenger);

        Check.WithCustomMessage("The menu reopened on layer two")
             .That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.OpenMore)).IsEqualTo(1);
    }

    [Fact]
    public void WithNoSecondaryActions_ThereIsNoMoreTarget()
    {
        var settings = Defaults();
        settings.ActionMenu.VisibleActions = new List<InteractionAction>
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
        };
        settings.ActionMenu.PrimaryActions = new List<InteractionAction>
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
        };

        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(settings, messenger);
        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(900, 500),
            settings.ActionMenu.VisibleActions,
            ShowBack: false,
            DwellTimeMs: 600,
            new PhysicalPoint(900, 500),
            CircleSize: 250));

        Check.WithCustomMessage("A More target that opens an empty layer is a dead end")
             .That(vm.Items.Any(i => i.Kind == ActionMenuItemKind.OpenMore)).IsFalse();
        Check.That(vm.Items.Count).IsEqualTo(2);
    }
```

Add `using System.Linq;` and `using System.Collections.Generic;` to the file.

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelLayerTests"
```

Expected: FAIL to **compile**, `CS1061: 'ActionMenuViewModel' does not contain a definition for 'ActivateForTest'`.

- [ ] **Step 3: Rebuild the ring around layers**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, add the settings field (the constructor currently discards its `settings` parameter):

```csharp
    private readonly EyeCtrlSettings _settings;
    private bool _onSecondaryLayer;
    private ShowActionMenuMessage? _shownWith;
```

and assign it in the constructor body:

```csharp
        _settings = settings;
```

Replace `Receive(ShowActionMenuMessage)` in full:

```csharp
    public void Receive(ShowActionMenuMessage message)
    {
        Center = message.Center;
        ManualSelectionCenter = message.ManualSelectionCenter;
        ManualSelectionRadius = message.ManualSelectionRadius;
        GazePoint = message.GazePoint;
        ShowBack = message.ShowBack;

        // A fresh open always lands on layer one. Reopening onto whichever layer was last used
        // would put a different target under the slot the user is already looking at.
        _onSecondaryLayer = false;
        _shownWith = message;

        BuildLayer();
        IsVisible = true;
    }

    /// <summary>
    /// Rebuilds the ring for the current layer. Called on open and on every layer change — the
    /// gaze engine is never involved, so a layer change costs no round trip and cannot race the
    /// engine's own state machine.
    /// </summary>
    private void BuildLayer()
    {
        var message = _shownWith;
        if (message is null) return;

        Items.Clear();
        DwellDetector.ClearTargets();
        _itemLookup.Clear();

        // Partition what the MESSAGE asked for, not what settings hold. Production is
        // equivalent — GazeEngineAdapter.cs:134-136 passes settings.ActionMenu.VisibleActions —
        // but every existing test drives the ring by sending a hand-picked action list
        // (ActionMenuViewModelTests.cs:27,59; ActionMenuViewModelPinNoteTests.cs:27-31; plan 2's
        // ActionMenuViewModelSymbolTests sends one action and calls Items.Single()). Reading
        // settings here instead would silently ignore the message and break all of them.
        var primaryList = _settings.ActionMenu.PrimaryActions ?? new List<InteractionAction>();
        var primary = message.Actions.Where(a => primaryList.Contains(a)).ToList();
        var secondary = message.Actions.Where(a => !primaryList.Contains(a)).ToList();

        var allItems = new List<(string Id, InteractionAction? Action, ActionIcon Icon, string Label, ActionMenuItemKind Kind)>();

        if (_onSecondaryLayer)
        {
            foreach (var action in secondary)
                allItems.Add((action.ToString(), action, GetActionIcon(action), FriendlyName(action), ActionMenuItemKind.Action));
            allItems.Add(("BackToPrimary", null, ActionIcon.Back, "Back", ActionMenuItemKind.BackToPrimary));
        }
        else
        {
            foreach (var action in primary)
                allItems.Add((action.ToString(), action, GetActionIcon(action), FriendlyName(action), ActionMenuItemKind.Action));

            // No More target when it would open an empty layer.
            if (secondary.Count > 0)
                allItems.Add(("More", null, ActionIcon.More, "More", ActionMenuItemKind.OpenMore));

            // The zoom Back only exists on layer one. From layer two it takes two dwells — back
            // to primary, then Back — because one nav slot cannot mean two things.
            if (message.ShowBack)
                allItems.Add(("Back", null, ActionIcon.Back, "Back", ActionMenuItemKind.ExitMenu));
        }

        if (allItems.Count == 0) return;

        // The window redraws only when something it subscribes to changes. It listens to
        // ActionMenuViewModel.IsVisible (OverlayVmBindingCoordinator.cs:259) and to each item's
        // DwellProgress, and nothing else — Items is a plain ObservableCollection whose
        // PropertyChanged never fires. Without this counter a layer switch would repoint the
        // DwellDetector while the canvas still showed layer one: the slot drawn as "More" would
        // activate Back, and the four action slots would fire the SECOND layer's actions. Bump it
        // last, so a subscriber that rebuilds synchronously sees a finished item list.
        RingVersion++;

        EffectiveRingRadius = message.CircleSize / 2 + UIConstants.ActionMenuButtonRadius + UIConstants.ActionMenuButtonOuterGap;
        var physicalRingRadius = DpiHelper.LogicalToPhysicalX(EffectiveRingRadius);
        var physicalButtonRadius = DpiHelper.LogicalToPhysicalX(UIConstants.ActionMenuButtonRadius);

        var angleStep = MathConstants.FullCircleDegrees / allItems.Count;
        for (int i = 0; i < allItems.Count; i++)
        {
            var (id, action, icon, label, kind) = allItems[i];
            var angleRad = (angleStep * i - MathConstants.RightAngleDegrees) * MathConstants.DegreesToRadians;
            var bx = message.Center.X + physicalRingRadius * Math.Cos(angleRad);
            var by = message.Center.Y + physicalRingRadius * Math.Sin(angleRad);
            var position = new PhysicalPoint(bx, by);

            var itemVm = new ActionMenuItemViewModel(id, action, icon, label, position, kind);
            Items.Add(itemVm);
            _itemLookup[id] = itemVm;

            DwellDetector.AddTarget(new DwellTarget(id, position, physicalButtonRadius, message.DwellTimeMs));
        }
    }
```

Replace `OnTargetActivated`:

```csharp
    protected override void OnTargetActivated(string targetId)
    {
        if (!_itemLookup.TryGetValue(targetId, out var item)) return;

        switch (item.Kind)
        {
            case ActionMenuItemKind.OpenMore:
                _onSecondaryLayer = true;
                BuildLayer();
                break;

            case ActionMenuItemKind.BackToPrimary:
                _onSecondaryLayer = false;
                BuildLayer();
                break;

            case ActionMenuItemKind.ExitMenu:
                Messenger.Send(new BackSelectedMessage());
                break;

            case ActionMenuItemKind.Action when item.Action.HasValue:
                Messenger.Send(new ActionSelectedMessage(item.Action.Value));
                break;
        }
    }
```

In `Receive(HideActionMenuMessage)`, reset the layer state:

```csharp
        _onSecondaryLayer = false;
        _shownWith = null;
```

Add the test seam beside `OnTargetActivated`:

```csharp
    /// <summary>
    /// Drives one activation without a DwellDetector, for tests. Layer changes are the only
    /// behaviour in this class that a caller cannot reach through a message, because they happen
    /// entirely inside a dwell activation.
    /// </summary>
    internal void ActivateForTest(string targetId) => OnTargetActivated(targetId);
```

`EyeCtrl.ViewModels` must already expose internals to its test project for this to bind. Check:

```bash
grep -rn "InternalsVisibleTo" src/EyeCtrl.ViewModels/
```

If absent, add to `src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj` inside an `<ItemGroup>`:

```xml
    <InternalsVisibleTo Include="EyeCtrl.ViewModels.Tests" />
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelLayerTests"
```

Expected: PASS, 8 tests.

- [ ] **Step 5: Make the window redraw when the layer changes**

`ActionMenuWindow.Show` is called from exactly one place — `OverlayVmBindingCoordinator.cs:267`, inside `if (e.PropertyName == nameof(ActionMenuViewModel.IsVisible))`. A layer switch does not touch `IsVisible`, so nothing redraws.

In `OverlayVmBindingCoordinator`, extract the existing `actionMenu.Show(...)` call (lines 267-278) into a private method — keep its arguments exactly as they are — and call it from both branches:

```csharp
        if (e.PropertyName == nameof(ActionMenuViewModel.IsVisible))
        {
            if (_context.ActionMenuVM.IsVisible)
            {
                cursorCircle.Hide();
                activationButton.HideImmediate();
                ShowBackdropIfEnabled();
                ShowActionMenu();
            }
            else
            {
                actionMenu.Hide();
                HideBackdrop();
            }
        }
        else if (e.PropertyName == nameof(ActionMenuViewModel.RingVersion)
                 && _context.ActionMenuVM.IsVisible)
        {
            // A layer switch. Same call, so the ring, the preview capture and the indicator all
            // rebuild together and the canvas can never disagree with the DwellDetector.
            ShowActionMenu();
        }
```

Write a test for the wiring itself in `ActionMenuViewModelLayerTests`:

```csharp
    [Fact]
    public void ALayerChange_RaisesRingVersion()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);
        var before = vm.RingVersion;

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        Check.WithCustomMessage(
                "Nothing told the window to redraw. The DwellDetector now targets layer two while "
                + "the canvas still shows layer one — every slot fires the wrong action.")
            .That(vm.RingVersion).IsStrictlyGreaterThan(before);
    }
```

- [ ] **Step 6: Update the ring-radius tests that now count five items, not eight**

`ActionMenuViewModelTests:233` asserts a distance from the centre across `vm.Items`. It still holds — every item sits on the ring whatever the layer. But any test asserting `vm.Items.Count == 8` now fails. Find them:

```bash
grep -n "Items.Count\|Items).HasSize\|Items).CountIs" tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelPinNoteTests.cs
```

Update each to the layer-one count of 5 (4 primary + More), or to 6 where `ShowBack: true`. Read the surrounding test before changing the number — a test that meant "every visible action appears" now means "every primary action appears", and its name may need to say so.

- [ ] **Step 7: Run the full gate and commit**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

`ActionMenuGeometryTests.EveryActionButton_LandsFullyOnCanvas` must stay green: fewer items on the same ring can only move buttons further apart.

```bash
git add src/EyeCtrl.ViewModels/ src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj tests/EyeCtrl.ViewModels.Tests/
git commit -m "feat(actionmenu): four primary targets plus More, with a second layer behind it

Both layers use the same five slots, so Back lands where More was and a slot's
gaze path is identical either side. Five 140 DIP targets clear each other at
every permitted CircleSize, which closes the overlap the eight-target ring had
below CircleSize 106.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Labels, off by default

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Modify: `src/EyeCtrl.Controls/DwellArcButton.cs`
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLabelTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLabelTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Labels are off until the Help target is dwelled, and they do not survive the menu closing.
/// </summary>
public class ActionMenuViewModelLabelTests
{
    private static void Show(IMessenger messenger) =>
        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(900, 500),
            new EyeCtrlSettings().ActionMenu.VisibleActions,
            ShowBack: false,
            DwellTimeMs: 600,
            new PhysicalPoint(900, 500),
            CircleSize: 250));

    [Fact]
    public void LabelsAreOff_WhenTheMenuOpens()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);

        Show(messenger);

        Check.That(vm.ShowLabels).IsFalse();
    }

    [Fact]
    public void TheToggleMessage_TurnsLabelsOn()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);

        messenger.Send(new ToggleActionMenuLabelsMessage());

        Check.That(vm.ShowLabels).IsTrue();
    }

    [Fact]
    public void TheToggleMessage_TurnsThemOffAgain()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);

        messenger.Send(new ToggleActionMenuLabelsMessage());
        messenger.Send(new ToggleActionMenuLabelsMessage());

        Check.That(vm.ShowLabels).IsFalse();
    }

    [Fact]
    public void LabelsSurviveALayerChange()
    {
        // Switching to More is not a reason to lose the names — the second layer is exactly where
        // a user who just asked what things are called still needs them.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);
        messenger.Send(new ToggleActionMenuLabelsMessage());

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        Check.That(vm.ShowLabels).IsTrue();
    }

    [Fact]
    public void ClosingTheMenu_ForgetsTheLabels()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);
        messenger.Send(new ToggleActionMenuLabelsMessage());

        messenger.Send(new HideActionMenuMessage());
        Show(messenger);

        Check.WithCustomMessage("Labels persisted across a menu session. That is a settings-level "
                                + "choice and is not what this toggle is.")
             .That(vm.ShowLabels).IsFalse();
    }

    [Fact]
    public void EveryItem_CarriesANameWhetherOrNotItIsShown()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);

        foreach (var item in vm.Items)
            Check.WithCustomMessage($"{item.Id} has no label")
                 .That(string.IsNullOrWhiteSpace(item.Label)).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelLabelTests"
```

Expected: FAIL to **compile**, `CS0246: The type or namespace name 'ToggleActionMenuLabelsMessage' could not be found`.

- [ ] **Step 3: Add the message**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, add beside `BackSelectedMessage`:

```csharp
/// <summary>
/// Toggles the action names on the ring's targets. Sent by the Help button on the right-edge bar.
///
/// A toggle rather than a momentary reveal: you cannot read a label while dwelling something
/// else, so a "names while held" gesture would show them only when they are least useful. Scoped
/// to the menu session — <see cref="HideActionMenuMessage"/> clears it.
/// </summary>
public record ToggleActionMenuLabelsMessage;
```

- [ ] **Step 4: Handle it in the ViewModel**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, add the interface to the class declaration:

```csharp
public partial class ActionMenuViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowActionMenuMessage>,
    IRecipient<HideActionMenuMessage>,
    IRecipient<ToggleActionMenuLabelsMessage>
```

Add the property beside the other observables:

```csharp
    /// <summary>
    /// Incremented every time the ring's items are rebuilt. The only signal the window gets that
    /// a layer changed — see <c>BuildLayer</c> for why Items cannot serve.
    /// </summary>
    [ObservableProperty] private int _ringVersion;

    /// <summary>
    /// Whether the ring shows each target's name. Off on every open; the Help target on the
    /// right-edge bar turns it on for the rest of the menu session.
    /// </summary>
    [ObservableProperty] private bool _showLabels;
```

Add the handler:

```csharp
    public void Receive(ToggleActionMenuLabelsMessage message) => ShowLabels = !ShowLabels;
```

In `Receive(ShowActionMenuMessage)`, add beside the layer reset:

```csharp
        ShowLabels = false;
```

and the same in `Receive(HideActionMenuMessage)`.

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelLabelTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 6: Let DwellArcButton draw a label under the icon**

In `src/EyeCtrl.Controls/DwellArcButton.cs`, add a parameter after `icon`:

```csharp
        IconGeometry? icon = null,
        string? label = null)
```

Inside the `if (icon is not null)` block, replace the icon-size line so a labelled button shrinks its icon:

```csharp
            // Medium, not Dominant, when a label shares the face: the icon is no longer the
            // button's entire content, which is the situation Dominant names. 140 x 0.32 = 44.8.
            var iconSize = dia * (label is null ? ContainerRatio.Dominant : ContainerRatio.Medium);
```

and offset the icon upward when labelled so icon and label are optically centred together:

```csharp
            var labelHeight = label is null ? 0 : Tokens.FontSizeSubtitle * 1.1;
            var stackGap = label is null ? 0 : dia * LabelGapRatio;
            var iconTop = center.Y - (iconSize + stackGap + labelHeight) / 2;
```

using `Canvas.SetTop(path, iconTop)` in `BuildLayer` instead of `center.Y - iconSize / 2`.

Add the ratio constant beside `BadgeDiameterThreshold`:

```csharp
    /// <summary>
    /// Gap between a button's icon and its label, as a fraction of the diameter. 0.05 of 140 is
    /// 7 DIP. Named rather than written inline so RawGeometryLiteralTests's bare-number rule does
    /// not flag the expression that reads it.
    /// </summary>
    private const double LabelGapRatio = 0.05;
```

Add the label TextBlock after the icon block:

```csharp
        TextBlock? labelText = null;
        if (label is not null)
        {
            labelText = new TextBlock
            {
                Text = label,
                Foreground = Tokens.BrushTextPrimary,
                FontSize = Tokens.FontSizeSubtitle,
                FontWeight = Tokens.FontWeightMedium,
                FontFamily = Tokens.FontStandard,
                TextAlignment = TextAlignment.Center,
                Width = dia,
            };
            labelText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(labelText, center.X - radius);
            Canvas.SetTop(labelText, iconTopForLabel + iconSizeForLabel + dia * LabelGapRatio);
        }
```

Hoist `iconSize` and `iconTop` out of the `if (icon is not null)` block into locals declared above it (`iconSizeForLabel`, `iconTopForLabel`) so the label can read them; both are 0 when there is no icon.

Add `Label` to the properties, the private constructor and the `return`, mirroring `IconStroke`, and add it to `AddTo` last:

```csharp
        if (Label is not null)
            canvas.Children.Add(Label);
```

- [ ] **Step 7: Pass the label from the window**

In `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`, the `Show` signature gains a flag and the button call gains a label. Add `bool showLabels` as a parameter and:

```csharp
                var btn = DwellArcButton.Create(
                    canvasPoint,
                    ActionMenuGeometry.ButtonRadius,
                    symbol: null,
                    icon: ActionIcons.For(itemVm.Icon),
                    label: showLabels ? itemVm.Label : null);
```

Find the caller and pass `vm.ShowLabels`:

```bash
grep -rn "ActionMenu.Show(" src --include=*.cs | grep -v "/obj/"
```

The window must also rebuild when `ShowLabels` changes. **Do not subscribe inside `Show`** — a handler attached there accumulates one instance per rebuild, and you cannot `-=` a lambda you did not keep. (`EdgeButtonBarWindow.xaml.cs:17,49-51` hits this exact hazard and solves it by *storing* the handlers in a field.)

Task 4 Step 5 already routes redraws through the coordinator, so extend that condition instead:

```csharp
        else if ((e.PropertyName == nameof(ActionMenuViewModel.RingVersion)
                  || e.PropertyName == nameof(ActionMenuViewModel.ShowLabels))
                 && _context.ActionMenuVM.IsVisible)
        {
            ShowActionMenu();
        }
```

`ShowActionMenu()` reads `_context.ActionMenuVM.ShowLabels` when it builds its argument list, so both triggers take the same path and there is nothing to keep in sync. Nothing inside `ActionMenuWindow.Show` needs a recursive call.

- [ ] **Step 8: Run the full gate and commit**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

`ContainerRatioCensusTests` will fail: `DwellArcButton.cs` now references `Medium` too. Update its entry to add:

```csharp
                ("Medium", 1, "the icon on a 140px action-menu button that is ALSO showing a "
                    + "label — the icon is no longer the button's entire content, so Dominant "
                    + "stops applying. 140 x 0.32 = 44.8px."),
```

```bash
git add src/EyeCtrl.ViewModels/ src/EyeCtrl.Controls/DwellArcButton.cs src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs tests/
git commit -m "feat(actionmenu): action names behind a toggle, off by default

Icon drops 63 -> 44 DIP when a label shares the face. Ring, slots and target
sizes are unchanged either way, so revealing a name never moves the thing the
user was already aiming at.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Populate the right-edge bar with Close and Help

**Files:**
- Modify: `src/EyeCtrl.Core/Models/ActionIcon.cs` — add `Close`
- Modify: `src/EyeCtrl.Controls/Icons/ActionIcons.cs` — geometry for it
- Modify: `src/EyeCtrl.ViewModels/EdgeButtonDefinition.cs` — optional `Icon`
- Modify: `src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs` — render an icon when named
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs`
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs:192-199` — `OnActionMenuDismissed` must cover the ManualSelection path
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` — `ActionMenuDismissedMessage`
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:86` — register it
- Create: `src/EyeCtrl.App/Services/ActionMenuEdgeButtons.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ActionMenuEdgeButtonsTests.cs`

**The action menu is the only overlay context that puts nothing on the right-edge bar**, and that is a hole worth closing in the same task. Every other context populates it, and each one that can be abandoned offers a `✕`:

| Context | Buttons | Source |
|---|---|---|
| Manual selection | `✕` close at `-offset`, `↻` recapture at `+offset` | `OverlayVmBindingCoordinator.cs:233-241` |
| Drag | `✕` cancel | `DragCoordinator.cs:73-78` |
| Scroll mode | `⏹` stop | `ScrollModeController.cs:31-36` |
| **Action menu** | **nothing** | — |

So today, once the menu opens, every target commits an action: `Back` exists only on the `ShowBack` path (`ActionMenuViewModel.cs:47-48`). An accidental open has no exit. Close and Help go on together, in the manual-selection layout verbatim.

**Use `ShowButtons`, not `AddButton`.** `AddButton` sets neither `IsVisible` (only `ShowButtons` does, `EdgeButtonBarViewModel.cs:51`) nor `_edgeAnchor` (`:41`), so a bar that was not already showing stays hidden and anchors every button at `(0,0)`. `ShowButtons` is also what every other context uses, and the "it would clear a drag's cancel button" worry does not apply: the menu closes before a drag begins.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Services/ActionMenuEdgeButtonsTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The action menu's right-edge buttons. Close is the point of this task: until 2026-08-26 the
/// menu was the one overlay context that put nothing on the bar, so an accidental open could only
/// be resolved by committing an action.
/// </summary>
public class ActionMenuEdgeButtonsTests
{
    [Fact]
    public void TheBarCarries_CloseAndHelp()
    {
        var defs = ActionMenuEdgeButtons.Definitions(dwellMs: 600);

        Check.That(defs.Select(d => d.Id)).ContainsExactly("ActionMenu_close", "ActionMenu_help");
        Check.That(defs.Select(d => d.Icon)).ContainsExactly(ActionIcon.Close, ActionIcon.Help);
    }

    [Fact]
    public void TheyUseTheManualSelectionSlots()
    {
        // Same two slots the manual-selection close/recapture pair occupies, so the bar's layout
        // is one convention rather than one per context.
        var defs = ActionMenuEdgeButtons.Definitions(dwellMs: 600);

        Check.That(defs[0].VerticalOffset).IsStrictlyLessThan(0);
        Check.That(defs[1].VerticalOffset).IsEqualTo(-defs[0].VerticalOffset);
        Check.That(defs.Select(d => d.RadiusLogical)).ContainsExactly(
            UIConstants.ButtonRadius, UIConstants.ButtonRadius);
    }

    [Fact]
    public void Close_GoesThroughTheStateMachine()
    {
        // NOT HideActionMenuMessage. That hides the overlay while InteractionManager stays in
        // ActionMenuVisible / ManualSelectionActionMenuVisible, so gaze does nothing until
        // something else resets the state — the menu is gone and the app is wedged. Back already
        // routes this way (GazeEngineAdapter.cs:86-87 maps BackSelectedMessage to
        // OnManualSelectionBack); Close takes the same shape.
        var messenger = new WeakReferenceMessenger();
        var dismissed = 0;
        messenger.Register<ActionMenuDismissedMessage>(this, (_, _) => dismissed++);

        ActionMenuEdgeButtons.Definitions(dwellMs: 600)
            .Single(d => d.Id == "ActionMenu_close").Activate(messenger);

        Check.That(dismissed).IsEqualTo(1);
    }

    [Fact]
    public void Help_SendsTheLabelToggle()
    {
        var messenger = new WeakReferenceMessenger();
        var toggled = 0;
        messenger.Register<ToggleActionMenuLabelsMessage>(this, (_, _) => toggled++);

        ActionMenuEdgeButtons.Definitions(dwellMs: 600)
            .Single(d => d.Id == "ActionMenu_help").Activate(messenger);

        Check.That(toggled).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ActionMenuEdgeButtonsTests"
```

Expected: FAIL to **compile**, `CS0103: The name 'ActionMenuEdgeButtons' does not exist`.

- [ ] **Step 3: Add the Close icon**

In `src/EyeCtrl.Core/Models/ActionIcon.cs`, after `Help`:

```csharp
    /// <summary>An X — dismisses the menu without performing anything. Navigation, not an action.</summary>
    Close,
```

In `ActionIcons.cs`'s `Build()`:

```csharp
        [ActionIcon.Close] = Make("M6,6 L18,18 M18,6 L6,18", null),
```

Plan 2's `ActionIconsTests.EveryActionIcon_HasGeometry` covers the new member; no new icon test is needed.

- [ ] **Step 4: Let an edge button name an icon**

Replace `src/EyeCtrl.ViewModels/EdgeButtonDefinition.cs`'s record declaration:

```csharp
public sealed record EdgeButtonDefinition(
    string Id,
    string Symbol,
    int DwellMs,
    Action<IMessenger> Activate,
    double VerticalOffset,
    double RadiusLogical = UIConstants.DragButtonRadius,
    ActionIcon? Icon = null,
    string? Label = null);
```

Add `using EyeCtrl.Core.Models;`, and to the doc comment:

```csharp
/// <paramref name="Icon"/> names a drawn icon to render INSTEAD of <paramref name="Symbol"/>, and
/// <paramref name="Label"/> a name to show beneath it. Both optional and last, so every existing
/// definition keeps its glyph and is not edited.
```

`Label` is on the definition rather than special-cased in the window because two buttons need it here, and a second special case is how a convention becomes a mess.

- [ ] **Step 5: Render it in the bar window**

In `src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs`, the `DwellArcButton.Create` call is at line 83. Resolve the definition's icon:

```csharp
            var definition = vm.DefinitionFor(state.Id);
            var btn = DwellArcButton.Create(
                point,
                radius,
                symbol: definition?.Icon is null ? state.Symbol : null,
                icon: definition?.Icon is { } iconKey ? ActionIcons.For(iconKey) : null,
                label: definition?.Label);
```

Add `using EyeCtrl.Controls.Icons;`. Read line 83's existing argument names first — `point` and `radius` are this plan's names for whatever locals are already there.

`EdgeButtonBarViewModel` has no way to reach a definition from an id; add one beside `SeedCenters`:

```csharp
    /// <summary>The definition behind a button id, or null. The window needs it to resolve an icon.</summary>
    public EdgeButtonDefinition? DefinitionFor(string id) =>
        _definitions.TryGetValue(id, out var def) ? def : null;
```

- [ ] **Step 6: Add the definitions and show them with the menu**

Create `src/EyeCtrl.App/Services/ActionMenuEdgeButtons.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The right-edge buttons shown while the action menu is open: Close, which dismisses it without
/// performing anything, and Help, which toggles the ring's action names.
///
/// Slots and radius match the manual-selection pair (<c>OverlayVmBindingCoordinator</c>) exactly,
/// so the bar reads the same whichever context owns it.
/// </summary>
internal static class ActionMenuEdgeButtons
{
    internal const string CloseId = "ActionMenu_close";
    internal const string HelpId = "ActionMenu_help";

    /// <summary>Close above the anchor, Help below — the manual-selection layout verbatim.</summary>
    internal static IReadOnlyList<EdgeButtonDefinition> Definitions(int dwellMs)
    {
        var offset = DpiHelper.LogicalToPhysicalY(
            UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap / 2);

        return new[]
        {
            new EdgeButtonDefinition(
                CloseId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ActionMenuDismissedMessage()),
                VerticalOffset: -offset,
                RadiusLogical: UIConstants.ButtonRadius,
                Icon: ActionIcon.Close,
                Label: "Close"),
            new EdgeButtonDefinition(
                HelpId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ToggleActionMenuLabelsMessage()),
                VerticalOffset: offset,
                RadiusLogical: UIConstants.ButtonRadius,
                Icon: ActionIcon.Help,
                Label: "Help"),
        };
    }
}
```

Both keep their labels at all times: they are the two targets that cannot rely on being recognised, since one explains the others and the other is the escape hatch.

In `src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs`, inside the `ShowActionMenuMessage` handler (line 100), **before** its `if (msg.ResolvedControl == null …)` early return:

```csharp
                    var edgeAnchor = ScreenGeometry.RightEdgeAnchor(
                        RightEdgeInsetLogical, UIConstants.ButtonRadius);
                    _context.OverlayManager.EdgeButtonBar.ShowButtons(
                        ActionMenuEdgeButtons.Definitions(
                            msg.ShowBack
                                ? _context.Settings.Dwell.ManualSelectionActionMenuDwellMs
                                : _context.Settings.Dwell.ActionMenuDwellMs),
                        edgeAnchor);
```

The dwell mirrors `OverlayVmBindingCoordinator.cs:271`, so Close and Help take the same time as the ring targets beside them. `RightEdgeInsetLogical` is a private const on `OverlayVmBindingCoordinator`; read its value and declare an equivalent here rather than widening its accessibility.

In the `HideActionMenuMessage` handler (line 122), before its existing body:

```csharp
                    _context.OverlayManager.EdgeButtonBar.Hide();
```

- [ ] **Step 7: Route Close through the state machine**

Dismissing the menu is a state transition, not just a hidden window. `InteractionManager` already owns this — but its `OnActionMenuDismissed()` (`InteractionManager.cs:192-199`) guards on `CurrentState == ActionMenuVisible` only, so on the manual-selection path it is a silent no-op. Widen it to cover both, mirroring what `TogglePause` already does at `:216-218`:

```csharp
    /// <summary>
    /// Dismisses the action menu and returns to free look.
    ///
    /// Covers the ManualSelection path too, since 2026-08-26. It guarded on
    /// <see cref="InteractionState.ActionMenuVisible"/> alone while nothing could dismiss the menu
    /// from the ManualSelection path — the ring's Back returns to the magnifier
    /// (<see cref="OnManualSelectionBack"/>), which is a step back rather than a way out. The
    /// right-edge Close is a way out from either state, so the magnified view has to be torn down
    /// as well; <see cref="TogglePause"/> does the same pairing.
    /// </summary>
    public void OnActionMenuDismissed()
    {
        if (CurrentState is not (InteractionState.ActionMenuVisible
                              or InteractionState.ManualSelectionActionMenuVisible)) return;

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
            EndManualSelection?.Invoke();
        ResetToFreeLook();
    }
```

Add the message to `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, beside `BackSelectedMessage`:

```csharp
/// <summary>
/// The user dismissed the action menu without choosing anything — the right-edge Close target.
/// Distinct from <see cref="HideActionMenuMessage"/>, which only tears down the overlay: this one
/// goes to <c>InteractionManager.OnActionMenuDismissed</c> so the state machine leaves the menu
/// too, and <see cref="HideActionMenuMessage"/> follows from there.
/// </summary>
public record ActionMenuDismissedMessage;
```

Register it in `GazeEngineAdapter`, immediately after the `BackSelectedMessage` registration at line 86:

```csharp
        messenger.Register<ActionMenuDismissedMessage>(this, (_, _) =>
            _interactionManager.OnActionMenuDismissed());
```

Cover the widened guard in `tests/EyeCtrl.Core.Tests` — find the file holding the existing `InteractionManager` state tests and add:

```csharp
    [Fact]
    public void OnActionMenuDismissed_FromTheManualSelectionPath_EndsManualSelectionToo()
    {
        var im = new InteractionManager();
        var endedManualSelection = 0;
        im.EndManualSelection += () => endedManualSelection++;

        // Drive to ManualSelectionActionMenuVisible the way the app does.
        im.SelectionMode = SelectionMode.ManualSelection;
        im.OnFixationStarted(new PhysicalPoint(400, 400));
        im.OnActivationDwell();
        im.OnManualSelectionDwell(new PhysicalPoint(410, 410));
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);

        im.OnActionMenuDismissed();

        Check.WithCustomMessage("The magnified view was left on screen.")
             .That(endedManualSelection).IsEqualTo(1);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }
```

Read the existing tests first — `SelectionMode` and the activation-dwell entry point are named from `InteractionManager`'s public surface, and the setup above must match whatever the neighbouring tests already do.

- [ ] **Step 8: The ShowBack overlap — settled, nothing to do**

This was an open question until the state machine was traced. **Manual selection tears its buttons down before the menu shows, so `ShowButtons` is correct on both paths and needs no `ShowBack` special case.** The trace, in order:

1. `InteractionManager.OnManualSelectionDwell` (`InteractionManager.cs:116-123`) fires `HideManualSelection` on line 120 and `ShowActionMenu` on line **121** — hide first, by construction.
2. Both handlers wrap in `_dispatcher.BeginInvoke` with no priority argument (`GazeEngineAdapter.cs:188` and `:108`), so both queue at `DispatcherPriority.Normal`, which is FIFO. `ManualSelectionHiddenMessage` is delivered before `ShowActionMenuMessage`.
3. `ManualSelectionOverlayViewModel.Receive(ManualSelectionHiddenMessage)` sets `IsVisible = false` (`ManualSelectionOverlayViewModel.cs:45-48`).
4. `OverlayVmBindingCoordinator.OnManualSelectionVisibilityChanged` sees the false and calls `EdgeButtonBar.Hide()` (`:245-248`).
5. Only then does `ShowActionMenuMessage` arrive and Step 6's `ShowButtons` run.

The reverse direction holds for the same reason: `OnManualSelectionBack` (`:172-181`) fires `HideActionMenu` before `RestoreManualSelectionView`, so the menu's bar is torn down before manual selection repopulates it.

Two consequences worth keeping in mind rather than fixing:

- **The bar hides and reshows within one dispatcher drain** on the ManualSelection→menu transition, because `Hide()` and `ShowButtons` both drive `IsVisible` and `OverlayManager.cs:94-101` maps that straight onto window visibility. If that reads as a flicker on the real display, the fix is to make `ShowButtons` skip the hide when the bar is already visible — not to reorder anything here.
- **Step 6's ordering claim now depends on `BeginInvoke` FIFO.** That is a real WPF guarantee, but it is invisible at the call site. Leave a comment naming it where the `ShowButtons` call goes, so a future change to dispatcher priorities cannot quietly invert the sequence.

- [ ] **Step 9: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ActionMenuEdgeButtonsTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 10: Run the full gate and commit**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

```bash
git add src/EyeCtrl.Core/Processing/InteractionManager.cs src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.App/Services/GazeEngineAdapter.cs src/EyeCtrl.App/Services/ActionMenuEdgeButtons.cs src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs src/EyeCtrl.ViewModels/EdgeButtonDefinition.cs src/EyeCtrl.ViewModels/EdgeButtonBarViewModel.cs src/EyeCtrl.Core/Models/ActionIcon.cs src/EyeCtrl.Controls/Icons/ActionIcons.cs tests/EyeCtrl.App.Tests/Services/ActionMenuEdgeButtonsTests.cs
git commit -m "feat(actionmenu): give the menu a Close and a Help on the right-edge bar

The action menu was the only overlay context that put nothing on the bar, so an
accidental open could only be resolved by committing an action - Back exists
only on the manual-selection path.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: Let the user choose the primary four

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs`

The tab currently has one toggle per action. It now needs a second axis: on/off, and for the ones that are on, primary or secondary.

- [ ] **Step 1: Add a primary toggle per visible action**

In `src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs`, after the existing `foreach` that builds the visibility toggles, add a second loop:

```csharp
        // Second axis: of the actions that are ON, which four sit on the ring's first layer. A
        // toggle rather than a picker because the settings surface is dwell-driven — a
        // multi-select control would need its own dwell affordance, and this reuses the row type
        // every other setting here already uses.
        foreach (var action in Enum.GetValues<InteractionAction>())
        {
            var captured = action;
            rows.Add(new ToggleSpec(
                $"am_primary_{action}",
                $"{FriendlyName(action)} on first layer",
                () => a.PrimaryActions.Contains(captured),
                on =>
                {
                    if (on)
                    {
                        if (a.PrimaryActions.Contains(captured)) return;
                        if (a.PrimaryActions.Count >= Core.Settings.ActionMenuSettings.MaxPrimaryActions)
                        {
                            _ctx.ShowModal(
                                $"The first layer holds {Core.Settings.ActionMenuSettings.MaxPrimaryActions} actions. "
                                + "Turn one off before adding another.",
                                () => { });
                            _ctx.RebuildContent();
                            return;
                        }
                        a.PrimaryActions.Add(captured);
                    }
                    else
                    {
                        a.PrimaryActions.RemoveAll(x => x == captured);
                    }
                    _ctx.ApplyAndSave();
                }));
        }
```

Update the reset button's body to restore both lists:

```csharp
                var defaults = new Core.Settings.ActionMenuSettings();
                a.VisibleActions = new List<InteractionAction>(defaults.VisibleActions);
                a.PrimaryActions = new List<InteractionAction>(defaults.PrimaryActions);
```

- [ ] **Step 2: Run the settings guard tests**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Settings"
```

Expected: **FAIL** on `LayoutActionMenuPageSourceTests` (`tests/EyeCtrl.App.Tests/Settings/Pages/LayoutActionMenuPageSourceTests.cs:33-34`), which pins the tab's toggle ids with `ContainsExactly("am_" + action)` for every action, and `:45`, which pins the single reset button. Extend both to cover the `am_primary_*` row per action, then re-run.

After that, expect PASS. This tab sets `PrefersAutoPagination => true`, so doubling its row count repaginates rather than overflowing — `SettingsContentFitTests` and `SettingsTabLabelFitTests` are the two that would catch a row too tall or a label too wide for its column. If `SettingsTabLabelFitTests` fails, shorten the row label (`$"{FriendlyName(action)} — first layer"`), not the column.

- [ ] **Step 3: Run the full gate and commit**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs
git commit -m "feat(settings): choose which four actions sit on the ring's first layer

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: Confirm the affected scope, then ask before looking

**Files:** none modified.

- [ ] **Step 1: Run change detection against the default branch**

```
detect_changes({scope: "compare", base_ref: "main"})
```

Expected: `ActionMenuSettings`, `ActionMenuViewModel`, `ActionMenuItemViewModel`, `ActionIcon`, `ActionIcons`, `DwellArcButton.Create`, `EdgeButtonDefinition`, `EdgeButtonBarWindow.SyncButtons`, `OverlayMessageCoordinator`, `ActionMenuEdgeButtons`, `ActionMenuPageSource`, plus tests.

**A hit naming `DragCoordinator` or `ScrollModeController` means Task 6 touched the shared edge bar's other contexts.** It must not have. Re-read the `AddButton` vs `ShowButtons` choice.

- [ ] **Step 2: Verify the three-plan claim about overlap**

```bash
grep -rn "MaxPrimaryActions" src --include=*.cs | grep -v "/obj/"
```

Confirm the cap is 4 and that `Validate()` enforces it.

Two separate facts, worth not conflating: **five slots never overlap at any permitted `CircleSize`** (the chord is `2R·sin(36°)` = 159 DIP against a 140 DIP button even at `CircleSize` 10), which is what closes the hole plans 1 and 2 both documented and neither fixed. The cap at *four* primary is a separation choice on top of that — six slots would still clear, at a 115 DIP neighbour gap instead of 160. Raising the cap would not reintroduce overlap; it would just make the ring something to read rather than aim at.

- [ ] **Step 3: Ask before running the app**

Everything above is proved headless. What no test can show: whether four targets plus More is the right four, whether the ring reads at a glance without labels, and whether one dwell on Help is a natural reach from the ring.

**Do not launch EyeCtrl without an explicit go-ahead.** Input simulation and UI automation seize this user's only input channel and can cut them off from their caregiver. Ask, wait for a clear yes, keep the run short.

If they decline, hand over with the visual behaviour unverified and say so. That is honest and recoverable; an unasked-for run is not.

- [ ] **Step 4: Commit nothing**

This task produces no diff.

---

## What this plan deliberately does not do

- **No per-action dwell times.** The canvas's error-safety direction (longer dwell for destructive actions, a cancel target, undo after a mis-selection) was not among the two the user chose. `DwellDetector.UpdateTargetDwellTime` already exists, so this is a small plan if it is ever wanted.
- **No persisted label preference.** Help is per-menu-session. A "always show names" setting is a reasonable follow-up and would be one `ToggleSpec` plus a seed for `ShowLabels`.
- **No layer-two Help variation.** The Help target behaves identically on both layers.
- **The zoom Back stays on layer one only**, so leaving zoom from layer two takes two dwells. Named at the top of this plan as a decision to disagree with before building, not after.

## Self-review

**Spec coverage:** the canvas's resting-state artboard is Tasks 3–5 (four actions plus More, icon-only at 63 DIP); the after-dwelling-Help artboard is Tasks 5–6 (names at 44 DIP, Help on the right-edge bar with its own permanent label); the More-layer artboard is Task 4 (same five slots, Back where More was); the settings split is Tasks 1 and 7. The `assumed` annotation's four open questions are restated at the top of this plan as decisions to settle before coding rather than buried.

**Placeholders:** none. Three steps direct the engineer to `grep` for a name this plan cannot know from the source it read (`EnsureDefaultsOnce` call sites, the action-menu dwell property, `DwellArcButton.Create` inside `EdgeButtonBarWindow`); each says exactly what to do with either answer, which is a lookup rather than a gap.

**Type consistency:** `ActionMenuItemKind` is declared in Task 3 Step 3 and its four members are used by name in Tasks 3, 4 and 5. `ActionMenuSettings.EffectivePrimaryActions`, `SecondaryActions`, `MaxPrimaryActions`, `MigratePrimaryOnce` and `PrimaryMigrated` are declared in Task 1 Step 3 and consumed in Tasks 4, 7 and 8. `ToggleActionMenuLabelsMessage` is declared in Task 5 Step 3 and consumed in Task 5 Step 4 and Task 6 Step 6. `ActionMenuEdgeButtons.CloseId` / `.HelpId` / `.Definitions` are declared in Task 6 Step 6 and used in that task's tests and coordinator wiring. `ActionIcon.More` / `.Help` are added in Task 2, and `.Close` in Task 6 Step 3, each before first use. `DwellArcButton`'s `label` parameter is added in Task 5 Step 6 before Task 6 Step 4 passes it.

**One gap I could not close from source:** Task 6 Step 4 assumes `EdgeButtonState` exposes `Symbol`. If it does not, read `EdgeButtonState.cs` and use whatever the existing `DwellArcButton.Create` call in `SyncButtons` already passes — that call is the ground truth, and Step 4's grep surfaces it before the edit.
