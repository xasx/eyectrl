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

`OnStartup` drops from ~130 lines to ~89 lines. File goes from 260 → ~219 lines.

## Motivation

`App.xaml.cs` is at 260 lines. The architecture review target is <200 lines.
The Phase 3.2 extraction (commit `45a1ea1`) extracted three helpers but the
file is still above target. Two more blocks are self-contained candidates:

1. **DPI initialization** (lines 63-78) — two branches with no shared state
2. **Settings + logging** (lines 80-104) — returns results, no coupling to rest

The remaining ~19 lines over target after this extraction are the event
coordination block (16 lines), which depends on 10+ local variables and is
not cleanly extractable without a large parameter object.

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
private (EyeCtrlSettings Settings, string AppDataDir, string SettingsFilePath) InitializeSettingsAndLogging()
{
    var appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
    var settingsFilePath = Path.Combine(appDataDir, "settings.json");
    var tempSettingsService = new SettingsService(settingsFilePath);
    var settings = tempSettingsService.Load();

    settings.ActionMenu.EnsureDefaults();

    var logsDir = Path.Combine(appDataDir, "logs");
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

    return (settings, appDataDir, settingsFilePath);
}
```

**Notes:**
- NOT static — subscribes to `DispatcherUnhandledException` (instance event).
- Returns the tuple so `OnStartup` can assign `_appDataDir` and pass settings/paths downstream.
- `EnsureDefaults()` migration stays inside — it's settings initialization, not orchestration.

### Resulting `OnStartup`

After extraction, `OnStartup` becomes ~89 lines of clean orchestration:

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);

    SplashWindow? splash = null;
    if (!IsTestMode)
    {
        splash = new SplashWindow();
        splash.Show();
    }

    InitializeDpi(splash);

    var (settings, appDataDir, settingsFilePath) = InitializeSettingsAndLogging();
    _appDataDir = appDataDir;

    _serviceProvider = AppBootstrapper.BuildServiceProvider(settings, settingsFilePath, Dispatcher, IsTestMode);

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

    var (cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm,
         manualSelectionIndicatorVm, backdropVm, calibrationVm, targetHighlightVm) = ResolveViewModels(settings);

    _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();
    _ = _serviceProvider.GetRequiredService<ManualSelectionDwellService>();

    var overlayManager = _serviceProvider.GetRequiredService<IOverlayManager>();
    var viewModels = new OverlayViewModels(cursorVm, activationVm, manualSelectionVm, actionMenuVm,
        toolbarVm, keyboardVm, manualSelectionIndicatorVm, calibrationVm, targetHighlightVm);
    var (cursorCircle, toolbar) = InitializeOverlays(overlayManager, viewModels, settings, activeProviderType);

    var calibrationPath = Path.Combine(_appDataDir, "calibration.json");
    var screen = new PhysicalPoint(
        SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
        SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
    var monitorName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";
    _serviceProvider.GetRequiredService<CalibrationService>().Load(calibrationPath, screen, monitorName);

    _eventCoordinator = new EventCoordinator();
    var noteManager = _serviceProvider.GetRequiredService<INoteManager>();
    var coordinationContext = new EventCoordinationContext(
        cursorVm, activationVm, manualSelectionVm, manualSelectionIndicatorVm, actionMenuVm, toolbarVm,
        keyboardVm, backdropVm, overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService,
        settings, targetHighlightVm, inputSimulator,
        _serviceProvider.GetRequiredService<CalibrationService>(), noteManager);
    _eventCoordinator.Initialize(coordinationContext);

    var modelRegistry = _serviceProvider.GetRequiredService<ModelRegistryService>();
    var modelDownload = _serviceProvider.GetRequiredService<ModelDownloadService>();
    var modelManagement = _serviceProvider.GetRequiredService<ModelManagementService>();
    _eventCoordinator.SetModelServices(modelRegistry, modelDownload, modelManagement);

    _eventCoordinator.LoadKeyboard(overlayManager.Keyboard, settings, inputSimulator, settingsService);

    _trayIconManager = new TrayIconManager(im, Dispatcher, () => Shutdown());
    ShowOverlays(splash, cursorCircle, toolbar, settings);

    if (settings.General.StartPaused)
        im.TogglePause();

    gazeEngine.Start();
    _serviceProvider.GetRequiredService<ExternalDwellService>().Start();
}
```

### File breakdown after extraction

| Section | Lines |
|---------|-------|
| Usings + class declaration | 30 |
| Fields + constants + IsTestMode + DllImport | 18 |
| `OnStartup` | ~89 |
| `OnExit` | 26 |
| `ResolveViewModels` | 26 |
| `InitializeOverlays` | 8 |
| `ShowOverlays` | 12 |
| `InitializeDpi` | 16 |
| `InitializeSettingsAndLogging` | 25 |
| Closing braces | 2 |
| **Total** | **~219** |

## What this is NOT

- Not extracting to a separate class — these are private helpers on `App`
- Not changing any behavior or ordering
- Not touching `OnExit`, `ResolveViewModels`, `InitializeOverlays`, or `ShowOverlays`
- Not extracting the event coordination block (16 lines, 10+ dependencies — not cleanly extractable)

## Verification

1. `dotnet build EyeCtrl.slnx` — clean
2. `dotnet test --filter "Category!=UI"` — all pass
3. `dotnet run --project src/EyeCtrl.App -- --test-mode` — launches and quits
4. Manual review: `OnStartup` reads as a flat sequence of well-named calls
