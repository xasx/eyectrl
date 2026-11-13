# SplashThreadingSpike results (2026-11-13 22:02)

| Measurement | Value | Expected | Meaning |
|---|---|---|---|
| Blocked-thread angle delta over 2 s blockage | 0,0° | ≈ 0 | storyboards on a blocked STA thread freeze (rev-1 §8 claim was wrong) |
| Independent-thread angle delta over the same 2 s | 78,7° | > 60 | a second STA thread + dispatcher keeps animating through the blockage |
| Frozen brush created on thread B | True | true | Tokens-style frozen brushes cross threads |

**Verdict: PASS — the separate-thread architecture is proven**

Sampling note: values are written by each thread's own `CompositionTarget.Rendering` handler into
volatile fields — a blocked thread stops firing the handler, which is exactly the evidence wanted.
Thread A blocks INSIDE a posted dispatcher work item, so its clock is provably running first.
Spike mechanics (implementation notes)
- `Dispatcher.DoEvents()` from the plan sketch is not a real API. Thread B simply runs
  `Dispatcher.Run()`; after sampling, the main thread posts `BeginInvokeShutdown` to both
  dispatchers (practical-note option 2). Thresholds are the plan's, unchanged:
  blocked delta < 1.0°, independent delta > 60°, brush frozen.
- Thread A pumps freely first (blockedReady = its first render tick), then the main thread posts
  the blockage work item onto A's dispatcher and waits for A to confirm it is inside that item
  (blockedNow). a0 = -76,3° is the last animated value A's running clock applied — a real
  animated reading, not the 0° rest value — so the frozen clock is provably a RUNNING clock.
- a0/b0 are sampled the moment A is frozen, a1/b1 exactly 2 s later, A is then released, and both
  dispatchers are shut down cleanly.