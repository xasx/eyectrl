# App.xaml.cs Startup Block Extraction — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract two self-contained startup blocks from `App.xaml.cs` into private helpers, reducing `OnStartup` from ~130 to ~89 lines and the file from 260 to ~219 lines.

**Architecture:** Two private method extractions in `src/EyeCtrl.App/App.xaml.cs`. No behavior change, no new types, no test changes. `InitializeDpi` is static (only uses static members). `InitializeSettingsAndLogging` is an instance method (subscribes to `DispatcherUnhandledException`).

**Tech Stack:** .NET 10.0, WPF, Serilog.

---

## File Map

| File | Action | Purpose |
|------|--------|---------|
| `src/EyeCtrl.App/App.xaml.cs` | Modify | Extract `InitializeDpi` and `InitializeSettingsAndLogging` from `OnStartup`; rewrite `OnStartup` to call them |

No other files are touched.

---

### Task 1: Extract `InitializeDpi` from `OnStartup`

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:63-78` (extract to new private method)

- [ ] **Step 1: Add the `InitializeDpi` method after `ShowOverlays`**

Add this method at line 260 (before the closing `}` of the class):

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

- [ ] **Step 2: Replace the DPI block in `OnStartup` with a method call**

Replace lines 63-78 (the `// ── DPI initialization ──` block through the closing brace of the `IsTestMode` block) with:

```csharp
        InitializeDpi(splash);
```

The `OnStartup` method should now read:

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

        // ── Settings ──
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
        // ... rest of OnStartup unchanged
```

- [ ] **Step 3: Verify build**

Run: `dotnet build EyeCtrl.slnx`
Expected: 0 errors.

- [ ] **Step 4: Run unit tests**

Run: `dotnet test --filter "Category!=UI"`
Expected: All pass (no behavior change).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "refactor: extract InitializeDpi from App.OnStartup"
```

---

### Task 2: Extract `InitializeSettingsAndLogging` from `OnStartup`

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:80-104` (extract to new private method)

- [ ] **Step 1: Add the `InitializeSettingsAndLogging` method after `InitializeDpi`**

Add this method after the `InitializeDpi` method (before the class closing `}`):

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

- [ ] **Step 2: Replace the settings+logging block in `OnStartup` with a method call**

Replace lines 80-104 (from `// ── Settings ──` through the `DispatcherUnhandledException` handler closing brace) with:

```csharp
        var (settings, appDataDir, settingsFilePath) = InitializeSettingsAndLogging();
        _appDataDir = appDataDir;
```

The `OnStartup` method should now read (showing the replaced region):

```csharp
        InitializeDpi(splash);

        var (settings, appDataDir, settingsFilePath) = InitializeSettingsAndLogging();
        _appDataDir = appDataDir;

        // ── Build DI container ──
        _serviceProvider = AppBootstrapper.BuildServiceProvider(settings, settingsFilePath, Dispatcher, IsTestMode);
```

- [ ] **Step 3: Update the `Log.Information` call to use the local `settingsFilePath`**

The existing `Log.Information` call at line 118-120 uses `Path.Combine(_appDataDir, "settings.json")`. Since we now have `settingsFilePath` as a local, replace:

```csharp
        Log.Information("Startup: EyeCtrl v{Version}, provider={Provider}, settings={SettingsPath}",
            version, activeProviderType,
            Path.Combine(_appDataDir, "settings.json"));
```

with:

```csharp
        Log.Information("Startup: EyeCtrl v{Version}, provider={Provider}, settings={SettingsPath}",
            version, activeProviderType, settingsFilePath);
```

This is a minor cleanup — the local `settingsFilePath` is the same value and avoids redundant `Path.Combine`.

- [ ] **Step 4: Verify build**

Run: `dotnet build EyeCtrl.slnx`
Expected: 0 errors.

- [ ] **Step 5: Run unit tests**

Run: `dotnet test --filter "Category!=UI"`
Expected: All pass (no behavior change).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "refactor: extract InitializeSettingsAndLogging from App.OnStartup"
```

---

### Task 3: Smoke test and final verification

**Files:** None (verification only).

- [ ] **Step 1: Run the app in test mode**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`
Expected: App launches (mouse-as-gaze), overlays appear. Press Ctrl+C or close the window to exit.

- [ ] **Step 2: Verify line count**

Run: `(Get-Content "src/EyeCtrl.App/App.xaml.cs").Count`
Expected: ~219 lines (down from 260).

- [ ] **Step 3: Verify `OnStartup` reads as flat orchestration**

Read `App.xaml.cs` and confirm `OnStartup` is a clean sequence of named calls with no inline blocks longer than ~5 lines.

- [ ] **Step 4: Run full test suite**

Run: `dotnet test`
Expected: All pass.

- [ ] **Step 5: No commit needed — this is verification only.**
