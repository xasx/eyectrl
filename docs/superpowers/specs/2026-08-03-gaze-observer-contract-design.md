# Gaze-observer contract — design note

**Date:** 2026-08-03
**Branch:** `worktree-gaze-observer-hub`
**Status:** contract only. Migration, `PollLoop` hardening, `CursorMoved` isolation and testing strategy are deliberately deferred — see [Out of scope](#out-of-scope).

## Why

`GazeEngine.GazeSampled` ([GazeEngine.cs:65](../../../src/EyeCtrl.Core/GazeEngine.cs), fired at `:230`) is already the extension point for a pluggable gaze pipeline. It was introduced for one consumer — commit `7603ed8 feat(core): expose GazeEngine.GazeSampled raw-sample event for eye-care` — and has since accumulated six subscribers, each wired ad hoc with `+=`:

| Subscriber | Wired at | Reads |
|---|---|---|
| `HeartbeatPublisher` | [App.xaml.cs:253](../../../src/EyeCtrl.App/App.xaml.cs) | nothing (arrival tick only) |
| eye care | [App.xaml.cs:309](../../../src/EyeCtrl.App/App.xaml.cs) | `X`, `Y`, `IsValid`, timestamp |
| `GazeQualityAggregator` | [App.xaml.cs:339](../../../src/EyeCtrl.App/App.xaml.cs) | `IsValid`, timestamp |
| `AdaptationCoordinator` | [AdaptationCoordinator.cs:193](../../../src/EyeCtrl.App/Services/AdaptationCoordinator.cs) | timestamp only |
| toolbar auto-hide | [EventCoordinator.cs:604](../../../src/EyeCtrl.App/Services/EventCoordinator.cs) | `X`, `Y`, `IsValid` |
| quick-actions summon | [EventCoordinator.cs:718](../../../src/EyeCtrl.App/Services/EventCoordinator.cs) | `X`, `Y`, `IsValid` |

Three problems follow from it being a bare multicast delegate rather than a designed contract.

**1. No fault isolation.** `GazeSampled?.Invoke(gaze)` at `:230` is unguarded, and it runs *before* calibration (`:233`), filtering (`:238`) and `CursorMoved` (`:242`). One throwing subscriber therefore (a) aborts the multicast chain, so every subscriber registered after it silently stops receiving samples — registration order, which nothing declares, decides who survives; (b) unwinds `OnGazePoint` before the cursor moves; and (c) propagates through the native callback boundary into [`PollLoop`](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs), which has no `try/catch`, on an `IsBackground = true` thread — terminating the process. The Supervisor respawns, so this is recoverable rather than terminal, but a deterministic fault becomes a respawn loop. `AdaptationRuntimeContainmentTests` already closes this hole on the dispatcher path; the gaze thread has the same stake and less protection.

**2. Nothing declares which coordinate space it consumes.** The tap fires pre-calibration. That was right for eye care and incidental for the two controllers, which subscribed later (`e16a93a`, `99aa140`) and inherited the space by accident. `QuickActionsSummonController.Feed`'s parameter doc still claims *"coordinates in screen space"* ([QuickActionsSummonController.cs:100](../../../src/EyeCtrl.App/Services/QuickActionsSummonController.cs)) — which is not what it receives.

**3. Observers are not independent, and nothing says so.** `GazeQualityAggregator` reads `eyeCare.FatigueScore` live, for the current sample, relying on eye care's handler having already run — encoded solely as a comment and the order of two `+=` statements ([App.xaml.cs:337](../../../src/EyeCtrl.App/App.xaml.cs), read at `:343`).

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
```

Validity and timestamp live on `Raw` — `sample.Raw.IsValid`, `sample.Raw.TimestampMicroseconds` — and are always populated regardless of declared consumption, which is why `GazeSampleKind.None` is meaningful rather than degenerate: three of the six current observers read no coordinates at all.

```csharp
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

`in` is not cosmetic. Measured over 2.5 M samples with six observers, process-isolated: by-value delivery costs **24.1 ns/sample**, `in` costs **8.4 ns/sample**. It also matches the existing house convention for `IStruggleTelemetrySink.Report(in DwellOutcome)`.

### Why one sample carries both spaces

`ToolbarAutoHideController` needs both, in the same call:

| Test | Line | Space | Why |
|---|---|---|---|
| `_toolbarRect.Contains(sample.X, sample.Y)` | [:159](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs) | **Screen** | an on-screen rect from the live window — inside calibration's fitted domain, where the transform helps |
| `IsPastDockedEdge(sample)` | [:214](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs) | **Raw** | tests `s.X < -_offScreenMarginPx` — outside the fitted domain |

Off-screen gestures must stay tracker-native. They work only because the provider converts normalized coordinates with `_screenWidth * gazePoint.position.x` and **no clamp** ([TobiiStreamEngineProvider.cs:185](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs)), so out-of-bounds values reach the tap. A calibration transform is fit exclusively to on-screen targets, and `CalibrationValidator.IsNearIdentity` constrains only the four corners and the centre — points on or inside the boundary. It bounds nothing beyond the edge, which is exactly where these gestures live. Since the available signal is small (a few tens of pixels before validity drops, and invalid samples are suppressed at [QuickActionsSummonController.cs:108](../../../src/EyeCtrl.App/Services/QuickActionsSummonController.cs) and [ToolbarAutoHideController.cs:216](../../../src/EyeCtrl.App/Services/ToolbarAutoHideController.cs)) while a "near identity" transform can still displace by hundreds of pixels, calibrating them would not degrade the gesture — it would erase it. Raw `x = -30` with a +200 px edge displacement becomes `+170`: comfortably on-screen, summon never fires.

Two taps were rejected: they would double fan-out cost and force that controller to correlate two callbacks as belonging to one sample. Widening the struct from 32 to 48 bytes costs essentially nothing when passed by `in`.

### Declared consumption

`Consumes` is a **contract and validator, not a router.** The hub keeps one ordered list and delivers the same sample to everyone; the declaration does three things:

- makes the coordinate space a typed statement rather than a comment that can drift out of true;
- lets the hub warn when an observer declares `Screen` while `CalibrationService.CurrentTransform` is null — a diagnostic with immediate value, since a calibration can be discarded silently (see [Related](#related));
- gives a future expensive derived signal (head pose, track status, jitter) a place to be skipped when nobody asks for it.

Routing delivery by declaration was rejected: it needs a third list for the both-case, and per-kind lists destroy the single global registration order that the eye-care → gaze-quality dependency currently relies on — turning a documented hazard into a live bug.

### Thread and cost rules

Three facts are true today, load-bearing, and undocumented. They belong in the interface doc, because a new observer author currently has to reverse-engineer all three from the call site.

1. **Observers run on the gaze thread, inside the provider's lock.** The callback fires at [TobiiStreamEngineProvider.cs:185](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs), reached from `tobii_device_process_callbacks` inside `lock (_lock)`. Blocking does not merely delay gaze — it also blocks `Stop()` and `TryGetLatestStatus`, which take the same lock. "Must not block" is a hard requirement.
2. **Observers receive invalid samples.** `gaze.IsValid` is not consulted until `:233`, after the fan-out. This is deliberate — it is how eye care detects eyes-lost — so an observer will be called with `IsValid == false` and must handle it.
3. **Observers see pre-filter data.** The fan-out precedes `_gapInterpolator.Process` at `:238`. `GazeSampled_FiresWithRawPoint_BeforeInterpolation` pins this, though note it passes no `CalibrationService` ([GazeEngineSampledTests.cs:43](../../../tests/EyeCtrl.Core.Tests/EyeCare/GazeEngineSampledTests.cs)), so the pre-calibration half of the ordering is currently untested.

### Fault policy

Part of the contract, because an observer must know it can be dropped.

Each call is individually guarded. On success the observer's consecutive-fault counter resets; on failure it increments, and after **5 consecutive faults** the observer is unregistered, logged once with the exception, and surfaced in a user-visible notice (`DriftNoticeService` sets the precedent). An observer that has been tripped is not retried until re-registered.

Five is a starting value, not a derived one: it is large enough to ride out a transient (a momentary file lock, a dispatcher race during shutdown) and small enough that at 30 Hz a hard-broken observer is gone within ~170 ms. *Consecutive* is the load-bearing word — a counter that never reset would trip an observer that fails once an hour.

The justification is *not* CPU cost. A caught exception measures ~1 µs and ~320 B — about 0.003% of a 30 Hz frame, so even an observer throwing on every sample would not stall the cursor arithmetically. Trip-and-disable is justified by Serilog I/O per sample on the gaze thread inside the provider lock, by Gen0 pressure, and by diagnosability.

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

**An always-on per-observer stopwatch watchdog is excluded from the design.** At 174.3 ns/sample it alone exceeds the entire recorded pipeline cost, and its StdDev is 76 ms on a 436 ms mean. If a slow-observer watchdog is wanted, it must be sampled.

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

Runtime `Add`/`Remove` is required, not a convenience: three of the six subscriptions are conditional and torn down at runtime — toolbar auto-hide subscribes only under a real tracker and unsubscribes on teardown, quick-actions likewise, and adaptation attaches in `Start()` gated on `AdaptationSettings.Enabled`. A constructor-injected `IEnumerable<IGazeObserver>`, the textbook plug-in shape, cannot express this.

A `DelegateGazeObserver(string name, GazeSampleKind consumes, Action<GazeSample> handler)` adapter lets the existing lambda subscribers migrate by gaining a name, so a trip can report *which* feature died. Today five of the six are anonymous closures — only `AdaptationCoordinator` binds a named method group — so without the adapter a trip notice could say nothing more useful than "a subscriber failed".

## Out of scope

Deferred to a second pass, once this contract is agreed:

- migrating the six `+=` sites to `hub.Add(...)`
- the `PollLoop` guard ([TobiiStreamEngineProvider.cs:275](../../../src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs) has no `try/catch` on a background thread) as defence in depth
- isolating `CursorMoved`'s invoke — three subscribers on the same thread, but it is the app's own output path, not an extension point, so no registry
- testing strategy: trip behaviour, fault isolation, order preservation, staleness substitution, and a hub benchmark as a regression guard
- whether tier 2 (`IStruggleTelemetrySink`) and tier 3 (`IEffectiveSettings`) adopt the same declaration model

## Related

- **Provider registry + Tobii extraction** (Spec B, approved but unwritten): providers are the pipeline's inlet, observers its outlet. Includes a `PhysicalTracker` capability replacing four provider-identity checks.
- **Adaptation seam** (Spec C, deferred): `IEffectiveSettings` in front of `EffectiveSettingsProvider`; the only place Core's pipeline names an adaptation implementation type.
- **Silently-discarded calibration** (spawned separately): `CalibrationService.Load` returns at `:222-227` with no log when the monitor device name or screen size differs. Verified live — `calibration.json` records `\\.\DISPLAY14` while the machine now reports `\\.\DISPLAY1`, and five days of logs contain no occurrence of "calibrat". This is why `Screen` currently equals `Raw` on this machine, and why the hub's "declared `Screen`, no calibration loaded" warning is worth having.
