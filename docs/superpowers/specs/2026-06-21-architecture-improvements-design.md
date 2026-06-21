# Architecture Improvements — Design Spec

> Born from the architecture assessment on 2026-06-21. Covers six improvement
> opportunities identified during the review: CI, build warnings, GitNexus index
> staleness, `App.OnStartup` length, Core's dependency footprint, and Platform's
> broad `FrameworkReference`. Sequenced as Approach A (safety-first): the safety
> net and force-multipliers land before the expensive or risky work.

## Summary

Six workstreams grouped into four phases, ordered so each phase benefits from the
safety and cleanliness of the previous one:

| Phase | Work | Risk | Effort |
|---|---|---|---|
| 1. Foundation | GitNexus index refresh + GitHub Actions CI | None / low | Small |
| 2. Warning cleanup | ~80 nullable/test warnings, then 726 CS1591 XML docs in Core | Very low | Large |
| 3. Low-risk refactors | Narrow Platform's `FrameworkReference`; extract `App.OnStartup` helpers | Low–medium | Small–medium |
| 4. Core dependency audit | Keep MVVM in Core (document why); extract ONNX/Tokenizers into `EyeCtrl.LM` | Medium | Medium |

The long pole is Phase 2.2 (726 XML doc comments in Core). Everything else is
bounded and mechanical. Total estimate: ~21 commits across 2-3 focused days.

## Motivation

The assessment found a mature, well-structured codebase with strong test
coverage (104 test files), a clean three-layer architecture enforced by project
references, and disciplined defensive programming. The opportunities are
incremental improvements, not corrections of structural problems:

- **No CI** — regressions in Core could land untested. High ROI to fix.
- **810 build warnings** — 726 are CS1591 (missing XML docs in Core, because
  `<GenerateDocumentationFile>true</GenerateDocumentationFile>` is set
  intentionally), 58 are CS8602 nullable derefs (mostly in tests), plus a
  handful of CS8629/CS8604/xUnit1051.
- **GitNexus index 16 commits behind HEAD** — `impact()` and
  `detect_changes()` may miss recently-added symbols.
- **`App.OnStartup` is ~150 lines** — readable but long; two helpers would
  improve clarity and fix PascalCase local-variable violations.
- **Core pulls in ONNX/Tokenizers natives** — heavy native binaries (~100MB+)
  are dragged into every Core consumer, including SDK-only consumers who never
  use the neural LM.
- **Platform references all of `Microsoft.WindowsDesktop.App`** — broader than
  needed; may be narrowed to `UseWPF` + `UseWindowsForms`.

## Approach

**Approach A (safety-first)** was chosen over architecture-first and
docs-first alternatives. Rationale:

1. CI and a fresh GitNexus index are cheap and make every subsequent step
   safer. Force-multipliers first.
2. Warning cleanup before refactors means refactor PRs have noise-free diffs.
   Docs are written against the current stable API.
3. The Core audit goes last — the only true architectural change. By then CI
   verifies each extraction step and a clean doc baseline travels with any
   moved types.

The trade-off accepted: docs are written against the current API shape; if the
Phase 4 LM extraction moves types, those docs move with them (minimal rework
since `///` comments travel with the code).

---

## Phase 1: Foundation

### 1.1 GitNexus index refresh

One command, no design decisions:

```
node .gitnexus/run.cjs analyze
```

Refreshes the index from `ab00178a` (16 commits behind as of 2026-06-21) to
HEAD. Makes `impact()` and `detect_changes()` accurate for all subsequent
phases. No code changes, no commit. Verify with `gitnexus status` (or the MCP
`list_repos` tool) showing `commitsBehind: 0`.

### 1.2 CI — GitHub Actions workflow

**File:** `.github/workflows/ci.yml`

**Runner:** `windows-latest` (required — solution targets `net10.0-windows`
and uses WPF/WinForms).

**SDK:** `actions/setup-dotnet@v4`. `global.json` pins to `10.0.102` with
`rollForward: latestFeature`; the action respects it. No explicit version
override needed.

**Triggers:** `push` to `main`, `pull_request` targeting `main`.

**Jobs — two, to keep UI tests from blocking fast feedback:**

| Job | Filter | Purpose | Blocking? |
|---|---|---|---|
| `build-and-test` | `dotnet build` + `dotnet test --filter "Category!=UI"` | Fast feedback (~30s). 100+ unit tests, no display needed. | Yes |
| `ui-tests` | `dotnet test --filter "Category=UI"` | FlaUI tests against the WPF overlay windows. `windows-latest` runners have a desktop session. | No (`continue-on-error: true` initially — FlaUI in CI is notoriously flaky) |

**Tobii SDK:** Not present in CI → Platform builds without `TOBII_AVAILABLE`
→ `TobiiStreamEngineProvider` excluded. This is the intended graceful
degradation (documented in AGENTS.md). No special handling; the mouse-fallback
path is what CI exercises.

**Caching:** `actions/cache@v4` on `~/.nuget/packages` keyed on
`**/*.csproj` hash. Modest speedup.

**Out of scope (initially):**
- Release/publish job (separate concern; can add later)
- Linting/formatting (none configured per AGENTS.md)
- Code coverage reporting (can layer onto CI later)

**Verification:** After merging the workflow, push a trivial commit to confirm
both jobs run green (UI tests may be orange initially — expected with
`continue-on-error`).

**Commits:** 1 (the CI workflow file). The index refresh is a local
operation, not a commit.

---

## Phase 2: Warning Cleanup

Two workstreams: the ~80 nullable/test warnings (mechanical), then the 726
CS1591 XML doc warnings in Core (substantive).

### 2.1 Nullable & test warnings (~80 total)

| Code | Count | Where | Fix pattern |
|---|---:|---|---|
| CS8602 | 58 | Mostly `EyeCtrl.Core.Tests` (`SettingsPropertyValidationTests`, `WordPredictorTests`, `FileDictionaryProviderTests`) + a few in `EyeCtrl.App.Tests/Services/ManualSelectionDwellServiceTests` | Add `!` (null-forgiving) after a `Check.That(x).IsNotNull()` guard, OR restructure to check-then-deref. Prefer `!` when the assertion already proves non-null. |
| CS8629 | 16 | Mix of `EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs` (2) + tests | Cast to nullable value type explicitly: `value.Value` instead of implicit cast, or add `!` where the nullable is guarded. |
| CS8604 | 4 | `WordPredictorTests` | Same as CS8602 — guard or `!`. |
| xUnit1051 | 4 | `EyeCtrl.ViewModels.Tests/CalibrationViewModelTests` (lines 93, 104) | Pass `TestContext.Current.CancellationToken` to `Task.Delay` / async calls. |

**Conventions:**
- **Tests:** After `Check.That(x).IsNotNull()`, dereference with `x!.Property`
  (idiomatic per AGENTS.md migration table). Don't add redundant
  `if (x is null) return;` — the assertion already failed the test if null.
- **Production** (`ManualSelectionOverlayWindow`): proper null guard or
  `!.Value` cast — these reflect real state-machine invariants and should be
  documented with the `!`.

**Risk:** LOW. All changes are assertion-adjacent or type-annotation only. No
behavior change. Run `dotnet test` after — same pass count.

**Commit:** 1.

### 2.2 CS1591 XML doc comments (726 in Core)

**Scope:** Every `public` member (class, record, struct, interface, enum,
delegate, method, property, event, field) in `src/EyeCtrl.Core/**/*.cs` that
lacks a `///` doc comment.

**Distribution by file (top contributors):**
`ModelDownloadService.cs` (38), `ModelRegistryService.cs` (32),
`CalibrationService.cs` (30), `IInputSimulator.cs` (28),
`InteractionManager.cs` (28), `ModelRegistry.cs` (28),
`InteractionAction.cs` (22), `CalibrationData.cs` (20), `NoteData.cs` (18),
`KeyboardSettings.cs` (18), `ModelManifest.cs` (18)…

**Documentation approach — per-member, not bulk:**

Each doc comment describes *what* the member does, *not* restating the name.

| Member type | Doc content |
|---|---|
| Class/record | One-sentence purpose. Note invariants (e.g., "Coordinates are in physical screen pixels"). |
| Method | Purpose, parameter semantics, return value. Note side effects (events fired, state mutated). |
| Property | What it represents, units (px, µs, ms), valid ranges if constrained. |
| Enum | The concept; each member gets a `<summary>` if not self-evident. |
| Event | When it fires, what the args mean, thread affinity. |
| Interface | The contract; implementer responsibilities. |

**Sequencing — by subdirectory, easiest-to-hardest:**

1. `Models/` — data shapes, quick wins, establish style
2. `Enums/` + `MathConstants.cs` + `LayoutConstants.cs` + `BackdropType.cs` — trivial
3. `Interfaces/` — contracts, sets up impls
4. `Settings/` (13 files, ~80 properties) — most properties are self-evident from name + type; units need noting
5. `Processing/` — behavioral, needs care
6. `Calibration/` — math-heavy
7. `Prediction/` — largest, most varied; orchestrator → models → helpers → ML
8. `Keyboard/` — file-format adjacent
9. `Services/` — already partially documented
10. `GazeEngine.cs` — central, already has some docs (extend)

**Verification per batch:**
- `dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "CS1591"`
  — count should trend to 0 per file
- No tests affected (docs don't change behavior)
- Spot-check 5 random files per batch for doc quality (not just
  `<summary>name</summary>`)

**What this is NOT:**
- Not adding `<remarks>`, `<example>`, `<see>` cross-references unless a member
  genuinely needs them
- Not documenting `internal`/`private` members (CS1591 only fires on public)
- Not documenting test files (tests don't have
  `<GenerateDocumentationFile>`)
- Not changing any code, only adding `///` comments

**Commit strategy:** One commit per subdirectory (10 commits), each
independently verifiable. Easier to review than one 726-comment megacommit.

**Risk:** VERY LOW. Pure additive documentation. Only risk is doc inaccuracy —
mitigated by spot-checks.

---

## Phase 3: Low-risk Refactors

Two independent refactors, both verified by CI (in place from Phase 1).

### 3.1 Narrow Platform's `FrameworkReference`

**Current state** (`src/EyeCtrl.Platform/EyeCtrl.Platform.csproj:41`):
```xml
<FrameworkReference Include="Microsoft.WindowsDesktop.App" />
```

This pulls in **all** of WindowsDesktop.App — WPF, WinForms, and everything
between. Platform shouldn't need any of it: its job is Win32 P/Invoke
(`SendInput`, `WindowFromPoint`), Tobii SDK interop, and `ControlFinder`
(which uses `System.Windows.Forms` for `Screen.PrimaryScreen` and
`VisualTreeHelper` for hit-testing).

**Investigation needed first:** Confirm exactly which `System.Windows.*`
namespaces Platform references. From the architecture review: `ControlFinder`
uses `VisualTreeHelper` (WPF) and `Screen` (WinForms). If both are truly
required, narrowing the framework reference may not be feasible without
restructuring `ControlFinder`.

**Decision point — two options:**

| Option | Change | Trade-off |
|---|---|---|
| **A. Replace with specific `PackageReference`s** | Drop `<FrameworkReference>`. Add `<UseWPF>true</UseWPF>` + `<UseWindowsForms>true</UseWindowsForms>` (these pull only the needed subsets). | Smaller dependency surface. Same compilation result. Low risk. |
| **B. Leave as-is** | No change. | The framework reference is broad but harmless at runtime — WindowsDesktop.App is present on any machine running the app. Narrowing is cosmetic, not functional. |

**Recommendation:** Try Option A. If `UseWPF` + `UseWindowsForms` together
still pull in everything (they may — both are subsets of
WindowsDesktop.App), then revert to B and document why. Either way, this is a
~1-line change with a build check.

**Verification:** `dotnet build EyeCtrl.slnx` clean, `dotnet test --filter
"Category!=UI"` green.

**Risk:** LOW. Compile-time verified. No behavior change.

### 3.2 Extract `App.OnStartup` VM-resolution + overlay-init

**Current state** (`src/EyeCtrl.App/App.xaml.cs:50-199`, ~150 lines in
`OnStartup`):

The method does eight distinct things linearly: splash → DPI → settings →
logging → DI → resolve VMs → init overlays → calibration → event coord →
keyboard → tray → show overlays → start gaze → external dwell.

**What to extract — two helpers, no behavior change:**

| Helper | Extracted from | Signature |
|---|---|---|
| `ResolveViewModels(IServiceProvider)` | Lines 124-133 (10 `GetRequiredService<T>` calls + `Configure`) | Returns a small record/tuple of the 10 VMs. Eliminates the 10 local variables and PascalCase-naming violations (`ManualSelectionVM`, `ManualSelectionIndicatorVM`) noted in the assessment. |
| `InitializeOverlays(IOverlayManager, OverlayViewModels, EyeCtrlSettings, string)` | Lines 145-147 + 159-160 + 186-190 (init, get refs, show, set mode, bind) | Encapsulates the overlay-show sequence. Returns the `(cursorCircle, toolbar)` refs needed later. |

**What stays in `OnStartup`:** The orchestration order — splash, DPI, settings,
logging, DI build, helper calls, calibration load, event coordinator init,
keyboard load, tray, gaze start, external dwell. The method becomes ~60-70
lines of readable orchestration calling well-named helpers.

**Where the helpers live:** Private methods on `App` (simplest — they use
`Dispatcher`, `_serviceProvider`, instance state). Not extracting to a
separate class — that would add indirection without benefit for
bootstrapping-only code.

**Naming fix:** The PascalCase locals (`ManualSelectionVM`,
`ManualSelectionIndicatorVM`) become camelCase inside the helper
(`manualSelectionVm`, etc.) or fields on the returned record.

**Verification:**
- `dotnet build` clean
- `dotnet test --filter "Category!=UI"` green (no App-level unit tests cover
  OnStartup directly, but the app launches in `AppLaunchTests` UI tests)
- Manual smoke: `dotnet run --project src/EyeCtrl.App -- --test-mode`
  launches and quits cleanly
- CI runs the UI test job (non-blocking) as a second signal

**Risk:** LOW-MEDIUM. Pure refactor, no behavior change. The risk is in the
overlay-show *ordering* (e.g., `SetMode` must happen before
`BindToViewModel`) — preserved verbatim by extracting as a block, not
reordering. No `impact()` call needed (this is a single-call-site
private-method extraction, not a symbol rename or signature change).

**Commit strategy:** Two commits — `3.1` (FrameworkReference, 1-line) then
`3.2` (OnStartup extraction, ~80 lines moved). Each independently verifiable.

---

## Phase 4: Core Dependency Audit

The only true architectural change. Two independent decisions.

### 4.1 MVVM in Core — keep it

**Finding:** `CommunityToolkit.Mvvm` is used pervasively and *idiomatically*
across Core's Settings model:
- 13 settings classes extend `ObservableObject`
- ~80 `[ObservableProperty]` source-generated properties
- `EyeCtrlSettings` is the root aggregate (117 incoming calls — the #2 hub
  symbol)
- `GazeEngine.cs:51` subscribes to `CursorSettings.PropertyChanged` to rebuild
  the filter chain live
- The `WeakReferenceMessenger` itself is *not* used in Core (only
  `ObservableObject` + `[ObservableProperty]`)

**Why keep it:**

1. **It's the right pattern.** Settings objects *need*
   `INotifyPropertyChanged` — the engine, ViewModels, and overlays all
   subscribe to live changes. `ObservableObject` + `[ObservableProperty]` is
   the most concise, correct way to implement this. Hand-rolling
   `INotifyPropertyChanged` for 80 properties would be ~400 lines of boilerplate
   with no benefit.

2. **The "framework-agnostic" label is about UI frameworks**, not utility
   packages. Core has no reference to WPF, WinForms, Win32, or any UI type.
   `CommunityToolkit.Mvvm` is a source-generator-only package — it emits C#
   code, no runtime UI dependency. Consistent with the architectural invariant.

3. **Extraction would be costly and pointless.** Moving Settings to a separate
   `EyeCtrl.Settings` project would require every Core consumer (Platform,
   ViewModels, App) to reference the new project, and `GazeEngine` would need
   to either reference it too (re-introducing the dependency) or use a
   reflection-based `PropertyChanged` subscription (worse).

**Decision:** No change. Document the rationale in `ARCHITECTURE.md` so the
question doesn't recur — add a note under "Architectural Invariants"
clarifying that `CommunityToolkit.Mvvm`'s `ObservableObject` is used in Core
for `INotifyPropertyChanged` and is distinct from UI-framework dependencies.

**Risk:** None. No code change.

### 4.2 ONNX/Tokenizers extraction — extract to new `EyeCtrl.LM` project

**Current state:** Core.csproj references:
```xml
<PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.21.0" />
<PackageReference Include="Tokenizers.DotNet" Version="1.4.1" />
<PackageReference Include="Tokenizers.DotNet.runtime.win-x64" Version="1.4.1" />
```

These are **heavy native dependencies** (~100MB+ of native binaries) pulled
into every Core consumer — including the SDK layer's
`EyeCtrl.Sdk.Abstractions` consumers who never use the LM.

**What uses them:** Only 3 files in Core:
- `SmallLMPredictor.cs` — the ONNX neural LM predictor
- `LMTokenizer.cs` — the tokenizer wrapper
- `ModelManifest.cs` / `ModelConstants.cs` — manifest shapes (no ONNX dep, but
  LM-scoped)

**Who uses *those*:** `PredictionOrchestrator.cs` references
`SmallLMPredictor` conditionally (optional, skipped if no model present). No
other Core type touches them.

**Extraction design:**

```
src/EyeCtrl.LM/                  (new project, net10.0)
├── EyeCtrl.LM.csproj
├── SmallLMPredictor.cs          (moved from Core/Prediction/)
├── LMTokenizer.cs               (moved from Core/Prediction/)
├── ModelManifest.cs             (moved from Core/Prediction/)
└── ModelConstants.cs            (moved from Core/Prediction/)
```

**Dependency direction:**
- `EyeCtrl.LM` → `EyeCtrl.Core` (LM references Core for `PredictionResult`,
  interfaces, settings)
- `EyeCtrl.App` → `EyeCtrl.LM` (App wires `SmallLMPredictor` into
  `PredictionOrchestrator` via DI)
- `EyeCtrl.ViewModels` → (no change — doesn't touch LM directly)
- `EyeCtrl.Platform` → (no change)

**The seam — `PredictionOrchestrator` stays in Core:**

`PredictionOrchestrator` currently instantiates/calls `SmallLMPredictor`
directly. After extraction, it can't reference `EyeCtrl.LM` (that would create
a circular dep). Two options:

| Option | Change | Trade-off |
|---|---|---|
| **A. Interface in Core, impl in LM** | Core defines `ISmallLMPredictor` (method: `PredictAsync(context) → List<PredictionResult>`). `SmallLMPredictor` moves to `EyeCtrl.LM` and implements it. `PredictionOrchestrator` depends on `ISmallLMPredictor` (nullable, optional). `AppBootstrapper` registers `SmallLMPredictor` → `ISmallLMPredictor` when a model is present. | Clean DI seam. Core has no ONNX dep. Orchestrator stays testable with a fake predictor (already the test pattern in this repo). **Recommended.** |
| **B. Move `PredictionOrchestrator` to LM too** | Orchestrator + all predictors move to `EyeCtrl.LM`. | Worse — the orchestrator is non-ML logic (bigram, phrase, sentence completion) and belongs in Core. Bloats LM, forces ViewModels to reference LM. |

**Recommendation: Option A.** Core keeps the orchestrator and the non-ML
predictors; `EyeCtrl.LM` is a leaf project with only the ONNX/Tokenizer code.
This mirrors how the SDK layer (`Contracts`/`Abstractions`) already isolates
concerns.

**What moves with the files:**
- The 3 PackageReferences move from `Core.csproj` → `EyeCtrl.LM.csproj`
- **Tests:** Create a new `tests/EyeCtrl.LM.Tests/` project following the
repo's existing convention (one test project per source project:
`Core.Tests`, `Platform.Tests`, `ViewModels.Tests`, `App.Tests`,
`Sdk.Abstractions.Tests`, `Sdk.WinUI.Tests`). Move
`SmallLMPredictorTests.cs`, `LMTokenizerTests.cs`, and
`ModelManifestTests.cs` there. Tests that exercise the predictor through the
`ISmallLMPredictor` interface from the orchestrator stay in
`EyeCtrl.Core.Tests` (using a fake, per the no-mocking-library convention).
- Benchmarks: `SmallLMPredictor`/`LMTokenizer` benchmarks in
  `benchmarks/EyeCtrl.Benchmarks/` add a project reference to `EyeCtrl.LM`

**Solution file:** Add `EyeCtrl.LM` to `EyeCtrl.slnx` under `/src/`. Add test
project under `/tests/` if created.

**Verification:**
- `dotnet build EyeCtrl.slnx` clean
- `dotnet test` green (Core.Tests LM tests pass via the interface; LM.Tests
  pass directly)
- `dotnet run --project src/EyeCtrl.App -- --test-mode` — app launches,
  prediction still works (with and without a model present)
- **The key check:** `dotnet publish src/EyeCtrl.App -c Release -r win-x64
  --self-contained true -p:PublishSingleFile=true -o publish` — confirm the
  ONNX native binaries are now in a separate LM dependency, not bundled into
  Core's surface.
- Run `impact({target: "SmallLMPredictor", direction: "upstream"})` *before*
  the move to confirm the caller list matches expectation, and
  `detect_changes()` *after* to verify only the intended symbols/files were
  affected.

**Risk:** MEDIUM. It's a project split with an interface insertion. The risk
is in the `PredictionOrchestrator` → `ISmallLMPredictor` seam: the orchestrator
currently may call multiple `SmallLMPredictor` methods; the interface must
cover all of them. Mitigated by:
- CI verifies build + unit tests on every commit
- The existing `PredictionOrchestratorTests` already use fakes for predictors
  (per AGENTS.md "no mocking library" convention) — the interface makes this
  *easier*, not harder
- `AppBootstrapper` is the single registration point

**Commit strategy:**
1. Create `EyeCtrl.LM` project, move files, add interface — single commit,
   build must stay green
2. Update DI registration in `AppBootstrapper` — second commit
3. Move tests — third commit
4. Update `ARCHITECTURE.md` + `AGENTS.md` — fourth commit

**What this is NOT:**
- Not changing `PredictionOrchestrator`'s behavior
- Not changing the LM model download/registry services (those stay in Core —
  they manage files, not ONNX)
- Not touching the benchmarks beyond adding a project reference

---

## Documentation Updates (per phase)

Each phase updates docs as it lands. No "docs phase" at the end — that creates
stale docs. Doc updates commit *with* the code change they describe.

| Phase | Doc update | File |
|---|---|---|
| 1 (CI) | Add "CI" section: GitHub Actions runs build + non-UI tests on PR/push; UI tests run non-blocking. Note no linter/formatter configured. | `AGENTS.md`, `README.md` |
| 2 (Warnings) | No doc change — internal cleanup | — |
| 3.1 (FrameworkRef) | If narrowed: note `UseWPF`+`UseWindowsForms` instead of `FrameworkReference`. If not: document why it stays. | `AGENTS.md` "Dependencies & Quirks" |
| 3.2 (OnStartup) | No doc change — private refactor | — |
| 4.1 (MVVM) | Add invariant: "`CommunityToolkit.Mvvm`'s `ObservableObject` is used in Core for `INotifyPropertyChanged`; this is a source-generator utility, not a UI-framework dependency." | `ARCHITECTURE.md` §7 |
| 4.2 (LM extraction) | Add `EyeCtrl.LM` to project table, dependency diagram, directory structure. Note the `ISmallLMPredictor` seam and that Core is now free of ONNX/Tokenizer natives. | `ARCHITECTURE.md` §2, §6, §10; `README.md` project structure |

## Verification Strategy — layered

| Layer | When | What |
|---|---|---|
| **Per-commit** | After every commit | `dotnet build EyeCtrl.slnx` clean (0 errors). Warnings must not increase from the previous commit's count. |
| **Per-phase** | End of each phase | `dotnet test --filter "Category!=UI"` green. `node .gitnexus/run.cjs analyze` (Phase 1 only — then `detect_changes()` to confirm only intended scope was touched). |
| **Pre-merge** | Before merging any phase's PR | Full `dotnet test` (including UI tests, non-blocking). Manual smoke: `dotnet run --project src/EyeCtrl.App -- --test-mode` launches and quits. |
| **Phase 4 specific** | Before/after LM extraction | `impact({target: "SmallLMPredictor", direction: "upstream"})` before; `detect_changes()` after. Confirm no symbols outside `Prediction/SmallLMPredictor.cs`, `Prediction/LMTokenizer.cs`, `Prediction/ModelManifest.cs`, `Prediction/ModelConstants.cs`, `AppBootstrapper.cs`, and the new `EyeCtrl.LM` project are affected. |

## GitNexus usage throughout

Per AGENTS.md / CLAUDE.md invariants:

- **Before editing any symbol** in Phases 2-4: run
  `impact({target: "symbolName", direction: "upstream"})` and report blast
  radius. HIGH/CRITICAL risk → warn before proceeding.
- **Before every commit**: run `detect_changes()` to verify the diff only
  affects expected symbols/flows.
- **For the LM extraction (Phase 4)**: use `rename` tool if any symbol renames
  are needed (don't find-and-replace). Use `context({name:
  "SmallLMPredictor"})` to confirm the full caller/callee graph before the
  move.

## Out of scope

- Adding a linter/formatter/`.editorconfig` (separate decision; not requested)
- Adding code coverage reporting (can be layered onto CI later)
- Adding a release/publish workflow (separate concern)
- Refactoring `PredictionOrchestrator` internals
- Touching the SDK layer (`Contracts`, `Sdk.*`)
- Any change to `ExternalDwellService` or the IPC protocol
- Test framework changes (NFluent migration just completed)

## Effort & timeline estimate

| Phase | Effort | Commits | Notes |
|---|---|---|---|
| 1: Foundation | Small | 1 (CI workflow only; index refresh is not a commit) | ~1 hour |
| 2: Warnings | Large | ~14 (1 for nullable + ~10 for docs by subdirectory + 1-2 for test warnings) | The 726 XML docs dominate; ~1-2 days of focused work |
| 3: Refactors | Small-medium | 2 | ~2-3 hours |
| 4: LM extraction | Medium | 4 | ~4-6 hours including verification |
| **Total** | — | ~21 | — |

The XML doc work (Phase 2.2) is the long pole. Everything else is bounded and
mechanical.
