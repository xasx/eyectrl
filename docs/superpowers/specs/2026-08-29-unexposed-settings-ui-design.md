# Unexposed Settings UI — Design

**Date:** 2026-08-29
**Status:** Approved in brainstorm (this document is the written form)
**Branch:** `feat/settings-orphan-ui` (worktree `.worktrees/settings-orphan-ui`)

## Problem

Seven of the nineteen `EyeCtrlSettings.EnumerateSubSettings()` groups have no tab or control
in the settings window; their only interface is hand-editing `%APPDATA%/EyeCtrl/settings.json`
(see the `_tabDefs` audit comment in `SettingsWindow.xaml.cs`). In addition, seven scattered
single properties outside those groups are JSON-only. This design gives every one of them a
row in the window, so nothing except registry-managed model lists and one derived flag remains
JSON-only.

## Decisions (settled in brainstorm)

1. **Full parity** — all 39 rows of the seven groups get UI; no group stays JSON-only.
2. **Three new tabs** — Eye Care, Adaptation, Advanced. Sidebar goes 11 → 14 tabs.
3. **Absorb the scattered singles** — but onto their feature's tab, not Advanced:
   Calibration.TargetColor → Calibration tab, Cursor extras → Cursor tab, gaze-indicator
   visuals → Selection tab. Advanced holds only the five groups that have no tab of their own.
4. **Advanced is auto-paginated** (`PrefersAutoPagination => true`) with `LabelSpec` dash
   separators between sections, the Models-tab pattern. The accepted cosmetic risk: a section
   separator can land at the bottom of a page.

## What gets built

### 1. Sidebar (`_tabDefs` in `SettingsWindow.xaml.cs`)

Insertion order: … Calibration, **Eye Care**, **Adaptation**, General, Quick Actions,
**Advanced**.

| Id | Icon | Label | Tab description |
|---|---|---|---|
| `EyeCare` | `◑` | Eye Care | Break reminders, gaze exercises and fatigue escalation. |
| `Adaptation` | `⇄` | Adaptation | Auto-tuning of dwell and targeting when you struggle. |
| `Advanced` | `⚒` | Advanced | Expert tuning: supervision, backdrop, drag, scroll, external dwell. |

Icons are BMP/geometric symbols (no Unicode-13 emoji — tofu risk). The audit comment's
"whole sub-setting groups" bullet is deleted; the per-property bullets for the scattered
singles are deleted too. What remains intentionally unexposed: Keyboard `CustomModels`
(managed by the model registry, Models tab owns that surface) and `ManualSelection.Enabled`
(derived from `SelectionMode`, Selection tab owns it).

### 2. Eye Care tab — new `EyeCarePageSource` (`ec_` prefix, 8 rows + reset)

| Id | Label | Spec | Range / values | Default | Applies |
|---|---|---|---|---|---|
| `ec_enabled` | Enable Eye Care | Toggle | — | off | live |
| `ec_interval` | Break Interval (min) | Slider | 1–120, step 1 | 20 | live |
| `ec_break` | Natural Break Length (s) | Slider | 5–600, step 5 | 30 | live |
| `ec_exercise` | Exercise Duration (s) | Slider | 10–180, step 5 | 45 | live |
| `ec_snooze_banner` | Banner Auto-snooze (s) | Slider | 5–120, step 5 | 15 | live |
| `ec_snooze` | Snooze Length (min) | Slider | 1–60, step 1 | 5 | live |
| `ec_fatigue` | Fatigue Escalation | Toggle | — | off | live |
| `ec_fatigue_thresh` | Fatigue Threshold | Slider | 0.0–1.0, step 0.05 | 0.75 | live |

Ranges come from `EyeCareSettings.Validate()` (lines 29–34). "Live" is verified:
`EyeCareService` subscribes to `Enabled` flips; `FatigueMonitorEnabled` is read live in
`StruggleMonitor.Assess`. Rows follow with `ec_reset` ("Reset Eye Care Settings",
`ButtonKind.Reset`, 300×100, confirmation modal) constructing a fresh `EyeCareSettings` and
copying all eight properties.

### 3. Adaptation tab — new `AdaptationPageSource` (`ad_` prefix, 14 rows + reset)

| Id | Label | Spec | Range / values | Default | Applies |
|---|---|---|---|---|---|
| `ad_enabled` | Enable Adaptation | Toggle | — | off | live |
| `ad_posture` | Posture | ComboBox | Conservative / Balanced / Aggressive | Conservative | live |
| `ad_dwell` | Adapt Dwell Time | Toggle | — | on | live |
| `ad_tolerance` | Adapt Targeting Tolerance | Toggle | — | on | live |
| `ad_smoothing` | Adapt Smoothing | Toggle | — | on | live |
| `ad_geometry` | Adapt Target Size | Toggle | — | on | live |
| `ad_cue` | Show Adaptation Cue | Toggle | — | on | live |
| `ad_caregiver` | Caregiver Alerts | Toggle | — | off | live |
| `ad_sensitive` | Suppress in Sensitive Contexts | Toggle | — | on | live |
| `ad_frac_dwell_short` | Dwell — Shorter Limit | Slider | 0.0–1.0, step 0.05 | 0.40 | live |
| `ad_frac_dwell_long` | Dwell — Longer Limit | Slider | 0.0–1.0, step 0.05 | 0.50 | live |
| `ad_frac_tol_loose` | Tolerance — Looser Limit | Slider | 0.0–1.0, step 0.05 | 0.50 | live |
| `ad_frac_smooth` | Smoothing — More Limit | Slider | 0.0–1.0, step 0.05 | 0.50 | live |
| `ad_frac_geom` | Target Size — Bigger Limit | Slider | 0.0–1.0, step 0.05 | 0.50 | live |

Fraction ranges match `GuardFraction` (0.0–1.0) and `Validate()`. "Live" is verified:
`EffectiveSettingsProvider` reads the gate and the fractions on every use. Each fraction
row's description must say what the number is *of* (baseline dwell, fixation radius, filter
parameter, target geometry) — the setting name alone does not carry that. `ad_reset` resets
all fourteen properties from a fresh `AdaptationSettings`.

### 4. Advanced tab — new `AdvancedPageSource` (`av_` prefix, 19 rows + reset)

19 rows carry the five groups' 17 settings — `FallbackAtOrder` is one setting rendered as
three position combos (§4 swap semantics). One `PageSpec`, auto-paginated, sections
separated by `LabelSpec(() => new string('─', 50), Wrap: false)` (the Models-tab dash rule).
Section order: Supervision, Backdrop, Drag, Scroll, External Dwell.

| Id | Label | Spec | Range / values | Default | Applies |
|---|---|---|---|---|---|
| `av_sup_enabled` | Enable Supervision | Toggle | — | off | **restart** |
| `av_sup_retries` | Max Restart Attempts | Slider | 1–20, step 1 | 3 | **restart** |
| `av_sup_crashwin` | Crash Window (s) | Slider | 10–3600, step 30 | 120 | **restart** |
| `av_sup_hang` | Hang Threshold (s) | Slider | 5–300, step 5 | 20 | **restart** |
| `av_sup_grace` | Startup Grace (s) | Slider | 10–600, step 5 | 60 | **restart** |
| `av_sup_fb1` | Fallback Assistive Tech — 1st | ComboBox | swap (below) | TD Control | **restart** |
| `av_sup_fb2` | Fallback Assistive Tech — 2nd | ComboBox | swap (below) | SnapCoreFirst | **restart** |
| `av_sup_fb3` | Fallback Assistive Tech — 3rd | ComboBox | swap (below) | Windows Eye Control | **restart** |
| `av_backdrop_enabled` | Enable Backdrop | Toggle | — | on | verify |
| `av_backdrop_type` | Backdrop Type | ComboBox | Acrylic / Mica / SolidDarken | Acrylic | verify |
| `av_backdrop_opacity` | Backdrop Opacity | Slider | 0.1–0.9, step 0.05 | 0.3 | verify |
| `av_drag_settle` | Drag Settle Dwell (ms) | Slider | 200–2000, step 50 | 500 | verify |
| `av_drag_radius` | Drag Settle Radius (px) | Slider | 10–200, step 5 | 40 | verify |
| `av_drag_color` | Drag Trail Color | ColorPicker | hex | AccentWarning | verify |
| `av_drag_opacity` | Drag Trail Opacity | Slider | 0.1–1.0, step 0.05 | 0.7 | verify |
| `av_scroll_sens` | Scroll Sensitivity | Slider | 0.5–2.0, step 0.1 | 1.0 | verify |
| `av_ext_enabled` | Enable External Dwell | Toggle | — | on | live |
| `av_ext_dwell` | External Dwell (ms) | Slider | 200–3000, step 50 | 800 | live |
| `av_ext_cooldown` | External Cooldown (ms) | Slider | 0–5000, step 100 | 400 | live |

Notes:
- **Supervision is restart-only**: `App.xaml.cs` reads `Supervision.Enabled` at launch (lines
  150, 260) and the supervisor is an external process. Every Supervision description must say
  so; none may imply an immediate effect. (Note `ExternalDwellSettings.CooldownMs` floor
  really is 0 — unlike the dwell tab's `d_cooldown`, no slider-minimum fix is needed.)
- **"verify"** rows (Backdrop/Drag/Scroll): whether overlays re-read these live or only when
  their window is rebuilt is not yet traced. The implementation must verify per property
  before writing its description; a description may only promise what the code proves.
- **Fallback-order swap semantics**: `FallbackAtOrder` is a `List<string>` of the three AT
  names. Each combo shows the AT at that position, options are all three names — labels are
  display names ("TD Control"), the stored values are the list strings ("TDControl").
  Selecting an AT that currently sits at another position **swaps the two list entries**
  (so a position is never duplicated and none is orphaned); selecting the already-current
  entry is a no-op. After any swap: `_ctx.ApplyAndSave()` and all three combos refresh.
- `av_reset` ("Reset Advanced Settings", confirmation modal) constructs fresh
  `BackdropSettings`, `DragSettings`, `ScrollSettings`, `ExternalDwellSettings` and replaces
  those four groups — it must not touch Supervision, whose enablement is restart-coupled and
  whose reset would be surprising mid-session. Supervision rows return to defaults only by
  hand or via the existing "Reset ALL Settings" on General.

### 5. Existing-tab additions

**Calibration tab** (`CalibrationPageSource`, page 1, after the four sliders):

| Id | Label | Spec | Range | Default |
|---|---|---|---|---|
| `cal_color` | Target Color | ColorPicker | hex | `SettingsDefaults.Accent` |

**Cursor tab** (`CursorPageSource`):

| Id | Label | Spec | Range / values | Default |
|---|---|---|---|---|
| `c_gap` | Gap Interpolation (ms) | Slider | 0–500, step 25 | 150 |
| `c_pos_style` | Position Indicator | ComboBox | Crosshair / Pin | Crosshair |

**Selection tab** (`SelectionPageSource`):

| Id | Label | Spec | Range (new — see §6) | Default |
|---|---|---|---|---|
| `s_gi_outer` | Gaze Ring Diameter (px) | Slider | 20–200 | 60 |
| `s_gi_dot` | Centre Dot (px) | Slider | 2–40 | 10 |
| `s_gi_stroke` | Ring Stroke (px) | Slider | 0.5–10 | 2 |
| `s_gi_opacity` | Ring Opacity | Slider | 0.1–1.0 | 0.7 |

### 6. Core change: validate the gaze-indicator visuals

`ManualSelectionSettings.Validate()` currently covers only `CircleRadiusPixels` and
`MagnificationLevel`; the four `GazeIndicator*` properties are entirely unvalidated. Add
`ValidateRange` entries for the four ranges above. Defaults (60 / 10 / 2 / 0.7) sit inside
the new ranges, so no persisted value can start failing validation. No setter guards are
added — the house pattern is slider minima ≥ setter floor enforced by
`SettingsSliderSpanGuardTests`, and there is no floor to enforce here.

## Descriptions

Every new row carries a `Description` — `SettingsRowDescriptionCompletenessTests` fails the
build otherwise. Style rules inherited from the 45-row pass: British prose, American labels,
one sentence per row, every behavioural claim verified against source (the live/restart
matrix above is the starting point). The three tab descriptions are in §1.

## Testing

**Extend existing suites:**
- `SettingsTabDriftTests` — expects 14 tabs, parses the three new `_tabDefs` entries.
- `SettingsTabLabelFitTests` — "Eye Care", "Adaptation", "Advanced" fit the sidebar slot.
- `SettingsRowDescriptionCompletenessTests` — the three new sources plus the 7 rows added to
  existing sources are all describable and described.
- `SettingsSliderSpanGuardTests` — every new slider's minimum ≥ the property's guard floor
  (this test exists because of the `d_cooldown` crash; all new minima here come straight from
  `Validate()` ranges, so it should pass by construction).
- Pitch/fit suites (`SettingsRowPitchTests`, `HandChunkedPageFitTests`) over the new pages.
- `SettingsRenderMappingTests` — only if it enumerates sources (it has flaked before; run the
  whole App suite, not that class alone).

**New tests:**
- `Settings/Pages/EyeCarePageSourceTests`, `AdaptationPageSourceTests`,
  `AdvancedPageSourceTests` — following `CursorPageSourceTests` style: row presence, ids,
  callbacks via `RecordingSettingsPageContext`, reset semantics.
- Fallback-order swap: select-occupied-position swaps both entries; select-current is a
  no-op; all three combos refresh after a swap; the list round-trips through
  `ApplyAndSave`.
- `ManualSelectionSettings` validation (Core tests): the four new range checks accept
  defaults and reject out-of-range values.
- Existing `CalibrationPageSource` / `CursorPageSource` / selection page-source tests extend
  for the 7 added rows.

## Non-goals

- No new row-spec types (fallback order is three combos; sections are LabelSpec rules).
- No feature-behaviour changes — the only Core edit is the four added `Validate()` entries.
- No rewording of existing rows' descriptions.
- No exposure of `Keyboard.CustomModels` (registry-managed) or `ManualSelection.Enabled`
  (SelectionMode-derived).

## Risks / open items

- **Section separator at page bottom** on the Advanced tab — accepted (auto-pagination is
  height-blind; self-heals when rows change).
- **Backdrop/Drag/Scroll live-vs-rebuild semantics** — verify per property before writing
  descriptions (§4 "verify" rows). Mica on Windows 10: check the backdrop service's fallback
  before the Type description promises anything.
- **Sidebar fit at 14 tabs** — pagination handles it structurally; the label-fit tests pin
  the three new labels. Watch that `◑ ⇄ ⚒` render in the sidebar font (the fit test catches
  tofu).
