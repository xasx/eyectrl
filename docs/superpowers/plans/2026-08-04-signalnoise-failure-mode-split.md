# `SignalNoise` Failure-Mode Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the jitter signal out of `FailureMode.SustainFailure` into its own `FailureMode.SignalNoise`, so the smoothing knob is driven only by evidence smoothing can fix — without changing the behaviour of any knob that currently reaches a consumer.

**Architecture:** Six commits, each leaving the suite green. The fold logic is first extracted as a pure `internal static` and guarded by a table-driven test **before** the mode-map change makes the two-feeder path unused by production; only then does the map change. Core-only: `FailureMode`, `StruggleMonitor`, `AdaptationController` and their tests. No new types, no signature changes to public API, no App-layer changes.

**Tech Stack:** C# / .NET 10, xUnit v3, NFluent assertions (`Check.That(...)`, **not** `Assert.*` — wired globally in `tests/Directory.Build.props`, which also supplies global `using NFluent;` and `using Xunit;`).

---

## Context you need before starting

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — **§13.3** (this slice's rationale and decisions), §7.1 (failure mode → knob transform table), §13.2 (scoring design). Read §13.3 first; it explains why this ordering matters.

**Why the split exists.** `StruggleMonitor` classified jitter *and* abandonment into one `SustainFailure` mode scored `max(abandonment, jitter)`, and `AdaptationController` drove the smoothing knob off that mode. Smoothing trades jitter reduction for **latency**, so once smoothing is actually routed to a consumer (a later slice), abandonment that has nothing to do with signal noise would add cursor lag — plausibly causing more abandonment, raising the same mode, and relaxing smoothing further. The aggregation makes the loop invisible because nothing downstream can tell which feeder drove the score.

**The invariant this slice must preserve.** Only **one** knob currently reaches a consumer: `GazeEngine.cs:157` reads `Effective(AdaptationKnob.FixationRadius, DwellContext.Other, ...)`. Dwell, smoothing and geometry fills are stored and read by nobody. So the exit criterion is: **the effective value of `FixationRadius` and the `AggregateLevel` are unchanged for every evidence combination.** Dwell and smoothing drives *do* change — that is the point — but invisibly, because they are unrouted.

Why tolerance is unchanged, algebraically: writing `g` for a signal's gated score (its score when gated, else 0), the old drive is `max(max(g(abandonment), g(jitter)), g(drift))` and the new one is `max(g(abandonment), g(jitter), g(drift))` — identical by associativity.

**Mode reporting deliberately changes.** `AdaptationLog` logs the set of gated `FailureMode`s, so evidence that previously logged `SustainFailure` on jitter now logs `SignalNoise`. That is this slice's one externally visible effect and it is intended. `AdaptationCueService` is unaffected — it reads only `AggregateLevel`.

**Reading test output.** `dotnet test` on this machine emits **German**. Per-test failures are `Fehler <FullyQualifiedName>` — **not** `Failed`/`Fehlgeschlagen` — and long test names wrap across lines. The run summary is `Bestanden!` (passed) or `Fehler!` (failed), with `Fehler: N, erfolgreich: M, übersprungen: K, gesamt: T`. Grepping for `Failed` will silently report a green suite. Always read the summary line.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/EyeCtrl.Core/Adaptation/FailureMode.cs` | The mode enum + per-mode response docs | Add `SignalNoise`; correct `SustainFailure`'s doc |
| `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs` | Evidence windowing, scoring, classification | Extract `ClassifyMode` as `internal static`; rewire the mode map |
| `src/EyeCtrl.Core/Adaptation/AdaptationController.cs` | Mode → knob transforms, relax/recover stepping | Rewire tolerance and smoothing feeders |
| `tests/.../Adaptation/StruggleMonitorClassifyModeTests.cs` | **New.** The fold contract, in isolation | Create — the 16-combination fold table (12 + 4 theory rows) + the F4 regression |
| `tests/.../Adaptation/StruggleAssessmentTests.cs` | Type/enum drift guards | Enum arity 5 → 6 |
| `tests/.../Adaptation/StruggleMonitorTests.cs` | Monitor behaviour through `Assess` | Retarget the jitter test; move the F4 test out |
| `tests/.../Adaptation/AdaptationControllerTests.cs` | Controller transform map | Fix 2 smoothing assertions; add `SignalNoise` + identity tests |

The new test file exists because the fold contract is a distinct responsibility from `Assess`-level monitor behaviour, and after this slice it is the *only* place the two-feeder path is exercised.

---

### Task 1: Extract the fold as a pure `internal static`

Pure refactor. No behaviour change, no test changes — the existing suite must stay green, which is the proof.

Merely widening `AddMode` to `internal` would **not** work: it takes `ref int count` and mutates the `private readonly _modeBuffer`, so a test could call it but not observe the outcome without also exposing the buffer. A pure static returning `FailureModeAssessment?` is directly callable and keeps the buffer private.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs:113-132`

- [ ] **Step 1: Confirm the suite is green before touching anything**

Run: `dotnet test tests/EyeCtrl.Core.Tests`

Expected: `Bestanden!` with `Fehler: 0`. Note the `gesamt:` count — you will compare against it later. (At time of writing: 1018 total, 1 skipped.)

- [ ] **Step 2: Replace the two `AddMode` overloads with a wrapper plus the extracted static**

In `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`, replace the body of the second `AddMode` overload. The two overloads currently read:

```csharp
    private void AddMode(ref int count, FailureMode mode, SignalScore only) =>
        AddMode(ref count, mode, only, SignalScore.None);

    private void AddMode(ref int count, FailureMode mode, SignalScore first, SignalScore second)
    {
        double rawScore = Math.Max(first.Score, second.Score);
        if (rawScore <= 0.0)
            return; // no feeder shows any struggle → omit (ForMode returns null)

        double gatedScore = 0.0, gatedConfidence = 0.0;
        bool anyGated = false;
        Fold(first, ref gatedScore, ref gatedConfidence, ref anyGated);
        Fold(second, ref gatedScore, ref gatedConfidence, ref anyGated);

        // Actionable score/confidence come from gated feeders; if none gated the mode surfaces with the
        // (non-actionable) raw score and GatePassed=false, so the controller ignores it but monitoring sees it.
        double score = anyGated ? gatedScore : rawScore;
        double confidence = anyGated ? gatedConfidence : 0.0;
        _modeBuffer[count++] = new FailureModeAssessment(mode, score, confidence, anyGated, null);
    }
```

Replace them with:

```csharp
    private void AddMode(ref int count, FailureMode mode, SignalScore only) =>
        AddMode(ref count, mode, only, SignalScore.None);

    private void AddMode(ref int count, FailureMode mode, SignalScore first, SignalScore second)
    {
        if (ClassifyMode(mode, first, second) is { } assessment)
            _modeBuffer[count++] = assessment;
    }

    /// <summary>
    /// The pure fold from one or two feeding signals to a mode assessment, or <c>null</c> when the mode is
    /// omitted because no feeder shows any struggle. Extracted from <c>AddMode</c> so the multi-feeder fold
    /// contract stays directly testable (design §13.3): after the `SignalNoise` split every mode in the live
    /// map is single-feeder, so the two-feeder path is no longer exercised incidentally through
    /// <see cref="Assess"/>. <c>internal</c> for <c>EyeCtrl.Core.Tests</c> (the `InternalsVisibleTo` seam in
    /// `EyeCtrl.Core.csproj`); production callers go through <c>AddMode</c>.
    /// </summary>
    /// <param name="mode">The failure mode being classified.</param>
    /// <param name="first">The first feeding signal.</param>
    /// <param name="second">The second feeding signal, or <see cref="SignalScore.None"/> for a single-feeder mode.</param>
    /// <returns>The mode assessment, or <c>null</c> when the mode should be omitted entirely.</returns>
    internal static FailureModeAssessment? ClassifyMode(FailureMode mode, SignalScore first, SignalScore second)
    {
        double rawScore = Math.Max(first.Score, second.Score);
        if (rawScore <= 0.0)
            return null; // no feeder shows any struggle → omit (ForMode returns null)

        double gatedScore = 0.0, gatedConfidence = 0.0;
        bool anyGated = false;
        Fold(first, ref gatedScore, ref gatedConfidence, ref anyGated);
        Fold(second, ref gatedScore, ref gatedConfidence, ref anyGated);

        // Actionable score/confidence come from gated feeders; if none gated the mode surfaces with the
        // (non-actionable) raw score and GatePassed=false, so the controller ignores it but monitoring sees it.
        double score = anyGated ? gatedScore : rawScore;
        double confidence = anyGated ? gatedConfidence : 0.0;
        return new FailureModeAssessment(mode, score, confidence, anyGated, null);
    }
```

`Fold` stays exactly as it is — a `private static` helper, now called from `ClassifyMode`.

- [ ] **Step 3: Verify nothing changed**

Run: `dotnet test tests/EyeCtrl.Core.Tests`

Expected: `Bestanden!`, `Fehler: 0`, and the same `gesamt:` count as Step 1. A pure extraction cannot change any test outcome; if anything fails, the extraction was not faithful — diff it against the original before proceeding.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs
git commit -m "refactor(adaptation): extract the mode fold as a testable internal static

AddMode mutates a private buffer through a ref counter, so widening it to
internal would not let a test observe the outcome. ClassifyMode returns the
assessment (or null when omitted), keeping _modeBuffer private. Pure
extraction: no behaviour change, existing tests unchanged and green."
```

---

### Task 2: Guard the fold contract before it becomes unused

This task must land **before** Task 4. After the mode-map change no production mode has two real feeders, so the fold stops being exercised through `Assess` — the guard has to exist first, or there is a window where the contract is unprotected.

Satisfies §13.3's requirement for "a table-driven identity test enumerating both feeders' gate states × zero/non-zero scores". The 16 rows below include **both** directions of the F4 asymmetry (gated-zero + ungated-high, and its mirror) — the second of which the verification spike never reached, because its harness always passed jitter's volume gate.

**Files:**
- Create: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorClassifyModeTests.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs` — remove `Assess_GatedZeroScoreFeeder_DoesNotLiftUngatedHighScore_IntoGatedMode` (lines 241-270), which moves into the new file

- [ ] **Step 1: Write the new test file**

Create `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorClassifyModeTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

/// <summary>
/// The multi-feeder fold contract (design §13.2/§13.3), tested directly against
/// <see cref="StruggleMonitor.ClassifyMode"/>. After the `SignalNoise` split every mode in the live map is
/// single-feeder, so this is the only place the two-feeder path is exercised — see §13.3 for why it is kept
/// rather than deleted.
/// </summary>
public class StruggleMonitorClassifyModeTests
{
    private const double High = 0.8;

    private static SignalScore Sig(double score, bool gated) => new(score, gated ? 1.0 : 0.5, gated);

    // Every combination of (gate state × zero/non-zero score) for both feeders where at least one feeder
    // shows struggle. Expected values follow the §13.2 rule: the mode is GATED iff some feeder is gated, and
    // its actionable SCORE is the peak among GATED feeders only — falling back to the raw peak, with
    // GatePassed=false, when no feeder is gated (so monitoring can surface "warming up" without acting).
    [Theory]
    // firstScore, firstGated, secondScore, secondGated, expectedScore, expectedGatePassed
    [InlineData(0.0, true, High, true, High, true)]
    [InlineData(0.0, true, High, false, 0.0, true)]   // F4: gated zero must not be lifted by ungated high
    [InlineData(High, true, 0.0, true, High, true)]
    [InlineData(High, true, High, true, High, true)]
    [InlineData(High, true, 0.0, false, High, true)]
    [InlineData(High, true, High, false, High, true)]
    [InlineData(0.0, false, High, true, High, true)]
    [InlineData(High, false, 0.0, true, 0.0, true)]   // F4 mirror: the direction the spike never reached
    [InlineData(High, false, High, true, High, true)]
    [InlineData(0.0, false, High, false, High, false)] // nothing gated → raw peak, not actionable
    [InlineData(High, false, 0.0, false, High, false)]
    [InlineData(High, false, High, false, High, false)]
    public void ClassifyMode_ScoresFromGatedFeedersOnly(
        double firstScore, bool firstGated, double secondScore, bool secondGated,
        double expectedScore, bool expectedGatePassed)
    {
        FailureModeAssessment? m = StruggleMonitor.ClassifyMode(
            FailureMode.SustainFailure, Sig(firstScore, firstGated), Sig(secondScore, secondGated));

        Check.That(m.HasValue).IsTrue();
        Check.That(m!.Value.Mode).IsEqualTo(FailureMode.SustainFailure);
        Check.That(m.Value.Score).IsEqualTo(expectedScore);
        Check.That(m.Value.GatePassed).IsEqualTo(expectedGatePassed);
    }

    // The omission rule: a mode with no positive raw score is dropped entirely, even when a zero-score
    // feeder has passed its volume gate. An omitted mode makes ForMode return null, which the controller's
    // GatedScore reads as 0 — exactly what a present-but-zero mode contributes, so omission is behaviourally
    // equivalent and the split's tolerance identity holds through it.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ClassifyMode_OmitsModeWhenNoFeederShowsStruggle(bool firstGated, bool secondGated)
    {
        FailureModeAssessment? m = StruggleMonitor.ClassifyMode(
            FailureMode.SustainFailure, Sig(0.0, firstGated), Sig(0.0, secondGated));

        Check.That(m.HasValue).IsFalse();
    }

    /// <summary>
    /// F4 regression, PR #71 review blocker #1 — kept from the original
    /// `Assess_GatedZeroScoreFeeder_DoesNotLiftUngatedHighScore_IntoGatedMode`, retargeted from `Assess` to
    /// the fold itself so it survives the `SignalNoise` split (design §13.3). A gated-but-zero-score feeder
    /// combined with an ungated-but-high-score feeder must NOT yield a mode carrying the high score:
    /// pre-fix the peak across ALL feeders was 0.8 with an OR'd gate, which would have driven adaptation on
    /// ungated evidence.
    /// </summary>
    [Fact]
    public void ClassifyMode_GatedZeroScoreFeeder_DoesNotLiftUngatedHighScore()
    {
        SignalScore gatedZero = Sig(0.0, gated: true);      // e.g. zero jitter meeting its volume gate
        SignalScore ungatedHigh = Sig(0.8, gated: false);   // e.g. abandonment below its 4-attempt gate

        FailureModeAssessment? m = StruggleMonitor.ClassifyMode(
            FailureMode.SustainFailure, gatedZero, ungatedHigh);

        Check.That(m.HasValue).IsTrue();
        Check.That(m!.Value.GatePassed).IsTrue();  // a feeder gated…
        Check.That(m.Value.Score).IsEqualTo(0.0);  // …but the actionable score is the GATED peak, never 0.8
    }

    // A single-feeder mode passes SignalScore.None as the absent second feeder, which is gate-failed with
    // score 0 and so must fold as a no-op. PoorTracking is used simply because it is already single-feeder;
    // the mode identity is irrelevant to what this asserts.
    [Fact]
    public void ClassifyMode_SingleFeeder_IgnoresTheAbsentSecond()
    {
        FailureModeAssessment? m = StruggleMonitor.ClassifyMode(
            FailureMode.PoorTracking, Sig(0.6, gated: true), SignalScore.None);

        Check.That(m.HasValue).IsTrue();
        Check.That(m!.Value.Score).IsEqualTo(0.6);
        Check.That(m.Value.GatePassed).IsTrue();
    }
}
```

Every mode named in this file already exists on `main`, so the file compiles before Task 3 adds `SignalNoise`.

**Already covered elsewhere — do not duplicate.** §13.3 also calls for the single-feeder F4 property that "an ungated high score surfaces with `GatePassed = false` and contributes 0 to the drive". The *surfacing* half is rows 10-12 of the table above. The *drive* half is already asserted at `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs:79`, which ticks `Mode(FailureMode.SustainFailure, 0.9, gated: false)` and expects baseline. Since `GatedScore` is mode-agnostic, that one test covers the mechanism for every mode; adding a `SignalNoise` variant would be redundant.

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleMonitorClassifyModeTests"`

Expected: compile error — `ClassifyMode` is inaccessible or undefined — **if Task 1 was skipped**. If Task 1 landed, expect `Bestanden!` with 17 tests passing immediately: this task characterises behaviour that already exists, so passing-on-first-run is the correct outcome. That is the point — these tests lock in current behaviour so Task 4 cannot silently change it.

- [ ] **Step 3: Delete the superseded test from `StruggleMonitorTests.cs`**

Delete the whole `Assess_GatedZeroScoreFeeder_DoesNotLiftUngatedHighScore_IntoGatedMode` method — its `[Fact]` attribute, its comment block, and its body (currently lines 241-270, from `[Fact]` through the closing `}` after the `AggregateLevel` assertion). Its coverage now lives in the new file, retargeted at the fold.

- [ ] **Step 4: Verify the suite is green with the test relocated**

Run: `dotnet test tests/EyeCtrl.Core.Tests`

Expected: `Bestanden!`, `Fehler: 0`. Total count rises by 17 — 18 new tests (12 fold rows + 4 omission rows + 2 facts) minus the 1 deleted.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorClassifyModeTests.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs
git commit -m "test(adaptation): guard the two-feeder fold contract directly

Table-driven over both feeders' gate states x zero/non-zero scores, including
both directions of the F4 asymmetry — the mirror direction (gated zero
abandonment + ungated high jitter) was never reachable in the verification
spike, whose harness always passed jitter's volume gate.

Lands before the mode-map change: afterwards no production mode has two real
feeders, so the fold stops being exercised through Assess and needs its own
guard. The original Assess-level F4 test moves here, retargeted."
```

---

### Task 3: Add `FailureMode.SignalNoise`

Adding the member wires nothing, so this is behaviour-neutral on its own. `_modeBuffer` sizes itself from the enum (`new FailureModeAssessment[Enum.GetValues<FailureMode>().Length]`), so no buffer change is needed.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/FailureMode.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleAssessmentTests.cs:16-27`

- [ ] **Step 1: Update the enum drift-guard test to expect six modes**

In `tests/EyeCtrl.Core.Tests/Adaptation/StruggleAssessmentTests.cs`, replace:

```csharp
    [Fact]
    public void FailureMode_DefinesTheFiveResponseModes()
    {
        // Locks the §7.1/§13.2 mode set so the monitor (1b-③b) and controller (1b-④) can rely on it.
        var modes = Enum.GetValues<FailureMode>();
        Check.That(modes).HasSize(5);
        Check.That(modes).Contains(
            FailureMode.SustainFailure,
            FailureMode.AccidentalActivation,
            FailureMode.Aiming,
            FailureMode.Drift,
            FailureMode.PoorTracking);
    }
```

with:

```csharp
    [Fact]
    public void FailureMode_DefinesTheSixResponseModes()
    {
        // Locks the §7.1/§13.2 mode set so the monitor and controller can rely on it. SignalNoise was split
        // out of SustainFailure in §13.3 so the smoothing response is driven only by noise evidence.
        var modes = Enum.GetValues<FailureMode>();
        Check.That(modes).HasSize(6);
        Check.That(modes).Contains(
            FailureMode.SustainFailure,
            FailureMode.AccidentalActivation,
            FailureMode.Aiming,
            FailureMode.Drift,
            FailureMode.PoorTracking,
            FailureMode.SignalNoise);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~FailureMode_DefinesTheSixResponseModes"`

Expected: compile error — `FailureMode.SignalNoise` does not exist.

- [ ] **Step 3: Add the enum member and correct `SustainFailure`'s doc**

In `src/EyeCtrl.Core/Adaptation/FailureMode.cs`, replace `SustainFailure`'s doc comment:

```csharp
    /// <summary>High-progress abandonment and/or jitter (plus retries): shorter dwell, looser tolerance, more smoothing.</summary>
    SustainFailure,
```

with:

```csharp
    /// <summary>High-progress abandonment (plus retries): shorter dwell and looser tolerance. Jitter is
    /// classified separately as <see cref="SignalNoise"/> (design §13.3), so abandonment alone never adds
    /// smoothing lag.</summary>
    SustainFailure,
```

Then append the new member **after** `PoorTracking` (appending avoids renumbering the existing values):

```csharp
    /// <summary>High invalid-sample fraction: more smoothing, enlarge geometry, caregiver alert if sustained; never tighten.</summary>
    PoorTracking,

    /// <summary>Unstable gaze signal (high fixation jitter): more smoothing and looser tolerance. Split out
    /// of <see cref="SustainFailure"/> (design §13.3) so the smoothing response answers only to evidence
    /// smoothing can fix — added latency is itself a struggle risk when the cause is abandonment, not noise.</summary>
    SignalNoise,
}
```

- [ ] **Step 4: Verify the whole suite is green**

Run: `dotnet test tests/EyeCtrl.Core.Tests`

Expected: `Bestanden!`, `Fehler: 0`. The member is unreferenced by production code, so nothing else moves.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/FailureMode.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleAssessmentTests.cs
git commit -m "feat(adaptation): add the SignalNoise failure mode

Unwired so far — the member alone changes no behaviour. _modeBuffer sizes
itself from the enum, so it needs no change. Also corrects SustainFailure's
doc, whose 'and/or jitter ... more smoothing' both become false next."
```

---

### Task 4: Rewire the monitor's mode map

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs:77-82`
- Modify: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs:157-170`

- [ ] **Step 1: Rewrite the jitter classification test for the new mode**

In `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs`, replace:

```csharp
    [Fact]
    public void Assess_JitterAlone_ContributesToSustainFailure()
    {
        // No dwell; only high-jitter quality (mean 500 ≥ high 400, ≥ 3 summaries) → jitter gates →
        // SustainFailure present (jitter is a sustain-failure feeder alongside abandonment).
        var monitor = new StruggleMonitor(SinkWith(quality: new[]
        {
            Quality(0, 0.0, 500), Quality(Sec, 0.0, 500), Quality(2 * Sec, 0.0, 500),
        }), EyeCare());
        var a = monitor.Assess(3 * Sec);

        Check.That(a.ForMode(FailureMode.SustainFailure)!.Value.GatePassed).IsTrue();
    }
```

with:

```csharp
    [Fact]
    public void Assess_JitterAlone_ClassifiesAsSignalNoise_NotSustainFailure()
    {
        // No dwell; only high-jitter quality (mean 500 ≥ high 400, ≥ 3 summaries) → jitter gates. Per §13.3
        // jitter is its own mode: SignalNoise gates, and SustainFailure — now fed by abandonment alone, of
        // which there is none — is omitted entirely.
        var monitor = new StruggleMonitor(SinkWith(quality: new[]
        {
            Quality(0, 0.0, 500), Quality(Sec, 0.0, 500), Quality(2 * Sec, 0.0, 500),
        }), EyeCare());
        var a = monitor.Assess(3 * Sec);

        Check.That(a.ForMode(FailureMode.SignalNoise)!.Value.GatePassed).IsTrue();
        Check.That(a.ForMode(FailureMode.SustainFailure).HasValue).IsFalse();
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~Assess_JitterAlone"`

Expected: `Fehler!` — `ForMode(FailureMode.SignalNoise)` returns null (the mode is not produced yet), so dereferencing `!.Value` throws `InvalidOperationException`.

- [ ] **Step 3: Rewire the mode map**

In `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`, replace:

```csharp
        AddMode(ref modeCount, FailureMode.SustainFailure, abandonment, jitter); // §13.2: abandonment(+retries, folded in ③a) + jitter
        AddMode(ref modeCount, FailureMode.AccidentalActivation, accidental);
        AddMode(ref modeCount, FailureMode.Aiming, aiming);
        AddMode(ref modeCount, FailureMode.Drift, drift);
        AddMode(ref modeCount, FailureMode.PoorTracking, poorTracking);
```

with:

```csharp
        // §13.3: every mode is single-feeder. SustainFailure is abandonment(+retries, folded in ③a) alone;
        // jitter is SignalNoise. Tolerance's drive is unchanged because the controller takes the peak across
        // both modes — max(max(a, j), drift) == max(a, j, drift) — see §13.3.
        AddMode(ref modeCount, FailureMode.SustainFailure, abandonment);
        AddMode(ref modeCount, FailureMode.AccidentalActivation, accidental);
        AddMode(ref modeCount, FailureMode.Aiming, aiming);
        AddMode(ref modeCount, FailureMode.Drift, drift);
        AddMode(ref modeCount, FailureMode.PoorTracking, poorTracking);
        AddMode(ref modeCount, FailureMode.SignalNoise, jitter);
```

- [ ] **Step 4: Verify the new test passes and the suite is green**

Run: `dotnet test tests/EyeCtrl.Core.Tests`

Expected: `Bestanden!`, `Fehler: 0`. The two `AdaptationControllerTests` smoothing assertions still pass at this point — they construct `StruggleAssessment` values directly rather than going through the monitor, so they are unaffected until Task 5.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs
git commit -m "feat(adaptation): classify jitter as SignalNoise, not SustainFailure

SustainFailure becomes single-feeder (abandonment + retries); jitter gets its
own mode. Tolerance's drive is unchanged by associativity of the peak, so the
one routed knob is unaffected; the controller's feeder map follows next."
```

---

### Task 5: Rewire the controller's feeders

This is where the smoothing drive actually moves off abandonment. **The two knobs whose drives change here are unrouted**, so no consumer sees it — but the tolerance identity is asserted explicitly.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/AdaptationController.cs:79-81` and `:88-90`
- Modify: `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs:164-174` and `:214-224`

- [ ] **Step 1: Fix the two tests that assert the old smoothing coupling, and add the new coverage**

In `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs`, replace:

```csharp
    public void SustainFailure_LoosensTolerance_ShortensDwell_IncreasesSmoothing_NotGeometry()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); // fill 0.10 on each driven knob

        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);   // +looser
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsCloseTo(672.0, 1e-9);      // −shorter (−0.4×700×0.10 = −28)
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // −more (−0.5×5×0.10 = −0.25)
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsEqualTo(100.0);        // sustain-failure does NOT enlarge geometry
    }
```

with:

```csharp
    public void SustainFailure_LoosensTolerance_ShortensDwell_NotSmoothingOrGeometry()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); // fill 0.10 on each driven knob

        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);   // +looser
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsCloseTo(672.0, 1e-9);      // −shorter (−0.4×700×0.10 = −28)
        // §13.3: abandonment must NOT add smoothing. Smoothing buys jitter reduction with latency, so
        // relaxing it here would add cursor lag to a user already failing to complete dwells.
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsEqualTo(5.0);
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsEqualTo(100.0);        // sustain-failure does NOT enlarge geometry
    }

    [Fact]
    public void SignalNoise_IncreasesSmoothingAndLoosensTolerance_NotDwellOrGeometry()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SignalNoise, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // −more (−0.5×5×0.10 = −0.25)
        // Tolerance stays on noise too (§13.3): a looser fixation radius is a correct response to a noisy
        // signal, and keeping it is what makes this split behaviour-neutral for the one routed knob.
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);
        // Shortening dwell on a noisy signal would invite the accidental activations the quarantined
        // tightening exists to suppress, so noise does not drive dwell.
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsEqualTo(100.0);
    }

    // The §13.3 identity for the ONE routed knob: tolerance's drive is the peak gated score across its three
    // feeders, so which feeder supplies the peak cannot matter. Modes are passed with explicit scores
    // (including 0) because the controller reads whatever it is given — the monitor's own omission of
    // zero-score modes is equivalent, since GatedScore reads an absent mode as 0.
    [Theory]
    [InlineData(0.7, 0.0, 0.0, 52.5)] // sustain-failure supplies the peak
    [InlineData(0.0, 0.7, 0.0, 52.5)] // drift supplies it
    [InlineData(0.0, 0.0, 0.7, 52.5)] // signal-noise supplies it (the feeder this split added)
    [InlineData(0.3, 0.3, 0.7, 52.5)] // peak wins over sub-threshold feeders
    [InlineData(0.7, 0.7, 0.7, 52.5)] // all three at once — still one step, not three
    [InlineData(0.3, 0.3, 0.3, 50.0)] // all below RelaxThreshold 0.5 → deadband, no relaxation
    public void Tolerance_DriveIsPeakOfSustainFailureDriftAndSignalNoise(
        double sustainFailure, double drift, double signalNoise, double expectedRadius)
    {
        var (c, p, _) = Make();
        var a = Assess(
            Mode(FailureMode.SustainFailure, sustainFailure),
            Mode(FailureMode.Drift, drift),
            Mode(FailureMode.SignalNoise, signalNoise));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(expectedRadius, 1e-9);
    }
```

Then replace:

```csharp
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);            // dwell disabled
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);   // tolerance still adapts
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // smoothing still adapts
```

with:

```csharp
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);            // dwell disabled
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);   // tolerance still adapts
        // Smoothing is NOT driven by sustain-failure any more (§13.3), so it stays at baseline here; that a
        // per-knob disable leaves the other knobs adapting is shown by tolerance above.
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsEqualTo(5.0);
```

- [ ] **Step 2: Run the controller tests to verify the new ones fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationControllerTests"`

Expected: `Fehler!`. Specifically `Fehler …SignalNoise_IncreasesSmoothingAndLoosensTolerance_NotDwellOrGeometry` (smoothing is still at baseline 5.0 because `SignalNoise` drives nothing yet, and tolerance is 50.0 for the same reason), plus failures in `Tolerance_DriveIsPeakOfSustainFailureDriftAndSignalNoise` on the signal-noise-only row. The two edited tests fail too, since smoothing still moves on sustain-failure.

- [ ] **Step 3: Rewire the feeders**

In `src/EyeCtrl.Core/Adaptation/AdaptationController.cs`, replace the tolerance call:

```csharp
            // Tolerance: loosen (forgiving = larger) on sustain-failure OR drift (union of forgiving responses, §8.2).
            UpdateKnob(AdaptationKnob.FixationRadius, _adaptation.AdaptTolerance, tuning, nowMicroseconds,
                forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure, FailureMode.Drift),
                tighteningDrive: 0.0);
```

with:

```csharp
            // Tolerance: loosen (forgiving = larger) on sustain-failure OR drift OR signal-noise (union of
            // forgiving responses, §8.2). SignalNoise was folded into SustainFailure before §13.3, so adding
            // it here is what keeps this knob's drive — the only one with a live consumer — unchanged.
            UpdateKnob(AdaptationKnob.FixationRadius, _adaptation.AdaptTolerance, tuning, nowMicroseconds,
                forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure, FailureMode.Drift, FailureMode.SignalNoise),
                tighteningDrive: 0.0);
```

and the smoothing call:

```csharp
            // Smoothing: more (parameter down) on sustain-failure OR poor-tracking (union).
            UpdateKnob(AdaptationKnob.Smoothing, _adaptation.AdaptSmoothing, tuning, nowMicroseconds,
                forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure, FailureMode.PoorTracking),
                tighteningDrive: 0.0);
```

with:

```csharp
            // Smoothing: more (parameter down) on signal-noise OR poor-tracking (union) — never on
            // sustain-failure (§13.3). More smoothing buys jitter reduction with latency, so driving it from
            // abandonment that is not noise-caused would add cursor lag to a user already failing to complete
            // dwells, plausibly causing more abandonment: a feedback loop the old max(abandonment, jitter)
            // fold made invisible.
            UpdateKnob(AdaptationKnob.Smoothing, _adaptation.AdaptSmoothing, tuning, nowMicroseconds,
                forgivingDrive: ForgivingDrive(assessment, FailureMode.SignalNoise, FailureMode.PoorTracking),
                tighteningDrive: 0.0);
```

Leave the dwell and geometry calls untouched: dwell forgiving stays on `SustainFailure` (now abandonment-only), its tightening stays quarantined to `AccidentalActivation`, and geometry stays on `Aiming` + `PoorTracking`.

- [ ] **Step 4: Verify the controller tests pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationControllerTests"`

Expected: `Bestanden!`, `Fehler: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/AdaptationController.cs tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs
git commit -m "feat(adaptation): drive smoothing from SignalNoise, never from abandonment

Smoothing's feeders become SignalNoise + PoorTracking; tolerance gains
SignalNoise so its drive stays max(abandonment, jitter, drift) — unchanged for
the one knob with a live consumer, asserted by a table-driven identity test.

Dwell keeps SustainFailure (now abandonment-only) and its quarantined
AccidentalActivation tightening; geometry is untouched."
```

---

### Task 6: Full verification

The per-task runs only covered `EyeCtrl.Core.Tests`. CI also runs the ViewModels and App headless subsets, and the App tests are the ones that historically caught stale assertions this kind of change can leave behind.

**Files:** none — verification only.

- [ ] **Step 1: Run the full Core suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests`

Expected: `Bestanden!`, `Fehler: 0`, and a `gesamt:` count **higher** than the Task 1 baseline — 18 fold tests and 1 `SignalNoise` response test and 6 identity theory rows are added, 1 test is relocated, and the rest are edited in place. Confirm `Fehler: 0` and that the total rose; do not try to match an exact number, since xUnit counts each `[InlineData]` row separately and the baseline depends on when you started.

- [ ] **Step 2: Run the ViewModels suite**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests`

Expected: `Bestanden!`, `Fehler: 0`. Nothing here touches adaptation, so a failure means something unexpected.

- [ ] **Step 3: Run the App headless subset — the one CI runs**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`

Expected: `Bestanden!`, `Fehler: 0`. Do **not** run App.Tests without the filter: the `Category=UI` tests launch the real application, which seizes the repo owner's only input channel. Roughly 13 of them also fail in any headless environment for environmental reasons unrelated to this change.

`AdaptationLog` lives in this project and logs the gated-mode set, so if any App test asserts on logged mode **names**, this is where it surfaces. That would be an expected update, not a regression — see §13.3's exit criterion.

- [ ] **Step 4: Confirm the change surface is exactly what was intended**

Run: `git diff --stat main`

Expected: exactly six files — three under `src/EyeCtrl.Core/Adaptation/`, three under `tests/EyeCtrl.Core.Tests/Adaptation/`, plus the new test file. **No** `src/EyeCtrl.App/`, `src/EyeCtrl.Platform/`, or `GazeEngine.cs` changes: this slice does not route anything.

- [ ] **Step 5: Verify the exit criterion honestly, and record what was not verified**

The §13.3 exit criterion is that `FixationRadius`'s effective value and `AggregateLevel` are unchanged for every evidence combination. The algebra is the proof; `Tolerance_DriveIsPeakOfSustainFailureDriftAndSignalNoise` and the 16-combination fold table are the regression guards.

State plainly in the PR description that the differential 288-scenario sweep from the design phase was a throwaway spike, **not** part of this suite, and that its grid never reached several relevant states (it always passed jitter's volume gate, so jitter was never ungated; it straddled no gate boundary; it preloaded all evidence before the first tick). Do not describe the split as exhaustively verified.

- [ ] **Step 6: Open the PR**

```bash
git push -u origin docs/struggle-detection-signalnoise-split
```

Then open a PR against `main` titled `feat(adaptation): split jitter into its own SignalNoise failure mode`, describing: the lag→abandonment feedback loop this forecloses, the behaviour-neutrality argument for the routed knob, the intended change to logged mode names, and the fact that this unblocks the smoothing-routing slice (spec §13.3, Phase 1c).

---

## Notes for the implementer

**Do not run the app or simulate input.** The repo owner controls their PC solely by eye tracking; driving the cursor or keyboard takes away their only input channel. Everything in this plan is verifiable headlessly.

**If a step's expected output does not match**, stop rather than adapting the plan. The commit-per-task structure means each task's failure is diagnosable in isolation, and a surprise usually means the source drifted from what this plan was written against (`main` at `b590a87`).

**The smoothing knob has a latent safety trap that this slice does not fix.** Spec §13.3 records it: `OneEuroMinCutoff` validates `[0.1, 20]` but the provider's `Smoothing` policy carries `ValidMin = 0.0`, so a `0.1 Hz` baseline composes below its validated floor — and at fraction `1.0` reaches exactly `0.0`, which `OneEuroFilter`'s constructor rejects, throwing on the gaze thread inside an unguarded `ApplyPendingInterpolatorRebuild`. It is unreachable today because the knob has no consumer. **Do not route smoothing as part of this slice**; the clamp is a requirement on the smoothing-routing slice.
