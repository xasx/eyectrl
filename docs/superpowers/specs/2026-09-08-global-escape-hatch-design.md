# Global Keyboard Escape Hatch (WH_KEYBOARD_LL) — Design

**Date:** 2026-09-08 · **Issue:** #141 · **Stacked on:** `feat/repeat-click-mode` (PR #139 — wires its `RepeatModeController`; `DragCoordinator` exists on `main`).
**Spec of record:** issue #141 (problem, constraints, acceptance criteria). This document records the design decisions and their ledger.

## What is being built

`GlobalEscapeHatch` in `EyeCtrl.Platform`: a `WH_KEYBOARD_LL` hook on a dedicated pumping
thread that raises `EscapePressed` when ESC is pressed **anywhere in the system**, so a
caregiver can end locked repeat mode or cancel a drag while a third-party app holds the
foreground — the situation the existing `ComponentDispatcher.ThreadFilterMessage` filters can
never see (they only observe EyeCtrl's own UI thread, and the overlays are `WS_EX_NOACTIVATE`).

## Owner-vetoable decision ledger

| # | Decision | Rationale | Rejected alternative |
|---|----------|-----------|---------------------|
| H1 | Hook is installed once at service construction (app startup) and unhooked on dispose/shutdown | Simplest lifecycle: no per-mode install/uninstall churn, no refcounting between two coordinators, no silent-failure window mid-mode. Callback cost when idle is two integer comparisons | Install-on-demand when a mode starts (lifecycle races; a failed install discovered only when the hatch is needed) |
| H2 | ESC matches are escalated **off the hook thread** (one `ThreadPool` dispatch); subscribers receive the event on a pool thread | The LL callback runs on every keystroke system-wide; it must return to `CallNextHookEx` immediately regardless of what a future subscriber does. Subscribers (the two coordinators) send messages whose registrations already `BeginInvoke` to the UI thread | Raising the event synchronously on the hook thread (correct today, one careless subscriber away from delaying system keyboard input) |
| H3 | Single fixed key: ESC (`VK_ESCAPE`), `WM_KEYDOWN` + `WM_SYSKEYDOWN` | Issue says "ESC at minimum"; configurability is YAGNI until a user asks | Configurable key/ignore-list settings |
| H4 | The `ComponentDispatcher` filters are **removed** from both coordinators — the hatch is the single ESC path | Keeping both double-fires when an EyeCtrl window is foreground; the old path is exactly the code #141 calls a false hatch | Keep old filters as fallback (duplicate sends) |
| H5 | Install failure is non-fatal: `IsActive == false`, no exception, app runs without the hatch | The hatch is best-effort; gaze-reachable exits (stop button, lost-gaze auto-exit, drag cancel button) remain. An AV-blocked hook must not take down an eye-controlled user's only input channel | Throw/log-and-crash |
| H6 | Native seam is `internal ILowLevelKeyboardInterop`, faked in `EyeCtrl.Platform.Tests` (IVT exists); real interop = thin calls into `NativeMethods` (`LibraryImport` conventions, entry-point naming rule honored) | Lifecycle + decision logic unit-tested without ever installing a real hook; CI (headless) never holds a system-wide keyboard hook | Testing against the real hook (untestable headless; would install a global hook on the CI runner) |
| H7 | Context wiring follows the existing seam: `EventCoordinationContext` gains `IGlobalEscapeHatch EscapeHatch`; DI singleton in `AppBootstrapper`; `TestFakes.MakeContext` gets a no-op fake | Same pattern as `IInputSimulator`; App.Tests coordinators construct without a real hook | Coordinators constructing their own hatch instance (two hooks) |
| H8 | Injected/synthesized keystrokes are **not** filtered (`LLKHF_INJECTED` ignored) | EyeCtrl itself synthesizes input; a gaze-typed ESC via the OSK triggering the hatch is a *feature* (a third, gaze-reachable exit), and EyeCtrl synthesizes no ESC today | Filter the injected flag |
| H9 | Coordinators keep their own state guard in the handler (drag: only send `DragCancelMessage` while `Dragging`; repeat: only while `RepeatMode`) | `OnDragCancel` sends an unconditional `MouseUp` — a stray mouse-up could release a caregiver's physically held button. The old filter had exactly this guard; it moves, not disappears | Rely on the state machine's guards alone (fine for repeat, wrong for drag) |
| H10 | Real-hook acceptance criteria (ESC while a third-party app is foreground, latency, leak) are owner-gated manual smoke; automated coverage = decision helper + lifecycle + event dispatch via the fake | Automated global-hook testing needs a desktop session with real input — out of CI's reach and unsafe on a gaze-driven machine | Automated UI tests |

## Components

```
EyeCtrl.Platform/Input/IGlobalEscapeHatch.cs     public interface: event Action? EscapePressed; bool IsActive; IDisposable
EyeCtrl.Platform/Input/GlobalEscapeHatch.cs      service: hook thread + pump + callback + escalation (+ internal ILowLevelKeyboardInterop,
                                                 internal LowLevelHookProc delegate, internal static IsEscapeKeyDown(long wParam, int vkCode))
EyeCtrl.Platform/Input/NativeMethods.cs          + SetWindowsHookEx / UnhookWindowsHookEx / CallNextHookEx / GetMessage / PostThreadMessage / GetModuleHandle, MSG struct
EyeCtrl.App/Services/AppBootstrapper.cs          + singleton IGlobalEscapeHatch → GlobalEscapeHatch
EyeCtrl.App/Services/IEventCoordinator.cs        + EventCoordinationContext.EscapeHatch (IGlobalEscapeHatch)
EyeCtrl.App/App.xaml.cs                          + context argument from DI
tests/App/Services/TestFakes.cs                  + FakeEscapeHatch (no-op) wired into MakeContext
DragCoordinator / RepeatModeController           − ComponentDispatcher filter; + EscapeHatch.EscapePressed subscription with state guard; unsubscribe on dispose
tests/Platform/Input/GlobalEscapeHatchTests.cs   decision + lifecycle + event via fake interop
```

## Error handling

- `SetWindowsHookEx` returns zero → thread sets `IsActive=false` and exits; no event ever fires.
- `Dispose` posts `WM_QUIT` to the hook thread, joins with 2 s timeout, idempotent.
- Process death removes an LL hook automatically (OS guarantee) — the Supervisor respawn path
  needs no special handling.
- No logging in the callback (H2); install failure is observable via `IsActive`.

## Testing

Platform.Tests (fake interop only — no real hook):
- `IsEscapeKeyDown`: WM_KEYDOWN+ESC true; WM_SYSKEYDOWN+ESC true; WM_KEYUP false; other vk false.
- Lifecycle: install called on the hook thread; `Dispose` unhooks; install-failure → `IsActive=false`, no throw.
- Event: captured hook proc invoked with ESC → `EscapePressed` raised (WaitHandle, pool thread).
App.Tests: `TestFakes` fake keeps coordinator construction green (no context-level coordinator tests — same precedent as the scroll/repeat controllers' static-only tests).

Out of scope: configurable keys, non-ESC gestures, Supervisor changes, hooking when running under CI.

## Review round 1 — external review dispositions (2026-09-08)

The external review (empirical: probed the live DLLs' exports, ran the real hatch, canary-measured the MSG buffer) found the feature **crashed on every launch**. All findings verified before fixing:

| # | Finding | Disposition |
|---|---------|-------------|
| R1 | Three P/Invoke declarations didn't resolve: `GetCurrentThreadId` on user32 (export lives in kernel32), `SetWindowsHookEx`/`PostThreadMessage` without their `W` entry points (LibraryImport's ExactSpelling looks up names the DLLs don't export). | **Fixed** — all three corrected (`kernel32` + two `EntryPoint` additions). The file's own header already documented this exact A/W trap from a prior crash; honored now. |
| R2 | The failure mode was a process kill, not degradation: the first native call threw `EntryPointNotFoundException` on the hook thread (no handler on background threads), the hatch is constructed unconditionally at startup → every launch, with Supervisor backoff relaunch = restart loop. H5 covered only the zero-handle return, not a throw. | **Fixed** — `HookThreadProc` wraps start-up in try/catch (any failure → `IsActive=false` + logged warning), `_started.Set()` runs on every path, the pump/unhook run in their own try/finally. Degradation pinned by `ThrowingInterop_DegradesToInactive_WithoutCrashing`. |
| R3 | `MSG` was 32 of 48 bytes (missing `time`, `pt`) — `GetMessageW` wrote 16 bytes past the declaration on every dequeued message. | **Fixed** — `Time` + `Pt` fields added; `Msg_MatchesTheNativeX64Layout` pins `Marshal.SizeOf == 48`. |
| R4 | Nothing read `IsActive` and the hatch never logged — a policy-blocked hook silently removed the caregiver's exit. | **Fixed** — `ILogger<GlobalEscapeHatch>` injected (NullLogger default, Platform precedent), warning on failed install / failed start / throwing subscriber, info on successful install; `App.xaml.cs` logs a startup warning when `!IsActive`. |
| R5 | No isolation around `handlers.Invoke()` on the pool thread — a subscriber throw kills the process exactly when the user is stuck. | **Fixed** — per-subscriber `GetInvocationList()` loop with try/catch, so one thrower neither kills the process nor skips its neighbours. Pinned by `ThrowingSubscriber_DoesNotKillTheDispatch_OthersStillRun`. |
| R6 | H2's premise was wrong for drag: `Messenger.Send` fans out synchronously and `DragTrailViewModel`'s `DragCancelMessage` handler does not marshal → `DwellDetector.ClearTargets()` off the UI thread. | **Fixed** — `OnEscapePressed` marshals via `Dispatcher.BeginInvoke` BEFORE the Send, with the state guard inside the dispatched action (a guard outside it would race a normally-completing drag into a stray `MouseUp`). Repeat mode gets the same shape for symmetry; its `ExitRepeatMode` remains state-guarded regardless. |
| R7 | Wiring untested: `FakeEscapeHatch.Press()` existed but was never called; the suite was 97/97 green on a build that could not run. | **Partly fixed** — `EscapeHatchWiringTests` (2 [StaFact]s) drive the real coordinators through the fake hatch + real Dispatcher (guard paths); `NativeMethodEntryPointTests` reflects over every `LibraryImport` in `NativeMethods` and resolves it to a real export via `LoadLibrary`/`GetProcAddress` — mutation-checked (reintroducing one bug fails it). The env-gated `RealHook_InstallsAndDisposes_WhenSmokeEnabled` (`EYECTRL_HATCH_SMOKE=1`) reproduces the reviewer's probe: run post-fix, the REAL hatch installs (`IsActive=true`) and disposes cleanly. Positive ESC paths still need overlay windows (concrete WPF types) → remain on the PR's manual-smoke checklist. |
