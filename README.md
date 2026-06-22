# EyeCtrl

[![CI](https://github.com/xasx/eyectrl/actions/workflows/ci.yml/badge.svg)](https://github.com/xasx/eyectrl/actions/workflows/ci.yml)

Full PC control using eye tracking. Built for the Tobii Dynavox PCEye 5.

EyeCtrl replaces mouse and keyboard with gaze-driven overlays — a cursor circle follows your eyes, dwell activates actions, and an on-screen keyboard lets you type. Everything is configurable and works on a single monitor.

## Features

- **Gaze cursor** — circle follows eye position with adaptive smoothing (1€ Filter)
- **Dwell activation** — look at a button long enough to "click" it, with visual progress indicators
- **Action menu** — radial menu for left click, right click, double click, drag, and scroll
- **Manual selection** — magnify a screen region for precise targeting; action menu buttons render outside the zoom circle in a ring layout
- **On-screen keyboard** — QWERTY/QWERTZ/AZERTY layouts, word prediction, emoji support; audible click feedback (toggle in Settings → Keyboard)
- **Toolbar** — pause, zoom toggle, keyboard toggle, settings
- **Settings** — 9 tabs of configurable options, all dwell-activated
- **System tray** — pause/resume, open settings, exit
- **Logging** — structured event log via Serilog for diagnostics

## Requirements

- Windows 10/11
- .NET 10 SDK 10.0.102 (pinned in `global.json`)
- Tobii Dynavox PCEye 5 (or compatible Tobii eye tracker with Stream Engine SDK)
- Tobii Stream Engine SDK installed for real eye-tracker support (provides `Tobii.StreamEngine.Net.dll` and `tobii_stream_engine.dll` in `C:\Program Files\Tobii\Tobii EyeX\`)

Without an eye tracker, the app falls back to mouse-cursor-as-gaze mode. If the Tobii SDK DLLs are not found, the Platform project builds without `TOBII_AVAILABLE` and excludes `TobiiStreamEngineProvider`; override the SDK path with `-p:TobiiSdkPath="..."` when needed.

## Build

```bash
dotnet build EyeCtrl.slnx
```

## Run

```bash
dotnet run --project src/EyeCtrl.App
```

With `--test-mode` to skip splash screen and force mouse gaze provider:

```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```

## Publish

Self-contained single-file executable:

```bash
dotnet publish src/EyeCtrl.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Test

```bash
dotnet test tests/EyeCtrl.Core.Tests            # unit tests (fast, no display)
dotnet test tests/EyeCtrl.Platform.Tests        # platform unit tests
dotnet test tests/EyeCtrl.ViewModels.Tests      # viewmodel unit tests
dotnet test                                     # all tests
dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"  # UI tests (needs display)
```

No linter, formatter, or CI is configured. The repo relies on `dotnet build` and `dotnet test` for local verification.

## Benchmarks

Benchmarks live in `benchmarks/EyeCtrl.Benchmarks/` and use BenchmarkDotNet. The benchmark project is standalone and is not included in the solution file.

```bash
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter * --job short   # all benchmarks
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Bigram*         # bigram benchmarks
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *SmallLM*        # ML benchmarks
```

SmallLM benchmarks require ONNX model and tokenizer files in `%APPDATA%/EyeCtrl/models` (or `EYECTRL_MODEL_DIR`). If no model is present, the SmallLM benchmark class is skipped.

## Development notes

- Use file-scoped namespaces, Allman braces, and `_camelCase` fields.
- `var` is preferred for obvious types; use explicit types for primitives or ambiguous values.
- Tests use xUnit without a mocking library. Create private nested fake classes inside the test class.
- Resource files (JSON layouts, dictionaries, emojis, and `click.wav`) use `ExcludeFromSingleFile`, so keep them loose next to the published single-file executable.
- Run `tools/gen_dicts.py` when keyboard word-frequency dictionaries need updating.

## Project Structure

```
src/
  EyeCtrl.Core/        Domain logic (framework-agnostic)
  EyeCtrl.Platform/    Win32 input simulation, Tobii SDK integration
  EyeCtrl.ViewModels/  MVVM layer (CommunityToolkit.Mvvm)
  EyeCtrl.App/         WPF overlays and application wiring
tests/
  EyeCtrl.Core.Tests/         Unit tests for Core (xUnit)
  EyeCtrl.Platform.Tests/     Unit tests for Platform (xUnit)
  EyeCtrl.ViewModels.Tests/   Unit tests for ViewModels (xUnit)
  EyeCtrl.App.Tests/          Service tests + UI/E2E tests (xUnit, FlaUI)
benchmarks/
  EyeCtrl.Benchmarks/         BenchmarkDotNet benchmarks (standalone)
docs/
  plans/                      Design docs and implementation plans
  superpowers/
    specs/                    Superpowers design specs
    plans/                    Superpowers implementation plans
```

## Architecture

Four-layer design (Core → Platform → ViewModels → App):

- **Core** — `GazeEngine`, `InteractionManager`, `DwellDetector`, `OneEuroFilter`, settings, keyboard layouts, word prediction. No framework dependencies.
- **Platform** — `GazeProviderFactory`, `TobiiStreamEngineProvider` (eye tracker), `MouseGazeProvider` (fallback), `InputSimulator` (SendInput for clicks/keystrokes). `unsafe` Win32 code.
- **ViewModels** — MVVM layer using CommunityToolkit.Mvvm v8.4.0 with source generators and `WeakReferenceMessenger`.
- **App** — WPF overlay windows (cursor circle, activation button, action menu, toolbar, keyboard, emoji grid, manual selection). `App.xaml.cs` is the central wiring hub for DI and overlay lifecycle; `EventCoordinator` coordinates dwell, zoom, and action-menu events.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full architecture diagram, execution flows, and functional areas.
See [AGENTS.md](AGENTS.md) for development conventions and dependency quirks.

## Troubleshooting

- **No eye tracker connected:** the app starts in mouse-cursor-as-gaze fallback mode.
- **Tobii provider unavailable:** install the Tobii Stream Engine SDK or pass `-p:TobiiSdkPath="..."` when building.
- **Single-file publish missing resources:** keep JSON layouts, dictionaries, emojis, and `click.wav` as loose files next to the executable.
- **Runtime diagnostics:** Serilog writes daily logs to `%APPDATA%/EyeCtrl/logs/eyectrl-.log` with 7-day retention.

## Key files

| File | Purpose |
|------|---------|
| `src/EyeCtrl.App/App.xaml.cs` | Application entry point, overlay lifecycle, tray icon |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | DI container setup |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Event coordination for dwell, zoom, and action menu |
| `src/EyeCtrl.Core/GazeEngine.cs` | Core gaze pipeline |
| `src/EyeCtrl.Core/Services/SettingsService.cs` | JSON settings persistence and validation |

## License

Private project. Not licensed for redistribution.
