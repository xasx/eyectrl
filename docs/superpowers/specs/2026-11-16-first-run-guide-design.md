# First-Run Introduction Guide — Design

- **Date:** 2026-11-16
- **Status:** Rev 1.2 — brainstormed with the owner interactively (flow relation, interactivity, settings picked); the remaining decisions were taken on best judgment while the owner was unreachable and are recorded as the vetoable ledger (§10). Rev 1.1: pre-review self-verification against the shipped code **replaced the interaction-pause mechanism** — calibration ships no pause; the guide uses the calibration overlay model instead (receiving backdrop + proactive-surface suspension, §5). Rev 1.2: the completion-flag latching point corrected — the reused `SafetySetupFlow` already latches `SafetySetupDone` at its last answer; the Done card is informational (§4, §7 step 8, §8).
- **Supersedes:** the startup Safety Setup's *surface* — its question chain survives verbatim inside the guide (§7).

## 1. Summary

A first-run introductory guide replaces the startup safety setup as the app's first-run experience. It presents the app's use interactively (live try-it tasks), guides through the most important settings, and offers calibration — then walks the owner through the five safety questions via the existing `SafetySetupFlow` chain. The guide is a centered card over a fullscreen receiving backdrop (the shipped calibration overlay model), driven by a headless state machine in `App/Services` — the `SafetySetupFlow` pattern: WPF-free logic, callback-driven surface, headlessly testable.

Owner picks (answered interactively):

- **One flow** — the guide absorbs the safety questions; `SafetySetupFlow` is retired as a standalone raiser.
- **Live try-it tasks** — dwell a target to continue; every task skippable.
- **Settings steps:** dwell time + theme, in addition to the absorbed safety questions.

## 2. Motivation

A new install of EyeCtrl hands a person a completely gaze-driven PC with no instruction. The current first run asks five safety questions through the Settings window's modal, but never teaches the interaction model (dwell), never helps tune the single most personal setting (dwell time), and never surfaces calibration. The guide turns the first run into a supervised hands-on introduction, while preserving the safety setup's never-strand semantics.

## 3. Non-goals

- No keyboard-typing practice task (needs real-keyboard interplay; candidate follow-up, §10 L9).
- No video/animation tutorials.
- No changes to `CalibrationWindow`, `CalibrationService`, or the calibration transform itself — the guide only launches the existing flow and reacts to its completion messages.
- No new persisted settings flag and no settings migration (§7 uses `GeneralSettings.SafetySetupDone` as-is).
- No installer (`EyeCtrl.Setup`) or Supervisor changes.
- No new KB-E1 logging (the guide is not a keyboard event surface); startup-only Information lines for shown/completed.

## 4. Trigger, flag, re-run (L1, L5)

- **Gate (unchanged semantics):** `SafetySetupStartup.ShouldPrompt(safetySetupDone, isPausedLaunch, isTestMode)` keeps its three conditions: prompts only while `GeneralSettings.SafetySetupDone` is false, never on a `--paused` launch (crash-recovery retry's inert relaunch), never in `--test-mode`. A normal crash-recovery relaunch re-prompts (same as today).
- **Raise:** `SafetySetupStartup.MaybeRequest` sends the renamed `FirstRunGuideRequestedMessage` (was `SafetySetupRequestedMessage`) from the same startup call site after `ShowOverlays`, so first-run timing relative to splash/overlays is unchanged.
- **Re-run entry:** a **Settings → General** action row "Run the introduction guide" (same action-row pattern the Calibration page uses for "Calibrate Now"). It starts the guide regardless of the flag: a **full replay** including the safety questions — answers re-apply through the same settings-write paths (idempotent). It refuses while a guide is already open (host's one-flow rule).
- **Completion flag:** `GeneralSettings.SafetySetupDone` stays the single latch, and its **latching point is unchanged**: the reused `SafetySetupFlow` sets it at the safety block's last answer (`Answered`, index == QuestionCount−1), exactly as today — the guide does not add a second write. The **Done card is informational** (Finish merely closes the guide and lifts the suspensions). "Later" on the welcome card or closing the window mid-flow leaves the flag false → the guide re-prompts at the next start; once the safety block has completed, the flag is true and a later crash does not re-prompt (all mandatory content was consumed; only the cheat-sheet was missed — accepted).

## 5. Window & surface

- **`FirstRunGuideWindow`** — `OverlayWindow` family, fullscreen topmost **receiving backdrop** (not click-through: background dwell-clicks land harmlessly on it — the shipped calibration model; the splash is click-through only because it has nothing interactive and the gaze engine is already running behind it); centered content card ≈ 560 × 440 DIP (content-sized per step, bounded), `Tokens.BrushSurfaceDark` + `Tokens.LargeCornerRadius`, styled after the splash card / calibration completion card.
- **Interaction:** the gaze pipeline stays live — the guide *teaches* dwell, so the cursor keeps moving and the in-card dwell controls work (shipped calibration proves dwell controls run with no pause: its Retry/Accept buttons). No `InteractionManager` pause: `TogglePause()` is the **user** pause (pause-indicator strip, heartbeat stamping) and is not the right tool. Instead, **proactive surfaces are suspended** while the guide is open — sticky notes, eye-care exercises, quick-actions summon, drift notices — reusing the shipped suspension seams (`NoteManager`'s calibration flag consumed from `CalibrationStateChangedMessage`; `QuickActionsSummonController.SetSummonSuspended`); on close the suspensions lift. During the calibration step the guide window hides entirely and yields the screen to `CalibrationWindow`.
- **Disarm latch:** every newly shown card holds its dwell choices disarmed until the gaze has left the card — the Settings window's `LeaveBeforeArmLatch` pattern reproduced in the guide window. This is the load-bearing anti-mis-dwell property: the flow's cards follow one another at the same place, and a gaze resting on the last answer sits on the next card's buttons.
- **String surface:** all guide text through `StringTable.Current.T(...)`, new `guide.*` keys with German translations (per `docs/release-notes/german-localization.md` conventions).

## 6. The flow state machine

`FirstRunGuideFlow` (internal sealed, `App/Services`, WPF-free): an ordered step list plus transition rules, driving the window through a narrow surface interface (`IGuideSurface`), headlessly testable with fakes — the `SafetySetupFlow` pattern.

```
Welcome → MeetDwell → DwellTime → Calibrate → ToolbarTask → Theme → Safety → Done
   ↓ Later                                     (Calibrate omitted when no tracker)
 close (flag stays false, re-prompt next start)
```

- **Events:** `Start`/`Next`/`Skip` (per-step policy), `TaskSucceeded` (practice target, toolbar summon), `CalibrationEnded(success, cancelled)`, `SafetyEnded(completed)`, `WindowClosed` (abandon).
- **Surface contract:** the flow knows only `IGuideSurface` (show step content, choices, task state); the window's code-behind implements it by updating `FirstRunGuideViewModel`, which the XAML binds. The VM never talks back to the flow — input flows window → flow events only.
- **Skip policy:** steps 2 (MeetDwell) and 5 (ToolbarTask) are skippable tasks; step 4 (Calibrate) is skippable; steps 1/3/6/7/8 are not (3 and 6 are Continue-only; 7 is answerable-through and **latches the flag** at its last answer; 8 is informational).
- **Safety block (§7):** the flow owns a `SafetySetupFlow` instance; entering the step shows the guide's own transition card, then calls the new `SafetySetupFlow.StartQuestions()`; `onEnded(true)` → advance to Done. The chain's failure-hold/checking cards render through the same card surface.
- **Abandon:** `WindowClosed` stops the safety chain (`Abandon()`), lifts the suspensions, keeps applied answers; flag untouched.
- **Re-run:** `FirstRunGuideHost` (window-side, headless like `SafetySetupHost` was) enforces one flow at a time and refuses a re-run while open.

## 7. The eight steps

### 1) Welcome
What EyeCtrl is in two sentences; "~2 minutes"; "every practice task can be skipped". Choices: **Start** / **Later**. Later closes the guide (flag stays false; re-prompts next start).

### 2) Meet dwell — live practice
Card: what the dwell ring means (progress arc, activation on completion). Content: a **big practice target** (DwellArcButton-class control bound to the live dwell setting) inside the card. Task: dwell it once. Success → auto-advance. **Skip** available — nobody gets stranded.

### 3) Dwell time — live tuning
Slider over the 200–2000 ms range, bound to `DwellSettings.ActivationButtonDwellMs` (the activation-button dwell — the guide's practice target and the app's dwell buttons follow it live), written through the same live-apply path Settings rows use. The practice target sits beside the slider so each change is felt immediately. Continue when ready.

### 4) Calibrate (omitted without a tracker)
Card: "Make sure the cursor follows your eyes." **Calibrate now** hides the guide window, sends `CalibrationRequestedMessage`, and re-shows on `CalibrationCompletedMessage`/`CalibrationCancelledMessage`, then advances. **Skip** → advance. Without a tracker (`GazeProviderType` fallback to mouse — the splash's no-tracker signal) the step is omitted from the list entirely.

### 5) Toolbar summon — live practice
Card: the docked edge band is the way to the toolbar (the keyboard, settings, everything opens from there). Task: summon the toolbar by dwelling the docked edge; success = a toolbar-shown observation on the `IToolbarVisibility.ShowToolbar` path, then the card updates and **Continue** advances. **Skip** available. The card stays centered and interactive; the band summon is coordinate-driven (`ToolbarAutoHideController`) and runs under the receiving backdrop — no interaction gating involved (L6).

### 6) Theme
Four chips (Midnight, Daylight, Ember, Contrast) bound to the Theme settings write path, applied live — the card re-skins itself as the selection changes. Continue.

### 7) Safety questions (absorbed)
Transition card ("5 quick questions to keep you safe") → `SafetySetupFlow.StartQuestions()` runs the five questions (crash recovery, start on boot, dwell cue, track status, Basic/All mode) as dwell-choice cards on the guide surface, with the chain's crash-recovery wait ("Checking crash recovery"), ceiling, and held-failure cards unchanged. The chain's last answer sets `SafetySetupDone = true` (its own latching, untouched — §4). **No skip** — each question is a Yes/No answer; the only early exit is closing the window (abandon: answers applied stay, flag stays false).

### 8) Done
"You're set." + cheat-sheet pointers: the docked edge summons the toolbar (main menu); the keyboard opens from the toolbar; every setting lives in Settings. **Finish** closes the guide and lifts the suspensions — the flag was already latched by the safety block's last answer (§4). A re-run from Settings ends here identically.

## 8. Data flow

| Step | Write |
|------|-------|
| 3 Dwell time | `DwellSettings.ActivationButtonDwellMs` via the live-apply + debounced-save path |
| 4 Calibrate | none (delegates to the existing calibration flow) |
| 6 Theme | theme selection via the Settings row's path |
| 7 Safety | the five `SafetySetupFlow` writes, unchanged — its last answer also sets `GeneralSettings.SafetySetupDone = true` (existing latching) |
| 8 Done | none (informational card) |

## 9. Edge cases

| Case | Behavior |
|------|----------|
| No tracker (mouse fallback) | Calibrate step omitted; splash already announces the fallback |
| Tracker lost mid-guide | Task steps are skippable; the dwell-time step still applies its value |
| Calibration cancelled | Advance (calibration remains available from Settings/toolbar later) |
| Crash-recovery bring-up fails mid-questions | Existing held-failure card + OK, chain machinery unchanged |
| Crash-recovery result never arrives | Existing ceiling → "could not be confirmed" failure card |
| Window closed mid-flow | Abandon: applied answers stay, flag false, re-prompt next start |
| Re-run requested while guide open | Refused (one flow at a time) |
| Gaze rests on the next card's buttons | Disarm latch: choices armed only after gaze has left the card |
| Gaze rests on the backdrop during the guide | Dwell-clicks land on the receiving backdrop — no-ops (calibration model) |
| Proactive surfaces pop mid-guide (notes, eye-care exercises, quick actions, drift notices) | Suspended while the guide is open via the shipped seams (§5); lifted on close |

## 10. Decision ledger (owner-vetoable)

Choices taken while the owner was not answering interactive prompts; every entry can be overturned at review.

- **L1 — Re-run semantics.** Re-prompt at every start until completed to the end; manual re-run entry in Settings → General (full replay). *Rejected alternative:* prompt-once-only ("Later" means never again) — a skipped guide stays unseen until found, contradicting never-strand.
- **L2 — Approach: dedicated guide window.** The owner's live-task pick rules out the Settings-modal chain (`ModalChoice` cards cannot host a practice target, slider, or theme preview). *Rejected:* hybrid (guide window + safety questions back in the Settings window) — two surfaces alternating mid-flow, no payoff over L4's reuse.
- **L3 — Step order.** Welcome → MeetDwell → DwellTime → Calibrate → ToolbarTask → Theme → Safety → Done. *Alternative considered:* calibration first (accuracy before practice) — rejected: the practice target is deliberately big/tolerant, and the interaction model is the first thing a new user needs; calibration before the toolbar task keeps that task accurate.
- **L4 — Reuse `SafetySetupFlow` for the safety block** (driven by the guide's card surface) instead of rewriting the five questions as native guide steps. *Rejected alternative:* rewrite — would re-implement the crash-recovery wait/ceiling/failure-hold machinery and its test coverage.
- **L5 — Single completion flag.** `GeneralSettings.SafetySetupDone` remains the latch (name now understates its meaning — "first-run setup completed"). *Rejected:* a new `FirstRunGuideDone` flag — always set together, so it would be a redundant migration.
- **L6 — Toolbar task without pause exceptions.** The band summon is coordinate-driven (`ToolbarAutoHideController`) and runs under the receiving backdrop; the deliberate pause-doesn't-gate-toolbar precedent (owner-exploited for paused-launch testing) corroborates that gaze-driven summons coexist with gated surfaces. Rev 1.1 removed the original premise (a guide-wide pause) entirely. The owner can also strike the task outright.
- **L7 — Re-run = full replay** including the safety questions. *Rejected alternative:* replay minus questions — needs per-block flags; YAGNI.
- **L8 — No mockups during brainstorm.** The owner was not answering; layout is specified textually (§5). Visual acceptance happens at implementation review; the design-at-physical-size rule applies to any mockups then.
- **L9 — No keyboard-typing practice task in v1.** Real-keyboard interplay (window summon, its dwell keys, pause scoping) is too heavy for v1; parked as a candidate follow-up step.

## 11. Files summary

### New

| File | Layer | Role |
|------|-------|------|
| `EyeCtrl.App/Services/FirstRunGuideFlow.cs` | App | Headless step state machine (steps, skip policy, task signals, owns the `SafetySetupFlow` instance) |
| `EyeCtrl.App/Services/FirstRunGuideHost.cs` | App | Window-side host: one flow at a time, re-run refusal, abandon on close |
| `EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml(.cs)` | App | Fullscreen receiving backdrop + centered step card; disarm latch; dwell choices, practice target, slider, theme chips |
| `EyeCtrl.ViewModels/FirstRunGuideViewModel.cs` | ViewModels | Binding surface for the current step (title, body, choices, progress, task state) |
| `tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs` | Tests | Headless flow tests |
| `tests/EyeCtrl.App.Tests/Services/FirstRunGuideHostTests.cs` | Tests | Host tests (one-flow, refusal, abandon) |
| `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs` | Tests | STA card tests (Category=UI, `InvisibleTestWindow.Prepare`) |

### Modified

| File | Change |
|------|--------|
| `EyeCtrl.App/Services/SafetySetupFlow.cs` | Add `StartQuestions()` (question chain without its own intro card); `SafetySetupStartup` message renamed to `FirstRunGuideRequestedMessage`; `SafetySetupLaunch`/`SafetySetupHost` deleted |
| `EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs` | Handle `FirstRunGuideRequestedMessage`: open `FirstRunGuideWindow`, start the flow (replaces `SafetySetupLaunch.Run`) |
| `EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs` | "Run the introduction guide" action row |
| `EyeCtrl.App/Services/AppBootstrapper.cs` | DI registrations (flow, host, window, VM) |
| `EyeCtrl.App/Services/OverlayManager.cs` (+ `IOverlayWindows`) | Window init in the overlay set |
| `EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | `FirstRunGuideRequestedMessage` (+ any step/task messages the surface interface needs) |
| `EyeCtrl.Core/Localization/strings.json` (+ German) | `guide.*` keys |

### Unchanged but load-bearing

| Piece | Why it matters |
|-------|----------------|
| `SafetySetupFlow` question chain | Absorbed verbatim (L4) — crash-recovery wait/ceiling/failure machinery and its tests survive |
| `CalibrationRequestedMessage`/`CalibrationCompletedMessage`/`CalibrationCancelledMessage` | The guide's calibration step rides them |
| Receiving-backdrop overlay model (shipped calibration) | Background dwell-clicks land harmlessly on the guide; in-card dwell controls stay live |
| `NoteManager` calibration flag / `QuickActionsSummonController.SetSummonSuspended` | The seams the guide's proactive-surface suspension reuses |

## 12. Testing guidance

- **Flow tests (headless, fakes — NFluent):** step sequence and order; Calibrate omitted without a tracker; skip policy per step (2/4/5 skippable; 3/6 Continue-only; 7 answerable-through; 8 latches); task-success auto-advance (practice target, toolbar summon); calibration ended (success *and* cancelled) advances and re-shows the card; safety block: transition card → `StartQuestions` → completed → advance; "Later" closes with flag false; the safety chain's last answer sets the flag (its own latching, unchanged); Finish merely closes; window-closed abandon (chain abandoned, suspensions lifted, flag untouched); re-run refusal while open; proactive surfaces suspended on open and lifted on close (surface-level pins on the seam calls).
- **Startup gate tests:** `ShouldPrompt`/`MaybeRequest` unchanged conditions but raising `FirstRunGuideRequestedMessage` — update the existing `SafetySetupStartup` pins.
- **Window tests (STA, Category=UI, no visible window — `InvisibleTestWindow.Prepare`):** a card renders per step VM; choices armed only after gaze leaves the card (disarm latch); dwell-time slider writes through the live-apply path; theme chips re-skin the card; practice target advance. The app-launching FlaUI gate (`EYECTRL_UI_TESTS=1`) is out of scope for new tests unless a startup-level assertion needs the real app.
- **Regression pins:** existing `SafetySetupFlow` tests keep passing (chain logic unchanged; new `StartQuestions` variant tested separately: same chain, no intro card, same end semantics). `SettingsUiCoordinator` tests updated for the new message → guide-window raise.
- **Log lines:** "First-run guide shown/completed" Information lines asserted textually (Serilog is already exercised by startup tests' patterns).
