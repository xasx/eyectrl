# Gaze fan-out safety — design

**Date:** 2026-08-03
**Branch:** `worktree-gaze-observer-hub`
**Scope:** four contained fixes to the gaze hot path. No new contract, no plug-in mechanism, no migration.
**Status:** all four fixes implemented. Fix 4 `4291fbf`; Fix 3 `ec9cf95`, `a4ed34a`; Fix 1 `1712b42`, `64adf32`, `996cc23`; Fix 2 `84c0850`, `de6f616`. Review follow-ups `d877e1a` (fault-reporting path made throw-proof, mouse logger wired, mouse catch rate-limited), `611c8fa` (mapper cost, reporter remark, remaining test gaps), `93a8b06` (spec notes). Cost claim in the Cost section is now a measurement (`c50704e`). Final headless suite: 1897 passed / 0 failed / 1 skipped.

## Why this is small

An earlier draft of this work specified a full pluggable observer tier — `IGazeObserver`, a `GazeSample` carrying two coordinate spaces, declared consumption, a trip-and-disable fault policy, and a twelve-site migration. That version is preserved in commit `32243f9` and was deliberately rejected as oversized for the actual problem. Three reasons:

1. **The trip policy generated its own bug.** Trip-and-disable is what makes a tripped eye-care observer freeze `FatigueScore` into adaptation, which then needed a third mechanism (timestamp staleness) to solve. The measurement undercut the case for tripping anyway: a caught exception costs ~1 µs, about 0.003% of a 30 Hz frame. Read that as the cost of the *mechanism* — throw, unwind, catch — and not as the cost of a real fault, where the faulting handler's own partial work (WPF calls, Serilog I/O) would dominate. It bounds what isolation costs; it does not bound what a broken handler costs.
2. **Declared consumption was speculative.** Third-party extensions are explicitly not a goal, so the declaration bought one diagnostic and some documentation.
3. **The dual coordinate space is a permanent complexity tax** on every future author, incurred because one controller performs two kinds of test.

What survives is the part that stands on its own: the gaze fan-out can kill the process, and two coordinate/staleness bugs sit next to it. Extension-point design waits until a second consumer actually demands it.

Two changes since the first reduction, both worth noting because they moved in opposite directions. A calibration-logging fix was dropped as **already done elsewhere** (see [Related work](#related-work)). And Problem 4 — the uncalibrated toolbar hit-test — was pulled **in** from a recorded-but-deferred finding, because that same calibration work is about to activate it.

## Problem 1 — the fan-out is unguarded

`GazeSampled?.Invoke(gaze)` ([GazeEngine.cs:230](../../../src/EyeCtrl.Core/GazeEngine.cs)) and `CursorMoved?.Invoke(...)` (`:242`) are bare multicast invocations on the gaze thread. `GazeSampled` has six subscribers in `src/`, `CursorMoved` three. One throwing subscriber:

- **aborts the multicast chain**, so every subscriber registered after it silently stops receiving samples — registration order, which nothing declares, decides who survives;
- **unwinds `OnGazePoint` before `:242`**, so that sample produces no cursor motion and no dwell progress;
- **escapes onto the provider's thread** and terminates the process (Problem 2).

The subscribers do WPF work and Serilog I/O. `AdaptationRuntimeContainmentTests` already closes this hole on the dispatcher path — *"an escaping throw costs this user control of their machine"* — and the gaze thread has the same stake with less protection.

### Fix 1 — custom event accessors over a copy-on-write array

Keep both events exactly as they appear to callers. Replace the compiler-generated accessors with hand-written ones that maintain an array:

```csharp
private volatile Action<GazePoint>[] _gazeSampledHandlers = [];
private readonly object _handlerLock = new();

public event Action<GazePoint>? GazeSampled
{
    add    { if (value is not null) lock (_handlerLock) _gazeSampledHandlers = [.. _gazeSampledHandlers, value]; }
    remove { if (value is not null) lock (_handlerLock) _gazeSampledHandlers = RemoveLast(_gazeSampledHandlers, value); }
}
```

`remove` must strip only the **last** matching handler, not every match — that is what `-=` does on a multicast delegate, and duplicating the semantics matters if the same target is ever subscribed twice. A `Where(h => !h.Equals(value))` filter would silently remove all of them and is wrong.

Publication iterates the array by index and guards each handler individually:

```csharp
var handlers = _gazeSampledHandlers;          // single volatile read
for (int i = 0; i < handlers.Length; i++)
{
    try { handlers[i](gaze); }
    catch (Exception ex) { ReportHandlerFault(handlers[i], ex); }
}
```

Why this shape:

- **No call-site churn.** `+=` and `-=` still compile. All twelve subscription sites — six in `src/`, two in tests, three in benchmarks, one in `samples/` — are untouched. There is no migration step and therefore no migration risk.
- **No hot-path allocation.** The obvious alternative, `GazeSampled.GetInvocationList()`, allocates an array *per sample* at 30–90 Hz. Today's path allocates nothing; that alternative would trade a crash risk for GC pressure on the gaze thread. Copy-on-write allocates only on subscribe/unsubscribe, which happens a handful of times per session.
- **Reentrancy-safe by construction.** A handler that unsubscribes itself, or triggers a subscription change, cannot disturb an iteration already in progress — the loop holds its own array reference.
- **Lock-free publication.** The lock is taken only by `add`/`remove`.

### Fault reporting: log, rate-limited, never trip

A faulting handler stays subscribed. There is **no trip-and-disable**, deliberately: nothing ever silently disappears, so no downstream consumer can be left reading a frozen value — the failure mode that sank the larger design.

`ReportHandlerFault` logs at most **once per handler per 30 seconds**, keyed on the delegate, with an occurrence count since the last log. Rate limiting is not optional: unbounded Serilog I/O per sample, on the gaze thread inside the provider's lock, is a worse outcome than the exception it is reporting.

Handlers are identified by `Delegate.Method.DeclaringType` + `Method.Name`. For a lambda that yields something like `App+<>c__DisplayClass12_0.<OnStartup>b__5` — ugly, but it names the file and the enclosing method, which is enough to diagnose. No naming mechanism is needed.

### Cost

**Measured (`c50704e`, BenchmarkDotNet, 10/10 benchmarks, artifacts outside the repo):**

| Variant | Mean per sample | vs `A_BareMulticastEvent` | Allocated |
|---|---|---|---|
| `A_BareMulticastEvent` (today's shape) | 22.10 ns | 1.00 | `-` |
| `I_DelegateArray` (array, no try/catch) | 19.26 ns | 0.87 | `-` |
| `J_DelegateArray_TryCatch` (Fix 1's shape) | 20.72 ns | **0.94** | `-` |

Fix 1's shipped shape is cost-neutral against the multicast it replaces — slightly *faster* (0.94×, well under the 1.5× stop threshold), with the per-handler `try/catch` adding +7.6% over the bare array loop. Both new rows allocate nothing per publication, discharging the no-allocation requirement directly (an allocation assertion in xUnit would be brittle; BDN's `Allocated` column measures it). One caveat for exactness: the benchmark hoists a non-volatile field read out of the sample loop, whereas production does one volatile read per publication — a single non-hoistable load, so the conclusion stands.

## Problem 2 — an escaping exception kills the process

Both providers let an exception from the managed callback reach a thread with no handler.

| Provider | Mechanism | Current guarding |
|---|---|---|
| `TobiiStreamEngineProvider` | [`PollLoop`](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs) (`:275`) on an `IsBackground = true` thread (`:89`) | none |
| `MouseGazeProvider` | [`OnTick`](../../../src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs) (`:47`), a `System.Threading.Timer` callback on a pooled thread | `try { … } finally { re-arm }` — **no `catch`** |

`MouseGazeProvider`'s `finally` exists so *"a single faulty tick doesn't silently stop the provider forever"*, so a throwing subscriber was anticipated — but only to the extent of re-arming the timer. The exception still escapes, and an unhandled exception in a timer callback terminates the process just as `PollLoop` does.

### Fix 2 — guard both, reusing the existing recovery paths

This is defence in depth and remains worthwhile after Fix 1, because it also covers faults in `GazeEngine`'s own pipeline code, which no handler guard can catch.

- **`PollLoop`:** wrap the loop body. The guard must sit in the frame that calls `tobii_device_process_callbacks`, since that is where the exception re-enters managed code across the native boundary. On catch, log, set `IsConnected = false`, and fall into the existing `_stopSignal.Wait(backoffMs)` exponential backoff rather than spinning. **Do not hold `_lock` while backing off**, or `Stop()` cannot make progress.
- **`MouseGazeProvider.OnTick`:** add a `catch` that logs. The existing `finally` already re-arms, so recovery needs nothing more.

Both logs are rate-limited on the same basis as Fix 1. The mouse provider's catch logs at most once
per 30 s window (a `TickCount64` comparison in `OnTick`); Tobii's limiter is its own recovery path —
the catch falls into `_stopSignal.Wait(backoffMs)`, so a persistent fault logs at most once per
backoff interval (500 ms → 5 s).

One symptom to know when diagnosing: the Tobii catch deliberately reuses the device-error recovery,
so a persistent pipeline fault reports as a disconnect — track status shows "disconnected" and the
stream degrades to one attempt per backoff interval — even though the device itself is healthy.

## Problem 3 — Fix 1 introduces a stale-data bug

This one is subtle and must land **in the same change** as Fix 1.

`GazeQualityAggregator` reads `eyeCare.FatigueScore` live, for the current sample, relying on eye care's handler having already run — encoded only as a comment and the order of two `+=` statements ([App.xaml.cs:337](../../../src/EyeCtrl.App/App.xaml.cs), read at `:343`).

Today, if eye care throws, the multicast aborts and gaze quality — registered after it — never runs either. Both stop together, which is bad but not silently wrong.

**Per-handler isolation changes that.** After Fix 1, eye care throwing no longer stops gaze quality, so gaze quality runs and reads a *stale* `FatigueScore`. `FatigueMonitor.Score` is written only by the constructor, `OnSample`, and `Reset` — and `Reset` fires only on a settings toggle-off or a completed break. Nothing decays it with time, so once `OnSample` stops being reached the score freezes at whatever it held when the fault hit. That value then flows into every summary, is averaged by [`StruggleSignals`](../../../src/EyeCtrl.Core/Adaptation/StruggleSignals.cs), and leaves adaptation behaving as though the user were permanently as fatigued as at that instant — loosening tolerances and lengthening dwell indefinitely.

So Fix 1 converts "both handlers stop" into "one handler runs on frozen data and quietly distorts dwell". The second is worse, because it is invisible.

### Fix 3 — staleness detection

Eye care exposes its fatigue score together with the timestamp of the sample it last processed; `GazeQualityAggregator` substitutes `0` when that does not match the current sample.

`0` is not an arbitrary sentinel: [`GazeQualitySummary.FatigueScore`](../../../src/EyeCtrl.Core/Adaptation/GazeQualitySummary.cs) is already documented as *"0 when fatigue monitoring is disabled"*, and `FatigueMonitor` already produces `0` on that path. Substituting `0` on a mismatch therefore lands on an existing, documented contract rather than inventing one.

This also removes the ordering dependency outright — worst case is a one-frame-stale value on a 60-second window — so the two handlers become genuinely independent and registration order stops being load-bearing. The other input, `gazeEngine.LastFixationJitter`, is read from the engine itself and cannot fault.

## Problem 4 — the toolbar rect test uses uncalibrated coordinates

`ToolbarAutoHideController` subscribes to `GazeSampled` ([EventCoordinator.cs:604](../../../src/EyeCtrl.App/Services/EventCoordinator.cs)), which fires at `GazeEngine.cs:230` — *before* the calibration transform at `:233-237`. The controller then performs two tests that want **different coordinate spaces**:

| Test | Line | Wants | Gets |
|---|---|---|---|
| `_toolbarRect.Contains(sample.X, sample.Y)` | [:159](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs) | calibrated — an on-screen rect captured from the live window | raw ❌ |
| `IsPastDockedEdge(sample)` | [:214](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs) | raw — tests `s.X < -_offScreenMarginPx`, outside calibration's fitted domain | raw ✔ |

So the rect hit-test compares tracker-native coordinates against screen geometry. **It has been correct only by accident**: `:233` guards the transform with `is { } t`, and this machine's persisted calibration was being discarded at load, so `CurrentTransform` was null, raw ≡ calibrated, and the error was zero.

**That accident is ending, which is why this is now in scope.** The calibration work described under [Related work](#related-work) makes previously-rejected calibrations load successfully. Once a transform is live, the toolbar's gaze-sensitive region separates from the visible toolbar — the toolbar stops hiding when you look away from it, and stops staying visible when you look at it.

`CalibrationValidator.IsNearIdentity` does **not** bound the error usefully. Its limit is `sqrt(w² + h²) × MaxIdentityDisplacementScreenFraction` ([CalibrationValidator.cs:95-97](../../../src/EyeCtrl.Core/Calibration/CalibrationValidator.cs)) — 25% of the screen *diagonal*, which on 3840×2400 permits ~1,132 px of displacement at any reference point. Far more than enough to move a hit-test off an 80 px-tall toolbar.

### Fix 4 — inject a to-screen mapper, and use it for the rect test only

The controller already accepts an optional delegate for calibration state (`Func<bool>? calibrationActive = null`, [:61](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs)). Follow that precedent with a second one:

```csharp
public ToolbarAutoHideController(
    …,
    Func<bool>? calibrationActive = null,
    Func<PhysicalPoint, PhysicalPoint>? toScreen = null)   // null ⇒ identity
```

Inside `Feed`, map **only** in the `State.Visible` branch, where the rect test lives:

```csharp
case State.Visible:
    var probe = _toScreen is null
        ? new PhysicalPoint(sample.X, sample.Y)
        : _toScreen(new PhysicalPoint(sample.X, sample.Y));
    if (sample.IsValid && _toolbarRect.Contains(probe.X, probe.Y))
```

`IsPastDockedEdge(sample)` is **unchanged** and keeps raw coordinates. Off-screen gaze must stay tracker-native: the transform is fit exclusively to on-screen targets, so beyond the edge it is extrapolating outside its fitted domain, and `IsNearIdentity` constrains only the corners and centre — it says nothing about points outside them. Calibrating the edge test would not degrade the gesture, it would erase it: raw `x = -30` with a +200 px edge displacement becomes `+170`, comfortably on-screen, and summon never fires.

Wiring, at the existing construction site ([EventCoordinator.cs:579-588](../../../src/EyeCtrl.App/Services/EventCoordinator.cs)), which already has `_context.CalibrationService` in scope:

```csharp
toScreen: p => _context.CalibrationService.CurrentTransform?.Apply(p) ?? p
```

Four properties make this the right shape:

- **It is a no-op today.** With `CurrentTransform` null the delegate returns its input, so behaviour is bit-identical to current until a calibration actually loads. The fix cannot regress the present state — which matters for a control surface on a daily driver.
- **It picks up recalibration live.** The delegate reads `CurrentTransform` per call rather than caching, so a mid-session recalibration takes effect without re-wiring.
- **It introduces no new cross-thread exposure.** `GazeEngine` already reads `_calibrationService?.CurrentTransform` on the gaze thread at `:233`. `CompleteSession` and `Load` both publish a *fresh* `AffineTransform` instance rather than mutating the live one, so a reference read cannot observe half-updated coefficients.
- **Cost is one affine apply** (6 multiplies, 4 adds) per sample, and only while the toolbar is Visible — the `Hidden` branch does not touch it. This does duplicate the transform the pipeline already computes at `:233-237`; passing the corrected point through instead would require the payload change this spec deliberately does not make, and the arithmetic is far below noise on a 33 ms frame.

**Not addressed, deliberately:** `FatigueMonitor` also compares gaze against a screen-space setting (`_fixationRadiusPixels`) while reading raw coordinates. Its measure is dispersion around a centroid, which is translation-invariant, so only the transform's scale terms bias it — and those are implicitly bounded by `IsNearIdentity`. Switching it would alter a tuned scoring heuristic for a second-order correction. Recorded here rather than fixed.

## Explicitly not doing

Preserved in `32243f9` should any of it become warranted:

- `IGazeObserver` / `IGazeObserverHub` / `GazeSample` / `GazeSampleKind`
- declared consumption and its calibration diagnostic
- trip-and-disable, and the user-visible notice it needed
- a calibrated-coordinate delivery *in the pipeline* — Fix 4 maps at the one consumer that needs it instead
- the twelve-site migration and the four-step sequencing
- a slow-observer watchdog in any form — measured at 174.3 ns/sample always-on, exceeding the entire pipeline cost, with StdDev 76 ms on a 436 ms mean

Also unchanged: `CursorMoved`'s signature. It drops `IsValid` deliberately, which is precisely why `samples/OffScreenGazeProbe` cannot use it ([:74](../../../samples/OffScreenGazeProbe/MainWindow.xaml.cs)).

Also unchanged: the provider-level `GazePointReceived` fan-out stays a bare multicast (subscribers: `GazeEngine.OnGazePoint` and the calibration view-model's raw-gaze handler). Fix 1 guards one level down and Fix 2 contains the consequences, so a throwing raw-gaze subscriber still loses that sample and, on Tobii, surfaces as a device-error backoff — but it can no longer kill the process.

## Testing

xUnit v3 with NFluent (`Check.That(...)`, never `Assert.*`), centrally wired in `tests/Directory.Build.props`.

**Core.Tests:**

- **A throwing handler does not prevent later handlers from receiving the sample.** This is the regression the current code fails, and the single most important test here.
- A throwing handler does not prevent `CursorMoved` from firing, i.e. the cursor still moves.
- Registration order is preserved in delivery order.
- A handler that throws stays subscribed and receives the next sample.
- Fault logging is rate-limited: N faults inside the window produce one log entry carrying the occurrence count.
- A handler unsubscribing itself from inside its own callback does not disturb the in-progress iteration or skip a sibling.
- Subscribe and unsubscribe still work through `+=` / `-=`, including removing one of several identical-target handlers.
- No allocation per publication with handlers attached.

**Platform.Tests:** a throwing subscriber does not escape `PollLoop`, and does not escape `MouseGazeProvider.OnTick`; the mouse provider's timer still re-arms afterwards. The `PollLoop` half is verified by inspection + a local SDK-present build only: `TobiiStreamEngineProvider.cs` is entirely inside `#if TOBII_AVAILABLE`, which CI never defines (no SDK on the runner), so the block is not compiled there — pre-existing for the whole provider, but a future refactor can break it invisibly.

**App.Tests (headless, `Category!=UI`):**

- `GazeQualityAggregator` receives `0` when eye care's last-processed timestamp does not match the current sample — asserted by making eye care throw, not by reordering registration.
- **Fix 4, the case that matters:** with a non-identity `toScreen` mapper, gaze whose *raw* coordinates fall inside the toolbar rect but whose *mapped* coordinates fall outside does **not** count as on-toolbar, and vice versa. A test that only exercises an identity mapper would pass against the unfixed code and prove nothing.
- With `toScreen: null`, behaviour is unchanged from today — the existing `ToolbarAutoHideControllerTests` should pass untouched, which is the regression guard for the no-calibration path.
- `IsPastDockedEdge` still fires on raw off-screen coordinates **even when a non-identity mapper is supplied** — the mapper must not reach the edge test. This is the test that would catch someone "helpfully" applying the mapper to the whole `Feed`.

No calibration-loading tests are needed here — that work landed separately, with its own coverage.

Test output is German — per-test failures appear as `Fehler <FQN>`, so grepping for `Failed` reports false green.

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

```bash
dotnet test tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj
```

The second is required because that project is not in `EyeCtrl.slnx`. No `samples/` rebuild is needed this time — nothing they consume changes — which is one of the benefits of keeping the event surface intact.

## Sequencing

1. **Fix 1 + Fix 3 together.** Isolation without staleness detection would ship the stale-`FatigueScore` distortion described in Problem 3. They are one change.
2. **Fix 2**, both providers. Independent of step 1 and separately verifiable.
3. **Fix 4**, the toolbar mapper. Independent of the others, and a no-op until a calibration loads — so it can land before the calibration work without any behaviour change, which is the safer order. Landing it *after* leaves a window where the toolbar hit-test is live-wrong.
4. Add the delegate-array variant to `GazeObserverFanOutBenchmarks` and record it, to confirm Fix 1 is not a regression.

## Related work

- **Provider registry + Tobii extraction** (Spec B, approved but unwritten) — the `PhysicalTracker` capability replacing four provider-identity checks, and extracting `EyeCtrl.Providers.Tobii` so `EyeCtrl.Platform` carries no third-party SDK. This is the work that actually serves the original goals of supporting other trackers and build/test isolation, and it carries no hot-path risk.
- **Adaptation seam** (Spec C, deferred) — `IEffectiveSettings` in front of `EffectiveSettingsProvider`.
- **Rejected larger design** — commit `32243f9`.
- **Silently-discarded calibration — ALREADY FIXED; was Fix 4 in an earlier draft of this spec.** Work in the main checkout (uncommitted as of 2026-08-03) supersedes it and goes further than this spec had proposed. `CalibrationService.Load` now logs both the screen-size and the monitor mismatch naming *both* sides; adds a `LastLoadRejection` sentence for the UI, so an inert calibration is distinguishable from never having calibrated; and adds EDID-derived `MonitorHardwareId` matching (`src/EyeCtrl.Platform/Interop/MonitorIdentity.cs`, with tests) so display renumbering no longer discards a calibration at all — the stable-identity question this spec had explicitly deferred as needing its own decision. Its own comment records the symptom: *"this path used to `return` silently, which is how an inert calibration could survive weeks unnoticed."*

  **This is what pulled [Problem 4](#problem-4--the-toolbar-rect-test-uses-uncalibrated-coordinates) into scope.** Calibrations that previously failed to load will now load successfully, so raw and calibrated coordinates stop coinciding — which is exactly the condition under which the uncalibrated toolbar hit-test starts misbehaving. Fix 4 is a no-op until that happens, so the safe order is to land it first; see [Sequencing](#sequencing).

## Review dispositions (2026-08-05)

Findings from the PR #78 review, and what each one changed. The suite was 1742 headless before
this pass and 1750 after (+7 Core, +1 ViewModels; the two Platform tests were rewritten, not added).

| # | Finding | Disposition |
|---|---------|-------------|
| 1 | `PollLoop`'s catch logs an unrate-limited stack trace every backoff iteration — one per 5 s, indefinitely, on a managed fault that repeats per sample | **Fixed.** Routed through `HandlerFaultReporter`, keyed on the fault-site message. Matches the 30 s window Fix 1 uses and the `IsError` dedup already in that file. |
| 2 | `CalibrationViewModel` captures the monitor hardware id at DI-resolve time | **Fixed.** The parameter is now `Func<string>`, resolved in `CompleteSession`. |
| 3 | Fix 3 makes the eye-care/bridge subscription order load-bearing, and violating it fails silently | **Documented, not enforced.** Comments at both subscription sites and on `GazeQualityBridge`. Not testable without refactoring `App.OnStartup`; see below. |
| 4 | The reporter's dictionary has no fence across the mouse provider's thread handoff | **Fixed.** The reporter is now locked, which also let `MouseGazeProvider` drop its hand-rolled limiter. |
| 5 | Dictionary grows one entry per faulting subscription instance | **Fixed.** `Forget(Delegate)` from both remove accessors, but only once the *last* equal subscription is gone — otherwise an unsubscribe of a duplicate would reset a live handler's window. |
| 5 | `HandlerFaultReporter` is Core-internal, so Platform duplicated it | **Fixed.** Public, with a second `Report(context, exception)` overload for fixed fault sites. Both providers now share it. |
| 5 | `MouseGazeProviderFaultTests` sleep a fixed 200 ms against a 10 ms timer | **Fixed.** Bounded poll-until-condition; also removes the two xUnit1051 warnings. |
| 5 | `IsPastDockedEdge` still mixes coordinate spaces | **Documented, deliberately not changed.** Mapping off-screen gaze would extrapolate outside the transform's fitted domain. The residual — summon threshold and rect edge measured in different spaces — is now named in the comment rather than left implicit. |
| 5 | `_lastFaultLogTicks == 0` doubles as a sentinel and a legal clock value | **Gone.** The field no longer exists; the reporter keys on presence in a dictionary. |

### Why the PollLoop fault still triggers a reconnect

The review asked whether aliasing a managed fault to a device error is right, given it forces
`IsConnected = false` and a reconnect against hardware that is fine. Keeping it, with the reasoning
now in the comment: the exception unwound through Stream Engine's native frame *while it was
dispatching callbacks*, so the device's internal dispatch state is unknown, and continuing to poll
it is the less defensible option. `IsConnected = false` is also honest — no gaze is reaching the
pipeline either way. What was actually wrong was the logging volume, which is what changed.

### The ordering constraint in Problem 3 is documented, not tested

`GazeQualityBridge` is only correct if eye care's `GazeSampled` handler is registered first.
`GazeEngine` guarantees registration order (`HandlersRun_InRegistrationOrder`), but nothing pins the
order of the two subscriptions in `App.OnStartup`, which is not reachable from a headless test.
Making it structural — the bridge invoking eye care itself rather than trusting it already ran —
would remove the constraint, at the cost of reshaping the App wiring. Deferred, and left as the one
finding from this review with no mechanical guard behind it.
