# Learned-Offset (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the Phase-2 learned layer (design §7.4) — a slow, forgiving-only, decaying EWMA over the momentary band fills, composed inside `EffectiveSettingsProvider`, persisted atomically to `%APPDATA%/EyeCtrl/adaptation-state.json`, resettable from the settings page, cleared by the manual §8.6 escape (and *only* by it), and reviewable via a size-capped `adaptation-log.jsonl` plus an Open-Log button — completing "Learning" per spec §13.

**Architecture:** The provider owns a per-knob **learned-fill store** (fill units, `[0, AdaptationSettings.LearnedMaxFill]`) and composes it exactly as §5 specifies: the forgiving delta becomes `(momentaryForgivingFill + learnedFill) × bandEdge`, so the single band clamp bounds the sum. A new Core class `AdaptationLearner` computes the EWMA from the provider's momentary fills (a new read API — the provider previously had none), pushes at ≥ ε, owns `ResetLearned`, and persists via a new `AdaptationStateFile` (atomic temp+rename, per-entry validation). The coordinator ticks the learner on assess ticks only and forks its reset paths: session edges and the disable edge keep learned (§13.1 — it persists across sessions and survives disable inertly, because `Effective` gates on `Enabled` first); the manual toolbar/tray escape clears momentary **and** learned. `AdaptationController` and `StruggleMonitor` are **not touched**. The 1c routers (`GazeEngine`, `DwellTimeRouter`, `GeometryRouter`, the Keyboard tracker) need **zero changes** — `Changed` now also fires on learned pushes and on composition-relevant settings edits (which fixes the §13.3 settings-edit staleness leftover), and every consumer simply re-reads `Effective`.

**Tech Stack:** C# / .NET 10, xUnit v3, NFluent assertions (`Check.That(...)`, **not** `Assert.*` — wired globally in `tests/Directory.Build.props`, which also supplies global `using NFluent;` and `using Xunit;`).

---

## Global Constraints

- **Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §5 (value model: `forgiving = clamp(learnedOffset + momentaryForgiving, band)`; "slow EWMA per context/knob, persisted, decays toward 0"), §7.4 (this phase's mandate), §8.3/§8.6/§8.7 (invariants), §9.1 (persistence debounced off the hot path), §10 (log format + `ResetLearnedBaseline`), §13.1 (learned persists across session boundaries), §13.3 (routing resolutions + the self-damping NB), §14 (parked list). Read §7.4 and §5 first.
- **Work in the worktree** `.worktrees/learned-offset`, branch `feat/struggle-learned-offset` (this plan is committed there as Task 1, the geometry-slice pattern). Run every command from the worktree root. `dotnet build-server shutdown` before any worktree removal.
- **Never run the application or any `Category=UI` test.** The repo owner controls their PC solely by eye tracking; launching it seizes their only input channel. Everything here is verifiable headlessly. No test in this plan may launch a window — this is why the Open-Log button's action is an injected callback, never `Process.Start` inside a test.
- **One test suite at a time**, sequential (named pipes are machine-global; this box routinely runs a dozen worktrees and possibly a live app). Check the box is quiet first (`Get-Process -Name dotnet,testhost*,EyeCtrl*`).
- **German test output.** The summary reads `Bestanden!` / `Fehler!` with `Fehler: N, erfolgreich: M, gesamt: T`. Read the summary line; grepping "Failed" silently reports green.
- **Baselines at `main` = `4929c39d` (re-measured 2026-09-04, independently re-confirmed by review round 1):** Core **1162** (`erfolgreich: 1161, übersprungen: 1`) / ViewModels **300** / App (`--filter "Category!=UI"`) **742** / Benchmarks.Tests **27**, all green. Different starting counts ⇒ stop and report.
- **Do NOT modify `AdaptationController` or `StruggleMonitor`** (the kickoff constraint — 1c never touched them and Phase 2 has no cause: the learner composes *around* the momentary layer by reading its output fills).
- **Additive-only provider semantics.** With `learned == 0`, `Effective` must be bit-identical to today for every knob/context/baseline — the fill-space sum `(0 + momentary) × edge` is algebraically today's `momentary × edge`, and the no-fill early-return must still trigger when both components are zero. Consequently **every existing test in all four suites must pass unchanged** except the four App-test modification classes this plan names explicitly (AdaptationPageSourceTests fact 1's row count; its Reset fact's `.Single()`; the `Make` helpers and existing construction sites that gain constructor arguments — coordinator, handler, learner; and the `HandChunkedPageFitTests.Construct` optional-params fallback). GitNexus impact at `4929c39d`: `EffectiveSettingsProvider` **CRITICAL** (119 symbols, 24 direct, 2 processes) — that blast radius is *why* this constraint exists; `AdaptationCoordinator` MEDIUM (34 / 4 direct).
- **Blast-radius note for reviewers:** the four 1c consumers read `Effective` only; none is modified. `GeometryRouter.cs` gets a doc-comment correction only (its "a cap EDIT does not raise Changed" NB becomes false in this slice — that leftover is fixed here, see Task 2 Step 7).
- **Learned survives; only the manual escape clears.** Session edges (`ResetOnEnable`/`ResetOnDisconnect`/`ResetOnResume`) and the disable edge (`ClearedOnDisable`, `AdaptationActivation.StopRuntime`) clear momentary only. The manual `ResetAdaptationMessage` (toolbar/tray) clears momentary **and** learned. The settings-page `ResetLearnedOffsetsMessage` clears learned only. §10/§13.1.
- **All numbers are starting values, tunable in review** (house style): sample interval 30 s, α 0.05 (time constant ≈ 10 min), push ε 0.01, `LearnedMaxFill` 0.5, log cap 1 MB. Recorded as `public const`s (learner) / a `const` on `AdaptationSettings` — no new user-facing settings rows.
- **No new benchmark.** The learner's per-tick cost is O(1) over 4 dictionary reads on a 30 s cadence (and zero on non-sample ticks), far inside §9.1; a benchmark would measure noise. Recorded as a decision, owner-vetoable.
- NFluent only; no mocking library (private fakes / real Core pieces); file-scoped namespaces; Allman braces; `_camelCase` fields; nullable enabled. `EyeCtrl.Core` escalates CS1591 (missing XML doc) to an **error** — every new public Core member needs its `///`. `InternalsVisibleTo` → `EyeCtrl.Core.Tests` **and** → `EyeCtrl.App.Tests` (`EyeCtrl.App.csproj:71`) both exist — but this plan adds no new `internal` seams; App tests drive public APIs and real instances, the existing pattern.
- **If a step's expected output does not match, stop rather than adapting.** Anchors reference `main` at `4929c39d`; drift ⇒ report BLOCKED with what you saw.

> **Review round 1 (2026-09-04, external analytical review): BLOCKED → findings applied.** All 7 blocking findings fixed (1 decay-expectation asserted the never-pushed provider value; 2 `Load` missing case-insensitive read options; 3 atomic-test `finally` deleting `%TEMP%`; 4 `Make(bool enabled)` needs a default; 5 the fourth coordinator construction site `AdaptationRuntimeContainmentTests.NewCoordinator`; 6 the `AdaptationLog` DI registration; 7 the gaze-clock-restart guard). Should-fix taken: 8 (per-knob disable zeroes learned immediately), 9 (drive timeline + comments), 10/12/17 (spec amendments §9.1/§10/§4), 11 (suppressed marker only on actual changes), 13 (decay-to-zero visible in the log), 18 (Load gated on Enabled), 23/24/25/26/20/21/22 (as folded below). Dispositioned: 15 → D9, 16 → documented, 19 → documented. Counts moved: learner 10 → **12** facts, log 6 → **7**; Core final **1194**, App final **758**.
>
> **Review round 2 (2026-09-05, implement-verbatim dry run): all counts landed — 6 findings applied.** F1 (blocker-class ordering): the coordinator/handler ctor changes break `AppBootstrapper`'s compile, so the learner/coordinator/handler DI registrations **moved from Task 5 into Task 4**, together with the registration fact (Task 4 now ends at App **749**; Task 5 is code-only and stays 749). F2: the two *existing* reset-handler facts' 2-arg ctor calls are an explicit edit, not just `Make` arity. F3: `HandChunkedPageFitTests.Construct` requires an exact 1-arg ctor — it gains the optional-params fallback (the fourth allowed existing-test modification). F4/F5: `AdaptationPageSource` needs `using CommunityToolkit.Mvvm.Messaging;` and `SettingsWindow.xaml.cs` needs `using System.IO;`. F6: `.gitnexus/run.cjs` does not exist in this checkout — Task 7 Step 4 names the MCP alternative. Final totals unchanged: **Core 1194 / ViewModels 300 / App 758 / Benchmarks.Tests 27** (interim App chain now 742 → 749 → 749 → 758).

### Owner-vetoable decision ledger (settled 2026-09-04, brainstorm session; the owner was not reachable interactively — each entry records rationale + the rejected alternative, the §13.3-fact-6 pattern. Flip any of these by editing the named member, not the structure)

| # | Decision | Rationale | Rejected |
|---|---|---|---|
| D1 | **Seat:** provider-owned learned store + new `AdaptationLearner`; composition inside `Effective` | §5's formula at the single point every consumer already reads; `Changed` on learned pushes reuses the 1c routers unchanged; the read API also unlocks the JSONL log; controller/monitor untouched | Inside `AdaptationController` (the §4 diagram's AC label is a 1a-era sketch; merging learned into the momentary push blurs `ClearMomentaryFills`' escape semantics and opens the controller without need); beside-the-provider wrappers (duplicated composition at 4+ consumers, splits §8.3) |
| D2 | **Shape:** EWMA of the momentary forgiving fill (per-knob, global `Other` context) | §5 pre-says "slow EWMA"; one mechanism both accumulates on sustained momentary work and decays toward 0 when it stops; the learner thereby inherits the controller's gates/sustain/deadband instead of re-implementing confidence thresholds (the "promotion criterion" *is* the EWMA time constant over the already-gated fill) | Decay-window max (jumps to full band on one bad minute, needs a separate decay policy); explicit step-promotion + hold timer (more knobs, duplicates §7.2 machinery) |
| D3 | **Per-context: global per knob in v1** | The momentary layer is global (monitor context is null; all fills live at `DwellContext.Other`), so there is no per-context signal to learn from yet; the provider store is already `(knob)`→fill here and `(knob, ctx)` for momentary, so per-context learned is a schema-compatible future extension | Per-context learned now (would learn from nothing; §4's confidence-threshold gate is deferred with it) |
| D4 | **Cap:** `AdaptationSettings.LearnedMaxFill = 0.5` (const, not a setting) | Learned pays at most half the forgiving band, so the momentary layer always retains headroom to the full band — the offset can never pin a knob at its band edge, preserving "must continuously re-earn" (§8.7) | Expose as a slider (+1 row, +guard, +validate — the page is full); cap 1.0 (zero momentary headroom) |
| D5 | **Persistence:** `adaptation-state.json`, atomic tmp+rename, per-entry validation (invalid entries dropped to 0, survivors kept), version-mismatch/corrupt ⇒ whole-file reset; save on app exit + `ResetLearned` only | §7.4 names the file + `SettingsService.Save`'s atomic write + `CalibrationService.Load`'s validation template; per-entry recovery is the `ClampGazeIndicator` precedent at file granularity and §7.4's "ignored and reset" read at entry granularity; crash loses only learning since last save (comfort data, re-learns in minutes) — accepted. **Known deviation, amended in Task 7:** §9.1 says "learned-state persistence is debounced to session boundaries" — exit + reset saves are the realized v1 form of that debounce (a session-boundary flush adds write churn for at most one session's worth of re-learnable data) | Periodic dirty flush (unneeded given the crash-loss acceptance); whole-file discard on any invalid entry (punishes valid entries); settings.json section (§14 keeps supervisor-write semantics separate) |
| D6 | **Log review:** size-capped `adaptation-log.jsonl` (mode transitions + learned pushes, fill units, sensitive-suppressed) + "Open Adaptation Log" and "Reset Learned Offsets" buttons on the Adaptation page | §10's format realized in the provider's native units (absolute effectives are consumer-relative — ~20 dwell baselines — and deliberately not logged; spec amendment records the substitution); buttons keep it gaze-reachable | A settings-page log viewer (parse+render+paginate — its own slice); live learned-status rows (values move on a 30 s+ cadence; a stale readout is worse than none — deferred with §10's "live status label" wording) |
| D7 | **Leftovers:** (a) settings-edit `Changed` notification **folded** (Task 2 — one provider-level subscription fixes the class for every consumer); (b) §13.3 stale preamble **folded** (Task 7); (c) `SettingsService.Load` whole-file wipe **parked** (different file, hot guarded surface, own review burden); (d) stale DI/router comments **folded** (Tasks 2 & 7) | (a) is Phase-2-shaped: learned pushes make band-cap edits more frequent, and the fix is one subscription; (c) the new file uses per-entry recovery by construction without touching settings.json behavior | — |
| D8 | **Carried calibration inputs parked:** no Aiming-denominator instrumentation, no §13.2 threshold changes | No live data exists yet; the JSONL (this slice) collects exactly the data a later calibration needs (`knob=Aiming` fill transitions). Task 7 records this under §14. NB the expected steady state differs per knob: for `TargetGeometry` the §13.3 self-damping NB means enlargement extinguishes its own evidence, so that knob's learned offset is structurally the *least* able to converge — expected, not a defect; the log will show it | Instrument now (unmeasurable) |
| D9 | **No offline aging:** `SavedAtUtc` is diagnostic only; learned offsets do not decay across disabled/idle stretches, only across active assess ticks | Full wall-clock decay on `Load()` would wipe the baseline on every overnight restart (16 h offline at τ ≈ 10 min ⇒ factor ≈ 0.007) — antithetical to §2's "slow baseline". Exposure, stated: a user who learns 0.3, disables for a month, and re-enables gets 0.3 applied instantly (the manual escape and the per-knob toggles clear it immediately if unwanted). If real usage disagrees, the fix is a capped offline-decay term in `Load()` — one line, owner call | Wall-clock decay on Load (kills the baseline overnight); full re-earn on every enable (throws away legitimate learning) |

---

## Context you need before starting

**What exists (all at `main` `4929c39d`).** The Phase-1c substrate is complete: `EffectiveSettingsProvider` (per-`(knob, context)` momentary fills as dimensionless `[0,1]` band positions; absolute delta resolved at read time as `fill × sign × bandFraction × baseline`; `Changed` fires only on fill set/clear; `ConcurrentDictionary` store — dispatcher-thread single writer, gaze-thread readers; **no read API**), `AdaptationController` (per-knob `KnobState`, relax/recover/deadband stepping, pushes at `DwellContext.Other` only, per-knob `Adapt*` enables, `Reset()` clears state + provider), `AdaptationCoordinator` (250 ms dispatcher tick; enable/disable/disconnect/resume edges; `Tick()` branch enum `CoordinatorTickResult`; `RunTick` crash-guarded; public `Reset()` is used by BOTH the manual escape and — via `AdaptationActivation.StopRuntime` — the disable edge), `AdaptationActivation` (starts/stops the runtime on the `Enabled` edge; `StopRuntime` calls `coordinator.Reset()` then `Dispose`), `AdaptationResetHandler` (`ResetAdaptationMessage` → `coordinator.Reset()`), `AdaptationLog` (Serilog entries on gated-mode transitions; its doc comment names "finer detail + the JSONL file/review" as Phase 2), `EffectiveValue.Compute` (its `forgivingDelta` param is *already documented* as "Combined learned + momentary forgiving delta" — the 1a calculator reserved this seam), and the settings-page row set (15 rows, one button).

**Facts that shape this plan:**

1. **Fill units compose.** §5's `clamp(learned + momentary, band)` is exactly `(learnedFill + momentaryFill) × bandEdge` passed to `EffectiveValue.Compute`, whose band clamp does the rest — for both forgiving signs. With `learned == 0` this is bit-identical to today, which is what keeps the 24-direct-caller CRITICAL blast radius at zero functional risk (pinned by the unchanged-existing-tests constraint above).
2. **The provider is the only composition point.** `GazeEngine` (smoothing), `DwellTimeRouter` (dwell), `GeometryRouter` (geometry, margin-capped by `GeometryMaxMarginPx`), and the Keyboard tracker all read `Effective` and re-apply on `Changed`. A learned fill that raises `Changed` reaches every surface with no router change. Smoothing's 1 s adaptive rebuild budget is far above the learner's ≥ 30 s push floor.
3. **Learned must survive five of the six reset paths.** Only the manual message clears it. `AdaptationActivation.StopRuntime` currently reuses the manual `coordinator.Reset()` for the disable edge — Task 4 forks this (`ResetMomentary()`), or every settings toggle would wipe learning.
4. **Forgiving-only is structural.** The learned store holds one non-negative fill per knob; there is no learned tightening component anywhere, so no learned value can express a tightening move (§8.2's quarantine untouched; the tightening sub-band math is unchanged).
5. **The EWMA self-decays and inherits safety — and the per-knob toggles zero learned *immediately*** (review round 1, finding 8). Sampling only happens on assess ticks (paused/disconnected/disabled ticks never call the learner — state freezes); the observed value is the *post-hysteresis* momentary fill, so the controller's gates, sustain timers and deadband do the promotion filtering. A per-knob disable (`AdaptDwell = false` etc.) is a user-facing off switch: the controller collapses that knob's momentary fill to 0 at once, and the learner must do the same to its learned offset (a ~40-minute decay tail on an off switch would be a defect), so `Tick` zeroes a disabled knob outright instead of stepping it.
6. **JSONL suppression rides the existing seam.** `IFeedbackGate.IsFeedbackSuppressed` (§8.10) already gates `AdaptationLog`; the JSONL gets the same coarse-marker treatment — but only when there was something to suppress (a marker per `Changed` would write 4 lines/s during momentary ramps in a password field; review round 1, finding 11). The learned-*state* file is not a log — it stores four numbers and is not suppressed.
7. **`InternalsVisibleTo` exists for both test projects** (`EyeCtrl.Core.csproj:12`, `EyeCtrl.App.csproj:71` — the AGENTS.md claim that only Core declares it is outdated). This plan adds no new `internal` seams regardless; App tests drive public APIs and real instances (the coordinator/handler test pattern).
8'. **The learner's clock is the gaze clock, which restarts.** The coordinator's `now` is the latest gaze timestamp; a tracker reconnect can restart that clock near 0. A due-time guard (`now < due → return`) would then block sampling for the whole old epoch's uptime — the silent-degradation class this codebase already guards three times (`StruggleMonitor`'s session-start resync, `GazeEngine`'s rebuild-budget resync, `TelemetryClockRestartTests`). The learner therefore keeps a *last-sample* timestamp and resyncs on any backward jump (review round 1, finding 7).
9'. **One sample can raise `Changed` up to four times** (one per moved knob) — four in-place router re-applies per ≥ 30 s. Accepted deliberately (review round 1, finding 19): the applies are in-place and idempotent, the JSONL entry-per-knob is desired, and a batch API would widen the provider's surface for a cost measured in microseconds per half-minute.
8. **App wiring anchors:** `AppBootstrapper.BuildServiceProvider` computes `appDataDir` (`%APPDATA%/EyeCtrl`) in scope at the registration site; the adaptation DI block sits at `AppBootstrapper.cs:297-358`; the activation start at `App.xaml.cs:340-345`; `OnExit` saves settings at `App.xaml.cs:409`; `using EyeCtrl.Core.Adaptation;` is already imported in `App.xaml.cs`. `SettingsWindow.xaml.cs:389` constructs `AdaptationPageSource(_ctx)`; `ButtonKind` is `{ Default, Reset, Delete }`.
9. **The spec's own text needs three touches** (Task 7): §13.3's preamble still says "only one of the four knobs reaches a consumer … a quarter live" (stale since PR #131); §5/§4 diagram wording ("per context/knob", AC labeled with the learned offset) needs the v1 scoping note; §10's log format needs the fill-unit substitution + the realized Phase-2 deliverable list.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs` | Learned store + read API + composition + settings-notify | Modify (Task 2) |
| `src/EyeCtrl.Core/Settings/AdaptationSettings.cs` | `LearnedMaxFill` const | Modify (Task 2) |
| `src/EyeCtrl.App/Services/GeometryRouter.cs` | Doc drift: cap-edit NB now false | Comment only (Task 2) |
| `src/EyeCtrl.Core/Adaptation/AdaptationState.cs` | **New.** Persisted DTO + per-entry validation | Task 3 |
| `src/EyeCtrl.Core/Adaptation/AdaptationStateFile.cs` | **New.** Load (validated) / Save (atomic) | Task 3 |
| `src/EyeCtrl.Core/Adaptation/AdaptationLearner.cs` | **New.** EWMA + cadence + push + reset + persist | Task 3 |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | `ResetLearnedOffsetsMessage` record | +1 line (Task 4) |
| `src/EyeCtrl.App/Services/AdaptationCoordinator.cs` | Learner dep + tick + reset fork + `ResetMomentary` | Modify (Task 4) |
| `src/EyeCtrl.App/Services/AdaptationActivation.cs` | Disable edge → `ResetMomentary()` | 1 line + comment (Task 4) |
| `src/EyeCtrl.App/Services/AdaptationResetHandler.cs` | + learner + second message | Modify (Task 4) |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | DI: learner + coordinator/handler registrations (Task 4, round-2 F1), stale provider comment (Task 5), `AdaptationLog` registration (Task 6) | Modify (Tasks 4-6) |
| `src/EyeCtrl.App/App.xaml.cs` | `Load()` at startup, `Persist()` at exit | 2 insertions (Task 5) |
| `src/EyeCtrl.App/Services/AdaptationLog.cs` | JSONL (learned pushes + mode transitions, cap, suppression) | Modify (Task 6) |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | **Also Task 6:** the `AdaptationLog` registration gains provider + path | Modify (Task 6) |
| `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs` | + `ad_reset_learned`, `ad_log` buttons; optional open-log callback | Modify (Task 6) |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | Wire open-log callback | 1 arg + 1 method (Task 6) |
| spec `…self-adaptation-design.md` | §7.4 resolution, §5/§4 scoping, §13.3 preamble fix, §13 status, §14 updates | Task 7 |
| `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs` | +12 facts; `Make` gains a default arg | Task 2 |
| `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationLearnerTests.cs` | **New**, 12 facts | Task 3 |
| `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationStateFileTests.cs` | **New**, 8 facts | Task 3 |
| `tests/EyeCtrl.App.Tests/Services/AdaptationCoordinatorTests.cs` | Make + learner; +4 facts | Task 4 |
| `tests/EyeCtrl.App.Tests/Services/AdaptationResetHandlerTests.cs` | Make + learner; +2 facts | Task 4 |
| `tests/EyeCtrl.App.Tests/Services/AdaptationRuntimeContainmentTests.cs` | Compile fix: `NewCoordinator` gains the learner arg (no fact change) | Task 4 |
| `tests/EyeCtrl.App.Tests/Services/TelemetrySinkRegistrationTests.cs` | +1 registration fact (moved into Task 4 by round-2 F1) | Task 4 |
| `tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs` | Make + provider/path; +7 facts | Task 6 |
| `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs` | Fact 1 → 17 rows; Reset fact `.Single()` filter; +2 facts | Task 6 |
| `tests/EyeCtrl.App.Tests/Settings/HandChunkedPageFitTests.cs` | `Construct` optional-params fallback (round-2 F3) | Task 6 |

**No ViewModels production change beyond the +1 message record; no router/engine/controller/monitor change.**

**Expected final counts (after review round 1):** Core **1194** (1193 + 1 skip) / ViewModels **300** / App **758** / Benchmarks.Tests **27**.

---

### Task 1: Commit the plan file itself

The kickoff provides this plan in the working tree (untracked). Commit it first so `git diff --stat main` lists it (the geometry-slice Task-1 pattern; round-1 finding 9 there).

- [ ] **Step 1: Commit**

```bash
git add docs/superpowers/plans/2026-09-04-learned-offset-phase2.md
git commit -m "docs(plan): learned-offset Phase-2 implementation plan"
```

---

### Task 2: Core — the provider's learned store, read API, composition, and the settings-edit notification (leftover a)

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs`
- Modify: `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`
- Modify: `src/EyeCtrl.App/Services/GeometryRouter.cs` (doc comment only)
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs` (append)

**Interfaces:**
- Produces (Task 3/4/6 consume these):
  - `EffectiveSettingsProvider.SetLearnedFill(AdaptationKnob knob, double fill)` — clamps to `[0, AdaptationSettings.LearnedMaxFill]`, raises `Changed` on value change.
  - `EffectiveSettingsProvider.ClearLearnedFills()` — clears all learned fills, raises `Changed` if any were present.
  - `bool TryGetMomentaryFill(AdaptationKnob knob, DwellContext context, out double forgivingFill, out double tighteningFill)`.
  - `IReadOnlyList<(AdaptationKnob Knob, double Fill)> EnumerateLearnedFills()` — non-zero entries only.
  - `const double AdaptationSettings.LearnedMaxFill = 0.5`.

- [ ] **Step 1: Write the failing tests**

Append inside the existing `EffectiveSettingsProviderTests` class. **First give the helper a default argument** (review round 1, finding 4 — its current signature is `Make(bool enabled)` with no default, so the parameterless calls below would not compile; the second tuple element is named `Settings`):

```csharp
    private static (EffectiveSettingsProvider Provider, AdaptationSettings Settings) Make(bool enabled = false)
```

(Every existing call site passes `enabled:` explicitly, so this is purely additive; each new fact that needs composition sets `Settings.Enabled = true` itself.)

```csharp
    [Fact]
    public void Learned_SumsWithMomentary_AtTheBandEdge()
    {
        var (p, a) = Make();
        a.Enabled = true;
        a.ToleranceLooserMaxFraction = 0.5;
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 0.9, 0.0);
        p.SetLearnedFill(AdaptationKnob.FixationRadius, 0.3);
        // 0.3 + 0.9 = 1.2 band-fills → clamped to the +50% band edge: 50 → 75.
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50.0)).IsEqualTo(75.0);
    }

    [Fact]
    public void Learned_Alone_ComposesTheForgivingDirection()
    {
        var (p, a) = Make();
        a.Enabled = true;
        a.DwellShorterMaxFraction = 0.4;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.4);
        // delta = −0.4 fill × 0.4 fraction × 1000 ms = −160 → 840. Learned alone must move effective.
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 1000.0)).IsEqualTo(840.0);
    }

    [Fact]
    public void LearnedZero_EffectiveIsExactlyMomentaryOnly()
    {
        var (p, a) = Make();
        a.Enabled = true;
        a.ToleranceLooserMaxFraction = 0.5;
        p.SetLearnedFill(AdaptationKnob.FixationRadius, 0.0); // stored zero — must behave like absence
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 0.6, 0.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50.0)).IsEqualTo(65.0);
    }

    [Fact]
    public void Disabled_ReturnsBaseline_EvenWithLearnedFillSet()
    {
        var (p, a) = Make();
        a.Enabled = false;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.5);
        p.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 1000.0)).IsEqualTo(1000.0);
    }

    [Fact]
    public void SetLearnedFill_RaisesChangedOnlyOnValueChange()
    {
        var (p, _) = Make();
        int raised = 0;
        p.Changed += () => raised++;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.2);
        Check.That(raised).IsEqualTo(1);
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.2); // same value → no raise
        Check.That(raised).IsEqualTo(1);
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.3);
        Check.That(raised).IsEqualTo(2);
    }

    [Fact]
    public void ClearLearnedFills_ClearsAndRaises_OnlyWhenPresent()
    {
        var (p, a) = Make();
        a.Enabled = true;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.4);
        int raised = 0;
        p.Changed += () => raised++;
        p.ClearLearnedFills();
        Check.That(raised).IsEqualTo(1);
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 1000.0)).IsEqualTo(1000.0);
        p.ClearLearnedFills(); // already empty → no raise
        Check.That(raised).IsEqualTo(1);
    }

    [Fact]
    public void Learned_IsForgivingOnly_EveryKnobMovesInItsSafeDirection()
    {
        var (p, a) = Make();
        a.Enabled = true;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.5);
        p.SetLearnedFill(AdaptationKnob.Smoothing, 0.5);
        p.SetLearnedFill(AdaptationKnob.TargetGeometry, 0.5);
        p.SetLearnedFill(AdaptationKnob.FixationRadius, 0.5);
        // dwell shorter, smoothing parameter DOWN, geometry bigger, tolerance looser (§7.1).
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 1000.0)).IsStrictlyLessThan(1000.0);
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsStrictlyLessThan(5.0);
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 40.0)).IsStrictlyGreaterThan(40.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50.0)).IsStrictlyGreaterThan(50.0);
    }

    [Fact]
    public void SetLearnedFill_ClampsToLearnedMaxFill()
    {
        var (p, _) = Make();
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.9);
        var entries = p.EnumerateLearnedFills();
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Knob).IsEqualTo(AdaptationKnob.DwellTime);
        Check.That(entries[0].Fill).IsEqualTo(AdaptationSettings.LearnedMaxFill);
    }

    [Fact]
    public void BandFractionEdit_WhileFillStored_RaisesChanged()
    {
        // Leftover (a): a band edit used to refresh the applied value only at the next fill write.
        var (p, a) = Make();
        a.Enabled = true;
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 0.6, 0.0);
        int raised = 0;
        p.Changed += () => raised++;
        a.ToleranceLooserMaxFraction = 0.3; // still valid; composition-relevant
        Check.That(raised).IsEqualTo(1);
    }

    [Fact]
    public void GeometryMarginCapEdit_RaisesChanged()
    {
        var (p, a) = Make();
        a.Enabled = true;
        int raised = 0;
        p.Changed += () => raised++;
        a.GeometryMaxMarginPx = 150.0;
        Check.That(raised).IsEqualTo(1);
        a.Posture = AdaptationPosture.Balanced; // not composition-relevant → no raise
        Check.That(raised).IsEqualTo(1);
    }

    [Fact]
    public void TryGetMomentaryFill_ReturnsStoredValues_AndFalseWhenAbsent()
    {
        var (p, a) = Make();
        a.Enabled = true;
        p.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.4, 0.2);
        Check.That(p.TryGetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, out double f, out double t)).IsTrue();
        Check.That(f).IsEqualTo(0.4);
        Check.That(t).IsEqualTo(0.2);
        Check.That(p.TryGetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, out _, out _)).IsFalse();
    }

    [Fact]
    public void EnumerateLearnedFills_ListsNonZeroEntriesOnly()
    {
        var (p, _) = Make();
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.2);
        p.SetLearnedFill(AdaptationKnob.Smoothing, 0.0); // stored zero → not listed
        var entries = p.EnumerateLearnedFills();
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Knob).IsEqualTo(AdaptationKnob.DwellTime);
        Check.That(entries[0].Fill).IsCloseTo(0.2, 1e-9);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: **Fehler!** — compile errors (`SetLearnedFill`/`ClearLearnedFills`/`TryGetMomentaryFill`/`EnumerateLearnedFills`/`LearnedMaxFill` do not exist). Compile failure is the failing state for API-addition steps.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`, add inside the class (after the `GeometryMaxMarginPx` property):

```csharp
    /// <summary>The maximum learned offset, as a fraction of the knob's forgiving band (§7.4). A const,
    /// not a setting: learned may pay at most half the band so the momentary layer always retains
    /// headroom to the full band — the offset can never pin a knob at its band edge (§8.7).</summary>
    public const double LearnedMaxFill = 0.5;
```

In `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs`:

(a) Extend the usings (the file has `EyeCtrl.Core.Settings` and `System.Collections.Concurrent`):

```csharp
using System.Collections.Generic;
using System.ComponentModel;
```

(b) Add the learned store beside `_fills`:

```csharp
    // Phase-2 learned fills (§7.4), keyed by knob only — v1 is global per knob (the momentary layer it
    // learns from is global; per-context learned arrives with per-context momentary fills, if ever).
    // Forgiving-only by construction: one non-negative fill per knob, no tightening component exists.
    // ConcurrentDictionary for the same reason as _fills: Effective reads it on the gaze thread.
    private readonly ConcurrentDictionary<AdaptationKnob, double> _learnedFills = new();
```

(c) Replace the constructor with a subscribing one:

```csharp
    /// <summary>Initializes the provider with the master adaptation settings (the enable gate + band-fraction source).</summary>
    /// <param name="adaptation">The adaptation settings whose <see cref="AdaptationSettings.Enabled"/> gates composition and whose band fractions bound each knob.</param>
    public EffectiveSettingsProvider(AdaptationSettings adaptation)
    {
        _adaptation = adaptation;
        // Composition-relevant settings edits re-raise Changed so consumers re-read: without this, a band
        // fraction or margin-cap edit while a fill is stored refreshed the applied value only at the next
        // fill write (the §13.3 settings-edit staleness leftover, fixed here for every consumer at once).
        // Enabled and Posture are excluded: the enable edge is owned by the coordinator/activation (which
        // also clear momentary fills), and posture affects only step rates, not composition. No unsubscribe:
        // provider and settings are co-extensive container singletons.
        _adaptation.PropertyChanged += OnAdaptationSettingsChanged;
    }

    private static readonly HashSet<string> CompositionRelevantSettings = new()
    {
        nameof(AdaptationSettings.DwellShorterMaxFraction),
        nameof(AdaptationSettings.DwellLongerMaxFraction),
        nameof(AdaptationSettings.ToleranceLooserMaxFraction),
        nameof(AdaptationSettings.SmoothingMoreMaxFraction),
        nameof(AdaptationSettings.GeometryBiggerMaxFraction),
        nameof(AdaptationSettings.GeometryMaxMarginPx),
    };

    private void OnAdaptationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && CompositionRelevantSettings.Contains(e.PropertyName))
            Changed?.Invoke();
    }
```

(d) Replace the body of `Effective` between the enabled gate and the policy lookup with the learned-aware version, and feed the summed fill into `EffectiveValue.Compute`:

```csharp
        bool hasMomentary = _fills.TryGetValue((knob, context), out var f)
            && (f.ForgivingFill != 0.0 || f.TighteningFill != 0.0);
        double learned = _learnedFills.TryGetValue(knob, out double l) ? l : 0.0;
        if (!hasMomentary && learned == 0.0)
            return baseline;
```

and in the `Compute` call, change the forgiving argument from `f.ForgivingFill * forgivingEdge` to:

```csharp
            (f.ForgivingFill + learned) * forgivingEdge,
```

(Leave the tightening argument and both band tuples exactly as they are. With `learned == 0` this is bit-identical to the previous expression — the additive-only constraint.)

(e) Add the four new public members (after `ClearMomentaryFills`), each with its `///` (CS1591):

```csharp
    /// <summary>
    /// Sets the Phase-2 learned band-fill fraction for a knob (§7.4) and raises <see cref="Changed"/> on
    /// value change. The value is clamped to <c>[0, <see cref="AdaptationSettings.LearnedMaxFill"/>]</c>:
    /// the store cannot hold a tightening or out-of-band learned offset, whatever the caller passes.
    /// </summary>
    /// <param name="knob">The knob.</param>
    /// <param name="fill">Learned band-fill fraction toward "easier" (forgiving-only, ≥ 0).</param>
    public void SetLearnedFill(AdaptationKnob knob, double fill)
    {
        double clamped = Math.Clamp(fill, 0.0, AdaptationSettings.LearnedMaxFill);
        double old = _learnedFills.GetOrAdd(knob, 0.0);
        if (clamped == old)
            return;
        _learnedFills[knob] = clamped;
        Changed?.Invoke();
    }

    /// <summary>Clears every learned fill (manual reset / load) and raises <see cref="Changed"/> if any were present.</summary>
    public void ClearLearnedFills()
    {
        if (_learnedFills.Count == 0)
            return;
        _learnedFills.Clear();
        Changed?.Invoke();
    }

    /// <summary>Reads the stored momentary fills for a knob/context — the Phase-2 learner's observation
    /// seam (the provider previously had no read API at all).</summary>
    /// <param name="knob">The knob.</param>
    /// <param name="context">The context.</param>
    /// <param name="forgivingFill">Receives the stored forgiving band fill (0 when absent).</param>
    /// <param name="tighteningFill">Receives the stored tightening band fill (0 when absent).</param>
    /// <returns>True when a fill entry is stored for the knob/context.</returns>
    public bool TryGetMomentaryFill(AdaptationKnob knob, DwellContext context, out double forgivingFill, out double tighteningFill)
    {
        if (_fills.TryGetValue((knob, context), out var f))
        {
            forgivingFill = f.ForgivingFill;
            tighteningFill = f.TighteningFill;
            return true;
        }
        forgivingFill = 0.0;
        tighteningFill = 0.0;
        return false;
    }

    /// <summary>The non-zero learned fills, for persistence, logging, and status.</summary>
    public IReadOnlyList<(AdaptationKnob Knob, double Fill)> EnumerateLearnedFills()
    {
        var list = new List<(AdaptationKnob, double)>();
        foreach (var (knob, fill) in _learnedFills)
            if (fill != 0.0)
                list.Add((knob, fill));
        return list;
    }
```

(f) Extend the class doc comment: after the sentence about `Effective` returning the exact baseline, add one paragraph:

```csharp
/// <para>
/// Phase 2 (§7.4) adds a per-knob <b>learned</b> band fill — forgiving-only, clamped to
/// <see cref="AdaptationSettings.LearnedMaxFill"/> — composed with the momentary forgiving fill
/// before the single band clamp (design §5: forgiving = clamp(learned + momentary, band)).
/// <see cref="Changed"/> therefore also fires on learned set/clear and on composition-relevant
/// settings edits (band fractions, the geometry margin cap), not only on momentary fill writes.
/// </para>
```

- [ ] **Step 4: Fix the now-stale GeometryRouter NB**

In `src/EyeCtrl.App/Services/GeometryRouter.cs` (doc comment, lines ~23-25), replace:

```
/// precedent). NB: a cap EDIT does not raise <c>Changed</c> (fills only), so a lowered cap takes
/// effect at the next fill write — bounded staleness, self-healing (the §13.3 smoothing leftover
/// class). UI-thread: constructed eagerly at startup (it subscribes and applies in its ctor — see
/// AppBootstrapper / App.xaml.cs).
```

with:

```
/// precedent). A cap EDIT raises <c>Changed</c> since the Phase-2 learned-offset slice (the provider
/// subscribes to the composition-relevant settings edits), so a lowered cap re-applies immediately.
/// UI-thread: constructed eagerly at startup (it subscribes and applies in its ctor — resolved in
/// App.xaml.cs's eager-resolve block).
```

- [ ] **Step 5: Run the suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: `Bestanden!` with `erfolgreich: 1173, übersprungen: 1, gesamt: 1174`. All 14 pre-existing provider facts must pass **unchanged** — if any of them fails, the composition change is not additive; stop and re-derive.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs src/EyeCtrl.Core/Settings/AdaptationSettings.cs src/EyeCtrl.App/Services/GeometryRouter.cs tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs
git commit -m "feat(adaptation): provider learned-fill store, read API, and settings-edit notification"
```

---

### Task 3: Core — `AdaptationLearner`, `AdaptationState`, `AdaptationStateFile`

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/AdaptationState.cs`
- Create: `src/EyeCtrl.Core/Adaptation/AdaptationStateFile.cs`
- Create: `src/EyeCtrl.Core/Adaptation/AdaptationLearner.cs`
- Test (new): `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationLearnerTests.cs`
- Test (new): `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationStateFileTests.cs`

**Interfaces:**
- Consumes (Task 2): `SetLearnedFill`, `ClearLearnedFills`, `TryGetMomentaryFill`, `EnumerateLearnedFills`, `AdaptationSettings.LearnedMaxFill`.
- Produces (Tasks 4-6): `AdaptationLearner(EffectiveSettingsProvider provider, AdaptationSettings adaptation, ILogger<AdaptationLearner>? logger = null, string? stateFilePath = null)`, `void Load()`, `void Tick(long nowMicroseconds)`, `void ResetLearned()`, `void Persist()`, `IReadOnlyList<(AdaptationKnob Knob, double Fill)> Snapshot()`; `AdaptationStateFile.Load(string path, ILogger? logger = null) → AdaptationState`, `AdaptationStateFile.Save(string path, AdaptationState state)`; `AdaptationState(int Version, DateTimeOffset SavedAtUtc, IReadOnlyDictionary<AdaptationKnob, double> Learned)` + `AdaptationState.CurrentVersion` + `AdaptationState.IsValueValid(double)`.

- [ ] **Step 1: Write the failing learner tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationLearnerTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class AdaptationLearnerTests
{
    private const long Sec = 1_000_000;
    private const long Sample = AdaptationLearner.SampleIntervalMicroseconds;

    private static (AdaptationLearner Learner, EffectiveSettingsProvider Provider) Make(string? path = null)
    {
        var settings = new EyeCtrlSettings();
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        return (new AdaptationLearner(provider, settings.Adaptation, stateFilePath: path), provider);
    }

    private static void Observe(EffectiveSettingsProvider provider, double forgivingFill)
        => provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill, 0.0);

    [Fact]
    public void FirstTick_SamplesImmediately_AndPushesAboveEpsilon()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(10 * Sec);
        // one sample: learned = α·1.0 = 0.05 ≥ ε → pushed to the provider.
        var entries = provider.EnumerateLearnedFills();
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Knob).IsEqualTo(AdaptationKnob.DwellTime);
        Check.That(entries[0].Fill).IsCloseTo(0.05, 1e-9);
    }

    [Fact]
    public void SubCadenceTicks_SkipSampling()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(10 * Sec);
        learner.Tick(10 * Sec + 29 * Sec); // 29 s later — below the 30 s cadence → skipped
        var entries = provider.EnumerateLearnedFills();
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Fill).IsCloseTo(0.05, 1e-9);
        learner.Tick(10 * Sec + 30 * Sec); // cadence reached → second sample
        Check.That(provider.EnumerateLearnedFills()[0].Fill).IsCloseTo(0.0975, 1e-9);
    }

    [Fact]
    public void SustainedMaxFill_AccumulatesAtAlphaPerSample()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        learner.Tick(Sec + 2 * Sample);
        // 1 − 0.95³ after three samples of observed 1.0.
        Check.That(provider.EnumerateLearnedFills()[0].Fill).IsCloseTo(0.142625, 1e-9);
    }

    [Fact]
    public void ObservedZero_DecaysTowardZero()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(Sec); // sample 1: learned 0.05 (pushed)
        Observe(provider, 0.0); // struggle stopped — the momentary layer has recovered to baseline
        for (int i = 1; i <= 10; i++)
            learner.Tick(Sec + i * Sample);
        // Assert the LEARNER's value (review round 1, finding 1): the provider only sees pushes that
        // move ≥ ε, and the only push in this window fires at i=5 (0.038689…), leaving the provider
        // behind the internal value (0.0299368). The learner is the source of truth for the math;
        // provider-side rate limiting is covered by AccumulationBelowEpsilon_DefersTheProviderPush.
        Check.That(learner.Snapshot().Single(e => e.Knob == AdaptationKnob.DwellTime).Fill)
            .IsCloseTo(0.05 * Math.Pow(0.95, 10), 1e-9);
    }

    [Fact]
    public void Learned_CappedAtLearnedMaxFill()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        for (int i = 0; i <= 200; i++)
            learner.Tick(Sec + i * Sample);
        Check.That(provider.EnumerateLearnedFills()[0].Fill).IsEqualTo(AdaptationSettings.LearnedMaxFill);
    }

    [Fact]
    public void AccumulationBelowEpsilon_DefersTheProviderPush()
    {
        var (learner, provider) = Make();
        Observe(provider, 0.05); // equilibrium 0.05 > ε, but early samples are below it
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        learner.Tick(Sec + 2 * Sample);
        learner.Tick(Sec + 3 * Sample); // 4 samples: 0.00927… still < ε
        Check.That(provider.EnumerateLearnedFills()).IsEmpty();
        learner.Tick(Sec + 4 * Sample); // 5th sample crosses ε
        Check.That(provider.EnumerateLearnedFills()).HasSize(1);
    }

    [Fact]
    public void TwoKnobsAccumulateIndependently()
    {
        var (learner, provider) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, 1.0, 0.0);
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        // Both elevated knobs learn (each ≈ 0.0975 after two samples); the two idle knobs stay absent.
        var entries = provider.EnumerateLearnedFills();
        Check.That(entries).HasSize(2);
        Check.That(entries.Select(e => e.Knob).ToList())
            .ContainsOnlyElementsThatMatch(k => k is AdaptationKnob.DwellTime or AdaptationKnob.Smoothing);
    }

    [Fact]
    public void MomentaryTighteningAlone_NeverLearns()
    {
        var (learner, provider) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.0, 0.9);
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        learner.Tick(Sec + 2 * Sample);
        Check.That(provider.EnumerateLearnedFills()).IsEmpty();
    }

    [Fact]
    public void ClockRestart_ResumesSamplingImmediately()
    {
        // Review round 1, finding 7: the coordinator's clock is the GAZE clock; a tracker reconnect can
        // restart it near 0. A due-time guard would block sampling for the old epoch's uptime — the
        // StruggleMonitor/GazeEngine resync class. A backward jump resyncs and samples again at once.
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(3600 * Sec); // an hour of uptime: sample 1 (learned 0.05)
        learner.Tick(3600 * Sec + Sample); // sample 2 (0.0975)
        learner.Tick(2 * Sec); // clock restarted near 0 — long BEFORE the next due time; must sample NOW
        // The restart tick itself is sample 3 (observed still 1.0): 0.0975 + 0.05·(1 − 0.0975).
        Check.That(learner.Snapshot().Single(e => e.Knob == AdaptationKnob.DwellTime).Fill)
            .IsCloseTo(0.142625, 1e-9);
    }

    [Fact]
    public void PerKnobDisable_ZeroesItsLearnedOffsetImmediately()
    {
        // Review round 1, finding 8: the per-knob Adapt* toggles are user-facing off switches; the
        // controller collapses a disabled knob's momentary fill at once, and the learner must match —
        // a 40-minute decay tail on an off switch would keep adapting after the user turned it off.
        var settings = new EyeCtrlSettings();
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var learner = new AdaptationLearner(provider, settings.Adaptation);
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        learner.Tick(Sec + 2 * Sample);
        Check.That(provider.EnumerateLearnedFills()).HasSize(1); // learned ≈ 0.1426

        settings.Adaptation.AdaptDwell = false;
        learner.Tick(Sec + 3 * Sample);

        Check.That(provider.EnumerateLearnedFills()).IsEmpty(); // zeroed at the very next sample
    }

    [Fact]
    public void ResetLearned_ClearsProviderAndPersistsZeros()
    {
        string path = Path.Combine(Path.GetTempPath(), "eyectrl_learner_test_" + Guid.NewGuid() + ".json");
        try
        {
            var (learner, provider) = Make(path);
            Observe(provider, 1.0);
            learner.Tick(Sec);
            Check.That(provider.EnumerateLearnedFills()).HasSize(1);

            learner.ResetLearned();

            Check.That(provider.EnumerateLearnedFills()).IsEmpty();
            var state = AdaptationStateFile.Load(path);
            Check.That(state.Learned.Values).ContainsOnlyElementsThatMatch(v => v == 0.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_AppliesPersistedFillsToProvider()
    {
        string path = Path.Combine(Path.GetTempPath(), "eyectrl_learner_test_" + Guid.NewGuid() + ".json");
        try
        {
            AdaptationStateFile.Save(path, new AdaptationState(
                AdaptationState.CurrentVersion, DateTimeOffset.UtcNow,
                new Dictionary<AdaptationKnob, double>
                {
                    [AdaptationKnob.DwellTime] = 0.2,
                    [AdaptationKnob.Smoothing] = 0.1,
                }));
            var (learner, provider) = Make(path);

            learner.Load();

            var entries = provider.EnumerateLearnedFills();
            Check.That(entries).HasSize(2);
            Check.That(entries.Single(e => e.Knob == AdaptationKnob.DwellTime).Fill).IsCloseTo(0.2, 1e-9);
            Check.That(entries.Single(e => e.Knob == AdaptationKnob.Smoothing).Fill).IsCloseTo(0.1, 1e-9);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Write the failing state-file tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationStateFileTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class AdaptationStateFileTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "eyectrl_state_test_" + Guid.NewGuid() + ".json");

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        string path = TempPath();
        try
        {
            var saved = new AdaptationState(
                AdaptationState.CurrentVersion, DateTimeOffset.UtcNow,
                new Dictionary<AdaptationKnob, double> { [AdaptationKnob.DwellTime] = 0.25 });
            AdaptationStateFile.Save(path, saved);

            var loaded = AdaptationStateFile.Load(path);
            Check.That(loaded.Version).IsEqualTo(AdaptationState.CurrentVersion);
            Check.That(loaded.Learned[AdaptationKnob.DwellTime]).IsCloseTo(0.25, 1e-9);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        var loaded = AdaptationStateFile.Load(TempPath()); // never written
        Check.That(loaded.Learned).IsEmpty();
    }

    [Fact]
    public void CorruptJson_LoadsEmpty_NeverThrows()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Check.That(AdaptationStateFile.Load(path).Learned).IsEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OutOfRangeEntry_IsDropped_OthersSurvive()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                { "version": 1, "savedAtUtc": "2026-09-04T00:00:00Z",
                  "learned": { "dwellTime": 0.3, "fixationRadius": 7.0 } }
                """);
            var loaded = AdaptationStateFile.Load(path);
            Check.That(loaded.Learned.ContainsKey(AdaptationKnob.DwellTime)).IsTrue();
            Check.That(loaded.Learned.ContainsKey(AdaptationKnob.FixationRadius)).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownKnobName_IsIgnored()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                { "version": 1, "savedAtUtc": "2026-09-04T00:00:00Z",
                  "learned": { "dwellTime": 0.3, "bogus": 0.4 } }
                """);
            var loaded = AdaptationStateFile.Load(path);
            Check.That(loaded.Learned).HasSize(1);
            Check.That(loaded.Learned.ContainsKey(AdaptationKnob.DwellTime)).IsTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WrongVersion_LoadsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                { "version": 99, "savedAtUtc": "2026-09-04T00:00:00Z",
                  "learned": { "dwellTime": 0.3 } }
                """);
            Check.That(AdaptationStateFile.Load(path).Learned).IsEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NegativeEntry_IsDropped()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                { "version": 1, "savedAtUtc": "2026-09-04T00:00:00Z",
                  "learned": { "dwellTime": -0.2 } }
                """);
            Check.That(AdaptationStateFile.Load(path).Learned).IsEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_IsAtomic_CreatesDirectory_LeavesNoTemp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eyectrl_state_test_" + Guid.NewGuid());
        string path = Path.Combine(dir, "sub", "adaptation-state.json");
        try
        {
            AdaptationStateFile.Save(path, AdaptationState.Empty);
            Check.That(File.Exists(path)).IsTrue();
            Check.That(File.Exists(path + ".tmp")).IsFalse();
        }
        finally { Directory.Delete(dir, recursive: true); } // the GUID dir itself — NOT its parent (= %TEMP%)
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: **Fehler!** — compile errors (`AdaptationLearner`/`AdaptationState`/`AdaptationStateFile` do not exist).

- [ ] **Step 4: Implement the three files**

Create `src/EyeCtrl.Core/Adaptation/AdaptationState.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;
using System;
using System.Collections.Generic;

/// <summary>
/// The persisted Phase-2 learned state (design §7.4): one forgiving band-fill fraction per knob,
/// global in v1 (per-context learned is a future extension — the store is keyed by knob alone).
/// Validated per entry on load; <see cref="AdaptationStateFile"/> drops invalid entries and keeps
/// the survivors (the ClampGazeIndicator precedent at file granularity).
/// </summary>
/// <param name="Version">Schema version; a mismatch with <see cref="CurrentVersion"/> resets the state.</param>
/// <param name="SavedAtUtc">When the state was written (diagnostic only — never used for expiry).</param>
/// <param name="Learned">Per-knob learned band fills, each in <c>[0, <see cref="AdaptationSettings.LearnedMaxFill"/>]</c>.</param>
public sealed record AdaptationState(
    int Version,
    DateTimeOffset SavedAtUtc,
    IReadOnlyDictionary<AdaptationKnob, double> Learned)
{
    /// <summary>The schema version this build writes and accepts.</summary>
    public const int CurrentVersion = 1;

    /// <summary>An empty state — no learned offsets (fresh install, or rejected file).</summary>
    public static AdaptationState Empty { get; } = new(CurrentVersion, DateTimeOffset.MinValue, new Dictionary<AdaptationKnob, double>());

    /// <summary>Whether a persisted learned value is in-band (finite, forgiving-only, ≤ the cap).</summary>
    public static bool IsValueValid(double value) =>
        double.IsFinite(value) && value >= 0.0 && value <= AdaptationSettings.LearnedMaxFill;
}
```

Create `src/EyeCtrl.Core/Adaptation/AdaptationStateFile.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Load/save for <see cref="AdaptationState"/> at <c>%APPDATA%/EyeCtrl/adaptation-state.json</c> (§7.4):
/// atomic temp-file + rename on write (the <c>SettingsService.Save</c> pattern, minus its cross-process
/// mutex — the app is the single writer; the supervisor does not write this file, §14), validated on
/// load (the <c>CalibrationService.Load</c> pattern): corrupt JSON, a version mismatch, or a missing
/// file yield <see cref="AdaptationState.Empty"/>; individually out-of-band entries are dropped while
/// valid entries survive. Never throws — a rejected file means "no learned offsets", not a broken app.
/// </summary>
public static class AdaptationStateFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Case-insensitive read (review round 1, finding 2): Save writes camelCase property names and
    // System.Text.Json matches case-SENSITIVELY by default, so without this a file this build just
    // wrote would deserialize Version to 0 and be rejected as a version mismatch — persistence dead
    // behind green-enough tests. Same fix as HeartbeatPipeServer / CalibrationService's s_readOptions.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class StateDto
    {
        public int Version { get; set; }
        public DateTimeOffset SavedAtUtc { get; set; }
        public Dictionary<string, double> Learned { get; set; } = new();
    }

    /// <summary>Loads and validates the state file. Any failure degrades to <see cref="AdaptationState.Empty"/>.</summary>
    /// <param name="path">Full path to the state file.</param>
    /// <param name="logger">Optional logger for rejected-file diagnostics.</param>
    public static AdaptationState Load(string path, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(path))
                return AdaptationState.Empty;

            var dto = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(path), ReadOptions);
            if (dto is null || dto.Version != AdaptationState.CurrentVersion)
            {
                logger?.LogWarning(
                    "Adaptation state from {FilePath} is absent, unreadable, or a different schema version; ignoring (learned offsets start at zero)",
                    path);
                return AdaptationState.Empty;
            }

            var learned = new Dictionary<AdaptationKnob, double>();
            foreach (var (name, value) in dto.Learned)
            {
                if (!Enum.TryParse<AdaptationKnob>(name, ignoreCase: true, out var knob))
                    continue; // a knob this build no longer knows (or never did) — ignore, keep the rest
                if (!AdaptationState.IsValueValid(value))
                    continue; // out-of-band entry dropped, survivors kept (§7.4 per-entry recovery)
                learned[knob] = value;
            }
            return new AdaptationState(dto.Version, dto.SavedAtUtc, learned);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger?.LogWarning(ex, "Failed to load adaptation state from {FilePath}; ignoring (learned offsets start at zero)", path);
            return AdaptationState.Empty;
        }
    }

    /// <summary>Persists the state atomically (temp file + rename). Throws only on I/O failure; the caller owns the best-effort catch.</summary>
    public static void Save(string path, AdaptationState state)
    {
        var dto = new StateDto { Version = state.Version, SavedAtUtc = state.SavedAtUtc };
        foreach (var (knob, fill) in state.Learned)
            dto.Learned[JsonNamingPolicy.CamelCase.ConvertName(knob.ToString())] = fill;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(dto, WriteOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
```

Create `src/EyeCtrl.Core/Adaptation/AdaptationLearner.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The Phase-2 learned layer (design §7.4): a slow, forgiving-only EWMA over the momentary layer's
/// forgiving band fills. One mechanism both accumulates (sustained momentary relaxation ⇒ the offset
/// creeps up, so the momentary layer starts from a partially-relaxed state) and decays toward baseline
/// (observed fill 0 ⇒ the offset shrinks — §8.7: the learned layer must continuously re-earn itself).
/// The observed value is the controller's <b>post-hysteresis</b> fill, so the promotion criterion is
/// the EWMA time constant applied to already-gated, already-sustained evidence — no second confidence
/// machinery. Ticked by the <c>AdaptationCoordinator</c> on assess ticks only: paused, disconnected,
/// or disabled stretches freeze the state (no sample ⇒ no decay — no offline aging either, decision
/// D9). A disabled per-knob <c>Adapt*</c> toggle zeroes that knob's learned offset at the next sample
/// — an off switch must stop adapting at once, not decay for 40 minutes (review round 1, finding 8).
/// Persisted via <see cref="AdaptationStateFile"/>; cleared by <see cref="ResetLearned"/> (the manual
/// escape and the settings action — never by session boundaries, §13.1). Single-threaded (dispatcher).
/// </summary>
public sealed class AdaptationLearner
{
    /// <summary>Wall-clock spacing between EWMA samples. 30 s — with <see cref="Alpha"/> 0.05 this is a
    /// time constant of ≈ 10 minutes: full-band momentary struggle learns to the 0.5 cap in ≈ 9 minutes,
    /// and calm decays a cap-filled offset below the push epsilon in ≈ 40 minutes. Starting value, tunable.</summary>
    public const long SampleIntervalMicroseconds = 30_000_000L;

    /// <summary>EWMA weight of the newest observation. 0.05. Starting value, tunable.</summary>
    public const double Alpha = 0.05;

    /// <summary>Minimum movement since the last push before a new value is written to the provider —
    /// the §7.4 rate limit (and the §9.1 budget guard: learned pushes are ≥ 30 s apart per knob, far
    /// inside the smoothing rebuild budget). 0.01. Starting value, tunable.</summary>
    public const double PushEpsilon = 0.01;

    // "Not yet sampled." long.MinValue, NOT 0 — gaze timestamps carry an arbitrary epoch and 0 is a
    // legitimate value (the Unseeded discipline; see AdaptationCoordinator._latestGazeTs).
    private const long Unsampled = long.MinValue;

    private static readonly AdaptationKnob[] Knobs =
    {
        AdaptationKnob.DwellTime,
        AdaptationKnob.FixationRadius,
        AdaptationKnob.Smoothing,
        AdaptationKnob.TargetGeometry,
    };

    private readonly EffectiveSettingsProvider _provider;
    private readonly AdaptationSettings _adaptation;
    private readonly ILogger<AdaptationLearner>? _logger;
    private readonly string? _stateFilePath; // null = no persistence (unit tests)
    private readonly Dictionary<AdaptationKnob, double> _learned = new();
    private readonly Dictionary<AdaptationKnob, double> _pushed = new();
    private long _lastSampleAt = Unsampled;

    /// <summary>Initializes the learner over the provider it observes and pushes into.</summary>
    /// <param name="provider">The effective-settings provider — read for momentary fills, written with learned fills.</param>
    /// <param name="adaptation">Adaptation settings — the per-knob <c>Adapt*</c> toggles zero a knob's learned offset immediately when off.</param>
    /// <param name="logger">Optional logger; persistence failures are swallowed after logging (§11).</param>
    /// <param name="stateFilePath">Full path of <c>adaptation-state.json</c>, or null to disable persistence.</param>
    public AdaptationLearner(
        EffectiveSettingsProvider provider,
        AdaptationSettings adaptation,
        ILogger<AdaptationLearner>? logger = null,
        string? stateFilePath = null)
    {
        _provider = provider;
        _adaptation = adaptation;
        _logger = logger;
        _stateFilePath = stateFilePath;
    }

    /// <summary>Loads the persisted state (if any) and applies its learned fills to the provider directly,
    /// bypassing <see cref="PushEpsilon"/> (persisted values were already rate-limited when they were earned).
    /// Clears the provider's learned store first, so a reload can never leave stale entries for knobs the
    /// file no longer mentions.</summary>
    public void Load()
    {
        _learned.Clear();
        _pushed.Clear();
        _provider.ClearLearnedFills();
        if (_stateFilePath is null)
            return;
        var state = AdaptationStateFile.Load(_stateFilePath, _logger);
        foreach (var (knob, fill) in state.Learned)
        {
            _learned[knob] = fill;
            _pushed[knob] = fill;
            if (fill != 0.0)
                _provider.SetLearnedFill(knob, fill);
        }
    }

    /// <summary>
    /// Advances one tick: samples the momentary forgiving fills (per knob, at <see cref="DwellContext.Other"/>
    /// — the global momentary layer) no more often than <see cref="SampleIntervalMicroseconds"/>, steps each
    /// knob's learned EWMA toward its observation (or zeroes it outright when its per-knob toggle is off),
    /// clamps to <c>[0, <see cref="AdaptationSettings.LearnedMaxFill"/>]</c>, and pushes to the provider only
    /// when a knob moved ≥ <see cref="PushEpsilon"/> since its last push.
    /// </summary>
    /// <param name="nowMicroseconds">The current tick timestamp, in microseconds (the gaze clock).</param>
    public void Tick(long nowMicroseconds)
    {
        // Gaze-clock restart (review round 1, finding 7): `now` is the coordinator's latest GAZE timestamp,
        // and a tracker reconnect can restart that clock near 0. Waiting on a due time would then block
        // sampling for the old epoch's uptime — the silent-degradation class StruggleMonitor and GazeEngine
        // already guard. Resync on any backward jump and treat it as the first sample of a new epoch.
        if (nowMicroseconds < _lastSampleAt)
            _lastSampleAt = Unsampled;
        if (_lastSampleAt != Unsampled && nowMicroseconds - _lastSampleAt < SampleIntervalMicroseconds)
            return;
        _lastSampleAt = nowMicroseconds;

        foreach (var knob in Knobs)
        {
            double observed = _provider.TryGetMomentaryFill(knob, DwellContext.Other, out double forgiving, out _)
                ? forgiving
                : 0.0; // the tightening component is deliberately invisible — learned is forgiving-only (§7.4)
            double value = _learned.TryGetValue(knob, out double l) ? l : 0.0;
            // A disabled per-knob toggle zeroes the learned offset outright (finding 8): the off switch
            // must stop adapting at once. The EWMA path runs only for enabled knobs.
            value = IsKnobEnabled(knob) ? value + Alpha * (observed - value) : 0.0;
            value = Math.Clamp(value, 0.0, AdaptationSettings.LearnedMaxFill);
            _learned[knob] = value;

            double pushed = _pushed.TryGetValue(knob, out double p) ? p : 0.0;
            if (Math.Abs(value - pushed) >= PushEpsilon)
            {
                _provider.SetLearnedFill(knob, value);
                _pushed[knob] = value;
            }
        }
    }

    private bool IsKnobEnabled(AdaptationKnob knob) => knob switch
    {
        AdaptationKnob.DwellTime => _adaptation.AdaptDwell,
        AdaptationKnob.FixationRadius => _adaptation.AdaptTolerance,
        AdaptationKnob.Smoothing => _adaptation.AdaptSmoothing,
        AdaptationKnob.TargetGeometry => _adaptation.AdaptGeometry,
        _ => true,
    };

    /// <summary>Zeroes every learned offset, clears the provider, and persists the zeroed state — the manual
    /// escape's learned half and the settings-page action (§10). Idempotent. The persist is deliberately
    /// synchronous: the reset's durability matters more than milliseconds on this path (a lost reset would
    /// resurrect cleared offsets on the next start), the write is one small JSON file, and every I/O failure
    /// is caught below — the escape itself cannot fail (review round 1, finding 16).</summary>
    public void ResetLearned()
    {
        _learned.Clear();
        _pushed.Clear();
        _provider.ClearLearnedFills();
        Persist();
    }

    /// <summary>Persists the current learned state (all four knobs, zeros included — a stable schema).
    /// Best-effort: an I/O failure logs and is swallowed (§11 — a comfort feature must never end the session).</summary>
    public void Persist()
    {
        if (_stateFilePath is null)
            return;
        try
        {
            AdaptationStateFile.Save(_stateFilePath, new AdaptationState(
                AdaptationState.CurrentVersion, DateTimeOffset.UtcNow, Snapshot().ToDictionary(e => e.Knob, e => e.Fill)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex, "Failed to persist adaptation state to {FilePath}; learned offsets continue in memory", _stateFilePath);
        }
    }

    /// <summary>The current learned fills for all knobs (zeros included), for diagnostics and persistence.</summary>
    public IReadOnlyList<(AdaptationKnob Knob, double Fill)> Snapshot() =>
        Knobs.Select(k => (k, _learned.TryGetValue(k, out double v) ? v : 0.0)).ToList();
}
```

- [ ] **Step 5: Run the suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: `Bestanden!` with `erfolgreich: 1193, übersprungen: 1, gesamt: 1194` (12 learner facts + 8 state-file facts on top of Task 2's 1174).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/AdaptationState.cs src/EyeCtrl.Core/Adaptation/AdaptationStateFile.cs src/EyeCtrl.Core/Adaptation/AdaptationLearner.cs tests/EyeCtrl.Core.Tests/Adaptation/AdaptationLearnerTests.cs tests/EyeCtrl.Core.Tests/Adaptation/AdaptationStateFileTests.cs
git commit -m "feat(adaptation): EWMA learned layer with validated atomic persistence"
```

---

### Task 4: App+VM — the reset fork, the learner tick, and the learned-reset message

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (+1 record beside `ResetAdaptationMessage` at line ~124)
- Modify: `src/EyeCtrl.App/Services/AdaptationCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/AdaptationActivation.cs` (1 line)
- Modify: `src/EyeCtrl.App/Services/AdaptationResetHandler.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (learner + coordinator + handler registrations — round-2 F1: without these the App suite cannot compile after the ctor changes, so they belong in THIS task, not Task 5)
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationCoordinatorTests.cs` (Make + 4 facts)
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationResetHandlerTests.cs` (Make + 2 facts + the two existing facts' ctor calls — round-2 F2)
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationRuntimeContainmentTests.cs` (compile fix only — review round 1, finding 5: the fourth coordinator construction site)
- Test: `tests/EyeCtrl.App.Tests/Services/TelemetrySinkRegistrationTests.cs` (+1 registration fact — moved here from Task 5 by round-2 F1, so it keeps its red-green cycle)

**Interfaces:**
- Consumes (Task 3): `AdaptationLearner` ctor/`Tick`/`ResetLearned`.
- Produces (Tasks 5-6): `AdaptationCoordinator(monitor, controller, learner, adaptation, gazeProvider, interactionManager)` — learner is the **third** parameter; `AdaptationCoordinator.ResetMomentary()`; `AdaptationResetHandler(messenger, coordinator, learner)`; `ResetLearnedOffsetsMessage` (ViewModels.Messages).

- [ ] **Step 1: Add the message**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, directly under the existing `public record ResetAdaptationMessage;`:

```csharp
/// <summary>Clears ONLY the Phase-2 learned offsets (settings-page action, §10 <c>ResetLearnedBaseline</c>);
/// momentary adaptation is untouched — unlike <see cref="ResetAdaptationMessage"/>, the full §8.6 escape.</summary>
public record ResetLearnedOffsetsMessage;
```

(`ResetAdaptationMessage` itself carries a `///` doc comment — verified — so the new record's doc matches the file's style. CS1591-as-error is Core-only; ViewModels documents messages by convention.)

- [ ] **Step 2: Write the failing coordinator tests**

In `tests/EyeCtrl.App.Tests/Services/AdaptationCoordinatorTests.cs`: add `using System;` and `using System.IO;` to the usings, extend `Make` (create the learner with a temp path and thread it into the coordinator; return it and the path):

```csharp
    private static (AdaptationCoordinator Coord, EffectiveSettingsProvider Provider, BufferingTelemetrySink Sink,
                    AdaptationSettings Adaptation, TestFakes.FakeGazeProvider Gaze, InteractionManager Im,
                    AdaptationLearner Learner, string StatePath) Make(bool enabled)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = enabled;
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        string statePath = Path.Combine(Path.GetTempPath(), "eyectrl_learner_coord_test_" + Guid.NewGuid() + ".json");
        var learner = new AdaptationLearner(provider, settings.Adaptation, stateFilePath: statePath);
        var gaze = new TestFakes.FakeGazeProvider();
        var im = new InteractionManager();
        var coord = new AdaptationCoordinator(monitor, controller, learner, settings.Adaptation, gaze, im);
        return (coord, provider, sink, settings.Adaptation, gaze, im, learner, statePath);
    }
```

Deconstruction in the existing 10 facts changes only where they discard the new elements — update each `var (c, _, _, _, _, _) = Make(...)` to `var (c, _, _, _, _, _, _, _) = Make(...)` (and the six-element forms accordingly). **These are mechanical arity fixes, not behavior changes — the 10 existing facts' assertions stay byte-identical.**

**Also fix the fourth construction site** (review round 1, finding 5 — the plan's own impact analysis listed it at depth 1 and the first draft missed it): in `tests/EyeCtrl.App.Tests/Services/AdaptationRuntimeContainmentTests.cs`, `NewCoordinator` gains the learner as its third argument. No fact changes — it is never ticked there:

```csharp
    private static AdaptationCoordinator NewCoordinator(AdaptationSettings adaptation)
    {
        var settings = new EyeCtrlSettings();
        var sink = new BufferingTelemetrySink();
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(adaptation);
        var controller = new AdaptationController(provider, adaptation);
        return new AdaptationCoordinator(
            monitor, controller, new AdaptationLearner(provider, adaptation), adaptation,
            new TestFakes.FakeGazeProvider(), new InteractionManager());
    }
```

Append the four new facts (the drive uses 7 abandons so the ≈4-attempt gate still passes at t=73 s, and two learner samples 30 s apart so the second push clears ε — see Context fact 5):

```csharp
    // Sample cadence note (review round 1, finding 9 — the first draft's inline reasoning was wrong):
    // the learner samples on the FIRST assess tick (no seed-only step) and then every ≥ 30 s. So the
    // sample points here are t=40 (observes fill 0 — the controller has only anchored relaxSince and
    // pushed nothing yet), t=73 (observes fill 0.2 → learned 0.01, a knife-edge ε margin), and t=103
    // (observes fill 0.3 → learned ≈ 0.0245, a solid push). The 103 s tick exists to get the assertion
    // off the knife edge; do not remove it. The abandon stream runs through t=90 (nine reports): the
    // 60 s evidence max-age means a stream ending at 70 would leave only 3 in-window attempts at
    // t=103, failing the ≈4-attempt gate and *recovering* the fill instead of stepping it to 0.3.
    private static void DriveToLearnedOffset(AdaptationCoordinator c, BufferingTelemetrySink sink)
    {
        c.Tick(); // ResetOnEnable
        for (int i = 0; i < 9; i++)
            sink.Report(Abandon((i + 1) * 10 * Sec, progress: 0.8)); // gate SustainFailure through t=90 s
        c.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true)); c.Tick();  // anchor relaxSince; learner sample 1 (observes 0)
        c.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true)); c.Tick();  // 3 s sustain → fill 0.1 (within cadence — no sample)
        c.OnGazeSample(new GazePoint(0, 0, 73 * Sec, true)); c.Tick();  // fill 0.2; learner sample 2 (0.01 — marginal)
        c.OnGazeSample(new GazePoint(0, 0, 103 * Sec, true)); c.Tick(); // fill 0.3; learner sample 3 → solid push (≥ ε)
    }

    [Fact]
    public void ActiveTicks_WithSustainedStruggle_AccumulateLearnedOffset()
    {
        var (c, provider, sink, _, _, _, _, _) = Make(enabled: true);
        DriveToLearnedOffset(c, sink);
        Check.That(provider.EnumerateLearnedFills()).Not.IsEmpty();
    }

    [Fact]
    public void ManualReset_ClearsLearned_AndPersistsZeroState()
    {
        var (c, provider, sink, _, _, _, _, statePath) = Make(enabled: true);
        try
        {
            DriveToLearnedOffset(c, sink);
            Check.That(provider.EnumerateLearnedFills()).Not.IsEmpty();

            c.Reset(); // the manual §8.6 escape: momentary AND learned

            Check.That(provider.EnumerateLearnedFills()).IsEmpty();
            Check.That(provider.TryGetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, out _, out _)).IsFalse(); // momentary store cleared
            Check.That(AdaptationStateFile.Load(statePath).Learned.Values).ContainsOnlyElementsThatMatch(v => v == 0.0);
        }
        finally { File.Delete(statePath); }
    }

    [Fact]
    public void SessionEdgeResets_PreserveLearnedOffsets()
    {
        var (c, provider, sink, _, gaze, _, _, statePath) = Make(enabled: true);
        try
        {
            DriveToLearnedOffset(c, sink);
            gaze.IsConnected = false;
            Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnDisconnect);
            // §13.1: learned persists across session boundaries — only momentary evidence resets.
            Check.That(provider.EnumerateLearnedFills()).Not.IsEmpty();
            Check.That(provider.TryGetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, out _, out _)).IsFalse();
        }
        finally { File.Delete(statePath); }
    }

    [Fact]
    public void DisableEdge_PreservesLearnedOffsets()
    {
        var (c, provider, sink, adaptation, _, _, _, statePath) = Make(enabled: true);
        try
        {
            DriveToLearnedOffset(c, sink);
            adaptation.Enabled = false;
            Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ClearedOnDisable);
            // Learned survives disable inertly: Effective gates on Enabled first (§8.3/§9.1).
            Check.That(provider.EnumerateLearnedFills()).Not.IsEmpty();
            Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50.0)).IsEqualTo(50.0);
        }
        finally { File.Delete(statePath); }
    }
```

Also append the **registration fact** to `tests/EyeCtrl.App.Tests/Services/TelemetrySinkRegistrationTests.cs` (moved here from Task 5 by round-2 F1 — it belongs beside the DI edits it guards, and this keeps its red-green cycle intact):

```csharp
    [Fact]
    public void AdaptationLearner_Resolves_OverTheSharedProvider()
    {
        using var sp = BuildProvider();

        var learner = sp.GetService<AdaptationLearner>();
        var provider = sp.GetService<EffectiveSettingsProvider>();

        Check.That(learner).IsNotNull();
        Check.That(provider).IsNotNull();
        // A dropped registration would degrade to silently never learning — same failure class as the sinks.
    }
```

- [ ] **Step 3: Write the failing reset-handler tests**

In `tests/EyeCtrl.App.Tests/Services/AdaptationResetHandlerTests.cs`, extend `Make` (learner with a temp path, threaded into the handler; return the adaptation and the state path for cleanup):

```csharp
    private static (AdaptationCoordinator Coord, EffectiveSettingsProvider Provider, BufferingTelemetrySink Sink,
                    AdaptationSettings Adaptation, string StatePath) Make()
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        string statePath = Path.Combine(Path.GetTempPath(), "eyectrl_learner_reset_test_" + Guid.NewGuid() + ".json");
        var learner = new AdaptationLearner(provider, settings.Adaptation, stateFilePath: statePath);
        var coord = new AdaptationCoordinator(monitor, controller, learner, settings.Adaptation, new TestFakes.FakeGazeProvider(), new InteractionManager());
        return (coord, provider, sink, settings.Adaptation, statePath);
    }
```

(The two existing facts' destructuring gains two elements; assertions unchanged. Add `using System;` / `using System.IO;`.) **Their handler constructions change too** (round-2 F2 — extending `Make` alone leaves both existing facts calling the 2-arg ctor, a compile error):

```csharp
        var (coord, provider, sink, adaptation, _) = Make();
        using var handler = new AdaptationResetHandler(messenger, coord, new AdaptationLearner(provider, adaptation));
```

(Both existing facts take this shape — capture `adaptation` from the extended `Make` and pass the learner as the third argument.) Append:

```csharp
    [Fact]
    public void LearnedResetMessage_ClearsLearnedOffsetsOnly()
    {
        var (coord, provider, sink, adaptation, statePath) = Make();
        try
        {
            DriveToRelaxedTolerance(coord, sink);
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.3);
            var messenger = new WeakReferenceMessenger();
            using var handler = new AdaptationResetHandler(messenger, coord, learner: new AdaptationLearner(provider, adaptation));
            handler.Start();

            messenger.Send(new ResetLearnedOffsetsMessage());

            Check.That(provider.EnumerateLearnedFills()).IsEmpty();
            // Learned-only: the momentary relaxation is untouched (unlike ResetAdaptationMessage).
            Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();
        }
        finally { File.Delete(statePath); }
    }

    [Fact]
    public void LearnedResetMessage_AfterDispose_IsIgnored()
    {
        var (coord, provider, _, adaptation, statePath) = Make();
        try
        {
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.3);
            var messenger = new WeakReferenceMessenger();
            var handler = new AdaptationResetHandler(messenger, coord, learner: new AdaptationLearner(provider, adaptation));
            handler.Start();
            handler.Dispose();

            messenger.Send(new ResetLearnedOffsetsMessage());

            Check.That(provider.EnumerateLearnedFills()).HasSize(1);
        }
        finally { File.Delete(statePath); }
    }
```

Note the handler facts construct a **second** path-less learner for the handler itself — the message must clear the *handler's* learner, and here both learners share the provider, which is what the assertions read. (The coordinator's learner inside `Make` exists only to satisfy the constructor; it is not ticked in these tests.)

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: **Fehler!** — compile errors (coordinator/handler constructors take 5 args; `ResetLearnedOffsetsMessage` missing).

- [ ] **Step 5: Implement**

`src/EyeCtrl.App/Services/AdaptationCoordinator.cs`:

(a) Add the field + ctor parameter (third position) + assignment:

```csharp
    private readonly AdaptationLearner _learner;
```

```csharp
    /// <param name="learner">The Phase-2 learned layer, ticked after the controller on each assess tick (§7.4).</param>
    public AdaptationCoordinator(
        StruggleMonitor monitor,
        AdaptationController controller,
        AdaptationLearner learner,
        AdaptationSettings adaptation,
        IGazeProvider gazeProvider,
        InteractionManager interactionManager)
    {
        _monitor = monitor;
        _controller = controller;
        _learner = learner;
        _adaptation = adaptation;
        _gazeProvider = gazeProvider;
        _interactionManager = interactionManager;
    }
```

(b) In `Tick`'s assess branch, tick the learner after the controller:

```csharp
        StruggleAssessment assessment = _monitor.Assess(now);
        _controller.Tick(assessment, now);
        _learner.Tick(now); // assess ticks only — paused/disconnected stretches freeze the learned state
        LastAssessment = assessment;
        return CoordinatorTickResult.Assessed;
```

(c) Fork the reset paths — replace `Reset()`/`ResetInternal` with:

```csharp
    /// <summary>
    /// The manual always-reachable escape (§8.6): clears momentary AND learned adaptation (§10) and starts
    /// a fresh evidence session. Invoked by the toolbar/tray reset message (wired via <c>AdaptationResetHandler</c>).
    /// </summary>
    public void Reset() => ResetInternal(Volatile.Read(ref _latestGazeTs), clearLearned: true);

    /// <summary>
    /// Clears momentary adaptation only, preserving the learned offsets (§13.1 — they persist across
    /// session boundaries and survive disable inertly). The disable-edge path used by
    /// <c>AdaptationActivation.StopRuntime</c>, where reusing <see cref="Reset"/> would wipe learning on
    /// every settings toggle.
    /// </summary>
    public void ResetMomentary() => ResetInternal(Volatile.Read(ref _latestGazeTs), clearLearned: false);

    // Moves the monitor's session boundary to `now', clears the controller's momentary deltas, and — only
    // for the manual escape — clears the learned layer too (which also persists the zeroed state).
    private void ResetInternal(long now, bool clearLearned)
    {
        _monitor.ResetSession(now);
        _controller.Reset();
        if (clearLearned)
            _learner.ResetLearned();
        LastAssessment = StruggleAssessment.None;
    }
```

The two existing `ResetInternal(now)` call sites inside `Tick` both pass `clearLearned: false` — every reset *inside* `Tick` is a session/disable edge, never the manual escape. Show both edits explicitly (review round 1, finding 20):

```csharp
        if (!_adaptation.Enabled)
        {
            if (_wasEnabled)
            {
                _wasEnabled = false;
                ResetInternal(Volatile.Read(ref _latestGazeTs), clearLearned: false);
                return CoordinatorTickResult.ClearedOnDisable;
            }
            return CoordinatorTickResult.Disabled;
        }
```

```csharp
        if (reset is { } reason)
        {
            ResetInternal(now, clearLearned: false);
            return reason;
        }
```

`src/EyeCtrl.App/Services/AdaptationActivation.cs` — in `StopRuntime`, replace:

```csharp
        var coordinator = services.GetRequiredService<AdaptationCoordinator>();
        coordinator.Reset();
```

with:

```csharp
        var coordinator = services.GetRequiredService<AdaptationCoordinator>();
        // Momentary only — the learned offsets survive the disable and resume when it is toggled back on
        // (§7.4/§13.1; they are inert while disabled because Effective gates on Enabled first).
        coordinator.ResetMomentary();
```

`src/EyeCtrl.App/Services/AdaptationResetHandler.cs` — add the learner field/param and the second registration:

```csharp
    private readonly AdaptationLearner _learner;
```

```csharp
    /// <param name="learner">The learned layer the learned-only reset message clears (§10 ResetLearnedBaseline).</param>
    public AdaptationResetHandler(IMessenger messenger, AdaptationCoordinator coordinator, AdaptationLearner learner)
    {
        _messenger = messenger;
        _coordinator = coordinator;
        _learner = learner;
    }

    /// <summary>Registers both reset-message subscriptions. Call once at startup.</summary>
    public void Start()
    {
        _messenger.Register<AdaptationResetHandler, ResetAdaptationMessage>(this, static (h, _) => h._coordinator.Reset());
        _messenger.Register<AdaptationResetHandler, ResetLearnedOffsetsMessage>(this, static (h, _) => h._learner.ResetLearned());
    }
```

(Update the class doc comment's first sentence to mention both messages.)

(d) **Update the DI registrations in `src/EyeCtrl.App/Services/AppBootstrapper.cs`** (round-2 F1 — these moved here from Task 5 because the ctor changes above break this file's compile, and Task 4's test run needs it green):

```csharp
        // Phase-2 learned layer (§7.4): EWMA over the controller's momentary fills, pushed into the
        // provider; ticked by the coordinator on assess ticks. Load()/Persist() are called by App
        // (startup/exit, Task 5) — DI construction itself performs no I/O.
        services.AddSingleton(sp => new AdaptationLearner(
            sp.GetRequiredService<EffectiveSettingsProvider>(),
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<ILogger<AdaptationLearner>>(),
            Path.Combine(appDataDir, "adaptation-state.json")));
        services.AddSingleton(sp => new AdaptationCoordinator(
            sp.GetRequiredService<StruggleMonitor>(),
            sp.GetRequiredService<AdaptationController>(),
            sp.GetRequiredService<AdaptationLearner>(),
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IGazeProvider>(),
            sp.GetRequiredService<GazeEngine>().InteractionManager));
```

```csharp
        services.AddSingleton(sp => new AdaptationResetHandler(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<AdaptationCoordinator>(),
            sp.GetRequiredService<AdaptationLearner>()));
```

- [ ] **Step 6: Run the suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Bestanden!` with `gesamt: 749` (4 coordinator facts + 2 handler facts + the registration fact, with the bootstrapper compile fixed in Step 5d). (Run the whole App project, never `ExternalDwellServiceTests` alone.)

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.App/Services/AdaptationCoordinator.cs src/EyeCtrl.App/Services/AdaptationActivation.cs src/EyeCtrl.App/Services/AdaptationResetHandler.cs src/EyeCtrl.App/Services/AppBootstrapper.cs tests/EyeCtrl.App.Tests/Services/AdaptationCoordinatorTests.cs tests/EyeCtrl.App.Tests/Services/AdaptationResetHandlerTests.cs tests/EyeCtrl.App.Tests/Services/AdaptationRuntimeContainmentTests.cs tests/EyeCtrl.App.Tests/Services/TelemetrySinkRegistrationTests.cs
git commit -m "feat(adaptation): learner tick wiring and the manual/session reset fork"
```

---

### Task 5: App — DI registration, startup load, exit persist

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (stale provider comment only — the registrations moved to Task 4, round-2 F1)
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Interfaces:**
- Consumes (Tasks 3-4): the `AdaptationLearner` constructor and DI registration (Task 4, round-2 F1).
- Produces: `Load()` called once at startup before the adaptation runtime activates; `Persist()` called at exit beside the settings save. **No test changes in this task** — the registrations and their guard fact landed in Task 4 (round-2 F1); this task is App wiring only, verified by the unchanged suite count and the build.

- [ ] **Step 1: Implement**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`:

(a) Fix the stale provider comment at ~lines 147-149 — replace "Holds no deltas until the AdaptationController lands, and returns baseline while AdaptationSettings.Enabled == false." with "Holds no deltas while adaptation is disabled or idle, and returns baseline while AdaptationSettings.Enabled == false; Phase 2 adds the learned fills beside the momentary ones." (Leftover (d); comment only.)

In `src/EyeCtrl.App/App.xaml.cs`:

(b) At the activation block (~line 340-345), load the learned state **before** the runtime starts, gated on the master switch (review round 1, finding 18 — §9.1 "disabled = current cost": an unconditional `Load()` would read the file and fan a `Changed` into every eagerly-resolved router while the feature is off). Keep it in `OnStartup`, **not** in `AdaptationActivation.StartRuntime` — a disable→enable cycle must not resurrect the stale on-disk state over the in-memory decay (the disable path deliberately does not persist), so a mid-session first enable starts learned at 0:

```csharp
            // Struggle-detection adaptation. AdaptationActivation starts and stops the whole runtime —
            // …(keep the existing comment)…
            // §7.4 persisted learned offsets — only read when the feature is on (§9.1 disabled = current
            // cost); a mid-session first enable then starts learned at zero, conservatively.
            if (settings.Adaptation.Enabled)
                _serviceProvider.GetRequiredService<AdaptationLearner>().Load();
            _serviceProvider.GetRequiredService<AdaptationActivation>().Start(gazeEngine);
```

(c) In `OnExit`, persist beside the settings save (after `settingsService.Save(settings);` at ~line 409):

```csharp
            _serviceProvider.GetRequiredService<AdaptationLearner>().Persist(); // best-effort (§11); never throws
```

- [ ] **Step 2: Run the suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Bestanden!` with `gesamt: 749` — **unchanged from Task 4** (this task adds no tests; a change here means the wiring broke something).

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(adaptation): DI wiring, startup load, exit persist for the learned layer"
```

---

### Task 6: App — the adaptation log's JSONL review file and the two page buttons

**Files:**
- Modify: `src/EyeCtrl.App/Services/AdaptationLog.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (the `AdaptationLog` registration gains provider + path — review round 1, finding 6)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (1 ctor arg + 1 method)
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs` (Make + 7 facts)
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs` (1 fact modified, 2 added)
- Test: `tests/EyeCtrl.App.Tests/Settings/HandChunkedPageFitTests.cs` (`Construct` fallback — round-2 F3)

**Interfaces:**
- Consumes (Task 2): `EnumerateLearnedFills`, `EffectiveSettingsProvider.Changed`; (Task 4) `ResetLearnedOffsetsMessage`.
- Produces: `AdaptationLog(adaptation, feedbackGate, provider, string? jsonlPath = null)`; `AdaptationPageSource(ctx, Action? onOpenAdaptationLog = null)` with rows `ad_reset_learned` and `ad_log`; `%APPDATA%/EyeCtrl/adaptation-log.jsonl` (+ `.1` rotation).

- [ ] **Step 1: Write the failing log tests**

In `tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs`: add `using System.IO;` and `using EyeCtrl.Core.Processing;` (for `InteractionManager`; the file's existing usings cover the rest) and extend `Make` (the existing 6 facts keep their behavior — default path null disables the JSONL side):

```csharp
    private static (AdaptationLog Log, AdaptationSettings Adaptation, FakeFeedbackGate Gate) Make()
    {
        var settings = new EyeCtrlSettings();
        var gate = new FakeFeedbackGate();
        return (new AdaptationLog(settings.Adaptation, gate, new EffectiveSettingsProvider(settings.Adaptation)), settings.Adaptation, gate);
    }

    private static string TempLogPath()
        => Path.Combine(Path.GetTempPath(), "eyectrl_adaptlog_test_" + Guid.NewGuid() + ".jsonl");

    // A minimal real wiring (log + provider + coordinator pieces) so provider.Changed reaches the log
    // through the same subscription Start installs at runtime.
    private static (AdaptationLog Log, EffectiveSettingsProvider Provider, FakeFeedbackGate Gate, string JsonlPath) MakeWired(bool suppressed = false)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var gate = new FakeFeedbackGate { IsFeedbackSuppressed = suppressed };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var sink = new BufferingTelemetrySink(capacity: 64);
        var coordinator = new AdaptationCoordinator(
            new StruggleMonitor(sink, settings.EyeCare),
            new AdaptationController(provider, settings.Adaptation),
            new AdaptationLearner(provider, settings.Adaptation),
            settings.Adaptation, new TestFakes.FakeGazeProvider(), new InteractionManager());
        string path = TempLogPath();
        var log = new AdaptationLog(settings.Adaptation, gate, provider, jsonlPath: path);
        log.Start(coordinator);
        return (log, provider, gate, path);
    }
```

Append the seven facts:

```csharp
    [Fact]
    public void LearnedChange_WritesJsonlEntry()
    {
        var (_, provider, _, path) = MakeWired();
        try
        {
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.2);
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.0); // "given back" — review round 1, finding 13
            string[] lines = File.ReadAllLines(path);
            Check.That(lines).HasSize(2);
            Check.That(lines[0]).Contains("\"kind\":\"learned\"");
            Check.That(lines[0]).Contains("\"knob\":\"DwellTime\""); // enum values serialize verbatim (PascalCase), like the modes array
            Check.That(lines[0]).Contains("\"new\":0.2");
            // A knob dropping to 0 leaves EnumerateLearnedFills, so the log must diff against the last
            // seen KEY SET as well — otherwise the decay-to-zero event (and the manual reset) vanish.
            Check.That(lines[1]).Contains("\"knob\":\"DwellTime\"");
            Check.That(lines[1]).Contains("\"old\":0.2");
            Check.That(lines[1]).Contains("\"new\":0");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ModeTransition_WritesJsonlEntry()
    {
        var (log, _, _, path) = MakeWired();
        try
        {
            log.OnAssessment(Gated(FailureMode.SustainFailure));
            string[] lines = File.ReadAllLines(path);
            Check.That(lines).HasSize(1);
            Check.That(lines[0]).Contains("\"kind\":\"modes\"");
            Check.That(lines[0]).Contains("\"modes\":[\"SustainFailure\"]");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MomentaryOnlyChange_WritesNoJsonlEntry()
    {
        var (_, provider, _, path) = MakeWired();
        try
        {
            provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.5, 0.0);
            Check.That(File.Exists(path)).IsFalse(); // momentary ramps are derivative of mode transitions
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SuppressedContext_MomentaryOnlyChange_WritesNothing()
    {
        // Review round 1, finding 11: the suppressed marker is written only when there was something to
        // suppress. Unconditionally, a password field + a 4 Hz momentary ramp would append 4 lines/s —
        // a timing trace of interaction during credential entry, the opposite of §8.10's intent.
        var (_, provider, _, path) = MakeWired(suppressed: true);
        try
        {
            provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.5, 0.0);
            Check.That(File.Exists(path)).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SuppressedContext_WritesCoarseMarkerOnly()
    {
        var (log, provider, _, path) = MakeWired(suppressed: true);
        try
        {
            log.OnAssessment(Gated(FailureMode.SustainFailure));
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.2);
            string[] lines = File.ReadAllLines(path);
            Check.That(lines).HasSize(2);
            Check.That(lines[0]).Contains("\"kind\":\"suppressed\"");
            Check.That(lines[1]).Contains("\"kind\":\"suppressed\"");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SizeCap_RotatesToDotOne()
    {
        var (_, provider, _, path) = MakeWired();
        try
        {
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.2); // create the file
            // Push past the cap WITHOUT a 1 MB string alloc (finding 24): set the length directly.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
                fs.SetLength(AdaptationLog.MaxLogBytes + 1);
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.3); // next write rotates
            Check.That(File.Exists(path + ".1")).IsTrue();
            string[] lines = File.ReadAllLines(path);
            Check.That(lines).HasSize(1); // fresh file holds only the new entry
        }
        finally { File.Delete(path); File.Delete(path + ".1"); }
    }

    [Fact]
    public void NullPath_DisablesTheJsonlSide_Entirely()
    {
        // Self-contained (Make returns the 3-tuple the six existing facts destructure): a null path must
        // keep the Serilog behavior while no file is ever written.
        var settings = new EyeCtrlSettings();
        var gate = new FakeFeedbackGate();
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var log = new AdaptationLog(settings.Adaptation, gate, provider);
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure))).IsEqualTo(AdaptationLogAction.Logged);
        provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.2); // no throw, no file side
        Check.That(log.OnAssessment(Gated())).IsEqualTo(AdaptationLogAction.Logged);
    }
```

- [ ] **Step 2: Write the failing page tests**

In `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs`: modify fact 1 (15 → 17 rows, three buttons) and the Reset fact's `.Single()`, then add two facts:

```csharp
    [Fact]
    public void Build_HasSeventeenRows_ThreeButtons_NoCaregiverRow()
    {
        var rows = new AdaptationPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        Check.That(rows).HasSize(17);
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id).ToList())
            .ContainsExactly("ad_reset", "ad_reset_learned", "ad_log");
        Check.That(rows.Any(r => r.Id.Contains("caregiver", StringComparison.Ordinal))).IsFalse();
    }
```

(In the existing `Reset_CopiesAllFourteenFromDefaults`, change `.OfType<ButtonSpec>().Single()` to `.OfType<ButtonSpec>().Single(b => b.Id == "ad_reset")` — with three buttons the unqualified `Single` throws. Nothing else in that fact changes.)

```csharp
    [Fact]
    public void ResetLearnedButton_ModalYes_SendsResetLearnedOffsetsMessage()
    {
        var messenger = new WeakReferenceMessenger();
        var recipient = new object();
        bool received = false;
        messenger.Register<object, ResetLearnedOffsetsMessage>(recipient, (_, _) => received = true);
        var source = new AdaptationPageSource(TestSettingsPageContext.Build(messenger: messenger, showModal: (_, act) => act()));

        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>()
            .Single(b => b.Id == "ad_reset_learned").OnActivate();

        Check.That(received).IsTrue();
    }

    [Fact]
    public void OpenLogButton_InvokesTheCallback()
    {
        bool opened = false;
        var source = new AdaptationPageSource(TestSettingsPageContext.Build(), onOpenAdaptationLog: () => opened = true);

        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>()
            .Single(b => b.Id == "ad_log").OnActivate();

        Check.That(opened).IsTrue();
    }
```

(Add `using CommunityToolkit.Mvvm.Messaging;` and `using EyeCtrl.ViewModels.Messages;` if not implied by the file's existing usings.)

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: **Fehler!** — compile errors (`MaxLogBytes`, 4-arg `AdaptationLog`, 2-arg page source, `ad_reset_learned`/`ad_log` rows missing).

- [ ] **Step 4: Implement `AdaptationLog`'s JSONL side**

Rework `src/EyeCtrl.App/Services/AdaptationLog.cs` (full new content — the Serilog behavior is unchanged):

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// The struggle-adaptation session log (design §10). Two sides:
/// <list type="bullet">
/// <item>Serilog entries on gated-mode-set transitions — unchanged since 1b-⑤c.</item>
/// <item>The Phase-2 review file <c>%APPDATA%/EyeCtrl/adaptation-log.jsonl</c>: one JSON line per
/// learned-fill change (<c>kind":"learned"</c>, knob + old/new fill) and per mode transition
/// (<c>kind":"modes"</c>, the gated mode set + aggregate + the learned snapshot). Momentary-only
/// changes write nothing (they are derivative of mode transitions). In a sensitive context every
/// entry collapses to a coarse <c>kind":"suppressed"</c> marker (§8.10). Units are the provider's
/// band fills — absolute effective values are consumer-relative (a dwell fill composes against ~20
/// baselines) and are deliberately not logged (spec §10 amendment). Size-capped: on append past
/// <see cref="MaxLogBytes"/> the file rotates to <c>.jsonl.1</c> (single generation, replaced).</item>
/// </list>
/// All writes happen on the dispatcher thread (provider.Changed / the coordinator's tick), so the
/// I/O stays off the gaze hot path (§9.1). A null <c>jsonlPath</c> disables the file side entirely.
/// The decision methods are pure/headless-testable; only <see cref="Start"/>'s subscriptions are runtime.
/// </summary>
public sealed class AdaptationLog : IDisposable
{
    /// <summary>The JSONL size cap; a write past it rotates the file to <c>.jsonl.1</c>. Starting value, tunable.</summary>
    public const int MaxLogBytes = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly EffectiveSettingsProvider _provider;
    private readonly string? _jsonlPath;
    private readonly HashSet<FailureMode> _loggedGatedModes = new();
    private readonly Dictionary<AdaptationKnob, double> _lastLearned = new();
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    /// <summary>Initializes the log over its posture source, the suppression seam, and the provider whose
    /// learned fills are logged.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Posture"/> is recorded in each entry.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); when suppressing, only the coarse marker is logged.</param>
    /// <param name="provider">The effective-settings provider — its <c>Changed</c> drives learned-fill entries.</param>
    /// <param name="jsonlPath">Full path of the JSONL review file, or null to disable the file side.</param>
    public AdaptationLog(AdaptationSettings adaptation, IFeedbackGate feedbackGate, EffectiveSettingsProvider provider, string? jsonlPath = null)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
        _provider = provider;
        _jsonlPath = jsonlPath;
    }

    /// <summary>The set of failure modes whose confidence gate is met in the given assessment.</summary>
    public static IReadOnlySet<FailureMode> GatedModes(StruggleAssessment assessment)
    {
        var set = new HashSet<FailureMode>();
        foreach (var mode in assessment.Modes)
            if (mode.GatePassed)
                set.Add(mode.Mode);
        return set;
    }

    /// <summary>
    /// Logs a transition entry if the gated-mode set changed since the last assessment; otherwise no-op.
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <returns>Which logging action was taken.</returns>
    public AdaptationLogAction OnAssessment(StruggleAssessment assessment)
    {
        IReadOnlySet<FailureMode> current = GatedModes(assessment);
        if (_loggedGatedModes.SetEquals(current))
            return AdaptationLogAction.None;

        _loggedGatedModes.Clear();
        _loggedGatedModes.UnionWith(current);

        if (_feedbackGate.IsFeedbackSuppressed)
        {
            Log.Information("Adaptation suppressed (sensitive context)");
            WriteJsonl(new { ts = DateTimeOffset.UtcNow, kind = "suppressed" });
            return AdaptationLogAction.SuppressedMarker;
        }

        Log.Information(
            "Adaptation: struggle {AggregateLevel:F2}, gated modes [{Modes}], posture {Posture}",
            assessment.AggregateLevel,
            string.Join(", ", current.OrderBy(m => m)),
            _adaptation.Posture);
        WriteJsonl(new
        {
            ts = DateTimeOffset.UtcNow,
            kind = "modes",
            aggregateLevel = Math.Round(assessment.AggregateLevel, 2),
            modes = current.OrderBy(m => m).Select(m => m.ToString()).ToArray(),
            posture = _adaptation.Posture.ToString(),
            learned = LearnedSnapshot(),
        });
        return AdaptationLogAction.Logged;
    }

    // provider.Changed handler: writes one entry per knob whose learned fill changed since the last
    // observation — including a knob that DROPPED OUT of the enumeration (decayed/reset to 0), so the
    // §8.7 "given back" event and the §8.6 reset stay visible (review round 1, finding 13). Momentary-
    // only changes write nothing, suppressed or not: `Changed` fires at up to 4 Hz during momentary
    // ramps, and an unconditional suppressed marker there would be a 4 Hz timing trace of interaction
    // during credential entry — the opposite of §8.10 (finding 11). The coarse marker is written only
    // when there was something to suppress.
    private void OnProviderChanged()
    {
        var current = _provider.EnumerateLearnedFills().ToDictionary(e => e.Knob, e => e.Fill);
        var changed = new List<(AdaptationKnob Knob, double Old, double New)>();
        foreach (var (knob, fill) in current)
        {
            double last = _lastLearned.TryGetValue(knob, out double v) ? v : 0.0;
            if (fill != last)
                changed.Add((knob, last, fill));
        }
        foreach (var knob in _lastLearned.Keys)
            if (!current.ContainsKey(knob))
                changed.Add((knob, _lastLearned[knob], 0.0)); // dropped out = decayed or reset to zero

        if (changed.Count == 0)
            return; // momentary-only change (or no-op) — nothing to log, suppressed or not

        if (_feedbackGate.IsFeedbackSuppressed)
        {
            WriteJsonl(new { ts = DateTimeOffset.UtcNow, kind = "suppressed" });
        }
        else
        {
            foreach (var (knob, old, @new) in changed)
                WriteJsonl(new { ts = DateTimeOffset.UtcNow, kind = "learned", knob = knob.ToString(), old, @new });
        }
        RefreshLearnedSnapshot();
    }

    private void RefreshLearnedSnapshot()
    {
        _lastLearned.Clear();
        foreach (var (knob, fill) in _provider.EnumerateLearnedFills())
            _lastLearned[knob] = fill;
    }

    private object LearnedSnapshot()
    {
        var dict = new Dictionary<string, double>();
        foreach (var (knob, fill) in _provider.EnumerateLearnedFills())
            dict[JsonNamingPolicy.CamelCase.ConvertName(knob.ToString())] = Math.Round(fill, 4);
        return dict;
    }

    private void WriteJsonl(object entry)
    {
        if (_jsonlPath is null)
            return;
        try
        {
            var file = new FileInfo(_jsonlPath);
            if (file.Exists && file.Length > MaxLogBytes)
                File.Move(_jsonlPath, _jsonlPath + ".1", overwrite: true);
            File.AppendAllText(_jsonlPath, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Warning(ex, "Failed to append the adaptation log entry to {FilePath}; entry skipped", _jsonlPath);
        }
    }

    /// <summary>Subscribes to the coordinator's per-tick assessment stream and the provider's change
    /// stream. Call once on the UI thread; idempotent.</summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> drives the transition entries.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return;
        _coordinator = coordinator;
        coordinator.AssessmentUpdated += OnAssessmentEvent;
        _provider.Changed += OnProviderChanged;
        RefreshLearnedSnapshot(); // entries are diffs from now on
    }

    private void OnAssessmentEvent(StruggleAssessment assessment) => OnAssessment(assessment);

    /// <summary>Unsubscribes from both streams.</summary>
    public void Dispose()
    {
        if (_coordinator is not null)
        {
            _coordinator.AssessmentUpdated -= OnAssessmentEvent;
            _coordinator = null;
        }
        _provider.Changed -= OnProviderChanged;
    }
}
```

- [ ] **Step 5: Implement the JSONL production wiring, the page buttons, and the window**

**First, the DI registration** (review round 1, finding 6 — without this the §10 deliverable ships dead behind a green suite: the tests construct the log directly, so nothing but the bootstrapper can hand it the real path). In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, replace the `AdaptationLog` registration (~line 351):

```csharp
        services.AddSingleton(sp => new AdaptationLog(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IFeedbackGate>(),
            sp.GetRequiredService<EffectiveSettingsProvider>(),
            Path.Combine(appDataDir, "adaptation-log.jsonl")));
```

(`appDataDir` is in scope — same method, line ~51.) Then, in `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs` (the file uses an explicit constructor + `_ctx` field — keep that style): change the constructor and add the callback field, then add the two rows after the `ad_reset` button (keep the existing button and modal untouched):

```csharp
    private readonly SettingsPageContext _ctx;
    private readonly Action? _onOpenAdaptationLog;

    public AdaptationPageSource(SettingsPageContext ctx, Action? onOpenAdaptationLog = null)
    {
        _ctx = ctx;
        _onOpenAdaptationLog = onOpenAdaptationLog;
    }
```

```csharp
                new ButtonSpec("ad_reset_learned", "Reset Learned Offsets",
                    () => _ctx.ShowModal("Reset the learned adaptation offsets to zero?", () =>
                        _ctx.Messenger.Send(new ResetLearnedOffsetsMessage())),
                    ButtonKind.Reset, Width: 300, Height: 100)
                    { Description = "Clear what the app has learned about your needs; it re-learns as you use it." },
                new ButtonSpec("ad_log", "Open Adaptation Log",
                    () => _onOpenAdaptationLog?.Invoke(),
                    ButtonKind.Default, Width: 300, Height: 100)
                    { Description = "Show the adaptation log file in Explorer." },
```

(Add `using EyeCtrl.ViewModels.Messages;` **and `using CommunityToolkit.Mvvm.Messaging;`** — round-2 F4: the single-arg `Send` is the messenger extension, and `CalibrationPageSource` carries the same using. `Description { get; init; }` lives on the `SettingsRowSpec` base — verified — so both button initializers compile. `ButtonKind` offers `{ Default, Reset, Delete }` — `ad_log` takes `Default`.)

**Then fix the page-fit harness the optional ctor parameter breaks** (round-2 F3: `HandChunkedPageFitTests.Construct` reflection-matches an exact `(SettingsPageContext)` constructor; the new optional callback removes that shape and two facts fail). In `tests/EyeCtrl.App.Tests/Settings/HandChunkedPageFitTests.cs`, extend the lookup in `Construct` (~line 190):

```csharp
        var ctor = type.GetConstructor([typeof(SettingsPageContext)])
            // Sources may declare additional OPTIONAL constructor dependencies (the Adaptation page's
            // open-log callback); the harness builds those with their defaults — the guard measures
            // layout reachability, not wiring, and the tripwire below still fails loudly when neither
            // shape exists.
            ?? type.GetConstructors()
                .Where(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length > 0
                        && ps[0].ParameterType == typeof(SettingsPageContext)
                        && ps.Skip(1).All(p => p.HasDefaultValue);
                })
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();
```

(The existing `Check.WithCustomMessage` tripwire below it still fires when neither shape exists, so the guard stays loud.)

In `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (~line 389):

```csharp
        _adaptationSource = new AdaptationPageSource(_ctx, onOpenAdaptationLog: OpenAdaptationLogFile);
```

and the method (with the class's other private helpers):

```csharp
    // §10 "log review": gaze-reachable access to the adaptation log. Selects the file when it exists,
    // otherwise opens its folder (first run / log disabled). Never throws into the settings UI.
    private void OpenAdaptationLogFile()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "adaptation-log.jsonl");
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open the adaptation log location.");
        }
    }
```

(`SettingsWindow` holds no logger field — verified — so use the static `Serilog.Log` as above. It also needs **`using System.IO;`** — round-2 F5 confirmed the hedge: the file's existing imports do not cover `Path`/`File`.)

- [ ] **Step 6: Run the suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Bestanden!` with `gesamt: 758` (7 log facts + 2 page facts on top of Task 5's 749).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/AdaptationLog.cs src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/HandChunkedPageFitTests.cs
git commit -m "feat(adaptation): JSONL review log, reset-learned and open-log buttons"
```

---

### Task 7: Spec amendment, remaining doc nits, full verification

**Files:**
- Modify: `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md`
- Verify: all four suites

- [ ] **Step 1: Amend the spec**

1. **§7.4 — append the resolution block** (after the existing three bullets):

```markdown
**Phase-2 resolution (2026-09-04, plan `2026-09-04-learned-offset-phase2.md`).** The learned offset is a
**slow EWMA over the momentary layer's forgiving band fills** (per knob, global `Other` context in v1 —
per-context learned arrives with per-context momentary fills, if ever; D3), computed by a new Core
`AdaptationLearner` and composed inside `EffectiveSettingsProvider` exactly per §5:
`(momentaryForgivingFill + learnedFill) × bandEdge` under the single band clamp, with learned clamped to
`AdaptationSettings.LearnedMaxFill = 0.5` (half the band — the momentary layer always retains headroom;
D4). Rates (starting values, tunable): 30 s sample cadence on assess ticks only (paused/disconnected/
disabled stretches freeze the state), α 0.05 (τ ≈ 10 min: sustained full-band struggle reaches the cap in
≈ 9 min; calm decays a capped offset below the push ε in ≈ 40 min), push ε 0.01 (≥ 30 s between provider
writes per knob — the §7.4 rate limit and a §9.1 budget guard). The promotion criterion IS the EWMA time
constant applied to the controller's post-hysteresis fill — no second confidence machinery (D2). Reset
fork: session boundaries (§13.1) and the disable edge preserve learned (inert while disabled — Effective
gates on Enabled first); the manual §8.6 escape clears momentary AND learned; the settings action
(`ResetLearnedOffsetsMessage`, the §10 `ResetLearnedBaseline`) clears learned only. A per-knob `Adapt*`
toggle **zeroes** that knob's learned offset at the next sample (an off switch must stop adapting at
once — round-1 finding 8), and the learner's cadence resyncs on a gaze-clock restart (the
StruggleMonitor/GazeEngine class — round-1 finding 7). Persistence:
`adaptation-state.json`, atomic tmp+rename, per-entry validation (invalid entries dropped, survivors
kept — `ClampGazeIndicator` at file granularity), version-mismatch/corrupt ⇒ whole-file reset, saved at
app exit and on `ResetLearned` only (round-1 finding 10: this **narrows §9.1's "debounced to session
boundaries" to exit + reset saves** — a per-boundary flush adds write churn for at most one session of
re-learnable data; crash loses learning since the last save — accepted). The state is loaded at startup
only while `Enabled` (round-1 finding 18): a mid-session first enable starts learned at zero — loading
then would resurrect the pre-disable on-disk state over the (deliberately unpersisted) disable. "Log
review" (§13 Phase-2 line) = the size-capped `adaptation-log.jsonl` + "Open Adaptation Log" and
"Reset Learned Offsets" buttons on the Adaptation page; a live learned-status readout and a
settings-page log viewer remain future work (§10's "live status label" wording included). Also fixed
here: the §13.3 settings-edit staleness leftover — the provider now re-raises `Changed` on
composition-relevant settings edits (band fractions, `GeometryMaxMarginPx`), so every consumer
re-applies immediately. **§10 field mapping (round-1 finding 12), field by field:** `ts` → `ts` ✓;
`context` → dropped (always `Other` in v1 — the global layer has no context to report); `signal` →
dropped (the provider's `Changed` seam carries no per-signal payload; the Serilog transition line and
the `modes` entry name the gated failure modes, which is the signal-level detail); `failureMode` →
`modes` (an array on the `kind:"modes"` entry — transitions carry the whole gated set); `knob` → `knob`
on `kind:"learned"` entries ✓; `oldEffective`/`newEffective` → `old`/`new` **band fills** (absolute
effectives are consumer-relative — a dwell fill composes against ~20 baselines — and are deliberately
not logged); `reason` → dropped (redundant with `kind`, the only two of which are `learned` and
`modes`; the suppressed marker carries no detail by design, §8.10).
```

2. **§13.3 preamble fix (leftover b)** — replace the stale sentence "Phases 1a and 1b are merged, but only **one** of the four knobs reaches a consumer: `GazeEngine.cs:157` reads `Effective(FixationRadius, …)` and nothing reads the other three. The dwell/smoothing/geometry **routing slices** deferred out of 1b were never written, so the momentary layer is a quarter live." with a two-line historical note: routing completed 2026-09-01..03 (see the status block in §13); the section below is retained as the record of the prerequisite analysis.

3. **§5 learnedOffset bullet** — append "(v1 is global per knob — the 'per context/knob' scoping above is the full design; per-context arrives with per-context momentary fills, §7.4 resolution)".

4. **§13 Phase-2 line** — append "**(Complete 2026-09-04 — learned-offset slice; see the §7.4 resolution. Phase 3 (caregiver) is next.)**" in this task's commit, the geometry-slice precedent (`eed8ae36` stamped "Phase 1c complete" in its own spec task on the branch, pre-merge).

5. **§14 — add two entries:** (i) under the time-of-day bullet, note the Aiming-denominator self-damping assumption (§13.3 NB) and the §13.2 px-thresholds remain uncalibrated and are now *data-collectible* via `adaptation-log.jsonl` (`kind":"learned"`, `knob":"TargetGeometry"` transitions) — revisit with real data (D8); (ii) the supervisor does not write `adaptation-state.json` in v1 (single writer: the app) — unchanged from the existing SettingsService note.

6. **§4 diagram note** — under the mermaid diagram, add one sentence: the learned offset lives in the provider's composition (§5) and is computed by `AdaptationLearner` (coordinator-ticked), not inside `AdaptationController` — the diagram's AC label is the Phase-1a sketch; §5 is authoritative.
7. **§4 confidence-threshold clause (round-1 finding 17)** — after the context-strategy paragraph's "do not get a per-context learned offset until they cross a confidence threshold" sentence, add: "Phase 2's v1 learned offset is **global per knob** and inherits §6.3's confidence gate *transitively*: it EWMA-samples only the controller's post-gate, post-sustain momentary fill, so un-gated evidence never reaches it; the explicit per-context confidence threshold returns together with per-context learning (§7.4 resolution, D3)."
8. **§9.1 persistence line (round-1 finding 10)** — amend "learned-state persistence is debounced to session boundaries with an atomic write" to "learned-state persistence is **exit + manual-reset saves** with an atomic write (the realized v1 form of the debounce — see §7.4 resolution)".
9. **§14 no-offline-aging decision (round-1 finding 15, ledger D9)** — add: "Learned offsets do not age across disabled/idle stretches (only across active assess ticks). Exposure: re-enabling after a long disable applies the previously-learned offsets immediately; the manual escape or per-knob toggles clear them at once. Wall-clock decay on load was rejected because it would wipe the baseline on every overnight restart; revisit with real usage data."

- [ ] **Step 2: Fix the remaining stale comment (leftover d, second site)**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs` (~line 152), the dwell-routing comment "Dwell routing (Phase 1c slice 2): … Eagerly resolved at startup (App.xaml.cs)" — verify against `App.xaml.cs`'s eager-resolve block (it resolves both routers there) and, if the comment's pointers are accurate, leave it; the kickoff's nit is the provider comment fixed in Task 5(c). If any pointer is inaccurate, correct it. Comment only.

- [ ] **Step 3: Full verification — all four suites, sequential, one at a time**

```bash
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Benchmarks.Tests
```

Expected, in order: `Bestanden!` **gesamt: 1194** (`erfolgreich: 1193, übersprungen: 1`) / **300** / **758** / **27**.

- [ ] **Step 4: Graph change analysis (AGENTS gate)**

Run graph change analysis over the branch's changes — from this worktree the CLI script does not exist (round-2 F6: `.gitnexus/run.cjs` is a main-checkout artifact), so use the MCP tool with the worktree parameter: `detect_changes({scope: "all", worktree: "C:/Users/andre/source/repos/eyectrl/.worktrees/learned-offset"})`; the CLI equivalent (when run from the main checkout) is `node .gitnexus/run.cjs detect-changes --scope all --repo .`. `partial: true` or `truncated: true` is not a clean check; re-run until clean. The expectation (confirmed by the round-2 dry run): changes confined to the adaptation community + the settings page source + the test harness; nothing in the gaze-pipeline hot path.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md src/EyeCtrl.App/Services/AppBootstrapper.cs
git commit -m "docs(spec): record the Phase-2 learned-offset resolution (§7.4); fix §13.3 preamble"
```

---

## Notes for the implementer

- **Read Global Constraints first** — especially additive-only provider semantics and the reset fork. The two most expensive mistakes this plan guards against: (1) breaking the "learned == 0 ⇒ bit-identical" identity (any existing-test failure means you did), and (2) wiring the disable edge through the learned-clearing reset (wipes learning on every settings toggle).
- **German output**: read the `Bestanden!`/`Fehler!` summary line, never grep "Failed". One suite at a time; check the box is quiet (`Get-Process -Name dotnet,testhost*,EyeCtrl*`) before each run.
- The learner tests use exact-arithmetic expectations (powers of 0.95) — if an EWMA fact fails by a tiny epsilon, suspect the clamp or the cadence guard order, not the math.
- `AdaptationPageSource`'s exact ctor style (primary-constructor vs explicit) must match what the file uses today; the contract is the optional `onOpenAdaptationLog` parameter and the two row ids.
- If `AdaptationLogTests.MakeWired`'s coordinator construction fights the test-file's existing usings, add `using EyeCtrl.Core.Processing;` (InteractionManager) — the handler/coordinator test files already carry the set.
- **Do not** "improve" `AdaptationController`, `StruggleMonitor`, or any router while in there. If a task's expected count is off by one, recount before adapting — the deltas (after both review rounds) are: provider +12, learner +12, state file +8, coordinator +4, handler +2, registration +1 (in **Task 4**, round-2 F1), log +7, page +2 (net); interim totals 1174 → 1194 (Core) and 742 → 749 → 749 → 758 (App — Task 4 ends at 749, Task 5 is code-only).
- Two knife edges the review rounds carved around — do not "simplify" them away: the coordinator drive's t=73 learner sample clears the push ε by ~1.7e-18 (the 103 s tick exists so the assertion does not depend on it), and the JSON round-trip depends on `ReadOptions` being case-insensitive (a camelCase write with a case-sensitive read silently loads `Empty`).
