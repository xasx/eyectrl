# SettingsWindow Decomposition (Declarative Page-Spec) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decompose `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (~1,630 lines) by extracting its 11 procedural `Build*Pages` page-builders into per-tab `ISettingsPageSource` classes that emit a **declarative row-spec**, rendered by a single dispatch method in the shell — killing the P1 god-object while establishing the data shape a future full-MVVM migration binds to.

**Architecture:** A new `Views/Settings/` folder holds (a) a pure-data spec hierarchy (`SliderSpec`, `ToggleSpec`, `ComboBoxSpec`, `ColorPickerSpec`, `ButtonSpec`, `LabelSpec` + `PageSpec`), (b) a `SettingsPageContext` bag of services + view-delegates, (c) one `XxxPageSource` per tab whose `Build()` returns `IReadOnlyList<PageSpec>`. The shell gains a `RenderSpec(SettingsRowSpec)` switch that maps each spec to its `EyeCtrl.Controls.DwellXxx` and calls the existing `Add(...)` overloads.

**Two-phase bridge, then collapse (this is what makes the approach an MVVM bridge, not plain-A's detour):**
- *Phase 1 (Tasks 2–8):* `_pages` **stays** `List<List<Action>>` and each migrated `BuildXxxPages` is a one-line forwarder `=> _source.Build().Select(RenderPage).ToList()`. The reason is **per-tab incrementality, not the grep-tests**: `_pageBuilders` must stay homogeneous (`Func<List<List<Action>>`) while 10 tabs are still unmigrated, otherwise converting `_pages` to `List<PageSpec>` forces all 11 tabs into one giant commit. (The grep-tests only check the `_tabDefs` *identifier* and *id*, not the builder's return type, so they tolerate either phase.)
- *Phase 2 (Task 9):* once all 11 forwarders exist, **collapse the bridge** — `_pages` becomes `List<PageSpec>`, `_tabDefs`'s 4th element becomes `Func<IReadOnlyList<PageSpec>>`, `RenderCurrentPage` iterates `_pages[page].Rows` calling `RenderSpec`, and `RenderPage` is deleted. The forwarders shrink to `=> _source.Build();`. This is what leaves the spec as the canonical form going forward; without it, A′ ships plain-A's Action-list throwaway *plus* a spec layer. The grep-tests stay green through the collapse (forwarder names are still bare identifiers; `_tabDefs` ids unchanged).

**Tech Stack:** .NET 10, WPF (`net10.0-windows`), CommunityToolkit.Mvvm, xUnit v3 + NFluent, `EyeCtrl.Controls.DwellControl`/`IDwellComposite` family.

## Global Constraints

- **Conventions:** file-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled, no `#region`. NFluent (not xUnit asserts). xUnit v3 (`[Fact]`/`[StaFact]`).
- **Build/test output is German-locale:** `0 Fehler` = 0 errors, `Bestanden!` = passed.
- **Never run two test projects at once** (named-pipe contention — see AGENTS.md).
- **SettingsWindow's construction signature is frozen:** `EventCoordinator.ShowSettingsWindow` (`src/EyeCtrl.App/Services/EventCoordinator.cs:1208`; the `new SettingsWindow(...)` call spans ~1235–1245) is the only caller; the 8-dependency ctor, the `SettingsChanged` event, and `UpdateGaze(...)` must keep their current shapes.
- **FOUR source-grep fit-tests read `SettingsWindow.xaml.cs` as text.** Three survive untouched because the design keeps `_tabDefs`'s tuple shape, the `Add(DwellControl)` signature, and the `internal` consts byte-identical — do NOT repoint these:
  - `SettingsTabLabelFitTests.TabChrome` regex-parses `_tabDefs =` tuples shaped `("id","icon","label", \w+)` — keep `_tabDefs` in `SettingsWindow.xaml.cs` with bare-identifier 4th elements (a one-line forwarder method name still matches `\w+`).
  - `SettingsTabDriftTests.RenderedTabIds` parses `_tabDefs` ids (1st tuple element) from `SettingsWindow.xaml.cs`.
  - `SettingsContentFitTests.TheContentAdd_StillAppliesTheConstraint` greps for `private void Add(DwellControl control)` in `SettingsWindow.xaml.cs`.
  - **`SettingsContentFitTests.TheWidestSeparatorLabel_FitsTheContentColumn` (fourth — the one the bridge design does NOT preserve):** it regexes `new string\('\\u2500',\s*(\d+)\)` in `SettingsWindow.xaml.cs`, and the only matches are the two Models separators. **Task 7 moves them into `ModelsPageSource.cs` → this test breaks.** Task 7 therefore includes an explicit step to repoint that test's source-read at `ModelsPageSource.cs` and update its doc comment. The test's own failure message sanctions the repoint ("if they merely moved or changed character, repoint the pattern"). Also update the now-stale comment at `SettingsContentFitTests.cs:187` (`ChunkPickerPages` → `QuickActionsPageSource`'s picker builder).
- **Four `internal` consts + one `internal static` method must remain on `SettingsWindow`** (fit-test consumers): `TabContainerHeight`, `WindowSizeRatio`, `PickerButtonWidth`, `MinimumDwellableWidth`, `ConstrainToContentWidth`. Additionally **Task 2 promotes `ControlBtnRadius` and `ColorSwatchRadius` to `internal`** (sources reference them); they are not test-coupled today.
- **Run `dotnet test tests/EyeCtrl.Architecture.Tests` once before starting** (the layering gate on main). It does not reference `SettingsWindow` (verified), so it should be green; re-run it in the final task to confirm the refactor introduced no cross-project violation.
- **Constructor ordering is load-bearing (NRE, not compile error, if wrong):** `_ctx` and every `_*Source` field MUST be assigned in the ctor **before** `BuildTabButtons();` / `ShowTab("Cursor");` (~`SettingsWindow.xaml.cs:249–250`). `ShowTab` fires `CurrentTabName` → `RebuildContent()` → `BuildCursorPages()` → `_cursorSource.Build()`, and the QA `CurrentTabName` handler calls `_qaSource.ResetView()`. An agent that appends the source assignments *after* `ShowTab` gets a `NullReferenceException` on first window open. Task 2 places them correctly; Tasks 3–8 add theirs in the same spot — say "immediately after `_cursorSource = ...`", never "at the end of the ctor".
- **GitNexus notes `SettingsWindow` as HIGH-risk upstream.** That risk is the construction-signature/startup-flow surface, which this plan does NOT change. Every edit is to private `Build*Pages` bodies + new internal classes in a new folder. Run `detect_changes({scope: "compare", base_ref: "main"})` in the final task to confirm only expected symbols moved.
- **Impact before edit:** each task that rewrites a `Build*Pages` body should first run `impact({target:"BuildXxxPages",direction:"upstream",repo:"<repo>"})` — expected: zero external consumers (confirmed: `Build*Pages` are referenced only inside `SettingsWindow.xaml.cs`).
- **`using` directives per source (expected compile iterations):** several source listings abbreviate namespace qualifiers (`Core.Settings.DwellSettings`, `Core.Models.InteractionAction`, `Services.ModelManagementService.DeriveModelName`). Before each listing compiles, normalize these to top-level `using`s at the top of the file: `using EyeCtrl.Core.Settings;` (for `CursorSettings`/`DwellSettings`/`KeyboardSettings`/`ToolbarSettings`/`ActionMenuSettings`/`EyeCtrlSettings`/`ThemeSettings`), `using EyeCtrl.Core.Models;` (for `GazeFilterMode`/`SelectionMode`/`ViewPosition`/`InteractionAction`/`QuickActionType`/`QuickActionPresets`), `using EyeCtrl.Core.Prediction;` (for `ModelRegistryService` — it lives in Core/Prediction, **not** `EyeCtrl.App.Services`; `ModelManagementService` is the one in `EyeCtrl.App.Services`), `using EyeCtrl.ViewModels;` + `using EyeCtrl.ViewModels.Messages;` (for `ModalChoice`/`ModalChoiceKind`/`RestartAppRequestedMessage`/`CalibrationRequestedMessage`). Two converse facts: (a) `SettingsWindow` needs no using *from a source* (it lives in the parent namespace `EyeCtrl.App.Views`, visible by enclosing-namespace lookup); (b) **the shell needs `using EyeCtrl.App.Views.Settings;`** — a child namespace is NOT in scope from its parent, so `SettingsWindow.xaml.cs` must import `EyeCtrl.App.Views.Settings` to name `SettingsPageContext`/`CursorPageSource`/etc. The per-task `dotnet build` step surfaces any missing `using` as a fast compile error — fix and rebuild. This back-and-forth is the normal extraction-refactor compile-interaction, not a sign the pattern is wrong.

---

## File Structure

All new files live in `src/EyeCtrl.App/Views/Settings/` (namespace `EyeCtrl.App.Views.Settings`) and `tests/EyeCtrl.App.Tests/Settings/Pages/` (namespace `EyeCtrl.App.Tests.Settings.Pages`). Staying inside `EyeCtrl.App` is required: page sources construct `EyeCtrl.Controls` WPF types, and `EyeCtrl.ViewModels` does not reference WPF.

| File | Responsibility |
|------|----------------|
| `Views/Settings/SettingsRowSpec.cs` | Pure-data spec hierarchy: abstract `SettingsRowSpec` + `SliderSpec`, `ToggleSpec`, `ComboBoxSpec`, `ColorPickerSpec`, `ButtonSpec`, `LabelSpec`; `ButtonKind` enum; `PageSpec`. **No WPF types.** |
| `Views/Settings/SettingsPageContext.cs` | Sealed bag: settings/services the sources read + view-delegates (`ShowModal`, `ShowError`, `ApplyAndSave`, `RebuildContent`, `Invoke`) they invoke. |
| `Views/Settings/ISettingsPageSource.cs` | `IReadOnlyList<PageSpec> Build();` |
| `Views/Settings/Sources/CursorPageSource.cs` … `QuickActionsPageSource.cs` | One per tab. `internal`. Produce specs from current settings/registry/editor state. |
| `Views/SettingsWindow.xaml.cs` (modify) | Add `RenderSpec` + `RenderPage`; promote two consts to `internal`; instantiate sources + context; rewrite each `BuildXxxPages` body to a forwarder; keep `_tabDefs`/`Add`/consts intact. |
| `tests/EyeCtrl.App.Tests/Settings/Pages/XxxPageSourceTests.cs` | `[Fact]` (headless — specs are pure data). Assert each source's page/row structure + that getters/setters bind to a real `EyeCtrlSettings`. |

### The spec→control rendering bridge (throwaway in Task 9)

```
XxxPageSource.Build()      ──▶ IReadOnlyList<PageSpec>   (pure data; the canonical form, survives into MVVM)
SettingsWindow.RenderSpec(row) ──▶ Add(new DwellXxx(...))  (the view-side dispatch; stays)
Phase 1 only: RenderPage(PageSpec) ──▶ List<Action>        (the Action-compile shim; DELETED in Task 9)
```

During Phase 1 (Tasks 2–8) `_pages` stays `List<List<Action>>` and `RenderCurrentPage` is unchanged, so `_tabDefs`'s 4th-element type stays `Func<List<List<Action>>>` and migration happens one tab at a time with zero disruption to neighbours. **Task 9 collapses the shim**: `_pages` → `List<PageSpec>`, forwarders lose `.Select(RenderPage)`, `RenderPage` is deleted, and `RenderSpec` becomes the only render path.

---

## Task 1: Spec model + context + interface

**Files:**
- Create: `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs`
- Create: `src/EyeCtrl.App/Views/Settings/SettingsPageContext.cs`
- Create: `src/EyeCtrl.App/Views/Settings/ISettingsPageSource.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowSpecTests.cs`

**Interfaces:**
- Produces: `SettingsRowSpec` (abstract, `string Id`, `string Label`), `SliderSpec`, `ToggleSpec`, `ComboBoxSpec`, `ColorPickerSpec`, `ButtonSpec`, `LabelSpec`, `enum ButtonKind`, `PageSpec`, `SettingsPageContext`, `ISettingsPageSource`. Nothing consumes these yet (Task 2 wires the first one).

- [ ] **Step 1: Write the spec hierarchy**

Create `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs`:

```csharp
namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// Declarative description of one row on a settings page. Pure data + delegates, no WPF types:
/// <see cref="EyeCtrl.App.Views.SettingsWindow.RenderSpec"/> is the single place that maps a spec
/// onto an <see cref="EyeCtrl.Controls.DwellControl"/>. This data shape is what a future full-MVVM
/// migration binds to (it becomes the row view-model collection); only the renderer is throwaway.
/// </summary>
public abstract record SettingsRowSpec(string Id, string Label);

/// <summary>A stepped numeric value with a live read/write binding to a setting.</summary>
/// <param name="Decimals">Decimal places shown (mirrors <c>DwellSlider</c>'s <c>decimals</c> arg).</param>
public sealed record SliderSpec(
    string Id, string Label, double Min, double Max, double Step, int Decimals,
    Func<double> Get, Action<double> Set) : SettingsRowSpec(Id, Label);

public sealed record ToggleSpec(
    string Id, string Label, Func<bool> Get, Action<bool> Set) : SettingsRowSpec(Id, Label);

/// <param name="Code">Value codes; <c>null</c> means the display strings are also the codes.</param>
public sealed record ComboBoxSpec(
    string Id, string Label, string[] Display, string[]? Code,
    Func<string> Get, Action<string> Set) : SettingsRowSpec(Id, Label);

/// <param name="RadiusLogical">Hit radius in logical DIP (Cursor uses the swatch radius, Selection the control radius).</param>
public sealed record ColorPickerSpec(
    string Id, string Label, Func<string> Get, Action<string> Set, double RadiusLogical)
    : SettingsRowSpec(Id, Label);

public enum ButtonKind { Default, Reset, Delete }

/// <summary>An action button (reset / download / delete / nav). Width/Height are per-call (they vary).</summary>
public sealed record ButtonSpec(
    string Id, string Label, Action OnActivate, ButtonKind Kind, double Width, double Height)
    : SettingsRowSpec(Id, Label);

/// <param name="Text">Deferred so dynamic labels (calibration status) re-evaluate on each rebuild.</param>
public sealed record LabelSpec(Func<string> Text) : SettingsRowSpec("", "");

/// <summary>One page of the paginated content area; rendered top-to-bottom into <c>ContentPanel</c>.</summary>
public sealed record PageSpec(IReadOnlyList<SettingsRowSpec> Rows);
```

- [ ] **Step 2: Write the context bag**

Create `src/EyeCtrl.App/Views/Settings/SettingsPageContext.cs`:

```csharp
namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Everything a <see cref="ISettingsPageSource"/> needs to build its specs: the settings/services it
/// reads, and the view-bound delegates it invokes from action callbacks. The view-bound delegates
/// (ShowModal/ShowError/ApplyAndSave/RebuildContent/Invoke) are the seam that keeps page sources
/// free of WPF/Dispatcher coupling — the shell supplies them.
/// </summary>
internal sealed class SettingsPageContext
{
    public EyeCtrlSettings Settings { get; }
    public SettingsWindowViewModel ViewModel { get; }
    public ModelRegistryService Registry { get; }
    public ModelManagementService ModelManagement { get; }
    public CalibrationService Calibration { get; }
    public IMessenger Messenger { get; }
    public AppRestartCoordinator Restart { get; }
    public int DwellMs { get; }

    /// <param name="showModal"><c>(message, onYes)</c> — the two-button Yes/No modal. A thin wrapper
    /// over <see cref="ViewModel"/><c>.ShowModal(message, onYes)</c>; kept on the context for
    /// call-site readability. The General tab's three-choice modal bypasses it and calls
    /// <c>_ctx.ViewModel.ShowModal(message, choices)</c> directly (the only place that needs &gt;2 choices).</param>
    /// <param name="showError">Acknowledgement modal — the shell implements it as
    /// <c>ShowModal(message, () => {})</c>, so it is also reachable via the VM; kept for call-site clarity.</param>
    /// <param name="invoke">Marshals to the UI thread (Dispatcher.Invoke).</param>
    /// <param name="close">Closes the settings window (the Calibrate-Now flow closes before handing
    /// off to the calibration overlay). Defined up front so the context signature is stable across
    /// every task — do not add parameters to this ctor later without updating every test helper.</param>
    /// <param name="readClipboard">Reads the clipboard text, so <c>ModelsPageSource</c>'s custom-URL
    /// branch is headlessly testable instead of calling <c>System.Windows.Clipboard</c> directly
    /// (which would both leak WPF into the spec layer and make the invalid-URL/downloading branches
    /// untestable).</param>
    public SettingsPageContext(
        EyeCtrlSettings settings, SettingsWindowViewModel viewModel,
        ModelRegistryService registry, ModelManagementService modelManagement,
        CalibrationService calibration, IMessenger messenger, AppRestartCoordinator restart,
        int dwellMs,
        Action<string, Action> showModal, Action<string> showError,
        Action applyAndSave, Action rebuildContent, Action<Action> invoke, Action close,
        Func<string> readClipboard)
    {
        Settings = settings;
        ViewModel = viewModel;
        Registry = registry;
        ModelManagement = modelManagement;
        Calibration = calibration;
        Messenger = messenger;
        Restart = restart;
        DwellMs = dwellMs;
        ShowModal = showModal;
        ShowError = showError;
        ApplyAndSave = applyAndSave;
        RebuildContent = rebuildContent;
        Invoke = invoke;
        Close = close;
        ReadClipboard = readClipboard;
    }

    public Action<string, Action> ShowModal { get; }
    public Action<string> ShowError { get; }
    public Action ApplyAndSave { get; }
    public Action RebuildContent { get; }
    public Action<Action> Invoke { get; }
    public Action Close { get; }
    public Func<string> ReadClipboard { get; }
}
```

- [ ] **Step 3: Write the source interface**

Create `src/EyeCtrl.App/Views/Settings/ISettingsPageSource.cs`:

```csharp
namespace EyeCtrl.App.Views.Settings;

/// <summary>One settings tab's page content, as a declarative spec list. Re-invoked on every rebuild.</summary>
internal interface ISettingsPageSource
{
    IReadOnlyList<PageSpec> Build();
}
```

- [ ] **Step 4: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowSpecTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class SettingsRowSpecTests
{
    [Fact]
    public void SliderSpec_CarriesRangeAndBinding()
    {
        var slider = new SliderSpec("s", "Label", 0, 1, 0.1, 1, () => 0.5, _ => { });
        Check.That(slider.Id).IsEqualTo("s");
        Check.That(slider.Min).IsEqualTo(0);
        Check.That(slider.Max).IsEqualTo(1);
        Check.That(slider.Get()).IsEqualTo(0.5);
    }

    [Fact]
    public void ComboBoxSpec_NullCode_FallsBackToDisplayAtRender()
    {
        var combo = new ComboBoxSpec("c", "L", new[] { "A", "B" }, null, () => "A", _ => { });
        Check.That(combo.Code).IsNull();
        Check.That(combo.Display).HasSize(2);
    }

    [Fact]
    public void PageSpec_HoldsRowsInOrder()
    {
        var page = new PageSpec(new SettingsRowSpec[]
        {
            new SliderSpec("a", "A", 0, 1, 1, 0, () => 0, _ => { }),
            new LabelSpec(() => "hi"),
        });
        Check.That(page.Rows).HasSize(2);
        Check.That(page.Rows[0]).IsInstanceOf<SliderSpec>();
        Check.That(page.Rows[1]).IsInstanceOf<LabelSpec>();
    }
}
```

- [ ] **Step 5: Run the test (verify it passes — types are implemented, not pending)**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsRowSpecTests"`
Expected: PASS (`Bestanden!`). If it fails to find the namespace, confirm `tests/EyeCtrl.App.Tests` references `EyeCtrl.App` (it already does — see existing `SettingsContentFitTests`).

- [ ] **Step 6: Build the whole solution to confirm nothing else broke**

Run: `dotnet build EyeCtrl.slnx`
Expected: `0 Fehler`. (Nothing uses the new types yet, so this is a compile-only sanity check.)

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/ tests/EyeCtrl.App.Tests/Settings/
git commit -m "feat(settings): add declarative SettingsRowSpec model + context + ISettingsPageSource"
```

---

## Task 2: Renderer bridge in the shell + Cursor pilot

This task proves the entire pipeline (spec → `RenderSpec` → `Add(new DwellXxx)` → `ContentPanel`) with one real tab, and leaves the other 10 `Build*Pages` untouched.

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (consts `internal`; add `RenderSpec`/`RenderPage`; instantiate `_ctx` + `_cursorSource`; rewrite `BuildCursorPages` body)
- Create: `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/CursorPageSourceTests.cs`

**Interfaces:**
- Consumes: `SettingsRowSpec` family, `SettingsPageContext`, `ISettingsPageSource`, `EyeCtrl.Controls.DwellSlider/ComboBox/ColorPicker/Button/Label`, `DpiHelper`, `Tokens`, `EyeCtrlSettings`, `GazeFilterMode`.
- Produces: `SettingsWindow.RenderSpec(SettingsRowSpec)`, `SettingsWindow.RenderPage(PageSpec)`, `CursorPageSource`.

- [ ] **Step 1: Promote the two radius consts to internal**

In `SettingsWindow.xaml.cs`, change (lines ~24, ~26):
```csharp
    private const double ControlBtnRadius = 50;
    // Colour swatches are 90×75 px; 35 px keeps adjacent hit areas from overlapping.
    private const double ColorSwatchRadius = 35;
```
to:
```csharp
    // internal: page sources reference the control-radius standard (every content dwell target but
    // the colour swatches uses it). Not test-coupled today; promoted for the extraction.
    internal const double ControlBtnRadius = 50;
    // internal: CursorPageSource passes the swatch radius; SelectionPageSource passes the control radius.
    internal const double ColorSwatchRadius = 35;
```

- [ ] **Step 2: Add the renderer (RenderSpec + RenderPage) to the shell**

In `SettingsWindow.xaml.cs`, in the `// ────── Shared helpers ──────` region (near `Add`), add:

```csharp
    // ────── Spec rendering (the imperative bridge that a future MVVM step deletes) ──────

    /// <summary>
    /// Maps a <see cref="ButtonKind"/> to its (fill, label-brush) pair. Extracted and
    /// <c>internal static</c> so a headless test can pin it: <c>ResetBase</c> and <c>DeleteBase</c>
    /// are BOTH <c>Tokens.AccentWarning</c>, so swapping those two arms is invisible on screen —
    /// only the label-brush differs (surface-dark vs primary), which the test asserts.
    /// </summary>
    internal static (Color Fill, System.Windows.Media.Brush? Text) FillFor(ButtonKind kind) => kind switch
    {
        ButtonKind.Reset  => (ResetBase, Tokens.BrushSurfaceDark),
        ButtonKind.Delete => (DeleteBase, Tokens.BrushSurfaceDark),
        _                 => (BtnBase,   null),
    };

    /// <summary>
    /// Maps one declarative <paramref name="row"/> onto its <see cref="DwellControl"/> and places it
    /// via the <see cref="Add(DwellControl)"/>/<see cref="Add(IDwellComposite)"/>/<see cref="Add(DwellLabel)"/>
    /// overloads. The single WPF-coupled point of the spec pipeline; the per-tab sources are pure.
    /// </summary>
    private void RenderSpec(SettingsRowSpec row)
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        switch (row)
        {
            case SliderSpec s:
                Add(new DwellSlider(_viewModel, s.Id, s.Label, s.Min, s.Max, s.Step, s.Decimals,
                    s.Get, s.Set, cat, r, _dwellMs));
                break;
            case ToggleSpec t:
                Add(new DwellToggle(_viewModel, t.Id, t.Label, t.Get, t.Set, cat, r, _dwellMs));
                break;
            case ComboBoxSpec c:
                Add(new DwellComboBox(_viewModel, c.Id, c.Label, c.Display,
                    c.Code ?? c.Display, c.Get, c.Set, cat, r, _dwellMs));
                break;
            case ColorPickerSpec cp:
                Add(new DwellColorPicker(_viewModel, cp.Id, cp.Label, cp.Get, cp.Set, cat,
                    DpiHelper.LogicalToPhysicalX(cp.RadiusLogical), _dwellMs));
                break;
            case ButtonSpec b:
                var (fill, textBrush) = FillFor(b.Kind);
                Add(new DwellButton(_viewModel, b.Id, b.Label, b.OnActivate, fill, cat, r, _dwellMs,
                    width: b.Width, height: b.Height, textBrush: textBrush));
                break;
            case LabelSpec l:
                Add(new DwellLabel(l.Text()));
                break;
            default:
                throw new InvalidOperationException($"Unknown settings row spec: {row?.GetType().Name}");
        }
    }

    /// <summary>Compiles one page of specs to the Action list <c>_pages</c> holds today (Phase 1 only; deleted in Task 9).</summary>
    private List<Action> RenderPage(PageSpec page) => page.Rows.Select(row => (Action)(() => RenderSpec(row))).ToList();
```

> Why keep `_pages = List<List<Action>>`? So every unmigrated tab still returns its existing Action list verbatim and `_tabDefs`'s tuple type is unchanged — migration is per-tab with zero neighbor disruption. `RenderPage` is the only compile step.

- [ ] **Step 3: Build the context + cursor source in the constructor**

In `SettingsWindow.xaml.cs` ctor, after the `_pageBuilders` loop and before `BuildTabButtons();`, add fields + construction. First add two fields near the other `private readonly` state (top of class) — no `= null!`, matching the existing ctor-assigned fields at lines 95–103:

```csharp
    private readonly SettingsPageContext _ctx;
    private readonly CursorPageSource _cursorSource;
```

Then in the ctor, immediately before `BuildTabButtons();` (line ~249 — **before** `ShowTab("Cursor")` at :250; see Global Constraints on ordering), insert:

```csharp
        // Spec pipeline: page sources build declarative specs; RenderSpec renders them. The context
        // bag carries the view-delegate seam (ShowModal etc.) so sources stay WPF-free.
        _ctx = new SettingsPageContext(
            _settings, _viewModel, _registryService, _modelManagementService,
            _calibrationService, _messenger, _restartCoordinator, _dwellMs,
            ShowModal, ShowError, ApplyAndSave, RebuildContent, a => Dispatcher.Invoke(a), Close,
            () => System.Windows.Clipboard.GetText() ?? "");
        _cursorSource = new CursorPageSource(_ctx);
```

> The later tasks add the other 10 sources here, in tab order.

- [ ] **Step 4: Create CursorPageSource**

Create `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs`:

```csharp
namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Views;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The Cursor tab, page-for-page the former <c>SettingsWindow.BuildCursorPages</c>: filter mode,
/// smoothing, snap, colour on page 1; opacity, thickness, reset on page 2.
/// </summary>
internal sealed class CursorPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public CursorPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var c = _ctx.Settings.Cursor;
        return
        [
            new PageSpec([
                new ComboBoxSpec("c_filter", "Filter Mode", Enum.GetNames<GazeFilterMode>(), null,
                    () => c.FilterMode.ToString(),
                    v => { c.FilterMode = Enum.Parse<GazeFilterMode>(v); _ctx.ApplyAndSave(); }),
                new SliderSpec("c_smooth", "Smoothing Factor", 0, 1, 0.1, 1,
                    () => c.SmoothingFactor,
                    v => { c.SmoothingFactor = v; _ctx.ApplyAndSave(); }),
                new SliderSpec("c_snap", "Snap Radius (px)", 10, 100, 5, 0,
                    () => c.SnapRadiusPixels,
                    v => { c.SnapRadiusPixels = (int)v; _ctx.ApplyAndSave(); }),
                new ColorPickerSpec("c_color", "Circle Color",
                    () => c.CircleColor,
                    v => { c.CircleColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius),
            ]),
            new PageSpec([
                new SliderSpec("c_opacity", "Circle Opacity", 0.1, 1, 0.1, 1,
                    () => c.CircleOpacity,
                    v => { c.CircleOpacity = v; _ctx.ApplyAndSave(); }),
                new SliderSpec("c_thick", "Circle Thickness", 1, 6, 0.5, 1,
                    () => c.CircleThickness,
                    v => { c.CircleThickness = v; _ctx.ApplyAndSave(); }),
                new ButtonSpec("c_reset", "Reset Cursor Settings",
                    () => _ctx.ShowModal("Reset Cursor settings to defaults?", () =>
                    {
                        var d = new CursorSettings();
                        c.FilterMode = d.FilterMode;
                        c.SmoothingFactor = d.SmoothingFactor;
                        c.SnapRadiusPixels = d.SnapRadiusPixels;
                        c.CircleColor = d.CircleColor;
                        c.CircleOpacity = d.CircleOpacity;
                        c.CircleThickness = d.CircleThickness;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }
}
```

> NOTE on `SettingsWindow.ColorSwatchRadius`: `SettingsWindow` is `public` and now `ColorSwatchRadius` is `internal const`, so the source (same assembly) can read it. The `using EyeCtrl.App.Views;` is the namespace containing `SettingsWindow`.

- [ ] **Step 5: Rewrite BuildCursorPages as the forwarder**

In `SettingsWindow.xaml.cs`, replace the entire body of `BuildCursorPages` (currently lines ~532–587) with:

```csharp
    private List<List<Action>> BuildCursorPages() =>
        _cursorSource.Build().Select(RenderPage).ToList();
```

Delete the old body entirely (the `var r = ...; var cat = ...; return [ [() => Add(...)], ... ];` block). The method signature `private List<List<Action>> BuildCursorPages()` is unchanged, so `_tabDefs`'s `("Cursor","◎","Cursor",BuildCursorPages)` still compiles and `SettingsTabLabelFitTests.TabChrome`'s `\w+` still matches.

- [ ] **Step 6: Shared test-context factory + Cursor test + FillFor test**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/TestSettingsPageContext.cs` — **one** factory for the 15-arg context, used by every per-source test (Tasks 2–8). This is the response to the copy-drift risk: the context signature lives in one place, so adding a parameter later touches one file, not seven.

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Builds a <see cref="SettingsPageContext"/> for headless page-source tests: real settings,
/// default-constructible services where they exist, no-op view delegates. Override only the deps a
/// source actually reads (e.g. <see cref="ModelsPageSource"/> passes registry+modelManagement,
/// <see cref="QuickActionsPageSource"/> passes viewModel+messenger).
/// </summary>
internal static class TestSettingsPageContext
{
    public static SettingsPageContext Build(
        EyeCtrlSettings? settings = null,
        SettingsWindowViewModel? viewModel = null,
        ModelRegistryService? registry = null,
        ModelManagementService? modelManagement = null,
        CalibrationService? calibration = null,
        IMessenger? messenger = null,
        Action<string, Action>? showModal = null,
        Func<string>? readClipboard = null) => new(
        settings ?? new EyeCtrlSettings(),
        viewModel!, registry!, modelManagement!,
        calibration ?? new CalibrationService(),
        messenger!,
        restart: null!, dwellMs: 500,
        showModal: showModal ?? ((_, _) => { }),
        showError: _ => { },
        applyAndSave: () => { }, rebuildContent: () => { }, invoke: _ => { },
        close: () => { }, readClipboard: readClipboard ?? (() => ""));
}
```

Create `tests/EyeCtrl.App.Tests/Settings/Pages/CursorPageSourceTests.cs` (specs are pure data → `[Fact]`):

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

public class CursorPageSourceTests
{
    [Fact]
    public void Build_TwoPages_FilterThenReset()
    {
        var source = new CursorPageSource(TestSettingsPageContext.Build());
        var pages = source.Build();

        Check.That(pages).HasSize(2);
        Check.That(pages[0].Rows[0]).IsInstanceOf<ComboBoxSpec>();
        Check.That(((ComboBoxSpec)pages[0].Rows[0]).Id).IsEqualTo("c_filter");

        var reset = pages[1].Rows.OfType<ButtonSpec>().Single();
        Check.That(reset.Id).IsEqualTo("c_reset");
        Check.That(reset.Kind).IsEqualTo(ButtonKind.Reset);
    }

    [Fact]
    public void Build_ColorPickerUsesSwatchRadius()
    {
        var source = new CursorPageSource(TestSettingsPageContext.Build());
        var cp = source.Build().SelectMany(p => p.Rows).OfType<ColorPickerSpec>().Single();
        Check.That(cp.RadiusLogical).IsEqualTo(SettingsWindow.ColorSwatchRadius);
    }

    [Fact]
    public void Build_GettersReadLiveSettings()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.SmoothingFactor = 0.75;
        var source = new CursorPageSource(TestSettingsPageContext.Build(settings));
        var smooth = source.Build().SelectMany(p => p.Rows).OfType<SliderSpec>()
            .Single(s => s.Id == "c_smooth");
        Check.That(smooth.Get()).IsEqualTo(0.75);
    }
}
```

Create `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRenderMappingTests.cs` — pins `FillFor` (R4). `[StaFact]` because it dereferences `Tokens` brushes; the page-source tests above stay `[Fact]` because they never invoke anything that touches WPF.

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls.Theme;
using System.Windows.Media;
using NFluent;
using Xunit;

public class SettingsRenderMappingTests
{
    // ResetBase and DeleteBase are BOTH Tokens.AccentWarning, so swapping the two fill arms is
    // invisible on screen — only the label brush differs (surface-dark for both, primary for
    // Default). This pins the brush mapping, which is the only observable difference.
    [StaFact]
    public void FillFor_ResetAndDelete_UseDarkLabel_DefaultUsesPrimary()
    {
        var (_, resetText) = SettingsWindow.FillFor(ButtonKind.Reset);
        var (_, deleteText) = SettingsWindow.FillFor(ButtonKind.Delete);
        var (_, defaultText) = SettingsWindow.FillFor(ButtonKind.Default);

        Check.That(resetText).IsSameReferenceAs(Tokens.BrushSurfaceDark);
        Check.That(deleteText).IsSameReferenceAs(Tokens.BrushSurfaceDark);
        Check.That(defaultText).IsNull();
    }

    [StaFact]
    public void FillFor_ResetAndDeleteFills_AreTheSharedWarning_DefaultIsTheSurface()
    {
        // The fills themselves: both warning-tinted, default is the neutral surface. Asserted so a
        // future token rename that silently merges them is caught here, not on a live screen.
        var (resetFill, _) = SettingsWindow.FillFor(ButtonKind.Reset);
        var (deleteFill, _) = SettingsWindow.FillFor(ButtonKind.Delete);
        var (defaultFill, _) = SettingsWindow.FillFor(ButtonKind.Default);

        Check.That(resetFill).IsEqualTo(Tokens.AccentWarning);
        Check.That(deleteFill).IsEqualTo(Tokens.AccentWarning);
        Check.That(defaultFill).IsEqualTo(Tokens.SurfaceMedium);
    }
}
```

> `SettingsWindow.FillFor` / `SettingsWindow.ColorSwatchRadius` are reachable because `EyeCtrl.App.Tests` is `InternalsVisibleTo` from `EyeCtrl.App` (`EyeCtrl.App.csproj:61`).

- [ ] **Step 7: Run the new test (verify pass)**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~CursorPageSourceTests"`
Expected: PASS.

- [ ] **Step 8: Run the four source-grep fit-tests + drift test (verify they are untouched)**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsTabLabelFitTests|FullyQualifiedName~SettingsTabDriftTests|FullyQualifiedName~SettingsContentFitTests"`
Expected: all PASS. If any fails, `_tabDefs` or `Add(DwellControl)` was disturbed — re-check Steps 1 and 5 did not alter those.

- [ ] **Step 9: Build + run the full EyeCtrl.App.Tests project (no display needed — these are headless)**

Run: `dotnet test tests/EyeCtrl.App.Tests`
Expected: PASS (all green, including `SettingsWindowViewModelTests` which still asserts `.Tabs.HasSize(13)` — untouched).

- [ ] **Step 10: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/CursorPageSourceTests.cs
git commit -m "refactor(settings): extract Cursor page builder to declarative CursorPageSource (pilot)"
```

---

## Task 3: Migrate the uniform medium tabs (Dwell, Selection, Keyboard, Toolbar)

Same pattern as the Cursor pilot, four sources. Each `BuildXxxPages` body becomes a one-line forwarder; each source is transcribed verbatim from the current builder (sliders/toggles/combos → matching specs; reset buttons → `ButtonSpec(…, ButtonKind.Reset, 300, 80)`). Add four fields + four ctor assignments beside `_cursorSource`.

**Files:**
- Create: `Sources/DwellPageSource.cs`, `Sources/SelectionPageSource.cs`, `Sources/KeyboardPageSource.cs`, `Sources/ToolbarPageSource.cs`
- Modify: `SettingsWindow.xaml.cs` (forwarders + ctor assignments)
- Test: one `DwellSelectionKeyboardToolbarPageSourceTests.cs`

- [ ] **Step 1: Create DwellPageSource**

Create `src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs`. Body (transcribed from `BuildDwellPages`, lines ~589–663):

```csharp
namespace EyeCtrl.App.Views.Settings;

internal sealed class DwellPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public DwellPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var d = _ctx.Settings.Dwell;
        return
        [
            new PageSpec([
                new SliderSpec("d_fix", "Fixation Detection (ms)", 50, 500, 25, 0,
                    () => d.FixationDetectionMs, v => { d.FixationDetectionMs = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("d_act", "Activation Button (ms)", 200, 1500, 50, 0,
                    () => d.ActivationButtonDwellMs, v => { d.ActivationButtonDwellMs = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("d_menu", "Action Menu (ms)", 200, 1500, 50, 0,
                    () => d.ActionMenuDwellMs, v => { d.ActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("d_ManualSelection", "ManualSelection Action Menu (ms)", 200, 1500, 50, 0,
                    () => d.ManualSelectionActionMenuDwellMs, v => { d.ManualSelectionActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); }),
            ]),
            new PageSpec([
                new SliderSpec("d_tool", "Toolbar / Settings (ms)", 200, 1500, 50, 0,
                    () => d.ToolbarButtonDwellMs, v => { d.ToolbarButtonDwellMs = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("d_key", "Keyboard Key (ms)", 100, 1000, 25, 0,
                    () => d.KeyboardKeyDwellMs, v => { d.KeyboardKeyDwellMs = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("d_pred", "Prediction (ms)", 100, 1000, 25, 0,
                    () => d.KeyboardPredictionDwellMs, v => { d.KeyboardPredictionDwellMs = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("d_rad", "Fixation Radius (px)", 20, 120, 5, 0,
                    () => d.FixationRadiusPixels, v => { d.FixationRadiusPixels = v; _ctx.ApplyAndSave(); }),
                new SliderSpec("d_cooldown", "Dwell Cooldown (ms)", 0, 1000, 50, 0,
                    () => d.DwellCooldownMs, v => { d.DwellCooldownMs = (int)v; _ctx.ApplyAndSave(); }),
                new ButtonSpec("d_reset", "Reset Dwell Settings",
                    () => _ctx.ShowModal("Reset Dwell settings to defaults?", () =>
                    {
                        var def = new Core.Settings.DwellSettings();
                        d.FixationDetectionMs = def.FixationDetectionMs;
                        d.ActivationButtonDwellMs = def.ActivationButtonDwellMs;
                        d.ActionMenuDwellMs = def.ActionMenuDwellMs;
                        d.ManualSelectionActionMenuDwellMs = def.ManualSelectionActionMenuDwellMs;
                        d.ToolbarButtonDwellMs = def.ToolbarButtonDwellMs;
                        d.KeyboardKeyDwellMs = def.KeyboardKeyDwellMs;
                        d.KeyboardPredictionDwellMs = def.KeyboardPredictionDwellMs;
                        d.FixationRadiusPixels = def.FixationRadiusPixels;
                        d.DwellCooldownMs = def.DwellCooldownMs;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }
}
```

- [ ] **Step 2: Create SelectionPageSource**

Create `src/EyeCtrl.App/Views/Settings/Sources/SelectionPageSource.cs` (transcribed from `BuildSelectionPages`, ~665–709). Note the colour picker here uses the **control radius**, not the swatch radius:

```csharp
namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Models;

internal sealed class SelectionPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public SelectionPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings;
        return
        [
            new PageSpec([
                new ComboBoxSpec("s_mode", "Selection Mode",
                    new[] { "None", "Manual Selection", "Auto Selection" },
                    new[] { "None", "ManualSelection", "AutoSelection" },
                    () => s.SelectionMode.ToString(),
                    v => { s.SelectionMode = Enum.Parse<SelectionMode>(v); _ctx.ApplyAndSave(); }),
                new SliderSpec("s_rad", "Circle Radius (px)", 50, 300, 10, 0,
                    () => s.ManualSelection.CircleRadiusPixels, v => { s.ManualSelection.CircleRadiusPixels = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("s_mag", "Magnification Level", 1.5, 5, 0.5, 1,
                    () => s.ManualSelection.MagnificationLevel, v => { s.ManualSelection.MagnificationLevel = v; _ctx.ApplyAndSave(); }),
                new SliderSpec("s_grace", "Grace Period (ms)", 0, 2000, 50, 0,
                    () => s.ManualSelection.ManualSelectionGracePeriodMs, v => { s.ManualSelection.ManualSelectionGracePeriodMs = (int)v; _ctx.ApplyAndSave(); }),
                new ColorPickerSpec("s_highlight", "Highlight Color",
                    () => s.AutoSelection.HighlightColor, v => { s.AutoSelection.HighlightColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ControlBtnRadius),
                new SliderSpec("s_highlightThickness", "Highlight Thickness", 1, 10, 1, 0,
                    () => s.AutoSelection.HighlightThickness, v => { s.AutoSelection.HighlightThickness = v; _ctx.ApplyAndSave(); }),
            ]),
        ];
    }
}
```

- [ ] **Step 3: Create KeyboardPageSource**

Create `src/EyeCtrl.App/Views/Settings/Sources/KeyboardPageSource.cs` (transcribed from `BuildKeyboardPages`, ~758–811):

```csharp
namespace EyeCtrl.App.Views.Settings;

internal sealed class KeyboardPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public KeyboardPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var k = _ctx.Settings.Keyboard;
        return
        [
            new PageSpec([
                new ComboBoxSpec("k_layout", "Active Layout",
                    new[] { "QWERTY", "QWERTZ", "AZERTY" }, null,
                    () => k.ActiveLayout, v => { k.ActiveLayout = v; _ctx.ApplyAndSave(); }),
                new SliderSpec("k_size", "Key Size Multiplier", 0.5, 2.0, 0.1, 1,
                    () => k.KeySizeMultiplier, v => { k.KeySizeMultiplier = v; _ctx.ApplyAndSave(); }),
                new ToggleSpec("k_pred", "Word Prediction",
                    () => k.PredictionEnabled, v => { k.PredictionEnabled = v; _ctx.ApplyAndSave(); }),
                new ComboBoxSpec("k_lang", "Dictionary Language",
                    new[] { "English", "French", "Spanish", "German", "Polish" },
                    new[] { "en", "fr", "es", "de", "pl" },
                    () => string.IsNullOrEmpty(k.LanguagePack) ? "en" : k.LanguagePack,
                    v => { k.LanguagePack = v; _ctx.ApplyAndSave(); }),
                new ToggleSpec("k_sound", "Click Sound",
                    () => k.ClickSoundEnabled, v => { k.ClickSoundEnabled = v; _ctx.ApplyAndSave(); }),
                new ComboBoxSpec("k_edge", "Screen Edge",
                    new[] { "Bottom", "Top" }, null,
                    () => k.ScreenEdge, v => { k.ScreenEdge = v; _ctx.ApplyAndSave(); }),
                new ButtonSpec("k_reset", "Reset Keyboard Settings",
                    () => _ctx.ShowModal("Reset Keyboard settings to defaults?", () =>
                    {
                        var def = new Core.Settings.KeyboardSettings();
                        k.ActiveLayout = def.ActiveLayout;
                        k.KeySizeMultiplier = def.KeySizeMultiplier;
                        k.PredictionEnabled = def.PredictionEnabled;
                        k.LanguagePack = "";
                        k.ScreenEdge = def.ScreenEdge;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }
}
```

- [ ] **Step 4: Create ToolbarPageSource**

Create `src/EyeCtrl.App/Views/Settings/Sources/ToolbarPageSource.cs` (transcribed from `BuildToolbarPages`, ~813–848):

```csharp
namespace EyeCtrl.App.Views.Settings;

internal sealed class ToolbarPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public ToolbarPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var t = _ctx.Settings.Toolbar;
        return
        [
            new PageSpec([
                new ToggleSpec("t_dock", "Dock to Screen Edge",
                    () => t.IsDocked, v => { t.IsDocked = v; _ctx.ApplyAndSave(); }),
                new ComboBoxSpec("t_edge", "Docked Edge",
                    new[] { "Top", "Bottom", "Left", "Right" }, null,
                    () => t.DockedEdge, v => { t.DockedEdge = v; _ctx.ApplyAndSave(); }),
                new SliderSpec("t_size", "Button Size", 32, 120, 4, 0,
                    () => t.Size, v => { t.Size = v; _ctx.ApplyAndSave(); }),
                new ButtonSpec("t_reset", "Reset Toolbar Settings",
                    () => _ctx.ShowModal("Reset Toolbar settings to defaults?", () =>
                    {
                        var def = new Core.Settings.ToolbarSettings();
                        t.IsDocked = def.IsDocked;
                        t.DockedEdge = def.DockedEdge;
                        t.Size = def.Size;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }
}
```

- [ ] **Step 5: Wire the four sources into the shell ctor + add fields**

Add four fields beside `_cursorSource`:
```csharp
    private readonly DwellPageSource _dwellSource;
    private readonly SelectionPageSource _selectionSource;
    private readonly KeyboardPageSource _keyboardSource;
    private readonly ToolbarPageSource _toolbarSource;
```
And in the ctor, after `_cursorSource = new CursorPageSource(_ctx);`:
```csharp
        _dwellSource = new DwellPageSource(_ctx);
        _selectionSource = new SelectionPageSource(_ctx);
        _keyboardSource = new KeyboardPageSource(_ctx);
        _toolbarSource = new ToolbarPageSource(_ctx);
```

- [ ] **Step 6: Replace the four Build methods' bodies with forwarders**

```csharp
    private List<List<Action>> BuildDwellPages() => _dwellSource.Build().Select(RenderPage).ToList();
    private List<List<Action>> BuildSelectionPages() => _selectionSource.Build().Select(RenderPage).ToList();
    private List<List<Action>> BuildKeyboardPages() => _keyboardSource.Build().Select(RenderPage).ToList();
    private List<List<Action>> BuildToolbarPages() => _toolbarSource.Build().Select(RenderPage).ToList();
```
Delete the four old bodies entirely.

- [ ] **Step 7: Write the test**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/DwellSelectionKeyboardToolbarPageSourceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class DwellSelectionKeyboardToolbarPageSourceTests
{
    // No local Ctx() — use the shared TestSettingsPageContext.Build() introduced in Task 2.
    [Fact]
    public void Dwell_TwoPagesWithCooldownAndReset()
    {
        var pages = new DwellPageSource(TestSettingsPageContext.Build()).Build();
        Check.That(pages).HasSize(2);
        Check.That(pages[1].Rows.OfType<ButtonSpec>().Single().Id).IsEqualTo("d_reset");
        Check.That(pages[1].Rows.OfType<SliderSpec>().Select(s => s.Id))
            .Contains(new[] { "d_tool", "d_key", "d_pred", "d_rad", "d_cooldown" });
    }

    [Fact]
    public void Selection_HighlightColorUsesControlRadius()
    {
        var cp = new SelectionPageSource(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows)
            .OfType<ColorPickerSpec>().Single();
        Check.That(cp.RadiusLogical).IsEqualTo(SettingsWindow.ControlBtnRadius);
    }

    [Fact]
    public void Keyboard_LanguageComboHasCodeMapping()
    {
        var lang = new KeyboardPageSource(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows)
            .OfType<ComboBoxSpec>().Single(c => c.Id == "k_lang");
        Check.That(lang.Code).IsNotNull();
        Check.That(lang.Code!).Contains(new[] { "en", "de" });
    }

    [Fact]
    public void Toolbar_DockedEdgeComboHasNullCode()
    {
        var edge = new ToolbarPageSource(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows)
            .OfType<ComboBoxSpec>().Single(c => c.Id == "t_edge");
        Check.That(edge.Code).IsNull();
    }
}
```

- [ ] **Step 8: Run the new tests + the three fit/drift suites**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellSelectionKeyboardToolbarPageSourceTests|FullyQualifiedName~SettingsTabLabelFitTests|FullyQualifiedName~SettingsTabDriftTests|FullyQualifiedName~SettingsContentFitTests"`
Expected: all PASS.

- [ ] **Step 9: Build + run the full project**

Run: `dotnet test tests/EyeCtrl.App.Tests`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "refactor(settings): extract Dwell/Selection/Keyboard/Toolbar builders to page sources"
```

---

## Task 4: Migrate the tiny tabs (Layout, ActionMenu)

**Files:**
- Create: `Sources/LayoutPageSource.cs`, `Sources/ActionMenuPageSource.cs`
- Modify: `SettingsWindow.xaml.cs` (forwarders + ctor)
- Test: append to the Task-3 test file or a new `LayoutActionMenuPageSourceTests.cs`

- [ ] **Step 1: Create LayoutPageSource** (from `BuildLayoutPages`, ~732–756)

```csharp
namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Models;

internal sealed class LayoutPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public LayoutPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings;
        return
        [
            new PageSpec([
                new ComboBoxSpec("lay_pos", "Position",
                    new[] { "At Gaze", "At Center" }, null,
                    () => s.ViewPosition == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { s.ViewPosition = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; _ctx.ApplyAndSave(); }),
                new ButtonSpec("lay_reset", "Reset Layout Settings",
                    () => _ctx.ShowModal("Reset Layout settings to defaults?", () =>
                    {
                        s.ViewPosition = new Core.Settings.EyeCtrlSettings().ViewPosition;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }
}
```

- [ ] **Step 2: Create ActionMenuPageSource** (from `BuildActionMenuPages`, ~711–730)

```csharp
namespace EyeCtrl.App.Views.Settings;

internal sealed class ActionMenuPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public ActionMenuPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var a = _ctx.Settings.ActionMenu;
        return
        [
            new PageSpec([
                new ButtonSpec("am_reset", "Reset Action Menu Settings",
                    () => _ctx.ShowModal("Reset Action Menu settings to defaults?", () =>
                    {
                        a.VisibleActions = new System.Collections.Generic.List<Core.Models.InteractionAction>(
                            new Core.Settings.ActionMenuSettings().VisibleActions);
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }
}
```

- [ ] **Step 3: Wire + forward in the shell** (fields + ctor assignments + two forwarders, same pattern as Task 3 Steps 5–6).

- [ ] **Step 4: Write the test**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/LayoutActionMenuPageSourceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class LayoutActionMenuPageSourceTests
{
    [Fact]
    public void Layout_OnePageWithPositionComboAndReset()
    {
        var rows = new LayoutPageSource(TestSettingsPageContext.Build()).Build().Single().Rows;
        Check.That(rows.OfType<ComboBoxSpec>().Single().Id).IsEqualTo("lay_pos");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Kind)).ContainsOnly(ButtonKind.Reset);
    }

    [Fact]
    public void ActionMenu_OneResetButton()
    {
        var rows = new ActionMenuPageSource(TestSettingsPageContext.Build()).Build().Single().Rows;
        Check.That(rows).HasSize(1);
        Check.That(rows[0]).IsInstanceOf<ButtonSpec>();
    }
}
```

- [ ] **Step 5: Run tests + full project, then commit**

Run: `dotnet test tests/EyeCtrl.App.Tests`  → Expected: PASS.
```bash
git add -A && git commit -m "refactor(settings): extract Layout/ActionMenu builders to page sources"
```

---

## Task 5: Migrate the General tab (restart modal branching)

This is the first tab whose reset button has non-trivial branching (the Restart Now choice between supervised / plain / cancel). The modal is built directly via `_ctx.ViewModel.ShowModal(message, IReadOnlyList<ModalChoice>)`.

**Files:**
- Create: `Sources/GeneralPageSource.cs`
- Modify: `SettingsWindow.xaml.cs`
- Test: `GeneralPageSourceTests.cs`

- [ ] **Step 1: Create GeneralPageSource** (from `BuildGeneralPages`, ~1020–1096)

```csharp
namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

internal sealed class GeneralPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public GeneralPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings;
        return
        [
            new PageSpec([
                new ComboBoxSpec("g_theme", "Theme (restart to apply)",
                    Core.Settings.ThemeSettings.ValidNames, null,
                    () => s.Theme.Name, v => { s.Theme.Name = v; _ctx.ApplyAndSave(); }),
                new ToggleSpec("g_boot", "Start on Windows Boot",
                    () => s.General.StartOnBoot, v => { s.General.StartOnBoot = v; _ctx.ApplyAndSave(); }),
                new ToggleSpec("g_pause", "Start Paused",
                    () => s.General.StartPaused, v => { s.General.StartPaused = v; _ctx.ApplyAndSave(); }),
                new ToggleSpec("g_track", "Show Track Status",
                    () => s.TrackStatus.Enabled, v => { s.TrackStatus.Enabled = v; _ctx.ApplyAndSave(); }),
                new ButtonSpec("g_restart", "↻ Restart Now", OnRestart, ButtonKind.Reset, Width: 300, Height: 80),
                new ButtonSpec("g_reset", "Reset General Settings",
                    () => _ctx.ShowModal("Reset General settings to defaults?", () =>
                    {
                        var g = new Core.Settings.GeneralSettings();
                        s.General.StartOnBoot = g.StartOnBoot;
                        s.General.StartPaused = g.StartPaused;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
                new ButtonSpec("g_resetall", "\u26A0 Reset ALL Settings",
                    () => _ctx.ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        _ctx.ViewModel.Settings.ResetAllSettings();
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }

    private void OnRestart()
    {
        // Only offer what is installed and not already running. The multi-choice modal is built
        // directly on the ViewModel (the same object ShowModal's two-button helper wraps).
        if (_ctx.Restart.ShouldOfferSupervisor())
        {
            _ctx.ViewModel.ShowModal(
                "Restart EyeCtrl now?\n"
                + "A supervisor is installed. It restarts EyeCtrl if it ever fails, and can recover "
                + "a restart that goes wrong.\nStarting it turns supervision on and leaves it on.",
                [
                    new ModalChoice("restart_supervised", "↻ Restart with supervisor",
                        ModalChoiceKind.Affirmative,
                        () => _ctx.Messenger.Send(new RestartAppRequestedMessage(StartSupervisor: true))),
                    new ModalChoice("restart_plain", "↻ Restart without",
                        ModalChoiceKind.Neutral,
                        () => _ctx.Messenger.Send(new RestartAppRequestedMessage())),
                    new ModalChoice("restart_cancel", "✕ Cancel",
                        ModalChoiceKind.Negative, null),
                ]);
        }
        else
        {
            _ctx.ShowModal("Restart EyeCtrl now?",
                () => _ctx.Messenger.Send(new RestartAppRequestedMessage()));
        }
    }
}
```

> `OnRestart` is split out so the test can assert the button exists without invoking it (it touches `Restart.ShouldOfferSupervisor()`).

- [ ] **Step 2: Wire + forward in the shell** (`_generalSource` field, ctor assignment, `BuildGeneralPages` → forwarder). Same pattern.

- [ ] **Step 3: Write the test**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/GeneralPageSourceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class GeneralPageSourceTests
{
    [Fact]
    public void General_OnePageWithThemeBootPauseTrackAndThreeButtons()
    {
        var rows = new GeneralPageSource(TestSettingsPageContext.Build()).Build().Single().Rows;
        Check.That(rows.OfType<ComboBoxSpec>().Single().Id).IsEqualTo("g_theme");
        Check.That(rows.OfType<ToggleSpec>().Select(t => t.Id))
            .Contains(new[] { "g_boot", "g_pause", "g_track" });
        var buttons = rows.OfType<ButtonSpec>().ToList();
        Check.That(buttons.Select(b => b.Id)).Contains(new[] { "g_restart", "g_reset", "g_resetall" });
        // ContainsOnly (not .All().IsTrue()) so a future non-Reset button names itself in the message.
        Check.That(buttons.Select(b => b.Kind)).ContainsOnly(ButtonKind.Reset);
    }
}
```

- [ ] **Step 4: Run tests + full project, then commit**

Run: `dotnet test tests/EyeCtrl.App.Tests` → Expected: PASS.
```bash
git add -A && git commit -m "refactor(settings): extract General builder (incl. restart modal) to page source"
```

---

## Task 6: Migrate the Calibration tab (dynamic status label)

The status text is a `LabelSpec` whose `Text` re-reads `_ctx.Calibration` on each rebuild.

**Files:**
- Create: `Sources/CalibrationPageSource.cs`
- Modify: `SettingsWindow.xaml.cs` (also delete the now-orphaned `CalibrationStatusText` private method — it moves into the source)
- Test: `CalibrationPageSourceTests.cs`

- [ ] **Step 1: Create CalibrationPageSource** (from `BuildCalibrationPages` + `CalibrationStatusText`, ~1258–1318)

```csharp
namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.ViewModels.Messages;

internal sealed class CalibrationPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public CalibrationPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var cal = _ctx.Settings.Calibration;
        return
        [
            new PageSpec([
                new SliderSpec("cal_points", "Calibration Points", 3, 16, 1, 0,
                    () => cal.PointCount, v => { cal.PointCount = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("cal_samples", "Samples per Point", 5, 100, 5, 0,
                    () => cal.SamplesPerPoint, v => { cal.SamplesPerPoint = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("cal_hold", "Target Hold (ms)", 200, 3000, 100, 0,
                    () => cal.TargetHoldMs, v => { cal.TargetHoldMs = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("cal_settle", "Settle Window (ms)", 0, 500, 25, 0,
                    () => cal.SettleWindowMs, v => { cal.SettleWindowMs = (int)v; _ctx.ApplyAndSave(); }),
            ]),
            new PageSpec([
                new LabelSpec(StatusText),
                new ButtonSpec("cal_run", "Calibrate Now",
                    () => { _ctx.Close(); _ctx.Messenger.Send(new CalibrationRequestedMessage()); },
                    ButtonKind.Default, Width: 300, Height: 80),
                new ButtonSpec("cal_clear", "Clear Calibration",
                    () => _ctx.ShowModal("Clear saved calibration?", () =>
                    {
                        _ctx.Messenger.Send(new CalibrationClearRequestedMessage());
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }

    private string StatusText()
    {
        var data = _ctx.Calibration.CurrentData;
        if (data != null)
        {
            return $"Last calibration: {data.CalibratedAt.LocalDateTime:g}  \u00B7  "
                + $"{data.ComputeMeanErrorPixels():F1} px ({data.ComputeQualityPercent():F1}%)";
        }
        var rejection = _ctx.Calibration.LastLoadRejection;
        return rejection == null ? "Last calibration: none" : $"Last calibration: {rejection}";
    }
}
```

- [ ] **Step 2: Wire + forward in the shell + delete the dead helper**

`Close` is already on `SettingsPageContext` (Task 1, wired in Task 2 as `close: Close`), so the source's `cal_run` action compiles unchanged. Add the `_calibrationSource` field, the ctor assignment (`_calibrationSource = new CalibrationPageSource(_ctx);` — in the same spot as `_cursorSource`, before `BuildTabButtons()`), and the forwarder:

```csharp
    private List<List<Action>> BuildCalibrationPages() => _calibrationSource.Build().Select(RenderPage).ToList();
```

Then **delete the now-dead `CalibrationStatusText` private method** from `SettingsWindow.xaml.cs` — it moved into the source as `StatusText`.

- [ ] **Step 3: Write the test**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/CalibrationPageSourceTests.cs`. `CalibrationService(ILogger<CalibrationService>? logger = null)` is default-constructible (`CalibrationService.cs:37`), so `TestSettingsPageContext.Build()` supplies one with `CurrentData == null` → status reads "none":

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class CalibrationPageSourceTests
{
    [Fact]
    public void Calibration_TwoPages_StatusLabelThenRunAndClear()
    {
        var pages = new CalibrationPageSource(TestSettingsPageContext.Build()).Build();
        Check.That(pages).HasSize(2);
        Check.That(pages[1].Rows[0]).IsInstanceOf<LabelSpec>();
        var buttons = pages[1].Rows.OfType<ButtonSpec>().Select(b => b.Id);
        Check.That(buttons).Contains(new[] { "cal_run", "cal_clear" });
    }

    [Fact]
    public void StatusText_None_WhenNoCalibration()
    {
        var src = new CalibrationPageSource(TestSettingsPageContext.Build());
        var label = src.Build()[1].Rows[0];
        Check.That(((LabelSpec)label).Text()).IsEqualTo("Last calibration: none");
    }
}
```

- [ ] **Step 4: Run tests + full project, then commit**

Run: `dotnet test tests/EyeCtrl.App.Tests` → Expected: PASS.
```bash
git add -A && git commit -m "refactor(settings): extract Calibration builder (status label) to page source"
```

---

## Task 7: Migrate the Models tab (dynamic registry + async downloads)

This tab is **dynamic** (page content depends on the live model registry) and owns two async download flows. The download orchestration moves **into the source**, using `_ctx.Invoke` (Dispatcher) for the completion callback — removing ~60 lines and the `ShowError`/`FormatSize` helpers from the shell.

**Files:**
- Create: `Sources/ModelsPageSource.cs`
- Modify: `SettingsWindow.xaml.cs` (forwarder; delete `FormatSize`, `DownloadBundledModelAsync`, `DownloadCustomModelFromClipboard`, `ShowError` if now unused — `ShowError` is re-exposed via context, so keep the shell's `ShowError` method as the context delegate target; delete only the two download methods + `FormatSize`)
- Test: `ModelsPageSourceTests.cs`

- [ ] **Step 1: Create ModelsPageSource** (from `BuildModelsPages` + `FormatSize` + downloads, ~850–1018)

```csharp
namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Services;

internal sealed class ModelsPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public ModelsPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var page1 = new List<SettingsRowSpec>();
        var downloaded = _ctx.Registry.SelectableModels;

        if (downloaded.Count == 0)
        {
            page1.Add(new LabelSpec(() => "No models downloaded. Download one below."));
        }
        else
        {
            var display = downloaded.Select(m => m.DisplayName).ToArray();
            var ids = downloaded.Select(m => m.Id).ToArray();
            page1.Add(new ComboBoxSpec("md_active", "Active Model",
                display.Prepend("Off").ToArray(), ids.Prepend("Off").ToArray(),
                () => _ctx.Registry.ActiveModelId ?? "Off",
                v => { _ctx.ModelManagement.SetActiveModel(v); _ctx.ApplyAndSave(); }));
        }

        page1.Add(new LabelSpec(() => new string('\u2500', 50)));

        foreach (var entry in _ctx.Registry.Registry.Values.OrderBy(e => e.SizeBytes))
        {
            if (_ctx.Registry.IsModelDownloaded(entry.Id))
            {
                var e1 = entry;
                page1.Add(new LabelSpec(() => $"  {e1.DisplayName}  {FormatSize(e1.SizeBytes)}  \u2713 Downloaded"));
            }
            else
            {
                var e2 = entry;
                page1.Add(new ButtonSpec($"md_dl_{entry.Id}",
                    $"{entry.DisplayName}  {FormatSize(entry.SizeBytes)}  [Download]",
                    () =>
                    {
                        if (_ctx.ModelManagement.IsDownloading) return;
                        _ctx.ShowModal($"Download {e2.DisplayName} ({FormatSize(e2.SizeBytes)})?",
                            () => _ = DownloadBundledAsync(e2.Id, e2.Url, e2.Sha256, e2.TokenizerUrl, e2.TokenizerSha256));
                    },
                    ButtonKind.Default, Width: 400, Height: 60));
            }
        }

        page1.Add(new LabelSpec(() => new string('\u2500', 50)));
        page1.Add(new ButtonSpec("md_add", "\uff0b Add Custom Model (paste URL)",
            DownloadCustomFromClipboard, ButtonKind.Default, Width: 300, Height: 60));

        var manageable = _ctx.Registry.DownloadedModels;
        if (manageable.Count > 0)
        {
            page1.Add(new ButtonSpec("md_manage", "Manage models...",
                () => _ctx.ViewModel.Settings.NextPage(), ButtonKind.Default, Width: 300, Height: 60));

            var page2 = new List<SettingsRowSpec>();
            foreach (var entry in manageable)
            {
                var e3 = entry;
                page2.Add(new ButtonSpec($"md_del_{entry.Id}",
                    $"{entry.DisplayName}  {FormatSize(entry.SizeBytes)}  \uD83D\uDDD1 Delete",
                    () => _ctx.ShowModal($"Delete {e3.DisplayName}? This cannot be undone.", () =>
                    {
                        _ctx.ModelManagement.DeleteModel(e3.Id);
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Delete, Width: 400, Height: 60));
            }
            page2.Add(new ButtonSpec("md_back", "Back to models",
                () => _ctx.ViewModel.Settings.PreviousPage(), ButtonKind.Default, Width: 300, Height: 60));
            return [new PageSpec(page1), new PageSpec(page2)];
        }

        return [new PageSpec(page1)];
    }

    private async Task DownloadBundledAsync(string id, string url, string sha256, string tokUrl, string tokSha256)
    {
        try
        {
            var result = await _ctx.ModelManagement.DownloadBundledModelAsync(id, url, sha256, tokUrl, tokSha256);
            _ctx.Invoke(() =>
            {
                if (!result.IsSuccess) _ctx.ShowError(result.Error ?? "Download failed.");
                _ctx.RebuildContent();
            });
        }
        catch (Exception ex)
        {
            _ctx.Invoke(() =>
            {
                _ctx.ShowError($"Download failed: {ex.Message}");
                _ctx.RebuildContent();
            });
        }
    }

    private void DownloadCustomFromClipboard()
    {
        var url = (_ctx.ReadClipboard() ?? "").Trim();
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://"))
        {
            _ctx.ShowError("Copy a valid HTTPS model URL to clipboard first, then dwell this button.");
            return;
        }
        if (_ctx.ModelManagement.IsDownloading)
        {
            _ctx.ShowError("Another download is already in progress.");
            return;
        }
        var displayName = ModelManagementService.DeriveModelName(url);
        _ctx.ShowModal($"Download custom model \"{displayName}\" from clipboard URL?", () =>
        {
            _ = Task.Run(async () =>
            {
                var result = await _ctx.ModelManagement.DownloadCustomModelAsync(url);
                _ctx.Invoke(() =>
                {
                    if (!result.IsSuccess) _ctx.ShowError(result.Error ?? "Download failed.");
                    else _ctx.ApplyAndSave();
                    _ctx.RebuildContent();
                });
            });
        });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576:F0} MB";
        if (bytes >= 1024) return $"{bytes / 1024:F0} KB";
        return $"{bytes} B";
    }
}
```

- [ ] **Step 2 (7a): Shell cleanup + forwarder + repoint the separator fit-test**

Add `_modelsSource` field + ctor assignment + forwarder `=> _modelsSource.Build().Select(RenderPage).ToList()`. Delete from `SettingsWindow.xaml.cs`: `FormatSize`, `DownloadBundledModelAsync`, `DownloadCustomModelFromClipboard`. **Keep** `ShowError` (the context delegate `showError: ShowError` still points at it).

**Repoint `SettingsContentFitTests.TheWidestSeparatorLabel_FitsTheContentColumn` (B2 — the fourth grep-test):** the two `new string('\u2500', 50)` separators moved from `SettingsWindow.xaml.cs` into `ModelsPageSource.cs`, so the test's source-read now matches zero lines and fails its `Check.That(runs).Not.IsEmpty()`. Change the path it reads — at `SettingsContentFitTests.cs:365` — from `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` to `src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs`, and update the test's doc comment / failure message to name `ModelsPageSource` instead of `SettingsWindow.xaml.cs`. The measurement logic (building a `DwellLabel` and asserting it does not drag the content-column layout) is unchanged — only the file it reads the run-length from moves. The test's own failure message sanctions this repoint ("if they merely moved or changed character, repoint the pattern").

- [ ] **Step 3 (7a): Smoke test + commit 7a**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/ModelsPageSourceTests.cs` with a smoke test only for now (7b adds the real assertions once the fakes exist). The empty-registry fake comes from Step 4:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class ModelsPageSourceTests
{
    [Fact]
    public void Build_EmptyRegistry_DoesNotThrow_AndHasAddButton()
    {
        var ctx = TestSettingsPageContext.Build(
            registry: ModelTestFakes.EmptyRegistry(),
            modelManagement: ModelTestFakes.NoOpManagement());
        var rows = new ModelsPageSource(ctx).Build().Single().Rows;

        Check.That(rows.OfType<LabelSpec>().First().Text()).Contains("No models downloaded");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Contains("md_add");
    }
}
```

Run: `dotnet test tests/EyeCtrl.App.Tests` → Expected: PASS.
```bash
git add -A && git commit -m "refactor(settings): extract Models builder (downloads, clipboard via ctx); repoint separator fit-test"
```

- [ ] **Step 4 (7b): Write `ModelTestFakes` from scratch (they do NOT exist on main)**

The fakes are NOT reusable from elsewhere — `TestFakes.cs`/`ModelTestFakes` exist only on the unmerged `refactor/eventcoordinator-decomposition` branch, not on `main` (verified: no matches under `tests/`). Create `tests/EyeCtrl.App.Tests/Settings/Pages/ModelTestFakes.cs`. `ModelRegistryService` lives in **`EyeCtrl.Core.Prediction`** (not `EyeCtrl.App.Services`) with ctor `(string modelsDirectory, ModelManifest bundledManifest, EyeCtrlSettings settings, ILogger?)` (`ModelRegistryService.cs:99`), and `ModelManagementService` (in `EyeCtrl.App.Services`) takes `(ModelRegistryService, ModelDownloadService, EyeCtrlSettings, ILogger?)`. **Consult `tests/EyeCtrl.App.Tests/Services/ModelManagementServiceTests.cs` for the exact `ModelManifest`/`ModelDownloadService` construction** and mirror it against a unique temp dir (so parallel test runs don't collide — see AGENTS.md on unique temp paths):

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

internal static class ModelTestFakes
{
    // Mirror the construction shape from ModelManagementServiceTests.cs (adapt the ModelManifest /
    // ModelDownloadService args to whatever that file uses). A unique temp dir per call avoids the
    // machine-global contention AGENTS.md warns about for named resources.
    public static ModelRegistryService EmptyRegistry()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eyectrl_models_test_" + System.Guid.NewGuid());
        // Empty bundled manifest — exact shape per ModelManagementServiceTests.
        var manifest = new ModelManifest();   // adjust: Models = new() if required by its ctor/init
        return new ModelRegistryService(dir, manifest, new EyeCtrlSettings(), logger: null);
    }

    public static ModelManagementService NoOpManagement(ModelRegistryService? registry = null)
    {
        registry ??= EmptyRegistry();
        return new ModelManagementService(registry, new ModelDownloadService(), new EyeCtrlSettings(), logger: null);
    }
}
```

- [ ] **Step 5 (7b): Add real assertions + run + commit**

Extend `ModelsPageSourceTests` with a second test asserting the downloaded-state branch renders the active-model combo and a delete page (construct a registry that reports one downloaded model — follow `ModelManagementServiceTests`'s setup for placing a model file). If wiring a "downloaded" model into the fake proves disproportionate, leave the Step-3 smoke test as the coverage and record that decision in the test's doc comment. Do NOT delete the smoke test.

Run: `dotnet test tests/EyeCtrl.App.Tests` → Expected: PASS.
```bash
git add -A && git commit -m "test(settings): add ModelsPageSource registry-state fakes + assertions"
```

---

## Task 8: Migrate the Quick Actions tab (sub-view state machine + pickers)

The largest source. Owns the `QaView` state machine, four picker kinds, and pagination via `ChunkPresets`. The shell's `OnSettingsViewModelPropertyChanged` (CurrentTabName) handler must reset the source's view on tab switch.

**Files:**
- Create: `Sources/QuickActionsPageSource.cs` (includes `QaView` enum, `QaTypeGlyph`, `ChunkPresets`, the QA consts)
- Modify: `SettingsWindow.xaml.cs` (forwarder; reset wiring; delete the QA cluster ~1098–1256)
- Test: `QuickActionsPageSourceTests.cs`

- [ ] **Step 1: Create QuickActionsPageSource** (transcribed from ~1098–1256)

```csharp
namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Models;

internal sealed class QuickActionsPageSource : ISettingsPageSource
{
    private const int QaActionsPerPage = 4;
    private const int QaPresetsPerPage = 6;

    // internal so the test can reference QuickActionsPageSource.QaView.PickShortcut when calling GoTo.
    internal enum QaView { List, PickType, PickShortcut, PickMedia, PickApp, PickWindowAction }

    private readonly SettingsPageContext _ctx;
    private QaView _view = QaView.List;

    public QuickActionsPageSource(SettingsPageContext ctx) => _ctx = ctx;

    /// <summary>Called by the shell when the user switches tabs, so re-entering QA lands on the list.</summary>
    public void ResetView()
    {
        _view = QaView.List;
    }

    // internal (not private) so the page-source test can drive the state machine directly instead of
    // reflecting into it (reflection on an enum-typed arg throws ArgumentException — Int32 does not
    // widen to a boxed enum). EyeCtrl.App.Tests is InternalsVisibleTo from EyeCtrl.App.
    internal void GoTo(QaView v)
    {
        _view = v;
        _ctx.RebuildContent();
    }

    public IReadOnlyList<PageSpec> Build() => _view switch
    {
        QaView.PickType => TypePicker(),
        QaView.PickShortcut => PresetPicker("qa_ks", Core.Models.QuickActionPresets.KeyboardShortcuts, _ctx.ViewModel.QuickActionsEditor.AddKeyboardShortcut),
        QaView.PickMedia => PresetPicker("qa_md", Core.Models.QuickActionPresets.MediaControls, _ctx.ViewModel.QuickActionsEditor.AddMediaControl),
        QaView.PickWindowAction => PresetPicker("qa_win", Core.Models.QuickActionPresets.WindowActions, _ctx.ViewModel.QuickActionsEditor.AddWindowAction),
        QaView.PickApp => AppPicker(),
        _ => List(),
    };

    private IReadOnlyList<PageSpec> List()
    {
        var qa = _ctx.Settings.QuickActions;
        var editor = _ctx.ViewModel.QuickActionsEditor;
        var pages = new List<PageSpec>();

        pages.Add(new PageSpec([
            new ToggleSpec("qa_enabled", "Enable Quick Actions", () => qa.Enabled, v => { qa.Enabled = v; _ctx.ApplyAndSave(); }),
            new ComboBoxSpec("qa_edge", "Summon Edge", new[] { "Left", "Right", "Top", "Bottom" }, null, () => qa.SummonEdge, v => { qa.SummonEdge = v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_summon", "Summon Threshold (ms)", 200, 2000, 50, 0, () => qa.SummonThresholdMs, v => { qa.SummonThresholdMs = (int)v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_dismiss", "Dismiss Timeout (ms)", 1000, 15000, 500, 0, () => qa.DismissTimeoutMs, v => { qa.DismissTimeoutMs = (int)v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_columns", "Grid Columns", 2, 5, 1, 0, () => qa.GridColumns, v => { qa.GridColumns = (int)v; _ctx.ApplyAndSave(); }),
            new ButtonSpec("qa_add", "\uFF0B  Add action", () => GoTo(QaView.PickType), ButtonKind.Default, Width: 360, Height: 80),
        ]));

        var actions = editor.Actions.ToList();
        for (int i = 0; i < actions.Count; i += QaActionsPerPage)
        {
            var chunk = actions.Skip(i).Take(QaActionsPerPage).ToList();
            var rows = new List<SettingsRowSpec>();
            foreach (var a in chunk)
            {
                var captured = a;
                var caption = $"{Glyph(a.Type)}  {a.Label}";
                rows.Add(new ToggleSpec("qa_en_" + a.Id, caption, () => captured.Enabled, _ => editor.ToggleEnabled(captured.Id)));
                rows.Add(new ButtonSpec("qa_del_" + a.Id, "\u2715  Delete",
                    () => _ctx.ShowModal($"Delete \"{captured.Label}\"?", () => { editor.DeleteAction(captured.Id); _ctx.RebuildContent(); }),
                    ButtonKind.Delete, Width: 360, Height: 70));
            }
            pages.Add(new PageSpec(rows));
        }
        return pages;
    }

    private IReadOnlyList<PageSpec> TypePicker() =>
    [
        new PageSpec([
            .. BackRow(0),
            new ButtonSpec("qa_type_kbd", "\u2328  Keyboard Shortcut", () => GoTo(QaView.PickShortcut), ButtonKind.Default, Width: 420, Height: 90),
            new ButtonSpec("qa_type_media", "\u266B  Media Control", () => GoTo(QaView.PickMedia), ButtonKind.Default, Width: 420, Height: 90),
            new ButtonSpec("qa_type_app", "\u25A6  Launch App", () => GoTo(QaView.PickApp), ButtonKind.Default, Width: 420, Height: 90),
            new ButtonSpec("qa_type_window", "\u25A3  Window Action", () => GoTo(QaView.PickWindowAction), ButtonKind.Default, Width: 420, Height: 90),
        ]),
    ];

    private IReadOnlyList<PageSpec> PresetPicker(string idPrefix,
        IReadOnlyList<(string Label, string Payload)> presets, Action<string, string> add) =>
        ChunkPresets(presets.Count, idPrefix, i =>
        {
            var (label, payload) = presets[i];
            return (label, () => { add(label, payload); GoTo(QaView.List); });
        });

    private IReadOnlyList<PageSpec> AppPicker()
    {
        var editor = _ctx.ViewModel.QuickActionsEditor;
        var apps = editor.InstalledApps;
        if (apps.Count == 0)
        {
            return
            [
                new PageSpec([
                    new LabelSpec(() => "No installed apps found."),
                    BackButton(0),
                ]),
            ];
        }
        return ChunkPresets(apps.Count, "qa_app", i =>
        {
            var app = apps[i];
            return (app.DisplayName, () => { editor.AddApp(app); GoTo(QaView.List); });
        });
    }

    private IReadOnlyList<PageSpec> ChunkPresets(int count, string idPrefix, Func<int, (string Label, Action OnPick)> itemAt)
    {
        var pages = new List<PageSpec>();
        for (int start = 0, page = 0; start < count; start += QaPresetsPerPage, page++)
        {
            // qa_back_{page}: preserve the original per-page back-button id (QaBackButton(page)).
            var rows = BackRow(page);
            for (int i = start; i < Math.Min(start + QaPresetsPerPage, count); i++)
            {
                var (label, onPick) = itemAt(i);
                var id = $"{idPrefix}_{i}";
                rows.Add(new ButtonSpec(id, label, onPick, ButtonKind.Default,
                    Width: SettingsWindow.PickerButtonWidth, Height: 80));
            }
            pages.Add(new PageSpec(rows));
        }
        return pages;
    }

    private static List<SettingsRowSpec> BackRow(int page) => new() { BackButton(page) };
    private ButtonSpec BackButton(int page) => new("qa_back_" + page, "\u2190  Back",
        () => GoTo(QaView.List), ButtonKind.Default, Width: 360, Height: 70);

    private static string Glyph(QuickActionType t) => t switch
    {
        QuickActionType.KeyboardShortcut => "\u2328",
        QuickActionType.CannedText => "T",
        QuickActionType.LaunchExe => "\u229E",
        QuickActionType.LaunchWindowsApp => "\u25A6",
        QuickActionType.LaunchUrl => "@",
        QuickActionType.MediaControl => "\u266B",
        QuickActionType.WindowAction => "\u25A3",
        _ => "\u26A1",
    };
}
```

> **Glyph fidelity:** the original `QaTypeGlyph` used specific BMP chars (`⊞` `\u229E`, `▦` `\u25A6`, etc.). The literals above match. Double-check each against the original at review.

- [ ] **Step 2: Shell — field, ctor, forwarder, tab-switch reset, delete QA cluster**

Add field `private readonly QuickActionsPageSource _qaSource;` (no `= null!`), ctor assignment `_qaSource = new QuickActionsPageSource(_ctx);` (same spot as the other sources, before `BuildTabButtons()`), forwarder:
```csharp
    private List<List<Action>> BuildQuickActionsPages() => _qaSource.Build().Select(RenderPage).ToList();
```
In `OnSettingsViewModelPropertyChanged`, the `CurrentTabName` case currently does `_qaView = QaView.List; RebuildContent();`. Replace with `_qaSource.ResetView(); RebuildContent();` and delete the `_qaView` field + the `QaView` enum + `GoToQaView` method + the entire QA builder cluster (`QaActionsPerPage`, `QaPresetsPerPage`, `QaTypeGlyph`, `BuildQuickActionsPages` old body, `BuildQaListPages`, `QaBackButton`, `BuildQaTypePickerPages`, `BuildQaPresetPages`, `BuildQaShortcutPages`, `BuildQaMediaPages`, `BuildQaWindowActionPages`, `BuildQaAppPages`, `ChunkPickerPages`). Also delete the now-unused `_qaView`/`QaView` declaration near line 126. Update the now-stale comment at `SettingsContentFitTests.cs:187` (`ChunkPickerPages` → `QuickActionsPageSource`'s `ChunkPresets`).

- [ ] **Step 3: Write the test (drive the state machine directly, not by reflection)**

`GoTo` and `QaView` are `internal` (Step 1), so the test calls them directly — no reflection (which would throw, since `Invoke` won't widen a boxed `Int32` to the `QaView` enum). The QA ViewModel fake is NOT reusable from elsewhere on `main` (no `ViewModelFakes`/`TestFakes` exist — verified); build it by mirroring `SettingsWindowViewModelTests`' setup.

Create `tests/EyeCtrl.App.Tests/Settings/Pages/QuickActionsPageSourceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using CommunityToolkit.Mvvm.Messaging;
using NFluent;
using Xunit;

public class QuickActionsPageSourceTests
{
    private static SettingsPageContext Ctx() => TestSettingsPageContext.Build(viewModel: MakeQaViewModel());

    // Mirrors SettingsWindowViewModelTests.CreateViewModel() (ViewModel.Tests:23-29). The two fakes
    // are private nested there, so they are re-declared here — they are 3-line stubs.
    private static SettingsWindowViewModel MakeQaViewModel()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var settingsService = new SettingsService("test_qa_source_" + System.Guid.NewGuid() + ".json");
        var settingsVM = new SettingsViewModel(settings, settingsService, messenger);
        var editor = new SettingsQuickActionsEditorViewModel(
            new FakeQuickActionsStore(), new FakeInstalledAppProvider());
        var vm = new SettingsWindowViewModel(settingsVM, editor, messenger, settings.Dwell);
        editor.Load();   // production calls Load() right after constructing the editor
        return vm;
    }

    private sealed class FakeQuickActionsStore : IQuickActionsStore
    {
        private readonly List<QuickAction> _actions = new();
        public IReadOnlyList<QuickAction> LoadAll() => _actions;
        public void Save(IReadOnlyList<QuickAction> actions) { _actions.Clear(); _actions.AddRange(actions); }
        public QuickAction? FindById(string id) => _actions.FirstOrDefault(a => a.Id == id);
    }

    private sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }

    [Fact]
    public void ListPage_HasConfigControlsAndAddButton()
    {
        var rows = new QuickActionsPageSource(Ctx()).Build().First().Rows;
        Check.That(rows.OfType<ToggleSpec>().First().Id).IsEqualTo("qa_enabled");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Contains("qa_add");
    }

    [Fact]
    public void ShortcutPicker_ButtonsUsePickerButtonWidth()
    {
        var src = new QuickActionsPageSource(Ctx());
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);   // direct call — GoTo/QaView are internal

        var preset = src.Build().First().Rows.OfType<ButtonSpec>().First(b => !b.Id.StartsWith("qa_back"));
        Check.That(preset.Width).IsEqualTo(SettingsWindow.PickerButtonWidth);
    }

    [Fact]
    public void ShortcutPicker_BackButtonHasPerPageId()
    {
        var src = new QuickActionsPageSource(Ctx());
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);
        var back = src.Build().First().Rows.OfType<ButtonSpec>().First(b => b.Id.StartsWith("qa_back"));
        Check.That(back.Id).IsEqualTo("qa_back_0");   // R5: per-page id preserved, not a constant "qa_back"
    }
}
```

- [ ] **Step 4: Run tests + full project**

Run: `dotnet test tests/EyeCtrl.App.Tests` → Expected: PASS.
Run: `dotnet test tests/EyeCtrl.ViewModels.Tests` → Expected: PASS (confirms `SettingsWindowViewModelTests.Tabs.HasSize(13)` still green).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "refactor(settings): extract Quick Actions builder (state machine + pickers) to page source"
```

---

## Task 9: Collapse the bridge, then docs + full verification

All 11 tabs now route through page sources via the Phase-1 `RenderPage` shim. **This task deletes the shim** so the spec is the canonical form — the change that makes "less throwaway than plain A" true rather than asserted (R1). Then docs + verification.

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (collapse: `_pages` type, `_tabDefs`/`_pageBuilders` element type, the 11 forwarders, `RenderCurrentPage`; delete `RenderPage`)
- Modify: `docs/SETTINGS_WINDOW_REFACTOR_PLAN.md` (mark superseded)
- Modify: `ARCHITECTURE.md` §4.10 (repo root, not `docs/`)

- [ ] **Step 1: Collapse `_pages` from `List<List<Action>>` to `IReadOnlyList<PageSpec>`**

In `SettingsWindow.xaml.cs`:

1. The `_pages` field: `private List<List<Action>> _pages = new();` → `private IReadOnlyList<PageSpec> _pages = Array.Empty<PageSpec>();`
2. The `_tabDefs` tuple type and `_pageBuilders` dictionary: change the builder type `Func<List<List<Action>>>` → `Func<IReadOnlyList<PageSpec>>` in both the `_tabDefs` array element type and the `_pageBuilders` dictionary value type.
3. `BuildPages()`: change its return type from `List<List<Action>>` to `IReadOnlyList<PageSpec>` (body unchanged — it already returns `_pageBuilders[...].Invoke()`).
4. The 11 forwarders — each loses the `.Select(RenderPage).ToList()` and changes return type, e.g.:
```csharp
    private IReadOnlyList<PageSpec> BuildCursorPages() => _cursorSource.Build();
```
   (apply the same `IReadOnlyList<PageSpec>` return-type + bare `=> _xxxSource.Build();` to all 11).
5. `RenderCurrentPage` — change the inner loop from invoking Actions to rendering rows:
```csharp
        var currentPage = _viewModel.Settings.CurrentPage;
        if (currentPage < _pages.Count)
        {
            foreach (var row in _pages[currentPage].Rows)
                RenderSpec(row);
        }
```
6. **Delete `RenderPage`** (the `List<Action>` shim) — it is now unreferenced.

> The grep-tests stay green: forwarder method *names* are unchanged (still bare identifiers → `\w+` matches), `_tabDefs` ids are unchanged, `Add(DwellControl)` is untouched. Only the builders' *return type* changed, which none of the four grep-tests inspect.

- [ ] **Step 2: Build + run the four grep-tests + all page-source tests**

Run: `dotnet build EyeCtrl.slnx` → Expected: `0 Fehler`.
Run: `dotnet test tests/EyeCtrl.App.Tests` → Expected: PASS (confirms the collapse preserved behaviour and the four grep-tests survive).

- [ ] **Step 3: Confirm the shell shrank and the shim is gone**

Run:
```bash
wc -l src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
grep -c "Source.Build();"  src/EyeCtrl.App/Views/SettingsWindow.xaml.cs   # expect 11 forwarders (each ends "=> _xxxSource.Build();")
grep -n "RenderPage\b"     src/EyeCtrl.App/Views/SettingsWindow.xaml.cs   # expect ZERO (shim deleted)
grep -n "BuildQaListPages\|ChunkPickerPages\|DownloadBundledModelAsync\|CalibrationStatusText\|FormatSize\|_qaView" src/EyeCtrl.App/Views/SettingsWindow.xaml.cs   # expect: only comments, if anything
```
Expected: shell ~800–900 lines; 11 forwarders; `RenderPage` gone; no dead QA/download/calibration helpers.

- [ ] **Step 4: Run the architecture gate**

Run: `dotnet test tests/EyeCtrl.Architecture.Tests`
Expected: PASS (green as on main; confirms no cross-project violation introduced).

- [ ] **Step 5: Run the full suite + detect_changes**

Run: `dotnet test --filter "Category!=UI"` → Expected: all PASS.
Run (GitNexus MCP): `detect_changes({scope: "compare", base_ref: "main"})`
Expected: only symbols in `SettingsWindow.xaml.cs` + the new `Views/Settings/` files affected; no surprises in `EventCoordinator`, the ViewModels, or Core.

- [ ] **Step 6: Update docs**

- `docs/SETTINGS_WINDOW_REFACTOR_PLAN.md`: prepend `> **STATUS: Superseded (2026-08-12).**` — its Phase 1 (custom controls) shipped earlier as `EyeCtrl.Controls/Dwell*.cs`; its Phase 2 (page builders) shipped as declarative page-specs in `Views/Settings/` and now render through a single `RenderSpec` (the Action-list shim collapsed). Full XAML/MVVM remains a possible future step.
- `ARCHITECTURE.md` §4.10 (at the **repo root**, not `docs/`): update the "Each tab has a `BuildXxxPages` process …" bullet to note the builders now live in `Views/Settings/Sources/*PageSource.cs` as declarative `ISettingsPageSource` specs rendered by `SettingsWindow.RenderSpec`, with `_pages` holding `PageSpec` directly.

- [ ] **Step 7: Commit + open a PR**

```bash
git add -A && git commit -m "refactor(settings): collapse the spec→Action bridge; _pages is List<PageSpec>"
git add docs/ && git commit -m "docs(settings): mark SETTINGS_WINDOW_REFACTOR_PLAN superseded; update ARCHITECTURE §4.10"
```
Report the before/after line count for `SettingsWindow.xaml.cs` (1,630 → target ~850) and the 11 new source files' sizes. Open a PR off the worktree branch targeting `main`, describing the declarative-spec approach, the two-phase-then-collapse MVVM bridge, and the preserved construction signature / four grep-tests.

---

## Self-Review Notes (read before executing)

- **Spec coverage:** all 11 tabs (`_tabDefs`: Cursor, Dwell, Selection, Actions, Layout, Keyboard, Models, Toolbar, Calibration, General, QuickActions) → Tasks 2–8. ✓
- **Behaviour preservation:** every getter/setter, range, step, decimals, modal message, reset-to-defaults field list, button width/height/kind, and the two color-picker radii (Cursor=swatch 35, Selection=control 50) are transcribed verbatim. `cal_run` closes the window via `_ctx.Close()` before sending `CalibrationRequestedMessage` (Task 6 Step 1 — no spurious `CurrentPage = 0`). The QA tab-switch reset and per-page `qa_back_{page}` ids are preserved (Task 8).
- **Test-coupling invariants:** FOUR grep-tests read `SettingsWindow.xaml.cs` as text. Three survive untouched (`_tabDefs` bare-identifier forwarders, `Add(DwellControl)`, the `internal` consts); the fourth — `TheWidestSeparatorLabel_FitsTheContentColumn` — is repointed at `ModelsPageSource.cs` in Task 7 Step 2 (its separators moved). Run the fit/drift suites after every task.
- **Type consistency:** `RenderSpec` switches on the six records from Task 1; `ButtonKind {Default,Reset,Delete}` maps via the extracted `FillFor` (Task 2, tested by `SettingsRenderMappingTests` since Reset/Delete fills are visually identical). `SettingsPageContext` (15 args incl. `ReadClipboard`) is defined once in Task 1; all tests build it through the **single** `TestSettingsPageContext.Build()` factory (Task 2) — no per-file `Ctx()` copy-drift.
- **Two-phase then collapse (R1):** Tasks 2–8 keep `_pages = List<List<Action>>` for per-tab incrementality; **Task 9 collapses** it to `IReadOnlyList<PageSpec>` and deletes `RenderPage`. This is what makes the spec canonical and the "MVVM bridge" claim honest — without Task 9 the plan ships plain-A's throwaway plus a spec layer.
- **Review round 1 applied:** B1 (capitalize `Width:`/`Height:` at ButtonSpec sites), B2 (repoint the separator fit-test), B3 (`TypePicker` collection-expression spread), B4 (`QaView`/`GoTo` internal — test calls directly, no reflection), B5 (`cal_run` uses `_ctx.Close()`, no `CurrentPage=0`), B6 (model fakes + QA-VM fake written from scratch — none exist on `main`), R1 (Task 9 collapse), R3 (`ReadClipboard` delegate — no `System.Windows.Clipboard` in the source), R4 (extracted+tested `FillFor`), R5 (`qa_back_{page}` ids restored), R7 (ctor ordering before `ShowTab`), plus N1/N2/N3/N4/N5/N7/N8/N10.
- **Known soft spots (honest):** (a) Task 7's `ModelTestFakes` mirrors `ModelManagementServiceTests` — the exact `ModelManifest`/`ModelDownloadService` ctor args must be confirmed against that file at execution time. (b) `SettingsViewModel.Tabs` (the 13-tab declarative model) is **vestigial** and intentionally NOT merged with these specs — `SettingsTabDriftTests` documents the two drift independently; reconciling them is a separate effort. (c) `using`-directive iterations are expected (Global Constraints); the per-task `dotnet build` surfaces them.
