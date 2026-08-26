# Settings Row Descriptions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Give every describable settings row on the ten auto-paginated tabs a short, truthful description, and gate against bare rows — plus fix the two dwell slider minima that can crash the app.

**Architecture:** Pure content pass over the declarative page sources (`ISettingsPageSource` → `SettingsRowSpec.Description`), which the existing renderer already draws. Two new tests: a range-vs-guard pin on the dwell sliders, and a completeness gate over all covered sources.

**Tech Stack:** .NET 10 WPF (`EyeCtrl.App`), xUnit v3 + NFluent 3.1.0 (both global usings in test projects — no using lines needed for them).

**Spec:** `docs/superpowers/specs/2026-08-26-settings-row-descriptions-design.md` (rev 2, approved). All copy below is verbatim from the spec's inventory — do not reword while implementing.

## Global Constraints

- Work in the worktree `C:\Users\andre\source\repos\eyectrl\.worktrees\settings-descriptions` (branch `feat/settings-descriptions`). All paths below are relative to it.
- Copy is British prose ("Colour", "centred"); labels stay American. Copy is taken verbatim from the spec tables — including punctuation.
- No delegate, range, step, or ordering changes anywhere except the two dwell slider minima in Task 1 (`d_fix` 50→100, `d_cooldown` 0→100).
- `CursorPageSource.cs` and `QuickActionsPageSource.cs` are NOT modified.
- Test assertions use NFluent (`Check.That(...)`). Never run two test suites at once on this box; the command below is the one to use.
- Per-task test command: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` (baseline 608/608 green, ~26 s). Do not run single test classes except where a step says to (the ExternalDwellServiceTests-alone XAML pitfall).
- Commit style: conventional commits, as in each task. Do not push.
- AGENTS.md ritual: before editing each page source's `Build()`, GitNexus `impact({target:"<SourceName>.Build", direction:"upstream", repo:"eyectrl"})` — expected LOW (UI leaves consumed only by `SettingsWindow`). One `detect_changes` covers the branch at the end (Task 5).

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs` | 9 descriptions + 2 slider minima | Modify |
| `src/EyeCtrl.App/Views/Settings/Sources/SelectionPageSource.cs` | 7 descriptions | Modify |
| `src/EyeCtrl.App/Views/Settings/Sources/LayoutPageSource.cs` | 1 description | Modify |
| `src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs` | 10 descriptions via `ActionDescription` switch | Modify |
| `src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs` | 1 description | Modify |
| `src/EyeCtrl.App/Views/Settings/Sources/KeyboardPageSource.cs` | 6 descriptions | Modify |
| `src/EyeCtrl.App/Views/Settings/Sources/ToolbarPageSource.cs` | 3 descriptions | Modify |
| `src/EyeCtrl.App/Views/Settings/Sources/CalibrationPageSource.cs` | 4 descriptions | Modify |
| `src/EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs` | 4 descriptions | Modify |
| `tests/EyeCtrl.App.Tests/Settings/Pages/DwellSelectionKeyboardToolbarPageSourceTests.cs` | range-vs-guard test | Modify |
| `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs` | completeness gate | Create |

---

### Task 1: Dwell slider-min fix with range-vs-guard test

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs:16` (`d_fix` min) and `:34` (`d_cooldown` min)
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/DwellSelectionKeyboardToolbarPageSourceTests.cs`

**Interfaces:**
- Consumes: `DwellPageSource(SettingsPageContext)`, `TestSettingsPageContext.Build()`, `SliderSpec` positional record (`Min`, `Max` properties).
- Produces: `Dwell_SliderSpans_StayInsideTheValidatedRanges` fact; slider spans `d_fix` 100–500, `d_cooldown` 100–1000 that Tasks 2 and 5 see.

- [x] **Step 1: Run impact, then write the failing test**

Run GitNexus `impact({target:"DwellPageSource.Build", direction:"upstream", repo:"eyectrl"})` — confirm LOW, then append this fact to `DwellSelectionKeyboardToolbarPageSourceTests`:

```csharp
// Range-vs-guard: a dwell slider offering a value the Core setter rejects crashes on the UI
// thread — DwellSlider.Adjust clamps to the spec's Min, then calls Set unguarded, and
// App.xaml.cs deliberately lets dispatcher exceptions terminate the process. Guard bounds are
// restated from DwellSettings.OnXxxChanging so moving either side fails loudly here.
[Fact]
public void Dwell_SliderSpans_StayInsideTheValidatedRanges()
{
    var sliders = new DwellPageSource(TestSettingsPageContext.Build()).Build()
        .SelectMany(p => p.Rows).OfType<SliderSpec>()
        .ToDictionary(s => s.Id);

    var guards = new Dictionary<string, (double Min, double Max)>
    {
        ["d_fix"] = (100, 1000),
        ["d_act"] = (200, 2000),
        ["d_menu"] = (200, 2000),
        ["d_ManualSelection"] = (200, 2000),
        ["d_tool"] = (200, 2000),
        ["d_key"] = (100, 1500),
        ["d_pred"] = (100, 1500),
        ["d_rad"] = (10, 200),
        ["d_cooldown"] = (100, 5000),
    };

    Check.That(sliders).HasSize(guards.Count);
    foreach (var (id, guard) in guards)
    {
            Check.That(sliders[id].Min).IsGreaterOrEqualThan(guard.Min);
            Check.That(sliders[id].Max).IsLessOrEqualThan(guard.Max);
    }
}
```

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellSelectionKeyboardToolbarPageSourceTests"`
Expected: FAIL — `d_fix` Min is 50 (guard floor 100) and `d_cooldown` Min is 0 (guard floor 100). This class is safe to run in isolation (it builds no overlay XAML).

- [x] **Step 3: Fix the two minima**

In `DwellPageSource.cs`, change the `d_fix` construction (line 16) from `50, 500, 25, 0` to:

```csharp
new SliderSpec("d_fix", "Fixation Detection (ms)", 100, 500, 25, 0,
```

and the `d_cooldown` construction (line 34) from `0, 1000, 50, 0` to:

```csharp
new SliderSpec("d_cooldown", "Dwell Cooldown (ms)", 100, 1000, 50, 0,
```

Both minimums land on-step (step 25 and step 50 respectively). Change nothing else — descriptions come in Task 2.

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS, 609 total (608 + the new fact).

- [x] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/DwellSelectionKeyboardToolbarPageSourceTests.cs
git commit -m "fix(settings): keep the dwell sliders inside the validated range

Two down-steps on Fixation Detection (slider min 50) or Dwell Cooldown
(min 0) reached values the Core setters reject; DwellSlider.Adjust calls
the setter unguarded and the app deliberately crashes on dispatcher
exceptions, so those sliders could take EyeCtrl down. Minima raised to
the guard floor (100), pinned by a range-vs-guard test."
```

---

### Task 2: Dwell, Selection and Layout descriptions (17 rows)

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs` (all 9 sliders)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/SelectionPageSource.cs` (7 rows)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/LayoutPageSource.cs` (1 row)

**Interfaces:**
- Consumes: `SettingsRowSpec.Description { get; init; }` — attach as `{ Description = "..." }` after each construction's argument list, before the comma.
- Produces: described rows Tasks 5's gate consumes.

- [x] **Step 1: Run impact on the three `Build()` methods** (expected LOW for each).

- [x] **Step 2: Add the Dwell descriptions**

Each construction in `DwellPageSource.Build()` now reads (copy verbatim; delegates unchanged — only the initializer is new; `d_fix`/`d_cooldown` carry Task 1's minima):

```csharp
new SliderSpec("d_fix", "Fixation Detection (ms)", 100, 500, 25, 0,
    () => d.FixationDetectionMs, v => { d.FixationDetectionMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "How long your gaze must hold steady before a dwell can start." },
new SliderSpec("d_act", "Activation Button (ms)", 200, 1500, 50, 0,
    () => d.ActivationButtonDwellMs, v => { d.ActivationButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Dwell for the button that appears at your gaze." },
new SliderSpec("d_menu", "Action Menu (ms)", 200, 1500, 50, 0,
    () => d.ActionMenuDwellMs, v => { d.ActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "How long to rest on an action in the ring." },
new SliderSpec("d_ManualSelection", "ManualSelection Action Menu (ms)", 200, 1500, 50, 0,
    () => d.ManualSelectionActionMenuDwellMs, v => { d.ManualSelectionActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Dwell for the action ring inside manual selection." },
new SliderSpec("d_tool", "Toolbar / Settings (ms)", 200, 1500, 50, 0,
    () => d.ToolbarButtonDwellMs, v => { d.ToolbarButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "How long to rest on EyeCtrl's own buttons." },
new SliderSpec("d_key", "Keyboard Key (ms)", 100, 1000, 25, 0,
    () => d.KeyboardKeyDwellMs, v => { d.KeyboardKeyDwellMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "How long to rest on a key before it fires." },
new SliderSpec("d_pred", "Prediction (ms)", 100, 1000, 25, 0,
    () => d.KeyboardPredictionDwellMs, v => { d.KeyboardPredictionDwellMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Dwell for the word suggestions above the keyboard." },
new SliderSpec("d_rad", "Fixation Radius (px)", 20, 120, 5, 0,
    () => d.FixationRadiusPixels, v => { d.FixationRadiusPixels = v; _ctx.ApplyAndSave(); })
    { Description = "How far your gaze may drift within a fixation." },
new SliderSpec("d_cooldown", "Dwell Cooldown (ms)", 100, 1000, 50, 0,
    () => d.DwellCooldownMs, v => { d.DwellCooldownMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "How long a new dwell must wait after an activation." },
```

The `d_reset` ButtonSpec is untouched (buttons render no description).

- [x] **Step 3: Add the Selection descriptions**

In `SelectionPageSource.Build()`:

```csharp
new ComboBoxSpec("s_mode", "Selection Mode",
    new[] { "None", "Manual Selection", "Auto Selection" },
    new[] { "None", "ManualSelection", "AutoSelection" },
    () => s.SelectionMode.ToString(),
    v => { s.SelectionMode = Enum.Parse<SelectionMode>(v); _ctx.ApplyAndSave(); })
    { Description = "None clicks your gaze point; Manual zooms; Auto snaps." },
new SliderSpec("s_rad", "Circle Radius (px)", 50, 300, 10, 0,
    () => s.ManualSelection.CircleRadiusPixels, v => { s.ManualSelection.CircleRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Radius of the magnifier's zoom circle." },
new SliderSpec("s_mag", "Magnification Level", 1.5, 5, 0.5, 1,
    () => s.ManualSelection.MagnificationLevel, v => { s.ManualSelection.MagnificationLevel = v; _ctx.ApplyAndSave(); })
    { Description = "How strongly the zoom circle magnifies." },
new SliderSpec("s_grace", "Grace Period (ms)", 0, 2000, 50, 0,
    () => s.ManualSelection.ManualSelectionGracePeriodMs, v => { s.ManualSelection.ManualSelectionGracePeriodMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Dwell waits this long after manual selection opens." },
new ColorPickerSpec("s_highlight", "Highlight Color",
    () => s.AutoSelection.HighlightColor, v => { s.AutoSelection.HighlightColor = v; _ctx.ApplyAndSave(); },
    SettingsWindow.ControlBtnRadius)
    { Description = "Colour of the box around an auto-selected control." },
new SliderSpec("s_highlightThickness", "Highlight Thickness", 1, 10, 1, 0,
    () => s.AutoSelection.HighlightThickness, v => { s.AutoSelection.HighlightThickness = v; _ctx.ApplyAndSave(); })
    { Description = "Line weight of that auto-selection box." },
new SliderSpec("s_snapRadius", "Snap Search Radius (px)", 16, 96, 16, 0,
    () => s.AutoSelection.SnapSearchRadiusPixels, v => { s.AutoSelection.SnapSearchRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
    { Description = "How far from your gaze Auto Selection searches for a control." },
```

Keep the existing step-16 comment above `s_snapRadius` — it documents the stepping, not the copy.

- [x] **Step 4: Add the Layout description**

In `LayoutPageSource.Build()`:

```csharp
new ComboBoxSpec("lay_pos", "Position",
    new[] { "At Gaze", "At Center" }, null,
    () => s.ViewPosition == ViewPosition.AtGaze ? "At Gaze" : "At Center",
    v => { s.ViewPosition = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; _ctx.ApplyAndSave(); })
    { Description = "The action menu and magnifier: at your gaze, or centred." },
```

- [x] **Step 5: Build and run the tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS, 609 total (descriptions change no behavior; some lines wrap to two lines, which is harmless inside the 150 DIP row box).

- [x] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs src/EyeCtrl.App/Views/Settings/Sources/SelectionPageSource.cs src/EyeCtrl.App/Views/Settings/Sources/LayoutPageSource.cs
git commit -m "feat(settings): describe the dwell, selection and layout rows"
```

---

### Task 3: Action Menu and Models descriptions (11 rows)

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs`
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs:103`

**Interfaces:**
- Consumes: `ToggleSpec`, `ComboBoxSpec`, `Description` init; the existing `FriendlyName` switch.
- Produces: `ActionDescription(InteractionAction)` private static method (exhaustive switch, no null fallback — a new enum member must throw, so the completeness gate and this switch both force copy).

- [x] **Step 1: Run impact on both `Build()` methods** (expected LOW).

- [x] **Step 2: Add the ActionDescription switch and attach it**

Add beside `FriendlyName` in `ActionMenuPageSource`:

```csharp
// Copy verbatim from the spec inventory. Exhaustive with a throwing fallback, not a silent
// null: a new InteractionAction must fail loudly here and in the completeness test, never
// ship a bare row.
private static string ActionDescription(InteractionAction action) => action switch
{
    InteractionAction.LeftClick => "One left click at the point you chose.",
    InteractionAction.RightClick => "One right click at the point you chose.",
    InteractionAction.DoubleClick => "Two quick clicks at the point you chose.",
    InteractionAction.Drag => "Grabs at that point, then follows your gaze.",
    InteractionAction.ScrollUp => "Scrolls up from the point you chose.",
    InteractionAction.ScrollDown => "Scrolls down from the point you chose.",
    InteractionAction.ScrollLeft => "Scrolls left from the point you chose.",
    InteractionAction.ScrollRight => "Scrolls right from the point you chose.",
    InteractionAction.ScrollMode => "Continuous scrolling, steered by where you look.",
    InteractionAction.PinNote => "Opens a note to type at the point you chose.",
    _ => throw new InvalidOperationException($"{action} needs a settings description"),
};
```

Change the loop's `rows.Add` to:

```csharp
rows.Add(new ToggleSpec(
    $"am_{action}",
    FriendlyName(action),
    () => a.VisibleActions.Contains(captured),
    on =>
    {
        if (on && !a.VisibleActions.Contains(captured)) a.VisibleActions.Add(captured);
        else if (!on) a.VisibleActions.RemoveAll(x => x == captured);
        _ctx.ApplyAndSave();
    })
    { Description = ActionDescription(captured) });
```

- [x] **Step 3: Add the Models description**

In `ModelsPageSource.Build()`, the `md_active` combo gets:

```csharp
page1.Add(new ComboBoxSpec("md_active", "Active Model",
    display.Prepend("Off").ToArray(), ids.Prepend("Off").ToArray(),
    () => _ctx.Registry.ActiveModelId ?? "Off",
    v => { _ctx.ModelManagement.SetActiveModel(v); _ctx.ApplyAndSave(); })
    { Description = "Which model powers predictions; Off keeps dictionary words." });
```

- [x] **Step 4: Build and run the tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS, 609 total.

- [x] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/ActionMenuPageSource.cs src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs
git commit -m "feat(settings): describe the action menu and models rows"
```

---

### Task 4: Keyboard, Toolbar, Calibration and General descriptions (17 rows)

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/KeyboardPageSource.cs` (6 rows)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ToolbarPageSource.cs` (3 rows)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/CalibrationPageSource.cs` (4 rows)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs` (4 rows)

**Interfaces:**
- Consumes: `Description` init.
- Produces: described rows Task 5's gate consumes. After this task all 45 rows are described.

- [x] **Step 1: Run impact on the four `Build()` methods** (expected LOW).

- [x] **Step 2: Keyboard** — attach in `KeyboardPageSource.Build()`:

```csharp
new ComboBoxSpec("k_layout", "Active Layout",
    new[] { "QWERTY", "QWERTZ", "AZERTY" }, null,
    () => k.ActiveLayout, v => { k.ActiveLayout = v; _ctx.ApplyAndSave(); })
    { Description = "Key arrangement; QWERTZ suits German, AZERTY suits French." },
new SliderSpec("k_size", "Key Size Multiplier", 0.5, 2.0, 0.1, 1,
    () => k.KeySizeMultiplier, v => { k.KeySizeMultiplier = v; _ctx.ApplyAndSave(); })
    { Description = "Scales every key; bigger keys are easier to hit." },
new ToggleSpec("k_pred", "Word Prediction",
    () => k.PredictionEnabled, v => { k.PredictionEnabled = v; _ctx.ApplyAndSave(); })
    { Description = "Shows suggested words above the keyboard while you type." },
new ComboBoxSpec("k_lang", "Dictionary Language",
    new[] { "English", "French", "Spanish", "German", "Polish" },
    new[] { "en", "fr", "es", "de", "pl" },
    () => string.IsNullOrEmpty(k.LanguagePack) ? "en" : k.LanguagePack,
    v => { k.LanguagePack = v; _ctx.ApplyAndSave(); })
    { Description = "Which language the word suggestions draw from." },
new ToggleSpec("k_sound", "Click Sound",
    () => k.ClickSoundEnabled, v => { k.ClickSoundEnabled = v; _ctx.ApplyAndSave(); })
    { Description = "Plays a click each time a key activates." },
new ComboBoxSpec("k_edge", "Screen Edge",
    new[] { "Bottom", "Top" }, null,
    () => k.ScreenEdge, v => { k.ScreenEdge = v; _ctx.ApplyAndSave(); })
    { Description = "The screen edge the keyboard docks to." },
```

- [x] **Step 3: Toolbar**:

```csharp
new ToggleSpec("t_dock", "Dock to Screen Edge",
    () => t.IsDocked, v => { t.IsDocked = v; _ctx.ApplyAndSave(); })
    { Description = "Docks the toolbar to an edge, where it auto-hides." },
new ComboBoxSpec("t_edge", "Docked Edge",
    new[] { "Top", "Bottom", "Left", "Right" }, null,
    () => t.DockedEdge, v => { t.DockedEdge = v; _ctx.ApplyAndSave(); })
    { Description = "Which screen edge the toolbar attaches to." },
new SliderSpec("t_size", "Button Size", 32, 120, 4, 0,
    () => t.Size, v => { t.Size = v; _ctx.ApplyAndSave(); })
    { Description = "Size of each button; bigger ones are easier to aim at." },
```

(`t_size`'s 32–120 span stays as is — the 40–200 mismatch is recorded in the spec's Observations and deliberately not fixed here.)

- [x] **Step 4: Calibration**:

```csharp
new SliderSpec("cal_points", "Calibration Points", 3, 16, 1, 0,
    () => cal.PointCount, v => { cal.PointCount = (int)v; _ctx.ApplyAndSave(); })
    { Description = "How many points the calibration run visits." },
new SliderSpec("cal_samples", "Samples per Point", 5, 100, 5, 0,
    () => cal.SamplesPerPoint, v => { cal.SamplesPerPoint = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Gaze samples gathered at each calibration point." },
new SliderSpec("cal_hold", "Target Hold (ms)", 200, 3000, 100, 0,
    () => cal.TargetHoldMs, v => { cal.TargetHoldMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Each target stays on screen at least this long." },
new SliderSpec("cal_settle", "Settle Window (ms)", 0, 500, 25, 0,
    () => cal.SettleWindowMs, v => { cal.SettleWindowMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Time at a target before its samples start counting." },
```

- [x] **Step 5: General**:

```csharp
new ComboBoxSpec("g_theme", "Theme (restart to apply)",
    Core.Settings.ThemeSettings.ValidNames, null,
    () => s.Theme.Name, v => { s.Theme.Name = v; _ctx.ApplyAndSave(); })
    { Description = "All three are dark; Contrast is the boldest." },
new ToggleSpec("g_boot", "Start on Windows Boot",
    () => s.General.StartOnBoot, v => { s.General.StartOnBoot = v; _ctx.ApplyAndSave(); })
    { Description = "Opens EyeCtrl automatically when you sign in." },
new ToggleSpec("g_pause", "Start Paused",
    () => s.General.StartPaused, v => { s.General.StartPaused = v; _ctx.ApplyAndSave(); })
    { Description = "Starts with tracking paused until you switch it on." },
new ToggleSpec("g_track", "Show Track Status",
    () => s.TrackStatus.Enabled, v => { s.TrackStatus.Enabled = v; _ctx.ApplyAndSave(); })
    { Description = "A corner box showing live eye-tracker status." },
```

- [x] **Step 6: Build and run the tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS, 609 total.

- [x] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/KeyboardPageSource.cs src/EyeCtrl.App/Views/Settings/Sources/ToolbarPageSource.cs src/EyeCtrl.App/Views/Settings/Sources/CalibrationPageSource.cs src/EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs
git commit -m "feat(settings): describe the keyboard, toolbar, calibration and general rows"
```

---

### Task 5: Completeness gate and branch verification

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs`

**Interfaces:**
- Consumes: all ten covered page sources; `TestSettingsPageContext.Build` (registry must be passed for Models — it is null-forgiven); `ModelTestFakes.Create()` + `WithDownloadedBundled()`; `ISettingsPageSource.Build()` → `PageSpec.Rows`.
- Produces: the drift gate future settings development must keep green.

- [x] **Step 1: Write the test**

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Every describable row (slider / toggle / combo / colour picker) on every covered tab carries a
/// Description — a settings row that ships bare fails here. QuickActions is exempt BY EXPLICIT
/// OMISSION, named here: its pages are hand-chunked and height-gated (HandChunkedPageFitTests), so
/// its rows deliberately carry no descriptions. The exemption is NOT expressed via
/// PrefersAutoPagination — that property defaults to false in the interface, so testing it would
/// silently excuse any future source that merely forgets to opt in. Models is built with a
/// downloaded model because an empty registry builds no describable row at all (md_active is gated
/// on SelectableModels being non-empty) — the empty build would cover nothing.
/// </summary>
public class SettingsRowDescriptionCompletenessTests
{
    [Fact]
    public void EveryDescribableRow_OnEveryCoveredSource_HasADescription()
    {
        var ctx = TestSettingsPageContext.Build();
        // Mirrors SettingsWindow._tabDefs order (QuickActions omitted, see the class doc).
        // A new page source must be added here consciously — the same forcing function as
        // EyeCtrlSettingsEnumerationTests.
        ISettingsPageSource[] sources =
        [
            new CursorPageSource(ctx),
            new DwellPageSource(ctx),
            new SelectionPageSource(ctx),
            new LayoutPageSource(ctx),
            new ActionMenuPageSource(ctx),
            new KeyboardPageSource(ctx),
            new ToolbarPageSource(ctx),
            new CalibrationPageSource(ctx),
            new GeneralPageSource(ctx),
        ];

        var bare = sources.SelectMany(BareRows).ToList();

        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithDownloadedBundled();
        var models = new ModelsPageSource(
            TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt));
        bare.AddRange(BareRows(models));

        Check.That(bare).IsEmpty();
    }

    private static IEnumerable<string> BareRows(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows)
            .Where(r => r is SliderSpec or ToggleSpec or ComboBoxSpec or ColorPickerSpec)
            .Where(r => string.IsNullOrWhiteSpace(r.Description))
            .Select(r => $"{source.GetType().Name}:{r.Id}");
}
```

- [x] **Step 2: Verify the test detects bareness (red-check), then passes**

Temporarily delete the `Description` initializer from one row (say `k_size`), run:
`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsRowDescriptionCompletenessTests"`
Expected: FAIL naming `KeyboardPageSource:k_size`. Restore the initializer, re-run.
Expected: PASS.

- [x] **Step 3: Full branch verification**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` — Expected: PASS, 610 total (608 baseline + range-vs-guard + completeness).
Run GitNexus `detect_changes({scope:"compare", base_ref:"main", repo:"eyectrl", worktree:"C:\\Users\\andre\\source\\repos\\eyectrl\\.worktrees\\settings-descriptions"})` — expected: changed symbols confined to the ten page-source `Build()` methods; no processes affected beyond settings rendering.

- [x] **Step 4: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs
git commit -m "test(settings): gate against settings rows shipping without a description"
```

---

## Self-Review (completed)

- **Spec coverage:** 45 descriptions = Task 2 (9+7+1) + Task 3 (10+1) + Task 4 (6+3+4+4); slider-min fix = Task 1; range-vs-guard test = Task 1; completeness test with explicit-type exemption rationale and Models-with-downloaded-model = Task 5; Cursor untouched; QuickActions untouched. Observations items are all no-action by spec.
- **Placeholders:** none — every description and test is verbatim in-plan.
- **Type consistency:** `SliderSpec.Min/Max`, `Description` init property, `PageSpec.Rows`, `ISettingsPageSource`, `ModelTestFakes.Create()`/`WithDownloadedBundled()`, `TestSettingsPageContext.Build(registry:, modelManagement:)` all match the source read on 2026-08-26.
