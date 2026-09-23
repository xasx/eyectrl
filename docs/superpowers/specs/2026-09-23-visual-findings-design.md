# Visual findings fixes — design

Date: 2026-09-23 · Branch: `fix/visual-findings-2026-09` (`.worktrees/visual-findings`) ·
Status: revision 2 — external review round 1 (2 CRITICAL, 4 HIGH, 5 MEDIUM, 3 LOW) verified
against source and folded in; awaiting owner re-review.

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
- **Reload trigger (review C1 — the repair must re-enable the LM).** A repair re-download is
  present→present, so `KeyboardStackSnapshot`'s only filesystem term (`ActiveModelOnDisk`,
  file existence) is inert and `ShouldReload` (KeyboardCoordinator.cs:252–253, record
  equality) would never fire — the row would heal while the LM stayed disabled. Therefore
  `KeyboardStackSnapshot` gains one term: the active model's `ModelVerificationOutcome`
  (captured through the same required-`models` parameter as `ActiveModelOnDisk`). A repair
  flips the stored snapshot (`Failed` → captured `Verified`) → reload → stack rebuild → LM
  live. This stays inside the snapshot's stated principle ("the only acceptable trigger is
  something the user did" — a repair download is one). Accepted and recorded: on the first
  settings apply after a session start, the ledger flip (`NotYetVerified` →
  `Verified`/`Failed`) triggers one extra reload that rebuilds an identical stack — same
  shape as the file's existing accepted quirk. (Alternative rejected: forcing an explicit
  reload after repair bypasses the guard whose job is "reload only when a stack input moved".)
- **Download-path reporting (review M1).** `ModelManagementService` reports `Verified` on
  bundled download success **only when `sha256` was non-empty** (i.e. the bytes were actually
  hash-checked — ModelDownloadService.cs:168–175 skips the check on empty); with an empty
  hash it reports nothing. The stale docstring at ModelDownloadService.cs:201–205 ("the
  shipped models.json currently leaves these empty") is corrected in the same change — the
  manifest hashes are live (the factory enforces them).
- **Rendering** (`ModelsPageSource.Build`, list view):
  - **Scope, stated honestly (review H4):** only the settings-named model is verified at
    stack build; non-active downloaded models carry only their download-time verdict, and a
    corrupt non-active model is **not** detected. Acceptable: only the active model's failure
    costs predictions. The tab lists all entries; non-active rows render as today.
  - A downloaded entry whose verdict is `Failed` renders as one warning `ButtonSpec` row
    (same 544-width family):

      > ⚠ {DisplayName} — integrity check FAILED · predictions fall back to dictionary · dwell to re-download

  - **Kind (review L1):** `ButtonKind` gains `Danger` (non-destructive danger — repair, not
    delete); the renderer maps `Danger` and `Delete` to the same visual family. No
    `Delete`-kinded button is used for a re-download.
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
(`DownloadOfTheModelTheSettingNames_MovesTheSnapshot_WithoutASettingChanging`) with: verdict
flip moves the snapshot → reload fires. The `KeyboardStackSnapshot` arity change updates its
construction/capture sites and their tests.

## 2. Pause edge strip while the band is summoned (assessment finding 2, MEDIUM)

**Problem.** `PauseIndicatorWindow` shows only in `HideToolbar(paused: true)`'s slide-out
callback and is unconditionally hidden in `ShowToolbar` (:50) and `ForceShow` (:116) — paused
+ band summoned leaves the ▶ icon as the only cue, and `AppFixture.IsAppPaused` (keyed on the
indicator) reads false.

**Design (reshaped by review H1 + H3 + M4).**

- **New seam:** `IToolbarVisibility.SetPauseIndicator(bool visible)`, implemented on
  `ToolbarVisibilityAdapter` (dispatcher-marshalled, no-change edge guard). All three
  existing indicator call sites in the adapter are **removed** — the indicator becomes a pure
  function of paused state, no longer coupled to band show/hide transitions.
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
- **Z-order (review H3):** the old per-site call orders ("indicator first, so it is not
  underneath the incoming band" vs `ForceShow`'s opposite) are exactly why "show it in place"
  could not work. With the seam, `SetPauseIndicator(true)` shows the strip and re-asserts
  topmost after any band transition in the same dispatcher body; the strip stays
  click-through and non-activating, rendered as a thin line at the docked edge **above** the
  band. The live visual check remains — as verification of this mechanism, not as the
  mechanism.
- `HideToolbar` keeps its `paused` parameter for the slide-out log; its callback's indicator
  logic goes. Unpause path unchanged: dwelling ▶ with the band up stays the dismissal route.

**Tests.** `ToolbarVisibilityAdapterHeadlessTests` with `FakePauseIndicator`: summon /
force-show / hide in both paused states; **pause flip while the band is visible** (strip
appears); **unpause while the band is visible** (strip disappears — the H1 row the original
matrix lacked); startup-paused pushes visible; the edge guard suppresses re-asserts
(`StateChangedMessage` fires on Fixation↔FreeLook flips several times a second).

## 3. Full-width separator in settings (assessment finding 3, LOW)

**Problem.** The `'─'`-character divider (`LabelSpec(() => new string('\u2500', 50))`) renders
at text width, left-aligned — the rule stops mid-column (Models ×2, Advanced ×4 sites).

**Design (review M2/M3).** New `SeparatorSpec : SettingsRowSpec` (record, no payload).
`CreateControl` maps it to a **`DwellLabel` hosting a 1 px `BrushBorderSubtle` border**,
`HorizontalAlignment.Stretch`, margins matching the neighbouring row rhythm — an
already-accepted shape, so neither consumer switch needs a case: `RenderSpec`
(SettingsWindow.xaml.cs:1338–1350) and `SettingsAutoPagination.MeasureRows`
(SettingsAutoPagination.cs:92–105) both switch on
`DwellControl`/`IDwellComposite`/`DwellLabel` and **throw** on anything else; hosting the
border in a `DwellLabel` cannot trip them. Replace all six dash-label sites (ModelsPageSource
110, 153; AdvancedPageSource 33, 48, 53, 58). The `LabelSpec` doc sentence about "the Models
separator rule" opting out of wrap (SettingsRowSpec.cs:52–55) is rewritten — it documents the
dash being replaced.

**Tests.** `AdvancedPageSourceTests.Build_HasSeventeenRows_FourSeparators_ResetLast` asserts
`OfType<LabelSpec>()` has size 4 — it is updated to `OfType<SeparatorSpec>() == 4` (the
reviewer-named suite this change deliberately breaks).
`SettingsRowDescriptionCompletenessTests` is unaffected (its `BareRows` filter covers
Slider/Toggle/ComboBox/ColorPicker only); `SettingsTabDriftTests` enumerates ids/headers and
is unaffected. A width-span assertion is added where the fit harness makes it cheap.

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

- Three commits on `fix/visual-findings-2026-09`: (1) integrity ledger + Models surface +
  reload term, (2) pause strip seam, (3) separator — each with its tests green. No push.
- Review round 1 processing: all 13 findings (2C/4H/5M/3L as amended above) verified against
  worktree source and incorporated; none rejected. M4 was dissolved by the §2 seam design
  rather than answered.
- Fresh-worktree LFS verified 2026-09-23 (completions are real binaries).
