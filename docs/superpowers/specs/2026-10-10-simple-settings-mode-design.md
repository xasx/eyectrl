# Simple vs Advanced Settings Mode — Design

- **Date:** 2026-10-10 (r2–r4 2026-10-11)
- **Status:** r4 — external review rounds 1–3 (verdicts at the main checkout root) fully processed; four owner rulings recorded (header layout, re-arm guard, release-radius hysteresis, toggle label wording). Awaiting owner approval before planning.
- **Branch:** `feat/simple-settings-mode` (worktree `.worktrees/simple-settings-mode`), rebased on `origin/main` `32952f11`
- **Input:** owner brainstorm request — "a simple vs advanced settings interface, e.g. only some dwell settings instead of plenty. I consider the current interface the advanced one."

## 1. Problem

The settings window exposes **15 tabs and ~125 interactive rows** (sidebar tabs paginate; content pages re-chunk by height). For a gaze-only user every tab switch and page flip is paid in dwells; for a novice the surface is overwhelming. The owner is also the app's daily driver and wants a quick view of the settings actually touched day-to-day.

The current interface stays as the **advanced** mode, unchanged except for one new header control (§5.5, L13). A **simple** mode shows a curated subset: same settings objects, same values, same validation — fewer tabs, fewer rows, fewer dwell targets.

## 2. Goals

- Simple mode is a **strict visibility subset** of advanced mode: every simple row is the very same spec (same `Get`/`Set`, same description) that advanced mode renders. No value changes, no presets, no scaling.
- One-dwell switch between the modes, from a control that is **always visible in both modes** and cannot be flipped back by a resting gaze.
- **Zero behavioral change in advanced mode** beyond that one control — same tabs, same rows, same pagination.
- Every existing gate keeps holding (span guard, description completeness, fit/reachability, tab drift, string-key coverage, literal ratchet, fit-class teeth).

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

`GeneralSettings.SimpleSettingsUi` — `[ObservableProperty] bool`, default `false` (advanced). `false` is bit-for-bit today's behavior; the missing key in an existing `settings.json` loads as `false`. Persisted by the normal `SettingsService` path (camelCase `"simpleSettingsUi"`, **debounced save** like every setting); the window is recreated per open, so the flag is read at construction — no live file watching. Nothing outside `SettingsWindow` reads the flag (the Supervisor's settings writes go through the shared Core model, writer-scoped — round 3 verified — so the new key survives them).

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

**Tab visibility is NOT a bool on the `_tabDefs` tuple** (`SettingsTabDriftTests` and `SettingsTabLabelFitTests` regex-parse it with exactly four members). `SimpleSettingsFilter.cs` — T1's pure file, created before the sidebar model exists — carries the set:

```csharp
internal static readonly IReadOnlySet<string> SimpleTabIds = new HashSet<string>
{ "Cursor", "Dwell", "Keyboard", "General" };
```

reachable by headless tests, inside the `Views/Settings/**` WPF-purity census; the sidebar model (§5.6) takes it as an **input**, so T1's drift test does not depend on T2's file. A drift test asserts the set matches reality: every id in `SimpleTabIds` has a source that yields ≥1 Simple row, and no other tab's source has any.

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

`SimpleSettingsFilter` (new, pure, WPF-free under `Views/Settings/`; owns `SimpleTabIds`): keeps `row.Simple` rows, drops pages left empty. Because every simple-tier source has `PrefersAutoPagination == true`, declared page boundaries are irrelevant after the filter — **only auto-pagination sources may be simple-tier**, pinned by a guard test (this excludes QuickActions, the one hand-chunked source). Models passes that rule but is excluded by curation anyway — its list/manage view-state machine is expert territory.

### 5.5 The mode toggle — title-line, decided by measurement and owner rulings (2026-10-11)

**Layout (round-2 N8 measured; round-3 I2 extended with the horizontal row; owner ruling L15).** Headless WPF measurement at the narrowest supported header width 448 DIP, real tokens (Display 34 Bold / Subtitle 18 / Title 24 Bold), T09 pseudo-locale transform replicated; German measured from the T10 draft (`21f1677b`):

| Quantity | English | German (T10 draft) | Pseudo-locale (×1.4) |
|---|---|---|---|
| header height, current layout | 101.1 / 112 | 103.9 / 112 | 125.0 — over |
| header height, title-line 48-DIP toggle | **103.9 — fits** | 103.9 — fits | 170.3 if the title wrapped; production clips |
| title-line 60 DIP / two-column full label | 115.9 / 211.6 — over | — | ∞ (label alone 476) |
| **title width + toggle, title line** | **363.7 / 448** | **348.3 / 448** | 550.6 / 448 |

The toggle sits **beside the title line only**, short label right-aligned on the title row, **description spanning the full width underneath**. Header `Height` stays 112: no pagination change on any tab in either mode (L13).

- **Title overflow rule (round-3 I2):** the header grid becomes `Grid(*, Auto)` — title column star, **no wrap** (production `HeaderTitle` has none), clipping at its column; toggle column auto. The pseudo-locale framing, corrected: the description's third line is **pre-existing** overflow (125.0 without any toggle); the **title collision is introduced by the toggle** — non-binding under the pseudo-locale (a test-only language, made visible by its `[…]` wrapper) and **bound for every shipped language by the new fit class** below, so clipping cannot occur in English or German.
- **Margin budget (round-3 m1):** the toggle's **vertical margin is 0**; the rule is "toggle height + vertical margins ≤ 56 DIP" (48 ≤ 56.12 — copying the pager's 4-DIP margin would land at 111.88/112, a 0.12-DIP trap).
- **Construction (round-3 m2):** the XAML holds a **named host** in the header grid; code builds the face via **`internal static Border BuildModeToggle(...)`** (which calls `MakeChromeButton`) and inserts it into the host. `MakeChromeButton` becomes the **4th caller** — `FitSeamPinTests.TheSeamsHaveNoSecondPath`'s count pin goes 3→4 — and a `MakeModalButton_BuildsTheFaceTheGateMeasures`-style routing pin asserts the instance path builds through the static builder, which is also what the fit measurer measures. The UIA `AutomationId="Settings_ModeToggle"` goes on the label **TextBlock child**, not the Border container (Border containers collapse in the UIA view of a transparent overlay).
- **Width policy:** fixed button width via `MakeChromeButton` (it takes an explicit width, no padding), initially sized to the widest shipped label + 32 DIP text inset ("All settings" ≈ 130 DIP text; the plan pins the constant); the fit class enforces the invariant, not the constant.
- **Fit class (round-3 I1, L18):** the class is **horizontal**, A-style: *toggle label ≤ header width − widest admissible title − gap − button inset*, with the widest title **derived from `tab156`'s maximum rendered at Display** (220.2 EN / 213.8 DE), not a restated number. A one-line label can never fail a height check, and `FitClassTests.AnOverLongText_FailsEveryMeasurementPathOfTheClass` rejects a measurer that cannot fail. New files this pulls in: `StringTableSchema.FitClasses`, `FIT_CLASSES` in `tools/strings_validate.py` (`StringsEnumParityTests` requires parity), and the `AppFitMeasurers.Measurers` map.

**Wiring (round-2 N3/N4 + round-3 I3, all specified):**

- **Category `Pagination`** (round-1 C2): the activation dispatch sends every non-`close` Tab-category id to `ShowTab(id)`; the `Pagination` case dispatches unknown ids generically via `_pagActions` — zero ViewModel changes. Registered **once in the ctor** (Pagination is never `ClearCategory`'d; only `Control` is).
- **Labels (owner ruling, round-3 m8):** the label names the mode you switch **TO** — **"All settings"** while in simple mode, **"Basic"** while in advanced. Keys `settings.mode.show.all` / `settings.mode.show.basic`, **as literals at the call site** (the coverage and unused-key gates read literals), `where: "settings"`, **no manifest edit on main**. The pair avoids the round-3 m8 collision: "Advanced" would have duplicated the Advanced tab's label in English and the T10 draft's German ("Erweitert") too. German strings ride the **T10 draft** (on `32952f11` the German table is `{}`, the manifest `[]`; the entry ships English-only until T10 lands). No live re-localization — `StringTable` activates once at startup, so the label resolves at build time.
- **Radius, hysteresis (owner ruling, round-3 I3):** one named hit-radius constant (**50 DIP logical**), registered by a **guard-aware sibling `RegisterModeToggleTarget()`** — NOT `RegisterPagerTarget`, which hard-codes `PagBtnRadius` = 60 and would re-arm the guard inside its own live circle (the oscillation round-2 N3 closed). The sibling consults `_pagerRearmGuard.IsDisarmed("settings_mode")` and registers at the 50 constant. **`Disarm(..., LogicalToPhysicalX(85))`** — the release radius is deliberately larger than the hit radius: the owner's measured top-right gaze bias (~80 px against a 100-px hit circle) sits near the circle's edge, and hysteresis absorbs that wander while keeping L16's rule ("until the gaze leaves"). The old "85 overlaps `pag_next`" rationale belonged to the abandoned right-column placement and is withdrawn — the title-line centre (y = 24) sits ~152 DIP from the pager's and clears any of these radii.
- **Re-arm guard (owner ruling, round-2 N3):** after each flip, `Disarm("settings_mode", Pagination, centre, LogicalToPhysicalX(85))`; `case "settings_mode"` in `RearmPagerTarget`. The detector zeroes the toggle's progress before activation, so the disarm leaves no stuck wash. `ClearContentPager` is `pag_*`-scoped, so content re-renders never drop this guard. **Mouse-path fallback:** unlike a jumped pager, the toggle's Border is never re-rendered, so a direct centre read is valid when no gaze frame has pushed one. Pinned by a resting-gaze test modelled on `SettingsPagerRestingGazeTests` (one flip over 200 frames). A guard held on `tab_*` while the pager is absent is harmless (`RegisterTabPagerTarget` early-returns at ≤1 page; a rebuilt pager re-registers via `IsDisarmed`) — say so where the switch code lives, so nobody "fixes" it.
- **Feedback wash:** the toggle gets **dedicated one-entry dictionaries** (`MakeChromeButton`'s signature takes `Dictionary<string, Border>`/`Dictionary<string, SolidColorBrush>`, and `BlendProgress`/`UpdateButtonPositions` take dictionaries) — never `_pagButtons`/`_pagBrushes`, which `RenderPagination` clears on every render. A third call joins the `PagProgress` case beside `UpdatePaginationVisuals`/`UpdateTabPaginationVisuals`.
- **Per-frame position push:** `UpdateAllTargetPositions` gains the toggle beside `CloseButton`/tabs/pagers/modal/content items — without it the registered target sits at (0,0) and never accrues. The push must be **lambda-free** (`TryUpdatePosition`/`UpdateButtonPositions` — `SettingsFrameLoopAllocationTests.ThePerFrameMethod_ContainsNoLambdaAtAll` forbids `=>` in the method). The toggle's own registration, disarm and wash code lives in **its own methods**, never inside `RenderPagination`, `MakePagButton` or `RegisterPagerTarget` (the `SettingsPagerChromeTests` method-body extraction cuts at the first closing brace; `MakePagButton` must keep containing no `_viewModel.Register(`).
- **Census honesty:** `DwellWashCensusTests` exempts `SettingsWindow.xaml.cs` at file level — a non-washing toggle fails **nothing**, and the new wash passes silently. The wash in the exempt file is **accepted debt, recorded in L17**.
- **Contrast:** the label-on-`BtnBase` pair at rest and at full wash is pinned by a new contrast test; the 0.4 blend factor sits near 3.39:1, which only clears the **large-text** 3:1 bar, so the label's `FontSizeTitle`/`FontWeightStrong` must stay at or above the large-text threshold (they are).
- **Geometry note (round-3 m9):** at 448 DIP the 50-DIP hit circle's lower band overlaps the description's first line (y 56–80) — reading that line's end can accrue toggle dwell. This does not occur on the owner's display (1456-DIP header; descriptions end before ~900). Recorded beside the geometry pin, not fixed.
- Id `settings_mode`; mouse fallback like every chrome button. `HeaderPanel` keeps its `x:Name`, `Height`, `Margin` and `Grid.Row="0"` — `ContentAvailableHeight` and the fit tests read them, and the hidden `Settings_Panel_Content` anchor lives in row 2, untouched.

### 5.6 Mode switch flow and the pure sidebar model

The sidebar reconciliation is extracted into a **pure, WPF-free model** under `Views/Settings/` (round-2 N5 — no test constructs `SettingsWindow`: eight non-optional concrete dependencies plus app-level XAML resources), in the spirit of `SettingsPagerChrome`/`SettingsTabPagination`:

- **Inputs:** full defs, `SimpleTabIds`, mode, sidebar budget, current tab.
- **Outputs:** visible ids, page count, **page index computed after applying the snap**, slice, pager-present, the pager register/unregister diff (used **only** for `tab_prev`/`tab_next` — the tab faces are handled by `RenderTabPage`'s own unregister-all loop, pinned by `SettingsTabPaginationWiringTests.RenderTabPage_UnregistersTabsItHides`), and **`SnapTo`** (null | tab id) — the one T2 decision with user-visible consequences, made a model output precisely so it gets a headless test (round-3 I4).

`BuildTabButtons` (ctor) and the switch both apply it. **The switch, as one ordered contract** (round-3 I4 — the order is load-bearing; applying the sidebar before the snap renders an empty sidebar, because `PageSlice` clamps an out-of-range page to `(count, 0)`):

1. Flip `SimpleSettingsUi` → `ApplyAndSave` (debounced persist).
2. **Disarm the toggle** (release radius 85).
3. `model = SettingsSidebarModel.Compute(defs, SimpleTabIds, mode, budget, currentTab)`.
4. If `SnapTo` is set → `ShowTab(SnapTo)` **and set `ActiveTabId`** (rides the `CurrentTabName` rebuild); otherwise an explicit `RebuildContent`. Either way the content **page resets to 0** (ShowTab semantics, round-3 m5): landing mid-list of a differently-filtered page — e.g. advanced Dwell page 2 → simple Dwell — is disorienting, and a bare clamp is what `RebuildContent` would otherwise do.
5. Apply the sidebar **once, after the snap**: clear `TabPagerPanel` and the pager dictionaries, build the pager when >1 page, **unregister `tab_prev`/`tab_next`** when it collapses to 1, then a **single `RenderTabPage`** (its unregister-all-then-register-slice loop keeps the gaze-trap invariant).
6. Re-render the toggle's label for the new mode.

Model tests pin `SnapTo` and the post-snap page in **both directions**, including "constructed in simple, switched to advanced" (the round-2 N1 regression) and "advanced sidebar page 2 → simple" (the empty-sidebar regression this ordering closes). The window's applier is pinned `SettingsPagerWiringTests`-style by method-body scans: faces built over `_tabDefs`, **one `RenderTabPage` after the snap**, pager panel cleared before build, `ActiveTabId` set, toggle position push present.

**Language deep link:** `ShowLanguageRow` lands on General — simple-tier in both modes; with the count/index/slice lookups on `VisibleTabDefs` it stays correct with no extra work (both literal pins, `:233` and `:234`, move — §6). `LanguageRowReachTests` keeps holding: `g_language` stays first on General page 1 in both modes. (`SettingsTarget` is only `Default` or `GeneralPage1` — no external path can name a hidden tab.)

Invariant: **dwell registrations exactly mirror on-screen chrome at every step** — no in-flight dwell may land on hidden chrome (mutation inside the activation event is snapshot-safe; the pagers already rely on this). Known accepted exception: the tab list itself jumps when the pager row appears/disappears (L17).

### 5.7 Resets

- Per-tab reset rows (`d_reset`, `c_reset`, `k_reset`, `g_reset`) are Simple where their tab is; each modal stays exactly "Reset ⟨Tab⟩ settings to defaults?" — **declined** (L19): adding "…including settings shown only in advanced mode" is one more localized string per tab for marginal honesty, and the modal already names the whole tab. A per-tab reset in simple mode does reset that tab's hidden advanced rows to defaults — that is what the modal says.
- `g_resetall` is advanced-only. `SettingsViewModel.ResetAllSettings` **captures and restores `SimpleSettingsUi` around the reflective copy** (UiLanguage/D10 pattern — today a no-op, see §5.1). `g_reset`'s explicit copy list omits it, as it already omits `UiLanguage`. Pinned by a both-resets-keep-the-flag test plus the `notResetPropsByDesign` census entry.
- The `c_color` ↔ `LabelSpec` swap under AchromaticReticle keeps both branches Simple so the explanatory note survives the filter.

### 5.8 What does not change

- Any setting's value, range, validation, defaults; `settings.json` gains exactly one key.
- Advanced mode's tabs, rows, pages, pagination — except the one header toggle (L13).
- `SettingsService`, `SettingsUiCoordinator`, tray, toolbar, keyboard, action menu, `SettingsWindowViewModel` (the Pagination seam needs no dispatch change).
- Localization parity: every new string rides `StringTable`; no literal is introduced.

## 6. Testing strategy

**New tests**
- *Curation pin:* for each simple tab, build the source and assert the exact filtered **row sequence** (kind + id; `LabelSpec` twins by position + text predicate), in **build order**, with **all three supervision branches** built — unavailable, available+connected, available+disconnected (adds `g_supervision_retry`) — and with the pending-language-note present (`GeneralPageSourceResetDriftTests` shows the fake). (A fourth, null-service branch yields no rows in either mode — no pin needed.)
- *Tab-set drift:* `SimpleTabIds` ↔ actual Simple rows, both directions; every simple-tier source has `PrefersAutoPagination == true`.
- *Filter unit tests:* rows dropped, empty pages dropped, advanced passes through untouched.
- *Simple-mode reachability:* per simple tab, `Build → filter → Repaginate` at the narrowest supported budget; every dwell id reachable (reuses the `HandChunkedPageFitTests` machinery).
- *Sidebar model tests* (round-2 N5, round-3 I4): pure model driven headlessly in **both directions**, including "constructed in simple, switched to advanced" (N1 regression), "advanced sidebar page 2 → simple" (empty-sidebar regression), `SnapTo` and the post-snap page. The window's applier is pinned by method-body scans: faces built over `_tabDefs`, **one `RenderTabPage` after the snap**, pager panel cleared before build, `ActiveTabId` set, toggle position push present, lambda-free.
- *Toggle dispatch:* `settings_mode` under `Pagination` reaches `_pagActions` and never `ShowTab` (`SettingsPagerWiringTests.TabPager_RegistersUnderPagination` is the precedent pin).
- *Resting-gaze pin:* one flip over 200 frames, modelled on `SettingsPagerRestingGazeTests`; register radius 50, release radius 85.
- *Both resets keep the flag* + `("General", SimpleSettingsUi)` added to `notResetPropsByDesign`.
- *Toggle contrast pin:* label-on-`BtnBase` at rest and full wash; size/weight at or above the large-text threshold.
- *Geometry:* radius-50 clearance vs `pag_prev`/`pag_next` and the first content row at the narrowest supported width (title-line centre y = 24 vs the pager's ~176 — clears at any candidate radius), with the m9 description-overlap note recorded beside it.
- *Header fit:* the toggle's fit-class measurer (horizontal rule per §5.5; `FitClassTests`' per-class teeth run automatically).
- *Deep link in simple mode:* lands on the language row.

**Updated**
- `SettingsDeepLinkTests:233` **and `:234`** — both literal pins move to `VisibleTabDefs`/the model.
- `SettingsTabPaginationWiringTests:79` — the page-math location pin follows the model extraction.
- `FitSeamPinTests:119/129` — `MakeChromeButton` call count 3→4 with the toggle's box pinned; `BuildTabButtons` keeps `BuildTabFace(` and no `new Border`; a routing pin ties the instance path to `BuildModeToggle`.
- `SettingsContentFitTests.EveryHeaderStringFits_TheHeadersFixedHeight` — mirrors the header element-for-element and must mirror the **title row as max(title line, toggle + vertical margins)**, or it keeps measuring a 45.22-DIP title row and a later 48→60 toggle change would pass it (round-3 m3).
- `SettingsTests` (gated UI): default advanced unchanged (15 `Settings_Tab_*`); the optional gated simple-mode pass via `Settings_ModeToggle` (UIA id on the TextBlock). **Both force advanced at start and restore the flag in a `finally`** — `--test-mode` reads the owner's real `%APPDATA%/EyeCtrl/settings.json` (round-2 m6).
- Localization data: two new entries in `strings.json` with full metadata and the new fit class (`StringKeyCoverageTests`/ratchet/parity gates pick these up; `StringTableSchema.cs` + `tools/strings_validate.py` updated in lockstep).

**Untouched by design**
- Span guard, per-source row tests, description-completeness census, `HandChunkedPageFitTests`, pagination math, purity gate (auto-covers the new files), `SettingsTabDriftTests`/`SettingsTabLabelFitTests` (tuple keeps four members; first-occurrence `"_tabDefs ="` anchors noted in §5.2), `LanguageRowReachTests` (language row stays first in both modes; mode-blind — an optional simple-mode reach pin is noted but not required), `SettingsPageHeaderContrastTests` (by-name), `SettingsFrameLoopAllocationTests` (the push is lambda-free), `DwellWashCensusTests` (file-level exemption — L17).

## 7. Blast radius (files)

| File | Change |
|---|---|
| `SettingsRowSpec.cs` | `Simple` init property |
| `Sources/{Dwell,Cursor,Keyboard,General}PageSource.cs` | `Simple = true` on curated rows (incl. label twins, language + all supervision branches) |
| `SettingsWindow.xaml` | named header-grid host for the toggle (`HeaderPanel` keeps name/height/margin/row; `Grid(*, Auto)`) |
| `SettingsWindow.xaml.cs` | apply the sidebar model; filter step; toggle chrome (`BuildModeToggle` seam, Pagination, radius 50/release 85, dedicated one-entry dictionaries, lambda-free position push, third wash call, own methods) |
| `Views/Settings/SimpleSettingsFilter.cs` (new) | pure row filter + `SimpleTabIds` (T1's file) |
| `Views/Settings/SettingsSidebarModel.cs` (new) | pure sidebar reconciliation incl. `SnapTo` (takes `SimpleTabIds` as input) |
| `GeneralSettings.cs` | `SimpleSettingsUi` |
| `SettingsViewModel.cs` | `ResetAllSettings` capture/restore (UiLanguage pattern) |
| `src/EyeCtrl.Core/Localization/StringTableSchema.cs` | new fit class in `FitClasses` |
| `tools/strings_validate.py` | `FIT_CLASSES` parity |
| `src/EyeCtrl.Core/Localization/strings.json` | two toggle entries (who/where/settings, new fit class) |
| tests | per §6 (incl. `AppFitMeasurers` map entry) |

**`SettingsWindowViewModel.cs` is deliberately absent** — the Pagination dispatch seam makes no VM change necessary (L5).

## 8. Owner decision ledger (veto any line — the design bends)

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| L1 | Audience: owner's daily driving first, curated so a novice could also survive in it | The owner is the only live user; the app competes on usability for real gaze users | Novice-first; caregiver-only |
| L2 | Mechanism: tier flag + pipeline filter (Approach A) | Single source of truth; smallest blast radius; sources stay dumb | Parallel sources; presets |
| L3 | Simple tabs: Cursor, Dwell, Keyboard, General | Covers identity, feel, typing, startup/look/language; no external path opens a hidden tab; Cursor leads both sets so the snap target is stable | EyeCare/Actions/Toolbar/Calibration — parked (§9) |
| L4 | Curation per §5.3 (~26 rows, build-order pin, three supervision branches); new rows advanced by default; `+g_language` (r1-C1), `+d_pred` (moves with `d_key`), `+d_rad` (drift tolerance — r1's 75→90 claim did not match the live file; UX grounds only) | Every kept row is one the user feels or needs for recovery | Broader sets; hiding the language row |
| L5 | Toggle: Pagination category (Tab ids all dispatch to `ShowTab`), **hit radius 50 via one named constant, registered by a guard-aware `RegisterModeToggleTarget()` sibling** (`RegisterPagerTarget` hard-codes 60 — the trap), fixed button width, `_dwellMs`, built via `BuildModeToggle`→`MakeChromeButton` (4th caller, routing-pinned), dedicated one-entry dictionaries, lambda-free position push, third wash call, registered once in the ctor | Zero VM changes via the generic `_pagActions` arm; every wiring detail pinned | Tab category + VM special case; `RegisterPagerTarget` as-is (60/50 mismatch re-opens oscillation) |
| L6 | `SimpleSettingsUi`, default **advanced** | Existing user keeps today's behavior; missing key → false | Default simple — revisit when external users exist |
| L7 | Per-tab resets Simple; `g_resetall` advanced; UiLanguage capture/restore (no-op today, future-proofing for L6) | Resets stay gentle; a bulk reset must not flip the interface in the L6-flipped world | Resetting the flag; the Supervision section-skip |
| L8 | Only auto-pagination sources may be simple-tier | The filter's semantics are total; hand-chunked navigation can't be re-split | Teaching the filter about hand-chunked pages |
| L9 | Static `SimpleTabIds` in **`SimpleSettingsFilter.cs` (T1's file)**; the sidebar model takes it as input | Both `_tabDefs` regex parsers stay green; censused, greppable, drift-testable; T1's drift test needs no T2 dependency (round-3 m6) | Bool on the tuple; the sidebar-model file (T1/T2 coupling) |
| L10 | Switch contract: faces always over the full `_tabDefs` (r2-N1), `UpdateHeader` unfiltered, **disarm → model (with `SnapTo` + post-snap page as outputs) → snap → single post-snap `RenderTabPage` → re-label** (r3-I4), content **page resets to 0** on every switch (r3-m5), pager diff only for `tab_prev`/`tab_next` | Gaze-trap rule extends to mode hiding; every ordering gap found by rounds 2–3 lives here | Applying the sidebar before the snap (empty sidebar); clamping the page; leaving `CurrentPage` wherever it was |
| L11 | No "dwell speed" master knob in v1 | A multiplier over individually-tuned times has no coherent meaning | Scaling slider (parked, §9) |
| L12 | Toggle strings as **literal** keys (`settings.mode.show.all` / `.basic`), `where: settings`, **no manifest edit on main**; German via the T10 draft; no live re-localization | The gates read literals; the German table does not exist yet; the settings surface is already manifested | Hard-coded literals; interpolated keys; assuming a populated German table |
| L13 | Advanced mode unchanged **except the one header toggle**; header height stays 112, no pagination change anywhere | The toggle is visible in both modes by design; the measured layout needs no height | Any layout that changes the budget |
| L14 | Deep link keeps working in simple mode via model lookups; **both** literal pins (`:233`, `:234`) update | A wrong-language user's recovery path must not depend on the mode | Special-casing the deep link |
| L15 | **Header layout decided (owner, 2026-10-11):** title-line toggle, 48 DIP tall, vertical margin 0 (budget: height + vertical margins ≤ 56), short label, description spans full width, `Grid(*, Auto)` with a no-wrap clipping title. Measured: EN 103.9/112 high, 363.7/448 wide; DE (T10 draft) 103.9/112, 348.3/448; 60-DIP (115.9) and two-column full-label (211.6 EN, ∞ pseudo) vetoed. **Pseudo-locale framing corrected (r3-I2):** the description's third line is pre-existing (125.0 with no toggle); the title collision is the toggle's — non-binding under the test-only pseudo-locale, bound for shipped languages by the fit class. Reserved-row fallback dropped | The only candidate that fits English and German at the narrowest display without touching any page budget | Reserved row; two-column; 60-DIP chrome |
| L16 | **Re-arm guard (owner, r2-N3) with hysteresis (owner, r3-I3):** register at the named 50 constant, **`Disarm` at release radius 85** — larger than the hit radius, absorbing the owner's measured ~80 px top-right gaze bias; `RearmPagerTarget` case; resting-gaze pin; `ClearContentPager` stays `pag_*`-scoped; mouse-path disarm may read the border centre directly (never re-rendered) | The label invites reading; an unguarded toggle oscillates every cooldown + dwell; equal radii re-arm on edge-wander | Relying on the global cooldown; release = hit radius; `RegisterPagerTarget`'s 60 |
| L17 | Accepted debt, recorded: (a) the toggle's wash lives in a file `DwellWashCensusTests` exempts; (b) the ~256-DIP tab-list jump when the pager row appears/disappears on a switch — deliberate, user-initiated, one-time; (c) a re-arm guard held on `tab_*` at ≤1 page is harmless; (d) the m9 description-first-line overlap with the hit circle at 448 DIP — does not occur on the owner's display | Honest books beat silent exemptions | Wiring the toggle into `DwellOutlineOverlay` now; freezing the sidebar layout across modes; shrinking the hit circle to clear m9 |
| L18 | **Fit class (rewritten per r3-I1):** horizontal, A-style — *toggle label ≤ header width − widest admissible title − gap − button inset*, the title bound **derived from `tab156`'s maximum rendered at Display** (220.2 EN / 213.8 DE), not restated; measured through the **`BuildModeToggle` static seam** (routing-pinned); `StringTableSchema.cs` + `tools/strings_validate.py` + `AppFitMeasurers` updated in lockstep; the per-class teeth test runs automatically | A height measurer cannot fail (one-line label); the teeth test rejects it; the horizontal bound is the real constraint | The r3 wording ("reproduces the 448/112 box") — vetoed by round 3 |
| L19 | Declined micro-decisions: reset modals keep "Reset ⟨Tab⟩ settings to defaults?"; `d_pred`'s description stays as-is | Localization churn vs. marginal wording gains | One suffix key per tab; description rewrite |
| L20 | Gated UI tests must not depend on or mutate the owner's real `settings.json`: force advanced at start, restore in `finally` | `--test-mode` reads the real file; a persisted simple flag would break the 15-tab pin and the owner's live UI | Letting the gated tests race the owner's state |
| L21 | **Toggle label wording (owner ruling, r3-m8):** "All settings" (in simple) / "Basic" (in advanced) — names the mode you switch TO; collision-free vs the Advanced tab in English and German | "Advanced" duplicated the Advanced tab's label (EN + T10's DE "Erweitert") — a user could read the toggle as a link to that tab | "Advanced"/"Simple"; "All settings"/"Simple settings" (longest, eats title room) |

## 9. Future work (explicitly out of scope)

- Flip the default to simple when the app ships to external users (L6).
- Candidate simple additions: Eye Care enabled toggle, a Calibration entry point, an Actions subset, the Toolbar size slider.
- A "dwell speed" master slider over the simple dwell times, if the six-slider curation proves too fiddly in live use (L11).
- The header description's pseudo-locale third line (125/112 with no toggle) — the l10n fit program's pre-existing debt, surfaced by this feature's measurement.

## 10. Review history

- **Round 1 (2026-10-11):** FIX-THEN-SHIP — 2 critical (stale base pre-l10n; Tab-category toggle hijacked to `ShowTab`), 5 important, ledger partial vetoes. One factual correction returned (owner's radius is the default 75, not 90).
- **Round 2 (2026-10-11):** FIX-THEN-SHIP — 1 critical (filtered face loop crashes simple→advanced), 7 important, 10 minors; header layout + re-arm ruled by the owner against a headless measurement.
- **Round 3 (2026-10-11):** FIX-THEN-SHIP — **no criticals**; r2's fold was real. 4 important: fit class measured the wrong dimension (needed the horizontal rule + `BuildModeToggle` seam + two more files), the title line's width was never measured (pseudo "pre-existing" framing half wrong — corrected), `RegisterPagerTarget`'s hard-coded 60 is an oscillation trap (hysteresis ruled by the owner: hit 50 / release 85), and the switch steps were ordered around the snap wrongly (empty-sidebar regression; `SnapTo` became a model output). 11 minors folded (margin budget, UIA anchor on the TextBlock, header-fit mirror, `:234` pin, `CurrentPage` reset, T1/T2 file split, dictionaries + lambda-free push, label wording — ruled "All settings"/"Basic" by the owner, description-overlap note, mouse-path centre read, centre y = 24). Round 3 independently reproduced the measurement (English exact; real German from the T10 draft also fits: 348.3/448, 103.9/112) and verified the Supervisor's settings writes carry unknown keys. Scope stays three tickets: T1 pure model + filter + curation pins, T2 sidebar model + window mechanics, T3 header toggle.
