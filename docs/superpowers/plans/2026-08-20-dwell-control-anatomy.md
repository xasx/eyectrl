# Dwell Control Anatomy Implementation Plan

> **Sequence: plan 2 of 3** of the settings restyle — `dwell-progress-feedback` -> `dwell-control-anatomy` -> `settings-two-column-rows`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the four settings dwell controls the anatomy the approved Direction B design specifies — a real switch on the toggle, a non-reflowing option border, a value plate that shows where a number sits in its range, and colour swatches whose names are visible rather than hidden in a tooltip no gaze user can reach.

**Architecture:** Four surgical rewrites inside `EyeCtrl.Controls`, each confined to one control's constructor and refresh path. Three keep their existing type. `DwellToggle` changes from `DwellControl` to `IDwellComposite` so its dwell target is the switch rather than the whole row — the only structural change, sequenced last.

**Tech Stack:** .NET 10.0.102 SDK, WPF (`net10.0-windows`), xUnit v3 (`xunit.v3` 3.2.2) + NFluent 3.1.0. Solution is `EyeCtrl.slnx`.

**Baseline:** `main` @ `153f318a`. **Plan 1 is already landed** (`ebe18035..153f318a`, PR #105), so the prerequisite below is satisfied — run the greps anyway, they cost nothing. This plan consumes `DwellButton.Content`, `DwellButton.ProgressBar`, `DwellButton.ProgressBarHeight` and `DwellControl.UpdateVisualForTest`, all introduced there.

---

## Prerequisite

**Plan 1 is merged** (PR #105), so this is a formality rather than a gate. Every task below assumes `DwellButton` already exposes `Content` and a `ProgressBar`, and that dwell feedback no longer washes the fill. Verify:

```bash
grep -n "public UIElement? Content" src/EyeCtrl.Controls/DwellButton.cs
grep -n "public Rectangle ProgressBar" src/EyeCtrl.Controls/DwellButton.cs
```

Both must hit. If they do not, stop — this plan will not compile.

---

## The four changes, and why each is not merely cosmetic

**1. Option groups reflow when you pick one.** `DwellComboBox.cs:216` swaps a 1px border for a 3px one on selection. The border sits outside the child, so the pill grows 4 DIP in both axes and the `WrapPanel` re-lays the whole row — options visibly jump, and on a row near its wrap point one can hop to the next line. Every hit circle in that row moves with them. A constant 2px border that only changes colour removes the reflow entirely.

**2. A number with no range.** `DwellSlider` shows `0.5` in a 140 DIP `TextBlock` between two buttons. Nothing on screen says the range is 0–1, or that the step is 0.1, so a user dwelling `+` cannot tell whether they are near an end. A plate with the value at 34px over a min/max rail says both without a word of help text.

**3. Colour swatch names are unreachable.** `DwellColorPicker.cs:143` puts each preset's name in `btn.Root.ToolTip` and `:144` sets the button's content to `null`. **A tooltip requires hover. A gaze-only user has no hover.** The four swatches are therefore four unlabelled coloured rectangles, and the names ("Accent", "Green", "Amber", "Light") are effectively invisible to the one person who has to use them. Drawing the label inside the swatch is an accessibility fix wearing a cosmetic hat.

**4. The toggle's ON label is 2.91:1.** `#D6DEF5` on `#4A8F5C`, under the 3:1 large-text bar — the resting value plan 1 explicitly did not fix, because fixing it needs the label off the green. Moving state into a switch puts the label on the row surface at 10.3:1 and leaves green carrying only the track, which as a non-text UI component needs 3:1 and gets 3.55:1.

---

## Impact analysis (run before writing this plan, per CLAUDE.md)

| Target | Direction | Risk | Direct | Notes |
|--------|-----------|------|--------|-------|
| `DwellToggle` | upstream | **CRITICAL** | 3 | Type changes here. Reaches `SupervisorSetupWindow`. |
| `DwellButton` | upstream | **CRITICAL** | 8 | Consumed, not modified. |

**The `DwellToggle` type change, traced to every consumer.** Going from `DwellControl` to `IDwellComposite` removes the public `Root`, `Attach(Panel)`/`Detach(Panel)` stay (both types declare them), and `Children` appears.

| Consumer | Uses | Verdict |
|---|---|---|
| `SettingsWindow.CreateControl` | returns `object` | fine |
| `SettingsWindow.RenderSpec` | `switch` over `DwellControl` / `IDwellComposite` / `DwellLabel` | already has the arm |
| `SettingsAutoPagination.MeasureRows` | same three-way `switch` | already has the arm |
| `SupervisorSetupWindow.xaml.cs:85,105,115,116` | `var` + `Attach(ToggleHost)` | compiles unchanged |
| `DwellToggleVisualTests` | subclasses it, calls `protected ApplyProgress`/`OnActivated` | **must be rewritten** (Task 5) |
| `DwellProgressContrastTests` (from plan 1) | `toggle.Root.Background` | **must be rewritten** (Task 5) |

**Why the Supervisor survives it.** `src/EyeCtrl.Supervisor/UI/NoopDwellRegistrar.cs` is what those toggles register against, and grepping the whole Supervisor project for `UpdateVisual`, `UpdatePosition`, `DwellControl` or `IDwellComposite` finds nothing but that file. The Supervisor never drives a per-frame dwell loop — its controls are mouse-driven in practice — so it needs neither `Root` nor `Children`. Confirmed by reading, not assumed.

---

## Global Constraints

- **Layout uses literal DIPs, not tokens.** Tokens are for colour, corner radius, font and border thickness only.
- **NFluent 3.1.0.** No `Assert.*`. WPF tests use `[StaFact]`.
- **`EyeCtrl.App.Tests` needs `--filter "Category!=UI"`.** Controls / ViewModels / Supervisor suites run unfiltered.
- **No `InternalsVisibleTo` additions.** `EyeCtrl.Controls`'s IVT reaches `EyeCtrl.Controls.Tests` only; the width guard in Task 3 needs `SettingsWindow` internals, so it goes in `EyeCtrl.App.Tests`.
- **No per-tick allocation** in `ApplyProgress` or any refresh called at gaze rate. Mutate existing brushes and transforms.
- **The 100 DIP WYSIWYG floor holds.** No dwell target is drawn smaller than the hit circle it registers.
- **No state carried by colour alone.** Knob position and a state word back the switch's green; the radio glyph backs the option border.
- **File-scoped namespaces**, Allman braces, `_camelCase` fields, nullable enabled.
- **Don't run two test suites at once** — named-pipe contention is machine-global.
- **`dotnet test` output is German.** Read `Fehlgeschlagen: N`; never grep for `Failed`.
- **Commit per task.** Conventional commits. No push unless asked.
- **Never launch the app or run `Category=UI` tests without asking first.** Task 7 is gated.

---

## Out of scope

- **Two-column rows, the page header, the grouped sidebar, the 150 DIP row pitch.** That is the layout plan, and it moves geometry `SettingsContentFitTests` and `SettingsAutoPagination` measure.
- **Stroke `Path` tab icons.** Those are `SettingsWindow` tab glyphs, not `EyeCtrl.Controls`; they belong with the layout work.
- **Palette changes.** `Tokens.ToggleOn` stays as it is; Task 5 fixes the contrast by moving the label, not by moving the colour.

---

## File Structure

| File | Responsibility | Task |
|------|----------------|------|
| `src/EyeCtrl.Controls/DwellComboBox.cs` | Constant 2px option border | 2 |
| `tests/EyeCtrl.Controls.Tests/DwellComboBoxSelectionTests.cs` | **New** — selection must not change geometry | 2 |
| `src/EyeCtrl.Controls/DwellSlider.cs` | Value plate + range rail | 3 |
| `tests/EyeCtrl.Controls.Tests/DwellSliderPlateTests.cs` | **New** — rail tracks value; plate updates | 3 |
| `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs` | Slider row must fit the narrow column | 3 |
| `src/EyeCtrl.Controls/DwellColorPicker.cs` | Visible swatch names | 4 |
| `tests/EyeCtrl.Controls.Tests/DwellColorPickerLabelTests.cs` | **New** — every swatch names itself on screen | 4 |
| `src/EyeCtrl.Controls/DwellToggle.cs` | Composite; label + switch | 5 |
| `tests/EyeCtrl.Controls.Tests/DwellToggleVisualTests.cs` | Rewritten for the composite | 5 |
| `tests/EyeCtrl.Controls.Tests/DwellProgressContrastTests.cs` | Toggle arm rewritten | 5 |

---

## Task 1: Worktree + baseline

- [ ] **Step 1: Confirm the prerequisite and that main has not moved**

```bash
git fetch && git log --oneline -1 origin/main
```

Plan 1's commits must be in the history. If main has moved beyond them, re-read the four control files before continuing — a settings-polish workstream has been landing on exactly these.

- [ ] **Step 2: Create the worktree**

```bash
git worktree add .worktrees/dwell-control-anatomy -b feat/dwell-control-anatomy origin/main
```

- [ ] **Step 3: Build**

```bash
dotnet build EyeCtrl.slnx
```

Expected: `Build succeeded`.

- [ ] **Step 4: Baseline, one suite at a time**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj
```

Record each total. Red before you start means **stop and report**.

---

## Task 2: Option selection stops reflowing the row

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellComboBox.cs` (constructor `:136-143`, `UpdateOptionSelectionStyles` `:210-218`)
- Create: `tests/EyeCtrl.Controls.Tests/DwellComboBoxSelectionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// Selecting an option must not change any option's size. The original code swapped a 1px
/// border for a 3px one, and a Border's thickness sits outside its child — so the chosen pill
/// grew 4 DIP in both axes and the WrapPanel re-laid the whole row. Options jumped under the
/// user's gaze, and every hit circle in the row moved with them.
/// </summary>
public class DwellComboBoxSelectionTests
{
    /// <summary>A three-option combo over a captured selection, "Alpha" chosen.</summary>
    private static DwellComboBox Build()
    {
        var selected = "Alpha";
        return new DwellComboBox(
            new FakeRegistrar(), "cb", "Mode", ["Alpha", "Beta", "Gamma"],
            () => selected, v => selected = v,
            DwellCategory.Control, 50, 800);
    }

    [StaFact]
    public void EveryOption_HasTheSameBorderThickness_RegardlessOfSelection()
    {
        var combo = Build();

        var thicknesses = combo.Children
            .Select(c => ((FrameworkElement)c.Root.Parent).GetValue(System.Windows.Controls.Border.BorderThicknessProperty))
            .Cast<Thickness>()
            .ToList();

        Check.That(thicknesses.Select(t => t.Left).Distinct().Count()).IsEqualTo(1);
        Check.That(thicknesses.First().Left).IsEqualTo(2.0);
    }

    [StaFact]
    public void SelectionIsCarriedByColour_NotByThickness()
    {
        var combo = Build();

        var borders = combo.Children
            .Select(c => (System.Windows.Controls.Border)c.Root.Parent)
            .ToList();

        // Compare COLOURS, not brush instances. SolidColorBrush inherits DependencyObject's
        // reference equality — every option gets its own `new SolidColorBrush(...)`, so
        // Distinct() over the brushes themselves counts three, not two, however they are painted.
        var colours = borders.Select(b => ((SolidColorBrush)b.BorderBrush).Color).ToList();
        Check.That(colours.Distinct().Count()).IsEqualTo(2);
        Check.That(colours.Count(c => c == Tokens.ToggleOn)).IsEqualTo(1);
        Check.That(borders.Select(b => b.BorderThickness.Left).Distinct().Count()).IsEqualTo(1);
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

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellComboBoxSelectionTests"
```

Expected: `EveryOption_HasTheSameBorderThickness_RegardlessOfSelection` fails — the selected option reports 3, the rest 1.

- [ ] **Step 3: Make the border constant**

In `src/EyeCtrl.Controls/DwellComboBox.cs`, add the constant beside `SelectedColor`:

```csharp
    /// <summary>
    /// Option border thickness, identical selected or not.
    ///
    /// Constant by design: a Border's thickness lays out around its child, so varying it moved
    /// the pill and re-flowed the whole WrapPanel on every selection. Two rather than one so the
    /// unselected outline is visible against SurfaceKey without the selected state needing to
    /// grow to be seen.
    /// </summary>
    private const double OptionBorderThickness = 2;
```

In the constructor, replace the `optionBorder` initialiser's thickness:

```csharp
            var optionBorder = new Border
            {
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = Tokens.StandardCornerRadius,
                BorderBrush = new SolidColorBrush(Tokens.BorderSubtle),
                BorderThickness = new Thickness(OptionBorderThickness),
                Child = btn.Root
            };
```

Replace the body of `UpdateOptionSelectionStyles`'s loop:

```csharp
            _optionBorders[i].BorderBrush = new SolidColorBrush(isSelected ? SelectedColor : Tokens.BorderSubtle);
            // Thickness deliberately untouched — see OptionBorderThickness.
```

Update the class doc-comment's first `<para>`, which currently says the selected option gets "a thicker border thickness":

```csharp
/// The currently selected option is highlighted by a coloured border (green,
/// <c>SelectedColor</c>) on the outer wrapper of each button. The border's THICKNESS never
/// changes — only its brush — so selecting an option cannot resize it or re-flow the row.
/// A radio-button symbol (◉/◯) is also prepended to each option's text label.
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellComboBox"
```

Expected: the new tests plus `DwellComboBoxValidationTests` all pass.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/DwellComboBox.cs tests/EyeCtrl.Controls.Tests/DwellComboBoxSelectionTests.cs
git commit -m "fix(controls): selecting a combo option no longer resizes it or re-flows the row"
```

---

## Task 3: The slider shows where the value sits in its range

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellSlider.cs`
- Create: `tests/EyeCtrl.Controls.Tests/DwellSliderPlateTests.cs`
- Modify: `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs`

**Width budget — read before writing code.** The row must fit the narrowest content column the app supports. `SettingsContentFitTests` fixes that at a 800 DIP display: `800 × 0.9 = 720` window, minus a 240 sidebar and two 16 DIP margin pairs = **416 DIP**. The two stepper buttons carry `DwellButton`'s `(4,0,4,0)` margin; `_valueText` carries none (`DwellSlider.cs:83-92`). Today's row is therefore `108 + 140 + 108 = 356`. The plate replacing that text block **does** take a `(4,0,4,0)` margin in the code below, so it contributes 188: `108 + 188 + 108 = 404`, with 12 DIP of slack against 416. Verify by running the guard in Step 5 rather than trusting the arithmetic — margins are the easy part to get wrong, and an earlier revision of this very paragraph got them wrong. Do not widen it to the 300 the mockup shows — that mockup was drawn against a 1728 DIP window.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.Controls.Tests;

using System.Windows.Controls;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// The value plate must say what the bare number never did: where this value sits between its
/// minimum and maximum. A gaze user dwelling "+" otherwise has no way to know they are at an end.
/// </summary>
public class DwellSliderPlateTests
{
    /// <summary>A slider over [min, max] reporting a fixed current value. Decimals: 1.</summary>
    private static DwellSlider Build(double min, double max, double step, double current) =>
        new(new FakeRegistrar(), "sl", "Smoothing", min, max, step, 1,
            () => current, _ => { }, DwellCategory.Control, 50, 800);

    [StaFact]
    public void Plate_ShowsTheValueAndBothEndsOfTheRange()
    {
        var slider = Build(0, 1, 0.1, current: 0.5);

        Check.That(slider.ValueText.Text).IsEqualTo("0.5");
        Check.That(slider.MinimumText.Text).IsEqualTo("0.0");
        Check.That(slider.MaximumText.Text).IsEqualTo("1.0");
    }

    [StaFact]
    public void Rail_FillFraction_TracksWhereTheValueSitsInTheRange()
    {
        var slider = Build(0, 1, 0.1, current: 0.5);

        Check.That(slider.RailFraction).IsEqualTo(0.5);
    }

    [StaFact]
    public void Rail_AtTheMinimum_IsEmpty_AndAtTheMaximum_IsFull()
    {
        var slider = new DwellSlider(
            new FakeRegistrar(), "sl", "Thickness", 1, 6, 0.5, 1,
            () => 1.0, v => { }, DwellCategory.Control, 50, 800);
        Check.That(slider.RailFraction).IsEqualTo(0.0);

        var atMax = new DwellSlider(
            new FakeRegistrar(), "sl", "Thickness", 1, 6, 0.5, 1,
            () => 6.0, v => { }, DwellCategory.Control, 50, 800);
        Check.That(atMax.RailFraction).IsEqualTo(1.0);
    }

    [StaFact]
    public void Rail_OnADegenerateRange_DoesNotDivideByZero()
    {
        // min == max is nonsense but must not produce NaN and paint an invisible rail.
        var slider = new DwellSlider(
            new FakeRegistrar(), "sl", "Fixed", 3, 3, 1, 0,
            () => 3.0, v => { }, DwellCategory.Control, 50, 800);

        Check.That(double.IsNaN(slider.RailFraction)).IsFalse();
        Check.That(slider.RailFraction).IsEqualTo(0.0);
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

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellSliderPlateTests"
```

Expected: **compile error** — `DwellSlider` exposes none of `ValueText`, `MinimumText`, `MaximumText`, `RailFraction`.

- [ ] **Step 3: Build the plate**

In `src/EyeCtrl.Controls/DwellSlider.cs`, add usings:

```csharp
using System.Windows.Shapes;
```

Add the constant and fields:

```csharp
    /// <summary>
    /// Plate width in DIPs. Sized so the whole row fits the narrowest content column the app
    /// supports: 108 + 188 + 108 = 404 against 416 (a 800 DIP display at the 0.9 window
    /// ratio, less a 240 sidebar and two 16 DIP margin pairs). Guarded by
    /// SettingsContentFitTests.TheSliderRow_FitsTheContentColumn_OnANarrowDisplay.
    /// </summary>
    private const double PlateWidth = 180;

    private readonly TextBlock _minText;
    private readonly TextBlock _maxText;
    private readonly Rectangle _railFill;
    private readonly ScaleTransform _railScale;
```

Expose what the tests read:

```csharp
    /// <summary>The current value, rendered at display size in the plate.</summary>
    public TextBlock ValueText => _valueText;

    /// <summary>The minimum end-label under the rail.</summary>
    public TextBlock MinimumText => _minText;

    /// <summary>The maximum end-label under the rail.</summary>
    public TextBlock MaximumText => _maxText;

    /// <summary>How far along its range the value sits, in [0, 1]. Zero on a degenerate range.</summary>
    public double RailFraction => _railScale.ScaleX;
```

Replace the `_valueText` initialiser and the `_row` assembly. The value moves to display size, gains a rail, and sits on its own plate:

```csharp
        _valueText = new TextBlock
        {
            Text = FormatVal(get()),
            Foreground = Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeDisplay,
            FontWeight = Tokens.FontWeightStrong,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _minText = new TextBlock
        {
            Text = Format(decimals, min),
            Foreground = Tokens.BrushTextDim,
            FontSize = Tokens.FontSizeSubtitle,
            VerticalAlignment = VerticalAlignment.Center
        };

        _maxText = new TextBlock
        {
            Text = Format(decimals, max),
            Foreground = Tokens.BrushTextDim,
            FontSize = Tokens.FontSizeSubtitle,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Same stretch-and-scale trick as DwellButton's progress bar: a Width would need
        // ActualWidth, which is 0 until layout, and SettingsAutoPagination measures rows that
        // are never laid out.
        _railScale = new ScaleTransform(0, 1);
        _railFill = new Rectangle
        {
            Height = 5,
            Fill = Tokens.BrushAccent,
            RadiusX = 2.5,
            RadiusY = 2.5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransform = _railScale,
            RenderTransformOrigin = new Point(0, 0.5)
        };

        var railTrack = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = Tokens.BrushSurfaceDark,
            Child = _railFill
        };

        var railRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        railRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        railRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        railRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _minText.Margin = new Thickness(0, 0, 10, 0);
        _maxText.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_minText, 0);
        Grid.SetColumn(railTrack, 1);
        Grid.SetColumn(_maxText, 2);
        railRow.Children.Add(_minText);
        railRow.Children.Add(railTrack);
        railRow.Children.Add(_maxText);

        var plateContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        plateContent.Children.Add(_valueText);
        plateContent.Children.Add(railRow);

        var plate = new Border
        {
            Width = PlateWidth,
            Height = 100,
            Margin = new Thickness(4, 0, 4, 0),
            CornerRadius = Tokens.StandardCornerRadius,
            Background = Tokens.BrushSurfaceMedium,
            Padding = new Thickness(14, 0, 14, 0),
            Child = plateContent
        };
```

Then assemble the row with the plate in place of the old text block:

```csharp
        _row.Children.Add(_minus.Root);
        _row.Children.Add(plate);
        _row.Children.Add(_plus.Root);
```

Set the initial fraction at the end of the constructor, and keep it in step on every adjustment. Add the helper and call it from both places:

```csharp
    /// <summary>
    /// Sets the rail to <paramref name="value"/>'s position in [min, max].
    ///
    /// Guards min == max rather than letting it produce NaN: a NaN ScaleX silently paints
    /// nothing, which looks exactly like a value pinned at the minimum.
    /// </summary>
    private void UpdateRail(double value)
    {
        var span = _max - _min;
        _railScale.ScaleX = span <= 0
            ? 0
            : Math.Clamp((value - _min) / span, 0, 1);
    }
```

At the end of the constructor:

```csharp
        UpdateRail(get());
```

And in `Adjust`, after `_valueText.Text = FormatVal(v);`:

```csharp
        UpdateRail(v);
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellSlider"
```

Expected: the new tests plus `DwellSliderFormatTests` all pass.

- [ ] **Step 5: Guard the row against the narrow column**

Add to `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs`, inside the existing class so it reuses `Xml()`, `SidebarWidth`, `Margin` and `WindowWidthFor`:

```csharp
    /// <summary>
    /// The slider row is a composite, and composites are deliberately NOT capped by
    /// ConstrainToContentWidth — DwellSlider owns a horizontal StackPanel, which clips rather
    /// than wraps. So its natural width has to fit the narrowest column outright. The plate that
    /// widened this row from 356 to 404 DIP is the reason this guard exists.
    /// </summary>
    [StaFact]
    public void TheSliderRow_FitsTheContentColumn_OnANarrowDisplay()
    {
        var root = Xml();
        var contentArea = Margin(root, "ContentArea");
        var contentPanel = Margin(root, "ContentPanel");
        var available = WindowWidthFor(NarrowDisplayWidthDip)
            - SidebarWidth(root)
            - contentArea.Left - contentArea.Right
            - contentPanel.Left - contentPanel.Right;

        var scratch = new StackPanel();
        TextElement.SetFontFamily(scratch, Tokens.FontStandard);
        var slider = new DwellSlider(
            new NullRegistrar(), "sl", "Smoothing Factor", 0, 1, 0.1, 1,
            () => 0.5, _ => { }, DwellCategory.Control, 50, 800);
        slider.Attach(scratch);
        scratch.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The slider row wants {0:F2} DIP against a {1:F2} DIP content column on a {2:F0} "
                + "DIP display. It is a composite, so nothing caps it and its StackPanel clips "
                + "rather than wraps — the plus button would be drawn off the panel. Narrow "
                + "DwellSlider's PlateWidth; do not narrow the sidebar, which "
                + "SettingsTabLabelFitTests needs.",
                scratch.DesiredSize.Width, available, NarrowDisplayWidthDip))
            .That(scratch.DesiredSize.Width).IsLessOrEqualThan(available);
    }
```

- [ ] **Step 6: Run the App suite**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

Expected: green, including the new guard and every `SettingsAutoPaginationTests` case. The row's **height** is unchanged at 100, so measured row heights must not move; if `SettingsAutoPaginationTests` shifts, the plate is taller than its `Height=100` — stop and report rather than adjusting expectations.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Controls/DwellSlider.cs tests/EyeCtrl.Controls.Tests/DwellSliderPlateTests.cs tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs
git commit -m "feat(controls): slider shows its value at display size over a range rail"
```

---

## Task 4: Colour swatches name themselves on screen

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellColorPicker.cs:139-146`
- Create: `tests/EyeCtrl.Controls.Tests/DwellColorPickerLabelTests.cs`

The swatch keeps `SwatchWidth = 120` and `SwatchHeight = 100` so `SwatchTargetGeometryTests` stays green untouched; the name is drawn inside that box, under the colour.

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// Swatch names used to live only in Root.ToolTip. A tooltip needs hover; this application's
/// user has no hover — gaze is their only input channel — so the four presets were four
/// unlabelled rectangles. The name must be drawn.
/// </summary>
public class DwellColorPickerLabelTests
{
    private static DwellColorPicker Build()
    {
        var hex = "#A5F3FC";
        return new DwellColorPicker(
            new FakeRegistrar(), "cp", "Circle Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800);
    }

    [StaFact]
    public void EverySwatch_DrawsItsName()
    {
        var picker = Build();
        var expected = DwellColorPicker.PresetsFor(Tokens.Current).Select(p => p.Name).ToList();

        var drawn = picker.Children
            .Select(c => ((DwellButton)c).Content)
            .OfType<Panel>()
            .Select(p => p.Children.OfType<TextBlock>().Single().Text)
            .ToList();

        Check.That(drawn).IsEqualTo(expected);
    }

    [StaFact]
    public void EverySwatch_KeepsTheStandardTargetGeometry()
    {
        var picker = Build();

        foreach (var swatch in picker.Children)
        {
            Check.That(swatch.Root.Width).IsEqualTo(DwellColorPicker.SwatchWidth);
            Check.That(swatch.Root.Height).IsEqualTo(DwellColorPicker.SwatchHeight);
        }
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

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellColorPickerLabelTests"
```

Expected: `EverySwatch_DrawsItsName` fails — `Content` is `null`, so the `OfType<Panel>()` projection is empty.

- [ ] **Step 3: Draw the name**

In `src/EyeCtrl.Controls/DwellColorPicker.cs`, replace the block from `btn.Root.Background = ...` through `btn.Content = null;` (`:149-150` — plan 1 changed that last line from `Root.Child` to `Content`) with:

```csharp
            // The button's own fill becomes the card behind the colour chip, so the name has a
            // surface to be legible on. The chip carries the preset colour instead.
            btn.Root.Background = Tokens.BrushSurfaceMedium;
            btn.Root.BorderThickness = new Thickness(SwatchBorderThickness);
            btn.Root.BorderBrush = hex.Equals(get(), StringComparison.OrdinalIgnoreCase)
                ? Tokens.BrushTextPrimary : Brushes.Transparent;
            btn.Root.ToolTip = name;

            var chip = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(Tokens.CornerRadiusSmall)
            };

            var caption = new TextBlock
            {
                Text = name,
                Foreground = Tokens.BrushTextSecondary,
                FontSize = Tokens.FontSizeSubtitle,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var swatchContent = new DockPanel { LastChildFill = true, Margin = new Thickness(6) };
            DockPanel.SetDock(caption, Dock.Bottom);
            swatchContent.Children.Add(caption);
            swatchContent.Children.Add(chip);

            // Content, not Root.Child: Root's child is DwellButton's own layout, and overwriting
            // it would remove the dwell progress bar with it.
            btn.Content = swatchContent;
```

Add the thickness constant beside the other geometry consts, so `RefreshSelection` and the constructor agree:

```csharp
    /// <summary>
    /// Selection-border thickness, constant in both states so choosing a swatch cannot resize it
    /// and re-flow the WrapPanel — the same rule as DwellComboBox's OptionBorderThickness.
    /// </summary>
    public const double SwatchBorderThickness = 3;
```

`RefreshSelection` already only swaps `BorderBrush`, so it needs no change — confirm that by reading it rather than assuming.

Add usings if absent:

```csharp
using System.Windows.Controls;
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellColorPicker"
```

Expected: the new tests, `DwellColorPickerProgressTests` (plan 1) and `ColorPickerPresetTests` all pass.

- [ ] **Step 5: Check the geometry guard is still honest**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~SwatchTargetGeometryTests"
```

Expected: green. `SwatchWidth`/`SwatchHeight`/the margins are untouched, so it should be — if it is not, the swatch grew and the change is wrong.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Controls/DwellColorPicker.cs tests/EyeCtrl.Controls.Tests/DwellColorPickerLabelTests.cs
git commit -m "fix(controls): draw swatch names — a tooltip is unreachable without hover"
```

---

## Task 5: The toggle becomes a label plus a switch

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellToggle.cs` (full rewrite)
- Modify: `tests/EyeCtrl.Controls.Tests/DwellToggleVisualTests.cs`
- Modify: `tests/EyeCtrl.Controls.Tests/DwellProgressContrastTests.cs`

The structural change, sequenced last so everything before it is already green.

**Why the target moves to the switch.** `DwellControl.UpdatePosition` registers the hit circle at `Root`'s **centre**. Keeping the whole row as one `DwellControl` and adding a switch on its right would put a 100 DIP circle in the middle of a ~450 DIP row — the user would have to dwell the gap between the label and the switch, and looking at the switch itself would miss by ~200 DIP. The row must therefore stop being the target. `DwellSlider` and `DwellComboBox` already solve this the same way: a plain label plus separately-registered child targets.

**Cheaper alternative, recorded and rejected.** The 2.91:1 could be fixed in one line by passing a dark label brush on the ON fill (`#1A1A2E` on `#4A8F5C` measures 4.37:1), keeping the single-`DwellControl` row. That fixes the contrast and nothing else: no switch, the affordance stays a 450 DIP visual with a 100 DIP target in its middle, and state stays carried by a full-bleed fill. It is the fallback if this task turns out to be more disruptive than the plan expects — take it deliberately and say so, do not drift into it.

- [ ] **Step 1: Rewrite the tests**

Replace `tests/EyeCtrl.Controls.Tests/DwellToggleVisualTests.cs` in full:

```csharp
namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// The toggle is a composite: a plain label the user reads, and a switch that is the dwell
/// target. The row itself is no longer a target — a 100 DIP hit circle at the centre of a
/// 450 DIP row would sit in the gap between the two, and looking at the switch would miss it.
/// </summary>
public class DwellToggleVisualTests
{
    /// <summary>
    /// A toggle over a captured bool. The backing field lives in the closure, so
    /// <c>Toggle()</c> really does flip the value the control reads back — a test that
    /// passed a throwaway setter would assert against a state that never changed.
    /// </summary>
    private static DwellToggle Build(bool initial)
    {
        var state = initial;
        return new DwellToggle(
            new FakeDwellRegistrar(), "id", "Start on Windows Boot",
            () => state, v => state = v, DwellCategory.Control, 40, 800);
    }

    [StaFact]
    public void TheLabelIsPlainText_AndTheSwitchIsTheOnlyTarget()
    {
        var toggle = Build(initial: false);

        Check.That(toggle.Label.Text).IsEqualTo("Start on Windows Boot");
        Check.That(toggle.Children.Count()).IsEqualTo(1);
        Check.That(toggle.Children.Single()).IsSameReferenceAs(toggle.Switch);
    }

    [StaFact]
    public void StateIsReadableWithoutColour()
    {
        var toggle = Build(initial: false);
        Check.That(toggle.StateText.Text).IsEqualTo("Off");

        toggle.Switch.UpdateVisualForTest(0);
        toggle.Toggle();

        Check.That(toggle.StateText.Text).IsEqualTo("On");
    }

    [StaFact]
    public void TheKnobMovesSide_SoPositionCarriesStateToo()
    {
        var toggle = Build(initial: false);
        Check.That(toggle.Knob.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Left);

        toggle.Toggle();

        Check.That(toggle.Knob.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Right);
    }

    [StaFact]
    public void TheTrackCarriesTheStateColour()
    {
        var toggle = Build(initial: true);

        Check.That(((SolidColorBrush)toggle.Track.Background).Color).IsEqualTo(Tokens.ToggleOn);

        toggle.Toggle();

        Check.That(((SolidColorBrush)toggle.Track.Background).Color).IsEqualTo(Tokens.ToggleOff);
    }

    [StaFact]
    public void TheSwitchIsNeverDrawnSmallerThanItsHitCircle()
    {
        var toggle = Build(initial: false);

        Check.That(toggle.Switch.Root.Height).IsGreaterOrEqualThan(100.0);
        Check.That(toggle.Switch.Root.Width).IsGreaterOrEqualThan(100.0);
    }

    [StaFact]
    public void DwellSweepsTheSwitchesProgressBar()
    {
        var toggle = Build(initial: false);

        toggle.Switch.UpdateVisualForTest(0.75);

        Check.That(((ScaleTransform)toggle.Switch.ProgressBar.RenderTransform).ScaleX)
            .IsEqualTo(0.75);
    }

    private class FakeDwellRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
```

In `tests/EyeCtrl.Controls.Tests/DwellProgressContrastTests.cs`, the toggle arm reads `toggle.Root.Background`, which no longer exists. Replace `DwellToggle_LabelContrast_IsTheSameAtFullDwellAsAtRest` with:

```csharp
    /// <summary>
    /// The label now sits on the row surface, not on the state fill — which is what lifted it
    /// from 2.91:1 (#D6DEF5 on the #4A8F5C ON fill, under the 3:1 large-text bar) to 10.3:1.
    /// Green is left carrying only the switch track, a non-text component needing 3:1.
    /// </summary>
    [StaFact]
    public void DwellToggle_PutsItsLabelOnTheRowSurface_NotOnTheStateFill()
    {
        var state = true;
        var toggle = new DwellToggle(
            new FakeRegistrar(), "id", "Label", () => state, v => state = v,
            DwellCategory.Control, 50, 800);

        var labelContrast = Contrast(
            Tokens.TextPrimary, ((SolidColorBrush)toggle.RowSurface.Background).Color);
        var trackContrast = Contrast(
            ((SolidColorBrush)toggle.Track.Background).Color,
            ((SolidColorBrush)toggle.RowSurface.Background).Color);

        Check.WithCustomMessage($"label sits at {labelContrast:F2}:1")
            .That(labelContrast).IsStrictlyGreaterThan(4.5);
        Check.WithCustomMessage($"ON track sits at {trackContrast:F2}:1 against the row")
            .That(trackContrast).IsStrictlyGreaterThan(3.0);
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~DwellToggle"
```

Expected: **compile errors** — `DwellToggle` has no `Switch`, `Track`, `Knob`, `StateText`, `RowSurface`, `Children` or public `Toggle`.

- [ ] **Step 3: Rewrite `DwellToggle`**

Replace `src/EyeCtrl.Controls/DwellToggle.cs` in full:

```csharp
namespace EyeCtrl.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// A dwell-enabled boolean setting: a plain label the user reads, and a switch that is the
/// dwell target.
/// </summary>
/// <remarks>
/// <para>
/// A composite rather than a single <see cref="DwellControl"/> because the hit circle is
/// registered at its Root's CENTRE. A one-root row with a switch on its right would put the
/// circle in the gap between label and switch: looking at the switch — the obvious thing to
/// look at — would miss the target entirely. <see cref="DwellSlider"/> and
/// <see cref="DwellComboBox"/> already split label from target for the same reason.
/// </para>
/// <para>
/// State is carried three ways, none of them colour alone: the knob's side, the word
/// "On"/"Off", and the track's fill. The label sits on the row surface rather than on the
/// state fill, which is what keeps it at 10.3:1 — on the green ON fill it measured 2.91:1,
/// under the 3:1 large-text bar.
/// </para>
/// </remarks>
public class DwellToggle : IDwellComposite
{
    /// <summary>Switch track width in DIPs.</summary>
    private const double TrackWidth = 84;

    /// <summary>Switch track height in DIPs.</summary>
    private const double TrackHeight = 48;

    /// <summary>Knob diameter in DIPs.</summary>
    private const double KnobSize = 34;

    /// <summary>
    /// The switch button's box. At or above the 100 DIP hit circle it registers, per
    /// DwellButton's WYSIWYG floor — the track is smaller, but the target the user aims at is
    /// this whole box.
    /// </summary>
    private const double SwitchWidth = 140;

    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly TextBlock _label;
    private readonly TextBlock _stateText;
    private readonly Border _rowSurface;
    private readonly Border _track;
    private readonly Border _knob;
    private readonly DwellButton _switch;

    // Mutated in place on state change; never reallocated.
    private readonly SolidColorBrush _trackBrush = new();
    private readonly SolidColorBrush _knobBrush = new();

    /// <summary>The descriptive label. Plain text — not a dwell target.</summary>
    public TextBlock Label => _label;

    /// <summary>The "On"/"Off" word beside the switch: state without relying on colour.</summary>
    public TextBlock StateText => _stateText;

    /// <summary>The row's background panel — the surface the label's contrast is measured against.</summary>
    public Border RowSurface => _rowSurface;

    /// <summary>The switch track, which carries the state colour.</summary>
    public Border Track => _track;

    /// <summary>The knob. Its side is the non-colour state cue.</summary>
    public Border Knob => _knob;

    /// <summary>The dwell target. Everything else in the row is decoration.</summary>
    public DwellButton Switch => _switch;

    /// <inheritdoc/>
    public IEnumerable<DwellControl> Children => [_switch];

    /// <summary>
    /// Creates a <see cref="DwellToggle"/>.
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="id">Unique target identifier within <paramref name="category"/>.</param>
    /// <param name="label">Descriptive text shown to the left of the switch.</param>
    /// <param name="get">Returns the current boolean value of the setting.</param>
    /// <param name="set">Applies a new boolean value when the switch activates.</param>
    /// <param name="category">Dwell category the switch is registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    public DwellToggle(
        IDwellRegistrar registrar,
        string id,
        string label,
        Func<bool> get,
        Action<bool> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        _get = get;
        _set = set;

        _label = new TextBlock
        {
            Text = label,
            Foreground = Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightMedium,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        System.Windows.Automation.AutomationProperties.SetAutomationId(_label, $"Settings_Toggle_{id}");

        _stateText = new TextBlock
        {
            Foreground = Tokens.BrushTextSecondary,
            FontSize = Tokens.FontSizeSubtitle,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Width = 40,
            Margin = new Thickness(16, 0, 12, 0)
        };

        _knob = new Border
        {
            Width = KnobSize,
            Height = KnobSize,
            CornerRadius = new CornerRadius(KnobSize / 2),
            Background = _knobBrush,
            VerticalAlignment = VerticalAlignment.Center,
            // A dark ring so a light knob stays distinct from the light-ish ON track: the knob
            // on green alone measures 2.91:1, the ring against green 4.37:1.
            BorderThickness = new Thickness(2),
            BorderBrush = Tokens.BrushSurfaceDark
        };

        _track = new Border
        {
            Width = TrackWidth,
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = _trackBrush,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _knob
        };

        var switchContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _stateText, _track }
        };

        _switch = new DwellButton(
            registrar, id, "", Toggle, Tokens.SurfaceLight, category, radiusPhysical, dwellMs,
            width: SwitchWidth, height: 100)
        {
            Content = switchContent
        };
        _switch.Root.Margin = new Thickness(0);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _label.Margin = new Thickness(20, 0, 20, 0);
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(_switch.Root, 1);
        row.Children.Add(_label);
        row.Children.Add(_switch.Root);

        _rowSurface = new Border
        {
            // No explicit Width: the row hugs its label plus the switch. A fixed width would
            // overflow the Supervisor's 560 DIP setup window, which hosts two of these.
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 360,
            Height = 100,
            Margin = new Thickness(0, 0, 0, 24),
            CornerRadius = Tokens.StandardCornerRadius,
            Background = Tokens.BrushSurfaceMedium,
            BorderThickness = new Thickness(Tokens.BorderThicknessStandard),
            BorderBrush = Tokens.BrushBorderSubtle,
            Cursor = Cursors.Hand,
            Child = row
        };

        RefreshVisual();
    }

    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        parent.Children.Add(_rowSurface);
        _switch.RegisterDwell();
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
        parent.Children.Remove(_rowSurface);
        _switch.UnregisterDwell();
    }

    /// <summary>Flips the value and repaints. Public so a mouse fallback and tests can drive it.</summary>
    public void Toggle()
    {
        _set(!_get());
        RefreshVisual();
    }

    private void RefreshVisual()
    {
        var on = _get();
        _stateText.Text = on ? "On" : "Off";
        _stateText.Foreground = on ? Tokens.BrushTextPrimary : Tokens.BrushTextSecondary;
        _trackBrush.Color = on ? Tokens.ToggleOn : Tokens.ToggleOff;
        _knobBrush.Color = on ? Tokens.TextPrimary : Tokens.TextSecondary;
        _knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
```

- [ ] **Step 4: Run the Controls suite**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

Expected: green. If `DwellToggle` no longer compiles at a call site, it is one of the six in the impact table — go back and read that table rather than casting.

- [ ] **Step 5: Run everything else this reaches**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj
```

Expected: both green. Watch `SettingsAutoPaginationTests` — the toggle row is still 100 tall with a 24 DIP bottom margin, exactly as before, so measured heights must not move.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Controls/DwellToggle.cs tests/EyeCtrl.Controls.Tests/
git commit -m "feat(controls): toggle becomes a label plus a switch, lifting its label to 10.3:1"
```

---

## Task 6: Whole-suite verification

- [ ] **Step 1: Full build**

```bash
dotnet build EyeCtrl.slnx
```

Expected: `Build succeeded`, with no new warnings from the four rewritten files.

- [ ] **Step 2: Every suite, one at a time**

```bash
dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj
```

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj
```

Each at or above its Task 1 baseline, zero failures. Read `Fehlgeschlagen: 0` — do not grep for `Failed`.

- [ ] **Step 3: Confirm nothing measured moved**

The four changes were chosen to leave every measured dimension alone: control heights are 100, the toggle row keeps its 24 DIP bottom margin, swatch geometry is untouched, and the slider row's new width has its own guard. If `SettingsAutoPaginationTests`, `SettingsContentFitTests` or `SwatchTargetGeometryTests` moved, something grew that should not have — **stop and report rather than updating an expectation**.

---

## Task 7: Live verification — **requires the user's explicit go-ahead**

- [ ] **Step 1: Ask before doing anything in this task**

The user drives this machine solely by eye-tracking; launching EyeCtrl seizes their only input channel and can cut them off from their caregiver. **Stop and ask**, saying how long the run will take. Do not proceed on assumed consent, and do not run `Category=UI` tests without the same permission.

- [ ] **Step 2: On a yes — check the four controls, then close promptly**

Settings → Cursor:
1. **Filter Mode** — dwelling an option changes only its border colour; no pill moves, nothing re-wraps.
2. **Smoothing Factor** — the value reads at display size with `0` and `1` under a rail filled halfway; dwelling `+` moves both the number and the rail.
3. **Circle Colour** — each swatch shows its name under the colour; the selected one has a white border; dwelling one sweeps its progress bar.

Settings → General:
4. **Any toggle** — the label sits on the row and stays crisp; the switch is on the right; dwelling the *switch* activates it, and dwelling the *label* does not. The knob crosses sides and the word changes with it.

- [ ] **Step 3: On a yes — the Supervisor setup window**

It hosts two `DwellToggle`s in a 560 DIP-wide window, and it is the consumer nobody looks at. Confirm both rows fit without clipping and both switches work. If no supervisor is installed, say so and record the check as not performed — do not install one to test it.

- [ ] **Step 4: Report**

Report exactly what happened, including anything that did not work. If the run was declined, say the plan is complete but unverified live.

---

## Self-review

**Design coverage.** Direction B's control anatomy is four things: the switch (Task 5), the non-reflowing option border (Task 2), the value plate with a rail (Task 3), and labelled swatches (Task 4). All four are here. The stroke icons and the two-column layout are named in "Out of scope" and belong to the layout plan.

**Placeholder scan.** Every step that changes code carries the code. The one instruction to read rather than write — `RefreshSelection` in Task 4 Step 3 — says explicitly what to check and why.

**Type consistency.** `DwellButton.Content`/`ProgressBar` and `DwellControl.UpdateVisualForTest` come from plan 1 and are used in Tasks 4 and 5. `DwellColorPicker.SwatchBorderThickness` is defined and used in Task 4. `DwellToggle`'s `Label`/`StateText`/`RowSurface`/`Track`/`Knob`/`Switch`/`Children`/`Toggle` are defined in Task 5 Step 3 and used in Task 5 Step 1's tests. `DwellSlider`'s `ValueText`/`MinimumText`/`MaximumText`/`RailFraction` are defined in Task 3 Step 3 and used in Step 1.

**Ordering dependency.** Task 5 must run last: it changes `DwellToggle`'s type and rewrites a test file Task 1 baselines. Task 3's fit guard depends on Task 3's `PlateWidth`. Do not reorder.

**The risk that remains.** `DwellToggle` is CRITICAL upstream and the type change is real. The mitigation is that every consumer is enumerated in the impact table above, five of six need no change, and the sixth is a test file this plan rewrites. The Supervisor's two call sites use `var` and `Attach(Panel)` and register against a `NoopDwellRegistrar` with no per-frame loop — verified by grepping the whole project, not assumed. Task 7 Step 3 looks at that window by eye because nothing headless covers its rendering.
