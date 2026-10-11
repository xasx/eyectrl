# Simple Settings Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the spec at `docs/superpowers/specs/2026-10-10-simple-settings-mode-design.md` (r4, `a9b74ecb`): a simple settings mode — a curated visibility subset (Cursor/Dwell/Keyboard/General, ~26 rows) over the existing spec system, switchable by a header toggle, with the current UI as an otherwise unchanged advanced mode.

**Architecture:** A `Simple` init flag on `SettingsRowSpec` + a pure `SimpleSettingsFilter` between `BuildPages()` and `MaybeRepaginateByHeight`; a pure `SettingsSidebarModel` that owns every sidebar decision (visible ids, pages, the snap target) so the window is a thin applier; a title-line chrome toggle in the header registered under the `Pagination` dwell category (generic dispatch, zero ViewModel changes) with a re-arm guard. Read the spec's §5 and §8 (ledger L1–L21) before starting; this plan implements them.

**Tech Stack:** .NET 10 WPF (`EyeCtrl.slnx`), CommunityToolkit.Mvvm, xUnit v3 + NFluent 3.1.0, StringTable localization with fit classes.

## Global Constraints

- **Worktree:** everything happens in `C:\Users\andre\source\repos\eyectrl\.worktrees\simple-settings-mode`, branch `feat/simple-settings-mode`. The main checkout is dirty with the owner's files — never touch it.
- **One test project per `dotnet test` invocation; never two suites at once** (machine-global named pipes). Test output is German: "Fehler!" = FAIL, "Bestanden!" = pass.
- **NFluent only** for assertions (`Check.That(...)`); xUnit v3 for structure. Inclusive comparisons are `IsGreaterOrEqualThan`/`IsLessOrEqualThan`.
- **No UI literals:** every user-visible string is a `StringTable` key present in `src/EyeCtrl.Core/Localization/strings.json` with `who`/`where`/`fit` metadata. Keys are **literals at the call site**.
- **Insertion-scan caution:** `LanguageRowReachTests`, `SettingsTabDriftTests`, `SettingsTabLabelFitTests` anchor on the **first** `"_tabDefs ="` text in `SettingsWindow.xaml.cs`, comments included. Never write that token anywhere above the initializer.
- **Own-methods rule:** the toggle's registration/disarm/wash/label code lives in its own methods, never inside `RenderPagination`, `MakePagButton`, or `RegisterPagerTarget` (their bodies are regex-extracted by `SettingsPagerChromeTests`; `MakePagButton` must keep containing no `_viewModel.Register(`).
- **Lambda-free frame loop:** never add `=>` inside `UpdateAllTargetPositions` (`SettingsFrameLoopAllocationTests`).
- **Gaze-trap invariant:** a hidden control has no live hit circle. Every registration change ships with its unregister counterpart.
- **The app is never launched by non-gated tests.** The FlaUI set (`Category=UI` + `EYECTRL_UI_TESTS=1`) is owner-gated: Task 13 only, after asking.
- Advanced mode (the default) must stay bit-for-bit unchanged except the one header toggle (ledger L13).

---

## Ticket T1 — pure model + filter + curation (no window changes)

### Task 1: `SimpleSettingsUi` flag + reset preservation

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/GeneralSettings.cs`
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs:55-75` (`ResetAllSettings`)
- Test: `tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Produces: `GeneralSettings.SimpleSettingsUi` (`bool`, default `false`) — every later task reads this; `ResetAllSettings` preserves it.

- [ ] **Step 1: Write the failing tests**

In `SettingsViewModelTests.cs`, next to the existing `UiLanguage` survival tests, add:

```csharp
[Fact]
public void ResetAllSettings_KeepsSimpleSettingsUi()
{
    var settings = new EyeCtrlSettings();
    settings.General.SimpleSettingsUi = true;
    var vm = new SettingsViewModel(settings);

    vm.ResetAllSettings();

    Check.That(settings.General.SimpleSettingsUi).IsTrue();
}
```

And extend the `notResetPropsByDesign` census (currently listing `("General", ...)` pairs around `:118-121`) with the new pair:

```csharp
("General", nameof(EyeCtrl.Core.Settings.GeneralSettings.SimpleSettingsUi)),
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ResetAllSettings_KeepsSimpleSettingsUi"`
Expected: FAIL — `SimpleSettingsUi` does not exist (compile error).

- [ ] **Step 3: Implement**

In `GeneralSettings.cs`, after `_startPaused`:

```csharp
/// <summary>Whether the settings window opens in its simple (curated) mode. A UI preference in
/// the company of <see cref="UiLanguage"/>: nothing outside SettingsWindow reads it, and bulk
/// resets keep it for the same reason they keep the language (spec §5.1).</summary>
[ObservableProperty] private bool _simpleSettingsUi = false;
```

In `SettingsViewModel.ResetAllSettings`, mirror the `UiLanguage` capture/restore exactly (the D10 pattern — save before the copy loop, restore after):

```csharp
var language = _settings.General.UiLanguage;
var simpleUi = _settings.General.SimpleSettingsUi;
// ... existing copy loop unchanged ...
_settings.General.UiLanguage = language;
_settings.General.SimpleSettingsUi = simpleUi;
```

Also extend the `<summary>`'s "The language survives (D10…)" paragraph with one sentence: "The interface mode survives for the same reason — a bulk reset must not flip fifteen tabs into a novice's face."

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests`
Expected: all pass (the census test now expects the new pair and finds it).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/GeneralSettings.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs
git commit -m "feat(settings): SimpleSettingsUi flag with D10-pattern reset preservation"
```

### Task 2: `SettingsRowSpec.Simple`

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs:14-25`
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowSpecTests.cs`

**Interfaces:**
- Produces: `bool SettingsRowSpec.Simple { get; init; }` (default `false`) — Tasks 3–5 build on it.

- [ ] **Step 1: Write the failing test**

In `SettingsRowSpecTests.cs`:

```csharp
[Fact]
public void Simple_DefaultsToFalse_AndIsSettableOnAnySpecKind()
{
    var unflagged = new ToggleSpec("t1", "Label", () => true, _ => { });
    var flagged = new SliderSpec("s1", "Label", 0, 1, 0.1, 1, () => 0, _ => { }) { Simple = true };
    var flaggedLabel = new LabelSpec(() => "note") { Simple = true };

    Check.That(unflagged.Simple).IsFalse();
    Check.That(flagged.Simple).IsTrue();
    Check.That(flaggedLabel.Simple).IsTrue();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Simple_DefaultsToFalse"`
Expected: FAIL (compile error — no `Simple`).

- [ ] **Step 3: Implement**

In `SettingsRowSpec.cs`, beside `Description`:

```csharp
/// <summary>
/// Shown in simple mode. Default false: a new row joins advanced only — entering the curated
/// subset is a deliberate act recorded by the curation pin test (spec §5.3). An init property
/// rather than a positional parameter so every existing spec construction keeps compiling.
/// </summary>
public bool Simple { get; init; }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsRowSpecTests"`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowSpecTests.cs
git commit -m "feat(settings): Simple tier flag on SettingsRowSpec"
```

### Task 3: `SimpleSettingsFilter` + `SimpleTabIds`

**Files:**
- Create: `src/EyeCtrl.App/Views/Settings/SimpleSettingsFilter.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/SimpleSettingsFilterTests.cs`

**Interfaces:**
- Produces: `internal static class SimpleSettingsFilter` with `IReadOnlySet<string> SimpleTabIds` ({"Cursor","Dwell","Keyboard","General"}) and `IReadOnlyList<PageSpec> Apply(IReadOnlyList<PageSpec> pages)` — Tasks 4–8 consume both.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;

public class SimpleSettingsFilterTests
{
    private static SliderSpec Row(string id, bool simple = false) =>
        new(id, id, 0, 1, 0.1, 1, () => 0, _ => { }) { Simple = simple };

    [Fact]
    public void Apply_KeepsOnlySimpleRows_AndDropsEmptyPages()
    {
        var pages = new List<PageSpec>
        {
            new([Row("a"), Row("b", simple: true)]),
            new([Row("c")]),                       // no simple row -> dropped
            new([Row("d", simple: true), Row("e", simple: true)]),
        };

        var filtered = SimpleSettingsFilter.Apply(pages);

        Check.That(filtered).HasSize(2);
        Check.That(filtered[0].Rows.Select(r => r.Id)).ContainsExactly("b");
        Check.That(filtered[1].Rows.Select(r => r.Id)).ContainsExactly("d", "e");
    }

    [Fact]
    public void SimpleTabIds_AreExactlyTheCuratedTabs()
    {
        Check.That(SimpleSettingsFilter.SimpleTabIds).ContainsExactly("Cursor", "Dwell", "Keyboard", "General");
    }

    [Fact]
    public void EverySimpleTierSource_PrefersAutoPagination()
    {
        var ctx = EyeCtrl.App.Tests.Settings.Pages.TestSettingsPageContext.Build();
        var sources = new Dictionary<string, ISettingsPageSource>
        {
            ["Cursor"] = new CursorPageSource(ctx),
            ["Dwell"] = new DwellPageSource(ctx),
            ["Keyboard"] = new KeyboardPageSource(ctx),
            ["General"] = new GeneralPageSource(ctx),
        };

        foreach (var id in SimpleSettingsFilter.SimpleTabIds)
            Check.That(sources[id].PrefersAutoPagination).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SimpleSettingsFilterTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement**

```csharp
namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// The simple-mode visibility layer: keeps the rows whose spec opted into the curated subset,
/// drops pages left empty. Pure — the pipeline calls it between BuildPages() and
/// MaybeRepaginateByHeight, sources never know it exists, and every per-source gate keeps
/// testing Build() unfiltered (spec §5.4).
/// </summary>
internal static class SimpleSettingsFilter
{
    /// <summary>
    /// The tabs simple mode shows. A static set, deliberately NOT a bool on the window's
    /// _tabDefs tuple: SettingsTabDriftTests and SettingsTabLabelFitTests regex-parse that
    /// tuple with exactly four members. Lives here (the filter's file) so the T1 drift test
    /// needs nothing from the sidebar model (spec §5.2, ledger L9).
    /// </summary>
    internal static readonly IReadOnlySet<string> SimpleTabIds = new HashSet<string>
    {
        "Cursor", "Dwell", "Keyboard", "General"
    };

    internal static IReadOnlyList<PageSpec> Apply(IReadOnlyList<PageSpec> pages) =>
        pages
            .Select(p => new PageSpec(p.Rows.Where(r => r.Simple).ToArray()))
            .Where(p => p.Rows.Count > 0)
            .ToArray();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SimpleSettingsFilterTests"`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/SimpleSettingsFilter.cs tests/EyeCtrl.App.Tests/Settings/SimpleSettingsFilterTests.cs
git commit -m "feat(settings): pure SimpleSettingsFilter with the curated tab set"
```

### Task 4: curation — `Simple = true` on the four sources

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs`
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs`
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/KeyboardPageSource.cs`
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs`

**Interfaces:**
- Consumes: `SettingsRowSpec.Simple` (Task 2).
- Produces: the curated row set asserted by Task 5.

Every edit appends `Simple = true` to the row's existing object initializer: `{ Description = ... }` becomes `{ Description = ..., Simple = true }`; rows without a `Description` (the reset buttons, `LabelSpec` twins) get `{ Simple = true }`. Exactly these rows, per source:

- [ ] **Step 1: DwellPageSource** — `d_act`, `d_menu`, `d_tool`, `d_key`, `d_pred`, `d_rad`, `d_cue_sound`, `d_reset`. Example shape (each construction already ends with `{ Description = StringTable.Current.T(...) }`):

```csharp
new SliderSpec("d_act",
    StringTable.Current.T("settings.dwell.activation.label"), 200, 1500, 50, 0,
    () => d.ActivationButtonDwellMs, v => { d.ActivationButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = StringTable.Current.T("settings.dwell.activation.description"), Simple = true },
```

`d_reset` has no `Description`: append `{ Simple = true }` after its `ButtonKind.Reset, Width: 300, Height: 100` argument list — the object initializer goes after the positional ones: `new ButtonSpec("d_reset", ..., ButtonKind.Reset, Width: 300, Height: 100) { Simple = true }`.

- [ ] **Step 2: CursorPageSource** — `c_ring_style`, **both branches of the `c_color` ternary** (the `LabelSpec` unavailable-note AND the `ColorPickerSpec`), `c_size`, `c_opacity`, `c_reset`. The ternary needs both branches flagged or the note disappears in simple mode:

```csharp
c.RingStyle == CursorRingStyle.AchromaticReticle
    ? (SettingsRowSpec)new LabelSpec(
        () => StringTable.Current.T("settings.cursor.color_unavailable")) { Simple = true }
    : new ColorPickerSpec("c_color",
        StringTable.Current.T("settings.cursor.color.label"),
        () => c.CircleColor,
        v => { c.CircleColor = v; _ctx.ApplyAndSave(); },
        SettingsWindow.ColorSwatchRadius)
        { Description = StringTable.Current.T("settings.cursor.color.description"), Simple = true },
```

- [ ] **Step 3: KeyboardPageSource** — `k_layout`, `k_size`, `k_pred`, `k_lang`, `k_sound`, `k_reset`. The learned-words page and `k_pred_count`/`k_edge` stay advanced (they simply get no flag).

- [ ] **Step 4: GeneralPageSource** — `g_language` (first), the pending-language note from `PendingLanguageNote()`, `g_theme`, `g_boot`, `g_pause`, `g_track`, and in `SupervisionRows()`: the **unavailable** `LabelSpec`, the `g_supervision` toggle, the conditional `g_supervision_retry` button, and the status `LabelSpec` — but **not** the null-service early `yield break` (no rows exist to flag). `g_reset` gets `{ Simple = true }` (it has no `Description`).

- [ ] **Step 5: Run the full App suite to prove nothing else moved**

Run: `dotnet test tests/EyeCtrl.App.Tests`
Expected: all pass — the flag is inert until a filter reads it.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/
git commit -m "feat(settings): curate the simple-mode rows on the four sources"
```

### Task 5: curation pin + reachability

**Files:**
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/SimpleModeCurationTests.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/SimpleModeReachabilityTests.cs`

**Interfaces:**
- Consumes: `SimpleSettingsFilter` (Task 3), the curated rows (Task 4), `SupervisionComposition.Create` (see `tests/EyeCtrl.App.Tests/Settings/Pages/GeneralPageSourceResetDriftTests.cs:38-60` for the live-service construction), `SettingsAutoPagination.Repaginate` + `SettingsWindow.CreateControl` (see `tests/EyeCtrl.App.Tests/Settings/HandChunkedPageFitTests.cs` for the budget/reach idiom).

- [ ] **Step 1: Write the curation pin test**

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Pins the curated subset per tab as an ordered row sequence (kind + id for interactive rows;
/// LabelSpec twins pinned by a text predicate, their id is ""). Built with live supervision
/// (three constructible branches) and with the pending-language note present, so conditional
/// rows cannot silently leave simple mode (spec §6, round-2 m4/m5).
/// </summary>
public class SimpleModeCurationTests
{
    public static TheoryData<string> SimpleTabs() => new() { "Cursor", "Dwell", "Keyboard", "General" };

    [Theory]
    [MemberData(nameof(SimpleTabs))]
    public void SimpleMode_ShowsExactlyTheCuratedSequence(string tab)
    {
        var settings = new EyeCtrl.Core.Settings.EyeCtrlSettings();
        settings.General.UiLanguage = EyeCtrl.Core.Enums.UiLanguage.German; // pending note present
        var ctx = TestSettingsPageContext.Build(settings: settings);
        var source = SourceFor(ctx, tab);

        var rows = SimpleSettingsFilter.Apply(source.Build()).SelectMany(p => p.Rows).ToArray();

        Check.That(rows.Select(Describe)).ContainsExactly(ExpectedSequence(tab).ToArray());
    }

    [Fact]
    public void SupervisionUnavailable_NoteIsSimple()
    {
        // The unavailable branch needs a service whose exe is absent — the composition fake
        // with isSupervisorInstalled: () => false, mirroring ResetDrift's wiring.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_simplecur_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrl.Core.Settings.EyeCtrlSettings();
            var (_, toggle, _) = EyeCtrl.App.Services.Supervision.SupervisionComposition.Create(
                settings, path, new FakeStartupRegistrar(),
                enablePublishing: () => { }, launchSupervisor: () => false,
                tryReachSupervisor: () => false, isSupervisorInstalled: () => false,
                isLaunchRegistered: () => true, isSupervisorConnected: () => false,
                runInBackground: work => work(), postToUi: work => work());
            var c = TestSettingsPageContext.Build(settings: settings, supervision: toggle);
            var rows = SimpleSettingsFilter.Apply(new GeneralPageSource(c).Build())
                .SelectMany(p => p.Rows).ToArray();

            Check.That(rows.OfType<LabelSpec>()
                .Any(l => l.Text().Contains("unavailable"))).IsTrue();
        }
        finally { File.Delete(path); }
    }

    private static ISettingsPageSource SourceFor(SettingsPageContext ctx, string tab) => tab switch
    {
        "Cursor" => new CursorPageSource(ctx),
        "Dwell" => new DwellPageSource(ctx),
        "Keyboard" => new KeyboardPageSource(ctx),
        "General" => new GeneralPageSource(ctx),
        _ => throw new ArgumentException($"Unknown simple tab {tab}")
    };

    /// <summary>Interactive rows pin as "Kind:id"; LabelSpec rows (id "") pin by kind + a text
    /// predicate prefix — the sanctioned label-twin rule (round-1 I4, round-2 m4).</summary>
    private static string Describe(SettingsRowSpec row) => row switch
    {
        SliderSpec s => $"Slider:{s.Id}",
        ToggleSpec t => $"Toggle:{t.Id}",
        ComboBoxSpec c => $"Combo:{c.Id}",
        ColorPickerSpec p => $"Pick:{p.Id}",
        ButtonSpec b => $"Button:{b.Id}",
        LabelSpec l => $"Label:{l.Text()[..Math.Min(12, l.Text().Length)]}",
        _ => row.GetType().Name
    };
}
```

The `ExpectedSequence` lists (write them literally in the test file, derived from the sources after Task 4):

- Cursor (set `settings.Cursor.RingStyle = CursorRingStyle.WeightedSandwich` first so the picker branch builds): `Pick:c_color`, `Combo:c_ring_style`, `Slider:c_size`, `Slider:c_opacity`, `Button:c_reset` — pages flatten in BUILD order, and Cursor builds the colour row on page 1 before the ring-style row on page 2.
- Dwell: `Slider:d_act`, `Slider:d_menu`, `Slider:d_tool`, `Slider:d_key`, `Slider:d_pred`, `Slider:d_rad`, `Toggle:d_cue_sound`, `Button:d_reset`.
- Keyboard: `Combo:k_layout`, `Slider:k_size`, `Toggle:k_pred`, `Combo:k_lang`, `Toggle:k_sound`, `Button:k_reset`.
- General (German pending note present, supervision live + disconnected via `SupervisionComposition.Create` with `isSupervisorInstalled: () => true, isSupervisorConnected: () => false`, and `settings.Supervision.Enabled = true` + `SupervisionEnabledWriter` as in the ResetDrift test): `Combo:g_language`, `Label:<pending-note predicate>`, `Combo:g_theme`, `Toggle:g_boot`, `Toggle:g_pause`, `Toggle:g_track`, `Toggle:g_supervision`, `Button:g_supervision_retry`, `Label:<status predicate>`, `Button:g_reset`. For the two labels use `ContainsExactly` with a matcher: assert the filtered sequence by mapping labels through `Describe` but for `LabelSpec` positions use NFluent's element-wise `IsEqualTo` against the `Describe` prefix, and where the 12-char prefix is brittle (localized text), pin position + a `Text()` contains-predicate via `HasElementThatMatches` composition — keep it as one ordered list where label entries are `"Label:"` + a stable prefix of the resolved English text (the headless table is the embedded English).

- [ ] **Step 2: Run to verify it fails, then adjust expected sequences to the real build order**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SimpleModeCuration"`
Expected: initially FAIL where the hand-written sequence mis-orders a row (e.g. Cursor builds `c_color` before `c_ring_style` because pages flatten in build order). Fix the expected lists to the sources' true order — that order IS the pin.

- [ ] **Step 3: Write the reachability test**

In `SimpleModeReachabilityTests.cs`, follow `HandChunkedPageFitTests`'s idiom end to end — reuse that file's own budget constants and its `CreateControl`-based row-measurement helper (do not reinvent measuring and do not invent budget numbers): for each simple tab, `Build → SimpleSettingsFilter.Apply → SettingsAutoPagination.Repaginate(filteredRows.SelectMany(p => p.Rows).ToList(), width, height, radius, dwellMs)` with the SAME width/height/radius values that file's narrowest-budget case uses, then assert every interactive row id in the filtered list appears in exactly one output page and `pages.Count >= 1`. Sliders/toggles/combo/picker rows count; `LabelSpec` rows have no dwell id and are ignored by the assertion.

- [ ] **Step 4: Run both new test classes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SimpleMode"`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Settings/Pages/SimpleModeCurationTests.cs tests/EyeCtrl.App.Tests/Settings/SimpleModeReachabilityTests.cs
git commit -m "test(settings): pin the simple-mode curation sequence and reachability"
```

---

## Ticket T2 — sidebar model + window mechanics

### Task 6: `SettingsSidebarModel`

**Files:**
- Create: `src/EyeCtrl.App/Views/Settings/SettingsSidebarModel.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsSidebarModelTests.cs`

**Interfaces:**
- Consumes: `SimpleSettingsFilter.SimpleTabIds` (Task 3), `SettingsTabPagination` (existing).
- Produces: `SettingsSidebarModel.Compute(allIds, simpleIds, simple, sidebarBudget, tabSlot, currentTab) → SidebarLayout(VisibleIds, TabsPerPage, PageCount, PageIndex, Slice, PagerPresent, SnapTo)` — Tasks 7–8 apply it; Task 11's resting-gaze test needs nothing from here.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;

public class SettingsSidebarModelTests
{
    private static readonly string[] All15 =
    [
        "Cursor", "Dwell", "Selection", "Actions", "Layout", "Keyboard", "Models", "Toolbar",
        "Calibration", "General", "QuickActions", "EyeCare", "Adaptation", "Advanced", "Diagnostics"
    ];

    private const double TallBudget = 1080 - 116 - 196 - 16;   // ~6 tabs per page at the owner's height
    private const double Slot = 100 + 2 * 4;                    // TabContainerHeight + 2 * TabButtonMargin

    [Fact]
    public void Advanced_ShowsAllTabs_NoPager_NoSnap()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: false, TallBudget, Slot, "Adaptation");

        Check.That(layout.VisibleIds).HasSize(15);
        Check.That(layout.PagerPresent).IsTrue();
        Check.That(layout.SnapTo).IsNull();
        Check.That(layout.Slice.Count).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Simple_HidesNonCuratedTabs_AndNeverSnapsFromAHiddenTab_WithoutCrashing()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: true, TallBudget, Slot, "Adaptation");

        Check.That(layout.VisibleIds).ContainsExactly("Cursor", "Dwell", "Keyboard", "General");
        Check.That(layout.SnapTo).IsEqualTo("Cursor");          // Adaptation is hidden -> snap to the leader
        Check.That(layout.PageIndex).IsEqualTo(0);              // post-snap page, not a stale clamp
        Check.That(layout.Slice).IsEqualTo((0, 4));
        Check.That(layout.PagerPresent).IsFalse();
    }

    [Fact]
    public void Simple_ToAdvanced_OnGeneral_ReDerivesGeneralsPage_NotClampedToZero()
    {
        // The round-3 I4 regression: simple always sits on page 0; a clamp would strand General.
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: false, TallBudget, Slot, "General");

        Check.That(layout.SnapTo).IsNull();                     // General survives the switch
        var generalIndex = 9;                                   // 0-based index of "General" in All15
        Check.That(layout.PageIndex).IsEqualTo(generalIndex / layout.TabsPerPage);
    }

    [Fact]
    public void Slice_IsAlwaysWithinVisibleIds_TheN1Regression()
    {
        // Faces are built over ALL defs by the window; the model's slice may only name ids the
        // window will have built. Constructed-in-simple, switched-to-advanced must not slice a
        // tab whose face was never created.
        var simple = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: true, TallBudget, Slot, "Cursor");
        Check.That(simple.VisibleIds).HasSize(4);
        Check.That(simple.VisibleIds).ContainsOnlyElementsThatMatch(id => All15.Contains(id));
    }

    [Fact]
    public void EmptyCurrentTab_LandsOnPageZero()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: false, TallBudget, Slot, "");
        Check.That(layout.PageIndex).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsSidebarModelTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement**

```csharp
namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// Pure sidebar reconciliation for the settings window (spec §5.6): everything the sidebar
/// needs — visible ids, page count, the post-snap page index, the slice, pager presence, and
/// the snap decision — derived from (all tab ids, the simple set, the mode, the sidebar
/// budget, the current tab), so the one switch decision with user-visible consequences is
/// headless-testable. The window applies the outputs; it never re-derives them. No WPF types:
/// the purity census walks this file.
/// </summary>
internal static class SettingsSidebarModel
{
    internal sealed record SidebarLayout(
        IReadOnlyList<string> VisibleIds,
        int TabsPerPage,
        int PageCount,
        int PageIndex,
        (int Start, int Count) Slice,
        bool PagerPresent,
        string? SnapTo);

    internal static SidebarLayout Compute(
        IReadOnlyList<string> allIds,
        IReadOnlySet<string> simpleIds,
        bool simple,
        double sidebarBudget,
        double tabSlot,
        string currentTab)
    {
        var visible = simple ? allIds.Where(id => simpleIds.Contains(id)).ToArray() : allIds.ToArray();
        var perPage = SettingsTabPagination.TabsPerPage(sidebarBudget, tabSlot);
        var pageCount = SettingsTabPagination.PageCount(visible.Length, perPage);
        // The snap: the current tab must be visible after the switch; the leader of the target
        // set (Cursor in both) is the stable target. The page index is derived from the
        // POST-snap tab — a clamp would strand the current tab off-page (round-3 I4).
        var snapTo = visible.Contains(currentTab) || visible.Count == 0
            ? null
            : visible[0];
        var effective = snapTo ?? currentTab;
        var index = Array.IndexOf(visible, effective);
        var pageIndex = index < 0 ? 0 : SettingsTabPagination.PageOf(index, perPage);
        var slice = SettingsTabPagination.PageSlice(visible.Length, perPage, pageIndex);
        return new SidebarLayout(visible, perPage, pageCount, pageIndex, slice, pageCount > 1, snapTo);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsSidebarModelTests"`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/SettingsSidebarModel.cs tests/EyeCtrl.App.Tests/Settings/SettingsSidebarModelTests.cs
git commit -m "feat(settings): pure SettingsSidebarModel with SnapTo and post-snap paging"
```

### Task 7: the window applies the model (advanced behavior unchanged)

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (regions: fields near `:342`, `BuildTabButtons` `:750-775`, `RenderTabPage` `:793-806`, `ShowLanguageRow` `:994-1001`)
- Test: `tests/EyeCtrl.App.Tests/Tests/` none; Test: `tests/EyeCtrl.App.Tests/Settings/SettingsTabPaginationWiringTests.cs`, `tests/EyeCtrl.App.Tests/Localization/LanguageRowReachTests.cs`, `tests/EyeCtrl.App.Tests/SettingsDeepLinkTests.cs`

**Interfaces:**
- Consumes: `SettingsSidebarModel` (Task 6), `SimpleSettingsFilter.SimpleTabIds` (Task 3).
- Produces: window fields/methods Task 8 and Task 11 rely on — `_visibleDefs` array, `ComputeSidebarLayout(currentTab)`, `ApplySidebarLayout(layout)`, `RebuildTabPagerChrome()`.

- [ ] **Step 1: Add the failing wiring pins first**

In `SettingsTabPaginationWiringTests.cs` (which extracts method bodies via the shared `ExtractMethod` helper in `SettingsPagerChromeTests.cs:786-796`), add:

```csharp
[Fact]
public void RenderTabPage_SlicesTheVisibleDefs()
{
    var body = ExtractMethod(WindowSource(), "private void RenderTabPage(");
    Check.WithCustomMessage(
            "RenderTabPage must slice the model-visible defs, never the full list — a hidden tab "
            + "must not be attached or registered (the gaze-trap rule extends to mode hiding).")
        .That(body).Contains("_visibleDefs");
}

[Fact]
public void BuildTabButtons_BuildsFacesOverEveryDef()
{
    var body = ExtractMethod(WindowSource(), "private void BuildTabButtons(");
    Check.WithCustomMessage(
            "Faces are built for EVERY _tabDefs entry, once — filtering the face loop crashes "
            + "simple-to-advanced with KeyNotFoundException (round-2 N1: the process dies with "
            + "no Supervisor present).")
        .That(body).Contains("foreach (var (id, icon, labelKey, _) in _tabDefs)");
}
```

Update the existing `BuildTabButtons_SizesPagesFromTheChunker` pin (`:79`): it expects `SettingsTabPagination.TabsPerPage` inside `BuildTabButtons`; after this task the math routes through `ComputeSidebarLayout` — change the expected literal to `ComputeSidebarLayout(` with the same custom-message style.

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsTabPaginationWiringTests"`
Expected: FAIL on the two new pins.

- [ ] **Step 2: Implement the window changes**

Add a field and two helpers near the `_tabDefs` declaration (remember the Global Constraint: no text matching `_tabDefs =` may appear above the initializer — write `_tabDefs` without the assignment operator in comments):

```csharp
private (string Id, string Icon, string LabelKey, string DescriptionKey)[] _visibleDefs = [];

/// <summary>
/// Everything the sidebar needs for the current mode, from the pure model. The FACE loop never
/// reads this (faces exist for every tab); only the page count, the active index, the
/// RenderTabPage slice and the language deep link ride it.
/// </summary>
private SettingsSidebarModel.SidebarLayout ComputeSidebarLayout(string currentTab) =>
    SettingsSidebarModel.Compute(
        _tabDefs.Select(t => t.Id).ToArray(),
        SimpleSettingsFilter.SimpleTabIds,
        _settings.General.SimpleSettingsUi,
        SidebarTabBudget, TabContainerHeight + 2 * TabButtonMargin,
        currentTab);

/// <summary>
/// Applies one layout: page-count fields, the pager chrome (cleared before rebuild —
/// BuildTabPagePager only appends), then ONE RenderTabPage. Registration mirrors chrome at
/// every step; the tab pager's guards keep their own lifecycle and are harmless while absent.
/// </summary>
private void ApplySidebarLayout(SettingsSidebarModel.SidebarLayout layout)
{
    _visibleDefs = _tabDefs.Where(d => layout.VisibleIds.Contains(d.Id)).ToArray();
    _tabsPerPage = layout.TabsPerPage;
    _tabPageCount = layout.PageCount;
    _tabPageIndex = layout.PageIndex;
    RebuildTabPagerChrome();
    RenderTabPage();
}

/// <summary>Clears the tab pager chrome unconditionally, then rebuilds it only when the mode's
/// page count needs one. Unregisters first: a vanished button must not keep a live circle.</summary>
private void RebuildTabPagerChrome()
{
    _viewModel.Unregister("tab_prev", DwellCategory.Pagination);
    _viewModel.Unregister("tab_next", DwellCategory.Pagination);
    TabPagerPanel.Children.Clear();
    _tabPagerButtons.Clear();
    _tabPagerBrushes.Clear();
    if (_tabPageCount > 1)
    {
        BuildTabPagePager();
        UpdateTabPagePagerState();
    }
}
```

Rewire `BuildTabButtons`'s tail (the face loop over `_tabDefs` stays untouched):

```csharp
var layout = ComputeSidebarLayout(
    string.IsNullOrEmpty(_viewModel.Settings.CurrentTabName) ? "Cursor" : _viewModel.Settings.CurrentTabName);
ApplySidebarLayout(layout);
```

(replacing the four lines that today compute `_tabsPerPage`/`_tabPageCount`/`activeIndex`/`_tabPageIndex` and call `BuildTabPagePager(); RenderTabPage();`).

In `RenderTabPage`, change the slice source and the loop element:

```csharp
var (start, count) = SettingsTabPagination.PageSlice(_visibleDefs.Length, _tabsPerPage, _tabPageIndex);
for (var i = start; i < start + count; i++)
{
    var (id, _, _, _) = _visibleDefs[i];
    ...
}
```

In `ShowLanguageRow`, change the lookup (General is visible in both modes, so the index always exists):

```csharp
var index = Array.FindIndex(_visibleDefs, t => t.Id == "General");
```

- [ ] **Step 3: Update the literal pins that moved**

- `SettingsDeepLinkTests` around `:233`/`:234`: its `rowBody` extraction of `ShowLanguageRow` expects `Array.FindIndex(_tabDefs, t => t.Id == "General")` — update the expected literal to `_visibleDefs`. The `PageOf` line below it is unchanged; if the test asserts both lines in one string, keep both updated to the new body.
- `SettingsTabPaginationWiringTests.RenderTabPage_UnregistersTabsItHides` (existing) stays green — the unregister-all loop is untouched.

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsDeepLinkTests|FullyQualifiedName~SettingsTabPaginationWiringTests|FullyQualifiedName~SettingsTabDriftTests|FullyQualifiedName~SettingsTabLabelFitTests|FullyQualifiedName~LanguageRowReachTests"`
Expected: pass (advanced default → `_visibleDefs` == `_tabDefs`; the drift/label-fit regexes still find the initializer first).

- [ ] **Step 4: Run the whole App suite**

Run: `dotnet test tests/EyeCtrl.App.Tests`
Expected: all pass — advanced mode's behavior is identical; nothing consumes `SimpleSettingsFilter` in the pipeline yet.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SettingsTabPaginationWiringTests.cs tests/EyeCtrl.App.Tests/SettingsDeepLinkTests.cs
git commit -m "refactor(settings): window applies the pure sidebar model; deep link rides visible defs"
```

### Task 8: filter step + `SwitchSettingsUiMode`

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (`RebuildContent` `:1076-1090`; new method near `ShowTab`)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsModeSwitchWiringTests.cs`

**Interfaces:**
- Consumes: `SimpleSettingsFilter.Apply` (Task 3), `ApplySidebarLayout`/`ComputeSidebarLayout` (Task 7).
- Produces: `SwitchSettingsUiMode(bool simple)` — Task 11's toggle calls it; `UpdateModeToggleLabel` is added by Task 12 (the call site here is added there — do NOT add a call to a nonexistent method).

- [ ] **Step 1: Write the failing wiring pin**

```csharp
namespace EyeCtrl.App.Tests.Settings;

public class SettingsModeSwitchWiringTests
{
    [Fact]
    public void SwitchSettingsUiMode_RendersTheSidebarOnce_AfterTheSnap()
    {
        var body = ExtractMethod(WindowSource(), "private void SwitchSettingsUiMode(");
        Check.WithCustomMessage(
                "The ordered contract (spec §5.6): model -> snap -> ONE sidebar apply. Applying "
                + "the sidebar before the snap renders an empty sidebar (round-3 I4).")
            .That(body.IndexOf("ShowTab(")).IsStrictlyLessThan(body.IndexOf("ApplySidebarLayout("));
        Check.That(CountOccurrences(body, "RenderTabPage(")).IsEqualTo(0); // inside ApplySidebarLayout, not here
        Check.That(body).Contains("CurrentPage = 0");
    }

    [Fact]
    public void RebuildContent_FiltersPagesInSimpleMode()
    {
        var body = ExtractMethod(WindowSource(), "private void RebuildContent(");
        Check.WithCustomMessage(
                "The simple-mode filter must sit between BuildPages and the height repagination.")
            .That(body).Contains("SimpleSettingsFilter.Apply");
    }
}
```

Use the same `ExtractMethod`/`WindowSource` helpers (copy their invocations from `SettingsTabPaginationWiringTests`; `CountOccurrences` is a three-line local helper you write in this class).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeSwitchWiring"`
Expected: FAIL (no such method).

- [ ] **Step 3: Implement**

In `RebuildContent`, replace `_pages = BuildPages();` with:

```csharp
_pages = _settings.General.SimpleSettingsUi
    ? SimpleSettingsFilter.Apply(BuildPages())
    : BuildPages();
```

Add the switch (near `ShowTab`):

```csharp
/// <summary>
/// The mode switch, as one ordered contract (spec §5.6): flip + debounced save, disarm the
/// toggle (Task 11's GuardModeToggle is inserted HERE when it exists), reset the content page
/// (m5: landing mid-list of a differently-filtered list is disorienting), compute the layout,
/// snap (riding the normal CurrentTabName rebuild) or rebuild in place, then apply the sidebar
/// ONCE — after the snap — and re-label the toggle (Task 12).
/// </summary>
private void SwitchSettingsUiMode(bool simple)
{
    if (_settings.General.SimpleSettingsUi == simple) return;
    _settings.General.SimpleSettingsUi = simple;
    ApplyAndSave();

    _viewModel.Settings.CurrentPage = 0;
    var layout = ComputeSidebarLayout(_viewModel.Settings.CurrentTabName);
    if (layout.SnapTo is not null)
    {
        ShowTab(layout.SnapTo);                      // CurrentTabName -> RebuildContent (already filtered)
        _viewModel.ActiveTabId = layout.SnapTo;      // the sidebar must not contradict the content (F6)
    }
    else
    {
        RebuildContent();
    }
    ApplySidebarLayout(layout);
}
```

- [ ] **Step 4: Run to verify it passes, then the whole App suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeSwitchWiring"` then `dotnet test tests/EyeCtrl.App.Tests`
Expected: pass both (nothing calls `SwitchSettingsUiMode` yet; simple mode is unreachable — inert until T3).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SettingsModeSwitchWiringTests.cs
git commit -m "feat(settings): simple-mode filter step and the ordered mode-switch contract"
```

---

## Ticket T3 — header toggle

### Task 9: the toggle face — XAML host + `BuildModeToggle` seam

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml` (`HeaderPanel` `:92-102`)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (constants near `:123`; static seam beside `BuildModalButton` `:2291`; ctor insertion)
- Test: `tests/EyeCtrl.App.Tests/Localization/Fit/FitSeamPinTests.cs`; `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs`

**Interfaces:**
- Produces: `internal static Border BuildModeToggle(string text, Action onMouseDown, Dictionary<string, Border>, Dictionary<string, SolidColorBrush>)`; constants `ModeToggleId = "settings_mode"`, `ModeToggleHitRadius = 50`, `ModeToggleReleaseRadius = 85`, `ModeToggleWidth = 208`, `ModeToggleHeight = 48`; instance dicts `_modeToggleButtons`/`_modeToggleBrushes` — Tasks 10–12 consume all of these.

- [ ] **Step 1: Update the FitSeam pin (failing first)**

In `FitSeamPinTests.cs:114-119`, `TheSeamsHaveNoSecondPath` pins `MakeChromeButton`'s call count at 3 — change to 4 and extend the custom message with `"the 4th caller is the settings mode toggle (BuildModeToggle)"`. Add the routing pin beside the existing `MakeModalButton` one (`:57-65`):

```csharp
[Fact]
public void BuildModeToggle_BuildsTheFaceTheGateMeasures()
{
    var modal = ExtractMethod(WindowSource(), "internal static Border BuildModeToggle(");
    Check.WithCustomMessage("BuildModeToggle must build through MakeChromeButton — the fit gate measures this face.")
        .That(modal).Contains("MakeChromeButton(");

    var ctor = ExtractMethod(WindowSource(), "public SettingsWindow(");
    Check.WithCustomMessage("The window must insert the toggle through BuildModeToggle, not a second construction path.")
        .That(ctor).Contains("BuildModeToggle(");
}
```

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FitSeamPinTests"`
Expected: FAIL.

- [ ] **Step 2: XAML — the header grid**

Replace `HeaderPanel`'s StackPanel content with a two-column grid (keep `x:Name="HeaderPanel"`, `Height="112"`, `Margin="0,0,0,12"`, `Grid.Row="0"` — `ContentAvailableHeight` and `SettingsContentFitTests` read them; the hidden `Settings_Panel_Content` anchor stays in row 2):

```xml
<Grid x:Name="HeaderPanel" Grid.Row="0" Height="112" Margin="0,0,0,12"
      RowDefinitions="Auto,*" ColumnDefinitions="*,Auto">
    <TextBlock x:Name="HeaderTitle" Grid.Row="0" Grid.Column="0"
               Foreground="{StaticResource BrushTextPrimary}"
               FontSize="{StaticResource FontSizeDisplay}"
               FontWeight="{StaticResource FontWeightStrong}"/>
    <!-- The mode toggle's host: code builds the face (BuildModeToggle) and inserts it here.
         The toggle rides the TITLE LINE only (row 0); the description spans the FULL width in
         row 1 (ColumnSpan 2) — that is what the measured layout (103.9/112) assumes. Column
         Auto keeps the toggle's 208 DIP fixed; the title clips in its column (no wrap) — the
         fit class A208 bounds label+title for every shipped language (spec §5.5, L15). -->
    <Border x:Name="ModeToggleHost" Grid.Row="0" Grid.Column="1" VerticalAlignment="Top"/>
    <TextBlock x:Name="HeaderDescription" Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2"
               Foreground="{StaticResource BrushTextSecondary}"
               FontSize="{StaticResource FontSizeSubtitle}"
               TextWrapping="Wrap"
               Margin="0,8,0,0"/>
</Grid>
```

- [ ] **Step 3: Constants + seam + insertion**

Constants (near `PagBtnRadius`, `:123-153`):

```csharp
/// <summary>The settings mode toggle's dwell id. Registers under Pagination: the activation
/// dispatch sends every non-close Tab id to ShowTab, and Pagination's generic _pagActions arm
/// needs no ViewModel change (spec §5.5, L5).</summary>
internal const string ModeToggleId = "settings_mode";
/// <summary>Hit radius, DIP. 50 (the control standard), NOT PagBtnRadius's 60 — and NOT
/// RegisterPagerTarget, which hard-codes 60: the sibling RegisterModeToggleTarget owns this
/// constant so register and release can never disagree (spec L5/L16).</summary>
internal const double ModeToggleHitRadius = 50;
/// <summary>Release radius, DIP — deliberately larger than the hit radius (owner ruling
/// 2026-10-11): the measured top-right gaze bias (~80 px) sits near a 100 px circle's edge,
/// and hysteresis absorbs that wander while keeping "until the gaze leaves" (L16).</summary>
internal const double ModeToggleReleaseRadius = 85;
/// <summary>Fixed button width: 448 (narrowest header) − 220.2 (tab156's widest English title
/// at Display/Strong) − 12 (gap) − visual slack = 208. The A208 fit class enforces the label
/// inside it; SettingsContentFitTests enforces title+gap+this ≤ 448 live per language.</summary>
internal const double ModeToggleWidth = 208;
/// <summary>Button height. 48: the measured header fits (EN 103.9/112); 60 does not (115.9).
/// Vertical margin is 0 — the budget is "height + vertical margins ≤ 56" (round-3 m1).</summary>
internal const double ModeToggleHeight = 48;
```

Instance fields (beside `_pagButtons`):

```csharp
private readonly Dictionary<string, Border> _modeToggleButtons = new();
private readonly Dictionary<string, SolidColorBrush> _modeToggleBrushes = new();
```

Static seam (beside `BuildModalButton`, using the static `BtnBase`):

```csharp
/// <summary>
/// The settings mode toggle's face, through MakeChromeButton — the fit gate's seam (a
/// routing pin ties the instance path and the measurer to this builder). The UIA id goes on
/// the label TextBlock, not the Border: Border containers collapse in the UIA control view of
/// a transparent overlay (the tab-face precedent).
/// </summary>
internal static Border BuildModeToggle(
    string text, Action onMouseDown,
    Dictionary<string, Border> buttons, Dictionary<string, SolidColorBrush> brushes)
{
    var border = MakeChromeButton(
        ModeToggleId, text, ModeToggleWidth, ModeToggleHeight, new Thickness(0),
        BtnBase, Tokens.BrushTextPrimary, onMouseDown, buttons, brushes);
    var label = (TextBlock)border.Child;
    label.FontSize = Tokens.FontSizeTitle;               // RawGeometryLiteralTests: tokens only
    System.Windows.Automation.AutomationProperties.SetAutomationId(label, "Settings_ModeToggle");
    return border;
}
```

Ctor (after `BuildTabButtons();`, before `ShowTab("Cursor");`) — the MOUSE path only; the dwell path arrives in Task 11 and converges on the same flip:

```csharp
var toggle = BuildModeToggle(
    StringTable.Current.T("settings.mode.show.all"),
    OnModeToggleMouseDown, _modeToggleButtons, _modeToggleBrushes);
ModeToggleHost.Child = toggle;
```

The `settings.mode.show.all` key does not exist until Task 10 — so this ctor insertion lands with Task 10 Step 3 (the XAML host, seam, dicts, and pins land here; the keyed construction lands there). The routing pin's ctor assertion (`ctor contains BuildModeToggle(`) is satisfied when Task 10 lands.

```csharp
private void OnModeToggleMouseDown() =>
    SwitchSettingsUiMode(!_settings.General.SimpleSettingsUi);
```

- [ ] **Step 4: Update the header-fit mirror (failing first, then fix)**

`SettingsContentFitTests.EveryHeaderStringFits_TheHeadersFixedHeight` (`:975-1008`) mirrors `HeaderPanel` element-for-element. It now measures the old full-width StackPanel while production has a two-row header. Update its mirror to the production shape: a `Grid` with `RowDefinitions="Auto,*"` and `ColumnDefinitions="*,Auto"`; row 0 holds the title TextBlock (measured in its column, `448 − 208 − 12` DIP wide at the narrowest display, no wrap) and a placeholder `new Border { Width = SettingsWindow.ModeToggleWidth, Height = SettingsWindow.ModeToggleHeight }`; row 1 holds the description at the **full** header width (`ColumnSpan 2`). The measured height is `max(title line, 48) + 8 + description height`; keep asserting `<= declaredHeight` (112).

Add the horizontal companion beside it:

```csharp
[Fact]
public void TitlePlusModeToggle_FitsTheNarrowestHeaderWidth()
{
    var root = Xml();
    var contentArea = Margin(root, "ContentArea");
    var headerWidth = WindowWidthFor(NarrowDisplayWidthDip)
        - SidebarWidth(root) - contentArea.Left - contentArea.Right;   // 448
    var gap = 12.0;
    var widestTitle = SettingsTabDriftTests.RenderedTabHeaders()
        .Select(h => MeasureAtDisplay(h.label)).Max();
    Check.That(widestTitle + gap + SettingsWindow.ModeToggleWidth).IsLessOrEqualThan(headerWidth);
}
```

`MeasureAtDisplay` measures a `TextBlock` at `Tokens.FontSizeDisplay` + `FontWeightStrong` + `Tokens.FontStandard` — write it as a small local helper mirroring the file's existing measurement helper (it already sets `TextElement.SetFontFamily(panel, Tokens.FontStandard)`; reuse that idiom).

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsContentFitTests|FullyQualifiedName~FitSeamPinTests"`
Expected: pass.

- [ ] **Step 5: Full App suite + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests` — Expected: pass.

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Localization/Fit/FitSeamPinTests.cs tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs
git commit -m "feat(settings): mode toggle face — header host, BuildModeToggle seam, fit mirrors"
```

### Task 10: localization — keys + the `A208` fit class

**Files:**
- Modify: `src/EyeCtrl.Core/Localization/strings.json`
- Modify: `src/EyeCtrl.Core/Localization/StringTableSchema.cs:43-96`
- Modify: `tools/strings_validate.py:88-110`
- Modify: `tests/EyeCtrl.App.Tests/Localization/Fit/AppFitMeasurers.cs`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (the ctor insertion + `UpdateModeToggleLabel`, moved from Task 9's note)

**Interfaces:**
- Consumes: `BuildModeToggle` (Task 9).
- Produces: keys `settings.mode.show.all` / `settings.mode.show.basic`, fit class `A208`, `UpdateModeToggleLabel()` — Task 11's switch calls it.

- [ ] **Step 1: Add the strings.json entries**

Beside the other `settings.*` entries (alphabetical within the settings block; follow the file's existing ordering):

```json
"settings.mode.show.all": {
  "text": "All settings",
  "who": "user",
  "where": "settings",
  "fit": "A208",
  "args": []
},
"settings.mode.show.basic": {
  "text": "Basic",
  "who": "user",
  "where": "settings",
  "fit": "A208",
  "args": []
},
```

(The labels name the mode you switch TO — owner ruling, ledger L21. No German entries: the table is empty on main; T10's draft owns German.)

- [ ] **Step 2: Register the class in all three registries**

`StringTableSchema.FitClasses` (with a derivation comment):

```csharp
// The settings mode toggle's label: one line inside the toggle's fixed 208 DIP box
// (448 header − 220.2 tab156-widest-EN-title − 12 gap − slack). The live per-language
// title+toggle bound is pinned in SettingsContentFitTests, not restated here.
"A208",
```

`tools/strings_validate.py` `FIT_CLASSES` — add `"A208",` in the same position. `AppFitMeasurers.Measurers` map:

```csharp
["A208"] = (_, text) => ModeToggleLabel(text),
```

with the measurer (place it beside the A44 one, following the file's section comment style; use the file's existing `System.Windows.Controls`/`System.Windows.Media` usings):

```csharp
/// <summary>A208: the settings mode toggle's label, through the production BuildModeToggle
/// face: one line within the toggle's fixed box. Horizontal by construction — a one-line
/// label can never fail a height check, and the teeth test rejects a measurer that cannot
/// fail.</summary>
private static FitVerdict ModeToggleLabel(string text)
{
    var buttons = new Dictionary<string, System.Windows.Controls.Border>();
    var brushes = new Dictionary<string, System.Windows.Media.SolidColorBrush>();
    var border = SettingsWindow.BuildModeToggle(text, () => { }, buttons, brushes);
    var panel = new System.Windows.Controls.StackPanel();
    panel.Children.Add(border);
    using var host = FitHost.Open(panel, family: Tokens.FontStandard);
    var label = (System.Windows.Controls.TextBlock)border.Child;
    return OneLine(label, text, SettingsWindow.ModeToggleWidth,
        $"the toggle's {SettingsWindow.ModeToggleWidth} DIP box");
}
```

- [ ] **Step 3: Construct the toggle with the real key + add the label update**

In the window ctor (Task 9's host is in place):

```csharp
var toggle = BuildModeToggle(
    StringTable.Current.T("settings.mode.show.all"),
    OnModeToggleMouseDown, _modeToggleButtons, _modeToggleBrushes);
ModeToggleHost.Child = toggle;
```

And the label method (its own method — own-methods rule):

```csharp
/// <summary>Re-labels the toggle for the mode now active. The label names the mode you switch
/// TO. No live re-localization: the table activates once at startup, so a build-time resolve
/// is correct for the window's lifetime.</summary>
private void UpdateModeToggleLabel() =>
    ((TextBlock)_modeToggleButtons[ModeToggleId].Child).Text = StringTable.Current.T(
        _settings.General.SimpleSettingsUi ? "settings.mode.show.basic" : "settings.mode.show.all");
```

- [ ] **Step 4: Run the localization gates**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FitClassTests|FullyQualifiedName~StringsEnumParityTests|FullyQualifiedName~StringKeyCoverageTests|FullyQualifiedName~TextLiteralRatchetTests|FullyQualifiedName~PseudoLocaleGateTests"`
Expected: pass — the teeth test auto-runs for A208 (an over-long probe text fails the measurer); the pseudo gate measures "All settings" (~200 DIP pseudo) inside 208.

Also run the Python validator once: `python tools/strings_validate.py` (it must report no errors; it checks the new fit name and the entry shape).

- [ ] **Step 5: Full App suite + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests` — Expected: pass.

```bash
git add src/EyeCtrl.Core/Localization/strings.json src/EyeCtrl.Core/Localization/StringTableSchema.cs tools/strings_validate.py tests/EyeCtrl.App.Tests/Localization/Fit/AppFitMeasurers.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat(settings): mode toggle strings and the A208 horizontal fit class"
```

### Task 11: registration, hysteresis, wash, position push

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (own methods; `RearmPagerTarget` `:1291-1301`; `UpdateAllTargetPositions` `:2010-2047`; the `PagProgress` case `:602-608`)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleWiringTests.cs`

**Interfaces:**
- Consumes: constants + dicts (Task 9), `SwitchSettingsUiMode` (Task 8), `SettingsPagerRearmGuard` + `_pagerCenters` + `TryScreenCenter` (existing).
- Produces: `RegisterModeToggleTarget()`, `GuardModeToggle()`, `OnModeToggleActivated`, `UpdateModeToggleVisuals()`.

- [ ] **Step 1: Write the failing wiring + behavior pins**

```csharp
namespace EyeCtrl.App.Tests.Settings;

public class SettingsModeToggleWiringTests
{
    [Fact]
    public void ModeToggle_RegistersUnderPagination_AtItsOwnRadius_ThroughItsOwnMethod()
    {
        var body = ExtractMethod(WindowSource(), "private void RegisterModeToggleTarget(");
        Check.WithCustomMessage(
                "The toggle must NOT ride RegisterPagerTarget: that path hard-codes PagBtnRadius "
                + "= 60, and a 60-hit/50-release mismatch re-opens the oscillation (round-3 I3).")
            .That(body).Contains("DwellCategory.Pagination");
        Check.That(body).Contains("ModeToggleHitRadius");
        Check.That(body).Contains("IsDisarmed");
    }

    [Fact]
    public void ModeToggle_GuardUsesTheReleaseRadius_AndTheTrackedCentre()
    {
        var body = ExtractMethod(WindowSource(), "private void GuardModeToggle(");
        Check.That(body).Contains("ModeToggleReleaseRadius");
        Check.That(body).Contains("_pagerCenters");
    }

    [Fact]
    public void RearmPagerTarget_KnowsTheModeToggle()
    {
        var body = ExtractMethod(WindowSource(), "private void RearmPagerTarget(");
        Check.That(body).Contains("\"settings_mode\"");
    }

    [Fact]
    public void FrameLoop_PushesTheToggleWithoutALambda()
    {
        var body = ExtractMethod(WindowSource(), "private void UpdateAllTargetPositions(");
        Check.WithCustomMessage("No lambda may appear in the frame loop (allocation gate).")
            .That(body.Contains("=>")).IsFalse();
        Check.That(body).Contains("_modeToggleButtons, DwellCategory.Pagination");
    }

    [Fact]
    public void PagProgress_WashesTheToggleThroughItsOwnDictionary()
    {
        var body = ExtractMethod(WindowSource(), "case nameof(SettingsWindowViewModel.PagProgress)");
        Check.That(body.Contains("UpdateModeToggleVisuals")).IsTrue();
    }
}
```

(If `ExtractMethod` cannot key on a `case` label, extract `OnViewModelPropertyChanged` and assert it contains the third call — adjust the extraction target, not the assertion.)

Resting-gaze pin — add to `SettingsModeToggleWiringTests` a test modelled on `SettingsPagerRestingGazeTests.RestingGazeOnAJumpedButton_TurnsThePageExactlyOnce`: construct a `SettingsPagerRearmGuard` over a recording registrar, `Disarm("settings_mode", Pagination, center, LogicalToPhysicalX(85))`, then feed 200 gaze frames inside 85 DIP of the centre and assert the registrar saw ZERO registrations; then one frame outside and assert exactly one re-arm callback for `settings_mode`. Follow that file's fake-registrar and frame-feeding helpers directly.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeToggleWiring"`
Expected: FAIL.

Also the VM dispatch test (spec §6 "Toggle dispatch"): `SettingsWindowViewModel` IS constructible headlessly (unlike the window). In `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelDispatchTests.cs` (or the existing dispatch test class, wherever `SimulateActivated` is already tested):

```csharp
[Fact]
public void ModeToggleId_UnderPagination_DispatchesItsAction_NeverShowTab()
{
    var vm = /* construct as the existing dispatch tests do, with a SettingsViewModel */;
    var fired = false;
    vm.Register("settings_mode", DwellCategory.Pagination, radiusPhysical: 100, dwellMs: 700,
        () => fired = true);

    var handled = vm.SimulateActivated("settings_mode", DwellCategory.Pagination);

    Check.That(handled).IsTrue();
    Check.That(fired).IsTrue();
    Check.That(vm.Settings.CurrentTabName).IsEqualTo("");   // ShowTab never ran: the r1-C2 invariant
}
```

(This dispatch test passes the moment it is written — the generic arm already exists on main; it is a regression pin for the r1-C2 invariant, not a red-first test. The wiring pins above are the red-first part.)

- [ ] **Step 3: Implement (all in the toggle's own methods)**

```csharp
/// <summary>The dwell activation: the same flip the mouse path performs. Guarded by the
/// re-arm guard at the registration site, not here — the guard IS unregistration.</summary>
private void OnModeToggleActivated() =>
    SwitchSettingsUiMode(!_settings.General.SimpleSettingsUi);

/// <summary>Guard-aware registration at the toggle's OWN hit radius — never
/// RegisterPagerTarget (hard-coded 60; round-3 I3's trap). Consults the guard so a re-render
/// cannot silently re-arm a held toggle.</summary>
private void RegisterModeToggleTarget()
{
    if (_pagerRearmGuard.IsDisarmed(ModeToggleId)) return;
    _viewModel.Register(
        ModeToggleId, DwellCategory.Pagination,
        DpiHelper.LogicalToPhysicalX(ModeToggleHitRadius), _dwellMs, OnModeToggleActivated);
}

/// <summary>Disarms the toggle after a flip, at the RELEASE radius (85 — hysteresis over the
/// 50 hit radius; owner ruling, L16). The centre comes from the position loop's tracked value;
/// the direct border read is valid as a fallback because the toggle's Border is never
/// re-rendered (round-3 m10).</summary>
private void GuardModeToggle()
{
    if (_pagerCenters.TryGetValue(ModeToggleId, out var tracked))
    {
        _pagerRearmGuard.Disarm(ModeToggleId, DwellCategory.Pagination, tracked,
            DpiHelper.LogicalToPhysicalX(ModeToggleReleaseRadius));
        return;
    }
    if (_modeToggleButtons.TryGetValue(ModeToggleId, out var border)
        && TryScreenCenter(border, out var center))
        _pagerRearmGuard.Disarm(ModeToggleId, DwellCategory.Pagination, center,
            DpiHelper.LogicalToPhysicalX(ModeToggleReleaseRadius));
}

/// <summary>The toggle's dwell-progress wash: its own one-entry dictionary — never
/// _pagBrushes, which RenderPagination clears on every render (round-2 N4).</summary>
private void UpdateModeToggleVisuals()
{
    BlendProgress(_viewModel.PagProgress, _modeToggleBrushes, BtnBase, 0.4);
}
```

Wire-ins (each a one-line change in EXISTING methods — allowed, they are not the extracted toggle methods):

1. Ctor, after `ModeToggleHost.Child = toggle;` → `RegisterModeToggleTarget();`
2. `SwitchSettingsUiMode`, immediately after `ApplyAndSave();` → `GuardModeToggle();` and at the end → `UpdateModeToggleLabel();`
3. `RearmPagerTarget`'s switch → `case ModeToggleId: RegisterModeToggleTarget(); break;`
4. `UpdateAllTargetPositions`, after the `_pagButtons` line → `UpdateButtonPositions(_modeToggleButtons, DwellCategory.Pagination);`
5. The `PagProgress` case → add `UpdateModeToggleVisuals();` beside the two existing calls.
6. Mouse path: change the ctor construction to pass `OnModeToggleActivated` (dwell and mouse converge).

- [ ] **Step 4: Run**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeToggleWiring|FullyQualifiedName~SettingsPagerChromeTests|FullyQualifiedName~SettingsFrameLoopAllocationTests|FullyQualifiedName~DwellWashCensusTests"`
Expected: pass — `MakePagButton` still contains no `_viewModel.Register(`; the frame loop is lambda-free; the wash census tolerates the exempt file (accepted debt, L17).

- [ ] **Step 5: Full App suite + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests` — Expected: pass.

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleWiringTests.cs
git commit -m "feat(settings): mode toggle registration with 50/85 hysteresis, wash and push"
```

### Task 12: contrast + geometry pins

**Files:**
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleChromeTests.cs`

**Interfaces:**
- Consumes: the finished toggle (Tasks 9–11).

- [ ] **Step 1: Write the tests**

```csharp
namespace EyeCtrl.App.Tests.Settings;

/// <summary>
/// The toggle's chrome gates: the label-on-BtnBase contrast pair at rest and at the full dwell
/// wash (round-2 N6's restored pin), and the radius-50 clearance against the content pager at
/// the narrowest supported header. The 0.4 wash factor lands near 3.39:1, which only clears
/// the LARGE-text 3:1 bar — FontSizeTitle/FontWeightStrong must stay (they are the size/weight
/// under test).
/// </summary>
public class SettingsModeToggleChromeTests
{
    [StaFact]
    public void LabelContrast_AtRestAndFullWash_ClearsTheLargeTextBar()
    {
        var buttons = new Dictionary<System.Windows.Controls.Border>();
        var brushes = new Dictionary<System.Windows.SolidColorBrush>();
        var border = EyeCtrl.App.Views.SettingsWindow.BuildModeToggle(
            "All settings", () => { }, buttons, brushes);

        // rest: TextPrimary on SurfaceMedium (BtnBase)
        var rest = Contrast(ThemeText(), Tokens.SurfaceMedium);
        // full wash: TextPrimary blended 0.4 toward Accent is the BRUSH's colour; the label keeps
        // TextPrimary — measure against the washed base, the dwell-complete state.
        var washed = Blend(Tokens.SurfaceMedium, Tokens.Accent, 0.4);
        var washedContrast = Contrast(ThemeText(), washed);

        Check.That(rest).IsGreaterOrEqualThan(4.5);
        Check.That(washedContrast).IsGreaterOrEqualThan(3.0);   // large-text bar
    }

    [Fact]
    public void ToggleClears_TheContentPager_AtTheNarrowestHeader()
    {
        // Title-line centre y = 24 (top of header + half of 48). The content pager's centre sits
        // ~176 DIP down (header 112 + margin 12 + pager row 8 + half button 40). Vertical alone
        // clears; assert the Euclidean distance with the horizontal offset the narrowest width
        // forces (toggle right-aligned at 448, pager centred).
        double toggleY = 24, pagerY = 176;
        double toggleX = 448 - SettingsWindow.ModeToggleWidth / 2;
        double pagerX = 448 / 2;
        var distance = Math.Sqrt(Math.Pow(toggleX - pagerX, 2) + Math.Pow(toggleY - pagerY, 2));
        Check.That(distance).IsGreaterOrEqualThan(SettingsWindow.ModeToggleHitRadius + 60); // + PagBtnRadius
    }
}
```

Fill the helpers locally (they are small and self-contained):

```csharp
private static System.Windows.Media.Color ThemeText() => Tokens.TextPrimary;   // the label brush colour

private static double Luminance(System.Windows.Media.Color c)
{
    double F(double ch) { var s = ch / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
}

private static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
{
    var la = Luminance(a); var lb = Luminance(b);
    return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
}

private static System.Windows.Media.Color Blend(System.Windows.Media.Color baseC, System.Windows.Media.Color target, double t) =>
    System.Windows.Media.Color.FromRgb(
        (byte)(baseC.R + (target.R - baseC.R) * t),
        (byte)(baseC.G + (target.G - baseC.G) * t),
        (byte)(baseC.B + (target.B - baseC.B) * t));
```

(If `Tokens.TextPrimary` exposes a Brush rather than a Color, read `((SolidColorBrush)Tokens.BrushTextPrimary).Color` — mirror what `SettingsPageHeaderContrastTests` does for its pair.)

- [ ] **Step 2: Run + adjust thresholds only if a measured theme fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeToggleChromeTests"`
Expected: pass. If a theme's measured pair misses the bar, that is a FINDING, not a threshold to nudge: report it (the label brush/size is pinned by L5/L18).

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleChromeTests.cs
git commit -m "test(settings): mode toggle contrast and clearance pins"
```

### Task 13: full gate + owner-gated UI pass

**Files:**
- Test: `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs` (gated)

- [ ] **Step 1: Gate the UI test against the owner's real settings file**

`SettingsTests`' 15-tab test launches the real app via `--test-mode`, which reads `%APPDATA%/EyeCtrl/settings.json`. Add an isolation guard INSIDE the fixture usage of this test (and any new simple-mode test): back up the file, write `simpleSettingsUi: false`, run, restore in `finally`:

```csharp
var settingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "settings.json");
var backup = settingsPath + ".ui_test_backup";
var hadFile = File.Exists(settingsPath);
try
{
    if (hadFile) File.Copy(settingsPath, backup, overwrite: true);
    ForceSimpleSettingsUi(settingsPath, simple: false);
    // ... existing test body (15-tab expectation) ...
}
finally
{
    if (hadFile) File.Move(backup, settingsPath, overwrite: true);
    else if (File.Exists(settingsPath)) File.Delete(settingsPath);
}
```

`ForceSimpleSettingsUi` is a small local helper: read the JSON with `System.Text.Json` (`JsonNode.Parse`), set `["general"]["simpleSettingsUi"] = simple`, write back. If a simple-mode pass is added (find `Settings_ModeToggle`, dwell it, expect exactly the four `Settings_Tab_*` ids), it uses the same guard with `simple: true`.

- [ ] **Step 2: Full non-UI gate**

Run each separately, in order:

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
dotnet test tests/EyeCtrl.Platform.Tests
dotnet test tests/EyeCtrl.App.Tests
```

Expected: 0 failed everywhere. Also `python tools/strings_validate.py` and `python tools/palette_validate.py` (CI parity).

- [ ] **Step 3: ASK THE OWNER before the gated UI run**

The FlaUI set launches the real app and seizes the owner's only input channel. Ask; if approved:

```bash
EYECTRL_UI_TESTS=1 dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"
```

Expected: pass, including the 15-tab pin under the isolation guard.

- [ ] **Step 4: Final commit**

```bash
git add tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs
git commit -m "test(settings): UI tests isolate the owner's settings file for the mode flag"
```

---

## Self-review record

- **Spec coverage:** §5.1→Task 1; §5.2→Tasks 3/6/7; §5.3→Tasks 4/5; §5.4→Tasks 3/8; §5.5→Tasks 9–12; §5.6→Tasks 6–8/11; §5.7→Task 1 (+ declined wording, no work); §5.8→Tasks 7/13 (unchanged-by-construction gates re-run); §6→Tasks 1–13, with one substitution: the spec's "deep link in simple mode lands on the language row" behavioural test is covered by the model tests (General is in `SimpleTabIds`, never snappable-away) plus the `_visibleDefs` wiring pin — a window-level behavioural test is infeasible headless (round-2 N5) and the gated UI pass exercises the real path; §7 file table→all present; ledger L1–L21→L9 Task 3, L10 Task 8, L12/L18 Task 10, L15 Task 9, L16 Task 11, L21 Task 10, L20 Task 13.
- **Known judgment calls an executor must not re-decide:** `ModeToggleWidth = 208` (derivation in the constant's comment; the live test re-derives per language); the curation pin's expected sequences are finalized against the real build order in Task 5 Step 2; Task 9's keyed construction slides into Task 10 Step 3 (noted inline) so every task lands green.
- **Type consistency:** `SidebarLayout(VisibleIds, TabsPerPage, PageCount, PageIndex, Slice, PagerPresent, SnapTo)` defined Task 6, consumed verbatim Tasks 7/8; `BuildModeToggle(string, Action, Dictionary<string, Border>, Dictionary<string, SolidColorBrush>)` defined Task 9, consumed Tasks 10/12; constants defined Task 9, referenced by name thereafter; the ctor passes `OnModeToggleMouseDown` from Task 10 and Task 11 Step 3 point 6 converges mouse and dwell on `OnModeToggleActivated`.
```
