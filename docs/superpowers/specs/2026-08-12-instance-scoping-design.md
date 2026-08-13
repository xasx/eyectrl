# Instance Scoping — Design (v5)

**Date:** 2026-08-12
**Status:** Approved design, not yet implemented
**Prerequisite for:** `docs/superpowers/plans/2026-08-10-core-e2e-test-plan.md` (supersedes its Task 1)
**Revision:** v5 after round-4 review (0 BLOCKERs, 1 MAJOR, 4 MINORs; shape verdict: converges,
implementable as written). See §"Revision history".

## Problem

`EyeCtrl.App` binds process-external state on every launch, and nothing scopes any of it to the
running process. A second instance — a test run, a scratch build, a sibling worktree's suite —
collides with the first.

This surfaced while reviewing the core end-to-end test plan, which introduced an
`EYECTRL_APPDATA_DIR` override and then claimed a run "cannot read or rewrite the live
configuration." The claim was false: every launch also calls `IStartupRegistrar.SetStartOnBoot`
(`App.xaml.cs:150-157`), and `WindowsStartupRegistrar` **deletes** `HKCU\…\Run\EyeCtrl` when the
value is false (`WindowsStartupRegistrar.cs:34-37`). A profile redirect does not touch the
registry.

The failure was never the missing registry guard. It was that the redirect covered the one
resource that had been thought of, and no mechanism would have noticed the others.

### Enumeration has failed four times

| Attempt | Count | Missed |
|---|---|---|
| E2E plan Task 1 | 1 | everything but the profile |
| This spec, v1 draft | 5 | settings-writer mutex, restart-ready event |
| v1 reviewed | 7 | supervisor singleton, supervisor profile, heartbeat **server** name |
| v2 reviewed | 5 + 2 refused | the heartbeat **exit path** — see below |

Two of these are instructive beyond the count.

**v1's third miss:** the heartbeat row scoped the *client* and left the *server* fixed —
`HeartbeatPipeServer.PipeName` is a `const string = "eyectrl_watch"`
(`src/EyeCtrl.Supervisor/Ipc/HeartbeatPipeServer.cs:43`) — the identical server/client asymmetry
this same document had diagnosed two sections earlier for the dwell pipe.

**v2's miss:** v2 declared the heartbeat pipe "gone, not scoped" on the grounds that refusing
supervision made it unreachable. It traced the *enabling* path and not the *exit* path.
`SignalShuttingDown` dials the pipe with no `_publishing` gate
(`HeartbeatPublisher.cs:138-151`), the publisher is constructed unconditionally
(`App.xaml.cs:260`), and `OnExit`/`OnSessionEnding` call it unconditionally
(`App.xaml.cs:375,389`). That is the postmortem's signature error — specify the destination,
declare it closed — committed on the one subsystem v2 claimed to have removed from the problem.

**The repairs have their own record.** v3 stated the heartbeat fix correctly in one section and
left the discarded draft — "do not construct the publisher" — standing in four others, including
the phase-2 acceptance criterion and the test list. An implementer following TDD would have
written `publisher == null`, made it pass, and hit the gaze-thread NRE this document had already
diagnosed. Round 3 caught it. A stated fix in this document is not self-evidently safe, and the
implementation plan must treat each one as a claim to verify.

Hand enumeration of this surface is not a reliable method. Four attempts, four misses. That is
the argument for the automated guard in §Testing, and the reason a reviewer should re-derive the
table from source rather than audit the rows presented here.

### Terminology

"Machine-global" was imprecise and obscured real differences in blast radius:

- **Per-user persistent state** — survives reboot, visible to every process of this user: the
  profile directory, the `HKCU\…\Run` value.
- **Session-named kernel objects** — `Local\…`, scoped to the logon session: the app singleton
  mutex, the settings-writer mutex, the restart-ready event.
- **Machine-wide named pipes** — no namespace qualifier: `eyectrl_dwell`, `eyectrl_watch`.

All three need instance separation. Only the third is genuinely machine-wide.

## The shape: a scoped instance is self-contained

v1 tried to scope every name in a three-process system with one environment variable. Review
found that untenable: the supervisor is a separate process with its own singleton, its own
unscoped profile resolution and its own fixed pipe name, and the restart handoff signals
readiness *before* a child could have validated its instance.

v2 removed those from the problem rather than solving them, and round 2 confirmed that boundary
is the right shape. v3 keeps it and closes the one path v2 left open.

> A scoped instance runs alone. Supervision and self-restart are refused, and every path that
> reaches their process-external state is gated — including exit paths.

When an instance id is set:

- **Supervision is forced off**, not merely refused. `Supervision.Enabled` is ignored: no
  supervisor is launched (`SupervisorLauncher.TryLaunch`), the heartbeat publisher is
  constructed but never publishes, no probe runs, and the settings window's supervisor offer is
  suppressed. Forcing
  is chosen over a startup refusal because the refusal could not be ordered — see
  §"Why supervision is forced rather than refused".
- **Self-restart is refused.** `AppRestartCoordinator.HandleRestart` refuses under a scoped
  instance, `SelfRestartStrategy.Begin` refuses as a second line, and `--restart-handoff` is
  rejected at startup. The restart-ready event leaves the design because
  `RestartHandoff.SignalReady` is reachable only with that argument and the parent-side event is
  created only by `Begin` (`SelfRestartStrategy.cs:125-126`).

  **`HandleRestart` is the load-bearing gate; suppressing the offer is not sufficient.**
  `ShouldOfferSupervisor` returns `_supervisorInstalled() && !(Enabled && probe)`
  (`AppRestartCoordinator.cs:140-141`), which with `Enabled` forced false evaluates to "is a
  supervisor exe present" — typically true. Hiding the offer only removes a button; both
  `SettingsWindow.xaml.cs:1045-1071` and `SettingsViewModel.cs:175-176` still send
  `RestartAppRequestedMessage`, and `HandleRestart` → `TryBringUpSupervisor`
  (`AppRestartCoordinator.cs:256-265`) calls `_enablePublishing()` **before** flipping `Enabled`.
  That is the one mid-session route onto the heartbeat pipe, and it defeats the forced-off gate
  unless `HandleRestart` itself refuses.
- **The SDK is not ambient.** `DwellClient` does not read the environment; the host names the
  pipe explicitly. See §The SDK.

Cost, stated rather than discovered later: tier-2 coverage of supervision, restart and heartbeat
can never run scoped and will need a different harness — most likely one driving the supervisor
directly. Recorded as an open question.

### Why supervision is forced rather than refused

v2 had a fail-closed row "scoped with `Supervision.Enabled` → exit 78". That could not be
ordered. The flag lives only in settings, which load *after* the phase where the check sat; a
dedicated early `Load()` contradicts "before anything is bound" and can write a migrated file
back without the writer mutex (`SettingsService.cs:123-126`); and moving the check after the
load means the refusing instance has already loaded settings.

Forcing deletes the contradiction instead of resolving it. There is no ordering question because
there is no check: the composition root constructs the publisher with `supervised: false` and
never launches a supervisor when scoped, whatever the profile says. It is observable rather than silent — logged,
and recorded in the manifest's `refused` array — which was the objection to a downgrade in the
first place.

### The heartbeat exit path

**One change: `SignalShuttingDown` gains the `_publishing` guard it is missing**, matching
`TryReachSupervisor` (`HeartbeatPublisher.cs:179`). Its doc comment already claims "if not
supervised (no server), this is a bounded no-op" — true only when no server exists. When the
user's own supervisor *is* listening, an unsupervised instance's exit dials it.

Combined with supervision being forced off, that closes the path: scoped ⇒ `supervised: false`
⇒ `_publishing == 0` ⇒ every entry point including the exit path is inert.

**Not** "when scoped, do not construct the publisher." That was the first draft of this fix and
it is wrong: `App.xaml.cs:261` and `:264` dereference `_heartbeatPublisher` **without** `?.`, on
every gaze sample and every three-second tick. A null publisher would throw on the gaze thread
and the dispatcher rather than staying quiet. Only `:274`, `:291`, `:375`, `:389` and `:412` are
null-safe. Guarding the method is the whole fix; adding a null as defence in depth introduces a
worse failure than the one it guards.

**This exposure is created by this design, not inherited.** Today the singleton mutex is what
prevents a second instance from ever reaching that exit path. Suffixing the mutex removes that
protection, so the latent bug becomes reachable *because* of scoping. Consequence if left
unfixed, verified end to end: a scoped instance's graceful exit sends `shuttingDown: true`;
`ClientIdentityBinder.ForAdoption` authenticates on exe path + session + ACL and deliberately
skips the creation-time comparison (`ClientIdentityBinder.cs:38-66`), so the connection binds as
verified; `CleanExitRequested` carries no PID (`SupervisionInputs.cs:16`), so the supervisor
applies it to whatever it is supervising; the live app does not exit; the 5 s deadline expires
and it is force-killed with a crash strike, walking the escalation toward launching a fallback
AT on the user's desktop.

The heartbeat row therefore returns to the resource table as **refused, exit-path gated** — a
row with a stated gate, not an absence. That also gives the drift guard a defensible allow-list
entry instead of one that would have legitimised the residual.

### What the `--test-mode` requirement does and does not enforce

**A scoped instance additionally requires `--test-mode`.** Instance id without it fails closed.

v2 claimed this made the concurrency non-goal "enforced rather than documented." That was an
overclaim, and it is withdrawn. Precisely:

| Hazard | Status under v3 |
|---|---|
| Two instances binding one eye tracker | **Enforced.** `--test-mode` forces `MouseGazeProvider` (`AppBootstrapper.cs:86-89`) |
| Two instances colliding on names | **Enforced.** All five resources scoped |
| Two instances fighting over the OS cursor | **Not enforced.** Both still move the real cursor and read it back as gaze |
| Overlays, tray icon, foreign-window actions | **Not enforced.** Unnamed and screen-wide; each instance shows its own |

The last two are real and the gate does nothing about them. Worse, v2 *removed* the accidental
protection: an unscoped second instance used to exit on the singleton mutex, whereas two scoped
instances both start silently.

**This obligation lands on the E2E harness, and the plan must carry it.** The plan's
`E2EGate.FindSingleInstanceBlocker` probes `Local\EyeCtrl.App.Singleton` (plan:462-468) on a
rationale — "a second instance exits at startup" — that this design removes. It still detects a
*live* unscoped instance, which is worth keeping, but it can no longer detect or serialise a
second concurrent scoped run. The harness must additionally hold a well-known run lock
(`Local\EyeCtrl.E2E.RunLock`, deliberately *unscoped*) for the duration of a run, so two
concurrent E2E runs serialise instead of interleaving cursor writes. That is a plan change, not
a spec change, and it is called out here because v2 carried it nowhere.

## Goals and non-goals

**Goal.** One named concept — an *instance id* — that scopes the process-external names a
self-contained app instance binds, so a second instance can run without touching the first, and
so a harness can verify that it did.

| Non-goal | Enforcement |
|---|---|
| Concurrent real gaze-driven instances | scoped instance requires `--test-mode` |
| Supervised operation while scoped | supervision forced off ⇒ publisher never publishes; `SignalShuttingDown` guarded |
| Self-restart while scoped | refused at startup and mid-session; offer suppressed |
| Scoping the supervisor process | unreachable — no scoped instance launches one |
| Two scoped runs sharing the cursor | **not enforced here** — harness run lock, above |
| Gaze-input arbitration with the user's live AT | **not enforced here** — E2E plan's `GazePath` |

**Out of scope: user-facing surface.** No settings UI. The instance id is an environment
variable; `--test-mode` is an existing argument.

## The signal

```
EYECTRL_INSTANCE_ID=<id>
```

Presence scopes the instance; absence preserves today's behaviour byte for byte. The value is
both the switch and the uniquifying suffix.

**Validation.** `^[A-Za-z0-9_-]{1,64}$` — long enough for a GUID, restricted to characters legal
in pipe names, mutex names and path segments, so a malformed value can never produce an
unbindable name nor smuggle a separator into `Local\…` or a directory path.

Mirrors the existing `EYECTRL_MODEL_DIR`, `EYECTRL_SUPERVISOR_EXE`, `EYECTRL_APP_EXE` overrides.

### Why an instance id rather than a test flag

The first draft called this `EYECTRL_TEST_ISOLATION` and treated each resource as something to
suppress. Renaming the concept improved two decisions and both survive:

1. **The registry becomes a redirect.** There is no harmless registry *location*, but there is a
   harmless *value name*: `EyeCtrl` → `EyeCtrl_<id>` under the same key. The registrar stays live
   and assertable instead of stubbed out.
2. **Two knobs collapse into one.** The id derives the profile directory, so scoping yields a
   separate profile automatically and "id set but profile shared" disappears by construction.

## Placement

The rule lives in **`EyeCtrl.Contracts`**, which is forced rather than chosen:
`EyeCtrl.Sdk.Abstractions` targets `net8.0` and references only Contracts; it cannot see
`EyeCtrl.Core` (`net10.0`). Contracts is also where `DwellPipe` already lives, for a rationale
that extends verbatim — its doc comment says it is there so server and client "read the name
from one place and can never drift apart."

```csharp
namespace EyeCtrl.Contracts;

public static class InstanceScope
{
    public const string EnvVar = "EYECTRL_INSTANCE_ID";

    /// <summary>True when the id is well-formed. Out param is the trimmed id.</summary>
    public static bool TryParseId(string? raw, out string id);

    /// <summary>Appends the instance suffix, or returns defaultName unchanged when id is absent.</summary>
    public static string Scope(string defaultName, string? id);
}
```

### The reference graph decides who may call it

Only `EyeCtrl.App` and `EyeCtrl.Sdk.Abstractions` reference Contracts. `EyeCtrl.Core` has no
project references at all; `EyeCtrl.Platform` references only Core. Core and Platform therefore
**cannot** call `InstanceScope`, and adding references would push a transport concern into the
domain layer.

They do not need to. The composition root derives every name; components receive finished strings
and never learn instance ids exist. `AppPaths.Resolve(overrideDir, roamingAppData, instanceId)`
stays a pure function in Core taking the id as a parameter, so it needs no reference and is
testable without touching process environment.

`CalibrationViewModel.cs:63-65` derives `%APPDATA%\EyeCtrl` itself and ViewModels cannot see
Contracts either; the App's DI already injects a resolved calibration path
(`AppBootstrapper.cs:203`), so only the view model's own fallback needs removing.

## The resource table

**Scoped (five):**

| Resource | Kind | Default | Scoped | Receiver |
|---|---|---|---|---|
| Profile directory | per-user | `%APPDATA%\EyeCtrl` | `…\instances\<id>` | `AppPaths` (Core, new) |
| App singleton mutex | session object | `Local\EyeCtrl.App.Singleton` | `…_<id>` | `SingleInstanceGuard` (Core) |
| Settings-writer mutex | session object | `Local\EyeCtrl.Settings.Writer` | `…_<id>` | `SettingsService` (Core) |
| Dwell pipe | machine-wide | `eyectrl_dwell` | `eyectrl_dwell_<id>` | `DwellPipe` (Contracts) |
| Startup Run value | per-user | `EyeCtrl` | `EyeCtrl_<id>` | `WindowsStartupRegistrar` (Platform) |

Everything under the profile directory follows it: `settings.json`, `logs\`,
`calibration.json`, `actions.json`, `recent_emojis.json`, the user dictionary, `models\`, and the
new `instance.json`.

**All three resolvers must be wired, and phase 2 cannot tell you if one was missed.**
`App.xaml.cs:502-503`, `AppBootstrapper.cs:51` and `EventCoordinator.cs:98` each derive the
profile path independently. Phase 2 reads back a single `ProfileDirectory` value, so a missed
resolver leaves `recent_emojis.json`, the user dictionary or `actions.json` in the live profile
while the check still passes. The drift guard's `SpecialFolder.ApplicationData` rule is the
mechanism that catches this one, not phase 2 — which is why the guard covers ViewModels and why
`CalibrationViewModel.cs:63-65` must lose its fallback rather than merely be bypassed.

**The dwell pipe binds even when the feature is off.** `ExternalDwellService.Start` is called
unconditionally (`App.xaml.cs:309`); `ExternalDwell.Enabled` gates frame *processing*
(`ExternalDwellService.cs:109-115`), not the accept loop. So the pipe name is always bound and
always needs scoping — the E2E plan's seeded `externalDwell.enabled = false` does not avoid it.

**Refused, with a named gate (two):**

| Resource | Gate that makes it unreachable |
|---|---|
| Heartbeat pipe `eyectrl_watch` | `SignalShuttingDown` gains its `_publishing` guard; supervision forced off ⇒ `supervised: false` ⇒ never publishing. The publisher is still **constructed** — `App.xaml.cs:261,264` are not null-safe |
| Restart-ready event `Local\EyeCtrl.App.RestartReady` | `--restart-handoff` rejected; `HandleRestart`, `Begin` and `ShouldOfferSupervisor` all gated |

A refused row is not an absence. Each names the gate that must exist, so the drift guard's
allow-list entry points at a gate rather than legitimising a residual.

### Accepting a name is not being passed one

v1 claimed most receivers "already accept an injectable name" from #76 and treated the wiring as
done. The call sites pass nothing:

- `ExternalDwellService` is constructed with no `pipeName` (`AppBootstrapper.cs:236-242`).
- `WindowsStartupRegistrar` is registered with no arguments (`AppBootstrapper.cs:71-72`) and has
  no name parameter to accept.
- `SettingsService` is constructed before the DI container (`App.xaml.cs:500-506`) and builds its
  writer mutex from an inline literal (`SettingsService.cs:178`) — not even a named const.

Capability is not wiring. Two receivers need a new parameter; three need their existing parameter
actually supplied. §Fail-closed phase 2 is what proves none was missed.

## Startup ordering

`OnStartup` acquires the singleton mutex at `App.xaml.cs:94-95` and does not initialise Serilog
until `:111` — the code documents at `:101-103` that the rejection is therefore "deliberately
unlogged". A fail-closed check that must explain itself cannot live there, and the mutex's *name*
depends on the id, so resolution must precede it.

1. Parse `--test-mode`, `--paused`, `--restart-handoff` (unchanged).
2. Resolve and validate the instance id; **if it is malformed, write to stderr and exit `78`
   here** — there is no legal log directory to open yet. Otherwise derive the profile directory,
   **initialise Serilog**, then run the remaining **fail-closed phase 1** rows, which can
   therefore log. v2 had three of its four rows firing before Serilog existed; v4 leaves exactly
   one there, necessarily.
3. `RestartHandoff.SignalReady()` when handed off — unreachable while scoped, so it is never
   reached by an instance that might still fail validation.
4. Acquire the singleton mutex under its scoped name.
5. Load settings. When scoped, force `Supervision.Enabled` off in the in-memory graph and log it.
6. Build the DI container and wire services.
7. **Fail-closed phase 2**, then write the manifest — both before any overlay is shown and before
   `gazeEngine.Start()`, i.e. before anything can act on input.

Two improvements fall out: the rejection at `:104` becomes loggable, and settings are no longer
loaded by an instance about to exit, which matters because `SettingsService.Load` can **write** a
migrated file back (`SettingsService.cs:115-128`).

**Constraints preserved:** `SignalReady` still precedes the mutex on the unscoped handoff path
(`:79-85` documents that reversing it deadlocks); and nothing in step 2 touches the mutex.

**Note on "before the first window".** v2 said phase 2 runs "before the first window", which is
false — `ExternalDwellService`'s constructor builds a `DwellFeedbackOverlay`
(`ExternalDwellService.cs:86`), so a window object exists during step 6. The meaningful boundary
is *shown* overlays and a *started* gaze engine, which is what step 7 states.

## Fail-closed, in two phases

**Phase 1 — before anything is bound.** May this instance run?

| Condition | Behaviour |
|---|---|
| `EYECTRL_INSTANCE_ID` present but malformed | stderr, exit `78` — **not** Serilog, see below |
| Scoped without `--test-mode` | log fatal, exit `78` |
| Scoped with `--restart-handoff` | log fatal, exit `78` |
| No instance id | proceed exactly as today |

v2's fourth row — scoped with `Supervision.Enabled` — is gone; supervision is forced off in step
5 instead, for the ordering reason above.

**The malformed-id row cannot log.** A malformed id yields no legal `instances\<id>` directory, so
there is no scoped log path to initialise. Logging it anyway would open the **live**
`%APPDATA%\EyeCtrl\logs` (`LoggingInitializer.cs:21-26`) — writing to the profile the instance was
supposed to stay out of, on the one failure path where the id cannot be trusted. So this row
writes to stderr and exits; it is the only phase-1 row that runs before Serilog exists. The other
two run after step 2's logging init and log normally.

**Phase 2 — after wiring, before anything acts on input.** Did every resource take its scoped
name? Each receiver exposes its effective name as a property; the check reads them back off the
**constructed objects** and compares against the derived set. A mismatch, or a resource
reporting a default while scoped, is fatal. Refused subsystems are checked too: the publisher
must report `IsPublishing == false` — it is still constructed, see §The heartbeat exit path —
and the restart coordinator must report itself gated.

This is a read-back rather than a re-derivation on purpose — re-deriving would compute the same
expected value twice and agree with itself. It is the check that catches
`ExternalDwellService` being constructed without its `pipeName`.

**Phase 2 requires public surface that does not exist yet.** The spec assumed "each receiver
exposes its effective name" without saying what to add. The additions, all new:

| Member | On | Why |
|---|---|---|
| `bool IsPublishing` | `HeartbeatPublisher` | `_publishing` is a private `int` (`:68`); phase 2 reads it |
| effective-name getter | `SettingsService`, `WindowsStartupRegistrar`, `ExternalDwellService`, `SingleInstanceGuard` | phase 2 reads each back; four of the five have the name only in a private field |
| `bool IsScopeGated` | `AppRestartCoordinator` | phase 2 asserts the restart path reports itself refused |

**And `AppRestartCoordinator` must be told it is scoped.** `HandleRestart` is the load-bearing
gate, but nothing in the coordinator can see an instance id today and it must not read the
environment itself — that is the composition root's job. The scoped flag is constructor-injected
alongside its existing delegates (`tryReachSupervisor`, `enablePublishing`, …), which is also what
makes the gate unit-testable without process state.

Exit code `78` (`EX_CONFIG`) distinguishes isolation failure from the `0` a rejected second
instance already returns. Use `Application.Shutdown(78)`, matching `:104`.

**A refusal must not read as a crash to a supervisor.** `ProcessController.Launch` sets no
environment (`ProcessController.cs:55-57`), so a supervisor whose own environment carried an
instance id would relaunch a scoped, non-test-mode child that exits 78 repeatedly, booking crash
strikes toward `FallenBack`. `ProcessController.Launch` therefore strips
`EYECTRL_INSTANCE_ID` from the child environment.

## The manifest

`instance.json`, written in ordering step 7 after phase 2.

```json
{
  "instanceId": "e2e-8f3c1a",
  "processId": 24680,
  "startedAtUtc": "2026-08-12T09:14:22.113Z",
  "testMode": true,
  "resources": [
    { "name": "ProfileDirectory",    "scoped": true, "effectiveValue": "C:\\…\\instances\\e2e-8f3c1a" },
    { "name": "AppSingletonMutex",   "scoped": true, "effectiveValue": "Local\\EyeCtrl.App.Singleton_e2e-8f3c1a" },
    { "name": "SettingsWriterMutex", "scoped": true, "effectiveValue": "Local\\EyeCtrl.Settings.Writer_e2e-8f3c1a" },
    { "name": "DwellPipe",           "scoped": true, "effectiveValue": "eyectrl_dwell_e2e-8f3c1a" },
    { "name": "StartupRunValue",     "scoped": true, "effectiveValue": "EyeCtrl_e2e-8f3c1a" }
  ],
  "refused": ["Supervision", "SelfRestart"]
}
```

`effectiveValue` records the name each resource is **configured** with, not proof it is bound.
v2 said "bound", which overstated it: the settings-writer mutex is created per `Save` call
(`SettingsService.cs:178`) and may not bind until `OnExit`. Genuinely bound at manifest time are
the singleton mutex, the dwell pipe and the Run value.

camelCase, matching every other JSON emitter here. Not written when no id is set — a default
launch must leave no new artefact. A write failure is fatal: a harness that cannot verify scoping
must not go on to drive input.

## The SDK

`DwellClient` hard-codes `private const string PipeName = DwellPipe.Name;`
(`DwellClient.cs:17`), so under scoping the server binds `eyectrl_dwell_<id>` and a consumer
still dials `eyectrl_dwell`.

v1 had the SDK read the environment. That is rejected: `DwellHeadCore` owns a process-wide client
started lazily (`DwellHeadCore.cs:9-16`) and the connection loop swallows all failures
(`DwellClient.cs:96,151`), so a host inheriting a valid-but-stale id would silently target another
instance's pipe and say nothing.

The host names the pipe explicitly, with a **latched** contract — v2 said "set before first use;
ignored afterwards", which reintroduced the same silent failure on the late-set path:

```csharp
namespace EyeCtrl.Sdk.Abstractions;

public static class DwellHead
{
    /// <summary>
    /// Pipe this process's dwell client connects to. Defaults to <see cref="DwellPipe.Name"/>.
    /// The value is LATCHED at the first DwellHeadCore.EnsureStarted; a later set throws
    /// InvalidOperationException rather than being silently ignored.
    /// </summary>
    public static string PipeName { get; set; }
}
```

Three things v2 left unspecified, now pinned:

- **Where the snapshot lives — and a required refactor.** `DwellHeadCore.EnsureStarted` latches
  the value and passes it to the client's constructor. The reconnect loop reads the latched
  field, never the property; v2's "natural implementation" would have read the property inside
  the loop, making late sets apply on the next reconnect and contradicting its own contract.

  This is not implementable as `DwellHeadCore` stands: `Client` is a **field initialiser** —
  `public DwellClient Client { get; } = new();` (`DwellHeadCore.cs:9`) — so the client exists
  before `EnsureStarted` runs and before any name could be passed. Construction must move into
  `EnsureStarted`, behind the same `Interlocked.Exchange` latch it already uses (`:13-16`), with
  `Client` becoming nullable or lazily exposed. Callers touching `Client` before `EnsureStarted`
  must be found rather than assumed absent.
- **Throw, not ignore.** A set after the latch throws. Silently ignoring it is what made the
  late-set path indistinguishable from success, and `Dwell.Enabled="True"` in XAML latches during
  `InitializeComponent` (`Sdk.Wpf/Dwell.cs:60-71`) — early enough that a host setting the
  property in almost any startup hook would have been too late.
- **Two clients, one property — so the latch is static.** WPF and WinUI each own a separate
  process-wide client (`Sdk.Wpf/Dwell.cs:55` `new("wpf_")`, `Sdk.WinUI/Dwell.cs:42`
  `new("winui_")`). A per-client latch would let a process using both bind two different names,
  with the second set silently accepted. The latch flag therefore lives on `DwellHead` itself:
  the first `EnsureStarted` of either client latches, the second reads the same latched value,
  and any set after the first latch throws.

A mutable public static shared by every consumer in a process remains a real cost — a plugin
setting it affects its host. It is accepted because the alternative (threading a name through
`Dwell.Enabled`'s attached-property surface) would change a shipped XAML contract, which is
worse.

**Shipped-surface caution.** `EyeCtrl.Sdk.Abstractions` ships with its own test project, and
postmortem lesson 4 came from a refactor that touched this SDK assuming concept-equivalence and
regressed a live consumer's equality gate. The implementation plan must trace `DwellClient`'s
consumers and prove the latch contract against the real WPF and WinUI entry points rather than
asserting it.

## Testing

**Pure resolver.** `InstanceScope.TryParseId` and `.Scope` over strings: valid, empty,
whitespace, over-long, path separators, backslashes, characters illegal in pipe names. No
environment mutation — reading the variable happens in a thin property; only the pure function is
exercised, because `Environment.SetEnvironmentVariable` races sibling classes in the same xUnit
host. (`tests/EyeCtrl.App.Tests/SupervisorLauncherTests.cs:18-25` does mutate it; do not copy
that pattern.)

**Per-resource derivation.** One test per table row, so table and code cannot drift.

**Refusal rules.** One test per phase-1 condition asserting the exit code and that no resource
was bound; plus tests that a scoped instance never publishes, refuses `HandleRestart`
and `Begin`, and suppresses `ShouldOfferSupervisor`.

**The heartbeat regression.** `SignalShuttingDown` must send nothing when not publishing. Assert
directly against a pipe server that receives zero bytes — this is the test for the BLOCKER that
v2 shipped.

**Exactly one existing test pins the bug.**
`HeartbeatPublisherTests.SignalShuttingDown_DeliversFinalPayload_EvenUnderBlockingSyncContext`
(`HeartbeatPublisherTests.cs:46-95`) constructs with `supervised: false` and asserts the payload
**is** received. It is the F2 regression guard for a dispatcher-strand bug, so its delivery
assertion must survive: add `EnablePublishing()` before `SignalShuttingDown`. The writer loop then
exists but parks on `_sendSignal.WaitAsync` with nothing stamped and nothing released, so the
synchronous flush remains the only connection — which matters because the test's server is
`maxInstances: 1`. Do not weaken the guard to keep it green; it encodes the defect.

The two `HeartbeatRestartPayloadTests.SignalShuttingDown_*` tests are **unaffected** — they
already use `supervised: true` (`:174`, `:188`). An earlier draft of this section named them
instead, having matched test names to `supervised: false` line numbers that belong to
`TryReachSupervisor_IsFalse_WhenNotSupervised` (`:222`) and
`TryReachSupervisor_IsFalseWhileNotPublishing_EvenWithAServerListening` (`:295`) — both of which
already assert no send and are also unaffected.

**Guard the send, not the method.** An early return at the top of `SignalShuttingDown` would skip
`_shuttingDown = true` and `_pending = final`, breaking
`HeartbeatPublisherTests.SignalShuttingDown_StampsFinalPayload` (`:37-42`) and silently emptying
`CurrentPayload`, which exists for diagnostics. The guard wraps only the `SendOneAsync(...).Wait`
call. This is narrower than "matching `TryReachSupervisor`", which *is* an early return
(`HeartbeatPublisher.cs:179`) — the analogy holds for intent, not for placement.

**Phase-2 read-back.** Construct the wired graph with a scoped id; assert every receiver reports
its scoped name and every refused subsystem reports itself gated.

**Drift guard.** Roslyn-based, via a new `Microsoft.CodeAnalysis.CSharp` reference on the guard's
test project — the repo has never referenced Roslyn, and that dependency is the acknowledged cost
of the only mechanism that sees through wrappers. Regex over source cannot: v2's proposed guard
would have matched the pipe commentary in `ExternalDwellService.cs:20-35` and the tray commentary
in `SupervisorTrayIcon.cs:12-19` while missing anything reached indirectly. Scan syntax nodes for
constructor and member invocations across App, Core, Platform, ViewModels, **Supervisor and the
SDK**, over the API surface `Registry.*`, `new Mutex`, `new EventWaitHandle`,
`EventWaitHandle.OpenExisting`, `NamedPipeServerStream`, `NamedPipeClientStream`, `NotifyIcon`,
`SpecialFolder.ApplicationData`. `EventWaitHandle` is listed because its absence is why v2's
guard would have missed the restart-ready event — a resource v2 had just added to its own table.

Allow-list entries name a **symbol**, and each is asserted to resolve against a live symbol so a
stale exemption fails rather than silently widening. Entries for refused resources must point at
the gate, not merely at the call site.

**Integration.** Launch the app scoped; read `instance.json`; assert all five resources scoped
and both subsystems refused; assert the default `HKCU\…\Run\EyeCtrl` value is byte-identical
before and after; and assert a graceful exit sends nothing to `eyectrl_watch`. Carries
`Category=E2E` and the opt-in gate.

## Relationship to the end-to-end test plan

Supersedes **Task 1** of `docs/superpowers/plans/2026-08-10-core-e2e-test-plan.md`. `AppPaths`
survives as the profile-directory resource, with the id feeding it and `EYECTRL_APPDATA_DIR`
demoted to an explicit path override.

The plan must additionally:

- **set `EYECTRL_INSTANCE_ID`.** Its `AppHarness` currently passes `EYECTRL_APPDATA_DIR` only
  (plan:2556). Since this spec supersedes Task 1, the harness must set the id and let the profile
  path derive from it, keeping the path override only if it needs a specific location;
- pass `--test-mode` alongside the id, since scoping requires it (already done, plan:2553);
- **hold** `Local\EyeCtrl.E2E.RunLock` (unscoped) for the duration of each cursor-driving journey.
  "Hold" is the operative word and v3 did not specify it: the plan's only gate is
  `E2EGate.SkipUnlessRunnable` (plan:472-484), which probes and returns, and a `using` mutex
  inside it would release before `GazePath` ever moves the cursor. The lock must be an
  `IDisposable` owned by the journey — returned from the gate or held by a fixture — acquired with
  a `WaitOne` that **serialises** rather than skipping, and released in teardown. The timeout must
  be long enough to outlast a queued run, not merely a slow one: a journey is ~3.5 s of dwell plus
  app and target startup, so **five minutes** with the expiry treated as a hard failure, never a
  skip. A short bound silently degrades the lock into the probe it replaced. Every test that
  drives the cursor must take it. This matters because the E2E project has no
  `DisableTestParallelization`: each class is its own collection, so `LeftClickJourneyTests`,
  `SuppressionTests` and `CoordinateRoundTripTests` can otherwise interleave cursor writes;
- keep `E2EGate.FindSingleInstanceBlocker`'s probe of the *default* mutex — it still detects a
  live unscoped instance — but stop describing it as preventing a second run, which is the
  rationale this design removes;
- assert `instance.json` before any journey drives the cursor.

None of this closes a manually launched scoped instance running beside an E2E suite. That remains
un-enforced and is listed as such in §"What the `--test-mode` requirement does and does not
enforce".

Its seeded `supervision.enabled = false` becomes belt-and-braces rather than load-bearing, since
scoping forces supervision off regardless.

## Revision history

| Round | Finding | Resolution |
|---|---|---|
| 1 BLOCKER | supervisor singleton, profile, heartbeat server unscoped | supervision refused → first two unreachable; heartbeat handled in v3 |
| 1 BLOCKER | restart-handoff cannot be scoped safely | self-restart refused at startup *and* mid-session; event leaves the design |
| 1 BLOCKER | drift guard misses `EventWaitHandle`, excludes Supervisor | guard revised: Roslyn, `EventWaitHandle` added, trees widened |
| 1 MAJOR | fail-closed checked names, not bound resources | split into phase 1 (permission) and phase 2 (read-back) |
| 1 MAJOR | SDK ambient env read → silent wrong-pipe | explicit `DwellHead.PipeName` |
| 1 MAJOR | concurrency disclaimed, not prevented | `--test-mode` required; residual hazards tabulated honestly in v3 |
| 1 MAJOR | "single composition root" ignores Supervisor and SDK | both removed from scope by the refusal rules |
| 2 BLOCKER | heartbeat dialled on every graceful exit | `_publishing` guard added to `SignalShuttingDown`; row returns as refused-with-gate. The first draft of this fix also skipped constructing the publisher, which would have thrown at the two non-null-safe call sites — dropped |
| 2 MAJOR | phase 1 cannot read `Supervision.Enabled` | check deleted; supervision **forced off** in step 5 |
| 2 MAJOR | `--test-mode` does not enforce what v2 claimed | overclaim withdrawn; hazards tabulated; harness run lock required |
| 2 MAJOR | `DwellHead.PipeName` late-set silently ignored | latched at `EnsureStarted`; late set throws; two-client case pinned |
| 2 MINOR | phase-1 rows fire before Serilog | Serilog init moved ahead of the checks |
| 2 MINOR | manifest says "bound" | "configured", with the genuinely-bound subset named |
| 2 MINOR | supervisor offer still shown when scoped | `ShouldOfferSupervisor` gated |
| 2 MINOR | Roslyn dependency unstated | stated and accepted |
| 2 MINOR | supervisor relaunch of scoped child crash-loops | `ProcessController.Launch` strips the variable |
| 3 BLOCKER | v3 stated the heartbeat fix correctly once and the discarded draft in five other places, including phase 2's acceptance criterion | all five corrected to "constructed, never publishing"; the repair-failure record added to §Problem |
| 3 MAJOR | run lock specified as "hold" but never given an owner, acquire or release | shape pinned: `IDisposable` owned by the journey, bounded `WaitOne` that serialises, released in teardown |
| 3 MAJOR | gating `ShouldOfferSupervisor` alone leaves `HandleRestart` → `TryBringUpSupervisor` → `_enablePublishing()` reachable | `HandleRestart` named as the load-bearing gate; offer suppression demoted to cosmetic |
| 3 MINOR | malformed-id row cannot log — no scoped log dir exists yet | writes to stderr; explicitly must not open the live log directory |
| 3 MINOR | `DwellHeadCore.Client` is a field initialiser, so the latch is not implementable | construction must move into `EnsureStarted`; latch flag made static on `DwellHead` |
| 3 MINOR | phase 2 cannot detect a missed `%APPDATA%` resolver | all three named; the drift guard, not phase 2, is the mechanism |
| 4 MAJOR | the two tests named as pinning the heartbeat bug do not — they already use `supervised: true`; the cited line numbers belong to `TryReachSupervisor_*` tests | corrected to the one real test, `SignalShuttingDown_DeliversFinalPayload_EvenUnderBlockingSyncContext`; the misattribution is recorded in place |
| 4 MINOR | step 2 claimed every phase-1 row can log | reworded: the malformed-id row exits before Serilog, necessarily and alone |
| 4 MINOR | `IsPublishing` and the effective-name getters do not exist and were not specified as new | new-surface table added to §Fail-closed; `AppRestartCoordinator`'s scoped flag specified as constructor-injected |
| 4 MINOR | guarding `SignalShuttingDown` as an early return would skip `_pending` and break `SignalShuttingDown_StampsFinalPayload` | guard narrowed to wrap only the send |
| 4 MINOR | run-lock timeout unspecified; a short bound degrades to a skip | five minutes, hard failure on expiry, never a skip |

One round-2 provenance claim is not accepted: MAJOR 1 was described as an unaddressed round-1
finding, but the two-phase check is a v2 invention, so no round-1 finding could have referred to
it. Round 1's related MAJOR — fail-closed checking resolved rather than bound names — *is*
addressed by phase 2. The finding itself is real and fixed above; the provenance is not, and the
distinction matters because "unaddressed since round 1" and "introduced in v2" imply different
things about the process.

## Open questions

- Whether `EYECTRL_APPDATA_DIR` survives once the id derives the profile directory. Keeping it
  costs one branch and preserves an escape hatch; removing it is simpler.
- Whether tier-2 coverage of supervision, restart and heartbeat needs its own harness design, or
  whether `EyeCtrl.Supervisor.Tests` reaches far enough. Deferred until a journey needs it.
- Whether the `SignalShuttingDown` guard should ship separately and sooner. It is independent of
  everything else here, but it is **latent rather than live**: reaching it needs a second
  instance that exits gracefully, and today the singleton mutex makes a second instance exit at
  `App.xaml.cs:104` before the publisher is constructed. The existing `Category=UI` tests also
  kill the process (`AppFixture.DisposeAsync`) rather than exiting, so they never dial either.
  The guard is worth adding on correctness grounds — the method's own doc comment claims a
  no-op it does not deliver — but this spec is what would make it reachable, so shipping it
  with the rest is defensible.
