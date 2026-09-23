# Visual findings fixes — design

Date: 2026-09-23 · Branch: `fix/visual-findings-2026-09` (`.worktrees/visual-findings`) ·
Status: revision 3 — external review round 2 (1 HIGH rendering-impossibility, 1 HIGH
self-contradiction, 1 MEDIUM ordering bug, 2 LOW) verified against source and folded in;
round 1's twelve confirmed fixed. Awaiting owner re-review.

Fixes the actionable findings of the 2026-09-22 unattended visual assessment
(`visual-assessment/report.md` in the main checkout: findings 1–3 plus two defaults).
Owner decisions were answered interactively on 2026-09-23 and are unchanged by this revision.

## 1. Models tab: surface model integrity failure (assessment finding 1, HIGH)

**Problem.** `PredictionStackFactory` (src/EyeCtrl.App/Services/PredictionStackFactory.cs)
SHA-256-verifies the active model and its tokenizer when the prediction stack builds; on
mismatch it logs an ERR line and leaves the LM disabled — nothing user-visible. The Models
tab (src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs) renders "✓ Downloaded" from
`ModelRegistryService.IsModelDownloaded`, a live *file-existence* check. A
corrupted-but-present model reads as healthy. Not hypothetical: the owner's
`qwen2.5-1.5b.onnx` currently fails this exact check.

**Design.**

- New enum `ModelVerificationOutcome { NotYetVerified, Verified, Failed }` next to
  `ModelRegistryService` (src/EyeCtrl.Core/Prediction/ModelRegistryService.cs; Core stays
  framework-free).
- `ModelRegistryService` gains a verification ledger:
  `void ReportVerification(string modelId, ModelVerificationOutcome outcome)` and
  `ModelVerificationOutcome GetVerification(string modelId)` (default `NotYetVerified`),
  concurrent-dictionary-backed — writers run on gaze/background threads, the reader on the
  UI thread during tab build.
- **Live refresh (review H2).** `ReportVerification` raises `VerificationChanged` whenever a
  verdict changes. `ModelsPageSource` subscribes (mirroring its existing
  `DownloadProgressChanged` pattern, including `Detach` from `SettingsWindow.Closed`) and
  rebuilds when the Models tab is current. Without this, a verdict landing after the tab was
  built (stack build runs on a background thread) would leave a corrupt model rendering
  "✓ Downloaded" until the tab is re-entered — the assessment's bug on a timer.
- **Verdict derivation (review C2) — from availability, not from enumerated causes.**
  `SmallLMPredictor`'s constructor swallows ONNX init failures into
  `IsAvailable = false` (SmallLMPredictor.cs:127–138), and an unparseable tokenizer is
  swallowed into `tokenizer == null` (PredictionStackFactory.cs:405–408) — so "constructed"
  does not mean live, and cause-enumeration cannot be exhaustive. The rules:
  - `Verified` ⇐ the stack build produced a predictor with `IsAvailable == true` **and** a
    loaded tokenizer — the exact conditions `PredictionOrchestrator` requires for the LM to
    serve predictions — for a model that is on disk and hash-gated (registry names a hash).
  - `Failed` ⇐ the settings-named model is on disk and hash-gated, and the outcome is not
    the above (hash mismatch, tokenizer mismatch/missing/unparseable, predictor absent or
    `!IsAvailable`). Which cause fired stays on the log line only.
  - Custom models are exempt — `DownloadCustomModelAsync` always passes empty hashes
    (ModelManagementService.cs:189–200), so a custom model can never be hash-gated; the
    warning row is effectively bundled-only.
- **Reload trigger (review C1 + N3 — the repair must re-enable the LM).** A repair re-download is
  present→present, so `KeyboardStackSnapshot`'s only filesystem term (`ActiveModelOnDisk`,
  file existence) is inert and `ShouldReload` (KeyboardCoordinator.cs:252–253, record
  equality) would never fire — the row would heal while the LM stayed disabled. Therefore
  `KeyboardStackSnapshot` gains one term: the active model's `ModelVerificationOutcome`
  (captured through the same required-`models` parameter as `ActiveModelOnDisk`).
  - **And the capture ordering bug is fixed, not priced (review N3).** Today
    `LoadKeyboard` captures `_keyboardSnapshot` at KeyboardCoordinator.cs:205, immediately
    after `Rebuild` returns (:196) — but the build (and its `ReportVerification` write) runs
    on a background thread, so the stored snapshot systematically reads the *pre-build*
    verdict: a stale read, not the accepted-quirk shape (that one was a relevant term firing
    for an irrelevant reason; this is the term lying about the live stack, against the
    collapse-both-captures invariant at :222–231). Fix: the stored snapshot is captured at
    **stack-swap completion** (the factory reports its verdict and the coordinator captures
    there, via a swap-completion callback), so `stored` always describes the stack actually
    running, verdict included. The spurious once-per-session reload (dwell reset +
    multi-hundred-MB ONNX load on the user's first apply) disappears; the repair path is
    intact — download reports `Verified`, and the next apply's capture differs from the
    stored swap-time `Failed` → reload → LM live.
  - Declined alternative, named: refreshing the stored snapshot on every
    `VerificationChanged` (review N3's first suggestion) — the **download** writer's report
    would be absorbed into `stored` before any apply compares it, silently un-fixing C1.
    Build-writer reports describe the live stack; download-writer reports describe a future
    one; only swap-time capture keeps that distinction without tagging event origins.
- **Download-path reporting (review M1 + N4).** `ModelManagementService` reports `Verified`
  on bundled download success **only when both hashes were non-empty and checked** — the
  model's `sha256` *and* the tokenizer's `tokenizerSha256` (ModelDownloadService.cs:168–175
  skips any empty-hash check; a `Verified` with an unchecked tokenizer would exceed the
  stack writer's meaning). Otherwise it reports nothing. Two writers, two meanings, named
  for the future reader: download-`Verified` = "the bytes matched the expected hashes";
  stack-`Verified` = "the LM is serving" (`IsAvailable` + tokenizer). They converge only for
  the settings-named model (a stack build re-reports after the reload); for non-active
  models the download verdict stands, which is harmless under the pixel-identical rendering
  below. The stale docstring at ModelDownloadService.cs:201–205 ("the shipped models.json
  currently leaves these empty") is corrected in the same change — the manifest hashes are
  live (the factory enforces them).
- **Rendering** (`ModelsPageSource.Build`, list view):
  - **Scope, stated honestly (review H4):** only the settings-named model is verified at
    stack build; non-active downloaded models carry only their download-time verdict, and a
    corrupt non-active model is **not** detected. Acceptable: only the active model's failure
    costs predictions. The tab lists all entries; non-active rows render as today.
  - A downloaded entry whose verdict is `Failed` renders as one warning `ButtonSpec` row
    (same 544-width family):

      > ⚠ {DisplayName} — integrity check FAILED · predictions fall back to dictionary · dwell to re-download

  - **Kind (review L1 + N5):** `ButtonKind` gains `Danger` (non-destructive danger — repair,
    not delete). `FillFor` (SettingsWindow.xaml.cs:1020–1025) gains an explicit `Danger` arm
    mapping to the same family as Reset/Delete (`AccentWarning` fill, surface-dark text) —
    the `_` arm would otherwise silently style the repair row as a plain button. Because
    Reset/Delete/Danger fills are then identical, `SettingsRenderMappingTests` gains a named
    `Danger` pin (its two existing tests name their kinds literally and would not fail on
    the new member — the same blind spot the file header records for Reset-vs-Delete).
    Label contrast holds at full dwell blend toward accent per the standing rule, and the
    fill stays on the contrast-gated token (`AccentWarning`, not `AccentDanger`, which
    `DwellButtonProgressBarContrastTests` deliberately excludes from its gate).
  - **Fit policy (review M5):** the label is ~95 chars for the real entry — it wraps (no
    clipping), with the resulting height measured and pinned by a fit assertion in the
    `SettingsContentFitTests` harness (the class of check
    `TheLongestRealToggleLabel_FitsTheContentColumn_OnANarrowDisplay` already models), and
    label contrast verified at full dwell blend toward accent. Whether the dictionary-fallback
    sentence stays in-label or moves to the row Description is settled by that fit test.
  - **Activation** opens the existing confirmation modal and runs the existing
    `DownloadBundledAsync` flow.
  - **In-flight precedence restructure (review L3):** the `CurrentDownloadId == entry.Id`
    check currently sits inside the not-downloaded `else` branch (ModelsPageSource.cs:126) —
    unreachable for a repair, because `File.Move(overwrite: true)` keeps the file present
    throughout. The in-flight check moves **above** the downloaded/failed split so a
    re-download shows the live progress line.
  - `NotYetVerified` and `Verified` rows are pixel-identical to today. Active Model combo
    unchanged.

**Tests.** Core.Tests: ledger default/round-trip/overwrite. App.Tests:
`ModelsPageSourceTests` — `Failed` → warning row (Danger kind) with activation wired to the
confirm+download flow; `Verified`/`NotYetVerified` → unchanged rows; in-flight overrides the
warning row (via the restructured check); a `VerificationChanged` raise rebuilds.
`KeyboardCoordinatorTests` extends its existing snapshot family
(`DownloadOfTheModelTheSettingNames_MovesTheSnapshot_WithoutASettingChanging`) with: the
swap-time capture carries the build's verdict; **no reload on the first apply after a
session start** (the N3 regression test); a download-time verdict flip still reloads on the
next apply (the C1 path). The `KeyboardStackSnapshot` arity change updates its
construction/capture sites and their tests. `SettingsRenderMappingTests` gains the named
`Danger` fill pin (§1 rendering).

## 2. Pause edge strip while the band is summoned (assessment finding 2, MEDIUM)

**Problem.** `PauseIndicatorWindow` shows only in `HideToolbar(paused: true)`'s slide-out
callback and is unconditionally hidden in `ShowToolbar` (:50) and `ForceShow` (:116) — paused
+ band summoned leaves the ▶ icon as the only cue, and `AppFixture.IsAppPaused` (keyed on the
indicator) reads false.

**Design (reshaped by review H1 + H3 + M4).**

- **New seam:** `IToolbarVisibility.SetPauseIndicator(bool visible)`, implemented on
  `ToolbarVisibilityAdapter` (dispatcher-marshalled, no-change edge guard). The show/hide
  indicator calls **leave** the three band methods (`ShowToolbar` :50, `HideToolbar`'s
  callback :78–85, `ForceShow` :116) — the indicator becomes a pure function of paused
  state, no longer coupled to band transitions.
- **Z-order (review H3 + N2), stated as the exchange it is:** leaving the show/hide calls
  must not remove the raise that keeps the strip above a band shown *after* it — so a
  **conditional `RaiseToTop()` enters** `ShowToolbar` and `ForceShow`, at the end of each
  dispatcher body (after `ShowThenSlideIn` / `ShowAtRest`), called iff the strip is
  currently commanded visible. `HideToolbar` gets none: the band is leaving, and a quick
  re-summon re-enters `ShowToolbar`, which raises. `SetPauseIndicator(true)` itself shows
  **and** raises (covers pause flipping while the band is already up). The primitive already
  exists: `OverlayWindow.RaiseToTop()` (OverlayWindow.cs:92, `SetWindowPos`
  `HWND_TOPMOST … SWP_NOACTIVATE`), documented for exactly this sibling-topmost case;
  `PauseIndicatorWindow : OverlayWindow` inherits it. Because the band's `Show()` activation
  happens synchronously inside `ShowThenSlideIn`, raising after the call holds through the
  ~200 ms positional slide; the live visual check captures after the slide settles, not just
  at the summon instant. **Interface ripple:** `RaiseToTop()` is added to
  `IPauseIndicatorOverlay` (IOverlayManager.cs:41) and to `FakePauseIndicator`
  (ToolbarVisibilityAdapterHeadlessTests.cs:95), which logs it like Show/Hide.
- **Driver:** `ToolbarLifecycleCoordinator` already mirrors every `StateChangedMessage` into
  `ToolbarAutoHideController.SetPaused` (UI thread); the same mirror calls
  `SetPauseIndicator(paused)` on a paused-state change, and pushes the initial state at
  startup (covering `startPaused` / `--paused`). No adapter constructor change and no
  gaze-thread read is needed (review M4 dissolved: there is no `isPaused` lookup to wire).
- **Why the seam must exist (review H1):** today unpause-while-band-up routes through
  `SetPaused(false)`, which only calls `HideToolbar` when the band is `Hidden`
  (ToolbarAutoHideController.cs:130–131) — with a visible band nothing would ever dismiss the
  strip. Explicit commands on every pause flip make a stuck strip structurally impossible,
  and `IsAppPaused` becomes correct in every state.
- `HideToolbar` keeps its `paused` parameter for the slide-out log; its callback's indicator
  logic goes. Unpause path unchanged: dwelling ▶ with the band up stays the dismissal route.

**Tests.** `ToolbarVisibilityAdapterHeadlessTests` with `FakePauseIndicator`: summon /
force-show / hide in both paused states; **pause flip while the band is visible** (strip
appears); **unpause while the band is visible** (strip disappears — the H1 row the original
matrix lacked); startup-paused pushes visible; the edge guard suppresses re-asserts
(`StateChangedMessage` fires on Fixation↔FreeLook flips several times a second); and the
N2 rows — summon/force-show while paused ends with `Indicator.RaiseToTop` after the band
call, summon while unpaused issues none.

## 3. Full-width separator in settings (assessment finding 3, LOW)

**Problem.** The `'─'`-character divider (`LabelSpec(() => new string('\u2500', 50))`) renders
at text width, left-aligned — the rule stops mid-column (Models ×2, Advanced ×4 sites).

**Design (review M2/M3 + N1).** New `SeparatorSpec : SettingsRowSpec` (record, no payload).
`CreateControl` maps it to a **dedicated `IDwellComposite`** — a one-child composite whose
panel holds the 1 px `BrushBorderSubtle` border (`HorizontalAlignment.Stretch`, margins
matching the neighbouring row rhythm) and whose dwell-children collection is empty, exactly
like the established visual-only precedent `LabelledSettingsRow : IDwellComposite`
(SettingsWindow.xaml.cs:1166, "a name is not one"). `IDwellComposite` is one of the three
accepted arms in **both** consumer switches — `RenderSpec`
(SettingsWindow.xaml.cs:1338–1350) and `SettingsAutoPagination.MeasureRows`
(SettingsAutoPagination.cs:92–105) — so neither grows a case, which was the round-2 goal.
(Round 2's revision named `DwellLabel` as the host; that shape does not exist — `DwellLabel`
is a `TextBlock` wrapper with a `(string, bool)` constructor and no child slot,
DwellLabel.cs:19–33.) Replace all six dash-label sites (ModelsPageSource 110, 153;
AdvancedPageSource 33, 48, 53, 58). Two docs go stale and are rewritten in the same change:
the `LabelSpec` wrap doc's "Models separator rule" sentence (SettingsRowSpec.cs:52–55) and
the `DwellLabel.wrap` doc that cites the same rule as its motivating example
(DwellLabel.cs:26–32).

**Tests.** `AdvancedPageSourceTests.Build_HasSeventeenRows_FourSeparators_ResetLast` asserts
`OfType<LabelSpec>()` has size 4 — it is updated to `OfType<SeparatorSpec>() == 4` (the
reviewer-named suite this change deliberately breaks).
`SettingsRowDescriptionCompletenessTests` is unaffected (its `BareRows` filter covers
Slider/Toggle/ComboBox/ColorPicker only); `SettingsTabDriftTests` enumerates ids/headers and
is unaffected. The three probes that pin the *current* dash rendering are named and
redirected, not left asserting deleted behavior (review N1): the source-regex dash-run scan
and the two `DwellLabel('\u2500', …)` clip probes in `SettingsContentFitTests`
(:462, :479, :502) become span assertions on the `SeparatorSpec` composite — the rule
stretches the content column.

## Decided with the owner (2026-09-23, unchanged by review)

1. Failure presentation: single warning row with re-download (not a separate warning line,
   not a modal on tab open).
2. Pause cue: edge strip stays visible while paused, above the band (not icon-only, not a
   tinted Play button).
3. Settings close button: **keep** `BrushAccentDanger` as-is (token stability; red is shared
   with genuinely destructive actions).
4. Dividers: real full-width separator (not leave-as-is).

## Vetoable defaults (no owner objection; veto to overturn)

5. Pause latency (state flip landed 3–5 s after the dwell in the assessment): investigate via
   one instrumented live pass against the existing Serilog trail; fix only a confirmed
   defect, as a separate change. No speculative edit in this branch. The strip change removes
   the band-summoned false-negatives but does not by itself fix a late state flip.
6. Gaze ring absent from stills (finding 5): no code change; opportunistic live glance during
   verification.
7. The owner's corrupt `qwen2.5-1.5b.onnx` is repaired by dwelling the new Re-download row
   after merge; no model file ships in this change.

## Non-goals

- Verifying non-active models beyond their download-time verdict (explicit limitation, §1).
- Settings tab-strip pagination advancing via UIA (tour coverage gap, not a user defect).
- Theme picker discoverability; quick-actions/modes/task-switcher visuals (unassessed).
- Any restyle of shared design tokens.

## Verification

- Per-project then full non-UI `dotnet test` green on the German-locale box.
- GitNexus impact pass before each edit (AGENTS.md rule) on: `ModelRegistryService`,
  `PredictionStackFactory`, `ModelManagementService`, `ModelsPageSource`,
  `KeyboardStackSnapshot`/`KeyboardCoordinator`, `ToolbarVisibilityAdapter`,
  `ToolbarLifecycleCoordinator`, `ToolbarAutoHideController`, `IToolbarVisibility`, the
  settings row renderer, `ButtonKind` consumers.
- One live launch (explicit owner OK required — input-channel rule) to eyeball: the failure
  row against the real corrupt model on this box (including that the re-download actually
  re-enables the LM — C1's end-to-end path), the paused+summoned strip above the band, the
  separators. Optional: re-run the gated visual tour (`VisualTourTests`) for before/after
  shots.

## Delivery notes

- Delivery shape unchanged: three commits on `fix/visual-findings-2026-09` — (1) integrity
  ledger + Models surface + snapshot term + swap-time capture, (2) pause strip seam +
  conditional raise, (3) separator — each with its tests green. No push.
- Review round 2 processing: all five findings (N1, N2, N3, N4, N5) verified against source
  and incorporated; none rejected. N1's fix is the reviewer's option 3 (`IDwellComposite`,
  not a `DwellLabel` host — that shape does not exist). N3's cause was named and the
  reviewer's first suggested fix **declined with reasoning** (it un-fixes C1; see §1) — the
  second (capture at stack-swap) is taken. N5's `Danger` fill lands on the contrast-gated
  `AccentWarning` family with a named render-mapping pin. Round 1's twelve remain confirmed
  fixed per the round-2 review's own verification table.
- Fresh-worktree LFS verified 2026-09-23 (completions are real binaries).
