# Dwell Progress Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `DwellButton`'s fill-wash dwell feedback with an accent progress bar, so a dwelling control's label stays legible to the end of the dwell and every dwell target actually shows progress — including the colour swatches, which today show none.

**Architecture:** `DwellButton.Root` gains a two-row `Grid` — content on top, a bottom-anchored `Rectangle` underneath — and a `Content` property replaces callers reaching into `Root.Child`. `ApplyProgress` drives a `ScaleTransform` on that rectangle instead of mutating the background brush, so feedback is layout-independent, allocation-free per tick, and immune to a caller swapping `Root.Background`. `DwellToggle` moves to the same model.

**Tech Stack:** .NET 10.0.102 SDK, WPF (`net10.0-windows`), xUnit v3 (`xunit.v3` 3.2.2) + NFluent 3.1.0. Solution is `EyeCtrl.slnx`. Tests are stand-alone exes; global usings `Xunit` + `NFluent` come from `tests/Directory.Build.props`.

**Baseline:** written against `main` @ `26af8928`. Main moved 16 commits during the design session that produced this plan — **re-read the four control files before starting** and reconcile if they have moved again.

---

## The two defects being fixed

**1. The label collapses exactly when the user is staring hardest at it.**

`ColorBlend.HighlightColor(base, progress, factor = 0.5)` blends the resting fill toward `Tokens.Accent`. Computed for `#D6DEF5` text on a `#2A2A44` fill under Midnight:

| progress | fill | label contrast |
|---|---|---|
| 0.0 | `#2A2A44` | 10.3:1 |
| 0.4 | `#425268` | 5.9:1 |
| 0.8 | `#5B7A8D` | 3.4:1 |
| **1.0** | `#678EA0` | **2.62:1** — below the 3:1 large-text bar |

This is already known for one surface. `ModalButtonPalette` carries a per-kind blend factor whose comment states the Neutral fill's label "would collapse (to ~2.6:1 at full dwell)" at 0.5. Content controls still pass the flat 0.5 and collapse identically.

**2. Colour swatches show no dwell feedback whatsoever.**

`DwellButton`'s constructor does `_brush = new SolidColorBrush(baseColor); Root.Background = _brush;` and `ApplyProgress` mutates `_brush.Color`. `DwellColorPicker.cs:139` then does `btn.Root.Background = new SolidColorBrush(color);` — replacing the brush. `_brush` is orphaned, `ApplyProgress` mutates something no longer in the visual tree, and `DwellColorPicker` renders no progress of its own (its only per-frame visual is `RefreshSelection`'s border, which tracks *selection*, not dwell). The swatch sits inert and then fires.

A progress bar is a separate element. A caller swapping the background cannot orphan it.

## Why a bar, specifically

This is not a new idea in this codebase — it is the house pattern everywhere except `DwellButton`:

- `ToolbarWindow.xaml.cs:16` — `private const int ProgressBarHeight = 4;`, a `Rectangle` in a `StackPanel` under each button's label.
- `DwellArcButton` — a `ProgressPath` with `UpdateArc`, sweeping an arc plus a halo.

`DwellButton` washing its fill is the odd one out. Bringing it in line is consistency work that happens to fix two defects.

---

## Impact analysis (run before writing this plan, per CLAUDE.md)

| Target | Direction | Risk | Direct | Processes |
|--------|-----------|------|--------|-----------|
| `DwellButton` | upstream | **CRITICAL** | 8 | 4 (`OnFallenBack`, `OpenSettings`, `OnHeartbeatReceived`, `DoLaunchApp`) |
| `DwellToggle` | upstream | **CRITICAL** | 3 | 1 (`OpenSettings`) |

**Read this before touching anything: the blast radius leaves Settings.**

`DwellButton` is constructed in six places — `SettingsWindow.xaml.cs`, `DwellColorPicker`, `DwellComboBox`, `DwellSlider`, **`src/EyeCtrl.Supervisor/UI/FallenBackDialog.xaml.cs`** and **`src/EyeCtrl.Supervisor/UI/SupervisorSetupWindow.xaml.cs`**. `DwellToggle` is constructed in `SettingsWindow.xaml.cs` and twice in `SupervisorSetupWindow.xaml.cs:85,105`.

So this change repaints the Supervisor's setup and fallback dialogs too. That is **intended** — those windows are dwell-driven and have the same defect — but it means:

- `EyeCtrl.Supervisor.Tests` must be run, not just Controls/App/ViewModels.
- The Supervisor windows are `560x640`; nothing here changes a control's width or height, so they cannot overflow. Verify by reading, not by assuming.

The interface boundary reported by the analysis (`ApplyProgress` has one interface-level consumer, `DwellControl.UpdateVisual`) means the traced count is a lower bound. The mitigation is that `ApplyProgress` is `protected abstract` on `DwellControl` — the compiler finds every override.

---

## Global Constraints

Copied from repo conventions (`AGENTS.md`) and the preceding settings plans — every task implicitly obeys these:

- **Layout uses literal DIPs, not tokens.** No spacing/size token scale exists. Tokens are for colour, corner radius, font and border thickness only.
- **NFluent 3.1.0 for assertions.** No `Assert.*`.
- **`EyeCtrl.App.Tests` runs must filter out UI tests** — `--filter "Category!=UI"`. `EyeCtrl.Controls.Tests`, `EyeCtrl.ViewModels.Tests` and `EyeCtrl.Supervisor.Tests` have no UI category; run them unfiltered.
- **Do not add `InternalsVisibleTo` declarations.** `EyeCtrl.Controls`'s IVT targets `EyeCtrl.Controls.Tests` only. Everything new here is `public` on a `public` class, so no IVT is needed.
- **WPF tests need `[StaFact]`**, not `[Fact]` — `Xunit.StaFact` 3.0.13 is already referenced by `EyeCtrl.Controls.Tests`. Every existing test in `DwellButtonProgressTests` uses it; match that.
- **No per-tick allocation.** Dwell visuals update at gaze rate. Mutate an existing `ScaleTransform`'s `ScaleX`; never construct a brush, transform or geometry inside `ApplyProgress`. This is why the existing code keeps one mutable brush per control.
- **File-scoped namespaces**, **Allman braces**, `_camelCase` fields, nullable enabled.
- **Don't run two test suites at once** — named-pipe contention is machine-global. One `dotnet test` invocation at a time.
- **`dotnet test` output on this machine is German.** Failures print as `Fehler <FQN>`; long names wrap across lines. Read the summary (`Fehlgeschlagen: N`), never grep for `Failed`.
- **Commit per task.** Conventional commits. No push unless the user asks.
- **Never launch the app or run `Category=UI` tests without asking the user first.** The user drives this machine solely by eye-tracking; a UI run seizes their only input channel. Task 7 is gated on an explicit go-ahead.

---

## What this plan does NOT fix

The ON toggle's label sits at **2.91:1** (`#D6DEF5` on `#4A8F5C`), below the 3:1 large-text bar, *before* any dwell. Removing the wash stops that degrading further but does not lift the resting value.

Fixing it needs the label off the green fill — the switch anatomy in the Direction B design, which is the next plan. Do not attempt it here by darkening `Tokens.ToggleOn`: that is a palette change gated by `ToggleContrastTests` (ON/OFF ≥ 3:1 and a chroma ceiling, across all three themes) and `tools/palette_validate.py`, and it would land in the keyboard and overlays too.

---

## File Structure

| File | Responsibility | Task |
|------|----------------|------|
| `src/EyeCtrl.Controls/DwellButton.cs` | `Content` property; two-row `Grid`; bar-driven `ApplyProgress` | 2, 3 |
| `src/EyeCtrl.Controls/DwellComboBox.cs:135` | `Root.Child = text` → `Content = text` | 2 |
| `src/EyeCtrl.Controls/DwellColorPicker.cs:144` | `Root.Child = null` → `Content = null` | 2 |
| `src/EyeCtrl.Controls/DwellToggle.cs` | Same bar model | 5 |
| `tests/EyeCtrl.Controls.Tests/DwellButtonTextWrappingTests.cs` | Reach the label through `Content` | 2 |
| `tests/EyeCtrl.Controls.Tests/DwellButtonProgressTests.cs` | Assert the bar, not the background | 3 |
| `tests/EyeCtrl.Controls.Tests/DwellColorPickerProgressTests.cs` | **New** — swatch feedback survives a background swap | 4 |
| `tests/EyeCtrl.Controls.Tests/DwellToggleVisualTests.cs` | Reach the label through `Content`; assert the bar | 5 |
| `tests/EyeCtrl.Controls.Tests/DwellProgressContrastTests.cs` | **New** — the fill must not move with progress | 6 |

---

## Task 1: Isolated worktree + clean baseline

**Files:** none (workspace setup).

- [ ] **Step 1: Confirm main has not moved again**

```bash
git fetch && git log --oneline -1 origin/main
```

Expected: `26af8928`. **If it is anything else, stop and re-read** `DwellButton.cs`, `DwellToggle.cs`, `DwellComboBox.cs` and `DwellColorPicker.cs` before continuing — this plan quotes their current bodies, and a settings-polish workstream has been landing on exactly these files.

- [ ] **Step 2: Create the worktree**

```bash
git worktree add .worktrees/dwell-progress-bar -b feat/dwell-progress-bar main
```

- [ ] **Step 3: Build**

Run from `.worktrees/dwell-progress-bar`:

```bash
dotnet build EyeCtrl.slnx
```

Expected: `Build succeeded`.

- [ ] **Step 4: Baseline every suite this change can reach**

One at a time:

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

Record each total. If any suite is red before you change anything, **stop and report**.

---

## Task 2: Add `Content` and migrate the callers (pure refactor)

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellButton.cs`
- Modify: `src/EyeCtrl.Controls/DwellComboBox.cs:135`
- Modify: `src/EyeCtrl.Controls/DwellColorPicker.cs:144`
- Test: `tests/EyeCtrl.Controls.Tests/DwellButtonTextWrappingTests.cs:27`

No behaviour changes here — the button still washes its fill. This task only moves the child element behind a property so Task 3 has somewhere to put the bar. Doing it separately keeps the risky visual change reviewable on its own.

- [ ] **Step 1: Write the failing test**

Add to `tests/EyeCtrl.Controls.Tests/DwellButtonProgressTests.cs`, inside the class:

```csharp
    /// <summary>
    /// Root.Child is the button's own layout container from here on. Callers that used to
    /// assign it (DwellComboBox, DwellColorPicker) go through Content instead — assigning
    /// Root.Child directly would evict the progress bar along with the old label.
    /// </summary>
    [StaFact]
    public void Content_IsTheLabel_AndRootChildIsTheLayoutContainer()
    {
        var btn = new DwellButton(
            new FakeDwellRegistrar(), "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        Check.That(btn.Content).IsInstanceOf<TextBlock>();
        Check.That(((TextBlock)btn.Content!).Text).IsEqualTo("Label");
        Check.That(btn.Root.Child).IsInstanceOf<Grid>();
    }

    [StaFact]
    public void Content_WhenReplaced_SwapsTheOldElementOut()
    {
        var btn = new DwellButton(
            new FakeDwellRegistrar(), "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);
        var replacement = new TextBlock { Text = "Other" };

        btn.Content = replacement;

        var grid = (Grid)btn.Root.Child;
        Check.That(grid.Children.Cast<UIElement>()).Contains(replacement);
        Check.That(grid.Children.Cast<UIElement>().OfType<TextBlock>().Count()).IsEqualTo(1);
    }

    [StaFact]
    public void Content_WhenClearedToNull_LeavesTheLayoutContainerIntact()
    {
        var btn = new DwellButton(
            new FakeDwellRegistrar(), "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.Content = null;

        Check.That(btn.Content).IsNull();
        Check.That(btn.Root.Child).IsInstanceOf<Grid>();
        Check.That(((Grid)btn.Root.Child).Children.Cast<UIElement>().OfType<TextBlock>()).IsEmpty();
    }
```

Add these usings at the top of that file if absent:

```csharp
using System.Linq;
using System.Windows;
using System.Windows.Controls;
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellButtonProgressTests"
```

Expected: **compile error** — `DwellButton` has no `Content`.

- [ ] **Step 3: Add the layout container and `Content`**

In `src/EyeCtrl.Controls/DwellButton.cs`, add to the usings:

```csharp
using System.Windows.Shapes;
```

Add these fields beside `_brush`:

```csharp
    // The button's own layout container. Root.Child belongs to DwellButton from here on:
    // Task 3 puts the progress bar in row 1, and a caller assigning Root.Child would throw
    // it away along with the label. Callers use Content instead.
    private readonly Grid _layout;
    private UIElement? _content;
```

Add this property above the constructor:

```csharp
    /// <summary>
    /// The element drawn inside the button, above the dwell progress bar.
    ///
    /// Replaces direct assignment to <see cref="DwellControl.Root"/>'s <c>Child</c>, which the
    /// composites used to do: <see cref="Root"/>'s child is now this control's own two-row
    /// layout, and overwriting it would silently remove the progress bar. Setting <c>null</c>
    /// clears the content and keeps the bar — that is the colour-swatch case.
    /// </summary>
    public UIElement? Content
    {
        get => _content;
        set
        {
            if (_content is not null)
                _layout.Children.Remove(_content);

            _content = value;

            if (value is not null)
            {
                Grid.SetRow(value, 0);
                // Index 0 keeps the content behind the bar in z-order. The two occupy
                // different rows, so they do not overlap — this only fixes paint order if a
                // future caller sets a content element that spans both rows.
                _layout.Children.Insert(0, value);
            }
        }
    }
```

In the constructor, replace the block that assigns `Root.Child = new TextBlock { ... }` with:

```csharp
        _layout = new Grid();
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.Child = _layout;

        Content = new TextBlock
        {
            Text = label,
            // Default white text suits the dark surface fills; light semantic fills
            // (AccentWarning/AccentDanger) need dark text to clear WCAG, so the caller
            // passes a textBrush for those.
            Foreground = textBrush ?? Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightStrong,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Readability: a label wider than the button's text room WRAPS to a second
            // centered line instead of clipping mid-word. Every label that fits stays on
            // one line — wrap only engages on overflow (found 2026-08-17: the Models
            // download rows needed up to 561 DIP against 360 of room and clipped a third
            // of their text).
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
```

- [ ] **Step 4: Migrate the two composite callers**

In `src/EyeCtrl.Controls/DwellComboBox.cs`, replace line 135:

```csharp
            btn.Content = text;
```

In `src/EyeCtrl.Controls/DwellColorPicker.cs`, replace line 144:

```csharp
            btn.Content = null; // No text label for colour swatches
```

- [ ] **Step 5: Fix the existing test that reaches through `Root.Child`**

In `tests/EyeCtrl.Controls.Tests/DwellButtonTextWrappingTests.cs:27`, replace:

```csharp
        var text = (TextBlock)button.Root.Child;
```

with:

```csharp
        var text = (TextBlock)button.Content!;
```

- [ ] **Step 6: Run the Controls suite**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

Expected: Task 1's baseline + 3, all passing. `DwellButtonProgressTests`' four original tests still pass — the fill wash is untouched so far.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Controls/DwellButton.cs src/EyeCtrl.Controls/DwellComboBox.cs src/EyeCtrl.Controls/DwellColorPicker.cs tests/EyeCtrl.Controls.Tests/
git commit -m "refactor(controls): give DwellButton a Content property and its own layout container"
```

---

## Task 3: Replace the fill wash with a progress bar

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellButton.cs`
- Test: `tests/EyeCtrl.Controls.Tests/DwellButtonProgressTests.cs`

A `ScaleTransform` on `ScaleX`, not a `Width` assignment: `Width` needs `ActualWidth`, which is 0 until the first layout pass and would leave the bar dead on a control measured before display (which is exactly what `SettingsAutoPagination` does). Scaling a stretched rectangle is layout-independent, and mutating one double allocates nothing per tick.

- [ ] **Step 1: Rewrite the progress tests**

In `tests/EyeCtrl.Controls.Tests/DwellButtonProgressTests.cs`, replace the three `ApplyProgress_*` tests (keep `OnActivated_InvokesAction` and the three `Content_*` tests from Task 2) with:

```csharp
    [StaFact]
    public void ApplyProgress_AtZero_ShowsNoBar()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(0);

        Check.That(btn.ProgressScaleX).IsEqualTo(0.0);
    }

    [StaFact]
    public void ApplyProgress_AtHalf_FillsHalfTheBar()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(0.5);

        Check.That(btn.ProgressScaleX).IsEqualTo(0.5);
    }

    [StaFact]
    public void ApplyProgress_AtFull_FillsTheBar()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(1.0);

        Check.That(btn.ProgressScaleX).IsEqualTo(1.0);
    }

    [StaFact]
    public void ApplyProgress_OutOfRange_IsClamped()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(-1);
        Check.That(btn.ProgressScaleX).IsEqualTo(0.0);

        btn.TestApplyProgress(2);
        Check.That(btn.ProgressScaleX).IsEqualTo(1.0);
    }

    /// <summary>
    /// The whole point of the bar: the fill the label sits on never moves, so the label's
    /// contrast at the end of a dwell equals its contrast at rest. The wash this replaced
    /// took a #D6DEF5 label on #2A2A44 from 10.3:1 down to 2.62:1 at full dwell.
    /// </summary>
    [StaFact]
    public void ApplyProgress_NeverMovesTheFillTheLabelSitsOn()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "Label", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        foreach (var p in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            btn.TestApplyProgress(p);
            var brush = (SolidColorBrush)btn.Root.Background;
            Check.WithCustomMessage($"fill moved at progress {p}")
                .That(brush.Color).IsEqualTo(Tokens.SurfaceMedium);
        }
    }
```

Extend the `TestDwellButton` helper in the same file so the test can read the transform:

```csharp
    private class TestDwellButton : DwellButton
    {
        public TestDwellButton(IDwellRegistrar registrar, string id, string label, Action action, Color baseColor, DwellCategory category, double radius, int dwellMs)
            : base(registrar, id, label, action, baseColor, category, radius, dwellMs)
        {
        }

        public double ProgressScaleX => ((ScaleTransform)ProgressBar.RenderTransform).ScaleX;

        public void TestApplyProgress(double progress) => ApplyProgress(progress);
        public void TestOnActivated() => OnActivated();
    }
```

Add to that file's usings:

```csharp
using System.Windows.Shapes;
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellButtonProgressTests"
```

Expected: **compile error** — `DwellButton` exposes no `ProgressBar`.

- [ ] **Step 3: Add the bar and rewrite `ApplyProgress`**

In `src/EyeCtrl.Controls/DwellButton.cs`, add the constant and fields:

```csharp
    /// <summary>
    /// Height of the dwell progress bar in DIPs.
    ///
    /// Six rather than <c>ToolbarWindow</c>'s four: this bar sits inside a control whose whole
    /// visual is the 100 DIP hit circle, and the toolbar's four reads thin at that scale.
    /// Both are literal layout DIPs — there is no spacing token scale to draw from.
    /// </summary>
    public const double ProgressBarHeight = 6;

    // Mutated in place by ApplyProgress at gaze rate: one ScaleX write, no allocation.
    private readonly Rectangle _progressBar;
    private readonly ScaleTransform _progressScale;
```

Expose the bar for tests and for composites that want to restyle it:

```csharp
    /// <summary>
    /// The dwell progress bar drawn along the button's bottom edge.
    ///
    /// A separate element rather than a wash over <see cref="DwellControl.Root"/>'s background,
    /// for two reasons. The label's contrast no longer decays as the dwell completes (the wash
    /// took a light label on a dark fill to 2.62:1 at full dwell). And a caller that replaces
    /// <c>Root.Background</c> — <see cref="DwellColorPicker"/> does, for every swatch — can no
    /// longer orphan the feedback, which is why swatches used to show no progress at all.
    /// </summary>
    public Rectangle ProgressBar => _progressBar;
```

In the constructor, after the `_layout` rows are added and before `Root.Child = _layout;`, add:

```csharp
        _progressScale = new ScaleTransform(0, 1);
        _progressBar = new Rectangle
        {
            Height = ProgressBarHeight,
            Fill = Tokens.BrushAccent,
            // Stretch + a ScaleX transform, not a Width: Width would need ActualWidth, which is
            // 0 until the first layout pass — and SettingsAutoPagination measures controls that
            // are never laid out. Scaling is layout-independent and costs one double per tick.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransform = _progressScale,
            RenderTransformOrigin = new Point(0, 0.5),
            IsHitTestVisible = false
        };
        Grid.SetRow(_progressBar, 1);
        _layout.Children.Add(_progressBar);
```

Replace `ApplyProgress` in full:

```csharp
    /// <inheritdoc/>
    protected override void ApplyProgress(double progress)
    {
        _progressScale.ScaleX = Math.Clamp(progress, 0, 1);
    }
```

Delete the now-unused `_baseColor` field and its assignment in the constructor — nothing reads it once the wash is gone, and leaving it produces a warning. Keep `_brush` and `Root.Background = _brush;`: the resting fill is still the button's, and callers still swap it.

Update the class doc-comment's third `<para>`, which currently describes the wash:

```csharp
/// <para>
/// Dwell visual feedback: <see cref="ApplyProgress"/> sweeps <see cref="ProgressBar"/> along
/// the button's bottom edge. The background never moves, so the label's contrast at the end
/// of a dwell equals its contrast at rest.
/// </para>
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellButtonProgressTests"
```

Expected: all pass.

- [ ] **Step 5: Run the whole Controls suite**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

Expected: green **except** `DwellToggleVisualTests.ApplyProgress_BlendsTowardAccent`, which still asserts the toggle's own wash. `DwellToggle` overrides `ApplyProgress` and is untouched until Task 5, so that test is legitimately still passing — confirm it is, and that nothing else broke.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Controls/DwellButton.cs tests/EyeCtrl.Controls.Tests/DwellButtonProgressTests.cs
git commit -m "fix(controls): sweep a progress bar for dwell instead of washing the fill"
```

---

## Task 4: Pin the colour-swatch regression

**Files:**
- Create: `tests/EyeCtrl.Controls.Tests/DwellColorPickerProgressTests.cs`

Task 3 fixed the swatches as a side effect. This task proves it and stops it regressing — the failure mode was silent for as long as it existed, because nothing throws when a brush is orphaned.

- [ ] **Step 1: Write the test**

```csharp
namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// Colour swatches showed no dwell feedback at all until 2026-08-20. DwellColorPicker assigns
/// its own brush to each swatch's Root.Background, which orphaned the brush DwellButton's
/// ApplyProgress mutated — so the swatch sat inert for the whole dwell and then fired. Nothing
/// threw, so nothing caught it.
///
/// The progress bar is a separate element, immune to a background swap. These tests hold that.
/// </summary>
public class DwellColorPickerProgressTests
{
    [StaFact]
    public void ASwatch_ShowsDwellProgress_DespiteItsBackgroundBeingReplaced()
    {
        var hex = "#A5F3FC";
        var picker = new DwellColorPicker(
            new FakeRegistrar(), "cp", "Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800);

        var swatch = picker.Children.First();
        swatch.UpdateVisualForTest(0.5);

        var bar = FindBar(swatch);
        Check.That(((ScaleTransform)bar.RenderTransform).ScaleX).IsEqualTo(0.5);
    }

    [StaFact]
    public void ASwatch_KeepsItsOwnColour_WhileDwelling()
    {
        var hex = "#A5F3FC";
        var picker = new DwellColorPicker(
            new FakeRegistrar(), "cp", "Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800);

        var swatch = picker.Children.First();
        var before = ((SolidColorBrush)swatch.Root.Background).Color;
        swatch.UpdateVisualForTest(1.0);

        Check.That(((SolidColorBrush)swatch.Root.Background).Color).IsEqualTo(before);
    }

    private static Rectangle FindBar(DwellControl control) =>
        ((DwellButton)control).ProgressBar;

    private class FakeRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
```

`ApplyProgress` is `protected`, and these tests hold a `DwellButton` they did not subclass. Add this to `DwellControl` so any test — and any composite — can drive a child's visual without a subclass, which is what `UpdateVisual` already does through the registrar:

```csharp
    /// <summary>
    /// Applies <paramref name="progress"/> directly, bypassing the registrar.
    ///
    /// <see cref="UpdateVisual"/> is the production path and reads progress from the
    /// <see cref="Registrar"/>; this exists for composites and tests that hold a child control
    /// and need to drive its feedback with a known value.
    /// </summary>
    /// <param name="progress">Dwell progress in [0, 1].</param>
    public void UpdateVisualForTest(double progress) => ApplyProgress(progress);
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellColorPickerProgressTests"
```

Expected: **compile error** until `UpdateVisualForTest` is added to `src/EyeCtrl.Controls/DwellControl.cs`. Add it, re-run, and both tests should pass — Task 3 already did the work.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Controls/DwellControl.cs tests/EyeCtrl.Controls.Tests/DwellColorPickerProgressTests.cs
git commit -m "test(controls): pin that colour swatches show dwell progress"
```

---

## Task 5: Bring `DwellToggle` onto the bar

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellToggle.cs`
- Test: `tests/EyeCtrl.Controls.Tests/DwellToggleVisualTests.cs`

`DwellToggle` is a `DwellControl`, not a `DwellButton`, so it builds its own root and needs its own bar. Its wash is worse than the button's: it starts from the green ON fill, where the label is already 2.91:1.

- [ ] **Step 1: Update the tests**

In `tests/EyeCtrl.Controls.Tests/DwellToggleVisualTests.cs`, the two symbol tests reach through `Root.Child`, which is now a `Grid`. Replace both casts:

```csharp
        var text = (TextBlock)toggle.Label;
```

Replace `ApplyProgress_BlendsTowardAccent` entirely with:

```csharp
    [StaFact]
    public void ApplyProgress_SweepsTheBar()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        toggle.TestApplyProgress(0.75);

        Check.That(((ScaleTransform)toggle.ProgressBar.RenderTransform).ScaleX).IsEqualTo(0.75);
    }

    /// <summary>
    /// The ON fill is #4A8F5C, on which the #D6DEF5 label already sits at 2.91:1 — under the
    /// 3:1 large-text bar before any dwell begins. Washing it toward the accent made that
    /// worse with every frame. The fill must now hold still; lifting the resting 2.91:1 needs
    /// the label off the green entirely, which is the switch anatomy in the next plan.
    /// </summary>
    [StaFact]
    public void ApplyProgress_NeverMovesTheStateFill()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        foreach (var p in new[] { 0.0, 0.5, 1.0 })
        {
            toggle.TestApplyProgress(p);
            Check.WithCustomMessage($"ON fill moved at progress {p}")
                .That(((SolidColorBrush)toggle.Root.Background).Color).IsEqualTo(Tokens.ToggleOn);
        }
    }
```

Extend the helper:

```csharp
    private class TestDwellToggle : DwellToggle
    {
        public TestDwellToggle(IDwellRegistrar registrar, string id, string label, Func<bool> get, Action<bool> set, DwellCategory category, double radius, int dwellMs)
            : base(registrar, id, label, get, set, category, radius, dwellMs)
        {
        }

        public void TestApplyProgress(double progress) => ApplyProgress(progress);
        public void TestOnActivated() => OnActivated();
    }
```

Add to that file's usings:

```csharp
using System.Windows.Shapes;
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellToggleVisualTests"
```

Expected: **compile error** — `DwellToggle` exposes neither `Label` nor `ProgressBar`.

- [ ] **Step 3: Give the toggle the same layout and bar**

In `src/EyeCtrl.Controls/DwellToggle.cs`, add usings:

```csharp
using System.Windows.Shapes;
```

Add the fields beside `_brush`:

```csharp
    private readonly Rectangle _progressBar;
    private readonly ScaleTransform _progressScale;
```

Expose both elements:

```csharp
    /// <summary>The checkbox-and-label text block. Root's child is this control's layout container.</summary>
    public TextBlock Label => _text;

    /// <summary>
    /// The dwell progress bar along the toggle's bottom edge. Mirrors
    /// <see cref="DwellButton.ProgressBar"/>: the state fill stays put through the dwell, so the
    /// label's contrast on it does not decay.
    /// </summary>
    public Rectangle ProgressBar => _progressBar;
```

In the constructor, replace `Root.Child = _text;` with:

```csharp
        _progressScale = new ScaleTransform(0, 1);
        _progressBar = new Rectangle
        {
            Height = DwellButton.ProgressBarHeight,
            Fill = Tokens.BrushAccent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransform = _progressScale,
            RenderTransformOrigin = new Point(0, 0.5),
            IsHitTestVisible = false
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_text, 0);
        Grid.SetRow(_progressBar, 1);
        layout.Children.Add(_text);
        layout.Children.Add(_progressBar);
        Root.Child = layout;
```

Replace `ApplyProgress` in full:

```csharp
    /// <inheritdoc/>
    protected override void ApplyProgress(double progress)
    {
        _progressScale.ScaleX = Math.Clamp(progress, 0, 1);
    }
```

`RefreshVisual` is unchanged: it still sets `_brush.Color` to `OnColor`/`OffColor` on state change, which is the only thing that should move that fill.

Update the class doc-comment's last sentence, which currently reads "Dwell feedback blends the current state colour toward white as dwell accumulates":

```csharp
/// Dwell feedback sweeps a progress bar along the bottom edge; the state colour holds still,
/// so the label's contrast on it does not decay as the dwell completes.
```

- [ ] **Step 4: Run the Controls suite**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

Expected: fully green now, at Task 1's baseline + the new cases.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/DwellToggle.cs tests/EyeCtrl.Controls.Tests/DwellToggleVisualTests.cs
git commit -m "fix(controls): sweep a progress bar on DwellToggle instead of washing the state fill"
```

---

## Task 6: Guard the invariant across every dwell control

**Files:**
- Create: `tests/EyeCtrl.Controls.Tests/DwellProgressContrastTests.cs`

Tasks 3 and 5 each pin their own control. This one states the rule once, so a control added later is caught: **no dwell control may change the surface its own label sits on in response to progress.**

- [ ] **Step 1: Write the test**

```csharp
namespace EyeCtrl.Controls.Tests;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// One rule, stated once: dwell feedback may not move the surface a label sits on.
///
/// The fill wash this replaced was a real, measured defect — a #D6DEF5 label on #2A2A44 went
/// from 10.3:1 at rest to 2.62:1 at full dwell, under the 3:1 large-text bar exactly when the
/// user was staring hardest at it. ModalButtonPalette already carried a per-kind blend factor
/// to work around the same collapse; content controls did not.
///
/// This is a relationship test (contrast at full dwell equals contrast at rest), not a pixel
/// aesthetic — the exception the repo's "layout literals are not unit-tested" convention allows.
/// </summary>
public class DwellProgressContrastTests
{
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

    [StaFact]
    public void DwellButton_LabelContrast_IsTheSameAtFullDwellAsAtRest()
    {
        var btn = new DwellButton(
            new FakeRegistrar(), "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 50, 800);

        var atRest = Contrast(Tokens.TextPrimary, ((SolidColorBrush)btn.Root.Background).Color);
        btn.UpdateVisualForTest(1.0);
        var atFull = Contrast(Tokens.TextPrimary, ((SolidColorBrush)btn.Root.Background).Color);

        Check.WithCustomMessage(
                $"label contrast fell from {atRest:F2}:1 to {atFull:F2}:1 over the dwell")
            .That(atFull).IsEqualTo(atRest);
    }

    [StaFact]
    public void DwellToggle_LabelContrast_IsTheSameAtFullDwellAsAtRest()
    {
        var state = true;
        var toggle = new DwellToggle(
            new FakeRegistrar(), "id", "Label", () => state, v => state = v,
            DwellCategory.Control, 50, 800);

        var atRest = Contrast(Tokens.TextPrimary, ((SolidColorBrush)toggle.Root.Background).Color);
        toggle.UpdateVisualForTest(1.0);
        var atFull = Contrast(Tokens.TextPrimary, ((SolidColorBrush)toggle.Root.Background).Color);

        Check.WithCustomMessage(
                $"label contrast fell from {atRest:F2}:1 to {atFull:F2}:1 over the dwell")
            .That(atFull).IsEqualTo(atRest);
    }

    private class FakeRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
```

- [ ] **Step 2: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellProgressContrastTests"
```

Expected: both pass. To confirm the test has teeth, temporarily restore the old body of `DwellButton.ApplyProgress`:

```csharp
        _brush.Color = ColorBlend.Blend(_baseColor, Tokens.Accent, progress * 0.5);
```

re-run, see `DwellButton_LabelContrast_IsTheSameAtFullDwellAsAtRest` fail with the 10.33 → 2.62 message, then revert.

- [ ] **Step 3: Run every suite this change reaches**

One at a time:

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

All three must be at or above their Task 1 baselines with zero failures. `SettingsAutoPaginationTests` and `SettingsContentFitTests` deserve a specific look: control heights are unchanged (`Root.Height` is still explicit and the bar lives inside it), so measured row heights must not have moved. If they have, the two-row `Grid` is being measured taller than the fixed `Height` — stop and report rather than adjusting the expectations.

- [ ] **Step 4: Commit**

```bash
git add tests/EyeCtrl.Controls.Tests/DwellProgressContrastTests.cs
git commit -m "test(controls): dwell feedback may not move the surface a label sits on"
```

---

## Task 7: Live verification — **requires the user's explicit go-ahead**

**Files:** none.

- [ ] **Step 1: Ask before doing anything in this task**

The user drives this machine solely by eye-tracking. Launching EyeCtrl takes over the cursor and can cut them off from their caregiver. **Stop and ask for an explicit go-ahead**, saying how long the run will take. Do not proceed on assumed consent, and do not run `Category=UI` tests without the same permission.

- [ ] **Step 2: On a yes — launch and check the feedback**

```bash
dotnet run --project src/EyeCtrl.App/EyeCtrl.App.csproj
```

Open Settings and confirm, then close promptly:

1. Dwelling any settings button sweeps a cyan bar left-to-right along its bottom edge; the button's fill does not change and the label stays crisp throughout.
2. On the Cursor tab, dwelling a **colour swatch** sweeps the same bar — this showed nothing at all before.
3. On the General tab, dwelling a toggle sweeps the bar without the green or the dark fill shifting.
4. Combo options (Cursor → Filter Mode) sweep the bar, and the selected option's green border is unaffected.
5. The bar resets to empty when gaze leaves before activation.

- [ ] **Step 3: On a yes — check the Supervisor windows too**

These are in the blast radius and are the ones nobody looks at. If a supervisor is installed, open its setup window and confirm its two toggles and its buttons sweep the same bar. If it is not installed, say so and record the check as not performed — do not install one to test it.

- [ ] **Step 4: Report**

Report exactly what happened, including anything that did not work. If the run was declined, say the plan is complete but unverified live, and leave it there.

---

## Self-review

**Defect coverage.** Defect 1 (label collapse) — Tasks 3 and 5 remove the wash, Task 6 states the rule. Defect 2 (inert swatches) — fixed by Task 3's design, pinned by Task 4. Covered.

**Stated non-goal.** The ON toggle's resting 2.91:1 is not fixed here and the plan says so in two places, including in a test comment where the next reader will hit it.

**Type consistency.** `DwellButton.Content` (Task 2) is consumed in Tasks 2 and 5. `DwellButton.ProgressBar` and `ProgressBarHeight` (Task 3) are consumed in Tasks 4, 5 and 6. `DwellControl.UpdateVisualForTest` (Task 4) is consumed in Tasks 4 and 6 — note it is added in Task 4 but Task 6 depends on it, so Tasks must run in order. `_progressScale`/`_progressBar` are named identically in `DwellButton` and `DwellToggle`.

**Ordering dependency.** Task 6 uses `UpdateVisualForTest`, introduced in Task 4. Task 5's tests use `DwellButton.ProgressBarHeight`, introduced in Task 3. Do not reorder.

**Risk not eliminated, only bounded.** `DwellButton` is CRITICAL upstream with four affected processes, all in the Supervisor. Nothing here changes a signature, a size or a hit radius — the change is confined to what is drawn inside an unchanged `Root`. Task 6 Step 3 runs `EyeCtrl.Supervisor.Tests`, and Task 7 Step 3 looks at the windows by eye. That is the whole mitigation; there is no headless test of the Supervisor's rendering.
