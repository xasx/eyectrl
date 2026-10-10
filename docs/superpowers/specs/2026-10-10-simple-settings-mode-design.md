# Simple vs Advanced Settings Mode — Design

- **Date:** 2026-10-10 (revised 2026-10-11, round 1)
- **Status:** r2 — external review round 1 (FIX-THEN-SHIP, verdict in `review-verdict-simple-settings-mode.md` at the main checkout root) fully processed; all findings verified against source before folding in. Awaiting owner approval before planning.
- **Branch:** `feat/simple-settings-mode` (worktree `.worktrees/simple-settings-mode`), rebased on `origin/main` `32952f11`
- **Input:** owner brainstorm request — "a simple vs advanced settings interface, e.g. only some dwell settings instead of plenty. I consider the current interface the advanced one."

## 1. Problem

The settings window exposes **15 tabs and ~125 interactive rows** (sidebar tabs paginate; content pages re-chunk by height). For a gaze-only user every tab switch and page flip is paid in dwells; for a novice the surface is overwhelming. The owner is also the app's daily driver and wants a quick view of the settings actually touched day-to-day.

The current interface stays as the **advanced** mode, unchanged except for one new header control (§5.5, L13). A **simple** mode shows a curated subset: same settings objects, same values, same validation — fewer tabs, fewer rows, fewer dwell targets.

## 2. Goals

- Simple mode is a **strict visibility subset** of advanced mode: every simple row is the very same spec (same `Get`/`Set`, same description) that advanced mode renders. No value changes, no presets, no scaling.
- One-dwell switch between the modes, from a control that is **always visible in both modes**.
- **Zero behavioral change in advanced mode** beyond that one control — same tabs, same rows, same pagination.
- Every existing gate keeps holding (span guard, description completeness, fit/reachability, tab drift, string-key coverage, literal ratchet).

## 3. Non-goals (each parked in the ledger, §8)

- Value presets or derived knobs (a "dwell speed" master slider scaling all dwell times).
- Separate simple-mode page sources or custom task-based grouping.
- A first-run mode-choice dialog / onboarding wizard.
- Any change outside `SettingsWindow`, its spec layer, and the localization tables (tray, toolbar, coordinator, services untouched).

## 4. Approaches considered

### A — Tier flag on the existing specs + pipeline filter (**chosen**)

`SettingsRowSpec` gains `Simple { get; init; }` (default `false`). Curated rows in four existing page sources opt in with `Simple = true`. When simple mode is on, a filter step between `BuildPages()` and `MaybeRepaginateByHeight` drops non-simple rows and empty pages; the sidebar renders a filtered tab list.

- *Pros:* single source of truth per row (no delegate/description drift between modes); sources stay WPF-free and dumb; ~30 existing per-source tests untouched because they test `Build()` directly (unfiltered); new rows default to advanced-only so the simple set cannot silently grow.
- *Cons:* simple mode inherits the advanced tabs' grouping (no custom ordering/grouping); a new curation pin test must hold the curated sets.

### B — Parallel curated simple sources

A second `ISettingsPageSource` set building four tabs from scratch with hand-picked rows.

- *Rejected:* duplicates every `Get`/`Set` delegate and description — guaranteed drift with the per-tab reset lists; doubles the source-test surface; the completeness census must learn both sets; strictly more code for zero user-visible gain over A.

### C — Presets instead of a simpler UI

"Comfort / Balanced / Precise" buttons that write scaled dwell values.

- *Rejected:* solves a different problem (value choice, not navigational burden); *changes* settings rather than hiding complexity — riskier for a user whose dwell tuning is their input channel's feel.

## 5. Design

### 5.1 Mode model

`GeneralSettings.SimpleSettingsUi` — `[ObservableProperty] bool`, default `false` (advanced). `false` is bit-for-bit today's behavior; the missing key in an existing `settings.json` loads as `false`. Persisted by the normal `SettingsService` path (camelCase `"simpleSettingsUi"`); the window is recreated per open, so the flag is read at construction — no live file watching.

It is a UI preference in the company of `UiLanguage`, and inherits that setting's reset discipline (§5.7): **`ResetAllSettings` captures it before the reflective copy and restores it after** — the D10 pattern the language setting already uses ("a bulk reset must not undo a language choice"; a bulk reset equally must not flip 15 tabs into a novice's face). Nothing outside `SettingsWindow` reads the flag.

### 5.2 The tier flag and the visible-tab set

```csharp
public abstract record SettingsRowSpec(string Id, string Label)
{
    public string? Description { get; init; }
    /// <summary>Shown in simple mode. Default false: new rows join advanced only.</summary>
    public bool Simple { get; init; }
}
```

Same shape as `Description` (init property, not positional) so all ~125 existing constructions keep compiling untouched. Advanced mode never reads it.

**Tab visibility is NOT a bool on the `_tabDefs` tuple.** `SettingsTabDriftTests` and `SettingsTabLabelFitTests` both regex-parse `_tabDefs` with exactly four tuple members; extending the tuple breaks both. Instead the new pure filter file (§5.4) carries the set:

```csharp
internal static readonly IReadOnlySet<string> SimpleTabIds = new HashSet<string>
{ "Cursor", "Dwell", "Keyboard", "General" };
```

reachable by headless tests and inside the `Views/Settings/**` WPF-purity census. The window derives **`VisibleTabDefs`** — `_tabDefs` filtered by the mode — and every existing consumer that today iterates or searches `_tabDefs` reads `VisibleTabDefs` instead: `BuildTabButtons`, `RenderTabPage`'s slice, `_tabsPerPage`/`_tabPageCount`/active-index math, `UpdateHeader`'s lookup, and the language deep link's `Array.FindIndex` (§5.6). One filtered list, one place; no per-site filtering to forget. A drift test asserts the set matches reality: every id in `SimpleTabIds` has a source that yields ≥1 Simple row, and no other tab's source has any.

### 5.3 The curation

**Simple tabs: Cursor, Dwell, Keyboard, General — 4 tabs, ~26 interactive rows** (advanced: 15 tabs, ~125 rows). Curation is id-based; labels and descriptions localize automatically (they are `StringTable` keys on `origin/main`). Exact rows (ids as built today):

| Tab | Simple rows (id) | Why these |
|---|---|---|
| Dwell | `d_act` · `d_menu` · `d_tool` · `d_key` · `d_pred` · `d_rad` · `d_cue_sound` · `d_reset` | The dwell times a user feels, **plus the prediction dwell that must move with `d_key`** (raising key dwell while prediction dwell is untunable widens the window in which the prediction bar steals keystrokes — review I5), **plus the drift-tolerance radius** — the one knob that makes fixation *easier* for a struggling user. `d_fix`, `d_cooldown`, `d_ManualSelection` stay advanced — mis-tuned, they make dwell worse, and defaults are sane. |
| Cursor | `c_ring_style` · `c_color` (both branches, incl. the AchromaticReticle `LabelSpec` note) · `c_size` · `c_opacity` · `c_reset` | Identity and visibility of the cursor — the thing you stare at all day. Filter mode, One-Euro, gap interpolation, position indicator stay advanced. |
| Keyboard | `k_layout` · `k_size` · `k_pred` · `k_lang` · `k_sound` · `k_reset` | Layout/language/size are the first-day choices; prediction + sound are the daily toggles. `k_pred_count`, `k_edge` and the learned-words page stay advanced. |
| General | `g_language` (first) · its conditional pending-restart note · `g_theme` · `g_boot` · `g_pause` · `g_track` · `g_supervision` (+ status label + conditional `g_supervision_retry`) · `g_reset` | `g_language` is **mandated**: it is the language-recovery row, deliberately first on page 1 and pinned by `LanguageRowReachTests` — hiding it in simple mode would strand a user stuck in a language they cannot read (review C1). Startup, look, and the safety net follow. `g_restart` and `g_resetall` stay advanced (§5.7). |

Everything not listed is advanced-only. **New rows are advanced by default** — joining simple mode is a deliberate curation act recorded in the pin test. At the production row pitch (150 DIP) each simple tab lands on 1–2 content pages; at typical desktop heights the 4 tabs fit one sidebar page (no tab pager at all). Very short screens may still paginate — the existing mechanism handles that.

### 5.4 Pipeline

Filter step, inserted in `RebuildContent` between `BuildPages()` and `MaybeRepaginateByHeight`:

```csharp
_pages = _settings.General.SimpleSettingsUi
    ? SimpleSettingsFilter.Apply(BuildPages())
    : BuildPages();
```

`SimpleSettingsFilter` (new, pure, WPF-free under `Views/Settings/`; also owns `SimpleTabIds`): keeps `row.Simple` rows, drops pages left empty. Because every simple-tier source has `PrefersAutoPagination == true`, declared page boundaries are irrelevant after the filter (the chunker flattens them anyway) — **only auto-pagination sources may be simple-tier**, pinned by a guard test (this excludes QuickActions, the one hand-chunked source). Models passes that rule but is excluded by curation anyway — its list/manage view-state machine is expert territory.

### 5.5 The mode toggle

A chrome dwell button on the **header row, right side** (`HeaderPanel` becomes a two-column grid: title/description left, toggle right; header `Height` stays 112 so `ContentAvailableHeight` and advanced-mode pagination are untouched):

- **Category: `Pagination` — not `Tab`.** The activation dispatch (`SettingsWindowViewModel.HandleTargetActivated`) sends *every* non-`close` Tab-category id to `ShowTab(id)`; a dwell on a Tab-category `settings_mode` would first navigate to a nonexistent tab (blank page, empty header) and then trip the snap. The `Pagination` case already dispatches unknown ids generically through `_pagActions` (that is how the tab pager's own buttons ride it) — **zero ViewModel changes**. Pinned by a dispatch test.
- **Labels via `StringTable` keys** (`settings.mode.show.advanced` / `settings.mode.show.simple`), with entries in the embedded `strings.json`, the German table, and the German surface manifest — the settings surface is localized on `origin/main`, and `TextLiteralRatchetTests`/`StringKeyCoverageTests` would reject hard-coded literals.
- **Hit radius 50 DIP logical.** At the tab radius of 85 the toggle's circle overlaps `pag_next` at the narrowest supported window (126.5 DIP between centres, under the 145 required); 50 clears at any position. Dwell time `_dwellMs` (toolbar dwell — a deliberate one-shot switch, not a pager step). Dwell progress joins the existing `PagProgress` wash by adding the toggle's brush to the wash set `UpdatePaginationVisuals` already drives — a registered target that does not wash fails `DwellWashCensusTests`.
- **Fit:** width capped by the `ConstrainToContentWidth` pattern; `SettingsContentFitTests` extended to measure the two-column header at the narrowest supported column (it measures the old full-width layout today and would stay green through a regression). Fallback if measurement vetoes header-right: a reserved full-width row directly under the header, budget-adjusted like `PaginationReserveHeight` (L15).
- Id `settings_mode`, UIA `AutomationId="Settings_ModeToggle"` (new FlaUI anchor), mouse fallback like every chrome button.

### 5.6 Mode switch flow

1. Dwell/mouse on the toggle → flip `SimpleSettingsUi` → `ApplyAndSave` (persists immediately).
2. Rebuild the sidebar over `VisibleTabDefs`: recompute `_tabsPerPage`/`_tabPageCount`, clamp `_tabPageIndex`; **clear `TabPagerPanel.Children` before rebuilding the pager** (`BuildTabPagePager` only appends — calling it again doubles the buttons), and when the page count collapses to 1, **unregister `tab_prev`/`tab_next`** (mirroring `RenderPagination`'s single-page teardown); `RenderTabPage`.
3. If the current tab is not in the target mode's set → `ShowTab(first tab of the target mode's set)` — Cursor, which leads both sets — **and set `ActiveTabId`** (the dwell path sets it; the deep link sets it explicitly; a snap that skips it leaves the sidebar highlighting the previous tab). Otherwise an explicit `RebuildContent` (the row filter changed the pages). All indices derive from `VisibleTabDefs`.
4. Re-render the toggle's own label for the new mode.

**Language deep link:** `ShowLanguageRow` lands on General — simple-tier in both modes, so it is always in `VisibleTabDefs`; with every lookup already on the filtered list the deep link stays correct with no extra work. Pinned by a test in simple mode. `LanguageRowReachTests` keeps holding: `g_language` stays the first row of General page 1 in both modes.

Invariant: **dwell registrations exactly mirror on-screen chrome at every step of the switch** — no in-flight dwell may land on hidden chrome. The rebuild paths already clear/re-register `Control`; the switch adds the tab-pager reconciliation.

### 5.7 Resets

- Per-tab reset rows (`d_reset`, `c_reset`, `k_reset`, `g_reset`) are Simple where their tab is. Resetting to defaults is safe, and each modal already says what it does — **a per-tab reset in simple mode also resets that tab's hidden advanced rows to their defaults** (L7).
- `g_resetall` is advanced-only. A novice's clean slate is the per-tab reset; the global nuke stays one deliberate mode-switch away.
- `SettingsViewModel.ResetAllSettings` **captures and restores `SimpleSettingsUi` around the reflective copy** — the exact `UiLanguage` pattern (D10), not a property skip. `g_reset`'s explicit copy list omits it, as it already omits `UiLanguage` ("Reset General leaves `UiLanguage` alone for the same reason"). Both facts pinned by a both-resets-keep-the-flag test.
- The `c_color` ↔ `LabelSpec` swap under AchromaticReticle keeps both branches Simple so the explanatory note survives the filter.

### 5.8 What does not change

- Any setting's value, range, validation, defaults; `settings.json` gains exactly one key.
- Advanced mode's tabs, rows, pages, pagination — except the one new header toggle (L13).
- `SettingsService`, `SettingsUiCoordinator`, tray, toolbar, keyboard, action menu, `SettingsWindowViewModel` (the Pagination seam needs no dispatch change).
- Localization parity: every new string rides `StringTable`; no literal is introduced.

## 6. Testing strategy

**New tests**
- *Curation pin:* for each simple tab, build the source and assert the exact filtered **row sequence** — kind + id for interactive rows; `LabelSpec` twins (whose id is `""`) pinned by position and text predicate. Built with a **live supervision fake** (the default test context yields `Supervision == null`, so the conditional rows never materialize) and with the pending-language-note present.
- *Tab-set drift:* `SimpleTabIds` ↔ actual Simple rows, both directions; every simple-tier source has `PrefersAutoPagination == true`.
- *Filter unit tests:* rows dropped, empty pages dropped, advanced passes through untouched.
- *Simple-mode reachability:* per simple tab, `Build → filter → Repaginate` at the narrowest supported budget; every dwell id reachable (reuses the `HandChunkedPageFitTests` machinery).
- *Mode-switch (STA):* toggle flips the setting; sidebar shows 4 tabs; registrations mirror visible chrome; hidden tabs/pagers unregistered; snap sets `CurrentTabName` **and** `ActiveTabId`; tab pager cleared not doubled; deep link lands on the language row in simple mode.
- *Toggle dispatch:* `settings_mode` under `Pagination` reaches `_pagActions` and never `ShowTab`.
- *Both resets keep the flag* (mirrors the `UiLanguage` reset-survival pin).
- *Geometry:* radius-50 clearance vs `pag_prev`/`pag_next` and the first content row at the narrowest supported width; header two-column fit (extended `SettingsContentFitTests`).
- *Wash census:* the toggle's brush joins the `PagProgress` wash (`DwellWashCensusTests` extension).

**Updated**
- `SettingsTests` (gated UI): default advanced unchanged (15 `Settings_Tab_*`); optionally a gated simple-mode pass via `Settings_ModeToggle`.
- Localization tables: two new keys in `strings.json` + German table + manifest (covered by the existing `StringKeyCoverageTests` / `MirrorParityTests` / ratchet gates — no test edits expected).

**Untouched by design**
- Span guard, per-source row tests, description-completeness census, `HandChunkedPageFitTests`, pagination math, purity gate (auto-covers the new file), `SettingsTabDriftTests` and `SettingsTabLabelFitTests` (the tuple keeps four members — that is why the flag is a static set, L9), `LanguageRowReachTests` (language row stays first in both modes).

## 7. Blast radius (files)

| File | Change |
|---|---|
| `SettingsRowSpec.cs` | `Simple` init property |
| `Sources/{Dwell,Cursor,Keyboard,General}PageSource.cs` | `Simple = true` on curated rows (incl. label twins, language + supervision rows) |
| `SettingsWindow.xaml` | `HeaderPanel` → grid + toggle |
| `SettingsWindow.xaml.cs` | `VisibleTabDefs`; sidebar rebuild on switch (pager clear + unregister); filter step; toggle chrome/handler (Pagination category, radius 50); wash extension; header fit cap |
| `Views/Settings/SimpleSettingsFilter.cs` (new) | pure filter + `SimpleTabIds` |
| `GeneralSettings.cs` | `SimpleSettingsUi` |
| `SettingsViewModel.cs` | `ResetAllSettings` capture/restore (UiLanguage pattern) |
| `src/EyeCtrl.Core/Localization/strings.json` + German table + manifest | two toggle keys |
| tests | per §6 |

**`SettingsWindowViewModel.cs` is deliberately absent** — the Pagination dispatch seam makes no VM change necessary (L5).

## 8. Owner decision ledger (veto any line — the design bends)

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| L1 | Audience: owner's daily driving first, curated so a novice could also survive in it | The owner is the only live user; the app competes on usability for real gaze users | Novice-first (would force opinionated defaults), caregiver-only |
| L2 | Mechanism: tier flag + pipeline filter (Approach A) | Single source of truth; smallest blast radius; sources stay dumb | Parallel simple sources (drift); presets (changes values) |
| L3 | Simple tabs: Cursor, Dwell, Keyboard, General | Covers identity (cursor), feel (dwell), typing (keyboard), startup/look/language (general) | Adding EyeCare/Actions/Toolbar/Calibration — parked (§9) |
| L4 | Curation per §5.3 (~26 rows); new rows advanced by default. **r2:** `+g_language` (review C1 — the language-recovery row must stay reachable); `+d_pred` (moves with `d_key`, prediction-steal window); `+d_rad` (drift tolerance makes dwell easier for a struggling user — note: the review's claim that the owner changed radius 75→90 does not match the live `settings.json`, which sits at the migrated default 75; the row earns its place on UX grounds) | Every kept row is one the user feels or needs for recovery | Broader sets (defeats the purpose); hiding the language row (strands wrong-language users) |
| L5 | Toggle: header-right, **Pagination category** (Tab-category ids all go to `ShowTab` — a dwell would blank the page first), **radius 50** (85 overlaps `pag_next` at the narrowest window), **StringTable labels**, wash joined, `_dwellMs` | Always visible in both modes; zero VM changes via the generic `_pagActions` dispatch | Sidebar-under-close (hit-circle overlap with Close); a row per page (inconsistent); Tab category + VM special case (more code on the hot dispatch path) |
| L6 | Flag `GeneralSettings.SimpleSettingsUi`, default **advanced** | Existing user keeps today's behavior; missing key → false | Default simple (novice-first) — revisit when external users exist |
| L7 | Per-tab resets Simple; `g_resetall` advanced; both bulk paths keep the flag via the **UiLanguage capture/restore pattern (D10)** | Resets stay gentle in simple mode; a reset must not flip the interface | Excluding per-tab resets from simple (weaker clean-slate); resetting the flag (trap); the Supervision section-skip pattern (wrong tool — that is an ownership boundary, not a preference) |
| L8 | Only auto-pagination sources may be simple-tier | The filter's semantics are total; hand-chunked navigation can't be re-split | Teaching the filter about hand-chunked pages |
| L9 | Tab visibility via a **static `SimpleTabIds` set in the pure filter file** (test-reachable), NOT a tuple bool | The tuple bool breaks `SettingsTabDriftTests` + `SettingsTabLabelFitTests` regexes (both parse exactly four members); a set is greppable, censused, and drift-testable | Bool on `_tabDefs` (breaks two gates); computing from "has ≥1 Simple row" (implicit, surprises on conditional rows) |
| L10 | Mode switch reconciles both pagers (panel **cleared** — the builder only appends), snaps hidden current tab to Cursor **setting `ActiveTabId`**, all indices over `VisibleTabDefs`, deep link verified in simple mode | Gaze-trap rule extends to mode hiding; the review found each of these gaps in r1 | Leaving pager registrations or the sidebar highlight stale |
| L11 | No "dwell speed" master knob in v1 | A multiplier over individually-tuned times has no coherent meaning | Scaling slider (parked, §9) |
| L12 | **r2, rewritten:** the localization premise of r1 is false on `origin/main` — chrome is `StringTable`-keyed with a German table and manifest. The toggle's two strings ship as keys with German entries; the ratchet/coverage gates enforce it | Premise verified against `origin/main` (l10n program merged 2026-10-11) | Hard-coded English literals (rejected by `TextLiteralRatchetTests`) |
| L13 | **r2, reworded:** advanced mode is unchanged **except the one header toggle** — tabs, rows, pages, pagination identical; the flag is otherwise inert there | The toggle is visible in both modes by design, so "bit-for-bit" was over-claimed in r1 | Restyling advanced to "make room" |
| L14 | Language deep link (`NavigateToLanguageRow`/`ShowLanguageRow`) must keep working in simple mode; free once all lookups ride `VisibleTabDefs`, pinned by test | A wrong-language user's recovery path must not depend on the mode | Special-casing the deep link for simple mode |
| L15 | Header fit: height stays 112 (no pagination change), toggle width capped, `SettingsContentFitTests` extended to the two-column layout; reserved-row fallback if the narrowest-window measurement vetoes | r1 had no fit plan; the current fit test measures the old layout and cannot catch the regression | Growing the header height (shifts every advanced page's budget) |

## 9. Future work (explicitly out of scope)

- Flip the default to simple when the app ships to external users (L6).
- Candidate simple additions: Eye Care enabled toggle, a Calibration entry point, an Actions subset, the Toolbar size slider.
- A "dwell speed" master slider over the simple dwell times, if the six-slider curation proves too fiddly in live use (L11).
- A second external review round over this r2 spec, if the owner wants one before planning.

## 10. Review history

- **Round 1 (2026-10-11):** FIX-THEN-SHIP — 2 critical (spec predated the `l10n` program on `origin/main`; Tab-category toggle would be sent to `ShowTab`), 5 important (header fit, toggle geometry, switch-chain gaps, missing tests, curation questions), ledger partial vetoes on L4/L9/L12/L13. All verified against source before folding in; one factual correction returned: the owner's live fixation radius is the migrated default 75, not 90 (the toolbar-size 120→112 migration does check out). Scope confirmed: three implementation tickets — pure model + filter, window mode-switch mechanics, header toggle.
