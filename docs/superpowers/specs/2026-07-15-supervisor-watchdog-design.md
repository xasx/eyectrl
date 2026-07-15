# EyeCtrl Supervisor (Watchdog) — Design Spec

**Date:** 2026-07-15
**Status:** Draft
**Type:** Reliability / new component
**Author:** EyeCtrl Team
**Reference:** brainstorming session 2026-07-15

## Problem

EyeCtrl is, for its target users, *primary input software* — the only way some users can click, type, or launch anything. If the EyeCtrl process dies, the user is stranded: they cannot click anything (including any "restart EyeCtrl" dialog) without a caregiver. Today the app has **zero process-level supervision**:

- No watchdog, no respawn logic. A crash = total loss of control until manual relaunch or next logon, and only if the user enabled the off-by-default autostart.
- `DispatcherUnhandledException` logs but **never sets `Handled = true`** ([App.xaml.cs:339](src/EyeCtrl.App/App.xaml.cs:339)) — log-then-crash. (An `AppDomain.CurrentDomain.UnhandledException` handler *already exists* at [App.xaml.cs:332](src/EyeCtrl.App/App.xaml.cs:332) and already `CloseAndFlush()`es — so in-app change #3 is narrower than "add global handlers": only the Dispatcher decision and the `TaskScheduler.UnobservedTaskException` handler are net-new.)
- No `TaskScheduler.UnobservedTaskException` handler — fire-and-forget task faults are invisible.
- No single-instance guard — a respawn could create a duplicate process fighting over the gaze device, the `eyectrl_dwell` named pipe, and the settings file.
- No handling for `OnSessionEnding` / `PowerModeChanged` (logoff, sleep, Windows Update reboot).
- `IGazeProvider.IsConnected` and the gaze-sample stream feed only UI; nothing detects "no samples for N seconds = something is wrong."

Provider-level resilience is already strong (`TobiiStreamEngineProvider` reconnects indefinitely with exponential backoff, falls back to mouse-gaze on missing hardware, never crashes on missing hardware). The gap is purely at the *process* level.

## Goals

A companion supervisor process that keeps EyeCtrl running and recoverable for users who depend on it. Concretely, recover automatically from four failure modes:

1. **Process crash / hard exit** — EyeCtrl terminates unexpectedly → supervisor relaunches it.
2. **Process hung / frozen but alive** — UI thread frozen, deadlock, no gaze samples → supervisor detects via heartbeat timeout, force-kills, relaunches.
3. **System reboot / Windows Update** — EyeCtrl comes back at the next logon automatically.
4. **Tracker lost / gaze stream dead** — heartbeat reports no gaze samples despite a supposedly-connected tracker → supervisor kills + restarts the app to re-trigger its reconnect logic.

And a critical safety guarantee:

5. **Never strand the user.** If EyeCtrl is in a crash loop, the supervisor launches a fallback assistive-technology (AT) app so the user retains eye control while EyeCtrl is down. All supervisor UI is itself dwell-operable, because EyeCtrl may be the thing that's broken when that UI is shown.

## Non-Goals

- **Not a Session 0 Windows Service.** A real Windows Service runs in Session 0, which cannot launch a WPF app into the user's interactive desktop without `CreateProcessAsUser` token gymnastics. The supervisor lives in the user's interactive session instead. (See "Alternatives considered.")
- **Not auto-return from fallback.** Once the supervisor has launched a fallback AT (FALLEN_BACK state), it stops retrying EyeCtrl for the rest of the session. No return path — see State Machine.
- **Not a full eye-control environment.** The supervisor's dwell capability covers only its own UI (setup wizard, crash dialogs). No zoom, no keyboard, no overlays — that remains the app's job.
- **No telemetry / cloud reporting.** Local logs only (Serilog, same pattern as the app).
- **No installer authoring in this spec.** The spec defines *what* the Scheduled Task must contain; the actual installer (.msi / .msix / .exe bootstrapper) is a separate workstream. This spec covers the supervisor exe and the in-app changes.

## Terminology

- **Supervisor** — `EyeCtrl.Supervisor.exe`, the new companion process.
- **App** — the existing `EyeCtrl.App` WPF application.
- **Heartbeat** — a small JSON payload the app writes to the supervisor's named pipe every ~3s while healthy.
- **Crash window** — a sliding time window (default 2 minutes) used to count restarts and detect a crash loop.
- **Fallback AT** — a third-party or built-in eye-control app launched when the app is in a crash loop (TD Control, Snap Core First, Windows Eye Control).
- **Standalone mode** — current behavior; no supervisor running.
- **Supervised mode** — supervisor installed and enabled; owns app launch-at-logon.

## Architecture

### Topology

```
┌─────────────────────────────────────────────────────────────┐
│  Windows interactive session (user logon)                    │
│                                                              │
│  Scheduled Task "EyeCtrl Supervisor"                         │
│  ├─ trigger: At logon                                        │
│  └─ native "restart on failure" (3×) — outermost safety net  │
│                                                              │
│  ┌────────────────────────────┐   heartbeat     ┌──────────┐ │
│  │ EyeCtrl.Supervisor         │ ◀────────────── │EyeCtrl.App│ │
│  │  (WinExe, headless+small UI)│  named pipe    │ (WPF)     │ │
│  │                            │  eyectrl_watch  │           │ │
│  │  • process watch           │                 │ emits HB  │ │
│  │  • hang detect (HB timeout)│  ─── launch ──▶ │ + Mutex   │ │
│  │  • crash-loop escalation   │                 │ + Xcptn   │ │
│  │  • fallback AT launch      │                 │   handlers│ │
│  │  • own dwell UI (Core+Plat+Ctrl)│             └──────────┘ │
│  └────────────────────────────┘                              │
│          │ on crash-loop                                     │
│          ▼                                                   │
│  Fallback AT: TD Control → Snap Core First → Win Eye Control │
└─────────────────────────────────────────────────────────────┘
```

### New projects

Two new projects, because the supervisor's mandatory dwell UI depends on WPF controls that currently live in the wrong place.

**`src/EyeCtrl.Controls`** (new, prerequisite) — `net10.0-windows`, `UseWPF`, class library. ⚠ The dwell WPF controls the supervisor must reuse — `DwellButton`, `DwellControl`, `DwellArcButton`, `DwellSlider`, `DwellComboBox`, `DwellToggle`, `DwellLabel`, `ColorBlend`, `IDwellComposite` — all currently live in [`src/EyeCtrl.App/Controls/`](src/EyeCtrl.App/Controls/) (namespace `EyeCtrl.App.Controls`). `EyeCtrl.Core` and `EyeCtrl.Platform` contain **zero** `.xaml` and zero dwell controls — only the framework-agnostic dwell *logic* (`DwellDetector`, `DwellTarget`, `IDwellRegistrar` in `EyeCtrl.Core.Processing`). A supervisor referencing only Core+Platform **cannot** get a `DwellButton` under the current project graph. The fix: move these control files into `EyeCtrl.Controls` (namespace `EyeCtrl.Controls`), referencing `EyeCtrl.Core` (their only non-WPF dependency is `EyeCtrl.Core.Processing`). The controls are code-built C# (no `.xaml`), so this is a file move + namespace change. Both `EyeCtrl.App` and `EyeCtrl.Supervisor` then reference `EyeCtrl.Controls`.

**`src/EyeCtrl.Supervisor`** (new) — `net10.0-windows`, `OutputType=WinExe` (no console window), `UseWPF` (for its setup/dialog windows and the dwell controls). **No** `UseWindowsForms` except for a `NotifyIcon` tray (matching `TrayIconManager` in the app). References **`EyeCtrl.Core`** (`DwellDetector`, `OneEuroFilter`, `GazePoint`), **`EyeCtrl.Platform`** (`GazeProviderFactory`, `TobiiStreamEngineProvider`, `MouseGazeProvider`, and the Win32 P/Invoke seam for pipe client identity / pipe ACLs / process queries), and **`EyeCtrl.Controls`** (`DwellButton` et al.). Reuses gaze/dwell logic verbatim.

Both new projects are added to `EyeCtrl.slnx`.

### Two run modes

| Mode | Who starts the app at logon | Supervisor runs at logon? |
|---|---|---|
| **Standalone** (supervisor disabled/uninstalled) | eyectrl's HKCU Run key (`WindowsStartupRegistrar`), gated by `StartOnBoot` | No |
| **Supervised** (supervisor enabled) | Supervisor (gated by `StartOnBoot`) | **Always** — supervisor runs at every logon to watch the app even when `StartOnBoot` is off |

Reconciliation rule (single source of truth): **when supervised mode is active, the supervisor owns the launch-at-logon job and eyectrl's HKCU Run key must be cleared; when supervised mode is off, the Run key reflects `StartOnBoot` as today.**

⚠ **This rule must NOT live in `WindowsStartupRegistrar`** — that class ([WindowsStartupRegistrar.cs](src/EyeCtrl.Platform/Services/WindowsStartupRegistrar.cs)) is a passive setter/getter with no knowledge of supervision. Critically, the app **unconditionally** self-registers on every launch: [App.xaml.cs:90](src/EyeCtrl.App/App.xaml.cs:90) calls `SetStartOnBoot(settings.General.StartOnBoot)` in `OnStartup`. So if supervised mode simply cleared the Run key, the app's *very next* launch would re-add it — and at the next logon *both* the supervisor's scheduled task and the app's Run key would fire (the mutex would only paper over the resulting double-launch).

**Fix:** the reconciliation moves into the app's `OnStartup`, where it already happens, and becomes *conditional on the shared supervision flag*:

```csharp
// src/EyeCtrl.App/App.xaml.cs — replace the current unconditional call at line 90
var startupRegistrar = _serviceProvider.GetRequiredService<IStartupRegistrar>();
if (settings.Supervision.Enabled)
{
    // Supervised: the supervisor owns launch-at-logon. The app must never
    // self-register — doing so would double-launch at next logon.
    startupRegistrar.SetStartOnBoot(false);
}
else
{
    // Standalone: reconcile the Run key with the persisted setting, as today.
    startupRegistrar.SetStartOnBoot(settings.General.StartOnBoot);
}
```

The live-toggle `PropertyChanged` handler at [App.xaml.cs:93](src/EyeCtrl.App/App.xaml.cs:93) gets the same conditional: toggling `StartOnBoot` while supervised is a no-op on the Run key (the supervisor owns launch). `WindowsStartupRegistrar` itself is unchanged.

`StartOnBoot` keeps a single user-facing meaning — "start at logon" — regardless of mode; only the mechanism swaps.

### Four in-app changes to `EyeCtrl.App`

1. **Heartbeat emitter** — a `DispatcherTimer` (UI thread) **stamps** the heartbeat payload into a slot every ~3s; a **bounded async writer** sends it over the pipe with connect/write timeouts. The Dispatcher must never perform synchronous pipe I/O (a stalled server/full buffer would freeze the UI thread — see Heartbeat Liveness).
2. **Single-instance Mutex** — `Local\EyeCtrl.App.Singleton` acquired on startup; if held, exit immediately. Prevents the supervisor's respawn + a stray double-click from running two instances. (`Local\` not `Global\` — multi-user/RDP is a non-goal, and `Local\` avoids cross-session collisions under fast-user-switching without needing `SeCreateGlobalPrivilege`.) Handle `AbandonedMutexException` on the supervisor's respawn path: a force-killed process leaves an abandoned mutex, which must be treated as acquired.
3. **Clean-shutdown signal** — the app sends a final heartbeat with `"shuttingDown": true` before an intentional exit (wired into the `OnExit` path at [App.xaml.cs:229](src/EyeCtrl.App/App.xaml.cs:229) and `OnSessionEnding`), so the supervisor distinguishes a user/caregiver "Exit" from a crash and does **not** respawn. This is an authenticated message (see Clean-Exit Protocol + Process Binding) — the supervisor does **not** infer a clean exit from a mere pipe close.
4. **Exception-handler fixes** — register `TaskScheduler.UnobservedTaskException`; decide `DispatcherUnhandledException` behavior deliberately (log + flush + let crash, so the supervisor's respawn owns recovery rather than the app limping on). The existing `AppDomain.CurrentDomain.UnhandledException` handler at [App.xaml.cs:332](src/EyeCtrl.App/App.xaml.cs:332) is retained as-is.

## Design

### State Machine

The supervisor runs one state machine per supervised app instance:

```
   ┌──────────────────────────────────────────────────────────────────┐
   │  IDLE_WATCH   (supervision enabled; app not running; no relaunch) │
   │  entry: logon, OR clean exit received, OR FALLEN_BACK dismissed  │
   └──┬──────────────────────┬─────────────────────────────────────────┘
      │ logon + StartOnBoot  │ first heartbeat from a MANUALLY-launched app
      │ → launch app         │ (adopt path: supervisor didn't launch it)
      ▼                      ▼
   ┌──────────────────────────────────────────────────────────────────┐
   │  LAUNCHING   (only when supervisor itself launched the app)       │
   │  wait for first heartbeat (60s grace)                             │
   └──┬──────────────────────┬──────────────────────────────────────────┘
      │ first HB within 60s   │ process exit before HB, OR 60s elapsed
      ▼                       ▼
   ┌──────────────────────────────────────────────────────────────────┐
   │  SUPERVISING   (steady state)                                     │
   │  monitor: process-exists + heartbeat freshness + gaze liveness   │
   └──┬───────┬──────────┬────────────────────────────────────────────┘
      │ clean │ process  │ HB timeout     │ restarts in window
      │ exit  │ exit /   │ (hang) OR      │ < maxRetries
      │ flag  │ gaze     │ frame arrival  │
      │       │ dead     │ stopped while  │
      │       │          │ IsConnected    │
      ▼       ▼          ▼                ▼
   IDLE   ┌──────────────────────────────┐  ┌────────────────────────┐
   _WATCH  │  RECOVERING                  │  │  (back to LAUNCHING     │
           │  force-kill if hung;         │  │   if supervisor         │
           │  wait backoff; relaunch      │  │   relaunched)           │
           └──┬───────────────────────┬───┘  └────────────────────────┘
              │ restarts in window    │
              │ ≥ maxRetries          │
              ▼                       │ (escalation only —
           ┌──────────────────┐       │  see FALLEN_BACK below)
           │  FALLEN_BACK      │◀──────┘
           │  fallback AT      │
           │  launched; stop   │
           │  retrying app     │
           │  this session     │
           └──────────────────┘
```

**The adopt path** (IDLE_WATCH → SUPERVISING via "first heartbeat from a manually-launched app") closes the gap where supervised mode + `StartOnBoot` off left a manually-started app effectively unsupervised: the supervisor always runs at logon (it has its own scheduled task), and if it receives a first heartbeat from an app it didn't launch, it adopts it directly into SUPERVISING.

**Rules:**

- **Startup grace (LAUNCHING):** 60s to receive the first heartbeat. The app has a splash screen, DI build, Tobii init, and settings load; 60s covers slow machines + cold Tobii bring-up without false-positive crash verdicts. Two failure exits from LAUNCHING: process exits before first heartbeat (startup crash), or 60s elapse with no heartbeat. Both → RECOVERING and **count as one strike** against the crash window (yes — a failed first-logon launch is strike 1).
- **Crash window + `maxRetries` (worked example).** Sliding 2-minute window; `maxRetries = 3`. **"3" means three strikes (three restart attempts), not three crashes.** A *strike* = one entry into RECOVERING. Worked example for a persistently-crashing app:
  - T=0s: supervisor launches app → LAUNCHING.
  - T=5s: app crashes → RECOVERING (**strike 1**) → backoff → relaunch at T=5s.
  - T=10s: app crashes again → RECOVERING (**strike 2**) → backoff → relaunch at T=20s (5s→15s→60s curve).
  - T=25s: app crashes again → RECOVERING (**strike 3**) → backoff → relaunch at T=80s.
  - T=85s: app crashes a 4th time. Strike count within the trailing 2-min window (T=85s → window start T=-35s) is now 4 > `maxRetries(3)`. **→ FALLEN_BACK.**
  - So the user sees a fallback AT after **3 failed restart attempts** (4 crashes total: the original + 3 restarts that all failed). The `ShouldEscalate()` boundary test asserts exactly: `restarts in window > maxRetries` (strictly greater, so `maxRetries` attempts are always permitted; the `(maxRetries+1)`-th failure escalates).
- **Counter reset — no infinite-restart hole.** A restart that survives **longer than the crash window (default 120s)** in SUPERVISING resets the strike counter (transient crash → genuinely recovered). The earlier "30s clean → reset" rule is **removed**: it reopened the infinite-restart hole the spec explicitly rejected (an app surviving ~31–60s then crashing would reset every cycle and never escalate). 120s is the bar because that is, by definition, longer than the window — if the app ran cleanly for a full window, the previous strikes have aged out anyway, so the reset is equivalent to "let the window expire naturally." The explicit reset is retained only to make the intent legible and to cover the case where the window slides while the app is mid-run.
- **Hang vs crash:** heartbeat timeout with process still alive → **force-kill** (`Process.Kill(entireProcessTree: true)`) before relaunch. Counts as one strike.
- **Gaze-stream death:** heartbeat reports `isConnected == true` but `now - lastFrameUtc > hangThreshold` → treated as a recoverable failure → force-kill → RECOVERING, counts as one strike. (`isConnected == false` alone is a *soft* signal: the app's own reconnect loop owns it — no kill.) See Gaze-stream-death detection for why `lastFrameUtc`, not valid-gaze time.
- **Clean exit is authenticated, not inferred** (see Clean-Exit Protocol): only an explicit `shuttingDown: true` from a bound client, followed by process exit within the deadline (default 5s), → **IDLE_WATCH**, no respawn. A user/caregiver tray-Exit is honored. `OnSessionEnding` (logoff / Windows shutdown) also sends `shuttingDown: true`. **Any unannounced disconnect (pipe close with no prior `shuttingDown: true`) is a crash signal → RECOVERING** — a crashing process closes its pipe too.
- **FALLEN_BACK is terminal for the session.** No automatic return path. Rationale: re-introducing the app would re-introduce tracker contention with the fallback AT. Next logon = clean slate (IDLE_WATCH → LAUNCHING). The FALLEN_BACK dialog does offer a dwell-clickable "Retry EyeCtrl once" — an explicit user action, not automatic — so a stranded user has a one-shot way back without a caregiver.

### Components

The supervisor is intentionally small: six components, each one job.

#### Heartbeat liveness — what "fresh heartbeat" actually proves

Goal #2 is "detect a frozen UI thread / deadlock." A heartbeat emitted by a background `System.Threading.Timer` **cannot** prove this — the timer thread keeps firing while the UI thread is deadlocked, so the heartbeat stays fresh and the hang is invisible.

The heartbeat's *freshness* must therefore be **produced on the Dispatcher** — but the Dispatcher must **never** perform the pipe I/O itself. Synchronous pipe writes from the UI thread are a hazard: a stalled server, a full pipe buffer, or a slow reconnect would block the Dispatcher, creating exactly the hang the watchdog then kills. The two concerns are split:

- **Liveness stamp (Dispatcher-owned).** A `DispatcherTimer` ticks every ~3s on the UI thread and builds the heartbeat payload into a single mutable slot (`volatile` / `Interlocked`-swapped reference), stamping it with `DateTime.UtcNow`. If the UI thread is frozen, the stamp stops advancing → the supervisor sees a stale `lastHeartbeatUtc` → hang detected. This is the *only* thing the timer does on the UI thread.
- **Bounded async writer (background).** A dedicated writer thread/task reads the current payload slot and sends it over the pipe with **connect + write timeouts** (e.g. 2s each). It keeps only the **single most-recent payload** (a new stamp overwrites a pending one — a stuck write never blocks a fresher heartbeat). On any I/O error, timeout, or full buffer, it drops that send and waits for the next stamp cycle; it **never throws onto the Dispatcher** and never blocks waiting for the server.

Net effect: the Dispatcher proves liveness by stamping; the writer proves delivery best-effort with bounded latency. A frozen UI thread stops the stamps regardless of the writer; a stalled pipe stops delivery without freezing the UI.

#### Gaze-stream-death detection — frame arrival, not gaze validity

⚠ **This is the most dangerous failure mode to get wrong for the target user.** The Tobii provider forwards **both valid and invalid** samples deliberately: invalid samples (blink, eyes lost, user absent) go through as `GazePoint(0,0,ts,false)` specifically so EyeCare away-detection works ([TobiiStreamEngineProvider.cs:191-197](src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs:191)). Consequences:

- If `lastGazeSampleUtc` is keyed on **valid** gaze (the intuitive reading of "last gaze sample"), then a user who looks away, closes their eyes, reads, or steps out for 20s produces no valid samples → false "gaze dead" → force-kill → relaunch. Repeatedly. This could burn the crash window and escalate to **FALLEN_BACK while the user is merely absent** — catastrophic for someone who depends on this as their only input.

Therefore the heartbeat field — call it `lastFrameUtc`, **not** `lastGazeSampleUtc` — **must track frame arrival regardless of validity**: "the SDK is still delivering frames," updated on *every* `GazePointReceived` callback (valid or invalid). A genuinely wedged device stops delivering frames entirely; a user looking away does not. The app records `lastFrameUtc` (any sample) and `lastValidGazeUtc` (valid only) separately; the heartbeat sends only `lastFrameUtc`.

**Narrow failure this catches:** the poll thread stuck in `tobii_wait_for_callbacks` / `tobii_device_process_callbacks` while `IsConnected` stays stale-true (no callback fires to flip it). The supervisor should additionally treat `IsConnected == false` sustained past a grace period as a *soft* signal (log + surface in tray) rather than an immediate kill — the app's own reconnect loop ([TobiiStreamEngineProvider.cs:314-323](src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs:314)) is already working that case. The hard kill trigger is *frame arrival stopped while `IsConnected == true`*: that combination specifically indicates a wedged poll loop that the reconnect logic has not noticed.

#### Clean-Exit Protocol — intentional quit vs crash

Without a clean-shutdown signal, the supervisor treats every process exit as a crash and respawns — so a user or caregiver who deliberately closes EyeCtrl (tray Exit, etc.) gets it immediately relaunched. That defeats the ability to ever stop the app in supervised mode.

**Protocol — authenticated clean exit only.** ⚠ A crashing process also closes its pipe. Treating "pipe close without a prior failure" as a clean exit is **wrong**: it would route the primary crash path into IDLE_WATCH and the app would never restart. **Only an explicit `shuttingDown: true` heartbeat message suppresses recovery**, and even then only if the process actually exits within a short deadline:

1. The app sends a final heartbeat with `"shuttingDown": true` before an intentional exit — wired into the existing tray-Exit `Shutdown()` path at [App.xaml.cs:229](src/EyeCtrl.App/App.xaml.cs:229) (the `OnExit` save/teardown) and the `shutdownCallback` in `TrayIconManager`.
2. The supervisor, on receiving an **authenticated** `shuttingDown: true` (see Process Binding — the message must come from the verified supervised process, not an untrusted sender), enters a **CLEAN_EXIT_PENDING** window with a deadline (default 5s).
3. If the supervised process exits within the deadline → **intentional exit** → IDLE_WATCH (supervision enabled, app not running, no relaunch). The supervisor only leaves IDLE_WATCH to relaunch on logon (next session) or via an explicit "Launch EyeCtrl now" dwell action from its own tray/dialog.
4. If the process does **not** exit within the deadline despite claiming `shuttingDown: true` → it's lying or stuck mid-shutdown → treat as unhealthy → RECOVERING (force-kill if still alive after grace). A clean-exit claim that doesn't actually exit is a failure signal, not a pardon.

**Any unannounced disconnect — pipe close with no prior `shuttingDown: true` — remains a crash/unhealthy signal → RECOVERING.** There is no "silent clean exit."

`OnSessionEnding` (logoff / Windows shutdown) should also send `shuttingDown: true` — a system logoff is intentional, not a crash, and the supervisor must not fight it. (This is one of the Problem gaps the supervisor design should close.)

The full state list is now: LAUNCHING, SUPERVISING, RECOVERING, FALLEN_BACK, IDLE_WATCH, plus the transient **CLEAN_EXIT_PENDING** sub-state (a timed bridge from SUPERVISING → IDLE_WATCH, falling through to RECOVERING on timeout).

#### 1. Gaze subsystem (independent dwell)

- References `EyeCtrl.Core` (`DwellDetector`, `OneEuroFilter`, `GazePoint`), `EyeCtrl.Platform` gaze providers, and `EyeCtrl.Controls` (the extracted dwell WPF controls — see New projects).
- **Runs only while a supervisor window is visible** — starts gaze when the first window opens, stops when the last one closes. No 24/7 tracker hold during healthy operation.
- Dwell-only: the `DwellButton` WPF control (large hit area, dwell progress-ring animation) — sourced from `EyeCtrl.Controls`, **not** re-implemented. Used everywhere in supervisor UI. No zoom, no keyboard, no overlays.
- **Tracker sharing:** supervisor and app may subscribe to the Tobii device concurrently (Stream Engine supports multiple subscribers); no mutual-exclusion rule. The supervisor simply doesn't hold the device when it has no UI on screen. **⚠ This assumption needs a spike.** "Stream Engine supports multiple subscribers" is asserted without evidence; each process creates its own `tobii_api_create` + `tobii_device_create` context over the same device URL. The implementation plan must verify that two processes can `tobii_device_create` + `tobii_gaze_point_subscribe` to the same device simultaneously without one failing with `TOBII_ERROR_INSUFFICIENT_LICENSE` or similar. **If the spike fails, the design degrades to the mutual-exclusion alternative** (supervisor acquires gaze only when its UI is visible *and* the app is not running) — which is already close to the gating rule above and would simply also coordinate with the app's process state. The "Retry EyeCtrl once" path (fallback AT + relaunched app both wanting the device) is the case most affected and should be the focus of the spike.

#### 2. IPC — the heartbeat pipe

- Named pipe **`eyectrl_watch`** — distinct from the existing `eyectrl_dwell` pipe (no overload, no semantic collision).
- **Supervisor = pipe server** (owns the endpoint, single instance, `NamedPipeServerStream`). **App = client** (`NamedPipeClientStream`).
- App writes a small JSON heartbeat every ~3s. The **payload is stamped on the UI thread**, but **sent by a bounded async writer** with connect/write timeouts — see Heartbeat Liveness. The Dispatcher never waits for IPC.

  ```json
  {
    "pid": 12345,
    "startedAtUtc": "2026-07-15T10:00:00Z",
    "isConnected": true,
    "lastFrameUtc": "2026-07-15T10:05:12.3Z",
    "shuttingDown": false
  }
  ```

  `lastFrameUtc` is updated on **every** `GazePointReceived` callback (valid or invalid — see Gaze-stream-death detection). `shuttingDown` is the clean-exit flag (see Clean-Exit Protocol — **authenticated**, not inferable from a pipe close).

#### Process Binding — trust nothing the pipe says until verified

⚠ The JSON `pid` field is **untrusted**, and process-name discovery alone (`Process.GetProcessesByName("EyeCtrl.App")`) can confuse a stale or unrelated process with the one the supervisor launched. The supervisor must **bind** to the exact supervised process before trusting its heartbeats:

- **OS-level client identity.** `NamedPipeServerStream` exposes the connecting client's PID via `GetNamedPipeClientProcessId` (Win32, P/Invoke in Platform). The supervisor reads the *kernel-reported* client PID, not the JSON field.
- **Full identity check on connect.** For the client PID, verify: (a) same Windows session as the supervisor (`Process.GetProcessIdSessionId`); (b) same user; (c) **executable path** matches the supervised app exe path the supervisor launched (via the Process handle / `QueryFullProcessImageName`); (d) **process creation time** matches the handle captured at `LaunchApp()` — guards against PID reuse where a crashed process's PID is recycled by an unrelated one. Only after all four pass does the supervisor treat the connection as "the supervised app" and record its `Process` handle + PID for monitoring.
- **Restrictive pipe ACL.** The server pipe is created with a security descriptor allowing access **only to the current user's SID** (deny NETWORK and other users) — so a different-user / cross-session / remote process cannot impersonate the app or inject heartbeats. This uses the existing Win32 P/Invoke seam in Platform.
- **Monitor the bound handle, not a name.** `IsProcessAlive()` checks the recorded `Process` handle (registered for `Exited`, or polled via `HasExited`), not a name lookup. The Mutex (component 6) remains the dedup guard for launch, but supervision tracks the specific instance.
- **Untrusted JSON fields.** The JSON `pid`/`startedAtUtc` are used only as cross-checks against the kernel-bound identity; a mismatch is a warning, not authoritative. Only `lastFrameUtc`, `isConnected`, `shuttingDown` carry behavioral meaning, and only after the connection has passed identity binding.

#### Heartbeat-freshness + gaze rules

- Supervisor tracks `lastHeartbeatUtc` (from bound client only). If `now - lastHeartbeat > hangThreshold (20s)` and the process is alive → **hang** → force-kill → RECOVERING.
- Gaze-stream death: `lastFrameUtc` stale beyond `hangThreshold` **while `isConnected == true`** → force-kill → RECOVERING. (`isConnected == false` alone is a *soft* signal: log + tray, no kill — the app's reconnect loop owns it.)
- **Clean exit is authenticated, not inferred** (see Clean-Exit Protocol): only `shuttingDown: true` from a bound client, followed by exit within the deadline, → IDLE_WATCH. **Any unannounced disconnect (pipe close with no prior `shuttingDown: true`) remains a crash signal → RECOVERING.**
- Pipe write failure (client gone) + process exit + no clean-exit signal → crash path → RECOVERING.
- **Both sides tolerate the other being absent:** the supervisor's pipe server simply has no connections when the app isn't running (standalone mode, or app not yet started); the app's heartbeat emitter must no-op/retry quietly when no server exists (standalone mode) — it must not throw or spam logs.

#### 3. Process controller

- `LaunchApp()` — starts the app exe, records the `Process` handle (and its creation time, for binding — see Process Binding), enters LAUNCHING.
- `IsProcessAlive()` — checks the **recorded `Process` handle** (registered for `Exited` / polled via `HasExited`), **not** a name lookup. The Mutex (component 6) is the dedup guard for *launch*; supervision tracks the specific bound instance. See Process Binding for why name discovery is unsafe.
- `ForceKill()` — `Process.Kill(entireProcessTree: true)` for hang recovery.
- **Backoff:** immediate → 5s → 15s → 60s across consecutive RECOVERING cycles, capped at 60s. Resets to immediate only after the app survives a full crash window (default 120s) in SUPERVISING — see Crash window for why a shorter reset reopens the infinite-restart hole.

#### 4. Crash window + crash counter

- Sliding 2-minute window (default). Each entry into RECOVERING pushes a timestamp into the window.
- `ShouldEscalate()` returns true when the count of restart timestamps within the window is **strictly greater than** `maxRetries` — so `maxRetries = 3` means three restart attempts are always permitted, and the *fourth* failure (the `(maxRetries+1)`-th) triggers escalation. See the worked example in State Machine.
- Strike counter resets only after the app survives longer than the crash window in SUPERVISING (default 120s). No 30s shortcut.
- Implementation note: `CrashWindow` is a small mutable class (it accumulates timestamps), not an immutable struct — earlier "immutable struct" wording was wrong. Its *query* method `ShouldEscalate()` is pure (reads window + now), so the state machine's decision is still deterministic and unit-testable.

#### 5. Fallback AT launcher

Ordered candidate list (configurable in settings; default order below). Probes install state, then launches the first found:

1. **Tobii Dynavox TD Control** — registry probe `HKLM\SOFTWARE\Tobii Dynavox\TD Control` or known path `%ProgramFiles%\Tobii Dynavox\TD Control\*.exe`.
2. **Tobii Snap Core First** — `HKLM\SOFTWARE\Tobii Dynavox\Snap Core First`.
3. **Windows built-in Eye Control** — present on Win10 1709+ / Win11 via Settings → Accessibility → Eye Control. **⚠ Launch mechanism needs a spike.** `ms-cortana:eyecontrol` is unreliable (Cortana is deprecated/removed on Win11), and Eye Control is a Settings/Ease-of-Access *toggle*, not an app with a clean public launch API. Since this is the linchpin of Goal #5 ("never strand the user"), the implementation plan must include a verified launch path for it (spike candidates: `easeofaccess` / `IShellDispatch6`, the Eye Control API `GazeInput` API, or instructing the user to enable it once during setup so a known launch vector exists). If the spike finds no reliable programmatic launch, Windows Eye Control drops to "documented manual fallback" and Goal #5's guarantee rests on *at least one Tobii AT being installed* — which must be called out as a setup-time recommendation, not silently assumed.

Logs which AT launched and its PID. After launch → FALLEN_BACK, terminal for the session. **Fallback-launcher probes also need a spike:** see Open Questions.

#### 6. Single-instance Mutex (in the app — defense-in-depth)

- The app acquires `Local\EyeCtrl.App.Singleton` on startup. If already held, it exits immediately (the supervisor's instance is the legitimate one).
- Prevents any race where both the supervisor's respawn and a stray double-click launch try to run the app. The supervisor itself also holds a `Local\EyeCtrl.Supervisor.Singleton` Mutex for the same reason. (`Local\` not `Global\` — see the in-app-change note; `AbandonedMutexException` from a force-killed prior holder is treated as acquired.)

### Supervisor UI

The supervisor surfaces a small WPF UI. Two entry points:

- **First run / `--setup` arg** — shows the setup wizard.
- **Tray icon** (`NotifyIcon`, same pattern as `TrayIconManager`) — right-click menu: "EyeCtrl Supervisor settings," "Pause supervision," "Exit supervisor." Doubles as a liveness affordance for the user.

The setup window owns these controls:

| Control | What it does |
|---|---|
| **Enable supervision** toggle | Installs/removes the Scheduled Task; toggles supervised vs standalone. Clears/restores the app's HKCU Run key accordingly. |
| **Start EyeCtrl at logon** toggle | Reflected into the shared `StartOnBoot` setting so both the app's settings UI and the supervisor's stay in sync. |
| **Fallback AT priority list** | Ordered list (TD Control → Snap Core First → Windows Eye Control) with enable/disable + reorder. The supervisor probes installed ones at fallback time. |
| **Crash-restart policy** *(Advanced)* | Max retries within window (default 3 / 2 min), backoff curve. Hidden behind an "Advanced" expander. |
| **Hang threshold** *(Advanced)* | Heartbeat timeout (default 20s). |
| **Diagnostics** | "View supervisor log," "Test fallback launch," "Launch EyeCtrl now." |

All buttons in supervisor windows are `DwellButton`s — operable by gaze. This is non-negotiable: the crash-recovery dialog is shown precisely when the app (the user's usual dwell surface) is broken.

### Crash-recovery dialog (FALLEN_BACK UI)

When the supervisor enters FALLEN_BACK it shows a non-modal, topmost, large dwell-enabled window:

```
┌─────────────────────────────────────────────────────┐
│  EyeCtrl couldn't stay running.                      │
│  A fallback eye-control app has been launched.       │
│                                                      │
│  ┌──────────────────┐  ┌──────────────────────────┐  │
│  │   Retry EyeCtrl  │  │  Dismiss (keep fallback) │  │
│  │      (once)      │  │                          │  │
│  └──────────────────┘  └──────────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

- The fallback AT is already running and provides OS cursor control; the user can also use the supervisor's own dwell to click these buttons.
- "Retry EyeCtrl (once)" is a one-shot manual restart (not a return to automatic retry). If it crashes again, the dialog reappears.
- "Dismiss" closes the dialog; the supervisor stays resident, supervision paused for the session.

### Shared configuration

Both supervisor and app read/write the same settings file at `%APPDATA%/EyeCtrl/settings.json` (the existing `SettingsService` validates and persists there). A new `SupervisionSettings` section is added:

```csharp
// src/EyeCtrl.Core/Settings/SupervisionSettings.cs
namespace EyeCtrl.Core.Settings;

public class SupervisionSettings
{
    public bool Enabled { get; set; } = false;
    public int MaxRetries { get; set; } = 3;
    public int CrashWindowSeconds { get; set; } = 120;
    public int HangThresholdSeconds { get; set; } = 20;
    public int StartupGraceSeconds { get; set; } = 60;
    public List<string> FallbackAtOrder { get; set; } = new()
    {
        "TDControl", "SnapCoreFirst", "WindowsEyeControl"
    };
}
```

Both processes read this file. The supervisor uses **`FileSystemWatcher`** to reload when the file changes, so the two config surfaces (app settings window, supervisor setup window) stay in sync. Single source of truth.

⚠ **Lost updates, not just corruption.** A write mutex + atomic rename (below) prevent file *corruption*, but they do **not** prevent **lost updates**: both processes hold full in-memory snapshots of the entire settings object, and the app writes the whole object on exit ([App.xaml.cs:229](src/EyeCtrl.App/App.xaml.cs:229), `settingsService.Save(settings)`). So if the supervisor enables supervision (`Supervision.Enabled = true`) and the app then saves its stale snapshot (`Supervision.Enabled = false`) on its next exit, the supervisor's change is silently overwritten — leaving the Run key cleared and supervision disabled. A write mutex makes that overwrite *clean*, which is worse: it happens silently with no error.

This requires a **single-writer or transactional read-modify-write** discipline, not just mutual exclusion. The design uses:

1. **Single owner for `SupervisionSettings` writes.** Only the supervisor writes `SupervisionSettings`. The app reads `Supervision.Enabled` (for the Run-key reconciliation and to decide whether to emit heartbeats) but **must never persist a `SupervisionSettings` snapshot** — its `SettingsService.Save` must omit the `Supervision` section (or re-read-then-preserve-it: load the on-disk `Supervision` block immediately before serializing and pass it through unmodified). This closes the clobber path directly: the app cannot overwrite what it never owns. Generalized to a rule: **each section of `settings.json` has exactly one writer process**; the app owns the UX sections, the supervisor owns the `Supervision` section.
2. **Transactional read-modify-write for any writer** (defense-in-depth, in case a future feature needs cross-process writes to the same section): under the writer mutex, *re-read* the current on-disk file, apply only the section being changed to that fresh copy, then atomic-rename. Never serialize a full in-memory snapshot that may be stale.
3. **Atomic writes:** serialize to `settings.json.tmp`, then `File.Move(tmp, final, overwrite: true)` (atomic on NTFS for same-volume renames). A crash mid-write leaves either the old file intact or the complete new file — never a partial.
4. **Cross-process write mutex:** `Local\EyeCtrl.Settings.Writer` held for the read-modify-write window, so two writers never overlap. (Atomic rename already prevents corruption; the mutex additionally serializes the read-modify-write so the loser's re-read sees the winner's change.)
5. **Tolerate read of a partially-written file:** the existing `SettingsService.Load` validation path treats a parse failure as "use defaults + log," not crash. This class of corruption is already known to be severe here (a bad `calibration.json` silently breaks *all* dwell).

The heartbeat pipe is **one-way (app → supervisor)**; it does not carry config-sync notifications. `FileSystemWatcher` is the sole sync mechanism.

### Logging

Serilog, same initialization pattern as the app (`LoggingInitializer.Initialize()`), to `%APPDATA%/EyeCtrl/logs/supervisor-.log` with daily rolling and 7-day retention. The supervisor logs every state transition, every restart attempt (with crash count), every fallback launch, and every heartbeat-timeout event. Backoff dedups repeated log lines (mirroring the app's Tobii reconnect dedup).

## Error Handling

| Failure | Behavior |
|---|---|
| App crashes (process exit, **no** prior `shuttingDown: true`) | Crash path → RECOVERING → backoff → relaunch. A crash also closes the pipe — that close is **not** treated as a clean exit. |
| App hung (HB timeout, process alive) | Force-kill → RECOVERING → relaunch. |
| Gaze stream dead (`lastFrameUtc` stale while `isConnected == true`) | Force-kill → RECOVERING → relaunch. |
| Gaze stream soft-lost (`isConnected == false`) | Soft signal only: log + tray. The app's own reconnect loop owns recovery; no kill. |
| App exits cleanly (authenticated `shuttingDown: true` + exit within 5s deadline; incl. logoff/shutdown) | IDLE_WATCH — no respawn. |
| `shuttingDown: true` sent but process does **not** exit within deadline | Stuck mid-shutdown → unhealthy → RECOVERING (force-kill if still alive). A clean-exit claim that doesn't actually exit is a failure signal. |
| Unannounced pipe disconnect (no prior `shuttingDown: true`) | Crash/unhealthy signal → RECOVERING. There is no "silent clean exit." |
| Unverified/spoofing pipe client (wrong session/user/exe-path/creation-time) | Heartbeats ignored; connection dropped. See Process Binding. |
| Crash loop (restarts in window > `maxRetries`) | Launch fallback AT → FALLEN_BACK → terminal this session. |
| Fallback AT not installed (none found) | Log error; show FALLEN_BACK dialog anyway (user can still "Retry EyeCtrl once"). Windows Eye Control is the intended last resort — see Open Questions for the launch-mechanism spike. |
| Supervisor itself crashes | Scheduled Task native "restart on failure" relaunches it (up to 3×). |
| Supervisor itself hangs | **Residual risk — not automatically covered.** Task Scheduler's "restart on failure" only fires on *exit*, not hang. The supervisor is intentionally small (single supervision loop, no UI thread during headless operation) to minimize hang probability, but it has no self-hang detection. Acceptable for v1; a future watcher-of-the-watcher (e.g. a second scheduled task that kills+restarts the supervisor if its own heartbeat file goes stale) is noted in Out of Scope. |
| Heartbeat pipe error (transient) | Supervisor pipe server tolerates connect/disconnect/reconnect; a missing client is normal (app not started / standalone mode). |
| Heartbeat pipe write stalls (full buffer / slow server) | Bounded async writer drops the stalled send after its timeout; the Dispatcher is never blocked. Next stamp cycle sends a fresh payload. |
| Heartbeat pipe server missing (standalone mode) | App heartbeat emitter no-ops/retries quietly — must not throw or spam logs. |
| Tobii init fails in supervisor gaze subsystem | Falls back to `MouseGazeProvider` via `GazeProviderFactory` (same Auto logic as the app). Supervisor dwell still works for testing; in production this means the user has no tracker and the fallback AT is the real recourse. |
| Concurrent Tobii subscription (supervisor + app both open) | Permitted by design — see Open Questions spike. Degrades to mutual-exclusion if the spike fails. |
| Sleep / resume (`PowerModeChanged`) | The supervisor does not specially handle sleep; on resume, a wedged post-resume app state is caught by the normal heartbeat-timeout / frame-arrival path → RECOVERING. The app should additionally handle `PowerModeChanged` to reset its gaze provider cleanly (separate app-side hardening, not blocking for the supervisor). |
| Scheduled Task missing/corrupted | Setup window "Enable supervision" re-installs it idempotently. |
| `settings.json` corruption | Atomic write (temp+rename) prevents partial writes; parse failure on load → use defaults + log, not crash. |
| `settings.json` lost update (app clobbers supervisor's `Supervision` section) | Prevented by single-writer ownership: the app never persists `SupervisionSettings`; the supervisor is the sole writer of that section. Transactional read-modify-write under the writer mutex is defense-in-depth. |

## Testing

Follows repo conventions: **xUnit v3 (3.2.2)** + NFluent 3.1.0, no mocking library (private nested fakes), `XxxTests` naming, `InternalsVisibleTo` from source projects to their test projects. (The repo migrated from xUnit 2.x to v3 — test projects are stand-alone executables per `tests/Directory.Build.props`; UI/STA tests use `[StaFact]`.)

- **State machine unit tests** (`EyeCtrl.Supervisor.Tests`): drive the state machine through LAUNCHING → SUPERVISING → RECOVERING → SUPERVISING, the crash-loop → FALLEN_BACK path, and the new IDLE_WATCH + adopt-on-first-heartbeat path, with a fake clock and a fake process controller. Verify transitions, backoff curve, crash-window reset only after a full window (120s) of clean SUPERVISING. Verify a clean-exit (`shuttingDown: true`) heartbeat → IDLE_WATCH (no respawn).
- **CrashWindow unit tests**: sliding-window counter; `ShouldEscalate()` boundary — assert it returns `false` at exactly `maxRetries` restarts in window (those are permitted) and `true` at `maxRetries + 1` (escalates); assert expiry of old timestamps; assert the worked-example sequence (3 strikes permitted, 4th escalates).
- **Fallback AT launcher unit tests**: fake registry/path probes returning installed/not-installed; verify first-found-is-launched ordering. Windows Eye Control launch mechanism is spike-gated (see Open Questions) — the test covers the ordering/probe logic, the actual launch vector is validated separately.
- **Heartbeat serialization tests**: round-trip the JSON payload; verify `lastFrameUtc` (any sample, valid or invalid) feeds gaze-stream-death detection, and that valid-only gating would *not* trip on look-away. Include a regression test: invalid samples (`GazePoint(0,0,ts,false)`) arriving every 33ms must keep `lastFrameUtc` fresh.
- **Clean-exit protocol tests**: authenticated `shuttingDown: true` + process exit within deadline → IDLE_WATCH, no relaunch; `shuttingDown: true` but process does **not** exit within deadline → RECOVERING (stuck mid-shutdown); **unannounced pipe close (no prior `shuttingDown: true`) → RECOVERING, not IDLE_WATCH** (regression test: a crash also closes the pipe); `shuttingDown: true` from an unbound/unverified client → ignored (not a clean exit).
- **Process-binding tests**: kernel client PID from `GetNamedPipeClientProcessId` is matched against the launched `Process` handle; reject mismatched session/user/exe-path/creation-time; a spoofing client (different exe path, or PID-reuse with wrong creation time) is rejected and its heartbeats ignored; the JSON `pid` field alone is never trusted.
- **In-app changes** (`EyeCtrl.App.Tests` / `EyeCtrl.Core.Tests`):
  - `Local\` mutex acquisition: second instance exits; `AbandonedMutexException` on respawn is treated as acquired.
  - Heartbeat emitter: Dispatcher stamps payload; bounded async writer sends expected JSON on the expected pipe name with timeouts; **no-ops quietly when no pipe server exists** (standalone mode) — must not throw or log-spam, and the Dispatcher must never block on I/O.
  - **Reconciliation logic in `App.OnStartup`** (not in `WindowsStartupRegistrar`): supervised mode → `SetStartOnBoot(false)` regardless of `StartOnBoot`; standalone → `SetStartOnBoot(settings.General.StartOnBoot)` as today.
- **Settings tests**: atomic write (temp+rename leaves old file intact on simulated mid-write crash; parse-failure → defaults); **lost-update regression test** — app `Save` under supervised mode does **not** persist a `Supervision` section that could clobber a concurrent supervisor write (app re-reads/preserves or omits it); transactional read-modify-write under the writer mutex sees a concurrent writer's change.
- **`EyeCtrl.Controls` extraction test**: `DwellButton` dwell-firing behavior is unchanged after the move (the App's existing dwell-control tests continue to pass against the moved types); `EyeCtrl.Controls` references only `EyeCtrl.Core` + WPF (no App/ViewModels/LM/EyeCare dependencies).
- **Integration / UI tests** (`Category=UI`, not in CI): the crash-recovery dialog renders, `DwellButton` fires on dwell, "Retry EyeCtrl once" relaunches.
- **Add `EyeCtrl.Controls` and `EyeCtrl.Supervisor` to `EyeCtrl.slnx`**, plus a `tests/Directory.Build.props`-covered `EyeCtrl.Supervisor.Tests` project.

## Alternatives Considered

| Alternative | Why rejected |
|---|---|
| **Windows Service (Session 0)** | Most robust against session-level problems, but Session 0 cannot launch a WPF app into the user's interactive desktop without `CreateProcessAsUserW` + the active user's token (nontrivial P/Invoke, `SeTcbPrivilege` handling). Heartbeat IPC would also cross the session boundary. Materially more complex for no user-facing benefit. |
| **Task Scheduler only (no custom code)** | Zero new code, but Task Scheduler only knows about process *exit* — it cannot detect hangs (no heartbeat), cannot enforce single-instance beyond what the app does, and cannot launch a fallback AT. Covers crash + reboot only; leaves the hung and tracker-lost failure modes uncovered. |
| **App monitors itself via a secondary thread** | Does not survive the app crashing. Useless for the primary failure mode. |
| **Route supervisor dwell through the app** | Circular dependency: the supervisor UI is shown *because* the app is crashing. Dead on arrival for the crash-dialog path. |
| **Vendored copy of gaze code in supervisor** | Full isolation, but ~1500 lines of subtle gaze code duplicated and hand-synced; drift risk. Rejected in favor of referencing Core+Platform directly — that's what the framework-agnostic layers were designed for. |
| **Extract shared `EyeCtrl.Gaze` library** | Cleanest dependency hygiene, but a multi-project refactor touching App and Platform layouts now — expands scope. Candidate for a later cleanup; not required for this design to work. |
| **Restart forever with backoff (no escalation)** | Maximizes availability, but a permanently broken build would loop forever. Rejected: a stranded user needs a fallback AT more than they need infinite retries. |
| **Auto-return from fallback** | Re-introduces tracker contention with the fallback AT. Rejected: FALLEN_BACK is terminal for the session. |

## Open Questions (spikes required before/during implementation)

These must be resolved during implementation (early, in spike tasks) because they affect core guarantees. The design degrades gracefully in each case, but the fallback shape differs.

1. **Windows Eye Control launch mechanism (affects Goal #5).** `ms-cortana:eyecontrol` is unreliable on Win11 (Cortana deprecated/removed); Eye Control is a Settings toggle, not an app with a clean public API. Spike candidates: `easeofaccess` / `IShellDispatch6`, the `GazeInput` accessibility API, or a setup-time "enable Eye Control once" instruction so a known launch vector exists. **If no reliable programmatic launch is found:** Windows Eye Control drops to "documented manual fallback," Goal #5's guarantee rests on *at least one Tobii AT being installed*, and the setup wizard must surface a prominent recommendation to install TD Control / Snap Core First — not silently assume a guaranteed fallback.

2. **Tobii multi-subscriber (affects tracker-sharing + "Retry EyeCtrl once").** Verify two processes can `tobii_device_create` + `tobii_gaze_point_subscribe` to the same device URL simultaneously without `TOBII_ERROR_INSUFFICIENT_LICENSE` or equivalent. **If the spike fails:** degrade to mutual-exclusion — supervisor acquires gaze only when (its UI visible) AND (app not running). This is close to the existing gating rule (supervisor holds the device only while a window is open) and mainly affects the "Retry EyeCtrl once" path where the fallback AT + relaunched app both want the device.

## Out of Scope (future work)

- Installer authoring (.msi / .msix / bootstrapper) for the Scheduled Task + supervisor exe — separate workstream. **Handoff note:** the supervisor needs the app's exe path for `LaunchApp()`; this is an install-dir dependency the installer must supply (e.g. via a registry key or a known relative path between the two exes in the same install directory).
- **Watcher-of-the-watcher** — a second scheduled task that kills + restarts the supervisor if its own heartbeat file goes stale. The supervisor has no self-hang detection (see Error Handling). Acceptable residual risk for v1 given the supervisor is intentionally small; noted here for a future hardening pass.
- Auto-return from fallback AT to EyeCtrl (would need careful tracker-handoff logic).
- Multi-user / RDP session scenarios.
- Remote monitoring / telemetry dashboard for caregivers.
- Code signing of the supervisor exe (follows whatever the app's signing story becomes).
