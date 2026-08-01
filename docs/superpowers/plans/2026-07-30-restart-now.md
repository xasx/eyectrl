# "Restart Now" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the user a gaze-reachable "Restart Now" that brings the app back, so settings that only apply at startup (the theme picker, first among them) can be applied without waiting for the next logon.

**Architecture:** Two strategies behind one button, tried in order of safety. When a supervisor is running, it performs the relaunch: the app flags `restartRequested` on its final heartbeat and the supervisor treats that as "clean exit, then launch again" — no crash strike, no backoff. When no supervisor answers, the app restarts **itself** via a handshake: it launches a child, waits (off the UI thread) for that child to prove it started, and only then exits. If neither strategy can prove a successor is coming, the restart is abandoned and the app keeps running.

**Tech Stack:** C# / .NET 10 / WPF, named-pipe IPC (`eyectrl_watch`), xUnit v3 + NFluent (`Check.That`).

**Prerequisite:** This is the follow-up named in `docs/superpowers/plans/2026-07-30-itheme-theme-system.md` ("Why 'restart to apply' rather than a restart button"). The theme slice does **not** depend on this one; this one is worth doing only if that picker exists, since it is the first setting that needs it.

---

## Two strategies, and why the order matters

**Supervisor-mediated is preferred wherever it is available**, because it is the only one where a *failed* relaunch is still recovered: if `Process.Start` throws, `DoLaunchApp` logs and deliberately leaves the machine in `Launching` (`src/EyeCtrl.Supervisor/Program.cs:399-422`), where the startup-grace check fires `StartupGraceElapsed → GoRecovering` (`SupervisionStateMachine.cs:110-115`) and retries. Nothing in-process can offer that, because the thing that would do the retrying is the thing that exited.

**Self-restart is the fallback, and it has one genuine obstacle: the mutex.** `SingleInstanceGuard.TryAcquire` uses `WaitOne(0)` (`src/EyeCtrl.Core/Services/SingleInstanceGuard.cs`), so a child launched while the parent still holds the mutex exits instantly (`src/EyeCtrl.App/App.xaml.cs:81-87`), and the mutex is not released until `OnExit` runs (`App.xaml.cs:311` today; Task 6 Step 3 moves it to the *end* of `OnExit`, so the successor cannot start while this instance still holds the settings file, the Tobii device or the dwell pipe). Naively, then, the parent cannot confirm a healthy child before committing to exit — which is what made this look unsafe.

**The handshake dissolves that.** Split "the child started" from "the child took over":

1. Parent launches the child with `--restart-handoff`.
2. Child starts, and **before** waiting for the mutex, signals a named event: *I am alive and I am queued for the mutex.*
3. Parent — waiting off the UI thread, so the app stays responsive to gaze throughout — sees the signal and only then calls `Shutdown()`.
4. Parent's `OnExit` releases the mutex; the child's wait returns and it initialises normally.

Signalling *before* the wait is what makes it work: reversing those two steps deadlocks, since the parent would wait for a readiness the child cannot reach until the parent is gone.

By the time the parent commits, the child has demonstrated that the exe path resolves, the runtime loads, the bundle extracts, and no AV or policy blocked the launch — which is essentially every realistic failure. If the signal never arrives, the parent kills the orphaned child, reports, and stays running.

Two residual risks remain, and they are real:

- **The child dies during initialisation, after signalling** (a Tobii or DI failure). Nothing in-process can retry it. Task 12 addresses this at the source rather than cushioning it: when a supervisor is installed but idle, the restart prompt offers to start it, which moves the whole operation onto the recoverable path *and* leaves the replacement watched afterwards. Where no supervisor is installed, this risk stands, and the plan says so rather than pretending otherwise.
- **`AbandonedMutexException` already covers the parent dying mid-exit** — the guard treats an abandoned mutex as acquired (`SingleInstanceGuard.cs`), so a parent that crashes while shutting down still hands over cleanly. This one is already handled; it is listed so nobody "fixes" it later.

---

## Scope boundaries — read before starting

- **Works with or without a supervisor.** `SupervisionSettings.Enabled` defaults to **`false`** (`src/EyeCtrl.Core/Settings/SupervisionSettings.cs:10`), so the unsupervised path is the *common* case, not an edge case. The button is always offered.
- **A successor must be proven before the app exits, on both paths.** Supervised: the supervisor must answer a probe on the heartbeat pipe — `Supervision.Enabled == true` only records that one was *configured*, not that one is *running*. Unsupervised: the child must signal that it started. If neither can be proven, the restart is abandoned, the app stays running, and the user is told. **This is the single most important requirement in the plan** — every task that touches it says so.
- **The readiness wait never blocks the UI thread.** A frozen dispatcher means no gaze processing, i.e. the user loses control of the machine for the duration of the wait — the exact outcome this feature is supposed to avoid. The wait runs on a background thread and marshals back to the dispatcher to shut down.
- **A restart behaves exactly like a normal launch.** The supervisor relaunches without `--paused` (`Program.cs:411`), so the app applies `General.StartPaused` itself, as at logon. Current pause state is deliberately **not** carried across: predictable beats clever, and "restart put me back exactly where a fresh start would" is the easier rule to hold in your head.
- **No changes to crash accounting.** A user-requested restart books no strike and does not touch `ConsecutiveFailures`, the crash window, or the fallback-AT escalation. Task 3 additionally exempts it from backoff.
- **One end-to-end step needs a real launch.** Tasks 1-13 are fully headless. Task 14 runs the three smoke scenarios and is marked **STOP — requires explicit go-ahead**. All are scripted to be short and log-driven rather than screen-driven.
- **The app never launches a fallback AT.** Escalating to another assistive technology stays the supervisor's job (`Program.cs`, `OnFallenBack`). Where the app can help, it offers to start the supervisor instead.

---

## Preconditions — check these before Task 1

This plan branches from `main`, which now includes the merged overlay-unification work (PR #72).

```bash
git merge-base --is-ancestor origin/main HEAD && echo "based on main"
test -f src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs && echo supervisor ok
dotnet build EyeCtrl.slnx --nologo                          # 0 errors
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo   # 1710 passing
```

**Line numbers in this plan are pinned to `4451cdb`** — the merge commit on `main`, whose tree is byte-identical to the `f02cd2a` the plan was originally written against.

**This plan overlaps with the ITheme plan on three files**, and whichever lands second will have to reconcile:

| File | ITheme touches | This plan touches |
|---|---|---|
| `src/EyeCtrl.App/App.xaml.cs` | theme activation after settings load (~line 101) | handoff arg (74-87), coordinator (~215), `OnExit` (300-311) |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | theme picker + delete dead toggle in `BuildGeneralPages` | restart button in `BuildGeneralPages`, modal rework |
| `src/EyeCtrl.ViewModels/SettingsViewModel.cs` | replace 3 dead theme items with 1 combo | append a restart action button |

The edits are in different regions of `App.xaml.cs`, so that one should merge cleanly. `BuildGeneralPages` and the General tab item list are the genuine collision points — both plans add an entry to the same list. Task 13 Step 6 already carries a note for the case where ITheme landed first.

Test output is German: per-test failures read `Fehler <FQN>`, not `Failed`.

---

## The flow, end to end

```
User dwells "Restart Now"  (SettingsWindow, General page)
  → ShowModal("Restart EyeCtrl now?")                       [existing confirm pattern]
  → IMessenger.Send(RestartAppRequestedMessage)
  → AppRestartCoordinator.HandleRestart() — ALL strategy work runs off the UI thread
       (probe, bring-up poll, self-restart wait); the dispatcher only ever executes
       the shutdown or the refusal
       │
       ├─ STRATEGY 1 — supervisor-mediated (preferred: recovers a failed relaunch)
       │    Supervision.Enabled && publisher.TryReachSupervisor()
       │      → back on the dispatcher: SupervisorRelaunchRequested = true; Shutdown()
       │      → OnExit stamps { shuttingDown: true, restartRequested: true }
       │      → supervisor: Supervising → CleanExitPending (remembers restart)
       │      → ProcessExited → Launching + LaunchApp   (no strike, no backoff)
       │      → DoLaunchApp → app starts → heartbeat → Supervising
       │
       └─ STRATEGY 2 — self-restart (no supervisor answering)
              exePath = Environment.ProcessPath            → null?    abort
              create Local\EyeCtrl.App.RestartReady (unset) — PARENT owns it for the wait
              Process.Start(exePath, "--restart-handoff")  → throws?  abort
              wait up to 30s on the event the parent owns
                ├─ child exited early / never signalled → kill child, abort
                └─ signalled → back on the dispatcher: Shutdown()
                     → OnExit releases the mutex (restartRequested stays FALSE)
                     → child's mutex wait returns → child initialises normally

  abort (either strategy) = report to the user, STAY RUNNING
```

Child, when started with `--restart-handoff`:

```
OpenExisting + Set Local\EyeCtrl.App.RestartReady   ← BEFORE waiting, or this deadlocks
SingleInstanceGuard.TryAcquire(30s)          ← waits instead of failing fast
  ├─ acquired  → carry on with normal startup
  └─ timed out → exit quietly (the parent never left; do not fight it)
```

Failure branches, all landing somewhere safe:

| Failure | Outcome |
|---|---|
| Supervisor unreachable | Falls through to self-restart |
| `Environment.ProcessPath` null, or `Process.Start` throws | Abort, app keeps running, user is told |
| Child exits before signalling ready | Abort, app keeps running, user is told |
| Child never signals within 30s | Child killed, abort, app keeps running, user is told |
| Parent hangs during `OnExit` | Child's mutex wait times out after 30s and it exits quietly, leaving one instance rather than two. **Traceless from the child's side** — logging is not configured that early (Task 6 Step 2); diagnose from the parent's refusal modal and `Restart refused` log line |
| Parent killed mid-exit | Mutex is abandoned; the guard treats that as acquired, so the child takes over (`SingleInstanceGuard.cs`) |
| Child signals, then dies during init | **The residual risk**, on the self-restart path only. Task 12's offer moves the operation onto the supervised path, where it is recovered |
| Supervisor dies between probe and exit | App exits, no relaunch. **Accepted residual risk** — a ~2s window, indistinguishable from the supervisor crashing at any other moment |
| App signals restart but hangs (supervised) | 5s deadline → `DeadlineElapsed` → `GoRecovering(forceKill: true)` → killed and relaunched (`SupervisionStateMachine.cs:135-137`) |
| `Process.Start` throws on relaunch (supervised) | State stays `Launching` → startup grace → `GoRecovering` → retry (`Program.cs:416-420`) |

---

## File Structure

**New files:**

| Path | Responsibility |
|---|---|
| `src/EyeCtrl.ViewModels/Messages/AppLifecycleMessages.cs` | `RestartAppRequestedMessage`, `RestartUnavailableMessage` — the UI's only coupling to the mechanism. |
| `src/EyeCtrl.App/Services/AppRestartCoordinator.cs` | Chooses a strategy and owns the refusal. The one place the safety rule lives. |
| `src/EyeCtrl.App/Services/SelfRestartStrategy.cs` | Launch child, await its readiness signal, or abort. Seams for the process and the wait so it is testable headlessly. |
| `src/EyeCtrl.App/Services/RestartHandoff.cs` | The two names the parent and child agree on, and the child's signal. |
| `src/EyeCtrl.Supervisor/Supervision/RelaunchDelayPolicy.cs` | Pure: how long to wait before a relaunch. Extracted so the no-backoff rule is testable without a WPF `Application`. |
| `src/EyeCtrl.App/Services/SupervisorLauncher.cs` | Locates and starts the supervisor. Resolution mirrors how the supervisor locates the app. |
| `tests/EyeCtrl.App.Tests/SupervisorLauncherTests.cs` | Exe resolution: sibling, env override, stale override, none installed. |
| `tests/EyeCtrl.Supervisor.Tests/Supervision/RestartRequestTests.cs` | State-machine behaviour for the restart path. |
| `tests/EyeCtrl.Supervisor.Tests/Supervision/RelaunchDelayPolicyTests.cs` | The no-backoff rule. |
| `tests/EyeCtrl.App.Tests/HeartbeatRestartPayloadTests.cs` | Payload carries the flag; probe reports real delivery against a live pipe. |
| `tests/EyeCtrl.App.Tests/AppRestartCoordinatorTests.cs` | Strategy selection, and abort when neither can prove a successor. |
| `tests/EyeCtrl.App.Tests/SelfRestartStrategyTests.cs` | Every abort branch, plus "signals ready ⇒ proceed". |
| `tests/EyeCtrl.App.Tests/Services/SingleInstanceHandoffTests.cs` | The waiting acquire, including the real hand-over between two threads. |
| `src/EyeCtrl.ViewModels/ModalChoice.cs` | One modal button: id, label, semantic kind, action. Kind not colour — ViewModels cannot see the palette. |
| `src/EyeCtrl.App/Views/ModalButtonPalette.cs` | Fill/label pairing per kind, so a dark neutral fill gets a light label. |
| `tests/EyeCtrl.App.Tests/ModalButtonPaletteTests.cs` | Every kind clears 4.5:1 label-on-fill. |

**Modified files:**

| Path | Change |
|---|---|
| `src/EyeCtrl.Core/Models/HeartbeatPayload.cs:13-19` | 7th member, `RestartRequested`, defaulted. |
| `src/EyeCtrl.App/Services/HeartbeatPublisher.cs:97-108,132-156` | `SignalShuttingDown(bool)`, `TryReachSupervisor()`, `SendOneAsync` reports success. |
| `src/EyeCtrl.Supervisor/Supervision/SupervisionInputs.cs:12` | `CleanExitRequested` gains a defaulted `RestartRequested`. |
| `src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs:90-104` | Remember the request; branch `ProcessExited`. |
| `src/EyeCtrl.Supervisor/Program.cs:280-286,366-378` | Thread the flag in; zero backoff for a restart-originated launch. |
| `src/EyeCtrl.Core/Services/SingleInstanceGuard.cs` | `TryAcquire(TimeSpan)` — wait for the mutex instead of failing fast. |
| `src/EyeCtrl.App/App.xaml.cs:74-87,~215,275-286,300-330` | Handoff arg; move the mutex release to the end of `OnExit`; construct the coordinator; pass the flag to `SignalShuttingDown`. |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:901,99-106` | The button; display a refusal. |
| `src/EyeCtrl.ViewModels/SettingsViewModel.cs:~171-179` | The matching `ActionButtonSettingItem`. |
| `src/EyeCtrl.App/Services/HeartbeatPublisher.cs` (again) | `EnablePublishing()` so a session can start publishing mid-flight (Task 11). |
| `src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs:207-220,289-330` | `ModalChoices`; generalise the modal beyond Yes/No (Task 12). |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:1300-1373` | Render N choices; vertical stack past two; route mouse through the ViewModel (Task 12). |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | `SetRestartCoordinator`; pass the coordinator to `SettingsWindow` at construction (Task 13). |
| `tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs` | Join the `HeartbeatPipe` collection so it serializes against `HeartbeatRestartPayloadTests` (Task 1). |
| `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs` | Multi-choice modal cases beside the four existing ones (Task 12). |

---

## Task 1: `restartRequested` on the wire

**Files:**
- Modify: `src/EyeCtrl.Core/Models/HeartbeatPayload.cs:13-19`
- Modify: `src/EyeCtrl.App/Services/HeartbeatPublisher.cs:97-108,132-156`
- Test: `tests/EyeCtrl.App.Tests/HeartbeatRestartPayloadTests.cs` (create)

Two changes to the publisher beyond the flag itself. `SendOneAsync` currently returns `void`-ish `Task` and silently returns on connect timeout (`HeartbeatPublisher.cs:145-152`), so no caller can tell a delivered payload from a dropped one — Task 8's safety rule needs exactly that distinction. And `TryReachSupervisor` must **not** touch `_shuttingDown`: that field latches (`HeartbeatPublisher.cs:99`), so a probe that set it and then aborted would make every subsequent periodic `Stamp` announce a shutdown that never comes, arming the supervisor's 5s deadline and getting the app force-killed.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/HeartbeatRestartPayloadTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// Test collection for every class that binds the real <c>eyectrl_watch</c> pipe name.
/// The pipe server is constructed with <c>maxInstances: 1</c>, and xUnit v3 runs test
/// classes in parallel — two classes standing up a server concurrently means the second
/// construction throws <c>IOException: All pipe instances are busy</c> and the failure
/// is intermittent. <c>HeartbeatPublisherTests</c> (same assembly, same pipe name) joins
/// this collection so the two classes serialize against each other.
/// </summary>
[CollectionDefinition("HeartbeatPipe")]
public sealed class HeartbeatPipeCollection
{
}

/// <summary>
/// The restart request travels on the existing heartbeat pipe. These tests stand up a
/// real NamedPipeServerStream, so "was it actually delivered?" is answered by the
/// wire rather than by a mock — which is the question AppRestartCoordinator bets the
/// user's only input channel on.
/// </summary>
[Collection("HeartbeatPipe")]
public class HeartbeatRestartPayloadTests
{
    private const string PipeName = "eyectrl_watch";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Accepts one connection and returns the payload it received, or null on timeout.</summary>
    private static async Task<HeartbeatPayload?> ReceiveOneAsync(TimeSpan timeout)
    {
        await using var server = new NamedPipeServerStream(
            PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var cts = new CancellationTokenSource(timeout);
        try { await server.WaitForConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }

        using var reader = new StreamReader(server, Encoding.UTF8);
        var line = await reader.ReadLineAsync();
        return line is null ? null : JsonSerializer.Deserialize<HeartbeatPayload>(line, JsonOptions);
    }

    [Fact]
    public void Payload_DefaultsRestartRequestedToFalse()
    {
        // The flag is additive: every existing construction site must keep compiling
        // and keep meaning "no restart".
        var p = new HeartbeatPayload(1, DateTime.UtcNow, true, false, DateTime.UtcNow, ShuttingDown: false);
        Check.That(p.RestartRequested).IsFalse();
    }

    [Fact]
    public async Task SignalShuttingDown_WithRestart_PutsBothFlagsOnTheWire()
    {
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));

        using var publisher = new HeartbeatPublisher(supervised: true);
        publisher.SignalShuttingDown(restartRequested: true);

        var payload = await received;
        Check.WithCustomMessage("no payload arrived on the pipe").That(payload).IsNotNull();
        Check.That(payload!.ShuttingDown).IsTrue();
        Check.That(payload.RestartRequested).IsTrue();
    }

    [Fact]
    public async Task SignalShuttingDown_WithoutRestart_LeavesTheFlagClear()
    {
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));

        using var publisher = new HeartbeatPublisher(supervised: true);
        publisher.SignalShuttingDown();

        var payload = await received;
        Check.That(payload).IsNotNull();
        Check.That(payload!.ShuttingDown).IsTrue();
        Check.That(payload.RestartRequested).IsFalse();
    }

    [Fact]
    public async Task TryReachSupervisor_IsTrue_WhenAServerIsListening()
    {
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));

        using var publisher = new HeartbeatPublisher(supervised: true);
        var reachable = publisher.TryReachSupervisor();

        Check.That(reachable).IsTrue();
        Check.That(await received).IsNotNull();
    }

    [Fact]
    public void TryReachSupervisor_IsFalse_WhenNothingIsListening()
    {
        // No server stood up. This is the case that must stop a restart dead.
        using var publisher = new HeartbeatPublisher(supervised: true);
        Check.That(publisher.TryReachSupervisor()).IsFalse();
    }

    [Fact]
    public void TryReachSupervisor_IsFalse_WhenNotSupervised()
    {
        // Standalone mode starts no writer at all; there is no supervisor by construction.
        using var publisher = new HeartbeatPublisher(supervised: false);
        Check.That(publisher.TryReachSupervisor()).IsFalse();
    }

    [Fact]
    public async Task TryReachSupervisor_DoesNotLatchTheShutdownFlag()
    {
        // The probe must be semantically inert. If it set _shuttingDown, an aborted
        // restart would leave every later Stamp announcing a shutdown that never
        // comes — the supervisor arms its 5s deadline and force-kills a healthy app.
        var probe = ReceiveOneAsync(TimeSpan.FromSeconds(5));

        using var publisher = new HeartbeatPublisher(supervised: true);
        _ = publisher.TryReachSupervisor();
        var probePayload = await probe;
        Check.That(probePayload!.ShuttingDown).IsFalse();
        Check.That(probePayload.RestartRequested).IsFalse();

        publisher.Stamp(isConnected: true, isPaused: false);
        Check.That(publisher.CurrentPayload!.ShuttingDown).IsFalse();
    }
}
```

Also join the pre-existing pipe-bound class to the same collection, or the two classes collide under xUnit v3's parallel class execution. In `tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs`, annotate the class:

```csharp
[Collection("HeartbeatPipe")]
public class HeartbeatPublisherTests
```

its `SignalShuttingDown_DeliversFinalPayload_EvenUnderBlockingSyncContext` test (line 44) binds the same `eyectrl_watch` name with `maxInstances: 1`; without the shared collection, one of the two concurrent server constructions throws `IOException: All pipe instances are busy` and the failure is intermittent. (A live supervisor on the machine produces the same symptom — see Step 6.)

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HeartbeatRestartPayloadTests" --nologo
```

Expected: **build failure**, `CS1739`/`CS0117` — `HeartbeatPayload` has no `RestartRequested`, `HeartbeatPublisher` has no `TryReachSupervisor`.

- [ ] **Step 3: Extend the payload**

Replace `src/EyeCtrl.Core/Models/HeartbeatPayload.cs:13-19`:

```csharp
public record HeartbeatPayload(
    int Pid,
    DateTime StartedAtUtc,
    bool IsConnected,
    bool IsPaused,
    DateTime LastFrameUtc,
    bool ShuttingDown);
```

with:

```csharp
public record HeartbeatPayload(
    int Pid,
    DateTime StartedAtUtc,
    bool IsConnected,
    bool IsPaused,
    DateTime LastFrameUtc,
    bool ShuttingDown,
    bool RestartRequested = false);
```

and add the parameter's doc line after the `ShuttingDown` one at line 12:

```csharp
/// <param name="RestartRequested">
/// <c>true</c> when the final payload accompanies a user-requested restart: the
/// supervisor should relaunch after the process exits, without booking a crash
/// strike. Only meaningful alongside <paramref name="ShuttingDown"/>.
/// </param>
```

Defaulted so every existing construction site keeps compiling and keeps meaning "no restart".

- [ ] **Step 4: Make `SendOneAsync` report delivery**

In `src/EyeCtrl.App/Services/HeartbeatPublisher.cs`, change the signature at line 132:

```csharp
    private static async Task SendOneAsync(HeartbeatPayload payload, CancellationToken token)
```

to:

```csharp
    /// <summary>
    /// Sends one payload. Returns <c>true</c> only if the write completed — the
    /// connect-timeout path returns <c>false</c> rather than succeeding silently,
    /// because <see cref="TryReachSupervisor"/> stakes the restart decision on the
    /// difference between "delivered" and "dropped".
    /// </summary>
    private static async Task<bool> SendOneAsync(HeartbeatPayload payload, CancellationToken token)
```

Then in the timeout branch (line 151), replace `return;` with `return false;`, and after the write at line 155 add `return true;`:

```csharp
            _ = connectTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return false;
        }
        await connectTask.ConfigureAwait(false); // observe exceptions if connect threw
        var json = JsonSerializer.Serialize(payload, JsonOptions) + "\n";
        await client.WriteAsync(Encoding.UTF8.GetBytes(json), token).ConfigureAwait(false);
        return true;
    }
```

The writer loop at line 127 discards the result; change `try { await SendOneAsync(payload, _cts.Token); }` to `try { _ = await SendOneAsync(payload, _cts.Token); }` so the intent to ignore it is explicit.

- [ ] **Step 5: Remember `supervised`, add the restart parameter and the probe**

In `src/EyeCtrl.App/Services/HeartbeatPublisher.cs`, add a field beside `_shuttingDown` (line 52):

```csharp
    private readonly bool _supervised;
```

and set it in the constructor (lines 64-67):

```csharp
    public HeartbeatPublisher(bool supervised)
    {
        _supervised = supervised;
        if (supervised) StartWriter();
    }
```

Replace `SignalShuttingDown` (lines 97-108) with:

```csharp
    public void SignalShuttingDown(bool restartRequested = false)
    {
        _shuttingDown = true;
        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
        var final = new HeartbeatPayload(
            _pid, _startedAtUtc, false, false, lastFrame,
            ShuttingDown: true, RestartRequested: restartRequested);
        _pending = final;
        // Synchronous bounded flush — a fresh CancellationTokenSource (NOT _cts) so Dispose's cancel
        // can't race it. If the send can't complete in 2s (no server / slow server), give up; the
        // supervisor's heartbeat-stale path will then treat it as a crash, which is the safe default.
        try { SendOneAsync(final, CancellationToken.None).Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort — never block OnExit on the final send */ }
    }

    /// <summary>
    /// Sends one ordinary payload and reports whether it was actually delivered — i.e.
    /// whether a supervisor is listening on the pipe *right now*.
    /// <c>Supervision.Enabled</c> only records that the user configured one; the app can
    /// still be launched by hand with no supervisor running, and in that case a restart
    /// would exit into nothing.
    ///
    /// Deliberately inert: it does NOT set <see cref="_shuttingDown"/>. That field latches,
    /// so a probe that set it and then aborted would make every later <see cref="Stamp"/>
    /// announce a shutdown that never arrives — arming the supervisor's 5s clean-exit
    /// deadline and getting a healthy app force-killed.
    ///
    /// The caller's wait is bounded by <see cref="ProbeWait"/>, not by the 2s connect
    /// timeout: <see cref="AppRestartCoordinator.ShouldOfferSupervisor"/> runs this on the
    /// dispatcher, and a live supervisor answers in single-digit milliseconds. Anything
    /// slower is not a supervisor worth freezing the user's gaze control for — a stale
    /// probe simply reads false and the restart falls through to the self-restart
    /// strategy, which is the safe direction.
    /// </summary>
    /// <returns><c>true</c> if the payload reached the supervisor within the timeout.</returns>
    public bool TryReachSupervisor()
    {
        if (!_supervised) return false;   // standalone: no writer, no supervisor, no channel

        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
        var probe = new HeartbeatPayload(
            _pid, _startedAtUtc, false, false, lastFrame,
            ShuttingDown: false, RestartRequested: false);

        try
        {
            var send = SendOneAsync(probe, CancellationToken.None);
            return send.Wait(ProbeWait) && send.Result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// How long a supervisor probe may block the caller before the supervisor is treated
    /// as absent. A live supervisor answers in milliseconds; 500ms also matches the
    /// bring-up poll interval (Task 13). The probe's connect may run past this in the
    /// background — it is bounded by <see cref="ConnectTimeout"/> and its faulted outcome
    /// is observed there — but the caller never waits on it.
    /// </summary>
    private static readonly TimeSpan ProbeWait = TimeSpan.FromMilliseconds(500);
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HeartbeatRestartPayloadTests" --nologo
```

Expected: 0 errors; 7 passing. These bind the real `eyectrl_watch` pipe name, so **a running supervisor on the same machine will steal the connection and make them flake.** If they fail intermittently, check for a live `EyeCtrl.Supervisor` process before debugging the code. (Collision with the sibling `HeartbeatPublisherTests` class is already excluded — the shared `HeartbeatPipe` collection from Step 1 serializes the two.)

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Core/Models/HeartbeatPayload.cs src/EyeCtrl.App/Services/HeartbeatPublisher.cs tests/EyeCtrl.App.Tests/HeartbeatRestartPayloadTests.cs tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs
git commit -m "feat(restart): carry restartRequested on the heartbeat and report real delivery"
```

---

## Task 2: The supervisor relaunches after a restart-flagged clean exit

**Files:**
- Modify: `src/EyeCtrl.Supervisor/Supervision/SupervisionInputs.cs:12`
- Modify: `src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs:90-104`
- Test: `tests/EyeCtrl.Supervisor.Tests/Supervision/RestartRequestTests.cs` (create)

The machine reuses `CleanExitPending` rather than gaining a new state. That is deliberate: the 5s deadline (`Program.cs:69`), the `HeartbeatReceived` case (`SupervisionStateMachine.cs:74`) and the hung-shutdown recovery (`SupervisionStateMachine.cs:135-137`) are all correct for a restart exactly as they are for a plain exit, and a parallel state would have to duplicate all three.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Supervisor.Tests/Supervision/RestartRequestTests.cs`:

```csharp
namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// A user-requested restart is a clean exit that is followed by a launch. It must not
/// look like a crash in any respect: no strike, no consumed crash budget, no step
/// toward the fallback-AT escalation.
/// </summary>
public class RestartRequestTests
{
    private static readonly DateTime T0 = new(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc);

    private static SupervisionStateMachine Supervising()
    {
        var sm = NewMachine();
        sm.Handle(new AdoptRequest(T0));
        return sm;
    }

    [Fact]
    public void Restart_AfterExit_GoesLaunching_AndCommandsLaunchApp()
    {
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        Check.That(sm.State).IsEqualTo(SupervisorState.CleanExitPending);

        sm.Handle(new ProcessExited(T0.AddSeconds(6)));

        Check.That(sm.State).IsEqualTo(SupervisorState.Launching);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Restart_BooksNoStrike()
    {
        // The whole point: restarting to apply a setting must not spend crash budget,
        // or a few theme changes plus one real crash would escalate to the fallback AT.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(6)));

        Check.That(sm.ConsecutiveFailures).IsEqualTo(0);
    }

    [Fact]
    public void PlainCleanExit_StillGoesIdleWatch_WithNoLaunch()
    {
        // Regression guard on the existing contract — the restart branch must be opt-in.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(6)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Restart_FromAnUnverifiedClient_IsIgnored()
    {
        // Same rule as clean exit: an untrusted sender must not be able to drive the
        // supervised process's lifecycle.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: false, RestartRequested: true));

        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);

        sm.Handle(new ProcessExited(T0.AddSeconds(6)));

        // Unannounced exit from Supervising -> recovery, exactly as if no request had come.
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.ConsecutiveFailures).IsEqualTo(1);
    }

    [Fact]
    public void Restart_ThatNeverExits_IsForceKilledAndRecovered()
    {
        // The app claimed it was restarting and then hung. The existing 5s deadline
        // must still apply — a restart is not a licence to stop watching.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        sm.Handle(new DeadlineElapsed(T0.AddSeconds(11)));

        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.Commands).Contains(SupervisorCommand.ForceKill);
    }

    [Fact]
    public void RestartIntent_DoesNotSurviveIntoTheNextCleanExit()
    {
        // The flag must be per-request. A restart, then later a genuine quit, must
        // leave the supervisor idle rather than relaunching an app the user closed.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(6)));       // -> Launching
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(20)));  // -> Supervising

        sm.Handle(new CleanExitRequested(T0.AddSeconds(30), ClientVerified: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(31)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
    }
}
```

Add the same private helper the sibling test file uses (`SupervisionStateMachineTests.cs:175-176`) as the last member of the class:

```csharp
    private static SupervisionStateMachine NewMachine(int maxRetries = 3, int windowSeconds = 120)
        => new(maxRetries, windowSeconds);
```

`EyeCtrl.Supervisor.Tests` has global usings for `Xunit`, `NFluent` and `System` — `SupervisionStateMachineTests.cs` declares only `using EyeCtrl.Supervisor.Supervision;` — so the file above needs no further using directives. (`EyeCtrl.App.Tests` is the opposite: it spells out `using NFluent; using Xunit;` per file. Follow each project's own convention.)

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~RestartRequestTests" --nologo
```

Expected: **build failure** — `CleanExitRequested` takes no `RestartRequested`.

- [ ] **Step 3: Extend the input record**

In `src/EyeCtrl.Supervisor/Supervision/SupervisionInputs.cs`, replace line 12:

```csharp
public sealed record CleanExitRequested(DateTime At, bool ClientVerified) : SupervisionInput(At);
```

with:

```csharp
/// <param name="RestartRequested">
/// <c>true</c> when the app asked to be relaunched after it exits (user-requested
/// restart). Books no strike and skips backoff; see RelaunchDelayPolicy.
/// </param>
public sealed record CleanExitRequested(DateTime At, bool ClientVerified, bool RestartRequested = false)
    : SupervisionInput(At);
```

Defaulted, so the three existing construction sites in `SupervisionStateMachineTests.cs:95,107,131` and the one in `Program.cs:285` keep compiling.

- [ ] **Step 4: Branch the state machine**

In `src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs`, add a field beside the other private state (near line 12):

```csharp
    /// <summary>
    /// Set when the pending clean exit is a user-requested restart, so ProcessExited
    /// relaunches instead of going idle. Per-request: cleared as soon as it is consumed,
    /// and cleared on a plain clean exit, so it can never leak into a later genuine quit.
    /// </summary>
    private bool _restartAfterExit;
```

Replace the `CleanExitRequested` case (lines 90-99):

```csharp
            case CleanExitRequested ce when State == SupervisorState.Supervising:
                // Spec: the shuttingDown signal "must come from the verified supervised
                // process, not an untrusted sender." An unverified request is IGNORED —
                // it must not suppress crash recovery.
                if (ce.ClientVerified)
                {
                    State = SupervisorState.CleanExitPending;
                    _commands.Add(SupervisorCommand.NotifyCleanExit);
                }
                break;
```

with:

```csharp
            case CleanExitRequested ce when State == SupervisorState.Supervising:
                // Spec: the shuttingDown signal "must come from the verified supervised
                // process, not an untrusted sender." An unverified request is IGNORED —
                // it must not suppress crash recovery. That applies to the restart flag
                // too: an untrusted sender must not be able to cycle the app.
                if (ce.ClientVerified)
                {
                    State = SupervisorState.CleanExitPending;
                    _restartAfterExit = ce.RestartRequested;
                    _commands.Add(SupervisorCommand.NotifyCleanExit);
                }
                break;
```

Replace the `ProcessExited` case for `CleanExitPending` (lines 101-104):

```csharp
            case ProcessExited when State == SupervisorState.CleanExitPending:
                State = SupervisorState.IdleWatch; // authenticated clean exit within deadline
                _supervisingSince = null;
                break;
```

with:

```csharp
            case ProcessExited when State == SupervisorState.CleanExitPending:
                _supervisingSince = null;
                if (_restartAfterExit)
                {
                    // User-requested restart: relaunch, and book NO strike. This is not a
                    // crash, so it must not consume crash budget or step toward the
                    // fallback-AT escalation. Consumed here so the intent cannot leak
                    // into a later genuine quit.
                    _restartAfterExit = false;
                    State = SupervisorState.Launching;
                    _launchingSince = DateTime.UtcNow;   // same source as the LaunchRequest case
                    _commands.Add(SupervisorCommand.LaunchApp);
                }
                else
                {
                    State = SupervisorState.IdleWatch; // authenticated clean exit within deadline
                }
                break;
```

Finally, clear the flag in `GoRecovering` so a hung restart does not leave it set — add as the first line of the method body (near line 141):

```csharp
        _restartAfterExit = false;
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests --nologo
```

Expected: all pass, including the pre-existing `SupervisionStateMachineTests` — in particular `CleanExit_AuthenticatedWithinDeadline_GoesIdleWatch_NoRelaunch` (`SupervisionStateMachineTests.cs:90`), which is the contract the new branch must not disturb.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Supervisor/Supervision/SupervisionInputs.cs src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs tests/EyeCtrl.Supervisor.Tests/Supervision/RestartRequestTests.cs
git commit -m "feat(restart): relaunch after a restart-flagged clean exit, without a strike"
```

---

## Task 3: A restart does not inherit crash backoff

**Files:**
- Create: `src/EyeCtrl.Supervisor/Supervision/RelaunchDelayPolicy.cs`
- Create: `tests/EyeCtrl.Supervisor.Tests/Supervision/RelaunchDelayPolicyTests.cs`
- Modify: `src/EyeCtrl.Supervisor/Program.cs:366-378`

`DrainCommands` currently delays every `LaunchApp` by `NextBackoffDelay` (`Program.cs:377`), which is `0/5/15/60s` by `ConsecutiveFailures` (`BackoffPolicy.cs`). A restart books no strike, so the count is *usually* 0 and the delay *usually* 0 — but if the app crashed within the recent crash window and the user then asks for a restart, they would dwell a button and wait up to a minute with no feedback. On this app that reads as a hang, and the user cannot alt-tab to check.

The rule is extracted to a pure helper because `SupervisorApp` is a WPF `Application` (`Program.cs:61`) and cannot be exercised headlessly.

**The discriminator is the state PAIR, not the previous state alone.** Two transitions leave `CleanExitPending` emitting `LaunchApp`, and only one of them is a restart:

| Transition | Emits | Strike? | Correct delay |
|---|---|---|---|
| `ProcessExited` → `Launching` (Task 2's restart branch) | `LaunchApp` | no | **zero** |
| `DeadlineElapsed` → `Recovering`/`FallenBack` (`SupervisionStateMachine.cs:135-137`) | `ForceKill` + `LaunchApp` (`:153-154`) | **yes** | its backoff |

The second is the hung-shutdown recovery: the app announced a restart, failed to exit within the 5s deadline, and got force-killed. That is a genuine crash recovery, it books a strike, and today it correctly inherits its backoff from `Program.cs:377`. Keying the policy on `prevState` alone would silently strip it — producing exactly the tight relaunch loop the policy's own rationale says backoff exists to prevent, bounded only by the three-strike escalation to `FallenBack`. So the policy takes both states, and zero applies only to `CleanExitPending → Launching`. `DrainCommands` runs *after* `_sm.Handle` (`Program.cs:201-202`), so `_sm.State` is already the post-transition state and no extra plumbing is needed.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Supervisor.Tests/Supervision/RelaunchDelayPolicyTests.cs`:

```csharp
namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class RelaunchDelayPolicyTests
{
    [Fact]
    public void RestartOriginatedLaunch_IsImmediate()
    {
        // CleanExitPending -> Launching is Task 2's restart branch: the ONLY path that
        // reaches LaunchApp without booking a strike. The user is waiting for their only
        // UI to come back; a crash-loop brake is the wrong instrument.
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.CleanExitPending, SupervisorState.Launching, TimeSpan.FromSeconds(60)))
             .IsEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void HungRestartForceKill_KeepsItsBackoff()
    {
        // THE case that makes the state PAIR necessary. The app announced a restart and
        // then failed to exit inside the 5s deadline, so DeadlineElapsed force-killed it
        // (SupervisionStateMachine.cs:135-137) and GoRecovering emitted ForceKill +
        // LaunchApp (:153-154) — with prevState still CleanExitPending. That is a real
        // crash recovery: it books a strike, and stripping its backoff would produce the
        // tight relaunch loop backoff exists to prevent.
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.CleanExitPending, SupervisorState.Recovering, TimeSpan.FromSeconds(15)))
             .IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void HungRestartThatEscalates_KeepsItsBackoff()
    {
        // Same transition, but the strike tripped the crash window so GoRecovering went
        // to FallenBack instead of Recovering. Still not a restart.
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.CleanExitPending, SupervisorState.FallenBack, TimeSpan.FromSeconds(60)))
             .IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void RecoveryLaunch_KeepsItsBackoff()
    {
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.Supervising, SupervisorState.Recovering, TimeSpan.FromSeconds(15)))
             .IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void InitialLogonLaunch_KeepsItsZeroDelay()
    {
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.IdleWatch, SupervisorState.Launching, TimeSpan.Zero))
             .IsEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void FallenBackRelaunch_KeepsItsBackoff()
    {
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.FallenBack, SupervisorState.FallenBack, TimeSpan.FromSeconds(60)))
             .IsEqualTo(TimeSpan.FromSeconds(60));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~RelaunchDelayPolicyTests" --nologo
```

Expected: **build failure**, `CS0103: The name 'RelaunchDelayPolicy' does not exist`.

- [ ] **Step 3: Create the policy**

Create `src/EyeCtrl.Supervisor/Supervision/RelaunchDelayPolicy.cs`:

```csharp
namespace EyeCtrl.Supervisor.Supervision;

using System;

/// <summary>
/// How long to wait before acting on a <see cref="SupervisorCommand.LaunchApp"/>.
///
/// Backoff exists to stop a crash loop hammering the process table. A user-requested
/// restart is not a crash loop: it books no strike, and the user is waiting for the
/// only interface they can operate to come back. Making them wait up to 60s because
/// the app happened to crash earlier in the window reads as a hang.
///
/// The restart is identified by the state PAIR, not by the previous state alone. Two
/// transitions leave <see cref="SupervisorState.CleanExitPending"/> emitting LaunchApp:
/// Task 2's restart branch (to <see cref="SupervisorState.Launching"/>, no strike) and
/// the hung-shutdown force-kill (DeadlineElapsed → GoRecovering → Recovering/FallenBack,
/// which DOES book a strike). Only the first may skip backoff.
/// </summary>
public static class RelaunchDelayPolicy
{
    /// <summary>
    /// Chooses the delay for a launch command.
    /// </summary>
    /// <param name="stateWhenCommanded">The state the machine was in before this input.</param>
    /// <param name="stateAfterInput">
    /// The state the machine reached. Together with <paramref name="stateWhenCommanded"/>
    /// this identifies the emitting transition: <c>CleanExitPending → Launching</c> is the
    /// user-requested restart, and the only path that reaches LaunchApp without a strike.
    /// <c>CleanExitPending → Recovering/FallenBack</c> is the hung-shutdown force-kill,
    /// which is a real crash recovery and must keep its brake.
    /// </param>
    /// <param name="backoffDelay">The crash-backoff delay that would otherwise apply.</param>
    /// <returns>Zero for a user-requested restart; <paramref name="backoffDelay"/> otherwise.</returns>
    public static TimeSpan For(
        SupervisorState stateWhenCommanded,
        SupervisorState stateAfterInput,
        TimeSpan backoffDelay) =>
        stateWhenCommanded == SupervisorState.CleanExitPending
            && stateAfterInput == SupervisorState.Launching
                ? TimeSpan.Zero
                : backoffDelay;
}
```

- [ ] **Step 4: Wire it into `DrainCommands`**

`DrainCommands` needs the pre-transition state, which `HandleInput` already captures as `prevState` (used at `Program.cs:205`). Change the call site at `Program.cs:202`:

```csharp
            DrainCommands();
```

to:

```csharp
            DrainCommands(prevState);
```

and the declaration at `Program.cs:366`:

```csharp
    private void DrainCommands()
```

to:

```csharp
    /// <param name="prevState">
    /// The state the machine was in before this input. Paired with the machine's current
    /// state it identifies the emitting transition: only CleanExitPending → Launching is a
    /// user-requested restart and skips backoff — see <see cref="RelaunchDelayPolicy"/>.
    /// </param>
    private void DrainCommands(SupervisorState prevState)
```

Then replace the delay computation at `Program.cs:377-378`:

```csharp
                    var delay = _sm.NextBackoffDelay;
                    _pendingLaunchAt = DateTime.UtcNow + delay;
```

with:

```csharp
                    // _sm.State is already the POST-transition state here: HandleInput calls
                    // DrainCommands after _sm.Handle(input). The pair is what distinguishes a
                    // restart (CleanExitPending -> Launching, no strike) from the hung-shutdown
                    // force-kill (CleanExitPending -> Recovering/FallenBack, which books one and
                    // must keep its brake).
                    var delay = RelaunchDelayPolicy.For(prevState, _sm.State, _sm.NextBackoffDelay);
                    _pendingLaunchAt = DateTime.UtcNow + delay;
```

`prevState` is the local `HandleInput` captures at `Program.cs:200` (`var prevState = _sm.State;`), already in scope at the `DrainCommands()` call on line 202 — pass it straight through. (Verified against `4451cdb`; if you are implementing on a later `main`, the `grep -n "prevState" src/EyeCtrl.Supervisor/Program.cs` check at the top of `HandleInput` confirms the name and scope in one line.)

`_sm.State` needs no plumbing: `DrainCommands` is an instance method and `_sm` is a field, so it reads the post-transition state directly. Do **not** capture the state into a local before `_sm.Handle` and pass that as the second argument — that would make both arguments the *previous* state and the restart branch would never match.

- [ ] **Step 5: Run the full suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Supervisor/Supervision/RelaunchDelayPolicy.cs src/EyeCtrl.Supervisor/Program.cs tests/EyeCtrl.Supervisor.Tests/Supervision/RelaunchDelayPolicyTests.cs
git commit -m "feat(restart): exempt a user-requested restart from crash backoff"
```

---

## Task 4: Thread the flag through the supervisor's pipe handler

**Files:**
- Modify: `src/EyeCtrl.Supervisor/Program.cs:280-286`

One line, but it is the join between Task 1's wire format and Task 2's state machine, and without it both are inert.

- [ ] **Step 1: Pass the payload's flag into the input**

Replace `src/EyeCtrl.Supervisor/Program.cs:280-286`:

```csharp
            // 3. Clean exit: the app's final ShuttingDown payload. ClientVerified == (id != null),
            //    which is true here (we dropped unbound above); the flag is threaded through for
            //    the state machine's authenticated-shutdown check.
            if (hb.ShuttingDown)
            {
                HandleInput(new CleanExitRequested(now, ClientVerified: id != null));
            }
```

with:

```csharp
            // 3. Clean exit: the app's final ShuttingDown payload. ClientVerified == (id != null),
            //    which is true here (we dropped unbound above); the flag is threaded through for
            //    the state machine's authenticated-shutdown check.
            //    RestartRequested rides along: a user-requested restart is a clean exit that is
            //    followed by a launch. It is only honoured for a verified client, by the same
            //    rule and in the same place as the shutdown signal itself.
            if (hb.ShuttingDown)
            {
                HandleInput(new CleanExitRequested(
                    now,
                    ClientVerified: id != null,
                    RestartRequested: hb.RestartRequested));
            }
```

- [ ] **Step 2: Build and run the suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures. There is no new unit test here — `SupervisorApp` is a WPF `Application` and not headless-testable; the behaviour either side of this line is covered by Tasks 1 and 2, and the join is exercised end to end in Task 12 (scenario B).

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Supervisor/Program.cs
git commit -m "feat(restart): honour the payload's restart flag for verified clients"
```

---

## Task 5: `SingleInstanceGuard` can wait for the mutex

**Files:**
- Modify: `src/EyeCtrl.Core/Services/SingleInstanceGuard.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/SingleInstanceHandoffTests.cs` (create)

Today the guard fails fast (`WaitOne(0)`), which is right for a stray double-click and wrong for a restart child, which must queue behind the parent it is replacing. The waiting form is opt-in: the existing parameterless `TryAcquire()` keeps its exact behaviour, so nothing about the double-click guard changes.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Services/SingleInstanceHandoffTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using System.Threading;
using EyeCtrl.Core.Services;

/// <summary>
/// The restart child must queue behind the outgoing instance rather than exiting on
/// contention, so ownership hands over the moment the parent's OnExit releases it.
///
/// A named Mutex is thread-reentrant, so — as in SingleInstanceGuardTests — the
/// contending guard must run on a DEDICATED thread. A pooled thread can be the very
/// one that acquired first, which would re-acquire reentrantly and give a false pass.
/// Each test uses a unique name.
/// </summary>
public class SingleInstanceHandoffTests
{
    private static string UniqueName() => @"Local\EyeCtrl.App.Handoff." + Guid.NewGuid();

    [Fact]
    public void WaitingAcquire_SucceedsOnceTheHolderReleases()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();

        var acquired = false;
        var started = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            started.Set();
            acquired = second.TryAcquire(TimeSpan.FromSeconds(10));
        });
        thread.Start();

        Check.That(started.Wait(TimeSpan.FromSeconds(5))).IsTrue(); // it is queued
        Check.That(acquired).IsFalse();                            // and still waiting

        first.Dispose();                                           // the parent's OnExit

        Check.That(thread.Join(TimeSpan.FromSeconds(10))).IsTrue();
        Check.That(acquired).IsTrue();
    }

    [Fact]
    public void WaitingAcquire_GivesUp_WhenTheHolderNeverReleases()
    {
        // The parent hung during shutdown. Better one instance than two fighting over
        // the gaze device, the settings file and the dwell pipe.
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();

        var acquired = true;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            acquired = second.TryAcquire(TimeSpan.FromMilliseconds(300));
        });
        thread.Start();

        Check.That(thread.Join(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(acquired).IsFalse();
    }

    [Fact]
    public void ZeroTimeout_BehavesLikeTheParameterlessForm()
    {
        // The double-click guard must keep failing fast — this is the regression that
        // would let two instances start from a stray launch.
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();

        var acquired = true;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            acquired = second.TryAcquire(TimeSpan.Zero);
        });
        thread.Start();

        Check.That(thread.Join(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(acquired).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SingleInstanceHandoffTests" --nologo
```

Expected: **build failure**, `CS1501: No overload for method 'TryAcquire' takes 1 arguments`.

- [ ] **Step 3: Add the waiting overload**

In `src/EyeCtrl.Core/Services/SingleInstanceGuard.cs`, replace the `TryAcquire` method with a pair. The parameterless form keeps its exact semantics by delegating with a zero timeout:

```csharp
    /// <summary>True if this instance acquired the mutex (legitimate to run).</summary>
    public bool TryAcquire() => TryAcquire(TimeSpan.Zero);

    /// <summary>
    /// Acquires the mutex, waiting up to <paramref name="timeout"/> for a current holder
    /// to release it.
    ///
    /// <see cref="TimeSpan.Zero"/> — the default and the behaviour every existing caller
    /// gets — fails fast, which is what the double-click guard needs. A restart child
    /// passes a real timeout instead: it must QUEUE behind the instance it is replacing,
    /// because that instance does not release until the end of its own OnExit — after it
    /// has written settings and torn down the gaze device.
    /// </summary>
    /// <param name="timeout">How long to wait for the current holder to release.</param>
    /// <returns><see langword="true"/> if the mutex is now held by this instance.</returns>
    public bool TryAcquire(TimeSpan timeout)
    {
        // createdNew==true means no prior owner existed anywhere — we got it cleanly.
        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out _owned);
        if (_owned) return true;

        // Already owned by some process. WaitOne returns true only if we can take it
        // within the timeout. AbandonedMutex means a prior holder was force-killed —
        // treat as acquired (spec, in-app #2). That case also covers a parent that died
        // mid-exit during a restart handoff: ownership still passes to the child.
        try
        {
            _owned = _mutex.WaitOne(timeout); // RETURN the result — do not discard it.
            return _owned;
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
            return true;
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SingleInstance" --nologo
```

Expected: 0 errors; the three new tests plus the two existing `SingleInstanceGuardTests` all pass. The existing pair passing is the point — the fast-fail path must be untouched.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Services/SingleInstanceGuard.cs tests/EyeCtrl.App.Tests/Services/SingleInstanceHandoffTests.cs
git commit -m "feat(restart): let a restart child queue for the single-instance mutex"
```

---

## Task 6: The handoff contract and the child's side of it

**Files:**
- Create: `src/EyeCtrl.App/Services/RestartHandoff.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs:74-87` (handoff arg) and `:300-330` (`OnExit` — release the mutex last)

Two processes have to agree on two names and one ordering rule. Putting them in one small type means the parent and child cannot drift, and gives the ordering rule somewhere to be written down.

- [ ] **Step 1: Create the contract**

Create `src/EyeCtrl.App/Services/RestartHandoff.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.Threading;

/// <summary>
/// The agreement between an outgoing EyeCtrl instance and the replacement it launches.
///
/// The child signals <see cref="ReadyEventName"/> as soon as it is running and BEFORE
/// it waits for the single-instance mutex. That ordering is load-bearing in both
/// directions:
///
/// <list type="bullet">
/// <item>Signal-then-wait lets the parent learn "my replacement started" while the
/// parent still holds the mutex — which is the only moment at which the parent can
/// still change its mind and stay running.</item>
/// <item>Wait-then-signal would deadlock: the child cannot take the mutex until the
/// parent exits, and the parent will not exit until the child signals.</item>
/// </list>
///
/// What the signal proves is deliberately modest: the exe path resolved, the runtime
/// loaded, the single-file bundle extracted, and nothing blocked the launch. It does
/// NOT prove the child will finish initialising — see the startup-failure fallback.
/// </summary>
public static class RestartHandoff
{
    /// <summary>Command-line argument marking a process as a restart replacement.</summary>
    public const string Argument = "--restart-handoff";

    /// <summary>
    /// Name of the event a restart child sets to announce it is alive and queued.
    /// <c>Local\</c> (not <c>Global\</c>) to match the single-instance mutex: multi-user
    /// and RDP are non-goals, and a session-local name avoids cross-session collisions.
    ///
    /// Owned by the PARENT for the duration of the handoff: the parent creates it BEFORE
    /// launching the child and holds it open across the wait (see
    /// <see cref="SelfRestartStrategy.CreateDefault"/>). That is what makes the child's
    /// signal reliable in both directions — see <see cref="SignalReady"/>.
    /// </summary>
    public const string ReadyEventName = @"Local\EyeCtrl.App.RestartReady";

    /// <summary>
    /// How long a restart child waits for the outgoing instance to release the mutex
    /// before giving up and exiting quietly. Generous: the parent's OnExit saves
    /// settings and the user dictionary and tears down the gaze engine.
    /// </summary>
    public static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a parent waits for its child to signal readiness before treating the
    /// restart as failed. Covers a cold start of a self-contained single-file build.
    /// </summary>
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sets the ready event. Safe to call when no parent is listening — see the note below.
    /// </summary>
    /// <remarks>
    /// The child OPENS the event the parent already owns rather than creating-or-opening it.
    /// <see cref="EventWaitHandle.OpenExisting"/> is used deliberately:
    /// <list type="bullet">
    /// <item><b>A missing event means no parent is waiting</b> (the parent always creates it
    /// before launching). A standalone launch must not fabricate an orphaned Set event into
    /// the void, so a missing event is a clean no-op, not a signal. <c>CreateOrOpen</c> would
    /// leave a stray Set event from a launch with no listener, which the NEXT restart's parent
    /// could observe as a false ready.</item>
    /// <item>The child never RESETs the event. The parent created it unset and holds it; the
    /// child only ever Sets. Reversed roles (child creating, parent observing) would need the
    /// creator to set <c>initialState</c>, which only applies on first creation — a leftover
    /// event would be opened <i>still Set</i> from a prior run and produce a false positive.</item>
    /// </list>
    /// </remarks>
    public static void SignalReady()
    {
        try
        {
            using var ready = EventWaitHandle.OpenExisting(ReadyEventName);
            ready.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No parent created this event, so there is no handoff to acknowledge. The child
            // proceeds with normal startup — it was not launched as a restart replacement, or
            // the parent died before we got here. Do NOT fabricate an event: a stray Set event
            // with no owner would be observed as a false ready by the next restart's parent.
        }
        catch
        {
            // Best-effort. A child that cannot signal for any other reason will be killed by
            // the parent's timeout, and the parent stays running — the safe outcome either way.
        }
    }
}
```

- [ ] **Step 2: Teach startup to honour the handoff**

In `src/EyeCtrl.App/App.xaml.cs`, replace the single-instance block (lines 74-87):

```csharp
        IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
        IsPausedLaunch = e.Args.Contains("--paused", StringComparer.OrdinalIgnoreCase);

        // Single-instance guard: a second instance exits fast. The supervisor's respawn
        // is legitimate, but a stray double-click/race must not launch two instances
        // fighting over the same gaze device, settings file, and dwell pipe.
        // Local\ (not Global\): multi-user/RDP is a non-goal; avoids cross-session collisions.
        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.TryAcquire())
        {
            // Another instance is already running (likely the supervisor's legitimate one).
            Shutdown();
            return;
        }
```

with:

```csharp
        IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
        IsPausedLaunch = e.Args.Contains("--paused", StringComparer.OrdinalIgnoreCase);
        var isRestartHandoff = e.Args.Contains(RestartHandoff.Argument, StringComparer.OrdinalIgnoreCase);

        // A restart replacement announces itself BEFORE queueing for the mutex: the
        // outgoing instance is waiting for exactly this before it exits, and it still
        // holds the mutex while it waits. Reversing the order deadlocks.
        if (isRestartHandoff)
        {
            RestartHandoff.SignalReady();
        }

        // Single-instance guard: a second instance exits fast. The supervisor's respawn
        // is legitimate, but a stray double-click/race must not launch two instances
        // fighting over the same gaze device, settings file, and dwell pipe.
        // Local\ (not Global\): multi-user/RDP is a non-goal; avoids cross-session collisions.
        //
        // A restart replacement is the one case that must WAIT rather than fail fast: the
        // instance it replaces holds the mutex until the very end of its own OnExit.
        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.TryAcquire(isRestartHandoff ? RestartHandoff.MutexWait : TimeSpan.Zero))
        {
            // Another instance is already running (likely the supervisor's legitimate one),
            // or — for a handoff — the outgoing instance never let go. Either way, exit:
            // one working instance beats two fighting over the gaze device.
            //
            // Deliberately unlogged: InitializeSettingsAndLogging() has not run yet, so
            // Serilog's static Log is still a SilentLogger and a Log.Information here would
            // compile and then discard the line. See the note below for what that costs.
            Shutdown();
            return;
        }
```

**No logging here, and that has a consequence worth knowing about.** `Log` resolves (`using Serilog;` is at the top of the file) but `InitializeSettingsAndLogging()` is not called until `App.xaml.cs:99` — after this block — and Serilog's static `Log.Logger` is a `SilentLogger` until configured. Any `Log.Information` at this point compiles and silently discards. Reordering startup to log two lines is not worth it.

The cost is that **a handoff child which times out on the mutex exits leaving no trace at all.** That is the "Parent hangs during `OnExit`" row in the failure table, and its diagnosis has to come from the parent's side rather than the child's: the parent stays running and shows a refusal modal, and `Log.Warning("Restart refused: {Reason}", …)` from Task 9 is the log line to look for. If you ever find yourself needing the child's own account of a failed handoff, the fix is a Serilog bootstrap logger configured before the guard — not a `Log` call here that looks like it works.

- [ ] **Step 3: Release the mutex LAST, not partway through `OnExit`**

`OnExit` currently disposes the guard at `App.xaml.cs:311`, but the resources the guard exists to protect are released **after** that:

| `App.xaml.cs` | Statement |
|---|---|
| 311 | `_singleInstance?.Dispose()` ← mutex released here |
| 320 | `settingsService.Save(settings)` |
| 324-325 | `adapter.Dispose()` / `gazeEngine.Dispose()` (Tobii device torn down) |
| 326 | `ExternalDwellService.Dispose()` |

The guard's own comment (`App.xaml.cs:78-79`) says it exists so two instances never fight "over the same gaze device, settings file, and dwell pipe". Today that gap is harmless because a second instance fails `TryAcquire(0)` and exits instantly — nothing ever proceeds into the window. Step 2 changes that: a handoff child now *waits*, so it acquires the mutex the moment line 311 runs and begins initialising while the outgoing instance is still holding all three.

Two of the three are provably benign, and are recorded here so nobody has to re-derive them:

- **settings.json — safe.** `SettingsService.Save` writes `*.tmp` then `File.Move(overwrite: true)` under the cross-process mutex `Local\EyeCtrl.Settings.Writer`, so the child always reads a complete file. Worst case is a stale-but-valid read, and the settings window's `SaveDebounced` (500ms, `SettingsWindow.xaml.cs:295`) has long since fired by the time a restart commits.
- **`eyectrl_dwell` — safe.** `ExternalDwellService` binds with `NamedPipeServerStream.MaxAllowedServerInstances` (`ExternalDwellService.cs:201-202`), so both servers coexist; a cooperating app may briefly reach either instance, which resolves when the parent disposes.
- **The Tobii device — not verified, and not verifiable headlessly.** Both processes would hold `tobii_device_create` handles across the window.

Rather than ship three separate "verified safe" arguments that a later edit could quietly invalidate, close the window. Move the disposal to the end of `OnExit` — from line 311:

```csharp
        _trayIconManager?.Dispose();
        _eventCoordinator?.Dispose();
        _singleInstance?.Dispose();
```

to:

```csharp
        _trayIconManager?.Dispose();
        _eventCoordinator?.Dispose();
```

and immediately before `Log.CloseAndFlush()` at the end of the method:

```csharp
        _heartbeatPublisher?.Dispose();

        // Release the single-instance mutex LAST. A restart child is queued on it
        // (Task 6's waiting acquire) and starts initialising the instant it is released, so
        // everything this instance still owns — the settings file, the Tobii device, the
        // external-dwell pipe — must already be released. Releasing it at the TOP of OnExit
        // was harmless only while a second instance exited immediately instead of waiting.
        _singleInstance?.Dispose();

        Log.CloseAndFlush();
        base.OnExit(e);
```

This costs nothing: the child's `RestartHandoff.MutexWait` is 30s and the remaining teardown is well under a second. It also strictly improves the ordinary double-click case, which simply waits marginally longer to be told "no".

- [ ] **Step 4: Build and run the suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures. Nothing exercises the handoff yet; this step only proves the argument plumbing compiles and the ordinary launch path is unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/RestartHandoff.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(restart): add the handoff contract and honour it during startup"
```

---

## Task 7: `SelfRestartStrategy`

**Files:**
- Create: `src/EyeCtrl.App/Services/SelfRestartStrategy.cs`
- Create: `tests/EyeCtrl.App.Tests/SelfRestartStrategyTests.cs`

The parent's half. Every external dependency — resolving the exe, starting the process, waiting on the event — is behind a seam, so all five outcomes are testable without launching anything.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/SelfRestartStrategyTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using EyeCtrl.App.Services;
using NFluent;
using Xunit;

/// <summary>
/// The parent's half of the handoff. Its job is to be certain a replacement started
/// before the app that the user depends on goes away — and to leave that app running
/// whenever it cannot be certain.
/// </summary>
public class SelfRestartStrategyTests
{
    private sealed class FakeChild : IRestartChild
    {
        public bool HasExited { get; set; }
        public int Kills { get; private set; }
        public void Kill() => Kills++;
    }

    private static SelfRestartStrategy Build(
        string? exePath = @"C:\EyeCtrl\EyeCtrl.exe",
        FakeChild? child = null,
        bool launchThrows = false,
        bool readySignalled = true,
        List<string>? launchLog = null) =>
        new(
            resolveExePath: () => exePath,
            launch: (path, args) =>
            {
                launchLog?.Add($"{path} {args}");
                if (launchThrows) throw new InvalidOperationException("boom");
                return child ?? new FakeChild();
            },
            // The seam receives the parent-owned handle; tests ignore it and return the
            // canned result. The signature matches the production wait, which waits on the
            // real named event — exercising it headlessly would mean standing up a second
            // process, so the outcome is made injectable instead.
            waitForReady: (_, _) => readySignalled,
            readyWait: TimeSpan.FromSeconds(1));

    [Fact]
    public void Succeeds_WhenTheChildSignalsReady()
    {
        Check.That(Build().Begin()).IsEqualTo(RestartOutcome.Started);
    }

    [Fact]
    public void PassesTheHandoffArgument()
    {
        var log = new List<string>();
        Build(launchLog: log).Begin();

        Check.That(log).HasSize(1);
        Check.That(log[0]).Contains(RestartHandoff.Argument);
        Check.That(log[0]).StartsWith(@"C:\EyeCtrl\EyeCtrl.exe");
    }

    [Fact]
    public void Aborts_WhenTheExePathCannotBeResolved()
    {
        // Environment.ProcessPath is null in some hosting scenarios. Exiting on a guess
        // would be exiting into nothing.
        Check.That(Build(exePath: null).Begin()).IsEqualTo(RestartOutcome.NoExePath);
    }

    [Fact]
    public void Aborts_WhenTheLaunchThrows()
    {
        Check.That(Build(launchThrows: true).Begin()).IsEqualTo(RestartOutcome.LaunchFailed);
    }

    [Fact]
    public void Aborts_WhenTheChildExitsBeforeSignalling()
    {
        var child = new FakeChild { HasExited = true };
        Check.That(Build(child: child, readySignalled: false).Begin())
             .IsEqualTo(RestartOutcome.ChildExitedEarly);
    }

    [Fact]
    public void Aborts_AndKillsTheChild_WhenReadinessNeverArrives()
    {
        // The child is alive but stuck. Leaving it queued on the mutex would mean a
        // zombie that springs to life the next time this instance exits.
        var child = new FakeChild { HasExited = false };
        var outcome = Build(child: child, readySignalled: false).Begin();

        Check.That(outcome).IsEqualTo(RestartOutcome.ChildNotReady);
        Check.That(child.Kills).IsEqualTo(1);
    }

    [Fact]
    public void DoesNotKillAHealthyChild()
    {
        var child = new FakeChild();
        Build(child: child).Begin();
        Check.That(child.Kills).IsEqualTo(0);
    }

    [Theory]
    [InlineData(RestartOutcome.NoExePath)]
    [InlineData(RestartOutcome.LaunchFailed)]
    [InlineData(RestartOutcome.ChildExitedEarly)]
    [InlineData(RestartOutcome.ChildNotReady)]
    public void EveryFailureOutcome_HasAUserFacingExplanation(RestartOutcome outcome)
    {
        // The user completed a dwell and the app is still here; "why" must be sayable.
        var reason = SelfRestartStrategy.Describe(outcome);
        Check.That(reason).IsNotEmpty();
        Check.That(reason).Contains("running");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SelfRestartStrategyTests" --nologo
```

Expected: **build failure** — `SelfRestartStrategy`, `IRestartChild` and `RestartOutcome` do not exist.

- [ ] **Step 3: Create the strategy**

Create `src/EyeCtrl.App/Services/SelfRestartStrategy.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.Diagnostics;

/// <summary>The child process a restart launched, narrowed to what the parent needs.</summary>
public interface IRestartChild
{
    /// <summary>True if the process has already exited.</summary>
    bool HasExited { get; }

    /// <summary>Terminates the process. Used only to clean up a child that never became ready.</summary>
    void Kill();
}

/// <summary>Why a self-restart did or did not get far enough to exit on.</summary>
public enum RestartOutcome
{
    /// <summary>A replacement is running and queued for the mutex. Safe to exit.</summary>
    Started,

    /// <summary>The running executable's path could not be determined.</summary>
    NoExePath,

    /// <summary>Starting the replacement threw.</summary>
    LaunchFailed,

    /// <summary>The replacement exited before announcing itself.</summary>
    ChildExitedEarly,

    /// <summary>The replacement is alive but never announced itself in time.</summary>
    ChildNotReady,
}

/// <summary>
/// Restarts EyeCtrl without a supervisor, by launching a replacement and waiting for it
/// to prove it started.
///
/// <see cref="Begin"/> BLOCKS for up to the readiness timeout and must therefore never
/// run on the dispatcher: a frozen UI thread means no gaze processing, which would take
/// the user's control of the machine away for the duration — the exact outcome this
/// feature exists to prevent. <see cref="AppRestartCoordinator"/> runs it on a background
/// thread and marshals the result back.
///
/// Every dependency is injected so all five outcomes are exercised headlessly; the
/// production wiring is in <see cref="CreateDefault"/>.
/// </summary>
public sealed class SelfRestartStrategy
{
    private readonly Func<string?> _resolveExePath;
    private readonly Func<string, string, IRestartChild> _launch;
    private readonly Func<EventWaitHandle, TimeSpan, bool> _waitForReady;
    private readonly TimeSpan _readyWait;

    /// <summary>Creates a strategy with explicit seams (tests supply fakes).</summary>
    /// <param name="resolveExePath">Returns the running executable's path, or null.</param>
    /// <param name="launch">Starts a process: (exePath, arguments) => child.</param>
    /// <param name="waitForReady">
    /// Waits on the PARENT-OWNED ready event (created by <see cref="Begin"/> before the
    /// launch) for up to the given timeout; returns true if the child signalled. Production
    /// waits on the real named event; tests ignore the handle and return a canned result.
    /// </param>
    /// <param name="readyWait">How long to wait for that signal.</param>
    public SelfRestartStrategy(
        Func<string?> resolveExePath,
        Func<string, string, IRestartChild> launch,
        Func<EventWaitHandle, TimeSpan, bool> waitForReady,
        TimeSpan readyWait)
    {
        _resolveExePath = resolveExePath;
        _launch = launch;
        _waitForReady = waitForReady;
        _readyWait = readyWait;
    }

    /// <summary>The production wiring: the real apphost path, Process.Start, and the named event.</summary>
    public static SelfRestartStrategy CreateDefault() =>
        new(
            // The stable single-file bundle apphost path — Assembly.Location is empty for
            // a single-file publish, which is how this app ships.
            resolveExePath: () => Environment.ProcessPath,
            launch: (exePath, args) =>
            {
                var process = Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = false })
                    ?? throw new InvalidOperationException("Process.Start returned no process.");
                return new ProcessRestartChild(process);
            },
            // The parent OWNS the ready event for the whole handoff: it creates the event
            // (unset) BEFORE launch, passes that same handle into waitForReady, and disposes
            // it after the wait returns. That ownership is what makes the signal reliable:
            //
            //  - No stale-Set false positive: the event is a fresh, unset object the parent
            //    controls the lifetime of, so a leftover event from a prior run can never be
            //    observed. (new EventWaitHandle(false, ManualReset, name) only applies
            //    initialState on CREATION — opening an existing Set event keeps it Set.)
            //  - No signal-before-open false negative: the event exists from before the child
            //    is launched, so however fast the child opens+sets, there is something to open.
            //
            // The child's side (RestartHandoff.SignalReady) OPENs this event rather than
            // creating it — see that method for why a missing event is a clean no-op.
            waitForReady: (readyEvent, timeout) =>
            {
                try { return readyEvent.WaitOne(timeout); }
                catch { return false; }
            },
            readyWait: RestartHandoff.ReadyWait);

    /// <summary>
    /// Launches a replacement and waits for it to announce itself. Blocks — see the
    /// class remarks. The caller may only shut down on <see cref="RestartOutcome.Started"/>.
    /// </summary>
    /// <returns>What happened, for the caller to act on and to tell the user.</returns>
    public RestartOutcome Begin()
    {
        var exePath = _resolveExePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return RestartOutcome.NoExePath;
        }

        // Create the ready event BEFORE launching, so the child always has something to
        // open+set, and a leftover event from a prior run can never be observed. Created
        // unset; the child is the only thing that ever Sets it. Disposed after the wait
        // regardless of outcome — the event's job is done once the parent has decided.
        using var readyEvent = new EventWaitHandle(
            initialState: false, EventResetMode.ManualReset, RestartHandoff.ReadyEventName);

        IRestartChild child;
        try
        {
            child = _launch(exePath, RestartHandoff.Argument);
        }
        catch
        {
            return RestartOutcome.LaunchFailed;
        }

        if (_waitForReady(readyEvent, _readyWait))
        {
            return RestartOutcome.Started;
        }

        // No signal. Either it died on the way up, or it is wedged. A wedged child is
        // still queued for the mutex, so leaving it alive would mean a zombie that
        // springs to life the next time this instance exits — kill it.
        if (child.HasExited)
        {
            return RestartOutcome.ChildExitedEarly;
        }

        try { child.Kill(); } catch { /* already gone; nothing to clean up */ }
        return RestartOutcome.ChildNotReady;
    }

    /// <summary>A user-facing explanation for a failed restart. Always states the app is still running.</summary>
    /// <param name="outcome">The outcome to describe.</param>
    /// <returns>Text suitable for a modal.</returns>
    public static string Describe(RestartOutcome outcome) => outcome switch
    {
        RestartOutcome.Started => "Restarting EyeCtrl…",
        RestartOutcome.NoExePath =>
            "Restart failed: EyeCtrl could not determine its own program path. "
            + "EyeCtrl is still running.",
        RestartOutcome.LaunchFailed =>
            "Restart failed: the new EyeCtrl could not be started. "
            + "EyeCtrl is still running.",
        RestartOutcome.ChildExitedEarly =>
            "Restart failed: the new EyeCtrl closed immediately. "
            + "EyeCtrl is still running.",
        RestartOutcome.ChildNotReady =>
            "Restart failed: the new EyeCtrl did not start in time. "
            + "EyeCtrl is still running.",
        _ => "Restart failed. EyeCtrl is still running.",
    };

    private sealed class ProcessRestartChild(Process process) : IRestartChild
    {
        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch { return true; }   // handle gone: treat as exited
            }
        }

        public void Kill() => process.Kill(entireProcessTree: true);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SelfRestartStrategyTests" --nologo
```

Expected: 0 errors; 11 passing (7 facts + 4 theory cases).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/SelfRestartStrategy.cs tests/EyeCtrl.App.Tests/SelfRestartStrategyTests.cs
git commit -m "feat(restart): add the self-restart strategy with a readiness handshake"
```

---

## Task 8: `AppRestartCoordinator` — strategy selection and the refusal

**Files:**
- Create: `src/EyeCtrl.ViewModels/Messages/AppLifecycleMessages.cs`
- Create: `src/EyeCtrl.App/Services/AppRestartCoordinator.cs`
- Create: `tests/EyeCtrl.App.Tests/AppRestartCoordinatorTests.cs`

Where the promise lives: **the app exits only when a successor has been proven.** Supervised or not, the same rule; only the proof differs.

One detail worth naming before writing it: the flag `OnExit` reads is called `SupervisorRelaunchRequested`, not `RestartRequested`. It must be true **only** on the supervisor-mediated path. Setting it on a self-restart would ask a supervisor that *is* alive (the probe can fail transiently) to launch a second instance alongside the child we already started — the loser dies on the mutex, which self-corrects, but it is avoidable by naming the flag after what it actually means.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/AppRestartCoordinatorTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The coordinator decides whether the app is allowed to exit. Getting this wrong
/// strands a user whose only input channel is the app being restarted, so every
/// branch is pinned.
/// </summary>
public class AppRestartCoordinatorTests
{
    private sealed class Harness
    {
        public bool SupervisorReachable { get; set; }
        public int Probes { get; private set; }
        public RestartOutcome SelfOutcome { get; set; } = RestartOutcome.Started;
        public int SelfAttempts { get; private set; }
        public List<string> Log { get; } = new();
        public IMessenger Messenger { get; } = new WeakReferenceMessenger();
        public AppRestartCoordinator Coordinator { get; }

        public Harness(bool supervisionEnabled)
        {
            var settings = new EyeCtrlSettings();
            settings.Supervision.Enabled = supervisionEnabled;

            Coordinator = new AppRestartCoordinator(
                Messenger,
                settings,
                tryReachSupervisor: () => { Probes++; return SupervisorReachable; },
                selfRestart: () => { SelfAttempts++; return SelfOutcome; },
                shutdown: () => Log.Add("shutdown"),
                reportUnavailable: reason => Log.Add("report:" + reason),
                runInBackground: work => work(),   // inline: the tests are the background thread
                postToUi: work => work());
        }

        public void Request() => Messenger.Send(new RestartAppRequestedMessage());
    }

    [Fact]
    public void SupervisorReachable_UsesTheSupervisorAndDoesNotSelfRestart()
    {
        // Preferred path: only the supervisor can recover a relaunch that fails.
        var h = new Harness(supervisionEnabled: true) { SupervisorReachable = true };
        h.Request();

        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsTrue();
        Check.That(h.SelfAttempts).IsEqualTo(0);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void Unsupervised_SelfRestarts_WithoutProbing()
    {
        // The common case: Supervision.Enabled defaults to false.
        var h = new Harness(supervisionEnabled: false);
        h.Request();

        Check.That(h.Probes).IsEqualTo(0);
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void SupervisorConfiguredButUnreachable_FallsBackToSelfRestart()
    {
        // Enabled records that a supervisor was configured, not that one is running.
        var h = new Harness(supervisionEnabled: true) { SupervisorReachable = false };
        h.Request();

        Check.That(h.Probes).IsEqualTo(1);
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void SelfRestartFailure_ReportsAndStaysRunning()
    {
        // THE load-bearing case. Exiting here would leave the user with nothing.
        var h = new Harness(supervisionEnabled: false) { SelfOutcome = RestartOutcome.LaunchFailed };
        h.Request();

        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).StartsWith("report:");
        Check.That(h.Log[0]).Contains("still running");
        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsFalse();
    }

    [Fact]
    public void SelfRestart_NeverSetsTheSupervisorRelaunchFlag()
    {
        // Otherwise a supervisor that is actually alive (transient probe failure) would
        // launch a SECOND instance beside the child we just started.
        var h = new Harness(supervisionEnabled: true) { SupervisorReachable = false };
        h.Request();

        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsFalse();
    }

    [Fact]
    public void SupervisorFlagIsSetBeforeShutdown_SoOnExitCanSeeIt()
    {
        // OnExit reads the flag when it stamps the final payload. Setting it after
        // Shutdown() would make the payload say "plain quit" and the supervisor would
        // go idle instead of relaunching.
        var settings = new EyeCtrlSettings();
        settings.Supervision.Enabled = true;
        var messenger = new WeakReferenceMessenger();
        bool? seenAtShutdown = null;
        AppRestartCoordinator? coordinator = null;
        coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => true,
            selfRestart: () => RestartOutcome.Started,
            shutdown: () => seenAtShutdown = coordinator!.SupervisorRelaunchRequested,
            reportUnavailable: _ => { },
            runInBackground: work => work(),
            postToUi: work => work());

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(seenAtShutdown).IsEqualTo(true);
    }

    [Fact]
    public void SelfRestart_RunsOffTheUiThread_AndShutsDownOnIt()
    {
        // Begin() blocks for up to 30s. On the dispatcher that is 30s of no gaze
        // processing — the user loses control of the machine. And Shutdown() must be
        // called back ON the dispatcher.
        var settings = new EyeCtrlSettings();
        var order = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => { order.Add("self"); return RestartOutcome.Started; },
            shutdown: () => order.Add("shutdown"),
            reportUnavailable: _ => { },
            runInBackground: work => { order.Add("background"); work(); },
            postToUi: work => { order.Add("ui"); work(); });

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(order).ContainsExactly("background", "self", "ui", "shutdown");
    }

    [Fact]
    public void SupervisorProbe_RunsOffTheUiThread_AndShutsDownOnIt()
    {
        // The supervised probe can wait up to ProbeWait (500ms). Same rule as the
        // self-restart: the dispatcher must never block on IPC, or gaze processing
        // freezes while the user is waiting for a restart they asked for.
        var settings = new EyeCtrlSettings();
        settings.Supervision.Enabled = true;
        var order = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => { order.Add("probe"); return true; },
            selfRestart: () => RestartOutcome.Started,
            shutdown: () => order.Add("shutdown"),
            reportUnavailable: _ => { },
            runInBackground: work => { order.Add("background"); work(); },
            postToUi: work => { order.Add("ui"); work(); });

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(order).ContainsExactly("background", "probe", "ui", "shutdown");
    }

    [Fact]
    public void SecondRequest_WhileTheFirstIsInFlight_IsIgnored()
    {
        // Two rapid dwells must not launch two children: the loser would sit on the
        // single-instance mutex for its 30s wait and then exit — one orphaned process.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var requests = new List<Action>();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => RestartOutcome.ChildNotReady,
            shutdown: () => { },
            reportUnavailable: _ => { },
            runInBackground: work => requests.Add(work),   // defer: the request is "in flight"
            postToUi: work => work());

        messenger.Send(new RestartAppRequestedMessage());  // first: in flight
        messenger.Send(new RestartAppRequestedMessage());  // second: dropped

        Check.That(requests).HasSize(1);

        // The refusal path re-arms: after the failed attempt reports, the button works again.
        requests[0]();
        messenger.Send(new RestartAppRequestedMessage());

        Check.That(requests).HasSize(2);
    }

    [Fact]
    public void AThrowingStrategy_ReportsAndReArms_RatherThanDeadeningTheButton()
    {
        // The background body runs on a thread-pool thread, so an escaping exception would
        // be swallowed as an unobserved faulted Task: no report, and _restartInFlight stays
        // latched forever — the user dwells the one control meant to get them unstuck and
        // nothing happens, silently. Task 13's persistSettings is a real throw source
        // (SettingsService.Save throws on invalid settings), so this is not hypothetical.
        var settings = new EyeCtrlSettings();
        var reports = new List<string>();
        var attempts = 0;
        var messenger = new WeakReferenceMessenger();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => { attempts++; throw new InvalidOperationException("boom"); },
            shutdown: () => reports.Add("shutdown"),
            reportUnavailable: reports.Add,
            runInBackground: work => work(),
            postToUi: work => work());

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(reports).HasSize(1);
        Check.That(reports[0]).Contains("still running");
        Check.WithCustomMessage("the app must not exit when the strategy threw")
            .That(reports).Not.Contains("shutdown");

        // Re-armed: a second dwell is honoured rather than silently dropped.
        messenger.Send(new RestartAppRequestedMessage());
        Check.That(attempts).IsEqualTo(2);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AppRestartCoordinatorTests" --nologo
```

Expected: **build failure** — neither `RestartAppRequestedMessage` nor `AppRestartCoordinator` exists.

- [ ] **Step 3: Create the messages**

Create `src/EyeCtrl.ViewModels/Messages/AppLifecycleMessages.cs`:

```csharp
namespace EyeCtrl.ViewModels.Messages;

/// <summary>
/// Sent when the user asks for the app to restart (Settings → General → Restart Now).
///
/// The sender does not decide whether a restart is possible — AppRestartCoordinator
/// does, because refusing is the safety-critical half. A settings surface must never
/// call Shutdown directly.
/// </summary>
public record RestartAppRequestedMessage;

/// <summary>
/// Sent when a requested restart was refused, with the reason to show the user.
///
/// A refusal must be visible. The user has just completed a dwell and is entitled to
/// know the app is deliberately still here; silence would read as the dwell not
/// registering, and they would try again.
/// </summary>
/// <param name="Reason">User-facing explanation.</param>
public record RestartUnavailableMessage(string Reason);
```

Confirm the namespace matches the other files in that folder — check the top of `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` and use the same one.

- [ ] **Step 4: Create the coordinator**

Create `src/EyeCtrl.App/Services/AppRestartCoordinator.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Owns the decision to restart, and — more importantly — the decision to refuse.
///
/// <b>The rule this class exists to enforce: never shut down until a successor has been
/// proven to exist.</b> Two strategies can supply that proof, tried in order of safety:
///
/// <list type="number">
/// <item><b>Supervisor-mediated.</b> Preferred wherever available, because it is the only
/// one that recovers a relaunch which then fails — the supervisor retries; a process that
/// has exited cannot. Requires a live answer on the heartbeat pipe, not merely
/// <c>Supervision.Enabled</c>, which records only that one was configured.</item>
/// <item><b>Self-restart.</b> Launches a replacement and waits for it to announce itself
/// before exiting. Covers the unsupervised case, which is the default.</item>
/// </list>
///
/// Both strategies run OFF the dispatcher: the probe can wait up to 500ms and the
/// self-restart up to 30s, and a frozen dispatcher means no gaze processing — the user
/// loses control of the machine for the duration. <see cref="HandleRestart"/> marshals
/// the outcome back to the dispatcher, which only ever executes the shutdown or the
/// refusal.
///
/// If neither can prove it, the restart is abandoned and the app keeps running — always
/// recoverable, unlike exiting into nothing.
/// </summary>
public sealed class AppRestartCoordinator
{
    private readonly EyeCtrlSettings _settings;
    private readonly Func<bool> _tryReachSupervisor;
    private readonly Func<RestartOutcome> _selfRestart;
    private readonly Action _shutdown;
    private readonly Action<string> _reportUnavailable;
    private readonly Action<Action> _runInBackground;
    private readonly Action<Action> _postToUi;

    /// <summary>
    /// 0 = idle, 1 = a restart attempt is in progress. Guards against a second dwell
    /// launching a second child while the first readiness wait is still running — the
    /// loser would sit on the single-instance mutex for 30s and then exit.
    ///
    /// Latched with <see cref="Interlocked.Exchange(ref int, int)"/> and released with
    /// <see cref="Volatile.Write(ref int, int)"/>. Every exit from the background body
    /// must release it, including the exceptional one: a latch that is never cleared
    /// deadens the button for the remainder of the session, silently.
    /// </summary>
    private int _restartInFlight;

    /// <summary>Creates the coordinator and subscribes it to <see cref="RestartAppRequestedMessage"/>.</summary>
    /// <param name="messenger">Application messenger.</param>
    /// <param name="settings">Loaded settings; <c>Supervision.Enabled</c> selects the first strategy.</param>
    /// <param name="tryReachSupervisor">Probes the heartbeat pipe; true only if a payload was delivered.</param>
    /// <param name="selfRestart">Runs the self-restart handshake. Blocks; never called on the dispatcher.</param>
    /// <param name="shutdown">Begins application shutdown. Normally <c>Application.Shutdown</c>.</param>
    /// <param name="reportUnavailable">Tells the user why the restart did not happen.</param>
    /// <param name="runInBackground">Runs work off the dispatcher.</param>
    /// <param name="postToUi">Marshals work back onto the dispatcher.</param>
    public AppRestartCoordinator(
        IMessenger messenger,
        EyeCtrlSettings settings,
        Func<bool> tryReachSupervisor,
        Func<RestartOutcome> selfRestart,
        Action shutdown,
        Action<string> reportUnavailable,
        Action<Action> runInBackground,
        Action<Action> postToUi)
    {
        _settings = settings;
        _tryReachSupervisor = tryReachSupervisor;
        _selfRestart = selfRestart;
        _shutdown = shutdown;
        _reportUnavailable = reportUnavailable;
        _runInBackground = runInBackground;
        _postToUi = postToUi;

        messenger.Register<RestartAppRequestedMessage>(this, (_, _) => HandleRestart());
    }

    /// <summary>
    /// Set only when the SUPERVISOR will perform the relaunch, and read by
    /// <c>App.OnExit</c> when it stamps the final heartbeat.
    ///
    /// Deliberately not set for a self-restart: a supervisor that is alive despite a
    /// failed probe would then launch a second instance beside the child already
    /// started, and one of them would die on the single-instance mutex.
    /// </summary>
    public bool SupervisorRelaunchRequested { get; private set; }

    /// <summary>
    /// Picks a strategy and runs it. Returns immediately; the work continues in the
    /// background. The dispatcher never blocks: the probe, the bring-up poll and the
    /// self-restart wait all happen off-thread, and the dispatcher only ever executes
    /// the shutdown or the refusal — a frozen dispatcher means no gaze processing, i.e.
    /// the user loses control of the machine for the duration.
    /// </summary>
    public void HandleRestart()
    {
        // One restart attempt at a time. A second dwell while the first readiness wait
        // is still running must not launch a second child (it would queue on the mutex
        // for 30s and then exit). Re-armed on the refusal path, so a failed attempt
        // does not deaden the button.
        if (Interlocked.Exchange(ref _restartInFlight, 1) != 0)
        {
            return;
        }

        _runInBackground(() =>
        {
            // Everything below runs on a thread-pool thread, where an escaping exception
            // becomes an unobserved faulted Task — silent. If that happened, _postToUi would
            // never run, _restartInFlight would stay latched, and the button would be dead
            // for the rest of the session with no modal and no log: the user dwells the one
            // control meant to get them unstuck and nothing happens. So the whole body is
            // guarded, and the guard re-arms.
            try
            {
                // Strategy 1 — supervisor-mediated. Enabled is necessary but not sufficient:
                // the app may have been launched by hand with no supervisor running.
                if (_settings.Supervision.Enabled && _tryReachSupervisor())
                {
                    _postToUi(() =>
                    {
                        SupervisorRelaunchRequested = true;   // before _shutdown: OnExit reads this
                        _shutdown();
                    });
                    return;
                }

                // Strategy 2 — self-restart. Begin() blocks for up to the readiness timeout.
                var outcome = _selfRestart();
                _postToUi(() =>
                {
                    if (outcome == RestartOutcome.Started)
                    {
                        _shutdown();
                    }
                    else
                    {
                        Volatile.Write(ref _restartInFlight, 0);   // re-arm: the user may try again
                        _reportUnavailable(SelfRestartStrategy.Describe(outcome));
                    }
                });
            }
            catch (Exception ex)
            {
                // Never leave the button latched. The app is still running by construction —
                // nothing here calls _shutdown outside the proven-successor branches — so the
                // safe report is the generic one.
                Volatile.Write(ref _restartInFlight, 0);
                _postToUi(() => _reportUnavailable(
                    "Restart failed: " + ex.Message + " EyeCtrl is still running."));
            }
        });
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AppRestartCoordinatorTests" --nologo
```

Expected: 0 errors; 10 passing.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/AppLifecycleMessages.cs src/EyeCtrl.App/Services/AppRestartCoordinator.cs tests/EyeCtrl.App.Tests/AppRestartCoordinatorTests.cs
git commit -m "feat(restart): pick a restart strategy, and refuse when neither can prove a successor"
```

---

## Task 9: Wire the coordinator into the app

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` (heartbeat section ~line 215; `OnExit` lines 300-311)

- [ ] **Step 1: Construct the coordinator after the publisher**

In `src/EyeCtrl.App/App.xaml.cs`, add the field beside the other App fields:

```csharp
    private AppRestartCoordinator? _restartCoordinator;
```

Then, immediately after `_heartbeatPublisher` is assigned (the block beginning `// ── Heartbeat liveness (supervisor watchdog) ──`, ~line 214), add:

```csharp
            // ── Restart coordination ──
            // After the publisher, because the supervised strategy probes the same pipe.
            // The coordinator refuses to shut down unless a successor has been proven, so
            // a failed restart leaves a running app rather than a dark screen.
            _restartCoordinator = new AppRestartCoordinator(
                messenger,
                settings,
                tryReachSupervisor: () => _heartbeatPublisher?.TryReachSupervisor() ?? false,
                selfRestart: SelfRestartStrategy.CreateDefault().Begin,
                shutdown: () => Shutdown(),
                reportUnavailable: reason =>
                {
                    Log.Warning("Restart refused: {Reason}", reason);
                    messenger.Send(new RestartUnavailableMessage(reason));
                },
                runInBackground: work => System.Threading.Tasks.Task.Run(work),
                postToUi: work => Dispatcher.Invoke(work));
```

Add `using EyeCtrl.ViewModels.Messages;` if `App.xaml.cs` does not already have it. The refusal is both logged and surfaced: the log is what you read afterwards, the message is what the user sees at the time.

- [ ] **Step 2: Pass the flag to the final payload**

In `OnExit`, replace the `SignalShuttingDown()` call at line 307 (verified at `4451cdb`; it is the one inside `OnExit`, not the one in `OnSessionEnding` at line 296 — that one stays parameterless, see below):

```csharp
        _heartbeatPublisher?.SignalShuttingDown();
```

with:

```csharp
        // Carries the restart intent to the supervisor. False for every ordinary exit —
        // and also for a self-restart, where a replacement is already coming up and a
        // supervisor-launched second instance would just lose the race for the mutex.
        _heartbeatPublisher?.SignalShuttingDown(
            restartRequested: _restartCoordinator?.SupervisorRelaunchRequested ?? false);
```

Leave `OnSessionEnding` (line 296) calling the parameterless form: a logoff or shutdown is never a restart.

- [ ] **Step 3: Build and run the suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(restart): wire the restart coordinator into app startup and exit"
```

---

## Task 10: The button

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:901,99-106`
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs:171-179`

The button is now **unconditional** — both strategies are available to every configuration, and the coordinator refuses safely when neither can proceed.

**Why this task touches two files, and why they behave differently.** The settings surfaces are split the same way every other tab is: `SettingsViewModel`'s `Tabs` list is **pagination and layout only** (`SettingsViewModel.cs:180-181`: "item count here only drives pagination … the actual interactive controls are built in `SettingsWindow`"), and `SettingsWindow.BuildGeneralPages` builds the real, gaze-driven controls. The Calibration tab already follows this split — `calCalibrate`/`calClear` in the VM mirror `cal_run`/`cal_clear` in the window. So **both additions below are required**, and they are not redundant:
- Step 1's `DwellButton` is the actual interactive control the user dwells on. It alone runs `ShouldOfferSupervisor()` and may show the three-way modal (Task 13).
- Step 3's `ActionButtonSettingItem` keeps the VM's pagination count consistent with the window, so the page layout does not desync from the rendered controls. It sends the plain (no-offer) message because prompting belongs to the one interactive surface, not to a pagination placeholder.

- [ ] **Step 1: Add it to the gaze-driven settings window**

In `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`, `BuildGeneralPages` (line 901) returns a single page. Insert the restart button **above** the reset buttons — deliberately, so a mis-aimed dwell near the destructive controls cannot land on it and vice versa. After the `g_track` toggle and before `g_reset`:

```csharp
                () => Add(new DwellButton(_viewModel, "g_restart", "↻ Restart Now",
                    () => ShowModal(
                        "Restart EyeCtrl now?",
                        () => _messenger.Send(new RestartAppRequestedMessage())),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80,
                    textBrush: Tokens.BrushSurfaceDark)),
```

The window is constructed with an `IMessenger` (`SettingsWindow.xaml.cs:99-106`); if it is not already retained in a field alongside `_viewModel` and `_settings`, add `_messenger = messenger;` in the constructor and a matching field. Add `using EyeCtrl.ViewModels.Messages;`.

- [ ] **Step 2: Show the refusal**

The coordinator sends `RestartUnavailableMessage` when it declines. Nothing displays it yet, which would leave the user's dwell looking like it did not register. `SettingsWindow` already receives an `IMessenger` and owns a modal, and it is where the user is standing when this fires.

In the constructor (after `_messenger = messenger;`), register the handler:

```csharp
        // A refused restart must be visible: the user just completed a dwell, and
        // silence reads as the dwell not registering. onYes is a no-op — ShowModal is
        // the only dialog this window has, and here it is used purely as an OK notice.
        _messenger.Register<RestartUnavailableMessage>(this, (_, m) =>
            Dispatcher.Invoke(() => ShowModal(m.Reason, onYes: () => { })));
```

`ShowModal` delegates to `_viewModel.ShowModal(message, onYes)` (`SettingsWindow.xaml.cs:1333-1337`, `SettingsWindowViewModel.cs:289`). `Dispatcher.Invoke` because the refusal is raised from the coordinator's UI-marshalling callback, and the registration must be safe regardless of which thread reaches it.

- [ ] **Step 3: Add the matching ViewModel item**

The General tab is built from an inline list (`SettingsViewModel.cs:171-179`). Add the action button as its last entry, after the theme items:

```csharp
            new ActionButtonSettingItem("restartApp", "Restart Now",
                () => _messenger.Send(new RestartAppRequestedMessage())),
```

`_messenger` is already a field (`SettingsViewModel.cs:33`), and `ActionButtonSettingItem` is used exactly this way at `SettingsViewModel.cs:188-190`. Add `using EyeCtrl.ViewModels.Messages;` if the message namespace differs from the file's own. As noted at the top of this task, this item intentionally sends the plain message — it is a pagination placeholder, not the interactive surface that prompts.

> **If the ITheme slice landed first**, the three theme lines (`themeMode`, `overlayOpacity`, `themeHighContrast`) will already be a single `themeName` combo. Keep whatever is there and just append this item.

- [ ] **Step 4: Build and run the suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures.

**Do not launch the app to look at the button.** Task 14 covers that, with your go-ahead. Note that Task 13 revises this button's prompt once the three-way offer exists — the plain two-button modal here is deliberate, so this task lands green on its own. The General page now carries **seven** controls — the theme combo (`g_theme`), three toggles, this restart button and the two resets — after the ITheme slice merged. `BuildGeneralPages` returns a single page and does **not** chunk (unlike the Quick Actions pages, which split at `QaActionsPerPage`), so nothing bounds the page height automatically. Whether they still fit without clipping cannot be verified headlessly, so it is checked in Task 14, scenario A — which makes that scenario the only mitigation for a page that has since grown by one control.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs
git commit -m "feat(restart): offer Restart Now in both settings surfaces"
```

---

## Task 11: Find the supervisor, and be able to start publishing to it

**Files:**
- Create: `src/EyeCtrl.App/Services/SupervisorLauncher.cs`
- Create: `tests/EyeCtrl.App.Tests/SupervisorLauncherTests.cs`
- Modify: `src/EyeCtrl.App/Services/HeartbeatPublisher.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs:213-228`

The mechanics for Task 12's offer. Two pieces: locating the supervisor exe, and being able to turn heartbeat publishing on mid-session.

**Why publishing must be switchable, and why it is safety-critical.** The heartbeat timer is created only when `Supervision.Enabled` (`App.xaml.cs:222-228`), so an unsupervised session sends nothing. If a probe were to reach a supervisor from such a session, `OnHeartbeatReceived` would adopt the app into `Supervising` (`Program.cs:270-278`) — and `PeriodicCheckStale` would then force-kill it once `HangThresholdSeconds` (default 20) passed with no follow-up heartbeat. **Adoption without publishing is a delayed kill.** That is the real reason `TryReachSupervisor` returns false when not publishing, and this task makes that reason explicit in the code rather than incidental.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/SupervisorLauncherTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using EyeCtrl.App.Services;
using NFluent;
using Xunit;

/// <summary>
/// Locating the supervisor follows the same shape as the supervisor locating the app: an
/// environment override wins, otherwise it is assumed to sit beside us in the install
/// directory (`Program.cs:597-608`). One install layout then works in both directions.
///
/// The one deliberate difference is what these tests exist to pin: unlike
/// `ResolveAppExePath`, a missing sibling yields null rather than a hopeful path, because
/// `IsInstalled` gates whether Task 13 offers the user a supervisor at all.
/// </summary>
public class SupervisorLauncherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eyectrl-sup").FullName;
    private readonly string? _originalEnv = Environment.GetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", _originalEnv);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not really an exe");
        return path;
    }

    [Fact]
    public void Resolves_TheSiblingExe_WhenPresent()
    {
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", null);
        var expected = MakeFile("EyeCtrl.Supervisor.exe");

        var resolved = SupervisorLauncher.ResolveExePath(baseDirectory: _dir);

        Check.That(resolved).IsEqualTo(Path.GetFullPath(expected));
    }

    [Fact]
    public void ResolvesNull_WhenNoSupervisorIsInstalled()
    {
        // The offer must not be made when there is nothing to launch.
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", null);

        Check.That(SupervisorLauncher.ResolveExePath(baseDirectory: _dir)).IsNull();
    }

    [Fact]
    public void EnvironmentOverride_WinsOverTheSibling()
    {
        var sibling = MakeFile("EyeCtrl.Supervisor.exe");
        var elsewhere = MakeFile("Custom.Supervisor.exe");
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", elsewhere);

        var resolved = SupervisorLauncher.ResolveExePath(baseDirectory: _dir);

        Check.That(resolved).IsEqualTo(Path.GetFullPath(elsewhere));
        Check.That(resolved).IsNotEqualTo(Path.GetFullPath(sibling));
    }

    [Fact]
    public void EnvironmentOverride_PointingAtNothing_FallsBackToTheSibling()
    {
        // A stale env var must not disable a perfectly good installed supervisor.
        var sibling = MakeFile("EyeCtrl.Supervisor.exe");
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", Path.Combine(_dir, "gone.exe"));

        Check.That(SupervisorLauncher.ResolveExePath(baseDirectory: _dir))
             .IsEqualTo(Path.GetFullPath(sibling));
    }
}
```

Add to `tests/EyeCtrl.App.Tests/HeartbeatRestartPayloadTests.cs`:

```csharp
    [Fact]
    public async Task EnablePublishing_TurnsTheProbeOn()
    {
        // A session that started unsupervised can be upgraded when the user chooses to
        // start a supervisor. Until then the probe must stay false — see below.
        using var publisher = new HeartbeatPublisher(supervised: false);
        Check.That(publisher.TryReachSupervisor()).IsFalse();

        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));
        publisher.EnablePublishing();

        Check.That(publisher.TryReachSupervisor()).IsTrue();
        Check.That(await received).IsNotNull();
    }

    [Fact]
    public void TryReachSupervisor_IsFalseWhileNotPublishing_EvenWithAServerListening()
    {
        // Load-bearing, and not an optimisation: a successful probe ADOPTS this app into
        // the supervisor's Supervising state (Program.cs:270-278). With no periodic
        // heartbeats following, PeriodicCheckStale force-kills it after
        // HangThresholdSeconds. Probing without publishing is a delayed kill.
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(2));

        using var publisher = new HeartbeatPublisher(supervised: false);
        Check.That(publisher.TryReachSupervisor()).IsFalse();

        Check.WithCustomMessage("nothing should have been sent")
            .That(received.Wait(TimeSpan.FromSeconds(3)) && received.Result is not null).IsFalse();
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SupervisorLauncherTests|FullyQualifiedName~HeartbeatRestartPayloadTests" --nologo
```

Expected: **build failure** — `SupervisorLauncher` does not exist, `HeartbeatPublisher` has no `EnablePublishing`.

- [ ] **Step 3: Create the launcher**

Create `src/EyeCtrl.App/Services/SupervisorLauncher.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Locates and starts the EyeCtrl supervisor.
///
/// Resolution follows the same shape as the supervisor's `ResolveAppExePath`
/// (`Program.cs:597-608`): an environment override wins, otherwise the exe is assumed to
/// sit beside us in the same install directory. One install layout therefore works in both
/// directions, and one env var pair documents both.
///
/// It is NOT an exact mirror, and the difference is load-bearing: `ResolveAppExePath`
/// returns the sibling path unconditionally — it never checks `File.Exists` on it, because
/// the supervisor has to launch *something* and lets `Process.Start` fail loudly. Here the
/// sibling IS existence-checked and a missing exe yields <see langword="null"/>, because
/// <see cref="IsInstalled"/> is a question Task 13 asks before offering the user a choice.
/// Offering to start a supervisor that is not installed would be worse than not offering.
/// </summary>
public static class SupervisorLauncher
{
    /// <summary>Environment override for the supervisor exe path. Mirrors EYECTRL_APP_EXE.</summary>
    public const string ExeEnvVar = "EYECTRL_SUPERVISOR_EXE";

    /// <summary>Default file name, expected beside the app exe.</summary>
    public const string ExeFileName = "EyeCtrl.Supervisor.exe";

    /// <summary>
    /// Resolves the supervisor exe, or <see langword="null"/> when none is installed.
    /// </summary>
    /// <param name="baseDirectory">
    /// Directory to look in. Defaults to <see cref="AppContext.BaseDirectory"/>; tests pass
    /// a temp directory.
    /// </param>
    /// <returns>Full path to the supervisor exe, or null.</returns>
    public static string? ResolveExePath(string? baseDirectory = null)
    {
        var env = Environment.GetEnvironmentVariable(ExeEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        // A stale override must not disable an installed supervisor, so fall through
        // rather than returning null when the env path does not exist.
        var sibling = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, ExeFileName);
        return File.Exists(sibling) ? Path.GetFullPath(sibling) : null;
    }

    /// <summary>True when a supervisor is installed and could be started.</summary>
    public static bool IsInstalled => ResolveExePath() is not null;

    /// <summary>
    /// Starts the supervisor. Returns false if none is installed or the launch threw —
    /// callers must treat that as "no supervisor" and fall back, never as fatal.
    /// </summary>
    /// <returns><see langword="true"/> if a process was started.</returns>
    public static bool TryLaunch()
    {
        var exePath = ResolveExePath();
        if (exePath is null)
        {
            return false;
        }

        try
        {
            return Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false }) is not null;
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Make publishing switchable**

In `src/EyeCtrl.App/Services/HeartbeatPublisher.cs`, replace the `_supervised` field added in Task 1 with a mutable one, and add the enable method. The field:

```csharp
    private readonly bool _supervised;
```

becomes:

```csharp
    private int _publishing;   // 0 = off, 1 = on — swapped with Interlocked, see EnablePublishing
```

The constructor (Task 1's version) becomes:

```csharp
    public HeartbeatPublisher(bool supervised)
    {
        if (supervised) EnablePublishing();
    }
```

Replace `StartWriter` (line 110) with the idempotent enable:

```csharp
    /// <summary>
    /// Starts publishing heartbeats, if not already. Idempotent.
    ///
    /// Called from the constructor when <c>Supervision.Enabled</c>, and again if the user
    /// starts a supervisor mid-session. Turning this on BEFORE probing is mandatory: a
    /// probe that reaches a supervisor adopts this app into its Supervising state, and an
    /// adopted app that then sends nothing is force-killed once HangThresholdSeconds
    /// elapses.
    /// </summary>
    public void EnablePublishing()
    {
        // CompareExchange, not a check-then-set: two callers (the constructor and Task 13's
        // mid-session enable) must not both spin up a writer loop.
        if (Interlocked.CompareExchange(ref _publishing, 1, 0) != 0) return;
        _writerLoop = Task.Run(WriterLoopAsync);
    }
```

and change `TryReachSupervisor`'s guard from `if (!_supervised) return false;` to:

```csharp
        // Not publishing? Then do not probe. A successful probe adopts this app into the
        // supervisor's Supervising state, after which the absence of periodic heartbeats
        // gets it force-killed within HangThresholdSeconds. Probing without publishing is
        // a delayed kill, not a harmless question.
        if (Volatile.Read(ref _publishing) == 0) return false;
```

Finally, make `Stamp` skip the wake-up when there is no writer to wake. Step 5 starts the timer unconditionally, so `Stamp` now runs every 3s in standalone mode — which is the default (`SupervisionSettings.Enabled == false`). `_sendSignal` is `new(0, 1)` (line 51), so with nothing draining it the count saturates at 1 and **every tick from the second one onward throws `SemaphoreFullException` into the existing catch**. Caught, so not a correctness bug, but it is ~20 thrown exceptions a minute on the dispatcher forever, and it makes any first-chance-exception debugging session unusable. Change the tail of `Stamp` (lines 82-87) from:

```csharp
        try { _sendSignal.Release(); }
        catch (SemaphoreFullException) { /* already signaled — writer will pick up _pending */ }
```

to:

```csharp
        // Nothing to wake in standalone mode: the timer stamps unconditionally (App.xaml.cs)
        // but no writer loop exists, so the semaphore would saturate at its max of 1 and every
        // subsequent Release would throw into the catch below — every 3s, for the life of the
        // process. The payload still lands in _pending, which is all CurrentPayload needs, and
        // EnablePublishing's writer drains it on its first pass.
        if (Volatile.Read(ref _publishing) == 0) return;

        try { _sendSignal.Release(); }
        catch (SemaphoreFullException) { /* already signaled — writer will pick up _pending */ }
```

The `catch` stays: it still covers the genuine case it was written for, a stamp arriving while the writer is mid-send.

- [ ] **Step 5: Create the heartbeat timer unconditionally**

In `src/EyeCtrl.App/App.xaml.cs`, replace the conditional timer (lines 213-228):

```csharp
            // ── Heartbeat liveness (supervisor watchdog) ──
            // A DispatcherTimer stamps a HeartbeatPayload on the UI thread every ~3s. A bounded
            // async writer sends it over the `eyectrl_watch` named pipe to the supervisor. The
            // Dispatcher NEVER blocks on IPC — Stamp() only refreshes an in-memory slot. No-ops
            // (no writer task, no timer) in standalone mode (Supervision.Enabled == false).
            // lastFrameUtc is fed by every raw gaze sample (valid or invalid), so a wedged poll
            // loop that stops delivering frames is detectable.
            _heartbeatPublisher = new HeartbeatPublisher(supervised: settings.Supervision.Enabled);
            gazeEngine.GazeSampled += _ => _heartbeatPublisher.OnGazeSample();
            if (settings.Supervision.Enabled)
            {
                var heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                heartbeatTimer.Tick += (_, _) =>
                    _heartbeatPublisher.Stamp(gazeProvider.IsConnected, im.CurrentState == InteractionState.Paused);
                heartbeatTimer.Start();
            }
```

with:

```csharp
            // ── Heartbeat liveness (supervisor watchdog) ──
            // A DispatcherTimer stamps a HeartbeatPayload on the UI thread every ~3s. A bounded
            // async writer sends it over the `eyectrl_watch` named pipe to the supervisor. The
            // Dispatcher NEVER blocks on IPC — Stamp() only refreshes an in-memory slot.
            // lastFrameUtc is fed by every raw gaze sample (valid or invalid), so a wedged poll
            // loop that stops delivering frames is detectable.
            //
            // The TIMER now runs unconditionally; the WRITER is what standalone mode omits
            // (HeartbeatPublisher.EnablePublishing). Stamping with no writer costs one field
            // assignment every 3s — Stamp skips the semaphore Release when not publishing,
            // see below — and it means that if the user starts a supervisor mid-session,
            // heartbeats begin flowing the moment publishing is enabled, with no window in
            // which the supervisor has adopted an app that is not yet stamping.
            _heartbeatPublisher = new HeartbeatPublisher(supervised: settings.Supervision.Enabled);
            gazeEngine.GazeSampled += _ => _heartbeatPublisher.OnGazeSample();
            var heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            heartbeatTimer.Tick += (_, _) =>
                _heartbeatPublisher.Stamp(gazeProvider.IsConnected, im.CurrentState == InteractionState.Paused);
            heartbeatTimer.Start();
```

**One consequence of the unconditional timer, verified safe — recorded so nobody "fixes" it.** `Stamp` passes `_shuttingDown` positionally with `RestartRequested` defaulting to false (`HeartbeatPublisher.cs:81`). So a tick landing *after* `SignalShuttingDown(restartRequested: true)` has latched `_shuttingDown` would put `{shuttingDown: true, restartRequested: false}` on the wire — apparently retracting the restart. It cannot: the supervisor's `CleanExitRequested` case is guarded `when State == SupervisorState.Supervising` (`SupervisionStateMachine.cs:90`), and by then the machine is already in `CleanExitPending`, so the duplicate falls through unmatched and `_restartAfterExit` survives untouched.

Two independent reasons it is unreachable anyway — a `DispatcherTimer` cannot tick while `OnExit` occupies the dispatcher, and `SignalShuttingDown` flushes synchronously — but the guard is the one that would still hold if either changed. If you ever make `Stamp` preserve the latched restart intent, do it because the positional default is fragile, not because this scenario is live.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/SupervisorLauncher.cs src/EyeCtrl.App/Services/HeartbeatPublisher.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.App.Tests/SupervisorLauncherTests.cs tests/EyeCtrl.App.Tests/HeartbeatRestartPayloadTests.cs
git commit -m "feat(restart): locate the supervisor, and allow publishing to start mid-session"
```

---

## Task 12: A modal that can offer more than two choices

**Files:**
- Create: `src/EyeCtrl.ViewModels/ModalChoice.cs`
- Create: `src/EyeCtrl.App/Views/ModalButtonPalette.cs`
- Create: `tests/EyeCtrl.App.Tests/ModalButtonPaletteTests.cs`
- Modify: `src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs:207-220,289-330`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:1300-1373`
- Modify: `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs`

The settings modal is hardcoded to Yes/No — two ids (`modal_yes`, `modal_no`) branched on by name in `OnTargetActivated` (`SettingsWindowViewModel.cs:207-220`), and two buttons built literally in `UpdateModalVisibility` (`SettingsWindow.xaml.cs:1311-1319`). Task 13 needs three.

**The existing two-button API and its appearance must not change.** Four modal tests already pin it (`SettingsWindowViewModelTests.cs:223-274`), and every reset confirmation in the settings window goes through it. So `ShowModal(message, onYes)` survives as a thin wrapper that builds the standard pair with the same ids, and two-choice modals keep their horizontal layout exactly as today. Only three-or-more switches to a vertical stack, which is also where descriptive labels stop fitting side by side.

**One contrast trap, worth naming before it is written.** `MakeModalButton` hardcodes `Tokens.BrushSurfaceDark` as the foreground (`SettingsWindow.xaml.cs:1353`), which is correct on the bright semantic fills used by Yes and No — dark label on a light button. A neutral third option filled with `SurfaceMedium` is *dark*, so the same foreground would be dark-on-dark. This is the identical failure the Plan A review caught (semantic tokens as button fills under white text, 2.16:1). The mapping therefore lives in one tested place rather than inline.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/ModalButtonPaletteTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Windows.Media;
using EyeCtrl.App.Views;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Modal buttons are filled shapes with a label on them, so the label's contrast
/// depends on the fill. The bright semantic fills need a dark label; a neutral fill is
/// itself dark and needs a light one. Getting this backwards produced a 2.16:1 button
/// during the overlay migration, which is why the mapping is a tested function rather
/// than a literal at the call site.
/// </summary>
public class ModalButtonPaletteTests
{
    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData(ModalChoiceKind.Affirmative)]
    [InlineData(ModalChoiceKind.Neutral)]
    [InlineData(ModalChoiceKind.Negative)]
    public void EveryKind_PutsALegibleLabelOnItsFill(ModalChoiceKind kind)
    {
        var (background, foreground) = ModalButtonPalette.For(kind);
        var ratio = Contrast(background, foreground.Color);

        Check.WithCustomMessage(
                $"{kind} modal button is {ratio:F2}:1 label-on-fill; 4.5 is the AA floor "
                + "for the 24px bold label these buttons use.")
            .That(ratio).IsStrictlyGreaterThan(4.5);
    }

    [Fact]
    public void AffirmativeAndNegative_KeepTheirExistingFills()
    {
        // Regression guard: every reset confirmation in the settings window already uses
        // these. This task must not restyle them.
        Check.That(ModalButtonPalette.For(ModalChoiceKind.Affirmative).Background)
             .IsEqualTo(Tokens.ToggleOn);
        Check.That(ModalButtonPalette.For(ModalChoiceKind.Negative).Background)
             .IsEqualTo(Tokens.AccentDanger);
    }

    [Fact]
    public void Neutral_IsDistinctFromBothOthers()
    {
        var neutral = ModalButtonPalette.For(ModalChoiceKind.Neutral).Background;
        Check.That(neutral).IsNotEqualTo(ModalButtonPalette.For(ModalChoiceKind.Affirmative).Background);
        Check.That(neutral).IsNotEqualTo(ModalButtonPalette.For(ModalChoiceKind.Negative).Background);
    }
}
```

Add to `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs`, inside the Modal Tests region:

```csharp
    [Fact]
    public void ShowModal_TwoButtonForm_StillOffersYesAndNo()
    {
        // The wrapper must keep the ids every existing confirmation depends on.
        var vm = CreateViewModel();
        vm.ShowModal("Confirm action?", () => { });

        Check.That(vm.ModalChoices.Select(c => c.Id)).ContainsExactly("modal_yes", "modal_no");
    }

    [Fact]
    public void ShowModal_ThreeChoices_ExposesThemAll()
    {
        var vm = CreateViewModel();
        vm.ShowModal("Pick one", new[]
        {
            new ModalChoice("a", "Alpha", ModalChoiceKind.Affirmative, () => { }),
            new ModalChoice("b", "Beta", ModalChoiceKind.Neutral, () => { }),
            new ModalChoice("c", "Cancel", ModalChoiceKind.Negative, null),
        });

        Check.That(vm.IsModalVisible).IsTrue();
        Check.That(vm.ModalChoices.Select(c => c.Label)).ContainsExactly("Alpha", "Beta", "Cancel");
    }

    [Fact]
    public void ChoosingAMiddleOption_InvokesOnlyItsAction_AndCloses()
    {
        var vm = CreateViewModel();
        var chosen = new List<string>();
        vm.ShowModal("Pick one", new[]
        {
            new ModalChoice("a", "Alpha", ModalChoiceKind.Affirmative, () => chosen.Add("a")),
            new ModalChoice("b", "Beta", ModalChoiceKind.Neutral, () => chosen.Add("b")),
            new ModalChoice("c", "Cancel", ModalChoiceKind.Negative, () => chosen.Add("c")),
        });

        vm.SimulateActivated("b", DwellCategory.Modal);

        Check.That(chosen).ContainsExactly("b");
        Check.That(vm.IsModalVisible).IsFalse();
    }

    [Fact]
    public void AChoiceWithNoAction_JustCloses()
    {
        var vm = CreateViewModel();
        vm.ShowModal("Pick one", new[]
        {
            new ModalChoice("a", "Alpha", ModalChoiceKind.Affirmative, () => { }),
            new ModalChoice("c", "Cancel", ModalChoiceKind.Negative, null),
        });

        vm.SimulateActivated("c", DwellCategory.Modal);

        Check.That(vm.IsModalVisible).IsFalse();
    }

    [Fact]
    public void HideModal_ClearsTheChoices()
    {
        // Stale choices would let a dwell on a leftover target fire an action from a
        // modal the user already dismissed.
        var vm = CreateViewModel();
        vm.ShowModal("Test", () => { });

        vm.HideModal();

        Check.That(vm.ModalChoices).IsEmpty();
    }
```

Add `using System.Linq;` and `using System.Collections.Generic;` to that test file if absent.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~ModalButtonPaletteTests|FullyQualifiedName~SettingsWindowViewModelTests" --nologo
```

Expected: **build failure** — `ModalChoice`, `ModalChoiceKind`, `ModalButtonPalette` and `ModalChoices` do not exist.

- [ ] **Step 3: Create the choice model**

Create `src/EyeCtrl.ViewModels/ModalChoice.cs`:

```csharp
namespace EyeCtrl.ViewModels;

using System;

/// <summary>
/// What a modal choice means, so the view can colour it. Deliberately semantic rather
/// than a colour: EyeCtrl.ViewModels references only EyeCtrl.Core and cannot see the
/// palette in EyeCtrl.Controls.
/// </summary>
public enum ModalChoiceKind
{
    /// <summary>The action the modal is asking about. Rendered as the confirm button.</summary>
    Affirmative,

    /// <summary>An alternative way forward — neither the primary action nor backing out.</summary>
    Neutral,

    /// <summary>Backing out without acting.</summary>
    Negative,
}

/// <summary>
/// One button in a settings modal.
/// </summary>
/// <param name="Id">
/// Dwell-target id, unique within the modal. The two-button form uses <c>modal_yes</c>
/// and <c>modal_no</c>, which existing confirmations and their tests depend on.
/// </param>
/// <param name="Label">Text on the button.</param>
/// <param name="Kind">Drives colour; see <see cref="ModalChoiceKind"/>.</param>
/// <param name="OnChosen">
/// Invoked when this choice is taken, before the modal closes. Null for a choice that
/// only dismisses.
/// </param>
public sealed record ModalChoice(string Id, string Label, ModalChoiceKind Kind, Action? OnChosen);
```

- [ ] **Step 4: Generalise the ViewModel**

In `src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs`, add the property beside the other modal state (near line 46):

```csharp
    /// <summary>Buttons currently offered by the modal, in display order. Empty when hidden.</summary>
    [ObservableProperty] private IReadOnlyList<ModalChoice> _modalChoices = [];
```

Replace `ShowModal` (lines 289-312) with the wrapper plus the general form. The wrapper preserves the existing ids and behaviour exactly:

```csharp
    /// <summary>
    /// Shows a two-button Yes/No confirmation. Unchanged in behaviour and appearance —
    /// every reset confirmation in the settings window uses this form.
    /// </summary>
    /// <param name="message">Text displayed in the modal dialog body.</param>
    /// <param name="onYes">Invoked when the user confirms (dwells or clicks Yes).</param>
    public void ShowModal(string message, Action onYes) =>
        ShowModal(message,
        [
            new ModalChoice("modal_yes", "✓ Yes", ModalChoiceKind.Affirmative, onYes),
            new ModalChoice("modal_no", "✕ No", ModalChoiceKind.Negative, null),
        ]);

    /// <summary>
    /// Shows a modal offering <paramref name="choices"/>. Clears any previous modal state
    /// and always notifies the view to re-register modal dwell targets, even if the modal
    /// was already visible.
    /// </summary>
    /// <param name="message">Text displayed in the modal dialog body.</param>
    /// <param name="choices">Buttons to offer, in display order. Must not be empty.</param>
    public void ShowModal(string message, IReadOnlyList<ModalChoice> choices)
    {
        // Clear any existing modal state
        _modalDwell.ClearTargets();
        _modalActions.Clear();
        ModalProgress.Clear();
        OnPropertyChanged(nameof(ModalProgress));

        // Reset non-modal detectors so stale EnteredTimestamp values accumulated before
        // the modal opened cannot fire a spurious activation when updates resume on close.
        _tabDwell.ResetAllState();
        _controlDwell.ResetAllState();
        _pagDwell.ResetAllState();

        ModalMessage = message;
        ModalChoices = choices;
        // Always fire PropertyChanged so the view always re-registers the modal targets.
        // SetProperty skips notification if IsModalVisible is already true, which would
        // leave _modalDwell without targets after ClearTargets() above.
        if (!IsModalVisible)
            IsModalVisible = true;
        else
            OnPropertyChanged(nameof(IsModalVisible));
    }
```

Delete the `_modalYesAction` field and its assignment — the affirmative action now lives on its `ModalChoice`.

In `HideModal`, clear the list alongside the message:

```csharp
        ModalChoices = [];
```

Replace the `DwellCategory.Modal` case in `OnTargetActivated` (lines 207-220):

```csharp
            case DwellCategory.Modal:
                // Cache the callback before HideModal() clears _modalActions.
                _modalActions.TryGetValue(id, out var modalAction);

                if (id == "modal_yes")
                {
                    _modalYesAction?.Invoke();
                    HideModal();
                }
                else if (id == "modal_no")
                {
                    HideModal();
                }

                modalAction?.Invoke();
                break;
```

with:

```csharp
            case DwellCategory.Modal:
                // Cache both callbacks before HideModal() clears the modal state.
                _modalActions.TryGetValue(id, out var modalAction);
                var choice = ModalChoices.FirstOrDefault(c => c.Id == id);

                // An id that is not one of the current choices is stale — a target left
                // over from a modal that has already closed. Ignore it rather than
                // closing a modal the user is still reading.
                if (choice is not null)
                {
                    var chosen = choice.OnChosen;
                    HideModal();
                    chosen?.Invoke();
                }

                modalAction?.Invoke();
                break;
```

Add `using System.Linq;` if absent.

Note the ordering change: `HideModal()` now runs *before* the chosen action, not after. Task 13's supervisor choice shuts the app down from that callback, and leaving the modal state live across a shutdown would mean dwell targets pointing at a disposing window.

- [ ] **Step 5: Create the colour mapping**

Create `src/EyeCtrl.App/Views/ModalButtonPalette.cs`:

```csharp
namespace EyeCtrl.App.Views;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;

/// <summary>
/// Fill and label colours for modal buttons.
///
/// The label colour depends on the fill and cannot be a constant. Affirmative and
/// Negative are bright semantic tokens, so they take a dark label; Neutral is a dark
/// surface, so it takes a light one. Hardcoding the dark label for all three is exactly
/// the mistake that produced a 2.16:1 button during the overlay migration, so the pairing
/// lives here and is gated by ModalButtonPaletteTests.
/// </summary>
public static class ModalButtonPalette
{
    /// <summary>Fill and label for a choice kind.</summary>
    /// <param name="kind">The choice's meaning.</param>
    /// <returns>Background colour and a frozen foreground brush.</returns>
    public static (Color Background, SolidColorBrush Foreground) For(ModalChoiceKind kind) => kind switch
    {
        // Bright fills: dark label.
        ModalChoiceKind.Affirmative => (Tokens.ToggleOn, Tokens.BrushSurfaceDark),
        ModalChoiceKind.Negative => (Tokens.AccentDanger, Tokens.BrushSurfaceDark),

        // Dark fill: light label. Same surface as the ordinary control buttons, so a
        // neutral option reads as "another way forward", not as a second confirm.
        ModalChoiceKind.Neutral => (Tokens.SurfaceMedium, Tokens.BrushTextPrimary),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
```

- [ ] **Step 6: Render the choices**

In `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`, replace the modal build block (lines 1306-1319):

```csharp
            ModalMessage.Text = _viewModel.ModalMessage;
            ModalButtonPanel.Children.Clear();
            _modalButtons.Clear();
            _modalBrushes.Clear();

            var yesBtn = MakeModalButton("modal_yes", "✓ Yes", ModalYes);
            var noBtn = MakeModalButton("modal_no", "✕ No", ModalNo);

            ModalButtonPanel.Children.Add(yesBtn);
            ModalButtonPanel.Children.Add(noBtn);

            // Register modal buttons as dwell targets with DPI-scaled radius
            _viewModel.Register("modal_yes", DwellCategory.Modal, DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs, () => { });
            _viewModel.Register("modal_no", DwellCategory.Modal, DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs, () => { });
```

with:

```csharp
            ModalMessage.Text = _viewModel.ModalMessage;
            ModalButtonPanel.Children.Clear();
            _modalButtons.Clear();
            _modalBrushes.Clear();
            _modalBaseColors.Clear();

            // Two choices keep the horizontal row they have always had. Three or more
            // stack vertically: side by side they would not fit descriptive labels, and a
            // vertical stack gives a dwell user more separation between targets.
            var choices = _viewModel.ModalChoices;
            var vertical = choices.Count > 2;
            ModalButtonPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

            foreach (var choice in choices)
            {
                var (background, foreground) = ModalButtonPalette.For(choice.Kind);
                var button = MakeModalButton(choice.Id, choice.Label, background, foreground, vertical);
                ModalButtonPanel.Children.Add(button);

                // Register as a dwell target with DPI-scaled radius. The empty callback is
                // the extension point; the ViewModel runs the choice's own action.
                _viewModel.Register(
                    choice.Id, DwellCategory.Modal,
                    DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs, () => { });
            }
```

Replace `MakeModalButton` (lines 1339-1373):

```csharp
    /// <summary>Builds one modal button.</summary>
    /// <param name="id">Dwell-target id.</param>
    /// <param name="text">Label.</param>
    /// <param name="bg">Fill, from <see cref="ModalButtonPalette"/>.</param>
    /// <param name="foreground">Label brush, paired with the fill for contrast.</param>
    /// <param name="wide">
    /// True for a vertically stacked layout, where buttons are wider (descriptive labels)
    /// and separated by vertical margins instead of horizontal ones.
    /// </param>
    private Border MakeModalButton(string id, string text, Color bg, SolidColorBrush foreground, bool wide)
    {
        var modalBrush = new SolidColorBrush(bg);
        var border = new Border
        {
            Width = wide ? 460 : 170,
            Height = 80,
            Margin = wide ? new Thickness(0, 8, 0, 8) : new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            Background = modalBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            }
        };

        // Mouse fallback. Everything routes through the ViewModel now — it owns which
        // choice does what, so the view no longer branches on the id.
        border.MouseLeftButtonDown += (_, _) => _viewModel.SimulateActivated(id, DwellCategory.Modal);

        _modalButtons[id] = border;
        _modalBrushes[id] = modalBrush;
        _modalBaseColors[id] = bg;
        return border;
    }
```

**The dwell-progress blend needs the same per-choice base.** `UpdateModalVisuals` (`SettingsWindow.xaml.cs:1281-1292`) still branches on `id == "modal_yes" ? ModalYes : ModalNo` for the blend base — with three choices that paints every new button's progress from `AccentDanger` red, and it is the reason `ModalYes`/`ModalNo` cannot simply be deleted. Replace the branch with a per-id base stored at build time:

```csharp
    private void UpdateModalVisuals()
    {
        foreach (var (id, progress) in _viewModel.ModalProgress)
        {
            if (_modalBrushes.TryGetValue(id, out var brush)
                && _modalBaseColors.TryGetValue(id, out var baseC))
            {
                // HighlightColor(baseC, progress, 0.5) == Blend(baseC, Accent, progress * 0.5)
                brush.Color = ColorBlend.Blend(baseC, Tokens.Accent, progress * 0.5);
            }
        }
    }
```

Add the dictionary beside `_modalBrushes` (line 72):

```csharp
    private readonly Dictionary<string, Color> _modalBaseColors = new();
```

and clear it wherever `_modalBrushes` is cleared — both branches of `UpdateModalVisibility` (the visible branch above, and the collapsed branch at lines 1326-1327). The two-button form keeps its exact behaviour: its choices are Affirmative/Negative, so the bases are `ToggleOn`/`AccentDanger` — the former `ModalYes`/`ModalNo` values — and the existing reset confirmations blend exactly as they do today.

`ModalYes` and `ModalNo` (`SettingsWindow.xaml.cs:48-49`) can now be deleted — with the build block, `MakeModalButton` and `UpdateModalVisuals` migrated, nothing reads them (a compiler warning and `StaticTokenCaptureTests` flag unused fields). Confirm nothing else references them:

```bash
grep -n "ModalYes\|ModalNo" src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
```

Expected: no output. Add `using EyeCtrl.ViewModels;` for `ModalChoiceKind` if absent, and confirm `Orientation` resolves (`System.Windows.Controls`).

- [ ] **Step 7: Run the suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures — **including the four pre-existing modal tests** (`SettingsWindowViewModelTests.cs:223-274`). Those passing untouched is the point of this task: the two-button form is unchanged.

The vertical three-button layout hardcodes `Width = 460` for wide buttons. That value is **not verified headlessly** — the `ModalButtonPaletteTests` check contrast, not fit, and the settings window's available width is a runtime concern. Scenario C in Task 14 (the only step with eyes on the UI) must confirm the three wide buttons fit without clipping before this ships; if they overflow, lower 460 rather than growing the panel.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.ViewModels/ModalChoice.cs src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs src/EyeCtrl.App/Views/ModalButtonPalette.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/ModalButtonPaletteTests.cs tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs
git commit -m "feat(settings): let the modal offer more than two choices"
```

---

## Task 13: Offer to start the supervisor

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/AppLifecycleMessages.cs`
- Modify: `src/EyeCtrl.App/Services/AppRestartCoordinator.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs` (coordinator construction)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (the prompt)
- Modify: `tests/EyeCtrl.App.Tests/AppRestartCoordinatorTests.cs`

When a supervisor is installed but not running, offer to start it — that converts the restart from the fallback strategy into the preferred one, and leaves the *new* instance watched afterwards. Declining restarts anyway, unsupervised. Three choices, which is what Task 12 built the modal for:

| Choice | Kind | Effect |
|---|---|---|
| `↻ Restart with supervisor` | Affirmative | Enable supervision, start it, then supervisor-mediated restart |
| `↻ Restart without` | Neutral | Self-restart. No settings change |
| `✕ Cancel` | Negative | Nothing happens |

**Enabling supervision is part of the first choice, not a side effect.** The relaunched instance must publish heartbeats, or the supervisor adopts it and force-kills it after `HangThresholdSeconds`. So that choice sets `Supervision.Enabled = true` and persists it, and its label plus the modal body say so.

- [ ] **Step 1: Extend the tests**

In `tests/EyeCtrl.App.Tests/AppRestartCoordinatorTests.cs`, extend `Harness` with the new seams — add these fields:

```csharp
        public bool SupervisorInstalled { get; set; }
        public bool SupervisorLaunches { get; set; } = true;
        public int Launches { get; private set; }
        public bool PublishingEnabled { get; private set; }
        public bool SupervisionPersisted { get; private set; }
        public EyeCtrlSettings Settings { get; }
```

assign `Settings = settings;` in the constructor, and pass four more arguments to the coordinator:

```csharp
                supervisorInstalled: () => SupervisorInstalled,
                launchSupervisor: () => { Launches++; BringUp.Add("launch"); return SupervisorLaunches; },
                enablePublishing: () => { PublishingEnabled = true; BringUp.Add("publish"); },
                persistSettings: () => { SupervisionPersisted = true; BringUp.Add("persist"); },
```

`BringUp` records the bring-up sequence so the documented order is pinned, not merely the end state — add it beside the other fields:

```csharp
        /// <summary>Bring-up steps in the order TryBringUpSupervisor performed them.</summary>
        public List<string> BringUp { get; } = new();
```

Update `Request` to carry the choice:

```csharp
        public void Request(bool startSupervisor = false) =>
            Messenger.Send(new RestartAppRequestedMessage(startSupervisor));
```

Then add:

```csharp
    [Fact]
    public void OffersTheSupervisor_WhenInstalledButNotRunning()
    {
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };
        Check.That(h.Coordinator.ShouldOfferSupervisor()).IsTrue();
    }

    [Fact]
    public void DoesNotOffer_WhenNoSupervisorIsInstalled()
    {
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = false };
        Check.That(h.Coordinator.ShouldOfferSupervisor()).IsFalse();
    }

    [Fact]
    public void DoesNotOffer_WhenOneIsAlreadyRunning()
    {
        var h = new Harness(supervisionEnabled: true) { SupervisorInstalled = true, SupervisorReachable = true };
        Check.That(h.Coordinator.ShouldOfferSupervisor()).IsFalse();
    }

    [Fact]
    public void AcceptingTheOffer_EnablesPublishingAndPersistsBeforeLaunching()
    {
        // Order matters twice over: publishing must be on before the probe can safely
        // adopt us, and Enabled must be persisted before the replacement starts, or the
        // supervisor adopts an instance that never heartbeats and kills it.
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };
        h.SupervisorReachable = true;   // it comes up on the first poll

        h.Request(startSupervisor: true);

        Check.That(h.PublishingEnabled).IsTrue();
        Check.That(h.Settings.Supervision.Enabled).IsTrue();
        Check.That(h.SupervisionPersisted).IsTrue();
        Check.That(h.Launches).IsEqualTo(1);
        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsTrue();
        Check.That(h.Log).ContainsExactly("shutdown");

        // The ORDER, not just the end state. Asserting only the three booleans let the
        // implementation drift from its own doc comment once already: publishing must be on
        // before anything can adopt us, and persist must precede the launch. It is also the
        // only throwing step, so it goes last.
        Check.That(h.BringUp).ContainsExactly("publish", "persist", "launch");
    }

    [Fact]
    public void SupervisorThatNeverComesUp_FallsBackToSelfRestart()
    {
        // Degrades rather than fails: supervision is now enabled, so the replacement
        // publishes heartbeats and the late-starting supervisor adopts it.
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };
        h.SupervisorReachable = false;

        h.Request(startSupervisor: true);

        Check.That(h.Launches).IsEqualTo(1);
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsFalse();
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void SupervisorThatFailsToLaunch_FallsBackToSelfRestart()
    {
        var h = new Harness(supervisionEnabled: false)
        {
            SupervisorInstalled = true,
            SupervisorLaunches = false,
        };

        h.Request(startSupervisor: true);

        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void DecliningTheOffer_RestartsUnsupervised_AndChangesNoSettings()
    {
        // "Restart without" is a real restart, not a cancel. Cancelling is the third
        // choice, and it never reaches the coordinator at all.
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };

        h.Request(startSupervisor: false);

        Check.That(h.Launches).IsEqualTo(0);
        Check.That(h.Settings.Supervision.Enabled).IsFalse();
        Check.That(h.SupervisionPersisted).IsFalse();
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AppRestartCoordinatorTests" --nologo
```

Expected: **build failure** — the message takes no argument and the coordinator has no `ShouldOfferSupervisor`.

- [ ] **Step 3: Carry the choice on the message**

In `src/EyeCtrl.ViewModels/Messages/AppLifecycleMessages.cs`, replace:

```csharp
public record RestartAppRequestedMessage;
```

with:

```csharp
/// <param name="StartSupervisor">
/// True when the user chose to start the supervisor first. Only ever set by a surface
/// that actually asked — the coordinator does not prompt.
/// </param>
public record RestartAppRequestedMessage(bool StartSupervisor = false);
```

- [ ] **Step 4: Add the branch to the coordinator**

In `src/EyeCtrl.App/Services/AppRestartCoordinator.cs`, add four dependencies and store them:

```csharp
    private readonly Func<bool> _supervisorInstalled;
    private readonly Func<bool> _launchSupervisor;
    private readonly Action _enablePublishing;
    private readonly Action _persistSettings;
```

Add the offer predicate, rewrite `HandleRestart`, and add the bring-up helper:

```csharp
    /// <summary>
    /// Whether the settings surface should offer to start a supervisor before restarting.
    /// True only when one is installed and none is currently answering — offering what is
    /// not installed, or starting a second one, would both be noise.
    /// </summary>
    /// <remarks>
    /// <b>Not a pure query:</b> the reachability check sends one real heartbeat, and a live
    /// supervisor records/adopts it (see <c>Program.cs</c>'s heartbeat handler). That is safe
    /// here because the probe is only evaluated when <c>Supervision.Enabled</c> is already
    /// true — i.e. periodic heartbeats are already flowing, so the probe's extra payload is a
    /// no-op adoption, not the delayed-kill from Task 11 (which concerns probing while NOT
    /// publishing). The side-effect is called out because a reader named <c>Should…</c> would
    /// not expect it to reach across a process boundary.
    ///
    /// <b>Runs on the dispatcher</b> (the dwell handler asks it synchronously), so its probe
    /// is bounded by the publisher's <c>ProbeWait</c> (500ms): a live supervisor answers in
    /// milliseconds, and an absent one must not freeze the user's gaze control while they
    /// wait for a modal. The restart path itself never probes on the dispatcher — see
    /// <see cref="HandleRestart"/>.
    /// </remarks>
    /// <returns>True if the offer is worth making.</returns>
    public bool ShouldOfferSupervisor() =>
        _supervisorInstalled() && !(_settings.Supervision.Enabled && _tryReachSupervisor());

    /// <summary>
    /// Picks a strategy and runs it. Returns immediately; the work continues in the
    /// background. The dispatcher never blocks: the probe, the bring-up poll and the
    /// self-restart wait all happen off-thread, and the dispatcher only ever executes
    /// the shutdown or the refusal — a frozen dispatcher means no gaze processing, i.e.
    /// the user loses control of the machine for the duration.
    /// </summary>
    /// <param name="startSupervisor">True when the user chose to start a supervisor first.</param>
    public void HandleRestart(bool startSupervisor)
    {
        // One restart attempt at a time (see Task 8). Re-armed on the refusal path.
        if (Interlocked.Exchange(ref _restartInFlight, 1) != 0)
        {
            return;
        }

        _runInBackground(() =>
        {
            // Guarded exactly as in Task 8, and it matters more here: TryBringUpSupervisor
            // calls _persistSettings, i.e. SettingsService.Save, which THROWS
            // InvalidOperationException when settings.Validate() fails. Unguarded, that
            // exception would vanish into an unobserved faulted Task with the button left
            // latched — and with Supervision.Enabled already mutated in memory.
            try
            {
                // Strategy 1 — supervisor-mediated. Enabled is necessary but not sufficient:
                // the app may have been launched by hand with no supervisor running.
                if (_settings.Supervision.Enabled && _tryReachSupervisor())
                {
                    _postToUi(() =>
                    {
                        SupervisorRelaunchRequested = true;   // before _shutdown: OnExit reads this
                        _shutdown();
                    });
                    return;
                }

                // Strategy 1b — the user asked us to start a supervisor first, which upgrades
                // this restart to the recoverable path AND leaves the replacement watched.
                if (startSupervisor && TryBringUpSupervisor())
                {
                    _postToUi(() =>
                    {
                        SupervisorRelaunchRequested = true;
                        _shutdown();
                    });
                    return;
                }

                // Strategy 2 — self-restart. Begin() blocks for up to the readiness timeout.
                var outcome = _selfRestart();
                _postToUi(() =>
                {
                    if (outcome == RestartOutcome.Started)
                    {
                        _shutdown();
                    }
                    else
                    {
                        Volatile.Write(ref _restartInFlight, 0);   // re-arm: the user may try again
                        _reportUnavailable(SelfRestartStrategy.Describe(outcome));
                    }
                });
            }
            catch (Exception ex)
            {
                // Never leave the button latched — see Task 8.
                Volatile.Write(ref _restartInFlight, 0);
                _postToUi(() => _reportUnavailable(
                    "Restart failed: " + ex.Message + " EyeCtrl is still running."));
            }
        });
    }

    /// <summary>
    /// Enables supervision, starts the supervisor, and waits for it to answer.
    ///
    /// Ordering is load-bearing in both directions, and the three statements below are in
    /// their documented order — keep them that way:
    ///
    /// <list type="number">
    /// <item><b>Publishing on FIRST.</b> The probe that follows adopts this app into the
    /// supervisor's Supervising state, and an adopted app that sends nothing is force-killed
    /// after HangThresholdSeconds. It is also the only one of the three that cannot throw,
    /// so doing it first means a later failure leaves a consistent session rather than one
    /// claiming supervision while silent.</item>
    /// <item><b>Then the in-memory flag</b>, so the running session's own behaviour matches
    /// what is about to be written.</item>
    /// <item><b>Then persist</b>, BEFORE the replacement launches, because that replacement
    /// must publish too, for exactly the same reason. This is the step that can throw —
    /// <c>SettingsService.Save</c> rejects settings that fail validation — and
    /// <see cref="HandleRestart"/>'s catch is what turns that into a report rather than a
    /// dead button.</item>
    /// </list>
    ///
    /// Returns false on any failure, and the caller falls through to self-restart — which
    /// still improves matters, since supervision is now on and a late-arriving supervisor
    /// will adopt the replacement.
    /// </summary>
    private bool TryBringUpSupervisor()
    {
        // Publishing FIRST, and before the throwing call. _persistSettings can throw
        // (SettingsService.Save rejects invalid settings), and if it did with publishing
        // still off we would be left claiming Supervision.Enabled in memory while sending
        // no heartbeats — the delayed-kill shape from Task 11. Enabling first makes that
        // failure inert; HandleRestart's catch then reports it and re-arms.
        _enablePublishing();
        _settings.Supervision.Enabled = true;
        _persistSettings();

        if (!_launchSupervisor())
        {
            return false;
        }

        // Poll rather than sleep-then-check: a supervisor that comes up in 300ms should
        // not cost the user the whole budget.
        var deadline = DateTime.UtcNow + SupervisorStartWait;
        while (DateTime.UtcNow < deadline)
        {
            if (_tryReachSupervisor())
            {
                return true;
            }

            Thread.Sleep(SupervisorPollInterval);
        }

        return false;
    }

    /// <summary>How long to wait for a just-started supervisor to answer on the pipe.</summary>
    private static readonly TimeSpan SupervisorStartWait = TimeSpan.FromSeconds(15);

    /// <summary>Gap between reachability polls while waiting for the supervisor.</summary>
    private static readonly TimeSpan SupervisorPollInterval = TimeSpan.FromMilliseconds(500);
```

Change the registration to pass the flag:

```csharp
        messenger.Register<RestartAppRequestedMessage>(this, (_, m) => HandleRestart(m.StartSupervisor));
```

Add `using System.Threading;`.

**Keep the Task 8 tests compiling:** the four new constructor parameters are required, so the direct (non-harness) constructions in `AppRestartCoordinatorTests` must pass them too — `SupervisorFlagIsSetBeforeShutdown_SoOnExitCanSeeIt`, `SupervisorProbe_RunsOffTheUiThread_AndShutsDownOnIt`, `SecondRequest_WhileTheFirstIsInFlight_IsIgnored` and `SelfRestart_RunsOffTheUiThread_AndShutsDownOnIt` each gain `supervisorInstalled: () => false, launchSupervisor: () => false, enablePublishing: () => { }, persistSettings: () => { }`. Those tests do not exercise the offer path, so the no-op defaults are correct for them.

- [ ] **Step 5: Wire the new dependencies**

In `src/EyeCtrl.App/App.xaml.cs`, extend the coordinator construction from Task 9:

```csharp
                supervisorInstalled: () => SupervisorLauncher.IsInstalled,
                launchSupervisor: SupervisorLauncher.TryLaunch,
                enablePublishing: () => _heartbeatPublisher?.EnablePublishing(),
                persistSettings: () =>
                {
                    settingsService.Save(settings);
                    // Supervision just turned on: the app must stop self-registering the
                    // HKCU Run key, or the next logon launches it BOTH from the Run key and
                    // from the supervisor's own launch path (a transient double-launch the
                    // mutex resolves, but a race at logon). Same reconciliation the startup
                    // block performs, with the captured `supervised` flag now known true.
                    startupRegistrar.SetStartOnBoot(
                        StartOnBootPolicy.ShouldSelfRegister(supervised: true, settings.General.StartOnBoot));
                },
```

`settingsService` is already resolved at `App.xaml.cs:107`, `startupRegistrar` at line 119. Immediately after the coordinator construction, hand it to the settings window's owner so Task 13 Step 6 can pass it through:

```csharp
            _eventCoordinator.SetRestartCoordinator(_restartCoordinator);
```

- [ ] **Step 6: Make the offer**

In `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`, replace the restart button's action from Task 10. The window needs the coordinator: add `AppRestartCoordinator restartCoordinator` to the constructor parameter list and store it in `_restartCoordinator`. `SettingsWindow` is constructed in `EventCoordinator.ShowSettingsWindow` (`EventCoordinator.cs:1146`), so thread the coordinator through there: give `EventCoordinator` a `_restartCoordinator` field plus

```csharp
    /// <summary>Hands the restart coordinator to the settings window at construction. Called once at startup.</summary>
    public void SetRestartCoordinator(AppRestartCoordinator coordinator) => _restartCoordinator = coordinator;
```

(mirroring `SetModelServices`), and pass it as the new argument at `EventCoordinator.cs:1146`, using the same guard the model services use:

```csharp
                _restartCoordinator ?? throw new InvalidOperationException(
                    "Restart coordinator not set; SetRestartCoordinator must be called at startup."),
```

The settings window is only reachable after startup completes, so the throw can never fire in practice.

```csharp
                () => Add(new DwellButton(_viewModel, "g_restart", "↻ Restart Now",
                    () =>
                    {
                        // Only offer what is installed and not already running.
                        if (_restartCoordinator.ShouldOfferSupervisor())
                        {
                            _viewModel.ShowModal(
                                "Restart EyeCtrl now?\n"
                                + "A supervisor is installed. It restarts EyeCtrl if it ever "
                                + "fails, and can recover a restart that goes wrong.\n"
                                + "Starting it turns supervision on and leaves it on.",
                                [
                                    new ModalChoice("restart_supervised", "↻ Restart with supervisor",
                                        ModalChoiceKind.Affirmative,
                                        () => _messenger.Send(new RestartAppRequestedMessage(StartSupervisor: true))),
                                    new ModalChoice("restart_plain", "↻ Restart without",
                                        ModalChoiceKind.Neutral,
                                        () => _messenger.Send(new RestartAppRequestedMessage())),
                                    new ModalChoice("restart_cancel", "✕ Cancel",
                                        ModalChoiceKind.Negative, null),
                                ]);
                        }
                        else
                        {
                            ShowModal(
                                "Restart EyeCtrl now?",
                                () => _messenger.Send(new RestartAppRequestedMessage()));
                        }
                    },
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80,
                    textBrush: Tokens.BrushSurfaceDark)),
```

The body is three short lines: what is being asked, what the supervisor buys, and — the third line — that choosing it turns supervision on **and leaves it on**. That last point is a persistent settings change made on the user's behalf, so it is stated outright rather than left to be discovered in the settings list afterwards. "and leaves it on" is doing real work in that sentence: without it, "turns supervision on" reads as something scoped to this restart.

Three lines is the ceiling for a dwell modal. If a fourth is ever wanted, cut one of the first two rather than growing the block — **and do not add a fourth button.**

Add `using EyeCtrl.ViewModels;` for `ModalChoice`/`ModalChoiceKind`.

The ViewModel item (Task 10 Step 3) keeps sending the plain message: that list is a secondary surface and does not prompt.

- [ ] **Step 7: Run the suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/AppLifecycleMessages.cs src/EyeCtrl.App/Services/AppRestartCoordinator.cs src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/AppRestartCoordinatorTests.cs
git commit -m "feat(restart): offer to start the supervisor when one is installed but idle"
```

---

## Task 14: End-to-end checks

> ## STOP — REQUIRES EXPLICIT GO-AHEAD
>
> **Do not run this task without asking the user first and receiving a clear yes.**
>
> The user controls this PC solely by eye tracking. This task deliberately shuts down
> the application that provides that control. Everything else in this plan is verified
> headlessly precisely so this is the only step that carries that risk.
>
> **Before asking, confirm all of the following and state them in the request:**
> - `dotnet build` and `dotnet test --filter "Category!=UI"` are green (Tasks 1-13).
> - Which scenario you propose to run, and that it is **one** of them, not all three.
> - The user has a way to recover if the app does not come back — a caregiver present,
>   or a known-good way to launch EyeCtrl by other means.
>
> **If the user declines, that is a complete answer.** Tasks 1-13 stand on their own;
> the feature ships unverified end to end, and this document says so.

All three scenarios are log-driven rather than screen-driven, so they stay short and leave evidence.

- [ ] **Scenario A — unsupervised self-restart**

Confirm `Supervision.Enabled` is `false` and **move the supervisor exe aside** so no offer is made. Ask the user to open Settings → General and dwell **Restart Now**, confirming the modal. Do not drive the UI.

```bash
Get-Content "$env:APPDATA\EyeCtrl\logs\eyectrl*.log" -Tail 40
```

Expected: a `Shutdown` from the outgoing instance, then a fresh startup line seconds later.

| Observation | Diagnosis |
|---|---|
| Modal says "did not start in time" | The child never signalled — check Task 6's `SignalReady` runs *before* the mutex wait |
| Modal says "closed immediately" | The child exited early; look for its own startup exception in the log |
| App exits and does not come back | The handshake passed but init failed after it — the residual risk; check the replacement's log |
| Two instances briefly visible | The child did not wait on the mutex — check Task 5's timeout is being passed |
| General page controls clip or overflow the page | Seven controls now share one non-chunking page (Task 10's restart button plus the ITheme combo) — reduce the row height or split `BuildGeneralPages` into two pages, as the Quick Actions builder already does (fit cannot be verified headlessly) |

- [ ] **Scenario B — supervisor-mediated restart**

Set `Supervision.Enabled` to `true`, start the supervisor, confirm it reaches `Supervising`, then the same dwell.

```bash
Get-Content "$env:APPDATA\EyeCtrl\logs\supervisor*.log" -Tail 30
```

Expected: clean-exit notification → `Bound process exited (pid=…)` → `LaunchApp: starting …` **within about a second**, not after a backoff delay → heartbeat → `Supervising`.

| Observation | Diagnosis |
|---|---|
| No relaunch, supervisor in `IdleWatch` | The restart flag did not reach the state machine — check Task 4 |
| Relaunch happened but a strike was booked | `ProcessExited` reached the machine while still `Supervising` (the `ShuttingDown:true` payload lost the race with process exit) — check Task 2; the synchronous 2s flush in `SignalShuttingDown` is what minimizes this window |
| Relaunch delayed 5-60s | `RelaunchDelayPolicy` not consulted, or the state pair is wrong — check Task 3. The second argument must be the POST-transition `_sm.State`; passing `prevState` twice makes the restart branch never match and every relaunch inherit backoff |
| Relaunch was immediate after a **hung** shutdown (force-killed first) | The reverse error: the pair is being collapsed to `prevState` alone, so the `DeadlineElapsed → Recovering` crash recovery is skipping its brake — check Task 3 |
| Two instances start | `SupervisorRelaunchRequested` was set on a self-restart path — check Task 8 |

- [ ] **Scenario C — the three-way offer**

The highest-value scenario: it is the only one that exercises the ordering Task 13 warns about, and the only one that shows the three-button modal. Set `Supervision.Enabled` to `false`, ensure the supervisor exe **is** present, and ensure no supervisor is running. Then the same dwell.

First, without shutting anything down, confirm the modal shows **three** vertically stacked buttons and that all three are readable. Ask the user to pick **Cancel** — that exercises the new modal path with zero risk. Only then repeat and pick "Restart with supervisor".

```bash
Get-Content "$env:APPDATA\EyeCtrl\logs\supervisor*.log" -Tail 30
Get-Content "$env:APPDATA\EyeCtrl\logs\eyectrl*.log" -Tail 20
```

Expected: supervisor starts, adopts the outgoing app (`IdleWatch → Supervising`), then the ordinary clean-exit-and-relaunch sequence from scenario B. Afterwards `Supervision.Enabled` should be `true` in `settings.json`, and the new instance should be publishing heartbeats.

| Observation | Diagnosis |
|---|---|
| No offer in the modal | `ShouldOfferSupervisor` false — check the exe resolves (`EYECTRL_SUPERVISOR_EXE`, or a sibling of the app exe) |
| Cancel does nothing but the modal stays open | The choice id is not in `ModalChoices` — check Task 12's `OnTargetActivated` lookup |
| A button's label is hard to read | `ModalButtonPalette` pairing — the neutral fill needs the light label, not the dark one |
| **App force-killed ~20s after the supervisor started** | The ordering bug Task 13 exists to prevent: publishing was not enabled before the probe adopted the app. Check `TryBringUpSupervisor` |
| Supervisor adopts, then kills the *replacement* | `Supervision.Enabled` was not persisted before the relaunch, so the new instance is not publishing |
| Falls back to self-restart | The supervisor did not answer within 15s — check it actually started, then consider raising `SupervisorStartWait` |

- [ ] **Record the result in this document**

Append a status block at the top of this file — `**STATUS:** scenarios A and C verified <date>; B not run`, or whatever is true. A plan that claims a supervised launch was verified when it was not is worse than one that admits the gap.

- [ ] **Commit**

```bash
git add docs/superpowers/plans/2026-07-30-restart-now.md
git commit -m "docs(restart): record the end-to-end verification result"
```

---

## Verification summary

| Gate | Command | Expected |
|---|---|---|
| Build | `dotnet build EyeCtrl.slnx --nologo` | 0 errors |
| Tests | `dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo` | 0 failures (baseline 1710 + ~70) |
| End-to-end | Task 14 | Only with explicit go-ahead |

Tasks 1-13 are fully headless. Three environmental notes:

- **Task 1's tests bind the real `eyectrl_watch` pipe**, so a running supervisor will steal the connection and make them flake. Check for a live `EyeCtrl.Supervisor` process before investigating a failure there.
- **So does a `dotnet test` running in ANOTHER WORKTREE.** Pipe names are machine-global and bound with a single server instance, so a concurrent run elsewhere on the box steals the connection outright — and this repo routinely has a dozen worktrees checked out. The symptom is maximally misleading: a *different* test fails on each run, which reads as flaky product code rather than an external process. `xUnit`'s `DisableParallelization` cannot help; it is process-local. Before investigating any pipe-test failure, run `Get-Process -Name "dotnet","testhost*","EyeCtrl*"` and confirm the box is quiet, then re-run. This branch's App.Tests take ~21s (up from ~7s) because of the pipe timeouts, so the contention window is wide.
- **Tasks 5 and 7 use real named kernel objects.** The tests use per-test GUID names and are safe in parallel. The `EyeCtrl.App.RestartReady` event is **parent-owned**: the strategy creates it (unset) before launch and disposes it after the wait, and the child `OpenExisting`s it rather than creating-or-opening — so a leftover Set event from a prior run cannot produce a false ready (the parent always observes its own fresh object). If you experiment by hand outside the strategy, create+dispose the event yourself rather than leaving a stray Set event named `Local\EyeCtrl.App.RestartReady`.
- **Task 11's tests mutate `EYECTRL_SUPERVISOR_EXE`** for the current process and restore it in `Dispose`. They must not run in parallel with anything else reading that variable — they are the only such tests today.

**CI will run on this PR**, because it branches from `main` and the workflow triggers on `branches: [main]`.

## What this slice does not do

- **No fallback AT launched by the app.** Deliberately excluded. Where a supervisor is installed, Task 13 offers to start it instead — which fixes the situation rather than cushioning it, and leaves the replacement watched. Crash-loop escalation to a fallback AT remains the supervisor's job alone (`Program.cs`, `OnFallenBack`).
- **No net for a self-restart with no supervisor installed.** If the replacement announces itself and then dies during initialisation, nothing retries it. This is the accepted residual risk of the unsupervised path, and it is why Task 13's offer exists.
- **No modal changes beyond choice count.** Task 12 generalises the button row; it does not add scrolling, keyboard handling, or a dismiss-by-looking-away. Modals with more than about four choices will overflow the panel — nothing today comes close, and a guard would be speculative.
- **No pause-state preservation.** A restart behaves exactly like a normal launch and applies `General.StartPaused`.
- **No "apply without restarting."** Live theme re-resolution — recreating the overlay windows in place — is still unbuilt, and remains the better long-term answer for the theme picker specifically.
- **No hardening of the probe/exit race (supervised path).** If the supervisor dies in the ~2s between probe and exit, the app will not come back. Closing that would need a supervisor→app acknowledgement, and the pipe is one-way today.
