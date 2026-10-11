# Simple vs Advanced Settings Mode — Design

- **Date:** 2026-10-10 (r2 2026-10-11, r3 2026-10-11)
- **Status:** r3 — external review rounds 1 and 2 (both FIX-THEN-SHIP; verdicts at the main checkout root) fully processed, every finding verified against source before folding in; header layout and re-arm policy decided by the owner 2026-10-11 against a headless measurement. Awaiting owner approval before planning.
- **Branch:** `feat/simple-settings-mode` (worktree `.worktrees/simple-settings-mode`), rebased on `origin/main` `32952f11`
- **Input:** owner brainstorm request — "a simple vs advanced settings interface, e.g. only some dwell settings instead of plenty. I consider the current interface the advanced one."

## 1. Problem

The settings window exposes **15 tabs and ~125 interactive rows** (sidebar tabs paginate; content pages re-chunk by height). For a gaze-only user every tab switch and page flip is paid in dwells; for a novice the surface is overwhelming. The owner is also the app's daily driver and wants a quick view of the settings actually touched day-to-day.

The current interface stays as the **advanced** mode, unchanged except for one new header control (§5.5, L13). A **simple** mode shows a curated subset: same settings objects, same values, same validation — fewer tabs, fewer rows, fewer dwell targets.

## 2. Goals

- Simple mode is a **strict visibility subset** of advanced mode: every simple row is the very same spec (same `Get`/`Set`, same description) that advanced mode renders. No value changes, no presets, no scaling.
- One-dwell switch between the modes, from a control that is **always visible in both modes** and cannot be flipped back by a resting gaze.
- **Zero behavioral change in advanced mode** beyond that one control — same tabs, same rows, same pagination.
- Every existing gate keeps holding (span guard, description completeness, fit/reachability, tab drift, string-key coverage, literal ratchet).

## 3. Non-goals (each parked in the ledger, §8)

- Value presets or derived knobs (a "dwell speed" master slider scaling all dwell times).
- Separate simple-mode page sources or custom task-based grouping.
- A first-run mode-choice dialog / onboarding wizard.
- Any change outside `SettingsWindow`, its spec layer, the localization tables and the sidebar model (tray, toolbar, coordinator, services untouched).

## 4. Approaches considered

### A — Tier flag on the existing specs + pipeline filter (**chosen**)

`SettingsRowSpec` gains `Simple { get; init; }` (default `false`). Curated rows in four existing page sources opt in with `Simple = true`. When simple mode is on, a filter step between `BuildPages()` and `MaybeRepaginateByHeight` drops non-simple rows and empty pages; the sidebar renders a filtered tab list.

- *Pros:* single source of truth per row (no delegate/description drift between modes); sources stay WPF-free and dumb; ~30 existing per-source tests untouched because they test `Build()` directly (unfiltered); new rows default to advanced-only so the simple set cannot silently grow.
- *Cons:* simple mode inherits the advanced tabs' grouping; a new curation pin test must hold the curated sets.

### B — Parallel curated simple sources — *rejected:* duplicates every delegate/description (guaranteed drift), doubles the test surface, zero user-visible gain.

### C — Presets instead of a simpler UI — *rejected:* changes values instead of hiding complexity; riskier for a user whose dwell tuning is their input channel's feel.

## 5. Design

### 5.1 Mode model

`GeneralSettings.SimpleSettingsUi` — `[ObservableProperty] bool`, default `false` (advanced). `false` is bit-for-bit today's behavior; the missing key in an existing `settings.json` loads as `false`. Persisted by the normal `SettingsService` path (camelCase `"simpleSettingsUi"`, **debounced save** like every setting); the window is recreated per open, so the flag is read at construction — no live file watching. Nothing outside `SettingsWindow` reads the flag.

It is a UI preference in the company of `UiLanguage`, and inherits that setting's reset discipline (§5.7): **`ResetAllSettings` captures it before the reflective copy and restores it after** (the D10 pattern). Today that capture/restore is a no-op — its only caller `g_resetall` is advanced-only, and in advanced mode the flag already equals its default — but it is cheap future-proofing for the day L6 flips the default (a bulk reset must not drop a novice from simple into 15 tabs).

### 5.2 The tier flag, the visible-tab set, and what filters what

```csharp
public abstract record SettingsRowSpec(string Id, string Label)
{
    public string? Description { get; init; }
    /// <summary>Shown in simple mode. Default false: new rows join advanced only.</summary>
    public bool Simple { get; init; }
}
```

Same shape as `Description` (init property, not positional) so all ~125 existing constructions keep compiling untouched. Advanced mode never reads it.

**Tab visibility is NOT a bool on the `_tabDefs` tuple** (`SettingsTabDriftTests` and `SettingsTabLabelFitTests` regex-parse it with exactly four members). The new pure sidebar model file (§5.6) carries the set:

```csharp
internal static readonly IReadOnlySet<string> SimpleTabIds = new HashSet<string>
{ "Cursor", "Dwell", "Keyboard", "General" };
```

reachable by headless tests, inside the `Views/Settings/**` WPF-purity census. A drift test asserts the set matches reality: every id in `SimpleTabIds` has a source that yields ≥1 Simple row, and no other tab's source has any.

**What reads the filtered list (`VisibleTabDefs`) and what must not** — the distinction is load-bearing (round-2 N1: filtering the wrong consumer is a crash):

- `BuildTabButtons` builds a face for **every** `_tabDefs` entry, **once, in the ctor** — never filtered. It is the only place `_tabButtons`/`_tabBrushes` are populated; `RenderTabPage` indexes it by id for every visible tab, so a filtered face loop means `_tabButtons["Selection"]` throws `KeyNotFoundException` when a window opened in simple mode switches to advanced — a dispatcher fault that kills the process with no Supervisor present.
- `UpdateHeader` also stays on the **full** `_tabDefs`: the current tab is always visible by construction, `Array.Find`'s default-tuple behavior keeps the TextBlocks safe, and filtering gains nothing.
- Only these read `VisibleTabDefs`: the **page count**, the **active-index derivation**, the **`RenderTabPage` slice**, and the **language deep-link's** `Array.FindIndex`. The window applies the pure sidebar model's outputs (§5.6) rather than re-deriving them inline.
- Insertion-scan caution: `LanguageRowReachTests`, `SettingsTabDriftTests` and `SettingsTabLabelFitTests` anchor on the **first** `"_tabDefs ="` occurrence in the file, comments included — no new text (code or comment) containing that token may precede the initializer.

### 5.3 The curation

**Simple tabs: Cursor, Dwell, Keyboard, General — 4 tabs, ~26 interactive rows** (advanced: 15 tabs, ~125 rows). Curation is id-based; labels/descriptions are `StringTable` keys on `origin/main` and localize automatically. The table below is **unordered** — the curation pin asserts the filtered **build sequence** (Cursor builds `c_color`/note on page 1 before `c_ring_style` on page 2, and pages are flattened by the chunker anyway):

| Tab | Simple rows (id) | Why these |
|---|---|---|
| Dwell | `d_act` · `d_menu` · `d_tool` · `d_key` · `d_pred` · `d_rad` · `d_cue_sound` · `d_reset` | The dwell times a user feels, **plus the prediction dwell that must move with `d_key`** (raising key dwell while prediction dwell is untunable widens the window in which the prediction bar steals keystrokes — round-1 I5), **plus the drift-tolerance radius** — the one knob that makes fixation *easier* for a struggling user. `d_fix`, `d_cooldown`, `d_ManualSelection` stay advanced — mis-tuned, they make dwell worse, and defaults are sane. The 300 ms floor `d_key`/`d_pred` share protects the user regardless of mode (blink-credit, see the source comment); the hidden `d_cooldown` term is accepted (L19). |
| Cursor | `c_ring_style` · `c_color` (both branches, incl. the AchromaticReticle `LabelSpec` note) · `c_size` · `c_opacity` · `c_reset` | Identity and visibility of the cursor. Filter mode, One-Euro, gap interpolation, position indicator stay advanced. |
| Keyboard | `k_layout` · `k_size` · `k_pred` · `k_lang` · `k_sound` · `k_reset` | Layout/language/size are the first-day choices; prediction + sound are the daily toggles. `k_pred_count`, `k_edge` and the learned-words page stay advanced. |
| General | `g_language` (first) · its conditional pending-restart note · `g_theme` · `g_boot` · `g_pause` · `g_track` · `g_supervision` (+ status label + conditional `g_supervision_retry` + the **unavailable-note `LabelSpec`** when the Supervisor exe is absent) · `g_reset` | `g_language` is **mandated** (round-1 C1): the language-recovery row, deliberately first on page 1 and pinned by `LanguageRowReachTests` — hiding it would strand a user stuck in a language they cannot read. The unavailable note is Simple too: without it a simple-mode user without the Supervisor sees no supervision line at all (round-2 m4). `g_restart` and `g_resetall` stay advanced (§5.7). |

Everything not listed is advanced-only. **New rows are advanced by default.** At the production row pitch (150 DIP) each simple tab lands on 1–2 content pages; at typical desktop heights the 4 tabs fit one sidebar page. Very short screens may still paginate — the existing mechanism handles that.

### 5.4 Pipeline

Filter step, inserted in `RebuildContent` between `BuildPages()` and `MaybeRepaginateByHeight`:

```csharp
_pages = _settings.General.SimpleSettingsUi
    ? SimpleSettingsFilter.Apply(BuildPages())
    : BuildPages();
```

`SimpleSettingsFilter` (new, pure, WPF-free under `Views/Settings/`): keeps `row.Simple` rows, drops pages left empty. Because every simple-tier source has `PrefersAutoPagination == true`, declared page boundaries are irrelevant after the filter — **only auto-pagination sources may be simple-tier**, pinned by a guard test (this excludes QuickActions, the one hand-chunked source). Models passes that rule but is excluded by curation anyway — its list/manage view-state machine is expert territory.

### 5.5 The mode toggle — title-line, decided by measurement (owner, 2026-10-11)

**Layout (round-2 N8, measured headlessly at the narrowest supported header width 448 DIP, T09 pseudo-locale transform replicated):**

| Candidate | English | Pseudo-locale (×1.4) |
|---|---|---|
| current header, no toggle | 101.1 / 112 | 125.0 — already over |
| **title-line toggle, 48 DIP tall, short label** | **103.9 — fits** | 170.3 |
| title-line toggle, 60 DIP tall | 115.9 — over | 170.3 |
| two-column, full label | 211.6 — over | ∞ (label alone 476 DIP) |

The toggle sits **beside the title line only** — short labels ("Simple" / "Advanced"; German "Einfach" / "Erweitert", a ~136 DIP button) right-aligned on the title row, **description spanning the full width underneath**. Header `Height` stays 112: no pagination change on any tab in either mode (L13). The pseudo-locale overflow is **pre-existing** (125 without any toggle) and stays the l10n fit program's debt; the toggle's own fit class tracks its box from now on (L18). The reserved-row fallback is **dropped** — it fit anywhere but repaginated every page in both modes.

**Wiring (round-2 N3/N4, all specified):**

- **Category `Pagination`** (round-1 C2): the activation dispatch sends every non-`close` Tab-category id to `ShowTab(id)`; the `Pagination` case dispatches unknown ids generically via `_pagActions` — zero ViewModel changes. Registered **once in the ctor** (Pagination is never `ClearCategory`'d).
- **Hit radius 50 DIP logical** (`ControlBtnRadius`; 85 overlaps `pag_next` at the narrowest window), **dwell `_dwellMs`** (toolbar — a deliberate one-shot switch, not a pager step).
- **Re-arm guard (owner ruling, round-2 N3):** register guard-aware (`RegisterPagerTarget` or a sibling taking radius 50); after each flip, `Disarm("settings_mode", Pagination, <tracked centre>, LogicalToPhysicalX(50))`; add `case "settings_mode"` to `RearmPagerTarget`. A resting gaze on the new label cannot flip the mode back until the gaze leaves the hit circle — the same cure the tab pager uses for wrap-jumps, no timeout. `ClearContentPager` is `pag_*`-scoped, so content re-renders never drop this guard. Pinned by a resting-gaze test modelled on `SettingsPagerRestingGazeTests` (one flip over 200 frames). A guard held on `tab_*` while the pager is absent is harmless (`RegisterTabPagerTarget` early-returns at ≤1 page; a rebuilt pager re-registers via `IsDisarmed`) — say so where the switch code lives, so nobody "fixes" it.
- **Chrome construction:** built through `MakeChromeButton` like the other chrome (consistency over exemption) — this makes it the **4th caller**, so `FitSeamPinTests.TheSeamsHaveNoSecondPath`'s count pin goes 3→4 and the toggle's box gets pinned (L18).
- **Feedback wash:** the toggle gets **dedicated border/brush fields** — never `_pagButtons`/`_pagBrushes`, which `RenderPagination` clears on every render. A third call joins the `PagProgress` case beside `UpdatePaginationVisuals`/`UpdateTabPaginationVisuals`, washing the toggle's own brush at the established blend.
- **Per-frame position push:** `UpdateAllTargetPositions` gains the toggle beside `CloseButton`/tabs/pagers/modal/content items — without it the registered target sits at (0,0) and never accrues.
- **Census honesty (round-2 N4):** `DwellWashCensusTests` exempts `SettingsWindow.xaml.cs` at file level — a non-washing toggle fails **nothing**, and the new wash passes silently. The wash in the exempt file is **accepted debt, recorded here and in L17**, consistent with the census's stated direction (migrate washes to `DwellOutlineOverlay`).
- **Contrast:** the toggle's label-on-`BtnBase` pair at rest and at full wash is pinned by a new contrast test (restored per round-2 N6); the 0.4 blend factor sits near 3.39:1, which only clears the **large-text** 3:1 bar, so the label's size and weight must stay at or above the large-text threshold.
- **Localization (round-2 N7):** keys **as literals at the call site** (`settings.mode.show.advanced` / `settings.mode.show.simple` — not interpolated; the coverage and unused-key gates read literals), `where: "settings"` (the existing surface; **no manifest edit on main**), a **new fit class** plus its measurer in `AppFitMeasurers` (no existing class describes a 48-DIP title-line chrome toggle; the measurer reproduces this section's 448-DIP/112-DIP box). German strings ride the **T10 draft** — on `32952f11` the German table is `{}` and the manifest is `[]`; the entry ships English-only until T10 lands. One line in the plan: **no live re-localization** — `StringTable` activates once at startup, so the label resolves at build time and a mid-session language change is a non-issue.
- Id `settings_mode`, UIA `AutomationId="Settings_ModeToggle"` (new FlaUI anchor), mouse fallback like every chrome button. `HeaderPanel` keeps its `x:Name`, `Height`, `Margin` and `Grid.Row="0"` — `ContentAvailableHeight` and the fit tests read them, and the hidden `Settings_Panel_Content` anchor lives in row 2, untouched.

### 5.6 Mode switch flow and the pure sidebar model

The sidebar reconciliation is extracted into a **pure, WPF-free model** under `Views/Settings/` (round-2 N5 — no test constructs `SettingsWindow`: eight non-optional concrete dependencies plus app-level XAML resources), in the spirit of `SettingsPagerChrome`/`SettingsTabPagination`:

- **Inputs:** full defs, `SimpleTabIds`, mode, sidebar budget, current tab.
- **Outputs:** visible ids, page count, page index, slice, pager-present, and the ids to register/unregister.

`BuildTabButtons` (ctor) and the mode switch both apply it. The switch:

1. Dwell/mouse on the toggle → flip `SimpleSettingsUi` → `ApplyAndSave` (debounced persist) → **disarm the toggle** (re-arm guard).
2. Apply the model: recompute `_tabPageCount` and the slice over `VisibleTabDefs` (`_tabsPerPage` is budget-derived and does not change); **clear `TabPagerPanel.Children` and `_tabPagerButtons`/`_tabPagerBrushes` before rebuilding the pager** (`BuildTabPagePager` only appends — calling it again doubles the buttons); when the page count collapses to 1, **unregister `tab_prev`/`tab_next`** (mirroring `RenderPagination`'s single-page teardown); `RenderTabPage`.
3. If the current tab is not in the target mode's set → `ShowTab(first tab of the target mode's set)` — Cursor, which leads both sets — **and set `ActiveTabId`** (the dwell path sets it; the deep link sets it explicitly; a snap that skips it leaves the sidebar contradicting the content). Otherwise an explicit `RebuildContent`.
4. **Re-derive the sidebar page, never clamp it** (round-2 N2): `_tabPageIndex = PageOf(IndexOf(CurrentTabName in VisibleTabDefs), _tabsPerPage)`, computed **after** the snap — simple mode always sits on page 0, so a clamp leaves General's tab off-screen after switching to advanced while on it (the exact "sidebar contradicts content" defect F6 fixed for the deep link).
5. Re-render the toggle's own label for the new mode.

**Language deep link:** `ShowLanguageRow` lands on General — simple-tier in both modes; with the count/index/slice lookups on `VisibleTabDefs` it stays correct with no extra work; the deep-link literal pin updates accordingly (§6). `LanguageRowReachTests` keeps holding: `g_language` stays first on General page 1 in both modes. (`SettingsTarget` is only `Default` or `GeneralPage1` — no external path can name a hidden tab.)

Invariant: **dwell registrations exactly mirror on-screen chrome at every step** — no in-flight dwell may land on hidden chrome. Known accepted exception: the tab list itself jumps when the pager row appears/disappears (L17).

### 5.7 Resets

- Per-tab reset rows (`d_reset`, `c_reset`, `k_reset`, `g_reset`) are Simple where their tab is; each modal stays exactly "Reset ⟨Tab⟩ settings to defaults?" — **declined** (L19, round-2 m2): adding "…including settings shown only in advanced mode" is one more localized string per tab for marginal honesty, and the modal already names the whole tab. A per-tab reset in simple mode does reset that tab's hidden advanced rows to defaults — that is what the modal says.
- `g_resetall` is advanced-only. `SettingsViewModel.ResetAllSettings` **captures and restores `SimpleSettingsUi` around the reflective copy** (UiLanguage/D10 pattern — today a no-op, see §5.1). `g_reset`'s explicit copy list omits it, as it already omits `UiLanguage`. Pinned by a both-resets-keep-the-flag test plus the `notResetPropsByDesign` census entry.
- The `c_color` ↔ `LabelSpec` swap under AchromaticReticle keeps both branches Simple so the explanatory note survives the filter.

### 5.8 What does not change

- Any setting's value, range, validation, defaults; `settings.json` gains exactly one key.
- Advanced mode's tabs, rows, pages, pagination — except the one header toggle (L13).
- `SettingsService`, `SettingsUiCoordinator`, tray, toolbar, keyboard, action menu, `SettingsWindowViewModel` (the Pagination seam needs no dispatch change).
- Localization parity: every new string rides `StringTable`; no literal is introduced.

## 6. Testing strategy

**New tests**
- *Curation pin:* for each simple tab, build the source and assert the exact filtered **row sequence** (kind + id; `LabelSpec` twins by position + text predicate), in **build order**, with **all three supervision branches** built — unavailable, available+connected, available+disconnected (adds `g_supervision_retry`) — and with the pending-language-note present (`GeneralPageSourceResetDriftTests` shows the fake).
- *Tab-set drift:* `SimpleTabIds` ↔ actual Simple rows, both directions; every simple-tier source has `PrefersAutoPagination == true`.
- *Filter unit tests:* rows dropped, empty pages dropped, advanced passes through untouched.
- *Simple-mode reachability:* per simple tab, `Build → filter → Repaginate` at the narrowest supported budget; every dwell id reachable (reuses the `HandChunkedPageFitTests` machinery).
- *Sidebar model tests* (round-2 N5 — replaces the infeasible STA window test): pure model driven headlessly in **both directions**, including "constructed in simple, switched to advanced" (the N1 regression: slice ⊆ faces that must have been built over the full defs) and "re-derive, don't clamp" (N2: General stays on-screen). The window's applier is pinned `SettingsPagerWiringTests`-style by method-body scans: faces built over `_tabDefs`, pager panel cleared before build, `ActiveTabId` set on snap, toggle position push present.
- *Toggle dispatch:* `settings_mode` under `Pagination` reaches `_pagActions` and never `ShowTab` (`SettingsPagerWiringTests.TabPager_RegistersUnderPagination` is the precedent pin).
- *Resting-gaze pin:* one flip over 200 frames, modelled on `SettingsPagerRestingGazeTests` (N3).
- *Both resets keep the flag* + `("General", SimpleSettingsUi)` added to `notResetPropsByDesign`.
- *Toggle contrast pin:* label-on-`BtnBase` at rest and full wash; size/weight at or above the large-text threshold (restored per round-2 N6).
- *Geometry:* radius-50 clearance vs `pag_prev`/`pag_next` and the first content row at the narrowest supported width (title-line placement: centre y ≈ 22 DIP vs the pager's ~176 — clears even at radius 85, pinned at 50).
- *Header fit:* the toggle's fit-class measurer reproduces the 448/112 box (per N7).
- *Deep link in simple mode:* lands on the language row.

**Updated**
- `SettingsDeepLinkTests:233` — the literal `Array.FindIndex(_tabDefs, …)` pin moves to `VisibleTabDefs`.
- `SettingsTabPaginationWiringTests:79` — the page-math location pin follows the model extraction.
- `FitSeamPinTests:119/129` — `MakeChromeButton` call count 3→4 with the toggle's box pinned; `BuildTabButtons` keeps `BuildTabFace(` and no `new Border`.
- `SettingsTests` (gated UI): default advanced unchanged (15 `Settings_Tab_*`); the optional gated simple-mode pass via `Settings_ModeToggle`. **Both must force advanced at start and restore the flag in a `finally`** — `--test-mode` reads the owner's real `%APPDATA%/EyeCtrl/settings.json`, so the pin must not depend on (nor write) the owner's live mode (round-2 m6).
- Localization data: two new entries in `strings.json` with full metadata and a new fit class + measurer (the existing coverage/ratchet/parity gates pick these up; no test edits expected beyond the fit seam).

**Untouched by design**
- Span guard, per-source row tests, description-completeness census, `HandChunkedPageFitTests`, pagination math, purity gate (auto-covers the new files), `SettingsTabDriftTests`/`SettingsTabLabelFitTests` (tuple keeps four members — first-occurrence `"_tabDefs ="` anchors noted in §5.2), `LanguageRowReachTests` (language row stays first in both modes; it is mode-blind — an optional simple-mode reach pin, toolbar→language in 2 dwells, is noted but not required), `SettingsPageHeaderContrastTests` (checks `HeaderTitle`/`HeaderDescription` by name; the restructure keeps them), `DwellWashCensusTests` (file-level exemption — see L17).

## 7. Blast radius (files)

| File | Change |
|---|---|
| `SettingsRowSpec.cs` | `Simple` init property |
| `Sources/{Dwell,Cursor,Keyboard,General}PageSource.cs` | `Simple = true` on curated rows (incl. label twins, language + all supervision branches) |
| `SettingsWindow.xaml` | title-line toggle on the header row (`HeaderPanel` keeps name/height/margin/row) |
| `SettingsWindow.xaml.cs` | apply the sidebar model; filter step; toggle chrome (Pagination, radius 50, re-arm, dedicated fields, position push, third wash call); header layout |
| `Views/Settings/SimpleSettingsFilter.cs` (new) | pure row filter |
| `Views/Settings/SettingsSidebarModel.cs` (new) | pure sidebar reconciliation + `SimpleTabIds` |
| `GeneralSettings.cs` | `SimpleSettingsUi` |
| `SettingsViewModel.cs` | `ResetAllSettings` capture/restore (UiLanguage pattern) |
| `src/EyeCtrl.Core/Localization/strings.json` | two toggle entries (who/where/settings, new fit class) |
| tests | per §6 |

**`SettingsWindowViewModel.cs` is deliberately absent** — the Pagination dispatch seam makes no VM change necessary (L5).

## 8. Owner decision ledger (veto any line — the design bends)

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| L1 | Audience: owner's daily driving first, curated so a novice could also survive in it | The owner is the only live user; the app competes on usability for real gaze users | Novice-first; caregiver-only |
| L2 | Mechanism: tier flag + pipeline filter (Approach A) | Single source of truth; smallest blast radius; sources stay dumb | Parallel sources; presets |
| L3 | Simple tabs: Cursor, Dwell, Keyboard, General | Covers identity, feel, typing, startup/look/language; no external path opens a hidden tab | EyeCare/Actions/Toolbar/Calibration — parked (§9) |
| L4 | Curation per §5.3 (~26 rows, build-order pin, three supervision branches); new rows advanced by default; `+g_language` (r1-C1), `+d_pred` (moves with `d_key`), `+d_rad` (drift tolerance — r1's 75→90 claim did not match the live file; UX grounds only) | Every kept row is one the user feels or needs for recovery | Broader sets; hiding the language row |
| L5 | Toggle: Pagination category (Tab ids all dispatch to `ShowTab`), radius 50 (`ControlBtnRadius`), `_dwellMs`, built via `MakeChromeButton` (4th caller, box pinned), dedicated border/brush fields, position push in `UpdateAllTargetPositions`, third wash call, registered once in the ctor | Zero VM changes via the generic `_pagActions` arm; per-render-cleared `_pagBrushes` must not be reused | Tab category + VM special case; freestanding construction (exempts the FitSeam pin) |
| L6 | `SimpleSettingsUi`, default **advanced** | Existing user keeps today's behavior; missing key → false | Default simple — revisit when external users exist |
| L7 | Per-tab resets Simple; `g_resetall` advanced; UiLanguage capture/restore (no-op today, future-proofing for L6) | Resets stay gentle; a bulk reset must not flip the interface in the L6-flipped world | Resetting the flag; the Supervision section-skip (an ownership boundary, not a preference) |
| L8 | Only auto-pagination sources may be simple-tier | The filter's semantics are total; hand-chunked navigation can't be re-split | Teaching the filter about hand-chunked pages |
| L9 | Static `SimpleTabIds` in the pure model file | Both `_tabDefs` regex parsers stay green; censused, greppable, drift-testable | Bool on the tuple; computed visibility |
| L10 | Mode switch: faces always built over the full `_tabDefs` (r2-N1 crash), `UpdateHeader` unfiltered, pager panel cleared + unregistered on collapse, snap to Cursor sets `ActiveTabId`, **page re-derived not clamped** (r2-N2), all via the pure sidebar model | Gaze-trap rule extends to mode hiding; every gap found by round 2 lives here | Clamping; filtering the face loop or header lookup |
| L11 | No "dwell speed" master knob in v1 | A multiplier over individually-tuned times has no coherent meaning | Scaling slider (parked, §9) |
| L12 | Toggle strings as **literal** keys, `where: settings`, **no manifest edit on main**; German via the T10 draft (tables empty on `32952f11`); no live re-localization (table activates once at startup) | The gates read literals; the German table does not exist yet; the settings surface is already manifested | Hard-coded literals; interpolated keys; assuming a populated German table |
| L13 | Advanced mode unchanged **except the one header toggle**; header height stays 112, no pagination change anywhere | The toggle is visible in both modes by design; the measured layout needs no height | Any layout that changes the budget |
| L14 | Deep link keeps working in simple mode via model lookups; its literal pin updates | A wrong-language user's recovery path must not depend on the mode | Special-casing the deep link |
| L15 | **Header layout decided by owner 2026-10-11 against measurement:** title-line toggle, 48 DIP tall, short label ("Simple"/"Advanced", DE "Einfach"/"Erweitert"), description spans full width. Measured at 448 DIP: EN 103.9/112 fits; 60-DIP (115.9) and two-column full-label (211.6 EN, ∞ pseudo) vetoed; pseudo-locale overflows even without a toggle (125/112) — pre-existing l10n debt, tracked by the toggle's fit class. Reserved-row fallback dropped (it repaginated every page) | The only candidate that fits English at the narrowest display without touching any page budget | Reserved row; two-column; 60-DIP chrome |
| L16 | **Re-arm guard on the toggle (owner ruling 2026-10-11):** `Disarm` after each flip, `RearmPagerTarget` case, resting-gaze pin; `ClearContentPager` stays `pag_*`-scoped | The label invites reading; an unguarded toggle oscillates every cooldown + dwell; the window already has the exact cure | Relying on the global cooldown alone |
| L17 | Accepted debt, recorded: (a) the toggle's wash lives in a file `DwellWashCensusTests` exempts (a non-washing target fails nothing; consistent with the census's migration direction); (b) the tab-list jump when the pager row appears/disappears on a mode switch (~256 DIP at the owner's height) — a deliberate, user-initiated, one-time transition; (c) a re-arm guard held on `tab_*` at ≤1 page is harmless, not a bug | Honest books beat silent exemptions | Wiring the toggle into `DwellOutlineOverlay` now; freezing the sidebar layout across modes |
| L18 | Localization route for the toggle: new fit class + `AppFitMeasurers` measurer reproducing the 448/112 title-line box; `MakeChromeButton` becomes the 4th FitSeam caller with its box pinned | No existing class describes this chrome; the pseudo-locale gate must track the box | Sizing the label to an existing class that describes another box |
| L19 | Declined micro-decisions (round-2 m2/m8): reset modals keep "Reset ⟨Tab⟩ settings to defaults?" (no "…including hidden settings" suffix — N localization churn for marginal honesty); `d_pred`'s description stays as-is (no coupling hint to the hidden `d_cooldown`) | Localization churn vs. marginal wording gains | One suffix key per tab; description rewrite |
| L20 | Gated UI tests must not depend on or mutate the owner's real `settings.json`: force advanced at start, restore in `finally` (the optional simple pass writes the flag — same isolation) | `--test-mode` reads the real file; a persisted simple flag would break the 15-tab pin and the owner's live UI | Letting the gated tests race the owner's state |

## 9. Future work (explicitly out of scope)

- Flip the default to simple when the app ships to external users (L6).
- Candidate simple additions: Eye Care enabled toggle, a Calibration entry point, an Actions subset, the Toolbar size slider.
- A "dwell speed" master slider over the simple dwell times, if the six-slider curation proves too fiddly in live use (L11).
- The header's own pseudo-locale overflow at 448 DIP (125/112 with no toggle) — the l10n fit program's debt, surfaced by this feature's measurement.

## 10. Review history

- **Round 1 (2026-10-11):** FIX-THEN-SHIP — 2 critical (stale base pre-l10n; Tab-category toggle hijacked to `ShowTab`), 5 important, ledger partial vetoes. One factual correction returned (owner's radius is the default 75, not 90).
- **Round 2 (2026-10-11):** FIX-THEN-SHIP — 1 critical (N1: filtered face loop crashes simple→advanced; the process dies without a Supervisor), 7 important (re-trigger oscillation, toggle wiring incl. the false census claim, infeasible STA test, unlisted pin breaks, localization facts, header layout undecided), 10 minors. All folded; the two open decisions (header layout L15, re-arm L16) ruled by the owner against the headless measurement recorded in §5.5. Scope stays three tickets: T1 pure model + filter + curation pins, T2 window mode-switch mechanics (sidebar model), T3 header toggle (layout, wiring, fit class, localization entries).
