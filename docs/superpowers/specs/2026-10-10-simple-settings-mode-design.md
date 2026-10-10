# Simple vs Advanced Settings Mode — Design

- **Date:** 2026-10-10
- **Status:** draft, awaiting owner review (design approval doubles as the review gate below)
- **Branch:** `feat/simple-settings-mode` (worktree `.worktrees/simple-settings-mode`)
- **Input:** owner brainstorm request — "a simple vs advanced settings interface, e.g. only some dwell settings instead of plenty. I consider the current interface the advanced one."

## 1. Problem

The settings window exposes **15 tabs and ~120 interactive rows** (sidebar tabs paginate; content pages re-chunk by height). For a gaze-only user every tab switch and page flip is paid in dwells; for a novice the surface is overwhelming. The owner is also the app's daily driver and wants a quick view of the settings actually touched day-to-day.

The current interface stays as the **advanced** mode, unchanged. A new **simple** mode shows a curated subset: same settings objects, same values, same validation — fewer tabs, fewer rows, fewer dwell targets.

## 2. Goals

- Simple mode is a **strict visibility subset** of advanced mode: every simple row is the very same spec (same `Get`/`Set`, same description) that advanced mode renders. No value changes, no presets, no scaling.
- One-dwell switch between the modes, from a control that is **always visible in both modes**.
- **Zero behavioral change in advanced mode** — same tabs, same rows, same pagination.
- Every existing gate keeps holding (span guard, description completeness, fit/reachability, tab drift).

## 3. Non-goals (each parked in the ledger, §8)

- Value presets or derived knobs (a "dwell speed" master slider scaling all dwell times).
- Separate simple-mode page sources or custom task-based grouping.
- A first-run mode-choice dialog / onboarding wizard.
- Localization of the two new strings (chrome is English-only today).
- Any change outside `SettingsWindow` and its spec layer (tray, toolbar, coordinator, service untouched).

## 4. Approaches considered

### A — Tier flag on the existing specs + pipeline filter (**chosen**)

`SettingsRowSpec` gains `Simple { get; init; }` (default `false`). Curated rows in four existing page sources opt in with `Simple = true`. When simple mode is on, a filter step between `BuildPages()` and `MaybeRepaginateByHeight` drops non-simple rows and empty pages; the sidebar renders a filtered `_tabDefs` list.

- *Pros:* single source of truth per row (no delegate/description drift between modes); sources stay WPF-free and dumb; ~30 existing per-source tests untouched because they test `Build()` directly (unfiltered); smallest blast radius; new rows default to advanced-only so the simple set cannot silently grow.
- *Cons:* simple mode inherits the advanced tabs' grouping (no custom ordering/grouping); a new curation pin test must hold the curated sets.

### B — Parallel curated simple sources

A second `ISettingsPageSource` set building four tabs from scratch with hand-picked rows.

- *Rejected:* duplicates every `Get`/`Set` delegate and description — guaranteed drift with the per-tab reset lists; doubles the source-test surface; the completeness census must learn both sets; strictly more code for zero user-visible gain over A.

### C — Presets instead of a simpler UI

"Comfort / Balanced / Precise" buttons that write scaled dwell values.

- *Rejected:* solves a different problem (value choice, not navigational burden); *changes* settings rather than hiding complexity — riskier for a user whose dwell tuning is their input channel's feel.

## 5. Design

### 5.1 Mode model

`GeneralSettings.SimpleSettingsUi` — `[ObservableProperty] bool`, default `false` (advanced). `false` is bit-for-bit today's behavior; the missing key in an existing `settings.json` loads as `false`. Persisted by the normal `SettingsService` path (`camelCase` `"simpleSettingsUi"`); the window is recreated per open, so the flag is read at construction — no live file watching.

> It is a UI preference, not a behavior setting: nothing outside `SettingsWindow` reads it. Sharing the file with the Supervisor is therefore a non-event.

### 5.2 The tier flag

```csharp
public abstract record SettingsRowSpec(string Id, string Label)
{
    public string? Description { get; init; }
    /// <summary>Shown in simple mode. Default false: new rows join advanced only.</summary>
    public bool Simple { get; init; }
}
```

Same shape as `Description` (init property, not positional) so all ~120 existing constructions keep compiling untouched. Advanced mode never reads it. Tab visibility is a static bool on the `_tabDefs` entry (Cursor, Dwell, Keyboard, General) — explicit and greppable; a drift test asserts it matches reality (§7).

### 5.3 The curation

**Simple tabs: Cursor, Dwell, Keyboard, General — 4 tabs, ~24 rows** (advanced: 15 tabs, ~120 rows). Exact rows (ids as built today):

| Tab | Simple rows (id → label) | Why these |
|---|---|---|
| Dwell | `d_act` Activation Button · `d_menu` Action Menu · `d_tool` Toolbar/Settings · `d_key` Keyboard Key · `d_cue_sound` Dwell Completion Sound · `d_reset` | The four dwell times a user actually feels; the sound toggle; the gentle reset. `d_fix`, `d_rad`, `d_cooldown`, `d_pred`, `d_ManualSelection` stay advanced — mis-tuned, they make dwell *worse*, and defaults are sane. |
| Cursor | `c_ring_style` Ring Style · `c_color` Circle Color (both branches, incl. the AchromaticReticle `LabelSpec` note) · `c_size` Circle Size · `c_opacity` Circle Opacity · `c_reset` | Identity and visibility of the cursor — the thing you stare at all day. Filter mode, One-Euro, gap interpolation, position indicator stay advanced. |
| Keyboard | `k_layout` Active Layout · `k_size` Key Size Multiplier · `k_pred` Word Prediction · `k_lang` Dictionary Language · `k_sound` Click Sound · `k_reset` | Layout/language/size are the first-day choices; prediction + sound are the daily toggles. `k_pred_count`, `k_edge` and the learned-words page stay advanced. |
| General | `g_theme` Theme · `g_boot` Start on Boot · `g_pause` Start Paused · `g_track` Show Track Status · `g_supervision` Automatic Crash Recovery (+ status label + conditional `g_supervision_retry`) · `g_reset` | Startup, look, and the safety net. `g_restart` and `g_resetall` stay advanced (§5.6). |

Everything not listed is advanced-only. **New rows are advanced by default** — joining simple mode is a deliberate curation act recorded in the pin test.

At the production row pitch (150 DIP) each simple tab lands on 1–2 content pages; at typical desktop heights the 4 tabs fit one sidebar page (no tab pager at all). Very short screens may still paginate — the existing mechanism handles that; nothing new is invented.

### 5.4 Pipeline

Filter step, inserted in `RebuildContent` between `BuildPages()` and `MaybeRepaginateByHeight`:

```csharp
private IReadOnlyList<PageSpec> BuildPages() =>
    … source.Build() …
    → simple mode ? SimpleSettingsFilter.Apply(pages) : pages
```

`SimpleSettingsFilter` (new, pure, WPF-free under `Views/Settings/`): keeps `row.Simple` rows, drops pages left empty. Because every simple-tier source has `PrefersAutoPagination == true`, declared page boundaries are irrelevant after the filter (the chunker flattens them anyway) — **only auto-pagination sources may be simple-tier**, pinned by a guard test (this excludes QuickActions, the one hand-chunked source). Models passes that rule but is excluded by curation anyway — its list/manage view-state machine is expert territory.

Sidebar: the existing `_tabButtons` borders are built once for all 15 tabs as today; `RenderTabPage` slices the **filtered** defs. `TabsPerPage`/`PageCount`/`PageOf` math is reused unchanged over the filtered count. Tabs hidden by simple mode are detached **and unregistered** — the standing gaze-trap rule ("a hidden tab with a live hit circle is a gaze trap") extends naturally to mode hiding.

### 5.5 The mode toggle

A chrome dwell button on the **header row, right side** (`HeaderPanel` becomes a two-column grid: title/description left, toggle right; header `Height` stays 112 so `ContentAvailableHeight` is untouched):

- Label is action-oriented, state-clear: **"Show advanced settings"** while in simple mode, **"Show simple settings"** while in advanced.
- Id `settings_mode`, UIA `AutomationId="Settings_ModeToggle"` (new FlaUI anchor), dwell category `Tab` (chrome that must survive content rebuilds — `RebuildContent` clears only `Control`), dwell = `_dwellMs` (toolbar dwell, like tabs/close), mouse fallback like every chrome button.
- Clearance: no overlapping live hit circles with the content pager (`Pagination`) or the first content row (`Control`) — different categories accrue concurrently, so this needs a pin test at the narrowest supported content column. Fallback if measurement vetoes header-right: a reserved full-width row directly under the header (budget-adjusted like `PaginationReserveHeight`). Ledger L5.

### 5.6 Mode switch flow

1. Dwell/mouse on the toggle → flip `SimpleSettingsUi` → `ApplyAndSave` (persists immediately).
2. Rebuild the sidebar over the filtered defs: clamp `_tabPageIndex`, reconcile `TabPagerPanel` (build when >1 page; **clear + unregister `tab_prev`/`tab_next`** when it collapses to 1 — mirroring `RenderPagination`'s existing single-page teardown), `RenderTabPage`.
3. If the current tab is not in the target mode's set → `ShowTab(first tab of the target mode's set)` — Cursor, which leads both sets (rides the normal `CurrentTabName` rebuild); otherwise an explicit `RebuildContent` (the row filter changed the pages).
4. Re-render the toggle's own label for the new mode.

Invariant: **dwell registrations exactly mirror on-screen chrome at every step of the switch** — no in-flight dwell may land on hidden chrome. The rebuild paths already clear/re-register `Control` and `Tab`; the switch adds the tab-pager reconciliation.

### 5.7 Resets

- Per-tab reset rows (`d_reset`, `c_reset`, `k_reset`, `g_reset`) are Simple where their tab is. Resetting to defaults is safe, and each modal already says "Reset ⟨Tab⟩ settings to defaults?" — which is exactly what happens: **a per-tab reset in simple mode also resets that tab's hidden advanced rows to their defaults** (ledger L7).
- `g_resetall` is advanced-only. A novice's clean slate is the per-tab reset; the global nuke stays one deliberate mode-switch away.
- `SettingsViewModel.ResetAllSettings` **excludes `SimpleSettingsUi`** — same pattern as the Supervision skip: a reset must not flip the interface out from under the user (a novice in simple mode resetting all settings would suddenly face 15 tabs).
- `g_reset`'s explicit property copy list does not include `SimpleSettingsUi` (it is a UI preference, not a General behavior setting; both facts documented at the flag).

### 5.8 What does not change

- Any setting's value, range, validation, defaults; `settings.json` gains exactly one key.
- Advanced mode rendering: identical tabs, rows, pages, pagination (the flag is inert in advanced mode).
- `SettingsService`, `SettingsUiCoordinator`, tray, toolbar, keyboard, action menu.
- The `c_color` ↔ `LabelSpec` swap under AchromaticReticle (both branches marked Simple so the explanatory note survives the filter).

## 6. Testing strategy

**New tests**
- *Curation pin:* for each simple tab, build the source and assert the exact filtered id set (both directions — no row silently joins or leaves simple mode).
- *Tab-flag ↔ row-flag drift:* every tab marked simple has ≥1 Simple row; no unmarked tab has any.
- *Simple-source guard:* every simple-tier source has `PrefersAutoPagination == true`.
- *Filter unit tests:* rows dropped, empty pages dropped, advanced mode passes through untouched.
- *Simple-mode reachability:* for each simple tab, `Build → filter → Repaginate` at the narrowest supported budget; every dwell id reachable (reuses the `HandChunkedPageFitTests` machinery).
- *Mode-switch (STA):* toggle flips the setting, sidebar shows 4 tabs, registrations mirror visible chrome, hidden tabs/pagers unregistered, current-tab snap rule.
- *Clearance pin:* mode-toggle hit circle vs content pager and first content row at the narrowest supported width.

**Updated**
- `SettingsTabDriftTests` (tab defs gain a bool — its source regex).
- `SettingsTests` (gated UI): default advanced unchanged (15 `Settings_Tab_*`); optionally a gated simple-mode pass via `Settings_ModeToggle`.

**Untouched by design**
- Span guard, per-source row tests, description-completeness gate, `HandChunkedPageFitTests` (mode-agnostic source sweeps), pagination math tests, purity gate.

## 7. Blast radius (files)

| File | Change |
|---|---|
| `SettingsRowSpec.cs` | `Simple` init property |
| `Sources/{Dwell,Cursor,Keyboard,General}PageSource.cs` | `Simple = true` on curated rows (incl. the `c_color` label twin and supervision rows) |
| `SettingsWindow.xaml` | `HeaderPanel` → grid + toggle |
| `SettingsWindow.xaml.cs` | tab-def flag; filtered defs; sidebar rebuild on switch; filter step; toggle chrome/handler; pager reconciliation |
| `Views/Settings/SimpleSettingsFilter.cs` (new) | pure filter |
| `GeneralSettings.cs` | `SimpleSettingsUi` |
| `SettingsViewModel.cs` | `ResetAllSettings` skip |
| tests | per §6 |

## 8. Owner decision ledger (veto any line — the design bends)

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| L1 | Audience: owner's daily driving first, curated so a novice could also survive in it | The owner is the only live user; the app competes on usability for real gaze users | Novice-first (would force opinionated defaults), caregiver-only |
| L2 | Mechanism: tier flag + pipeline filter (Approach A) | Single source of truth; smallest blast radius; sources stay dumb | Parallel simple sources (drift); presets (changes values) |
| L3 | Simple tabs: Cursor, Dwell, Keyboard, General | Covers identity (cursor), feel (dwell), typing (keyboard), startup/look (general) | Adding EyeCare/Actions/Calibration — parked (§9) |
| L4 | Curation per §5.3 (~24 rows); new rows advanced by default | Every kept row is one the user feels; nothing that can silently degrade dwell | Broader sets (defeats the purpose) |
| L5 | Toggle in header-right, clearance-tested | Always visible in both modes, zero navigation; survives content rebuilds | Sidebar-under-close (hit-circle overlap with Close); pagination row (per-tab chrome); a row per page (inconsistent) |
| L6 | Flag `GeneralSettings.SimpleSettingsUi`, default **advanced** | Existing user keeps today's behavior; missing key → false | Default simple (novice-first) — revisit when external users exist |
| L7 | Per-tab resets Simple; `g_resetall` advanced; `ResetAllSettings`/`g_reset` skip the mode flag | Resets stay gentle in simple mode; a reset must not flip the interface | Excluding per-tab resets from simple (weaker clean-slate); resetting the flag (trap) |
| L8 | Only auto-pagination sources may be simple-tier | The filter's semantics are total; hand-chunked navigation can't be re-split | Teaching the filter about hand-chunked pages |
| L9 | Tab visibility via static flag + drift test | Explicit, greppable, testable | Computing from "has ≥1 Simple row" (implicit, surprises on conditional rows) |
| L10 | Mode switch reconciles both pagers + snaps hidden current tab | Gaze-trap rule extends to mode hiding | Leaving pager registrations stale |
| L11 | No "dwell speed" master knob in v1 | A multiplier over individually-tuned times has no coherent meaning | Scaling slider (parked, §9) |
| L12 | No localization, no onboarding dialog, no first-run choice | Chrome is English-only today; YAGNI | — |
| L13 | Advanced mode is bit-for-bit unchanged; flag inert there | The owner's current workflow is the contract | Restyling advanced to "make room" |

## 9. Future work (explicitly out of scope)

- Flip the default to simple when the app ships to external users (L6).
- Candidate simple additions: Eye Care enabled toggle, a Calibration entry point, an Actions subset.
- A "dwell speed" master slider over the four simple dwell times, if the four-slider curation proves too fiddly in live use (L11).
- Localize the two toggle strings with the eventual resx work.
