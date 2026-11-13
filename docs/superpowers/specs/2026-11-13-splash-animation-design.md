# Startup Splash Animation — Design

- **Date:** 2026-11-13
- **Status:** Approved in brainstorming (owner picked every visual decision live in the visual companion); this document records the design for the owner's spec review.
- **Scope:** `SplashWindow` (card content, geometry, choreography, adaptive timing), its exit hand-off, and the affected tests. No changes to startup ordering, DI, provider resolution, or any other overlay.

## 1. Summary

The splash becomes a short animated brand moment built from the accepted app icon's own vocabulary: amber gaze dots arrive, the gaze dot blooms, and a cyan dwell arc sweeps in a half-turn while filling, settling at the arc's 120° brand position before the title rises and the card fades into the working app. The arc fill doubles as the progress surface: it paces itself against real startup readiness (T3 "adaptive fill" — fill stretches on slow starts, catches up on fast ones, never cuts mid-arc, never repeats a beat). The framing stays today's floating card (F3), just larger. The indeterminate ProgressBar is retired; everything else about startup sequencing is untouched.

## 2. Context (measured, on this machine)

- The splash shows when not in test mode (`App.xaml.cs:165-169`) and closes at the readiness point `ShowOverlays` (`App.xaml.cs:380` → `App.xaml.cs:963`). The failure path hard-closes it (`App.xaml.cs:728`) before `Shutdown()`.
- Warm-start log span (2026-11-12 22:43 launch): splash visible ≈ 0.8-1.0 s (provider factory 22:43:28.48 → overlays band lines 22:43:29.28). Cold start (first launch after boot, trie/ONNX work) can take 2-3 s. Any fixed-length choreography longer than readiness either cuts mid-beat or must hold; the owner explicitly chose the adaptive rule instead (T3).
- The current splash is a `SizeToContent` card: 64 px ico `Image`, "EyeCtrl" `TextBlock`, indeterminate `ProgressBar`, and the hidden no-tracker sentence (`AutomationId="Splash_NoTracker"`, string key `overlays.splash.noTracker`).

## 3. Goals and non-goals

**Goals**

1. A professional, calm brand moment unmistakably EyeCtrl — built from the accepted icon's geometry (amber gaze dot + bloom + cyan 120° dwell arc) so splash and icon read as one system.
2. Timing that fits real startup: the arc fill is honest progress — stretches when startup is slow, catches up when startup is fast, completes exactly when readiness arrives (plus a short fixed tail).
3. Never delay control beyond that fixed tail; never trap startup behind an animation (dead-man close).

**Non-goals (YAGNI)**

- No sound; no settings row to enable/disable the animation; no `--no-splash` flag; no full-screen mode; no new localization keys (the brand title is untranslated; the sentence keeps its existing key); no changes to themes, the icon asset, or any other overlay.

## 4. The card (F3 — floating card, owner pick)

| Property | Value |
|---|---|
| Window | `SplashWindow : OverlayWindow` — unchanged base, `WindowStyle=None`, `AllowsTransparency=True`, `Topmost`, `ShowInTaskbar=False`, `WindowStartupLocation=CenterScreen` |
| Size | **Fixed** 420 × 270 DIP (replaces `SizeToContent`; the reserved sentence row needs stable space) |
| Surface | `Tokens.BrushSurfaceDark`, `CornerRadius = Tokens.LargeCornerRadius` |
| Title | "EyeCtrl" — `Tokens.BrushTextPrimary`, `Tokens.FontSizeDisplay`, `Tokens.FontWeightMedium` (as today) |
| No-tracker sentence | `Tokens.BrushTextPrimary`, `Tokens.FontSizeSubtitle`, centered, text = `overlays.splash.noTracker` (key unchanged), `AutomationId="Splash_NoTracker"` kept; its row is **always reserved** so showing it never reflows the card |

Content layout (top to bottom, centered): mark (150×150) → title → reserved sentence row.

### 4.1 The mark (the accepted icon's geometry, animated)

Mark canvas 150 × 150 DIP, centre (75, 75). All geometry is a 0.75× scale of the 200-unit mock the owner approved (`A2` trail dots + `M1` half-sweep).

| Element | Geometry | Color |
|---|---|---|
| Gaze dot | circle r = 12 at (75, 75) | amber `#E8A33C` (brand pin, L6) |
| Bloom | circle r = 25.5 at (75, 75), radial gradient: α .45 → .22 (55 %) → 0 | amber `#E8A33C` |
| Trail dots (3) | r = 2.6 / 3.75 / 4.9, arriving along the diagonal from (75-82.5, 75-63.75) to centre | amber `#E8A33C`; the lead dot bright amber `#F6C77E` |
| Dwell arc | r = 57, stroke 4.9, round caps; **rest state:** 120° arc from 12 o'clock clockwise — `M 75,18 A 57,57 0 0 1 124.36,103.5` | cyan `Tokens.BrushAccent` (Midnight `#A5F3FC`; all shipped themes are cyan-family) |

The rest state is deliberately the icon's composition: amber dot + cyan 120°-from-12 arc. When the animation ends, the splash looks like the icon came alive.

## 5. Choreography (owner picks: "Gaze Arrives" → M1 mix → T3 timing)

All properties animated are render-thread-friendly (opacity, scale/translate/rotate transforms) except the dash offset — see §8.

| # | Beat | Window | Detail |
|---|---|---|---|
| 1 | Trail arrival | 0.00 – 0.45 s | Three dots decelerate in along the diagonal, staggered 0 / 0.05 / 0.10 s, each ~0.45 s ease-out; peak opacities .40 / .55 / .75; absorbed (fade to 0) by 0.55 s |
| 2 | Dot pop | 0.30 – 0.54 s | Gaze dot scale 0 → 1.15 → 1.0 with opacity; bloom fades to peak α .5 then settles to a gentle α .25 → .32 breath |
| 3 | Arc sweep + fill | starts 0.55 s | Arc group rotates **−180° → 0°** while the stroke **fills in** from its start (12 o'clock end); decelerating ease (KeySpline ≈ `.3,.55,.3,1`); base pace = a full, unimpeded fill would complete at 3.55 s (3.0 s duration) |
| 4 | Title rise | starts when the fill completes, runs 0.25 s | Opacity 0 → 1, rise 8 px; **after** the arc lands |
| 5 | Exit fade | title + 0.20 s hold | Whole content opacity → 0 over 0.30 s, then `Close()` |

The mark's elements then rest in the icon's composition until the exit fade covers everything.

## 6. T3 — adaptive fill (owner pick)

The fill cannot know startup progress in advance, so it follows three rules (the owner approved the demo: warm start collapses toward a ~1.5 s beat; a simulated 2.2 s start stretched the fill to complete exactly on the ready tick):

1. **Stretch:** the fill runs its slow base course (3.0 s duration from its 0.55 s start) to completion regardless of startup. On long startups the base course outlasts the wait and the arc simply arrives on its own — it never stalls frozen and never restarts; on such starts the catch-up below becomes a no-op and only the fixed tail remains.
2. **Catch up on readiness:** when `Complete()` fires (§7), the remaining rotation and fill animate to their final values over **0.35 s**, cubic ease-out, from their current animated values.
3. **Fixed tail:** title rise 0.25 s → hold 0.20 s → exit fade 0.30 s → `Close()`. Control is never delayed beyond (readiness + ~1.1 s).

Concrete outcomes: warm start (ready ≈ 1.0 s) → catch-up 1.0-1.35 s, title by ≈ 1.6 s, close ≈ 2.1 s (≈ 1.1 s tail). Cold start (ready ≈ 2.5 s) → fill has stretched past its base pace, small catch-up, same tail.

**`SplashExitController`** (new, `src/EyeCtrl.App/Overlays/SplashExitController.cs`, `internal`, covered by the existing `InternalsVisibleTo` App → App.Tests) owns these beats as a small state machine:

```
Idle → Entrance (fixed beats 1-2) → Fill (stretchable) → CatchUp (0.35 s)
     → Title (0.25 s) → Hold (0.20 s) → Exit (0.30 s) → Closed
```

- `Complete()` is idempotent and may be called from any state (including during Entrance — the catch-up then joins the fill at whatever point it has reached; if the fill has not started, the catch-up starts it and completes it).
- Time sources are injectable (a monotonic clock abstraction) so the beat math is unit-testable headless; the WPF side applies progress through storyboards/`BeginAnimation`.
- Every public method is exception-contained: any fault in the animation must degrade to an immediate `Close()`, never to a stuck splash (the splash sits above a gaze-only user's screen).

## 7. Startup integration

| Site | Today | Change |
|---|---|---|
| `App.xaml.cs:167-168` | construct + `Show()` | unchanged |
| `App.xaml.cs:236` | `splash.ShowNoTrackerSentence()` | same call; the window now fades the sentence in (0.25 s) in its reserved row |
| `App.xaml.cs:380` → `ShowOverlays` (`:963`) | `splash?.Close()` | `splash?.BeginExit()` — the exit choreography runs while the real overlays appear beneath the fading splash (splash stays topmost during the fade; ordering/band behavior untouched) |
| `App.xaml.cs:728` (catch) | `splash?.Close()` | **unchanged** — failures close immediately, no theatrics |

**Dead-man close (L9):** the controller arms a 15 s timer at `Show()`. If `Complete()` has not fired by then (a controller fault, an unexpected startup hang that later recovers, anything), the splash closes outright. The splash must never become a trap.

**ProgressBar retirement (L4):** the indeterminate bar is removed. The arc fill is the only progress surface. The no-tracker sentence keeps its informational role in the reserved row.

## 8. WPF mechanism and known limits

- Entrance beats (opacity, scale, translate) and the sweep rotation are transform/opacity animations — eligible for WPF's render-thread composition, so motion continues even while the UI thread is busy during DI/wiring (the splash's entire visible window is one synchronous `OnStartup` stretch).
- The fill itself is a `StrokeDashOffset` animation on the arc `Path` (`StrokeDashArray` in stroke-thickness units: arc length = 57 · (2π/3) ≈ 119.4 px, stroke 4.9 → dash units ≈ 24.36; animate `StrokeDashOffset` 24.36 → 0; `StrokeDashCap`/`StrokeLineCap` round). `StrokeDashOffset` is **not** render-thread eligible: during a UI-thread blockage it can briefly stall while the rotation keeps moving. **Accepted (L10):** T3's defining moment is the catch-up at readiness — which runs after `OnStartup` returns, with a free UI thread — so a mid-fill stall is absorbed by the catch-up rather than visible as a wrong final state; the continuing rotation keeps the mark alive through the stall. The plan's first implementation task verifies the offset sign/direction visually.
- The catch-up re-targets from the *current animated values* (reading the DPs returns the effective animated value), so no progress bookkeeping can drift from what is on screen.
- Exit fade animates the root content element's opacity, not `Window.Opacity` (layered-window whole-surface redraws are the expensive path; content-level opacity stays cheap).
- All colors/geometry are declarative XAML resources; the window must construct cleanly headless (`OverlayClientOriginTests` constructs every `OverlayWindow` subclass without showing it) — no animation runs at construction.

## 9. Testing

| Area | Test | Mode |
|---|---|---|
| Controller beat math | New: state machine transitions, warm/cold catch-up math, idempotent `Complete()`, dead-man close, exception containment (fake clock) | headless unit |
| Window construction | Existing `OverlayClientOriginTests` keeps constructing `SplashWindow` (must parse the new XAML clean); new non-launching STA test: mark elements present, sentence row reserved, `AutomationId` intact | `StaFact`, no `Show()` |
| No-tracker logic | Existing `SplashNoTrackerSentenceTests` untouched | headless |
| Localization | `SingleSurfaceKeyTests`: `overlays.splash.noTracker` key survives (no new keys) | headless |
| Icon harness | `AppIconTests.Splash_Decode_Yields128PxFrame` keeps pinning WIC frame order, but its comment says the splash's XAML renders that URI — the splash no longer hosts the ico `Image`; the comment is updated (test + assertion unchanged) | headless |
| App-launching FlaUI | Unchanged — splash is skipped in test mode; no new opt-in tests | `EYECTRL_UI_TESTS` only |

No visible windows in any test (`InvisibleTestWindow` rules do not apply: nothing new calls `Show`). No named pipes involved. The visual acceptance is the owner's live check (§10), which is owner-gated as always.

## 10. Acceptance criteria

1. `dotnet build EyeCtrl.slnx` and the non-UI suite pass; no `EYECTRL_UI_TESTS` run needed for the gate.
2. Live check (owner, after install/launch): trail → dot pop → sweeping fill → title → fade hand-off visible; the resting mark reads as the accepted icon; the card never outstays the tail after readiness; no-tracker sentence (when applicable) appears in the reserved row without reflow.
3. The splash can never linger: dead-man close verified in the controller unit tests; failure path closes immediately (unchanged code).
4. `--test-mode` shows no splash at all (unchanged behavior).

## 11. Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|---|---|---|
| L1 | Choreography "Gaze Arrives" M1: discrete trail dots + 180° half-sweep fill | Owner picks (A → "a2 and a3 mixed" → M1) | Comet streak (A1), full sweep (A3 as shown), quarter sweep (M2), eye-opens (C), dwell-converges (B) |
| L2 | Timing = T3 adaptive fill | Owner pick; fits measured warm ~1.0 s / cold 2-3 s | T1 exit-on-ready (cuts mid-arc, title never shows warm), T2 fixed 1.5 s beat |
| L3 | Framing = floating card, larger (420×270) | Owner pick (F3) | Full-screen dark canvas (F1), card on dimmed desktop (F2) |
| L4 | ProgressBar retired; arc fill = the only progress surface | T3 makes the arc honest progress; two progress surfaces would compete | Keep both |
| L5 | Sentence below title, row always reserved, fades in on fallback | Same warning, no reflow | Drop the sentence (loses the fallback warning) |
| L6 | Amber pinned to the icon's `#E8A33C` (dot, trail, bloom); cyan/surface/text from theme tokens | The mark must match the accepted icon in every theme | Amber from `AccentWarning` per theme (drifts from the icon) |
| L7 | Readiness = the `ShowOverlays` call site; error path keeps hard `Close()` | One-line integration, zero new startup callbacks | New readiness plumbing mid-startup |
| L8 | The dismissed duration question (AskUserQuestion, unanswered) is superseded by L2 | T3 answers it directly | — |
| L9 | Dead-man close at 15 s | A faulting animation must never trap a gaze-only user behind the splash | No backstop |
| L10 | `StrokeDashOffset` fill may briefly stall under UI-thread blockage; absorbed by the ready-time catch-up | WPF render-thread composition covers transforms/opacity only; the alternative (rotation-only sweep) loses the approved draw look | Rotation-only sweep (different look than approved) |

## 12. Work items (for the implementation plan)

1. `SplashWindow.xaml` — new card content: mark canvas (dot/bloom/trail/arc with declared storyboards), title, reserved sentence row; fixed 420×270.
2. `SplashWindow.xaml.cs` — controller host and wiring: `BeginExit()`, `ShowNoTrackerSentence()` (fade-in), exception containment (the dead-man timer lives in the controller).
3. `SplashExitController.cs` (new) — beat state machine, injectable clock, catch-up from current values.
4. `App.xaml.cs` — `ShowOverlays` calls `BeginExit()` (one line); everything else untouched.
5. Tests — controller unit tests; non-launching STA construction/geometry test; `AppIconTests` comment touch-up.
6. Owner live check hand-off (launch gated on explicit owner OK, per standing rule).
