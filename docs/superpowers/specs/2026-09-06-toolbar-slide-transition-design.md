# Toolbar Slide Transition on Hide and Show

**Date:** 2026-09-06
**Status:** Draft — awaiting review
**Author:** EyeCtrl Team

## Problem

Auto-hide (see [2026-07-09-toolbar-auto-hide-design.md](2026-07-09-toolbar-auto-hide-design.md)) takes the docked toolbar away with a bare `Window.Hide()` and brings it back with a bare `Window.Show()` (`ToolbarVisibilityAdapter.cs:24-52`). The strip vanishes and reappears between two frames.

Two costs. The obvious one is that an instant appearance carries no direction: nothing on screen says the toolbar came from the edge the user just looked past, so the summon gesture teaches itself less than it could. The subtler one is that a full-width band popping into existence in peripheral vision reads as a glitch rather than as a response — for a user whose only input channel is gaze, an unexplained change in the visual field is a reason to look at it, which is a dwell hazard on a surface made entirely of dwell targets.

The toolbar should retract into its docked edge and emerge from it, so the motion names where it went and where it came from.

## Goals

- Hiding retracts the toolbar into the edge it is docked to; summoning slides it back out of that edge.
- The motion is directional: the axis and sign follow `ToolbarSettings.DockedEdge`, so a left-docked toolbar slides left, not up.
- Toolbar dwell is disarmed for the entire duration of either slide. A button must never be dwellable while it is not at the coordinate its dwell target was published at.
- The recovery paths stay instant. `ForceShow` is what un-strands a user; it must not spend 200 ms on presentation.
- An in-flight slide cannot leave the toolbar in a wrong terminal state — in particular, a superseded slide-out must not hide a toolbar that has since been force-shown.

## Non-Goals

- **No user-facing setting.** Duration and easing are `private const` in `ToolbarWindow`. No `ToolbarSettings` field, no `EnumerateSubSettings` entry, no settings row, no migration. Decided 2026-09-06; the constants are one localized edit if the feel proves wrong.
- **No OS-preference gate.** `SystemParameters.ClientAreaAnimation` (the Windows "Animation effects" toggle) is deliberately not read. Considered and dropped in the same decision as above: a second source of truth for whether motion happens buys nothing while the duration is a constant nobody can change.
- **No startup animation.** `App.xaml.cs:478-479` shows and positions the toolbar directly, not through the adapter. First paint stays instant.
- **No slide for any other overlay.** The pause indicator, edge button bar, quick-actions grid and keyboard keep their current show/hide behaviour.
- **No change to auto-hide's decision logic.** `ToolbarAutoHideController` is untouched: same states, same delays, same hysteresis, same `IToolbarVisibility` surface.
- **No change to the toolbar's contents, dock position, sizing, or dwell timing.**

## Terminology

- **Slide-out** — the hide transition: band at rest → band fully clear of the window, then `Window.Hide()`.
- **Slide-in** — the summon transition: band placed fully clear, `Window.Show()`, band → rest.
- **Extent** — the window's dimension along the docked axis (`ActualHeight` for Top/Bottom, `ActualWidth` for Left/Right), in DIPs.
- **Resting base value** — the value written directly to the `TranslateTransform` (as opposed to the animated value a live clock imposes on top of it).

## Design

### Approach

`ToolbarWindow` gains a `TranslateTransform` on its root content and two methods that animate it. `ToolbarVisibilityAdapter` calls those instead of `Window.Show()`/`Window.Hide()`. The existing division of labour is preserved and is the reason the code lands where it does: the window already owns where it sits and how it is drawn (`SetMode` at `ToolbarWindow.xaml.cs:211`), and the adapter already owns the ordering contract between dwell state and visibility (`ToolbarVisibilityAdapter.cs:31-45`). The window learns *how* to slide; the adapter keeps deciding *when*.

`IToolbarVisibility` does not change, so `ToolbarAutoHideController` and its entire test suite are untouched by this work.

Rejected alternatives:

- **Animate `Window.Left`/`Window.Top`.** This is what "slide the window" means literally, and it would slide the band past the *physical* screen edge rather than past its own docked rect — visually better when `SystemParameters.WorkArea` is inset on the docked side (taskbar on top, for a top-docked toolbar). Rejected on two counts. It is a `SetWindowPos` per frame on a topmost layered window (`AllowsTransparency="True"`, `ToolbarWindow.xaml:6`), on the same UI thread that renders the gaze cursor and services dwell updates — the cost `AdaptationCueService.cs:19-25` already records as a reason to throttle. And `ToolbarLifecycleCoordinator` caches the toolbar's rect in physical pixels for the gaze-thread hit test (`ToolbarLifecycleCoordinator.cs:143-152`); moving the window would make that cache wrong for the duration of every transition, on the very geometry the hide decision is computed against.
- **Animate `Window.Opacity` (fade instead of slide).** Cheap and already precedented in this codebase (`TargetHighlightWindow.xaml.cs:39-47`, `ActivationButtonWindow.xaml.cs:47-55`), but it carries no direction — the thing the summon gesture most needs the motion to say.
- **Animate the band's width/height.** Would squeeze the buttons rather than move them, so the dwell-target divergence grows in every direction at once instead of along one axis, and `SizeToContent` would re-measure the window each frame.

### Geometry: the slide vector

A pure function, so the whole direction table is testable without a window:

```csharp
internal static Vector SlideVectorFor(string? edgeKey, double width, double height)
```

| `edgeKey` | Hidden-state offset | Meaning |
|---|---|---|
| `"top"` | `(0, -height)` | retracts upward |
| `"bottom"` | `(0, +height)` | retracts downward |
| `"left"` | `(-width, 0)` | retracts leftward |
| `"right"` | `(+width, 0)` | retracts rightward |
| `null` / unrecognised | `(0, 0)` | no slide — degrades to instant show/hide |

`null` is the floating case, and it is reachable rather than defensive: `SetMode(isDocked: false, …)` normalises its edge to `"top"` for orientation purposes (`ToolbarWindow.xaml.cs:215`), which is the right answer for the strip's axis and the wrong one for a slide — an undocked toolbar has no edge to retract into. `SetMode` therefore records `isDocked ? key : null` as the slide edge, separately from the `_vertical` flag it already derives. Auto-hide is disabled when floating anyway (`ToolbarLifecycleCoordinator.cs:168`), so the only caller that can reach a floating window is `ForceShow`, which is instant regardless.

A zero vector is also the graceful answer for an unmeasured window (extent `0`), which is why the degradation is stated as a table row rather than guarded separately.

Both the transform and `ActualWidth`/`ActualHeight` are in DIPs, so — unlike the auto-hide rect — this geometry needs no `DpiHelper` conversion. Stated because the surrounding code's default is physical pixels.

### Clipping

The transform goes on the root `Border`, wrapped in a new `Grid ClipToBounds="True"`:

```xml
<Grid ClipToBounds="True">
    <Border Background="{StaticResource BrushSurfaceDark}" …>
        <Border.RenderTransform>
            <TranslateTransform x:Name="SlideTransform"/>
        </Border.RenderTransform>
        …
    </Border>
</Grid>
```

`RenderTransform`, not `LayoutTransform`: it does not participate in measure, so `SizeToContent="WidthAndHeight"` is unaffected and the window keeps the size the band computes for it (`ToolbarBand.LayOut`, `ToolbarBand.cs:165-194`).

The explicit `ClipToBounds` is the part worth defending. An HWND clips its own client area, so the overhang would very likely never render even without it — but "very likely" is the whole problem: that is a claim about window-composition behaviour under `AllowsTransparency`, and it cannot be asserted in a headless test. One element makes the clip a property of the visual tree instead of an assumption. The `Grid` measures to the `Border`'s desired size, so it costs nothing else.

### Sequencing

`HideToolbar(bool paused)`:

1. `_toolbarVm.SetActive(false)` — **unchanged, still first.** The existing comment at `ToolbarVisibilityAdapter.cs:33-35` explains why it precedes the hide; the slide widens that gap from one dispatcher tick to 200 ms, which makes the ordering more load-bearing, not less.
2. Start the slide-out.
3. On completion: `Window.Hide()`, then show or hide the pause indicator per `paused`.

Step 2 short-circuits when the window is already hidden: the completion effects run synchronously instead. Without that, `SetPaused`'s re-assertion of `HideToolbar` on an already-hidden toolbar (`ToolbarAutoHideController.cs:99-100`, and edge case #9) would animate a zero-distance slide on an invisible window and delay the pause indicator by 200 ms — feedback latency on the one path whose entire purpose is telling a paused user that the app is paused.

The indicator moving to step 3 is a small improvement the sequencing hands over for free: today it appears on the same tick the toolbar disappears, and it sits along the same docked edge (`PauseIndicatorWindow.SetEdge`). Showing it after the band has cleared reads as a handover instead of a swap.

`ShowToolbar()`:

1. `_overlays.PauseIndicator.Hide()` — first, so the indicator is not under the incoming band.
2. Start the slide-in — clock attached, running from the hidden offset.
3. `Window.Show()`.
4. On completion: `_toolbarVm.SetActive(true)`.

Steps 2 and 3 are in that order on purpose. Showing first and animating second would rest the correctness of "no frame is ever painted at the rest position" on the argument that no render pass can occur between two statements in one dispatcher callback. That argument happens to hold, and it is still the wrong thing to depend on. Attaching the clock first makes the off-edge start a property of the property's value rather than of dispatcher scheduling; animating a hidden window is fine, since the timing engine does not care about `Visibility`.

Dwell re-arms in step 4, not step 3, because `ToolbarViewModel`'s dwell targets are at fixed screen coordinates published by `ToolbarWindow.UpdateTargetPositions` (`ToolbarWindow.xaml.cs:265-290`) — the buttons' *resting* centres. During the slide-in the drawn button and its target disagree by up to the full extent, so an armed dwell would accumulate against a target the user cannot see and ignore the button they are looking at.

`ForceShow()` stays synchronous and gains only cancellation: cancel any in-flight slide, write the resting base value `0`, `Window.Show()`, `PauseIndicator.Hide()`, `SetActive(true)`. Its callers are the recovery paths — the tray fallback (`ToolbarLifecycleCoordinator.cs:118-123`), modal suspend-recover (`ToolbarAutoHideController.cs:111`) and the dock-toggle path (`:166`) — and every one of them exists because the user needs the toolbar *now*.

The cancellation in `ForceShow` is load-bearing rather than defensive, and one specific interleaving is why: `SetSuspended(true)` force-shows only when the controller is already in `Hidden` (`ToolbarAutoHideController.cs:109-111`), and the controller enters `Hidden` at the moment it calls `HideToolbar` (`:222-224`) — i.e. as the slide-out *starts*. So "modal opens 50 ms into a slide-out" lands exactly on a `ForceShow` that must both stop the animation and defuse its pending `Hide()`.

### Animation mechanics

One `DoubleAnimation` per slide, on the single axis the edge selects (`TranslateTransform.XProperty` or `YProperty`); the cross-axis is written to `0` directly and never animated. One animation means one `Completed`, so the completion contract has one owner. `BeginAnimation` matches the house pattern (`TargetHighlightWindow`, `ActivationButtonWindow`, `CueOverlayWindow`, `DriftNoticeWindow`); no `Storyboard` is needed for a single-property animation.

```
Duration = 200 ms       (matches TargetHighlightWindow.xaml.cs:40's fades)
Slide-in  easing: CubicEase, EasingMode.EaseOut   — decelerates into place
Slide-out easing: CubicEase, EasingMode.EaseIn    — accelerates away
```

**Clock lifetime.** One invariant covers both directions: **the transform's base value is always written to the slide's destination before the clock is attached**, and the clock uses `FillBehavior.Stop`.

```csharp
var from = SlideTransform.X;           // current animated value, read before the clock is cleared
CancelSlide();                         // remove clock, bump generation
SlideTransform.X = to;                 // base value == where this slide ends
var anim = new DoubleAnimation(from, to, Duration) { FillBehavior = FillBehavior.Stop, EasingFunction = ease };
```

`to` is the hidden offset for a slide-out and `0` for a slide-in; `From` is what keeps the visible position continuous, so the base value never has to equal the *starting* position. That distinction is the whole point, and getting it backwards is the trap: with `FillBehavior.Stop` the property reverts to its base value when the clock detaches, so a slide-in that left the base at the hidden offset would snap the band straight back off-screen on its final tick.

`HoldEnd` would avoid that snap and introduce a worse one — a held clock silently outranks any later direct assignment, so a subsequent `SlideTransform.X = 0` would be ignored and the toolbar would stay off-screen permanently, with no visible cause. `Stop` plus a pre-written base value has neither failure: nothing snaps, and no cleanup write has to race the final frame. It is also the choice `CueOverlayWindow.xaml.cs:43` and `DriftNoticeWindow.xaml.cs:58` already make, for the adjacent reason (reverting opacity to its XAML rest value).

**Superseded slides.** A generation counter, bumped on every slide start and on every cancellation; a `Completed` handler whose generation is stale does nothing:

```csharp
int generation = ++_slideGeneration;
anim.Completed += (_, _) => { if (generation == _slideGeneration) OnSlideCompleted(); };
```

The repo holds two comments that disagree on whether this is necessary. `DriftNoticeWindow.xaml.cs:36-39` states that `BeginAnimation(dp, null)` does not raise the removed clock's `Completed`, and concludes no guard is needed. `CueOverlayWindow.xaml.cs:20-21` hedges — "WPF may or may not raise `Completed` on a replaced animation" — and keeps a `_fadeGeneration`. The difference is removal versus replacement, and this design has both: `ForceShow` removes, while a slide-in started over a live slide-out replaces. Taking the guard follows the more conservative of the two precedents, and the asymmetry of being wrong decides it: a stale `Completed` here fires `Window.Hide()` on a toolbar that has just been force-shown, which strands the user with no visible controls — the exact failure the tray fallback exists to prevent. An `int` compare is not worth trading against that.

**Mid-flight reversal is not a live case.** The `from` value is read from the transform's current (animated) value so a reversal would be visually continuous, but the animated paths cannot actually produce one: a summon costs 400 ms of sustained off-edge gaze (`ToolbarLifecycleCoordinator.cs:180`) against a 200 ms slide, so any slide-out has finished before `ShowToolbar` can be called. The only real canceller is `ForceShow`, which is instant. Consequently the duration is **not** scaled by remaining distance — that would be code for a case nothing reaches.

### What must not run mid-slide

Two places read positional or visibility state as a proxy for "the toolbar is where the user sees it". Both are wrong for 200 ms unless told about the slide.

**Dwell-target placement.** `UpdateTargetPositions` publishes each button's centre via `PointToScreen` (`ToolbarWindow.xaml.cs:280`), which includes the render transform. It gains an early return while sliding, next to the existing `FacesAreArranged()` guard (`:272-273`) and with the same retry semantics — the gaze route keeps delivering samples until placement succeeds (`ToolbarLifecycleCoordinator.cs:80-94`), so a skipped attempt costs one frame.

This is reachable, not hypothetical: `SetMode` sets `_targetsInitialized = false` (`ToolbarWindow.xaml.cs:242`), so a re-dock leaves placement pending, and the next hide/show would otherwise pin every target to a mid-flight coordinate. The failure mode is the one `FacesAreArranged`'s doc comment already records for the collapsed-centre bug — `UpdateButtonScreenPosition` *creates* a target on first call, so a wrong first placement is permanent, and the toolbar's buttons stay undwellable until restart.

**`Toolbar.IsVisible` as a re-arm gate.** `OverlayVmBindingCoordinator.cs:345-350` re-arms toolbar dwell when the task switcher closes, gated on `Toolbar.IsVisible`, with a comment stating the intent precisely: "Reactivate only if the toolbar is actually on screen … activating a hidden toolbar creates gaze targets for invisible buttons — the exact hazard `SetActive`'s contract exists to prevent." A deferred `Hide()` makes `IsVisible` a poor proxy for that intent — during a slide-out it is `true` while the band is leaving, and during a slide-in it is `true` while the band is still off its targets.

`ToolbarWindow` therefore exposes what the call site actually means, and that call site switches to it:

```csharp
/// <summary>Visible AND at rest — i.e. every button is at the coordinate its dwell target was published at.</summary>
public bool IsOnScreen => IsVisible && !IsSliding;
```

This converges in all four interleavings. Task switcher closes during a slide-in: no re-arm here, and the slide-in's own completion re-arms. During a slide-out: no re-arm, and completion hides. While fully hidden: no re-arm, as today. While at rest: re-arms, as today.

## Wiring

- **`ToolbarWindow.xaml`** — wrap the root `Border` in `Grid ClipToBounds="True"`; add the named `TranslateTransform`.
- **`ToolbarWindow.xaml.cs`** — `SlideVectorFor`; the slide edge recorded in `SetMode`; `SlideOutThenHide(Action onHidden)`, `ShowThenSlideIn(Action onShown)`, `CancelSlide()`; `IsSliding`; `IsOnScreen`; the `UpdateTargetPositions` guard.
- **`ToolbarVisibilityAdapter.cs`** — `ShowToolbar`/`HideToolbar` re-sequenced as above; `ForceShow` gains `CancelSlide()`. Still one `Dispatcher.BeginInvoke` per call: the slide is started on the UI thread and its completion arrives there too, so no second hop is added.
- **`OverlayVmBindingCoordinator.cs:349`** — `Toolbar.IsVisible` → `Toolbar.IsOnScreen`.
- **Nothing else.** `IToolbarVisibility`, `ToolbarAutoHideController`, `ToolbarBand`, `ToolbarViewModel`, `ToolbarSettings`, `EnumerateSubSettings` and the settings pages are all untouched.

## Edge cases

1. **Startup** — `App.xaml.cs:478-479` shows the toolbar directly; the transform is at `0` and no slide runs. First paint is instant, as today.
2. **Floating toolbar** — slide edge is `null` → zero vector → instant. Auto-hide is disabled when floating (`ToolbarLifecycleCoordinator.cs:168`), so only `ForceShow` reaches a floating window anyway.
3. **Unmeasured window** — extent `0` → zero vector → instant show/hide. No separate guard.
4. **Modal opens mid-slide-out** — `SetSuspended(true)` → `ForceShow` cancels the clock, bumps the generation, writes base `0`, shows. The pending `Hide()` is defused by the generation check. See Animation mechanics.
5. **Tray "Show toolbar" mid-slide-out** — same path as #4 via `ToolbarLifecycleCoordinator.ForceShow`, plus `ResetToVisible()` so the controller does not immediately re-hide.
6. **Task switcher closes mid-slide** — `IsOnScreen` is false, so no re-arm; the slide's own completion re-arms (slide-in) or hides (slide-out).
7. **Dock edge changed in settings while hidden** — settings opening suspends auto-hide (`SettingsUiCoordinator.cs:117`), which force-shows and zeroes the transform, so `SetMode`'s reposition (`:177`) never runs against a translated band. `SetMode` then records the new slide edge, and the next slide uses the new axis.
8. **`IsDocked` toggled off at runtime** — `UpdateEnabled(false)` → `ForceShow` (instant) and the slide edge becomes `null` at the next `SetMode`.
9. **Pause toggled while hidden** — `SetPaused` re-asserts `HideToolbar(paused)` while already `Hidden` (`ToolbarAutoHideController.cs:99-100`). The toolbar is already hidden and the slide is a no-op re-run; only the indicator toggles. Worth naming because it is the one call site that reaches `HideToolbar` when the toolbar is *not* on screen, and it must not re-animate a hidden window or re-run the slide-out from `0`.
10. **App shutdown mid-slide** — `ShutdownMode="OnExplicitShutdown"`; a `Completed` handler that runs after the window is closed touches only `Hide()`/`SetActive`, both harmless on a closed window. No new disposal is needed.

## Settings

None. Duration and easing are `private const` in `ToolbarWindow`, and no `ToolbarSettings` field is added — so no `EnumerateSubSettings` entry and no `EyeCtrlSettingsEnumerationTests` involvement. Decided 2026-09-06 alongside "both directions" and "200 ms".

Should a toggle be wanted later, the shape is already right: `SlideVectorFor` returning a zero vector is the "no motion" path the whole design already degrades through, so an off switch is one early return, not a new code path.

## Testing

Repo conventions: xUnit v3, NFluent (`Check.That`), private nested fakes, no mocking library.

**Headless (no `Category=UI`)** — `SlideVectorFor` is pure:

- One case per edge (Top/Bottom/Left/Right): correct axis, correct sign, magnitude equal to the extent along that axis.
- `null` edge → zero vector.
- Unrecognised edge string → zero vector.
- Zero extent → zero vector.

**UI (`Category=UI`, `[StaFact]`)** — a test STA thread has no message pump, so animation clocks never tick there. This is the same constraint that produced the `EndFlash` seam (`ToolbarWindow.xaml.cs:404-409`, `ToolbarBand.cs:450-472`), and the completion path is tested the same way: driven directly rather than waited on.

- `UpdateTargetPositions` publishes nothing while sliding, and publishes correct centres once at rest. The sharpest test in the set — it guards the permanent-mispositioning failure.
- `IsOnScreen` is false during a slide-out and a slide-in, true at rest, false when hidden.
- Slide-out completion hides the window and then toggles the indicator per `paused`.
- Slide-in re-arms dwell only on completion, not on `Show()`.
- Slide-in attaches its clock before `Show()`: subscribing to `IsVisibleChanged` and asserting `IsSliding` is already true when it fires tests the ordering without depending on a clock tick — which a pumpless STA thread cannot deliver, so asserting the transform's animated value here would test nothing.
- `ForceShow` mid-slide: shows immediately, transform at `0`, and the superseded slide-out's completion does **not** hide the window. Directly covers edge cases #4 and #5.
- `SetPaused` while already hidden toggles the indicator without re-running a slide (edge case #9).

**Existing tests that must change.** `ToolbarVisibilityAdapterTests` (`:49`, `:112`, `:162`) encodes the old timing: `HideToolbar_CallsSetActiveFalse_BeforeToolbarHide` asserts exactly two ordered effects with `Hide` already done after one dispatcher pump, and the two show tests assert `SetActive(true)` has landed by then. All three are rewritten to the new contract — `SetActive(false)` before the slide *starts*, `Hide` on completion, `SetActive(true)` on completion. The ordering guarantee they exist to protect is preserved and strengthened; only the tick it is observed on moves. These are `Category=UI`, so CI's headless subset never ran them (`ci-runs-app-tests-headless-subset`) and local verification is the only signal.

**Mutation check before any green claim.** Per standing instruction, each of these must turn a test red:

| Mutation | Must fail |
|---|---|
| Flip a sign in `SlideVectorFor` | the per-edge direction tests |
| Drop the `IsSliding` guard in `UpdateTargetPositions` | the mid-slide placement test |
| Move `SetActive(true)` from slide-in completion to its start | the re-arm-on-completion test |
| Revert `IsOnScreen` to `IsVisible` | the `IsOnScreen`-during-slide test |
| Remove the generation guard | the `ForceShow`-mid-slide test |
| Change `FillBehavior.Stop` to `HoldEnd` without writing the base value | a second slide after the first (the stuck-off-screen trap) |

Read-only review does not substitute for this; five rounds of it previously missed three one-line breakages with the suite green.

**No visual check** is performed without an explicit go-ahead, and any such run is kept short.

## Open questions

1. **[TUNING, post-visual] Does 200 ms read as responsive or as lag on the summon?** A summon already costs 400 ms of dwell, so the slide-in puts the toolbar 600 ms behind the gesture. If that reads as sluggish, the smaller change is not a shorter duration but moving `SetActive(true)` to the *start* of the slide-in — accepting up to 200 ms of target/visual divergence in exchange for immediate arming. That trade was consciously declined here (see Sequencing step 4) and should stay declined unless real use argues otherwise.

2. **[DEFERRED] Should the pause indicator slide too?** It occupies the edge the toolbar retracts into, so the handover is currently a slide followed by a pop. Out of scope, and not obviously worth it for an 8-DIP bar (`PauseIndicatorWindow.BarThicknessLogical`).
