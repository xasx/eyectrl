# Competitive Feature Analysis — OptiKey & Tobii Dynavox Computer Control vs. EyeCtrl

**Date:** 2026-07-11 · **Refreshed:** 2026-08-04, 2026-09-08 (code-verified re-runs; 2026-09-08 also shipped the first implementation slice, on `feat/mouse-osk-polish`)
**Purpose:** Reference for prioritizing feature work. Inventories what OptiKey and Tobii Dynavox Computer Control (TD Control) offer, what EyeCtrl has today, and ranks the gaps by sensibility to integrate.

**Method:** Verify EyeCtrl's *actual* state by reading the code (not assuming) — every "already present" / "resolved" claim cites a file on `main`. Candidate features are grouped, then ranked into tiers by (a) value to the end user, (b) implementation effort in this codebase (with the likely seam named), (c) fit with EyeCtrl's mission (computer-control + eye-care). AAC/communication boards and full UI localization are explicitly de-scoped unless cheap.

**Sources inspected:**
- **OptiKey** — open-source (GitHub: OptiKey/OptiKey). README, wiki, supported-trackers page.
- **Tobii Dynavox Computer Control / TD Control** — online docs (TD Control User's Manual, release notes v1.0–v1.80.1) **plus the live v1.80.1 installation on this machine** at `C:\Program Files (x86)\Tobii Dynavox\Computer Control\Tdx.ComputerControl.exe` (settings schema at `%AppData%\Tobii Dynavox\Computer Control\Profiles\Default\Settings\Settings.json`, keyboard language packs in `%ProgramData%\Tobii Dynavox\Computer Control\Content\Keyboard\`).
- **EyeCtrl** — codebase read-only inspection (Core, Platform, ViewModels, App, EyeCare, LM).

> **How to read this refresh:** the 2026-07-11 version listed 6 Tier-1 gaps. Four of them shipped to `main` since (auto-launch, track-status box, radar/anchor scroll, adaptive dwell) and are moved to §"Resolved since 2026-07-11" below with ship evidence. The tiered table in §Gap inventory reflects only what is **still open** as of 2026-08-04. See the end for the change-log.

> **How to read the 2026-09-08 refresh:** third run, same method (every claim code-verified). Window-management actions (#2) confirmed shipped to `main` since August — moved to "Resolved". The mouse/OSK polish bundle (#21) was implemented the same day on branch `feat/mouse-osk-polish` (`2ded5589`, PR #138) and is recorded under "Resolved" with its branch rather than left in the open tables. Two new gaps were found and ranked: prediction controls (#22, Tier 1) and per-eye tracking (#23, Tier 2).

---

## EyeCtrl's differentiators — keep & protect

Capabilities the two competitors lack. EyeCtrl's strategic position rests on them; verify presence before any adjacent refactor.

1. **Eye-care subsystem (`src/EyeCtrl.EyeCare/`)** — `EyeCareScheduler` (20-20-20 + natural-break suppression + snooze/hold), `FatigueMonitor` (rolling score, opt-in escalation), `ExerciseRunner` + 5 scored gaze-tracked exercises (20-20-20, Directional Sweeps, Near-Far Focus, Saccade Jumps, Smooth Pursuit), `IEyeCareService`. Fully wired into the live gaze stream. **Neither OptiKey nor TD Control has any break/fatigue/exercise feature** — the single biggest differentiator.
2. **ONNX SmallLM word prediction (`src/EyeCtrl.LM/`)** — pluggable SmolLM2/Qwen2.5/Phi-3.5 next-word models via `SmallLMPredictor` + `LMTokenizer`, GPU/DirectML, per-user custom model entries. More advanced than OptiKey's dictionary; on par with TD Control's SwiftKey.
3. **Cooperating-app SDK (`src/EyeCtrl.Sdk.Abstractions/`, `Sdk.Wpf/`, `Sdk.WinUI/`) + `src/EyeCtrl.App/Services/ExternalDwellService.cs`** named-pipe server — third-party apps register their own controls as dwellable. Unique to EyeCtrl.
4. **UIAutomation snap-to-control (`src/EyeCtrl.Platform/Services/ControlFinder.cs`)** — gaze-snap to the nearest interactive control via the UIA tree (`AutomationElement`). EyeCtrl's analog of TD Control's YOLO/CV element-detection, via a different (and dependency-free) mechanism.
5. **Granular per-surface dwell times (`src/EyeCtrl.Core/Settings/DwellSettings.cs`, `DragSettings.cs`)** — ~16 independent per-surface dwell knobs (activation button, action menu, manual-selection action menu, toolbar, keyboard key, prediction, drag release, drag right-edge, scroll stop, manual-selection side, backdrop hide, …) each with its own validated range, plus a separate dwell set under `DragSettings`. **Finer-grained than TD Control**, which exposes only four (`ButtonDwellTime` 550, `KeyboardDwellTime` 450, `KeyboardPredictionDwellTime` 600, `TraceFixationTime` 700).

---

## Resolved since 2026-07-11 (previously listed as gaps)

Each row records where it shipped — `main` unless the row says otherwise. Listed here so the history is preserved and the still-open tables stay uncluttered.

| # | Was a gap | Resolution |
|---|-----------|------------------------------|
| 1 | Auto-launch on startup was a no-op toggle | **Implemented.** `WindowsStartupRegistrar` writes the HKCU `…\CurrentVersion\Run` "EyeCtrl" value; `IStartupRegistrar` is the seam; `StartOnBootPolicy` suppresses self-registration under supervised mode (the supervisor owns launch-at-logon then). `Environment.ProcessPath` is used (not `Assembly.Location`) so the single-file bundle works. |
| 2 | **Window-management actions** — minimize / maximize / restore / close on the window under the gaze point | **Implemented on `main`.** `WindowAction` enum (`src/EyeCtrl.Core/Models/WindowAction.cs`), `Win32WindowManager.Minimize/MaximizeOrRestore/CloseWindow`, fired from Quick Action presets via `QuickActionDispatcher.ExecuteWindowAction` (the target HWND is resolved synchronously at selection). `ShowDesktop` deliberately not added — reachable as Win+D through the OSK's Win modifier. |
| 3 | No track-status box | **Implemented.** `TrackStatusWindow` + `TrackStatusViewModel` render a top-right click-through overlay: two eye dots in a track-box canvas, head distance (mm) + a Good/Fair/Poor quality triad via palette tokens, polled at ~15 Hz. Reacts to validity changes, not just X/Y. |
| 4 | **Continuous / Repeat-Click modes** — lock a click action so it repeats without reopening the action menu | **Implemented 2026-09-08** on `feat/repeat-click-mode` (stacked on PR #138). `InteractionState.RepeatMode` + `InteractionManager` locked-repeat state machine (`EnterRepeatMode` direct entry, FreeLook-guarded; `LastRepeatableAction` click-family memory feeds the locked action). Entry UX is a **toolbar Modes button** opening a small mode menu under the toolbar (owner direction, rework D1′ — TD's ModesMenu analog; extensible for a future Drawing-mode row): one "Repeat clicks" row toggles the mode and shows its on/off state. App-side `RepeatModeController` re-fires the locked click at the live `GazeEngine.StableCentroid` every `RepeatSettings.IntervalMs` (default 1000, Advanced tab); exits via the menu row, the right-edge stop button (⏹), ESC (global hook, #141/#142), or lost-gaze auto-exit; pause is blocked while repeating; the cursor keeps following gaze so the user can aim by looking; ticks are suppressed until gaze leaves the menu (D21). Nothing ships in the action ring. Declined: TD's Click-on-Fixation variant — direct dwell-to-click contradicts EyeCtrl's Interaction-First stance. |
| 5 | Scroll was discrete notch actions only | **Implemented (radar/anchor variable-speed).** `ScrollEngine` is a gaze-joystick: dead-zone (`DeadZoneRadiusPixels = 30`) around an `anchor` point → proportional velocity → clamp at `MaxVelocityNotchesPerSec = 60`; `ScrollModeController` mounts it on the right edge (`RightEdgeAnchor`). This **is** TD Control's radar/anchor variant. |
| 6 | No adaptive dwell | **Implemented.** Full Adaptation subsystem: `EyeCtrl.Core/Adaptation/` (`AdaptationController`, `StruggleMonitor`, `AdaptationKnob`, `EffectiveSettingsProvider`, `EffectiveValue`, `FailureModeAssessment`, `DwellContext`/`DwellOutcome`, `BufferingTelemetrySink`) + App-side `AdaptationCoordinator`, `AdaptationActivation`, `AdaptationCueService`, `AdaptationLog`. Struggle evidence re-tunes effective dwell settings. (In-flight `feat/struggle-detection-phase1a/1b` add per-surface telemetry wiring + cue/drift overlays on top of this core.) |
| 21 | **Mouse/OSK polish bundle** — middle click, word-delete key, clipboard keys on the Fn layer | **Implemented 2026-09-08** on `feat/mouse-osk-polish` (`2ded5589`; PR #138). `InteractionAction.MiddleClick` + `IInputSimulator.MiddleClick` + `MOUSEEVENTF_MIDDLE*` flags + ring icon/label/description — ships **OFF** by default (like ScrollLeft/ScrollRight) because a fifth default secondary action breaks the ring's 4+More/4+Back layer slot symmetry (`ActionMenuViewModelLayerTests.TheSecondLayer_ReusesTheSameFiveSlots`); enable via Settings → Action Menu. `deleteWord` key (Ctrl+Backspace with word-wise CurrentInput/`_recentText` bookkeeping, `KeyboardViewModel.HandleDeleteWord`) beside Bksp in every sublayout of all 3 layouts, Shift/Bksp narrowed to compensate on the 960-DIP screen. New `shortcut:<chord>` layout grammar powers the Fn clipboard row (Copy/Paste/Cut/Undo/Redo/Sel All); `EyeCtrl.Core/Keyboard/KeyboardShortcut.TryParse` is the single chord parser, now also used by `QuickActionDispatcher.ExecuteKeyboardShortcut`. `ShippedLayoutFilesTests` guards the shipped layouts: parse, known grammar only, DelWd + clipboard present, specialAction ids unique per sublayout. |
| 22 | **Prediction controls** — configurable suggestion count, add-word key, dictionary editor | **Implemented 2026-09-20/22, complete on `main`** (PR #160 + PR #162; two external review rounds each). Slice 1: `KeyboardSettings.MaxSuggestions` (1–5, default 5) + `k_pred_count` slider on the Keyboard tab. Slice 2 (PR #162): an **Add Word** key on the Fn clipboard row (learns CurrentInput without committing; the word shows in the bar immediately), and a **learned-words dictionary page** in Settings (`UserDictionaryService` lists learned = user-file keys minus base keys, filtered to real word shapes; deletes go through the LIVE prediction stack via `WordPredictor.ForgetWord` + `PredictionStackFactory.ForgetWords` one-swap tombstones so in-flight background builds can't resurrect a deleted word; three-state write results with error surface). |

---

## Gap inventory — still open, ranked by sensibility to integrate

### Tier 1 — High sensibility (still open)

*No open Tier-1 gaps. The last one, #22 prediction controls, completed 2026-09-22 (PR #160 + PR #162) — see the Resolved table.*

### Tier 2 — Medium sensibility (moderate–high effort, solid value, on-mission)

| # | Gap | Source | Notes + likely seam |
|---|---|---|---|
| 7 | **Multi-monitor gaze** | Both | Still hard-coded to the primary monitor. `Win32InputSimulator` uses `SM_CXSCREEN`/`SM_CYSCREEN`; the doc-comment at `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs:12-15` explicitly notes `MOUSEEVENTF_VIRTUALDESK` + virtual-screen metrics are the fix. Needs that flag + a coordinate remap + Tobii provider sizing. Confirmed still open on `main`. |
| 8 | **Physical switch / gaze+switch hybrid input** | Both (TD Control "Gaze & Switch"; OptiKey supports adaptive switches) | Serves users with unsteady gaze. Switch-confirm path in `InteractionManager`. Confirmed not on `main`. |
| 9 | **Multiple named user profiles** | TD Control | `SettingsService` already validates/persists JSON; add a profile picker + per-profile files. No cloud needed. Confirmed not on `main`. |
| 10 | **Drawing Mode** — free-path hold-and-drag with intentional-movement threshold + 8-direction straight-line catch-up | TD Control | The basic `Drag` action exists (`InteractionAction.Drag` → `MouseDown` + gaze-follow), but there is no 8-direction lock, jitter threshold, or straight-line catch-up. Incremental on the drag path. Confirmed not on `main`. |
| 11 | **Dead-key / diacritic composition** (`´` then `e` → `é`) | Both | **Implemented 2026-09 on `feat/accent-input`, pending merge** — per-layout `accents` sub-layouts with auto-return (QWERTZ 28-key French-in-German set, AZERTY 20-key tail); dead-key engine declined as the revisit path (spec D1) |
| 12 | **More keyboard languages** (TD Control ships 14 incl. CJK/IME) | Both | EyeCtrl ships 3 JSON layouts: `qwerty`/`azerty`/`qwertz` (`src/EyeCtrl.App/Resources/Layouts/`). Latin-script additions are easy via JSON; **CJK/IME is a large separate project — keep out of scope.** |

**Parked — #23 per-eye tracking (TrackedEye Left/Right/Both).** License-gated on this owner's hardware: the unlicensed consumer Stream Engine path returns `TOBII_ERROR_INSUFFICIENT_LICENSE` for `tobii_set_enabled_eye` (field-validated 2026-09-19 — zero user-visible difference with or without the branch). The complete implementation is preserved on branch `feat/tracked-eye` (worktree `.worktrees/tracked-eye`); resurrect by rebasing it if a Tobii license ever materializes. Until then this gap is declined, not queued.

### Tier 3 — Lower sensibility for EyeCtrl (high effort, or off-mission)

| # | Gap | Source | Why deprioritize |
|---|---|---|---|
| 13 | **AAC / TTS / communication boards** ("Speak", phrase boards, symbol libraries) | OptiKey (core); TD Control has a Notepad only | Big feature, **different mission**. EyeCtrl = computer-control + eye-care, not a communication aid. TTS on the Notes feature would be the thin end of the wedge if ever wanted. |
| 14 | **AI/vision element detection (YOLO/CV gaze-snap)** | TD Control ships `yolov3-tiny.weights` + `Tdx.ElementDetection.dll` | **Re-confirmed:** `main` has no YOLO/OpenCV/EmguCV/Dlib/HaarCascade — EyeCtrl covers this via UIAutomation (`ControlFinder`). CV path only helps for non-accessible apps; high effort, marginal gain. |
| 15 | **Localization (UI languages)** | Both (TD Control: full UI localization, 14 keyboard langs) | Broad, tedious, low short-term strategic value; English UI + the prediction-dictionary languages already shipped is reasonable. Decline unless cheap. |
| 16 | **Webcam-based gaze** | OptiKey | Low accuracy, large effort (model + camera pipeline). Tobii + mouse positioning covers the realistic install base. |
| 17 | **Non-Tobii tracker SDKs** (EyeTribe, myGaze, etc.) | OptiKey | Tobii Stream Engine + mouse fallback covers the realistic install base. Diminishing returns. |
| 18 | **Cloud settings/prediction sync** | TD Control (myTobiiDynavox) | Infrastructure + privacy burden; local-JSON is fine for a single-user tool. |
| 19 | **Blink selection** | (neither major app; niche) | TD Control explicitly rejects blink. Low value, tricky accuracy. Skip. |
| 20 | **Gaze-to-wake from sleep** | TD Control (I-Series hardware only) | Hardware/BIOS-level; not reliably achievable in software. Skip. |
| 24 | **Multi-key whole-word capture** (glance one letter per word, dictionary-matched) — *added 2026-09-08* | OptiKey (its rarest capability) | High effort for a new interaction mode; EyeCtrl's SmallLM next-word + sentence completions attack the same keystroke economy differently — building both dilutes differentiator #2. |
| 25 | **Dynamic XML keyboards, plugin/HTTP keys, simplified letter-cluster layout, guided onboarding** — *added 2026-09-08* | OptiKey (boards/plugins/simplified); both (onboarding) | QuickActions (launchers, canned text) + the dwell SDK already cover the realistic extensibility need; struggle adaptation auto-serves accuracy-limited users; onboarding is polish, not parity. Decline unless a user asks. |

---

## Suggested implementation order (re-ranked 2026-09-22 for this owner's hardware)

Re-ranked after the owner stated the actual hardware (2026-09-22): one Tobii tracker with **no license** (→ #23 parked, see the Tier-2 note), a **single display** (→ multi-monitor #7 has no second screen to validate with), and **no adaptive switch** (→ #8 has nothing to trigger with; a caregiver-oriented global-hotkey trigger remains possible but has no switch use case here). **#7 and #8 are declined-for-this-user, not queued.** With #22 prediction controls complete on `main` (PR #160 + PR #162), the value-ranked queue for a single-display gaze-only owner:

1. **Dwell-completion audio cue** — small, direct confirmation value for a gaze-only typist.
2. **Drawing mode (#10)** — bigger, niche.
3. **Profiles (#9)** — single-user household, low.

(Robustness work that is not a competitive gap — e.g. issue #153 settings-load tolerance — is tracked in the issue queue and prioritized there.)

Tier 3 should be **declined unless a user explicitly asks** — they either duplicate existing capability (UIA snap, #14), conflict with EyeCtrl's positioning (AAC, #13), or carry cost disproportionate to value (localization, webcam, cloud, multi-key capture #24).

---

## Reference values available on this machine (re-confirmed 2026-08-04 and 2026-09-08)

The TD Control install is a useful design reference for any Tier 1/2 item. Paths verified present today:

- **exe:** `C:\Program Files (x86)\Tobii Dynavox\Computer Control\Tdx.ComputerControl.exe` (binary prefix `Tdx.`, not legacy `Tobii.ComputerControl`)
- **Settings schema:** `C:\Users\andre\AppData\Roaming\Tobii Dynavox\Computer Control\Profiles\Default\Settings\Settings.json` — dwell defaults (`ButtonDwellTime: 550`, `KeyboardDwellTime: 450`, `KeyboardPredictionDwellTime: 600`, `TraceFixationTime: 700`), action-menu button ordering, continuous/drawing-mode enums, adaptive-button mappings. Compare EyeCtrl's ~16 per-surface dwell knobs (differentiator #5).
- **Keyboard layouts:** `C:\ProgramData\Tobii Dynavox\Computer Control\Content\Keyboard\Layouts\<locale>\` — XML per locale. **14 locales confirmed present:** `de-DE, el-GR, en-US, es-MX, fr-FR, he-IL, it-IT, ja-JP, nl-NL, pl-PL, pt-BR, ru-RU, zh-CN-Sogou, zh-CN-Strokes` (EyeCtrl ships 3).
- **Word prediction:** `…\Computer Control\Word Prediction Data\` — SwiftKey `dynamic.lm` + `learned.json` adaptive model parameters.
- **Neural classifiers:** `C:\ProgramData\Tobii Dynavox\Computer Control\Content\Neural Classifiers\` — `yolov3-tiny.cfg` + `.weights` for element detection (the YOLO gaze-snap approach EyeCtrl deliberately does NOT replicate, favoring UIA instead).
- **User manual (PDF):** https://download.mytobiidynavox.com/Computer%20Control/Documentation/Users%20Manual/TobiiDynavox_UsersManual_TDControl_en-US.pdf

---

## Notable feature details from the competitors (for reference)

### OptiKey (open-source)
- Free/open-source; runs on ~$100 consumer Tobii trackers (EyeX, 4C, SteelSeries Sentry) plus TheEyeTribe, myGaze, ITU GazeTracker; also **webcam control** and **mouse-emulation fallback**.
- Combines full computer control AND AAC/speech (TTS "speak", conversation mode, phrase boards).
- Multi-key whole-word typing; magnified targeting for small UI targets; plugins; **XML-defined dynamic keyboards** for extensibility.
- Live reconfiguration via the Management Console.
- Weaker than commercial AAC on symbol-library depth, vendor support/training, out-of-the-box page sets.

### Tobii Dynavox Computer Control / TD Control (v1.80.1)
- **"Interaction First" two-step selection** — fixate target → choose action from a radial Interaction Menu; reduces accidental clicks vs. direct dwell-to-click.
- **The Trace** — gaze cursor shows a live zoomed snapshot of the target area with a crosshair (magnified precision feedback).
- **Adjust Target** — pixel-level crosshair fine-tuning within the zoomed target; three modes incl. an adaptive Auto mode that learns.
- **Radar/anchor scroll** with variable speed (proportional to distance from the scroll anchor). *[EyeCtrl now has the equivalent — `ScrollEngine`.]*
- **Drawing Mode** — fine-tuned hold-and-drag; 8-direction straight-line with catch-up; ignores sub-threshold movement. *[Still a gap for EyeCtrl — Tier 2 #10.]*
- **Continuous Mode** / **Repeat Click Mode** — lock an action to repeat without re-opening the menu; Click-on-Fixation for max speed. *[EyeCtrl now has the equivalent — locked `RepeatMode` re-firing at the live gaze point + a one-shot `RepeatLastAction` ring entry; Click-on-Fixation deliberately declined.]*
- **Gaze & Switch** hybrid — gaze pointing + switch confirmation. *[Still a gap — Tier 2 #8.]*
- **Cloud-synced SwiftKey predictions** via myTobiiDynavox account; settings sync across the Tobii Dynavox suite.
- **Gamepad Mode** — control Xbox games via gaze + Microsoft Adaptive Controller + Pretorian adapter.
- **Overlays Mode (I-Series only)** — custom eye-gaze hotspots for specific web pages/apps.
- **14 keyboard languages** incl. CJK (Sogou pinyin + stroke), Japanese (romaji + predictions), Hebrew, Greek, Arabic (vowel indicators). *[EyeCtrl ships 3 Latin layouts — Tier 2 #12.]*
- Locked to Tobii Dynavox hardware (I-Series I-13/I-16, PCEye 5); does NOT support third-party trackers. **Single display only** (multi-monitor not supported — same limitation EyeCtrl has today, Tier 2 #7).
- No blink selection (explicitly rejected). No software gaze-to-wake (I-Series has a hardware Wake-On-Gaze BIOS feature).

---

## Change-log

- **2026-09-25 (gap #11 implemented):** `feat/accent-input` ships the diacritic gap as per-layout `accents` sub-layouts with auto-return (QWERTZ 28-key French-in-German set, AZERTY 20-key tail, zero-move `Acc` keys at the letters row-0 right end) — landing geometry pinned by real-window probe tests at effective scale 2.0; the swap-landing suppression applies to all layouts' same-slot switch pairs (123↔ABC), QWERTY included — an immediate back-dwell at the swap spot needs a look that lands on another key (a glance at the text or the prediction bar does not clear it); the resting misfire it removes was the point. Tier-2 row 11 records the branch as **implemented, pending merge**; the dead-key composition engine is declined as the revisit path (spec D1 of `docs/superpowers/specs/2026-09-24-accent-input-design.md`). Queue re-ranked: #11 leaves the head, "Dwell-completion audio cue" takes it.

- **2026-09-22 (queue re-rank for owner hardware):** Gap #23 per-eye tracking moved out of the Tier-2 table to **Parked** — the implementation is complete on branch `feat/tracked-eye` but license-gated (`tobii_set_enabled_eye` → `TOBII_ERROR_INSUFFICIENT_LICENSE`, field-validated 2026-09-19). #7 multi-monitor and #8 switch input marked **declined-for-this-user** in the queue (single display, no switch hardware — stated by the owner 2026-09-22). #22 prediction controls recorded **complete** (slice 1 merged as PR #160, add-word key + dictionary page as PR #162) and moved to Resolved; Tier 1 is now empty. Queue re-ranked around what a single-display gaze-only owner actually touches (typing/prediction, dwell quality, settings robustness, eye care).

- **2026-09-08 (b — gap #4 implemented):** `feat/repeat-click-mode` (stacked on PR #138's `feat/mouse-osk-polish`) closes the last original Tier-1 gap. Core: `InteractionState.RepeatMode` + `InteractionManager` last-click memory (`LastRepeatableAction`), locked `RepeatAction`, guarded `FireRepeatTick`/`ExitRepeatMode`, `TogglePause` blocked while repeating, `InteractionActionExtensions.IsRepeatableClick` classification; `RepeatLastAction` converts to the remembered click inside `OnActionSelected` so dispatch stays on the single `ActionRequested` path. App: `RepeatModeController` (stop button ⏹ on the right edge, `DispatcherTimer` ticking the locked click at `GazeEngine.StableCentroid` every `RepeatSettings.IntervalMs`, VK_ESCAPE hatch), `GazeEngine` cursor-follow + `CursorCircleViewModel` visibility extended to RepeatMode (the circle is the mode's only aiming feedback). Settings: `RepeatSettings` (IntervalMs 250–5000 default 1000, exposed in Advanced; StopButtonDwellMs unexposed like scroll-stop). Ring: two new `InteractionAction` members (values 11/12, appended), renewal-arrow mouse icons, labels/descriptions — **both OFF by default** (layer-slot symmetry). Design ledger: `docs/superpowers/specs/2026-09-08-repeat-click-mode-design.md` (D1–D15).
- **2026-09-08 (refresh + first implementation slice):** Third run, same method — parallel read-only sweeps of the EyeCtrl code (src + tests, every claim path-cited), the live TD Control v1.80.1 install (full `Settings.json`, keyboard layout XMLs + language packs, curated binary-strings feature hunt), and the OptiKey README + ~25 wiki pages. Findings: **#2 shipped to `main`** (`WindowAction` + `Win32WindowManager` + presets) — moved to Resolved; `ShowDesktop` intentionally skipped (Win+D reachable via the OSK's Win modifier). **The mouse/OSK polish bundle (#21) was implemented the same day** on `feat/mouse-osk-polish` (`2ded5589`): middle click (OFF by default to preserve layer slot symmetry), `deleteWord` key, Fn clipboard row via the new `shortcut:<chord>` grammar, shared `KeyboardShortcut` parser, `ShippedLayoutFilesTests` grammar guard. New gaps ranked: #22 prediction controls (Tier 1), #23 per-eye tracking (Tier 2), #24 multi-key capture + #25 misc declines (Tier 3). All suites green after the slice: Core 1224 / ViewModels 307 / Platform 88 / Controls 299 / App non-UI 821 / Supervisor 79. `detect_changes` flagged HIGH — expected: the changed flows all pass through the `OnKeyDwellCompleted`/`KeyboardViewModel` hubs, touched only with additive branches.
- **2026-08-04 (refresh):** Re-verified every differentiator and gap against `main` @ `b590a87` and the working tree (`fix/calibration-identity-and-toolbar-space` @ `6b98ca0`). Findings: differentiators #1–#5 all still present (per-surface dwell granularity in #5 is even stronger than first noted — ~16 knobs vs TD Control's 4). **Gaps #1, #3, #5, #6 have shipped to `main`** — moved to "Resolved". **#2 partially resolved** (Task Switcher + Quick Actions grids landed; per-window min/max/close actions still open). **#4 remains the only open Tier-1 item.** Tier-2 #7–#12 and Tier-3 #13–#20 re-confirmed open as described (YOLO/CV absence re-confirmed for #14). In-flight (unmerged) relevant branches: `feat/quick-actions-editor`, `feat/quick-actions-toolbar`, `feat/struggle-detection-phase1a`, `feat/struggle-detection-phase1b`. Implementation order updated to reflect the shorter queue. TD Control install paths re-confirmed present.
- **2026-07-11 (original):** First analysis from OptiKey README/wiki + TD Control v1.80.1 install + EyeCtrl code. 6 Tier-1 gaps, 6 Tier-2, 8 Tier-3.
