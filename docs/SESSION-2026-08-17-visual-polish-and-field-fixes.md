# Session record: settings visual polish + field fixes (2026-08-17/18)

Branch `polish/settings-mixed-pages` (worktree `.worktrees/settings-mixed-pages`), rebased onto
main @ `0e60ef33`. Every fix below is committed, covered by tests, and verified by a live
paused-launch round whose log was checked clean (zero ERR/FTL) afterwards. Suite state at the
time of writing: 12 projects, 3296 non-UI tests, 0 failures.

The session began as visual polish of the settings window's mixed pages and became a field-fix
session: everything below was found either by headless measurement against the user's real
settings/geometry or by the user testing live builds.

## The fixes

### 1. Mixed pages had two left axes and no row rhythm (`e353372c`)
**Symptom:** mixed-type pages "could need a little more tweaking" (user).
**Cause:** standalone `ButtonSpec` rows kept default `Stretch` + explicit `Width`, which WPF
*centers* (measured x=130 in the 560-DIP column) while toggles/labels/sliders hug x=0; and
`DwellButton`'s shared `(4,0,4,0)` margin left stacked buttons flush — tangent hit circles.
**Fix:** `CreateControl`'s ButtonSpec arm pins `HorizontalAlignment.Left` + margin `(4,0,4,24)`
(one axis; the 24-DIP bottom rhythm every other row kind has). Side effect: Calibration's
Calibrate/Clear pair, split across pages by the greedy chunker, reunites.
**Tests:** `StandaloneButtonRow_SitsOnTheColumnAxis_AndEndsTheRowRhythm`.

### 2. Models buttons below the WYSIWYG dwell floor (`5b8a7927`)
**Cause:** all five Models `ButtonSpec` sites declared `Height: 60` while registering the
100-DIP hit circle — circles stuck out 20 DIP past the visual and overlapped on the flush stack.
Exactly what PR #94's floor principle forbids; the rows predate it.
**Fix:** 60 → 100 on md_dl_*/md_add/md_manage/md_back/md_del_*.
**Tests:** `EveryModelsButton_MeetsTheDwellFloor`.

### 3. Action Menu tab had no settings, only a reset (`de643a89`)
**Symptom:** "action menu tab has no settings controls but the reset button" (user).
**Cause:** the tab's one real setting — `VisibleActions`, which actions appear in the ring —
was never exposed anywhere.
**Fix:** ten toggles (one per `InteractionAction`; ScrollLeft/Right ship off and are enabled
here) + reset. Toggles apply live (the ring reads the list at menu-open). Structural change:
startup `EnsureDefaults` → `EnsureDefaultsOnce` (persisted `DefaultsMigrated` flag) — the
per-startup re-add would have silently undone user removals across restarts.
**Tests:** `ActionMenu_OneTogglePerAction_ThenReset`, `EnsureDefaultsOnce_*` (3).

### 4. Labels clipped instead of wrapping (`1e9cdd7f`)
**Symptom:** "Models names are not fully readable" (user).
**Cause (measured):** worst label wanted 561 DIP against 360 of text room; two reset buttons
also shaved their tails.
**Fix:** `DwellButton` labels wrap to a second centered line on overflow; `LabelSpec` status
text wraps by default via a `Wrap` flag — **wrap is opt-in on `DwellLabel`** because the
separator rule's pinned rendering (SettingsContentFitTests) *is* the clip. Widths tuned so every
known label fits one line: Models download/delete 400→544, Add-Custom 300→440, the two clipping
resets 300→380.
**Tests:** `DwellButtonTextWrappingTests`, `LabelRow_ForwardsTheWrapFlag`.

### 5. Sidebar clipped tabs 10–11 (General, Quick Actions) (`1c768176`)
**Symptom:** "I can't find the last two tabs" (user; measured: 948 DIP of tab room vs 1312
needed for close + 11 tabs).
**User decision:** paginate the tab list (rejected compact two-line tabs).
**Fix:** pure `SettingsTabPagination` sizes pages to the real sidebar height; ▲/▼ pager row
(content-pager metrics, 14-px margins so 120-DIP hit circles clear); hidden tabs are DETACHED
**and unregistered** (a hidden tab with a live hit circle is a gaze trap); VM routes
`tab_prev`/`tab_next` to their registered actions *before* `ShowTab`. 2 pages (7+4) at the
user's geometry.
**Tests:** `SettingsTabPaginationTests` (4) + wiring pins, `TabPagerIds_RunRegisteredAction_…`.

### 6. Model downloads ran invisibly (`2cefcb5e`)
**Symptom:** "models do not download… may be the ux is just not clear. There is no progress."
**Cause:** plumbing was sound (pinned by a new e2e test with a fake HttpClient: dwell → modal →
confirm → both files on disk) — but `ProgressChanged` had ZERO consumers, the button kept saying
"[Download]", the busy guard silently swallowed dwells, and model downloads don't log their
start, so a killed mid-flight download left no trace at all.
**Fix:** `ModelManagementService` exposes `CurrentDownloadId`/`CurrentDownloadProgress` + a
~2 Hz throttled `DownloadProgressChanged`; every terminal path funnels through `Complete()`
which clears state. The Models page renders the in-flight model as a live
"⇣ name  downloading… 42 %" line; the busy guard now says why. Source detaches on window close
(window is per-open, service is a singleton).
**Tests:** `DwellDownload_ThenConfirm_DownloadsBothFiles_EndToEnd`,
`WhileDownloading_InFlightModelShowsProgressLine_AndBusyIsLoud`.

### 7. Task switcher tiles undwellable — positions on the wrong DPI basis (`fd4453b0`)
**Symptom:** "tasks in the task switcher are not dwellable" (user).
**Cause:** the switcher was the ONE overlay not pushing dwell-target positions through
`UIElement.PointToScreen`; it scaled window-space centers with `DpiHelper.LogicalToPhysicalX`,
which does not know Windows' DPI virtualization (see Environment findings). Targets landed far
from the tiles.
**Fix:** `fe.PointToScreen(center)` like every working overlay; pinned by source-scan
`TaskSwitcherDwellPositionTests`.
**Follow-up, completed in `824712cc` after an instrumented field run:** the diagnostics proved
the dwell chain works end-to-end (two selections fired, overlay closed) — re-framing the
report. Two further defects found and fixed: (1) the tiles had *never* had a dwell-progress
visual (`DwellProgress` set but never bound) — a ProgressBar now fills at each tile's bottom;
(2) `BringToFront` could never work: `AllowSetForegroundWindow` delegates a right the caller
must already hold, and this non-activating overlay process never holds foreground, so
`SetForegroundWindow` was silently rejected — selections closed the overlay and no window came
forward. Now the ALT-press simulation earns the right (minimize/restore fallback). Both
source-scan pinned. The run also resolved the coordinate space: gaze and `PointToScreen` both
live in the tracker-scaled 3840×2400 space (provider multiplies normalized gaze by
PrimaryScreen × DpiHelper.ScaleX = 2.0 on this display; PointToScreen applies the same 2×) —
consistent by construction; the >1920 / negative samples were the user looking beyond screen
edges, not a space mismatch. Diagnostics stripped after the diagnosis.

### 8. Calibration advanced on a timer, not the user (`c972ceed`)
**Symptom:** "calibration is not usable. the points should move at the user's pace" (user).
**Cause:** each target advanced on a blind `Task.Delay(TargetHoldMs)` and averaged every sample
after a settle window that started at target *creation* — no notion of arrival (the
`SettleWindowMs` doc even claimed "after gaze first enters the target area").
**Fix:** `CalibrationPointCollector` gates on ARRIVAL — within an acceptance radius (scaled to
the display, ≥120 px) **or** a tight recent cluster anywhere (= a steadily fixating user whose
raw gaze is systematically offset, the calibration chicken-and-egg). Settle runs from arrival
(the arriving sample is the saccade landing and is discarded); `IsComplete` at
`SamplesPerPoint` post-settle samples. The VM advances on `IsComplete && elapsed ≥ TargetHoldMs`
(TargetHoldMs is now a visible floor, not a pacer); a 10-s per-target safety cap degrades to
the old behavior if the user never arrives; an expected-index guard collapses the gaze-vs-cap
race. Clock injectable; collector tests sleep-free.
**Tests:** collector suite rewritten around arrival semantics (8),
`Target_WaitsForTheUser_ThenAdvancesAtTheirPace`.

### 9. Toolbar too small/cramped — mis-dwells made a button undwellable (`f2093790`)
**Symptom:** "I couldn't dwell the task switcher button on the toolbar. Also, I mis dwell
often. Enlarge the buttons and spacing" (user). Confirmed fixed by the user the next round.
**Cause:** 80-DIP buttons with 2-px margins and inscribed hit circles — adjacent circles
near-tangent; the switcher button (flanked by Settings) could never win the aim.
**Fix:** default size 80→120, `ButtonMargin` 2→12 (24-px circle clearance = the settings-window
dwell-row rhythm). The user's settings.json had `size: 80` persisted (would override any new
default forever), so startup runs a once-only `MigrateSizeOnce`: exactly-the-old-default bumps
once; deliberate values are untouched; sliding back afterwards sticks.

## Environment findings (load-bearing for future work)

- **The app runs DPI-virtualized in the user's session.** Evidence: the test host sees the
  screen as 1920×1200 DIP (not the documented 960×600 @200%); the sidebar fits 9 tabs
  (consistent with a ~1080-DIP window, not 540); gaze coordinates in the switcher diagnostics
  exceed 1920 in x and go negative in y. Consequences: (a) `UIElement.PointToScreen`-based
  overlays work; manual `DpiHelper` scaling does not (fix #7); (b) the pagination budget my
  page dumps modeled at 364 DIP may not match what the user currently sees (their window may
  be ~1728×1080 → budget ~904); (c) gaze coordinates suggest a **multi-monitor desktop** —
  unconfirmed.
- The known transient `Tokens.Current` race never fired this session. German test output.
- Task-switcher P/Invoke crash seen at 00:22 on 2026-08-17 was the OLD pre-fix binary from a
  previous worktree (fixed on main by `78d7a843`).

## Open items

1. **Switcher verification round pending:** the progress bar + foreground fix (`824712cc`) need
   one confirming live round (dwell a tile → bar fills → window actually comes forward).
2. `MixedPageDiagnosticTests.cs` (the page-dump diagnostic) stays uncommitted by design.
3. Toolbar size 120 persists to settings.json only on the next arbitrary settings save
   (migration applies in memory every launch until then) — harmless, self-resolving.
4. Long-standing deferred follow-ups unchanged: QA list page clipping, vacuous pagination
   routing tests, `ChunkByHeight` NaN-hardening, `SettingsRenderMappingTests` collection
   isolation.

## Verification rounds (all paused-launch, 2 min, force-kill, log check — all clean)

| Round | Time | Verified |
|---|---|---|
| 1 | 02:53 | Mixed-page axis/rhythm + floor (user feedback → fixes 3, 4) |
| 2 | 21:40 | Wrap/widths + Action Menu toggles; real 1.2 GB model download succeeded |
| 3 | 22:10 | Tab pagination ("pagination works. All tabs reachable."); bugs 6–8 reported |
| 4 | 23:05 | Fixes 6–8 live; toolbar bug reported |
| 5 | 23:24 | Toolbar enlargement ("Toolbar button works"); tiles still not dwellable |
| diag | 00:09 | Instrumented switcher run: chain works end-to-end, 2 selections logged → fixes in `824712cc` |
