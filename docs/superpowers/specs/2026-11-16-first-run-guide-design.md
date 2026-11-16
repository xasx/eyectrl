# First-Run Introduction Guide — Design

- **Date:** 2026-11-16
- **Status:** Rev 2.0 — external review r1 processed (verdict FIX-FIRST, 6×S1 + 4×S2 + 2×S3; all 12 findings verified valid, none pushed back). r1's headline correction: the interaction model is **`GuideOpen` claims**, not a receiving backdrop — shipped calibration is click-through (`CalibrationWindow.xaml:12`) and keeps the desktop inert via `AcquireFixationSuppression` (`OverlayMessageCoordinator.cs:283`) and `ActivationOfferCoordinator`/`ModalInteractionState`, so the guide uses the same seams with its own suppressor member and modal-state claim. Full disposition: §13.
- **Supersedes:** the startup Safety Setup's *surface* — its question chain survives verbatim inside the guide (§7 step 7), plus one targeted race fix inside that chain (§7 step 7, r1 F9).

## 1. Summary

A first-run introductory guide replaces the startup safety setup as the app's first-run experience. It presents the app's use interactively (live try-it tasks), guides through the most important settings, and offers calibration — then walks the owner through the five safety questions via the existing `SafetySetupFlow` chain. The guide is a centered card over a fullscreen **click-through** backdrop (the shipped calibration model), kept inert by explicit **`GuideOpen` claims** in the app's existing suppression seams, driven by a headless state machine in `App/Services` — the `SafetySetupFlow` pattern: WPF-free logic, callback-driven surface, headlessly testable.

Owner picks (answered interactively):

- **One flow** — the guide absorbs the safety questions; `SafetySetupFlow` is retired as a standalone raiser.
- **Live try-it tasks** — dwell a target to continue; every task skippable.
- **Settings steps:** dwell time + theme, in addition to the absorbed safety questions.

## 2. Motivation

A new install of EyeCtrl hands a person a completely gaze-driven PC with no instruction. The current first run asks five safety questions through the Settings window's modal, but never teaches the interaction model (dwell), never helps tune the single most personal setting (dwell time), and never surfaces calibration. The guide turns the first run into a supervised hands-on introduction, while preserving the safety setup's never-strand semantics.

## 3. Non-goals

- No keyboard-typing practice task (needs real-keyboard interplay; candidate follow-up, §10 L9).
- No video/animation tutorials.
- No changes to `CalibrationWindow`, `CalibrationService`, or the calibration transform itself — the guide only launches the existing flow and reacts to its state/completion messages.
- No mid-session theme re-skinning of other live surfaces — theme applies at startup (r1 F2); the guide only previews locally (§7 step 6).
- No new persisted settings flag and no settings migration (§4 uses `GeneralSettings.SafetySetupDone` as-is).
- No installer (`EyeCtrl.Setup`) or Supervisor changes.
- No new KB-E1 logging (the guide is not a keyboard event surface); startup-only Information lines, content-free per §9.

## 4. Trigger, flag, re-run (L1, L5)

- **Gate (unchanged semantics):** `SafetySetupStartup.ShouldPrompt(safetySetupDone, isPausedLaunch, isTestMode)` keeps its three conditions: prompts only while `GeneralSettings.SafetySetupDone` is false, never on a `--paused` launch (crash-recovery retry's inert relaunch), never in `--test-mode`. A normal crash-recovery relaunch re-prompts (same as today).
- **Raise:** `SafetySetupStartup.MaybeRequest` sends the renamed `FirstRunGuideRequestedMessage` (was `SafetySetupRequestedMessage`) from the same startup call site after `ShowOverlays`. **The guide window's `Show()` is deferred until `SplashWindow.ExitFinished`** (r1 Q3) — the splash's exit tail runs asynchronously on its own STA thread for ~1–2 s, and an immediate show would collide with it; when no splash is shown (test mode), the guide shows immediately. Startup wiring order is otherwise unchanged (`supervisionToggle.RequestEnsure()` and `PauseLaunchProvenance.Apply` precede the raise, `StatusCardPresenter.Start()` ordering unchanged).
- **Re-run entry:** the existing **`g_safety` action row in Settings → General** ("Run safety setup again", `GeneralPageSource.cs`) is **replaced** by "Run the introduction guide" (r1 F4): it follows the Calibration page's action-row pattern — `_ctx.Close()` **synchronously first**, then send `FirstRunGuideRequestedMessage`. The close matters: an open Settings window hides `CursorCircle` (`!_modal.SettingsOpen` in `OverlayVmBindingCoordinator`) and holds the `"settings"` claim on `ToolbarLifecycleCoordinator.SetAutoHideSuspended`, which would break step 5's summon task. Full replay including the safety questions (answers re-apply; idempotent); refuses while a guide is already open (host's one-flow rule).
- **Completion flag:** `GeneralSettings.SafetySetupDone` stays the single latch, and its **latching point is unchanged**: the reused `SafetySetupFlow` sets it at the safety block's last answer (`Answered`, index == QuestionCount−1), exactly as today — the guide does not add a second write. The **Done card is informational** (Finish merely closes the guide and lifts the claims). "Later" on the welcome card or the chrome Close (§6) leaves the flag false → the guide re-prompts at the next start; once the safety block has completed, the flag is true and a later crash does not re-prompt (all mandatory content was consumed; only the cheat-sheet was missed — accepted).

## 5. Window & surface

- **`FirstRunGuideWindow`** — `OverlayWindow` family, fullscreen topmost, **`IsClickThrough = true`** (calibration parity: OS clicks pass through harmlessly because the pipeline is suppressed — see below); centered content card ≈ 560 × 440 DIP (content-sized per step, bounded), `Tokens.BrushSurfaceDark` + `Tokens.LargeCornerRadius`, styled after the splash card. `AllowsTransparency = true` with **no post-`Show` `Opacity` writes and no fade animations** (r1 F10) so `InvisibleTestWindow.Prepare` stays valid; if a fade-in is ever wanted, it must use `PrepareCloaked`-compatible design — decided at plan level.
- **Inertness via `GuideOpen` claims (r1 F1):** while the guide is open the guide flow holds:
  1. **`AcquireFixationSuppression(FixationSuppressor.Guide)`** — a new `FixationSuppressor` member (Core enum), acquired on guide open, released on close/abandon — the same seam calibration uses during capture (`OverlayMessageCoordinator.cs:283`), so no desktop dwell-activation offers fire under the click-through backdrop.
  2. **`ModalInteractionState.GuideOpen`** — a new member of the combined modal-suspension state, included in `ActivationOfferCoordinator.RecomputeSuspension` — which already suppresses both activation offers and `NoteManager` (via `ModalSuspended`) without touching `CalibrationStateChangedMessage`. **The guide never broadcasts synthetic calibration state messages** — `OverlayMessageCoordinator` reacts to those by showing/hiding `CalibrationWindow`, deactivating toolbar dwell (`_toolbar.SetActive(false)`), and hiding `CursorCircle`; a synthetic broadcast would corrupt those surfaces.
  3. **`canInterrupt` gate:** the eye-care break raise in `App.xaml.cs` (`canInterrupt: im.CurrentState == InteractionState.FreeLook`) gains `&& !modal.GuideOpen` — no eye-care break interrupts the guide mid-task.
  4. **Proactive-surface pins:** `StatusCardPresenter` banners (including the NoTracker card queued at startup, `App.xaml.cs:424`), `RestartNoticeHost`, and `DriftNoticeService` defer/hold while `GuideOpen` is true (exact gate per component pinned at plan level after tracing each raise path).
- **Dwell controls in the card work under click-through** — gaze-tracked WPF dwell controls fire their own activation events, no OS click needed (`CalibrationWindow`'s `CancelDwellButton`, a dwell target inside a click-through window, is the shipped precedent).
- **Dwell knobs (r1 F7):** the **practice target binds live `DwellSettings.ActivationButtonDwellMs`** — that setting governs desktop click activation (`GazeEngine.cs:481`) and is the concept step 3 teaches. Guide navigation buttons (Next/Skip/Close/Finish) and choice buttons bind `DwellSettings.ToolbarButtonDwellMs` (the in-window UI dwell, `SettingsWindow.xaml.cs:510`), protected by the disarm latch on card transitions — so dragging the slider low never makes the guide's own controls mis-dwell.
- **Disarm latch:** every newly shown card holds its dwell choices disarmed until the gaze has left the card — `LeaveBeforeArmLatch` (standalone class in `EyeCtrl.ViewModels`, takes a `DwellDetector` + release-margin func) reused or its pattern reproduced in the guide window; the plan picks reuse-vs-reproduce after wiring the guide's detector.
- **String surface:** all guide text through `StringTable.Current.T(...)` under a **new closed `guide` surface** (r1 F8): `StringTableSchema.Surfaces` gains `guide` (a key's prefix must equal its surface), with the matching updates in `tools/strings_validate.py` (+ its test) and the German side (`src/EyeCtrl.App/Localization/strings.de.json` + `strings.de.manifest.json`, `src` hashes via `strings_validate.py --refresh-de-src`), and a fit class assigned to every `guide.*` key.

## 6. The flow state machine

`FirstRunGuideFlow` (internal sealed, `App/Services`, WPF-free): an ordered step list plus transition rules, driving the window through a narrow surface interface (`IGuideSurface`), headlessly testable with fakes — the `SafetySetupFlow` pattern.

```
Welcome → MeetDwell → DwellTime → Calibrate → ToolbarTask → Theme → Safety → Done
   ↓ Later / Close                                  (Calibrate and ToolbarTask omitted per §7)
 close (flag stays false unless the safety block completed, re-prompt next start)
```

- **Events:** `Start`/`Next`/`Skip`/`Close` (per-step policy), `TaskSucceeded` (practice target, toolbar summon), `CalibrationIdle`/`CalibrationCancelled` (step 4's advance triggers), `SafetyEnded(completed)`, `WindowClosed` (abandon).
- **Surface contract:** the flow knows only `IGuideSurface` (show step content, choices, task state); the window's code-behind implements it by updating `FirstRunGuideViewModel`, which the XAML binds. The VM never talks back to the flow — input flows window → flow events only.
- **Chrome Close (r1 F6):** every card except Welcome (which has "Later") and Done (which has "Finish") carries a persistent, gaze-dwellable **Close** target in the card chrome — present **through step 7 as well** (`OverlayWindow` has no titlebar, `WS_EX_NOACTIVATE`, so there is no gaze-reachable Esc; without it a gaze-only user is trapped inside the safety block). Close calls `Abandon()` and closes without latching the flag — the gaze-reachable equivalent of today's close-the-Settings-window semantics.
- **Skip policy:** steps 2 (MeetDwell) and 5 (ToolbarTask) are skippable tasks; step 4 (Calibrate) is skippable; steps 1/3/6/7/8 are not (3 and 6 are Continue-only; 7 is answerable-through and **latches the flag** at its last answer; 8 is informational).
- **Safety block (§7 step 7):** the flow owns a `SafetySetupFlow` instance. **`StartQuestions()` contract (r1 F6):** performs the same initialization as `Start()` minus the intro card — `_active = true; _pendingFailure = null; Advance(0);` — and the guide supplies `setSimpleSettingsUi` as `v => { settings.General.SimpleSettingsUi = v; applyAndSave(); }`. `onEnded(true)` → advance to Done; the chain's failure-hold/checking cards render through the same card surface, chrome Close available throughout.
- **Claims lifecycle:** guide open → claims acquired (§5); step 4's calibration phase **releases nothing early** — the calibration flow itself acquires `FixationSuppressor.Calibration` and deactivates toolbar dwell; the guide's own claims stay held so the backdrop remains inert while the calibration window is up. Close/abandon/finish → all claims released, in acquisition order.
- **Abandon:** `WindowClosed`/chrome Close stops the safety chain (`Abandon()`), releases the claims, keeps applied answers; flag untouched.
- **Re-run:** `FirstRunGuideHost` (window-side, headless like `SafetySetupHost` was) enforces one flow at a time and refuses a re-run while open.

## 7. The eight steps

### 1) Welcome
What EyeCtrl is in two sentences; "~2 minutes"; "every practice task can be skipped". Choices: **Start** / **Later**. Later closes the guide (flag stays false; re-prompts next start).

### 2) Meet dwell — live practice
Card: what the dwell ring means (progress arc, activation on completion). Content: a **big practice target** (DwellArcButton-class, in-card gaze-tracked control bound to live `ActivationButtonDwellMs`) inside the card. Task: dwell it once. Success → auto-advance. **Skip** available — nobody gets stranded.

### 3) Dwell time — live tuning
Slider over the 200–2000 ms range bound to `DwellSettings.ActivationButtonDwellMs`, written through the same live-apply path Settings rows use. The practice target sits beside the slider so each change is felt immediately. **Copy says what it tunes: the desktop dwell-click** (the cursor dwell that clicks — `GazeEngine.cs:481`); a note that the guide's own buttons keep the standard UI dwell (`ToolbarButtonDwellMs`). Continue when ready.

### 4) Calibrate (omitted without a real tracker)
Card: "Make sure the cursor follows your eyes." **Calibrate now** hides the guide window, sends `CalibrationRequestedMessage`, and **waits for `CalibrationStateChangedMessage` to return to `Idle` (or `Cancelled`)** before re-showing and advancing (r1 F3) — not on `CalibrationCompletedMessage`, which fires the moment the result state is entered while `CalibrationWindow` still shows its completion card (good results auto-dismiss in ~3 s; poor results wait for Retry/Accept, and **Retry loops back to `Collecting`**, so only the `Idle`/`Cancelled` transition is a safe advance signal). On re-show, **`CursorCircle` is re-raised above the guide** (calibration hides it; `OverlayMessageCoordinator` re-shows it — the plan verifies it lands above the guide window). **Skip** → advance. Without a real tracker (`App.ActiveProviderType == GazeProviderType.Mouse` — the splash's no-tracker signal) the step is omitted. Known pre-existing limitation, not the guide's to fix: calibration's completion `Retry`/`Accept` are mouse-only `<Button>`s (r1 Q5) — a gaze-only user waits out the auto-dismiss; the guide's own affordances stay dwellable.

### 5) Toolbar summon — live practice (omitted when floating or mouse-only)
Card: the docked edge band is the way to the toolbar (the keyboard, settings, everything opens from there). Task: summon the toolbar by dwelling the docked edge. **The step is omitted when `!settings.Toolbar.IsDocked || App.ActiveProviderType == GazeProviderType.Mouse`** (r1 F5) — `ToolbarAutoHideController` is enabled only for a real tracker with a docked toolbar (`ToolbarLifecycleCoordinator.cs:338-339`), so in floating/mouse setups the summon never fires and the step would strand. On entry the toolbar is forced hidden (if it was up); success = a toolbar-shown observation on the `IToolbarVisibility.ShowToolbar` path; the card updates and **Continue** advances. **Skip** available. During the task the **`ToolbarWindow` is raised above the guide** (`ToolbarVisibilityAdapter.ShowToolbar` never calls `RaiseToTop()` and the guide is created later in `ZBID_UIACCESS`, so an un-raised toolbar would slide in behind the backdrop); `CursorCircle` likewise (r1 F3/F5). The band summon itself is coordinate-driven and needs no interaction gating.

### 6) Theme (local preview + restart-applied persist)
Four chips (Midnight, Daylight, Ember, Contrast) (r1 F2): selection **previews locally** — the guide card resolves the chosen theme via `ThemeRegistry.Resolve(name)` and re-skins **its own card only** — and **persists `settings.Theme.Name`** through the Settings write path. It **must not** call `ThemeStartup.Apply` or mutate `Tokens.Current`: themes apply at startup only (`ThemeStartup.Apply(settings, appResources)` at boot; `OnSettingsChanged` re-applies nothing), and mutating `Tokens.Current` mid-session would leave already-created overlays on startup brushes — mixed-polarity UI until restart. The card carries the same note the Settings row uses (`settings.general.theme.description`: takes effect after restart). Continue.

### 7) Safety questions (absorbed)
Transition card ("5 quick questions to keep you safe") → `SafetySetupFlow.StartQuestions()` (§6 contract) runs the five questions (crash recovery, start on boot, dwell cue, track status, Basic/All mode) as dwell-choice cards on the guide surface, with the chain's crash-recovery wait ("Checking crash recovery"), ceiling, and held-failure cards unchanged, chrome Close available throughout. The chain's last answer sets `SafetySetupDone = true` (its own latching, untouched — §4). **No skip** — each question is a Yes/No answer; the exits are the chrome Close (abandon: answers applied stay, flag stays false) or answering through.

### 8) Done
"You're set." + cheat-sheet pointers: the docked edge summons the toolbar (main menu); the keyboard opens from the toolbar; every setting lives in Settings. **Finish** closes the guide and releases the claims — the flag was already latched by the safety block's last answer (§4). A re-run from Settings ends here identically.

## 8. Data flow

| Step | Write |
|------|-------|
| 3 Dwell time | `DwellSettings.ActivationButtonDwellMs` (desktop dwell-click knob) via the live-apply + debounced-save path; guide nav buttons stay on `ToolbarButtonDwellMs` |
| 4 Calibrate | none (delegates to the existing calibration flow) |
| 6 Theme | `settings.Theme.Name` persisted (startup-applied; guide previews locally only) |
| 7 Safety | the five `SafetySetupFlow` writes, unchanged — its last answer also sets `GeneralSettings.SafetySetupDone = true` (existing latching) |
| 8 Done | none (informational card) |

## 9. Edge cases

| Case | Behavior |
|------|----------|
| No real tracker (mouse fallback) | Calibrate step omitted; ToolbarTask step omitted (§7 steps 4/5); splash already announces the fallback |
| Floating toolbar | ToolbarTask omitted (docked-only gating, r1 F5) |
| Tracker lost mid-guide | Task steps are skippable; the dwell-time step still applies its value |
| Calibration retry loop | Guide stays hidden through `Collecting` re-entry; advances only on the `Idle`/`Cancelled` transition (§7 step 4) |
| Crash-recovery bring-up fails mid-questions | Existing held-failure card + OK, chain machinery unchanged |
| Crash-recovery result never arrives | Existing ceiling → "could not be confirmed" failure card |
| **Unrelated supervision flight in progress** (startup `RequestEnsure()` or a Settings `RequestDisable()` still `InFlight`) when crash recovery is answered "Yes" | **Targeted chain fix (r1 F9):** `SafetySetupFlow.RequestCrashRecovery` no longer attaches to an unrelated flight — when `_supervision.Enabled` is already true it treats the answer as a no-op success, and an in-flight disable/ensure is not mistaken for the enable's result (exact mechanism — flight-kind exposure vs. enabled-guard — pinned at plan level; `RequestEnable`'s early return is `SupervisionToggleService.cs:151`) |
| Window closed mid-flow | Abandon: applied answers stay, flag false unless the safety block completed, re-prompt next start |
| Re-run requested while guide open | Refused (one flow at a time) |
| Re-run triggered while user-paused | The guide runs (r1 F12): in-card dwell controls are gaze-tracked and work under user-pause; the summon task works (pause deliberately doesn't gate toolbar dwell); the pause indicator stays visible above the guide (z-order pinned with step 5's raise rule) |
| Gaze rests on the next card's buttons | Disarm latch: choices armed only after gaze has left the card |
| Proactive surfaces pop mid-guide (status-card banners, restart notices, drift notices, eye-care breaks, notes) | Held/deferred while `GuideOpen` (§5 claims 2–4); released on close |
| Splash still exiting when the guide raises | `Show()` deferred until `SplashWindow.ExitFinished` (§4); immediate when no splash |
| KB-E1 / logging hygiene (r1 F12) | Guide log lines carry **step ids and state transitions only** — no localized text, no user content; the KB-E1 content-free contract applies to anything the guide logs |

## 10. Decision ledger (owner-vetoable)

Choices taken while the owner was not answering interactive prompts; every entry can be overturned at review. r1-revised entries marked.

- **L1 — Re-run semantics.** Re-prompt at every start until completed to the end; manual re-run entry replacing `g_safety` in Settings → General (full replay). *Rejected alternative:* prompt-once-only ("Later" means never again) — a skipped guide stays unseen until found, contradicting never-strand.
- **L2 — Approach: dedicated guide window.** The owner's live-task pick rules out the Settings-modal chain (`ModalChoice` cards cannot host a practice target, slider, or theme preview). *Rejected:* hybrid (guide window + safety questions back in the Settings window) — two surfaces alternating mid-flow, no payoff over L4's reuse.
- **L3 — Step order.** Welcome → MeetDwell → DwellTime → Calibrate → ToolbarTask → Theme → Safety → Done, with steps 4 and 5 conditionally omitted (no tracker / floating toolbar). *Alternative considered:* calibration first (accuracy before practice) — rejected: the practice target is deliberately big/tolerant, and the interaction model is the first thing a new user needs; calibration before the toolbar task keeps that task accurate.
- **L4 — Reuse `SafetySetupFlow` for the safety block** (driven by the guide's card surface) instead of rewriting the five questions as native guide steps — plus one targeted race fix inside the reused chain (§9, r1 F9). *Rejected alternative:* rewrite — would re-implement the crash-recovery wait/ceiling/failure-hold machinery and its test coverage.
- **L5 — Single completion flag.** `GeneralSettings.SafetySetupDone` remains the latch (name now understates its meaning — "first-run setup completed"). *Rejected:* a new `FirstRunGuideDone` flag — always set together, so it would be a redundant migration. *(r1: also carries the step-omission conditions, §7 steps 4/5.)*
- **L6 — Toolbar task without pause exceptions.** *(r1-revised: the original pause premise is gone — there is no guide pause; inertness comes from `GuideOpen` claims, §5.)* The task runs only when it can (docked + real tracker, §7 step 5), the summoned toolbar is raised above the guide, and the owner can strike the task outright.
- **L7 — Re-run = full replay** including the safety questions. *Rejected alternative:* replay minus questions — needs per-block flags; YAGNI. Sound across re-runs: the chain only ever sets the flag, never resets it (r1 Q2); the unrelated-flight race is fixed separately (§9).
- **L8 — No mockups during brainstorm.** The owner was not answering; layout is specified textually (§5). Visual acceptance happens at implementation review; the design-at-physical-size rule applies to any mockups then.
- **L9 — No keyboard-typing practice task in v1.** Real-keyboard interplay (window summon, its dwell keys, gating scoping) is too heavy for v1; parked as a candidate follow-up step.

## 11. Files summary

### New

| File | Layer | Role |
|------|-------|------|
| `EyeCtrl.App/Services/FirstRunGuideFlow.cs` | App | Headless step state machine (steps, skip policy, task signals, owns the `SafetySetupFlow` instance, claims lifecycle) |
| `EyeCtrl.App/Services/FirstRunGuideHost.cs` | App | Window-side host: one flow at a time, re-run refusal, abandon on close |
| `EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml(.cs)` | App | Fullscreen click-through backdrop + centered step card (`AllowsTransparency`, no post-show Opacity writes); chrome Close; disarm latch; dwell choices, practice target, slider, theme chips |
| `EyeCtrl.ViewModels/FirstRunGuideViewModel.cs` | ViewModels | Binding surface for the current step (title, body, choices, progress, task state) |
| `tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs` | Tests | Headless flow tests |
| `tests/EyeCtrl.App.Tests/Services/FirstRunGuideHostTests.cs` | Tests | Host tests (one-flow, refusal, abandon) |
| `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs` | Tests | STA card tests (Category=UI, `InvisibleTestWindow.Prepare`) |

### Modified

| File | Change |
|------|--------|
| `EyeCtrl.App/Services/SafetySetupFlow.cs` | Add `StartQuestions()` (§6 contract); `RequestCrashRecovery` unrelated-flight guard (§9, r1 F9); `SafetySetupStartup` message renamed to `FirstRunGuideRequestedMessage`; `SafetySetupLaunch`/`SafetySetupHost` deleted |
| `EyeCtrl.App/Views/SettingsWindow.xaml.cs` | `SafetySetupHost` field + wiring removed (r1 F4) |
| `EyeCtrl.App/Views/Settings/SettingsPageContext.cs` | `RunSafetySetup` (or equivalent) removed/replaced by the guide raise |
| `EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs` | `g_safety` row replaced by "Run the introduction guide" (`_ctx.Close()` then send, r1 F4) |
| `EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs` | Handle `FirstRunGuideRequestedMessage`: open + deferred-show `FirstRunGuideWindow`, start the flow (replaces `SafetySetupLaunch.Run`) |
| `EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs` | `ModalInteractionState.GuideOpen` included in `RecomputeSuspension` (offers + notes, r1 F1) |
| `EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs` | no change to calibration handling; consumes nothing new (the guide never fakes calibration messages) — listed because its calibration branch is the parity reference |
| `EyeCtrl.ViewModels/Messages/...` (`ModalInteractionState.cs` or its home) | `GuideOpen` member added |
| `EyeCtrl.Core/FixationSuppressor.cs` | `Guide` member added (acquired/released by the guide flow) |
| `EyeCtrl.App/App.xaml.cs` | `canInterrupt` gate gains `&& !modal.GuideOpen`; guide show-deferral ordering after `ShowOverlays` |
| `EyeCtrl.App/Services/StatusCardPresenter.cs`, `RestartNoticeHost`, `DriftNoticeService.cs` | Defer/hold raises while `GuideOpen` (§5 claim 4) |
| `EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs` (or `ToolbarWindow.xaml.cs`) | `RaiseToTop()` for the summoned toolbar above the guide during step 5 (r1 F5) |
| `EyeCtrl.App/Services/AppBootstrapper.cs` | DI registrations (flow, host, window, VM) |
| `EyeCtrl.App/Services/OverlayManager.cs` (+ `IOverlayWindows`) | Window init in the overlay set |
| `EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | `FirstRunGuideRequestedMessage` (+ step/task messages the surface interface needs) |
| `EyeCtrl.Core/Localization/StringTableSchema.cs` | `guide` surface added to the closed `Surfaces` list (r1 F8) |
| `tools/strings_validate.py` (+ `tools/strings_validate_test.py`) | Validate the new surface; refresh German `src` hashes (`--refresh-de-src`) |
| `EyeCtrl.Core/Localization/strings.json` | `guide.*` keys with fit classes |
| `EyeCtrl.App/Localization/strings.de.json` (+ `strings.de.manifest.json`) | German `guide.*` translations + manifest |
| `EyeCtrl.ViewModels/LeaveBeforeArmLatch.cs` | Reused unchanged (or the pattern reproduced in the guide window — plan decides) |

### Unchanged but load-bearing

| Piece | Why it matters |
|-------|----------------|
| `SafetySetupFlow` question chain | Absorbed verbatim (L4) minus the targeted §9 race fix — crash-recovery wait/ceiling/failure machinery and tests survive |
| `CalibrationRequestedMessage`/`CalibrationStateChangedMessage`/`CalibrationCompletedMessage`/`CalibrationCancelledMessage` | Step 4 rides them; advance keys on the `Idle`/`Cancelled` state transition (§7 step 4) |
| `ThemeRegistry.Resolve` / `ThemeStartup.Apply` | Step 6 previews via the former; never calls the latter mid-session (r1 F2) |
| `IToolbarVisibility.ShowToolbar` path | Step 5's success observation |
| Dwell-knob split: `ActivationButtonDwellMs` (desktop click) vs `ToolbarButtonDwellMs` (in-window UI) | Steps 2/3 teach the former; guide chrome uses the latter (r1 F7) |

## 12. Testing guidance

- **Flow tests (headless, fakes — NFluent, xUnit v3, private nested fakes):** step sequence and order; Calibrate/ToolbarTask omitted per their conditions; skip policy per step; task-success auto-advance (practice target, toolbar summon); calibration advance keys on `Idle`/`Cancelled` transitions only (a `Collecting` re-entry after Retry does **not** advance, and `CompletedMessage` alone does not re-show); safety block: transition card → `StartQuestions` → completed → advance; `StartQuestions` initialization contract (no intro card, chain runs from question 0); chrome Close during step 7 abandons without latching; "Later" closes with flag false; the chain's last answer sets the flag (unchanged latching); window-closed abandon (chain abandoned, claims released, flag untouched); re-run refusal while open; claims acquired on open and released on close/abandon/finish (surface-level pins); the §9 unrelated-flight guard (in-flight disable/ensure is not mistaken for the enable's result; already-enabled "Yes" is a no-op success).
- **Startup gate tests:** `ShouldPrompt`/`MaybeRequest` unchanged conditions but raising `FirstRunGuideRequestedMessage`; show-deferral until `SplashWindow.ExitFinished` (immediate when no splash) — update the existing `SafetySetupStartup` pins.
- **Window tests (STA, Category=UI, no visible window — `InvisibleTestWindow.Prepare`, which requires `AllowsTransparency=true` and no product post-`Show` Opacity writes; `PrepareCloaked` if a fade is ever added):** a card renders per step VM; choices armed only after gaze leaves the card (disarm latch); chrome Close present on steps 2–7; dwell-time slider writes `ActivationButtonDwellMs` while chrome buttons stay on `ToolbarButtonDwellMs`; theme chips re-skin **the card only** (no `ThemeStartup.Apply`/`Tokens.Current` mutation — pinned by asserting `Tokens.Current` unchanged); practice target advance. The app-launching FlaUI gate (`EYECTRL_UI_TESTS=1`) is out of scope for new tests unless a startup-level assertion needs the real app.
- **Repo gate tests added to the plan's checklist:** `NoVisibleTestWindowGuardTests` (scans the new window's sources), `InvisibleTestWindowTests` (proves the prepared state), `StringsEnumParityTests` (schema ↔ validator divergence fails — runs in CI), and the strings validation script's German-parity gates.
- **Regression pins:** existing `SafetySetupFlow` tests keep passing (chain logic unchanged except the §9 guard, which gets its own tests); `SettingsUiCoordinator` tests updated for the new message → guide-window raise.
- **Log lines:** "First-run guide shown/completed" Information lines, **step ids + state transitions only — no localized text, no user content** (KB-E1 content-free contract, r1 F12); asserted textually (Serilog is already exercised by startup tests' patterns).

## 13. Review r1 disposition (external verifier, verdict FIX-FIRST: 6×S1, 4×S2, 2×S3)

| Finding | Verification result | Disposition |
|---------|--------------------|-------------|
| F1 (S1) backdrop/suspension model wrong | Verified: `CalibrationWindow.xaml:12` click-through; suppression via `OverlayMessageCoordinator.cs:283` (`FixationSuppressor.Calibration`), `ActivationOfferCoordinator.RecomputeSuspension`, `canInterrupt` (`App.xaml.cs:704`); synthetic calibration messages corrupt toolbar/cursor surfaces (`OverlayMessageCoordinator.cs:283-316`) | §5 rewritten: `GuideOpen` claims (new `FixationSuppressor.Guide` + `ModalInteractionState.GuideOpen` + `canInterrupt` gate + proactive-surface pins); synthetic-broadcast prohibition added |
| F2 (S1) theme chips can't re-skin live | Verified: `ThemeStartup.Apply` startup-only (`ThemeStartup.cs:30-35`); `OnSettingsChanged` re-applies nothing; overlays capture `Tokens` at construction | §7 step 6 rewritten: local preview via `ThemeRegistry.Resolve`, persist `Theme.Name`, restart-applied note; §3 non-goal added |
| F3 (S1) Retry/Accept are mouse buttons; `CompletedMessage` overlap race | Verified: `CalibrationWindow.xaml:122-127` plain `<Button>`s; `CompleteSession` emits `CalibrationCompletedMessage` on entering Active/Failed while the completion card is still up | §5 cites `CancelDwellButton` instead; §7 step 4 waits for `Idle`/`Cancelled` state transition; `CursorCircle` re-raise pinned; Q5 limitation noted |
| F4 (S1) re-run row omits synchronous close; dangling references | Verified: `CalibrationPageSource.cs:46-50` `_ctx.Close()` first; `g_safety` exists (`GeneralPageSource.cs:75`); Settings-open hides `CursorCircle` + holds toolbar auto-hide claim | §4 rewritten: replace `g_safety`, close synchronously first; §11 gains `SettingsWindow.xaml.cs` + `SettingsPageContext.cs` |
| F5 (S1) ToolbarTask unreachable floating/mouse; toolbar slides behind guide | Verified: `ToolbarLifecycleCoordinator.cs:338-339` `enabled = realTracker && IsDocked`; adapters never `RaiseToTop()` | §7 step 5 rewritten: omission condition + force-hidden entry + raise above guide |
| F6 (S1) gaze-only stranding in step 7; `StartQuestions` contract unspecified | Verified: `OverlayWindow` `WS_EX_NOACTIVATE` (:16,:175), no Esc/titlebar; `SimpleSettingsUi` exists on GeneralSettings | §6 chrome Close through step 7; `StartQuestions()` contract specified |
| F7 (S2) dwell-knob split | Verified: `GazeEngine.cs:481` vs `SettingsWindow.xaml.cs:510` | §5 + §7 step 3 + §8: practice target on `ActivationButtonDwellMs`, chrome on `ToolbarButtonDwellMs`, copy says what it tunes |
| F8 (S2) localization schema closed | Verified: `StringTableSchema.cs:15-31` closed `Surfaces` (no guide surface); German lives in `App/Localization/` + manifest | §5 + §11: schema/validator/manifest/DE files added; fit classes required |
| F9 (S2) `InFlight` race | Verified: `RequestEnable` early-returns at `SupervisionToggleService.cs:151` while an unrelated flight runs | §9 targeted chain fix + §12 tests |
| F10 (S2) `InvisibleTestWindow` constraints | Verified: `Prepare` = Opacity 0, requires AllowsTransparency + no post-show Opacity writes; `PrepareCloaked` exists | §5 window constraints + §12 |
| F11 (S3) ledger/scope polish | — | L5/L6 revised; §11 inventory completed |
| F12 (S3) paused re-run + KB-E1 hygiene | Verified: deliberate pause-doesn't-gate-toolbar; KB-E1 contract in AGENTS.md | §4/§9 pinned: guide runs while paused, indicator above; log lines step-ids only |

Open questions: Q1 (backdrop hole) — confirmed, closed by §5 claims; Q2 (re-run + in-flight) — flag semantics safe, race fixed via §9; Q3 (splash overlap) — `ExitFinished` deferral in §4; Q4 (`ActivationButtonDwellMs` is the right teach-knob) — adopted with the F7 split; Q5 (gaze-only gaps) — chrome Close fixes the trap; calibration's mouse-only Retry/Accept noted as pre-existing.
