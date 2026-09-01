# Smoothing-Knob Routing Implementation Plan (Phase 1c, slice 1 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route the `AdaptationKnob.Smoothing` momentary fill to its first consumer — the live gaze filter — so sustained signal-noise/poor-tracking evidence actually retunes the active filter, clamped to the active filter's validated range, with a bounded rebuild budget.

**Architecture:** The controller already pushes a Smoothing band-fill into `EffectiveSettingsProvider`; nothing reads it. This slice adds: (1) a factory overload accepting the *effective* active-filter parameter, (2) a `GazeEngine` value path that resolves the active mode's effective parameter (Direct ⇒ no-op), clamped to the **active filter's** validated range, (3) a knob-aware rebuild request (compare live vs. applied via an Interlocked bit round-trip, revalidated after publish so a racing fill change can't leave the filter stale), and (4) the §9.1 rebuild budget: adaptive rebuilds at most ~1/s, pending request deferred not dropped, a gaze-clock restart treated as budget-satisfied. Core-only; no App-layer changes (the DI wiring already passes the provider into `GazeEngine`).

**Tech Stack:** C# / .NET 10, xUnit v3, NFluent assertions (`Check.That(...)`, **not** `Assert.*` — wired globally in `tests/Directory.Build.props`, which also supplies global `using NFluent;` and `using Xunit;`).

---

## Global Constraints

- **Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — **§13.3** (this slice's four missing items, the One-Euro clamp requirement, the Direct no-op, the `SmoothingMoreMaxFraction` comfort question), §5 (warm-start R1, typed per-knob transforms), §9.1 (rebuild budget). Read §13.3 first.
- **This is slice 1 of the Phase-1c routing sequence (§13.3: smoothing → dwell → geometry).** Do NOT route dwell or geometry here. The Phase-2 gate stays open until all three land.
- **Never run the app or simulate real input.** The repo owner controls their PC solely by eye tracking; driving the cursor or keyboard takes away their only input channel. Everything here is verifiable headlessly. Do not run `Category=UI` tests.
- **German test output.** Per-test failures are `Fehler <FullyQualifiedName>` — not `Failed` — and the run summary is `Bestanden!` / `Fehler!`. Grepping for `Failed` silently reports a green suite. Always read the summary line.
- **Do not run two test suites at once** on this box (named-pipe/machine-global contention); each task's steps run exactly one suite.
- Baseline: `main` at `e6c28176`, `EyeCtrl.Core.Tests` = **1100 total (1099 passed, 1 skipped)**, green. A different starting count means you did not branch from `main`.
- Code style: file-scoped namespaces, Allman braces, `_camelCase` private fields, nullable enabled.
- `InternalsVisibleTo` → `EyeCtrl.Core.Tests` already exists (`EyeCtrl.Core.csproj:12`); `ImplicitUsings` is enabled (so `System.Threading`/`Interlocked` needs no new using).

---

## Context you need before starting

**What exists today.** The momentary layer is quarter-live: `GazeEngine.cs:262` (`EffectiveFixationRadius`) is the only production `Effective(...)` consumer — tolerance. The provider stores fills for all four knobs and raises a payloadless `Changed` on every fill write. `GazeEngine` already defers provider-driven reconfiguration onto the gaze thread (`_fixationReconfigureRequested` pattern) and rebuilds its `GapInterpolator` on baseline smoothing edits (`_interpolatorRebuildRequested`, warm-started — the R1 no-cursor-jump fix). The filter is built by `GazeFilterFactory.Create(CursorSettings)`, which accepts **baseline** settings only — that is missing item (1).

**The four §13.3 items this slice discharges:**
1. **Provider→filter value path** — the rebuild must compose the *effective* smoothing parameter, not the baseline.
2. **Knob-aware change handling** — `Changed` fires for every knob; naively requesting a rebuild there would rebuild on dwell/geometry steps too. Fix: compare the live effective parameter against the last-applied one.
3. **§9.1 rebuild budget** — the controller steps a fill every 250 ms tick; adaptive rebuilds must occur at most ~1/s, never per tick. Fix: a minimum-interval gate on the gaze thread, **deferring** a pending request rather than dropping it (so the final value always lands).
4. **Cross-thread snapshot strategy** — the provider's `Dictionary` is written on the dispatcher and read on the gaze thread; a first-key insert can resize while a gaze-thread `TryGetValue` is in flight. Resolved by making the store a `ConcurrentDictionary` (single-writer semantics unchanged; reads become safe) rather than adding a snapshot copy.

**The safety requirement (§13.3, "the smoothing knob needs an active-filter-specific clamp").** The provider's Smoothing policy carries the union superset `ValidMin = 0.0`, but `OneEuroMinCutoff` validates `[0.1, 20]`: a legal `0.1 Hz` baseline at the default 0.50 fraction composes to `0.05`, and at fraction 1.0 to exactly `0.0` — which `OneEuroFilter`'s constructor rejects (`minCutoff <= 0` throws). `GazeEngine.ApplyPendingInterpolatorRebuild` has **no try/catch**, so that throw would surface on the **gaze thread** — loss of control for a user whose sole input channel is gaze. Unreachable today (knob unrouted); this slice arms the path, so the clamp is mandatory, not cosmetic. EMA is range-safe (`[0, 1]`, floor 0), but the clamp is applied for both modes symmetrically. Only the **floor** can ever engage: smoothing is forgiving-only (parameter down, no tightening), so the composed value never exceeds the baseline, which the `CursorSettings` setters keep in range.

**The two §13.3 open decisions this slice closes** (Task 4): `SmoothingMoreMaxFraction` **stays 0.50** — the fraction-of-baseline model is kept (the uniform fill model composes cleanly for both filters), and the comfort ceiling is confirmed with numbers: One-Euro 5.0 → 2.5 Hz moves the fixation time constant ≈32 ms → ≈64 ms; EMA 0.5 → 0.25 moves it ≈48 ms → ≈116 ms at every speed. Bounded, reversible, and now gated on noise evidence only. And `GazeFilterMode.Direct` **no-ops**: `DirectFilter` has no smoothing parameter, mode is never auto-switched (§5), so the effective parameter is `null` and no rebuild is ever requested from the adaptation path.

**Lag sanity (why the clamp test uses beta 0):** with `OneEuroBeta = 0`, cutoff ≡ minCutoff at every speed, so step-lag differences isolate the parameter under test. The same trick the existing `GazeEngineFilterRecreationTests` regressions use.

**Reading the deferred-apply machinery.** `OnGazePoint` calls `ApplyPendingFixationReconfiguration()` / `ApplyPendingInterpolatorRebuild(...)` at the top, on the gaze thread, before processing the sample — so the *same sample* that consumes a request is processed by the freshly built filter. `TestGazeProvider.SimulateGaze` fires `GazePointReceived` synchronously, which drives `OnGazePoint` inline: set a fill → one `SimulateGaze` → rebuild applied and observable. No threads, no waiting.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/EyeCtrl.Core/Processing/GazeFilterFactory.cs` | Filter construction | Add optional `effectiveSmoothingParameter` parameter |
| `src/EyeCtrl.Core/GazeEngine.cs` | Live pipeline + reconfiguration | Add the value path (`EffectiveSmoothingParameter`), the knob-aware request, the 1/s budget, the applied-bits bookkeeping |
| `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs` | Fill store + composition | `ConcurrentDictionary` swap (hardening); header/policy comment updates |
| `src/EyeCtrl.Core/Settings/AdaptationSettings.cs` | Band-fraction settings | Resolve the `SmoothingMoreMaxFraction` PROVISIONAL doc (decision: keep 0.50) |
| `tests/EyeCtrl.Core.Tests/Processing/GazeFilterFactoryTests.cs` | **New.** Factory effective-parameter contract | Create |
| `tests/EyeCtrl.Core.Tests/GazeEngineSmoothingRoutingTests.cs` | **New.** Value path + clamp + wiring + budget + continuity | Create |
| `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` | Design record | Mark the §13.3 smoothing items + §14 ceiling resolved |

No benchmark changes: `AdaptationBenchmarks` measures the dispatcher tick (untouched — the handler adds one clamped read per `Changed`), and `GazePipelineWithAdaptationBenchmarks` measures the per-sample path, which gains one `volatile bool` check. Both fixtures still describe reality.

---

### Task 1: Factory overload — compose the filter from an effective parameter

Pure addition; the existing one-argument call sites are untouched (default parameter). Nothing consumes the new parameter yet.

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/GazeFilterFactory.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/GazeFilterFactoryTests.cs` (new)

**Interfaces:**
- Consumes: `CursorSettings`, `GazeFilterMode`, the three filters (all existing).
- Produces: `GazeFilterFactory.Create(CursorSettings settings, double? effectiveSmoothingParameter = null) → IGazeFilter`. The value is the **active filter's** one adaptation-sensitive parameter: `OneEuroMinCutoff` for `OneEuro`, `SmoothingFactor` for `Smoothed`/`SmoothedWithSnapping`; ignored for `Direct`. `null` ⇒ compose from baseline. **The factory does not clamp** — the caller owns the active-range clamp (Task 2).

- [ ] **Step 0: Branch (and commit the plan) BEFORE any code**

Run: `git switch -c feat/struggle-smoothing-routing` — from `main` at `e6c28176`, before Task 1 touches anything. Nothing in this plan may land on `main`. (If the executor works in a `.worktrees/` directory per house convention, create the worktree + branch first and copy this plan file into it — an untracked file does not follow into a new worktree.)

Then commit the plan itself on the branch:

```bash
git add docs/superpowers/plans/2026-09-01-smoothing-knob-routing.md
git commit -m "docs(plan): smoothing-knob routing implementation plan (Phase 1c, slice 1/3)"
```

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Processing/GazeFilterFactoryTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

/// <summary>
/// The §13.3 smoothing-routing contract of <see cref="GazeFilterFactory.Create(CursorSettings,double?)"/>:
/// the effective parameter must behave exactly like the same value set as the baseline, and Direct — which
/// has no smoothing parameter — must ignore it. Verified by step-response differentials (no filter
/// internals), with beta pinned to 0 on One-Euro so cutoff == minCutoff at every speed.
/// </summary>
public class GazeFilterFactoryEffectiveParameterTests
{
    [Fact]
    public void Create_OneEuro_EffectiveMinCutoff_BehavesLikeThatBaseline()
    {
        // 0.1 Hz (the settings floor, maximum smoothing) must lag a step far more than the 5.0 Hz default.
        Check.That(OneEuroStepX(effectiveMinCutoff: 0.1))
            .IsStrictlyLessThan(OneEuroStepX(effectiveMinCutoff: 5.0));
    }

    [Fact]
    public void Create_OneEuro_NullParameter_EqualsTheBaselineComposition()
    {
        Check.That(OneEuroStepX(effectiveMinCutoff: null))
            .IsEqualTo(OneEuroStepX(effectiveMinCutoff: 5.0));
    }

    [Fact]
    public void Create_Ema_EffectiveSmoothingFactor_BehavesLikeThatBaseline()
    {
        // EMA has no speed adaptation, so the effect is large and unambiguous: 0.05 lags far more than 0.5.
        Check.That(EmaStepX(effectiveSmoothingFactor: 0.05))
            .IsStrictlyLessThan(EmaStepX(effectiveSmoothingFactor: 0.5));
    }

    [Fact]
    public void Create_Ema_NullParameter_EqualsTheBaselineComposition()
    {
        Check.That(EmaStepX(effectiveSmoothingFactor: null))
            .IsEqualTo(EmaStepX(effectiveSmoothingFactor: 0.5));
    }

    [Fact]
    public void Create_Direct_IgnoresTheParameter()
    {
        var settings = new CursorSettings { FilterMode = GazeFilterMode.Direct };
        Check.That(GazeFilterFactory.Create(settings, 0.1)).IsInstanceOf<DirectFilter>();
    }

    // 12 steady samples at (500,500), then one step to x=600; the step sample's filtered X is the response.
    private static double OneEuroStepX(double? effectiveMinCutoff)
    {
        var settings = new CursorSettings { FilterMode = GazeFilterMode.OneEuro, OneEuroBeta = 0.0 };
        return StepX(GazeFilterFactory.Create(settings, effectiveMinCutoff));
    }

    private static double EmaStepX(double? effectiveSmoothingFactor)
    {
        var settings = new CursorSettings { FilterMode = GazeFilterMode.Smoothed };
        return StepX(GazeFilterFactory.Create(settings, effectiveSmoothingFactor));
    }

    private static double StepX(IGazeFilter filter)
    {
        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
            filter.Filter(new GazePoint(500, 500, ts, true));
        return filter.Filter(new GazePoint(600, 500, ts, true)).X;
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeFilterFactoryEffectiveParameterTests"`

Expected: compile error — `Create` has no two-parameter overload.

- [ ] **Step 3: Add the overload**

In `src/EyeCtrl.Core/Processing/GazeFilterFactory.cs`, replace the whole `Create` method with:

```csharp
    /// <summary>
    /// Creates an <see cref="IGazeFilter"/> instance based on the configured <see cref="CursorSettings.FilterMode"/>.
    /// </summary>
    /// <param name="settings">Cursor settings specifying filter mode and parameters.</param>
    /// <param name="effectiveSmoothingParameter">The EFFECTIVE value of the active filter's single
    /// adaptation-sensitive parameter (design §13.3 smoothing routing): <c>OneEuroMinCutoff</c> for
    /// <see cref="GazeFilterMode.OneEuro"/>, <c>SmoothingFactor</c> for
    /// <see cref="GazeFilterMode.Smoothed"/>/<see cref="GazeFilterMode.SmoothedWithSnapping"/>; ignored for
    /// <see cref="GazeFilterMode.Direct"/>, which has no smoothing parameter. <c>null</c> composes the filter
    /// from the baseline settings alone — the pre-adaptation behaviour. The caller owns clamping the value
    /// into the ACTIVE filter's validated range; this factory does not clamp.</param>
    /// <returns>A new gaze filter instance configured per the settings.</returns>
    public static IGazeFilter Create(CursorSettings settings, double? effectiveSmoothingParameter = null)
    {
        return settings.FilterMode switch
        {
            GazeFilterMode.Direct => new DirectFilter(),
            GazeFilterMode.Smoothed => new SmoothedFilter(effectiveSmoothingParameter ?? settings.SmoothingFactor),
            // Intentionally identical to Smoothed — see GazeFilterMode.SmoothedWithSnapping docs.
            GazeFilterMode.SmoothedWithSnapping => new SmoothedFilter(effectiveSmoothingParameter ?? settings.SmoothingFactor),
            GazeFilterMode.OneEuro => new OneEuroFilter(effectiveSmoothingParameter ?? settings.OneEuroMinCutoff, settings.OneEuroBeta),
            _ => new OneEuroFilter(effectiveSmoothingParameter ?? settings.OneEuroMinCutoff, settings.OneEuroBeta)
        };
    }
```

- [ ] **Step 4: Run the new tests and the full suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeFilterFactoryEffectiveParameterTests"`
Expected: `Bestanden!`, 5 tests.

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: `Bestanden!`, `Fehler: 0`, `gesamt: 1105` (1100 + 5).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Processing/GazeFilterFactory.cs tests/EyeCtrl.Core.Tests/Processing/GazeFilterFactoryTests.cs
git commit -m "feat(gaze): let the filter factory compose from an effective smoothing parameter

Optional second parameter: the ACTIVE filter's one adaptation-sensitive
value (OneEuroMinCutoff / SmoothingFactor), null = baseline-only — the
pre-adaptation behaviour every existing caller keeps. Direct ignores it.
The factory does not clamp; the caller owns the active-filter range."
```

---

### Task 2: The value path — `EffectiveSmoothingParameter` + the applied-bookkeeping seam

Adds the §13.3 clamp and the mode switch, plus the `AppliedSmoothingParameter` test seam and its construction-time initialisation. **No wiring yet**: nothing calls `EffectiveSmoothingParameter` from production except the constructor initialiser, and no rebuild behaviour changes — the suite stays green and the new tests characterise the method directly through the `InternalsVisibleTo` seam (same pattern as `StruggleMonitor.ClassifyMode`).

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`
- Test: `tests/EyeCtrl.Core.Tests/GazeEngineSmoothingRoutingTests.cs` (new)

**Interfaces:**
- Consumes: `EffectiveSettingsProvider.Effective(AdaptationKnob, DwellContext, double)` (existing), `GazeFilterFactory.Create(settings, double?)` (Task 1).
- Produces:
  - `internal double? EffectiveSmoothingParameter()` — the clamped effective parameter for the CURRENT mode; `null` when there is no provider or the mode is `Direct`.
  - `internal double? AppliedSmoothingParameter` — the parameter the live filter was last BUILT with; `null` whenever the live filter has no smoothing parameter (no provider, or Direct mode) — read by the Task 3 tests.
  - `private const long NoSmoothingBits` and the `_lastAppliedSmoothingBits` field (Task 3's handler compares against it).

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/GazeEngineSmoothingRoutingTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// §13.3 smoothing routing: the provider→filter VALUE path, the active-filter range clamp, and (Task 3)
/// the knob-aware request, the §9.1 rebuild budget, and R1 continuity on the adaptive path. Drives a real
/// <see cref="GazeEngine"/> with a synchronous fake provider — each SimulateGaze runs one OnGazePoint
/// inline, so a deferred reconfiguration is applied and observable with no threading.
/// </summary>
public class GazeEngineSmoothingRoutingTests
{
    // Default EyeCtrlSettings.Cursor: OneEuro, OneEuroMinCutoff 5.0, OneEuroBeta 0.5, SmoothingFactor 0.5.
    // Beta is pinned to 0 so the One-Euro cutoff == minCutoff at every speed — isolates the parameter under
    // test (same trick as GazeEngineFilterRecreationTests); with the default beta 0.5 a fast sample spikes
    // the speed-adaptive cutoff and washes the effect out.
    private static (GazeEngine Engine, EffectiveSettingsProvider Provider, TestGazeProvider TestGaze, EyeCtrlSettings Settings) Make(
        GazeFilterMode mode = GazeFilterMode.OneEuro, bool adaptEnabled = true)
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = mode;
        settings.Cursor.OneEuroBeta = 0.0;
        settings.Adaptation.Enabled = adaptEnabled;
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var testGaze = new TestGazeProvider();
        var engine = new GazeEngine(testGaze, new TestInputSimulator(), settings, effectiveSettings: provider);
        return (engine, provider, testGaze, settings);
    }

    // --- Task 2: the value path (method-level, no pumping) ---

    [Fact]
    public void EffectiveParameter_NoProvider_ReturnsNull()
    {
        var settings = new EyeCtrlSettings();
        var engine = new GazeEngine(new TestGazeProvider(), new TestInputSimulator(), settings);
        Check.That(engine.EffectiveSmoothingParameter()).IsNull();
    }

    [Theory]
    [InlineData(GazeFilterMode.OneEuro, 5.0)]
    [InlineData(GazeFilterMode.Smoothed, 0.5)]
    public void EffectiveParameter_NoFill_EqualsTheActiveModesBaseline(GazeFilterMode mode, double baseline)
    {
        var (engine, _, _, _) = Make(mode);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(baseline);
    }

    [Fact]
    public void EffectiveParameter_Disabled_EqualsBaseline_EvenWithFillSet()
    {
        var (engine, provider, _, _) = Make(GazeFilterMode.OneEuro, adaptEnabled: false);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(5.0);
    }

    [Fact]
    public void EffectiveParameter_Direct_ReturnsNull_EvenWithFillSet()
    {
        // §13.3: DirectFilter takes no smoothing parameter and mode is never auto-switched (§5), so the
        // knob must no-op — the smoothing fill is simply not observable in Direct mode.
        var (engine, provider, _, _) = Make(GazeFilterMode.Direct);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsNull();
    }

    [Theory]
    // mode, baseline, band fraction, expected — the fill is always the full band (1.0)
    [InlineData(GazeFilterMode.OneEuro, 5.0, 0.50, 2.5)]    // plain composition, no clamp
    [InlineData(GazeFilterMode.OneEuro, 0.2, 0.50, 0.1)]    // composes exactly onto the floor
    [InlineData(GazeFilterMode.OneEuro, 0.1, 0.50, 0.1)]    // 0.05 < floor 0.1 → clamped (§13.3 safety)
    [InlineData(GazeFilterMode.OneEuro, 0.1, 1.00, 0.1)]    // 0.0: OneEuroFilter's ctor would throw → clamped
    [InlineData(GazeFilterMode.OneEuro, 20.0, 0.50, 10.0)]  // top baseline; the upper bound never engages
    [InlineData(GazeFilterMode.Smoothed, 0.5, 0.50, 0.25)]  // EMA composes the same way
    [InlineData(GazeFilterMode.Smoothed, 1.0, 1.00, 0.0)]   // EMA floor is 0, and 0.0 is a legal factor
    public void EffectiveParameter_ClampsToTheActiveFiltersValidatedRange(
        GazeFilterMode mode, double baseline, double fraction, double expected)
    {
        // The provider's Smoothing policy carries the union superset [0, 20]; the ACTIVE filter's own
        // validated range ([0.1, 20] One-Euro, [0, 1] EMA) is re-applied here (§13.3 safety requirement).
        var (engine, provider, _, settings) = Make(mode);
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = baseline;
        else settings.Cursor.SmoothingFactor = baseline;
        settings.Adaptation.SmoothingMoreMaxFraction = fraction;
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(expected);
    }

    [Theory]
    [InlineData(GazeFilterMode.OneEuro, 0.1, 1.0)]   // the composed-0.0 trap: must assign 0.1, not 0.0
    [InlineData(GazeFilterMode.Smoothed, 0.5, 1.0)]
    public void EffectiveParameter_IsAlwaysAssignableToTheSettingsGuard(GazeFilterMode mode, double baseline, double fill)
    {
        // Setter-as-oracle drift guard (the FixationRadius precedent): the CursorSettings setters ARE the
        // validated range — they throw outside it. If the clamp ever drifts from the guard, this throws.
        var (engine, provider, _, settings) = Make(mode);
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = baseline;
        else settings.Cursor.SmoothingFactor = baseline;
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: fill, tighteningFill: 0.0);

        double effective = engine.EffectiveSmoothingParameter()!.Value;
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = effective;
        else settings.Cursor.SmoothingFactor = effective;
    }

    [Fact]
    public void EffectiveParameter_ComposesAgainstTheActiveModesBaseline_AcrossAModeSwitch()
    {
        // The fill is a fraction OF the baseline, and the baseline is resolved PER MODE at read time:
        // OneEuro 5.0 → 2.5, then switching the mode to Smoothed (baseline 0.5) composes the SAME fill
        // against 0.5 → 0.25. The apply-time re-read (Task 3) is what makes a mode switch converge.
        var (engine, provider, _, settings) = Make(GazeFilterMode.OneEuro);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(2.5);

        settings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(0.25);
    }

    [Fact]
    public void AppliedParameter_AfterConstruction_IsTheInitialBaselineParameter()
    {
        var (engine, _, _, _) = Make(GazeFilterMode.OneEuro);
        Check.That(engine.AppliedSmoothingParameter).IsEqualTo(5.0);
    }

    [Fact]
    public void AppliedParameter_DirectModeAtConstruction_IsNull()
    {
        var (engine, _, _, _) = Make(GazeFilterMode.Direct);
        Check.That(engine.AppliedSmoothingParameter).IsNull();
    }

    // --- Fakes (own copies — no shared base class; mirrors GazeEngineFilterRecreationTests) ---

    private sealed class TestGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void SimulateGaze(double x, double y, long ts) =>
            GazePointReceived?.Invoke(new GazePoint(x, y, ts, true));
    }

    private sealed class TestInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint point) { }
        public void LeftClick(PhysicalPoint point) { }
        public void RightClick(PhysicalPoint point) { }
        public void DoubleClick(PhysicalPoint point) { }
        public void MouseDown(PhysicalPoint point) { }
        public void MouseUp(PhysicalPoint point) { }
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) { }
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngineSmoothingRoutingTests"`

Expected: compile error — `EffectiveSmoothingParameter` / `AppliedSmoothingParameter` do not exist.

- [ ] **Step 3: Add the fields, the seam, and the method to `GazeEngine`**

In `src/EyeCtrl.Core/GazeEngine.cs`:

**(a)** Next to the other reconfiguration fields (after `_fixationReconfigureRequested`, around line 44), add:

```csharp
    // §13.3 smoothing routing. Sentinel for "the live filter was not built from an effective smoothing
    // parameter" (no provider, or Direct mode). NB: long.MinValue IS the bit pattern of −0.0 — Clamp
    // returns −0.0 unchanged and the CursorSettings guard accepts it (−0.0 < 0.0 is false), so a
    // hand-edited settings.json could collide the sentinel with a real EMA parameter. Benign: −0.0 ≡ 0.0
    // numerically (identical filter behaviour) and the value never changes, so the swallowed comparison
    // is a correct no-op. The bits round-trip exists because the gaze thread (apply) publishes the applied
    // value and the dispatcher thread (the Changed handler) compares against it — a double has no atomic
    // cross-thread read; the long does (Interlocked).
    private const long NoSmoothingBits = long.MinValue;
    private long _lastAppliedSmoothingBits;
```

(The three §9. budget members — `MinAdaptiveRebuildIntervalMicroseconds`, `_lastAdaptiveRebuildTimestampMicroseconds`, `_adaptiveRebuildRequested` — are added in Task 3 Step 4, where they first get a reader. Declaring them here would ship CS0169/CS0414 warnings in the Task-2 commit.)

**(b)** After the `_effectiveSettings.Changed += _effectiveSettingsChangedHandler;` block in the constructor (around line 250, before `_gazeProvider.GazePointReceived += OnGazePoint;`), add:

```csharp
        // The parameter the constructor-built interpolator runs at (fills are necessarily empty here, so
        // this is the active mode's baseline). Without this, bits default to 0 and the first Changed of
        // ANY knob would compare unequal and spend a spurious rebuild.
        _lastAppliedSmoothingBits = EffectiveSmoothingParameter() is { } initial
            ? BitConverter.DoubleToInt64Bits(initial)
            : NoSmoothingBits;
```

**(c)** Next to `EffectiveFixationRadius()` (around line 263), add:

```csharp
    /// <summary>Test seam (<c>InternalsVisibleTo</c> → EyeCtrl.Core.Tests, the ClassifyMode pattern): the
    /// smoothing parameter the live filter was last BUILT with, or <c>null</c> whenever the live filter has
    /// no smoothing parameter (no provider, or Direct mode — Task 3's apply clears the bits to the sentinel
    /// on a null-parameter rebuild). Production reads nothing — the routing tests assert against this
    /// instead of inferring the parameter from step responses.</summary>
    internal double? AppliedSmoothingParameter
    {
        get
        {
            long bits = Interlocked.Read(ref _lastAppliedSmoothingBits); // one atomic read, used twice
            return bits == NoSmoothingBits ? null : BitConverter.Int64BitsToDouble(bits);
        }
    }

    // §13.3 item (1): the provider→filter VALUE path. The active filter's ONE adaptation-sensitive
    // parameter, composed against the CURRENT mode's baseline — the fill is a fraction OF the baseline, so
    // the baseline must be the active parameter's (OneEuroMinCutoff vs SmoothingFactor), resolved afresh on
    // every read. null = the knob no-ops: Direct has no smoothing parameter (mode is never auto-switched,
    // §5), and no provider means no adaptation at all.
    //
    // §13.3 SAFETY CLAMP: the provider's Smoothing policy carries the union superset [0, 20], but the
    // ACTIVE filter's own validated range is re-applied here — [0.1, 20] for One-Euro, [0, 1] for EMA.
    // Without it, a 0.1 Hz One-Euro baseline at the default fraction composes to 0.05 (and to exactly 0.0
    // at fraction 1.0), which OneEuroFilter's constructor rejects — a throw on the gaze thread, inside the
    // unguarded rebuild. The literals deliberately duplicate the CursorSettings guards (the FixationRadius
    // precedent); the assign-back oracle test fails the build if they drift. Only the FLOOR can engage:
    // smoothing is forgiving-only (parameter down, no tightening), so the composed value never exceeds the
    // baseline, which the setters keep inside the range.
    internal double? EffectiveSmoothingParameter()
    {
        if (_effectiveSettings is null)
            return null;
        return _settings.Cursor.FilterMode switch
        {
            GazeFilterMode.Direct => null,
            GazeFilterMode.Smoothed or GazeFilterMode.SmoothedWithSnapping => Math.Clamp(
                _effectiveSettings.Effective(AdaptationKnob.Smoothing, DwellContext.Other, _settings.Cursor.SmoothingFactor),
                0.0, 1.0),
            // OneEuro, plus the default arm mirroring GazeFilterFactory's default (OneEuroFilter) so a
            // future enum member can never compose an EMA baseline onto a One-Euro filter.
            GazeFilterMode.OneEuro => Math.Clamp(
                _effectiveSettings.Effective(AdaptationKnob.Smoothing, DwellContext.Other, _settings.Cursor.OneEuroMinCutoff),
                0.1, 20.0),
            _ => Math.Clamp(
                _effectiveSettings.Effective(AdaptationKnob.Smoothing, DwellContext.Other, _settings.Cursor.OneEuroMinCutoff),
                0.1, 20.0),
        };
    }
```

- [ ] **Step 4: Run the new tests and the full suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngineSmoothingRoutingTests"`
Expected: `Bestanden!`, 17 tests.

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: `Bestanden!`, `Fehler: 0`, `gesamt: 1122` (1105 + 17).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs tests/EyeCtrl.Core.Tests/GazeEngineSmoothingRoutingTests.cs
git commit -m "feat(gaze): resolve the effective smoothing parameter for the active filter

EffectiveSmoothingParameter composes the Smoothing fill against the CURRENT
mode's baseline and clamps to the ACTIVE filter's validated range — the §13.3
safety requirement: One-Euro's floor is 0.1, not 0, and an unclamped 0.0 would
throw from OneEuroFilter's constructor on the gaze thread. Direct no-ops.
AppliedSmoothingParameter records what the live filter runs at (Interlocked
bits: written on the gaze thread, compared on the dispatcher). Not wired yet."
```

---

### Task 3: Wire it — knob-aware request, the 1/s budget, and the effective rebuild

This is the behaviour-changing task. After it, a sustained Smoothing fill audibly (well, measurably) retunes the live filter.

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs` (handler subscription + `ApplyPendingInterpolatorRebuild` + call site)
- Test: `tests/EyeCtrl.Core.Tests/GazeEngineSmoothingRoutingTests.cs` (append)

**Interfaces:**
- Consumes: everything from Task 2.
- Produces: behavioural only. `ApplyPendingInterpolatorRebuild` gains a `long timestampMicroseconds` parameter (internal call site only).

- [ ] **Step 1: Write the failing tests**

Append to the `GazeEngineSmoothingRoutingTests` class (before the fakes region):

```csharp
    // --- Task 3: the wiring (behavioural, through SimulateGaze) ---

    [Fact]
    public void SmoothingFill_RetunesOneEuro_Live()
    {
        // Two identical engines; a full Smoothing fill on A only. After the next sample applies the
        // deferred rebuild, A's live filter must run at the EFFECTIVE minCutoff (5.0 − 0.50×5.0 = 2.5 Hz)
        // and lag a step MORE than B's baseline filter. End-to-end: Changed → knob-aware request →
        // gaze-thread apply → factory overload.
        var a = Make(GazeFilterMode.OneEuro);
        var b = Make(GazeFilterMode.OneEuro);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
        {
            a.TestGaze.SimulateGaze(500, 500, ts);
            b.TestGaze.SimulateGaze(500, 500, ts);
        }
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1 (seeded clock → first is immediate)
        b.TestGaze.SimulateGaze(500, 500, ts); // identical timestamp streams — One-Euro alpha is dt-dependent
        ts += 33_000;

        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);
        Check.That(b.Engine.AppliedSmoothingParameter).IsEqualTo(5.0);

        double lastA = 0, lastB = 0;
        a.Engine.CursorMoved += (p, _) => lastA = p.X;
        b.Engine.CursorMoved += (p, _) => lastB = p.X;
        a.TestGaze.SimulateGaze(600, 500, ts);
        b.TestGaze.SimulateGaze(600, 500, ts);

        Check.That(lastA).IsStrictlyLessThan(lastB); // the 2.5 Hz filter trails the step further behind
    }

    [Fact]
    public void SmoothingFill_RetunesEma_Live()
    {
        // Same for the EMA path: effective factor 0.5 − 0.50×0.5 = 0.25 lags far more than the baseline
        // (EMA has no speed adaptation, so the difference is large and unambiguous).
        var a = Make(GazeFilterMode.Smoothed);
        var b = Make(GazeFilterMode.Smoothed);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
        {
            a.TestGaze.SimulateGaze(500, 500, ts);
            b.TestGaze.SimulateGaze(500, 500, ts);
        }
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1
        b.TestGaze.SimulateGaze(500, 500, ts); // identical streams (EMA is dt-independent; kept for symmetry)
        ts += 33_000;

        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(0.25);

        double lastA = 0, lastB = 0;
        a.Engine.CursorMoved += (p, _) => lastA = p.X;
        b.Engine.CursorMoved += (p, _) => lastB = p.X;
        a.TestGaze.SimulateGaze(600, 500, ts);
        b.TestGaze.SimulateGaze(600, 500, ts);

        Check.That(lastA).IsStrictlyLessThan(lastB);
    }

    [Fact]
    public void NonSmoothingFill_DoesNotRebuildTheFilter()
    {
        // §13.3 item (2), the negative: Changed is payloadless and fires for EVERY knob's fill, but a
        // DwellTime fill must not spend a rebuild. The dwell-filled engine's step response must be
        // identical to the never-adapted reference (same sequence through the same, unchanged filter).
        var a = Make(GazeFilterMode.OneEuro);
        var b = Make(GazeFilterMode.OneEuro);
        a.Provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        double lastA = 0, lastB = 0;
        a.Engine.CursorMoved += (p, _) => lastA = p.X;
        b.Engine.CursorMoved += (p, _) => lastB = p.X;

        long ts = 0;
        for (int i = 0; i < 13; i++, ts += 33_000)
        {
            a.TestGaze.SimulateGaze(500, 500, ts);
            b.TestGaze.SimulateGaze(500, 500, ts);
        }
        a.TestGaze.SimulateGaze(600, 500, ts);
        b.TestGaze.SimulateGaze(600, 500, ts);

        Check.That(lastA).IsEqualTo(lastB);
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(5.0); // no rebuild happened
    }

    [Fact]
    public void SecondAdaptiveRebuild_DefersUntilOneSecondElapsed()
    {
        // §9.1 budget: adaptation can step the fill every 250 ms tick; the ADAPTIVE rebuild is rate-limited
        // to ~1/s and a pending request is DEFERRED, not dropped — the next sample after the interval
        // applies the then-current effective value. Rebuild #1 (fill 1.0 → 2.5 Hz) is immediate; a second
        // fill change (fill 0.5 → effective 5.0 − 0.50×0.5×5.0 = 3.75 Hz) within 1 s must not apply until
        // the interval elapses — at exactly the 1 s boundary it does. The first fill is set AFTER the
        // warmup loop: set earlier, the loop's ts=0 sample consumes rebuild #1 immediately and anchors the
        // budget clock at 0, so the "exactly 1 s" sample lands at elapsed 1.396 s and the boundary itself
        // is never exercised (round-2 review, finding 2).
        var a = Make(GazeFilterMode.OneEuro);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1 at ts = 396_000 (seeded clock → immediate)
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 0.5, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts += 100_000);     // elapsed exactly 100_000 → DEFERRED
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.TestGaze.SimulateGaze(500, 500, ts += 900_000);     // elapsed exactly 1_000_000 → applies (boundary)
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(3.75);
    }

    [Fact]
    public void ClearMomentaryFills_RestoresTheBaselineFilter()
    {
        // §8.6 escape: clearing the fills (toolbar reset / session boundary) must return the LIVE filter to
        // baseline — the handler sees effective (no fill) ≠ applied and requests the restore rebuild.
        var a = Make(GazeFilterMode.OneEuro);
        var b = Make(GazeFilterMode.OneEuro); // never adapted reference

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
        {
            a.TestGaze.SimulateGaze(500, 500, ts);
            b.TestGaze.SimulateGaze(500, 500, ts);
        }
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1
        b.TestGaze.SimulateGaze(500, 500, ts); // identical timestamp streams — One-Euro alpha is dt-dependent
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.Provider.ClearMomentaryFills();
        a.TestGaze.SimulateGaze(500, 500, ts += 2_000_000); // 2 s later — past the budget clock
        b.TestGaze.SimulateGaze(500, 500, ts);
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(5.0);

        double lastA = 0, lastB = 0;
        a.Engine.CursorMoved += (p, _) => lastA = p.X;
        b.Engine.CursorMoved += (p, _) => lastB = p.X;
        a.TestGaze.SimulateGaze(600, 500, ts += 33_000);
        b.TestGaze.SimulateGaze(600, 500, ts);

        // Both filters now run at 5.0 Hz from (within rounding of) the same steady state, so the step
        // responses agree to far below a pixel. Bit-equality is NOT asserted: A's filter was rebuilt
        // (warm-seeded) while B's is continuous, and float rounding along those two histories differs.
        Check.That(lastA).IsCloseTo(lastB, 1e-6);
    }

    [Fact]
    public void FilterModeSwitch_AppliesTheNewModesEffectiveParameter()
    {
        // A mode switch (baseline rebuild path, per-frame — not budget-limited) must also apply the
        // EFFECTIVE parameter of the NEW mode: the same fill composes against 0.5 → 0.25.
        var a = Make(GazeFilterMode.OneEuro);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.Settings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        a.TestGaze.SimulateGaze(500, 500, ts += 33_000);
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(0.25);
    }

    [Fact]
    public void AdaptiveRebuild_IsContinuous_ContinuesTheTrajectoryNoSnap()
    {
        // R1 continuity on the ADAPTIVE path. The engine drifts +10 px per sample (raw 560 by sample 18,
        // filtered X lagging behind raw at ≈550.5). Filling and pumping one more sample rebuilds the filter
        // BEFORE the sample is processed: the fresh 2.5 Hz filter is warm-seeded from the outgoing one, so
        // the output must continue the trajectory — strictly between the last filtered X and the raw 570.
        // A cold filter would snap to exactly 570 (a cold One-Euro's first call returns the raw point).
        // The applied-parameter assert first pins that the rebuild REALLY ran — without it this test passes
        // even when no rebuild happens at all (review round 1, finding 5).
        var a = Make(GazeFilterMode.OneEuro);
        double lastX = 0;
        a.Engine.CursorMoved += (p, _) => lastX = p.X;

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);
        for (int i = 1; i <= 6; i++, ts += 33_000) a.TestGaze.SimulateGaze(500 + 10 * i, 500, ts);
        double before = lastX;

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(570, 500, ts); // rebuild consumes at the top; THIS sample runs the new filter

        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5); // the rebuild really ran
        Check.That(lastX).IsStrictlyGreaterThan(before); // no backwards jump (warm seed worked)
        Check.That(lastX).IsStrictlyLessThan(570.0);     // no snap to raw (cold filter would return 570)
    }

    [Fact]
    public void OneEuroFloorTrap_EndToEnd_ConstructsAndApplies_NoThrow()
    {
        // §13.3's mandated end-to-end discharge: a 0.1 Hz baseline at fraction 1.0 composes to exactly 0.0
        // pre-clamp, which OneEuroFilter's ctor rejects — unguarded, that throw lands on the gaze thread
        // inside ApplyPendingInterpolatorRebuild. The clamp must hold through the REAL rebuild path
        // (factory construction), not just at the value level. SimulateGaze is synchronous, so reaching
        // the assertions at all proves no exception escaped OnGazePoint.
        var a = Make(GazeFilterMode.OneEuro);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);

        a.Settings.Cursor.OneEuroMinCutoff = 0.1;
        a.Settings.Adaptation.SmoothingMoreMaxFraction = 1.0;
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild consumes here (baseline edit + fill both pending)

        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(0.1);
    }

    [Fact]
    public void GazeClockRestart_AppliesImmediately_ResyncsTheBudgetClock()
    {
        // A device reconnect can move the gaze clock backwards — the same jump class the telemetry sink's
        // resync guard handles (TelemetryClockRestartTests). Without the signed elapsed guard, a restart
        // makes elapsed hugely negative, it reads "< MinInterval", and every adaptive rebuild is deferred
        // for the length of the old epoch's uptime. Rebuild #1 at 1 h of uptime; the clock restarts near
        // zero with a changed fill — the rebuild must apply on the very next sample.
        var a = Make(GazeFilterMode.OneEuro);
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        long ts = 3_600_000_000; // 1 h of device uptime
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);
        a.TestGaze.SimulateGaze(500, 500, ts);
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 0.5, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, 50_000); // reconnect: gaze clock back near zero
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(3.75);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngineSmoothingRoutingTests"`

Expected: `Fehler!` — 8 of the 26 tests in this file fail (`AppliedSmoothingParameter` never leaves the construction value: the handler still only requests the tolerance apply, and the rebuild still composes baseline settings). `NonSmoothingFill_DoesNotRebuildTheFilter` **passes already and must** — it is the negative guard, and pre-implementation nothing rebuilds, which is exactly what it asserts. The 17 Task-2 tests stay green.

- [ ] **Step 3: Rewire the handler subscription**

In `src/EyeCtrl.Core/GazeEngine.cs`, in the constructor, replace:

```csharp
        if (_effectiveSettings is not null)
        {
            _effectiveSettingsChangedHandler = RequestFixationReconfiguration;
            _effectiveSettings.Changed += _effectiveSettingsChangedHandler;
        }
```

with:

```csharp
        if (_effectiveSettings is not null)
        {
            _effectiveSettingsChangedHandler = OnEffectiveSettingsChanged;
            _effectiveSettings.Changed += _effectiveSettingsChangedHandler;
        }
```

- [ ] **Step 4: Add the fields, the handler, and rewrite the apply**

**(a)** Next to the other reconfiguration fields (after `_fixationReconfigureRequested`, around line 44), add:

```csharp
    // §9.1 rebuild budget: at most one ADAPTIVE rebuild per second (baseline slider rebuilds are exempt —
    // they already coalesce to one per gaze frame). Seeded at long.MinValue/2 so the FIRST adaptive rebuild
    // of a session is never deferred: a 0 seed would stall it for a full second of gaze-clock time (test
    // clocks start near zero), and long.MinValue itself would overflow the subtraction.
    // See ApplyPendingInterpolatorRebuild for the restart guard.
    private const long MinAdaptiveRebuildIntervalMicroseconds = 1_000_000;
    private long _lastAdaptiveRebuildTimestampMicroseconds = long.MinValue / 2;
    // Written on the dispatcher thread (the provider's Changed handler), read+cleared on the gaze thread —
    // same defer-onto-the-gaze-thread contract as _interpolatorRebuildRequested.
    private volatile bool _adaptiveRebuildRequested;
```

**(b)** Next to `RequestFixationReconfiguration` (around line 269), add:

```csharp
    // Runs on the dispatcher thread (the provider's Changed fires there — controller tick, toolbar reset,
    // per-knob disable). Besides the in-place tolerance apply, an effective SMOOTHING change needs the
    // interpolator REBUILT. Changed is payloadless and fires for every knob's fill (§13.3 item 2), so the
    // request is knob-aware by comparison: only when the live effective parameter differs from the one the
    // filter was last built with. Baseline smoothing edits take the separate CursorSettings path, which
    // rebuilds per gaze frame as before.
    private void OnEffectiveSettingsChanged()
    {
        RequestFixationReconfiguration();
        if (EffectiveSmoothingParameter() is { } parameter
            && BitConverter.DoubleToInt64Bits(parameter) != Interlocked.Read(ref _lastAppliedSmoothingBits))
            _adaptiveRebuildRequested = true;
    }
```

**(c)** Replace `ApplyPendingInterpolatorRebuild` and its call site. In `OnGazePoint`, change:

```csharp
        ApplyPendingFixationReconfiguration();
        ApplyPendingInterpolatorRebuild();
```

to:

```csharp
        ApplyPendingFixationReconfiguration();
        ApplyPendingInterpolatorRebuild(gaze.TimestampMicroseconds);
```

and replace the method (keep the existing leading comment paragraphs, then continue):

```csharp
    // Runs on the gaze thread, before Process below, so CopyStateFrom reads the outgoing interpolator on the
    // only thread that mutates it. Same contract as ApplyPendingFixationReconfiguration — one convention for
    // every reconfiguration of live gaze-pipeline state. (This one still rebuilds: IGazeFilter exposes no way
    // to retune in place, so the fresh-filter + WarmStartFromCurrentState path stands — see design §5 R1.)
    //
    // The rebuild composes the ACTIVE filter's EFFECTIVE smoothing parameter (§13.3 routing), re-read HERE
    // so a coalesced burst applies the latest value, not the one that set the flag.
    private void ApplyPendingInterpolatorRebuild(long timestampMicroseconds)
    {
        if (!_interpolatorRebuildRequested && !_adaptiveRebuildRequested)
            return;

        // §9.1 rebuild budget: the controller can step the smoothing fill on every 250 ms tick, so an
        // ADAPTIVE rebuild is rate-limited to one per second — a pending request is DEFERRED, not dropped:
        // the next sample after the interval applies the then-current effective value, so the final value
        // always lands. Baseline (settings-slider) rebuilds are exempt and satisfy an outstanding adaptive
        // request too (they apply the effective parameter below); any rebuild that runs resets the clock.
        // A NEGATIVE elapsed is a gaze-clock restart (device reconnect — the same backward jump the
        // telemetry sink's resync guard handles, TelemetryClockRestartTests): resync rather than defer for
        // the length of the old epoch.
        if (!_interpolatorRebuildRequested)
        {
            long elapsed = timestampMicroseconds - _lastAdaptiveRebuildTimestampMicroseconds;
            if (elapsed >= 0 && elapsed < MinAdaptiveRebuildIntervalMicroseconds)
                return;
        }
        _interpolatorRebuildRequested = false;
        _adaptiveRebuildRequested = false;
        _lastAdaptiveRebuildTimestampMicroseconds = timestampMicroseconds;

        double? parameter = EffectiveSmoothingParameter();
        var fresh = new GapInterpolator(
            GazeFilterFactory.Create(_settings.Cursor, parameter),
            _settings.Cursor.MaxGapInterpolationMs);
        fresh.CopyStateFrom(_gapInterpolator);
        fresh.WarmStartFromCurrentState(); // R1: seed the fresh filter — no cursor jump
        _gapInterpolator = fresh;

        // Publish the applied parameter (the sentinel for null — Direct/no-provider — so a runtime switch
        // into Direct reports null honestly). Then RE-VALIDATE: a provider write that landed between the
        // effective read at the top and this exchange compared against the STALE applied bits and may have
        // skipped its request — e.g. the §8.6 toolbar reset clearing fills while an older fill's rebuild was
        // in flight, which would pin the filter at a stale value with no outstanding request. The write
        // orderings close the window (the dispatcher writes fills before reading bits; the gaze thread
        // exchanges bits before re-reading), so at least one side observes the other; any re-request goes
        // back through the budget gate (deferred, not dropped).
        Interlocked.Exchange(
            ref _lastAppliedSmoothingBits,
            parameter is { } applied ? BitConverter.DoubleToInt64Bits(applied) : NoSmoothingBits);
        if (parameter is { } published && EffectiveSmoothingParameter() is { } current && current != published)
            _adaptiveRebuildRequested = true;
    }
```

- [ ] **Step 5: Run the new tests, then the whole routing file**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngineSmoothingRoutingTests"`
Expected: `Bestanden!`, 26 tests (17 + 9).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: `Bestanden!`, `Fehler: 0`, `gesamt: 1131` (1122 + 9). If anything outside this file fails, stop and diff — the most likely cause is a test that asserted the old handler wiring (`RequestFixationReconfiguration` as the `Changed` target) somewhere in the App tests; nothing in Core should.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs tests/EyeCtrl.Core.Tests/GazeEngineSmoothingRoutingTests.cs
git commit -m "feat(adaptation): route the Smoothing knob to the live gaze filter

The provider's Changed handler now requests an interpolator rebuild only when
the effective active-filter parameter actually differs from the applied one
(knob-aware — Changed is payloadless), and the rebuild composes that effective
value instead of the baseline. Adaptive rebuilds are rate-limited to ~1/s per
the §9.1 budget, deferring a pending request rather than dropping it; a gaze-
clock restart (device reconnect) resyncs the budget instead of deferring for
the old epoch's uptime. Manual settings edits keep per-frame rebuilds; Direct
no-ops; the applied parameter is revalidated after publish so a racing fill
change (e.g. the toolbar reset) can never leave the filter stale.

A sustained SignalNoise/PoorTracking fill now retunes the filter — warm-started,
so continuous (R1) — clamped to the active filter's range."
```

---

### Task 4: Provider hardening + closing the §13.3 open decisions

The `ConcurrentDictionary` swap resolves §13.3 item (4) (the cross-thread strategy — make reads safe rather than snapshot), and the doc updates record the two decisions (fraction model + 0.50 ceiling confirmed; Direct no-op). Pure hardening/documentation: the existing provider tests are the proof.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs`
- Modify: `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`

**Interfaces:**
- Consumes/Produces: no signature changes.

- [ ] **Step 1: Swap the fill store to `ConcurrentDictionary`**

In `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs`, add `using System.Collections.Concurrent;` to the using block, and replace:

```csharp
    private readonly Dictionary<(AdaptationKnob Knob, DwellContext Context), (double ForgivingFill, double TighteningFill)> _fills = new();
```

with:

```csharp
    // ConcurrentDictionary resolves §13.3 item (4): fills are written on the dispatcher thread (controller
    // tick) and read on the gaze thread (GazeEngine's deferred reconfiguration reads Effective there), and
    // a first-key insert on the writer can resize a plain Dictionary while the reader is inside TryGetValue.
    // Single-WRITER semantics are unchanged (the controller is serialized on the dispatcher); only the
    // cross-thread READ becomes safe. SetMomentaryFill/ClearMomentaryFills still raise Changed on the
    // writer thread, so consumer-side coalescing is unaffected.
    private readonly ConcurrentDictionary<(AdaptationKnob Knob, DwellContext Context), (double ForgivingFill, double TighteningFill)> _fills = new();
```

Also update the class-level doc: the sentence ending "the other three knobs are composable but have no consumer yet (design §9)." becomes:

```
    /// the dwell and geometry knobs are composable but have no consumer yet (design §13.3); the smoothing
    /// knob is routed by <c>GazeEngine</c>'s adaptive interpolator rebuild (smoothing-routing slice,
    /// 2026-09-01), which clamps the composed value to the active filter's validated range.
```

And in the `Policies` table, the Smoothing entry's comment `per-mode range re-applied at smoothing routing` becomes `per-mode range re-applied by GazeEngine.EffectiveSmoothingParameter (smoothing routing, 2026-09-01)`.

- [ ] **Step 2: Resolve the PROVISIONAL note on `SmoothingMoreMaxFraction`**

In `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`, replace the `SmoothingMoreMaxFraction` doc:

```csharp
    /// <summary>Maximum "more smoothing" shift of the active filter parameter as a fraction of baseline.
    /// Default 0.50 — PROVISIONAL: §13.1 gives no smoothing percentage, and fraction-of-baseline is a
    /// filter-dependent unit (One-Euro <c>OneEuroMinCutoff</c> Hz vs EMA <c>SmoothingFactor</c>, both
    /// DECREASED for more smoothing). 1b-④ (the first consumer) must revisit whether a fraction or an
    /// absolute per-filter delta is the right model before relying on this number.</summary>
```

with:

```csharp
    /// <summary>Maximum "more smoothing" shift of the active filter parameter as a fraction of baseline.
    /// Default 0.50 — CONFIRMED 2026-09-01 by the smoothing-routing slice (the knob's first consumer):
    /// fraction-of-baseline is kept, and the ceiling holds — One-Euro 5.0 → 2.5 Hz (fixation τ ≈ 32 →
    /// 64 ms), EMA 0.5 → 0.25 (τ ≈ 48 → 116 ms at every speed). Both DECREASED for more smoothing. The
    /// composed value is clamped to the ACTIVE filter's validated range by <c>GazeEngine</c>
    /// (One-Euro floor 0.1 Hz — the fraction model is range-safe only for EMA; design §13.3).</summary>
```

- [ ] **Step 3: Verify the suite is green**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: `Bestanden!`, `Fehler: 0`, `gesamt: 1131` (1130 passed, 1 skipped) — unchanged from Task 3. A pure hardening + doc change cannot move a count; if one fails, the swap was not faithful.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs src/EyeCtrl.Core/Settings/AdaptationSettings.cs
git commit -m "refactor(adaptation): make the fill store cross-thread readable; close the §13.3 smoothing questions

ConcurrentDictionary: fills are written on the dispatcher and read on the gaze
thread (the deferred reconfiguration), and a first-key insert can resize a
plain Dictionary mid-read. Single-writer semantics unchanged.

SmoothingMoreMaxFraction 0.50 confirmed now that the knob has its first
consumer; Direct no-ops. Doc-only otherwise."
```

---

### Task 5: Spec amendment + full verification

**Files:**
- Modify: `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md`

- [ ] **Step 1: Amend §13.3's smoothing bullet**

In the "Smoothing." bullet (the one listing the four missing things), append after the list:

```markdown
  **(Resolved 2026-09-01 — smoothing-routing slice, plan `2026-09-01-smoothing-knob-routing.md`.)**
  (1) the value path is a `GazeFilterFactory.Create(settings, effectiveSmoothingParameter)` overload — the
  *active* filter's one adaptation-sensitive parameter, `null` = baseline-only; (2) knob-aware handling is a
  comparison of the live effective parameter against the applied one (`Interlocked` bit round-trip between
  the dispatcher-thread handler and the gaze-thread apply), **revalidated after publish** so a racing fill
  write (e.g. the §8.6 toolbar reset's `ClearMomentaryFills`) can never leave the filter at a stale value
  with no outstanding request; (3) the §9.1 budget is a gaze-thread minimum
  interval of 1 s for *adaptive* rebuilds with the pending request **deferred, not dropped** (manual slider
  rebuilds keep per-frame coalescing — the budget exists for indefinite adaptation ramps; a baseline
  rebuild satisfies an outstanding adaptive request and resyncs the budget clock; a gaze-clock
  restart / device reconnect is treated as budget-satisfied — resync, never defer for the old epoch's
  uptime); (4) the
  cross-thread strategy is a `ConcurrentDictionary` fill store (read-safe, still single-writer) rather than
  a snapshot. The §13.3 clamp is implemented in `GazeEngine.EffectiveSmoothingParameter`
  (`[0.1, 20]` One-Euro / `[0, 1]` EMA, setter-as-oracle test), and `Direct` no-ops. The
  `SmoothingMoreMaxFraction` comfort ceiling is resolved at **0.50, kept** (One-Euro 5.0 → 2.5 Hz ≈ τ
  32 → 64 ms; EMA 0.5 → 0.25 ≈ τ 48 → 116 ms). **Discovered leftover (left open, minor):** editing a
  band *fraction* in settings does not raise the provider's `Changed` (it fires only on fill
  set/clear), so a ceiling edit while a fill is stored refreshes the applied value only at the next
  fill change — bounded staleness, self-healing, no safety impact (the stale value is still inside
  the old band); owner: a future settings-notification slice.
```

- [ ] **Step 2: Amend the "Also still open" paragraph**

The paragraph beginning "**Also still open** (owned by the smoothing-routing slice): whether `SmoothingMoreMaxFraction` stays at 0.50..." — replace its opening with:

```markdown
**Resolved by the smoothing-routing slice (2026-09-01)** (formerly "Also still open", owned by the
smoothing-routing slice): `SmoothingMoreMaxFraction` **stays at 0.50** now that smoothing answers only to
noise evidence — the numbers above; and `GazeFilterMode.Direct` **no-ops** (`DirectFilter` takes no
smoothing parameter; `EffectiveSmoothingParameter` returns `null` and no rebuild is requested from the
adaptation path — mode is never auto-switched, §5).
```

- [ ] **Step 3: Amend §14 and the Phase-1c status**

In §14, the bullet beginning "`SmoothingMoreMaxFraction` fraction-vs-absolute model" — append at the end:
`(The 0.50 comfort ceiling question is likewise resolved: kept — see §13.3, smoothing-routing slice 2026-09-01.)`

In §13's Phase 1c paragraph, after "**A hard release gate before Phase 2:**" sentence, insert:

```markdown
**Status: smoothing routed 2026-09-01** (`GazeEngine` composes the effective active-filter parameter into
the rebuild, clamped, budgeted); **dwell and geometry routing still open — the Phase-2 gate remains closed.**
```

- [ ] **Step 4: Run the full Core suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: `Bestanden!`, `Fehler: 0`, `gesamt: 1131` (1130 passed, 1 skipped) — the 1100 baseline **+ 31**:

| Task | Delta | From |
|---|---|---|
| 1 | **+5** | factory: 2 One-Euro + 2 EMA + 1 Direct |
| 2 | **+17** | 3 single facts + 2-row baseline theory + 7-row clamp theory + 2-row oracle theory + 2 ctor facts + 1 mode-switch fact |
| 3 | **+9** | 2 live-retune + 1 knob-aware negative + 1 budget + 1 clock-restart + 1 clear-restores + 1 mode-switch + 1 continuity + 1 floor-trap end-to-end |

A different total means a theory row was duplicated or dropped — find it rather than adjusting this number.

- [ ] **Step 5: Run the ViewModels suite**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests`
Expected: `Bestanden!`, `Fehler: 0`. Nothing here touches adaptation; a failure means something unexpected.

- [ ] **Step 6: Run the App headless subset — the one CI runs**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Bestanden!`, `Fehler: 0`. Do **not** run App.Tests without the filter (the `Category=UI` tests launch the real application and seize the user's only input channel). If anything here asserts the old `Changed → RequestFixationReconfiguration` wiring or "smoothing is unrouted", that is an expected update, not a regression — verify the assertion is genuinely stale before editing it.

- [ ] **Step 7: Run the benchmark-correctness suite**

Run: `dotnet test tests/EyeCtrl.Benchmarks.Tests`
Expected: `Bestanden!`, `Fehler: 0`. The benchmark sources are untouched, but `GazePipelineWithAdaptationBenchmarks` constructs a real `GazeEngine`, so this is cheap insurance that the fixtures still compile and hold. Do **not** run the benchmarks themselves (minutes each; per-sample cost gains one `volatile` read).

- [ ] **Step 8: Confirm the change surface**

Run: `git diff --stat main`
Expected: exactly eight files — this plan (committed in Task 1 Step 0), `GazeFilterFactory.cs` (Task 1), `GazeEngine.cs` (Tasks 2–3), `EffectiveSettingsProvider.cs` + `AdaptationSettings.cs` (Task 4), two new test files, and the spec doc. **No** `src/EyeCtrl.App/`, `src/EyeCtrl.Platform/`, `AdaptationController.cs`, or `StruggleMonitor.cs` changes: the controller and monitor already push the right fills; this slice only made them observable.

- [ ] **Step 9: State what was and was not verified (for the PR description)**

Verified: the routing chain end-to-end with a synchronous fake provider; the clamp at both filter floors including the `0.1 Hz` @ fraction 1.0 constructor-throw case; the knob-aware negative; the 1/s deferral including the exact-boundary apply; R1 continuity on the adaptive path; disabled/zero-fill ⇒ exact baseline (the locked exit invariant, via existing provider tests plus `EffectiveParameter_Disabled_…`).
Not verified: real-tracker feel (the px/s jitter thresholds feeding SignalNoise remain uncalibrated starting guesses, §13.2); behaviour under a genuinely concurrent controller tick + gaze stream (the tests are single-threaded by design; the `ConcurrentDictionary`/`Interlocked` choices are reasoned, not race-tested).

- [ ] **Step 10: Commit the spec amendment**

```bash
git add docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md
git commit -m "docs(spec): record the smoothing-routing slice against §13.3/§14"
```

- [ ] **Step 11: Push and open the PR — ONLY after the repo owner's explicit OK**

**Do not run the command below until the repo owner has explicitly approved the push.** Ask first; an approval to execute the plan is not an approval to publish. If no answer, stop here and report — the branch is complete locally.

```bash
git push -u origin feat/struggle-smoothing-routing
```

Then open a PR against `main` titled `feat(adaptation): route the Smoothing knob to the live gaze filter (Phase 1c, slice 1/3)`, covering: the lag→abandonment loop foreclosures already argued in §13.3, the active-filter clamp as a gaze-thread-crash prevention, the 1/s deferral budget (with clock-restart resync), the ConcurrentDictionary rationale, and that dwell + geometry routing (and therefore the Phase-2 gate) remain open.

---

## Notes for the implementer

- **Do not run the app or simulate input.** Headless verification only; see Global Constraints.
- **If a step's expected output does not match, stop rather than adapting the plan.** The commit-per-task structure makes each failure diagnosable in isolation; a surprise usually means the source drifted from what this plan was written against (`main` at `e6c28176`).
- **The tolerance path is deliberately untouched.** `OnEffectiveSettingsChanged` still calls `RequestFixationReconfiguration()` first; `ApplyPendingFixationReconfiguration` keeps its in-place, idempotent write. If any tolerance test moves, you changed something you should not have.
- **Do not "simplify" the two-flag structure into one.** The baseline flag (`_interpolatorRebuildRequested`) and the adaptive flag exist so the §9.1 budget applies only to adaptation ramps; merging them would rate-limit manual slider edits for no specified reason.
- **Known limitation, deliberately left open:** changing a band *fraction* (e.g. `SmoothingMoreMaxFraction`) while a fill is stored does not raise the provider's `Changed`, so the applied filter value refreshes only at the next fill change (self-healing, bounded by the old band — no safety impact). The budget test therefore triggers its second rebuild with a *fill* change, not a fraction change. A settings-notification wiring is future work, recorded in the Task 5 spec amendment.
- **Phase-2 gate:** after this slice, exactly one of three routing slices is done. Do not let the PR description claim Phase 1c is complete.
