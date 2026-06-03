# External Dwell API — Design Spec

## Summary

Enable external Windows applications to have their UI controls activated by gaze dwell, with minimal integration effort. Two paths: a NuGet SDK for cooperating apps (XAML-level integration), and profile config files for unmodified third-party apps.

## Architecture

```
┌───────────────────────────────┐     ┌──────────────────────────────┐
│         EyeCtrl.App           │     │      External WPF App         │
│                               │     │                              │
│  GazeEngine → MoveCursor()    │     │  EyeCtrl.Sdk NuGet package   │
│                     │         │     │  ┌────────────────────────┐  │
│                     ▼         │     │  │ XAML: eyectrl:Dwell.*  │  │
│              System Cursor ───┼─────┼──► Attached properties    │  │
│                     ▲         │     │  │    ├─ Auto-register    │  │
│  ┌──────────┐       │         │     │  │    ├─ DwellDetector   │  │
│  │Zone Editor│──────┘         │     │  │    └─ OnActivate→Click│  │
│  │ (Phase 2)│                 │     │  └────────────────────────┘  │
│  └──────────┘   ┌──────────┐  │     │                              │
│                 │Profile   │──┼─────┼── Config files (JSON)        │
│                 │Loader    │  │     │  for unmodified apps          │
│                 └──────────┘  │     │                              │
│                               │     │                              │
│  IPC: "hide cursor" signal    │     │  (only IPC channel)          │
└───────────────────────────────┘     └──────────────────────────────┘
```

Zero IPC for control registration or activation. The cooperating app's SDK runs its own DwellDetector locally, watching the system cursor (already positioned by EyeCtrl). On activation, the SDK fires the control's click handler directly. No round-trip to EyeCtrl.

The sole IPC channel is a lightweight "hide/show cursor" signal, so EyeCtrl's gaze cursor ring disappears when the user is over a dwell-registered control (replaced by the control's own visual feedback).

## Cooperating Apps (NuGet SDK)

### Project: `EyeCtrl.Sdk`

A new project producing a NuGet package. Targets `net10.0-windows`, references `EyeCtrl.Core` (for `DwellDetector`, `DwellTarget`, `PhysicalPoint`).

### XAML Integration

```xml
<Window xmlns:eyectrl="clr-namespace:EyeCtrl.Sdk;assembly=EyeCtrl.Sdk">
    <Button Content="Save"
            eyectrl:Dwell.Enabled="True"
            eyectrl:Dwell.Time="800"
            eyectrl:Dwell.Radius="60" />
</Window>
```

**Attached properties** (`Dwell.cs`):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enables dwell activation on this element |
| `Time` | `int` | `800` | Dwell duration in milliseconds |
| `Radius` | `double` | `60` | Hit-test radius in pixels |
| `Mode` | `enum` | `Click` | `Click` (fires Click/Command) or `Focus` (sets focus only) |

**Registration lifecycle** (`DwellManager.cs`):

1. `FrameworkElement.Loaded` → walk visual tree to root `Window` → get HWND via `WindowInteropHelper` → compute control bounds relative to window → register `DwellTarget(controlId, relativeRect, time, radius)` with local `DwellDetector`
2. `FrameworkElement.Unloaded` → unregister
3. Optional: `LayoutUpdated` hook for controls that reposition within window (off by default)

**Screen-position resolution**:

Each frame, `DwellManager` calls `GetWindowRect(hwnd)` and offsets each control's relative bounds to compute current screen coordinates. The app never sends position updates. Window moves, resizes, and minimizes are handled automatically.

### Dwell Detection

The SDK embeds a copy of `DwellDetector` from `EyeCtrl.Core.Processing`. It polls cursor position via `GetCursorPos()` on a timer, running the same nearest-target accumulation algorithm EyeCtrl uses for its own overlays.

On activation:
- For `ButtonBase` controls: raises the `Click` event or executes the bound `ICommand`
- For `Mode=Focus`: calls `element.Focus()`
- The activation happens in-process, no IPC

### IPC Client (Cursor Visibility)

A lightweight named pipe client in the SDK sends two signals to EyeCtrl:
- `cursor:hide` — when gaze enters any dwell-registered element's hit area
- `cursor:show` — when gaze leaves all dwell-registered elements' hit areas

Failure to connect (EyeCtrl not running) is silent — the app functions normally, the gaze cursor ring simply remains visible.

## Unmodified Apps (Profile Configs)

### Profile Loader: `ProfileDwellService`

EyeCtrl watches a profiles directory (`%APPDATA%/EyeCtrl/profiles/`). Each `.json` file defines dwell targets for a specific application window.

**Profile JSON schema**:

```json
{
  "app": "notepad",
  "windowTitle": "*Notepad*",
  "windowClass": "Notepad",
  "targets": [
    {
      "id": "file_menu",
      "relativeX": 0, "relativeY": 0,
      "width": 40, "height": 24,
      "dwellTimeMs": 800,
      "radius": 30
    }
  ]
}
```

**Runtime behavior**:

- `ProfileDwellService` enumerates top-level windows via `EnumWindows`, matches against profile criteria (title and/or class wildcard)
- For matched windows, creates `DwellTargets` in EyeCtrl's own `DwellDetector`
- On activation, EyeCtrl performs `SendInput` click at the target's resolved screen position
- Tracks window minimize/close: removes targets when window disappears

This runs entirely within EyeCtrl's process — no SDK code in the target app.

### Zone Editor (Phase 2, out of initial scope)

A companion tool to visually define dwell zones by dragging rectangles on any window, then exporting to profile JSON. Not built in Phase 1.

## IPC Server: `CursorHideService`

EyeCtrl runs a named pipe server (`eyectrl_cursor_hide`) with a simple text protocol:
```
cursor:hide
cursor:show
```

When any connected SDK client signals `cursor:hide`, EyeCtrl hides the gaze cursor ring (the `CursorCircleWindow`). When all clients have signaled `cursor:show`, the ring reappears.

## Files Changed / Created

| File | New/Change | Purpose |
|------|------------|---------|
| `src/EyeCtrl.Sdk/EyeCtrl.Sdk.csproj` | New | SDK project, produces NuGet package |
| `src/EyeCtrl.Sdk/Dwell.cs` | New | Attached properties (Enabled, Time, Radius, Mode) |
| `src/EyeCtrl.Sdk/DwellManager.cs` | New | Per-app dwell engine: registration lifecycle, cursor polling, screen-coord resolution |
| `src/EyeCtrl.Sdk/DwellCursorNotifier.cs` | New | Named pipe client for cursor hide/show signals |
| `src/EyeCtrl.Core/Processing/DwellDetector.cs` | Change | Make non-UI-thread-safe or expose shared instance for SDK embedding |
| `src/EyeCtrl.App/Services/ProfileDwellService.cs` | New | Loads JSON profiles, matches windows, creates EyeCtrl-side DwellTargets |
| `src/EyeCtrl.App/Services/CursorHideService.cs` | New | Named pipe server for cursor visibility signals |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Change | Register new services in DI |
| `tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs` | Change | Add tests for SDK embedding scenario (non-UI-thread) |
| `tests/EyeCtrl.Sdk.Tests/` | New | SDK unit tests |

## Non-Goals (for this phase)

- Zone editor visual tool (Phase 2)
- Gaze data streaming to SDK (future)
- Cross-platform support (Windows-only)
- Non-WPF app support in SDK (WinForms, etc.)
- UI Automation auto-discovery
- Per-app dwell time/cooldown configuration in profiles

## Testing Strategy

- **DwellDetector**: existing tests pass; add concurrency tests for SDK embedding scenario
- **SDK**: test attached property lifecycle (register on load, unregister on unload, screen-coord resolution)
- **ProfileDwellService**: test JSON parsing, window matching, target creation/destruction
- **CursorHideService**: test pipe connect/disconnect, single vs multi-client visibility state
- **Integration**: manual smoke test with a sample WPF app using `eyectrl:Dwell.Enabled="True"`
