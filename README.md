# EyeCtrl

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
- .NET 10.0 Runtime
- Tobii Dynavox PCEye 5 (or compatible Tobii eye tracker with Stream Engine SDK)
- Tobii Stream Engine SDK installed (provides `Tobii.StreamEngine.Net.dll` and `tobii_stream_engine.dll` in `C:\Program Files\Tobii\Tobii EyeX\`)

Without an eye tracker, the app falls back to mouse-cursor-as-gaze mode.

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
```

## Architecture

Four-layer design (Core → Platform → ViewModels → App):

- **Core** — `GazeEngine`, `InteractionManager`, `DwellDetector`, `OneEuroFilter`, settings, keyboard layouts, word prediction. No framework dependencies.
- **Platform** — `GazeProviderFactory`, `TobiiStreamEngineProvider` (eye tracker), `MouseGazeProvider` (fallback), `InputSimulator` (SendInput for clicks/keystrokes). `unsafe` Win32 code.
- **ViewModels** — MVVM layer using CommunityToolkit.Mvvm v8.4.0 with source generators and `WeakReferenceMessenger`.
- **App** — WPF overlay windows (cursor circle, activation button, action menu, toolbar, keyboard, emoji grid, manual selection). `EventCoordinator` is the central wiring hub; `App.xaml.cs` manages overlay lifecycle and tray icon.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full architecture diagram, execution flows, and functional areas.
See [AGENTS.md](AGENTS.md) for development conventions and dependency quirks.

## License

Private project. Not licensed for redistribution.
