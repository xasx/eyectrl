# Adaptation preview & Reset visibility — design

- **Date:** 2026-10-13 (rev 2, same day — external review round 1 processed; all findings accepted)
- **Status:** approved design, revised per review (see §10 ledger)
- **Branch:** `feat/adaptation-preview` (rebased onto `origin/main` `408e5734`; includes Daylight #202 + l10n T11/T12)
- **Input:** owner request — "Show adaptation parameters to the user. Hide Reset button on toolbar if adaptation is disabled."

## 1. Problem

The struggle-adaptation system changes the user's effective dwell/tolerance/smoothing/geometry settings live, but the user never sees what it is doing:

- The **cue badge** flashes struggle intensity only — never what was adapted.
- The **JSONL log** is machine-oriented and deliberately records band fills, not absolute effective values (spec §10 amendment: effective values are consumer-relative — dwell composes against ~20 surface baselines).
- **Settings → Adaptation** shows only the *configuration* (posture, band fractions, toggles) — nothing live.

Meanwhile the toolbar's **Reset button** (the §8.6 always-reachable escape, the one action the band itself calls unrecoverable) is built unconditionally, so it sits on the strip even when adaptation is disabled — where resetting momentary state is a no-op (disabled ⇒ effective == baseline) and only the inert learned layer would be cleared, which Settings already reaches via "Reset learned offsets".

## 2. Decisions ledger

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Show **live adaptation state**, not configuration | Configuration is already fully exposed in Settings → Adaptation; the gap is "what is it doing to me right now" |
| D2 | Surface = **preview flyout anchored to the Reset button** (rest-on-Reset) | Gaze-native; directly answers "what would Reset undo?"; no new toolbar dwellables (placement ruling honored) |
| D3 | Content = **layout A: bars + values** (approved mockup, visual-companion session `2010-1791841801`) | One row per knob: direction word, bar, right-aligned value; absolutes only where a single baseline exists |
| D4 | Trigger protection = **seen-latch freeze** | Default `ToolbarButtonDwellMs` is **700 ms** (range 200–2000): at a 0.40 trigger only ~420 ms remain, so an unfrozen preview would fire Reset about half the time while the user reads |
| D5 | Hide rule applies to **toolbar band AND tray item**; Settings buttons remain | One consistent story; learned-offset reset stays reachable without re-enabling |
| D6 | Suppression: no panel **and no freeze start** while feedback is suppressed; **not** gated on `ShowCue` | Sensitive contexts keep byte-for-byte today's behavior; resting on a button is deliberate intent (tooltip-like), not ambient feedback |
| D7 (r2) | **Engaged region = button ∪ panel** while the preview is visible; arm only after a **debounced** continuous leave of the union | Rev-1 tied both lifetimes to the Reset hit area alone, so *reading the panel* hid it and armed the button — the natural glance back then fired Reset (review C1/H1) |
| D8 (r2) | **No panel on the armed pass** — the arc restarting from 0 and running is the armed signal | A panel re-appearing at 0.40 of the armed pass is bait that pulls gaze off the escape dwell (review H2) |
| D9 (r2) | A preview engagement emits **zero struggle telemetry** | Frozen-then-left at 0.40 ≥ `TelemetryThresholds.AbandonProgress` (0.3) would score abandonment (+ retry on return), feeding the very knob being previewed (review H3) |
| D10 (r2) | Freeze mechanics = the **existing `DwellDetector.Update` targetFilter** + a VM-held arc; **no Core freeze API** | Rev-1's `FreezeTarget` rejected its own straw man: the per-target filter (the pause-overlap fix) freezes only Reset, suppresses its abandon telemetry (D9 for free), and needs no new freeze semantics (review M1) |
| D11 (r2) | The band-rebuild seam into the VM is a new **`SetRenderedButtons`** call | The VM has no rebuild notification today; `_positionedButtons` only grows, so naive target removal dead-buttons Reset forever after re-enable (review M3) |
| D12 (r2) | Struggle-spec **§8.2/§8.6 amendment** ships with this feature | The two-step toolbar escape supersedes the "instant escape" promise; it must be amended and bounded, not slipped in (review H2) |

Approved tunable defaults: trigger threshold **0.40** of the Reset dwell; leave debounce **120 ms** continuous off-union (the `PauseReArmLeaveUs` discipline); content refresh throttle **≥ 500 ms** on `EffectiveSettingsProvider.Changed`; the panel **does** show at baseline (four flat rows + muted "nothing active" line).

## 3. Feature 1 — adaptation preview flyout

### 3.1 Interaction state machine

**Engaged region.** While the preview is visible, the engaged region is the **union** of the Reset target's live hit area (`DwellDetector.HitTest` — geometry-grown extents honored, up to `GeometryMaxMarginPx`; an overlap with the panel's near edge is therefore harmless) and the **panel rect**. Gaze may wander freely between button and panel — reading — with the freeze held and the panel up.

1. Gaze rests on Reset; dwell progress climbs normally.
2. At progress ≥ **0.40**, while un-armed, un-suppressed, and adaptation enabled: the freeze engages (§3.2) and the panel appears anchored to the button. Reading can never fire Reset.
3. Gaze leaves the **union**: after a **continuous off-union run ≥ 120 ms** (single stray samples and 1–3-sample excursions do not count; the Pause re-arm discipline), the button becomes **armed**, the panel hides, the displayed arc drops to 0.
4. Gaze returns and rests: the dwell runs a **full unfrozen pass from 0** and fires normally. **No panel on the armed pass** (D8): the arc restarting and climbing is the signal that this pass will fire. An armed-pass dwell abandoned midway keeps today's behavior (a real abandoned dwell on a real button).
5. The armed latch clears when Reset fires, the toolbar deactivates (auto-hide), or the band reports Reset absent (§4 seam) — the next engagement previews fresh.

The panel carries **no dwell targets** and no hit-testing: a dismissal-only surface that cannot trap gaze.

**Residual race, accepted:** the freeze engages on the first progress event ≥ 0.40; one sample that spans from below 0.40 to completion (possible only at the 200 ms dwell floor with a ≥ 80 ms tracker gap landing inside the first 80 ms) fires Reset with no preview. Worst case equals today's behavior — the preview is additive safety, never a regression.

### 3.2 Freeze mechanics (D10)

- **Engage:** on the ≥ 0.40 progress event, `ToolbarViewModel` starts passing a `targetFilter` that excludes `ResetId` to `DwellDetector.Update` (existing 3-arg overload). An ineligible target can neither be nearest nor accumulate — the excluded target **cannot fire**.
- **Display:** the filtered pass clears detector state and raises **ungated `TargetProgress(Reset, 0)`**; while frozen the VM's `OnTargetProgress` override suppresses propagation for `ResetId` and holds the button VM's `DwellProgress` at the freeze value (0.40) so the arc reads as held.
- **Telemetry:** the abandon-outcome sink call is already filter-gated in the detector — an excluded target's abandon is not reported. One preview engagement therefore emits **zero** `DwellOutcome`s (D9).
- **Arm (debounced leave):** the VM stops filtering, sets `DwellProgress(Reset) = 0`, and marks armed. The detector state is already clean (the filtered pass cleared it), so the armed pass enters fresh — **no per-target reset API, no RemoveTarget/re-add** (which would drop geometry-grown extents).
- **Interactions moot by construction:** cooldown passes, gap re-anchor, and mid-freeze `SetEffectiveDwellTime` denominator changes all operate on a target that receives no samples while filtered; `ResetAllState` clears everything as today, and the deactivation path hides the panel and clears the latch.

### 3.3 Placement, anchoring, auto-hide

- Anchored to the Reset button, offset **into the screen away from the docked edge** (mirrored for vertical strips), connector tick per approved mockup, clamped to the work area. Reset is the last button behind its separator, so the panel never covers sibling buttons.
- **Auto-hide must not take the strip away mid-read:** gaze on the panel is off the toolbar rect, so without action the toolbar would hide, deactivate dwell, clear the latch, and re-freeze on the next rest. While the preview is visible the service suspends toolbar auto-hide via the existing `SetAutoHideSuspended` pattern — **guarded so the preview's resume cannot clobber the settings window's suspension** (only resume what we suspended; plan picks ref-count or ownership token).
- **Z-order / other windows:** the preview is a topmost overlay like its siblings; opening Settings, the keyboard, or any window naturally moves gaze off the union, and the debounced leave hides the panel. No special "settings open" trigger exists (rev-1 listed one without a mechanism — dropped, review G4).

### 3.4 Content & composition (per approved mockup A)

Four rows — Dwell, Tolerance, Smoothing, Geometry — plus footer "Reset clears all momentary *and* learned offsets." At baseline: four flat rows + muted "nothing active" line.

**Composition is the clamped, net quantity routing consumes (review G1):** learned and momentary forgiving fills are summed *then* band-clamped (`EffectiveSettingsProvider.Effective`), so bars are not drawn as two stacked independent segments:

- **Dwell** — bidirectional bar (shorter left / longer right; the knob has both a forgiving and the quarantined tightening fill, driven by accidental activation). Direction word = **net** ("shorter" / "longer" / "—"); each side shows that direction's clamped composed fill; the learned portion is shaded slate within the clamped total (learned is forgiving-only, so the slate shade appears on the shorter side only).
- **Tolerance / Smoothing / Geometry** — single-direction bar: total = `clamp(learned + momentary, band)`, learned shaded within.
- Values: **Tolerance** absolute px vs baseline `DwellSettings.FixationRadiusPixels`; **Smoothing** absolute vs baseline with unit **per filter mode** — Hz (One-Euro), factor (EMA), "—" (Direct) (review G2); **Dwell / Geometry** band % only (multi-baseline).

**Smoothing single source of truth (G2):** `GazeEngine.EffectiveSmoothingParameter`'s per-mode composition+clamp is extracted into a static Core helper that both the engine and the snapshot builder call — the duplicated range literals stop existing; the oracle test pins engine ≡ helper.

### 3.5 Suppression & layering (D6, review M2)

`IFeedbackGate` lives in `EyeCtrl.App.Services` and is unreachable from `EyeCtrl.ViewModels`: the VM receives a **`Func<bool>? isFeedbackSuppressed`** constructor delegate (App wires the gate; `null` = never suppressed, the test default). While suppressed: no panel and no freeze **engagement**. If suppression begins **mid-freeze** (password field gains focus while resting), the panel hides immediately but the freeze persists until the debounced leave — suppression must never convert a hidden preview into a sudden fire. Not gated on `ShowCue`.

### 3.6 Components & data flow

1. **ViewModels, `ToolbarViewModel`** — owns the state machine: filter engagement, held arc (`OnTargetProgress` override), 120 ms off-union debounce, armed latch, suppression check via the injected delegate, and a per-id cache of last-reported screen centers (today only a `HashSet` exists).
2. **Messages (repo `WeakReferenceMessenger` pattern)** — VM → service: `AdaptationPreviewVisibilityMessage(bool Visible, anchor center, docked edge)`; service → VM: `AdaptationPreviewBoundsMessage(Rect? PanelRect)` so the VM's union test covers the panel. The service **creates no dwell targets**; it only places and fills the window (rev-1's "positions/creates no targets" garble — review G3).
3. **App, `AdaptationPreviewService`** (shaped like `AdaptationCueService`) — anchors the window inward from the docked edge, builds content from the snapshot, throttled refresh (≥ 500 ms) on `provider.Changed`, suspends/resumes auto-hide around visibility (guarded, §3.3), crash-contained (log-and-skip): a comfort feature must never take down the session.
4. **App, `AdaptationPreviewWindow : OverlayWindow`** — the `CueOverlayWindow` precedent; registered in the overlay-inventory drift test and docs spec §31; themed via existing Tokens only (correct under Daylight automatically; no new palette entries).
5. **Core, pure `AdaptationPreviewSnapshot` builder** — over the provider's public reads (`TryGetMomentaryFill`, `EnumerateLearnedFills`, `Effective`) plus public baselines; emits per-knob rows per §3.4; calls the extracted smoothing helper.

## 4. Feature 2 — hide Reset when adaptation is disabled

- **Band:** `ToolbarBand.Build` gains `includeReset` (separator + `ResetGap` included only with the button); the flag comes from `AdaptationSettings.Enabled` at every build; the strip shortens when absent.
- **Flag delivery:** `ToolbarWindow.Initialize(double buttonSize, int dwellTimeMs, bool includeReset)` — callers are `OverlayManager` (startup) and the re-init block below (review M5).
- **Rebuild trigger (review M4):** `SettingsUiCoordinator` subscribes `AdaptationSettings.PropertyChanged(Enabled)` (the `AdaptationActivation` precedent) so **every** writer triggers the rebuild, not only a Settings-window save. The handler runs the **whole re-init block** the toolbar-snapshot branch runs — `Initialize` + `SetMode` + `UpdateEnabled` + `UpdateEdge` + `PositionIndicator` + `RefreshToolbarRect` — factored into one method both paths call (`Initialize` alone rebuilds the band but leaves the strip off-centre with a stale auto-hide rect). Deliberately NOT added to `ToolbarSnapshot` (toolbar-fields-only contract).
- **VM seam (D11, review M3):** `ToolbarWindow` calls `ToolbarViewModel.SetRenderedButtons(ids)` after every band build. The seam removes the detector target **and the `_positionedButtons` entry** for ids no longer rendered (a leftover entry would make the first post-re-enable position report take the `UpdateTargetCenter` branch, which no-ops on a missing target — Reset undwellable forever), zeroes the removed ids' button-VM `DwellProgress` and highlight (`RemoveTarget` fires no `TargetProgress(0)`, and `BindButton` re-applies VM state on rebuild — a frozen 0.40 arc would otherwise survive), and clears the preview latch (publishing hidden if the panel was up). Target creation stays lazy via `UpdateButtonScreenPosition`.
- **Tray:** the "Reset adaptation" item's `Visible` is refreshed in the context menu's `Opening` event from the same setting.
- **Deliberate asymmetry:** the VM's static button list and its `ResetAdaptation` switch case remain (the bind loop resolves from the band at event time); `ToolbarButtonDriftTests` encodes "band membership conditional on `Adaptation.Enabled`, VM list static".
- **Settings untouched:** "Reset to defaults" and "Reset learned offsets" stay on the Adaptation page; learned offsets survive a disable inertly and remain clearable without re-enabling.
- **Consequence (accepted):** with adaptation off the flyout is unreachable too — consistent, since effective settings equal baseline and there is nothing momentary to preview.

## 5. Edge cases & error handling

- **Disable mid-dwell / mid-preview:** band rebuild removes the target, abandons the dwell (never fires), the seam clears the latch and VM state, the panel closes — one consequence of the rebuild path.
- **Re-enable:** button returns with the band; `SetRenderedButtons` re-registers nothing (creation stays lazy); latch cleared; next rest previews fresh. Pinned by a disable → enable → Reset-fires test with the arc starting at 0.
- **Auto-hide / axis flip / settings open:** any band rebuild or toolbar deactivation hides the preview and clears the latch; the debounced union-leave covers every gaze-away path including Settings and the keyboard.
- **Paused:** the toolbar is deliberately dwellable while paused (owner's paused-launch testing pattern) — freeze/latch/preview run identically; no special-casing.
- **Suppression:** §3.5 — no engagement while suppressed; mid-freeze suppression hides the panel but never unfreezes into a fire.
- **After Reset fires:** existing cooldown unchanged; latch cleared; a later engagement previews fresh.
- **Containment:** preview operations crash-contained (log-and-skip); the filter, latch and seam logic are pure VM state, headless-testable.

## 6. Testing

- **Core:** `AdaptationPreviewSnapshot` rows — fills, learned-only, all-at-baseline, tolerance px, smoothing per filter mode incl. Direct "—", **bidirectional dwell** (tightening-only "longer", mixed, learned+momentary summing past the band → clamped display), and the **oracle test** pinning the extracted smoothing helper ≡ the engine's applied value.
- **ViewModels** (existing `Adaptation` test folder): the state machine — engagement at 0.40; **panel → button → panel stays, no fire**; several seconds reading → no hide, no fire, **zero `DwellOutcome`s** (D9); 1-sample and 3-sample off-union excursions do not arm; the debounced leave arms and drops the arc to 0; the armed pass runs unfrozen with **no panel event**; fire/deactivate/`SetRenderedButtons`-absent clear the latch; suppressed → no engagement; mid-freeze suppression → panel hides, freeze persists; the **worst-case escape bound** (§7) pinned as a timing test.
- **App:** band layout with/without Reset (extent shrinks, no separator, `Contains(ResetId)` false); drift-guard update; tray `Opening` visibility; `SettingsUiCoordinator` `PropertyChanged(Enabled)` triggers the full re-init block; `SetRenderedButtons` removes stale targets **and set entries** and zeroes stuck arcs; **disable → enable → Reset fires, arc from 0**; new window in the overlay inventory + docs spec §31; **existing Reset-assuming tests** (~20 references across 8 files: `ToolbarButtonVisualTests` ×9, `ToolbarTargetPlacementTests`, `ToolbarPausedTargetAlignmentTests`, `ToolbarOrientationTests`, `GeometryInvarianceTests`, `ToolbarActivationFlashTests`, `AppLaunchTests`) gain a fixture helper enabling adaptation before construction, plus new without-Reset cases (review M5).
- **Localization & theming:** new surface prefix `overlays.adaptationPreview` in `strings.json` **and** `strings.de.json`, with the surface added to `strings.de.manifest.json` (the per-surface switch-on; missing-de degrades to English, but German ships with the feature); palette untouched (existing Tokens only — Daylight-correct by construction).

## 7. Governing-spec amendment (D12, review H2)

The struggle-adaptation design (`2026-07-19-struggle-detection-self-adaptation-design.md`) promises an **"instant escape"** (§"always offer an instant escape back to baseline", "bounded and **instantly escapable**"). The seen-latch makes the **toolbar** escape two-step. This feature therefore amends §8.2/§8.6 of that document:

- The toolbar Reset becomes: freeze-preview pass → debounced leave → one full armed dwell. **Worst case to reset** = `0.40 × ToolbarButtonDwellMs` + 120 ms + `1 × ToolbarButtonDwellMs` (defaults ≈ 1.1 s; 200 ms floor ≈ 0.4 s; 2000 ms ceiling ≈ 2.9 s; the dwell term may itself be shortened by live adaptation).
- The **tray** Reset item remains a single-step instant escape (mouse path), and Settings' "Reset learned offsets" is unchanged — the promise holds for attendants and Settings; the toolbar path trades instant for cannot-fire-while-reading, which is the repo's own planned confirm step for the one unrecoverable button.

## 8. Out of scope

- A live status panel inside Settings → Adaptation (rejected this round — D2).
- Absolute effective values for the multi-baseline knobs (dwell ~20 surfaces, geometry per-target).
- Gating the preview on `ShowCue` (D6).
- A dedicated armed-pass arc hue beyond the moving arc (noted follow-up polish).
- Confirm-step mechanics for any button other than Reset.

## 9. Residual known gaps

- The §3.1 residual single-sample race at the 200 ms dwell floor (fires with no preview; equals today's behavior).
- Auto-hide suspension ownership (ref-count vs token) — plan-level.

## 10. Review round 1 ledger (2026-10-13, all accepted)

| Finding | Resolution | Where |
|---|---|---|
| C1 reading = leaving; glance-back fires | Engaged region = button ∪ panel; debounced arm on union leave; auto-hide suspended while visible | D7, §3.1, §3.3 |
| H1 no leave debounce | 120 ms continuous off-union run (`PauseReArmLeaveUs` discipline); excursion tests | D7, §3.1, §6 |
| H2 armed-pass bait panel + §8.6 contradiction | No panel on armed pass; struggle-spec §8.2/§8.6 amended with worst-case bound; bound pinned in test | D8, D12, §7, §6 |
| H3 preview feeds struggle telemetry | Filter-gated abandon (excluded target reports nothing); zero-outcome test | D9, D10, §3.2, §6 |
| M1 straw man + under-specified Core freeze | Adopted the existing `Update` targetFilter + VM-held arc; no Core freeze API; interactions moot by construction | D10, §3.2 |
| M2 `IFeedbackGate` is App-layer | `Func<bool>` injected into the VM | §3.5 |
| M3 dead button / stuck arc / no seam | `SetRenderedButtons` removes targets + set entries + VM arc/highlight; disable→enable test | D11, §4, §6 |
| M4 half-named re-init path | Full block factored; `PropertyChanged(Enabled)` subscription covers all writers | §4 |
| M5 default band has no Reset; 20 test refs | `Initialize` gains the flag; fixture helper enables adaptation; without-Reset cases added | §4, §6 |
| M6 base 34 behind origin/main | Rebased onto `408e5734`; Daylight via Tokens-only; l10n manifest switch-on specified | §3.6, §6 |
| G1 tightening + band clamp ignored | Net bidirectional dwell bar; clamped composed totals; snapshot test rows | §3.4, §6 |
| G2 smoothing units + duplicated clamp | Static Core helper extracted, engine and snapshot both call it; oracle test | §3.4, §6 |
| G3 anchor data path + garble + grown-target overlap | Rect messages both ways; union test = `HitTest` ∪ panel rect; sentence rewritten | §3.6, §3.1 |
| G4 "settings open" trigger had no mechanism | Dropped; debounced union-leave covers it | §3.3 |
