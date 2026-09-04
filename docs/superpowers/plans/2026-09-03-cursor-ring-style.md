# Cursor Ring Style Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the user a Cursor setting that picks between three gaze-ring
treatments, each of which keeps a usable amount of ring visible on both light and
dark desktop backdrops.

**Architecture:** A `CursorRingStyle` enum on `CursorSettings` selects one of
three stroke stacks. The stack is computed by a pure function in `EyeCtrl.Core`
(`CursorRingStacks.For`) that returns two `RingBand` records; `CursorCircleWindow`
applies them to the two `Ellipse` elements it already declares and decides
nothing itself. The settings combo doubles as a preset, writing each style's
tuned thickness and opacity into the existing sliders.

**Tech Stack:** C# / .NET 10 / WPF, CommunityToolkit.Mvvm `[ObservableProperty]`,
xUnit v3 with NFluent assertions.

**Spec:** `docs/superpowers/specs/2026-09-03-cursor-ring-style-design.md`. Read
§3 and §5 before Task 4 — the dash geometry has a silent failure mode.

---

## Before you start

**Read the spec's §5 dash section.** Task 4 implements a pattern where the wrong
reading of one number compiles, passes every test that exists today, and silently
destroys the feature. The plan's tests catch it; do not "simplify" them.

**`dotnet test` output on this machine is German.** A failing test line reads
`Fehler <FullyQualifiedName>`, *not* `Failed`, and long names wrap across lines.
Grepping for "Failed" or "error" will report green on a red run. Read the summary
line (`Fehlgeschlagen: N`) or check the process exit code.

**Document every parameter, or none.** `GenerateDocumentationFile` is on, so a doc
comment that documents *some* of a member's parameters raises CS1573 for each one
it skips. Where a code block below shows `<param>` tags, add the missing ones for
that member's other parameters rather than shipping a partial set.

**Do not run two test suites at once on this box.** Named pipes are
machine-global and other worktrees of this repo may be testing concurrently; a
concurrent run produces failures in unrelated tests that look like product bugs.

**Baseline first.** Before Task 1, confirm the suite is green so later failures
are attributable:

```bash
dotnet test tests/EyeCtrl.Core.Tests
```

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Expected: both pass, with no failures at all. The FlaUI tests that need a real
display — the ones that fail headless with "Toolbar_Window did not appear within
15s" — are all `[Trait("Category", "UI")]` and are excluded by this filter, so
seeing them here would itself be a surprise. Any failure: stop and report rather
than assuming it is environmental.

---

## File Structure

**Created:**

| File | Responsibility |
| --- | --- |
| `src/EyeCtrl.Core/Models/CursorRingStyle.cs` | The three-value enum. Nothing else. |
| `src/EyeCtrl.Core/Models/CursorRingStack.cs` | `RingBand`, `CursorRingStack`, and the pure `CursorRingStacks.For` that maps a style to two bands. The only place ring geometry is decided. |
| `tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs` | Band widths, colours, dash geometry. Headless, no WPF. |
| `tests/EyeCtrl.App.Tests/CursorRingStyleContrastTests.cs` | The §4.1.3 contrast guarantee, per style × theme × backdrop × colour preset. |

**Modified:**

| File | Change |
| --- | --- |
| `src/EyeCtrl.Core/Settings/CursorSettings.cs` | Add `RingStyle`; change `CircleThickness` and `CircleOpacity` defaults; rewrite the `CircleOpacity` doc comment. |
| `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs` | `UpdateAppearance` consumes the stack instead of hard-coding one. |
| `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml` | A comment only (spec §10). Element tags stay byte-identical. |
| `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs` | Ring Style combo with preset write; colour row swap; reset copies `RingStyle`; opacity description rewritten. |
| `tests/EyeCtrl.App.Tests/Settings/Pages/CursorPageSourceTests.cs` | Cover the combo, the preset, the swap and the reset. |

**Not touched:** the two `Ellipse` tags in `CursorCircleWindow.xaml` — their
declaration order and their `BrushStrokeHalo` literals are pinned by
`HaloZOrderTests`, and only the comment above them changes.
`EyeCtrlSettings.EnumerateSubSettings` stays as it is (it enumerates
sub-settings *classes*), as do the other four §4.1.3 overlays.

---

### Task 1: The enum and the setting

**Files:**
- Create: `src/EyeCtrl.Core/Models/CursorRingStyle.cs`
- Modify: `src/EyeCtrl.Core/Settings/CursorSettings.cs:35` (insert after `_positionIndicatorStyle`)
- Test: `tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class CursorRingStacksTests
{
    [Fact]
    public void DefaultRingStyle_IsWeightedSandwich()
    {
        Check.That(new CursorSettings().RingStyle).IsEqualTo(CursorRingStyle.WeightedSandwich);
    }
}
```

Note: `Xunit` and `NFluent` are global usings from `tests/Directory.Build.props`
— do not add `using Xunit;` or `using NFluent;`, and use `Check.That(...)`, never
`Assert.*`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: compile error — `CursorRingStyle` and `RingStyle` do not exist.

- [ ] **Step 3: Create the enum**

`src/EyeCtrl.Core/Models/CursorRingStyle.cs`:

```csharp
namespace EyeCtrl.Core.Models;

/// <summary>How the gaze cursor ring keeps contrast against arbitrary desktop content.</summary>
public enum CursorRingStyle
{
    /// <summary>Accent core between two StrokeHalo rims. The shipped look, re-proportioned.</summary>
    WeightedSandwich,
    /// <summary>One ring alternating accent and StrokeHalo segments, interleaved not stacked.</summary>
    BiTonalSegments,
    /// <summary>Near-black core inside a TextPrimary outline. No hue at all.</summary>
    AchromaticReticle
}
```

Declaration order is load-bearing: `Enum.GetNames<CursorRingStyle>()` feeds the
settings combo's `Code` array positionally against its `Display` array in Task 8.

- [ ] **Step 4: Add the property**

In `src/EyeCtrl.Core/Settings/CursorSettings.cs`, immediately after the
`_positionIndicatorStyle` line:

```csharp
    /// <summary>How the cursor ring's stroke stack is built. See CursorRingStacks.</summary>
    [ObservableProperty] private CursorRingStyle _ringStyle = CursorRingStyle.WeightedSandwich;
```

No `OnRingStyleChanging` guard and no `Validate()` entry — matching
`PositionIndicatorStyle`, the existing enum property on this class. Do **not**
add an `OnRingStyleChanged` hook: JSON deserialization runs property setters, so
a hook there would overwrite the user's persisted thickness and opacity depending
on property order in the file. The preset write belongs in the UI (Task 8).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: PASS, 1 test.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Models/CursorRingStyle.cs src/EyeCtrl.Core/Settings/CursorSettings.cs tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs
git commit -m "feat(cursor): add the CursorRingStyle setting"
```

---

### Task 2: The stack descriptor, WeightedSandwich only

**Files:**
- Create: `src/EyeCtrl.Core/Models/CursorRingStack.cs`
- Test: `tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `CursorRingStacksTests`:

```csharp
    private const string Mark = "#A5F3FC";
    private const string Halo = "#0A0A14";
    private const string Light = "#D6DEF5";

    private static CursorRingStack Build(CursorRingStyle style, double size = 250, double thickness = 3, double opacity = 1.0) =>
        CursorRingStacks.For(style, size, thickness, opacity, Mark, Halo, Light);

    [Fact]
    public void WeightedSandwich_HaloOutlineIsFourWiderThanTheAccentCore()
    {
        var stack = Build(CursorRingStyle.WeightedSandwich);

        Check.That(stack.Outer.Hex).IsEqualTo(Halo);
        Check.That(stack.Outer.Thickness).IsEqualTo(7.0);
        Check.That(stack.Outer.Opacity).IsEqualTo(1.0);

        Check.That(stack.Core.Hex).IsEqualTo(Mark);
        Check.That(stack.Core.Thickness).IsEqualTo(3.0);
    }

    [Fact]
    public void WeightedSandwich_CoreCarriesTheUserOpacity_OutlineNeverDoes()
    {
        // The outline is the backdrop-independent half of the edge. Dimming it would
        // void the guarantee, so CircleOpacity applies to the core alone.
        var stack = Build(CursorRingStyle.WeightedSandwich, opacity: 0.4);

        Check.That(stack.Core.Opacity).IsEqualTo(0.4);
        Check.That(stack.Outer.Opacity).IsEqualTo(1.0);
    }

    [Fact]
    public void WeightedSandwich_IsSolid()
    {
        var stack = Build(CursorRingStyle.WeightedSandwich);

        Check.That(stack.Outer.DashPeriod).IsEqualTo(0.0);
        Check.That(stack.Core.DashPeriod).IsEqualTo(0.0);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: compile error — `CursorRingStacks` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/EyeCtrl.Core/Models/CursorRingStack.cs`:

```csharp
namespace EyeCtrl.Core.Models;

/// <summary>
/// One concentric band of the cursor ring's stroke stack. Widths are logical pixels;
/// <see cref="DashPeriod"/> and <see cref="DashOffset"/> are lengths ALONG the arc, in the
/// same units — the renderer converts them into WPF's multiples-of-StrokeThickness.
/// </summary>
/// <param name="DashPeriod">Dash length, equal to the gap length. 0 for a solid band.</param>
public readonly record struct RingBand(
    string Hex, double Thickness, double Opacity, double DashPeriod, double DashOffset);

/// <summary>The two bands a cursor ring is drawn from. <see cref="Outer"/> paints first.</summary>
public readonly record struct CursorRingStack(RingBand Outer, RingBand Core);

/// <summary>
/// Maps a <see cref="CursorRingStyle"/> onto the two bands that render it.
///
/// Pure and framework-free on purpose: EyeCtrl.Core cannot reference EyeCtrl.Controls (the
/// dependency runs the other way), so the palette colours arrive as hex strings from the
/// caller rather than from Tokens. Keeping the decision here — rather than in the overlay —
/// is what lets the geometry be tested headlessly.
/// </summary>
public static class CursorRingStacks
{
    /// <summary>Builds the stack for <paramref name="style"/>.</summary>
    /// <param name="size">Ring diameter in logical pixels; sets the dash period.</param>
    /// <param name="thickness">The core band's width; the outline derives from it.</param>
    /// <param name="opacity">User opacity. Applies to the core band only.</param>
    /// <param name="markHex">The user's ring colour.</param>
    /// <param name="haloHex">Tokens.StrokeHalo.</param>
    /// <param name="lightHex">Tokens.TextPrimary.</param>
    public static CursorRingStack For(
        CursorRingStyle style, double size, double thickness, double opacity,
        string markHex, string haloHex, string lightHex) => style switch
    {
        CursorRingStyle.WeightedSandwich => new CursorRingStack(
            new RingBand(haloHex, thickness + 4, 1.0, 0, 0),
            new RingBand(markHex, thickness, opacity, 0, 0)),
        _ => throw new ArgumentOutOfRangeException(
            nameof(style), style, "Unknown cursor ring style")
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/CursorRingStack.cs tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs
git commit -m "feat(cursor): add the ring stack descriptor with WeightedSandwich"
```

---

### Task 3: AchromaticReticle

**Files:**
- Modify: `src/EyeCtrl.Core/Models/CursorRingStack.cs`
- Test: `tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `CursorRingStacksTests`:

```csharp
    [Fact]
    public void AchromaticReticle_IsALightOutlineOverADarkCore()
    {
        // Deliberately the INVERSE of the crosshair precedent in overlay-unification-spec
        // §4.1.3 (light mark, dark outline). Contrast-equivalent; this arrangement puts the
        // thicker carrying band on dark backdrops. Do not "correct" it — see spec §3.
        var stack = Build(CursorRingStyle.AchromaticReticle);

        Check.That(stack.Outer.Hex).IsEqualTo(Light);
        Check.That(stack.Outer.Thickness).IsEqualTo(7.0);
        Check.That(stack.Core.Hex).IsEqualTo(Halo);
        Check.That(stack.Core.Thickness).IsEqualTo(3.0);
        Check.That(stack.Outer.DashPeriod).IsEqualTo(0.0);
    }

    [Fact]
    public void AchromaticReticle_IgnoresTheMarkColour()
    {
        // The style has no hue, so the colour picker is swapped out for it in the UI.
        var stack = CursorRingStacks.For(
            CursorRingStyle.AchromaticReticle, 250, 3, 1.0, "#FF00FF", Halo, Light);

        Check.That(stack.Outer.Hex).IsEqualTo(Light);
        Check.That(stack.Core.Hex).IsEqualTo(Halo);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: FAIL — `ArgumentOutOfRangeException: Unknown cursor ring style`.

- [ ] **Step 3: Add the arm**

In `CursorRingStacks.For`, insert before the `_ =>` arm:

```csharp
        CursorRingStyle.AchromaticReticle => new CursorRingStack(
            new RingBand(lightHex, thickness + 4, 1.0, 0, 0),
            new RingBand(haloHex, thickness, opacity, 0, 0)),
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/CursorRingStack.cs tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs
git commit -m "feat(cursor): add the AchromaticReticle ring style"
```

---

### Task 4: BiTonalSegments — the dash geometry

**Files:**
- Modify: `src/EyeCtrl.Core/Models/CursorRingStack.cs`
- Test: `tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs`

**Read this before writing code.** `P = π · size / 32` is the **dash length**,
and the gap length equals it, so one full on/off cycle is `2P` and the ring
carries 16 cycles. The core band is offset by exactly `P` — a *half* cycle — so
the accent segments land in the halo's gaps and the two never overlap. Offsetting
by `2P` instead puts them in phase: the accent lands exactly on the halo, the
bi-tonal property is gone, and nothing catches it — the closure arithmetic still
holds and every contrast test still passes, because no colour changed.

- [ ] **Step 1: Write the failing test**

Append to `CursorRingStacksTests`:

```csharp
    [Fact]
    public void BiTonalSegments_BothBandsShareOneThickness()
    {
        // Not a stack: the two strokes interleave along the arc at the same radius and
        // width. Equal thickness is also why the StrokeDashArray scaling trap of §4.1.3
        // does not arise here — both patterns divide by the same number.
        var stack = Build(CursorRingStyle.BiTonalSegments, thickness: 5);

        Check.That(stack.Outer.Hex).IsEqualTo(Halo);
        Check.That(stack.Outer.Thickness).IsEqualTo(5.0);
        Check.That(stack.Core.Hex).IsEqualTo(Mark);
        Check.That(stack.Core.Thickness).IsEqualTo(5.0);
    }

    [Fact]
    public void BiTonalSegments_CoreIsOffsetByExactlyOneDashLength()
    {
        // THE test for this style. A whole-cycle offset (2 * DashPeriod) would put the
        // bands in phase, the accent would cover the halo, and the style would silently
        // become a plain accent ring that still passes every contrast test.
        var stack = Build(CursorRingStyle.BiTonalSegments, thickness: 5);

        Check.That(stack.Outer.DashOffset).IsEqualTo(0.0);
        Check.That(stack.Core.DashOffset).IsEqualTo(stack.Core.DashPeriod);
        Check.That(stack.Outer.DashPeriod).IsEqualTo(stack.Core.DashPeriod);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(250)]
    [InlineData(333)]
    [InlineData(500)]
    public void BiTonalSegments_PatternClosesAtEveryLegalSize(double size)
    {
        // Asserted against the RETURNED period, not a restatement of the formula: written
        // as pure arithmetic this identity holds for any input and guards nothing. What it
        // guards here is rounding inside the implementation, which would leave a visible
        // seam where the dash pattern wraps.
        var stack = CursorRingStacks.For(
            CursorRingStyle.BiTonalSegments, size, 5, 1.0, Mark, Halo, Light);

        var cycles = Math.PI * size / (2 * stack.Core.DashPeriod);

        Check.That(Math.Abs(cycles - Math.Round(cycles))).IsStrictlyLessThan(1e-9);
        Check.That(Math.Round(cycles)).IsEqualTo(16.0);
    }

    [Theory]
    [InlineData(CursorRingStyle.WeightedSandwich)]
    [InlineData(CursorRingStyle.BiTonalSegments)]
    [InlineData(CursorRingStyle.AchromaticReticle)]
    public void EveryStyle_CoreCarriesTheUserOpacity_OutlineNeverDoes(CursorRingStyle style)
    {
        // Only WeightedSandwich pinned this, and only it passes a non-default opacity. At
        // 1.0 the property is untestable: a style whose arm swapped `opacity` and `1.0`
        // between its two bands would pass every other test in this file. The outline is
        // the backdrop-independent half of the edge, so dimming it voids the guarantee.
        var stack = CursorRingStacks.For(style, 250, 3, 0.4, Mark, Halo, Light);

        Check.WithCustomMessage($"{style} core").That(stack.Core.Opacity).IsEqualTo(0.4);
        Check.WithCustomMessage($"{style} outline").That(stack.Outer.Opacity).IsEqualTo(1.0);
    }

    [Theory]
    [InlineData(CursorRingStyle.WeightedSandwich)]
    [InlineData(CursorRingStyle.BiTonalSegments)]
    [InlineData(CursorRingStyle.AchromaticReticle)]
    public void EveryStyle_OuterIsNeverNarrowerThanTheCore(CursorRingStyle style)
    {
        // The property that makes a stack a stack: if the core were wider it would cover the
        // outline completely and there would be no internal edge left to see. Swept across
        // the whole legal thickness range, not just the presets.
        for (var thickness = 1.0; thickness <= 10.0; thickness += 0.5)
        {
            var stack = CursorRingStacks.For(style, 250, thickness, 1.0, Mark, Halo, Light);

            Check.WithCustomMessage($"{style} at thickness {thickness}")
                .That(stack.Outer.Thickness).IsGreaterOrEqualThan(stack.Core.Thickness);
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: FAIL — `ArgumentOutOfRangeException: Unknown cursor ring style` on the
`BiTonalSegments` cases. Both cross-style theories fail only on their
`BiTonalSegments` row; their `WeightedSandwich` and `AchromaticReticle` rows
should already pass, since Tasks 2 and 3 implemented those arms correctly.

- [ ] **Step 3: Add the arm**

In `CursorRingStacks.For`, insert before the `_ =>` arm:

```csharp
        CursorRingStyle.BiTonalSegments => BiTonal(size, thickness, opacity, markHex, haloHex),
```

and add the private helper to the class:

```csharp
    /// <summary>
    /// Interleaved halo and mark segments at one radius and width.
    ///
    /// The dash length is a 32nd of the circumference, so a full on/off cycle is a 16th and
    /// the pattern closes at the wrap point for any diameter. The mark band is offset by ONE
    /// dash length — half a cycle — which is what drops its segments into the halo's gaps.
    /// An offset of a whole cycle would put the two in phase and quietly void the style.
    /// </summary>
    private static CursorRingStack BiTonal(
        double size, double thickness, double opacity, string markHex, string haloHex)
    {
        var dash = Math.PI * size / 32.0;
        return new CursorRingStack(
            new RingBand(haloHex, thickness, 1.0, dash, 0),
            new RingBand(markHex, thickness, opacity, dash, dash));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: PASS, 19 tests (6 from Tasks 1–3, plus 2 facts, a 5-case theory and two
3-case theories here).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/CursorRingStack.cs tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs
git commit -m "feat(cursor): add the BiTonalSegments ring style"
```

---

### Task 5: New defaults

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/CursorSettings.cs:20-31`
- Test: `tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `CursorRingStacksTests`:

```csharp
    [Fact]
    public void Defaults_GiveWeightedSandwichItsIntendedWeights()
    {
        // A fresh install must get the style's real proportions, not its structure at the
        // weights that fail: a 2px core at 0.7 opacity is the shipped defect.
        var settings = new CursorSettings();

        Check.That(settings.CircleThickness).IsEqualTo(3.0);
        Check.That(settings.CircleOpacity).IsEqualTo(1.0);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CursorRingStacksTests"`

Expected: FAIL — actual 2.0 and 0.7.

- [ ] **Step 3: Change the defaults and rewrite the doc comment**

Replace the whole `CircleOpacity` doc comment plus the two property lines. The
existing comment argues for 0.7 on contrast grounds and would be left
contradicting the value beside it:

```csharp
    /// <summary>
    /// Opacity of the gaze cursor circle. Valid range: 0–1.
    ///
    /// 1.0, not 0.7: the ring's outline band is opaque regardless (CursorRingStacks applies
    /// this value to the core band alone), so the backdrop-independent edge no longer needs
    /// the core dimmed to stay above the 7.0 floor — at full opacity that edge is
    /// Accent against StrokeHalo, 15.78:1. The earlier 0.7 was chosen when this value scaled
    /// the whole stack; it also composited the accent toward the backdrop, which is what put
    /// it at 1.17:1 over a white document. Lowering this still works and still dims only the
    /// mark side of the edge.
    /// </summary>
    [ObservableProperty] private double _circleOpacity = 1.0;
    /// <summary>Thickness of the gaze cursor circle stroke in logical pixels. Valid range: 1–10.</summary>
    [ObservableProperty] private double _circleThickness = 3.0;
```

- [ ] **Step 4: Run the full Core suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests`

Expected: PASS. No test pins the old defaults (verified during spec review), so
nothing else should move. If something fails, it pins a default the review
missed — report it rather than editing the assertion.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/CursorSettings.cs tests/EyeCtrl.Core.Tests/CursorRingStacksTests.cs
git commit -m "feat(cursor): default to the weights WeightedSandwich needs"
```

---

### Task 6: Render the stack

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs:28-59`
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml` (a comment only — see below)

**Change nothing inside the two `Ellipse` tags.** `HaloZOrderTests` reads that
file as text: `HaloElement_IsDeclaredBeforeItsAccentElement` compares the
positions of the two `x:Name="…"` strings, and `EveryHaloElement_UsesTheStrokeHaloBrush`
scans from `x:Name="CircleHalo"` forward to the next `>` and requires
`BrushStrokeHalo` in that span (`HaloZOrderTests.cs:59-96`). So: do not reorder
the elements, and do not remove the now-redundant `Stroke=` literals.

Under `AchromaticReticle` that brush assertion becomes vacuous — the runtime
stroke is `TextPrimary` — so the Core tests of Tasks 2–4 are the real guard for
what each band paints.

A comment **above** the elements is safe (both tests index from `x:Name`
forward, so nothing preceding it is scanned) and is required by spec §10.

- [ ] **Step 1: Replace `UpdateAppearance`**

Replace the whole method body, keeping the signature and the
`MinimumCircleSize` constant:

```csharp
    public void UpdateAppearance(CursorSettings settings)
    {
        var size = Math.Max(MinimumCircleSize, settings.CircleSize);

        // Core owns the geometry; this method owns only WPF. The palette colours are passed
        // in as hex because EyeCtrl.Core cannot reference EyeCtrl.Controls.
        var stack = CursorRingStacks.For(
            settings.RingStyle, size, settings.CircleThickness, settings.CircleOpacity,
            settings.CircleColor, ToHex(Tokens.StrokeHalo), ToHex(Tokens.TextPrimary));

        Apply(CircleHalo, stack.Outer, Tokens.BrushStrokeHalo);
        Apply(Circle, stack.Core, Tokens.BrushAccent);

        Width = size;
        Height = size;
        Circle.Width = size;
        Circle.Height = size;
        CircleHalo.Width = size;
        CircleHalo.Height = size;
    }

    /// <summary>
    /// Paints one band onto one ellipse. The brush is built and frozen here, once per
    /// settings change and never per frame — see the note in HaloZOrderTests about why this
    /// file is exempt from ActivationButton_NeverAllocatesABrush.
    /// </summary>
    /// <param name="target">The ellipse to paint.</param>
    /// <param name="band">The band's colour, width, opacity and dash geometry.</param>
    /// <param name="fallback">Used when the band's hex is unparseable, which only the
    /// user-supplied mark colour can be.</param>
    private static void Apply(Ellipse target, RingBand band, SolidColorBrush fallback)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(band.Hex));
            brush.Freeze();
            target.Stroke = brush;
        }
        catch
        {
            target.Stroke = fallback;
        }

        target.StrokeThickness = band.Thickness;
        target.Opacity = band.Opacity;

        if (band.DashPeriod > 0)
        {
            // StrokeDashArray and StrokeDashOffset are both in MULTIPLES OF StrokeThickness,
            // not device pixels (same as DragTrailOverlay). Dash and gap are equal, and the
            // offset is one dash — half a cycle — which interleaves the two bands.
            var units = band.DashPeriod / band.Thickness;
            target.StrokeDashArray = new DoubleCollection { units, units };
            target.StrokeDashOffset = band.DashOffset / band.Thickness;
        }
        else
        {
            target.StrokeDashArray = new DoubleCollection();
            target.StrokeDashOffset = 0;
        }
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
```

- [ ] **Step 2: Add the usings the new code needs**

At the top of the file, alongside the existing usings:

```csharp
using System.Windows.Shapes;
```

`System.Windows.Media` (for `Color`, `ColorConverter`, `SolidColorBrush`,
`DoubleCollection`) and `EyeCtrl.Core.Models` are already imported.

- [ ] **Step 2b: Comment the two elements in the XAML**

Spec §10 keeps the names `CircleHalo` / `Circle` even though which of them is
dark now depends on the style. Add this **above** the existing `CircleHalo`
element in `CursorCircleWindow.xaml`, replacing the current "Halo first" comment
and leaving both element tags exactly as they are:

```xml
    <!-- Two bands, outer then core: later siblings paint on top, so the wider one must
         come first. Which band is dark depends on CursorSettings.RingStyle — under
         AchromaticReticle "CircleHalo" carries TextPrimary, not StrokeHalo. The names are
         historical; CursorRingStacks decides the colours (spec §4.1.3, §10). -->
```

- [ ] **Step 3: Build**

Run: `dotnet build EyeCtrl.slnx`

Expected: build succeeds, no new warnings.

- [ ] **Step 4: Run the App test subset**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`

Expected: same result as the baseline. `HaloZOrderTests` in particular must still
pass — it reads the untouched XAML.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs
git commit -m "feat(cursor): render the ring from the Core stack descriptor"
```

---

### Task 7: The contrast guarantee, per style

**Files:**
- Create: `tests/EyeCtrl.App.Tests/CursorRingStyleContrastTests.cs`

This lives in App.Tests because it needs `ITheme`, `ThemeRegistry` and
`DwellColorPicker.PresetsFor`, which are in the WPF-referencing `EyeCtrl.Controls`.
It carries no UI category, so CI's `Category!=UI` subset runs it.

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// §4.1.3's guarantee, extended to every ring style. HaloContrastTests proves the palette
/// can hold an edge; this proves each style actually uses a pair that does, whatever the
/// user picked in the colour picker and whatever theme is active.
/// </summary>
public class CursorRingStyleContrastTests
{
    /// <summary>Backdrops a transparent overlay may be drawn over. Mirrors spec §9.5.</summary>
    private static readonly string[] Backdrops =
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

    private static double StrongestVisibleEdge(Color mark, Color halo, Color backdrop) =>
        Math.Max(Contrast(mark, halo),
            Math.Max(Contrast(mark, backdrop), Contrast(halo, backdrop)));

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryStyle_KeepsTheGuarantee_ForEveryPresetAndBackdrop(ITheme theme)
    {
        foreach (var style in Enum.GetValues<CursorRingStyle>())
        {
            foreach (var preset in DwellColorPicker.PresetsFor(theme))
            {
                var stack = CursorRingStacks.For(
                    style, 250, 3, 1.0, preset.Hex, Hex(theme.StrokeHalo), Hex(theme.TextPrimary));

                var outer = Parse(stack.Outer.Hex);
                var core = Parse(stack.Core.Hex);

                foreach (var b in Backdrops)
                {
                    var edge = StrongestVisibleEdge(core, outer, Parse(b));
                    Check.WithCustomMessage(
                            $"{theme.Name}/{style}: preset '{preset.Name}' ({preset.Hex}) reaches "
                            + $"only {edge:F2}:1 over {b}; §4.1.3 requires >= {EdgeFloor}.")
                        .That(edge).IsStrictlyGreaterThan(EdgeFloor);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AchromaticReticle_DoesNotDependOnThePresetAtAll(ITheme theme)
    {
        // Its bands are TextPrimary and StrokeHalo, so the colour picker cannot weaken it —
        // which is why the picker is swapped out for a note under this style.
        var withAccent = CursorRingStacks.For(
            CursorRingStyle.AchromaticReticle, 250, 3, 1.0,
            Hex(theme.Accent), Hex(theme.StrokeHalo), Hex(theme.TextPrimary));
        var withMagenta = CursorRingStacks.For(
            CursorRingStyle.AchromaticReticle, 250, 3, 1.0,
            "#FF00FF", Hex(theme.StrokeHalo), Hex(theme.TextPrimary));

        Check.That(withMagenta).IsEqualTo(withAccent);

        var edge = Contrast(Parse(withAccent.Core.Hex), Parse(withAccent.Outer.Hex));
        Check.WithCustomMessage($"{theme.Name}: reticle edge is only {edge:F2}:1.")
            .That(edge).IsStrictlyGreaterThan(EdgeFloor);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~CursorRingStyleContrastTests"`

Expected: PASS, 6 tests (2 theories × 3 themes). These should pass immediately —
Tasks 2–4 already produce compliant stacks, and this task's job is to pin that
they keep doing so. **If any fails, do not adjust the floor or the backdrops** —
a failure means a style genuinely breaks the guarantee for some preset, which is
a design problem to report.

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/CursorRingStyleContrastTests.cs
git commit -m "test(cursor): pin the 4.1.3 guarantee for every ring style"
```

---

### Task 8: The settings row

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/CursorPageSourceTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `CursorPageSourceTests`:

```csharp
    [Fact]
    public void Build_RingStyleCombo_MapsDisplayOntoEnumNames()
    {
        var rows = new CursorPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        var combo = rows.OfType<ComboBoxSpec>().Single(c => c.Id == "c_ring_style");

        // Display and Code are positional. If the enum gains a member or is reordered
        // without the display list following, the combo silently mislabels every style.
        Check.That(combo.Code).ContainsExactly(Enum.GetNames<CursorRingStyle>());
        Check.That(combo.Display).ContainsExactly(
            "Weighted sandwich", "Bi-tonal segments", "Achromatic reticle");
        Check.That(combo.Description).IsNotNull();
    }

    [Fact]
    public void RingStyleSetter_WritesThePresetWeights()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleThickness = 1;
        settings.Cursor.CircleOpacity = 0.2;
        var source = new CursorPageSource(TestSettingsPageContext.Build(settings));
        var combo = source.Build().SelectMany(p => p.Rows).OfType<ComboBoxSpec>()
            .Single(c => c.Id == "c_ring_style");

        combo.Set(nameof(CursorRingStyle.BiTonalSegments));

        Check.That(settings.Cursor.RingStyle).IsEqualTo(CursorRingStyle.BiTonalSegments);
        Check.That(settings.Cursor.CircleThickness).IsEqualTo(5.0);
        Check.That(settings.Cursor.CircleOpacity).IsEqualTo(1.0);
    }

    [Fact]
    public void Build_UnderAchromaticReticle_SwapsTheColourPickerForANote()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.RingStyle = CursorRingStyle.AchromaticReticle;
        var rows = new CursorPageSource(TestSettingsPageContext.Build(settings)).Build()
            .SelectMany(p => p.Rows).ToList();

        Check.That(rows.OfType<ColorPickerSpec>()).IsEmpty();
        var note = rows.OfType<LabelSpec>().Single();
        Check.That(note.Text()).Contains("Circle Color");
    }

    [Fact]
    public void Reset_RestoresTheRingStyle()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.RingStyle = CursorRingStyle.BiTonalSegments;
        // The default context DISCARDS the modal callback — TestSettingsPageContext.Build
        // defaults showModal to ((_, _) => { }) — so the confirmation must be run explicitly
        // or the reset copies nothing. Same pattern as Reset_CopiesAllTenProperties above.
        var source = new CursorPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        var reset = source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>()
            .Single(b => b.Id == "c_reset");

        reset.OnActivate();

        Check.That(settings.Cursor.RingStyle).IsEqualTo(CursorRingStyle.WeightedSandwich);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~CursorPageSourceTests"`

Expected: the four new tests fail. The first three fail because no `c_ring_style`
row exists; `Reset_RestoresTheRingStyle` fails because the reset callback does not
yet copy `RingStyle` (its modal *does* fire — the test opts in explicitly).

- [ ] **Step 3: Add the combo to page 1**

In `CursorPageSource.Build()`, add to the **first** `PageSpec`'s rows, after the
`c_smooth` slider and before the colour row:

```csharp
                new ComboBoxSpec("c_ring_style", "Ring Style",
                    ["Weighted sandwich", "Bi-tonal segments", "Achromatic reticle"],
                    Enum.GetNames<CursorRingStyle>(),
                    () => c.RingStyle.ToString(),
                    v =>
                    {
                        c.RingStyle = Enum.Parse<CursorRingStyle>(v);

                        // The style is a preset: it writes the weights it was designed at and
                        // leaves them editable. Deliberately here and not in an
                        // OnRingStyleChanged hook on CursorSettings — that would also fire
                        // during JSON load and overwrite the user's tuning.
                        (c.CircleThickness, c.CircleOpacity) = c.RingStyle switch
                        {
                            CursorRingStyle.BiTonalSegments => (5.0, 1.0),
                            _ => (3.0, 1.0),
                        };

                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();   // repaints the sliders and swaps the colour row
                    })
                    { Description = "How the ring stays visible on light and dark backgrounds." },
```

- [ ] **Step 4: Swap the colour row**

Replace the existing `c_color` `ColorPickerSpec` construction with:

```csharp
                c.RingStyle == CursorRingStyle.AchromaticReticle
                    ? (SettingsRowSpec)new LabelSpec(
                        () => "Circle Color is not used by the Achromatic reticle ring style.")
                    : new ColorPickerSpec("c_color", "Circle Color",
                        () => c.CircleColor,
                        v => { c.CircleColor = v; _ctx.ApplyAndSave(); },
                        SettingsWindow.ColorSwatchRadius)
                        { Description = "The cursor ring’s colour; every preset stays clearly visible." },
```

The `(SettingsRowSpec)` cast on the first branch is belt-and-braces, not a
requirement: the conditional is target-typed from the collection's element type
and compiles without it. Keep the cast anyway — it states the intent, and it
keeps compiling if the row list is ever refactored into something less
target-typed.

- [ ] **Step 5: Add `RingStyle` to the reset**

In the `c_reset` `ButtonSpec`'s callback, alongside the other field copies:

```csharp
                        c.RingStyle = d.RingStyle;
```

- [ ] **Step 5b: Rewrite the opacity slider's description**

Spec §7 requires the opacity row to say what lowering it costs, now that every
preset sets it to 1.0. Replace the `Description` on the `c_opacity` `SliderSpec`
(`CursorPageSource.cs:43`), leaving the slider's range and setter untouched:

```csharp
                    { Description = "Lowering this dims the coloured half of the ring's edge, which is what keeps it visible." },
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~CursorPageSourceTests"`

Expected: PASS, all tests in the class.

`Build_ColorPickerUsesSwatchRadius` uses `.OfType<ColorPickerSpec>().Single()`
with no predicate. It still passes because the default style keeps the picker —
do not extend it to `AchromaticReticle` without adding a predicate.

- [ ] **Step 7: Run the description gate and the wider subset**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`

Expected: same as baseline. `SettingsRowDescriptionCompletenessTests` covers
`CursorPageSource` and fails a `ComboBoxSpec` with no `Description` — Step 3
supplies one. If a pagination or fit test now fails, the new row pushed the tab
over a page boundary: fix by row placement, not by deleting the assertion.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/CursorPageSourceTests.cs
git commit -m "feat(cursor): expose Ring Style as a preset on the Cursor tab"
```

---

### Task 9: Whole-suite verification

**Files:** none.

- [ ] **Step 1: Build clean**

Run: `dotnet build EyeCtrl.slnx`

Expected: succeeds with no new warnings.

- [ ] **Step 2: Run every headless suite, one at a time**

```bash
dotnet test tests/EyeCtrl.Core.Tests
```

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests
```

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
```

Run them sequentially, never concurrently — named pipes are machine-global.
Expected: green, matching the Task 0 baseline exactly (the display-dependent UI
tests are filtered out, so there should be no failures to explain away). Read the
German summary line, not a grep for "Failed".

- [ ] **Step 3: Confirm the affected scope**

Run `detect_changes({scope: "compare", base_ref: "main"})` and check the affected
symbols are limited to `CursorSettings`, `CursorRingStacks`/`RingBand`/
`CursorRingStack`, `CursorCircleWindow.UpdateAppearance`, and
`CursorPageSource.Build`. Anything outside that set means an unintended edit.

- [ ] **Step 4: Report what was NOT verified**

The three styles have not been seen on a real desktop. Every check in this plan
is arithmetic or structural: no test can tell you the `BiTonalSegments` ring
looks right at 250px, or that `AchromaticReticle` reads as a cursor rather than
as a hole. Say so plainly when handing back, and leave the visual check to the
user — this app is the user's sole input channel, so do not launch it to look.

---

## Notes for the implementer

**Why the preset is not on the model.** `[ObservableProperty]` generates a
setter, and `System.Text.Json` deserialization goes through setters. A
`partial void OnRingStyleChanged` that wrote thickness and opacity would fire
while loading a saved settings file and clobber the user's tuned values,
depending on the order the properties appear in the JSON. The combo setter fires
only on a real gaze interaction.

**Why `EnumerateSubSettings` is untouched.** It yields the sub-settings
*classes*, and its drift-guard test reflects over `EyeCtrlSettings`' class-typed
properties. Adding a property to `CursorSettings` cannot affect it.

**If you need to add a new settings row type:** you don't, for this plan. The
colour swap uses the existing `LabelSpec`, which `SettingsWindow.RenderSpec`
already accepts as a `DwellLabel`. A two-column labelled row would require an
`IDwellComposite` for the control column plus a `CreateControl` case — explicitly
rejected in spec §7 on cost.
