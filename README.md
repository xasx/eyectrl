# EyeCtrl

Full PC control using eye tracking. Built for the Tobii Dynavox PCEye 5.

EyeCtrl replaces mouse and keyboard with gaze-driven overlays — a cursor circle follows your eyes, dwell activates actions, and an on-screen keyboard lets you type. Everything is configurable and works on a single monitor.

## Features

- **Gaze cursor** — circle follows eye position with adaptive smoothing (1€ Filter)
- **Dwell activation** — look at a button long enough to "click" it, with visual progress indicators
- **Action menu** — radial menu for left click, right click, double click, drag, and scroll
- **Zoom mode** — magnify a screen region for precise targeting
- **On-screen keyboard** — QWERTY/QWERTZ/AZERTY layouts, word prediction, emoji support
- **Toolbar** — pause, zoom toggle, keyboard toggle, settings
- **Settings** — 7 tabs of configurable options, all dwell-activated
- **System tray** — pause/resume, open settings, exit

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- Tobii Dynavox PCEye 5 (or compatible Tobii eye tracker)
- Tobii Dynavox Computer Control installed (provides `tobii_interaction_lib_cs.dll`)

Without an eye tracker, the app falls back to mouse-cursor-as-gaze mode.

## Build

```bash
dotnet build EyeCtrl.slnx
```

## Run

```bash
dotnet run --project src/EyeCtrl.App
```

## Publish

Self-contained single-file executable:

```bash
dotnet publish src/EyeCtrl.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Test

Unit tests (fast, no display needed):

```bash
dotnet test tests/EyeCtrl.Core.Tests
```

UI tests (requires display, launches the real app):

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"
```

## Project Structure

```
src/
  EyeCtrl.Core/       Core logic — models, settings, processing (framework-agnostic)
  EyeCtrl.Platform/   Win32 input simulation, Tobii SDK integration
  EyeCtrl.App/        WPF overlays and application wiring
tests/
  EyeCtrl.Core.Tests/     Unit tests for Core (xUnit)
  EyeCtrl.Platform.Tests/ Unit tests for Platform (xUnit)
  EyeCtrl.App.Tests/      UI/E2E tests with FlaUI (xUnit)
docs/
  plans/                   Design docs and implementation plans
```

## Architecture

Three-layer design:

- **Core** — `GazeEngine`, `InteractionManager` state machine, `DwellDetector`, `OneEuroFilter`, settings, keyboard layouts, word prediction. No framework dependencies.
- **Platform** — `TobiiInteractionLibProvider` (eye tracker), `MouseGazeProvider` (fallback), `Win32InputSimulator` (SendInput for clicks/keystrokes).
- **App** — WPF overlay windows (cursor circle, activation button, action menu, toolbar, keyboard, zoom, settings). All code-behind driven. `App.xaml.cs` is the central wiring hub.

## License

Private project. Not licensed for redistribution.
