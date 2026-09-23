# Dwell-Completion Audio Cue — Design

- **Date:** 2026-09-23
- **Status:** Approved (owner, interactive session 2026-09-23) — spec review pending
- **Branch:** `feat/dwell-audio-cue` (worktree `.worktrees/dwell-audio-cue`, base `c9225e2c`)
- **Origin:** competitive-feature queue re-rank 2026-09-22 (`docs/COMPETITIVE_FEATURE_ANALYSIS.md` follow-up tier, "dwell-completion audio cue — small, direct confirmation value for a gaze-only typist"); picked by owner as next queue item after #157 merged (PR #165).

## Problem

The only sound in the app is the keyboard key click (`Assets/click.wav`, played by `KeyboardSoundService` from `KeyboardWindow.OnKeyDwellCompleted`, gated by `KeyboardSettings.ClickSoundEnabled`, default `true`). Every other dwell in the app completes silently. For a gaze-only user a completed dwell is the primary "I did something" moment, and on surfaces where the visual effect is subtle or delayed (toolbar toggles, task-switcher switches, settings rows) there is no immediate confirmation channel. Hearing the activation closes that feedback gap without requiring a visual check.

## Behavior

A short, soft confirmation tone plays when a dwell **activates** (completes), split into two sound families:

| Family | Surfaces | Sound | Gate |
|---|---|---|---|
| Typing (existing) | Keyboard keys; prediction-bar accepts (new); emoji picks (already routed as keys) | existing `click.wav` | `KeyboardSettings.ClickSoundEnabled` (default **true**, unchanged) |
| Cue (new) | Toolbar band, action-menu ring, modes menu, quick-actions grid, task switcher, right-edge bar, activation button, glass-backdrop hide, drag targets, settings (Tab/Control/Pagination/Modal), emoji category bar, eye-care banner, notes overlay, manual-selection dwells | new `Assets/dwell-cue.wav` (~50 ms soft tone) | new `DwellSettings.CueEnabled` (default **false**, owner choice) |

**Deliberately excluded:** external-app dwells (`ExternalDwellService` — owner declined), repeat-mode re-fire ticks (timer-driven in `RepeatModeController`, not dwell completions), `ScrollOverlayViewModel` and `ManualSelectionOverlayViewModel` (their `OnTargetActivated` overrides are empty — no effect to confirm; they opt out of publishing, see Mechanism), Supervisor UI (out of scope), and Core (framework-agnostic — no sound concerns there).

## Mechanism

Approved approach: **activation message + single subscriber** (alternatives in ledger D7).

1. **Message** — new empty record `DwellActivationMessage` in `EyeCtrl.ViewModels/Messages/InteractionMessages.cs`. Published on the UI thread by the surfaces below through the `WeakReferenceMessenger` each already owns. Empty payload by design (D5).
2. **`DwellOverlayViewModelBase`** (`src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs`) — the base class currently subscribes `DwellDetector.TargetActivated += OnTargetActivated` (line 29). It will instead subscribe a private handler that (a) publishes `DwellActivationMessage` when the new protected virtual property `AnnounceDwellActivation` is `true` (default), then (b) calls the existing virtual `OnTargetActivated(targetId)`. This covers all nine effectful derived VMs — Toolbar, ActionMenu, ModesMenu, QuickActionsGrid, TaskSwitcher, EdgeButtonBar, ActivationButton, GlassBackdrop, DragTrail — with **zero derived-constructor changes**. The two empty-handler VMs (`ScrollOverlayViewModel`, `ManualSelectionOverlayViewModel`) override `AnnounceDwellActivation => false`.
3. **Stragglers with their own detectors/dispatch** publish the same message where the **detector event arrives** — never inside dispatch methods that non-dwell paths (mouse-click fallback, `SimulateActivated`, test hooks) also call. One publish per site:
   - `SettingsWindowViewModel` — the four detector-subscription lambdas (`src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs:89–98`: `_tabDwell`/`_controlDwell`/`_pagDwell`/`_modalDwell`), covering all four categories including modal Yes/No. NOT inside the private `OnTargetActivated(id, category)` dispatch (line 187): `SimulateActivated` (line 178, mouse-click fallback and tests) routes through it too, and a mouse click is not a dwell completion.
   - `EmojiGridManager.OnCategoryDwelled` (`src/EyeCtrl.App/Overlays/EmojiGridManager.cs:97`) — the `_categoryDwellDetector.TargetActivated` handler; category switching is navigation, not text insertion (D1).
   - `EyeCareBannerWindow.OnTargetActivated` (`src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs:103`) — detector handler.
   - `NoteManager.OnTargetActivated` (`src/EyeCtrl.App/Services/NoteManager.cs:260`) — detector handler.
   - `ManualSelectionDwellService.OnDwellActivated` (`src/EyeCtrl.App/Services/ManualSelectionDwellService.cs:103`) — detector handler.
   - The **prediction bar** does NOT publish the cue message: `KeyboardWindow.OnPredictionDwelled` calls the existing `_soundService?.PlayKeyClick()` — prediction accepts are typing (D1). Emoji cell picks already flow through `OnKeyDwellCompleted` as keys.
   - The same boundary rule holds for the base class: the wrapper sits on the `DwellDetector.TargetActivated` subscription, so the derived VMs' `Simulate*`/`ActivateForTest` direct calls into `OnTargetActivated` stay silent (they bypass the detector event).
4. **`DwellCueService`** (`src/EyeCtrl.App/DwellCueService.cs`, new) — mirrors `KeyboardSoundService`: primary ctor `(DwellSettings settings, string wavPath)` preloads a `SoundPlayer` only if the file exists (missing/corrupt file ⇒ null player ⇒ silent no-op); an optional player-factory parameter is the test seam. Constructor-subscribes `DwellActivationMessage` on the injected `IMessenger` (the `DwellTimeRouter` pattern); handler: `if (settings.CueEnabled) player?.Play()`. Reads the **live** settings object, so toggling applies without restart (same as `ClickSoundEnabled`). Registered in `AppBootstrapper` next to `DwellTimeRouter`; wav path resolved like the click (`Path.Combine(BaseDirectory, "Assets", "dwell-cue.wav")`).
5. **Asset** — `Assets/dwell-cue.wav`: a synthesized ~50 ms soft tone (sine with fast exponential decay, quiet level — D4/D6). Committed as binary; `<Content Include>` + `<ExcludeFromSingleFile>true</ExcludeFromSingleFile>` exactly like `click.wav` (`EyeCtrl.App.csproj:51`). Generated by a one-off script (not committed); amplitude IS the volume — `SoundPlayer` exposes no volume control.

## Settings

- `DwellSettings.CueEnabled` (`[ObservableProperty] private bool _cueEnabled = false;`) with XML doc. No `Validate()` entry (plain bool; nothing to clamp).
- Row on the Dwell tab: `ToggleSpec("d_cue_sound", "Dwell Completion Sound", …)` in `DwellPageSource`, getter/setter + `ApplyAndSave()` like siblings. The descriptions-completeness gate binds it automatically (every row needs a description — one is written).
- `d_reset` ("Reset Dwell Settings") learns `d.CueEnabled = def.CueEnabled;`. The #157 row-enumerated drift guard (`KeyboardPageSourceResetDriftTests` pattern — enumerates `Build()` rows and perturbs via each row's own Set) then covers the new row **by construction**, no new guard test needed; the plan verifies this by running the guard.
- #153 per-setting tolerance in `SettingsService.Load` covers a plain bool automatically (bool is either accepted or the section probe treats it as any other member; no clamp family). No new tolerance test; plan spot-verifies a hand-edited `"cueEnabled": "bogus"` degrades to default without wiping the section.
- **Not** added to `KeyboardStackSnapshot` (`SettingsSnapshots.cs`): that record captures "every input the prediction stack is rebuilt from" (`SettingsSnapshots.cs:26`) — `CueEnabled` is not one; adding it would trigger pointless prediction-stack rebuilds.

## Pause interplay

The cue means "a dwell completed", not "the OS effect fired" (D2). Surfaces that did not opt into `DwellActiveWhilePaused` cannot activate while paused at all (`SampleMayDwell` gate). Opt-in surfaces (task switcher tiles, quick actions) suppress only the OS-input effect via `LastSampleWasPaused` and still perform their own dismissal — an activation always *does* something visible, so the sound is truthful in every reachable state. No new gating is introduced: publishing rides the same activation path the effects do.

## Error handling

- Missing or unreadable wav ⇒ `DwellCueService` holds a null player ⇒ no-op, no crash, no log spam (KeyboardSoundService precedent).
- `SoundPlayer.Play()` is non-blocking (plays on a thread-pool thread); overlapping activations overlap playback — acceptable for a ~50 ms tone and identical to existing key-click behavior under fast typing.
- Service is a DI singleton subscribed for the process lifetime (DwellTimeRouter precedent) — no unsubscription path needed, no leak.

## Testing strategy

- **ViewModels.Tests** — base class: a minimal test subclass of `DwellOverlayViewModelBase` publishes `DwellActivationMessage` on activation (drive the `DwellDetector` via `Receive`/`GazeCursorMovedMessage` samples); an `AnnounceDwellActivation = false` subclass publishes nothing. `SettingsWindowViewModel`: a real detector activation publishes the message, and `SimulateActivated` does NOT (pins the publish-boundary rule against mouse fallbacks). Real-subclass coverage rule (repo trap: synthetic-subclass tests can't prove real-subclass wiring): a reflection test over the `EyeCtrl.ViewModels` assembly pins the opt-out set to exactly `{ScrollOverlayViewModel, ManualSelectionOverlayViewModel}` — the default is `true`, so a future effectful VM is covered with no action; the pin forces any new opt-out to be a conscious decision that touches the test.
- **App.Tests** — `DwellCueService`: enabled + message ⇒ player-factory fake records `Play()`; disabled ⇒ no play; missing wav ⇒ constructed with null player, publishes harmlessly. `KeyboardWindow` prediction-click: `KeyboardSoundService.PlayKeyClick` becomes `virtual`; a recording test double is injected via the existing `SetSoundService` seam; `OnPredictionDwelled` (made internal-visible if needed) asserts the click fires.
- **Drift guard / tolerance** — verified by running existing suites (`KeyboardPageSourceResetDriftTests` grows one row automatically; `SettingsService` tolerance suites stay green). Settings-row description completeness gate enforces the description.
- Full gates before handoff: Core, ViewModels, App (non-UI + gated UI where applicable) green; GitNexus `impact` before edits (`DwellOverlayViewModelBase` upstream — expect the 11-VM fan, LOW risk: additive publish) and `detect_changes` before commit.

## Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | Prediction-bar accepts = typing family (existing click, `ClickSoundEnabled`); emoji **category** switching = cue family | Prediction accepts insert text like keys; they must not be silent until a default-off toggle is enabled, nor sound different from the keys around them. Category switching navigates, doesn't type. | Prediction/emoji as cue-family (silent by default / different tone for text input). |
| D2 | Cue fires on pause-suppressed activations (opt-in surfaces) | Activation still performs its own dismissal — the sound says "dwell completed", which is true. No extra gating machinery. | Suppressing the cue while `LastSampleWasPaused` (extra per-surface plumbing for a rare state where the sound is still honest). |
| D3 | `CueEnabled` default **false**; live-read (no restart) | Owner choice — conservative ship, no behavior change until opted in. Live object read matches `ClickSoundEnabled` semantics. | Default true (immediate value, but changes behavior for existing installs unasked). |
| D4 | No volume knob | `SoundPlayer` has none; the wav's recorded amplitude is the volume. A volume slider is new UI + settings surface for marginal value. | Volume setting 0–100 (requires MediaPlayer or NAudio dependency). |
| D5 | Empty `DwellActivationMessage` record | One subscriber, one behavior; no consumer needs a payload. Adding one later is additive. | Payload with surface id/VM type (only useful for debugging/spam analysis — YAGNI). |
| D6 | Synthesized ~50 ms wav committed as Content | No external download/licensing; matches `click.wav` packaging (`ExcludeFromSingleFile` — loose file beside the exe). | Shipping a downloaded sample (provenance/licensing burden); NAudio-generated tones at runtime (new dependency). |
| D7 | Messenger message + ctor-subscribing App service | Zero derived-VM ctor changes; sanctioned patterns (`WeakReferenceMessenger` as VM bus; `DwellTimeRouter` ctor-subscribe); testable end-to-end. | (B) `IDwellActivationAnnouncer` injected through 11 VM ctors — hits the factory-lambda-ignores-optional-param test trap. (C) Core `DwellDetector`-level hook — fires before VM effect-gating (pause-suppressed beeps; violates the gate-the-effect principle) and drags sound policy into Core. |

## Out of scope / future work

- Cue for external-app dwells (`ExternalDwellService`) — owner declined for now; would be one more publish site if wanted.
- Per-surface cue curation (e.g., cue everywhere except settings) — single toggle only; split later if the owner finds a surface noisy.
- Cue volume / additional sounds (two-tier feedback) — declined in brainstorm (YAGNI).
