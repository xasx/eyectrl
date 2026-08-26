# Settings Row Descriptions — Design

Date: 2026-08-26 · Status: awaiting review · Branch: `feat/settings-descriptions`

## Context

The settings window renders each row as a label plus an optional second-line
description (`SettingsRowSpec.Description`, drawn at subtitle size in
`TextSecondary`, contrast-gated per theme by `SettingsRowDescriptionContrastTests`).
The infrastructure shipped with the two-column-rows rework (2026-08-20), which
described the Cursor tab's five rows and explicitly left the remaining tabs as
"a content pass the user should drive". All eleven tab-header descriptions
already exist (`SettingsWindow.xaml.cs` `_tabDefs`).

This spec is that content pass: description copy for every describable row on
the nine remaining auto-paginated tabs, plus a completeness test so future
settings rows cannot ship bare.

## Goal / Non-goals

**Goal.** Every slider, toggle, combo box and colour picker row on the ten
auto-paginated tabs (Cursor + nine) carries a short, truthful, user-facing
description.

**Non-goals.** No infrastructure changes; no Quick Actions rows (that tab is
hand-chunked and height-gated by `HandChunkedPageFitTests` — descriptions would
grow rows out of the tested budget); no descriptions on `ButtonSpec`/`LabelSpec`
(they render no description line); no touching the ~30 settings that have no
live UI row; no localization.

## Copy rules

Generalizing the Cursor house style:

- One sentence, roughly 4–12 words. Gaze reading is slow, and description wrap
  grows rows and pagination.
- Plain language: no enum names, property names, or internals. Say what the
  user sees happening.
- Directional guidance where it helps a decision ("bigger keys are easier to
  hit"), not just naming the field.
- Don't repeat slider ranges — the slider already shows them. Labels that
  already carry units ("(ms)", "(px)") are not re-explained.
- Truth rule: every line below is grounded in the Core XML doc comments and the
  code paths cited in the inventory. Nothing invented.
- **Spelling: British prose.** The existing description voice uses British
  spellings — Cursor's "colour", the Layout tab header's "screen centre" —
  while control labels stay American ("Circle Color"). This pass follows the
  prose voice (colour, centre) and changes no existing copy.
  *(Amendment to the presented design, which proposed normalizing Cursor's
  "colour" → "color"; the tab-header evidence says the prose voice is British,
  so nothing is normalized. Flagged for reviewer sign-off.)*

## Inventory — the copy

45 new descriptions, verified against the code noted per tab. Cursor's five
existing descriptions are unchanged.

### Dwell Times (`DwellPageSource.cs`, grounding: `DwellSettings.cs`)

| Id | Label | Description |
|---|---|---|
| d_fix | Fixation Detection (ms) | How long your gaze must hold steady before a dwell can start. |
| d_act | Activation Button (ms) | Dwell for the button that appears at your gaze. |
| d_menu | Action Menu (ms) | How long to rest on an action in the ring. |
| d_ManualSelection | ManualSelection Action Menu (ms) | Dwell for the action ring inside manual selection. |
| d_tool | Toolbar / Settings (ms) | How long to rest on toolbar and settings buttons. |
| d_key | Keyboard Key (ms) | How long to rest on a key before it types. |
| d_pred | Prediction (ms) | Dwell for the word suggestions above the keyboard. |
| d_rad | Fixation Radius (px) | How far your gaze may drift within a fixation. |
| d_cooldown | Dwell Cooldown (ms) | How long a new dwell must wait after an activation. |

d_act grounding: on fixation `GazeEngineAdapter` sends
`ShowActivationButtonMessage(point)`; the shown button dwells with
`ActivationButtonDwellMs` (`ActivationButtonViewModel.cs:38`).

### Selection (`SelectionPageSource.cs`, grounding: `ManualSelectionSettings.cs`, `AutoSelectionSettings.cs`, `EyeCtrlSettings.cs`)

| Id | Label | Description |
|---|---|---|
| s_mode | Selection Mode | Manual zooms in around your gaze; Auto snaps to controls. |
| s_rad | Circle Radius (px) | Radius of the magnifier's zoom circle. |
| s_mag | Magnification Level | How strongly the zoom circle magnifies. |
| s_grace | Grace Period (ms) | Dwell waits this long after manual selection opens. |
| s_highlight | Highlight Color | Colour of the box drawn around the control you selected. |
| s_highlightThickness | Highlight Thickness | Line weight of the highlight box. |
| s_snapRadius | Snap Search Radius (px) | How far from your gaze Auto Selection searches for a control. |

### Action Menu (`ActionMenuPageSource.cs`, grounding: `InteractionAction`, `KeyboardInteractionDispatcher.cs`, `DragSettings.cs`, `ScrollSettings.cs`)

One toggle per action; each description says what picking that action does.

| Id | Label | Description |
|---|---|---|
| am_LeftClick | Left Click | One left click at the point you chose. |
| am_RightClick | Right Click | One right click at the point you chose. |
| am_DoubleClick | Double Click | Two quick clicks at the point you chose. |
| am_Drag | Drag | Pick up, move and drop by trailing your gaze. |
| am_ScrollUp | Scroll Up | Scrolls up from the point you chose. |
| am_ScrollDown | Scroll Down | Scrolls down from the point you chose. |
| am_ScrollLeft | Scroll Left | Scrolls left from the point you chose. |
| am_ScrollRight | Scroll Right | Scrolls right from the point you chose. |
| am_ScrollMode | Scroll Mode | Continuous scrolling, steered by where you look. |
| am_PinNote | Pin Note | Pins a typed note at the point you chose. |

PinNote grounding: `KeyboardInteractionDispatcher.cs:183` →
`NoteManager.CreateNoteAt(pt)`.

### Layout (`LayoutPageSource.cs`, grounding: `EyeCtrlSettings.ViewPosition`)

| Id | Label | Description |
|---|---|---|
| lay_pos | Position | Where overlays open: under your gaze, or at screen centre. |

### Keyboard (`KeyboardPageSource.cs`, grounding: `KeyboardSettings.cs`)

| Id | Label | Description |
|---|---|---|
| k_layout | Active Layout | Key arrangement; QWERTZ suits German, AZERTY suits French. |
| k_size | Key Size Multiplier | Scales every key; bigger keys are easier to hit. |
| k_pred | Word Prediction | Shows suggested words above the keyboard while you type. |
| k_lang | Dictionary Language | Which language the word suggestions draw from. |
| k_sound | Click Sound | Plays a sound each time a key types. |
| k_edge | Screen Edge | The screen edge the keyboard docks to. |

### Models (`ModelsPageSource.cs`, grounding: `ModelManagementService.SetActiveModel`)

| Id | Label | Description |
|---|---|---|
| md_active | Active Model | Which downloaded model powers word predictions; Off leaves dictionary suggestions. |

### Toolbar (`ToolbarPageSource.cs`, grounding: `ToolbarSettings.cs`)

| Id | Label | Description |
|---|---|---|
| t_dock | Dock to Screen Edge | Keeps the toolbar attached to a screen edge. |
| t_edge | Docked Edge | Which screen edge the toolbar attaches to. |
| t_size | Button Size | Size of each button; bigger ones are easier to aim at. |

### Calibration (`CalibrationPageSource.cs`, grounding: `CalibrationSettings.cs`)

| Id | Label | Description |
|---|---|---|
| cal_points | Calibration Points | How many points the calibration run visits. |
| cal_samples | Samples per Point | Gaze samples gathered at each calibration point. |
| cal_hold | Target Hold (ms) | Each target stays on screen at least this long. |
| cal_settle | Settle Window (ms) | Time at a target before its samples start counting. |

cal_hold deliberately says "at least": `TargetHoldMs` is a visible floor, not a
timer — the real pacer is gaze arrival plus post-settle samples.

### General (`GeneralPageSource.cs`, grounding: `ThemeSettings.cs`, `GeneralSettings.cs`, `TrackStatusSettings.cs`, `TrackStatusWindow`)

| Id | Label | Description |
|---|---|---|
| g_theme | Theme (restart to apply) | The colour scheme the whole app uses. |
| g_boot | Start on Windows Boot | Opens EyeCtrl automatically when you sign in. |
| g_pause | Start Paused | Starts with tracking paused until you switch it on. |
| g_track | Show Track Status | A corner box showing live eye-tracker status. |

g_theme leaves the restart note to the label, which already says it.

## Completeness test

New `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs`:

- Build each of the eleven page sources headlessly via the existing
  `TestSettingsPageContext` seam (Models with an empty registry — whatever rows
  it does build must be described).
- Skip sources with `PrefersAutoPagination == false` (today: Quick Actions) with
  a comment explaining why: hand-chunked pages are height-gated, so they carry
  no row descriptions yet.
- Fail naming the first row whose `SliderSpec`/`ToggleSpec`/`ComboBoxSpec`/
  `ColorPickerSpec` has null/whitespace `Description`.
- The list of source types is hardcoded — a new page source must be added to
  the test consciously, the same forcing function as
  `EyeCtrlSettingsEnumerationTests`.

## Files touched

- Nine page sources under `src/EyeCtrl.App/Views/Settings/Sources/` —
  description initializers only, no delegate or range changes.
- One new test file.
- `CursorPageSource.cs` unchanged.

## Verification

- `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` (baseline today:
  608/608 green).
- Row growth is absorbed by auto-pagination on every touched tab
  (`SettingsAutoPaginationTests`, `SettingsTabPaginationTests` keep passing);
  contrast is already gated by `SettingsRowDescriptionContrastTests` (brush
  unchanged); Quick Actions and its `HandChunkedPageFitTests` are untouched.
- GitNexus `impact` on each edited `Build()` (expected LOW — page sources are
  UI leaves consumed only by `SettingsWindow`), and `detect_changes` before
  committing.

## Observations — no action in this pass

Listed so they are on record; none block or belong to this content pass:

- `d_fix` slider offers 50–500 ms while `OnFixationDetectionMsChanging` throws
  below 100 — dragging to 50 or 75 looks like it would throw. Worth a look
  separately.
- `t_size` slider (32–120) is narrower than the validated range (40–200); the
  XML doc on `ToolbarSettings.Size` says 40–200.
- `DwellSettings` XML doc ranges (e.g. FixationDetectionMs "100–1000") disagree
  with several slider spans; the UI is the narrower of the two in each case.
