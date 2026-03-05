# Tobii Provider Hardening & Stream Engine Alternative

## Context

EyeCtrl currently uses `TobiiInteractionLibProvider` (via `tobii_interaction_lib_cs.dll`) with a simple try/catch fallback to `MouseGazeProvider`. The provider has no reconnect logic, no error deduplication, and uses the device timestamp which drifts ~5ms/6min.

OptiKey's (now-removed) Tobii Stream Engine implementation demonstrates reconnect, error dedup, and clock drift mitigation. The Stream Engine DLLs (`Tobii.StreamEngine.Net.dll`, `tobii_stream_engine.dll`) already ship with Tobii Dynavox Computer Control — no extra distribution needed.

## Design

### GazeProviderType Enum

`EyeCtrl.Core/Enums/GazeProviderType.cs`:

```
Auto | InteractionLib | StreamEngine | Mouse
```

`Auto` tries InteractionLib -> StreamEngine -> Mouse at startup, catching constructor exceptions at each step. Explicit types create that specific provider and throw on failure.

### GeneralSettings

Add `GazeProviderType` property (default: `Auto`).

### GazeProviderFactory

`EyeCtrl.Platform/Gaze/GazeProviderFactory.cs`:

```csharp
IGazeProvider Create(GazeProviderType type, double w, double h)
```

- `Auto`: try each provider in order, log each attempt and which one succeeded
- Explicit type: create that one, throw if unavailable
- Replaces the try/catch block in `App.xaml.cs`

### TobiiInteractionLibProvider Hardening

Four changes:

1. **Reconnect** — on exception in PollLoop, dispose and recreate `_interactionLib`, re-subscribe the event, re-configure the display area. Back-off: 500ms -> 1s -> 2s, capped at 5s. On success, reset back-off. Never gives up (handles physical disconnect/reconnect).

2. **Error dedup** — track `_lastExceptionMessage`. Only log when the message changes.

3. **Clock drift** — replace `data.timestamp_us` with `DateTimeOffset.UtcNow.Ticks` in emitted `GazePoint`. Device clock drifts and would cause any TTL/age filtering to silently discard data after extended use.

4. **IsConnected** — toggles to `false` during reconnect attempts, `true` on successful poll.

### TobiiStreamEngineProvider

New `EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs` implementing `IGazeProvider`.

**Init** (constructor, fail-fast):
- `tobii_api_create` -> `tobii_enumerate_local_device_urls` -> `tobii_device_create`
- Throws if no device found

**Start**:
- `tobii_gaze_point_subscribe` with callback
- Poll thread: `tobii_wait_for_callbacks` (blocking) -> `tobii_device_process_callbacks`

**Gaze callback**:
- Normalized coordinates (0-1) multiplied by screen dimensions
- `DateTimeOffset.UtcNow.Ticks` for timestamp
- Filter by `validity == TOBII_VALIDITY_VALID`

**Reconnect** (same pattern as InteractionLib):
- On error: `tobii_device_reconnect(deviceContext)`, back-off sleep, never gives up

**Cleanup** (`Stop`/`Dispose`):
- Unsubscribe, destroy device, destroy API
- Lock around cleanup to avoid racing with poll thread

### Toolbar Indicator

Three-state indicator showing which engine is active: InteractionLib, StreamEngine, or Mouse.

### Libs

Copy `Tobii.StreamEngine.Net.dll` from Tobii Dynavox directory to `libs/` and reference from `EyeCtrl.Platform`.

## Files Changed

```
New:
  src/EyeCtrl.Core/Enums/GazeProviderType.cs
  src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs
  src/EyeCtrl.Platform/Gaze/GazeProviderFactory.cs
  libs/Tobii.StreamEngine.Net.dll

Modified:
  src/EyeCtrl.Platform/Gaze/TobiiInteractionLibProvider.cs  (hardening)
  src/EyeCtrl.Core/Settings/GeneralSettings.cs               (add GazeProviderType)
  src/EyeCtrl.App/App.xaml.cs                                (use factory)
  src/EyeCtrl.App/ toolbar indicator                         (three-state)

Tests:
  GazeProviderFactory returns MouseGazeProvider on Auto when no hardware
  GazeProviderFactory returns MouseGazeProvider on explicit Mouse
  GazeProviderFactory throws on explicit InteractionLib when unavailable
```

## Testing

Unit tests cover factory logic (the testable seam). Tobii SDK calls are hardware-bound and tested manually: Auto connects, unplug/replug resumes, setting override works, indicator shows correct engine.
