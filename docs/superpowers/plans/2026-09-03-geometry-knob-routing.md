# Geometry-Knob Routing Implementation Plan (Phase 1c, slice 3 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route the `AdaptationKnob.TargetGeometry` momentary fill to every live dwell surface's hit geometry — hit-area margins only, state-preserving, identity-restoring, absolutely bounded, and inert while adaptation is disabled — completing Phase 1c and opening the Phase-2 gate.

**Architecture:** The dwell slice's registry is the shared discovery substrate (§13.3): `DwellRoutingRegistration` gains a second applier that pushes a base-extent→effective-extent map, auto-wired by `ForDetector` so no registration call site changes except External's lock-wrapping construction. `DwellDetector.SetEffectiveGeometry(map)` mirrors `SetEffectiveDwellTime` (born-effective on `AddTarget`, null restores the as-registered geometry, margin composed against the retained base extent — the §13.3 unit-mismatch resolution: the detector converts the absolute extent the provider composes into the signed margin `ApplyHitAreaMargin` consumes). A small App-side `GeometryRouter` — the `DwellTimeRouter` shape — applies the map on provider `Changed` + `RegistrationChanged`, clamping the margin to the new §13.1 feature-introduced bound `AdaptationSettings.GeometryMaxMarginPx` (consumer-side clamp, the smoothing/dwell precedent). The visible size never changes (owner decision 2026-09-03 — see Global Constraints) with one sanctioned exception: External's dwell-feedback overlay sizes itself from the routed rect by construction (§8.9's visible⇒hit direction, not a violation — round-1 finding 2). External's SDK-pushed rect updates go through a new `DwellDetector.RebaseTarget` so any later map application — including the identity restore — can never revert a foreign resize to stale base extents (round-1 finding 1, the blocker).

**Tech Stack:** C# / .NET 10, xUnit v3, NFluent assertions (`Check.That(...)`, **not** `Assert.*` — wired globally in `tests/Directory.Build.props`, which also supplies global `using NFluent;` and `using Xunit;`).

---

## Global Constraints

- **Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §5 (value model, "surfaces read effective values at (re)creation", the unit-mismatch note in `EffectiveSettingsProvider`'s policy table), §7.3/C4 (in-progress dwell during geometry change), §8.4/§8.9/§13.1 (the feature-introduced absolute bound this slice owns), §13.3 (this slice's mandate: the "Geometry additionally" bullet). Read §13.3's "Dwell and geometry" bullet and its dwell-resolution block first.
- **This is slice 3 of the Phase-1c sequence (§13.3: smoothing → dwell → geometry), the last one.** Do NOT change `AdaptationController` or `StruggleMonitor` (the TargetGeometry fill is already pushed — Aiming ∪ PoorTracking under `DwellContext.Other`; per-context fills are Phase 2). Do NOT touch `GazeEngine` (its routing slices are done).
- **Hit areas only (owner decision, 2026-09-03).** The spec's Aiming response (§7.1, §13.1) says "enlarge target geometry … and coupled visible size, atomically". This slice enlarges HIT GEOMETRY ONLY: no EyeCtrl layout or drawn element changes size anywhere. §8.9 ("visible size never changes without hit geometry changing with it") still holds — not vacuously (round-1 finding 2): the one visible element coupled to hit geometry is **External's dwell-feedback overlay**, which reads the routed `target.Bounds` on every frame and is sized from it (`OnCursorMoved` → `SetFeedback` → `DwellFeedbackOverlay.ShowAt`). That coupling is the sanctioned visible⇒hit direction and is accepted; do NOT "fix" `ShowAt` to draw base extents. External targets (foreign UI we cannot resize) prove hit-only is the only universal shape; the rendering side of visible coupling is only verifiable via `Category=UI` tests this repo never runs. Task 6 amends the spec to record the scoping. **Do not "complete" the spec's visible coupling here.**
- **The absolute bound (owner decision, 2026-09-03):** a new `AdaptationSettings.GeometryMaxMarginPx` (double, default **100 physical px**, setter guard + `Validate()` range **[0, 300]**), exposed as an Adaptation-page slider row like the five band fractions. The provider's `TargetGeometry` policy stays fraction-bounded with `ValidMax = double.MaxValue`; the routing layer applies the absolute clamp — the exact consumer-side-clamp precedent of smoothing (per-filter clamp in `GazeEngine`) and dwell (per-surface ranges in `DwellTimeRouter`).
- **Keyboard keys are geometry-exempt (owner decision, 2026-09-03).** `KeyboardViewModel.ProcessKeyGaze` is nearest-key tracked with **no hit boundary** (dead-zone-free by design — its own doc comment), so the spec's "key hit slop" (§5) has no boundary to enlarge; enlarging it would mean re-introducing dead zones, a behaviour change. The Keyboard's **prediction detector** (circular radius targets in `KeyboardWindow`) IS routed like every other registered surface. `ScrollOverlayViewModel` stays exempt (not registered — inert, per-session); Settings' four detectors stay unregistered (the remedy surface must not adapt, §2). State the exemptions; do not wire them.
- **Never run the application or any `Category=UI` test.** The repo owner controls their PC solely by eye tracking; launching it seizes their only input channel. Everything here is verifiable headlessly.
- **One test suite at a time**, sequential (named pipes are machine-global; this box routinely runs a dozen worktrees and possibly a live app).
- **German test output.** Failures read `Fehler <name>`; the summary reads `Bestanden!` / `Fehler!` with `Fehler: N, erfolgreich: M, gesamt: T`. Read the summary line; grepping "Failed" silently reports green.
- Baselines at `main` = `c8a4361b`: **Core 1148 / ViewModels 300 / App (Category!=UI) 735 / Benchmarks.Tests 27**, all green. Different starting counts ⇒ stop and report.
- NFluent only; no mocking library (private fakes); file-scoped namespaces; Allman braces; `_camelCase` fields; nullable enabled. `EyeCtrl.Core` escalates CS1591 (missing XML doc) to an **error** — every new public member needs its `///`. `InternalsVisibleTo` → `EyeCtrl.Core.Tests` exists (`EyeCtrl.Core.csproj`).
- **If a step's expected output does not match, stop rather than adapting.** Anchors reference `main` at `c8a4361b`; drift ⇒ report BLOCKED with what you saw.

---

## Context you need before starting

**What exists.** The dwell slice (PR #132) landed: `IDwellDetectorRegistry`/`DwellDetectorRegistry` (owner-keyed, snapshot-on-read, `RegistrationChanged` raised outside the lock), `DwellRoutingRegistration` (Context + MinDwellMs + MaxDwellMs + an applier delegate), `AdaptationDwellRanges`, `DwellTimeRouter` (App), and `DwellDetector.SetEffectiveDwellTime` + `DwellTarget`'s per-target dwell baseline. The 1a-⑤ geometry substrate (`DwellDetector.UpdateTargetGeometry`, `DwellTarget.ApplyHitAreaMargin` with `_baseRadius`/`_baseHalfWidth`/`_baseHalfHeight`) exists and is tested (6 facts in `DwellTargetUpdateTests`) but has **zero production callers** — that is the unrouted state this slice ends. The provider already composes `TargetGeometry` (forgiving-only, `+GeometryBiggerMaxFraction` × baseline, `[0, double.MaxValue]`), and `AdaptationController.Tick` already pushes that fill; nobody reads it.

**Facts that shape this plan:**

1. **Geometry is a margin problem, not an extent problem.** `ApplyHitAreaMargin(marginPx)` outsets a target relative to its as-registered extents (circle radius, or both rect half-extents — `HitTest` ignores `Radius` when `Bounds` is set), idempotently, state-preserving. The provider composes an ABSOLUTE extent (`baseline + fill × fraction × baseline`). The §13.3 unit-mismatch resolution: the map takes the target's **base hit extent** — `_baseRadius` for circles, `Math.Min(_baseHalfWidth, _baseHalfHeight)` for rects (gaze error is isotropic, so the margin is uniform on every side and the narrow dimension — the binding constraint — sets the scale) — and the detector applies `margin = map(base) − base`.
2. **`ForDetector` auto-wiring makes the registration slice nearly free.** Every registered surface wants geometry (there are no geometry exemptions among registered surfaces — keys/ScrollOverlay/Settings are *unregistered*). Extending the record with a required `ApplyGeometry` and wiring it inside `ForDetector` changes **zero** registration call sites except ExternalDwellService's manual lock-wrapping construction (and the one test fake that constructs the record positionally — a deliberate, loud compile break).
3. **The mid-flight semantics already exist and are specified.** §7.3/C4: enlarge mid-dwell keeps progress (gaze already inside stays inside — nothing to do); a recovery-driven shrink that leaves the gaze outside ends the dwell on the next `Update` as an ordinary gaze-left. `UpdateTargetGeometry`'s doc comment pins this; the routing map inherits it. Tests pin the map-level behaviour.
4. **The External surface owns the only rect targets** (`ExternalDwellService.cs:281,300`) and serializes ALL detector access under `_stateLock` on the gaze thread — its geometry applier wraps the call in that lock, exactly like its dwell applier. **Its `UpdateMessage` branch mutates a live target in place** (`existing.Bounds = rect; existing.Center = rect.Center;` at `:295-296`) while `_baseHalfWidth`/`_baseHalfHeight` are `readonly` and captured at construction — so once ANY `SetEffectiveGeometry` runs (including the null/disabled margin-0 restore, which `ApplyHitAreaMargin` executes by recomputing `Bounds` from the base half-extents), an SDK-pushed resize reverts to the stale size. Reachable via every `RegistrationChanged` (e.g. each manual-selection session start) and every provider `Changed`. **Round-1 blocker — fixed by the `RebaseTarget` seam (Task 1) wired into the `UpdateMessage` branch (Task 3), pinned by tests in Tasks 1 and 4.**
5. **A settings-row edit does not re-apply the margin cap until the next `Changed`** (the provider fires `Changed` only on fill set/clear — the §13.3 smoothing leftover class). Bounded staleness, self-healing, no safety impact: the stale value is still inside the old cap. Documented in `GeometryRouter`'s doc comment; owner: a future settings-notification slice.
6. **Routing couples the Aiming signal's own denominator to the knob's output** (round-1 finding 8): `DwellOutcome` captures the LIVE `target.Radius`/`Bounds` at outcome time, and `StruggleSignals.ScoreAiming` divides `|NearMiss|` by it (rect/`TargetRadius <= 0` outcomes are already excluded, so External never feeds Aiming). After routing, a grown radius compresses the ratio toward the `lowRatio` 1.0 ramp floor — the response damps its own driver, which is the desired thermostat behaviour (enlargement extinguishes the evidence that requested it), and the controller's deadband + 0.05–0.10/tick ramp preclude chatter. **Decision (owner-vetoable): document, do not change the signal.** Reporting `BaseHitExtent` in `DwellOutcome` instead would ALSO change the accidental-activation re-approach radius (2x `TargetRadius`, §13.1) where the live hit radius is arguably the right measure — a Phase-2-era calibration decision, not a routing-slice one. Task 6 records it under "not verified".
7. **External's dwell-feedback overlay is the one visible element that grows with the routed hit area** (round-1 finding 2 — see Global Constraints). Every other surface draws nothing from `DwellTarget` geometry.

**Design decisions (plan-level; the review rounds may overturn them):**

- **One registration, two appliers.** The record carries `Action<Func<int,int>?> Apply` (dwell) and `Action<Func<double,double>?> ApplyGeometry` (geometry) side by side. Alternatives rejected: a (owner, knob)-keyed registry (breaks every call site and guard test for no gain); a second parallel registry (duplicates the event/machinery; two `Register` calls per owner to drift apart); optional-with-default geometry (a silently-absent applier is exactly the mutation class the wiring tests exist to catch).
- **A second App-side router, `GeometryRouter`.** Mirrors `DwellTimeRouter`'s subscription skeleton (~15 lines) rather than generalizing/renaming it — each router stays single-knob and independently testable, and the existing router's tests/name are untouched.
- **Identity-restore, not teardown.** Disabled/no-fill ⇒ `Effective` returns the baseline ⇒ `margin 0` ⇒ `ApplyHitAreaMargin(0)` restores the exact as-registered geometry. No special-casing, no null-path divergence (the null map IS margin 0).
- **No budget machinery.** In-place, idempotent apply — the tolerance/dwell precedent; `Changed` fires ≤ once per 250 ms tick.
- **Rebase, not raw writes, for live external geometry.** `ExternalDwellService`'s `UpdateMessage` must go through `DwellDetector.RebaseTarget(id, rect)` (rebase the as-registered extents + re-apply the stored map) rather than writing `Bounds`/`Center` directly. Replacing the target via `AddTarget` was rejected: it discards the in-progress dwell the in-place path deliberately preserves. With no map stored, `RebaseTarget` is behaviour-identical to today's raw write (margin 0 recomputes the exact new rect).
- **The cap clamps the margin, not the extent.** §13.1 bounds the *growth*. `margin = clamp(effective − base, 0, GeometryMaxMarginPx)`; identity restores are never truncated (margin 0 is always in `[0, cap]`, `cap ≥ 0` by validation — the External-truncation bug class cannot exist here).

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/EyeCtrl.Core/Processing/DwellTarget.cs` | Base hit extent + rebase | Add `BaseHitExtent`, `Rebase` (rect base extents become mutable) |
| `src/EyeCtrl.Core/Processing/DwellDetector.cs` | Map application | Add `SetEffectiveGeometry(map)` + `ApplyGeometryMap` + `RebaseTarget`; `AddTarget` applies it |
| `src/EyeCtrl.Core/Settings/AdaptationSettings.cs` | §13.1 absolute bound | Add `GeometryMaxMarginPx` (+ guard, Validate) |
| `src/EyeCtrl.Core/Adaptation/DwellRoutingRegistration.cs` | Entry + appliers | Add required `ApplyGeometry`; `ForDetector` wires both |
| `src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs` | Doc drift | `TargetGeometry` doc: routed |
| `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs` | Doc drift | Class + policy comments: geometry routed, §8.4 discharged at routing |
| `src/EyeCtrl.App/Services/ExternalDwellService.cs` | Lock-wrapping applier + rebase wiring | 5th registration argument; `UpdateMessage` routes through `RebaseTarget` |
| `src/EyeCtrl.App/Services/GeometryRouter.cs` | **New.** Changed → apply | Create |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | DI | `AddSingleton<GeometryRouter>()` |
| `src/EyeCtrl.App/App.xaml.cs` | Eager resolve | One line |
| `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs` | Slider row + reset | Row after `ad_frac_geom`; reset entry |
| `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs` | Map + rebase semantics | +8 facts |
| `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs` | Bound | +3 facts |
| `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorRegistryTests.cs` | Applier wiring | +2 facts |
| `tests/EyeCtrl.App.Tests/Services/GeometryRouterTests.cs` | **New.** Router | Create, 5 facts |
| `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs` | External applier + rebase | +2 facts |
| `tests/EyeCtrl.App.Tests/Services/DwellTimeRouterTests.cs` | Compile fix | `CaptureEntry` 5th arg |
| `tests/EyeCtrl.App.Tests/Services/DwellRoutingOwnerRegistrationTests.cs` | Guard | `ApplyGeometry` not-null ×3 |
| `tests/EyeCtrl.App.Tests/Services/DwellRoutingRegistrationWiringTests.cs` | Guard | `ApplyGeometry` not-null in `CheckSingleEntry` |
| `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayDwellRegistrationTests.cs` | Guard | `ApplyGeometry` not-null |
| `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs` | Row counts | 14→15 rows; Reset 13→14; setters |
| `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs` | Span | 5→6 sliders + `ad_geom_margin` pin |
| spec `…self-adaptation-design.md` | Record | §13.3 geometry resolution + status + scoping amendments (Task 6) |

**No ViewModels production change at all** — `DwellOverlayViewModelBase` and the nine subclasses already register via `ForDetector`, which now wires geometry automatically.

---

### Task 1: Core substrate — `BaseHitExtent`, `SetEffectiveGeometry`, `RebaseTarget`

Behaviour-neutral: no production caller sets a geometry map or rebases yet (External's rebase wiring lands in Task 3). Adds the map form of the existing `UpdateTargetGeometry` semantics (the `SetEffectiveDwellTime` shape) plus the rebase seam the round-1 blocker requires.

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/DwellTarget.cs`
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs` (append)

**Interfaces:**
- Produces:
  - `DwellTarget`: `internal double BaseHitExtent { get; }` — the as-registered reference extent (`_baseRadius` for circles; `Math.Min(_baseHalfWidth, _baseHalfHeight)` for rects); `internal void Rebase(PhysicalRect rect)` — resets the as-registered rect extents AND the live geometry to `rect` (the `_baseHalfWidth`/`_baseHalfHeight` fields lose `readonly`; `_baseRadius`/`_baseDwellTimeMicroseconds` keep it — circles are never rebased).
  - `DwellDetector`: `public void SetEffectiveGeometry(Func<double, double>? mapBaseExtentToEffectiveExtent)` — stores the map and applies it to every current target; `AddTarget` applies the stored map to each newly added target (born-effective); `public void RebaseTarget(string id, PhysicalRect rect)` — rebases and re-applies the stored map.

- [ ] **Step 1: Commit the plan file itself**

The kickoff provides this plan in the working tree (untracked). Commit it first — `git diff --stat main` does not list untracked files, so Task 6's 24-file surface check reads true only with this commit (round-1 finding 9):

```bash
git add docs/superpowers/plans/2026-09-03-geometry-knob-routing.md
git commit -m "docs(plan): geometry-knob routing implementation plan (Phase 1c slice 3)"
```

- [ ] **Step 2: Write the failing tests**

Append to `tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs` (inside the existing class; it already has the needed usings — `DwellTarget`, `DwellDetector`, `PhysicalPoint`, `PhysicalRect` are used by the existing facts):

```csharp
    [Fact]
    public void SetEffectiveGeometry_EnlargeMidDwell_PreservesProgress()
    {
        // §7.3/C4 enlarge: the grown hit area admits a gaze point only the margin covers while the
        // accumulation already in progress continues — 35 px is outside the base 30, inside the
        // effective 45, and progress must still run against the ORIGINAL EnteredTimestamp.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(120, 100), 0);        // 20 px — inside the base 30: enter
        d.Update(new PhysicalPoint(120, 100), 150_000);  // half done
        Check.That(d.GetProgress("t")).IsCloseTo(0.5, 0.01);

        d.SetEffectiveGeometry(ext => ext * 1.5);        // 30 → 45
        d.Update(new PhysicalPoint(135, 100), 290_000);  // 35 px — margin-only point; progress continues
        Check.That(activated).IsFalse();
        Check.That(d.GetProgress("t")).IsCloseTo(290.0 / 300.0, 0.01);

        d.Update(new PhysicalPoint(135, 100), 300_000);
        Check.That(activated).IsTrue();
    }

    [Fact]
    public void SetEffectiveGeometry_IsIdempotent_ComposesAgainstBaseExtent()
    {
        // The map must receive the BASE extent each application (never the live Radius), so
        // re-applying can never compound. The recorded arguments pin the contract directly: an
        // additive map alone cannot distinguish base from live input — under the live-extent
        // mutation the margin is still +10 and ApplyHitAreaMargin recomposes against _baseRadius,
        // so Radius settles at 50 either way (round-1 finding 3). The arguments can't lie.
        var d = new DwellDetector();
        var t = new DwellTarget("t", new PhysicalPoint(0, 0), radius: 40, dwellTimeMs: 1000);
        d.AddTarget(t);
        var seen = new double[3];
        int i = 0;
        Func<double, double> grow = ext => { seen[i++] = ext; return ext + 10; };

        d.SetEffectiveGeometry(grow);
        d.SetEffectiveGeometry(grow);
        d.SetEffectiveGeometry(grow);

        Check.That(seen).ContainsExactly(40.0, 40.0, 40.0); // the BASE each time, not the live 50
        Check.That(t.Radius).IsEqualTo(50.0); // 40 + 10, not 40 + 30
    }

    [Fact]
    public void SetEffectiveGeometry_TargetAddedAfterMap_IsBornEffective()
    {
        // Surfaces recreate targets per show; a target born while a fill is held must never serve
        // the as-registered geometry (spec §5 "surfaces read effective values at (re)creation").
        var d = new DwellDetector();
        d.SetEffectiveGeometry(ext => ext * 2); // 30 → 60
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 30, dwellTimeMs: 1000));

        d.Update(new PhysicalPoint(50, 0), 0);      // 50 > 30 base, < 60 effective — enters immediately
        d.Update(new PhysicalPoint(50, 0), 999_000);
        Check.That(d.GetProgress("t")).IsCloseTo(0.999, 0.001);
    }

    [Fact]
    public void SetEffectiveGeometry_NullMap_RestoresAsRegisteredGeometry()
    {
        var d = new DwellDetector();
        var t = new DwellTarget("t", new PhysicalPoint(0, 0), radius: 30, dwellTimeMs: 1000);
        d.AddTarget(t);
        d.SetEffectiveGeometry(ext => ext * 2);

        d.SetEffectiveGeometry(null);

        Check.That(t.Radius).IsEqualTo(30.0);
        d.Update(new PhysicalPoint(50, 0), 0); // outside the restored 30 — never enters
        d.Update(new PhysicalPoint(50, 0), 500_000);
        Check.That(d.GetProgress("t")).IsEqualTo(0.0);
    }

    [Fact]
    public void SetEffectiveGeometry_RectTarget_GrowsUniformly_FromTheMinHalfExtent()
    {
        // §13.3 unit mismatch, rect side: the reference extent is the SMALLER half-extent (gaze
        // error is isotropic; the narrow dimension binds), and the margin is uniform per side.
        var d = new DwellDetector();
        var r = new DwellTarget("r", new PhysicalRect(0, 0, 40, 80), dwellTimeMs: 1000);
        d.AddTarget(r);
        Check.That(r.BaseHitExtent).IsEqualTo(20.0); // min(20, 40)

        d.SetEffectiveGeometry(ext => ext * 1.5); // reference 20 → 30 ⇒ margin +10 on every side

        Check.That(r.Bounds).IsNotNull();
        var b = r.Bounds!.Value; // PhysicalRect is a readonly record struct — unwrap the Nullable once
        Check.That(b.X).IsCloseTo(-10.0, 0.001);
        Check.That(b.Y).IsCloseTo(-10.0, 0.001);
        Check.That(b.Width).IsCloseTo(60.0, 0.001);
        Check.That(b.Height).IsCloseTo(100.0, 0.001);
        Check.That(r.HitTest(new PhysicalPoint(45, 40))).IsTrue();   // outside base width, inside grown
        Check.That(r.HitTest(new PhysicalPoint(105, 40))).IsFalse(); // beyond grown
    }

    [Fact]
    public void SetEffectiveGeometry_ShrinkOut_MidDwell_EndsAsOrdinaryGazeLeft()
    {
        // §7.3/C4 shrink (recovery ramps the fill down): a gaze point that lived only in the margin
        // falls outside on restore and the dwell ends as a normal gaze-left — no activation, and
        // the accumulation is discarded (progress 0), never frozen.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 30, dwellTimeMs: 1000));
        d.SetEffectiveGeometry(ext => ext * 2); // 60
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(50, 0), 0);       // margin-only point: inside 60, outside base 30
        d.Update(new PhysicalPoint(50, 0), 500_000); // half done
        Check.That(d.GetProgress("t")).IsCloseTo(0.5, 0.01);

        d.SetEffectiveGeometry(null);               // restore: gaze now outside
        d.Update(new PhysicalPoint(50, 0), 600_000);

        Check.That(activated).IsFalse();
        Check.That(d.GetProgress("t")).IsEqualTo(0.0); // gaze-left reset, not a frozen 0.6
    }

    [Fact]
    public void RebaseTarget_NewRect_ResetsBaseExtents_LaterIdentityMapKeepsTheNewSize()
    {
        // Round-1 blocker, pinned at the Core seam: ExternalDwellService's UpdateMessage rebases a
        // live rect target; a LATER map application — including the null/disabled margin-0 restore,
        // which recomputes Bounds from the base half-extents — must recompute from the NEW base,
        // not revert the SDK-pushed size to the stale one.
        var d = new DwellDetector();
        var r = new DwellTarget("r", new PhysicalRect(0, 0, 40, 80), dwellTimeMs: 1000);
        d.AddTarget(r);

        d.RebaseTarget("r", new PhysicalRect(0, 0, 400, 100));

        d.SetEffectiveGeometry(null); // the disabled/cleared restore path — margin 0
        Check.That(r.Bounds).IsNotNull();
        var b = r.Bounds!.Value; // PhysicalRect is a readonly record struct — unwrap the Nullable once
        Check.That(b.Width).IsCloseTo(400.0, 0.001);
        Check.That(b.Height).IsCloseTo(100.0, 0.001);
    }

    [Fact]
    public void RebaseTarget_WhileAMarginIsHeld_ReappliesItOnTheNewBase()
    {
        var d = new DwellDetector();
        var r = new DwellTarget("r", new PhysicalRect(0, 0, 40, 80), dwellTimeMs: 1000);
        d.AddTarget(r);
        d.SetEffectiveGeometry(ext => ext + 10); // min half-extent 20 ⇒ margin 10 per side

        d.RebaseTarget("r", new PhysicalRect(0, 0, 400, 100)); // new min half-extent 50

        Check.That(r.Bounds).IsNotNull();
        var b = r.Bounds!.Value;
        Check.That(b.Width).IsCloseTo(420.0, 0.001);  // 400 + 2×10 — the margin rides the NEW base
        Check.That(b.Height).IsCloseTo(120.0, 0.001); // 100 + 2×10
    }
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetUpdateTests"`
Expected: compile error — `SetEffectiveGeometry`/`BaseHitExtent`/`RebaseTarget` do not exist.

- [ ] **Step 4: Implement**

In `src/EyeCtrl.Core/Processing/DwellTarget.cs`, after `ApplyEffectiveDwellMs` (currently ending ~line 102), add:

```csharp
    /// <summary>The as-registered reference hit extent the geometry map composes against: the radius
    /// for circular targets, the smaller half-extent for rects — gaze error is isotropic, so the
    /// routing margin is uniform on every side and the narrow dimension (the binding constraint)
    /// sets the scale. Unlike the live <see cref="Radius"/>/<see cref="Bounds"/>, this never changes,
    /// so repeated effective-geometry applications never compound.</summary>
    internal double BaseHitExtent => Bounds is not null
        ? Math.Min(_baseHalfWidth, _baseHalfHeight)
        : _baseRadius;
```

In the same file, drop `readonly` from `_baseHalfWidth`/`_baseHalfHeight` (the rebase seam mutates them; leave `_baseRadius` and `_baseDwellTimeMicroseconds` readonly — circles are never rebased), and add after `BaseHitExtent`:

```csharp
    /// <summary>
    /// Rebases the as-registered rect extents to <paramref name="rect"/> (an SDK-pushed
    /// size/position change on a live target — ExternalDwellService's UpdateMessage path) and
    /// resets the live geometry to the unmargined rect. The owner must re-apply its stored
    /// geometry map afterwards (<see cref="DwellDetector.RebaseTarget"/> does both), so a routed
    /// margin rides the NEW base instead of being lost — and a later map application can never
    /// revert the new size to stale base extents. Rect targets only; preserves dwell accumulation
    /// state.
    /// </summary>
    internal void Rebase(PhysicalRect rect)
    {
        _baseHalfWidth = rect.Width / 2;
        _baseHalfHeight = rect.Height / 2;
        Center = rect.Center;
        Bounds = rect;
    }
```

In `src/EyeCtrl.Core/Processing/DwellDetector.cs`: add a field next to `_effectiveDwellMap` (~line 26):

```csharp
    private Func<double, double>? _effectiveGeometryMap;
```

In `AddTarget` (currently ~lines 56–61), after the existing `ApplyMap(target);` add `ApplyGeometryMap(target);`. And after the existing `ApplyMap` private method (~lines 148–154), add:

```csharp
    /// <summary>
    /// Stores the effective-geometry map (adaptation routing) and applies it to every current
    /// target; targets added afterwards are born effective (spec §5). The map receives each target's
    /// base hit extent (<see cref="DwellTarget.BaseHitExtent"/> — radius, or the smaller rect
    /// half-extent) and returns an ABSOLUTE extent; the applied margin is
    /// <c>map(base) − base</c> (the §13.3 unit-mismatch resolution), so re-applying is idempotent.
    /// <c>null</c> restores every target's as-registered geometry (margin 0). State-preserving: never
    /// touches <c>EnteredTimestamp</c>/<c>IsGazeInside</c> — enlarging keeps an in-progress dwell
    /// alive; a shrink that leaves the gaze outside ends it on the next
    /// <see cref="Update(PhysicalPoint, long)"/> as a normal gaze-left (design §7.3/C4). Same
    /// threading contract as the other mutators (single thread — the UI thread, or the owning
    /// service's lock).
    /// </summary>
    public void SetEffectiveGeometry(Func<double, double>? mapBaseExtentToEffectiveExtent)
    {
        _effectiveGeometryMap = mapBaseExtentToEffectiveExtent;
        var targets = _targetsSnapshot;
        for (int i = 0; i < targets.Length; i++)
            ApplyGeometryMap(targets[i]);
    }

    private void ApplyGeometryMap(DwellTarget target)
    {
        // Null map = restore: margin 0 IS the null case's effective geometry. A bare early-return
        // here would leave targets stuck at the last margin (the SetEffectiveDwellTime lesson,
        // dwell plan review round 1 blocker 1).
        var map = _effectiveGeometryMap;
        double extent = target.BaseHitExtent;
        target.ApplyHitAreaMargin((map is null ? extent : map(extent)) - extent);
    }

    /// <summary>
    /// Rebases a rect target's as-registered extents to <paramref name="rect"/> and re-applies the
    /// stored effective-geometry map, so an owner that moves/resizes a live target in place
    /// (ExternalDwellService's UpdateMessage) neither loses the routed margin nor has a later map
    /// application revert the new size to the stale base extents. With no map stored this is
    /// behaviour-identical to a raw Bounds/Center write (margin 0 recomputes the exact rect).
    /// State-preserving; rect targets only (circular targets have no external geometry source).
    /// Same threading contract as the other mutators (single thread — the UI thread, or the owning
    /// service's lock).
    /// </summary>
    public void RebaseTarget(string id, PhysicalRect rect)
    {
        if (_targets.TryGetValue(id, out var target))
        {
            target.Rebase(rect);
            ApplyGeometryMap(target);
        }
    }
```

- [ ] **Step 4: Run the tests, then the full Core suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetUpdateTests"` → `Bestanden!`, 18 tests (10 existing + 8).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1156` (1148 + 8).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Processing/DwellTarget.cs src/EyeCtrl.Core/Processing/DwellDetector.cs tests/EyeCtrl.Core.Tests/Processing/DwellTargetUpdateTests.cs
git commit -m "feat(dwell): effective-geometry map + rebase seam, state-preserving

SetEffectiveGeometry(map) applies a base-extent->effective-extent map
to every target as a uniform margin (map(base) - base — the §13.3
unit-mismatch resolution; rect reference extent is the smaller
half-extent), targets added later are born effective, null restores
the as-registered geometry. Enlarge mid-dwell preserves progress;
a shrink that leaves the gaze outside ends the dwell as an ordinary
gaze-left (§7.3/C4). RebaseTarget gives in-place SDK rect updates a
seam that resets the base extents and re-applies the stored map, so a
routed margin can never revert a foreign resize (round-1 blocker)."
```

---

### Task 2: Core settings — the §13.1 absolute bound `GeometryMaxMarginPx`

Behaviour-neutral on its own (nothing reads the cap until the router exists).

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs` (append)

**Interfaces:**
- Produces: `AdaptationSettings.GeometryMaxMarginPx` — `double`, default `100.0`, setter guard and `Validate()` range `[0.0, 300.0]`, physical pixels. Read live by `GeometryRouter` (Task 4).

- [ ] **Step 1: Write the failing tests**

Append to `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs`:

```csharp
    [Fact]
    public void GeometryMaxMarginPx_DefaultsTo100_AndValidates()
    {
        // §13.1's feature-introduced geometry bound, landed by the geometry-routing slice: default
        // 100 physical px — generous enough that the default 0.50 fraction never binds on typical
        // baselines (~15–70 px margins), tight enough to bound overlap growth on large targets.
        var s = new AdaptationSettings();
        Check.That(s.GeometryMaxMarginPx).IsEqualTo(100.0);
        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void GeometryMaxMarginPx_OutOfRange_ThrowsFromSetterGuard()
    {
        var s = new AdaptationSettings();
        Check.ThatCode(() => s.GeometryMaxMarginPx = -1.0).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => s.GeometryMaxMarginPx = 300.5).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GeometryMaxMarginPx_AtTheBounds_IsAccepted()
    {
        var s = new AdaptationSettings { GeometryMaxMarginPx = 0.0 };
        s.GeometryMaxMarginPx = 300.0;
        Check.That(s.GeometryMaxMarginPx).IsEqualTo(300.0);
        Check.That(s.Validate()).IsEmpty();
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationSettingsTests"`
Expected: compile error — `GeometryMaxMarginPx` does not exist.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`, after the `GeometryBiggerMaxFraction` property (~line 61) add:

```csharp
    /// <summary>Absolute cap on the geometry growth margin, in physical screen px — §13.1's
    /// feature-introduced bound (geometry has no pre-existing validated range; the provider stays
    /// fraction-bounded). Applied at routing on top of the fraction band:
    /// <c>margin = clamp(effective − base, 0, this)</c>. Default 100.</summary>
    [ObservableProperty] private double _geometryMaxMarginPx = 100.0;
```

After the existing `OnGeometryBiggerMaxFractionChanging` partial (~line 67) add:

```csharp
    partial void OnGeometryMaxMarginPxChanging(double value) =>
        GuardRange(value, 0.0, 300.0, nameof(GeometryMaxMarginPx));
```

Next to `GuardFraction` (~lines 69–73) add:

```csharp
    private static void GuardRange(double value, double min, double max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(nameof(value), value, $"{name} must be between {min} and {max}.");
    }
```

And in `Validate()` (~lines 76–81), append one entry to the `ValidationHelper.CollectErrors` list:

```csharp
        ValidationHelper.ValidateRange(GeometryMaxMarginPx, 0.0, 300.0, nameof(GeometryMaxMarginPx)));
```

- [ ] **Step 4: Run the tests, then the full Core suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationSettingsTests"` → `Bestanden!`, 8 tests (5 existing + 3).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1159` (1156 + 3).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/AdaptationSettings.cs tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs
git commit -m "feat(adaptation): absolute geometry margin cap (the §13.1 bound)

GeometryMaxMarginPx, default 100 physical px, validated [0, 300] —
the feature-introduced bound §13.1 assigned to the geometry-routing
slice. The provider stays fraction-bounded; routing clamps the margin
(the consumer-side precedent)."
```

---

### Task 3: Registration shape — `ApplyGeometry` on `DwellRoutingRegistration`

One registration per owner now carries both appliers. `ForDetector` wires geometry automatically, so the only production construction that changes is ExternalDwellService's manual one (its lock-wrapping applier) — and the same service's `UpdateMessage` branch gains the rebase wiring the round-1 blocker requires. The one test fake that constructs the record positionally (`DwellTimeRouterTests.CaptureEntry`) is fixed here so every suite stays green.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/DwellRoutingRegistration.cs`
- Modify: `src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs` (doc drift only)
- Modify: `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs` (doc drift only)
- Modify: `src/EyeCtrl.App/Services/ExternalDwellService.cs` (registration ~lines 90–97; `UpdateMessage` branch ~lines 287–307)
- Modify: `tests/EyeCtrl.App.Tests/Services/DwellTimeRouterTests.cs` (compile fix)
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorRegistryTests.cs` (append)

**Interfaces:**
- Consumes: `DwellDetector.SetEffectiveGeometry` (Task 1).
- Produces: `DwellRoutingRegistration(DwellContext Context, int MinDwellMs, int MaxDwellMs, Action<Func<int, int>?> Apply, Action<Func<double, double>?> ApplyGeometry)` — the 5th parameter is REQUIRED (a silently-absent geometry applier is the mutation class the wiring guards exist to catch). `ForDetector(detector, context, minMs, maxMs)` signature unchanged; now wires both appliers.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorRegistryTests.cs` (inside the existing class):

```csharp
    [Fact]
    public void ForDetector_WiresBothAppliers_DwellAndGeometry()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 30, dwellTimeMs: 1000));
        var registration = DwellRoutingRegistration.ForDetector(detector, DwellContext.Toolbar, 200, 2000);

        registration.ApplyGeometry(ext => ext + 20); // geometry: 30 → 50
        registration.Apply(ms => ms / 2);            // dwell: 1000 → 500

        // One probe kills both absent-wiring defects: 45 px is outside the base radius (geometry
        // must have grown it) and 490 ms is past the halved dwell (the dwell map must have landed).
        detector.Update(new PhysicalPoint(45, 0), 0);
        detector.Update(new PhysicalPoint(45, 0), 490_000);
        Check.That(detector.GetProgress("t")).IsCloseTo(490.0 / 500.0, 0.001);
    }

    [Fact]
    public void GeometryApplier_ThroughTheRecord_GrowsRectBounds()
    {
        var detector = new DwellDetector();
        var rect = new DwellTarget("r", new PhysicalRect(0, 0, 40, 80), dwellTimeMs: 1000);
        detector.AddTarget(rect);
        var registration = DwellRoutingRegistration.ForDetector(detector, DwellContext.ExternalDwell,
            AdaptationDwellRanges.ExternalMinDwellMs, AdaptationDwellRanges.ExternalMaxDwellMs);

        registration.ApplyGeometry(ext => ext + 10); // min half-extent 20 → 30 ⇒ +10 per side

        Check.That(rect.Bounds).IsNotNull();
        var b = rect.Bounds!.Value; // PhysicalRect is a readonly record struct — unwrap once
        Check.That(b.Width).IsCloseTo(60.0, 0.001);
        Check.That(b.Height).IsCloseTo(100.0, 0.001);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorRegistryTests"`
Expected: compile error — `DwellRoutingRegistration.ApplyGeometry` does not exist.

- [ ] **Step 3: Extend the record**

Rewrite `src/EyeCtrl.Core/Adaptation/DwellRoutingRegistration.cs` as:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Processing;

/// <summary>
/// One dwell surface's routing entry: the telemetry context, the surface's validated dwell range, and
/// the two appliers that push baseline→effective maps onto the surface's live targets — dwell time
/// (ms→ms) and hit geometry (extent→extent; converted to a margin inside the detector). The appliers
/// are delegates (not a raw <see cref="DwellDetector"/>) so an owner with its own threading contract —
/// ExternalDwellService serializes detector access under <c>_stateLock</c> on the gaze thread — can
/// wrap the calls in its lock.
/// </summary>
/// <param name="Context">Telemetry context (informational today; per-context fills are Phase 2).</param>
/// <param name="MinDwellMs">Surface-family validated floor for the effective dwell value.</param>
/// <param name="MaxDwellMs">Surface-family validated ceiling for the effective dwell value.</param>
/// <param name="Apply">Receives the dwell map (null = restore baselines); must apply it on the owner's thread.</param>
/// <param name="ApplyGeometry">Receives the geometry map (null = restore as-registered geometry);
/// must apply it on the owner's thread. Required, not optional — a silently-absent applier is the
/// mutation class the wiring guard tests exist to catch.</param>
public sealed record DwellRoutingRegistration(
    DwellContext Context,
    int MinDwellMs,
    int MaxDwellMs,
    Action<Func<int, int>?> Apply,
    Action<Func<double, double>?> ApplyGeometry)
{
    /// <summary>The default appliers for the common case: a detector owned and mutated on one thread
    /// (the UI thread) with no additional locking.</summary>
    public static DwellRoutingRegistration ForDetector(
        DwellDetector detector, DwellContext context, int minDwellMs, int maxDwellMs) =>
        new(context, minDwellMs, maxDwellMs, detector.SetEffectiveDwellTime, detector.SetEffectiveGeometry);
}
```

- [ ] **Step 4: Fix the two positional constructions**

`src/EyeCtrl.App/Services/ExternalDwellService.cs` (~lines 90–97) — the registration becomes:

```csharp
        // Dwell + geometry routing (Phase 1c): the External range, NOT Standard — DefaultDwellMs
        // legally climbs to 3000, and an identity restore through [200, 2000] would truncate such a
        // baseline. Both appliers wrap their call in _stateLock: this detector Updates on the gaze
        // thread (OnCursorMoved) under that same lock, so an unlocked map write could interleave.
        _dwellRegistry?.Register(this, new DwellRoutingRegistration(
            DwellContext.ExternalDwell,
            AdaptationDwellRanges.ExternalMinDwellMs, AdaptationDwellRanges.ExternalMaxDwellMs,
            map => { lock (_stateLock) _detector.SetEffectiveDwellTime(map); },
            map => { lock (_stateLock) _detector.SetEffectiveGeometry(map); }));
```

In the same file's `UpdateMessage` branch (~lines 287–307), replace the raw in-place writes — `existing.Bounds = rect; existing.Center = rect.Center;` — with the rebase seam (round-1 blocker: the raw write leaves the readonly base half-extents at the ORIGINAL size, so the first routed map application — including the null/disabled margin-0 restore — recomputes `Bounds` from them and reverts the SDK's resize):

```csharp
            case UpdateMessage u:
                lock (_stateLock)
                {
                    if (conn.TargetIds.Contains(u.Id))
                    {
                        var rect = ToRect(u.Rect);
                        if (_targetsById.ContainsKey(u.Id))
                        {
                            // Rebase, never a raw Bounds/Center write: resets the as-registered
                            // extents to the SDK's new rect and re-applies the stored geometry map,
                            // so a routed margin can neither be lost nor revert the new size
                            // (round-1 blocker). In-place — the in-progress dwell survives.
                            _detector.RebaseTarget(u.Id, rect);
                        }
                        else
                        {
                            var target = new DwellTarget(u.Id, rect, _settings.DefaultDwellMs);
                            _targetsById[u.Id] = target;
                            _hwndById[u.Id] = conn.Hwnd;
                            _detector.AddTarget(target);
                        }
                    }
                }
                break;
```

(The `else` arm is unchanged; it is shown for context so the branch is copied whole.)

`tests/EyeCtrl.App.Tests/Services/DwellTimeRouterTests.cs` — the `CaptureEntry` fake (~lines 10–16) gains a geometry capture (recorded, unused by the dwell facts):

```csharp
    private sealed class CaptureEntry
    {
        public Func<int, int>? Applied;
        public DwellRoutingRegistration AsRegistration(DwellContext context = DwellContext.Toolbar,
            int min = AdaptationDwellRanges.StandardMinDwellMs, int max = AdaptationDwellRanges.StandardMaxDwellMs) =>
            new(context, min, max, map => Applied = map, _ => { });
    }
```

- [ ] **Step 5: Update the two doc-drift comments (Core)**

In `src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs`: (a) the `TargetGeometry` doc currently ends "Unrouted until the geometry-routing slice." Replace that sentence with: "Routed by the App-side `GeometryRouter` (geometry-routing slice, 2026-09-03): hit-area margins only — the map composes against `DwellTarget.BaseHitExtent` and the detector converts to the signed margin; no EyeCtrl-drawn element changes size (v1 scoping, §13.3 — the one coupled visible element is External's feedback overlay)." (b) the enum summary's "dwell and geometry are composable but unrouted until their routing slices land" → "all four knobs are routed" (as Task 3 already states). (c) **Round-1 finding 7:** the `DwellTime` member doc (~line 17) still ends "Unrouted until the dwell-routing slice." (stale since PR #132) — replace that sentence with "Routed by the App-side `DwellTimeRouter` (dwell-routing slice, 2026-09-02)." so the file the plan edits does not ship self-contradictory.

In `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs` — **round-1 finding 6 widened the edit to THREE spots**: (a) in the class doc, replace "the dwell and geometry knobs are composable but have no consumer yet (design §13.3)" with "the dwell knob is routed by the App-side `DwellTimeRouter` (dwell-routing slice, 2026-09-02), the geometry knob by `GeometryRouter` (geometry-routing slice, 2026-09-03), which clamps the growth margin to `GeometryMaxMarginPx`"; (b) the `TargetGeometry` policy comment — replace the WHOLE five-line block from `// Geometry: forgiving = bigger (+); no tightening. No pre-existing validated range (runtime-` through `// unrouted here, so the fraction bounds the (no-op) delta.` (the old anchor that started one line later would leave "(§8.4 deferred, per 1b-①)" contradicting the new text) with:

```csharp
            // Geometry: forgiving = bigger (+); no tightening. No pre-existing validated range (runtime-
            // derived) → unbounded above; the +fraction band is the operative proportional bound. The §13.1
            // absolute bound landed with the geometry-routing slice (2026-09-03) as
            // AdaptationSettings.GeometryMaxMarginPx, applied at routing (consumer-side clamp, the
            // smoothing/dwell precedent) — §8.4 for geometry is discharged at the routing layer.
            //
```

— keep the ⚠️ unit-mismatch paragraph that follows, it is accurate history; (c) the policy-table preamble above (~lines 29–38) still says "The two still-unrouted knobs (dwell, geometry) get SUPERSET validated ranges (… §8.4 for geometry stays deferred)" — rewrite that sentence to: "Dwell and geometry carry SUPERSET validated ranges (dwell = union of the 12 dwell-time guards [100, 3000] — per-surface ranges re-applied at dwell routing; geometry = unbounded above — §8.4 for geometry is discharged at routing, per the geometry-routing slice)". Keep the FixationRadius and smoothing sentences untouched.

- [ ] **Step 6: Run the Core suite, then App and ViewModels**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorRegistryTests"` → `Bestanden!`, 13 tests (11 existing — 4 theory rows + 7 facts — + 2).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1161` (1159 + 2).
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → `Bestanden!`, `gesamt: 735` (unchanged — compile fix + behaviour-neutral rebase: with no map stored, `RebaseTarget` recomputes the exact SDK rect).
Run: `dotnet test tests/EyeCtrl.ViewModels.Tests` → `Bestanden!`, `gesamt: 300` (unchanged — `ForDetector` signature is the same).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/DwellRoutingRegistration.cs src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs src/EyeCtrl.App/Services/ExternalDwellService.cs tests/EyeCtrl.App.Tests/Services/DwellTimeRouterTests.cs tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorRegistryTests.cs
git commit -m "feat(adaptation): registration carries a geometry applier

DwellRoutingRegistration gains a required ApplyGeometry (extent->extent
map); ForDetector wires SetEffectiveGeometry automatically, so every
detector-based registration site gains geometry routing with no call-site
change. ExternalDwellService's manual construction passes a _stateLock-
wrapping geometry applier, mirroring its dwell applier — and its
UpdateMessage branch now rebases through DwellDetector.RebaseTarget
instead of raw Bounds/Center writes, so a routed map can never revert
an SDK-pushed resize (round-1 blocker)."
```

---

### Task 4: App — `GeometryRouter`, DI, guard strengthening, the External fact

Behaviour-introducing for enabled users (identity map restores baselines while disabled). The router is the `DwellTimeRouter` shape, single-knob.

**Files:**
- Create: `src/EyeCtrl.App/Services/GeometryRouter.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (~line 156)
- Modify: `src/EyeCtrl.App/App.xaml.cs` (~line 182)
- Modify: `tests/EyeCtrl.App.Tests/Services/DwellRoutingOwnerRegistrationTests.cs`, `tests/EyeCtrl.App.Tests/Services/DwellRoutingRegistrationWiringTests.cs`, `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayDwellRegistrationTests.cs` (guard strengthening)
- Test: `tests/EyeCtrl.App.Tests/Services/GeometryRouterTests.cs` (new); one fact appended to `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3; `EffectiveSettingsProvider.Changed`; `Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, double)`; `AdaptationSettings.GeometryMaxMarginPx`.
- Produces: `GeometryRouter(EffectiveSettingsProvider provider, IDwellDetectorRegistry registry, EyeCtrlSettings settings) : IDisposable` — applies on every `provider.Changed` and `registry.RegistrationChanged`.

- [ ] **Step 1: Write the failing router tests**

Create `tests/EyeCtrl.App.Tests/Services/GeometryRouterTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class GeometryRouterTests
{
    // A capture fake standing in for a surface: records the applied geometry map.
    private sealed class CaptureEntry
    {
        public Func<double, double>? Applied;
        public DwellRoutingRegistration AsRegistration(DwellContext context = DwellContext.Toolbar) =>
            new(context, AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs,
                _ => { }, map => Applied = map);
    }

    private static (GeometryRouter Router, EffectiveSettingsProvider Provider, DwellDetectorRegistry Registry) Make()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        return (new GeometryRouter(provider, registry, settings), provider, registry);
    }

    [Fact]
    public void ProviderChanged_AppliesMarginMap_CappedAtTheSetting()
    {
        var (router, provider, registry) = Make();
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());

        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        // Full forgiving fill at the default 0.50 fraction: 60 → 90 (margin 30, under the cap)…
        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(60)).IsEqualTo(90.0);
        // …and 700 → 800, NOT 1050 — the §13.1 absolute bound caps the margin at 100 px.
        Check.That(entry.Applied(700)).IsEqualTo(800.0);
    }

    [Fact]
    public void ClearedFills_IdentityMap_RestoresExactGeometry()
    {
        var (router, provider, registry) = Make();
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());
        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, 1.0, 0.0);

        provider.ClearMomentaryFills();

        Check.That(entry.Applied!(60)).IsEqualTo(60.0); // margin 0 ⇒ as-registered geometry
    }

    [Fact]
    public void LateRegistration_AppliesImmediately()
    {
        var (router, provider, registry) = Make();
        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, 1.0, 0.0);
        var entry = new CaptureEntry();

        registry.Register(new object(), entry.AsRegistration()); // AFTER the fill exists

        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(60)).IsEqualTo(90.0);
    }

    [Fact]
    public void Constructor_AppliesToEntriesRegisteredBeforeConstruction()
    {
        // Resolve-order insurance: every other fact constructs the router BEFORE registering, so the
        // RegistrationChanged subscription masks the ctor ApplyAll() — deleting that call would fail
        // nothing. This fact registers first; the map must arrive with no event at all.
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration()); // BEFORE any router exists

        using var router = new GeometryRouter(provider, registry, settings);

        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(60)).IsEqualTo(90.0);
    }

    [Fact]
    public void DisabledProvider_IdentityMap()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = false } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        using var router = new GeometryRouter(provider, registry, settings);
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());

        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, 1.0, 0.0);

        Check.That(entry.Applied!(60)).IsEqualTo(60.0); // gated in Effective → baseline
    }
}
```

(Note the first three facts' `router` tuple element is intentionally unused — constructing it IS the subscription; `Make()` assigns it, so no warning fires. The DwellTimeRouterTests precedent.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~GeometryRouterTests&Category!=UI"`
Expected: compile error — `GeometryRouter` does not exist. (One `--filter` with `&`; two separate `--filter` options silently drop the first.)

- [ ] **Step 3: Implement the router**

Create `src/EyeCtrl.App/Services/GeometryRouter.cs`:

```csharp
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

/// <summary>
/// Routes the momentary TargetGeometry fill to every registered dwell surface (design §13.3 geometry
/// routing): on every <see cref="EffectiveSettingsProvider.Changed"/> (payloadless — the geometry map
/// is re-applied even when another knob caused the event; the apply is idempotent) and on every
/// registration change (late registrants get the current values immediately). Per entry, the map
/// composes the fill against each target's base hit extent (radius, or the smaller rect half-extent)
/// and returns an ABSOLUTE extent — the detector converts it to the signed margin
/// <see cref="DwellDetector.UpdateTargetGeometry"/> consumes (§13.3's unit-mismatch resolution). The
/// §13.1 feature-introduced absolute bound is applied HERE — margin clamped to [0,
/// <see cref="AdaptationSettings.GeometryMaxMarginPx"/>] — the consumer-side-clamp precedent
/// (smoothing per-filter in GazeEngine, dwell per-surface in DwellTimeRouter). With adaptation
/// disabled or no fill held, <see cref="EffectiveSettingsProvider.Effective"/> returns the baseline,
/// so the margin is 0 and the exact as-registered geometry is restored — no teardown path exists or
/// is needed. Hit areas only: no EyeCtrl-drawn element changes size (v1 scoping, §13.3); the one
/// visible element coupled to hit geometry is External's dwell-feedback overlay, which sizes itself
/// from the routed rect (§8.9's visible⇒hit direction, by construction). In-place and idempotent,
/// so there is deliberately no §9.1 budget (the tolerance
/// precedent). NB: a cap EDIT does not raise <c>Changed</c> (fills only), so a lowered cap takes
/// effect at the next fill write — bounded staleness, self-healing (the §13.3 smoothing leftover
/// class). UI-thread: constructed eagerly at startup (it subscribes and applies in its ctor — see
/// AppBootstrapper / App.xaml.cs).
/// </summary>
public sealed class GeometryRouter : IDisposable
{
    private readonly EffectiveSettingsProvider _provider;
    private readonly IDwellDetectorRegistry _registry;
    private readonly AdaptationSettings _adaptation;

    public GeometryRouter(EffectiveSettingsProvider provider, IDwellDetectorRegistry registry,
        EyeCtrlSettings settings)
    {
        _provider = provider;
        _registry = registry;
        _adaptation = settings.Adaptation;
        _provider.Changed += ApplyAll;
        _registry.RegistrationChanged += ApplyAll;
        ApplyAll(); // entries registered before the router existed must not wait for the next event.
                    // Reentrancy-safe: Register/Unregister raise RegistrationChanged OUTSIDE the
                    // registry lock and Registrations snapshots on read (the DwellTimeRouter analysis).
    }

    private void ApplyAll()
    {
        foreach (var registration in _registry.Registrations)
            registration.ApplyGeometry(MakeMap());
    }

    private Func<double, double> MakeMap() => baseExtent =>
    {
        double effective = _provider.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, baseExtent);
        double margin = Math.Clamp(effective - baseExtent, 0.0, _adaptation.GeometryMaxMarginPx);
        return baseExtent + margin;
    };

    public void Dispose()
    {
        _provider.Changed -= ApplyAll;
        _registry.RegistrationChanged -= ApplyAll;
    }
}
```

- [ ] **Step 4: Run the router tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~GeometryRouterTests&Category!=UI"`
Expected: `Bestanden!`, 5 tests.

- [ ] **Step 5: Wire DI + eager resolve**

`src/EyeCtrl.App/Services/AppBootstrapper.cs` — after `services.AddSingleton<DwellTimeRouter>();` (~line 156):

```csharp
        services.AddSingleton<GeometryRouter>();
```

(Type-based: DI injects `EffectiveSettingsProvider`, `IDwellDetectorRegistry`, and the registered `EyeCtrlSettings` automatically. Update the neighbouring comment's "slice 2" wording to mention both routers if you touch it — otherwise leave it.)

`src/EyeCtrl.App/App.xaml.cs` — next to the existing eager resolves (~line 182, `_ = _serviceProvider.GetRequiredService<DwellTimeRouter>();`), add:

```csharp
            _ = _serviceProvider.GetRequiredService<GeometryRouter>();
```

- [ ] **Step 6: Strengthen the wiring guards (assertion-only, no new facts)**

- `tests/EyeCtrl.App.Tests/Services/DwellRoutingRegistrationWiringTests.cs` — in `CheckSingleEntry` (~lines 61–69), after the `MaxDwellMs` assert: `Check.That(registrations[0].ApplyGeometry).IsNotNull();`
- `tests/EyeCtrl.App.Tests/Services/DwellRoutingOwnerRegistrationTests.cs` — same one-line assert after the range asserts in `NoteManager_DirectConstruction_…`, `EmojiGridManager_DirectConstruction_…`, and the ManualSelection fact (~lines 52–53, 78–79, 120–121).
- `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayDwellRegistrationTests.cs` — same assert in `Constructor_RegistersStandardRangeEntry_WithItsContext` after the `MaxDwellMs` check.

- [ ] **Step 7: Append the External geometry fact**

In `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs`, after `RegisterApplyHalvingMapAndDwell` (~line 422), add its geometry sibling + helper (mirroring the dwell fact's structure exactly — same `RunOnStaThread`, same construction, same pipe-settle discipline; **run the whole App project, not this class alone** — the XAML-brush dependency documented in the class remarks):

```csharp
    [Fact]
    public void GeometryRoutingEntry_MarginMap_GrowsTheLiveRectTarget()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            var dwellRegistry = new DwellDetectorRegistry();
            using var svc = new ExternalDwellService(
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
                engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
                new WeakReferenceMessenger(), _ => 7,
                dwellRegistry: dwellRegistry, pipeName: _pipeName);
            svc.Start();

            Check.That(dwellRegistry.Registrations).HasSize(1);
            Check.That(dwellRegistry.Registrations[0].ApplyGeometry).IsNotNull();

            var activated = RegisterApplyMarginMapAndDwell(
                dwellRegistry.Registrations[0], provider, _pipeName).GetAwaiter().GetResult();

            // (205, 100) is OUTSIDE the as-registered rect [0, 200] × [0, 200] and INSIDE the
            // +20-outset rect [−20, 220] — the activation can only have fired because the routed
            // margin reached the live target's geometry.
            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
        });
    }

    // Registers a rect target, lets the async pipe registration settle, applies a +20 margin map
    // through the routing entry's geometry applier, then dwells at a point only the GROWN rect
    // covers — proving the applier drives the service's live target under its lock.
    private static async Task<string?> RegisterApplyMarginMapAndDwell(
        DwellRoutingRegistration entry, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200); // let the server register the target BEFORE applying the map

        entry.ApplyGeometry(ext => ext + 20); // min half-extent 100 → 120 ⇒ +20 per side

        provider.Raise(new GazePoint(205, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(205, 100, 800_000, IsValid: true));

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    [Fact]
    public void GeometryRouting_RebaseAfterSdkSizeChange_IdentityMapDoesNotRevertTheNewSize()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            var dwellRegistry = new DwellDetectorRegistry();
            using var svc = new ExternalDwellService(
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
                engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
                new WeakReferenceMessenger(), _ => 7,
                dwellRegistry: dwellRegistry, pipeName: _pipeName);
            svc.Start();

            var activated = RegisterUpdateApplyIdentityAndDwell(
                dwellRegistry.Registrations[0], provider, _pipeName).GetAwaiter().GetResult();

            // (30, 50) is inside the SDK's NEW 400×100 rect and outside the stale-200×200-based
            // reverted rect (x ∈ [100, 300] around the new centre) — the activation proves the
            // rebase survived the routed identity map (round-1 blocker, service level).
            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
        });
    }

    // Registers 200×200, lets it settle, pushes an UpdateMessage to 400×100, lets it settle,
    // applies the NULL geometry map (the disabled/cleared restore path — margin 0), then dwells at
    // a point only the NEW rect covers. Under the raw-write bug the null map recomputes Bounds from
    // the stale base half-extents and the point falls outside — no dwell, no activation.
    private static async Task<string?> RegisterUpdateApplyIdentityAndDwell(
        DwellRoutingRegistration entry, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200);
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new UpdateMessage { Id = "btn", Rect = new SdkRect(0, 0, 400, 100) }, WireJson.Options));
        await Task.Delay(200); // let the server rebase the target BEFORE applying the map

        entry.ApplyGeometry(null);

        provider.Raise(new GazePoint(30, 50, 0, IsValid: true));
        provider.Raise(new GazePoint(30, 50, 800_000, IsValid: true));

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }
```

- [ ] **Step 8: Run the App headless suite, then Core and ViewModels**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → `Bestanden!`, `gesamt: 742` (735 + 5 router + 2 external).
Run: `dotnet test tests/EyeCtrl.Core.Tests` → `gesamt: 1161` (unchanged). Run: `dotnet test tests/EyeCtrl.ViewModels.Tests` → `gesamt: 300` (unchanged).

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.App/Services/GeometryRouter.cs src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.App.Tests/Services/GeometryRouterTests.cs tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs tests/EyeCtrl.App.Tests/Services/DwellRoutingOwnerRegistrationTests.cs tests/EyeCtrl.App.Tests/Services/DwellRoutingRegistrationWiringTests.cs tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayDwellRegistrationTests.cs
git commit -m "feat(adaptation): route TargetGeometry to every live dwell surface

GeometryRouter applies the momentary TargetGeometry fill (global Other
context) as a base-extent->effective-extent map on provider Changed and
on late registration; the margin is clamped to the new §13.1 bound
GeometryMaxMarginPx (consumer-side, the smoothing/dwell precedent).
Disabled or cleared => margin 0 => exact as-registered geometry;
in-place apply, no budget (the tolerance precedent). Hit areas only —
the visible size never changes (v1 scoping). External's applier wraps
SetEffectiveGeometry in _stateLock, mirroring its dwell applier; the
wiring guards now assert every entry carries a geometry applier."
```

---

### Task 5: App — the `GeometryMaxMarginPx` slider row + reset entry

User-facing exposure of the bound (owner decision): a sibling slider of the five band fractions. The completeness gate (`SettingsRowDescriptionCompletenessTests`) requires the row to carry a Description; the span guard pins the slider inside the Core guard.

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs`
- Modify: `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs`
- Modify: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs`

**Interfaces:** none new.

- [ ] **Step 1: Update the tests first**

In `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs`, `Adaptation_SliderSpans_StayInsideTheValidatedRanges` (~lines 112–121) becomes:

```csharp
    [Fact]
    public void Adaptation_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new AdaptationPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(6);
        SpanInside(sliders, "ad_frac_dwell_short", 0.0, 1.0);
        SpanInside(sliders, "ad_frac_dwell_long", 0.0, 1.0);
        SpanInside(sliders, "ad_frac_tol_loose", 0.0, 1.0);
        SpanInside(sliders, "ad_frac_smooth", 0.0, 1.0);
        SpanInside(sliders, "ad_frac_geom", 0.0, 1.0);
        SpanInside(sliders, "ad_geom_margin", 0, 300);
    }
```

In `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs`: rename `Build_HasFourteenRows_NoCaregiverRow` → `Build_HasFifteenRows_NoCaregiverRow` and change `HasSize(14)` → `HasSize(15)`; in `Setters_WriteLiveSettings` append:

```csharp
        sliders["ad_geom_margin"].Set(120);
        Check.That(settings.Adaptation.GeometryMaxMarginPx).IsEqualTo(120);
```

and rename `Reset_CopiesAllThirteenFromDefaults` → `Reset_CopiesAllFourteenFromDefaults`, adding `settings.Adaptation.GeometryMaxMarginPx = 250;` to the mutations (after the `GeometryBiggerMaxFraction` line) and `Check.That(settings.Adaptation.GeometryMaxMarginPx).IsEqualTo(d.GeometryMaxMarginPx);` to the assertions.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationPageSourceTests|FullyQualifiedName~SettingsSliderSpanGuardTests"`
Expected: `Fehler!` — 14 ≠ 15 rows, 5 ≠ 6 sliders.

- [ ] **Step 3: Add the row and reset entry**

In `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs`, after the `ad_frac_geom` slider (~lines 54–56):

```csharp
                new SliderSpec("ad_geom_margin", "Target Size — Growth Cap", 0.0, 300.0, 5.0, 0,
                    () => s.GeometryMaxMarginPx, v => { s.GeometryMaxMarginPx = v; _ctx.ApplyAndSave(); })
                    { Description = "The most a target's hit area may grow when adapted, in screen pixels." },
```

and in the reset button's modal (~line 73, after `s.GeometryBiggerMaxFraction = d.GeometryBiggerMaxFraction;`):

```csharp
                        s.GeometryMaxMarginPx = d.GeometryMaxMarginPx;
```

- [ ] **Step 4: Run the settings-page tests, then the full App suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationPageSourceTests|FullyQualifiedName~SettingsSliderSpanGuardTests"` → `Bestanden!`, 13 tests (4 page-source facts + 9 slider-span facts).
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → `Bestanden!`, `gesamt: 741` (unchanged — no new facts).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs
git commit -m "feat(settings): expose the geometry growth cap on the Adaptation page

A sixth slider (0-300 px, step 5) bound to GeometryMaxMarginPx beside
the five band fractions, included in the page reset; row/span tests
updated (15 rows, 6 sliders, span pinned to the Core guard)."
```

---

### Task 6: Spec amendment + full verification

**Files:**
- Modify: `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md`

- [ ] **Step 1: Amend §13.3's "Dwell and geometry." bullet**

Append to the bullet (after the dwell-resolution block that ends "…take the registry argument explicitly in their DI lambdas."):

```markdown
  **(Geometry resolved 2026-09-03 — geometry-routing slice, plan `2026-09-03-geometry-knob-routing.md`;
  Phase 1c complete.)** Hit-area margins only (owner decision): no EyeCtrl-drawn element changes size — §8.9
  still holds, not vacuously: the ONE visible element coupled to hit geometry is External's
  dwell-feedback overlay, which sizes itself from the routed rect (the sanctioned visible⇒hit
  direction, by construction). External targets (foreign UI we cannot resize) prove hit-only is the
  only universal shape, and the rendering side of visible coupling is only verifiable via
  UI-category tests this repo never runs; the §7.1/§13.1 "coupled visible size" wording is hereby
  scoped to hit geometry for v1, visible coupling deferred (the feedback overlay excepted as above). The unit mismatch resolves in the detector: the map composes the fill against
  each target's base hit extent (`DwellTarget.BaseHitExtent` — radius, or the smaller rect
  half-extent) and applies `margin = map(base) − base` through the existing `ApplyHitAreaMargin`
  (uniform per side; gaze error is isotropic). `DwellDetector.SetEffectiveGeometry(map)` mirrors
  `SetEffectiveDwellTime` (born-effective on `AddTarget`; null restores; state-preserving — §7.3/C4:
  enlarge keeps progress, shrink-out is an ordinary gaze-left). The shared substrate is the dwell
  registry: `DwellRoutingRegistration` gained a required geometry applier, auto-wired by
  `ForDetector` (zero registration-site churn; External passes a `_stateLock`-wrapping lambda).
  App-side `GeometryRouter` (the `DwellTimeRouter` shape) applies on provider `Changed` +
  `RegistrationChanged`. External's `UpdateMessage` rebases through
  `DwellDetector.RebaseTarget(id, rect)` (reset the as-registered extents + re-apply the stored
  map) instead of raw Bounds/Center writes, so a routed map — including the identity restore — can
  never revert an SDK-pushed resize to stale base extents. The §13.1 absolute bound landed as `AdaptationSettings.GeometryMaxMarginPx`
  (default 100 physical px, validated [0, 300], exposed as an Adaptation-page slider): margin =
  clamp(effective − base, 0, cap), applied at routing — the consumer-side-clamp precedent
  (smoothing per-filter, dwell per-surface); the provider stays fraction-bounded, discharging §8.4
  for geometry at the routing layer. Identity restores are never truncated (margin 0 is always
  in [0, cap]). The Keyboard's main keys are geometry-exempt alongside the existing dwell
  exemptions: nearest-key tracking has no hit boundary (dead-zone-free by design), so the §5
  "key hit slop" phrase has no referent — the prediction bar IS routed (circular targets).
```

- [ ] **Step 2: Amend the remaining spec spots** (round 1 extended the list from 5 to 12 — the
sweep must leave NO stale "geometry open/unrouted" or coupled-visible-size claim behind; grep the
amended spec for `unrouted`, `still open`, `vacuously`, and `coupled visible` afterwards and read
every match)

**NB — the quoted sentences below span LINE BREAKS in the spec file** (e.g. `coupled␊atomically`,
`geometry␊still open`, `the␊geometry-routing slice`, `this bound;␊it is now owned`): grep the
SHORTEST distinctive fragment of each quote, never the whole quote, and edit the sentence in place.
The dry run hit exactly this trap twice.

1. **§13 status line** — replace "Geometry routing remains — the Phase-2 gate stays closed." with: "**Status: Phase 1c complete — smoothing routed 2026-09-01 (PR #131), dwell 2026-09-02 (PR #132), geometry 2026-09-03. The Phase-2 gate is open.**" (keep the existing smoothing/dwell sentences, drop only the "remains" clause).
2. **§5 knobs paragraph** — in "target geometry (button ring radius + hit radius, key hit slop, and the coupled visible size)", strike "key hit slop" (no hit boundary in the boundary-free nearest-key tracker) and change the tail to "and the coupled visible size — v1 routes hit geometry only; see §13.3's geometry resolution"). **Then replace the bolded clause in the same paragraph** — "**As implemented today only tolerance is actually routed** — the other three are composable but have no consumer; see §13.3 (Phase 1c)." — with "**All four knobs are routed as of 2026-09-03 (§13.3 Phase 1c).**" (round-1 finding 5: the clause was already stale since PR #131 and the original edit stopped one sentence short).
3. **§7.1 Aiming row** — change "**enlarge target geometry** (ring + hit radius / key slop) *and* coupled visible size, atomically" to "**enlarge target geometry** (hit radius / rect margins; v1 hit-only — visible coupling deferred, §13.3)".
4. **§13.1 geometry width sentence** — in "target geometry **bigger** +50 % of the (runtime-derived) ring/hit radius, coupled atomically with the visible size (§8.9), bounded by the feature-introduced geometry bound above", strike "coupled atomically with the visible size (§8.9)," (round-1 finding 10) and after "bounded by the feature-introduced geometry bound above" append: "(landed 2026-09-03 as `GeometryMaxMarginPx`, default 100 px, [0, 300] — §13.3)".
5. **§14 last bullet** — replace "owned by the geometry-routing slice (§13.3), together with the absolute-extent vs. signed-margin unit conversion." with "**Resolved (§13.3, 2026-09-03):** `AdaptationSettings.GeometryMaxMarginPx` (default 100, [0, 300]), margin-clamped at routing; unit conversion in `DwellDetector.SetEffectiveGeometry`."
6. **§13.3 dwell-resolution header** (round-1 finding 4) — the block Step 1 appends to opens "**(Dwell resolved 2026-09-02 — dwell-routing slice, plan `2026-09-02-dwell-knob-routing.md`; geometry still open.)**"; amend "; geometry still open.)**" to "; geometry resolved 2026-09-03 — see the block below.)**" so the bullet does not say both.
7. **§13.3 "Geometry additionally" sub-bullet** (round-1 finding 4) — the sentence "- **Geometry additionally** needs the §13.1 absolute bound (which §13.1 assigns to Phase 1b and 1b deferred — see §14) and resolution of the absolute-extent vs. signed-margin unit mismatch noted in `EffectiveSettingsProvider`'s `KnobPolicy` table." gets a resolution marker appended: " **(Resolved 2026-09-03 — see the geometry-resolution block above.)**"
8. **§13.1 amended-marker** (round-1 finding 6) — "**(Amended: Phase 1b deferred this bound; it is now owned by the geometry-routing slice, which supplies its first consumer — §13.3 and §14.)**" gets " **(Landed 2026-09-03 — `GeometryMaxMarginPx`.)**" appended.
9. **§13.2 response-map aiming row** (round-1 finding 10) — "- **aiming** ← Aiming → enlarge target geometry (+ coupled visible size, §8.9)." → "- **aiming** ← Aiming → enlarge target geometry (v1 hit-only — visible coupling deferred, §13.3)."
10. **§12 acceptance line** (round-1 finding 10) — "- Size adaptation atomicity: visible size + hit geometry change together (action-menu ring + hit radius)." → "- Size adaptation atomicity (v1 scope): no visible size changes at all; hit geometry changes alone (§13.3 geometry resolution). The visible+hit atomic coupling returns if visible coupling is ever un-deferred."
11. **§5 dwell-time bullet's stale "still missing"** (round-1 finding 12, stale since PR #132) — "**(Done in 1a-⑤: `Radius` now has an `internal set`, `_baseRadius` is retained for idempotent margins, and both update methods exist. What is still missing is the application-level detector registry + dwell-time baseline + context mapping — see §13.3.)**" → replace the "What is still missing…" sentence with "(The registry + dwell-time baseline + context mapping landed with the dwell-routing slice — see §13.3.)"
12. **Aiming-denominator coupling recorded** (round-1 finding 8) — append one sentence at the end of the §13.3 geometry-resolution block (Step 1's markdown): "NB: `DwellOutcome` reports the LIVE hit extent at outcome time, so the Aiming signal's |NearMiss|÷radius denominator grows with the knob's output — self-damping (enlargement extinguishes its own evidence; deadband + slow ramp preclude chatter); kept deliberately, a Phase-2 calibration decision if real data disagrees."

- [ ] **Step 3: Run the full sweep, sequentially**

1. `dotnet test tests/EyeCtrl.Core.Tests` → `Bestanden!`, `gesamt: 1161` — baseline 1148 **+ 13**:

| Task | Delta | From |
|---|---|---|
| 1 | **+8** | enlarge-preserves-progress, idempotent (argument-recording), born-effective, null-restore, rect min-half-extent, shrink-out gaze-left, rebase-vs-identity-map, rebase-while-margin-held |
| 2 | **+3** | cap default+validate, out-of-range guard, bounds accepted |
| 3 | **+2** | ForDetector wires both appliers, rect growth through the record |

2. `dotnet test tests/EyeCtrl.ViewModels.Tests` → `Bestanden!`, `gesamt: 300` (unchanged — assertion-only guard strengthening).
3. `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → `Bestanden!`, `gesamt: 742` — baseline 735 **+ 7** (5 router + 2 external; the settings-row task updates existing facts only).
4. `dotnet test tests/EyeCtrl.Benchmarks.Tests` → `Bestanden!`, `gesamt: 27` (unchanged).

A different total means a row was duplicated or dropped — find it, do not adjust the number.

- [ ] **Step 4: Confirm the change surface**

Run: `git diff --stat main`
Expected: **exactly 24 files** (the plan file itself is among them — Task 1 Step 1 commits it; `git diff` does not list untracked files) — Core: `DwellTarget.cs`, `DwellDetector.cs`, `AdaptationSettings.cs`, `DwellRoutingRegistration.cs`, `AdaptationKnob.cs`, `EffectiveSettingsProvider.cs` (6, no new files); App: `GeometryRouter.cs` (new), `AppBootstrapper.cs`, `App.xaml.cs`, `ExternalDwellService.cs`, `AdaptationPageSource.cs` (5); ViewModels: **none**; tests: `GeometryRouterTests.cs` (new), `DwellTargetUpdateTests`, `AdaptationSettingsTests`, `DwellDetectorRegistryTests`, `ExternalDwellServiceTests`, `DwellTimeRouterTests`, `DwellRoutingOwnerRegistrationTests`, `DwellRoutingRegistrationWiringTests`, `OverlayDwellRegistrationTests`, `AdaptationPageSourceTests`, `SettingsSliderSpanGuardTests` (11); docs: the spec + this plan (2). **No** `AdaptationController.cs`, `StruggleMonitor.cs`, `GazeEngine.cs`, `DwellOverlayViewModelBase.cs`, any overlay subclass, `KeyboardViewModel.cs`, `ScrollModeController.cs`, `ScrollOverlayViewModel.cs`, `SettingsWindowViewModel.cs`, or Platform changes.

- [ ] **Step 5: State what was and was not verified (for the PR description)**

Verified: state-preserving geometry maps (enlarge preserves progress, shrink-out is an ordinary gaze-left with the accumulation discarded), idempotent per-target composition against the base extent (the map's ARGUMENTS are pinned to the base extent, not just the settled value), born-effective targets, null/identity restore (cleared fills AND disabled), the rect min-half-extent reference with uniform margins, the margin cap clamping an over-band composition (700 → 800, not 1050), late-registration + construct-time apply, `ForDetector` auto-wiring both appliers (the kill test), External's lock-wrapping applier driving its live rect target, the rebase seam at BOTH levels (Core: rebase + identity map keeps the new size, rebase + held margin re-applies on the new base; service: SDK resize via `UpdateMessage` survives a routed null map), the settings row/reset/span/description gates, and the wiring guards DOCUMENTING `ApplyGeometry` presence (compiler-enforced non-null; the `ForDetector` wiring fact is what actually kills an unwired applier — round-1 finding 11). Not verified: true concurrent router-apply vs. gaze-thread `Update` for External (the lock is by construction and reviewed, not race-tested); real-user feel of grown hit areas (the §13.2 thresholds remain uncalibrated); per-context geometry fills (Phase 2); the Aiming-signal denominator coupling (documented as self-damping, round-1 finding 8 — a Phase-2 calibration decision); External's feedback overlay growth (visible-only, `Category=UI` territory this repo never runs).

- [ ] **Step 6: Commit the amendment; push and open the PR — ONLY after the repo owner's explicit OK**

```bash
git add docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md
git commit -m "docs(spec): record the geometry-routing slice; Phase 1c complete (§13.3)"
```

**Do not push until the repo owner has explicitly approved.** If no answer, stop and report — the branch is complete locally.

---

## Notes for the implementer

- **Do not run the app or simulate input.** Headless verification only; see Global Constraints.
- **If a step's expected output does not match, stop rather than adapting the plan.** The anchors reference `main` at `c8a4361b`.
- **Do not enlarge anything visible.** The scope is hit-area margins. If a task seems to require touching a drawn size, a layout constant, or a WPF element, that is scope creep — stop and report. The ONE exception is by omission, not action: External's dwell-feedback overlay reads the routed `target.Bounds` and grows with it — leave that alone (§8.9's sanctioned direction, round-1 finding 2); do NOT rewire `ShowAt` to base extents.
- **Do not add geometry routing for Settings' four detectors** (the remedy surface must never adapt, §2), the Keyboard's main keys (boundary-free nearest-key tracking), or `ScrollOverlayViewModel` (not registered; inert). If a guard test insists every detector registers, the guard is wrong, not the design.
- **The External applier's lock is a correctness requirement, not a nicety** — its detector is `Update`d on the gaze thread under `_stateLock`; an unlocked dispatcher-thread `SetEffectiveGeometry` would race target mutation.
- **Do not "optimize" the router to skip applies when a non-geometry knob caused `Changed`** — the payloadless event makes that filter fragile, and the apply is idempotent and cheap (one clamp per target per ≤4 Hz event).
- **A `GeometryMaxMarginPx` edit does not re-apply until the next fill write** (the provider's `Changed` fires on fill set/clear only) — known, bounded, self-healing (the §13.3 smoothing leftover class); do not add a settings-changed subscription in this slice.
- **The map composes against `BaseHitExtent`, never the live `Radius`/`Bounds`** — composing against the live value would compound on every re-apply. `ApplyHitAreaMargin` already derives from the as-registered extents; keep that invariant.
- **SDK rect updates go through `RebaseTarget`, never raw `Bounds`/`Center` writes** — a raw write desynchronizes the live geometry from the as-registered base extents, and the next map application (including the identity restore) reverts the SDK's size (round-1 blocker).
- **The Aiming signal's denominator now moves with the knob** (round-1 finding 8) — deliberate, documented, self-damping; do not "stabilize" `DwellOutcome.TargetRadius` to `BaseHitExtent` in this slice (it would also move the accidental-activation re-approach radius).
- **Run `ExternalDwellServiceTests` via the whole App project**, never the class alone (the XamlParseException dependency documented in the class remarks).
- **Phase-2 gate:** this slice completes Phase 1c — all four knobs reach a consumer. Say so in the PR; do not start Phase 2 here.
