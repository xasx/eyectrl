# Settings Two-Column Rows Implementation Plan

> **Sequence: plan 3 of 3** of the settings restyle — `dwell-progress-feedback` -> `dwell-control-anatomy` -> `settings-two-column-rows`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the settings content area from a stack of stacks into a scannable list — each setting's name and its control on one line, with enough vertical room between controls that a gaze drift cannot land on the neighbour.

**Architecture:** The four labelled composites gain a `showLabel` flag; `SettingsWindow` wraps each in a row that is two-column where the content column is wide enough for one and stacked where it is not, at a 150 DIP minimum pitch either way, inside a card, under a page header naming the tab. Button and label rows keep their current full-width form untouched.

**Tech Stack:** .NET 10.0.102 SDK, WPF (`net10.0-windows`), xUnit v3 + NFluent 3.1.0. Solution is `EyeCtrl.slnx`.

**Baseline:** `main` @ `26af8928`, plus **plans 1 and 2 landed** (`2026-08-20-dwell-progress-feedback.md`, `2026-08-20-dwell-control-anatomy.md`). This plan assumes `DwellToggle` is already an `IDwellComposite` with a switch.

---

## Prerequisite

```bash
grep -n "class DwellToggle : IDwellComposite" src/EyeCtrl.Controls/DwellToggle.cs
grep -n "private const double PlateWidth" src/EyeCtrl.Controls/DwellSlider.cs
```

Both must hit. If not, plans 1 and 2 have not landed and this one will not compile.

---

## Why 150 DIP of pitch is the point

Today a slider row costs roughly 190 DIP: a `DwellLabel` (24px text, 14/20 margins ≈ 66) above a 100 DIP control row with a 24 DIP bottom margin. The name is on one line and the control on the next, so the column reads as a stack of stacks and the eye re-scans for every setting.

Two-column rows put them on one line. That is worth doing on its own, but the pitch is the part that matters for this user:

**`DwellControl.UpdatePosition` registers the hit circle at the control's centre.** Two 100 DIP circles whose centres are 100 DIP apart are exactly tangent — no gap, no tolerance, and a one-pixel drift picks the neighbour. The codebase has already hit this once: `SettingsWindow.CreateControl` comments that `DwellButton`'s own `(4,0,4,0)` margin "left stacked buttons flush, their 100 DIP hit circles tangent", and fixes it with a 24 DIP bottom margin.

The precedent for how much clearance is enough is the colour-swatch grid, the one pitch already pinned by a geometry test: `SwatchHeight` 100 + 2 × `SwatchVerticalMargin` 20 = **140 DIP centre-to-centre, 40 DIP between circles**. This plan uses 150, giving 50 — at or above the only measured precedent in the app.

**A naive compaction would have made this worse.** Packing 100 DIP rows back-to-back to fit more per page trades a real safety margin for one fewer page turn. The pitch is chosen first; the row count is whatever falls out of it.

---

## Impact analysis (run before writing this plan, per CLAUDE.md)

| Target | Direction | Risk | Notes |
|---|---|---|---|
| `SettingsWindow.CreateControl` | upstream | HIGH | Called by `RenderSpec` **and** `SettingsAutoPagination.MeasureRows` — the two must stay identical or measured height stops matching rendered height. Task 4 also adds a required `availableWidth` parameter, which touches all four call sites (two of them tests). |
| `DwellSlider` / `DwellComboBox` / `DwellColorPicker` | upstream | MEDIUM | Constructed only by `SettingsWindow`. A defaulted `showLabel` parameter changes no existing call. |
| `DwellToggle` | upstream | **CRITICAL** | Also constructed by `SupervisorSetupWindow` (×2). `showLabel` defaults to `true`, so the Supervisor keeps its current row. |

**The measurement coupling is the thing to be careful about.** `SettingsAutoPagination.MeasureRowHeight` builds the *real* control through `CreateControl` and measures it on a detached panel. If two-column rows are assembled in `RenderSpec` rather than in `CreateControl`, the measurer sees the old single-column height and every page count is wrong. Task 4 therefore moves the row assembly **into `CreateControl`**, which both paths share. This is the single most important design decision in this plan.

---

## Guarded expectations this plan must not break

| Guard | What it pins | Effect here |
|---|---|---|
| `SettingsAutoPaginationTests.StandaloneButtonRow_...` | a `ButtonSpec` row measures **exactly 124.0** DIP | Button rows must stay full-width and untouched |
| `SettingsAutoPaginationTests.EveryChunkedPage_FitsTheBudget*` | chunked pages fit the budget they were chunked against | Holds automatically if measurement and rendering stay identical |
| `SettingsContentFitTests` | the picker button fits the narrow column, and overruns without the cap | Card padding narrows the column to 400; the cap absorbs it with 0.00 DIP spare. **Five sites must be re-pointed by hand — the negative control cannot detect a partial job** (Task 4 Step 6) |
| `SettingsTabLabelFitTests` | tab labels fit the 240 sidebar | Sidebar untouched |
| `SettingsTabDriftTests` | `_tabDefs` ids match the UI test's expectations | Its regex matches only the tuple's first element, so a 5th element is safe |
| `SwatchTargetGeometryTests` | swatch hit circles do not overlap | Swatch geometry untouched |
| `ContainerRatioCensusTests` | which `ContainerRatio` each file uses | **Why tab icons are out of scope** — see below |

---

## Out of scope, and why

- **Stroke `Path` tab icons.** The tab glyph is a `TextBlock` sized `TabContainerHeight * ContainerRatio.Medium`. `ContainerRatioCensusTests` pins, per file, which ratio that file uses — replacing the glyph with a `Path` removes the usage and fails the census. Doing it properly means updating the census deliberately, which is its own change.
- **Grouping the sidebar into sections.** `SettingsTabPagination.TabsPerPage(budget, slotHeight)` assumes uniform slots; group eyebrows are not slots, so grouping silently pushes tabs off the bottom — reintroducing the exact bug `aa6d2df7` just fixed. Grouping needs either variable-height chunking or a one-group-per-page model, and that is a design decision, not a task.
- **Moving "Reset" into a footer.** Reset rows come from ten of the eleven page sources as `ButtonSpec`s (`SelectionPageSource` has none). Relocating them means touching almost every source for a purely visual gain.
- **Writing description copy for every setting.** Task 2 adds the field and fills in the Cursor tab as a worked example. The remaining ten tabs are a content pass the user should drive — they know what needs explaining.

---

## Global Constraints

- **Layout uses literal DIPs, not tokens.** Tokens are for colour, corner radius, font and border thickness only.
- **NFluent 3.1.0.** No `Assert.*`. WPF tests use `[StaFact]`.
- **`EyeCtrl.App.Tests` needs `--filter "Category!=UI"`.** Controls / ViewModels / Supervisor suites run unfiltered.
- **Measurement and rendering must stay identical.** Anything that changes a row's rendered height belongs in `CreateControl`, never in `RenderSpec` alone.
- **The 100 DIP WYSIWYG floor holds**, and no control is drawn smaller than its hit circle.
- **Read layout numbers off live elements**, never restate them — the sidebar's 220→240 widening silently ate 20 DIP of content column because the arithmetic was copied.
- **File-scoped namespaces**, Allman braces, `_camelCase` fields, nullable enabled.
- **Don't run two test suites at once.** **German output** — read `Fehlgeschlagen: N`.
- **Commit per task.** No push unless asked.
- **Never launch the app or run `Category=UI` tests without asking first.** Task 7 is gated.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs` | Optional `Description` on the base record | 2 |
| `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs` | Worked-example descriptions | 2 |
| `src/EyeCtrl.Controls/DwellSlider.cs`, `DwellComboBox.cs`, `DwellColorPicker.cs`, `DwellToggle.cs` | `showLabel` flag, default `true` | 3 |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | Two-column assembly in `CreateControl`; `RowPitch`; header wiring; width/height budgets | 4, 5 |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml` | Card around the content panel; header row | 4, 5 |
| `tests/EyeCtrl.App.Tests/Settings/SettingsRowLayoutTests.cs` | **New** — row shape, pitch, target clearance, narrow-column fallback | 2, 4 |
| `tests/EyeCtrl.App.Tests/Settings/SettingsAutoPaginationTests.cs` | Two `CreateControl` call sites gain the width argument | 4 |
| `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs` | Header must not starve the content column | 5 |

---

## Task 1: Worktree + baseline

- [ ] **Step 1: Confirm prerequisites and that main has not moved**

```bash
git fetch && git log --oneline -1 origin/main
```

Plans 1 and 2 must be in the history. If main has moved beyond them, re-read `SettingsWindow.xaml.cs` and the four composites before continuing.

- [ ] **Step 2: Create the worktree**

```bash
git worktree add .worktrees/settings-two-column -b feat/settings-two-column main
```

- [ ] **Step 3: Build**

```bash
dotnet build EyeCtrl.slnx
```

- [ ] **Step 4: Baseline, one suite at a time**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj
```

Record each total. Red before you start means **stop and report**.

---

## Task 2: An optional description on every row

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs`
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs`

An `init` property on the base record, not a positional parameter: positional would force all six subclasses and every one of the ~80 existing spec constructions to pass it.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Settings/SettingsRowLayoutTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class SettingsRowLayoutTests
{
    [Fact]
    public void ARowSpec_CarriesAnOptionalDescription()
    {
        var without = new ToggleSpec("t", "Start Paused", () => false, _ => { });
        var with = new ToggleSpec("t", "Start Paused", () => false, _ => { })
        {
            Description = "EyeCtrl starts with tracking suspended."
        };

        Check.That(without.Description).IsNull();
        Check.That(with.Description).IsEqualTo("EyeCtrl starts with tracking suspended.");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~SettingsRowLayoutTests"
```

Expected: **compile error** — `ToggleSpec` has no `Description`.

- [ ] **Step 3: Add the property**

In `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs`, give the base record a body:

```csharp
public abstract record SettingsRowSpec(string Id, string Label)
{
    /// <summary>
    /// One line explaining what the setting does, drawn under <see cref="Label"/> in the row's
    /// name column. Optional: a row whose name already says everything leaves it null and the
    /// second line is not drawn at all.
    ///
    /// An <c>init</c> property rather than a positional parameter so the ~80 existing spec
    /// constructions across eleven page sources keep compiling untouched.
    /// </summary>
    public string? Description { get; init; }
}
```

- [ ] **Step 4: Fill in the Cursor tab as the worked example**

In `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs`, add a `Description` initialiser to each of the five settings. The copy below is drawn from the XML doc comments on `CursorSettings` and from `ColorPickerPresetTests`' guarantee — do not invent beyond what the code states:

```csharp
                new ComboBoxSpec("c_filter", "Filter Mode", Enum.GetNames<GazeFilterMode>(), null,
                    () => c.FilterMode.ToString(),
                    v => { c.FilterMode = Enum.Parse<GazeFilterMode>(v); _ctx.ApplyAndSave(); })
                    { Description = "Filtering applied to raw gaze samples." },
                new SliderSpec("c_smooth", "Smoothing Factor", 0, 1, 0.1, 1,
                    () => c.SmoothingFactor,
                    v => { c.SmoothingFactor = v; _ctx.ApplyAndSave(); })
                    { Description = "Higher is more responsive; lower is smoother." },
                new ColorPickerSpec("c_color", "Circle Color",
                    () => c.CircleColor,
                    v => { c.CircleColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius)
                    { Description = "Every preset clears 7:1 against the ring halo." },
```

and on page two:

```csharp
                new SliderSpec("c_opacity", "Circle Opacity", 0.1, 1, 0.1, 1,
                    () => c.CircleOpacity,
                    v => { c.CircleOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = "How solid the ring is over what is behind it." },
                new SliderSpec("c_thick", "Circle Thickness", 1, 6, 0.5, 1,
                    () => c.CircleThickness,
                    v => { c.CircleThickness = v; _ctx.ApplyAndSave(); })
                    { Description = "Ring stroke width, in pixels." },
```

Leave the `ButtonSpec` reset row (`c_reset`) without a description — button rows do not render one.

- [ ] **Step 5: Run and commit**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

Expected: green, baseline + 1.

```bash
git add src/EyeCtrl.App/Views/Settings/
git commit -m "feat(settings): rows can carry a one-line description"
```

---

## Task 3: Composites can skip their own label

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellSlider.cs`, `DwellComboBox.cs`, `DwellColorPicker.cs`, `DwellToggle.cs`

Each of the four owns a label today. In a two-column row the label belongs to the row's left column, so the composite must be able to omit it. `showLabel` defaults to `true`, so the Supervisor's two toggles and any other caller are unaffected.

- [ ] **Step 1: Write the failing test**

Add to `tests/EyeCtrl.Controls.Tests/DwellComboBoxSelectionTests.cs` (it already has the registrar fake):

```csharp
    [StaFact]
    public void WithShowLabelFalse_TheCompositeAttachesNoLabelOfItsOwn()
    {
        var selected = "Alpha";
        var panel = new System.Windows.Controls.StackPanel();

        var combo = new DwellComboBox(
            new FakeRegistrar(), "cb", "Mode", ["Alpha", "Beta"],
            () => selected, v => selected = v,
            DwellCategory.Control, 50, 800, showLabel: false);
        combo.Attach(panel);

        Check.That(panel.Children.Cast<System.Windows.UIElement>()
            .OfType<System.Windows.Controls.TextBlock>()).IsEmpty();
    }

    [StaFact]
    public void ByDefault_TheCompositeStillAttachesItsOwnLabel()
    {
        var selected = "Alpha";
        var panel = new System.Windows.Controls.StackPanel();

        var combo = new DwellComboBox(
            new FakeRegistrar(), "cb", "Mode", ["Alpha", "Beta"],
            () => selected, v => selected = v,
            DwellCategory.Control, 50, 800);
        combo.Attach(panel);

        Check.That(panel.Children.Cast<System.Windows.UIElement>()
            .OfType<System.Windows.Controls.TextBlock>().Single().Text).IsEqualTo("Mode");
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellComboBoxSelectionTests"
```

Expected: **compile error** — no `showLabel` parameter.

- [ ] **Step 3: Add the flag to all four**

The pattern is identical in each. In `DwellSlider`, `DwellComboBox` and `DwellColorPicker`, add a final defaulted parameter to the primary constructor:

```csharp
        int dwellMs,
        bool showLabel = true)
```

document it:

```csharp
    /// <param name="showLabel">
    /// Whether this composite draws its own heading above its controls. False when the host
    /// lays the name out itself — the settings window's two-column rows put the name in their
    /// left column, and a second copy inside the composite would duplicate it.
    /// </param>
```

store it:

```csharp
    private readonly bool _showLabel;
```

```csharp
        _showLabel = showLabel;
```

and gate both lifecycle calls:

```csharp
    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        if (_showLabel)
            _label.Attach(parent);
        // ... the rest unchanged
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
        if (_showLabel)
            _label.Detach(parent);
        // ... the rest unchanged
    }
```

`DwellComboBox` has a convenience overload that chains to the primary constructor — add `showLabel` to it too and forward it, so both entry points behave alike.

`DwellToggle` (from plan 2) has no `DwellLabel`; its name is the `_label` `TextBlock` in the row's left column. Gate that instead — in the constructor, after the row `Grid` is built:

```csharp
        _label.Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed;
```

Use `Collapsed`, not `Hidden`: collapsed takes no space, so the row shrinks to the switch and the star column gives its width back.

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

Expected: green, baseline + 2. The Supervisor path is unchanged because the default is `true`; confirm with:

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj
```

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/
git commit -m "feat(controls): composites can defer their heading to the host"
```

---

## Task 4: Two-column rows

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` — `CreateControl` and new consts
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml` — card around `ContentPanel`
- Modify: `tests/EyeCtrl.App.Tests/Settings/SettingsRowLayoutTests.cs`

**The assembly goes in `CreateControl`, not `RenderSpec`.** `SettingsAutoPagination.MeasureRowHeight` calls `CreateControl` to build the real control and measure it. Anything that changes rendered height and lives only in `RenderSpec` makes every measured height a lie and every page count wrong.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EyeCtrl.App.Tests/Settings/SettingsRowLayoutTests.cs`:

```csharp
    private const double RadiusPhysical = 50;
    private const int DwellMs = 800;
    // A real content column at 1920x1080 — wide enough for the two-column form. The narrow
    // case has its own test; using 416 here would silently make every assertion below about
    // the stacked form instead.
    private const double ColumnWidth = 1424;

    /// <summary>Builds a row against a wide column, i.e. in its two-column form.</summary>
    private static object Build(SettingsRowSpec row) =>
        SettingsWindow.CreateControl(row, new NullRegistrar(), RadiusPhysical, DwellMs, ColumnWidth);

    [StaFact]
    public void ALabelledRow_PutsItsNameAndControlOnOneLine()
    {
        var row = new SliderSpec("s", "Smoothing Factor", 0, 1, 0.1, 1, () => 0.5, _ => { })
        {
            Description = "Higher is more responsive; lower is smoother."
        };

        var composite = (IDwellComposite)Build(row);
        var panel = new StackPanel { Width = ColumnWidth };
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        composite.Attach(panel);

        // One element per row, and it is a two-column grid — not a heading stacked on a control.
        Check.That(panel.Children.Count).IsEqualTo(1);
        var grid = (Grid)panel.Children[0];
        Check.That(grid.ColumnDefinitions.Count).IsEqualTo(2);
    }

    [StaFact]
    public void ALabelledRow_DrawsBothItsNameAndItsDescription()
    {
        var row = new SliderSpec("s", "Smoothing Factor", 0, 1, 0.1, 1, () => 0.5, _ => { })
        {
            Description = "Higher is more responsive; lower is smoother."
        };

        var panel = new StackPanel { Width = ColumnWidth };
        ((IDwellComposite)Build(row)).Attach(panel);

        var texts = Descendants<TextBlock>(panel).Select(t => t.Text).ToList();
        Check.That(texts).Contains("Smoothing Factor");
        Check.That(texts).Contains("Higher is more responsive; lower is smoother.");
    }

    [StaFact]
    public void ARowWithoutADescription_DrawsOnlyItsName()
    {
        var row = new SliderSpec("s", "Smoothing Factor", 0, 1, 0.1, 1, () => 0.5, _ => { });

        var panel = new StackPanel { Width = ColumnWidth };
        ((IDwellComposite)Build(row)).Attach(panel);

        // The name is there; nothing empty is drawn beneath it.
        var texts = Descendants<TextBlock>(panel).Select(t => t.Text).ToList();
        Check.That(texts).Contains("Smoothing Factor");
        Check.That(texts.Where(string.IsNullOrWhiteSpace)).IsEmpty();
    }

    /// <summary>
    /// The reason the pitch exists. Two 100 DIP hit circles whose centres are 100 DIP apart are
    /// tangent — a one-pixel drift picks the neighbour. The clearance floor is derived from the
    /// colour-swatch grid, the only adjacent-target pitch in the app already pinned by a
    /// geometry test (SwatchTargetGeometryTests).
    /// </summary>
    [StaFact]
    public void ALabelledRow_LeavesTheSwatchGridsClearanceBetweenAdjacentTargets()
    {
        var swatchClearance =
            DwellColorPicker.SwatchHeight + 2 * DwellColorPicker.SwatchVerticalMargin
            - 2 * SettingsWindow.ColorSwatchRadius;

        var row = new SliderSpec("s", "Smoothing Factor", 0, 1, 0.1, 1, () => 0.5, _ => { });
        var measured = SettingsAutoPagination.MeasureRowHeight(row, ColumnWidth, RadiusPhysical, DwellMs);

        var clearance = measured - 2 * SettingsWindow.ControlBtnRadius;

        Check.WithCustomMessage(
                $"a labelled row is {measured:F1} DIP tall, leaving {clearance:F1} DIP between "
                + $"the hit circles of vertically adjacent controls. The swatch grid, which is "
                + $"pinned by SwatchTargetGeometryTests, leaves {swatchClearance:F1}.")
            .That(clearance).IsGreaterOrEqualThan(swatchClearance);
    }

    /// <summary>
    /// The narrowest supported display cannot take a two-column row at all: the slider is 404
    /// DIP of a 416 DIP column, so no name column fits beside it. The row must stack there
    /// rather than clip — DwellSlider's row is a horizontal StackPanel, composites are
    /// deliberately uncapped, and a clipped row leaves registered hit circles over blank card.
    ///
    /// 800 DIP is the display SettingsContentFitTests exists to support ("high scaling is
    /// ordinary for users of this application").
    /// </summary>
    [StaFact]
    public void ANarrowColumn_StacksRatherThanClipping()
    {
        const double NarrowColumn = 416;
        var row = new SliderSpec("s", "Smoothing Factor", 0, 1, 0.1, 1, () => 0.5, _ => { })
        {
            Description = "Higher is more responsive; lower is smoother."
        };

        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            row, new NullRegistrar(), RadiusPhysical, DwellMs, NarrowColumn);

        var panel = new StackPanel { Width = NarrowColumn };
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        composite.Attach(panel);
        panel.Measure(new Size(NarrowColumn, double.PositiveInfinity));

        Check.WithCustomMessage(
                "at 416 DIP the row must stack — a two-column Grid there would clip the slider")
            .That(panel.Children[0]).IsInstanceOf<StackPanel>();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "the stacked row wants {0:F2} DIP against a {1:F0} DIP column. It clips rather "
                + "than wraps, so the stepper buttons would draw past the card with their hit "
                + "circles still registered.",
                panel.DesiredSize.Width, NarrowColumn))
            .That(panel.DesiredSize.Width).IsLessOrEqualThan(NarrowColumn);
    }

    /// <summary>
    /// And the converse: a real content column is wide enough, so the two-column form is not
    /// dead code that only the tests ever see. 1424 DIP is the column at 1920x1080.
    /// </summary>
    [StaFact]
    public void ARealContentColumn_GetsTheTwoColumnForm()
    {
        const double WideColumn = 1424;
        var row = new SliderSpec("s", "Smoothing Factor", 0, 1, 0.1, 1, () => 0.5, _ => { });

        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            row, new NullRegistrar(), RadiusPhysical, DwellMs, WideColumn);

        var panel = new StackPanel { Width = WideColumn };
        composite.Attach(panel);

        Check.That(panel.Children[0]).IsInstanceOf<Grid>();
        Check.That(WideColumn).IsGreaterOrEqualThan(SettingsWindow.TwoColumnMinimumWidth);
    }

    /// <summary>
    /// Button rows are deliberately NOT two-column: the label is inside the button, and
    /// SettingsAutoPaginationTests pins their measured height at exactly 124.0.
    /// </summary>
    [StaFact]
    public void AButtonRow_IsUnchanged()
    {
        var row = new ButtonSpec("b", "Do it", () => { }, ButtonKind.Default, Width: 300, Height: 100);

        Check.That(Build(row)).IsInstanceOf<DwellButton>();
        Check.That(SettingsAutoPagination.MeasureRowHeight(row, 416.0, RadiusPhysical, DwellMs))
            .IsEqualTo(124.0);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private sealed class NullRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
```

`Descendants` walks the **visual** tree, which requires the panel to have been measured. Add this before each `Descendants` call:

```csharp
        panel.Measure(new Size(ColumnWidth, double.PositiveInfinity));
        panel.Arrange(new Rect(new Size(ColumnWidth, panel.DesiredSize.Height)));
```

Add these usings **alongside** the ones Task 2 already put in the file — this is not a replacement list, and dropping Task 2's `using EyeCtrl.App.Views.Settings;` would take every spec type with it:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~SettingsRowLayoutTests"
```

Expected: the layout tests fail — today a slider attaches a `DwellLabel` **and** a row, so `panel.Children.Count` is 2, not 1.

- [ ] **Step 3: Add the row consts**

In `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`, beside `ControlBtnRadius`:

```csharp
    /// <summary>
    /// Minimum height of a labelled settings row, in DIPs.
    ///
    /// Not an aesthetic choice. A control's hit circle is registered at its centre with radius
    /// <see cref="ControlBtnRadius"/>, so rows this far apart leave 50 DIP between the circles of
    /// vertically adjacent controls — at or above the 40 the colour-swatch grid leaves, which is
    /// the only adjacent-target pitch already pinned by a geometry test. A minimum, not a fixed
    /// height: a description that wraps grows the row rather than being clipped.
    /// Guarded by SettingsRowLayoutTests.ALabelledRow_LeavesTheSwatchGridsClearance...
    /// </summary>
    internal const double LabelledRowMinHeight = 150;

    /// <summary>
    /// Width of a labelled row's name column, in DIPs, when the row is drawn two-column.
    ///
    /// The widest control that sits in a labelled row is the slider at 404 DIP, so a two-column
    /// row wants 784 — nearly twice the 416 DIP column a 800 DIP display leaves. That is not a
    /// number to tune: the slider alone is 404 of 416, so no name column of any width fits
    /// beside it there. Narrow columns therefore fall back to stacked rows; see
    /// <see cref="TwoColumnMinimumWidth"/>.
    /// </summary>
    internal const double RowNameColumnWidth = 380;

    /// <summary>
    /// The content-column width at or above which a labelled row is drawn two-column.
    ///
    /// Below it the row stacks — name above control, as it has always been — because the
    /// alternative is a 404 DIP slider clipped by a horizontal StackPanel that does not wrap,
    /// leaving live hit circles over blank card. Composites are deliberately uncapped
    /// (see <see cref="ConstrainToContentWidth"/>'s note), so nothing else would catch it.
    ///
    /// 380 + 404 + a 24 DIP gutter. At 1920x1080 the column is 1424 and every row is
    /// two-column; at the 800 DIP display SettingsContentFitTests supports it is 416 and every
    /// row stacks. Guarded by SettingsRowLayoutTests.ANarrowColumn_StacksRatherThanClipping.
    /// </summary>
    internal const double TwoColumnMinimumWidth = 808;
```

- [ ] **Step 4: Thread the available width into the shared construction path**

The row has to know how much room it has, because below a threshold it must **not** be two-column (Step 5 explains why). `CreateControl` does not currently take a width; both its callers already have one. Add it as a required parameter — no default, so a missed call site is a compile error rather than a silently two-column row.

There are exactly four call sites:

| Site | Passes |
|---|---|
| `SettingsWindow.RenderSpec` (`:926`) | `ContentAvailableWidth` |
| `SettingsAutoPagination.MeasureRows` (`:85`) | its own `availableWidth` parameter |
| `SettingsAutoPaginationTests` (`:181`) | `416.0`, the budget that test already uses |
| `SettingsAutoPaginationTests` (`:196`) | `416.0`, likewise |

```csharp
    internal static object CreateControl(
        SettingsRowSpec row, IDwellRegistrar registrar, double radiusPhysical, int dwellMs,
        double availableWidth)
```

Both existing test call sites measure a `ButtonSpec` and a `LabelSpec`, neither of which is a labelled row, so their expectations — including the pinned `124.0` — are unaffected by the value passed. Pass the real 416 anyway rather than a placeholder.

- [ ] **Step 5: Assemble the row inside `CreateControl`**

Replace the four labelled arms so each builds its composite with `showLabel: false` and wraps it in a row. Keep the `ButtonSpec` and `LabelSpec` arms exactly as they are.

```csharp
            case SliderSpec s:
                return LabelledRow(s, availableWidth, new DwellSlider(registrar, s.Id, s.Label,
                    s.Min, s.Max, s.Step, s.Decimals, s.Get, s.Set, cat, radiusPhysical, dwellMs,
                    showLabel: false));
            case ToggleSpec t:
                return LabelledRow(t, availableWidth, new DwellToggle(registrar, t.Id, t.Label,
                    t.Get, t.Set, cat, radiusPhysical, dwellMs, showLabel: false));
            case ComboBoxSpec c:
                return LabelledRow(c, availableWidth, new DwellComboBox(registrar, c.Id, c.Label,
                    c.Display, c.Code ?? c.Display, c.Get, c.Set, cat, radiusPhysical, dwellMs,
                    showLabel: false));
            case ColorPickerSpec cp:
                return LabelledRow(cp, availableWidth, new DwellColorPicker(registrar, cp.Id,
                    cp.Label, cp.Get, cp.Set, cat,
                    DpiHelper.LogicalToPhysicalX(cp.RadiusLogical), dwellMs, showLabel: false));
```

**Why a row is allowed to refuse two-column.** The widest control that sits in a labelled row is the slider at 404 DIP. At the 800 DIP display `SettingsContentFitTests` exists to support, the content column is 416 — the slider alone is 404 of it. No name-column width fits beside that, so this is not a number to tune down: below `TwoColumnMinimumWidth` the row stacks instead, exactly as it does today.

Getting this wrong is not cosmetic. `DwellSlider`'s row is a horizontal `StackPanel`, which clips rather than wraps, and composites are deliberately left uncapped (see `ConstrainToContentWidth`'s note). An over-wide row would draw its stepper buttons past the card edge while their hit circles stayed registered — a live target over blank space, which is the gaze-trap this codebase guards against everywhere else.

Add the wrapper and its composite type below `CreateControl`:

```csharp
    /// <summary>
    /// Wraps <paramref name="inner"/> in a settings row: two-column where there is room for one,
    /// stacked where there is not.
    ///
    /// Built here rather than in <see cref="RenderSpec"/> because
    /// <see cref="SettingsAutoPagination.MeasureRowHeight"/> also goes through
    /// <see cref="CreateControl"/>. A row assembled only on the render path would be measured at
    /// its old height and every page count would be wrong.
    /// </summary>
    private static LabelledSettingsRow LabelledRow(
        SettingsRowSpec spec, double availableWidth, IDwellComposite inner) =>
        new(spec.Label, spec.Description, inner,
            twoColumn: availableWidth >= TwoColumnMinimumWidth);

    /// <summary>
    /// One settings row: the name and its control side by side where the column is wide enough,
    /// stacked where it is not.
    ///
    /// A composite in its own right so it slots into the existing three-way switch in
    /// <see cref="RenderSpec"/> and <c>SettingsAutoPagination.MeasureRows</c> without either
    /// learning a fourth case. Its children are the inner composite's children — the row itself
    /// registers no dwell target, because a name is not one.
    /// </summary>
    private sealed class LabelledSettingsRow : IDwellComposite
    {
        private readonly IDwellComposite _inner;
        private readonly Panel _root;
        private readonly StackPanel _controlHost;

        public LabelledSettingsRow(
            string label, string? description, IDwellComposite inner, bool twoColumn)
        {
            _inner = inner;

            var name = new TextBlock
            {
                Text = label,
                Foreground = Tokens.BrushTextPrimary,
                FontSize = Tokens.FontSizeTitle,
                FontWeight = Tokens.FontWeightMedium,
                TextWrapping = TextWrapping.Wrap
            };

            var nameColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameColumn.Children.Add(name);

            // Only when there is something to say. An empty TextBlock would still take a line
            // box and push every row taller for nothing.
            if (!string.IsNullOrWhiteSpace(description))
            {
                nameColumn.Children.Add(new TextBlock
                {
                    Text = description,
                    Foreground = Tokens.BrushTextDim,
                    FontSize = Tokens.FontSizeSubtitle,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            _controlHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // Right-aligned two-column; left when stacked, so a stacked row keeps the same
                // column axis every other row kind sits on.
                HorizontalAlignment = twoColumn
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (twoColumn)
            {
                var grid = new Grid { MinHeight = LabelledRowMinHeight };
                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(RowNameColumnWidth) });
                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(nameColumn, 0);
                Grid.SetColumn(_controlHost, 1);
                grid.Children.Add(nameColumn);
                grid.Children.Add(_controlHost);
                _root = grid;
            }
            else
            {
                // The stacked form is today's shape: heading, then controls. It keeps the same
                // minimum height, so the hit-circle clearance the pitch buys does not depend on
                // how wide the user's display happens to be.
                nameColumn.VerticalAlignment = VerticalAlignment.Top;
                nameColumn.Margin = new Thickness(0, 0, 0, 12);
                var stack = new StackPanel { MinHeight = LabelledRowMinHeight };
                stack.Children.Add(nameColumn);
                stack.Children.Add(_controlHost);
                _root = stack;
            }
        }

        public IEnumerable<DwellControl> Children => _inner.Children;

        public void Attach(Panel parent)
        {
            _inner.Attach(_controlHost);
            parent.Children.Add(_root);
        }

        public void Detach(Panel parent)
        {
            parent.Children.Remove(_root);
            _inner.Detach(_controlHost);
        }
    }
```

**Note the `Attach` order.** The inner composite is attached to the host *before* the row goes into the parent, so the row measures with its content already in place. `Detach` reverses it.

- [ ] **Step 6: Put the rows on a card**

In `src/EyeCtrl.App/Views/SettingsWindow.xaml`, wrap `ContentPanel` in a `Border`. Keep `ContentPanel`'s `x:Name` and `Margin` exactly as they are — `ContentAvailableWidth` reads both off this element:

```xml
            <!-- Card: gives the row list an edge, so it reads as a group rather than as
                 items floating on the window. Its padding comes off the content column,
                 which is why ContentAvailableWidth reads it off this element by name. -->
            <Border x:Name="ContentCard" Grid.Row="0"
                    CornerRadius="{StaticResource LargeCornerRadius}"
                    Background="{StaticResource BrushSurfaceMedium}"
                    Padding="24,8"
                    VerticalAlignment="Top">
                <StackPanel x:Name="ContentPanel"/>
            </Border>
```

`ContentPanel` loses its own `Margin="16"` to the card's padding — so update `ContentAvailableWidth` to read the card instead:

```csharp
            return Width
                - RootGrid.ColumnDefinitions[0].Width.Value
                - ContentArea.Margin.Left - ContentArea.Margin.Right
                - ContentCard.Padding.Left - ContentCard.Padding.Right;
```

and `ContentAvailableHeight` likewise:

```csharp
            return Height
                - ContentArea.Margin.Top - ContentArea.Margin.Bottom
                - ContentCard.Padding.Top - ContentCard.Padding.Bottom
                - PaginationReserveHeight;
```

Both doc-comments name `ContentPanel`'s margin as one of the two nested insets — update them to name the card's padding instead, or the next reader will go looking for a margin that is gone.

**And update `SettingsContentFitTests`' own derivation, not just production's.** That suite re-derives the content column by parsing `SettingsWindow.xaml`, and its `Margin(root, name)` helper returns `default` for an element with no `Margin` attribute — **silently** (`SettingsContentFitTests.cs:144-146`). Leaving it reading `ContentPanel`'s now-absent margin makes it compute a column 32 DIP wider than the real one.

There are **five** such sites, and they split two ways. Three *subtract* the inset:

| Line | Context |
|---|---|
| `:200` | `MeasurePickerButton` |
| `:337` | `TheSidebarAndTheContentColumn` |
| `:384` | `TheWidestSeparatorLabel` |

Two *apply* it, rebuilding the panel with the inset as its own margin — easy to miss because they are not subtractions at all:

| Line | Context |
|---|---|
| `:183` | `MeasurePickerButton`'s rebuilt tree |
| `:477` | `BuildContentTree` |

Plus the slider guard plan 2 added. Point the subtractions at `ContentCard`'s `Padding`, and wrap the rebuilt panels in a `Border` carrying that padding so the measured tree matches production. Add a `Padding(root, name)` helper beside `Margin` rather than parsing inline twice. Two doc-comments (`:154`, `:454`) also describe the old shape.

**Do not rely on the negative control to catch a partial job.** `WithoutTheConstraint_ThatSameButtonOverruns` cannot detect a stale derivation: with the old numbers the column computes as 448 instead of 400, the capped button is capped to 448 and still fits, and the unconstrained 460 DIP button still overruns 448. Both tests stay green while measuring against a column that does not exist. The only real check is enumerating the five sites. Done correctly the arithmetic is `720 − 240 − 32 − 48 = 400`, and the capped picker clears the panel edge by exactly 0.00 DIP — which passes the suite's `< 0.01` tolerance, but leaves nothing spare, so treat any further widening of the card padding as a change that needs re-measuring.

- [ ] **Step 7: Run**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

Expected: the new layout tests pass, `SettingsAutoPaginationTests` and `SettingsContentFitTests` stay green. Page counts **will** change — that is the point, and those tests assert relationships (fits-the-budget), not counts. If one asserts a count, read it before touching it.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Views/ tests/EyeCtrl.App.Tests/Settings/SettingsRowLayoutTests.cs
git commit -m "feat(settings): name and control on one line, at a 150 DIP row pitch"
```

---

## Task 5: A page header that names the tab

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml` — header row
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` — `_tabDefs` gains a description; header wiring; height budget
- Modify: `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs`

- [ ] **Step 1: Add the header to the XAML**

Change `ContentArea`'s rows from `"*, Auto"` to `"Auto, *, Auto"` and add the header as row 0, shifting the card and pagination down:

```xml
        <Grid x:Name="ContentArea" Grid.Column="1" Margin="16" RowDefinitions="Auto, *, Auto">

            <!-- Page header. Fixed height so ContentAvailableHeight can subtract it before the
                 first layout pass, the same reason every other budget here reads Height and
                 never ActualHeight. -->
            <StackPanel x:Name="HeaderPanel" Grid.Row="0" Height="96" Margin="0,0,0,12">
                <TextBlock x:Name="HeaderTitle"
                           Foreground="{StaticResource BrushTextPrimary}"
                           FontSize="{StaticResource FontSizeDisplay}"
                           FontWeight="{StaticResource FontWeightStrong}"/>
                <TextBlock x:Name="HeaderDescription"
                           Foreground="{StaticResource BrushTextSecondary}"
                           FontSize="{StaticResource FontSizeSubtitle}"
                           TextWrapping="Wrap"
                           Margin="0,8,0,0"/>
            </StackPanel>
```

and move the card and pagination to rows 1 and 2 (`Grid.Row="1"` on `ContentCard`, `Grid.Row="2"` on `PaginationPanel`). The hidden `Settings_Panel_Content` UIA anchor at `SettingsWindow.xaml:60-61` is a sibling currently on `Grid.Row="0"` — move it to row 1 as well, or it lands in the header. Only the `Category=UI` suite reads it, so CI will not catch a slip here.

- [ ] **Step 2: Give each tab a description**

In `_tabDefs`, add a fifth tuple element. `SettingsTabDriftTests`' regex matches only the first element, so this is safe — but re-run that test rather than trusting the sentence.

```csharp
        _tabDefs =
        [
            ("Cursor",   "◎", "Cursor", "How raw gaze is filtered, and how the cursor ring is drawn.", BuildCursorPages),
            ("Dwell",    "⏱", "Dwell Times", "How long gaze must rest on a target before it activates.", BuildDwellPages),
            ("Selection", "⊕", "Selection", "How EyeCtrl decides what you are looking at.", BuildSelectionPages),
            ("Actions",  "⊙", "Action Menu", "The radial menu that opens after an activation.", BuildActionMenuPages),
            ("Layout",   "⊞", "Layout", "Where the overlays sit on screen.", BuildLayoutPages),
            ("Keyboard", "⌨", "Keyboard", "On-screen keyboard size, layout and word prediction.", BuildKeyboardPages),
            ("Models",    "⬡", "Models", "Language models used for word prediction.", BuildModelsPages),
            ("Toolbar",  "▣", "Toolbar", "The always-available utility bar.", BuildToolbarPages),
            ("Calibration", "⌖", "Calibration", "Correcting the offset between where you look and where the cursor lands.", BuildCalibrationPages),
            ("General",  "⚙", "General", "Startup, theme, and resetting settings.", BuildGeneralPages),
            ("QuickActions", "⚡", "Quick Actions", "The grid of one-dwell shortcuts.", BuildQuickActionsPages)
        ];
```

Update the field's declared type to match:

```csharp
    private readonly (string Id, string Icon, string Label, string Description,
        Func<IReadOnlyList<PageSpec>> PageBuilder)[] _tabDefs;
```

and fix **all three** deconstructions that read it — there are three, not two:

| Site | Today | Becomes |
|---|---|---|
| `:235` page-builder loop | `foreach (var (id, _, _, pageBuilder) in _tabDefs)` | `(id, _, _, _, pageBuilder)` |
| `:359` `BuildTabButtons` | `foreach (var (id, icon, label, _) in _tabDefs)` | `(id, icon, label, _, _)` |
| `:487` `RenderTabPage` | `var (id, _, _, _) = _tabDefs[i];` | `var (id, _, _, _, _) = _tabDefs[i];` |

Miss the third and the build fails — which is a cheap failure, but the plan should not be the thing that caused it.

- [ ] **Step 3: Fill the header on tab change**

Add the method and call it from `RebuildContent`, before the pages are built:

```csharp
    /// <summary>Points the header at the active tab. Silent if the id is unknown — the header is
    /// chrome, and a missing description must never stop the page rendering.</summary>
    private void UpdateHeader()
    {
        var def = Array.Find(_tabDefs, t => t.Id == _viewModel.Settings.CurrentTabName);
        HeaderTitle.Text = def.Label ?? string.Empty;
        HeaderDescription.Text = def.Description ?? string.Empty;
    }
```

- [ ] **Step 4: Subtract the header from the height budget**

`ContentAvailableHeight` must now account for it, read off the element:

```csharp
            return Height
                - HeaderPanel.Height - HeaderPanel.Margin.Top - HeaderPanel.Margin.Bottom
                - ContentArea.Margin.Top - ContentArea.Margin.Bottom
                - ContentCard.Padding.Top - ContentCard.Padding.Bottom
                - PaginationReserveHeight;
```

- [ ] **Step 5: Guard that the header did not starve the column**

Add to `SettingsContentFitTests`:

```csharp
    /// <summary>
    /// The page header takes height off every content page at once, exactly as the sidebar
    /// widening took width. A header that leaves less than one labelled row's worth of room
    /// would paginate every tab down to one row per page.
    /// </summary>
    [StaFact]
    public void TheContentColumn_StillFitsALabelledRow_BelowTheHeader()
    {
        var root = Xml();
        var header = Named(root, "HeaderPanel");
        var headerHeight = double.Parse(
            header.Attribute("Height")!.Value, CultureInfo.InvariantCulture);
        var headerMargin = Margin(root, "HeaderPanel");
        var contentArea = Margin(root, "ContentArea");

        var available = NarrowDisplayWidthDip * 0.75 * SettingsWindow.WindowSizeRatio
            - headerHeight - headerMargin.Top - headerMargin.Bottom
            - contentArea.Top - contentArea.Bottom;

        Check.WithCustomMessage(
                $"a {headerHeight} DIP header leaves {available:F0} DIP, under one "
                + $"{SettingsWindow.LabelledRowMinHeight} DIP row")
            .That(available).IsStrictlyGreaterThan(SettingsWindow.LabelledRowMinHeight);
    }
```

- [ ] **Step 6: Run everything**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

Expected: green, including `SettingsTabDriftTests` — if it fails, the 5-tuple broke its regex and the regex must be widened deliberately, not the tab list reverted.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Views/ tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs
git commit -m "feat(settings): each tab gets a page header naming what it covers"
```

---

## Task 6: Whole-suite verification

- [ ] **Step 1: Build**

```bash
dotnet build EyeCtrl.slnx
```

- [ ] **Step 2: Every suite, one at a time**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj
```

- [ ] **Step 3: Sanity-check the pagination arithmetic by hand**

At 1920×1080 the window is 972 tall. Subtract the header block (96 + 12), `ContentArea`'s margins (32), the card padding (16) and `PaginationReserveHeight` (~112): about **704 DIP**, or **four** 150 DIP rows per page against today's three. Confirm the numbers the code produces match that order of magnitude; a page holding one row means a budget term is wrong.

---

## Task 7: Live verification — **requires the user's explicit go-ahead**

- [ ] **Step 1: Ask first**

The user drives this machine solely by eye-tracking; launching EyeCtrl seizes their only input channel. **Stop and ask**, saying how long the run will take.

- [ ] **Step 2: On a yes — check the layout, then close promptly**

1. Settings → Cursor: each setting's name and control sit on one line, with the description under the name and the control right-aligned.
2. The header reads "Cursor" with its one-line description above the card.
3. Dwelling the `+` on Smoothing Factor activates *that* control — not the row above or below. Try it on two adjacent rows.
4. Settings → General: toggles show name left, switch right, and dwelling the switch works.
5. Settings → Quick Actions: the picker button still fits the column and is not clipped.
6. A tab whose rows have no descriptions (anything but Cursor) draws names only, with no empty gap under them.

- [ ] **Step 3: Report** exactly what happened, including anything that did not work.

---

## Self-review

**Design coverage.** Direction B's content area is four things: two-column rows (Task 4), the 150 DIP pitch (Task 4), the card (Task 4) and the page header (Task 5). All present — with the caveat that two-column is conditional: below `TwoColumnMinimumWidth` the row stacks, because at the 416 DIP narrowest supported column the slider alone is 404 and no name column fits beside it. That is a designed fallback, not a gap, and `ANarrowColumn_StacksRatherThanClipping` pins it. The sidebar grouping, the stroke icons and the footer are in "Out of scope" with a stated reason each.

**The one thing most likely to go wrong.** Assembling the row anywhere but `CreateControl` silently decouples measurement from rendering. It is called out in the architecture line, in the impact table, in Task 4's preamble and in `LabelledRow`'s doc comment.

**Type consistency.** `SettingsRowSpec.Description` (Task 2) is read in Task 4's `LabelledRow`. `showLabel` (Task 3) is passed in Task 4's `CreateControl` arms. `LabelledRowMinHeight` and `RowNameColumnWidth` (Task 4 Step 3) are used in Task 4 Step 4 and Task 5 Step 5. `ContentCard` (Task 4 Step 5) is read by both budgets in Tasks 4 and 5.

**Ordering.** Task 4 needs Task 3's `showLabel` and Task 2's `Description`. Task 5's budget builds on Task 4's. Do not reorder.

**Residual risk.** `CreateControl` is the shared construction path for rendering and measurement, and this plan changes both its signature and what it returns for four of six row kinds. The mitigation is that `LabelledSettingsRow` is an `IDwellComposite`, which both consumers already switch on, and that Task 4's clearance test measures through `SettingsAutoPagination.MeasureRowHeight` — the measurement path — rather than through the renderer. If those two ever disagree, that test is where it shows.
