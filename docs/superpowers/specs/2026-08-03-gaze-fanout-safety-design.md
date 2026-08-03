# Gaze fan-out safety — design

**Date:** 2026-08-03
**Branch:** `worktree-gaze-observer-hub`
**Scope:** three contained fixes to the gaze hot path. No new contract, no plug-in mechanism, no migration.

## Why this is small

An earlier draft of this work specified a full pluggable observer tier — `IGazeObserver`, a `GazeSample` carrying two coordinate spaces, declared consumption, a trip-and-disable fault policy, and a twelve-site migration. That version is preserved in commit `32243f9` and was deliberately rejected as oversized for the actual problem. Three reasons:

1. **The trip policy generated its own bug.** Trip-and-disable is what makes a tripped eye-care observer freeze `FatigueScore` into adaptation, which then needed a third mechanism (timestamp staleness) to solve. The measurement undercut the case for tripping anyway: a caught exception costs ~1 µs, about 0.003% of a 30 Hz frame. Read that as the cost of the *mechanism* — throw, unwind, catch — and not as the cost of a real fault, where the faulting handler's own partial work (WPF calls, Serilog I/O) would dominate. It bounds what isolation costs; it does not bound what a broken handler costs.
2. **Declared consumption was speculative.** Third-party extensions are explicitly not a goal, so the declaration bought one diagnostic and some documentation.
3. **The dual coordinate space is a permanent complexity tax** on every future author, incurred because one controller performs two kinds of test.

What survives is the part that stands on its own: the gaze fan-out can kill the process, and a latent stale-data bug sits next to it. Extension-point design waits until a second consumer actually demands it.

A fourth fix — logging a silently-discarded calibration — was in an earlier draft of this spec and has been **removed as already done**; see [Related work](#related-work).

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

**This design gives up the 2.7× speedup measured for the rejected hub.** That win came from `in`-passing a new sample type (8.4 ns/sample vs 24.1 by value), which required exactly the contract being cut. A delegate-array loop passing `GazePoint` by value should land near today's 29.0 ns/sample, plus roughly the +20% that per-handler `try/catch` cost in the benchmark — so approximately cost-neutral, possibly slightly better since it drops multicast dispatch.

That is a projection, not a measurement: `GazeObserverFanOutBenchmarks` measured *interface* dispatch, not delegate-array dispatch. Add a delegate-array variant to that class and record it before claiming a number. The absolute figures are tiny against a 33 ms frame either way; what matters is confirming no regression.

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

Both logs must be rate-limited on the same basis as Fix 1.

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

## Explicitly not doing

Preserved in `32243f9` should any of it become warranted:

- `IGazeObserver` / `IGazeObserverHub` / `GazeSample` / `GazeSampleKind`
- declared consumption and its calibration diagnostic
- trip-and-disable, and the user-visible notice it needed
- a calibrated-coordinate delivery, and moving the toolbar rect test onto it
- the twelve-site migration and the four-step sequencing
- a slow-observer watchdog in any form — measured at 174.3 ns/sample always-on, exceeding the entire pipeline cost, with StdDev 76 ms on a 436 ms mean

Also unchanged: `CursorMoved`'s signature. It drops `IsValid` deliberately, which is precisely why `samples/OffScreenGazeProbe` cannot use it ([:74](../../../samples/OffScreenGazeProbe/MainWindow.xaml.cs)).

### Known bug NOT fixed here: the toolbar rect test uses uncalibrated coordinates

Recorded so the finding is not lost with the rejected design. It is verified, it is not addressed by any of the four fixes, and it is **not** introduced by them.

`ToolbarAutoHideController` subscribes to `GazeSampled` ([EventCoordinator.cs:604](../../../src/EyeCtrl.App/Services/EventCoordinator.cs)), which fires at `GazeEngine.cs:230` — *before* the calibration transform at `:233-237`. The controller then performs two tests that want **different coordinate spaces**:

| Test | Line | Wants | Gets |
|---|---|---|---|
| `_toolbarRect.Contains(sample.X, sample.Y)` | [:159](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs) | calibrated — an on-screen rect from the live window | raw ❌ |
| `IsPastDockedEdge(sample)` | [:214](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs) | raw — tests `s.X < -_offScreenMarginPx`, outside calibration's fitted domain | raw ✔ |

So the rect hit-test compares tracker-native coordinates against screen geometry. **It has been correct only by accident**: `:233` guards the transform with `is { } t`, and this machine's persisted calibration was being discarded at load, so `CurrentTransform` was null, raw ≡ calibrated, and the error was zero. That accident is ending — the calibration fix described under [Related work](#related-work) makes previously-rejected calibrations load — and once a transform is live, the toolbar's gaze-sensitive region separates from the visible toolbar.

`CalibrationValidator.IsNearIdentity` does **not** bound this usefully. Its limit is `sqrt(w² + h²) × MaxIdentityDisplacementScreenFraction` ([CalibrationValidator.cs:95-97](../../../src/EyeCtrl.Core/Calibration/CalibrationValidator.cs)) — 25% of the screen *diagonal*, which on 3840×2400 permits ~1,132 px of displacement at any reference point. That is far more than enough to move a hit-test off an 80 px-tall toolbar.

Not fixed here because the fix needs a calibrated-coordinate delivery, which is the contract this spec deliberately does not build. Whoever addresses it should note that off-screen gaze must *keep* tracker-native coordinates — a transform fit exclusively to on-screen targets extrapolates badly beyond the edge, and `IsNearIdentity` constrains only the corners and centre, saying nothing outside them.

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

**Platform.Tests:** a throwing subscriber does not escape `PollLoop`, and does not escape `MouseGazeProvider.OnTick`; the mouse provider's timer still re-arms afterwards.

**App.Tests (headless, `Category!=UI`):** `GazeQualityAggregator` receives `0` when eye care's last-processed timestamp does not match the current sample — asserted by making eye care throw, not by reordering registration.

No calibration tests are needed here — that work landed separately, with its own coverage.

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
3. Add the delegate-array variant to `GazeObserverFanOutBenchmarks` and record it, to confirm Fix 1 is not a regression.

## Related work

- **Provider registry + Tobii extraction** (Spec B, approved but unwritten) — the `PhysicalTracker` capability replacing four provider-identity checks, and extracting `EyeCtrl.Providers.Tobii` so `EyeCtrl.Platform` carries no third-party SDK. This is the work that actually serves the original goals of supporting other trackers and build/test isolation, and it carries no hot-path risk.
- **Adaptation seam** (Spec C, deferred) — `IEffectiveSettings` in front of `EffectiveSettingsProvider`.
- **Rejected larger design** — commit `32243f9`.
- **Silently-discarded calibration — ALREADY FIXED; was Fix 4 in an earlier draft of this spec.** Work in the main checkout (uncommitted as of 2026-08-03) supersedes it and goes further than this spec had proposed. `CalibrationService.Load` now logs both the screen-size and the monitor mismatch naming *both* sides; adds a `LastLoadRejection` sentence for the UI, so an inert calibration is distinguishable from never having calibrated; and adds EDID-derived `MonitorHardwareId` matching (`src/EyeCtrl.Platform/Interop/MonitorIdentity.cs`, with tests) so display renumbering no longer discards a calibration at all — the stable-identity question this spec had explicitly deferred as needing its own decision. Its own comment records the symptom: *"this path used to `return` silently, which is how an inert calibration could survive weeks unnoticed."*

  **This raises the priority of the [known toolbar bug](#known-bug-not-fixed-here-the-toolbar-rect-test-uses-uncalibrated-coordinates).** Calibrations that previously failed to load will now load successfully, so raw and calibrated coordinates will stop coinciding — which is exactly the condition under which the uncalibrated rect test starts misbehaving. The "correct today only by accident" reasoning in that section describes a state that is about to end.
