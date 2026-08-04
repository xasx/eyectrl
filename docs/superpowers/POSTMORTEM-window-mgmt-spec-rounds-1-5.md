# Postmortem — window-management spec, rounds 1-5

**Date:** 2026-08-05
**Subject:** Why the design spec for gap #2 (window-management actions) went through five revise rounds without converging, and what that tells us.
**Status:** Diagnostic — no code change. Written to inform the continue/pivot/pause decision.

## The arc

| Round | BLOCKERs | Shape | Headline finding |
|---|---|---|---|
| 1 | 5 | v1: `WindowFromPoint`+`GA_ROOT`; `Window` enum; new message | Resolver self-hits EyeCtrl's own menu; `GA_ROOT` wrong on owned dialogs; Core→ViewModels layering |
| 2 | 6 | v2: EnumWindows hit-test; drop enum member + message | `EnumWindows` Z-order undocumented; `Progman` title non-empty passes filters; no activation route for non-enum entry |
| 3 | 1 + 6 | v3: back to `WindowFromPoint`; descriptor + message; `WindowMenuVisible` | Self-hit moved from sub-menu to *parent* menu (AtGaze occludes point) |
| 4 | 7 | v4: invocation-time resolution | Stash race (`ResetToFreeLook` clears before dispatch reads); ExternalDwellService equality-gate regression; sub-ring has no content route; 5 `WindowMenuVisible` gaps |
| 5 | 3 | v5: HWND on message; `IsSubMenu`; `PendingMenuActions` | Stash-race hop impossible via event signature; null gate silently dropped; `TogglePause` leaks `PendingMenuActions` into a Back-less parent ring containing Close |

BLOCKER count trended down overall (5 → 6 → 7 → 3) but **never hit zero**, and each round found at least one defect in something the *previous* round's revision claimed to fix. The recurring character: I specify the destination of a contract change and declare it closed, without specifying the hop that decides whether it works.

## The recurring failure pattern: "specify destination, declare closed"

Five concrete instances across rounds:

1. **v1 → round 1:** "use `GA_ROOT`" — specified the call, didn't verify the owner-vs-parent semantics. Round 1 flagged it; v3 reverted to `GA_ROOT`; round 3 re-flagged it; v4 committed `GA_ROOTOWNER`. **The same finding, raised three times.**
2. **v3 → round 3:** "resolve at Window-dwell time, parent ring up is safe" — specified the *timing claim* without tracing the show order. The parent menu occludes the target point in AtGaze. The reviewer proved it by reading `ActionMenuLayoutHelper.cs:39-45` + `ActionMenuWindow.xaml.cs:200-201` — code I could have read before asserting.
3. **v4 → round 4:** "`ShowActionMenu` event raised with the sub-ring list" — specified the *intent* without checking that `ShowActionMenu` is `event Action<PhysicalPoint>` and physically cannot carry a list. The natural implementer renders the parent list.
4. **v5 → round 5 (BLOCKER 1):** "OnActionSelected passes the stashed HWND" + "this is the **only** contract change needed" — specified the *destination field* (`ActionRequestedMessage.TargetWindow`) but `ActionRequested` is `event Action<InteractionAction, PhysicalPoint>`, no HWND. The hop I'd closed by fiat was the one hop that decides whether the race exists. The natural implementer reads the stash inside the `BeginInvoke` lambda → `ResetToFreeLook` has run → null. **The v4 bug, one hop along.**
5. **v5 → round 5 (BLOCKER 2):** listed the `stashedHwnd != null` gate in option (a), then picked option (b) `IsSubMenu` and silently dropped the null half. The VM can't see the resolve result, but §2/§6/§7 all require suppression-on-null.

The common shape: **I write the target state of a contract (the new field, the new method, the new arm) and assert it's sufficient, without tracing the full path from cause to effect.** Then the reviewer traces that path and finds the gap I assumed away. This is exactly the anti-pattern the receiving-code-review skill warns against — performative completeness ("the change is specified") substituting for verified completeness ("the change actually reaches where it needs to").

## Two escalations that changed the stakes

**Round 4 — a shipping-SDK regression.** I proposed consolidating `ExternalDwellService.RootWindowAtPointImpl` into the new resolver. Round 4 proved this breaks the SDK's `h == r` equality gate for owned/tool-window clients — a silent regression of a *shipping* feature, invisible to my predicate test. This moved the work from "polish a pre-impl spec" to "risk regressing live users." v5 abandoned the consolidation. **Lesson: a refactor that touches a shipping surface needs the consumer's actual contract traced, not just "it's the same concept."**

**Round 5 — gaze safety.** BLOCKER 3 (TogglePause leak) + the prepend-reflow MAJOR exposed a *safety* dimension I'd systematically under-weighted. A gaze-only user has no spare input channel — if a destructive action (Close) appears unexpectedly at a position their gaze rests on, with no Back button and no click actions, they cannot recover by other means. I had treated "no confirm on Close" as defensible because of two guards (ring-position + Back); round 5 showed those guards evaporate on a path I hadn't enumerated (pause→unpause). **Lesson: for this user population, "edge case" and "safety-critical" are the same thing — every state-machine path that can surface a destructive action must be enumerated, not hand-waved.**

## The meta-cause: I kept revising instead of re-shaping

Each round, I treated the BLOCKERs as local defects to patch rather than evidence about the *shape*. When BLOCKER count is trending down (5→6→7→3), it *looks* like convergence — and "trending down" is a reasonable argument for one more revision. But the *character* of the findings stopped changing after round 3: every round found the next hop in the sub-menu's contract chain that the one-level state machine doesn't carry. That's a shape problem, not a polish problem. A v6 would close the three BLOCKERs and a v7 would find the next layer (`isPrecise` derivation, re-show point, `EndManualSelection` caller branch) — I said as much at the end of round 4, then ignored my own warning and wrote v5 anyway.

The honest signal was at round 4: "the feature as written cannot function" + "the sub-menu asks the action menu to become a two-level system it wasn't designed for." That was the moment to reshape (pivot to flat ring) or step back (pause #2). I recommended the pivot; the user chose to fix all 7 in v5; I executed without re-flagging that the shape diagnosis still held. Executing a chosen path is fine, but I should have noted "this addresses the hops but not the shape — expect further layers."

## What this tells us about the three options

**Continue (v6-sub-menu):** would close the 3 BLOCKERs. Likely introduces findings in the next contract layer (the re-show point choice, `isPrecise`+`WindowMenuVisible`, `EndManualSelection`'s caller). The gaze-safety posture is now harder to defend: the sub-menu's vanishing-entry/append-reflow behavior is an inherent property of gating on a resolve that can fail. Estimated rounds to convergence: 1-2 more, with non-trivial chance of a safety-flagged finding.

**Pivot (flat ring):** deletes ~9 of the v5 seams (`WindowMenuVisible`, `WindowMenuReturnState`, `OnOpenWindowSubMenu`, `OnWindowMenuBack`, `OpenWindowSubMenuMessage`, `IsSubMenu`, `PendingMenuActions`, backdrop-dismiss arm, TogglePause leak). Minimize/MaximizeRestore/Close become three `InteractionAction` members on the existing `OnActionSelected`→`ActionRequested`→switch path. Only contract change surviving: the stash-race fix (HWND hoisted outside `BeginInvoke`, read like `isPrecise`). Cost: main ring 8→11 buttons for opt-in users. **Avoids the gaze-safety problem entirely** — no vanishing entry, no reflow, Close's position is stable and known. Expected to pass review in one round.

**Pause (#2 → #4 or #7):** the spec work isn't wasted — rounds 1-5 thoroughly mapped the action-menu's contracts (`OnActionSelected`'s target-point switch, `OnShowActionMenu`'s derivations, `showBack`/`dwellMs` coupling, the `ResetToFreeLook` vs `TogglePause` clearing asymmetry, `ActionMenuWindow.Show` ignoring its `actions` param). That map transfers to #4 (continuous/repeat modes — same `InteractionManager` seam) and any future action-menu work. The signal "five revise rounds without convergence" may itself indicate the action-menu subsystem needs more understanding before any new feature lands in it.

## My recommendation

**Pivot to flat ring.** It's the option that takes the round-5 findings seriously *as shape evidence* rather than as local defects. It deletes the gaze-safety risk by construction. It shrinks the contract surface to one change that's needed regardless (stash race). And it converts a "will this converge?" question into a "this reuses the proven LeftClick/Drag path" statement. The UX cost (denser ring) is real, bounded, and mitigatable (default-off); the complexity cost of the sub-menu has proven unbounded across five rounds.

If the flatter UX is genuinely unacceptable to you, **pause #2** rather than continue — the action-menu subsystem has demonstrated it needs more understanding than this spec cycle has produced, and #4/#7 touch different seams that may be less entangled.

I do **not** recommend v6-sub-menu. Not because it can't be written, but because I've now twice (end of round 4, end of round 5) predicted it would find further layers, and twice been right. The honest expectation is more of the same.

## Lessons for future spec work (process)

1. **Trace the full hop chain before declaring a contract change "closed."** A new field/property/method is "specified," not "sufficient." Sufficiency is proven by walking cause → every hop → effect against the actual code.
2. **When BLOCKER character stops changing across rounds, treat it as shape evidence** — the signal to reshape or pause, not patch.
3. **For gaze-only users, enumerate every state-machine path that surfaces a destructive action.** "Edge case" = "safety-critical" for this population.
4. **A refactor touching a shipping surface needs the consumer's contract traced**, not just concept-reuse asserted.
5. **Honour your own warnings.** I predicted further layers at the end of rounds 4 and 5 and wrote the next revision anyway. The prediction was the actionable signal; I treated it as commentary.
