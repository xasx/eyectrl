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

**No linter, formatter, or CI is configured.** There is no `.editorconfig`, no `Directory.Build.props`, and no GitHub Actions. The repo relies on `dotnet build` for compilation checks only.

## Architecture

Three-layer + ViewModels:

| Project | Layer | Key details |
|---------|-------|-------------|
| `EyeCtrl.Core` | Domain logic | Framework-agnostic. GazeEngine, DwellDetector, OneEuroFilter, settings, keyboard layouts, word prediction |
| `EyeCtrl.Platform` | OS/SDK bridge | `unsafe` blocks, Win32 SendInput, Tobii SDK via conditional `TOBII_AVAILABLE` (only when SDK DLLs are present on disk) |
| `EyeCtrl.ViewModels` | MVVM layer | CommunityToolkit.Mvvm v8.4.0, `[ObservableProperty]` source generators, `WeakReferenceMessenger` |
| `EyeCtrl.App` | WPF app entrypoint | `WinExe`, `net10.0-windows`, `UseWPF` + `UseWindowsForms`. App.xaml.cs is the central wiring hub (DI, overlay lifecycle) |

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
- **Resource files** (JSON layouts, dictionaries, emojis, `click.wav`) use `<ExcludeFromSingleFile>true</ExcludeFromSingleFile>` — they must remain as loose files alongside the single-file exe.
- **Benchmarks** live in `benchmarks/EyeCtrl.Benchmarks/` and use BenchmarkDotNet. Not part of the solution file (standalone project).
- **Dictionary generation**: `tools/gen_dicts.py` generates word frequency dictionaries. Run manually when dictionaries need updating.

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

This project is indexed by GitNexus as **eyectrl** (5021 symbols, 11207 relationships, 264 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

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
