# Caregiver Alerts (Phase 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Phase 3 of struggle-detection/self-adaptation (design §10, §13) — a caregiver alert that fires when high struggle or poor tracking is **sustained**, rendered as an **app-owned banner window plus an attention chime** (never an OS notification), rate-limited by a user-visible setting, suppressed in sensitive contexts, logged to `adaptation-log.jsonl`, and exposed on the Adaptation settings page.

**Architecture:** A new `CaregiverAlertService` follows the landed feedback-service pattern exactly (`AdaptationCueService` / `AdaptationLog` / `DriftNoticeService`): it subscribes `AdaptationCoordinator.AssessmentUpdated`, consults the shared `IFeedbackGate` (§8.10), keeps a pure headless-testable decision over an injected clock, and presents through a new `ICaregiverAlertPresenter` seam whose runtime implementation is a new `CaregiverAlertWindow` — a `DriftNoticeWindow`-pattern overlay (topmost, click-through, excluded from magnifier capture, gaze-safe) that shows **while the triggering condition stays sustained** and fades when it clears. `AdaptationActivation` starts/stops the service on the `Enabled` edge like its three siblings. `TrayIconManager`, `HeartbeatPublisher`, `AdaptationCoordinator`, `StruggleMonitor`, `AdaptationController`, and every 1c router are **not touched**. The supervisor relay stays **out of v1** (§14).

**Tech Stack:** C# / .NET 10 (WPF + WinForms for `SystemSounds`), xUnit v3, NFluent assertions (`Check.That(...)`, **not** `Assert.*` — wired globally in `tests/Directory.Build.props`, which also supplies global `using NFluent;` and `using Xunit;`).

---

## Global Constraints

- **Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §10 (caregiver-alert v1 mandate + feedback settings), §7.1 (poor tracking → "caregiver alert if sustained; never tighten"), §8.10 (feedback suppressed in sensitive contexts — the toast is named), §13.1 (the `IFeedbackGate`/UIA suppression seam, which explicitly anticipates "the caregiver toast (Phase 3)"), §6.3/§7.2 (what "sustained" means for adaptation), §13 (Phase-3 line), §14 (supervisor-relay open items). Read §10 and §13.1 first.
- **Owner decisions, settled interactively 2026-09-05 — fixed constraints, never re-litigated downstream** (rationale + rejected alternatives in the ledger below):
  1. **App-owned window + chime, NOT OS notifications** (owner instruction: Windows notifications "can be suppressed and thus not be visible to the caregiver" — disqualifying for a safety channel).
  2. Persistence: **while sustained + fade** on natural clear.
  3. **Window + chime** (`SystemSounds.Exclamation`, once per onset).
  4. Trigger: **two conditions, 60 s continuous** — aggregate struggle ≥ 0.5, or PoorTracking gated.
  5. Rate limit: **global clock, `CaregiverMinIntervalMinutes` (default 10, [1, 60]) exposed as a slider**, plus a ≥ 30 s clear gap to re-arm.
  6. Suppression: **consume suppressed onsets** (DriftNoticeService precedent); mid-display suppression hides immediately.
  7. Drift is **not** a caregiver condition (stays the in-app banner).
  8. Alert onsets write **`kind:"caregiver"` JSONL entries**.
  9. Page exposure: **toggle row + rate-limit slider row** (the owner's explicit pick over toggle-only).
  10. Supervisor relay: **out of v1**, recorded in §14.
- **Work in the worktree** `.worktrees/caregiver-alerts`, branch `feat/struggle-caregiver-alerts` (this plan is committed there as Task 1, the Phase-2/geometry pattern). Run every command from the worktree root. `dotnet build-server shutdown` before any worktree removal.
- **Never run the application or any `Category=UI` test.** The repo owner controls their PC solely by eye tracking; launching it seizes their only input channel. Everything here is verifiable headlessly. Every NEW test drives the decision through the `ICaregiverAlertPresenter` fake — no new test creates, shows, or resolves a WPF `Window`. The one window-constructing touch is ADDING a case to the existing sanctioned sweep `OverlayResourceResolutionTests` (`[Collection("WpfApplication")]` + `[StaTheory]`; it constructs each listed overlay and closes it without ever showing — the repo's own mechanism; review round 1, F3).
- **One test suite at a time**, sequential (named pipes are machine-global; this box routinely runs a dozen worktrees and possibly a live app). Check the box is quiet first (`Get-Process -Name dotnet,testhost*,EyeCtrl*`).
- **German test output.** The summary reads `Bestanden!` / `Fehler!` with `Fehler: N, erfolgreich: M, gesamt: T`. Read the summary line; grepping "Failed" silently reports green.
- **Baselines (re-measured 2026-09-05, sequential, all "Bestanden!"):** Core **1218** (`erfolgreich: 1217, übersprungen: 1`) / ViewModels **300** / App (`--filter "Category!=UI"`) **778** / Benchmarks.Tests **27**. Measured at `7f457cd2`; `main` has since advanced to `7c62761c` with a **docs-only** commit (README/ARCHITECTURE/AGENTS — zero code/test files, so the counts stand; this worktree branches from `7c62761c`). Different counts ⇒ stop and report.
- **Additive-only behavior.** With `CaregiverAlerts = false` (the default) the service computes sustain state but never fires, and no JSONL entries are written; no window is constructed while `Adaptation.Enabled` is false (with `Enabled` on and `CaregiverAlerts` off the HWND exists but stays hidden — exactly the `CueOverlayWindow`/`ShowCue` precedent). Every existing test in all four suites must pass unchanged **except the explicitly modified ones this plan names**: `AdaptationSettingsTests.NewDefaults` (one added assert), `AdaptationPageSourceTests` facts 1/3/4 (row count 17→19, caregiver setters, reset 14→16 — all verbatim below), `AdaptationLogTests` (one **added** fact, no modification), `SettingsSliderSpanGuardTests.Adaptation_SliderSpans` (slider count 6→7 + one span pin — review round 1, F1), and `OverlayResourceResolutionTests.MigratedOverlays` (+1 theory case — F3). `OverlayInventoryDriftTests` needs no test edit — it is kept green by editing `docs/design/overlay-unification-spec.md` in the same task that adds the window class.
- **Blast radius (GitNexus, graph re-indexed 2026-09-05):** `TrayIconManager` LOW — and **untouched** (the window is a DI singleton like `DriftNoticeWindow`; the tray's non-DI construction is why the balloon route is dead regardless of the owner's decision); `AdaptationActivation` MEDIUM (6 direct, 23 total); `AdaptationLog` MEDIUM (4 direct, 33 total); `AdaptationCoordinator` unchanged. No HIGH/CRITICAL surface is modified.
- **Index notes:** the GitNexus re-index left the FTS (keyword-search) side degraded after two `--repair-fts` attempts (a LadybugDB WAL-rotation failure) and the commit-stamp reports stale even though the graph content is current (verified: `AdaptationLearner` and its Phase-2 callers resolve). Impact/context/detect-changes gates are trustworthy; keyword search is not. `.gitnexus/run.cjs` does not exist in the worktree (Phase-2 round-2 F6) — use the MCP tools with the worktree parameter from there.
- **No new benchmark.** The service runs on the 4 Hz assessment stream doing O(1) condition tracking; §9.1 is untouched. Recorded as a decision, owner-vetoable.
- **No new `internal` seams are required** (`InternalsVisibleTo` exists for both test projects — `EyeCtrl.Core.csproj:12` and `EyeCtrl.App.csproj:71`; the AGENTS.md claim that only Core declares it is outdated and is fixed in Task 7). The service's three consts are `internal` (the `AdaptationCueService.ReflashIntervalMs` precedent) and the tests drive **literal** timings so an accidental const change fails a fact rather than silently tracking it. **Honesty note (implementation review, F2):** as originally written this pinned only `SustainMs` exactly — mutation testing showed `ClearGapMs` (surviving band 10 250–31 250 ms) and `StruggleLevelThreshold` (band 0.49–0.6) could drift silently; the round-3 facts below pin all three, including both exact boundaries (`>=` at 30 000 ms clear, fires-at-exactly-the-interval).
- NFluent only; no mocking library (private fakes); file-scoped namespaces; Allman braces; `_camelCase` fields; nullable enabled. `EyeCtrl.Core` escalates CS1591 (missing XML doc) to an **error** — the one Core member this plan adds (a settings property) needs its `///`.
- **If a step's expected output does not match, stop rather than adapting.** Anchors reference `main` at `7c62761c`; drift ⇒ report BLOCKED with what you saw.

> **Review round 1 (2026-09-05, external analytical review): NEEDS FIXES → all 10 findings verified against source and applied.** F1 (blocker): `SettingsSliderSpanGuardTests.Adaptation_SliderSpans` pins the Adaptation slider count at 6 — Task 6 Step 1 now edits it (7 + a span pin for `ad_caregiver_interval`). F2 (blocker): the disable edge delivers NO `None` (`StopRuntime` calls `ResetMomentary()`, which sets `LastAssessment` without raising, and synchronously disposes the timer; on re-enable `_wasEnabled` is still true so the first raise is already a real assessment) — a stale `SustainedSince` would fire instantly 10 minutes later, so `Dispose` now resets both `ConditionState`s, with a 17th service fact pinning it. F3: the window joins `OverlayResourceResolutionTests.MigratedOverlays` (+1 `[StaTheory]` case — a bad `{StaticResource}` key would otherwise first throw inside `AdaptationActivation.StartRuntime`, whose catch converts it to "adaptation stays inert", silently killing ALL adaptation on enable with every suite green). F4: §3.1's finding paragraph does its own overlay arithmetic — "11 transparent" → 12 and "Two of the eleven" → "Three of the twelve" join the Task 4 spec edit. F5: the §10 resolution lands at the **end** of §10 (the Phase-2 §7.4 precedent; inserting after the caregiver bullet would split the Markdown list). F6–F10 cosmetic: a test comment's arithmetic (91.5 s ≈ 1.5 min, not 2.5), the `||`-short-circuit comment ("within the same tick" → the struggle onset is skipped that tick and rate-limited thereafter), the HWND-wording overclaim in Global Constraints, the "satisfies both conditions" hedge (`PeakGatedScore × (1 + w·fatigue)`), and the File-Structure `.xaml.cs` cell. **Counts:** the App chain is 778 → 795 → 796 → 797 → 797, final **797** (the reviewer's next-steps box said 796 — F2's +1 and F3's +1 stack; re-derived here). The reviewer's open question — the `detect_changes({worktree})` parameter and the blast-radius figures — was closed by the planning session: both verified directly against the MCP tool schema and the re-indexed graph.

> **Review round 2 (2026-09-05, implement-verbatim dry run): FINDINGS F1–F3 — all counts landed, fix-forwards mirrored into this plan.** Per-task: Core 1219 / App 795 → 796 → 797 → 797 / final four-suite **1219 / 300 / 797 / 27**; every RED step failed for exactly the stated reason. F1: the test `Make()` tuple declared `AdaptationSettings Settings` but returns an `EyeCtrlSettings` (CS0029 + 5× CS1061) — the element type is `EyeCtrlSettings`. F2: `Dispose_ClearsSustainState` ran 1 + 240 = 241 fresh-sustain ticks, making the "still nothing" assert land ON the onset tick by this plan's own fact-2 arithmetic — the second `Run` is 239 ticks (240 total = 59.75 s, then the explicit onset tick fires). F3: `FontWeight="Bold"` trips `RawGeometryLiteralTests.NoUIFile_WritesAGeometryOrTypeValueOutsideTheTokens` — raw `CornerRadius`/`FontFamily`/`FontSize`/`FontWeight` literals are test-forbidden; the XAML uses `{StaticResource FontWeightStrong}` (`TrackStatusWindow` precedent; all three themes map it to `FontWeights.Bold`). All three mirrors are in place; no count changes. **The dry run left a GREEN IMPLEMENTATION on the branch** (`ea1a1221..0f154892`, 6 commits, tree clean, unpushed) — the process ladder's SDD step now overlaps it; the owner decides whether to accept the dry-run commits as the implementation or re-execute SDD fresh (the Phase-2 precedent). The graph gate was discharged by the reviewer via MCP: `detect_changes` scope-all clean LOW; compare-vs-main 39 symbols / 22 files, the HIGH label driven solely by additive DI registrations plus a comment-only `OnStartup` touch matching 10 startup processes; the changed files are exactly this plan's set — zero gaze-hot-path symbols. Environment note, outside this slice: `ExternalDwellWireContractTests.Unregister_MidDwell_StopsProgress_WithoutActivation` flaked 3× in ~14 runs ("Register did not reach the server") on a GUID-suffixed pipe untouched since `4422f962` — a pre-existing machine-level flake, own ticket.

> **Implementation review (2026-09-05, external, mutation-tested): NEEDS FIXES → applied.** Verdict: **no code defect, no blocker** — every owner decision (D4/D5/D6/D11/D12) verified implemented and unbreakable by hand; 21 mutations of the state machine, 11 killed, **10 survived (9 non-equivalent)**, all closed by **8 additive test facts, zero production changes**: `StruggleThreshold_FiresAtExactlyHalf_NotJustBelow` (pins the 0.5 const + `>=`), `FirstEverAlert_FiresEvenWhenTheClockIsNearZero` (the `_hasAlerted` guard — without it the first alert of a session is blocked for the first 10 min of uptime), `Dispose_KeepsTheRateLimitClock_TogglingCannotBypassTheInterval` (the D11 toggle-spam half), `ReArm_AtExactlyThirtySecondsOfClear_IsAFreshEpisode` + `ReArm_JustUnderThirtySecondsOfClear_IsTheSameFlareUp` (the exact clear-gap boundary both ways), `RateLimit_AtExactlyTheInterval_Fires` (`<` not `<=`), `SuppressionMidDisplay_HidesOnce_NotEveryTick` (`_showing` staleness), and `SuppressedOnset_WritesNothingToTheLog` — the §8.10 privacy guarantee: a suppressed onset must leave no JSONL trace, which no prior fact observed. M9 (`!none &&` operands) is provably equivalent — kept as documentation-in-code, like `AdaptationCoordinator.IsNone`; noted that `AlertedThisEpisode`/`SuppressedOnsetConsumed` are functionally one flag (diagnostic naming only). The plan's const-pinning claim (Global Constraints) is corrected per the review's F2 table. **App 797 → 805** (25 service facts; normal post-plan fix-wave drift — cite 805 in the PR). Three cosmetic/open items left to the owner: the cross-condition fade corner (D12's accepted corner in fade form — intended as designed), a `try/catch` around the presenter call in `Dispose` (protects the UIA-hook-stops-last ordering in `StopRuntime`), and `EnsurePositioned`'s one-shot latch (drift precedent; a safety channel may deserve its own slice). Banner layout budget verified (longest message ≈108 DIP against `Height="120"`).
>
> **Follow-up (2026-09-06, owner-sanctioned): the F6 `try/catch` applied** (+1 fact `Dispose_SwallowsAPresenterFailure_NeverBreaksTeardown`; **App 805 → 806** — the PR's count). The fade corner and the `EnsurePositioned` latch are accepted as designed (documented above; dynamic positioning is its own slice if real usage ever shows a stale display).

### Owner-vetoable decision ledger (settled 2026-09-05, interactive brainstorm — every entry was answered by the owner or stated in the round intros without objection; flip any by editing the named member, not the structure)

| # | Decision | Rationale | Rejected |
|---|---|---|---|
| D1 | **Mechanism: app-owned `CaregiverAlertWindow` + chime — never an OS notification** | Owner instruction: Focus Assist / DND / per-app settings can silence Windows notifications; a caregiver safety channel must not be suppressible by the OS. The app's own topmost window is exactly as reliable as the app's own cursor rendering | `NotifyIcon.ShowBalloonTip` (spec §10's original first listing — lands in the OS notification channel); WinRT `AppNotification` (same channel, plus a NuGet package, registration, and activation handling) |
| D2 | **Persistence: while sustained + ~1 s fade on natural clear; immediate hide on suppression/feature-off/teardown** | Mirrors the situation — visible exactly as long as the user is struggling; nothing for anyone to dismiss; the immediate-hide path is the cue's §8.10 precedent (a fade is too slow to keep showing in a sensitive context) | Fixed 60 s auto-hide (missable); persist-until-caregiver-clicks (introduces click handling + gaze-safety questions on the alert surface, and lingers stale) |
| D3 | **Chime: `SystemSounds.Exclamation`, once per onset, played by the presenter, failures swallowed** | Reaches a caregiver in another room where a window cannot; second independent channel (each muteable only separately); zero new assets (`click.wav` is a subtle click — wrong semantics); presenter-side keeps the decision service pure and UI-free (§11: log/toast failures are swallowed) | Visual only; shipping a new .wav asset (new loose resource + `ExcludeFromSingleFile` plumbing for v1 polish) |
| D4 | **Trigger: two conditions, 60 s continuous — aggregate level ≥ 0.5, or PoorTracking gated; poor tracking evaluated first when both are due** | §10 names exactly these two; 60 s = one full evidence window (§6.3's rolling max-age), long enough to be a caregiver-scale event vs. §7.2's 3 s adaptation sustain; PoorTracking fires with zero interactions (§6.3); the 0.5 threshold is fixed and posture-independent — a caregiver channel must not get more sensitive because adaptation is eager. When both hold, poor tracking is the more specific, more actionable cause (reposition/re-calibrate) | Aggregate-only (loses the zero-interaction condition §7.1 calls out); 30 s (more false alarms); posture-scaled threshold |
| D5 | **Rate limit: one global clock, `AdaptationSettings.CaregiverMinIntervalMinutes` (default 10, validated [1, 60], Adaptation-page slider), plus a ≥ 30 s clear gap to re-arm; a rate-limit block delays (retries each tick while sustained) rather than consuming** | Owner explicitly chose the slider exposure; one clock prevents cross-condition spam; the clear gap makes a flapping condition one flare-up, not a series of alerts; delay-not-consume means a long episode still surfaces once the interval elapses (safety channel) | Per-condition clocks (two toasts a minute apart); once-per-episode only (flap spam); consume-on-block (a >interval-long struggle would never show) |
| D6 | **Suppression: an onset arriving while suppressed is consumed (episode marked, no window even after suppression lifts) and does NOT start the rate-limit clock; suppression/feature-off mid-display hides immediately** | DriftNoticeService's documented §8.10 trade — never leak that struggle coincided with credential entry; not starting the clock avoids an invisible 10-minute lockout after a password-adjacent onset | Show-when-suppression-lifts (the pop timing itself leaks); suppressed onset arms the clock |
| D7 | **Drift is not a caregiver condition** | §7.1's drift response is "report" — the in-app `DriftNoticeService` banner is user-actionable (re-calibrate); paging a caregiver for it adds an alert path with no new capability | Drift as a third condition |
| D8 | **JSONL: `kind:"caregiver"` entries (`ts` + `condition`) appended by `AdaptationLog.LogCaregiverAlert`, invoked via an `Action<CaregiverAlertCondition>` callback; suppressed onsets write nothing** | The review file is the data source for tuning the 60 s / 10 min numbers (the D8-carried calibration item); the callback keeps the service decoupled from the log type and headless-testable; the write rides `AdaptationLog`'s existing rotation + cap | Serilog only; the service writing the file itself (duplicates the rotation/cap owner) |
| D9 | **Supervisor relay: out of v1** | Needs its own pipe channel (never `eyectrl_watch` — §10 bars the liveness heartbeat), a supervisor-side consumption design (what a headless watchdog *does* with an alert), and would reopen the `SettingsService` single-writer question — its own slice with its own review | In v1 (roughly doubles the slice) |
| D10 | **Seat/wiring: `CaregiverAlertService` in `EyeCtrl.App.Services` (cue/log/drift shape), presenter seam `ICaregiverAlertPresenter`, window + presenter as DI singletons exactly like `DriftNoticeWindow`/`IDriftNoticePresenter`; started/stopped by `AdaptationActivation`; `TrayIconManager` untouched** | The established pattern verbatim; DI ownership of the window is what makes the dead balloon route also the *simplest* route to drop; activation on the `Enabled` edge keeps the off-by-default footprint at zero (no HWND while disabled) | A tray balloon wrapper (dead per D1); a late-attach presenter holder (unneeded once the window is DI-owned) |
| D11 | **State across enable/disable: sustain/episode state dies on `Dispose` (both `ConditionState`s fully reset — round-1 F2; the disable edge delivers NO `None`, see Context fact 1); the rate-limit clock survives (singleton field, not reset)** | Toggling adaptation off/on must not bypass the interval (toggle-spam guard); conversely a stale sustain must never fire on restart — without the Dispose reset, a 20 s pre-disable sustain fires instantly 10 min later on the first re-enabled tick | Reset everything on Dispose (interval bypass via toggling); persist the clock (meaningless across processes) |
| D12 | **Both conditions share one window; the message reflects the most recent onset's condition** | One alert surface; the global clock already prevents two onsets within an interval, so overlap is a corner (the showing condition's message simply stays) | Two windows (double the surface for a corner case) |
| D13 | **Carried items:** (a) AGENTS.md `InternalsVisibleTo` claim fix **folded** (Task 7 — one line, both csproj facts verified); (b) D9-offline-aging **stands** (§14 already records the exposure); (c) D8-calibration **stands** (the JSONL this slice extends keeps collecting; `kind:"caregiver"` onsets add alert-frequency data) | (a) is cheap and discovered twice (Phase-2 round-1 + kickoff); (b)/(c) are recorded exposures with revisit criteria | — |

---

## Context you need before starting

**What exists (all at `main` `7c62761c`).** The full Phase-1a/1b/1c/2 stack: `AdaptationCoordinator` (250 ms dispatcher tick; raises `AssessmentUpdated` at 4 Hz carrying `LastAssessment`, which is `StruggleAssessment.None` on non-assessing ticks — disabled, paused, disconnected, awaiting-gaze, reset — with **runs of Nones coalesced to the first**); `StruggleAssessment` (`Modes` list + `AggregateLevel` in [0,1] + `ForMode(FailureMode)` returning `FailureModeAssessment?` with `GatePassed`); `AdaptationActivation` (lazily resolves + starts/stops the runtime on the `Enabled` edge — `StartRuntime`/`StopRuntime` currently start/dispose coordinator, cue, log, drift, and the UIA detector); `AdaptationCueService`/`AdaptationLog`/`DriftNoticeService` (the three `AssessmentUpdated` subscribers — pure decisions, `IFeedbackGate` seam, presenter seams `ICuePresenter`/`IDriftNoticePresenter`); `DriftNoticeWindow` (the overlay-window template: `OverlayWindow` subclass, `IsClickThrough = true`, `ExcludeFromScreenCapture = true` set **before** `InitializeComponent`, bottom-center `SystemParameters.WorkArea` positioning, `RaiseToTop()`); `AdaptationSettings.CaregiverAlerts` (exists, default `false`, XML doc already says "Phase 3"); the Adaptation page (17 rows, 3 buttons, `PrefersAutoPagination = true`); `adaptation-log.jsonl` (JSONL writer with 1 MB rotation, camelCase policy, `WriteJsonl` private).

**Facts that shape this plan:**

1. **The coordinator's None semantics do most of the session work — but NOT the disable edge (review round 1, F2).** Pause/disconnect/awaiting-gaze/reset all arrive as a `None` (runs coalesced), which the service treats as "both conditions unmet" — episodes end, windows fade, sustain restarts fresh. **Disable delivers no `None` at all:** `AdaptationActivation.StopRuntime` calls `coordinator.ResetMomentary()` — which sets `LastAssessment = None` *without raising* — and synchronously disposes the timer on the setting-change thread, so no tick ever runs; and on re-enable `_wasEnabled` is still true, so the first raise is already a real assessment. `Dispose` is therefore the only place pre-disable sustain state can die — the service's `Dispose` resets both `ConditionState`s (the F2 fix) while the rate-limit clock deliberately survives (D11).
2. **A gated PoorTracking episode usually also raises the aggregate.** The monitor's aggregate is `PeakGatedScore(modes) × (1 + w·fatigue)` (§13.2), so a gated PoorTracking scoring ≥ 0.5 — as the test fixture does, 0.6 — satisfies *both* conditions; a low-scoring gated mode (e.g. 0.35) does not. Evaluating the poor-tracking onset first (D4) is what makes the toast say the specific thing whenever both are due. This is asserted by `PoorTrackingGated_SixtySeconds_FiresPoorTracking`.
3. **The overlay inventory is test-enforced.** `OverlayInventoryDriftTests` (3 facts, headless, `Category!=UI`) reflect over every concrete `OverlayWindow` subclass and read `docs/design/overlay-unification-spec.md`: the new class must land in the same task as its §3.1 row, and §1's opening count sentence must go 25→26 overlays / 23→24 with XAML. No other section of that spec counts overlays (the other "25"s are ΔE2000 values).
4. **The settings-page completeness gate binds both new rows.** `SettingsRowDescriptionCompletenessTests` fails any describable row (toggle/slider/combo/color) without a `Description`; the two new rows below carry one.
5. **`Environment.TickCount64` has no safe "never" sentinel** (arithmetic with `long.MinValue` overflows), so the rate-limit clock uses a `_hasAlerted` bool rather than a sentinel timestamp — the same class of discipline as the coordinator's `Unseeded` comment (there the gaze clock made a sentinel necessary; here a bool is simpler and sufficient).
6. **`AdaptationLog`'s JSONL options camelCase property NAMES, not enum VALUES** — a `kind:"caregiver"` entry carries `"condition":"SustainedPoorTracking"` (PascalCase value). The test asserts exactly that.
7. **NFluent traps:** string `Contains` with ONE argument is a substring check (correct here); never pass the two-argument `Contains(string, StringComparison)` form (silently misbinds). `Check.ThatCode(() => ...).Throws<ArgumentOutOfRangeException>()` is the file-local precedent in `AdaptationSettingsTests` for setter guards.
8. **App wiring anchors:** the adaptation DI block ends at `AppBootstrapper.cs:370` (the `DriftNoticeService` registration — insert after it); `AdaptationActivation.StartRuntime` starts drift at `AdaptationActivation.cs:138` and `StopRuntime` disposes it at `:157`; `App.xaml.cs:341` names "the cue / drift-notice / log subscribers" in a comment; `SettingsWindow.xaml.cs:246` carries the now-dying "Adaptation.CaregiverAlerts (no consumer until Phase 3)" audit-comment line. `AppBootstrapper.cs` already imports the Overlays namespace (it registers `DriftNoticeWindow` unqualified).
9. **The spec's own text needs three touches** (Task 7): §10's caregiver bullet gains the Phase-3 resolution (superseding the balloon/WinRT wording, which is retained as history); §13's Phase-3 line gets the completion stamp (the geometry-slice precedent — stamped on the branch, pre-merge); §14's relay bullet records the deferral. Plus the §3 key-decisions row and the AGENTS.md IVT line.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/EyeCtrl.Core/Settings/AdaptationSettings.cs` | `CaregiverMinIntervalMinutes` property + guard + `Validate()` entry | Modify (Task 2) |
| `src/EyeCtrl.App/Services/CaregiverAlertCondition.cs` | **New.** The two alert conditions (enum) | Task 3 |
| `src/EyeCtrl.App/Services/ICaregiverAlertPresenter.cs` | **New.** Presenter seam (show / fade / hide) | Task 3 |
| `src/EyeCtrl.App/Services/CaregiverAlertService.cs` | **New.** Pure sustain/rate-limit/suppression decision + lifecycle | Task 3 |
| `src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml` + `.xaml.cs` | **New.** The banner + chime (DriftNoticeWindow pattern) | Task 4 |
| `docs/design/overlay-unification-spec.md` | §3.1 row + §1 counts + the finding-paragraph arithmetic (keeps `OverlayInventoryDriftTests` green) | Task 4 |
| `tests/EyeCtrl.App.Tests/OverlayResourceResolutionTests.cs` | +1 `MigratedOverlays` theory case (round-1 F3) | Task 4 |
| `src/EyeCtrl.App/Services/AdaptationLog.cs` | `LogCaregiverAlert` public write | Task 5 |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | DI: window + presenter + service | Task 6 |
| `src/EyeCtrl.App/Services/AdaptationActivation.cs` | Start/Stop lines + doc-comment count | Task 6 |
| `src/EyeCtrl.App/App.xaml.cs` | Comment-only: name the fourth subscriber | Task 6 |
| `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs` | `ad_caregiver` toggle + `ad_caregiver_interval` slider + reset copies | Task 6 |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | Remove the dead "no consumer until Phase 3" audit line | Task 6 |
| `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs` | `NewDefaults` +1 assert; +1 guard fact | Task 2 |
| `tests/EyeCtrl.App.Tests/Services/CaregiverAlertServiceTests.cs` | **New**, 17 facts | Task 3 |
| `tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs` | +1 JSONL fact | Task 5 |
| `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs` | Facts 1/Setters/Reset modified (verbatim below) | Task 6 |
| `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs` | Slider count 6→7 + `ad_caregiver_interval` span pin (round-1 F1) | Task 6 |
| spec `…self-adaptation-design.md` + `AGENTS.md` | §10/§13/§14/§3 amendments; IVT claim fix | Task 7 |

**No ViewModels, Core-adaptation, router, engine, controller, monitor, tray, or heartbeat change.**

**Expected final counts:** Core **1219** (1218 + 1 skip) / ViewModels **300** / App **797** / Benchmarks.Tests **27**.

---

### Task 1: Commit the plan file itself

The kickoff provides this plan in the working tree (untracked). Commit it first so `git diff --stat main` lists it (the Phase-2/geometry Task-1 pattern).

- [ ] **Step 1: Commit**

```bash
git add docs/superpowers/plans/2026-09-05-caregiver-alerts-phase3.md
git commit -m "docs(plan): caregiver-alerts Phase-3 implementation plan"
```

---

### Task 2: Core — the `CaregiverMinIntervalMinutes` setting

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs` (modify + append)

**Interfaces:**
- Produces (Tasks 3/6 consume): `double AdaptationSettings.CaregiverMinIntervalMinutes { get; set; }` — default `10.0`, setter guard `[1.0, 60.0]`, included in `Validate()`.

- [ ] **Step 1: Write the failing tests**

In `AdaptationSettingsTests.cs`, first extend the existing `NewDefaults_AreAdaptationNeutralAndValid` fact — after the `CaregiverAlerts` line (`Check.That(s.CaregiverAlerts).IsFalse();`) add:

```csharp
        // Phase-3 caregiver rate limit (§10 resolution): default 10 minutes.
        Check.That(s.CaregiverMinIntervalMinutes).IsEqualTo(10.0);
```

Then append the new fact inside the class:

```csharp
    [Fact]
    public void CaregiverMinInterval_OutOfRange_ThrowsFromSetterGuard()
    {
        var s = new AdaptationSettings();
        Check.ThatCode(() => s.CaregiverMinIntervalMinutes = 0.5).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => s.CaregiverMinIntervalMinutes = 61).Throws<ArgumentOutOfRangeException>();
    }
```

- [ ] **Step 2: Run the suite to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "Category!=UI"`
Expected: `Fehler!` — compile error, `AdaptationSettings` does not define `CaregiverMinIntervalMinutes`.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.Core/Settings/AdaptationSettings.cs`, add the property directly after the `_caregiverAlerts` field's property block (i.e. after the `CaregiverAlerts` `[ObservableProperty]` line):

```csharp
    /// <summary>Minimum minutes between caregiver alert onsets (§10, Phase 3). Default 10; validated [1, 60].</summary>
    [ObservableProperty] private double _caregiverMinIntervalMinutes = 10.0;
```

Add the guard next to the other `On…Changing` partials:

```csharp
    partial void OnCaregiverMinIntervalMinutesChanging(double value) =>
        GuardRange(value, 1.0, 60.0, nameof(CaregiverMinIntervalMinutes));
```

And rewrite `Validate()` so the new range check is the final comma-separated entry of `CollectErrors` — the full method body becomes:

```csharp
    /// <inheritdoc />
    public IReadOnlyList<string> Validate() => ValidationHelper.CollectErrors(
        ValidationHelper.ValidateRange(DwellShorterMaxFraction, 0.0, 1.0, nameof(DwellShorterMaxFraction)),
        ValidationHelper.ValidateRange(DwellLongerMaxFraction, 0.0, 1.0, nameof(DwellLongerMaxFraction)),
        ValidationHelper.ValidateRange(ToleranceLooserMaxFraction, 0.0, 1.0, nameof(ToleranceLooserMaxFraction)),
        ValidationHelper.ValidateRange(SmoothingMoreMaxFraction, 0.0, 1.0, nameof(SmoothingMoreMaxFraction)),
        ValidationHelper.ValidateRange(GeometryBiggerMaxFraction, 0.0, 1.0, nameof(GeometryBiggerMaxFraction)),
        ValidationHelper.ValidateRange(GeometryMaxMarginPx, 0.0, 300.0, nameof(GeometryMaxMarginPx)),
        ValidationHelper.ValidateRange(CaregiverMinIntervalMinutes, 1.0, 60.0, nameof(CaregiverMinIntervalMinutes)));
```

- [ ] **Step 4: Run the suite to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "Category!=UI"`
Expected: `Bestanden!` with `gesamt: 1219` (`erfolgreich: 1218, übersprungen: 1`) — the +1 is the new guard fact; `NewDefaults` was extended in place.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/AdaptationSettings.cs tests/EyeCtrl.Core.Tests/Settings/AdaptationSettingsTests.cs
git commit -m "feat(adaptation): CaregiverMinIntervalMinutes setting (default 10, [1,60])"
```

---

### Task 3: App — `CaregiverAlertService` (the pure decision) + its seams

**Files:**
- Create: `src/EyeCtrl.App/Services/CaregiverAlertCondition.cs`
- Create: `src/EyeCtrl.App/Services/ICaregiverAlertPresenter.cs`
- Create: `src/EyeCtrl.App/Services/CaregiverAlertService.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/CaregiverAlertServiceTests.cs` (new file, 17 facts)

**Interfaces:**
- Consumes: `AdaptationCoordinator.AssessmentUpdated` (existing), `IFeedbackGate.IsFeedbackSuppressed` (existing), `StruggleAssessment` / `FailureMode.PoorTracking` / `FailureModeAssessment` (existing), `AdaptationSettings.CaregiverAlerts` (existing) and `.CaregiverMinIntervalMinutes` (Task 2).
- Produces (Tasks 4/5/6 consume):
  - `enum CaregiverAlertCondition { SustainedStruggle, SustainedPoorTracking }`
  - `interface ICaregiverAlertPresenter { void ShowCaregiverAlert(CaregiverAlertCondition condition); void FadeOutCaregiverAlert(); void HideCaregiverAlert(); }`
  - `CaregiverAlertService(AdaptationSettings adaptation, IFeedbackGate feedbackGate, ICaregiverAlertPresenter presenter, Action<CaregiverAlertCondition>? logAlert = null, Func<long>? nowMs = null)`
  - `bool CaregiverAlertService.OnAssessment(StruggleAssessment assessment)` — true iff an onset fired this tick
  - `void CaregiverAlertService.Start(AdaptationCoordinator coordinator)` (idempotent) / `void Dispose()`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Services/CaregiverAlertServiceTests.cs` with exactly this content (17 facts; the `Run` helper advances a controllable clock 250 ms per tick, mirroring the coordinator cadence):

```csharp
namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class CaregiverAlertServiceTests
{
    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    private sealed class FakePresenter : ICaregiverAlertPresenter
    {
        public List<CaregiverAlertCondition> Shows { get; } = new();
        public int FadeCalls { get; private set; }
        public int HideCalls { get; private set; }
        public void ShowCaregiverAlert(CaregiverAlertCondition condition) => Shows.Add(condition);
        public void FadeOutCaregiverAlert() => FadeCalls++;
        public void HideCaregiverAlert() => HideCalls++;
    }

    // Aggregate-only struggle (no modes): the struggle condition keys off AggregateLevel alone.
    private static StruggleAssessment Struggle(double level) =>
        new(Array.Empty<FailureModeAssessment>(), level);

    // Mirrors the monitor: a gated PoorTracking mode's score feeds the aggregate, so this assessment
    // satisfies BOTH conditions — which is why the service evaluates poor tracking first (D4).
    private static StruggleAssessment PoorTracking(bool gated) => new(
        new[] { new FailureModeAssessment(FailureMode.PoorTracking, Score: 0.6, Confidence: 1.0, GatePassed: gated, Context: null) },
        gated ? 0.6 : 0.0);

    private sealed class Clock
    {
        public long Now;
    }

    private static (CaregiverAlertService Svc, EyeCtrlSettings Settings, FakeFeedbackGate Gate, FakePresenter Presenter, List<CaregiverAlertCondition> Logged, Clock Clock) Make()
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.CaregiverAlerts = true;
        var gate = new FakeFeedbackGate();
        var presenter = new FakePresenter();
        var logged = new List<CaregiverAlertCondition>();
        var clock = new Clock();
        return (new CaregiverAlertService(settings.Adaptation, gate, presenter, logged.Add, () => clock.Now),
            settings, gate, presenter, logged, clock);
    }

    // Drives `ticks` assessments 250 ms apart from the clock's current value.
    private static void Run(CaregiverAlertService svc, Clock clock, StruggleAssessment assessment, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            clock.Now += 250;
            svc.OnAssessment(assessment);
        }
    }

    [Fact]
    public void Disabled_NoOnset_EvenAfterLongSustain()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        settings.Adaptation.CaregiverAlerts = false;
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.8), ticks: 600); // 150 s of sustained struggle
        Check.That(presenter.Shows).IsEmpty();
    }

    [Fact]
    public void StruggleSustained_SixtySeconds_FiresOnceAndLogs()
    {
        var (svc, _, _, presenter, logged, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // the 241st tick reaches exactly 60.00 s of sustain
        Check.That(presenter.Shows).ContainsExactly(CaregiverAlertCondition.SustainedStruggle);
        Check.That(logged).ContainsExactly(CaregiverAlertCondition.SustainedStruggle);
        Run(svc, clock, Struggle(0.6), ticks: 40); // still the same episode — once per episode
        Check.That(presenter.Shows).HasSize(1);
    }

    [Fact]
    public void Onset_AtExactlySixtySeconds_NotAtFiftyNine()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 240); // 60.0 s of ticks, 59.75 s of sustain
        Check.That(presenter.Shows).IsEmpty();
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6)); // sustain reaches exactly 60 s
        Check.That(presenter.Shows).HasSize(1);
    }

    [Fact]
    public void SustainBrokenByDipBelowThreshold_Restarts()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 120); // 30 s
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.4)); // below 0.5 → the continuous hold breaks
        Run(svc, clock, Struggle(0.6), ticks: 240); // 59.75 s of the NEW sustain — cumulative 90+ but not continuous
        Check.That(presenter.Shows).IsEmpty();
        Run(svc, clock, Struggle(0.6), ticks: 1); // the new sustain reaches exactly 60 s
        Check.That(presenter.Shows).HasSize(1);
    }

    [Fact]
    public void PoorTrackingGated_SixtySeconds_FiresPoorTracking()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, PoorTracking(gated: true), ticks: 241);
        // Aggregate is also 0.6 here — poor tracking wins because it is evaluated first (D4).
        Check.That(presenter.Shows).ContainsExactly(CaregiverAlertCondition.SustainedPoorTracking);
    }

    [Fact]
    public void PoorTrackingUngated_NeverFires()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, PoorTracking(gated: false), ticks: 600); // aggregate 0.0 → struggle unmet too
        Check.That(presenter.Shows).IsEmpty();
    }

    [Fact]
    public void RateLimit_SecondEpisodeWithinInterval_DoesNotFire()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // first onset fires
        Check.That(presenter.Shows).HasSize(1);
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.3)); // clear → the window fades
        clock.Now += 31_000; // a proper clear gap — the next episode is FRESH
        Run(svc, clock, Struggle(0.6), ticks: 241); // fully sustained again, ~92 s after the first onset
        Check.That(presenter.Shows).HasSize(1); // but inside the default 10-minute interval
        Check.That(presenter.FadeCalls).IsEqualTo(1);
    }

    [Fact]
    public void RateLimit_AfterIntervalAndClearGap_FiresAgain()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241);
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 10 * 60_000 + 31_000; // interval elapsed AND clear gap satisfied
        Run(svc, clock, Struggle(0.6), ticks: 241);
        Check.That(presenter.Shows).HasSize(2);
    }

    [Fact]
    public void RateLimit_IntervalReadsTheLiveSetting()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241);
        settings.Adaptation.CaregiverMinIntervalMinutes = 1;
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 31_000; // clear gap only — ~92 s total since the first onset
        Run(svc, clock, Struggle(0.6), ticks: 241);
        Check.That(presenter.Shows).HasSize(2); // with the default 10 min this would still be blocked
    }

    [Fact]
    public void ReArm_RequiresThirtySecondClearGap()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        settings.Adaptation.CaregiverMinIntervalMinutes = 1; // take the interval out of the way
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.3)); // clear
        clock.Now += 10_000; // only 10 s clear — below the 30 s re-arm gap
        Run(svc, clock, Struggle(0.6), ticks: 241); // same flare-up: inherits "already alerted"
        Check.That(presenter.Shows).HasSize(1);
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 31_000; // a proper 31 s clear
        Run(svc, clock, Struggle(0.6), ticks: 241);
        Check.That(presenter.Shows).HasSize(2);
    }

    [Fact]
    public void SuppressedOnset_Consumed_EvenAfterSuppressionLifts()
    {
        var (svc, _, gate, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 240); // 59.75 s — the NEXT tick is the onset
        gate.IsFeedbackSuppressed = true;
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6)); // onset due, suppressed → consumed
        gate.IsFeedbackSuppressed = false;
        Run(svc, clock, Struggle(0.6), ticks: 100); // same episode → never shows (§8.10, D6)
        Check.That(presenter.Shows).IsEmpty();
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 31_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fresh episode
        Check.That(presenter.Shows).HasSize(1);
    }

    [Fact]
    public void SuppressedOnset_DoesNotStartTheRateLimitClock()
    {
        var (svc, _, gate, presenter, _, clock) = Make(); // interval default: 10 min
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 240);
        gate.IsFeedbackSuppressed = true;
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6)); // suppressed onset → consumed, NO clock start
        gate.IsFeedbackSuppressed = false;
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 31_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // ≈1.5 min (91.5 s) after the suppressed onset
        Check.That(presenter.Shows).HasSize(1); // fires: had the suppressed onset armed the clock, this would block
    }

    [Fact]
    public void SuppressionMidDisplay_HidesImmediately()
    {
        var (svc, _, gate, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires, window up
        gate.IsFeedbackSuppressed = true;
        svc.OnAssessment(Struggle(0.6)); // still sustained — suppression takes the window down AT ONCE
        Check.That(presenter.HideCalls).IsEqualTo(1);
        Check.That(presenter.FadeCalls).IsEqualTo(0);
    }

    [Fact]
    public void EpisodeClear_FadesTheWindowOut()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires
        svc.OnAssessment(Struggle(0.3)); // natural clear → fade, not hide (D2)
        Check.That(presenter.FadeCalls).IsEqualTo(1);
        Check.That(presenter.HideCalls).IsEqualTo(0);
    }

    [Fact]
    public void NoneAssessment_EndsEpisode_SubsequentSustainStartsFresh()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        settings.Adaptation.CaregiverMinIntervalMinutes = 1; // keep the interval out of the way
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires
        svc.OnAssessment(StruggleAssessment.None); // paused/disconnected → episode ends, window fades
        Check.That(presenter.FadeCalls).IsEqualTo(1);
        clock.Now += 31_000;
        Run(svc, clock, Struggle(0.6), ticks: 240); // 59.75 s of the fresh sustain — no fire yet
        Check.That(presenter.Shows).HasSize(1);
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6));
        Check.That(presenter.Shows).HasSize(2);
    }

    [Fact]
    public void CaregiverToggledOffMidDisplay_HidesImmediately()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires, window up
        settings.Adaptation.CaregiverAlerts = false;
        svc.OnAssessment(Struggle(0.6)); // still sustained — feature-off takes the window down AT ONCE
        Check.That(presenter.HideCalls).IsEqualTo(1);
        Check.That(presenter.FadeCalls).IsEqualTo(0);
    }

    [Fact]
    public void Dispose_ClearsSustainState_NoStaleFireAfterRestart()
    {
        // Round-1 F2: the disable edge (StopRuntime) never raises a None — ResetMomentary sets
        // LastAssessment without invoking, and the timer is disposed synchronously on the
        // setting-change thread — so Dispose is the only place pre-disable sustain state can die.
        // Without the reset, the first post-restart tick below would fire instantly off the stale
        // SustainedSince (10 minutes old), banner + chime + JSONL after zero seconds of struggle.
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 120); // 30 s sustained — below the onset threshold
        svc.Dispose(); // the disable edge
        clock.Now += 10 * 60_000; // a long disable
        Run(svc, clock, Struggle(0.6), ticks: 1); // first tick after re-enable: sustain must restart
        Check.That(presenter.Shows).IsEmpty();
        Run(svc, clock, Struggle(0.6), ticks: 239); // with the restart tick: 240 ticks = 59.75 s of fresh sustain — still nothing
        Check.That(presenter.Shows).IsEmpty();
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6));
        Check.That(presenter.Shows).HasSize(1); // fires at the new episode's own 60 s
    }
}
```

- [ ] **Step 2: Run the suite to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Fehler!` — compile errors (`CaregiverAlertCondition`, `ICaregiverAlertPresenter`, `CaregiverAlertService` do not exist).

- [ ] **Step 3: Implement the three files**

`src/EyeCtrl.App/Services/CaregiverAlertCondition.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>The two conditions that sustain a caregiver alert (design §10, Phase 3). Drift is
/// deliberately absent — its report is the user-actionable in-app banner (§7.1, D7).</summary>
public enum CaregiverAlertCondition
{
    /// <summary>Aggregate struggle level at or above <see cref="CaregiverAlertService.StruggleLevelThreshold"/>, held continuously.</summary>
    SustainedStruggle,

    /// <summary>The <see cref="EyeCtrl.Core.Adaptation.FailureMode.PoorTracking"/> mode gated, held continuously (fires with zero interactions, §6.3).</summary>
    SustainedPoorTracking,
}
```

`src/EyeCtrl.App/Services/ICaregiverAlertPresenter.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>
/// Renders the caregiver alert. Separate from <see cref="CaregiverAlertService"/> so the sustain/
/// rate-limit/suppression decision is headless-testable with a fake; the WPF <c>CaregiverAlertWindow</c>
/// is the runtime implementation (Task 4). The surface is informational only and gaze-safe by
/// construction (click-through, excluded from screen capture).
/// </summary>
public interface ICaregiverAlertPresenter
{
    /// <summary>Shows the alert banner for the given condition (and plays the attention chime).</summary>
    void ShowCaregiverAlert(CaregiverAlertCondition condition);

    /// <summary>Begins the short fade-out used when the condition naturally clears (D2).</summary>
    void FadeOutCaregiverAlert();

    /// <summary>Takes the banner down at once — suppression (§8.10), feature-off, or teardown.</summary>
    void HideCaregiverAlert();
}
```

`src/EyeCtrl.App/Services/CaregiverAlertService.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// Drives the caregiver alert (design §10, Phase 3): a prominent app-owned banner plus an attention
/// chime, shown while high struggle or poor tracking stays <b>sustained</b>. Subscribes to
/// <see cref="AdaptationCoordinator"/>'s per-tick <c>AssessmentUpdated</c> event and consults the same
/// <see cref="IFeedbackGate"/> as the cue/log/drift (§8.10). The decision
/// (<see cref="OnAssessment"/>/<see cref="Track"/>/<see cref="EvaluateOnset"/>) is pure and
/// headless-testable over an injected clock; only <see cref="Start"/>'s event subscription is runtime.
/// </summary>
public sealed class CaregiverAlertService : IDisposable
{
    /// <summary>Aggregate struggle level that sustains the struggle condition. Fixed and
    /// posture-independent: a caregiver channel must not get more sensitive because adaptation is eager (D4).</summary>
    internal const double StruggleLevelThreshold = 0.5;

    /// <summary>How long a condition must be continuously met before an alert onset is due (§10 "sustained").</summary>
    internal const long SustainMs = 60_000;

    /// <summary>How long a condition must stay clear before it may alert again (the re-arm gap, D5).</summary>
    internal const long ClearGapMs = 30_000;

    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly ICaregiverAlertPresenter _presenter;
    private readonly Action<CaregiverAlertCondition>? _logAlert;
    private readonly Func<long> _nowMs;
    private readonly ConditionState _struggle = new();
    private readonly ConditionState _poorTracking = new();
    private CaregiverAlertCondition? _showing; // the condition whose window is currently up
    private bool _hasAlerted; // rate-limit clock flag — TickCount64 has no overflow-safe "never" sentinel
    private long _lastAlertMs;
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    /// <summary>Per-condition sustain/episode state (dispatcher thread only, like the cue's throttle state).</summary>
    private sealed class ConditionState
    {
        public long SustainedSince = long.MinValue; // MinValue = not currently sustained
        public long LastClearedAt = long.MinValue;  // MinValue = never cleared (first episode)
        public bool AlertedThisEpisode;
        public bool SuppressedOnsetConsumed;
    }

    /// <summary>Initializes the service over its settings gate, suppression seam, presenter, and optional log sink.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.CaregiverAlerts"/> gates onsets and
    /// <see cref="AdaptationSettings.CaregiverMinIntervalMinutes"/> is the rate limit.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); the no-op gate never suppresses.</param>
    /// <param name="presenter">The alert renderer (the WPF window at runtime, a fake in tests).</param>
    /// <param name="logAlert">Invoked once per unsuppressed alert onset — the JSONL hook (D8).</param>
    /// <param name="nowMs">Monotonic millisecond clock; injectable so sustain/rate-limit timing is headless-testable.</param>
    public CaregiverAlertService(AdaptationSettings adaptation, IFeedbackGate feedbackGate,
        ICaregiverAlertPresenter presenter, Action<CaregiverAlertCondition>? logAlert = null,
        Func<long>? nowMs = null)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
        _presenter = presenter;
        _logAlert = logAlert;
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
    }

    /// <summary>
    /// Handles one assessment: tracks both conditions' sustain state, takes the window down on
    /// suppression/feature-off (immediately) and on episode end (fade), and fires at most one alert
    /// onset per episode, subject to the global rate limit.
    /// </summary>
    /// <param name="assessment">The current struggle assessment (None on non-assessing ticks).</param>
    /// <returns>True iff an alert onset fired this tick.</returns>
    public bool OnAssessment(StruggleAssessment assessment)
    {
        long now = _nowMs();
        bool none = assessment.Modes.Count == 0 && assessment.AggregateLevel <= 0.0;

        // Hard visibility gates first: take the window down AT ONCE (never fade) when feedback is
        // suppressed (§8.10) or the feature was switched off mid-display. The window cannot reappear
        // for this episode — its onset is already marked alerted — so there is no suppression-lift pop.
        if (_showing is { } && (!_adaptation.CaregiverAlerts || _feedbackGate.IsFeedbackSuppressed))
        {
            _showing = null;
            _presenter.HideCaregiverAlert();
        }

        Track(_struggle, met: !none && assessment.AggregateLevel >= StruggleLevelThreshold, now);
        Track(_poorTracking, met: !none && assessment.ForMode(FailureMode.PoorTracking) is { GatePassed: true }, now);

        // The window fades (~1 s) when the showing condition's episode ends — including the None
        // assessments the coordinator sends while paused/disconnected/disabled.
        if (_showing is { } showing && !IsSustained(showing))
        {
            _showing = null;
            _presenter.FadeOutCaregiverAlert();
        }

        // Poor tracking is evaluated first: when both conditions are due it is the more specific,
        // more actionable cause (and the one that fires with zero interactions, §6.3). With the
        // short-circuit, the struggle onset is not even evaluated on a tick where poor tracking
        // fired; on subsequent ticks the global rate-limit clock blocks it (round-1 F7).
        return EvaluateOnset(_poorTracking, CaregiverAlertCondition.SustainedPoorTracking, now)
            || EvaluateOnset(_struggle, CaregiverAlertCondition.SustainedStruggle, now);
    }

    // Updates one condition's sustain/episode state. met=false ends the episode; met=true starts (or
    // continues) one. An episode starting < ClearGapMs after the previous one cleared is the SAME
    // flare-up and inherits its alerted/consumed state, so a flapping condition cannot re-alert by
    // clearing for a moment (D5).
    private void Track(ConditionState state, bool met, long now)
    {
        if (met)
        {
            if (state.SustainedSince == long.MinValue)
            {
                bool sameFlareUp = state.LastClearedAt != long.MinValue
                    && now - state.LastClearedAt < ClearGapMs;
                if (!sameFlareUp)
                {
                    state.AlertedThisEpisode = false;
                    state.SuppressedOnsetConsumed = false;
                }
                state.SustainedSince = now;
            }
        }
        else if (state.SustainedSince != long.MinValue)
        {
            state.SustainedSince = long.MinValue;
            state.LastClearedAt = now;
        }
    }

    // Fires this condition's alert onset if it is due: sustained >= SustainMs, this episode not yet
    // alerted/consumed, the feature on, feedback not suppressed, and the global interval elapsed.
    // A rate-limit block DELAYS the onset (retried every tick while sustained) rather than consuming
    // it — a safety channel must eventually surface a long episode (D5). A suppressed onset is
    // CONSUMED (§8.10, the DriftNoticeService trade) and must not start the rate-limit clock, or a
    // password-adjacent struggle would silently lock the channel for the whole interval (D6).
    private bool EvaluateOnset(ConditionState state, CaregiverAlertCondition condition, long now)
    {
        if (state.SustainedSince == long.MinValue
            || state.AlertedThisEpisode
            || state.SuppressedOnsetConsumed
            || now - state.SustainedSince < SustainMs
            || !_adaptation.CaregiverAlerts)
        {
            return false;
        }

        if (_feedbackGate.IsFeedbackSuppressed)
        {
            state.SuppressedOnsetConsumed = true;
            return false;
        }

        if (_hasAlerted && now - _lastAlertMs < (long)(_adaptation.CaregiverMinIntervalMinutes * 60_000.0))
            return false;

        state.AlertedThisEpisode = true;
        _hasAlerted = true;
        _lastAlertMs = now;
        _showing = condition;
        _presenter.ShowCaregiverAlert(condition);
        _logAlert?.Invoke(condition);
        Log.Information("Adaptation: caregiver alert shown ({Condition})", condition);
        return true;
    }

    private bool IsSustained(CaregiverAlertCondition condition) =>
        StateFor(condition).SustainedSince != long.MinValue;

    private ConditionState StateFor(CaregiverAlertCondition condition) =>
        condition == CaregiverAlertCondition.SustainedPoorTracking ? _poorTracking : _struggle;

    /// <summary>Subscribes to the coordinator's per-tick assessment stream. Call once on the UI thread; idempotent.</summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> event drives the alert.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return;
        _coordinator = coordinator;
        coordinator.AssessmentUpdated += OnAssessmentEvent;
    }

    private void OnAssessmentEvent(StruggleAssessment assessment) => OnAssessment(assessment);

    /// <summary>
    /// Unsubscribes, takes the window down without fading, and resets both conditions' sustain/
    /// episode state. The disable edge never delivers a None — StopRuntime's ResetMomentary sets
    /// LastAssessment without raising, and the timer is disposed synchronously — so this is the
    /// only place pre-disable sustain can die; without the reset, a stale sustain fires instantly
    /// on the first re-enabled tick (round-1 F2). The rate-limit clock deliberately SURVIVES — the
    /// singleton outlives enable/disable toggles, and toggling adaptation off and on must not
    /// bypass the interval (D11).
    /// </summary>
    public void Dispose()
    {
        if (_coordinator is not null)
        {
            _coordinator.AssessmentUpdated -= OnAssessmentEvent;
            _coordinator = null;
        }
        if (_showing is { })
        {
            _showing = null;
            _presenter.HideCaregiverAlert();
        }
        Reset(_struggle);
        Reset(_poorTracking);
    }

    private static void Reset(ConditionState state)
    {
        state.SustainedSince = long.MinValue;
        state.LastClearedAt = long.MinValue;
        state.AlertedThisEpisode = false;
        state.SuppressedOnsetConsumed = false;
    }
}
```

- [ ] **Step 4: Run the suite to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Bestanden!` with `gesamt: 795` (778 + the 17 new facts).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/CaregiverAlertCondition.cs src/EyeCtrl.App/Services/ICaregiverAlertPresenter.cs src/EyeCtrl.App/Services/CaregiverAlertService.cs tests/EyeCtrl.App.Tests/Services/CaregiverAlertServiceTests.cs
git commit -m "feat(adaptation): CaregiverAlertService — sustained-condition decision (pure, headless-tested)"
```

---

### Task 4: App — the `CaregiverAlertWindow` (+ the overlay inventory row that keeps the drift tests green)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml.cs`
- Modify: `docs/design/overlay-unification-spec.md` (§3.1 row + §1 counts + finding-paragraph arithmetic)
- Modify: `tests/EyeCtrl.App.Tests/OverlayResourceResolutionTests.cs` (+1 `MigratedOverlays` case)

**Interfaces:**
- Consumes: `ICaregiverAlertPresenter` (Task 3), `OverlayWindow` (`IsClickThrough`, `ExcludeFromScreenCapture`, `RaiseToTop` — the `DriftNoticeWindow` usage), theme resources `BrushSurfaceDark`/`BrushBorderSubtle`/`BrushTextPrimary`/`BrushTextSecondary`/`StandardBorderThickness`/`StandardCornerRadius`/`FontSizeTitle`/`FontSizeBodyLarge` (all defined in `src/EyeCtrl.Controls/Theme/OverlayStyles.xaml`).
- Produces (Task 6 consumes): `CaregiverAlertWindow : OverlayWindow, ICaregiverAlertPresenter` — parameterless ctor.

One test-file touch and no new test *methods*: **+1 `[StaTheory]` case in `OverlayResourceResolutionTests.MigratedOverlays`** — the repo's sanctioned window-constructing sweep (`[Collection("WpfApplication")]`, constructs each listed overlay via its first ctor and closes it, never shows; runs headlessly under `Category!=UI` — round-1 F3: without it, a misspelled or type-wrong `{StaticResource}` key first throws inside `AdaptationActivation.StartRuntime`, whose catch converts the failure to "adaptation stays inert" — silently killing ALL adaptation on enable with every suite green). Beyond that the window is a WPF surface behind the presenter seam (the `DriftNoticeWindow` precedent has no dedicated fact); the three `OverlayInventoryDriftTests` facts keep the spec file honest and must stay green **in the same task**.

- [ ] **Step 1: Write the XAML**

`src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml`:

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.CaregiverAlertWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        AutomationProperties.AutomationId="CaregiverAlert_Window"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        Width="720" Height="120">
    <Grid>
        <Border x:Name="Banner"
                HorizontalAlignment="Center" VerticalAlignment="Center"
                Background="{StaticResource BrushSurfaceDark}"
                BorderBrush="{StaticResource BrushBorderSubtle}"
                BorderThickness="{StaticResource StandardBorderThickness}"
                CornerRadius="{StaticResource StandardCornerRadius}" Padding="24,14" Opacity="0">
            <StackPanel>
                <TextBlock Text="Caregiver attention needed"
                           Foreground="{StaticResource BrushTextPrimary}"
                           FontSize="{StaticResource FontSizeTitle}" FontWeight="{StaticResource FontWeightStrong}" TextAlignment="Center"/>
                <TextBlock x:Name="Message"
                           AutomationProperties.AutomationId="CaregiverAlert_Message"
                           Foreground="{StaticResource BrushTextSecondary}"
                           FontSize="{StaticResource FontSizeBodyLarge}" TextAlignment="Center"
                           Margin="0,6,0,0" TextWrapping="Wrap"/>
            </StackPanel>
        </Border>
    </Grid>
</overlays:OverlayWindow>
```

**Census gate (round-2 F3):** raw geometry/type literals — `CornerRadius`, `FontFamily`, `FontSize`, `FontWeight` written as `="…"` values — are forbidden by `RawGeometryLiteralTests.NoUIFile_WritesAGeometryOrTypeValueOutsideTheTokens`. Always use the token resources; for the heading that is `FontWeight="{StaticResource FontWeightStrong}"`, the `TrackStatusWindow` precedent (all three themes map it to `FontWeights.Bold`, so the rendering is identical and the `OverlayResourceResolutionTests` case passes).

- [ ] **Step 2: Write the code-behind**

`src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml.cs`:

```csharp
namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using EyeCtrl.App.Services;
using Serilog;

/// <summary>
/// The Phase-3 caregiver alert banner (design §10 resolution): a bottom-center banner shown while
/// high struggle or poor tracking stays sustained (the service drives visibility), fading ~1 s after
/// the condition clears. An app-owned window, NOT an OS notification — Windows notifications can be
/// silenced by Focus Assist / per-app settings, which is disqualifying for a caregiver safety channel
/// (owner decision, D1). Like <see cref="DriftNoticeWindow"/> it is click-through and
/// <see cref="OverlayWindow.ExcludeFromScreenCapture"/>=true, so it never intercepts gaze/mouse (no
/// dwell targets — zero mis-activation surface) and is not baked into the magnifier / action-menu
/// capture. The chime plays once per onset (D3).
/// </summary>
public partial class CaregiverAlertWindow : OverlayWindow, ICaregiverAlertPresenter
{
    private const double MarginDip = 48;
    private static readonly Duration FadeOut = new(TimeSpan.FromSeconds(1));
    private bool _positioned;

    /// <summary>Creates the caregiver-alert window (click-through + excluded from screen capture, set before init).</summary>
    public CaregiverAlertWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void ShowCaregiverAlert(CaregiverAlertCondition condition)
    {
        Message.Text = condition == CaregiverAlertCondition.SustainedPoorTracking
            ? "Eye tracking has been unreliable for the last minute — consider repositioning or re-calibrating."
            : "The user has been struggling continuously — consider checking in.";
        EnsurePositioned();
        // Cancel any in-flight fade and re-show at full opacity (the drift banner's re-show discipline:
        // a fade is started only by FadeOutCaregiverAlert, and removing a clock via BeginAnimation(dp, null)
        // does not raise the removed clock's Completed, so a re-show during a fade cannot leave a stale
        // Completed to hide the banner).
        Banner.BeginAnimation(UIElement.OpacityProperty, null);
        Banner.Opacity = 1.0;
        if (!IsVisible)
            base.Show();
        RaiseToTop();

        try
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Caregiver alert chime failed; the visual alert was shown.");
        }
    }

    /// <inheritdoc />
    public void FadeOutCaregiverAlert()
    {
        var fade = new DoubleAnimation(1.0, 0.0, FadeOut) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => Hide();
        Banner.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <inheritdoc />
    public void HideCaregiverAlert()
    {
        Banner.BeginAnimation(UIElement.OpacityProperty, null); // cancel any in-flight fade — down AT ONCE
        Hide();
    }

    private void EnsurePositioned()
    {
        if (_positioned)
            return;
        var area = SystemParameters.WorkArea; // DIPs, matching Window.Left/Top/Width/Height
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - MarginDip;
        _positioned = true;
    }
}
```

- [ ] **Step 3: Update the overlay inventory spec (same task — the drift tests read this file)**

In `docs/design/overlay-unification-spec.md`:

1. In the §3.1 table, add this row directly after the `DriftNotice` row (keeping the adaptation-family grouping and its bold-name style):

```markdown
| **CaregiverAlert** | `CaregiverAlertWindow` | `Transparent` + `Border #E0202020` | XAML |
```

2. In §1's opening sentence, change `EyeCtrl has 25 overlay windows and 7 dwell controls — 23 with XAML under` to `EyeCtrl has 26 overlay windows and 7 dwell controls — 24 with XAML under` (the two numbers are coupled: the new window is XAML-format).

3. §3.1's finding paragraph also does its own overlay arithmetic (round-1 F4 — not test-enforced, but the section self-contradicts otherwise): change `**Finding:** 14 overlays carry a dark fill; 11 are `Background="Transparent"` at the window level.` to `…; 12 are `Background="Transparent"` at the window level.` (14 + 12 = 26), and change `Two of the eleven (`AdaptationCue`, `DriftNotice`) are transparent windows hosting a *filled* shape, which is the case §4.1.3 originally missed.` to `Three of the twelve (`AdaptationCue`, `DriftNotice`, `CaregiverAlert`) are transparent windows hosting a *filled* shape…`.

(`OverlayInventoryDriftTests` matches the **Class** column against every concrete `OverlayWindow` subclass and the §1 count against the subclass count — the only test-enforced totals.)

- [ ] **Step 4: Add the window to the resource-resolution sweep (round-1 F3)**

In `tests/EyeCtrl.App.Tests/OverlayResourceResolutionTests.cs`, add `typeof(CaregiverAlertWindow)` to the `MigratedOverlays` list — in the adaptation-family cluster next to `DriftNoticeWindow`:

```csharp
        typeof(DriftNoticeWindow), typeof(CaregiverAlertWindow), typeof(TargetHighlightWindow),
```

`CaregiverAlertWindow` has a parameterless constructor, so the sweep's `Type.Missing` activation works unchanged. The `[StaTheory]` case counts as exactly one additional test result.

- [ ] **Step 5: Run the suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Bestanden!` with `gesamt: 796` (Task 3's 795 + the one new `OverlayResourceResolutionTests` theory case; `OverlayInventoryDriftTests` stays green via the spec edit).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml.cs docs/design/overlay-unification-spec.md tests/EyeCtrl.App.Tests/OverlayResourceResolutionTests.cs
git commit -m "feat(adaptation): CaregiverAlertWindow banner + chime; overlay inventory row"
```

---

### Task 5: App — `AdaptationLog.LogCaregiverAlert` (the JSONL entry)

**Files:**
- Modify: `src/EyeCtrl.App/Services/AdaptationLog.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs` (append one fact)

**Interfaces:**
- Consumes: `WriteJsonl` (existing private writer with rotation), `CaregiverAlertCondition` (Task 3).
- Produces (Task 6 consumes): `void AdaptationLog.LogCaregiverAlert(CaregiverAlertCondition condition)` — appended verbatim as the DI-injected `logAlert` callback.

- [ ] **Step 1: Write the failing test**

Append inside `AdaptationLogTests` (the file already carries the needed usings — `System.IO`, `EyeCtrl.App.Services`, `EyeCtrl.Core.Adaptation`, `EyeCtrl.Core.Settings`):

```csharp
    [Fact]
    public void LogCaregiverAlert_WritesKindCaregiverEntry()
    {
        string path = TempLogPath();
        try
        {
            var settings = new EyeCtrlSettings();
            var log = new AdaptationLog(settings.Adaptation, new FakeFeedbackGate(),
                new EffectiveSettingsProvider(settings.Adaptation), jsonlPath: path);

            log.LogCaregiverAlert(CaregiverAlertCondition.SustainedPoorTracking);

            var line = File.ReadAllLines(path).Single();
            Check.That(line).Contains("\"kind\":\"caregiver\"");
            Check.That(line).Contains("\"condition\":\"SustainedPoorTracking\"");
        }
        finally
        {
            File.Delete(path);
        }
    }
```

- [ ] **Step 2: Run the suite to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Fehler!` — compile error, `AdaptationLog` has no `LogCaregiverAlert`.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.App/Services/AdaptationLog.cs`, add this public method after `OnAssessment` (before the `OnProviderChanged` region):

```csharp
    /// <summary>
    /// Appends a <c>kind:"caregiver"</c> entry for one alert onset (§10 Phase-3 resolution). Called by
    /// <see cref="CaregiverAlertService"/> via its injected callback on UNSUPPRESSED onsets only — a
    /// suppressed onset writes nothing, so the file never carries a timing trace of credential-adjacent
    /// struggle (§8.10, the same discipline as <see cref="OnProviderChanged"/>'s suppressed-marker rule).
    /// Rides the same size cap and rotation as every other entry.
    /// </summary>
    /// <param name="condition">Which sustained condition raised the alert.</param>
    public void LogCaregiverAlert(CaregiverAlertCondition condition) =>
        WriteJsonl(new { ts = DateTimeOffset.UtcNow, kind = "caregiver", condition = condition.ToString() });
```

- [ ] **Step 4: Run the suite to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Bestanden!` with `gesamt: 797` (796 + 1).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/AdaptationLog.cs tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs
git commit -m "feat(adaptation): kind:\"caregiver\" JSONL entries"
```

---

### Task 6: App — wiring (DI, activation edge, settings rows)

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (DI, after the `DriftNoticeService` registration at the end of the adaptation block)
- Modify: `src/EyeCtrl.App/Services/AdaptationActivation.cs` (Start/Stop + doc comment)
- Modify: `src/EyeCtrl.App/App.xaml.cs` (comment only)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs` (two rows + reset copies)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (remove the dead audit line)
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs` (three facts modified — verbatim below)
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs` (slider count + span pin — round-1 F1)

**Interfaces:**
- Consumes: everything Tasks 2–5 produced.
- Produces: the complete runtime path (Enabled edge → activation → service start → assessment stream → window) and the two settings rows.

- [ ] **Step 1: Modify the page tests first (they pin the row surface)**

In `AdaptationPageSourceTests.cs`:

**Fact 1** — replace `Build_HasSeventeenRows_ThreeButtons_NoCaregiverRow` entirely with:

```csharp
    [Fact]
    public void Build_HasNineteenRows_ThreeButtons_CaregiverRows()
    {
        var rows = new AdaptationPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        Check.That(rows).HasSize(19);
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id).ToList())
            .ContainsExactly("ad_reset", "ad_reset_learned", "ad_log");
        Check.That(rows.Select(r => r.Id).ToList())
            .Contains("ad_caregiver", "ad_caregiver_interval");
    }
```

**Fact 3 (`Setters_WriteLiveSettings`)** — add after the `ad_geom_margin` assertions:

```csharp
        toggles["ad_caregiver"].Set(true);
        Check.That(settings.Adaptation.CaregiverAlerts).IsTrue();
        sliders["ad_caregiver_interval"].Set(15);
        Check.That(settings.Adaptation.CaregiverMinIntervalMinutes).IsEqualTo(15);
```

**Fact 4 (`Reset_CopiesAllFourteenFromDefaults`)** — rename to `Reset_CopiesAllSixteenFromDefaults`; add to the pre-reset mutation block (after `settings.Adaptation.SuppressInSensitiveContexts = false;`):

```csharp
        settings.Adaptation.CaregiverAlerts = true;
        settings.Adaptation.CaregiverMinIntervalMinutes = 20;
```

and add to the assertion block (after the `SuppressInSensitiveContexts` check, keeping the defaults-object order):

```csharp
        Check.That(settings.Adaptation.CaregiverAlerts).IsEqualTo(d.CaregiverAlerts);
        Check.That(settings.Adaptation.CaregiverMinIntervalMinutes).IsEqualTo(d.CaregiverMinIntervalMinutes);
```

**`SettingsSliderSpanGuardTests.Adaptation_SliderSpans_StayInsideTheValidatedRanges`** (round-1 F1 — this fact pins the Adaptation page's slider count and would fail at Step 5 otherwise): change `Check.That(sliders).HasSize(6);` to `Check.That(sliders).HasSize(7);`, and append one span pin after the `ad_geom_margin` line:

```csharp
        SpanInside(sliders, "ad_caregiver_interval", 1, 60);
```

- [ ] **Step 2: Run the page tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Fehler!` — the row-count fact (19 vs 17) and the Setters/Reset facts fail on the missing rows/copies.

- [ ] **Step 3: Implement the page rows**

In `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs`, insert after the `ad_sensitive` toggle's closing `},` and before the `ad_frac_dwell_short` slider:

```csharp
                new ToggleSpec("ad_caregiver", "Caregiver Alerts",
                    () => s.CaregiverAlerts, v => { s.CaregiverAlerts = v; _ctx.ApplyAndSave(); })
                    { Description = "Show a prominent alert for a caregiver while struggle or poor tracking persists." },
                new SliderSpec("ad_caregiver_interval", "Caregiver Alerts — Minimum Interval", 1.0, 60.0, 1.0, 0,
                    () => s.CaregiverMinIntervalMinutes, v => { s.CaregiverMinIntervalMinutes = v; _ctx.ApplyAndSave(); })
                    { Description = "The least time between two caregiver alerts, in minutes." },
```

In the `ad_reset` modal's copy block, add after `s.SuppressInSensitiveContexts = d.SuppressInSensitiveContexts;`:

```csharp
                        s.CaregiverAlerts = d.CaregiverAlerts;
                        s.CaregiverMinIntervalMinutes = d.CaregiverMinIntervalMinutes;
```

- [ ] **Step 4: Wire the runtime**

**`src/EyeCtrl.App/Services/AppBootstrapper.cs`** — after the `DriftNoticeService` registration (the last line of the adaptation DI block), add:

```csharp
        // Phase-3 caregiver alert (§10 resolution): the app-owned banner + chime. Same pattern as the
        // drift notice — the window is a DI singleton (constructed lazily on the first Enabled edge,
        // never while disabled) and doubles as the presenter; the service is the pure decision layer
        // started/stopped by AdaptationActivation. The log callback is the JSONL hook (D8).
        services.AddSingleton<CaregiverAlertWindow>();
        services.AddSingleton<ICaregiverAlertPresenter>(sp => sp.GetRequiredService<CaregiverAlertWindow>());
        services.AddSingleton(sp => new CaregiverAlertService(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IFeedbackGate>(),
            sp.GetRequiredService<ICaregiverAlertPresenter>(),
            sp.GetRequiredService<AdaptationLog>().LogCaregiverAlert));
```

**`src/EyeCtrl.App/Services/AdaptationActivation.cs`** — in `StartRuntime`, after the `DriftNoticeService` start line, add:

```csharp
        services.GetRequiredService<CaregiverAlertService>().Start(coordinator);
```

In `StopRuntime`, after the `DriftNoticeService` dispose line, add:

```csharp
        services.GetRequiredService<CaregiverAlertService>().Dispose();
```

And in the class doc comment, update "its three assessment subscribers" to "its four assessment subscribers".

**`src/EyeCtrl.App/App.xaml.cs`** — in the adaptation comment block (~line 341), extend "the cue / drift-notice / log subscribers" to "the cue / drift-notice / log / caregiver-alert subscribers". Comment only.

**`src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`** — delete the now-false audit line `//  - Adaptation.CaregiverAlerts (no consumer until Phase 3)` from the INTENTIONALLY NOT EXPOSED list.

- [ ] **Step 5: Run the suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: `Bestanden!` with `gesamt: 797` (no new facts this task — the three page facts and the span-guard fact were modified in place).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/AdaptationActivation.cs src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs
git commit -m "feat(adaptation): wire caregiver alerts — DI, activation edge, settings rows"
```

---

### Task 7: Spec amendment, AGENTS.md fix, full verification

**Files:**
- Modify: `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md`
- Modify: `AGENTS.md`
- Verify: all four suites + graph change analysis

- [ ] **Step 1: Amend the design spec**

1. **§10 — append the Phase-3 resolution at the END of §10** (after the **`AdaptationSettings`** bullet — the Phase-2 §7.4 precedent: resolution blocks land at the section's end, and inserting after the caregiver bullet would split the Markdown list; round-1 F5):

```markdown
**Phase-3 resolution (2026-09-05, plan `2026-09-05-caregiver-alerts-phase3.md`).** The v1 alert is an
**app-owned banner window + attention chime, NOT an OS notification** (owner decision: Windows
notifications — balloon or WinRT toast, the wording above — can be silenced by Focus Assist / per-app
settings, which is disqualifying for a caregiver safety channel). `CaregiverAlertWindow` follows the
`DriftNoticeWindow` pattern (topmost, click-through, excluded from magnifier capture, gaze-safe), shows
**while the condition stays sustained** and fades ~1 s after it clears; the chime
(`SystemSounds.Exclamation`) plays once per onset. Two conditions (owner-settled): **aggregate struggle
level ≥ 0.5 held continuously 60 s** (fixed threshold, posture-independent) and **PoorTracking gated
60 s** — poor tracking is evaluated first when both hold (the more specific cause; it fires with zero
interactions, §6.3). Rate limit: one **global** clock, `AdaptationSettings.CaregiverMinIntervalMinutes`
(default 10 min, validated [1, 60], exposed as an Adaptation-page slider), plus a **≥ 30 s clear gap**
before a condition may re-alert (an episode starting inside the gap is the same flare-up); a
rate-limit block *delays* the onset (retried each tick while sustained) rather than consuming it.
§8.10: an onset arriving while feedback is suppressed is **consumed** — no window that episode even
after suppression lifts (the DriftNoticeService trade) — and does **not** start the rate-limit clock;
suppression or feature-off mid-display hides the window immediately. Alert onsets append
`kind:"caregiver"` entries (`ts` + condition) to `adaptation-log.jsonl` (suppressed onsets write
nothing). Drift deliberately does **not** reach the caregiver channel (user-actionable in-app, §7.1).
The service is the cue/log/drift shape: subscribes `AssessmentUpdated`, consults `IFeedbackGate`, a
pure headless-testable decision over an injected clock, an `ICaregiverAlertPresenter` seam, and
start/stop by `AdaptationActivation` on the `Enabled` edge.
```

2. **§13 Phase-3 line** — append: `**(Complete 2026-09-05 — caregiver-alert slice; see the §10 resolution. The optional Supervisor relay stays deferred, §14.)**` (the geometry-slice precedent: stamped in this slice's own spec task on the branch, pre-merge).

3. **§14 supervisor-relay bullet** — append one sentence: `Phase 3 (2026-09-05) deferred the relay out of v1: the app-owned window + chime shipped alone; a relay needs its own pipe channel (never \`eyectrl_watch\`), a supervisor-side consumption design, and would reopen the single-writer question — its own slice.`

4. **§3 key-decisions table** — in the `Caregiver channel (v1)` row's Choice cell, append: ` — realized as an app-owned banner + chime (Phase 3): OS notifications rejected as suppressible (§10 resolution)`.

- [ ] **Step 2: Fix the AGENTS.md InternalsVisibleTo claim (carried item a)**

Replace the line

```markdown
- `InternalsVisibleTo` is only declared from `EyeCtrl.Core` → `EyeCtrl.Core.Tests`
```

with

```markdown
- `InternalsVisibleTo` is declared from `EyeCtrl.Core` → `EyeCtrl.Core.Tests` and from `EyeCtrl.App` → `EyeCtrl.App.Tests` (`EyeCtrl.App.csproj`)
```

- [ ] **Step 3: Full verification — all four suites, sequential, one at a time**

Check the box is quiet first (`Get-Process -Name dotnet,testhost*,EyeCtrl*`), then:

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"
dotnet test tests/EyeCtrl.Benchmarks.Tests --filter "Category!=UI"
```

Expected, in order: `Bestanden!` **gesamt: 1219** (`erfolgreich: 1218, übersprungen: 1`) / **300** / **797** / **27**.

- [ ] **Step 4: Graph change analysis (AGENTS gate)**

`.gitnexus/run.cjs` does not exist in this worktree — use the MCP tool with the worktree parameter: `detect_changes({scope: "all", worktree: "C:/Users/andre/source/repos/eyectrl/.worktrees/caregiver-alerts"})`. `partial: true` or `truncated: true` is not a clean check; re-run until clean. Expectation: changes confined to the adaptation feedback services (new caregiver files, `AdaptationLog`, `AdaptationActivation`, `AppBootstrapper`), the settings page source, Core `AdaptationSettings`, and the two spec/AGENTS doc files; nothing in the gaze-pipeline hot path.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md AGENTS.md
git commit -m "docs(spec): record the Phase-3 caregiver resolution (§10); fix AGENTS InternalsVisibleTo"
```

---

## Notes for the implementer

- **Read Global Constraints first** — especially the ten owner decisions and the additive-only rule. The two most expensive mistakes this plan guards against: (1) reaching for `NotifyIcon.ShowBalloonTip` or any OS notification (explicitly rejected by the owner — D1), and (2) letting a suppressed onset arm the rate-limit clock or leave the window up (the two §8.10 leaks — D6).
- **German output**: read the `Bestanden!`/`Fehler!` summary line, never grep "Failed". One suite at a time; check the box is quiet before each run.
- The service tests are wall-clock-free — every timing fact drives the injected clock; if one fails, suspect the state machine order in `OnAssessment` (visibility gates → `Track` → fade-check → onsets), not the arithmetic.
- `AdaptationPageSource`'s exact ctor style must match what the file uses today; this plan adds no ctor parameters — only the two rows and the two reset copies.
- **Do not** "improve" `AdaptationCoordinator`, `StruggleMonitor`, `AdaptationController`, any router, `TrayIconManager`, or `HeartbeatPublisher` while in there. If a task's expected count is off by one, recount before adapting — the deltas are: Core +1 (guard fact; `NewDefaults` extended in place), App +17 (service facts, including the round-1 F2 Dispose fact) +1 (`OverlayResourceResolutionTests` theory case, F3) +1 (log fact), and the three page facts plus the span-guard fact modified in place (F1). Interim App chain: 778 → 795 → 796 → 797 → 797.
- The `ICaregiverAlertPresenter` fake in Task 3 is the only test-side presenter; **never** resolve `CaregiverAlertWindow` or `ICaregiverAlertPresenter` from the container in a test (constructing a WPF `Window` off an STA/dispatcher context is exactly what the fake exists to avoid — and the reason there is deliberately no DI registration fact in `TelemetrySinkRegistrationTests` for this service, mirroring cue/drift).
- If `OverlayInventoryDriftTests` fails after Task 4, the §3.1 Class cell must be exactly `` `CaregiverAlertWindow` `` (backticked) and the §1 count must equal the concrete-subclass count — recount the declared overlays rather than editing the test.
