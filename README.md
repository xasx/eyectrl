# EyeCtrl

[![CI](https://github.com/xasx/eyectrl/actions/workflows/ci.yml/badge.svg)](https://github.com/xasx/eyectrl/actions/workflows/ci.yml)

EyeCtrl is a Windows accessibility application for controlling a PC with eye tracking. It is built for the Tobii Dynavox PCEye 5 and compatible Tobii Stream Engine devices.

EyeCtrl replaces the mouse and keyboard with gaze-driven overlays. A cursor circle follows the user's gaze, dwell activates controls, an action menu provides mouse actions, and an on-screen keyboard supports text entry and prediction. All primary controls are configurable and the application supports single-monitor setups.

## Features

- **Gaze cursor** - follows eye position with configurable adaptive smoothing, including a One Euro filter.
- **Dwell activation** - activates controls after a configurable gaze duration with visual progress feedback.
- **Action menu** - radial actions for left click, right click, double click, drag, and scrolling.
- **Manual selection** - magnifies a screen region for precise targeting and positions action buttons around the zoom area.
- **Auto-selection** - identifies WPF controls under the gaze point and exposes their actions through the dwell system.
- **On-screen keyboard** - QWERTY, QWERTZ, and AZERTY layouts with word prediction, emoji support, modifier keys, and optional click sounds.
- **Toolbar and system tray** - pause/resume, zoom, keyboard, settings, and application exit controls.
- **Calibration** - screen calibration with affine and polynomial transforms for physical screen coordinates.
- **Eye-care support** - fatigue monitoring, scheduled breaks, gaze exercises, and eye-care scoring.
- **External dwell SDK** - named-pipe integration for adding gaze-dwell behavior to external WPF and WinUI applications.
- **Supervisor watchdog** - optional crash recovery, heartbeat monitoring, fallback launching, and a supervisor tray UI.
- **Diagnostics** - structured Serilog logs for the application and supervisor.

## Requirements

- Windows 10 or Windows 11.
- .NET 10 SDK 10.0.102, pinned in `global.json`.
- Tobii Dynavox PCEye 5 or another compatible Tobii eye tracker.
- Tobii Stream Engine SDK for real eye-tracker support. The build looks for `Tobii.StreamEngine.Net.dll` and `tobii_stream_engine.dll` under `C:\Program Files\Tobii\Tobii EyeX\`.

The application falls back to mouse-cursor-as-gaze mode when no eye tracker is connected. If the Tobii SDK DLLs are unavailable, the Platform project builds without `TOBII_AVAILABLE` and excludes the Tobii provider. Override the SDK location when necessary:

```bash
dotnet build EyeCtrl.slnx -p:TobiiSdkPath="C:\path\to\tobii-sdk"
```

## Build

Build the complete solution:

```bash
dotnet build EyeCtrl.slnx
```

## Run

Start the main application:

```bash
dotnet run --project src/EyeCtrl.App
```

Use test mode to skip the splash screen and force mouse-cursor-as-gaze mode:

```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```

The supervisor is a separate Windows application. It can be launched directly when testing watchdog behavior:

```bash
dotnet run --project src/EyeCtrl.Supervisor
```

## Publish

Create a self-contained, single-file x64 build:

```bash
dotnet publish src/EyeCtrl.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Resource files such as keyboard layouts, dictionaries, emoji data, and `click.wav` are intentionally kept loose beside the single-file executable. Do not remove them from the published directory.

## Test

Run all non-UI tests:

```bash
dotnet test --filter "Category!=UI"
```

Run the complete test suite, including tests that do not require a display:

```bash
dotnet test
```

Run the UI tests locally in an interactive Windows desktop session:

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"
```

The test suite uses xUnit v3 and NFluent. UI tests use FlaUI and are not run in headless CI. GitHub Actions builds the solution and runs the non-UI tests on pushes to `main` and pull requests targeting `main`.

## Benchmarks

Benchmarks live in `benchmarks/EyeCtrl.Benchmarks/` and use BenchmarkDotNet. The benchmark project is standalone and is not included in `EyeCtrl.slnx`.

```bash
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter * --job short
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Bigram*
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *WithEyeCare*
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *SmallLM*
```

SmallLM benchmarks require an ONNX model and tokenizer under `%APPDATA%\EyeCtrl\models`. Set `EYECTRL_MODEL_DIR` to use another directory. If no model is available, the SmallLM benchmark class is skipped.

## Project Structure

The solution uses a layered architecture with separate SDK, eye-care, and supervisor components:

```text
src/
  EyeCtrl.Core/              Framework-agnostic gaze, dwell, settings, and prediction logic
  EyeCtrl.Platform/          Win32 input, mouse fallback, and conditional Tobii integration
  EyeCtrl.Controls/          WPF control discovery for auto-selection mode
  EyeCtrl.ViewModels/        MVVM view models and application messages
  EyeCtrl.App/               WPF application, overlays, settings, tray, and composition root
  EyeCtrl.LM/                ONNX language model and tokenizer
  EyeCtrl.EyeCare/           Fatigue monitoring, breaks, exercises, and scoring
  EyeCtrl.Contracts/         Named-pipe wire messages shared by the SDK and app
  EyeCtrl.Sdk.Abstractions/  Framework-agnostic external dwell client and adapters
  EyeCtrl.Sdk.Wpf/           WPF attached properties for external dwell
  EyeCtrl.Sdk.WinUI/         WinUI 3 attached properties for external dwell
  EyeCtrl.Supervisor/        Watchdog, crash recovery, fallback, and supervisor UI
tests/
  EyeCtrl.*.Tests/           Unit, integration, SDK, eye-care, supervisor, and UI tests
benchmarks/
  EyeCtrl.Benchmarks/        Standalone BenchmarkDotNet project
docs/
  plans/                     Design documents and implementation plans
  superpowers/               Superpowers design specs and implementation plans
```

The main application follows `App -> ViewModels/Platform -> Core`. The SDK layer is independent of the application internals and communicates with `EyeCtrl.App` through named-pipe IPC. The supervisor is a separate process that monitors the app through heartbeat messages and can relaunch it or start a configured fallback application.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the dependency graph, execution flows, functional areas, and SDK protocol details.

## External Dwell SDK

External applications can opt controls into gaze dwell without depending on the WPF app internals:

- `EyeCtrl.Contracts` defines the named-pipe wire format.
- `EyeCtrl.Sdk.Abstractions` provides `DwellClient` and framework-neutral element adapters.
- `EyeCtrl.Sdk.Wpf` provides WPF attached properties such as `Dwell.Enabled` and `Dwell.Mode`.
- `EyeCtrl.Sdk.WinUI` provides the corresponding WinUI 3 integration.

The running EyeCtrl app hosts the named-pipe server and renders dwell feedback for registered external controls.

## Troubleshooting

- **No eye tracker connected:** EyeCtrl starts in mouse-cursor-as-gaze fallback mode.
- **Tobii provider unavailable:** install the Tobii Stream Engine SDK or build with `-p:TobiiSdkPath="..."`.
- **Application starts paused:** check the `StartPaused` setting or whether it was launched with the supervisor's `--paused` retry handoff.
- **Single-file publish is missing resources:** keep JSON layouts, dictionaries, emoji data, and `click.wav` beside the executable.
- **Runtime diagnostics:** application logs are written to `%APPDATA%\EyeCtrl\logs\eyectrl-.log` with daily rolling and seven-day retention. Supervisor logs use `%APPDATA%\EyeCtrl\logs\supervisor-.log`.
- **UI tests do not run:** run them in an interactive Windows desktop session with `dotnet test --filter "Category=UI"`.

## Development

- Use file-scoped namespaces, Allman braces, and `_camelCase` fields.
- Keep Core free of native ML dependencies; use `ILanguageModelPredictor` for the language-model seam.
- Tests use private nested fakes rather than a mocking library.
- Run `tools/gen_dicts.py` when keyboard word-frequency dictionaries need updating.
- No linter, formatter, or code-coverage tool is configured; `dotnet build` and `dotnet test` are the primary verification commands.

See [AGENTS.md](AGENTS.md) for detailed build commands, testing conventions, dependency quirks, and repository guidance.

## Key Files

| File | Purpose |
|------|---------|
| `src/EyeCtrl.App/App.xaml.cs` | Application entry point, overlay lifecycle, and tray icon |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Dependency-injection composition root |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Dwell, zoom, keyboard, and action-menu coordination |
| `src/EyeCtrl.Core/GazeEngine.cs` | Core gaze-processing pipeline |
| `src/EyeCtrl.Core/Services/SettingsService.cs` | JSON settings persistence and validation |
| `src/EyeCtrl.Supervisor/Program.cs` | Supervisor startup and watchdog orchestration |
| `src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs` | Crash-recovery state transitions |
| `src/EyeCtrl.Contracts/` | External SDK named-pipe contracts |

## License

Private project. Not licensed for redistribution.
