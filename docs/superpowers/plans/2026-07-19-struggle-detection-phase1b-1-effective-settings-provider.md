# Struggle Detection — Phase 1b-① Effective-Settings Provider & Tolerance Reconfiguration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce the stateful `EffectiveSettingsProvider` — the composition layer the Phase-1b `AdaptationController` will push bounded per-knob deltas into — and route the **fixation-tolerance** knob through it in `GazeEngine`, rebuilding the (readonly-radius) `FixationDetector` on change. This also fixes a **latent bug**: `GazeEngine` subscribes only to `CursorSettings`, so manual `DwellSettings` tolerance edits never reach the live detector today. **Zero behaviour change** while `AdaptationSettings.Enabled == false` (the locked exit invariant: effective == baseline); the sole intended change is that manual tolerance edits now propagate.

**Architecture:** `EffectiveSettingsProvider` wraps the pure `EffectiveValue` calculator (from plan 1a-⑤) with a per-`(knob, context)` momentary-delta store, the `AdaptationSettings.Enabled` gate, and a `Changed` event. `GazeEngine` takes the provider (optional), reads the **effective** fixation radius from it, and rebuilds `_fixationDetector` (now `volatile`, with its four event subscriptions moved across rebuilds) whenever the baseline `DwellSettings` tolerance changes **or** the provider raises `Changed`. No controller exists yet, so the provider holds no deltas and always returns baseline.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (framework-agnostic); xUnit v3 + NFluent (`Check.That(...)`).

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §5 (value model + live-update integration), §7.1 (loosen tolerance = looser/larger), §8.3/§8.4 (zero-delta ⇒ exact baseline; sub-bands strictly inside validated ranges), §13.1 (fixed safety envelope; fixation radius +50 %; geometry bound is feature-introduced).

**Depends on:** **Phase 1a fully implemented** — specifically 1a-⑤'s `EffectiveValue.Compute(...)` (`src/EyeCtrl.Core/Adaptation/EffectiveValue.cs`) and `AdaptationSettings` (`Enabled` default false, `Posture`; registered via `EyeCtrlSettings.EnumerateSubSettings()`). This plan is written against the **post-1a** source.

**Executor pre-flight (do this FIRST — 1a is not yet in the tree):** confirm these three Phase-1a signatures against the *merged* 1a source before writing any code, because this plan assumes them and a shape mismatch breaks Tasks 1 and 3 silently — (1) `EffectiveValue.Compute(double baseline, double forgivingDelta, double tighteningDelta, (double Min, double Max) forgivingBand, (double Min, double Max) tighteningBand, double validMin, double validMax)`; (2) `AdaptationSettings.Enabled` (settable `bool`, default `false`) reachable as `EyeCtrlSettings.Adaptation`; (3) `DwellContext` (enum incl. `Other`). If 1a shipped any differently (e.g. a named struct instead of the band tuples, or a different parameter order), adjust Task 1's provider + tests to match before proceeding.

**⚠️ Core enforces XML docs as build errors:** `src/EyeCtrl.Core/EyeCtrl.Core.csproj` sets `<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `<WarningsAsErrors>CS1591</WarningsAsErrors>`. Every public member below — **including every enum member** — is documented. Do not strip the doc comments.

---

## Scope boundary (explicit — deferred pieces are noted where they land)

This slice routes **only the fixation-tolerance knob** (the single Core choke point, `GazeEngine._fixationDetector` at `GazeEngine.cs:83`). Verified deferrals:

- **Dwell-time routing** across the ~20 creation sites (ViewModels: `ActivationButtonViewModel.cs:37`, `ToolbarViewModel.cs:57`, `TaskSwitcherViewModel.cs:120`, `QuickActionsGridViewModel.cs:112,121`, `GlassBackdropViewModel.cs:37`, `DragTrailViewModel.cs:137`; App: `EventCoordinator.cs:1079,1418`, `GazeEngineAdapter.cs:114`, `ManualSelectionDwellService.cs:76`, `ScrollModeController.cs:73`, `OverlayManager.cs:103`) → a dedicated **dwell-routing** slice.
- **Smoothing routing** (the `GazeEngine` filter rebuild reading effective `OneEuroMinCutoff` / `SmoothingFactor` — recall **down = more smoothing for both**, review round-1 F1) → the dwell/smoothing routing slice.
- **Cursor/overlay-size + target-geometry routing** → needs the **feature-introduced geometry bound** in the fuller `AdaptationSettings` (1b-②; the geometry knob has no pre-existing validated range — review round-1 F2).
- **Migrating the per-knob band constants into user-configurable `AdaptationSettings`** (posture presets, per-knob enables/bands, cue/caregiver toggles) → **1b-②**. Here the bands are an internal constant table encoding §13.1's fixed envelope; they are exercised only by tests and (later) the controller, since no deltas are produced in this slice.
- **`FixationDetectionMs` is NOT an adaptation knob** (§13.1 gives no fixation-*time* band). It stays at baseline, but the rebuild still fires on a manual `FixationDetectionMs` edit (the detector is reconstructed with the new baseline time).
- **`FixationCentroidTracker.VelocityThresholdPxPerSec`** also does not propagate today (`_centroidTracker` at `GazeEngine.cs:87` is readonly and never rebuilt), but it is **not** an adaptation knob and its tracker holds Welford jitter state (1a-④). Its live reconfiguration is out of scope.

`AdaptationKnob` starts with the single wired knob (`FixationRadius`); `DwellTime` / `Smoothing` / `TargetGeometry` are added with their routing slices.

---

## File Structure

**Create (Core — `EyeCtrl.Core.Adaptation`):**
- `src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs` — enum of adaptable knobs (initially `FixationRadius`).
- `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs` — stateful composition layer over `EffectiveValue`.

**Modify:**
- `src/EyeCtrl.Core/GazeEngine.cs` — optional provider param; effective-tolerance routing; `FixationDetector` rebuild (`volatile` field + event re-wiring) on `DwellSettings` change **and** `provider.Changed`.
- `src/EyeCtrl.App/Services/AppBootstrapper.cs` — register the provider; pass it to `GazeEngine`.

**Test (create):**
- `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs`
- `tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs`

**Zero-behaviour-change guarantee:** the provider holds no deltas; `GazeEngine`'s new provider parameter defaults `null` (falls back to reading baseline `settings.Dwell.FixationRadiusPixels`), so the existing 4-arg `GazeEngine` constructions across the test suite compile and behave identically. With `AdaptationSettings.Enabled == false`, `Effective` returns the exact baseline. The one intended change is the new `DwellSettings`→`FixationDetector` propagation (latent-bug fix), covered by a test.

---

## Task 1: `EffectiveSettingsProvider` + `AdaptationKnob` (Core)

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs`, `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class EffectiveSettingsProviderTests
{
    private static EffectiveSettingsProvider Make(bool enabled)
        => new(new AdaptationSettings { Enabled = enabled });

    [Fact]
    public void Disabled_ReturnsBaseline_EvenWithDeltaSet()
    {
        var p = Make(enabled: false);
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 40, tightening: 0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void Enabled_NoDelta_ReturnsBaseline()
    {
        var p = Make(enabled: true);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void Enabled_ForgivingDelta_LoosensRadius_ClampedToBand()
    {
        var p = Make(enabled: true);
        // Forgiving (looser = larger) delta of +40; band is [0, +50% of baseline] = [0, 25].
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 40, tightening: 0);
        double eff = p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50);
        Check.That(eff > 50.0).IsTrue();
        Check.That(eff).IsEqualTo(75.0); // +40 clamped to +25 → 50 + 25
    }

    [Fact]
    public void Enabled_ForgivingDelta_NeverExceedsValidatedMax()
    {
        var p = Make(enabled: true);
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 1000, tightening: 0);
        // 190 + min(1000, 190*0.5=95) = 285, clamped to the validated max (200).
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 190)).IsEqualTo(200.0);
    }

    [Fact]
    public void ClearMomentaryDeltas_RestoresBaseline()
    {
        var p = Make(enabled: true);
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, 40, 0);
        p.ClearMomentaryDeltas();
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void SetAndClear_RaiseChanged()
    {
        var p = Make(enabled: true);
        int changed = 0;
        p.Changed += () => changed++;
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, 40, 0);
        p.ClearMomentaryDeltas();
        Check.That(changed).IsEqualTo(2);
    }

    [Fact]
    public void FixationRadius_EffectiveStaysWithinDwellSettingsValidatedRange()
    {
        // Drift guard: KnobPolicy hardcodes the fixation-radius validated range (10–200), duplicating
        // DwellSettings.FixationRadiusPixels's guard. Use that setter as the ORACLE — if KnobPolicy's
        // ValidMax ever drifts above the settings guard, assigning the effective value back throws and
        // this fails. No magic number in the assertion (Phase 1b-② removes the duplication).
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var p = new EffectiveSettingsProvider(settings.Adaptation);
        settings.Dwell.FixationRadiusPixels = 200; // top of the validated range
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 10_000, tightening: 0);
        double eff = p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, settings.Dwell.FixationRadiusPixels);

        bool threw = false;
        try { settings.Dwell.FixationRadiusPixels = eff; }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check.That(threw).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EffectiveSettingsProviderTests"`
Expected: FAIL to **compile** — `AdaptationKnob` and `EffectiveSettingsProvider` do not exist.

- [ ] **Step 3: Create the enum**

`src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Identifies an adaptable setting ("knob") the <see cref="EffectiveSettingsProvider"/> composes a
/// bounded momentary delta onto. Each knob has its own safe direction, units, and safety band
/// (design §5/§13.1). Further knobs (dwell time, smoothing, target geometry) are added as their
/// routing lands in later Phase-1b slices.
/// </summary>
public enum AdaptationKnob
{
    /// <summary>Fixation-detection hit radius in physical pixels — the tolerance knob (looser = larger).</summary>
    FixationRadius,
}
```

- [ ] **Step 4: Create the provider**

`src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Settings;

/// <summary>
/// Stateful composition layer over the pure <see cref="EffectiveValue"/> calculator. Holds the
/// momentary per-<c>(knob, context)</c> delta the Phase-1b <c>AdaptationController</c> pushes in,
/// applies the <see cref="AdaptationSettings.Enabled"/> gate, and exposes the effective value each
/// consumer reads. When adaptation is disabled or no delta is stored, <see cref="Effective"/> returns
/// the exact baseline — the locked "disabled ⇒ effective == baseline" invariant (design §8.3).
/// Not thread-safe for concurrent writes: deltas are set on the serialized controller/dispatcher
/// thread. In this slice the only reader (<c>GazeEngine.RebuildFixationDetector</c>) runs on that same
/// thread; cross-thread reads via <see cref="Effective"/> from consumer (ViewModel) threads arrive with
/// the later routing slices (design §9).
/// </summary>
public sealed class EffectiveSettingsProvider
{
    // §13.1 fixed safety envelope, as fractions of baseline + the setting's validated range. These are
    // internal constants in this slice; Phase 1b-② migrates them into user-configurable AdaptationSettings
    // (which validates the bands inside the base range, removing the duplication).
    // ⚠️ ValidMin/ValidMax here DUPLICATE DwellSettings.FixationRadiusPixels's guard (10–200); the Task-1
    // drift-guard test uses that setter as the oracle so a divergence fails the build. A knob reaches this
    // table only when it has a stored delta (see Effective), which falls back to baseline for any knob
    // without a policy entry.
    private readonly record struct KnobPolicy(
        double ForgivingFracMin, double ForgivingFracMax,
        double TighteningFracMin, double TighteningFracMax,
        double ValidMin, double ValidMax);

    private static readonly IReadOnlyDictionary<AdaptationKnob, KnobPolicy> Policies =
        new Dictionary<AdaptationKnob, KnobPolicy>
        {
            // Fixation radius: forgiving = looser = up to +50 % larger; no tightening. Validated range
            // 10–200 px (DwellSettings.FixationRadiusPixels guard).
            [AdaptationKnob.FixationRadius] = new(0.0, 0.5, 0.0, 0.0, 10.0, 200.0),
        };

    private readonly AdaptationSettings _adaptation;
    private readonly Dictionary<(AdaptationKnob Knob, DwellContext Context), (double Forgiving, double Tightening)> _deltas = new();

    /// <summary>Raised whenever a momentary delta is set or cleared, so consumers can re-read effective values.</summary>
    public event Action? Changed;

    /// <summary>Initializes the provider with the master adaptation settings (the enable gate).</summary>
    /// <param name="adaptation">The adaptation settings whose <see cref="AdaptationSettings.Enabled"/> gates composition.</param>
    public EffectiveSettingsProvider(AdaptationSettings adaptation) => _adaptation = adaptation;

    /// <summary>
    /// Returns the effective value for <paramref name="knob"/> in <paramref name="context"/>, composing
    /// the stored momentary delta onto <paramref name="baseline"/> via <see cref="EffectiveValue.Compute"/>.
    /// Returns the exact <paramref name="baseline"/> when adaptation is disabled or no non-zero delta is stored.
    /// </summary>
    /// <param name="knob">The knob being read.</param>
    /// <param name="context">The interaction context; use <see cref="DwellContext.Other"/> for global knobs such as tolerance.</param>
    /// <param name="baseline">The user's saved baseline value for the knob.</param>
    /// <returns>The composed, clamped effective value, or the baseline.</returns>
    public double Effective(AdaptationKnob knob, DwellContext context, double baseline)
    {
        if (!_adaptation.Enabled)
            return baseline;
        if (!_deltas.TryGetValue((knob, context), out var d) || (d.Forgiving == 0 && d.Tightening == 0))
            return baseline;

        if (!Policies.TryGetValue(knob, out var p))
            return baseline; // defensive (design §11): a delta for a knob without a policy falls back to baseline
        return EffectiveValue.Compute(
            baseline, d.Forgiving, d.Tightening,
            (p.ForgivingFracMin * baseline, p.ForgivingFracMax * baseline),
            (p.TighteningFracMin * baseline, p.TighteningFracMax * baseline),
            p.ValidMin, p.ValidMax);
    }

    /// <summary>
    /// Sets the momentary delta components for a knob/context and raises <see cref="Changed"/>. Called by
    /// the Phase-1b controller (and tests); the components are clamped to the knob's sub-bands inside
    /// <see cref="EffectiveValue.Compute"/> when read.
    /// </summary>
    /// <param name="knob">The knob.</param>
    /// <param name="context">The context.</param>
    /// <param name="forgiving">Forgiving delta (signed toward "easier"; clamped to the knob's forgiving band).</param>
    /// <param name="tightening">Tightening delta (clamped to the knob's tightening band; zero for forgiving-only knobs).</param>
    public void SetMomentaryDelta(AdaptationKnob knob, DwellContext context, double forgiving, double tightening)
    {
        _deltas[(knob, context)] = (forgiving, tightening);
        Changed?.Invoke();
    }

    /// <summary>Clears every momentary delta (toolbar reset / session boundary) and raises <see cref="Changed"/> if any were present.</summary>
    public void ClearMomentaryDeltas()
    {
        if (_deltas.Count == 0)
            return;
        _deltas.Clear();
        Changed?.Invoke();
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EffectiveSettingsProviderTests"`
Expected: PASS (7 tests). If any `CS1591` error appears, a doc comment was dropped — restore it.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs
git commit -m "feat(adaptation): add EffectiveSettingsProvider (gated composition over EffectiveValue)"
```

---

## Task 2: Register the provider in DI and pass it to `GazeEngine`

**Files:** Modify `src/EyeCtrl.App/Services/AppBootstrapper.cs`

- [ ] **Step 1: Register the provider before the `GazeEngine` registration**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, add `using EyeCtrl.Core.Adaptation;` if not present. Immediately **before** the `// Core engine` block (line 141), add:

```csharp
        // Struggle-detection effective-settings provider (Phase 1b): composes bounded momentary
        // deltas onto baseline settings. Holds no deltas until the AdaptationController lands, and
        // returns baseline while AdaptationSettings.Enabled == false.
        services.AddSingleton(sp => new EffectiveSettingsProvider(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation));
```

- [ ] **Step 2: Pass the provider into `GazeEngine`**

Change the `GazeEngine` registration (lines 142-146) from:

```csharp
        services.AddSingleton(sp => new GazeEngine(
            sp.GetRequiredService<IGazeProvider>(),
            sp.GetRequiredService<IInputSimulator>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<CalibrationService>()));
```

to:

```csharp
        services.AddSingleton(sp => new GazeEngine(
            sp.GetRequiredService<IGazeProvider>(),
            sp.GetRequiredService<IInputSimulator>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<CalibrationService>(),
            sp.GetRequiredService<EffectiveSettingsProvider>()));
```

- [ ] **Step 3: Build**

Run: `dotnet build src/EyeCtrl.App`
Expected: builds. (The `GazeEngine` 5th parameter added in Task 3 is optional, so this compiles once Task 3 lands; if building Task 2 in isolation, complete Task 3 first — they ship in one plan. The provider resolves from the Task 1 type + the `EyeCtrlSettings.Adaptation` sub-setting.)

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs
git commit -m "feat(adaptation): register EffectiveSettingsProvider and feed it to GazeEngine"
```

---

## Task 3: `GazeEngine` routes fixation tolerance through the provider + rebuilds the detector on change

Make `_fixationDetector` rebuildable: change it from `readonly` to `volatile` (mirroring `_gapInterpolator`), extract its four event subscriptions into wire/unwire helpers, read the effective radius from the provider, and rebuild on **either** a baseline `DwellSettings` tolerance change (the latent-bug fix) **or** `provider.Changed`.

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`
- Test: `tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow; // reuse ScriptedGazeProvider + RecordingInputSimulator (internal, same assembly)

public class GazeEngineFixationRebuildTests
{
    private static EyeCtrlSettings Settings(double radius, int detectMs)
    {
        var s = new EyeCtrlSettings();
        s.Cursor.FilterMode = GazeFilterMode.Direct; // passthrough: filtered == raw
        s.Dwell.FixationRadiusPixels = radius;
        s.Dwell.FixationDetectionMs = detectMs;
        return s;
    }

    // Alternates gaze between (cx,cy) and (cx+spanPx, cy) every 33 ms for durationMs. The maximum
    // distance from the first point is exactly spanPx, so the cluster is ONE fixation iff the detector
    // radius >= spanPx; with radius < spanPx the center keeps resetting and never fixates. spanPx: 0
    // holds gaze still — a guaranteed fixation for any positive radius.
    private static void DriveJitter(ScriptedGazeProvider p, double cx, double cy, double spanPx, int durationMs, ref long ts)
    {
        bool far = false;
        for (int elapsed = 0; elapsed < durationMs; elapsed += 33)
        {
            ts += 33_000;
            p.Push(new GazePoint(far ? cx + spanPx : cx, cy, ts, true));
            far = !far;
        }
    }

    [Fact]
    public void ManualToleranceWidening_PropagatesToLiveDetector()
    {
        // Latent-bug fix: GazeEngine previously subscribed only to CursorSettings, so this edit was ignored.
        var s = Settings(radius: 20, detectMs: 150);
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts); // radius 20 < 40 → no fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        s.Dwell.FixationRadiusPixels = 120; // widen at runtime → rebuild
        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts); // radius 120 >= 40 → fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    [Fact]
    public void AdaptationDelta_LoosensToleranceLive_WhenEnabled()
    {
        var s = Settings(radius: 30, detectMs: 150);
        s.Adaptation.Enabled = true;
        var provider = new ScriptedGazeProvider();
        var effective = new EffectiveSettingsProvider(s.Adaptation);
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s, effectiveSettings: effective);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts); // baseline 30 < 40 → no fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        // Controller-style forgiving delta: +100 clamps to +50% (=15) → effective radius 45 >= 40.
        effective.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 100, tightening: 0);
        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts);
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    [Fact]
    public void AdaptationDelta_HasNoEffect_WhenDisabled()
    {
        // Exit invariant: disabled ⇒ effective == baseline, so the delta is inert.
        var s = Settings(radius: 30, detectMs: 150); // Adaptation.Enabled defaults false
        var provider = new ScriptedGazeProvider();
        var effective = new EffectiveSettingsProvider(s.Adaptation);
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s, effectiveSettings: effective);
        long ts = 0;

        effective.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 100, tightening: 0);
        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts);
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void RebuildMidFixation_EndsCleanly_SoNextFixationRetriggers()
    {
        // Review round-1 (reviewer F2): a rebuild while CurrentState == Fixation must NOT strand the state
        // machine — unwiring the old detector fires no FixationEnded, and a fresh FixationStarted no-ops
        // unless in FreeLook, so RebuildFixationDetector ends the in-progress fixation cleanly.
        var s = Settings(radius: 60, detectMs: 150);
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 0, durationMs: 300, ref ts); // hold still → fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);

        s.Dwell.FixationRadiusPixels = 80; // rebuild mid-fixation → clean end → FreeLook
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        DriveJitter(provider, 700, 700, spanPx: 0, durationMs: 300, ref ts); // fresh fixation re-triggers
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngineFixationRebuildTests"`
Expected: FAIL to **compile** — the `effectiveSettings:` constructor argument does not exist yet (added in Step 4). Once it compiles, `ManualToleranceWidening_*` and `RebuildMidFixation_*` are the behaviours Steps 3–6 turn green (today the `DwellSettings` edit is ignored, and a mid-fixation rebuild would strand the state machine).

- [ ] **Step 3: Make `_fixationDetector` rebuildable and add the provider + fields**

In `src/EyeCtrl.Core/GazeEngine.cs`, add `using EyeCtrl.Core.Adaptation;` after the existing `using` lines.

Change the field declaration (line 23):

```csharp
    private readonly FixationDetector _fixationDetector;
```

to:

```csharp
    private volatile FixationDetector _fixationDetector;
```

Add these fields next to it (after `_centroidTracker` on line 24):

```csharp
    private readonly EffectiveSettingsProvider? _effectiveSettings;
    private readonly PropertyChangedEventHandler _dwellSettingsChangedHandler;
    private readonly Action? _effectiveSettingsChangedHandler;
```

- [ ] **Step 4: Extend the constructor**

Change the constructor signature (lines 54-58):

```csharp
    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings,
        CalibrationService? calibrationService = null)
```

to (append the optional provider + document it):

```csharp
    /// <param name="effectiveSettings">Optional effective-settings provider. When null, tolerance
    /// reads the baseline directly and no adaptation-driven rebuild occurs.</param>
    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings,
        CalibrationService? calibrationService = null,
        EffectiveSettingsProvider? effectiveSettings = null)
```

Replace the constructor **body** from the `_fixationDetector` creation through the fixation-event wiring (current lines 83-100) — i.e. this block:

```csharp
        _fixationDetector = new FixationDetector(
            settings.Dwell.FixationRadiusPixels,
            settings.Dwell.FixationDetectionMs);

        _centroidTracker = new FixationCentroidTracker(settings.Dwell.VelocityThresholdPxPerSec);

        InteractionManager = new InteractionManager
        {
            SelectionMode = settings.SelectionMode
        };

        _startTrackingHandler = _ => _centroidTracker.StartTracking();

        _gazeProvider.GazePointReceived += OnGazePoint;
        _fixationDetector.FixationStarted += InteractionManager.OnFixationStarted;
        _fixationDetector.FixationEnded += InteractionManager.OnFixationEnded;
        _fixationDetector.FixationStarted += _startTrackingHandler;
        _fixationDetector.FixationEnded += _centroidTracker.StopTracking;
```

with (create the collaborators the wiring depends on first, then build + wire the detector, then subscribe the two rebuild triggers):

```csharp
        _effectiveSettings = effectiveSettings;

        _centroidTracker = new FixationCentroidTracker(settings.Dwell.VelocityThresholdPxPerSec);

        InteractionManager = new InteractionManager
        {
            SelectionMode = settings.SelectionMode
        };

        _startTrackingHandler = _ => _centroidTracker.StartTracking();

        // FixationDetector radius/time are readonly, so tolerance changes are applied by REBUILDING it
        // (design §5). Build with the effective radius and move its four subscriptions across rebuilds.
        _fixationDetector = new FixationDetector(EffectiveFixationRadius(), settings.Dwell.FixationDetectionMs);
        WireFixation(_fixationDetector);

        // Rebuild on a baseline tolerance edit. GazeEngine previously subscribed to CursorSettings only,
        // so DwellSettings tolerance edits never reached the live detector (latent bug — now fixed).
        _dwellSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(DwellSettings.FixationRadiusPixels)
                               or nameof(DwellSettings.FixationDetectionMs))
                RebuildFixationDetector();
        };
        _settings.Dwell.PropertyChanged += _dwellSettingsChangedHandler;

        // Rebuild when an adaptation delta changes the effective tolerance.
        if (_effectiveSettings is not null)
        {
            _effectiveSettingsChangedHandler = RebuildFixationDetector;
            _effectiveSettings.Changed += _effectiveSettingsChangedHandler;
        }

        _gazeProvider.GazePointReceived += OnGazePoint;
```

- [ ] **Step 5: Add the helpers**

In `src/EyeCtrl.Core/GazeEngine.cs`, add these private methods (e.g. immediately after the constructor, before `Start()`):

```csharp
    private double EffectiveFixationRadius() =>
        _effectiveSettings?.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, _settings.Dwell.FixationRadiusPixels)
        ?? _settings.Dwell.FixationRadiusPixels;

    private void WireFixation(FixationDetector d)
    {
        d.FixationStarted += InteractionManager.OnFixationStarted;
        d.FixationEnded += InteractionManager.OnFixationEnded;
        d.FixationStarted += _startTrackingHandler;
        d.FixationEnded += _centroidTracker.StopTracking;
    }

    private void UnwireFixation(FixationDetector d)
    {
        d.FixationStarted -= InteractionManager.OnFixationStarted;
        d.FixationEnded -= InteractionManager.OnFixationEnded;
        d.FixationStarted -= _startTrackingHandler;
        d.FixationEnded -= _centroidTracker.StopTracking;
    }

    // Rebuilds the detector with the current effective radius + baseline detection time (FixationDetectionMs
    // is not an adaptation knob). Invoked on the settings/dispatcher thread; the volatile publish makes the
    // swap visible to the gaze thread, which reads a fully-wired detector (old or new). The fresh detector
    // starts cold, so any in-progress fixation is dropped; since the state machine only honours a new
    // FixationStarted from FreeLook (InteractionManager.OnFixationStarted no-ops otherwise), we END an
    // in-progress fixation cleanly here — mirroring the FixationEnded fan-out from WireFixation — so the
    // activation button never strands and the fresh detector's next fixation re-triggers (review round-1 F2).
    private void RebuildFixationDetector()
    {
        var fresh = new FixationDetector(EffectiveFixationRadius(), _settings.Dwell.FixationDetectionMs);
        WireFixation(fresh);
        var old = _fixationDetector;
        bool wasFixating = old.IsFixating;
        _fixationDetector = fresh;
        UnwireFixation(old);
        if (wasFixating)
        {
            // Old is now unwired; replicate its FixationEnded handlers so the state machine returns to
            // FreeLook and the centroid tracker stops. OnFixationEnded is guarded (no-op unless in Fixation).
            InteractionManager.OnFixationEnded();
            _centroidTracker.StopTracking();
        }
    }
```

- [ ] **Step 6: Update `Dispose` to unsubscribe the new handlers**

In `Dispose()` (lines 146-156), change:

```csharp
        _settings.Cursor.PropertyChanged -= _cursorSettingsChangedHandler;
        _gazeProvider.GazePointReceived -= OnGazePoint;
        _fixationDetector.FixationStarted -= InteractionManager.OnFixationStarted;
        _fixationDetector.FixationEnded -= InteractionManager.OnFixationEnded;
        _fixationDetector.FixationStarted -= _startTrackingHandler;
        _fixationDetector.FixationEnded -= _centroidTracker.StopTracking;
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
```

to:

```csharp
        _settings.Cursor.PropertyChanged -= _cursorSettingsChangedHandler;
        _settings.Dwell.PropertyChanged -= _dwellSettingsChangedHandler;
        if (_effectiveSettings is not null && _effectiveSettingsChangedHandler is not null)
            _effectiveSettings.Changed -= _effectiveSettingsChangedHandler;
        _gazeProvider.GazePointReceived -= OnGazePoint;
        UnwireFixation(_fixationDetector);
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
```

- [ ] **Step 7: Run the new tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngineFixationRebuildTests"`
Expected: PASS (4 tests).

- [ ] **Step 8: Run the existing GazeEngine + fixation suites to confirm zero regressions**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngine|FullyQualifiedName~FixationDetector"`
Expected: PASS — existing `GazeEngineTests`, `GazeEngineDisposeTests`, `GazeEngineFilterRecreationTests`, `GazeEngineSampledTests`, and `FixationDetector*Tests` unchanged (the 4-arg constructor still works; the fixation-event wiring is behaviourally identical, just routed through `WireFixation`).

- [ ] **Step 9: Full build + headless suites**

Run: `dotnet build EyeCtrl.slnx`
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS. (App.Tests confirms the DI registration from Task 2 resolves `GazeEngine` with the provider.)

- [ ] **Step 10: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs
git commit -m "feat(adaptation): route fixation tolerance through the effective-settings provider + rebuild-on-change"
```

---

## Self-Review

**Spec coverage (this slice):**
- §5 value model consumed through a stateful layer — `EffectiveSettingsProvider` wraps `EffectiveValue.Compute`, adding the delta store + `Enabled` gate + `Changed` event. ✓
- §5 live-update: tolerance is `readonly` → **rebuild** with **event re-wiring** of the readonly detector (`WireFixation`/`UnwireFixation`, `volatile` field mirroring `_gapInterpolator`). ✓
- §5 "real gap to fix": `FixationDetector` ignored `DwellSettings` (only `CursorSettings` was subscribed, `GazeEngine.cs:81`) — now subscribes to `DwellSettings.PropertyChanged` (`ManualToleranceWidening_*` test). ✓
- §7.1 loosen tolerance = looser/larger — `FixationRadius` forgiving band `[0, +50 %]` (`KnobPolicy`). ✓
- §8.3 zero delta / disabled ⇒ exact baseline — `Effective` short-circuits; `AdaptationDelta_HasNoEffect_WhenDisabled` (the exit invariant, locked). ✓
- §8.4 sub-bands strictly inside validated ranges — band fractions clamp, then the validated-range clamp (10–200) via `EffectiveValue.Compute`; `Enabled_ForgivingDelta_NeverExceedsValidatedMax`. ✓
- §13.1 fixed envelope — bands are posture-independent constants here (migrated to `AdaptationSettings` in 1b-②). ✓
- Rebuild does not strand the interaction state machine — a mid-fixation rebuild ends the in-progress fixation cleanly (mirrors the `FixationEnded` fan-out) so the activation button never lingers and the fresh detector re-triggers (`RebuildMidFixation_*`; review round-1 F2). And `Effective` falls back to baseline for any knob without a `KnobPolicy` entry, so no exception can reach the gaze/settings path (§11; review round-1 F3). ✓

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `EffectiveSettingsProvider(AdaptationSettings)`, `Effective(AdaptationKnob, DwellContext, double)`, `SetMomentaryDelta(AdaptationKnob, DwellContext, double, double)`, `ClearMomentaryDeltas()`, `event Action Changed` are used identically in `EffectiveSettingsProviderTests`, `GazeEngineFixationRebuildTests`, and the `GazeEngine` wiring. `EffectiveValue.Compute(double, double, double, (double,double), (double,double), double, double)` matches its 1a-⑤ definition. `GazeEngine`'s new 5th ctor arg `EffectiveSettingsProvider? = null` matches the DI registration (Task 2) and the test constructions. `DwellSettings.FixationRadiusPixels`/`FixationDetectionMs` and `CursorSettings.FilterMode`/`GazeFilterMode.Direct` are verified against source.

**Verified facts relied on (from source):**
- `GazeEngine` ctor is 4-arg (`GazeEngine.cs:54`); subscribes only `_settings.Cursor.PropertyChanged` (`:81`), never `Dwell` — the latent bug. `_fixationDetector` is `readonly` (`:23`) with 4 subscriptions (`:97-100`); `_gapInterpolator` is `volatile` (`:20`) — the pattern mirrored here.
- `FixationDetector` radius/time are `readonly` (`FixationDetector.cs:10-11`); ctor validates positive/finite (`:33-40`); events `FixationStarted`/`FixationEnded` (`:18,20`).
- `InteractionManager.OnFixationStarted` transitions `FreeLook → Fixation` (`InteractionManager.cs:61-66`), observable via `CurrentState`; harness precedent `GazeEngineFilterRecreationTests` + `GazeFlowHarness`.
- `GazeEngine` DI registration at `AppBootstrapper.cs:142-146`; `EyeCtrlSettings.Adaptation` sub-setting (1a-⑤).
- Jitter geometry: alternating `(cx,cy)`/`(cx+span,cy)` gives max-distance-from-first `== span`, so fixation occurs iff radius `>= span` (baseline 20/30 `< 40 <=` widened 120 / effective 45).

**Honest testability limits:** the provider is fully unit-tested (7 tests, incl. the range drift-guard). The `GazeEngine` rebuild is verified end-to-end (4 tests) through the interaction state machine (integration-style) rather than by inspecting the private detector, **reusing `ScriptedGazeProvider`/`RecordingInputSimulator` from `EyeCtrl.Core.Tests.Flow`** (no new hand-rolled fakes — avoids fake-drift as `IGazeProvider`/`IInputSimulator` evolve; review round-1). Task 2 (DI) is build- + App.Tests-verified.

**Deferred (documented above, not gaps):** dwell-time routing (~20 sites), smoothing routing, cursor/overlay-size + geometry routing, and migrating the band constants into configurable `AdaptationSettings` — each assigned to a named later 1b slice.
