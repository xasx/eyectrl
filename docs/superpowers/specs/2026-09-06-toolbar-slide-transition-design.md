# Toolbar Slide Transition on Hide and Show

**Date:** 2026-09-06
**Status:** Reviewed (round 1 applied 2026-09-06 — 8 findings, all confirmed against source)
**Author:** EyeCtrl Team

## Problem

Auto-hide (see [2026-07-09-toolbar-auto-hide-design.md](2026-07-09-toolbar-auto-hide-design.md)) takes the docked toolbar away with a bare `Window.Hide()` and brings it back with a bare `Window.Show()` (`ToolbarVisibilityAdapter.cs:24-52`). The strip vanishes and reappears between two frames.

Two costs. The obvious one is that an instant appearance carries no direction: nothing on screen says the toolbar came from the edge the user just looked past, so the summon gesture teaches itself less than it could. The subtler one is that a full-width band popping into existence in peripheral vision reads as a glitch rather than as a response — for a user whose only input channel is gaze, an unexplained change in the visual field is a reason to look at it, which is a dwell hazard on a surface made entirely of dwell targets.

The toolbar should retract into its docked edge and emerge from it, so the motion names where it went and where it came from.

## Goals

- Hiding retracts the toolbar into the edge it is docked to; summoning slides it back out of that edge.
- The motion is directional: the axis and sign follow `ToolbarSettings.DockedEdge`, so a left-docked toolbar slides left, not up.
- Toolbar dwell is disarmed for the entire slide-**out**, before the band starts leaving. A button must never be dwellable once it is on its way to being hidden.
- Toolbar dwell arms at the *start* of the slide-**in**, so a summoned toolbar is usable the moment it appears. This deliberately accepts up to 200 ms during which a button is drawn away from the coordinate its dwell target sits at; open question 1 records why that is the better trade here.
- Dwell-target *placement* is suppressed for the whole of either slide, in both directions. Arming against a stale-but-correct coordinate is recoverable; publishing a mid-flight coordinate is not.
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
3. On completion: clear the sliding flags, `Window.Hide()`, then show or hide the pause indicator per `paused`.

Step 2 short-circuits when the window is already hidden: the completion effects run synchronously instead. Without that, `SetPaused`'s re-assertion of `HideToolbar` on an already-hidden toolbar (`ToolbarAutoHideController.cs:99-100`, and edge case #11) would animate a zero-distance slide on an invisible window and delay the pause indicator by 200 ms — feedback latency on the one path whose entire purpose is telling a paused user that the app is paused.

The indicator moving to step 3 is a small improvement the sequencing hands over for free: today it appears on the same tick the toolbar disappears, and it sits along the same docked edge (`PauseIndicatorWindow.SetEdge`). Showing it after the band has cleared reads as a handover instead of a swap.

`ShowToolbar()`:

1. `_overlays.PauseIndicator.Hide()` — first, so the indicator is not under the incoming band.
2. Start the slide-in — clock attached, running from the hidden offset.
3. `Window.Show()`.
4. `_toolbarVm.SetActive(true)` — synchronously, in the same callback. **Not** deferred to the slide's completion.
5. On completion: clear the sliding flags. No dwell-state change.

Steps 2 and 3 are in that order on purpose. Showing first and animating second would rest the correctness of "no frame is ever painted at the rest position" on the argument that no render pass can occur between two statements in one dispatcher callback. That argument happens to hold, and it is still the wrong thing to depend on. Attaching the clock first makes the off-edge start a property of the property's value rather than of dispatcher scheduling; animating a hidden window is fine, since the timing engine does not care about `Visibility`.

**Dwell arms at the start of the slide-in, accepting up to 200 ms of divergence between where a button is drawn and where its dwell target sits.** Decided 2026-09-06; open question 1 records the reasoning and what it costs. `ToolbarViewModel`'s targets are at fixed screen coordinates published by `ToolbarWindow.UpdateTargetPositions` (`ToolbarWindow.xaml.cs:265-290`) — the buttons' *resting* centres — so for the duration of the slide-in, gaze resting on a button's final position accumulates against it while the drawn button is still arriving there.

Step 4 follows step 3 rather than preceding it, keeping the hide side's principle intact: the window reaches its safe configuration before dwell arms. Arming a hidden window is the hazard `SetActive`'s contract exists to prevent (stated at `OverlayVmBindingCoordinator.cs:345-348`), and while nothing can observe the gap inside one synchronous callback, ordering it correctly costs nothing and does not depend on that being true.

One consequence to hold onto: because arming no longer happens on completion, **the slide-in's completion no longer repairs a dwell state that something else cleared mid-slide.** That is what forces the directional `IsOnScreen` below — get it wrong and the toolbar can end up visible, at rest, and permanently unarmed.

`ForceShow()` stays synchronous and gains only cancellation: cancel any in-flight slide, write the resting base value `0`, `Window.Show()`, `PauseIndicator.Hide()`, `SetActive(true)`. Its callers are the recovery paths — the tray fallback (`ToolbarLifecycleCoordinator.cs:118-123`), modal suspend-recover (`ToolbarAutoHideController.cs:111`) and the dock-toggle path (`:167`) — and every one of them exists because the user needs the toolbar *now*.

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

**`CancelSlide()` is unconditional, idempotent, and clears the flags.** All three properties are load-bearing, and each one was missing from an earlier draft of this spec:

```csharp
private void CancelSlide()
{
    _slideGeneration++;                                  // any pending Completed becomes a no-op
    SlideTransform.BeginAnimation(_axis, null);          // unconditional — see below
    _sliding = _slidingOut = false;                      // nothing else will clear these
}
```

*Clears the flags*, because the generation bump it performs one line earlier is precisely what stops the cancelled slide's `Completed` from running — so the completion handler cannot be the thing that clears them. Without this line, `ForceShow` mid-slide-out (edge case #4, the flagship interleaving) leaves the toolbar visible and at rest with both flags stuck true: `IsOnScreen` reads false forever, so a task-switcher open/close finds the re-arm gate shut and arms nothing — a visible toolbar that ignores gaze, which is exactly the stranding the directional gate was introduced to prevent. `UpdateTargetPositions` stays suppressed too. Self-repairing only on the next full hide-and-summon cycle.

*Unconditional*, i.e. **not** guarded on a slide being in flight. A completed slide leaves no live clock, so removal looks pointless — but `BeginAnimation(dp, null)` is also what guarantees no *stale* clock outranks the base-value write that follows in `ForceShow`. Skipping it when "no slide is in flight" reintroduces the `HoldEnd` failure by the back door, and the cost of always calling it is one no-op call on a path that runs at most a few times per session.

*Idempotent*, so `ForceShow` calling it and a slide start calling it need no coordination.

**Superseded slides.** A generation counter, bumped on every slide start and on every cancellation; a `Completed` handler whose generation is stale does nothing:

```csharp
int generation = ++_slideGeneration;
anim.Completed += (_, _) => { if (generation == _slideGeneration) OnSlideCompleted(); };
```

The repo holds two comments that disagree on whether this is necessary. `DriftNoticeWindow.xaml.cs:36-39` states that `BeginAnimation(dp, null)` does not raise the removed clock's `Completed`, and concludes no guard is needed. `CueOverlayWindow.xaml.cs:20-21` hedges — "WPF may or may not raise `Completed` on a replaced animation" — and keeps a `_fadeGeneration`. The framework documentation sides with the first: [`Timeline.Completed`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.timeline.completed) states that stopping a timeline does not trigger its completed event.

**So the guard is insurance, not necessity, and it is kept deliberately.** Being precise about that matters, because an earlier draft of this spec justified it by the replacement case — a slide-in started over a live slide-out — which its own reachability analysis below rules out. With the replacement case unreachable and removal documented not to raise `Completed`, no live path needs the counter today. It stays for two reasons: the asymmetry of being wrong is brutal (a stale `Completed` fires `Window.Hide()` on a toolbar that has just been force-shown, stranding the user with no visible controls — the exact failure the tray fallback exists to prevent), and an `int` compare is not a cost. What the guard must *not* do is stand in for the flag-clearing above; that was the earlier draft's actual defect.

**Mid-flight reversal is not a live case.** The `from` value is read from the transform's current (animated) value so a reversal would be visually continuous, but the animated paths cannot actually produce one: a summon costs 400 ms of sustained off-edge gaze (`ToolbarLifecycleCoordinator.cs:180`) against a 200 ms slide, so any slide-out has finished before `ShowToolbar` can be called. The only real canceller is `ForceShow`, which is instant. Consequently the duration is **not** scaled by remaining distance — that would be code for a case nothing reaches.

### What must not run mid-slide

Two places read positional or visibility state as a proxy for "the toolbar is where the user sees it". Introducing a transform breaks both — the re-arm gate for the 200 ms of a slide-out, and dwell-target placement for that *plus* the whole hidden period, since the transform stays parked at the offset while hidden. They need to be told *different things*, which is why the window carries two flags rather than one:

| Flag | True during | Question it answers |
|---|---|---|
| `IsSliding` | slide-in **and** slide-out | Is the transform in motion? |
| `IsSlidingOut` | slide-out only | Is the band on its way to being hidden? |

Collapsing these into one flag is the obvious-looking simplification and it breaks something in either direction, so the distinction is load-bearing: coordinate *publication* must be suppressed both ways (a mid-flight coordinate is permanent), while dwell *arming* is now permitted during a slide-in by decision and forbidden during a slide-out because that one ends in `Hide()`.

**Dwell-target placement** — gated on `IsVisible && !IsSliding`. `UpdateTargetPositions` publishes each button's centre via `PointToScreen` (`ToolbarWindow.xaml.cs:280`), which includes the render transform. It gains an early return unless the window is visible and the transform is at rest, next to the existing `FacesAreArranged()` guard (`:272-273`) and with the same retry semantics — the gaze route keeps delivering samples until placement succeeds (`ToolbarLifecycleCoordinator.cs:80-94`), so a skipped attempt costs one frame.

This guard is unaffected by the arming decision, and the two must not be conflated. Arming during a slide-in means dwell accumulates against targets already sitting at their correct resting coordinates — bounded, self-correcting, over in 200 ms. Publishing during a slide-in would move those coordinates to a wrong place and leave them there.

**`IsVisible` is part of the predicate, not decoration.** The transform's base value sits at the hidden offset for the *entire* hidden period, not merely while a clock is live — the slide-out writes it there and nothing resets it until the next slide-in or `ForceShow`, deliberately, because the slide-in's `from` read depends on it. A placement that runs while the toolbar is hidden therefore publishes rest-plus-offset for every button and latches it, since `UpdateButtonScreenPosition` *creates* a target on first call (`ToolbarViewModel.cs:50-58`). Note what that means: the hidden state is safe today — with no transform, a placement while hidden publishes correct coordinates — so this change is what would make it hazardous. A gate on `IsSliding` alone would cover the 200 ms the clock is live and miss the open-ended window on either side of it.

**Neither conjunct is currently reachable, and both are kept anyway.** An earlier draft justified this guard by claiming a re-dock leaves placement pending into the next hide/show. That is false, and the correction is worth recording so nobody re-derives it: after `SetMode` (`ToolbarWindow.xaml.cs:242`) the gaze route's early-out is disabled while the settings window is open, `SetMode` forces `UpdateLayout` (`:233`) so the faces *are* arranged, the toolbar is visible and at rest (opening settings force-shows it), and auto-hide stays suspended until the settings window closes (`SettingsUiCoordinator.cs:203`) whereas `SetMode` runs on save (`:177`) — so placement completes within a frame or two of the next gaze sample, while a hide needs 5 s of them. "Placement pending *and* a slide in flight" is not a state this pipeline can currently hold.

The guard stays regardless, for the reason `FacesAreArranged`'s own doc comment gives: a wrong first placement is permanent and leaves the toolbar's buttons undwellable until restart, and that shipped once already. Two cheap conjuncts against a permanent, restart-only failure is the same trade that guard already makes.

**`Toolbar.IsVisible` as a re-arm gate** — gated on `IsSlidingOut`. `OverlayVmBindingCoordinator.cs:345-350` re-arms toolbar dwell when the task switcher closes, gated on `Toolbar.IsVisible`, with a comment stating the intent precisely: "Reactivate only if the toolbar is actually on screen … activating a hidden toolbar creates gaze targets for invisible buttons — the exact hazard `SetActive`'s contract exists to prevent." A deferred `Hide()` makes `IsVisible` a poor proxy for that intent: during a slide-out it is `true` while the band is leaving.

`ToolbarWindow` therefore exposes what the call site actually means, and that call site switches to it:

```csharp
/// <summary>
/// Visible and not on its way out — i.e. arming dwell against the published target
/// coordinates is safe. True during a slide-IN (see Sequencing), false during a slide-out.
/// </summary>
public bool IsOnScreen => IsVisible && !IsSlidingOut;
```

**`!IsSlidingOut`, not `!IsSliding`, and this is the part the arming decision changed.** Once `SetActive(true)` moved to the start of the slide-in, the slide-in's completion stopped repairing dwell state — so a gate that reported "not on screen" during a slide-in would produce this sequence: slide-in arms dwell → task switcher opens and calls `SetActive(false)` (`:339`) → task switcher closes before the slide finishes → gate says no → completion arms nothing → **toolbar visible, at rest, and dwellable by nothing.** It self-repairs only on the next full hide-and-summon cycle. A 200 ms window is a poor reason to relax about that; this is a codebase where a similar first-placement race shipped and left no toolbar button dwellable at all.

With the directional gate it converges in all four interleavings. Task switcher closes during a slide-in: re-arms, correctly, because the band is visible and staying. During a slide-out: no re-arm, and the completion hides. While fully hidden: no re-arm, as today. While at rest: re-arms, as today.

## Wiring

- **`ToolbarWindow.xaml`** — wrap the root `Border` in `Grid ClipToBounds="True"`; add the named `TranslateTransform`.
- **`ToolbarWindow.xaml.cs`** — `SlideVectorFor`; the slide edge recorded in `SetMode`; `SlideOutThenHide(Action onHidden)`, `ShowThenSlideIn()`, `CancelSlide()`; `IsSliding` and `IsSlidingOut`; `IsOnScreen`; the `IsVisible && !IsSliding` guard in `UpdateTargetPositions`.
- **`ToolbarVisibilityAdapter.cs`** — `ShowToolbar`/`HideToolbar` re-sequenced as above; `ForceShow` gains `CancelSlide()`. Still one `Dispatcher.BeginInvoke` per call: the slide is started on the UI thread and its completion arrives there too, so no second hop is added.
- **`OverlayVmBindingCoordinator.cs:349`** — `Toolbar.IsVisible` → `Toolbar.IsOnScreen`.
- **Nothing else.** `IToolbarVisibility`, `ToolbarAutoHideController`, `ToolbarBand`, `ToolbarViewModel`, `ToolbarSettings`, `EnumerateSubSettings` and the settings pages are all untouched.

## Edge cases

1. **Startup** — `App.xaml.cs:478-479` shows the toolbar directly; the transform is at `0` and no slide runs. First paint is instant, as today.
2. **Floating toolbar** — slide edge is `null` → zero vector → instant. Auto-hide is disabled when floating (`ToolbarLifecycleCoordinator.cs:168`), so only `ForceShow` reaches a floating window anyway.
3. **Unmeasured window** — extent `0` → zero vector → instant show/hide. No separate guard.
4. **Modal opens mid-slide-out** — `SetSuspended(true)` → `ForceShow` cancels the clock, bumps the generation, writes base `0`, shows. The pending `Hide()` is defused by the generation check. See Animation mechanics.
5. **Tray "Show toolbar" mid-slide-out** — same path as #4 via `ToolbarLifecycleCoordinator.ForceShow`, plus `ResetToVisible()` so the controller does not immediately re-hide.
6. **Task switcher closes mid-slide-in** — `IsOnScreen` is true, so dwell re-arms. Load-bearing: nothing downstream would arm it, since the slide-in no longer arms on completion. See "What must not run mid-slide".
7. **Task switcher closes mid-slide-out** — `IsOnScreen` is false, so no re-arm; the slide-out's completion hides the window with dwell already disarmed.
8. **`ShowToolbar` on an already-visible, at-rest toolbar** — short-circuits to arming dwell only, no animation. Unreachable from the controller (it calls `ShowToolbar` only from `Hidden`, `ToolbarAutoHideController.cs:242-245`) but reached by `ToolbarVisibilityAdapterTests`, and a gratuitous jump-to-offset-then-slide-back would be a real glitch if a future caller did it. The condition is `IsOnScreen` itself, which deliberately does *not* swallow a slide-out in flight — that case still runs the full slide-in and cancels the outgoing clock.
9. **Dock edge changed in settings while hidden** — settings opening suspends auto-hide (`SettingsUiCoordinator.cs:117`), which force-shows and zeroes the transform, so `SetMode`'s reposition (`:177`) never runs against a translated band. `SetMode` then records the new slide edge, and the next slide uses the new axis.
10. **`IsDocked` toggled off at runtime** — `UpdateEnabled(false)` → `ForceShow` (instant) and the slide edge becomes `null` at the next `SetMode`.
11. **`HideToolbar` called while already hidden** — reached from **two** call sites, not one, and both must hit the step-2 short-circuit rather than animating an invisible window. `SetPaused` re-asserts `HideToolbar(_paused)` while already `Hidden` (`ToolbarAutoHideController.cs:99-100`), and so does `ReassertVisibility` (`:137-144`), which the calibration-terminal path calls at `OverlayMessageCoordinator.cs:239` and `:250`. The short-circuit handles both identically; a re-assert arriving *mid*-slide-out is also safe, since it restarts from the current `from` value under the generation guard.
12. **App shutdown mid-slide** — the completion handler must be guarded. `Window.Hide()` is documented to throw `InvalidOperationException` when called on a window that is closing or has closed ([Window.Hide, Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.hide?view=windowsdesktop-10.0)) — an earlier draft of this spec wrongly called it harmless. `SetActive` on the view-model genuinely is harmless. Whether a `Completed` can even fire after the window closes under `ShutdownMode="OnExplicitShutdown"` is doubtful (the timing engine stops with the render loop) and is not worth establishing: the completion handler checks before hiding, which costs one condition.

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

- `UpdateTargetPositions` publishes nothing while sliding — in **both** directions — and publishes correct centres once at rest. The sharpest test in the set: it guards the permanent-mispositioning failure, and it is the one that stays honest about the slide-in now that arming no longer waits.
- `UpdateTargetPositions` publishes nothing while the window is hidden, with the transform parked at the hidden offset. Drive it by completing a slide-out and then calling `UpdateGaze` with `_targetsInitialized` false. Without the `IsVisible` conjunct this publishes rest-plus-offset and latches it.
- `IsOnScreen` is **true** during a slide-in, false during a slide-out, true at rest, false when hidden. Written as four assertions in one test so the directional asymmetry cannot be half-broken.
- Slide-out completion hides the window and then toggles the indicator per `paused`.
- Slide-in arms dwell synchronously: after `ShowToolbar` returns (one dispatcher pump), gaze on a positioned target accumulates, without the completion seam having been driven.
- Slide-in attaches its clock before `Show()`: subscribing to `IsVisibleChanged` and asserting `IsSliding` is already true when it fires tests the ordering without depending on a clock tick — which a pumpless STA thread cannot deliver, so asserting the transform's animated value here would test nothing.
- Task switcher closing mid-slide-in leaves dwell armed (edge case #6). This is the regression test for the stranding the directional gate exists to prevent; it fails if `IsOnScreen` is defined on `IsSliding`. **It needs a new harness**: `OverlayVmBindingCoordinatorTests` is 42 lines of constructor/dispose tests with no `EventCoordinationContext` scaffolding, and the gate at `OverlayVmBindingCoordinator.cs:349` has *zero* coverage today. Build it from the `FakeOverlayManager` pattern in `ToolbarVisibilityAdapterTests.cs:238-268`. Budget for this — it is not an extra assertion on an existing test.
- `ForceShow` mid-slide: shows immediately, transform at `0`, the superseded slide-out's completion does **not** hide the window, **and both sliding flags read false afterwards**. Directly covers edge cases #4 and #5. The flags assertion is the one that catches the `CancelSlide` defect; show/transform/no-hide all pass without it.
- `ForceShow` *after* a completed slide-out shows the toolbar at transform `0`. This is the test that fails if cancellation is made conditional on a slide being in flight — the back-door route to the stuck-off-screen trap.
- `HideToolbar` while already hidden toggles the indicator without re-running a slide, driven from **both** call sites — `SetPaused` and `ReassertVisibility` (edge case #11).

**Existing tests that must change — one, not three.** `ToolbarVisibilityAdapterTests` encodes the old timing, but arming synchronously means only the hide test actually moves:

| Test | Fate |
|---|---|
| `HideToolbar_CallsSetActiveFalse_BeforeToolbarHide` (`:49`) | **Rewritten.** It asserts exactly two ordered effects with `Hide` already done after one dispatcher pump; `Hide` now lands on the slide's completion. The contract becomes `SetActive(false)` before the slide *starts*, `Hide` when it finishes — the guarantee it protects is preserved and strengthened, only the tick it is observed on moves. |
| `ShowToolbar_CallsSetActiveTrue` (`:112`) | **Passes unchanged.** `SetActive(true)` is synchronous in the same callback, so the existing "gaze accumulates after one pump" assertion still holds. Its `toolbar.Show()` preamble makes the window already-visible, which is exactly what edge case #8's short-circuit is for. |
| `ForceShow_CallsSetActiveTrue` (`:162`) | **Passes unchanged.** `ForceShow` stays instant. |

That both show tests survive is a consequence of the arming decision, not an accident of it — and it is the cheapest available evidence that the decision reduced this change's blast radius rather than enlarging it. All three are `Category=UI`, and CI runs `dotnet test --filter "Category!=UI"` (`.github/workflows/ci.yml:84`), so it never ran them — local verification is the only signal here.

**Mutation check before any green claim.** Per standing instruction, each of these must turn a test red:

| # | Mutation | Must fail |
|---|---|---|
| 1 | Flip a sign in `SlideVectorFor` | the per-edge direction tests |
| 2 | Drop the `!IsSliding` conjunct in `UpdateTargetPositions` | the mid-slide placement test |
| 3 | Drop the `IsVisible` conjunct in `UpdateTargetPositions` | the placement-while-hidden test |
| 4 | Move `SetActive(true)` from the slide-in *start* to its completion | the synchronous-arming test |
| 5 | Define `IsOnScreen` as `IsVisible && !IsSliding` | the task-switcher-mid-slide-in test |
| 6 | Revert `IsOnScreen` to plain `IsVisible` | the `IsOnScreen`-during-slide-out test |
| 7 | Stop clearing the flags in `CancelSlide` | the `ForceShow`-mid-slide flags assertion |
| 8 | Make `CancelSlide` conditional on a slide being in flight | `ForceShow` after a completed slide-out |
| 9 | Remove the generation guard | the `ForceShow`-mid-slide test |

Rows five and six are deliberately a matched pair: they mutate the same expression in opposite directions, so passing both is what proves the gate is *directional* rather than merely non-constant. One test cannot cover that. Rows two and three are the same arrangement for the placement predicate.

**One mutation deliberately absent.** An earlier draft listed "change `FillBehavior.Stop` to `HoldEnd`" and mapped it to "a second slide after the first". That mutation cannot fail, and the reason is worth keeping: with the base value still pre-written, a held clock's final value *equals* the base, and the next slide's `BeginAnimation` replaces the clock anyway. `HoldEnd` only bites in combination with something else — which is what rows seven and eight actually test. A mutation row that cannot turn a test red is worse than no row, because it reads as coverage.

Read-only review does not substitute for this; five rounds of it previously missed three one-line breakages with the suite green.

**No visual check** is performed without an explicit go-ahead, and any such run is kept short.

## Open questions

1. **~~[TUNING] Should the slide-in delay dwell arming by 200 ms?~~ — RESOLVED 2026-09-06. No: dwell arms at the start of the slide-in.** The draft deferred arming to the slide's completion, which put a summoned toolbar 600 ms behind the gesture (400 ms summon dwell + 200 ms slide) before any button could be used. Resolved in favour of immediate arming, accepting up to 200 ms during which a button is drawn away from its target's coordinate.

   Three things make that divergence cheap, and they are worth recording because they are the reason this is not merely a speed-for-correctness trade:

   - **Gaze is off-screen at the moment of the summon.** The gesture *is* sustained gaze past the docked edge (`ToolbarAutoHideController.IsPastDockedEdge`), so at t=0 gaze is outside every target. The user then has to saccade back and travel to the band — by which time much of the 200 ms has already elapsed.
   - **Dwell times dwarf the slide.** `DwellSettings.ToolbarButtonDwellMs` defaults to 700 ms with a validated floor of 200 (`DwellSettings.cs:17`, range enforced at `:77-81` and `:170`), against a 200 ms slide. A dwell that starts mid-slide completes well after the band is at rest, so the user watches their target arrive and *then* watches progress fill on it. Even at the 200 ms floor the two converge rather than the dwell completing on a moving button. Arming early makes the first moments of the interaction count rather than discarding them.
   - **The divergence is bounded and self-correcting**, whereas the failure it trades against is not. Nothing is written that has to be undone: the targets are already at their correct resting coordinates the whole time. Contrast dwell-target *placement*, which stays suppressed in both directions precisely because a wrong coordinate published once is permanent.

   The cost, stated plainly: for up to 200 ms after a summon, gaze resting on a button's final position accumulates against that button while the button is drawn short of it — so a fast user can start a dwell on something they are not yet looking at. Reverting is a one-line move of `SetActive(true)` back into the completion handler, plus the directional `IsOnScreen` becoming unnecessary.

2. **[DEFERRED] Should the pause indicator slide too?** It occupies the edge the toolbar retracts into, so the handover is currently a slide followed by a pop. Out of scope, and not obviously worth it for an 8-DIP bar (`PauseIndicatorWindow.BarThicknessLogical`).
