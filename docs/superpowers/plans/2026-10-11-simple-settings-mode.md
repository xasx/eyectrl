# Simple Settings Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the spec at `docs/superpowers/specs/2026-10-10-simple-settings-mode-design.md` (r4, `a9b74ecb`): a simple settings mode — a curated visibility subset (Cursor/Dwell/Keyboard/General, ~26 rows) over the existing spec system, switchable by a header toggle, with the current UI as an otherwise unchanged advanced mode.

**Architecture:** A `Simple` init flag on `SettingsRowSpec` + a pure `SimpleSettingsFilter` between `BuildPages()` and `MaybeRepaginateByHeight`; a pure `SettingsSidebarModel` that owns every sidebar decision (visible ids, pages, the snap target) so the window is a thin applier; a title-line chrome toggle in the header registered under the `Pagination` dwell category (generic dispatch, zero ViewModel changes) with a re-arm guard. Read the spec's §5 and §8 (ledger L1–L21) before starting; this plan implements them.

**Tech Stack:** .NET 10 WPF (`EyeCtrl.slnx`), CommunityToolkit.Mvvm, xUnit v3 + NFluent 3.1.0, StringTable localization with fit classes.

## Global Constraints

- **Worktree + cwd discipline:** everything happens in `C:\Users\andre\source\repos\eyectrl\.worktrees\simple-settings-mode`, branch `feat/simple-settings-mode`. The main checkout is dirty with the owner's files — never touch it. **The shell's cwd resets to the main checkout between commands**: prefix every command with `cd C:\Users\andre\source\repos\eyectrl\.worktrees\simple-settings-mode && ` (or use absolute paths), and run `git -C C:\Users\andre\source\repos\eyectrl\.worktrees\simple-settings-mode status --short` before every commit to confirm you are committing the worktree, not main.
- **One test project per `dotnet test` invocation; never two suites at once** (machine-global named pipes). Test output is German: "Fehler!" = FAIL, "Bestanden!" = pass.
- **Test helpers are private per class:** `ExtractMethod`, `WindowSource`, `Body`/`Source`, `FakeStartupRegistrar` — copy the needed helper into each new test class; do not assume a shared one. `SettingsTabPaginationWiringTests` (inside `Settings/SettingsTabPaginationTests.cs`) has `ExtractMethod` but no `WindowSource()` — copy the window-source loader from `SettingsPagerChromeTests.cs`. Tests that construct WPF elements are `[StaFact]`. New `EyeCtrl.App.Tests` files import `using System.IO;` explicitly (the WindowsDesktop SDK drops it from implicit usings).
- **NFluent only** for assertions (`Check.That(...)`); xUnit v3 for structure. Inclusive comparisons are `IsGreaterOrEqualThan`/`IsLessOrEqualThan`; unordered set checks use `IsOnlyMadeOf` + `HasSize` (never `ContainsExactly` on a `HashSet` — it pins enumeration order).
- **No UI literals:** every user-visible string is a `StringTable` key present in `src/EyeCtrl.Core/Localization/strings.json` with `who`/`where`/`fit` metadata. Keys are **literals at the call site**.
- **Insertion-scan caution:** `LanguageRowReachTests`, `SettingsTabDriftTests`, `SettingsTabLabelFitTests` anchor on the **first** `"_tabDefs ="` text in `SettingsWindow.xaml.cs`, comments included. Never write that token anywhere above the initializer.
- **Own-methods rule:** the toggle's registration/disarm/wash/label code lives in its own methods, never inside `RenderPagination`, `MakePagButton`, or `RegisterPagerTarget` (their bodies are regex-extracted by `SettingsPagerChromeTests`; `MakePagButton` must keep containing no `_viewModel.Register(`).
- **Lambda-free frame loop:** never add `=>` inside `UpdateAllTargetPositions` (`SettingsFrameLoopAllocationTests`).
- **Gaze-trap invariant:** a hidden control has no live hit circle. Every registration change ships with its unregister counterpart.
- **The app is never launched by non-gated tests.** The FlaUI set (`Category=UI` + `EYECTRL_UI_TESTS=1`) is owner-gated: Task 13 only, after asking.
- Advanced mode (the default) must stay bit-for-bit unchanged except the one header toggle (ledger L13) — including the `_viewModel.Register("close", …)` line at the end of `BuildTabButtons`, which no test pins: keep it.
- **Base:** the branch is rebased on `52b031ba` (= origin/main, 2026-10-11), which merged the T10 German draft: `src/EyeCtrl.App/Localization/strings.de.json` now EXISTS (673 entries) — the spec's "the German table is empty on main" was true at r4 and is stale now; harmless, because the German surface manifest is still `[]`. The two mode keys get their German entries in Task 10, AFTER the A208 re-class (the German `src` hash includes `fit`).

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

- [ ] **Step 4: Make the census exemption load-bearing, then run**

In the census test's arrange, the new pair only proves something if the flag is perturbed before `ResetAllSettings()` runs: set `settings.General.SimpleSettingsUi = true;` alongside the other non-default arrangements the census makes (the existing `notResetPropsByDesign` test perturbs each listed property — mirror how it does it for `UiLanguage`).

Run both projects (the property lives in Core, the capture in ViewModels):

```bash
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
```

Expected: all pass.

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
        // Unordered: IsOnlyMadeOf + size, never ContainsExactly (a HashSet's enumeration
        // order is an implementation detail).
        Check.That(SimpleSettingsFilter.SimpleTabIds).IsOnlyMadeOf("Cursor", "Dwell", "Keyboard", "General");
        Check.That(SimpleSettingsFilter.SimpleTabIds).HasSize(4);
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

### Task 5: curation pin + tab-set drift + reachability

**Files:**
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/SimpleModeCurationTests.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/SimpleModeReachabilityTests.cs`
- Modify: `tests/EyeCtrl.App.Tests/Settings/HandChunkedPageFitTests.cs` (internalize `AllSourceTypes`/`Construct` + the type-to-tab-id map for the drift test)

**Interfaces:**
- Consumes: `SimpleSettingsFilter` (Task 3), the curated rows (Task 4), `SupervisionComposition.Create` + `SupervisionEnabledWriter` (namespace `EyeCtrl.App.Services`; see `tests/EyeCtrl.App.Tests/Settings/Pages/GeneralPageSourceResetDriftTests.cs:38-60` for the live-service construction), `TestSettingsPageContext.Build`, `HandChunkedPageFitTests`' source-construction + placement machinery, `SettingsContentFitTests`' narrow-display budget derivations.

- [ ] **Step 1: Write the curation pin test**

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Pins the curated subset per tab as an ordered row sequence (kind + id for interactive rows;
/// LabelSpec twins pinned by a text predicate, their id is ""). General is built through ALL
/// THREE supervision branches, and Cursor through both ring-style branches, so conditional rows
/// cannot silently leave simple mode (spec §6, round-2 m4/m5; plan-review I6).
/// </summary>
public class SimpleModeCurationTests
{
    // The precedent fake, verbatim (GeneralPageSourceResetDriftTests.cs:98-102): the real
    // IStartupRegistrar is SetStartOnBoot(bool) + an IsRegistered property.
    private sealed class FakeStartupRegistrar : EyeCtrl.Core.Interfaces.IStartupRegistrar
    {
        public bool IsRegistered => true;
        public void SetStartOnBoot(bool enabled) { }
    }

    // General is NOT here: its rows depend on the supervision service, so it is built only in
    // the three-branch theory below (plan-review r2 N3).
    public static TheoryData<string> SimpleTabs() => new() { "Cursor", "Dwell", "Keyboard" };

    [Theory]
    [MemberData(nameof(SimpleTabs))]
    public void SimpleMode_ShowsExactlyTheCuratedSequence(string tab)
    {
        var settings = new EyeCtrlSettings();
        settings.General.UiLanguage = UiLanguage.German;            // pending-language note present
        settings.Cursor.RingStyle = CursorRingStyle.WeightedSandwich;
        var ctx = TestSettingsPageContext.Build(settings: settings);

        var rows = SimpleFilter(ctx, tab);

        Check.That(rows.Select(Describe)).ContainsExactly(ExpectedSequence(tab).ToArray());
    }

    [Fact]
    public void Cursor_AchromaticReticle_NoteIsSimple()
    {
        var settings = new EyeCtrlSettings
            { Cursor = { RingStyle = CursorRingStyle.AchromaticReticle } };
        var ctx = TestSettingsPageContext.Build(settings: settings);
        var rows = SimpleFilter(ctx, "Cursor");

        // Five entries: the picker's LabelSpec twin (12-char prefix of the resolved English
        // note "Circle Color is not used...") replaces the Pick row (plan-review r2 N3.4).
        Check.That(rows.Select(Describe))
            .ContainsExactly("Label:Circle Colo", "Combo:c_ring_style",
                "Slider:c_opacity", "Slider:c_size", "Button:c_reset");
    }

    public static TheoryData<string> SupervisionBranches() => new() { "connected", "disconnected", "unavailable" };

    [Theory]
    [MemberData(nameof(SupervisionBranches))]
    public void SimpleMode_GeneralBuildsEverySupervisionBranch(string branch)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_simplecur_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.General.UiLanguage = UiLanguage.German;
            var connected = branch == "connected";
            var installed = branch != "unavailable";
            if (installed)
            {
                settings.Supervision.Enabled = true;
                new SupervisionEnabledWriter(path).Persist(true);
            }
            var (_, toggle, _) = SupervisionComposition.Create(
                settings, path, new FakeStartupRegistrar(),
                enablePublishing: () => { },
                launchSupervisor: () => false,
                tryReachSupervisor: () => false,
                isSupervisorInstalled: () => installed,
                isLaunchRegistered: () => true,
                isSupervisorConnected: () => connected,
                runInBackground: work => work(),
                postToUi: work => work());
            var ctx = TestSettingsPageContext.Build(settings: settings, supervision: toggle);

            var rows = SimpleFilter(ctx, "General").Select(Describe).ToArray();

            // Disconnected adds exactly one row over connected: the retry button (the branch
            // difference IS the pin). Unavailable collapses to the note + no toggle.
            // FULL ordered sequences per branch — the exact-sequence form is what gives the
            // branch checks teeth (plan-review r2 N3.2/N3.3).
            var expected = new List<string> { "Combo:g_language", "Label:<pending-note prefix>",
                "Combo:g_theme", "Toggle:g_boot", "Toggle:g_pause", "Toggle:g_track" };
            if (branch == "unavailable")
                expected.AddRange(["Label:<unavailable-note prefix>", "Button:g_reset"]);
            else
            {
                expected.Add("Toggle:g_supervision");
                if (branch == "disconnected") expected.Add("Button:g_supervision_retry");
                expected.AddRange(["Label:<status prefix>", "Button:g_reset"]);
            }
            Check.That(rows).ContainsExactly(expected.ToArray());
            // The unavailable note resolves the key settings.general.supervision.unavailable —
            // pin its prefix explicitly; the pending-note prefix likewise (both resolved
            // against the embedded English table).
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SimpleTabIds_MatchReality_BothDirections()
    {
        // Spec §5.2/L9: every id in the set yields ≥1 Simple row; no other source has any.
        // Route: internalize HandChunkedPageFitTests' private AllSourceTypes()/Construct()
        // (this task edits that file — see Files), and map each source TYPE to its tab id
        // (ActionMenuPageSource -> "Actions", etc.; the map THROWS on an unmapped type so a
        // new source cannot silently skip the drift test).
        foreach (var (id, source) in HandChunkedPageFitTests.AllSources(_ => new TestSettingsPageContext()))
        {
            var hasSimple = source.Build().SelectMany(p => p.Rows).Any(r => r.Simple);
            if (SimpleSettingsFilter.SimpleTabIds.Contains(id))
                Check.WithCustomMessage($"{id} is in SimpleTabIds but yields no Simple row")
                    .That(hasSimple).IsTrue();
            else
                Check.WithCustomMessage($"{id} is not in SimpleTabIds but has a Simple row")
                    .That(hasSimple).IsFalse();
        }
    }

    [Fact]
    public void ResetGeneral_KeepsTheModeFlag()
    {
        // Spec §5.7 / plan-review I9: the General reset never touches the flag. Modelled on
        // ResetGeneral_KeepsTheLanguage in GeneralPageSourceResetDriftTests.
        var settings = new EyeCtrlSettings();
        settings.General.SimpleSettingsUi = true;
        var ctx = TestSettingsPageContext.Build(settings: settings, showModal: (_, act) => act());
        var reset = new GeneralPageSource(ctx).Build().SelectMany(p => p.Rows)
            .OfType<ButtonSpec>().Single(b => b.Id == "g_reset");

        reset.OnActivate();

        Check.That(settings.General.SimpleSettingsUi).IsTrue();
    }

    private static SettingsRowSpec[] SimpleFilter(SettingsPageContext ctx, string tab) =>
        SimpleSettingsFilter.Apply(SourceFor(ctx, tab).Build())
            .SelectMany(p => p.Rows).ToArray();

    private static ISettingsPageSource SourceFor(SettingsPageContext ctx, string tab) => tab switch
    {
        "Cursor" => new CursorPageSource(ctx),
        "Dwell" => new DwellPageSource(ctx),
        "Keyboard" => new KeyboardPageSource(ctx),
        "General" => new GeneralPageSource(ctx),
        _ => throw new ArgumentException($"Unknown simple tab {tab}")
    };

    /// <summary>Interactive rows pin as "Kind:id"; LabelSpec rows (id "") pin by kind + a text
    /// prefix — the sanctioned label-twin rule (round-1 I4, round-2 m4).</summary>
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

(`HandChunkedPageFitTests.AllSources()` is whatever its source-enumeration helper is actually called — the file enumerates every source with a constructible context; use that member and note its name here once confirmed. The last `Contains` in the supervision theory should be written as a plain `if (branch != "unavailable") Check.That(rows).Contains("Toggle:g_supervision");` — do that, not the null-predicate form sketched above.)

The `ExpectedSequence` lists — **membership comes from spec §5.3 and is never edited to match output; only the ORDER may be corrected to build order** (plan-review I6):

- Cursor (WeightedSandwich): `Pick:c_color`, `Combo:c_ring_style`, `Slider:c_opacity`, `Slider:c_size`, `Button:c_reset` — `c_opacity` builds before `c_size` (CursorPageSource.cs:79 vs :103).
- Dwell: `Slider:d_act`, `Slider:d_menu`, `Slider:d_tool`, `Slider:d_key`, `Slider:d_pred`, `Slider:d_rad`, `Toggle:d_cue_sound`, `Button:d_reset`.
- Keyboard: `Combo:k_layout`, `Slider:k_size`, `Toggle:k_pred`, `Combo:k_lang`, `Toggle:k_sound`, `Button:k_reset`.
- General lives ONLY in the supervision theory (plan-review r2 N3): disconnected = `Combo:g_language`, `Label:<pending-note prefix>`, `Combo:g_theme`, `Toggle:g_boot`, `Toggle:g_pause`, `Toggle:g_track`, `Toggle:g_supervision`, `Button:g_supervision_retry`, `Label:<status prefix>`, `Button:g_reset`; connected = the same minus the retry; unavailable = `…g_track`, `Label:<unavailable-note prefix — settings.general.supervision.unavailable>`, `Button:g_reset`. The headless table is the embedded English — pin label entries as `"Label:"` + a stable prefix of the resolved English text.

- [ ] **Step 2: Run, and correct ONLY the order**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SimpleModeCuration"`
Expected: may fail where the hand-written order mis-orders a row. Fix the expected ORDER to the sources' true build order. **Never delete an expected row because the output lacks it** — a missing row is a curation bug (usually: a `Simple = true` forgotten in Task 4), fix the source, not the pin.

- [ ] **Step 3: Write the reachability test**

In `SimpleModeReachabilityTests.cs`: for each simple tab, `Build → SimpleSettingsFilter.Apply → SettingsAutoPagination.Repaginate(...)`. Derive the budget the way `SettingsContentFitTests` does for the narrowest supported display — replicate its `ContentColumnWidth(root, 800)` and `ContentHeightBudget(root, 600)` arithmetic (the 800×600-DIP case; cite that file when copying) rather than inventing numbers. Assert every interactive row id in the filtered list appears in the output pages and `pages.Count >= 1`; for real teeth, reuse `HandChunkedPageFitTests`' placement/reach machinery (its `Place` + circle-in-cell checks) over the repaginated output rather than re-asserting `Repaginate`'s own partition property. `LabelSpec` rows have no dwell id and are ignored.

- [ ] **Step 4: Run both new test classes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SimpleMode"`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Settings/Pages/SimpleModeCurationTests.cs tests/EyeCtrl.App.Tests/Settings/SimpleModeReachabilityTests.cs tests/EyeCtrl.App.Tests/Settings/HandChunkedPageFitTests.cs
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
using EyeCtrl.App.Views;                    // SettingsWindow internals (TabContainerHeight etc.)

public class SettingsSidebarModelTests
{
    // PRODUCTION order, from SettingsWindow's _tabDefs initializer (verify against
    // SettingsTabDriftTests.RenderedTabDefs() when writing — do not hand-shuffle).
    private static readonly string[] All15 =
    [
        "Cursor", "Dwell", "Selection", "Actions", "Layout", "Keyboard", "Models", "Toolbar",
        "Calibration", "EyeCare", "Adaptation", "General", "QuickActions", "Advanced", "Diagnostics"
    ];

    private const double TallBudget = 1080 - 116 - 196 - 16;   // ~6 tabs per page at the owner's height
    private const double Slot = SettingsWindow.TabContainerHeight + 2 * SettingsWindow.TabButtonMargin;

    [Fact]
    public void Advanced_ShowsAllTabs_WithPager_NoSnap()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: false, TallBudget, Slot, "Adaptation");

        Check.That(layout.VisibleIds).HasSize(15);
        Check.That(layout.PagerPresent).IsTrue();
        Check.That(layout.SnapTo).IsNull();
        Check.That(layout.Slice.Count).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Simple_HidesNonCuratedTabs_SnapsToTheLeader_AtThePostSnapPage()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: true, TallBudget, Slot, "Adaptation");

        Check.That(layout.VisibleIds).HasSize(4);
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
        var generalIndex = Array.IndexOf(All15, "General");
        Check.That(layout.PageIndex).IsEqualTo(generalIndex / layout.TabsPerPage);
    }

    [Fact]
    public void Simple_OnShortScreens_StillPaginates()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: true, sidebarBudget: 320, Slot, "General");

        Check.That(layout.PageCount).IsStrictlyGreaterThan(1);  // ~3 tabs/page at this budget
        Check.That(layout.PagerPresent).IsTrue();
    }

    [Fact]
    public void Simple_GeneralIsAlwaysVisible_TheDeepLinkTarget()
    {
        // Spec L14's model-side half: General is simple-tier, so the language deep link's
        // lookup can never miss in either mode.
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: true, TallBudget, Slot, "General");
        Check.That(layout.VisibleIds).Contains("General");
        Check.That(layout.SnapTo).IsNull();
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
        var snapTo = visible.Contains(currentTab) || visible.Length == 0
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
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (regions: fields near `:342`, `BuildTabButtons` `:750-781`, `RenderTabPage` `:793-806`, `ShowLanguageRow` `:994-1001`)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsTabPaginationTests.cs` (contains the `SettingsTabPaginationWiringTests` class — there is no separate file; adding one would duplicate the type)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsDeepLinkTests.cs`

**Interfaces:**
- Consumes: `SettingsSidebarModel` (Task 6), `SimpleSettingsFilter.SimpleTabIds` (Task 3).
- Produces: window fields/methods Task 8 and Task 11 rely on — `_visibleDefs` array, `ComputeSidebarLayout(currentTab)`, `ApplySidebarLayout(layout)`, `RebuildTabPagerChrome()`.

- [ ] **Step 1: Add the wiring pins first (regression guards — they are green on contact; their value is future-facing)**

Into `SettingsTabPaginationWiringTests` (inside `Settings/SettingsTabPaginationTests.cs`; it has a private `ExtractMethod` but no `WindowSource()` — copy the window-source loader from `SettingsPagerChromeTests.cs` into this class as a private helper):

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
public void BuildTabButtons_NeverFiltersTheFaceLoop()
{
    // Round-2 N1's real home: filtering the face loop crashes simple-to-advanced with
    // KeyNotFoundException (the process dies with no Supervisor present). The loop's literal
    // foreach is already green today; these Not-Contains are the teeth — no filter may creep in.
    var body = ExtractMethod(WindowSource(), "private void BuildTabButtons(");
    Check.WithCustomMessage("The face loop must not read the visible defs.")
        .That(body).Not.Contains("_visibleDefs");
    Check.WithCustomMessage("The face loop must not read the simple set.")
        .That(body).Not.Contains("SimpleTabIds");
    Check.WithCustomMessage("The face loop must not read the mode flag.")
        .That(body).Not.Contains("SimpleSettingsUi");
    Check.WithCustomMessage("No 'if (!visible) continue;' filter may be added to the face loop.")
        .That(body).Not.Contains("continue");
}

[Fact]
public void BuildTabButtons_RegistersTheCloseButton()
{
    // Plan-review I4: no test anywhere pins this; an executor "rewiring the tail" must not
    // drop it — the gaze-only owner would lose the only dwell that closes Settings.
    var body = ExtractMethod(WindowSource(), "private void BuildTabButtons(");
    Check.That(body).Contains("Register(\"close\"");
}

[Fact]
public void ComputeSidebarLayout_FeedsTheRealBudgetAndSlot()
{
    // Plan-review I7: the page math must still derive from the live sidebar budget, not a
    // constant smuggled through the model call.
    var body = ExtractMethod(WindowSource(), "private SettingsSidebarModel.SidebarLayout ComputeSidebarLayout(");
    Check.That(body).Contains("SidebarTabBudget");
    Check.That(body).Contains("TabContainerHeight + 2 * TabButtonMargin");
}

[Fact]
public void ModelCompute_UsesTheChunker()
{
    // Rooted at the repo root like every source scan in this class (the test host's cwd is
    // the bin directory — a relative path throws FileNotFoundException). Green on contact:
    // the model exists from Task 6; this pin guards future rewrites (plan-review r2 N2).
    var body = File.ReadAllText(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "Settings", "SettingsSidebarModel.cs"));
    Check.WithCustomMessage("SettingsSidebarModel.Compute must derive paging from SettingsTabPagination.")
        .That(body).Contains("SettingsTabPagination.TabsPerPage");
}
```

(`WindowSource()` is the copied loader; `File` needs `using System.IO;` — that file already imports it.)

Update the existing `BuildTabButtons_SizesPagesFromTheChunker` pin (`:79`): it expects `SettingsTabPagination.TabsPerPage` inside `BuildTabButtons`; after this task the call routes through `ComputeSidebarLayout` — change the expected literal to `ComputeSidebarLayout(` (the budget/slot pins above carry the original intent).

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsTabPaginationWiringTests"`
Expected: the behavior pins (`RenderTabPage_SlicesTheVisibleDefs`, `ComputeSidebarLayout_FeedsTheRealBudgetAndSlot`) FAIL; the guard pins (`NeverFiltersTheFaceLoop`, `RegistersTheCloseButton`, `ModelCompute_UsesTheChunker`) are green on contact — they exist to fail the day someone breaks them. Note: `ExtractMethod` slices to the next `\n    }`, so the expression-bodied `ComputeSidebarLayout`'s slice spills into `ApplySidebarLayout` — harmless today (both bodies contain what the pin wants); if `ExtractMethod` is ever tightened, revisit this pin, do not delete it (plan-review r2 m-f).

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

Rewire `BuildTabButtons`'s tail (the face loop over `_tabDefs` stays untouched, and the closing `_viewModel.Register("close", DwellCategory.Tab, …)` line at the end STAYS — only the four paging lines between them are replaced):

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

- `Settings/SettingsDeepLinkTests.cs` around `:233`/`:234`: its `rowBody` extraction of `ShowLanguageRow` expects `Array.FindIndex(_tabDefs, t => t.Id == "General")` — update the expected literal to `_visibleDefs`. The `PageOf` line below it stays unchanged (`_tabsPerPage` is model-fed — a recorded deviation from the spec's "both pins move"; see the self-review record); if the test asserts both lines in one string, keep only the `FindIndex` expectation changed.
- `SettingsTabPaginationWiringTests.RenderTabPage_UnregistersTabsItHides` (existing) stays green — the unregister-all loop is untouched.

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsDeepLinkTests|FullyQualifiedName~SettingsTabPaginationWiringTests|FullyQualifiedName~SettingsTabDriftTests|FullyQualifiedName~SettingsTabLabelFitTests|FullyQualifiedName~LanguageRowReachTests"`
Expected: pass (advanced default → `_visibleDefs` == `_tabDefs`; the drift/label-fit regexes still find the initializer first).

- [ ] **Step 4: Run the whole App suite**

Run: `dotnet test tests/EyeCtrl.App.Tests`
Expected: all pass — advanced mode's behavior is identical; nothing consumes `SimpleSettingsFilter` in the pipeline yet.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SettingsTabPaginationTests.cs tests/EyeCtrl.App.Tests/Settings/SettingsDeepLinkTests.cs
git commit -m "refactor(settings): window applies the pure sidebar model; deep link rides visible defs"
```

### Task 8: filter step + `SwitchSettingsUiMode`

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (`RebuildContent` `:1076-1090`; new methods near `ShowTab`)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsModeSwitchWiringTests.cs` (new file; `using System.IO;` for the copied `WindowSource`)

**Interfaces:**
- Consumes: `SimpleSettingsFilter.Apply` (Task 3), `ApplySidebarLayout`/`ComputeSidebarLayout` (Task 7).
- Produces: `SwitchSettingsUiMode(bool simple)` — Task 11's toggle calls it. `UpdateModeToggleLabel` AND its call site (the final line of `SwitchSettingsUiMode`) both land in Task 10 — do NOT call a method that does not exist yet.

- [ ] **Step 1: Write the failing wiring pins**

```csharp
namespace EyeCtrl.App.Tests.Settings;

using System.IO;

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
        Check.That(body).Contains("ActiveTabId = layout.SnapTo");   // the F6 sidebar-must-not-contradict pin
    }

    [Fact]
    public void RebuildContent_FiltersPagesInSimpleMode()
    {
        var body = ExtractMethod(WindowSource(), "private void RebuildContent(");
        Check.WithCustomMessage(
                "The simple-mode filter must sit between BuildPages and the height repagination.")
            .That(body).Contains("SimpleSettingsFilter.Apply");
    }

    [Fact]
    public void ApplySidebarLayout_AssignsTheModelsPage_RendersOnce()
    {
        var body = ExtractMethod(WindowSource(), "private void ApplySidebarLayout(");
        Check.WithCustomMessage(
                "The applier must assign the model's post-snap page (a stale _tabPageIndex is "
                + "the empty-sidebar regression, round-3 I4).")
            .That(body).Contains("_tabPageIndex = layout.PageIndex");
        Check.WithCustomMessage("Exactly one RenderTabPage in the applier.")
            .That(CountOccurrences(body, "RenderTabPage(")).IsEqualTo(1);
    }

    [Fact]
    public void RebuildTabPagerChrome_ClearsBeforeItBuilds()
    {
        var body = ExtractMethod(WindowSource(), "private void RebuildTabPagerChrome(");
        Check.WithCustomMessage(
                "BuildTabPagePager only appends — the panel must be cleared first (round-2 I3).")
            .That(body.IndexOf("TabPagerPanel.Children.Clear()"))
            .IsStrictlyLessThan(body.IndexOf("BuildTabPagePager("));
    }
}
```

Copy `ExtractMethod` + `WindowSource` from `SettingsPagerChromeTests.cs` into this class (they are private per class — Global Constraints); `CountOccurrences` is a three-line local helper.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeSwitchWiring"`
Expected: FAIL (no such methods).

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
/// toggle (Task 11's GuardModeToggle is inserted HERE when it exists), compute the layout, snap
/// (riding the normal CurrentTabName rebuild, which resets the page) or rebuild in place with
/// the page reset AFTER the rebuild (m5: landing mid-list of a differently-filtered list is
/// disorienting), then apply the sidebar ONCE — after the snap — and re-label the toggle (Task 10).
/// </summary>
private void SwitchSettingsUiMode(bool simple)
{
    if (_settings.General.SimpleSettingsUi == simple) return;
    _settings.General.SimpleSettingsUi = simple;
    ApplyAndSave();

    var layout = ComputeSidebarLayout(_viewModel.Settings.CurrentTabName);
    if (layout.SnapTo is not null)
    {
        ShowTab(layout.SnapTo);                      // CurrentTabName -> RebuildContent; ShowTab resets the page
        _viewModel.ActiveTabId = layout.SnapTo;      // the sidebar must not contradict the content (F6)
    }
    else
    {
        RebuildContent();
        if (_viewModel.Settings.CurrentPage != 0)
            _viewModel.Settings.CurrentPage = 0;     // m5; the property handler re-renders the
                                                     // page — no manual RenderCurrentPage (that
                                                     // rendered twice, plan-review r2 m-b)
    }
    ApplySidebarLayout(layout);
}
```

Recorded deviation (spec §5.6 wording): the model's "pager register/unregister diff" output is realized as an **unconditional teardown** in `RebuildTabPagerChrome` (unregister + clear, then build only when >1 page) — equivalent for the two ids the diff covered, and idempotent on the advanced path (`AddTarget` replaces; see DwellDetector).

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

### Task 9: the toggle face — XAML host + `BuildModeToggle` seam + strings

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml` (`HeaderPanel` `:92-102`)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (constants near `:123`; static seam beside `BuildModalButton` `:2291`; ctor insertion)
- Modify: `src/EyeCtrl.Core/Localization/strings.json` (two entries, temporarily classed `row` — Task 10 re-classes them `A208`)
- Test: `tests/EyeCtrl.App.Tests/Localization/Fit/FitSeamPinTests.cs`; `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs`

**Interfaces:**
- Produces: `internal static Border BuildModeToggle(string text, Action onMouseDown, Dictionary<string, Border>, Dictionary<string, SolidColorBrush>)`; constants `ModeToggleId = "settings_mode"`, `ModeToggleHitRadius = 50`, `ModeToggleReleaseRadius = 85`, `ModeToggleWidth = 208`, `ModeToggleHeight = 48`; instance dicts `_modeToggleButtons`/`_modeToggleBrushes` — Tasks 10–12 consume all of these.

- [ ] **Step 1: Update the FitSeam pin (failing first)**

In `FitSeamPinTests.cs:114-119`, `TheSeamsHaveNoSecondPath` pins `MakeChromeButton`'s call count at 3 — change to 4 and extend the custom message with `"the 4th caller is the settings mode toggle (BuildModeToggle)"`. Add the routing pin beside the existing `MakeModalButton` one (`:57-65`), using THIS file's own helper idiom (`Body(Source("Views", "SettingsWindow.xaml.cs"), ...)` — this file strips comments and has NO `ExtractMethod`/`WindowSource`):

```csharp
[Fact]
public void BuildModeToggle_BuildsTheFaceTheGateMeasures()
{
    var seam = Body(Source("Views", "SettingsWindow.xaml.cs"), "internal static Border BuildModeToggle(");
    Check.WithCustomMessage("BuildModeToggle must build through MakeChromeButton — the fit gate measures this face.")
        .That(seam).Contains("MakeChromeButton(");

    var ctor = Body(Source("Views", "SettingsWindow.xaml.cs"), "public SettingsWindow(");
    Check.WithCustomMessage("The window must insert the toggle through BuildModeToggle, not a second construction path.")
        .That(ctor).Contains("BuildModeToggle(");
}
```

(Adapt to the exact helper names in that file — mirror how the `MakeModalButton` pin extracts its two sites.)

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FitSeamPinTests"`
Expected: FAIL.

- [ ] **Step 2: XAML — the two-row header grid, with the toggle's left margin**

Replace `HeaderPanel`'s StackPanel content with a two-row grid (keep `x:Name="HeaderPanel"`, `Height="112"`, `Margin="0,0,0,12"`, `Grid.Row="0"` — `ContentAvailableHeight` and `SettingsContentFitTests` read them; the hidden `Settings_Panel_Content` anchor stays in row 2):

```xml
<Grid x:Name="HeaderPanel" Grid.Row="0" Height="112" Margin="0,0,0,12"
      RowDefinitions="Auto,*" ColumnDefinitions="*,Auto">
    <TextBlock x:Name="HeaderTitle" Grid.Row="0" Grid.Column="0"
               Foreground="{StaticResource BrushTextPrimary}"
               FontSize="{StaticResource FontSizeDisplay}"
               FontWeight="{StaticResource FontWeightStrong}"/>
    <!-- The mode toggle's host: code builds the face (BuildModeToggle) and inserts it here.
         The toggle rides the TITLE LINE only (row 0); the description spans the FULL width in
         row 1 (ColumnSpan 2) — that is what the measured layout (103.9/112) assumes. The
         12-DIP left margin IS the title-to-toggle gap the fit arithmetic uses — a marginless
         host would make the mirror measure a gap production does not have (plan-review I10).
         Column Auto keeps the toggle's 208 DIP fixed; the title clips in its column (no
         wrap) — the A208 class bounds label+title for shipped languages (spec L15). -->
    <Border x:Name="ModeToggleHost" Grid.Row="0" Grid.Column="1" Margin="12,0,0,0" VerticalAlignment="Top"/>
    <TextBlock x:Name="HeaderDescription" Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2"
               Foreground="{StaticResource BrushTextSecondary}"
               FontSize="{StaticResource FontSizeSubtitle}"
               TextWrapping="Wrap"
               Margin="0,8,0,0"/>
</Grid>
```

- [ ] **Step 3: Constants + seam + keyed strings + ctor insertion**

Constants (near `PagBtnRadius`, `:123-153`):

```csharp
/// <summary>The settings mode toggle's dwell id. Registers under Pagination: the activation
/// dispatch sends every non-close Tab id to ShowTab, and Pagination's generic _pagActions arm
/// needs no ViewModel change (spec S5.5, L5).</summary>
internal const string ModeToggleId = "settings_mode";
/// <summary>Hit radius, DIP. 50 (the control standard), NOT PagBtnRadius's 60 — and NOT
/// RegisterPagerTarget, which hard-codes 60: the sibling RegisterModeToggleTarget owns this
/// constant so register and release can never disagree (spec L5/L16).</summary>
internal const double ModeToggleHitRadius = 50;
/// <summary>Release radius, DIP — deliberately larger than the hit radius (owner ruling
/// 2026-10-11): the measured top-right gaze bias (~80 px) sits near a 100 px circle's edge,
/// and hysteresis absorbs that wander while keeping "until the gaze leaves" (L16).</summary>
internal const double ModeToggleReleaseRadius = 85;
/// <summary>Fixed button width: 448 (narrowest header) - 220.2 (tab156's widest English title
/// at Display/Strong) - 12 (gap) - visual slack = 208. The A208 fit class enforces the label
/// inside it minus the standard 8-DIP one-line inset; SettingsContentFitTests enforces
/// title+gap+this <= 448 live per language. NOTE: this leaves only ~7.8 DIP of title room at
/// the narrowest display — any T10+ tab title growth eats it immediately; the fit tests
/// police it (plan-review m13).</summary>
internal const double ModeToggleWidth = 208;
/// <summary>Button height. 48: the measured header fits (EN 103.9/112); 60 does not (115.9).
/// Vertical margin is 0 — the budget is "height + vertical margins <= 56" (round-3 m1).</summary>
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

The two strings.json entries land NOW (temporarily classed `row`, re-classed `A208` in Task 10 — a `fit` value must name an existing class, and A208 does not exist yet):

```json
"settings.mode.show.all": {
  "text": "All settings",
  "who": "user",
  "where": "settings",
  "fit": "row",
  "args": []
},
"settings.mode.show.basic": {
  "text": "Basic",
  "who": "user",
  "where": "settings",
  "fit": "row",
  "args": []
},
```

(The labels name the mode you switch TO — owner ruling, ledger L21. No German entries: the table is empty on main; T10's draft owns German.)

Ctor (after `BuildTabButtons();`, before `ShowTab("Cursor");`) — the MOUSE path only; the dwell path arrives in Task 11 and converges on the same flip. `ModeToggleLabelKey` is added by Task 10; to keep THIS task green, inline the ternary here and Task 10 replaces it with the helper call:

```csharp
var toggle = BuildModeToggle(
    StringTable.Current.T(_settings.General.SimpleSettingsUi
        ? "settings.mode.show.all" : "settings.mode.show.basic"),
    OnModeToggleMouseDown, _modeToggleButtons, _modeToggleBrushes);
ModeToggleHost.Child = toggle;
```

```csharp
private void OnModeToggleMouseDown() =>
    SwitchSettingsUiMode(!_settings.General.SimpleSettingsUi);
```

- [ ] **Step 4: Update the header-fit mirror (failing first, then fix)**

`SettingsContentFitTests.EveryHeaderStringFits_TheHeadersFixedHeight` (`:975-1008`) mirrors `HeaderPanel` element-for-element. Update its mirror to the production shape: a `Grid` with `RowDefinitions="Auto,*"` and `ColumnDefinitions="*,Auto"`; row 0 holds the title TextBlock in its column (width = header width minus `ModeToggleWidth` minus the host Margin **read from the XAML** (12) — not a restated constant) and the **real** `SettingsWindow.BuildModeToggle(...)` face (not a constant-sized placeholder — a future vertical margin on the face is then measured); row 1 holds the description at the **full** header width (`ColumnSpan 2`). The measured height is the laid-out grid height; keep asserting `<= declaredHeight` (112).

Add the horizontal companion beside it (`RenderedTabHeaders()` returns `(string Label, string Description)` — `h.Label`, not `h.label`; `[StaFact]` — this file's WPF precedent):

```csharp
[StaFact]
public void TitlePlusModeToggle_FitsTheNarrowestHeaderWidth()
{
    var root = Xml();
    var contentArea = Margin(root, "ContentArea");
    var headerWidth = WindowWidthFor(NarrowDisplayWidthDip)
        - SidebarWidth(root) - contentArea.Left - contentArea.Right;   // 448
    var gap = Margin(root, "ModeToggleHost").Left;
    var widestTitle = SettingsTabDriftTests.RenderedTabHeaders()
        .Select(h => MeasureAtDisplay(h.Label)).Max();
    Check.That(widestTitle + gap + SettingsWindow.ModeToggleWidth).IsLessOrEqualThan(headerWidth);
}
```

`MeasureAtDisplay` measures a `TextBlock` at `Tokens.FontSizeDisplay` + `FontWeightStrong` + `Tokens.FontStandard` — write it as a small local helper mirroring the file's existing measurement idiom (`TextElement.SetFontFamily(panel, Tokens.FontStandard)`).

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsContentFitTests|FullyQualifiedName~FitSeamPinTests"`
Expected: pass.

- [ ] **Step 5: Full App suite + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests` — Expected: pass.

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.Core/Localization/strings.json tests/EyeCtrl.App.Tests/Localization/Fit/FitSeamPinTests.cs tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs
git commit -m "feat(settings): mode toggle face — header host, BuildModeToggle seam, keyed strings, fit mirrors"
```

### Task 10: the label mapping + the `A208` fit class

**Files:**
- Modify: `src/EyeCtrl.Core/Localization/strings.json` (re-class the two entries `row` -> `A208`)
- Modify: `src/EyeCtrl.Core/Localization/StringTableSchema.cs:43-96`
- Modify: `tools/strings_validate.py:88-110`
- Modify: `tests/EyeCtrl.App.Tests/Localization/Fit/AppFitMeasurers.cs`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (`ModeToggleLabelKey` + `UpdateModeToggleLabel` + the call site in `SwitchSettingsUiMode`; the ctor ternary -> helper call)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleLabelTests.cs` (new)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsModeSwitchWiringTests.cs` (Task 8's file — gains the re-label order pin)
- Modify: `src/EyeCtrl.App/Localization/strings.de.json` (the two German entries, AFTER the re-class)

**Interfaces:**
- Consumes: `BuildModeToggle` (Task 9), `SwitchSettingsUiMode` (Task 8).
- Produces: `internal static string ModeToggleLabelKey(bool simple)`, `UpdateModeToggleLabel()`, fit class `A208`. Task 10 ALSO wires the re-label call into `SwitchSettingsUiMode` — this task owns the call site (plan-review r2 N1: r2 defined the method and never called it).

- [ ] **Step 1: Write the failing label-key test (plan-review C1 — the mapping was inverted in this plan's first draft)**

```csharp
namespace EyeCtrl.App.Tests.Settings;

using System.IO;

public class SettingsModeToggleLabelTests
{
    [Fact]
    public void LabelKey_NamesTheModeYouSwitchTo()
    {
        // Owner ruling L21: "All settings" while IN simple mode (it offers the full mode);
        // "Basic" while IN advanced. The label names the TARGET.
        Check.That(SettingsWindow.ModeToggleLabelKey(simple: true)).IsEqualTo("settings.mode.show.all");
        Check.That(SettingsWindow.ModeToggleLabelKey(simple: false)).IsEqualTo("settings.mode.show.basic");
    }

    [Fact]
    public void UpdateModeToggleLabel_RoutesThroughTheKeyHelper()
    {
        var label = ExtractMethod(WindowSource(), "private void UpdateModeToggleLabel(");
        Check.WithCustomMessage("The re-label must route through ModeToggleLabelKey — the one tested mapping.")
            .That(label).Contains("ModeToggleLabelKey(");
    }
}
```

(`ExtractMethod`/`WindowSource` are the copied private helpers, per Global Constraints; `using System.IO;` for the source read.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeToggleLabelTests"`
Expected: FAIL (no `ModeToggleLabelKey`).

- [ ] **Step 3: Implement the mapping and the re-label**

In `SettingsWindow.xaml.cs`:

```csharp
/// <summary>
/// The key for the toggle's label in the given mode. The label names the mode you switch TO:
/// "All settings" while simple (it offers the full mode), "Basic" while advanced (owner
/// ruling, ledger L21 — "Advanced" collided with the Advanced tab in EN and DE). Both keys
/// are literals: the coverage and unused-key gates read literals.
/// </summary>
internal static string ModeToggleLabelKey(bool simple) =>
    simple ? "settings.mode.show.all" : "settings.mode.show.basic";

/// <summary>Re-labels the toggle for the mode now active. No live re-localization: the table
/// activates once at startup, so a build-time resolve is correct for the window's lifetime.</summary>
private void UpdateModeToggleLabel() =>
    ((TextBlock)_modeToggleButtons[ModeToggleId].Child).Text =
        StringTable.Current.T(ModeToggleLabelKey(_settings.General.SimpleSettingsUi));
```

And replace the ctor's inlined ternary (Task 9 Step 3) with the helper:

```csharp
StringTable.Current.T(ModeToggleLabelKey(_settings.General.SimpleSettingsUi))
```

**The re-label call site (plan-review r2 N1 — the r2 plan defined the method and never called it):** append `UpdateModeToggleLabel();` as the FINAL statement of `SwitchSettingsUiMode` (Task 8's method), after `ApplySidebarLayout(layout);`. Pin it in `SettingsModeSwitchWiringTests`:

```csharp
[Fact]
public void SwitchSettingsUiMode_RelabelsAfterTheSidebarApply()
{
    var body = ExtractMethod(WindowSource(), "private void SwitchSettingsUiMode(");
    Check.WithCustomMessage(
            "The switch must re-label the toggle AFTER applying the sidebar — the label names "
            + "the mode now active (L21).")
        .That(body.IndexOf("ApplySidebarLayout("))
        .IsStrictlyLessThan(body.IndexOf("UpdateModeToggleLabel()"));
}
```

This completes the C1 fix: the window opens with the mode-correct label, and every flip re-labels correctly — pinned, not promised.

- [ ] **Step 4: Re-class the strings and register A208 everywhere**

In `strings.json`, change both entries' `"fit"` from `"row"` to `"A208"`. THEN add the two German entries to `src/EyeCtrl.App/Localization/strings.de.json` (the German table exists on this base — 673 entries; order matters because the German `src` hash includes `fit`): `"settings.mode.show.all": "Alle Einstellungen"`, `"settings.mode.show.basic": "Einfach"` — target-mode naming per L21; if the T10 draft's established terminology differs, follow it, but the label must fit the A208 room ("Grundeinstellungen" also fits; verify with the measurer). Inert until the German manifest lists the settings surface (it is still `[]` on this base).

`StringTableSchema.FitClasses` (with the derivation comment):

```csharp
// The settings mode toggle's label: one line inside the toggle's box less the standard
// 8-DIP one-line inset — i.e. <= 208 - 8. 208 derives from 448 (narrowest header) -
// 220.2 (tab156's widest English title at Display/Strong) - 12 (gap) - slack. The live
// per-language title+toggle bound is pinned in SettingsContentFitTests, not restated here.
"A208",
```

`tools/strings_validate.py` `FIT_CLASSES` — add `"A208",` in the same position.

`AppFitMeasurers` — the map entry and the measurer (beside the A44 one; the box is READ from the built border, never restated, and the standard `OneLineInset = 8` applies — plan-review I11):

```csharp
["A208"] = (_, text) => ModeToggleLabel(text),
```

```csharp
/// <summary>A208: the settings mode toggle's label, through the production BuildModeToggle
/// face: one line within the toggle's measured box less the standard one-line inset.
/// Horizontal by construction — a one-line label can never fail a height check, and the
/// teeth test rejects a measurer that cannot fail.</summary>
private static FitVerdict ModeToggleLabel(string text)
{
    var buttons = new Dictionary<string, System.Windows.Controls.Border>();
    var brushes = new Dictionary<string, System.Windows.Media.SolidColorBrush>();
    var border = SettingsWindow.BuildModeToggle(text, () => { }, buttons, brushes);
    var panel = new System.Windows.Controls.StackPanel();
    panel.Children.Add(border);
    using var host = FitHost.Open(panel, family: Tokens.FontStandard);
    // Arrange exactly the way the sibling measurers realize layout (whatever SidebarTab /
    // modal170 do after FitHost.Open) — the invariant: border.ActualWidth is laid-out, not 0.
    var label = (System.Windows.Controls.TextBlock)border.Child;
    return OneLine(label, text, border.ActualWidth - OneLineInset,
        $"box {FitText.Dip(border.ActualWidth)} - {OneLineInset} inset");
}
```

- [ ] **Step 5: Run the localization gates**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FitClassTests|FullyQualifiedName~StringsEnumParityTests|FullyQualifiedName~StringKeyCoverageTests|FullyQualifiedName~TextLiteralRatchetTests|FullyQualifiedName~SettingsModeToggleLabelTests"`
Expected: pass — the teeth test auto-runs for A208 (an over-long probe text fails the measurer).

Also run the Python validator once: `python tools/strings_validate.py` (must report no errors; it checks the new fit name and the entry shape).

Note (plan-review m5): per-class pseudo-locale measurement is the NON-BINDING `PseudoLocaleFitReportTests` — the binding gates are the teeth test and the key/parity/ratchet set above. Do not claim a pseudo pass that nothing enforces.

- [ ] **Step 6: Full App suite + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests` — Expected: pass.

```bash
git add src/EyeCtrl.Core/Localization/strings.json src/EyeCtrl.Core/Localization/StringTableSchema.cs tools/strings_validate.py tests/EyeCtrl.App.Tests/Localization/Fit/AppFitMeasurers.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleLabelTests.cs tests/EyeCtrl.App.Tests/Settings/SettingsModeSwitchWiringTests.cs src/EyeCtrl.App/Localization/strings.de.json
git commit -m "feat(settings): mode toggle label mapping (target-mode naming) and the A208 horizontal fit class"
```

### Task 11: registration, hysteresis, wash, position push

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (own methods; `RearmPagerTarget` `:1291-1301`; `UpdateAllTargetPositions` `:2010-2047`; the `PagProgress` case `:602-608`)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleWiringTests.cs` (new; `using System.IO;`)
- Test: `tests/EyeCtrl.ViewModels.Tests/` — the dispatch pin in the class that already tests `SimulateActivated`

**Interfaces:**
- Consumes: constants + dicts (Task 9), `SwitchSettingsUiMode` (Task 8), `UpdateModeToggleLabel` (Task 10), `SettingsPagerRearmGuard` + `_pagerCenters` + `TryScreenCenter` (existing).
- Produces: `RegisterModeToggleTarget()`, `GuardModeToggle()`, `OnModeToggleActivated`, `UpdateModeToggleVisuals()`.

- [ ] **Step 1: Write the failing wiring pins**

(New file — copy the usings of the precedent files it leans on: `System.IO` for the source scan, plus whatever `QaTestSupport`'s home and `EyeCtrl.App.Views` require.)

```csharp
namespace EyeCtrl.App.Tests.Settings;

using System.IO;

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
        Check.That(body).Contains("case ModeToggleId:");          // agrees with wire-in 3 below
        Check.That(body).Contains("RegisterModeToggleTarget()");
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
        // Slice the switch statement to the NEXT case, or a call landed in ModalProgress would
        // pass this pin (plan-review m11).
        var body = SliceBetween(
            WindowSource(),
            "case nameof(SettingsWindowViewModel.PagProgress):",
            "case nameof(SettingsWindowViewModel.ModalProgress):");
        Check.That(body).Contains("UpdateModeToggleVisuals");
    }
}
```

(`ExtractMethod`/`WindowSource` copied from `SettingsPagerChromeTests.cs`; `SliceBetween` is a small local helper: substring from the first marker to the next.)

The dispatch pin (spec §6 "Toggle dispatch") goes in the ViewModels test class that already exercises `SimulateActivated` — note it returns **void** (plan-review I3):

```csharp
[Fact]
public void ModeToggleId_UnderPagination_DispatchesItsAction_NeverShowTab()
{
    var vm = /* construct as the existing dispatch tests do, with a SettingsViewModel */;
    var fired = false;
    vm.Register("settings_mode", DwellCategory.Pagination, radiusPhysical: 100, dwellMs: 700,
        () => fired = true);

    vm.SimulateActivated("settings_mode", DwellCategory.Pagination);

    Check.That(fired).IsTrue();
    Check.That(vm.Settings.CurrentTabName).IsEqualTo("");   // ShowTab never ran: the r1-C2 invariant
}
```

(This passes the moment it is written — the generic arm already exists on main; it is a regression pin for the r1-C2 invariant, not a red-first test. The wiring pins above are the red-first part.)

- [ ] **Step 2: Run to verify the wiring pins fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeToggleWiring"`
Expected: FAIL.

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

1. Ctor, after `ModeToggleHost.Child = toggle;` -> `RegisterModeToggleTarget();`
2. `SwitchSettingsUiMode`, immediately after `ApplyAndSave();` -> `GuardModeToggle();`
3. `RearmPagerTarget`'s switch -> `case ModeToggleId: RegisterModeToggleTarget(); break;` (recorded deviation from the spec's `case "settings_mode"` literal: the constant IS that string — see the self-review record)
4. `UpdateAllTargetPositions`, after the `_pagButtons` line -> `UpdateButtonPositions(_modeToggleButtons, DwellCategory.Pagination);`
5. The `PagProgress` case -> add `UpdateModeToggleVisuals();` beside the two existing calls.
6. Mouse path converges: change the ctor construction to pass `OnModeToggleActivated` and **DELETE the now-dead `OnModeToggleMouseDown`** (plan-review m10).

- [ ] **Step 4: The resting-gaze behavioural test (real VM, real dwell, DPI 2.0 — plan-review r2 N4)**

Put it in its OWN class — `SettingsModeToggleRestingGazeTests.cs` — because the DPI-isolation gate matches per FILE: a file that calls `DpiHelper.Initialize` must sit in the non-parallel collection, and the source-scan pins in `SettingsModeToggleWiringTests` must not join it. Copy the usings from `SettingsPagerChromeTests.cs:3-17` (the precedent file), then:

```csharp
namespace EyeCtrl.App.Tests.Settings;

[Collection("DpiHelper Tests (App)")]   // ManualSelectionDwellServiceTests.cs:57 is the precedent —
                                        // DpiHelperWriterIsolationTests requires this on every
                                        // DpiHelper.Initialize-calling file
public class SettingsModeToggleRestingGazeTests
{
    [StaFact]
    public void RestingGazeOnTheModeToggle_FlipsExactlyOnce_UntilTheGazeLeaves()
    {
        DpiHelper.Initialize(2.0, 2.0);              // the wander/away arithmetic below is
        try                                            // LOGICAL-DIP-derived; at 1.0 it cannot pass
        {
            var vm = QaTestSupport.MakeViewModel();  // per the pager precedent's construction
            SettingsPagerRearmGuard? guard = null;
            var flips = 0;
            var centre = new PhysicalPoint(DpiHelper.LogicalToPhysicalX(1000), DpiHelper.LogicalToPhysicalX(300));
            var hit = DpiHelper.LogicalToPhysicalX(SettingsWindow.ModeToggleHitRadius);      // 50 logical
            var release = DpiHelper.LogicalToPhysicalX(SettingsWindow.ModeToggleReleaseRadius); // 85 logical
            guard = new SettingsPagerRearmGuard(vm, (_, _) => Register());

            void Register()
            {
                if (guard!.IsDisarmed("settings_mode")) return;   // mirrors RegisterModeToggleTarget
                vm.Register("settings_mode", DwellCategory.Pagination, hit, 400,
                    () => { flips++; guard.Disarm("settings_mode", DwellCategory.Pagination, centre, release); });
                vm.UpdatePosition("settings_mode", DwellCategory.Pagination, centre);
            }
            Register();

            // (a) 200 frames resting ON the centre: the cooldown re-arms the detector; the guard
            // must hold — exactly one flip. Use a running timestamp across ALL phases exactly as
            // the pager precedent does (SettingsPagerChromeTests.cs:744-766).
            for (var i = 0; i < 200; i++) { Frame(vm, centre); guard.Update(centre); }
            Check.That(flips).IsEqualTo(1);

            // (b) wander at 70 LOGICAL DIP — outside the 50 hit circle, inside the 85 release
            // circle: the hysteresis must hold the guard (the assertion that proves 50/85, not 50/50).
            var wander = new PhysicalPoint(centre.X + DpiHelper.LogicalToPhysicalX(70), centre.Y);
            for (var i = 0; i < 50; i++) guard.Update(wander);
            for (var i = 0; i < 20; i++) { Frame(vm, centre); guard.Update(centre); }
            Check.That(flips).IsEqualTo(1);

            // (c) leave beyond the release radius, return: exactly one more flip.
            var away = new PhysicalPoint(centre.X + DpiHelper.LogicalToPhysicalX(120), centre.Y);
            guard.Update(away);                       // re-arm -> Register() via the callback seam
            for (var i = 0; i < 20; i++) Frame(vm, centre);
            Check.That(flips).IsEqualTo(2);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);           // restore the ambient scale
        }
    }
}
```

`Frame(vm, gaze)` is the precedent's dwell-frame feeder (its `vm.Receive(...)`-of-gaze/timestamp plumbing, copied verbatim from `SettingsPagerChromeTests.cs:653-707` including the running-timestamp discipline). Do not weaken the three assertions (a)/(b)/(c); do not hard-code physical offsets — everything derives through `DpiHelper.LogicalToPhysicalX` at the 2.0 scale.

- [ ] **Step 5: Run everything this task touches**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeToggleWiring|FullyQualifiedName~SettingsPagerChromeTests|FullyQualifiedName~SettingsFrameLoopAllocationTests|FullyQualifiedName~DwellWashCensusTests"` then `dotnet test tests/EyeCtrl.ViewModels.Tests`
Expected: pass — `MakePagButton` still contains no `_viewModel.Register(`; the frame loop is lambda-free; the wash census tolerates the exempt file (accepted debt, L17).

- [ ] **Step 6: Full App suite + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests` — Expected: pass.

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleWiringTests.cs
git commit -m "feat(settings): mode toggle registration with 50/85 hysteresis, wash and push"
```

### Task 12: contrast + geometry pins

**Files:**
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleChromeTests.cs` (new; `using System.IO;`)

**Interfaces:**
- Consumes: the finished toggle (Tasks 9–11): `BuildModeToggle`, `ModeToggleHitRadius`, `ModeToggleWidth/Height`, the header XAML.

- [ ] **Step 1: Write the tests**

The contrast pin reads the BUILT face (not theme tokens), in EVERY theme, blending with production's `ColorBlend`, and asserts the size/weight that keep the pair legal (plan-review r1 I5, r2 N5). The right precedent is `FitClassThemeTests` (theme scoping via `ThemeScope` + the `[Collection("ActiveTheme")]` discipline) — NOT `SettingsPageHeaderContrastTests`, which reads theme objects directly without either; the WCAG helpers below are copied from that file's `Contrast`/`Luminance` (`SettingsPageHeaderContrastTests.cs:74-87`).

```csharp
namespace EyeCtrl.App.Tests.Settings;

using System.IO;
using EyeCtrl.App.Tests.TestSupport;      // ThemeScope — Activate, not Enter (plan-review r2 N5)
using EyeCtrl.App.Views;
using EyeCtrl.Controls;                   // ColorBlend — verify the namespace against its csproj use
using EyeCtrl.Controls.Theme;             // Tokens, ThemeRegistry

[Collection("ActiveTheme")]               // ThemeScope mutates process-global state — the
                                           // FitClassThemeTests collection discipline
public class SettingsModeToggleChromeTests
{
    // Copied from SettingsPageHeaderContrastTests.cs:74-87 (WCAG relative luminance):
    private static double Luminance(System.Windows.Media.Color c)
    {
        double F(double ch) { var v = ch / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    private static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [StaFact]
    public void LabelContrast_AtRestAndFullWash_ClearsTheBars_InEveryTheme()
    {
        foreach (var theme in ThemeRegistry.All)                       // mirror FitClassThemeTests'
        {                                                              // enumeration exactly
            using (ThemeScope.Activate(theme))
            {
                var buttons = new Dictionary<string, System.Windows.Controls.Border>();
                var brushes = new Dictionary<string, System.Windows.Media.SolidColorBrush>();
                var border = SettingsWindow.BuildModeToggle("All settings", () => { }, buttons, brushes);
                var label = (System.Windows.Controls.TextBlock)border.Child;

                // The pair the user sees, read from the face:
                var baseC = ((System.Windows.Media.SolidColorBrush)border.Background).Color;
                var labelC = ((System.Windows.Media.SolidColorBrush)label.Foreground).Color;

                // Size/weight are load-bearing: the 0.4 wash lands near 3.39:1, legal only at
                // or above the LARGE-text 3:1 bar. The floor is the precedent's
                // LargeTextThresholdDip = 24 (18.0 is NOT the WCAG large-text bar — plan-review r2 m-a).
                Check.That(label.FontSize).IsGreaterOrEqualThan(24.0);
                Check.That(label.FontWeight).IsEqualTo(System.Windows.FontWeights.Bold);

                Check.That(Contrast(labelC, baseC)).IsGreaterOrEqualThan(4.5);          // at rest
                var washed = ColorBlend.Blend(baseC, Tokens.Accent, 0.4);
                Check.That(Contrast(labelC, washed)).IsGreaterOrEqualThan(3.0);         // full dwell
            }
        }
    }
}
```

(`ThemeRegistry.All`/`ThemeScope.Enter` — mirror whatever `SettingsPageHeaderContrastTests.cs:196-232` actually uses to enumerate and scope themes; copy its pattern verbatim, including the collection the file runs under. `Contrast`/`Luminance` are the WCAG helpers already defined in this plan revision's earlier snippet — keep them in this class.)

The geometry pin derives centres from the XAML like `SettingsPagerClearanceTests` does (plan-review m7 — no restated arithmetic, no invented pager position), and records the m9 note:

```csharp
[Fact]
public void ToggleClears_ThePagerAndTheFirstContentRow_AtTheNarrowestHeader()
{
    // Parse SettingsWindow.xaml exactly as SettingsPagerClearanceTests does (same helpers),
    // at the narrowest supported width (WindowWidthFor(NarrowDisplayWidthDip)).
    // Centres: the toggle = header row 0, y = ModeToggleHeight / 2, x = headerWidth
    //   - ModeToggleWidth / 2 - hostMargin.Left (the 12-DIP gap);
    // pag_next = the PaginationPanel's centred bar (read its layout like the clearance test
    //   reads the tab pager's), y = header (112) + margins + PaginationButtonHeight / 2;
    // first content row centre = top of the content card + LabelledRowMinHeight / 2 (150).
    // Assert Euclidean(toggle, pag_next) >= ModeToggleHitRadius + PagBtnRadius
    //        and Euclidean(toggle, firstRow) >= ModeToggleHitRadius + ControlBtnRadius.
    // NOTE (spec L17d / round-3 m9), recorded here as the accepted-debt note, not a check:
    // at 448 DIP the 50-DIP hit circle's lower band overlaps the description's first line —
    // reading that line's end can accrue toggle dwell; does not occur on the owner's display.
}
```

(Flesh the body out with the clearance test's own parse-and-derive helpers; the assertion pair above is the contract. If the clearance test's machinery cannot be reused for the content-row centre, derive it from the same XAML constants it reads and say so in a comment.)

- [ ] **Step 2: Run + treat any measured failure as a FINDING**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsModeToggleChromeTests"`
Expected: pass. If a theme's measured pair misses the bar, that is a FINDING, not a threshold to nudge: report it (the label brush/size is pinned by L5/L18).

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Settings/SettingsModeToggleChromeTests.cs
git commit -m "test(settings): mode toggle contrast and clearance pins"
```

### Task 13: full gate + owner-gated UI pass

**Files:**
- Modify: `tests/EyeCtrl.App.Tests/Fixtures/AppFixture.cs` (the fixture-level settings guard — plan-review I12)
- Test: `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs` (the optional simple-mode pass)

- [ ] **Step 1: Move the settings-file guard into the AppFixture (I12 — the in-method guard cannot work)**

The app reads `%APPDATA%/EyeCtrl/settings.json` once at launch, and `AppFixture` (an `ICollectionFixture`, `Fixtures/AppCollection.cs`) launches it in `InitializeAsync` BEFORE any test method runs and kills it in `DisposeAsync` AFTER the whole collection. An in-test-method backup/force/restore therefore (a) changes nothing the running app already loaded, and (b) races the app's debounced save, which can land after the restore and persist `simpleSettingsUi: true` into the owner's real file — the exact L20 violation.

Move the guard to the fixture (gated on the UI-tests-enabled flag the fixture already checks):

```csharp
// Fields on the fixture:
private string? _settingsBackup;
private bool _hadSettingsFile;
private static string SettingsPath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "settings.json");

// In InitializeAsync, BEFORE Application.Launch:
if (UiTestsEnabled)
{
    _hadSettingsFile = File.Exists(SettingsPath);
    if (_hadSettingsFile)
    {
        _settingsBackup = SettingsPath + ".ui_test_backup";
        File.Copy(SettingsPath, _settingsBackup, overwrite: true);
        ForceSimpleSettingsUi(SettingsPath, simple: false);   // the launched app reads advanced
    }
    // No existing file: nothing to force or restore — advanced is the default the app will
    // write. A throw here fails the whole UI collection, so every step is guarded (m-d).
}

// In DisposeAsync, AFTER Kill():
if (UiTestsEnabled && _settingsBackup is not null)
{
    try
    {
        Process.GetProcessById(_app!.ProcessId).WaitForExit(5000);   // the app must be DEAD before
    }                                                                 // the restore, or its debounced
    catch (ArgumentException) { /* already gone */ }                  // writer overwrites it (m-d)
    if (_hadSettingsFile) File.Move(_settingsBackup, SettingsPath, overwrite: true);
    else if (File.Exists(SettingsPath)) File.Delete(SettingsPath);    // unreachable while _settingsBackup
}                                                                     // is gated on _hadSettingsFile — kept
                                                                      // defensive
```

`ForceSimpleSettingsUi` is a small static helper on the fixture: `JsonNode.Parse` the file, set `["general"]?["simpleSettingsUi"] = simple` (tolerate a missing `general` node by creating it), write back. **Record in the fixture's doc comment: a test-host crash mid-run leaves `settings.json.ui_test_backup` behind for manual restore.** Remove any per-test-method guard — the fixture owns this now.

The existing `SettingsWindow_RendersExactlyTheExpectedTabs` test is a no-phantom-tabs check (`expectedTabs` ⊆ actual, `>= 5` found) — with the fixture forcing advanced it stays honest. If you add the optional simple-mode pass (find `Settings_ModeToggle` on the TextBlock, dwell it, expect exactly the four simple `Settings_Tab_*` ids), it flips the live flag through the app: after asserting, **flip back via the toggle and wait out the debounced save (>= 2 s) before the test ends**, so the DisposeAsync restore is a belt-and-braces, not the only line of defense.

- [ ] **Step 2: Full non-UI gate — every test project, one per invocation**

The change touches Core (settings, strings) and the App; CI runs everything:

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
dotnet test tests/EyeCtrl.Platform.Tests
dotnet test tests/EyeCtrl.App.Tests
dotnet test tests/EyeCtrl.Controls.Tests
dotnet test tests/EyeCtrl.Supervisor.Tests
dotnet test tests/EyeCtrl.Architecture.Tests
dotnet test tests/EyeCtrl.EyeCare.Tests
dotnet test tests/EyeCtrl.LM.Tests
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests
dotnet test tests/EyeCtrl.Sdk.Wpf.Tests
```

(Adjust names to the actual project list under `tests/` — run every test project in the solution, sequentially.) Expected: 0 failed everywhere. Also `python tools/strings_validate.py` and `python tools/palette_validate.py` (CI parity).

- [ ] **Step 3: ASK THE OWNER before the gated UI run**

The FlaUI set launches the real app and seizes the owner's only input channel. Ask; if approved:

```bash
EYECTRL_UI_TESTS=1 dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"
```

Expected: pass, including the advanced-mode tab check under the fixture-level guard.

- [ ] **Step 4: Final commit**

```bash
git add tests/EyeCtrl.App.Tests/Fixtures/AppFixture.cs tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs
git commit -m "test(settings): fixture-level isolation of the owner's settings file for UI tests"
```

## Self-review record (r3 — plan-review rounds 1 and 2 folded)

- **Spec coverage:** unchanged from r2's map (every S5 section and ledger line -> task), with round-2's corrections: the re-label CALL SITE is Task 10's (written and pinned, not promised); L20's guard is fixture-level; L12's German route now points at the POPULATED `src/EyeCtrl.App/Localization/strings.de.json` on base `52b031ba` (entries added in Task 10 after the A208 re-class, inert while the manifest is `[]`).
- **Recorded deviations from the spec's letter:** (1) `case ModeToggleId:` for the spec's `case "settings_mode"` literal; (2) `SettingsDeepLinkTests:234` does not move (`_tabsPerPage` is model-fed); (3) the model's pager diff realized as an unconditional teardown; (4) the deep-link behavioural test covered model-side + wiring pin + gated UI; (5) the spec's "German table is empty" was true at r4 and is stale on this base — recorded, harmless, German entries routed per above.
- **Greenness:** every task ends green and commits. Round-2's reds closed: Task 7's model pin roots at `SourceScanner.RepoRoot()`; Task 5 compiles (precedent registrar members, General split out of the context-less theory into the three full-sequence branches, real Achromatic literals, internalized `AllSourceTypes`/`Construct` + type->tab-id map); Task 11's resting-gaze harness is the precedent's shape (DPI 2.0 in the non-parallel collection, guard-consulting Register with UpdatePosition, running timestamp); Task 12's contrast compiles (`ThemeScope.Activate`, `[Collection("ActiveTheme")]`, helpers copied inline, floor 24).
- **Known judgment calls an executor must not re-decide:** `ModeToggleWidth = 208` (~7.8 DIP headroom, flagged); curation ORDER finalized at Task 5 Step 2, MEMBERSHIP never edited; measurer layout realization mirrors the siblings; geometry pin mirrors `SettingsPagerClearanceTests`; German wording "Alle Einstellungen"/"Einfach" (follow the T10 draft's terminology if it differs; the A208 room governs).
- **Plan-review history:** round 1 (FIX-THEN-SHIP): C1 + I1-I13 + m1-m13 folded in r2 (`3f233d24`, now `ba7345e1` after the `52b031ba` rebase). Round 2 (FIX-THEN-SHIP): N1 (the never-called re-label) + N2-N5 + m-a..m-f folded here; verdict at the main checkout root.
