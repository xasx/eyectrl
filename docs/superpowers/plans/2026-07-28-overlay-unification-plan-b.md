# Overlay Design Unification — Plan B (StrokeHalo Contrast Guarantee) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the seven transparent overlays a contrast guarantee that holds on any desktop backdrop, replacing single-colour strokes that currently fall to 1.37:1 over a white document.

**Architecture:** Every accent mark on a `Background="Transparent"` overlay is drawn twice — a dark `StrokeHalo` shape beneath or around the `Accent` shape. The visible feature becomes the *internal edge* between the two, which is fixed at ≥15.78:1 regardless of what is behind the window. Two cases: **stroked** shapes get a halo under-stroke at `StrokeThickness + 2`; **filled** shapes get a halo outline ring. This is a rendering change, not a colour swap — which is why it is a separate plan from A.

**Tech Stack:** C# / .NET 10 / WPF, xUnit v3 + NFluent, Python 3 for the validator.

**Source spec:** `docs/design/overlay-unification-spec.md` §4.1.3 (the rule and its two cases), §9.5 (measured backdrop table), §5 Phase 5 steps 33–38, plus the `DwellArcButton` halo from step 5.

**Prerequisite: Plan A must be complete.** Verify before Task 1 rather than
discovering it as a wall of compile errors:

```bash
git rev-parse --abbrev-ref HEAD                      # -> docs/overlay-unification
test -f src/EyeCtrl.Controls/Theme/Tokens.cs && echo "Plan A Task 1 done"
grep -c 'BrushStrokeHalo' src/EyeCtrl.Controls/Theme/OverlayStyles.xaml   # -> 1
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo                 # -> PASS
```

All four must succeed. This plan consumes `Tokens.BrushStrokeHalo` and
`Tokens.BrushAccent`, and assumes `LegacyColourLiteralTests.StillPending` has been
pruned to exactly the four Plan B entries.

**Location matters:** the spec and `tools/palette_validate.py` this plan cites live
on branch `docs/overlay-unification`, not on `main`.

**Why this matters more than it looks.** These are the app's *primary dwell feedback* — the cursor ring, the activation arc, the gaze dot, the struggle cue. Measured against raw desktop:

| | over black | over mid grey | over white | over similar-hue content |
|---|---|---|---|---|
| Lime ring (today) | 15.30 | **2.88** | **1.37** | — |
| Accent alone | 16.83 | **3.16** | **1.25** | **1.33** |
| Accent + StrokeHalo | 16.83 | **15.78** | **19.69** | **15.78** |
| Cue badge `#FFB300` alone | 11.70 | **2.20** | **1.79** | **1.06** |

The cue badge at 1.06:1 is invisible over amber content — on the one overlay whose entire job is getting attention when the user is already struggling.

---

## File Structure

**New files:**

| Path | Responsibility |
|---|---|
| `tests/EyeCtrl.App.Tests/HaloContrastTests.cs` | Proves the accent/halo edge clears 7:1 on every backdrop, and that no palette-legal cursor colour breaks it. |
| `tests/EyeCtrl.App.Tests/HaloZOrderTests.cs` | Parses each transparent overlay's XAML and asserts the halo element is declared **before** its accent element. This is the only automated guard against the plan's main hazard. |

**Modified files:** `DwellArcButton.cs` (Controls) · `ActivationButtonWindow.xaml`, `CursorCircleWindow.xaml` + `.xaml.cs`, `ManualSelectionGazeIndicatorWindow.xaml`, `ScrollOverlayWindow.xaml`, `DwellFeedbackOverlay.xaml`, `CueOverlayWindow.xaml` (App) · `LegacyColourLiteralTests.cs`.

**Two hazards to hold in mind throughout:**

1. **Z-order.** If the halo is declared *after* the accent shape it paints on top, rendering as a dark ring on every backdrop. Obvious to the eye, invisible to every headless test — hence `HaloZOrderTests`.
2. **The gaze path.** `CursorCircleWindow` and `ActivationButtonWindow` redraw at gaze rate. Adding one `Ellipse`/`Path` per overlay is fine; allocating a brush per frame is not. Use `Tokens.Brush*` (already frozen) and never `new SolidColorBrush(...)` inside an update method.

---

## Task 1: Prove the halo mechanism before applying it

**Files:**
- Create: `tests/EyeCtrl.App.Tests/HaloContrastTests.cs`

Establish the guarantee as an executable claim first, so every later task has something to satisfy.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/HaloContrastTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The §4.1.3 contrast guarantee, as an executable claim. A single-colour stroke
/// on a transparent overlay cannot hold contrast against arbitrary desktop
/// content; a two-tone stroke can, because the visible feature becomes the
/// internal accent/halo edge rather than the accent/backdrop edge.
/// </summary>
public class HaloContrastTests
{
    /// <summary>Backdrops a transparent overlay may be drawn over. Mirrors spec §9.5.</summary>
    public static readonly string[] Backdrops =
    [
        "#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF",
        "#66D9E8", "#A0E060", "#FF5C7A", "#1A1A2E",
    ];

    private const double EdgeFloor = 7.0;

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>
    /// The strongest edge available to the eye: accent-vs-halo (backdrop
    /// independent), accent-vs-backdrop, or halo-vs-backdrop.
    ///
    /// Taking the maximum is deliberate and is exactly what the guarantee claims:
    /// the mark is discernible if ANY of its edges is strong. It does NOT claim the
    /// accent-vs-backdrop edge is strong - that one is often weak, which is the
    /// entire reason the halo exists.
    /// </summary>
    private static double StrongestVisibleEdge(Color mark, Color halo, Color backdrop) =>
        Math.Max(Contrast(mark, halo),
            Math.Max(Contrast(mark, backdrop), Contrast(halo, backdrop)));

    [Fact]
    public void AccentWithHalo_ClearsTheFloorOnEveryBackdrop()
    {
        foreach (var b in Backdrops)
        {
            var edge = StrongestVisibleEdge(Tokens.Accent, Tokens.StrokeHalo, Parse(b));
            Check.WithCustomMessage(
                    $"Accent+StrokeHalo reaches only {edge:F2}:1 over {b}; "
                    + $"§4.1.3 requires >= {EdgeFloor}.")
                .That(edge).IsStrictlyGreaterThan(EdgeFloor);
        }
    }

    [Fact]
    public void AccentAlone_FailsOnAtLeastOneBackdrop()
    {
        // The reason the halo exists. If this ever passes, the guarantee has
        // become unnecessary and §4.1.3 should be revisited.
        var worst = double.MaxValue;
        foreach (var b in Backdrops)
            worst = Math.Min(worst, Contrast(Tokens.Accent, Parse(b)));

        Check.WithCustomMessage(
                $"Accent alone manages {worst:F2}:1 at worst — if that is above "
                + $"{EdgeFloor} the halo is redundant.")
            .That(worst).IsStrictlyLessThan(EdgeFloor);
    }

    [Fact]
    public void CueBadgeColour_NeedsTheHaloToo()
    {
        // The cue badge is AccentWarning on a filled shape (spec §4.1.3 case 2).
        var worstAlone = double.MaxValue;
        foreach (var b in Backdrops)
            worstAlone = Math.Min(worstAlone, Contrast(Tokens.AccentWarning, Parse(b)));
        Check.That(worstAlone).IsStrictlyLessThan(EdgeFloor);

        foreach (var b in Backdrops)
            Check.That(StrongestVisibleEdge(Tokens.AccentWarning, Tokens.StrokeHalo, Parse(b)))
                 .IsStrictlyGreaterThan(EdgeFloor);
    }

    [Fact]
    public void EveryColourPickerPreset_KeepsTheGuarantee()
    {
        // CursorSettings.CircleColor is user-selectable, so the guarantee must
        // survive any preset — not just the default.
        foreach (var preset in EyeCtrl.Controls.DwellColorPicker.Presets)
        {
            var mark = Parse(preset.Hex);
            foreach (var b in Backdrops)
                Check.WithCustomMessage(
                        $"Preset '{preset.Name}' ({preset.Hex}) breaks the halo "
                        + $"guarantee over {b}.")
                    .That(StrongestVisibleEdge(mark, Tokens.StrokeHalo, Parse(b)))
                    .IsStrictlyGreaterThan(EdgeFloor);
        }
    }
}
```

- [ ] **Step 2: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~HaloContrastTests" --nologo`

Expected: PASS, 4 tests. These assert properties of the *palette*, which Plan A already delivered — the point is to pin the guarantee before any XAML depends on it, and to make `AccentAlone_FailsOnAtLeastOneBackdrop` document why the mechanism exists.

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/HaloContrastTests.cs
git commit -m "test(theme): pin the StrokeHalo contrast guarantee as an executable claim"
```

---

## Task 2: The z-order guard

**Files:**
- Create: `tests/EyeCtrl.App.Tests/HaloZOrderTests.cs`

An inverted halo is the failure mode most likely to ship, because it looks like a deliberate dark ring and no functional test exercises rendering order. This test reads the XAML and asserts declaration order.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/HaloZOrderTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NFluent;
using Xunit;

/// <summary>
/// In WPF, later siblings paint on top. A StrokeHalo element declared AFTER its
/// accent element hides the accent and renders as a dark ring on every backdrop —
/// visually obvious, invisible to every other test. This guard reads declaration
/// order straight out of the XAML.
/// </summary>
public class HaloZOrderTests
{
    /// <summary>
    /// overlay XAML (repo-relative) -> (halo element x:Name, accent element x:Name).
    ///
    /// Starts EMPTY and grows one entry per task, so the branch is green at every
    /// commit rather than red from here until the last overlay lands. Add each entry
    /// in the same commit that adds its halo.
    /// </summary>
    public static readonly Dictionary<string, (string Halo, string Accent)> HaloedOverlays = new()
    {
        // Task 3: ["src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml"] = ("CircleHalo", "Circle"),
        // Task 4: ["src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml"] = ("ProgressArcHalo", "ProgressArc"),
        // Task 5: ["src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml"] = ("OuterCircleHalo", "OuterCircle"),
        // Task 5: ["src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml"] = ("VelocityRingHalo", "VelocityRing"),
    };

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(HaloZOrderTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!;
    }

    [Fact]
    public void HaloElement_IsDeclaredBeforeItsAccentElement()
    {
        var root = RepoRoot().FullName;

        foreach (var (relative, names) in HaloedOverlays)
        {
            var xaml = File.ReadAllText(Path.Combine(root, relative));

            var haloAt = xaml.IndexOf($"x:Name=\"{names.Halo}\"", System.StringComparison.Ordinal);
            var accentAt = xaml.IndexOf($"x:Name=\"{names.Accent}\"", System.StringComparison.Ordinal);

            Check.WithCustomMessage($"{relative} has no element named '{names.Halo}'")
                .That(haloAt).IsStrictlyGreaterThan(-1);
            Check.WithCustomMessage($"{relative} has no element named '{names.Accent}'")
                .That(accentAt).IsStrictlyGreaterThan(-1);

            Check.WithCustomMessage(
                    $"{relative}: '{names.Halo}' must be declared BEFORE "
                    + $"'{names.Accent}' or it paints over the accent and renders "
                    + "as a dark ring on every backdrop (spec §4.1.3).")
                .That(haloAt).IsStrictlyLessThan(accentAt);
        }
    }

    [Fact]
    public void FilledCueBadge_HasAHaloOutlineStroke()
    {
        // The filled-shape case: a fill has no stroke to sit under, so it takes an
        // outline ring instead (spec §4.1.3).
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml"));

        Check.WithCustomMessage(
                "CueOverlayWindow's badge is a filled Ellipse and must carry "
                + "Stroke=\"{StaticResource BrushStrokeHalo}\" — at 1.06:1 over "
                + "amber content the unringed badge is invisible.")
            .That(xaml).Contains("BrushStrokeHalo");
    }

    [Fact]
    public void ActivationButton_NeverAllocatesABrush()
    {
        // ActivationButtonWindow redraws at gaze rate and has no user-configurable
        // colour, so it should reach for frozen Tokens brushes only.
        //
        // CursorCircleWindow is deliberately NOT checked: its ring colour comes
        // from CursorSettings.CircleColor, so it must parse a string into a brush.
        // That happens once per settings change, not per frame, and the brush is
        // frozen immediately (see Task 3). Scoping this test to ActivationButton is
        // the decision, not an oversight - do not widen it to CursorCircle without
        // first giving that file a cache keyed on the settings string.
        var path = Path.Combine(RepoRoot().FullName,
            "src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs");

        Check.WithCustomMessage(
                "ActivationButtonWindow must use frozen Tokens brushes, not "
                + "allocate per update - it redraws at gaze rate.")
            .That(File.ReadAllText(path).Contains("new SolidColorBrush", System.StringComparison.Ordinal))
            .IsFalse();
    }

    [Fact]   // enabled by Task 7
    public void DwellFeedbackOverlay_UsesTheAccentBrush()
    {
        // Not a haloed overlay (it is a filled Border, not a stroke on transparency)
        // but it is migrated in Task 7 and would otherwise have no guard at all.
        var path = Path.Combine(RepoRoot().FullName,
            "src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml");

        Check.WithCustomMessage(
                "DwellFeedbackOverlay must reference BrushAccent; if this fails the "
                + "migration in Task 7 was reverted.")
            .That(File.ReadAllText(path)).Contains("BrushAccent");
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~HaloZOrderTests" --nologo`

Expected: PASS. `HaloElement_IsDeclaredBeforeItsAccentElement` passes trivially because `HaloedOverlays` starts empty, and `ActivationButton_NeverAllocatesABrush` already holds.

`FilledCueBadge_HasAHaloOutlineStroke` and `DwellFeedbackOverlay_UsesTheAccentBrush` cover files not yet migrated, so comment out their `[Fact]` attributes with `// enabled by Task 6` and `// enabled by Task 7` and re-enable each in the task that satisfies it.

- [ ] **Step 3: Commit the guard (green)**

```bash
git add tests/EyeCtrl.App.Tests/HaloZOrderTests.cs
git commit -m "test(theme): guard halo z-order and gaze-path allocation"
```

Each of Tasks 3–7 adds its dictionary entry or re-enables its `[Fact]` **in the same commit as the change it guards**, so `dotnet test --filter "Category!=UI"` passes at every point on the branch. That matters because CI runs on anything targeting `main` — a guard committed red would leave the branch failing for most of its life.

---

## Task 3: CursorCircleWindow — the most-looked-at overlay

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml`
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs:31-40`

The ring colour comes from `CursorSettings.CircleColor` at runtime, so the XAML stroke is a **fallback only**. The halo must track the ring's thickness, which is also settings-driven (`CircleThickness`).

**Do not touch** the hidden `TextBlock` with `AutomationProperties.AutomationId="CursorCircle_Ellipse_Main"` — WPF transparent overlays only expose `TextBlock` nodes to UIA, so it is the sole automation anchor for this window.

- [ ] **Step 1: Write the XAML**

Replace the `Canvas` contents of `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml`. `CircleHalo` comes **first** so it paints underneath:

```xml
    <Canvas>
        <!-- Halo first: later siblings paint on top, so this must precede Circle.
             Gives the ring a backdrop-independent edge (spec §4.1.3). -->
        <Ellipse x:Name="CircleHalo"
                 Stroke="{StaticResource BrushStrokeHalo}" StrokeThickness="4"
                 Width="250" Height="250"
                 Canvas.Left="0" Canvas.Top="0"/>
        <Ellipse x:Name="Circle"
                 Stroke="{StaticResource BrushAccent}" StrokeThickness="2"
                 Width="250" Height="250"
                 Canvas.Left="0" Canvas.Top="0"/>
        <!-- Hidden TextBlock as UIA anchor — WPF transparent overlay windows only expose
             TextBlock (ControlType.Text) nodes in the UIA tree; Ellipse/Canvas are collapsed. -->
        <TextBlock AutomationProperties.AutomationId="CursorCircle_Ellipse_Main"
                   Opacity="0" IsHitTestVisible="False"
                   Canvas.Left="0" Canvas.Top="0"/>
    </Canvas>
```

- [ ] **Step 2: Write the code-behind**

In `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs`, add `using EyeCtrl.Controls.Theme;`, then update the settings-application block (currently lines 31–40) so the halo tracks the ring and no brush is allocated:

```csharp
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(settings.CircleColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze();                 // built once per settings change, not per frame
            Circle.Stroke = brush;
        }
        catch (FormatException)
        {
            Circle.Stroke = Tokens.BrushAccent;
        }

        Circle.StrokeThickness = settings.CircleThickness;
        Circle.Opacity = settings.CircleOpacity;

        // Halo is 2px wider on each side so a rim of it shows past the accent ring.
        CircleHalo.Stroke = Tokens.BrushStrokeHalo;
        CircleHalo.StrokeThickness = settings.CircleThickness + 2;
        CircleHalo.Opacity = settings.CircleOpacity;
```

And wherever `Circle.Width`/`Height` are set from `size` (currently lines 46–47), mirror them:

```csharp
        Circle.Width = size;
        Circle.Height = size;
        CircleHalo.Width = size;
        CircleHalo.Height = size;
```

> **On brush allocation — decided, not a loose end.** This file keeps one `new SolidColorBrush`. The ring colour is user-configurable (`CursorSettings.CircleColor`), so a string must become a brush somewhere; doing it in the settings-change path and freezing immediately costs one allocation per settings change, not per frame. The guard is therefore scoped to `ActivationButtonWindow`, which has no configurable colour and must never allocate — see `ActivationButton_NeverAllocatesABrush` in Task 2. Do not widen it to this file without first adding a cache keyed on the settings string.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~HaloZOrderTests" --nologo`

Expected: `HaloElement_IsDeclaredBeforeItsAccentElement` still fails (three overlays to go) but no longer names `CursorCircleWindow`.

- [ ] **Step 4: Prune the allow-list**

In `LegacyColourLiteralTests.StillPending`, delete the `CursorCircleWindow.xaml` entry. Run the full headless suite: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo` → PASS except the two remaining z-order overlays.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs tests/EyeCtrl.App.Tests/HaloZOrderTests.cs
git commit -m "feat(overlay): halo the cursor ring for backdrop-independent contrast"
```

---

## Task 4: ActivationButtonWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml`

The progress arc is a `Path` whose geometry is animated in code-behind. The halo needs its **own** `Path` with its own `PathFigure`/`ArcSegment`, because a single geometry cannot be shared between two `Path` elements with different stroke thicknesses and still be mutated — sharing one `PathGeometry` instance across both is acceptable and cheaper, so do that and give the segments distinct names only where the code-behind needs them.

- [ ] **Step 1: Write the XAML**

Replace the `Canvas` contents of `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml`:

```xml
    <Canvas>
        <!-- Background circle -->
        <Ellipse x:Name="BackgroundCircle"
                 Fill="{StaticResource BrushSurfaceDark}"
                 Width="100" Height="100"
                 Canvas.Left="0" Canvas.Top="0"/>
        <!-- Halo first (spec §4.1.3): 2px wider than the accent arc on each side. -->
        <Path x:Name="ProgressArcHalo" Stroke="{StaticResource BrushStrokeHalo}"
              StrokeThickness="9" Canvas.Left="0" Canvas.Top="0">
            <Path.Data>
                <PathGeometry>
                    <PathFigure x:Name="ArcFigureHalo" StartPoint="50,5" IsClosed="False">
                        <ArcSegment x:Name="ArcSegmentHalo"
                                    Point="50,5" Size="45,45"
                                    IsLargeArc="False" SweepDirection="Clockwise"/>
                    </PathFigure>
                </PathGeometry>
            </Path.Data>
        </Path>
        <!-- Dwell progress arc -->
        <Path x:Name="ProgressArc" Stroke="{StaticResource BrushAccent}"
              StrokeThickness="5" Canvas.Left="0" Canvas.Top="0">
            <Path.Data>
                <PathGeometry>
                    <PathFigure x:Name="ArcFigure" StartPoint="50,5" IsClosed="False">
                        <ArcSegment x:Name="ArcSegment"
                                    Point="50,5" Size="45,45"
                                    IsLargeArc="False" SweepDirection="Clockwise"/>
                    </PathFigure>
                </PathGeometry>
            </Path.Data>
        </Path>
        <!-- Center icon -->
        <Ellipse Fill="{StaticResource BrushTextPrimary}" Width="30" Height="30"
                 Canvas.Left="35" Canvas.Top="35"/>
    </Canvas>
```

- [ ] **Step 2: Mirror the arc sweep in code-behind**

`src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs` drives the arc through the shared static helper `DwellArcButton.UpdateArc`, so the halo needs one extra call rather than duplicated trigonometry. Two places to change.

Lines 70-71, the reset path - add the halo reset immediately after:

```csharp
            ArcSegment.Point = new Point(ButtonCenter, ButtonCenter - ArcRadius);
            ArcSegment.IsLargeArc = false;
            ArcSegmentHalo.Point = new Point(ButtonCenter, ButtonCenter - ArcRadius);
            ArcSegmentHalo.IsLargeArc = false;
```

Line 75, the progress path - add the matching call:

```csharp
        DwellArcButton.UpdateArc(ArcSegment, new Point(ButtonCenter, ButtonCenter), ArcRadius, progress);
        DwellArcButton.UpdateArc(ArcSegmentHalo, new Point(ButtonCenter, ButtonCenter), ArcRadius, progress);
```

Omit either and the halo arc stays at zero length while only the accent arc sweeps - which looks like the halo working at rest and vanishing under dwell.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~HaloZOrderTests" --nologo`

Expected: `ActivationButtonWindow` no longer named; two overlays remain.

- [ ] **Step 4: Prune the allow-list, run the suite**

Delete the `ActivationButtonWindow.xaml` entry from `StillPending`, then `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "feat(overlay): halo the activation progress arc"
```

---

## Task 5: ManualSelectionGazeIndicatorWindow and ScrollOverlayWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml`
- Modify: `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml`

- [ ] **Step 1: ManualSelectionGazeIndicatorWindow**

This one file contains **both** halo cases: `OuterCircle` (line 11) is stroked, `CenterDot` (line 16) is filled. Keep the existing `x:Name`s - the code-behind and `ManualSelectionGazeIndicatorViewModel` bind to them.

`OuterCircle` is stroked, so it takes an under-stroke declared **before** it:

```xml
        <Ellipse x:Name="OuterCircleHalo"
                 Stroke="{StaticResource BrushStrokeHalo}" StrokeThickness="4"
                 Fill="Transparent"/>
        <Ellipse x:Name="OuterCircle"
                 Stroke="{StaticResource BrushAccent}" StrokeThickness="2"
                 Fill="Transparent"/>
```

Copy `OuterCircle`'s existing `Width`/`Height`/`Canvas.*` attributes onto `OuterCircleHalo` verbatim, then keep them in sync: whatever sizes `OuterCircle` (the view model exposes `OuterCircleDiameter`) must size the halo too.

`CenterDot` is filled, so it takes an outline ring instead - no second element:

```xml
        <Ellipse x:Name="CenterDot"
                 Fill="{StaticResource BrushAccent}"
                 Stroke="{StaticResource BrushStrokeHalo}"
                 StrokeThickness="2"/>
```

Preserve every `AutomationProperties.AutomationId` and any hidden `TextBlock` UIA anchor in this file.

- [ ] **Step 2: ScrollOverlayWindow**

Lines 13–18 — crosshairs, cursor circle and anchor dot: `Stroke="White"` / `Fill="White"` → `{StaticResource BrushTextPrimary}`.

Line 19 — the velocity ring gains a halo declared before it:

```xml
        <Ellipse x:Name="VelocityRingHalo" Stroke="{StaticResource BrushStrokeHalo}"
                 StrokeThickness="4" Width="80" Height="80" Opacity="0.8"/>
        <Ellipse x:Name="VelocityRing" Stroke="{StaticResource BrushAccent}"
                 StrokeThickness="2" Width="80" Height="80" Opacity="0.8"/>
```

Any code-behind that positions or resizes `VelocityRing` must do the same to `VelocityRingHalo`.

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~HaloZOrderTests" --nologo`

Expected: `HaloElement_IsDeclaredBeforeItsAccentElement` now PASSES — all four overlays present and correctly ordered.

- [ ] **Step 4: Prune the allow-list, run the suite**

Delete the `ManualSelectionGazeIndicatorWindow.xaml` entry from `StillPending`. Run `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "feat(overlay): halo the gaze indicator and scroll velocity ring"
```

---

## Task 6: CueOverlayWindow — the filled-shape case

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml`

A 40px filled `Ellipse` with no stroke at all, so there is nothing for a halo to sit *under*. It takes an outline ring instead — the second case in §4.1.3, which the rule originally missed.

`#FFB300` sits only ΔE 7.0 from `AccentWarning`, so this is a substitution rather than a redesign. `Opacity` is animated in code-behind and the `AdaptationCue_Badge` UIA anchor must survive.

- [ ] **Step 1: Write the XAML**

In `src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml`, replace the badge (line 14 region):

```xml
        <!-- Amber "struggle" badge; Opacity is animated in code-behind (0 = invisible at rest).
             Filled shapes take a StrokeHalo OUTLINE rather than an under-stroke (spec §4.1.3):
             unringed, this badge reaches 1.06:1 over amber desktop content. -->
        <Ellipse x:Name="Badge"
                 Width="40" Height="40"
                 Canvas.Left="12" Canvas.Top="12"
                 Fill="{StaticResource BrushAccentWarning}"
                 Stroke="{StaticResource BrushStrokeHalo}"
                 StrokeThickness="2"
                 Opacity="0"/>
```

Leave the hidden `TextBlock` UIA anchor exactly as it is.

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~HaloZOrderTests" --nologo`

Expected: PASS, 3 tests — `FilledCueBadge_HasAHaloOutlineStroke` now finds `BrushStrokeHalo`.

- [ ] **Step 3: Prune the allow-list**

Delete the `CueOverlayWindow.xaml` entry from `StillPending`. `StillPending` should now be **empty** — leave the empty dictionary and both tests in place; they guard against any legacy literal returning.

- [ ] **Step 4: Run the suite**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "feat(overlay): ring the struggle cue badge (filled-shape halo case)"
```

---

## Task 7: DwellFeedbackOverlay and the DwellArcButton halo arc

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml:7-8`
- Modify: `src/EyeCtrl.Controls/DwellArcButton.cs`

The `DwellArcButton` change is §5 step 5's halo, deferred from Plan A. It is the highest-leverage item in this plan: `ActionMenuWindow` and `EdgeButtonBarWindow` are both `Background="Transparent"` and get their guarantee entirely from this one control, without either file being touched.

- [ ] **Step 1: DwellFeedbackOverlay.xaml**

Line 8 — border and fill both move onto the accent; the fill keeps a low alpha so the highlight stays translucent:

```xml
    <Border x:Name="ProgressBorder" CornerRadius="4" BorderThickness="2"
            BorderBrush="{StaticResource BrushAccent}"
            Background="#1AA5F3FC" Visibility="Hidden">
```

> `#1AA5F3FC` is `Accent` at 10% alpha. This is the one place in the migration where a literal is unavoidable: WPF has no XAML syntax for "this brush at N% alpha." Add the comment `<!-- Accent at 10% alpha; no XAML syntax exists for deriving this from BrushAccent -->` above it so a future reader does not mistake it for drift.

- [ ] **Step 2: DwellArcButton halo arc**

`src/EyeCtrl.Controls/DwellArcButton.cs` builds the arc at lines 56-71 (`arcSegment` then `figure` then `geometry` then `progressPath`), exposes the segment as `public ArcSegment Arc { get; }` (line 18), mutates it at line 116, and adds children in order at lines 134-137.

Four coordinated changes.

**1. Build a second arc in `Create`**, immediately before the existing `progressPath` block. The geometry must be duplicated, not shared - two `Path` elements need independent thickness and independent mutation:

```csharp
        // Halo under-stroke (spec 4.1.3). ActionMenu and EdgeButtonBar are both
        // transparent overlays, so this is where their arcs get a contrast
        // guarantee - neither window file needs to change.
        var haloSegment = new ArcSegment
        {
            Point = new Point(center.X, center.Y - arcRad),
            Size = new Size(arcRad, arcRad),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
        };
        var haloFigure = new PathFigure
        {
            StartPoint = new Point(center.X, center.Y - arcRad),
            IsClosed = false,
        };
        haloFigure.Segments.Add(haloSegment);
        var haloGeometry = new PathGeometry();
        haloGeometry.Figures.Add(haloFigure);
        var haloPath = new Path
        {
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = arcThickness + 2,
            Data = haloGeometry,
        };
```

The snippet above is the verified content of lines 56-76 with `arcThickness + 2` substituted: `Point`, `Size`, `IsLargeArc` and `SweepDirection` on the segment; `StartPoint` and `IsClosed` on the figure; `Data`, `Stroke` and `StrokeThickness` on the path. Diff it against the adjacent block before moving on — if the accent arc has gained a property since `b8295b6`, mirror it.

**2. Carry both through the constructor.** Add `HaloArc` and `HaloPath` properties beside `Arc` (line 18) and `ProgressPath`, and extend the private constructor (line 24) to accept them.

**3. Add the halo *before* the accent arc** in the `AddTo` method (lines 134-137). Order here is exactly what determines paint order:

```csharp
        canvas.Children.Add(Background);
        canvas.Children.Add(HaloPath);      // must precede ProgressPath
        canvas.Children.Add(ProgressPath);
        if (Symbol is not null)
            canvas.Children.Add(Symbol);
```

**4. Sweep both arcs.** Line 116 mutates `Arc.IsLargeArc`; mirror every mutation onto `HaloArc`. The simplest correct form reuses the existing static helper for both:

```csharp
        UpdateArc(Arc, _center, _arcRadius, progress);
        UpdateArc(HaloArc, _center, _arcRadius, progress);
```

Miss this and the halo shows at rest but disappears as the arc sweeps.

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --nologo && dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`

Expected: both PASS.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml src/EyeCtrl.Controls/DwellArcButton.cs
git commit -m "feat(controls): halo the dwell arc, covering ActionMenu and EdgeButtonBar"
```

---

## Task 8: Full verification

**Files:** none modified.

- [ ] **Step 1: Clean build**

Run: `dotnet build EyeCtrl.slnx --nologo` → `Build succeeded`, 0 errors.

- [ ] **Step 2: Validator and headless suite**

Run: `python tools/palette_validate.py` → `ALL GATES PASS - 3 theme(s)`, exit 0.

Run: `dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo` → PASS.

- [ ] **Step 3: Confirm the migration guard is fully armed**

```bash
grep -c "\[\"src/" tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
```

Expected: `0` — `StillPending` is empty, so every previously-listed literal is now guarded against returning.

- [ ] **Step 4: Visual verification — mandatory for this plan**

Nothing above can catch an inverted halo, a halo that fails to track its accent's geometry, or a misspelled resource key. **Ask the user before launching** — they control the PC solely by eye tracking, so a launch takes over their only input channel. Keep it short and have a plan for each check before starting.

Confirm, over both a **dark** and a **light** background window (the light one is the case that fails today):

1. Cursor ring — visible over both; a dark rim around a light ring, not a dark ring
2. Activation button — arc sweeps with the halo tracking it, no dark arc leading or trailing
3. Action menu and edge button bar — arcs haloed via `DwellArcButton`
4. Scroll overlay — velocity ring haloed, crosshairs legible
5. Manual-selection gaze dot — haloed
6. Struggle cue badge — trigger it and confirm the ring; check it over amber/mid-tone content, the case that measures 1.06:1 unringed

If any element shows a **dark ring instead of a light one**, the halo is declared after its accent — fix the ordering, and add that overlay to `HaloZOrderTests.HaloedOverlays` if it is not already there.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore(theme): Plan B complete — StrokeHalo contrast guarantee"
```

---

## Self-Review

**Spec coverage.** §4.1.3 both cases → Tasks 1, 3–7. §5 Phase 5: step 33 → Task 4, step 34 → Task 3, step 35 → Task 5, step 36 → Task 5, step 37 → Task 7, step 38 → Task 6. §5 step 5's `DwellArcButton` halo → Task 7. §9.5's measured table → Task 1's `Backdrops`. Phase 6 → Task 8.

**`TargetHighlightWindow.xaml` is deliberately excluded** — §6 lists it as opacity-driven with no accent mark, so there is nothing to halo. `GlassBackdropWindow`, `ManualSelectionOverlayWindow` and `DragTrailOverlay` are likewise excluded per §6 (fully dynamic or screen-capture).

**Type consistency.** `Tokens.BrushStrokeHalo` / `BrushAccent` / `BrushAccentWarning` / `BrushTextPrimary` / `BrushSurfaceDark` all come from Plan A Task 1 and are spelled identically here. `DwellColorPicker.Presets` (Plan A Task 7) is consumed by `HaloContrastTests`. The `x:Name` pairs in `HaloZOrderTests.HaloedOverlays` match the names introduced in Tasks 3–5 exactly: `CircleHalo`/`Circle`, `ProgressArcHalo`/`ProgressArc`, `OuterCircleHalo`/`OuterCircle`, `VelocityRingHalo`/`VelocityRing` — all four verified against the current XAML rather than invented. `CenterDot` in the gaze indicator is deliberately absent from that dictionary: it is a *filled* shape taking an outline ring, so there is no second element whose order could be wrong.

**Review outcome (2026-07-28).** Findings that held and are fixed: the
`new SolidColorBrush` decision is now made rather than delegated (scoped to
`ActivationButtonWindow`, with the reasoning recorded in both Task 2 and Task 3);
`DwellFeedbackOverlay` gained a guard; `BestEdge` is renamed
`StrongestVisibleEdge` and documents what it does *not* claim; the z-order
dictionary now starts empty so the branch is green at every commit; the
`DwellArcButton` geometry is quoted verbatim from source rather than "copy the
values"; and the Plan A prerequisite is now an executable check.

A finding that the spec and validator "do not exist" did not hold — both are
committed in `3477eef` on this branch, and the validator exits 0. They are absent
from `main` only, which the Prerequisite block now calls out.

**Remaining honest weak points.**

1. `CursorCircleWindow.xaml.cs` keeps one `new SolidColorBrush` and is therefore not covered by the allocation guard. That is now a decision with stated reasoning (Task 2 and Task 3) rather than an open question, but it does leave a real gap: nothing stops a future edit moving that parse into a per-frame path. Closing it properly needs a brush cache keyed on the settings string, which is out of scope here.
2. Task 5 requires copying `OuterCircle`'s layout attributes onto its halo by hand, because those attributes are set through view-model bindings that this plan does not enumerate. Read line 11 of that file before starting, and keep the two elements' geometry in sync in any code that resizes the indicator.
