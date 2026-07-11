# Competitive Feature Analysis — OptiKey & Tobii Dynavox Computer Control vs. EyeCtrl

**Date:** 2026-07-11
**Purpose:** Reference for prioritizing feature work. Inventories what OptiKey and Tobii Dynavox Computer Control (TD Control) offer, what EyeCtrl has today, and ranks the gaps by sensibility to integrate.

**Sources inspected:**
- **OptiKey** — open-source (GitHub: OptiKey/OptiKey). README, wiki, supported-trackers page.
- **Tobii Dynavox Computer Control / TD Control** — online docs (TD Control v1.2 User's Manual, v1.0 manual, release notes v1.0–v1.80.1) **plus the live v1.80.1 installation on this machine** at `C:\Program Files (x86)\Tobii Dynavox\Computer Control\Tdx.ComputerControl.exe` (settings schema at `%AppData%\Tobii Dynavox\Computer Control\Profiles\Default\Settings\Settings.json`, keyboard language packs in `%ProgramData%\Tobii Dynavox\Computer Control\Content\Keyboard\`).
- **EyeCtrl** — codebase read-only inspection (Core, Platform, ViewModels, App, EyeCare, LM).

---

## EyeCtrl's existing differentiators (protect these)

These are capabilities the two competitors lack — EyeCtrl's strategic position rests on them:

1. **Eye-care subsystem (`EyeCtrl.EyeCare`)** — break scheduler (20-20-20 + natural-break suppression + snooze/hold), fatigue monitor (rolling score, opt-in escalation), 5 scored gaze-tracked exercises (20-20-20, Directional Sweeps, Near-Far Focus, Saccade Jumps, Smooth Pursuit), summary scoring. Fully wired into the live gaze stream. Neither OptiKey nor TD Control has any break/fatigue/exercise feature.
2. **ONNX SmallLM word prediction** — pluggable SmolLM2/Qwen2.5/Phi-3.5 next-word models with GPU/DirectML and per-user custom model entries. More advanced than OptiKey's dictionary; on par with TD Control's SwiftKey.
3. **SDK for cooperating apps** (`EyeCtrl.Sdk.Wpf` / `Sdk.WinUI` + `ExternalDwellService` named-pipe server) — third-party apps register their own controls as dwellable.
4. **UIAutomation snap-to-control** (`ControlFinder`) — gaze-snap to the nearest interactive control via the UIA tree (TD Control's analog is YOLO/CV-based; different mechanism, similar end).
5. **Granular per-surface dwell times** — separate dwell for activation/menu/keyboard/predictions/drag/scroll/etc.; actually finer-grained than TD Control's.

---

## Gap inventory — ranked by sensibility to integrate into EyeCtrl

### Tier 1 — High sensibility (low–moderate effort, high value, fits architecture)

| # | Gap | Source | Rationale |
|---|---|---|---|
| 1 | **Auto-launch on startup (currently a no-op toggle)** | Both | `GeneralSettings.StartOnBoot` exists but no code writes the registry Run key. ~30 lines to make it real. |
| 2 | **Window/app-management actions** (minimize, maximize, close, Alt-Tab app switcher, taskbar/dock access, Show Desktop) | Both (TD Control has TD Switcher; OptiKey has a full app/window bar) | Action menu already dispatches `Win32InputSimulator` actions; `SendModifiedKeyPress` already handles the Win key. Natural extension. |
| 3 | **Track Status box** (real-time eye-position + distance indicator) | TD Control | Tobii stream exposes eye position + distance; small click-through overlay; high reassurance value for calibration/troubleshooting. |
| 4 | **Continuous / Repeat-Click modes** (lock an action so it repeats without reopening the action menu) | TD Control | `InteractionManager` state machine is the natural home; big usability win for games/photo-advancing/design. |
| 5 | **Scroll: radar/anchor-based variable speed** | TD Control | `ScrollEngine` already has dead-zone + proportional velocity; anchor variant is incremental. |
| 6 | **Adaptive dwell** (shorten dwell as the user gets faster) | TD Control | EyeCtrl already tracks per-surface dwell progress; rolling-success-rate adjustment is contained; complements the eye-care work. |

### Tier 2 — Medium sensibility (moderate–high effort, solid value, on-mission)

| # | Gap | Source | Notes |
|---|---|---|---|
| 7 | **Multi-monitor gaze** | Both | Hard-coded to primary monitor (`Win32InputSimulator` uses `SM_CXSCREEN`; comment notes `MOUSEEVENTF_VIRTUALDESK` would fix it). Coordinate remap + virtual-desk flag + Tobii provider sizing. |
| 8 | **Physical switch / gaze+switch hybrid input** | Both (TD Control "Gaze & Switch"; OptiKey supports adaptive switches) | Serves users with unsteady gaze; switch-confirm path in `InteractionManager`. |
| 9 | **Multiple named user profiles** | TD Control (cloud-synced single account) | `SettingsService` already validates JSON; add a profile picker + per-profile files; no cloud needed. |
| 10 | **Drawing Mode** (free-path hold-and-drag with intentional-movement threshold + straight-line catch-up) | TD Control | `Drag` action already does `MouseDown` + gaze-follow; 8-direction lock + jitter threshold is incremental. |
| 11 | **Dead-key / diacritic composition** (type `´` then `e` → `é`) | Both | EyeCtrl emits Unicode directly today; per-layout dead-key tables unlock more languages cheaply. |
| 12 | **More keyboard languages** (TD Control ships 14 incl. CJK/IME) | Both | EyeCtrl has QWERTY/AZERTY/QWERTZ. Latin-script additions are easy via JSON; **CJK/IME is a large separate project** — keep out of scope. |

### Tier 3 — Lower sensibility for EyeCtrl (high effort, or off-mission)

| # | Gap | Source | Why deprioritize |
|---|---|---|---|
| 13 | **AAC / TTS / communication boards** ("Speak", phrase boards, symbol libraries) | OptiKey (core); TD Control has a Notepad only | Big feature, **different mission**. EyeCtrl = computer-control + eye-care, not a communication aid. TTS on the Notes feature would be the thin end of the wedge if ever wanted. |
| 14 | **AI/vision element detection (YOLO/CV gaze-snap)** | TD Control ships `yolov3-tiny.weights` + `Tdx.ElementDetection.dll` | EyeCtrl already covers this via UIAutomation (`ControlFinder`). CV path only helps for non-accessible apps; high effort, marginal gain. |
| 15 | **Localization (UI languages)** | Both (TD Control: full UI localization, 14 keyboard langs) | Broad, tedious, low short-term strategic value; English UI + 5 prediction-dictionary languages is reasonable. |
| 16 | **Webcam-based gaze** | OptiKey | Low accuracy, large effort (model + camera pipeline). Tobii+mouse positioning is fine. |
| 17 | **Non-Tobii tracker SDKs** (EyeTribe, myGaze, etc.) | OptiKey | Tobii Stream Engine + mouse fallback covers the realistic install base. Diminishing returns. |
| 18 | **Cloud settings/prediction sync** | TD Control (myTobiiDynavox) | Infrastructure + privacy burden; local-JSON is fine for a single-user tool. |
| 19 | **Blink selection** | (neither major app; niche) | TD Control explicitly rejects blink. Low value, tricky accuracy. Skip. |
| 20 | **Gaze-to-wake from sleep** | TD Control (I-Series hardware only) | Hardware/BIOS-level; not reliably achievable in software. Skip. |

---

## Suggested roadmap

1. **Fix auto-launch (#1)** — trivial, ships value immediately.
2. **Window/app-management actions + app switcher (#2)** — biggest perceived-feature jump for modest work; reuses `Win32InputSimulator` + action menu.
3. **Track Status box (#3) + Continuous/Repeat-Click modes (#4)** — polish that brings EyeCtrl to TD Control's day-to-day usability tier.
4. **Multi-monitor (#7)** — removes a documented hard limitation.
5. Then choose between **profiles (#9)**, **switch input (#8)**, or **drawing mode (#10)** based on user need.

Tier 3 items should be **declined unless a user explicitly asks** — they either duplicate existing EyeCtrl capability (UIA snap, #14), conflict with its eye-care/computer-control positioning (AAC, #13), or carry cost disproportionate to value (localization, webcam, cloud).

---

## Reference values available on this machine

The TD Control install on this machine is a useful design reference for any Tier 1/2 item:

- **Settings schema:** `C:\Users\andre\AppData\Roaming\Tobii Dynavox\Computer Control\Profiles\Default\Settings\Settings.json` — reveals dwell defaults (`ButtonDwellTime: 550`, `KeyboardDwellTime: 450`, `KeyboardPredictionDwellTime: 600`, `TraceFixationTime: 700`), action-menu button ordering, continuous/drawing-mode enums, adaptive-button mappings, etc.
- **Keyboard layouts:** `C:\ProgramData\Tobii Dynavox\Computer Control\Content\Keyboard\Layouts\<locale>\<locale>_Qwerty.xml` — XML layout definitions per locale (14 locales incl. CJK).
- **Word prediction:** `%AppData%\Tobii Dynavox\Computer Control\Word Prediction Data\` — SwiftKey `dynamic.lm` + `learned.json` adaptive model parameters.
- **Neural classifiers:** `C:\ProgramData\Tobii Dynavox\Computer Control\Content\Neural Classifiers\` — `yolov3-tiny.cfg` + `.weights` for element detection (the YOLO gaze-snap approach EyeCtrl deliberately does NOT replicate, favoring UIA instead).
- **Pending update staged:** `C:\ProgramData\Tobii Dynavox\Computer Control\Downloads\Update\` — full second copy of app binaries (v1.80.1.5066).

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
- **Radar/anchor scroll** with variable speed (proportional to distance from the scroll anchor).
- **Drawing Mode** — fine-tuned hold-and-drag; 8-direction straight-line with catch-up; ignores sub-threshold movement.
- **Continuous Mode** / **Repeat Click Mode** — lock an action to repeat without re-opening the menu; Click-on-Fixation for max speed.
- **Gaze & Switch** hybrid — gaze pointing + switch confirmation.
- **Cloud-synced SwiftKey predictions** via myTobiiDynavox account; settings sync across the Tobii Dynavox suite.
- **Gamepad Mode** — control Xbox games via gaze + Microsoft Adaptive Controller + Pretorian adapter.
- **Overlays Mode (I-Series only)** — custom eye-gaze hotspots for specific web pages/apps.
- **14 keyboard languages** incl. CJK (Sogou pinyin + stroke), Japanese (romaji + predictions), Hebrew, Greek, Arabic (vowel indicators).
- Locked to Tobii Dynavox hardware (I-Series I-13/I-16, PCEye 5); does NOT support third-party trackers. **Single display only** (multi-monitor not supported, same as EyeCtrl today).
- No blink selection (explicitly rejected). No software gaze-to-wake (I-Series has a hardware Wake-On-Gaze BIOS feature).
