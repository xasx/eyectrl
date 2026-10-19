# AGENTS.md

## Build & Run

Requires **git-lfs**: the sentence-completion tries in
`src/EyeCtrl.App/Resources/Completions/` are LFS objects. A clone without it gets
pointer files, and `EyeCtrl.App.Tests` fails on them. `git lfs install && git lfs checkout`.

```bash
dotnet build EyeCtrl.slnx                # build everything
dotnet run --project src/EyeCtrl.App     # run (falls back to mouse-as-gaze if no eye tracker)
dotnet run --project src/EyeCtrl.App -- --test-mode   # skip splash, force mouse gaze provider
dotnet run --project src/EyeCtrl.App -- --ring-arm=today|run-only   # ring diagnosis; the default is hybrid
dotnet publish src/EyeCtrl.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Test

```bash
dotnet test tests/EyeCtrl.Core.Tests                              # unit tests (fast, no display)
dotnet test tests/EyeCtrl.Platform.Tests                          # platform unit tests
dotnet test tests/EyeCtrl.ViewModels.Tests                        # viewmodel unit tests
dotnet test                                                       # all tests
dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"        # UI tests (needs display)
```

## CI

GitHub Actions runs on `push` to `main` and `pull_request` targeting `main`:
- `build-and-test` — validates the theme palettes (`tools/palette_validate.py`), builds the
  solution, runs `dotnet test --filter "Category!=UI"` with coverage collection, runs the
  benchmark generator tests, and gates on coverage thresholds (≥50% line, ≥45% branch)

UI tests (`Category=UI`, FlaUI) are **not** run in CI — they need an interactive desktop
session that the headless `windows-latest` runner doesn't provide. Locally, note the split:

- The **app-launching** FlaUI tests (keyboard/toolbar/settings/startup — everything using
  `AppFixture`) are **opt-in** via `EYECTRL_UI_TESTS=1`, because they launch the real app and
  drive the OS cursor, seizing control from a gaze-only user:
  `EYECTRL_UI_TESTS=1 dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"`.
  Without the variable those tests SKIP (cleanly) — a plain `--filter "Category=UI"` runs only
  the non-launching UI tests and silently skips the rest.
- The remaining `Category=UI` tests (STA/XAML/window-construction tests) run without the gate.

No linter or formatter is configured.

## Assertions

Use **NFluent 3.1.0** (fluent assertion library) in tests. xUnit remains the test runner (`[Fact]`, discovery, execution). NFluent is configured repo-wide in `tests/Directory.Build.props` with a global `<Using Include="NFluent" />`.

```csharp
// Equality
Check.That(actual).IsEqualTo(expected);

// Collections
Check.That(collection).HasSize(n);
Check.That(collection).IsEmpty();
Check.That(collection).Not.IsEmpty();
Check.That(collection).HasElementThatMatches(predicate);
Check.That(collection).ContainsOnlyElementsThatMatch(predicate);

// Nullability
Check.That(value).IsNotNull();

// Booleans
Check.That(condition).IsTrue();
Check.That(condition).IsFalse();

// Dictionaries
Check.That(dict).ContainsKey(key);
Check.That(dict).HasSize(n);

// Exception type + details (use try/catch — NFluent's Throws<T> chain doesn't expose ParamName/Message cleanly)
ArgumentException? ex = null;
try { DoThing(badArg); }
catch (ArgumentException e) { ex = e; }
Check.That(ex).IsNotNull();
Check.That(ex!.ParamName).IsEqualTo("badArg");

// Floating-point: IsCloseTo uses ABSOLUTE DELTA, unlike xUnit's decimal-places
Check.That(actual).IsCloseTo(Math.PI, 1e-10);  // ±1e-10 absolute
// NOT:   IsCloseTo(Math.PI, 10) — that means ±10 (vacuous); xUnit's precision:N → use 1e-N
```

### Migration traps (xUnit → NFluent)

| xUnit pattern | NFluent | Watch out |
|---|---|---|
| `Assert.Equal(expected, actual, N)` | `Check.That(actual).IsCloseTo(expected, 1e-N)` | xUnit precision = decimal places; NFluent = absolute delta |
| `Assert.Throws<T>(...)` + property checks | try/catch + `Check.That(ex!.Prop).IsEqualTo(...)` | `Check.ThatCode().Throws<T>()` alone drops ParamName/Message |
| `Assert.True(cond)` | `Check.That(cond).IsTrue()` | Prefer native checks (`Check.That(x).IsStrictlyLessThan(y)`) for better messages |
| `Assert.Null(v)` | `Check.That(v).IsNull()` | |
| `Assert.Same(a, b)` | `Check.That(b).IsSameReferenceAs(a)` | |

## Architecture

Three-layer + ViewModels, plus the dwell-SDK line for external apps (the `Sdk.*` +
`Contracts` projects target **net8.0** on purpose so external integrators on older
runtimes can consume them; the app side is net10):

| Project | Layer | Key details |
|---------|-------|-------------|
| `EyeCtrl.Core` | Domain logic | Framework-agnostic. GazeEngine, DwellDetector, OneEuroFilter, settings, keyboard layouts, word prediction |
| `EyeCtrl.Platform` | OS/SDK bridge | `unsafe` blocks, Win32 SendInput, Tobii SDK via conditional `TOBII_AVAILABLE` (only when SDK DLLs are present on disk) |
| `EyeCtrl.ViewModels` | MVVM layer | CommunityToolkit.Mvvm v8.4.2, `[ObservableProperty]` source generators, `WeakReferenceMessenger` |
| `EyeCtrl.App` | WPF app entrypoint | `WinExe`, `net10.0-windows`, `UseWPF` + `UseWindowsForms`. App.xaml.cs is entry/tray/overlay lifecycle; DI wiring lives in `Services/AppBootstrapper.cs`, event flow in `Services/Coordinators/` |
| `EyeCtrl.Controls` | Gaze control library | `net10.0-windows`, `UseWPF`. DwellArcButton, DwellButton/Toggle/Slider/ComboBox, dwell outline overlay, icons |
| `EyeCtrl.Contracts` | Wire contracts | `net8.0`. Dwell pipe name, `SdkRect`, wire JSON + messages — shared verbatim by the app's pipe server and the SDK's `DwellClient` |
| `EyeCtrl.Sdk.Abstractions` | SDK core | `net8.0`. `DwellClient`, `DwellHeadCore`, element-adapter contracts for cooperating apps |
| `EyeCtrl.Sdk.Wpf` | SDK (WPF) | `net8.0-windows`, `UseWPF`. `Dwell` attachable control + WPF element adapter for WPF host apps |
| `EyeCtrl.Sdk.WinUI` | SDK (WinUI 3) | `net8.0-windows10.0.19041.0`. `EyeCtrlDwell` control + WinUI coordinates/adapter |
| `EyeCtrl.Supervisor` | Watchdog process | `WinExe`, `net10.0-windows`, `UseWPF` + `UseWindowsForms`. Heartbeat IPC, crash supervision, fallback launch chain, caregiver setup UI |
| `EyeCtrl.LM` | ONNX LM | `net10.0` leaf project. `SmallLMPredictor` + `LMTokenizer`. References Core. |
| `EyeCtrl.EyeCare` | Eye-care logic | `net10.0` leaf project. Scheduler, fatigue monitor, gaze exercises, scoring. References Core. |

The solution file is `.slnx` format (new .NET solution format).
SDK is .NET 10.0.102 (pinned in `global.json`).

## Code Conventions

- **File-scoped namespaces** (`namespace EyeCtrl.Core;`)
- **Allman braces** (opening brace on new line)
- **Fields**: `_camelCase` with underscore prefix (e.g., `_gazeProvider`, `_serviceProvider`)
- **Nullable enabled** project-wide. DI-injected fields use `= null!`
- **`var` for obvious types**, explicit types for primitives or ambiguous cases
- **No `#region` blocks** used anywhere

## Testing Conventions

- xUnit **v3 (3.2.2)** via `xunit.v3` — test projects are stand-alone executables (`<OutputType>Exe</OutputType>`). Global usings `Xunit` + `NFluent` are set in `tests/Directory.Build.props` (not per-csproj). UI/STA tests use `[StaFact]`.
- **No mocking library** — use private nested fake classes (e.g., `FakeGazeProvider`, `FakeInputSimulator`)
- Test classes are named `XxxTests` with underscore-separated PascalCase names (`ProcessGaze_MovesCursor`)
- Test namespaces mirror the source namespace (e.g., `EyeCtrl.Core.Tests.Processing`)
- `InternalsVisibleTo` is declared from `EyeCtrl.Core` → `EyeCtrl.Core.Tests` and from `EyeCtrl.App` → `EyeCtrl.App.Tests` (`EyeCtrl.App.csproj`)
- No shared base test class or `IClassFixture` — each test creates its own dependencies
- **Don't run two test suites on this box at once, and prefer unique pipe names in new tests.** Named pipes are machine-global, and a server created with a single instance is stolen by any other process binding the same name — a `dotnet test` in another worktree (this repo routinely has a dozen checked out), or a live `EyeCtrl.Supervisor`/`EyeCtrl.App`. The symptom is deceptive: a *different* test fails on each run, which reads as flaky product code. xUnit's `DisableParallelization` cannot help, being process-local — the only real fix is not to share the name. **`HeartbeatPublisher` and `ExternalDwellService` both take an optional `pipeName` for exactly this** — their tests pass `"eyectrl_watch_test_" + Guid` and `ExternalDwellService.DefaultPipeName + "_test_" + Guid` respectively (#76). Follow that pattern in any new pipe-using service: keep the production name in a `DefaultPipeName` const (the dwell channel's lives in `EyeCtrl.Contracts.DwellPipe.Name`, shared with the SDK's `DwellClient`) and let tests inject a GUID-suffixed one. Before debugging any pipe-test failure, confirm the box is quiet and re-run:

```powershell
Get-Process -Name "dotnet","testhost*","EyeCtrl*" -ErrorAction SilentlyContinue
```

- `ExternalDwellServiceTests` **runs fine in isolation** now — its `RunOnStaThread` helper calls
  `WpfApplicationGate.EnsureApplicationWithTokens()`, which creates the Application singleton and
  merges the overlay token dictionary on demand. (It used to fail 8/8 standalone with
  `XamlParseException: resource "BrushAccent" not found`, depending on another test having
  populated `Application.Current.Resources` first; running the whole `EyeCtrl.App.Tests` project
  remains the safer default.)

## Dependencies & Quirks

- **Tobii SDK** is conditionally referenced. The Platform csproj checks `C:\Program Files\Tobii\Tobii EyeX\` for DLLs at build time. If absent, `TOBII_AVAILABLE` is not defined and `TobiiStreamEngineProvider` is excluded. Override path with `-p:TobiiSdkPath="..."`.
- **Platform uses `UseWPF` not `FrameworkReference`** — Platform only needs `System.Windows.Point` and `System.Windows.Automation` (WPF subset, no WinForms). Narrowed from the broader `Microsoft.WindowsDesktop.App`.
- **CommunityToolkit.Mvvm in Core** — `ObservableObject` is used solely for `INotifyPropertyChanged` on settings objects (source-generator utility). The `WeakReferenceMessenger` is NOT used in Core (only in ViewModels). This is documented as architectural invariant #7.
- **EyeCtrl.LM** is a leaf project for ONNX/Tokenizer natives. Core is free of native ML dependencies — the `ILanguageModelPredictor` interface provides the seam. App references LM, constructs `SmallLMPredictor` in `EventCoordinator.LoadKeyboard`, and passes it to `PredictionOrchestrator` as `ILanguageModelPredictor`.
- **Resource files** (JSON layouts, dictionaries, emojis, `click.wav`) use `<ExcludeFromSingleFile>true</ExcludeFromSingleFile>` — they must remain as loose files alongside the single-file exe.
- **Benchmarks** live in `benchmarks/EyeCtrl.Benchmarks/` and use BenchmarkDotNet. Not part of the solution file (standalone project). The generator correctness tests (`tests/EyeCtrl.Benchmarks.Tests`) are run in CI via a dedicated `dotnet test` step that references the benchmark project directly, without adding it to the solution. See the Benchmarks section below for run instructions.
- **Dictionary generation**: `tools/gen_dicts.py` generates word frequency dictionaries. Run manually when dictionaries need updating.

## Benchmarks

```bash
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter * --job short   # all benchmarks
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Bigram*         # specific class
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *SmallLM*        # ML benchmarks (skips gracefully if no ONNX model)
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*               # all gaze benchmarks (10 classes: filters, detectors, DwellDetector, GazeEngine + WithFatigue, FatigueMonitor params + steady-state, observer fan-out + fault containment)
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeFatigue*        # fatigue monitor micro-benchmarks only
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *WithEyeCare*         # eye-care-wired pipeline integration only
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Adaptation*          # struggle-adaptation: monitor/controller tick + pipeline enabled-vs-disabled
```

18 benchmark classes, 72 benchmarks total — covering WordPredictor, BigramModel, PhraseModel, SentenceCompletionIndex, FuzzyEditDistance, CapitalizationHelper, CasingHelper, PredictionOrchestrator, SmallLMPredictor, LMTokenizer (prediction), the gaze processing pipeline (DirectFilter, SmoothedFilter, OneEuroFilter, FixationDetector, FixationCentroidTracker, GapInterpolator, DwellDetector, end-to-end GazeEngine), the gaze-observer fan-out and its contained-fault path (per-observer try/catch, consecutive-fault counter, slow-observer watchdog, throw-frequency sweep), the eye-care gaze subscriber (FatigueMonitor O(window) scan, EyeCareService pipeline integration), and struggle adaptation (StruggleMonitor.Assess over a filled evidence window, AdaptationController tick + relax ramp, and the pipeline with telemetry wired at adaptation off/on against a bare-pipeline baseline).

SmallLM benchmarks require ONNX model + tokenizer files in `%APPDATA%/EyeCtrl/models` (overridable via `EYECTRL_MODEL_DIR` env var). The `Program.cs` pre-flight check excludes the class entirely if no model is present.

**Toolchain is pinned — don't remove it.** `Program.cs` gives every job an explicit toolchain built by `PinnedProjectToolchain.cs`, resolving `EyeCtrl.Benchmarks.csproj` by walking *up* from the running assembly. BenchmarkDotNet's stock behaviour is to search *down* from the repo root instead, which finds one copy of the project per checkout under `.worktrees/` and aborts every benchmark with `NotSupportedException: Found more than one matching project file ... Benchmark project names needs to be unique.` — i.e. the whole suite is unrunnable while any worktree exists, and no result can be recorded. Consequences:

- Benchmarks run from a worktree pin to *that* worktree's project, which is the copy that was built. No absolute paths, nothing to keep in sync.
- The pin fixes the target framework at `net10.0`, so keep every class on `[SimpleJob(RuntimeMoniker.Net10_0)]`. A stale moniker no longer fails the build — it produces a result labelled with a runtime it did not run on. (`PredictionBenchmarks` was on `Net90` and never produced a recorded report; corrected.)
- `--inProcess` still works and now applies to every job. It generates no project, so it also sidesteps the search — but it runs in the host process with no isolation. Smoke tests only; don't record results from it.

## Logging

Serilog, initialized in `LoggingInitializer.Initialize()`. Logs to `%APPDATA%/EyeCtrl/logs/eyectrl-.log` with daily rolling and 7-day retention. Both Serilog's static `Log` class and injected `ILogger<T>` are used.

**Keyboard E1 lines (`KB-E1 <event> k=v …`).** `KeyboardE1Recorder` (Core) writes one `Information` line per keyboard EVENT — key activation, hold-on, abandonment, bar pick, pick-undo, Space, mode change, mode-undo, edge button, shown, hidden, mirror check — through `ILogger<KeyboardViewModel>`. They are **content-free by contract**: ints, bools and closed ids only (key row/column and class, timings, lengths, the dock edge, the foreground PROCESS name); never a character, a word, a prediction or a window title. `KeyboardE1PrivacyTests` is the merge gate (property names and string vocabularies are allow-lists there, so a new field needs a deliberate edit), and a double must never be logged (Serilog renders it with the current culture). `python tools/kb_log_metrics.py <day log>` turns the lines into the read-outs the key-dwell decision uses; `python tools/kb_log_metrics_test.py` self-tests it in CI.

## Key Files

| File | Purpose |
|------|---------|
| `src/EyeCtrl.App/App.xaml.cs` | Application entry point, overlay lifecycle, tray icon |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | DI container setup (`BuildServiceProvider()`) |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Event coordination (dwell, zoom, action menu) |
| `src/EyeCtrl.Core/GazeEngine.cs` | Core gaze pipeline |
| `src/EyeCtrl.Core/Settings/SettingsService.cs` | JSON settings persistence with validation |

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **eyectrl** (12287 symbols, 28850 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "main"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/eyectrl/context` | Codebase overview, check index freshness |
| `gitnexus://repo/eyectrl/clusters` | All functional areas |
| `gitnexus://repo/eyectrl/processes` | All execution flows |
| `gitnexus://repo/eyectrl/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
