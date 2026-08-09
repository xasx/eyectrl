# Competitive Feature Analysis — OptiKey & Tobii Dynavox Computer Control vs. EyeCtrl

**Date:** 2026-07-11 · **Refreshed:** 2026-08-04 (verified against `main` + `fix/calibration-identity-and-toolbar-space`)
**Purpose:** Reference for prioritizing feature work. Inventories what OptiKey and Tobii Dynavox Computer Control (TD Control) offer, what EyeCtrl has today, and ranks the gaps by sensibility to integrate.

**Method:** Verify EyeCtrl's *actual* state by reading the code (not assuming) — every "already present" / "resolved" claim cites a file on `main`. Candidate features are grouped, then ranked into tiers by (a) value to the end user, (b) implementation effort in this codebase (with the likely seam named), (c) fit with EyeCtrl's mission (computer-control + eye-care). AAC/communication boards and full UI localization are explicitly de-scoped unless cheap.

**Sources inspected:**
- **OptiKey** — open-source (GitHub: OptiKey/OptiKey). README, wiki, supported-trackers page.
- **Tobii Dynavox Computer Control / TD Control** — online docs (TD Control User's Manual, release notes v1.0–v1.80.1) **plus the live v1.80.1 installation on this machine** at `C:\Program Files (x86)\Tobii Dynavox\Computer Control\Tdx.ComputerControl.exe` (settings schema at `%AppData%\Tobii Dynavox\Computer Control\Profiles\Default\Settings\Settings.json`, keyboard language packs in `%ProgramData%\Tobii Dynavox\Computer Control\Content\Keyboard\`).
- **EyeCtrl** — codebase read-only inspection (Core, Platform, ViewModels, App, EyeCare, LM).

> **How to read this refresh:** the 2026-07-11 version listed 6 Tier-1 gaps. Four of them shipped to `main` since (auto-launch, track-status box, radar/anchor scroll, adaptive dwell) and are moved to §"Resolved since 2026-07-11" below with ship evidence. The tiered table in §Gap inventory reflects only what is **still open** as of 2026-08-04. See the end for the change-log.

---

## EyeCtrl's differentiators — keep & protect

Capabilities the two competitors lack. EyeCtrl's strategic position rests on them; verify presence before any adjacent refactor.

1. **Eye-care subsystem (`src/EyeCtrl.EyeCare/`)** — `EyeCareScheduler` (20-20-20 + natural-break suppression + snooze/hold), `FatigueMonitor` (rolling score, opt-in escalation), `ExerciseRunner` + 5 scored gaze-tracked exercises (20-20-20, Directional Sweeps, Near-Far Focus, Saccade Jumps, Smooth Pursuit), `IEyeCareService`. Fully wired into the live gaze stream. **Neither OptiKey nor TD Control has any break/fatigue/exercise feature** — the single biggest differentiator.
2. **ONNX SmallLM word prediction (`src/EyeCtrl.LM/`)** — pluggable SmolLM2/Qwen2.5/Phi-3.5 next-word models via `SmallLMPredictor` + `LMTokenizer`, GPU/DirectML, per-user custom model entries. More advanced than OptiKey's dictionary; on par with TD Control's SwiftKey.
3. **Cooperating-app SDK (`src/EyeCtrl.Sdk.Abstractions/`, `Sdk.Wpf/`, `Sdk.WinUI/`) + `src/EyeCtrl.App/Services/ExternalDwellService.cs`** named-pipe server — third-party apps register their own controls as dwellable. Unique to EyeCtrl.
4. **UIAutomation snap-to-control (`src/EyeCtrl.Platform/Services/ControlFinder.cs`)** — gaze-snap to the nearest interactive control via the UIA tree (`AutomationElement`). EyeCtrl's analog of TD Control's YOLO/CV element-detection, via a different (and dependency-free) mechanism.
5. **Granular per-surface dwell times (`src/EyeCtrl.Core/Settings/DwellSettings.cs`, `DragSettings.cs`)** — ~16 independent per-surface dwell knobs (activation button, action menu, manual-selection action menu, toolbar, keyboard key, prediction, drag release, drag right-edge, scroll stop, manual-selection side, backdrop hide, …) each with its own validated range, plus a separate dwell set under `DragSettings`. **Finer-grained than TD Control**, which exposes only four (`ButtonDwellTime` 550, `KeyboardDwellTime` 450, `KeyboardPredictionDwellTime` 600, `TraceFixationTime` 700).

---

## Resolved since 2026-07-11 (previously listed as gaps — now on `main`)

These were Tier-1 gaps in the original analysis; all have shipped. Listed here so the history is preserved and the still-open table isn't cluttered.

| # | Was a gap | Resolution (file on `main`) |
|---|-----------|------------------------------|
| 1 | Auto-launch on startup was a no-op toggle | **Implemented.** `WindowsStartupRegistrar` writes the HKCU `…\CurrentVersion\Run` "EyeCtrl" value; `IStartupRegistrar` is the seam; `StartOnBootPolicy` suppresses self-registration under supervised mode (the supervisor owns launch-at-logon then). `Environment.ProcessPath` is used (not `Assembly.Location`) so the single-file bundle works. |
| 3 | No track-status box | **Implemented.** `TrackStatusWindow` + `TrackStatusViewModel` render a top-right click-through overlay: two eye dots in a track-box canvas, head distance (mm) + a Good/Fair/Poor quality triad via palette tokens, polled at ~15 Hz. Reacts to validity changes, not just X/Y. |
| 5 | Scroll was discrete notch actions only | **Implemented (radar/anchor variable-speed).** `ScrollEngine` is a gaze-joystick: dead-zone (`DeadZoneRadiusPixels = 30`) around an `anchor` point → proportional velocity → clamp at `MaxVelocityNotchesPerSec = 60`; `ScrollModeController` mounts it on the right edge (`RightEdgeAnchor`). This **is** TD Control's radar/anchor variant. |
| 6 | No adaptive dwell | **Implemented.** Full Adaptation subsystem: `EyeCtrl.Core/Adaptation/` (`AdaptationController`, `StruggleMonitor`, `AdaptationKnob`, `EffectiveSettingsProvider`, `EffectiveValue`, `FailureModeAssessment`, `DwellContext`/`DwellOutcome`, `BufferingTelemetrySink`) + App-side `AdaptationCoordinator`, `AdaptationActivation`, `AdaptationCueService`, `AdaptationLog`. Struggle evidence re-tunes effective dwell settings. (In-flight `feat/struggle-detection-phase1a/1b` add per-surface telemetry wiring + cue/drift overlays on top of this core.) |

---

## Gap inventory — still open, ranked by sensibility to integrate

### Tier 1 — High sensibility (still open)

| # | Gap | Source | Rationale + likely seam |
|---|---|---|---|
| 2 | **Window-management *actions*** — minimize / maximize / restore / close / Show Desktop on the window under the gaze point. (App-*switching* is already done — see partial note below.) | Both | The `InteractionAction` enum (`src/EyeCtrl.Core/Models/InteractionAction.cs`) currently holds only LeftClick/RightClick/DoubleClick/Drag/Scroll*/PinNote. Add `Minimize`/`Maximize`/`Restore`/`Close`/`ShowDesktop`; resolve the target window via `ControlFinder`/UIA `WindowPattern` and dispatch alongside the existing mouse actions. `Win32InputSimulator.SendModifiedKeyPress` already handles Win/Alt, and there is no registry/IPC work — pure enum + handlers + `ActionMenuSettings` defaults. |
| 4 | **Continuous / Repeat-Click modes** (lock an action — click, scroll, drag — so it repeats without reopening the action menu) | TD Control | `InteractionManager` (`src/EyeCtrl.Core/Processing/InteractionManager.cs`) is the state machine; add a locked-repeat state alongside `ScrollMode`/`Dragging`. Big usability win for games, photo-advancing, design apps. |

> **Partial note on #2 (app switching already landed):** a fullscreen **Task Switcher** (`src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml.cs` — a grid of tiles, one per open window, icons via `IAppIconProvider`) and a **Quick Actions grid** (`QuickActionsGridWindow`) are already on `main`. So "switch to app X" is covered; what's still missing is the per-window *action* (min/max/close that specific window). The in-flight `feat/quick-actions-editor`/`feat/quick-actions-toolbar` branches extend the quick-actions surface but are **not merged**.

### Tier 2 — Medium sensibility (moderate–high effort, solid value, on-mission)

| # | Gap | Source | Notes + likely seam |
|---|---|---|---|
| 7 | **Multi-monitor gaze** | Both | Still hard-coded to the primary monitor. `Win32InputSimulator` uses `SM_CXSCREEN`/`SM_CYSCREEN`; the doc-comment at `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs:12-15` explicitly notes `MOUSEEVENTF_VIRTUALDESK` + virtual-screen metrics are the fix. Needs that flag + a coordinate remap + Tobii provider sizing. Confirmed still open on `main`. |
| 8 | **Physical switch / gaze+switch hybrid input** | Both (TD Control "Gaze & Switch"; OptiKey supports adaptive switches) | Serves users with unsteady gaze. Switch-confirm path in `InteractionManager`. Confirmed not on `main`. |
| 9 | **Multiple named user profiles** | TD Control | `SettingsService` already validates/persists JSON; add a profile picker + per-profile files. No cloud needed. Confirmed not on `main`. |
| 10 | **Drawing Mode** — free-path hold-and-drag with intentional-movement threshold + 8-direction straight-line catch-up | TD Control | The basic `Drag` action exists (`InteractionAction.Drag` → `MouseDown` + gaze-follow), but there is no 8-direction lock, jitter threshold, or straight-line catch-up. Incremental on the drag path. Confirmed not on `main`. |
| 11 | **Dead-key / diacritic composition** (`´` then `e` → `é`) | Both | EyeCtrl emits Unicode directly via `Win32InputSimulator.SendText`; per-layout dead-key tables unlock more languages cheaply. Confirmed not on `main`. |
| 12 | **More keyboard languages** (TD Control ships 14 incl. CJK/IME) | Both | EyeCtrl ships 3 JSON layouts: `qwerty`/`azerty`/`qwertz` (`src/EyeCtrl.App/Resources/Layouts/`). Latin-script additions are easy via JSON; **CJK/IME is a large separate project — keep out of scope.** |

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

---

## Suggested implementation order (refreshed)

With #1/#3/#5/#6 already shipped, the near-term queue is short:

1. **Window-management actions (#2 remainder)** — add `Minimize`/`Maximize`/`Restore`/`Close`/`ShowDesktop` to `InteractionAction` + UIA `WindowPattern` resolution + `ActionMenuSettings` defaults. Biggest perceived-feature jump for modest work; reuses `Win32InputSimulator` + `ControlFinder`.
2. **Continuous / Repeat-Click modes (#4)** — `InteractionManager` locked-repeat state; high day-to-day usability.
3. **Multi-monitor (#7)** — removes a documented hard limitation (`Win32InputSimulator` already names the fix).
4. Then choose between **profiles (#9)**, **switch input (#8)**, or **drawing mode (#10)** based on user need.

Tier 3 should be **declined unless a user explicitly asks** — they either duplicate existing capability (UIA snap, #14), conflict with EyeCtrl's positioning (AAC, #13), or carry cost disproportionate to value (localization, webcam, cloud).

---

## Reference values available on this machine (re-confirmed 2026-08-04)

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
- **Continuous Mode** / **Repeat Click Mode** — lock an action to repeat without re-opening the menu; Click-on-Fixation for max speed. *[Still a gap — Tier 1 #4.]*
- **Gaze & Switch** hybrid — gaze pointing + switch confirmation. *[Still a gap — Tier 2 #8.]*
- **Cloud-synced SwiftKey predictions** via myTobiiDynavox account; settings sync across the Tobii Dynavox suite.
- **Gamepad Mode** — control Xbox games via gaze + Microsoft Adaptive Controller + Pretorian adapter.
- **Overlays Mode (I-Series only)** — custom eye-gaze hotspots for specific web pages/apps.
- **14 keyboard languages** incl. CJK (Sogou pinyin + stroke), Japanese (romaji + predictions), Hebrew, Greek, Arabic (vowel indicators). *[EyeCtrl ships 3 Latin layouts — Tier 2 #12.]*
- Locked to Tobii Dynavox hardware (I-Series I-13/I-16, PCEye 5); does NOT support third-party trackers. **Single display only** (multi-monitor not supported — same limitation EyeCtrl has today, Tier 2 #7).
- No blink selection (explicitly rejected). No software gaze-to-wake (I-Series has a hardware Wake-On-Gaze BIOS feature).

---

## Change-log

- **2026-08-04 (refresh):** Re-verified every differentiator and gap against `main` @ `b590a87` and the working tree (`fix/calibration-identity-and-toolbar-space` @ `6b98ca0`). Findings: differentiators #1–#5 all still present (per-surface dwell granularity in #5 is even stronger than first noted — ~16 knobs vs TD Control's 4). **Gaps #1, #3, #5, #6 have shipped to `main`** — moved to "Resolved". **#2 partially resolved** (Task Switcher + Quick Actions grids landed; per-window min/max/close actions still open). **#4 remains the only open Tier-1 item.** Tier-2 #7–#12 and Tier-3 #13–#20 re-confirmed open as described (YOLO/CV absence re-confirmed for #14). In-flight (unmerged) relevant branches: `feat/quick-actions-editor`, `feat/quick-actions-toolbar`, `feat/struggle-detection-phase1a`, `feat/struggle-detection-phase1b`. Implementation order updated to reflect the shorter queue. TD Control install paths re-confirmed present.
- **2026-07-11 (original):** First analysis from OptiKey README/wiki + TD Control v1.80.1 install + EyeCtrl code. 6 Tier-1 gaps, 6 Tier-2, 8 Tier-3.
