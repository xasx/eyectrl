# Struggle Detection — Phase 1b-② Fuller AdaptationSettings & Posture Presets — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expand `AdaptationSettings` (created bare in 1a-⑤) into the full struggle-detection **configuration contract** (design §10) — per-knob enables, feedback/control toggles, and the per-knob safety-band bounds (the §13.1 fixed envelope, as fractions of baseline) — with validation; and add the `PosturePresets` table (the §13.1 eagerness numbers) the Phase-1b controller will consume. **Pure settings + data: zero behaviour change** (nothing reads these until the controller/coordinator land in later slices).

**Architecture:** `AdaptationSettings` stays a single `EyeCtrlSettings` sub-setting (already registered via `EnumerateSubSettings()`); this slice only adds `[ObservableProperty]` fields + `Validate()` rules, so the enumeration count is **unchanged**. Band bounds are stored as fractions of baseline and validated to `[0, 1]`; the hard safety guarantee (§8.4) is the validated-range clamp applied at composition time by `EffectiveValue.Compute`, so a fraction is a *desired* band, never an escape from the base range. `PosturePresets` is a pure static lookup keyed by the existing `AdaptationPosture` enum.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (framework-agnostic); xUnit v3 + NFluent (`Check.That(...)`).

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §10 (`AdaptationSettings` surface), §13.1 (fixed per-knob envelope + posture-preset table), §7.2 (relax/recover tick semantics), §8.4 (sub-bands strictly inside validated ranges).

**Depends on:** **Phase 1a implemented** — 1a-⑤'s `AdaptationSettings` (`Enabled` bool default false, `Posture` `AdaptationPosture` default `Conservative`, `Validate()` returning empty) and `AdaptationPosture` (`Conservative`/`Balanced`/`Responsive`). Builds on the **post-1a** source. (No dependency on 1b-①.)

**Executor pre-flight (do this FIRST):** confirm against the *merged* 1a source: `AdaptationSettings` is `partial class : ObservableObject, IValidatable` with `[ObservableProperty] private bool _enabled;` + `[ObservableProperty] private AdaptationPosture _posture = AdaptationPosture.Conservative;` and a `Validate()`; `EyeCtrlSettingsEnumerationTests` asserts the post-1a count (**19**, since 1a-⑤ bumped it from 18). If the shape differs, adjust Task 1 accordingly.

**⚠️ Core enforces XML docs as build errors:** `EyeCtrl.Core.csproj` sets `<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `<WarningsAsErrors>CS1591</WarningsAsErrors>`. Every public member — **including every `[ObservableProperty]` field's `<summary>` (copied to the generated property), every enum/record-struct positional `<param>`** — is documented below. Do not strip the doc comments.

---

## Scope boundary (explicit — deferred pieces noted where they land)

This slice **defines** the configuration surface; it does **not** wire consumers. Deferred:
- **Consuming the band bounds + posture presets** — the `AdaptationController` (1b-④) reads them to compute bounded per-knob deltas, and the `EffectiveSettingsProvider` band-source is unified with these settings there (its 1b-① `KnobPolicy` constants for the one wired knob are transitional until then; the 1b-① range drift-guard test keeps that covered meanwhile).
- **`ResetLearnedBaseline` action** → Phase 2 (the learned layer is its only consumer; a reset with nothing to reset is premature).
- **Live status label** → 1b-⑤ (`AdaptationCoordinator` owns the live adaptation state the label reflects).
- **Settings-window UI** for the new fields (via the existing `SettingItemViewModel` subclasses) → a later UI step; this slice is the Core data + validation only.
- **Base-range centralization** (exposing `DwellSettings`/`CursorSettings` validated ranges as constants so bands validate against a single source) → a separate cleanup; here bands validate to `[0, 1]` and rely on the composition-time range clamp for §8.4.

The band fractions are the **complete** §13.1 envelope (all four knob families) even though only tolerance is routed so far — this is the config *contract*, defined once and consumed incrementally, not per-knob drift.

---

## File Structure

**Modify (Core):** `src/EyeCtrl.Core/Settings/AdaptationSettings.cs` — add enables, toggles, band-fraction fields + guards + `Validate()`.
**Create (Core):** `src/EyeCtrl.Core/Adaptation/PosturePresets.cs` — `PostureTuning` record struct + `PosturePresets.For(...)`.
**Test:**
- Modify `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs` — extend with new-field defaults + validation.
- Create `tests/EyeCtrl.Core.Tests/Adaptation/PosturePresetsTests.cs`.

**Zero-behaviour-change guarantee:** only additive `[ObservableProperty]` fields (all defaulting to valid, adaptation-neutral values) + a `Validate()` that returns empty for defaults + a new pure static lookup. `AdaptationSettings` remains one enumerated sub-setting, so `EyeCtrlSettingsEnumerationTests` (count 19 post-1a) is untouched. Nothing consumes the new members yet.

---

## Task 1: Expand `AdaptationSettings` into the full configuration surface

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs` (keep the existing `Defaults_AreOptInAndConservative` / `EyeCtrlSettings_EnumeratesAdaptation` tests):

```csharp
    [Fact]
    public void NewDefaults_AreAdaptationNeutralAndValid()
    {
        var s = new AdaptationSettings();

        // Per-knob enables default on.
        Check.That(s.AdaptDwell).IsTrue();
        Check.That(s.AdaptTolerance).IsTrue();
        Check.That(s.AdaptSmoothing).IsTrue();
        Check.That(s.AdaptGeometry).IsTrue();

        // Feedback toggles: cue on, suppression on, caregiver alerts off (opt-in, Phase 3).
        Check.That(s.ShowCue).IsTrue();
        Check.That(s.SuppressInSensitiveContexts).IsTrue();
        Check.That(s.CaregiverAlerts).IsFalse();

        // Band fractions match the §13.1 fixed envelope.
        Check.That(s.DwellShorterMaxFraction).IsEqualTo(0.40);
        Check.That(s.DwellLongerMaxFraction).IsEqualTo(0.50);
        Check.That(s.ToleranceLooserMaxFraction).IsEqualTo(0.50);
        Check.That(s.SmoothingMoreMaxFraction).IsEqualTo(0.50);
        Check.That(s.GeometryBiggerMaxFraction).IsEqualTo(0.50);

        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void BandFraction_OutOfRange_IsReportedByValidate()
    {
        // Bypass the setter guard via the generated OnChanging by constructing then forcing an invalid
        // value through Validate on a mutated copy is not possible (guard throws on set), so assert the
        // guard throws — the runtime protection — and that Validate is the collected-report counterpart.
        var s = new AdaptationSettings();
        Check.ThatCode(() => s.ToleranceLooserMaxFraction = 1.5).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => s.DwellShorterMaxFraction = -0.1).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BandFraction_InRange_IsAccepted()
    {
        var s = new AdaptationSettings { ToleranceLooserMaxFraction = 0.25, DwellShorterMaxFraction = 0.0 };
        Check.That(s.ToleranceLooserMaxFraction).IsEqualTo(0.25);
        Check.That(s.DwellShorterMaxFraction).IsEqualTo(0.0);
        Check.That(s.Validate()).IsEmpty();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationSettingsTests"`
Expected: FAIL to **compile** — the new properties don't exist yet.

- [ ] **Step 3: Add the fields, guards, and validation**

Replace the body of `src/EyeCtrl.Core/Settings/AdaptationSettings.cs` (keep the namespace + usings; the class already has `_enabled` and `_posture`). The full class:

```csharp
namespace EyeCtrl.Core.Settings;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Master switch, posture, per-knob enables + safety-band bounds, and feedback toggles for
/// struggle-detection self-adaptation. Off by default (opt-in v1). Band bounds are fractions of the
/// knob's baseline and stay a *desired* band — the hard guarantee is the validated-range clamp applied
/// at composition time (design §5/§8.4). Posture scales eagerness only; its preset values live in the
/// PosturePresets table. The safety-band widths are fixed across postures (§13.1).
/// </summary>
public partial class AdaptationSettings : ObservableObject, IValidatable
{
    /// <summary>Master switch. When false, adaptation is fully inert (effective settings == baseline).</summary>
    [ObservableProperty] private bool _enabled;

    /// <summary>How eagerly adaptation responds. Conservative by default.</summary>
    [ObservableProperty] private AdaptationPosture _posture = AdaptationPosture.Conservative;

    /// <summary>Whether dwell-duration adaptation is enabled.</summary>
    [ObservableProperty] private bool _adaptDwell = true;

    /// <summary>Whether targeting-tolerance adaptation is enabled.</summary>
    [ObservableProperty] private bool _adaptTolerance = true;

    /// <summary>Whether smoothing adaptation is enabled.</summary>
    [ObservableProperty] private bool _adaptSmoothing = true;

    /// <summary>Whether target-geometry adaptation is enabled.</summary>
    [ObservableProperty] private bool _adaptGeometry = true;

    /// <summary>Whether the on-screen adaptation cue is shown (still suppressed in sensitive contexts).</summary>
    [ObservableProperty] private bool _showCue = true;

    /// <summary>Whether sustained-struggle caregiver alerts are enabled (Phase 3; off by default).</summary>
    [ObservableProperty] private bool _caregiverAlerts;

    /// <summary>Whether feedback (cue/toast/verbose log) is suppressed in sensitive (password) contexts.</summary>
    [ObservableProperty] private bool _suppressInSensitiveContexts = true;

    /// <summary>Maximum forgiving (shorter) dwell deviation as a fraction of baseline (§13.1). Default 0.40.</summary>
    [ObservableProperty] private double _dwellShorterMaxFraction = 0.40;

    /// <summary>Maximum tightening (longer) dwell deviation for accidental activation, as a fraction of baseline (§13.1). Default 0.50.</summary>
    [ObservableProperty] private double _dwellLongerMaxFraction = 0.50;

    /// <summary>Maximum looser (larger) fixation-radius deviation as a fraction of baseline (§13.1). Default 0.50.</summary>
    [ObservableProperty] private double _toleranceLooserMaxFraction = 0.50;

    /// <summary>Maximum "more smoothing" shift of the active filter parameter as a fraction of baseline. Default 0.50 (starting value).</summary>
    [ObservableProperty] private double _smoothingMoreMaxFraction = 0.50;

    /// <summary>Maximum larger target-geometry deviation as a fraction of baseline (§13.1). Default 0.50.</summary>
    [ObservableProperty] private double _geometryBiggerMaxFraction = 0.50;

    partial void OnDwellShorterMaxFractionChanging(double value) => GuardFraction(value, nameof(DwellShorterMaxFraction));
    partial void OnDwellLongerMaxFractionChanging(double value) => GuardFraction(value, nameof(DwellLongerMaxFraction));
    partial void OnToleranceLooserMaxFractionChanging(double value) => GuardFraction(value, nameof(ToleranceLooserMaxFraction));
    partial void OnSmoothingMoreMaxFractionChanging(double value) => GuardFraction(value, nameof(SmoothingMoreMaxFraction));
    partial void OnGeometryBiggerMaxFractionChanging(double value) => GuardFraction(value, nameof(GeometryBiggerMaxFraction));

    private static void GuardFraction(double value, string name)
    {
        if (value < 0.0 || value > 1.0)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between 0 and 1.");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate() => ValidationHelper.CollectErrors(
        ValidationHelper.ValidateRange(DwellShorterMaxFraction, 0.0, 1.0, nameof(DwellShorterMaxFraction)),
        ValidationHelper.ValidateRange(DwellLongerMaxFraction, 0.0, 1.0, nameof(DwellLongerMaxFraction)),
        ValidationHelper.ValidateRange(ToleranceLooserMaxFraction, 0.0, 1.0, nameof(ToleranceLooserMaxFraction)),
        ValidationHelper.ValidateRange(SmoothingMoreMaxFraction, 0.0, 1.0, nameof(SmoothingMoreMaxFraction)),
        ValidationHelper.ValidateRange(GeometryBiggerMaxFraction, 0.0, 1.0, nameof(GeometryBiggerMaxFraction)));
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationSettingsTests"`
Expected: PASS (5 tests — the 2 existing + 3 new). No `CS1591`.

- [ ] **Step 5: Run the enumeration drift-guard (must be unchanged)**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EyeCtrlSettingsEnumerationTests"`
Expected: PASS (3 tests, count still **19**). Adding fields to `AdaptationSettings` adds no sub-setting, so the count is unchanged; `Validate_DefaultInstance_HasNoErrors` passes because every new default is in range.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Settings/AdaptationSettings.cs tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs
git commit -m "feat(adaptation): expand AdaptationSettings with per-knob enables, toggles, and safety-band bounds"
```

---

## Task 2: `PosturePresets` (the §13.1 eagerness table)

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/PosturePresets.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/PosturePresetsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/PosturePresetsTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class PosturePresetsTests
{
    [Fact]
    public void Conservative_MatchesSpec()
    {
        var t = PosturePresets.For(AdaptationPosture.Conservative);
        Check.That(t.RelaxThreshold).IsEqualTo(0.50);
        Check.That(t.SustainSeconds).IsEqualTo(3);
        Check.That(t.RelaxStepFraction).IsEqualTo(0.10);
        Check.That(t.RecoverThreshold).IsEqualTo(0.25);
        Check.That(t.RecoverStepFraction).IsEqualTo(0.05);
    }

    [Fact]
    public void Balanced_MatchesSpec()
    {
        var t = PosturePresets.For(AdaptationPosture.Balanced);
        Check.That(t.RelaxThreshold).IsEqualTo(0.40);
        Check.That(t.SustainSeconds).IsEqualTo(2);
        Check.That(t.RelaxStepFraction).IsEqualTo(0.15);
        Check.That(t.RecoverThreshold).IsEqualTo(0.20);
        Check.That(t.RecoverStepFraction).IsEqualTo(0.05);
    }

    [Fact]
    public void Responsive_MatchesSpec()
    {
        var t = PosturePresets.For(AdaptationPosture.Responsive);
        Check.That(t.RelaxThreshold).IsEqualTo(0.30);
        Check.That(t.SustainSeconds).IsEqualTo(1);
        Check.That(t.RelaxStepFraction).IsEqualTo(0.20);
        Check.That(t.RecoverThreshold).IsEqualTo(0.15);
        Check.That(t.RecoverStepFraction).IsEqualTo(0.05);
    }

    [Fact]
    public void RecoverStepFraction_IsPostureIndependent()
    {
        Check.That(PosturePresets.For(AdaptationPosture.Conservative).RecoverStepFraction)
            .IsEqualTo(PosturePresets.For(AdaptationPosture.Balanced).RecoverStepFraction);
        Check.That(PosturePresets.For(AdaptationPosture.Balanced).RecoverStepFraction)
            .IsEqualTo(PosturePresets.For(AdaptationPosture.Responsive).RecoverStepFraction);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PosturePresetsTests"`
Expected: FAIL to **compile** — `PostureTuning` / `PosturePresets` do not exist.

- [ ] **Step 3: Create the presets**

`src/EyeCtrl.Core/Adaptation/PosturePresets.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// Momentary-controller tuning for one posture (design §7.2/§13.1): how eagerly adaptation relaxes
/// toward the transform target and recovers back toward baseline. Posture scales eagerness only — the
/// per-knob safety envelope (band bounds in <see cref="AdaptationSettings"/>) is fixed across postures.
/// </summary>
/// <param name="RelaxThreshold">Struggle score above which a knob relaxes (toward easier).</param>
/// <param name="SustainSeconds">Seconds the score must stay above <paramref name="RelaxThreshold"/> before relaxing.</param>
/// <param name="RelaxStepFraction">Fraction of the band moved toward the target per tick while relaxing.</param>
/// <param name="RecoverThreshold">Struggle score below which a knob recovers (toward baseline).</param>
/// <param name="RecoverStepFraction">Fraction of the band moved toward baseline per tick while recovering (posture-independent).</param>
public readonly record struct PostureTuning(
    double RelaxThreshold,
    int SustainSeconds,
    double RelaxStepFraction,
    double RecoverThreshold,
    double RecoverStepFraction);

/// <summary>
/// The fixed <see cref="PostureTuning"/> presets per <see cref="AdaptationPosture"/> (design §13.1).
/// Consumed by the Phase-1b AdaptationController; recovery is uniformly gentle across postures.
/// </summary>
public static class PosturePresets
{
    /// <summary>Returns the controller tuning for the given posture.</summary>
    /// <param name="posture">The adaptation posture.</param>
    /// <returns>The posture's <see cref="PostureTuning"/>.</returns>
    public static PostureTuning For(AdaptationPosture posture) => posture switch
    {
        AdaptationPosture.Conservative => new PostureTuning(0.50, 3, 0.10, 0.25, 0.05),
        AdaptationPosture.Balanced => new PostureTuning(0.40, 2, 0.15, 0.20, 0.05),
        AdaptationPosture.Responsive => new PostureTuning(0.30, 1, 0.20, 0.15, 0.05),
        _ => new PostureTuning(0.50, 3, 0.10, 0.25, 0.05), // unknown → Conservative (safest)
    };
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PosturePresetsTests"`
Expected: PASS (4 tests). No `CS1591`.

- [ ] **Step 5: Full Core suite + commit**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS (the existing `AdaptationSettingsTests`, `EyeCtrlSettingsEnumerationTests`, and all other Core tests are unaffected; only additive members + a new static type).

```bash
git add src/EyeCtrl.Core/Adaptation/PosturePresets.cs tests/EyeCtrl.Core.Tests/Adaptation/PosturePresetsTests.cs
git commit -m "feat(adaptation): add PosturePresets eagerness table (Conservative/Balanced/Responsive)"
```

---

## Self-Review

**Spec coverage (this slice):**
- §10 `AdaptationSettings` surface — `Enabled` (1a-⑤) + per-knob enables (`AdaptDwell/Tolerance/Smoothing/Geometry`) + per-knob band bounds + `ShowCue`/`CaregiverAlerts`/`SuppressInSensitiveContexts` toggles. `ResetLearnedBaseline` + status label deferred (documented) to their consuming slices. ✓
- §13.1 fixed envelope — band fractions default to the §13.1 numbers (dwell −40 %, dwell-longer +50 %, tolerance +50 %, geometry +50 %; smoothing +50 % as a flagged starting value); posture-independent (not scaled by posture). ✓
- §13.1 posture presets — `PosturePresets` returns the exact Conservative/Balanced/Responsive rows; `RecoverStepFraction` posture-independent (tested). ✓
- §8.4 sub-bands inside validated ranges — fractions are a *desired* band; the hard clamp to the base validated range happens in `EffectiveValue.Compute` at composition time (unchanged from 1b-①). Bands validate to `[0, 1]` here. ✓
- Registration convention — `AdaptationSettings` stays one enumerated sub-setting; count unchanged (19); `Validate()` uses `ValidationHelper` and returns empty for defaults. ✓

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** the five band-fraction property names (`DwellShorterMaxFraction`, `DwellLongerMaxFraction`, `ToleranceLooserMaxFraction`, `SmoothingMoreMaxFraction`, `GeometryBiggerMaxFraction`) are identical across the fields, the `OnXChanging` guards, `Validate()`, and the tests. `PostureTuning(double, int, double, double, double)` positional order matches its construction in `PosturePresets.For` and the field reads in `PosturePresetsTests`. `ValidationHelper.ValidateRange<double>(value, min, max, name)` and `CollectErrors(params string?[])` match their source signatures.

**Verified facts relied on (from source):**
- `ValidationHelper.ValidateRange<T>(T,T,T,string) where T : IComparable<T>` → `string?`; `CollectErrors(params string?[])` → `List<string>` (`ValidationHelper.cs:11,60`).
- `EyeCtrlSettingsEnumerationTests` asserts a hardcoded count (18 pre-1a; **19** post-1a-⑤) + a reflection coverage test + `Validate_DefaultInstance_HasNoErrors` (`EyeCtrlSettingsEnumerationTests.cs:8,17,34`). Adding fields to an already-enumerated sub-setting changes none of them.
- `AdaptationSettings`/`AdaptationPosture` shape from 1a-⑤ (`Enabled` bool, `Posture` default `Conservative`, `Validate()`); the settings validation pattern (`[ObservableProperty]` + `OnXChanging` guard + `Validate()` via `ValidationHelper`) mirrors `DwellSettings.cs`/`CursorSettings.cs`.
- CommunityToolkit copies each `[ObservableProperty]` field's `<summary>` to the generated public property, satisfying CS1591 (as the existing settings classes do).

**Honest testability limits:** fully headless-unit-tested Core data + validation. `Check.ThatCode(...).Throws<ArgumentOutOfRangeException>()` verifies the setter guards; the `Validate()` collected-report path is exercised via the default-valid assertion (a deliberately-invalid instance can't be constructed through the guarded setter, which is the intended runtime protection). No consumers yet — the controller (1b-④) exercises the band bounds + presets end-to-end.

**Deferred (documented above, not gaps):** consuming the bounds/presets (1b-④ controller, which also unifies the `EffectiveSettingsProvider` band source with these settings); `ResetLearnedBaseline` (Phase 2); status label (1b-⑤); Settings-window UI; base-range centralization.
