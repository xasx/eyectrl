# Bug Fix Plan — EyeCtrl Codebase Audit

## Impact Summary

GitNexus impact analysis (upstream, summary-only) for the symbols touched by HIGH bugs.

> **What the section tiers mean:** The `HIGH` / `MEDIUM` / `LOW` headers below reflect **change-risk / blast radius** from impact analysis (how much depends on the touched symbol) — **not** user-facing severity. A `HIGH` bug means "test thoroughly because lots depends on this symbol," not necessarily "severe symptom."

- **`GazeEngine`** (Bugs #1, #2): **HIGH** blast radius. 8 impacted symbols, 7 direct dependents across `Services`, `EyeCtrl.Core.Tests`, `Processing`, and `StreamEngineGazeTracker`. Affects 2 execution flows (`OnStartup` in `App.xaml.cs`, `OnLoaded` in the StreamEngine sample) — breakage hits at step 1 of both. **Note:** the fixes touch only *private internals* (the `_gapInterpolator` field, the constructor's `PropertyChanged` lambda, and the read in `OnGazePoint`) — the public surface is unchanged, so the actual regression risk of these specific edits is much lower than the class-level `HIGH` suggests. The `HIGH` is a signal to run a full compile + test pass, since everything downstream depends on this class.
- **`Win32InputSimulator`** (Bug #3): **LOW** blast radius. No direct upstream dependents in the graph (consumed via `IInputSimulator` abstraction at call sites). Now filed under **Optional / Robustness** below — see the corrected reasoning; this may not be a real bug.
- **`CalibrationService`** (Bug #4): adding the logger as an **optional** parameter keeps this local (no call-site changes). A *required* parameter would ripple to 10 call sites (1 DI registration + 9 tests) — see the fix note.
- MEDIUM/LOW bugs (#5–#11) are local, single-file fixes with narrow blast radius and no cross-module process impact.
- **Bug #12 is struck** (see below) — the original recommendation was incorrect and would have introduced a regression.

## HIGH

### Bug #1: GapInterpolator state lost on settings change
**File:** `src/EyeCtrl.Core/GazeEngine.cs:45-52`
**Problem:** Replacing `_gapInterpolator` on settings change creates a fresh instance with `_lastValidPosition = (0, 0)` and `_lastValidTimestamp = -1`. If the **first** gaze sample after the swap is *invalid* (no eye detected), `Process` returns `_lastValidPosition` → the cursor jumps to the top-left corner. In the common case (a valid sample arrives first, since the user is looking at the settings UI), the visible symptom is a smoothing discontinuity rather than a corner jump.
**Fix:** Add a `CopyStateFrom(GapInterpolator other)` method to `GapInterpolator`. Before replacing, copy the old instance's `_lastValidPosition` and `_lastValidTimestamp` to the new one.
**Note:** This restores gap-tracking continuity, but the new instance still gets a cold filter from `GazeFilterFactory.Create`, so the OneEuroFilter's internal state is lost and a brief smoothing blip remains. That is inherent when `FilterMode` changes; for a `MaxGapInterpolationMs`-only change it is avoidable (update the max-gap value in place without rebuilding the interpolator) if the residual blip proves noticeable.

### Bug #2: Race condition on `_gapInterpolator` field
**File:** `src/EyeCtrl.Core/GazeEngine.cs:19,45-51,90`
**Problem:** `_gapInterpolator` is written on the UI thread (PropertyChanged handler) and read on the gaze provider thread (`OnGazePoint`) with no synchronization. Reference assignment is atomic, so this is a *visibility/ordering* issue, not tearing; on win-x64's strong memory model it is largely benign, but `volatile` is the correct defensive fix and guards against JIT read hoisting.
**Fix:** Mark the field `private volatile GapInterpolator _gapInterpolator;`. That alone is sufficient — plain reads and writes of a `volatile` field already carry the needed acquire/release semantics. **Do NOT also wrap the read in `Volatile.Read(ref _gapInterpolator)`:** passing a `volatile` field by `ref` triggers compiler warning **CS0420** ("a reference to a volatile field will not be treated as volatile") and, under `-warnaserror`, breaks the build. Use the `volatile` keyword *or* `Volatile.Read`/`Volatile.Write` on a non-volatile field — never both.

---

## MEDIUM

### Bug #4: CalibrationService.Load empty catch block
**File:** `src/EyeCtrl.Core/Calibration/CalibrationService.cs:132-134`
**Problem:** Empty `catch { }` silently swallows all exceptions including `OutOfMemoryException`.
**Fix:** Add an **optional, nullable** constructor parameter `ILogger<CalibrationService>? logger = null` (matching the pattern in `SettingsService` and `FileDictionaryProvider`). Log at `LogWarning` level. Only swallow expected exceptions (`JsonException`, `IOException`); let fatal exceptions propagate.
**Call sites:** `CalibrationService` is constructed with no arguments at `AppBootstrapper.cs:119` (DI registration) and in 9 places in `CalibrationServiceTests.cs`. Keeping the parameter optional means none of these need to change; a *required* parameter would break all 10. The DI registration can optionally forward a resolved logger via `sp.GetService<ILogger<CalibrationService>>()`.

### Bug #5: FileDictionaryProvider user dictionary missing try-catch
**File:** `src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs:84-91`
**Problem:** User dictionary loading has no exception handling, unlike base dictionary loading which wraps in `try-catch`. A corrupted user dictionary crashes the app.
**Fix:** Wrap `File.ReadAllText` + `JsonSerializer.Deserialize` in `try-catch` for `JsonException`, `IOException`, and `UnauthorizedAccessException`. Log and return empty on failure, matching the pattern used for bigram/phrase timestamp loading.

### Bug #6: GazeEngineAdapter async void handler missing try-catch
**File:** `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:54-72`
**Problem:** The `async void` lambda for `ActivationCompletedMessage` has no top-level try-catch. If `_interactionManager.OnActivationButtonDwelled()` throws, the exception is unobserved.
**Fix:** Wrap the entire async lambda body in `try-catch(Exception ex)` and log via `_logger.LogError(ex, "Failed to handle activation completed")`.

---

## LOW

### Bug #7: ExternalDwellService.Send swallows pipe write errors
**File:** `src/EyeCtrl.App/Services/ExternalDwellService.cs:301`
**Problem:** Empty `catch { }` in `PipeConnection.Send` silently swallows write errors. Broken connections appear active but never deliver messages.
**Fix:** Log the exception at `LogDebug` level in the catch block. **Note:** `PipeConnection` is a nested class with no logger field, so this is not a pure one-liner — pass a logger (or an `Action<Exception>` callback) into the `PipeConnection` constructor from the outer `ExternalDwellService`, which has `_logger`. Leave the empty `catch` in `PipeConnection.Dispose` as-is (intentional during teardown).

### Bug #8: SettingsService.SaveDebounced not thread-safe
**File:** `src/EyeCtrl.Core/Settings/SettingsService.cs:27-61`
**Problem:** `_debounceCts` is replaced and disposed without locking. If called from multiple threads, the CTS could be disposed while being read.
**Fix:** Add `private readonly object _debounceLock = new();` and wrap the CTS swap + `Task.Run` in a `lock`.

### Bug #9: CalibrationService.BeginTarget silently replaces collector
**File:** `src/EyeCtrl.Core/Calibration/CalibrationService.cs:43-48`
**Problem:** `BeginTarget` doesn't check if `_currentCollector` is already non-null. Calling it twice without `EndTarget` silently discards accumulated samples.
**Fix:** Check `_currentCollector != null` and throw `InvalidOperationException("A target is already being collected. Call EndTarget() first.")`.

### Bug #10: SmallLMPredictor SessionOptions leaked on GPU fallback
**File:** `src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs:76-89`
**Problem:** When `AppendExecutionProvider_DML()` fails, the old `SessionOptions` is not disposed before creating a new one.
**Fix:** Add `options.Dispose();` before `options = new SessionOptions();` in the catch block.

### Bug #11: EventCoordinator.Dispose missing _glassBackdrop cleanup
**File:** `src/EyeCtrl.App/Services/EventCoordinator.cs:917-953`
**Problem:** `Dispose` doesn't hide or clean up `_glassBackdrop`. If the backdrop is visible when the coordinator is disposed, it remains on screen.
**Fix:** Add `_glassBackdrop?.Hide();` and `_glassBackdrop = null;` in the `Dispose` method. (`_glassBackdrop` is owned by `OverlayManager`, so hide it and drop the reference — do **not** `Dispose` it here. The recent "Fix GlassBackdropWindow hide crash" commit makes `Hide()` safe to call on this teardown path.)

---

## OPTIONAL / ROBUSTNESS

### Bug #3: DoubleClick may rely on timing edge cases
**File:** `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs:40-44`
**Risk:** LOW blast radius (consumed via the `IInputSimulator` abstraction). **This may not be a real bug** — see below.
**Problem:** Two `LeftClick` calls fire back-to-back with no delay. Windows recognizes a double-click when two clicks occur *within* the double-click time threshold (`SPI_GETDOUBLECLICKTIME`, default ~500ms), so back-to-back clicks are already within threshold and this likely works in practice. However, two separate `SendInput` calls are not atomic — a very slow dispatch could in theory push the second click outside the threshold. There is no confirmed repro.
**Fix (robustness, optional):** Replace the two `LeftClick` calls with a single `SendInput` call containing four mouse events (down+up+down+up) with appropriate `Time` offsets. This guarantees atomic delivery and correct double-click detection regardless of dispatch timing. Do NOT add an arbitrary `Thread.Sleep` — a sleep does not fix the non-atomic dispatch and only reduces responsiveness.

---

## RELATED OBSERVATIONS (not part of this audit's fix set)

Surfaced while verifying the bugs above. Optional follow-ups, not blockers.

### Obs A: `SuppressFixation` has the same cross-thread race as Bug #2
**File:** `src/EyeCtrl.Core/GazeEngine.cs:24,106` (read on the gaze thread); written from the UI thread at `EventCoordinator.cs:179,185,310,318,325` and `ExternalDwellService.cs:119,127`.
**Problem:** `public bool SuppressFixation` is written on the UI thread and read on the gaze thread (`OnGazePoint`, line 106) with no synchronization — the identical pattern to Bug #2, a sibling field two lines away. Practical impact on win-x64 is low (a stale read costs at most one frame), but if Bug #2 is being fixed for visibility, this should ride along.
**Fix (optional):** Back the auto-property with a `volatile bool` field.

### Obs B: `Win32InputSimulator` never checks the `SendInput` return value
**File:** `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs` (all `NativeMethods.SendInput` call sites).
**Problem:** `SendInput` returns the number of events successfully injected and can return 0 when blocked (UIPI, the secure desktop / UAC prompt, a locked workstation). For an app whose core function *is* input injection, a silent failure produces no diagnostics — clicks/keys simply don't register. Not in scope for this audit, but a real robustness gap.
**Fix (optional, follow-up):** Check `SendInput`'s return value and log at `LogDebug`/`LogWarning` when it is less than the expected event count.

---

### ~~Bug #12: BigramModel.ApplyDecay timestamp reset~~ (STRUCK — not a bug)
**File:** `src/EyeCtrl.Core/Prediction/BigramModel.cs:188-190`
**Original claim:** After decay, the timestamp is updated to `now`, preventing future decay.
**Why this is wrong:** The decay formula is `frequency * 0.5^(age / halfLifeDays)` and `followers[word2]` is overwritten with the decayed frequency. The timestamp reset to `now` is intentional (see the code comment at line 188: "to prevent compounding decay on subsequent runs"). If the timestamp were *not* reset, the next decay pass would recompute `age` from the original creation time against the already-reduced frequency — producing compounding exponential decay that rapidly destroys legitimate high-frequency bigrams. The current code is correct.
**Action:** No change. Do not remove `word2Timestamps[word2] = now;` in `BigramModel.cs` (line 190) or the equivalent `phraseTimestamps[fullPhrase] = now;` in `PhraseModel.cs:235-236`.
