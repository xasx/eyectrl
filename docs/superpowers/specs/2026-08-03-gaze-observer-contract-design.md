# Pluggable gaze pipeline — observer contract and migration

**Date:** 2026-08-03
**Branch:** `worktree-gaze-observer-hub`
**Scope:** the sample-observation tier of the gaze pipeline (tier 1). Tier 2 (`IStruggleTelemetrySink`) and tier 3 (`IEffectiveSettings`) are named where they interact but are not changed here.

## Contents

- [Why](#why)
- [The contract](#the-contract)
- [Migration](#migration)
- [Provider-thread hardening](#provider-thread-hardening)
- [`CursorMoved`](#cursormoved)
- [Testing strategy](#testing-strategy)
- [Rejected alternatives](#rejected-alternatives)
- [Out of scope](#out-of-scope)
- [Related work](#related-work)

## Why

`GazeEngine.GazeSampled` ([GazeEngine.cs:65](../../../src/EyeCtrl.Core/GazeEngine.cs), fired at `:230`) is already the extension point for a pluggable gaze pipeline. It was introduced for exactly one consumer — `7603ed8 feat(core): expose GazeEngine.GazeSampled raw-sample event for eye-care` — and has since accumulated six subscribers inside `src/`, each wired ad hoc with `+=`:

| Subscriber | Wired at | Reads | Unsubscribes |
|---|---|---|---|
| `HeartbeatPublisher` | [App.xaml.cs:253](../../../src/EyeCtrl.App/App.xaml.cs) | nothing (arrival tick only) | never (process lifetime) |
| eye care | [App.xaml.cs:309](../../../src/EyeCtrl.App/App.xaml.cs) | `X`, `Y`, `IsValid`, timestamp | never |
| `GazeQualityAggregator` | [App.xaml.cs:339](../../../src/EyeCtrl.App/App.xaml.cs) | `IsValid`, timestamp | never |
| `AdaptationCoordinator` | [AdaptationCoordinator.cs:193](../../../src/EyeCtrl.App/Services/AdaptationCoordinator.cs) | timestamp only | `:263` |
| toolbar auto-hide | [EventCoordinator.cs:604](../../../src/EyeCtrl.App/Services/EventCoordinator.cs) | `X`, `Y`, `IsValid` | `:1509` |
| quick-actions summon | [EventCoordinator.cs:718](../../../src/EyeCtrl.App/Services/EventCoordinator.cs) | `X`, `Y`, `IsValid` | `:1514` |

Being a bare multicast delegate rather than a designed contract causes three problems.

### 1. No fault isolation

`GazeSampled?.Invoke(gaze)` at `:230` is unguarded, and it runs *before* calibration (`:233`), filtering (`:238`) and `CursorMoved` (`:242`). One throwing subscriber therefore:

- **aborts the multicast chain**, so every subscriber registered after it silently stops receiving samples — registration order, which nothing declares, decides who survives;
- **unwinds `OnGazePoint` before the cursor moves**, so that sample produces no cursor motion and no dwell progress;
- **propagates through the native callback boundary into [`PollLoop`](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs)**, which has no `try/catch`, on an `IsBackground = true` thread — terminating the process.

The Supervisor respawns, so this is recoverable rather than terminal, but a *deterministic* fault becomes a respawn loop. These six subscribers do WPF work and Serilog I/O. `AdaptationRuntimeContainmentTests` already closes this hole on the dispatcher path — *"an escaping throw costs this user control of their machine"* — and the gaze thread has the same stake with less protection.

### 2. Nothing declares which coordinate space it consumes

The tap fires pre-calibration. That was right for eye care and incidental for the two controllers, which subscribed later (`e16a93a`, `99aa140`) and inherited the space by accident. `QuickActionsSummonController.Feed`'s parameter doc still claims *"coordinates in screen space"* ([QuickActionsSummonController.cs:100](../../../src/EyeCtrl.App/Services/QuickActionsSummonController.cs)) — which is not what it receives.

### 3. Observers are not independent, and nothing says so

`GazeQualityAggregator` reads `eyeCare.FatigueScore` live, for the current sample, relying on eye care's handler having already run. This is encoded solely as a comment and the order of two `+=` statements ([App.xaml.cs:337](../../../src/EyeCtrl.App/App.xaml.cs), read at `:343`).

## The contract

### Delivery

```csharp
/// One gaze sample, in both coordinate spaces the pipeline produces before filtering.
public readonly record struct GazeSample(
    GazePoint Raw,          // tracker-native; may fall outside the screen box
    PhysicalPoint Screen);  // calibration applied; equals Raw.X/Y when no transform is loaded

/// What an observer reads. Flags, because a consumer may need both spaces for one sample.
[Flags]
public enum GazeSampleKind
{
    /// Timestamp and validity only — no coordinates (heartbeat, adaptation clock, quality aggregation).
    None = 0,
    /// Tracker-native coordinates, including out-of-bounds values (off-screen gestures).
    Raw = 1,
    /// Calibration-corrected screen coordinates (on-screen hit tests).
    Screen = 2,
}

public interface IGazeObserver
{
    /// Stable identity, used in logs and in the user-facing notice when this observer is tripped.
    string Name { get; }

    /// What this observer reads. Enforced at registration; see "Declared consumption".
    GazeSampleKind Consumes { get; }

    /// Called on the gaze thread, once per sample. Must be cheap and non-blocking.
    void OnGazeSample(in GazeSample sample);
}
```

Validity and timestamp live on `Raw` — `sample.Raw.IsValid`, `sample.Raw.TimestampMicroseconds` — and are always populated regardless of declared consumption, which is why `GazeSampleKind.None` is meaningful rather than degenerate: three of the six current observers read no coordinates at all.

`in` is not cosmetic. Measured over 2.5 M samples with six observers, process-isolated: by-value delivery costs **24.1 ns/sample**, `in` costs **8.4 ns/sample**. It also matches the house convention already used for `IStruggleTelemetrySink.Report(in DwellOutcome)`.

### Why one sample carries both spaces

`ToolbarAutoHideController` needs both, in the same call:

| Test | Line | Space | Why |
|---|---|---|---|
| `_toolbarRect.Contains(sample.X, sample.Y)` | [:159](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs) | **Screen** | an on-screen rect from the live window — inside calibration's fitted domain, where the transform helps |
| `IsPastDockedEdge(sample)` | [:214](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs) | **Raw** | tests `s.X < -_offScreenMarginPx` — outside the fitted domain |

Off-screen gestures must stay tracker-native. They work only because the provider converts normalized coordinates with `_screenWidth * gazePoint.position.x` and **no clamp** ([TobiiStreamEngineProvider.cs:185](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs)), so out-of-bounds values reach the tap. A calibration transform is fit exclusively to on-screen targets, and `CalibrationValidator.IsNearIdentity` constrains only the four corners and the centre — points on or inside the boundary. It bounds nothing beyond the edge, which is exactly where these gestures live.

Since the available signal is small (a few tens of pixels before validity drops, and invalid samples are suppressed at [QuickActionsSummonController.cs:108](../../../src/EyeCtrl.App/Services/QuickActionsSummonController.cs) and [ToolbarAutoHideController.cs:216](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs)) while a "near identity" transform can still displace by hundreds of pixels, calibrating these tests would not degrade the gesture — it would erase it. Raw `x = -30` with a +200 px edge displacement becomes `+170`: comfortably on-screen, summon never fires.

This is independently corroborated in the tree. [`samples/OffScreenGazeProbe/MainWindow.xaml.cs:74`](../../../samples/OffScreenGazeProbe/MainWindow.xaml.cs) carries the comment *"CRITICAL: subscribe to GazeSampled (RAW, pre-filter, carries IsValid + physical px), NOT CursorMoved (filtered/clamped, loses the off-edge coordinate we're measuring)"* — a seventh consumer whose whole purpose is measuring off-edge coordinates, written by someone who had already reached the same conclusion.

### Declared consumption

`Consumes` is a **contract and validator, not a router.** The hub keeps one ordered list and delivers the same sample to everyone; the declaration does three things:

- makes the coordinate space a typed statement rather than a comment that can drift out of true;
- lets the hub warn when an observer declares `Screen` while `CalibrationService.CurrentTransform` is null — a diagnostic with immediate value, since a calibration can be discarded silently (see [Related work](#related-work));
- gives a future expensive derived signal (head pose, track status, jitter) a place to be skipped when nobody asks for it.

The warning must fire **once per registration, not per sample** — 30–90 Hz logging on the gaze thread inside the provider lock is the failure mode this whole design exists to prevent.

### Thread and cost rules

Three facts are true today, load-bearing, and undocumented. They belong in the interface doc, because a new observer author currently has to reverse-engineer all three from the call site.

1. **Observers run on the gaze thread, inside the provider's lock.** The callback fires at [TobiiStreamEngineProvider.cs:185](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs), reached from `tobii_device_process_callbacks` inside `lock (_lock)`. Blocking does not merely delay gaze — it also blocks `Stop()` and `TryGetLatestStatus`, which take the same lock. "Must not block" is a hard requirement, not style advice.
2. **Observers receive invalid samples.** `gaze.IsValid` is not consulted until `:233`, after the fan-out. This is deliberate — it is how eye care detects eyes-lost — so an observer will be called with `IsValid == false` and must handle it.
3. **Observers see pre-filter data.** The fan-out precedes `_gapInterpolator.Process` at `:238`. `GazeSampled_FiresWithRawPoint_BeforeInterpolation` pins this, though note it passes no `CalibrationService` ([GazeEngineSampledTests.cs:43](../../../tests/EyeCtrl.Core.Tests/EyeCare/GazeEngineSampledTests.cs)), so the pre-calibration half of the ordering is currently untested.

The existing subscribers already honour rule 1 by convention — the gaze-thread handlers are thin (`controller.Feed(...)`) and all WPF work is marshalled via `_dispatcher.BeginInvoke` ([GazeEngineAdapter.cs:99](../../../src/EyeCtrl.App/Services/GazeEngineAdapter.cs)). The contract states an existing discipline; it does not impose a new one.

### Fault policy

Part of the contract, because an observer must know it can be dropped.

Each call is individually guarded. On success the observer's consecutive-fault counter resets; on failure it increments, and after **5 consecutive faults** the observer is unregistered, logged once with the exception, and surfaced in a user-visible notice (`DriftNoticeService` sets the precedent). A tripped observer is not retried until re-registered.

Five is a starting value, not a derived one: large enough to ride out a transient (a momentary file lock, a dispatcher race during shutdown), small enough that at 30 Hz a hard-broken observer is gone within ~170 ms. *Consecutive* is the load-bearing word — a counter that never reset would trip an observer that fails once an hour.

The justification is **not** CPU cost. A caught exception measures ~1 µs and ~320 B — about 0.003% of a 30 Hz frame, so even an observer throwing on every sample would not stall the cursor arithmetically. Trip-and-disable is justified by Serilog I/O per sample on the gaze thread inside the provider lock, by Gen0 pressure, and by diagnosability.

Measured costs of the guard machinery, six observers, process-isolated:

| | ns/sample | vs. today |
|---|---:|---:|
| bare multicast event (today) | 29.0 | 1.00× |
| hub, `in`, no isolation | 8.4 | 0.29× |
| + per-observer `try/catch` | 10.1 | 0.35× |
| + consecutive-fault counter | 10.7 | 0.37× |
| + watchdog every 64th sample | 13.7 | 0.47× |
| + always-on stopwatch watchdog | 174.3 | 6.01× |
| hub with zero observers | 0.5 | 0.02× |

The fully-featured hub is **~2.7× faster than the mechanism it replaces** — fault isolation is a net saving, because an array loop with `in` beats a multicast delegate over six closures (five of the six real subscribers are lambdas). `try/catch` is not free (+20%) and neither is the fault counter (+6%), but both are negligible against a 33 ms frame.

**An always-on per-observer stopwatch watchdog is excluded.** At 174.3 ns/sample it alone exceeds the entire recorded pipeline cost, and its StdDev is 76 ms on a 436 ms mean — it is both the most expensive element and the least stable. If a slow-observer watchdog is wanted later, it must be sampled.

Source: `GazeObserverFanOutBenchmarks`, `GazeObserverFaultBenchmarks` (commit `f9c9085`), pinned CsProj toolchain, Intel Core Ultra 9 285HX.

### Observer independence

Observers must not depend on another observer having already run for the current sample. The one existing violation is resolved by **staleness detection rather than by ordering**: eye care exposes its fatigue score together with the timestamp of the sample it last processed, and `GazeQualityAggregator` substitutes `0` when that does not match the current sample.

This matters because of what trip-and-disable would otherwise do. `FatigueMonitor.Score` is assigned only inside `OnSample`, and the documented "0 when fatigue monitoring is disabled" semantics of [`GazeQualitySummary.FatigueScore`](../../../src/EyeCtrl.Core/Adaptation/GazeQualitySummary.cs) are produced *by eye care's handler running*. If eye care were tripped, the score would freeze at its value when the fault hit, flow into every summary via [App.xaml.cs:343](../../../src/EyeCtrl.App/App.xaml.cs), be averaged by [`StruggleSignals`](../../../src/EyeCtrl.Core/Adaptation/StruggleSignals.cs), and leave adaptation behaving as though the user were permanently as fatigued as at that instant — loosening tolerances and lengthening dwell indefinitely.

Substituting `0` on a timestamp mismatch collapses all of that: the ordering dependency disappears (worst case is a one-frame-stale value on a 60-second window), a tripped or disabled eye care degrades to exactly the already-documented contract, and the two observers become genuinely independent. The other input, `gazeEngine.LastFixationJitter`, is read from the engine itself, which cannot trip.

### Hub surface

```csharp
public interface IGazeObserverHub
{
    void Add(IGazeObserver observer);
    void Remove(IGazeObserver observer);
    void Publish(in GazeSample sample);   // called only by GazeEngine
}
```

Runtime `Add`/`Remove` is required, not a convenience: three of the six subscriptions are conditional and torn down at runtime — toolbar auto-hide subscribes only under a real tracker and unsubscribes at `:1509`, quick-actions likewise at `:1514`, and adaptation attaches in `Start()` gated on `AdaptationSettings.Enabled` and detaches at `:263`. A constructor-injected `IEnumerable<IGazeObserver>`, the textbook plug-in shape, cannot express this.

A `DelegateGazeObserver(string name, GazeSampleKind consumes, Action<GazeSample> handler)` adapter lets the existing lambda subscribers migrate by gaining a name, so a trip can report *which* feature died. Today five of the six are anonymous closures — only `AdaptationCoordinator` binds a named method group — so without the adapter a trip notice could say nothing more useful than "a subscriber failed".

**Storage is copy-on-write.** `Publish` reads a single volatile array reference and iterates it by index; `Add` and `Remove` build a replacement array under a lock and swap it in. This is required for correctness, not just speed: **a trip removes an observer from inside `Publish`'s own iteration**, and an observer may legitimately call `Remove` on itself. Copy-on-write makes both safe without a snapshot allocation per sample, keeps the hot path lock-free, and preserves the array iteration the benchmark measured. Mutation is rare (registration and teardown); publication is 30–90 Hz.

Order of delivery is registration order, and is part of the contract — even though no observer may *depend* on it, deterministic order keeps behaviour reproducible between runs and makes the log of a trip cascade readable.

## Migration

### Inventory

Twelve `GazeSampled +=` sites, in four trees:

| Tree | Count | Sites |
|---|---:|---|
| `src/` | 6 | the table in [Why](#why) |
| `tests/` | 2 | `GazeEngineSampledTests.cs:46`, `:66` |
| `benchmarks/` | 3 | `GazePipelineBenchmarks.cs:141`, `GazePipelineWithAdaptationBenchmarks.cs:114`, `GazePipelineWithFatigueBenchmarks.cs:87` |
| `samples/` | 1 | `OffScreenGazeProbe/MainWindow.xaml.cs:76` |

⚠️ **Neither `samples/` nor `EyeCtrl.Benchmarks.Tests` is in `EyeCtrl.slnx`.** A solution-wide build or test run will not catch breakage in either. Both must be built explicitly as part of verification, or this migration will appear green while two trees are broken.

### Where the types live

`GazeSample`, `GazeSampleKind`, `IGazeObserver`, `IGazeObserverHub` and `GazeObserverHub` go in `EyeCtrl.Core` alongside `IGazeProvider` — Core has no project references, and the hub needs none.

### How `GazeEngine` acquires the hub

An **optional constructor parameter** defaulting to a fresh internal hub, exposed as a property:

```csharp
public GazeEngine(
    IGazeProvider gazeProvider,
    IInputSimulator inputSimulator,
    EyeCtrlSettings settings,
    CalibrationService? calibrationService = null,
    EffectiveSettingsProvider? effectiveSettings = null,   // becomes IEffectiveSettings under Spec C
    IGazeObserverHub? observers = null)

public IGazeObserverHub Observers { get; }   // never null
```

This matters for churn: there are **31 `new GazeEngine(...)` call sites across 11 files**. An optional trailing parameter leaves all 31 compiling unchanged, and `Observers` is always usable, so a test can attach an observer without constructing a hub. `AppBootstrapper` registers the hub in DI and injects it, so App-side code resolves `IGazeObserverHub` directly rather than reaching through the engine.

`OnGazePoint` changes from `GazeSampled?.Invoke(gaze)` to building the sample and publishing it — after the calibration transform is computed, so `Screen` is available, but with publication still ordered before filtering:

```csharp
var corrected = gaze;
if (gaze.IsValid && _calibrationService?.CurrentTransform is { } t)
{
    var p = t.Apply(new PhysicalPoint(gaze.X, gaze.Y));
    corrected = gaze with { X = p.X, Y = p.Y };
}
_observers.Publish(new GazeSample(gaze, new PhysicalPoint(corrected.X, corrected.Y)));
var (filtered, isInGap) = _gapInterpolator.Process(corrected);
```

Note this **moves the calibration block above the fan-out**. The pipeline is unaffected — `corrected` is consumed by `Process` exactly as before — but observers now receive both spaces. Rules 2 and 3 of the thread contract are preserved: invalid samples still reach observers, and publication still precedes filtering.

### Per-site mapping

| Site | Observer name | `Consumes` | Notes |
|---|---|---|---|
| `App.xaml.cs:253` | `Heartbeat` | `None` | reads neither coordinate |
| `App.xaml.cs:309` | `EyeCare` | `Raw` | keeps its current space — see below |
| `App.xaml.cs:339` | `GazeQuality` | `None` | plus the staleness change |
| `AdaptationCoordinator.cs:193` | `AdaptationClock` | `None` | already a named method group |
| `EventCoordinator.cs:604` | `ToolbarAutoHide` | `Raw \| Screen` | rect test moves to `Screen`; edge test stays `Raw` |
| `EventCoordinator.cs:718` | `QuickActionsSummon` | `Raw` | corrects the false "screen space" doc |
| `OffScreenGazeProbe:76` | `OffScreenProbe` | `Raw` | sample app; must be rebuilt explicitly |

**Eye care deliberately keeps `Raw`.** `FatigueMonitor` compares dispersion against `_fixationRadiusPixels`, a screen-space setting, so `Screen` is arguably more correct — but its dispersion measure is translation-invariant, only the transform's scale terms would change anything, and switching it would alter a tuned scoring heuristic for no present benefit. Declaring `Raw` records the status quo honestly and leaves the question open rather than smuggling a behaviour change into a mechanical migration.

### Sequencing

One commit per step, each independently verifiable:

1. **Add the contract and hub to Core**, with tests. Nothing consumes it yet; `GazeSampled` untouched.
2. **Publish through the hub inside `GazeEngine`**, keeping `GazeSampled` as a hub-registered adapter observer so existing subscribers keep working. Both mechanisms exist, but only briefly, and every subscriber is already isolated because the event itself is now an observer.
3. **Migrate the six `src/` sites** to `hub.Add(...)`, plus the staleness change in eye care and gaze quality, plus the toolbar rect test moving to `Screen`.
4. **Migrate tests, benchmarks and samples**, then **delete `GazeSampled`**.

Step 2 is what makes this safe: it buys isolation for every subscriber before any of them is touched. Leaving the event in place permanently would be wrong — a second un-isolated path defeats the purpose — which is why step 4 deletes it rather than deprecating it.

## Provider-thread hardening

Defence in depth, independent of the hub. [`PollLoop`](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs) (`:275`) has no `try/catch` and runs on an `IsBackground = true` thread (`:89`), so **anything** escaping the managed callback terminates the process — not only observer faults, but a bug in `GazeEngine`'s own pipeline code, which the hub cannot guard.

The guard must sit in the frame that calls `tobii_device_process_callbacks`, because that is where the exception re-enters managed code from the native boundary. Two constraints:

- It must **not** swallow silently into a hot spin. Reuse the existing error path: log, mark `IsConnected = false`, and go through the same `_stopSignal.Wait(backoffMs)` exponential backoff the error branch already uses.
- It must **not** hold `_lock` while backing off, or `Stop()` cannot make progress.

**`MouseGazeProvider` has the same exposure by a different route**, verified: `OnTick` (`:47`) is a `System.Threading.Timer` callback wrapped in `try { … } finally { re-arm }` with **no `catch`**. The `finally` exists specifically so *"a single faulty tick doesn't silently stop the provider forever"* — so a throwing subscriber was anticipated, but only to the extent of re-arming the timer. The exception still escapes, and an unhandled exception in a timer callback on a thread-pool thread terminates the process exactly as `PollLoop` does. Both providers need the guard; only the mechanism differs (dedicated background thread vs. pooled timer callback).

## `CursorMoved`

`CursorMoved` ([:242](../../../src/EyeCtrl.Core/GazeEngine.cs)) has three direct subscribers in `src/`, each with a matching unsubscribe, plus one sample app:

| Subscriber | Subscribe | Unsubscribe |
|---|---|---|
| `EventCoordinator` | `:557` | `:1540` |
| `ExternalDwellService` | `:97` | `:346` |
| `GazeEngineAdapter` | `:42` | `:225` |
| `samples/StreamEngineGazeTracker` | `:79` | — |

**Isolate the invoke; do not build a registry.** These are the app's own output plumbing, not extension points — `GazeEngineAdapter` immediately marshals onto the dispatcher and re-broadcasts as `GazeCursorMovedMessage`, which the ViewModels consume through the messenger's own semantics. Isolation is still warranted, because `_dispatcher.BeginInvoke` can throw during shutdown and would escape onto the gaze thread exactly as an observer fault would.

**Its signature does not change.** `(PhysicalPoint filtered, long timestamp)` deliberately drops `IsValid`, and that shape is relied upon: it is precisely why the off-screen probe cannot use it. Widening it would invite consumers to use the filtered point for off-screen work, which is the confusion this spec exists to remove.

## Testing strategy

Conventions: xUnit v3 with NFluent (`Check.That(...)`, never `Assert.*`), centrally wired in `tests/Directory.Build.props`.

**Core.Tests — contract and hub:**

- fault isolation: a throwing observer does not prevent later observers from receiving the sample. *This is the regression the current code fails* and is the single most important test here.
- order preservation: delivery order equals registration order.
- trip: 5 consecutive faults unregisters and raises the notice exactly once; 4 faults followed by a success resets the counter and the observer stays registered.
- a tripped observer is never called again.
- **reentrancy**: an observer that throws its 5th fault is removed during `Publish`'s own iteration without corrupting it or skipping a sibling; an observer calling `Remove(self)` from inside its callback is safe.
- declaration validation: declaring `Screen` with no calibration loaded logs once per registration, never per sample.
- `GazeSample.Screen` equals `Raw.X/Y` when `CurrentTransform` is null; equals the transformed point when one is loaded — this is the pre-calibration ordering that `GazeEngineSampledTests` currently leaves untested.
- empty hub publishes without throwing and without allocating.

**App.Tests (headless, `Category!=UI`) — migration wiring:**

- all six observers are registered, with the names and `Consumes` values in the mapping table.
- the three conditional observers are added and removed on the same edges as today (real-tracker gating, `AdaptationSettings.Enabled`).
- staleness substitution: `GazeQualityAggregator` receives `0` when eye care's last-processed timestamp does not match the current sample — asserted by tripping eye care, not by reordering registration.

**Benchmarks as a regression guard:** `GazeObserverFanOutBenchmarks` and `GazeObserverFaultBenchmarks` stay. They are not asserted in CI, so they are documentation of cost rather than a gate; the numbers in this spec should be re-recorded if the hub's hot path changes.

**Verification commands.** Test output is German — per-test failures appear as `Fehler <FQN>`, so grepping for `Failed` reports false green.

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

```bash
dotnet test tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj
```

The second is required because that project is not in the solution. The `samples/` projects are likewise outside it and must be built explicitly before step 4 is considered done.

## Rejected alternatives

| Alternative | Why rejected |
|---|---|
| Constructor-injected `IEnumerable<IGazeObserver>` | Cannot express the three conditional subscriptions that are added and removed at runtime. |
| Isolation only, no registry (per-delegate `try/catch` over `GetInvocationList()`) | Fixes the fault-tolerance gap with the smallest diff, but gives no named extension point, and fault state keyed by delegate cannot name a lambda — five of six subscribers are anonymous, so a trip notice could not say what died. |
| Two taps (raw and calibrated) | Doubles fan-out cost and forces `ToolbarAutoHideController` to correlate two callbacks as belonging to one sample. |
| Move the two controllers to `CursorMoved` | It drops `IsValid`, which both controllers require — auto-hide has explicit blink handling at `:159`, and quick-actions gates on it at `:108`. |
| Declaration routes delivery | Needs a third list for the both-case and destroys the single registration order that the eye-care → gaze-quality dependency relies on, converting a documented hazard into a live bug. |
| Hub dependency graph with cascading trips | Most general, but makes the hub a dependency graph after deciding it should not be a router; staleness detection solves the only real case without it. |
| Middleware chain of transforming stages | Puts arbitrary code between the user's eyes and their cursor, multiplies the race surface that `GazeEngine`'s deferred-write discipline (`:22-42`, `:160-181`) exists to contain, and makes ordering an invisible semantic contract. Observation is pluggable; transformation is not. |
| Always-on slow-observer watchdog | 174.3 ns/sample, exceeding the entire pipeline cost, with StdDev 76 ms on a 436 ms mean. |

## Out of scope

- Whether tier 2 (`IStruggleTelemetrySink`) and tier 3 (`IEffectiveSettings`) adopt the same declaration model.
- Queued or off-thread delivery. The pattern is proven at tier 2 by `DispatcherTelemetrySink`, but of the six tier-1 observers only `FatigueMonitor` could tolerate it: heartbeat exists to detect a wedged poll loop, the adaptation clock is judged on staleness, the two controllers drive interaction timing, and gaze quality reads eye-care state synchronously. Not worth the machinery for one tolerant observer.
- Re-recording `GazePipelineBenchmarks`. The 134 ns/sample pipeline figure this spec compares the watchdog against predates 50 commits of `main`; the conclusion is robust against any plausible baseline, but the number is stale.
- The calibration diagnostics gap — tracked separately, see below.

## Related work

- **Provider registry + Tobii extraction** (Spec B, approved but unwritten). Providers are the pipeline's inlet, observers its outlet. Includes a `PhysicalTracker` capability replacing four provider-identity checks, and extracting `EyeCtrl.Providers.Tobii` so `EyeCtrl.Platform` carries no third-party SDK.
- **Adaptation seam** (Spec C, deferred). `IEffectiveSettings` in front of `EffectiveSettingsProvider` — the only place Core's pipeline names an adaptation implementation type. Referenced by the `GazeEngine` constructor above.
- **Silently-discarded calibration** (spawned separately). `CalibrationService.Load` returns at `:222-227` with no log when the monitor device name or screen size differs. Verified live: `calibration.json` records `\\.\DISPLAY14` while the machine now reports `\\.\DISPLAY1`, and five days of logs contain no occurrence of "calibrat". This is why `Screen` currently equals `Raw` on this machine, and why the hub's "declared `Screen`, no calibration loaded" warning is worth having.
