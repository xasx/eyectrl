# Adaptation Preview & Reset Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A live adaptation-status preview flyout on the toolbar's Reset button (seen-latch freeze, never able to fire while being read) and hiding the Reset affordances when adaptation is disabled — per spec `docs/superpowers/specs/2026-10-13-adaptation-preview-and-reset-visibility-design.md` r6 (`45cb7917`).

**Architecture:** A pure ViewModels-layer state machine (`ResetPreviewEngine`) owns the freeze/latch/debounce/expiry/bounds protocol; `ToolbarViewModel` feeds it gaze samples (with their timestamps) and progress events and publishes messenger messages; an App-side service (`AdaptationPreviewService`, an `AdaptationActivation` participant) owns the window, anchoring, the D17 collision inventory, throttled content, and auto-hide/offer suspension. Core gains only pure builders. Feature 2 (hide Reset) rides the existing band-rebuild path with a new `SetRenderedButtons` seam.

**Tech Stack:** .NET 10 WPF (App), plain net10.0 (Core, ViewModels), CommunityToolkit.Mvvm, xUnit v3 + NFluent 3.1.0.

**Plan revision:** rev 2 (`P1`–`P18` from implementation-plan review 01 folded in; ledger at the end).

## Global Constraints

- Spec decisions D1–D18 are binding; the ledger is in spec §2/§10. Key constants verbatim: trigger **0.40** of the Reset dwell; leave debounce **120 ms** continuous off-union; arm expiry **3 s** judged at the start of the return; bounds-unknown timeout **500 ms** (cancels the panel); refresh throttle **≥ 500 ms**.
- **All engine timing is sample-timestamp time** (`long` microseconds, the gaze message's `TimestampMicroseconds`), never wall clock — the same clock `DwellDetector` and the Pause re-arm latch use (P4).
- **State is committed before any event is raised** — messenger delivery is synchronous, so a subscriber (the service) can re-enter the engine (`OnBounds`) inside a transition; every event field must already reflect the committed state (P18).
- `EyeCtrl.ViewModels` is plain `net10.0` — **no WPF types**; rects crossing VM↔App are `EyeCtrl.Core.Models.PhysicalRect` in physical pixels (the type already exists — extend, never replace, P1).
- Tests use **NFluent** (`Check.That(...)`), xUnit v3; private nested fakes, no mocking library. `IsCloseTo` delta is ABSOLUTE.
- One test suite at a time on this box; never run two `dotnet test` concurrently.
- Strings: new keys under `overlays.adaptationPreview.*` in `src/EyeCtrl.Core/Localization/strings.json`, **English only** (surface `overlays`; German is T13's). No new theme palette entries — existing Tokens only.
- All work in the worktree `.worktrees/adaptation-preview` (branch `feat/adaptation-preview`). Commit per task. No pushes unless asked.
- Every window show/hide/refresh in the service is try/catch (log-and-skip) and publishes `NoPanel` bounds on failure — a comfort feature must never take down the session.
- Build gate per commit: `dotnet build EyeCtrl.slnx` + the touched project's tests.
- Test paths: `ToolbarButtonVisualTests.cs` / `ToolbarButtonDriftTests.cs` live at the **root** of `tests/EyeCtrl.App.Tests/`; `DwellToggleVisualTests` is in `EyeCtrl.Controls.Tests`; `SettingsUiCoordinatorTests` is in `Services/Coordinators/` (P17).

---

### Task 1: Extend `PhysicalRect` (Core.Models — the type ALREADY EXISTS)

**Files:**
- Modify: `src/EyeCtrl.Core/Models/PhysicalRect.cs` (exists since `6daeadd0`; ~24 files use it — keep every existing member: `CenterX`, `CenterY`, `Center`, `Contains(double, double)`)
- Test: create `tests/EyeCtrl.Core.Tests/Models/PhysicalRectGeometryTests.cs` (a sibling of the existing `PhysicalRectContainsTests`)

**Interfaces:**
- Adds: `bool Contains(PhysicalPoint point)` (delegates to the existing overload), `bool IntersectsWith(PhysicalRect other)` — **strict**: touching edges do NOT intersect (D17 must not fire on tangency), `static PhysicalRect FromCenter(PhysicalPoint center, double width, double height)`. Used by Tasks 5, 10, 11.

- [ ] **Step 1: Failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Models;

public class PhysicalRectGeometryTests
{
    [Fact]
    public void Contains_Point_Inline_With_DoubleOverload()
    {
        var r = new PhysicalRect(10, 10, 20, 20);
        Check.That(r.Contains(new PhysicalPoint(20, 20))).IsTrue();
        Check.That(r.Contains(new PhysicalPoint(9, 20))).IsFalse();
    }

    [Fact]
    public void IntersectsWith_TouchingEdges_DoNotIntersect()
    {
        // STRICT on purpose: D17's collision check must not fire on tangency (P1) —
        // a panel whose edge exactly kisses the keyboard's edge is not "over" it.
        var a = new PhysicalRect(0, 0, 10, 10);
        Check.That(a.IntersectsWith(new PhysicalRect(10, 0, 10, 10))).IsFalse();
        Check.That(a.IntersectsWith(new PhysicalRect(9.9, 0, 10, 10))).IsTrue();
    }

    [Fact]
    public void FromCenter_CentersTheRect()
    {
        var r = PhysicalRect.FromCenter(new PhysicalPoint(100, 50), 20, 10);
        Check.That(r.X).IsCloseTo(90, 1e-9);
        Check.That(r.Y).IsCloseTo(45, 1e-9);
    }

    [Fact]
    public void ExistingMembers_StillPresent()
    {
        var r = new PhysicalRect(0, 0, 10, 10);
        Check.That(r.CenterX).IsCloseTo(5, 1e-9);
        Check.That(r.Contains(5, 5)).IsTrue();
    }
}
```

- [ ] **Step 2: Verify fail** (`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PhysicalRectGeometryTests"` → compile error).
- [ ] **Step 3: Implement** the three new members; touch nothing existing.
- [ ] **Step 4: Run → PASS. Commit:** `feat: PhysicalRect geometry members — point Contains, strict IntersectsWith, FromCenter (P1/D14)`

---

### Task 2: Extract the smoothing composition helper (Core)

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/EffectiveSmoothing.cs`
- Modify: `src/EyeCtrl.Core/GazeEngine.cs:549-568` (delegate)
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSmoothingTests.cs`

**Interfaces:** Produces `public static class EffectiveSmoothing { public static double? Compose(EffectiveSettingsProvider provider, CursorSettings cursor); }`; `GazeEngine.EffectiveSmoothingParameter()` becomes a one-line delegate.

- [ ] **Step 1: Failing tests** — four cases: **Direct mode ⇒ null** (`FilterMode = GazeFilterMode.Direct`); **One-Euro floor** (baseline `OneEuroMinCutoff = 0.1`, `SmoothingMoreMaxFraction = 1.0`, momentary fill 1.0 ⇒ composes to 0.05 ⇒ clamped to the 0.1 floor); **"EMA composes against `SmoothingFactor`"** (renamed, L3: `FilterMode = Smoothed`, `SmoothingFactor = 0.9`, fraction 0.5, full fill ⇒ 0.45, in-range — the EMA floor is unreachable from in-range inputs; the case pins the baseline selection, not a clamp); **disabled ⇒ baseline** (`AdaptationSettings.Enabled = false`, provider with no fills ⇒ returns the cursor's `OneEuroMinCutoff` exactly). Fixture: one `(EffectiveSettingsProvider, CursorSettings)` factory per case; `provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, fill, 0.0)` for the fill cases; set **only the baseline the filter mode reads** (P13): One-Euro cases never touch `SmoothingFactor`; EMA cases stay inside its 0–1 guard.
- [ ] **Step 2: Verify fail. Step 3: Implement** — move the engine body verbatim (with its §13.3 comment); engine delegates.
- [ ] **Step 4: Run Core tests (including the GazeEngine oracle) → PASS. Commit:** `feat: extract EffectiveSmoothing.Compose (G2, P13)`

---

### Task 3: `AdaptationPreviewSnapshot` builder (Core, pure)

**Files:** Create `src/EyeCtrl.Core/Adaptation/AdaptationPreviewSnapshot.cs`; test `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationPreviewSnapshotTests.cs`.

**Interfaces:**
- Consumes: `EffectiveSmoothing.Compose` (Task 2).
- Produces: `public static AdaptationPreviewSnapshot Build(AdaptationSettings adaptation, EffectiveSettingsProvider provider, DwellSettings dwell, CursorSettings cursor)` (P14 — the provider exposes no `Enabled`; disabled ⇒ all-zero rows via `adaptation.Enabled`), returning:

```csharp
public sealed record AdaptationPreviewRow(
    AdaptationKnob Knob,
    double TotalFill,         // clamped composed net fill toward the row's direction, [0,1]
    double LearnedFill,       // the learned portion shaded inside TotalFill, [0, TotalFill]
    bool TighteningDirection, // true only for DwellTime when the tightening side dominates
    double? AbsoluteValue,    // tolerance px / smoothing parameter; null for Dwell/Geometry
    double? BaselineValue,    // the absolute knob's baseline; null with AbsoluteValue
    bool IsHzUnit);           // smoothing: Hz for One-Euro, false (factor) for EMA

public sealed record AdaptationPreviewSnapshot(
    IReadOnlyList<AdaptationPreviewRow> Rows,   // exactly four, knob order: Dwell, Tolerance, Smoothing, Geometry
    bool AnythingActive);                       // false ⇒ the window shows the "nothing active" line
```

Composition rule (mirrors `EffectiveSettingsProvider.Effective`, spec §3.4): `TotalFill = clamp(learned + momentary, 0, 1)` **per direction**; for Dwell, the forgiving (shorter) and tightening (longer) fills are compared — the larger side sets `TotalFill` and `TighteningDirection` (ties ⇒ forgiving/"shorter", `TighteningDirection = false`); the learned fill is forgiving-only, so `LearnedFill = tighteningDominates ? 0.0 : min(learned, TotalFill)` (spec §3.4: the slate shade appears on the shorter side only — never inside a "longer" bar). Tolerance absolute: `provider.Effective(FixationRadius, DwellContext.Other, dwell.FixationRadiusPixels)` as `AbsoluteValue`, the baseline as `BaselineValue` (tests assert BOTH the literal and the provider oracle call). Smoothing: `EffectiveSmoothing.Compose(provider, cursor)` as `AbsoluteValue` (the oracle — a test must assert it equals a direct `Compose` call), baseline = `cursor.OneEuroMinCutoff` (One-Euro, `IsHzUnit = true`) or `cursor.SmoothingFactor` (EMA, `false`), both null when `Compose` returns null. Dwell/Geometry: `AbsoluteValue`/`BaselineValue` null.

- [ ] **Step 1: Failing tests** — cases: **all-at-baseline** (no fills anywhere ⇒ `AnythingActive == false`, four rows all-zero); **learned-only** (learned fill set, momentary zero ⇒ that knob's `TotalFill == LearnedFill`, `AnythingActive == true`); **tolerance absolute** (`dwell.FixationRadiusPixels = 45`, tolerance momentary fill 0.4 with `ToleranceLooserMaxFraction = 0.5` ⇒ effective 54 ⇒ `AbsoluteValue == 54`, `BaselineValue == 45`); **smoothing per filter mode** (One-Euro ⇒ `IsHzUnit == true` and value ≡ `Compose` oracle; EMA ⇒ `false` and ≡ oracle; Direct ⇒ null); **bidirectional dwell** (tightening-only fill ⇒ `TighteningDirection == true` "longer"; mixed with larger forgiving ⇒ `false` "shorter"; learned 0.5 + momentary 0.8 ⇒ `TotalFill == 1.0` clamped, `LearnedFill <= TotalFill`); **geometry band-only** (`AbsoluteValue` null, fill present); PLUS `adaptation.Enabled == false` with non-zero fills still stored ⇒ all-zero rows / `AnythingActive == false` (locked §8.3 invariant).
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: AdaptationPreviewSnapshot — pure rows, clamped net composition, disabled-aware (D3/G1/P14)`

---

### Task 4: `ResetPreviewEngine` — core state machine (ViewModels)

**Files:** Create `src/EyeCtrl.ViewModels/Adaptation/ResetPreviewEngine.cs`, `src/EyeCtrl.ViewModels/Messages/PreviewMessages.cs`; test `tests/EyeCtrl.ViewModels.Tests/Adaptation/ResetPreviewEngineTests.cs`.

**Interfaces (exact; Tasks 6/10/11 rely on these):**

```csharp
public enum PreviewFeed { FeedAll, FeedExcluded, FeedNone }
public enum PreviewProgress { Propagate, HoldAtFreeze, Zero }

public sealed class ResetPreviewEngine
{
    // Suppression is the ENGINE's concern (P15): polled on every fed sample.
    public ResetPreviewEngine(Func<bool>? isFeedbackSuppressed = null);

    public bool IsArmed { get; }
    public bool FrozenSilent { get; }              // frozen && no panel → D18 rim cue
    public event Action<bool, int, PhysicalPoint>? PreviewVisibility; // visible, engagementId, anchor
    public event Action? FrozenSilentChanged;      // L1 — Task 6 binds the rim cue to this

    public void SetAnchor(PhysicalPoint center);
    public PreviewFeed DecideFeed(bool gazeOverReset, PhysicalPoint position, long timestampUs); // P4: sample time
    public PreviewProgress OnTargetProgress(string targetId, double progress, long timestampUs);
    public void OnActivated(string targetId);      // any target: Reset clears the latch; another clears _passArmed
    public void Reset();                           // deactivate / band rebuild / absent-report
}
```

**Clock (P4):** no wall clock anywhere. `DecideFeed` and `OnTargetProgress` take the sample timestamp; the engine caches `_lastSampleUs` so `OnBounds` (Task 5) times the 500 ms timeout on sample time too. Tests drive everything by explicit timestamps — no sleeps.

**Suppression (P15, D6 r3):** polled at the **top of every `DecideFeed`** (the gate is pull-only). Rising edge while a panel is up → policy hide (Task 5's `HiddenKeepLast`: rect stays, `FrozenSilent = true`, publish visibility-false). Suppressed **at engage** → no panel from the start: `FrozenSilent = true`, union = hit area immediately, **no unknown window** (publish nothing; the panel never existed).

**State machine** (`Disarmed, Frozen, Leaving, Armed`; constants `Threshold = 0.40`, `LeaveDebounceUs = 120_000`, `ArmExpiryUs = 3_000_000`):

- **Disarmed**: `OnTargetProgress(ResetId, p ≥ Threshold)` → **Frozen** (`_engagementId++`, `_held = p`, bounds unknown); publish `PreviewVisibility(true, id, anchor)` ONLY when unsuppressed — **suppressed at engage publishes nothing** (L1: no panel ever existed, so no service is told to show one; `FrozenSilent = true`, union = hit area immediately). Suppression **ending** mid-engagement does NOT restore the panel for that engagement (it stays hidden; the next engagement may show — L1).
- **Frozen**: `DecideFeed`: over Reset → `FeedNone`; off Reset with bounds unknown → `FeedExcluded`; off Reset inside the known rect → `FeedNone`; otherwise → **Leaving** (`_offSinceUs = ts`) returning `FeedExcluded`. Back in union cancels the run → Frozen. `OnTargetProgress(ResetId, ·)` → `HoldAtFreeze`.
- **Leaving**: continuous off-union ≥ `LeaveDebounceUs` → **Armed** (`_armedAtUs = ts`, publish visibility-false, `FrozenSilent = false`). `OnTargetProgress(ResetId, ·)` → **`HoldAtFreeze`** (P6 — the leave run's ungated `TargetProgress(0)` must not drop the arc; if the run cancels back to Frozen no event would restore it).
- **Armed — pass discipline (P3):** the pass starts on Reset's **first `OnTargetProgress(ResetId, progress > 0)`** while `_armedAtUs` is within `ArmExpiryUs` — NOT on `HitTest` (in the grown-overlap a Modes sample sets `HitTest(Reset)` true while Modes is nearest and fires; Reset then never emits progress 0, and a HitTest-latched `_passArmed` would outlive everything). `_passArmed` ends on: any `DecideFeed` sample with `gazeOverReset == false`, `OnActivated` of **any** target, or `Reset()`. While `_passArmed`: `FeedAll`, `Propagate`, and **no freeze engages mid-pass** even past 0.40. A gaze return whose first Reset progress arrives later than `ArmExpiryUs` → **Disarmed** first (fresh preview next). While Armed without a pass: `FeedAll`, `OnTargetProgress(ResetId, ·)` → `Propagate`.
- **Event invariant (P18):** every field is committed **before** its event is raised — a re-entrant `OnBounds` from inside `PreviewVisibility` must observe the committed Frozen state.

- [ ] **Step 1: Failing tests** — rev-1 list (engagement at 0.40/0.39; panel→button→panel no-fire; `HoldAtFreeze` arc+highlight contract via return values; multi-second read no-hide-no-fire; 1- and 3-sample excursions don't arm; debounced leave arms + `Zero`... — with P6 corrected: the **leave run returns `HoldAtFreeze`**, and `Zero` is only the post-arm drop; expiry at 3.1 s re-previews; 2.9 s return fires unfrozen with no second visibility-true) PLUS:
  - **P3 tests (L2 — pinned times):** Armed; gaze into the Modes/Reset overlap (HitTest true) but Modes accumulates and fires (`OnActivated("Modes")`) — the pass ends and the engine returns to **Armed with the original `_armedAtUs` unchanged**; then a later rest on Reset **more than 3 s** after arming → **previews again** (no zombie pass). Sibling: the same overlap→Modes-fires sequence with the later rest **within 3 s** → **fires unfrozen** (the latch legitimately persists through another target's activation). Both tests state the pass-end→Armed-original-timestamp rule.
  - **P6 test:** excursion → cancel back into the union mid-run → arc still reads the held 0.40.
  - **P18 test:** a `PreviewVisibility` subscriber that synchronously calls `OnBounds(id, rect)` (Task 5's API, stubbed here as a no-op) — engine does not corrupt state; the frozen state survives the re-entrant call.
  - **Suppression tests:** suppressed at engage → no visibility-true ever, `FrozenSilent == true`, union = hit area from the first sample; suppressed flipping mid-read (delegate returns true on a later `DecideFeed`) → visibility-false published, `FrozenSilent == true` — and with Task 5, the rect stays.
  - **Worst-case escape bound (P16):** with timestamps, freeze pass `0.4×D` + off-union `120 ms` + return + `1×D` completes; assert the fire happened and no preview interrupted the armed pass; a return after 3 s restarts with a preview.
- [ ] **Step 2: Verify fail. Step 3: Implement** (~180 lines). **Step 4: Run → PASS. Commit:** `feat: ResetPreviewEngine — sample-time state machine, pass discipline, suppression (P3/P4/P6/P15/P18)`

`PreviewMessages.cs` (P2/P10 shape — the bounds message carries a KIND, and visibility carries no edge):

```csharp
namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>VM → AdaptationPreviewService: show/hide the panel for one engagement. The service reads
/// all placement geometry (toolbar bounds, docked edge, floating state) itself — the VM knows none of it (P10).</summary>
public record AdaptationPreviewVisibilityMessage(bool Visible, int EngagementId, PhysicalPoint ResetCenter);

public enum PreviewBoundsKind
{
    Rect,           // the live panel bounds (show or resize)
    HiddenKeepLast, // a POLICY hide (suppression, D17 collision): no panel, but the last rect stays in the VM's union until the leave completes
    NoPanel         // nothing was or is shown (never-shown collision, contained failure, timeout, service absent): union falls back to the hit area
}

/// <summary>Service → VM: the live panel bounds and what kind of absence a hide is (P2).</summary>
public record AdaptationPreviewBoundsMessage(int EngagementId, PreviewBoundsKind Kind, PhysicalRect? PanelBounds);
```

---

### Task 5: `ResetPreviewEngine` — bounds protocol (ViewModels)

**Files:** Modify the engine; extend the tests.

**Interfaces:** Adds `public void OnBounds(int engagementId, PreviewBoundsKind kind, PhysicalRect? bounds)` (timed on the cached last sample clock, P4). Constant `BoundsTimeoutUs = 500_000`. **Carries from Task 4's review:** (a) `Engage` and `Disarm` must INVALIDATE the stored bounds (`_panelBounds = null`, `_boundsKind = unknown`) so engagement N's stale rect cannot inflate engagement N+1's union during its first-show unknown window (D14 "first show only"); (b) the Frozen→Leaving edge gains the guard: bounds-unknown-while-panel-wanted ⇒ stay Frozen (engaged regardless of gaze, `FeedExcluded`, no leave run) until the 500 ms timeout converts to the hit-area fallback Task 4 already implements; (c) ~7 Task-4 tests (shuttle, both excursions, debounced-leave, both P3 siblings, P16 bound) need a one-line `OnBounds(id, PreviewBoundsKind.Rect, rect)` inserted after engage — fixture adaptation to keep testing the debounce mechanics, NOT assertion weakening; also pin the 3.000 s expiry boundary (exactly 3.0 s ⇒ within, fires unfrozen) while in the file.

- [ ] **Step 1: Failing tests:**
  - `Rect` → stored; gaze in rect → `FeedNone`; a later `Rect` (resize) replaces it — between the resize and the new `Rect` **the last-known rect stays in the union** (gaze in the OLD rect → still `FeedNone`).
  - `HiddenKeepLast` → panel conceptually hidden (`FrozenSilent = true`), **the last rect remains in the union**: gaze held on the old rect > 120 ms → **still Frozen, NOT armed** (the C1/R2-M2 regression tests — one for a collision hide, one for a suppression flip).
  - `NoPanel` → immediate fallback: union = hit area, `FrozenSilent = true`, visibility already false.
  - **Bounds never arrive** (engage, then keep feeding samples past 500 ms of sample time): the engine publishes `PreviewVisibility(false, ...)` (cancel), and **`Pause still fires`** — expressed at engine level as: off-button samples return `FeedExcluded` and on-button `FeedNone`, i.e. the union is the hit area and the toolbar's other targets were never gated (the VM-level Pause test lives in Task 6).
  - Late `Rect` after the timeout does not resurrect the cancelled panel (engagement closed).
  - Mismatched engagement id (stale `NoPanel` from engagement N during N+1) → dropped.
  - First-show-only unknown: after a `Rect` arrives there is no further unknown window.
- [ ] **Step 2: Verify fail. Step 3: Implement** (`_boundsKind`, `_panelRect`, `_unknownSinceUs`; the timeout check on `DecideFeed`; `HiddenKeepLast` keeps `_panelRect` in the union while clearing panel-up).
- [ ] **Step 4: Run → PASS. Commit:** `feat: preview bounds protocol — three-kind message, last-rect keeps the union, sample-time timeout (P2/P11/D14)`

---

### Task 6: `ToolbarViewModel` integration (ViewModels + DI)

**Files:**
- Modify `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` (Receive ~:128-148, `OnTargetProgress` ~:309-321, `OnTargetActivated` switch ~:303, ctor ~:90, deactivation/`ResetAllState` ~:204, `UpdateButtonScreenPosition` ~:71-88)
- Modify `src/EyeCtrl.App/Services/AppBootstrapper.cs:372` — **the registration (P5)**
- Test: `tests/EyeCtrl.ViewModels.Tests/Adaptation/ToolbarViewModelPreviewTests.cs`

**Interfaces:**
- VM ctor gains `Func<bool>? isFeedbackSuppressed = null` (passed into the engine).
- `AppBootstrapper.cs:372` becomes a factory that forwards it (MS DI fills optional params with `null` — the shipped app would never suppress otherwise):

```csharp
services.AddSingleton(sp => new ToolbarViewModel(
    sp.GetRequiredService<Core.Settings.EyeCtrlSettings>(),
    WeakReferenceMessenger.Default,
    sink: sp.GetRequiredService<IStruggleTelemetrySink>(),
    dwellRegistry: sp.GetRequiredService<IDwellDetectorRegistry>(),
    isFeedbackSuppressed: () => sp.GetRequiredService<IFeedbackGate>().IsFeedbackSuppressed));
```

**Full arity is load-bearing (R2-1):** `IStruggleTelemetrySink` (`AppBootstrapper.cs:353`) and `IDwellDetectorRegistry` (`:315`) are both registered; both VM params are optional, so dropping either compiles and runs while silently switching off the toolbar's struggle telemetry and its adaptive dwell/geometry routing — the exact trap `DwellRoutingRegistrationWiringTests` exists to catch.
- Produces: `bool ResetFrozenSilent` (PropertyChanged, from `FrozenSilentChanged`), `void SetRenderedButtons(IReadOnlyCollection<string> renderedIds)`, `void Receive(AdaptationPreviewBoundsMessage)`, per-id `Dictionary<string, PhysicalPoint> _lastCenters`.

- [ ] **Step 1: Failing tests** — rev-1 list (0.40 hold; geometry-fill-1.0 overlap + panel-top-left → zero activations and zero outcomes via the telemetry sink; 130 ms leave arms and zeroes; return fires `ResetAdaptationMessage`; `SetRenderedButtons` absent-report clears target+latch+arc; **bounds never arrive → after the timeout the Pause target still completes and fires** — the strip never died) PLUS:
  - **P5 composition test (R2-1) — against the PRODUCTION container, not a hand-built copy:** resolve via `AppBootstrapper.BuildServiceProvider(...)` (the `DwellRoutingRegistrationWiringTests` pattern — that suite never resolves ViewModels on `WeakReferenceMessenger.Default` for leak reasons; follow its provider-lifetime discipline) with the fake gate substituted, resolve `ToolbarViewModel`, and assert THREE things: the toolbar's entry is present in `IDwellDetectorRegistry`; the sink reaches its `DwellDetector`; the suppression delegate is wired (gate returns true → drive to 0.40 → no visibility-true, `ResetFrozenSilent == true`). **Dispose the provider** so the VM unregisters.
  - Sample timestamps: every driven gaze sample carries explicit `TimestampMicroseconds`; no test sleeps (P4).
- [ ] **Step 2: Verify fail. Step 3: Implement** per rev 1 (feed switch with `FeedAll/FeedExcluded(t => t.Id != ResetAdaptationId)/FeedNone`; progress routing `Propagate/HoldAtFreeze/Zero` incl. `IsHighlighted`; `SetRenderedButtons`; `_lastCenters` → `SetAnchor`; `OnActivated` routed for every activation; `Reset()` on deactivate and absent-report; `Receive(AdaptationPreviewBoundsMessage)` → `_previewEngine.OnBounds(...)`). The VM caches the last sample's timestamp from `Receive(GazeCursorMovedMessage)` for `OnTargetProgress` calls.
- [ ] **Step 4: Run ViewModels tests → PASS. Commit:** `feat: toolbar VM preview integration + DI factory wiring of the suppression gate (P5/D9/D11)`

---

### Task 7: Band membership — `includeReset` + `Initialize` flag (App)

**Files:**
- Modify `src/EyeCtrl.App/Overlays/ToolbarBand.cs:139-160` (`Build`), `:167-196` (`LayOut`)
- Modify `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:142-175` — store the flag: `private bool _includeReset;` set by `Initialize`; `RebuildBand` (both callers: `Initialize` AND `SetMode`'s axis flip) passes it (P17)
- Modify `src/EyeCtrl.App/Services/OverlayManager.cs:184`, `src/EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs:207`
- Test: `tests/EyeCtrl.App.Tests/ToolbarButtonVisualTests.cs` + `tests/EyeCtrl.App.Tests/ToolbarButtonDriftTests.cs` (**repo-root paths**, P17)

**Interfaces:** `ToolbarBand.Build(double buttonSize, bool vertical, bool includeReset)`; `ToolbarWindow.Initialize(double buttonSize, int dwellTimeMs, bool includeReset)`.

- [ ] **Step 1: Failing tests** — rev-1 (6-button extent/no-separator/`Contains` false with the flag off; byte-for-byte old layout with it on; drift-test rule "band conditional, VM static" with D11 named; fixture policy: STA tests pass `includeReset: true` directly, `AppLaunchTests` assert the default no-Reset set) PLUS an axis-flip case: `SetMode` after `Initialize(..., includeReset: true)` keeps Reset, after `false` keeps it absent (the stored flag, P17).
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: band builds without Reset when adaptation is disabled (D5/M5)`

---

### Task 8: `SetRenderedButtons` wiring + stuck-arc clearing + D18 rim cue (App)

**Files:** Modify `ToolbarWindow.xaml.cs` (RebuildBand tail; PropertyChanged for `ResetFrozenSilent`), `ToolbarBand.cs:320-330, 429-441`; test `tests/EyeCtrl.App.Tests/Overlays/ToolbarBandRenderedButtonsTests.cs`.

**Interfaces:** RebuildBand ends with `vm.SetRenderedButtons(_buttons.Keys.ToArray())`; `ToolbarBand.SetFrozenSilent(bool)` — **the cue lives in `PaintFace`'s precedence chain BETWEEN `flashing` and `acquired`, and is a thicker rim stroke** (R2-3): the freeze holds `IsHighlighted = true`, so `ToolbarWindow` maps it to `SetAcquired(id, true)` and the frozen face is the **acquired** face (`BorderFocus` rim, `SurfaceLight` fill) — `RimFor` never runs while frozen, and `Tokens.BorderThicknessStandard` is the ONLY thickness token anyway while the rim is actually `DwellArcButton.Background` (a public `Ellipse`) stroked at the private `RimThickness = 2`. So: `SetFrozenSilent` adds the id to a `_frozenSilentIds` set consulted in `PaintFace` above `acquired`; when set it writes `b.Background.StrokeThickness = RimThickness * 2` (i.e. 4 DIP — a stated multiple, restored to the DwellArcButton default on cue end; the constant `2` is duplicated from `DwellArcButton.RimThickness` with a comment pointing at it, or exposed internally if the Controls project already grants InternalsVisibleTo — check and prefer exposure).

- [ ] **Step 1: Failing tests** — rev-1 (seam removes target AND set entry → re-`Initialize(true)` re-creates it via AddTarget, the dead-button regression; preloaded 0.4/highlight zeroed on removal; frozen-silent repaint + restored) PLUS:
  - **R2-3 assertions against the ACQUIRED face** (what the user actually sees while frozen — `BorderFocus` rim / `SurfaceLight` fill): cue-on differs from acquired in `StrokeThickness` (assert the actual values 4 vs 2); the cue is distinguishable from AND has adequate contrast against the acquired face (darkest-blend pattern from `DwellToggleVisualTests`, which lives in `EyeCtrl.Controls.Tests`); precedence: flashing still beats the cue (flash during frozen-silent paints the flash face).
  - **P16 full flow:** disable (`Initialize(..., false)`) mid-dwell on Reset → no fire, arc 0; re-enable (`Initialize(..., true)`) → gaze rest → **previews** (freeze) → leave → return → **fires**, arc from 0 the whole way.
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: SetRenderedButtons seam, stuck-arc clearing, distinct frozen-silent rim cue (P7/D11/D18)`

---

### Task 9: Enabled-flip re-init + tray refresh (App)

**Files:**
- Modify `src/EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs:195-219` (factored `ReinitializeToolbar` + `PropertyChanged(Enabled)` subscription)
- Modify `src/EyeCtrl.App/Services/TrayIconManager.cs:63-96` **and its construction site** `src/EyeCtrl.App/App.xaml.cs:283` (P12)
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/SettingsUiCoordinatorTests.cs` (exists there), **create** `tests/EyeCtrl.App.Tests/Services/TrayIconManagerTests.cs`

**Interfaces:**
- Coordinator: as rev 1 (whole block factored; `PropertyChanged(Enabled)` subscription, `AdaptationActivation` pattern).
- Tray (P12 + L4): promote the local `resetAdaptationItem` (at `:82`) to a field; ctor gains `Func<bool> isAdaptationEnabled` **placed BEFORE the optional `showToolbarCallback`**; the construction site `App.xaml.cs:283` passes `() => settings.Adaptation.Enabled` (**the local `settings`**, not a `_settings` field); the `ContextMenuStrip.Opening` handler calls a new `internal void RefreshMenuItems()` that sets `_resetAdaptationItem.Available = _isAdaptationEnabled()` — **`Available`, not `Visible`** (WinForms: `ToolStripItem.Visible`'s getter returns false whenever the menu isn't displayed) — and the internal seam is what headless tests call (firing `Opening` without a shown menu is unreliable).

- [ ] **Step 1: Failing tests** — rev-1 coordinator cases; tray: `Opening` with the delegate false → `Available == false`; true → `Available == true` (assert `Available`).
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: Enabled-flip re-init block + tray item gated via Available (P12/D5/M4)`

---

### Task 10: `AdaptationPreviewWindow` + `AdaptationPreviewService` (App)

**Files:**
- Create `src/EyeCtrl.App/Overlays/AdaptationPreviewWindow.xaml`(+`.cs`), `src/EyeCtrl.App/Services/AdaptationPreviewService.cs`
- Modify `src/EyeCtrl.App/Services/AppBootstrapper.cs` (register) and `src/EyeCtrl.App/Services/AdaptationActivation.cs` (participant list)
- Modify `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs:165` — **named-holder suspension (P8, lives HERE so the service never references a later task)**
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationPreviewServiceTests.cs`

**Interfaces:**
- **Window:** `IPreviewPanel` (extracted interface: `ShowPanel(snapshot)`, `HidePanel()`, `event Action<PhysicalRect?>? BoundsChanged`) implemented by `AdaptationPreviewWindow : OverlayWindow` (ctor pattern of `CueOverlayWindow`: `IsClickThrough`, `ExcludeFromScreenCapture`, `InitializeComponent`; DIP→physical at its DPI).
- **Service (P9 + R2-2):** an `AdaptationActivation` participant — registered in DI but resolved lazily on first **activation** (`AdaptationActivation`'s existing lazy-participant pattern: constructing `CueOverlayWindow` "must not happen while disabled"); `Activate` resolves the window and force-creates the HWND (`new WindowInteropHelper(w).EnsureHandle()`); **`Deactivate`/`Dispose` performs the FULL teardown by itself** — `HidePanel()`, publish `NoPanel` bounds, release the `"adaptation-preview"` auto-hide holder, publish `PreviewPanelActiveMessage(false)`, unsubscribe — **never waiting for a VM message**: `SettingsUiCoordinator`'s and `AdaptationActivation`'s `PropertyChanged(Enabled)` handlers fire in undefined order, so the service may already be unsubscribed when the absent-report arrives; without self-contained teardown, offers and auto-hide stay suspended until restart. Restartable after `Dispose` (the participant contract: idempotent `Start`, restartable `Dispose`) — re-enable shows the panel again. **No unconditional startup resolve** (P9). The 500 ms timeout already covers a runtime that never activates (spec D14).
- **Placement (P10):** the service reads geometry itself — `IToolbarOverlay.GetPhysicalBounds()`/`IsOnScreen` + `settings.Toolbar` (`IsDocked`, `DockedEdge`) via injected `EyeCtrlSettings`; the message carries only `(Visible, EngagementId, ResetCenter)`. **Floating toolbar (`IsDocked == false`) ⇒ no panel** — publish `NoPanel` immediately; silent confirm (D13's own named case).
- **Bounds publishes (P2/P11):** on show → `Rect`; on `SizeChanged` → `Rect`; on visibility-false from the VM (user left) → nothing (the engine already hid); on **policy hide** (suppression is engine-side; D17 collision) → `HiddenKeepLast`; on **show-time collision** or contained failure → `NoPanel` **immediately** (no 500 ms unknown wait, P11).
- **Auto-hide (P8):** the coordinator's API becomes `SetAutoHideSuspended(string holder, bool suspended)` backed by a `HashSet<string>` (empty set ⇒ not suspended; migration: `SettingsUiCoordinator` passes `"settings"`, the preview passes `"adaptation-preview"`; the old single-bool call sites are all migrated in this task). The service holds/releases only its own holder — both clobber orders become impossible.
- DI: `services.AddSingleton<AdaptationPreviewWindow>(); services.AddSingleton<AdaptationPreviewService>();` — resolution stays lazy via the activation participant.

- [ ] **Step 1: Failing tests** — rev-1 (content from snapshot incl. nothing-active; bounds rect published at the test's scale; visibility-false ends cleanly; `provider.Changed` twice within 500 ms → one refresh; a throwing `IPreviewPanel` → `NoPanel` and no crash) PLUS:
  - **P8 both orders:** settings suspends → preview suspends → preview releases ⇒ still suspended; preview suspends → settings opens/closes ⇒ still suspended while the panel is up.
  - **P9:** with adaptation disabled, resolving+starting the app DI graph constructs no `AdaptationPreviewWindow` (composition assertion: resolve the activation participant list / the service's lazy seam).
  - **R2-2 disable-mid-preview:** panel up, then `AdaptationSettings.Enabled = false` (drive the real `PropertyChanged` path, EITHER handler order): offers no longer suspended (unless another input holds them), the auto-hide holder released (`IsAutoHideSuspended == false`), panel hidden; then **enable → disable → enable: the panel shows again** (restartable participant).
  - **P10 floating:** `IsDocked == false` → `NoPanel`, no window show.
- [ ] **Step 2: Verify fail. Step 3: Implement** (XAML per rev 1: title/subtitle rows, four row items with the two-segment bar, right-aligned values, footer; `SizeToContent="WidthAndHeight"`, MaxWidth 360 DIP; strings via `overlays.adaptationPreview.*` keys landing in Task 13).
- [ ] **Step 4: Run → PASS. Commit:** `feat: preview window + service — activation participant, self-read geometry, named-holder suspension (P8/P9/P10/P11/D2/D3)`

---

### Task 11: D17 collision inventory + continuous re-check (App)

**Files:** Create `src/EyeCtrl.App/Services/GazeConsumerInventory.cs`; modify `AdaptationPreviewService.cs`, `tests/EyeCtrl.App.Tests/OverlayInventoryDriftTests.cs`, `docs/design/overlay-unification-spec.md` §3.1; tests `tests/EyeCtrl.App.Tests/Services/GazeConsumerInventoryTests.cs` + extend the service tests.

**Interfaces:** `IGazeConsumerInventory.IReadOnlyList<PhysicalRect> Live()` — keyboard (`TryGetChromeBounds`), **edge-button bar**, action menu, modes menu, **external dwell** (new `public IReadOnlyList<PhysicalRect> LiveTargetRects()` on `ExternalDwellService`, read under `_stateLock`). `OverlayInventoryDriftTests` gains the `collision-zone | exempt:<reason>` classification — every `OverlayWindow` subclass classified, missing classification fails the build. **Carries from Task 10's review:** (a) the collision rect must come from the service's REAL measured bounds (`BoundsChanged`/last-known rect), never the DIP-numbered `NominalWidthPx` estimate (under-covers at scale > 1.0); (b) the implemented-but-untested `HideAsPolicy` refresh-path hide gets its pinned test here (flip a collision source between show and a `provider.Changed` ⇒ `HiddenKeepLast` published with the live engagement id).

Service: at show AND on every `SchedulePublish`-set trigger AND on `SizeChanged`: intersect (strict, Task 1) → `HidePanel()` + **`HiddenKeepLast`** (mid-read) or **`NoPanel`** (still at show). No opposite anchor (r6).

- [ ] **Step 1: Failing tests** — rev-1 list (keyboard-on-edge ⇒ no panel; **side dock + bottom keyboard ⇒ no panel** (P16 — a fake inventory rect modelling the clamped bottom-corner panel overlapping the bottom keyboard, plus the real-inventory mapping test pinning each named source); auto-show mid-read (`ShowKeyboardRequestMessage` driven through the coordinator where headless-feasible) ⇒ panel hides with **`HiddenKeepLast`** — and, stated plainly: the *zero keyboard activations* half is asserted headless via the fake inventory + service-level hide; the live-keyboard activation count is covered by the owner-gated UI pass, not by a headless suite (P16 note); resize-into-zone re-check; classification drift (delete a row → red, restore).
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: D17 inventory + drift guard + continuous re-check (R4-H1/R5-M1/P16)`

---

### Task 12: Offer suspension — fifth input (App)

**Files:** Modify `src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs:88-95,139-168`, `AdaptationPreviewService.cs`; extend coordinator + service tests.

**Interfaces:** `_previewVisible` from a new `PreviewPanelActiveMessage(bool)` (service publishes; it never writes `SetActivationOffersSuspended`); `RecomputeSuspension` gains "adaptation preview" as the fifth reason.

- [ ] **Step 1: Failing tests:** panel visible → suspended with the reason; **panel hides while Settings open → STILL suspended**; **offers RESUMED on hide when nothing else holds** (P16); the coordinator remains the only writer (source-scan test, the `DwellSurfaceCensusTests` pattern).
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: preview as fifth offer-suspension input (D16 r5/P16)`

---

### Task 13: Strings, fit class, overlay-spec row (App/Core)

**Files:**
- Modify `src/EyeCtrl.Core/Localization/strings.json` — keys under `overlays.adaptationPreview.*`: `title` ("Adaptation right now"), `subtitle` ("what Reset would clear"), `footer` ("Reset clears all momentary and learned offsets."), knob labels (`dwell`, `tolerance`, `smoothing`, `geometry`), direction words (`shorter`, `longer`, `looser`, `more`, `bigger`, `atBaseline`), `nothingActive` ("Nothing active right now"), units (`px`, `hz`, `factor`, `band`). **English only** — `strings.de.json` untouched (T13).
- Modify `src/EyeCtrl.Core/Localization/StringTableSchema.cs` — `FitClasses` gains `PV1` (comment: "the adaptation-preview row: direction word + right-aligned value in a ≤360 DIP panel row, ink ≥ 2 DIP from the bar") and `tools/strings_validate.py` gains the same class (`StringsEnumParityTests` fails if only one changes).
- Test: a fit-harness case in `tests/EyeCtrl.App.Tests/Localization/Fit` measuring the longest English row ("Tolerance — looser" + "58 px ← 45 px") in the production control at scale 2.0 (mirror an existing class's harness file).
- Modify `docs/design/overlay-unification-spec.md` §3.1 — complete `AdaptationPreviewWindow`'s row (Task 11 added the classification; this fills the remaining columns).

- [ ] **Step 1: Failing tests:** keys resolve in the App test host (`StringTable.Current.T("overlays.adaptationPreview.title")` is not the fallback); the validator passes with `PV1`; the fit harness's longest row fits.
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: `python tools/strings_validate.py` + tests → PASS. Commit:** `feat: preview strings (overlays surface, EN), PV1 fit class, overlay-spec row (D15)`

---

### Task 14: Struggle-spec §8.2/§8.6 amendment (docs, own commit — D12)

**Files:** Modify `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` §8.2 and §8.6.

Text (spec §7 verbatim intent): the toolbar Reset is a two-step escape — freeze-preview pass, debounced leave, one full armed dwell returned **within 3 s** of arming; worst case `0.40 × ToolbarButtonDwellMs + 120 ms + return + 1 × ToolbarButtonDwellMs` (defaults ≈ 1.1 s + return); the **tray** item remains single-step; Settings' "Reset learned offsets" unchanged. Link this feature's spec as the reason.

- [ ] **Step 1:** edit both sections. **Step 2:** `grep -n "instantly escapable" docs/superpowers/specs/2026-07-19-*.md` — every remaining occurrence is one deliberately kept (the tray path). **Step 3:** drift tests still green. **Step 4: Commit (docs only):** `docs: amend struggle spec §8.2/§8.6 — two-step toolbar escape, bounded worst case (D12)`

---

### Task 15: Full gate

- [ ] `dotnet build EyeCtrl.slnx` → 0 errors
- [ ] `dotnet test` (all projects, non-UI) → 0 failed; totals in the commit message
- [ ] `python tools/palette_validate.py` → green (no palette entries added — proves it)
- [ ] Mutation spot-checks (surgical back-mutations, never `git checkout --`; re-run the touched test after each restore; commit gate is THIS plan's tests, never `--no-build` after a mutation): `Threshold 0.40 → 0.60` (engagement test red); `LeaveDebounceUs 120_000 → 0` (excursion test red); drop one `FeedNone` return (overlap test red); invert `FrozenSilent` (rim-cue test red); **`HiddenKeepLast → NoPanel` in the engine's hide path (the collision-hide regression test red)**; **delete the `dwellRegistry:` argument from the Task-6 factory (the R2-1 wiring test must go red — the silent-adaptation-off trap)**.
- [ ] Final commit if anything moved: `test: full-gate green for the adaptation preview feature`

---

## Plan review round 1 ledger (P1–P18)

| # | Where fixed |
|---|---|
| P1 PhysicalRect exists; test/impl contradiction | Task 1 rewritten: EXTEND, strict `IntersectsWith` (touching ⇒ false) with the test asserting it |
| P2 null-on-every-hide broke last-rect-in-union; no suppression trigger | `PreviewBoundsKind { Rect, HiddenKeepLast, NoPanel }` (Task 4 messages, Task 5 rules, Tasks 10-11 publishers); gate polled every `DecideFeed` (Task 4) |
| P3 `_passArmed` zombie via HitTest | Pass starts on Reset's first progress > 0; ends on off-button sample / any activation / Reset (Task 4) + the overlap-Modes test |
| P4 wall clock vs sample clock | `timestampUs` in `DecideFeed`/`OnTargetProgress`; cached for `OnBounds`; no wall-clock default; tests timestamp-driven (Task 4/6) |
| P5 DI fills optional param with null | Factory registration at `AppBootstrapper.cs:372` + composition test (Task 6) |
| P6 Leaving had no progress rule | Leaving → `HoldAtFreeze` + excursion-cancel test (Task 4) |
| P7 cue = fainter resting face | Thicker stroke on the same token; distinctness + actual-blend contrast tests (Task 8) |
| P8 two writers on one bool | `SetAutoHideSuspended(string holder, bool)` + `HashSet`; both callers migrated; both-order tests (Task 10) |
| P9 startup construct vs activation rule | Service is an `AdaptationActivation` participant; lazy resolve + `EnsureHandle` at activation; no startup resolve (Task 10) |
| P10 edge/floating/extent not expressible | Message trimmed to `(Visible, EngagementId, ResetCenter)`; service reads `GetPhysicalBounds` + `settings.Toolbar`; floating ⇒ `NoPanel` (Task 10) |
| P11 show-time collision unanswered | Publishes `NoPanel` immediately (Task 10) |
| P12 tray field/ctor/site/tests + WinForms trap | Field + `Func<bool>` param + `App.xaml.cs:283` site + `Available` (Task 9) |
| P13 fixture threw on `SmoothingFactor = 5.0` | Mode-matching baselines only (Task 2) |
| P14 provider invisible to Build | `Build(AdaptationSettings, ...)` + disabled test (Task 3) |
| P15 suppression ownership contradiction | Engine owns it; suppressed-at-engage ⇒ immediate no-panel, no unknown window (Task 4) |
| P16 dropped spec tests | Escape-bound timing (Task 4); disable→enable full flow (Task 8); side-dock+bottom-keyboard + fake-inventory note (Task 11); offers-resumed-on-hide (Task 12) |
| P17 wrong paths / order / stored flag | Test paths corrected (Global Constraints + Tasks 7-9); suspension API moved into Task 10; `_includeReset` stored for `SetMode` (Task 7) |
| P18 re-entrancy | Commit-before-raise invariant + re-entrant `OnBounds` test (Task 4, Global Constraints) |

*Delta re-review pointers: check P2 (bounds kinds, Tasks 4/5/10/11), P3 (pass discipline, Task 4), P4 (timestamps, Tasks 4/6) first.*

## Plan review round 2 ledger (R2-1–R2-3, L1–L5 — verdict FIX-THEN-EXECUTE, no further round)

| # | Where fixed |
|---|---|
| R2-1 factory dropped sink + registry (silent adaptation-off) | Full-arity snippet (Task 6) + `BuildServiceProvider`-based wiring test (registry entry, sink reach, gate wired, provider disposed) + Task 15 mutant |
| R2-2 disable-mid-preview leaves offers/auto-hide suspended | `Deactivate`/`Dispose` performs the full self-contained teardown (hide, NoPanel, release holder, publish `PreviewPanelActiveMessage(false)`, unsubscribe); restartable; both tests (Task 10) |
| R2-3 cue on the unreachable RimFor branch; token doesn't exist | Cue precedence between `flashing` and `acquired` in `PaintFace`; `Background.StrokeThickness = RimThickness × 2` on the public Ellipse, restored on cue end; assertions against the ACQUIRED face (Task 8) |
| L1 interface incomplete / publish contradiction / suppression-end | `FrozenSilentChanged` added to the block; suppressed-at-engage publishes nothing; suppression-end keeps the panel hidden for that engagement (Task 4) |
| L2 P3 test timing unpinned | Overlap test with the later rest > 3 s (previews) + sibling < 3 s (fires); pass-end returns to Armed with the original `_armedAtUs` (Task 4) |
| L3 EMA case named a clamp it can't reach | Renamed "EMA composes against `SmoothingFactor`", 0.9 baseline (Task 2) |
| L4 tray details | Local `settings`; param before the optional callback; `internal RefreshMenuItems()` seam (Task 9) |
| L5 typo | `WindowInteropHelper` (Task 10) |
