# Unexposed Settings UI — Design

**Date:** 2026-08-29 (rev 2, same day — external review verdict processed: 9 blockers,
8 should-fixes, 4 nits, all verified against source and folded in)
**Status:** Approved in brainstorm; re-scoped after external review
**Branch:** `feat/settings-orphan-ui` (worktree `.worktrees/settings-orphan-ui`)

## Problem

Six of the nineteen `EyeCtrlSettings.EnumerateSubSettings()` groups have no tab or control
in the settings window; their only interface is hand-editing `%APPDATA%/EyeCtrl/settings.json`
(see the `_tabDefs` audit comment in `SettingsWindow.xaml.cs`). In addition, scattered
single properties outside those groups are JSON-only — and the audit comment itself is
incomplete: it lists `Cursor.MaxGapInterpolationMs` and `Cursor.PositionIndicatorStyle` but
misses `Cursor.CircleSize`, `Cursor.OneEuroMinCutoff` and `Cursor.OneEuroBeta`, and its
`GazeIndicator*` bullet under-counts (five properties, not four).

**Correction after review:** Supervision was mis-classified as orphaned. It has working UI
in the supervisor's own process — `SupervisorSetupWindow` exposes `Enabled`, `MaxRetries`,
`HangThresholdSeconds` and a read-only `FallbackAtOrder`, persisted through
`SettingsWriter.Supervisor` with live policy application (`Program.cs` save callback).
The App process **cannot** persist that section at all: `SettingsWriter.App` owns
everything *except* Supervision, and `MergeForWriter` overlays the on-disk Supervision
section over every App save (`SettingsService.cs` MergeForWriter branch). Supervision rows
in this window would silently discard their edits. They stay in the supervisor's window.

This design gives UI to everything the App process owns, so nothing App-owned remains
JSON-only except the two deliberate exclusions below.

## Decisions (settled in brainstorm; #1 re-scoped after review)

1. **Full parity** — every App-owned orphaned setting gets UI; hand-editing settings.json
   becomes obsolete. Intentionally still without a row here: Keyboard `CustomModels`
   (registry-managed, Models tab owns that surface), `ManualSelection.Enabled`
   (SelectionMode-derived, Selection tab owns it), and `Adaptation.CaregiverAlerts`
   (zero production consumers — an inert toggle would be a UI lie; revisit in Phase 3).
2. **Three new tabs** — Eye Care, Adaptation, Advanced. Sidebar goes 11 → 14 tabs.
3. **Scattered singles go to their feature tabs**, not Advanced: Cursor extras → Cursor tab,
   gaze-indicator visuals → Selection tab, `Calibration.TargetColor` → Calibration tab.
   Advanced holds only the four App-owned groups with no tab of their own: Backdrop, Drag,
   Scroll, External Dwell. (Supervision removed by review — see Problem.)
4. **All three new sources opt into auto-pagination** (`PrefersAutoPagination => true`).
   `HandChunkedPageFitTests` reflects over every concrete source and treats a source that
   does not opt in as hand-chunked against a ~800 DIP page budget (~5 rows) — a 9-row Eye
   Care page or 14-row Adaptation page would fail that gate if it forgot the opt-in.
   Advanced uses `LabelSpec` dash separators between sections (Models-tab pattern); the
   accepted cosmetic risk is a separator landing at a page bottom.

## What gets built

### 1. Sidebar (`_tabDefs` in `SettingsWindow.xaml.cs`)

Insertion order: … Calibration, **Eye Care**, **Adaptation**, General, Quick Actions,
**Advanced**.

| Id | Icon | Label | Tab description |
|---|---|---|---|
| `EyeCare` | `◑` | Eye Care | Break reminders, gaze exercises and fatigue escalation. |
| `Adaptation` | `⇄` | Adaptation | Auto-tuning of dwell and targeting when you struggle. |
| `Advanced` | `⚒` | Advanced | Expert tuning: backdrop, drag, scroll and external dwell. |

The audit comment is rewritten to be **complete**: per-property entries for
`Cursor.CircleSize`, `OneEuroMinCutoff`, `OneEuroBeta` are added before the comment
shrinks; `Supervision` is re-pointed at `SupervisorSetupWindow` ("has its own UI in the
supervisor process; the App cannot persist that section — SettingsWriter.Supervisor owns
it"), not deleted as if it never existed. What remains intentionally unexposed after this
design: `CustomModels`, `ManualSelection.Enabled`, `CaregiverAlerts` (above).

### 2. Eye Care tab — new `EyeCarePageSource` (`ec_` prefix, 8 rows + reset)

`PrefersAutoPagination => true`.

| Id | Label | Spec | Range / values | Default | Applies |
|---|---|---|---|---|---|
| `ec_enabled` | Enable Eye Care | Toggle | — | off | live |
| `ec_interval` | Break Interval (min) | Slider | 1–120, step 1 | 20 | live |
| `ec_break` | Natural Break Length (s) | Slider | 5–600, step 5 | 30 | live |
| `ec_exercise` | Exercise Duration (s) | Slider | 10–180, step 5 | 45 | live |
| `ec_snooze_banner` | Banner Auto-snooze (s) | Slider | 5–120, step 5 | 15 | **restart** |
| `ec_snooze` | Snooze Length (min) | Slider | 1–60, step 1 | 5 | live |
| `ec_fatigue` | Fatigue Escalation | Toggle | — | off | live |
| `ec_fatigue_thresh` | Fatigue Threshold | Slider | 0.0–1.0, step 0.05 | 0.75 | live |

Ranges from `EyeCareSettings.Validate()`. Live/restart cells are traced:
`EyeCareScheduler` reads interval/natural-break/snooze per call; `ExerciseDurationSeconds`
at `EyeCareService.cs:124`; `FatigueMonitorEnabled`/`FatigueThreshold` per sample;
`Enabled` both per-sample and via `PropertyChanged`. **`BannerAutoSnoozeSeconds` is the
exception** — it is snapshotted into the singleton `EyeCareHooks` at DI construction
(`AppBootstrapper.cs`), so its description must say it applies after a restart.
`ec_reset` copies all eight properties from a fresh `EyeCareSettings` (per-property copy;
group objects are never replaced — services hold references).

### 3. Adaptation tab — new `AdaptationPageSource` (`ad_` prefix, 13 rows + reset)

`PrefersAutoPagination => true`. `CaregiverAlerts` is deliberately absent (no consumer).

| Id | Label | Spec | Range / values | Default | Applies |
|---|---|---|---|---|---|
| `ad_enabled` | Enable Adaptation | Toggle | — | off | live |
| `ad_posture` | Posture | ComboBox | Conservative / Balanced / **Responsive** | Conservative | live |
| `ad_dwell` | Adapt Dwell Time | Toggle | — | on | live |
| `ad_tolerance` | Adapt Targeting Tolerance | Toggle | — | on | live |
| `ad_smoothing` | Adapt Smoothing | Toggle | — | on | live |
| `ad_geometry` | Adapt Target Size | Toggle | — | on | live |
| `ad_cue` | Show Adaptation Cue | Toggle | — | on | live |
| `ad_sensitive` | Suppress in Sensitive Contexts | Toggle | — | on | live |
| `ad_frac_dwell_short` | Dwell — Shorter Limit | Slider | 0.0–1.0, step 0.05 | 0.40 | live |
| `ad_frac_dwell_long` | Dwell — Longer Limit | Slider | 0.0–1.0, step 0.05 | 0.50 | live |
| `ad_frac_tol_loose` | Tolerance — Looser Limit | Slider | 0.0–1.0, step 0.05 | 0.50 | live |
| `ad_frac_smooth` | Smoothing — More Limit | Slider | 0.0–1.0, step 0.05 | 0.50 | live |
| `ad_frac_geom` | Target Size — Bigger Limit | Slider | 0.0–1.0, step 0.05 | 0.50 | live |

The third posture value is `Responsive` (not "Aggressive" — `AdaptationPosture.cs`).
All cells live: `EffectiveSettingsProvider` reads gate and fractions per call; posture and
the four adapt toggles in `AdaptationController`; cue in `AdaptationCueService`; sensitive
suppression in `SensitiveContextFeedbackGate`. Each fraction description must say what the
number is *of* (baseline dwell, fixation radius, filter parameter, target geometry).
`ad_reset` copies all thirteen properties from a fresh `AdaptationSettings`.

### 4. Advanced tab — new `AdvancedPageSource` (`av_` prefix, 11 rows + reset)

`PrefersAutoPagination => true`. Sections separated by `LabelSpec(() => new string('─',
50), Wrap: false)`; 11 setting rows + 3 separators + reset are emitted. Section order:
Backdrop, Drag, Scroll, External Dwell. **No Supervision section** — see Problem.

| Id | Label | Spec | Range / values | Default | Applies |
|---|---|---|---|---|---|
| `av_backdrop_enabled` | Enable Backdrop | Toggle | — | on | next open |
| `av_backdrop_type` | Backdrop Type | ComboBox | Acrylic / Mica / SolidDarken | Acrylic | next open |
| `av_backdrop_opacity` | Backdrop Opacity | Slider | 0.1–0.9, step 0.05 | 0.3 | next open |
| `av_drag_settle` | Drag Settle Dwell (ms) | Slider | 200–2000, step 50 | 500 | **restart** |
| `av_drag_radius` | Drag Settle Radius (px) | Slider | 10–200, step 5 | 40 | **restart** |
| `av_drag_color` | Drag Trail Color | ColorPicker | hex | AccentWarning | next drag |
| `av_drag_opacity` | Drag Trail Opacity | Slider | 0.1–1.0, step 0.05 | 0.7 | next drag |
| `av_scroll_sens` | Scroll Sensitivity | Slider | 0.5–2.0, step 0.1 | 1.0 | live |
| `av_ext_enabled` | Enable External Dwell | Toggle | — | on | live |
| `av_ext_dwell` | External Dwell (ms) | Slider | 200–3000, step 50 | 800 | at registration |
| `av_ext_cooldown` | External Cooldown (ms) | Slider | 0–5000, step 100 | 400 | live |

Traced semantics (these replace the rev-1 "verify" placeholders):
- **Backdrop** — `ApplyBackdrop(settings)` runs fresh on every backdrop show
  (`OverlayVmBindingCoordinator` calls it when the action menu / magnifier opens): all
  three rows apply from the next open, no restart. **Opacity trap:** `Opacity` is consumed
  only by the SolidDarken fallback path (`GlassBackdropWindow.ApplySolidDarken`); when
  Acrylic or Mica succeeds the slider does nothing. Its description must say so. Mica
  itself degrades gracefully below Windows 11 build 22000 (falls back to legacy acrylic
  then solid darken) — no Win-11 caveat needed in the Type description.
- **Drag** — split. `TrailColor`/`TrailOpacity` are re-read at each drag start
  (`DragCoordinator.Configure`): live from the next drag. `SettleDwellMs`/
  `SettleRadiusPixels` are baked into a `FixationDetector` inside the **singleton**
  `DragTrailViewModel`'s constructor: restart-only.
- **Scroll** — `Sensitivity` is read inside the scroll message handler: live.
- **External Dwell** — `Enabled` is checked per gaze frame, `CooldownMs` via
  `PropertyChanged`: live. `DefaultDwellMs` is read per *target registration* (not per
  activation) — the description should say the duration applies when a target registers.

`av_reset` ("Reset Advanced Settings", confirmation modal) copies every property of the
four groups from fresh instances onto the live settings objects — per-property copy, the
General-tab pattern; group objects are never replaced.

### 5. Existing-tab additions

**Calibration tab** (`CalibrationPageSource`, page 1, after the four sliders):

| Id | Label | Spec | Range | Default | Applies |
|---|---|---|---|---|---|
| `cal_color` | Target Color | ColorPicker | hex | `SettingsDefaults.Accent` | verify |

**Cursor tab** (`CursorPageSource`) — appended **before `c_reset`**: auto-pagination
flattens all PageSpecs' rows in order, so the reset button must stay last.

| Id | Label | Spec | Range / values | Default | Applies |
|---|---|---|---|---|---|
| `c_gap` | Gap Interpolation (ms) | Slider | 0–500, step 25 | 150 | live |
| `c_pos_style` | Position Indicator | ComboBox | Crosshair / Pin | Crosshair | verify |
| `c_size` | Circle Size (px) | Slider | 10–500, step 10 | 250 | verify |
| `c_euro_cutoff` | One Euro Min Cutoff | Slider | 0.1–20.0, step 0.1 | 5.0 | verify |
| `c_euro_beta` | One Euro Beta | Slider | 0.0–5.0, step 0.1 | 0.5 | verify |

Ranges from `CursorSettings.Validate()`. `c_gap` is verified live (`GazeEngine` rebuilds
the interpolator on `PropertyChanged`). The other three "verify" cells are traced at
implementation before their descriptions are written — the same rule as every cell here:
a description may only promise what the code proves. **`c_reset` must be extended** to
copy all five new properties (it currently copies five; it will copy ten).

**Selection tab** (`SelectionPageSource`):

| Id | Label | Spec | Range (new — see §6) | Default | Applies |
|---|---|---|---|---|---|
| `s_gi_outer` | Gaze Ring Diameter (px) | Slider | 20–200 | 60 | **restart** |
| `s_gi_dot` | Centre Dot (px) | Slider | 2–40 | 10 | **restart** |
| `s_gi_stroke` | Ring Stroke (px) | Slider | 0.5–10 | 2 | **restart** |
| `s_gi_opacity` | Ring Opacity | Slider | 0.1–1.0 | 0.7 | **restart** |
| `s_gi_anim` | Reset Animation (ms) | Slider | 0–1000, step 50 | 100 | **restart** |

All five are restart-only: the gaze-indicator view model's `Configure` is called exactly
once, at startup (`AppBootstrapper.ResolveViewModels`). (Selection and Calibration have no
per-tab settings-reset button — `cal_clear` clears calibration *data* — so nothing to
extend there.)

### 6. Core change: clamp-on-load + validate the gaze-indicator properties

The five `GazeIndicator*` properties (`OuterDiameter`, `CenterDotDiameter`,
`StrokeThickness`, `Opacity`, `ResetAnimationMs`) are unvalidated today, and
`SettingsService.Load()` returns a **whole-file default** on any validation error. Adding
bare `Validate()` ranges would therefore let a previously-legal hand-edited value (e.g.
`GazeIndicatorOuterDiameter: 250`) wipe the user's entire settings file on next launch —
exactly the users this feature serves. Two-part fix:

1. **Clamp on load**, inside `Load()` before validation, following the existing in-Load
   migration pattern (the SelectionMode seed from `ManualSelection.Enabled`): clamp each
   of the five properties into its new range and log when a value moved. A persisted
   out-of-range value survives as clamped — never as a file wipe.
2. **Add the five `ValidateRange` entries** (ranges above; `ResetAnimationMs` 0–1000) so
   future writes are checked. Slider ranges equal Validate ranges exactly, so the UI
   cannot introduce a violation; `SettingsSliderSpanGuardTests` pins that equality — see
   Testing. (Rev 1's "there is no floor to enforce here" was wrong: the span guard's
   purpose is precisely to pin slider spans against Validate-only guards, whose failure
   mode is a swallowed `Save` — the value applies live and is silently lost on restart.)

Pre-existing and out of scope: `CircleSize`/`OneEuro*` already have Validate ranges, so a
hand-edited violation there already risks the whole-file default today; this design does
not widen that behaviour. Recorded as an observation, ticket-worthy separately.

## Descriptions

Every new row carries a `Description` — `SettingsRowDescriptionCompletenessTests` fails
the build otherwise. Style rules inherited from the 45-row pass: British prose, American
labels, one sentence per row, every behavioural claim matching the traced live/restart
cells above. 43 describable rows total (8 + 13 + 11 + 1 + 5 + 5). The three tab
descriptions are in §1; the Advanced one (57 chars) stays under the longest existing
description, which `SettingsContentFitTests.EveryHeaderStringFits_TheHeadersFixedHeight`
measures against the header panel's fixed height.

## Testing

**Extend existing suites:**
- `SettingsTabDriftTests` parses `_tabDefs` and compares against the **`expectedTabs` array
  in `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs`** (`Settings_Tab_*` literals) — the
  plan must add `Settings_Tab_EyeCare`, `Settings_Tab_Adaptation`, `Settings_Tab_Advanced`
  there; the drift test itself needs no new literals.
- `SettingsSliderSpanGuardTests` asserts **exact slider counts per source** — `HasSize(3)`
  for Cursor becomes **7** (`c_smooth`, `c_opacity`, `c_thick` + the four new sliders),
  `HasSize(5)` for Selection becomes **10** — and every new slider needs a `SpanInside`
  pin against its Validate range, including all five `s_gi_*` rows. New per-source Facts
  for `EyeCarePageSource` (6 sliders), `AdaptationPageSource` (5), `AdvancedPageSource` (7).
- `SettingsRowDescriptionCompletenessTests` — the gate's source list is hand-maintained (a
  source added to the codebase but not to the test's array ships uncovered), so the plan
  must consciously add the three new sources to that array. Buttons are exempt by design
  (the gate filters to slider/toggle/combo/colour-picker rows), so the three reset buttons
  need no descriptions.
- Pitch/fit suites (`SettingsRowPitchTests`, `HandChunkedPageFitTests`) cover the new
  sources automatically via reflection once `PrefersAutoPagination => true` is set.
- `SettingsContentFitTests.EveryHeaderStringFits_TheHeadersFixedHeight` measures the real
  tab descriptions; the new Advanced description is written to fit.
- `SettingsTabLabelFitTests` needs **no extension** — it self-parses `_tabDefs` with a
  loose floor; the three labels are covered the moment they are added.
- `SettingsRenderMappingTests` unaffected (pins `FillFor` brush mapping per `ButtonKind`;
  no new `ButtonKind`).

**New tests:**
- `Settings/Pages/EyeCarePageSourceTests`, `AdaptationPageSourceTests`,
  `AdvancedPageSourceTests` — following `CursorPageSourceTests` style: row presence, ids,
  callbacks via `RecordingSettingsPageContext`, reset semantics.
- `ManualSelectionSettings` load-clamp + validation (Core tests): an out-of-range
  persisted value is clamped (not wiped); the five new range checks accept defaults and
  reject out-of-range values.
- Existing `CalibrationPageSource` / `CursorPageSource` / selection page-source tests
  extend for the 11 added rows, including the extended `c_reset` copy list.

## Non-goals

- No new row-spec types (sections are LabelSpec rules; the fallback-order combos died with
  the Supervision section).
- No Supervision UI in the app window — single-writer architecture puts that section in
  the supervisor's own setup window; changing that is its own design.
- No feature-behaviour changes — the only Core edits are the five Validate entries and the
  load-time clamp.
- No exposure of `CustomModels`, `ManualSelection.Enabled`, or `CaregiverAlerts`.
- No rewording of existing rows' descriptions.

## Risks / open items

- **Section separator at page bottom** on the Advanced tab — accepted.
- **Icon glyphs**: `◑ ⇄ ⚒` are BMP/geometric symbols, but `SettingsTabLabelFitTests` only
  checks `Icon.Length > 0` — it cannot catch a tofu box. Glyph rendering needs one visual
  confirmation when the tabs first render; if any of the three draws badly, swap it before
  merge (the drift/label tests don't pin the characters themselves).
- **Untraced "verify" cells** (`c_pos_style`, `c_size`, `c_euro_cutoff`, `c_euro_beta`,
  `cal_color`): traced at implementation; descriptions written only after.
