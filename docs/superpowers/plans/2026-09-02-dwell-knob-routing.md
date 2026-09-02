# Dwell-Knob Routing Implementation Plan (Phase 1c, slice 2 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route the `AdaptationKnob.DwellTime` momentary fill to every live dwell surface — via a detector registry both remaining routing slices can reuse, per-target baseline composition, and per-surface validated ranges — state-preserving, identity-restoring, and inert while adaptation is disabled.

**Architecture:** A Core-side registry holds owner-keyed routing entries (context + validated range + an *applier delegate* that pushes a baseline→effective map onto a detector's targets). A small App-side router subscribes `EffectiveSettingsProvider.Changed` and the registry's `RegistrationChanged`, and applies one map per entry: `clamp(round(Effective(DwellTime, Other, baseline)), min, max)`. `DwellTarget` gains a retained dwell-time baseline (the `_baseRadius` pattern) and `DwellDetector` gains a `SetEffectiveDwellTime(map)` API that re-adds the `UpdateTargetDwellTime` semantics the 2026-08-29 simplification pass deleted. The Keyboard's hand-rolled key tracker (not a `DwellDetector`) composes its effective dwell on `Configure` and on `Changed` directly.

**Tech Stack:** C# / .NET 10, xUnit v3, NFluent assertions (`Check.That(...)`, **not** `Assert.*` — wired globally in `tests/Directory.Build.props`, which also supplies global `using NFluent;` and `using Xunit;`).

---

## Global Constraints

- **Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §5 (value model, "surfaces read effective values at (re)creation", state-preserving updates, in-progress dwell C4), §7 (shorter/longer dwell are *separately detected* responses), §13.1 (band widths; per-surface stricter floors "re-applied at dwell routing"), §13.3 (this slice's mandate: application-level discovery + registry shared with the geometry slice; `DwellTarget` dwell-time baseline). Read §13.3's "Dwell and geometry" bullet first.
- **This is slice 2 of the Phase-1c sequence (§13.3: smoothing → dwell → geometry).** Do NOT route geometry here; do not change `AdaptationController` or `StruggleMonitor` (fills are already pushed, globally under `DwellContext.Other`). The Phase-2 gate stays open until geometry lands.
- **Never run the application or any `Category=UI` test.** The repo owner controls their PC solely by eye tracking; launching it seizes their only input channel. Everything here is verifiable headlessly.
- **One test suite at a time**, sequential (named pipes are machine-global).
- **German test output.** Failures read `Fehler <name>`; the summary reads `Bestanden!` / `Fehler!` with `Fehler: N, erfolgreich: M, gesamt: T`. Read the summary line; grepping "Failed" silently reports green.
- Baselines at `main` = `0cbdfc0d`: **Core 1132 / ViewModels 293 / App (Category!=UI) 721 / Benchmarks.Tests 27**, all green. Different starting counts ⇒ stop and report.
- NFluent only; no mocking library (private fakes); file-scoped namespaces; Allman braces; `_camelCase` fields; nullable enabled. `InternalsVisibleTo` → `EyeCtrl.Core.Tests` exists (`EyeCtrl.Core.csproj`).
- **If a step's expected output does not match, stop rather than adapting.** Anchors reference `main` at `0cbdfc0d`; drift ⇒ report BLOCKED with what you saw.

---

## Context you need before starting

**What exists.** The smoothing slice (PR #131) routed the second knob: `GazeEngine` composes `Effective(AdaptationKnob.Smoothing, …)` into its interpolator rebuild. `AdaptationController` already pushes a `DwellTime` fill (forgiving −0.40×baseline on sustain-failure; the one quarantined tightening +0.50× on accidental-activation) into `EffectiveSettingsProvider`, which raises a payloadless `Changed` per fill write — and nothing reads it. The provider's `DwellTime` policy clamps to the union `[100, 2000]` of the eleven `DwellSettings` dwell guards.

**Three facts discovered during planning that shape this plan:**

1. **`DwellDetector.UpdateTargetDwellTime` does not exist on `main`.** Phase 1a-⑤ added it; the 2026-08-29 simplification pass deleted it as unused (plan `2026-08-29-simplification-pass-2.md`, "delete `UpdateTargetDwellTime`"), together with its semantics test. This slice re-adds the *semantics* under a routing-shaped API (`SetEffectiveDwellTime(map)`): set the dwell time WITHOUT touching `EnteredTimestamp`/`IsGazeInside`, so the next `Update` re-evaluates elapsed against the new threshold — shortening mid-dwell can activate on the very next sample; lengthening keeps accumulated progress.
2. **Dwell-time baselines are per-target and heterogeneous.** Most surfaces bake a `DwellSettings` property into each `DwellTarget` at creation; the EdgeButtonBar's targets each carry their *own* per-button `DwellMs` (from different settings properties); Notes hard-codes 600 ms; EyeCare's targets carry `ActivationButtonDwellMs` baselines (a Standard-family, user-settable guard — the 700 at its DI site is merely the setting's default); ActionMenu receives a per-message `DwellTimeMs`. So the baseline must be retained **per target** (exactly the `_baseRadius` pattern) and the fill composed per target — never per detector.
3. **Two surfaces do not fit the "UI-thread detector" default.** `ExternalDwellService` serializes ALL detector access under its own `_stateLock` on the gaze thread — a dispatcher-thread push would race its `Update`. And the Keyboard's key dwell is not a `DwellDetector` at all: `KeyboardViewModel.ProcessKeyGaze` hand-rolls nearest-key tracking with `_keyDwellMs` and an `_nearestKeyEnteredTimestamp` (an EnteredTimestamp pattern — so changing `_keyDwellMs` mid-dwell re-evaluates for free on the next sample).

**Design decisions (plan-level; the review rounds may overturn them):**

- **Owner-keyed applier entries, not raw detectors.** The registry stores `DwellRoutingRegistration(context, minMs, maxMs, Apply)` keyed by the owning object. `Apply` is a delegate: for every surface except External it is `DwellRoutingRegistration.ForDetector(detector, …)` → `detector.SetEffectiveDwellTime(map)`; External passes `map => lock (_stateLock) _detector.SetEffectiveDwellTime(map)`. This keeps `DwellDetector`'s single-thread contract intact per owner and gives the geometry slice the same discovery mechanism.
- **Per-surface validated ranges (§13.1 "stricter feature floor… re-applied at dwell routing").** The provider clamps to the union `[100, 2000]`; the registry re-applies the surface's own guard family so an effective value never exceeds what the surface's own settings would allow: `Standard` = `[200, 2000]` (nine guards), `Keyboard` = `[100, 1500]` (KeyboardKeyDwellMs, KeyboardPredictionDwellMs — the emoji grid and the prediction bar use these), `External` = `[200, 3000]` (ExternalDwellSettings.DefaultDwellMs — **not** Standard: a legal 2500 ms baseline identity-restored through `[200, 2000]` would be truncated to 2000). Notes hard-codes 600 (no settings guard, but inside Standard); EyeCare's `ActivationButtonDwellMs` baselines are Standard-family by construction. Constants live in `AdaptationDwellRanges` with a setter-as-oracle drift test (the FixationRadius precedent).
- **Identity-restore, not null-map teardown.** When adaptation is disabled or fills clear, `Effective` returns the baseline; `clamp(round(baseline)) == baseline` (baselines are in-range ints), so the same map path restores exact baselines. No special-casing.
- **No budget machinery.** Unlike smoothing (filter rebuild), the dwell apply is in-place and idempotent against the retained baseline — the spec's tolerance precedent ("applied in place… a per-tick step is free and lossless") applies. `Changed` fires ≤ once per knob per 250 ms tick; applying the dwell map on every `Changed` (even one caused by a tolerance-only fill) is a no-op rewrite.
- **Born-effective targets.** `AddTarget` applies the detector's *current* stored map to the new target, so surfaces that recreate targets per show (action menu, banner, edge bar, …) never serve a stale baseline-held target after a fill exists. This is the spec's "surfaces read effective values at (re)creation" implemented once, in the detector.
- **Settings is never registered** (the remedy surface must not adapt — spec §2). Its four detectors stay untouched.
- **The router must be eagerly constructed** (it subscribes in its ctor; DI singletons are lazy). The App wiring resolves it once at startup, next to the other subscribe-in-ctor services.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/EyeCtrl.Core/Processing/DwellTarget.cs` | Per-target dwell baseline | Add `_baseDwellTimeMicroseconds`, `BaseDwellMs`, `ApplyEffectiveDwellMs` |
| `src/EyeCtrl.Core/Processing/DwellDetector.cs` | Map application | Add `SetEffectiveDwellTime(map)`; `AddTarget` applies stored map |
| `src/EyeCtrl.Core/Adaptation/AdaptationDwellRanges.cs` | **New.** Guard-family ranges | Create |
| `src/EyeCtrl.Core/Adaptation/DwellRoutingRegistration.cs` | **New.** Entry + default applier | Create |
| `src/EyeCtrl.Core/Adaptation/DwellDetectorRegistry.cs` | **New.** `IDwellDetectorRegistry` + impl | Create |
| `src/EyeCtrl.Core/GazeEngine.cs` | PR-131 review nit | Constructor ordering (Task 6) |
| `src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs` | Base registration | Optional registry param; register/unregister |
| `src/EyeCtrl.ViewModels/<9 DI-constructed subclasses>.cs` | Pass-through | Optional registry param each (ScrollOverlay exempt) |
| `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` | Hand-rolled tracker | Effective key dwell (Task 5) |
| `src/EyeCtrl.App/Services/DwellTimeRouter.cs` | **New.** Changed → apply | Create |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | DI (2 new + 5 factory args) | Registry, router |
| `src/EyeCtrl.App/App.xaml.cs` | Eager router resolve | One line (Task 4) |
| `src/EyeCtrl.App/Overlays/EmojiGridManager.cs`, `KeyboardWindow.xaml.cs`, `EyeCareBannerWindow.xaml.cs`, `OverlayManager.cs` | Registration pass-through | Optional registry param chain |
| `src/EyeCtrl.App/Services/NoteManager.cs`, `ManualSelectionDwellService.cs`, `ExternalDwellService.cs` | Registration | Register (External: locking applier) |
| `tests/…/Processing/DwellTargetUpdateTests.cs` | Restored + new semantics | Re-add deleted fact (map form), +3 |
| `tests/…/Adaptation/DwellDetectorRegistryTests.cs` | **New.** Registry + ranges | Create |
| `tests/EyeCtrl.ViewModels.Tests/Adaptation/…` | Base registration | +2; guard test update |
| `tests/EyeCtrl.ViewModels.Tests/KeyboardEffectiveDwellTests.cs` | **New.** Tracker | Create |
| `tests/EyeCtrl.App.Tests/Services/DwellTimeRouterTests.cs` | **New.** Router | Create; +1 in ExternalDwellServiceTests |
| spec `…self-adaptation-design.md` | Record | §13.3 dwell resolution (Task 7) |

---

### Task 1: Core substrate — per-target baseline + `SetEffectiveDwellTime`

Behaviour-neutral: no production caller sets a map yet. Re-adds the deleted semantics.

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/DwellTarget.cs`
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs` (re-add + extend)

**Interfaces:**
- Produces:
  - `DwellTarget`: `internal int BaseDwellMs { get; }`, `internal void ApplyEffectiveDwellMs(int effectiveMs)` (idempotent — derives from the retained baseline each call, never compounds).
  - `DwellDetector`: `public void SetEffectiveDwellTime(Func<int, int>? mapBaselineMsToEffectiveMs)` — stores the map and applies it to every current target; `AddTarget` applies the stored map to each newly added target.

- [ ] **Step 1: Write the failing tests**

In `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs`, add to the existing class (the first fact restores, in map form, the semantics deleted by the simplification pass — its original body is the reference):

```csharp
    [Fact]
    public void SetEffectiveDwellTime_ReEvaluatesWithoutResettingProgress()
    {
        // Restored from the simplification pass's deletion (UpdateTargetDwellTime_ReEvaluatesWithout-
        // ResettingProgress): shortening mid-dwell must NOT clear EnteredTimestamp — the next Update
        // re-evaluates elapsed against the NEW threshold, so 160 ms of a 100 ms dwell activates.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(100, 100), 0);        // enter (EnteredTimestamp = 0)
        d.Update(new PhysicalPoint(100, 100), 150_000);  // 150 ms of 300 ms — not yet activated
        Check.That(activated).IsFalse();

        d.SetEffectiveDwellTime(_ => 100);               // shorten to 100 ms; must NOT reset the timer
        d.Update(new PhysicalPoint(100, 100), 160_000);  // elapsed 160 ms >= new 100 ms → activates

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void SetEffectiveDwellTime_IsIdempotent_ComposesAgainstBaseline()
    {
        // The map receives the BASELINE each application, so re-applying the same map can never
        // compound (the ApplyHitAreaMargin discipline — a router may re-apply every Changed).
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 10, dwellTimeMs: 1000));
        Func<int, int> half = ms => ms / 2;

        d.SetEffectiveDwellTime(half);
        d.SetEffectiveDwellTime(half);
        d.SetEffectiveDwellTime(half);

        d.Update(new PhysicalPoint(0, 0), 0);
        d.Update(new PhysicalPoint(0, 0), 490_000);
        Check.That(d.GetProgress("t") > 0.9).IsTrue(); // ~490/500, not 490/125 — no compounding
    }

    [Fact]
    public void SetEffectiveDwellTime_TargetAddedAfterMap_IsBornEffective()
    {
        // Surfaces recreate targets per show; a target born while a fill is held must never serve the
        // raw baseline (spec §5 "surfaces read effective values at (re)creation").
        var d = new DwellDetector();
        d.SetEffectiveDwellTime(ms => ms / 2);
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 10, dwellTimeMs: 1000));

        d.Update(new PhysicalPoint(0, 0), 0);
        d.Update(new PhysicalPoint(0, 0), 450_000);
        bool activated = false;
        d.TargetActivated += _ => activated = true;
        d.Update(new PhysicalPoint(0, 0), 500_000); // 500 ms >= effective 500 ms (not the 1000 baseline)

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void SetEffectiveDwellTime_NullMap_RestoresExactBaseline()
    {
        var d = new DwellDetector();
        var t = new DwellTarget("t", new PhysicalPoint(0, 0), radius: 10, dwellTimeMs: 1000);
        d.AddTarget(t);
        d.SetEffectiveDwellTime(ms => ms / 2);

        d.SetEffectiveDwellTime(null);

        d.Update(new PhysicalPoint(0, 0), 0);
        d.Update(new PhysicalPoint(0, 0), 600_000); // 600 < 1000 baseline → still inside, not activated
        Check.That(d.GetProgress("t")).IsCloseTo(0.6, 0.01);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetUpdateTests"`
Expected: compile error — `SetEffectiveDwellTime` does not exist.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.Core/Processing/DwellTarget.cs`: add a baseline field next to `_baseRadius` (`private readonly long _baseDwellTimeMicroseconds;` — long, matching `DwellTimeMicroseconds`, so `BaseDwellMs` needs no round-trip cast), assign it in BOTH constructors (`= dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;`), and add after `ApplyHitAreaMargin`:

```csharp
    /// <summary>The as-registered dwell time in ms — the composition baseline for adaptation routing,
    /// so repeated effective-value applications never compound.</summary>
    internal int BaseDwellMs => (int)(_baseDwellTimeMicroseconds / MathConstants.MicrosecondsPerMillisecond);
    /// <summary>Sets the effective dwell time (adaptation routing) WITHOUT touching dwell accumulation
    /// state: the next <see cref="DwellDetector.Update(PhysicalPoint, long)"/> re-evaluates elapsed
    /// against the new threshold (design §5/§7.3 — shortening mid-dwell preserves progress and can
    /// activate on the next sample; lengthening keeps progress). Idempotent by construction: callers
    /// derive the value from <see cref="BaseDwellMs"/>, never from the current effective value.</summary>
    internal void ApplyEffectiveDwellMs(int effectiveMs) =>
        DwellTimeMicroseconds = effectiveMs * MathConstants.MicrosecondsPerMillisecond;
```

In `src/EyeCtrl.Core/Processing/DwellDetector.cs`: add a field next to `_context` (`private Func<int, int>? _effectiveDwellMap;`) and, after `UpdateTargetGeometry`:

```csharp
    /// <summary>
    /// Stores the effective-dwell-time map (adaptation routing) and applies it to every current
    /// target; targets added afterwards are born effective (spec §5). The map receives each target's
    /// as-registered baseline ms (never the current effective value), so re-applying is idempotent.
    /// <c>null</c> restores every target to its exact baseline. State-preserving: never touches
    /// <c>EnteredTimestamp</c>/<c>IsGazeInside</c> — the next <see cref="Update(PhysicalPoint, long)"/>
    /// re-evaluates elapsed against the new threshold. Same threading contract as the other mutators
    /// (single thread — the UI thread, or the owning service's lock).
    /// </summary>
    public void SetEffectiveDwellTime(Func<int, int>? mapBaselineMsToEffectiveMs)
    {
        _effectiveDwellMap = mapBaselineMsToEffectiveMs;
        var targets = _targetsSnapshot;
        for (int i = 0; i < targets.Length; i++)
            ApplyMap(targets[i]);
    }

    private void ApplyMap(DwellTarget target)
    {
        // Null map = restore: the baseline IS the null case's effective value. A bare early-return
        // here would leave targets stuck at the last effective value (review round 1, blocker 1).
        var map = _effectiveDwellMap;
        target.ApplyEffectiveDwellMs(map is null ? target.BaseDwellMs : map(target.BaseDwellMs));
    }
```

and in `AddTarget`, after `_targetsSnapshot = _targets.Values.ToArray();` add `ApplyMap(target);`.

- [ ] **Step 4: Run the tests, then the full Core suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetUpdateTests"` → `Bestanden!`, 10 tests (6 existing + 4).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1136` (1132 + 4).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Processing/DwellTarget.cs src/EyeCtrl.Core/Processing/DwellDetector.cs tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs
git commit -m "feat(dwell): per-target effective dwell time, state-preserving

Re-adds the UpdateTargetDwellTime semantics the simplification pass
deleted, in routing shape: SetEffectiveDwellTime(map) applies a
baseline->effective map to every target (idempotent — composed against
the retained per-target baseline, never the current value), targets
added later are born effective, null restores exact baselines.
EnteredTimestamp is never touched: the next Update re-evaluates elapsed
against the new threshold."
```

---

### Task 2: Core registry — ranges, entries, `IDwellDetectorRegistry`

Behaviour-neutral: nothing registers yet.

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/AdaptationDwellRanges.cs`
- Create: `src/EyeCtrl.Core/Adaptation/DwellRoutingRegistration.cs`
- Create: `src/EyeCtrl.Core/Adaptation/DwellDetectorRegistry.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorRegistryTests.cs` (new)

**Interfaces:**
- Consumes: `DwellDetector.SetEffectiveDwellTime` (Task 1).
- Produces:
  - `AdaptationDwellRanges` — `StandardMinDwellMs = 200`, `StandardMaxDwellMs = 2000`, `KeyboardMinDwellMs = 100`, `KeyboardMaxDwellMs = 1500`, `ExternalMinDwellMs = 200`, `ExternalMaxDwellMs = 3000`.
  - `DwellRoutingRegistration(DwellContext Context, int MinDwellMs, int MaxDwellMs, Action<Func<int, int>?> Apply)` + `static DwellRoutingRegistration ForDetector(DwellDetector detector, DwellContext context, int minDwellMs, int maxDwellMs)`.
  - `IDwellDetectorRegistry` — `void Register(object owner, DwellRoutingRegistration registration); void Unregister(object owner); IReadOnlyList<DwellRoutingRegistration> Registrations { get; } event Action? RegistrationChanged;` + sealed impl `DwellDetectorRegistry`.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorRegistryTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public class DwellDetectorRegistryTests
{
    // Drift guard (the FixationRadius precedent): the range constants duplicate the DwellSettings
    // guards. Assigning min/max catches a NARROWED guard (the setter throws) and the equality checks
    // catch the CONSTANT drifting in either direction. A guard WIDENED beyond its constant is NOT
    // caught — that asymmetry is accepted; the constants, not the guards, are what routing reads.
    [Theory]
    [InlineData(nameof(DwellSettings.ActivationButtonDwellMs), 200, 2000, nameof(AdaptationDwellRanges.StandardMinDwellMs), nameof(AdaptationDwellRanges.StandardMaxDwellMs))]
    [InlineData(nameof(DwellSettings.ToolbarButtonDwellMs), 200, 2000, nameof(AdaptationDwellRanges.StandardMinDwellMs), nameof(AdaptationDwellRanges.StandardMaxDwellMs))]
    [InlineData(nameof(DwellSettings.KeyboardKeyDwellMs), 100, 1500, nameof(AdaptationDwellRanges.KeyboardMinDwellMs), nameof(AdaptationDwellRanges.KeyboardMaxDwellMs))]
    [InlineData(nameof(DwellSettings.KeyboardPredictionDwellMs), 100, 1500, nameof(AdaptationDwellRanges.KeyboardMinDwellMs), nameof(AdaptationDwellRanges.KeyboardMaxDwellMs))]
    public void Ranges_MatchTheirGuardFamilies(string propertyName, int min, int max, string minField, string maxField)
    {
        var settings = new DwellSettings();
        var property = typeof(DwellSettings).GetProperty(propertyName)!;
        property.SetValue(settings, min); // throws ArgumentOutOfRangeException below the guard floor
        property.SetValue(settings, max);
        Check.That(min).IsEqualTo((int)typeof(AdaptationDwellRanges).GetField(minField)!.GetValue(null)!);
        Check.That(max).IsEqualTo((int)typeof(AdaptationDwellRanges).GetField(maxField)!.GetValue(null)!);
    }

    [Fact]
    public void ExternalRange_MatchesExternalDwellSettingsGuard_AndEarnsItsCeiling()
    {
        // 3000, not the Standard 2000: a legal 2500 ms baseline identity-restored through Standard
        // would be truncated to 2000. Pin the truncation scenario through a real (disabled) provider —
        // Effective returns the baseline exactly, so the only variable is the clamp (round 1, nit 11).
        var s = new ExternalDwellSettings { DefaultDwellMs = AdaptationDwellRanges.ExternalMaxDwellMs };
        Check.That(s.DefaultDwellMs).IsEqualTo(3000);

        var provider = new EffectiveSettingsProvider(new AdaptationSettings()); // disabled ⇒ baseline
        double restore = provider.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 2500);
        Check.That(restore).IsEqualTo(2500.0);
        Check.That((int)Math.Clamp(Math.Round(restore),
            AdaptationDwellRanges.ExternalMinDwellMs, AdaptationDwellRanges.ExternalMaxDwellMs)).IsEqualTo(2500);
        Check.That((int)Math.Clamp(Math.Round(restore),
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs)).IsEqualTo(2000);
    }

    [Fact]
    public void Register_AddsEntryAndRaisesChanged()
    {
        var registry = new DwellDetectorRegistry();
        int changed = 0;
        registry.RegistrationChanged += () => changed++;
        var owner = new object();

        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.Toolbar, 200, 2000));

        Check.That(registry.Registrations).HasSize(1);
        Check.That(registry.Registrations[0].Context).IsEqualTo(DwellContext.Toolbar);
        Check.That(changed).IsEqualTo(1);
    }

    [Fact]
    public void Register_SameOwner_ReplacesWithoutDuplicating()
    {
        var registry = new DwellDetectorRegistry();
        var owner = new object();
        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.Toolbar, 200, 2000));

        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.EdgeButtonBar, 200, 2000));

        Check.That(registry.Registrations).HasSize(1);
        Check.That(registry.Registrations[0].Context).IsEqualTo(DwellContext.EdgeButtonBar);
    }

    [Fact]
    public void Unregister_RemovesAndRaises_UnknownOwnerIsNoOp()
    {
        var registry = new DwellDetectorRegistry();
        int changed = 0;
        registry.RegistrationChanged += () => changed++;
        var owner = new object();
        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.Toolbar, 200, 2000));

        registry.Unregister(new object()); // unknown — no event
        Check.That(changed).IsEqualTo(1);
        registry.Unregister(owner);
        Check.That(registry.Registrations).IsEmpty();
        Check.That(changed).IsEqualTo(2);
    }

    [Fact]
    public void Registrations_ReturnsASnapshot_NotALiveView()
    {
        var registry = new DwellDetectorRegistry();
        var owner = new object();
        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.Toolbar, 200, 2000));
        var snapshot = registry.Registrations;

        registry.Unregister(owner);

        Check.That(snapshot).HasSize(1); // the snapshot still sees the old state
    }

    [Fact]
    public void ForDetector_AppliesMapThroughTheDetector()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 10, dwellTimeMs: 1000));
        var registration = DwellRoutingRegistration.ForDetector(detector, DwellContext.Toolbar, 200, 2000);

        registration.Apply(ms => ms / 2);

        detector.Update(new PhysicalPoint(0, 0), 0);
        detector.Update(new PhysicalPoint(0, 0), 450_000);
        Check.That(detector.GetProgress("t")).IsCloseTo(0.9, 0.01); // 450/500, not 450/1000
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorRegistryTests"`
Expected: compile error — the types do not exist.

- [ ] **Step 3: Implement the three files**

`src/EyeCtrl.Core/Adaptation/AdaptationDwellRanges.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// Per-surface validated dwell-time ranges for DwellTime routing (design §13.1: per-surface sub-ranges
/// are re-applied at dwell routing; the provider only clamps to the union [100, 2000]). A range is the
/// <see cref="DwellSettings"/> guard FAMILY the surface's baselines come from, so an effective value
/// never leaves the span the surface's own settings would allow — and an identity restore returns the
/// exact baseline. Drift-guarded by the setter-as-oracle test (the FixationRadius precedent).
/// </summary>
public static class AdaptationDwellRanges
{
    /// <summary>The nine non-keyboard DwellSettings guards (Toolbar, ActionMenu, ActivationButton, …): 200–2000 ms.</summary>
    public const int StandardMinDwellMs = 200;
    /// <summary>See <see cref="StandardMinDwellMs"/>.</summary>
    public const int StandardMaxDwellMs = 2000;
    /// <summary>KeyboardKeyDwellMs / KeyboardPredictionDwellMs (also the emoji grid's category tabs, which read KeyboardKeyDwellMs): 100–1500 ms.</summary>
    public const int KeyboardMinDwellMs = 100;
    /// <summary>See <see cref="KeyboardMinDwellMs"/>.</summary>
    public const int KeyboardMaxDwellMs = 1500;
    /// <summary>ExternalDwellSettings.DefaultDwellMs: 200–3000 ms — NOT Standard; a legal 2500 ms baseline restored through [200, 2000] would be truncated.</summary>
    public const int ExternalMinDwellMs = 200;
    /// <summary>See <see cref="ExternalMinDwellMs"/>.</summary>
    public const int ExternalMaxDwellMs = 3000;
}
```

`src/EyeCtrl.Core/Adaptation/DwellRoutingRegistration.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Processing;

/// <summary>
/// One dwell surface's routing entry: the telemetry context, the surface's validated dwell range, and
/// an applier that pushes a baseline→effective-ms map onto the surface's live targets. The applier is a
/// delegate (not a raw <see cref="DwellDetector"/>) so an owner with its own threading contract —
/// ExternalDwellService serializes detector access under <c>_stateLock</c> on the gaze thread — can wrap
/// the call in its lock. The geometry-routing slice reuses this discovery mechanism.
/// </summary>
/// <param name="Context">Telemetry context (informational today; per-context fills are Phase 2).</param>
/// <param name="MinDwellMs">Surface-family validated floor for the effective value.</param>
/// <param name="MaxDwellMs">Surface-family validated ceiling for the effective value.</param>
/// <param name="Apply">Receives the map (null = restore baselines); must apply it on the owner's thread.</param>
public sealed record DwellRoutingRegistration(
    DwellContext Context,
    int MinDwellMs,
    int MaxDwellMs,
    Action<Func<int, int>?> Apply)
{
    /// <summary>The default applier for the common case: a detector owned and mutated on one thread
    /// (the UI thread) with no additional locking.</summary>
    public static DwellRoutingRegistration ForDetector(
        DwellDetector detector, DwellContext context, int minDwellMs, int maxDwellMs) =>
        new(context, minDwellMs, maxDwellMs, detector.SetEffectiveDwellTime);
}
```

`src/EyeCtrl.Core/Adaptation/DwellDetectorRegistry.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Application-level discovery of live dwell surfaces (design §13.3: "nothing can enumerate live
/// DwellDetectors" — this is that registry, shared with the geometry-routing slice). Owner-keyed:
/// re-registering replaces (idempotent for per-session owners like ManualSelectionDwellService);
/// <see cref="Unregister"/> on an unknown owner is a no-op. Raises <see cref="RegistrationChanged"/>
/// on every actual mutation so a router can apply the current effective values to a late registrant
/// immediately. Registrations are rare (construction / per-show), so a lock is free; the snapshot
/// copies on read so routers iterate a stable view.
/// </summary>
public interface IDwellDetectorRegistry
{
    /// <summary>Raised on every actual registration change (not for no-op unregisters).</summary>
    event Action? RegistrationChanged;
    /// <summary>Registers (or replaces) the entry owned by <paramref name="owner"/>.</summary>
    void Register(object owner, DwellRoutingRegistration registration);
    /// <summary>Removes the owner's entry; no-op (and no event) when absent.</summary>
    void Unregister(object owner);
    /// <summary>A stable snapshot of the live registrations.</summary>
    IReadOnlyList<DwellRoutingRegistration> Registrations { get; }
}

/// <summary>Default <see cref="IDwellDetectorRegistry"/> implementation.</summary>
public sealed class DwellDetectorRegistry : IDwellDetectorRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<object, DwellRoutingRegistration> _registrations = new();

    // NB: every public member needs its /// — EyeCtrl.Core.csproj escalates CS1591 (missing XML doc)
    // to an ERROR (review round 2, blocker). <inheritdoc /> pulls the interface docs.

    /// <inheritdoc />
    public event Action? RegistrationChanged;

    /// <inheritdoc />
    public void Register(object owner, DwellRoutingRegistration registration)
    {
        lock (_lock)
            _registrations[owner] = registration;
        RegistrationChanged?.Invoke();
    }

    /// <inheritdoc />
    public void Unregister(object owner)
    {
        bool removed;
        lock (_lock)
            removed = _registrations.Remove(owner);
        if (removed)
            RegistrationChanged?.Invoke();
    }

    /// <inheritdoc />
    public IReadOnlyList<DwellRoutingRegistration> Registrations
    {
        get { lock (_lock) return _registrations.Values.ToArray(); }
    }
}
```

- [ ] **Step 4: Run the tests, then the full Core suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorRegistryTests"` → `Bestanden!`, 10 tests (4 theory rows + 6 facts).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1146` (1136 + 10).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/AdaptationDwellRanges.cs src/EyeCtrl.Core/Adaptation/DwellRoutingRegistration.cs src/EyeCtrl.Core/Adaptation/DwellDetectorRegistry.cs tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorRegistryTests.cs
git commit -m "feat(adaptation): dwell detector registry with per-surface ranges

Owner-keyed DwellRoutingRegistration entries (context + validated range +
an applier delegate, so lock-owning surfaces wrap their own thread
contract); RegistrationChanged lets a router catch late registrants.
Ranges are the DwellSettings guard families — Standard 200-2000,
Keyboard 100-1500, External 200-3000 (NOT Standard: a legal 2500 ms
baseline restored through 2000 would truncate) — drift-guarded by a
setter-as-oracle test."
```

---

### Task 3: ViewModels — base-class registration (covers 9 of the 10 overlay surfaces; ScrollOverlay exempt)

Behaviour-neutral until a router exists (registry entries are inert data).

**Files:**
- Modify: `src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs`
- Modify: the 9 DI-constructed subclass files (list below)
- Modify: `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs` (ctor-shape guard, anchor ~lines 88–105)
- Test: `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayDwellRegistrationTests.cs` (new)

**Interfaces:**
- Consumes: Task 2's registry + `ForDetector`.
- Produces: `DwellOverlayViewModelBase` ctor signature `(IMessenger, int dwellCooldownMs, DwellContext context, IStruggleTelemetrySink? sink, IDwellDetectorRegistry? dwellRegistry)`; the NINE DI-constructed subclass ctors gain a trailing `IDwellDetectorRegistry? dwellRegistry = null` parameter (type-based `AddSingleton<T>()` registrations inject it automatically). `ScrollOverlayViewModel` is exempt — not DI-constructed (`new`'d per scroll session in `ScrollModeController.cs:77`), inert (no targets), disposed at `:117`; state the exemption, do not wire it.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayDwellRegistrationTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

public class OverlayDwellRegistrationTests
{
    // internal sealed PARTIAL, matching the two existing probes in OverlayTelemetryWiringTests — a
    // private nested subclass of an ObservableObject-partial base crashes the MVVM source generator
    // with MSB6006 and NO per-file diagnostic (documented there; review round 1, blocker 5).
    internal sealed partial class TestOverlayViewModel : DwellOverlayViewModelBase
    {
        public TestOverlayViewModel(IMessenger messenger, IDwellDetectorRegistry? registry)
            : base(messenger, dwellCooldownMs: 0, DwellContext.Toolbar, sink: null, dwellRegistry: registry) { }
        protected override void OnTargetActivated(string targetId) { }
        protected override void OnTargetProgress(string targetId, double progress) { }
    }

    [Fact]
    public void Constructor_RegistersStandardRangeEntry_WithItsContext()
    {
        var registry = new DwellDetectorRegistry();
        var vm = new TestOverlayViewModel(new WeakReferenceMessenger(), registry);

        Check.That(registry.Registrations).HasSize(1);
        var entry = registry.Registrations[0];
        Check.That(entry.Context).IsEqualTo(DwellContext.Toolbar);
        Check.That(entry.MinDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMinDwellMs);
        Check.That(entry.MaxDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMaxDwellMs);
    }

    [Fact]
    public void Dispose_Unregisters()
    {
        var registry = new DwellDetectorRegistry();
        var vm = new TestOverlayViewModel(new WeakReferenceMessenger(), registry);
        Check.That(registry.Registrations).HasSize(1);

        vm.Dispose(); // MessengerViewModelBase.Dispose → OnDispose → Unregister

        Check.That(registry.Registrations).IsEmpty();
    }

    [Fact]
    public void Constructor_NullRegistry_NoThrow()
    {
        var vm = new TestOverlayViewModel(new WeakReferenceMessenger(), registry: null);
        Check.That(vm).IsNotNull();
    }
}
```

(`MessengerViewModelBase` exposes `Dispose` publicly — verify at implementation time; if the dispose seam differs, adapt the test to whatever the base's documented teardown is and note it.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayDwellRegistrationTests"`
Expected: compile error — the base ctor has no `dwellRegistry` parameter.

- [ ] **Step 3: Modify the base class**

In `src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs`: add `using EyeCtrl.Core.Adaptation;` if not present, store `private readonly IDwellDetectorRegistry? _dwellRegistry;`, and change the ctor:

```csharp
    protected DwellOverlayViewModelBase(IMessenger messenger, int dwellCooldownMs,
        DwellContext context = DwellContext.Other, IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger)
    {
        DwellDetector = new DwellDetector(dwellCooldownMs, context, sink);
        DwellDetector.TargetActivated += OnTargetActivated;
        DwellDetector.TargetProgress += OnTargetProgress;
        _dwellRegistry = dwellRegistry;
        _dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            DwellDetector, context,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));
    }
```

and in `OnDispose`, after the existing unsubscribes:

```csharp
        _dwellRegistry?.Unregister(this);
```

- [ ] **Step 4: Add the pass-through parameter to the 9 DI-constructed subclasses**

Each subclass: add `IDwellDetectorRegistry? dwellRegistry = null` as the LAST ctor parameter and pass it to `base(...)`. All nine base calls are identical in shape; e.g. `ToolbarViewModel.cs:67-68` becomes:

```csharp
    public ToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Toolbar, sink, dwellRegistry)
```

The NINE DI-constructed subclass files (context shown for the base call — do not change it): `ActionMenuViewModel` (ActionMenu), `ActivationButtonViewModel` (ActivationButton), `DragTrailViewModel` (DragTrail), `EdgeButtonBarViewModel` (EdgeButtonBar), `GlassBackdropViewModel` (Backdrop), `ManualSelectionOverlayViewModel` (ManualSelection), `QuickActionsGridViewModel` (QuickActions), `TaskSwitcherViewModel` (TaskSwitcher), `ToolbarViewModel` (Toolbar; ctor at ~line 67, base call at 68). Add `using EyeCtrl.Core.Adaptation;` where the interface is named. **`ScrollOverlayViewModel` is EXEMPT** (review round 1, should-fix 7): it is absent from DI entirely — `new`'d per scroll session at `ScrollModeController.cs:77` (disposed at `:117`) — and it is inert (no targets; its scroll-stop dwell rides the shared `EdgeButtonBar`). State the exemption, do not chase the call site. **No AppBootstrapper change for the nine** — their registrations are type-based, so DI injects the new optional parameter automatically.

- [ ] **Step 5: Update the ctor-shape guard test**

In `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs`, the guard `EveryDwellOverlaySubclass_TakesTheSinkOnItsSingleConstructor` (~line 88) asserts each subclass has a **single public ctor declaring an `IStruggleTelemetrySink` parameter** — a trailing optional registry parameter keeps it green as-is (measured, review round 1). Strengthen it to ALSO require the `IDwellDetectorRegistry` parameter, keeping the existing ScrollOverlay exemption set untouched. Read the test first; adapt minimally.

- [ ] **Step 6: Run the ViewModels suite, then Core**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests` → `Bestanden!`, `gesamt: 296` (293 + 3).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1146` (unchanged).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels tests/EyeCtrl.ViewModels.Tests
git commit -m "feat(viewmodels): overlay base registers its detector for dwell routing

DwellOverlayViewModelBase takes an optional IDwellDetectorRegistry and
registers its detector (Standard range, its own context) on construction,
unregistering on dispose. The nine DI-constructed subclasses pass the
optional parameter through; ScrollOverlayViewModel is exempt (new'd per
scroll session by ScrollModeController, inert — no targets) alongside
the by-design Settings exclusion."
```

---

### Task 4: App — the router, DI, and the six non-base owners

Still behaviour-neutral for users with adaptation disabled (the router's identity map restores baselines; `Enabled=false` ⇒ `Effective` returns the baseline ⇒ map is identity). This is the behaviour-introducing slice for enabled users.

**Files:**
- Create: `src/EyeCtrl.App/Services/DwellTimeRouter.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (DI + eager resolve)
- Modify: `src/EyeCtrl.App/Overlays/EmojiGridManager.cs`, `Overlays/KeyboardWindow.xaml.cs`, `Overlays/EyeCareBannerWindow.xaml.cs`, `Services/OverlayManager.cs` (pass-through chain)
- Modify: `src/EyeCtrl.App/Services/NoteManager.cs`, `Services/ManualSelectionDwellService.cs`, `Services/ExternalDwellService.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/DwellTimeRouterTests.cs` (new); one fact appended to `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3; `EffectiveSettingsProvider.Changed`; `Effective(AdaptationKnob.DwellTime, DwellContext.Other, double)`.
- Produces: `DwellTimeRouter(EffectiveSettingsProvider provider, IDwellDetectorRegistry registry) : IDisposable` — applies on every `provider.Changed` and `registry.RegistrationChanged`.

- [ ] **Step 1: Write the failing router tests**

Create `tests/EyeCtrl.App.Tests/Services/DwellTimeRouterTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class DwellTimeRouterTests
{
    // A capture fake standing in for a surface: records the applied map, answers like a real target.
    private sealed class CaptureEntry
    {
        public Func<int, int>? Applied;
        public DwellRoutingRegistration AsRegistration(DwellContext context = DwellContext.Toolbar,
            int min = AdaptationDwellRanges.StandardMinDwellMs, int max = AdaptationDwellRanges.StandardMaxDwellMs) =>
            new(context, min, max, map => Applied = map);
    }

    private static (DwellTimeRouter Router, EffectiveSettingsProvider Provider, DwellDetectorRegistry Registry) Make()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        return (new DwellTimeRouter(provider, registry), provider, registry);
    }

    [Fact]
    public void ProviderChanged_AppliesComposedMap_WithTheEntryRange()
    {
        var (router, provider, registry) = Make();
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());

        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        // Full forgiving fill: 700 − 0.40×700 = 420; the map must compose AND clamp to the entry range.
        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(700)).IsEqualTo(420);
        Check.That(entry.Applied(300)).IsEqualTo(200); // 300 − 0.40×300 = 180, below the Standard floor → clamped (the provider's union floor is 100)
    }

    [Fact]
    public void ClearedFills_IdentityMap_RestoresExactBaseline()
    {
        var (router, provider, registry) = Make();
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);

        provider.ClearMomentaryFills();

        Check.That(entry.Applied!(700)).IsEqualTo(700);
    }

    [Fact]
    public void LateRegistration_AppliesImmediately()
    {
        var (router, provider, registry) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        var entry = new CaptureEntry();

        registry.Register(new object(), entry.AsRegistration()); // AFTER the fill exists

        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(700)).IsEqualTo(420);
    }

    [Fact]
    public void DisabledProvider_IdentityMap()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = false } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        using var router = new DwellTimeRouter(provider, registry);
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());

        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);

        Check.That(entry.Applied!(700)).IsEqualTo(700); // gated in Effective → baseline
    }
}
```

Note: `router` is intentionally unused in the first three bodies — constructing it is the subscription. If the compiler warns (it will not, for a field-assigned tuple element), bind with `using var router = …` and ignore.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellTimeRouterTests&Category!=UI"`
Expected: compile error — `DwellTimeRouter` does not exist. (One `--filter` with `&` — two separate `--filter` options would silently drop the first.)

- [ ] **Step 3: Implement the router**

Create `src/EyeCtrl.App/Services/DwellTimeRouter.cs`:

```csharp
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

/// <summary>
/// Routes the momentary DwellTime fill to every registered dwell surface (design §13.3 dwell routing):
/// on every <see cref="EffectiveSettingsProvider.Changed"/> (a controller fill write — payloadless, so
/// the dwell map is re-applied even when another knob caused the event; the apply is idempotent) and on
/// every registration change (a late registrant gets the current values immediately). Per entry, the
/// map composes the fill against each target's own baseline and clamps to the surface's validated range;
/// with adaptation disabled or no fill held, <see cref="EffectiveSettingsProvider.Effective"/> returns
/// the baseline, so the identity map restores exact baselines — no teardown path exists or is needed.
/// Dwell time applies in place (no rebuild), so there is deliberately no §9.1 budget (the tolerance
/// precedent). UI-thread: provider fills are written on the dispatcher and surfaces mutate on the UI
/// thread; constructed eagerly at startup (it subscribes in its ctor — see AppBootstrapper).
/// </summary>
public sealed class DwellTimeRouter : IDisposable
{
    private readonly EffectiveSettingsProvider _provider;
    private readonly IDwellDetectorRegistry _registry;

    public DwellTimeRouter(EffectiveSettingsProvider provider, IDwellDetectorRegistry registry)
    {
        _provider = provider;
        _registry = registry;
        _provider.Changed += ApplyAll;
        _registry.RegistrationChanged += ApplyAll;
        ApplyAll(); // entries registered before the router existed must not wait for the next event
                    // (review round 1, should-fix 9). Reentrancy-safe: Register/Unregister raise
                    // RegistrationChanged OUTSIDE the registry lock and Registrations snapshots on
                    // read, so a registration during ApplyAll neither deadlocks nor mutates the
                    // collection being iterated.
    }

    private void ApplyAll()
    {
        foreach (var registration in _registry.Registrations)
        {
            registration.Apply(MakeMap(registration));
        }
    }

    private Func<int, int> MakeMap(DwellRoutingRegistration registration) => baselineMs =>
    {
        double effective = _provider.Effective(AdaptationKnob.DwellTime, DwellContext.Other, baselineMs);
        return (int)Math.Clamp(Math.Round(effective), registration.MinDwellMs, registration.MaxDwellMs);
    };

    public void Dispose()
    {
        _provider.Changed -= ApplyAll;
        _registry.RegistrationChanged -= ApplyAll;
    }
}
```

- [ ] **Step 4: Run the router tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellTimeRouterTests&Category!=UI"`
Expected: `Bestanden!`, 4 tests.

- [ ] **Step 5: Wire DI + the six non-base owners**

**AppBootstrapper** (near the `EffectiveSettingsProvider` registration at ~line 149):

```csharp
        // Dwell routing (Phase 1c slice 2): the registry discovers live dwell surfaces; the router
        // applies the effective DwellTime on provider Changed / registration change. Eagerly resolved
        // at startup (App.xaml.cs) — it subscribes and applies in its ctor, and DI singletons are lazy.
        services.AddSingleton<IDwellDetectorRegistry, DwellDetectorRegistry>();
        services.AddSingleton<DwellTimeRouter>();
```

**The five factory registrations are NOT type-based** (review round 1, should-fix 6 — a new optional ctor parameter is silently ignored by a `sp => new …(…)` lambda): every one of them must gain an explicit `sp.GetRequiredService<IDwellDetectorRegistry>()` argument —

| Owner | AppBootstrapper anchor |
|---|---|
| `ManualSelectionDwellService` | ~230–236 |
| `ExternalDwellService` | ~239–245 |
| `NoteManager` | ~248–250 |
| `OverlayManager` | ~253–257 |
| `EyeCareBannerWindow` | ~260–262 |

(The nine type-registered ViewModels and `KeyboardViewModel` DO auto-inject; only these five lambdas need the edit.)

**Eager resolve** — the anchor already exists: `App.xaml.cs:174` (`_ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();`) and `:177` (`ManualSelectionDwellService`), both after `ResolveViewModels` (line 171) and before `InitializeOverlays` (line 183) — early enough that the router exists before any surface registers. Add alongside them: `_ = _serviceProvider.GetRequiredService<DwellTimeRouter>();`.

**NoteManager** — ctor gains the param and registers (its DI registration is the ~248–250 lambda — add the registry argument there per the table above):

```csharp
    public NoteManager(IMessenger messenger, IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
    {
        _messenger = messenger;
        _dwellDetector = new DwellDetector(cooldownMs: 300, context: DwellContext.Notes, sink: sink);
        // …existing wiring unchanged…
        dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _dwellDetector, DwellContext.Notes,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));
    }
```

**EmojiGridManager** — ctor gains `IDwellDetectorRegistry? dwellRegistry = null` (after `sink`); registers with the **Keyboard** range (its category tabs read `KeyboardKeyDwellMs`):

```csharp
        dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _categoryDwellDetector, DwellContext.Emoji,
            AdaptationDwellRanges.KeyboardMinDwellMs, AdaptationDwellRanges.KeyboardMaxDwellMs));
```

**KeyboardWindow** — ctor gains `IDwellDetectorRegistry? dwellRegistry = null`; registers its prediction detector (Keyboard range) and passes the registry into `EmojiGridManager` at the `SetViewModel` construction site (~line 103).

**OverlayManager** — constructs `KeyboardWindow` with `new` (~line 63); gains `IDwellDetectorRegistry` through its factory registration (the ~253–257 lambda — add the argument per the table above) and passes it to `KeyboardWindow`. In `KeyboardWindow.SetViewModel`, if an `EmojiGridManager` from a previous call is being replaced, unregister it first (`dwellRegistry?.Unregister(oldManager)`) — the method's own `_viewModel != null` guard anticipates re-entry, and a stranded entry holds a strong reference to a dead detector (review round 1, nit).

**EyeCareBannerWindow** — ctor gains `IDwellDetectorRegistry? dwellRegistry = null` after `sink`; registers (Standard). Its DI factory at `AppBootstrapper.cs:260-262` gains the argument: `new EyeCareBannerWindow(sp.GetRequiredService<EyeCtrlSettings>().Dwell.ActivationButtonDwellMs, sp.GetRequiredService<IStruggleTelemetrySink>(), sp.GetRequiredService<IDwellDetectorRegistry>())` (resolve everything from `sp` — the lambda has no `settings`/`sink` locals).

**ManualSelectionDwellService** — ctor stores `IDwellDetectorRegistry?` in a field; `OnManualSelectionStarted` registers the fresh per-session detector (Standard) keyed by `this`; `OnManualSelectionEnded` unregisters before nulling the detector.

**ExternalDwellService** — ctor gains `IDwellDetectorRegistry? dwellRegistry = null`; registers with the **External** range and the locking applier:

```csharp
        dwellRegistry?.Register(this, new DwellRoutingRegistration(
            DwellContext.ExternalDwell,
            AdaptationDwellRanges.ExternalMinDwellMs, AdaptationDwellRanges.ExternalMaxDwellMs,
            map => { lock (_stateLock) _detector.SetEffectiveDwellTime(map); }));
```

All pass-through sites pass the registry on (OverlayManager → KeyboardWindow → EmojiGridManager). **Settings is not wired anywhere** — deliberate.

- [ ] **Step 6: Append the External applier fact**

In `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs` (inside the existing class — conservatively run the whole App project rather than this class alone, though it has been observed green standalone since `WpfApplicationGate.EnsureApplicationWithTokens()` was added; the whole-project run is the safe default), add ONE fact using that file's existing construction/register/Update harness (read it first; reuse its helper or ctor pattern, adding a `dwellRegistry` argument the same way the production ctor now takes it):

1. Construct the service with a `new DwellDetectorRegistry()`.
2. Assert the registry holds exactly one `DwellContext.ExternalDwell` entry, with `MinDwellMs == AdaptationDwellRanges.ExternalMinDwellMs` (200) and `MaxDwellMs == AdaptationDwellRanges.ExternalMaxDwellMs` (3000).
3. Register one rect target through the service's normal registration path with a known dwell time (the harness's existing pattern — use its default or an explicitly-passed ms). **Allow the async pipe registration to settle first** (the harness's existing `await Task.Delay(200)` pattern) — `entry.Apply` before the target exists would compose against nothing.
4. Call `entry.Apply(ms => ms / 2);` — the halving map.
5. Prove the target's dwell time halved **behaviourally**: drive the service's gaze input at a timestamp just past half the original dwell and assert the activation fired (it would NOT have at the baseline), mirroring how the existing tests assert activations.

The lock itself (`lock (_stateLock)` around the delegated `SetEffectiveDwellTime`) is by construction in the registration lambda and is the code reviewer's to verify — the test pins the delegation and the range, not the lock. Do not add a public test seam to the service.

- [ ] **Step 7: Run the App headless suite, then Core and ViewModels**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → `Bestanden!`, `gesamt: 726` (721 + 5).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `gesamt: 1146` (unchanged). Run: `dotnet test tests/EyeCtrl.ViewModels.Tests` → `gesamt: 296` (unchanged).

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App tests/EyeCtrl.App.Tests
git commit -m "feat(adaptation): route DwellTime to every live dwell surface

DwellTimeRouter applies the momentary DwellTime fill (global Other
context) to every registry entry on provider Changed and on late
registration: clamp(round(Effective(DwellTime, Other, baseline)), min,
max) per target, composed against each target's own baseline. Disabled
or cleared => identity map => exact baselines; in-place apply, no
budget (the tolerance precedent).

Registers the six non-base owners: emoji + prediction (Keyboard range
100-1500), Notes and EyeCare (Standard), ManualSelection per session,
External with a _stateLock-wrapping applier and its own 200-3000 range
(Standard would truncate a legal 2500 ms baseline on restore). Settings
stays unregistered by design."
```

---

### Task 5: ViewModels — the Keyboard's hand-rolled key tracker

`KeyboardViewModel.ProcessKeyGaze` is not a `DwellDetector`; it composes its own effective key dwell. Its `_nearestKeyEnteredTimestamp` pattern means a mid-dwell change re-evaluates on the next sample for free — the same semantics Task 1 pinned.

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/KeyboardEffectiveDwellTests.cs` (new)

**Interfaces:**
- Consumes: `EffectiveSettingsProvider.Effective` + `.Changed`; `AdaptationDwellRanges.Keyboard*`.
- Produces: `KeyboardViewModel(EyeCtrlSettings, IMessenger, IStruggleTelemetrySink? sink = null, EffectiveSettingsProvider? effectiveSettings = null)`; `Configure` stores the baseline and applies the effective value.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.ViewModels.Tests/KeyboardEffectiveDwellTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

public class KeyboardEffectiveDwellTests
{
    private static (KeyboardViewModel Vm, EffectiveSettingsProvider Provider, EyeCtrlSettings Settings) Make()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        return (new KeyboardViewModel(settings, new WeakReferenceMessenger(), sink: null, effectiveSettings: provider),
            provider, settings);
    }

    private static Dictionary<string, PhysicalPoint> Keys() =>
        new() { ["a"] = new PhysicalPoint(100, 100) };

    [Fact]
    public void Configure_ComposesTheEffectiveDwell()
    {
        var (vm, provider, _) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);

        vm.Configure(keyDwellMs: 700, cooldownMs: 0);

        bool completed = false;
        vm.KeyDwellCompleted += _ => completed = true;
        var keys = Keys();
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 450_000);
        Check.That(completed).IsTrue(); // 450 ms >= effective 700−0.40×700 = 420, not the 700 baseline
    }

    [Fact]
    public void ProviderChange_MidDwell_ReEvaluatesWithoutReset()
    {
        var (vm, provider, _) = Make();
        vm.Configure(keyDwellMs: 700, cooldownMs: 0);
        var keys = Keys();
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 350_000); // half of 700

        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0); // → 420

        bool completed = false;
        vm.KeyDwellCompleted += _ => completed = true;
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 430_000); // 430 >= 420 → fires
        Check.That(completed).IsTrue();
    }

    [Fact]
    public void DisabledOrNoProvider_UsesTheBaseline()
    {
        var settings = new EyeCtrlSettings();
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger()); // no provider
        vm.Configure(keyDwellMs: 700, cooldownMs: 0);
        var keys = Keys();

        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 650_000);

        Check.That(vm.NearestKeyProgress).IsCloseTo(650.0 / 700.0, 0.01); // still accumulating vs 700
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardEffectiveDwellTests"`
Expected: compile error — no `effectiveSettings` parameter.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`: add `using EyeCtrl.Core.Adaptation;` + `using EyeCtrl.Core.Settings;` as needed; add fields `private readonly EffectiveSettingsProvider? _effectiveSettings; private readonly Action _onEffectiveSettingsChanged;` and `private int _baselineKeyDwellMs;`. Ctor:

```csharp
    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, EffectiveSettingsProvider? effectiveSettings = null)
        : base(messenger)
    {
        _settings = settings;
        _sink = sink;
        _effectiveSettings = effectiveSettings;
        if (_effectiveSettings is not null)
        {
            _onEffectiveSettingsChanged = ApplyEffectiveKeyDwell;
            _effectiveSettings.Changed += _onEffectiveSettingsChanged;
        }
        else
        {
            _onEffectiveSettingsChanged = () => { };
        }
    }
```

`Configure` becomes:

```csharp
    public void Configure(int keyDwellMs, int cooldownMs)
    {
        _baselineKeyDwellMs = keyDwellMs;
        _cooldownMs = cooldownMs;
        ApplyEffectiveKeyDwell();
    }
```

and add (near `Configure`):

```csharp
    // The key tracker is not a DwellDetector, so it composes its own effective dwell (design §15:
    // KeyboardViewModel effective key dwell). The _nearestKeyEnteredTimestamp pattern means a mid-dwell
    // change re-evaluates on the next sample without any reset — the same semantics the detector's
    // SetEffectiveDwellTime pins. NB: no early-return on a null provider — Configure MUST assign
    // _keyDwellMs even without one, or ProcessKeyGaze's `<= 0` guard silently kills the keyboard
    // (review round 1, blocker 2). The clamp also covers the no-provider path; it is identity there
    // because KeyboardKeyDwellMs's own guard is exactly [100, 1500].
    private void ApplyEffectiveKeyDwell()
    {
        if (_baselineKeyDwellMs <= 0)
            return; // not configured yet — Configure drives the initial apply
        double effective = _effectiveSettings?.Effective(
            AdaptationKnob.DwellTime, DwellContext.Other, _baselineKeyDwellMs) ?? _baselineKeyDwellMs;
        _keyDwellMs = (int)Math.Clamp(Math.Round(effective),
            AdaptationDwellRanges.KeyboardMinDwellMs, AdaptationDwellRanges.KeyboardMaxDwellMs);
    }
```

Unsubscribe in the VM's dispose seam (override `OnDispose` if the class has none yet — `MessengerViewModelBase` provides it) — add `if (_effectiveSettings is not null) _effectiveSettings.Changed -= _onEffectiveSettingsChanged;`. **No AppBootstrapper change** (type-registered; DI injects the optional parameter).

- [ ] **Step 4: Run the ViewModels suite, then Core**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests` → `Bestanden!`, `gesamt: 299` (296 + 3).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `gesamt: 1146` (unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs tests/EyeCtrl.ViewModels.Tests/KeyboardEffectiveDwellTests.cs
git commit -m "feat(keyboard): compose the effective key dwell in the hand-rolled tracker

KeyboardViewModel's key tracking is not a DwellDetector, so it takes an
optional EffectiveSettingsProvider and composes its own effective dwell
on Configure and on provider Changed, clamped to the Keyboard guard
family [100, 1500]. The EnteredTimestamp-style accumulation re-evaluates
mid-dwell changes on the next sample with no reset — the same semantics
the detector pins."
```

---

### Task 6: Core — the GazeEngine constructor-order nit carried from PR #131

PR #131's review left one accepted nit: the constructor builds `_gapInterpolator` from baseline settings BEFORE `_effectiveSettings` is assigned, while the applied-bits seed then records the effective value — inconsistent only for a provider with pre-existing fills (unreachable in production; reachable in tests). The ruling was to fold it into the next Core-touching slice.

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`
- Test: `tests/EyeCtrl.Core.Tests/GazeEngineSmoothingRoutingTests.cs` (append)

**Interfaces:** none new.

- [ ] **Step 1: Write the failing test**

Append to `GazeEngineSmoothingRoutingTests` (step-response differential, modelled on `SmoothingFill_RetunesEma_Live` — a seed-level assert CANNOT detect this defect: the applied-bits seed already reads the effective value on `main`; only the live filter's response can, review round 1, blocker 4):

```csharp
    [Fact]
    public void Constructor_WithPreExistingFill_LiveFilterRunsAtTheEffectiveParameter()
    {
        // PR #131 review nit, closed here per its ruling: the constructor built the interpolator from
        // BASELINE settings while the applied-bits seed recorded the effective value — the two
        // disagreed for a provider with pre-existing fills. On main the filled engine's FILTER runs at
        // the baseline factor, so its step response matches the baseline engine's; after the reorder it
        // runs at the effective factor (0.5 − 0.50×0.5 = 0.25) and trails.
        var filledSettings = new EyeCtrlSettings();
        filledSettings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        filledSettings.Adaptation.Enabled = true;
        var provider = new EffectiveSettingsProvider(filledSettings.Adaptation);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        var gazeA = new TestGazeProvider();
        var a = new GazeEngine(gazeA, new TestInputSimulator(), filledSettings, effectiveSettings: provider);

        var baselineSettings = new EyeCtrlSettings();
        baselineSettings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        var gazeB = new TestGazeProvider();
        var b = new GazeEngine(gazeB, new TestInputSimulator(), baselineSettings);

        double lastA = 0, lastB = 0;
        a.CursorMoved += (p, _) => lastA = p.X;
        b.CursorMoved += (p, _) => lastB = p.X;

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
        {
            gazeA.SimulateGaze(500, 500, ts);
            gazeB.SimulateGaze(500, 500, ts);
        }
        gazeA.SimulateGaze(600, 500, ts);
        gazeB.SimulateGaze(600, 500, ts);

        Check.That(a.AppliedSmoothingParameter).IsEqualTo(0.25); // the seed and the filter now agree…
        Check.That(a.InterpolatorRebuildCount).IsEqualTo(0);     // …and no rebuild was spent
        Check.That(lastA).IsStrictlyLessThan(lastB);             // the 0.25-factor filter trails the step
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~Constructor_WithPreExistingFill"`
Expected: `Fehler!` — on `main` the filled engine's filter still runs at the baseline factor 0.5, so `lastA == lastB` and the strict-less assertion fails (`AppliedSmoothingParameter` alone would PASS on main — the seed already reads the effective value; that is the defect).

- [ ] **Step 3: Reorder the constructor**

In `GazeEngine`'s ctor: move `_effectiveSettings = effectiveSettings;` (and the `_settings = settings;` it depends on — already above) so it precedes the `_gapInterpolator = new GapInterpolator(...)` assignment, and build the interpolator through the effective path:

```csharp
        _effectiveSettings = effectiveSettings;

        _gapInterpolator = new GapInterpolator(
            GazeFilterFactory.Create(_settings.Cursor, EffectiveSmoothingParameter()),
            _settings.Cursor.MaxGapInterpolationMs);
```

Delete the now-duplicate later `_effectiveSettings = effectiveSettings;` line. Nothing else changes: the `_lastAppliedSmoothingBits` seed further down already reads `EffectiveSmoothingParameter()` and now agrees with the built filter. With no provider (or no fills) `EffectiveSmoothingParameter()` returns null/baseline — the previous behaviour exactly.

- [ ] **Step 4: Run the routing file, then the full Core suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngineSmoothingRoutingTests"` → `Bestanden!`, 28 tests (27 + 1).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1147` (1146 + 1).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs tests/EyeCtrl.Core.Tests/GazeEngineSmoothingRoutingTests.cs
git commit -m "fix(gaze): build the constructor interpolator from the effective parameter

PR #131 review nit (ruled: fold into the next Core-touching slice): the
constructor built the interpolator from baseline settings before
_effectiveSettings was assigned, so a provider with pre-existing fills
would start its filter at baseline while the applied-bits seed claimed
the effective value — unreachable in production, reachable in tests.
Assign _effectiveSettings first and build through the factory overload;
with no provider or no fills the behaviour is bit-identical."
```

---

### Task 7: Spec amendment + full verification

**Files:**
- Modify: `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md`

- [ ] **Step 1: Amend §13.3's "Dwell and geometry." bullet**

**First strike the bullet's now-false opening sentence** ("The *per-detector mutation* substrate already exists: `DwellDetector.UpdateTargetDwellTime` / `UpdateTargetGeometry` …" — the simplification pass deleted `UpdateTargetDwellTime`; `UpdateTargetGeometry` is the survivor). Replace it with: "The *per-detector mutation* substrate is `DwellDetector.UpdateTargetGeometry` (geometry) plus the dwell-routing slice's `SetEffectiveDwellTime` map API (dwell — re-adding the semantics the 2026-08-29 simplification pass deleted):". Then append to the bullet:

```markdown
  **(Dwell resolved 2026-09-02 — dwell-routing slice, plan `2026-09-02-dwell-knob-routing.md`; geometry
  still open.)** The shared substrate is `IDwellDetectorRegistry` (Core): owner-keyed
  `DwellRoutingRegistration` entries carrying the surface's context, its validated range, and an *applier
  delegate* — `DwellDetector.SetEffectiveDwellTime(map)` for UI-thread owners, a `_stateLock`-wrapping
  lambda for `ExternalDwellService` (its detector Updates on the gaze thread). `DwellTarget` retains a
  per-target dwell-time baseline (the `_baseRadius` pattern) — baselines are heterogeneous per target
  (per-button EdgeButtonBar values; Notes 600; EyeCare 700; per-message ActionMenu), so the fill composes
  per target: `clamp(round(Effective(DwellTime, Other, baseline)), min, max)`. Per-surface ranges
  (`AdaptationDwellRanges`): Standard [200, 2000] (nine guards), Keyboard [100, 1500] (incl. the emoji
  grid and prediction bar), External [200, 3000] — NOT Standard, whose ceiling would truncate a legal
  2500 ms baseline on identity-restore. The deleted `UpdateTargetDwellTime` semantics return as the map
  API (state-preserving re-evaluation; targets born effective on `AddTarget`); the Keyboard's hand-rolled
  tracker composes its own effective dwell on `Configure`/`Changed`. App-side `DwellTimeRouter` applies on
  provider `Changed` + `RegistrationChanged` (late registrants; construct-time apply makes resolve order
  non-load-bearing); disabled/cleared ⇒ identity map ⇒ exact baselines; in-place apply, so no §9.1 budget
  (the tolerance precedent). The nine DI-constructed overlay subclasses register via the base class;
  `ScrollOverlayViewModel` is exempt (per-session construction in ScrollModeController, inert), alongside
  the by-design exclusion of Settings' four detectors. Five factory-registered owners
  (ManualSelection, External, NoteManager, OverlayManager, EyeCareBannerWindow) take the registry
  argument explicitly in their DI lambdas.
```

- [ ] **Step 2: Update the Phase-1c status line in §13**

Replace the smoothing-only status sentence (added by the smoothing slice) with:

```markdown
**Status: smoothing routed 2026-09-01 (PR #131); dwell routed 2026-09-02 (registry +
`DwellTimeRouter` + Keyboard tracker). Geometry routing remains — the Phase-2 gate stays closed.**
```

- [ ] **Step 3: Run the full sweep, sequentially**

1. `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1147` (1146 passed, 1 skipped) — baseline 1132 **+ 15**:

| Task | Delta | From |
|---|---|---|
| 1 | **+4** | restored re-evaluation fact + idempotence + born-effective + null-restore |
| 2 | **+10** | 4 range-oracle theory rows + 6 facts (External range, register/replace/unregister/snapshot, ForDetector delegation) |
| 6 | **+1** | constructor-with-pre-existing-fill |

2. `dotnet test tests/EyeCtrl.ViewModels.Tests` → `Bestanden!`, `gesamt: 299` (293 + 3 registration + 3 keyboard).
3. `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → `Bestanden!`, `gesamt: 726` (721 + 4 router + 1 external-applier).
4. `dotnet test tests/EyeCtrl.Benchmarks.Tests` → `Bestanden!`, `gesamt: 27` (unchanged).

A different total means a row was duplicated or dropped — find it, do not adjust the number.

- [ ] **Step 4: Confirm the change surface**

Run: `git diff --stat main`
Expected: **exactly 36 files** — Core: 3 new (`AdaptationDwellRanges`, `DwellRoutingRegistration`, `DwellDetectorRegistry`) + `DwellTarget.cs` + `DwellDetector.cs` + `GazeEngine.cs` (6); ViewModels: `DwellOverlayViewModelBase` + the 9 non-Scroll subclasses + `KeyboardViewModel` (11); App: `DwellTimeRouter` (new), `AppBootstrapper.cs`, `App.xaml.cs`, `EmojiGridManager.cs`, `KeyboardWindow.xaml.cs`, `EyeCareBannerWindow.xaml.cs`, `OverlayManager.cs`, `NoteManager.cs`, `ManualSelectionDwellService.cs`, `ExternalDwellService.cs` (10); tests: 4 new files + `DwellTargetUpdateTests` + `OverlayTelemetryWiringTests` + `ExternalDwellServiceTests` (7); docs: the spec + this plan (2). **No** `AdaptationController.cs`, `StruggleMonitor.cs`, `EffectiveSettingsProvider.cs`, `ScrollModeController.cs`, `ScrollOverlayViewModel.cs`, or Platform changes.

- [ ] **Step 5: State what was and was not verified (for the PR description)**

Verified: state-preserving re-evaluation (restored semantics), idempotent per-target composition, born-effective targets, identity restore (cleared fills AND disabled), per-surface range clamps incl. the External 2500-restore case, late-registration + construct-time apply, base-class registration for the nine DI-constructed overlay surfaces (ScrollOverlay exempt as inert), the Keyboard tracker's mid-dwell re-evaluation and no-provider path, and the PR-131 constructor nit. Not verified: true concurrent router-apply vs. gaze-thread `Update` for External (the lock is by construction and reviewed, not race-tested); real-user feel of shorter/longer effective dwells (the §13.2 thresholds remain uncalibrated); per-context dwell fills (the controller fills globally under `Other` today — per-context composition is Phase 2).

- [ ] **Step 6: Commit the amendment; push and open the PR — ONLY after the repo owner's explicit OK**

```bash
git add docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md
git commit -m "docs(spec): record the dwell-routing slice against §13.3 (geometry remains)"
```

**Do not push until the repo owner has explicitly approved.** If no answer, stop and report — the branch is complete locally.

---

## Notes for the implementer

- **Do not run the app or simulate input.** Headless verification only; see Global Constraints.
- **If a step's expected output does not match, stop rather than adapting the plan.** The anchors reference `main` at `0cbdfc0d`.
- **Do not "optimize" the router to skip applies when a non-dwell knob caused `Changed`** — the payloadless event makes that filter fragile, and the apply is idempotent and cheap (one clamp per target per ≤4 Hz event).
- **Do not add dwell routing for Settings' four detectors** — the remedy surface must never adapt (spec §2). If a guard test insists every detector registers, the guard is wrong, not the design.
- **The External applier's lock is a correctness requirement, not a nicety** — its detector is `Update`d on the gaze thread under `_stateLock`; an unlocked dispatcher-thread `SetEffectiveDwellTime` would race target mutation.
- **Emoji grid targets carry `KeyboardKeyDwellMs` baselines** (category tabs) — hence the Keyboard range for a non-keyboard surface; do not "fix" it to Standard.
- **Phase-2 gate:** after this slice, two of three routing slices are done. Geometry remains; do not claim Phase 1c complete.
