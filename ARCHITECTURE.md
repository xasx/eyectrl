# EyeCtrl Architecture

EyeCtrl is a gaze-controlled Windows application that enables hands-free computer interaction through eye tracking. It provides cursor control, an on-screen keyboard with word prediction, action menus (click, scroll, drag), and a manual selection mode for precise targeting.

## Layer Diagram

```mermaid
graph TB
    subgraph "EyeCtrl.App (WPF Entry Point)"
        App["App.xaml.cs<br/>Startup / Shutdown"]
        Bootstrapper["AppBootstrapper<br/>DI Container"]
        Coordinator["EventCoordinator<br/>Event Routing"]
        OverlayMgr["OverlayManager<br/>Window Lifecycle"]
        TrayIcon["TrayIconManager"]
        GazeAdapter["GazeEngineAdapter<br/>Core → MVVM Bridge"]
        MSelectionDwell["ManualSelectionDwellService"]

        subgraph "Overlay Windows"
            CursorWin["CursorCircleWindow"]
            ActivationWin["ActivationButtonWindow"]
            ActionMenuWin["ActionMenuWindow"]
            ToolbarWin["ToolbarWindow"]
            KeyboardWin["KeyboardWindow"]
            MSelectionWin["ManualSelectionOverlayWindow"]
            MSelectionIndicator["ManualSelectionGazeIndicatorWindow"]
            MSelectionSideBtns["ManualSelectionSideButtonWindow"]
            BackdropWin["GlassBackdropWindow"]
            SplashWin["SplashWindow"]
            SettingsWin["SettingsWindow"]
        end
    end

    subgraph "EyeCtrl.ViewModels (MVVM)"
        CursorVM["CursorCircleViewModel"]
        ActivationVM["ActivationButtonViewModel"]
        ActionMenuVM["ActionMenuViewModel"]
        ToolbarVM["ToolbarViewModel"]
        KeyboardVM["KeyboardViewModel"]
        MSelectionVM["ManualSelectionOverlayViewModel"]
        MSelectionIndVM["ManualSelectionGazeIndicatorViewModel"]
        BackdropVM["GlassBackdropViewModel"]
        SettingsVM["SettingsViewModel"]
        SettingsWinVM["SettingsWindowViewModel"]
        Messenger["WeakReferenceMessenger<br/>(Message Bus)"]
    end

    subgraph "EyeCtrl.Core (Domain Logic)"
        GazeEngine["GazeEngine<br/>Core Pipeline"]

        subgraph "Processing"
            GapInterp["GapInterpolator"]
            Filter["OneEuroFilter / SmoothedFilter / DirectFilter"]
            FixDet["FixationDetector"]
            FixCentroid["FixationCentroidTracker"]
            DwellDet["DwellDetector"]
            DwellTarget["DwellTarget"]
            IntMgr["InteractionManager<br/>State Machine"]
        end

        subgraph "Prediction"
            PredOrch["PredictionOrchestrator"]
            WordPred["WordPredictor"]
            BigramModel["BigramModel"]
            FuzzySearch["FuzzyEditDistance"]
            SentenceIdx["SentenceCompletionIndex"]
            LMPredictor["SmallLMPredictor"]
            LMTokenizer["LMTokenizer"]
            PhraseModel["PhraseModel"]
        end

        subgraph "Keyboard"
            KBLayout["KeyboardLayout"]
            KBLoader["KeyboardLayoutLoader"]
            KeyDef["KeyDefinition"]
            EmojiData["EmojiData"]
            RecentEmoji["RecentEmojiStore"]
        end

        subgraph "Settings"
            SettingsSvc["SettingsService"]
            EyeCtrlSettings["EyeCtrlSettings"]
            DwellSettings["DwellSettings"]
            CursorSettings["CursorSettings"]
            KBSettings["KeyboardSettings"]
            ActionMenuSettings["ActionMenuSettings"]
            ToolbarSettings["ToolbarSettings"]
            MSelectionSettings["ManualSelectionSettings"]
            ThemeSettings["ThemeSettings"]
            BackdropSettings["BackdropSettings"]
            GeneralSettings["GeneralSettings"]
        end

        subgraph "Services"
            DPI["DpiHelper"]
            Logging["LoggingInitializer"]
        end

        subgraph "Interfaces"
            IGazeProvider["IGazeProvider"]
            IInputSimulator["IInputSimulator"]
            IDwellRegistrar["IDwellRegistrar"]
        end
    end

    subgraph "EyeCtrl.Platform (OS/SDK Bridge)"
        subgraph "Gaze"
            GazeFactory["GazeProviderFactory"]
            MouseGaze["MouseGazeProvider"]
            TobiiGaze["TobiiStreamEngineProvider"]
        end
        subgraph "Input"
            Win32Input["Win32InputSimulator"]
            MouseCoord["MouseCoordinateMapper"]
            NativeMethods["NativeMethods"]
            VK["VirtualKeys"]
        end
    end

    %% App → DI
    App --> Bootstrapper
    Bootstrapper -->|"registers"| GazeEngine
    Bootstrapper -->|"registers"| CursorVM
    Bootstrapper -->|"registers"| ActivationVM
    Bootstrapper -->|"registers"| ActionMenuVM
    Bootstrapper -->|"registers"| ToolbarVM
    Bootstrapper -->|"registers"| KeyboardVM
    Bootstrapper -->|"registers"| MSelectionVM
    Bootstrapper -->|"registers"| Win32Input
    Bootstrapper -->|"registers"| MouseGaze
    Bootstrapper -->|"registers"| TobiiGaze

    %% App → Coordination
    App --> Coordinator
    App --> OverlayMgr
    App --> TrayIcon
    App --> GazeAdapter

    %% EventCoordinator wiring
    Coordinator -->|"PropertyChanged"| CursorVM
    Coordinator -->|"PropertyChanged"| ActivationVM
    Coordinator -->|"PropertyChanged"| ActionMenuVM
    Coordinator -->|"PropertyChanged"| ToolbarVM
    Coordinator -->|"PropertyChanged"| MSelectionVM
    Coordinator -->|"PropertyChanged"| BackdropVM
    Coordinator -->|"messages"| Messenger
    Coordinator -->|"routes gaze"| ToolbarWin
    Coordinator -->|"routes gaze"| KeyboardWin
    Coordinator -->|"routes gaze"| SettingsWin

    %% GazeEngineAdapter bridge
    GazeAdapter -->|"subscribes"| GazeEngine
    GazeAdapter -->|"sends messages"| Messenger

    %% GazeEngine pipeline
    GazeEngine --> GapInterp
    GapInterp --> Filter
    GazeEngine --> FixDet
    FixDet --> FixCentroid
    GazeEngine --> IntMgr
    FixDet -->|"FixationStarted"| IntMgr

    %% InteractionManager events
    IntMgr -->|"ShowActivationButton"| ActivationVM
    IntMgr -->|"ShowActionMenu"| ActionMenuVM
    IntMgr -->|"StartManualSelection"| MSelectionVM
    IntMgr -->|"ActionRequested"| Coordinator

    %% Prediction pipeline
    KeyboardVM --> PredOrch
    PredOrch --> WordPred
    PredOrch --> SentenceIdx
    PredOrch --> LMPredictor
    WordPred --> BigramModel
    WordPred --> FuzzySearch
    LMPredictor --> LMTokenizer

    %% Platform
    GazeFactory --> MouseGaze
    GazeFactory --> TobiiGaze
    MouseGaze -->|"implements"| IGazeProvider
    TobiiGaze -->|"implements"| IGazeProvider
    Win32Input -->|"implements"| IInputSimulator
    Win32Input --> NativeMethods

    %% Dwell
    DwellDet --> DwellTarget
    DwellDet --> IDwellRegistrar

    %% Settings
    SettingsSvc -->|"loads/saves"| EyeCtrlSettings
    EyeCtrlSettings --> DwellSettings
    EyeCtrlSettings --> CursorSettings
    EyeCtrlSettings --> KBSettings
    EyeCtrlSettings --> ActionMenuSettings
    EyeCtrlSettings --> ToolbarSettings
    EyeCtrlSettings --> MSelectionSettings
    EyeCtrlSettings --> ThemeSettings
    EyeCtrlSettings --> BackdropSettings
    EyeCtrlSettings --> GeneralSettings
```

## Functional Areas

### 1. Gaze Processing Pipeline (`EyeCtrl.Core/Processing/`)

The core signal processing chain that converts raw eye tracker data into smooth cursor movement and detected fixations.

| Component | File | Purpose |
|-----------|------|---------|
| `GapInterpolator` | `GapInterpolator.cs` | Fills brief signal dropouts from the eye tracker to prevent cursor jitter |
| `OneEuroFilter` | `OneEuroFilter.cs` | Adaptive low-pass filter — smooth when slow, responsive when fast |
| `SmoothedFilter` | `SmoothedFilter.cs` | Alternative simple smoothing filter |
| `DirectFilter` | `DirectFilter.cs` | Passthrough (no filtering) |
| `GazeFilterFactory` | `GazeFilterFactory.cs` | Creates the appropriate filter based on `CursorSettings.FilterMode` |
| `FixationDetector` | `FixationDetector.cs` | Detects when the user holds gaze within a radius long enough |
| `FixationCentroidTracker` | `FixationCentroidTracker.cs` | Tracks the stable centroid of a fixation using velocity thresholding |
| `DwellDetector` | `DwellDetector.cs` | Manages dwell targets and fires dwell completion events |
| `DwellTarget` | `DwellTarget.cs` | Represents a single registered dwell-activatable UI element |
| `InteractionManager` | `InteractionManager.cs` | State machine governing the full interaction lifecycle |

### 2. Interaction State Machine

The `InteractionManager` drives all gaze-based interactions through these states:

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

- **FreeLook**: Cursor follows gaze, fixation detection active
- **Fixation**: Activation button shown at fixation point
- **ActionMenuVisible**: Radial action menu (click, scroll, drag) shown
- **ManualSelectionActive**: Magnified area shown for precise targeting
- **ManualSelectionActionMenuVisible**: Action menu positioned at precise dwell point
- **Paused**: All interaction suspended (cursor still follows gaze for toolbar access)

### 3. Word Prediction (`EyeCtrl.Core/Prediction/`)

Multi-strategy prediction system combining n-gram statistics, trie-based sentence completion, and optional small language model inference.

| Component | File | Purpose |
|-----------|------|---------|
| `PredictionOrchestrator` | `PredictionOrchestrator.cs` | Merges results from all prediction strategies |
| `WordPredictor` | `WordPredictor.cs` | N-gram + fuzzy matching word completion |
| `BigramModel` | `BigramModel.cs` | Bigram word transition probabilities |
| `SentenceCompletionIndex` | `SentenceCompletionIndex.cs` | Trie-based prefix completion |
| `SmallLMPredictor` | `SmallLMPredictor.cs` | ONNX runtime neural language model |
| `LMTokenizer` | `LMTokenizer.cs` | Tokenizer for the LM model |
| `FuzzyEditDistance` | `FuzzyEditDistance.cs` | Edit-distance-based fuzzy matching with keyboard adjacency weighting |
| `FileDictionaryProvider` | `FileDictionaryProvider.cs` | Loads word frequency dictionaries from JSON |
| `LanguageDetector` | `LanguageDetector.cs` | Auto-detects system language for dictionary selection |
| `ModelDownloadService` | `ModelDownloadService.cs` | Downloads LM model files on demand |

### 4. On-Screen Keyboard (`EyeCtrl.Core/Keyboard/` + `EyeCtrl.App/Overlays/KeyboardWindow`)

JSON-driven keyboard layouts with sub-layouts, emoji support, and prediction integration.

| Component | File | Purpose |
|-----------|------|---------|
| `KeyboardLayout` | `KeyboardLayout.cs` | Parsed keyboard layout definition |
| `KeyboardLayoutLoader` | `KeyboardLayoutLoader.cs` | Loads layouts from JSON files |
| `KeyDefinition` | `KeyDefinition.cs` | Single key definition (label, action, size, modifiers) |
| `SubLayout` | `SubLayout.cs` | Shift/symbol layer within a layout |
| `EmojiData` | `EmojiData.cs` | Emoji grid data |
| `EmojiDataLoader` | `EmojiDataLoader.cs` | Loads emoji data from JSON |
| `RecentEmojiStore` | `RecentEmojiStore.cs` | Persists recently used emojis |

### 5. Settings (`EyeCtrl.Core/Settings/`)

Hierarchical JSON settings with validation and debounced persistence.

| Component | File | Purpose |
|-----------|------|---------|
| `SettingsService` | `SettingsService.cs` | JSON load/save with debounce and validation |
| `EyeCtrlSettings` | `EyeCtrlSettings.cs` | Root settings object (contains all sub-settings) |
| `ValidationHelper` | `ValidationHelper.cs` | Clamps and validates setting values |

Sub-settings: `CursorSettings`, `DwellSettings`, `KeyboardSettings`, `ActionMenuSettings`, `ToolbarSettings`, `ManualSelectionSettings`, `ThemeSettings`, `BackdropSettings`, `GeneralSettings`.

### 6. ViewModel Layer (`EyeCtrl.ViewModels/`)

CommunityToolkit.Mvvm ViewModels using `[ObservableProperty]` source generators and `WeakReferenceMessenger` for decoupled communication.

| ViewModel | Purpose |
|-----------|---------|
| `CursorCircleViewModel` | Cursor circle position and appearance |
| `ActivationButtonViewModel` | Fixation activation button state |
| `ActionMenuViewModel` | Radial action menu layout and dwell progress |
| `ToolbarViewModel` | Floating toolbar (pause, keyboard toggle, settings) |
| `KeyboardViewModel` | On-screen keyboard state, prediction display |
| `ManualSelectionOverlayViewModel` | Magnified selection area |
| `ManualSelectionGazeIndicatorViewModel` | Gaze indicator during manual selection |
| `GlassBackdropViewModel` | Screen backdrop overlay |
| `SettingsViewModel` | Settings page tree |
| `SettingsWindowViewModel` | Settings window chrome |

### 7. Overlay Windows (`EyeCtrl.App/Overlays/`)

Transparent WPF windows rendered over the desktop. Each window is paired with a ViewModel.

| Window | Purpose |
|--------|---------|
| `CursorCircleWindow` | Visual cursor indicator |
| `ActivationButtonWindow` | Dwell activation button at fixation point |
| `ActionMenuWindow` | Radial action menu |
| `ToolbarWindow` | Persistent toolbar (dockable) |
| `KeyboardWindow` | Full on-screen keyboard |
| `ManualSelectionOverlayWindow` | Magnified area for precise clicks |
| `ManualSelectionGazeIndicatorWindow` | Gaze dot during manual selection |
| `ManualSelectionSideButtonWindow` | Close/recapture buttons |
| `GlassBackdropWindow` | Dimming backdrop overlay |
| `SplashWindow` | Startup splash screen |
| `SettingsWindow` | Settings UI |

### 8. Platform Bridge (`EyeCtrl.Platform/`)

OS-specific implementations with conditional Tobii SDK support.

| Component | File | Purpose |
|-----------|------|---------|
| `GazeProviderFactory` | `Gaze/GazeProviderFactory.cs` | Creates gaze provider (Tobii SDK or mouse fallback) |
| `MouseGazeProvider` | `Gaze/MouseGazeProvider.cs` | Mouse cursor position as gaze proxy (development/fallback) |
| `TobiiStreamEngineProvider` | `Gaze/TobiiStreamEngineProvider.cs` | Tobii EyeX SDK integration (conditional on `TOBII_AVAILABLE`) |
| `Win32InputSimulator` | `Input/Win32InputSimulator.cs` | `SendInput` P/Invoke for mouse/keyboard simulation |
| `MouseCoordinateMapper` | `Input/MouseCoordinateMapper.cs` | DPI-aware coordinate mapping |
| `NativeMethods` | `Input/NativeMethods.cs` | Win32 API declarations |
| `VirtualKeys` | `Input/VirtualKeys.cs` | Virtual key code constants |

### 9. Application Services (`EyeCtrl.App/Services/`)

| Service | File | Purpose |
|---------|------|---------|
| `AppBootstrapper` | `AppBootstrapper.cs` | DI container configuration |
| `EventCoordinator` | `EventCoordinator.cs` | Wires PropertyChanged events and messages between VMs and overlay windows |
| `OverlayManager` | `OverlayManager.cs` | Creates and manages overlay window instances |
| `GazeEngineAdapter` | `GazeEngineAdapter.cs` | Bridges `GazeEngine` core events into MVVM messages |
| `ManualSelectionDwellService` | `ManualSelectionDwellService.cs` | Handles manual selection dwell detection |
| `TrayIconManager` | `TrayIconManager.cs` | System tray icon and context menu |
| `ScreenCaptureService` | `ScreenCaptureService.cs` | Screen capture for magnification |
| `LoggingInitializer` | `LoggingInitializer.cs` | Serilog configuration (file + rolling) |

## Key Execution Flows

### Application Startup

```
App.OnStartup
  ├── SplashWindow.Show()
  ├── DpiHelper.Initialize()
  ├── SettingsService.Load() → EyeCtrlSettings
  ├── LoggingInitializer.Initialize()
  ├── AppBootstrapper.BuildServiceProvider() → DI container
  ├── Resolve all services and ViewModels from DI
  ├── OverlayManager.Initialize() → create all overlay windows
  ├── EventCoordinator.Initialize() → wire all event handlers
  ├── EventCoordinator.LoadKeyboard() → layout + prediction setup
  ├── TrayIconManager()
  ├── OverlayManager show cursor + toolbar
  └── GazeEngine.Start() → IGazeProvider.Start()
```

### Gaze Processing Loop

```
IGazeProvider.GazePointReceived
  └── GazeEngine.OnGazePoint(GazePoint)
        ├── GapInterpolator.Process() → filtered point
        ├── CursorMoved event → EventCoordinator routes to Toolbar + Keyboard
        ├── FixationCentroidTracker.Update()
        ├── [if FreeLook] IInputSimulator.MoveCursor()
        └── [if !suppress] FixationDetector.Update()
              ├── [if fixation started] → InteractionManager.OnFixationStarted()
              │     └── ShowActivationButton event → ActivationButtonViewModel
              └── [if fixation ended] → InteractionManager.OnFixationEnded()
                    └── HideActivationButton event
```

### Dwell Activation → Action Menu

```
DwellDetector.Update() [activation button dwell]
  └── InteractionManager.OnActivationButtonDwelled()
        ├── [if ManualSelection enabled]
        │     ├── StartManualSelection event → ManualSelectionOverlayViewModel
        │     └── State → ManualSelectionActive
        └── [else]
              ├── ShowActionMenu event → ActionMenuViewModel
              └── State → ActionMenuVisible
```

### Action Selection

```
ActionMenuViewModel action dwell
  └── Messenger.Send(ActionRequestedMessage)
        └── EventCoordinator.HandleActionRequested()
              └── IInputSimulator.LeftClick / RightClick / DoubleClick / Drag / Scroll
                    └── Win32InputSimulator → SendInput P/Invoke
```

### Keyboard Prediction

```
KeyboardViewModel.ProcessKeyGaze(key)
  ├── Append character to typed text
  ├── PredictionOrchestrator.GetSuggestions(context)
  │     ├── WordPredictor.GetSuggestions() → n-gram + fuzzy
  │     ├── SentenceCompletionIndex.Search() → trie prefix
  │     └── SmallLMPredictor.Predict() → neural LM
  └── Display merged suggestions in prediction bar

[Prediction dwell]
  └── Messenger.Send(PredictionSelectedMessage)
        └── EventCoordinator.HandlePredictionSelected()
              ├── SendKeyPress(Backspace) × deleteCount
              └── SendText(word + " ")
```

### Manual Selection Flow

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

## Technology Stack

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 10.0 | Runtime |
| WPF | (bundled) | UI framework |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM framework with source generators |
| Serilog | — | Structured logging |
| Microsoft.Extensions.DependencyInjection | — | DI container |
| ONNX Runtime | — | Neural language model inference |
| Tobii EyeX SDK | (optional) | Eye tracker integration |
| xUnit | 2.9.x | Testing framework |
| BenchmarkDotNet | — | Performance benchmarks |

## Project Structure

```
src/
├── EyeCtrl.Core/           Domain logic (framework-agnostic)
│   ├── GazeEngine.cs       Core gaze pipeline
│   ├── Processing/         Signal processing, dwell, fixation, interaction state
│   ├── Prediction/         Word prediction, n-gram, trie, LM
│   ├── Keyboard/           Layout definitions, emoji data
│   ├── Settings/           Settings model, validation, persistence
│   ├── Interfaces/         IGazeProvider, IInputSimulator, IDwellRegistrar
│   ├── Models/             GazePoint, PhysicalPoint, etc.
│   └── Services/           DpiHelper, LoggingInitializer
├── EyeCtrl.Platform/       OS/SDK bridge
│   ├── Gaze/               GazeProviderFactory, MouseGazeProvider, TobiiStreamEngineProvider
│   └── Input/              Win32InputSimulator, NativeMethods, VirtualKeys
├── EyeCtrl.ViewModels/     MVVM layer
│   ├── *.cs                ViewModels (CommunityToolkit.Mvvm)
│   ├── Helpers/            Layout helpers
│   └── Messages/           WeakReferenceMessenger message types
└── EyeCtrl.App/            WPF application
    ├── App.xaml.cs          Entry point, lifecycle
    ├── Services/            AppBootstrapper, EventCoordinator, OverlayManager, etc.
    ├── Overlays/            All overlay windows
    ├── Views/               SettingsWindow
    ├── Controls/            Dwell-aware custom controls
    ├── Resources/           Layouts, dictionaries, emojis, completions, assets
    └── Assets/              click.wav, icons

tests/
├── EyeCtrl.Core.Tests/     Core unit tests
├── EyeCtrl.Platform.Tests/ Platform unit tests
├── EyeCtrl.ViewModels.Tests/ ViewModel unit tests
└── EyeCtrl.App.Tests/      UI integration tests

benchmarks/
└── EyeCtrl.Benchmarks/     BenchmarkDotNet performance tests
```
