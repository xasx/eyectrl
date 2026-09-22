# Visual findings fixes — design

Date: 2026-09-23 · Branch: `fix/visual-findings-2026-09` (`.worktrees/visual-findings`) · Status: approved design, pending owner spec review

Fixes the actionable findings of the 2026-09-22 unattended visual assessment
(`visual-assessment/report.md` in the main checkout: findings 1–3 plus two defaults).
Every owner-facing decision was answered interactively on 2026-09-23; the remaining
judgment calls are listed as vetoable defaults.

## 1. Models tab: surface model integrity failure (assessment finding 1, HIGH)

**Problem.** `PredictionStackFactory` (src/EyeCtrl.App/Services/PredictionStackFactory.cs)
SHA-256-verifies the active model and its tokenizer when the prediction stack builds; on
mismatch it logs an ERR line and leaves the LM disabled — nothing user-visible. The Models
tab (src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs) renders
"✓ Downloaded" from `ModelRegistryService.IsModelDownloaded`, which is a live *file-existence*
check. A corrupted-but-present model therefore reads as healthy. This is not hypothetical:
the owner's `qwen2.5-1.5b.onnx` currently fails this exact check.

**Design.**

- New enum `ModelVerificationOutcome { NotYetVerified, Verified, Failed }` next to
  `ModelRegistryService` (src/EyeCtrl.Core/Prediction/ModelRegistryService.cs; Core stays
  framework-free).
- `ModelRegistryService` gains a verification ledger:
  `void ReportVerification(string modelId, ModelVerificationOutcome outcome)` and
  `ModelVerificationOutcome GetVerification(string modelId)` (default `NotYetVerified`).
  Backed by a concurrent dictionary — writers run on the gaze/background threads (stack
  build, download completion), the reader runs on the UI thread during tab build.
- **Reporting rules** — the ledger holds each model's *latest* verification event, so every
  downloaded model always carries a verdict:
  - `PredictionStackFactory`, per stack build, for the model the settings name (only when
    the model file exists on disk and the registry names a hash for it — custom models
    without a manifest hash are exempt):
    - model hash mismatch, tokenizer hash mismatch, or tokenizer file missing → `Failed`
      (any of the three leaves predictions dictionary-only; the log line keeps which one);
    - predictor constructed **and** tokenizer loaded → `Verified`;
    - model file absent → report nothing (the tab already shows the `[Download]` button).
  - `ModelManagementService` (src/EyeCtrl.App/Services/ModelManagementService.cs) on bundled
    download success → `Verified` (the downloader already verifies SHA before accepting
    bytes). Download failure → no change (the previous verdict stands).
- **Rendering** (`ModelsPageSource.Build`, list view): a downloaded entry whose verdict is
  `Failed` renders as one danger-styled `ButtonSpec` row (same 544-width family as the
  download buttons; danger styling consistent with the existing delete-button family —
  exact `ButtonKind` choice at plan time; label contrast must hold at full dwell blend
  toward accent, per the standing testing rule):

  > ⚠ {DisplayName} — integrity check FAILED · predictions fall back to dictionary · dwell to re-download

  Activation opens the existing confirmation modal and runs the existing
  `DownloadBundledAsync` flow. The in-flight branch (`CurrentDownloadId == entry.Id`,
  live progress line) takes precedence over the warning row while that model downloads.
  A successful re-download reports `Verified` and the rebuild restores "✓ Downloaded".
- `NotYetVerified` and `Verified` rows are pixel-identical to today. The Active Model combo
  is unchanged (the setting stays valid; the row carries the warning).

**Tests.** Core.Tests: ledger default, round-trip, overwrite semantics (`Failed` →
`Verified`). App.Tests (`ModelsPageSourceTests`, headless): `Failed` → warning row present
with activation wired to the confirm+download flow; `Verified`/`NotYetVerified` → unchanged
rows; in-flight overrides the warning row.

## 2. Pause edge strip while the band is summoned (assessment finding 2, MEDIUM)

**Problem.** `ToolbarVisibilityAdapter`
(src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs) shows `PauseIndicatorWindow` only in
the `HideToolbar(paused: true)` slide-out completion callback and unconditionally hides it in
`ShowToolbar` (line 50) and `ForceShow` (line 116). Paused + band summoned → the only cue is
the ▶ icon, and the UIA pause signal reads false (`AppFixture.IsAppPaused` keys on the
indicator) — the exact false-negative that aborted the assessment tour's pause phase.

**Design.**

- `ToolbarVisibilityAdapter` constructor gains `Func<bool> isPaused`, wired in the
  bootstrapper from the interaction-state source (exact wiring at plan time).
- `ShowToolbar` and `ForceShow`: replace the unconditional `PauseIndicator.Hide()` with
  `if (isPaused) Show() else Hide()`. `HideToolbar` is unchanged.
- Window requirements (PauseIndicatorWindow): stays click-through and non-activating; renders
  as a thin strip at the docked edge; when both are visible the strip must appear **above**
  the band. Two topmost WPF windows order by activation — the plan must include a live visual
  check of this, not just unit tests.
- Unpause path unchanged: dwelling the (▶) Pause button with the band up already works and
  stays the dismissal route.

**Tests.** `ToolbarVisibilityAdapterHeadlessTests` (a `FakePauseIndicator` exists there):
matrix {paused, unpaused} × {ShowToolbar, ForceShow, HideToolbar} → indicator show/hide per
matrix in both paused states; re-asserts don't spam the fake.

## 3. Full-width separator in settings (assessment finding 3, LOW)

**Problem.** The `'─'`-character divider (`LabelSpec(() => new string('\u2500', 50))`) renders
at its text width, left-aligned — the rule visibly stops mid-column (Models ×2, Advanced ×4
sites).

**Design.** New `SeparatorSpec : SettingsRowSpec` (record, no payload); renderer case in the
settings row switch (src/EyeCtrl.App/Views/SettingsWindow.xaml.cs, the `case LabelSpec` at
~line 1124): a 1 px `BrushBorderSubtle` `Border`, `HorizontalAlignment.Stretch`, margins
matching the neighbouring row rhythm. Replace all six dash-label sites: ModelsPageSource.cs
lines 110 and 153, AdvancedPageSource.cs lines 33, 48, 53, 58.

**Tests.** The spec-enumerating suites (`SettingsTabDriftTests`,
`SettingsRowDescriptionCompletenessTests`, `SettingsContentFitTests`) are updated if the new
spec type trips them; the fit-test harness asserts the rule spans the content width where
cheap to do so.

## Decided with the owner (2026-09-23)

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
   defect, as a separate change. No speculative edit in this branch.
6. Gaze ring absent from stills (finding 5): no code change; opportunistic live glance during
   verification.
7. The owner's corrupt `qwen2.5-1.5b.onnx` is repaired by dwelling the new Re-download row
   after merge; no model file ships in this change.

## Non-goals

- Settings tab-strip pagination advancing via UIA (tour coverage gap, not a user defect).
- Theme picker discoverability; quick-actions/modes/task-switcher visuals (unassessed).
- Any restyle of shared design tokens.

## Verification

- Per-project then full non-UI `dotnet test` green on the German-locale box.
- GitNexus impact pass before each edit (AGENTS.md rule) on: `ModelRegistryService`,
  `PredictionStackFactory`, `ModelManagementService`, `ModelsPageSource`,
  `ToolbarVisibilityAdapter`, the settings row renderer.
- One live launch (explicit owner OK required — input-channel rule) to eyeball: the failure
  row against the real corrupt model on this box, the paused+summoned strip, the separators.
  Optional: re-run the gated visual tour (`VisualTourTests`) for before/after shots. Note the
  tour's original pause-phase timeout was latency-shaped (default 5), so the strip change
  removes the band-summoned false-negatives (its later phases) but does not by itself fix a
  late state flip.

## Delivery notes

- Three commits on `fix/visual-findings-2026-09`: (1) integrity ledger + Models surface,
  (2) pause strip, (3) separator — each with its tests green. No push.
- Fresh-worktree LFS verified 2026-09-23 (completions are real binaries).
