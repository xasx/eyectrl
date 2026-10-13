# Adaptation preview & Reset visibility — design

- **Date:** 2026-10-13 (rev 4 — external review round 3 processed: FIX-THEN-SHIP, all findings accepted)
- **Status:** approved design, revised per reviews (§10 ledger covers all three rounds)
- **Branch:** `feat/adaptation-preview` (on `origin/main` `408e5734`)
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
| D1 | Show **live adaptation state**, not configuration | Configuration is already fully exposed in Settings → Adaptation |
| D2 | Surface = **preview flyout anchored to the Reset button** (rest-on-Reset) | Gaze-native; answers "what would Reset undo?"; no new toolbar dwellables |
| D3 | Content = **layout A: bars + values** (approved mockup `2010-1791841801`) | One row per knob: direction word, bar, right-aligned value; absolutes only where single-baseline |
| D4 | Trigger protection = **seen-latch freeze** | Default `ToolbarButtonDwellMs` = **700 ms** (200–2000): an unfrozen preview fires Reset mid-read |
| D5 | Hide rule applies to **toolbar band AND tray item**; Settings buttons remain | Learned-offset reset stays reachable without re-enabling |
| D6 (r3) | Suppression hides the **panel only**; the freeze, latch, and debounce run **everywhere** as a silent confirm | r2 coupled a safety mechanism to feedback suppression and its "never a sudden fire" promise was self-contradictory: suppression mid-read shrank the union to the button, armed it in 120 ms, and the glance back fired. The freeze is a confirm step, not feedback — accidental dwells don't stop at sensitive-context boundaries, and a hidden panel leaks nothing (review R2-M2) |
| D7 | **Engaged region = button ∪ panel** while the preview is up; arm only after a **debounced** continuous leave of the union | r1 tied lifetimes to the Reset hit area alone, so reading the panel armed the button (reviews C1/H1) |
| D8 | **No panel on the armed pass** — the arc restarting from 0 and running is the armed signal | A panel re-appearing at 0.40 of the armed pass is bait (review H2) |
| D9 | A preview engagement emits **zero struggle telemetry for Reset**, and in-union time feeds the detector **nothing** | Frozen-then-left at 0.40 ≥ `AbandonProgress` (0.3) would feed the very knob being previewed (review H3) |
| D10 (r3) | While gaze is **in the union, the VM feeds the detector nothing** (the pause-latch `return` precedent); during the off-union leave run it feeds with **Reset excluded** (targetFilter); armed ⇒ feed all | r2's filter-Reset-only freeze let a **neighbour** win the dwell: geometry-grown hit areas overlap (Modes↔Reset centres 168 DIP = 336 px; grown radii sum 360 px at defaults ⇒ 24 px overlap; 16 px at the owner's size 112; both knobs user-tunable higher), so Modes could accumulate and fire mid-read (review R2-H1) |
| D11 | The band-rebuild seam into the VM is **`SetRenderedButtons`** | The VM has no rebuild notification; `_positionedButtons` only grows (review M3) |
| D12 | Struggle-spec **§8.2/§8.6 amendment** — an explicit implementation deliverable (its own commit), not a spec side-note | The two-step toolbar escape supersedes the "instant escape" promise; must be amended and bounded (review H2; round-2 Low: r2 claimed it "ships with this feature" while touching only this spec) |
| D13 (r4) | The armed latch **expires 3 s** after arming (the `StruggleSignals` retry window), measured at the **start of the return**: a pass that begins armed runs to fire or abandon even if the 3 s elapses mid-pass | r2's latch cleared only on fire/deactivation/absence, but auto-hide runs 5 s after leaving the toolbar rect and is disabled outright for floating toolbars and the Mouse provider — a preview walked away from left Reset armed for the rest of the session, firing in one pass minutes later (review R2-H2). Expiry-at-the-end would drop the latch mid-escape-pass (return at 2.5 s + 2.0 s dwell ends at 4.5 s), re-freezing at 0.40 with the panel up — the D8 bait inside the escape (review R3-M1) |
| D14 (r4) | **Panel-bounds unknown ⇒ engaged, bounded by a 500 ms timeout** that falls back to union = Reset's hit area with the silent freeze held (and logs); a contained show failure publishes **`null` bounds** ("no panel") and the VM falls back the same way; while bounds are unknown and gaze is **outside** Reset's hit area, the VM feeds the detector with **Reset excluded** rather than nothing | The bounds arrive asynchronously after show; a cold first show (or a refresh resize) can outlast the 120 ms debounce — but the r3 rule had **no path for the message never arriving**: the service crash-contains a failed show (the VM never learns), and `AdaptationActivation`'s catch leaves `Enabled == true` with the runtime inert — so the VM could publish visible into the void and freeze the whole toolbar (Pause included, stranding a paused gaze-only user) forever on floating/Mouse where deactivation never comes (reviews R2-H3 + R3-H1). `EyeCtrl.ViewModels` is plain `net10.0`, so the union carries a Core physical-px rect |
| D15 (r3) | l10n: strings live under the **`overlays` surface, English-only**; German ships with **T13** (the paused overlays scope); row strings get a **new fit class** | A key's prefix must equal its surface (`StringTableSchema`), `overlays.adaptationPreview` belongs to the closed `overlays` surface, and the manifest does not list `overlays` — switching it on would switch on every overlay. A new surface id would mean editing `Surfaces`, the Python validator and the manifest for one panel (review R2-M1) |
| D16 (r4) | **Activation offers are suspended while the preview panel is visible** (`SetActivationOffersSuspended`, clearing any live offer — the Settings / task-switcher / quick-actions / eye-care-banner treatment, owner decision 2026-10-02) | The activation offer must never sit over EyeCtrl's own controls (decision 8), but r3's panel anchors beyond the strip's end — outside the toolbar chrome zone — so the offer engine would place activation buttons around the panel mid-read, and dwelling one opens the action menu where the user is reading (review R3-H2). Suspension fits better than adding the panel to the zone composition: reading the panel is the user's whole interaction at that moment |

Approved tunable defaults: trigger threshold **0.40**; leave debounce **120 ms** continuous off-union (`PauseReArmLeaveUs` discipline); latch expiry **3 s** (judged at the pass start); bounds-unknown fallback timeout **500 ms**; content refresh throttle **≥ 500 ms**; the panel **does** show at baseline (four flat rows + muted "nothing active" line).

## 3. Feature 1 — adaptation preview flyout

### 3.1 Interaction state machine

**Engaged region.** While the preview is up, the engaged region is the union of the Reset target's live hit area (`DwellDetector.HitTest` — geometry-grown extents honored) and the **last-published panel rect** (physical px). Gaze may wander freely between button and panel — reading — with the freeze held and the panel up. While the panel is visible but its bounds are **unknown** (first show, resize), the state is **engaged** regardless of gaze — the leave debounce does not run — but this is **bounded**: after **500 ms** the union falls back to Reset's hit area alone, the silent freeze still held, with a log; a **`null` bounds message** ("no panel" — contained show failure, service absent) triggers the same fallback immediately. During the unknown window, gaze **outside** Reset's hit area feeds the detector with **Reset excluded** (other buttons stay live — the panel anchors beyond the strip's end, so nothing else needs freezing) (D14).

1. Gaze rests on Reset; dwell progress climbs normally.
2. At progress ≥ **0.40**, while un-armed and adaptation enabled: the freeze engages (§3.2) and the panel appears anchored to the button (unless suppressed — §3.5, where the freeze engages silently). Reading can never fire Reset — and nothing else can fire either, because the detector is fed nothing while gaze is in the union.
3. Gaze leaves the **union**: a continuous off-union run ≥ **120 ms** (single stray samples and 1–3-sample excursions do not count) **arms** the button, hides the panel, and drops the displayed arc and highlight to 0. During the run the VM feeds the detector with Reset **excluded**, so other buttons behave normally and a transient re-entry onto the button neither accumulates nor emits an outcome. A sample back inside the union cancels the run and resumes the freeze.
4. Gaze returns **within 3 s** of arming and rests: the dwell runs a **full unfrozen pass from 0** and fires normally — the pass is judged armed at its **start** (gaze entering the hit area with the arm younger than 3 s) and runs to fire or abandon even if the 3 s elapses mid-pass (D13 r4). **No panel on the armed pass** (D8). An armed-pass dwell abandoned midway keeps today's behavior (a real abandoned dwell on a real button). A return **after 3 s** finds the latch expired: the next engagement previews again.
5. The latch also clears when Reset fires, the toolbar deactivates, or the band reports Reset absent (§4 seam). The 3 s expiry (D13) bounds every other path — including floating toolbars and the Mouse provider, where auto-hide never deactivates the strip.

The panel carries **no dwell targets** and no hit-testing: a dismissal-only surface that cannot trap gaze.

**Residual race, accepted (numbers per review round 2):** the freeze engages on the first progress event ≥ 0.40; one sample spanning from below 0.40·D to completion needs a gap ≥ **0.6·D**, and gaps ≥ **150 ms re-anchor** the dwell — so the race exists only for **D < 250 ms** with a gap in **[0.6·D, 150 ms)** (at D = 200 ms: 120–150 ms). Worst case equals today's behavior — the preview is additive safety, never a regression.

### 3.2 Freeze mechanics (D10)

- **Engage:** on the ≥ 0.40 progress event, `ToolbarViewModel` **stops feeding the detector entirely** while gaze is in the union (the pause-latch `return`-before-`Update` precedent). No samples ⇒ nothing accumulates, nothing fires, no outcomes are emitted for **any** target — the zero-telemetry guarantee (D9) holds by construction, not by suppression. The one bounded exception is the unknown-bounds window off the button (§3.1: feed with Reset excluded, 500 ms max).
- **Display:** the VM holds the button VM's `DwellProgress` at the freeze value **and `IsHighlighted = true`** (`OnTargetProgress` derives highlight from `progress > 0`; without holding it, the acquired face drops while the arc holds). While un-armed — engaged or in a leave run — the VM suppresses `ResetId` progress propagation.
- **Leave run:** the VM resumes feeding with a `targetFilter` excluding `ResetId` (existing 3-arg `Update` overload). Reset's stale `IsGazeInside` clears via the ungated `TargetProgress(0)` (suppressed from display); the abandon-outcome sink call is filter-gated, so **no outcome** is reported for Reset.
- **Arm:** the VM stops filtering, sets `DwellProgress(Reset) = 0` and `IsHighlighted = false`, and marks armed (timestamped for the 3 s expiry). The armed pass enters fresh — **no per-target reset API, no RemoveTarget/re-add** (which would drop geometry-grown extents).
- **Interactions moot by construction:** cooldown passes, gap re-anchor, and mid-freeze `SetEffectiveDwellTime` changes all operate on a detector that receives no in-union samples; `ResetAllState` clears everything as today, and the deactivation path hides the panel and clears the latch.

### 3.3 Placement, anchoring, auto-hide

- Anchored with its **near edge at Reset's outer end, extending away from the run of the strip** (past the last button), offset into the screen from the docked edge, connector tick per approved mockup, clamped to the work area. Centering the panel under Reset would put its upper rows inside the geometry-grown circles of Modes (24 px overlap at shipped defaults, 16 px at the owner's size, larger when the fraction/cap are tuned up). The beyond-the-end anchor keeps the panel clear of sibling hit areas **where the work-area clamp allows it** — a large toolbar on a side dock or a wide panel can clamp it back — so this is **defence-in-depth**: D10's feed-nothing rule makes any residual overlap safe regardless (review R2-H1, reworded per round 3).
- **Auto-hide must not take the strip away mid-read:** while the preview is visible the service suspends toolbar auto-hide via the existing `SetAutoHideSuspended` pattern — guarded so the preview's resume cannot clobber the settings window's suspension (only resume what we suspended; plan picks ref-count or ownership token). Resuming **resets** the hide countdown (`ToolbarAutoHideController`), so closing the panel cannot trigger an instant hide.
- **Z-order / other windows:** the preview is a topmost overlay like its siblings; opening Settings, the keyboard, or any window naturally moves gaze off the union, and the debounced leave hides the panel. No special "settings open" trigger exists.

### 3.4 Content & composition (per approved mockup A)

Four rows — Dwell, Tolerance, Smoothing, Geometry — plus footer "Reset clears all momentary *and* learned offsets." At baseline: four flat rows + muted "nothing active" line.

**Composition is the clamped, net quantity routing consumes:** learned and momentary forgiving fills are summed *then* band-clamped (`EffectiveSettingsProvider.Effective`), so bars are not two stacked independent segments:

- **Dwell** — bidirectional bar (shorter left / longer right; the knob has both a forgiving and the quarantined tightening fill). Direction word = **net** ("shorter" / "longer" / "—"); each side shows that direction's clamped composed fill; the learned portion is shaded slate within the clamped total (learned is forgiving-only, so the slate shade appears on the shorter side only).
- **Tolerance / Smoothing / Geometry** — single-direction bar: total = `clamp(learned + momentary, band)`, learned shaded within.
- Values: **Tolerance** absolute px vs baseline `DwellSettings.FixationRadiusPixels`; **Smoothing** absolute vs baseline with unit **per filter mode** — Hz (One-Euro), factor (EMA), "—" (Direct); **Dwell / Geometry** band % only (multi-baseline).

**Smoothing single source of truth:** `GazeEngine.EffectiveSmoothingParameter`'s per-mode composition+clamp is extracted into a static Core helper that both the engine and the snapshot builder call — the duplicated range literals stop existing; the oracle test pins engine ≡ helper.

### 3.5 Suppression & layering (D6 r3)

`IFeedbackGate` lives in `EyeCtrl.App.Services`, unreachable from `EyeCtrl.ViewModels`: the VM receives a **`Func<bool>? isFeedbackSuppressed`** constructor delegate (App wires the gate; `null` = never suppressed, the test default). While suppressed the **panel does not show and its content is not exposed** (§8.10: fills and offsets are interaction state) — but the freeze, latch, debounce, and expiry run **unchanged everywhere** (silent confirm): the one unrecoverable button is never single-step, in any context. The **last-published panel rect stays in the union** until a leave completes, so a panel hidden mid-read by suppression cannot arm the button under the user's gaze. Not gated on `ShowCue`.

### 3.6 Components & data flow

1. **ViewModels, `ToolbarViewModel`** — owns the state machine: feed-nothing while engaged, filtered feed during the leave run, held arc + highlight (`OnTargetProgress` override), 120 ms off-union debounce, 3 s latch expiry, suppression via the injected delegate, and a per-id cache of last-reported screen centers.
2. **Messages (repo `WeakReferenceMessenger` pattern)** — VM → service: `AdaptationPreviewVisibilityMessage(bool Visible, anchor center, docked edge)`; service → VM: `AdaptationPreviewBoundsMessage(PhysicalRect? PanelBounds)` — a **Core physical-px rect** (`EyeCtrl.ViewModels` is plain `net10.0`, no WPF; `HitTest` and `GazeCursorMovedMessage.Position` already use physical px; the App service converts from the window's DIP rect at its DPI). **`null` bounds mean "no panel"** (hidden, contained show failure, or service absent) and trigger the §3.1 fallback. Bounds are re-published on **every size change**; unknown-while-visible ⇒ engaged, bounded by the 500 ms timeout (D14). The service **creates no dwell targets**; it places and fills the window.
3. **App, `AdaptationPreviewService`** (shaped like `AdaptationCueService`) — anchors the window per §3.3, builds content from the snapshot, throttled refresh (≥ 500 ms) on `provider.Changed`, suspends/resumes auto-hide around visibility (guarded, §3.3), and **suspends activation offers while the panel is visible** (D16 — clearing any live offer, resuming on hide). Crash-contained (log-and-skip): a comfort feature must never take down the session — and on a contained show failure the service publishes **`null` bounds** so the VM falls back instead of freezing the toolbar forever (D14). The window is registered as a DI singleton (`CueOverlayWindow` precedent); the plan resolves it at startup and force-creates the HWND (`EnsureHandle`-equivalent) so first-show latency stays well inside the 500 ms fallback budget — an optimization, not a correctness dependency.
4. **App, `AdaptationPreviewWindow : OverlayWindow`** — the `CueOverlayWindow` precedent; registered in the overlay-inventory drift test and docs spec §31; themed via existing Tokens only (correct under Daylight automatically; no new palette entries).
5. **Core, pure `AdaptationPreviewSnapshot` builder** — over the provider's public reads (`TryGetMomentaryFill`, `EnumerateLearnedFills`, `Effective`) plus public baselines; emits per-knob rows per §3.4; calls the extracted smoothing helper.

## 4. Feature 2 — hide Reset when adaptation is disabled

- **Band:** `ToolbarBand.Build` gains `includeReset` (separator + `ResetGap` included only with the button); the flag comes from `AdaptationSettings.Enabled` at every build; the strip shortens when absent.
- **Flag delivery:** `ToolbarWindow.Initialize(double buttonSize, int dwellTimeMs, bool includeReset)` — callers are `OverlayManager` (startup) and the re-init block below.
- **Rebuild trigger:** `SettingsUiCoordinator` subscribes `AdaptationSettings.PropertyChanged(Enabled)` (the `AdaptationActivation` precedent) so **every** writer triggers the rebuild. The handler runs the **whole re-init block** the toolbar-snapshot branch runs — `Initialize` + `SetMode` + `UpdateEnabled` + `UpdateEdge` + `PositionIndicator` + `RefreshToolbarRect` — factored into one method both paths call. Deliberately NOT added to `ToolbarSnapshot` (toolbar-fields-only contract).
- **VM seam (D11):** `ToolbarWindow` calls `ToolbarViewModel.SetRenderedButtons(ids)` after every band build. The seam removes the detector target **and the `_positionedButtons` entry** for ids no longer rendered, zeroes the removed ids' button-VM `DwellProgress` and highlight, and — **when it reports `ResetId` absent** — clears the preview latch and publishes hidden if the panel was up. (Clearing on every build would drop a valid latch on unrelated rebuilds; the absent-report is the one and only band-side latch clear.) Target creation stays lazy via `UpdateButtonScreenPosition`.
- **Tray:** the "Reset adaptation" item's `Visible` is refreshed in the context menu's `Opening` event from the same setting.
- **Deliberate asymmetry:** the VM's static button list and its `ResetAdaptation` switch case remain; `ToolbarButtonDriftTests` encodes "band membership conditional on `Adaptation.Enabled`, VM list static".
- **Settings untouched:** "Reset to defaults" and "Reset learned offsets" stay on the Adaptation page; learned offsets survive a disable inertly and remain clearable without re-enabling.
- **Consequence (accepted):** with adaptation off the flyout is unreachable too — consistent, since effective settings equal baseline.
- **Test-fixture policy (review R2-M3):** STA/window tests that need Reset pass `includeReset: true` straight to `Initialize` — they do **not** enable adaptation, which in the launched app would start the whole runtime (coordinator, cue, JSONL log, learner persistence — adapting mid-test). `AppLaunchTests` asserts the **default** button set, which has no Reset; the enabled-band surface is covered headlessly (band + VM tests).

## 5. Edge cases & error handling

- **Disable mid-dwell / mid-preview:** band rebuild removes the target, abandons the dwell (never fires), the seam clears the latch and VM state, the panel closes.
- **Re-enable:** button returns with the band; latch cleared; next rest previews fresh. Pinned by a disable → enable → Reset-fires test with the arc starting at 0.
- **Auto-hide / axis flip / settings open:** any band rebuild or toolbar deactivation hides the preview and clears the latch; the debounced union-leave covers every gaze-away path.
- **Paused:** the toolbar is deliberately dwellable while paused (owner's paused-launch testing pattern) — freeze/latch/preview run identically; no special-casing.
- **Suppression:** §3.5 — panel hidden, silent confirm continues; last-known rect stays in the union; suppression never arms, never fires.
- **Preview plumbing failure (D14):** bounds never arriving (inert adaptation runtime — `AdaptationActivation`'s catch leaves `Enabled == true` with nothing behind it — or a lost message) falls back after 500 ms to union = Reset's hit area with the silent freeze held: **the toolbar — Pause included — never goes dead**. A contained service show failure publishes `null` and falls back immediately.
- **Activation offers (D16):** suspended while the panel is visible and any live offer is cleared; resumed on hide. Reading the panel is the whole interaction — no offer is placed around it.
- **After Reset fires:** existing cooldown unchanged; latch cleared; a later engagement previews fresh.
- **Neighbour growth:** the panel anchors beyond the strip's end; the in-union feed-nothing rule makes even a grown-overlap gaze position safe (zero activations, zero outcomes).
- **Containment:** preview operations crash-contained (log-and-skip); the state machine is pure VM state, headless-testable.

## 6. Testing

- **Core:** `AdaptationPreviewSnapshot` rows — fills, learned-only, all-at-baseline, tolerance px, smoothing per filter mode incl. Direct "—", bidirectional dwell (tightening-only "longer", mixed, learned+momentary past the band → clamped display), and the **oracle test** pinning the extracted smoothing helper ≡ the engine's applied value.
- **ViewModels** (existing `Adaptation` test folder): the state machine — engagement at 0.40; **panel → button → panel stays, no fire**; **the acquired face (`IsHighlighted`) and the arc hold while frozen**; **geometry fill 1.0, gaze held 3 s in the Modes/Reset overlap then at the panel's top-left → zero activations, zero outcomes**; several seconds reading → no hide, no fire; **bounds-unknown ⇒ no debounce — but after 500 ms of bounds never arriving, Pause still fires** (fallback: union = hit area, silent freeze); **`null` bounds (contained show failure) ⇒ immediate fallback**; 1-sample and 3-sample off-union excursions do not arm; debounced leave arms and drops arc + highlight to 0; **arm expiry at 3 s re-previews; a return within 3 s fires unfrozen with no panel event — including a return at 2.9 s with a 2000 ms dwell (expiry judged at the pass start)**; fire/deactivate/absent-report clear the latch; suppressed → no panel, freeze still engages; the **worst-case escape bound** (§7) pinned as a timing test.
- **App:** band layout with/without Reset (extent shrinks, no separator, `Contains(ResetId)` false); drift-guard update; tray `Opening` visibility; `PropertyChanged(Enabled)` triggers the full re-init block; `SetRenderedButtons` removes stale targets **and set entries**, zeroes stuck arcs, clears the latch **only on absent-report**; **disable → enable → Reset fires, arc from 0**; `AppLaunchTests` assert the default (no-Reset) set; **activation offers suspended while the preview is visible and resumed on hide** (coordinator test, D16); new window in the overlay inventory + docs spec §31.
- **Localization & theming (D15):** keys under `overlays.adaptationPreview.*` — surface **`overlays`**, English-only; German with T13. A **new fit class** is added (schema `FitClasses` + the Python validator + the fit harness, parity-gated) measuring the preview's row strings in the production control at scale 2.0 — German forms ("kürzer", "länger", "lockerer", "größer") budget against it when T13 lands. Palette untouched (existing Tokens only).

## 7. Governing-spec amendment (D12)

The struggle-adaptation design (`2026-07-19-struggle-detection-self-adaptation-design.md`) promises an **"instant escape"**. The seen-latch makes the **toolbar** escape two-step. **An explicit implementation deliverable of this feature (its own commit):** amend §8.2/§8.6 of that document to read:

- The toolbar Reset becomes: freeze-preview pass → debounced leave → one full armed dwell, **returned within 3 s of arming** (after expiry the sequence restarts with a preview). **Worst case to reset** = `0.40 × ToolbarButtonDwellMs` + 120 ms + return + `1 × ToolbarButtonDwellMs` (defaults ≈ 1.1 s plus the return; the dwell term may itself be shortened by live adaptation).
- The **tray** Reset item remains a single-step instant escape (mouse path), and Settings' "Reset learned offsets" is unchanged.

## 8. Out of scope

- A live status panel inside Settings → Adaptation (rejected — D2).
- Absolute effective values for the multi-baseline knobs.
- Gating the preview on `ShowCue`.
- A dedicated armed-pass arc hue beyond the moving arc (noted follow-up polish).
- Confirm-step mechanics for any button other than Reset.
- German translations for the preview surface (T13's paused scope).

## 9. Residual known gaps

- The §3.1 residual single-sample race for D < 250 ms with a gap in [0.6·D, 150 ms) (fires with no preview; equals today's behavior).
- Auto-hide suspension ownership (ref-count vs token) — plan-level.
- The exact fit-class id and harness control wiring — plan-level (the class itself is committed to here).

## 10. Review ledger

### Round 1 (2026-10-13 — all accepted, r2)

| Finding | Resolution |
|---|---|
| C1 reading = leaving; glance-back fires | Union engaged region + debounced arm + auto-hide suspension (D7) |
| H1 no leave debounce | 120 ms continuous off-union run; excursion tests |
| H2 armed-pass bait panel + §8.6 contradiction | No panel on armed pass; struggle-spec amendment with bound (D8, D12, §7) |
| H3 preview feeds struggle telemetry | Zero-outcome guarantee (D9) |
| M1 straw man + under-specified Core freeze | Filter-based freeze, no Core API (D10 — further revised in r3) |
| M2 `IFeedbackGate` is App-layer | `Func<bool>` delegate (§3.5) |
| M3 dead button / stuck arc / no seam | `SetRenderedButtons` (D11, §4) |
| M4 half-named re-init path | Full block factored; `PropertyChanged(Enabled)` (§4) |
| M5 default band has no Reset; 20 test refs | `Initialize` flag + fixture policy (§4) |
| M6 base 34 behind origin/main | Rebased onto `408e5734` |
| G1 tightening + band clamp ignored | Net bidirectional dwell bar; clamped totals (§3.4) |
| G2 smoothing units + duplicated clamp | Extracted static Core helper + oracle test (§3.4) |
| G3 anchor data path + grown-target overlap | Rect messages both ways; union test (§3.6) |
| G4 "settings open" trigger had no mechanism | Dropped; union-leave covers it (§3.3) |

### Round 2 (2026-10-13 — all accepted, r3)

| Finding | Resolution |
|---|---|
| R2-H1 neighbour (Modes) dwell fires mid-read; panel under grown circles | In-union **feed-nothing** (pause-latch precedent); Reset-excluded feed only during the leave run; panel anchored **beyond the strip's end**; overlap test at geometry fill 1.0 (D10 r3, §3.2, §3.3, §6) |
| R2-H2 armed latch never expires (auto-hide 5 s / disabled for floating + Mouse) | **3 s expiry** from arming; within-T qualifier added to §7; expiry + within-T tests (D13, §3.1, §6) |
| R2-H3 panel-bounds race reopens C1; `Rect` wrong layer | Bounds-unknown ⇒ engaged; republish on size change; pre-created DI-singleton window; Core physical-px rect (D14, §3.1, §3.6) |
| R2-M1 l10n contradicts string-table schema | English-only under the closed `overlays` surface, German with T13; new fit class for row strings (D15, §6) |
| R2-M2 D6 coupled safety to suppression; mid-freeze promise couldn't hold | Silent confirm everywhere; panel-only hiding; last-known rect stays in union until leave completes (D6 r3, §3.5) |
| R2-M3 fixture would enable adaptation in the launched app | `includeReset` passed directly; `AppLaunchTests` assert the default set; enabled case headless (§4) |
| Low: race arithmetic | Corrected: D < 250 ms, gap ∈ [0.6·D, 150 ms) (§3.1, §9) |
| Low: hold `IsHighlighted` too | Held together with the arc; cleared on arm (§3.2) |
| Low: latch-clear wording conflict | One rule: the seam clears the latch **only on absent-report** (§3.1 step 5, §4) |
| Low: D12 amendment not a deliverable | Explicit own-commit deliverable (D12, §7) |

### Round 3 (2026-10-13 — all accepted, r4)

| Finding | Resolution |
|---|---|
| R3-H1 "unknown ⇒ engaged" had no timeout; inert runtime (`AdaptationActivation` catch leaves `Enabled == true`) or a contained show failure could freeze the whole toolbar — Pause included — forever on floating/Mouse | **500 ms unknown-bounds timeout** → fallback union = Reset hit area, silent freeze held, logged; **`null` bounds message** ("no panel") on contained failure → immediate fallback; while unknown and gaze off the button, feed with **Reset excluded** (other buttons live); tests: bounds never arrive → Pause still fires; null → fallback (D14 r4, §3.1, §3.2, §3.6, §5, §6) |
| R3-H2 panel sits outside every chrome zone, so the activation offer lands around it mid-read (decision 8) | **Offers suspended while the panel is visible** (`SetActivationOffersSuspended`, clearing live offers — the Settings/task-switcher/quick-actions/eye-care-banner treatment, owner decision 2026-10-02 precedent); zone-composition rejected; coordinator test (D16, §3.6, §5, §6) |
| R3-M1 3 s expiry could land mid-armed-pass, re-freezing at 0.40 with the panel up (D8 bait inside the escape) | Expiry judged at the **start of the return**; a pass begun armed runs to fire or abandon; test: return at 2.9 s, 2000 ms dwell → fire, no panel event (D13 r4, §3.1, §6) |
| Low: §3.3 anchor claim is conditional on the work-area clamp | Reworded as **defence-in-depth**; feed-nothing is the guarantee (§3.3) |
| Low: DI singleton ≠ fast (HWND is created on first `Show`) | Plan resolves the window at startup and force-creates the handle — optimization; the 500 ms timeout is the correctness bound (§3.6) |
