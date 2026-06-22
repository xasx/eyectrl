# AGENTS.md

## Build & Run

```bash
dotnet build EyeCtrl.slnx                # build everything
dotnet run --project src/EyeCtrl.App     # run (falls back to mouse-as-gaze if no eye tracker)
dotnet run --project src/EyeCtrl.App -- --test-mode   # skip splash, force mouse gaze provider
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
- `build-and-test` — builds the solution and runs `dotnet test --filter "Category!=UI"` (blocking)

UI tests (`Category=UI`, FlaUI) are **not** run in CI — they need an interactive desktop
session that the headless `windows-latest` runner doesn't provide. Run them locally with
`dotnet test --filter "Category=UI"`.

No linter, formatter, or code coverage configured.

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

Three-layer + ViewModels:

| Project | Layer | Key details |
|---------|-------|-------------|
| `EyeCtrl.Core` | Domain logic | Framework-agnostic. GazeEngine, DwellDetector, OneEuroFilter, settings, keyboard layouts, word prediction |
| `EyeCtrl.Platform` | OS/SDK bridge | `unsafe` blocks, Win32 SendInput, Tobii SDK via conditional `TOBII_AVAILABLE` (only when SDK DLLs are present on disk) |
| `EyeCtrl.ViewModels` | MVVM layer | CommunityToolkit.Mvvm v8.4.0, `[ObservableProperty]` source generators, `WeakReferenceMessenger` |
| `EyeCtrl.App` | WPF app entrypoint | `WinExe`, `net10.0-windows`, `UseWPF` + `UseWindowsForms`. App.xaml.cs is the central wiring hub (DI, overlay lifecycle) |
| `EyeCtrl.LM` | ONNX LM | `net10.0` leaf project. `SmallLMPredictor` + `LMTokenizer`. References Core. |

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

- xUnit 2.9.x. Global using `Xunit` set in csproj.
- **No mocking library** — use private nested fake classes (e.g., `FakeGazeProvider`, `FakeInputSimulator`)
- Test classes are named `XxxTests` with underscore-separated PascalCase names (`ProcessGaze_MovesCursor`)
- Test namespaces mirror the source namespace (e.g., `EyeCtrl.Core.Tests.Processing`)
- `InternalsVisibleTo` is only declared from `EyeCtrl.Core` → `EyeCtrl.Core.Tests`
- No shared base test class or `IClassFixture` — each test creates its own dependencies

## Dependencies & Quirks

- **Tobii SDK** is conditionally referenced. The Platform csproj checks `C:\Program Files\Tobii\Tobii EyeX\` for DLLs at build time. If absent, `TOBII_AVAILABLE` is not defined and `TobiiStreamEngineProvider` is excluded. Override path with `-p:TobiiSdkPath="..."`.
- **Platform uses `UseWPF` not `FrameworkReference`** — Platform only needs `System.Windows.Point` and `System.Windows.Automation` (WPF subset, no WinForms). Narrowed from the broader `Microsoft.WindowsDesktop.App`.
- **CommunityToolkit.Mvvm in Core** — `ObservableObject` is used solely for `INotifyPropertyChanged` on settings objects (source-generator utility). The `WeakReferenceMessenger` is NOT used in Core (only in ViewModels). This is documented as architectural invariant #7.
- **EyeCtrl.LM** is a leaf project for ONNX/Tokenizer natives. Core is free of native ML dependencies — the `ILanguageModelPredictor` interface provides the seam. App references LM, constructs `SmallLMPredictor` in `EventCoordinator.LoadKeyboard`, and passes it to `PredictionOrchestrator` as `ILanguageModelPredictor`.
- **Resource files** (JSON layouts, dictionaries, emojis, `click.wav`) use `<ExcludeFromSingleFile>true</ExcludeFromSingleFile>` — they must remain as loose files alongside the single-file exe.
- **Benchmarks** live in `benchmarks/EyeCtrl.Benchmarks/` and use BenchmarkDotNet. Not part of the solution file (standalone project). See the Benchmarks section below for run instructions.
- **Dictionary generation**: `tools/gen_dicts.py` generates word frequency dictionaries. Run manually when dictionaries need updating.

## Benchmarks

```bash
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter * --job short   # all benchmarks
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Bigram*         # specific class
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *SmallLM*        # ML benchmarks (skips gracefully if no ONNX model)
```

7 benchmark classes, 28 benchmarks total — covering WordPredictor, BigramModel, PhraseModel, SentenceCompletionIndex, FuzzyEditDistance, CapitalizationHelper, CasingHelper, PredictionOrchestrator, SmallLMPredictor, and LMTokenizer.

SmallLM benchmarks require ONNX model + tokenizer files in `%APPDATA%/EyeCtrl/models` (overridable via `EYECTRL_MODEL_DIR` env var). The `Program.cs` pre-flight check excludes the class entirely if no model is present.

## Logging

Serilog, initialized in `LoggingInitializer.Initialize()`. Logs to `%APPDATA%/EyeCtrl/logs/eyectrl-.log` with daily rolling and 7-day retention. Both Serilog's static `Log` class and injected `ILogger<T>` are used.

## Key Files

| File | Purpose |
|------|---------|
| `src/EyeCtrl.App/App.xaml.cs` | Application entry point, overlay lifecycle, tray icon |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | DI container setup (`BuildServiceProvider()`) |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Event coordination (dwell, zoom, action menu) |
| `src/EyeCtrl.Core/GazeEngine.cs` | Core gaze pipeline |
| `src/EyeCtrl.Core/Services/SettingsService.cs` | JSON settings persistence with validation |

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **eyectrl** (5788 symbols, 13001 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "main"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.

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
