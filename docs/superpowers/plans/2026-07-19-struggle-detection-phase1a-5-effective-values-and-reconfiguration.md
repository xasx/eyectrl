# Struggle Detection — Phase 1a-⑤ Effective-Value Model & Live Reconfiguration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lay the final Phase-1a substrate: the master `AdaptationSettings` gate, the pure **effective-value model** (design §5 sub-band arithmetic, with the zero-delta ⇒ exact-baseline exit criterion), and the **live-reconfiguration primitives** the Phase-1b controller needs to apply changed values without disruption — dwell-time/geometry updates that preserve dwell state, and a **warm-started** filter rebuild (the R1 fix). Zero adaptation behaviour (nothing produces a non-zero delta until Phase 1b).

**Architecture:** `AdaptationSettings` (`Enabled` off by default) registers in `EyeCtrlSettings.EnumerateSubSettings()`. `EffectiveValue` is a pure calculator: `clamp(baseline + clamp(forgiving, band) + clamp(tightening, band), validRange)` — two independently-clamped sub-bands so a bounded tightening is never swallowed (the C1 fix), and any zero delta returns the exact baseline. `DwellDetector` gains state-preserving `UpdateTargetDwellTime`/`UpdateTargetGeometry` (mirroring `UpdateTargetCenter`). `GapInterpolator` gains `WarmStartFromCurrentState`, and `GazeEngine`'s filter-rebuild trigger extends to the smoothing params, seeding the fresh filter so a reconfiguration never causes a cursor jump.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core`; xUnit v3 + NFluent. **Core enforces CS1591.**

**Depends on:** 1a-①..④ (contracts, sink, telemetry). No dependency on a running controller — this is pure substrate.

**Scope boundary (deferred to Phase 1b, where deltas become non-zero):**
- The **stateful effective-settings provider** and **routing every consumer** (dwell targets, filter, fixation tolerance, cursor/overlay sizes) to read effective values. In Phase 1a every delta is 0, so routing would be a pure no-op refactor; it belongs with the `AdaptationController` that first produces non-zero deltas.
- **`FixationDetector` rebuild-on-tolerance-change** (needs event re-wiring of a `readonly` detector) — an apply-time concern paired with tolerance deltas.
- The fuller `AdaptationSettings` (per-knob bands, cue/caregiver toggles) — added in 1b when consumed.

The Phase-1a exit criterion is realized here as a **locked invariant**: `EffectiveValue` returns the exact baseline at zero delta (Task 2). Phase 1b's controller yields zero deltas when `AdaptationSettings.Enabled == false`, so the whole pipeline reduces to baseline — resting on this invariant.

---

## File Structure

**Create (Core):** `Settings/AdaptationSettings.cs`, `Settings/AdaptationPosture.cs`, `Adaptation/EffectiveValue.cs`.
**Modify (Core):** `Settings/EyeCtrlSettings.cs` (register `AdaptationSettings`), `Processing/DwellTarget.cs` (settable dwell/radius), `Processing/DwellDetector.cs` (update methods), `Processing/GapInterpolator.cs` (`WarmStartFromCurrentState`), `GazeEngine.cs` (extend rebuild trigger + warm-start).
**Test (create):** `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs`, `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveValueTests.cs`, `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs`, `tests/EyeCtrl.Core.Tests/Processing/GapInterpolatorWarmStartTests.cs`.

---

## Task 1: `AdaptationSettings` gate + registration

**Files:** Create `Settings/AdaptationSettings.cs`, `Settings/AdaptationPosture.cs`; Modify `Settings/EyeCtrlSettings.cs`; Test `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using System.Linq;
using EyeCtrl.Core.Settings;

public class AdaptationSettingsTests
{
    [Fact]
    public void Defaults_AreOptInAndConservative()
    {
        var s = new AdaptationSettings();
        Check.That(s.Enabled).IsFalse();
        Check.That(s.Posture).IsEqualTo(AdaptationPosture.Conservative);
        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void EyeCtrlSettings_EnumeratesAdaptation()
    {
        var names = new EyeCtrlSettings().EnumerateSubSettings().Select(x => x.Name).ToList();
        Check.That(names).Contains("Adaptation");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationSettingsTests"`
Expected: FAIL to compile — `AdaptationSettings` / `AdaptationPosture` do not exist.

- [ ] **Step 3: Create the settings + posture and register them**

`src/EyeCtrl.Core/Settings/AdaptationPosture.cs`:

```csharp
namespace EyeCtrl.Core.Settings;

/// <summary>How eagerly self-adaptation responds to detected struggle.</summary>
public enum AdaptationPosture
{
    /// <summary>Intervenes only on clear, sustained struggle; small steps, slow recovery. Default.</summary>
    Conservative,
    /// <summary>Moderate response to moderate struggle.</summary>
    Balanced,
    /// <summary>Reacts quickly and in larger steps at the first signs of struggle.</summary>
    Responsive,
}
```

`src/EyeCtrl.Core/Settings/AdaptationSettings.cs`:

```csharp
namespace EyeCtrl.Core.Settings;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Master switch + posture for struggle-detection self-adaptation. Off by default (opt-in v1).
/// Per-knob safety bands and feedback toggles are added in Phase 1b, when a controller consumes them.
/// </summary>
public partial class AdaptationSettings : ObservableObject, IValidatable
{
    /// <summary>Master switch. When false, adaptation is fully inert (effective settings == baseline).</summary>
    [ObservableProperty] private bool _enabled;

    /// <summary>How eagerly adaptation responds. Conservative by default.</summary>
    [ObservableProperty] private AdaptationPosture _posture = AdaptationPosture.Conservative;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate() => Array.Empty<string>();
}
```

In `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`, add the property next to the other sub-settings (after `_supervision`):

```csharp
    /// <summary>Struggle-detection self-adaptation settings (master switch + posture).</summary>
    [ObservableProperty] private AdaptationSettings _adaptation = new();
```

and add the enumeration entry at the end of `EnumerateSubSettings()` (after the `Supervision` yield):

```csharp
        yield return (nameof(Adaptation), Adaptation);
```

Finally, update the hardcoded count in `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs` — the enumeration now yields **19**. Rename the method and bump the assertion:

```csharp
    [Fact]
    public void EnumerateSubSettings_Yields19Entries_AllValidatable()
    {
        var subs = new EyeCtrlSettings().EnumerateSubSettings().ToList();

        Check.That(subs).HasSize(19);
        Check.That(subs.All(t => t.Settings is IValidatable)).IsTrue();
    }
```

(The reflection-based `_CoversEveryValidatableProperty` and `Validate_DefaultInstance_HasNoErrors` tests already pass with the addition — only this hardcoded count needs bumping.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationSettingsTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the enumeration drift-guard**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EyeCtrlSettingsEnumerationTests"`
Expected: PASS (all 3 tests) — the count test now asserts 19, and the reflection-based coverage test confirms the property is enumerated. (Coverage-test failure ⇒ property added without the `yield return` or vice-versa; count-test failure ⇒ the `HasSize` wasn't bumped to 19.)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Settings/AdaptationSettings.cs src/EyeCtrl.Core/Settings/AdaptationPosture.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs
git commit -m "feat(adaptation): add AdaptationSettings gate (opt-in) + enumeration registration"
```

---

## Task 2: `EffectiveValue` model + exit-criterion invariant

**Files:** Create `src/EyeCtrl.Core/Adaptation/EffectiveValue.cs`; Test `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveValueTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveValueTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class EffectiveValueTests
{
    // dwell-like knob: baseline 700 ms, forgiving = shorter (band [-300,0]), tightening = longer (band [0,500]).
    private static readonly (double Min, double Max) Forgiving = (-300, 0);
    private static readonly (double Min, double Max) Tightening = (0, 500);

    [Fact]
    public void ZeroDelta_ReturnsExactBaseline()
    {
        double v = EffectiveValue.Compute(700, 0, 0, Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(700.0);
    }

    [Fact]
    public void ZeroDelta_ReturnsBaseline_EvenWhenBaselineIsOutsideTheValidatedRange()
    {
        // A legacy/hand-edited baseline ABOVE validMax. With zero delta the value model must return
        // it exactly — the validated-range clamp only engages when delta != 0 (design §5). This is
        // the invariant that keeps "adaptation idle == the user's baseline" true even for bad settings.
        double v = EffectiveValue.Compute(2500, 0, 0, Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(2500.0);
    }

    [Fact]
    public void TighteningIsNotSwallowedByForgiving()
    {
        // Forgiving pinned to its floor (-300) AND tightening +500 both apply (independent sub-bands):
        // 700 - 300 + 500 = 900. A naive sum-then-one-sided-clamp would have lost the tightening.
        double v = EffectiveValue.Compute(700, forgivingDelta: -1000, tighteningDelta: 500,
            Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(900.0);
    }

    [Fact]
    public void ForgivingClampedToItsBand()
    {
        double v = EffectiveValue.Compute(700, forgivingDelta: -1000, tighteningDelta: 0,
            Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(400.0); // -1000 clamped to -300
    }

    [Fact]
    public void EffectiveClampedToValidatedRange()
    {
        double v = EffectiveValue.Compute(1900, forgivingDelta: 0, tighteningDelta: 500,
            Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(2000.0); // 1900 + 500 = 2400 clamped to 2000
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EffectiveValueTests"`
Expected: FAIL to compile — `EffectiveValue` does not exist.

- [ ] **Step 3: Create the calculator**

`src/EyeCtrl.Core/Adaptation/EffectiveValue.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using System;

/// <summary>
/// Pure implementation of the effective-value model (design §5): composes a baseline with a
/// forgiving delta and a separately-clamped tightening delta, then clamps the result to the
/// setting's validated range. The two deltas are clamped in independent sub-bands so a bounded
/// tightening (e.g. longer dwell for accidental activation) is never swallowed by an accumulated
/// forgiving offset. A zero delta always returns the exact baseline — the Phase-1a exit invariant
/// (with adaptation disabled the controller yields zero deltas, so the pipeline reduces to baseline).
/// </summary>
public static class EffectiveValue
{
    /// <summary>Computes the effective value for one knob.</summary>
    /// <param name="baseline">The user's saved baseline value for the knob.</param>
    /// <param name="forgivingDelta">Combined learned + momentary forgiving delta (signed toward "easier").</param>
    /// <param name="tighteningDelta">Momentary tightening delta (e.g. longer dwell); zero for forgiving-only knobs.</param>
    /// <param name="forgivingBand">Inclusive (Min, Max) the forgiving delta is clamped to.</param>
    /// <param name="tighteningBand">Inclusive (Min, Max) the tightening delta is clamped to.</param>
    /// <param name="validMin">Lower bound of the setting's validated range.</param>
    /// <param name="validMax">Upper bound of the setting's validated range.</param>
    /// <returns>The exact <paramref name="baseline"/> when the clamped delta is zero; otherwise
    /// <c>clamp(baseline + clampedForgiving + clampedTightening, [validMin, validMax])</c>.</returns>
    public static double Compute(
        double baseline,
        double forgivingDelta,
        double tighteningDelta,
        (double Min, double Max) forgivingBand,
        (double Min, double Max) tighteningBand,
        double validMin,
        double validMax)
    {
        double forgiving = Math.Clamp(forgivingDelta, forgivingBand.Min, forgivingBand.Max);
        double tightening = Math.Clamp(tighteningDelta, tighteningBand.Min, tighteningBand.Max);
        double delta = forgiving + tightening;
        // §5: the validated-range clamp engages only when delta != 0, so a zero delta always returns
        // the exact baseline — even a legacy/hand-edited baseline sitting outside the range.
        if (delta == 0) return baseline;
        return Math.Clamp(baseline + delta, validMin, validMax);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EffectiveValueTests"`
Expected: PASS (5 tests). The `ZeroDelta_*` tests are the **Phase-1a exit invariant** — treat them as locked; Phase 1b must not weaken them.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/EffectiveValue.cs tests/EyeCtrl.Core.Tests/Adaptation/EffectiveValueTests.cs
git commit -m "feat(adaptation): add EffectiveValue sub-band model (zero-delta == baseline invariant)"
```

---

## Task 3: State-preserving `DwellTarget` dwell/geometry updates

`DwellTarget.DwellTimeMicroseconds` and `Radius` are currently get-only. Make them `internal set` (mutable only within Core), and add `DwellDetector` update methods that mirror `UpdateTargetCenter` — changing the value without touching `EnteredTimestamp`/`IsGazeInside`, so an in-progress dwell continues rather than resetting (design C4).

**Files:** Modify `Processing/DwellTarget.cs`, `Processing/DwellDetector.cs`; Test `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class DwellTargetUpdateTests
{
    [Fact]
    public void UpdateTargetDwellTime_ReEvaluatesWithoutResettingProgress()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(100, 100), 0);        // enter (EnteredTimestamp = 0)
        d.Update(new PhysicalPoint(100, 100), 150_000);  // 150 ms of 300 ms — not yet activated
        Check.That(activated).IsFalse();

        d.UpdateTargetDwellTime("t", 100);               // shorten to 100 ms; must NOT reset the timer
        d.Update(new PhysicalPoint(100, 100), 160_000);  // elapsed 160 ms >= new 100 ms → activates

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_EnlargingMidDwell_PreservesProgress()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(100, 100), 0);        // enter
        d.Update(new PhysicalPoint(100, 100), 150_000);  // 50% progress
        d.UpdateTargetGeometry("t", 60);                 // enlarge radius; must preserve EnteredTimestamp
        d.Update(new PhysicalPoint(100, 100), 300_000);  // reaches 300 ms → activates (progress not reset)

        Check.That(activated).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetUpdateTests"`
Expected: FAIL to compile — `UpdateTargetDwellTime`/`UpdateTargetGeometry` do not exist.

- [ ] **Step 3: Make the fields mutable within Core**

In `src/EyeCtrl.Core/Processing/DwellTarget.cs`, change the two get-only auto-properties to `internal set`:

```csharp
    /// <summary>Hit-test radius in physical pixels (used when <see cref="Bounds"/> is null).</summary>
    public double Radius { get; }
    /// <summary>Required dwell time in microseconds for activation.</summary>
    public long DwellTimeMicroseconds { get; }
```

to:

```csharp
    /// <summary>Hit-test radius in physical pixels (used when <see cref="Bounds"/> is null).</summary>
    public double Radius { get; internal set; }
    /// <summary>Required dwell time in microseconds for activation.</summary>
    public long DwellTimeMicroseconds { get; internal set; }
```

- [ ] **Step 4: Add the state-preserving update methods**

In `src/EyeCtrl.Core/Processing/DwellDetector.cs`, add after `UpdateTargetCenter`:

```csharp
    /// <summary>
    /// Updates a target's required dwell time without resetting accumulated dwell state. The next
    /// <see cref="Update"/> re-evaluates completion against the new time using the existing
    /// <c>EnteredTimestamp</c> (design C4).
    /// </summary>
    /// <param name="id">Target ID.</param>
    /// <param name="dwellTimeMs">New required dwell time in milliseconds.</param>
    public void UpdateTargetDwellTime(string id, int dwellTimeMs)
    {
        if (_targets.TryGetValue(id, out var target))
            target.DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>
    /// Updates a target's hit radius without resetting accumulated dwell state. Enlarging mid-dwell
    /// keeps progress (gaze already inside stays inside); shrinking that leaves gaze outside ends the
    /// dwell on the next <see cref="Update"/> as a normal gaze-left (design C4).
    /// </summary>
    /// <param name="id">Target ID.</param>
    /// <param name="radius">New hit radius in physical pixels.</param>
    public void UpdateTargetGeometry(string id, double radius)
    {
        if (_targets.TryGetValue(id, out var target))
            target.Radius = radius;
    }
```

- [ ] **Step 5: Run + confirm no regressions**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetUpdateTests"`
Expected: PASS (2 tests).
Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetector"`
Expected: PASS (existing dwell tests unaffected — the new methods aren't called by existing paths).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Processing/DwellTarget.cs src/EyeCtrl.Core/Processing/DwellDetector.cs tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs
git commit -m "feat(adaptation): add state-preserving dwell-time/geometry target updates"
```

---

## Task 4: Warm-started filter reconfiguration (R1)

Today `GazeEngine` rebuilds the interpolator only on `FilterMode`/`MaxGapInterpolationMs`, and the new interpolator's filter is cold (`CopyStateFrom` carries only last position/timestamp). This task (a) extends the trigger to the smoothing params and (b) seeds the fresh filter from the carried-over position so the first post-rebuild sample is smoothed rather than snapping to the raw sample — no cursor jump. (It also fixes a latent gap: manual smoothing-param changes previously didn't take effect until the next mode switch.)

**Files:** Modify `Processing/GapInterpolator.cs`, `GazeEngine.cs`; Test `tests/EyeCtrl.Core.Tests/Processing/GapInterpolatorWarmStartTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Processing/GapInterpolatorWarmStartTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class GapInterpolatorWarmStartTests
{
    // Covers both filter kinds the GazeEngine rebuild can produce (design R1: both return the seed
    // unchanged on first call). A cold rebuild emits the raw sample; a warm-started rebuild smooths
    // from the carried-over seed — below the raw, and never past the seed (bounded one-sample lag).
    private static void AssertWarmStartSmoothsFirstSample(Func<IGazeFilter> makeFilter)
    {
        // Establish a filtered position near (500,500) on the "old" interpolator.
        var old = new GapInterpolator(makeFilter(), maxGapMs: 150);
        long t = 0;
        for (int i = 0; i < 10; i++) { old.Process(new GazePoint(500, 500, t, true)); t += 33_000; }

        // Rebuild WITH warm-start, then feed a sample at x=600.
        var warm = new GapInterpolator(makeFilter(), maxGapMs: 150);
        warm.CopyStateFrom(old);
        warm.WarmStartFromCurrentState();
        double warmX = warm.Process(new GazePoint(600, 500, t, true)).Position.X;

        // Rebuild WITHOUT warm-start (cold filter): first Process returns the raw sample.
        var cold = new GapInterpolator(makeFilter(), maxGapMs: 150);
        cold.CopyStateFrom(old);
        double coldX = cold.Process(new GazePoint(600, 500, t, true)).Position.X;

        Check.That(coldX).IsEqualTo(600.0);   // cold: emits the raw point (the jump)
        Check.That(warmX < 600.0).IsTrue();    // warm: smoothed below the raw...
        Check.That(warmX >= 500.0).IsTrue();   // ...and no overshoot past the seed (bounded lag)
    }

    [Fact]
    public void WarmStart_SmoothsFirstSample_OneEuro()
        => AssertWarmStartSmoothsFirstSample(() => new OneEuroFilter());

    [Fact]
    public void WarmStart_SmoothsFirstSample_Ema()
        => AssertWarmStartSmoothsFirstSample(() => new SmoothedFilter(0.5));

    [Fact]
    public void WarmStart_IsNoOp_WithNoPriorState()
    {
        var i = new GapInterpolator(new OneEuroFilter(), maxGapMs: 150);
        i.WarmStartFromCurrentState(); // no established position yet → must not throw
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GapInterpolatorWarmStartTests"`
Expected: FAIL to compile — `WarmStartFromCurrentState` does not exist.

- [ ] **Step 3: Add `WarmStartFromCurrentState`**

In `src/EyeCtrl.Core/Processing/GapInterpolator.cs`, add `using EyeCtrl.Core.Models;` if not present, and add after `CopyStateFrom`:

```csharp
    /// <summary>
    /// Primes the (freshly constructed) filter with the current carried-over position so the first
    /// real sample after a rebuild is smoothed relative to it rather than snapping to the raw sample
    /// (design R1 — no cursor jump on reconfiguration). No-op if no valid position is known yet.
    /// </summary>
    public void WarmStartFromCurrentState()
    {
        if (_lastValidTimestamp < 0) return;
        _filter.Filter(new GazePoint(_lastValidPosition.X, _lastValidPosition.Y, _lastValidTimestamp, true));
    }
```

- [ ] **Step 4: Extend the rebuild trigger and warm-start in `GazeEngine`**

In `src/EyeCtrl.Core/GazeEngine.cs`, change the cursor-settings handler:

```csharp
        _cursorSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(CursorSettings.FilterMode)
                               or nameof(CursorSettings.MaxGapInterpolationMs))
            {
                var newInterpolator = new GapInterpolator(
                    GazeFilterFactory.Create(_settings.Cursor),
                    _settings.Cursor.MaxGapInterpolationMs);
                newInterpolator.CopyStateFrom(_gapInterpolator);
                _gapInterpolator = newInterpolator;
            }
        };
```

to (add the three smoothing params + warm-start):

```csharp
        _cursorSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(CursorSettings.FilterMode)
                               or nameof(CursorSettings.MaxGapInterpolationMs)
                               or nameof(CursorSettings.OneEuroMinCutoff)
                               or nameof(CursorSettings.OneEuroBeta)
                               or nameof(CursorSettings.SmoothingFactor))
            {
                var newInterpolator = new GapInterpolator(
                    GazeFilterFactory.Create(_settings.Cursor),
                    _settings.Cursor.MaxGapInterpolationMs);
                newInterpolator.CopyStateFrom(_gapInterpolator);
                newInterpolator.WarmStartFromCurrentState(); // R1: seed the fresh filter — no cursor jump
                _gapInterpolator = newInterpolator;
            }
        };
```

- [ ] **Step 5: Run + confirm no regressions**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GapInterpolatorWarmStartTests"`
Expected: PASS (3 tests — One-Euro + EMA warm-start, plus the no-prior-state no-op).
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS (full Core suite — `GapInterpolator`/`GazeEngine` existing tests unaffected; the rebuild now also warm-starts, which only removes the cold first sample).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Processing/GapInterpolator.cs src/EyeCtrl.Core/GazeEngine.cs tests/EyeCtrl.Core.Tests/Processing/GapInterpolatorWarmStartTests.cs
git commit -m "feat(adaptation): warm-start filter on reconfiguration; extend trigger to smoothing params"
```

---

## Self-Review

**Spec coverage (Phase-1a substrate completion):**
- §5 value model — `EffectiveValue`: sub-band composition, tightening not swallowed (C1), **delta-gated** clamp to the validated range so **zero delta ⇒ exact baseline even for an out-of-*range* (legacy/hand-edited) baseline** — the locked exit invariant. ✓
- §5/§8 `AdaptationSettings.Enabled` gate — the master switch exists and defaults off; Phase 1b's controller yields zero deltas when off, and `EffectiveValue` guarantees zero deltas → baseline. ✓
- §5 live-update primitives — `UpdateTargetDwellTime`/`UpdateTargetGeometry` (state-preserving, C4), warm-started filter rebuild (R1) extended to the smoothing params. ✓
- Registration convention — `AdaptationSettings` added as an `[ObservableProperty]` **and** yielded from `EnumerateSubSettings()`; the drift-guard test enforces both. ✓
- **Deferred to Phase 1b (documented, not gaps):** the stateful provider + routing consumers to read effective values (a no-op refactor at zero delta); `FixationDetector` tolerance-rebuild (readonly-field event re-wiring); fuller `AdaptationSettings` (per-knob bands, cue/caregiver toggles); cursor/overlay-size reconfiguration.

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `EffectiveValue.Compute` signature matches its tests; `AdaptationSettings`/`AdaptationPosture` match the test + the `EnumerateSubSettings` entry (`nameof(Adaptation)`); `UpdateTargetDwellTime(string,int)`/`UpdateTargetGeometry(string,double)` mirror `UpdateTargetCenter(string,PhysicalPoint)` and use `MathConstants.MicrosecondsPerMillisecond`; `DwellTarget.Radius`/`DwellTimeMicroseconds` become `internal set` (DwellDetector is same-assembly); `WarmStartFromCurrentState` constructs `GazePoint(X, Y, TimestampMicroseconds, IsValid)` in the verified positional order.

**Behaviour-change note (honest):** the feature stays inert (no auto-deltas), but extending the rebuild trigger means a **manual** smoothing-param change now takes effect immediately (previously it was silently ignored until the next `FilterMode`/gap change) — a latent-bug fix, and warm-started so even that path has no cursor jump. `DwellTarget`'s `internal set` and the new update methods are unused by existing code paths, so current dwell behaviour is unchanged.

**What this unblocks:** with the substrate complete (telemetry in from 1a-①..④, effective-value model + reconfiguration primitives here), **Phase 1b** can add the `StruggleMonitor` (consume the buffered outcomes/summaries) and the `AdaptationController` (produce bounded, typed per-knob deltas → feed `EffectiveValue` → apply via the reconfiguration primitives and consumer routing). That is where adaptation first changes behaviour, behind the `AdaptationSettings.Enabled` gate.
