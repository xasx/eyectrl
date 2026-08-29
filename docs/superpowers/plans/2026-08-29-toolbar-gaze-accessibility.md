# Toolbar Gaze-Accessibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every main-toolbar button hittable everywhere it is drawn, legible without firing it, and honest about what it is doing — for a user whose only input channel is gaze dwell.

**Architecture:** The toolbar stops hand-rolling its buttons and adopts `DwellArcButton`, the control the action menu and edge bar already use. That single swap delivers most of the design: an `Ellipse` face (so the drawn shape *is* the circular dwell target), a `BrushBorderSubtle` rim with a halo, a sweeping progress arc, and drawn `IconGeometry` icons instead of Unicode glyphs. The strip's `StackPanel` becomes a `Canvas` with computed button centres, because that is how `DwellArcButton` positions itself. The provider indicator moves out of the target band entirely.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (`[ObservableProperty]`), NFluent 3.1.0 + xUnit v3, `Xunit.StaFact` for WPF-constructing tests.

**Design:** `docs/design/toolbar-accessibility/` (working artboards) and the published canvas at <https://claude.ai/code/artifact/8ea35a4b-8bc4-4a80-9b39-c5ec242a8672>. Read `Main.dc.html` and `Anatomy.dc.html` first; this plan does not repeat their rationale.

**Revision:** Rewritten 2026-08-29 after source-verified review. The first draft hand-rolled a disc `Border`, a second icon system, and `DwellOutlineOverlay` wiring — reinventing `DwellArcButton`, `ActionIcons` and `IconGeometry`. It also assigned `Tokens.CircleCornerRadius` from code-behind, which `CornerRadiusClampTests` refuses outright (`tests/EyeCtrl.App.Tests/CornerRadiusClampTests.cs:213-226`). Building on `DwellArcButton` removes both problems: it draws an `Ellipse`, so no corner-radius token is involved at all.

---

## Scope

**In scope — the "Proposed" and "Anatomy" artboards (canvas page 1).**

**Out of scope — the three "Rethink" artboards, each its own plan:** `StableSlots` (fixed slot band / 3×2 cluster), `ModePicker` (open-then-pick selection mode), `RiskTier` (the Reset confirm step). The latter two add and remove dwell targets at runtime, which is the hazard `ToolbarViewModel`'s lazy-target comments were written about.

**Known gap this plan leaves open.** Task 7 gives Reset a warning rim and extra clearance, which reduces *overshoot*. It does not make an accidental activation recoverable — that is the confirm step in the `RiskTier` follow-up. Do not describe Reset as "protected" until that ships.

## Design deltas, decided

The artboards specify three numbers the existing control does not produce. The control wins; the artboards are being updated to match, not the other way round.

| Artboard | Ships as | Why |
|---|---|---|
| 6 px progress ring | **4.8 px arc** — `max(dia × ArcThicknessRatio 0.04, MinArcThickness 4)` at 120 DIP | Pinned by `DwellArcButtonArcWeightTests`; the ratio exists specifically to stop callers passing a number (`DwellArcButton.cs:17-31`). |
| 44 px icon | **54 px** — `dia × ContainerRatio.Dominant` (0.45) | `DwellArcButton.cs:274-276`: an icon that is the button's whole content takes `Dominant`. |
| 22 px provider badge | **27.6 px** — `_buttonSize × ContainerRatio.Small` | Keeps the badge token-backed rather than a literal. |

The artboards showed a *sweeping* arc, and `DwellArcButton.UpdateProgress` sweeps (`DwellArcButton.cs:333-347`) — so the design's ring is delivered as drawn, which the first draft had conceded away to `DwellOutlineOverlay`'s inward-thickening outline.

## Global Constraints

- **Conventions:** file-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled.
- **Tests:** NFluent (`Check.That(...)`), never `Assert.*`. No mocking library — private nested fakes. WPF-constructing tests use `[StaFact]` and `[Collection("WpfApplication")]`.
- **Never call `Show()`** in a test, and never simulate input. Constructing a `Window` parses its XAML, which is all any test here needs. This suite must never take over the screen of a user whose only input channel is an eye tracker.
- **`TranslatePoint` against an unshown `Window` returns `(0,0)` for every element**, even after `Measure` + `Arrange` + `UpdateLayout` — a window's content is not visually connected until `Show()`. Any test asserting "nothing moved" by comparing `TranslatePoint` results is vacuously green. Measure layout relative to the hosting `Canvas` instead.
- **CLAUDE.md house rule:** run `impact({target: "<symbol>", direction: "upstream"})` before editing any function/class/method and report the blast radius; run `detect_changes()` before each commit. **`rename` and `impact` cannot see `[ObservableProperty]`-generated members** — the graph indexes the declared field, not the generated property, so `rename({from: "ToolbarButtonViewModel.Symbol"})` returns "not found". Rename the backing field by hand against the explicit reference list in Task 2.
- **No literal corner radius, font family, font size or font weight** in `src/EyeCtrl.App` or `src/EyeCtrl.Controls` — `RawGeometryLiteralTests` whitelists against `Tokens` and `ContainerRatio` by reflection.
- **Do not assign `Tokens.CircleCornerRadius` from code-behind.** `CornerRadiusClampTests` scans every `.cs` outside `/Theme/` in the scanned projects and requires zero hits (`CornerRadiusClampTests.cs:213-226`). This plan never does — `DwellArcButton` uses an `Ellipse`.
- **Do not change the dwell-target lifecycle.** Targets are created lazily by `UpdateButtonScreenPosition` at the first real on-screen centre, never at `(0,0)`; later reports move them via `UpdateTargetCenter` so accumulated dwell survives re-layout. Nothing here adds or removes a target at runtime.
- **Do not change dwell timings.** 700 ms for every button, Reset included — spec §8.6 wants Reset reachable exactly when adaptation has gone wrong.
- **Match each test project's own layout when adding a file.** `EyeCtrl.Controls.Tests` uses folder-per-area with matching sub-namespaces (`Icons/`, `Theme/`), so a new icon test belongs in `Icons/` — Task 1 originally specified the project root and had to be moved. `EyeCtrl.App.Tests` keeps most guards at the root with only some areas foldered (`Settings/`, `Services/`, `Fixtures/`), so a root-level file is correct there. Check the sibling of the thing you are testing before choosing a path.
- **Verify command:**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

  Output is German: per-test failures read `Fehler <FQN>`, not `Failed`, and long names wrap across lines. Grepping for `Failed` reports a green run that is not green. Three load-dependent flakes are not regressions: `HeartbeatPublisher` under 1500 ms, `MouseGazeProviderTests`, `ExternalDwellWireContractTests.Cooldown_SecondDwellWithinWindow`.

## File Structure

**New files:**
- `src/EyeCtrl.Controls/Icons/ToolbarIconKey.cs` — enum naming the toolbar's icons.
- `src/EyeCtrl.Controls/Icons/ToolbarIcons.cs` — `ToolbarIconKey` → the **existing** `IconGeometry` record. Sits beside `ActionIcons.cs` and reuses `ActionIcons.GridSize` and `ActionIcons.GridStrokeThickness`, so the whole app draws icons on one grid at one weight.
- `tests/EyeCtrl.Controls.Tests/Icons/ToolbarIconsTests.cs`
- `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs`
- `tests/EyeCtrl.App.Tests/ToolbarIconKeyBridgeTests.cs` — pins the ViewModels-side icon-key strings against the real `ToolbarIconKey` enum. This project is the only one that references both.

**Modified files:**
- `src/EyeCtrl.ViewModels/SelectionModeGlyphs.cs` → `SelectionModeIcons.cs`
- `src/EyeCtrl.ViewModels/ToolbarButtonViewModel.cs` — `_symbol` → `_iconKey`
- `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`
- `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml` — `StackPanel` → `Canvas`, plus an indicator region outside it
- `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` — the bulk of the work
- `tests/EyeCtrl.App.Tests/ContainerRatioCensusTests.cs`, `ToolbarButtonDriftTests.cs`
- `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`

**Not modified:** `DwellWashCensusTests`. The toolbar never washed — it used a bottom-edge bar — and it does not adopt `DwellOutlineOverlay`, so it belongs in neither `MustOutline` nor the `[InlineData]` rows on `EveryFormerWashSite_DrivesAnOutline` (`DwellWashCensusTests.cs:103-105`). The offender scan looks for `Blend(… Tokens.Accent, progress)`, which this plan never writes.

---

## Task 1: `ToolbarIcons`, on the existing icon grid

**Files:**
- Create: `src/EyeCtrl.Controls/Icons/ToolbarIconKey.cs`, `src/EyeCtrl.Controls/Icons/ToolbarIcons.cs`
- Test: `tests/EyeCtrl.Controls.Tests/Icons/ToolbarIconsTests.cs`

**Interfaces:**
- Produces: `ToolbarIconKey { Pause, Play, SelectionNone, SelectionManual, SelectionAuto, Keyboard, Settings, TaskSwitcher, ResetAdaptation, ProviderEye, ProviderMouse }`
- Produces: `ToolbarIcons.For(ToolbarIconKey) → IconGeometry`

- [ ] **Step 1: Read the system being extended**

Read `src/EyeCtrl.Controls/Icons/ActionIcons.cs` and `IconGeometry.cs` in full before writing anything. `ToolbarIcons` must mirror `ActionIcons`' shape exactly: a private `Dictionary<TKey, IconGeometry> Map = Build()`, a `Parse` helper that freezes, a `For` that throws on a missing entry, and **one** stroke weight — `ActionIcons.GridStrokeThickness` (1.8) on `ActionIcons.GridSize` (24). Do not introduce a per-icon thickness; the app's icon vocabulary is one weight, and `DwellArcButton` hard-codes `ActionIcons.GridStrokeThickness` when it builds icon paths (`DwellArcButton.cs:297`), so a per-icon value would be ignored anyway.

- [ ] **Step 2: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Icons/ToolbarIconsTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Icons;

using System;
using System.Windows;
using EyeCtrl.Controls.Icons;
using NFluent;
using Xunit;

/// <summary>
/// The toolbar's icons are drawn geometry, not font glyphs — a font missing the codepoint
/// renders tofu, which already happened to the window-switcher button and left it on the far
/// less readable arrows glyph. These tests cannot say an icon looks right; they say every key
/// resolves, every geometry is non-degenerate and frozen, and every one stays on the same
/// 24-unit grid the action menu's icons use, so the two sets scale and weigh identically.
/// </summary>
public class ToolbarIconsTests
{
    public static TheoryData<ToolbarIconKey> AllKeys()
    {
        var data = new TheoryData<ToolbarIconKey>();
        foreach (var key in Enum.GetValues<ToolbarIconKey>())
            data.Add(key);
        return data;
    }

    private static Rect UnionBounds(IconGeometry g)
    {
        var bounds = Rect.Empty;
        if (g.Stroke is not null) bounds.Union(g.Stroke.Bounds);
        if (g.Fill is not null) bounds.Union(g.Fill.Bounds);
        return bounds;
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void EveryKey_ResolvesToNonDegenerateFrozenGeometry(ToolbarIconKey key)
    {
        var g = ToolbarIcons.For(key);

        Check.That(g.Stroke is not null || g.Fill is not null).IsTrue();
        if (g.Stroke is not null) Check.That(g.Stroke.IsFrozen).IsTrue();
        if (g.Fill is not null) Check.That(g.Fill.IsFrozen).IsTrue();

        var bounds = UnionBounds(g);
        Check.WithCustomMessage($"{key} has empty bounds").That(bounds.IsEmpty).IsFalse();
        Check.WithCustomMessage($"{key} is under 4 grid units across")
            .That(Math.Max(bounds.Width, bounds.Height)).IsStrictlyGreaterThan(4.0);
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void EveryKey_StaysOnTheSharedGrid(ToolbarIconKey key)
    {
        // Same grid as ActionIcons: DwellArcButton scales by iconSize / ActionIcons.GridSize,
        // so an icon drawn outside the box renders clipped or off-centre at every call site.
        var bounds = UnionBounds(ToolbarIcons.For(key));
        var slack = ActionIcons.GridStrokeThickness / 2.0;

        Check.WithCustomMessage($"{key} bounds {bounds} escape the {ActionIcons.GridSize} grid")
            .That(bounds.Left >= -slack && bounds.Top >= -slack
                  && bounds.Right <= ActionIcons.GridSize + slack
                  && bounds.Bottom <= ActionIcons.GridSize + slack)
            .IsTrue();
    }

    [Fact]
    public void TheRegistry_CoversEveryKey()
    {
        // For() throws on a missing entry; this makes a new enum member fail here rather than at
        // window-construction time in front of a user whose only input channel is an eye tracker.
        foreach (var key in Enum.GetValues<ToolbarIconKey>())
            Check.ThatCode(() => ToolbarIcons.For(key)).DoesNotThrow();
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ToolbarIconsTests"`
Expected: build failure — `ToolbarIconKey` and `ToolbarIcons` do not exist.

- [ ] **Step 4: Create the enum**

Create `src/EyeCtrl.Controls/Icons/ToolbarIconKey.cs`:

```csharp
namespace EyeCtrl.Controls.Icons;

/// <summary>
/// The main toolbar's icons, named by meaning rather than by appearance.
///
/// Separate from the button ids in <c>ToolbarViewModel</c> because one button shows several
/// icons over its life: Pause swaps to <see cref="Play"/> while paused, and the selection-mode
/// button shows one of three icons for the mode it is currently in.
///
/// Separate from <c>ActionIcon</c> because that set is deliberately all mice — every action-menu
/// entry is a mouse action. These are application controls and share none of that vocabulary.
/// </summary>
public enum ToolbarIconKey
{
    /// <summary>Two upright bars — input is running; dwelling pauses it.</summary>
    Pause,
    /// <summary>Right-pointing triangle — input is paused; dwelling resumes it.</summary>
    Play,
    /// <summary>Slashed ring — SelectionMode.None, raw pixel targeting.</summary>
    SelectionNone,
    /// <summary>Crosshair in a ring — SelectionMode.ManualSelection, zoom and dwell.</summary>
    SelectionManual,
    /// <summary>Ring around a control — SelectionMode.AutoSelection, snap to control.</summary>
    SelectionAuto,
    /// <summary>Key rows in a rounded case.</summary>
    Keyboard,
    /// <summary>Gear.</summary>
    Settings,
    /// <summary>Two offset windows.</summary>
    TaskSwitcher,
    /// <summary>Counter-clockwise arrow — reset struggle adaptation to baseline.</summary>
    ResetAdaptation,
    /// <summary>Eye — the gaze provider is a real tracker.</summary>
    ProviderEye,
    /// <summary>Mouse — the gaze provider is the mouse fallback.</summary>
    ProviderMouse,
}
```

- [ ] **Step 5: Create the registry**

Create `src/EyeCtrl.Controls/Icons/ToolbarIcons.cs`. Mirror `ActionIcons`' structure exactly — read it again if the shape below drifts from it.

```csharp
namespace EyeCtrl.Controls.Icons;

using System.Collections.Generic;
using System.Windows.Media;

/// <summary>
/// The main toolbar's drawn icon set: <see cref="ToolbarIconKey"/> to <see cref="IconGeometry"/>.
///
/// <para>
/// Authored on the same <see cref="ActionIcons.GridSize"/> square at the same
/// <see cref="ActionIcons.GridStrokeThickness"/> as the action menu, and scaled by the caller's
/// RenderTransform — which is what <see cref="EyeCtrl.Controls.DwellArcButton"/> already does for
/// any <see cref="IconGeometry"/> it is handed. One grid and one weight across the app is the
/// point; do not introduce a per-icon thickness, and note DwellArcButton would ignore it.
/// </para>
///
/// <para>
/// Geometries are parsed once, frozen and shared. Frozen is not an optimisation: these are
/// statics handed to every button on screen, and an unfrozen Geometry is mutable and
/// thread-affine.
/// </para>
/// </summary>
public static class ToolbarIcons
{
    /// <summary>A full circle of radius 7 centred on the grid, as two half arcs.</summary>
    private const string Ring = "M5,12 A7,7 0 1 1 19,12 A7,7 0 1 1 5,12 Z";

    private static readonly Dictionary<ToolbarIconKey, IconGeometry> Map = Build();

    /// <summary>The geometry for <paramref name="key"/>. Throws if the registry has no entry.</summary>
    /// <param name="key">Which icon to resolve.</param>
    /// <returns>That icon's two layers.</returns>
    public static IconGeometry For(ToolbarIconKey key) => Map[key];

    private static Geometry? Parse(string? data)
    {
        if (data is null) return null;
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static IconGeometry Icon(string? stroke, string? fill = null)
        => new(Parse(stroke), Parse(fill));

    private static Dictionary<ToolbarIconKey, IconGeometry> Build() => new()
    {
        [ToolbarIconKey.Pause] = Icon(null, "M8.4,5.2 L11.2,5.2 L11.2,18.8 L8.4,18.8 Z "
                                          + "M12.8,5.2 L15.6,5.2 L15.6,18.8 L12.8,18.8 Z"),
        [ToolbarIconKey.Play] = Icon(null, "M9,5.6 L18.6,12 L9,18.4 Z"),
        [ToolbarIconKey.SelectionNone] = Icon(Ring + " M7.1,7.1 L16.9,16.9"),
        [ToolbarIconKey.SelectionManual] = Icon(
            Ring + " M12,1.8 L12,5.2 M12,18.8 L12,22.2 M1.8,12 L5.2,12 M18.8,12 L22.2,12",
            "M12,10.3 A1.7,1.7 0 1 1 12,13.7 A1.7,1.7 0 1 1 12,10.3 Z"),
        [ToolbarIconKey.SelectionAuto] = Icon(
            Ring + " M8.6,9.4 L15.4,9.4 L15.4,14.6 L8.6,14.6 Z"),
        [ToolbarIconKey.Keyboard] = Icon(
            "M4.8,6 L19.2,6 A2.6,2.6 0 0 1 21.8,8.6 L21.8,15.4 A2.6,2.6 0 0 1 19.2,18 "
            + "L4.8,18 A2.6,2.6 0 0 1 2.2,15.4 L2.2,8.6 A2.6,2.6 0 0 1 4.8,6 Z "
            + "M6,10.2 L6.01,10.2 M9.5,10.2 L9.51,10.2 M13,10.2 L13.01,10.2 "
            + "M16.5,10.2 L16.51,10.2 M8,14.2 L16,14.2"),
        [ToolbarIconKey.Settings] = Icon(
            "M12,8.6 A3.4,3.4 0 1 1 12,15.4 A3.4,3.4 0 1 1 12,8.6 Z "
            + "M12,1.6 L12,5 M12,19 L12,22.4 M22.4,12 L19,12 M5,12 L1.6,12 "
            + "M19.35,4.65 L16.95,7.05 M7.05,16.95 L4.65,19.35 "
            + "M19.35,19.35 L16.95,16.95 M7.05,7.05 L4.65,4.65"),
        [ToolbarIconKey.TaskSwitcher] = Icon(
            "M4.8,4.6 L13.2,4.6 A2.2,2.2 0 0 1 15.4,6.8 L15.4,12 A2.2,2.2 0 0 1 13.2,14.2 "
            + "L4.8,14.2 A2.2,2.2 0 0 1 2.6,12 L2.6,6.8 A2.2,2.2 0 0 1 4.8,4.6 Z "
            + "M10.8,9.8 L19.2,9.8 A2.2,2.2 0 0 1 21.4,12 L21.4,17.2 A2.2,2.2 0 0 1 19.2,19.4 "
            + "L10.8,19.4 A2.2,2.2 0 0 1 8.6,17.2 L8.6,12 A2.2,2.2 0 0 1 10.8,9.8 Z"),
        [ToolbarIconKey.ResetAdaptation] = Icon(
            "M3,12 A9,9 0 1 0 12,3 A9,9 0 0 0 5.26,5.74 L3,8 M3,3 L3,8 L8,8"),
        [ToolbarIconKey.ProviderEye] = Icon(
            "M2,12 C5,7 8.4,6 12,6 C15.6,6 19,7 22,12 C19,17 15.6,18 12,18 C8.4,18 5,17 2,12 Z "
            + "M12,9.2 A2.8,2.8 0 1 1 12,14.8 A2.8,2.8 0 1 1 12,9.2 Z"),
        [ToolbarIconKey.ProviderMouse] = Icon(
            "M12,2.4 A6.4,6.4 0 0 1 18.4,8.8 L18.4,15.2 A6.4,6.4 0 0 1 5.6,15.2 "
            + "L5.6,8.8 A6.4,6.4 0 0 1 12,2.4 Z M12,6 L12,10"),
    };
}
```

> Pause is a filled pair of bars rather than heavy strokes, because the shared grid has one stroke weight and 1.8 is too light to read as a pause symbol.

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ToolbarIconsTests"`
Expected: PASS, 23 tests (11 keys × 2 theories + 1 fact).

- [ ] **Step 7: Look at the icons once, by eye**

The tests prove the geometry parses, freezes and fits. They cannot prove `ResetAdaptation` reads as "reset" or that the gear looks like a gear. Paste each string into an SVG `viewBox="0 0 24 24"` with `fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"` and look. Fix anything that reads wrong now — this is the only point where an icon's shape is cheap to change.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Controls/Icons tests/EyeCtrl.Controls.Tests/Icons/ToolbarIconsTests.cs
git commit -m "feat(controls): toolbar icon geometry on the shared icon grid"
```

---

## Task 2: The icon-key channel

The view-model sends the view a Unicode string and the view maps it back with `SelectionModeGlyphs.ToMode`. With drawn icons there is no glyph to round-trip, so the channel carries a `ToolbarIconKey` member name instead. It travels as a string because `EyeCtrl.ViewModels` must not reference the WPF `EyeCtrl.Controls` assembly.

**Files:**
- Rename: `src/EyeCtrl.ViewModels/SelectionModeGlyphs.cs` → `SelectionModeIcons.cs`
- Modify: `src/EyeCtrl.ViewModels/ToolbarButtonViewModel.cs`, `ToolbarViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`
- Create: `tests/EyeCtrl.App.Tests/ToolbarIconKeyBridgeTests.cs`

- [ ] **Step 1: Run impact analysis on what the graph can see**

```
impact({target: "SelectionModeGlyphs", direction: "upstream"})
```

Expected: `ToolbarViewModel`, `ToolbarWindow`. Report the result; stop if HIGH or CRITICAL.

Do **not** run `impact` or `rename` on `ToolbarButtonViewModel.Symbol` — it is generated from `[ObservableProperty] private string _symbol` (`ToolbarButtonViewModel.cs:9`) and the graph has no node for it (`context` on the class lists `Id` only). Both tools return "not found". The complete reference list, verified by grep, is: `ToolbarViewModel.cs:133,139,145`; `ToolbarWindow.xaml.cs:283,297,299`; `ToolbarViewModelTests.cs:70,93,96,99`.

- [ ] **Step 2: Write the failing tests**

Replace the glyph assertions in `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs` — `UpdatesPausedState_OnStateChanged` at line 70 asserts `"▶"`, and `SelectionModeChanged_UpdatesSelectionButtonSymbol` at lines 93-99 asserts `"○"`/`"⊕"`/`"◎"`. Rewrite both against icon keys, and add:

```csharp
    [Fact]
    public void SelectionModeIcons_RoundTripEveryMode()
    {
        foreach (var mode in Enum.GetValues<SelectionMode>())
            Check.WithCustomMessage($"{mode} lost its identity through the icon key")
                .That(SelectionModeIcons.ToMode(SelectionModeIcons.ForMode(mode))).IsEqualTo(mode);
    }

    [Fact]
    public void SelectionModeIcons_EmitDistinctKeysPerMode()
    {
        // With no label the icon is the button's primary state channel. The fill swap stays as a
        // secondary one (Task 5's SetSelectionModeState), but three modes sharing an icon would
        // leave colour carrying the distinction alone, which is what this design moved away from.
        var keys = Enum.GetValues<SelectionMode>().Select(SelectionModeIcons.ForMode).ToList();
        Check.That(keys.Distinct(StringComparer.Ordinal)).HasSize(keys.Count);
    }

    [Fact]
    public void SelectionModeChanged_UpdatesTheButtonsIconKey()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), messenger);
        messenger.RegisterAll(vm);

        messenger.Send(new SelectionModeChangedMessage(SelectionMode.AutoSelection));

        Check.That(vm.Buttons.Single(b => b.Id == "SelectionMode").IconKey)
            .IsEqualTo(SelectionModeIcons.ForMode(SelectionMode.AutoSelection));
    }
```

Create `tests/EyeCtrl.App.Tests/ToolbarIconKeyBridgeTests.cs` — this project is the only one referencing both assemblies:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// ToolbarViewModel names its icons as strings because EyeCtrl.ViewModels must not reference the
/// WPF Controls assembly. That string is parsed back to a <see cref="ToolbarIconKey"/> when the
/// toolbar builds its buttons, so a typo is a runtime throw during window construction — in front
/// of a user whose only input channel is an eye tracker. This project sees both sides; it is the
/// only place the two ends can be pinned together.
/// </summary>
public class ToolbarIconKeyBridgeTests
{
    [Fact]
    public void EverySelectionModeIconKey_NamesARealToolbarIconKey()
    {
        foreach (var mode in Enum.GetValues<SelectionMode>())
        {
            var key = SelectionModeIcons.ForMode(mode);
            Check.WithCustomMessage($"SelectionModeIcons.ForMode({mode}) = \"{key}\", which is not "
                    + "a ToolbarIconKey member.")
                .That(Enum.TryParse<ToolbarIconKey>(key, out _)).IsTrue();
        }
    }

    [Fact]
    public void EveryViewModelButtonIconKey_NamesARealToolbarIconKey()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());

        foreach (var button in vm.Buttons)
            Check.WithCustomMessage($"Button {button.Id} carries icon key \"{button.IconKey}\", "
                    + "which is not a ToolbarIconKey member.")
                .That(Enum.TryParse<ToolbarIconKey>(button.IconKey, out _)).IsTrue();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"`
Expected: build failure — `SelectionModeIcons` and `IconKey` do not exist.

- [ ] **Step 4: Rename the backing field by hand**

```bash
git mv src/EyeCtrl.ViewModels/SelectionModeGlyphs.cs src/EyeCtrl.ViewModels/SelectionModeIcons.cs
```

In `ToolbarButtonViewModel.cs`, rename `_symbol` → `_iconKey` (the generated `IconKey` property follows) and update the constructor parameter. Then update each site in the verified list from Step 1.

- [ ] **Step 5: Rewrite the mapping**

`src/EyeCtrl.ViewModels/SelectionModeIcons.cs`:

```csharp
namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Models;

/// <summary>
/// Single source of truth for which toolbar icon represents each <see cref="SelectionMode"/>.
/// Shared by <see cref="ToolbarViewModel"/> (mode → key) and the toolbar window (key → mode) so
/// the forward and reverse mappings cannot drift apart.
///
/// Values are <c>EyeCtrl.Controls.Icons.ToolbarIconKey</c> member NAMES carried as strings,
/// because this project must not reference the WPF Controls assembly. Pinned against the real
/// enum by <c>ToolbarIconKeyBridgeTests</c> in EyeCtrl.App.Tests, which sees both sides.
/// </summary>
public static class SelectionModeIcons
{
    /// <summary>None (raw pixel targeting) — a slashed ring.</summary>
    public const string None = "SelectionNone";

    /// <summary>Manual Selection (zoom-and-dwell) — a crosshair in a ring.</summary>
    public const string ManualSelection = "SelectionManual";

    /// <summary>Auto Selection (snap-to-control) — a ring around a control.</summary>
    public const string AutoSelection = "SelectionAuto";

    /// <summary>Returns the icon key for the given selection mode.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The icon key name.</returns>
    public static string ForMode(SelectionMode mode) => mode switch
    {
        SelectionMode.ManualSelection => ManualSelection,
        SelectionMode.AutoSelection => AutoSelection,
        _ => None
    };

    /// <summary>Maps an icon key back to its mode (defaults to <see cref="SelectionMode.None"/>).</summary>
    /// <param name="iconKey">The icon key name.</param>
    /// <returns>The mode it represents.</returns>
    public static SelectionMode ToMode(string iconKey) => iconKey switch
    {
        ManualSelection => SelectionMode.ManualSelection,
        AutoSelection => SelectionMode.AutoSelection,
        _ => SelectionMode.None
    };
}
```

- [ ] **Step 6: Swap the view-model's glyph literals**

In `ToolbarViewModel.cs`, the constructor array — keep the `new (string Id, string Symbol)[]` declaration text verbatim, because `ToolbarButtonDriftTests` locates both button arrays by that exact string and its failure message asks you to update the guard rather than move the declaration (`ToolbarButtonDriftTests.cs:81-86`):

```csharp
        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause", "Pause"),
            ("SelectionMode", SelectionModeIcons.None),
            ("Keyboard", "Keyboard"),
            ("Calibrate", "Settings"),
            ("Settings", "Settings"),
            ("TaskSwitcher", "TaskSwitcher"),
            ("QuickActions", "Settings"),
            ("ResetAdaptation", "ResetAdaptation"),
        };
```

> `Calibrate` and `QuickActions` are not rendered on the toolbar (`ToolbarButtonDriftTests.ReachableElsewhere`, lines 42-44). They take `Settings` as a placeholder so `ToolbarIconKeyBridgeTests` stays total. The drift guard's regex captures only the id group (line 48-49), so the value is unconstrained — this is filler, and the `Calibrate` follow-up plan replaces it with real success/failure icons.

Then the three handlers:

```csharp
    public void Receive(StateChangedMessage message)
    {
        IsPaused = message.NewState == InteractionState.Paused;
        if (_buttonLookup.TryGetValue("Pause", out var btn))
            btn.IconKey = IsPaused ? "Play" : "Pause";
    }

    public void Receive(SelectionModeChangedMessage message)
    {
        _currentSelectionMode = message.Mode;
        if (_buttonLookup.TryGetValue("SelectionMode", out var btn))
            btn.IconKey = SelectionModeIcons.ForMode(message.Mode);
    }
```

Delete the bodies of `Receive(CalibrationCompletedMessage)` and `Receive(CalibrationCancelledMessage)`'s icon writes and replace with a comment — the `✓`/`⚠` swap was already dead, since `Calibrate` is never rendered:

```csharp
    // Calibrate is not on the toolbar (ToolbarButtonDriftTests.ReachableElsewhere), so the old
    // success/failure glyph swap was never visible to anyone. The handlers stay registered so the
    // messages keep a recipient; the icon swap returns with the button.
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"`
Expected: PASS. `src/EyeCtrl.App` will not compile until Task 3 — this commit is intentionally not independently buildable.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.ViewModels tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.App.Tests/ToolbarIconKeyBridgeTests.cs
git commit -m "refactor(toolbar): carry icon keys instead of Unicode glyphs"
```

---

## Task 3: The strip becomes a Canvas of `DwellArcButton`s

The largest task, and the one that delivers most of the design: disc face, visible rim, sweeping arc, drawn icons — all from the control the action menu already uses.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml`, `ToolbarWindow.xaml.cs`
- Test: `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs`

**Interfaces:**
- Consumes: `DwellArcButton.Create(center, radius, symbol, …, icon, label)`, `.AddTo(Canvas)`, `.UpdateProgress(double)`, `.Background`, `ToolbarIcons.For`, `SelectionModeIcons.ToMode`.

- [ ] **Step 1: Read the reference implementation**

Read `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs:205-232`. That block — `DwellArcButton.Create(centre, radius, symbol: null, icon: …, label: …)`, `btn.AddTo(canvas)`, `_buttonVisuals[id] = btn`, a `PropertyChanged` handler forwarding `DwellProgress` — is the pattern this task reproduces. Note it passes `label: showLabels ? … : null`; the toolbar always passes `label: null`.

- [ ] **Step 2: Write the failing test**

Create `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The toolbar's dwell target is a circle of radius Size/2. These tests pin the properties that
/// make the drawn button honest about that — it is an Ellipse of exactly that size, it carries a
/// rim visible against the strip, and its progress is an arc rather than a 4 px edge bar.
///
/// Layout is measured against the hosting Canvas, never with TranslatePoint against the Window:
/// a Window's content is not visually connected until Show(), so TranslatePoint returns (0,0) for
/// every element and any "nothing moved" assertion built on it is vacuously green.
///
/// Nothing calls Show(). Constructing the window parses its XAML, which is all these need.
/// </summary>
[Collection("WpfApplication")]
public class ToolbarButtonVisualTests
{
    private const double ButtonSize = 120;

    private static ToolbarWindow NewInitialisedToolbar()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ToolbarWindow();
        window.Initialize(ButtonSize, 700);
        return window;
    }

    private static Canvas ButtonCanvas(ToolbarWindow w) => w.ButtonCanvasForTest;

    private static List<Ellipse> Faces(ToolbarWindow w) =>
        ButtonCanvas(w).Children.OfType<Ellipse>().Where(e => e.Width == ButtonSize).ToList();

    [StaFact]
    public void EveryButton_IsAnEllipseOfExactlyTheDwellDiameter()
    {
        var faces = Faces(NewInitialisedToolbar());

        Check.WithCustomMessage("Six rendered buttons expected.").That(faces).HasSize(6);
        foreach (var f in faces)
        {
            // A square tile hid ~21.5% dead area outside the inscribed hit circle. An Ellipse of
            // the dwell diameter makes the drawing and the hit test the same shape.
            Check.That(f.Width).IsEqualTo(ButtonSize);
            Check.That(f.Height).IsEqualTo(ButtonSize);
        }
    }

    [StaFact]
    public void EveryButton_CarriesAVisibleRim()
    {
        // SurfaceMedium on SurfaceDark computes to 1.23:1 — without a rim the boundary between
        // adjacent targets is invisible. DwellArcButton's default stroke is BrushBorderSubtle.
        foreach (var f in Faces(NewInitialisedToolbar()))
        {
            var rim = ((SolidColorBrush)f.Stroke).Color;
            Check.That(rim == Tokens.BorderSubtle || rim == Tokens.AccentWarning).IsTrue();
            Check.That(f.StrokeThickness).IsStrictlyGreaterThan(0.0);
        }
    }

    [StaFact]
    public void EveryButton_DrawsAnIcon_AndNoTextOnItsFace()
    {
        var canvas = ButtonCanvas(NewInitialisedToolbar());

        Check.WithCustomMessage("No icon paths — the buttons fell back to glyphs.")
            .That(canvas.Children.OfType<Path>().Count()).IsStrictlyGreaterThan(5);

        var visibleText = canvas.Children.OfType<TextBlock>().Where(t => t.Opacity > 0).ToList();
        Check.WithCustomMessage("This design has no text on the button faces.")
            .That(visibleText).IsEmpty();
    }

    [StaFact]
    public void EveryButton_KeepsATextBlockAutomationAnchor()
    {
        // WPF's UIA control view does not expose Ellipse or Path from a transparent overlay
        // window, so the AutomationId has to live on a TextBlock. Losing it silently deletes
        // Toolbar_Button_<Id> from the UIA tree, which is how AppFixture finds these buttons
        // (AppFixture.cs:193-196, ToolbarTests.cs:35).
        var ids = ButtonCanvas(NewInitialisedToolbar()).Children.OfType<TextBlock>()
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        Check.That(ids).Contains("Toolbar_Button_Pause", "Toolbar_Button_SelectionMode",
            "Toolbar_Button_Keyboard", "Toolbar_Button_Settings",
            "Toolbar_Button_TaskSwitcher", "Toolbar_Button_ResetAdaptation");
    }

    [StaFact]
    public void ButtonCentres_AreEvenlyPitchedAndInsideTheCanvas()
    {
        var window = NewInitialisedToolbar();
        var canvas = ButtonCanvas(window);
        var lefts = Faces(window).Select(Canvas.GetLeft).OrderBy(x => x).ToList();

        Check.That(canvas.Width).IsStrictlyGreaterThan(lefts.Last() + ButtonSize - 1);
        Check.That(canvas.Height).IsStrictlyGreaterThan(ButtonSize - 1);

        // Even pitch everywhere except the deliberate extra gap before Reset (Task 7).
        var gaps = lefts.Zip(lefts.Skip(1), (a, b) => b - a).Distinct().ToList();
        Check.WithCustomMessage("More than two distinct pitches means the layout maths drifted.")
            .That(gaps.Count).IsStrictlyLessThan(3);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarButtonVisualTests"`
Expected: build failure — `ButtonCanvasForTest` does not exist.

- [ ] **Step 4: Replace the XAML**

`src/EyeCtrl.App/Overlays/ToolbarWindow.xaml` body:

```xml
    <Border Background="{StaticResource BrushSurfaceDark}" CornerRadius="{StaticResource StandardCornerRadius}" Padding="4">
        <StackPanel x:Name="StripWrapper" Orientation="Horizontal" VerticalAlignment="Center">
            <!-- Chrome, not a target: the provider indicator is not dwellable, so it must not sit
                 inside the button band where it reads as a broken button. Empty until a provider
                 is known, and an empty StackPanel measures to zero. -->
            <StackPanel x:Name="IndicatorPanel" Orientation="Horizontal"
                        VerticalAlignment="Center" Margin="8,0,4,0"/>
            <!-- DwellArcButton positions itself with Canvas.SetLeft/SetTop, so the button band is
                 a Canvas with an explicitly computed size — a Canvas measures to zero otherwise,
                 and this window is SizeToContent. -->
            <Canvas x:Name="ButtonCanvas"/>
        </StackPanel>
    </Border>
```

- [ ] **Step 5: Rewrite the button construction**

In `ToolbarWindow.xaml.cs`, replace `_buttonVisuals`, `AddButton`, `SetPausedState`, `BindButton`'s symbol branch and `UpdateTargetPositions`' iteration:

```csharp
    /// <summary>Centre-to-centre spacing: the button plus its margin on both sides.</summary>
    private double Pitch => _buttonSize + ButtonMargin * 2;

    /// <summary>Extra clearance before the one unrecoverable action. See Task 7.</summary>
    private double ResetGap => ButtonMargin * 2;

    private readonly Dictionary<string, DwellArcButton> _buttons = new();
    private readonly Dictionary<string, TextBlock> _anchors = new();

    /// <summary>The button band, for tests. Layout must be measured against this, not the Window.</summary>
    internal Canvas ButtonCanvasForTest => ButtonCanvas;

    private void AddButton(string id, string iconKey, Point center)
    {
        var button = DwellArcButton.Create(
            center,
            _buttonSize / 2,
            symbol: null,
            stroke: RimFor(id),
            arcBrush: ArcBrushFor(id),
            symbolBrush: IconBrushFor(id),
            icon: ToolbarIcons.For(ParseIconKey(iconKey)),
            label: null);

        button.AddTo(ButtonCanvas);

        // The anchor exists so the button appears in the UIA tree at all: WPF's UIA control view
        // does not expose Ellipse or Path from a transparent overlay window. ActionMenuWindow sets
        // no AutomationIds and so needs no anchor; the toolbar's ids are consumed by AppFixture.
        // Opacity 0 keeps it laid out and in the tree while drawing nothing.
        var anchor = new TextBlock { Text = id, Opacity = 0, IsHitTestVisible = false };
        AutomationProperties.SetAutomationId(anchor, $"Toolbar_Button_{id}");
        AutomationProperties.SetName(anchor, id);
        Canvas.SetLeft(anchor, center.X - _buttonSize / 2);
        Canvas.SetTop(anchor, center.Y - _buttonSize / 2);
        ButtonCanvas.Children.Add(anchor);

        _buttons[id] = button;
        _anchors[id] = anchor;
    }

    /// <summary>Parses a key name. An unknown key is a coding error, not user input.</summary>
    private static ToolbarIconKey ParseIconKey(string key) => Enum.Parse<ToolbarIconKey>(key);

    /// <summary>The at-rest rim. Only Reset differs — see Task 7.</summary>
    private static Brush RimFor(string id) =>
        id == "ResetAdaptation" ? Tokens.BrushAccentWarning : Tokens.BrushBorderSubtle;

    /// <summary>The progress arc. Reset's is warning-coloured so its dwell reads as different.</summary>
    private static Brush ArcBrushFor(string id) =>
        id == "ResetAdaptation" ? Tokens.BrushAccentWarning : Tokens.BrushAccent;

    /// <summary>The icon. Only Reset differs.</summary>
    private static Brush IconBrushFor(string id) =>
        id == "ResetAdaptation" ? Tokens.BrushAccentWarning : Tokens.BrushTextPrimary;
```

`Initialize` now lays the band out and sizes the canvas:

```csharp
    public void Initialize(double buttonSize, int dwellTimeMs)
    {
        _buttonSize = buttonSize;
        _buttons.Clear();
        _anchors.Clear();
        ButtonCanvas.Children.Clear();
        _targetsInitialized = false;

        // Every id here must exist in ToolbarViewModel's button list and have a case in its
        // OnTargetActivated switch; ToolbarButtonDriftTests enforces that and records why the
        // deliberately-absent ids are absent.
        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause",           "Pause"),
            ("SelectionMode",   SelectionModeIcons.None),
            ("Keyboard",        "Keyboard"),
            ("Settings",        "Settings"),
            ("TaskSwitcher",    "TaskSwitcher"),
            ("ResetAdaptation", "ResetAdaptation"),
        };

        LayOutBand(buttons);

        if (_providerType.HasValue)
            AddIndicatorElement(_providerType.Value);
    }

    /// <summary>
    /// Places each button along the docked axis and sizes the Canvas to hold them. A Canvas
    /// measures to zero, and this window is SizeToContent, so the size must be explicit or the
    /// strip collapses.
    /// </summary>
    private void LayOutBand((string Id, string Symbol)[] buttons)
    {
        var along = ButtonMargin + _buttonSize / 2;   // centre of the first slot
        var across = Pitch / 2;
        double? previousEdge = null;

        foreach (var (id, iconKey) in buttons)
        {
            if (id == "ResetAdaptation")
            {
                // Widen the gap FIRST, then place the rule halfway across it. Deriving the rule's
                // position from the pre-gap `along` puts it 48 px right of centre — straight across
                // the warning-rimmed disc it is supposed to fence off.
                along += ResetGap;
                if (previousEdge is { } edge)
                    AddSeparator((edge + along - _buttonSize / 2) / 2);
            }

            AddButton(id, iconKey, _vertical ? new Point(across, along) : new Point(along, across));
            previousEdge = along + _buttonSize / 2;
            along += Pitch;
        }

        var extent = along - Pitch + _buttonSize / 2 + ButtonMargin;
        ButtonCanvas.Width = _vertical ? Pitch : extent;
        ButtonCanvas.Height = _vertical ? extent : Pitch;
    }

    private bool _vertical;
```

`SetPausedState` and the icon-swap path go through one method, since `DwellArcButton` builds its icon paths at construction and does not expose a setter:

```csharp
    /// <summary>
    /// Rebuilds one button with a new icon. DwellArcButton builds its icon Paths in Create and
    /// has no icon setter, so a swap is a rebuild — cheap, and only Pause and SelectionMode ever
    /// swap. Preserves the button's centre so no dwell target moves.
    /// </summary>
    private void SwapIcon(string id, string iconKey)
    {
        if (!_buttons.TryGetValue(id, out var old))
            return;

        var center = new Point(Canvas.GetLeft(old.Background) + _buttonSize / 2,
                               Canvas.GetTop(old.Background) + _buttonSize / 2);
        RemoveButton(id);
        AddButton(id, iconKey, center);
    }

    private void RemoveButton(string id)
    {
        if (_buttons.Remove(id, out var b))
        {
            foreach (var child in new UIElement?[] { b.RimHalo, b.Background, b.HaloPath,
                         b.ProgressPath, b.Symbol, b.IconFill, b.IconStroke, b.Label })
                if (child is not null) ButtonCanvas.Children.Remove(child);
        }
        if (_anchors.Remove(id, out var anchor))
            ButtonCanvas.Children.Remove(anchor);
    }

    public void SetPausedState(bool isPaused) => SwapIcon("Pause", isPaused ? "Play" : "Pause");
```

`UpdateTargetPositions` reads the `Ellipse` instead of a `Border`:

```csharp
    private void UpdateTargetPositions()
    {
        if (DataContext is not ToolbarViewModel vm) return;

        foreach (var (id, button) in _buttons)
        {
            try
            {
                var face = button.Background;
                var pos = face.PointToScreen(new Point(face.ActualWidth / 2, face.ActualHeight / 2));
                vm.UpdateButtonScreenPosition(id, new PhysicalPoint(pos.X, pos.Y));
            }
            catch
            {
                return;   // not laid out yet
            }
        }

        // Don't latch until every face has been laid out — a gaze dispatch at Normal priority can
        // outrank the Render layout pass, collapsing all targets onto (0,0).
        foreach (var (_, button) in _buttons)
            if (button.Background.ActualWidth == 0)
                return;

        _targetsInitialized = true;
    }
```

Add usings: `EyeCtrl.Controls`, `EyeCtrl.Controls.Icons`, `System.Windows.Automation`, `System`.

- [ ] **Step 6: Rewrite `SetMode` around the axis flag**

```csharp
    public void SetMode(bool isDocked, string edge)
    {
        var key = isDocked ? edge.ToLowerInvariant() : "top";
        var wantVertical = key is "left" or "right";

        // The old code set Orientation only in the left/right cases and never reset it, so a
        // left-dock followed by an undock or a top-dock left the strip vertical until restart.
        if (wantVertical != _vertical)
        {
            _vertical = wantVertical;
            StripWrapper.Orientation = IndicatorPanel.Orientation =
                _vertical ? Orientation.Vertical : Orientation.Horizontal;
            Initialize(_buttonSize, 0);   // re-lays the band along the new axis
            BindToViewModel();            // re-attaches handlers to the rebuilt buttons
        }

        UpdateLayout();   // SizeToContent does not re-measure until the next pass

        if (!isDocked) { _targetsInitialized = false; return; }

        var screen = SystemParameters.WorkArea;
        switch (key)
        {
            case "bottom":
                Left = (screen.Width - ActualWidth) / 2; Top = screen.Height - ActualHeight; break;
            case "left":
                Left = 0; Top = (screen.Height - ActualHeight) / 2; break;
            case "right":
                Left = screen.Width - ActualWidth; Top = (screen.Height - ActualHeight) / 2; break;
            default:
                Left = (screen.Width - ActualWidth) / 2; Top = 0; break;
        }

        _targetsInitialized = false;
    }
```

> `BindToViewModel` must become safe to call twice against rebuilt buttons — see Task 4, which replaces the `_boundIds` guard with a subscription list torn down on rebuild.
>
> A dock-edge save rebuilds the band twice: `SettingsUiCoordinator` calls `Initialize` and then `SetMode`, whose axis branch calls `Initialize` again. Harmless — the second rebuild is idempotent and no dwell target moves, because `UpdateTargetPositions` re-reports every centre — but it is churn, and worth collapsing if the axis branch ever grows.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarButtonVisualTests"`
Expected: PASS, 5 tests.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Overlays tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs
git commit -m "feat(toolbar): build the strip from DwellArcButton"
```

---

## Task 4: Progress arc wiring

**Files:** `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [StaFact]
    public void DwellProgress_DrivesTheArc_AndIsInvisibleAtRest()
    {
        var window = NewInitialisedToolbar();
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.DataContext = vm;
        window.BindToViewModel();

        var arcs = ButtonCanvas(window).Children.OfType<Path>()
            .Where(p => p.Visibility == Visibility.Collapsed).ToList();
        Check.WithCustomMessage("Arcs must be collapsed at rest, not drawn at zero length.")
            .That(arcs).Not.IsEmpty();

        // Before/after, not a bare count: seven icon Paths are already visible at rest, so
        // "greater than zero" passes even with the DwellProgress branch deleted.
        var before = window.ProgressVisibleCountForTest;
        vm.SimulateTargetProgress("Pause", 0.5);

        Check.WithCustomMessage("Dwell progress did not reach the arc.")
            .That(window.ProgressVisibleCountForTest).IsEqualTo(before + 2);   // arc + its halo
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellProgress_DrivesTheArc"`
Expected: build failure. Four members this task's code references arrive in later tasks, and C# does not defer-compile behind a `// Task N` comment — add all four as stubs now, then fill them in where the plan says:

- `ToolbarViewModel.SimulateTargetProgress` (Task 5 Step 3) — add it now, it is three lines.
- `ToolbarViewModel.ButtonActivated` (Task 8 Step 3) — add the event declaration now, leave it unraised.
- `ToolbarWindow.FlashActivated(string)` (Task 8 Step 3) — add an empty body now.
- `ToolbarWindow.ApplyAcquiredState(string, bool)` (Task 5 Step 6) — add an empty body now.

- [ ] **Step 3: Replace `BindButton`'s progress branch and make rebinding safe**

```csharp
    private readonly List<(ToolbarButtonViewModel Vm, PropertyChangedEventHandler Handler)> _subs = new();

    /// <summary>
    /// Subscribes each button view-model to its visuals. Safe to call repeatedly: Task 3's
    /// axis change rebuilds every DwellArcButton, so the old subscriptions must be dropped or
    /// they drive dead visuals. The former _boundIds guard assumed the visuals never changed.
    /// </summary>
    public void BindToViewModel()
    {
        if (DataContext is not ToolbarViewModel vm) return;

        foreach (var (target, handler) in _subs)
            target.PropertyChanged -= handler;
        _subs.Clear();

        foreach (var btn in vm.Buttons)
            BindButton(btn);

        if (_activationHooked) return;
        _activationHooked = true;
        vm.ButtonActivated += FlashActivated;   // Task 8
    }

    private bool _activationHooked;

    private void BindButton(ToolbarButtonViewModel btn)
    {
        var id = btn.Id;

        if (_buttons.ContainsKey(id))
        {
            SwapIcon(id, btn.IconKey);
            if (id == "SelectionMode")
                SetSelectionModeState(SelectionModeIcons.ToMode(btn.IconKey));
        }

        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(ToolbarButtonViewModel.DwellProgress))
            {
                if (_buttons.TryGetValue(id, out var b)) b.UpdateProgress(btn.DwellProgress);
            }
            else if (e.PropertyName == nameof(ToolbarButtonViewModel.IsHighlighted))
            {
                ApplyAcquiredState(id, btn.IsHighlighted);   // Task 5
            }
            else if (e.PropertyName == nameof(ToolbarButtonViewModel.IconKey))
            {
                SwapIcon(id, btn.IconKey);
                if (id == "SelectionMode")
                    SetSelectionModeState(SelectionModeIcons.ToMode(btn.IconKey));
            }
        };

        btn.PropertyChanged += handler;
        _subs.Add((btn, handler));
    }

    /// <summary>For tests: how many progress arcs are currently drawn.</summary>
    internal int ProgressVisibleCountForTest =>
        ButtonCanvas.Children.OfType<Path>().Count(p => p.Visibility == Visibility.Visible);
```

Delete `ProgressBarHeight` and every `progressBar` local.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarButtonVisualTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs tests/
git commit -m "feat(toolbar): dwell progress as a sweeping arc, not a 4px edge bar"
```

---

## Task 5: The acquired state

`ToolbarButtonViewModel.IsHighlighted` exists and — verified by grep — has never been written or read anywhere. Bind it: on gaze entry the rim goes to `BorderFocus` and the fill to `SurfaceLight`, so you know you are on target before the arc means anything.

**Files:** `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`, `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, both test projects

- [ ] **Step 1: Write the failing view-model test**

```csharp
    [Fact]
    public void TargetProgress_RaisesAndClearsIsHighlighted()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var button = vm.Buttons.Single(b => b.Id == "Pause");

        Check.That(button.IsHighlighted).IsFalse();
        vm.SimulateTargetProgress("Pause", 0.02);
        Check.That(button.IsHighlighted).IsTrue();
        vm.SimulateTargetProgress("Pause", 0.0);
        Check.That(button.IsHighlighted).IsFalse();
    }

    [Fact]
    public void TargetActivated_ClearsIsHighlightedWithTheProgress()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var button = vm.Buttons.Single(b => b.Id == "Pause");

        vm.SimulateTargetProgress("Pause", 0.9);
        vm.SimulateButtonActivated("Pause");

        Check.That(button.DwellProgress).IsEqualTo(0.0);
        Check.That(button.IsHighlighted).IsFalse();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~IsHighlighted"`
Expected: FAIL, not a build failure — Task 4 already added `SimulateTargetProgress` as a stub, so this compiles and fails on the assertion: `IsHighlighted` is never written, so it stays false.

- [ ] **Step 3: Write the view-model half**

```csharp
    /// <summary>For testing: simulate dwell progress without gaze.</summary>
    /// <param name="buttonId">The button id.</param>
    /// <param name="progress">Progress in 0..1.</param>
    public void SimulateTargetProgress(string buttonId, double progress)
        => OnTargetProgress(buttonId, progress);

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (!_buttonLookup.TryGetValue(targetId, out var btn))
            return;

        btn.DwellProgress = progress;
        // Derived rather than separately plumbed. DwellDetector fires no progress for the FIRST
        // sample inside a target (DwellDetector.cs:224-228) and a non-zero value from the second
        // on (lines 229-233); it fires exactly 0 on leave (line 253) and on activation (line 276).
        // So "progress > 0" is "gaze is on this target", one gaze sample (~11 ms) after entry.
        btn.IsHighlighted = progress > 0;
    }
```

and in `OnTargetActivated`:

```csharp
        if (_buttonLookup.TryGetValue(targetId, out var btn))
        {
            btn.DwellProgress = 0;
            btn.IsHighlighted = false;
        }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~IsHighlighted"`
Expected: PASS.

- [ ] **Step 5: Write the failing view test**

```csharp
    [StaFact]
    public void AcquiredState_LiftsTheFillAndTheRim()
    {
        var window = NewInitialisedToolbar();
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.DataContext = vm;
        window.BindToViewModel();

        var button = vm.Buttons.Single(b => b.Id == "Pause");
        var face = Faces(window).OrderBy(Canvas.GetLeft).First();

        Check.That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceMedium);

        button.IsHighlighted = true;
        Check.That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceLight);
        Check.That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.BorderFocus);

        button.IsHighlighted = false;
        Check.That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceMedium);
    }
```

- [ ] **Step 6: Write the view half**

```csharp
    /// <summary>
    /// The discrete "gaze is on this target" state, distinct from the continuous arc. Fill and rim
    /// only — never a progress-proportional wash over the fill, which collapsed a keyboard key's
    /// label from 10.42:1 to 1.08:1 and is guarded by DwellWashCensusTests.
    /// </summary>
    private void ApplyAcquiredState(string id, bool acquired)
    {
        if (!_buttons.TryGetValue(id, out var b)) return;

        b.Background.Fill = acquired ? Tokens.BrushSurfaceLight : FillFor(id);
        b.Background.Stroke = acquired ? Tokens.BrushBorderFocus : RimFor(id);
    }

    /// <summary>The at-rest fill. SelectionMode overrides it to carry its mode.</summary>
    private Brush FillFor(string id) =>
        id == "SelectionMode" ? _selectionModeBrush : Tokens.BrushSurfaceMedium;

    private Brush _selectionModeBrush = Tokens.BrushSurfaceMedium;

    public void SetSelectionModeState(SelectionMode mode)
    {
        _selectionModeBrush = mode switch
        {
            SelectionMode.ManualSelection => Tokens.BrushToggleOn,
            SelectionMode.AutoSelection => Tokens.BrushSurfaceKeySwitch,
            _ => Tokens.BrushSurfaceMedium
        };

        if (_buttons.TryGetValue("SelectionMode", out var b))
            b.Background.Fill = _selectionModeBrush;
    }
```

Pass `fill: FillFor(id)` in `AddButton`'s `DwellArcButton.Create` call.

- [ ] **Step 7: Run to verify it passes, then commit**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarButtonVisualTests"
git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs tests/
git commit -m "feat(toolbar): show an acquired state before the dwell commits"
```

---

## Task 6: The provider indicator leaves the target band

It is the same size, radius and margin as a button, sits inside the target band, and is not dwellable — so it reads as a button that is broken. And `AddIndicatorElement` is not idempotent — calling `SetProviderIndicator` twice appends a second badge. Latent today rather than live: the settings-save path goes through `Initialize` (`SettingsUiCoordinator.cs:172`), which clears the panel before re-adding, and `SetProviderIndicator` has one caller. The test below exercises it as a code property, not a reproduction.

> **Motivation corrected.** An earlier draft claimed the indicator shifts every learned button position by 72 px. It does not: `SetProviderIndicator` has exactly one caller (`OverlayManager.cs:108`), which runs immediately after `Toolbar.Initialize` and before the first `SetMode` centring (`App.xaml.cs:467`), and the settings-save path re-runs `Initialize` then `SetMode`. The indicator always exists before the strip is centred. The hazard is structural — nothing keeps chrome out of the band — not live.

**Files:** `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [StaFact]
    public void TheProviderIndicator_IsNotInTheBand_AndNeverDuplicates()
    {
        var window = NewInitialisedToolbar();

        window.SetProviderIndicator(GazeProviderType.StreamEngine);
        Check.WithCustomMessage("The indicator must not add a face to the button band.")
            .That(Faces(window)).HasSize(6);
        var afterOne = window.IndicatorChildCountForTest;

        window.SetProviderIndicator(GazeProviderType.Mouse);
        Check.WithCustomMessage("Setting the indicator twice appended a second one.")
            .That(window.IndicatorChildCountForTest).IsEqualTo(afterOne);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TheProviderIndicator"`
Expected: FAIL — the indicator is added to the band, and doubles.

- [ ] **Step 3: Rewrite the indicator**

```csharp
    internal int IndicatorChildCountForTest => IndicatorPanel.Children.Count;

    private void AddIndicatorElement(GazeProviderType providerType)
    {
        // Idempotent: Initialize also calls this when _providerType is already set, which
        // previously appended a second indicator.
        IndicatorPanel.Children.Clear();

        var key = providerType switch
        {
#pragma warning disable CS0618 // Type or member is obsolete
            GazeProviderType.InteractionLib => ToolbarIconKey.ProviderEye,
#pragma warning restore CS0618
            GazeProviderType.Mouse => ToolbarIconKey.ProviderMouse,
            _ => ToolbarIconKey.ProviderEye,
        };

        var size = _buttonSize * ContainerRatio.Small;
        var geometry = ToolbarIcons.For(key);
        var scale = size / ActionIcons.GridSize;

        var icon = new Path
        {
            Data = geometry.Stroke,
            Stroke = Tokens.BrushTextDim,
            StrokeThickness = ActionIcons.GridStrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = new ScaleTransform(scale, scale),
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var rule = new Border
        {
            Width = _vertical ? _buttonSize * ContainerRatio.Dominant : Tokens.BorderThicknessStandard,
            Height = _vertical ? Tokens.BorderThicknessStandard : _buttonSize * ContainerRatio.Dominant,
            Background = Tokens.BrushSurfaceMedium,
            Margin = _vertical ? new Thickness(0, ButtonMargin, 0, 0)
                               : new Thickness(ButtonMargin, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        IndicatorPanel.Children.Add(icon);
        IndicatorPanel.Children.Add(rule);
    }

    public void SetProviderIndicator(GazeProviderType providerType)
    {
        _providerType = providerType;
        AddIndicatorElement(providerType);
    }
```

`SetMode`'s axis branch (Task 3, Step 6) already re-runs `Initialize`, which re-adds the indicator with the correct orientation.

- [ ] **Step 4: Run to verify it passes, then commit**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarButtonVisualTests"
git add src/EyeCtrl.App/Overlays tests/
git commit -m "fix(toolbar): move the provider indicator out of the dwell-target band"
```

---

## Task 7: Reset is visibly different, and harder to overshoot into

Most of this arrived free in Task 3 — `RimFor`, `ArcBrushFor` and `IconBrushFor` already special-case `ResetAdaptation`, and `LayOutBand` already inserts `ResetGap`. This task adds the separator and the tests.

**Files:** `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [StaFact]
    public void Reset_CarriesTheWarningHue_AndExtraClearance()
    {
        var window = NewInitialisedToolbar();
        var faces = Faces(window).OrderBy(Canvas.GetLeft).ToList();
        var reset = faces.Last();

        Check.That(((SolidColorBrush)reset.Stroke).Color).IsEqualTo(Tokens.AccentWarning);

        var lefts = faces.Select(Canvas.GetLeft).ToList();
        var gaps = lefts.Zip(lefts.Skip(1), (a, b) => b - a).ToList();
        Check.WithCustomMessage("Reset is the one adjacency where an overshoot is unrecoverable.")
            .That(gaps.Last()).IsStrictlyGreaterThan(gaps.First());

        var rule = ButtonCanvas(window).Children.OfType<System.Windows.Shapes.Line>().Single();
        var prevEdge = Canvas.GetLeft(faces[^2]) + ButtonSize;
        var resetEdge = Canvas.GetLeft(reset);

        // Position, not just existence: deriving the rule from the pre-gap centre put it 48 px
        // inside the Reset disc, and a count-only assertion was blind to it.
        Check.WithCustomMessage($"The rule is at {rule.X1}, outside the gap {prevEdge}..{resetEdge}.")
            .That(rule.X1 > prevEdge && rule.X1 < resetEdge).IsTrue();
        Check.That(rule.X1).IsCloseTo((prevEdge + resetEdge) / 2, 0.5);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Reset_Carries"`
Expected: FAIL on the separator — none exists.

- [ ] **Step 3: Add the separator**

A `Line` rather than a `Border`, so `Faces` and the icon-path count in the other tests cannot pick it up:

```csharp
    /// <summary>
    /// A hairline between the ordinary buttons and the destructive one. NOT protection — an
    /// accidental Reset is still unrecoverable; the confirm step is the RiskTier follow-up plan.
    /// Do not describe this button as protected until that ships.
    /// </summary>
    private void AddSeparator(double along)
    {
        var half = _buttonSize * ContainerRatio.Dominant / 2;
        var across = Pitch / 2;

        var line = new Line
        {
            Stroke = Tokens.BrushSurfaceMedium,
            StrokeThickness = Tokens.BorderThicknessStandard,
            IsHitTestVisible = false,
            X1 = _vertical ? across - half : along,
            X2 = _vertical ? across + half : along,
            Y1 = _vertical ? along : across - half,
            Y2 = _vertical ? along : across + half,
        };

        ButtonCanvas.Children.Add(line);
    }
```

`UpdateTargetPositions` iterates `_buttons`, not the canvas, so the separator never becomes a dwell target.

- [ ] **Step 4: Run to verify it passes, then commit**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarButtonVisualTests"
git add src/EyeCtrl.App/Overlays tests/
git commit -m "feat(toolbar): mark and separate the one unrecoverable action"
```

---

## Task 8: Fired confirmation

On activation the view-model zeroes `DwellProgress`, the arc collapses, and nothing says the button fired. For Windows and Settings the effect happens somewhere the user is not looking.

**Files:** `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`, `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, tests

- [ ] **Step 1: Write the failing test**

```csharp
    [StaFact]
    public void Activation_FlashesTheSuccessColour_ThenReturnsToRest()
    {
        var window = NewInitialisedToolbar();
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.DataContext = vm;
        window.BindToViewModel();

        var face = Faces(window).OrderBy(Canvas.GetLeft).First();

        vm.SimulateButtonActivated("Pause");
        Check.That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfacePrediction);
        Check.That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.AccentSuccess);

        window.EndFlashForTest("Pause");
        Check.That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceMedium);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Activation_Flashes"`
Expected: build failure — `EndFlashForTest` does not exist. `ButtonActivated` and `FlashActivated` do exist by now, as the stubs Task 4 Step 2 required; this task fills in their real bodies. Once `EndFlashForTest` is added, the test compiles and fails on the fill assertion, because the stub `FlashActivated` recolours nothing.

- [ ] **Step 3: Implement**

In `ToolbarViewModel`:

```csharp
    /// <summary>Raised after a button's dwell completes, for view-side activation feedback.</summary>
    public event Action<string>? ButtonActivated;
```

invoked at the end of `OnTargetActivated`, after the message send: `ButtonActivated?.Invoke(targetId);`

In `ToolbarWindow`:

```csharp
    /// <summary>How long an activation confirmation is held, in milliseconds.</summary>
    private const int FlashHoldMs = 250;

    private readonly Dictionary<string, DispatcherTimer> _flashTimers = new();

    /// <summary>
    /// Holds a success-coloured face on <paramref name="id"/> for <see cref="FlashHoldMs"/>.
    /// Without it, activation is indistinguishable from a dwell abandoned at 99% — the arc
    /// collapses either way — and Windows and Settings act where the user is not looking.
    /// </summary>
    /// <param name="id">The button that fired.</param>
    public void FlashActivated(string id)
    {
        if (!_buttons.TryGetValue(id, out var b)) return;

        b.Background.Fill = Tokens.BrushSurfacePrediction;
        b.Background.Stroke = Tokens.BrushAccentSuccess;

        if (_flashTimers.TryGetValue(id, out var running)) running.Stop();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FlashHoldMs) };
        timer.Tick += (_, _) => EndFlashForTest(id);
        _flashTimers[id] = timer;
        timer.Start();
    }

    /// <summary>Ends the flash immediately. Named for tests; also the timer's callback.</summary>
    /// <param name="id">The button to restore.</param>
    public void EndFlashForTest(string id)
    {
        if (_flashTimers.Remove(id, out var timer)) timer.Stop();
        ApplyAcquiredState(id, acquired: false);
    }
```

Add `using System.Windows.Threading;`. The `vm.ButtonActivated += FlashActivated` hook is already in Task 4's `BindToViewModel`.

- [ ] **Step 4: Run to verify it passes, then commit**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarButtonVisualTests"
git add src/ tests/
git commit -m "feat(toolbar): confirm that a dwell actually fired"
```

---

## Task 9: Census reconciliation and full verification

- [ ] **Step 1: Run the full headless suite**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

Read failures as `Fehler <FQN>`. Expected: `ContainerRatioCensusTests`.

- [ ] **Step 2: Reconcile the container-ratio census**

`ToolbarWindow.xaml.cs` previously held two `ContainerRatio.Large` references, both `FontSize` on Unicode glyph `TextBlock`s (lines 78 and 121). Both are gone — `DwellArcButton` sizes the icon internally by `ContainerRatio.Dominant`. The file now reads `ContainerRatio.Small` (the provider badge) and `ContainerRatio.Dominant` (the separator length and the indicator rule). Count them:

```bash
grep -c "ContainerRatio\." src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs
grep -o "ContainerRatio\.[A-Za-z]*" src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs | sort | uniq -c
```

Write the census entry to the counts that command actually reports, with the rationale the test's doc comment demands:

```csharp
            ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs"] =
            [
                ("Small", 1, "the provider indicator, which is chrome beside the band rather "
                    + "than an icon filling its own cell."),
                ("Dominant", 3, "the rule before Reset, and the indicator rule's two ternary "
                    + "arms — each sized to read as a divider across most of a button's height "
                    + "without matching it."),
            ],
```

> The old entry claimed Large ×2 for "the toolbar symbols … this file's own ButtonSymbolSizeRatio = 0.4 is where Large came from". Large is still the bucket's founding observation and other files still read it; the toolbar simply no longer sizes a glyph, because it no longer draws one. Say that in the entry rather than deleting the history.

- [ ] **Step 3: Update the drift guard's description**

`ToolbarButtonDriftTests` still passes — both arrays kept the `new (string Id, string Symbol)[]` declaration text. Update the comment on `ButtonEntry` and add to the class summary:

```csharp
/// The second tuple element has been a ToolbarIconKey member name, not a Unicode symbol, since
/// the 2026-08-29 gaze-accessibility change. The declaration text this guard keys on was left
/// alone deliberately so the guard kept working across it; if it is ever renamed, update this
/// file rather than deleting it. The guard constrains only the id, so the key itself is pinned
/// separately by ToolbarIconKeyBridgeTests.
```

- [ ] **Step 4: Check the arc-button suites still describe reality**

The toolbar becomes `DwellArcButton`'s fourth caller, at radius 60 — a radius none of `DwellArcButtonGlyphSizeTests`, `DwellArcButtonIconTests`, `DwellArcButtonRimTests` or `DwellArcButtonArcWeightTests` currently walks. Those test the control, not a caller census, so none should fail. But several doc comments say "the three callers" (`DwellArcButtonIconTests.cs:132`, `DwellArcButton.cs:150-156`). Update that count to four and name the toolbar, or the next reader is told a false thing about the code.

- [ ] **Step 5: Re-run the suite**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

Expected: green apart from the three known load-dependent flakes. Do not skip `EyeCtrl.App.Tests` — CI runs its `Category!=UI` subset and every toolbar census lives there.

- [ ] **Step 6: Check the change scope**

```
detect_changes({scope: "compare", base_ref: "main"})
```

Expected: `ToolbarWindow`, `ToolbarViewModel`, `ToolbarButtonViewModel`, `SelectionModeIcons`, `ToolbarIcons`. Anything touching `DwellArcButton`, `DwellDetector`, `ToolbarAutoHideController` or `EventCoordinator` means this drifted into a follow-up's territory — stop and report.

- [ ] **Step 7: Commit**

```bash
git add tests/EyeCtrl.App.Tests tests/EyeCtrl.Controls.Tests
git commit -m "test(toolbar): reconcile the ratio census and caller counts"
```

---

## Task 10: Look at it, once, on the real tracker

Every test above is headless by design. Three things can only be judged by a person using it, and one is the reason the labels came off.

**Simulate no input. Ask before launching. Keep the run short.**

- [ ] **Step 1: Ask, then launch**

```bash
dotnet run --project src/EyeCtrl.App
```

- [ ] **Step 2: Check what tests cannot**

1. **Are the three selection-mode icons distinguishable at 54 px?** Slashed ring, crosshair ring, ring-around-control. With no label this is the button's only state channel besides fill colour — if two read alike at a glance, the encoding has failed.
2. **Does the 4.8 px arc read as progress at 120 DIP?** It is 4% of the button by design and pinned by `DwellArcButtonArcWeightTests`. If it is too thin here, that is a conversation about `ArcThicknessRatio` across all four callers, not a toolbar-local override.
3. **Is `BorderSubtle` enough as a rim?** It computes to 3.35:1 on `SurfaceDark` from the token hex values, but both surfaces ship at `0xFA` alpha over live desktop content, so the real ratio drifts. If the boundary disappears over a bright window, the rim wants `BorderFocus` at rest.
4. **Does `Toolbar_Button_Pause` still resolve in UIA?** The zero-opacity anchor is the one claim no headless test can check — `LogicalTreeHelper` sees it regardless of whether UIA does. Run the UI suite once, or confirm via an accessibility inspector.

- [ ] **Step 3: Record what you found**

Append findings to `docs/design/toolbar-accessibility/`. Open follow-ups for anything that failed; do not patch items 2 or 4 inside this plan.

---

## Self-Review

**Spec coverage against `Main.dc.html` and `Anatomy.dc.html`:**

| Design claim | Task |
|---|---|
| The shape is the target (disc, zero dead pixels) | 3 — `Ellipse` at the dwell diameter |
| Progress where the eye already is, never washing the icon | 4 — sweeping arc, as drawn in the artboard |
| Acquired before committed (`IsHighlighted` bound at last) | 5 |
| Icons that read at a glance, no font-coverage risk | 1, 3 |
| Mode needs its own icon (three distinct) | 1, 2 |
| Risk is visible and separated; dwell unchanged | 7 |
| Rim so the target boundary is findable | 3 — `DwellArcButton`'s default stroke, plus its halo |
| Provider indicator is chrome, not a target | 6 |
| Fired state (Anatomy) | 8 |
| Cooling-down state (Anatomy) | **not covered.** `DwellDetector` exposes no in-cooldown signal, so this needs detector work. Deferred, not dropped. |

**Type consistency:** `IconKey` (string, a `ToolbarIconKey` member name) is produced by `SelectionModeIcons.ForMode` and `ToolbarViewModel`, consumed by `ToolbarWindow.ParseIconKey`, and pinned at the seam by `ToolbarIconKeyBridgeTests`. `_buttons` is `Dictionary<string, DwellArcButton>` from Task 3 onward. `RimFor`, `ArcBrushFor`, `IconBrushFor`, `FillFor`, `ApplyAcquiredState`, `SwapIcon`, `Pitch`, `ResetGap` and `_vertical` are each defined once and referenced consistently by Tasks 4–8.

**Ordering:** Task 4's test uses `SimulateTargetProgress`, which Task 5 adds — add it early if executing out of order (the step says so). Task 7 depends on `RimFor`/`ArcBrushFor`/`LayOutBand`'s `ResetGap` from Task 3. Task 8's `ButtonActivated` hook lives in Task 4's `BindToViewModel`. Task 9 runs last. Task 2's commit intentionally leaves `EyeCtrl.App` uncompilable until Task 3 — the tasks are not individually buildable, only individually reviewable.

**Guards this plan touches, and the ones it deliberately does not:** touched — `ContainerRatioCensusTests`, `ToolbarButtonDriftTests`, and four `DwellArcButton*Tests` doc comments. Untouched by design — `CornerRadiusClampTests` (no corner-radius token is written; `DwellArcButton` uses an `Ellipse`), `DwellWashCensusTests` (the toolbar never washed and does not adopt `DwellOutlineOverlay`), `RawGeometryLiteralTests`, `RawColourLiteralTests`, `LegacyColourLiteralTests`, `OverlayFontTests`, `HaloZOrderTests`, `OverlayInventoryDriftTests`, `StaticTokenCaptureTests`, `GlyphSizeCouplingTests` (its four sites are const-sized containers; the toolbar's is runtime-sized).
