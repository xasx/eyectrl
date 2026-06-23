# App.xaml.cs Startup Block Extraction — Design Spec

> Further thins `App.xaml.cs` by extracting two self-contained startup blocks
> into private helpers. Builds on the Phase 3.2 extraction (commit `45a1ea1`)
> that already extracted `ResolveViewModels`, `InitializeOverlays`, and
> `ShowOverlays`.

## Summary

Extract two blocks from `OnStartup` into private methods:

| Helper | Lines moved | Purpose |
|--------|-------------|---------|
| `InitializeDpi(SplashWindow?)` | 16 | DPI initialization (splash visual or Win32 fallback) |
| `InitializeSettingsAndLogging()` | 25 | Settings load, Serilog init, exception handlers |

After extraction, `OnStartup` reads as a flat sequence of well-named calls.

## Motivation

`App.OnStartup` is a ~130-line method whose two-line-comment-sections obscure
its orchestration flow. The Phase 3.2 extraction (commit `45a1ea1`) already
extracted `ResolveViewModels`, `InitializeOverlays`, and `ShowOverlays`; two
more blocks are self-contained candidates:

1. **DPI initialization** (lines 63-78) — two branches with no shared state
2. **Settings + logging** (lines 80-104) — returns results, no coupling to rest

Extracting these as named helpers makes `OnStartup` scan as a flat sequence of
intent-revealing calls. **This is a readability refactor, not a file-size
reduction** — the extracted bodies stay private methods on `App`, so the file
size is essentially unchanged. The architecture review's <200-line target is
not addressed by this change; reaching it would require extracting helpers to a
separate file, which is out of scope here.

The event coordination block (16 lines) is left in place — it depends on 10+
local variables and is not cleanly extractable without a large parameter
object.

## Approach

### 1. `InitializeDpi(SplashWindow? splash)`

**Current code (lines 63-78):**
```csharp
if (splash != null)
{
    var source = PresentationSource.FromVisual(splash);
    if (source?.CompositionTarget != null)
    {
        var m = source.CompositionTarget.TransformToDevice;
        DpiHelper.Initialize(m.M11, m.M22);
    }
}
if (IsTestMode)
{
    var dpi = GetDpiForSystem();
    var scale = dpi / (double)DpiStandardScale;
    DpiHelper.Initialize(scale, scale);
}
```

**Extracted method:**
```csharp
/// <summary>
/// Initializes <see cref="DpiHelper"/> from the splash window's composition target.
/// In test mode no splash is shown, so it falls back to the Win32 system DPI.
/// </summary>
private static void InitializeDpi(SplashWindow? splash)
{
    if (splash != null)
    {
        var source = PresentationSource.FromVisual(splash);
        if (source?.CompositionTarget != null)
        {
            var m = source.CompositionTarget.TransformToDevice;
            DpiHelper.Initialize(m.M11, m.M22);
        }
    }
    if (IsTestMode)
    {
        var dpi = GetDpiForSystem();
        var scale = dpi / (double)DpiStandardScale;
        DpiHelper.Initialize(scale, scale);
    }
}
```

**Notes:**
- `static` — accesses `IsTestMode` (static property) and `GetDpiForSystem` (static P/Invoke).
- `DpiStandardScale` is a `private const` on `App` — accessible from static method.
- No return value — `DpiHelper.Initialize` is a static side effect.

### 2. `InitializeSettingsAndLogging()`

**Current code (lines 80-104):**
```csharp
_appDataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
var settingsFilePath = Path.Combine(_appDataDir, "settings.json");
var tempSettingsService = new SettingsService(settingsFilePath);
var settings = tempSettingsService.Load();

// Migrate settings for newly-introduced features
settings.ActionMenu.EnsureDefaults();

// ── Logging ──
var logsDir = Path.Combine(_appDataDir, "logs");
LoggingInitializer.Initialize(logsDir);

AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})",
        args.IsTerminating);
    Log.CloseAndFlush();
};

DispatcherUnhandledException += (_, args) =>
{
    Log.Error(args.Exception, "Unhandled dispatcher exception");
};
```

**Extracted method:**
```csharp
/// <summary>
/// Resolves the app data directory (stored in <see cref="_appDataDir"/>), loads settings
/// while migrating newly-introduced defaults, initializes Serilog, and registers the global
/// (AppDomain + dispatcher) exception handlers.
/// </summary>
private (EyeCtrlSettings Settings, string SettingsFilePath) InitializeSettingsAndLogging()
{
    _appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
    var settingsFilePath = Path.Combine(_appDataDir, "settings.json");
    var tempSettingsService = new SettingsService(settingsFilePath);
    var settings = tempSettingsService.Load();

    // Migrate settings for newly-introduced features
    settings.ActionMenu.EnsureDefaults();

    var logsDir = Path.Combine(_appDataDir, "logs");
    LoggingInitializer.Initialize(logsDir);

    AppDomain.CurrentDomain.UnhandledException += (_, args) =>
    {
        Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})",
            args.IsTerminating);
        Log.CloseAndFlush();
    };

    DispatcherUnhandledException += (_, args) =>
    {
        Log.Error(args.Exception, "Unhandled dispatcher exception");
    };

    return (settings, settingsFilePath);
}
```

**Notes:**
- NOT static — subscribes to `DispatcherUnhandledException` (instance event).
- Because it is already an instance method, it assigns the `_appDataDir` field directly
  (as the original code does) and returns only `(settings, settingsFilePath)` — no need to
  round-trip `appDataDir` back through `OnStartup`.
- It does three things: load settings, init logging, and register the two global exception
  handlers. The name omits the last, so the `<summary>` above documents it — a reader scanning
  `OnStartup` then knows where exception wiring lives.
- `EnsureDefaults()` migration stays inside — it's settings initialization, not orchestration.
  Its `// Migrate settings for newly-introduced features` comment is carried into the method.

### Resulting `OnStartup`

After extraction, the DPI and settings/logging blocks collapse into two named
calls. The method names replace the `// ── DPI initialization ──` and
`// ── Settings ──` / `// ── Logging ──` section labels; **every other comment is
preserved** — including the `triggers … subscriptions` notes on the discard lines
and the PHYSICAL-pixels calibration rationale, which document non-obvious,
bug-preventing behavior:

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);

    // ── Splash screen ──
    SplashWindow? splash = null;
    if (!IsTestMode)
    {
        splash = new SplashWindow();
        splash.Show();
    }

    InitializeDpi(splash);

    var (settings, settingsFilePath) = InitializeSettingsAndLogging();

    // ── Build DI container ──
    _serviceProvider = AppBootstrapper.BuildServiceProvider(settings, settingsFilePath, Dispatcher, IsTestMode);

    // ── Resolve services ──
    var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
    var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
    var messenger = _serviceProvider.GetRequiredService<IMessenger>();
    var inputSimulator = _serviceProvider.GetRequiredService<IInputSimulator>();
    var gazeProvider = _serviceProvider.GetRequiredService<IGazeProvider>();
    var activeProviderType = GazeProviderFactory.GetProviderType(gazeProvider);
    var version = System.Reflection.Assembly.GetExecutingAssembly()
        .GetName().Version?.ToString() ?? "unknown";
    Log.Information("Startup: EyeCtrl v{Version}, provider={Provider}, settings={SettingsPath}",
        version, activeProviderType, settingsFilePath);
    var im = gazeEngine.InteractionManager;

    // ── Resolve ViewModels ──
    var (cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm,
         manualSelectionIndicatorVm, backdropVm, calibrationVm, targetHighlightVm) = ResolveViewModels(settings);

    // Resolve adapter (triggers Core event subscriptions)
    _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();

    // Resolve ManualSelectionDwellService (triggers message subscriptions)
    _ = _serviceProvider.GetRequiredService<ManualSelectionDwellService>();

    // ── Initialize overlay windows via OverlayManager ──
    var overlayManager = _serviceProvider.GetRequiredService<IOverlayManager>();
    var viewModels = new OverlayViewModels(cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm, manualSelectionIndicatorVm, calibrationVm, targetHighlightVm);
    var (cursorCircle, toolbar) = InitializeOverlays(overlayManager, viewModels, settings, activeProviderType);

    // Load calibration. Screen size must be in PHYSICAL pixels to match what
    // CompleteSession persists (logical DIPs here would discard calibration on any non-100% DPI).
    var calibrationPath = Path.Combine(_appDataDir, "calibration.json");
    var screen = new PhysicalPoint(
        SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
        SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
    var monitorName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";
    _serviceProvider.GetRequiredService<CalibrationService>().Load(calibrationPath, screen, monitorName);

    // ── Event Coordination ──
    // All PropertyChanged and message handlers are now managed by EventCoordinator
    _eventCoordinator = new EventCoordinator();
    var noteManager = _serviceProvider.GetRequiredService<INoteManager>();
    var coordinationContext = new EventCoordinationContext(
        cursorVm, activationVm, manualSelectionVm, manualSelectionIndicatorVm, actionMenuVm, toolbarVm, keyboardVm, backdropVm,
        overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, targetHighlightVm, inputSimulator,
        _serviceProvider.GetRequiredService<CalibrationService>(),
        noteManager);
    _eventCoordinator.Initialize(coordinationContext);

    var modelRegistry = _serviceProvider.GetRequiredService<ModelRegistryService>();
    var modelDownload = _serviceProvider.GetRequiredService<ModelDownloadService>();
    var modelManagement = _serviceProvider.GetRequiredService<ModelManagementService>();
    _eventCoordinator.SetModelServices(modelRegistry, modelDownload, modelManagement);

    // Load keyboard using EventCoordinator
    _eventCoordinator.LoadKeyboard(overlayManager.Keyboard, settings, inputSimulator, settingsService);

    // ── System tray ──
    _trayIconManager = new TrayIconManager(im, Dispatcher, () => Shutdown());

    // ── Close splash and show persistent overlays (last, after all wiring) ──
    ShowOverlays(splash, cursorCircle, toolbar, settings);

    if (settings.General.StartPaused)
        im.TogglePause();

    gazeEngine.Start();

    // External dwell pipe server (cooperating-app control activation).
    _serviceProvider.GetRequiredService<ExternalDwellService>().Start();
}
```

> **Note:** the startup log line now passes `settingsFilePath` (the local)
> instead of `Path.Combine(_appDataDir, "settings.json")` — same value, one
> fewer recomputation. Called out here for transparency even though the spec
> otherwise preserves behavior.

## What this is NOT

- Not extracting to a separate class — these are private helpers on `App`
- Not changing any behavior or ordering (aside from the log-line cleanup above)
- Not removing any explanatory comments — only the two section labels for the extracted
  blocks disappear (the method names replace them); the `triggers … subscriptions` and
  PHYSICAL-pixels rationale comments stay put
- Not a file-size reduction — the extracted bodies remain in `App.xaml.cs` as
  private methods, so the file size is essentially unchanged
- Not addressing the architecture review's <200-line target (would require
  extracting to a separate file, out of scope)
- Not touching `OnExit`, `ResolveViewModels`, `InitializeOverlays`, or `ShowOverlays`
- Not extracting the event coordination block (16 lines, 10+ dependencies — not cleanly extractable)

## Verification

Follow the project's GitNexus conventions (`CLAUDE.md`) alongside the standard build/test gates:

1. **Impact analysis** — `impact({target: "OnStartup", direction: "upstream"})`. Already run
   against the current index: **0 callers, 0 affected processes, LOW risk.** `OnStartup` is a
   WPF framework entry point, so extracting private helpers from it cannot break any caller —
   the blast radius is confined to `App`.
2. `dotnet build EyeCtrl.slnx` — clean.
3. `dotnet test --filter "Category!=UI"` — all pass (the headless-safe subset; the ~13
   environmental `AppLaunchTests` are excluded by category).
4. `dotnet run --project src/EyeCtrl.App -- --test-mode` — launches and quits.
5. **`detect_changes()`** before committing — expect only `App.OnStartup` (and the two new
   private helpers) as changed symbols; anything else means the edit reached further than intended.
6. Manual review: the DPI and settings/logging blocks are now two named calls and all
   rationale comments are intact.

> A trial extraction following this spec built cleanly and kept the headless suite green
> (926 passed / 1 skipped / 0 failed), confirming the approach before it was reverted pending
> sign-off on this design.

## Revisions (post-review)

- **Comments preserved.** The earlier "Resulting `OnStartup`" sketch silently dropped the
  `triggers … subscriptions` and PHYSICAL-pixels rationale comments. Those are now kept; only
  the two section labels for the extracted blocks go away.
- **2-tuple instead of 3.** `InitializeSettingsAndLogging` is already an instance method, so it
  assigns `_appDataDir` directly and returns `(settings, settingsFilePath)` rather than handing
  `appDataDir` back through `OnStartup`.
- **GitNexus gates added.** Verification now includes the mandated `impact` (result recorded)
  and pre-commit `detect_changes` steps.
- **Doc comments added** to both helpers; `InitializeSettingsAndLogging`'s `<summary>` records
  that it also registers the global exception handlers — which the name alone omits.
