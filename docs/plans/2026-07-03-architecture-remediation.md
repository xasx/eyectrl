# EyeCtrl Architecture Remediation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This project is indexed by **GitNexus** — per `CLAUDE.md`, run `impact({target, direction:"upstream"})` before editing any symbol and `detect_changes()` before each commit. Tasks that touch high-fan-in symbols (notably `DwellDetector.Update`, `PhysicalPoint`, `GazePoint`, `EyeCtrlSettings`) call this out explicitly.

**Goal:** Fix every issue surfaced by the architecture audit — correctness bugs, threading/lifecycle hazards, DPI defects, and cross-project code duplication — across all 10 source projects, with regression tests, while preserving the app's clean layering.

**Architecture:** Work is split into four phases run in dependency order — **A (Core & Platform) → D (ViewModels & Settings) → C (SDK, EyeCare & LM) → B (App layer)**. Earlier phases publish contracts (new `PhysicalPoint` geometry helpers, `EyeCtrlSettings.EnumerateSubSettings()`, the `ScrollOverlayViewModel` ctor, `Contracts.WireJson`) that later phases consume. Every task is test-first (xUnit v3 + NFluent), bite-sized, and ends in a commit; purely mechanical or UI-only changes substitute a build/manual-verification step for the failing test.

**Tech Stack:** C# / .NET 10 (some SDK projects target net8.0 intentionally), WPF + WinUI SDK heads, CommunityToolkit.Mvvm, xUnit v3, NFluent, Xunit.StaFact, Tobii Stream Engine (native, `#if TOBII_AVAILABLE`), ONNX Runtime (DirectML). Solution file: `EyeCtrl.slnx`.

---

## Execution order & cross-phase contracts

Run the phases **A → D → C → B**. Within a phase, run tasks in numeric order. The ordering is driven by these hard dependencies:

| Contract (produced) | Producer | Consumers | Why the order matters |
|---|---|---|---|
| `PhysicalPoint.DistanceSquaredTo(PhysicalPoint)` / `DistanceTo(PhysicalPoint)` | **A12** | D15 (VM distance migration), B8 (`DwellDetector` filter overload) | Method must exist before any call site compiles. |
| `EyeCtrlSettings.EnumerateSubSettings() : IEnumerable<(string Name, object Settings)>` | **D8** | B3 (Reset-ALL delegation) | B's reset button delegates to the enumerator-driven `ResetAllSettings()`. |
| `ScrollOverlayViewModel(IMessenger, ScrollSettings, DwellSettings)` ctor + `DwellSettings.ScrollStopButtonDwellMs` | **D10** | B12 (`ScrollModeController`) | B constructs the VM with the new 3-arg signature. |
| Kept keyboard messages `ShowKeyboardMessage`/`HideKeyboardMessage` | **D7** (keeps, documents) | B17 (EventCoordinator sends them) | B makes `KeyboardViewModel.IsVisible` truthful via these types. |
| `EyeCtrl.Contracts.WireJson.Options` | **C13** (also swaps `ExternalDwellService.cs:29`) | B9 (rewrites the rest of `ExternalDwellService`) | C must land its one-line change before B rewrites the file, so B preserves it. |
| `DwellDetector.Update(point, ts, Func<DwellTarget,bool>?)` overload | **B8** (owns the Core change) | B9 (external-dwell rewrite) | Additive overload; 2-arg path unchanged. HIGH fan-in — run `impact` first. |

**Phase independence:** C (SDK/EyeCare/LM) shares no code with A or D and may run any time before B; it is scheduled third only to keep B last. If parallelizing across worktrees, A and C can proceed concurrently; D needs A; B needs all three.

**Note on task labels:** tasks keep their phase-letter prefixes (A1–A20, D1–D24, C1–C16, B1–B24) — 84 tasks total. They are presented below in execution order (A, then D, then C, then B).

---



## Phase A: Core & Platform

This phase fixes seven verified bugs and performs twelve refactor/cleanup passes across `src/EyeCtrl.Platform/` and `src/EyeCtrl.Core/` (excluding `Core/Settings/` and `DwellDetector.cs` API changes, which other sections own). Bugs come first: Tobii sample dropping/timestamps, a shutdown stall in the Tobii poll loop, the dead Auto→Mouse fallback, spatially unbalanced calibration targets, the ModelDownloadService triplication + broken CancelDownload, and FileDictionaryProvider's drifted exception handling. **Other phases consume from this phase:** Task A12 adds `public double DistanceSquaredTo(PhysicalPoint other)` and `public double DistanceTo(PhysicalPoint other)` to `src/EyeCtrl.Core/Models/PhysicalPoint.cs` — these exact signatures are depended on by other plan sections; land A12 before any section that references them.

**Environment facts verified 2026-07-02/03 on this machine:**
- Tobii SDK present: `C:\Program Files\Tobii\Tobii EyeX\Tobii.StreamEngine.Net.dll` + `tobii_stream_engine.dll` both exist → `TOBII_AVAILABLE` **is defined** in local builds (condition in `EyeCtrl.Platform.csproj:25-38`). Tobii code compiles locally; runtime behavior on the real PCEye 5 still needs manual verification (noted per task).
- `Tobii.StreamEngine.tobii_gaze_point_t` has fields `Int64 timestamp_us`, `tobii_validity_t validity`, `TobiiVector2 position` (verified by reflecting the SDK DLL).
- Tests: xUnit v3 + NFluent (`Check.That(...)`, never `Assert.*`). `Xunit` and `NFluent` are global usings via `tests/Directory.Build.props`.
- Build: `dotnet build EyeCtrl.slnx`. Test filter form: `dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~<TestClass>"`.

**GitNexus compliance (CLAUDE.md):** before editing each symbol run `impact({target: "<symbol>", direction: "upstream"})` and report blast radius; run `detect_changes()` before every commit. These apply to every task below even where not restated.

---

### Task A1: Tobii provider — forward invalid samples and use device timestamps

Finding 1 + finding 2 (same 13-line callback). Verified current code at `TobiiStreamEngineProvider.cs:131-143`: only VALID samples fire `GazePointReceived`, and the timestamp is fabricated from wall clock at millisecond resolution. Verified all `TimestampMicroseconds` consumers (OneEuroFilter dt, GapInterpolator gap length, FixationDetector/CentroidTracker durations, EyeCareScheduler/FatigueMonitor windows) use timestamp *differences* only — no wall-clock comparison exists, so the device epoch is safe. (The 2026-03-06 hardening doc chose wall clock to protect hypothetical TTL/age filtering; none was ever built.)

**Files:**
- Modify: `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs:131-143`
- Test: none possible (code is inside `#if TOBII_AVAILABLE` and behavior needs hardware) — build + full-suite verification instead, plus manual device check.

- [ ] **Step 1: Confirm TOBII_AVAILABLE compiles locally**
  ```
  dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj -v n | Select-String "TOBII"
  ```
  Expect the build to succeed. (SDK DLLs verified present at `C:\Program Files\Tobii\Tobii EyeX\`. If this ever moves, verification of this task becomes build-only-with-SDK.)

- [ ] **Step 2: Implement.** Replace the callback body. Current code (lines 131-143):
  ```csharp
        _gazeCallback = (ref tobii_gaze_point_t gazePoint, IntPtr userData) =>
        {
            if (gazePoint.validity == tobii_validity_t.TOBII_VALIDITY_VALID)
            {
                // Stream Engine gives normalized 0..1 coords; convert to screen pixels
                var timestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                GazePointReceived?.Invoke(new GazePoint(
                    _screenWidth * gazePoint.position.x,
                    _screenHeight * gazePoint.position.y,
                    timestampUs,
                    true));
            }
        };
  ```
  New code:
  ```csharp
        _gazeCallback = (ref tobii_gaze_point_t gazePoint, IntPtr userData) =>
        {
            // Device timestamp: monotonic microseconds from the tracker clock (arbitrary
            // epoch). The pipeline only ever consumes timestamp DIFFERENCES (OneEuroFilter
            // dt, GapInterpolator gap length, fixation durations), so the epoch is
            // irrelevant — but the previous wall-clock fabrication
            // (UtcNow.ToUnixTimeMilliseconds() * 1000) had only 1 ms resolution, causing
            // dt jitter/zero divisions in the OneEuroFilter at ~33 Hz.
            if (gazePoint.validity == tobii_validity_t.TOBII_VALIDITY_VALID)
            {
                // Stream Engine gives normalized 0..1 coords; convert to screen pixels
                GazePointReceived?.Invoke(new GazePoint(
                    _screenWidth * gazePoint.position.x,
                    _screenHeight * gazePoint.position.y,
                    gazePoint.timestamp_us,
                    true));
            }
            else
            {
                // Forward invalid samples (eyes lost / blink) instead of dropping them.
                // GapInterpolator blink handling and EyeCare away-detection both key off
                // GazePoint.IsValid == false and never saw it from the real device before.
                GazePointReceived?.Invoke(new GazePoint(0, 0, gazePoint.timestamp_us, false));
            }
        };
  ```

- [ ] **Step 3: Build + run full Platform and Core suites**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "Category!=UI"
  ```

- [ ] **Step 4: MANUAL VERIFICATION (device required — flag for the user):** run the app on the PCEye 5; blink deliberately: cursor should hold position (gap interpolation) and dwell progress should suppress during the blink instead of the cursor freezing on a stale point; verify eye-care away-detection now triggers when looking away. Verify cursor motion feels smoother (real dt into the 1€ filter).

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs
  git commit -m "fix(tobii): forward invalid gaze samples and use device timestamps

  Invalid samples never reached Core, disabling GapInterpolator blink
  handling and EyeCare away-detection on real hardware. Wall-clock ms
  timestamps caused dt jitter in the OneEuroFilter; use the tracker's
  monotonic timestamp_us instead (pipeline is difference-based only).

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A2: Tobii provider — stop holding `_lock` across sleeps; make Stop() prompt

Finding 3, verified: `PollLoop` (lines 151-198) takes `lock (_lock)` around the whole iteration, including `Thread.Sleep(backoffMs)` (up to 5000 ms) and the blocking `tobii_wait_for_callbacks`. `Stop()` (lines 59-72) does `Join(1000)` then `lock (_lock)` — with no device connected it can stall multiple seconds at shutdown.

**Files:**
- Modify: `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs:20-72, 151-198, 232-236`
- Test: `tests/EyeCtrl.Platform.Tests/Gaze/TobiiStreamEngineProviderStopTests.cs` (new, `#if TOBII_AVAILABLE`)

- [ ] **Step 1: Write the regression-guard test** (new file `tests/EyeCtrl.Platform.Tests/Gaze/TobiiStreamEngineProviderStopTests.cs`):
  ```csharp
  #if TOBII_AVAILABLE
  namespace EyeCtrl.Platform.Tests.Gaze;

  using System.Diagnostics;
  using EyeCtrl.Platform.Gaze;
  using Microsoft.Extensions.Logging.Abstractions;

  public class TobiiStreamEngineProviderStopTests
  {
      [Fact]
      public void Stop_ReturnsPromptly_EvenDuringReconnectBackoff()
      {
          var provider = new TobiiStreamEngineProvider(1920, 1080,
              NullLogger<TobiiStreamEngineProvider>.Instance);
          provider.Start();

          // Let the poll loop run: with no device it enters exponential backoff
          // (sleeps grow toward 5 s); with a device it sits in wait_for_callbacks
          // (~230 ms internal timeout). Either way Stop() must return promptly.
          Thread.Sleep(1500);

          var sw = Stopwatch.StartNew();
          provider.Stop();
          sw.Stop();

          // Pre-fix, with no device connected, Stop() could stall ~6 s
          // (Join timeout + a Thread.Sleep(backoffMs) held under _lock).
          Check.That(sw.ElapsedMilliseconds).IsStrictlyLessThan(2500);
          provider.Dispose();
      }
  }
  #endif
  ```

- [ ] **Step 2: Run test to verify it fails**
  ```
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj --filter "FullyQualifiedName~TobiiStreamEngineProviderStopTests"
  ```
  Expected failure **only when no Tobii device is connected** (backoff path active): elapsed > 2500 ms. **NOTE:** on this machine the PCEye 5 is usually connected, so the poll loop is in `wait_for_callbacks` and the test may pass trivially pre-fix. If it passes, unplug the tracker (or accept the test as a regression guard) and rely on Step 3's code-review of lock scope as the fail-first evidence. Record which case occurred.

- [ ] **Step 3: Implement.** Three edits in `TobiiStreamEngineProvider.cs`.

  (a) Add a stop signal next to `_lock` (line 24). Current:
  ```csharp
    private readonly object _lock = new();
  ```
  New:
  ```csharp
    private readonly object _lock = new();
    // Signaled by Stop(): aborts reconnect/backoff waits promptly instead of
    // sleeping out the full backoff. Reset on Start() so the provider is restartable.
    private readonly ManualResetEventSlim _stopSignal = new(false);
  ```

  (b) `Start()`/`Stop()`. Current (lines 46-72):
  ```csharp
    public void Start()
    {
        TryFullConnect();

        _running = true;
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "TobiiStreamEnginePoll"
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _pollThread?.Join(1000);
        _pollThread = null;

        lock (_lock)
        {
            CleanupDevice();
            CleanupApi();
        }

        IsConnected = false;
    }
  ```
  New:
  ```csharp
    public void Start()
    {
        _stopSignal.Reset();
        TryFullConnect();

        _running = true;
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "TobiiStreamEnginePoll"
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _stopSignal.Set();          // aborts any backoff wait immediately
        _pollThread?.Join(2000);    // wait_for_callbacks returns within ~230 ms internally
        _pollThread = null;

        lock (_lock)
        {
            CleanupDevice();
            CleanupApi();
        }

        IsConnected = false;
    }
  ```

  (c) `PollLoop()` — hold `_lock` only around interop touching `_deviceContext`/`_apiContext`; all waits outside the lock and abortable. Current (lines 151-198):
  ```csharp
    private void PollLoop()
    {
        int backoffMs = 500;
        const int maxBackoffMs = 5000;

        while (_running)
        {
            lock (_lock)
            {
                if (_apiContext == IntPtr.Zero || _deviceContext == IntPtr.Zero)
                {
                    // Not connected — attempt full reconnect with backoff
                    Thread.Sleep(backoffMs);
                    backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
                    TryFullConnect();
                    continue;
                }

                tobii_error_t result = Interop.tobii_wait_for_callbacks(
                    new[] { _deviceContext });
                bool hasError = IsError(result);

                if (!hasError)
                {
                    result = Interop.tobii_device_process_callbacks(_deviceContext);
                    hasError = IsError(result);
                }

                if (hasError)
                {
                    IsConnected = false;
                    Thread.Sleep(backoffMs);
                    backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
                    Interop.tobii_device_reconnect(_deviceContext);
                }
                else
                {
                    if (!IsConnected)
                    {
                        IsConnected = true;
                        _lastErrorMessage = null;
                        backoffMs = 500;
                        _logger.LogInformation("Connected");
                    }
                }
            }
        }
    }
  ```
  New:
  ```csharp
    private void PollLoop()
    {
        int backoffMs = 500;
        const int maxBackoffMs = 5000;

        while (_running)
        {
            // Lock scope is limited to interop calls that touch _deviceContext /
            // _apiContext. Backoff waits happen OUTSIDE the lock and abort as soon
            // as Stop() signals, so shutdown never stalls behind a sleep.
            bool connected;
            lock (_lock)
            {
                connected = _apiContext != IntPtr.Zero && _deviceContext != IntPtr.Zero;
            }

            if (!connected)
            {
                if (_stopSignal.Wait(backoffMs)) return;
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
                lock (_lock)
                {
                    if (_running) TryFullConnect();
                }
                continue;
            }

            bool hasError;
            lock (_lock)
            {
                if (!_running) return;
                tobii_error_t result = Interop.tobii_wait_for_callbacks(
                    new[] { _deviceContext });
                hasError = IsError(result);

                if (!hasError)
                {
                    result = Interop.tobii_device_process_callbacks(_deviceContext);
                    hasError = IsError(result);
                }
            }

            if (hasError)
            {
                IsConnected = false;
                if (_stopSignal.Wait(backoffMs)) return;
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
                lock (_lock)
                {
                    if (_running && _deviceContext != IntPtr.Zero)
                        Interop.tobii_device_reconnect(_deviceContext);
                }
            }
            else if (!IsConnected)
            {
                IsConnected = true;
                _lastErrorMessage = null;
                backoffMs = 500;
                _logger.LogInformation("Connected");
            }
        }
    }
  ```
  (Note: `TryFullConnect` and its helpers only touch the contexts, which is why they are now called under the lock explicitly — previously the whole loop body was under it. `SubscribeGaze`'s callback assignment is unaffected.)

  (d) `Dispose()`. Current (lines 232-235):
  ```csharp
    public void Dispose()
    {
        Stop();
    }
  ```
  New:
  ```csharp
    public void Dispose()
    {
        Stop();
        _stopSignal.Dispose();
    }
  ```

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```
  All Platform tests including `Create_StreamEngine_StartDoesNotThrow_WhenNoDeviceConnected` and the new stop test must pass. **MANUAL:** with the device attached, quit the app via tray Exit — should exit instantly; unplug the tracker mid-session and exit — still instant.

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs tests/EyeCtrl.Platform.Tests/Gaze/TobiiStreamEngineProviderStopTests.cs
  git commit -m "fix(tobii): don't hold _lock across backoff sleeps; make Stop() prompt

  PollLoop slept up to 5s holding _lock, so Stop() could stall seconds at
  shutdown. Lock now covers only interop calls; backoff waits on a
  ManualResetEventSlim that Stop() signals.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A3: GazeProviderFactory.CreateAuto — probe for a device instead of dead try/catch

Finding 4, verified: `TobiiStreamEngineProvider`'s constructor (lines 38-44) only assigns three fields — it cannot throw — so the `try/catch` in `CreateAuto` (`GazeProviderFactory.cs:54-72`) is dead and `Auto` never falls back to Mouse when the SDK is compiled in but no device is attached.

**Files:**
- Modify: `src/EyeCtrl.Platform/Gaze/GazeProviderFactory.cs:54-72`
- Test: `tests/EyeCtrl.Platform.Tests/Gaze/GazeProviderFactoryTests.cs` (existing tests must keep passing; a true fail-first unit test is impossible — device enumeration is native interop with no seam. Build + existing tests + manual verification instead.)

- [ ] **Step 1: Re-read existing tests** `tests/EyeCtrl.Platform.Tests/Gaze/GazeProviderFactoryTests.cs` — `Create_Auto_ReturnsValidProvider` already accepts either `StreamEngine` or `Mouse`, so it stays valid on machines with and without a device.

- [ ] **Step 2: Baseline: run existing factory tests**
  ```
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj --filter "FullyQualifiedName~GazeProviderFactoryTests"
  ```
  Expect all pass (baseline).

- [ ] **Step 3: Implement.** Current code (`GazeProviderFactory.cs:54-72`):
  ```csharp
    private IGazeProvider CreateAuto(double screenWidth, double screenHeight)
    {
  #if TOBII_AVAILABLE
        try
        {
            var provider = new TobiiStreamEngineProvider(screenWidth, screenHeight,
                _loggerFactory.CreateLogger<TobiiStreamEngineProvider>());
            _logger.LogInformation("Using Tobii Stream Engine");
            return provider;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("StreamEngine unavailable: {Message}", ex.Message);
        }
  #endif

        _logger.LogInformation("Falling back to Mouse simulation");
        return new MouseGazeProvider();
    }
  ```
  New code (also add `using Tobii.StreamEngine;` guarded by `#if TOBII_AVAILABLE` at the top of the file, after the existing usings):
  ```csharp
    private IGazeProvider CreateAuto(double screenWidth, double screenHeight)
    {
  #if TOBII_AVAILABLE
        // The provider constructor only assigns fields and never throws (connection is
        // deferred to Start/poll loop), so choosing Tobii must be gated on an explicit
        // device probe — otherwise Auto can never fall back to Mouse.
        if (TobiiDeviceIsPresent())
        {
            var provider = new TobiiStreamEngineProvider(screenWidth, screenHeight,
                _loggerFactory.CreateLogger<TobiiStreamEngineProvider>());
            _logger.LogInformation("Using Tobii Stream Engine");
            return provider;
        }
        _logger.LogWarning("No Tobii device found during probe");
  #endif

        _logger.LogInformation("Falling back to Mouse simulation");
        return new MouseGazeProvider();
    }

  #if TOBII_AVAILABLE
    /// <summary>
    /// Probes for a connected Tobii device: creates a throwaway API context,
    /// enumerates local device URLs, and destroys the context again.
    /// </summary>
    private bool TobiiDeviceIsPresent()
    {
        IntPtr api = IntPtr.Zero;
        try
        {
            if (Interop.tobii_api_create(out api, null) != tobii_error_t.TOBII_ERROR_NO_ERROR)
                return false;
            var result = Interop.tobii_enumerate_local_device_urls(api, out List<string> urls);
            return result == tobii_error_t.TOBII_ERROR_NO_ERROR && urls.Count > 0;
        }
        catch (Exception ex) // e.g. DllNotFoundException if the native DLL is missing at runtime
        {
            _logger.LogWarning("Tobii device probe failed: {Message}", ex.Message);
            return false;
        }
        finally
        {
            if (api != IntPtr.Zero) Interop.tobii_api_destroy(api);
        }
    }
  #endif
  ```
  Non-TOBII builds are untouched (`Create` for `StreamEngine` still throws `PlatformNotSupportedException`; `InteractionLib`/`Auto` fall through to Mouse) — graceful behavior preserved.

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```
  **MANUAL (flag for user):** with the PCEye 5 attached, `Auto` must still select StreamEngine (check toolbar's active-engine indicator); with the tracker unplugged, `Auto` must fall back to Mouse instead of a permanently-reconnecting Tobii provider.

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Platform/Gaze/GazeProviderFactory.cs
  git commit -m "fix(platform): Auto provider now probes for a Tobii device before choosing it

  The try/catch around the provider ctor was dead (ctor only assigns
  fields), so Auto never fell back to Mouse. Probe api-create +
  enumerate-device-urls and fall back when no device is found.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A4: CalibrationTargetPositionHelper — spatially balanced target selection

Finding 5, verified: for counts 6-8 (3×3 grid) and 10-15 (4×4 grid), `all.Take(count)` (line 77) takes rows top-down, leaving the bottom of the screen without targets; the affine fit then extrapolates there.

**Files:**
- Modify: `src/EyeCtrl.Core/Calibration/CalibrationTargetPositionHelper.cs:61-77`
- Test: `tests/EyeCtrl.Core.Tests/Calibration/CalibrationTargetPositionHelperTests.cs`

- [ ] **Step 1: Write the failing test.** Append to the existing `CalibrationTargetPositionHelperTests` class (before the private `Dist` helper):
  ```csharp
    [Fact]
    public void GetPositions_5To16Points_AlwaysCoverAllFourScreenExtremes()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var minX = screen.X * 0.2; var maxX = screen.X * 0.8;
        var minY = screen.Y * 0.2; var maxY = screen.Y * 0.8;

        for (int count = 5; count <= 16; count++)
        {
            var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);

            Check.WithCustomMessage($"count={count}: top row (minY) missing")
                .That(positions.Any(p => Math.Abs(p.Y - minY) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: bottom row (maxY) missing")
                .That(positions.Any(p => Math.Abs(p.Y - maxY) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: left column (minX) missing")
                .That(positions.Any(p => Math.Abs(p.X - minX) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: right column (maxX) missing")
                .That(positions.Any(p => Math.Abs(p.X - maxX) < 1)).IsTrue();
        }
    }

    [Fact]
    public void GetPositions_IsDeterministic()
    {
        var screen = new PhysicalPoint(1920, 1080);
        for (int count = 3; count <= 16; count++)
        {
            var a = CalibrationTargetPositionHelper.GetPositions(count, screen);
            var b = CalibrationTargetPositionHelper.GetPositions(count, screen);
            Check.WithCustomMessage($"count={count} not deterministic")
                .That(a.SequenceEqual(b)).IsTrue();
        }
    }
  ```

- [ ] **Step 2: Run test to verify it fails**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~CalibrationTargetPositionHelperTests"
  ```
  Expected: `GetPositions_5To16Points_AlwaysCoverAllFourScreenExtremes` fails first at `count=6` with "bottom row (maxY) missing". `GetPositions_IsDeterministic` passes (already deterministic).

- [ ] **Step 3: Implement.** Replace line 77. Current:
  ```csharp
        var selected = all.Take(count).ToList();
  ```
  New (the sort/interleave ordering below line 77 stays untouched):
  ```csharp
        // Spatially balanced selection: when the grid has more points than requested,
        // seed with the 4 corners + center and then greedily add the grid point farthest
        // from everything already selected (deterministic; grid order breaks ties).
        // This guarantees the top/bottom rows and left/right columns are always
        // represented, so the affine fit never has to extrapolate a screen edge.
        // When count consumes the full grid (9 on 3x3, 16 on 4x4) the selected SET is
        // unchanged from before.
        List<PhysicalPoint> selected;
        if (count >= all.Count)
        {
            selected = new List<PhysicalPoint>(all);
        }
        else
        {
            selected = new List<PhysicalPoint>(count);
            var seeds = new[]
            {
                new PhysicalPoint(minX, minY), new PhysicalPoint(maxX, minY),
                new PhysicalPoint(minX, maxY), new PhysicalPoint(maxX, maxY),
                new PhysicalPoint(cx, cy),
            };
            foreach (var seed in seeds)
            {
                if (selected.Count >= count) break;
                PhysicalPoint nearest = default;
                double nearestDist = double.MaxValue;
                foreach (var p in all)
                {
                    if (selected.Contains(p)) continue;
                    double d = DistSq(p, seed);
                    if (d < nearestDist) { nearestDist = d; nearest = p; }
                }
                selected.Add(nearest);
            }
            while (selected.Count < count)
            {
                PhysicalPoint farthest = default;
                double farthestDist = -1;
                foreach (var p in all)
                {
                    if (selected.Contains(p)) continue;
                    double d = double.MaxValue;
                    foreach (var s in selected) d = Math.Min(d, DistSq(p, s));
                    if (d > farthestDist) { farthestDist = d; farthest = p; }
                }
                selected.Add(farthest);
            }
        }
  ```
  And add a private helper at the bottom of the class (before the closing brace):
  ```csharp
    private static double DistSq(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
  ```
  Notes: counts 3-5 use the explicit branches above and never reach this code. On the 4×4 grid there is no exact center point — the center seed deterministically picks the first of the four inner points (grid order tie-break). `PhysicalPoint` is a `readonly record struct` with value equality computed from identical expressions, so `Contains` is exact.

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~CalibrationTargetPositionHelperTests"
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~CalibrationServiceTests"
  ```
  All must pass, including the pre-existing 3/5/9-point and margin tests.

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Calibration/CalibrationTargetPositionHelper.cs tests/EyeCtrl.Core.Tests/Calibration/CalibrationTargetPositionHelperTests.cs
  git commit -m "fix(calibration): spatially balanced target selection for counts 6-8 and 10-15

  Row-major Take(count) left the bottom of the screen without calibration
  targets, forcing the affine fit to extrapolate. Corners + center are now
  seeded first, remaining slots filled farthest-first (deterministic).

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A5: ModelDownloadService — extract DownloadFileCoreAsync (part 1: core + model wrapper)

Finding 6, verified: three ~85-line near-identical routines at lines 144-228, 244-321, 330-398; `_downloadCts` is only created in `DownloadModelAsync` (line 161) so `CancelDownload()` (131-134) ignores tokenizer downloads. This task extracts the core and rewires `DownloadModelAsync`; Task A6 rewires the tokenizer overloads and fixes cancellation. Public API preserved exactly.

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/ModelDownloadService.cs:144-228` (+ new private members)
- Test: `tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs`

- [ ] **Step 1: Write characterization tests** (they must pass BEFORE and AFTER the refactor — this pins current behavior; the true fail-first test for the cancel bug is in Task A6). Append to `ModelDownloadServiceTests`:
  ```csharp
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;
        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request, ct));
    }

    [Fact]
    public async Task DownloadModelAsync_ValidPayloadAndHash_WritesFileAndReportsCompletion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("hello model payload");
            var expectedSha = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(payload));
            using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                }));
            using var service = new ModelDownloadService(tempDir, client);
            var events = new List<ModelDownloadService.ModelDownloadProgress>();
            service.ProgressChanged += p => events.Add(p);

            var ok = await service.DownloadModelAsync(
                "custom-x", "custom-x.onnx", "http://localhost/model.onnx", expectedSha);

            Check.That(ok).IsTrue();
            Check.That(File.ReadAllBytes(Path.Combine(tempDir, "custom-x.onnx")))
                .ContainsExactly(payload);
            Check.That(File.Exists(Path.Combine(tempDir, "custom-x.onnx.download"))).IsFalse();
            Check.That(events.Last().IsComplete).IsTrue();
            Check.That(events.Last().Error).IsNull();
            Check.That(events.First().Progress).IsEqualTo(0.0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DownloadModelAsync_HashMismatch_ReturnsFalseAndCleansUp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("hello model payload");
            using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                }));
            using var service = new ModelDownloadService(tempDir, client);
            var events = new List<ModelDownloadService.ModelDownloadProgress>();
            service.ProgressChanged += p => events.Add(p);

            var ok = await service.DownloadModelAsync(
                "custom-x", "custom-x.onnx", "http://localhost/model.onnx",
                expectedSha256: "deadbeef");

            Check.That(ok).IsFalse();
            Check.That(File.Exists(Path.Combine(tempDir, "custom-x.onnx"))).IsFalse();
            Check.That(File.Exists(Path.Combine(tempDir, "custom-x.onnx.download"))).IsFalse();
            Check.That(events.Last().Error).IsEqualTo("Hash mismatch");
            Check.That(events.Last().IsComplete).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
  ```
  Add `using System.Net.Http;` to the test file's usings if not implicit.

- [ ] **Step 2: Run tests — they must PASS against the current implementation** (characterization, not fail-first):
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~ModelDownloadServiceTests"
  ```
  If either fails, the current behavior differs from expectation — stop and re-read the code before refactoring.

- [ ] **Step 3: Implement.** Add the private core and rewrite `DownloadModelAsync` as a thin wrapper. Insert after `CancelDownload()` (line 134):
  ```csharp
    private enum DownloadOutcome { Success, Busy, HashMismatch, Failed }

    /// <summary>
    /// Shared download core used by all public download methods: single-flight
    /// semaphore, temp-file streaming with incremental SHA-256 via CryptoStream,
    /// optional hash verification, atomic move into place, temp-file cleanup.
    /// Creates the linked <see cref="_downloadCts"/> so <see cref="CancelDownload"/>
    /// works for every download type. Throws OperationCanceledException on cancel.
    /// </summary>
    private async Task<(DownloadOutcome Outcome, string? Error)> DownloadFileCoreAsync(
        string outputFileName,
        string url,
        string expectedSha256,
        Action? onStarted,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(_modelsDirectory, outputFileName);
        var tempPath = outputPath + ".download";

        if (!await _downloadSemaphore.WaitAsync(0, cancellationToken))
            return (DownloadOutcome.Busy, "Another download is already in progress");

        _isDownloading = true;
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _downloadCts.Token;

        try
        {
            onStarted?.Invoke();

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            using var sha256 = SHA256.Create();
            using (var fileStream = File.Create(tempPath))
            using (var cs = new CryptoStream(contentStream, sha256, CryptoStreamMode.Read, leaveOpen: true))
            {
                while ((bytesRead = await cs.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalRead += bytesRead;
                    if (totalBytes > 0)
                        onProgress?.Invoke((double)totalRead / totalBytes);
                }
            }
            var computedHash = Convert.ToHexStringLower(sha256.Hash!);

            // Integrity check is opt-in: an empty expectedSha256 skips it (HTTPS
            // transport trust). See DownloadModelAsync remarks.
            if (!string.IsNullOrEmpty(expectedSha256) &&
                !string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Hash mismatch for {File}: expected {Expected}, got {Actual}",
                    outputFileName, expectedSha256, computedHash);
                File.Delete(tempPath);
                return (DownloadOutcome.HashMismatch, "Hash mismatch");
            }

            File.Move(tempPath, outputPath, overwrite: true);
            _logger?.LogInformation("{File} downloaded successfully ({Bytes} bytes)", outputFileName, totalRead);
            return (DownloadOutcome.Success, null);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download {File}", outputFileName);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return (DownloadOutcome.Failed, ex.Message);
        }
        finally
        {
            _isDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            _downloadSemaphore.Release();
        }
    }
  ```
  Then replace the entire `DownloadModelAsync` body (lines 144-228, keeping the signature and the `<remarks>` doc comment verbatim) with:
  ```csharp
    public async Task<bool> DownloadModelAsync(
        string modelId,
        string outputFileName,
        string url,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var progress = ProgressChanged;
        try
        {
            var (outcome, error) = await DownloadFileCoreAsync(
                outputFileName, url, expectedSha256,
                onStarted: () => progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = 0 }),
                onProgress: p => progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = p }),
                cancellationToken);

            switch (outcome)
            {
                case DownloadOutcome.Success:
                    progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = 1.0, IsComplete = true });
                    return true;
                case DownloadOutcome.HashMismatch:
                    progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, IsComplete = true, Error = error });
                    return false;
                default: // Busy, Failed
                    progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Error = error });
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Model download cancelled: {Id}", modelId);
            ProgressChanged?.Invoke(new ModelDownloadProgress { ModelId = modelId, IsCancelled = true });
            throw;
        }
    }
  ```
  Behavioral notes (accepted, document in commit): core log messages now key on the output file name rather than model id; the Busy path previously fired its error event via a fresh `ProgressChanged` read — now via the captured `progress` local (same observable behavior for subscribers registered before the call).

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~ModelDownloadServiceTests"
  ```
  All characterization + pre-existing tests green.

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Prediction/ModelDownloadService.cs tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs
  git commit -m "refactor(prediction): extract DownloadFileCoreAsync from DownloadModelAsync

  First step of de-duplicating three ~85-line download routines. Public
  API and event sequence preserved (characterization tests added).

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A6: ModelDownloadService — tokenizer wrappers + CancelDownload fix (part 2)

Completes finding 6: both `DownloadTokenizerAsync` overloads (lines 244-321, 330-398) become thin wrappers over `DownloadFileCoreAsync`, which creates the linked `_downloadCts` — making `CancelDownload()` effective for tokenizer downloads (currently it is not).

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/ModelDownloadService.cs:244-398`
- Test: `tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs`

- [ ] **Step 1: Write the failing test.** Append to `ModelDownloadServiceTests`:
  ```csharp
    /// <summary>A read stream that produces data forever (until cancelled) — models a huge download.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(20);
            Array.Clear(buffer, offset, count);
            return count;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(20, ct);
            buffer.Span.Clear();
            return buffer.Length;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CancelDownload_CancelsTokenizerDownload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StreamContent(new EndlessStream())
                }));
            using var service = new ModelDownloadService(tempDir, client);

            var task = service.DownloadTokenizerAsync("360M", "http://localhost/tokenizer.json", "");

            // Wait for the download to actually start
            var start = Environment.TickCount64;
            while (!service.IsDownloading)
            {
                Check.That(Environment.TickCount64 - start).IsStrictlyLessThan(2000);
                await Task.Delay(10);
            }

            service.CancelDownload();

            // Before the fix _downloadCts is never created for tokenizer downloads,
            // so CancelDownload() is a no-op and the endless download never finishes.
            var completed = await Task.WhenAny(task, Task.Delay(5000));
            Check.That(completed).IsSameReferenceAs(task);

            OperationCanceledException? oce = null;
            try { await task; }
            catch (OperationCanceledException ex) { oce = ex; }
            Check.That(oce).IsNotNull();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
  ```

- [ ] **Step 2: Run test to verify it fails**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~ModelDownloadServiceTests.CancelDownload_CancelsTokenizerDownload"
  ```
  Expected failure: `Task.Delay(5000)` wins the `WhenAny` → `Check.That(completed).IsSameReferenceAs(task)` fails.

- [ ] **Step 3: Implement.** Replace both overloads entirely (preserving signatures and doc comments).

  Replace `DownloadTokenizerAsync(string modelSize, string url, string expectedSha256, IProgress<double>? progress, CancellationToken)` (current body lines 250-321) with:
  ```csharp
    public async Task<bool> DownloadTokenizerAsync(
        string modelSize,
        string url,
        string expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileName = GetTokenizerFileName(modelSize);
        _logger?.LogInformation("Downloading tokenizer {Size} from {Url} to {Path}",
            modelSize, url, Path.Combine(_modelsDirectory, fileName));

        try
        {
            var (outcome, _) = await DownloadFileCoreAsync(
                fileName, url, expectedSha256,
                onStarted: null,
                onProgress: p => progress?.Report(p),
                cancellationToken);

            if (outcome == DownloadOutcome.Busy)
                _logger?.LogWarning("Tokenizer download rejected: another download is already in progress");
            return outcome == DownloadOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Tokenizer download cancelled: {Size}", modelSize);
            throw;
        }
    }
  ```
  Replace `DownloadTokenizerAsync(string modelId, string outputFileName, string url, string expectedSha256, CancellationToken)` (current body lines 336-398) with:
  ```csharp
    public async Task<bool> DownloadTokenizerAsync(
        string modelId,
        string outputFileName,
        string url,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Downloading tokenizer {Id} from {Url} to {Path}",
            modelId, url, Path.Combine(_modelsDirectory, outputFileName));

        var (outcome, _) = await DownloadFileCoreAsync(
            outputFileName, url, expectedSha256,
            onStarted: null,
            onProgress: null,
            cancellationToken);

        if (outcome == DownloadOutcome.Busy)
            _logger?.LogWarning("Tokenizer download rejected: another download is already in progress");
        return outcome == DownloadOutcome.Success;
    }
  ```
  Then update the `CancelDownload` doc comment (line 130) — current:
  ```csharp
    /// <summary>Cancels the currently active download operation.</summary>
    public void CancelDownload()
  ```
  New:
  ```csharp
    /// <summary>Cancels the currently active download operation (model or tokenizer).</summary>
    public void CancelDownload()
  ```
  Behavior deltas (intentional/accepted): the "Downloading tokenizer …" log now fires before semaphore acquisition instead of after; tokenizer downloads are now cancellable via `CancelDownload()` (that is the fix); the second overload previously reported no progress and still doesn't (`onProgress: null`).

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~ModelDownloadServiceTests"
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "Category!=UI"
  ```

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Prediction/ModelDownloadService.cs tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs
  git commit -m "fix(prediction): CancelDownload now cancels tokenizer downloads too

  Both DownloadTokenizerAsync overloads are thin wrappers over
  DownloadFileCoreAsync, which owns the linked _downloadCts. Removes
  ~170 lines of triplicated download logic.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A7: FileDictionaryProvider — generic LoadJson/SaveJson with uniform exception handling

Finding 7, verified: five copy-pasted Load/Save/GetPath triples (lines 139-392) with drifted catches — `LoadBigrams` and `LoadPhrases` catch only `JsonException` (a locked file throws `IOException` out of `WordPredictor`'s ctor, lines 50-68), while the timestamp/casing variants also catch `IOException`/`UnauthorizedAccessException`. Every Save allocates `new JsonSerializerOptions { WriteIndented = true }`.

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs:125-392`
- Test: `tests/EyeCtrl.Core.Tests/Prediction/FileDictionaryProviderTests.cs`

- [ ] **Step 1: Write the failing test.** Append to `FileDictionaryProviderTests`:
  ```csharp
    [Fact]
    public void LoadBigrams_FileLocked_ReturnsEmptyInsteadOfThrowing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dict_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var provider = new FileDictionaryProvider(
                Path.Combine(tempDir, "base.json"), tempDir, "en");
            var bigramsPath = Path.Combine(tempDir, "user_dictionary_en_bigrams.json");
            File.WriteAllText(bigramsPath, """{"hello":{"world":1}}""");

            using var fileLock = new FileStream(
                bigramsPath, FileMode.Open, FileAccess.Read, FileShare.None);

            Dictionary<string, Dictionary<string, int>>? result = null;
            Check.ThatCode(() => result = provider.LoadBigrams()).DoesNotThrow();
            Check.That(result).IsEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadPhrases_FileLocked_ReturnsEmptyInsteadOfThrowing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dict_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var provider = new FileDictionaryProvider(
                Path.Combine(tempDir, "base.json"), tempDir, "en");
            var phrasesPath = Path.Combine(tempDir, "user_dictionary_en_phrases.json");
            File.WriteAllText(phrasesPath, """{"how are":{"how are you":1}}""");

            using var fileLock = new FileStream(
                phrasesPath, FileMode.Open, FileAccess.Read, FileShare.None);

            Dictionary<string, Dictionary<string, int>>? result = null;
            Check.ThatCode(() => result = provider.LoadPhrases()).DoesNotThrow();
            Check.That(result).IsEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
  ```

- [ ] **Step 2: Run test to verify it fails**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~FileDictionaryProviderTests"
  ```
  Expected: both new tests fail with an unhandled `IOException` ("being used by another process") escaping `LoadBigrams`/`LoadPhrases`.

- [ ] **Step 3: Implement.** Add the shared private members (place after the `Save` method, ~line 133):
  ```csharp
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    /// <summary>Builds the path of an extension file that lives next to the user dictionary
    /// (e.g. "user_dictionary_en" + "_bigrams.json").</summary>
    private string GetExtensionPath(string fileSuffix)
    {
        var directory = Path.GetDirectoryName(_userDictionaryPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(_userDictionaryPath);
        var fileName = fileNameWithoutExtension + fileSuffix;
        return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
    }

    /// <summary>
    /// Loads a JSON extension file, returning an empty instance when the file is missing,
    /// corrupt, locked, or unreadable. Uniform error handling for all extension data
    /// (bigrams, phrases, timestamps, casing) — a locked file must degrade gracefully,
    /// not throw out of WordPredictor's constructor.
    /// </summary>
    private T LoadJson<T>(string fileSuffix) where T : class, new()
    {
        var path = GetExtensionPath(fileSuffix);
        if (!File.Exists(path))
            return new T();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "Failed to load {Path}, returning empty data", path);
            return new T();
        }
    }

    private void SaveJson<T>(string fileSuffix, T data)
    {
        var path = GetExtensionPath(fileSuffix);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(data, s_writeOptions));
    }
  ```
  Replace the ten public extension methods with one-liners (keep every existing signature + XML doc comment):
  ```csharp
    public Dictionary<string, Dictionary<string, int>> LoadBigrams()
        => LoadJson<Dictionary<string, Dictionary<string, int>>>("_bigrams.json");

    public void SaveBigrams(Dictionary<string, Dictionary<string, int>> bigrams)
        => SaveJson("_bigrams.json", bigrams);

    public Dictionary<string, Dictionary<string, DateTimeOffset>> LoadBigramTimestamps()
        => LoadJson<Dictionary<string, Dictionary<string, DateTimeOffset>>>("_bigrams_timestamps.json");

    public void SaveBigramTimestamps(Dictionary<string, Dictionary<string, DateTimeOffset>> timestamps)
        => SaveJson("_bigrams_timestamps.json", timestamps);

    public Dictionary<string, Dictionary<string, int>> LoadPhrases()
        => LoadJson<Dictionary<string, Dictionary<string, int>>>("_phrases.json");

    public void SavePhrases(Dictionary<string, Dictionary<string, int>> phrases)
        => SaveJson("_phrases.json", phrases);

    public Dictionary<string, Dictionary<string, DateTimeOffset>> LoadPhraseTimestamps()
        => LoadJson<Dictionary<string, Dictionary<string, DateTimeOffset>>>("_phrases_timestamps.json");

    public void SavePhraseTimestamps(Dictionary<string, Dictionary<string, DateTimeOffset>> timestamps)
        => SaveJson("_phrases_timestamps.json", timestamps);

    public Dictionary<string, Dictionary<string, int>> LoadCasingPreferences()
        => LoadJson<Dictionary<string, Dictionary<string, int>>>("_casing.json");

    public void SaveCasingPreferences(Dictionary<string, Dictionary<string, int>> casingPreferences)
        => SaveJson("_casing.json", casingPreferences);
  ```
  Delete the now-unused private `GetBigramPath`, `GetBigramTimestampsPath`, `GetPhrasePath`, `GetPhraseTimestampsPath`, `GetCasingPath` (lines 309-347). In the main `Save(Dictionary<string, int> words)` (line 131), replace:
  ```csharp
        var json = JsonSerializer.Serialize(words, new JsonSerializerOptions { WriteIndented = true });
  ```
  with:
  ```csharp
        var json = JsonSerializer.Serialize(words, s_writeOptions);
  ```
  The file-name suffixes exactly match the old `GetXPath` outputs (`_bigrams.json`, `_bigrams_timestamps.json`, `_phrases.json`, `_phrases_timestamps.json`, `_casing.json`) — on-disk format and paths unchanged.

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~FileDictionaryProviderTests"
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~WordPredictorTests"
  ```

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs tests/EyeCtrl.Core.Tests/Prediction/FileDictionaryProviderTests.cs
  git commit -m "fix(prediction): uniform IO error handling in FileDictionaryProvider

  LoadBigrams/LoadPhrases caught only JsonException, so a locked file threw
  IOException out of WordPredictor's ctor. Extracted generic LoadJson/SaveJson
  with uniform catches and a cached JsonSerializerOptions.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A8: WordPredictor — extract casing/tier helpers

Finding 8 part 1, verified: the casing-preference lookup repeats at lines 125-128, 203-206, 252-255, and the tiered exact-case bonus repeats at lines 132-139, 166-173, 210-217. (The four sites are NOT uniform: phrases apply only the tier bonus; fuzzy applies only the casing preference — so two small helpers, not one.)

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/WordPredictor.cs:87-289`
- Test: `tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs` (existing suite is the harness; pure extraction)

- [ ] **Step 1: Baseline — run existing predictor suite** (replaces failing-test step for a pure extraction; the suite pins behavior):
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~WordPredictor"
  ```
  All green before touching anything.

- [ ] **Step 2: Implement helpers.** Add two private methods to `WordPredictor` (after `GetSuggestions`):
  ```csharp
    /// <summary>Returns the user's learned casing for <paramref name="word"/> (Phase 7.3),
    /// or the word unchanged when no preference is recorded.</summary>
    private string GetDisplayWord(string word) =>
        _casingPreferences.HasPreference(word)
            ? _casingPreferences.GetPreferredCasing(word) ?? word
            : word;

    /// <summary>
    /// Phase 7.1 tiered case matching: candidates whose exact case matches the typed
    /// prefix get <see cref="ExactCaseMatchBonus"/>; case-insensitive-only matches get
    /// <see cref="CaseInsensitiveMultiplier"/>. No-op when the prefix is empty.
    /// </summary>
    private static double ApplyCaseTierBonus(double score, string candidate, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return score;
        return candidate.StartsWith(prefix, StringComparison.Ordinal)
            ? score * ExactCaseMatchBonus
            : score * CaseInsensitiveMultiplier;
    }
  ```

- [ ] **Step 3: Replace the four sites.**

  (a) NextWord block — current (lines 125-139):
  ```csharp
                // Phase 7.3: Apply user casing preference if available
                var displayWord = _casingPreferences.HasPreference(word)
                    ? _casingPreferences.GetPreferredCasing(word) ?? word
                    : word;

                // Apply tiered case matching bonus (only when prefix is non-empty)
                // Use displayWord after applying casing preference for accurate case comparison
                if (!string.IsNullOrEmpty(prefix))
                {
                    bool exactCaseMatch = displayWord.StartsWith(prefix, StringComparison.Ordinal);
                    if (exactCaseMatch)
                        score *= ExactCaseMatchBonus;  // Tier 1: Exact case match
                    else
                        score *= CaseInsensitiveMultiplier; // Tier 2: Case-insensitive match
                }
  ```
  New:
  ```csharp
                var displayWord = GetDisplayWord(word);
                score = ApplyCaseTierBonus(score, displayWord, prefix);
  ```
  (Note: `score` is currently declared `var score = baseFreq * frequency * NextWordMultiplier;` — leave the declaration; the helper call replaces only the block above.)

  (b) Phrase block — current (lines 165-173):
  ```csharp
                    // Apply tiered case matching bonus (only when prefix is non-empty)
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        bool exactCaseMatch = phraseSuggestion.StartsWith(prefix, StringComparison.Ordinal);
                        if (exactCaseMatch)
                            score *= ExactCaseMatchBonus;  // Tier 1: Exact case match
                        else
                            score *= CaseInsensitiveMultiplier; // Tier 2: Case-insensitive match
                    }
  ```
  New:
  ```csharp
                    score = ApplyCaseTierBonus(score, phraseSuggestion, prefix);
  ```
  (Change `var score = frequency * PhraseMultiplier;` on line 163 stays as-is.)

  (c) ExactPrefix block — current (lines 203-217):
  ```csharp
                    // Phase 7.3: Apply user casing preference if available
                    var displayWord = _casingPreferences.HasPreference(word)
                        ? _casingPreferences.GetPreferredCasing(word) ?? word
                        : word;

                    // Phase 7.1: Apply tiered case matching bonus (only when prefix is non-empty)
                    // Use displayWord for case comparison after applying casing preference
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        bool exactCaseMatch = displayWord.StartsWith(prefix, StringComparison.Ordinal);
                        if (exactCaseMatch)
                            score *= ExactCaseMatchBonus;  // Tier 1: Exact case match
                        else
                            score *= CaseInsensitiveMultiplier; // Tier 2: Case-insensitive match
                    }
  ```
  New:
  ```csharp
                    var displayWord = GetDisplayWord(word);
                    score = ApplyCaseTierBonus(score, displayWord, prefix);
  ```

  (d) Fuzzy block — current (lines 252-255):
  ```csharp
                // Phase 7.3: Apply user casing preference if available
                var fuzzyDisplayWord = _casingPreferences.HasPreference(word)
                    ? _casingPreferences.GetPreferredCasing(word) ?? word
                    : word;
  ```
  New:
  ```csharp
                var fuzzyDisplayWord = GetDisplayWord(word);
  ```

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~WordPredictor"
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~Prediction"
  ```

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Prediction/WordPredictor.cs
  git commit -m "refactor(prediction): extract GetDisplayWord/ApplyCaseTierBonus helpers

  Removes the 4x repetition of the Phase 7 casing-preference and tiered
  exact-case-bonus blocks in GetSuggestions. No behavior change.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A9: WordPredictor — allocation-free fuzzy pre-filter

Finding 8 part 2, verified: the Category 3/4 loop (lines 193-259) runs per keystroke over the whole `_words` dictionary; for every non-prefix-match candidate it allocates `word[..prefix.Length]` and `.ToLowerInvariant()` then runs an O(m×n) DP. Key insight verified in `FuzzyEditDistance.Compute` (lines 131-132): it already lowercases per-char internally, so the `ToLowerInvariant()` calls (and `lowerPrefix`) are pure waste. A mathematically safe pre-filter: for same-length comparisons, any solution within `MaxFuzzyDistance = 1.5` cannot use insert/delete (each pair costs 2.0), so distance = Σ per-position substitution costs ≥ 0.5 × (positional case-insensitive mismatches); 4+ mismatches ⇒ distance ≥ 2.0 ⇒ skip. (Do NOT use a first-char-only filter — a first-char typo costs only 0.5-1.0 and must still match.)

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/WordPredictor.cs:190-246`
- Test: `tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs`

- [ ] **Step 1: Write the guard test** (protects against over-filtering; passes before AND after — this is the safety net for the perf change):
  ```csharp
    [Fact]
    public void GetSuggestions_FirstCharTypo_StillFuzzyMatches()
    {
        // 'r' and 't' are adjacent on QWERTY: "rhe" must still suggest "the".
        // Guards the fuzzy pre-filter against over-eager filtering (e.g. a
        // first-character compare would wrongly discard this).
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "world", 500 }
        });

        var results = predictor.GetSuggestions("rhe", null, maxCount: 5);

        Check.That(results).HasElementThatMatches(r => r.Word == "the");
    }
  ```

- [ ] **Step 2: Run — must pass on current code**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~WordPredictorTests"
  ```

- [ ] **Step 3: Implement.** Three edits inside `GetSuggestions`:

  (a) Delete the precomputed lowercase prefix — current (lines 190-191):
  ```csharp
            // Precompute lowercase prefix once for fuzzy matching (performance optimization)
            var lowerPrefix = prefix.ToLowerInvariant();
  ```
  Delete both lines (`FuzzyEditDistance.Compute` lowercases per-char internally).

  (b) Add the pre-filter and de-allocate the comparison — current (lines 236-243):
  ```csharp
                // Compare prefix against the same-length prefix of the word, or full word if shorter
                // Use case-insensitive comparison for fuzzy matching
                var wordPrefix = word.Length >= prefix.Length
                    ? word[..prefix.Length]
                    : word;

                var dist = FuzzyEditDistance.Compute(lowerPrefix, wordPrefix.ToLowerInvariant(), _adjacencyMap);
                if (dist > MaxFuzzyDistance) continue;
  ```
  New:
  ```csharp
                // Cheap pre-filter before the O(m*n) DP (runs per dictionary word per
                // keystroke). For same-length comparisons, any alignment within
                // MaxFuzzyDistance (1.5) cannot use insert/delete (a pair costs 2.0),
                // so distance >= 0.5 * positional case-insensitive mismatches;
                // 4+ mismatches can never qualify. Sound for first-char typos too.
                if (word.Length >= prefix.Length)
                {
                    int mismatches = 0;
                    for (int i = 0; i < prefix.Length && mismatches <= 3; i++)
                    {
                        if (char.ToLowerInvariant(word[i]) != char.ToLowerInvariant(prefix[i]))
                            mismatches++;
                    }
                    if (mismatches > 3) continue;
                }

                // Compare prefix against the same-length prefix of the word, or the full
                // word if shorter. FuzzyEditDistance lowercases per-char internally, so
                // no ToLowerInvariant() string allocations are needed here.
                var wordPrefix = word.Length >= prefix.Length
                    ? word[..prefix.Length]
                    : word;

                var dist = FuzzyEditDistance.Compute(prefix, wordPrefix, _adjacencyMap);
                if (dist > MaxFuzzyDistance) continue;
  ```

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~WordPredictor"
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~Prediction"
  ```
  All fuzzy tests (including the existing `GetSuggestions_FuzzyAdjacentKey_SuggestsIntendedWord` and the new first-char-typo test) must pass.

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Prediction/WordPredictor.cs tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs
  git commit -m "perf(prediction): allocation-free fuzzy pre-filter in GetSuggestions

  Skip the O(m*n) edit-distance DP when >3 positional case-insensitive
  mismatches make MaxFuzzyDistance unreachable, and drop redundant
  ToLowerInvariant allocations (Compute lowercases per char internally).

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A10: GazePoint — convert to readonly record struct

Finding 9. Usage audit completed (repo-wide grep for `GazePoint`, `GazePoint?`, `with` expressions, null checks):
- `src/EyeCtrl.Core/GazeEngine.cs:117` — `gaze with { X = p.X, Y = p.Y }`: `with` works on record structs, no change needed.
- `tests/EyeCtrl.Core.Tests/EyeCare/GazeEngineSampledTests.cs:45-54` — the ONLY null-semantics usage: `GazePoint? seen = null;` becomes `Nullable<GazePoint>` and `seen!.IsValid` / `seen!.X` won't compile → fix below.
- All other usages (event `Action<GazePoint>`, `GazePoint[]` in benchmarks with `= null!` on the *array*, by-value params in EyeCare/ViewModels/Platform/filters) are value-compatible.
- `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs:12-13` doc comment explicitly says "GazePoint is a record (class), so each stream heap-allocates 1000 objects" — must be updated.
- No dictionary keys, no other null checks found. Risk is LOW; no substep split needed.

**Files:**
- Modify: `src/EyeCtrl.Core/Models/GazePoint.cs:11`
- Modify: `tests/EyeCtrl.Core.Tests/EyeCare/GazeEngineSampledTests.cs:45-54`
- Modify: `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs:12-13`
- Test: `tests/EyeCtrl.Core.Tests/Models/GazePointTests.cs` (new)

- [ ] **Step 1: Write the failing test** (new file `tests/EyeCtrl.Core.Tests/Models/GazePointTests.cs`):
  ```csharp
  namespace EyeCtrl.Core.Tests.Models;

  using EyeCtrl.Core.Models;

  public class GazePointTests
  {
      [Fact]
      public void GazePoint_IsValueType_ForAllocationFreeStreaming()
      {
          // The gaze pipeline processes 30-60 samples/sec; a class record allocates
          // one object per sample (plus another via `with` in GazeEngine).
          Check.That(typeof(GazePoint).IsValueType).IsTrue();
      }

      [Fact]
      public void GazePoint_ValueEquality_Holds()
      {
          var a = new GazePoint(1.5, 2.5, 1000, true);
          var b = new GazePoint(1.5, 2.5, 1000, true);
          Check.That(a).IsEqualTo(b);
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~GazePointTests"
  ```
  Expected: `GazePoint_IsValueType_ForAllocationFreeStreaming` fails (`IsValueType` is false for a class record).

- [ ] **Step 3: Implement.**

  (a) `src/EyeCtrl.Core/Models/GazePoint.cs:11` — current:
  ```csharp
  public record GazePoint(double X, double Y, long TimestampMicroseconds, bool IsValid);
  ```
  New:
  ```csharp
  public readonly record struct GazePoint(double X, double Y, long TimestampMicroseconds, bool IsValid);
  ```

  (b) `tests/EyeCtrl.Core.Tests/EyeCare/GazeEngineSampledTests.cs` — current (lines 45-54):
  ```csharp
        GazePoint? seen = null;
        engine.GazeSampled += p => seen = p;

        var raw = new GazePoint(100, 200, 5_000_000, IsValid: true);
        provider.SimulateGaze(raw);

        Check.That(seen).IsNotNull();
        Check.That(seen!.IsValid).IsTrue();
        Check.That(seen!.X).IsEqualTo(100.0);
        Check.That(seen!.Y).IsEqualTo(200.0);
  ```
  New:
  ```csharp
        GazePoint? seen = null;
        engine.GazeSampled += p => seen = p;

        var raw = new GazePoint(100, 200, 5_000_000, IsValid: true);
        provider.SimulateGaze(raw);

        Check.That(seen.HasValue).IsTrue();
        Check.That(seen!.Value.IsValid).IsTrue();
        Check.That(seen!.Value.X).IsEqualTo(100.0);
        Check.That(seen!.Value.Y).IsEqualTo(200.0);
  ```

  (c) `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs` — current (lines 12-13, partial doc comment):
  ```csharp
  /// <see cref="GazePoint"/> is a record (class), so each stream heap-allocates 1000 objects in
  /// GlobalSetup (untimed). The generator writes pixel-scale values into GazePoint.X/Y (the tracker's
  ```
  New:
  ```csharp
  /// <see cref="GazePoint"/> is a readonly record struct, so streams are flat value arrays
  /// allocated once in GlobalSetup (untimed). The generator writes pixel-scale values into GazePoint.X/Y (the tracker's
  ```

- [ ] **Step 4: Run tests to verify pass — FULL solution (all consumers)**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "Category!=UI"
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj
  dotnet test tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj
  dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj
  ```
  If the solution build surfaces any additional null-semantics site the audit missed, fix it in the same pattern (`x != null` → `x.HasValue`, `x!.Member` → `x!.Value.Member`) and list it in the commit message.

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Models/GazePoint.cs tests/EyeCtrl.Core.Tests/Models/GazePointTests.cs tests/EyeCtrl.Core.Tests/EyeCare/GazeEngineSampledTests.cs benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs
  git commit -m "perf(core): GazePoint is now a readonly record struct

  Eliminates 1-2 heap allocations per gaze sample (30-60/sec) on the
  hot path. Only null-semantics usage was in GazeEngineSampledTests
  (Nullable<GazePoint> fixups).

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A11: GazeEngine — unsubscribe the settings PropertyChanged handler in Dispose

Finding 10, verified: the lambda subscribed at `GazeEngine.cs:66-77` is anonymous and never unsubscribed in `Dispose` (lines 140-145) — the engine leaks via the long-lived `EyeCtrlSettings` instance.

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs:66-77, 140-145` (+ field, + using)
- Test: `tests/EyeCtrl.Core.Tests/GazeEngineDisposeTests.cs` (new)

- [ ] **Step 1: Write the failing test** (new file; self-contained fakes copied from the `GazeEngineSampledTests` pattern):
  ```csharp
  namespace EyeCtrl.Core.Tests;

  using System.Reflection;
  using CommunityToolkit.Mvvm.ComponentModel;
  using EyeCtrl.Core;
  using EyeCtrl.Core.Interfaces;
  using EyeCtrl.Core.Keyboard;
  using EyeCtrl.Core.Models;
  using EyeCtrl.Core.Settings;

  public class GazeEngineDisposeTests
  {
      private sealed class FakeGazeProvider : IGazeProvider
      {
          public event Action<GazePoint>? GazePointReceived;
          public bool IsConnected { get; private set; }
          public void Start() => IsConnected = true;
          public void Stop() => IsConnected = false;
          public void Dispose() => Stop();
          public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
      }

      private sealed class FakeInputSimulator : IInputSimulator
      {
          public void MoveCursor(PhysicalPoint p) { }
          public void LeftClick(PhysicalPoint p) { }
          public void RightClick(PhysicalPoint p) { }
          public void DoubleClick(PhysicalPoint p) { }
          public void MouseDown(PhysicalPoint p) { }
          public void MouseUp(PhysicalPoint p) { }
          public void Scroll(PhysicalPoint p, int dx, int dy) { }
          public void SendKeyPress(ushort vk) { }
          public void SendKeyDown(ushort vk) { }
          public void SendKeyUp(ushort vk) { }
          public void SendText(string text) { }
          public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
      }

      private static int PropertyChangedSubscriberCount(CursorSettings settings)
      {
          var field = typeof(ObservableObject).GetField(
              nameof(ObservableObject.PropertyChanged),
              BindingFlags.Instance | BindingFlags.NonPublic);
          var handler = (Delegate?)field!.GetValue(settings);
          return handler?.GetInvocationList().Length ?? 0;
      }

      [Fact]
      public void Dispose_UnsubscribesFromCursorSettingsPropertyChanged()
      {
          var settings = new EyeCtrlSettings();
          int before = PropertyChangedSubscriberCount(settings.Cursor);

          var engine = new GazeEngine(new FakeGazeProvider(), new FakeInputSimulator(), settings);
          Check.That(PropertyChangedSubscriberCount(settings.Cursor)).IsStrictlyGreaterThan(before);

          engine.Dispose();
          Check.That(PropertyChangedSubscriberCount(settings.Cursor)).IsEqualTo(before);
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~GazeEngineDisposeTests"
  ```
  Expected: final check fails — subscriber count stays elevated after Dispose.

- [ ] **Step 3: Implement.** In `src/EyeCtrl.Core/GazeEngine.cs`: add `using System.ComponentModel;` to the usings, add a field next to `_gapInterpolator` (line 19):
  ```csharp
    private readonly PropertyChangedEventHandler _cursorSettingsChangedHandler;
  ```
  Current subscription (lines 66-77):
  ```csharp
        _settings.Cursor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CursorSettings.FilterMode)
                               or nameof(CursorSettings.MaxGapInterpolationMs))
            {
                var newInterpolator = new GapInterpolator(
                    GazeFilterFactory.Create(_settings.Cursor),
                    _settings.Cursor.MaxGapInterpolationMs);
                newInterpolator.CopyStateFrom(_gapInterpolator);
                _gapInterpolator = newInterpolator;
            }
        };
  ```
  New:
  ```csharp
        _cursorSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(CursorSettings.FilterMode)
                               or nameof(CursorSettings.MaxGapInterpolationMs))
            {
                var newInterpolator = new GapInterpolator(
                    GazeFilterFactory.Create(_settings.Cursor),
                    _settings.Cursor.MaxGapInterpolationMs);
                newInterpolator.CopyStateFrom(_gapInterpolator);
                _gapInterpolator = newInterpolator;
            }
        };
        _settings.Cursor.PropertyChanged += _cursorSettingsChangedHandler;
  ```
  Current `Dispose` (lines 140-145):
  ```csharp
    /// <summary>Unsubscribes from the gaze provider and disposes it.</summary>
    public void Dispose()
    {
        _gazeProvider.GazePointReceived -= OnGazePoint;
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
    }
  ```
  New:
  ```csharp
    /// <summary>Unsubscribes from the gaze provider and settings, and disposes the provider.</summary>
    public void Dispose()
    {
        _settings.Cursor.PropertyChanged -= _cursorSettingsChangedHandler;
        _gazeProvider.GazePointReceived -= OnGazePoint;
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
    }
  ```

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~GazeEngine"
  ```
  (Covers `GazeEngineTests`, `GazeEngineFilterRecreationTests`, `GazeEngineSampledTests`, and the new dispose test.)

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/GazeEngine.cs tests/EyeCtrl.Core.Tests/GazeEngineDisposeTests.cs
  git commit -m "fix(core): GazeEngine unsubscribes its settings handler on Dispose

  The CursorSettings.PropertyChanged lambda kept disposed engines alive
  via the long-lived settings object. Handler is now stored and removed.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A12: PhysicalPoint — add DistanceSquaredTo / DistanceTo (CONTRACT FOR OTHER PHASES)

Finding 11 part 1. **Other plan sections depend on these EXACT signatures — do not rename or alter:**
`public double DistanceSquaredTo(PhysicalPoint other)` and `public double DistanceTo(PhysicalPoint other)`.

**Files:**
- Modify: `src/EyeCtrl.Core/Models/PhysicalPoint.cs` (insert after the `-` operator, before `ToString`, ~line 86)
- Test: `tests/EyeCtrl.Core.Tests/Models/PhysicalPointDistanceTests.cs` (new)

- [ ] **Step 1: Write the failing test** (new file):
  ```csharp
  namespace EyeCtrl.Core.Tests.Models;

  using EyeCtrl.Core.Models;

  public class PhysicalPointDistanceTests
  {
      [Fact]
      public void DistanceSquaredTo_ReturnsSquaredEuclideanDistance()
      {
          var a = new PhysicalPoint(1, 2);
          var b = new PhysicalPoint(4, 6);
          Check.That(a.DistanceSquaredTo(b)).IsEqualTo(25.0);
          Check.That(b.DistanceSquaredTo(a)).IsEqualTo(25.0);
      }

      [Fact]
      public void DistanceTo_ReturnsEuclideanDistance()
      {
          var a = new PhysicalPoint(1, 2);
          var b = new PhysicalPoint(4, 6);
          Check.That(a.DistanceTo(b)).IsEqualTo(5.0);
      }

      [Fact]
      public void DistanceTo_Self_IsZero()
      {
          var p = new PhysicalPoint(123.45, -67.8);
          Check.That(p.DistanceTo(p)).IsEqualTo(0.0);
          Check.That(p.DistanceSquaredTo(p)).IsEqualTo(0.0);
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~PhysicalPointDistanceTests"
  ```
  Expected: compile error CS1061 (`PhysicalPoint` contains no definition for `DistanceSquaredTo`).

- [ ] **Step 3: Implement.** Insert into `PhysicalPoint` after the `operator -` block (line 85):
  ```csharp
    /// <summary>
    /// Returns the squared Euclidean distance to <paramref name="other"/> in physical pixels².
    /// Prefer this over <see cref="DistanceTo"/> on hot paths and for radius comparisons
    /// (compare against radius²) — it avoids the square root.
    /// </summary>
    /// <param name="other">The other point.</param>
    /// <returns>Squared distance in physical pixels².</returns>
    public double DistanceSquaredTo(PhysicalPoint other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>Returns the Euclidean distance to <paramref name="other"/> in physical pixels.</summary>
    /// <param name="other">The other point.</param>
    /// <returns>Distance in physical pixels.</returns>
    public double DistanceTo(PhysicalPoint other)
    {
        return Math.Sqrt(DistanceSquaredTo(other));
    }
  ```

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~PhysicalPointDistanceTests"
  dotnet build EyeCtrl.slnx
  ```

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Models/PhysicalPoint.cs tests/EyeCtrl.Core.Tests/Models/PhysicalPointDistanceTests.cs
  git commit -m "feat(core): add PhysicalPoint.DistanceSquaredTo/DistanceTo

  Shared distance helpers; other refactor phases depend on these
  signatures.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A13: Migrate Core+Platform distance call sites to PhysicalPoint helpers

Finding 11 part 2. Migrate ONLY these verified sites (do NOT touch `DwellDetector.cs` — owned by another section — nor anything under `src/EyeCtrl.App` / `src/EyeCtrl.ViewModels`). The FixationDetector/CentroidTracker migrations also remove `Math.Pow(x, 2)` from the per-frame path.

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/FixationDetector.cs:60-62`
- Modify: `src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs:72-77`
- Modify: `src/EyeCtrl.Core/Processing/DwellTarget.cs:57-67`
- Modify: `src/EyeCtrl.Core/Processing/ScrollEngine.cs:35-45`
- Modify: `src/EyeCtrl.Core/Calibration/CalibrationValidator.cs:136-142`
- Modify: `src/EyeCtrl.Platform/Services/ControlFinder.cs:169-176`
- Test: existing suites (pure-mechanical migration → build + full suite replaces failing-test step)

- [ ] **Step 1: Baseline**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "Category!=UI"
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```

- [ ] **Step 2: Implement all six migrations.**

  (a) `FixationDetector.cs` — current (lines 60-62):
  ```csharp
        double distance = Math.Sqrt(
            Math.Pow(currentPoint.X - _fixationCenter.Value.X, 2) +
            Math.Pow(currentPoint.Y - _fixationCenter.Value.Y, 2));
  ```
  New:
  ```csharp
        double distance = currentPoint.DistanceTo(_fixationCenter.Value);
  ```

  (b) `FixationCentroidTracker.cs` — current (lines 72-77):
  ```csharp
        double dtSeconds = (timestampMicroseconds - _lastTimestamp) / 1_000_000.0;
        double speed = dtSeconds > 0
            ? Math.Sqrt(
                Math.Pow(point.X - _lastPoint.Value.X, 2) +
                Math.Pow(point.Y - _lastPoint.Value.Y, 2)) / dtSeconds
            : 0;
  ```
  New:
  ```csharp
        double dtSeconds = (timestampMicroseconds - _lastTimestamp) / 1_000_000.0;
        double speed = dtSeconds > 0
            ? point.DistanceTo(_lastPoint.Value) / dtSeconds
            : 0;
  ```

  (c) `DwellTarget.cs` — current (lines 57-67):
  ```csharp
    /// <summary>Rect containment when <see cref="Bounds"/> is set; otherwise circle distance.</summary>
    public bool HitTest(PhysicalPoint point)
    {
        if (Bounds is { } r)
            return point.X >= r.X && point.X <= r.X + r.Width
                && point.Y >= r.Y && point.Y <= r.Y + r.Height;

        double dx = point.X - Center.X;
        double dy = point.Y - Center.Y;
        return (dx * dx + dy * dy) <= Radius * Radius;
    }
  ```
  New:
  ```csharp
    /// <summary>Rect containment when <see cref="Bounds"/> is set; otherwise circle distance.</summary>
    public bool HitTest(PhysicalPoint point)
    {
        if (Bounds is { } r)
            return point.X >= r.X && point.X <= r.X + r.Width
                && point.Y >= r.Y && point.Y <= r.Y + r.Height;

        return point.DistanceSquaredTo(Center) <= Radius * Radius;
    }
  ```

  (d) `ScrollEngine.cs` — current (lines 35-45):
  ```csharp
        if (stopZoneCenter.HasValue)
        {
            double sx = gaze.X - stopZoneCenter.Value.X;
            double sy = gaze.Y - stopZoneCenter.Value.Y;
            if (Math.Sqrt(sx * sx + sy * sy) <= stopZoneRadiusPx)
                return (0, 0);
        }

        double dx = gaze.X - anchor.X;
        double dy = anchor.Y - gaze.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
  ```
  New (keep `dx`/`dy` — they carry the direction, including the intentional Y inversion; distance is symmetric so `DistanceTo` is equivalent):
  ```csharp
        if (stopZoneCenter.HasValue &&
            gaze.DistanceTo(stopZoneCenter.Value) <= stopZoneRadiusPx)
        {
            return (0, 0);
        }

        double dx = gaze.X - anchor.X;
        double dy = anchor.Y - gaze.Y;
        double distance = gaze.DistanceTo(anchor);
  ```

  (e) `CalibrationValidator.cs` — current (lines 136-142, inside `MaxResidualPixels`):
  ```csharp
        foreach (var point in points)
        {
            var mapped = transform.Apply(point.MeanGaze);
            double dx = mapped.X - point.Target.X;
            double dy = mapped.Y - point.Target.Y;
            double err = Math.Sqrt(dx * dx + dy * dy);
            if (err > max) max = err;
        }
  ```
  New:
  ```csharp
        foreach (var point in points)
        {
            var mapped = transform.Apply(point.MeanGaze);
            double err = mapped.DistanceTo(point.Target);
            if (err > max) max = err;
        }
  ```

  (f) `ControlFinder.cs` — current (lines 169-176):
  ```csharp
    private static double DistanceSquaredToRect(System.Windows.Point point, PhysicalRect rect)
    {
        var clampedX = Math.Clamp(point.X, rect.X, rect.X + rect.Width);
        var clampedY = Math.Clamp(point.Y, rect.Y, rect.Y + rect.Height);
        var dx = point.X - clampedX;
        var dy = point.Y - clampedY;
        return (dx * dx) + (dy * dy);
    }
  ```
  New:
  ```csharp
    private static double DistanceSquaredToRect(System.Windows.Point point, PhysicalRect rect)
    {
        var nearest = new PhysicalPoint(
            Math.Clamp(point.X, rect.X, rect.X + rect.Width),
            Math.Clamp(point.Y, rect.Y, rect.Y + rect.Height));
        return new PhysicalPoint(point.X, point.Y).DistanceSquaredTo(nearest);
    }
  ```

- [ ] **Step 3: Build + run affected suites**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "Category!=UI"
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```
  Note: `Math.Pow(x, 2)` → `x * x` is bit-identical for finite doubles; `Math.Sqrt` composition unchanged — no tolerance drift expected.

- [ ] **Step 4: Run ViewModels tests too** (they exercise DwellTarget via DwellDetector):
  ```
  dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj
  ```

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Processing/FixationDetector.cs src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs src/EyeCtrl.Core/Processing/DwellTarget.cs src/EyeCtrl.Core/Processing/ScrollEngine.cs src/EyeCtrl.Core/Calibration/CalibrationValidator.cs src/EyeCtrl.Platform/Services/ControlFinder.cs
  git commit -m "refactor: migrate distance math to PhysicalPoint.DistanceTo/DistanceSquaredTo

  Also removes Math.Pow(x,2) from the per-frame fixation/centroid paths.
  DwellDetector.cs intentionally untouched (owned by another change).

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A14: MouseCoordinateMapper — delegate to PhysicalPoint.ToNormalized

Finding 12, verified: `MouseCoordinateMapper.NormalizeScreenCoordinate` (lines 12-37) duplicates `PhysicalPoint.ToNormalized` (clamp → round(÷size×65535) → clamp). Keep the mapper's distinct exception contract (ArgumentException for non-finite, InvalidOperationException for bad dimensions — `ToNormalized` throws ArgumentException for bad bounds and doesn't check finiteness, and `Math.Clamp(NaN,…)` propagates NaN).

**Files:**
- Modify: `src/EyeCtrl.Platform/Input/MouseCoordinateMapper.cs:12-37`
- Test: `tests/EyeCtrl.Platform.Tests/Input/MouseCoordinateMapperTests.cs` (existing — pins the contract)

- [ ] **Step 1: Baseline — run the existing contract tests**
  ```
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj --filter "FullyQualifiedName~MouseCoordinateMapperTests"
  ```
  All green (NaN/Infinity → ArgumentException, bad dims → InvalidOperationException, clamping to 65535).

- [ ] **Step 2: Implement.** Current body (lines 12-37):
  ```csharp
    public (int normalizedX, int normalizedY) NormalizeScreenCoordinate(PhysicalPoint point, int screenWidth, int screenHeight)
    {
        // Validate coordinates are finite (not NaN or Infinity)
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentException("Screen coordinates must be finite numbers.", nameof(point));

        // Validate screen dimensions are positive
        if (screenWidth <= 0 || screenHeight <= 0)
            throw new InvalidOperationException("Unable to get valid screen dimensions.");

        // Clamp coordinates to valid screen bounds (0 to screen size)
        // This prevents overflow and ensures cursor stays on screen
        var clampedX = Math.Clamp(point.X, 0, screenWidth);
        var clampedY = Math.Clamp(point.Y, 0, screenHeight);

        // Normalize to 0-65535 range with rounding for better accuracy
        // Using Math.Round instead of truncating cast
        int normalizedX = (int)Math.Round((clampedX * PlatformConstants.Win32AbsoluteCoordinateMax) / screenWidth);
        int normalizedY = (int)Math.Round((clampedY * PlatformConstants.Win32AbsoluteCoordinateMax) / screenHeight);

        // Final clamp to ensure we're in the valid range after rounding
        normalizedX = Math.Clamp(normalizedX, 0, PlatformConstants.Win32AbsoluteCoordinateMax);
        normalizedY = Math.Clamp(normalizedY, 0, PlatformConstants.Win32AbsoluteCoordinateMax);

        return (normalizedX, normalizedY);
    }
  ```
  New body:
  ```csharp
    public (int normalizedX, int normalizedY) NormalizeScreenCoordinate(PhysicalPoint point, int screenWidth, int screenHeight)
    {
        // Contract kept from the original implementation: non-finite coordinates are an
        // ArgumentException, invalid dimensions an InvalidOperationException. These checks
        // must stay here — PhysicalPoint.ToNormalized does not validate finiteness and
        // throws a different exception type for bad bounds.
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentException("Screen coordinates must be finite numbers.", nameof(point));

        if (screenWidth <= 0 || screenHeight <= 0)
            throw new InvalidOperationException("Unable to get valid screen dimensions.");

        // Clamp/round/normalize logic lives in one place: PhysicalPoint.ToNormalized.
        var normalized = point.ToNormalized(new ScreenBounds(screenWidth, screenHeight));
        return ((int)normalized.X, (int)normalized.Y);
    }
  ```
  (`ScreenBounds` and `NormalizedPoint` are `readonly record struct`s in `EyeCtrl.Core.Models`, already imported via `using EyeCtrl.Core.Models;`. `ToNormalized` rounds and clamps to 0-65535, so the `(int)` cast is exact.)

- [ ] **Step 3: Run tests to verify pass**
  ```
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj --filter "FullyQualifiedName~MouseCoordinateMapperTests"
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```

- [ ] **Step 4: Full solution build**
  ```
  dotnet build EyeCtrl.slnx
  ```

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Platform/Input/MouseCoordinateMapper.cs
  git commit -m "refactor(platform): MouseCoordinateMapper delegates to PhysicalPoint.ToNormalized

  Removes the duplicated clamp/round/normalize logic; exception contract
  (ArgumentException / InvalidOperationException) preserved and pinned by
  existing tests.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A15: Dead code — NativeMethods VK re-exports + OneEuroFilter unused ctor param

Finding 13 (parts 1-2), verified by grep: `VK_BACK` through `VK_DELETE` (NativeMethods.cs:39-70) have zero references outside their own definitions (`VK_SHIFT`/`VK_CONTROL`/`VK_MENU`/`VK_LWIN` at lines 35-38 ARE used by Win32InputSimulator — keep). `OneEuroFilter.LowPassFilter`'s ctor `alpha` param (line 264) is unused, and the four `Alpha(_minCutoff, 1.0/30)` computations at init (lines 159-162) are wasted work.

**Files:**
- Modify: `src/EyeCtrl.Platform/Input/NativeMethods.cs:39-70`
- Modify: `src/EyeCtrl.Core/Processing/OneEuroFilter.cs:156-166, 252-267`
- Test: pure-mechanical deletion → build + full suite replaces the failing-test step.

- [ ] **Step 1: Re-verify deadness before deleting** (grep each name; expect only the definition lines):
  ```
  # from repo root
  rg "VK_BACK|VK_TAB|VK_RETURN|VK_SPACE|VK_ESCAPE|VK_F1\b|VK_F2\b|VK_F3\b|VK_F4\b|VK_F5\b|VK_F6\b|VK_F7\b|VK_F8\b|VK_F9\b|VK_F10|VK_F11|VK_F12|VK_PRIOR|VK_NEXT|VK_END|VK_HOME|VK_LEFT|VK_UP|VK_RIGHT|VK_DOWN|VK_SNAPSHOT|VK_INSERT|VK_DELETE" src tests samples benchmarks
  ```
  (Verified 2026-07-02: only `NativeMethods.cs:39-70` definitions + one *comment* mentioning VK_ESCAPE in `EventCoordinator.cs:394` that references the literal `0x1B`, not the constant.)

- [ ] **Step 2: Implement.**

  (a) `NativeMethods.cs`: delete lines 39-70 — everything from
  ```csharp
    internal const ushort VK_BACK = VirtualKeys.Back;
  ```
  through
  ```csharp
    internal const ushort VK_DELETE = VirtualKeys.Delete;
  ```
  including the `// Function keys` and `// Navigation keys` comments. Keep lines 35-38 (`VK_SHIFT`, `VK_CONTROL`, `VK_MENU`, `VK_LWIN`).

  (b) `OneEuroFilter.cs`: current init (lines 156-166):
  ```csharp
        if (!_initialized)
        {
            // Initialize all filters with a default sample rate of 30 Hz
            _xFilter = new LowPassFilter(Alpha(_minCutoff, 1.0 / 30), x);
            _yFilter = new LowPassFilter(Alpha(_minCutoff, 1.0 / 30), y);
            _dxFilter = new LowPassFilter(Alpha(_dCutoff, 1.0 / 30), 0);
            _dyFilter = new LowPassFilter(Alpha(_dCutoff, 1.0 / 30), 0);
  ```
  New:
  ```csharp
        if (!_initialized)
        {
            _xFilter = new LowPassFilter(x);
            _yFilter = new LowPassFilter(y);
            _dxFilter = new LowPassFilter(0);
            _dyFilter = new LowPassFilter(0);
  ```
  Current ctor (lines 259-267):
  ```csharp
        /// <summary>
        /// Initializes a new low-pass filter with the given smoothing factor and initial value.
        /// </summary>
        /// <param name="alpha">Initial smoothing factor (not used, alpha is passed per-sample).</param>
        /// <param name="initVal">Initial value for the filter state.</param>
        public LowPassFilter(double alpha, double initVal)
        {
            Last = initVal;
        }
  ```
  New:
  ```csharp
        /// <summary>
        /// Initializes a new low-pass filter with the given initial value.
        /// The smoothing factor is passed per-sample to <see cref="Filter"/>.
        /// </summary>
        /// <param name="initVal">Initial value for the filter state.</param>
        public LowPassFilter(double initVal)
        {
            Last = initVal;
        }
  ```

- [ ] **Step 3: Build + full suite**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "Category!=UI"
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```
  OneEuroFilter has dedicated suites (`OneEuroFilterTests`, `OneEuroFilterValidationTests`) — all must stay green (behavior is identical; the deleted alpha was never read).

- [ ] **Step 4: Re-run grep from Step 1** to confirm no dangling references.

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Platform/Input/NativeMethods.cs src/EyeCtrl.Core/Processing/OneEuroFilter.cs
  git commit -m "chore: remove dead VK_ re-exports and unused LowPassFilter alpha param

  VK_SHIFT/CONTROL/MENU/LWIN retained (used by Win32InputSimulator).
  OneEuroFilter no longer computes four Alpha() values that were discarded.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A16: GazeFilterMode.SmoothedWithSnapping — correct the docs (comment-only)

Finding 13 part 3, investigated: `GazeFilterFactory.cs:20` maps `SmoothedWithSnapping` to the identical `SmoothedFilter` as `Smoothed`. Grep evidence: "snapping" in this codebase means snap-to-control via Auto Selection (`ControlFinder`, `SelectionMode.Auto`), which operates on fixation targets, independent of the gaze filter; no filter-level snapping was ever implemented. Furthermore `CursorSettings.SnapRadiusPixels` has **no functional consumer** (only settings UI read/write — verified by grep). Decision per plan guidance: the enum value is persisted in settings JSON (`SettingsSerializationTests`), so removing it needs migration care → **comment only**; removal is deferred.

**Files:**
- Modify: `src/EyeCtrl.Core/Models/GazeFilterMode.cs:10-11`
- Modify: `src/EyeCtrl.Core/Processing/GazeFilterFactory.cs:20`
- Test: none (comment-only) — build verifies.

- [ ] **Step 1: Implement.**

  (a) `GazeFilterMode.cs` — current (lines 10-11):
  ```csharp
    /// <summary>EMA smoothing with attraction toward the nearest interactive control.</summary>
    SmoothedWithSnapping,
  ```
  New:
  ```csharp
    /// <summary>
    /// Historical alias of <see cref="Smoothed"/>. Filter-level control snapping was never
    /// implemented — "snapping" in EyeCtrl is snap-to-control via Auto Selection
    /// (SelectionMode.Auto + ControlFinder), which is independent of the gaze filter.
    /// Kept because the value is persisted in settings JSON; removing it would break
    /// existing settings files without a migration.
    /// </summary>
    SmoothedWithSnapping,
  ```

  (b) `GazeFilterFactory.cs` — current (line 20):
  ```csharp
            GazeFilterMode.SmoothedWithSnapping => new SmoothedFilter(settings.SmoothingFactor),
  ```
  New:
  ```csharp
            // Intentionally identical to Smoothed — see GazeFilterMode.SmoothedWithSnapping docs.
            GazeFilterMode.SmoothedWithSnapping => new SmoothedFilter(settings.SmoothingFactor),
  ```

- [ ] **Step 2: Build + settings tests**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~Settings"
  ```

- [ ] **Step 3: Commit**
  ```
  git add src/EyeCtrl.Core/Models/GazeFilterMode.cs src/EyeCtrl.Core/Processing/GazeFilterFactory.cs
  git commit -m "docs(core): clarify SmoothedWithSnapping is an alias of Smoothed

  Filter-level snapping was never implemented; snapping lives in Auto
  Selection. Value kept for settings-JSON compatibility.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A17: MouseGazeProvider — one-shot timer re-arm and synchronous Stop

Finding 14, verified: `new Timer(OnTick, null, 0, _intervalMs)` (line 31) is periodic — if a tick (Win32 `GetCursorPos` + event fan-out through the whole gaze pipeline) overruns the 30 ms period, ticks overlap on multiple threadpool threads; `Stop()` uses `Timer.Dispose()` which does not wait for in-flight callbacks.

**Files:**
- Modify: `src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs:28-53`
- Test: `tests/EyeCtrl.Platform.Tests/Gaze/MouseGazeProviderTests.cs` (new)

- [ ] **Step 1: Write the test** (race-regression guard — may pass intermittently pre-fix; deterministic post-fix because `Dispose(WaitHandle)` guarantees callback completion):
  ```csharp
  namespace EyeCtrl.Platform.Tests.Gaze;

  using EyeCtrl.Platform.Gaze;

  public class MouseGazeProviderTests
  {
      [Fact]
      public async Task Stop_NoEventsFireAfterStopReturns()
      {
          var provider = new MouseGazeProvider(pollIntervalMs: 5);
          int count = 0;
          provider.GazePointReceived += _ => Interlocked.Increment(ref count);

          provider.Start();
          await Task.Delay(60); // several ticks
          provider.Stop();

          int atStop = Volatile.Read(ref count);
          await Task.Delay(120);
          // Timer.Dispose(WaitHandle) in Stop() guarantees no in-flight tick
          // survives past Stop() returning.
          Check.That(Volatile.Read(ref count)).IsEqualTo(atStop);
          Check.That(atStop).IsStrictlyGreaterThan(0);
          provider.Dispose();
      }

      [Fact]
      public void StartStopCycles_DoNotThrow()
      {
          var provider = new MouseGazeProvider(pollIntervalMs: 1);
          for (int i = 0; i < 10; i++)
          {
              provider.Start();
              Thread.Sleep(5);
              provider.Stop();
          }
          provider.Dispose();
          Check.That(provider.IsConnected).IsFalse();
      }
  }
  ```

- [ ] **Step 2: Run tests** — record the pre-fix result (the first test can fail intermittently before the fix; if it passes, note that the deterministic evidence is the `Dispose()`-vs-`Dispose(WaitHandle)` code review):
  ```
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj --filter "FullyQualifiedName~MouseGazeProviderTests"
  ```

- [ ] **Step 3: Implement.** Current (lines 28-48):
  ```csharp
    public void Start()
    {
        IsConnected = true;
        _timer = new Timer(OnTick, null, 0, _intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        IsConnected = false;
    }

    private void OnTick(object? state)
    {
        if (GetCursorPos(out var point))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * MathConstants.MicrosecondsPerMillisecond;
            GazePointReceived?.Invoke(new GazePoint(point.X, point.Y, timestamp, true));
        }
    }
  ```
  New:
  ```csharp
    public void Start()
    {
        IsConnected = true;
        // One-shot timer, re-armed at the end of each tick: guarantees ticks never
        // overlap even when a tick (GetCursorPos + event fan-out through the whole
        // gaze pipeline) overruns the poll interval.
        _timer = new Timer(OnTick, null, 0, Timeout.Infinite);
    }

    public void Stop()
    {
        var timer = _timer;
        _timer = null;
        IsConnected = false;

        if (timer != null)
        {
            // Dispose(WaitHandle) blocks until any in-flight callback has completed,
            // so no GazePointReceived fires after Stop() returns.
            using var done = new ManualResetEvent(false);
            if (timer.Dispose(done))
                done.WaitOne(1000);
        }
    }

    private void OnTick(object? state)
    {
        if (GetCursorPos(out var point))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * MathConstants.MicrosecondsPerMillisecond;
            GazePointReceived?.Invoke(new GazePoint(point.X, point.Y, timestamp, true));
        }

        // Re-arm for the next tick; a concurrent Stop() may have disposed the timer.
        try
        {
            _timer?.Change(_intervalMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Stopped concurrently — nothing to re-arm.
        }
    }
  ```
  (Wall-clock ms-resolution timestamps are fine here and stay: dt is ~33 ms at 30 Hz polling, well above the 1 ms resolution — unlike the Tobii 33 Hz case fixed in A1 where the callback cadence made equal-ms collisions likely; noted per finding 15.)

- [ ] **Step 4: Run tests to verify pass**
  ```
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs tests/EyeCtrl.Platform.Tests/Gaze/MouseGazeProviderTests.cs
  git commit -m "fix(platform): MouseGazeProvider one-shot timer re-arm; Stop waits for in-flight tick

  Periodic Timer could overlap ticks when the pipeline overran 30ms, and
  Dispose() let a callback fire after Stop() returned.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A18: Cached JsonSerializerOptions + remove redundant CalibrationService lock

Finding 15 (parts 1-2), verified: `CalibrationService.Load` (lines 203-206) and `Save` (252-256) allocate fresh `JsonSerializerOptions` per call (`KeyboardLayoutLoader.Load` already caches at line 8, but `Save` at lines 30-34 does not). `_currentTransform` (line 14) is `volatile` AND wrapped in `lock (_transformLock)` at lines 166, 185, 238 — the lock guards nothing else (verified: `_transformLock` appears only at 13, 166, 185, 238; all reads go through the volatile field directly), and a volatile reference assignment is already atomic → lock is redundant.

**Files:**
- Modify: `src/EyeCtrl.Core/Calibration/CalibrationService.cs:13-14, 166, 185, 203-206, 238, 252-256`
- Modify: `src/EyeCtrl.Core/Keyboard/KeyboardLayoutLoader.cs:28-36`
- Test: pure-mechanical → build + existing suites replace failing-test step.

- [ ] **Step 1: Baseline**
  ```
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~CalibrationServiceTests"
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~Keyboard"
  ```

- [ ] **Step 2: Implement CalibrationService.** Current (lines 13-14):
  ```csharp
    private readonly object _transformLock = new();
    private volatile ICalibrationTransform? _currentTransform;
  ```
  New:
  ```csharp
    // Volatile reference assignment is atomic; no lock needed for a single-field swap.
    private volatile ICalibrationTransform? _currentTransform;

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
  ```
  Replace the three lock sites:
  - Line 166: `lock (_transformLock) { _currentTransform = transform; }` → `_currentTransform = transform;`
  - Line 185: `lock (_transformLock) { _currentTransform = null; }` → `_currentTransform = null;`
  - Line 238: `lock (_transformLock) { _currentTransform = transform; }` → `_currentTransform = transform;`

  Replace Load's options (lines 203-206):
  ```csharp
            var data = JsonSerializer.Deserialize<CalibrationData>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
  ```
  →
  ```csharp
            var data = JsonSerializer.Deserialize<CalibrationData>(json, s_readOptions);
  ```
  Replace Save's options (lines 252-256):
  ```csharp
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
  ```
  →
  ```csharp
        var json = JsonSerializer.Serialize(data, s_writeOptions);
  ```

- [ ] **Step 3: Implement KeyboardLayoutLoader.** Current (lines 28-36):
  ```csharp
    public static void Save(KeyboardLayout layout, string filePath)
    {
        var json = JsonSerializer.Serialize(layout, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }
  ```
  New — add a second cached options field next to the existing `Options` (line 8-12):
  ```csharp
    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
  ```
  and body:
  ```csharp
    public static void Save(KeyboardLayout layout, string filePath)
    {
        var json = JsonSerializer.Serialize(layout, SaveOptions);
        File.WriteAllText(filePath, json);
    }
  ```

- [ ] **Step 4: Build + suites**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "Category!=UI"
  ```

- [ ] **Step 5: Commit**
  ```
  git add src/EyeCtrl.Core/Calibration/CalibrationService.cs src/EyeCtrl.Core/Keyboard/KeyboardLayoutLoader.cs
  git commit -m "perf(core): cache JsonSerializerOptions; drop redundant CalibrationService lock

  _currentTransform is volatile and only ever single-assigned; the lock
  guarded nothing else. Options instances are now static readonly.

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A19: Consolidate private POINT/RECT structs into NativeMethods

Finding 15 part 3, verified: `MouseGazeProvider.cs:14-15` and `ControlFinder.cs:202-211` each declare private `POINT` (ControlFinder also `RECT`).

**Files:**
- Modify: `src/EyeCtrl.Platform/Input/NativeMethods.cs` (add structs)
- Modify: `src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs:10-15`
- Modify: `src/EyeCtrl.Platform/Services/ControlFinder.cs:178-211`
- Test: pure-mechanical → build + Platform suite.

- [ ] **Step 1: Implement.**

  (a) `NativeMethods.cs` — add after the `KEYBDINPUT` struct (end of class):
  ```csharp
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }
  ```

  (b) `MouseGazeProvider.cs` — current (lines 10-15):
  ```csharp
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
  ```
  New (add `using EyeCtrl.Platform.Input;` to the file's usings):
  ```csharp
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativeMethods.POINT lpPoint);
  ```
  (`OnTick` uses `point.X`/`point.Y` — unchanged.)

  (c) `ControlFinder.cs` — delete the private struct declarations (lines 202-211):
  ```csharp
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
  ```
  Add `using EyeCtrl.Platform.Input;` to the usings, and update the three P/Invoke signatures + `FallbackToWindow` (lines 178-200) to use `NativeMethods.POINT` / `NativeMethods.RECT`:
  ```csharp
    private static ControlInfo? FallbackToWindow(System.Windows.Point point)
    {
        var pt = new NativeMethods.POINT { X = (int)point.X, Y = (int)point.Y };
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return null;

        if (!GetClientRect(hwnd, out var clientRect)) return null;

        var topLeft = new NativeMethods.POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref topLeft);

        return new ControlInfo(new PhysicalRect(
            topLeft.X, topLeft.Y, clientRect.Width, clientRect.Height));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT Point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativeMethods.POINT lpPoint);
  ```

- [ ] **Step 2: Build + Platform suite**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```

- [ ] **Step 3: Commit**
  ```
  git add src/EyeCtrl.Platform/Input/NativeMethods.cs src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs src/EyeCtrl.Platform/Services/ControlFinder.cs
  git commit -m "refactor(platform): consolidate private POINT/RECT structs into NativeMethods

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

### Task A20: Win32InputSimulator — document primary-monitor assumption + allocation-free MoveCursor

Finding 15 parts 4-5, verified: `MoveCursor` runs per gaze frame (30-60 Hz) and `SendSingleInput` (line 232-235) allocates a fresh `INPUT[1]` (plus `Marshal.SizeOf` per call); `ScreenWidth`/`ScreenHeight` use `SM_CXSCREEN`/`SM_CYSCREEN`, which are primary-monitor-only — undocumented.

**Files:**
- Modify: `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs:8-18, 232-240`
- Test: pure-mechanical/doc → build + Platform suite. (No unit test can safely call `SendInput` — it injects real OS input.)

- [ ] **Step 1: Implement.**

  (a) Class doc + assumption. Current (lines 8-11):
  ```csharp
  public class Win32InputSimulator : IInputSimulator
  {
      private static int ScreenWidth => NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
      private static int ScreenHeight => NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
  ```
  New:
  ```csharp
  /// <summary>
  /// Sends mouse/keyboard input via Win32 SendInput.
  /// <para>
  /// SINGLE-PRIMARY-MONITOR ASSUMPTION: absolute mouse coordinates are normalized against
  /// SM_CXSCREEN/SM_CYSCREEN, which describe the PRIMARY monitor only. On multi-monitor
  /// setups, gaze-driven cursor positioning is therefore only correct on the primary
  /// display (matching the eye tracker's single-screen calibration). Extending to
  /// multiple monitors would require MOUSEEVENTF_VIRTUALDESK + virtual-screen metrics.
  /// </para>
  /// </summary>
  public class Win32InputSimulator : IInputSimulator
  {
      private static int ScreenWidth => NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
      private static int ScreenHeight => NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
  ```

  (b) Allocation-free single-input path. Current (lines 232-240):
  ```csharp
    private static void SendSingleInput(NativeMethods.INPUT input)
    {
        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendInputs(params NativeMethods.INPUT[] inputs)
    {
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
  ```
  New:
  ```csharp
    private static readonly int s_inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    // MoveCursor runs per gaze frame (30-60 Hz); a [ThreadStatic] one-element buffer
    // makes the single-input path allocation-free. ThreadStatic (not a shared static)
    // because clicks can be sent from the UI thread while gaze moves on the poll thread.
    [ThreadStatic]
    private static NativeMethods.INPUT[]? t_singleInput;

    private static void SendSingleInput(NativeMethods.INPUT input)
    {
        var buffer = t_singleInput ??= new NativeMethods.INPUT[1];
        buffer[0] = input;
        NativeMethods.SendInput(1, buffer, s_inputSize);
    }

    private static void SendInputs(params NativeMethods.INPUT[] inputs)
    {
        NativeMethods.SendInput((uint)inputs.Length, inputs, s_inputSize);
    }
  ```

- [ ] **Step 2: Build + Platform suite**
  ```
  dotnet build EyeCtrl.slnx
  dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
  ```

- [ ] **Step 3: MANUAL smoke (flag for user):** run the app briefly — cursor follows gaze, left/right/double click and keyboard input still work.

- [ ] **Step 4: Commit**
  ```
  git add src/EyeCtrl.Platform/Input/Win32InputSimulator.cs
  git commit -m "perf(platform): allocation-free MoveCursor; document primary-monitor assumption

  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
  ```

---

## Deferred (noted, not planned)

1. **Removing `GazeFilterMode.SmoothedWithSnapping` from the enum + settings UI** — the value is persisted in settings JSON; removal needs a settings-migration story. Documented as an alias instead (Task A16).
2. **`CursorSettings.SnapRadiusPixels` appears functionally dead** (only settings-UI read/write; no pipeline consumer — verified by grep). It belongs to `Core/Settings/`, which another plan section owns — flag to that section rather than acting here.
3. **Span-based `FuzzyEditDistance.Compute` overload** to eliminate the remaining `word[..prefix.Length]` slice allocation per fuzzy candidate — real but small after A9's pre-filter; API change not justified yet.
4. **Multi-monitor support in Win32InputSimulator** (`MOUSEEVENTF_VIRTUALDESK` + virtual-screen metrics) — out of scope; the tracker is calibrated to one screen. Documented instead (A20).
5. **MouseGazeProvider timestamp source** — wall-clock ms×1000 is fine at 30 Hz polling (dt ≈ 33 ms ≫ 1 ms resolution); a shared monotonic-timestamp helper is moot now that the Tobii provider uses device timestamps (A1).
6. **Populating `sha256`/`tokenizerSha256` in the shipped models.json manifest** so ModelDownloadService's integrity check actually engages for bundled models — data/manifest change, not code; separate task.
7. **`GazeProviderFactory` probe seam for unit testing** (injectable device-enumeration function) — would allow a true fail-first test for A3; adds indirection to `#if TOBII_AVAILABLE` code that unit tests can't exercise without the SDK anyway.
8. **`ModelDownloadService._downloadCts`/`_isDownloading` races under concurrent callers** — the semaphore makes real overlap impossible today; a fuller redesign (per-download state object) is YAGNI.

---

## Phase D: ViewModels & Settings

This phase fixes seven verified bugs in `src/EyeCtrl.ViewModels/` and `src/EyeCtrl.Core/Settings/` (reset coverage, a calibration unsubscribe race, a stuck overlay, missing validation guards, missing settings UI, a dead label, a dead message), then performs structural refactors: a single-source-of-truth `EnumerateSubSettings()` on `EyeCtrlSettings`, promotion of hardcoded dwell times into `DwellSettings`, two ViewModel base classes with incremental migration of 12 ViewModels, and extraction of the duplicated Tobii SDK MSBuild probe. **Contracts other phases consume:** `EyeCtrlSettings.EnumerateSubSettings()` with exactly the signature `public IEnumerable<(string Name, object Settings)> EnumerateSubSettings()` (D8); the new `ScrollOverlayViewModel(IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings)` constructor whose App-layer construction site is `src/EyeCtrl.App/Services/EventCoordinator.cs:1071` (D10); `ShowKeyboardMessage`/`HideKeyboardMessage` are deliberately KEPT (another phase makes EventCoordinator send them); and D15 assumes another phase has added `PhysicalPoint.DistanceSquaredTo`/`DistanceTo`.

All code below was verified against the working tree on 2026-07-02. Tests are xUnit v3 + NFluent (`Check.That`, never `Assert.*`); `tests/Directory.Build.props` provides global `using Xunit;` and `using NFluent;`.

---

### Task D1: Fix `ResetAllSettings()` missing `Backdrop`

**Files:**
- Modify: src/EyeCtrl.ViewModels/SettingsViewModel.cs:212-225
- Test: tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs

- [ ] **Step 1: Write the failing test** — append to `SettingsViewModelTests`:

```csharp
    [Fact]
    public void ResetAllSettings_ResetsBackdropToo()
    {
        var settings = new EyeCtrlSettings { Backdrop = { Opacity = 0.8, Enabled = false } };
        var service = new SettingsService("test.json");

        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        Check.That(settings.Backdrop.Opacity).IsEqualTo(defaults.Backdrop.Opacity);
        Check.That(settings.Backdrop.Enabled).IsEqualTo(defaults.Backdrop.Enabled);
    }
```

- [ ] **Step 2: Run test to verify it fails**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`
  Expected: `ResetAllSettings_ResetsBackdropToo` fails — `Opacity` stays `0.8` (checked value is 0.8 instead of 0.3).

- [ ] **Step 3: Implement** — in `ResetAllSettings()` add the missing line after `CopySettings(defaults.Theme, _settings.Theme);`:

```csharp
        CopySettings(defaults.Theme, _settings.Theme);
        CopySettings(defaults.Backdrop, _settings.Backdrop);
        CopySettings(defaults.General, _settings.General);
```

  (D9 later rewrites this method as a loop over `EnumerateSubSettings()`; this minimal fix lands the bug fix first and the test survives the rewrite.)

- [ ] **Step 4: Run tests to verify pass**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/SettingsViewModel.cs tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs && git commit -m "fix: ResetAllSettings now resets BackdropSettings (was missing from the 15 sub-settings)"`

---

### Task D2: Fix calibration unsubscribe race in `CalibrationViewModel.MoveToNextTarget`

The pending `Task.Delay(holdMs).ContinueWith(...)` unsubscribes `_gazeProvider.GazePointReceived -= OnRawGaze` BEFORE the `State != Collecting` guard (which sits inside `PostToSyncContext`). Cancel + restart while a delay is pending therefore unsubscribes the NEW session's handler. Fix: a session-generation token incremented by `StartSession()` and `Cancel()`, checked inside the posted block, with the unsubscribe moved inside the guarded block.

**Files:**
- Modify: src/EyeCtrl.ViewModels/CalibrationViewModel.cs:70-126, 169-177
- Test: tests/EyeCtrl.ViewModels.Tests/CalibrationViewModelTests.cs

- [ ] **Step 1: Write the failing test** — append inside `CalibrationViewModelTests` (below the existing `FakeGazeProviderForTests` class add the counting provider, and add the test method):

```csharp
    private sealed class CountingGazeProvider : IGazeProvider
    {
        private Action<GazePoint>? _handlers;
        public int SubscriberCount { get; private set; }
        public event Action<GazePoint>? GazePointReceived
        {
            add { _handlers += value; SubscriberCount++; }
            remove { _handlers -= value; SubscriberCount--; }
        }
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task Cancel_WhileTargetHoldPending_StaleContinuationDoesNotUnsubscribeAgain()
    {
        // Bug: the Task.Delay continuation unsubscribed OnRawGaze BEFORE the
        // State != Collecting guard. After Cancel() (which already unsubscribed),
        // the stale continuation fired a second unsubscribe — which, after a
        // restart, silently removes the NEW session's handler.
        var provider = new CountingGazeProvider();
        var settings = new CalibrationSettings { PointCount = 3, TargetHoldMs = 200 };
        var messenger = new WeakReferenceMessenger();
        var service = new CalibrationService();

        // Create off any test SynchronizationContext so PostToSyncContext runs inline
        // (same pattern as FailedCalibration_SetsStateFailed_AndSendsFailedCompletion).
        var vm = await Task.Run(() => new CalibrationViewModel(
            settings, service, messenger, provider,
            new PhysicalPoint(1920, 1080), "test-monitor"));

        messenger.Send(new CalibrationRequestedMessage());
        Check.That(provider.SubscriberCount).IsEqualTo(1);

        vm.Cancel(); // unsubscribes; the 200ms hold continuation is still pending
        Check.That(provider.SubscriberCount).IsEqualTo(0);

        // Let the stale continuation fire. With the bug it unsubscribes again -> -1.
        await Task.Delay(600, TestContext.Current.CancellationToken);

        Check.That(provider.SubscriberCount).IsEqualTo(0);
    }
```

- [ ] **Step 2: Run test to verify it fails**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~CalibrationViewModelTests"`
  Expected: final check fails with SubscriberCount `-1` instead of `0`.

- [ ] **Step 3: Implement** — three edits in `CalibrationViewModel.cs`.

  (a) Add a generation field below `private bool _disposed;`:

```csharp
    private bool _disposed;

    /// <summary>
    /// Incremented on every StartSession/Cancel. Pending per-target Task.Delay continuations
    /// capture the generation at schedule time and no-op if it has moved on, so a stale
    /// continuation can never unsubscribe a newer session's gaze handler.
    /// </summary>
    private int _sessionGeneration;
```

  (b) In `StartSession()` (line 70), increment first:

```csharp
    private void StartSession()
    {
        _sessionGeneration++;
        TotalTargets = _calibrationService.StartSession(_settings.PointCount, _screenSize);
```

  (c) Replace the continuation block in `MoveToNextTarget()` (lines 100-125). Current code:

```csharp
        _gazeProvider.GazePointReceived += OnRawGaze;
        var holdMs = _settings.TargetHoldMs;

        Task.Delay(holdMs).ContinueWith(_ =>
        {
            if (_disposed) return;
            _gazeProvider.GazePointReceived -= OnRawGaze;
            PostToSyncContext(() =>
            {
                if (State != CalibrationState.Collecting) return;
```

  New code (unsubscribe moved INSIDE the guarded posted block, guarded additionally by generation):

```csharp
        _gazeProvider.GazePointReceived += OnRawGaze;
        var holdMs = _settings.TargetHoldMs;
        var generation = _sessionGeneration;

        Task.Delay(holdMs).ContinueWith(_ =>
        {
            if (_disposed) return;
            PostToSyncContext(() =>
            {
                // Stale continuation from a cancelled/replaced session: do nothing.
                // In particular do NOT unsubscribe — that would remove the handler
                // a newer session subscribed.
                if (generation != _sessionGeneration || State != CalibrationState.Collecting) return;
                _gazeProvider.GazePointReceived -= OnRawGaze;
```

  The rest of the posted block (`try { _calibrationService.EndTarget(); ... } catch ...`) is unchanged.

  (d) In `Cancel()` (line 169), invalidate pending continuations:

```csharp
    public void Cancel()
    {
        if (State != CalibrationState.Collecting && State != CalibrationState.Training) return;
        _sessionGeneration++;
        _gazeProvider.GazePointReceived -= OnRawGaze;
```

  Note: the unsubscribe now happens on the sync-context post rather than immediately when the delay elapses, so a handful of extra samples may arrive between delay expiry and the post; they belong to the same still-active target and are harmless (`EndTarget` aggregates whatever was collected).

- [ ] **Step 4: Run tests to verify pass**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~CalibrationViewModelTests"` (all existing calibration tests must stay green, including the async Failed-path test).

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/CalibrationViewModel.cs tests/EyeCtrl.ViewModels.Tests/CalibrationViewModelTests.cs && git commit -m "fix: calibration target-hold continuation no longer unsubscribes a newer session's gaze handler"`

---

### Task D3: `ManualSelectionGazeIndicatorViewModel` must handle `ManualSelectionHiddenMessage`

The VM handles `EndManualSelectionMessage` but not `ManualSelectionHiddenMessage`, so on the "hidden" path the indicator stays visible with stale geometry (`ManualSelectionOverlayViewModel` handles both; this VM only one).

**Files:**
- Modify: src/EyeCtrl.ViewModels/ManualSelectionGazeIndicatorViewModel.cs:9-15, 79-84
- Test: tests/EyeCtrl.ViewModels.Tests/ManualSelectionGazeIndicatorViewModelTests.cs (new file)

- [ ] **Step 1: Write the failing test** — new file:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ManualSelectionGazeIndicatorViewModelTests
{
    [Fact]
    public void Receive_EndManualSelectionMessage_HidesIndicator()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);
        vm.Configure(new EyeCtrlSettings());
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));
        Check.That(vm.IsVisible).IsTrue();

        messenger.Send(new EndManualSelectionMessage());

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Receive_ManualSelectionHiddenMessage_HidesIndicatorToo()
    {
        // The "hidden" path (overlay dismissed without a completed selection) must also
        // hide the indicator, exactly like ManualSelectionOverlayViewModel does.
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);
        vm.Configure(new EyeCtrlSettings());
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));
        Check.That(vm.IsVisible).IsTrue();

        messenger.Send(new ManualSelectionHiddenMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.FixationProgress).IsEqualTo(0.0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~ManualSelectionGazeIndicatorViewModelTests"`
  Expected: `Receive_ManualSelectionHiddenMessage_HidesIndicatorToo` fails (`IsVisible` still true); the End test passes.

- [ ] **Step 3: Implement** — add the interface to the declaration list:

```csharp
public partial class ManualSelectionGazeIndicatorViewModel : ObservableObject,
    IRecipient<StartManualSelectionMessage>,
    IRecipient<EndManualSelectionMessage>,
    IRecipient<ManualSelectionHiddenMessage>,
    IRecipient<UpdateManualSelectionIndicatorPositionMessage>,
```

  and add the handler directly below `Receive(EndManualSelectionMessage)`:

```csharp
    public void Receive(ManualSelectionHiddenMessage message)
    {
        IsVisible = false;
        FixationProgress = 0.0;
        OuterCircleDiameter = _outerCircleDiameterSetting;
    }
```

- [ ] **Step 4: Run tests to verify pass** — same command as Step 2.

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/ManualSelectionGazeIndicatorViewModel.cs tests/EyeCtrl.ViewModels.Tests/ManualSelectionGazeIndicatorViewModelTests.cs && git commit -m "fix: gaze indicator now hides on ManualSelectionHiddenMessage (stale-visible bug)"`

---

### Task D4: Close the three specific validation gaps in `DwellSettings`/`CursorSettings`

Both classes are otherwise well-guarded (11 `On…Changing` guards + `Validate()` in `DwellSettings`; 8 in `CursorSettings`). This task closes only the three verified gaps, not the whole surface: `DwellSettings.DwellCooldownMs` (documented 100–5000: no guard, no Validate entry), `CursorSettings.MaxGapInterpolationMs` (documented 0–500: neither), `DwellSettings.VelocityThresholdPxPerSec` (guard exists at line 105, but missing from `Validate()`).

**Files:**
- Modify: src/EyeCtrl.Core/Settings/DwellSettings.cs, src/EyeCtrl.Core/Settings/CursorSettings.cs
- Test: tests/EyeCtrl.Core.Tests/Settings/ValidationGuardGapTests.cs (new file)

- [ ] **Step 1: Write the failing test**:

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class ValidationGuardGapTests
{
    [Fact]
    public void DwellCooldownMs_BelowRange_Throws()
    {
        Check.ThatCode(() => new DwellSettings().DwellCooldownMs = 50)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DwellCooldownMs_AboveRange_Throws()
    {
        Check.ThatCode(() => new DwellSettings().DwellCooldownMs = 5001)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MaxGapInterpolationMs_OutOfRange_Throws()
    {
        Check.ThatCode(() => new CursorSettings().MaxGapInterpolationMs = -1)
            .Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => new CursorSettings().MaxGapInterpolationMs = 501)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Defaults_ValidateClean()
    {
        Check.That(new DwellSettings().Validate()).IsEmpty();
        Check.That(new CursorSettings().Validate()).IsEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
  `dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~ValidationGuardGapTests"`
  Expected: the three `Throws` tests fail (no exception thrown — no guards exist yet).

- [ ] **Step 3: Implement**

  (a) `DwellSettings.cs` — insert after `OnVelocityThresholdPxPerSecChanging` (line 112):

```csharp
    partial void OnDwellCooldownMsChanging(int value)
    {
        if (value < 100 || value > 5000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DwellCooldownMs)} must be between 100 and 5000");
    }
```

  (b) `DwellSettings.Validate()` — extend the `CollectErrors` call (currently ends with the two Drag entries) to:

```csharp
            ValidationHelper.ValidateRange(FixationRadiusPixels, 10.0, 200.0, nameof(FixationRadiusPixels)),
            VelocityThresholdPxPerSec > 0
                ? null
                : $"{nameof(VelocityThresholdPxPerSec)} must be greater than 0, but was {VelocityThresholdPxPerSec}",
            ValidationHelper.ValidateRange(DwellCooldownMs, 100, 5000, nameof(DwellCooldownMs)),
            ValidationHelper.ValidateRange(DragReleaseButtonDwellMs, 200, 2000, nameof(DragReleaseButtonDwellMs)),
            ValidationHelper.ValidateRange(DragRightEdgeButtonDwellMs, 200, 2000, nameof(DragRightEdgeButtonDwellMs))
```

  (c) `CursorSettings.cs` — insert after `OnSnapRadiusPixelsChanging` (line 68):

```csharp
    partial void OnMaxGapInterpolationMsChanging(int value)
    {
        if (value < 0 || value > 500)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(MaxGapInterpolationMs)} must be between 0 and 500");
    }
```

  (d) `CursorSettings.Validate()` — add after the `SnapRadiusPixels` entry:

```csharp
            ValidationHelper.ValidateRange(SnapRadiusPixels, 0, 100, nameof(SnapRadiusPixels)),
            ValidationHelper.ValidateRange(MaxGapInterpolationMs, 0, 500, nameof(MaxGapInterpolationMs)),
            ValidationHelper.ValidateHexColor(CircleColor, nameof(CircleColor)),
```

- [ ] **Step 4: Run tests to verify pass**
  `dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~Settings"` (run the whole Settings namespace — SettingsPropertyValidationTests etc. must stay green).

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.Core/Settings/DwellSettings.cs src/EyeCtrl.Core/Settings/CursorSettings.cs tests/EyeCtrl.Core.Tests/Settings/ValidationGuardGapTests.cs && git commit -m "fix: add missing validation guards (DwellCooldownMs, MaxGapInterpolationMs, VelocityThreshold in Validate)"`

---

### Task D5: `BuildTabs()` — add Backdrop + Drag tabs and missing Dwell sliders

Missing: any UI for `BackdropSettings` and `DragSettings`; Dwell tab omits `VelocityThresholdPxPerSec`, `DwellCooldownMs`, `DragReleaseButtonDwellMs`, `DragRightEdgeButtonDwellMs`. Depends on D4 (cooldown guard defines the slider range).

**Files:**
- Modify: src/EyeCtrl.ViewModels/SettingsViewModel.cs:34-168
- Test: tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs

- [ ] **Step 1: Write the failing test** — in `SettingsViewModelTests`, change `Has11Tabs` and add coverage:

```csharp
    [Fact]
    public void Has13Tabs()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        Check.That(vm.Tabs).HasSize(13);
    }

    [Fact]
    public void BackdropAndDragTabs_Exist()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        Check.That(vm.Tabs.Find(t => t.Name == "Backdrop")).IsNotNull();
        Check.That(vm.Tabs.Find(t => t.Name == "Drag")).IsNotNull();
    }

    [Fact]
    public void DwellTab_ExposesCooldownVelocityAndDragDwells()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        var dwellTab = vm.Tabs.Find(t => t.Name == "Dwell")!;
        var ids = dwellTab.Items.Select(i => i.Id).ToList();
        Check.That(ids).Contains("velocityThreshold", "dwellCooldownMs", "dragReleaseMs", "dragRightEdgeMs");
    }
```

  Delete the old `Has11Tabs` test (replaced by `Has13Tabs`).

- [ ] **Step 2: Run test to verify it fails**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`
  Expected: `Has13Tabs` fails (11), `BackdropAndDragTabs_Exist` fails (null), `DwellTab_...` fails.

- [ ] **Step 3: Implement** — three edits in `BuildTabs()`.

  (a) Add `using EyeCtrl.Core;` to the file's usings (for `BackdropType`).

  (b) Extend the Dwell tab list (after the `fixationRadius` slider, line 69):

```csharp
            new SliderSettingViewModel("fixationRadius", "Fixation Radius (px)", d.FixationRadiusPixels, 10, 200, 5, v => d.FixationRadiusPixels = v),
            new SliderSettingViewModel("velocityThreshold", "Velocity Threshold (px/s)", d.VelocityThresholdPxPerSec, 10, 1000, 10, v => d.VelocityThresholdPxPerSec = v),
            new SliderSettingViewModel("dwellCooldownMs", "Dwell Cooldown (ms)", d.DwellCooldownMs, 100, 5000, 50, v => d.DwellCooldownMs = (int)v),
            new SliderSettingViewModel("dragReleaseMs", "Drag Release Dwell (ms)", d.DragReleaseButtonDwellMs, 200, 2000, 50, v => d.DragReleaseButtonDwellMs = (int)v),
            new SliderSettingViewModel("dragRightEdgeMs", "Drag Edge Button Dwell (ms)", d.DragRightEdgeButtonDwellMs, 200, 2000, 50, v => d.DragRightEdgeButtonDwellMs = (int)v),
        }));
```

  (c) After the Toolbar tab block (line 130), insert two new tabs:

```csharp
        var b = _settings.Backdrop;
        Tabs.Add(new SettingsTabViewModel("Backdrop", "▣", new List<SettingItemViewModel>
        {
            new ToggleSettingViewModel("backdropEnabled", "Enabled", b.Enabled, v => b.Enabled = v),
            new ComboSettingViewModel("backdropType", "Material", b.Type.ToString(),
                Enum.GetNames<BackdropType>(), v => b.Type = Enum.Parse<BackdropType>(v)),
            new SliderSettingViewModel("backdropOpacity", "Opacity", b.Opacity, 0.1, 0.9, 0.05, v => b.Opacity = v),
        }));

        var dr = _settings.Drag;
        Tabs.Add(new SettingsTabViewModel("Drag", "✊", new List<SettingItemViewModel>
        {
            new SliderSettingViewModel("dragSettleMs", "Settle Dwell (ms)", dr.SettleDwellMs, 200, 2000, 50, v => dr.SettleDwellMs = (int)v),
            new SliderSettingViewModel("dragSettleRadius", "Settle Radius (px)", dr.SettleRadiusPixels, 10, 200, 5, v => dr.SettleRadiusPixels = (int)v),
            new ColorSettingViewModel("dragTrailColor", "Trail Color", dr.TrailColor, v => dr.TrailColor = v),
            new SliderSettingViewModel("dragTrailOpacity", "Trail Opacity", dr.TrailOpacity, 0.1, 1.0, 0.1, v => dr.TrailOpacity = v),
        }));
```

  (d) Add the drift-audit comment at the top of `BuildTabs()` (immediately after `private void BuildTabs()` `{`):

```csharp
        // INTENTIONALLY NOT EXPOSED in the settings UI (audit this list when adding settings):
        //  - Cursor.MaxGapInterpolationMs, Cursor.PositionIndicatorStyle  (advanced tuning)
        //  - ManualSelection.Enabled (driven by SelectionMode), ManualSelection.GazeIndicator* (visual tuning)
        //  - Keyboard ML/model settings (CustomModels etc. — managed by the model registry)
        //  - Calibration.TargetColor (visual tuning)
        // Everything else in EyeCtrlSettings.EnumerateSubSettings() should have a tab/control here.
```

  NOTE: the App-layer `SettingsWindow` renders tabs generically from `Tabs`, but verify manually that the two new tab glyphs render and page correctly (UI-only aspect; automated tests cover the VM side only).

- [ ] **Step 4: Run tests to verify pass**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"` then `dotnet build EyeCtrl.slnx`. Also grep tests for `Has11Tabs`/hardcoded tab counts: `rg -n "HasSize\(11\)|Tabs" tests/` and fix any other count assertions.

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/SettingsViewModel.cs tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs && git commit -m "feat: settings UI tabs for Backdrop and Drag; expose cooldown/velocity/drag dwell sliders"`

---

### Task D6: Wire `calStatus` label to `CalibrationCompletedMessage`

The `ReadOnlyLabelSettingItem("calStatus", ...)` (SettingsViewModel.cs:153) is write-once dead — nothing ever updates `DisplayText`. `CalibrationCompletedMessage(bool Success, double? MeanErrorPixels)` already exists (InteractionMessages.cs:100).

**Files:**
- Modify: src/EyeCtrl.ViewModels/SettingsViewModel.cs
- Test: tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs

- [ ] **Step 1: Write the failing test**:

```csharp
    [Fact]
    public void CalibrationCompleted_UpdatesCalStatusLabel()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        _messenger.Send(new CalibrationCompletedMessage(true, 12.34));

        var calTab = vm.Tabs.Find(t => t.Name == "Calibration")!;
        var status = (ReadOnlyLabelSettingItem)calTab.Items.First(i => i.Id == "calStatus");
        Check.That(status.DisplayText).IsEqualTo("Calibrated — mean error 12.3 px");
    }

    [Fact]
    public void CalibrationFailed_UpdatesCalStatusLabel()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        _messenger.Send(new CalibrationCompletedMessage(false, null));

        var calTab = vm.Tabs.Find(t => t.Name == "Calibration")!;
        var status = (ReadOnlyLabelSettingItem)calTab.Items.First(i => i.Id == "calStatus");
        Check.That(status.DisplayText).IsEqualTo("Calibration failed");
    }
```

  (Add `using EyeCtrl.ViewModels;` is already implicit via namespace; `System.Linq` is implicit.)

- [ ] **Step 2: Run test to verify it fails** — same filter as D1. Expected: `DisplayText` is `"No calibration data"`.

- [ ] **Step 3: Implement**

  (a) Make the VM a recipient — change the class declaration:

```csharp
public partial class SettingsViewModel : ObservableObject,
    IRecipient<CalibrationCompletedMessage>,
    IDisposable
```

  (b) Add a field below `_disposed` and register in the constructor:

```csharp
    private bool _disposed;
    private ReadOnlyLabelSettingItem? _calStatusItem;
```

```csharp
    public SettingsViewModel(EyeCtrlSettings settings, SettingsService settingsService, IMessenger messenger)
    {
        _settings = settings;
        _settingsService = settingsService;
        _messenger = messenger;
        BuildTabs();
        messenger.RegisterAll(this);
    }
```

  (c) Capture the item in `BuildTabs()` — replace line 153:

```csharp
            new ActionButtonSettingItem("calClear", "Clear Calibration",
                () => _messenger.Send(new CalibrationClearRequestedMessage())),
            _calStatusItem = new ReadOnlyLabelSettingItem("calStatus", "Last Calibration", "No calibration data"),
        }));
```

  (d) Add the handler (e.g. after `SaveSettings()`):

```csharp
    public void Receive(CalibrationCompletedMessage message)
    {
        _calStatusItem!.DisplayText = message.Success
            ? message.MeanErrorPixels is double px
                ? $"Calibrated — mean error {px:F1} px"
                : "Calibrated"
            : "Calibration failed";
    }
```

  (e) Update `Dispose()` — replace the no-op comment body:

```csharp
    public void Dispose()
    {
        if (_disposed)
            return;

        _messenger.UnregisterAll(this);
        _disposed = true;
    }
```

- [ ] **Step 4: Run tests to verify pass**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/SettingsViewModel.cs tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs && git commit -m "fix: calStatus label now updates from CalibrationCompletedMessage (was write-once dead)"`

---

### Task D7: Delete dead `ShowSettingsMessage`; document the two ManualSelection end messages

Verified: `ShowSettingsMessage` has zero senders/receivers in `src/` (only doc-plan mentions). `ShowKeyboardMessage`/`HideKeyboardMessage` ARE received by `KeyboardViewModel` and MUST be kept (another phase adds EventCoordinator senders). `EndManualSelectionMessage` and `ManualSelectionHiddenMessage` have byte-identical handler bodies but different semantics — keep both, document.

**Files:**
- Modify: src/EyeCtrl.ViewModels/Messages/OverlayMessages.cs, src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs:41-43
- Test: build-passes replaces test steps (pure deletion + comments; no observable behavior)

- [ ] **Step 1-2 (replaced): confirm dead** — `rg -n "ShowSettingsMessage" src/ tests/` must show only the declaration in OverlayMessages.cs:8.

- [ ] **Step 3: Implement**

  (a) `OverlayMessages.cs` — delete lines 7-8; result:

```csharp
namespace EyeCtrl.ViewModels.Messages;

/// <summary>Sent when keyboard should be shown/hidden.</summary>
public record ShowKeyboardMessage;
public record HideKeyboardMessage;

/// <summary>Sent when settings window should close.</summary>
public record CloseSettingsMessage;
```

  (b) `InteractionMessages.cs` — replace lines 41-43:

```csharp
/// <summary>
/// Sent when a manual selection completes normally (an action was chosen or the user closed it).
/// Overlays tear down their manual-selection visuals. Distinct from
/// <see cref="ManualSelectionHiddenMessage"/>, which is the abnormal/forced-dismissal path
/// (e.g. state machine leaves ManualSelection without user completion); both must hide
/// the overlay, hence the intentionally identical handler bodies.
/// </summary>
public record EndManualSelectionMessage;

/// <summary>
/// Sent when the manual selection overlay is force-hidden without a completed selection.
/// See <see cref="EndManualSelectionMessage"/> for the normal-completion counterpart.
/// </summary>
public record ManualSelectionHiddenMessage;
```

  (Before committing, verify the semantic description against the actual senders: `rg -n "EndManualSelectionMessage\(\)|ManualSelectionHiddenMessage\(\)" src/` and adjust wording to match the real send sites in the App layer.)

- [ ] **Step 4: Verify** — `dotnet build EyeCtrl.slnx` (must succeed with zero new warnings).

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/Messages/OverlayMessages.cs src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs && git commit -m "chore: delete dead ShowSettingsMessage; document End vs Hidden manual-selection messages"`

---

### Task D8: `EyeCtrlSettings.EnumerateSubSettings()` + `Validate()` as a loop

**CONTRACT: other plan phases call `public IEnumerable<(string Name, object Settings)> EnumerateSubSettings()` — the signature must be exactly this.** Replaces the 15 copy-pasted null-check blocks in `Validate()` (lines 45-126).

**Files:**
- Modify: src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs:44-126
- Test: tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs (new file)

- [ ] **Step 1: Write the failing test**:

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class EyeCtrlSettingsEnumerationTests
{
    [Fact]
    public void EnumerateSubSettings_Yields15Entries_AllValidatable()
    {
        var subs = new EyeCtrlSettings().EnumerateSubSettings().ToList();

        Check.That(subs).HasSize(15);
        Check.That(subs.All(t => t.Settings is IValidatable)).IsTrue();
    }

    [Fact]
    public void EnumerateSubSettings_CoversEveryValidatableProperty()
    {
        // Drift guard: adding a new sub-settings class to EyeCtrlSettings without
        // extending EnumerateSubSettings() must fail this test.
        var expected = typeof(EyeCtrlSettings).GetProperties()
            .Where(p => typeof(IValidatable).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        var actual = new EyeCtrlSettings().EnumerateSubSettings()
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Check.That(actual).ContainsExactly(expected);
    }

    [Fact]
    public void Validate_DefaultInstance_HasNoErrors()
    {
        Check.That(new EyeCtrlSettings().Validate()).IsEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
  `dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~EyeCtrlSettingsEnumerationTests"`
  Expected: compile error — `EnumerateSubSettings` does not exist.

- [ ] **Step 3: Implement** — replace the entire `Validate()` body (lines 44-126) with:

```csharp
    /// <summary>
    /// Enumerates every sub-settings object with its property name. Single source of truth
    /// consumed by <see cref="Validate"/>, SettingsViewModel.ResetAllSettings, and other
    /// full-coverage consumers. MUST be extended when adding a new sub-settings class
    /// (the drift-guard test EyeCtrlSettingsEnumerationTests enforces this).
    /// </summary>
    public IEnumerable<(string Name, object Settings)> EnumerateSubSettings()
    {
        yield return (nameof(Cursor), Cursor);
        yield return (nameof(Dwell), Dwell);
        yield return (nameof(ManualSelection), ManualSelection);
        yield return (nameof(ActionMenu), ActionMenu);
        yield return (nameof(Keyboard), Keyboard);
        yield return (nameof(Toolbar), Toolbar);
        yield return (nameof(Theme), Theme);
        yield return (nameof(Backdrop), Backdrop);
        yield return (nameof(Calibration), Calibration);
        yield return (nameof(General), General);
        yield return (nameof(AutoSelection), AutoSelection);
        yield return (nameof(ExternalDwell), ExternalDwell);
        yield return (nameof(Scroll), Scroll);
        yield return (nameof(EyeCare), EyeCare);
        yield return (nameof(Drag), Drag);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        foreach (var (name, settings) in EnumerateSubSettings())
        {
            if (settings is null)
                errors.Add($"{name} settings must not be null.");
            else if (settings is IValidatable validatable)
                errors.AddRange(validatable.Validate());
        }

        return errors;
    }
```

  Note: null-error text changes for one case ("Action menu settings must not be null." → "ActionMenu settings must not be null."). Verified: no test asserts these exact strings (`rg "must not be null" tests/` → no matches).

- [ ] **Step 4: Run tests to verify pass**
  `dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj` (whole Core suite — serialization/validation tests exercise `Validate()`).

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs && git commit -m "refactor: EyeCtrlSettings.EnumerateSubSettings() single source of truth; Validate() as loop"`

---

### Task D9: `ResetAllSettings()` via `EnumerateSubSettings()` + full-coverage test

Depends on D8. `CopySettings<T>` currently reflects over `typeof(T)`; with `object`-typed pairs it must reflect over the runtime type.

**Files:**
- Modify: src/EyeCtrl.ViewModels/SettingsViewModel.cs:209-243
- Test: tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs

- [ ] **Step 1: Write the failing test** (fails only in the sense of guarding future drift; it should pass immediately after D1 — that is fine, it is the structural coverage net demanded by the audit):

```csharp
    [Fact]
    public void ResetAllSettings_CoversEveryEnumeratedSubSetting()
    {
        var settings = new EyeCtrlSettings();
        // Perturb one representative property per mutable sub-setting we can safely change:
        settings.Cursor.CircleSize = 400;
        settings.Dwell.FixationDetectionMs = 999;
        settings.Backdrop.Opacity = 0.8;
        settings.Drag.SettleDwellMs = 1500;
        settings.Scroll.Sensitivity = 1.9;

        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        var defaultSubs = defaults.EnumerateSubSettings().ToDictionary(t => t.Name, t => t.Settings);

        foreach (var (name, sub) in settings.EnumerateSubSettings())
        {
            var defaultSub = defaultSubs[name];
            var comparableProps = sub.GetType().GetProperties()
                .Where(p => p.CanRead && p.CanWrite &&
                            (p.PropertyType.IsValueType || p.PropertyType == typeof(string)));
            foreach (var prop in comparableProps)
            {
                Check.WithCustomMessage($"{name}.{prop.Name} was not reset")
                    .That(prop.GetValue(sub)).IsEqualTo(prop.GetValue(defaultSub));
            }
        }
    }
```

- [ ] **Step 2: Run test** — expected to PASS already (D1 fixed the only gap). Temporarily comment out the `CopySettings(defaults.Backdrop, ...)` line locally to see it fail with `Backdrop.Opacity was not reset`, then restore — this proves the net works.

- [ ] **Step 3: Implement** — replace `ResetAllSettings()` and `CopySettings` (lines 209-243) with:

```csharp
    public void ResetAllSettings()
    {
        var defaults = new EyeCtrlSettings();
        var defaultSubs = defaults.EnumerateSubSettings().ToDictionary(t => t.Name, t => t.Settings);
        foreach (var (name, sub) in _settings.EnumerateSubSettings())
            CopySettings(defaultSubs[name], sub);
        _settings.SelectionMode = defaults.SelectionMode;
        _settings.ViewPosition = defaults.ViewPosition;

        // Rebuild tabs to reflect new values
        Tabs.Clear();
        BuildTabs();
        if (!string.IsNullOrEmpty(CurrentTabName))
            ShowTab(CurrentTabName);
    }

    private static void CopySettings(object source, object target)
    {
        foreach (var prop in source.GetType().GetProperties())
        {
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(target, prop.GetValue(source));
        }
    }
```

- [ ] **Step 4: Run tests to verify pass**
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/SettingsViewModel.cs tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs && git commit -m "refactor: ResetAllSettings iterates EnumerateSubSettings — structurally immune to new-sub-setting drift"`

---

### Task D10: `ScrollStopButtonDwellMs` setting + `ScrollOverlayViewModel` constructor contract

**CONTRACT: the resulting constructor MUST be `ScrollOverlayViewModel(IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings)` — the App-layer phase relies on it.** The single App construction site (`src/EyeCtrl.App/Services/EventCoordinator.cs:1071`) is updated here in the same commit to keep the build green; the App-layer phase owns any further changes at that site.

**Files:**
- Modify: src/EyeCtrl.Core/Settings/DwellSettings.cs, src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs:15-52, src/EyeCtrl.ViewModels/SettingsViewModel.cs (Dwell tab), src/EyeCtrl.App/Services/EventCoordinator.cs:1071
- Test: tests/EyeCtrl.Core.Tests/Settings/ValidationGuardGapTests.cs, tests/EyeCtrl.ViewModels.Tests/ScrollOverlayViewModelTests.cs

- [ ] **Step 1: Write the failing tests**

  (a) In `ValidationGuardGapTests`:

```csharp
    [Fact]
    public void ScrollStopButtonDwellMs_HasDefault600_AndRangeGuard()
    {
        Check.That(new DwellSettings().ScrollStopButtonDwellMs).IsEqualTo(600);
        Check.ThatCode(() => new DwellSettings().ScrollStopButtonDwellMs = 100)
            .Throws<ArgumentOutOfRangeException>();
    }
```

  (b) In `ScrollOverlayViewModelTests`, replace the factory (line 10-11):

```csharp
    private static ScrollOverlayViewModel NewVm() =>
        new(new WeakReferenceMessenger(), new ScrollSettings(), new DwellSettings());
```

  and add:

```csharp
    [Fact]
    public void StopButtonDwellTime_ComesFromSettings()
    {
        var dwell = new DwellSettings { ScrollStopButtonDwellMs = 2000 };
        var vm = new ScrollOverlayViewModel(new WeakReferenceMessenger(), new ScrollSettings(), dwell);
        bool exitCalled = false;
        vm.ExitRequested += () => exitCalled = true;

        vm.EnterScrollMode(Anchor, StopCenter);
        var stopPos = vm.StopButtonCenter;
        vm.Receive(new GazeCursorMovedMessage(stopPos, 0));
        vm.Receive(new GazeCursorMovedMessage(stopPos, 1_000_000)); // 1000ms < 2000ms
        Check.That(exitCalled).IsFalse();

        vm.Receive(new GazeCursorMovedMessage(stopPos, 2_100_000)); // past 2000ms
        Check.That(exitCalled).IsTrue();
    }
```

- [ ] **Step 2: Run tests to verify they fail** — compile errors (no `ScrollStopButtonDwellMs`, no 3-arg ctor):
  `dotnet build EyeCtrl.slnx`

- [ ] **Step 3: Implement**

  (a) `DwellSettings.cs` — add field after `_dragRightEdgeButtonDwellMs` (line 31):

```csharp
    /// <summary>Dwell time in milliseconds for the scroll-mode stop button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _scrollStopButtonDwellMs = 600;
```

  guard after `OnDragRightEdgeButtonDwellMsChanging`:

```csharp
    partial void OnScrollStopButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(ScrollStopButtonDwellMs)} must be between 200 and 2000");
    }
```

  and Validate entry:

```csharp
            ValidationHelper.ValidateRange(ScrollStopButtonDwellMs, 200, 2000, nameof(ScrollStopButtonDwellMs)),
```

  (b) `ScrollOverlayViewModel.cs` — delete `private const int StopButtonDwellMs = 600;` (line 16); add field + new ctor replacing lines 21-52:

```csharp
    private readonly DwellDetector _dwellDetector;
    private readonly IMessenger _messenger;
    private readonly ScrollSettings _scrollSettings;
    private readonly DwellSettings _dwellSettings;
    private bool _disposed;
```

```csharp
    public ScrollOverlayViewModel(IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings)
    {
        _messenger = messenger;
        _scrollSettings = scrollSettings;
        _dwellSettings = dwellSettings;
        _dwellDetector = new DwellDetector(dwellSettings.DwellCooldownMs);
        _dwellDetector.TargetActivated += _ => ExitRequested?.Invoke();
        _dwellDetector.TargetProgress += (_, progress) => StopButtonProgress = progress;
        messenger.RegisterAll(this);
    }
```

  and in `EnterScrollMode` replace `StopButtonDwellMs` with `_dwellSettings.ScrollStopButtonDwellMs`:

```csharp
        _dwellDetector.AddTarget(new DwellTarget(
            StopButtonTargetId,
            _stopButtonCenter,
            StopButtonRadiusPx,
            _dwellSettings.ScrollStopButtonDwellMs));
```

  (c) `SettingsViewModel.cs` Dwell tab — add after the `dragRightEdgeMs` slider (from D5):

```csharp
            new SliderSettingViewModel("scrollStopMs", "Scroll Stop Button Dwell (ms)", d.ScrollStopButtonDwellMs, 200, 2000, 50, v => d.ScrollStopButtonDwellMs = (int)v),
```

  (d) `EventCoordinator.cs:1071` — replace:

```csharp
        _scrollOverlayVm = new ScrollOverlayViewModel(_context.Settings.Scroll, _context.Messenger);
```

  with:

```csharp
        _scrollOverlayVm = new ScrollOverlayViewModel(_context.Messenger, _context.Settings.Scroll, _context.Settings.Dwell);
```

- [ ] **Step 4: Run tests to verify pass**
  `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~ScrollOverlayViewModelTests" && dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~ValidationGuardGapTests"`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.Core/Settings/DwellSettings.cs src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.Core.Tests/Settings/ValidationGuardGapTests.cs tests/EyeCtrl.ViewModels.Tests/ScrollOverlayViewModelTests.cs && git commit -m "refactor: ScrollStopButtonDwellMs setting; ScrollOverlayViewModel(messenger, scroll, dwell) ctor + cooldown"`

---

### Task D11: `UIConstants.ManualSelectionSideButtonDwellMs` → `DwellSettings.ManualSelectionSideButtonDwellMs`

Consumers: `ManualSelectionOverlayViewModel.cs:71,73`. Tests referencing the constant: `UIConstantsTests.cs:33-36,52`, `MagicNumberReplacementTests.cs:29`.

**Files:**
- Modify: src/EyeCtrl.Core/Settings/DwellSettings.cs, src/EyeCtrl.ViewModels/ManualSelectionOverlayViewModel.cs, src/EyeCtrl.ViewModels/UIConstants.cs:28-32, src/EyeCtrl.ViewModels/SettingsViewModel.cs
- Test: tests/EyeCtrl.Core.Tests/Settings/ValidationGuardGapTests.cs, tests/EyeCtrl.ViewModels.Tests/UIConstantsTests.cs, tests/EyeCtrl.ViewModels.Tests/MagicNumberReplacementTests.cs

- [ ] **Step 1: Write the failing test** — in `ValidationGuardGapTests`:

```csharp
    [Fact]
    public void ManualSelectionSideButtonDwellMs_HasDefault600_AndRangeGuard()
    {
        Check.That(new DwellSettings().ManualSelectionSideButtonDwellMs).IsEqualTo(600);
        Check.ThatCode(() => new DwellSettings().ManualSelectionSideButtonDwellMs = 100)
            .Throws<ArgumentOutOfRangeException>();
    }
```

- [ ] **Step 2: Run test to verify it fails** — compile error (property does not exist).

- [ ] **Step 3: Implement**

  (a) `DwellSettings.cs` — add field, guard, Validate entry (identical pattern to D10, name `ManualSelectionSideButtonDwellMs`, default 600, range 200-2000):

```csharp
    /// <summary>Dwell time in milliseconds for manual selection side buttons (close/recapture). Valid range: 200–2000.</summary>
    [ObservableProperty] private int _manualSelectionSideButtonDwellMs = 600;
```

```csharp
    partial void OnManualSelectionSideButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(ManualSelectionSideButtonDwellMs)} must be between 200 and 2000");
    }
```

```csharp
            ValidationHelper.ValidateRange(ManualSelectionSideButtonDwellMs, 200, 2000, nameof(ManualSelectionSideButtonDwellMs)),
```

  (b) `ManualSelectionOverlayViewModel.cs` — store the settings (ctor currently discards them). Add field and assign:

```csharp
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _dwellDetector;
```

```csharp
    public ManualSelectionOverlayViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
```

  and in `Receive(StartManualSelectionMessage)` replace both target registrations (lines 70-73):

```csharp
        _dwellDetector.AddTarget(new DwellTarget(
            CloseTargetId, CloseButtonCenter, physicalBtnRadius, _settings.Dwell.ManualSelectionSideButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            RecaptureTargetId, RecaptureButtonCenter, physicalBtnRadius, _settings.Dwell.ManualSelectionSideButtonDwellMs));
```

  (c) `UIConstants.cs` — delete lines 28-32 (the XML doc + `public const int ManualSelectionSideButtonDwellMs = 600;`).

  (d) Tests — in `UIConstantsTests.cs` delete the `ManualSelectionSideButtonDwellMs_IsDefinedAndPositive` test and the line-52 reference (read the surrounding test to remove only those assertions); in `MagicNumberReplacementTests.cs:29` replace with `Check.That(new EyeCtrl.Core.Settings.DwellSettings().ManualSelectionSideButtonDwellMs).IsEqualTo(600);` (read the file first; keep its intent of pinning the value).

  (e) `SettingsViewModel.cs` Dwell tab — add slider:

```csharp
            new SliderSettingViewModel("manualSelSideMs", "Manual Selection Side Button Dwell (ms)", d.ManualSelectionSideButtonDwellMs, 200, 2000, 50, v => d.ManualSelectionSideButtonDwellMs = (int)v),
```

- [ ] **Step 4: Run tests to verify pass**
  `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj && dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~ValidationGuardGapTests"`

- [ ] **Step 5: Commit**
  `git add -A src tests && git commit -m "refactor: ManualSelectionSideButtonDwellMs is a DwellSettings setting, not a UIConstants constant"`

---

### Task D12: `UIConstants.BackdropHideButtonDwellMs` → `DwellSettings.BackdropHideButtonDwellMs`

Same pattern as D11. Consumer: `GlassBackdropViewModel.cs:46`. Tests referencing the constant: `GlassBackdropViewModelTests.cs:52,99,120`.

**Files:**
- Modify: src/EyeCtrl.Core/Settings/DwellSettings.cs, src/EyeCtrl.ViewModels/GlassBackdropViewModel.cs, src/EyeCtrl.ViewModels/UIConstants.cs:40-41, src/EyeCtrl.ViewModels/SettingsViewModel.cs
- Test: tests/EyeCtrl.Core.Tests/Settings/ValidationGuardGapTests.cs, tests/EyeCtrl.ViewModels.Tests/GlassBackdropViewModelTests.cs

- [ ] **Step 1: Write the failing test** — in `ValidationGuardGapTests`:

```csharp
    [Fact]
    public void BackdropHideButtonDwellMs_HasDefault600_AndRangeGuard()
    {
        Check.That(new DwellSettings().BackdropHideButtonDwellMs).IsEqualTo(600);
        Check.ThatCode(() => new DwellSettings().BackdropHideButtonDwellMs = 100)
            .Throws<ArgumentOutOfRangeException>();
    }
```

- [ ] **Step 2: Run test to verify it fails** — compile error.

- [ ] **Step 3: Implement**

  (a) `DwellSettings.cs` — field `[ObservableProperty] private int _backdropHideButtonDwellMs = 600;` with doc comment "Dwell time in milliseconds for the backdrop hide button. Valid range: 200–2000.", guard `OnBackdropHideButtonDwellMsChanging` (200-2000, same shape as D10/D11), Validate entry `ValidationHelper.ValidateRange(BackdropHideButtonDwellMs, 200, 2000, nameof(BackdropHideButtonDwellMs)),`.

  (b) `GlassBackdropViewModel.cs` — store settings; current ctor only reads cooldown. Add field + use it:

```csharp
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _dwellDetector;
```

```csharp
    public GlassBackdropViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
```

  and in `Receive(ShowBackdropMessage)` (line 42-46):

```csharp
        _dwellDetector.AddTarget(new DwellTarget(
            HideTargetId,
            message.HideButtonCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.BackdropHideButtonDwellMs));
```

  (c) `UIConstants.cs` — delete lines 40-41 (doc + `public const int BackdropHideButtonDwellMs = 600;`). Keep `BackdropHideButtonOffset`.

  (d) `GlassBackdropViewModelTests.cs` — replace all three `UIConstants.BackdropHideButtonDwellMs` reads with the settings value; e.g. line 52:

```csharp
        long dwellUs = settings.Dwell.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond;
```

  (lines 99 and 120 analogously — each test already has a local `settings` variable).

  (e) `SettingsViewModel.cs` Dwell tab slider:

```csharp
            new SliderSettingViewModel("backdropHideMs", "Backdrop Hide Button Dwell (ms)", d.BackdropHideButtonDwellMs, 200, 2000, 50, v => d.BackdropHideButtonDwellMs = (int)v),
```

- [ ] **Step 4: Run tests to verify pass** — `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~GlassBackdropViewModelTests"`

- [ ] **Step 5: Commit**
  `git add -A src tests && git commit -m "refactor: BackdropHideButtonDwellMs is a DwellSettings setting"`

---

### Task D13: `ToolbarViewModel` — replace hardcoded `% 3` with enum length

**Files:**
- Modify: src/EyeCtrl.ViewModels/ToolbarViewModel.cs:113
- Test: tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs

- [ ] **Step 1: Write the failing test** — append to `ToolbarViewModelTests` (read the file first to match its construction helpers; it constructs `new ToolbarViewModel(settings, messenger)` and uses `SimulateButtonActivated`):

```csharp
    [Fact]
    public void SelectionModeCycle_VisitsEveryEnumValueAndWraps()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(settings, messenger);
        var seen = new List<SelectionMode>();
        messenger.Register<SelectionModeChangedMessage>(this, (_, m) =>
        {
            seen.Add(m.Mode);
            vm.Receive(m); // toolbar itself normally receives it back via the messenger
        });

        int count = Enum.GetValues<SelectionMode>().Length;
        for (int i = 0; i <= count; i++)
            vm.SimulateButtonActivated("SelectionMode");

        // Every enum member must appear, and after a full cycle we must wrap to the first.
        Check.That(seen.Distinct().Count()).IsEqualTo(count);
        Check.That(seen[count]).IsEqualTo(seen[0]);
    }
```

  Note: this test passes with `% 3` today only because `SelectionMode` currently has exactly 3 members; it fails the moment a 4th is added. It documents the invariant; the code change makes it future-proof. (`vm.Receive(m)` mirrors the production loop where ToolbarViewModel is itself a registered recipient — check whether `messenger.Send` already delivers to the VM; if so, drop the manual `vm.Receive(m)` line.)

- [ ] **Step 2: Run test** — `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~ToolbarViewModelTests"`. Expected: passes (see note); verify it compiles and the cycle assertion holds.

- [ ] **Step 3: Implement** — line 113, replace:

```csharp
            case "SelectionMode":
                var next = (SelectionMode)(((int)_currentSelectionMode + 1) % 3);
                _messenger.Send(new SelectionModeChangedMessage(next));
                break;
```

  with:

```csharp
            case "SelectionMode":
                var next = (SelectionMode)(((int)_currentSelectionMode + 1) % Enum.GetValues<SelectionMode>().Length);
                _messenger.Send(new SelectionModeChangedMessage(next));
                break;
```

- [ ] **Step 4: Run tests to verify pass** — same command.

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs && git commit -m "refactor: SelectionMode cycling uses Enum.GetValues length instead of hardcoded % 3"`

---

### Task D14: `DragTrailViewModel` — magic numbers to UIConstants; fold `SetupDwellTargetsWithBack`

**Files:**
- Modify: src/EyeCtrl.ViewModels/DragTrailViewModel.cs:105, 120-160, src/EyeCtrl.ViewModels/UIConstants.cs
- Test: tests/EyeCtrl.ViewModels.Tests/DragTrailViewModelTests.cs (existing suite is the regression net; pure-mechanical refactor — build+existing tests replace new-test steps)

- [ ] **Step 1-2 (replaced): baseline** — `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~DragTrailViewModelTests"` green before starting.

- [ ] **Step 3: Implement**

  (a) `UIConstants.cs` — append inside the class:

```csharp
    /// <summary>Radius of the drag release/cancel/back buttons in logical pixels.</summary>
    public const double DragButtonRadius = 20.0;

    /// <summary>Offset in logical pixels from the cursor to the drag release button center (x and y).</summary>
    public const double DragReleaseButtonOffset = 14.0;
```

  (b) `DragTrailViewModel.cs` — replace `UpdateReleaseButtonPosition` (lines 120-131):

```csharp
    private void UpdateReleaseButtonPosition(PhysicalPoint cursorPos)
    {
        var offset = DpiHelper.LogicalToPhysicalX(UIConstants.DragReleaseButtonOffset);
        var btnRadius = DpiHelper.LogicalToPhysicalX(UIConstants.DragButtonRadius);

        double dx = cursorPos.X - ReleaseButtonCenter.X;
        double dy = cursorPos.Y - ReleaseButtonCenter.Y;
        if (dx * dx + dy * dy <= btnRadius * btnRadius)
            return;

        ReleaseButtonCenter = new PhysicalPoint(cursorPos.X - offset, cursorPos.Y - offset);
    }
```

  (c) Replace BOTH `SetupDwellTargets()` and `SetupDwellTargetsWithBack()` (lines 133-160) with one method:

```csharp
    private void SetupDwellTargets(bool includeBack = false)
    {
        _dwellDetector.ClearTargets();
        var btnRadius = DpiHelper.LogicalToPhysicalX(UIConstants.DragButtonRadius);

        _dwellDetector.AddTarget(new DwellTarget(
            ReleaseTargetId, ReleaseButtonCenter, btnRadius,
            _settings.Dwell.DragReleaseButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            CancelTargetId, CancelButtonCenter, btnRadius,
            _settings.Dwell.DragRightEdgeButtonDwellMs));

        if (includeBack)
        {
            _dwellDetector.AddTarget(new DwellTarget(
                BackTargetId, BackButtonCenter, btnRadius,
                _settings.Dwell.DragRightEdgeButtonDwellMs));
        }
    }
```

  (d) Update the one caller of the removed method — `OnSettleStarted` (line 105): `SetupDwellTargetsWithBack();` → `SetupDwellTargets(includeBack: true);`.

- [ ] **Step 4: Run tests to verify pass**
  `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~DragTrailViewModelTests"`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/DragTrailViewModel.cs src/EyeCtrl.ViewModels/UIConstants.cs && git commit -m "refactor: DragTrail magic numbers to UIConstants; fold SetupDwellTargetsWithBack into SetupDwellTargets(bool)"`

---

### Task D15: Migrate ViewModels distance math to `PhysicalPoint.DistanceSquaredTo` / `DistanceTo`

**DEPENDENCY: Task A12 adds `public double DistanceSquaredTo(PhysicalPoint other)` and `public double DistanceTo(PhysicalPoint other)` to src/EyeCtrl.Core/Models/PhysicalPoint.cs. Verify they exist (`rg -n "DistanceSquaredTo" src/EyeCtrl.Core/Models/PhysicalPoint.cs`) before starting; if not, run Task A12 first.** Sites: KeyboardViewModel.cs:100-110, DragTrailViewModel.cs `UpdateReleaseButtonPosition` (post-D14), ScrollOverlayViewModel.cs:90-92.

**Files:**
- Modify: src/EyeCtrl.ViewModels/KeyboardViewModel.cs, src/EyeCtrl.ViewModels/DragTrailViewModel.cs, src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs
- Test: existing suites (pure-mechanical, behavior-preserving — build+existing tests replace new-test steps)

- [ ] **Step 1-2 (replaced): baseline** — full ViewModels test run green: `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj`

- [ ] **Step 3: Implement**

  (a) `KeyboardViewModel.cs` (lines 100-110) — replace the nearest-key loop body:

```csharp
        foreach (var (id, center) in keyScreenPositions)
        {
            double dist = gazePoint.DistanceSquaredTo(center);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }
```

  (b) `DragTrailViewModel.cs` `UpdateReleaseButtonPosition` — replace the dx/dy check:

```csharp
    private void UpdateReleaseButtonPosition(PhysicalPoint cursorPos)
    {
        var offset = DpiHelper.LogicalToPhysicalX(UIConstants.DragReleaseButtonOffset);
        var btnRadius = DpiHelper.LogicalToPhysicalX(UIConstants.DragButtonRadius);

        if (cursorPos.DistanceSquaredTo(ReleaseButtonCenter) <= btnRadius * btnRadius)
            return;

        ReleaseButtonCenter = new PhysicalPoint(cursorPos.X - offset, cursorPos.Y - offset);
    }
```

  (c) `ScrollOverlayViewModel.cs` (lines 90-92) — keep `dx`/`dy` (needed individually for the offsets; note `dy` is Y-inverted) but derive `dist` from the helper:

```csharp
        double dx = message.Position.X - _anchor.X;
        double dy = _anchor.Y - message.Position.Y;
        double dist = message.Position.DistanceTo(_anchor);
```

- [ ] **Step 4: Run tests to verify pass**
  `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs src/EyeCtrl.ViewModels/DragTrailViewModel.cs src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs && git commit -m "refactor: use PhysicalPoint.DistanceSquaredTo/DistanceTo in ViewModels distance math"`

---

### Task D16: Fix Zoom→ManualSelection rename artifacts (fields, comments, setting ids)

**Safety analysis (verified):** the CommunityToolkit `[ObservableProperty]` generator strips the leading `_` and uppercases the first character, so `_ManualSelection`, `_manualSelection` both generate property `ManualSelection` — renaming `_ManualSelection` → `_manualSelection` (and the two analogous fields) does NOT change the generated property name, JSON serialization, or the `OnXChanging` partial-method names. The setting ids `"ManualSelectionActionMs"`/`"ManualSelectionRadius"` are NOT persisted or looked up anywhere (verified: `rg` finds only the declarations) — renaming is safe.

**Files:**
- Modify: src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs:14, src/EyeCtrl.Core/Settings/DwellSettings.cs:15, src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs:15, src/EyeCtrl.ViewModels/ManualSelectionGazeIndicatorViewModel.cs:37-38,67, src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs:59, src/EyeCtrl.ViewModels/SettingsViewModel.cs:65,103
- Test: pure-mechanical — build + full test suite replaces test steps

- [ ] **Step 1-2 (replaced): pre-check** — confirm the generated property names by building and grepping the generated sources are NOT needed: instead run `dotnet build EyeCtrl.slnx` before and after; identical public property names mean no consumer breaks (the compiler enforces it).

- [ ] **Step 3: Implement**

  (a) `EyeCtrlSettings.cs:14`: `private ManualSelectionSettings _ManualSelection = new();` → `private ManualSelectionSettings _manualSelection = new();`

  (b) `DwellSettings.cs:15`: `private int _ManualSelectionActionMenuDwellMs = 600;` → `private int _manualSelectionActionMenuDwellMs = 600;`

  (c) `ManualSelectionSettings.cs:15`: `private int _ManualSelectionGracePeriodMs = 500;` → `private int _manualSelectionGracePeriodMs = 500;`

  (d) `ManualSelectionGazeIndicatorViewModel.cs:37-38` (plain fields, not ObservableProperty) — rename `_ManualSelectionOriginalCenter` → `_manualSelectionOriginalCenter` and `_ManualSelectionWindowCenter` → `_manualSelectionWindowCenter`, updating all uses inside the file (lines 68, 72, 109, 110).

  (e) Mangled comments:
  - `InteractionMessages.cs:59`: `"...not yet converted to the underlying real/unManualSelectioned screen coordinate"` → `"...not yet converted to the underlying real (unmagnified) screen coordinate"`.
  - `ManualSelectionGazeIndicatorViewModel.cs:67`: `// The capture center is the gaze point (what we're ManualSelectioning in on)` → `// The capture center is the gaze point (what we're zooming in on)`.
  - Also fix the comment on line 71: `"...where the overlay is displayed (gaze point in AtGaze mode, screen center in AtCenter mode)"` — keep, it is fine; only line 67 is mangled in this file.

  (f) Setting ids in `SettingsViewModel.cs` — line 65: `"ManualSelectionActionMs"` → `"manualSelectionActionMs"`; line 103: `"ManualSelectionRadius"` → `"manualSelectionRadius"` (matches the camelCase convention of every other id).

- [ ] **Step 4: Verify** — `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj`. Additionally sanity-check serialization round-trip is unaffected: the `SettingsSerializationTests` in Core.Tests must stay green (property names unchanged).

- [ ] **Step 5: Commit**
  `git add -A src && git commit -m "chore: fix mechanical Zoom->ManualSelection rename artifacts (field casing, comments, setting ids)"`

---

### Task D17: Make `ActionButtonSettingItem.OnActivate` get-only

Verified: nothing assigns `OnActivate` outside the constructor (`rg -n "OnActivate" src/` shows only the declaration and ctor assignment; App-layer `OnActivated` hits are unrelated `DwellControl` members).

**Files:**
- Modify: src/EyeCtrl.ViewModels/SettingItemViewModel/ActionButtonSettingItem.cs
- Test: pure-mechanical — build replaces test steps

- [ ] **Step 3: Implement** — full new file body:

```csharp
namespace EyeCtrl.ViewModels;

public class ActionButtonSettingItem : SettingItemViewModel
{
    public Action? OnActivate { get; }

    public ActionButtonSettingItem(string key, string label, Action? onActivate)
        : base(key, label)
    {
        OnActivate = onActivate;
    }
}
```

- [ ] **Step 4: Verify** — `dotnet build EyeCtrl.slnx`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/SettingItemViewModel/ActionButtonSettingItem.cs && git commit -m "refactor: ActionButtonSettingItem.OnActivate is get-only"`

---

### Task D18: Introduce `MessengerViewModelBase`

Evaluation performed: CommunityToolkit 8.4's `ObservableRecipient` fits the existing `IRecipient<T>` + `RegisterAll` usage exactly — `IsActive = true` runs `Messenger.RegisterAll(this)` and `IsActive = false` unregisters. Decision: derive the base from `ObservableRecipient` rather than hand-rolling registration, adding only the `IDisposable` + once-only + cleanup-hook semantics the 11 VMs repeat.

**Files:**
- Create: src/EyeCtrl.ViewModels/MessengerViewModelBase.cs
- Test: tests/EyeCtrl.ViewModels.Tests/MessengerViewModelBaseTests.cs (new file)

- [ ] **Step 1: Write the failing test**:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels;

public class MessengerViewModelBaseTests
{
    private sealed record PingMessage;

    private sealed class TestVm : MessengerViewModelBase, IRecipient<PingMessage>
    {
        public int Received;
        public int Cleaned;
        public TestVm(IMessenger messenger) : base(messenger) { }
        public void Receive(PingMessage message) => Received++;
        protected override void OnDispose() => Cleaned++;
    }

    [Fact]
    public void Construction_RegistersAllRecipients()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestVm(messenger);

        messenger.Send(new PingMessage());

        Check.That(vm.Received).IsEqualTo(1);
    }

    [Fact]
    public void Dispose_Unregisters_AndIsIdempotent()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestVm(messenger);

        vm.Dispose();
        vm.Dispose();

        messenger.Send(new PingMessage());
        Check.That(vm.Received).IsEqualTo(0);
        Check.That(vm.Cleaned).IsEqualTo(1); // OnDispose ran exactly once
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — compile error (`MessengerViewModelBase` missing):
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~MessengerViewModelBaseTests"`

- [ ] **Step 3: Implement** — new file `src/EyeCtrl.ViewModels/MessengerViewModelBase.cs`:

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Base class for ViewModels that register <see cref="IRecipient{TMessage}"/> handlers on an
/// <see cref="IMessenger"/> and must unregister exactly once on Dispose. Replaces the
/// hand-rolled "_messenger + _disposed + RegisterAll + Dispose{UnregisterAll}" boilerplate.
/// Built on <see cref="ObservableRecipient"/>: IsActive = true registers every IRecipient
/// implementation of the concrete type; IsActive = false unregisters them all.
/// </summary>
public abstract class MessengerViewModelBase : ObservableRecipient, IDisposable
{
    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    protected bool IsDisposed { get; private set; }

    /// <summary>
    /// Registers all IRecipient message handlers immediately. NOTE: this runs before the
    /// derived constructor body, so derived state (e.g. a DwellDetector) is not yet
    /// initialized at registration time. That is safe in this codebase because messages
    /// are never sent while ViewModels are being constructed; do not start sending
    /// messages from constructors.
    /// </summary>
    protected MessengerViewModelBase(IMessenger messenger) : base(messenger)
    {
        IsActive = true;
    }

    /// <summary>Hook for derived cleanup (event unhooking, owned disposables). Called exactly once.</summary>
    protected virtual void OnDispose() { }

    public void Dispose()
    {
        if (IsDisposed) return;
        OnDispose();
        IsActive = false; // Messenger.UnregisterAll(this)
        IsDisposed = true;
    }
}
```

- [ ] **Step 4: Run tests to verify pass** — same command as Step 2, then `dotnet build EyeCtrl.slnx`.

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/MessengerViewModelBase.cs tests/EyeCtrl.ViewModels.Tests/MessengerViewModelBaseTests.cs && git commit -m "refactor: MessengerViewModelBase (ObservableRecipient + IDisposable) for VM messenger boilerplate"`

---

### Task D19: Introduce `DwellOverlayViewModelBase`

**Files:**
- Create: src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs
- Test: tests/EyeCtrl.ViewModels.Tests/DwellOverlayViewModelBaseTests.cs (new file)

- [ ] **Step 1: Write the failing test**:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

public class DwellOverlayViewModelBaseTests
{
    private sealed partial class TestOverlayVm : DwellOverlayViewModelBase
    {
        public string? Activated;
        public double LastProgress = -1;

        public TestOverlayVm(IMessenger messenger) : base(messenger, dwellCooldownMs: 400) { }

        public void AddTestTarget(PhysicalPoint center, double radius, int dwellMs) =>
            DwellDetector.AddTarget(new DwellTarget("t1", center, radius, dwellMs));

        protected override void OnTargetActivated(string targetId) => Activated = targetId;
        protected override void OnTargetProgress(string targetId, double progress) => LastProgress = progress;
    }

    [Fact]
    public void Gaze_WhileVisible_DrivesDwellToActivation()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = true;

        long dwellUs = 600 * MathConstants.MicrosecondsPerMillisecond;
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000 + dwellUs));

        Check.That(vm.Activated).IsEqualTo("t1");
    }

    [Fact]
    public void Gaze_WhileHidden_IsIgnored()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = false;

        long dwellUs = 600 * MathConstants.MicrosecondsPerMillisecond;
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000 + dwellUs));

        Check.That(vm.Activated).IsNull();
    }

    [Fact]
    public void Dispose_UnhooksDetectorEvents_AndUnregisters()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        vm.Dispose();
        var ex = Record.Exception(() => vm.Dispose());
        Check.That(ex).IsNull();
    }
}
```

  (If `IsVisible`'s setter is not public via the generated property, adjust the test to set it through a public helper — the generated `[ObservableProperty]` property IS public with a public setter, so direct assignment works.)

- [ ] **Step 2: Run test to verify it fails** — compile error (class missing).

- [ ] **Step 3: Implement** — new file `src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs`:

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Base class for overlay ViewModels that own a single <see cref="Core.Processing.DwellDetector"/>
/// and feed it gaze updates while visible. Derived classes add/clear targets on the protected
/// <see cref="DwellDetector"/> and react in <see cref="OnTargetActivated"/> /
/// <see cref="OnTargetProgress"/>. Override <see cref="Receive"/> to change the visibility
/// gating (e.g. always-on toolbars or IsActive-gated drags).
/// </summary>
public abstract partial class DwellOverlayViewModelBase : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>
{
    [ObservableProperty] private bool _isVisible;

    /// <summary>The dwell detector owned by this overlay. Event wiring/unwiring is handled here.</summary>
    protected DwellDetector DwellDetector { get; }

    protected DwellOverlayViewModelBase(IMessenger messenger, int dwellCooldownMs)
        : base(messenger)
    {
        DwellDetector = new DwellDetector(dwellCooldownMs);
        DwellDetector.TargetActivated += OnTargetActivated;
        DwellDetector.TargetProgress += OnTargetProgress;
    }

    /// <summary>Called when a dwell target completes.</summary>
    protected abstract void OnTargetActivated(string targetId);

    /// <summary>Called with dwell progress (0..1) while gaze is on a target.</summary>
    protected abstract void OnTargetProgress(string targetId, double progress);

    /// <summary>Feeds gaze into the detector while the overlay is visible.</summary>
    public virtual void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    protected override void OnDispose()
    {
        DwellDetector.TargetActivated -= OnTargetActivated;
        DwellDetector.TargetProgress -= OnTargetProgress;
    }
}
```

- [ ] **Step 4: Run tests to verify pass** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~DwellOverlayViewModelBaseTests"`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs tests/EyeCtrl.ViewModels.Tests/DwellOverlayViewModelBaseTests.cs && git commit -m "refactor: DwellOverlayViewModelBase for the repeated detector+gaze-gating overlay pattern"`

---

### Task D20: Migrate batch 1 — `CursorCircleViewModel`, `ActivationButtonViewModel`

No new tests: the existing suites (`CursorCircleViewModelTests`, `ActivationButtonViewModelTests`, `ViewModelDisposableTests`) are the regression net; constructor signatures are unchanged.

**Files:**
- Modify: src/EyeCtrl.ViewModels/CursorCircleViewModel.cs, src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs

- [ ] **Step 1-2 (replaced): baseline** — `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj` green.

- [ ] **Step 3: Implement**

  (a) `CursorCircleViewModel.cs` — MessengerViewModelBase only (no dwell). Change declaration + ctor, delete `_messenger`, `_disposed`, and `Dispose()`:

```csharp
public partial class CursorCircleViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>,
    IRecipient<ExternalDwellActiveMessage>
{
    private readonly EyeCtrlSettings _settings;

    private bool _stateAllowsCursor = true;
    private bool _externalDwellActive;

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private bool _isVisible = true;

    public double Size => _settings.Cursor.CircleSize;
    public string CircleColor => _settings.Cursor.CircleColor;
    public double Opacity => _settings.Cursor.CircleOpacity;
    public double Thickness => _settings.Cursor.CircleThickness;

    public CursorCircleViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger)
    {
        _settings = settings;
    }
```

  All three `Receive` methods and `UpdateVisibility()` are unchanged; the whole `Dispose()` block is deleted (base provides it). Keep the explanatory comment above `_stateAllowsCursor`.

  (b) `ActivationButtonViewModel.cs` — DwellOverlayViewModelBase. Full new file body (behavior identical; `IsVisible` now inherited):

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActivationButtonViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>
{
    private const string TargetId = "activate";

    private readonly EyeCtrlSettings _settings;

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private double _dwellProgress;

    public ActivationButtonViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        _settings = settings;
    }

    public void Receive(ShowActivationButtonMessage message)
    {
        Position = message.Position;
        IsVisible = true;
        DwellProgress = 0;
        DwellDetector.ClearTargets();
        DwellDetector.AddTarget(new DwellTarget(TargetId,
            message.Position,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ActivationButtonDwellMs));
    }

    public void Receive(HideActivationButtonMessage message)
    {
        IsVisible = false;
        DwellProgress = 0;
        DwellDetector.ClearTargets();
    }

    protected override void OnTargetActivated(string targetId) =>
        Messenger.Send(new ActivationCompletedMessage());

    protected override void OnTargetProgress(string targetId, double progress) =>
        DwellProgress = progress;
}
```

- [ ] **Step 4: Run tests to verify pass** — `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj`

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/CursorCircleViewModel.cs src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs && git commit -m "refactor: migrate CursorCircle + ActivationButton VMs to messenger/dwell base classes"`

---

### Task D21: Migrate batch 2 — `GlassBackdropViewModel`, `ActionMenuViewModel`, `ManualSelectionOverlayViewModel`

All three follow the identical pattern: derive `DwellOverlayViewModelBase`, drop `IRecipient<GazeCursorMovedMessage>` from the declaration (base implements it), delete `_messenger`/`_disposed`/`_dwellDetector` fields, delete `Receive(GazeCursorMovedMessage)` and `Dispose()`, convert `OnTargetActivated`/`OnTargetProgress` from `private` to `protected override`, replace `_dwellDetector.` → `DwellDetector.` and `_messenger.` → `Messenger.`.

**Files:**
- Modify: src/EyeCtrl.ViewModels/GlassBackdropViewModel.cs, src/EyeCtrl.ViewModels/ActionMenuViewModel.cs, src/EyeCtrl.ViewModels/ManualSelectionOverlayViewModel.cs

- [ ] **Step 1-2 (replaced): baseline** — `dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj` green.

- [ ] **Step 3: Implement** — exact edits per VM (post-D11/D12 state):

  (a) `GlassBackdropViewModel.cs` declaration + ctor:

```csharp
public partial class GlassBackdropViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowBackdropMessage>,
    IRecipient<HideBackdropMessage>
{
    private const string HideTargetId = "backdrop_hide";

    private readonly EyeCtrlSettings _settings;

    [ObservableProperty] private PhysicalPoint _hideButtonCenter = new(0, 0);
    [ObservableProperty] private double _hideButtonProgress;

    public GlassBackdropViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        _settings = settings;
    }
```

  `Receive(ShowBackdropMessage)` / `Receive(HideBackdropMessage)` bodies unchanged except `_dwellDetector` → `DwellDetector`. Delete `Receive(GazeCursorMovedMessage)` (base gating on `IsVisible` is identical). Handlers become:

```csharp
    protected override void OnTargetActivated(string targetId)
    {
        if (targetId == HideTargetId)
            Messenger.Send(new BackdropDismissMessage());
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == HideTargetId)
            HideButtonProgress = progress;
    }
```

  Delete `Dispose()` entirely.

  (b) `ActionMenuViewModel.cs` declaration + ctor:

```csharp
public partial class ActionMenuViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowActionMenuMessage>,
    IRecipient<HideActionMenuMessage>
{
    private readonly Dictionary<string, ActionMenuItemViewModel> _itemLookup = new();

    [ObservableProperty] private PhysicalPoint _center = new(0, 0);
    [ObservableProperty] private bool _showBack;
    [ObservableProperty] private double _effectiveRingRadius;
```

  (delete the `_isVisible` ObservableProperty — inherited; delete `_messenger`, `_dwellDetector`, `_disposed`)

```csharp
    public ActionMenuViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
    }
```

  In `Receive(ShowActionMenuMessage)` / `Receive(HideActionMenuMessage)`: `_dwellDetector` → `DwellDetector`. Delete `Receive(GazeCursorMovedMessage)`. Convert handlers:

```csharp
    protected override void OnTargetActivated(string targetId)
    {
        if (!_itemLookup.TryGetValue(targetId, out var item)) return;

        if (item.IsBackButton)
            Messenger.Send(new BackSelectedMessage());
        else if (item.Action.HasValue)
            Messenger.Send(new ActionSelectedMessage(item.Action.Value));
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (_itemLookup.TryGetValue(targetId, out var item))
            item.DwellProgress = progress;
    }
```

  Delete `Dispose()`.

  (c) `ManualSelectionOverlayViewModel.cs` declaration + ctor (post-D11 it has `_settings`):

```csharp
public partial class ManualSelectionOverlayViewModel : DwellOverlayViewModelBase,
    IRecipient<StartManualSelectionMessage>,
    IRecipient<EndManualSelectionMessage>,
    IRecipient<ManualSelectionHiddenMessage>
{
    private const string CloseTargetId = "ManualSelection_close";
    private const string RecaptureTargetId = "ManualSelection_recapture";

    private readonly EyeCtrlSettings _settings;
```

  (delete `_isVisible` ObservableProperty — inherited; delete `_messenger`, `_dwellDetector`, `_disposed`)

```csharp
    public ManualSelectionOverlayViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        _settings = settings;
    }
```

  All three `Receive` message handlers keep their bodies (`_dwellDetector` → `DwellDetector`); delete `Receive(GazeCursorMovedMessage)`; convert `OnTargetActivated`/`OnTargetProgress` to `protected override` with `_messenger.Send` → `Messenger.Send`; delete `Dispose()`.

- [ ] **Step 4: Run tests to verify pass** — `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj` (covers GlassBackdropViewModelTests, ActionMenuViewModelTests, ManualSelectionOverlayViewModelTests, ViewModelDisposableTests).

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/GlassBackdropViewModel.cs src/EyeCtrl.ViewModels/ActionMenuViewModel.cs src/EyeCtrl.ViewModels/ManualSelectionOverlayViewModel.cs && git commit -m "refactor: migrate GlassBackdrop, ActionMenu, ManualSelectionOverlay VMs to DwellOverlayViewModelBase"`

---

### Task D22: Migrate batch 3 — `ScrollOverlayViewModel`, `ToolbarViewModel`, `DragTrailViewModel`

These override the base `Receive` (different gating): Scroll gates on `IsVisible` but does extra work; Toolbar is ungated (always-on); DragTrail gates on `IsActive`.

**Files:**
- Modify: src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs, src/EyeCtrl.ViewModels/ToolbarViewModel.cs, src/EyeCtrl.ViewModels/DragTrailViewModel.cs

- [ ] **Step 1-2 (replaced): baseline** — full ViewModels test run green.

- [ ] **Step 3: Implement**

  (a) `ScrollOverlayViewModel.cs` (post-D10/D15 state) — declaration + ctor:

```csharp
public partial class ScrollOverlayViewModel : DwellOverlayViewModelBase
{
    private const string StopButtonTargetId = "scroll-stop";
    private const double StopButtonRadiusPx = 30;
    private const double StopZoneRadiusPx = 50;
    private const double MaxOffsetPx = 200.0;

    private readonly ScrollSettings _scrollSettings;
    private readonly DwellSettings _dwellSettings;
```

  (delete `_dwellDetector`, `_messenger`, `_disposed`, and the `_isVisible` ObservableProperty — inherited)

```csharp
    public ScrollOverlayViewModel(IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings)
        : base(messenger, dwellSettings.DwellCooldownMs)
    {
        _scrollSettings = scrollSettings;
        _dwellSettings = dwellSettings;
    }

    protected override void OnTargetActivated(string targetId) => ExitRequested?.Invoke();

    protected override void OnTargetProgress(string targetId, double progress) => StopButtonProgress = progress;
```

  `EnterScrollMode`/`ExitScrollMode`: `_dwellDetector` → `DwellDetector`. Receive becomes an override:

```csharp
    public override void Receive(GazeCursorMovedMessage message)
    {
        if (!IsVisible) return;

        DwellDetector.Update(message.Position, message.TimestampMicroseconds);
```

  (rest of the method body unchanged). Delete `Dispose()`.

  (b) `ToolbarViewModel.cs` — declaration + ctor:

```csharp
public partial class ToolbarViewModel : DwellOverlayViewModelBase,
    IRecipient<StateChangedMessage>,
    IRecipient<CalibrationCompletedMessage>,
    IRecipient<CalibrationCancelledMessage>,
    IRecipient<SelectionModeChangedMessage>
{
    private readonly Dictionary<string, ToolbarButtonViewModel> _buttonLookup = new();

    [ObservableProperty] private bool _isPaused;

    private SelectionMode _currentSelectionMode = SelectionMode.None;
```

  (delete `_messenger`, `_dwellDetector`, `_disposed`)

```csharp
    public ToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause", "⏸"),
            ("SelectionMode", "⊕"),
            ("Keyboard", "⌨"),
            ("Calibrate", "⌖"),
            ("Settings", "⚙"),
        };

        foreach (var (id, symbol) in buttons)
        {
            var btn = new ToolbarButtonViewModel(id, symbol);
            Buttons.Add(btn);
            _buttonLookup[id] = btn;
            DwellDetector.AddTarget(new DwellTarget(id,
                new PhysicalPoint(0, 0),
                DpiHelper.LogicalToPhysicalX(settings.Toolbar.Size / 2),
                settings.Dwell.ToolbarButtonDwellMs));
        }
    }

    // The toolbar is always on screen — feed gaze unconditionally (no IsVisible gate).
    public override void Receive(GazeCursorMovedMessage message) =>
        DwellDetector.Update(message.Position, message.TimestampMicroseconds);
```

  `UpdateButtonScreenPosition` becomes `DwellDetector.UpdateTargetCenter(buttonId, screenCenter);`. `OnTargetActivated`/`OnTargetProgress` become `protected override` with `_messenger.Send` → `Messenger.Send`. Delete `Dispose()`. Note: the inherited `IsVisible` property is unused by the toolbar (always-on overlay) — acceptable; documented by the Receive override comment.

  (c) `DragTrailViewModel.cs` (post-D14/D15 state) — declaration + ctor:

```csharp
public partial class DragTrailViewModel : DwellOverlayViewModelBase,
    IRecipient<StartDragTrailMessage>
{
    private const string ReleaseTargetId = "Drag_release";
    private const string CancelTargetId = "Drag_cancel";
    private const string BackTargetId = "Drag_back";

    private readonly EyeCtrlSettings _settings;
    private readonly FixationDetector _settleDetector;
```

  (delete `_messenger`, `_dwellDetector`, `_disposed`)

```csharp
    public DragTrailViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        _settings = settings;

        _settleDetector = new FixationDetector(
            settings.Drag.SettleRadiusPixels,
            settings.Drag.SettleDwellMs);
        _settleDetector.FixationStarted += OnSettleStarted;
        _settleDetector.FixationEnded += OnSettleEnded;
    }
```

  Receive becomes an override gated on the drag-active flag (not `IsVisible`) — but first resolve a name collision.

  **CAREFUL — name collision:** `ObservableRecipient` (base of `MessengerViewModelBase`) already has a `bool IsActive` property with registration semantics, and `DragTrailViewModel` declares `[ObservableProperty] private bool _isActive` — these WILL collide. Resolve by renaming the drag-state property: `_isActive` → `_isDragActive` (generated property `IsDragActive`). Update all uses in the file (`IsActive = true/false` in `Receive(StartDragTrailMessage)`, `CompleteDrag`, `CancelDrag`, the `Receive` gate) and in the App layer/tests: run `rg -n "\.IsActive" src/EyeCtrl.App tests/EyeCtrl.ViewModels.Tests --glob "*DragTrail*"` plus `rg -n "IsActive" src/EyeCtrl.App/Overlays` and update every `DragTrailViewModel.IsActive` consumer to `IsDragActive`. So the gate reads:

```csharp
    public override void Receive(GazeCursorMovedMessage message)
    {
        if (!IsDragActive) return;

        CursorPosition = message.Position;
        DwellDetector.Update(message.Position, message.TimestampMicroseconds);
        ...
```

  `OnTargetActivated` / `OnTargetProgress` become `protected override` (`OnTargetProgress` keeps its empty body). Dispose is replaced by:

```csharp
    protected override void OnDispose()
    {
        base.OnDispose();
        _settleDetector.FixationStarted -= OnSettleStarted;
        _settleDetector.FixationEnded -= OnSettleEnded;
    }
```

- [ ] **Step 4: Run tests to verify pass** — `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj` and (because of the `IsDragActive` rename touching App code) `dotnet test --filter "Category!=UI"`.

- [ ] **Step 5: Commit**
  `git add -A src tests && git commit -m "refactor: migrate Scroll, Toolbar, DragTrail VMs to DwellOverlayViewModelBase (DragTrail IsActive -> IsDragActive)"`

---

### Task D23: Migrate batch 4 — `KeyboardViewModel`, `ManualSelectionGazeIndicatorViewModel`, `CalibrationViewModel`, `SettingsWindowViewModel`

These four use `MessengerViewModelBase` only (no single shared DwellDetector pattern: Keyboard has custom nearest-key logic, the indicator has no detector, Calibration has none, SettingsWindow has four detectors).

**Files:**
- Modify: src/EyeCtrl.ViewModels/KeyboardViewModel.cs, src/EyeCtrl.ViewModels/ManualSelectionGazeIndicatorViewModel.cs, src/EyeCtrl.ViewModels/CalibrationViewModel.cs, src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs

- [ ] **Step 1-2 (replaced): baseline** — full ViewModels test run green.

- [ ] **Step 3: Implement**

  (a) `KeyboardViewModel.cs`:

```csharp
public partial class KeyboardViewModel : MessengerViewModelBase,
    IRecipient<ShowKeyboardMessage>,
    IRecipient<HideKeyboardMessage>
{
    private readonly EyeCtrlSettings _settings;
```

  (delete `_messenger`, `_disposed`)

```csharp
    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger)
    {
        _settings = settings;
    }
```

  Replace the remaining 8 `_messenger.` references in method bodies with `Messenger.` (verify count with `rg -c "_messenger" src/EyeCtrl.ViewModels/KeyboardViewModel.cs` — currently 10 total including field + ctor). Delete `Dispose()` (it only unregisters — grep the end of the file to confirm before deleting).

  (b) `ManualSelectionGazeIndicatorViewModel.cs` (post-D3/D16 state):

```csharp
public partial class ManualSelectionGazeIndicatorViewModel : MessengerViewModelBase,
    IRecipient<StartManualSelectionMessage>,
    IRecipient<EndManualSelectionMessage>,
    IRecipient<ManualSelectionHiddenMessage>,
    IRecipient<UpdateManualSelectionIndicatorPositionMessage>,
    IRecipient<UpdateManualSelectionIndicatorProgressMessage>,
    IRecipient<ResetManualSelectionIndicatorMessage>
{
```

  (delete `_messenger`, `_disposed` fields)

```csharp
    public ManualSelectionGazeIndicatorViewModel(IMessenger messenger)
        : base(messenger)
    {
    }
```

  Delete `Dispose()`. All other members unchanged.

  (c) `CalibrationViewModel.cs` (post-D2 state):

```csharp
public partial class CalibrationViewModel : MessengerViewModelBase,
    IRecipient<CalibrationRequestedMessage>,
    IRecipient<CalibrationClearRequestedMessage>
{
```

  (delete `_messenger`, `_disposed`; keep every other field including `_sessionGeneration`). Constructor tail changes from `messenger.RegisterAll(this);` to `: base(messenger)` in the signature:

```csharp
    public CalibrationViewModel(
        CalibrationSettings settings,
        CalibrationService calibrationService,
        IMessenger messenger,
        IGazeProvider gazeProvider,
        PhysicalPoint screenSize,
        string monitorDeviceName,
        string? calibrationFilePath = null)
        : base(messenger)
    {
        _settings = settings;
        _calibrationService = calibrationService;
        _gazeProvider = gazeProvider;
        _screenSize = screenSize;
        _monitorDeviceName = monitorDeviceName;
        _calibrationFilePath = calibrationFilePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "calibration.json");
        _targetColor = settings.TargetColor;
        _syncContext = SynchronizationContext.Current;
    }
```

  Replace every `_messenger.Send(` with `Messenger.Send(` (8 sites) and the continuation guard `if (_disposed) return;` with `if (IsDisposed) return;`. Replace `Dispose()` with:

```csharp
    protected override void OnDispose()
    {
        _gazeProvider.GazePointReceived -= OnRawGaze;
    }
```

  (d) `SettingsWindowViewModel.cs`:

```csharp
public partial class SettingsWindowViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IDwellRegistrar
{
```

  (delete `_messenger`, `_disposed`). Constructor: add `: base(messenger)` and remove the `_messenger = messenger;` and trailing `messenger.RegisterAll(this);` lines. In `Receive(GazeCursorMovedMessage)` replace `if (_disposed) return;` with `if (IsDisposed) return;`. In `OnTargetActivated` replace `_messenger.Send(new CloseSettingsMessage());` with `Messenger.Send(new CloseSettingsMessage());`. Replace `Dispose()` with:

```csharp
    protected override void OnDispose()
    {
        _settingsViewModel.Dispose();
    }
```

- [ ] **Step 4: Run tests to verify pass** — `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj` (KeyboardViewModelTests, KeyboardViewModelOrchestratorTests, CalibrationViewModelTests, SettingsWindowViewModelTests, ManualSelectionGazeIndicatorViewModelTests, ViewModelDisposableTests all green).

- [ ] **Step 5: Commit**
  `git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs src/EyeCtrl.ViewModels/ManualSelectionGazeIndicatorViewModel.cs src/EyeCtrl.ViewModels/CalibrationViewModel.cs src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs && git commit -m "refactor: migrate Keyboard, GazeIndicator, Calibration, SettingsWindow VMs to MessengerViewModelBase"`

---

### Task D24: Extract shared Tobii SDK probe into `src/Tobii.props`

The TobiiSdkPath/Exists/TOBII_AVAILABLE MSBuild block is duplicated in `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj:18-38` and `tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj:18-26`.

**Files:**
- Create: src/Tobii.props
- Modify: src/EyeCtrl.Platform/EyeCtrl.Platform.csproj, tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
- Test: build-passes replaces test steps (MSBuild refactor; no runtime behavior change)

- [ ] **Step 3: Implement**

  (a) New file `src/Tobii.props`:

```xml
<Project>
  <!-- Shared Tobii Stream Engine SDK probe. Imported by EyeCtrl.Platform and
       EyeCtrl.Platform.Tests so TOBII_AVAILABLE is defined identically in both.
       Override the install location via -p:TobiiSdkPath="...". -->
  <PropertyGroup>
    <TobiiSdkPath Condition="'$(TobiiSdkPath)' == ''">C:\Program Files\Tobii\Tobii EyeX\</TobiiSdkPath>
    <TobiiSdkPath>$([MSBuild]::EnsureTrailingSlash($(TobiiSdkPath)))</TobiiSdkPath>
    <TobiiManagedDll>$(TobiiSdkPath)Tobii.StreamEngine.Net.dll</TobiiManagedDll>
    <TobiiNativeDll>$(TobiiSdkPath)tobii_stream_engine.dll</TobiiNativeDll>
    <TobiiSdkAvailable Condition="$([MSBuild]::IsOSPlatform('Windows')) And Exists('$(TobiiManagedDll)') And Exists('$(TobiiNativeDll)')">true</TobiiSdkAvailable>
  </PropertyGroup>

  <PropertyGroup Condition="'$(TobiiSdkAvailable)' == 'true'">
    <DefineConstants>$(DefineConstants);TOBII_AVAILABLE</DefineConstants>
  </PropertyGroup>
</Project>
```

  (b) `EyeCtrl.Platform.csproj` — replace lines 16-38 (the probe `PropertyGroup`, the conditional `ItemGroup`, and the `DefineConstants` `PropertyGroup`) with:

```xml
  <!-- Tobii Stream Engine: shared probe defines TobiiSdkAvailable + TOBII_AVAILABLE. -->
  <Import Project="..\Tobii.props" />

  <ItemGroup Condition="'$(TobiiSdkAvailable)' == 'true'">
    <Reference Include="Tobii.StreamEngine.Net">
      <HintPath>$(TobiiManagedDll)</HintPath>
      <Private>true</Private>
    </Reference>
    <Content Include="$(TobiiNativeDll)">
      <Link>tobii_stream_engine.dll</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

  (c) `EyeCtrl.Platform.Tests.csproj` — replace lines 17-26 (both probe `PropertyGroup`s and the mirror comment) with:

```xml
  <!-- Mirror TOBII_AVAILABLE from the Platform project via the shared probe. -->
  <Import Project="..\..\src\Tobii.props" />
```

- [ ] **Step 4: Verify** — `dotnet build EyeCtrl.slnx` on this machine (SDK installed: TOBII_AVAILABLE path), then simulate the SDK-absent path: `dotnet build EyeCtrl.slnx -p:TobiiSdkPath="C:\nonexistent\"` — both must succeed; the second must compile `TobiiStreamEngineProvider` out (`#if TOBII_AVAILABLE`). Then `dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj`.

- [ ] **Step 5: Commit**
  `git add src/Tobii.props src/EyeCtrl.Platform/EyeCtrl.Platform.csproj tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj && git commit -m "build: extract duplicated Tobii SDK probe into shared src/Tobii.props"`

---

## Deferred (noted, not planned)

- **Generic `ValueSettingItemViewModel<T>` base** for Slider/Toggle/Combo/Color setting items — audited as optional; the four classes are small and stable (YAGNI until a fifth item type appears).
- **`SettingsViewModel` migration to `MessengerViewModelBase`** — after D6 it registers exactly one recipient; it was not in the audited 12-VM list and its `SettingsService` coupling makes it a poor fit for the overlay-oriented bases. Revisit if it grows more handlers.
- **Merging `EndManualSelectionMessage` / `ManualSelectionHiddenMessage`** — deliberately kept separate (different semantics, documented in D7).
- **Exposing `Cursor.MaxGapInterpolationMs` and `PositionIndicatorStyle` in the settings UI** — left in the intentionally-hidden list (advanced tuning); D4 gives them validation regardless.
- **A `DwellSettings`-driven default for `DwellDetector()`'s parameterless constructor** — out of scope; D10 removed the last uncooled `new DwellDetector()` in ViewModels.

---

## Phase C: SDK, EyeCare & LM

This phase fixes the verified bugs in the external-dwell SDK (WPF head element leak, stale rects after window moves, `DwellClient` races), the EyeCare pipeline (disabled-gap counted as active time, fatigue-score flicker on blinks, missing unsubscription), and the LM predictor (injected-tokenizer disposal, multi-word suggestion spam, worker/dispose races), then lands the smaller refactors (shared head core, live `Dwell.Mode`, shared wire-JSON options, dead field, TFM comments, sealing). All findings were re-verified against current `main`; none are stale, though two need adjusted fixes (noted inline in C6 and C8). Cross-phase touchpoints: C13 changes exactly one line of `src/EyeCtrl.App/Services/ExternalDwellService.cs` (the App phase owns the rest of that file), and C14 removes one constructor argument at `src/EyeCtrl.App/Services/AppBootstrapper.cs:213-219`; both changes are mechanical and must not be expanded.

Conventions: xUnit v3 + NFluent (`Check.That(...)`, never `Assert.*`); build with `dotnet build EyeCtrl.slnx`; run a single test project with `dotnet test tests/<proj>/<proj>.csproj`. Tasks are ordered bugs-first; C2 is a prerequisite of C3, C11/C12 quote code as it stands after C3.

---

### Task C1: New tests/EyeCtrl.Sdk.Wpf.Tests project + fix WPF head element leak (attached-property entry storage)

`src/EyeCtrl.Sdk.Wpf/Dwell.cs:31` keeps a static `ConcurrentDictionary<FrameworkElement, DwellEntry> _byElement`. Entries are only removed in `Unregister` (i.e. when `Dwell.Enabled` is set back to `false`); `OnUnloaded` (line 82-86) unregisters from the client but never removes from the map, so every element that ever had `Dwell.Enabled="True"` is pinned for process lifetime. Fix mirrors the WinUI head (`src/EyeCtrl.Sdk.WinUI/Dwell.cs:39-40`): stash the entry on the element itself via a private attached `DependencyProperty`.

**Files:**
- Create: `tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj`
- Create: `tests/EyeCtrl.Sdk.Wpf.Tests/DwellLeakTests.cs`
- Modify: `src/EyeCtrl.Sdk.Wpf/Dwell.cs:3,30-87`
- Modify: `EyeCtrl.slnx` (add the test project to the `/tests/` folder)
- Test: `tests/EyeCtrl.Sdk.Wpf.Tests/DwellLeakTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj`. `tests/Directory.Build.props` already supplies xunit.v3 3.2.2, NFluent, OutputType Exe, and the `Xunit`/`NFluent` global usings. Sdk.Wpf targets `net8.0-windows`; a `net10.0-windows` test project can reference it directly (higher TFM referencing lower is allowed — unlike WinUI, WPF needs no special runtime, so no `Compile Include` linking is required). `Xunit.StaFact` 3.0.13 is the same version `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj:12` already uses with xunit v3:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Xunit.StaFact" Version="3.0.13" />
    <ProjectReference Include="..\..\src\EyeCtrl.Sdk.Wpf\EyeCtrl.Sdk.Wpf.csproj" />
  </ItemGroup>
</Project>
```

Register it in `EyeCtrl.slnx` — add inside `<Folder Name="/tests/">`:

```xml
    <Project Path="tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj" />
```

(`dotnet sln EyeCtrl.slnx add tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj` does the same.)

Create `tests/EyeCtrl.Sdk.Wpf.Tests/DwellLeakTests.cs`:

```csharp
namespace EyeCtrl.Sdk.Wpf.Tests;

using System.Runtime.CompilerServices;
using System.Windows.Controls;
using EyeCtrl.Sdk.Wpf;

public class DwellLeakTests
{
    // NoInlining so the JIT cannot extend the Button local's lifetime into the caller,
    // which would keep the WeakReference alive regardless of the SDK's behavior.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateEnabledElement()
    {
        var btn = new Button();
        Dwell.SetEnabled(btn, true);
        return new WeakReference(btn);
    }

    [StaFact]
    public void EnabledElement_IsCollectable_WhenDropped()
    {
        // A never-loaded element with Dwell.Enabled=true must not be pinned by SDK statics.
        var wr = CreateEnabledElement();

        for (var i = 0; i < 3 && wr.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Check.That(wr.IsAlive).IsFalse();
    }
}
```

(The element is never loaded, so `DwellClient.RegisterAdapter` is never called and the client holds no reference; the only pin pre-fix is the static `_byElement` map. `LayoutUpdated` subscriptions do not pin — WPF's `LayoutEventList` holds handlers weakly.)

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj
```

Expected: `EnabledElement_IsCollectable_WhenDropped` FAILS with NFluent "The checked boolean is true whereas it must be false" — the static `ConcurrentDictionary` key pins the Button.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.Sdk.Wpf/Dwell.cs`, replace the storage block and lifecycle methods. Current code (lines 30-87):

```csharp
    private static readonly DwellClient _client = new();
    private static readonly ConcurrentDictionary<FrameworkElement, DwellEntry> _byElement = new();
    private static int _started;
    private static long _idCounter;

    private sealed record DwellEntry(WpfElementAdapter Adapter, EventHandler LayoutHandler);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) Register(el);
        else Unregister(el);
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _client.Start();
    }

    private static void Register(FrameworkElement el)
    {
        EnsureStarted();
        if (_byElement.ContainsKey(el)) return;

        var id = $"wpf_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WpfElementAdapter(id, el, GetMode(el));
        EventHandler layoutHandler = (_, _) => _client.OnAdapterLayoutChanged(adapter.Id);
        var entry = new DwellEntry(adapter, layoutHandler);
        _byElement[el] = entry;

        el.Loaded += OnLoaded;
        el.Unloaded += OnUnloaded;
        el.LayoutUpdated += layoutHandler;

        if (el.IsLoaded) _client.RegisterAdapter(adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (!_byElement.TryRemove(el, out var entry)) return;
        el.Loaded -= OnLoaded;
        el.Unloaded -= OnUnloaded;
        el.LayoutUpdated -= entry.LayoutHandler;
        _client.UnregisterAdapter(entry.Adapter.Id);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && _byElement.TryGetValue(el, out var entry))
            _client.RegisterAdapter(entry.Adapter);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && _byElement.TryGetValue(el, out var entry))
            _client.UnregisterAdapter(entry.Adapter.Id);
    }
```

New code (same region; `using System.Collections.Concurrent;` on line 3 becomes unused and is removed):

```csharp
    // Per-element companion state is stashed on the element itself via a private attached
    // property, so it is collected together with the element — the SDK keeps no static
    // reference to any element (mirrors the WinUI head).
    private static readonly DependencyProperty EntryProperty = DependencyProperty.RegisterAttached(
        "Entry", typeof(object), typeof(Dwell), new PropertyMetadata(null));

    private static readonly DwellClient _client = new();
    private static int _started;
    private static long _idCounter;

    private sealed record DwellEntry(WpfElementAdapter Adapter,
                                     RoutedEventHandler Loaded, RoutedEventHandler Unloaded,
                                     EventHandler LayoutHandler);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) Register(el);
        else Unregister(el);
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _client.Start();
    }

    private static void Register(FrameworkElement el)
    {
        EnsureStarted();
        if (el.GetValue(EntryProperty) is DwellEntry) return;

        var id = $"wpf_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WpfElementAdapter(id, el, GetMode(el));
        EventHandler layoutHandler = (_, _) => _client.OnAdapterLayoutChanged(adapter.Id);
        RoutedEventHandler loaded = (_, _) => _client.RegisterAdapter(adapter);
        RoutedEventHandler unloaded = (_, _) => _client.UnregisterAdapter(adapter.Id);
        el.SetValue(EntryProperty, new DwellEntry(adapter, loaded, unloaded, layoutHandler));

        el.Loaded += loaded;
        el.Unloaded += unloaded;
        el.LayoutUpdated += layoutHandler;

        if (el.IsLoaded) _client.RegisterAdapter(adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (el.GetValue(EntryProperty) is not DwellEntry entry) return;
        el.ClearValue(EntryProperty);
        el.Loaded -= entry.Loaded;
        el.Unloaded -= entry.Unloaded;
        el.LayoutUpdated -= entry.LayoutHandler;
        _client.UnregisterAdapter(entry.Adapter.Id);
    }
```

Also update the class doc comment (lines 7-10) to mention "The SDK holds no static strong reference to any element" (mirroring the WinUI wording).

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add tests/EyeCtrl.Sdk.Wpf.Tests src/EyeCtrl.Sdk.Wpf/Dwell.cs EyeCtrl.slnx
git commit -m "fix(sdk-wpf): stop pinning dwell elements in a static map; add Sdk.Wpf test project"
```

---

### Task C2: Move AdapterGroupTracker from Sdk.WinUI to Sdk.Abstractions

`src/EyeCtrl.Sdk.WinUI/AdapterGroupTracker.cs` is verified framework-agnostic (only `System.Collections.Concurrent`). C3 needs it from the WPF head, so it moves to `EyeCtrl.Sdk.Abstractions` (and becomes `public`, since the heads are separate assemblies). Its tests move from the linked-source WinUI test project to `Sdk.Abstractions.Tests`, which references the Abstractions project directly.

**Files:**
- Create: `src/EyeCtrl.Sdk.Abstractions/AdapterGroupTracker.cs` (moved from `src/EyeCtrl.Sdk.WinUI/AdapterGroupTracker.cs`)
- Delete: `src/EyeCtrl.Sdk.WinUI/AdapterGroupTracker.cs`
- Create: `tests/EyeCtrl.Sdk.Abstractions.Tests/AdapterGroupTrackerTests.cs` (moved from `tests/EyeCtrl.Sdk.WinUI.Tests/AdapterGroupTrackerTests.cs`)
- Delete: `tests/EyeCtrl.Sdk.WinUI.Tests/AdapterGroupTrackerTests.cs`
- Modify: `tests/EyeCtrl.Sdk.WinUI.Tests/EyeCtrl.Sdk.WinUI.Tests.csproj:9` (drop the `Compile Include` for AdapterGroupTracker.cs)
- Test: `tests/EyeCtrl.Sdk.Abstractions.Tests/AdapterGroupTrackerTests.cs`

- [ ] **Step 1: Write the failing test (move the existing tests first)**

`git mv tests/EyeCtrl.Sdk.WinUI.Tests/AdapterGroupTrackerTests.cs tests/EyeCtrl.Sdk.Abstractions.Tests/AdapterGroupTrackerTests.cs`, then edit its header. Current (lines 1-3):

```csharp
namespace EyeCtrl.Sdk.WinUI.Tests;

using EyeCtrl.Sdk.WinUI;
```

New:

```csharp
namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Sdk.Abstractions;
```

Test bodies (6 `[Fact]`s, `NewTracker_IsEmpty` through `AddAfterFullCycle_RestoresNonEmpty`) are unchanged.

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj --filter "FullyQualifiedName~AdapterGroupTrackerTests"
```

Expected: compile error CS0246 "The type or namespace name 'AdapterGroupTracker' could not be found" — the type does not exist in Abstractions yet.

- [ ] **Step 3: Implement**

`git mv src/EyeCtrl.Sdk.WinUI/AdapterGroupTracker.cs src/EyeCtrl.Sdk.Abstractions/AdapterGroupTracker.cs`, then change the header and visibility. Current (lines 1, 10):

```csharp
namespace EyeCtrl.Sdk.WinUI;
...
internal sealed class AdapterGroupTracker
```

New:

```csharp
namespace EyeCtrl.Sdk.Abstractions;
...
public sealed class AdapterGroupTracker
```

Rest of the file (doc comments, `IsEmpty`, `Add`, `Remove`) is unchanged — the existing XML docs satisfy `GenerateDocumentationFile=true` now that it is public.

Consumers: `src/EyeCtrl.Sdk.WinUI/Dwell.cs` already has `using EyeCtrl.Sdk.Abstractions;` (line 4) and references the type unqualified — no change needed there. Remove line 9 from `tests/EyeCtrl.Sdk.WinUI.Tests/EyeCtrl.Sdk.WinUI.Tests.csproj`:

```xml
    <Compile Include="..\..\src\EyeCtrl.Sdk.WinUI\AdapterGroupTracker.cs" Link="AdapterGroupTracker.cs" />
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj
dotnet test tests/EyeCtrl.Sdk.WinUI.Tests/EyeCtrl.Sdk.WinUI.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.Sdk.Abstractions/AdapterGroupTracker.cs src/EyeCtrl.Sdk.WinUI tests/EyeCtrl.Sdk.Abstractions.Tests tests/EyeCtrl.Sdk.WinUI.Tests
git commit -m "refactor(sdk): move framework-agnostic AdapterGroupTracker to Sdk.Abstractions"
```

---

### Task C3: WPF head — per-dispatcher rect-refresh timer (fix stale rects after window move + recover after reconnect)

Verified: after C1 the only rect-refresh trigger in the WPF head is `el.LayoutUpdated`. Moving a window does not fire `LayoutUpdated`, so registered rects stay at the old screen position and gaze at the *old* position still activates the control. Also, `DwellClient.RunAsync` (`src/EyeCtrl.Sdk.Abstractions/DwellClient.cs:81-86`) re-sends rects after reconnect from a background thread where WPF's `PointToScreen` throws (swallowed by `WpfElementAdapter.TryGetScreenRect`'s catch), so nothing recovers until the next layout pass. Fix: adopt the WinUI pattern verbatim — a 200ms per-dispatcher `DispatcherTimer` calling `OnAdapterLayoutChanged` for each loaded adapter (dedupe in `OutboundRectTracker` makes unchanged rects free), torn down via `AdapterGroupTracker` when the last adapter unregisters. `LayoutUpdated` is dropped entirely, matching WinUI.

**Files:**
- Modify: `src/EyeCtrl.Sdk.Wpf/Dwell.cs` (the post-C1 file)
- Test: manual (see Steps 1/4) + existing `tests/EyeCtrl.Sdk.Wpf.Tests` and `tests/EyeCtrl.Sdk.Abstractions.Tests` as regression guards

- [ ] **Step 1: Write the failing test — not practical as a unit test; record the manual repro instead**

Timer-driven screen-rect behavior needs a real shown `Window` + the EyeCtrl pipe service; that is environmental (App.Tests-style UI tests are known to fail headless). Manual repro of the bug BEFORE the fix:
1. Run EyeCtrl (`dotnet run --project src/EyeCtrl.App`) and the sample (`dotnet run --project samples/EyeCtrl.Sdk.Wpf.Sample`).
2. Dwell-activate a sample button to confirm wiring, then drag the sample window ~300px away.
3. Observe: dwelling on the button's OLD screen position still activates it; its new position does nothing.

- [ ] **Step 2: Run test to verify it fails** — perform the manual repro above; the stale-rect behavior confirms the bug on current code.

- [ ] **Step 3: Implement**

In the post-C1 `src/EyeCtrl.Sdk.Wpf/Dwell.cs`: add `using System.Collections.Concurrent;` and `using System.Windows.Threading;`, remove the `LayoutHandler` from `DwellEntry`, and replace the Register/Unregister lifecycle with the group/timer pattern. Replace this post-C1 code:

```csharp
    private static readonly DwellClient _client = new();
    private static int _started;
    private static long _idCounter;

    private sealed record DwellEntry(WpfElementAdapter Adapter,
                                     RoutedEventHandler Loaded, RoutedEventHandler Unloaded,
                                     EventHandler LayoutHandler);
```

with:

```csharp
    private static readonly DwellClient _client = new();
    private static int _started;
    private static long _idCounter;

    // One refresh timer per UI thread (WPF supports multiple dispatchers). Its Tick runs on
    // that dispatcher's thread, so PointToScreen/ActualWidth are read on the element's own
    // thread. Covers window moves (which fire no LayoutUpdated) and re-sends rects after a
    // service reconnect (DwellClient's own resend runs on a background thread where WPF's
    // PointToScreen throws). Unchanged rects are deduped by OutboundRectTracker, so the
    // 200ms tick normally sends nothing.
    //
    // LIFECYCLE: the group is created lazily on first Loaded for that dispatcher and torn
    // down (timer stopped, group evicted) when the last adapter is removed, driven by the
    // shared AdapterGroupTracker (unit-tested in EyeCtrl.Sdk.Abstractions.Tests).
    private sealed class ThreadGroup
    {
        public DispatcherTimer Timer = null!;
        public readonly ConcurrentDictionary<string, WpfElementAdapter> Adapters = new();
        public readonly AdapterGroupTracker Tracker;
        public ThreadGroup(Action onEmpty) => Tracker = new AdapterGroupTracker(onEmpty);
    }

    private static readonly ConcurrentDictionary<Dispatcher, ThreadGroup> _groups = new();

    private sealed record DwellEntry(WpfElementAdapter Adapter,
                                     RoutedEventHandler Loaded, RoutedEventHandler Unloaded);
```

Replace the post-C1 `Register`/`Unregister` bodies with:

```csharp
    private static void Register(FrameworkElement el)
    {
        EnsureStarted();
        if (el.GetValue(EntryProperty) is DwellEntry) return;

        var id = $"wpf_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WpfElementAdapter(id, el, GetMode(el));
        RoutedEventHandler loaded = (_, _) => OnLoaded(el, adapter);
        RoutedEventHandler unloaded = (_, _) => OnUnloaded(el, adapter);
        el.Loaded += loaded;
        el.Unloaded += unloaded;
        el.SetValue(EntryProperty, new DwellEntry(adapter, loaded, unloaded));

        if (el.IsLoaded) OnLoaded(el, adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (el.GetValue(EntryProperty) is not DwellEntry entry) return;
        el.ClearValue(EntryProperty);
        el.Loaded -= entry.Loaded;
        el.Unloaded -= entry.Unloaded;
        OnUnloaded(el, entry.Adapter);
    }

    private static void OnLoaded(FrameworkElement el, WpfElementAdapter adapter)
    {
        var group = _groups.GetOrAdd(el.Dispatcher, CreateGroup);
        group.Adapters[adapter.Id] = adapter;
        group.Tracker.Add(adapter.Id);
        _client.RegisterAdapter(adapter);
    }

    private static void OnUnloaded(FrameworkElement el, WpfElementAdapter adapter)
    {
        if (_groups.TryGetValue(el.Dispatcher, out var group))
        {
            group.Adapters.TryRemove(adapter.Id, out _);
            group.Tracker.Remove(adapter.Id);
        }
        _client.UnregisterAdapter(adapter.Id);
    }

    // Runs on the owning UI thread (the GetOrAdd factory is invoked synchronously by
    // OnLoaded, which fires on that thread), so the parameterless-priority DispatcherTimer
    // binds to the correct dispatcher.
    private static ThreadGroup CreateGroup(Dispatcher dispatcher)
    {
        ThreadGroup group = null!;
        group = new ThreadGroup(onEmpty: () =>
        {
            if (_groups.TryGetValue(dispatcher, out var g) && g.Tracker.IsEmpty)
            {
                g.Timer.Stop();
                _groups.TryRemove(dispatcher, out _);
            }
        });
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        timer.Tick += (_, _) =>
        {
            foreach (var adapter in group.Adapters.Values)
                _client.OnAdapterLayoutChanged(adapter.Id);
        };
        timer.Start();
        group.Timer = timer;
        return group;
    }
```

Also update the class doc comment to match the WinUI head's ("Each UI thread gets its own rect-refresh timer...").

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj
```

(C1's leak test still passes: unloaded elements are only referenced by the group while loaded, and removed on Unloaded — the timer holds no reference to never-loaded elements.)

Manual verification (required):
1. Run EyeCtrl + `samples/EyeCtrl.Sdk.Wpf.Sample`; drag the sample window; within ~200ms dwelling at the NEW button position activates it and the old position does nothing.
2. Kill EyeCtrl, restart it, wait for the client's reconnect backoff (≤30s); dwell targets work again without touching the sample window.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.Sdk.Wpf/Dwell.cs
git commit -m "fix(sdk-wpf): per-dispatcher rect-refresh timer so dwell targets track window moves and reconnects"
```

---

### Task C4: DwellClient — enqueue under tracker lock (+ optional Start/dispose hardening)

**Severity note:** `Start()` already has a single-flight guard (`if (_cts != null) return;`, line 62) and is documented "safe to call once per process," so this is not a "no guard → duplicate loops" bug. Parts (a) and (b) below are *defensive hardening* only (they matter solely if `Start()` is ever called concurrently, which the current app never does); part (c) is the one substantive — if low-probability — correctness fix. Implement (c); treat (a)/(b) as optional and skip if reducing churn.

Verified in `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs`: (a) `Start()` lines 60-66 guard is check-then-act, not atomic — two *concurrent* callers could each spawn loops (benign under once-per-process usage); (b) `Dispose()` line 159 `_cts?.Dispose()` runs after `Cancel()` (line 156), so the `Task.Delay(backoff, token)` at line 99 has already been signalled and throws `OperationCanceledException` (caught) — disposing after cancel is effectively benign, hardening at most; (c) **[real fix]** `UnregisterAdapter` (48-51) and `SendRect` (123-129) decide the message under `lock (_tracker)` but call `TrySend` outside it, so a racing register/update for the same id can be enqueued between decision and enqueue, reordering the wire stream.

**Files:**
- Modify: `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs:45-66,123-132,151-160`
- Test: `tests/EyeCtrl.Sdk.Abstractions.Tests/DwellClientTests.cs` (new file)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Sdk.Abstractions.Tests/DwellClientTests.cs`:

```csharp
namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Sdk.Abstractions;

public class DwellClientTests
{
    [Fact]
    public async Task Start_CalledConcurrently_ThenDispose_DoesNotThrow()
    {
        var client = new DwellClient();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => { await gate.Task; client.Start(); }))
            .ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);
        client.Dispose();
    }

    [Fact]
    public void Dispose_WhileConnectLoopInBackoffDelay_DoesNotThrow()
    {
        var client = new DwellClient();
        client.Start();
        // No pipe server exists, so the connect loop fails fast and sits in Task.Delay backoff.
        Thread.Sleep(100);
        client.Dispose();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj --filter "FullyQualifiedName~DwellClientTests"
```

Expected: these are race-condition guards — they may pass intermittently on current code (the CTS-dispose fault surfaces on an unobserved background task, and the Start race needs an exact interleaving). Treat them as regression guards; the authoritative verification of (a)-(c) is the code change itself. Proceed regardless of Step 2 outcome, but run the suite to confirm the tests at least execute.

- [ ] **Step 3: Implement**

(a) Replace `Start()` (lines 59-66):

```csharp
    /// <summary>Starts the background connect/read and send loops. Idempotent; safe to call once per process.</summary>
    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        _ = SendLoopAsync(_cts.Token);
    }
```

with:

```csharp
    /// <summary>Starts the background connect/read and send loops. Idempotent and thread-safe.</summary>
    public void Start()
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) != null)
        {
            cts.Dispose(); // lost the race — another caller already started the loops
            return;
        }
        _ = RunAsync(cts.Token);
        _ = SendLoopAsync(cts.Token);
    }
```

(b) In `Dispose()` (lines 152-160), delete the `_cts?.Dispose();` line and document why:

```csharp
    /// <summary>Stops the background loops, drops the queue, and disconnects from the service.</summary>
    public void Dispose()
    {
        _disposed = true;
        _outbox.Writer.TryComplete();
        _cts?.Cancel();
        lock (_writeLock) { _writer?.Dispose(); _writer = null; }
        _pipe?.Dispose();
        // Deliberately NOT disposing _cts: the background loops may still be inside
        // Task.Delay(backoff, token) with registrations on this CTS, and disposing it
        // concurrently can throw ObjectDisposedException inside those awaits. The client
        // is process-lifetime (one per process); one cancelled, undisposed CTS is the
        // safe trade-off.
    }
```

(c) Replace `UnregisterAdapter` (lines 44-51):

```csharp
    /// <summary>Removes the adapter with this id and tells the service it is no longer dwellable.</summary>
    public void UnregisterAdapter(string id)
    {
        _adapters.TryRemove(id, out _);
        WireMessage msg;
        lock (_tracker) msg = _tracker.OnUnregister(id);
        TrySend(msg);
    }
```

with:

```csharp
    /// <summary>Removes the adapter with this id and tells the service it is no longer dwellable.</summary>
    public void UnregisterAdapter(string id)
    {
        _adapters.TryRemove(id, out _);
        // Enqueue while still holding the tracker lock so outbox order matches the tracker's
        // decision order (a racing rect for the same id cannot slip in between). TryWrite on
        // an unbounded channel is non-blocking, so holding the lock across it is safe.
        lock (_tracker)
        {
            var msg = _tracker.OnUnregister(id);
            TrySend(msg);
        }
    }
```

and `SendRect` (lines 123-129):

```csharp
    private void SendRect(IDwellElementAdapter adapter)
    {
        if (!adapter.TryGetScreenRect(out var rect)) return;
        WireMessage? msg;
        lock (_tracker) msg = _tracker.OnRect(adapter.Id, rect, adapter.WindowHandle);
        if (msg != null) TrySend(msg);
    }
```

with:

```csharp
    private void SendRect(IDwellElementAdapter adapter)
    {
        if (!adapter.TryGetScreenRect(out var rect)) return;
        // See UnregisterAdapter: decide AND enqueue under the tracker lock to keep wire order.
        lock (_tracker)
        {
            var msg = _tracker.OnRect(adapter.Id, rect, adapter.WindowHandle);
            if (msg != null) TrySend(msg);
        }
    }
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.Sdk.Abstractions/DwellClient.cs tests/EyeCtrl.Sdk.Abstractions.Tests/DwellClientTests.cs
git commit -m "fix(sdk): DwellClient thread-safety — CAS Start, keep CTS undisposed, enqueue under tracker lock"
```

---

### Task C5: EyeCare — disabled gap must not count as active time (scheduler + service)

Verified: `EyeCareScheduler.Update` (`src/EyeCtrl.EyeCare/EyeCareScheduler.cs:32`) returns on `!_settings.Enabled` BEFORE `ComputeStepMicroseconds` advances `_lastTimestampUs` (line 38), and `EyeCareService.OnGazeSample` (`src/EyeCtrl.EyeCare/EyeCareService.cs:47`) has the same early return, so the scheduler never even sees samples while disabled. Result: disable for 25 minutes → re-enable → the first sample's step is the whole gap → instant break prompt. Fix both layers: the service always forwards to the (enable-aware) scheduler, and the scheduler advances the baseline before its Enabled check.

**Files:**
- Modify: `src/EyeCtrl.EyeCare/EyeCareScheduler.cs:30-38`
- Modify: `src/EyeCtrl.EyeCare/EyeCareService.cs:45-49`
- Test: `tests/EyeCtrl.EyeCare.Tests/EyeCareSchedulerTests.cs`, `tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `EyeCareSchedulerTests` (note: a fresh `EyeCareSettings` instance, NOT the shared static `Cfg`, because the test mutates `Enabled`):

```csharp
    [Fact]
    public void DisabledGap_DoesNotCountAsActiveTime_OnReEnable()
    {
        var cfg = new EyeCareSettings
        {
            Enabled = true, IntervalMinutes = 1, NaturalBreakSeconds = 30, SnoozeMinutes = 1
        };
        var scheduler = new EyeCareScheduler(cfg);
        bool fired = false;
        scheduler.BreakDue += () => fired = true;

        // 30s active gaze while enabled — under the 60s interval.
        for (long t = 0; t <= 30_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        // Disabled for ~25 minutes; gaze samples keep flowing (the tracker never stops).
        cfg.Enabled = false;
        for (long t = 31_000_000; t <= 1_530_000_000; t += 60_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        // Re-enable: the next sample must NOT be charged with the disabled gap.
        cfg.Enabled = true;
        scheduler.Update(new GazePoint(500, 500, 1_531_000_000, IsValid: true), isPaused: false);

        Check.That(fired).IsFalse();
    }
```

Append to `EyeCareServiceTests` — first extend `FakeHooks` (lines 11-19) with a banner counter. Current line 14:

```csharp
        public Task<bool> ShowBannerAsync(string message) => Task.FromResult(false);
```

New:

```csharp
        public int BannerCalls;
        public Task<bool> ShowBannerAsync(string message) { BannerCalls++; return Task.FromResult(false); }
```

Then the test (exercises the service-level early return):

```csharp
    [Fact]
    public void DisabledGap_DoesNotTriggerBreak_OnReEnable()
    {
        var settings = new EyeCareSettings
        {
            Enabled = true, IntervalMinutes = 1, NaturalBreakSeconds = 30, SnoozeMinutes = 1
        };
        var hooks = new FakeHooks();
        var svc = NewService(settings, hooks);

        for (long t = 0; t <= 30_000_000; t += 1_000_000)
            svc.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        settings.Enabled = false;
        for (long t = 31_000_000; t <= 1_530_000_000; t += 60_000_000)
            svc.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        settings.Enabled = true;
        svc.OnGazeSample(new GazePoint(500, 500, 1_531_000_000, IsValid: true), isPaused: false);

        Check.That(hooks.BannerCalls).IsEqualTo(0);
    }
```

(`ShowBannerAsync` completes synchronously, so the `async void OnBreakDue` runs to `Snooze()` before `OnGazeSample` returns — the assertion is deterministic.)

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj --filter "FullyQualifiedName~DisabledGap"
```

Expected: both FAIL — scheduler test: `fired` is true (the 25min step lands on `_activeMicroseconds` ≥ 60s interval); service test: `BannerCalls` is 1.

- [ ] **Step 3: Implement**

`EyeCareScheduler.Update` — current lines 30-38:

```csharp
    public void Update(GazePoint sample, bool isPaused, bool canInterrupt = true)
    {
        if (!_settings.Enabled) return;

        // Advance the inter-sample baseline on EVERY call (valid, invalid, paused).
        // Computing the step here — before any early return — means an away/paused gap
        // moves the baseline forward instead of being added to the active total on the
        // first sample after the gap (which would over-count away/paused time).
        var step = ComputeStepMicroseconds(sample.TimestampMicroseconds);
```

New:

```csharp
    public void Update(GazePoint sample, bool isPaused, bool canInterrupt = true)
    {
        // Advance the inter-sample baseline on EVERY call (valid, invalid, paused — and
        // while DISABLED). Computing the step here — before any early return, including the
        // Enabled check — means a disabled/away/paused gap moves the baseline forward
        // instead of being added to the active total on the first sample after the gap
        // (which would instantly prompt a break right after re-enabling eye care).
        var step = ComputeStepMicroseconds(sample.TimestampMicroseconds);

        if (!_settings.Enabled) return;
```

`EyeCareService.OnGazeSample` — current lines 45-49:

```csharp
    public void OnGazeSample(GazePoint sample, bool isPaused, bool canInterrupt = true)
    {
        if (!_settings.Enabled) return;
        _fatigueMonitor.OnSample(sample);
        _scheduler.Update(sample, isPaused, canInterrupt);
```

New:

```csharp
    public void OnGazeSample(GazePoint sample, bool isPaused, bool canInterrupt = true)
    {
        // The scheduler is enable-aware and must see EVERY sample so its inter-sample
        // baseline stays fresh while eye care is disabled — otherwise the whole disabled
        // stretch would be counted as active time on re-enable.
        _scheduler.Update(sample, isPaused, canInterrupt);
        if (!_settings.Enabled) return;
        _fatigueMonitor.OnSample(sample);
```

(The escalation block below stays as-is; it now runs only when enabled, same as before.)

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj
```

All scheduler/service tests must pass, including the pre-existing `Disabled_NeverFires` and gap-leak tests.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.EyeCare/EyeCareScheduler.cs src/EyeCtrl.EyeCare/EyeCareService.cs tests/EyeCtrl.EyeCare.Tests/EyeCareSchedulerTests.cs tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs
git commit -m "fix(eyecare): don't count the disabled gap as active time on re-enable"
```

---

### Task C6: FatigueMonitor — blinks must not zero the score; single-pass scoring without allocation

Verified at `src/EyeCtrl.EyeCare/FatigueMonitor.cs:29-33`: one invalid sample (a blink) resets `Score` to 0, causing flicker against `FatigueThreshold`. Nothing in the class docs or existing tests requires that reset (tests only cover sustained staring, normal movement, and disabled) — correct behavior is to ignore invalid samples and let the window age out. Also verified lines 45-55: `ToArray()` plus 7 LINQ passes per sample at 30-60Hz. Note on the audit's "O(1)" suggestion: the fixation ratio is defined relative to the *current* window centroid, so an exact incremental version is impossible (every centroid shift re-classifies old samples); the honest fix is running sums for the centroid (O(1)) plus ONE allocation-free pass over the queue for fixation count and bounding box (was: 1 array copy + 7 passes).

**Files:**
- Modify: `src/EyeCtrl.EyeCare/FatigueMonitor.cs:13-69`
- Test: `tests/EyeCtrl.EyeCare.Tests/FatigueMonitorTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `FatigueMonitorTests`:

```csharp
    [Fact]
    public void SingleInvalidSample_DoesNotResetScore()
    {
        var monitor = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);

        for (int i = 0; i < 100; i++)
            monitor.OnSample(new GazePoint(500, 500, i * 50_000L, IsValid: true));
        var before = monitor.Score;
        Check.That(before).IsStrictlyGreaterThan(0.0);

        // One blink frame must not flicker the score back to zero.
        monitor.OnSample(new GazePoint(0, 0, 100 * 50_000L, IsValid: false));

        Check.That(monitor.Score).IsEqualTo(before);
    }

    // Characterization guard for the running-sum refactor: after evicting old samples the
    // score must equal a fresh monitor fed only the still-in-window samples.
    [Fact]
    public void ScoreAfterEviction_MatchesFreshRecompute()
    {
        var a = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);
        var rnd = new Random(7);
        var pts = Enumerable.Range(0, 300)
            .Select(i => (x: 200.0 + rnd.Next(1200), y: 200.0 + rnd.Next(600), ts: i * 50_000L))
            .ToList();
        foreach (var p in pts)
            a.OnSample(new GazePoint(p.x, p.y, p.ts, IsValid: true));

        var cutoff = pts[^1].ts - 5_000_000L;
        var b = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);
        foreach (var p in pts.Where(p => p.ts >= cutoff))
            b.OnSample(new GazePoint(p.x, p.y, p.ts, IsValid: true));

        Check.That(a.Score).IsCloseTo(b.Score, 1e-9);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj --filter "FullyQualifiedName~FatigueMonitorTests"
```

Expected: `SingleInvalidSample_DoesNotResetScore` FAILS (Score is 0.0 after the invalid sample). `ScoreAfterEviction_MatchesFreshRecompute` passes on current code (it is the refactor's regression guard).

- [ ] **Step 3: Implement**

Replace `OnSample` and `Reset` (lines 26-69) and add the sum fields next to `_samples` (line 13). New fields:

```csharp
    private readonly Queue<(long ts, double x, double y)> _samples = new();
    private double _sumX;
    private double _sumY;
```

New `OnSample`:

```csharp
    /// <summary>Feeds one raw gaze sample. Invalid samples (blinks/looking away) are ignored —
    /// the score keeps its last value and stale samples age out of the window naturally.</summary>
    public void OnSample(GazePoint sample)
    {
        if (!_settings.FatigueMonitorEnabled)
        {
            Score = 0.0;
            return;
        }
        if (!sample.IsValid) return;

        _samples.Enqueue((sample.TimestampMicroseconds, sample.X, sample.Y));
        _sumX += sample.X;
        _sumY += sample.Y;

        // Evict samples outside the window, keeping the running sums in step.
        var cutoff = sample.TimestampMicroseconds - _windowMicroseconds;
        while (_samples.Count > 0 && _samples.Peek().ts < cutoff)
        {
            var old = _samples.Dequeue();
            _sumX -= old.x;
            _sumY -= old.y;
        }

        if (_samples.Count < 2) { Score = 0.0; return; }

        // ── Signal 1: sustained-fixation ratio ──
        // % of samples within _fixationRadiusPixels of the window centroid. The centroid
        // comes from O(1) running sums; classifying samples against it inherently needs one
        // pass (each new centroid re-classifies old samples), but it is allocation-free —
        // previously this did a ToArray() plus 7 LINQ passes per sample.
        var count = _samples.Count;
        var cx = _sumX / count;
        var cy = _sumY / count;
        var rSq = _fixationRadiusPixels * _fixationRadiusPixels;
        var inFixation = 0;
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var (_, x, y) in _samples)
        {
            var dx = x - cx;
            var dy = y - cy;
            if (dx * dx + dy * dy <= rSq) inFixation++;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        var fixationRatio = (double)inFixation / count;

        // ── Signal 2: saccade-range shrink ──
        // Bounding-box area normalized; small box = eyes locked in.
        var boxArea = (maxX - minX) * (maxY - minY);
        // Normalize: assume a "healthy" box of ~1,000,000 sq px (1000x1000); shrink → higher score.
        var shrinkScore = 1.0 - Math.Clamp(boxArea / 1_000_000.0, 0.0, 1.0);

        // Weighted blend (fixation ratio weighted higher — more reliable signal).
        Score = Math.Clamp(0.7 * fixationRatio + 0.3 * shrinkScore, 0.0, 1.0);
    }
```

New `Reset`:

```csharp
    /// <summary>Resets the monitor (call on break completion or settings toggle).</summary>
    public void Reset()
    {
        _samples.Clear();
        _sumX = 0;
        _sumY = 0;
        Score = 0.0;
    }
```

(Float drift on the running sums is bounded: samples are screen-pixel magnitudes over a ≤60s window; sums are fully rebuilt whenever `Reset()` runs — after every completed break.)

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj
```

All four pre-existing FatigueMonitor expectations plus the two new tests must pass (in particular `Disabled_NeverRaisesScore` still expects Score 0 when the feature is off — that branch is unchanged).

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.EyeCare/FatigueMonitor.cs tests/EyeCtrl.EyeCare.Tests/FatigueMonitorTests.cs
git commit -m "fix(eyecare): ignore blink samples in FatigueMonitor and score in a single allocation-free pass"
```

---

### Task C7: EyeCareService — implement IDisposable with unsubscription

Verified: the ctor subscribes `_scheduler.BreakDue += OnBreakDue` and `_settings.PropertyChanged += OnSettingsChanged` (`src/EyeCtrl.EyeCare/EyeCareService.cs:39-41`) and the class is not `IDisposable`. `EyeCareSettings` is app-lifetime, so every throwaway `EyeCareService` (tests construct several) stays reachable via the settings' event list. The DI container (`AppBootstrapper` registers it as a singleton) disposes `IDisposable` singletons automatically, so no App-side change is needed.

**Files:**
- Modify: `src/EyeCtrl.EyeCare/EyeCareService.cs:12,69-74` (class declaration + new Dispose)
- Test: `tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `EyeCareServiceTests`:

```csharp
    [Fact]
    public void Dispose_UnsubscribesFromSettings()
    {
        var settings = new EyeCareSettings { Enabled = true };
        var hooks = new FakeHooks();
        var svc = NewService(settings, hooks);

        svc.Dispose();
        settings.Enabled = false; // would call hooks.CancelActive() if still subscribed

        Check.That(hooks.CancelActiveCalls).IsEqualTo(0);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj --filter "FullyQualifiedName~Dispose_UnsubscribesFromSettings"
```

Expected: compile error CS1061 "'EyeCareService' does not contain a definition for 'Dispose'".

- [ ] **Step 3: Implement**

Change the class declaration (line 12):

```csharp
public sealed class EyeCareService : IEyeCareService
```

to:

```csharp
public sealed class EyeCareService : IEyeCareService, IDisposable
```

Add after `Cancel()` (after line 74):

```csharp
    /// <summary>Unsubscribes from the shared settings object and the owned scheduler. The
    /// settings instance is app-lifetime; without this, every constructed service instance
    /// stays reachable through its PropertyChanged list. Safe to call more than once.</summary>
    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _scheduler.BreakDue -= OnBreakDue;
    }
```

(`IEyeCareService` stays unchanged — it is slated to move to Core per its own doc comment, and the DI container disposes by concrete type.)

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.EyeCare/EyeCareService.cs tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs
git commit -m "fix(eyecare): EyeCareService unsubscribes from settings/scheduler via IDisposable"
```

---

### Task C8: SmallLMPredictor — ownsTokenizer flag

Verified: `Dispose` (`src/EyeCtrl.LM/SmallLMPredictor.cs:338,354`) disposes the injected `LMTokenizer`. Nuance the audit missed: the only production caller (`src/EyeCtrl.App/Services/EventCoordinator.cs:996-1018`) creates the tokenizer, hands it to the predictor, and keeps NO reference — the predictor's dispose is today the only thing preventing a native-handle leak. So the fix is an explicit `ownsTokenizer` ctor parameter **defaulting to `true`** (preserving the de-facto ownership transfer, no EventCoordinator change needed); callers who retain their tokenizer pass `false`.

**Files:**
- Modify: `src/EyeCtrl.LM/SmallLMPredictor.cs:20,44-50,338,354`
- Create: `tests/EyeCtrl.LM.Tests/TestTokenizerJson.cs` (shared fixture helper)
- Test: `tests/EyeCtrl.LM.Tests/SmallLMPredictorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.LM.Tests/TestTokenizerJson.cs` — extract the minimal char-level BPE fixture that `LMTokenizerTests.WriteValidTokenizerJson` (lines 98-144) already proves loads under Tokenizers.DotNet 1.4.1, as an internal static helper:

```csharp
namespace EyeCtrl.LM.Tests;

using System.Text.Json;

/// <summary>Writes the minimal char-level BPE tokenizer.json used across LM tests.</summary>
internal static class TestTokenizerJson
{
    public static void Write(string path)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = "1.0",
            truncation = (object?)null,
            padding = (object?)null,
            added_tokens = new[]
            {
                new { id = 0, special = true, content = "<s>", single_word = false, lstrip = false, rstrip = false, normalized = true },
                new { id = 1, special = true, content = "</s>", single_word = false, lstrip = false, rstrip = false, normalized = true },
                new { id = 2, special = true, content = "<unk>", single_word = false, lstrip = false, rstrip = false, normalized = true }
            },
            normalizer = (object?)null,
            pre_tokenizer = new { type = "Whitespace" },
            post_processor = (object?)null,
            decoder = (object?)null,
            model = new
            {
                dropout = (object?)null,
                unk_token = "<unk>",
                continuing_subword_prefix = "",
                end_of_word_suffix = "",
                fuse_unk = false,
                vocab = new Dictionary<string, int>
                {
                    ["<s>"] = 0, ["</s>"] = 1, ["<unk>"] = 2,
                    ["h"] = 3, ["e"] = 4, ["l"] = 5, ["o"] = 6,
                    ["w"] = 7, ["r"] = 8, ["d"] = 9, [" "] = 10
                },
                merges = Array.Empty<string>()
            }
        });
        File.WriteAllText(path, json);
    }
}
```

(Optionally have `LMTokenizerTests.WriteValidTokenizerJson` delegate to it — one-line body — to avoid the duplicate; keep both signatures so no existing test changes.)

Append to `SmallLMPredictorTests`:

```csharp
    [Fact]
    public void Dispose_WithOwnsTokenizerFalse_LeavesInjectedTokenizerAlive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tokenizerPath = Path.Combine(tempDir, "tokenizer.json");
            TestTokenizerJson.Write(tokenizerPath);
            using var tokenizer = new LMTokenizer(tokenizerPath);

            // Model file absent → predictor unavailable, but Dispose still runs its cleanup path.
            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false,
                NullLogger<SmallLMPredictor>.Instance, tokenizer: tokenizer, modelPath: null,
                ownsTokenizer: false);
            predictor.Dispose();

            Check.ThatCode(() => tokenizer.Encode("hello")).DoesNotThrow();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Dispose_DefaultOwnership_DisposesInjectedTokenizer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tokenizerPath = Path.Combine(tempDir, "tokenizer.json");
            TestTokenizerJson.Write(tokenizerPath);
            var tokenizer = new LMTokenizer(tokenizerPath);

            // Default is ownership transfer — EventCoordinator relies on this to avoid a leak.
            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false,
                NullLogger<SmallLMPredictor>.Instance, tokenizer: tokenizer);
            predictor.Dispose();

            Check.ThatCode(() => tokenizer.Encode("hello")).Throws<ObjectDisposedException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj --filter "FullyQualifiedName~SmallLMPredictorTests"
```

Expected: compile error CS1739 — no `ownsTokenizer` parameter exists (`Dispose_DefaultOwnership...` alone would pass today; the `ownsTokenizer: false` call site is the failing part).

- [ ] **Step 3: Implement**

Add field after line 20 (`private readonly LMTokenizer? _tokenizer;`):

```csharp
    private readonly bool _ownsTokenizer;
```

Extend the ctor (line 44). Current signature:

```csharp
    public SmallLMPredictor(string modelsDirectory, string? modelSize, bool preferGpu, ILogger<SmallLMPredictor>? logger = null, LMTokenizer? tokenizer = null, string? modelPath = null)
```

New (add the param + doc comment line `/// <param name="ownsTokenizer">Whether the predictor takes ownership of <paramref name="tokenizer"/> and disposes it. Defaults to true (ownership transfer), which the app relies on; pass false if the caller keeps using the tokenizer.</param>`):

```csharp
    public SmallLMPredictor(string modelsDirectory, string? modelSize, bool preferGpu, ILogger<SmallLMPredictor>? logger = null, LMTokenizer? tokenizer = null, string? modelPath = null, bool ownsTokenizer = true)
```

and in the ctor body, after `_tokenizer = tokenizer;`:

```csharp
        _ownsTokenizer = tokenizer != null && ownsTokenizer;
```

In `Dispose()`, replace both occurrences of:

```csharp
            _tokenizer?.Dispose();
```

with:

```csharp
            if (_ownsTokenizer) _tokenizer?.Dispose();
```

(lines 338 and 354; C10 restructures this method — if C10 lands first, apply the same guard there.)

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.LM/SmallLMPredictor.cs tests/EyeCtrl.LM.Tests
git commit -m "fix(lm): explicit tokenizer ownership on SmallLMPredictor (ownsTokenizer, default transfer)"
```

---

### Task C9: SmallLMPredictor.PostProcess — emit only the first generated word

Verified at `src/EyeCtrl.LM/SmallLMPredictor.cs:304-319`: every whitespace-separated word of the 8-token generation becomes an independent suggestion, all sharing the same `avgProb` — so one LM call floods the suggestion bar with up to ~8 same-scored entries, and only the first is actually the model's next-word prediction. Consumer check (`src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs:82-95`): it merges by `Word` and treats each entry as one independent word suggestion — emitting a single first-word entry keeps that contract exactly. (Full-phrase entries would be typed wholesale by the keyboard on selection; that is a product decision — deferred, see end of file.)

**Files:**
- Modify: `src/EyeCtrl.LM/SmallLMPredictor.cs:304-319` (`PostProcess` becomes `internal static` for testability — `EyeCtrl.LM.csproj` already has `InternalsVisibleTo Include="EyeCtrl.LM.Tests"`)
- Test: `tests/EyeCtrl.LM.Tests/SmallLMPredictorTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `SmallLMPredictorTests`:

```csharp
    [Fact]
    public void PostProcess_MultiWordGeneration_EmitsOnlyFirstWord()
    {
        var results = SmallLMPredictor.PostProcess("world peace now", prefix: null, avgProb: 0.5);

        Check.That(results).HasSize(1);
        Check.That(results[0].Word).IsEqualTo("world");
    }

    [Fact]
    public void PostProcess_FirstWordNotMatchingPrefix_EmitsNothing()
    {
        // Later words matching the prefix must NOT be promoted — only the first word is the
        // model's next-word prediction.
        var results = SmallLMPredictor.PostProcess("banana wonder world", prefix: "wo", avgProb: 0.5);

        Check.That(results).IsEmpty();
    }

    [Fact]
    public void PostProcess_WhitespaceOnly_EmitsNothing()
    {
        Check.That(SmallLMPredictor.PostProcess("   ", prefix: null, avgProb: 0.5)).IsEmpty();
    }
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj --filter "FullyQualifiedName~PostProcess"
```

Expected: compile error CS0122/CS0120 (`PostProcess` is private instance). After making it visible mechanically, `..._EmitsOnlyFirstWord` fails with 3 results and `..._EmitsNothing` fails with 2 results.

- [ ] **Step 3: Implement**

Replace (lines 304-319):

```csharp
    private List<PredictionResult> PostProcess(string generatedText, string? prefix, double avgProb)
    {
        var results = new List<PredictionResult>();
        if (string.IsNullOrWhiteSpace(generatedText)) return results;

        var words = generatedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (!string.IsNullOrEmpty(prefix) && !word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(new PredictionResult(word, avgProb * 100.0, PredictionType.LMSuggestion, avgProb));
        }

        return results;
    }
```

with:

```csharp
    /// <summary>Turns the raw generation into suggestions. Only the FIRST generated word is
    /// emitted: it is the model's actual next-word prediction, and avgProb was measured over
    /// the whole sequence — later words are neither independent suggestions nor correctly
    /// scored by it. Internal (not private) for unit testing via InternalsVisibleTo.</summary>
    internal static List<PredictionResult> PostProcess(string generatedText, string? prefix, double avgProb)
    {
        var results = new List<PredictionResult>();
        if (string.IsNullOrWhiteSpace(generatedText)) return results;

        var words = generatedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return results;

        var first = words[0];
        if (!string.IsNullOrEmpty(prefix) && !first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return results;

        results.Add(new PredictionResult(first, avgProb * 100.0, PredictionType.LMSuggestion, avgProb));
        return results;
    }
```

(The only call site, `RunInference` line 249 `return PostProcess(generatedText, prefix, avgProb);`, compiles unchanged against the static method.)

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~PredictionOrchestrator"
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.LM/SmallLMPredictor.cs tests/EyeCtrl.LM.Tests/SmallLMPredictorTests.cs
git commit -m "fix(lm): PostProcess emits only the first generated word, not the whole sequence"
```

---

### Task C10: SmallLMPredictor — capture results bag in worker, Monitor-based dispose wait, KV-cache TODO

Verified: (c) the worker (`src/EyeCtrl.LM/SmallLMPredictor.cs:150-177`) re-reads the `_pendingResults` FIELD after its token check — a superseding `Predict()` swaps in a new bag between the check and the read, so a stale worker can dump old-context results into the new request's bag; fix by capturing the bag in the closure under the same lock that created it. (d) `Dispose` (328-356) busy-polls `_inferenceCount` with `Thread.Sleep(10)` and, on timeout, spawns a second polling thread; replace with `Monitor.Wait/Pulse` (chosen over the audit's CountdownEvent/TCS suggestion because the in-flight count is dynamic — a Monitor loop over the counter has no reset/set ordering races). (e) add the KV-cache TODO on `RunInference`.

**Files:**
- Modify: `src/EyeCtrl.LM/SmallLMPredictor.cs:137-181,196,322-357`
- Test: `tests/EyeCtrl.LM.Tests/SmallLMPredictorTests.cs`

- [ ] **Step 1: Write the failing test**

The bag race and the dispose handoff need a live ONNX session to exercise for real (no model file ships with the repo — existing LM tests all run model-less), so automated coverage is a characterization guard plus review:

```csharp
    [Fact]
    public void Dispose_WithNoInflightInference_CompletesImmediately()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false,
                NullLogger<SmallLMPredictor>.Instance, tokenizer: null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            predictor.Dispose();
            sw.Stop();
            // Must not sit in any wait/poll loop when nothing is in flight.
            Check.That(sw.ElapsedMilliseconds).IsStrictlyLessThan(1000);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj --filter "FullyQualifiedName~Dispose_WithNoInflightInference"
```

Expected: PASSES on current code (the poll loop exits immediately at count 0) — this is a regression guard for the rewrite, not a bug repro. The (c)/(d) fixes are verified by review; existing `Dispose_CalledTwice_DoesNotThrow` and the C8 ownership tests must stay green.

- [ ] **Step 3: Implement**

Add a gate field next to `_lock` (line 23):

```csharp
    private readonly object _inferenceGate = new();
```

(c) In `Predict` (lines 137-177), capture the bag. Replace:

```csharp
            var token = _pendingCts.Token;
            var session = _session;
            var capturePrefix = prefix;
            var captureContext = context;
```

with:

```csharp
            var token = _pendingCts.Token;
            var session = _session;
            var capturePrefix = prefix;
            var captureContext = context;
            // Capture THIS request's bag. Re-reading the field inside the worker races a
            // superseding Predict(), which swaps in a fresh bag — a stale worker could then
            // pour old-context results into the new request.
            var bag = _pendingResults;
```

and replace the worker's result loop:

```csharp
                    foreach (var result in results)
                    {
                        if (token.IsCancellationRequested) break;
                        var bag = _pendingResults;
                        if (bag == null) break;
                        bag.Add(result);
                    }
```

with:

```csharp
                    foreach (var result in results)
                    {
                        if (token.IsCancellationRequested) break;
                        bag.Add(result);
                    }
```

and change the worker's `finally` from:

```csharp
                finally
                {
                    Interlocked.Decrement(ref _inferenceCount);
                }
```

to:

```csharp
                finally
                {
                    Interlocked.Decrement(ref _inferenceCount);
                    lock (_inferenceGate) Monitor.PulseAll(_inferenceGate);
                }
```

(d) Replace `Dispose` (lines 321-357):

```csharp
    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();

        var start = DateTime.UtcNow;
        while (_inferenceCount > 0 && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(10);
        }

        if (_inferenceCount == 0)
        {
            _session?.Dispose();
            _session = null;
            if (_ownsTokenizer) _tokenizer?.Dispose();
            return;
        }

        _logger?.LogWarning("SmallLMPredictor dispose timed out while inference was still running; deferring session disposal");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (Interlocked.CompareExchange(ref _inferenceCount, 0, 0) > 0)
            {
                Thread.Sleep(10);
            }

            lock (_lock)
            {
                _session?.Dispose();
                _session = null;
                if (_ownsTokenizer) _tokenizer?.Dispose();
            }
        });
    }
```

(shown post-C8; if C8 has not landed yet the two guarded lines read `_tokenizer?.Dispose();`) with:

```csharp
    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();

        // Wait (not poll) for in-flight inference: workers pulse _inferenceGate after each
        // decrement. Monitor over the live counter avoids the set/reset ordering races an
        // event-per-batch (CountdownEvent/TCS) would have with a dynamic in-flight count.
        lock (_inferenceGate)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (_inferenceCount > 0)
            {
                var remainingMs = deadline - Environment.TickCount64;
                if (remainingMs <= 0 || !Monitor.Wait(_inferenceGate, (int)remainingMs)) break;
            }
        }

        if (_inferenceCount == 0)
        {
            _session?.Dispose();
            _session = null;
            if (_ownsTokenizer) _tokenizer?.Dispose();
            return;
        }

        _logger?.LogWarning("SmallLMPredictor dispose timed out while inference was still running; deferring session disposal");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_inferenceGate)
            {
                while (_inferenceCount > 0) Monitor.Wait(_inferenceGate);
            }

            lock (_lock)
            {
                _session?.Dispose();
                _session = null;
                if (_ownsTokenizer) _tokenizer?.Dispose();
            }
        });
    }
```

(e) Add above `RunInference` (line 181):

```csharp
    // TODO(perf): no KV cache — every generated token re-runs the model over the FULL
    // sequence (input_ids grows each step), making the maxNewTokens loop O(n^2) in sequence
    // length. If the exported ONNX model gains past_key_values inputs/outputs, feed only the
    // newest token per step and carry the cache between steps.
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.LM/SmallLMPredictor.cs tests/EyeCtrl.LM.Tests/SmallLMPredictorTests.cs
git commit -m "fix(lm): capture results bag per request and replace dispose busy-poll with Monitor wait"
```

---

### Task C11: Extract DwellHeadCore (client singleton start + id generation) to Sdk.Abstractions

After C1/C3 the WPF and WinUI heads still duplicate: the process-wide `DwellClient` field, the `Interlocked.Exchange` start latch, and `"wpf_"/"winui_"` id generation. Per the finding's scope guidance, ONLY these zero-framework-dependency pieces are extracted; the ThreadGroup/timer plumbing stays per-head (the timer types `DispatcherTimer` vs `DispatcherQueueTimer` are framework-specific and a generic abstraction over them is not worth it — noted as deferred).

**Files:**
- Create: `src/EyeCtrl.Sdk.Abstractions/DwellHeadCore.cs`
- Modify: `src/EyeCtrl.Sdk.Wpf/Dwell.cs` (post-C3 state)
- Modify: `src/EyeCtrl.Sdk.WinUI/Dwell.cs:42-44,76-81`
- Test: `tests/EyeCtrl.Sdk.Abstractions.Tests/DwellHeadCoreTests.cs` (new file)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Sdk.Abstractions.Tests/DwellHeadCoreTests.cs`:

```csharp
namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Sdk.Abstractions;

public class DwellHeadCoreTests
{
    [Fact]
    public void NextId_UsesPrefixAndIncrements()
    {
        var core = new DwellHeadCore("wpf_");
        Check.That(core.NextId()).IsEqualTo("wpf_1");
        Check.That(core.NextId()).IsEqualTo("wpf_2");
    }

    [Fact]
    public async Task NextId_IsUniqueUnderConcurrency()
    {
        var core = new DwellHeadCore("x_");
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => { for (var i = 0; i < 250; i++) ids.Add(core.NextId()); })));
        Check.That(ids.Distinct().Count()).IsEqualTo(2000);
    }

    [Fact]
    public void EnsureStarted_IsIdempotent()
    {
        var core = new DwellHeadCore("t_");
        core.EnsureStarted();
        core.EnsureStarted(); // second call must be a no-op, not a second Start
        core.Client.Dispose();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj --filter "FullyQualifiedName~DwellHeadCoreTests"
```

Expected: compile error CS0246 — `DwellHeadCore` does not exist.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.Sdk.Abstractions/DwellHeadCore.cs`:

```csharp
namespace EyeCtrl.Sdk.Abstractions;

/// <summary>
/// Framework-agnostic state shared by the WPF and WinUI attached-property heads: the
/// process-wide <see cref="DwellClient"/>, its lazy one-time start, and adapter id
/// generation. Each head owns exactly one static instance.
/// </summary>
public sealed class DwellHeadCore
{
    private readonly string _idPrefix;
    private int _started;
    private long _idCounter;

    /// <summary>The process-wide dwell client for this head.</summary>
    public DwellClient Client { get; } = new();

    /// <summary>Creates the core with the head's id prefix (e.g. "wpf_" or "winui_").</summary>
    public DwellHeadCore(string idPrefix) => _idPrefix = idPrefix;

    /// <summary>Starts the client exactly once, no matter how many callers race here.</summary>
    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) Client.Start();
    }

    /// <summary>Returns the next unique adapter id for this head (prefix + counter).</summary>
    public string NextId() => $"{_idPrefix}{Interlocked.Increment(ref _idCounter)}";
}
```

WPF head (`src/EyeCtrl.Sdk.Wpf/Dwell.cs`, post-C3): replace

```csharp
    private static readonly DwellClient _client = new();
    private static int _started;
    private static long _idCounter;
```

with

```csharp
    private static readonly DwellHeadCore _core = new("wpf_");
```

delete the `EnsureStarted()` method, replace every `_client` use with `_core.Client` (in `OnLoaded`, `OnUnloaded`, and the timer tick), and in `Register` replace

```csharp
        EnsureStarted();
        ...
        var id = $"wpf_{Interlocked.Increment(ref _idCounter)}";
```

with

```csharp
        _core.EnsureStarted();
        ...
        var id = _core.NextId();
```

WinUI head (`src/EyeCtrl.Sdk.WinUI/Dwell.cs`): replace lines 42-44

```csharp
    private static readonly DwellClient _client = new();
    private static int _started;
    private static long _idCounter;
```

with

```csharp
    private static readonly DwellHeadCore _core = new("winui_");
```

and in `Register` (lines 76-81) replace

```csharp
        if (Interlocked.Exchange(ref _started, 1) == 0) _client.Start();
        if (el.GetValue(EntryProperty) is Entry) return;

        var id = $"winui_{Interlocked.Increment(ref _idCounter)}";
```

with

```csharp
        _core.EnsureStarted();
        if (el.GetValue(EntryProperty) is Entry) return;

        var id = _core.NextId();
```

then replace the remaining `_client` uses (`OnLoaded`:107, `OnUnloaded`:117, timer tick:138) with `_core.Client`.

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj
dotnet test tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.Sdk.Abstractions/DwellHeadCore.cs src/EyeCtrl.Sdk.Wpf/Dwell.cs src/EyeCtrl.Sdk.WinUI/Dwell.cs tests/EyeCtrl.Sdk.Abstractions.Tests/DwellHeadCoreTests.cs
git commit -m "refactor(sdk): extract DwellHeadCore (client start latch + id generation) shared by both heads"
```

---

### Task C12: Dwell.Mode — read live at activation time

Verified: both heads snapshot `GetMode(el)` once at registration (`src/EyeCtrl.Sdk.Wpf/Dwell.cs` — `new WpfElementAdapter(id, el, GetMode(el))`; `src/EyeCtrl.Sdk.WinUI/Dwell.cs:82`), so changing `Dwell.Mode` after `Enabled=true` is silently ignored until Enabled is toggled. Live-read is cheap: `Mode` is consumed ONLY inside `Activate()`'s UI-thread-dispatched callback in both adapters (verified `DwellClient` never reads `IDwellElementAdapter.Mode` — mode is not on the wire), and `DependencyObject.GetValue` on the owning thread is a fast local read. So the adapters read the attached property live.

**Files:**
- Modify: `src/EyeCtrl.Sdk.Wpf/WpfElementAdapter.cs:10-34`
- Modify: `src/EyeCtrl.Sdk.WinUI/WinUiElementAdapter.cs:13-26`
- Modify: `src/EyeCtrl.Sdk.Wpf/Dwell.cs`, `src/EyeCtrl.Sdk.WinUI/Dwell.cs` (drop the `GetMode(el)` ctor argument)
- Modify: `src/EyeCtrl.Sdk.Abstractions/IDwellElementAdapter.cs:25-26` (doc)
- Modify: `src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj` (add InternalsVisibleTo for the test)
- Test: `tests/EyeCtrl.Sdk.Wpf.Tests/WpfElementAdapterTests.cs` (new file)

- [ ] **Step 1: Write the failing test**

Add to `src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj` (inside a new `ItemGroup`):

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="EyeCtrl.Sdk.Wpf.Tests" />
  </ItemGroup>
```

Create `tests/EyeCtrl.Sdk.Wpf.Tests/WpfElementAdapterTests.cs`:

```csharp
namespace EyeCtrl.Sdk.Wpf.Tests;

using System.Windows.Controls;
using EyeCtrl.Sdk.Abstractions;
using EyeCtrl.Sdk.Wpf;

public class WpfElementAdapterTests
{
    [StaFact]
    public void Mode_ReflectsAttachedPropertyChanges_AfterRegistration()
    {
        var btn = new Button();
        Dwell.SetMode(btn, DwellMode.Focus);
        var adapter = new WpfElementAdapter("t1", btn);

        Check.That(adapter.Mode).IsEqualTo(DwellMode.Focus);

        Dwell.SetMode(btn, DwellMode.Invoke);
        Check.That(adapter.Mode).IsEqualTo(DwellMode.Invoke);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj --filter "FullyQualifiedName~WpfElementAdapterTests"
```

Expected: compile error CS7036 — `WpfElementAdapter` currently requires the third `DwellMode mode` ctor argument.

- [ ] **Step 3: Implement**

`WpfElementAdapter.cs` — replace (lines 15-16 and 29-34):

```csharp
    public string Id { get; }
    public DwellMode Mode { get; }
...
    public WpfElementAdapter(string id, FrameworkElement element, DwellMode mode)
    {
        Id = id;
        _element = element;
        Mode = mode;
    }
```

with:

```csharp
    public string Id { get; }

    // Read live so Dwell.Mode changes after registration take effect on the next activation.
    // Only ever read inside Activate()'s dispatched callback, i.e. on the element's UI thread.
    public DwellMode Mode => Dwell.GetMode(_element);
...
    public WpfElementAdapter(string id, FrameworkElement element)
    {
        Id = id;
        _element = element;
    }
```

`WinUiElementAdapter.cs` — same change to lines 18-26 (`public DwellMode Mode => Dwell.GetMode(_element);`, two-arg ctor).

Call sites: in `src/EyeCtrl.Sdk.Wpf/Dwell.cs` change `new WpfElementAdapter(id, el, GetMode(el))` to `new WpfElementAdapter(id, el)`; in `src/EyeCtrl.Sdk.WinUI/Dwell.cs:82` change `new WinUiElementAdapter(id, el, GetMode(el))` to `new WinUiElementAdapter(id, el)`.

`IDwellElementAdapter.cs` — update the `Mode` doc (lines 25-26) to:

```csharp
    /// <summary>How activation is delivered (invoke vs focus). Implementations read this live
    /// at activation time on the element's UI thread; it is not read by <see cref="DwellClient"/>.</summary>
    DwellMode Mode { get; }
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Sdk.Wpf.Tests/EyeCtrl.Sdk.Wpf.Tests.csproj
```

WinUI cannot be unit-tested here (no WinUI runtime in the linked-source test project) — manual check: in `samples/EyeCtrl.Sdk.WinUI.Sample`, toggle a button's `Dwell.Mode` at runtime and confirm the next dwell focuses instead of invoking.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.Sdk.Wpf src/EyeCtrl.Sdk.WinUI/WinUiElementAdapter.cs src/EyeCtrl.Sdk.WinUI/Dwell.cs src/EyeCtrl.Sdk.Abstractions/IDwellElementAdapter.cs tests/EyeCtrl.Sdk.Wpf.Tests/WpfElementAdapterTests.cs
git commit -m "fix(sdk): read Dwell.Mode live at activation instead of snapshotting at registration"
```

---

### Task C13: Shared WireJson options in EyeCtrl.Contracts

Verified duplication: `src/EyeCtrl.App/Services/ExternalDwellService.cs:29` and `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs:24` each build `new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }` — a drift risk for the wire protocol. **Scope guard: the App phase owns ExternalDwellService.cs; this task changes ONLY line 29 of that file.**

**Files:**
- Create: `src/EyeCtrl.Contracts/WireJson.cs`
- Modify: `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs:24`
- Modify: `src/EyeCtrl.App/Services/ExternalDwellService.cs:29` (single line — see scope guard)
- Test: `tests/EyeCtrl.Sdk.Abstractions.Tests/WireJsonTests.cs` (new file)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Sdk.Abstractions.Tests/WireJsonTests.cs`:

```csharp
namespace EyeCtrl.Sdk.Abstractions.Tests;

using System.Text.Json;
using EyeCtrl.Contracts;

public class WireJsonTests
{
    [Fact]
    public void Options_SerializeCamelCase_WithTypeDiscriminator()
    {
        WireMessage msg = new RegisterMessage { Id = "a", Rect = new SdkRect(1, 2, 3, 4), Hwnd = 5 };

        var json = JsonSerializer.Serialize(msg, WireJson.Options);

        Check.That(json).Contains("\"type\":\"register\"");
        Check.That(json).Contains("\"id\":\"a\"");
        Check.That(json).Contains("\"hwnd\":5");
    }

    [Fact]
    public void Options_RoundTripActivated()
    {
        var json = JsonSerializer.Serialize<WireMessage>(new ActivatedMessage { Id = "z" }, WireJson.Options);
        var back = JsonSerializer.Deserialize<WireMessage>(json, WireJson.Options);

        Check.That(back).IsInstanceOf<ActivatedMessage>();
        Check.That(((ActivatedMessage)back!).Id).IsEqualTo("z");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj --filter "FullyQualifiedName~WireJsonTests"
```

Expected: compile error CS0103 — `WireJson` does not exist.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.Contracts/WireJson.cs`:

```csharp
namespace EyeCtrl.Contracts;

using System.Text.Json;

/// <summary>
/// Single source of truth for the JSON settings of the newline-framed wire protocol between
/// the EyeCtrl dwell service and SDK clients. Both sides MUST use these options — a naming
/// policy drift between server and client silently breaks message routing.
/// </summary>
public static class WireJson
{
    /// <summary>Shared serializer options: camelCase property names (the "type" discriminator
    /// comes from the <see cref="WireMessage"/> polymorphism attributes).</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
```

`src/EyeCtrl.Sdk.Abstractions/DwellClient.cs:24` — replace:

```csharp
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
```

with:

```csharp
    private readonly JsonSerializerOptions _json = WireJson.Options;
```

`src/EyeCtrl.App/Services/ExternalDwellService.cs:29` — replace:

```csharp
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
```

with:

```csharp
    private readonly JsonSerializerOptions _json = WireJson.Options;
```

(`using EyeCtrl.Contracts;` already exists in both files — DwellClient.cs:8, ExternalDwellService.cs:11.)

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.Contracts/WireJson.cs src/EyeCtrl.Sdk.Abstractions/DwellClient.cs src/EyeCtrl.App/Services/ExternalDwellService.cs tests/EyeCtrl.Sdk.Abstractions.Tests/WireJsonTests.cs
git commit -m "refactor(contracts): shared WireJson options for both sides of the dwell wire protocol"
```

---

### Task C14: Remove dead EyeCareService._screenCenter field + ctor param

Verified: `_screenCenter` is assigned at `src/EyeCtrl.EyeCare/EyeCareService.cs:38` and read nowhere (only lines 19/29/38 mention it). All construction sites confirmed by grep: `src/EyeCtrl.App/Services/AppBootstrapper.cs:213-219` and `tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs:33`. (The separate `PhysicalPoint` passed to `EyeCareHooks` at AppBootstrapper.cs:204-212 is a different, live parameter — leave it alone.)

**Files:**
- Modify: `src/EyeCtrl.EyeCare/EyeCareService.cs:19,25-38`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs:213-219` (mechanical argument removal only)
- Modify: `tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs:33`
- Test: existing suites (dead-code removal — no new test; the compiler is the verifier)

- [ ] **Step 1: Write the failing test** — none (dead-code removal). The regression guard is the full existing EyeCare suite plus a solution build.

- [ ] **Step 2: Run test to verify it fails** — n/a; instead confirm the field is dead: `grep -n "_screenCenter" src/EyeCtrl.EyeCare/EyeCareService.cs` must show only the declaration and the ctor assignment.

- [ ] **Step 3: Implement**

`EyeCareService.cs`: delete line 19 (`private readonly PhysicalPoint _screenCenter;`), delete ctor parameter `PhysicalPoint screenCenter,` (line 29), delete `_screenCenter = screenCenter;` (line 38). New signature:

```csharp
    public EyeCareService(
        EyeCareSettings settings,
        ExerciseRunner runner,
        IEyeCareHooks hooks,
        double fixationRadiusPixels = 50,
        int fatigueWindowSeconds = 60)
```

`AppBootstrapper.cs:213-219` — replace:

```csharp
        services.AddSingleton<IEyeCareService, EyeCareService>(sp => new EyeCareService(
            sp.GetRequiredService<EyeCtrlSettings>().EyeCare,
            sp.GetRequiredService<ExerciseRunner>(),
            sp.GetRequiredService<IEyeCareHooks>(),
            new PhysicalPoint(
                System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX / 2,
                System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY / 2)));
```

with:

```csharp
        services.AddSingleton<IEyeCareService, EyeCareService>(sp => new EyeCareService(
            sp.GetRequiredService<EyeCtrlSettings>().EyeCare,
            sp.GetRequiredService<ExerciseRunner>(),
            sp.GetRequiredService<IEyeCareHooks>()));
```

`EyeCareServiceTests.cs:33` — replace:

```csharp
        return new EyeCareService(settings, runner, hooks, new PhysicalPoint(0, 0));
```

with:

```csharp
        return new EyeCareService(settings, runner, hooks);
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.EyeCare/EyeCareService.cs src/EyeCtrl.App/Services/AppBootstrapper.cs tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs
git commit -m "refactor(eyecare): remove dead EyeCareService screenCenter field and ctor param"
```

---

### Task C15: Document intentional net8.0 TFMs in SDK/Contracts csprojs

Verified TFMs: Contracts `net8.0`, Sdk.Abstractions `net8.0`, Sdk.Wpf `net8.0-windows` — everything else in the solution is net10.0. These are NuGet-shaped SDK packages (`PackageId`/`Description` present on Sdk.Wpf); the lower TFM maximizes consumer reach. An XML comment stops a future "align TFMs" cleanup from breaking that.

**Files:**
- Modify: `src/EyeCtrl.Contracts/EyeCtrl.Contracts.csproj:3`
- Modify: `src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj:3`
- Modify: `src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj:3`
- Test: build only

- [ ] **Step 1: Write the failing test** — none (comment-only change).

- [ ] **Step 2: Run test to verify it fails** — n/a.

- [ ] **Step 3: Implement**

Directly above the `<TargetFramework>` line in each of the three csprojs, insert (adjusting the TFM name per file):

```xml
    <!-- net8.0 ON PURPOSE (rest of the solution is net10.0): this assembly ships to external
         SDK consumers, and targeting the oldest supported LTS maximizes the apps that can
         reference it. Do NOT "upgrade" this to match the solution TFM until .NET 8 LTS
         end-of-life (Nov 2026), or sooner if the SDK-consumer-reach policy changes. -->
```

For `EyeCtrl.Sdk.Wpf.csproj` use the same comment with "net8.0-windows ON PURPOSE ...". Do not add it to `EyeCtrl.Sdk.WinUI.csproj` — its `net8.0-windows10.0.19041.0` TFM is dictated by WindowsAppSDK, which the existing comment block (lines 13-15) already contextualizes; extend that block with one line: `<!-- TFM is net8.0 (not the solution's net10.0) for the same SDK-consumer-reach reason as Sdk.Abstractions/Sdk.Wpf. -->` if desired.

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.Contracts/EyeCtrl.Contracts.csproj src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj
git commit -m "docs(sdk): mark net8.0 TFMs as intentional (SDK consumer reach)"
```

---

### Task C16: Seal ExerciseMetrics, SmallLMPredictor, LMTokenizer

Verified by grep: no type in the repo derives from any of the three (`: SmallLMPredictor|: LMTokenizer|: ExerciseMetrics` matches only a docs file). All sibling types in these projects are already `sealed` (`FatigueMonitor`, `EyeCareScheduler`, `EyeCareService`, `DwellClient`, ...).

**Files:**
- Modify: `src/EyeCtrl.EyeCare/Scoring/ExerciseMetrics.cs:6`
- Modify: `src/EyeCtrl.LM/SmallLMPredictor.cs:14`
- Modify: `src/EyeCtrl.LM/LMTokenizer.cs:7`
- Test: build + existing suites

- [ ] **Step 1: Write the failing test** — none (the compiler enforces this; any hidden inheritor would fail the solution build).

- [ ] **Step 2: Run test to verify it fails** — n/a; re-verify no inheritors first: `grep -rn ": SmallLMPredictor\|: LMTokenizer\|: ExerciseMetrics" src tests samples` → only doc hits.

- [ ] **Step 3: Implement**

```csharp
public class ExerciseMetrics          →  public sealed class ExerciseMetrics
public class SmallLMPredictor : ILanguageModelPredictor  →  public sealed class SmallLMPredictor : ILanguageModelPredictor
public class LMTokenizer : IDisposable                   →  public sealed class LMTokenizer : IDisposable
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.EyeCare/Scoring/ExerciseMetrics.cs src/EyeCtrl.LM/SmallLMPredictor.cs src/EyeCtrl.LM/LMTokenizer.cs
git commit -m "refactor: seal ExerciseMetrics, SmallLMPredictor, LMTokenizer (no inheritors)"
```

---

## Deferred (noted, not planned)

- **LM full-phrase suggestion entry**: `PostProcess` could additionally emit the whole generated phrase as one entry, but the keyboard would type it wholesale on selection — a product/UX decision, not a bug fix. Revisit with the keyboard owner.
- **KV cache for `RunInference`**: only the TODO comment is planned (C10e); implementing it requires re-exporting the ONNX models with `past_key_values` I/O.
- **Generic timer abstraction for the SDK heads**: `DispatcherTimer` vs `DispatcherQueueTimer` share no interface; unifying the ThreadGroup plumbing would need a hand-rolled timer seam for ~30 duplicated lines. Not worth it (per the finding's own "don't force it" guidance).
- **Pipe-level integration tests for `DwellClient`** (reconnect/resend/activation routing): needs an in-test named-pipe server harness; C4's race fixes are review-verified with smoke-test guards instead.
- **Exact O(1) `FatigueMonitor` fixation ratio**: mathematically incompatible with the centroid-relative definition (see C6); single allocation-free pass is the end state.
- **WinUI-head unit tests for C12's live `Mode`**: the WinUI test project links sources without the WinUI runtime and cannot compile `WinUiElementAdapter`; covered by the WPF twin test + manual sample check.
- **`IEyeCareService` relocation to Core**: already flagged in its own doc comment as a future-milestone move; out of scope here.

---

## Phase B: App Layer

This phase covers `src/EyeCtrl.App/` plus one Core change (a target-filter capability on `DwellDetector`). It runs **after** Phases D, A, and C and consumes their contracts: Phase D's fixed `SettingsViewModel.ResetAllSettings()` (incl. Backdrop) and the new `ScrollOverlayViewModel(IMessenger, ScrollSettings, DwellSettings)` ctor with `DwellSettings.ScrollStopButtonDwellMs`; Phase A's `PhysicalPoint.DistanceSquaredTo/DistanceTo`; Phase C's `EyeCtrl.Contracts.WireJson.Options` (already applied to `ExternalDwellService.cs` line 29 — this phase must NOT re-plan that line and must preserve it when rewriting the file). Tasks are ordered bug fixes first (B1–B9), then refactors/cleanups (B10–B24).

**Global conventions for every task:**
- Build: `dotnet build EyeCtrl.slnx`
- Tests are xUnit v3 + NFluent (`Check.That(...)`, never `Assert.*`). App-layer tests live in `tests/EyeCtrl.App.Tests`, Core tests in `tests/EyeCtrl.Core.Tests`. WPF-window-touching tests use `[StaFact]` (Xunit.StaFact 3.0.13 is already referenced by App.Tests).
- ~13 AppLaunch/UI tests fail headless — environmental, not regressions. Always run scoped: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~<TestClass>"`.
- Per CLAUDE.md: run `impact({target: "<symbol>", direction: "upstream"})` before editing any symbol, and `detect_changes()` before each commit. This is repeated explicitly only where risk is HIGH.

---

### Task B1: Extract PredictionStackFactory + save user dictionary before rebuild

Fixes HIGH bug 1(a): `EventCoordinator.LoadKeyboard` calls `_orchestrator?.Dispose()` (line 1022 and 1028) without saving the user dictionary first — `PredictionOrchestrator.Dispose()` only disposes the LM predictor (verified: `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs:142-145`), so all words/bigrams/phrases learned since the last save are lost on ANY settings change. Also implements finding 11: the prediction-stack construction (lines 953–1031) moves into a testable factory that owns the current orchestrator, so the save-before-rebuild lives in exactly one place.

**Files:**
- Create: `src/EyeCtrl.App/Services/PredictionStackFactory.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:44,61-62,922-1038,1173-1177`
- Test: `tests/EyeCtrl.App.Tests/Services/PredictionStackFactoryTests.cs` (new)

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using Xunit;

public class PredictionStackFactoryTests : IDisposable
{
    private readonly string _appDataDir;
    private readonly string _baseDir;

    public PredictionStackFactoryTests()
    {
        _appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl-psf-" + Guid.NewGuid().ToString("N"));
        _baseDir = Path.Combine(_appDataDir, "base");
        Directory.CreateDirectory(_appDataDir);
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_appDataDir, recursive: true); } catch (IOException) { }
    }

    private static EyeCtrlSettings PredictionSettings(bool enabled = true)
    {
        var s = new EyeCtrlSettings();
        s.Keyboard.PredictionEnabled = enabled;
        s.Keyboard.LanguagePack = "en";
        s.Keyboard.MLSuggestionsEnabled = false; // never load ONNX in tests
        return s;
    }

    [Fact]
    public void Rebuild_PredictionDisabled_ReturnsNull()
    {
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(PredictionSettings(enabled: false), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(orchestrator).IsNull();
        Check.That(factory.Current).IsNull();
    }

    [Fact]
    public void Rebuild_PredictionEnabled_ReturnsOrchestratorWithWordPredictor()
    {
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(PredictionSettings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(orchestrator).IsNotNull();
        Check.That(orchestrator!.WordPredictor).IsNotNull();
        Check.That(factory.Current).IsSameReferenceAs(orchestrator);
    }

    [Fact]
    public void Rebuild_SavesLearnedWordsOfPreviousStack()
    {
        // THE regression test for the lost-user-dictionary bug: learn a word,
        // rebuild (as OnSettingsChanged does), and expect the new stack to know it.
        using var factory = new PredictionStackFactory();
        var settings = PredictionSettings();

        var first = factory.Rebuild(settings, _baseDir, _appDataDir, modelRegistry: null);
        first!.LearnWord("zebrastripes");
        first.LearnWord("zebrastripes");
        first.LearnWord("zebrastripes");

        var second = factory.Rebuild(settings, _baseDir, _appDataDir, modelRegistry: null);

        var words = second!.WordPredictor.GetSuggestions("zebrastr", null, 5).Select(r => r.Word).ToList();
        Check.That(words).Contains("zebrastripes");
    }

    [Fact]
    public void Dispose_SavesUserDictionaryToDisk()
    {
        var settings = PredictionSettings();
        using (var factory = new PredictionStackFactory())
        {
            var orchestrator = factory.Rebuild(settings, _baseDir, _appDataDir, modelRegistry: null);
            orchestrator!.LearnWord("qwertyzonk");
        }
        Check.That(File.Exists(Path.Combine(_appDataDir, "user_dictionary_en.json"))).IsTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~PredictionStackFactoryTests"
```
Expected: compile error `The type or namespace name 'PredictionStackFactory' could not be found` (class does not exist yet).

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.App/Services/PredictionStackFactory.cs`. The construction code below is the code currently at `EventCoordinator.cs:953-1031`, moved verbatim except: (1) `AppDomain.CurrentDomain.BaseDirectory` becomes the `baseDirectory` parameter, (2) the swap now saves the user dictionary first, (3) the language auto-detect side effect stays in EventCoordinator (it mutates settings + saves — not a factory concern):

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.IO;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.LM;
using Serilog;

/// <summary>
/// Builds and owns the prediction stack (dictionary provider, WordPredictor, sentence-completion
/// trie, tokenizer/LM predictor, orchestrator). Owns the swap: the previous stack's user
/// dictionary is ALWAYS saved before it is disposed, so learned words survive settings changes.
/// Extracted from EventCoordinator.LoadKeyboard.
/// </summary>
public sealed class PredictionStackFactory : IDisposable
{
    private PredictionOrchestrator? _current;
    private bool _disposed;

    /// <summary>The currently active orchestrator, or null when prediction is disabled.</summary>
    public PredictionOrchestrator? Current => _current;

    /// <summary>
    /// Tears down the current stack (saving its user dictionary) and builds a new one from
    /// <paramref name="settings"/>. Returns null when prediction is disabled.
    /// </summary>
    /// <param name="settings">Application settings (Keyboard.* drives the build).</param>
    /// <param name="baseDirectory">App base directory containing Resources/ (AppDomain.CurrentDomain.BaseDirectory in production).</param>
    /// <param name="appDataDir">%APPDATA%\EyeCtrl directory for user dictionary + models.</param>
    /// <param name="modelRegistry">Model registry for ML predictor paths, or null to skip ML.</param>
    public PredictionOrchestrator? Rebuild(EyeCtrlSettings settings, string baseDirectory,
        string appDataDir, ModelRegistryService? modelRegistry)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDir);

        // FIX (was the lost-learned-words bug): persist before dropping the old stack.
        // PredictionOrchestrator.Dispose only disposes the LM predictor — it does NOT save.
        SaveAndDisposeCurrent();

        if (!settings.Keyboard.PredictionEnabled)
            return null;

        var langCode = settings.Keyboard.LanguagePack;
        var dictPath = Path.Combine(baseDirectory, "Resources", "Dictionaries", $"{langCode}.json");
        var provider = new FileDictionaryProvider(dictPath, appDataDir, langCode);
        var wordPredictor = new WordPredictor(provider, settings.Keyboard.ActiveLayout);
        wordPredictor.ApplyDecay(settings.Keyboard.DecayEnabled, settings.Keyboard.DecayHalfLifeDays);

        // Load sentence completion trie if available
        SentenceCompletionIndex? trieIndex = null;
        var triePath = Path.Combine(baseDirectory, "Resources", "Completions", $"completions_{langCode}.bin");
        if (File.Exists(triePath))
        {
            try
            {
                using var trieStream = File.OpenRead(triePath);
                trieIndex = SentenceCompletionIndex.Load(trieStream);
                Log.Information("Loaded sentence completion trie: {Path}", triePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load sentence completion trie from {Path}", triePath);
            }
        }

        // Create LM predictor if ML settings are enabled
        ILanguageModelPredictor? lmPredictor = null;
        if (settings.Keyboard.MLSuggestionsEnabled && modelRegistry != null)
        {
            var modelsDir = Path.Combine(appDataDir, "models");
            var modelSize = settings.Keyboard.MLModelSize;

            if (!string.IsNullOrEmpty(modelSize) && modelSize != "Off")
            {
                LMTokenizer? tokenizer = null;
                var tokenizerPath = modelRegistry.GetTokenizerPath(modelSize);

                try
                {
                    if (File.Exists(tokenizerPath))
                        tokenizer = new LMTokenizer(tokenizerPath);
                    else
                        Log.Warning("Tokenizer file not found at {Path}; LM suggestions disabled", tokenizerPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to load tokenizer from {Path}; LM suggestions disabled", tokenizerPath);
                }

                var modelPath = modelRegistry.GetModelPath(modelSize);

                lmPredictor = new SmallLMPredictor(
                    modelsDir,
                    modelSize,
                    settings.Keyboard.PreferGPU,
                    tokenizer: tokenizer,
                    modelPath: modelPath);
            }
        }

        _current = new PredictionOrchestrator(wordPredictor, trieIndex, lmPredictor,
            settings.Keyboard.MLSuggestionsEnabled);
        return _current;
    }

    private void SaveAndDisposeCurrent()
    {
        if (_current == null) return;
        try
        {
            _current.SaveUserDictionary();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save user dictionary before prediction-stack rebuild");
        }
        _current.Dispose();
        _current = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveAndDisposeCurrent();
    }
}
```

Then in `EventCoordinator.cs`:

1. Replace the field `private PredictionOrchestrator? _orchestrator;` (line 44) with:
```csharp
private readonly PredictionStackFactory _predictionFactory = new();
```
2. Replace the two properties (lines 61-62):
```csharp
public WordPredictor? WordPredictor => _predictionFactory.Current?.WordPredictor;
public PredictionOrchestrator? Orchestrator => _predictionFactory.Current;
```
3. In `LoadKeyboard`, replace the whole `if (settings.Keyboard.PredictionEnabled) { ... } else { ... }` block (lines 953-1031) — currently starting with `if (settings.Keyboard.PredictionEnabled)` and ending with `keyboard.SetOrchestrator(null);` `}` — with:
```csharp
        if (settings.Keyboard.PredictionEnabled && string.IsNullOrEmpty(settings.Keyboard.LanguagePack))
        {
            settings.Keyboard.LanguagePack = LanguageDetector.Detect();
            settingsService.SaveDebounced(settings);
            Log.Information("Auto-detected language: {Lang}", settings.Keyboard.LanguagePack);
        }

        var orchestrator = _predictionFactory.Rebuild(
            settings, AppDomain.CurrentDomain.BaseDirectory, _appDataDir!, _modelRegistry);
        keyboard.SetOrchestrator(orchestrator);
```
4. In `Dispose()` (lines 1175-1177), replace:
```csharp
        _orchestrator?.WordPredictor.SaveUserDictionary();
        _orchestrator?.Dispose();
        _orchestrator = null;
```
with:
```csharp
        _predictionFactory.Dispose(); // saves the user dictionary before disposing the stack
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~PredictionStackFactoryTests"
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~EventCoordinatorTests"
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Services/PredictionStackFactory.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/PredictionStackFactoryTests.cs
git commit -m "fix: save user dictionary before prediction-stack rebuild; extract PredictionStackFactory"
```

---

### Task B2: Make OnSettingsChanged diff-aware (stop rebuilding the ONNX stack per slider tick)

Fixes HIGH bug 1(b): `EventCoordinator.OnSettingsChanged` (lines 728-740) unconditionally calls `Toolbar.Initialize(...)`, `Toolbar.SetMode(...)`, and `LoadKeyboard(...)` on EVERY settings change — `SettingsWindow.ApplyAndSave()` fires `SettingsChanged` on every slider tick, so the ONNX model reloads and the keyboard rebuilds when e.g. cursor opacity changes. Fix: capture value snapshots of the keyboard/prediction-relevant and toolbar-relevant settings; rebuild each subsystem only when its snapshot changed.

**Files:**
- Create: `src/EyeCtrl.App/Services/SettingsSnapshots.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:728-740` (OnSettingsChanged), `LoadKeyboard` tail, new fields
- Test: `tests/EyeCtrl.App.Tests/Services/SettingsSnapshotsTests.cs` (new)

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using Xunit;

public class SettingsSnapshotsTests
{
    [Fact]
    public void KeyboardStackSnapshot_UnrelatedSettingChange_SnapshotsAreEqual()
    {
        var s = new EyeCtrlSettings();
        var before = KeyboardStackSnapshot.Capture(s);
        s.Cursor.CircleOpacity = 0.42;           // cursor slider tick
        s.Toolbar.Size = 96;                     // toolbar change
        var after = KeyboardStackSnapshot.Capture(s);
        Check.That(after).IsEqualTo(before);
    }

    [Fact]
    public void KeyboardStackSnapshot_PredictionRelevantChange_SnapshotsDiffer()
    {
        var s = new EyeCtrlSettings();
        var before = KeyboardStackSnapshot.Capture(s);
        s.Keyboard.MLModelSize = "1.7B";
        Check.That(KeyboardStackSnapshot.Capture(s)).IsNotEqualTo(before);

        before = KeyboardStackSnapshot.Capture(s);
        s.Dwell.KeyboardKeyDwellMs += 25;
        Check.That(KeyboardStackSnapshot.Capture(s)).IsNotEqualTo(before);
    }

    [Fact]
    public void ToolbarSnapshot_OnlyToolbarFieldsMatter()
    {
        var s = new EyeCtrlSettings();
        var before = ToolbarSnapshot.Capture(s);
        s.Keyboard.KeySizeMultiplier = 1.5;
        Check.That(ToolbarSnapshot.Capture(s)).IsEqualTo(before);

        s.Toolbar.DockedEdge = "Left";
        Check.That(ToolbarSnapshot.Capture(s)).IsNotEqualTo(before);

        before = ToolbarSnapshot.Capture(s);
        s.Dwell.ToolbarButtonDwellMs += 50;
        Check.That(ToolbarSnapshot.Capture(s)).IsNotEqualTo(before);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~SettingsSnapshotsTests"
```
Expected: compile error — `KeyboardStackSnapshot` / `ToolbarSnapshot` do not exist.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.App/Services/SettingsSnapshots.cs`:

```csharp
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Settings;

/// <summary>
/// Value snapshot of every setting consumed by EventCoordinator.LoadKeyboard (layout, dwell
/// times, prediction stack, ML model). Two equal snapshots mean LoadKeyboard would produce an
/// identical result, so the rebuild can be skipped. Records give value equality for free.
/// </summary>
public sealed record KeyboardStackSnapshot(
    string ActiveLayout,
    double KeySizeMultiplier,
    bool PredictionEnabled,
    string LanguagePack,
    string ScreenEdge,
    int EmojiSkinTone,
    bool ClickSoundEnabled,
    bool DecayEnabled,
    int DecayHalfLifeDays,
    bool MLSuggestionsEnabled,
    string MLModelSize,
    bool PreferGPU,
    int KeyboardKeyDwellMs,
    int KeyboardPredictionDwellMs,
    int DwellCooldownMs)
{
    public static KeyboardStackSnapshot Capture(EyeCtrlSettings s) => new(
        s.Keyboard.ActiveLayout,
        s.Keyboard.KeySizeMultiplier,
        s.Keyboard.PredictionEnabled,
        s.Keyboard.LanguagePack,
        s.Keyboard.ScreenEdge,
        s.Keyboard.EmojiSkinTone,
        s.Keyboard.ClickSoundEnabled,
        s.Keyboard.DecayEnabled,
        s.Keyboard.DecayHalfLifeDays,
        s.Keyboard.MLSuggestionsEnabled,
        s.Keyboard.MLModelSize,
        s.Keyboard.PreferGPU,
        s.Dwell.KeyboardKeyDwellMs,
        s.Dwell.KeyboardPredictionDwellMs,
        s.Dwell.DwellCooldownMs);
}

/// <summary>Value snapshot of every setting consumed by ToolbarWindow.Initialize/SetMode.</summary>
public sealed record ToolbarSnapshot(double Size, bool IsDocked, string DockedEdge, int ToolbarButtonDwellMs)
{
    public static ToolbarSnapshot Capture(EyeCtrlSettings s) =>
        new(s.Toolbar.Size, s.Toolbar.IsDocked, s.Toolbar.DockedEdge, s.Dwell.ToolbarButtonDwellMs);
}
```

In `EventCoordinator.cs` add two fields next to `_predictionFactory` (from B1):

```csharp
    private KeyboardStackSnapshot? _keyboardSnapshot;
    private ToolbarSnapshot? _toolbarSnapshot;
```

Replace `OnSettingsChanged` (currently lines 728-740, quoted here in full):

```csharp
    private void OnSettingsChanged(EyeCtrlSettings s)
    {
        if (_context == null) return;

        _context.OverlayManager.CursorCircle.UpdateAppearance(s.Cursor);
        _context.OverlayManager.Toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
        _context.OverlayManager.Toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);
        _context.InteractionManager.SelectionMode = s.SelectionMode;
        _context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
        _context.Messenger.Send(new SelectionModeChangedMessage(s.SelectionMode));
        LoadKeyboard(_context.OverlayManager.Keyboard, s, _context.InputSimulator, _context.SettingsService);
        Log.Information("Settings saved");
    }
```

with:

```csharp
    private void OnSettingsChanged(EyeCtrlSettings s)
    {
        if (_context == null) return;

        // Cheap, idempotent updates: always apply.
        _context.OverlayManager.CursorCircle.UpdateAppearance(s.Cursor);
        _context.InteractionManager.SelectionMode = s.SelectionMode;
        _context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
        _context.Messenger.Send(new SelectionModeChangedMessage(s.SelectionMode));

        // Toolbar geometry: Initialize() rebuilds all buttons + dwell targets — only when changed.
        var toolbarNow = ToolbarSnapshot.Capture(s);
        if (toolbarNow != _toolbarSnapshot)
        {
            _toolbarSnapshot = toolbarNow;
            _context.OverlayManager.Toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
            _context.OverlayManager.Toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);
        }

        // Keyboard + prediction stack: LoadKeyboard reloads the layout AND rebuilds the prediction
        // stack (incl. the ONNX model) — only when a keyboard-relevant value actually changed.
        var keyboardNow = KeyboardStackSnapshot.Capture(s);
        if (keyboardNow != _keyboardSnapshot)
        {
            LoadKeyboard(_context.OverlayManager.Keyboard, s, _context.InputSimulator, _context.SettingsService);
        }

        Log.Information("Settings saved");
    }
```

At the END of `LoadKeyboard` (after the existing `keyboard.SetSoundService(new KeyboardSoundService(settings.Keyboard, wavPath));` line), add:

```csharp
        // Baseline for OnSettingsChanged's diffing. Captured at the end because LoadKeyboard may
        // auto-detect LanguagePack (mutating settings) above.
        _keyboardSnapshot = KeyboardStackSnapshot.Capture(settings);
```

(This also covers the initial startup call from App.xaml.cs:128, so the first settings tweak after startup diffs against the actual startup state.)

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~SettingsSnapshotsTests"
```
Manual verification: run the app (`--test-mode`), open Settings, drag "Circle Opacity" several ticks — the log must NOT show repeated "Loaded sentence completion trie"/model-load lines; change "Active Layout" — the keyboard must reload exactly once.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Services/SettingsSnapshots.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/SettingsSnapshotsTests.cs
git commit -m "fix: diff-aware OnSettingsChanged - rebuild toolbar/keyboard/prediction stack only when their settings changed"
```

---

### Task B3: SettingsWindow "Reset ALL Settings" delegates to SettingsViewModel.ResetAllSettings()

Fixes HIGH bug 2: the hand-maintained ~38-field reset list at `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:936-981` has drifted — verified it omits `Dwell.DwellCooldownMs`, all of Backdrop/EyeCare/Scroll/ExternalDwell/Calibration/Drag, and the keyboard ML fields. Phase D fixed `SettingsViewModel.ResetAllSettings()` (`src/EyeCtrl.ViewModels/SettingsViewModel.cs:209`) to enumerate every sub-setting including Backdrop — delegate to it. `_viewModel` is the window's `SettingsWindowViewModel` field (line 54); the SettingsViewModel is reachable as `_viewModel.Settings` (used throughout, e.g. line 264 `_viewModel.Settings.ShowTab(tabId)`).

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:936-981`
- Test: reset behavior covered by Phase D's ResetAllSettings tests in `tests/EyeCtrl.ViewModels.Tests`; window side is a one-line delegation inside a WPF window (not unit-testable headless)

- [ ] **Step 1: Write the failing test** — Not applicable at App level: the change deletes an inline lambda inside a WPF window that requires `InitializeComponent()` + 7 constructor services; the reset behavior itself is Phase D's tested code. Verification is Step 4's build + manual check. (Documented deviation, per plan rules on WPF-only behavior.)

- [ ] **Step 2: Verify precondition** — confirm Phase D landed:
```
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~ResetAllSettings"
```
Expected: Phase D's tests exist and pass. If they don't exist, STOP — phase ordering violated.

- [ ] **Step 3: Implement**

In `BuildGeneralPages()`, replace the entire `g_resetall` builder (lines 936-981). It currently reads (abridged only in this quote — the real block is the ~38 assignments from `_settings.Cursor.FilterMode = d.Cursor.FilterMode;` at line 940 down to `_settings.General.StartPaused = d.General.StartPaused;` at line 977):

```csharp
                () => Add(new DwellButton(_viewModel, "g_resetall", "⚠ Reset ALL Settings",
                    () => ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        var d = new EyeCtrlSettings();
                        _settings.Cursor.FilterMode = d.Cursor.FilterMode;
                        /* ... 36 more hand-copied assignments ... */
                        _settings.General.StartPaused = d.General.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
```

Replace with:

```csharp
                () => Add(new DwellButton(_viewModel, "g_resetall", "⚠ Reset ALL Settings",
                    () => ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        // Single source of truth for "reset everything" (fixed in Phase D to
                        // cover every sub-setting incl. Backdrop) — no more hand-copied list.
                        _viewModel.Settings.ResetAllSettings();
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
```

Behavior note: the old list set `Keyboard.LanguagePack = ""` explicitly; `ResetAllSettings()` copies the default `KeyboardSettings`, whose `LanguagePack` default is `""` (verified `src/EyeCtrl.Core/Settings/KeyboardSettings.cs:15`) — same result.

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```
Manual verification: run app → Settings → General → "Reset ALL Settings" → Yes; then check `%APPDATA%\EyeCtrl\settings.json` — `DwellCooldownMs`, `EyeCare`, `Scroll`, `ExternalDwell`, `Backdrop` must all be back at defaults.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "fix: Reset ALL Settings uses SettingsViewModel.ResetAllSettings instead of drifted hand-maintained field list"
```

---

### Task B4: "Reset Action Menu Settings" button actually resets

Fixes HIGH bug 3: at `SettingsWindow.xaml.cs:573-580` the confirm handler constructs `var a = new ActionMenuSettings();` and never uses it — nothing is reset. `ActionMenuSettings` has exactly one property, `VisibleActions` (verified `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs:11-18`).

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:573-580`

- [ ] **Step 1: Write the failing test** — Not applicable (one assignment inside a WPF-window lambda; see B3 Step 1 rationale). Verification is manual (Step 4).

- [ ] **Step 2: Confirm the current dead construction** — read lines 573-580; current code:

```csharp
                () => Add(new DwellButton(_viewModel, "am_reset", "Reset Action Menu Settings",
                    () => ShowModal("Reset Action Menu settings to defaults?", () =>
                    {
                        var a = new ActionMenuSettings();
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
```

- [ ] **Step 3: Implement** — replace the modal body:

```csharp
                () => Add(new DwellButton(_viewModel, "am_reset", "Reset Action Menu Settings",
                    () => ShowModal("Reset Action Menu settings to defaults?", () =>
                    {
                        // Copy defaults onto the live instance (ActionMenuSettings' only
                        // property is VisibleActions). Fresh list — don't alias the template.
                        var a = new ActionMenuSettings();
                        _settings.ActionMenu.VisibleActions = new List<InteractionAction>(a.VisibleActions);
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
```

(`InteractionAction` resolves via the file's existing `using EyeCtrl.Core.Models;` at line 8.)

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
```
Manual verification: edit `%APPDATA%\EyeCtrl\settings.json`, remove `"PinNote"` from `ActionMenu.VisibleActions`, restart app → Settings → Actions → "Reset Action Menu Settings" → Yes → the full default 8-action set is back in `settings.json`.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "fix: Reset Action Menu Settings constructed defaults and discarded them"
```

---

### Task B5: ExternalDwellService — always record registrations, gate only processing

Fixes HIGH bug 4: `src/EyeCtrl.App/Services/ExternalDwellService.cs:215` reads `case RegisterMessage r when _settings.Enabled:` — a registration arriving while the feature is disabled is silently dropped, permanently stranding the client (the client-side tracker dedupes subsequent sends to `Update`, which line 228 drops for unknown ids). This contradicts the service's own design comment at lines 60-64, which defends NOT clearing targets on disable precisely to avoid stranding clients; the same reasoning applies to accepting registrations while disabled. Processing is already gated in `OnCursorMoved` (line 80: `if (!_settings.Enabled || ...) return;`), so recording while disabled has no runtime effect until re-enable.

**Files:**
- Modify: `src/EyeCtrl.App/Services/ExternalDwellService.cs:215`
- Test: `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs` (add one test; reuse the file's existing `StubGazeProvider`, pipe-client helpers, and `RunOnStaThread`)

- [ ] **Step 1: Write the failing test**

Add to the existing `ExternalDwellServiceTests` class (it already contains `DisableThenReEnable_StillActivatesRegisteredTarget`, which registers while ENABLED; this new test registers while DISABLED):

```csharp
    [Fact]
    public void RegisterWhileDisabled_ThenEnable_DwellActivates()
    {
        RunOnStaThread(() =>
        {
            var settings = new ExternalDwellSettings { Enabled = false, DefaultDwellMs = 800, CooldownMs = 0 };
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = new ExternalDwellService(settings, engine, Dispatcher.CurrentDispatcher,
                NullLogger<ExternalDwellService>.Instance, new WeakReferenceMessenger(), _ => 7);
            svc.Start();

            var activated = RegisterWhileDisabledEnableAndDwell(settings, provider).GetAwaiter().GetResult();

            // Registration must be recorded even while the feature is disabled; enabling later
            // must make the already-registered target dwellable (no re-register from the client).
            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
            Check.That(activated).Contains("\"id\":\"btn\"");
        });
    }

    // Registers a target while Enabled == false, enables the feature, dwells over the target,
    // and returns the server's activation line (or null on timeout).
    private static async Task<string?> RegisterWhileDisabledEnableAndDwell(
        ExternalDwellSettings settings, StubGazeProvider provider)
    {
        await using var client = new NamedPipeClientStream(".", "eyectrl_dwell", PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, Json));
        await Task.Delay(200); // let the server process the registration (while disabled)

        settings.Enabled = true;

        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 800_000, IsValid: true));

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ExternalDwellServiceTests.RegisterWhileDisabled"
```
Expected failure: `Check.That(activated).IsNotNull()` fails (null — the registration was dropped, so no activation line arrives within the 2 s timeout).

- [ ] **Step 3: Implement**

In `HandleMessage` (line 215), change exactly one line. Current:

```csharp
            case RegisterMessage r when _settings.Enabled:
```

New:

```csharp
            // Always record the registration — processing is gated in OnCursorMoved instead.
            // Dropping it here would permanently strand the client: its tracker dedupes to
            // Update messages, which we discard for unknown ids (same rationale as the
            // do-NOT-clear-targets-on-disable comment in the constructor).
            case RegisterMessage r:
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ExternalDwellServiceTests"
```
All tests in the class (including the pre-existing 4) must pass.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Services/ExternalDwellService.cs tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs
git commit -m "fix: record external-dwell registrations while disabled so clients aren't stranded on re-enable"
```

---

### Task B6: EyeCareBannerWindow — DPI-correct dwell-target geometry

Fixes HIGH bug 5: `src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs:37-43` places dwell targets at `screenPhysWidth - 60` / `- 140` with `radius: 60` — raw logical-looking numbers subtracted from a PHYSICAL pixel coordinate. At 125/150 % DPI the invisible dwell circles drift left of the visible buttons. Cross-check against the XAML (`EyeCareBannerWindow.xaml:19-41`): the button row sits in a Grid with `Margin="20,0"`; the Start button is the rightmost 64x64 grid; the Snooze grid is 64x64 with `Margin="0,0,16,0"`. So in DIPs from the right screen edge: Start center = 20 + 32 = **52**, Snooze center = 20 + 64 + 16 + 32 = **132**. Vertical center 40 DIP is already converted correctly (line 36). Hit radius: 40 DIP (button half-size 32 + slack, and 40 < half the 80-DIP gap between centers so the two circles cannot overlap).

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs:29-43` (extract pure geometry + use it)
- Test: `tests/EyeCtrl.App.Tests/Overlays/EyeCareBannerGeometryTests.cs` (new)

- [ ] **Step 1: Write the failing test**

The geometry moves into a pure static (no DpiHelper global state, no WPF) so it is testable at any scale:

```csharp
namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using Xunit;

public class EyeCareBannerGeometryTests
{
    [Fact]
    public void ComputeDwellGeometry_At100Percent_MatchesXamlButtonCenters()
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(screenDipWidth: 1920, scaleX: 1.0, scaleY: 1.0);
        Check.That(g.Start.X).IsCloseTo(1920 - 52, 0.001);   // 20 margin + 32 half-button
        Check.That(g.Snooze.X).IsCloseTo(1920 - 132, 0.001); // 20 + 64 + 16 + 32
        Check.That(g.Start.Y).IsCloseTo(40, 0.001);          // banner is 80 DIP tall
        Check.That(g.Radius).IsCloseTo(40, 0.001);
    }

    [Fact]
    public void ComputeDwellGeometry_At150Percent_ScalesEverything()
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(screenDipWidth: 1280, scaleX: 1.5, scaleY: 1.5);
        // Physical screen width = 1280 * 1.5 = 1920 physical px.
        Check.That(g.Start.X).IsCloseTo(1920 - 52 * 1.5, 0.001);
        Check.That(g.Snooze.X).IsCloseTo(1920 - 132 * 1.5, 0.001);
        Check.That(g.Start.Y).IsCloseTo(40 * 1.5, 0.001);
        Check.That(g.Radius).IsCloseTo(40 * 1.5, 0.001);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~EyeCareBannerGeometryTests"
```
Expected: compile error — `ComputeDwellGeometry` does not exist.

- [ ] **Step 3: Implement**

In `EyeCareBannerWindow.xaml.cs`, add the pure helper and rewrite the positioning block. Current code (lines 32-43):

```csharp
        // Position the two dwell circles at top-right, ~64px diameter, centered vertically in the 80px banner.
        // ActualWidth and SystemParameters.PrimaryScreenWidth are in DIPs; DwellTarget expects physical pixels.
        var screenDipWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var screenPhysWidth = DpiHelper.LogicalToPhysicalX(screenDipWidth);
        var physTop = DpiHelper.LogicalToPhysicalY(40);
        _startCenter = new PhysicalPoint(screenPhysWidth - 60, physTop);
        _snoozeCenter = new PhysicalPoint(screenPhysWidth - 140, physTop);

        _dwell.ClearTargets();
        // DwellTarget ctor is (string id, PhysicalPoint center, double radius, int dwellTimeMs) — ms, not µs.
        _dwell.AddTarget(new DwellTarget("start", _startCenter, radius: 60, dwellTimeMs: _dwellMs));
        _dwell.AddTarget(new DwellTarget("snooze", _snoozeCenter, radius: 60, dwellTimeMs: _dwellMs));
```

New code:

```csharp
        // Position the two dwell circles over the XAML buttons. All XAML offsets are DIPs
        // (Grid Margin="20,0"; Snooze 64x64 with 16 right margin; Start 64x64), so convert
        // DIP offsets to physical px — at 125/150 % DPI raw numbers land left of the buttons.
        var screenDipWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var geometry = ComputeDwellGeometry(screenDipWidth, DpiHelper.ScaleX, DpiHelper.ScaleY);
        _startCenter = geometry.Start;
        _snoozeCenter = geometry.Snooze;

        _dwell.ClearTargets();
        // DwellTarget ctor is (string id, PhysicalPoint center, double radius, int dwellTimeMs) — ms, not µs.
        _dwell.AddTarget(new DwellTarget("start", _startCenter, geometry.Radius, dwellTimeMs: _dwellMs));
        _dwell.AddTarget(new DwellTarget("snooze", _snoozeCenter, geometry.Radius, dwellTimeMs: _dwellMs));
```

And add the pure static (internal + visible to tests, or public — App.Tests references the App assembly directly, so `public static` is simplest):

```csharp
    // XAML-derived DIP offsets (EyeCareBannerWindow.xaml): outer Grid Margin=20, buttons 64x64,
    // Snooze has 16 right margin before Start. Centers from the right edge: Start 52, Snooze 132.
    private const double StartCenterFromRightDip = 52;
    private const double SnoozeCenterFromRightDip = 132;
    private const double ButtonCenterFromTopDip = 40; // banner strip is 80 DIP tall
    private const double HitRadiusDip = 40;           // 32 half-button + slack; < half the 80-DIP center gap

    /// <summary>
    /// Computes the physical-pixel dwell geometry for the banner buttons. Pure function of the
    /// DIP screen width and DPI scale so it can be unit-tested at 100/125/150 %.
    /// </summary>
    public static (PhysicalPoint Start, PhysicalPoint Snooze, double Radius) ComputeDwellGeometry(
        double screenDipWidth, double scaleX, double scaleY)
    {
        var screenPhysWidth = screenDipWidth * scaleX;
        var physTop = ButtonCenterFromTopDip * scaleY;
        var start = new PhysicalPoint(screenPhysWidth - StartCenterFromRightDip * scaleX, physTop);
        var snooze = new PhysicalPoint(screenPhysWidth - SnoozeCenterFromRightDip * scaleX, physTop);
        return (start, snooze, HitRadiusDip * scaleX);
    }
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~EyeCareBannerGeometryTests"
```
**Manual verification (required, DPI fix):** set Windows display scaling to 150 %, run the app with eye-care enabled and a short break interval, wait for the banner, and verify that gazing at (or in `--test-mode`, mousing over) the visible ▶ start and ✕ snooze circles fills their dwell — and that gaze 80 px left of the snooze button does NOT trigger anything. Repeat at 100 %.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs tests/EyeCtrl.App.Tests/Overlays/EyeCareBannerGeometryTests.cs
git commit -m "fix: eye-care banner dwell targets use DPI-converted XAML-derived offsets (misaligned at 125/150% scaling)"
```

---

### Task B7: Persist emoji skin tone selection

Fixes HIGH bug 6: `EmojiGridManager.HandleAction` (`src/EyeCtrl.App/Overlays/EmojiGridManager.cs:212-220`) updates only the in-memory `_skinTone` on `setskintone:` — the choice is lost on restart. `settings.Keyboard.EmojiSkinTone` exists (source-generated from `KeyboardSettings._emojiSkinTone`, line 19) and is read at startup by `EventCoordinator.LoadKeyboard` (line 950: `keyboard.SetSkinTone(settings.Keyboard.EmojiSkinTone)`), but nothing ever writes it back. Wiring choice (verified): `KeyboardWindow.SetViewModel` creates the `EmojiGridManager` (lines 96-101) and EventCoordinator holds both settings and `SettingsService` — so: manager raises `SkinToneChanged`, `KeyboardWindow` forwards it, `EventCoordinator.Initialize` subscribes ONCE (not in LoadKeyboard, which runs on every keyboard-relevant settings change and would stack handlers). The never-called `KeyboardWindow.GetSkinTone`/`GetRecentStore` (lines 144-145, zero callers verified by grep) become superfluous under the event design — delete them.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/EmojiGridManager.cs:212-220` (+ event), `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:96-107,144-145`, `src/EyeCtrl.App/Services/EventCoordinator.cs` (Initialize + Dispose)
- Test: `tests/EyeCtrl.App.Tests/Overlays/EmojiGridManagerTests.cs` (new, `[StaFact]` — manager builds WPF `Border`s)

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Overlays;

using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using Xunit;

public class EmojiGridManagerTests
{
    private static EmojiGridManager NewManager() => new(
        keyRows: new StackPanel(),
        categoryBar: new StackPanel(),
        registerKeyVisual: (_, _, _, _) => { },
        getKeySize: () => 70,
        clearKeyState: () => { });

    [StaFact]
    public void SetSkinToneAction_RaisesSkinToneChanged()
    {
        var manager = NewManager();
        int? raised = null;
        manager.SkinToneChanged += tone => raised = tone;

        manager.HandleAction("setSkinTone:3");

        Check.That(raised).IsEqualTo(3);
        Check.That(manager.SkinTone).IsEqualTo(3);
    }

    [StaFact]
    public void SetSkinToneAction_ClampsOutOfRange()
    {
        var manager = NewManager();
        int? raised = null;
        manager.SkinToneChanged += tone => raised = tone;

        manager.HandleAction("setSkinTone:9");

        Check.That(raised).IsEqualTo(5);
    }

    [StaFact]
    public void SetSkinTone_ProgrammaticInitialization_DoesNotRaiseEvent()
    {
        // Startup path (LoadKeyboard -> SetSkinTone) must not echo a save back.
        var manager = NewManager();
        var raised = false;
        manager.SkinToneChanged += _ => raised = true;

        manager.SetSkinTone(2);

        Check.That(raised).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~EmojiGridManagerTests"
```
Expected: compile error — `SkinToneChanged` event does not exist on `EmojiGridManager`.

- [ ] **Step 3: Implement**

1. `EmojiGridManager.cs` — add the event next to `ExitRequested` (line 69):

```csharp
    public event Action? ExitRequested;
    /// <summary>Raised when the USER picks a skin tone (not on programmatic SetSkinTone).</summary>
    public event Action<int>? SkinToneChanged;
```

and in `HandleAction`, current `setskintone:` branch (lines 212-220):

```csharp
        if (lowerAction.StartsWith("setskintone:"))
        {
            if (int.TryParse(action[12..], out var tone))
            {
                _skinTone = Math.Clamp(tone, 0, 5);
                BuildGrid();
            }
            return;
        }
```

becomes:

```csharp
        if (lowerAction.StartsWith("setskintone:"))
        {
            if (int.TryParse(action[12..], out var tone))
            {
                _skinTone = Math.Clamp(tone, 0, 5);
                SkinToneChanged?.Invoke(_skinTone);
                BuildGrid();
            }
            return;
        }
```

2. `KeyboardWindow.xaml.cs` — forward the event. In `SetViewModel`, after the existing `_emojiManager.ExitRequested += ...` block (lines 102-106), add:

```csharp
        _emojiManager.SkinToneChanged += tone => SkinToneChanged?.Invoke(tone);
```

add the window-level event near the other public members:

```csharp
    /// <summary>Raised when the user picks an emoji skin tone; used to persist the choice.</summary>
    public event Action<int>? SkinToneChanged;
```

and DELETE lines 144-145 (never called, superseded by the event):

```csharp
    public int GetSkinTone() => _emojiManager?.SkinTone ?? 0;
    public RecentEmojiStore? GetRecentStore() => _emojiManager?.RecentStore;
```

3. `EventCoordinator.cs` — subscribe once in `Initialize` (after `SetupDragHandlers();`):

```csharp
        _context.OverlayManager.Keyboard.SkinToneChanged += OnEmojiSkinToneChanged;
```

add the handler (near OnSettingsChanged):

```csharp
    private void OnEmojiSkinToneChanged(int tone)
    {
        if (_context == null) return;
        _context.Settings.Keyboard.EmojiSkinTone = tone;
        _context.SettingsService.SaveDebounced(_context.Settings);
        // Keep the diff baseline in sync so this self-inflicted change doesn't
        // trigger a full keyboard reload on the next SettingsChanged event.
        _keyboardSnapshot = KeyboardStackSnapshot.Capture(_context.Settings);
    }
```

and unsubscribe in `Dispose()` (next to the other unsubscribes):

```csharp
        _context.OverlayManager.Keyboard.SkinToneChanged -= OnEmojiSkinToneChanged;
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~EmojiGridManagerTests"
```
Manual verification: run app → keyboard → emoji mode → 🎨 → pick a dark tone → close app → `settings.json` has `"EmojiSkinTone": 5` → restart → emoji grid shows the dark tone.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Overlays/EmojiGridManager.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Overlays/EmojiGridManagerTests.cs
git commit -m "fix: persist emoji skin tone to settings (was in-memory only); remove dead GetSkinTone/GetRecentStore"
```

---

### Task B8: Core — add an optional target pre-filter to DwellDetector.Update

Finding 7, part 1 (the ONE Core change owned by this phase). `ExternalDwellArbiter` (`src/EyeCtrl.App/Services/ExternalDwellArbiter.cs:49-116`) re-implements `DwellDetector.Update`'s state machine line-for-line (cooldown → nearest-hit selection → enter/accumulate/activate → reset-others); the only genuine difference is that the arbiter restricts candidates to targets whose registered hwnd matches the root window under the gaze. Give `DwellDetector` that capability directly: an `Update` overload taking `Func<DwellTarget, bool>? targetFilter`, applied during candidate selection only. The filter is per-call (not stored) because eligibility depends on per-frame context (the window under the gaze).

**RISK NOTE — HIGH fan-in.** `DwellDetector` has 32+ callers across ViewModels, App overlays, NoteManager, keyboard, eye-care. The executor MUST run `impact({target: "Update", direction: "upstream"})` (GitNexus, symbol `DwellDetector.Update`) before editing and report the blast radius. The change below is purely additive: the existing 2-arg `Update(PhysicalPoint, long)` keeps its exact signature (method-group compatible) and delegates with a null filter; null filter ⇒ byte-for-byte the old behavior. Do NOT convert the 2-arg form into an optional parameter (that would break any method-group/delegate usage).

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs:100-190` (Update)
- Test: `tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs` (append tests)

- [ ] **Step 1: Write the failing test**

Append to the existing `DwellDetectorTests` class (matches its established style — 30 ms steps, `Check.That`):

```csharp
    [Fact]
    public void Update_WithFilter_ExcludedTargetNeverAccumulates()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("blocked", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        long time = 0;
        for (int i = 0; i < 20; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time, targetFilter: _ => false);
            time += 30_000;
        }

        Check.That(activated).IsFalse();
        Check.That(detector.GetProgress("blocked")).IsEqualTo(0);
    }

    [Fact]
    public void Update_WithFilter_NearestEligibleWins_OverCloserIneligible()
    {
        var detector = new DwellDetector();
        // "near" is closer to the gaze but filtered out; "far" must accumulate instead.
        detector.AddTarget(new DwellTarget("near", new PhysicalPoint(100, 100), radius: 60, dwellTimeMs: 200));
        detector.AddTarget(new DwellTarget("far", new PhysicalPoint(140, 100), radius: 60, dwellTimeMs: 200));

        string? activatedId = null;
        detector.TargetActivated += id => activatedId = id;

        long time = 0;
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(105, 100), time, targetFilter: t => t.Id == "far");
            time += 30_000;
        }

        Check.That(activatedId).IsEqualTo("far");
    }

    [Fact]
    public void Update_FilterFlipsMidDwell_ExcludedTargetStateResets()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        // Accumulate 150 ms of the 200 ms dwell...
        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 150_000);
        // ...then become ineligible for one frame (e.g. another window moved under the gaze)...
        detector.Update(new PhysicalPoint(100, 100), 180_000, targetFilter: _ => false);
        // ...re-eligible: dwell must restart, so 60 more ms must NOT activate.
        detector.Update(new PhysicalPoint(100, 100), 210_000);
        detector.Update(new PhysicalPoint(100, 100), 240_000);

        Check.That(activated).IsFalse();
    }

    [Fact]
    public void Update_TwoArgOverload_StillActivates()
    {
        // Guard: the untouched 2-arg path (32+ existing callers) must be unchanged.
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));
        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 250_000);

        Check.That(activated).IsTrue();
    }
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~DwellDetectorTests"
```
Expected: compile error — no `Update` overload accepts a `targetFilter` argument.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.Core/Processing/DwellDetector.cs`, change `Update`. Current signature and candidate loop (lines 100, 126-143):

```csharp
    public void Update(PhysicalPoint gazePoint, long timestampMicroseconds)
    {
```
and
```csharp
        // ── Find the single nearest target within hit range ──
        string? nearestId = null;
        double nearestDistSq = double.MaxValue;

        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (target.HitTest(gazePoint))
            {
                double dx = gazePoint.X - target.Center.X;
                double dy = gazePoint.Y - target.Center.Y;
                double distSq = dx * dx + dy * dy;
                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearestId = target.Id;
                }
            }
        }
```

New code — keep the 2-arg method as a thin delegate and add the 3-arg overload; inside it, the only functional change is the filter check at the top of the candidate loop (use Phase A's `PhysicalPoint.DistanceSquaredTo` while touching the line):

```csharp
    /// <summary>
    /// Updates the dwell detector with the current gaze position and timestamp.
    /// Must be called from the UI thread (enforced by GazeEngineAdapter's Dispatcher marshaling).
    /// </summary>
    /// <param name="gazePoint">Current gaze position in physical screen pixels</param>
    /// <param name="timestampMicroseconds">Timestamp in microseconds</param>
    public void Update(PhysicalPoint gazePoint, long timestampMicroseconds)
        => Update(gazePoint, timestampMicroseconds, targetFilter: null);

    /// <summary>
    /// Same as <see cref="Update(PhysicalPoint, long)"/> but restricts dwell candidates to
    /// targets accepted by <paramref name="targetFilter"/>. Filtered-out targets behave exactly
    /// like targets the gaze is not over: their accumulated state resets. Pass null for the
    /// unfiltered behavior. The filter is evaluated per call because eligibility can depend on
    /// per-frame context (e.g. which window is under the gaze).
    /// </summary>
    public void Update(PhysicalPoint gazePoint, long timestampMicroseconds,
        Func<DwellTarget, bool>? targetFilter)
    {
```

…then inside the candidate loop, insert the filter check as the first statement of the loop body:

```csharp
        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (targetFilter != null && !targetFilter(target))
                continue; // ineligible this frame — never a candidate; reset happens below
            if (target.HitTest(gazePoint))
            {
                double distSq = gazePoint.DistanceSquaredTo(target.Center);
                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearestId = target.Id;
                }
            }
        }
```

Everything after (`// ── Accumulate dwell on nearest only; reset all others ──`, the activation firing, cooldown block) is untouched — a filtered-out target is simply never `nearestId`, so the existing "reset all others" branch resets it, which is the mid-dwell-flip semantics tested above.

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~DwellDetector"
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj
```
(The ViewModels suite exercises DwellDetector heavily through ActionMenu/Scroll/Toolbar VMs — run it to prove default behavior is unchanged. Also run `detect_changes()` and confirm only `DwellDetector.Update` and its new overload are affected.)

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.Core/Processing/DwellDetector.cs tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs
git commit -m "feat(core): DwellDetector.Update overload with per-call target filter (additive; default path unchanged)"
```

---

### Task B9: Rewrite ExternalDwellService on DwellDetector; delete ExternalDwellArbiter

Finding 7, part 2. With B8's filter, `ExternalDwellArbiter` (123 lines, incl. the dead `ArbiterResult.Suppress` — verified: `OnCursorMoved` reads `FeedbackId/FeedbackRect/Progress/ActivatedId`, never `.Suppress`) is a duplicate of Core's state machine and gets deleted. `ExternalDwellService` drives a `DwellDetector` with the hwnd-root filter and consumes its `TargetProgress`/`TargetActivated` events. **Preserve Phase C's `WireJson.Options` usage at the `_json` field (line 29) exactly as Phase C left it — do not touch that line.** Behavior notes (intentional, document in commit): feedback now appears from the second gaze frame of a dwell instead of the enter frame (DwellDetector fires progress from the first accumulating frame); mid-dwell rect updates preserve accumulation by mutating the held `DwellTarget` in place (same as arbiter's `AddOrUpdate`).

**Files:**
- Modify: `src/EyeCtrl.App/Services/ExternalDwellService.cs` (fields, ctor, OnCursorMoved, HandleMessage, connection cleanup)
- Delete: `src/EyeCtrl.App/Services/ExternalDwellArbiter.cs`
- Delete: `tests/EyeCtrl.App.Tests/Services/ExternalDwellArbiterTests.cs` (behavior assertions migrate below)
- Test: `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs` (add 3 migrated behavior tests)

- [ ] **Step 1: Write the failing tests (migrated from ExternalDwellArbiterTests)**

The arbiter tests asserted: (1) gated activation — wrong root window never activates; (2) feedback/suppression while accumulating; (3) cooldown blocks immediate retrigger. Migrate them to service level (pipe registration + `StubGazeProvider`), added to `ExternalDwellServiceTests` (reusing its `RunOnStaThread`, `Json`, `StubGazeProvider`, `StubInputSimulator`):

```csharp
    [Fact]
    public void Gating_WrongRootWindow_NeverActivates()
    {
        RunOnStaThread(() =>
        {
            var settings = new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 };
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            // Root under gaze is 999; the target registers with hwnd 7 — must never gate in.
            using var svc = new ExternalDwellService(settings, engine, Dispatcher.CurrentDispatcher,
                NullLogger<ExternalDwellService>.Instance, new WeakReferenceMessenger(), _ => 999);
            svc.Start();

            var activated = RegisterAndDwell(provider, dwellFrames: new long[] { 0, 800_000, 5_000_000 })
                .GetAwaiter().GetResult();

            Check.That(activated).IsNull();
            Check.That(engine.SuppressFixation).IsFalse();
        });
    }

    [Fact]
    public void Accumulating_SetsSuppressionAndReleasesWhenGazeLeaves()
    {
        RunOnStaThread(() =>
        {
            var settings = new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 };
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = new ExternalDwellService(settings, engine, Dispatcher.CurrentDispatcher,
                NullLogger<ExternalDwellService>.Instance, new WeakReferenceMessenger(), _ => 7);
            svc.Start();

            RegisterTarget().GetAwaiter().GetResult();

            provider.Raise(new GazePoint(100, 100, 0, IsValid: true));       // enter
            provider.Raise(new GazePoint(100, 100, 400_000, IsValid: true)); // accumulating (50%)
            Check.That(engine.SuppressFixation).IsTrue();                    // feedback active

            provider.Raise(new GazePoint(3000, 3000, 500_000, IsValid: true)); // leave
            Check.That(engine.SuppressFixation).IsFalse();
        });
    }

    [Fact]
    public void Cooldown_BlocksImmediateRetrigger()
    {
        RunOnStaThread(() =>
        {
            var settings = new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 500 };
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = new ExternalDwellService(settings, engine, Dispatcher.CurrentDispatcher,
                NullLogger<ExternalDwellService>.Instance, new WeakReferenceMessenger(), _ => 7);
            svc.Start();

            CooldownScenario(provider).GetAwaiter().GetResult();
        });
    }

    private static async Task CooldownScenario(StubGazeProvider provider)
    {
        await using var client = new NamedPipeClientStream(".", "eyectrl_dwell", PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, Json));
        await Task.Delay(200);

        // First dwell activates at t = 800 ms.
        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 800_000, IsValid: true));
        var first = r.ReadLineAsync();
        Check.That(await Task.WhenAny(first, Task.Delay(2000)) == first).IsTrue();
        Check.That(await first).Contains("activated");

        // t = 900 ms is inside the 500 ms cooldown (ends 1300 ms) — state resets, so an
        // uninterrupted-looking dwell ending at t = 1700 ms must NOT activate...
        provider.Raise(new GazePoint(100, 100, 900_000, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 1_700_000, IsValid: true));
        var second = r.ReadLineAsync();
        Check.That(await Task.WhenAny(second, Task.Delay(1000)) == second).IsFalse(); // timeout = no line

        // ...but continuing to t = 2500 ms (800 ms after the post-cooldown re-entry at 1700) does.
        provider.Raise(new GazePoint(100, 100, 2_500_000, IsValid: true));
        Check.That(await Task.WhenAny(second, Task.Delay(2000)) == second).IsTrue();
        Check.That(await second).Contains("activated");
    }

    private static async Task RegisterTarget()
    {
        await using var client = new NamedPipeClientStream(".", "eyectrl_dwell", PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, Json));
        await Task.Delay(200);
        // NOTE: keep the client alive for the caller's scenario? No — this helper is only used by
        // the suppression test, where disconnect-triggered target removal would break it. So:
        // do NOT dispose here; return the streams instead if needed. Simplest robust form below.
    }
```

**Correction to `RegisterTarget`:** a disconnecting client removes its targets (`HandleConnectionAsync` finally-block). The suppression test must keep the pipe open for the test body. Use this form instead — helper returns the connected client and the test disposes it at the end:

```csharp
    private static async Task<NamedPipeClientStream> RegisterTargetKeepOpen()
    {
        var client = new NamedPipeClientStream(".", "eyectrl_dwell", PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, Json));
        await Task.Delay(200);
        return client;
    }
```
…and in `Accumulating_SetsSuppressionAndReleasesWhenGazeLeaves` replace `RegisterTarget().GetAwaiter().GetResult();` with `using var client = RegisterTargetKeepOpen().GetAwaiter().GetResult();`. The same applies to `RegisterAndDwell` in the gating test — inline the register + raises with the client kept open:

```csharp
    private static async Task<string?> RegisterAndDwell(StubGazeProvider provider, long[] dwellFrames)
    {
        await using var client = new NamedPipeClientStream(".", "eyectrl_dwell", PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, Json));
        await Task.Delay(200);
        foreach (var t in dwellFrames)
            provider.Raise(new GazePoint(100, 100, t, IsValid: true));
        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(1500)) == read ? await read : null;
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ExternalDwellServiceTests"
```
`Gating_WrongRootWindow_NeverActivates` and `Cooldown_BlocksImmediateRetrigger` should PASS already (the old arbiter implements both) — they are safety nets for the rewrite. `Accumulating_SetsSuppressionAndReleasesWhenGazeLeaves` should also pass pre-rewrite. If all pass before the change, that is the point: they must STILL pass after. (This task's "failing test" is the post-delete compile: removing `ExternalDwellArbiter.cs` breaks `ExternalDwellService` until the rewrite lands.)

- [ ] **Step 3: Implement**

1. In `ExternalDwellService.cs`, replace the arbiter field (line 27) `private readonly ExternalDwellArbiter _arbiter;` with:

```csharp
    private readonly Func<PhysicalPoint, long?> _rootWindowAtPoint;
    // Core dwell state machine with the hwnd-root gating supplied per frame via B8's filter.
    // The service holds DwellTarget references so mid-dwell rect updates mutate in place
    // (AddTarget would replace the instance and reset accumulation).
    private readonly DwellDetector _detector;
    private readonly Dictionary<string, DwellTarget> _targetsById = new();
    private readonly Dictionary<string, long> _hwndById = new();
    // Per-frame event capture; written by detector events during Update, read right after.
    // Gaze-thread only, always under _stateLock.
    private string? _frameProgressId;
    private double _frameProgress;
    private string? _frameActivatedId;
```

2. In the constructor, replace `_arbiter = new ExternalDwellArbiter(rootWindowAtPoint ?? RootWindowAtPointImpl, settings.CooldownMs);` with:

```csharp
        _rootWindowAtPoint = rootWindowAtPoint ?? RootWindowAtPointImpl;
        _detector = new DwellDetector(settings.CooldownMs);
        _detector.TargetProgress += (id, p) =>
        {
            if (p > 0) { _frameProgressId = id; _frameProgress = p; }
            else if (_frameProgressId == id) { _frameProgressId = null; _frameProgress = 0; }
        };
        _detector.TargetActivated += id => _frameActivatedId = id;
```

and in the `PropertyChanged` handler replace `lock (_stateLock) _arbiter.SetCooldown(_settings.CooldownMs);` with `lock (_stateLock) _detector.SetCooldown(_settings.CooldownMs);`. Keep the whole not-clearing-on-disable comment.

3. Replace `OnCursorMoved` (lines 76-109) with:

```csharp
    private void OnCursorMoved(PhysicalPoint gaze, long timestampUs)
    {
        // Don't run while disabled or while EyeCtrl is paused. If we were holding
        // suppression/feedback, release it so we never strand SuppressFixation = true.
        if (!_settings.Enabled
            || _gazeEngine.InteractionManager.CurrentState == InteractionState.Paused)
        {
            ReleaseSuppression();
            SetFeedback(null, default, 0);
            return;
        }

        string? activatedId;
        string? feedbackId = null;
        PhysicalRect feedbackRect = default;
        double progress = 0;

        lock (_stateLock)
        {
            // Resolve the window under the gaze only when there is something to gate.
            long? root = _targetsById.Count == 0 ? null : _rootWindowAtPoint(gaze);

            _frameProgressId = null;
            _frameProgress = 0;
            _frameActivatedId = null;

            // Eligible only when the target's registered hwnd is the root window under the
            // gaze — the gating the old ExternalDwellArbiter re-implemented by hand.
            _detector.Update(gaze, timestampUs,
                t => root is { } r && _hwndById.TryGetValue(t.Id, out var h) && h == r);

            activatedId = _frameActivatedId;
            if (activatedId == null && _frameProgressId is { } fid
                && _targetsById.TryGetValue(fid, out var target) && target.Bounds is { } b)
            {
                feedbackId = fid;
                feedbackRect = b;
                progress = _frameProgress;
            }
        }

        if (activatedId is { } activated)
        {
            SendActivated(activated);
            ReleaseSuppression();
            SetFeedback(null, default, 0);
            return;
        }

        if (feedbackId is { } fid2)
        {
            AcquireSuppression();
            SetFeedback(fid2, feedbackRect, progress);
        }
        else
        {
            ReleaseSuppression();
            SetFeedback(null, default, 0);
        }
    }
```

4. Add the two target-management helpers (replacing arbiter calls):

```csharp
    // Must be called under _stateLock.
    private void AddOrUpdateTarget(string id, PhysicalRect rect, long hwnd)
    {
        if (_targetsById.TryGetValue(id, out var existing))
        {
            // In-place mutation: a rect move (window drag) must not reset accumulated dwell.
            existing.Bounds = rect;
            existing.Center = rect.Center;
        }
        else
        {
            var target = new DwellTarget(id, rect, _settings.DefaultDwellMs);
            _targetsById[id] = target;
            _detector.AddTarget(target);
        }
        _hwndById[id] = hwnd;
    }

    // Must be called under _stateLock.
    private void RemoveTarget(string id)
    {
        _targetsById.Remove(id);
        _hwndById.Remove(id);
        _detector.RemoveTarget(id);
    }
```

5. In `HandleMessage`, replace all three `_arbiter.*` calls: `_arbiter.AddOrUpdate(r.Id, ToRect(r.Rect), r.Hwnd, _settings.DefaultDwellMs)` → `AddOrUpdateTarget(r.Id, ToRect(r.Rect), r.Hwnd)`; same for the `UpdateMessage` case (`conn.Hwnd`); `_arbiter.Remove(un.Id)` → `RemoveTarget(un.Id)`. In `HandleConnectionAsync`'s finally block, `_arbiter.Remove(id)` → `RemoveTarget(id)`. Keep B5's un-gated `case RegisterMessage r:`.

6. Delete `src/EyeCtrl.App/Services/ExternalDwellArbiter.cs` and `tests/EyeCtrl.App.Tests/Services/ExternalDwellArbiterTests.cs` (including the now-dead `ArbiterResult` record and its `Suppress` flag).

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ExternalDwellServiceTests"
```
All service tests — the 4 pre-existing, B5's, and the 3 migrated — must pass. Run `detect_changes()` and confirm the affected symbols are only `ExternalDwellService.*` plus the deleted `ExternalDwellArbiter`.

- [ ] **Step 5: Commit**

```
git add -A src/EyeCtrl.App/Services/ExternalDwellService.cs src/EyeCtrl.App/Services/ExternalDwellArbiter.cs tests/EyeCtrl.App.Tests/Services/
git commit -m "refactor: ExternalDwellService drives Core DwellDetector via hwnd-root filter; delete duplicated ExternalDwellArbiter state machine"
```

---

### Task B10: Extract DwellArcButton control helper

Finding 9, part 1. Five near-identical copies of "circle button + Lime progress arc (+ symbol label)" rendering exist: `ActionMenuWindow.xaml.cs:199-254,275-292`, `ManualSelectionSideButtonWindow.xaml.cs:57-131`, `GlassBackdropWindow.xaml.cs:225-307`, `ActivationButtonWindow.xaml.cs:62-80`, `NoteWindow.xaml.cs:288-316` (variant: `ScrollOverlayWindow.xaml.cs:83-97`). All three fully-read programmatic sites share the exact constants `Fill=Argb(200,30,30,30)`, `Stroke=Argb(100,100,100,100)`, `ProgressArcInset=4`, `ArcThickness=4`, `FontSize=22`, `Stroke=Brushes.Lime`; NoteWindow differs (small radius, colored strokes, thickness 3, hide-at-zero); ActivationButton/Scroll have XAML-declared arcs and only share the arc-endpoint math. API therefore: an instance factory for the programmatic sites (with overridable style values) + a static `UpdateArc` for XAML-declared arcs.

**Files:**
- Create: `src/EyeCtrl.App/Controls/DwellArcButton.cs`
- Test: `tests/EyeCtrl.App.Tests/Controls/DwellArcButtonTests.cs` (new, `[StaFact]`)

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Controls;
using Xunit;

public class DwellArcButtonTests
{
    [StaFact]
    public void Create_PositionsEllipseAroundCenter()
    {
        var btn = DwellArcButton.Create(new Point(100, 100), radius: 50, symbol: "X");
        Check.That(btn.Background.Width).IsEqualTo(100);
        Check.That(Canvas.GetLeft(btn.Background)).IsEqualTo(50);
        Check.That(Canvas.GetTop(btn.Background)).IsEqualTo(50);
    }

    [StaFact]
    public void UpdateProgress_Half_ArcEndsAtBottomOfCircle()
    {
        var btn = DwellArcButton.Create(new Point(100, 100), radius: 50, symbol: null);
        btn.UpdateProgress(0.5);
        // At 50% the arc sweeps 180 degrees clockwise from 12 o'clock: end = center + (0, +arcRadius).
        var arcRadius = 50 - DwellArcButton.DefaultArcInset;
        Check.That(btn.Arc.Point.X).IsCloseTo(100, 0.001);
        Check.That(btn.Arc.Point.Y).IsCloseTo(100 + arcRadius, 0.001);
        Check.That(btn.Arc.IsLargeArc).IsFalse();
    }

    [StaFact]
    public void UpdateProgress_ThreeQuarters_SetsLargeArc()
    {
        var btn = DwellArcButton.Create(new Point(0, 0), radius: 50, symbol: null);
        btn.UpdateProgress(0.75);
        Check.That(btn.Arc.IsLargeArc).IsTrue();
        var arcRadius = 50 - DwellArcButton.DefaultArcInset;
        Check.That(btn.Arc.Point.X).IsCloseTo(-arcRadius, 0.001); // 270 deg clockwise from top = 9 o'clock
        Check.That(btn.Arc.Point.Y).IsCloseTo(0, 0.001);
    }

    [StaFact]
    public void UpdateProgress_Zero_CollapsesArcPath()
    {
        var btn = DwellArcButton.Create(new Point(0, 0), radius: 50, symbol: null);
        btn.UpdateProgress(0.5);
        btn.UpdateProgress(0);
        Check.That(btn.ProgressPath.Visibility).IsEqualTo(Visibility.Collapsed);
        btn.UpdateProgress(0.1);
        Check.That(btn.ProgressPath.Visibility).IsEqualTo(Visibility.Visible);
    }

    [StaFact]
    public void AddTo_AddsAllVisualsToCanvas()
    {
        var canvas = new Canvas();
        var btn = DwellArcButton.Create(new Point(10, 10), radius: 20, symbol: "✕");
        btn.AddTo(canvas);
        Check.That(canvas.Children.Count).IsEqualTo(3); // ellipse + arc path + label
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~DwellArcButtonTests"
```
Expected: compile error — `DwellArcButton` does not exist.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.App/Controls/DwellArcButton.cs`:

```csharp
namespace EyeCtrl.App.Controls;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core;

/// <summary>
/// The app's standard dwell button visual: a circular background, a clockwise Lime progress
/// arc starting at 12 o'clock, and an optional centered symbol label. Consolidates the five
/// hand-rolled copies (action menu, manual-selection side buttons, backdrop hide button,
/// pinned notes, activation button / scroll overlay via <see cref="UpdateArc"/>).
/// All coordinates are logical (DIP) canvas coordinates.
/// </summary>
public sealed class DwellArcButton
{
    public const double DefaultArcInset = 4;
    public const double DefaultArcThickness = 4;
    public const int DefaultSymbolFontSize = 22;
    private static readonly Color DefaultFill = Color.FromArgb(200, 30, 30, 30);
    private static readonly Color DefaultStroke = Color.FromArgb(100, 100, 100, 100);

    public Ellipse Background { get; }
    public Path ProgressPath { get; }
    public ArcSegment Arc { get; }
    public TextBlock? Label { get; }
    public Point Center { get; }
    public double Radius { get; }

    private readonly double _arcRadius;

    private DwellArcButton(Ellipse background, Path progressPath, ArcSegment arc,
        TextBlock? label, Point center, double radius, double arcRadius)
    {
        Background = background;
        ProgressPath = progressPath;
        Arc = arc;
        Label = label;
        Center = center;
        Radius = radius;
        _arcRadius = arcRadius;
    }

    /// <summary>Builds the visuals. Add them to a canvas with <see cref="AddTo"/>.</summary>
    public static DwellArcButton Create(
        Point center,
        double radius,
        string? symbol,
        Color? fill = null,
        Color? stroke = null,
        double strokeThickness = 1,
        Brush? arcBrush = null,
        double arcThickness = DefaultArcThickness,
        double arcInset = DefaultArcInset,
        double symbolFontSize = DefaultSymbolFontSize,
        Brush? symbolBrush = null)
    {
        var background = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = new SolidColorBrush(fill ?? DefaultFill),
            Stroke = new SolidColorBrush(stroke ?? DefaultStroke),
            StrokeThickness = strokeThickness
        };
        Canvas.SetLeft(background, center.X - radius);
        Canvas.SetTop(background, center.Y - radius);

        var arcRadius = radius - arcInset;
        var arc = new ArcSegment
        {
            Point = new Point(center.X, center.Y - arcRadius),
            Size = new Size(arcRadius, arcRadius),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        };
        var figure = new PathFigure
        {
            StartPoint = new Point(center.X, center.Y - arcRadius),
            IsClosed = false
        };
        figure.Segments.Add(arc);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        var progressPath = new Path
        {
            Data = geometry,
            Stroke = arcBrush ?? Brushes.Lime,
            StrokeThickness = arcThickness,
            Visibility = Visibility.Collapsed // shown on first positive progress
        };

        TextBlock? label = null;
        if (symbol != null)
        {
            label = new TextBlock
            {
                Text = symbol,
                Foreground = symbolBrush ?? Brushes.White,
                FontSize = symbolFontSize,
                TextAlignment = TextAlignment.Center,
                Width = radius * 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, center.X - radius);
            Canvas.SetTop(label, center.Y - label.DesiredSize.Height / 2);
        }

        return new DwellArcButton(background, progressPath, arc, label, center, radius, arcRadius);
    }

    /// <summary>Adds background, arc path, and (if present) label to <paramref name="canvas"/>.</summary>
    public void AddTo(Canvas canvas)
    {
        canvas.Children.Add(Background);
        canvas.Children.Add(ProgressPath);
        if (Label != null)
            canvas.Children.Add(Label);
    }

    /// <summary>Sets the dwell progress (0..1). Zero collapses the arc.</summary>
    public void UpdateProgress(double progress)
    {
        if (progress <= 0)
        {
            ProgressPath.Visibility = Visibility.Collapsed;
            Arc.Point = new Point(Center.X, Center.Y - _arcRadius);
            Arc.IsLargeArc = false;
            return;
        }
        ProgressPath.Visibility = Visibility.Visible;
        UpdateArc(Arc, Center, _arcRadius, progress);
    }

    /// <summary>
    /// Shared arc-endpoint math for XAML-declared arcs (activation button, scroll overlay):
    /// clockwise sweep starting at 12 o'clock. <paramref name="center"/>/<paramref name="arcRadius"/>
    /// are in the arc's own coordinate space.
    /// </summary>
    public static void UpdateArc(ArcSegment arc, Point center, double arcRadius, double progress)
    {
        var angle = Math.Clamp(progress, 0, 1) * MathConstants.FullCircleDegrees;
        var radians = angle * MathConstants.DegreesToRadians;
        arc.Point = new Point(
            center.X + arcRadius * Math.Sin(radians),
            center.Y - arcRadius * Math.Cos(radians));
        arc.IsLargeArc = angle > 180;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~DwellArcButtonTests"
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Controls/DwellArcButton.cs tests/EyeCtrl.App.Tests/Controls/DwellArcButtonTests.cs
git commit -m "feat: DwellArcButton helper consolidating circle-button + dwell-arc rendering (no call sites migrated yet)"
```

---

### Task B11: ActionMenuWindow — consume VM geometry/symbols and DwellArcButton

Finding 8 (+ first DwellArcButton migration, finding 9). `ActionMenuWindow.Show` (lines 190-197) re-derives the ring angles/positions that `ActionMenuViewModel.Receive(ShowActionMenuMessage)` already computed for its dwell targets (`ActionMenuViewModel.cs:66-73`, stored as `ActionMenuItemViewModel.Position` in PHYSICAL px), and duplicates `GetActionSymbol` (window lines 297-310 vs VM lines 118-130). If VM math and window math ever drift, dwell targets and visible buttons disagree. Fix: the window iterates `vm.Items` (physical→logical conversion per item) and uses `itemVm.Symbol`; delete the window's angle math and `GetActionSymbol`. **Detail found while reading:** the VM's symbol map is missing `InteractionAction.ScrollMode => "⇅"` which the window's map has — the VM currently falls back to `action.ToString()` for its (unrendered) Symbol. Add ScrollMode to the VM map first, or the scroll-mode button label regresses from ⇅ to "ScrollMode". That one-line ViewModels edit is required to preserve behavior and is in scope for this task.

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:118-130` (add ScrollMode symbol)
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs:33,41-59,190-254,275-292,297-311`
- Test: `tests/EyeCtrl.ViewModels.Tests` (symbol addition), manual visual verification (window)

- [ ] **Step 1: Write the failing test**

In `tests/EyeCtrl.ViewModels.Tests`, add to the existing ActionMenu VM test file (locate with `Glob tests/EyeCtrl.ViewModels.Tests/**/ActionMenu*Tests.cs`; if none exists, create `ActionMenuViewModelSymbolTests.cs`):

```csharp
namespace EyeCtrl.ViewModels.Tests;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

public class ActionMenuViewModelSymbolTests
{
    [Fact]
    public void Receive_ShowActionMenu_ScrollModeItemHasGlyphSymbol()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);

        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500),
            new List<InteractionAction> { InteractionAction.ScrollMode },
            ShowBack: false,
            DwellTimeMs: 600,
            new PhysicalPoint(500, 500),
            0,
            40));

        var item = vm.Items.Single();
        Check.That(item.Symbol).IsEqualTo("⇅");   // was "ScrollMode" (enum fallback)
    }
}
```

(Adjust the `ShowActionMenuMessage` construction to its real positional/named parameters — read `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` when writing the test; the shape above mirrors `GazeEngineAdapter.OnShowActionMenu`'s send at `GazeEngineAdapter.cs:133-150`.)

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~ActionMenuViewModelSymbolTests"
```
Expected failure: `Check.That(item.Symbol).IsEqualTo("⇅")` — actual is `"ScrollMode"`.

- [ ] **Step 3: Implement**

1. `ActionMenuViewModel.GetActionSymbol` (line 118): add before the fallback arm:

```csharp
        InteractionAction.ScrollMode => "⇅",             // gaze-joystick scroll (matches window glyph)
```

2. `ActionMenuWindow.xaml.cs` — replace the item construction + button loop. Delete lines 47-54 (the local `allItems` build):

```csharp
        var allItems = new List<(string Id, string Label)>();
        foreach (var action in actions)
        {
            var id = action.ToString();
            allItems.Add((id, GetActionSymbol(action)));
        }
        if (showBackButton)
            allItems.Add(("Back", "↩")); // ↩ Back arrow
```

and replace the ring-layout loop (lines 190-254, from `var angleStep = MathConstants.FullCircleDegrees / allItems.Count;` through the end of the `for` loop) plus the `_buttonVisuals` dictionary usage with VM-driven rendering. New field replacing line 33's tuple dictionary:

```csharp
    private readonly Dictionary<string, DwellArcButton> _buttons = new();
```

New loop (placed where the old `angleStep` loop was; `vm` is resolved once at the top of `Show` — move the existing `if (DataContext is ActionMenuViewModel vm)` check from line 257 up, and early-return with a warning log if the DataContext is not the VM, since the VM is now the geometry source):

```csharp
        if (DataContext is not ActionMenuViewModel vm)
        {
            Serilog.Log.Warning("ActionMenuWindow shown without ActionMenuViewModel DataContext; skipping buttons");
            Show();
            return;
        }

        // Single source of geometry + symbols: the VM already computed each item's ring position
        // (physical px) for its dwell targets. Convert physical -> logical and offset into this
        // window's canvas space. If these ever disagreed with local math, the visible buttons
        // and the dwell hit areas would drift apart — so there is no local math anymore.
        foreach (var itemVm in vm.Items)
        {
            var bx = canvasCenter.X + (DpiHelper.PhysicalToLogicalX(itemVm.Position.X)
                                       - DpiHelper.PhysicalToLogicalX(center.X));
            var by = canvasCenter.Y + (DpiHelper.PhysicalToLogicalY(itemVm.Position.Y)
                                       - DpiHelper.PhysicalToLogicalY(center.Y));

            var button = DwellArcButton.Create(new Point(bx, by), ButtonRadius, itemVm.Symbol,
                symbolFontSize: ButtonSymbolFontSize);
            button.AddTo(MenuCanvas);
            _buttons[itemVm.Id] = button;

            var capturedId = itemVm.Id;
            itemVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ActionMenuItemViewModel.DwellProgress))
                    UpdateItemProgress(capturedId, itemVm.DwellProgress);
            };
        }
```

(The old separate `foreach (var itemVm in vm.Items)` subscription block at lines 256-268 is absorbed into this loop — delete it.)

3. Replace `UpdateItemProgress` (lines 275-292) with:

```csharp
    private void UpdateItemProgress(string targetId, double progress)
    {
        if (_buttons.TryGetValue(targetId, out var button))
            button.UpdateProgress(progress);
    }
```

4. Delete the window's `GetActionSymbol` (lines 294-311) and clear `_buttons` where `_buttonVisuals.Clear()` was (line 44). Remove now-unused constants `ProgressArcThickness`, `ButtonBackgroundAlpha`, `ButtonBorderAlpha`, `ProgressArcInset` IF no other member uses them (`ProgressArcInset` is also referenced by nothing else after this change — verify with a file-local search before deleting). The `actions` and `showBackButton` parameters of `Show` become unused for layout but are still part of the signature used by `EventCoordinator.OnActionMenuVMPropertyChanged` (line 643) — leave the signature intact in this task; signature slimming is deferred (see "Deferred" section).

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~ActionMenu"
```
**Manual verification (required, visual alignment):** run app in `--test-mode`, fixate to open the action menu at screen center AND near a screen corner (clamped case): all 8 symbols render in a ring; hovering each button fills ITS arc (not a neighbor's) — this proves VM dwell targets and rendered buttons coincide; the back button (↩) appears in manual-selection mode; the scroll-mode button shows ⇅. Repeat once at 150 % DPI.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.ViewModels/ActionMenuViewModel.cs src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs tests/EyeCtrl.ViewModels.Tests/
git commit -m "refactor: ActionMenuWindow renders from ActionMenuViewModel items (single geometry/symbol source) via DwellArcButton"
```

---

### Task B12: Migrate ManualSelectionSideButtonWindow and GlassBackdropWindow to DwellArcButton

Finding 9, part 2. Both windows contain verbatim copies of the ellipse/arc/label construction and the arc-endpoint math (`ManualSelectionSideButtonWindow.xaml.cs:57-131`, `GlassBackdropWindow.xaml.cs:225-307`), with identical constants.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml.cs:13-22,51-131`
- Modify: `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs:23-35,225-329`
- Test: existing `tests/EyeCtrl.App.Tests/Overlays/GlassBackdropWindowTests.cs` must stay green; DwellArcButton math covered by B10 tests

- [ ] **Step 1: Write the failing test** — Not applicable: this is a pure like-for-like visual-construction swap; the arc math is unit-tested in B10 and `GlassBackdropWindowTests` pins the backdrop window's public behavior. Read `tests/EyeCtrl.App.Tests/Overlays/GlassBackdropWindowTests.cs` first — if it asserts on `DrawHideButton`/`UpdateHideButtonProgress` internals, adapt assertions to the helper-based fields in the SAME commit.

- [ ] **Step 2: Baseline** — run and record green:
```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~GlassBackdropWindowTests"
```

- [ ] **Step 3: Implement**

**ManualSelectionSideButtonWindow**: replace the field (line 22) `private readonly Dictionary<string, (Ellipse Background, Path ProgressArc, ArcSegment Arc, Point CanvasCenter)> _buttonVisuals = new();` with `private readonly Dictionary<string, DwellArcButton> _buttons = new();`; replace `DrawButton` (lines 57-114) with:

```csharp
    private void DrawButton(string id, string symbol, double cx, double cy)
    {
        var button = DwellArcButton.Create(new Point(cx, cy), ButtonRadius, symbol,
            symbolFontSize: ButtonSymbolFontSize);
        button.AddTo(ButtonCanvas);
        _buttons[id] = button;
    }
```

replace `UpdateProgress` (lines 116-131) with:

```csharp
    public void UpdateProgress(string buttonId, double progress)
    {
        if (_buttons.TryGetValue(buttonId, out var button))
            button.UpdateProgress(progress);
    }
```

update `ShowAt`'s `_buttonVisuals.Clear();` → `_buttons.Clear();`, add `using EyeCtrl.App.Controls;`, and delete the now-unused constants `ProgressArcInset`, `ProgressArcThickness`, `ButtonBackgroundAlpha`, `ButtonBorderAlpha` and unused `using System.Windows.Shapes;`/`EyeCtrl.Core` if nothing else in the file needs them (ButtonRadius/ButtonMargin/ButtonSymbolFontSize stay).

**GlassBackdropWindow**: replace the five fields (lines 29-35 `_hideButtonCircle/_hideButtonProgressPath/_hideButtonArcSegment/_hideButtonSymbol/_hideButtonCenterX/_hideButtonCenterY`) with:

```csharp
    private Rectangle? _solidBackground;
    private DwellArcButton? _hideButton;
```

replace `DrawHideButton` (lines 225-285) with:

```csharp
    public void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0)
    {
        ClearButton();
        var center = new Point(
            DpiHelper.PhysicalToLogicalX(buttonCenter.X),
            DpiHelper.PhysicalToLogicalY(buttonCenter.Y));
        _hideButton = DwellArcButton.Create(center, UIConstants.ButtonRadius, "✕",
            symbolFontSize: ButtonSymbolFontSize);
        _hideButton.AddTo(BackdropCanvas);
        if (progress > 0)
            _hideButton.UpdateProgress(progress);
    }
```

replace `UpdateHideButtonProgress` + `UpdateHideButtonArc` (lines 287-307) with:

```csharp
    public void UpdateHideButtonProgress(double progress) => _hideButton?.UpdateProgress(progress);
```

and replace `ClearButton` (lines 317-329) with:

```csharp
    private void ClearButton()
    {
        if (_hideButton != null)
        {
            BackdropCanvas.Children.Remove(_hideButton.Background);
            BackdropCanvas.Children.Remove(_hideButton.ProgressPath);
            if (_hideButton.Label != null)
                BackdropCanvas.Children.Remove(_hideButton.Label);
            _hideButton = null;
        }
    }
```

Add `using EyeCtrl.App.Controls;`; delete the now-unused constants `ProgressArcInset`, `ProgressArcThickness`, `ButtonBackgroundAlpha`, `ButtonBorderAlpha` (keep `ButtonSymbolFontSize`; keep the DWM constants). Note the tiny visual delta: the old code nudged the symbol down by `(dipSize - fontSize)/2 - 2`; DwellArcButton centers via measured text height — visually equivalent, confirm in the manual check.

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~GlassBackdropWindowTests|FullyQualifiedName~DwellArcButtonTests"
```
**Manual verification:** in `--test-mode` with Backdrop enabled: enter manual-selection mode — the ✕/↻ side buttons render with filling arcs on hover; the backdrop's top-right ✕ hide button renders and its arc fills on hover.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml.cs src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs
git commit -m "refactor: manual-selection side buttons and backdrop hide button use DwellArcButton"
```

---

### Task B13: Consolidate remaining arc math — ActivationButtonWindow, ScrollOverlayWindow, NoteWindow

Finding 9, part 3. These three keep their own layout (XAML-declared arcs / bespoke small buttons) but duplicate the arc-endpoint math. Replace with `DwellArcButton.UpdateArc` and, for NoteWindow, the full helper with its style knobs.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs:62-80`
- Modify: `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml.cs:83-97`
- Modify: `src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs:23-25,221-316`
- Test: `tests/EyeCtrl.App.Tests/Services/NoteManagerTests.cs` must stay green (NoteManager registers dwell targets mirroring NoteWindow buttons)

- [ ] **Step 1: Write the failing test** — Not applicable: arc math already covered by B10's `DwellArcButtonTests`; this task swaps duplicated implementations for the tested helper. Baseline: run `NoteManagerTests` green before starting.

- [ ] **Step 2: Baseline**
```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~NoteManagerTests"
```

- [ ] **Step 3: Implement**

**ActivationButtonWindow.UpdateDwellProgress** (lines 62-80) — current body computes angle/radians/endX/endY and the reset threshold; replace with:

```csharp
    public void UpdateDwellProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        DwellArcButton.UpdateArc(ArcSegment, new Point(ButtonCenter, ButtonCenter), ArcRadius, progress);

        // Reset start point if progress is ~0
        if (progress < ProgressResetThreshold)
        {
            ArcSegment.Point = new Point(ButtonCenter, ButtonCenter - ArcRadius);
        }
    }
```

**ScrollOverlayWindow.UpdateFeedback** arc block (lines 83-97) — replace the manual `angleDeg/angleRad/endX/endY` computation with:

```csharp
        if (stopProgress > 0)
        {
            StopButtonProgressArc.Visibility = Visibility.Visible;
            DwellArcButton.UpdateArc(_stopArc, new Point(25, 25), 25, stopProgress);
            StopButtonProgressArc.Data = _stopGeometry;
        }
        else
        {
            StopButtonProgressArc.Visibility = Visibility.Hidden;
        }
```

(Same math: old code used `(angleDeg - 90)` with cos/sin, which equals the sin/-cos form at 12-o'clock start; B10's tests pin the semantics.)

**NoteWindow** — replace the three visual dictionaries (lines 23-25) with `private readonly Dictionary<string, DwellArcButton> _noteButtons = new();` plus keep a per-button visibility map, and rewrite `DrawButtons` (221-265), `SetButtonVisible` (279-286), `UpdateDwellProgress` (288-316):

```csharp
    private void DrawButtons()
    {
        var symbols = new[] { ("close", "✕", Color.FromRgb(0xFF, 0x66, 0x66)),
                              ("edit",  "✎", Color.FromRgb(0x66, 0xFF, 0x66)),
                              ("move",  "↕", Color.FromRgb(0x66, 0x88, 0xFF)) };

        for (int i = 0; i < 3; i++)
        {
            var (suffix, symbol, color) = symbols[i];
            double cx = LayoutConstants.NoteWindowWidth - ButtonPadding - ButtonRadius;
            double cy = TitleBarHeight + ButtonPadding + ButtonRadius + i * (ButtonDiameter + ButtonGap);

            var button = DwellArcButton.Create(new Point(cx, cy), ButtonRadius, symbol,
                fill: Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
                stroke: color,
                arcThickness: 3,
                arcInset: 2,
                symbolFontSize: 14,
                symbolBrush: new SolidColorBrush(color));
            button.AddTo(_canvas);
            _noteButtons[$"{_noteId}:{suffix}"] = button;
        }
    }

    private void SetButtonVisible(string suffix, bool visible)
    {
        if (!_noteButtons.TryGetValue($"{_noteId}:{suffix}", out var button)) return;
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        button.Background.Visibility = vis;
        if (button.Label != null) button.Label.Visibility = vis;
        if (!visible) button.UpdateProgress(0); // collapses the arc
    }

    public void UpdateDwellProgress(string targetId, double progress)
    {
        if (!_noteButtons.TryGetValue(targetId, out var button)) return;
        Dispatcher.Invoke(() => button.UpdateProgress(progress));
    }
```

Also update `Draw()`'s clear block (lines 58-61): `_buttons.Clear(); _buttonLabels.Clear(); _arcs.Clear();` → `_noteButtons.Clear();`. Add `using EyeCtrl.App.Controls;`. Visual deltas to accept: arc radius is now `ButtonRadius - 2` via inset (identical to the old `r = ButtonRadius - 2`); label centering is measured rather than the old hard-coded `cx - 8 / cy - 10` (improves centering).

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~NoteManagerTests|FullyQualifiedName~DwellArcButtonTests"
```
**Manual verification:** in `--test-mode`: (1) fixate → activation button arc fills; (2) pin a note (📌) → the ✕/✎/↕ buttons show colored rings, hover fills each arc, Edit state hides ✎/↕; (3) enter scroll mode → stop-button arc fills on hover.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml.cs src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs
git commit -m "refactor: activation/scroll/note windows share DwellArcButton arc math"
```

---

### Task B14: Deduplicate ColorBlend and extract DwellHighlight

Finding 10. `ColorBlend.Blend` (`src/EyeCtrl.App/Controls/ColorBlend.cs:20-28`) is reimplemented verbatim as `SettingsWindow.Blend` (`SettingsWindow.xaml.cs:1210-1218`). Additionally, the "green dwell highlight" computation — `highlight = Argb(A, 0, 100 + 155*progress, 0)` then channel-wise blend by `progress` — is VERIFIED triplicated at `KeyboardWindow.OnPredictionProgress` (589-595), `KeyboardWindow.UpdateKeyVisual` (618-623), and `EmojiGridManager.OnCategoryProgress` (359-364). Extract `DwellHighlight.Toward(baseColor, progress)` next to ColorBlend and use it at all three sites; delete the SettingsWindow private copy.

**Files:**
- Modify: `src/EyeCtrl.App/Controls/ColorBlend.cs` (add DwellHighlight in same file or sibling)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:1141,1147,1158,1170,1210-1218`
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:586-595,613-626`
- Modify: `src/EyeCtrl.App/Overlays/EmojiGridManager.cs:351-365`
- Test: `tests/EyeCtrl.App.Tests/Controls/DwellHighlightTests.cs` (new)

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Controls;

using System.Windows.Media;
using EyeCtrl.App.Controls;
using Xunit;

public class DwellHighlightTests
{
    [Fact]
    public void Toward_ZeroProgress_ReturnsBaseColor()
    {
        var baseColor = Color.FromArgb(200, 50, 50, 50);
        Check.That(DwellHighlight.Toward(baseColor, 0)).IsEqualTo(baseColor);
    }

    [Fact]
    public void Toward_FullProgress_ReturnsFullGreenHighlight()
    {
        var baseColor = Color.FromArgb(200, 50, 50, 50);
        // At progress 1 the blend lands fully on Argb(200, 0, 255, 0).
        Check.That(DwellHighlight.Toward(baseColor, 1)).IsEqualTo(Color.FromArgb(200, 0, 255, 0));
    }

    [Fact]
    public void Toward_HalfProgress_MatchesLegacyChannelMath()
    {
        var baseColor = Color.FromArgb(200, 50, 70, 110);
        // Legacy formula from KeyboardWindow.UpdateKeyVisual / EmojiGridManager.OnCategoryProgress:
        // highlight = Argb(A, 0, 100 + 155*p, 0); blended = base*(1-p) + highlight*p, per channel.
        var expected = Color.FromArgb(200,
            (byte)(50 * 0.5 + 0 * 0.5),
            (byte)(70 * 0.5 + (100 + 155 * 0.5) * 0.5),
            (byte)(110 * 0.5 + 0 * 0.5));
        Check.That(DwellHighlight.Toward(baseColor, 0.5)).IsEqualTo(expected);
    }

    [Fact]
    public void Toward_PreservesBaseAlpha()
    {
        var baseColor = Color.FromArgb(170, 40, 80, 40); // confidence-shaded prediction color
        Check.That(DwellHighlight.Toward(baseColor, 0.7).A).IsEqualTo((byte)170);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~DwellHighlightTests"
```
Expected: compile error — `DwellHighlight` does not exist.

- [ ] **Step 3: Implement**

1. Add to `src/EyeCtrl.App/Controls/ColorBlend.cs` (same file, below ColorBlend):

```csharp
/// <summary>
/// The app's standard "dwell progress" tint: blends a base color toward an increasingly
/// bright green as progress approaches 1. Extracted from three identical copies in
/// KeyboardWindow (keys + predictions) and EmojiGridManager (category tabs).
/// </summary>
public static class DwellHighlight
{
    /// <summary>Returns <paramref name="baseColor"/> tinted toward green by <paramref name="progress"/> (0..1). Alpha is preserved.</summary>
    public static Color Toward(Color baseColor, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var highlight = Color.FromArgb(baseColor.A, 0, (byte)(100 + 155 * progress), 0);
        return ColorBlend.Blend(baseColor, highlight, progress);
    }
}
```

(Verify equivalence: `ColorBlend.Blend(base, highlight, p)` computes per-channel `base*(1-p) + highlight*p` with alpha `base.A*(1-p) + base.A*p = base.A` — identical to the three inline copies, including the prediction-bar variant that used `baseColor.A` as the highlight alpha.)

2. `KeyboardWindow.OnPredictionProgress` (lines 586-595): replace the `highlightColor` + `Color.FromArgb(...)` blend with:

```csharp
        // Blend highlight on top of the stored base color to preserve confidence shading
        var baseColor = _predictionBaseColors[index];
        _predictionBrushes[index].Color = DwellHighlight.Toward(baseColor, progress);
```

3. `KeyboardWindow.UpdateKeyVisual` (lines 613-626): replace body after the lookup with:

```csharp
        var baseColor = GetKeyBaseColor(targetId);
        visuals.Container.Background = new SolidColorBrush(DwellHighlight.Toward(baseColor, progress));
```

4. `EmojiGridManager.OnCategoryProgress` (lines 351-365): replace the `if (progress <= 0) ... else ...` color computation with:

```csharp
            var isActive = i == GetActiveCategoryIndex();
            var baseColor = isActive ? CategoryBarActiveColor : CategoryBarColor;
            _categoryBrushes[i].Color = DwellHighlight.Toward(baseColor, progress);
```

(`Toward(base, 0)` returns base, so the `progress <= 0` branch folds in.) Add `using EyeCtrl.App.Controls;` to both files.

5. `SettingsWindow.xaml.cs`: delete the private `Blend` (lines 1210-1218) and change its four call sites (1141, 1147, 1158, 1170) from `Blend(...)` to `ColorBlend.Blend(...)` — `using EyeCtrl.App.Controls;` already present (line 13).

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~DwellHighlightTests|FullyQualifiedName~KeyboardTests"
```

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Controls/ColorBlend.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs src/EyeCtrl.App/Overlays/EmojiGridManager.cs tests/EyeCtrl.App.Tests/Controls/DwellHighlightTests.cs
git commit -m "refactor: shared ColorBlend + DwellHighlight replace 4 inline color-blend copies"
```

---

### Task B15: Extract ScrollModeController from EventCoordinator

Finding 12. The scroll-mode block (`EventCoordinator.cs:1042-1130`: `OnInteractionStateChanged`, `OnScrollModeEntered/Exited`, the three event-pair handlers, plus the 4 fields `_scrollOverlayVm/_scrollOverlayWindow/_scrollWorker/_scrollModeActive`) moves into `src/EyeCtrl.App/Services/ScrollModeController.cs`. **Phase D contract:** construct the VM as `new ScrollOverlayViewModel(messenger, settings.Scroll, settings.Dwell)` — the stop-button dwell time now comes from `DwellSettings.ScrollStopButtonDwellMs`. (Current main still has `ScrollOverlayViewModel(ScrollSettings, IMessenger)` — if the Phase-D ctor is absent, STOP: phase ordering violated.)

**Files:**
- Create: `src/EyeCtrl.App/Services/ScrollModeController.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:50-53,1042-1130,Dispose`
- Test: `tests/EyeCtrl.App.Tests/Services/ScrollModeControllerTests.cs` (new — lifecycle contract only; Enter creates a WPF window and is manual-verified)

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using Xunit;

public class ScrollModeControllerTests
{
    [Fact]
    public void Exit_WithoutEnter_DoesNotThrow()
    {
        var controller = new ScrollModeController();
        controller.Exit();          // must be a safe no-op
        Check.That(controller.IsActive).IsFalse();
    }

    [Fact]
    public void Dispose_Idempotent_AndExitsIfActiveFlagCleared()
    {
        var controller = new ScrollModeController();
        controller.Dispose();
        controller.Dispose();       // must not throw
        Check.That(controller.IsActive).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ScrollModeControllerTests"
```
Expected: compile error — `ScrollModeController` does not exist.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.App/Services/ScrollModeController.cs` — the bodies are the current EventCoordinator code moved, with `_context` reads replaced by the `Initialize`-supplied context and the Phase-D VM ctor:

```csharp
namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// Owns the scroll-mode overlay lifecycle: ScrollOverlayViewModel + ScrollOverlayWindow +
/// ScrollWorker, and the three event pairs between them. Extracted from EventCoordinator
/// (which retains only the InteractionState.ScrollMode transition wiring).
/// </summary>
public sealed class ScrollModeController : IDisposable
{
    private EventCoordinationContext? _context;
    private ScrollOverlayViewModel? _vm;
    private ScrollOverlayWindow? _window;
    private ScrollWorker? _worker;
    private bool _active;
    private bool _disposed;

    public bool IsActive => _active;

    public void Initialize(EventCoordinationContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public void Enter()
    {
        if (_context == null || _active) return;

        var anchor = _context.InteractionManager.ScrollAnchor!.Value;

        _context.InputSimulator.MoveCursor(anchor);

        // Hide the gaze-following cursor circle so it doesn't compete with the scroll overlay's
        // anchor-locked circle (the locked circle is the whole point of Option D — no feedback loop).
        _context.OverlayManager.CursorCircle.Hide();

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var stopButtonCenter = new PhysicalPoint(
            bounds.Width - DpiHelper.LogicalToPhysicalX(50),
            bounds.Height / 2.0);

        // Phase-D ctor: stop-button dwell time comes from DwellSettings.ScrollStopButtonDwellMs.
        _vm = new ScrollOverlayViewModel(_context.Messenger, _context.Settings.Scroll, _context.Settings.Dwell);
        _vm.EnterScrollMode(anchor, stopButtonCenter);
        _vm.ExitRequested += OnExitRequested;

        _window = new ScrollOverlayWindow();
        _window.Show(_vm);

        _worker = new ScrollWorker(_context.InputSimulator);
        _worker.Start(anchor);

        _vm.TargetVelocityChanged += OnTargetVelocityChanged;
        _vm.FeedbackChanged += OnFeedbackChanged;

        _active = true;
    }

    // Defer the exit off the current call stack: ExitRequested fires synchronously from inside
    // ScrollOverlayViewModel.Receive (via the dwell detector), and tearing the VM down there would
    // dispose it mid-Receive. BeginInvoke runs the teardown on a fresh dispatcher turn instead.
    private void OnExitRequested()
        => _context?.Dispatcher.BeginInvoke(() => _context?.InteractionManager.ExitScrollMode());

    private void OnTargetVelocityChanged(double vx, double vy)
        => _worker?.UpdateTargetVelocity(vx, vy);

    private void OnFeedbackChanged()
    {
        if (_window == null || _vm == null) return;
        _window.UpdateFeedback(
            _vm.AnchorDotOffsetX,
            _vm.AnchorDotOffsetY,
            _vm.VelocityMagnitude,
            _vm.StopButtonProgress);
    }

    public void Exit()
    {
        _worker?.Stop();
        _worker?.Dispose();
        _worker = null;

        if (_vm != null)
        {
            _vm.TargetVelocityChanged -= OnTargetVelocityChanged;
            _vm.FeedbackChanged -= OnFeedbackChanged;
            _vm.ExitRequested -= OnExitRequested;
            _vm.ExitScrollMode();
            _vm.Dispose();
            _vm = null;
        }

        _window?.Close();
        _window = null;

        // Restore the gaze-following cursor circle (matches the calibration/settings restore pattern).
        if (_context != null && _context.CursorVM.IsVisible)
            _context.OverlayManager.CursorCircle.Show();

        _active = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_active) Exit();
    }
}
```

In `EventCoordinator.cs`:
- Delete fields `_scrollOverlayVm`, `_scrollOverlayWindow`, `_scrollWorker`, `_scrollModeActive` (lines 50-53); add `private readonly ScrollModeController _scrollMode = new();`.
- In `Initialize(...)` after `SetupDragHandlers();`: `_scrollMode.Initialize(context);`.
- Replace `OnInteractionStateChanged` (lines 1042-1052) with:

```csharp
    private void OnInteractionStateChanged(InteractionState state)
    {
        if (state == InteractionState.ScrollMode)
            _scrollMode.Enter();
        else if (_scrollMode.IsActive)
            _scrollMode.Exit();
    }
```

- Delete `OnScrollModeEntered`, `OnScrollExitRequested`, `OnScrollTargetVelocityChanged`, `OnScrollFeedbackChanged`, `OnScrollModeExited` (lines 1054-1130).
- In `Dispose()` replace `if (_scrollModeActive) OnScrollModeExited();` with `_scrollMode.Dispose();`.

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ScrollModeControllerTests|FullyQualifiedName~EventCoordinatorTests"
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~InteractionManagerScrollMode"
```
**Manual verification:** `--test-mode` → action menu → ⇅ → scroll overlay appears anchored, moving gaze scrolls, stop button exits, cursor circle returns.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Services/ScrollModeController.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/ScrollModeControllerTests.cs
git commit -m "refactor: extract ScrollModeController from EventCoordinator (uses Phase-D ScrollOverlayViewModel ctor)"
```

---

### Task B16: SettingsWindow — single source of pagination/tab state (the ViewModel)

Finding 13. `SettingsWindow` keeps `_activeTab`, `_currentPage`, `_totalPages` fields (lines 67-69) hand-synced with `SettingsViewModel.CurrentTabName/CurrentPage/TotalPages` in `OnSettingsViewModelPropertyChanged` (lines 171-197). Verified: `_totalPages` (written at line 284) is never read by rendering — `RenderPagination` (lines 303-332) reads the VM values — it only feeds a clamp of `_currentPage`. Remove the three fields; the window keeps only `_pages` (the WPF page-builder lists, which are view construction, not state).

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:67-69,171-197,277-301,326-327,356-362,374-379`
- Test: existing `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs` (UI category — verify headed if possible); primary check is build + the VM-level pagination tests already in `tests/EyeCtrl.ViewModels.Tests`

- [ ] **Step 1: Write the failing test** — Not applicable: this deletes duplicated view-side state inside a WPF window; the surviving source of truth (`SettingsViewModel.ShowTab/NextPage/PreviousPage/UpdatePageItems`) is already covered in ViewModels.Tests. Baseline those green first.

- [ ] **Step 2: Baseline**
```
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~SettingsViewModel"
```

- [ ] **Step 3: Implement**

1. Delete the fields (lines 67-69):
```csharp
    private string _activeTab = "";
    private int _currentPage;
    private int _totalPages;
```
(keep `private List<List<Action>> _pages = new();`).

2. `OnSettingsViewModelPropertyChanged` (lines 171-197): remove the mirror writes; current handler compares/assigns `_activeTab` and `_currentPage`. New version:

```csharp
    private void OnSettingsViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.CurrentTabName):
                    foreach (var (id, border) in _tabButtons)
                        border.Background = new SolidColorBrush(
                            id == _viewModel.Settings.CurrentTabName ? TabActive : TabInactive);
                    RebuildContent();
                    break;
                case nameof(SettingsViewModel.CurrentPage):
                    RenderCurrentPage();
                    RenderPagination();
                    break;
                case nameof(SettingsViewModel.TotalPages):
                    RenderPagination();
                    break;
            }
        });
    }
```

Note the deliberate behavior change: the old `if (_viewModel.Settings.CurrentTabName != _activeTab)` guard suppressed rebuilds when the same tab was re-selected; `SettingsViewModel.ShowTab` always raises `CurrentTabName` only when the value actually changes (ObservableProperty setter no-ops on equal values), so the guard was redundant — CurrentTabName PropertyChanged simply won't fire for a same-tab re-click. Verify this by reading `SettingsViewModel`'s `[ObservableProperty] CurrentTabName` (CommunityToolkit setters skip equal assignments). If `ShowTab` is found to force-raise regardless, keep a local `string _lastRenderedTab` guard instead — but do not mirror page state.

3. `RebuildContent` (lines 277-288): replace `_totalPages`/`_currentPage` usage:

```csharp
    private void RebuildContent()
    {
        _viewModel.ClearCategory(DwellCategory.Control);
        ContentPanel.Children.Clear();

        _pages = BuildPages();

        RenderCurrentPage();
        RenderPagination();
    }
```

4. `RenderCurrentPage` (lines 290-301): read the VM page, clamped against the window's actual built pages:

```csharp
    private void RenderCurrentPage()
    {
        _viewModel.ClearCategory(DwellCategory.Control);
        ContentPanel.Children.Clear();
        _activeItems.Clear();

        var page = Math.Clamp(_viewModel.Settings.CurrentPage, 0, Math.Max(0, _pages.Count - 1));
        if (page < _pages.Count)
        {
            foreach (var builder in _pages[page])
                builder();
        }
    }
```

5. `BuildPages` (lines 374-379): `_activeTab` → `_viewModel.Settings.CurrentTabName`:

```csharp
    private List<List<Action>> BuildPages()
    {
        return _pageBuilders.TryGetValue(_viewModel.Settings.CurrentTabName, out var pageBuilder)
            ? pageBuilder.Invoke()
            : new();
    }
```

6. `RenderPagination`/`MakePagButton` already read/drive VM state (lines 309-327, 356-362) — no change needed; confirm no other `_activeTab`/`_currentPage`/`_totalPages` reference survives (`grep -n "_activeTab\|_currentPage\|_totalPages" src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` must return nothing).

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~SettingsViewModel"
```
**Manual verification:** open Settings; switch tabs (highlight + content follow); page ◀/▶ through Dwell (2 pages) and Models; page counter matches; switch tab then back — page resets to 0 (VM's ShowTab behavior, unchanged).

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "refactor: SettingsWindow reads tab/page state from SettingsViewModel only (remove mirrored fields)"
```

---

### Task B17: ManualSelectionDwellService cleanup + PascalCase field/local renames

Finding 14. Verified in `src/EyeCtrl.App/Services/ManualSelectionDwellService.cs`: (a) `OnDwellActivated` (163-193) fetches `GetProgress` into an unused local `target` and carries 3 stale comments ("We need to get the actual target...", "We don't have access to the exact point..." — `_lastGazePoint` HAS been the mechanism since it was added); (b) `InvokeOnDispatcherIfNeeded` (152-161) always takes the `CheckAccess()` branch because every input arrives via `GazeCursorMovedMessage`, which `GazeEngineAdapter.OnCursorMoved` (line 98-99) marshals to the UI thread with `BeginInvoke` before sending — replace with direct calls; (c) `_lastGazePoint` is declared mid-file at line 213 — move to the top; (d) PascalCase private fields `_ManualSelectionCenter/_ManualSelectionRadius/_ManualSelectionStartTimestamp` → camelCase, and the same style fix for EventCoordinator locals (lines 89-91: unused — deleted in B18; lines 125-126 and 556-559: used, rename).

**Files:**
- Modify: `src/EyeCtrl.App/Services/ManualSelectionDwellService.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:125-126,137-138,153-155,556-559,561-580` (locals only)
- Test: existing `tests/EyeCtrl.App.Tests/Services/ManualSelectionDwellServiceTests.cs` must stay green

- [ ] **Step 1: Write the failing test** — Not applicable (behavior-preserving cleanup). Baseline the existing suite instead.

- [ ] **Step 2: Baseline**
```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ManualSelectionDwellServiceTests"
```
Record the pass count — it must be identical after.

- [ ] **Step 3: Implement**

1. Field renames (use an IDE/`gitnexus rename`, NOT find-and-replace, per CLAUDE.md): `_ManualSelectionCenter` → `_manualSelectionCenter`, `_ManualSelectionRadius` → `_manualSelectionRadius`, `_ManualSelectionStartTimestamp` → `_manualSelectionStartTimestamp`. Move `private PhysicalPoint? _lastGazePoint;` from line 213 up to the State block (after `_isActive`).

2. Delete `InvokeOnDispatcherIfNeeded` (lines 152-161) and inline direct calls. Resulting handlers:

```csharp
    private void OnDwellActivated(string targetId)
    {
        if (targetId != ManualSelectionDwellTargetId) return;
        if (_dwellDetector == null) return;

        try
        {
            // Gaze events are already marshaled to the UI thread by GazeEngineAdapter
            // (GazeCursorMovedMessage is sent via Dispatcher.BeginInvoke), so no re-dispatch here.
            _logger.LogInformation("ManualSelection dwell completed");
            _messenger.Send(new ResetManualSelectionIndicatorMessage());
            // The dwell target follows the gaze, so the last gaze point IS the precise fixation.
            _messenger.Send(new ManualSelectionDwellCompletedMessage(_lastGazePoint ?? _manualSelectionCenter!.Value));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ManualSelection dwell activated handler");
        }
    }

    private void OnDwellProgress(string targetId, double progress)
    {
        if (targetId != ManualSelectionDwellTargetId) return;
        try
        {
            _messenger.Send(new UpdateManualSelectionIndicatorProgressMessage(progress));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ManualSelection dwell progress handler");
        }
    }
```

Similarly flatten `ResetDwell`, `UpdateIndicatorPosition`, `UpdateIndicatorProgress` to direct `_messenger.Send(...)` in try/catch (delete `UpdateIndicatorProgress` entirely if `OnDwellProgress` now sends directly; `UpdateIndicatorPosition` keeps its try/catch body sans dispatcher). The `Dispatcher _dispatcher` ctor parameter/field becomes unused — KEEP the ctor parameter (AppBootstrapper wires it at line 169-174) but delete the field, or keep field with a `// retained for DI signature stability` comment; prefer: remove field, keep parameter, discard with `_ = dispatcher;`. Simplest honest form: remove the parameter AND update the single AppBootstrapper registration (lines 169-174) to drop `dispatcher` — do that.

3. EventCoordinator local renames (no behavior change): lines 125-126 `var ManualSelectionOverlay =` → `var manualSelectionOverlay =`, `var ManualSelectionIndicator =` → `var manualSelectionIndicator =` (update uses at 137-138, 153-155); lines 556-559 in `OnManualSelectionVMPropertyChanged`: `ManualSelectionOverlay/ManualSelectionIndicator` locals → camelCase (update uses through line 580); line 609 in `OnManualSelectionSideButtonsPropertyChanged`: `var ManualSelectionSideButtons` → `var manualSelectionSideButtons`; line 588 in `OnManualSelectionIndicatorVMPropertyChanged`: same.

- [ ] **Step 4: Run tests to verify pass**

```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ManualSelectionDwellServiceTests"
```
Same pass count as Step 2. Run `detect_changes()` — affected symbols must be limited to ManualSelectionDwellService members, the renamed locals' containing methods, and the AppBootstrapper registration.

- [ ] **Step 5: Commit**

```
git add src/EyeCtrl.App/Services/ManualSelectionDwellService.cs src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.App/Services/AppBootstrapper.cs
git commit -m "refactor: ManualSelectionDwellService drop always-true dispatcher indirection, stale comments, unused GetProgress fetch; camelCase renames"
```

---

### Task B18: Dead-code batch

Finding 15, all verified against current source:
- `SettingsWindow.BuildThemePages` (703-738): not in `_tabDefs` (lines 99-111 list 10 tabs, no "Theme") — unreachable. Delete. (Theme settings remain reachable via ResetAllSettings/persistence; only the never-shown page builder dies.)
- `AppBootstrapper.cs:142` `services.AddSingleton<SettingsViewModel>();` — nothing resolves `SettingsViewModel` from DI (EventCoordinator constructs it manually at `EventCoordinator.cs:696-699`). Delete the registration.
- `KeyboardWindow.SetPredictor` (line 126) — grep-verified zero callers (`SetPredictor` appears only at its declaration and `KeyboardViewModel.SetPredictor:139`). Delete the window method; check `KeyboardViewModel.SetPredictor` for remaining callers afterwards — if it too is orphaned, flag it for the ViewModels phase rather than deleting cross-phase.
- `EventCoordinator.cs:87-93`: the seven locals in `SetupPropertyChangedHandlers` (`cursorCircle`, `activationButton`, `ManualSelectionOverlay`, `ManualSelectionSideButtons`, `ManualSelectionIndicator`, `actionMenu`, `toolbar`) are never used in that method (each handler re-fetches from `_context.OverlayManager`). Delete all seven declarations.
- `App.xaml.cs:189` `_eventCoordinator?.WordPredictor?.SaveUserDictionary();` — always a no-op: it runs AFTER `_eventCoordinator?.Dispose()` (line 179), and Dispose (B1: `_predictionFactory.Dispose()`) already saved and nulled the stack, so `WordPredictor` is null here. Delete the line and add at the Dispose call site: `// EventCoordinator.Dispose (via PredictionStackFactory) saves the user dictionary.`
- `EventCoordinator.cs:1160-1164` in `Dispose`: `if (_context != null) { ... }` is redundant — line 1134 already early-returns when `_context == null`. Unwrap the block (keep the two unsubscribes).

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:703-738`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs:142`
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:126`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:87-93,1160-1164`
- Modify: `src/EyeCtrl.App/App.xaml.cs:179,189`

- [ ] **Step 1: Write the failing test** — Not applicable (pure deletions of unreachable/no-op code). Safety net: full build + full non-UI App test run.
- [ ] **Step 2: Verify deadness** — re-run the greps before deleting:
```
rg -n "BuildThemePages|SetPredictor|GetRequiredService<SettingsViewModel>" src tests
```
Expected: `BuildThemePages` only at its declaration; `SetPredictor` only at KeyboardWindow:126 + KeyboardViewModel:139; no `GetRequiredService<SettingsViewModel>` anywhere. If any new caller appeared since this plan was written, drop that deletion.
- [ ] **Step 3: Implement** — apply the six deletions listed above.
- [ ] **Step 4: Run tests to verify pass**
```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```
- [ ] **Step 5: Commit**
```
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "chore: remove dead code (BuildThemePages, unused DI registration, SetPredictor, unused locals, post-Dispose no-op save, redundant null check)"
```

---

### Task B19: Gaze routing — early-out on the gaze thread when no consumer needs per-sample dispatch

Finding 16. `EventCoordinator.SetupGazeRouting` (lines 345-365) posts a dispatcher closure for EVERY gaze sample (≈30-60/s) unconditionally. Verified consumers: `ToolbarWindow.UpdateGaze` (174-178) does nothing once `_targetsInitialized` is true (and `SetMode` resets it, line 171); the keyboard branch is gated on `_keyboardVisible && keyboard.IsVisible`; the settings branch on `_settingsWindow?.IsVisible`. So when the toolbar is initialized and neither keyboard nor settings is open — the overwhelmingly common state — every post is wasted UI-thread work.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (expose the flag)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:345-365`
- Test: existing `ToolbarTests` stay green; behavior is a perf gate (manual/log verification)

- [ ] **Step 1: Write the failing test** — Not applicable (perf-only guard around unchanged behavior; the gate members are bools read cross-thread). Baseline `ToolbarTests` green.
- [ ] **Step 2: Baseline**
```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ToolbarTests"
```
- [ ] **Step 3: Implement**

1. `ToolbarWindow.xaml.cs`: next to `UpdateGaze` add:

```csharp
    /// <summary>
    /// True while UpdateGaze still has work to do (button screen positions not yet captured).
    /// Read from the gaze thread by EventCoordinator to skip needless dispatcher posts; a stale
    /// read only costs/saves one frame, so no synchronization is needed for this bool.
    /// </summary>
    public bool NeedsGazeUpdates => !_targetsInitialized;
```

2. `EventCoordinator.SetupGazeRouting` — replace the handler (current code posts unconditionally):

```csharp
        _gazeMovedHandler = (point, timestamp) =>
        {
            // Runs on the gaze thread. Skip the per-sample dispatcher post when nobody needs it:
            // the toolbar only consumes gaze until its targets are initialized, and the
            // keyboard/settings windows only while open. _keyboardVisible/_settingsWindow are
            // written on the UI thread; a torn/stale read here costs at most one frame.
            if (!toolbar.NeedsGazeUpdates && !_keyboardVisible && _settingsWindow == null)
                return;

            _context.Dispatcher.BeginInvoke(() =>
            {
                toolbar.UpdateGaze(point, timestamp);

                if (_keyboardVisible && keyboard.IsVisible)
                    keyboard.UpdateGaze(point, timestamp);

                if (_settingsWindow?.IsVisible ?? false)
                    _settingsWindow.UpdateGaze(point, timestamp);
            });
        };
```

- [ ] **Step 4: Run tests to verify pass**
```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ToolbarTests"
```
**Manual verification:** `--test-mode`: toolbar dwell still works immediately after startup AND after toggling dock mode in settings (SetMode resets `_targetsInitialized`, so `NeedsGazeUpdates` flips true again — confirm hover-dwell on toolbar buttons still fills after changing DockedEdge); keyboard keys and settings controls still react to gaze.
- [ ] **Step 5: Commit**
```
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "perf: skip per-gaze-sample dispatcher posts when toolbar is initialized and no keyboard/settings window is open"
```

---

### Task B20: Unify keyboard visibility through one helper + make KeyboardViewModel.IsVisible truthful

Finding 17. Two independent show/hide paths exist in EventCoordinator: `KeyboardToggledMessage` (183-199: dispatcher + `SuppressFixation` + `OnFixationEnded`) and `ShowKeyboardRequestMessage`/`HideKeyboardRequestMessage` (295-310: neither dispatch nor suppression). Neither sends `ShowKeyboardMessage`/`HideKeyboardMessage`, which `KeyboardViewModel` handles (`KeyboardViewModel.cs:62-63`, verified: `Receive(ShowKeyboardMessage) => IsVisible = true`) — so `KeyboardViewModel.IsVisible` is currently never updated by the real show/hide flow.

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:183-199,294-310`
- Test: `tests/EyeCtrl.App.Tests/Services/EventCoordinatorKeyboardVisibilityTests.cs` — NOT feasible without full context init (EventCoordinator needs OverlayManager windows); instead assert the VM contract in ViewModels.Tests if not already covered, and manual-verify the App wiring

- [ ] **Step 1: Write the failing test**

In `tests/EyeCtrl.ViewModels.Tests` (create `KeyboardViewModelVisibilityTests.cs` if the existing KeyboardViewModelTests file lacks it — check first):

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

public class KeyboardViewModelVisibilityTests
{
    [Fact]
    public void ShowAndHideKeyboardMessages_DriveIsVisible()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(messenger);   // match the real ctor — read KeyboardViewModel.cs before writing

        messenger.Send(new ShowKeyboardMessage());
        Check.That(vm.IsVisible).IsTrue();

        messenger.Send(new HideKeyboardMessage());
        Check.That(vm.IsVisible).IsFalse();
    }
}
```

(This should pass already at VM level — it pins the contract the App wiring below starts using. The genuinely new behavior — EventCoordinator actually SENDING these messages — is WPF-window-coupled and manual-verified.)

- [ ] **Step 2: Run test**
```
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~KeyboardViewModelVisibilityTests"
```

- [ ] **Step 3: Implement**

Add to EventCoordinator:

```csharp
    /// <summary>
    /// Single show/hide path for the keyboard overlay. Sends Show/HideKeyboardMessage so
    /// KeyboardViewModel.IsVisible stays truthful (it previously was never updated).
    /// <paramref name="manageSuppression"/>: the toggle path (toolbar ⌨ button) owns
    /// SuppressFixation; the request path (text-field detection) does not.
    /// </summary>
    private void SetKeyboardVisible(bool visible, bool manageSuppression)
    {
        if (_context == null) return;
        var keyboard = _context.OverlayManager.Keyboard;

        _keyboardVisible = visible;
        Log.Information("Keyboard {State}", visible ? "shown" : "hidden");

        if (visible)
        {
            keyboard.Show();
            _context.Messenger.Send(new ShowKeyboardMessage());
            if (manageSuppression)
            {
                _context.GazeEngine.SuppressFixation = true;
                _context.InteractionManager.OnFixationEnded();
            }
        }
        else
        {
            keyboard.Hide();
            _context.Messenger.Send(new HideKeyboardMessage());
            if (manageSuppression)
                _context.GazeEngine.SuppressFixation = false;
        }
    }
```

Replace the `KeyboardToggledMessage` handler body (183-199) with:

```csharp
        _context.Messenger.Register<EventCoordinator, KeyboardToggledMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(!_keyboardVisible, manageSuppression: true)));
```

Replace the two request handlers (294-310) with (note: they gain the dispatcher marshaling the toggle path always had — these messages can originate off the UI thread):

```csharp
        // ShowKeyboardRequestMessage -> show keyboard for text input
        _context.Messenger.Register<EventCoordinator, ShowKeyboardRequestMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                SetKeyboardVisible(true, manageSuppression: false);
                Log.Debug("Keyboard shown for text input (target handle: {Handle})", msg.TargetHandle);
            }));

        // HideKeyboardRequestMessage -> hide keyboard
        _context.Messenger.Register<EventCoordinator, HideKeyboardRequestMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(false, manageSuppression: false)));
```

(`im.OnFixationEnded()` from the old toggle handler is preserved via `_context.InteractionManager` inside the helper — same object, see `EventCoordinationContext.InteractionManager`.)

- [ ] **Step 4: Run tests to verify pass**
```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --filter "FullyQualifiedName~KeyboardViewModel"
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~KeyboardTests"
```
**Manual verification:** toolbar ⌨ toggles keyboard (fixation suppressed while open); typing into a note still auto-shows/hides the keyboard without pausing fixation elsewhere.
- [ ] **Step 5: Commit**
```
git add src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.ViewModels.Tests/
git commit -m "refactor: single SetKeyboardVisible path; send Show/HideKeyboardMessage so KeyboardViewModel.IsVisible is truthful"
```

---

### Task B21: ActionMenuWindow.Show — cancellable delayed capture + single clamp helper

Finding 18. Verified in `ActionMenuWindow.xaml.cs`: (a) the delayed screen capture (`Dispatcher.InvokeAsync` + `Task.Delay(ScreenCaptureDelayMs)` at lines 116-133) is un-cancellable — rapid hide/re-show stacks captures that then insert stale preview ellipses into the CURRENT canvas; (b) the two indicator-offset clamp blocks (82-98 zoom path, 99-114 gaze-offset path) are near-identical (same scale formula, same clamp). Capture-before-visible is NOT feasible here: the 100 ms delay exists to let the PREVIOUS overlays (manual-selection zoom) finish hiding so they don't photobomb the capture — keep the delay, make it cancellable, and guard the insert.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs:34,63-133` (+ Hide override)
- Test: geometry helper unit-testable; add `tests/EyeCtrl.App.Tests/Overlays/ActionMenuIndicatorOffsetTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using Xunit;

public class ActionMenuIndicatorOffsetTests
{
    [Fact]
    public void ComputeIndicatorOffset_InsideCircle_IsUnclamped()
    {
        var (dx, dy) = ActionMenuWindow.ComputeIndicatorOffset(
            pointX: 510, pointY: 500, refCenterX: 500, refCenterY: 500,
            previewCircleSize: 200, captureRadiusPhysical: 100);
        // scale = 200 / (2*100) = 1.0
        Check.That(dx).IsCloseTo(10, 0.001);
        Check.That(dy).IsCloseTo(0, 0.001);
    }

    [Fact]
    public void ComputeIndicatorOffset_OutsideCircle_ClampsToRadiusMinusMargin()
    {
        var (dx, dy) = ActionMenuWindow.ComputeIndicatorOffset(
            pointX: 900, pointY: 500, refCenterX: 500, refCenterY: 500,
            previewCircleSize: 200, captureRadiusPhysical: 100);
        // raw offset 400 clamps to previewCircleSize/2 - 10 = 90, along +X.
        Check.That(dx).IsCloseTo(90, 0.001);
        Check.That(dy).IsCloseTo(0, 0.001);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ActionMenuIndicatorOffsetTests"
```
Expected: compile error — `ComputeIndicatorOffset` does not exist.

- [ ] **Step 3: Implement**

1. Add the pure helper to ActionMenuWindow (public static for the test; the body is the shared logic of both current blocks):

```csharp
    /// <summary>
    /// Computes the indicator's logical-canvas offset from the preview center for a physical
    /// point relative to a physical reference center, scaled into preview-circle space and
    /// clamped to keep the indicator inside the circle (IndicatorOffsetMargin px margin).
    /// Shared by the zoom path (ref = zoomCenter) and the at-center path (ref = captureCenter).
    /// </summary>
    public static (double X, double Y) ComputeIndicatorOffset(
        double pointX, double pointY, double refCenterX, double refCenterY,
        double previewCircleSize, double captureRadiusPhysical)
    {
        // scale = logical preview px per physical capture px
        double scale = previewCircleSize / (2.0 * captureRadiusPhysical);
        double dx = (pointX - refCenterX) * scale;
        double dy = (pointY - refCenterY) * scale;

        double mag = Math.Sqrt(dx * dx + dy * dy);
        double max = previewCircleSize / 2 - IndicatorOffsetMargin;
        if (mag > max && mag > 0)
        {
            dx = dx / mag * max;
            dy = dy / mag * max;
        }
        return (dx, dy);
    }
```

2. Replace both blocks (82-98 and 99-114) with:

```csharp
        double indicatorOffsetX = 0;
        double indicatorOffsetY = 0;
        var indicatorPoint = gazePoint ?? center;
        if (zoomCenter != null && zoomRadius != null)
        {
            (indicatorOffsetX, indicatorOffsetY) = ComputeIndicatorOffset(
                center.X, center.Y, zoomCenter.Value.X, zoomCenter.Value.Y,
                previewCircleSize, captureRadius);
        }
        else if (gazePoint != null && gazePoint != center)
        {
            (indicatorOffsetX, indicatorOffsetY) = ComputeIndicatorOffset(
                indicatorPoint.X, indicatorPoint.Y, captureCenter.X, captureCenter.Y,
                previewCircleSize, captureRadius);
        }
```

3. Cancellable capture — add a field `private CancellationTokenSource? _captureCts;` and replace the fire-and-forget block (116-133) with:

```csharp
        _captureCts?.Cancel();
        _captureCts = new CancellationTokenSource();
        var captureToken = _captureCts.Token;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // Delay lets the previous overlays (zoom circle etc.) finish hiding first.
                await Task.Delay(ScreenCaptureDelayMs, captureToken);
            }
            catch (TaskCanceledException) { return; }
            if (captureToken.IsCancellationRequested) return; // hidden or re-shown since

            var capturedImage = ScreenCaptureService.CaptureRegion(captureCenter, captureRadius);
            if (capturedImage == null || captureToken.IsCancellationRequested) return;

            var previewEllipse = new Ellipse
            {
                Width = captureCircleSize,
                Height = captureCircleSize,
                Fill = new ImageBrush(capturedImage) { Stretch = Stretch.Fill },
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(captureBorderColor)),
                StrokeThickness = PreviewCircleBorderThickness
            };
            Canvas.SetLeft(previewEllipse, captureCanvasCenter.X - captureCircleSize / 2);
            Canvas.SetTop(previewEllipse, captureCanvasCenter.Y - captureCircleSize / 2);
            MenuCanvas.Children.Insert(0, previewEllipse); // Insert at back so buttons stay on top
        });
```

4. Cancel on hide — ActionMenuWindow has no Hide override today (EventCoordinator calls `actionMenu.Hide()`); add:

```csharp
    public new void Hide()
    {
        _captureCts?.Cancel();
        base.Hide();
    }
```

(`new` hiding is the established pattern in this codebase — see `ActivationButtonWindow.Hide`, `GlassBackdropWindow.Hide`. The `EventCoordinator` call sites reference the concrete type, so the shadowed method is the one invoked; verify no call site holds it as `Window`.)

- [ ] **Step 4: Run tests to verify pass**
```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ActionMenuIndicatorOffsetTests"
```
**Manual verification:** rapidly open/dismiss the action menu several times in a row — no stale/duplicate preview circles accumulate behind the buttons; zoom path still shows the correctly-offset red indicator.
- [ ] **Step 5: Commit**
```
git add src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs tests/EyeCtrl.App.Tests/Overlays/ActionMenuIndicatorOffsetTests.cs
git commit -m "fix: cancellable action-menu screen capture (stale previews on rapid re-show); dedupe indicator clamp math"
```

---

### Task B22: Messenger hygiene — GazeEngineAdapter.UnregisterAll + TrayIconManager IMessenger injection

Findings 19 + 20 (verified): `GazeEngineAdapter.Dispose` (217-230) unsubscribes all Core events but never calls `_messenger.UnregisterAll(this)` despite five `messenger.Register<...>(this, ...)` calls at lines 54-93; `TrayIconManager` (lines 40-41, 84-85) sends via `WeakReferenceMessenger.Default` directly instead of an injected `IMessenger` (works only because AppBootstrapper registers `.Default` as THE messenger — line 54 — but breaks testability and would silently misroute if that registration ever changed).

**Files:**
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:217-230`
- Modify: `src/EyeCtrl.App/Services/TrayIconManager.cs:27-88`, `src/EyeCtrl.App/App.xaml.cs:131`

- [ ] **Step 1: Write the failing test**

Append to a new `tests/EyeCtrl.App.Tests/Services/GazeEngineAdapterDisposeTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using System.Windows.Threading;
using Xunit;

public class GazeEngineAdapterDisposeTests
{
    [StaFact]
    public void Dispose_UnregistersAllMessageHandlers()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var engine = new GazeEngine(new StubGazeProvider(), new StubInputSimulator(), settings);
        var adapter = new GazeEngineAdapter(engine, settings, messenger,
            Dispatcher.CurrentDispatcher, NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());

        Check.That(messenger.IsRegistered<PauseToggledMessage>(adapter)).IsTrue();

        adapter.Dispose();

        Check.That(messenger.IsRegistered<PauseToggledMessage>(adapter)).IsFalse();
        Check.That(messenger.IsRegistered<ActionSelectedMessage>(adapter)).IsFalse();
        Check.That(messenger.IsRegistered<SelectionModeChangedMessage>(adapter)).IsFalse();
    }
}
```

Reuse/duplicate the `StubGazeProvider`/`StubInputSimulator` shapes from `ExternalDwellServiceTests` (they are private there — copy them locally or promote to a shared `tests/EyeCtrl.App.Tests/Fixtures/Stubs.cs` in this task; promotion preferred, updating ExternalDwellServiceTests to use the shared ones). `StubControlFinder` implements `IControlFinder` with `FindTarget => null`.

- [ ] **Step 2: Run test to verify it fails**
```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~GazeEngineAdapterDisposeTests"
```
Expected failure: `IsRegistered<PauseToggledMessage>(adapter)` still true after Dispose.

- [ ] **Step 3: Implement**

1. `GazeEngineAdapter.Dispose` — after the last event unsubscribe (line 228 `_interactionManager.ActionRequested -= OnActionRequested;`) add:

```csharp
        _messenger.UnregisterAll(this);
```

2. `TrayIconManager` — add ctor parameter and field, replace both `WeakReferenceMessenger.Default.Send(new SettingsRequestedMessage())` occurrences:

```csharp
    public TrayIconManager(InteractionManager interactionManager, IMessenger messenger,
        Dispatcher dispatcher, Action shutdownCallback)
```
with `private readonly IMessenger _messenger;` assigned in ctor, and:
```csharp
        settingsItem.Click += (_, _) => dispatcher.BeginInvoke(() =>
            _messenger.Send(new SettingsRequestedMessage()));
        ...
        _trayIcon.DoubleClick += (_, _) => dispatcher.BeginInvoke(() =>
            _messenger.Send(new SettingsRequestedMessage()));
```

3. `App.xaml.cs:131`: `_trayIconManager = new TrayIconManager(im, Dispatcher, () => Shutdown());` → `_trayIconManager = new TrayIconManager(im, messenger, Dispatcher, () => Shutdown());` (`messenger` already resolved at line 76).

- [ ] **Step 4: Run tests to verify pass**
```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~GazeEngineAdapterDisposeTests|FullyQualifiedName~ExternalDwellServiceTests"
```
- [ ] **Step 5: Commit**
```
git add src/EyeCtrl.App/Services/GazeEngineAdapter.cs src/EyeCtrl.App/Services/TrayIconManager.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.App.Tests/
git commit -m "fix: GazeEngineAdapter.Dispose unregisters messenger handlers; TrayIconManager takes injected IMessenger"
```

---

### Task B23: AppBootstrapper dedup + OverlayViewModels-based ResolveViewModels

Finding 21, verified: screen-center `PhysicalPoint` computed 3x (lines 199-201, 209-211, 217-219), models-dir path built 2x (84, 114-116), 20-line manifest try/catch inline in a DI lambda (87-105); `App.ResolveViewModels` returns a 10-tuple (202-228) while an `OverlayViewModels` record already exists (`IOverlayManager.cs:82-91`, currently 9 VMs, missing Backdrop).

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs:80-119,194-219` (+ new static loader)
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs:82-91` (add BackdropVm to the record)
- Modify: `src/EyeCtrl.App/App.xaml.cs:86-98,113-119,202-228`
- Test: `tests/EyeCtrl.App.Tests/Services/ModelManifestLoaderTests.cs` (new)

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using Xunit;

public class ModelManifestLoaderTests
{
    [Fact]
    public void LoadOrEmpty_MissingFile_ReturnsEmptyManifest()
    {
        var manifest = ModelManifestLoader.LoadOrEmpty(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json"));
        Check.That(manifest).IsNotNull();
    }

    [Fact]
    public void LoadOrEmpty_CorruptJson_ReturnsEmptyManifestInsteadOfThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), "corrupt-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ not json !!");
        try
        {
            var manifest = ModelManifestLoader.LoadOrEmpty(path);
            Check.That(manifest).IsNotNull();
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~ModelManifestLoaderTests"
```
Expected: compile error — `ModelManifestLoader` does not exist.

- [ ] **Step 3: Implement**

1. Create `src/EyeCtrl.App/Services/ModelManifestLoader.cs` containing the moved try/catch (bodies identical to AppBootstrapper lines 87-105):

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.IO;
using System.Text.Json;
using EyeCtrl.Core.Services;
using Serilog;

/// <summary>Loads the bundled model manifest, degrading to an empty manifest on any failure.</summary>
public static class ModelManifestLoader
{
    public static ModelManifest LoadOrEmpty(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return new ModelManifest();
        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<ModelManifest>(json) ?? new ModelManifest();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load bundled model manifest from {Path}; falling back to empty manifest", manifestPath);
            return new ModelManifest();
        }
    }
}
```
(Confirm `ModelManifest`'s namespace — AppBootstrapper resolves it via its existing usings; mirror them.)

2. In `AppBootstrapper.BuildServiceProvider`, hoist before the registrations (DpiHelper is initialized by `App.InitializeDpi` before this runs):

```csharp
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
        var modelsDir = Path.Combine(appDataDir, "models");
        var screenPhysical = new PhysicalPoint(
            System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
        var screenCenter = new PhysicalPoint(screenPhysical.X / 2, screenPhysical.Y / 2);
```
Then: the ModelRegistryService lambda (81-112) becomes `return new ModelRegistryService(modelsDir, ModelManifestLoader.LoadOrEmpty(manifestPath), settings, sp.GetRequiredService<ILogger<ModelRegistryService>>());`; ModelDownloadService uses `modelsDir`; CalibrationViewModel uses `screenPhysical`; ExerciseRunner/EyeCareHooks/EyeCareService use `screenCenter` (their three identical inline `new PhysicalPoint(... / 2)` computations).

3. `IOverlayManager.cs` — extend the record (additive, position LAST to minimize churn):

```csharp
public record OverlayViewModels(
    CursorCircleViewModel CursorCircle,
    ActivationButtonViewModel ActivationButton,
    ManualSelectionOverlayViewModel ManualSelectionOverlay,
    ActionMenuViewModel ActionMenu,
    ToolbarViewModel Toolbar,
    KeyboardViewModel Keyboard,
    ManualSelectionGazeIndicatorViewModel ManualSelectionIndicator,
    CalibrationViewModel Calibration,
    TargetHighlightOverlayViewModel TargetHighlight,
    GlassBackdropViewModel Backdrop);
```

4. `App.xaml.cs` — `ResolveViewModels` returns the record instead of the 10-tuple:

```csharp
    private OverlayViewModels ResolveViewModels(EyeCtrlSettings settings)
    {
        var vms = new OverlayViewModels(
            _serviceProvider.GetRequiredService<CursorCircleViewModel>(),
            _serviceProvider.GetRequiredService<ActivationButtonViewModel>(),
            _serviceProvider.GetRequiredService<ManualSelectionOverlayViewModel>(),
            _serviceProvider.GetRequiredService<ActionMenuViewModel>(),
            _serviceProvider.GetRequiredService<ToolbarViewModel>(),
            _serviceProvider.GetRequiredService<KeyboardViewModel>(),
            _serviceProvider.GetRequiredService<ManualSelectionGazeIndicatorViewModel>(),
            _serviceProvider.GetRequiredService<CalibrationViewModel>(),
            _serviceProvider.GetRequiredService<TargetHighlightOverlayViewModel>(),
            _serviceProvider.GetRequiredService<GlassBackdropViewModel>());
        vms.ManualSelectionIndicator.Configure(settings);
        return vms;
    }
```

and in `OnStartup` replace the tuple deconstruction (lines 87-88) + record re-construction (line 98) with `var viewModels = ResolveViewModels(settings);`, then use `viewModels.CursorCircle`, `viewModels.Backdrop`, etc. at the EventCoordinationContext construction (lines 115-119) and overlay init. `OverlayManager.Initialize(viewModels, ...)` keeps working (record gained a property; positional call sites constructing OverlayViewModels must add the Backdrop argument — the only one is here).

- [ ] **Step 4: Run tests to verify pass**
```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI"
```
- [ ] **Step 5: Commit**
```
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/ModelManifestLoader.cs src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.App.Tests/Services/ModelManifestLoaderTests.cs
git commit -m "refactor: dedupe AppBootstrapper screen/model-path constants, extract ModelManifestLoader, OverlayViewModels replaces 10-tuple"
```

---

### Task B24: NoteLayout shared geometry + KeyboardWindow modifier-action parse

Finding 22, verified: (a) `NoteManager.RegisterDwellTargets` (197-222) mirrors `NoteWindow.DrawButtons` (221-265 pre-B13) button geometry with "must match" comments on both sides — extract a shared static; (b) `KeyboardWindow.OnKeyDwellCompleted` (274-364) decodes modifier action strings in an if-chain AND `GetModifierFromAction` (651-662) decodes the same strings again — one parse.

**Files:**
- Create: `src/EyeCtrl.App/Services/NoteLayout.cs` (or next to LayoutConstants — put it where LayoutConstants lives; locate with `rg -n "class LayoutConstants" src`)
- Modify: `src/EyeCtrl.App/Services/NoteManager.cs:197-222`, `src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs` (DrawButtons center computation)
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:274-364,651-662`
- Test: `tests/EyeCtrl.App.Tests/Services/NoteLayoutTests.cs` (new)

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core;
using Xunit;

public class NoteLayoutTests
{
    [Fact]
    public void ButtonCenter_MatchesDrawButtonsFormula()
    {
        // Window-relative logical coords; index i lays buttons top-down on the right edge.
        for (int i = 0; i < 3; i++)
        {
            var p = NoteLayout.ButtonCenter(i);
            Check.That(p.X).IsEqualTo(
                LayoutConstants.NoteWindowWidth - LayoutConstants.NoteButtonPadding - LayoutConstants.NoteButtonRadius);
            Check.That(p.Y).IsEqualTo(
                LayoutConstants.NoteTitleBarHeight + LayoutConstants.NoteButtonPadding + LayoutConstants.NoteButtonRadius
                + i * (2 * LayoutConstants.NoteButtonRadius + LayoutConstants.NoteButtonGap));
        }
    }
}
```
(Adjust the `LayoutConstants` namespace import to wherever it lives — find it first.)

- [ ] **Step 2: Run test to verify it fails**
```
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~NoteLayoutTests"
```
Expected: compile error — `NoteLayout` does not exist.

- [ ] **Step 3: Implement**

1. `NoteLayout` (namespace matching LayoutConstants' consumers):

```csharp
namespace EyeCtrl.App.Services;

using System.Windows;
using EyeCtrl.Core;

/// <summary>
/// Single source for note-window button geometry (window-relative logical coords).
/// NoteWindow renders from it; NoteManager registers dwell targets from it — previously two
/// hand-mirrored formulas guarded only by "must match" comments.
/// </summary>
public static class NoteLayout
{
    /// <summary>Center of control button <paramref name="index"/> (0=close, 1=edit, 2=move).</summary>
    public static Point ButtonCenter(int index) => new(
        LayoutConstants.NoteWindowWidth - LayoutConstants.NoteButtonPadding - LayoutConstants.NoteButtonRadius,
        LayoutConstants.NoteTitleBarHeight + LayoutConstants.NoteButtonPadding + LayoutConstants.NoteButtonRadius
            + index * (2 * LayoutConstants.NoteButtonRadius + LayoutConstants.NoteButtonGap));
}
```

2. `NoteWindow.DrawButtons`: replace the `cx`/`cy` computation with `var c = NoteLayout.ButtonCenter(i);` (use `c.X`/`c.Y`). `NoteManager.RegisterDwellTargets`: replace the local `cx/cy0/step` + `ButtonCenter(int)` closure with:

```csharp
        PhysicalPoint ButtonCenter(int index)
        {
            var local = NoteLayout.ButtonCenter(index);
            return new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(winLeftLogical + local.X),
                DpiHelper.LogicalToPhysicalY(winTopLogical + local.Y));
        }
```
(keeping `winLeftLogical`/`winTopLogical` and `radiusPhysical` as-is; delete the "must match NoteWindow.DrawButtons" comment — it now cannot drift).

3. `KeyboardWindow`: `GetModifierFromAction` (651-662) already IS the single decoder — reuse it in `OnKeyDwellCompleted` by replacing the five modifier if-blocks (lines 286-319: shift / modifier:ctrl / modifier:alt / modifier:win / toggle:capslock) with:

```csharp
        var modifier = GetModifierFromAction(action);
        if (modifier != null)
        {
            _viewModel.ToggleModifier(modifier.Value);
            UpdateModifierVisuals();
            return;
        }
```
placed right after the `switchemoji` block. The remaining non-modifier branches (backspace/enter/tab/space/switchsub:/key:/emoji fallthrough) stay as they are — they are distinct behaviors, not a duplicated decode.

- [ ] **Step 4: Run tests to verify pass**
```
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI&FullyQualifiedName~NoteLayoutTests|FullyQualifiedName~NoteManagerTests|FullyQualifiedName~KeyboardTests"
```
**Manual verification:** pin a note; the ✕/✎/↕ dwell targets align with the drawn buttons (hover fills the arc of the button you're on); keyboard Shift/Ctrl/Alt/Win/CapsLock keys still toggle + highlight.
- [ ] **Step 5: Commit**
```
git add src/EyeCtrl.App/Services/NoteLayout.cs src/EyeCtrl.App/Services/NoteManager.cs src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs tests/EyeCtrl.App.Tests/Services/NoteLayoutTests.cs
git commit -m "refactor: shared NoteLayout button geometry; keyboard modifier actions decoded once via GetModifierFromAction"
```

---

## Deferred (noted, not planned)

- **SettingsWindow page-builder DSL / declarative tab schema** — the audit rated it optional; the per-tab `Build*Pages` methods are verbose but uniform and low-risk. YAGNI until a new tab actually hurts.
- **Per-tab reset unification** (each tab's hand-rolled `X_reset` copy loop → a generic `CopySettings`-based per-section reset) — same optional rating; B3 fixes the one that was dangerous (Reset ALL). Worth revisiting if a per-tab reset drifts.
- **Slimming `ActionMenuWindow.Show`'s parameter list** after B11 makes `actions`/`showBackButton`/`dwellTimeMs`/`ringRadius` layout-unused — touches `EventCoordinator.OnActionMenuVMPropertyChanged` and the message shape; cosmetic, defer.
- **`ScrollOverlayViewModel` feedback push model** (window subscribing to VM directly instead of the controller relaying `FeedbackChanged`) — works fine as extracted in B15.
- **Promoting `EyeCareBannerWindow`'s remaining magic numbers** (banner height, dim opacity) into shared constants — only the dwell-target geometry was a bug (B6).
- **`ExternalDwellService` per-target dwell times** (protocol currently has only `DefaultDwellMs`) — protocol change, out of scope.
- **`KeyboardWindow.UpdateGaze` position-caching micro-optimizations** beyond B19's gating — no measured need.
