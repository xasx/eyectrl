# Adaptation preview & Reset visibility — design

- **Date:** 2026-10-13
- **Status:** approved design (brainstorm session 2026-10-12/13, mockup approved in visual-companion session `2010-1791841801`)
- **Branch:** `feat/adaptation-preview` (base `52b031ba`)
- **Input:** owner request — "Show adaptation parameters to the user. Hide Reset button on toolbar if adaptation is disabled."

## 1. Problem

The struggle-adaptation system changes the user's effective dwell/tolerance/smoothing/geometry settings live, but the user never sees what it is doing:

- The **cue badge** flashes struggle intensity only — never what was adapted.
- The **JSONL log** is machine-oriented and deliberately records band fills, not absolute effective values (spec §10 amendment: effective values are consumer-relative — dwell composes against ~20 surface baselines).
- **Settings → Adaptation** shows only the *configuration* (posture, band fractions, toggles) — nothing live.

Meanwhile the toolbar's **Reset button** (the §8.6 always-reachable escape, the one action the band itself calls unrecoverable) is built unconditionally, so it sits on the strip even when adaptation is disabled — where resetting momentary state is a no-op (disabled ⇒ effective == baseline) and only the inert learned layer would be cleared, which Settings already reaches via "Reset learned offsets".

## 2. Approved decisions

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Show **live adaptation state**, not configuration | Configuration is already fully exposed in Settings → Adaptation; the gap is "what is it doing to me right now" |
| D2 | Surface = **preview flyout anchored to the Reset button** (rest-on-Reset) | Gaze-native; directly answers "what would Reset undo?"; no new toolbar dwellables (placement ruling honored) |
| D3 | Content = **layout A: bars + values** (approved mockup) | One row per knob: direction word, thin bar (cyan momentary / slate learned), right-aligned value; absolutes only where a single baseline exists |
| D4 | Trigger protection = **seen-latch freeze** | Default `ToolbarButtonDwellMs` is **700 ms** (range 200–2000): at a 40 % trigger only ~420 ms remain, so an unfrozen preview would fire Reset about half the time while the user reads. Freeze delivers the confirm step the repo already plans for Reset (`ToolbarBand.AddSeparator` comment) |
| D5 | Hide rule applies to **toolbar band AND tray item**; Settings buttons remain | One consistent story; learned-offset reset stays reachable without re-enabling |
| D6 | Suppression: no panel **and no freeze** while `IFeedbackGate.IsFeedbackSuppressed`; **not** gated on `ShowCue` | Sensitive contexts keep byte-for-byte today's behavior; resting on a button is deliberate intent (tooltip-like), not ambient feedback |

Approved tunable defaults: trigger threshold **0.40** of the Reset dwell; content refresh throttle **≥ 500 ms** on `EffectiveSettingsProvider.Changed`; the panel **does** show at baseline (four flat rows + muted "nothing active" line) so resting on Reset always explains the button.

## 3. Feature 1 — adaptation preview flyout

### 3.1 Behavior

**State machine (per engagement with the Reset button):**

1. Gaze rests on Reset; dwell progress climbs normally.
2. At progress ≥ **0.40**, while un-armed, un-suppressed, and adaptation enabled: the target **freezes** (progress holds at 0.40), the panel appears anchored to the button. Reading can never fire Reset.
3. Gaze leaves the Reset hit area: the dwell **abandons** (progress to 0, no fire), the panel hides, the button becomes **armed**.
4. Gaze returns and rests: the dwell runs a **full unfrozen pass** and fires normally (the panel may show again, live, but never freezes). This is the deliberate leave-and-return contract of a destructive button.
5. The armed latch clears when Reset fires, the toolbar deactivates (auto-hide), or the band rebuilds (settings change, axis flip, enable/disable) — so the next engagement previews fresh.

The panel carries **no dwell targets** and no hit-testing: a dismissal-only surface that cannot trap gaze, and its presence never blocks the Reset dwell beneath it.

**Placement:** anchored to the Reset button, offset **into the screen away from the docked edge** (below a top-docked strip, above a bottom-docked one, mirrored for vertical strips), with the connector tick from the approved mockup; clamped to the work area. Reset is the last button behind its separator, so the panel never covers sibling buttons.

**Content (four rows + footer, per approved mockup A):**

| Knob | Direction word | Bar | Value |
|------|----------------|-----|-------|
| Dwell | shorter | momentary + learned | band % only (e.g. "−25 % band") — multi-baseline, no honest single ms number |
| Tolerance | looser | momentary + learned | absolute: "58 px ← 45 px" (baseline `DwellSettings.FixationRadiusPixels`) |
| Smoothing | more | momentary + learned | absolute: "3.5 Hz ← 5.0" (baseline from `CursorSettings` per active filter mode; "—" when Direct) |
| Geometry | bigger | momentary + learned | band % only — per-target runtime baselines |

Footer: "Reset clears all momentary *and* learned offsets." At baseline: four flat rows + muted "nothing active right now" line.

**Update cadence:** snapshot on show; refreshed on `EffectiveSettingsProvider.Changed` while visible, throttled to ≥ 500 ms (no 4 Hz peripheral flicker).

### 3.2 Components

1. **Per-target dwell freeze (Core, `DwellDetector`).** `FreezeTarget(id)` / `UnfreezeTarget(id)`: a frozen target's dwell neither advances nor abandons while gaze stays on it; gaze leaving abandons cleanly; `ResetAllState` clears freezes with everything else; freezing an unknown id is a no-op. The only Core change to the dwell machinery. (Rejected: skipping the detector feed in the VM — would freeze every toolbar button; effect-gating with a completed arc — filled arc + flash with nothing happening.)
2. **Seen-latch (ViewModels, `ToolbarViewModel`).** Mirrors the existing `_pauseReArmPending` leave-before-re-arm pattern on this surface: at ≥ 0.40 it freezes the target and publishes a preview-visibility **message** on the app `WeakReferenceMessenger` (the repo's VM→App pattern, e.g. `ResetAdaptationMessage`); on gaze leaving the Reset hit area (`DwellDetector.HitTest`, the pause-toggle precedent) it abandons, unfreezes, arms, and publishes hidden; fire / deactivate / rebuild clear the latch.
3. **Preview window + service (App).** New `AdaptationPreviewWindow : OverlayWindow` (the `CueOverlayWindow` precedent), registered in the overlay-inventory drift test and docs spec §31. A small `AdaptationPreviewService` (shaped like `AdaptationCueService`) listens to the VM's preview events, positions/creates no targets, builds content from the snapshot, throttled-refreshes on `provider.Changed`. All show/hide/refresh work is crash-contained (log-and-skip) — a comfort feature must never take down the session.
4. **Content snapshot (Core, pure).** `AdaptationPreviewSnapshot` builder over the provider's public reads (`TryGetMomentaryFill`, `EnumerateLearnedFills`, `Effective`) plus public baselines (`DwellSettings.FixationRadiusPixels`; `CursorSettings.FilterMode` → `OneEuroMinCutoff` / `SmoothingFactor`). The displayed smoothing value must equal what `GazeEngine` actually applies (its per-filter clamp) — the clamp is shared or oracle-pinned, never duplicated blindly; exact mechanism is a plan-level decision.

### 3.3 Data flow

gaze → `ToolbarViewModel` detector feed → progress ≥ 0.40 → freeze + preview-visibility message → `AdaptationPreviewService` → `AdaptationPreviewWindow` (content from the snapshot builder over provider + settings); `provider.Changed` → throttled refresh; gaze leaves Reset → VM abandons + unfreezes + arms → hidden message.

## 4. Feature 2 — hide Reset when adaptation is disabled

- **Band:** `ToolbarBand.Build` gains `includeReset` (separator + `ResetGap` included only with the button); the flag is read from `AdaptationSettings.Enabled` at every build; the strip shortens when absent.
- **Rebuild trigger:** a dedicated branch in `SettingsUiCoordinator.OnSettingsChanged` comparing the before/after `Enabled` value calls the same `Toolbar.Initialize` path as the snapshot branch — deliberately NOT added to `ToolbarSnapshot`, whose contract is "toolbar fields only".
- **Dwell targets:** after any band rebuild, targets for no-longer-rendered ids are removed (the VM diffs its `_positionedButtons` set); an in-progress dwell on Reset at disable-time is abandoned, never fires.
- **Tray:** the "Reset adaptation" item's `Visible` is refreshed in the context menu's `Opening` event from the same setting.
- **Deliberate asymmetry:** the VM's static button list and its `ResetAdaptation` switch case remain (the bind loop already resolves from the band at event time); `ToolbarButtonDriftTests` is updated to encode "band membership conditional on `Adaptation.Enabled`, VM list static" as the rule.
- **Settings untouched:** "Reset to defaults" and "Reset learned offsets" stay on the Adaptation page — learned offsets survive a disable inertly and remain clearable without re-enabling.
- **Consequence (accepted):** with adaptation off the flyout is unreachable too — consistent, since effective settings equal baseline and there is nothing momentary to preview.

## 5. Edge cases & error handling

- **Disable mid-dwell / mid-preview:** band rebuild removes the target, abandons the dwell, closes the panel — one consequence of the rebuild path.
- **Re-enable:** button returns with the band; latch cleared; next rest previews fresh.
- **Toolbar auto-hide / axis flip / settings open:** any band rebuild or toolbar deactivation hides the preview and clears the latch.
- **After Reset fires:** existing cooldown unchanged; latch cleared, so a later rest previews again.
- **Paused:** the toolbar is deliberately dwellable while paused (owner's paused-launch testing pattern) — freeze/latch/preview run identically; no special-casing.
- **Sensitive contexts (D6):** no panel and no freeze — identical behavior to today.
- **Placement:** clamped to the work area; anchoring mirrors for vertical strips.
- **Containment:** preview operations are crash-contained (log-and-skip, the cue's discipline); freeze of an unknown id no-ops.

## 6. Testing

- **Core:** `DwellDetector` freeze semantics (on-target holds, gaze-leave abandons, unfreeze resumes, `ResetAllState` clears — clock-seeding discipline per the gaze differential-test conventions); `AdaptationPreviewSnapshot` rows (fills, learned-only, all-at-baseline, tolerance px, smoothing per filter mode incl. Direct "—", oracle test pinning the displayed smoothing value to `GazeEngine`'s applied value).
- **ViewModels** (existing `Adaptation` test folder): latch state machine — trigger at 0.40, freeze once, leave → arm + unfreeze + abandon, second dwell completes unfrozen, fire/hide/rebuild clear, suppressed and disabled paths do nothing.
- **App:** band layout with/without Reset (extent shrinks, no separator, `Contains(ResetId)` false); drift-guard update; tray `Opening` visibility; `SettingsUiCoordinator` Enabled-flip triggers re-initialize; stale-target removal via `_positionedButtons` diff; new window in the overlay inventory + docs spec §31.
- **Localization:** new `strings.json` keys in English + German (l10n workflow); row strings follow the existing fit-test pattern where measured.

## 7. Out of scope

- A live status panel inside Settings → Adaptation (rejected this round — D2 chose the flyout).
- Absolute effective values for the multi-baseline knobs (dwell ~20 surfaces, geometry per-target) — a single number would be silently wrong for most surfaces.
- Gating the preview on `ShowCue` (D6).
- Any confirm-step mechanics for buttons other than Reset.
