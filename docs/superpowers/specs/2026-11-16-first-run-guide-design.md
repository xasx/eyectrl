# First-Run Introduction Guide — Design

- **Date:** 2026-11-16
- **Status:** Rev 2.1 — external review r2 processed (verdict FIX-FIRST, 4×S1 + 2×S2 + 1×S3; all 7 findings verified valid, one with a recorded amendment: r2 F3's CursorCircle raise conflicts with the shipped ring-hide convention, so the guide hides the ring on non-task steps and raises it only for the step-5 task — see §13). r1's headline correction stands: interaction model is **`GuideOpen` claims**, not a receiving backdrop. Full disposition: §13 (r1 table + r2 table).
- **Supersedes:** the startup Safety Setup's *surface* — its question chain survives verbatim inside the guide (§7 step 7), plus one targeted race fix inside that chain (§9, r1 F9).

## 1. Summary

A first-run introductory guide replaces the startup safety setup as the app's first-run experience. It presents the app's use interactively (live try-it tasks), guides through the most important settings, and offers calibration — then walks the owner through the five safety questions via the existing `SafetySetupFlow` chain. The guide is a centered card over a fullscreen **click-through** backdrop (the shipped calibration model), kept inert by explicit **`GuideOpen` claims** in the app's existing suppression seams, driven by a headless state machine in `App/Services` — the `SafetySetupFlow` pattern: WPF-free logic, callback-driven surface, headlessly testable.

Owner picks (answered interactively):

- **One flow** — the guide absorbs the safety questions; `SafetySetupFlow` is retired as a standalone raiser.
- **Live try-it tasks** — dwell a target to continue; every task skippable.
- **Settings steps:** dwell time + theme, in addition to the absorbed safety questions.

## 2. Motivation

A new install of EyeCtrl hands a person a completely gaze-driven PC with no instruction. The app's first run currently asks five safety questions through the Settings window's modal, but never teaches the interaction model (dwell), never helps tune the single most personal setting (dwell time), and never surfaces calibration. The guide turns the first run into a supervised hands-on introduction, while preserving the safety setup's never-strand semantics.

## 3. Non-goals

- No keyboard-typing practice task (needs real-keyboard interplay; candidate follow-up, §10 L9).
- No video/animation tutorials.
- No changes to `CalibrationWindow`/`CalibrationService` internals — the guide only launches the existing flow and reacts to its state messages; the completion card's mouse-only Retry/Accept stays out of scope (r1 Q5).
- No mid-session theme re-skinning of other live surfaces — theme applies at startup (r1 F2); the guide previews locally on its own card (§7 step 6, r2 F6).
- No new persisted settings flag and no settings migration (§4 uses `GeneralSettings.SafetySetupDone` as-is).
- No installer (`EyeCtrl.Setup`) or Supervisor changes.
- No new KB-E1 logging (the guide is not a keyboard event surface); startup-only Information lines, content-free per §9.

## 4. Trigger, flag, re-run (L1, L5)

- **Gate (unchanged semantics):** `SafetySetupStartup.ShouldPrompt(safetySetupDone, isPausedLaunch, testMode)` keeps its three conditions: prompts only while `GeneralSettings.SafetySetupDone` is false, never on a `--paused` launch (crash-recovery retry's inert relaunch), never in `--test-mode`. A normal crash-recovery relaunch re-prompts (same as today).
- **Raise:** `SafetySetupStartup.MaybeRequest` sends the renamed `FirstRunGuideRequestedMessage` from the same startup call site after `ShowOverlays`. **The guide window's `Show()` is deferred until the splash has exited** (r1 Q3, r2 F1): the splash runs on its own `"EyeCtrlSplash"` STA thread, `ExitFinished` fires on that thread (SplashWindow.xaml.cs:350), and `SplashThreadHandle` does not expose it to the main thread — so the guide uses a new **thread-safe, latched continuation** on the handle (e.g. `WhenExited(Action)`: marshals onto the main UI dispatcher, fires immediately when the splash is `null` — the launcher's 500 ms timeout/fault path — or already exited, attaches otherwise). `StatusCardPresenter.Start()` runs at `App.xaml.cs:424`, **before** `MaybeRequest` (:666) and ~1–2 s before splash exit; when the startup guide will raise (`ShouldPrompt` true — decidable at that point: settings are already loaded), `Start()` runs but **holds its cards (including the NoTracker card) until the guide closes**, releasing them after; on every other launch Start() is unchanged. Startup wiring order is otherwise unchanged (`supervisionToggle.RequestEnsure()` and `PauseLaunchProvenance.Apply` precede the raise).
- **Re-run entry:** the existing **`g_safety` action row in Settings → General** ("Run safety setup again", `GeneralPageSource.cs`) is **replaced** by "Run the introduction guide" (r1 F4): it follows the Calibration page's action-row pattern — `_ctx.Close()` **synchronously first**, then send `FirstRunGuideRequestedMessage`. The close matters: an open Settings window hides `CursorCircle` (`!_modal.SettingsOpen` in `OverlayVmBindingCoordinator`) and holds the `"settings"` claim on `ToolbarLifecycleCoordinator.SetAutoHideSuspended`, which would break step 5's summon task. Full replay including the safety questions (answers re-apply; idempotent); refuses while a guide is already open (host's one-flow rule).
- **Completion flag:** `GeneralSettings.SafetySetupDone` stays the single latch, and its **latching point is unchanged**: the reused `SafetySetupFlow` sets it at the safety block's last answer (`Answered`, index == QuestionCount−1), exactly as today — the guide does not add a second write. The **Done card is informational** (Finish merely closes the guide and releases the claims). "Later" or the chrome Close (§6) leaves the flag false → the guide re-prompts at the next start; once the safety block has completed, the flag is true and a later crash does not re-prompt (all mandatory content was consumed; only the cheat-sheet was missed — accepted).

## 5. Window & surface

- **`FirstRunGuideWindow`** — `OverlayWindow` family, fullscreen topmost, **`IsClickThrough = true`** (calibration parity), centered content card ≈ 560 × 440 DIP (content-sized per step, bounded), `Tokens.BrushSurfaceDark` + `Tokens.LargeCornerRadius`, styled after the splash card. `AllowsTransparency = true` with **no post-`Show` `Opacity` writes and no fade animations** (r1 F10). Not registered in `OverlayManager` — constructed per run (§6, r2 F2), so `OverlayManager.RepairSteps` does not repair it (transient window; accepted, pinned).
- **Inertness via `GuideOpen` claims (r1 F1):** while the guide is open the guide flow holds **six claims**:
  1. **`AcquireFixationSuppression(FixationSuppressor.Guide)`** — new Core enum member, acquired on guide open, released on close/abandon/finish.
  2. **`ModalInteractionState.GuideOpen`** — new member, included in `ActivationOfferCoordinator.RecomputeSuspension` (suppresses activation offers AND `NoteManager` in one claim).
  3. **`canInterrupt` gate:** the eye-care raise gains `&& !modal.GuideOpen` (`App.xaml.cs:704`).
  4. **Proactive-surface pins:** `RestartNoticeHost` and `DriftNoticeService` defer/hold while `GuideOpen`; `StatusCardPresenter` banners defer while `GuideOpen`, and during a **startup** raise the presenter additionally holds already-shown/queued cards until the guide closes (§4).
  5. **`_quickActions.SetSummonSuspended("guide", true)`** for the guide's entire lifetime (r2 F4) — without it, gaze past the hot edge pops `QuickActionsGridWindow` over the guide during any step (the claim pattern every other modal coordinator uses: `"settings"`, `"probe"`, `"exercise"`).
  6. **Toolbar hold outside step 5** (r2 F4): on steps 1–4 and 6–8 the guide holds `ToolbarLifecycleCoordinator.SetAutoHideSuspended("guide", true)` **and** sets `ToolbarViewModel.SetActive(false)` (+ force-hide the toolbar window if shown) — the toolbar starts dwellable (`ToolbarViewModel.cs:40` `_dwellingActive = true`, `DwellActiveWhilePaused => true` at :529), so without this, hidden-under-guide buttons (Keyboard, Settings, Quit, Pause) dwell-fire invisibly. **Both lifted exactly during step 5** (the summon task needs the band live), re-applied after the task.
- **Z-order (r2 F3, amended):** on **every** guide show/re-show, a **visible `PauseIndicator` is raised above the guide** (it is the pause cue and its resume disc must stay dwellable — on every step, including when step 5 is omitted). `CursorCircle` follows the shipped **ring-hide convention**: the ring hides whenever a fullscreen surface is up (`CursorCircleWindow.xaml.cs:40-42`) and when paused (`CursorCircleViewModel.cs:272-275`) — so the guide hides the ring on steps 1–4 and 6–8 (the card's own dwell arcs are the gaze feedback on-card), and **raises the ring above guide and toolbar only during step 5** (the edge task needs gaze feedback outside the card), re-hiding it after. The r2 reviewer's blanket "raise CursorCircle above the guide" was amended to this convention-following rule (§13).
- **Dwell knobs (r1 F7):** the **practice target binds live `DwellSettings.ActivationButtonDwellMs`** (desktop click activation, `GazeEngine.cs:481` — the concept step 3 teaches); guide chrome buttons bind `ToolbarButtonDwellMs` (`SettingsWindow.xaml.cs:510`), protected by the disarm latch.
- **Gaze feed contract (r2 F5):** the guide consumes `GazeCursorMovedMessage`, gating on `!IsInGap` and **not** on `IsPaused` — gaze samples reach overlay VMs while paused (shipped proof: `ToolbarViewModel.DwellActiveWhilePaused => true`, `ToolbarViewModel.cs:529`). The guide's VM **overrides `DwellActiveWhilePaused` to `true`** (the `DwellOverlayViewModelBase` default is `false`) and owns **separate dwell sources for the two knobs** — a dedicated practice-target detector on `ActivationButtonDwellMs` and chrome detection on `ToolbarButtonDwellMs` (per-target dwell times, the `SettingsWindow` pattern) — because the base class owns a single detector. While paused, `CursorCircle` stays hidden (§5 z-order) — `PauseIndicator` is the pause cue, matching Settings/toolbar.
- **Disarm latch:** every newly shown card holds its dwell choices disarmed until gaze has left the card — `LeaveBeforeArmLatch` reused or its pattern reproduced (plan decides after wiring the guide's detector).
- **String surface:** all guide text through `StringTable.Current.T(...)` under a **new closed `guide` surface** (r1 F8): `StringTableSchema.Surfaces` gains `guide` **in sorted position** (r2 F7 — `strings_validate.py:469` enforces alphabetical order on both the schema list and `strings.de.manifest.json`), with matching updates in `tools/strings_validate.py` (+ test), `src/EyeCtrl.App/Localization/strings.de.json` + `strings.de.manifest.json` (`src` hashes via `--refresh-de-src`), and a fit class per `guide.*` key.

## 6. The flow state machine

`FirstRunGuideFlow` (internal sealed, `App/Services`, WPF-free): an ordered step list plus transition rules, driving the window through a narrow surface interface (`IGuideSurface`), headlessly testable with fakes — the `SafetySetupFlow` pattern.

```
Welcome → MeetDwell → DwellTime → Calibrate → ToolbarTask → Theme → Safety → Done
   ↓ Later / Close                                  (Calibrate and ToolbarTask omitted per §7)
 close (flag stays false unless the safety block completed, re-prompt next start)
```

- **Events:** `Start`/`Next`/`Skip`/`Close` (per-step policy), `TaskSucceeded` (practice target, toolbar summon), `CalibrationIdle`/`CalibrationCancelled` (step 4's advance triggers), `SafetyEnded(completed)`, `WindowClosed` (abandon).
- **Lifecycle (r2 F2):** the window is **constructed per run** by `SettingsUiCoordinator` (the `SettingsWindow` precedent — a closed WPF `Window` throws on re-`Show()`, while `OverlayManager` windows are app-lifetime singletons that only `Hide()`; zero `.Close()` calls exist under `src/EyeCtrl.App/Overlays/`). Per-run construction keeps `Close()`/`WindowClosed` semantics honest and resets card state cleanly on re-runs. §11 drops the `OverlayManager`/`IOverlayWindows` rows.
- **Surface contract:** the flow knows only `IGuideSurface` (show step content, choices, task state); the window's code-behind implements it by updating `FirstRunGuideViewModel`, which the XAML binds. The VM never talks back to the flow — input flows window → flow events only.
- **Chrome Close (r1 F6):** every card except Welcome ("Later") and Done ("Finish") carries a persistent, gaze-dwellable **Close** in the card chrome — present **through step 7 as well** (`OverlayWindow` is `WindowStyle=None` + `WS_EX_NOACTIVATE`, no gaze-reachable Esc). Close calls `Abandon()` and closes without latching the flag.
- **Skip policy:** steps 2 (MeetDwell) and 5 (ToolbarTask) are skippable tasks; step 4 (Calibrate) is skippable; steps 1/3/6/7/8 are not (3 and 6 are Continue-only; 7 is answerable-through and **latches the flag** at its last answer; 8 is informational).
- **Safety block (§7 step 7):** the flow owns a `SafetySetupFlow` instance. **`StartQuestions()` contract (r1 F6):** `_active = true; _pendingFailure = null; Advance(0);` (Start() minus the intro card), and the guide supplies `setSimpleSettingsUi` as `v => { settings.General.SimpleSettingsUi = v; applyAndSave(); }`. `onEnded(true)` → advance to Done; the chain's failure-hold/checking cards render through the same card surface, chrome Close available throughout.
- **Claims lifecycle:** open → all six claims acquired (§5); step 5 toggles the toolbar hold (claim 6) off for the task's duration; step 4's calibration phase keeps the guide's claims held (the calibration flow acquires its own `FixationSuppressor.Calibration` on top). Close/abandon/finish → all claims released, in acquisition order; **the step-5 toolbar hold is re-acquired before release** so no window exists where the toolbar dwells unheld.
- **Abandon:** `WindowClosed`/chrome Close stops the safety chain (`Abandon()`), releases the claims, keeps applied answers; flag untouched.
- **Re-run:** `FirstRunGuideHost` enforces one flow at a time and refuses a re-run while open.

## 7. The eight steps

### 1) Welcome
What EyeCtrl is in two sentences; "~2 minutes"; "every practice task can be skipped". Choices: **Start** / **Later**. Later closes the guide (flag stays false; re-prompts next start).

### 2) Meet dwell — live practice
Card: what the dwell ring means (progress arc, activation on completion). Content: a **big practice target** (in-card gaze-tracked control on live `ActivationButtonDwellMs`) inside the card. Task: dwell it once. Success → auto-advance. **Skip** available — nobody gets stranded.

### 3) Dwell time — live tuning
Slider over the 200–2000 ms range bound to `DwellSettings.ActivationButtonDwellMs`, written through the same live-apply path Settings rows use; the practice target beside it re-renders its arc timing live. **Copy says what it tunes: the desktop dwell-click** (`GazeEngine.cs:481`); a note that the guide's own buttons keep the standard UI dwell (`ToolbarButtonDwellMs`). Continue when ready.

### 4) Calibrate (omitted without a real tracker)
Card: "Make sure the cursor follows your eyes." **Calibrate now** hides the guide window, sends `CalibrationRequestedMessage`, and **waits for `CalibrationStateChangedMessage` to return to `Idle` (or `Cancelled`)** before re-showing and advancing (r1 F3) — `CalibrationCompletedMessage` fires the moment the result state is entered while `CalibrationWindow` still shows its completion card (good → ~3 s auto-dismiss; poor → Retry/Accept, and **Retry loops back to `Collecting`**). On re-show the guide re-asserts its z-order rules (§5: PauseIndicator above, ring re-hidden by convention — the coordinator's re-show path calls `Show()`, not `RaiseToTop()`, `OverlayMessageCoordinator.cs:308/318`, which leaves the ring under the guide exactly as the convention wants). **Skip** → advance. Without a real tracker (`App.ActiveProviderType == GazeProviderType.Mouse`) the step is omitted. Pre-existing limitation, out of scope: calibration's completion `Retry`/`Accept` are mouse-only `<Button>`s (r1 Q5).

### 5) Toolbar summon — live practice (omitted when floating or mouse-only)
Card: the docked edge band is the way to the toolbar. Task: summon the toolbar by dwelling the docked edge; success = a toolbar-shown observation on the `IToolbarVisibility.ShowToolbar` path; the card updates and **Continue** advances. **Skip** available. **Omitted when `!settings.Toolbar.IsDocked || App.ActiveProviderType == GazeProviderType.Mouse`** (r1 F5 — `ToolbarLifecycleCoordinator.cs:338-339`). On entry: the toolbar hold (§5 claim 6) is **lifted** (auto-hide suspension released, `SetActive(true)`), the toolbar window force-hidden, and on the summon **`ToolbarWindow` and `CursorCircle` are raised above the guide** (adapters never `RaiseToTop()`; the guide is created later in `ZBID_UIACCESS`), ring topmost. On exit (Continue/Skip): the holds re-asserted, the ring re-hidden. The band summon itself is coordinate-driven, no gating change needed.

### 6) Theme (local preview + restart-applied persist)
Four chips (Midnight, Daylight, Ember, Contrast) (r1 F2, r2 F6): selection **previews locally** and **persists `settings.Theme.Name`**. The preview **must not** call `ThemeStartup.Apply`/mutate `Tokens.Current`, and it **cannot ride `{StaticResource Brush*}` or static `Tokens.*` reads**: overlay XAML and `DwellButton`/`DwellSlider` snapshot those at construction (`ThemeResourceApplier.cs:8-9`, `DwellButton.cs:168-214`, `DwellSlider.cs:30`), so the card applies the resolved `ITheme`'s brushes **explicitly to card-local elements** (or card-scoped `{DynamicResource}` that the previewed elements do not read through static `Tokens.*`) — startup-theme elements outside the preview area keep startup brushes by design. The card carries the `settings.general.theme.description` note (takes effect after restart). Continue.

### 7) Safety questions (absorbed)
Transition card → `SafetySetupFlow.StartQuestions()` (§6 contract) runs the five questions (crash recovery, start on boot, dwell cue, track status, Basic/All mode) as dwell-choice cards, with the chain's crash-recovery wait ("Checking crash recovery"), ceiling, and held-failure cards unchanged, chrome Close available throughout. The chain's last answer sets `SafetySetupDone = true` (its own latching, untouched — §4). **No skip** — the exits are the chrome Close (abandon) or answering through.

### 8) Done
"You're set." + cheat-sheet pointers: the docked edge summons the toolbar (main menu); the keyboard opens from the toolbar; every setting lives in Settings. **Finish** closes the guide and releases the claims — the flag was already latched by the safety block's last answer (§4). A re-run from Settings ends here identically.

## 8. Data flow

| Step | Write |
|------|-------|
| 3 Dwell time | `DwellSettings.ActivationButtonDwellMs` (desktop dwell-click knob) via the live-apply + debounced-save path; guide chrome stays on `ToolbarButtonDwellMs` |
| 4 Calibrate | none (delegates to the existing calibration flow) |
| 6 Theme | `settings.Theme.Name` persisted (startup-applied; guide previews locally only) |
| 7 Safety | the five `SafetySetupFlow` writes, unchanged — its last answer also sets `GeneralSettings.SafetySetupDone = true` (existing latching) |
| 8 Done | none (informational card) |

## 9. Edge cases

| Case | Behavior |
|------|----------|
| No real tracker (mouse fallback) | Calibrate **and** ToolbarTask steps omitted (§7 steps 4/5); splash already announces the fallback |
| Floating toolbar | ToolbarTask omitted (docked-only gating, r1 F5) |
| Tracker lost mid-guide | Task steps are skippable; the dwell-time step still applies its value |
| Calibration retry loop | Guide stays hidden through `Collecting` re-entry; advances only on the `Idle`/`Cancelled` transition (§7 step 4) |
| Crash-recovery bring-up fails mid-questions | Existing held-failure card + OK, chain machinery unchanged |
| Crash-recovery result never arrives | Existing ceiling → "could not be confirmed" failure card |
| Unrelated supervision flight in progress when crash recovery is answered "Yes" | **Targeted chain fix (r1 F9):** `RequestCrashRecovery` no longer attaches to an unrelated flight — already-enabled "Yes" is a no-op success; an in-flight disable/ensure is not mistaken for the enable's result (mechanism pinned at plan level; early return at `SupervisionToggleService.cs:151`) |
| Window closed mid-flow | Abandon: applied answers stay, flag false unless the safety block completed, re-prompt next start |
| Re-run requested while guide open | Refused (one flow at a time) |
| Re-run triggered while user-paused | The guide runs (r1 F12, r2 F5): in-card dwell controls are gaze-tracked and work under user-pause (`DwellActiveWhilePaused => true` override); the summon task works (pause deliberately doesn't gate toolbar dwell); **`CursorCircle` stays hidden while paused** (matching Settings/toolbar) — `PauseIndicator` is the pause cue, raised above the guide (§5) |
| Quick Actions hot edge during the guide | Summon suspended via the `"guide"` claim for the guide's whole lifetime (§5 claim 5, r2 F4) |
| Docked toolbar during steps 1–4 / 6–8 | Auto-hide suspended + toolbar dwell deactivated + toolbar hidden; both live only during step 5 (§5 claim 6, r2 F4) |
| Gaze rests on the next card's buttons | Disarm latch: choices armed only after gaze has left the card |
| Status cards / restart notices / drift notices / eye-care breaks | Deferred while `GuideOpen`; startup `StatusCardPresenter` cards held until the guide closes (§4, r2 F1) |
| Splash still exiting when the guide raises | `Show()` deferred via the latched `SplashThreadHandle.WhenExited` continuation — immediate when splash is `null` or already exited (§4, r2 F1) |
| KB-E1 / logging hygiene (r1 F12) | Guide log lines carry **step ids and state transitions only** — no localized text, no user content |

## 10. Decision ledger (owner-vetoable)

Choices taken while the owner was not answering interactive prompts; every entry can be overturned at review. r-revised entries marked.

- **L1 — Re-run semantics.** Re-prompt at every start until completed to the end; manual re-run entry replacing `g_safety` (full replay). *Rejected:* prompt-once-only — a skipped guide stays unseen until found, contradicting never-strand.
- **L2 — Approach: dedicated guide window, per-run construction.** Live tasks rule out the Settings-modal chain; per-run construction (r2 F2) keeps `Close()`/`WindowClosed` honest and re-run state clean. *Rejected:* OverlayManager singleton — singleton overlays only `Hide()`, and a closed WPF `Window` cannot re-`Show()`.
- **L3 — Step order.** Welcome → MeetDwell → DwellTime → Calibrate → ToolbarTask → Theme → Safety → Done, with steps 4/5 conditionally omitted. *Alternative considered:* calibration first — rejected: the practice target is deliberately big/tolerant; the interaction model is the first thing a new user needs.
- **L4 — Reuse `SafetySetupFlow` for the safety block**, driven by the guide's card surface, plus the targeted §9 race fix. *Rejected:* rewrite — re-implements the crash-recovery wait/ceiling/failure machinery and its tests.
- **L5 — Single completion flag.** `GeneralSettings.SafetySetupDone` remains the latch. *Rejected:* a new `FirstRunGuideDone` flag — redundant migration.
- **L6 — Toolbar task, gated and held.** Runs only when it can (docked + real tracker); the toolbar hold toggles per step (§5 claim 6); the summoned toolbar and ring are raised above the guide during the task; the owner can strike the task outright. *(r2-revised.)*
- **L7 — Re-run = full replay** including the safety questions. Sound across re-runs: the chain only ever sets the flag (r1 Q2); the unrelated-flight race is fixed (§9).
- **L8 — No mockups during brainstorm.** Layout specified textually (§5); visual acceptance at implementation review; design-at-physical-size applies then.
- **L9 — No keyboard-typing practice task in v1.** Real-keyboard interplay too heavy for v1; parked as a follow-up.

## 11. Files summary

### New

| File | Layer | Role |
|------|-------|------|
| `EyeCtrl.App/Services/FirstRunGuideFlow.cs` | App | Headless step state machine (steps, skip policy, task signals, owns the `SafetySetupFlow` instance, claims lifecycle) |
| `EyeCtrl.App/Services/FirstRunGuideHost.cs` | App | Host: one flow at a time, re-run refusal, abandon on close |
| `EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml(.cs)` | App | Fullscreen click-through backdrop + centered step card (`AllowsTransparency`, no post-show Opacity writes); chrome Close; disarm latch; dual dwell sources; explicit-brush theme preview |
| `EyeCtrl.ViewModels/FirstRunGuideViewModel.cs` | ViewModels | Binding surface for the current step; overrides `DwellActiveWhilePaused => true`; consumes `GazeCursorMovedMessage` (`!IsInGap`, not gated on `IsPaused`) |
| `tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs` | Tests | Headless flow tests |
| `tests/EyeCtrl.App.Tests/Services/FirstRunGuideHostTests.cs` | Tests | Host tests |
| `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs` | Tests | STA card tests (Category=UI, `InvisibleTestWindow.Prepare`) |

### Modified

| File | Change |
|------|--------|
| `EyeCtrl.App/Services/SafetySetupFlow.cs` | Add `StartQuestions()` (§6 contract); `RequestCrashRecovery` unrelated-flight guard (§9); `SafetySetupStartup` message renamed to `FirstRunGuideRequestedMessage`; `SafetySetupLaunch`/`SafetySetupHost` deleted |
| `EyeCtrl.App/Views/SettingsWindow.xaml.cs` | `SafetySetupHost` field + wiring removed |
| `EyeCtrl.App/Views/Settings/SettingsPageContext.cs` | `RunSafetySetup` removed/replaced by the guide raise |
| `EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs` | `g_safety` replaced by "Run the introduction guide" (`_ctx.Close()` then send) |
| `EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs` | Handle `FirstRunGuideRequestedMessage`: **construct `FirstRunGuideWindow` per run** + deferred show (§4/§6), start the flow |
| `EyeCtrl.App/Overlays/SplashThreadLauncher.cs` | `SplashThreadHandle` gains the latched `WhenExited(Action)` main-dispatcher continuation (r2 F1) |
| `EyeCtrl.App/Services/StatusCardPresenter.cs` | Hold cards while a startup guide launch is pending/closed-open (§4, r2 F1) |
| `EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs` | `ModalInteractionState.GuideOpen` in `RecomputeSuspension` (r1 F1) |
| `EyeCtrl.ViewModels/...` (`ModalInteractionState.cs` home) | `GuideOpen` member added |
| `EyeCtrl.Core/FixationSuppressor.cs` | `Guide` member added |
| `EyeCtrl.App/App.xaml.cs` | `canInterrupt` gate gains `&& !modal.GuideOpen`; status-card hold hook; raise ordering |
| `EyeCtrl.App/Services/RestartNoticeHost` / `DriftNoticeService.cs` | Defer while `GuideOpen` (§5 claim 4) |
| `EyeCtrl.App/Services/Coordinators/QuickActionsCoordinator.cs` | No seam change — the guide acquires `SetSummonSuspended("guide", …)` (listed as the consumed seam) |
| `EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs` | Guide's `SetAutoHideSuspended("guide", …)` hold + step-5 toggle (§5 claim 6) |
| `EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (or `ToolbarVisibilityAdapter.cs`) | `RaiseToTop()` for the summoned toolbar above the guide during step 5 |
| `EyeCtrl.App/Services/AppBootstrapper.cs` | DI registrations (flow, host, VM; window built per run) |
| `EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | `FirstRunGuideRequestedMessage` (+ step/task messages) |
| `EyeCtrl.Core/Localization/StringTableSchema.cs` | `guide` surface added **in sorted position** (r2 F7) |
| `tools/strings_validate.py` (+ test) | Validate the new surface; refresh German `src` hashes |
| `EyeCtrl.Core/Localization/strings.json` | `guide.*` keys with fit classes |
| `EyeCtrl.App/Localization/strings.de.json` (+ `strings.de.manifest.json`) | German `guide.*` + manifest (sorted surfaces) |
| `EyeCtrl.ViewModels/LeaveBeforeArmLatch.cs` | Reused unchanged (or pattern reproduced — plan decides) |

### Unchanged but load-bearing

| Piece | Why it matters |
|-------|----------------|
| `SafetySetupFlow` question chain | Absorbed verbatim minus the §9 race fix |
| `CalibrationRequestedMessage`/`CalibrationStateChangedMessage` | Step 4 rides them; advance keys on the `Idle`/`Cancelled` transition |
| `ThemeRegistry.Resolve` / `ThemeStartup.Apply` | Step 6 previews via the former; never calls the latter mid-session |
| `IToolbarVisibility.ShowToolbar` path | Step 5's success observation |
| Dwell-knob split: `ActivationButtonDwellMs` vs `ToolbarButtonDwellMs` | Steps 2/3 teach the former; chrome uses the latter |
| Ring-hide convention (`CursorCircleWindow` doc; paused hides the ring) | §5 z-order rule; step 5 is the sole raise window |

## 12. Testing guidance

- **Flow tests (headless, fakes — NFluent, xUnit v3, private nested fakes):** step sequence and order; Calibrate/ToolbarTask omitted per conditions; skip policy; task-success auto-advance; calibration advance keys on `Idle`/`Cancelled` only (`Collecting` re-entry after Retry does not advance; `CompletedMessage` alone does not re-show); safety block: transition card → `StartQuestions` → completed → advance; `StartQuestions` contract (no intro card, question 0); chrome Close during step 7 abandons without latching; "Later" closes with flag false; chain's last answer sets the flag; window-closed abandon (chain abandoned, claims released); re-run refusal; claims acquired on open / released on close in acquisition order with the step-5 toolbar hold re-acquired before release; the §9 unrelated-flight guard.
- **Splash/status tests (r2 F1):** `WhenExited` fires immediately when splash is `null` or already exited, marshals to the main dispatcher otherwise (latched); `StatusCardPresenter.Start()` holds cards while a startup guide is pending and releases on guide close; normal launches unchanged.
- **Window tests (STA, Category=UI, no visible window — `InvisibleTestWindow.Prepare`):** card renders per step VM; disarm latch; chrome Close on steps 2–7; slider writes `ActivationButtonDwellMs` while chrome stays on `ToolbarButtonDwellMs` (dual dwell sources); `DwellActiveWhilePaused => true` override consumed (gaze gated on `!IsInGap`, not `IsPaused`); theme chips apply resolved brushes **explicitly to card-local elements** with `Tokens.Current` unchanged and startup-theme elements untouched; z-order pins (PauseIndicator above the guide on show; ring hidden on non-task steps, raised above guide+toolbar on step 5, re-hidden after); toolbar hold assertions (auto-hide suspended + `SetActive(false)` on steps 1–4/6–8; lifted during step 5); quick-actions summon suspended for the guide's lifetime.
- **Repo gate tests:** `NoVisibleTestWindowGuardTests`, `InvisibleTestWindowTests`, `StringsEnumParityTests` (schema ↔ validator parity; `guide` inserted in sorted order), and the German-parity gates.
- **Wiring tests (r2 F7):** `SafetySetupWiringTests.cs` updated for the new seams (`FirstRunGuideRequestedMessage`, per-run construction) — it source-scans `SettingsUiCoordinator.cs`/`SettingsWindow.xaml.cs` for the deleted `SafetySetupLaunch.Run`/`SafetySetupHost`/`RunSafetySetup` strings.
- **Regression pins:** existing `SafetySetupFlow` tests keep passing (chain unchanged except the §9 guard, which gets its own tests); `SettingsUiCoordinator` tests updated for the new message → per-run guide raise.
- **Log lines:** "First-run guide shown/completed" Information lines, **step ids + state transitions only** (KB-E1 content-free contract); asserted textually.

## 13. Review dispositions

### r1 (verdict FIX-FIRST: 6×S1, 4×S2, 2×S3 — all 12 valid, all fixed in rev 2.0)

| Finding | Verification result | Disposition |
|---------|--------------------|-------------|
| F1 (S1) backdrop/suspension model wrong | Verified: `CalibrationWindow.xaml:12` click-through; suppression via `OverlayMessageCoordinator.cs:283` + `ActivationOfferCoordinator` + `canInterrupt` (`App.xaml.cs:704`) | §5 rewritten: `GuideOpen` claims; synthetic-broadcast prohibition |
| F2 (S1) theme can't re-skin live | Verified: `ThemeStartup.Apply` startup-only; overlays capture `Tokens` at construction | §7 step 6: local preview, persist, restart note |
| F3 (S1) Retry/Accept mouse-only; `CompletedMessage` overlap | Verified: `CalibrationWindow.xaml:122-127`; `CompleteSession` emits on entering Active/Failed | Advance on `Idle`/`Cancelled`; `CancelDwellButton` cited; Q5 noted |
| F4 (S1) re-run row gaps | Verified: `_ctx.Close()` first (`CalibrationPageSource.cs:46-50`); `g_safety` exists; Settings-open hides ring + holds toolbar claim | `g_safety` replaced, close first; files added |
| F5 (S1) ToolbarTask unreachable/z-order | Verified: `ToolbarLifecycleCoordinator.cs:338-339` | Omission condition + raise |
| F6 (S1) step-7 stranding | Verified: `WS_EX_NOACTIVATE` (OverlayWindow.cs:16,:175); `SimpleSettingsUi` exists | Chrome Close through step 7; `StartQuestions` contract |
| F7 (S2) dwell-knob split | Verified: `GazeEngine.cs:481` vs `SettingsWindow.xaml.cs:510` | Split adopted (§5/§7/§8) |
| F8 (S2) closed string schema | Verified: `StringTableSchema.cs:15-31`; German in `App/Localization/` + manifest | Schema/validator/manifest/DE files added |
| F9 (S2) `InFlight` race | Verified: early return `SupervisionToggleService.cs:151` | §9 targeted guard + tests |
| F10 (S2) test-fixture constraints | Verified: `Prepare` = Opacity 0 + AllowsTransparency + no post-show Opacity writes | §5 constraints + §12 |
| F11 (S3) ledger/scope polish | — | L5/L6 revised; inventory completed |
| F12 (S3) paused re-run + KB-E1 | Verified: deliberate ungating; KB-E1 contract | §4/§9 pinned |

Open questions: Q1 closed by §5 claims; Q2 — flag semantics safe, §9 guard; Q3 — `ExitFinished` deferral; Q4 — teach-knob adopted with the F7 split; Q5 — chrome Close closes the trap; calibration's mouse-only Retry/Accept pre-existing.

### r2 (verdict FIX-FIRST: 4×S1, 2×S2, 1×S3 — all 7 verified valid; one amended)

| Finding | Verification result | Disposition |
|---------|--------------------|-------------|
| F1 (S1) `ExitFinished`/`StatusCardPresenter` timing/thread gap | Verified: `ExitFinished` fires on the splash STA thread (`SplashWindow.xaml.cs:350`); `SplashThreadHandle` (`SplashThreadLauncher.cs:56`) exposes nothing; launcher can return `null` on timeout (`SplashThreadLauncher.cs:182-184`); `StatusCardPresenter.Start()` at `App.xaml.cs:424` precedes `MaybeRequest` (:666) | §4: latched `SplashThreadHandle.WhenExited` continuation (immediate on `null`/already-exited); `StatusCardPresenter.Start()` holds cards while a startup guide is pending; §11 gains `SplashThreadLauncher.cs` + `StatusCardPresenter.cs` |
| F2 (S1) OverlayManager singleton vs `Close()`/`WindowClosed` | Verified: zero `.Close()` under `src/EyeCtrl.App/Overlays/`; `OverlayManager.Initialize` builds app-lifetime singletons | §6: **per-run construction** in `SettingsUiCoordinator` (SettingsWindow precedent); §11 drops `OverlayManager`/`IOverlayWindows` rows |
| F3 (S1) `CursorCircle`/`PauseIndicator` z-order | Verified: all overlays in `ZBID_UIACCESS`; coordinator re-show uses `Show()` not `RaiseToTop()` (:308/318) — **but** the blanket "raise CursorCircle" conflicts with the shipped ring-hide convention (`CursorCircleWindow.xaml.cs:40-42`: ring hides whenever a fullscreen surface is up; `CursorCircleViewModel.cs:272-275`: Paused hides the ring) | §5 amended: `PauseIndicator` raised above the guide on every show; ring **hidden** on non-task steps per convention, raised above guide+toolbar **only during step 5**, re-hidden after. Amendment recorded for r3 judgment |
| F4 (S1) QuickActions summon + non-step-5 toolbar dwell | Verified: `SetSummonSuspended` claim pattern (`QuickActionsCoordinator.cs:42`, callers `"settings"`/`"probe"`/`"exercise"`); `ToolbarViewModel.cs:40` `_dwellingActive = true`, `:529` `DwellActiveWhilePaused => true` | §5 claims 5+6: `"guide"` summon-suspension claim for the whole lifetime; toolbar hold (auto-hide suspension + `SetActive(false)` + hide) on steps 1–4/6–8, lifted only during step 5 |
| F5 (S2) gaze feed contract + paused behavior | Verified: `DwellOverlayViewModelBase` owns a single detector (:31) and a `DwellActiveWhilePaused` default the toolbar overrides to `true` (:527-529); paused ring hide confirmed (`CursorCircleViewModel.cs:272-275`) | §5 gaze-feed contract (`!IsInGap`, not `IsPaused`; `DwellActiveWhilePaused => true` override; separate dwell sources); §9 paused row completed |
| F6 (S2) theme preview can't ride StaticResource/Tokens | Verified: consistent with r1 F2's construction-time capture (`ThemeResourceApplier.cs:8-9`, `DwellButton.cs:168-214`, `DwellSlider.cs:30`) | §7 step 6: explicit brush application to card-local elements (or card-scoped DynamicResource) |
| F7 (S3) wiring tests + Surfaces sort | Verified: `SafetySetupWiringTests.cs` source-scans the deleted symbols (:40,:51,:53,:63,:66); `strings_validate.py:469` enforces sorted surfaces | §12 gains `SafetySetupWiringTests` update; §5/§11 note the sorted-insert invariant |

Open questions: Q1/Q3 — closed by §5 claims 5–6 and §4's `WhenExited` + status-card hold; Q2/Q4/Q5 — rev 2.0's approach affirmed.
