# EyeCtrl Architecture

> Generated from the GitNexus knowledge graph: **5,021 symbols**, **11,207 relationships**, **264 execution flows** across **173 functional clusters**.

EyeCtrl is a gaze-controlled desktop interaction system built on .NET 10 / WPF. It replaces the physical mouse with eye tracking, using dwell-based selection to perform clicks, scrolls, drags, and keyboard input.

## Functional Areas

The knowledge graph auto-detected 14 functional areas (communities) via Leiden clustering. Tests and fixtures excluded.

| Area | Symbols | Description |
|------|---------|-------------|
| **Prediction** | 229 | Word prediction engine: n-gram + bigram models, fuzzy edit distance, language detection, capitalization |
| **Processing** | 153 | Core gaze pipeline: filters (OneEuro, EMA), fixation/dwell detection, centroid tracking, state machine, gap interpolation |
| **Settings** | 98 | JSON-persisted settings hierarchy: 9 sub-settings objects with range validation and camelCase serialization |
| **Overlays** | 104 | WPF overlay windows: cursor circle, activation button, action menu, manual selection (magnified preview), toolbar, keyboard, glass backdrop |
| **Services** | 55 | App-layer services: EventCoordinator (message → overlay routing), GazeEngineAdapter (Core ↔ ViewModel bridge), AppBootstrapper (DI), ManualSelectionDwellService |
| **EyeCtrl.ViewModels** | 37 | MVVM ViewModels (CommunityToolkit.Mvvm): CursorCircleVM, ActivationButtonVM, ManualSelectionOverlayVM, ActionMenuVM, ToolbarVM, KeyboardVM, SettingsVM |
| **Views** | 32 | Settings window UI (dwell-friendly controls), toolbar window, tray icon, splash screen |
| **Models** | 27 | Core value types: PhysicalPoint, LogicalPoint, GazePoint, NormalizedPoint, InteractionAction, ViewPosition, InteractionState |
| **Controls** | 27 | Custom dwell-friendly WPF controls: DwellButton, DwellToggle, DwellSlider, DwellComboBox, DwellColorPicker, ColorBlend |
| **Gaze** | 25 | Eye tracker providers: IGazeProvider interface, TobiiStreamEngineProvider (conditional), MouseGazeProvider (fallback) |
| **Keyboard** | 22 | JSON keyboard layouts (QWERTY, AZERTY, etc.), key definitions, layout loader, emoji data |
| **Input** | 12 | Win32 P/Invoke: SendInput (mouse clicks, keystrokes, scroll), coordinate normalization, IMouseCoordinateMapper |
| **EyeCtrl.App** | 5 | Application entry point: App.xaml startup, splash screen lifecycle |
| **Interfaces** | 10 | Core abstractions: IGazeProvider, IInputSimulator, IGazeFilter, IDictionaryProvider, IDwellRegistrar, IValidatable, IMouseCoordinateMapper, IEventCoordinator, IOverlayManager, IDwellComposite |

## Interface Inventory

All interfaces discovered by the knowledge graph:

| Interface | Project | File | Purpose |
|-----------|---------|------|---------|
| `IGazeProvider` | Core | `src/EyeCtrl.Core/Interfaces/IGazeProvider.cs` | Abstraction for eye tracker data sources |
| `IGazeFilter` | Core | `src/EyeCtrl.Core/Interfaces/IGazeFilter.cs` | Pluggable gaze data filtering |
| `IInputSimulator` | Core | `src/EyeCtrl.Core/Interfaces/IInputSimulator.cs` | Abstraction for mouse/keyboard simulation |
| `IDictionaryProvider` | Core | `src/EyeCtrl.Core/Prediction/IDictionaryProvider.cs` | Word frequency dictionary loading |
| `IDwellRegistrar` | Core | `src/EyeCtrl.Core/Processing/IDwellRegistrar.cs` | Dwell target registration interface |
| `IValidatable` | Core | `src/EyeCtrl.Core/Settings/IValidatable.cs` | Settings validation contract |
| `IMouseCoordinateMapper` | Platform | `src/EyeCtrl.Platform/Input/IMouseCoordinateMapper.cs` | Logical ↔ physical coordinate mapping |
| `IEventCoordinator` | App | `src/EyeCtrl.App/Services/IEventCoordinator.cs` | Message routing and event coordination |
| `IOverlayManager` | App | `src/EyeCtrl.App/Services/IOverlayManager.cs` | Overlay window lifecycle management |
| `IDwellComposite` | App | `src/EyeCtrl.App/Controls/IDwellComposite.cs` | Composite dwell control contract |

## Inter-Area Call Graph

```mermaid
graph LR
    %% Node styling
    classDef core fill:#4a90d9,color:white
    classDef platform fill:#50b050,color:white
    classDef app fill:#d94a4a,color:white
    classDef vm fill:#e09030,color:white
    classDef ui fill:#9060c0,color:white
    classDef models fill:#60b0b0,color:white

    %% Core layer
    Models("Models<br/>(27 symbols)")
    Interfaces("Interfaces<br/>(10 symbols)")
    Processing("Processing<br/>(153 symbols)")
    Prediction("Prediction<br/>(229 symbols)")
    Keyboard("Keyboard<br/>(22 symbols)")
    Settings("Settings<br/>(98 symbols)")

    %% Platform layer
    Gaze("Gaze<br/>(25 symbols)")
    Input("Input<br/>(12 symbols)")

    %% App layer
    Services("Services<br/>(55 symbols)")
    EyeCtrlApp("EyeCtrl.App<br/>(5 symbols)")

    %% ViewModels
    ViewModels("EyeCtrl.ViewModels<br/>(37 symbols)")

    %% UI
    Overlays("Overlays<br/>(104 symbols)")
    Views("Views<br/>(32 symbols)")
    Controls("Controls<br/>(27 symbols)")

    %% Core → Core
    Processing -->|6 calls| ViewModels
    Processing -->|4 calls| ViewModels

    %% App → Core/ViewModels
    Services -->|6 calls| Views
    Services -->|2 calls| Gaze
    Services -->|1 call| Prediction
    Services -->|1 call| Overlays

    %% ViewModels ← Views/Overlays
    Views -->|9 calls| Settings
    Views -->|3 calls| ViewModels
    Overlays -->|2 calls| ViewModels
    Overlays -->|3 calls| Processing
    Overlays -->|4 calls| Prediction
    Overlays -->|3 calls| Services
    Overlays -->|2 calls| Models

    %% Models used everywhere
    Models -->|3 calls| Overlays
    Models -->|2 calls| Views
    Models -->|1 call| Services

    %% Gaze feeds Processing
    EyeCtrlApp -->|1 call| Gaze
    EyeCtrlApp -->|3 calls| Services
    EyeCtrlApp -->|1 call| Overlays

    class Models,Interfaces,Processing,Prediction,Keyboard,Settings core
    class Gaze,Input platform
    class Services,EyeCtrlApp app
    class ViewModels vm
    class Overlays,Views,Controls ui
```

Arrows show the top inter-community `CALLS` edges from the graph. Edge labels are call counts.

## Project ↔ Area Mapping

| .NET Project | Primary Areas |
|-------------|---------------|
| `EyeCtrl.Core` | Processing, Prediction, Settings, Models, Interfaces, Keyboard |
| `EyeCtrl.Platform` | Gaze, Input |
| `EyeCtrl.ViewModels` | EyeCtrl.ViewModels |
| `EyeCtrl.App` | Services, Overlays, Views, Controls, EyeCtrl.App |

## Key Execution Flows

The graph contains 264 processes (execution flows). Top 5 by relevance, with step-by-step traces:

### 1. Gaze Engine Startup (`Start → GazePoint`)

4-step intra-community process:

| Step | Symbol | File |
|------|--------|------|
| 1 | `GazeEngine.Start()` | `src/EyeCtrl.Core/GazeEngine.cs` |
| 2 | `TobiiStreamEngineProvider.Start()` | `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs` |
| 3 | `SubscribeGaze()` | `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs` |
| 4 | `GazePoint` (data model) | `src/EyeCtrl.Core/Models/GazePoint.cs` |

### 2. Gaze Update → Dwell Position (`UpdateGaze → UpdatePosition`)

4-step intra-community process:

| Step | Symbol | File |
|------|--------|------|
| 1 | `SettingsWindow.UpdateGaze()` | `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` |
| 2 | `UpdateAllTargetPositions()` | `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` |
| 3 | `DwellControl.UpdatePosition()` | `src/EyeCtrl.App/Controls/DwellControl.cs` |
| 4 | `IDwellRegistrar.UpdatePosition()` | `src/EyeCtrl.Core/Processing/IDwellRegistrar.cs` |

### 3. Manual Selection Activation (`OnStartManualSelection → PhysicalPoint`)

3-step intra-community process:

| Step | Symbol | File |
|------|--------|------|
| 1 | `GazeEngineAdapter.OnStartManualSelection()` | `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` |
| 2 | `ManualSelectionLayoutHelper.CalculateDisplayPoint()` | `src/EyeCtrl.ViewModels/Helpers/ManualSelectionLayoutHelper.cs` |
| 3 | `PhysicalPoint` construction | `src/EyeCtrl.Core/Models/PhysicalPoint.cs` |

### 4. Action Menu Display (`OnShowActionMenu → PhysicalPoint`)

3-step intra-community process:

| Step | Symbol | File |
|------|--------|------|
| 1 | `GazeEngineAdapter.OnShowActionMenu()` | `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` |
| 2 | `ActionMenuLayoutHelper.CalculateDisplayPoint()` | `src/EyeCtrl.ViewModels/Helpers/ActionMenuLayoutHelper.cs` |
| 3 | `PhysicalPoint` construction | `src/EyeCtrl.Core/Models/PhysicalPoint.cs` |

### 5. Manual Selection Recapture (`Recapture → LogicalToPhysicalY`)

4-step intra-community process:

| Step | Symbol | File |
|------|--------|------|
| 1 | `ManualSelectionOverlayWindow.Recapture()` | `src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs` |
| 2 | `CaptureAndDisplay()` | `src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs` |
| 3 | `ScreenCaptureService.CaptureRegion()` | `src/EyeCtrl.App/Services/ScreenCaptureService.cs` |
| 4 | `DpiHelper.LogicalToPhysicalY()` | `src/EyeCtrl.Core/Services/DpiHelper.cs` |

## Interaction State Machine

```mermaid
stateDiagram-v2
    [*] --> FreeLook

    state FreeLook {
        [*] --> cursor_follows_gaze
    }

    FreeLook --> Fixation : fixation detected
    Fixation --> FreeLook : fixation ended

    state Fixation {
        [*] --> show_activation_button
    }

    Fixation --> ActionMenuVisible : dwell activation\n(ManualSelection disabled)
    Fixation --> ManualSelectionActive : dwell activation\n(ManualSelection enabled)

    ActionMenuVisible --> FreeLook : action selected / dismissed

    state ManualSelectionActive {
        [*] --> magnified_overlay
        state magnified_overlay {
            gaze_tracks --> dwell_accumulates
            dwell_accumulates --> indicator_shrinks
        }
    }

    ManualSelectionActive --> FreeLook : close button dwell
    ManualSelectionActive --> ManualSelectionActionMenuVisible : dwell on point

    ManualSelectionActionMenuVisible --> FreeLook : action selected
    ManualSelectionActionMenuVisible --> ManualSelectionActive : back button

    FreeLook --> Paused : toolbar pause
    Paused --> FreeLook : toolbar resume
    Fixation --> Paused : pause
    Paused --> Fixation : resumed fixation still active
```

**6 states:** FreeLook → Fixation → ActionMenuVisible / ManualSelectionActive → ManualSelectionActionMenuVisible / Paused

## Layers

```
┌──────────────────────────────────────────────────────────────────┐
│                     EyeCtrl.App (WPF Entry)                       │
│  ┌──────────┐   ┌──────────────┐   ┌──────────┐   ┌──────────┐  │
│  │ Overlays │◄──│ EventCoordin-│   │ TrayIcon │   │ GazeEng-  │  │
│  │(10 wind.)│   │ ator         │   │ Manager  │   │ ineAdapter│  │
│  └──────────┘   └──────┬───────┘   └──────────┘   └────┬─────┘  │
│                        │                                │        │
├────────────────────────┼────────────────────────────────┼────────┤
│       EyeCtrl.ViewModels (MVVM)                         │        │
│  ┌──────────┐ ┌───────┐ ┌─────────────┐ ┌───────────┐  │        │
│  │Cursor    │ │Activ- │ │ManualSel    │ │Action     │  │        │
│  │CircleVM  │ │BtnVM  │ │OverlayVM    │ │MenuVM     │  │        │
│  └──────────┘ └───────┘ └─────────────┘ └───────────┘  │        │
│  ┌──────────┐ ┌───────┐ ┌───────────┐                  │        │
│  │ToolbarVM │ │Keybrd │ │SettingsVM │                  │        │
│  │          │ │VM     │ │           │                  │        │
│  └──────────┘ └───────┘ └───────────┘                  │        │
├─────────────────────────────────────────────────────────┼────────┤
│                 EyeCtrl.Core (Domain)                   │        │
│  ┌────────────┐   ┌────────────────────────────┐       │        │
│  │ GazeEngine │   │ InteractionManager         │       │        │
│  │ (pipeline) │   │ (6-state FSM)              │       │        │
│  └─────┬──────┘   └─────────────┬──────────────┘       │        │
│        │                        │                       │        │
│  ┌─────┴────────────────────────┴───────────────────┐  │        │
│  │ FixationDetector   DwellDetector   DwellTarget   │  │        │
│  │ OneEuroFilter      GapInterpolator               │  │        │
│  │ FixationCentroidTracker                          │  │        │
│  └──────────────────────────────────────────────────┘  │        │
│  ┌──────────────────────────────────────────────────┐  │        │
│  │ WordPredictor (n-gram + bigram, 229 symbols)      │  │        │
│  │ KeyboardLayout (multi-lang JSON, 22 symbols)      │  │        │
│  │ SettingsService (JSON persistence, 98 symbols)    │  │        │
│  │ Models (PhysicalPoint, GazePoint, etc., 27 sym.)  │  │        │
│  └──────────────────────────────────────────────────┘  │        │
├──────────────────────────────────────────────────────────────────┤
│                 EyeCtrl.Platform (OS Bridge)                     │
│  ┌──────────────────────┐   ┌──────────────────────────────┐    │
│  │ IGazeProvider         │   │ IInputSimulator              │    │
│  │ ├─ TobiiStreamEngine  │   │ └─ Win32InputSimulator       │    │
│  │ └─ MouseGazeProvider  │   │    (SendInput P/Invoke)      │    │
│  └──────────────────────┘   │ IMouseCoordinateMapper       │    │
│                              └──────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────┘
```

## Messaging (WeakReferenceMessenger)

The ViewModels layer communicates with the App layer via three message groups defined in `src/EyeCtrl.ViewModels/Messages/`:

| Message Group | File | Purpose |
|---------------|------|---------|
| **GazeMessages** | `GazeMessages.cs` | Cursor position updates, gaze provider state changes |
| **InteractionMessages** | `InteractionMessages.cs` | Dwell activation, action selection, key presses |
| **OverlayMessages** | `OverlayMessages.cs` | Show/hide overlays, toolbar/keyboard visibility |

This replaces direct ViewModel→App references with decoupled publish/subscribe via `WeakReferenceMessenger`.

## Communication Flow

```mermaid
sequenceDiagram
    participant Platform as Platform<br/>(Gaze, Input)
    participant Core as Core<br/>(Processing, Prediction, Settings)
    participant Adapter as App<br/>(GazeEngineAdapter)
    participant VM as ViewModels
    participant Overlay as Overlays + Views

    Platform->>Core: GazePointReceived (C# event)
    Core->>Core: Filter → FixationDetect → InteractionManager
    Core->>Adapter: CursorMoved / ShowActivationButton (C# event)
    Adapter->>VM: Send message (WeakReferenceMessenger)
    VM->>Overlay: PropertyChanged → show/hide/position
    Overlay->>VM: Dwell activated on control
    VM->>Adapter: Send message (e.g., ActivationCompleted)
    Adapter->>Core: InteractionManager.OnActivationButtonDwelled()
    Core->>Core: State transition → event
    Core->>Adapter: ShowActionMenu / StartManualSelection
    Adapter->>VM: Send message
    VM->>Overlay: Show/hide overlay windows
    Overlay->>Overlay: User dwells action button
    Overlay->>Adapter: ActionSelected message
    Adapter->>Core: InteractionManager.OnActionSelected()
    Core->>Core: ActionRequested event
    Core->>Platform: IInputSimulator.Click() / SendInput
```

## Dependencies

| Dependency | Usage |
|------------|-------|
| CommunityToolkit.Mvvm 8.4.0 | `[ObservableProperty]` source generators, `WeakReferenceMessenger` |
| Microsoft.Extensions.DependencyInjection | DI container (`ServiceCollection`, `ServiceProvider`) |
| Serilog | Structured logging, daily rolling files, 7-day retention |
| System.Text.Json | Settings serialization with `JsonNamingPolicy.CamelCase` |
| Tobii Stream Engine SDK | Conditional (`#if TOBII_AVAILABLE`), only when SDK DLLs present |
| BenchmarkDotNet | Standalone benchmarks (not in solution, not in graph count) |

## Statistics (from Knowledge Graph)

| Metric | Count |
|--------|-------|
| Total symbols | 5,021 |
| Relationships | 11,207 |
| Communities | 173 |
| Processes (execution flows) | 264 |
| Interfaces | 10 |
| Source files indexed | 307 |
