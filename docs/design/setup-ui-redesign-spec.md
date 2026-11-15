# Setup UI Redesign — Proposal r3

**Status:** proposal r4 — **direction B picked (owner, 2026-11-15, D8/D9 modifications applied)**;
external review r3 (verdict "needs fixes": 1 High + 8 Medium/Low findings + 6 wrong-claim survivors)
processed in full; every claim verified against `main` @ 44dcc6af. No code changed. Mockup r4 rendered
and lint-checked (16 screens incl. the cancelled B6, DE/EN).

**Artifacts:** this spec + `setup-ui-redesign-mockup.html` (same folder; opens from `file://`, all inline,
DE/EN switch under the tabs — both languages carry the real string-table copy).
**Owner feedback folded in:** real program icon; raised type scale; English page text.

The mockup renders all three directions at **true size** (CSS px = DIP on the owner's 3840×2400 @ 200 %
display): progress/result windows 1344×806 DIP, choice windows 1564×806 DIP. The dwell demonstration on
each choice screen shows the real external-target dwell visual (r1 F1): a left→right accent fill sweep
(`DwellFeedbackOverlay`), accent-10 % groundwork, 2 DIP accent border, cursor ring hidden — shown at ~20 %.

## 1. Scope

The three screens `EyeCtrl.Setup` draws (all in `src/EyeCtrl.Setup/Display/`):

| Screen | Window | Verbs |
|---|---|---|
| Progress / result | `SetupProgressWindow` | update, rollback, uninstall |
| Uninstall choice, screen 1 | `UninstallChoiceWindow` | uninstall |
| Uninstall choice, screen 2 (confirm delete) | `UninstallChoiceWindow` | uninstall |

`repair` and `status` have no screen (`WpfSetupDisplayFactory`, `WpfSetupDisplay.cs:239-244`); repair and
revert phases run inside the update/rollback windows (`SetupWorkflow.cs:97-223`). Display-layer redesign
only — verbs, state machine, pipes/SDK, elevation and package verification untouched (§2 FC8).

## 2. Fixed contracts (no direction may change these)

Code references are the current `main` (44dcc6af).

- **FC1 Arming.** Screen 1 and screen 2 arm 1 s after appearance/re-registration
  (`UninstallChoiceWindow.ArmingDelay`, `UninstallChoiceWindow.cs:45`). A pre-arm Invoke restarts the
  resting-gaze gate `2 × dwell + cooldown + 500 ms blink + 300 ms margin` (≈ 2.8 s at defaults;
  `SetupDwellTiming.RestingGate`, `SetupUi.cs:227-247`), so a gaze resting where the window appeared never
  fires (fixup-06 M3(b), fixup-07 N1).
- **FC2 Cancel semantics.** Esc, the 5-minute no-choice timeout, and any close that is not an answer cancel
  with a recorded path (`UninstallCancelPath`); an unanswerable window resolves to cancel
  (`UninstallChoiceWindow.cs:498-552`, `WpfUninstallChoice.Ask`).
- **FC3 Confirm-screen placement.** On screen 2 the destructive answer is far left (grid column 0) and the
  safe answer far right (column 4) — under the gaze resting where "Delete" was
  (`UninstallChoiceWindow.BuildScreen(confirm: true)`, `UninstallChoiceWindow.cs:352-366`). Screen 1 keeps
  Keep/Cancel/Delete at columns 0/2/4. **Intent guard (r1 F6):** screen 2 must not anchor prose, icons or
  state marks over the danger column — screen 2 prose stays centred, danger markers centred or full-width
  (SD9). r2 verified the mockup geometry exact in all three directions (button x = 32/572/1112 and
  32/1112, rows at equal y).
- **FC4 Progress window is display-only.** `Focusable=false`, `IsHitTestVisible=false`, ordinary window
  (Q13, `SetupProgressWindow.cs:12-19`). It shows no dwell targets, ever.
- **FC5 Self-close and failure reading.** Success closes after 10 s, every failure — including `Degraded`
  (Q12) — after 60 s. An early close only ends the display (`WpfSetupDisplay.Finish`).
- **FC6 Dwell targets and the real dwell visual.** Choice buttons are SDK external targets
  (`Dwell.SetMode(…, DwellMode.Invoke)`, `UninstallChoiceWindow.cs:280`). External targets get the App's
  `DwellFeedbackOverlay` (`ExternalDwellService.cs:314-334`): a separate topmost click-through window with a
  2 DIP accent border on an accent-10 % background whose opaque accent `ProgressFill` grows left→right
  (`Overlays/DwellFeedbackOverlay.xaml:7-12`), cursor ring hidden (`ExternalDwellService.cs:307-312`).
  Only the screen in front is enabled (fixup-05); hiding drops targets, activation re-arms (fixup-06).
- **FC7 Security of result text — extended by D8 (r3 F1).** `VerifyFailed` renders from the *installed*
  table, never the update package's own table (`SetupUi.TableFor`); theme, language and dwell timing come
  from `settings.json` with clamped, tolerating fallbacks (`SetupUi.Load/ParseSettings`). **After D8 this
  includes the band title:** the progress window is built with the package's preferred table
  (`WpfSetupDisplay.cs:51`), so on every result the band title is re-resolved from the result's table
  (`SetupDisplayText.ProgressTitle(TableFor(result), verb)`) — otherwise a tampered table could leave
  e.g. "Update installiert" in the band directly above the trusted refusal. §7 adds the tampered-table
  test.
- **FC8 Workflow and security routing unchanged.** Nothing touches `SetupWorkflow`, the state machine,
  elevation, the catalog, or the App runtime. Layout is code-behind (no XAML). The files the redesign
  DOES touch are listed in §6 (Core schema/validator and App *test* fit measurers included).
- **FC9 Accessibility floors** (`SetupWindowMetrics.cs`): targets 420×160 DIP, gap 120 DIP, button text
  ≥ 28 DIP, prose ≥ 36 DIP, prose box 1280 DIP. Stage text is **setupProse** (`StringTableSchema.cs:108-112`)
  and keeps the 36 DIP floor. **Fit box note (r2 F8):** the shipped prose boxes are narrower than 1280
  (A ≈ 1152, C ≈ 1088 after the state bar) — the fit harness must measure `setupProse` at the **narrowest
  shipped box**, or `ProseWidth` changes with the implementation.

## 3. Shared redesign decisions (SD)

- **SD1 Identity mark.** The real program icon (`src/EyeCtrl.App/Assets/eyectrl.ico`, embedded as Setup
  resource, `EyeCtrl.Setup.csproj:37`) at 60 DIP beside the title (A), in the band (B). C has no icons.
  Decoded via `BitmapDecoder` with explicit frame selection — frame 0 is 16×16 and would blur at
  60 DIP @ 200 % (r1 F11); the 256-px frame is used; null-tolerant.
- **SD2 Prose alignment.** Prose left-aligned on progress, result and choice screen 1 (fixed line start
  re-found cheaply after saccades). **Exempt: screen 2 and its danger markers (r1 F6)** — they stay
  centred / full-width (SD9) so nothing anchors over the danger column.
- **SD3 Type scale** (today = `SetupWindowMetrics` values):

  | Element | Today (DIP) | Proposed (DIP) | Floor |
  |---|---|---|---|
  | Button text | 28 (= floor) | **32** | 28 |
  | Prose / detail lines | 36 (= floor) | **40** | 36 |
  | Dim hint lines (`noChoice`, …) | 36 | **36** (stays at floor) | 36 |
  | Stage text | 36 (= floor, setupProse) | **36** (unchanged) | 36 |
  | Titles | 36 bold | A 52 · B band 44 · C 56 (result/confirm 48) | — |

- **SD4 State system — three states, fully specified (r1 F5, r2 F6).** Success = bare check in
  `AccentSuccess`; failure/degraded (Q12) = alert in `AccentDanger`; **cancelled = neutral** — a dim
  horizontal dash glyph (A), a `TextDim` 6 DIP edge (B), a `TextDim` bar (C) — **never** the success check.
  Only `Cancelled` maps to neutral; `HandedOff` never reaches a display (`SetupWorkflow.cs:623-627`, it
  returns before the recorder/display path). The neutral glyphs are non-round (r1 F14). Requires
  `SetupDisplayResult` to carry a state enum — `SetupDisplayText` is therefore touched (§6). Titles stay
  `TextPrimary` in all states; state colour rides on the icon/edge/bar with the contrast floor moved there
  (D7). Result screens carry only real strings (D5). **B's neutral body glyph** = the dim dash at 64 DIP
  (mockup B6; r3 F5). **Band demotion on result screens (r3 F2, D8 mechanics amendment):** the band title
  renders `TextSecondary`/SemiBold — cancelled/blocked/notApplied otherwise contradict the band's process
  claim, and a 44-bold band competes with the outcome for the first fixation.
- **SD5 Stage visualization, scoped by verb (r1 F9, r2 wording fix, amended by owner decision D9 and
  r3 F3).**
  The window shows the verb's **full ordered plan** upfront (update/rollback: Verify → Stop → Swap → Start
  with verb-specific Swap/Start strings, `SetupDisplayText.cs:44-55`) — pending stages are declared plan,
  not emissions; emissions drive the done/active transitions. **The list is data-driven (owner decision
  D9, seam amended r3 F3): the plan is a display-side per-verb table** — `ISetupDisplay` has no plan
  channel and FC8 forbids workflow changes, so "no per-verb hard-coding" as written was not realizable
  (r3 F3). Update/Rollback plan = [Verify, Stop, Swap, Start], Uninstall plan = [Stop] (one row today;
  B5's single row covers the whole elevated job — stopping and deleting run in one pass, r3 F6). **Any
  emitted stage not in the plan is appended** and marked active; plan rows complete only on their own
  emission. "Grows automatically" therefore means: a new `SetupStage` member + string +
  `SetupDisplayText.Stage` case slots into the table — not zero-touch. The "plain status line when no
  stages" branch is dropped (dead code — every windowed verb has stages). During in-update repair/revert
  phases no new stages arrive; the open window holds its current state and never fabricates steps (r2
  wrong-claim fix: `repair` has no window, and revert runs inside the rollback window,
  `SetupWorkflow.cs:97-223`). Each direction renders the list in its own idiom (A/B: step rows, C:
  sentence) — all 36 DIP (FC9).
- **SD6 Zero new strings** for the core redesign. Only D2 (arming feedback) would add one.
- **SD7 Confirm-screen framing, theme-derived and opaque (r1 F12).** A = alert icon centred above the
  title; B = danger-tinted band; C = full-width danger hairline above the button row. The B tint is
  blended at runtime from **opaque** colours (`SurfaceMedium` carries 0xFA alpha; N1 pattern,
  `SetupProgressWindow.cs:186-192`), ratio pinned per theme (~18 % in Midnight ≈ #50334E);
  icon-vs-tint contrast ≥ 3:1 per theme.
- **SD8 Button anatomy — the contrast gate binds EVERY kind (r2 F1–F4).** Buttons carry **no icons** (the
  sweep paints over content; heads/bands keep theirs) and **centred** content (r1 F7). The sweep-vs-fill
  gate **≥ 3:1 per theme applies to primary, quiet and danger alike** — r2 measured accent-on-danger at
  2.06–2.49:1 across all four themes, so **no button is filled with a light colour**:
  - **Primary** = `SurfaceLight` fill + **bold label** (`FontWeightStrong`) + `BorderSubtle` border at
    `BorderThicknessStandard`. No accent border — the accent frame belongs to the overlay; a resting
    accent border would duplicate the dwell-start look (r2 F4) and primary-vs-quiet fill contrast is only
    ≈ 1.27:1 (Midnight) / 1.05:1 (Daylight), so the bold label carries the distinction.
  - **Quiet** = `SurfaceMedium` fill + `BorderSubtle` border at `BorderThicknessStandard` — which is
    **1 DIP in Midnight/Daylight/Ember and 2 DIP in Contrast** (`ContrastTheme.cs:104`; r2 wrong-claim fix
    on "1 DIP") — and a **`FontWeights.Normal` label (400)**: Bold-vs-SemiBold plus a 1.27:1 (Midnight) /
    1.05:1 (Daylight) fill difference was not enough to mark the primary at a glance (r3 F10); the
    per-theme pass confirms the primary/quiet distinction.
  - **Danger** = `SurfaceMedium` fill + `AccentDanger` border + `AccentDanger` text (r2 F1): the accent
    sweep over that groundwork is ≈ 8.5:1 (Midnight), the danger label ≈ 4.7:1 (Midnight) / 4.9:1
    (Daylight). This also stops the danger button from being the loudest element on screen 2 (r2 F3).
  - **Hover styling is a prerequisite (r2 F2, High).** Setup defines no button template today, so the
    Aero2 hover replaces fills (#BEE6FD) while the gaze-followed pointer rests on the target
    (`GazeEngine.cs:958-969`, FreeLook) and the click-through overlay draws the sweep on top — sweep-vs-
    hover ≈ 1.06:1, `TextPrimary` label ≈ 1.0:1 (unreadable). Already true today; the redesign must ship
    an **explicit `ControlTemplate` (Border + ContentPresenter, no state triggers)** for all choice
    buttons, with the §7 template test and an owner gaze check.
  - **Known limitation (recorded):** the App overlay paints above the target's content, so centred light
    text is progressively covered during the sweep regardless of fill choice — today's quiet buttons share
    it; a durable fix belongs to the App/SDK (out of scope, FC8).
  - **Opaque + frozen band/edge brushes (r3 F11):** `SurfaceMedium` carries 0xFA alpha — B's band
    background, its danger tint (SD7) and the state edge are computed from the opaque variants and frozen
    (same pattern as `OpaqueSurfaceDark`, `SetupProgressWindow.cs:186-192`); the contrast test's
    background pin covers them.
- **SD9 Screen-2 marker placement.** Danger markers centred (A: alert above centred title; B: centred band
  content) or full-width (C: danger hairline), never left-anchored.
- **SD10 Theme handling.** The mockup renders Midnight only; all colours derive from tokens at runtime.
  Per-theme pass at implementation: SD7 tint, SD8 gate for every kind, state-icon vs surface, and C's
  stage-state separability (SD11).
- **SD11 C's stage states carry a non-colour cue (r2 F7).** Past (`TextDim`, regular) vs future
  (`TextSecondary`, *italic*) is a known 1.29:1 (Midnight) / 1.42:1 (Daylight) colour-only pair; theme
  tokens are out of scope (§9), so the pending stages are **italic** — a shape cue the per-theme pass
  cannot be asked to deliver.

## 4. The three directions (each 5 screens in the mockup: update progress, result, uninstall choice,
confirm, uninstall progress)

### Direction A — „Ablauf" (structured system dialog)
Left-aligned column, app icon + 52 DIP title. Progress: detail line + vertical stepper (bare check done,
filled accent diamond active, outline diamond pending). Result: state icon above the title. Choice:
icon-free centred buttons (SD8). Confirm: centred alert + title, centred prose (SD2/SD9). Uninstall
progress: one-row step list (A5, SD5/D9). Most information per glance; medium layout work.

### Direction B — „Kopfband“ (product dialog) — **picked (owner, 2026-11-15)**
Header band (min-height 132, auto-grow for wrapped titles; **full-bleed** — the window padding goes, and
the body compensates so the button row stays at H−32−48, r3 F7) with app icon + title; result screens add
a 6 DIP state-coloured edge, keep the process title in the band **demoted** (`TextSecondary`/SemiBold,
r3 F2 — D8), and carry the **confirming text in the body** under the state icon (44 DIP, free wrapping);
vertical stage list (36 DIP, no connectors); icon-free centred buttons on the window surface (footband
removed r1 F8). Confirm: danger-tinted band, content centred (SD9). Uninstall progress: one-row step
list (B5, SD5/D9 — the single row covers the whole elevated job). Cancelled: neutral edge + dim dash +
`uninstall.cancelled` in the body (B6, SD4).

### Direction C — „Stille Typo" (quiet typographic)
No icons, no bands. 56 DIP titles, hairline rules, stage sentence with SD11's italic cue for pending
stages, state bar beside result titles; confirm screen centred with the full-width danger hairline.
Buttons: Keep primary (bold label), others outline-only, centred text. Smallest patch; target boundary
relies on the outline and the r2-flagged weak separabilities are covered by SD10/SD11.

## 5. Decision ledger (owner-vetoable)

| # | Decision | Rationale | Status |
|---|---|---|---|
| D1 | Pick a direction: A / B / C | r1 Q8 + r2 update: **A strongest overall** (36-DIP stepper, centred buttons, centred confirm); B = "A plus a band", highest layout risk, but **B4's tinted band is the best danger framing**; C lowest risk, weakest for gaze (stage states, outline targets, r2 F6's 110-DIP off-centre — fixed in r3 by centring). r2 reviewer's recommendation: **A's layout with B4's tinted band**, decided only after SD8's shared button anatomy (r2 F1–F4, applied in r3) | **B picked (owner, 2026-11-15)** — with D8/D9 modifications; A/C kept as mockup candidates |
| D2 | Arming feedback: dim targets until armed? | r1 F13 side effects (resting gaze keeps dim state; contrast halves; luminance step); reviewer-leaning answer: skip | **ruling: skip (owner, 2026-11-16)** — all four review rounds recommended skip; nothing to implement |
| D3 | Icons inside choice buttons | Removed (r1 F1/F2) — the sweep covers them; heads/bands keep theirs | applied |
| D4 | Prose left-aligned (SD2); button content centred (r1 F7); screen 2 fully centred (r1 F6) | Fixed scan anchor; in-target margin preserved; no gaze pull toward danger | open (owner veto) |
| D5 | Result screens carry **only** real strings | Every on-screen word traces to `strings.*.json` | **ruling** |
| D6 | Program icon as identity mark (owner request 2026-11-15) | `eyectrl.ico` 256-px frame, 60/52 DIP | applied |
| D7 | Failure title stays `TextPrimary`; colour + contrast floor move to the state icon (r1 F3) | Maximum title readability | **ruling** |
| D8 | The result/confirmation text lives in the **body** under the state icon; the band keeps the process title (owner, 2026-11-15) | Long result text (~150 chars) in a 44-bold band was also the r2 reviewer's concern; the body wraps freely and the band stays a stable identity/process anchor | **applied** — r3 F2 amendment: band title demoted on result screens (`TextSecondary`/SemiBold), owner-vetoable |
| D9 | The step list is **data-driven**: the uninstall window shows a list whenever stages exist (today: one, Stop) — the plan is a display-side per-verb table (owner, 2026-11-15; seam amended r3 F3) | Future uninstall stages slot into the table (new `SetupStage` member + string + `Stage` case); the "plain status line" branch is dead code and dropped | **applied** — r3 F3 amendment recorded |

## 6. Implementation sketch (only after D1)

- `SetupWindowMetrics.cs`: add icon sizes, state-icon size; keep every existing floor; decide the prose
  fit box per FC9's note (narrowest shipped box vs changing `ProseWidth`).
- **Choice buttons get an explicit `ControlTemplate` (Border + ContentPresenter, no hover/pressed
  triggers)** — prerequisite, not polish: without it the Aero2 hover replaces fills under the gaze
  (r2 F2, see SD8). Route: **`FrameworkElementFactory`** in code — keeps FC8's no-XAML rule
  (`XamlReader.Parse` would embed XAML strings; r3 F9). The template binds `Padding`,
  `HorizontalContentAlignment`, `Background`, `BorderBrush`, `BorderThickness` (so `MeasureButton`'s
  padding-based room maths stays truthful), sets `CornerRadius = SmallCornerRadius` (matching the
  overlay so the sweep doesn't overhang the corners) and `FocusVisualStyle = null`.
- `SetupDisplayText.cs`: `SetupDisplayResult` gains a `State` enum (Success/Failure/Neutral) as the
  **single source of truth — `IsFailure` is removed** (r3 T5: a defaulted enum next to `IsFailure` gives
  two sources of truth; a default of Success would render a check on a `VerifyFailed` screen).
  `CloseDelayFor` derives from `State`; the contrast test's positional construction is retargeted (§7).
  Neutral = `Cancelled` only (`HandedOff` never reaches a display, `SetupWorkflow.cs:623-627`).
- `SetupProgressWindow.cs`: title/state-icon composition, verb-aware ordered-plan seam (SD5), long-title
  wrap handling.
- `UninstallChoiceWindow.cs`: button/prose composition per direction; column placement, arming, gate,
  cancel paths byte-identical (FC1–FC3); the **full-bleed band compensates the removed window padding in
  the body** so the button row stays at H−32−48 and the bottom pin (`UninstallChoiceWindowTests:583`)
  holds unchanged (r3 F7).
- Icon: `BitmapDecoder` with explicit 256-px frame; frozen; null-tolerant.
- `CreateProse` is shared by both windows (`SetupProgressWindow.cs:89-91`) and hard-codes centre
  alignment today (`UninstallChoiceWindow.cs:288-305`) — it gets **alignment + size parameters** (B
  left-aligns screens 1/progress and centres screen 2, r3 F8); fit classes measure the **shipped boxes**:
  prose 40 at ≈1388 (choice) / 1168 (progress), result text 44 bold at the derived `TitleFitWidth`
  1094 (progress band title: 1344 − padding 176 − mark 52 − gap 22; shipped in implementation,
  2026-11-15), stage 36.
- Fit chain (r1 F10): new fit classes touch `strings.json` `fit` values (Core), `StringTableSchema.cs:108-112`,
  `tools/strings_validate.py:129-132` and `AppFitMeasurers.cs:51` (App.Tests) — "no App changes" holds
  only for App **runtime** code. `update.result.degraded` (~150 chars DE) and
  `uninstall.done.deleteIncomplete` (with path) must be measured at shipped size in the narrowest box.

## 7. Test impact

- `UninstallChoiceWindowTests` casts `(TextBlock)window.KeepDataButton.Content` / `ConfirmDeleteButton`
  (`:134-137`) — the redesign keeps TextBlock content or the test gets a text-extraction helper; the
  column assertions in the same test are the FC3 safety net and must keep passing unchanged.
- `SetupFitTests.MeasureButton` subtracts `2 × BorderThicknessStandard` (`:130-132`) — with SD8 the
  primary border is `BorderThicknessStandard` again, so the existing maths holds. The fit asserts are
  floors only (`≥ 28`/`≥ 36`, `:117-118`) — no font-size expectation to change (r3 T5).
- `SetupProgressWindowContrastTests` pins the tree shape `Border → StackPanel → Children[0]/[1]`
  (`:53-60`) and the failure title to `AccentDanger` (`:77`) — SD4/D7 deliberately breaks the second and
  every direction breaks the first; the tree pin becomes a per-direction structure test. The positional
  `new SetupDisplayResult("failure", IsFailure: true)` (`:75`) was **retargeted** during implementation
  (record note 2026-11-15): §6's own ruling removed `IsFailure`, so the construction became
  `new SetupDisplayResult("failure", SetupDisplayState.Failure)` (interim in Task 3, final per-direction
  rewrite in Task 5).
- `SetupProgressWindowTests` read `TitleText` (`:122`) and `StageText` (`:40`, `:60-66`, `:124`) — under
  D8 `:122` retargets to a new **`ResultText` accessor** (the body element; the title stays the process
  title, r3 F4), and the stage UI needs an active-stage accessor or these tests move to the new seam.
  `:123`/`:124` (detail empty, stage hidden) keep holding.
- FC7 band test (r3 F1): tampered preferred table + `VerifyFailed` → **no preferred-table text anywhere
  in the visual tree** (band title re-resolved from the trusted table).
- Bottom pin: `UninstallChoiceWindowTests:583` (`keep.Bottom ≈ H − 32 − 48`) holds unchanged — the
  full-bleed band's body compensates the removed window padding (r3 F7).
- New tests: verb-aware ordered-plan seam (data-driven lists per D9 — uninstall plans one stage today;
  repair/revert = no fabricated steps),
  state mapping incl. neutral cancel, B tint derivation per theme, **sweep-vs-fill contrast ≥ 3:1 per
  theme for every button kind**, **button template carries no hover reaction** (shipped as
  `Template.Triggers.Count == 0` — the only way a `ControlTemplate` can react to `IsMouseOver` —
  plus named border/presenter reference checks; implementation note 2026-11-15).
- `NoVisibleTestWindow` guard unchanged — none of these tests shows a window.

## 8. Verification plan

1. Per FC1–FC9: one check each against the implemented screens (fit harness + logic tests).
2. Mockup r4 render-checked at 1920 px (16 screens incl. B6, both languages); lint clean; DE page text
   matches the spec numbers.
3. Owner's live gaze check after implementation — explicitly including the hover-free template (SD8).
4. Per-theme pass (SD10): tint, sweep-vs-fill gate for every button kind, state-icon contrast, C's
   stage-state separability with the SD11 cue, and the primary-vs-quiet distinction (r3 F10: the fill
   difference is 1.27:1 Midnight / 1.05:1 Daylight — the Normal-vs-Bold label weights must carry it).

## 9. Out of scope

Workflow/state machine, elevation, catalog/package verification, pipes/SDK, settings schema, App runtime
code, App UI, new screens, topmost/click-through behaviour, theme token definitions, the App-side sweep
limitation on target content (SD8 note).
