# EyeCtrl Architecture

> Auto-generated from the GitNexus knowledge graph: **eyectrl** (744 files, 9053 symbols, 300 execution flows, 47 functional areas).
> ⚠️ The indexed graph is **142 commits behind HEAD** at the time of generation — figures reflect the indexed snapshot, not the working tree. Re-index with `node .gitnexus/run.cjs analyze` (or `npx gitnexus analyze`) to refresh. FTS indexes were missing at query time (`gitnexus analyze --repair-fts` to rebuild); process data below was gathered via Cypher.

EyeCtrl is a gaze control platform for Windows that turns an eye tracker (Tobii Stream Engine, or a mouse fallback) into a cursor, dwell-click, on-screen keyboard, and word-prediction input system. The codebase is split into **twelve .NET 10 projects** with a strict dependency direction (`App → ViewModels → Core`, `App → Platform → Core`), a standalone **Supervisor** watchdog process, an **EyeCare** subsystem, plus a standalone SDK layer (`Contracts → Abstractions → Sdk.Wpf / Sdk.WinUI`) that enables third-party apps to add gaze-dwell via named-pipe IPC.

Two named-pipe channels cross process boundaries:

| Pipe | Direction | Purpose |
|------|-----------|---------|
| `eyectrl_dwell` (`EyeCtrl.Contracts.DwellPipe.Name`) | Third-party app → EyeCtrl.App | External dwell: register UI elements as dwell targets |
| `eyectrl_watch` | EyeCtrl.App → EyeCtrl.Supervisor | Heartbeat: the supervisor watches this and relaunches/falls back if the App stalls |

---

## 1. Overview

```
+------------------------------------------------------------------+
|                        EyeCtrl.App (WPF)                         |
|  App.xaml.cs  AppBootstrapper  EventCoordinator                   |
|  ExternalDwellService  GazeEngineAdapter  OverlayManager          |
|  Overlays/*  Views/*  TrayIconManager  HeartbeatPublisher          |
+----------------------------+-------------------------------------+
                             |
        +--------------------+--------------------+
        |                                         |
+-------v--------+                      +----------v--------+
| ViewModels     |  <-- CommunityToolkit.Mvvm  |  Platform     |
|  (MVVM layer)  |      [ObservableProperty]   |  (OS / SDK)   |
|  IMessenger    |      WeakReferenceMessenger |  Win32, Tobii |
+-------+--------+                      +---------+---------+
        |                                         |
        +-------------------+---------------------+
                            |
                 +----------v----------+        +------------------+
                 |        Core         |        |   EyeCtrl.EyeCare |
                 | (framework-agnostic)|        |  Scheduler /       |
                 | GazeEngine          |<-------|  FatigueMonitor /  |
                 | DwellDetector       |        |  Exercises / Scoring|
                 | Prediction/*        |        +------------------+
                 | Adaptation/*         |
                 | Settings/*           |        +------------------+
                 +----------+----------+        |   EyeCtrl.LM      |
                            |                   |  SmallLMPredictor  |
                            +<------------------|  LMTokenizer (ONNX)|
                                                +------------------+

  +---------------------+        eyectrl_watch          +-----------------------+
  |  EyeCtrl.Supervisor  |<------ heartbeat ------------|  EyeCtrl.App           |
  |  (watchdog process)  |                             |  HeartbeatPublisher     |
  |  FallbackAtLauncher  |------- relaunch ----------->|                        |
  +----------------------+                             +-----------------------+

  ┌──────────── Named-pipe IPC (eyectrl_dwell) ────────────┐
  │                                                         │
  │  ExternalDwellService (server) ◄──── DwellClient (SDK) │
  │                                                         │
  └─────────────────────────────────────────────────────────┘
                     │                         │
          +----------+----------+    +---------+---------+
          | EyeCtrl.Sdk.Wpf     |    | EyeCtrl.Sdk.WinUI |
          | Dwell (attached     |    | Dwell (attached    |
          |   properties)       |    |   properties)      |
          | WpfElementAdapter   |    | WinUiElementAdapter|
          +---------------------+    +--------------------+
                     │                         │
                     └────────┬────────────────┘
                              │
                   +----------v----------+
                   | EyeCtrl.Sdk.Abstractions
                   | IDwellElementAdapter |
                   | DwellClient         |
                   | OutboundRectTracker  |
                   +---------------------+
                              │
                   +----------v----------+
                   | EyeCtrl.Contracts   |
                   | WireMessages        |
                   | SdkRect  /  DwellPipe|
                   +---------------------+
```

**Dependency direction**: `App` references everything. `ViewModels` and `Platform` depend on `Core`. `Core` has zero internal dependencies. `EyeCtrl.EyeCare` and `EyeCtrl.LM` are leaf projects that reference `Core` only — Core never references back (the LM is reached through the `ILanguageModelPredictor` interface seam; EyeCare subscribes to gaze samples). The SDK layer (`Contracts → Abstractions → Sdk.Wpf/WinUI`) is fully independent of Core and App — it communicates with EyeCtrl exclusively via named-pipe IPC using the `Contracts` wire format. The `Supervisor` is a separate executable that shares `Contracts`/`Core` types but does **not** reference `App`.

**Cross-project import weights** (from IMPORTS edges in the knowledge graph, current index):

| Dependency | Import count |
|------------|-------------:|
| `App → Core` | 174 |
| `ViewModels → Core` | 92 |
| `Platform → Core` | 34 |
| `App → ViewModels` | 33 |
| `EyeCare → Core` | 20 |
| `Supervisor → Core` | 10 |
| `App → Platform` | 8 |
| `Sdk.Abstractions → Contracts` | 5 |
| `Sdk.Wpf → Sdk.Abstractions` | 3 |
| `Sdk.WinUI → Sdk.Abstractions` | 2 |
| `LM → Core` | 1 |
| `App → Contracts` | 1 |

`App` leans heavily on `Core` (174 imports — it is the composition root). `ViewModels` depends only on `Core` (92), never on `Platform`. The leaf subsystems (`EyeCare` 20, `LM` 1) and the `Supervisor` (10) reference `Core` only. The SDK chain is fully isolated (`Contracts → Abstractions → Sdk.Wpf/WinUI`).

---

## 2. The Twelve Projects

| Project | Target | Role |
|---|---|---|
| `EyeCtrl.Core` | `net10.0` | Domain logic, gaze pipeline, prediction, adaptation, settings. Zero UI / OS dependencies. |
| `EyeCtrl.Platform` | `net10.0-windows` (`UseWPF`) | `unsafe` Win32 (`SendInput`), Tobii Stream Engine SDK (conditional, only when DLLs are present). Only needs `System.Windows.Point` + `System.Windows.Automation` (WPF subset, no WinForms). |
| `EyeCtrl.ViewModels` | `net10.0` | MVVM with `CommunityToolkit.Mvvm` v8.4.0. `[ObservableProperty]` source generators, `WeakReferenceMessenger` for pub/sub. |
| `EyeCtrl.App` | `net10.0-windows`, `WinExe`, `UseWPF`+`UseWindowsForms` | WPF entry point, DI wiring, overlay windows, settings UI, tray icon, **ExternalDwellService** (named-pipe server), **HeartbeatPublisher** (watchdog-pipe client). |
| `EyeCtrl.Controls` | `net10.0-windows` | Reusable WPF custom controls (e.g. `DwellControl`). |
| `EyeCtrl.LM` | `net10.0` | ONNX neural language model predictor (`SmallLMPredictor`) and tokenizer (`LMTokenizer`). Implements `ILanguageModelPredictor` from Core. Leaf project. |
| `EyeCtrl.EyeCare` | `net10.0` | Eye-care subsystem: break scheduler, fatigue monitor, gaze exercises, scoring. References Core (subscribes to gaze samples). Leaf project. |
| `EyeCtrl.Supervisor` | `net10.0-windows`, `WinExe`, `UseWPF` | Standalone watchdog process. Monitors the App's heartbeat pipe; on stall it launches a fallback candidate (`FallbackAtLauncher`/`FallbackCandidate`). Single-instance guarded. |
| `EyeCtrl.Contracts` | `net8.0` | Shared wire-format records (`WireMessage`, `SdkRect`) + `DwellPipe.Name` constant. Zero dependencies. Used by both App (server) and SDK (client) so the pipe name can never drift. |
| `EyeCtrl.Sdk.Abstractions` | `net8.0` | Framework-agnostic SDK surface: `IDwellElementAdapter`, `DwellClient` (named-pipe client), `OutboundRectTracker`. |
| `EyeCtrl.Sdk.Wpf` | `net8.0-windows` | WPF attached properties (`Dwell.Enabled`, `Dwell.Mode`). `WpfElementAdapter` bridges WPF elements to `IDwellElementAdapter`. |
| `EyeCtrl.Sdk.WinUI` | `net8.0-windows10.0.19041` | WinUI 3 attached properties. `WinUiElementAdapter` resolves HWNDs via `XamlRoot.ContentIslandEnvironment`. Per-dispatcher rect-refresh timers via `AdapterGroupTracker`. |

Tests (`/tests/`) mirror the source: `Core.Tests`, `Platform.Tests`, `ViewModels.Tests`, `App.Tests`, `Controls.Tests`, `LM.Tests`, `EyeCare.Tests`, `Supervisor.Tests`, `Sdk.Abstractions.Tests`, `Sdk.Wpf.Tests`, `Sdk.WinUI.Tests` — xUnit v3 (3.2.2) + NFluent 3.1.0, private nested fakes (no mocking library).

---

## 3. Functional Areas (GitNexus Communities)

Communities are auto-detected functional clusters (Leiden algorithm). Top 20 of 47 areas, ranked by symbol count from the indexed graph:

| Area | Symbols | Cohesion | Purpose |
|---|---:|---:|---|
| **Services** | 612 | 79% | App-layer composition: `AppBootstrapper`, `EventCoordinator`, `GazeEngineAdapter`, `OverlayManager`, `TrayIconManager`, `ExternalDwellService`, model registry/download. |
| **Prediction** | 368 | 86% | Word prediction: `WordPredictor`, `BigramModel`, `PhraseModel`, `SentenceCompletionIndex`, `FuzzyEditDistance`, `CapitalizationHelper`, `PredictionOrchestrator`, `SmallLMPredictor`. |
| **Adaptation** | 246 | 73% | Struggle-driven adaptation: `StruggleMonitor`, `AdaptationController`, live fixation-tolerance widening. |
| **Processing** | 210 | 67% | Gaze signal processing: `FixationDetector`, `FixationCentroidTracker`, `GapInterpolator`, `OneEuroFilter`, `DwellDetector`, `DwellTarget`, `GazeFilterFactory`. |
| **Overlays** | 196 | 81% | WPF overlay windows: cursor circle, activation button, manual selection, action menu, toolbar, keyboard, glass backdrop, calibration, target highlight, **DwellFeedbackOverlay**, eye-care banner/exercise. |
| **Settings** | 188 | 78% | `SettingsService` (JSON persistence + validation), `EyeCtrlSettings` model tree, tab/page builders. |
| **Supervision** | 76 | 77% | Watchdog state machine, `ProcessController`, `HeartbeatFreshnessEvaluator`, `FallbackAtLauncher`/`FallbackCandidate`. |
| **Models** | 71 | 80% | `GazePoint`, `PhysicalPoint`, `DwellTarget`, `EyeCtrlSettings` + sub-settings, `FallbackCandidate`. |
| **Calibration** | 69 | 81% | `CalibrationService`, affine/polynomial 2-D transform, Gaussian-elimination solver, validation. |
| **Views** | 54 | 69% | `SplashWindow`, `SettingsWindow` and tab page builders. |
| **Theme** | 54 | 91% | Theming, `KeyBaseColor`, accent brushes. |
| **Exercises** | 50 | 79% | Eye-care gaze exercises (saccade targets, break routines). |
| **Input** | 46 | 80% | `IInputSimulator` ↔ `Win32InputSimulator`, `MouseCoordinateMapper`, `MouseGazeProvider`. |
| **Gaze** | 40 | 68% | `GazeEngine` orchestrator, `GazeFilterFactory`, `MathConstants`. |

*(plus 33 smaller areas including Diagnostics, Interop, Ipc, Flow, Scoring, Keyboard, Fallback, and the per-project `*.Tests` / `Benchmarks` clusters).*

### Hub Symbols (most-referenced in the graph)

The load-bearing types — these have the most incoming `CALLS` edges (production code only, callers ≥ 27):

| Symbol | File | Kind | Incoming callers |
|--------|------|------|---------------:|
| `PhysicalPoint` | `Core/Models/PhysicalPoint.cs` | Constructor | 384 |
| `EyeCtrlSettings` | `Core/Settings/EyeCtrlSettings.cs` | Class | 195 |
| `GazePoint` | `Core/Models/GazePoint.cs` | Constructor | 89 |
| `DwellTarget` | `Core/Processing/DwellTarget.cs` | Constructor | 65 |
| `DwellDetector.AddTarget` | `Core/Processing/DwellDetector.cs` | Method | 60 |
| `GazeCursorMovedMessage` | `ViewModels/Messages/GazeMessages.cs` | Constructor | 56 |
| `DpiHelper.LogicalToPhysicalX` | `Core/Services/DpiHelper.cs` | Method | 48 |
| `DwellSettings` | `Core/Settings/DwellSettings.cs` | Class | 46 |
| `DwellDetector` (ctor) | `Core/Processing/DwellDetector.cs` | Constructor | 43 |
| `WordPredictor.GetSuggestions` | `Core/Prediction/WordPredictor.cs` | Method | 43 |
| `DwellDetector.Update` | `Core/Processing/DwellDetector.cs` | Method | 41 |
| `SettingsService` (ctor) | `Core/Settings/SettingsService.cs` | Constructor | 37 |
| `GazeEngine` (ctor) | `Core/GazeEngine.cs` | Constructor | 36 |
| `InteractionManager.OnActivationButtonDwelled` | `Core/Processing/InteractionManager.cs` | Method | 33 |
| `InteractionManager.OnFixationStarted` | `Core/Processing/InteractionManager.cs` | Method | 32 |
| `InteractionManager` | `Core/Processing/InteractionManager.cs` | Class | 32 |
| `DwellDetector.ClearTargets` | `Core/Processing/DwellDetector.cs` | Method | 28 |
| `CalibrationService` (ctor) | `Core/Calibration/CalibrationService.cs` | Constructor | 28 |
| `StruggleMonitor` (ctor) | `Core/Adaptation/StruggleMonitor.cs` | Constructor | 27 |

`PhysicalPoint` (384 callers) and `EyeCtrlSettings` (195 callers) are the two universal dependencies — the coordinate type and the configuration aggregate.

---

## 4. Key Execution Flows

GitNexus traced **300** call-chain processes (mostly `cross_community`). The most important entry-to-terminal flows are summarised below. Each narrative description is accompanied by a graph-verified step trace (from `STEP_IN_PROCESS` edges).

### 4.1 Application startup — `App.OnStartup`
`src/EyeCtrl.App/App.xaml.cs:50`

`OnStartup` delegates to extracted helpers for each phase:

1. **`InitializeDpi(splash)`** — sets `DpiHelper` from the splash window's `CompositionTarget.TransformToDevice`. In `--test-mode` (no splash), falls back to `GetDpiForSystem()` Win32 P/Invoke.
2. **`InitializeSettingsAndLogging()`** — resolves `%APPDATA%/EyeCtrl/`, loads `EyeCtrlSettings` via `SettingsService.Load()` (migrates defaults), initializes Serilog → `eyectrl-.log` (rolling, 7-day retention), registers `AppDomain.UnhandledException` and `DispatcherUnhandledException` handlers. Returns `(settings, settingsFilePath)`.
3. `AppBootstrapper.BuildServiceProvider(settings, settingsFilePath, Dispatcher, IsTestMode)` constructs the DI graph.
4. Resolve services: `SettingsService`, `GazeEngine`, `IMessenger`, `IInputSimulator`, `IGazeProvider`. Determine `activeProviderType` via `GazeProviderFactory.GetProviderType`.
5. **`ResolveViewModels(settings)`** — resolves all ViewModels from DI, calls `manualSelectionIndicatorVm.Configure(settings)`.
6. Resolve `GazeEngineAdapter` (subscribes to Core events) and `ManualSelectionDwellService` (subscribes to messages).
7. **`InitializeOverlays(overlayManager, viewModels, settings, providerType)`** — calls `IOverlayManager.Initialize`, returns `(cursorCircle, toolbar)`.
8. `CalibrationService.Load(calibrationPath, screenPx, monitorName)` — uses **physical** screen pixels.
9. Construct `EventCoordinator`, hand it an `EventCoordinationContext` of all participants.
10. `_eventCoordinator.SetModelServices(...)` exposes model registry/download/management for settings UI.
11. `_eventCoordinator.LoadKeyboard(...)` populates the on-screen keyboard from the active layout.
12. Start `TrayIconManager`.
13. **`ShowOverlays(splash, cursorCircle, toolbar, settings)`** — closes splash, shows cursor + toolbar, applies docked/selection-mode state.
14. If `settings.General.StartPaused` → `InteractionManager.TogglePause()`.
15. `gazeEngine.Start()` → `IGazeProvider.Start()` begins emitting samples. `ExternalDwellService.Start()` opens the dwell named-pipe server. `HeartbeatPublisher` begins publishing to `eyectrl_watch`.

**Graph-verified trace** (`OnStartup → ConnectApi`, 6 steps) — the gaze-provider connect path:

| Step | Symbol | File |
|------|--------|------|
| 1 | `App.OnStartup` | `src/EyeCtrl.App/App.xaml.cs` |
| 2 | `AppBootstrapper.BuildServiceProvider` | `src/EyeCtrl.App/Services/AppBootstrapper.cs` |
| 3 | `GazeProviderFactory.Create` | `src/EyeCtrl.Platform/Gaze/GazeProviderFactory.cs` |
| 4 | `GazeProviderFactory.CreateAuto` | `src/EyeCtrl.Platform/Gaze/GazeProviderFactory.cs` |
| 5 | `TobiiStreamEngineProvider` (ctor) | `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs` |
| 6 | `TobiiStreamEngineProvider.ConnectApi` | `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs` |

A sibling flow `OnStartup → ConnectDevice` (6 steps) follows the same path but terminates at `ConnectDevice`. The mouse fallback is `OnStartup → MouseGazeProvider` (5 steps) — used when no Tobii SDK is present or `--test-mode` is passed.

### 4.2 Gaze pipeline — `GazeEngine.OnGazePoint`
`src/EyeCtrl.Core/GazeEngine.cs:82`

```
IGazeProvider ──GazePointReceived──▶ GazeEngine.OnGazePoint
                                          │
                        ┌──────────────────┼──────────────────┐
                        ▼                  ▼                  ▼
               CalibrationService     GapInterpolator   CentroidTracker
               .CurrentTransform.Apply (filter + gap fill)  .Update
                        │                  │
                        └──────┬───────────┘
                               ▼
                     CursorMoved event  ──▶  GazeEngineAdapter
                                                   │
                                                   ▼
                                       InputSimulator.MoveCursor
                                       (FreeLook state only)
                                                   │
                                                   ▼
                                       FixationDetector.Update
                                                   │
                                                   ▼
                               FixationStarted / FixationEnded
                                                   │
                                                   ▼
                                        InteractionManager
                               (Paused / FreeLook / Dwell / …)
```

* Coordinates are always in **physical** screen pixels. Calibration applies a pre-computed 2-D transform (`PhysicalPoint` → `PhysicalPoint`).
* `GapInterpolator` wraps `GazeFilterFactory.Create(settings.Cursor)` (One-Euro or other) and fills short tracker gaps up to `MaxGapInterpolationMs`.
* `SuppressFixation` and the `InteractionState.Paused` short-circuit guard the cursor / dwell path so the toolbar can still receive gaze while paused (allows unpause via dwell).
* Observer fan-out uses copy-on-write handler arrays with per-observer try/catch and `HandlerFaultReporter` (rate-limited fault containment) — a buggy/throwing observer never takes down the gaze thread.

**Graph-verified trace** (`OnGazePoint → PhysicalPoint`, 4 steps):

| Step | Symbol | File |
|------|--------|------|
| 1 | `GazeEngine.OnGazePoint` | `src/EyeCtrl.Core/GazeEngine.cs` |
| 2 | `GapInterpolator.Process` | `src/EyeCtrl.Core/Processing/GapInterpolator.cs` |
| 3 | `SmoothedFilter.Filter` | `src/EyeCtrl.Core/Processing/SmoothedFilter.cs` |
| 4 | `PhysicalPoint` (ctor) | `src/EyeCtrl.Core/Models/PhysicalPoint.cs` |

Related flows terminate at `OneEuroFilter.Alpha` and `LowPassFilter`, showing the adaptive-smoothing branch.

### 4.3 Dwell activation — `DwellDetector.Update`
`src/EyeCtrl.Core/Processing/DwellDetector.cs`

* `DwellTarget`s are registered/removed by overlay windows through the `EventCoordinator` as their visual state changes (`BuildXxxPages` processes in GitNexus show this fan-out).
* `Update(point, timestamp)` is invoked from the UI thread (gaze events are marshaled via `Dispatcher.BeginInvoke` inside `GazeEngineAdapter`).
* Per target: tracks `IsGazeInside`, accumulates dwell time, emits `TargetProgress(id, fraction)` continuously and `TargetActivated(id)` on completion.
* `SetCooldown` (ms) prevents retrigger within a window; tested for thread-safety in `DwellDetectorThreadSafetyTests`.
* `ResetAllState` is called when switching input contexts (modal open/close) to avoid stale `EnteredTimestamp` activations.

### 4.4 Interaction State Machine
`src/EyeCtrl.Core/InteractionManager.cs`

```
FreeLook ──fixation──► Fixation ──activation dwell──► ActionMenuVisible
                                                           │
                        ┌──────────────────────────────────┘
                        ▼
               ManualSelectionActive ──precise dwell──► ManualSelectionActionMenuVisible
                        ▲                                        │
                        └────────── back ────────────────────────┘
                                                           │
                        Paused ◄── toggle pause ───────────┘
```

* **FreeLook** — Cursor follows gaze, fixation detection active.
* **Fixation** — Activation button shown at fixation point.
* **ActionMenuVisible** — Radial action menu (click, scroll, drag) shown.
* **ManualSelectionActive** — Magnified area shown for precise targeting.
* **ManualSelectionActionMenuVisible** — Action menu positioned at precise dwell point.
* **Paused** — All interaction suspended (cursor still follows gaze for toolbar access).

### 4.5 Action Selection
`src/EyeCtrl.App/Services/EventCoordinator.cs`

```
ActionMenuViewModel action dwell
  └── Messenger.Send(ActionRequestedMessage)
        └── EventCoordinator.HandleActionRequested()
              └── IInputSimulator.LeftClick / RightClick / DoubleClick / Drag / Scroll
                    └── Win32InputSimulator → SendInput P/Invoke
```

**Graph-verified trace** (`OnShowActionMenu → PhysicalPoint`, 3 steps) — triggered when the user dwells on the activation button to anchor the radial action menu:

| Step | Symbol | File |
|------|--------|------|
| 1 | `GazeEngineAdapter.OnShowActionMenu` | `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` |
| 2 | `ActionMenuLayoutHelper.CalculateDisplayPoint` | `src/EyeCtrl.ViewModels/Helpers/ActionMenuLayoutHelper.cs` |
| 3 | `PhysicalPoint` (ctor) | `src/EyeCtrl.Core/Models/PhysicalPoint.cs` |

**Graph-verified trace** (`ResolveTargetAsync → ControlInfo`, 5 steps) — when a dwell/fixation occurs, this resolves which on-screen control the user is looking at:

| Step | Symbol | File |
|------|--------|------|
| 1 | `GazeEngineAdapter.ResolveTargetAsync` | `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` |
| 2 | `ControlFinder.FindTarget` | `src/EyeCtrl.Platform/Services/ControlFinder.cs` |
| 3 | `ControlFinder.FindNearestInteractiveControl` | `src/EyeCtrl.Platform/Services/ControlFinder.cs` |
| 4 | `ControlFinder.ResolveInteractiveElement` | `src/EyeCtrl.Platform/Services/ControlFinder.cs` |
| 5 | `ControlInfo` (ctor) | `src/EyeCtrl.Core/Models/ControlInfo.cs` |

### 4.6 Keyboard Prediction
`src/EyeCtrl.ViewModels/KeyboardViewModel.cs`

```
KeyboardViewModel.ProcessKeyGaze(key)
  ├── Append character to typed text
  ├── PredictionOrchestrator.GetSuggestions(context)
  │     ├── WordPredictor.GetSuggestions() → n-gram + fuzzy
  │     ├── SentenceCompletionIndex.Search() → trie prefix
  │     └── ILanguageModelPredictor.Predict() → neural LM (via EyeCtrl.LM.SmallLMPredictor)
  └── Display merged suggestions in prediction bar

[Prediction dwell]
  └── Messenger.Send(PredictionSelectedMessage)
        └── EventCoordinator.HandlePredictionSelected()
              ├── SendKeyPress(Backspace) × deleteCount
              └── SendText(word + " ")
```

`SmallLMPredictor` lives in the `EyeCtrl.LM` project and implements `ILanguageModelPredictor` (defined in `EyeCtrl.Core`). Core never references `EyeCtrl.LM` directly — the App project wires the concrete `SmallLMPredictor` into `PredictionOrchestrator` at startup via the interface seam.

### 4.7 Manual Selection Flow
`src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs`

```
InteractionManager.OnActivationButtonDwelled() [ManualSelection enabled]
  ├── State → ManualSelectionActive
  └── StartManualSelection event
        └── ManualSelectionOverlayViewModel → shows magnified area
              ├── ScreenCaptureService captures region
              └── ManualSelectionGazeIndicatorWindow shows gaze dot

[Precise dwell within magnified area]
  └── ManualSelectionDwellService detects dwell
        └── Messenger.Send(ManualSelectionDwellCompletedMessage)
              └── EventCoordinator → coordinate conversion
                    └── InteractionManager.OnManualSelectionDwell()
                          ├── State → ManualSelectionActionMenuVisible
                          └── ShowActionMenu at precise point
```

### 4.8 External Dwell IPC — `ExternalDwellService`
`src/EyeCtrl.App/Services/ExternalDwellService.cs`

EyeCtrl exposes a named-pipe server (`eyectrl_dwell`) so third-party apps can register UI elements as dwell targets without referencing Core.

```
[Third-party app]                              [EyeCtrl]
Dwell.Enabled="True"                           ExternalDwellService
  │                                               │
  ├─ DwellClient.Start() ──pipe connect──────────► AcceptLoopAsync()
  │                                               │
  ├─ RegisterAdapter(adapter)                     │
  │  ├─ adapter.TryGetScreenRect → SdkRect        │
  │  └─ RegisterMessage{id, rect, hwnd} ────────► HandleMessage(Register)
  │                                               ├─ arbiter.AddOrUpdate()
  │                                               │
  │                          ◄── gaze loop ──── OnCursorMoved()
  │                                               ├─ arbiter.Process(gaze)
  │                                               │  ├─ tracks dwell time
  │                                               │  └─ emits FeedbackId + progress
  │                                               ├─ DwellFeedbackOverlay.ShowAt()
  │                                               ├─ SuppressFixation = true
  │                                               │
  │                          ◄── Activated{id} ── arbiter fires activation
  │                                               ├─ SuppressFixation = false
  │  adapter.Activate()                           │
  │  ├─ UIA Invoke pattern, or                    │
  │  └─ ButtonBase.Command.Execute()              │
```

**Graph-verified trace** (`AcceptLoopAsync → ExternalDwellActiveMessage`, 6 steps) — the server accepting a connection and publishing the activation message:

| Step | Symbol | File |
|------|--------|------|
| 1 | `ExternalDwellService.AcceptLoopAsync` | `src/EyeCtrl.App/Services/ExternalDwellService.cs` |
| 2 | `HandleConnectionAsync` | `src/EyeCtrl.App/Services/ExternalDwellService.cs` |
| 3 | `Dispose` | `src/EyeCtrl.App/Services/ExternalDwellService.cs` |
| 4 | `ReleaseSuppression` | `src/EyeCtrl.App/Services/ExternalDwellService.cs` |
| 5 | `SetCursorRingHidden` | `src/EyeCtrl.App/Services/ExternalDwellService.cs` |
| 6 | `ExternalDwellActiveMessage` | `src/EyeCtrl.ViewModels/Messages/GazeMessages.cs` |

**Key design points:**
- `DwellClient` dedupes outbound rects via `OutboundRectTracker` (register once, update on layout change).
- Per-UI-thread refresh timers (WinUI: `DispatcherQueueTimer` per `ThreadGroup`; WPF: `LayoutUpdated` event) keep rects current.
- `ExternalDwellArbiter` handles occlusion gating (checks `WindowFromPoint` + `GetAncestor`) and cooldown.
- `DwellFeedbackOverlay` is a click-through, DPI-correct WPF window showing dwell-progress rings.
- `SuppressFixation` is shared with `EventCoordinator` — the external dwell service acquires/releases its own contribution only, never clobbering other owners (keyboard, calibration).

### 4.9 Calibration flow — `CalibrationViewModel.CompleteSession`
`src/EyeCtrl.ViewModels/CalibrationViewModel.cs`

1. User triggers calibration (toolbar dwell) → `CalibrationRequestedMessage` published.
2. `CalibrationWindow` overlay opens; gaze samples are collected at known screen positions.
3. `CompleteSession()` validates before fitting:
   - `CalibrationValidator.HasSufficientGazeVariation(points)` — rejects if gaze barely moved (sensor not tracking).
   - `AffineTransform.Fit(points)` — Gaussian elimination for 2-D mapping.
   - `CalibrationValidator.IsTransformSane(transform, w, h)` — rejects degenerate fits that would map gaze off-screen.
   - `CalibrationValidator.MaxResidualPixels` — rejects if residual error exceeds a fraction of the screen diagonal.
4. Result wrapped in a `CalibrationData` and written to `%APPDATA%/EyeCtrl/calibration.json` by `CalibrationService.Save`.
5. On next startup, `CalibrationService.Load` (step 8 of §4.1) restores it; the `CurrentTransform` is consulted on every `OnGazePoint`.

### 4.10 Settings flow — `SettingsWindow`
`src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

* Tab buttons are built once (`BuildTabButtons`).
* Each tab has a `BuildXxxPages` process (Cursor, Keyboard, Toolbar, Theme, Calibration, General, Dwell, Selection, ActionMenu, Layout, QaList) — GitNexus indexed 9–11 such cross-community flows. They all fan out to:
  * `DwellDetector.AddTarget` — every setting row is a dwell-clickable target.
  * `SettingsWindowViewModel.GetActions` / `GetDetector` — bind view-model getters to the row.
* Live edits call into `SettingsService.Save` on exit (`App.OnExit`) and selectively persist via `EyeCtrlSettings.PropertyChanged` subscribers.
* Model registry/download/management services (small-LM) are exposed via `_eventCoordinator.SetModelServices(...)` so the settings UI can list / fetch / delete ONNX models.

**Graph-verified trace** (`Initialize → SettingsTabViewModel`, 6 steps) — the cross-community flow wiring messaging, opening settings, constructing tab VMs:

| Step | Symbol | File |
|------|--------|------|
| 1 | `EventCoordinator.Initialize` | `src/EyeCtrl.App/Services/EventCoordinator.cs` |
| 2 | `EventCoordinator.SetupMessageHandlers` | `src/EyeCtrl.App/Services/EventCoordinator.cs` |
| 3 | `EventCoordinator.ShowSettingsWindow` | `src/EyeCtrl.App/Services/EventCoordinator.cs` |
| 4 | `SettingsViewModel` (ctor) | `src/EyeCtrl.ViewModels/SettingsViewModel.cs` |
| 5 | `SettingsViewModel.BuildTabs` | `src/EyeCtrl.ViewModels/SettingsViewModel.cs` |
| 6 | `SettingsTabViewModel` (ctor) | `src/EyeCtrl.ViewModels/SettingsTabViewModel.cs` |

Sibling `Initialize → DwellDetector` (5 steps) follows the same path through `SettingsWindowViewModel` and terminates at `DwellDetector`, showing the gaze/dwell wiring during init. The `BuildXxxPages` family shares a common 6–7 step shape verified by the graph: `SettingsWindow.BuildXxxPages` → `SettingsWindowViewModel.GetDetector` / `GetActions` → `DwellDetector.AddTarget` / `DwellTarget` ctor / `DwellControl.CreateRoot`.

### 4.11 Supervisor watchdog & fallback — `OnHeartbeatReceived → FallbackCandidate`
`src/EyeCtrl.Supervisor/Program.cs`

The `EyeCtrl.Supervisor` is a separate, single-instance-guarded WPF process (`SupervisorApp`). Its `HeartbeatPipeServer` listens on `eyectrl_watch`; `HeartbeatFreshnessEvaluator` decides when the App has stalled. On a miss the state machine falls back through `FallbackAtLauncher`, which builds and launches a `FallbackCandidate` (a replacement App instance).

| Step | Symbol | File |
|------|--------|------|
| 1 | `Program.OnHeartbeatReceived` | `src/EyeCtrl.Supervisor/Program.cs` |
| 2 | `HandleInput` | `src/EyeCtrl.Supervisor/Program.cs` |
| 3 | `OnFallenBack` | `src/EyeCtrl.Supervisor/Program.cs` |
| 4 | `FallbackAtLauncher.BuildCandidates` | `src/EyeCtrl.Supervisor/Fallback/FallbackAtLauncher.cs` |
| 5 | `CreateCandidate` | `src/EyeCtrl.Supervisor/Fallback/FallbackAtLauncher.cs` |
| 6 | `FallbackCandidate` | `src/EyeCtrl.Supervisor/Fallback/FallbackCandidate.cs` |

The App side of this contract is `HeartbeatPublisher` (in `EyeCtrl.App`), which writes heartbeats to `eyectrl_watch`. A retried instance can be launched `--paused` so it is inert (no `SendInput`) without mutating the user's persisted `StartPaused` setting.

### 4.12 Scroll mode → dwell target registration — `OnInteractionStateChanged → AddTarget`
`src/EyeCtrl.App/Services/ScrollModeController.cs` → `Core/Processing/DwellDetector.cs`

When interaction state changes to scroll mode, edge buttons appear and register themselves as dwell targets in the Core `DwellDetector`. A clean cross-layer example: **App service → App overlay window → ViewModel → Core**.

| Step | Symbol | File |
|------|--------|------|
| 1 | `ScrollModeController.OnInteractionStateChanged` | `src/EyeCtrl.App/Services/ScrollModeController.cs` |
| 2 | `EnterScrollMode` | `src/EyeCtrl.App/Services/ScrollModeController.cs` |
| 3 | `EdgeButtonBarWindow.ShowButtons` | `src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs` |
| 4 | `EdgeButtonBarViewModel.ShowButtons` | `src/EyeCtrl.ViewModels/EdgeButtonBarViewModel.cs` |
| 5 | `EdgeButtonBarViewModel.AddTarget` | `src/EyeCtrl.ViewModels/EdgeButtonBarViewModel.cs` |
| 6 | `DwellDetector.AddTarget` | `src/EyeCtrl.Core/Processing/DwellDetector.cs` |

### 4.13 Eye Care
`src/EyeCtrl.EyeCare/`

A leaf subsystem that subscribes to gaze samples from Core. `FatigueMonitor` runs an O(window) scan over recent samples to detect strain; the `Scheduler` drives break/exercise cadence; `Exercises` (saccade targets, etc.) and `Scoring` quantify the session. The App's `AppBootstrapper.BuildExercisePool` / `BuildSaccadeTargets` construct the exercise pool, and eye-care banners/exercise overlays (`EyeCareBannerWindow`, `EyeCareExerciseWindow`) surface prompts. The `OnBreakDue → PhysicalPoint` flow (5 steps) shows a break becoming a screen point for the exercise UI.

---

## 5. Cross-Cutting Concerns

| Concern | Where |
|---|---|
| **DI** | `AppBootstrapper.BuildServiceProvider` — `Microsoft.Extensions.DependencyInjection`. All ViewModels and services are singletons. |
| **Pub/sub** | `CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default` — used by ViewModels to decouple (e.g., `CalibrationRequestedMessage`, selection-mode toggles, model-status updates, `ExternalDwellActiveMessage`). Core does **not** use the messenger (it uses `INotifyPropertyChanged` + direct events). |
| **Logging** | Serilog (file + `ILogger<T>`). Initialised in `App.OnStartup`, flushed on `OnExit`. |
| **Settings persistence** | `SettingsService` (JSON, validated on load). Sub-settings (`Cursor`, `Dwell`, `Toolbar`, `Keyboard`, `Calibration`, `General`, `Selection`, `ActionMenu`, `Model`, `ExternalDwell`) are nested under `EyeCtrlSettings`. |
| **DPI** | `DpiHelper` initialised once from `CompositionTarget.TransformToDevice`; physical-vs-logical conversion is centralised. |
| **Input simulation** | `IInputSimulator` → `Win32InputSimulator` (`SendInput`). Gaze is *never* synthesised by writing the mouse position from the gaze thread; cursor move is dispatched on the UI thread. |
| **Threading** | `DwellDetector` and most stateful services are designed for UI-thread use; `GazeEngineAdapter` marshals events via `Dispatcher.BeginInvoke`. |
| **Hardware abstraction** | `IGazeProvider`; concrete: `TobiiStreamEngineProvider` (conditional) and `MouseGazeProvider` (test-mode fallback). Selected by `GazeProviderFactory` based on `settings.General.GazeProviderType`. |
| **Resource files** | JSON layouts, dictionaries, emojis, `click.wav` use `<ExcludeFromSingleFile>true</ExcludeFromSingleFile>` — they ship alongside the single-file exe. |
| **Named-pipe IPC (dwell)** | `ExternalDwellService` (server, App) ↔ `DwellClient` (client, SDK). Byte-mode, newline-framed JSON via `eyectrl_dwell`. Wire format lives in `EyeCtrl.Contracts` so the SDK never references Core. |
| **Named-pipe IPC (watchdog)** | `HeartbeatPublisher` (App) → `HeartbeatPipeServer` (Supervisor) via `eyectrl_watch`. The supervisor is the resilience layer; it relaunches/falls back if heartbeats go stale. |
| **Single-instance** | `SingleInstanceGuard` (App, `Local\EyeCtrl.App.Singleton`) and a separate guard in the Supervisor (`Local\EyeCtrl.Supervisor.Singleton`). |
| **CI** | GitHub Actions runs on `push` to `main` and `pull_request` targeting `main`. `build-and-test` job builds and runs `dotnet test --filter "Category!=UI"`. UI tests (FlaUI, `Category=UI`) are excluded — they need an interactive desktop. |

---

## 6. Mermaid Architecture Diagram

```mermaid
flowchart TB
    %% ───────── Hardware / OS ─────────
    subgraph HW["Hardware & OS"]
        TOBII["Tobii Eye Tracker<br/>(Stream Engine DLLs)"]
        MOUSE["System Mouse<br/>(fallback / test-mode)"]
        WIN32["Win32<br/>user32 / SendInput"]
    end

    %% ───────── Platform layer ─────────
    subgraph PLAT["EyeCtrl.Platform  (net10.0-windows)"]
        TOBIIPROV{{"TobiiStreamEngineProvider<br/>(TOBII_AVAILABLE)"}}
        MOUSEGAZE{{"MouseGazeProvider"}}
        INPUTSIM{{"IInputSimulator"}}
        WIN32SIM["Win32InputSimulator<br/>unsafe SendInput"]
        MAPPER["MouseCoordinateMapper<br/>(DPI-aware)"]
        FACTORY["GazeProviderFactory"]
        CF["ControlFinder<br/>(IControlFinder)"]
    end

    %% ───────── Core layer ─────────
    subgraph CORE["EyeCtrl.Core  (framework-agnostic)"]
        ENGINE["GazeEngine<br/>+ InteractionManager"]
        FILTFAC["GazeFilterFactory<br/>(One-Euro, …)"]
        GAP["GapInterpolator"]
        FIX["FixationDetector"]
        CENT["FixationCentroidTracker"]
        DWELL["DwellDetector<br/>+ DwellTarget"]
        ADAPT["Adaptation<br/>StruggleMonitor / AdaptationController"]
        CAL["CalibrationService<br/>Transform  /  Gaussian"]
        SETTINGS["SettingsService<br/>EyeCtrlSettings  +  subs"]
        PRED{{"PredictionOrchestrator"}}
        WORDP["WordPredictor"]
        BIGRAM["BigramModel"]
        PHRASE["PhraseModel"]
        SENT["SentenceCompletionIndex"]
        FUZZY["FuzzyEditDistance"]
        CAPS["CapitalizationHelper"]
        LM{{"SmallLMPredictor<br/>(ONNX, optional)"}}
        KBD["Keyboard layouts / model"]
    end

    %% ───────── Leaf subsystems (ref Core only) ─────────
    subgraph LEAF["Leaf subsystems"]
        EYECARE["EyeCtrl.EyeCare<br/>Scheduler / FatigueMonitor / Exercises / Scoring"]
        LMBOX["EyeCtrl.LM<br/>SmallLMPredictor + LMTokenizer"]
    end

    %% ───────── ViewModels ─────────
    subgraph VM["EyeCtrl.ViewModels"]
        CURSORVM["CursorCircleViewModel"]
        ACTVM["ActivationButtonViewModel"]
        MSOVM["ManualSelectionOverlayViewModel"]
        ACTMVM["ActionMenuViewModel"]
        TOOLVM["ToolbarViewModel"]
        KBDVM["KeyboardViewModel"]
        CALVM["CalibrationViewModel"]
        SETVM["SettingsViewModel"]
        EDGEVM["EdgeButtonBarViewModel"]
    end

    %% ───────── App layer ─────────
    subgraph APP["EyeCtrl.App  (WPF entry)"]
        APPCL["App.OnStartup / OnExit"]
        BOOT["AppBootstrapper<br/>(DI graph)"]
        EVT["EventCoordinator<br/>(+ EventCoordinationContext)"]
        ADAPT2["GazeEngineAdapter<br/>Core events → IMessenger"]
        OVM["OverlayManager<br/>(IOverlayManager)"]
        MSGI["ManualSelectionDwellService"]
        EXTDWELL["ExternalDwellService<br/>(named-pipe server)"]
        ARBITER["ExternalDwellArbiter<br/>(occlusion + cooldown)"]
        FEEDBACK["DwellFeedbackOverlay<br/>(click-through)"]
        HBPUB["HeartbeatPublisher<br/>(watchdog-pipe client)"]
        OV_CUR["CursorCircle window"]
        OV_ACT["ActivationButton window"]
        OV_MSO["ManualSelectionOverlay window"]
        OV_AM["ActionMenu window"]
        OV_TB["Toolbar window"]
        OV_KB["Keyboard window"]
        OV_CAL["Calibration window"]
        OV_BG["GlassBackdrop window"]
        OV_HL["TargetHighlight window"]
        SETW["SettingsWindow<br/>(tab page builders)"]
        TRAY["TrayIconManager"]
        SPLASH["SplashWindow"]
    end

    %% ───────── Supervisor (separate process) ─────────
    subgraph SUP["EyeCtrl.Supervisor  (watchdog process)"]
        SUPAPP["SupervisorApp / Program"]
        STATE["SupervisionStateMachine"]
        HBPIPE["HeartbeatPipeServer"]
        FRESH["HeartbeatFreshnessEvaluator"]
        FBL["FallbackAtLauncher"]
        FBC["FallbackCandidate"]
    end

    %% ───────── SDK layer ─────────
    subgraph SDK["SDK  (third-party integration)"]
        direction TB
        CONTRACTS["EyeCtrl.Contracts<br/>WireMessages / SdkRect / DwellPipe"]
        ABSTRACTIONS["EyeCtrl.Sdk.Abstractions<br/>DwellClient / IDwellElementAdapter"]
        SDKWPF["EyeCtrl.Sdk.Wpf<br/>Dwell attached properties"]
        SDKWINUI["EyeCtrl.Sdk.WinUI<br/>Dwell attached properties"]
    end

    %% ───────── External infra ─────────
    subgraph EXT["External"]
        FILES[("%APPDATA%/EyeCtrl/<br/>settings.json<br/>calibration.json<br/>user-dict.json<br/>models/")]
        LOGS[("%APPDATA%/EyeCtrl/logs/<br/>eyectrl-.log")]
        DICT[("bundled dictionaries<br/>+ emoji + click.wav")]
        ONNX[("ONNX LM model<br/>(user-supplied)")]
        HTTP(["GitHub Releases<br/>(model download)"])
        PIPEDWELL[("Named Pipe<br/>eyectrl_dwell")]
        PIPEWATCH[("Named Pipe<br/>eyectrl_watch")]
    end

    %% ── edges: hardware → platform ──
    TOBII --> TOBIIPROV
    MOUSE --> MOUSEGAZE
    FACTORY --> TOBIIPROV
    FACTORY --> MOUSEGAZE

    %% ── DI wiring ──
    BOOT -. registers .-> ENGINE
    BOOT -. registers .-> ADAPT2
    BOOT -. registers .-> OVM
    BOOT -. registers .-> VM
    BOOT -. builds .-> EYECARE

    %% ── gaze source → engine ──
    TOBIIPROV -- GazePointReceived --> ENGINE
    MOUSEGAZE  -- GazePointReceived --> ENGINE

    %% ── engine internals ──
    ENGINE --> FILTFAC
    ENGINE --> GAP
    ENGINE --> FIX
    ENGINE --> CENT
    ENGINE --> CAL
    ENGINE --> DWELL
    ENGINE --> ADAPT
    ENGINE --> INPUTSIM
    INPUTSIM --> WIN32SIM
    WIN32SIM --> WIN32
    MAPPER --> WIN32SIM

    %% ── leaf subsystems ──
    EYECARE -. subscribes gaze .-> ENGINE
    LMBOX -. implements ILanguageModelPredictor .-> PRED
    LMBOX -. reads .-> ONNX

    %% ── prediction ──
    PRED --> WORDP
    PRED --> BIGRAM
    PRED --> PHRASE
    PRED --> SENT
    PRED --> FUZZY
    PRED --> CAPS
    PRED -. optional .-> LM
    KBDVM --> KBD

    %% ── adapter + messenger ──
    ADAPT2 -- subscribes --> ENGINE
    ADAPT2 -- publishes IMessenger --> CURSORVM
    ADAPT2 -- publishes IMessenger --> MSOVM
    ADAPT2 -- publishes IMessenger --> CALVM
    ADAPT2 -- publishes IMessenger --> SETVM
    ADAPT2 --> CF

    %% ── EventCoordinator wiring ──
    EVT -- subscribes --> CURSORVM
    EVT -- subscribes --> ACTVM
    EVT -- subscribes --> MSOVM
    EVT -- subscribes --> ACTMVM
    EVT -- subscribes --> TOOLVM
    EVT -- subscribes --> KBDVM
    EVT -- subscribes --> CALVM
    EVT -- subscribes --> SETVM
    EVT -- AddTarget / RemoveTarget --> DWELL
    EVT -- Save / Load --> SETTINGS
    EVT -- WordPredictor --> PRED
    MSGI -- subscribes IMessenger --> MSOVM
    MSGI --> DWELL

    %% ── external dwell IPC ──
    EXTDWELL -- CursorMoved --> ENGINE
    EXTDWELL --> ARBITER
    EXTDWELL --> FEEDBACK
    EXTDWELL -- SuppressFixation --> ENGINE
    EXTDWELL -- ExternalDwellActiveMessage --> CURSORVM

    %% ── overlays ──
    OVM --> OV_CUR
    OVM --> OV_ACT
    OVM --> OV_MSO
    OVM --> OV_AM
    OVM --> OV_TB
    OVM --> OV_KB
    OVM --> OV_CAL
    OVM --> OV_BG
    OVM --> OV_HL

    OV_TB  -- bound to --> TOOLVM
    OV_CUR -- bound to --> CURSORVM
    OV_ACT -- bound to --> ACTVM
    OV_MSO -- bound to --> MSOVM
    OV_AM  -- bound to --> ACTMVM
    OV_KB  -- bound to --> KBDVM
    OV_CAL -- bound to --> CALVM
    SETW   -- bound to --> SETVM
    SETW   -- dwell targets --> DWELL
    SETW   -- GetActions --> SETVM
    SPLASH --> APPCL
    TRAY   --> APPCL

    %% ── watchdog IPC ──
    HBPUB -- heartbeat --> PIPEWATCH
    PIPEWATCH -- heartbeat --> HBPIPE
    HBPIPE --> FRESH
    FRESH --> STATE
    STATE -- stale --> FBL
    FBL --> FBC
    FBC -. relaunch --paused .-> APPCL
    SUPAPP --> STATE

    %% ── SDK IPC ──
    CONTRACTS --> ABSTRACTIONS
    ABSTRACTIONS --> SDKWPF
    ABSTRACTIONS --> SDKWINUI
    SDKWPF -- RegisterAdapter --> PIPEDWELL
    SDKWINUI -- RegisterAdapter --> PIPEDWELL
    PIPEDWELL -- WireMessages --> EXTDWELL

    %% ── persistence ──
    SETTINGS -. read / write .-> FILES
    CAL     -. read / write .-> FILES
    PRED    -. user dict .-> FILES
    SETTINGS -. loads .-> DICT
    KBD     -. loads .-> DICT
    APPCL   -. logs .-> LOGS
    APPCL   -. HTTP GET .-> HTTP

    classDef optional stroke-dasharray: 4 3,opacity:0.85;
    classDef proc stroke:#7a3,stroke-width:2px;
    class TOBIIPROV,LM,ONNX,LMBOX optional;
    class SUP,APP proc;
```

---

## 7. Architectural Invariants

1. **Physical pixels everywhere in Core.** All gaze data inside `EyeCtrl.Core` is in physical screen pixels. `DpiHelper` (in `EyeCtrl.Core.Services`) is initialised once at startup from `CompositionTarget.TransformToDevice` in `App.xaml.cs`; all Core code uses it for physical ↔ logical conversion. Calibration operates in the same physical space.
2. **UI-thread serialisation.** Dwell/focus/progress state lives on the UI thread. `GazeEngineAdapter` is the single point that marshals Core events onto the dispatcher.
3. **Conditional hardware dependency.** `TOBII_AVAILABLE` is defined only when the Tobii SDK DLLs are on disk. The build degrades gracefully (mouse fallback) and the symbol is excluded from the assembly.
4. **Singleton DI lifetime.** ViewModels and services are singletons; their constructors register `IMessenger` handlers. Resolving a ViewModel *is* the act of subscribing.
5. **No mocking library in tests.** Tests use private nested fakes (`FakeGazeProvider`, `FakeInputSimulator`). Thread-safety is verified with explicit `DwellDetectorThreadSafetyTests` rather than a concurrency testing framework.
6. **Settings drive behaviour.** Almost every tunable (filter mode, dwell radius, calibration, provider type, model selection) lives in `EyeCtrlSettings` and is re-read on `PropertyChanged`; the engine rebuilds its filter chain live (see `GazeEngineFilterRecreationTests`).
7. **CommunityToolkit.Mvvm in Core is a source-generator utility, not a UI dependency.** `ObservableObject` + `[ObservableProperty]` provide `INotifyPropertyChanged` for settings objects (~80 properties). `GazeEngine` subscribes to `CursorSettings.PropertyChanged` to rebuild the filter chain live. The library emits C# source only — no runtime UI dependency. This is distinct from the `WeakReferenceMessenger` used in ViewModels (which Core does *not* use).
8. **LM is behind an interface seam.** `ILanguageModelPredictor` lives in `EyeCtrl.Core`; `SmallLMPredictor` + `LMTokenizer` live in `EyeCtrl.LM`. Core never references `EyeCtrl.LM` — the App project wires the concrete implementation at startup. This keeps Core free of ONNX native dependencies.
9. **EyeCare is a leaf subscriber.** `EyeCtrl.EyeCare` references `Core` only and observes gaze samples; Core never references EyeCare. Eye-care overlays live in the App.
10. **The Supervisor is a separate process sharing only Contracts/Core types.** It never references `EyeCtrl.App`. Resilience (heartbeat watchdog + fallback launch) is decoupled from the application it guards.
11. **IPC pipe names are machine-global and must be unique.** Production names live in constants (`EyeCtrl.Contracts.DwellPipe.Name` = `eyectrl_dwell`; the watchdog channel = `eyectrl_watch`). Tests inject GUID-suffixed names to avoid cross-worktree/instance collisions (see `AGENTS.md`).

---

## 8. Key Design Patterns

| Pattern | Location | Purpose |
|---------|----------|---------|
| **DI / IoC** | `AppBootstrapper.cs` | Wire all services, ViewModels, and providers |
| **MVVM + Messenger** | `EyeCtrl.ViewModels` | `WeakReferenceMessenger` for decoupled VM communication |
| **Factory** | `GazeProviderFactory.cs` | Select gaze provider at runtime |
| **Strategy** | `GazeFilterFactory.cs` | Select filter (One-Euro, Direct, Smoothed) per settings |
| **Observer** | `EventCoordinator.cs` | PropertyChanged handlers on all overlay VMs |
| **Adapter** | `GazeEngineAdapter.cs` | Bridge between Core engine and App layer |
| **Observer (fan-out)** | `GazeEngine` | Copy-on-write handler arrays with `HandlerFaultReporter` fault containment |
| **State machine** | `InteractionManager` / `SupervisionStateMachine` | Interaction modes (App) / watchdog phases (Supervisor) |
| **IPC / Bridge** | `ExternalDwellService` + `DwellClient` | Named-pipe IPC between EyeCtrl and third-party SDK consumers |
| **Heartbeat / Watchdog** | `HeartbeatPublisher` + `HeartbeatPipeServer` | Supervised lifecycle: detect stall, launch fallback candidate |
| **Interface seam** | `ILanguageModelPredictor` | Keep ONNX LM out of Core |
| **Attached Properties** | `Sdk.Wpf.Dwell` / `Sdk.WinUI.Dwell` | Declarative `Dwell.Enabled="True"` on any `FrameworkElement` |

---

## 9. Where to Start Reading

| Goal | Start here |
|---|---|
| Understand the runtime pipeline | `src/EyeCtrl.App/App.xaml.cs` (`OnStartup` → `InitializeDpi` → `InitializeSettingsAndLogging` → `AppBootstrapper.BuildServiceProvider`) → `src/EyeCtrl.Core/GazeEngine.cs` |
| See how events flow to overlays | `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` → `src/EyeCtrl.App/Services/EventCoordinator.cs` |
| Tweak dwell / fixation | `src/EyeCtrl.Core/Processing/DwellDetector.cs` and `src/EyeCtrl.Core/Processing/FixationDetector.cs` |
| Understand adaptation | `src/EyeCtrl.Core/Adaptation/` (`StruggleMonitor`, `AdaptationController`) |
| Understand eye care | `src/EyeCtrl.EyeCare/` (Scheduler, FatigueMonitor, Exercises) |
| Understand the supervisor | `src/EyeCtrl.Supervisor/Program.cs` → `Supervision/` → `Fallback/` |
| Add a new gaze provider | Implement `IGazeProvider` in `EyeCtrl.Platform/Gaze` and register it in `GazeProviderFactory` |
| Add a new keyboard layout | Drop a JSON layout into `Resources/KeyboardLayouts/`, extend the layout enum, no code change required to load |
| Add a new settings tab | `SettingsWindow.xaml.cs` (`BuildXxxPages`) + a new sub-model in `EyeCtrlSettings` |
| Improve word prediction | `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs` orchestrates the existing models; new strategies plug in alongside `BigramModel`, `PhraseModel`, `SmallLMPredictor` |
| Add a new LM backend | Implement `ILanguageModelPredictor` (in `EyeCtrl.Core/Prediction/`), wire it in `AppBootstrapper`. `SmallLMPredictor` in `EyeCtrl.LM` is the reference implementation. |
| Add dwell to a WPF app | `src/EyeCtrl.Sdk.Wpf/Dwell.cs` — set `Dwell.Enabled="True"` on any `FrameworkElement` |
| Add dwell to a WinUI 3 app | `src/EyeCtrl.Sdk.WinUI/Dwell.cs` — same attached-property API; see `samples/EyeCtrl.Sdk.WinUI.Sample/` |
| Understand the IPC protocol | `src/EyeCtrl.Contracts/WireMessages.cs` (wire format) → `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs` (client) → `src/EyeCtrl.App/Services/ExternalDwellService.cs` (server) |

---

## 10. Directory Structure

```
eyectrl/
├── src/
│   ├── EyeCtrl.Core/            # Domain logic (framework-agnostic)
│   │   ├── GazeEngine.cs        # Central gaze pipeline
│   │   ├── Processing/          # DwellDetector, filters, fixation tracking
│   │   ├── Prediction/          # Word prediction, LM interface, dictionaries
│   │   ├── Adaptation/          # StruggleMonitor, AdaptationController
│   │   ├── Calibration/         # Calibration transform + validation
│   │   ├── Settings/            # Configuration models, validation, SettingsService
│   │   ├── Services/            # DpiHelper, SingleInstanceGuard
│   │   ├── Models/              # GazePoint, PhysicalPoint, etc.
│   │   ├── Keyboard/            # Keyboard layout definitions
│   │   ├── Interfaces/          # Core interfaces (IGazeProvider, IInputSimulator, ILanguageModelPredictor)
│   │   ├── Diagnostics/         # HandlerFaultReporter (observer fault containment)
│   │   └── Enums/               # Enumerations
│   ├── EyeCtrl.Platform/       # OS/SDK bridge
│   │   ├── Gaze/               # GazeProviderFactory, Tobii, Mouse providers
│   │   ├── Input/              # Win32 SendInput wrappers
│   │   └── Services/           # Win32WindowManager, ControlFinder, etc.
│   ├── EyeCtrl.ViewModels/     # MVVM layer
│   │   ├── Messages/           # Messenger message types
│   │   └── Helpers/            # ViewModel utilities
│   ├── EyeCtrl.App/            # WPF application
│   │   ├── App.xaml.cs         # Entry point, overlay lifecycle
│   │   ├── Services/           # EventCoordinator, AppBootstrapper, GazeEngineAdapter,
│   │   │                       #   ExternalDwellService, HeartbeatPublisher, ScrollModeController
│   │   ├── Overlays/           # Transparent overlay windows + DwellFeedbackOverlay + eye-care windows
│   │   ├── Views/              # Settings window & other views
│   │   ├── Controls/           # WPF custom controls (DwellControl)
│   │   └── Assets/             # Resources, sounds, dictionaries
│   ├── EyeCtrl.Controls/       # Reusable WPF controls
│   ├── EyeCtrl.LM/              # ONNX neural LM
│   │   ├── SmallLMPredictor.cs  # ONNX inference via Microsoft.ML.OnnxRuntime
│   │   └── LMTokenizer.cs       # Tokenizer wrapper via Tokenizers.DotNet
│   ├── EyeCtrl.EyeCare/         # Eye-care: Scheduler, FatigueMonitor, Exercises, Scoring
│   ├── EyeCtrl.Supervisor/      # Watchdog process
│   │   ├── Program.cs           # Main, single-instance guard
│   │   ├── Supervision/         # State machine, ProcessController, HeartbeatFreshnessEvaluator
│   │   ├── Fallback/            # FallbackAtLauncher, FallbackCandidate
│   │   ├── Ipc/                 # HeartbeatPipeServer
│   │   └── UI/                  # Tray icon, recovery dialog
│   ├── EyeCtrl.Contracts/      # Shared wire format (SDK ↔ App IPC)
│   │   ├── WireMessages.cs     # Register/Update/Unregister/Activated records
│   │   ├── SdkRect.cs          # Physical-px rect for wire transport
│   │   └── DwellPipe.cs        # Pipe name constant (eyectrl_dwell)
│   ├── EyeCtrl.Sdk.Abstractions/  # Framework-agnostic SDK client
│   │   ├── DwellClient.cs      # Named-pipe client (connect, reconnect, send rects)
│   │   ├── IDwellElementAdapter.cs  # Per-element seam (rect, hwnd, activate)
│   │   └── OutboundRectTracker.cs   # Dedup register/update messages
│   ├── EyeCtrl.Sdk.Wpf/       # WPF dwell SDK
│   │   ├── Dwell.cs            # Attached properties (Enabled, Mode)
│   │   └── WpfElementAdapter.cs  # WPF → IDwellElementAdapter bridge
│   └── EyeCtrl.Sdk.WinUI/     # WinUI 3 dwell SDK
│       ├── Dwell.cs            # Attached properties + per-dispatcher ThreadGroup
│       ├── WinUiElementAdapter.cs  # WinUI → IDwellElementAdapter bridge
│       ├── WinUiCoords.cs      # DIP → physical-px conversion
│       ├── AdapterGroupTracker.cs  # Lifecycle tracking for per-UI-thread timers
│       └── EyeCtrlDwell.cs     # Explicit HWND fallback
├── tests/                      # 11 test projects mirroring src (xUnit v3 + NFluent)
├── samples/
│   ├── EyeCtrl.Sdk.Wpf.Sample/    # WPF sample app with dwell-enabled controls
│   ├── EyeCtrl.Sdk.WinUI.Sample/  # WinUI 3 sample app with dwell-enabled controls
│   └── StreamEngineGazeTracker/    # Tobii SDK sample
└── benchmarks/
    └── EyeCtrl.Benchmarks/     # BenchmarkDotNet benchmarks (standalone, not in .slnx)
```

---

## 11. Index Provenance

| Field | Value |
|-------|-------|
| Repository | `eyectrl` (`C:\Users\andre\source\repos\eyectrl`) |
| Solution | `EyeCtrl.slnx` (new .NET solution format), SDK .NET 10.0.102 (`global.json`) |
| Files / symbols / flows | 744 / 9053 / 300 (indexed snapshot) |
| Functional areas (communities) | 47 (top 20 listed in §3) |
| Index staleness | ⚠️ 142 commits behind HEAD at generation time |
| Embeddings | disabled (BM25 + structural only; FTS indexes were missing at query time — `gitnexus analyze --repair-fts` to rebuild) |

To refresh this document after code changes, re-index (`node .gitnexus/run.cjs analyze` or `npx gitnexus analyze`) and regenerate. Check staleness with `gitnexus status`. The graph-verified step traces in §4 are derived from `STEP_IN_PROCESS` edges; the hub symbol table in §3 from `CALLS` edge aggregation; the cross-project import weights in §1 from `IMPORTS` edges.
