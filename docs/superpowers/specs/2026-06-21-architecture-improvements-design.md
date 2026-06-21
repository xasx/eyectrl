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
bounded and mechanical. Total estimate: ~22 commits across 2-3 focused days.

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
- **GitNexus index 17 commits behind HEAD** — `impact()` and
  `detect_changes()` may miss recently-added symbols.
- **`App.OnStartup` is ~150 lines** — readable but long; two helpers would
  improve clarity and fix PascalCase local-variable violations.
- **Core pulls in ONNX/Tokenizers natives** — heavy native binaries (~100MB+)
  are dragged into every Core consumer, including SDK-only consumers who never
  use the neural LM.
- **Platform references all of `Microsoft.WindowsDesktop.App`** — broader than
  needed; may be narrowed to `UseWPF` (Platform uses `System.Windows.Point`
  and UI Automation, both WPF subset — no WinForms).

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

Refreshes the index from `ab00178a` (17 commits behind as of 2026-06-21) to
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
`**/*.csproj` + `**/Directory.Build.props` + `global.json` hash. Modest
speedup.

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

**`[ObservableProperty]` generated properties:** Empirically verified that
CommunityToolkit.Mvvm v8.4.0's source generator emits `[GeneratedCode]` on
generated properties, which suppresses CS1591. Document the source field with
`///` and the generated property inherits the doc. No special handling needed
— CS1591 does not fire on generated properties.

**Impact-analysis exception:** AGENTS.md requires `impact()` before editing
any symbol. For documentation-only edits (adding `///` comments with no code
change), running `impact()` per member across 726 warnings is not practical.
This phase is exempt from the per-symbol `impact()` requirement. Run
`detect_changes()` per commit as usual to confirm no code changed.

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
(which uses `System.Windows.Automation` for UI Automation hit-testing and
`System.Windows.Point` for coordinates — both WPF types, no WinForms).

**Investigation needed first:** Confirm no other Platform file uses WinForms
types. From the architecture review: `ControlFinder` uses
`System.Windows.Automation.AutomationElement.FromPoint` and
`System.Windows.Point` (`src/EyeCtrl.Platform/Services/ControlFinder.cs:3,41`).
UI Automation ships with the WPF framework subset, so `UseWPF` alone should
suffice.

**Decision point — two options:**

| Option | Change | Trade-off |
|---|---|---|
| **A. Replace with `UseWPF` only** | Drop `<FrameworkReference>`. Add `<UseWPF>true</UseWPF>` (pulls in WPF subset including `System.Windows.Point` + UI Automation types). | Smaller dependency surface. Same compilation result. Low risk. |
| **B. Leave as-is** | No change. | The framework reference is broad but harmless at runtime — WindowsDesktop.App is present on any machine running the app. Narrowing is cosmetic, not functional. |

**Recommendation:** Try Option A. If `UseWPF` doesn't cover all Platform's
needs (grep for `System.Windows.Forms` / `System.Drawing` in Platform first),
then revert to B and document why. Either way, this is a ~1-line change with a
build check.

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
reordering. Run `impact({target: "OnStartup", direction: "upstream"})` before
editing per AGENTS.md (even though it's a single-call-site private-method
extraction, the invariant applies).

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

**Decision:** No change. Document the rationale so the question doesn't recur
— add a note clarifying that `CommunityToolkit.Mvvm`'s `ObservableObject` is
used in Core for `INotifyPropertyChanged` and is distinct from UI-framework
dependencies.

**Where to document:** `ARCHITECTURE.md`'s header claims "Auto-generated from
the GitNexus knowledge graph" but git history shows it is manually maintained
(commits like `docs: refresh ARCHITECTURE.md with fresh knowledge-graph data`).
No generator script exists in the repo. Manual edits are the norm and survive
across commits. Add the MVVM invariant note to `ARCHITECTURE.md` §7
"Architectural Invariants" — it will persist until the next manual refresh,
at which point it should be re-added. (If a generator is later introduced,
the invariant should be encoded in the generator's template, not just the
output.) Cross-reference from `AGENTS.md` "Dependencies & Quirks" so it's
discoverable from the dev-onboarding doc.

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

**What uses them:** Only 2 files in Core directly use the ONNX/Tokenizer
packages:
- `SmallLMPredictor.cs` — the ONNX neural LM predictor
- `LMTokenizer.cs` — the tokenizer wrapper

**What must stay in Core (circular-dependency guard):**
`ModelConstants.cs` and `ModelManifest.cs` are referenced by
`ModelDownloadService.cs` and `ModelRegistryService.cs` (both in Core) —
moving them would create `Core → EyeCtrl.LM`. Specifically:
- `ModelDownloadService.cs:45,57,93` references `ModelConstants.ModelFileNames`
- `ModelRegistryService.cs:88` takes `ModelManifest` as a constructor param
- `ModelRegistryService.cs:119` iterates `_bundledManifest.Models`
- `ModelRegistryService.cs:188` references `ModelConstants.ModelFileNames`

These model-catalog types manage file paths and download metadata, not ONNX
runtime — they belong in Core. Only the ONNX/Tokenizer runtime code moves.

**Where `SmallLMPredictor` is currently constructed:**
`EventCoordinator.LoadKeyboard` (`src/EyeCtrl.App/Services/EventCoordinator.cs:876-906`)
constructs `SmallLMPredictor` and `LMTokenizer` directly with `new`, passing
model paths from `ModelRegistryService`. `AppBootstrapper` only wires the
registry/download services — it does **not** register `SmallLMPredictor`. This
matters for the DI seam design below.

**Extraction design:**

```
src/EyeCtrl.LM/                  (new project, net10.0)
├── EyeCtrl.LM.csproj
├── SmallLMPredictor.cs          (moved from Core/Prediction/)
└── LMTokenizer.cs               (moved from Core/Prediction/)
```

`ModelManifest.cs`, `ModelConstants.cs`, `ModelRegistryEntry`,
`ModelSource`, `ModelDownloadService`, `ModelRegistryService` all **stay in
Core** — they're model-catalog/file-management types with no ONNX dependency.

**Dependency direction:**
- `EyeCtrl.LM` → `EyeCtrl.Core` (LM references Core for `PredictionResult`,
  `ModelConstants` for file-name lookup, settings)
- `EyeCtrl.App` → `EyeCtrl.LM` (App constructs `SmallLMPredictor` in
  `EventCoordinator.LoadKeyboard`)
- `EyeCtrl.ViewModels` → (no change — doesn't touch LM directly)
- `EyeCtrl.Platform` → (no change)

**The seam — `PredictionOrchestrator` stays in Core:**

`PredictionOrchestrator` currently takes `SmallLMPredictor?` as a constructor
parameter. After extraction, it can't reference `EyeCtrl.LM` (circular dep).
Two options:

| Option | Change | Trade-off |
|---|---|---|
| **A. Interface in Core, impl in LM, factory in App** | Core defines `ILanguageModelPredictor` (covers all methods `PredictionOrchestrator` currently calls on `SmallLMPredictor`). `SmallLMPredictor` moves to `EyeCtrl.LM` and implements it. `PredictionOrchestrator` depends on `ILanguageModelPredictor?` (nullable, optional). `EventCoordinator.LoadKeyboard` constructs `SmallLMPredictor` and passes it as `ILanguageModelPredictor` (construction stays where it is — only the static type changes). | Clean seam. Core has no ONNX dep. Minimal change to `EventCoordinator` (just the static type of the local). **Recommended.** |
| **B. Move `PredictionOrchestrator` to LM too** | Orchestrator + all predictors move to `EyeCtrl.LM`. | Worse — the orchestrator is non-ML logic (bigram, phrase, sentence completion) and belongs in Core. Bloats LM, forces ViewModels to reference LM. |

**Recommendation: Option A.** Core keeps the orchestrator and the non-ML
predictors; `EyeCtrl.LM` is a leaf project with only the ONNX/Tokenizer code.
The interface name `ILanguageModelPredictor` (not `ISmallLMPredictor`) is
intentional — the interface lives in Core and shouldn't be named after a
specific LM implementation.

**Note on existing tests:** `PredictionOrchestratorTests` currently passes
`lmPredictor: null` (`tests/EyeCtrl.Core.Tests/Prediction/PredictionOrchestratorTests.cs:21`)
— it does **not** use a fake LM predictor. The interface change is still
beneficial (tests *can* now inject a fake), but the spec doesn't claim fakes
already exist. The `InMemoryDictProvider` fake at line 7 is for
`IDictionaryProvider`, not the LM predictor.

**What moves with the files:**
- The 3 PackageReferences move from `Core.csproj` → `EyeCtrl.LM.csproj`
- **Tests:** Create a new `tests/EyeCtrl.LM.Tests/` project following the
  repo's existing convention (one test project per source project:
  `Core.Tests`, `Platform.Tests`, `ViewModels.Tests`, `App.Tests`,
  `Sdk.Abstractions.Tests`, `Sdk.WinUI.Tests`). Move
  `SmallLMPredictorTests.cs` and `LMTokenizerTests.cs` there.
  `ModelManifestTests.cs` stays in `EyeCtrl.Core.Tests` (the type stays in
  Core). Tests that exercise the predictor through the
  `ILanguageModelPredictor` interface from the orchestrator stay in
  `EyeCtrl.Core.Tests`.
- Benchmarks: `SmallLMPredictor`/`LMTokenizer` benchmarks in
  `benchmarks/EyeCtrl.Benchmarks/` add a project reference to `EyeCtrl.LM`

**Solution file:** Add `EyeCtrl.LM` to `EyeCtrl.slnx` under `/src/`. Add
`EyeCtrl.LM.Tests` under `/tests/`.

**Verification:**
- `dotnet build EyeCtrl.slnx` clean
- `dotnet test` green (Core.Tests pass with `ILanguageModelPredictor?` =
  null as before; LM.Tests pass directly against `SmallLMPredictor`)
- `dotnet run --project src/EyeCtrl.App -- --test-mode` — app launches,
  prediction still works (with and without a model present)
- **The key check:** Inspect `src/EyeCtrl.Core/EyeCtrl.Core.csproj` to confirm
  the 3 ONNX/Tokenizer `<PackageReference>` entries are gone. `dotnet publish
  EyeCtrl.App` will still include LM natives because `App → EyeCtrl.LM` — that
  is expected and correct. The goal is Core's dependency graph is clean, not
  App's publish output.
- Run `impact({target: "SmallLMPredictor", direction: "upstream"})` *before*
  the move to confirm the caller list matches expectation
  (`EventCoordinator.LoadKeyboard` and `PredictionOrchestrator` ctor). Run
  `detect_changes()` *after* to verify only the intended symbols/files were
  affected.

**Risk:** MEDIUM. It's a project split with an interface insertion. The risk
is in the `PredictionOrchestrator` → `ILanguageModelPredictor` seam: the
orchestrator currently may call multiple `SmallLMPredictor` methods; the
interface must cover all of them. Use `context({name: "SmallLMPredictor"})`
before the move to enumerate every public method `PredictionOrchestrator`
calls. Mitigated by:
- CI verifies build + unit tests on every commit
- `EventCoordinator.LoadKeyboard` is the single construction site —
  diffable in one commit
- `PredictionOrchestratorTests` passing `null` means the no-LM code path is
  already covered; adding a fake for the with-LM path is an optional
  enhancement, not a requirement

**Commit strategy:**
1. Add `ILanguageModelPredictor` interface to Core, change
   `PredictionOrchestrator` to depend on it — single commit, build must stay
   green (SmallLMPredictor still in Core, temporarily implements the
   interface)
2. Create `EyeCtrl.LM` project, move `SmallLMPredictor.cs` +
   `LMTokenizer.cs`, move the 3 PackageReferences — second commit, build
   green
3. Update `EventCoordinator.LoadKeyboard` to reference
   `ILanguageModelPredictor` (local variable static type only) — third commit
4. Move tests + update `EyeCtrl.slnx` — fourth commit
5. Update `ARCHITECTURE.md` + `AGENTS.md` — fifth commit

**What this is NOT:**
- Not changing `PredictionOrchestrator`'s behavior
- Not moving `ModelManifest`, `ModelConstants`, `ModelRegistryEntry`,
  `ModelSource`, `ModelDownloadService`, or `ModelRegistryService` (they stay
  in Core — they manage files and catalog metadata, not ONNX runtime)
- Not touching the benchmarks beyond adding a project reference

---

## Documentation Updates (per phase)

Each phase updates docs as it lands. No "docs phase" at the end — that creates
stale docs. Doc updates commit *with* the code change they describe.

| Phase | Doc update | File |
|---|---|---|
| 1 (CI) | Add "CI" section: GitHub Actions runs build + non-UI tests on PR/push; UI tests run non-blocking. Note no linter/formatter configured. | `AGENTS.md`, `README.md` |
| 2 (Warnings) | No doc change — internal cleanup | — |
| 3.1 (FrameworkRef) | If narrowed: note `UseWPF` instead of `FrameworkReference`. If not: document why it stays. | `AGENTS.md` "Dependencies & Quirks" |
| 3.2 (OnStartup) | No doc change — private refactor | — |
| 4.1 (MVVM) | Add invariant: "`CommunityToolkit.Mvvm`'s `ObservableObject` is used in Core for `INotifyPropertyChanged`; this is a source-generator utility, not a UI-framework dependency." Note: `ARCHITECTURE.md` is manually maintained despite the "auto-generated" header — manual edits persist. | `ARCHITECTURE.md` §7; cross-ref from `AGENTS.md` |
| 4.2 (LM extraction) | Add `EyeCtrl.LM` to project table, dependency diagram, directory structure. Note the `ILanguageModelPredictor` seam and that Core is now free of ONNX/Tokenizer natives. | `ARCHITECTURE.md` §2, §6, §10; `README.md` project structure |

## Verification Strategy — layered

| Layer | When | What |
|---|---|---|
| **Per-commit** | After every commit | `dotnet build EyeCtrl.slnx` clean (0 errors). Warnings must not increase from the previous commit's count. |
| **Per-phase** | End of each phase | `dotnet test --filter "Category!=UI"` green. `node .gitnexus/run.cjs analyze` (Phase 1 only — then `detect_changes()` to confirm only intended scope was touched). |
| **Pre-merge** | Before merging any phase's PR | Full `dotnet test` (including UI tests, non-blocking). Manual smoke: `dotnet run --project src/EyeCtrl.App -- --test-mode` launches and quits. |
| **Phase 4 specific** | Before/after LM extraction | `impact({target: "SmallLMPredictor", direction: "upstream"})` before; `detect_changes()` after. Confirm no symbols outside `Prediction/SmallLMPredictor.cs`, `Prediction/LMTokenizer.cs`, `EventCoordinator.cs`, `Prediction/PredictionOrchestrator.cs` (interface insertion), and the new `EyeCtrl.LM` project are affected. |

## GitNexus usage throughout

Per AGENTS.md / CLAUDE.md invariants:

- **Before editing any symbol** in Phases 3-4: run
  `impact({target: "symbolName", direction: "upstream"})` and report blast
  radius. HIGH/CRITICAL risk → warn before proceeding. (Phase 2.2 XML-doc
  edits are exempt — see "Impact-analysis exception" in §2.2.)
- **Before every commit**: run `detect_changes()` to verify the diff only
  affects expected symbols/flows.
- **For the LM extraction (Phase 4)**: use `rename` tool if any symbol renames
  are needed (don't find-and-replace). Use `context({name:
  "SmallLMPredictor"})` to confirm the full caller/callee graph before the
  move — this enumerates every public method that `PredictionOrchestrator`
  calls, which defines the `ILanguageModelPredictor` interface surface.

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
| 4: LM extraction | Medium | 5 | ~4-6 hours including verification |
| **Total** | — | ~22 | — |

The XML doc work (Phase 2.2) is the long pole. Everything else is bounded and
mechanical.
