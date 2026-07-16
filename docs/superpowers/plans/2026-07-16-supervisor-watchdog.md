# EyeCtrl Supervisor (Watchdog) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a companion supervisor process that keeps EyeCtrl running and recoverable for users who depend on it as primary input — auto-restarting on crash/hang, launching a fallback assistive-tech app on crash-loop, with a dwell-operable recovery UI.

**Architecture:** A new `EyeCtrl.Supervisor` exe (WinExe, user session, launched by a Scheduled Task) watches the app over a heartbeat named pipe (`eyectrl_watch`), runs a 6-state supervision machine (LAUNCHING / SUPERVISING / RECOVERING / FALLEN_BACK / IDLE_WATCH / CLEAN_EXIT_PENDING), and on crash-loop launches a fallback AT (TD Control → Snap Core First → Windows Eye Control). A new shared `EyeCtrl.Controls` project houses the dwell WPF controls both processes reuse. The app gains a `--paused` launch arg, a single-instance mutex, a heartbeat emitter, and exception-handler fixes.

**Tech Stack:** C# / .NET 10 / WPF, xUnit v3 (3.2.2) + NFluent 3.1.0, System.Text.Json, Serilog, Win32 P/Invoke (`[LibraryImport]`).

**Spec:** `docs/superpowers/specs/2026-07-15-supervisor-watchdog-design.md` (read it before starting any task).

---

## Reference facts (verified against the codebase)

- **Test conventions:** xUnit v3, NFluent. `tests/Directory.Build.props` sets global usings `Xunit` + `NFluent` — test files need neither `using`. File-scoped namespaces. `Check.That(x).IsEqualTo(y)`. Test projects are `<OutputType>Exe</OutputType>` (standalone), get all test packages from `tests/Directory.Build.props` automatically — a new test csproj only needs `<TargetFramework>net10.0</TargetFramework>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<Nullable>enable</Nullable>`, and a `<ProjectReference>`.
- **Settings root:** `EyeCtrlSettings : ObservableObject, IValidatable` with `[ObservableProperty]` fields. Adding a section = add the field + add `yield return (nameof(X), X);` to `EnumerateSubSettings()` (drift-guard test `EyeCtrlSettingsEnumerationTests` asserts `HasSize(17)` → bump to 18).
- **SettingsService:** `new SettingsService(filePath, ILogger<SettingsService>?)`. System.Text.Json, camelCase, string enums. `Save(EyeCtrlSettings)` writes the whole object (the lost-update hazard — see Task 6).
- **DI:** `AppBootstrapper.BuildServiceProvider(...)` registers singletons; `EyeCtrlSettings` + `SettingsService` registered as concrete singletons.
- **Pipe server template:** `src/EyeCtrl.App/Services/ExternalDwellService.cs` (`eyectrl_dwell`). `NamedPipeServerStream` + async accept loop, one instance per client, newline-JSON, `PipeOptions.Asynchronous`.
- **Tray template:** `src/EyeCtrl.App/Services/TrayIconManager.cs` — `NotifyIcon` + `ContextMenuStrip`, `ToolStripMenuItem` `.Click` lambdas.
- **P/Invoke home:** `src/EyeCtrl.Platform/Input/NativeMethods.cs` — `internal static partial class`, `[LibraryImport]` style.
- **Pause:** `InteractionManager.TogglePause()` (`InteractionManager.cs:199`); gate at `GazeEngine.cs:133-134` returns before `MoveCursor`. Launch-paused honored at `App.xaml.cs:181-182`.
- **Gaze hook:** `GazeEngine.GazeSampled` event (`GazeEngine.cs:44`) fires per raw sample on the gaze thread — the place to stamp `lastFrameUtc`.
- **.slnx:** simple `<Folder><Project Path="..."/></Folder>` entries under `/src/` and `/tests/`.

---

## File Structure

**New projects:**
- `src/EyeCtrl.Controls/` — `net10.0-windows`, `UseWPF`, class library. Houses the dwell WPF controls moved out of App.
- `src/EyeCtrl.Supervisor/` — `net10.0-windows`, `WinExe`, `UseWPF` + minimal `UseWindowsForms` (tray). The watchdog.
- `tests/EyeCtrl.Controls.Tests/` — net10.0 test project.
- `tests/EyeCtrl.Supervisor.Tests/` — net10.0 test project.

**New files (Core):**
- `src/EyeCtrl.Core/Settings/SupervisionSettings.cs` — the supervision config section.

**New files (Platform):**
- `src/EyeCtrl.Platform/Interop/ProcessIdentity.cs` — P/Invokes + helpers for `GetNamedPipeClientProcessId`, `QueryFullProcessImageName`, `ProcessIdToSessionId`.
- `src/EyeCtrl.Platform/Interop/NamedPipeSecurityBuilder.cs` — pipe security-descriptor builder (current-user SID only).

**New files (App):**
- `src/EyeCtrl.App/Services/HeartbeatPublisher.cs` — Dispatcher stamp + bounded async pipe writer.
- `src/EyeCtrl.App/Services/SingleInstanceGuard.cs` — `Local\` mutex.

**New files (Supervisor):**
- `src/EyeCtrl.Core/Models/HeartbeatPayload.cs` — the JSON heartbeat record (shared: App emits, Supervisor reads; both reference Core).
- `src/EyeCtrl.Supervisor/Supervision/SupervisorState.cs` — enum.
- `src/EyeCtrl.Supervisor/Supervision/CrashWindow.cs` — sliding strike counter.
- `src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs` — the brain.
- `src/EyeCtrl.Supervisor/Supervision/ProcessController.cs` — launch/kill/isAlive.
- `src/EyeCtrl.Supervisor/Supervision/BackoffPolicy.cs` — 0→5→15→60s.
- `src/EyeCtrl.Supervisor/Ipc/HeartbeatPipeServer.cs` — `eyectrl_watch` server.
- `src/EyeCtrl.Supervisor/Ipc/ClientIdentityBinder.cs` — process binding checks.
- `src/EyeCtrl.Supervisor/Fallback/FallbackAtLauncher.cs` — TD/Snap/WinEyeControl probe+launch.
- `src/EyeCtrl.Supervisor/Fallback/FallbackCandidate.cs` — probe model.
- `src/EyeCtrl.Supervisor/UI/SupervisorTrayIcon.cs` — tray.
- `src/EyeCtrl.Supervisor/UI/FallenBackDialog.xaml[.cs]` — crash-recovery dialog.
- `src/EyeCtrl.Supervisor/UI/SupervisorSetupWindow.xaml[.cs]` — setup GUI.
- `src/EyeCtrl.Supervisor/Program.cs` — entry point.
- `src/EyeCtrl.Supervisor/SupervisorLoggingInitializer.cs` — Serilog to `supervisor-.log`.

**Modified files:**
- `EyeCtrl.slnx` — add 4 projects.
- `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs` — add `Supervision` section + enumeration entry.
- `src/EyeCtrl.Core/Services/SettingsService.cs` — writer-identity enforcement.
- `src/EyeCtrl.App/App.xaml.cs` — `--paused` arg, mutex, conditional `SetStartOnBoot`, heartbeat wiring, exception handlers, clean-shutdown signal.
- `src/EyeCtrl.App/EyeCtrl.App.csproj` — reference `EyeCtrl.Controls`.
- `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs` — bump `HasSize`.

---

## Spike tasks FIRST (Open Questions block later work)

The two Open Questions from the spec affect core assumptions. Run them before the integration tasks that depend on their answers.

- **Task S1 (spike): Windows Eye Control launch** — affects Goal #5 (never strand the user) and Task 23 (FallbackAtLauncher).
- **Task S2 (spike): Tobii multi-subscriber** — affects tracker-sharing and Task 22 (paused-retry handoff).

Run these as the very first implementation work; their outcomes are recorded in the plan comments and adjust later tasks.

---

## Phase 1 — Extract `EyeCtrl.Controls` (prerequisite for the supervisor's dwell UI)

### Task 1: Create the `EyeCtrl.Controls` project and move the dwell controls

**Files:**
- Create: `src/EyeCtrl.Controls/EyeCtrl.Controls.csproj`
- Move (from `src/EyeCtrl.App/Controls/` → `src/EyeCtrl.Controls/`): `DwellControl.cs`, `DwellButton.cs`, `DwellArcButton.cs`, `DwellSlider.cs`, `DwellComboBox.cs`, `DwellToggle.cs`, `DwellLabel.cs`, `DwellColorPicker.cs`, `ColorBlend.cs`, `IDwellComposite.cs`
- Modify: `EyeCtrl.slnx`
- Create: `tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj`

**Context:** The dwell WPF controls live in `EyeCtrl.App.Controls` (namespace `EyeCtrl.App.Controls`). Their only non-WPF dependency is `EyeCtrl.Core.Processing` (`IDwellRegistrar`, `DwellCategory`). Core/Platform have zero `.xaml`. A supervisor referencing only Core+Platform cannot get `DwellButton` — hence this extraction. The controls are code-built C# (no `.xaml`), so this is a file move + namespace change.

- [ ] **Step 1: Create the project file**

Create `src/EyeCtrl.Controls/EyeCtrl.Controls.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\EyeCtrl.Core\EyeCtrl.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Move the control files and change their namespace**

Move each of the 10 files listed above from `src/EyeCtrl.App/Controls/` into `src/EyeCtrl.Controls/`. In each moved file, change `namespace EyeCtrl.App.Controls;` → `namespace EyeCtrl.Controls;`. Use `git mv` so history is preserved:

```bash
git mv src/EyeCtrl.App/Controls/DwellControl.cs src/EyeCtrl.Controls/DwellControl.cs
git mv src/EyeCtrl.App/Controls/DwellButton.cs src/EyeCtrl.Controls/DwellButton.cs
git mv src/EyeCtrl.App/Controls/DwellArcButton.cs src/EyeCtrl.Controls/DwellArcButton.cs
git mv src/EyeCtrl.App/Controls/DwellSlider.cs src/EyeCtrl.Controls/DwellSlider.cs
git mv src/EyeCtrl.App/Controls/DwellComboBox.cs src/EyeCtrl.Controls/DwellComboBox.cs
git mv src/EyeCtrl.App/Controls/DwellToggle.cs src/EyeCtrl.Controls/DwellToggle.cs
git mv src/EyeCtrl.App/Controls/DwellLabel.cs src/EyeCtrl.Controls/DwellLabel.cs
git mv src/EyeCtrl.App/Controls/DwellColorPicker.cs src/EyeCtrl.Controls/DwellColorPicker.cs
git mv src/EyeCtrl.App/Controls/ColorBlend.cs src/EyeCtrl.Controls/ColorBlend.cs
git mv src/EyeCtrl.App/Controls/IDwellComposite.cs src/EyeCtrl.Controls/IDwellComposite.cs
```

Then, in each moved `.cs` file, replace the namespace line. Verify there are no remaining references to the old namespace inside the moved files:

```bash
grep -rn "namespace EyeCtrl.App.Controls" src/EyeCtrl.Controls/ || echo "none remaining (good)"
```

Expected: "none remaining (good)".

- [ ] **Step 3: Add the project to the solution**

In `EyeCtrl.slnx`, add inside `<Folder Name="/src/">`:

```xml
    <Project Path="src/EyeCtrl.Controls/EyeCtrl.Controls.csproj" />
```

- [ ] **Step 4: Create the test project skeleton**

Create `tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Controls\EyeCtrl.Controls.csproj" />
  </ItemGroup>
</Project>
```

Add to `EyeCtrl.slnx` under `<Folder Name="/tests/">`:

```xml
    <Project Path="tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj" />
```

- [ ] **Step 5: Build the new project in isolation**

Run: `dotnet build src/EyeCtrl.Controls/EyeCtrl.Controls.csproj`
Expected: build succeeds. If it fails on a missing dependency, that dependency is something the control needs from App — add it as a reference or move it into Controls only if it is itself a control (do NOT reference App from Controls).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(controls): extract dwell WPF controls into EyeCtrl.Controls

Move DwellButton/DwellControl/DwellArc/DwellSlider/DwellComboBox/
DwellToggle/DwellLabel/DwellColorPicker/ColorBlend/IDwellComposite
from EyeCtrl.App.Controls into a shared EyeCtrl.Controls project
(net10.0-windows, UseWPF, references Core). Namespace -> EyeCtrl.Controls.
Prerequisite for the supervisor's dwell UI."
```

### Task 2: Update `EyeCtrl.App` to consume `EyeCtrl.Controls`

**Files:**
- Modify: `src/EyeCtrl.App/EyeCtrl.App.csproj`
- Modify: every App file that referenced `EyeCtrl.App.Controls` (XAML + code-behind)

- [ ] **Step 1: Add the project reference**

In `src/EyeCtrl.App/EyeCtrl.App.csproj`, add to the `<ItemGroup>` of ProjectReferences:

```xml
    <ProjectReference Include="..\EyeCtrl.Controls\EyeCtrl.Controls.csproj" />
```

- [ ] **Step 2: Find and fix all references to the old namespace**

Run: `grep -rln "EyeCtrl.App.Controls" src/EyeCtrl.App/`
Expected: a list of files (XAML `xmlns:` declarations and C# `using` lines).

In each XAML file, change the xmlns to point at the new project, e.g.:
```xml
xmlns:controls="clr-namespace:EyeCtrl.App.Controls;assembly=EyeCtrl.App"
```
becomes
```xml
xmlns:controls="clr-namespace:EyeCtrl.Controls;assembly=EyeCtrl.Controls"
```

In each C# file, change `using EyeCtrl.App.Controls;` → `using EyeCtrl.Controls;`.

- [ ] **Step 3: Build the whole solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: succeeds with zero errors. If a type is ambiguous (a `ColorBlend` etc. exists in both), the old namespace is gone after the move, so this should not occur — but if it does, qualify the reference.

- [ ] **Step 4: Run the existing App tests to confirm no dwell regression**

Run: `dotnet test`
Expected: all green. This is the "extraction didn't break existing dwell functionality" gate (spec Testing section).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(app): consume dwell controls from EyeCtrl.Controls

Update App csproj reference + all xmlns/using from EyeCtrl.App.Controls
to EyeCtrl.Controls. Existing dwell tests confirm behavior unchanged."
```

---

## Phase 2 — Settings: `SupervisionSettings` + single-writer enforcement

### Task 3: Add `SupervisionSettings` section to Core

**Files:**
- Create: `src/EyeCtrl.Core/Settings/SupervisionSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`
- Create: `tests/EyeCtrl.Core.Tests/Settings/SupervisionSettingsTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/EyeCtrl.Core.Tests/Settings/SupervisionSettingsTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class SupervisionSettingsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var s = new SupervisionSettings();
        Check.That(s.Enabled).IsFalse();
        Check.That(s.MaxRetries).IsEqualTo(3);
        Check.That(s.CrashWindowSeconds).IsEqualTo(120);
        Check.That(s.HangThresholdSeconds).IsEqualTo(20);
        Check.That(s.StartupGraceSeconds).IsEqualTo(60);
        Check.That(s.FallbackAtOrder).ContainsExactly("TDControl", "SnapCoreFirst", "WindowsEyeControl");
    }

    [Fact]
    public void Validate_OutOfRange_ReturnsErrors()
    {
        var s = new SupervisionSettings { MaxRetries = 0, CrashWindowSeconds = 0, HangThresholdSeconds = 0, StartupGraceSeconds = 0 };
        var errors = s.Validate();
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(SupervisionSettings.MaxRetries)));
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(SupervisionSettings.CrashWindowSeconds)));
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(SupervisionSettings.HangThresholdSeconds)));
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(SupervisionSettings.StartupGraceSeconds)));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SupervisionSettingsTests"`
Expected: FAIL — `SupervisionSettings` not found.

- [ ] **Step 3: Implement `SupervisionSettings`**

Create `src/EyeCtrl.Core/Settings/SupervisionSettings.cs`. Follow the `EyeCareSettings` model (ObservableObject + IValidatable + ValidationHelper):

```csharp
namespace EyeCtrl.Core.Settings;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

public partial class SupervisionSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = false;
    [ObservableProperty] private int _maxRetries = 3;
    [ObservableProperty] private int _crashWindowSeconds = 120;
    [ObservableProperty] private int _hangThresholdSeconds = 20;
    [ObservableProperty] private int _startupGraceSeconds = 60;
    [ObservableProperty] private List<string> _fallbackAtOrder = new() { "TDControl", "SnapCoreFirst", "WindowsEyeControl" };

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(MaxRetries, 1, 20, nameof(MaxRetries)),
            ValidationHelper.ValidateRange(CrashWindowSeconds, 10, 3600, nameof(CrashWindowSeconds)),
            ValidationHelper.ValidateRange(HangThresholdSeconds, 5, 300, nameof(HangThresholdSeconds)),
            ValidationHelper.ValidateRange(StartupGraceSeconds, 10, 600, nameof(StartupGraceSeconds))
        );
    }
}
```

(If `ValidationHelper` method names differ, check `src/EyeCtrl.Core/Settings/ValidationHelper.cs` and match its real signatures — the EyeCare settings test proves the `CollectErrors`/`ValidateRange(value, min, max, name)` shape.)

- [ ] **Step 4: Register the section on `EyeCtrlSettings`**

In `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`:
- Add the field near the other sections: `[ObservableProperty] private SupervisionSettings _supervision = new();`
- In `EnumerateSubSettings()`, add: `yield return (nameof(Supervision), Supervision);`

- [ ] **Step 5: Bump the drift-guard test**

In `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs`, change `HasSize(17)` → `HasSize(18)`. (If the assertion uses a literal 17, update it; if it derives the count another way, follow that pattern.)

- [ ] **Step 6: Run tests, verify pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SupervisionSettingsTests|FullyQualifiedName~EyeCtrlSettingsEnumerationTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): add SupervisionSettings section

Enabled, MaxRetries(3), CrashWindowSeconds(120), HangThresholdSeconds(20),
StartupGraceSeconds(60), FallbackAtOrder. Registered on EyeCtrlSettings,
enumeration drift-guard bumped to 18."
```

### Task 4: Enforce single-writer ownership in `SettingsService`

**Files:**
- Modify: `src/EyeCtrl.Core/Services/SettingsService.cs`
- Modify: `src/EyeCtrl.Core/Services/SettingsService.cs` constructor / DI
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Create: `tests/EyeCtrl.Core.Tests/Services/SettingsServiceWriterOwnershipTests.cs`

**Context (spec, "Lost updates, not just corruption"):** The app writes the whole settings object on exit. Without enforcement, the app could clobber the supervisor's `Supervision` section. Each section has exactly one writer process; the app owns everything **except** `Supervision`; the supervisor owns only `Supervision`. `SettingsService` is constructed with a writer identity that controls which sections it may persist.

- [ ] **Step 1: Write failing test**

Create `tests/EyeCtrl.Core.Tests/Services/SettingsServiceWriterOwnershipTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

public class SettingsServiceWriterOwnershipTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");

    [Fact]
    public void AppWriter_DoesNotPersist_SupervisionSection()
    {
        var path = TempPath();
        try
        {
            // Supervisor wrote Enabled=true on disk.
            var supervisor = new SettingsService(path, SettingsWriter.Supervisor);
            var seed = new EyeCtrlSettings { Supervision = { Enabled = true } };
            supervisor.Save(seed);

            // App loads, then saves its own snapshot (Supervision.Enabled default false).
            var app = new SettingsService(path, SettingsWriter.App);
            var loaded = app.Load();
            Check.That(loaded.Supervision.Enabled).IsTrue(); // sees supervisor's value
            app.Save(loaded); // app must not clobber it

            var roundTripped = new SettingsService(path, SettingsWriter.App).Load();
            Check.That(roundTripped.Supervision.Enabled).IsTrue(); // preserved, not reset
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SettingsServiceWriterOwnershipTests"`
Expected: FAIL — no `SettingsWriter` type, and `Save` has no ownership concept.

- [ ] **Step 3: Add the `SettingsWriter` enum**

Add to `src/EyeCtrl.Core/Services/SettingsService.cs` (or a new `SettingsWriter.cs` next to it):

```csharp
namespace EyeCtrl.Core.Services;

/// <summary>
/// Identifies which process owns a SettingsService instance, so Save() can
/// refuse to persist sections the caller doesn't own (prevents cross-process
/// lost updates — see supervisor design spec, "Lost updates").
/// </summary>
public enum SettingsWriter
{
    /// <summary>Owns all sections except Supervision.</summary>
    App,
    /// <summary>Owns only the Supervision section.</summary>
    Supervisor
}
```

- [ ] **Step 4: Update `SettingsService` to honor ownership**

Modify `src/EyeCtrl.Core/Services/SettingsService.cs`:
- Add a field `private readonly SettingsWriter _writer;` and a new constructor overload `public SettingsService(string filePath, SettingsWriter writer, ILogger<SettingsService>? logger = null)` that stores `_writer`. Keep the existing 2-arg constructor delegating with `SettingsWriter.App` (the historical behavior — the app is the only existing writer today).
- In `Save(EyeCtrlSettings settings)`: before serializing, **re-read the on-disk file** under the cross-process mutex (`Local\EyeCtrl.Settings.Writer`) and, for any section the caller does **not** own, take that section's value from disk instead of from the in-memory snapshot. For the App writer, that means: `merged.Supervision = onDisk.Supervision` (preserve whatever the supervisor wrote). Then atomic-write (temp + `File.Move(overwrite:true)`).
- Add the atomic write + mutex if not already present (it is not — `Save` currently writes directly). Wrap the read-merge-write in:
  ```csharp
  using var mutex = new Mutex(false, @"Local\EyeCtrl.Settings.Writer");
  try { mutex.WaitOne(); /* read-merge-write */ }
  finally { mutex.ReleaseMutex(); }
  ```

- [ ] **Step 5: Register the app writer in DI**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, update the `SettingsService` registration (around line 58-62) to pass `SettingsWriter.App`:

```csharp
services.AddSingleton(sp => new SettingsService(
    settingsFilePath,
    SettingsWriter.App,
    sp.GetRequiredService<ILogger<SettingsService>>()));
```

Also update the throwaway startup instance in `App.xaml.cs` (`InitializeSettingsAndLogging`) — it loads only, but pass `SettingsWriter.App` for consistency.

- [ ] **Step 6: Run the new test + full Core test suite**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS (including the ownership test and existing settings tests).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): SettingsService single-writer ownership (SettingsWriter)

SettingsService constructed with SettingsWriter.App|Supervisor. Save()
re-reads on disk under Local\EyeCtrl.Settings.Writer mutex and preserves
sections the caller doesn't own (app never persists Supervision), then
atomic-writes via temp+rename. Prevents cross-process lost updates."
```

---

## Phase 3 — App-side changes (the five in-app changes)

### Task 5: Add the `--paused` launch argument

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:60` (arg parse) and `:181-182` (pause gate)
- Create: `tests/EyeCtrl.App.Tests/Startup/PausedArgTests.cs` (or extend an existing startup test)

**Context (spec, "Paused-retry handoff"):** The supervisor relaunches EyeCtrl with `--paused` so a retried instance is inert (no SendInput) and doesn't fight the fallback AT. This must NOT mutate the user's persisted `StartPaused` (that would get stuck). The startup gate ORs the arg with the setting.

- [ ] **Step 1: Write failing test**

The arg-parsing result needs to be observable. Add a static property `App.IsPausedLaunch` (mirroring the existing `IsTestMode`). Create `tests/EyeCtrl.App.Tests/Startup/PausedArgTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Startup;

// Note: full launch-paused verification is a Category=UI integration test.
// Here we assert the arg is parsed into the static flag.
public class PausedArgTests
{
    [Fact]
    public void IsPausedLaunch_DefaultsFalse()
    {
        Check.That(EyeCtrl.App.App.IsPausedLaunch).IsFalse();
    }
}
```

(If the existing test project already exercises arg parsing, follow its harness instead; the key is the flag exists and defaults false.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~PausedArgTests"`
Expected: FAIL — `IsPausedLaunch` not found.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.App/App.xaml.cs`:
- Add a property near `IsTestMode`: `public static bool IsPausedLaunch { get; private set; }`
- In `OnStartup` (line 60), parse it alongside test-mode:
  ```csharp
  IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
  IsPausedLaunch = e.Args.Contains("--paused", StringComparer.OrdinalIgnoreCase);
  ```
- Change the pause gate (line 181-182) to OR the arg:
  ```csharp
  if (settings.General.StartPaused || IsPausedLaunch)
      im.TogglePause();
  ```

- [ ] **Step 4: Run test, verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~PausedArgTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): --paused launch arg

Relaunch-paused for the supervisor's paused-retry handoff. OR'd into the
existing StartPaused gate; never mutates the persisted setting."
```

### Task 6: Single-instance guard (`Local\` mutex)

**Files:**
- Create: `src/EyeCtrl.App/Services/SingleInstanceGuard.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs` (acquire on startup, exit if held)
- Create: `tests/EyeCtrl.App.Tests/Services/SingleInstanceGuardTests.cs`

- [ ] **Step 1: Write failing test**

Create `tests/EyeCtrl.App.Tests/Services/SingleInstanceGuardTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;

public class SingleInstanceGuardTests
{
    // NOTE: a named Mutex is thread-reentrant — the owning thread can acquire it again.
    // So "two guards on the same test thread both return true" is expected OS behavior,
    // NOT a bug. To test real contention we must acquire the second guard from a
    // DIFFERENT thread. Each test uses a unique mutex name so tests don't interfere.
    private static string UniqueName() => @"Local\EyeCtrl.App.Test." + Guid.NewGuid();

    [Fact]
    public async Task TryAcquire_SecondThread_Fails_WhenFirstHoldsIt()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();
        try
        {
            // A second guard on a separate thread must fail to acquire.
            bool secondResult = await Task.Run(() =>
            {
                using var second = new SingleInstanceGuard(name);
                return second.TryAcquire();
            });
            Check.That(secondResult).IsFalse();
        }
        finally { first.Dispose(); }
    }

    [Fact]
    public void TryAcquire_AfterDispose_SucceedsAgain()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();
        first.Dispose();

        using var second = new SingleInstanceGuard(name);
        Check.That(second.TryAcquire()).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SingleInstanceGuardTests"`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.App/Services/SingleInstanceGuard.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System.Threading;

public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Default production mutex name. Injectable for tests (per-test isolation).</summary>
    public const string DefaultMutexName = @"Local\EyeCtrl.App.Singleton";
    private readonly string _mutexName;
    private Mutex? _mutex;
    private bool _owned;

    public SingleInstanceGuard() : this(DefaultMutexName) { }
    public SingleInstanceGuard(string mutexName) { _mutexName = mutexName; }

    /// <summary>True if this instance acquired the mutex (legitimate to run).</summary>
    public bool TryAcquire()
    {
        // createdNew==true means no prior owner existed anywhere — we got it cleanly.
        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out _owned);
        if (_owned) return true;

        // Already owned by some process. A zero-timeout wait returns true only if we
        // can take it now (e.g. it was released); false if still held. AbandonedMutex
        // means a prior holder was force-killed — treat as acquired (spec, in-app #2).
        try
        {
            _owned = _mutex.WaitOne(0); // RETURN the result — do not discard it.
            return _owned;
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
            return true;
        }
    }

    public void Dispose()
    {
        if (_mutex == null) return;
        try { if (_owned) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
```

- [ ] **Step 4: Wire into App startup**

In `src/EyeCtrl.App/App.xaml.cs` `OnStartup`, near the very top (after arg parse, before the splash work begins — a second instance must exit fast):

```csharp
var singleInstance = new SingleInstanceGuard();
if (!singleInstance.TryAcquire())
{
    // Another instance is already running (likely the supervisor's legitimate one).
    Shutdown();
    return;
}
// Keep singleInstance alive for the process lifetime; dispose in OnExit.
```

Store it in a field `_singleInstance` and dispose it in `OnExit`.

- [ ] **Step 5: Run tests, verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SingleInstanceGuardTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): single-instance Local mutex guard

Second instance exits immediately (supervisor's respawn is legitimate).
AbandonedMutexException from a force-killed holder is treated as acquired."
```

### Task 7: Conditional `SetStartOnBoot` (supervised vs standalone)

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:90-97` (the reconciliation block)
- Create: `tests/EyeCtrl.App.Tests/Startup/StartOnBootReconciliationTests.cs` (logic extracted for testability)

**Context (spec, Blocking #1):** In supervised mode the supervisor owns launch-at-logon, so the app must NOT self-register the HKCU Run key (it would re-add it every launch → double-launch at next logon). When supervised, always call `SetStartOnBoot(false)`; else reconcile as today.

- [ ] **Step 1: Extract the decision into a pure helper and write a failing test**

Create `tests/EyeCtrl.App.Tests/Startup/StartOnBootReconciliationTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;

public class StartOnBootReconciliationTests
{
    [Theory]
    [InlineData(true, true, false)]   // supervised + StartOnBoot on  -> never self-register
    [InlineData(true, false, false)]  // supervised + StartOnBoot off -> never self-register
    [InlineData(false, true, true)]   // standalone + StartOnBoot on  -> register
    [InlineData(false, false, false)] // standalone + StartOnBoot off -> don't register
    public void DecidesSelfRegistration(bool supervised, bool startOnBoot, bool expectRegister)
    {
        Check.That(StartOnBootPolicy.ShouldSelfRegister(supervised, startOnBoot)).IsEqualTo(expectRegister);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~StartOnBootReconciliationTests"`
Expected: FAIL — `StartOnBootPolicy` missing.

- [ ] **Step 3: Implement the policy**

Create `src/EyeCtrl.App/Startup/StartOnBootPolicy.cs`:

```csharp
namespace EyeCtrl.App.Startup;

public static class StartOnBootPolicy
{
    /// <summary>In supervised mode the supervisor owns launch-at-logon; the app never self-registers.</summary>
    public static bool ShouldSelfRegister(bool supervised, bool startOnBoot) => !supervised && startOnBoot;
}
```

- [ ] **Step 4: Apply the policy in App.OnStartup**

In `src/EyeCtrl.App/App.xaml.cs`, replace the unconditional `startupRegistrar.SetStartOnBoot(settings.General.StartOnBoot);` (line 90) with:

```csharp
var startupRegistrar = _serviceProvider.GetRequiredService<IStartupRegistrar>();
var supervised = settings.Supervision.Enabled;
startupRegistrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(supervised, settings.General.StartOnBoot));

settings.General.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(settings.General.StartOnBoot))
        startupRegistrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(supervised, settings.General.StartOnBoot));
};
```

- [ ] **Step 5: Run tests, verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~StartOnBootReconciliationTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "fix(app): conditional SetStartOnBoot under supervised mode

In supervised mode the app never self-registers the HKCU Run key (the
supervisor owns launch-at-logon) — prevents double-launch at next logon.
Standalone reconciles StartOnBoot as before."
```

### Task 8: Heartbeat emitter (Dispatcher stamp + bounded async writer)

**Files:**
- Create: `src/EyeCtrl.App/Services/HeartbeatPublisher.cs`
- Modify: `src/EyeCtrl.Core/GazeEngine.cs` subscription in App to feed `lastFrameUtc`
- Create: `tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs`

**Context (spec, "Heartbeat Liveness" + "Dispatcher must not do sync pipe I/O"):** A `DispatcherTimer` stamps a payload (PID, startedAtUtc, isConnected, isPaused, lastFrameUtc, shuttingDown) every ~3s on the UI thread. A bounded async writer sends it over `eyectrl_watch` with connect/write timeouts, keeping only the newest pending payload. No-ops quietly when no server exists (standalone mode).

- [ ] **Step 1: Write failing test for the payload-stamping behavior**

Create `tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs`. We test the stamping in isolation; construct with `supervised: false` so no pipe-writer task spins up. `LastFrameUtc` is driven by `OnGazeSample()` (the gaze-thread seam), and the test reads it back through the stamped payload:

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;

public class HeartbeatPublisherTests
{
    [Fact]
    public void Stamp_ReflectsLastGazeSample()
    {
        var pub = new HeartbeatPublisher(supervised: false);
        pub.OnGazeSample(); // gaze thread would call this per sample
        pub.Stamp(isConnected: true, isPaused: false);
        var snapshot = pub.CurrentPayload;
        Check.That(snapshot.IsConnected).IsTrue();
        Check.That(snapshot.IsPaused).IsFalse();
        Check.That(snapshot.ShuttingDown).IsFalse();
        // LastFrameUtc should be ~now (OnGazeSample just stamped UtcNow). Allow slack.
        Check.That(snapshot.LastFrameUtc).IsCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SignalShuttingDown_StampsFinalPayload()
    {
        var pub = new HeartbeatPublisher(supervised: false);
        pub.SignalShuttingDown();
        Check.That(pub.CurrentPayload.ShuttingDown).IsTrue();
    }
}
```

(`CurrentPayload` is a read-only accessor exposing the last `_pending` stamp; implement it on the class for testability.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HeartbeatPublisherTests"`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement `HeartbeatPublisher`**

Create `src/EyeCtrl.App/Services/HeartbeatPublisher.cs`. The payload type is shared with the supervisor — define it in a small shared model. To avoid a Core dependency on pipe plumbing, define `HeartbeatPayload` as a plain record in `EyeCtrl.Core/Models/HeartbeatPayload.cs` (both projects reference Core):

```csharp
// src/EyeCtrl.Core/Models/HeartbeatPayload.cs
namespace EyeCtrl.Core.Models;

public record HeartbeatPayload(
    int Pid,
    DateTime StartedAtUtc,
    bool IsConnected,
    bool IsPaused,
    DateTime LastFrameUtc,
    bool ShuttingDown);
```

Then `HeartbeatPublisher`:

```csharp
namespace EyeCtrl.App.Services;

using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Models;

public sealed class HeartbeatPublisher : IDisposable
{
    private const string PipeName = "eyectrl_watch";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly int _pid = Environment.ProcessId;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    // Single-slot writer: the newest stamp overwrites any unsent one. One long-lived
    // writer task drains the slot — no per-stamp Task.Run, no thread-pool starvation.
    // Cross-thread access (gaze thread writes lastFrameTicks, Dispatcher reads/writes)
    // uses Interlocked on a long (ticks), never a plain DateTime field.
    private long _lastFrameTicks = DateTime.UtcNow.Ticks;
    private HeartbeatPayload? _pending;          // next payload to send
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private volatile bool _shuttingDown;
    private readonly CancellationTokenSource _cts = new();
    private Task? _writerLoop;

    public HeartbeatPublisher(bool supervised)
    {
        // In standalone mode (default) there is no server — don't even spin up a writer
        // that would block on Connect forever. Gated on Supervision.Enabled.
        if (supervised) StartWriter();
    }

    /// <summary>Last stamped payload (testability + diagnostics). Null until first Stamp.</summary>
    public HeartbeatPayload? CurrentPayload => _pending;

    /// <summary>Called on the GAZE thread (GazeEngine.GazeSampled) on every sample, valid or invalid.</summary>
    public void OnGazeSample() => Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);

    /// <summary>Called on the UI thread (DispatcherTimer) every ~3s. Stamps; never blocks on IPC.</summary>
    public void Stamp(bool isConnected, bool isPaused)
    {
        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
        _pending = new HeartbeatPayload(_pid, _startedAtUtc, isConnected, isPaused, lastFrame, _shuttingDown);
        _sendSignal.Release(); // wake the single writer; if one is pending, the slot just refreshes
    }

    public void SignalShuttingDown()
    {
        _shuttingDown = true;
        Stamp(isConnected: false, isPaused: false); // ensure a final shuttingDown:true payload is queued
    }

    private void StartWriter() => _writerLoop = Task.Run(WriterLoopAsync);

    private async Task WriterLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await _sendSignal.WaitAsync(_cts.Token); // block until a stamp arrives (or shutdown)
            var payload = _pending;
            _pending = null;
            if (payload == null) continue;
            try { await SendOneAsync(payload, _cts.Token); }
            catch { /* drop this send — never throw, never block the Dispatcher */ }
        }
    }

    private static async Task SendOneAsync(HeartbeatPayload payload, CancellationToken token)
    {
        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        // CANCELLABLE connect with a real timeout — a missing server (standalone) does NOT
        // block a thread-pool thread forever. WaitAsync(cancel) bounds it to ConnectTimeout.
        var connectTask = client.ConnectAsync(token);
        var timeoutTask = Task.Delay(ConnectTimeout, token);
        if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
            return; // server absent or slow — drop this heartbeat
        await connectTask; // observe exceptions if connect threw
        var json = JsonSerializer.Serialize(payload) + "\n";
        await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json), token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _writerLoop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _sendSignal.Dispose();
        _cts.Dispose();
    }
}
```

⚠ **Three design points this fixes vs. the earlier sketch:**
1. **No per-stamp `Task.Run`.** A single long-lived writer drains a one-slot queue (`_pending`); the newest stamp overwrites an unsent one. No thread-pool starvation.
2. **Cancellable `ConnectAsync`.** A missing server (the *default* standalone config, `Supervision.Enabled == false`) no longer blocks a thread-pool thread forever — `Task.WhenAny(connect, Task.Delay(2s))` bounds it, and `StartWriter` is gated on `supervised` so standalone never spins a writer at all.
3. **Cross-thread correctness.** `lastFrameTicks` is a `long` swapped via `Interlocked`; no plain `DateTime` field read/write across gaze/Dispatcher threads.

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HeartbeatPublisherTests"`
Expected: PASS.

- [ ] **Step 5: Wire into App**

In `src/EyeCtrl.App/App.xaml.cs`, after the gaze engine and overlays are up:
- Construct the `HeartbeatPublisher` gated on `settings.Supervision.Enabled`:
  `var publisher = new HeartbeatPublisher(supervised: settings.Supervision.Enabled);`
- Subscribe to `gazeEngine.GazeSampled` (fires per raw sample on the gaze thread, before the pause gate at `GazeEngine.cs:117`): `gazeEngine.GazeSampled += _ => publisher.OnGazeSample();`
- Start a `DispatcherTimer` with ~3s interval whose tick calls `publisher.Stamp(provider.IsConnected, im.CurrentState == InteractionState.Paused)` (no `lastFrameUtc` arg — the publisher sources it from its own `OnGazeSample` stamp).
- In `OnExit`, call `publisher.SignalShuttingDown()` **before** disposing services (this is the authenticated clean-shutdown signal — see Task 9; the supervisor needs it within the 5s deadline).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): heartbeat emitter (Dispatcher stamp + bounded async writer)

HeartbeatPayload in Core.Models (shared with supervisor). DispatcherTimer
stamps on the UI thread; async writer sends over eyectrl_watch with a 2s
timeout, never blocking the Dispatcher. No-ops when no server (standalone).
lastFrameUtc fed by GazeEngine.GazeSampled (any sample, valid or invalid)."
```

### Task 9: Exception handlers + clean-shutdown signal

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` (`InitializeSettingsAndLogging` ~line 318-345, and `OnExit` ~line 215-241)

- [ ] **Step 1: Register `TaskScheduler.UnobservedTaskException`**

In `src/EyeCtrl.App/App.xaml.cs` `InitializeSettingsAndLogging`, alongside the existing `AppDomain.CurrentDomain.UnhandledException` handler (line 332), add:

```csharp
TaskScheduler.UnobservedTaskException += (_, args) =>
{
    Log.Error(args.Exception, "Unobserved task exception (observed set to true to prevent crash)");
    args.SetObserved();
};
```

- [ ] **Step 2: Confirm DispatcherUnhandledException deliberately lets crash**

The existing handler (line 339-342) logs but leaves `args.Handled = false`. This is intentional (spec, in-app change #4: "log + flush + let crash, so the supervisor's respawn owns recovery"). Add a comment making the intent explicit:

```csharp
DispatcherUnhandledException += (_, args) =>
{
    // Deliberately do NOT set args.Handled = true. We log + let the process
    // crash so the supervisor's respawn owns recovery, rather than the app
    // limping on in a corrupt state.
    Log.Error(args.Exception, "Unhandled dispatcher exception");
};
```

- [ ] **Step 3: Wire the clean-shutdown signal into OnExit**

In `OnExit` (line 215-241), as the **first** action (before disposing services), call the heartbeat publisher's `SignalShuttingDown()` from Task 8 and flush (the supervisor needs the `shuttingDown: true` heartbeat to reach it within the 5s deadline). If the publisher isn't constructed (e.g., fatal startup), skip.

- [ ] **Step 4: Build + run app tests**

Run: `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.App.Tests`
Expected: build OK, tests green (no new test here — the handlers are verified by integration; the comment documents intent).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): TaskScheduler.UnobservedTaskException + clean-shutdown signal

Register UnobservedTaskException (log + SetObserved). Document that
DispatcherUnhandledException deliberately lets crash (supervisor owns
recovery). OnExit signals shuttingDown:true to the supervisor first."
```

---

## Phase 4 — Supervisor core

### Task 10: Supervisor project skeleton

**Files:**
- Create: `src/EyeCtrl.Supervisor/EyeCtrl.Supervisor.csproj`
- Create: `src/EyeCtrl.Supervisor/Program.cs`
- Create: `tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Create the csproj**

Create `src/EyeCtrl.Supervisor/EyeCtrl.Supervisor.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\EyeCtrl.Core\EyeCtrl.Core.csproj" />
    <ProjectReference Include="..\EyeCtrl.Platform\EyeCtrl.Platform.csproj" />
    <ProjectReference Include="..\EyeCtrl.Controls\EyeCtrl.Controls.csproj" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create a minimal Program.cs**

Create `src/EyeCtrl.Supervisor/Program.cs`:

```csharp
namespace EyeCtrl.Supervisor;

using System;
using System.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // SupervisorLoggingInitializer.Initialize(...) wired in Task 24.
        using var app = new SupervisorApp();
        app.Initialize();
        return app.Run();
    }
}

internal sealed class SupervisorApp : Application
{
    public void Initialize() { /* wired in later tasks */ }
    public int Run() => Run() is int _ ? 0 : 0; // placeholder until Task 24
}
```

(This placeholder compiles; later tasks fill `SupervisorApp`.)

- [ ] **Step 3: Create the test project**

Create `tests/EyeCtrl.Supervisor.Tests/EyeCtrl.Supervisor.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Supervisor\EyeCtrl.Supervisor.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add both to the solution + build**

In `EyeCtrl.slnx` add under `/src/` and `/tests/`. Run: `dotnet build EyeCtrl.slnx`
Expected: builds.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(supervisor): project skeleton (WinExe, Core+Platform+Controls)"
```

### Task 11: `CrashWindow` (sliding strike counter)

**Files:**
- Create: `src/EyeCtrl.Supervisor/Supervision/CrashWindow.cs`
- Create: `tests/EyeCtrl.Supervisor.Tests/Supervision/CrashWindowTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/EyeCtrl.Supervisor.Tests/Supervision/CrashWindowTests.cs`:

```csharp
namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class CrashWindowTests
{
    // Worked example from spec: maxRetries=3 means 3 strikes permitted, escalate on 4th.
    private static readonly DateTime Base = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldEscalate_False_AtExactlyMaxRetries()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        for (int i = 0; i < 3; i++) w.RecordStrike(Base.AddSeconds(i * 10));
        Check.That(w.ShouldEscalate(Base.AddSeconds(30))).IsFalse();
    }

    [Fact]
    public void ShouldEscalate_True_AtMaxRetriesPlusOne()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        for (int i = 0; i < 4; i++) w.RecordStrike(Base.AddSeconds(i * 10));
        Check.That(w.ShouldEscalate(Base.AddSeconds(35))).IsTrue();
    }

    [Fact]
    public void OldStrikes_ExpireOutOfWindow()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        for (int i = 0; i < 3; i++) w.RecordStrike(Base.AddSeconds(i * 10));
        // 200s later — all strikes aged out
        Check.That(w.ShouldEscalate(Base.AddSeconds(200))).IsFalse();
    }

    [Fact]
    public void Reset_ClearsStrikes()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        w.RecordStrike(Base);
        w.Reset();
        Check.That(w.ShouldEscalate(Base)).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~CrashWindowTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.Supervisor/Supervision/CrashWindow.cs`:

```csharp
namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;

public sealed class CrashWindow
{
    private readonly int _maxRetries;
    private readonly int _windowSeconds;
    private readonly List<DateTime> _strikes = new();

    public CrashWindow(int maxRetries, int windowSeconds)
    {
        _maxRetries = maxRetries;
        _windowSeconds = windowSeconds;
    }

    public void RecordStrike(DateTime at) => _strikes.Add(at);

    public void Reset() => _strikes.Clear();

    public bool ShouldEscalate(DateTime now)
    {
        var cutoff = now.AddSeconds(-_windowSeconds);
        // Strictly greater than maxRetries: maxRetries attempts are always permitted.
        int inWindow = 0;
        foreach (var s in _strikes)
            if (s > cutoff) inWindow++;
        return inWindow > _maxRetries;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~CrashWindowTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(supervisor): CrashWindow sliding strike counter

ShouldEscalate() returns true only when in-window strikes > maxRetries
(3 strikes permitted, 4th escalates). Strikes age out past the window."
```

### Task 12: `BackoffPolicy`

**Files:**
- Create: `src/EyeCtrl.Supervisor/Supervision/BackoffPolicy.cs`
- Create: `tests/EyeCtrl.Supervisor.Tests/Supervision/BackoffPolicyTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class BackoffPolicyTests
{
    [Fact]
    public void Curve_IsImmediate_5_15_60_60()
    {
        var b = new BackoffPolicy();
        Check.That(b.DelayFor(consecutiveFailures: 1)).IsEqualTo(TimeSpan.Zero);
        Check.That(b.DelayFor(2)).IsEqualTo(TimeSpan.FromSeconds(5));
        Check.That(b.DelayFor(3)).IsEqualTo(TimeSpan.FromSeconds(15));
        Check.That(b.DelayFor(4)).IsEqualTo(TimeSpan.FromSeconds(60));
        Check.That(b.DelayFor(5)).IsEqualTo(TimeSpan.FromSeconds(60)); // capped
    }
}
```

- [ ] **Step 2: Run to verify it fails** → FAIL.

- [ ] **Step 3: Implement**

```csharp
namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;

public sealed class BackoffPolicy
{
    private static readonly List<TimeSpan> Curve = new()
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60)
    };

    public TimeSpan DelayFor(int consecutiveFailures)
    {
        var idx = Math.Min(consecutiveFailures - 1, Curve.Count - 1);
        return idx < 0 ? TimeSpan.Zero : Curve[idx];
    }
}
```

- [ ] **Step 4: Run, verify pass** → commit:

```bash
git add -A
git commit -m "feat(supervisor): BackoffPolicy 0/5/15/60s capped curve"
```

### Task 12b: `HeartbeatFreshnessEvaluator` (gaze-stream-death detection)

**Files:**
- Create: `src/EyeCtrl.Supervisor/Supervision/HeartbeatFreshnessEvaluator.cs`
- Create: `tests/EyeCtrl.Supervisor.Tests/Supervision/HeartbeatFreshnessEvaluatorTests.cs`

**Context (spec, "Gaze-stream-death detection" — the single most safety-critical section):** This logic was implicit in Task 13's `GazeStreamDead` input but **nothing computed it**. It must be pure, owned, and tested in isolation — getting it wrong force-kills the app when the user merely looks away. The rule: a heartbeat is gaze-dead iff `now - lastFrameUtc > hangThreshold` **while `isConnected == true`**. `lastFrameUtc` is updated on *every* sample (valid or invalid), so look-away (which still produces invalid frames) does NOT trip it. `isConnected == false` is a *soft* signal (no kill) — the app's reconnect loop owns it.

- [ ] **Step 1: Write failing tests**

Create `tests/EyeCtrl.Supervisor.Tests/Supervision/HeartbeatFreshnessEvaluatorTests.cs`:

```csharp
namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class HeartbeatFreshnessEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);
    private const int HangSeconds = 20;

    [Fact]
    public void NotDead_FreshFramesAndConnected()
    {
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: Now, isConnected: true)).IsFalse();
    }

    [Fact]
    public void Dead_StaleFramesWhileConnected()
    {
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        // 21s gap > 20s threshold, still connected -> wedged poll loop.
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: Now.AddSeconds(-21), isConnected: true)).IsTrue();
    }

    [Fact]
    public void NotDead_StaleFramesButDisconnected_SoftSignalOnly()
    {
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        // Even a long gap is NOT a hard kill when disconnected — app's reconnect loop owns it.
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: Now.AddSeconds(-60), isConnected: false)).IsFalse();
    }

    [Fact]
    public void Regression_LookAway_StillProducesFrames_NotDead()
    {
        // The Tobii provider forwards INVALID samples as GazePoint(0,0,ts,false) so
        // frame arrival continues while the user looks away / blinks. Those invalid
        // samples still advance lastFrameUtc. Simulate frames arriving every 33ms:
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        var lastFrame = Now.AddMilliseconds(-33); // recent invalid-sample frame
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: lastFrame, isConnected: true)).IsFalse();
    }

    [Fact]
    public void HeartbeatFreshness_UsesDeliveryTime_NotStampTime()
    {
        // The supervisor's lastHeartbeatUtc is pipe-RECEIPT time (delivery), not the
        // app's stamp time. A sustained pure delivery stall is indistinguishable from a
        // frozen UI here — but IsGazeStreamDead keys off the heartbeat's lastFrameUtc
        // field (which the app stamps on the Dispatcher), so it reflects app liveness.
        var e = new HeartbeatFreshnessEvaluator(HangSeconds);
        // lastFrameUtc fresh even if delivery was slow -> not dead.
        Check.That(e.IsGazeStreamDead(Now, lastFrameUtc: Now, isConnected: true)).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~HeartbeatFreshnessEvaluatorTests"`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.Supervisor/Supervision/HeartbeatFreshnessEvaluator.cs`:

```csharp
namespace EyeCtrl.Supervisor.Supervision;

using System;

public sealed class HeartbeatFreshnessEvaluator
{
    private readonly int _hangThresholdSeconds;

    public HeartbeatFreshnessEvaluator(int hangThresholdSeconds)
    {
        _hangThresholdSeconds = hangThresholdSeconds;
    }

    /// <summary>
    /// True only when the SDK has STOPPED delivering frames while still reporting connected.
    /// Keys off frame ARRIVAL (valid or invalid samples both advance lastFrameUtc), so a
    /// user looking away — which still produces invalid GazePoint(0,0,ts,false) frames —
    /// does NOT trip this. A disconnected tracker is a soft signal (handled by the app's
    /// own reconnect loop) and never returns true here.
    /// </summary>
    public bool IsGazeStreamDead(DateTime nowUtc, DateTime lastFrameUtc, bool isConnected)
    {
        if (!isConnected) return false; // soft signal — not our kill to make
        return (nowUtc - lastFrameUtc).TotalSeconds > _hangThresholdSeconds;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~HeartbeatFreshnessEvaluatorTests"`
Expected: PASS. The Task 20 supervision loop calls `IsGazeStreamDead` per received heartbeat and, when true, feeds `new GazeStreamDead(nowUtc)` into the state machine.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(supervisor): HeartbeatFreshnessEvaluator (gaze-stream-death)

Pure evaluator: dead iff now-lastFrameUtc > threshold WHILE connected.
Keys off frame arrival (valid or invalid) so look-away doesn't force-kill.
Disconnected is soft (app's reconnect loop owns it). Regression test for
the invalid-sample-still-produces-frames property (spec's #1 safety case)."
```

### Task 13: `SupervisionStateMachine`

**Files:**
- Create: `src/EyeCtrl.Supervisor/Supervision/SupervisorState.cs`
- Create: `src/EyeCtrl.Supervisor/Supervision/SupervisionInputs.cs`
- Create: `src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs`
- Create: `tests/EyeCtrl.Supervisor.Tests/Supervision/SupervisionStateMachineTests.cs`

**Context:** The brain. Pure, clock-driven, unit-testable. It takes inputs (heartbeat received, heartbeat stale, process exited, gaze-stream dead, clean-exit signal, now) and decides the next state + action. Process launch/kill live in `ProcessController` (Task 14); the state machine commands it via an interface seam.

- [ ] **Step 1: Write failing tests for the key transitions**

Create `tests/EyeCtrl.Supervisor.Tests/Supervision/SupervisionStateMachineTests.cs`. Use a fake clock and a recording command-sink:

```csharp
namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class SupervisionStateMachineTests
{
    private static readonly DateTime T0 = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

    // Spec: IDLE_WATCH is the entry state, not Launching — so supervised + StartOnBoot-off
    // stays idle instead of launching. Only a LaunchRequest (logon+StartOnBoot, or tray
    // "Launch EyeCtrl now") or an AdoptRequest (manually-launched app's first heartbeat)
    // leaves idle.
    [Fact]
    public void StartsIn_IdleWatch()
    {
        var sm = NewMachine();
        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
    }

    [Fact]
    public void LaunchRequest_FromIdle_GoesLaunching_CommandsLaunchApp()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        Check.That(sm.State).IsEqualTo(SupervisorState.Launching);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Launching_FirstHeartbeat_GoesSupervising()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(10)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);
    }

    [Fact]
    public void AdoptRequest_FromIdle_GoesSupervising_NoLaunchCommand()
    {
        // Manual-launch adopt path: app we didn't launch sends a heartbeat.
        var sm = NewMachine();
        sm.Handle(new AdoptRequest(T0));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Supervising_HeartbeatStale_GoesRecovering_CommandsForceKill()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new HeartbeatStale(T0.AddSeconds(22)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.Commands).Contains(SupervisorCommand.ForceKill);
    }

    [Fact]
    public void CrashLoop_BeyondMaxRetries_GoesFallenBack()
    {
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        for (int i = 1; i <= 4; i++)
            sm.Handle(new ProcessExited(T0.AddSeconds(i))); // strikes 1..4 (> maxRetries 3 -> escalate)
        Check.That(sm.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchFallbackAt);
    }

    [Fact]
    public void SurvivedWindow_ResetsStrikeAndBackoffCounters()
    {
        // Spec reset rule (no infinite-restart hole): surviving a FULL crash window (120s)
        // in Supervising resets both the strike counter and the backoff counter.
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new ProcessExited(T0.AddSeconds(1)));   // strike 1, backoff -> 5s
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.FromSeconds(5));
        sm.Handle(new LaunchRequest(T0.AddSeconds(10)));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(11)));
        // Now survive > 120s cleanly:
        sm.Handle(new SurvivedWindow(T0.AddSeconds(200)));
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.Zero); // backoff reset to immediate
        // And a subsequent single crash should not escalate (strikes reset):
        sm.Handle(new ProcessExited(T0.AddSeconds(201)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void CleanExit_AuthenticatedWithinDeadline_GoesIdleWatch_NoRelaunch()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5)));
        sm.Handle(new ProcessExited(T0.AddSeconds(6))); // exits within deadline
        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void CleanExit_NotExitedWithinDeadline_GoesRecovering()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5)));
        sm.Handle(new DeadlineElapsed(T0.AddSeconds(11))); // > 5s, still alive
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void UnannouncedProcessExit_GoesRecovering_NotIdle()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new ProcessExited(T0.AddSeconds(5))); // no CleanExitRequested
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    private static SupervisionStateMachine NewMachine(int maxRetries = 3, int windowSeconds = 120)
        => new(maxRetries, windowSeconds, hangThresholdSeconds: 20, startupGraceSeconds: 60, windowResetSeconds: windowSeconds);
}
```

**JSON casing note:** `HeartbeatPayload` uses PascalCase properties (`IsPaused`, `LastFrameUtc`) per C# convention; the spec's JSON shows camelCase (`isPaused`, `lastFrameUtc`). `SettingsService`'s `JsonNamingPolicy.CamelCase` serializes/deserializes symmetrically, so no hand-alignment of casing is needed — but the supervisor's `HeartbeatPipeServer` must deserialize with the **same camelCase policy** (construct matching `JsonSerializerOptions`) or the app's heartbeat fields will deserialize as default/zero. Match the options in Task 15.

- [ ] **Step 2: Run to verify it fails** → FAIL.

- [ ] **Step 3: Implement the real types**

Create `src/EyeCtrl.Supervisor/Supervision/SupervisorState.cs`:

```csharp
namespace EyeCtrl.Supervisor.Supervision;

public enum SupervisorState
{
    IdleWatch, Launching, Supervising, Recovering, FallenBack, CleanExitPending
}

public enum SupervisorCommand
{
    None, LaunchApp, ForceKill, LaunchFallbackAt, NotifyCleanExit
}
```

Create `src/EyeCtrl.Supervisor/Supervision/SupervisionInputs.cs` (the discriminated inputs as a small class hierarchy):

```csharp
namespace EyeCtrl.Supervisor.Supervision;

using System;

public abstract record SupervisionInput(DateTime At);
public sealed record LaunchRequest(DateTime At) : SupervisionInput(At);     // logon+StartOnBoot / tray "Launch now"
public sealed record AdoptRequest(DateTime At) : SupervisionInput(At);      // manual-launch first heartbeat
public sealed record HeartbeatReceived(DateTime At) : SupervisionInput(At);
public sealed record HeartbeatStale(DateTime At) : SupervisionInput(At);
public sealed record GazeStreamDead(DateTime At) : SupervisionInput(At);
public sealed record ProcessExited(DateTime At) : SupervisionInput(At);
public sealed record CleanExitRequested(DateTime At) : SupervisionInput(At);
public sealed record DeadlineElapsed(DateTime At) : SupervisionInput(At);
public sealed record SurvivedWindow(DateTime At) : SupervisionInput(At);    // survived a full crash window -> reset
```

Create `src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs`. ⚠ **Spec-fidelity points addressed vs. the earlier sketch:**
- Entry state is **`IdleWatch`**, not `Launching` (spec state diagram line 141).
- `IdleWatch → Launching` via `LaunchRequest`; `IdleWatch → Supervising` via `AdoptRequest`.
- Tracks `consecutiveFailures`, exposes `NextBackoffDelay` via `BackoffPolicy`, and **calls `_crash.Reset()` + resets backoff on `SurvivedWindow`** (spec reset rule, line 191/289/295 — the earlier sketch never reset anything).
- `Handle(input)` is single-arg (the input carries `.At`); no 2-arg overload.

```csharp
namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;

public sealed class SupervisionStateMachine
{
    private readonly CrashWindow _crash;
    private readonly BackoffPolicy _backoff = new();
    private readonly int _windowResetSeconds;
    private readonly List<SupervisorCommand> _commands = new();
    private DateTime? _supervisingSince;

    public SupervisorState State { get; private set; } = SupervisorState.IdleWatch; // spec entry state
    public IReadOnlyList<SupervisorCommand> Commands => _commands;
    public DateTime? LastHeartbeatUtc { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public TimeSpan NextBackoffDelay => _backoff.DelayFor(ConsecutiveFailures + 1);

    public SupervisionStateMachine(int maxRetries, int windowSeconds, int hangThresholdSeconds,
        int startupGraceSeconds, int windowResetSeconds)
    {
        _crash = new CrashWindow(maxRetries, windowSeconds);
        _windowResetSeconds = windowResetSeconds;
    }

    public void Handle(SupervisionInput input)
    {
        _commands.Clear();
        switch (input)
        {
            case LaunchRequest when State == SupervisorState.IdleWatch:
                State = SupervisorState.Launching;
                _commands.Add(SupervisorCommand.LaunchApp);
                break;

            case AdoptRequest when State == SupervisorState.IdleWatch:
                State = SupervisorState.Supervising;
                _supervisingSince = input.At;
                break;

            case HeartbeatReceived when State == SupervisorState.Launching:
                LastHeartbeatUtc = input.At;
                State = SupervisorState.Supervising;
                _supervisingSince = input.At;
                break;

            case HeartbeatReceived when State is SupervisorState.Supervising or SupervisorState.CleanExitPending:
                LastHeartbeatUtc = input.At;
                break;

            case SurvivedWindow when State == SupervisorState.Supervising:
                // Spec reset rule: surviving a full crash window resets BOTH counters.
                _crash.Reset();
                ConsecutiveFailures = 0;
                _supervisingSince = input.At;
                break;

            case HeartbeatStale when State == SupervisorState.Supervising:
            case GazeStreamDead when State == SupervisorState.Supervising:
                GoRecovering(input.At, forceKill: true);
                break;

            case CleanExitRequested when State == SupervisorState.Supervising:
                State = SupervisorState.CleanExitPending;
                _commands.Add(SupervisorCommand.NotifyCleanExit);
                break;

            case ProcessExited when State == SupervisorState.CleanExitPending:
                State = SupervisorState.IdleWatch; // authenticated clean exit within deadline
                _supervisingSince = null;
                break;

            case ProcessExited when State is SupervisorState.Supervising or SupervisorState.Recovering or SupervisorState.Launching:
                GoRecovering(input.At, forceKill: false); // unannounced exit / startup crash
                break;

            case DeadlineElapsed when State == SupervisorState.CleanExitPending:
                GoRecovering(input.At, forceKill: true); // claimed shutdown but didn't exit
                break;
        }
    }

    private void GoRecovering(DateTime at, bool forceKill)
    {
        ConsecutiveFailures++;
        _crash.RecordStrike(at);
        if (_crash.ShouldEscalate(at))
        {
            State = SupervisorState.FallenBack;
            _commands.Add(SupervisorCommand.LaunchFallbackAt);
        }
        else
        {
            State = SupervisorState.Recovering;
            if (forceKill) _commands.Add(SupervisorCommand.ForceKill);
            _commands.Add(SupervisorCommand.LaunchApp);
        }
        _supervisingSince = null;
    }
}
```

(The Task 20 supervision loop: after each successful launch → first heartbeat, record `_supervisingSince`; a periodic clock tick that sees `State == Supervising && now - supervisingSince > windowResetSeconds` emits `new SurvivedWindow(now)` to drive the reset.)

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~SupervisionStateMachineTests"`
Expected: PASS. Iterate until all six test cases pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(supervisor): SupervisionStateMachine (6 states, authenticated clean-exit)

Pure, clock-driven state machine. Commands: ForceKill/LaunchApp/
LaunchFallbackAt. Crash-loop escalation via CrashWindow. Clean exit is
authenticated (CleanExitRequested) + deadline-gated; unannounced exit
stays a failure. IDLE_WATCH adopt path for manually-launched app."
```

### Task 14: `ProcessController`

**Files:**
- Create: `src/EyeCtrl.Supervisor/Supervision/ProcessController.cs`

- [ ] **Step 1: Implement**

Create `src/EyeCtrl.Supervisor/Supervision/ProcessController.cs`. Uses the recorded `Process` handle (not name discovery — spec, Process Binding), with `StartInfo.Arguments` set to `--paused` when a paused-retry is requested:

```csharp
namespace EyeCtrl.Supervisor.Supervision;

using System.Diagnostics;

public sealed class ProcessController
{
    private Process? _process;
    public Process? BoundProcess => _process;
    public DateTime? StartTimeUtc { get; private set; }

    public void Launch(string exePath, bool paused)
    {
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
        if (paused) psi.ArgumentList.Add("--paused");
        _process = Process.Start(psi);
        StartTimeUtc = _process?.StartTime.ToUniversalTime();
    }

    public bool IsAlive => _process is { HasExited: false };

    public void ForceKill()
    {
        if (_process is { HasExited: false })
            _process.Kill(entireProcessTree: true);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/EyeCtrl.Supervisor/EyeCtrl.Supervisor.csproj`
Expected: OK. (Live process launch is exercised in integration; unit-test ProcessController with a fake if desired.)

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(supervisor): ProcessController (launch --paused, handle-bound IsAlive)"
```

---

## Phase 5 — IPC, binding, fallback, UI

### Task 15: Heartbeat pipe server (`eyectrl_watch`)

**Files:**
- Create: `src/EyeCtrl.Supervisor/Ipc/HeartbeatPipeServer.cs`

- [ ] **Step 1: Implement, modeled on `ExternalDwellService`**

Create `src/EyeCtrl.Supervisor/Ipc/HeartbeatPipeServer.cs`. Async accept loop, one `NamedPipeServerStream` per client (`MaxAllowedServerInstances`, `PipeOptions.Asynchronous`), newline-JSON reads via `StreamReader.ReadLineAsync`. On each received line, deserialize `HeartbeatPayload` (from `EyeCtrl.Core.Models`) and raise an event with the payload + the bound client identity (Task 16). Tolerate zero connections (app not running). Full `IDisposable` mirroring `ExternalDwellService`.

**Deserialize with camelCase policy** matching the app's emitter (`JsonNamingPolicy.CamelCase` + `JsonStringEnumConverter`) — see the JSON casing note in Task 13 — or the camelCase heartbeat fields (`isPaused`, `lastFrameUtc`, `shuttingDown`) will deserialize to defaults.

- [ ] **Step 2: Build**

Run: `dotnet build src/EyeCtrl.Supervisor/EyeCtrl.Supervisor.csproj`
Expected: OK.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(supervisor): HeartbeatPipeServer (eyectrl_watch, async, multi-client)"
```

### Task 16: Process binding (P/Invokes + identity checks)

**Files:**
- Create: `src/EyeCtrl.Platform/Interop/ProcessIdentity.cs`
- Create: `src/EyeCtrl.Platform/Interop/NamedPipeSecurityBuilder.cs`
- Create: `src/EyeCtrl.Supervisor/Ipc/ClientIdentityBinder.cs`
- Create: `tests/EyeCtrl.Platform.Tests/Interop/ProcessIdentityTests.cs` (where unit-testable)

**Context (spec, Process Binding):** trust nothing the pipe says until verified. Read the kernel client PID via `GetNamedPipeClientProcessId`; verify same session, same user, exe path matches the launched app, creation time matches. Restrictive per-user pipe ACL. Re-verify on every reconnect.

- [ ] **Step 1: Add the P/Invokes**

Create `src/EyeCtrl.Platform/Interop/ProcessIdentity.cs` using the `[LibraryImport]` style of `NativeMethods.cs`. ⚠ **Three correctness points** verified against `NativeMethods.cs:102-111`:
- `[LibraryImport]` does **not** support `StringBuilder` (SYSLIB1051) — that file documents it. Buffer-return params must be `[Out] char[]`.
- Every `bool`-returning P/Invoke in the file carries `[return: MarshalAs(UnmanagedType.Bool)]` — add it to all three here (`GetNamedPipeClientProcessId`, `ProcessIdToSessionId`, `QueryFullProcessImageName`).
- `dwFlags` is a plain `DWORD` (`uint`) — do **not** put `[MarshalAs(UnmanagedType.Bool)]` on it (that was wrong).

```csharp
namespace EyeCtrl.Platform.Interop;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static partial class ProcessIdentity
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    // [Out] char[] — NOT StringBuilder (SYSLIB1051). See NativeMethods.cs:102.
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
        [Out] char[] lpExeName, ref uint lpdwSize);
}
```

- [ ] **Step 2: Create the pipe ACL builder**

Create `src/EyeCtrl.Platform/Interop/NamedPipeSecurityBuilder.cs` returning a `PipeSecurity` that grants access only to the current user's SID (deny NETWORK/other users). ⚠ **Do not copy the `ExternalDwellService` pipe-construction template verbatim here** — that template creates a plain `NamedPipeServerStream` with *no* `PipeSecurity`. A per-user ACL requires `NamedPipeServerStreamAcl.Create(...)` (from the `System.IO.Pipes.AccessControl` assembly), not the plain constructor:

```csharp
// NamedPipeSecurityBuilder returns a PipeSecurity (current-user ReadWrite, no NETWORK).
// The pipe server must construct with:
var server = NamedPipeServerStreamAcl.Create(
    HeartbeatPipeServer.PipeName, PipeDirection.InOut,
    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous, 0, 0, securityBuilder.BuildCurrentUserOnly());
```

This keeps cross-user/session/remote spoofers off the pipe (spec, Process Binding).

- [ ] **Step 3: Implement `ClientIdentityBinder`**

Create `src/EyeCtrl.Supervisor/Ipc/ClientIdentityBinder.cs`. Given a `SafePipeHandle` and the expected app exe path + the `ProcessController`'s recorded start time, return a bound identity (PID, session, user, exe path, creation time) or null if any check fails. Treat any P/Invoke failure as "unbound" (ignore heartbeats, do not crash the server).

- [ ] **Step 4: Build + any unit-testable parts**

Run: `dotnet build EyeCtrl.slnx`
Expected: OK.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(platform,supervisor): process binding + restrictive pipe ACL

GetNamedPipeClientProcessId/ProcessIdToSessionId/QueryFullProcessImageName
P/Invokes. ClientIdentityBinder verifies session/user/exe-path/creation-time;
unverified clients' heartbeats ignored. Per-user pipe ACL denies spoofers."
```

### Task 17: Fallback AT launcher

**Files:**
- Create: `src/EyeCtrl.Supervisor/Fallback/FallbackCandidate.cs`
- Create: `src/EyeCtrl.Supervisor/Fallback/FallbackAtLauncher.cs`
- Create: `tests/EyeCtrl.Supervisor.Tests/Fallback/FallbackAtLauncherTests.cs`

**Context:** Probes TD Control → Snap Core First → Windows Eye Control; launches the first installed. **Windows Eye Control's launch mechanism depends on spike S1** — record its outcome here and adjust the third probe accordingly.

- [ ] **Step 1: Write failing test for ordering (with fake probes)**

Create `tests/EyeCtrl.Supervisor.Tests/Fallback/FallbackAtLauncherTests.cs`:

```csharp
namespace EyeCtrl.Supervisor.Tests.Fallback;

using EyeCtrl.Supervisor.Fallback;

public class FallbackAtLauncherTests
{
    [Fact]
    public void Launches_FirstInstalled_InOrder()
    {
        var candidates = new[]
        {
            Fake("TDControl", installed: false),
            Fake("SnapCoreFirst", installed: true),
            Fake("WindowsEyeControl", installed: true)
        };
        var launched = FallbackAtLauncher.LaunchFirstInstalled(candidates);
        Check.That(launched).IsNotNull();
        Check.That(launched!.Id).IsEqualTo("SnapCoreFirst");
    }

    [Fact]
    public void ReturnsNull_WhenNoneInstalled()
    {
        var candidates = new[] { Fake("TDControl", false), Fake("SnapCoreFirst", false), Fake("WindowsEyeControl", false) };
        Check.That(FallbackAtLauncher.LaunchFirstInstalled(candidates)).IsNull();
    }

    private static FallbackCandidate Fake(string id, bool installed) =>
        new(id, isInstalled: () => installed, launch: () => { });
}
```

- [ ] **Step 2: Run to verify it fails** → FAIL.

- [ ] **Step 3: Implement**

Create `src/EyeCtrl.Supervisor/Fallback/FallbackCandidate.cs`:

```csharp
namespace EyeCtrl.Supervisor.Fallback;

public sealed class FallbackCandidate
{
    public string Id { get; }
    public Func<bool> IsInstalled { get; }
    public Action Launch { get; }
    public FallbackCandidate(string id, Func<bool> isInstalled, Action launch)
    { Id = id; IsInstalled = isInstalled; Launch = launch; }
}
```

Create `src/EyeCtrl.Supervisor/Fallback/FallbackAtLauncher.cs` with `LaunchFirstInstalled(IEnumerable<FallbackCandidate>)` returning the first whose `IsInstalled()` is true (after calling `Launch()`), else null. Add a factory `BuildDefaultCandidates()` that constructs the real probes: TD Control (`HKLM\SOFTWARE\Tobii Dynavox\TD Control`), Snap Core First (same path root), Windows Eye Control (per spike S1 outcome).

- [ ] **Step 4: Run tests, verify pass** → commit:

```bash
git add -A
git commit -m "feat(supervisor): FallbackAtLauncher (TD/Snap/WinEyeControl probe+launch)"
```

### Task 18: Paused-retry handoff

**Files:**
- Modify: `src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs` (handle `isPaused` — paused app is healthy-but-inert)
- Create: `tests/EyeCtrl.Supervisor.Tests/Supervision/PausedRetryTests.cs`

**Context (spec, Paused-retry handoff):** "Retry EyeCtrl (once)" relaunches with `--paused` (ProcessController.Task 14 already supports it). A paused app (`isPaused == true`) with fresh frames stays SUPERVISING — never killed for "no cursor movement." Resume (`isPaused → false`) does not itself kill the fallback AT unless configured.

- [ ] **Step 1: Add a test asserting a paused app with fresh frames is not killed**

```csharp
// In PausedRetryTests.cs:
[Fact]
public void PausedApp_WithFreshFrames_StaysSupervising()
{
    var sm = new SupervisionStateMachine(3, 120, 20, 60);
    sm.Handle(new HeartbeatReceived(T0)); // -> Supervising
    // A paused-but-healthy heartbeat (frames fresh, isPaused true) does not trip HeartbeatStale.
    // The caller only raises HeartbeatStale when lastFrameUtc is older than threshold — paused apps keep streaming frames.
    Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);
}
```

- [ ] **Step 2: Verify the `--paused` relaunch path**

In the wiring that handles `SupervisorCommand.LaunchApp` after a "Retry EyeCtrl (once)", pass `paused: true` to `ProcessController.Launch`. Ensure the state machine treats `isPaused` as healthy (no false `HeartbeatStale`): the supervisor raises `HeartbeatStale` based on heartbeat timestamp, **not** on `isPaused`. Add a comment in the state machine: "pause gates SendInput, not the gaze stream."

- [ ] **Step 3: Build + test** → commit:

```bash
git add -A
git commit -m "feat(supervisor): paused-retry handoff (--paused relaunch, isPaused is healthy)"
```

### Task 19: Supervisor tray + crash-recovery dialog

**Files:**
- Create: `src/EyeCtrl.Supervisor/UI/SupervisorTrayIcon.cs`
- Create: `src/EyeCtrl.Supervisor/UI/FallenBackDialog.xaml` + `.cs`
- Create: `src/EyeCtrl.Supervisor/UI/SupervisorSetupWindow.xaml` + `.cs`

- [ ] **Step 1: Tray icon (model on `TrayIconManager`)**

Create `src/EyeCtrl.Supervisor/UI/SupervisorTrayIcon.cs` — `NotifyIcon` + `ContextMenuStrip` with items: "EyeCtrl Supervisor settings", "Launch EyeCtrl now", "Exit supervisor". Dispose sets `Visible=false` before `Dispose()`.

- [ ] **Step 2: Crash-recovery dialog**

Create `src/EyeCtrl.Supervisor/UI/FallenBackDialog.xaml` — non-modal, topmost, large dwell-enabled buttons "Retry EyeCtrl (once)" and "Dismiss (keep fallback)" using `DwellButton` from `EyeCtrl.Controls`. Code-behind raises events the supervisor host subscribes to.

- [ ] **Step 3: Setup window**

Create `src/EyeCtrl.Supervisor/UI/SupervisorSetupWindow.xaml` with the controls from the spec table (Enable supervision, Start EyeCtrl at logon, Fallback AT priority list, Advanced thresholds, Diagnostics). All buttons are `DwellButton`s. Writes go through the supervisor's `SettingsService` (`SettingsWriter.Supervisor`) so only the `Supervision` section is touched.

- [ ] **Step 4: Build** → commit:

```bash
git add -A
git commit -m "feat(supervisor): tray, FALLEN_BACK dialog, setup window (dwell-operable)"
```

### Task 20: Supervisor entry point + lifecycle wiring

**Files:**
- Create: `src/EyeCtrl.Supervisor/SupervisorLoggingInitializer.cs`
- Modify: `src/EyeCtrl.Supervisor/Program.cs`

- [ ] **Step 1: Logging**

Create `src/EyeCtrl.Supervisor/SupervisorLoggingInitializer.cs` modeled on `LoggingInitializer`, writing to `%AppData%\EyeCtrl\logs\supervisor-.log` (7-day retention).

- [ ] **Step 2: Wire `SupervisorApp`**

In `Program.cs`/`SupervisorApp`: initialize logging; construct the state machine, `ProcessController`, `HeartbeatPipeServer`, `ClientIdentityBinder`, `FallbackAtLauncher`, tray; run the supervision loop on a timer (evaluate state-machine inputs from the pipe + a clock). Detect clean-exit deadlines; show the FALLEN_BACK dialog on `FallenBack`.

- [ ] **Step 3: Build + manual smoke test**

Run: `dotnet build EyeCtrl.slnx`
Expected: OK. (Full integration testing is manual: run supervisor, confirm it launches the app, kill the app, confirm relaunch, force a crash-loop, confirm fallback.)

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(supervisor): entry point, logging, lifecycle wiring (supervision loop)"
```

---

## Spikes (run FIRST; outcomes adjust later tasks)

### Task S1: Windows Eye Control launch spike

**Goal:** Determine a reliable programmatic launch vector for Windows Eye Control (Win10 1709+/Win11), the guaranteed last-resort fallback.

- [ ] **Step 1:** Spike candidates: `easeofaccess` / `IShellDispatch6`, the `GazeInput` accessibility API, `ms-cortana:eyecontrol`. Test each on a Win11 machine.
- [ ] **Step 2:** Record the verified mechanism (or "none reliable") in a comment at the top of `FallbackAtLauncher.cs` and in the spec's Open Questions.
- [ ] **Outcome:** If none reliable, Windows Eye Control drops to "documented manual fallback"; Goal #5 rests on a Tobii AT being installed — surface as a setup recommendation in `SupervisorSetupWindow`.

### Task S2: Tobii multi-subscriber spike

**Goal:** Verify two processes can `tobii_device_create` + `tobii_gaze_point_subscribe` to the same device concurrently.

- [ ] **Step 1:** Write a throwaway two-process probe (or run the app + a minimal subscriber simultaneously) and observe for `TOBII_ERROR_INSUFFICIENT_LICENSE` / contention.
- [ ] **Step 2:** Record the outcome in the spec's Open Question #2.
- [ ] **Outcome:** If it fails, Task 18's paused-retry must additionally stop EyeCtrl's gaze provider while paused-pending-handoff (release the device, re-acquire on resume).

---

## Self-Review

**1. Spec coverage:**
- Controls extraction (finding #5) → Tasks 1–2 ✓
- Single-writer / lost-update (finding #2) → Task 4 ✓
- Clean-exit authenticated (#1) → Task 13 (CleanExitRequested + deadline) ✓
- Dispatcher-stamped heartbeat (#3) → Task 8 ✓
- Process binding (#4) → Task 16 ✓
- `--paused` arg → Task 5 ✓
- Single-instance mutex → Task 6 ✓
- Conditional SetStartOnBoot (#1 blocking) → Task 7 ✓
- State machine + crash window + backoff → Tasks 11–13 ✓
- **Gaze-stream-death detection (spec's #1 safety case) → Task 12b `HeartbeatFreshnessEvaluator`** ✓ (extracted + tested, not buried in the loop)
- **Counter reset on survived-window (spec reset rule) → Task 13 `SurvivedWindow` input + `_crash.Reset()` + backoff reset** ✓
- **IDLE_WATCH entry state + launch/adopt transitions → Task 13** ✓ (matches spec state diagram, not the old `Launching` default)
- Paused-retry handoff → Task 18 ✓
- Fallback AT + spikes → Tasks 17, S1 ✓
- Supervisor UI/tray → Tasks 19–20 ✓
- Tobii multi-subscriber spike → Task S2 ✓

**2. Placeholder scan:** The `SupervisorApp` async-loop wiring (Task 20) remains sketched at the structural level — genuine integration work (clock ticks, `SurvivedWindow` emission, dialog hosting) not reducible to a pre-written unit test; flagged inline. All pure/safety-critical logic that *was* previously buried there is now extracted and tested: `HeartbeatFreshnessEvaluator` (Task 12b), `SurvivedWindow` reset (Task 13), `SupervisionStateMachine` transitions (Task 13), `CrashWindow`, `BackoffPolicy`, fallback launcher, settings, mutex, heartbeat stamp.

**3. Type consistency:** `HeartbeatPayload` is defined once (Core.Models, per File Structure) and referenced by both app and supervisor. `SupervisorCommand`/`SupervisorState` defined in Task 13, used in Task 18. `SettingsWriter` (Task 4) used by both app DI and supervisor. `FallbackCandidate` (Task 17) matches the test's constructor. `ProcessController.Launch(exePath, paused)` (Task 14) matches the `--paused` usage in Task 18. `SupervisionInput` subtypes (`LaunchRequest`, `AdoptRequest`, `SurvivedWindow`, ...) defined in Task 13's inputs file and used consistently across the Task 13 tests/impl. `Handle(SupervisionInput)` is single-arg throughout (the input carries `.At`); no stale 2-arg calls remain.
