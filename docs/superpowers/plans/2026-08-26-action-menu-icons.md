# Action Menu Drawn Icons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the action menu's Unicode glyphs (`☝ ☰ ✌ ✊ ▲ ▼ ◀ ▶ ⇅ 📌`) with drawn vector icons that recolour with the theme, scale with the target, and do not depend on which font Windows falls back to.

**Architecture:** Three layers, each owning one thing. `EyeCtrl.Core` gains an `ActionIcon` enum — a name, no drawing. `EyeCtrl.ViewModels` maps `InteractionAction → ActionIcon` exactly where it maps to a glyph today. `EyeCtrl.Controls` owns the path data and turns an `ActionIcon` into WPF `Geometry`. This keeps ViewModels free of `System.Windows.Media` and lets `EdgeButtonDefinition` (plan 3) name an icon without referencing Controls.

**Tech Stack:** C# / .NET 10 / WPF, `Geometry.Parse` (SVG path mini-language), xUnit v3 with NFluent assertions (`Check.That(...)`, never `Assert.*`).

---

**Prerequisite:** [2026-08-26-action-menu-geometry.md](2026-08-26-action-menu-geometry.md) must be merged. That plan raises the action-menu button to 140 DIP, which is what makes a 63 DIP icon possible.

**Followed by:** `2026-08-26-action-menu-disclosure.md` (the More layer and the Help label toggle), which adds two more icons to the registry this plan creates.

## Risk warning (CLAUDE.md requires this be surfaced before editing)

`impact({target: "DwellArcButton", direction: "upstream"})` returns **HIGH**: 15 impacted symbols, 3 modules, and **2 affected execution flows** — `OverlayManager.Initialize` and `ActionMenuWindow.Show`, both with `earliest_broken_step: 1`. `Create` has exactly three call sites — `ActionMenuWindow.xaml.cs:187`, `EdgeButtonBarWindow.xaml.cs:83`, `GlassBackdropWindow.xaml.cs:224` — so two of them are not the action menu, and the edge bar's drag buttons.

**Mitigation, and it is the design constraint for Task 3:** every change to `Create` is *additive*. The existing `Create(center, radius, symbol, …)` call shape keeps working unchanged, so the two non-action-menu call sites are not edited by this plan and their rendering does not move. Task 3's first test pins exactly that.

`impact({target: "ActionMenuViewModel", direction: "upstream"})` returns **HIGH**: 20 direct dependents across 3 modules.

## File structure

**Create:**
- `src/EyeCtrl.Core/Models/ActionIcon.cs` — the enum. No WPF, no drawing.
- `src/EyeCtrl.Controls/Icons/IconGeometry.cs` — a two-layer icon (stroked paths + filled paths)
- `src/EyeCtrl.Controls/Icons/ActionIcons.cs` — `ActionIcon → IconGeometry`, all path data
- `tests/EyeCtrl.Controls.Tests/Icons/ActionIconsTests.cs`
- `tests/EyeCtrl.Controls.Tests/DwellArcButtonIconTests.cs`

**Modify:**
- `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs` — carry `Icon`, drop `Symbol`
- `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:36-74, 112-126` — map to `ActionIcon`, retire `GetActionSymbol`
- `src/EyeCtrl.Controls/DwellArcButton.cs` — optional icon rendering
- `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs:187` — pass the icon
- `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs` — glyph assertions become icon assertions
- `tests/EyeCtrl.App.Tests/ContainerRatioCensusTests.cs:106-113` — `DwellArcButton.cs` gains a `Dominant` reference
- `tests/EyeCtrl.Controls.Tests/DwellArcButtonGlyphSizeTests.cs:38` — a doc comment that plan 1 made stale

## The icon grid

Every path in `ActionIcons.cs` is authored on a **24 × 24 grid** with a **1.8-unit stroke**, rendered through a `ScaleTransform` of `iconSize / 24`. `RenderTransform` scales the stroke along with the geometry (unlike `Geometry.Transform`, which would leave a hairline stroke at any size), so `StrokeThickness` stays 1.8 in code and never needs multiplying.

Icon size is `diameter × ContainerRatio.Dominant` — 140 × 0.45 = **63 DIP**. `Dominant` is the token whose own doc comment says "an affordance whose entire content is one glyph, with no label beside it", which is exactly the resting action-menu button.

Every action is a mouse action, so every icon is a mouse. Modifiers live in the margins, never on the body: scroll variants take chevrons at the right, drag takes motion bars at the left. Double click is the one icon that must differ in **outline** rather than detail — with no label it would otherwise be left click with a small mark — so it breaks the silhouette with two click ripples and carries its count out to the right margin.

## Verification commands

**`dotnet test` output on this machine is German**: a per-test failure reads `Fehler <FullyQualifiedName>`, not `Failed`, and long names wrap across lines. Grepping for `Failed` reports a green run over a red one — read the summary (`Fehler: N`).

Full gate for this plan (note `EyeCtrl.Controls.Tests`, which plan 1 did not need):

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Roughly 13 `AppLaunchTests` fail in any headless session with "Toolbar_Window did not appear within 15s". Environmental, identical on clean `main`, which is why the filter is not optional.

---

### Task 1: Name the icons in Core and carry one per menu item

**Files:**
- Create: `src/EyeCtrl.Core/Models/ActionIcon.cs`
- Modify: `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs`
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelPinNoteTests.cs:30-31` — asserts on the `Symbol` this task deletes, so the ViewModels test project stops compiling unless it is fixed in the same task. Replace both assertions with `Check.That(vm.Items[0].Icon).IsEqualTo(ActionIcon.PinNote);`

- [ ] **Step 1: Write the failing test**

Replace the whole contents of `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The action menu names a drawn icon per action. This file pinned Unicode glyph strings until
/// 2026-08-26; the glyphs are gone, but the property that mattered is kept verbatim — every
/// <see cref="InteractionAction"/> must map to a DISTINCT icon, and none may fall through to a
/// default.
///
/// The fall-through is the specific regression worth a test: the glyph switch ended in
/// <c>_ => action.ToString()</c>, so a dropped case rendered the enum name in the ring rather
/// than failing. An icon map has the same hazard in a different shape — a missing case throws at
/// the drawing layer instead, which for a user whose only input channel is an eye tracker means
/// an empty ring, not a wrong label.
/// </summary>
public class ActionMenuViewModelSymbolTests
{
    [Fact]
    public void Receive_ShowActionMenu_ScrollModeItemHasItsIcon()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);

        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500),
            new List<InteractionAction> { InteractionAction.ScrollMode },
            ShowBack: false,
            DwellTimeMs: 600,
            new PhysicalPoint(500, 500)));

        var item = vm.Items.Single();
        Check.That(item.Icon).IsEqualTo(ActionIcon.ScrollMode);
    }

    [Fact]
    public void EveryAction_MapsToADistinctIcon()
    {
        var expected = new Dictionary<InteractionAction, ActionIcon>
        {
            [InteractionAction.LeftClick] = ActionIcon.LeftClick,
            [InteractionAction.RightClick] = ActionIcon.RightClick,
            [InteractionAction.DoubleClick] = ActionIcon.DoubleClick,
            [InteractionAction.Drag] = ActionIcon.Drag,
            [InteractionAction.ScrollUp] = ActionIcon.ScrollUp,
            [InteractionAction.ScrollDown] = ActionIcon.ScrollDown,
            [InteractionAction.ScrollLeft] = ActionIcon.ScrollLeft,
            [InteractionAction.ScrollRight] = ActionIcon.ScrollRight,
            [InteractionAction.ScrollMode] = ActionIcon.ScrollMode,
            [InteractionAction.PinNote] = ActionIcon.PinNote,
        };

        // Fails if a new InteractionAction is added without an icon.
        var allActions = Enum.GetValues<InteractionAction>();
        Check.That(expected.Keys).IsEquivalentTo(allActions);

        // Two actions sharing an icon would render two identical targets.
        Check.That(expected.Values.Distinct().Count()).IsEqualTo(expected.Count);

        foreach (var action in allActions)
        {
            var messenger = new WeakReferenceMessenger();
            using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
            messenger.Send(new ShowActionMenuMessage(
                new PhysicalPoint(500, 500),
                new List<InteractionAction> { action },
                ShowBack: false,
                DwellTimeMs: 600,
                new PhysicalPoint(500, 500)));

            Check.WithCustomMessage($"{action} icon")
                 .That(vm.Items.Single().Icon).IsEqualTo(expected[action]);
        }
    }

    [Fact]
    public void TheBackItem_TakesTheBackIcon()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);

        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500),
            new List<InteractionAction> { InteractionAction.LeftClick },
            ShowBack: true,
            DwellTimeMs: 600,
            new PhysicalPoint(500, 500)));

        var back = vm.Items.Single(i => i.IsBackButton);
        Check.That(back.Icon).IsEqualTo(ActionIcon.Back);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelSymbolTests"
```

Expected: FAIL to **compile**, `CS0246: The type or namespace name 'ActionIcon' could not be found`.

- [ ] **Step 3: Add the enum to Core**

Create `src/EyeCtrl.Core/Models/ActionIcon.cs`:

```csharp
namespace EyeCtrl.Core.Models;

/// <summary>
/// Names one drawn icon in the action menu's set. A name only — the path data lives in
/// <c>EyeCtrl.Controls.Icons.ActionIcons</c>, and the mapping from an
/// <see cref="InteractionAction"/> lives in <c>ActionMenuViewModel</c>.
///
/// **Why the name is in Core rather than beside the geometry.** EyeCtrl.ViewModels must be able
/// to say WHICH icon a menu item wants without referencing WPF, and <c>EdgeButtonDefinition</c>
/// needs the same. A ViewModels reference to EyeCtrl.Controls would pull System.Windows.Media
/// into a project whose tests deliberately run without a WPF Application.
///
/// Members do NOT correspond one-to-one with <see cref="InteractionAction"/>: <see cref="Back"/>
/// is a navigation affordance rather than an action, and this enum will grow entries for the
/// More layer and the Help toggle that no InteractionAction names.
/// </summary>
public enum ActionIcon
{
    /// <summary>Mouse with its left button filled.</summary>
    LeftClick,
    /// <summary>Mouse with its right button filled.</summary>
    RightClick,
    /// <summary>Left button filled, two click ripples off the upper-left, and a "2" at the right margin.</summary>
    DoubleClick,
    /// <summary>Left button filled, with motion bars trailing at the left.</summary>
    Drag,
    /// <summary>Mouse with a wheel and an up chevron at the right margin.</summary>
    ScrollUp,
    /// <summary>Mouse with a wheel and a down chevron at the right margin.</summary>
    ScrollDown,
    /// <summary>Mouse with a wheel and a left-pointing chevron at the right margin.</summary>
    ScrollLeft,
    /// <summary>Mouse with a wheel and a right-pointing chevron at the right margin.</summary>
    ScrollRight,
    /// <summary>Mouse with a wheel and both up and down chevrons — the gaze-joystick scroll mode.</summary>
    ScrollMode,
    /// <summary>A sticky note with a pin through its top edge.</summary>
    PinNote,
    /// <summary>A return arrow. Navigation, not an action.</summary>
    Back,
}
```

- [ ] **Step 4: Carry the icon on the item ViewModel**

Replace `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs` in full:

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class ActionMenuItemViewModel : ObservableObject
{
    public string Id { get; }
    public InteractionAction? Action { get; }

    /// <summary>
    /// Which drawn icon this item renders. Replaced a <c>Symbol</c> string of Unicode glyphs on
    /// 2026-08-26: those rendered in whatever font Windows fell back to, and one of them (📌) was
    /// a colour emoji that ignored the theme entirely.
    /// </summary>
    public ActionIcon Icon { get; }

    public PhysicalPoint Position { get; }
    public bool IsBackButton { get; }

    [ObservableProperty] private double _dwellProgress;

    public ActionMenuItemViewModel(string id, InteractionAction? action, ActionIcon icon, PhysicalPoint position, bool isBackButton = false)
    {
        Id = id;
        Action = action;
        Icon = icon;
        Position = position;
        IsBackButton = isBackButton;
    }
}
```

- [ ] **Step 5: Map action to icon in the ViewModel**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, replace the `GetActionSymbol` method at the bottom of the class:

```csharp
    /// <summary>
    /// The single source of truth for which icon an action shows. There is deliberately no
    /// default arm: a new <see cref="InteractionAction"/> must be given an icon here, and
    /// <c>ActionMenuViewModelSymbolTests.EveryAction_MapsToADistinctIcon</c> fails until it is.
    /// The glyph version this replaced ended in <c>_ => action.ToString()</c>, which silently
    /// rendered enum names in the ring when a case was dropped.
    /// </summary>
    private static ActionIcon GetActionIcon(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => ActionIcon.LeftClick,
        InteractionAction.RightClick => ActionIcon.RightClick,
        InteractionAction.DoubleClick => ActionIcon.DoubleClick,
        InteractionAction.Drag => ActionIcon.Drag,
        InteractionAction.ScrollUp => ActionIcon.ScrollUp,
        InteractionAction.ScrollDown => ActionIcon.ScrollDown,
        InteractionAction.ScrollLeft => ActionIcon.ScrollLeft,
        InteractionAction.ScrollRight => ActionIcon.ScrollRight,
        InteractionAction.ScrollMode => ActionIcon.ScrollMode,
        InteractionAction.PinNote => ActionIcon.PinNote,
        _ => throw new ArgumentOutOfRangeException(
            nameof(action), action, "No ActionIcon is mapped for this InteractionAction."),
    };
```

Then in `Receive(ShowActionMenuMessage)`, change the tuple list's element type and both `Add` calls (currently around lines 46-50):

```csharp
        var allItems = new List<(string Id, InteractionAction? Action, ActionIcon Icon, bool IsBack)>();
        foreach (var action in message.Actions)
            allItems.Add((action.ToString(), action, GetActionIcon(action), false));
        if (message.ShowBack)
            allItems.Add(("Back", null, ActionIcon.Back, true));
```

and the destructuring plus construction inside the layout loop (currently around lines 59-66):

```csharp
            var (id, action, icon, isBack) = allItems[i];
```
```csharp
            var itemVm = new ActionMenuItemViewModel(id, action, icon, position, isBack);
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelSymbolTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 7: Fix the one consumer this breaks**

`src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs:187` passes `itemVm.Symbol`, which no longer exists. Change it to render nothing for now — Task 4 gives it the real icon:

```csharp
                var btn = DwellArcButton.Create(canvasPoint, ActionMenuGeometry.ButtonRadius, symbol: null);
```

The ring renders as empty circles between this task and Task 4. That is deliberate and visible, not a silent gap; do not ship a branch stopped here.

- [ ] **Step 8: Run the full gate**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.Core/Models/ActionIcon.cs src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs src/EyeCtrl.ViewModels/ActionMenuViewModel.cs src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs
git commit -m "refactor(actionmenu): name a drawn icon per action instead of a Unicode glyph

The ring renders empty until the geometry lands two commits from now.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Draw the icons

**Files:**
- Create: `src/EyeCtrl.Controls/Icons/IconGeometry.cs`
- Create: `src/EyeCtrl.Controls/Icons/ActionIcons.cs`
- Test: `tests/EyeCtrl.Controls.Tests/Icons/ActionIconsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Icons/ActionIconsTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Icons;

using System;
using System.Linq;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// Pins the icon registry's completeness and its grid discipline.
///
/// The bounds check is the one that earns its place. Path data is hand-authored, and a
/// transposed digit produces a shape that still parses, still renders, and sits half outside the
/// button — a defect with no exception and no visual cue beyond "that icon looks wrong", on a
/// surface the user cannot inspect closely without spending dwell on it.
/// </summary>
public class ActionIconsTests
{
    [Fact]
    public void EveryActionIcon_HasGeometry()
    {
        foreach (var icon in Enum.GetValues<ActionIcon>())
        {
            var geometry = ActionIcons.For(icon);

            Check.WithCustomMessage($"{icon} resolved to null").That(geometry).IsNotNull();
            Check.WithCustomMessage($"{icon} has neither a stroke nor a fill layer")
                 .That(geometry.Stroke is not null || geometry.Fill is not null).IsTrue();
        }
    }

    [Fact]
    public void EveryIcon_FitsTheGrid()
    {
        // Half the stroke sits outside the path's own bounds, so the usable box is inset by it.
        var slack = ActionIcons.GridStrokeThickness / 2.0;

        foreach (var icon in Enum.GetValues<ActionIcon>())
        {
            var geometry = ActionIcons.For(icon);

            foreach (var layer in new[] { geometry.Stroke, geometry.Fill }.Where(g => g is not null))
            {
                var b = layer!.Bounds;
                var msg = $"{icon} bounds {b} escape the {ActionIcons.GridSize}-unit grid";

                Check.WithCustomMessage(msg).That(b.Left).IsStrictlyGreaterThan(-slack);
                Check.WithCustomMessage(msg).That(b.Top).IsStrictlyGreaterThan(-slack);
                Check.WithCustomMessage(msg).That(b.Right).IsStrictlyLessThan(ActionIcons.GridSize + slack);
                Check.WithCustomMessage(msg).That(b.Bottom).IsStrictlyLessThan(ActionIcons.GridSize + slack);
            }
        }
    }

    [Fact]
    public void EveryGeometry_IsFrozen()
    {
        // The registry is a static shared across every button on screen. An unfrozen Geometry is
        // mutable and thread-affine; frozen ones are neither.
        foreach (var icon in Enum.GetValues<ActionIcon>())
        {
            var geometry = ActionIcons.For(icon);

            if (geometry.Stroke is not null)
                Check.WithCustomMessage($"{icon} stroke is not frozen").That(geometry.Stroke.IsFrozen).IsTrue();
            if (geometry.Fill is not null)
                Check.WithCustomMessage($"{icon} fill is not frozen").That(geometry.Fill.IsFrozen).IsTrue();
        }
    }

    [Fact]
    public void LeftAndRightClick_AreNotTheSameShape()
    {
        // They differ only in which half of the mouse is filled, and a copy-paste slip that left
        // both filling the left would produce two identical targets in the ring.
        var left = ActionIcons.For(ActionIcon.LeftClick);
        var right = ActionIcons.For(ActionIcon.RightClick);

        Check.That(left.Fill!.Bounds).IsNotEqualTo(right.Fill!.Bounds);
    }

    [Fact]
    public void DoubleClick_BreaksTheMouseSilhouette()
    {
        // With labels off, double click must differ from left click in OUTLINE, not in detail:
        // a mark inside the same silhouette is invisible in the periphery, which is where a gaze
        // user finds a target before saccading to it. The ripples reach left of the mouse body
        // (x=7) and the numeral reaches right of it (x=17).
        var single = ActionIcons.For(ActionIcon.LeftClick).Stroke!.Bounds;
        var doubled = ActionIcons.For(ActionIcon.DoubleClick).Stroke!.Bounds;

        Check.WithCustomMessage("DoubleClick does not extend left of LeftClick")
             .That(doubled.Left).IsStrictlyLessThan(single.Left);
        Check.WithCustomMessage("DoubleClick does not extend right of LeftClick")
             .That(doubled.Right).IsStrictlyGreaterThan(single.Right);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ActionIconsTests"
```

Expected: FAIL to **compile**, `CS0246: The type or namespace name 'ActionIcons' could not be found`.

- [ ] **Step 3: Add the icon record**

Create `src/EyeCtrl.Controls/Icons/IconGeometry.cs`:

```csharp
namespace EyeCtrl.Controls.Icons;

using System.Windows.Media;

/// <summary>
/// One icon's two layers. Both are optional but at least one is always present: most icons are
/// stroke-only, a few (More) are fill-only, and the click icons are both.
///
/// Two layers rather than one because WPF applies a single <c>StrokeThickness</c> and a single
/// <c>Fill</c> per <c>Path</c>. The mouse body is an outline and the pressed button is a solid,
/// so they cannot share one Path without the fill swallowing the outline.
/// </summary>
/// <param name="Stroke">Outlined paths, drawn at <see cref="ActionIcons.GridStrokeThickness"/>.</param>
/// <param name="Fill">Solid paths, drawn with no stroke.</param>
public sealed record IconGeometry(Geometry? Stroke, Geometry? Fill);
```

- [ ] **Step 4: Add the registry**

Create `src/EyeCtrl.Controls/Icons/ActionIcons.cs`:

```csharp
namespace EyeCtrl.Controls.Icons;

using System.Collections.Generic;
using System.Windows.Media;
using EyeCtrl.Core.Models;

/// <summary>
/// The action menu's drawn icon set: <see cref="ActionIcon"/> to WPF <see cref="Geometry"/>.
///
/// **Everything is authored on a <see cref="GridSize"/>-unit square at
/// <see cref="GridStrokeThickness"/> stroke,** and scaled to the target by the caller's
/// <c>RenderTransform</c>. RenderTransform scales the stroke along with the geometry;
/// <c>Geometry.Transform</c> would not, and would leave a 1.8px hairline on a 63px icon.
///
/// **Every action is a mouse action, so every icon is a mouse.** Modifiers sit in the margins
/// and never on the body: scroll variants take chevrons at the right, drag takes motion bars at
/// the left. <see cref="ActionIcon.DoubleClick"/> is the deliberate exception — it must differ
/// from <see cref="ActionIcon.LeftClick"/> in outline rather than in detail, because with labels
/// hidden a mark inside the same silhouette is invisible in peripheral vision, which is where a
/// gaze user finds a target before looking straight at it.
///
/// Geometries are parsed once, frozen, and shared. Frozen is not an optimisation here: these are
/// statics handed to every button on screen, and an unfrozen Geometry is both mutable and
/// thread-affine.
/// </summary>
public static class ActionIcons
{
    /// <summary>The square every path in this file is authored on, in grid units.</summary>
    public const double GridSize = 24.0;

    /// <summary>Stroke width in grid units, before the caller's scale transform.</summary>
    public const double GridStrokeThickness = 1.8;

    /// <summary>The mouse outline every action icon is built from: a 10 x 19 stadium at x=7, y=2.5.</summary>
    private const string Body = "M7,7.5 A5,5 0 0 1 17,7.5 L17,16.5 A5,5 0 0 1 7,16.5 Z";

    /// <summary>The button divider and separator: what makes the outline read as a mouse, not a pill.</summary>
    private const string Buttons = " M12,2.5 L12,10.5 M7,10.5 L17,10.5";

    /// <summary>A short wheel stroke, used instead of <see cref="Buttons"/> on the scroll icons.</summary>
    private const string Wheel = " M12,6 L12,9.5";

    private const string LeftButtonFill = "M12,2.5 A5,5 0 0 0 7,7.5 L7,10.5 L12,10.5 Z";
    private const string RightButtonFill = "M12,2.5 A5,5 0 0 1 17,7.5 L17,10.5 L12,10.5 Z";

    private static readonly Dictionary<ActionIcon, IconGeometry> Map = Build();

    /// <summary>The geometry for <paramref name="icon"/>. Throws if the registry has no entry.</summary>
    public static IconGeometry For(ActionIcon icon) => Map[icon];

    private static Geometry? Parse(string? data)
    {
        if (data is null) return null;
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static IconGeometry Make(string? stroke, string? fill) => new(Parse(stroke), Parse(fill));

    private static Dictionary<ActionIcon, IconGeometry> Build() => new()
    {
        [ActionIcon.LeftClick] = Make(Body + Buttons, LeftButtonFill),

        [ActionIcon.RightClick] = Make(Body + Buttons, RightButtonFill),

        // Two click ripples concentric on (10, 7.5) reaching left of the body, and a "2" drawn
        // in the right margin. Together they change the outline; the badge they replaced sat
        // inside it and was invisible at a glance.
        [ActionIcon.DoubleClick] = Make(
            Body + Buttons
            + " M4.88,8.4 A5.2,5.2 0 0 1 8.05,2.68"
            + " M2.67,8.53 A7.4,7.4 0 0 1 6.3,1.09"
            + " M18.5,16.3 C18.5,14.2 22.9,14.2 22.9,16.6 C22.9,18.2 18.5,19.4 18.5,21.2 L23,21.2",
            LeftButtonFill),

        // Held button plus motion bars trailing at the left.
        [ActionIcon.Drag] = Make(
            Body + Buttons + " M4.4,8.6 L4.4,15.4 M1.6,10.6 L1.6,13.4",
            LeftButtonFill),

        [ActionIcon.ScrollUp] = Make(Body + Wheel + " M18.6,12.4 L20.8,10 L23,12.4", null),

        [ActionIcon.ScrollDown] = Make(Body + Wheel + " M18.6,11.6 L20.8,14 L23,11.6", null),

        [ActionIcon.ScrollLeft] = Make(Body + Wheel + " M22,9.4 L19.6,11.8 L22,14.2", null),

        [ActionIcon.ScrollRight] = Make(Body + Wheel + " M19.6,9.4 L22,11.8 L19.6,14.2", null),

        // Both chevrons: the gaze-joystick mode that scrolls either way.
        [ActionIcon.ScrollMode] = Make(
            Body + Wheel + " M18.6,9.4 L20.8,7 L23,9.4 M18.6,14.6 L20.8,17 L23,14.6", null),

        // A note with two text lines, and a pin whose head is the only filled part.
        [ActionIcon.PinNote] = Make(
            "M5,10 L5,19 A2.5,2.5 0 0 0 7.5,21.5 L16.5,21.5 A2.5,2.5 0 0 0 19,19 L19,10"
            + " A2.5,2.5 0 0 0 16.5,7.5 L7.5,7.5 A2.5,2.5 0 0 0 5,10 Z"
            + " M8.6,12.5 L15.4,12.5 M8.6,16.5 L13,16.5 M12,5.8 L12,7.5",
            "M9.8,3.6 A2.2,2.2 0 1 1 14.2,3.6 A2.2,2.2 0 1 1 9.8,3.6 Z"),

        [ActionIcon.Back] = Make(
            "M10,5.5 L4.5,11 L10,16.5 M4.5,11 L14.5,11 A5,5 0 0 1 19.5,16 L19.5,19", null),
    };
}
```

- [ ] **Step 5: Add the Core project reference if EyeCtrl.Controls lacks one**

Check:

```bash
grep -n "ProjectReference" src/EyeCtrl.Controls/EyeCtrl.Controls.csproj
```

If `EyeCtrl.Core` is absent, add inside an `<ItemGroup>`:

```xml
    <ProjectReference Include="..\EyeCtrl.Core\EyeCtrl.Core.csproj" />
```

Controls depends on Core, never the reverse — Core has no WPF and must keep none.

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ActionIconsTests"
```

Expected: PASS, 5 tests.

If `EveryIcon_FitsTheGrid` fails, read the reported bounds against the offending icon's path data and correct the coordinate — do **not** widen the slack. The grid is what guarantees every icon occupies the same optical square.

- [ ] **Step 7: Run the full gate**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Controls/Icons/ src/EyeCtrl.Controls/EyeCtrl.Controls.csproj tests/EyeCtrl.Controls.Tests/Icons/
git commit -m "feat(controls): add the action-menu drawn icon set

Ten action icons plus Back, authored on a 24-unit grid at 1.8
stroke. DoubleClick deliberately breaks the mouse silhouette rather than
marking inside it — with labels hidden, a mark inside the outline is invisible
in peripheral vision.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Teach DwellArcButton to render an icon

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellArcButton.cs`
- Test: `tests/EyeCtrl.Controls.Tests/DwellArcButtonIconTests.cs` (create)
- Modify: `tests/EyeCtrl.App.Tests/ContainerRatioCensusTests.cs:106-113`
- Modify: `tests/EyeCtrl.Controls.Tests/DwellArcButtonGlyphSizeTests.cs:38`

This is the HIGH-risk symbol. Every change is additive.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/DwellArcButtonIconTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests;

using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// Covers the icon path added to <c>DwellArcButton.Create</c> on 2026-08-26.
///
/// <c>impact({target: "DwellArcButton", direction: "upstream"})</c> reports HIGH risk across two
/// execution flows and four callers, only one of which is the action menu. The first test here
/// is therefore the important one: it pins that the glyph path is untouched, which is what lets
/// the other three callers go unedited.
///
/// <c>[StaFact]</c> because Create builds WPF shapes. No window is created and nothing is shown.
/// </summary>
public class DwellArcButtonIconTests
{
    private const double ActionMenuRadius = 70;

    [StaFact]
    public void WithNoIcon_TheGlyphPathIsUnchanged()
    {
        var button = DwellArcButton.Create(new Point(0, 0), 50, "✕");

        Check.WithCustomMessage("The glyph caller lost its TextBlock.")
             .That(button.Symbol).IsNotNull();
        Check.WithCustomMessage("A glyph caller must not gain icon paths.")
             .That(button.IconStroke).IsNull();
        Check.That(button.IconFill).IsNull();
    }

    [StaFact]
    public void WithAnIcon_BothLayersAreBuilt()
    {
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));

        Check.WithCustomMessage("LeftClick has a stroke layer.").That(button.IconStroke).IsNotNull();
        Check.WithCustomMessage("LeftClick has a fill layer.").That(button.IconFill).IsNotNull();
        Check.WithCustomMessage("An icon caller must not also get a TextBlock.")
             .That(button.Symbol).IsNull();
    }

    [StaFact]
    public void AFillOnlyIcon_BuildsOnlyTheFillLayer()
    {
        // Back is stroke-only; this asserts the null layer is skipped rather than rendered empty.
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.Back));

        Check.That(button.IconStroke).IsNotNull();
        Check.That(button.IconFill).IsNull();
    }

    [StaFact]
    public void TheIcon_TakesContainerRatioDominant()
    {
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));

        var dia = ActionMenuRadius * 2;
        var expectedScale = dia * ContainerRatio.Dominant / ActionIcons.GridSize;
        var transform = (ScaleTransform)button.IconStroke!.RenderTransform;

        Check.WithCustomMessage(
                $"A {dia}px button must draw its icon at {dia * ContainerRatio.Dominant}px — "
                + "ContainerRatio.Dominant is the token for an affordance whose entire content is "
                + "one glyph with no label beside it, which is the resting action-menu button.")
            .That(transform.ScaleX).IsCloseTo(expectedScale, 1e-9);
        Check.That(transform.ScaleY).IsCloseTo(expectedScale, 1e-9);
    }

    [StaFact]
    public void TheIcon_IsCentredOnTheButton()
    {
        var centre = new Point(300, 200);
        var button = DwellArcButton.Create(
            new Point(300, 200), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));

        var iconSize = ActionMenuRadius * 2 * ContainerRatio.Dominant;

        Check.That(System.Windows.Controls.Canvas.GetLeft(button.IconStroke!))
             .IsCloseTo(centre.X - iconSize / 2, 1e-9);
        Check.That(System.Windows.Controls.Canvas.GetTop(button.IconStroke!))
             .IsCloseTo(centre.Y - iconSize / 2, 1e-9);
    }

    [StaFact]
    public void TheIcon_TakesItsColourFromTheTheme()
    {
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));

        // The glyphs this replaced included one colour emoji that ignored the palette entirely.
        Check.That(button.IconStroke!.Stroke).IsSameReferenceAs(Tokens.BrushTextPrimary);
        Check.That(button.IconFill!.Fill).IsSameReferenceAs(Tokens.BrushTextPrimary);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~DwellArcButtonIconTests"
```

Expected: FAIL to **compile**, `CS1739: The best overload for 'Create' does not have a parameter named 'icon'`.

- [ ] **Step 3: Add the icon parameter and the two Paths**

In `src/EyeCtrl.Controls/DwellArcButton.cs`, add these using directives if absent:

```csharp
using EyeCtrl.Controls.Icons;
```

Add two properties beside the existing `Symbol` property:

```csharp
    /// <summary>The icon's outlined layer, or null when this button renders a glyph or nothing.</summary>
    public Path? IconStroke { get; }

    /// <summary>The icon's solid layer, or null when the icon has no filled paths.</summary>
    public Path? IconFill { get; }
```

Add both to the private constructor's parameter list and assignments, after `symbol`:

```csharp
    private DwellArcButton(Ellipse background, Path haloPath, Path progressPath, ArcSegment haloArc, ArcSegment arc, TextBlock? symbol, Path? iconStroke, Path? iconFill, Point center, double arcRadius)
    {
        Background = background;
        HaloPath = haloPath;
        ProgressPath = progressPath;
        HaloArc = haloArc;
        Arc = arc;
        Symbol = symbol;
        IconStroke = iconStroke;
        IconFill = iconFill;
        _center = center;
        _arcRadius = arcRadius;
    }
```

Add the parameter to `Create`, last so every existing positional call is unaffected:

```csharp
        double? fontSize = null,
        IconGeometry? icon = null)
```

Immediately after the existing `if (symbol is not null) { … }` block that builds `symbolText`, add:

```csharp
        // Drawn icon. Sized by ContainerRatio.Dominant — the token for an affordance whose
        // entire content is one glyph with no label beside it. The scale goes on RenderTransform
        // rather than Geometry.Transform on purpose: RenderTransform scales the stroke with the
        // path, so GridStrokeThickness stays 1.8 at every size. Geometry.Transform would leave a
        // 1.8px hairline on a 63px icon.
        Path? iconStrokePath = null;
        Path? iconFillPath = null;
        if (icon is not null)
        {
            var iconSize = dia * ContainerRatio.Dominant;
            var scale = iconSize / ActionIcons.GridSize;

            Path BuildLayer(Geometry geometry, bool filled)
            {
                var path = new Path
                {
                    Data = geometry,
                    Stroke = filled ? null : Tokens.BrushTextPrimary,
                    StrokeThickness = filled ? 0 : ActionIcons.GridStrokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Fill = filled ? Tokens.BrushTextPrimary : null,
                    RenderTransform = new ScaleTransform(scale, scale),
                };
                Canvas.SetLeft(path, center.X - iconSize / 2);
                Canvas.SetTop(path, center.Y - iconSize / 2);
                return path;
            }

            if (icon.Stroke is not null) iconStrokePath = BuildLayer(icon.Stroke, filled: false);
            if (icon.Fill is not null) iconFillPath = BuildLayer(icon.Fill, filled: true);
        }
```

Update the return:

```csharp
        return new DwellArcButton(bg, haloPath, progressPath, haloSegment, arcSegment, symbolText, iconStrokePath, iconFillPath, center, arcRad);
```

And in `AddTo`, add the icon layers after the progress path so they sit above the arc, stroke last so the outline reads over the fill:

```csharp
    public void AddTo(Canvas canvas)
    {
        canvas.Children.Add(Background);
        canvas.Children.Add(HaloPath);      // must precede ProgressPath
        canvas.Children.Add(ProgressPath);
        if (Symbol is not null)
            canvas.Children.Add(Symbol);
        if (IconFill is not null)
            canvas.Children.Add(IconFill);  // must precede IconStroke
        if (IconStroke is not null)
            canvas.Children.Add(IconStroke);
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~DwellArcButtonIconTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Update the ratio census**

`DwellArcButton.cs` now references a third ratio. In `tests/EyeCtrl.App.Tests/ContainerRatioCensusTests.cs`, replace the `DwellArcButton.cs` entry (lines 106-113) with:

```csharp
            ["src/EyeCtrl.Controls/DwellArcButton.cs"] =
            [
                ("Badge", 1, "diameters under BadgeDiameterThreshold — the edge bar's 40px "
                    + "drag/back buttons, where Small would give 9.2px, below the accessibility "
                    + "floor, on the affordance that CANCELS A DRAG."),
                ("Small", 1, "diameters at or above the threshold — the 100px hide button, "
                    + "whose 22/100 is where Small was observed. No longer the action menu: it "
                    + "took Small while it rendered a Unicode glyph, and now draws an icon."),
                ("Dominant", 1, "the drawn icon on a 140px action-menu button, whose entire "
                    + "content is one shape with no label beside it — the situation Dominant "
                    + "names. 140 x 0.45 = 63px."),
            ],
```

- [ ] **Step 6: Fix the doc comment plan 1 made stale**

`tests/EyeCtrl.Controls.Tests/DwellArcButtonGlyphSizeTests.cs:38` still says the 50 radius is both `UIConstants.ButtonRadius` and `ActionMenuGeometry.ButtonRadius`. Plan 1 split those; the action menu is 70. Replace that summary line:

```csharp
    /// <summary>
    /// UIConstants.ButtonRadius — the backdrop hide button. This was also
    /// ActionMenuGeometry.ButtonRadius until the two constants were split and the action-menu
    /// button grew to 70; the action menu no longer renders a glyph at all, so this test's
    /// large-caller arm now speaks only for the hide button.
    /// </summary>
    private const double LargeCallerRadius = 50;
```

- [ ] **Step 7: Run the full gate**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS.

**Nothing in the repo guards the icon layers' z-order.** `HaloZOrderTests` inspects `x:Name` declaration order in seven XAML files and names no `.cs` file at all; `HaloContrastTests` is theme colour arithmetic with zero references to `DwellArcButton`. The only existing child guard is `DwellArcButtonTests.AddTo_AddsAllVisualsToCanvas`, which pins a count of 4 for a glyph button and passes at any icon ordering.

Icon z-order does matter — a filled layer painted over its own outline erases it — so pin it here rather than relying on a guard that does not exist. Add to `DwellArcButtonIconTests`:

```csharp
    [StaFact]
    public void TheFillLayer_IsAddedBeforeTheStrokeLayer()
    {
        var canvas = new System.Windows.Controls.Canvas();
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));
        button.AddTo(canvas);

        Check.That(canvas.Children.IndexOf(button.IconFill))
             .IsStrictlyLessThan(canvas.Children.IndexOf(button.IconStroke));
    }
```

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Controls/DwellArcButton.cs tests/EyeCtrl.Controls.Tests/DwellArcButtonIconTests.cs tests/EyeCtrl.Controls.Tests/DwellArcButtonGlyphSizeTests.cs tests/EyeCtrl.App.Tests/ContainerRatioCensusTests.cs
git commit -m "feat(controls): let DwellArcButton render a drawn icon

Additive: the glyph path is untouched, so the edge bar, the hide button and the
drag buttons are not edited and do not move.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Put the icons in the ring

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs:187`
- Test: `tests/EyeCtrl.App.Tests/Overlays/ActionMenuIconRenderTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Overlays/ActionMenuIconRenderTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Overlays;

using System.Linq;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// The ring's icons come from the registry, not from a second mapping inside the window.
///
/// This is a source-level check rather than a rendering one: constructing ActionMenuWindow needs
/// a WPF Application and a screen capture, and this suite may not do that on a machine whose only
/// input channel is an eye tracker. What it can prove is that the window resolves icons through
/// ActionIcons.For and does not reintroduce a glyph string, which is the regression that would
/// otherwise be invisible until someone looked at the ring.
/// </summary>
public class ActionMenuIconRenderTests
{
    private const string WindowPath = "src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs";

    [Fact]
    public void TheWindow_ResolvesIconsThroughTheRegistry()
    {
        var source = ReadSource();

        Check.WithCustomMessage($"{WindowPath} no longer calls ActionIcons.For")
             .That(source).Contains("ActionIcons.For(");
    }

    [Fact]
    public void TheWindow_PassesNoGlyphString()
    {
        var source = ReadSource();

        Check.WithCustomMessage(
                $"{WindowPath} passes a symbol to DwellArcButton.Create. The action menu renders "
                + "drawn icons; a glyph here would render on top of one.")
            .That(source).Not.Contains("itemVm.Symbol");
    }

    [Fact]
    public void EveryIconTheRingCanAsForFor_Resolves()
    {
        // Belt and braces against a registry entry being deleted: the ring can request an icon for
        // any InteractionAction plus Back.
        foreach (var icon in System.Enum.GetValues<ActionIcon>())
            Check.WithCustomMessage($"{icon} does not resolve").That(ActionIcons.For(icon)).IsNotNull();
    }
}
```

`SourceScanner` exposes `ScannedFiles()` (an `IEnumerable<(string Relative, string Text)>`) and `StripComments(string)` — there is **no** `ReadWithoutComments`. Add this private helper to the test class:

```csharp
    private static string ReadSource() =>
        SourceScanner.StripComments(
            SourceScanner.ScannedFiles().Single(f => f.Relative == WindowPath).Text);
```

Add `using System.Linq;`. `ContainerRatioCensusTests.cs:121` is the call shape to copy.

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ActionMenuIconRenderTests"
```

Expected: FAIL — `TheWindow_ResolvesIconsThroughTheRegistry` reports the source does not contain `ActionIcons.For(`. (`TheWindow_PassesNoGlyphString` already passes: Task 1 Step 7 removed `itemVm.Symbol`.)

- [ ] **Step 3: Pass the icon**

In `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`, add the using directive:

```csharp
using EyeCtrl.Controls.Icons;
```

and replace line 187:

```csharp
                var btn = DwellArcButton.Create(
                    canvasPoint,
                    ActionMenuGeometry.ButtonRadius,
                    symbol: null,
                    icon: ActionIcons.For(itemVm.Icon));
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ActionMenuIconRenderTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Run the full gate**

```bash
dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs tests/EyeCtrl.App.Tests/Overlays/ActionMenuIconRenderTests.cs
git commit -m "feat(actionmenu): render drawn icons in the ring

The glyph set is gone from the action menu: no font fallback, no colour emoji,
and the icons recolour with the theme and scale with the target.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Confirm the affected scope, and look at it once

**Files:** none modified.

- [ ] **Step 1: Run change detection against the default branch**

```
detect_changes({scope: "compare", base_ref: "main"})
```

Expected affected symbols: `ActionIcon`, `ActionIcons`, `IconGeometry`, `ActionMenuItemViewModel`, `ActionMenuViewModel.Receive`, `DwellArcButton.Create`, `DwellArcButton.AddTo`, `ActionMenuWindow.Show`, plus the test classes.

**Any hit naming `EdgeButtonBarWindow`, `GlassBackdropWindow`, `ToolbarWindow` or `DragTrailOverlay` means Task 3 was not additive after all.** Those callers must be untouched. Stop and re-read `DwellArcButton.Create`'s signature — the `icon` parameter has to be last and optional.

- [ ] **Step 2: Ask the user before looking at the real app**

The icons have been proved to parse, fit the grid, and reach the ring. Nothing so far has proved they *look* right, and only running the app can.

**Do not launch EyeCtrl without an explicit go-ahead.** Input simulation and UI automation seize this user's only input channel. Ask, wait for a clear yes, and keep any such run short.

If they decline, say plainly that the icon shapes are unverified visually and stop — that is an honest state to hand over, and a far better one than a run they did not agree to.

- [ ] **Step 3: If and only if they agreed — look at the ring**

Open the action menu once, confirm the ten icons read as distinct at 63 DIP, and close it. The two things worth checking, because no test can:

- **Double click against left click.** They share a mouse body; the ripples and the "2" are what separate them. If they read as the same target at a glance, the icon needs more outline difference, not a bigger badge.
- **The `2` numeral**, which is drawn as a cubic curve rather than text. Hand-drawn numerals go wrong in ways bounds checks do not catch.

- [ ] **Step 4: Commit nothing**

This task produces no diff.

---

## What this plan deliberately does not do

- **No labels.** The ring is icons only. Names arrive in plan 3, behind the Help target on the right-edge bar, and that is when the icon drops from 63 to 44 DIP to make room.
- **No More layer.** All of `VisibleActions` still rings the cursor, so the small-`CircleSize` overlap plan 1 documented is still present below `CircleSize` 106.
- **No glyph removal anywhere else.** `ToolbarWindow`, `EdgeButtonBarWindow`, `GlassBackdropWindow` and the keyboard keep their Unicode symbols. Converting those is a separate piece of work with its own blast radius, and nothing in the design canvas asked for it.
- **`ActionIcon.ScrollLeft` / `ScrollRight` get icons they will not show.** Both ship OFF in `ActionMenuSettings.VisibleActions`, but `ActionMenuPageSource` offers a toggle for every `InteractionAction`, so a user can turn them on. An action reachable from settings with no icon would throw at the drawing layer.

## Self-review

**Spec coverage:** the canvas's icon-sheet artboard specifies drawn stroke icons on a 24 grid replacing the glyph set (Tasks 1, 2, 4), the mouse-plus-margin-modifier grammar (Task 2's path data and its doc comment), the `ContainerRatio.Dominant` sizing of the label-less icon (Task 3), and double click differing in outline rather than detail (Task 2's `DoubleClick_BreaksTheMouseSilhouette`).

**Placeholders:** none — every step carries literal code or a literal command.

**Type consistency:** `IconGeometry(Geometry? Stroke, Geometry? Fill)` is declared in Task 2 Step 3 and consumed in Task 3 Step 3 with those exact member names. `ActionIcons.For`, `ActionIcons.GridSize` and `ActionIcons.GridStrokeThickness` are declared in Task 2 Step 4 before their first use in Task 3's test. `ActionIcon` is declared in Task 1 Step 3 before Task 2 references it. `DwellArcButton.IconStroke` / `IconFill` are introduced in Task 3 Step 3 and used by that task's tests only. `ActionMenuItemViewModel.Icon` is introduced in Task 1 Step 4 and read in Task 4 Step 3.

**One ordering hazard, called out where it bites:** Task 1 leaves the ring rendering empty circles until Task 4. That is stated in Task 1 Step 7 rather than left to be discovered.
