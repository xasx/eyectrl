# Settings Dwell-Target Polish + Automatic Pagination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Status:** The isolated worktree is **already created** at `.worktrees/settings-dwell-sizing` on branch `polish/settings-dwell-sizing` (off `main` @ `c61fb198`), and this plan lives inside it. **Begin execution at Task 1, Step 3** (build + baseline tests) — skip Steps 1–2 (worktree creation). Do not re-run `git worktree add`.
>
> **Rev 2 (2026-08-14):** incorporates the plan review (verdict: approve-with-changes). Changes: new **Task 2 = automatic height-based pagination** (the review's Major 2 — the user mandated "the solution must be automatic paging"); all `dotnet test` commands on `EyeCtrl.App.Tests` now carry `--filter "Category!=UI"` (Major 1); the picker's 448-DIP row and the now-false composite-budget comment at `SettingsWindow.xaml.cs:695-700` are documented and rewritten (Major 3); the `InternalsVisibleTo` rationale is corrected (Minor 1 — 11 declarations exist, the old text came from stale AGENTS.md); the Supervisor setup window's `DwellToggle` gap change is recorded (Minor 2); combo options' residual hit-circle overlap is recorded as accepted (Minor 3); `DwellSelectionKeyboardToolbarPageSourceTests` added to the affected-test list (Minor 4); doc-comment and tautology-comment nits folded into Task 5. The polish values are **un-trimmed** — pagination absorbs the height.

**Goal:** Make Settings-window dwell controls larger and better-spaced for eye-gaze + dwell input — the user's only input channel — by (1) making settings pages **automatically re-paginate to fit the available content height** so no setting can ever be clipped out of reach, and (2) fixing crowded heading→control spacing plus two under-sized targets (colour swatches, combo options).

**Architecture:** Task 2 adds a pure, headlessly-testable chunker (`SettingsAutoPagination.ChunkByHeight`) plus a real-WPF row measurer, wired into `RebuildContent()` behind a per-source opt-in (`PrefersAutoPagination`); it re-splits a tab's rows into `PageSpec`s that fit `ContentAvailableHeight`, so the existing pagination machinery (`TotalPages`, prev/next, bar) is untouched. Tasks 3–5 are surgical edits to the shared `EyeCtrl.Controls` dwell composites plus two `SettingsWindow` constants; the recent SettingsWindow decomposition stores no dimensions in the spec, so control-internal literals remain the correct edit surface.

**Tech Stack:** .NET 10.0.102 SDK, WPF (`net10.0-windows`), CommunityToolkit.Mvvm 8.4.0, xUnit v3 (`xunit.v3` 3.2.2) + NFluent 3.1.0. Solution is `.slnx`. Tests are stand-alone exes; global usings `Xunit` + `NFluent` come from `tests/Directory.Build.props`.

## Global Constraints

Copied from repo conventions (AGENTS.md) and the review — every task implicitly obeys these:

- **Layout uses literal DIPs, not tokens.** There is deliberately no spacing/padding/control-size token scale. Edit `Margin`/`Padding`/`Height`/`Width` as literal doubles; tokens only for colour/corner-radius/font/border-thickness.
- **NFluent 3.1.0 for assertions.** Prefer native checks (`IsStrictlyGreaterThan`, `IsGreaterOrEqualThan`, `IsLessOrEqualThan`). No `Assert.*`.
- **`EyeCtrl.App.Tests` runs must filter out UI tests** — `--filter "Category!=UI"` — or FlaUI suites (AppLaunch/Keyboard/Settings/Toolbar tests, `Category=UI`) fail headless (AGENTS.md:19, review Major 1). `EyeCtrl.Controls.Tests` and `EyeCtrl.ViewModels.Tests` have no UI category; run them unfiltered.
- **Internals (corrected per review Minor 1):** 11 `InternalsVisibleTo` declarations exist across the csprojs. `EyeCtrl.Controls`'s IVT targets `EyeCtrl.Controls.Tests` **only**, and the swatch-geometry test also needs `SettingsWindow`'s internals — so that test must live in `EyeCtrl.App.Tests` (which has IVT from `EyeCtrl.App`), and `DwellColorPicker`'s geometry must be `public const`. Do not add new IVT declarations.
- **File-scoped namespaces** (namespace on line 1, `using` after), **Allman braces**, `_camelCase` fields, nullable enabled.
- **Layout literals are not unit-tested by convention**, with two principled exceptions in this plan: the swatch hit-geometry invariant (Task 5) and the pagination fit invariants (Task 2) — both assert *relationships* (non-overlap, fits-budget), never pixel aesthetics.
- **Don't run two test suites at once** on this box (named-pipe contention is machine-global). A single `dotnet test` invocation is fine.
- **Commit per logical group.** Conventional commits. No push unless the user asks.

**Impact context (GitNexus, run at Rev 1):** `DwellLabel`/`DwellComboBox` flag CRITICAL *reach* (every `Build*Pages` consumer). The edits are constructor-local literals — no signature changes — but the review correctly showed the *vertical* dimension of that reach (page height, non-Settings consumers) is what needed guarding; Task 2 is that guard, Task 6 verifies it live. `SettingsWindow.xaml.cs` changes in Tasks 2 and 5 are confined to `RebuildContent`/sizing constants and one comment.

**Pagination design constraints (Task 2):**
- The pagination row's height is **reserved unconditionally** (`PaginationButtonHeight` + its margins), whether or not the bar renders — otherwise page count would oscillate as the bar appears/disappears.
- A single row taller than the budget still gets its own page — never an empty page, never an infinite loop.
- Degenerate sizing inputs (non-finite/≤0 width or height) **decline to paginate** and pass the source's pages through unchanged, mirroring `ContentAvailableWidth`'s decline-to-constrain.
- **QuickActions and Models opt out** (`PrefersAutoPagination => false`): they bake navigation semantics into page boundaries (per-page back-button ids `qa_back_{page}`, Models' "next rebuild is the management view" flow) and already count-paginate. Their pages keeping count-based paging is pre-existing behaviour; adopting the chunker there is a recorded follow-up, not scope here.
- Measurement runs on a detached `StackPanel` with `Tokens.FontStandard` applied (matching the window's inherited font — `SettingsContentFitTests` proved detached measurement works and that font inheritance must be replicated) and a no-op dwell registrar, so measuring never touches the live detector.

---

## File Structure

| File | Responsibility | Change |
|------|----------------|--------|
| `src/EyeCtrl.App/Views/Settings/SettingsAutoPagination.cs` | **New** — pure chunker + WPF row/page measurer + `NoopDwellRegistrar` | Task 2 |
| `src/EyeCtrl.App/Views/Settings/ISettingsPageSource.cs` | Opt-in seam (`PrefersAutoPagination`, DIM default `false`) | Task 2 |
| `src/EyeCtrl.App/Views/Settings/Sources/*PageSource.cs` (9 static tabs) | Opt in (`=> true`) | Task 2 |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | `ContentAvailableHeight`, `_sources` map, chunking in `RebuildContent`, `CreateControl` extraction, comment rewrite at `:695-700` | Tasks 2, 5 |
| `tests/EyeCtrl.App.Tests/Settings/SettingsAutoPaginationTests.cs` | **New** — chunker + fit tests | Task 2 |
| `src/EyeCtrl.Controls/DwellLabel.cs` | Heading spacing | Task 3 |
| `src/EyeCtrl.Controls/DwellSlider.cs`, `DwellComboBox.cs`, `DwellToggle.cs`, `DwellColorPicker.cs` | Row rhythm; combo height (Task 4); swatch geometry (Task 5) | Tasks 3–5 |
| `tests/EyeCtrl.App.Tests/Settings/SwatchTargetGeometryTests.cs` | **New** — swatch hit-geometry invariant | Task 5 |

---

## Task 1: Isolated worktree + clean baseline

**Files:** none (workspace setup). **Status: Steps 1–2 already done** (see header).

- [ ] **Step 3: Build + baseline tests in the worktree**

Run (from `.worktrees/settings-dwell-sizing`):
```bash
dotnet build EyeCtrl.slnx
dotnet test --filter "Category!=UI"
```
Expected: build OK; all non-UI tests pass. If anything fails, stop and report — do not proceed onto a dirty baseline.

- [ ] **Step 4: Report ready.** No code commit in this task.

---

## Task 2: Automatic height-based pagination

**Files:**
- Create: `src/EyeCtrl.App/Views/Settings/SettingsAutoPagination.cs`
- Create: `tests/EyeCtrl.App.Tests/Settings/SettingsAutoPaginationTests.cs`
- Modify: `src/EyeCtrl.App/Views/Settings/ISettingsPageSource.cs`
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/{Cursor,Selection,Keyboard,Toolbar,Layout,ActionMenu,General,Calibration,Dwell}PageSource.cs`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (`RebuildContent` :443-449, `RenderSpec` :654-686, source construction :255-265, sizing region near `ContentAvailableWidth` :586-602)

**Why:** The content page is a bare `StackPanel` in a `*,Auto` grid row (`SettingsWindow.xaml:54-55`) — overflow **clips**, it does not scroll. Pages are fixed-composition per source, and the densest (6-row) pages measure ≈800–810 DIP against ≈804–836 DIP of content area at 1080p@100% — already borderline **before** this plan's growth. The user-mandated fix: pages must re-split themselves to fit the available height.

**Mechanics (verified):** `RebuildContent()` (SW.cs:443-449) already re-derives `_pages` per tab and re-renders safely; `TotalPages`/`CurrentPage` live in `SettingsViewModel` and the bar/prev/next are driven by their `PropertyChanged`. The chunker is a `List<PageSpec> → List<PageSpec>` transform, so nothing downstream changes.

**Interfaces produced (used by Task 2's own later steps and tests):**
- `internal static class SettingsAutoPagination` with `List<PageSpec> ChunkByHeight(IReadOnlyList<SettingsRowSpec> rows, Func<SettingsRowSpec, double> heightOf, double budget)`, `double MeasureRowHeight(SettingsRowSpec row, double availableWidth, double radiusPhysical, int dwellMs)`, `double MeasurePageHeight(PageSpec page, double availableWidth, double radiusPhysical, int dwellMs)`.
- `SettingsWindow.CreateControl(SettingsRowSpec, IDwellRegistrar, double radiusPhysical, int dwellMs)` → `object` (`DwellControl | IDwellComposite | DwellLabel`), `internal static`.
- `ISettingsPageSource.PrefersAutoPagination` (`bool`, default-interface-member `false`).

### Part A — the pure chunker (TDD, no WPF)

- [ ] **Step A1: Write the failing chunker tests**

Create `tests/EyeCtrl.App.Tests/Settings/SettingsAutoPaginationTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;

public class SettingsAutoPaginationTests
{
    private static SettingsRowSpec Row(string id) => new LabelSpec(() => id);

    // Rows are measured by an injected function, so the chunking arithmetic is testable
    // without WPF. Heights here are synthetic.
    [Fact]
    public void ChunkByHeight_SplitsWhenTheNextRowWouldOverflow()
    {
        var rows = new List<SettingsRowSpec> { Row("a"), Row("b"), Row("c") };
        var heights = new Dictionary<SettingsRowSpec, double>
        {
            [rows[0]] = 40, [rows[1]] = 40, [rows[2]] = 40
        };

        var pages = SettingsAutoPagination.ChunkByHeight(rows, r => heights[r], budget: 100);

        Check.That(pages).HasSize(2);
        Check.That(pages[0].Rows).HasSize(2);
        Check.That(pages[1].Rows).HasSize(1);
    }

    [Fact]
    public void ChunkByHeight_ASingleOversizedRow_GetsItsOwnPageAndNeverLoops()
    {
        var rows = new List<SettingsRowSpec> { Row("a"), Row("huge"), Row("c") };
        var heights = new Dictionary<SettingsRowSpec, double>
        {
            [rows[0]] = 40, [rows[1]] = 500, [rows[2]] = 40
        };

        var pages = SettingsAutoPagination.ChunkByHeight(rows, r => heights[r], budget: 100);

        // Order preserved, no empty pages, the oversized row isolated.
        Check.That(pages).HasSize(3);
        Check.That(pages[1].Rows).HasSize(1);
        Check.That(pages.SelectMany(p => p.Rows)).HasSize(3);
    }

    [Fact]
    public void ChunkByHeight_ExactFit_IsNotSplit()
    {
        var rows = new List<SettingsRowSpec> { Row("a"), Row("b") };
        var pages = SettingsAutoPagination.ChunkByHeight(rows, _ => 50, budget: 100);

        Check.That(pages).HasSize(1);
        Check.That(pages[0].Rows).HasSize(2);
    }

    [Fact]
    public void ChunkByHeight_DegenerateBudget_PassesRowsThroughAsOnePage()
    {
        var rows = new List<SettingsRowSpec> { Row("a"), Row("b"), Row("c") };
        var pages = SettingsAutoPagination.ChunkByHeight(rows, _ => 50, budget: 0);

        Check.That(pages).HasSize(1);
        Check.That(pages[0].Rows).HasSize(3);
    }

    [Fact]
    public void ChunkByHeight_EmptyRows_YieldsOneEmptyPage()
    {
        var pages = SettingsAutoPagination.ChunkByHeight(
            Array.Empty<SettingsRowSpec>(), _ => 50, budget: 100);

        Check.That(pages).HasSize(1);
        Check.That(pages[0].Rows).IsEmpty();
    }
}
```

- [ ] **Step A2: Run → expect compile failure** (`SettingsAutoPagination` not defined)

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsAutoPaginationTests"`
Expected: compile error.

- [ ] **Step A3: Implement the chunker**

Create `src/EyeCtrl.App/Views/Settings/SettingsAutoPagination.cs`:

```csharp
namespace EyeCtrl.App.Views.Settings;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// Height-based re-pagination of settings pages. The content page is a StackPanel with no
/// ScrollViewer (SettingsWindow.xaml: "no ScrollViewer — paginated"), so a page taller than
/// the content area CLIPS its last rows — an unreachable setting for a gaze-only user. This
/// re-splits a tab's rows into pages that each fit the available height.
/// </summary>
internal static class SettingsAutoPagination
{
    /// <summary>
    /// Splits <paramref name="rows"/> into pages whose accumulated row heights fit
    /// <paramref name="budget"/>. A single row taller than the budget gets its own page
    /// (never an empty page, never an unterminated loop). A non-positive budget declines:
    /// one page, rows unchanged — mirrors ContentAvailableWidth's decline-to-constrain.
    /// </summary>
    public static List<PageSpec> ChunkByHeight(
        IReadOnlyList<SettingsRowSpec> rows, Func<SettingsRowSpec, double> heightOf, double budget)
    {
        if (budget <= 0 || double.IsInfinity(budget) || double.IsNaN(budget))
            return [new PageSpec(rows.ToList())];

        var pages = new List<List<SettingsRowSpec>>();
        var current = new List<SettingsRowSpec>();
        var used = 0.0;
        foreach (var row in rows)
        {
            var h = heightOf(row);
            if (current.Count > 0 && used + h > budget)
            {
                pages.Add(current);
                current = [];
                used = 0;
            }
            current.Add(row);
            used += h;
        }
        if (current.Count > 0 || pages.Count == 0)
            pages.Add(current);
        return pages.Select(p => new PageSpec(p)).ToList();
    }

    /// <summary>
    /// Measures one row's rendered height the way the content panel would lay it out: the
    /// real control is constructed via <see cref="SettingsWindow.CreateControl"/> against a
    /// no-op registrar, attached to a detached StackPanel pinned to
    /// <paramref name="availableWidth"/> (so labels wrap exactly as they will on screen) and
    /// carrying the window's font, then measured. Nothing is shown and the live dwell
    /// detector is never touched.
    /// </summary>
    public static double MeasureRowHeight(
        SettingsRowSpec row, double availableWidth, double radiusPhysical, int dwellMs) =>
        MeasureRows([row], availableWidth, radiusPhysical, dwellMs);

    /// <summary>Measures a whole page's rendered height under the same conditions.</summary>
    public static double MeasurePageHeight(
        PageSpec page, double availableWidth, double radiusPhysical, int dwellMs) =>
        MeasureRows(page.Rows, availableWidth, radiusPhysical, dwellMs);

    private static double MeasureRows(
        IReadOnlyList<SettingsRowSpec> rows, double availableWidth, double radiusPhysical, int dwellMs)
    {
        var scratch = new StackPanel { Width = availableWidth };
        // The SettingsWindow is an OverlayWindow, whose constructor sets FontFamily =
        // Tokens.FontStandard and lets it inherit down. Without replicating that, labels
        // measure in the system font and report heights the application never produces
        // (same lesson as SettingsContentFitTests).
        TextElement.SetFontFamily(scratch, Tokens.FontStandard);

        var attached = new List<object>();
        try
        {
            foreach (var row in rows)
            {
                switch (SettingsWindow.CreateControl(row, new NoopDwellRegistrar(), radiusPhysical, dwellMs))
                {
                    case DwellControl c:
                        c.Attach(scratch); attached.Add(c); break;
                    case IDwellComposite comp:
                        comp.Attach(scratch); attached.Add(comp); break;
                    case DwellLabel label:
                        label.Attach(scratch); attached.Add(label); break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown settings row spec: {row?.GetType().Name}");
                }
            }

            scratch.Measure(new Size(availableWidth, double.PositiveInfinity));
            return scratch.DesiredSize.Height;
        }
        finally
        {
            foreach (var item in attached.OfType<IDwellComposite>()) item.Detach(scratch);
            foreach (var item in attached.OfType<DwellControl>()) item.Detach(scratch);
            foreach (var item in attached.OfType<DwellLabel>()) item.Detach(scratch);
        }
    }

    /// <summary>
    /// A registrar that records nothing: measurement constructs real controls, and their
    /// Attach registers dwell targets — against this, so the live detector is never touched.
    /// </summary>
    internal sealed class NoopDwellRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radiusPhysical,
            int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
```

Also add the `CreateControl` seam this references — extract the construction switch **verbatim** out of `RenderSpec` (SW.cs:654-686) into a new method, and reduce `RenderSpec` to construct-then-Add. New code in `SettingsWindow.xaml.cs`:

```csharp
    /// <summary>
    /// Constructs the control for one row spec — the single construction path shared by
    /// rendering (via <see cref="RenderSpec"/>) and by
    /// <see cref="SettingsAutoPagination"/>'s measurement, so the two can never drift.
    /// Returns a <see cref="DwellControl"/>, <see cref="IDwellComposite"/> or
    /// <see cref="DwellLabel"/> depending on the spec.
    /// </summary>
    internal static object CreateControl(
        SettingsRowSpec row, IDwellRegistrar registrar, double radiusPhysical, int dwellMs)
    {
        var cat = DwellCategory.Control;
        switch (row)
        {
            case SliderSpec s:
                return new DwellSlider(registrar, s.Id, s.Label, s.Min, s.Max, s.Step, s.Decimals,
                    s.Get, s.Set, cat, radiusPhysical, dwellMs);
            case ToggleSpec t:
                return new DwellToggle(registrar, t.Id, t.Label, t.Get, t.Set, cat, radiusPhysical, dwellMs);
            case ComboBoxSpec c:
                return new DwellComboBox(registrar, c.Id, c.Label, c.Display,
                    c.Code ?? c.Display, c.Get, c.Set, cat, radiusPhysical, dwellMs);
            case ColorPickerSpec cp:
                return new DwellColorPicker(registrar, cp.Id, cp.Label, cp.Get, cp.Set, cat,
                    DpiHelper.LogicalToPhysicalX(cp.RadiusLogical), dwellMs);
            case ButtonSpec b:
                var (fill, textBrush) = FillFor(b.Kind);
                return new DwellButton(registrar, b.Id, b.Label, b.OnActivate, fill, cat,
                    radiusPhysical, dwellMs, width: b.Width, height: b.Height, textBrush: textBrush);
            case LabelSpec l:
                return new DwellLabel(l.Text());
            default:
                throw new InvalidOperationException($"Unknown settings row spec: {row?.GetType().Name}");
        }
    }

    private void RenderSpec(SettingsRowSpec row)
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        switch (CreateControl(row, _viewModel, r, _dwellMs))
        {
            case DwellControl control: Add(control); break;
            case IDwellComposite composite: Add(composite); break;
            case DwellLabel label: Add(label); break;
        }
    }
```
(This replaces the old `RenderSpec` body; `Add(DwellControl)` still applies `ConstrainToContentWidth` — unchanged. `SettingsRenderMappingTests` pins `FillFor`, which is untouched.)

- [ ] **Step A4: Run chunker tests → pass; whole project still green**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsAutoPaginationTests"`
Expected: 5 PASS.
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: all pass (the `CreateControl` refactor is behaviour-preserving).

- [ ] **Step A5: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/SettingsAutoPagination.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SettingsAutoPaginationTests.cs
git commit -m "feat(settings): height-based page chunker + shared control-construction seam"
```

### Part B — real-measurement fit tests (TDD)

- [ ] **Step B1: Write the failing fit tests** — append to `SettingsAutoPaginationTests.cs`:

```csharp
public class SettingsAutoPaginationFitTests
{
    private const double RadiusPhysical = 50;  // match RenderSpec's logical 50 at 100% DPI
    private const int DwellMs = 500;

    private static SliderSpec Slider(string id) =>
        new(id, id, 0, 100, 1, 0, () => 0, _ => { });

    // The structural guarantee this whole task exists for: every page the chunker emits,
    // re-measured through the production measurement path at the same budget, fits —
    // except a page holding exactly one oversized row, which is by design.
    [StaFact]
    public void EveryChunkedPage_FitsTheBudgetItWasChunkedAgainst()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Slider($"s{i}")).ToList();
        const double budget = 400.0;
        const double width = 416.0;  // the narrowest content column SettingsContentFitTests measures

        var pages = SettingsAutoPagination.ChunkByHeight(
            rows, r => SettingsAutoPagination.MeasureRowHeight(r, width, RadiusPhysical, DwellMs), budget);

        Check.That(pages).HasSizeGreaterThan(1);                    // the budget really forces a split
        Check.That(pages.SelectMany(p => p.Rows)).HasSize(rows.Count);  // nothing lost
        foreach (var page in pages)
        {
            var h = SettingsAutoPagination.MeasurePageHeight(page, width, RadiusPhysical, DwellMs);
            if (page.Rows.Count > 1)
                Check.That(h).IsLessOrEqualThan(budget);
        }
    }

    // Negative control in the spirit of SettingsContentFitTests: an unchunked tall list must
    // NOT fit, proving the measurement above actually measures.
    [StaFact]
    public void UnchunkedTenSliderRows_DoNotFitTheSmallBudget()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Slider($"s{i}")).ToList();
        var onePage = new PageSpec(rows);
        var h = SettingsAutoPagination.MeasurePageHeight(onePage, 416.0, RadiusPhysical, DwellMs);

        Check.That(h).IsStrictlyGreaterThan(400.0);
    }
}
```
(`HasSizeGreaterThan` — if NFluent spells it differently, use `Check.That(pages.Count).IsStrictlyGreaterThan(1)`; intent is exact.)

- [ ] **Step B2: Run → expect PASS** (measurement shipped in Part A; these lock it)
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsAutoPaginationFitTests"` — Expected: 2 PASS. If the negative control fails, the measurer is broken — fix before proceeding.

- [ ] **Step B3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Settings/SettingsAutoPaginationTests.cs
git commit -m "test(settings): chunked pages fit their budget via the production measurer"
```

### Part C — wire into the shell (opt-in seam + available height)

- [ ] **Step C1: Add the opt-in to `ISettingsPageSource`** (default `false` via default-interface-member):

```csharp
    /// <summary>
    /// True when the shell may re-split this source's pages to fit the available content
    /// height (see <see cref="SettingsAutoPagination"/>). Sources that bake navigation
    /// semantics into page boundaries — QuickActions (per-page back-button ids) and Models
    /// ("next rebuild is the management view") — return false and keep their own paging.
    /// </summary>
    bool PrefersAutoPagination => false;
```

- [ ] **Step C2: Opt the 9 static tabs in.** In each of `Cursor/Selection/Keyboard/Toolbar/Layout/ActionMenu/General/Calibration/DwellPageSource.cs`, add:

```csharp
    public bool PrefersAutoPagination => true;
```
(Leave `QuickActionsPageSource` and `ModelsPageSource` on the default `false`.)

- [ ] **Step C3: Track sources + compute height + chunk in the shell.** In `SettingsWindow.xaml.cs`:

Add a field next to `_pageBuilders` (SW.cs ~:205-224) and populate it where sources are constructed (SW.cs :255-265):
```csharp
    private readonly Dictionary<string, ISettingsPageSource> _sources = new();
    // in the ctor, wherever `new XxxPageSource(_ctx)` is assigned:
    //   _sources["Cursor"] = cursorSource; // etc. — one entry per tab, same ids as _pageBuilders
```

Add the height budget beside `ContentAvailableWidth` (SW.cs :586-602):
```csharp
    /// <summary>
    /// The pagination row is reserved UNCONDITIONALLY, whether or not the bar renders
    /// (a single-page tab hides it): otherwise page count would oscillate as the bar
    /// appears and disappears. Same pre-layout philosophy as ContentAvailableWidth —
    /// reads <c>Height</c>, never <c>ActualHeight</c> (content is built in the ctor,
    /// before the first layout pass).
    /// </summary>
    private double ContentAvailableHeight
    {
        get
        {
            if (double.IsNaN(Height) || double.IsInfinity(Height) || Height <= 0)
                return double.PositiveInfinity;
            return Height
                - ContentArea.Margin.Top - ContentArea.Margin.Bottom
                - ContentPanel.Margin.Top - ContentPanel.Margin.Bottom
                - PaginationReserveHeight;
        }
    }

    private double PaginationReserveHeight =>
        PaginationButtonHeight + PaginationPanel.Margin.Top + PaginationPanel.Margin.Bottom;
```

In `RebuildContent()` (SW.cs :443-449), after `_pages = BuildPages();` insert:
```csharp
    _pages = MaybeRepaginateByHeight(_pages);
```
and add:
```csharp
    /// <summary>
    /// Re-splits the current tab's rows into height-fitting pages when the tab's source
    /// opts in. Declines (passes pages through) on degenerate sizing inputs, mirroring
    /// ContentAvailableWidth.
    /// </summary>
    private List<PageSpec> MaybeRepaginateByHeight(List<PageSpec> pages)
    {
        if (!_sources.TryGetValue(_viewModel.Settings.CurrentTabName, out var source)
            || !source.PrefersAutoPagination)
            return pages;

        var width = ContentAvailableWidth;
        var height = ContentAvailableHeight;
        if (!(width > 0) || double.IsInfinity(width) || !(height > 0) || double.IsInfinity(height))
            return pages;

        var rows = pages.SelectMany(p => p.Rows).ToList();
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        return SettingsAutoPagination.ChunkByHeight(
            rows, row => SettingsAutoPagination.MeasureRowHeight(row, width, r, _dwellMs), height);
    }
```

- [ ] **Step C4: Guard tests** — append to `SettingsAutoPaginationTests.cs`:

```csharp
public class SettingsAutoPaginationWiringTests
{
    // SettingsWindow can't be constructed headlessly (8 ctor deps), so — in the spirit of
    // SettingsContentFitTests.TheContentAdd_StillAppliesTheConstraint — pin by source read
    // that RebuildContent actually routes through the chunker.
    [Fact]
    public void RebuildContent_CallsTheRepaginationStep()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));
        var body = ExtractMethod(source, "private void RebuildContent()");

        Check.WithCustomMessage(
                "RebuildContent no longer calls MaybeRepaginateByHeight — the auto-pagination "
                + "this plan added. Repoint this check at the new call, do not delete it.")
            .That(body).Contains("MaybeRepaginateByHeight");
    }

    // The seam: static tabs opt in; the two navigation-state sources stay out.
    [Fact]
    public void QuickActionsAndModels_DoNotOptIn()
    {
        foreach (var name in new[] { "QuickActionsPageSource", "ModelsPageSource" })
        {
            var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Views", "Settings", "Sources", name + ".cs"));
            Check.WithCustomMessage(name + " must NOT set PrefersAutoPagination = true: its "
                    + "page boundaries carry navigation semantics.")
                .That(source.Contains("PrefersAutoPagination => true")).IsFalse();
        }
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
```

- [ ] **Step C5: Build + run all affected tests**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.ViewModels.Tests
```
Expected: all pass. `SettingsViewModelTests.Pagination_Works` (pins `TotalPages >= 1`) is untouched — the chunker always yields ≥1 page.

- [ ] **Step C6: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.App/Views/Settings/ISettingsPageSource.cs src/EyeCtrl.App/Views/Settings/Sources tests/EyeCtrl.App.Tests/Settings/SettingsAutoPaginationTests.cs
git commit -m "feat(settings): auto-paginate static tabs to the available content height"
```

---

## Task 3: Heading→control spacing + even row rhythm

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellLabel.cs:33`
- Modify: `src/EyeCtrl.Controls/DwellSlider.cs:103`, `DwellComboBox.cs:101`, `DwellToggle.cs:69`, `DwellColorPicker.cs:107`

**Why:** Only **2 px** separates a setting's heading from its control today; row top margins (2–4 px) double up against it. Values are **un-trimmed** — Task 2's pagination absorbs the added height.

**Reach note (review Minor 2):** `DwellToggle` is also constructed by `SupervisorSetupWindow.xaml.cs:85,105` (two toggles, no `DwellLabel` above them). The `(0,4,0,12) → (0,0,0,12)` change cuts their inter-toggle gap 16→12 DIP — **accepted**: those toggles register a 200 px physical hit radius, so their separation was never margin-driven.

**Testing:** No new unit test (repo convention). Task 2's fit tests guarantee the growth cannot clip.

Edits (all five):
```csharp
// DwellLabel.cs:33 — BEFORE
            Margin = new Thickness(0, 8, 0, 2)
// AFTER
            Margin = new Thickness(0, 10, 0, 8)

// DwellSlider.cs:103, DwellComboBox.cs:101 — BEFORE
            Margin = new Thickness(0, 2, 0, 12)
// AFTER
            Margin = new Thickness(0, 0, 0, 12)

// DwellColorPicker.cs:107 — BEFORE
        _panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 12) };
// AFTER
        _panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };

// DwellToggle.cs:69 — BEFORE
        Root.Margin = new Thickness(0, 4, 0, 12);
// AFTER
        Root.Margin = new Thickness(0, 0, 0, 12);
```
Net: heading→control **8 px** (was ~4); control→next-heading **22 px** (was ~20).

- [ ] **Step 1:** Apply the five edits.
- [ ] **Step 2:** `dotnet build EyeCtrl.slnx` → OK.
- [ ] **Step 3:** `dotnet test tests/EyeCtrl.Controls.Tests && dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → all pass.
- [ ] **Step 4:** Commit:
```bash
git add src/EyeCtrl.Controls/DwellLabel.cs src/EyeCtrl.Controls/DwellSlider.cs src/EyeCtrl.Controls/DwellComboBox.cs src/EyeCtrl.Controls/DwellToggle.cs src/EyeCtrl.Controls/DwellColorPicker.cs
git commit -m "style(settings): widen heading-to-control gap, even out row rhythm"
```

---

## Task 4: DwellComboBox option height → settings norm

**Files:** Modify `src/EyeCtrl.Controls/DwellComboBox.cs:128`

**Why:** Combo options are 70 px tall vs the 80 px chrome norm (`DwellButton` default, `DwellToggle`, pagination, modal).

**Accepted residual (review Minor 3):** option rows' centre-to-centre spacing is `height + optionBorder bottom margin` = 80 + 8 = **88 DIP against a 100 DIP hit diameter** — still overlapping. Options rarely wrap to a second row, and this task *improves* the ratio (78→88); fully closing it needs a 20 DIP gutter, which is recorded here as knowingly out of scope.

- [ ] **Step 1:** Edit `DwellComboBox.cs:128`:
```csharp
// BEFORE
                width: 0, height: 70);
// AFTER
                width: 0, height: 80);
```
- [ ] **Step 2:** `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.Controls.Tests` → pass (`DwellComboBoxValidationTests` validates arg lengths, not pixel height).
- [ ] **Step 3:** Commit: `git add src/EyeCtrl.Controls/DwellComboBox.cs && git commit -m "style(settings): raise combo option height to the 80 px chrome norm"`

---

## Task 5: Colour swatches up to the target floor + geometric guard (TDD)

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellColorPicker.cs` (public geometry consts; size + margin)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:29` (`ColorSwatchRadius`) **and** the composite-budget comment at `:695-700` (review Major 3)
- Create: `tests/EyeCtrl.App.Tests/Settings/SwatchTargetGeometryTests.cs`

**Why:** Swatches render 90×75 with a shrunk **35 px** hit radius (70 px circle) — below the app's own `MinimumDwellableWidth = 100` floor. Fix: render 100×80, restore the standard radius, size gutters so adjacent hit circles can't overlap, lock it with a test.

**Overlap math (logical px; DPI scales visual and hit circle identically):** horizontal CCC = 100 + 12 = **112** (12 px clearance); vertical CCC = 80 + 28 = **108** (8 px clearance).

**Width note (review Major 3):** the picker row becomes 4 × (100 + 12) = **448 DIP**, past the 416 DIP narrowest content column named in the comment at `SettingsWindow.xaml.cs:695-700` — the `WrapPanel` then **wraps** (never clips) to two rows on the narrowest display. That extra height is absorbed by Task 2's pagination on every static tab. The comment must be rewritten to stay true.

### Steps

- [ ] **Step 1: Refactor — expose swatch geometry as public consts (behaviour-preserving, current values).** In `DwellColorPicker`, add (each const individually documented, per review Nit):

```csharp
    /// <summary>Rendered swatch cell width in logical DIPs. See <see cref="SwatchHeight"/>.</summary>
    public const double SwatchWidth = 90;
    /// <summary>
    /// Rendered swatch cell height in logical DIPs. Together with the margins and the hit
    /// radius, these four consts are what <c>SwatchTargetGeometryTests</c> checks the
    /// non-overlap invariant against — the invariant the old hard-coded literals silently
    /// violated when the radius was shrunk below the control target floor.
    /// </summary>
    public const double SwatchHeight = 75;
    /// <summary>Horizontal gutter between swatches in the WrapPanel, in logical DIPs.</summary>
    public const double SwatchHorizontalMargin = 4;
    /// <summary>Vertical gutter between wrapped swatch rows, in logical DIPs.</summary>
    public const double SwatchVerticalMargin = 4;
```
And read the constructor from them (`:119`, `:121`):
```csharp
// BEFORE
                width: 90, height: 75);
// AFTER
                width: SwatchWidth, height: SwatchHeight);

// BEFORE
            btn.Root.Margin = new Thickness(4);
// AFTER
            btn.Root.Margin = new Thickness(
                SwatchHorizontalMargin, SwatchVerticalMargin,
                SwatchHorizontalMargin, SwatchVerticalMargin);
```
Build → green (`Thickness(4,4,4,4)` ≡ `Thickness(4)`).

- [ ] **Step 2: Write the invariant tests.** Create `tests/EyeCtrl.App.Tests/Settings/SwatchTargetGeometryTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views;
using EyeCtrl.Controls;

public class SwatchTargetGeometryTests
{
    // NOT tautological, even though ColorSwatchRadius is defined as ControlBtnRadius: this
    // trips the moment someone re-types a literal (the original 35 px defect) — do not
    // "simplify" it away. A swatch is a gaze target and must never be a smaller, harder
    // target than every other settings control.
    [Fact]
    public void ColorSwatchRadius_MeetsStandardTargetFloor()
    {
        Check.That(SettingsWindow.ColorSwatchRadius)
            .IsGreaterOrEqualThan(SettingsWindow.ControlBtnRadius);
    }

    // Adjacent swatch hit circles (diameter 2 * ColorSwatchRadius) must not overlap in
    // either axis once the WrapPanel lays cells out with the configured gutters.
    // Centre-to-centre = cell size + 2 * margin, which must strictly exceed the diameter.
    [Fact]
    public void AdjacentSwatches_HitCirclesDoNotOverlap()
    {
        var hitDiameter = 2.0 * SettingsWindow.ColorSwatchRadius;

        var horizontalSpacing =
            DwellColorPicker.SwatchWidth + 2 * DwellColorPicker.SwatchHorizontalMargin;
        Check.That(horizontalSpacing).IsStrictlyGreaterThan(hitDiameter);

        var verticalSpacing =
            DwellColorPicker.SwatchHeight + 2 * DwellColorPicker.SwatchVerticalMargin;
        Check.That(verticalSpacing).IsStrictlyGreaterThan(hitDiameter);
    }
}
```
(Global usings supply `Xunit`/`NFluent`; omitting the explicit `using`s matches `tests/Directory.Build.props` — review Nit 3.)

- [ ] **Step 3: Run → floor test FAILS** (`35 >= 50` false); overlap test PASSES (98 > 70, 83 > 70 — locks the coupling).
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SwatchTargetGeometryTests"`

- [ ] **Step 4: Make it pass.** Update the consts:
```csharp
    public const double SwatchWidth = 100;
    public const double SwatchHeight = 80;
    public const double SwatchHorizontalMargin = 6;
    public const double SwatchVerticalMargin = 14;
```
Update `SettingsWindow.xaml.cs:29`:
```csharp
// BEFORE
    // internal: CursorPageSource passes the swatch radius; SelectionPageSource passes the control radius.
    internal const double ColorSwatchRadius = 35;
// AFTER
    // internal: CursorPageSource passes the swatch radius; SelectionPageSource passes the control radius.
    // Swatches render at DwellColorPicker.SwatchWidth x SwatchHeight with WrapPanel gutters sized so the
    // standard control radius (100 px hit circle) fits without overlapping neighbours — see SwatchTargetGeometryTests.
    internal const double ColorSwatchRadius = ControlBtnRadius;
```
Rewrite the composite-budget comment at `SettingsWindow.xaml.cs:695-700` (review Major 3) — replace the last two sentences (`"…which clears the 416 DIP content column… cap it inside the composite where its layout lives."`) with:
```
    // DwellSlider's row stays the widest at ~316 DIP, but the colour picker's swatch row is
    // 4 × (SwatchWidth + 2 * SwatchHorizontalMargin) = 448 DIP, past the 416 DIP narrowest
    // content column — deliberate: a WrapPanel wraps (never clips) to a second row there,
    // and the extra height is absorbed by height-based pagination. Width overflow in a
    // composite is therefore "wrap", never "clip".
```

- [ ] **Step 5: Run → both PASS** (floor `50 >= 50`; overlap `112 > 100`, `108 > 100`).

- [ ] **Step 6: Commit**
```bash
git add src/EyeCtrl.Controls/DwellColorPicker.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SwatchTargetGeometryTests.cs
git commit -m "fix(settings): bring colour swatches up to the target floor + guard the geometry"
```

---

## Task 6: Full regression + visual verification gate

**Files:** none (verification only — the merge gate).

- [ ] **Step 1: Full non-UI regression**

Run: `dotnet test --filter "Category!=UI"`
Expected: all pass — including `CursorPageSourceTests` (asserts `RadiusLogical == ColorSwatchRadius` *by name* → green at 50), `DwellSelectionKeyboardToolbarPageSourceTests` (pins the Selection picker's radius == `ControlBtnRadius` by name → green; review Minor 4), `SettingsContentFitTests`, `SettingsAutoPaginationTests`/`Fit`/`Wiring`, and `SwatchTargetGeometryTests`.

- [ ] **Step 2: Pre-commit impact check (workspace rule).** Run GitNexus `detect_changes()`.
Expected: changed symbols confined to `SettingsWindow` (`RebuildContent`/`RenderSpec`/`CreateControl`/sizing constants), the 9 static page sources, `ISettingsPageSource`, the new `SettingsAutoPagination`, and the five `Dwell*` controls; affected processes confined to settings flows. No surprises.

- [ ] **Step 3: Visual check (REQUIRES USER OK before launching).** From the worktree:
```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```
Open Settings and verify:
- **Auto-paging:** on each dense tab (Cursor, Selection, Keyboard, Toolbar, Layout, ActionMenu, General, Calibration, Dwell), the **last row of every page is fully visible**; where a tab splits, prev/next navigates and the page indicator is correct. Quick Actions and Models behave exactly as before (count-based pages, back buttons).
- **Spacing:** headings sit ~8 px above their controls; rows breathe evenly.
- **Swatches:** visibly larger (~100×80) with generous gutters on **both** Cursor and Selection pickers; on a narrow window the picker wraps to a second row instead of clipping.
- **Combo options** match toggle/±-button height.

- [ ] **Step 4: Report.** `Complete. <N> commits on polish/settings-dwell-sizing. Visual check <passed/issues>.` Do not push or merge unless asked.

---

## Self-Review (Rev 2)

**1. Review coverage.** Major 1 → every `App.Tests` run filters `Category!=UI` (Tasks 2C5, 3, 6; Task 4 runs Controls.Tests only). Major 2 → Task 2 in full (user-mandated automatic paging), replacing the deleted "negligible height" claim. Major 3 → Task 5 Step 4's comment rewrite + width-note; wrap-not-clip is the documented behaviour. Minor 1 → Global Constraints corrected. Minor 2 → Task 3 reach note. Minor 3 → Task 4 accepted-residual note. Minor 4 → Task 6 Step 1 list. Nits 1–3 → Task 5 Step 1 (per-const docs) and Step 2 comments.

**2. Placeholder scan.** Every code step carries actual content; the two NFluent spellings flagged inline (`HasSizeGreaterThan`, else `pages.Count`) state exact intent. Commands, expected results, and commit messages are concrete. One deliberate source-reference: `CreateControl`'s switch is reproduced in full, not referenced.

**3. Type/name consistency.** `SettingsAutoPagination.ChunkByHeight/MeasureRowHeight/MeasurePageHeight`, `SettingsWindow.CreateControl`, `ISettingsPageSource.PrefersAutoPagination`, `DwellColorPicker.Swatch*` — introduced and consumed under identical names across steps. `CreateControl`'s `ColorPickerSpec` arm keeps `DpiHelper.LogicalToPhysicalX(cp.RadiusLogical)` (the spec-driven radius, unchanged). `NoopDwellRegistrar` implements the four `IDwellRegistrar` members exactly as `SettingsContentFitTests.NullRegistrar` declares them.

**4. Sequencing.** Pagination (Task 2) lands **before** any growth (Tasks 3–5), so no intermediate commit can clip. Each task leaves the tree green. The QA/Models opt-out is seam-tested (Task 2 C4) so the limitation is pinned, not implicit.
