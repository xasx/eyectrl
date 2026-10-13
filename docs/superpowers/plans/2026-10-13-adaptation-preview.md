# Adaptation Preview & Reset Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A live adaptation-status preview flyout on the toolbar's Reset button (seen-latch freeze, never able to fire while being read) and hiding the Reset affordances when adaptation is disabled — per spec `docs/superpowers/specs/2026-10-13-adaptation-preview-and-reset-visibility-design.md` r6 (`45cb7917`).

**Architecture:** A pure ViewModels-layer state machine (`ResetPreviewEngine`) owns the freeze/latch/debounce/expiry/bounds protocol; `ToolbarViewModel` feeds it gaze samples and progress events and publishes messenger messages; an App-side service (`AdaptationPreviewService`) owns the window, anchoring, the D17 collision inventory, throttled content, and auto-hide/offer suspension. Core gains only data types and pure builders. Feature 2 (hide Reset) rides the existing band-rebuild path with a new `SetRenderedButtons` seam.

**Tech Stack:** .NET 10 WPF (App), plain net10.0 (Core, ViewModels), CommunityToolkit.Mvvm, xUnit v3 + NFluent 3.1.0.

## Global Constraints

- Spec decisions D1–D18 are binding; the ledger is in spec §2/§10. Key constants verbatim: trigger **0.40** of the Reset dwell; leave debounce **120 ms** continuous off-union; arm expiry **3 s** judged at the start of the return; bounds-unknown timeout **500 ms** (cancels the panel); refresh throttle **≥ 500 ms**.
- `EyeCtrl.ViewModels` is plain `net10.0` — **no WPF types** in ViewModels code; rects crossing VM↔App are `EyeCtrl.Core.Models.PhysicalRect` in physical pixels.
- Tests use **NFluent** (`Check.That(...)`), xUnit v3; test namespaces mirror source; private nested fakes, no mocking library.
- Assertions: `Check.That(x).IsEqualTo(y)` etc. Floating point: `IsCloseTo(expected, delta)` is ABSOLUTE delta.
- One test suite at a time on this box; never run two `dotnet test` concurrently.
- Strings: new keys under `overlays.adaptationPreview.*` in `src/EyeCtrl.Core/Localization/strings.json`, **English only** (surface `overlays`; German is T13's scope). No new theme palette entries — existing Tokens only.
- All work in the worktree `.worktrees/adaptation-preview` (branch `feat/adaptation-preview`). Commit per task. No pushes unless asked.
- Every new service that shows/hides a window wraps show/hide/refresh in try/catch (log-and-skip) — a comfort feature must never take down the session.
- Build gate per commit: `dotnet build EyeCtrl.slnx` plus the touched project's test run.

---

### Task 1: `PhysicalRect` (Core.Models)

**Files:**
- Create: `src/EyeCtrl.Core/Models/PhysicalRect.cs`
- Test: `tests/EyeCtrl.Core.Tests/Models/PhysicalRectTests.cs`

**Interfaces:**
- Produces: `public readonly record struct PhysicalRect(double X, double Y, double Width, double Height)` with `bool Contains(PhysicalPoint point)`, `bool IntersectsWith(PhysicalRect other)`, `static PhysicalRect FromCenter(PhysicalPoint center, double width, double height)`. Used by Tasks 5, 10, 11.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Models;

public class PhysicalRectTests
{
    [Fact]
    public void Contains_PointInsideOrOnEdge_True()
    {
        var r = new PhysicalRect(10, 10, 20, 20);
        Check.That(r.Contains(new PhysicalPoint(20, 20))).IsTrue();
        Check.That(r.Contains(new PhysicalPoint(9, 20))).IsFalse();
    }

    [Fact]
    public void IntersectsWith_TouchingRects_True()
    {
        var a = new PhysicalRect(0, 0, 10, 10);
        Check.That(a.IntersectsWith(new PhysicalRect(10, 0, 10, 10))).IsTrue();
        Check.That(a.IntersectsWith(new PhysicalRect(10.1, 0, 10, 10))).IsFalse();
    }

    [Fact]
    public void FromCenter_CentersTheRect()
    {
        var r = PhysicalRect.FromCenter(new PhysicalPoint(100, 50), 20, 10);
        Check.That(r.X).IsCloseTo(90, 1e-9);
        Check.That(r.Y).IsCloseTo(45, 1e-9);
    }
}
```

- [ ] **Step 2: Run to verify fail** — `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~PhysicalRectTests"` → compile error (type not found).

- [ ] **Step 3: Implement**

```csharp
namespace EyeCtrl.Core.Models;

/// <summary>A screen-space rectangle in physical pixels — the unit <c>DwellDetector.HitTest</c>,
/// gaze positions, and D17's collision inventory already use. ViewModels (plain net10.0) cannot
/// reference <c>System.Windows.Rect</c>, so the preview protocol carries this instead; the App
/// side converts at the window's DPI.</summary>
public readonly record struct PhysicalRect(double X, double Y, double Width, double Height)
{
    public bool Contains(PhysicalPoint point) =>
        point.X >= X && point.X <= X + Width && point.Y >= Y && point.Y <= Y + Height;

    public bool IntersectsWith(PhysicalRect other) =>
        other.X < X + Width && other.X + other.Width > X &&
        other.Y < Y + Height && other.Y + other.Height > Y;

    public static PhysicalRect FromCenter(PhysicalPoint center, double width, double height) =>
        new(center.X - width / 2, center.Y - height / 2, width, height);
}
```

- [ ] **Step 4: Run tests → PASS. Commit:** `git add -A && git commit -m "feat: PhysicalRect — physical-px rectangle for the preview protocol (D14)"`

---

### Task 2: Extract the smoothing composition helper (Core)

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/EffectiveSmoothing.cs`
- Modify: `src/EyeCtrl.Core/GazeEngine.cs:549-568` (delegate to the helper)
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSmoothingTests.cs`

**Interfaces:**
- Produces: `public static class EffectiveSmoothing` with `public static double? Compose(EffectiveSettingsProvider provider, CursorSettings cursor)`. GazeEngine's `internal double? EffectiveSmoothingParameter()` becomes a one-line delegate. Used by Task 3's snapshot.

- [ ] **Step 1: Write the failing tests** (move the composition cases out of any GazeEngine tests if they exist there; at minimum pin the four filter modes + the clamps)

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class EffectiveSmoothingTests
{
    private static (EffectiveSettingsProvider Provider, CursorSettings Cursor) Make(
        GazeFilterMode mode, double baseline, double shorterFraction = 0.0, double momentary = 0.0)
    {
        var adaptation = new AdaptationSettings { Enabled = true, SmoothingMoreMaxFraction = shorterFraction };
        var cursor = new CursorSettings { FilterMode = mode };
        cursor.OneEuroMinCutoff = baseline; // and SmoothingFactor when EMA — set both, the helper picks
        cursor.SmoothingFactor = baseline;
        var provider = new EffectiveSettingsProvider(adaptation);
        if (momentary > 0)
            provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, momentary, 0.0);
        return (provider, cursor);
    }

    [Fact]
    public void DirectMode_IsNull()
    {
        var (provider, cursor) = Make(GazeFilterMode.Direct, 5.0);
        Check.That(EffectiveSmoothing.Compose(provider, cursor)).IsNull();
    }

    [Fact]
    public void OneEuro_ClampedToFloor()
    {
        // 0.1 Hz baseline, full band, full fill → 0.05 → clamped to the 0.1 floor (GazeEngine §13.3).
        var (provider, cursor) = Make(GazeFilterMode.OneEuro, 0.1, shorterFraction: 1.0, momentary: 1.0);
        Check.That(EffectiveSmoothing.Compose(provider, cursor)!.Value).IsCloseTo(0.1, 1e-9);
    }

    [Fact]
    public void Smoothed_Ema_ClampedToUnit()
    {
        var (provider, cursor) = Make(GazeFilterMode.Smoothed, 0.9, shorterFraction: 1.0, momentary: 1.0);
        Check.That(EffectiveSmoothing.Compose(provider, cursor)!.Value).IsCloseTo(0.0, 1e-9);
    }

    [Fact]
    public void Disabled_ReturnsBaseline()
    {
        var adaptation = new AdaptationSettings { Enabled = false };
        var provider = new EffectiveSettingsProvider(adaptation);
        var cursor = new CursorSettings();
        var expected = cursor.OneEuroMinCutoff;
        Check.That(EffectiveSmoothing.Compose(provider, cursor)!.Value).IsCloseTo(expected, 1e-9);
    }
}
```

- [ ] **Step 2: Verify fail.** — [ ] **Step 3: Implement** — move the body of `GazeEngine.EffectiveSmoothingParameter()` (lines 549-568, including the §13.3 clamp comment) into the helper verbatim, sourcing the baseline from `cursor.FilterMode` exactly as the engine did; the engine method becomes:

```csharp
internal double? EffectiveSmoothingParameter() =>
    _effectiveSettings is null ? null : EffectiveSmoothing.Compose(_effectiveSettings, _settings.Cursor);
```

(The `OneEuroFilter`/`EMA` range literals move with the code; the assign-back oracle test the comment mentions must keep passing — run `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngine"`.)

- [ ] **Step 4: Run Core tests → PASS. Commit:** `feat: extract EffectiveSmoothing.Compose — one composition source for engine and preview (G2)`

---

### Task 3: `AdaptationPreviewSnapshot` builder (Core, pure)

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/AdaptationPreviewSnapshot.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationPreviewSnapshotTests.cs`

**Interfaces:**
- Consumes: `EffectiveSmoothing.Compose` (Task 2), `EffectiveSettingsProvider`, `AdaptationSettings`.
- Produces:

```csharp
public sealed record AdaptationPreviewRow(
    AdaptationKnob Knob,
    double TotalFill,        // clamped composed net fill toward the row's direction, [0,1]
    double LearnedFill,      // the learned portion shaded inside TotalFill, [0, TotalFill]
    bool TighteningDirection, // true only for DwellTime when the tightening side dominates
    double? AbsoluteValue,    // tolerance px / smoothing parameter; null for Dwell/Geometry
    double? BaselineValue,    // the absolute knob's baseline; null with AbsoluteValue
    bool IsHzUnit);           // smoothing: Hz for One-Euro, false (factor) for EMA; meaningless otherwise

public sealed record AdaptationPreviewSnapshot(
    IReadOnlyList<AdaptationPreviewRow> Rows,
    bool AnythingActive);     // false ⇒ the window shows the "nothing active" line instead of bars
```

via `public static AdaptationPreviewSnapshot Build(EffectiveSettingsProvider provider, DwellSettings dwell, CursorSettings cursor)`.

- [ ] **Step 1: Write the failing tests** — cases (all per spec §3.4/§6): all-at-baseline (`AnythingActive == false`, four rows, all fills 0); learned-only; tolerance absolute (`58 ← 45` shape: with `dwell.FixationRadiusPixels = 45`, tolerance momentary 0.4 × fraction 0.5 → effective 54; assert `AbsoluteValue == 54` and `BaselineValue == 45`); smoothing per filter mode (One-Euro Hz `IsHzUnit == true`; EMA factor `false`; the value equals `EffectiveSmoothing.Compose` — the oracle); **bidirectional dwell**: tightening-only (`TighteningDirection == true` — accidental-activation fill, direction "longer"), mixed (net direction by larger side), learned+momentary summing past 1.0 (learned 0.5 cap + momentary 0.8 → `TotalFill == 1.0`, `LearnedFill <= TotalFill`); geometry band-only (`AbsoluteValue` null).

Fill composition rule (mirror `EffectiveSettingsProvider.Effective`, spec §3.4): `TotalFill = clamp(learned + momentary, 0, 1)` per direction; dwell compares forgiving (shorter) vs tightening (longer) sides to pick `TighteningDirection`.

- [ ] **Step 2: Verify fail. Step 3: Implement** — a pure static class; per knob read `TryGetMomentaryFill(knob, DwellContext.Other, out f, out t)` + `_learnedFills` via `EnumerateLearnedFills`; tolerance effective via `provider.Effective(FixationRadius, Other, dwell.FixationRadiusPixels)`; smoothing via the Task-2 helper and the `CursorSettings` baseline (`OneEuroMinCutoff` / `SmoothingFactor` by `FilterMode`); disabled provider ⇒ all-zero rows (locked §8.3 invariant surfaces as `AnythingActive == false`).
- [ ] **Step 4: Run Core tests → PASS. Commit:** `feat: AdaptationPreviewSnapshot — pure per-knob rows, clamped net composition (D3, G1)`

---

### Task 4: `ResetPreviewEngine` — core state machine (ViewModels)

**Files:**
- Create: `src/EyeCtrl.ViewModels/Adaptation/ResetPreviewEngine.cs`
- Create: `src/EyeCtrl.ViewModels/Messages/PreviewMessages.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/Adaptation/ResetPreviewEngineTests.cs`

**Interfaces:**
- Consumes: `PhysicalPoint`, `PhysicalRect` (Task 1).
- Produces (exact — Tasks 6 and 10 rely on these):

```csharp
public enum PreviewFeed { FeedAll, FeedExcluded, FeedNone }
public enum PreviewProgress { Propagate, HoldAtFreeze, Zero }

public sealed class ResetPreviewEngine
{
    public ResetPreviewEngine(Func<long>? nowUs = null); // defaults to Environment.TickCount64 * 1000

    public bool IsArmed { get; }
    public bool FrozenSilent { get; }          // frozen && no panel → D18 rim cue
    public event Action<bool, int, PhysicalPoint>? PreviewVisibility; // visible, engagementId, anchor
    public event Action? FrozenSilentChanged;

    public PreviewFeed DecideFeed(bool gazeOverReset, PhysicalPoint position);
    public PreviewProgress OnTargetProgress(string targetId, double progress);
    public void OnActivated(string targetId);          // Reset fired → Disarmed
    public void Reset();                               // deactivate/band-rebuild/absent-report
}
```

Constructor constants (spec verbatim): `Threshold = 0.40`, `LeaveDebounceUs = 120_000`, `ArmExpiryUs = 3_000_000`. Panel visibility intent is carried by `PreviewVisibility`; **suppression is NOT the engine's concern** (D6 r3: the freeze runs everywhere; the VM decides whether a panel may show and simply ignores `PreviewVisibility(true, ...)` while suppressed — the engine must not know why).

State machine (spec §3.1/§3.2 — implement exactly):

- **Disarmed**: `OnTargetProgress(ResetId, p)` with `p >= Threshold` → **Frozen**: increment `_engagementId`, `_held = p`, bounds unknown, publish `PreviewVisibility(true, id, anchor)` (the anchor is `SetAnchor(PhysicalPoint)` — the VM updates it from `UpdateButtonScreenPosition`; add that method).
- **Frozen**: `DecideFeed`: gaze over Reset → `FeedNone`; gaze off Reset but bounds unknown → `FeedExcluded` (the 500 ms bound lives in Task 5's bounds protocol — this task treats unknown as engaged); gaze off Reset and inside the known panel rect → `FeedNone`; otherwise → transition **Leaving** (`_offSince = now`), return `FeedExcluded`. A sample back inside the union cancels the run → Frozen.
- **Frozen** `OnTargetProgress(ResetId, p)` → `HoldAtFreeze` (the VM renders `_held` and keeps `IsHighlighted`).
- **Leaving**: elapsed ≥ `LeaveDebounceUs` continuous off-union → **Armed** (`_armedAtUs = now`): publish `PreviewVisibility(false, id, anchor)`; `FrozenSilent` false; `OnTargetProgress` → `Zero` (arc + highlight drop).
- **Armed**: on the first `DecideFeed` with `gazeOverReset == true`: if `now - _armedAtUs < ArmExpiryUs` → `_passArmed = true` (this pass runs to fire or abandon; `OnTargetProgress` → `Propagate`; **no freeze engages mid-pass even past 0.40**); else → back to **Disarmed** first (the next engagement previews fresh). While `_passArmed`, `DecideFeed` returns `FeedAll` and the armed flag survives until `OnActivated` (fire) or a `OnTargetProgress(ResetId, 0)` leave-abandon — both → **Disarmed**. Armed and gaze elsewhere → `FeedAll` (normal toolbar life).
- `OnActivated(ResetId)` / `Reset()` → **Disarmed**, publish `PreviewVisibility(false, id, anchor)` if a panel was up.

- [ ] **Step 1: Write the failing tests** — the spec §6 ViewModels rows (state-machine half): engagement at 0.40 (not at 0.39); panel→button→panel stays, no fire (`FeedNone` throughout, exactly one visibility-published true); acquired face + arc hold while frozen (`HoldAtFreeze`); several seconds reading → no hide, no fire; 1-sample and 3-sample off-union excursions do not arm (return to union within 120 ms cancels); the debounced leave arms (`IsArmed == true`), drops arc (`Zero`); **arm expiry at 3 s re-previews** (advance the injected clock 3.1 s, gaze on Reset → new engagement id, freeze again); **a return at 2.9 s with a 2000 ms-dwell-equivalent timeline fires unfrozen with no second visibility-true event** (`_passArmed`; mid-pass progress 0.6 → `Propagate`); `OnActivated` clears; `FrozenSilent` starts false, becomes true when the panel is told invisible... 

  — NOTE for the implementer: in Task 4 the engine publishes visibility-true on engage; `FrozenSilent` becomes true only when the panel is ruled out — Task 5's `OnBounds(id, null)` / timeout fallback / `MarkSuppressed(true)` all set it. For Task 4 assert `FrozenSilent == false` while frozen-with-panel and expose an internal `MarkPanelAbsent()` helper (sets `FrozenSilent = true`, raises `FrozenSilentChanged`) that Task 5's paths call (tested directly here).

- [ ] **Step 2: Verify fail. Step 3: Implement** (~150 lines; one enum `State { Disarmed, Frozen, Leaving, Armed }`, fields `_state, _engagementId, _held, _offSinceUs, _armedAtUs, _passArmed, _anchor, _panelUp, _frozenSilent`, the injected clock).
- [ ] **Step 4: Run ViewModels tests → PASS. Commit:** `feat: ResetPreviewEngine — seen-latch state machine, feed gating, arm expiry (D4/D7/D8/D10/D13)`

`PreviewMessages.cs` (needed by Task 6/10; write it here so the types exist once):

```csharp
namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>VM → AdaptationPreviewService: show/hide the panel for one engagement.</summary>
/// <param name="Visible">Whether the panel should be up.</param>
/// <param name="EngagementId">Monotonic id; echoed in <see cref="AdaptationPreviewBoundsMessage"/>.</param>
/// <param name="Anchor">The Reset button's last reported screen centre (physical px).</param>
/// <param name="DockedEdge">The toolbar's docked edge ("top"/"bottom"/"left"/"right").</param>
public record AdaptationPreviewVisibilityMessage(bool Visible, int EngagementId, PhysicalPoint Anchor, string DockedEdge);

/// <summary>Service → VM: the live panel bounds, or null for "no panel" (hidden, contained failure, absent).
/// Unknown-while-visible is "engaged" until the 500 ms timeout (D14).</summary>
public record AdaptationPreviewBoundsMessage(int EngagementId, PhysicalRect? PanelBounds);
```

---

### Task 5: `ResetPreviewEngine` — bounds protocol (ViewModels)

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Adaptation/ResetPreviewEngine.cs`
- Test: extend `tests/EyeCtrl.ViewModels.Tests/Adaptation/ResetPreviewEngineTests.cs`

**Interfaces:**
- Adds to Task 4's engine: `public void OnBounds(int engagementId, PhysicalRect? bounds)`, `public void MarkSuppressed(bool suppressed)` (VM-level: while true, published visibility-true must not raise a panel — simplest: the engine raises `PreviewVisibility(false-as-panel)` via a separate `PanelPermitted` gate it applies before publishing true; while suppressed, engage freezes silently: `FrozenSilent = true`, no true-publish). Internal timeout constant `BoundsTimeoutUs = 500_000`.

- [ ] **Step 1: Failing tests** (spec §6 bounds rows): bounds-unknown ⇒ no debounce (gaze far off-union for >120 ms while unknown → still Frozen, `FeedExcluded`); **after 500 ms of bounds never arriving, Pause still fires** (feed returns `FeedExcluded` off-button / `FeedNone` on-button — i.e. the union fell back to the hit area — AND the engine published `PreviewVisibility(false, ...)` = panel cancelled AND `FrozenSilent == true`); late bounds after the timeout do not resurrect the cancelled panel (`OnBounds(id, rect)` on a timed-out engagement is ignored — a new engagement begins); **`OnBounds(id, null)` ⇒ immediate fallback** (union = hit area, `FrozenSilent`, visibility false); **resize keeps the last-known rect in the union** (bounds arrive, then `OnBounds(id, smallerRect)` while gaze sits in the OLD rect → still `FeedNone`, until the new rect lands); **a stale-`null` with a mismatched engagement id is dropped** (engage N+1, deliver `OnBounds(N, null)` → no effect); matching-id rect while visible stores it (gaze in rect → `FeedNone`).

- [ ] **Step 2: Verify fail. Step 3: Implement**: fields `_boundsKnown, _panelRect, _unknownSinceUs`; `DecideFeed` consults the union = hit-area ∨ (known rect) ∨ unknown-window; the timeout check runs on `DecideFeed`/`OnBounds`; suppression gate wraps the visibility publish.
- [ ] **Step 4: Run → PASS. Commit:** `feat: preview bounds protocol — timeout cancels, last-known rect, engagement id, suppression gate (D14/D6r3)`

---

### Task 6: `ToolbarViewModel` integration (ViewModels)

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` (Receive override at ~:128-148, `OnTargetProgress` at ~:309-321, ctor at ~:90)
- Test: `tests/EyeCtrl.ViewModels.Tests/Adaptation/ToolbarViewModelPreviewTests.cs`

**Interfaces:**
- Consumes: `ResetPreviewEngine` (Tasks 4-5), messages.
- Produces: ctor param `Func<bool>? isFeedbackSuppressed = null`; `public bool ResetFrozenSilent` (raised via `OnPropertyChanged` for the band's D18 cue); `public void SetRenderedButtons(IReadOnlyCollection<string> renderedIds)`; `public void Receive(AdaptationPreviewBoundsMessage message)` (registers on the messenger); `SetAnchor` fed from `UpdateButtonScreenPosition` (add a per-id `Dictionary<string, PhysicalPoint> _lastCenters`).

- [ ] **Step 1: Failing tests** (VM-level, real `ToolbarViewModel` + real `DwellDetector`, fake gaze like existing ToolbarViewModel tests — mirror their construction pattern): gaze on Reset to 0.40 → `Messenger` sees `AdaptationPreviewVisibilityMessage(true, ...)` and the Reset button VM's `DwellProgress` holds at ~0.40 while further samples on-button produce no activation; **geometry fill 1.0, gaze held 3 s in the Modes/Reset overlap then at the panel's top-left → zero activations and zero outcomes** (fake `IStruggleTelemetrySink` collects nothing — the sink seam the base class already takes); leave-union 130 ms → armed, `DwellProgress == 0`, `IsHighlighted == false`; return → full dwell fires `ResetAdaptationMessage` (register a recipient); suppressed ctor-delegate true → freeze engages (`ResetFrozenSilent == true`) with **no** visibility-true message; `SetRenderedButtons` without "ResetAdaptation" → target removed (`DwellDetector` no longer hits it), latch cleared, `DwellProgress` zeroed, and a visibility-false published if a panel was up; **bounds never arrive → after the timeout the toolbar still fires Pause** (gaze to Pause target completes — proves the strip never died).

- [ ] **Step 2: Verify fail. Step 3: Implement**:
  - ctor: `_previewEngine = new ResetPreviewEngine(); _previewEngine.PreviewVisibility += OnPreviewVisibility;` wire `isFeedbackSuppressed` → `MarkSuppressed`; register `AdaptationPreviewBoundsMessage` on the messenger.
  - `Receive(GazeCursorMovedMessage)`: before the existing logic, compute `overReset = DwellDetector.HitTest(ResetAdaptationId, message.Position)` (false when no target) and switch on `_previewEngine.DecideFeed(overReset, message.Position)`: `FeedAll` → existing path unchanged; `FeedExcluded` → `DwellDetector.Update(pos, ts, static t => t.Id != ResetAdaptationId)`; `FeedNone` → `return` (before `DwellDetector.Update`, mirroring the pause-latch `return` at :145).
  - `OnTargetProgress`: for `targetId == ResetAdaptationId`, apply the engine's `Propagate/HoldAtFreeze/Zero` — HoldAtFreeze sets `btn.DwellProgress = _heldValue; btn.IsHighlighted = true;` and returns; Zero sets both to 0/false; other ids propagate untouched. `OnTargetActivated` (the :303 switch) for `ResetAdaptation` also calls `_previewEngine.OnActivated(id)`.
  - `SetRenderedButtons`: remove targets + `_positionedButtons` entries + zero `DwellProgress`/`IsHighlighted` for ids absent from `renderedIds`; if `ResetAdaptation` absent → `_previewEngine.Reset()`.
  - `UpdateButtonScreenPosition`: record `_lastCenters[id]`; for ResetAdaptation call `_previewEngine.SetAnchor(center)`.
  - `ResetFrozenSilent` from `FrozenSilentChanged` (raise PropertyChanged; the button VM lookup via `_buttonLookup`).
  - Toolbar deactivation path (`SetDwellingActive(false)`-adjacent, where `DwellDetector.ResetAllState()` already runs at :204): add `_previewEngine.Reset()`.

- [ ] **Step 4: Run ViewModels tests → PASS. Commit:** `feat: toolbar VM preview integration — feed gating, held arc, seam, silent-confirm flag (D9/D11/D18 state)`

---

### Task 7: Band membership — `includeReset` + `Initialize` flag (App)

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarBand.cs:139-160` (Build), `:167-196` (LayOut)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:142-175` (Initialize/RebuildBand)
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs:184` and `src/EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs:207` (callers pass the flag)
- Test: extend `tests/EyeCtrl.App.Tests/Overlays/ToolbarButtonVisualTests.cs` + `ToolbarButtonDriftTests` per the new rule

**Interfaces:**
- Produces: `ToolbarBand.Build(double buttonSize, bool vertical, bool includeReset)`; `ToolbarWindow.Initialize(double buttonSize, int dwellTimeMs, bool includeReset)`. The band's button array drops the Reset entry when `includeReset == false`, and `LayOut` emits neither the separator nor `ResetGap` (the strip shortens; `ResetGap` only applies to the id branch, which never runs).

- [ ] **Step 1: Failing tests**: with `includeReset: false` the band has 6 buttons, `Contains(ResetId)` false, and `_canvas.Width` equals the 6-button extent (derive from `ButtonMargin`/`Pitch` exactly as `ToolbarButtonVisualTests` does); with `true` the old 7-button layout byte-for-byte (existing tests keep passing once their fixture passes the flag — see Step 3). `ToolbarButtonDriftTests`: update the parity rule to encode "band membership conditional on `Adaptation.Enabled` (passed as `includeReset`), VM list static" — assert the VM list still contains `ResetAdaptation` unconditionally and the band builds from a flag, with a comment naming D11.
- **Fixture policy (R2-M3, binding):** STA/window tests that need Reset construct `ToolbarWindow` and call `Initialize(size, dwellMs, includeReset: true)` directly — they NEVER enable `AdaptationSettings.Enabled` (which in the launched app starts the whole runtime). `AppLaunchTests` asserts the DEFAULT set (no Reset). Sweep the ~20 existing Reset references across the 8 files named in spec §6 and give each either the flag or the default-set expectation.
- [ ] **Step 2: Verify fail. Step 3: Implement** the signatures above; both callers read `settings.Adaptation.Enabled`.
- [ ] **Step 4: Run App tests (non-launching subset) → PASS. Commit:** `feat: band builds without Reset when adaptation is disabled (D5, M5)`

---

### Task 8: `SetRenderedButtons` wiring + stuck-arc clearing + D18 rim cue (App)

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (RebuildBand tail; PaintFace path in `ToolbarBand.cs:320-330, 429-441`)
- Test: `tests/EyeCtrl.App.Tests/Overlays/ToolbarBandRenderedButtonsTests.cs` (new)

**Interfaces:**
- Consumes: `ToolbarViewModel.SetRenderedButtons` (Task 6), `ResetFrozenSilent` (Task 6).
- Produces: RebuildBand ends with `vm.SetRenderedButtons(_buttons.Keys.ToArray())` (guarded when DataContext isn't the VM); `ToolbarBand.SetFrozenSilent(bool)` — `PaintFace` for `ResetId` picks the frozen-silent rim token (existing warning-family, e.g. `Tokens.BrushAccentWarning` at reduced opacity via an existing subtle token — pick from `Tokens`, do NOT add a palette entry) when set, and the normal `RimFor(id)` otherwise.

- [ ] **Step 1: Failing tests**: after `Initialize(..., includeReset: false)` the VM's Reset target is gone AND `_positionedButtons` no longer contains it → a second `Initialize(..., true)` re-creates it (first position report takes the AddTarget branch — dead-button regression, spec M3); a button VM preloaded with `DwellProgress = 0.4, IsHighlighted = true` then removed via the seam reads back 0/false (stuck-arc regression); `SetFrozenSilent(true)` repaints Reset's rim stroke to the cue brush and `false` restores `RimFor(ResetId)`; the rim cue contrast at full dwell blend meets the repo's dwell-blend discipline (mirror the darkest-blend assertion pattern from `DwellToggleVisualTests` — assert against the blend toward Accent).
- [ ] **Step 2: Verify fail. Step 3: Implement.** ToolbarWindow subscribes `vm.PropertyChanged` for `ResetFrozenSilent` → `_band.SetFrozenSilent(...)` (unsubscribe in the existing teardown).
- [ ] **Step 4: Run → PASS. Commit:** `feat: SetRenderedButtons seam, stuck-arc clearing, frozen-silent rim cue (D11/D18)`

---

### Task 9: Enabled-flip re-init + tray refresh (App)

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs:195-219`; `src/EyeCtrl.App/Services/TrayIconManager.cs:82-96`
- Test: extend `tests/EyeCtrl.App.Tests/Services/SettingsUiCoordinatorTests.cs` (or create if absent — mirror existing coordinator tests), `tests/EyeCtrl.App.Tests/Services/TrayIconManagerTests.cs`

**Interfaces:**
- Produces: a private `ReinitializeToolbar(EyeCtrlSettings s)` holding the WHOLE block (`Initialize` + `SetMode` + `UpdateEnabled` + `UpdateEdge` + `PositionIndicator` + `RefreshToolbarRect`), called by both the snapshot branch and a new `AdaptationSettings.PropertyChanged(Enabled)` subscription (the `AdaptationActivation.cs:85-98` pattern — subscribe in `OnInitialize`, unsubscribe where the class's other subscriptions die). Tray: `_resetAdaptationItem.Visible = _settings.Adaptation.Enabled` in the menu's `Opening`.

- [ ] **Step 1: Failing tests**: flipping `settings.Adaptation.Enabled` (no toolbar-settings change) calls `Toolbar.Initialize` with the new flag AND `SetMode`/`RefreshToolbarRect` (assert via a fake `IToolbarOverlay`/window seam — if no seam exists, assert on the real window's band content, the established visual-test route); a toolbar-settings change still re-initializes exactly once (no double); tray: menu `Opening` with `Enabled == false` hides the item, `true` shows it.
- [ ] **Step 2: Verify fail. Step 3: Implement.**
- [ ] **Step 4: Run → PASS. Commit:** `feat: Enabled-flip rebuilds the toolbar via the full re-init block; tray item gated (D5, M4)`

---

### Task 10: `AdaptationPreviewWindow` + `AdaptationPreviewService` (App)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/AdaptationPreviewWindow.xaml` + `.xaml.cs`
- Create: `src/EyeCtrl.App/Services/AdaptationPreviewService.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (register; resolve + force-create the HWND at startup)
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationPreviewServiceTests.cs`

**Interfaces:**
- Consumes: messages (Task 4), `AdaptationPreviewSnapshot.Build` (Task 3), `ToolbarLifecycleCoordinator.SetAutoHideSuspended` (guarded — see Task 12), `EffectiveSettingsProvider.Changed`.
- Produces: `AdaptationPreviewWindow : OverlayWindow` with `void ShowPanel(AdaptationPreviewSnapshot snapshot)`, `void HidePanel()`, `event Action<PhysicalRect?>? BoundsChanged` (raised on show, on `SizeChanged`, null on hide/failure — converted from DIP at the window's DPI); the service `AdaptationPreviewService : IDisposable` registered singleton, subscribing `AdaptationPreviewVisibilityMessage` (publishing `AdaptationPreviewBoundsMessage` back), `provider.Changed` (refresh throttle ≥ 500 ms), nothing else in this task.

Window XAML: `IsClickThrough = true`, `ExcludeFromScreenCapture = true` (the CueOverlayWindow ctor pattern), a fixed-size-ish panel (`SizeToContent="WidthAndHeight"`, MaxWidth ~360 DIP) hosting a title row, four row items (a 7-DIP bar grid: cyan `TotalFill` with a slate `LearnedFill` segment; Dwell's bar is a two-side grid), a value `TextBlock` right-aligned, and the footer. Localized strings via `StringTable.Current.T("overlays.adaptationPreview.*")` (keys land in Task 13; until then reference them and let the table fall back to the key — Task 13 fills values).

Service logic:
- On visibility-true (matching nothing stale — it just acts on the latest): compute the anchor rect from the message (beyond Reset's outer end, away from the strip's run, offset into the screen from `DockedEdge`, connector tick, work-area clamped — §3.3), **then the D17 check (Task 11 lands next; here call a stub `IOverlayCollision` returning "clear" that Task 11 replaces)**, `ShowPanel(snapshot)`.
- On visibility-false: `HidePanel()`; publish `AdaptationPreviewBoundsMessage(id, null)`.
- All of it in try/catch → log + publish null bounds (D14 failure path).
- Suspend auto-hide around visibility with the Task-12 guard.
- `AppBootstrapper`: `services.AddSingleton<AdaptationPreviewWindow>(); services.AddSingleton<AdaptationPreviewService>();` plus a startup resolve + `EnsureHandle`-equivalent (`var w = sp.GetRequiredService<AdaptationPreviewWindow>(); w.CreateGraphics()`-style handle force — WPF: an `HwndSource` via `WindowInteropHelper` needs a Show; use the documented `EnsureHandle` pattern: `new WindowInteropHelper(w).EnsureHandle()`).

- [ ] **Step 1: Failing tests** (headless via the window on STA — mirror `GlassBackdropWindowTests`'s construction-without-showing pattern where possible): visibility-true → window content built from a hand-made snapshot (4 rows render, "nothing active" line when `AnythingActive == false`); bounds message published with a rect (DIP→physical at the test's known scale); visibility-false → null bounds; `provider.Changed` twice within 500 ms refreshes once (count `ShowPanel` invocations via a subclass seam); a throwing window (injected fake `IPreviewPanel` — extract that interface so the service is testable headless) produces null bounds and no crash.
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: preview window + service — anchor, content, throttled refresh, failure-safe bounds (D2/D3/D14)`

---

### Task 11: D17 collision inventory + continuous re-check (App)

**Files:**
- Create: `src/EyeCtrl.App/Services/GazeConsumerInventory.cs` (`IGazeConsumerInventory` + impl)
- Modify: `src/EyeCtrl.App/Services/AdaptationPreviewService.cs`; `tests/EyeCtrl.App.Tests/OverlayInventoryDriftTests.cs`; `docs/design/overlay-unification-spec.md` §3.1 (add the row + a new classification column or list)
- Test: `tests/EyeCtrl.App.Tests/Services/GazeConsumerInventoryTests.cs`; extend `AdaptationPreviewServiceTests`

**Interfaces:**
- Produces: `IGazeConsumerInventory.IReadOnlyList<PhysicalRect> Live()` — keyboard window (`TryGetChromeBounds`), **edge-button bar** (`EdgeButtonBarWindow` bounds when visible), action menu, modes menu (the `ActivationChromeZones` derived rects), **external dwell** (a new public `IReadOnlyList<PhysicalRect> LiveTargetRects()` on `ExternalDwellService` reading `_targetsById` under `_stateLock`). `OverlayInventoryDriftTests` gains a `ClassifiedOverlays` map: every `OverlayWindow` subclass → `collision-zone` | `exempt:<reason>` (the preview window itself and the non-interactive cue/drift/ring are exempt-with-reason; a missing classification fails the build — the drift guard).

Service: replace Task 10's stub — at show AND on every trigger in `ActivationOfferCoordinator.SchedulePublish`'s set (keyboard/menus `PropertyChanged`, toolbar visibility + settings, `DisplayGeometryChangedMessage`, `KeyboardEdgeToggledMessage`, `ExternalDwellActiveMessage`) AND on `SizeChanged`: if the panel rect intersects any live zone → `HidePanel()` + null bounds; the silent confirm holds (the VM keeps the last-known rect in its union — already Task 5's rule). No "opposite anchor" (r6 dropped it): collision at show ⇒ no panel.

- [ ] **Step 1: Failing tests**: keyboard visible on the toolbar's edge ⇒ no panel (service with a fake inventory returning an overlapping rect never shows; the real inventory test pins each named source maps to its window); **panel visible, then the keyboard auto-shows on the toolbar's edge mid-read ⇒ the panel hides and zero keyboard activations occur while gaze is held where the panel was** (drive `KeyboardCoordinator`'s `ShowKeyboardRequestMessage` in the coordinator test, or assert at service level: fake inventory flips to overlapping → `HidePanel` called + null bounds published); a content-refresh `SizeChanged` that grows the panel into a zone re-checks and hides; the drift test fails when a classification row is missing (delete one deliberately, watch it red, restore).
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: D17 — gaze-consumer inventory, drift-guarded classification, continuous collision re-check (R4-H1/R5-M1)`

---

### Task 12: Suspension ownership — offers 5th input + auto-hide guard (App)

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs:88-95, 139-168`
- Modify: `src/EyeCtrl.App/Services/AdaptationPreviewService.cs` (auto-hide suspension)
- Test: extend coordinator tests + `AdaptationPreviewServiceTests`

**Interfaces:**
- Produces: coordinator field `_previewVisible` set from a new `PreviewPanelActiveMessage(bool)` (published by the service — D16 r5: the service NEVER writes `SetActivationOffersSuspended`); `RecomputeSuspension` gains the fifth reason "adaptation preview". Auto-hide: the service owns a bool it set — resume only if it suspended (`_autoHideSuspensionHeld`), so the Settings window's suspension is never clobbered.

- [ ] **Step 1: Failing tests**: panel visible → offers suspended with reason logged; **panel hides while Settings is open → offers STILL suspended** (the combined-flag test, spec §6); the service suspending then resuming while Settings holds its own suspension does not unsuspend (assert the coordinator/lifecycle state); the service never calls `SetActivationOffersSuspended` (reflection or a seams test — simplest: the coordinator test proves it's the only writer by type-scan, mirroring `DwellSurfaceCensusTests`'s source-scan pattern).
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run → PASS. Commit:** `feat: preview as fifth offer-suspension input; guarded auto-hide suspension (D16 r5)`

---

### Task 13: Strings, fit class, overlay-spec row (App/Core)

**Files:**
- Modify: `src/EyeCtrl.Core/Localization/strings.json` (keys under `overlays.adaptationPreview.*`: title, subtitle, footer, knob labels ×4, direction words shorter/longer/looser/more/bigger/at-baseline, nothing-active line, value units px/Hz/factor/band)
- Modify: `src/EyeCtrl.Core/Localization/StringTableSchema.cs` (`FitClasses` + a comment naming the measured control) and `tools/strings_validate.py` (the same class — `StringsEnumParityTests` forces both)
- Test: extend `tests/EyeCtrl.App.Tests/Localization/Fit` (a fit-harness case measuring the preview row in the production control at scale 2.0 — mirror an existing class's harness file), plus the schema parity tests keep passing
- Modify: `docs/design/overlay-unification-spec.md` §3.1 row for `AdaptationPreviewWindow` (Task 11 added the classification; this completes the row's remaining columns)

**Interfaces:** none (leaf task). English only — the German file is untouched (T13). The fit class id: `PV1`, comment: "the adaptation-preview row: direction word + right-aligned value in a ≤360 DIP panel row, ink ≥ 2 DIP from the bar".

- [ ] **Step 1: Failing tests**: keys resolve (`StringTable.Current.T("overlays.adaptationPreview.title")` non-key-fallback in the App test host); the Python validator passes with the new class; the fit harness measures the longest English row ("Tolerance — looser" + "58 px ← 45 px") within budget.
- [ ] **Step 2: Verify fail. Step 3: Implement. Step 4: Run validator (`python tools/strings_validate.py`) + tests → PASS. Commit:** `feat: preview strings (overlays surface, EN), PV1 fit class, overlay-spec row (D15)`

---

### Task 14: Struggle-spec §8.2/§8.6 amendment (docs, own commit — D12)

**Files:**
- Modify: `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` §8.2 (the quarantined tightening's "instantly escapable" phrasing) and §8.6 (the manual escape)

**Interfaces:** none. Text (from spec §7, verbatim intent): the toolbar Reset is a two-step escape — freeze-preview pass, debounced leave, one full armed dwell returned **within 3 s** of arming; worst case `0.40 × ToolbarButtonDwellMs + 120 ms + return + 1 × ToolbarButtonDwellMs` (defaults ≈ 1.1 s + return); the tray item remains single-step; Settings' "Reset learned offsets" unchanged. Reference this feature's spec as the reason and link back.

- [ ] **Step 1:** edit the two sections; **Step 2:** verify the spec's internal cross-references still resolve (grep "instantly escapable" — every remaining occurrence must be one you deliberately kept, e.g. the tray path); **Step 3:** `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~OverlayInventoryDrift"` still green (docs table untouched); **Step 4: Commit (its own commit, no code):** `docs: amend struggle spec §8.2/§8.6 — the toolbar escape is two-step with a bounded worst case (D12)`

---

### Task 15: Full gate

- [ ] `dotnet build EyeCtrl.slnx` → 0 errors
- [ ] `dotnet test` (all projects, non-UI) → 0 failed; note the totals in the commit message
- [ ] `python tools/palette_validate.py` → green (no palette entries were added; proves it)
- [ ] Mutation spot-checks (the repo's discipline): back-mutate `Threshold = 0.40 → 0.60` (engagement test must fail), `LeaveDebounceUs = 120_000 → 0` (excursion test must fail), drop one `FeedNone` return (overlap test must fail), invert `FrozenSilent` (rim-cue test must fail). Restore each (surgical edits, never `git checkout --`), re-run the touched test after each restore.
- [ ] Final commit if anything moved: `test: full-gate green for the adaptation preview feature`

## Notes for the executor

- The two suites with pipe/app-launch tests are opt-in (`EYECTRL_UI_TESTS=1`); do not run them unattended — the app seizes the owner's only input channel. A live UI pass is an owner-gated step after merge review, not part of this plan.
- `docs/superpowers/plans/2026-10-13-adaptation-preview.md` is this file; the spec (`.../specs/2026-10-13-adaptation-preview-and-reset-visibility-design.md`) is the authority on any ambiguity — its §2 ledger D1–D18 and §10 review ledgers record WHY for every rule here.
