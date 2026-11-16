# First-Run Introduction Guide — Design

- **Date:** 2026-11-16
- **Status:** Rev 2.3 — external review r4 processed (verdict FIX-FIRST, 2×S1 + 3×S2 + 1×S3; all 6 findings verified valid). r4's corrections: **step 5's summoned toolbar must not be a live control surface** — non-`Pause` toolbar button activations gated and keyboard/settings sample feeds gain a `GuideOpen` check (r4 F1); the summon needs **`ResetToHidden()`** on `ToolbarAutoHideController` so summon detection is live immediately instead of waiting the 5 s hide delay (r4 F2); `SetupNoticeWatch` must be **gated on `!IsGuideOpen`**, not deferred after `_show` — `CheckStartup` writes `setup-notice-shown.json` *before* showing (r4 F3); `DriftNoticeService`'s onset drop is the sensitive-context gate, not modals — removed from scope (r4 F4); toolbar restore is unconditional on every close (r4 F5); path/line fixes (r4 F6). Dispositions: §13 (r1–r4 tables).
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
- No mid-session theme re-skinning of other live surfaces — theme applies at startup (r1 F2); the guide previews locally on its own card (§7 step 6).
- No new persisted settings flag and no settings migration (§4 uses `GeneralSettings.SafetySetupDone` as-is).
- No installer (`EyeCtrl.Setup`) or Supervisor changes.
- No new KB-E1 logging (the guide is not a keyboard event surface); startup-only Information lines, content-free per §9.

## 4. Trigger, flag, re-run (L1, L5)

- **Gate (unchanged semantics):** `SafetySetupStartup.ShouldPrompt(safetySetupDone, isPausedLaunch, isTestMode)` keeps its three conditions: prompts only while `GeneralSettings.SafetySetupDone` is false, never on a `--paused` launch (crash-recovery retry's inert relaunch), never in `--test-mode`. A normal crash-recovery relaunch re-prompts (same as today).
- **Raise:** `SafetySetupStartup.MaybeRequest` sends the renamed `FirstRunGuideRequestedMessage` from the same startup call site after `ShowOverlays`. **The guide window's `Show()` is deferred until the splash has exited** (r1 Q3, r2 F1): the splash runs on its own `"EyeCtrlSplash"` STA thread, `ExitFinished` fires on that thread, and `SplashThreadHandle` does not expose it to the main thread — so the handle gains a **thread-safe, latched continuation** (`WhenExited(Action)`: marshals onto the main UI dispatcher, fires immediately when the splash is `null` — the launcher's 500 ms timeout/fault path — or already exited, attaches otherwise). **Claims are acquired when the guide window actually shows — inside `WhenExited` — never before splash exit** (r3 F6), so the guide never suppresses fixation/offers for 1–2 s invisibly. `StatusCardPresenter.Start()` runs at `App.xaml.cs:424`, before `MaybeRequest` (:666) and ~1–2 s before splash exit; when the startup guide will raise (`ShouldPrompt` true — decidable at that point), `Start()` runs but **holds its cards (including the NoTracker card) until the guide closes** — with a **fallback release if guide startup fails** (r3 F6) so the hold can never wedge; on every other launch `Start()` is unchanged. Startup wiring order is otherwise unchanged (`supervisionToggle.RequestEnsure()` and `PauseLaunchProvenance.Apply` precede the raise).
- **Re-run entry:** the existing **`g_safety` action row in Settings → General** ("Run safety setup again", `GeneralPageSource.cs`) is **replaced** by "Run the introduction guide" (r1 F4): it follows the Calibration page's action-row pattern — `_ctx.Close()` **synchronously first**, then send `FirstRunGuideRequestedMessage`. The close matters: an open Settings window hides `CursorCircle` (`!_modal.SettingsOpen` in `OverlayVmBindingCoordinator`) and holds the `"settings"` claim on `ToolbarLifecycleCoordinator.SetAutoHideSuspended`, which would break step 5's summon task. Full replay including the safety questions (answers re-apply; idempotent); refuses while a guide is already open (host's one-flow rule).
- **Completion flag:** `GeneralSettings.SafetySetupDone` stays the single latch, and its **latching point is unchanged**: the reused `SafetySetupFlow` sets it at the safety block's last answer (`Answered`, index == QuestionCount−1), exactly as today — the guide does not add a second write. The **Done card is informational** (Finish merely closes the guide and releases the claims). "Later" or the chrome Close (§6) leaves the flag false → the guide re-prompts at the next start; once the safety block has completed, the flag is true and a later crash does not re-prompt (all mandatory content was consumed; only the cheat-sheet was missed — accepted).

## 5. Window & surface

- **`FirstRunGuideWindow`** — `OverlayWindow` family, fullscreen topmost, **`IsClickThrough = true`** (calibration parity), centered content card ≈ 560 × 440 DIP (content-sized per step, bounded), `Tokens.BrushSurfaceDark` + `Tokens.LargeCornerRadius`, styled after the splash card. `AllowsTransparency = true` with **no post-`Show` `Opacity` writes and no fade animations** (r1 F10). Not registered in `OverlayManager` — constructed per run (§6, r2 F2), so `OverlayManager.RepairSteps` does not repair it (transient window; accepted, pinned).
- **Inertness via `GuideOpen` claims (r1 F1):** while the guide is open the guide flow holds **seven claims**:
  1. **`AcquireFixationSuppression(FixationSuppressor.Guide)`** — new Core enum member, acquired when the window shows (inside `WhenExited`), released on close/abandon/finish.
  2. **`ModalInteractionState.GuideOpen`** — new member, included in `ActivationOfferCoordinator.RecomputeSuspension` (suppresses activation offers AND `NoteManager` in one claim).
  3. **`canInterrupt` gate:** the eye-care raise gains `&& !modal.GuideOpen` (`App.xaml.cs:704`).
  4. **Proactive surfaces defer, never drop (r3 F4, r4 F3/F4):**
     - `SetupNoticeWatch` is **gated on `!IsGuideOpen`** — checks pause while the guide is open and poll once on `GuideOpenChanged(false)`. Deferral inside `RestartNoticeHost` is *not* enough: `SetupLastNoticeService.CheckStartup` calls `_store.WriteShownTime(record.Time)` **before** `_show(...)` (`SetupLastNoticeService.cs:184-186`) and `SetupNoticeWatch` disposes once `CheckStartup()` returns true for a current-session record — so a deferral would still burn `setup-notice-shown.json` behind the guide and permanently lose the notice (r4 F3). (`SetupNoticeWatch`'s direct `_restartNoticeHost.Presenter.ShowNotice(...)` call, App.xaml.cs:584, keeps its wiring; the gate sits in front of the check.)
     - `RestartNoticeHost` gains a GuideOpen-aware path that **defers** (not drops) notices arriving through the host until the guide closes.
     - `BannerSlot` gains an explicit **hold state**: while held, the live banner is hidden and re-queued and dequeue is paused; on release, the slot resumes without wedging `_live` or dropping queued items — covering `StatusCardPresenter`, `SlotDriftNoticePresenter`, `DriftNoticeService`'s enqueued notices, and the shared-with-`CaregiverAlertService` slot safely. (`DriftNoticeService.cs` itself needs **no edit**: its onset drop is exclusively the sensitive/password feedback gate, `DriftNoticeService.cs:51-76` — turning that into a defer would leak feedback after password entry; `SlotDriftNoticePresenter` already just enqueues into the slot, r4 F4.)
     - `LagMarkHost` (which mirrors `RestartNoticeHost`, hiding its dwellable "Lag" button while modals own the screen, `LagMarkHost.cs:108-134`) is wired to `GuideOpenChanged` the same way.
  5. **`_quickActions.SetSummonSuspended("guide", true)`** for the guide's entire lifetime (r2 F4).
  6. **Toolbar hold (r2 F4, r3 F2, r4 F2/F5):** on steps 1–4 and 6–8 the guide holds an explicit **guide-hidden hold** on the toolbar, and the hold must defeat the shipped paths that would otherwise resurrect it:
     - (a) **`ForceShow` suppressed** while held hidden — `SetSuspended` posts an async `ToolbarVisibilityAdapter.ForceShow()` (`ShowAtRest` + `SetActive(true)`) when the toolbar is Hidden, which would land right after the guide's synchronous hide and re-show the toolbar behind the guide;
     - (b) **`ReassertVisibility` neutralized** while held hidden — with `_suspended` true its else-branch calls `SetActive(true)` (`ToolbarAutoHideController.cs:233-240`), which calibration's transitions (`OverlayMessageCoordinator.cs:306/:317`) invoke and step 4 → step 6 would inherit;
     - (c) **restore on guide close — unconditional** (r4 F5): `_toolbar.ForceShow()` + controller state reset to `Visible` runs on **every** guide close/abandon/finish, including closing while on step 5 mid-task (skipping it there would leave the docked toolbar hidden). The tray-fallback-free floating/mouse configurations (`_enabled == false`, no `showToolbarCallback`) have no other recovery path, so **every** mode gets its toolbar back when the guide ends;
     - (d) **step-5 entry needs `ResetToHidden()`** (r4 F2): `SetSuspended(true)` unconditionally forces the controller into `State.Visible` (`ToolbarAutoHideController.cs:208`), and `Feed`'s summon detection only runs `case State.Hidden:` (:386) — in `Visible` an edge dwell waits the 5 s `hideDelayMs` (`ToolbarLifecycleCoordinator.cs:350`) and gazing near the toolbar rect resets the anchor. So lifting the hold on step-5 entry calls a new **`ResetToHidden()`** on `ToolbarAutoHideController` (mirroring `ResetToVisible()` at :551: `_state = State.Hidden`, anchors cleared, hidden via `_visibility`), making the summon detection live **immediately**.
  7. **Toolbar buttons stay inert while `GuideOpen` (r4 F1):** step 5's summoned toolbar (`ShowToolbar` → `SetActive(true)`, `ToolbarVisibilityAdapter.cs:193`) sits above the guide for the 5 s hide delay between the docked edge and the card's Continue/Skip buttons — with every `ToolbarViewModel.OnTargetActivated` case live (`ToolbarViewModel.cs:531-568`), `Settings` would open `SettingsWindow` over the guide, `Keyboard` would inject OS text into whatever background app holds focus, and `TaskSwitcher`/`Modes`/`QuickActions`/`Calibrate` would open panels or trigger out-of-step calibration. So while `GuideOpen` is true, **non-`Pause` toolbar activations are gated** (the `Pause` case stays live — the deliberate emergency path), and `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` (`ToolbarLifecycleCoordinator.cs:193-195/:205`) gain a `!GuideOpen` term. The summon task itself needs none of the buttons — success is the toolbar-shown observation.
- **Z-order (r2 F3 amended, r3 F1/F3):** a **visible `PauseIndicator` is raised above the guide on every guide show/re-show** (resume disc stays dwellable). The ring (`CursorCircle`) is **gated, not merely hidden** (r3 F1): the shipped ring-hide convention stands, but two coordinators independently re-show the ring — `OverlayVmBindingCoordinator.OnCursorVMPropertyChanged` (`CursorVM.IsVisible && !SettingsOpen && !ProbeOpen`, :166-170) and `OverlayMessageCoordinator`'s calibration branches (`if (CursorVM.IsVisible) cursorCircle.Show()`, :308/:318) — and over the guide's **transparent** backdrop a re-shown ring floats visibly over the uncovered margin. So both coordinators' `cursorCircle.Show()` calls route through a **GuideOpen-aware predicate** (ring re-show allowed only while the guide's toolbar task is active); on guide close the ring is **explicitly restored** when `CursorVM.IsVisible && !SettingsOpen && !ProbeOpen`. During step 5 the summon-time z-order chain is pinned: **Guide < Toolbar < PauseIndicator (if visible) < CursorCircle (if `CursorVM.IsVisible`)** (r3 F3).
- **Dwell knobs (r1 F7):** the **practice target binds live `DwellSettings.ActivationButtonDwellMs`** (desktop click activation, `GazeEngine.cs:481` — the concept step 3 teaches); guide chrome buttons bind `ToolbarButtonDwellMs` (`SettingsWindow.xaml.cs:510`), protected by the disarm latch.
- **Gaze feed contract (r2 F5):** the guide consumes `GazeCursorMovedMessage`, gating on `!IsInGap` and **not** on `IsPaused` (shipped proof: `ToolbarViewModel.DwellActiveWhilePaused => true`, :529). The guide's VM **overrides `DwellActiveWhilePaused` to `true`** (the `DwellOverlayViewModelBase` default is `false`) and owns **separate dwell sources for the two knobs** (per-target dwell times, the `SettingsWindow` pattern) — the base class owns a single detector. While paused, `CursorCircle` stays hidden — `PauseIndicator` is the pause cue.
- **Disarm latch:** every newly shown card holds its dwell choices disarmed until gaze has left the card — `LeaveBeforeArmLatch` reused or its pattern reproduced (plan decides after wiring the guide's detector).
- **String surface:** all guide text through `StringTable.Current.T(...)` under a **new closed `guide` surface** (r1 F8, r3 F8): `StringTableSchema.Surfaces` and the validator's `SURFACES` list must stay in **exact parity** (`StringsEnumParityTests` asserts list equality; the schema list is *not* alphabetical today) — `guide` is added to **both** lists; alphabetical order is enforced only on `strings.de.manifest.json` (`strings_validate.py:469`). German side: `src/EyeCtrl.App/Localization/strings.de.json` + `strings.de.manifest.json` (`src` hashes via `--refresh-de-src`), a fit class per `guide.*` key.

## 6. The flow state machine

`FirstRunGuideFlow` (internal sealed, `App/Services`, WPF-free): an ordered step list plus transition rules, driving the window through a narrow surface interface (`IGuideSurface`), headlessly testable with fakes — the `SafetySetupFlow` pattern.

```
Welcome → MeetDwell → DwellTime → Calibrate → ToolbarTask → Theme → Safety → Done
   ↓ Later / Close                                  (Calibrate and ToolbarTask omitted per §7)
 close (flag stays false unless the safety block completed, re-prompt next start)
```

- **Events:** `Start`/`Next`/`Skip`/`Close` (per-step policy), `TaskSucceeded` (practice target, toolbar summon), `CalibrationIdle`/`CalibrationCancelled` (step 4's advance triggers), `SafetyEnded(completed)`, `WindowClosed` (abandon).
- **Lifecycle (r2 F2, r3 F6):** the **per-run trio** — flow, `FirstRunGuideViewModel`, and window — is built by a small factory inside `SettingsUiCoordinator` per `FirstRunGuideRequestedMessage` (the `SettingsWindow` precedent: a closed WPF `Window` throws on re-`Show()`; `OverlayManager` windows are app-lifetime singletons that only `Hide()`, zero `.Close()` under `src/EyeCtrl.App/Overlays/`). The VM is per-run with its window (not a DI singleton). `EventCoordinator` gains **`IsGuideOpen`/`GuideOpenChanged`** and the forwarded **`SplashThreadHandle?`** (`_splashHandle` lives in `App.xaml.cs:62` and is currently forwarded nowhere) so coordinators consume one source of truth; `SupervisionToggleService` keeps flowing through `EventCoordinator.SetSupervisionToggle` (`App.xaml.cs:639`, `EventCoordinator.cs:124`).
- **Surface contract:** the flow knows only `IGuideSurface` (show step content, choices, task state); the window's code-behind implements it by updating `FirstRunGuideViewModel`, which the XAML binds. The VM never talks back to the flow — input flows window → flow events only.
- **Chrome Close (r1 F6):** every card except Welcome ("Later") and Done ("Finish") carries a persistent, gaze-dwellable **Close** in the card chrome — present **through step 7 as well** (`OverlayWindow` is `WindowStyle=None` + `WS_EX_NOACTIVATE`, no gaze-reachable Esc). Close calls `Abandon()` and closes without latching the flag.
- **Skip policy:** steps 2 (MeetDwell) and 5 (ToolbarTask) are skippable tasks; step 4 (Calibrate) is skippable; steps 1/3/6/7/8 are not (3 and 6 are Continue-only; 7 is answerable-through and **latches the flag** at its last answer; 8 is informational).
- **Safety block (§7 step 7):** the flow owns a `SafetySetupFlow` instance. **`StartQuestions()` contract (r1 F6):** `_active = true; _pendingFailure = null; Advance(0);` (Start() minus the intro card), and the guide supplies `setSimpleSettingsUi` as `v => { settings.General.SimpleSettingsUi = v; applyAndSave(); }`. `onEnded(true)` → advance to Done; the chain's failure-hold/checking cards render through the same card surface, chrome Close available throughout.
- **Claims lifecycle:** open (inside `WhenExited`, at the actual `Show()`) → all seven claims acquired; step 5 releases the toolbar hold (claim 6) for the task's duration — with **`ResetToHidden()`** on entry (claim 6d) — and re-acquires it before any other transition; close/abandon/finish → all claims released in acquisition order, with the toolbar **restored unconditionally** (claim 6c, every mode, every exit path) and the ring restored per §5.
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
Card: "Make sure the cursor follows your eyes." **Calibrate now** hides the guide window, sends `CalibrationRequestedMessage`, and **waits for `CalibrationStateChangedMessage` to return to `Idle` (or `Cancelled`)** before re-showing and advancing (r1 F3) — `CalibrationCompletedMessage` fires the moment the result state is entered while `CalibrationWindow` still shows its completion card (good → ~3 s auto-dismiss; poor → Retry/Accept, and **Retry loops back to `Collecting`**). On re-show the guide re-asserts its z-order rules; the calibration transitions' `cursorCircle.Show()` (:308/:318) and `_toolbar.ReassertVisibility()` (:306/:317) both route through the guide's gates — ring re-show suppressed (transparent backdrop: a re-shown ring floats over the uncovered margin, r3 F1) and the toolbar hold's `ReassertVisibility` neutralization (claim 6b) absorbs the re-arm (r3 F2). **Skip** → advance. Without a real tracker (`App.ActiveProviderType == GazeProviderType.Mouse`) the step is omitted. Pre-existing limitation, out of scope: calibration's completion `Retry`/`Accept` are mouse-only `<Button>`s (r1 Q5).

### 5) Toolbar summon — live practice (omitted when floating or mouse-only)
Card: the docked edge band is the way to the toolbar. Task: summon the toolbar by dwelling the docked edge; success = a toolbar-shown observation on the `IToolbarVisibility.ShowToolbar` path; the card updates and **Continue** advances. **Skip** available. **Omitted when `!settings.Toolbar.IsDocked || App.ActiveProviderType == GazeProviderType.Mouse`** (r1 F5 — `ToolbarLifecycleCoordinator.cs:338-339`). On entry the toolbar hold is lifted, the toolbar window force-hidden, and **`ResetToHidden()`** puts the controller into `State.Hidden` so summon detection is live immediately — no 5 s dead window, no anchor reset from gazing near the toolbar rect (r4 F2, §5 claim 6d). On the summon, `ToolbarWindow` is raised above the guide via the new **`RaiseToTop()` members on `IToolbarOverlay`/`ICursorCircleOverlay`** (`OverlayWindow.RaiseToTop` exists on the base class, `OverlayWindow.cs:377`; the interfaces lack it, `IOverlayWindows.cs:67-114/:188-237`), and the summon-time z-order chain applies: **Guide < Toolbar < PauseIndicator (if visible) < CursorCircle (if `CursorVM.IsVisible`)** (r3 F3). The summoned toolbar's buttons stay **inert** while `GuideOpen` (§5 claim 7 — the task needs only the summon, no presses); on exit (Continue/Skip) the holds re-assert and the ring re-hides. The band summon itself is coordinate-driven.

### 6) Theme (local preview + restart-applied persist)
Four chips (Midnight, Daylight, Ember, Contrast) (r1 F2, r2 F6): selection **previews locally** and **persists `settings.Theme.Name`**. The preview **must not** call `ThemeStartup.Apply`/mutate `Tokens.Current`, and it **cannot ride `{StaticResource Brush*}` or static `Tokens.*` reads** (construction-time snapshots, `ThemeResourceApplier.cs:8-9`, `DwellButton.cs:168-214`, `DwellSlider.cs:30`): the card applies the resolved `ITheme`'s brushes **explicitly to card-local elements** — startup-theme elements outside the preview area keep startup brushes by design. The card carries the `settings.general.theme.description` note (takes effect after restart). Continue.

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
| Floating toolbar | ToolbarTask omitted; toolbar restore on guide close covers floating/mouse modes explicitly (claim 6c — no tray fallback to rely on) |
| Tracker lost mid-guide | Task steps are skippable; the dwell-time step still applies its value |
| Calibration retry loop | Guide stays hidden through `Collecting` re-entry; advances only on the `Idle`/`Cancelled` transition (§7 step 4) |
| Crash-recovery bring-up fails mid-questions | Existing held-failure card + OK, chain machinery unchanged |
| Crash-recovery result never arrives | Existing ceiling → "could not be confirmed" failure card |
| Unrelated supervision flight in progress when crash recovery is answered "Yes" | **Targeted chain fix (r1 F9, r3 F5):** `RequestCrashRecovery` distinguishes flights via a new seam on `SupervisionToggleService` — **`IsEnablingInFlight`** (or an equivalent flight-kind exposure; `RequestKind` is `private` at `SupervisionToggleService.cs:23`, `InFlight` a bare `bool` at :88, `RequestEnable` returns `void` at :149) — so an in-flight `Ensuring`/`Disabling` is never mistaken for the enable's result, and already-enabled "Yes" is a no-op success |
| Window closed mid-flow | Abandon: applied answers stay, flag false unless the safety block completed, re-prompt next start |
| Re-run requested while guide open | Refused (one flow at a time) |
| Re-run triggered while user-paused | The guide runs: in-card dwell controls are gaze-tracked and work under user-pause (`DwellActiveWhilePaused => true` override); the summon task works (pause deliberately doesn't gate toolbar dwell); `CursorCircle` stays hidden while paused — `PauseIndicator` is the pause cue, raised above the guide (§5) |
| Quick Actions hot edge during the guide | Summon suspended via the `"guide"` claim for the guide's whole lifetime (§5 claim 5, r2 F4) |
| Docked toolbar during steps 1–4 / 6–8 | Guide-hidden hold: auto-hide suspended, dwell deactivated, toolbar hidden, `ForceShow` + `ReassertVisibility` re-arms suppressed; restored on guide close in **every** mode, unconditionally (§5 claim 6, r3 F2, r4 F5) |
| Toolbar buttons while `GuideOpen` (summoned toolbar on step 5) | Non-`Pause` activations gated; `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` gain a `GuideOpen` term (§5 claim 7, r4 F1) — no Settings/Keyboard/TaskSwitcher/Modes/QuickActions/Calibrate over the guide, no OS text injection |
| Gaze rests on the next card's buttons | Disarm latch: choices armed only after gaze has left the card |
| Status cards / restart notices / caregiver alerts / the Lag mark | Deferred via the `BannerSlot` hold and host-level GuideOpen paths — **never dropped, never wedged**; the startup `StatusCardPresenter` hold releases on guide close or on guide-startup failure (§4, §5 claim 4, r3 F4/F6) |
| Setup notice during a startup guide | `SetupNoticeWatch` gated on `!IsGuideOpen` — checks pause, one poll on close; **no `setup-notice-shown.json` burn** (deferral alone would: `CheckStartup` writes shown-time before `_show`, r4 F3) |
| A ring re-show fires while the guide is up (visibility flip, calibration transition) | Both coordinators' `cursorCircle.Show()` routes through the GuideOpen predicate — suppressed except during step 5; restored on guide close when `CursorVM.IsVisible && !SettingsOpen && !ProbeOpen` (r3 F1) |
| Splash still exiting when the guide raises | `Show()` deferred via the latched `WhenExited` continuation; claims acquired at the actual `Show()` (r2 F1, r3 F6) |
| KB-E1 / logging hygiene (r1 F12) | Guide log lines carry **step ids and state transitions only** — no localized text, no user content |

## 10. Decision ledger (owner-vetoable)

Choices taken while the owner was not answering interactive prompts; every entry can be overturned at review. r-revised entries marked.

- **L1 — Re-run semantics.** Re-prompt at every start until completed to the end; manual re-run entry replacing `g_safety` (full replay). *Rejected:* prompt-once-only — a skipped guide stays unseen until found, contradicting never-strand.
- **L2 — Approach: dedicated guide window, per-run construction.** Live tasks rule out the Settings-modal chain; per-run construction (r2 F2) keeps `Close()`/`WindowClosed` honest and re-run state clean. *Rejected:* OverlayManager singleton — singleton overlays only `Hide()`, and a closed WPF `Window` cannot re-`Show()`.
- **L3 — Step order.** Welcome → MeetDwell → DwellTime → Calibrate → ToolbarTask → Theme → Safety → Done, with steps 4/5 conditionally omitted. *Alternative considered:* calibration first — rejected: the practice target is deliberately big/tolerant; the interaction model is the first thing a new user needs.
- **L4 — Reuse `SafetySetupFlow` for the safety block**, driven by the guide's card surface, plus the targeted §9 race fix (now pinned to the `IsEnablingInFlight` seam, r3 F5). *Rejected:* rewrite — re-implements the crash-recovery wait/ceiling/failure machinery and its tests.
- **L5 — Single completion flag.** `GeneralSettings.SafetySetupDone` remains the latch. *Rejected:* a new `FirstRunGuideDone` flag — redundant migration.
- **L6 — Toolbar task, gated, held, restored, and inert.** Runs only when it can (docked + real tracker); the guide-hidden hold suppresses `ForceShow`/`ReassertVisibility` re-arms, step-5 entry **resets the controller to `Hidden`** so the summon is live immediately, **restores the toolbar on close in every mode unconditionally** (r4 F5), and the summoned toolbar's non-`Pause` buttons stay inert while `GuideOpen` (r4 F1); summon-time z-order chain pinned (r3 F3); the owner can strike the task outright. *(r4-revised.)*
- **L7 — Re-run = full replay** including the safety questions. Sound across re-runs: the chain only ever sets the flag (r1 Q2); the unrelated-flight race is fixed via the flight-kind seam (§9).
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
| `EyeCtrl.App/Services/SafetySetupFlow.cs` | Add `StartQuestions()` (§6 contract); `RequestCrashRecovery` flight-kind guard (§9, r1 F9 + r3 F5); `SafetySetupStartup` message renamed to `FirstRunGuideRequestedMessage`; `SafetySetupLaunch`/`SafetySetupHost` deleted |
| `EyeCtrl.App/Views/SettingsWindow.xaml.cs` | `SafetySetupHost` field + wiring removed |
| `EyeCtrl.App/Views/Settings/SettingsPageContext.cs` | `RunSafetySetup` removed/replaced by the guide raise |
| `EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs` | `g_safety` replaced by "Run the introduction guide" (`_ctx.Close()` then send) |
| `EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs` | Per-run factory (flow + VM + window) behind `FirstRunGuideRequestedMessage`; deferred show via `WhenExited`; starts the flow |
| `EyeCtrl.App/Services/EventCoordinator.cs` | `IsGuideOpen`/`GuideOpenChanged` exposure; `SplashThreadHandle?` forwarding; ring/toolbar gate consumption points (r3 F4/F6, r4 F6 path fix) |
| `EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs` | `cursorCircle.Show()` routed through the GuideOpen predicate (r3 F1) |
| `EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs` | Calibration branches' `cursorCircle.Show()` routed through the GuideOpen predicate (r3 F1); `ReassertVisibility` path absorbed by the toolbar hold (r3 F2) |
| `EyeCtrl.App/Services/ToolbarAutoHideController.cs` | Guide-hidden hold: `ForceShow` + `ReassertVisibility` re-arms suppressed while held; restore-to-`Visible` on release; new **`ResetToHidden()`** (r3 F2, r4 F2) |
| `EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs` | Guide hold claims (`"guide"`) + step-5 toggle; `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` gain a `GuideOpen` term (r2 F4, r3 F2, r4 F1) |
| `EyeCtrl.ViewModels/ToolbarViewModel.cs` | Non-`Pause` activations gated while `GuideOpen` (`OnTargetActivated`, r4 F1) |
| `EyeCtrl.App/Services/IOverlayWindows.cs` | `RaiseToTop()` added to `IToolbarOverlay` + `ICursorCircleOverlay` (r3 F3) |
| `EyeCtrl.App/Overlays/SplashThreadLauncher.cs` | `SplashThreadHandle` gains the latched `WhenExited(Action)` main-dispatcher continuation (r2 F1) |
| `EyeCtrl.App/Services/StatusCardPresenter.cs` | Hold cards while a startup guide is pending; fallback release on guide-startup failure (r2 F1, r3 F6) |
| `EyeCtrl.App/Services/BannerSlot.cs` | Hold state: hide + re-queue live banner, pause dequeue, resume without wedging/dropping (r3 F4) |
| `EyeCtrl.App/Services/RestartNoticeHost.cs` | GuideOpen-aware defer path for host-arriving notices (r3 F4) |
| `EyeCtrl.App/Services/SetupNoticeWatch.cs` (+ `SetupLastNoticeService.cs` seam) | Checks gated on `!IsGuideOpen` (pause + one poll on `GuideOpenChanged(false)`) — no shown-time burn behind the guide (r4 F3) |
| `EyeCtrl.App/Services/LagMarkHost.cs` | `GuideOpenChanged` wiring (hide the Lag button while the guide is up) (r3 F4) |
| `EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs` | `ModalInteractionState.GuideOpen` in `RecomputeSuspension` (r1 F1) |
| `EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs` | `GuideOpen` member added (r4 F6 path fix) |
| `EyeCtrl.Core/FixationSuppressor.cs` | `Guide` member added |
| `EyeCtrl.App/App.xaml.cs` | `canInterrupt` gate gains `&& !modal.GuideOpen`; `SetupNoticeWatch` routed through the defer path; `SplashThreadHandle` forwarded; raise ordering |
| `EyeCtrl.App/Services/SupervisionToggleService.cs` | Flight-kind seam (`IsEnablingInFlight` or equivalent) for the §9 guard (r3 F5) |
| `EyeCtrl.App/Services/AppBootstrapper.cs` | DI registrations (flow/host seams, `EventCoordinator` wiring; VM + window are per-run, not singletons) |
| `EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | `FirstRunGuideRequestedMessage` (+ step/task messages) |
| `EyeCtrl.Core/Localization/StringTableSchema.cs` | `guide` added to `Surfaces` — **exact parity** with the validator's `SURFACES` (`StringsEnumParityTests`), not a sort claim (r3 F8) |
| `tools/strings_validate.py` (+ test) | `guide` added to `SURFACES` (parity); refresh German `src` hashes |
| `EyeCtrl.Core/Localization/strings.json` | `guide.*` keys with fit classes |
| `EyeCtrl.App/Localization/strings.de.json` (+ `strings.de.manifest.json`) | German `guide.*` + manifest (alphabetical surfaces enforced here) |
| `EyeCtrl.ViewModels/LeaveBeforeArmLatch.cs` | Reused unchanged (or pattern reproduced — plan decides) |

### Unchanged but load-bearing

| Piece | Why it matters |
|-------|----------------|
| `SafetySetupFlow` question chain | Absorbed verbatim minus the §9 race fix |
| `CalibrationRequestedMessage`/`CalibrationStateChangedMessage` | Step 4 rides them; advance keys on the `Idle`/`Cancelled` transition |
| `ThemeRegistry.Resolve` / `ThemeStartup.Apply` | Step 6 previews via the former; never calls the latter mid-session |
| `IToolbarVisibility.ShowToolbar` path | Step 5's success observation |
| Dwell-knob split: `ActivationButtonDwellMs` vs `ToolbarButtonDwellMs` | Steps 2/3 teach the former; chrome uses the latter |
| Ring-hide convention + the two coordinators' re-show paths | §5's GuideOpen ring gate; step 5's z-order chain |
| `ToolbarAutoHideController` state machine | The guide-hidden hold must integrate with `SetSuspended`/`ReassertVisibility`/`ForceShow`, not fight them |

## 12. Testing guidance

- **Flow tests (headless, fakes — NFluent, xUnit v3, private nested fakes):** step sequence and order; Calibrate/ToolbarTask omitted per conditions; skip policy; task-success auto-advance; calibration advance keys on `Idle`/`Cancelled` only; safety block: transition card → `StartQuestions` → completed → advance; `StartQuestions` contract; chrome Close during step 7 abandons without latching; "Later" closes with flag false; chain's last answer sets the flag; window-closed abandon; re-run refusal; claims acquired at the actual `Show()` (not before splash exit) and released in acquisition order with the toolbar **restored**; the §9 flight-kind guard (`Ensuring`/`Disabling` in flight ≠ enable result; already-enabled "Yes" no-op).
- **Splash/status tests (r2 F1, r3 F6):** `WhenExited` latched-immediate semantics (null splash, already exited, marshal otherwise); claims acquired inside `WhenExited` at `Show()`; `StatusCardPresenter` hold + fallback release on guide-startup failure; normal launches unchanged.
- **Toolbar/ring seam tests (r3 F1/F2/F3, r4 F1/F2/F5):** guide-hidden hold suppresses an async `ForceShow` landing after the hide; `ReassertVisibility` under the hold does not `SetActive(true)`; release restores the toolbar in docked, floating, and mouse modes (no tray fallback needed) — **including a close while on step 5 mid-task**; `ResetToHidden()` on step-5 entry puts the controller in `State.Hidden` with anchors cleared (summon detection live immediately — a same-moment edge dwell summons without waiting `hideDelayMs`); non-`Pause` toolbar activations gated while `GuideOpen` (Settings/Keyboard/TaskSwitcher/Modes/QuickActions/Calibrate suppressed; `Pause` live) and `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` reject when `GuideOpen`; ring re-show gated in `OverlayVmBindingCoordinator` and `OverlayMessageCoordinator`; ring restored on guide close when `CursorVM.IsVisible && !SettingsOpen && !ProbeOpen`; step-5 z-order chain Guide < Toolbar < PauseIndicator < CursorCircle.
- **Proactive-surface tests (r3 F4, r4 F3/F4):** `BannerSlot` hold hides + re-queues the live banner and resumes without wedging `_live` or dropping queued items (including the shared `CaregiverAlertService` slot); `RestartNoticeHost` defers (not drops); **`SetupNoticeWatch` gated on `!IsGuideOpen`** — no `WriteShownTime` while the guide is up, one poll on `GuideOpenChanged(false)` shows the deferred notice afterwards; `LagMarkHost` hides its button on `GuideOpenChanged`; `DriftNoticeService` untouched (its onset drop is the sensitive-context gate — a pin that it is *not* modified).
- **Window tests (STA, Category=UI, no visible window — `InvisibleTestWindow.Prepare`):** card renders per step VM; disarm latch; chrome Close on steps 2–7; slider writes `ActivationButtonDwellMs` while chrome stays on `ToolbarButtonDwellMs`; `DwellActiveWhilePaused => true` consumed; theme chips apply resolved brushes explicitly to card-local elements with `Tokens.Current` unchanged; z-order pins.
- **Repo gate tests:** `NoVisibleTestWindowGuardTests`, `InvisibleTestWindowTests`, `StringsEnumParityTests` (exact parity after adding `guide` to both lists), German-parity gates.
- **Wiring tests (r2 F7, r3 F7):** `SafetySetupWiringTests.cs` updated for the new seams; **`GeneralPageSourceTests.cs`** updated (`General_HasARunSafetySetupAgainRow_…` pins the `g_safety` label/parameter being replaced).
- **Regression pins:** existing `SafetySetupFlow` tests keep passing (chain unchanged except the §9 guard, which gets its own tests); `SettingsUiCoordinator` tests updated for the per-run guide raise.
- **Log lines:** "First-run guide shown/completed" Information lines, **step ids + state transitions only** (KB-E1 content-free contract); asserted textually.

## 13. Review dispositions

### r1 (FIX-FIRST: 6×S1, 4×S2, 2×S3 — all 12 valid, fixed in rev 2.0)

| Finding | Verification result | Disposition |
|---------|--------------------|-------------|
| F1 (S1) backdrop/suspension model wrong | Verified: `CalibrationWindow.xaml:12` click-through; suppression via `OverlayMessageCoordinator.cs:283` + `ActivationOfferCoordinator` + `canInterrupt` | §5: `GuideOpen` claims; synthetic-broadcast prohibition |
| F2 (S1) theme can't re-skin live | Verified: `ThemeStartup.Apply` startup-only; overlays capture `Tokens` at construction | §7 step 6: local preview, persist, restart note |
| F3 (S1) Retry/Accept mouse-only; `CompletedMessage` overlap | Verified: `CalibrationWindow.xaml:122-127`; emits on entering Active/Failed | Advance on `Idle`/`Cancelled`; `CancelDwellButton` cited |
| F4 (S1) re-run row gaps | Verified: `_ctx.Close()` first; `g_safety` exists; Settings-open hides ring + holds toolbar claim | `g_safety` replaced, close first |
| F5 (S1) ToolbarTask unreachable/z-order | Verified: `ToolbarLifecycleCoordinator.cs:338-339` | Omission condition + raise |
| F6 (S1) step-7 stranding | Verified: `WS_EX_NOACTIVATE`; `SimpleSettingsUi` exists | Chrome Close through step 7; `StartQuestions` contract |
| F7 (S2) dwell-knob split | Verified: `GazeEngine.cs:481` vs `SettingsWindow.xaml.cs:510` | Split adopted |
| F8 (S2) closed string schema | Verified: `StringTableSchema.cs:15-31`; German in `App/Localization/` | Schema/validator/manifest/DE files added |
| F9 (S2) `InFlight` race | Verified: early return `SupervisionToggleService.cs:151` | §9 guard (pinned to the flight-kind seam in r3) |
| F10 (S2) test-fixture constraints | Verified: `Prepare` requirements | §5 constraints + §12 |
| F11 (S3) ledger/scope polish | — | L5/L6 revised |
| F12 (S3) paused re-run + KB-E1 | Verified: deliberate ungating; KB-E1 contract | §4/§9 pinned |

### r2 (FIX-FIRST: 4×S1, 2×S2, 1×S3 — all 7 valid; F3 amended)

| Finding | Verification result | Disposition |
|---------|--------------------|-------------|
| F1 (S1) `ExitFinished`/`StatusCardPresenter` timing | Verified: `ExitFinished` on the splash STA thread (`SplashWindow.xaml.cs:350`); handle exposes nothing; launcher can return `null` | §4: latched `WhenExited`; presenter hold; files added |
| F2 (S1) singleton vs `Close()` | Verified: zero `.Close()` under `Overlays/`; `OverlayManager.Initialize` singletons | §6: per-run construction |
| F3 (S1) z-order | Verified: coordinator re-show uses `Show()`; **but** blanket "raise CursorCircle" conflicts with the ring-hide convention | §5 amended: PauseIndicator raised; ring hidden per convention, step-5-only raise — **r3 F1 then corrected the mechanism** (gated coordinators, not one-shot hides) |
| F4 (S1) QuickActions + toolbar dwell | Verified: claim pattern; `_dwellingActive = true`, `DwellActiveWhilePaused => true` | §5 claims 5+6 |
| F5 (S2) gaze-feed contract | Verified: single-detector base; toolbar's paused override | §5 contract + §9 row |
| F6 (S2) theme preview mechanics | Verified: construction-time captures | §7 step 6: explicit brushes |
| F7 (S3) wiring tests + sort | Verified: source-scans; manifest sort | §12 + parity wording |

### r3 (FIX-FIRST: 4×S1, 3×S2 — all 8 valid)

| Finding | Verification result | Disposition |
|---------|--------------------|-------------|
| F1 (S1) ring re-show breaks the hide rule over a transparent backdrop | Verified: `OverlayVmBindingCoordinator.cs:166-170` (`IsVisible && !SettingsOpen && !ProbeOpen → Show()`), `OverlayMessageCoordinator.cs:308/:318` (unconditional re-show on calibration transitions) | §5: GuideOpen-aware ring predicate in both coordinators + explicit restore on guide close; both files in §11 |
| F2 (S1) toolbar hold: async `ForceShow` race, `ReassertVisibility` leak, floating/mouse stranding | Verified: `ReassertVisibility` else-branch `SetActive(true)` while suspended (`ToolbarAutoHideController.cs:233-239`); `ForceShow` = `ShowAtRest` + `SetActive(true)` (`ToolbarVisibilityAdapter.cs:247-267`); floating/mouse have no tray fallback | §5 claim 6 rewritten: guide-hidden hold suppressing (a) `ForceShow`, (b) `ReassertVisibility` re-arm, (c) restore (`ForceShow` + state `Visible`) on close in every mode |
| F3 (S1) `RaiseToTop` contradictions + interface members + step-5 z-order | Verified: adapter *does* call `RaiseToTop` — for the `PauseIndicator` (`ToolbarVisibilityAdapter.cs:149/:205`) — so my r2 claim was wrong; `IToolbarOverlay`/`ICursorCircleOverlay` lack the member | §7 step 5 claim removed; `RaiseToTop()` added to both interfaces; step-5 chain pinned: Guide < Toolbar < PauseIndicator < CursorCircle; `IOverlayWindows.cs` in §11 |
| F4 (S1) proactive-surface holes | Verified: `SetupNoticeWatch` calls `_restartNoticeHost.Presenter.ShowNotice` directly (`App.xaml.cs:584`); `BannerSlot` has no hold seam (wedge/drop risks); `LagMarkHost` mirrors the host pattern (`LagMarkHost.cs:108`, `App.xaml.cs:629-631`) | §5 claim 4 rewritten: defer-not-drop hosts, `BannerSlot` hold state, `LagMarkHost` wired; `BannerSlot.cs`/`LagMarkHost.cs`/`EventCoordinator.cs` in §11 |
| F5 (S2) supervision seam missing | Verified: `RequestKind` private (:23), `InFlight` bare bool (:88), `RequestEnable` void (:149) | §9 guard pinned to a new `IsEnablingInFlight` seam; `SupervisionToggleService.cs` in §11 |
| F6 (S2) composition gaps | Verified: `_splashHandle` at `App.xaml.cs:62` forwarded nowhere; `SetSupervisionToggle` path exists (`EventCoordinator.cs:124`) | §6: per-run factory; `EventCoordinator` gains `IsGuideOpen`/`GuideOpenChanged` + splash forwarding; claims at actual `Show()`; status-card fallback release |
| F7 (S2) `GeneralPageSourceTests` omitted | Verified: pins the `g_safety` label/parameter being replaced | §12 updated |
| F8 (S3) `Surfaces` order + DynamicResource noise | Verified: schema list is *not* alphabetical; parity is exact-list-equality; manifest sort only | §5/§11 parity wording; DynamicResource parenthetical dropped |

Open questions: Q1 — closed by the ring/toolbar/banner/lag seams; Q2 — sound with the flight-kind seam; Q3 — sound with `WhenExited` + forwarding + claims-at-Show; Q4 — affirmed; Q5 — affirmed.

### r4 (FIX-FIRST: 2×S1, 3×S2, 1×S3 — all 6 valid)

| Finding | Verification result | Disposition |
|---------|--------------------|-------------|
| F1 (S1) live toolbar buttons + keyboard/settings feeds during `GuideOpen` | Verified: `ShouldFeedKeyboardSample` (`ToolbarLifecycleCoordinator.cs:193-195`) and `ShouldFeedSettingsSample` (:205) lack a `GuideOpen` term; all `OnTargetActivated` cases live (`ToolbarViewModel.cs:531-568`); summoned toolbar sits above the guide for the 5 s `hideDelayMs` (:350) | §5 claim 7: non-`Pause` activation gate + `GuideOpen` terms in both feed checks; §7 step 5 + §9 rows; `ToolbarViewModel.cs` in §11 |
| F2 (S1) step-5 force-hide leaves controller in `State.Visible` | Verified: `SetSuspended(true)` forces `_state = State.Visible` (`ToolbarAutoHideController.cs:208`); summon detection only in `case State.Hidden:` (:386); `hideDelayMs: 5000` | §5 claim 6(d): new **`ResetToHidden()`** on step-5 entry; §7 step 5; controller row in §11 |
| F3 (S2) `SetupNoticeWatch` deferral still burns `setup-notice-shown.json` | Verified: `CheckStartup` calls `_store.WriteShownTime` **before** `_show` (`SetupLastNoticeService.cs:184-186`); watch disposes on a true return | §5 claim 4 rewritten: **gate on `!IsGuideOpen`** (pause + one poll on close) instead of defer-after-show; `SetupNoticeWatch.cs` row in §11 |
| F4 (S2) `DriftNoticeService` mischaracterized | Verified: onset drop is exclusively the `_feedbackGate.IsFeedbackSuppressed` sensitive-context gate (`DriftNoticeService.cs:51-76`); presenter just enqueues | Claim 4 wording corrected; **`DriftNoticeService.cs` removed from §11**; §12 pins it unmodified |
| F5 (S2) "(outside step 5)" restore contradiction | Text-level: closing on step 5 mid-task would strand the toolbar | §5 claim 6(c): restore **unconditional** on every close/abandon/finish |
| F6 (S3) path/line fixes | Verified: `EventCoordinator.cs` at `Services/` (not `Coordinators/`); `ModalInteractionState.cs` at `App/Services/Coordinators/`; `ReassertVisibility` span `:233-240` | §11 paths fixed; citations updated |

Open questions: Q1 — closed by claim 7 (the last step-5 hole); Q2 — affirmed with the seam; Q3 — affirmed with the `!IsGuideOpen` gate; Q4/Q5 — affirmed.
