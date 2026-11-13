# Startup Splash Animation — Design

- **Date:** 2026-11-13
- **Status:** Rev 3.2. Rev 1 (84d3e1ed) — external r1 (12 findings, all valid) → owner picked the separate STA splash thread (L11). Rev 2 (61eb723b) — external r2 (8 findings + nit) → owner's V2 pick recorded as L16. Rev 3/3.1 (7c14bd92/57a9a5e2) — external r3 (formula + DPI thread + leftovers). Rev 3.2 — plan-review r4 (compile-level defects in the plan; §6 tail-at-readiness clarification with the `Filled` beat; §7 two bounded waits with cancel-before-show; §9 `ISplashWindow` factory; real-window fit gate). Rev 1's render-thread animation claim was wrong and is struck (L10 superseded).
- **Scope:** `SplashWindow` (thread model, card content, geometry, choreography, adaptive timing), its exit hand-off, the DPI initialization, and the affected tests. No changes to startup ordering on the main thread, DI, provider resolution, or any other overlay.

## 1. Summary

The splash becomes a short animated brand moment built from the accepted app icon's own vocabulary: amber gaze dots arrive, the gaze dot blooms, and a cyan dwell arc sweeps in a half-turn while filling, settling at the arc's 120° brand position before the title rises and the card fades into the working app. The arc fill doubles as the progress surface: it paces itself against real startup readiness (T3 "adaptive fill" — completes via a catch-up when readiness arrives, never compressed below a calm 1.0 s floor (L16), never cuts mid-arc, never repeats a beat). The framing stays today's floating card (F3), larger, click-through. To animate at all, the splash runs on its own STA thread with its own dispatcher — the standard WPF splash pattern — because the main thread is blocked for the splash's entire visible lifetime (§8).

## 2. Context (what is measured and what is not)

- The splash shows when not in test mode (`App.xaml.cs:165-169`). The main thread then runs `OnStartup` synchronously until ~line 725 (the try block's end; the catch beyond it runs only on failure) with **no dispatcher pumping**; there is no `await` in that stretch.
- **Readiness** is the `ShowOverlays` call (`App.xaml.cs:380`, which today closes the splash at `:963`). From the 2026-11-12 22:43 log: the post-ShowOverlays line `Startup paused state` lands at **28.941**, so readiness ≈ 28.94. The queued `Overlay band:` show-time checks only ran at **29.287** — i.e. the dispatcher freed up ≈ 0.35 s *after* readiness. The UI thread is blocked for roughly the splash's whole visible life and a little past readiness.
- Warm-start splash-visible span ≈ **0.5 s before readiness** (splash shown ≈ 28.5, not logged) plus the ~0.35 s tail after it. Cold-start duration is **not measured** — no log line exists before 28.48, and the LM/ONNX work this log shows runs after startup (33.71). The new `Splash:` log lines (§7) make all of this measurable from the next launch.
- The current splash is a `SizeToContent` card: 64 px ico `Image`, "EyeCtrl" `TextBlock`, indeterminate `ProgressBar`, and the hidden no-tracker sentence (`AutomationId="Splash_NoTracker"`, string key `overlays.splash.noTracker`). Its indeterminate bar cannot tick during the blockage either (§8).

## 3. Goals and non-goals

**Goals**

1. A professional, calm brand moment unmistakably EyeCtrl — built from the accepted icon's geometry (amber gaze dot + bloom + cyan 120° dwell arc) so splash and icon read as one system.
2. The arc fill is honest progress: it runs a slow base course, and when readiness arrives it catches up to complete — never cuts mid-arc, never repeats a beat.
3. Control is never delayed beyond a fixed tail after readiness, and nothing about the splash can trap startup (independent thread, dead-man close, failure containment).

**Non-goals (YAGNI)**

- No sound; no settings row; no `--no-splash` flag; no full-screen mode; no new localization keys; no changes to main-thread startup ordering, DI, or provider resolution; no changes to the icon asset or any other overlay.

## 4. The card (F3 — floating card, owner pick)

| Property | Value |
|---|---|
| Window | `SplashWindow : OverlayWindow` — `WindowStyle=None`, `AllowsTransparency=True`, `Topmost`, `ShowInTaskbar=False`, `WindowStartupLocation=CenterScreen` |
| **Click-through** | `IsClickThrough = true` (L13) — the splash has nothing to interact with; during the post-readiness tail the gaze engine is already running and must not dwell onto the card |
| Size | **Fixed** 440 × 336 DIP (real font metrics leave rev 2's 320 with ~1 DIP of slack — r2 #7; see the layout budget below) |
| Surface | `Tokens.BrushSurfaceDark`, `CornerRadius = Tokens.LargeCornerRadius` |
| Title | "EyeCtrl" — `Tokens.BrushTextPrimary`, `Tokens.FontSizeDisplay` (34), `Tokens.FontWeightMedium` |
| No-tracker sentence | `Tokens.BrushTextPrimary`, `Tokens.FontSizeSubtitle` (18), `TextWrapping=Wrap`, centered, key `overlays.splash.noTracker` unchanged, `AutomationId="Splash_NoTracker"` kept; its **two-line row is always reserved** so showing it never reflows the card |

**Layout budget** (the German sentence drives it — `strings.de.json:730`: *"Kein Eyetracker gefunden – die Maus wird verwendet."*, 51 chars ≈ 460-510 DIP natural width at 18 DIP, wider than any sensible inner width, so it must wrap):

| Item | DIP |
|---|---|
| Padding top + bottom | 28 + 28 |
| Mark (incl. its tile backdrop) | 150 |
| Gap mark → title | 12 |
| Title line (34 DIP ≈ 45.2 line height) | 45.2 |
| Gap title → sentence | 8 |
| Reserved sentence row (2 × ≈ 23.9 line height) | 47.9 |
| **Total** | **319.1 + 16.9 slack → card height 336** |

Width 440 with 36 DIP side padding gives a 368 DIP inner width; the German sentence wraps to 2 lines (460-510 ≤ 2 × 368). The fit gate (§9) enforces the sentence against the card's inner width **and the full content height against 336** — not the work area (r2 #7).

### 4.1 The mark (the accepted icon's geometry, animated)

The mark plays **on the icon's own tile**: a 150 × 150 DIP rounded square, radius 37.5 (= the icon's rx24 on its 96 grid, scaled), fill `#141828` — in **every** theme (L12). On Midnight it nearly blends into the card (`#1A1A2E` surface vs `#141828` tile); on Ember and Contrast the navy tile reads as a distinct square (intentional — it is the icon's tile; the owner's live check covers both), and on Daylight (`SurfaceDark = #E7E8F0`, light) it keeps the icon's dark-on-dark look instead of washing out. Both mark colors are pinned to the icon's values, theme-independent:

| Element | Geometry | Color |
|---|---|---|
| Tile backdrop | 150 × 150, radius 37.5 | `#141828` (icon tile pin, L12) |
| Gaze dot | circle r = 12 at (75, 75) | amber `#E8A33C` (L6) |
| Bloom | circle r = 25.5 at (75, 75), radial gradient α .45 → .22 (55 %) → 0 | amber `#E8A33C` |
| Trail dots (3) | r = 2.6 / 3.75 / 4.9, arriving along the diagonal from (75 − 82.5, 75 − 63.75) to centre | amber `#E8A33C`; the lead dot bright amber `#F6C77E` |
| Dwell arc | r = 57, stroke 4.9, round caps; **rest state:** 120° arc from 12 o'clock clockwise — `M 75,18 A 57,57 0 0 1 124.36,103.5` | cyan `#A5F3FC` (L6 amended — theme accents are NOT cyan-family: Daylight's `Accent` is `#07343E` dark teal on a light surface) |

The mark host sets `ClipToBounds=false` — the trail dots' start point sits slightly outside the 150 box. The rest state is deliberately the icon's composition: amber dot + cyan 120°-from-12 arc on the dark tile.

## 5. Choreography (owner picks: "Gaze Arrives" → M1 mix → T3 timing)

| # | Beat | Window | Detail |
|---|---|---|---|
| 1 | Trail arrival | 0.00 – 0.45 s | Three dots decelerate in along the diagonal, staggered 0 / 0.05 / 0.10 s, each ~0.45 s ease-out; peak opacities .40 / .55 / .75; absorbed (fade to 0) by 0.55 s |
| 2 | Dot pop | 0.30 – 0.54 s | Gaze dot scale 0 → 1.15 → 1.0 with opacity; bloom fades to peak α .5 then settles to a gentle α .25 → .32 breath |
| 3 | Arc sweep + fill | starts 0.55 s | Arc group rotates **−180° → 0°** while the stroke **fills in** from its 12 o'clock end; decelerating ease (KeySpline ≈ `.3,.55,.3,1`); base pace = 3.0 s duration (unimpeded completion at 3.55 s). The arc starts fully hidden (opacity-gated until the sweep begins) so the round dash cap cannot paint a stray dot at 12 o'clock at 0 % fill |
| 4 | Title rise | starts when the fill completes, runs 0.25 s | Opacity 0 → 1, rise 8 px; **after** the arc lands |
| 5 | Exit fade | after a 0.20 s hold | Whole content opacity → 0 over 0.30 s, then `Close()` and the splash dispatcher shuts down |

The mark's elements then rest in the icon's composition until the exit fade covers everything. Fill mechanics: `StrokeDashArray` in stroke-thickness units — arc length = 57 · (2π/3) ≈ 119.4 px, stroke 4.9 → dash units ≈ 24.36; `StrokeDashOffset` animated 24.36 → 0; `StrokeDashCap`/line caps round. The plan verifies the offset sign/direction visually (rev 1's L10 render-thread claim is gone — on its own thread the storyboard simply ticks).

## 6. T3 — adaptive fill (owner pick) on one clock

1. **Stretch:** the fill runs its slow base course (3.0 s duration from its 0.55 s start) to completion regardless of startup. On long startups the base course outlasts the wait and the beat parks in **Filled** — the arc rests complete and the splash WAITS; nothing further happens until readiness, and the fixed tail below starts **at readiness** (r4 #5 — the tail never runs on the fill clock's schedule).
2. **Catch up on readiness (calm floor, L16 — owner pick V2):** when `Complete()` fires (see naming below), the remaining rotation and fill re-target to their final values **from their current animated values** (captured before the base clock is stopped; a DP read returns the effective animated value), cubic ease-out, over `duration = min(remaining base-course time, 1.0 s)` — never compressed harder than a readable 1.0 s floor, and never slower than the base pace (the catch-up only ever *shortens* what remains; a 0 s remainder skips straight to the title). The request is **accepted only from beats before the tail** (Idle, Entrance, Fill, Filled) and ignored once CatchUp/Title/Hold/Exit/Closed are running — a late readiness signal never re-runs the tail (r4 #5). Warm start (ready ≈ 0.45 s): the whole sweep + fill runs 0.45 → 1.45 s, title by ≈ 1.7 s, closed ≈ 2.2 s (ready → close ≈ 1.75 s). Slow starts are unchanged (the remaining course simply runs at base pace, then the tail runs at readiness).
3. **Fixed tail:** title rise 0.25 s → hold 0.20 s → exit fade 0.30 s → `Close()`. The worst-case ready → close is the floor catch-up (1.0 s) + tail (0.75 s) ≈ **1.75 s** — the click-through card under the already-raised overlays makes that tail cost-free (r2 #2).

**Naming (r1 #10):** the main thread calls `SplashWindow.BeginExit()`; that marshals onto the splash thread, where the controller's `Complete()` runs. One name per side, stated once here.

**`SplashExitController`** (new, `src/EyeCtrl.App/Overlays/SplashExitController.cs`, `internal`, covered by the existing `InternalsVisibleTo` App → App.Tests) owns the beats as a small state machine:

```
Idle → Entrance (beats 1-2) → Fill (base 3.0 s course) → CatchUp (≤ 1.0 s per L16, on Complete())
     → Title (0.25 s) → Hold (0.20 s) → Exit (0.30 s) → Closed
```

- **One clock (r1 #9):** every transition is driven by the WPF animation clock on the splash thread — storyboard `Completed` events for beats, a splash-thread `DispatcherTimer` for the hold. There is no second wall-clock bookkeeping that can disagree with what is on screen. The pure transition/catch-up logic is factored so unit tests exercise it without running storyboards.
- `Complete()` is idempotent and may be called from any state (including during Entrance — the catch-up then joins the fill at whatever point it has reached; if the fill has not started, the catch-up starts and completes it).
- **Dead-man close, reworked (r1 #3, L9):** the guard is "closes if not **Closed** by 15 s" — not "if `Complete()` never ran". `BeginExit()` additionally arms a hard deadline at readiness + **3.0 s** (raised from rev 2's 2.0 s: it must clear the 1.75 s V2 tail with margin — r2 #2). Both timers live on the splash thread, which pumps continuously (unlike the blocked main thread), so they actually fire. A `BeginExit()`/`Close()` arriving after a dead-man close is a no-op.
- Every public method is exception-contained: any fault in the animation degrades to an immediate `Close()` + dispatcher shutdown — never a stuck splash above a gaze-only user's screen. `Dispatcher.UnhandledException` on the splash thread is handled the same way (log, close, shut the splash thread down; the app starts without a splash rather than not at all).

## 7. Startup integration

**Thread model (L11):** at the current splash site (`App.xaml.cs:165-169`) the app starts a dedicated STA thread hosting the splash window and its `Dispatcher`. The main thread does **not** wait for the splash's first frame; its waits are bounded — **≤ 500 ms** for the thread to hand back the handle (a timeout **cancels** the splash before it can show, so no stray window is left behind) and **≤ 500 ms** for the DPI report — then startup continues unchanged (r4 #8). All splash work — construction, layout, storyboards, band calls, timers — happens on that thread, which pumps freely for the entire main-thread blockage. The thread entry wraps everything (window construction, XAML parse, `Dispatcher.Run`) in a try/catch — an exception before the dispatcher loop would otherwise kill the process — and degrades to "no splash, logged, app starts". The thread is `IsBackground = true`. A **window-factory seam** (`ISplashWindow`) lets tests inject a fake or the real window prepared-invisible, so no test can put a visible splash on screen (r2 #3).

| Site | Today | Change |
|---|---|---|
| `App.xaml.cs:165-169` | construct + `Show()` on main thread | start the splash thread; `splash` becomes a thread-safe handle/proxy exposing `ShowNoTrackerSentence()`, `BeginExit()`, `Close()` (each marshals to the splash thread; safe if the thread never started or already shut down) |
| `InitializeDpi` (`App.xaml.cs:996`) | reads the splash's `PresentationSource` on the main thread | reworked: the splash thread reads its own `CompositionTarget.TransformToDevice` and **reports** the scale to the main thread; **only the main thread calls `DpiHelper.Initialize`** — with the reported scale, or the `GetDpiForSystem` fallback on timeout (the same path test mode uses, `App.xaml.cs:1007-1012`). A report arriving after the timeout is discarded — the splash thread never writes the shared static, so a late report cannot overwrite the fallback mid-DI-build (r3 #2) |
| `App.xaml.cs:236` | `splash.ShowNoTrackerSentence()` | same call on the proxy — marshals; the sentence fades in (0.25 s) in its reserved row |
| `App.xaml.cs:380` → `ShowOverlays` (`:963`) | `splash?.Close()` | `splash?.BeginExit()` — marshals; the catch-up + tail then run on the splash thread |
| `App.xaml.cs:728` (catch) | `splash?.Close()` | `splash?.Close()` on the proxy (marshalled hard close, no theatrics); a no-op if the splash thread is gone |
| App exit | — | `OnExit` force-shuts the splash dispatcher if it is somehow still alive |

**Click-through (L13):** set in §4 — the card never intercepts gaze dwells during the tail.

**Z-order hand-off, corrected (r1 #8, r2 #4):** the overlays do **not** appear beneath the fading splash — each `Show()` raises its window to the top of the topmost band, so the cursor ring and toolbar land **above** the fading splash. That is exactly what the ring's always-visible guarantee wants; the splash fades out underneath them. The splash's own queued show-time band check runs on the splash thread immediately after `Show()` — before the main thread reaches the token line that sets `ProcessHasUiAccess` (≈ `App.xaml.cs:188`) — so `UiAccessEnabled()` is false, the check skips quietly, and it never re-runs (once per show): **no `Overlay band: SplashWindow` line appears** and the splash's band is never re-verified; the creation-time band raise on the splash thread already established it. If the fallback ever did fire despite that, the default order repair would call `ProcessReassertOverlayOrder` from the splash thread — touching main-thread-owned windows (the cross-thread exception would only be logged, `OverlayWindow.cs:304-314`) — so `SplashWindow` overrides `NeedsOrderRepairAfterBandFallback => false`.

**Startup log lines (r1 suggested, L15):** the splash writes `Splash: shown` (first frame), `Splash: ready` (`BeginExit`), and `Splash: closed` with elapsed-ms values. Acceptance criterion 2 is then checkable from the log, not only by eye.

**ProgressBar retirement (L4):** the indeterminate bar is removed — it cannot tick during the blockage today anyway. The arc fill is the only progress surface. The no-tracker sentence keeps its informational role in the reserved row.

## 8. Threading model and resources (replaces rev 1 §8 / L10)

- **Why a separate thread:** rev 1 claimed transform/opacity storyboards keep running while the UI thread is busy. That claim was wrong for this design — `OnStartup` runs synchronously from `splash.Show()` (`:168`) to ~line 725 (the try block's end) with no pumping, readiness at `:380` is followed by ≈ 0.35 s of further blockage (§2), and the fill (`StrokeDashOffset`), title, and catch-up are UI-thread-bound regardless. Whether WPF hands some transform animations to a render thread is beside the point and is no longer relied on; the plan's task-0 spike measures it only as recorded evidence.
- **The splash thread** is STA, owns the window, its `Dispatcher`, all storyboards, and its timers. It pumps for the whole main-thread blockage, so every beat plays as designed and the dead-man timers can fire.
- **Marshalled surface (the only cross-thread calls):** `ShowNoTrackerSentence()`, `BeginExit()`, `Close()` → `Dispatcher.BeginInvoke` onto the splash thread; the DPI scale report (splash → main, once, before the DI build). Anything arriving after the dispatcher shut down is a logged no-op.
- **Resources (r2 #5 — audit answered in code):** `ThemeBase.CachedBrush` freezes every brush at creation (`ThemeBase.cs:39`) and `CachedFont` publishes `FontFamily` as read-only-once-published (`ThemeBase.cs:48-49` — FontFamily cannot be frozen), so `Tokens` colors, brushes, and the base constructor's `Tokens.FontStandard` are safe from the splash thread. The splash still avoids App-level `StaticResource`s and `OverlayStyles.xaml` (XAML-declared brushes with thread affinity); it builds its own frozen brushes from `Tokens` color values (`Color` is a struct; reads are thread-safe) and the pinned icon constants (`#E8A33C`, `#A5F3FC`, `#F6C77E`, `#141828`). Fonts/brushes the splash creates stay on its thread.
- **Band/interaction statics** (`ProcessHasUiAccess`, `ProcessReassertOverlayOrder`) are plain statics set on the main thread; the splash thread reads them. The splash's show-time band check may run before `ProcessHasUiAccess` is set (it is set later, at the token line); it then skips quietly — the creation-time band raise already ran on the splash thread.
- Colors/geometry stay declarative XAML in the splash's own resource scope; the window must construct cleanly headless on any STA thread (`OverlayClientOriginTests` constructs every `OverlayWindow` subclass without showing it).

## 9. Testing

| Area | Test | Mode |
|---|---|---|
| Task-0 spike (not a test — recorded evidence) | Measure animated values across a blocked UI thread; prove the splash dispatcher pumps during main-thread blockage and the frozen-brush approach works. Runs **off-screen or windowless** (r2 #3). The resource audit is already answered in code (r2 #5) | disposable console spike, results committed to the plan |
| Controller logic | New: state transitions, `Complete()` idempotence from every state, catch-up parameter math (pure — covering remaining time below 0.35 s, between 0.35 and 1.0 s, and above 1.0 s, r3 #1), dead-man rules (not-Closed-by-15 s, readiness + 3.0 s hard deadline, re-entry no-op), exception containment | headless unit |
| Marshalling | Calls on the proxy from a non-splash thread marshal safely; calls after dispatcher shutdown are logged no-ops; fatal-path `Close` from the main thread works with the splash thread gone; a DPI report arriving after the 500 ms timeout is discarded and the main thread's fallback value stands (r3 #2) — all through the `ISplashWindow` factory seam (a fake, or the real window prepared-invisible; never a visible splash, r2 #3, rev 3.2) | headless/STA unit |
| Window construction | Existing `OverlayClientOriginTests` keeps constructing `SplashWindow` (new XAML must parse clean); new non-launching STA test: mark elements + tile backdrop present, sentence row reserved 2 lines, `AutomationId` intact, `IsClickThrough` true | `StaFact`, no `Show()` |
| Sentence fit | `GermanGateTests.TheSplashSentence_FitsTheReservedRow_InEnglishAndGerman` re-targeted: wrapped height ≤ reserved row at the card's inner width, natural width ≤ 2 × inner width, **and full real-content height ≤ 336** — all measured **on the real SplashWindow content**, not a detached TextBlock against work-area constants (r2 #7, r4 #6) — English, shipped German, and the existing 10× overlong stress case | headless |
| Source pins (r2 #8) | `SplashNoTrackerSentenceTests` (:54) pins the `SplashWindow.ShowsNoTrackerSentence(settings…)` call shape and that `.ShowNoTrackerSentence(` appears exactly once; `ControlStatusWiringTests` (:62) pins the predicate inside `new ControlStatusService(…)`; `ToolbarLifecycleCoordinatorTests` (:286) pins the literal `ShowOverlays(splash, cursorCircle, toolbar, settings);` — so the proxy local keeps the name `splash`, the predicate stays a static on `SplashWindow`, and the sentence call stays once | headless source-scan, must stay green |
| Resource independence | New: the window constructs on a bare STA thread with **no Application resources** (proves the §8 XAML rule) | headless/STA unit |
| Sentence visibility | `SplashNoTrackerSentenceTests.TheSentence_IsHiddenUntilAsked_AndReadsItsString` updated for the opacity-fade mechanism (hidden = opacity 0, not `Visibility.Collapsed`); the string-content assertions stay | STA, no `Show()` |
| Localization | `SingleSurfaceKeyTests`: `overlays.splash.noTracker` key survives (no new keys) | headless |
| Icon harness | `AppIconTests.Splash_Decode_Yields128PxFrame` keeps pinning WIC frame order; its comment says the splash's XAML renders that URI — the splash no longer hosts the ico `Image`; the comment is updated (test + assertion unchanged) | headless |
| App-launching FlaUI | Unchanged — no splash in test mode; no new opt-in tests | `EYECTRL_UI_TESTS` only |

No test puts a visible window on screen: the splash thread itself calls `Show` in production, so what keeps tests off-screen is the **window-factory seam** — tests inject a fake or invisible window and never start the real splash thread against a real `SplashWindow` (`InvisibleTestWindow` rules do not apply: no test calls `Show` on a real window). No named pipes involved.

## 10. Acceptance criteria

1. `dotnet build EyeCtrl.slnx` and the non-UI suite pass; no `EYECTRL_UI_TESTS` run needed for the gate.
2. **By log:** a launch shows `Splash: shown` → `Splash: ready` → `Splash: closed` with `closed − ready` ≤ ≈ 2.0 s (worst-case V2 tail 1.75 s + margin), and `ready − shown` matching the observed startup span.
3. **By eye (owner live check, owner-gated launch):** trail → dot pop → sweeping fill → title → fade hand-off; the resting mark reads as the accepted icon **on all four shipped themes** (Midnight, Ember, Daylight, Contrast — on Ember and Contrast the navy tile reads as a distinct square by design, r2 #6); the card never outstays the tail; the no-tracker sentence (when applicable) appears in the reserved row without reflow.
4. The splash can never linger: dead-man rules verified in controller unit tests; the failure path closes immediately via the marshalled hard close.
5. `--test-mode` shows no splash at all and DPI still initializes (test-mode fallback verified by existing tests).

## 11. Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|---|---|---|
| L1 | Choreography "Gaze Arrives" M1: discrete trail dots + 180° half-sweep fill | Owner picks (A → "a2 and a3 mixed" → M1) | Comet streak (A1), full sweep (A3 as shown), quarter sweep (M2), eye-opens (C), dwell-converges (B) |
| L2 | Timing = T3 adaptive fill | Owner pick; re-checked against the measured ≈ 0.45 s warm readiness via the V2 floor (L16) | T1 exit-on-ready (cuts mid-arc), T2 fixed 1.5 s beat |
| L3 | Framing = floating card | Owner pick (F3) | Full-screen dark canvas (F1), card on dimmed desktop (F2) |
| L4 | ProgressBar retired; arc fill = the only progress surface | T3 makes the arc honest progress; the old bar cannot tick during the blockage anyway | Keep both |
| L5 | Sentence below title, two-line row always reserved, fades in on fallback, `Wrap` | Same warning, no reflow, German fits | Drop the sentence; one-line row (German clips) |
| L6 | Amber `#E8A33C` and cyan `#A5F3FC` pinned to the icon's values (rev 2: both, not just amber) | The mark must match the accepted icon in every theme; theme accents are not cyan-family (Daylight `#07343E`) | Theme-driven mark colors |
| L7 | Readiness = the `ShowOverlays` call site; error path keeps a hard close | One-line integration, zero new startup callbacks | New readiness plumbing mid-startup |
| L8 | The dismissed duration question is superseded by L2 | T3 answers it directly | — |
| L9 | Dead-man close (reworked in rev 2): closes if not **Closed** by 15 s; `BeginExit` arms readiness + **3.0 s** (raised from 2.0 s in rev 3 — must clear the 1.75 s V2 tail, r2 #2); re-entry no-op | A faulting animation must never trap a gaze-only user; the exit path itself can hang too (r1 #3) | Guard only `Complete()`; no backstop |
| L10 | ~~`StrokeDashOffset` fill may stall under UI-thread blockage~~ **SUPERSEDED** — rev 1's render-thread claim was wrong; the splash thread makes the question moot | r1 #1/#2 | — |
| L11 | **Separate STA splash thread** with its own dispatcher; marshalled `ShowNoTrackerSentence`/`BeginExit`/`Close`; frozen-brush resources; reworked `InitializeDpi`; task-0 spike first | Owner pick after r1 #1/#2 — the only in-scope option that keeps T3 real; standard WPF splash pattern | Yield the dispatcher mid-`OnStartup` (reentrancy risk); static splash (drops the feature) |
| L12 | Mark plays on the icon's dark tile (`#141828`, rx 37.5) in every theme | Keeps the approved dark-on-dark mark look on Daylight's light surface | Theme-conditional backdrop; light-theme mark without backing |
| L13 | `IsClickThrough = true` on the splash | The gaze engine runs during the tail; a 440×336 card must not swallow dwells (r1 #4) | Keep it input-opaque |
| L14 | Card 440 × 336; fit gate = sentence rules **+ full content height ≤ 336** (EN + DE + 10× overlong) | German sentence needs wrap + reserved two lines (r1 #5/#6); real font metrics leave 320 with ~1 DIP slack (r2 #7) | 420×270 fixed; 440×320; work-area-based gate (passes while the real card clips) |
| L15 | `Splash: shown/ready/closed` log lines with elapsed-ms | Acceptance by log, not by eye (r1 suggested addition) | Eye-only acceptance |
| L16 | Catch-up calm floor = 1.0 s: `duration = min(remaining base-course time, 1.0 s)` (r3 #1: no 0.35 s lower bound — the catch-up only ever shortens what remains, never slows it below base pace) | Owner pick V2 from the warm-start demo (r2 #2): with readiness at ≈ 0.45 s the rev-2 0.35 s catch-up was a snap; the click-through card under already-raised overlays makes a calmer tail cost-free | V1 snap (0.35 s), V3 floor 1.4 s |

## 12. Work items (for the implementation plan)

0. **Threading spike (first):** blocked-UI-thread animation measurement (recorded evidence for §8), frozen-brush proof, splash-dispatcher pumping proof; runs **off-screen or windowless** (r2 #3). The freezability audit is answered in code (r2 #5) — task 2's XAML simply avoids `OverlayStyles.xaml`.
1. Layout budget + fit gate: card 440×336, reserved 2-line sentence row, re-targeted `GermanGateTests` (sentence + full content height).
2. Splash window content: mark on its tile, trail/dot/bloom/arc geometry + declared storyboards, title, sentence row (opacity fade), `IsClickThrough`.
3. `SplashExitController`: state machine, storyboard-`Completed`-driven transitions, catch-up from current values, dead-man rules, exception containment, dispatcher shutdown.
4. Splash thread + proxy: STA thread with thread-entry try/catch containment + `IsBackground`, window-factory seam for tests, marshalled `ShowNoTrackerSentence`/`BeginExit`/`Close`, `Splash:` log lines, bounded DPI wait + `InitializeDpi` rework, `OnExit` safety.
5. `App.xaml.cs` wiring: splash site → thread start + proxy; `ShowOverlays` → `BeginExit()`; catch path → proxy `Close()`.
6. Tests: controller logic, marshalling, construction/geometry STA, sentence visibility mechanism; `AppIconTests` comment touch-up.
7. Full gate + owner live check hand-off (launch gated on explicit owner OK, per standing rule).
