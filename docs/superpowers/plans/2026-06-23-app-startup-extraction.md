# App.xaml.cs Startup Block Extraction — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract two self-contained startup blocks from `App.xaml.cs` into private helpers, making `OnStartup` read as a flat sequence of intent-revealing calls. This is a readability refactor, not a file-size reduction — the extracted bodies stay as private methods on `App`.

**Architecture:** Two private method extractions in `src/EyeCtrl.App/App.xaml.cs`. No behavior change, no new types, no test changes. `InitializeDpi` is static (only uses static members). `InitializeSettingsAndLogging` is an instance method (subscribes to `DispatcherUnhandledException` and assigns `_appDataDir` directly).

**Tech Stack:** .NET 10.0, WPF, Serilog.

---

## File Map

| File | Action | Purpose |
|------|--------|---------|
| `src/EyeCtrl.App/App.xaml.cs` | Modify | Extract `InitializeDpi` and `InitializeSettingsAndLogging` from `OnStartup`; rewrite `OnStartup` to call them |

No other files are touched.

## Before you start

> **Branch first.** This plan commits once per task. If you're on `main`, create a working
> branch before Task 1: `git switch -c refactor/app-startup-extraction`.

> **Impact analysis (per `CLAUDE.md`).** `impact({target: "OnStartup", direction: "upstream"})`
> returns **0 callers, 0 affected processes, LOW risk** — `OnStartup` is a WPF framework entry
> point, so extracting private helpers from it cannot break any caller. Each task ends with
> `detect_changes()` before its commit.

> **Line numbers below refer to the original `App.xaml.cs`.** Task 1 removes ~15 lines, so by
> Task 2 the settings block and `Log.Information` call have shifted up — match on the quoted
> code, not the absolute numbers.

---

### Task 1: Extract `InitializeDpi` from `OnStartup`

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:63-78` (extract to new private method)

- [ ] **Step 1: Add the `InitializeDpi` method after `ShowOverlays`**

Add this method at line 260 (before the closing `}` of the class):

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

- [ ] **Step 5: Verify changed scope**

Run `detect_changes()`. Expect only `App.OnStartup` (modified) and `App.InitializeDpi` (new) as
changed symbols — anything else means the edit reached further than intended.

- [ ] **Step 6: Commit**

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
- Assigns `_appDataDir` directly (already an instance method), returns only `(settings, settingsFilePath)`.
- The `<summary>` documents that it also registers exception handlers — which the name alone omits.
- `EnsureDefaults()` migration stays inside with its comment carried over.

- [ ] **Step 2: Replace the settings+logging block in `OnStartup` with a method call**

Replace the settings + logging block — lines 80-104 *in the original file* (from `// ── Settings ──` through the `DispatcherUnhandledException` handler closing brace) — with:

```csharp
        var (settings, settingsFilePath) = InitializeSettingsAndLogging();
```

The `OnStartup` method should now read (showing the replaced region):

```csharp
        InitializeDpi(splash);

        var (settings, settingsFilePath) = InitializeSettingsAndLogging();

        // ── Build DI container ──
        _serviceProvider = AppBootstrapper.BuildServiceProvider(settings, settingsFilePath, Dispatcher, IsTestMode);
```

- [ ] **Step 3: Update the `Log.Information` call to use the local `settingsFilePath`**

The existing `Log.Information` call (lines 118-120 *in the original*) uses `Path.Combine(_appDataDir, "settings.json")`. Since we now have `settingsFilePath` as a local, replace:

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

- [ ] **Step 6: Verify changed scope**

Run `detect_changes()`. Expect only `App.OnStartup` (modified) and `App.InitializeSettingsAndLogging`
(new) as changed symbols.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "refactor: extract InitializeSettingsAndLogging from App.OnStartup"
```

---

### Task 3: Smoke test and final verification

**Files:** None (verification only).

- [ ] **Step 1: Run the app in test mode**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`
Expected: app launches (mouse-as-gaze), overlays appear. The app uses
`ShutdownMode="OnExplicitShutdown"` with a system-tray icon, so closing an overlay won't quit —
exit via the tray's **Exit** item (or stop the `dotnet run` process).

- [ ] **Step 2: Verify `OnStartup` reads as flat orchestration**

Read `App.xaml.cs` and confirm the DPI and settings/logging blocks are now single named calls (`InitializeDpi(splash)` and `InitializeSettingsAndLogging()`). The event-coordination block (~16 lines, 10+ dependencies) stays inline **by design** — do not extract it. All rationale comments (PHYSICAL-pixels calibration, `triggers … subscriptions`) must be intact.

- [ ] **Step 3: Run the headless-safe suite**

Run: `dotnet test --filter "Category!=UI"`
Expected: all pass (~926 passed, 1 skipped at last run). The ~13 `AppLaunchTests` (`Category=UI`)
need a real desktop and fail headless — their CI job was dropped for that reason — so they're
excluded here, not a regression signal. Run the unfiltered `dotnet test` only on a machine with
an interactive desktop session.

- [ ] **Step 4: No commit needed — this is verification only.**
