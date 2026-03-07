# Logging Design

**Date:** 2026-03-07
**Goal:** Add comprehensive event-only file logging for daily-driver debugging using Serilog.

## Problem

The app has almost no logging. When something goes wrong (wrong click coordinates, unexpected state, Tobii disconnect), there is no record of what happened. The only existing logging is a few `System.Diagnostics.Trace` calls in the Platform gaze providers.

## Approach

Event-only logging: startup/shutdown, gaze provider connection changes, interaction state transitions, every action executed, keyboard/settings events, and unhandled exceptions. No gaze point or dwell progress logging (too noisy). Serilog writes a rolling daily file to `%AppData%\EyeCtrl\logs\`.

## Architecture

### Layer responsibilities

**`EyeCtrl.Platform`** — adds `Microsoft.Extensions.Logging.Abstractions`. `TobiiInteractionLibProvider` and `GazeProviderFactory` accept `ILogger<T>`, replacing existing `Trace.*` calls.

**`EyeCtrl.Core`** — no logging dependency added. State changes are already exposed via events (`InteractionManager.StateChanged`, `FixationDetector.FixationStarted/Ended`) which App subscribes to.

**`EyeCtrl.App`** — configures Serilog, registers `ILoggerFactory` in DI, logs App-layer events directly.

### Packages

| Project | Package |
|---|---|
| `EyeCtrl.Platform` | `Microsoft.Extensions.Logging.Abstractions` |
| `EyeCtrl.App` | `Serilog.Sinks.File`, `Serilog.Extensions.Logging` |

## What Gets Logged

### Startup / shutdown
- App version, settings file path, selected gaze provider type
- Clean shutdown with elapsed time

### Gaze provider (Platform layer)
- Provider selected: type and reason (e.g., fallback from Tobii IL to Stream Engine)
- Tobii disconnect: error message (deduplicated — already in existing code)
- Tobii reconnect

### Interaction state transitions (App layer, via `InteractionManager.StateChanged`)
- Every transition: `State → ZoomActive`, `State → Paused`, `State → FreeLook`, etc.

### Actions executed (App layer, `ActionRequestedMessage` handler)
- Action type and final coordinates: `LeftClick at (1234, 567)`
- Whether coordinates came from zoom or normal centroid path

### Keyboard & settings
- Keyboard shown / hidden
- Settings window opened, settings saved

### Errors
- Unhandled exceptions via `AppDomain.CurrentDomain.UnhandledException` and `Application.DispatcherUnhandledException`
- Caught exceptions that currently fail silently (e.g., layout file not found, dictionary file missing)

### Not logged
- Individual gaze points
- Dwell progress updates
- Fixation start/end events
- PropertyChanged notifications

## Log Format

Plain text, human-readable:

```
2026-03-07 14:23:01.234 [INF] [App] Startup: EyeCtrl v1.0, provider=TobiiInteractionLib, settings=C:\Users\...\EyeCtrl\settings.json
2026-03-07 14:23:02.891 [INF] [GazeEngineAdapter] State → ZoomActive
2026-03-07 14:23:03.105 [INF] [App] Action: LeftClick at (1234, 567) [zoom=False]
2026-03-07 14:23:05.002 [WRN] [TobiiInteractionLibProvider] Disconnected: timeout reading gaze data
2026-03-07 14:23:10.441 [INF] [TobiiInteractionLibProvider] Reconnected
2026-03-07 14:23:44.812 [ERR] [App] Unhandled exception: NullReferenceException in GazeEngine...
```

Output template:
```
{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}
```

## File Management

- **Location:** `%AppData%\EyeCtrl\logs\eyectrl-.log` (same `_appDataDir` as settings and dictionaries)
- **Rotation:** new file each calendar day (`RollingInterval.Day`)
- **Retention:** 7 days (`retainedFileCountLimit: 7`)
- Example filename: `eyectrl-20260307.log`

## Implementation Touch Points

### `EyeCtrl.Platform.csproj`
Add `Microsoft.Extensions.Logging.Abstractions`.

### `GazeProviderFactory`
Accept `ILogger<GazeProviderFactory>` constructor parameter. Replace `Trace.TraceInformation/Warning` calls with `_logger.LogInformation/Warning`.

### `TobiiInteractionLibProvider`
Accept `ILogger<TobiiInteractionLibProvider>` constructor parameter. Replace `Trace.TraceError` with `_logger.LogWarning` (disconnect) and `_logger.LogInformation` (reconnect).

### `TobiiStreamEngineProvider`
Accept `ILogger<TobiiStreamEngineProvider>` constructor parameter. Replace `Trace.TraceError` calls.

### `EyeCtrl.App.csproj`
Add `Serilog.Sinks.File` and `Serilog.Extensions.Logging`.

### `App.xaml.cs` — Serilog configuration (before DI build)
```csharp
var logsDir = Path.Combine(_appDataDir, "logs");
Directory.CreateDirectory(logsDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(logsDir, "eyectrl-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

### `App.xaml.cs` — DI registration
```csharp
services.AddLogging(builder => builder.AddSerilog(dispose: true));
```

### `App.xaml.cs` — log sites
- `OnStartup`: log version, settings path, provider type
- `ActionRequestedMessage` handler: log action + coordinates
- `KeyboardToggledMessage` handler: log keyboard shown/hidden
- `SettingsChanged` handler: log settings saved
- `OnExit`: log shutdown
- `AppDomain.CurrentDomain.UnhandledException`: log fatal exception
- `Application.DispatcherUnhandledException`: log UI-thread exception

### `GazeEngineAdapter`
Accept `ILogger<GazeEngineAdapter>`. Log each `InteractionManager.StateChanged` event.

### `OnExit`
Call `Log.CloseAndFlush()` before `base.OnExit(e)`.
