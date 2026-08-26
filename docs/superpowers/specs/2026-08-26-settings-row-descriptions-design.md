# Settings Row Descriptions — Design

Date: 2026-08-26 · Status: implemented on `feat/settings-descriptions` (rev 2, approved; plan-reviewed) · Branch: `feat/settings-descriptions`

Rev 2: amended after external review. All 3 blockers, 8 should-fixes and the
actionable nits were verified against source and adopted. The slider-minimum
fix is a review-adopted **scope addition** — flagged below for sign-off.

## Context

EyeCtrl is a WPF gaze-control app. Its settings window renders each row as a
label plus an optional second-line description (`SettingsRowSpec.Description`,
drawn at subtitle size in `TextSecondary`, contrast-gated per theme by
`SettingsRowDescriptionContrastTests`). The infrastructure shipped 2026-08-24
in `a21c4077` ("rows can carry a one-line description"); the 2026-08-20 plan
described the Cursor tab's five rows and explicitly left the remaining tabs as
"a content pass the user should drive". All eleven tab-header descriptions
already exist (`SettingsWindow.xaml.cs` `_tabDefs`).

This spec is that content pass: description copy for every describable row on
the nine remaining auto-paginated tabs, plus a completeness test so future
settings rows cannot ship bare, plus two slider-minimum fixes the review
uncovered (see Scope addition).

## Goal / Non-goals

**Goal.** Every slider, toggle, combo box and colour picker row on the ten
auto-paginated tabs (Cursor + nine) carries a short, truthful, user-facing
description.

**Non-goals.** No infrastructure changes; no Quick Actions rows (that tab is
hand-chunked and height-gated by `HandChunkedPageFitTests` — descriptions would
grow rows out of the tested budget); no descriptions on `ButtonSpec`/`LabelSpec`
(they render no description line); no touching the ~30 settings that have no
live UI row; no localization; no existing-copy changes beyond the two slider
minima (the Layout tab header's own "overlays" overclaim is noted under
Observations but not changed here).

## Copy rules

Generalizing the Cursor house style:

- One sentence, roughly 4–12 words. Gaze reading is slow; wrapping is
  tolerated (see Wrap behaviour) but brevity keeps lines to one where possible.
- Plain language: no enum names, property names, or internals. Say what the
  user sees happening.
- Directional guidance where it helps a decision ("bigger keys are easier to
  hit"), not just naming the field.
- Don't repeat slider ranges — the slider already shows them. Labels that
  already carry units ("(ms)", "(px)") are not re-explained.
- Truth rule: every line below is grounded in the Core XML doc comments and the
  code paths cited in the inventory, and was re-verified in external review.
  Nothing invented.
- **Spelling: British prose.** The existing description voice uses British
  spellings — Cursor's "colour", the Layout tab header's "screen centre" —
  while control labels stay American ("Circle Color"). This pass follows the
  prose voice (colour, centre) and changes no existing copy.
  *(Amendment to the originally presented design, which proposed normalizing
  Cursor's "colour" → "color"; review confirmed the prose voice is British —
  two independent instances, zero counter-examples — so nothing is
  normalized.)*

## Inventory — the copy

45 new descriptions (row count verified mechanically against the page sources:
9+7+10+1+6+1+3+4+4). Cursor's five existing descriptions are unchanged.

### Dwell Times (`DwellPageSource.cs`, grounding: `DwellSettings.cs`)

| Id | Label | Description |
|---|---|---|
| d_fix | Fixation Detection (ms) | How long your gaze must hold steady before a dwell can start. |
| d_act | Activation Button (ms) | Dwell for the button that appears at your gaze. |
| d_menu | Action Menu (ms) | How long to rest on an action in the ring. |
| d_ManualSelection | ManualSelection Action Menu (ms) | Dwell for the action ring inside manual selection. |
| d_tool | Toolbar / Settings (ms) | How long to rest on EyeCtrl's own buttons. |
| d_key | Keyboard Key (ms) | How long to rest on a key before it fires. |
| d_pred | Prediction (ms) | Dwell for the word suggestions above the keyboard. |
| d_rad | Fixation Radius (px) | How far your gaze may drift within a fixation. |
| d_cooldown | Dwell Cooldown (ms) | How long a new dwell must wait after an activation. |

Grounding notes: d_act — on fixation `GazeEngineAdapter` sends
`ShowActivationButtonMessage(point)`; the shown button dwells with
`ActivationButtonDwellMs` (`ActivationButtonViewModel.cs:38`). d_tool — the
value also drives the Quick Actions tiles (`QuickActionsGridViewModel.cs:114`)
and the task switcher (`TaskSwitcherViewModel.cs:122`), hence "EyeCtrl's own
buttons" rather than naming two of four consumers. d_key — fires on special
keys too (`PlayKeyClick()` precedes the special-action branch,
`KeyboardWindow.xaml.cs:270`), hence "fires" not "types".

### Selection (`SelectionPageSource.cs`, grounding: `ManualSelectionSettings.cs`, `AutoSelectionSettings.cs`, `EyeCtrlSettings.cs`)

| Id | Label | Description |
|---|---|---|
| s_mode | Selection Mode | None clicks your gaze point; Manual zooms; Auto snaps. |
| s_rad | Circle Radius (px) | Radius of the magnifier's zoom circle. |
| s_mag | Magnification Level | How strongly the zoom circle magnifies. |
| s_grace | Grace Period (ms) | Dwell waits this long after manual selection opens. |
| s_highlight | Highlight Color | Colour of the box around an auto-selected control. |
| s_highlightThickness | Highlight Thickness | Line weight of that auto-selection box. |
| s_snapRadius | Snap Search Radius (px) | How far from your gaze Auto Selection searches for a control. |

Grounding notes: `SelectionMode.None` is the shipped default
(`EyeCtrlSettings.cs:32`) and gets named first. The highlight draws only when a
control was resolved (`ResolvedControl != null`,
`OverlayMessageCoordinator.cs:105`), which requires Auto Selection — the copy
says so, because in the other modes these two rows change nothing visible.

### Action Menu (`ActionMenuPageSource.cs`, grounding: `InteractionAction`, `KeyboardInteractionDispatcher.cs`, `DragTrailViewModel.cs`, `ScrollSettings.cs`, `NoteManager.cs`)

One toggle per action; each description says what picking that action does.
The rows are loop-generated, so the descriptions attach via a
`Description(action)` switch beside `FriendlyName` — exhaustive over the enum,
**no silent `_ =>` null fallback** (a new action must fail the completeness
test, not ship bare).

| Id | Label | Description |
|---|---|---|
| am_LeftClick | Left Click | One left click at the point you chose. |
| am_RightClick | Right Click | One right click at the point you chose. |
| am_DoubleClick | Double Click | Two quick clicks at the point you chose. |
| am_Drag | Drag | Grabs at that point, then follows your gaze. |
| am_ScrollUp | Scroll Up | Scrolls up from the point you chose. |
| am_ScrollDown | Scroll Down | Scrolls down from the point you chose. |
| am_ScrollLeft | Scroll Left | Scrolls left from the point you chose. |
| am_ScrollRight | Scroll Right | Scrolls right from the point you chose. |
| am_ScrollMode | Scroll Mode | Continuous scrolling, steered by where you look. |
| am_PinNote | Pin Note | Opens a note to type at the point you chose. |

Grounding notes: am_Drag — the drop is a dwell on the release button
(`DragTrailViewModel.cs:132–146`), so the copy stops at "follows your gaze"
rather than describing the drop wrong. am_PinNote — `CreateNoteAt` opens an
empty note in `NoteState.Edit` with a focused editor
(`KeyboardInteractionDispatcher.cs:183–186`, `NoteManager.cs:41`): you type
after it opens. Scroll directions verified by sign at
`Win32InputSimulator.cs:68–84`.

### Layout (`LayoutPageSource.cs`, grounding: `EyeCtrlSettings.ViewPosition`)

| Id | Label | Description |
|---|---|---|
| lay_pos | Position | The action menu and magnifier: at your gaze, or centred. |

Grounding note: `ViewPosition` is read in exactly two places — the action menu
(`GazeEngineAdapter.cs:129`) and manual selection (`:171`). The keyboard,
toolbar, settings window, track status and notes never consult it, so the copy
must not say "overlays" (and must not restate the tab header, which does —
see Observations).

### Keyboard (`KeyboardPageSource.cs`, grounding: `KeyboardSettings.cs`)

| Id | Label | Description |
|---|---|---|
| k_layout | Active Layout | Key arrangement; QWERTZ suits German, AZERTY suits French. |
| k_size | Key Size Multiplier | Scales every key; bigger keys are easier to hit. |
| k_pred | Word Prediction | Shows suggested words above the keyboard while you type. |
| k_lang | Dictionary Language | Which language the word suggestions draw from. |
| k_sound | Click Sound | Plays a click each time a key activates. |
| k_edge | Screen Edge | The screen edge the keyboard docks to. |

Grounding note: k_layout's German/French guidance is code-grounded —
qwertz.json carries ß/ä/ö/ü, azerty.json à/ç/è/é/ù.

### Models (`ModelsPageSource.cs`, grounding: `ModelManagementService.SetActiveModel`, `PredictionStackFactory.cs:57,86`)

| Id | Label | Description |
|---|---|---|
| md_active | Active Model | Which model powers predictions; Off keeps dictionary words. |

Grounding note: "Off" → `ActiveModelId = null` → the LM predictor is left null
but the dictionary `wordPredictor` still builds, so predictions fall back to
dictionary suggestions.

### Toolbar (`ToolbarPageSource.cs`, grounding: `ToolbarSettings.cs`, `ToolbarLifecycleCoordinator.cs:163`)

| Id | Label | Description |
|---|---|---|
| t_dock | Dock to Screen Edge | Docks the toolbar to an edge, where it auto-hides. |
| t_edge | Docked Edge | Which screen edge the toolbar attaches to. |
| t_size | Button Size | Size of each button; bigger ones are easier to aim at. |

Grounding note: `IsDocked` also gates auto-hide
(`enabled = providerIsRealTracker && s.IsDocked`) — undocked means floating and
permanently visible; docked means edge-attached and auto-hiding.

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
| g_theme | Theme (restart to apply) | All three are dark; Contrast is the boldest. |
| g_boot | Start on Windows Boot | Opens EyeCtrl automatically when you sign in. |
| g_pause | Start Paused | Starts with tracking paused until you switch it on. |
| g_track | Show Track Status | A corner box showing live eye-tracker status. |

Grounding notes: g_theme — the overlay spec rules out a light theme outright
(`ThemeSettings.cs` doc, §2), so "all three are dark" is grounded, and the
label already carries the restart note. g_boot — registration is an HKCU
`...\Run` value (`WindowsStartupRegistrar.cs`), i.e. at logon, hence "when you
sign in".

## Scope addition — slider-minimum fixes (review-adopted, needs sign-off)

The review found that two Dwell sliders offer values **below** the Core
setter's validated floor, and that reaching them crashes the app:

- `d_fix` slider min 50 vs `OnFixationDetectionMsChanging` throwing below 100.
- `d_cooldown` slider min 0 vs `OnDwellCooldownMsChanging` throwing below 100.

`DwellSlider.Adjust` clamps to the spec's min, then calls the setter with no
try/catch (`DwellSlider.cs:371–372`), and `App.xaml.cs` deliberately does not
set `Handled` on dispatcher exceptions so the supervisor's respawn owns
recovery — so two down-steps on either slider crash EyeCtrl. For a gaze-only
user, a crash seizes the only input channel.

**Fix in this pass** (the file is already open for descriptions):
`d_fix` min 50 → 100, `d_cooldown` min 0 → 100. Both land on-step (25 and 50).
Maxima already sit inside the validated ranges.

**New test:** a range-vs-guard test asserting every `SliderSpec` span on the
Dwell page lies within the corresponding `OnXxxChanging` guard, so a future
slider edit cannot reintroduce the crash.

*Reviewer offered the alternative of a tracked defect instead; including the
two-token fix here was chosen because the file is already touched and the
failure mode is severe. Strike this section if you'd rather keep the content
pass pure and track it separately.*

## Completeness test

New `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs`:

- Build each page source headlessly via the existing `TestSettingsPageContext`
  seam, holding sources as `ISettingsPageSource` (the skip property is a
  default interface member).
- **Skip by explicit type** — `source is QuickActionsPageSource` — not by
  `PrefersAutoPagination == false`: that property defaults to false in the
  interface (`ISettingsPageSource.cs:14`) and QuickActions is exempt only by
  *not overriding* it, so the property test would silently excuse any future
  source that forgets to declare it.
- **Models must be built with a downloaded model**: an empty registry builds
  no describable row at all (`md_active` is gated on `downloaded.Count == 0`,
  `ModelsPageSource.cs:95–107`), so the empty build covers nothing. Use
  `ModelTestFakes.Create()` + `WithDownloadedBundled()` (documented exactly for
  driving the active-model combo branch, `ModelTestFakes.cs:71`) in a
  `using var scope`. A real registry must still be passed for the other
  sources — `TestSettingsPageContext.Build` null-forgives it.
- Fail naming the first row whose `SliderSpec`/`ToggleSpec`/`ComboBoxSpec`/
  `ColorPickerSpec` has null/whitespace `Description`.
- The list of source types is hardcoded — a new page source must be added to
  the test consciously, the same forcing function as
  `EyeCtrlSettingsEnumerationTests`.

## Files touched

- Nine page sources under `src/EyeCtrl.App/Views/Settings/Sources/` —
  description initializers only, plus the two slider minima in
  `DwellPageSource.cs`.
- One new test file (completeness) and one new range-vs-guard test.
- `CursorPageSource.cs` unchanged.

## Verification

- `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` (baseline today:
  608/608 green, run 2026-08-26).
- **Wrap behaviour**: the external spec review measured roughly 20 of the 45
  lines wrapping to two lines at the ~380 DIP name column (FormattedText at the
  subtitle type ramp) — a one-off, unrecorded measurement, so treat the number
  as approximate. Bounded impact either way: rows are 150 DIP
  `LabelledRowMinHeight` boxes and pagination re-chunks by measured height, so
  the worst case is one extra page, never a clip; one existing Cursor line
  already wraps. Contrast is already gated by
  `SettingsRowDescriptionContrastTests` (brush unchanged); Quick Actions and
  its `HandChunkedPageFitTests` are untouched.
- GitNexus `impact` on each edited `Build()` (expected LOW — page sources are
  UI leaves consumed only by `SettingsWindow`), and `detect_changes` before
  committing.

## Observations — no action in this pass

Corrected in rev 2 (the original bullet 3, "the UI is the narrower of the two
in each case", was false — four sliders sit below or outside their guards):

- **Fixed in this pass** (see Scope addition): `d_fix` and `d_cooldown` slider
  minima below the setter floor — crash path.
- `t_size` slider (32–120) vs validated 40–200: values 32/36 make
  `SettingsService.Save` throw; `SaveDebounced` catches and logs it, the size
  applies live and is silently lost on restart. No crash; left alone here.
- `s_grace`: the XML doc says 100–2000 but `ManualSelectionSettings.Validate`
  never checks `ManualSelectionGracePeriodMs` — an unenforced doc, harmless.
- Several `DwellSettings` XML doc ranges disagree with slider spans (e.g.
  FixationDetectionMs "100–1000" vs slider 50–500 → after the fix, 100–500).
- The Layout tab header itself says "Whether overlays open at your gaze or at
  the screen centre" — the same overclaim the row description had (`ViewPosition`
  affects only the action menu and manual selection). Existing shipped copy;
  changing it is a one-liner worth considering separately.
