# Core End-to-End Test Plan — Implementation Plan (steps 1–4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the infrastructure and the first three end-to-end journeys that prove EyeCtrl dispatches a real click at the coordinate the user looked at — and dispatches nothing when it shouldn't.

**Architecture:** A recording WPF oracle (`EyeCtrl.TestTarget`) runs full-screen and streams every real mouse event it receives over a named pipe to a new xUnit project (`EyeCtrl.E2E.Tests`). A `GazePath` driver moves the OS cursor along a saccade→fixation→hold trajectory so `MouseGazeProvider` feeds the real pipeline a signal shaped like gaze. A safety harness (cursor restore, hard wall-clock cap, physical-Esc abort, single-instance pre-flight) bounds every run. The app under test is launched against a scratch `%APPDATA%` profile so a run can never mutate the user's live configuration.

**Tech stack:** .NET 10, WPF, xUnit v3 + NFluent, FlaUI.UIA3, `System.IO.Pipes` + `System.Text.Json`, Win32 `SendInput`/`SetCursorPos`.

**Source spec:** [docs/superpowers/specs/2026-08-08-core-e2e-test-plan-design.md](../specs/2026-08-08-core-e2e-test-plan-design.md)

---

## Scope

This plan implements **build-order steps 1–4** of the spec: the tier-0 spike, `EyeCtrl.TestTarget` + pipe protocol, the `GazePath` driver + safety harness, and journeys **J1, J10, J11**. The remaining tier-1 journeys (J2–J9) and all of tier 2 are deliberately deferred to a follow-up plan, because their assertions depend on two numbers that do not exist until this plan produces a green run: the real coordinate tolerance (Task 7) and whether the alternate-desktop execution mode works (Task 3).

One addition beyond the spec: **Task 1** introduces an `EYECTRL_APPDATA_DIR` override. Without it the suite runs against the user's live profile, and J1/J4/J5 need different `SelectionMode` values which [GazeEngineAdapter.cs:92](../../../src/EyeCtrl.App/Services/GazeEngineAdapter.cs:92) **persists** — a test run would silently rewrite the settings of the machine's only input method.

## Corrections to the spec, verified against `main` @ `26efc7b`

The spec was written before commits that shifted `EventCoordinator`. Use these line numbers, not the spec's:

| Spec says | Actually | What's there |
|---|---|---|
| `EventCoordinator.cs:1374` | **[1454](../../../src/EyeCtrl.App/Services/EventCoordinator.cs:1454)** | the `IsPreciseTarget` / `StableCentroid` override |
| `EventCoordinator.cs:1394-1395` | **[1475](../../../src/EyeCtrl.App/Services/EventCoordinator.cs:1475)** | `DoubleClick` dispatch |
| `EventCoordinator.cs:1397-1400` | **[1477-1480](../../../src/EyeCtrl.App/Services/EventCoordinator.cs:1477)** | Scroll{Up,Down,Left,Right} dispatch |
| `EventCoordinator.cs:832` | **[907-912](../../../src/EyeCtrl.App/Services/EventCoordinator.cs:907)** | `OnDragStarted` → `MouseDown` |
| `Win32InputSimulator.cs:218` normalises to 0–65535 | **[MouseCoordinateMapper.cs:20](../../../src/EyeCtrl.Platform/Input/MouseCoordinateMapper.cs:20)** | the simulator delegates; `:218` is `BuildModifiedKeySequence` |

`GazeEngineAdapter.cs:196-201`, `InteractionManager.cs:130-142`, `AppBootstrapper.cs:86-89` and `MouseGazeProvider.cs:66` are all still accurate.

## The mechanic the spec does not state, and every journey depends on

**The activation button's dwell target is at the fixation point, not at the drawn button.**
[ActivationButtonViewModel.cs:33-37](../../../src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs:33) registers the `DwellTarget` at `message.Position` — the raw fixation point — with radius `UIConstants.ButtonRadius` (50 logical px). The `circleRadius + 55` offset at [EventCoordinator.cs:1045](../../../src/EyeCtrl.App/Services/EventCoordinator.cs:1045) is passed to `ActivationButtonWindow.ShowAt` and only moves where the button is *painted*, so it doesn't cover what the user is looking at.

Consequence: to activate, gaze **holds still**. It does not travel to the button. A path that chased the drawn button would leave the 50px fixation radius, fire `OnFixationEnded`, and drop back to `FreeLook` ([InteractionManager.cs:70-77](../../../src/EyeCtrl.Core/Processing/InteractionManager.cs:70)).

**The action menu ring, by contrast, is a real move**, and its geometry is fully computable ([ActionMenuViewModel.cs:57-71](../../../src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:57)):

```
ringRadiusLogical = CircleSize/2 + ButtonRadius + ActionMenuButtonOuterGap
                  = 40/2      + 50           + 20                        = 90
angle(i)          = (360/N * i - 90)°          // i=0 is straight UP
buttonCenter(i)   = (cx + R*cos(angle), cy + R*sin(angle))
```

With default `VisibleActions` ([ActionMenuSettings.cs:11-18](../../../src/EyeCtrl.Core/Settings/ActionMenuSettings.cs:11)) `LeftClick` is index 0 and `ShowBack` is false in `SelectionMode.None`, so **N=8 and the LeftClick button sits at `(cx, cy - 90)`** at 100% DPI. The action-menu buttons carry **no `AutomationId`** — verified, `ActionMenuWindow.xaml{,.cs}` set none — so the position must be computed, not discovered via FlaUI.

Moving from the fixation point to the ring is safe: at that moment the state is `ActionMenuVisible`, and `OnFixationEnded` is a no-op outside `Fixation`.

## Timing and tolerance budget (defaults, [DwellSettings.cs](../../../src/EyeCtrl.Core/Settings/DwellSettings.cs))

| Knob | Default | Used by |
|---|---:|---|
| `FixationDetectionMs` | 300 | hold before the activation button appears |
| `FixationRadiusPixels` | 50 | how far gaze may wander during the hold |
| `VelocityThresholdPxPerSec` | 100 | centroid inclusion filter |
| `ActivationButtonDwellMs` | 700 | hold to open the action menu |
| `ActionMenuDwellMs` | 600 | hold on the ring button to fire |
| `DwellCooldownMs` | 400 | between activations |

At the `MouseGazeProvider` poll interval of **30 ms** ([MouseGazeProvider.cs:33](../../../src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs:33)), a 1px jitter step is 33 px/s — comfortably inside the 100 px/s centroid threshold ([FixationCentroidTracker.cs:116](../../../src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs:116)). A 4px step is 133 px/s and would be **excluded** from the centroid. Keep `GazePath` jitter at ±1px.

A full J1 costs ≈ 300 + 700 + 600 ms of dwell plus travel and margins ≈ **3.5 s**.

## File structure

**Production (Task 1 only):**

| File | Responsibility |
|---|---|
| Create `src/EyeCtrl.Core/Services/AppPaths.cs` | single source of truth for the app-data directory + `EYECTRL_APPDATA_DIR` override |
| Modify `src/EyeCtrl.App/App.xaml.cs:502-503` | use `AppPaths.AppDataDir` |
| Modify `src/EyeCtrl.App/Services/AppBootstrapper.cs:51` | use `AppPaths.AppDataDir` |
| Modify `src/EyeCtrl.App/Services/EventCoordinator.cs:98` | use `AppPaths.AppDataDir` |

**Recording oracle:**

| File | Responsibility |
|---|---|
| `tests/EyeCtrl.TestTarget/EyeCtrl.TestTarget.csproj` | WPF exe, excluded from the tests' xUnit props |
| `tests/EyeCtrl.TestTarget/TargetPipe.cs` | pipe-name constant + `RecordedEvent` wire record + `WireJson` options |
| `tests/EyeCtrl.TestTarget/EventRecorder.cs` | pipe client; queues and streams JSON lines |
| `tests/EyeCtrl.TestTarget/RecorderWindow.cs` | full-screen window; translates WPF input events to `RecordedEvent` |
| `tests/EyeCtrl.TestTarget/Program.cs` | manual `Main`; parses `--pipe`, owns the WPF `Application` |

**E2E suite:**

| File | Responsibility |
|---|---|
| `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj` | xUnit project, references TestTarget + App |
| `tests/EyeCtrl.E2E.Tests/Infrastructure/E2EGate.cs` | opt-in env var + single-instance pre-flight |
| `tests/EyeCtrl.E2E.Tests/Infrastructure/TargetHarness.cs` | pipe **server**; launches the target, collects events |
| `tests/EyeCtrl.E2E.Tests/Infrastructure/CursorGuard.cs` | save/restore cursor position |
| `tests/EyeCtrl.E2E.Tests/Infrastructure/RunDeadline.cs` | hard wall-clock cap; kills registered processes |
| `tests/EyeCtrl.E2E.Tests/Infrastructure/AbortWatch.cs` | low-level hook for a *physical* Esc |
| `tests/EyeCtrl.E2E.Tests/Infrastructure/GazePath.cs` | saccade→hold cursor trajectories |
| `tests/EyeCtrl.E2E.Tests/Infrastructure/AppHarness.cs` | scratch profile + `EyeCtrl.App --test-mode` lifecycle |
| `tests/EyeCtrl.E2E.Tests/Infrastructure/ActionMenuGeometry.cs` | ring-position maths mirroring `ActionMenuViewModel` |
| `tests/EyeCtrl.E2E.Tests/Calibration/CoordinateRoundTripTests.cs` | Task 7 — pins the real tolerance |
| `tests/EyeCtrl.E2E.Tests/Journeys/LeftClickJourneyTests.cs` | J1 |
| `tests/EyeCtrl.E2E.Tests/Journeys/SuppressionTests.cs` | J10, J11 |
| `tests/EyeCtrl.E2E.Tests/GazePathShapeTests.cs` | headless — Task 8 |

**Throwaway:** `tools/DesktopIsolationSpike/` (Task 3, deleted at the end of that task).

---

## Task 1: `EYECTRL_APPDATA_DIR` override

The app-data path is currently `Path.Combine(Environment.GetFolderPath(ApplicationData), "EyeCtrl")` duplicated in three places. Centralise it and let an environment variable redirect it, matching the existing `EYECTRL_MODEL_DIR` / `EYECTRL_SUPERVISOR_EXE` convention.

The resolution is split into a **pure** function (`Resolve`) and a thin property that reads the environment. Only the pure function is tested — mutating `Environment` inside a test would race every other test class in the same xUnit process.

**Files:**
- Create: `src/EyeCtrl.Core/Services/AppPaths.cs`
- Test: `tests/EyeCtrl.Core.Tests/Services/AppPathsTests.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs:502-503`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs:51`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:98`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Services/AppPathsTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Services;

using System.IO;
using EyeCtrl.Core.Services;

public class AppPathsTests
{
    [Fact]
    public void Resolve_WithNoOverride_UsesRoamingAppDataEyeCtrl()
    {
        var result = AppPaths.Resolve(null, @"C:\Users\someone\AppData\Roaming");

        Check.That(result).IsEqualTo(Path.Combine(@"C:\Users\someone\AppData\Roaming", "EyeCtrl"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithBlankOverride_FallsBackToRoamingAppData(string blank)
    {
        var result = AppPaths.Resolve(blank, @"C:\Roaming");

        Check.That(result).IsEqualTo(Path.Combine(@"C:\Roaming", "EyeCtrl"));
    }

    [Fact]
    public void Resolve_WithOverride_UsesItVerbatim()
    {
        var result = AppPaths.Resolve(@"C:\scratch\e2e-profile", @"C:\Roaming");

        Check.That(result).IsEqualTo(@"C:\scratch\e2e-profile");
    }

    [Fact]
    public void Resolve_WithPaddedOverride_TrimsSurroundingWhitespace()
    {
        var result = AppPaths.Resolve("  C:\\scratch\\e2e  ", @"C:\Roaming");

        Check.That(result).IsEqualTo(@"C:\scratch\e2e");
    }

    [Fact]
    public void EnvVar_IsTheDocumentedName()
    {
        Check.That(AppPaths.EnvVar).IsEqualTo("EYECTRL_APPDATA_DIR");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AppPathsTests"
```

Expected: **compile error** — `AppPaths` does not exist. (Test output is German; a build failure surfaces as `Fehler CS0246`.)

- [ ] **Step 3: Write the implementation**

Create `src/EyeCtrl.Core/Services/AppPaths.cs`:

```csharp
namespace EyeCtrl.Core.Services;

using System.IO;

/// <summary>
/// Single source of truth for the per-user data directory that holds <c>settings.json</c>,
/// <c>calibration.json</c>, <c>actions.json</c>, downloaded models and the Serilog logs.
/// <para>
/// The <see cref="EnvVar"/> override exists so an end-to-end run can point the app at a
/// scratch profile. Without it the suite would read — and, via
/// <c>GazeEngineAdapter.OnSelectionModeChanged</c>, persist changes to — the live profile of
/// the machine the user drives entirely by eye tracking. Mirrors the existing
/// <c>EYECTRL_MODEL_DIR</c> and <c>EYECTRL_SUPERVISOR_EXE</c> overrides.
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>Environment variable that redirects the app data directory.</summary>
    public const string EnvVar = "EYECTRL_APPDATA_DIR";

    /// <summary>
    /// Pure resolution, split out from <see cref="AppDataDir"/> so it can be tested without
    /// mutating process-wide environment state (which would race sibling test classes in the
    /// same xUnit host).
    /// </summary>
    /// <param name="overrideDir">Value of <see cref="EnvVar"/>, or <c>null</c> when unset.</param>
    /// <param name="roamingAppData">Resolved <c>ApplicationData</c> special folder.</param>
    public static string Resolve(string? overrideDir, string roamingAppData) =>
        string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(roamingAppData, "EyeCtrl")
            : overrideDir.Trim();

    /// <summary>The app data directory for this process.</summary>
    public static string AppDataDir => Resolve(
        Environment.GetEnvironmentVariable(EnvVar),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AppPathsTests"
```

Expected: `Fehler: 0, erfolgreich: 5`.

- [ ] **Step 5: Replace the three hard-coded call sites**

In `src/EyeCtrl.App/App.xaml.cs`, replace lines 502-503:

```csharp
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
```

with:

```csharp
        _appDataDir = AppPaths.AppDataDir;
```

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, replace line 51:

```csharp
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
```

with:

```csharp
        var appDataDir = AppPaths.AppDataDir;
```

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, replace line 98:

```csharp
        _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
```

with:

```csharp
        _appDataDir = AppPaths.AppDataDir;
```

Add `using EyeCtrl.Core.Services;` to each file's using block if it is not already present (`App.xaml.cs` already has it for `SingleInstanceGuard`; check the other two and add where missing).

- [ ] **Step 6: Verify no hard-coded path survives**

```bash
grep -rn 'SpecialFolder.ApplicationData' src/ --include=*.cs
```

Expected: exactly one hit, `src/EyeCtrl.Core/Services/AppPaths.cs`.

- [ ] **Step 7: Run the headless suite**

```bash
dotnet test --filter "Category!=UI"
```

Expected: `Fehler: 0` on every assembly. Baseline was 1929 passed / 1 skipped; this task adds 5, so expect **1934 passed / 1 skipped**.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Core/Services/AppPaths.cs tests/EyeCtrl.Core.Tests/Services/AppPathsTests.cs src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat(core): centralize app data dir behind an EYECTRL_APPDATA_DIR override"
```

---

## Task 2: `EyeCtrl.E2E.Tests` project and the run gate

A separate project from `App.Tests`, per the spec: CI runs part of `App.Tests` headless and must not have to filter around a second risk class, and `App.Tests` carries the `Application.Current.Resources` cross-test coupling that makes isolation unreliable.

The gate has two jobs. `IsEnabled` is the `EYECTRL_E2E_TESTS=1` opt-in. `PreflightBlocker` catches the failure the spec misses: [App.xaml.cs:94-95](../../../src/EyeCtrl.App/App.xaml.cs:94) constructs `SingleInstanceGuard` with the fixed name `Local\EyeCtrl.App.Singleton` and **no override**, so if the user's daily-driver EyeCtrl is running the test instance exits at startup and every journey fails with a misleading "window did not appear". `Local\` is session-scoped, so an isolated desktop does not help — the pre-flight is required in both execution modes.

**Files:**
- Create: `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj`
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/E2EGate.cs`
- Test: `tests/EyeCtrl.E2E.Tests/Infrastructure/E2EGateTests.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Create the project**

Create `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FlaUI.UIA3" Version="5.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Core\EyeCtrl.Core.csproj" />
  </ItemGroup>

</Project>
```

`tests/Directory.Build.props` supplies xUnit v3, NFluent, `OutputType=Exe` and the global usings — do not repeat them here.

- [ ] **Step 2: Register it in the solution**

In `EyeCtrl.slnx`, inside `<Folder Name="/tests/">`, add:

```xml
    <Project Path="tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj" />
```

- [ ] **Step 3: Write the failing test**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/E2EGateTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using EyeCtrl.Core.Services;

/// <summary>
/// Headless tests for the gate itself — these never launch anything and carry no
/// Category=E2E trait, so they run in the ordinary suite.
/// </summary>
public class E2EGateTests
{
    [Fact]
    public void IsOptedIn_IsFalse_ForAnythingButOne()
    {
        Check.That(E2EGate.IsOptedIn(null)).IsFalse();
        Check.That(E2EGate.IsOptedIn("")).IsFalse();
        Check.That(E2EGate.IsOptedIn("0")).IsFalse();
        Check.That(E2EGate.IsOptedIn("yes")).IsFalse();
    }

    [Fact]
    public void IsOptedIn_IsTrue_ForTheDocumentedValues()
    {
        Check.That(E2EGate.IsOptedIn("1")).IsTrue();
        Check.That(E2EGate.IsOptedIn("true")).IsTrue();
        Check.That(E2EGate.IsOptedIn("TRUE")).IsTrue();
    }

    [Fact]
    public void FindSingleInstanceBlocker_ReturnsNull_WhenTheMutexIsFree()
    {
        var freeName = @"Local\EyeCtrl.E2E.GateTest." + Guid.NewGuid().ToString("N");

        Check.That(E2EGate.FindSingleInstanceBlocker(freeName)).IsNull();
    }

    [Fact]
    public void FindSingleInstanceBlocker_Explains_WhenTheMutexIsHeld()
    {
        var heldName = @"Local\EyeCtrl.E2E.GateTest." + Guid.NewGuid().ToString("N");
        using var holder = new SingleInstanceGuard(heldName);
        Check.That(holder.TryAcquire()).IsTrue();

        var blocker = E2EGate.FindSingleInstanceBlocker(heldName);

        Check.That(blocker).IsNotNull();
        Check.That(blocker!).Contains("already running");
    }

    [Fact]
    public void EnvVar_IsTheDocumentedName()
    {
        Check.That(E2EGate.EnvVar).IsEqualTo("EYECTRL_E2E_TESTS");
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

```bash
dotnet test tests/EyeCtrl.E2E.Tests
```

Expected: **compile error** — `E2EGate` does not exist.

- [ ] **Step 5: Write the implementation**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/E2EGate.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using EyeCtrl.Core.Services;

/// <summary>
/// Decides whether an end-to-end journey may run at all.
/// <para>
/// These tests take the OS cursor for several seconds. The machine's owner drives it solely by
/// eye tracking, so for the duration of a run they have no input at all — hence a hard opt-in
/// (<see cref="EnvVar"/>) on top of the <c>Category=E2E</c> trait, matching the existing
/// <c>EYECTRL_UI_TESTS</c> gate on <c>AppFixture</c>.
/// </para>
/// </summary>
public static class E2EGate
{
    /// <summary>Opt-in environment variable. A plain <c>dotnet test</c> never launches anything.</summary>
    public const string EnvVar = "EYECTRL_E2E_TESTS";

    /// <summary>Pure form of the opt-in check, so it is testable without touching process state.</summary>
    public static bool IsOptedIn(string? value) => value is "1" or "true" or "TRUE";

    /// <summary>True when the caller has opted in via <see cref="EnvVar"/>.</summary>
    public static bool IsEnabled => IsOptedIn(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// Returns a human-readable reason the run must not proceed, or <c>null</c> when clear.
    /// <para>
    /// <c>App.OnStartup</c> constructs <see cref="SingleInstanceGuard"/> with the fixed production
    /// name and no override, so a second instance exits immediately. If the user's real EyeCtrl is
    /// running, every journey would fail as "Toolbar_Window did not appear" — a diagnosis that
    /// costs an hour and has nothing to do with the product. The mutex is <c>Local\</c>, i.e.
    /// session-scoped, so running on an alternate desktop does not avoid this.
    /// </para>
    /// </summary>
    /// <param name="mutexName">Mutex to probe. Defaults to the production name.</param>
    public static string? FindSingleInstanceBlocker(string? mutexName = null)
    {
        using var probe = new SingleInstanceGuard(mutexName ?? SingleInstanceGuard.DefaultMutexName);
        return probe.TryAcquire()
            ? null
            : "An EyeCtrl.App instance is already running (single-instance mutex is held). "
              + "Close it before running end-to-end tests — a second instance exits at startup.";
    }

    /// <summary>
    /// Skips the calling test cleanly unless the run is opted in AND the box is clear.
    /// Call as the first statement of every <c>Category=E2E</c> test.
    /// </summary>
    public static void SkipUnlessRunnable()
    {
        if (!IsEnabled)
            Assert.Skip($"End-to-end tests are opt-in: set {EnvVar}=1. They launch the app and take "
                        + "the OS cursor for several seconds (also tagged Category=E2E).");

        var blocker = FindSingleInstanceBlocker();
        if (blocker != null)
            Assert.Skip(blocker);
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

```bash
dotnet test tests/EyeCtrl.E2E.Tests
```

Expected: `Fehler: 0, erfolgreich: 5`.

- [ ] **Step 7: Confirm the new project is picked up by the headless filter**

```bash
dotnet test --filter "Category!=UI"
```

Expected: 12 assemblies now, `Fehler: 0` on all.

- [ ] **Step 8: Commit**

```bash
git add tests/EyeCtrl.E2E.Tests EyeCtrl.slnx
git commit -m "test(e2e): add the E2E test project and its opt-in + single-instance gate"
```

---

## Task 3: Tier 0 — desktop isolation spike

Not a test. A throwaway probe answering three questions: does WPF render on an alternate desktop object (there is no DWM composition there), can UIA read that tree, and does `SendInput` stay contained. The answer decides whether tiers 1–2 run supervised or unattended.

**Expect this to fail, and that is fine.** `SendInput` injects into the input queue of the desktop associated with the calling thread, and Windows generally refuses to deliver injected input to a desktop that is not the *input desktop* (the visible one). The spike exists to convert that expectation into an observed fact rather than a guess. Supervised mode is the fallback and costs nothing.

The containment check is the interesting one: the spike launches **two** recorder windows — one on the alternate desktop, one on the default desktop — and sends a single click. Containment holds only if the alternate one records it and the default one does not. The default-desktop window is full-screen, so it absorbs the click either way; nothing else on the user's machine can receive it.

**Files:**
- Create: `tools/DesktopIsolationSpike/DesktopIsolationSpike.csproj`
- Create: `tools/DesktopIsolationSpike/Program.cs`
- Modify: `docs/superpowers/specs/2026-08-08-core-e2e-test-plan-design.md` (record the verdict)

- [ ] **Step 1: Create the spike project**

Create `tools/DesktopIsolationSpike/DesktopIsolationSpike.csproj`. It is deliberately **not** added to `EyeCtrl.slnx` — it is throwaway.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <!-- Manual Main: SetThreadDesktop must run before WPF creates any HWND. -->
    <StartupObject>DesktopIsolationSpike.Program</StartupObject>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Write the spike**

Create `tools/DesktopIsolationSpike/Program.cs`:

```csharp
namespace DesktopIsolationSpike;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>
/// THROWAWAY tier-0 probe for docs/superpowers/plans/2026-08-10-core-e2e-test-plan.md.
/// Delete once the verdict is recorded in the design spec.
///
///   spike.exe                     parent: create desktop, spawn both children, click, report
///   spike.exe --child &lt;desk&gt; &lt;out&gt;  child: optionally hop to &lt;desk&gt;, show full-screen, log clicks
/// </summary>
internal static class Program
{
    private const string DesktopName = "EyeCtrlE2ESpike";
    private const uint GENERIC_ALL = 0x10000000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string lpszDesktop, IntPtr lpszDevice,
        IntPtr pDevmode, int dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktopW(string lpszDesktop, int dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx; public int Dy; public int MouseData;
        public uint Flags; public uint Time; public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public MOUSEINPUT Mouse;
        // Mouse is the largest union member on x64 for our purposes; the spike sends mouse only.
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [STAThread]
    private static int Main(string[] args)
    {
        return args.Length >= 3 && args[0] == "--child"
            ? RunChild(desktop: args[1], outFile: args[2])
            : RunParent();
    }

    private static int RunChild(string desktop, string outFile)
    {
        // MUST happen before WPF creates any window or message hook, hence the manual Main.
        if (desktop != "-")
        {
            var hDesk = OpenDesktopW(desktop, 0, false, GENERIC_ALL);
            if (hDesk == IntPtr.Zero)
            {
                File.WriteAllText(outFile, $"OPEN_DESKTOP_FAILED {Marshal.GetLastWin32Error()}\n");
                return 2;
            }
            if (!SetThreadDesktop(hDesk))
            {
                File.WriteAllText(outFile, $"SET_THREAD_DESKTOP_FAILED {Marshal.GetLastWin32Error()}\n");
                return 3;
            }
        }

        var window = new Window
        {
            Title = "SpikeChild",
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Topmost = true,
            Background = Brushes.DarkSlateBlue,
            Content = new System.Windows.Controls.TextBlock { Text = "spike target" }
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(window, "SpikeChild");

        window.MouseDown += (_, e) =>
        {
            var p = window.PointToScreen(e.GetPosition(window));
            File.AppendAllText(outFile, $"CLICK {p.X:F0} {p.Y:F0}\n");
        };

        window.SourceInitialized += (_, _) =>
            File.AppendAllText(outFile, $"RENDERED {window.ActualWidth:F0}x{window.ActualHeight:F0}\n");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        timer.Tick += (_, _) => app.Shutdown();
        timer.Start();

        window.Show();
        return app.Run();
    }

    private static int RunParent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl-spike-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var isolatedOut = Path.Combine(dir, "isolated.txt");
        var defaultOut = Path.Combine(dir, "default.txt");
        var exe = Environment.ProcessPath!;

        Console.WriteLine($"Spike output: {dir}");

        var hDesk = CreateDesktopW(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (hDesk == IntPtr.Zero)
        {
            Console.WriteLine($"Q1 CreateDesktop FAILED: {Marshal.GetLastWin32Error()} — verdict: SUPERVISED");
            return 1;
        }

        var isolated = Process.Start(exe, ["--child", DesktopName, isolatedOut])!;
        var onDefault = Process.Start(exe, ["--child", "-", defaultOut])!;
        Thread.Sleep(4000);

        var isolatedLog = ReadAll(isolatedOut);
        var defaultLog = ReadAll(defaultOut);
        Console.WriteLine($"Q1 WPF rendered on alternate desktop: {isolatedLog.Contains("RENDERED")} ({isolatedLog.Trim()})");

        // Q2: UIA against the alternate desktop, from a thread bound to it.
        var uiaFound = false;
        var uiaThread = new Thread(() =>
        {
            var hOpen = OpenDesktopW(DesktopName, 0, false, GENERIC_ALL);
            if (hOpen == IntPtr.Zero || !SetThreadDesktop(hOpen)) return;
            using var automation = new FlaUI.UIA3.UIA3Automation();
            uiaFound = automation.GetDesktop()
                .FindFirstDescendant(cf => cf.ByAutomationId("SpikeChild")) != null;
        });
        uiaThread.SetApartmentState(ApartmentState.STA);
        uiaThread.Start();
        uiaThread.Join(TimeSpan.FromSeconds(10));
        Console.WriteLine($"Q2 UIA read the alternate desktop tree: {uiaFound}");

        // Q3: one click from a thread bound to the alternate desktop.
        var sendResult = 0u;
        var clickThread = new Thread(() =>
        {
            var hOpen = OpenDesktopW(DesktopName, 0, false, GENERIC_ALL);
            if (hOpen == IntPtr.Zero || !SetThreadDesktop(hOpen)) return;
            SetCursorPos(600, 400);
            var nx = 600 * 65535 / GetSystemMetrics(0);
            var ny = 400 * 65535 / GetSystemMetrics(1);
            INPUT[] inputs =
            [
                Mouse(nx, ny, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN),
                Mouse(nx, ny, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTUP)
            ];
            sendResult = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        });
        clickThread.SetApartmentState(ApartmentState.STA);
        clickThread.Start();
        clickThread.Join(TimeSpan.FromSeconds(10));
        Thread.Sleep(1500);

        var isolatedGotClick = ReadAll(isolatedOut).Contains("CLICK");
        var defaultGotClick = ReadAll(defaultOut).Contains("CLICK");
        Console.WriteLine($"Q3 SendInput returned {sendResult}; isolated got click: {isolatedGotClick}; "
                          + $"DEFAULT desktop got click (leak): {defaultGotClick}");

        var verdict = isolatedLog.Contains("RENDERED") && uiaFound && isolatedGotClick && !defaultGotClick
            ? "ISOLATED DESKTOP WORKS — tiers 1-2 may run unattended"
            : "ISOLATED DESKTOP DOES NOT WORK — tiers 1-2 run SUPERVISED";
        Console.WriteLine($"VERDICT: {verdict}");

        TryKill(isolated); TryKill(onDefault);
        CloseDesktop(hDesk);
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static INPUT Mouse(int dx, int dy, uint flags) => new()
    {
        Type = INPUT_MOUSE,
        Mouse = new MOUSEINPUT { Dx = dx, Dy = dy, Flags = flags }
    };

    private static string ReadAll(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
    }
}
```

Add the FlaUI package reference the spike's Q2 needs — in `tools/DesktopIsolationSpike/DesktopIsolationSpike.csproj`, inside a new `<ItemGroup>`:

```xml
  <ItemGroup>
    <PackageReference Include="FlaUI.UIA3" Version="5.0.0" />
  </ItemGroup>
```

- [ ] **Step 3: Ask the user before running it**

**STOP.** This spike puts two full-screen windows on screen and sends one click. Ask for an explicit go-ahead and confirm the user is ready to lose the cursor for ~10 seconds. Do not run it otherwise.

- [ ] **Step 4: Run the spike**

```bash
dotnet run --project tools/DesktopIsolationSpike
```

Expected: four lines (`Q1`, `Q2`, `Q3`, `VERDICT`) on stdout. Record the **verbatim output** — it is the evidence for the next step.

- [ ] **Step 5: Record the verdict in the design spec**

In `docs/superpowers/specs/2026-08-08-core-e2e-test-plan-design.md`, under `### Tiers`, append below the "Tier 0 — isolation spike" paragraph:

```markdown
**Tier 0 result (recorded YYYY-MM-DD, `tools/DesktopIsolationSpike`, now deleted).** Paste the
spike's four output lines verbatim here, then state the chosen execution mode in one sentence.
```

Replace the placeholder with the real date and the real output. Do not paraphrase the numbers.

- [ ] **Step 6: Delete the spike**

```bash
git rm -r --cached tools/DesktopIsolationSpike 2>/dev/null; rm -rf tools/DesktopIsolationSpike
```

The verdict now lives in the spec; the code has no further use and would rot.

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-08-08-core-e2e-test-plan-design.md
git commit -m "docs(testing): record the tier-0 desktop isolation verdict"
```

---

## Task 4: `EyeCtrl.TestTarget` project and the wire contract

The oracle. A WPF app that records every real mouse event it receives — button, screen coordinates, click count, wheel delta — and streams them as JSON lines over a named pipe.

Two decisions worth stating:

**The target is the pipe *client*, the test is the *server*.** The test starts listening first, then launches the target, so there is no race and no retry loop. This matches `HeartbeatPublisher` (app is client, supervisor is server) rather than `ExternalDwellService`.

**Mouse events only.** Keyboard recording is J9's requirement, and J9 is in the follow-up plan. The `Kind` discriminator makes it purely additive.

`tests/Directory.Build.props` sets `IsTestProject`, `OutputType=Exe` and injects xUnit + NFluent into **every** project under `tests/`. The target is not a test project, so it must be excluded by name.

**Files:**
- Create: `tests/EyeCtrl.TestTarget/EyeCtrl.TestTarget.csproj`
- Create: `tests/EyeCtrl.TestTarget/TargetPipe.cs`
- Modify: `tests/Directory.Build.props`
- Modify: `EyeCtrl.slnx`
- Modify: `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj`
- Test: `tests/EyeCtrl.E2E.Tests/WireContractTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.E2E.Tests/WireContractTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests;

using System.Text.Json;
using EyeCtrl.TestTarget;

/// <summary>Headless — no trait, runs in the ordinary suite.</summary>
public class WireContractTests
{
    [Fact]
    public void RecordedEvent_RoundTrips_ThroughCamelCaseJson()
    {
        var original = new RecordedEvent("LeftDown", 1234.5, 678.25, ClickCount: 1,
            WheelDelta: 0, TimestampMs: 1_700_000_000_123);

        var json = JsonSerializer.Serialize(original, TargetPipe.JsonOptions);
        var back = JsonSerializer.Deserialize<RecordedEvent>(json, TargetPipe.JsonOptions);

        Check.That(json).Contains("\"kind\"");
        Check.That(back).IsEqualTo(original);
    }

    [Fact]
    public void RecordedEvent_SerializesToASingleLine_SoTheReaderCanSplitOnNewline()
    {
        var json = JsonSerializer.Serialize(
            new RecordedEvent("Wheel", 10, 20, 0, -120, 5), TargetPipe.JsonOptions);

        Check.That(json).Not.Contains("\n");
    }

    [Fact]
    public void DefaultPipeName_IsDistinctFromTheProductionChannels()
    {
        Check.That(TargetPipe.DefaultName).IsEqualTo("eyectrl_e2e_target");
        Check.That(TargetPipe.DefaultName).Not.IsEqualTo("eyectrl_watch");
        Check.That(TargetPipe.DefaultName).Not.IsEqualTo("eyectrl_dwell");
    }

    [Fact]
    public void Kinds_CoverEveryButtonTransitionTheJourneysAssertOn()
    {
        Check.That(TargetPipe.Kind.LeftDown).IsEqualTo("LeftDown");
        Check.That(TargetPipe.Kind.LeftUp).IsEqualTo("LeftUp");
        Check.That(TargetPipe.Kind.RightDown).IsEqualTo("RightDown");
        Check.That(TargetPipe.Kind.RightUp).IsEqualTo("RightUp");
        Check.That(TargetPipe.Kind.Wheel).IsEqualTo("Wheel");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~WireContractTests"
```

Expected: **compile error** — namespace `EyeCtrl.TestTarget` not found.

- [ ] **Step 3: Create the TestTarget project**

Create `tests/EyeCtrl.TestTarget/EyeCtrl.TestTarget.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <!-- Manual Main so the pipe name can be parsed before the Application is built. -->
    <StartupObject>EyeCtrl.TestTarget.Program</StartupObject>
  </PropertyGroup>

</Project>
```

- [ ] **Step 4: Exclude it from the tests' xUnit properties**

In `tests/Directory.Build.props`, replace the whole `<Project>` body with:

```xml
<Project>
  <!--
    EyeCtrl.TestTarget lives under tests/ but is a WPF *application* (the end-to-end recording
    oracle), not a test project. Without this guard it would inherit OutputType=Exe,
    IsTestProject=true and the xUnit package set, and `dotnet test` would try to run it as a
    test assembly. MSBuildProjectName is available here because Directory.Build.props is
    imported after the project's identity is known.
  -->
  <PropertyGroup Condition="'$(MSBuildProjectName)' != 'EyeCtrl.TestTarget'">
    <!-- xUnit v3 test projects are stand-alone executables, not class libraries -->
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup Condition="'$(MSBuildProjectName)' != 'EyeCtrl.TestTarget'">
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.3.0" />
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <!-- Surfaces v2 -> v3 API breakages at compile time -->
    <PackageReference Include="xunit.analyzers" Version="1.27.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <!-- Was on 5 of 6 projects; centralizing adds it to Sdk.Abstractions.Tests too (parity, harmless) -->
    <PackageReference Include="coverlet.collector" Version="8.0.0">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="NFluent" Version="3.1.0" />
    <Using Include="Xunit" />
    <Using Include="NFluent" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Write the wire contract**

Create `tests/EyeCtrl.TestTarget/TargetPipe.cs`:

```csharp
namespace EyeCtrl.TestTarget;

using System.Text.Json;

/// <summary>
/// One real input event observed by the recording target.
/// <para>
/// <paramref name="X"/>/<paramref name="Y"/> are PHYSICAL screen pixels (from
/// <c>Visual.PointToScreen</c>), matching the <c>PhysicalPoint</c> space the app dispatches in,
/// so a journey can compare them without a DPI conversion.
/// </para>
/// </summary>
/// <param name="Kind">One of <see cref="TargetPipe.Kind"/>.</param>
/// <param name="X">Physical screen X.</param>
/// <param name="Y">Physical screen Y.</param>
/// <param name="ClickCount">Win32's own double-click determination: 2 means the OS registered a
/// double-click, which is exactly what J3 must assert rather than "two singles arrived".
/// 0 for wheel events.</param>
/// <param name="WheelDelta">Signed wheel delta; 0 for button events.</param>
/// <param name="TimestampMs">Unix milliseconds. Both processes run on the same box, so wall
/// clock is directly comparable and needs no handshake.</param>
public sealed record RecordedEvent(
    string Kind,
    double X,
    double Y,
    int ClickCount,
    int WheelDelta,
    long TimestampMs);

/// <summary>Transport constants for the end-to-end recording channel.</summary>
public static class TargetPipe
{
    /// <summary>
    /// Default channel name. Pipe names are machine-global, so the harness always injects a
    /// GUID-suffixed name instead of binding this one — the same discipline
    /// <c>HeartbeatPublisher</c> and <c>ExternalDwellService</c> follow (AGENTS.md, #76).
    /// </summary>
    public const string DefaultName = "eyectrl_e2e_target";

    /// <summary>CLI switch the harness uses to inject the per-run pipe name.</summary>
    public const string PipeArgument = "--pipe";

    /// <summary>camelCase, matching every other JSON emitter in this codebase.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Values for <see cref="RecordedEvent.Kind"/>.</summary>
    public static class Kind
    {
        public const string LeftDown = "LeftDown";
        public const string LeftUp = "LeftUp";
        public const string RightDown = "RightDown";
        public const string RightUp = "RightUp";
        public const string MiddleDown = "MiddleDown";
        public const string MiddleUp = "MiddleUp";
        public const string Wheel = "Wheel";
    }
}
```

- [ ] **Step 6: Add a placeholder `Program` so the project compiles**

Create `tests/EyeCtrl.TestTarget/Program.cs`:

```csharp
namespace EyeCtrl.TestTarget;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => 0;   // filled in by Task 5
}
```

- [ ] **Step 7: Wire the references**

In `EyeCtrl.slnx`, inside `<Folder Name="/tests/">`, add:

```xml
    <Project Path="tests/EyeCtrl.TestTarget/EyeCtrl.TestTarget.csproj" />
```

In `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj`, add to the existing `ProjectReference` group:

```xml
    <ProjectReference Include="..\EyeCtrl.TestTarget\EyeCtrl.TestTarget.csproj" />
```

The reference is what copies `EyeCtrl.TestTarget.exe` into the E2E test output directory, so the harness can launch it from `AppContext.BaseDirectory` without walking the repo tree.

- [ ] **Step 8: Run the test to verify it passes**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~WireContractTests"
```

Expected: `Fehler: 0, erfolgreich: 4`.

- [ ] **Step 9: Verify `dotnet test` does not try to run the target as a test assembly**

```bash
dotnet test --filter "Category!=UI"
```

Expected: still 12 test assemblies (not 13) and `Fehler: 0`. If `EyeCtrl.TestTarget` appears in the assembly list, the Directory.Build.props guard did not take.

- [ ] **Step 10: Commit**

```bash
git add tests/EyeCtrl.TestTarget tests/Directory.Build.props tests/EyeCtrl.E2E.Tests EyeCtrl.slnx
git commit -m "test(e2e): add the recording target project and its pipe wire contract"
```

---

## Task 5: The recorder and the harness that reads it

The target's window and pipe client, plus `TargetHarness` — the server side that launches it and accumulates events.

WPF's own mouse events are used rather than a `WH_MOUSE_LL` hook for one specific reason: `MouseButtonEventArgs.ClickCount` carries **Win32's** double-click determination, which is the assertion J3 needs and which a raw hook cannot give without reimplementing `GetDoubleClickTime` logic.

**Files:**
- Create: `tests/EyeCtrl.TestTarget/EventRecorder.cs`
- Create: `tests/EyeCtrl.TestTarget/RecorderWindow.cs`
- Modify: `tests/EyeCtrl.TestTarget/Program.cs`
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/TargetHarness.cs`
- Test: `tests/EyeCtrl.E2E.Tests/Infrastructure/TargetHarnessTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/TargetHarnessTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

/// <summary>
/// Category=E2E: this launches a full-screen window. It does NOT move the cursor, but it does
/// cover the screen, so it stays behind the opt-in gate like every other E2E test.
/// </summary>
[Trait("Category", "E2E")]
public class TargetHarnessTests
{
    [Fact]
    public async Task Target_LaunchesAndConnects_AndReportsZeroEventsWhenUntouched()
    {
        E2EGate.SkipUnlessRunnable();

        await using var harness = TargetHarness.Create();
        await harness.StartAsync(TimeSpan.FromSeconds(15));

        Check.That(harness.IsConnected).IsTrue();

        var events = await harness.DrainAsync(quietFor: TimeSpan.FromMilliseconds(500));

        Check.That(events).IsEmpty();
    }

    [Fact]
    public async Task PipeName_IsUniquePerHarness_SoConcurrentRunsCannotCollide()
    {
        var a = TargetHarness.Create();
        var b = TargetHarness.Create();

        Check.That(a.PipeName).Not.IsEqualTo(b.PipeName);
        Check.That(a.PipeName).StartsWith(EyeCtrl.TestTarget.TargetPipe.DefaultName);

        await a.DisposeAsync();
        await b.DisposeAsync();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~TargetHarnessTests"
```

Expected: **compile error** — `TargetHarness` does not exist.

- [ ] **Step 3: Write the recorder's pipe client**

Create `tests/EyeCtrl.TestTarget/EventRecorder.cs`:

```csharp
namespace EyeCtrl.TestTarget;

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

/// <summary>
/// Streams <see cref="RecordedEvent"/>s to the test harness as newline-delimited JSON.
/// <para>
/// <see cref="Record"/> is called from the UI thread on every input event and must never block
/// it — a blocked UI thread would stop the window processing input and silently lose the very
/// events the test is asserting on. It enqueues; one background writer drains.
/// </para>
/// </summary>
public sealed class EventRecorder : IDisposable
{
    private readonly BlockingCollection<RecordedEvent> _queue = new(new ConcurrentQueue<RecordedEvent>());
    private readonly NamedPipeClientStream _pipe;
    private readonly Thread _writer;

    public EventRecorder(string pipeName)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        _pipe.Connect(10_000);   // the harness is already listening; a failure here is fatal and loud
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "e2e-recorder-writer" };
        _writer.Start();
    }

    /// <summary>Queues one event. Never blocks, never throws.</summary>
    public void Record(RecordedEvent e)
    {
        try { _queue.Add(e); } catch (InvalidOperationException) { /* completed — shutting down */ }
    }

    private void WriterLoop()
    {
        try
        {
            foreach (var e in _queue.GetConsumingEnumerable())
            {
                var line = JsonSerializer.Serialize(e, TargetPipe.JsonOptions) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                _pipe.Write(bytes, 0, bytes.Length);
                _pipe.Flush();   // the harness asserts on timing; do not let events pool in a buffer
            }
        }
        catch (IOException) { /* harness went away — nothing useful left to do */ }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _pipe.Dispose();
        _queue.Dispose();
    }
}
```

- [ ] **Step 4: Write the recorder window**

Create `tests/EyeCtrl.TestTarget/RecorderWindow.cs`:

```csharp
namespace EyeCtrl.TestTarget;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>
/// Full-screen oracle window. Every real mouse event that lands here is recorded with its
/// physical screen coordinate.
/// <para>
/// It sits UNDER EyeCtrl's overlays, which is workable because every overlay that covers the
/// screen is created click-through — <c>OverlayWindow.SetOverlayInteraction</c> applies
/// <c>WS_EX_TRANSPARENT</c> when <c>IsClickThrough</c> is set — and the action menu is hidden
/// before the click is dispatched (<c>InteractionManager.OnActionSelected</c> raises
/// <c>HideActionMenu</c> before <c>ActionRequested</c>).
/// </para>
/// </summary>
public sealed class RecorderWindow : Window
{
    private readonly EventRecorder _recorder;

    public RecorderWindow(EventRecorder recorder)
    {
        _recorder = recorder;

        Title = "EyeCtrl E2E Target";
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x24));
        Content = new TextBlock
        {
            Text = "EyeCtrl end-to-end target — recording input",
            Foreground = Brushes.DimGray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(this, "E2E_Target_Window");

        // Preview* so a click is recorded even if some child element would handle it.
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseUp += OnPreviewMouseUp;
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => RecordButton(e, isDown: true);

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e) => RecordButton(e, isDown: false);

    private void RecordButton(MouseButtonEventArgs e, bool isDown)
    {
        var kind = (e.ChangedButton, isDown) switch
        {
            (MouseButton.Left, true) => TargetPipe.Kind.LeftDown,
            (MouseButton.Left, false) => TargetPipe.Kind.LeftUp,
            (MouseButton.Right, true) => TargetPipe.Kind.RightDown,
            (MouseButton.Right, false) => TargetPipe.Kind.RightUp,
            (MouseButton.Middle, true) => TargetPipe.Kind.MiddleDown,
            (MouseButton.Middle, false) => TargetPipe.Kind.MiddleUp,
            _ => null
        };
        if (kind == null) return;

        var p = PointToScreen(e.GetPosition(this));
        _recorder.Record(new RecordedEvent(
            kind, p.X, p.Y,
            ClickCount: e.ClickCount,
            WheelDelta: 0,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this));
        _recorder.Record(new RecordedEvent(
            TargetPipe.Kind.Wheel, p.X, p.Y,
            ClickCount: 0,
            WheelDelta: e.Delta,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
}
```

- [ ] **Step 5: Fill in `Program`**

Replace the whole body of `tests/EyeCtrl.TestTarget/Program.cs`:

```csharp
namespace EyeCtrl.TestTarget;

using System.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var pipeName = ParsePipeName(args) ?? TargetPipe.DefaultName;

        using var recorder = new EventRecorder(pipeName);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new RecorderWindow(recorder);
        app.MainWindow = window;
        window.Show();
        window.Activate();
        return app.Run();
    }

    private static string? ParsePipeName(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], TargetPipe.PipeArgument, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
```

- [ ] **Step 6: Write the harness**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/TargetHarness.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EyeCtrl.TestTarget;

/// <summary>
/// Server side of the recording channel: owns the pipe, launches <c>EyeCtrl.TestTarget.exe</c>,
/// and accumulates every event it reports.
/// <para>
/// The harness is the SERVER and the target is the CLIENT, so the listener is always up before
/// the target starts — no connect-retry loop, no startup race. Each instance binds a
/// GUID-suffixed name because pipe names are machine-global (AGENTS.md, #76).
/// </para>
/// </summary>
public sealed class TargetHarness : IAsyncDisposable
{
    private readonly List<RecordedEvent> _events = new();
    private readonly object _lock = new();
    private readonly NamedPipeServerStream _server;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private Task? _readLoop;

    public string PipeName { get; }

    /// <summary>True once the target has connected to the pipe.</summary>
    public bool IsConnected => _server.IsConnected;

    /// <summary>The target process, for registration with <see cref="RunDeadline"/>.</summary>
    public Process Process => _process
        ?? throw new InvalidOperationException("TargetHarness has not been started.");

    private TargetHarness(string pipeName)
    {
        PipeName = pipeName;
        _server = new NamedPipeServerStream(
            pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    /// <summary>Creates a harness on a unique pipe. Does not launch anything yet.</summary>
    public static TargetHarness Create() =>
        new($"{TargetPipe.DefaultName}_test_{Guid.NewGuid():N}");

    /// <summary>Launches the target and waits for it to connect.</summary>
    public async Task StartAsync(TimeSpan timeout)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EyeCtrl.TestTarget.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"Recording target not found at {exe}. It should be copied there by the "
                + "ProjectReference from EyeCtrl.E2E.Tests to EyeCtrl.TestTarget.", exe);

        var waitForConnect = _server.WaitForConnectionAsync(_cts.Token);

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"{TargetPipe.PipeArgument} {PipeName}",
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start the recording target.");

        var completed = await Task.WhenAny(waitForConnect, Task.Delay(timeout, _cts.Token));
        if (completed != waitForConnect)
            throw new TimeoutException(
                $"Recording target did not connect to '{PipeName}' within {timeout.TotalSeconds}s.");
        await waitForConnect;

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    /// <summary>Snapshot of everything recorded so far.</summary>
    public IReadOnlyList<RecordedEvent> Events
    {
        get { lock (_lock) return _events.ToArray(); }
    }

    /// <summary>Clears the accumulator — call between phases of a journey.</summary>
    public void Clear()
    {
        lock (_lock) _events.Clear();
    }

    /// <summary>
    /// Waits until no new event has arrived for <paramref name="quietFor"/>, then returns
    /// everything recorded. Bounded by <paramref name="maxWait"/> so a stuck target cannot hang
    /// the run. A settle-based drain rather than a fixed sleep: an assertion of "exactly one
    /// click" is only meaningful once the channel has gone quiet.
    /// </summary>
    public async Task<IReadOnlyList<RecordedEvent>> DrainAsync(
        TimeSpan quietFor, TimeSpan? maxWait = null)
    {
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(10));
        var lastCount = -1;
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            int count;
            lock (_lock) count = _events.Count;

            if (count != lastCount)
            {
                lastCount = count;
                stableSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - stableSince >= quietFor)
            {
                break;
            }

            await Task.Delay(50);
        }

        return Events;
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(
                _server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024, leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null) break;                 // target exited
                if (line.Length == 0) continue;

                var e = JsonSerializer.Deserialize<RecordedEvent>(line, TargetPipe.JsonOptions);
                if (e == null) continue;
                lock (_lock) _events.Add(e);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* pipe broken on shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_readLoop != null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }

        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _server.Dispose();
        _cts.Dispose();
    }
}
```

- [ ] **Step 7: Ask before running, then run**

**STOP.** This puts a full-screen window on screen for a few seconds. It does not move the cursor. Ask for an explicit go-ahead.

```bash
$env:EYECTRL_E2E_TESTS=1; dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~TargetHarnessTests"
```

Expected: `Fehler: 0, erfolgreich: 2`. Without the env var, both skip.

- [ ] **Step 8: Confirm the default run still skips**

```bash
dotnet test tests/EyeCtrl.E2E.Tests
```

Expected: the two `TargetHarnessTests` report as skipped; `WireContractTests` and `E2EGateTests` pass.

- [ ] **Step 9: Commit**

```bash
git add tests/EyeCtrl.TestTarget tests/EyeCtrl.E2E.Tests
git commit -m "test(e2e): add the recording target window and its pipe harness"
```

---

## Task 6: Safety harness

Three independent guards. The spec is explicit that the wall-clock cap is the actual safety net and the Esc hook is a convenience, because during a supervised run the user has no input at all.

**Files:**
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/CursorGuard.cs`
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/RunDeadline.cs`
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/AbortWatch.cs`
- Test: `tests/EyeCtrl.E2E.Tests/Infrastructure/SafetyHarnessTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/SafetyHarnessTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using System.Diagnostics;

/// <summary>
/// Headless where possible. Only the cursor-restore test moves the cursor, and it moves it
/// straight back, so it carries the E2E trait; the deadline tests are pure and do not.
/// </summary>
public class SafetyHarnessTests
{
    [Fact]
    public void RunDeadline_HasNotExpired_BeforeItsBudget()
    {
        using var deadline = new RunDeadline(TimeSpan.FromMinutes(5));

        Check.That(deadline.HasExpired).IsFalse();
        Check.That(deadline.Remaining).IsStrictlyGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task RunDeadline_Expires_AndFiresItsCallback()
    {
        var fired = 0;
        using var deadline = new RunDeadline(TimeSpan.FromMilliseconds(200), onExpired: () => fired++);

        await Task.Delay(700);

        Check.That(deadline.HasExpired).IsTrue();
        Check.That(fired).IsEqualTo(1);
    }

    [Fact]
    public async Task RunDeadline_KillsRegisteredProcesses_OnExpiry()
    {
        using var victim = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 60 127.0.0.1 > nul",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        using (var deadline = new RunDeadline(TimeSpan.FromMilliseconds(200)))
        {
            deadline.Register(victim);
            await Task.Delay(1500);
        }

        Check.That(victim.HasExited).IsTrue();
    }

    [Fact]
    public void RunDeadline_ThrowIfExpired_ThrowsAfterTheBudget()
    {
        using var deadline = new RunDeadline(TimeSpan.FromMilliseconds(50));
        Thread.Sleep(300);

        TimeoutException? ex = null;
        try { deadline.ThrowIfExpired(); }
        catch (TimeoutException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.Message).Contains("wall-clock cap");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public void CursorGuard_RestoresThePositionItCaptured()
    {
        E2EGate.SkipUnlessRunnable();

        var before = CursorGuard.GetPosition();
        using (var _ = CursorGuard.Capture())
        {
            CursorGuard.SetPosition(before.X + 37, before.Y + 23);
            Check.That(CursorGuard.GetPosition()).Not.IsEqualTo(before);
        }

        Check.That(CursorGuard.GetPosition()).IsEqualTo(before);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~SafetyHarnessTests"
```

Expected: **compile error** — `RunDeadline` and `CursorGuard` do not exist.

- [ ] **Step 3: Write `CursorGuard`**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/CursorGuard.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using System.Runtime.InteropServices;

/// <summary>
/// Saves the cursor position on construction and puts it back on dispose — including when the
/// test throws, which is when it matters most. The machine's owner drives it by eye tracking;
/// leaving the cursor parked wherever a failed journey abandoned it is a real cost to them.
/// </summary>
public sealed partial class CursorGuard : IDisposable
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly (int X, int Y) _saved;
    private bool _restored;

    private CursorGuard((int X, int Y) saved) => _saved = saved;

    /// <summary>Captures the current cursor position.</summary>
    public static CursorGuard Capture() => new(GetPosition());

    /// <summary>Current cursor position in physical screen pixels.</summary>
    public static (int X, int Y) GetPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>Moves the cursor. This is how a gaze path is played back.</summary>
    public static void SetPosition(int x, int y) => SetCursorPos(x, y);

    public void Dispose()
    {
        if (_restored) return;
        _restored = true;
        SetCursorPos(_saved.X, _saved.Y);
    }
}
```

- [ ] **Step 4: Write `RunDeadline`**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/RunDeadline.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using System.Diagnostics;

/// <summary>
/// A hard wall-clock cap on a run. On expiry it kills every registered process, so a hung app
/// or a wedged target cannot hold the user's only input channel indefinitely.
/// <para>
/// This — not the Esc hook — is the real safety net: during a supervised run the user has no
/// input at all, so nothing they can do would trigger a manual abort.
/// </para>
/// </summary>
public sealed class RunDeadline : IDisposable
{
    private readonly DateTime _expiresAt;
    private readonly List<Process> _processes = new();
    private readonly object _lock = new();
    private readonly Timer _timer;
    private readonly Action? _onExpired;
    private int _fired;

    public RunDeadline(TimeSpan budget, Action? onExpired = null)
    {
        Budget = budget;
        _expiresAt = DateTime.UtcNow + budget;
        _onExpired = onExpired;
        _timer = new Timer(_ => Expire(), null, budget, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The configured cap.</summary>
    public TimeSpan Budget { get; }

    /// <summary>True once the cap has elapsed.</summary>
    public bool HasExpired => DateTime.UtcNow >= _expiresAt;

    /// <summary>Time left, floored at zero.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = _expiresAt - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Registers a process to be killed if the cap elapses.</summary>
    public void Register(Process process)
    {
        lock (_lock) _processes.Add(process);
    }

    /// <summary>Throws if the cap has elapsed. Call between phases of a long journey.</summary>
    public void ThrowIfExpired()
    {
        if (HasExpired)
            throw new TimeoutException(
                $"End-to-end run exceeded its {Budget.TotalSeconds:F0}s wall-clock cap and was aborted.");
    }

    private void Expire()
    {
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;

        Process[] snapshot;
        lock (_lock) snapshot = _processes.ToArray();

        foreach (var p in snapshot)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }

        _onExpired?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
```

- [ ] **Step 5: Write `AbortWatch`**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/AbortWatch.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using System.Runtime.InteropServices;
using System.Windows.Threading;

/// <summary>
/// Watches for a PHYSICAL Escape key and signals a cancellation token when one arrives.
/// <para>
/// The <c>LLKHF_INJECTED</c> check is the whole point: the suite itself sends keystrokes via
/// <c>SendInput</c>, and an abort that fired on the run's own synthetic input would make every
/// keyboard journey unrunnable. Only a key a human physically pressed counts.
/// </para>
/// <para>
/// A convenience, not the safety net — see <see cref="RunDeadline"/>. During a supervised run the
/// user is operating by eye tracking and has no keyboard.
/// </para>
/// </summary>
public sealed class AbortWatch : IDisposable
{
    // DllImport rather than LibraryImport: the hook signature passes a managed delegate, which
    // the LibraryImport source generator will not marshal.
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const uint LLKHF_INJECTED = 0x00000010;
    private const int VK_ESCAPE = 0x1B;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode; public uint ScanCode; public uint Flags;
        public uint Time; public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // Held in a field: the hook is called from unmanaged code, and a collected delegate crashes
    // the process — the same trap TobiiStreamEngineProvider documents for its gaze callback.
    private readonly HookProc _proc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private IntPtr _hook;
    private Dispatcher? _dispatcher;

    public AbortWatch()
    {
        _proc = OnKey;
        _thread = new Thread(PumpLoop) { IsBackground = true, Name = "e2e-abort-watch" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Cancelled when a physical Esc is observed.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>True once a physical Esc has aborted the run.</summary>
    public bool Aborted => _cts.IsCancellationRequested;

    private void PumpLoop()
    {
        // A low-level keyboard hook needs a message loop on its own thread, otherwise Windows
        // times it out and silently unhooks it.
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        Dispatcher.Run();
    }

    private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYDOWN)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var injected = (data.Flags & LLKHF_INJECTED) != 0;
            if (!injected && data.VkCode == VK_ESCAPE)
                _cts.Cancel();
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
```

- [ ] **Step 6: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~SafetyHarnessTests"
```

Expected: 4 passed, 1 skipped (`CursorGuard_RestoresThePositionItCaptured` needs the opt-in).

- [ ] **Step 7: Ask, then verify the cursor-restore test**

**STOP.** This moves the cursor 37×23px and immediately restores it. Ask for an explicit go-ahead.

```bash
$env:EYECTRL_E2E_TESTS=1; dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~SafetyHarnessTests"
```

Expected: `Fehler: 0, erfolgreich: 5`.

- [ ] **Step 8: Commit**

```bash
git add tests/EyeCtrl.E2E.Tests
git commit -m "test(e2e): add the cursor, deadline and physical-abort safety harness"
```

---

## Task 7: Pin the real coordinate tolerance

The spec says to start at ±2 physical pixels and pin the real figure from the first green run, and not to encode a tolerance that has not been observed. This task measures it **with EyeCtrl out of the loop** — just `Win32InputSimulator` → `SendInput` → the target — so the number isolates the `MouseCoordinateMapper` 0–65535 round trip from anything the gaze pipeline contributes.

This is the single most useful measurement in the plan: J1's assertion is only as trustworthy as this number.

**Files:**
- Create: `tests/EyeCtrl.E2E.Tests/Calibration/CoordinateRoundTripTests.cs`
- Modify: `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj` (add the Platform reference)
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/E2ETolerance.cs`

- [ ] **Step 1: Add the Platform project reference**

In `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj`, add to the `ProjectReference` group:

```xml
    <ProjectReference Include="..\..\src\EyeCtrl.Platform\EyeCtrl.Platform.csproj" />
```

- [ ] **Step 2: Write the tolerance constant with the spec's starting value**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/E2ETolerance.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

/// <summary>
/// Coordinate tolerances for end-to-end assertions, in PHYSICAL pixels.
/// </summary>
public static class E2ETolerance
{
    /// <summary>
    /// Error introduced purely by <c>Win32InputSimulator</c> -&gt; <c>MouseCoordinateMapper</c>
    /// -&gt; <c>SendInput</c> -&gt; window. The mapper normalises to a 0-65535 absolute grid
    /// (<c>MouseCoordinateMapper.cs:20</c>), so the round trip is lossy below a pixel.
    /// <para>
    /// STARTING VALUE ONLY — the spec forbids encoding an unobserved tolerance. Replace this with
    /// the figure <c>CoordinateRoundTripTests.Measure_SendInputRoundTripError</c> actually reports,
    /// and record that run's output in the constant's comment. If the true error exceeds 2px,
    /// that is a finding about the pipeline, not a number to quietly widen.
    /// </para>
    /// </summary>
    public const double SendInputRoundTripPx = 2.0;

    /// <summary>
    /// End-to-end click placement tolerance: <see cref="SendInputRoundTripPx"/> plus whatever the
    /// gaze pipeline (OneEuroFilter, centroid averaging) contributes. Measured by J1, which
    /// reports the observed distance from the intended point.
    /// </summary>
    public const double ClickPlacementPx = 4.0;
}
```

- [ ] **Step 3: Write the failing measurement test**

Create `tests/EyeCtrl.E2E.Tests/Calibration/CoordinateRoundTripTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Calibration;

using EyeCtrl.Core.Models;
using EyeCtrl.E2E.Tests.Infrastructure;
using EyeCtrl.Platform.Input;
using EyeCtrl.TestTarget;

/// <summary>
/// Pins the coordinate error of the input path with EyeCtrl entirely out of the loop:
/// Win32InputSimulator -&gt; SendInput -&gt; recording target. Whatever J1 later observes on top
/// of this figure is attributable to the gaze pipeline, not to input dispatch.
/// </summary>
[Trait("Category", "E2E")]
public class CoordinateRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public CoordinateRoundTripTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Measure_SendInputRoundTripError()
    {
        E2EGate.SkipUnlessRunnable();

        using var cursor = CursorGuard.Capture();
        await using var target = TargetHarness.Create();
        await target.StartAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(750);   // let WPF finish layout so PointToScreen is meaningful

        var simulator = new Win32InputSimulator(new MouseCoordinateMapper());
        var probes = new (int X, int Y)[] { (200, 200), (640, 480), (1000, 700), (1337, 421) };
        var errors = new List<double>();

        foreach (var probe in probes)
        {
            target.Clear();
            simulator.LeftClick(new PhysicalPoint(probe.X, probe.Y));
            var events = await target.DrainAsync(quietFor: TimeSpan.FromMilliseconds(400));

            var down = events.FirstOrDefault(e => e.Kind == TargetPipe.Kind.LeftDown);
            Check.That(down).IsNotNull();

            var dx = down!.X - probe.X;
            var dy = down.Y - probe.Y;
            var error = Math.Sqrt(dx * dx + dy * dy);
            errors.Add(error);
            _output.WriteLine($"sent ({probe.X},{probe.Y}) -> got ({down.X:F2},{down.Y:F2}) "
                              + $"dx={dx:F2} dy={dy:F2} err={error:F2}px");
        }

        var worst = errors.Max();
        _output.WriteLine($"WORST ROUND-TRIP ERROR: {worst:F2}px "
                          + $"(E2ETolerance.SendInputRoundTripPx = {E2ETolerance.SendInputRoundTripPx})");

        Check.That(worst).IsStrictlyLessThan(E2ETolerance.SendInputRoundTripPx);
    }
}
```

- [ ] **Step 4: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~CoordinateRoundTripTests"
```

Expected: **skipped** (no opt-in) — this test needs no new production code, only the measurement run. Go to step 5.

- [ ] **Step 5: Ask, then run the measurement**

**STOP.** This sends four real clicks. They land on the full-screen target, which absorbs them, but the cursor moves. Ask for an explicit go-ahead.

```bash
$env:EYECTRL_E2E_TESTS=1; dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~CoordinateRoundTripTests"
```

- [ ] **Step 6: Record the observed number**

Read the `WORST ROUND-TRIP ERROR` line from the test output. Update `E2ETolerance.SendInputRoundTripPx` to the smallest round value that comfortably exceeds it (e.g. observed 0.6px → set 1.0), and replace the "STARTING VALUE ONLY" paragraph with the measured figure and the date. If the observed error exceeds 2px, **stop and report it** — that is a finding about `MouseCoordinateMapper`, not a number to widen.

- [ ] **Step 7: Re-run and commit**

```bash
$env:EYECTRL_E2E_TESTS=1; dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~CoordinateRoundTripTests"
```

Expected: `Fehler: 0, erfolgreich: 1`.

```bash
git add tests/EyeCtrl.E2E.Tests
git commit -m "test(e2e): pin the measured SendInput coordinate round-trip tolerance"
```

---

## Task 8: The `GazePath` driver

Replaces `AppFixture.SimulateDwellAt`, which teleports the cursor and holds. A teleport can satisfy `DwellDetector` while producing a centroid unlike anything the real pipeline computes — which would make J1's assertion meaningless, because J1 asserts against `StableCentroid`.

The driver is split so the interesting half is testable without a display: `Build` is a pure function returning a sample sequence, and `PlayAsync` walks it against `CursorGuard.SetPosition`. The headless test feeds `Build`'s output into the **real** `FixationDetector` and `FixationCentroidTracker` and asserts the shape is right.

That headless test proves the path's *shape*, not the end-to-end number: the real pipeline feeds these detectors the **filtered** point (`GazeEngine.cs:381,392` — post `OneEuroFilter`/`GapInterpolator`), whereas the test feeds them raw samples. J1 measures the real thing.

**Files:**
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/GazePath.cs`
- Test: `tests/EyeCtrl.E2E.Tests/GazePathShapeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.E2E.Tests/GazePathShapeTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.E2E.Tests.Infrastructure;

/// <summary>
/// Headless — no cursor, no app. Feeds a built path into the SAME detector types the live
/// pipeline uses, with their default settings, and asserts the path is shaped like something
/// that produces a fixation and a centroid where we aimed.
/// </summary>
public class GazePathShapeTests
{
    private const double FixationRadiusPixels = 50;      // DwellSettings default
    private const int FixationDetectionMs = 300;         // DwellSettings default
    private const double VelocityThresholdPxPerSec = 100;// DwellSettings default

    [Fact]
    public void SaccadeThenHold_StartsAtTheOrigin_AndEndsAtTheTarget()
    {
        var path = GazePath.SaccadeThenHold((100, 100), (800, 500), saccadeMs: 150, holdMs: 900);

        Check.That(path).Not.IsEmpty();
        Check.That(path[0].X).IsEqualTo(100);
        Check.That(path[0].Y).IsEqualTo(100);
        Check.That(Math.Abs(path[^1].X - 800)).IsStrictlyLessThan(3);
        Check.That(Math.Abs(path[^1].Y - 500)).IsStrictlyLessThan(3);
    }

    [Fact]
    public void SaccadeThenHold_SamplesAtTheProviderPollInterval()
    {
        var path = GazePath.SaccadeThenHold((0, 0), (400, 400), saccadeMs: 150, holdMs: 600);

        Check.That(path).ContainsOnlyElementsThatMatch(s => s.DelayMs == GazePath.SampleIntervalMs);
        // 150 + 600 = 750ms of path at 30ms per sample
        Check.That(path).HasSize(750 / GazePath.SampleIntervalMs);
    }

    [Fact]
    public void SaccadeThenHold_ProducesAFixation_AtTheHoldPoint()
    {
        var path = GazePath.SaccadeThenHold((100, 100), (800, 500), saccadeMs: 150, holdMs: 900);

        var detector = new FixationDetector(FixationRadiusPixels, FixationDetectionMs);
        PhysicalPoint? fixatedAt = null;
        detector.FixationStarted += p => fixatedAt ??= p;

        long tUs = 0;
        foreach (var s in path)
        {
            detector.Update(new PhysicalPoint(s.X, s.Y), tUs);
            tUs += s.DelayMs * 1000L;
        }

        // HasValue rather than IsNotNull: NFluent's null checks target reference types, and
        // PhysicalPoint? is a nullable struct.
        Check.That(fixatedAt.HasValue).IsTrue();
        Check.That(fixatedAt!.Value.DistanceTo(new PhysicalPoint(800, 500)))
             .IsStrictlyLessThan(FixationRadiusPixels);
    }

    [Fact]
    public void SaccadeThenHold_ProducesACentroid_WithinTwoPixelsOfTheHoldPoint()
    {
        var path = GazePath.SaccadeThenHold((100, 100), (800, 500), saccadeMs: 150, holdMs: 900);

        var detector = new FixationDetector(FixationRadiusPixels, FixationDetectionMs);
        var tracker = new FixationCentroidTracker(VelocityThresholdPxPerSec);
        detector.FixationStarted += _ => tracker.StartTracking();
        detector.FixationEnded += tracker.StopTracking;

        long tUs = 0;
        foreach (var s in path)
        {
            var p = new PhysicalPoint(s.X, s.Y);
            tracker.Update(p, tUs);          // GazeEngine updates the tracker on EVERY frame
            detector.Update(p, tUs);
            tUs += s.DelayMs * 1000L;
        }

        Check.That(tracker.StableCentroid.HasValue).IsTrue();
        Check.That(tracker.StableCentroid!.Value.DistanceTo(new PhysicalPoint(800, 500)))
             .IsStrictlyLessThan(2.0);
    }

    [Fact]
    public void HoldJitter_StaysBelowTheCentroidVelocityThreshold()
    {
        // A >3px step at the 30ms poll interval exceeds 100px/s and would be EXCLUDED from the
        // centroid (FixationCentroidTracker.cs:116), quietly biasing it toward the first sample.
        var path = GazePath.SaccadeThenHold((0, 0), (500, 500), saccadeMs: 90, holdMs: 900);
        var hold = path.Skip(90 / GazePath.SampleIntervalMs).ToArray();

        for (int i = 1; i < hold.Length; i++)
        {
            var dx = hold[i].X - hold[i - 1].X;
            var dy = hold[i].Y - hold[i - 1].Y;
            var speed = Math.Sqrt(dx * dx + dy * dy) / (GazePath.SampleIntervalMs / 1000.0);
            Check.That(speed).IsStrictlyLessThan(VelocityThresholdPxPerSec);
        }
    }

    [Fact]
    public void SaccadeThenHold_IsDeterministic_ForTheSameSeed()
    {
        var a = GazePath.SaccadeThenHold((0, 0), (300, 300), 120, 600, seed: 7);
        var b = GazePath.SaccadeThenHold((0, 0), (300, 300), 120, 600, seed: 7);

        Check.That(a).ContainsExactly(b);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~GazePathShapeTests"
```

Expected: **compile error** — `GazePath` does not exist.

- [ ] **Step 3: Write the implementation**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/GazePath.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

/// <summary>One cursor position and how long to hold it before the next.</summary>
public readonly record struct GazeSample(int X, int Y, int DelayMs);

/// <summary>
/// Emits a sequence of cursor positions modelling saccade -&gt; fixation -&gt; dwell, so
/// <c>FixationDetector</c> and <c>FixationCentroidTracker</c> see a signal shaped like real gaze.
/// <para>
/// Replaces <c>AppFixture.SimulateDwellAt</c>, which teleports and holds. A teleport can satisfy
/// <c>DwellDetector</c> while producing a centroid unlike anything the live pipeline computes —
/// and J1 asserts against <c>GazeEngine.StableCentroid</c>, so that difference is the assertion.
/// </para>
/// </summary>
public static class GazePath
{
    /// <summary>
    /// Matches <c>MouseGazeProvider</c>'s default poll interval, so the app samples roughly one
    /// emitted position per frame rather than aliasing across them.
    /// </summary>
    public const int SampleIntervalMs = 30;

    /// <summary>
    /// Jitter amplitude during the hold, in pixels. Kept at 1 because a 4px step at the 30ms poll
    /// interval is 133 px/s, above the 100 px/s centroid threshold, and would be dropped from the
    /// mean (<c>FixationCentroidTracker.cs:116</c>).
    /// </summary>
    public const int HoldJitterPx = 1;

    /// <summary>
    /// Builds a path: a smooth saccade from <paramref name="from"/> to <paramref name="to"/> over
    /// <paramref name="saccadeMs"/>, then a jittering hold on <paramref name="to"/> for
    /// <paramref name="holdMs"/>.
    /// </summary>
    /// <param name="seed">Fixed so a failing journey reproduces exactly.</param>
    public static IReadOnlyList<GazeSample> SaccadeThenHold(
        (int X, int Y) from, (int X, int Y) to, int saccadeMs, int holdMs, int seed = 12345)
    {
        var samples = new List<GazeSample>((saccadeMs + holdMs) / SampleIntervalMs);
        var rng = new Random(seed);

        var saccadeSteps = saccadeMs / SampleIntervalMs;
        for (int i = 0; i < saccadeSteps; i++)
        {
            // Ease-out: fast launch, decelerating arrival — the shape a real saccade has, and the
            // one that lets the last few samples fall inside the fixation radius before the hold.
            var t = (double)i / saccadeSteps;
            var eased = 1 - Math.Pow(1 - t, 3);
            samples.Add(new GazeSample(
                (int)Math.Round(from.X + (to.X - from.X) * eased),
                (int)Math.Round(from.Y + (to.Y - from.Y) * eased),
                SampleIntervalMs));
        }

        var holdSteps = holdMs / SampleIntervalMs;
        for (int i = 0; i < holdSteps; i++)
        {
            samples.Add(new GazeSample(
                to.X + rng.Next(-HoldJitterPx, HoldJitterPx + 1),
                to.Y + rng.Next(-HoldJitterPx, HoldJitterPx + 1),
                SampleIntervalMs));
        }

        return samples;
    }

    /// <summary>A hold in place, with no preceding saccade.</summary>
    public static IReadOnlyList<GazeSample> Hold((int X, int Y) at, int holdMs, int seed = 12345) =>
        SaccadeThenHold(at, at, saccadeMs: 0, holdMs, seed);

    /// <summary>
    /// Plays a path by moving the real cursor. Honours <paramref name="token"/> so a physical Esc
    /// or an expired deadline stops the run mid-path.
    /// </summary>
    public static async Task PlayAsync(IReadOnlyList<GazeSample> path, CancellationToken token = default)
    {
        foreach (var s in path)
        {
            token.ThrowIfCancellationRequested();
            CursorGuard.SetPosition(s.X, s.Y);
            await Task.Delay(s.DelayMs, token);
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~GazePathShapeTests"
```

Expected: `Fehler: 0, erfolgreich: 6`. If `SaccadeThenHold_ProducesACentroid_WithinTwoPixelsOfTheHoldPoint` fails, the ease-out curve is leaving too many decelerating samples inside the fixation radius before the hold begins — increase the cubic exponent rather than widening the tolerance.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.E2E.Tests
git commit -m "test(e2e): add the GazePath saccade-then-hold driver"
```

---

## Task 9: `AppHarness` — the app under test, on a scratch profile

Launches `EyeCtrl.App --test-mode` against a throwaway `%APPDATA%` directory seeded with a settings file the journeys control, so no run can read or write the user's live configuration. This is what Task 1 was for.

The settings are written explicitly rather than defaulted, because J1 asserts on a specific action-menu ring geometry and that geometry is a function of `CircleSize` and `VisibleActions`.

**Files:**
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/AppHarness.cs`
- Create: `tests/EyeCtrl.E2E.Tests/Infrastructure/ActionMenuGeometry.cs`
- Test: `tests/EyeCtrl.E2E.Tests/Infrastructure/ActionMenuGeometryTests.cs`
- Modify: `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj`

- [ ] **Step 1: Add the ViewModels reference**

`ActionMenuGeometry` must be checkable against the constants the app actually uses. In `tests/EyeCtrl.E2E.Tests/EyeCtrl.E2E.Tests.csproj`, add:

```xml
    <ProjectReference Include="..\..\src\EyeCtrl.ViewModels\EyeCtrl.ViewModels.csproj" />
    <ProjectReference Include="..\..\src\EyeCtrl.App\EyeCtrl.App.csproj" />
```

The `EyeCtrl.App` reference is what copies `EyeCtrl.App.exe` and its `Resources/` into the E2E output directory, so the harness launches a known-current build instead of shelling out to `dotnet build` the way `AppFixture` does.

- [ ] **Step 2: Write the failing geometry test**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/ActionMenuGeometryTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// Headless. The action-menu buttons carry no AutomationId, so a journey cannot find them via
/// FlaUI and must compute their positions. These tests keep that computation locked to the same
/// constants ActionMenuViewModel uses — if the ring formula changes there, this fails here.
/// </summary>
public class ActionMenuGeometryTests
{
    [Fact]
    public void RingRadius_MatchesTheViewModelsFormula()
    {
        // ActionMenuViewModel.cs:57 — CircleSize/2 + ButtonRadius + ActionMenuButtonOuterGap
        var expected = 40.0 / 2 + UIConstants.ButtonRadius + UIConstants.ActionMenuButtonOuterGap;

        Check.That(ActionMenuGeometry.RingRadiusLogical(circleSize: 40)).IsEqualTo(expected);
        Check.That(ActionMenuGeometry.RingRadiusLogical(circleSize: 40)).IsEqualTo(90.0);
    }

    [Fact]
    public void FirstItem_IsStraightUpFromTheCenter()
    {
        // angle(0) = -90 degrees => (cx, cy - R)
        var p = ActionMenuGeometry.ButtonCenter(
            new PhysicalPoint(500, 400), itemIndex: 0, itemCount: 8, ringRadiusPhysical: 90);

        Check.That(Math.Abs(p.X - 500)).IsStrictlyLessThan(0.001);
        Check.That(Math.Abs(p.Y - 310)).IsStrictlyLessThan(0.001);
    }

    [Fact]
    public void ItemsAreEvenlySpacedClockwise()
    {
        var center = new PhysicalPoint(0, 0);
        var second = ActionMenuGeometry.ButtonCenter(center, 1, 4, 100);   // -90 + 90 = 0 deg => +X

        Check.That(Math.Abs(second.X - 100)).IsStrictlyLessThan(0.001);
        Check.That(Math.Abs(second.Y - 0)).IsStrictlyLessThan(0.001);
    }

    [Fact]
    public void LeftClickIndex_IsZero_ForTheDefaultVisibleActions()
    {
        var defaults = new EyeCtrl.Core.Settings.ActionMenuSettings().VisibleActions;

        Check.That(defaults.IndexOf(InteractionAction.LeftClick)).IsEqualTo(0);
        Check.That(defaults).HasSize(8);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~ActionMenuGeometryTests"
```

Expected: **compile error** — `ActionMenuGeometry` does not exist.

- [ ] **Step 4: Write the geometry helper**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/ActionMenuGeometry.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// Mirrors the ring layout in <c>ActionMenuViewModel.Receive(ShowActionMenuMessage)</c>
/// (ActionMenuViewModel.cs:57-71). Duplicated rather than reused because the view model needs a
/// live messenger and settings graph, which a journey has no way to reach across a process
/// boundary. <c>ActionMenuGeometryTests</c> is the drift guard.
/// </summary>
public static class ActionMenuGeometry
{
    /// <summary>Ring radius in LOGICAL pixels: circle radius + button radius + outer gap.</summary>
    public static double RingRadiusLogical(double circleSize) =>
        circleSize / 2 + UIConstants.ButtonRadius + UIConstants.ActionMenuButtonOuterGap;

    /// <summary>
    /// Center of item <paramref name="itemIndex"/> on a ring of <paramref name="itemCount"/>.
    /// Item 0 is at -90 degrees, i.e. straight up; items advance clockwise.
    /// </summary>
    public static PhysicalPoint ButtonCenter(
        PhysicalPoint center, int itemIndex, int itemCount, double ringRadiusPhysical)
    {
        var angleStepDeg = 360.0 / itemCount;
        var angleRad = (angleStepDeg * itemIndex - 90.0) * (Math.PI / 180.0);
        return new PhysicalPoint(
            center.X + ringRadiusPhysical * Math.Cos(angleRad),
            center.Y + ringRadiusPhysical * Math.Sin(angleRad));
    }
}
```

- [ ] **Step 5: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~ActionMenuGeometryTests"
```

Expected: `Fehler: 0, erfolgreich: 4`.

- [ ] **Step 6: Write `AppHarness`**

Create `tests/EyeCtrl.E2E.Tests/Infrastructure/AppHarness.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Infrastructure;

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EyeCtrl.Core.Models;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

/// <summary>
/// Owns the <c>EyeCtrl.App</c> process for one journey.
/// <para>
/// The app is launched with <c>EYECTRL_APPDATA_DIR</c> pointed at a throwaway directory seeded
/// with a settings file this harness writes. Without that redirect the run would read — and,
/// through <c>GazeEngineAdapter.OnSelectionModeChanged</c>, persist changes to — the live profile
/// of the machine the user drives entirely by eye tracking.
/// </para>
/// </summary>
public sealed class AppHarness : IAsyncDisposable
{
    private readonly string _profileDir;
    private UIA3Automation? _automation;
    private Process? _process;

    /// <summary>Scratch profile directory for this run.</summary>
    public string ProfileDir => _profileDir;

    /// <summary>The <c>CircleSize</c> written into the seeded settings, for ring geometry.</summary>
    public int CircleSize { get; }

    private AppHarness(string profileDir, int circleSize)
    {
        _profileDir = profileDir;
        CircleSize = circleSize;
    }

    /// <summary>
    /// Mirrors <c>SettingsService.JsonOptions</c> (SettingsService.cs:27-32) exactly.
    /// <para>
    /// The enum converter is the load-bearing part: <c>SelectionMode</c> is persisted as a
    /// <b>camelCase string</b> (<c>"autoSelection"</c>), not as PascalCase and not as a number.
    /// Seeding it any other way risks the value being dropped on load, which would silently run
    /// the journey in the default mode and make J1 fail for a reason unrelated to the product.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions SeedOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Creates a harness with a fresh scratch profile.
    /// </summary>
    /// <param name="selectionMode">Typed rather than a string, so the camelCase-enum wire format
    /// is the serializer's problem instead of the caller's.</param>
    /// <param name="circleSize">Cursor circle diameter; drives the action-menu ring radius.</param>
    public static AppHarness Create(
        SelectionMode selectionMode = SelectionMode.None, int circleSize = 40)
    {
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // Only the keys the journeys depend on. SettingsService fills the rest from defaults, and
        // ActionMenuSettings.EnsureDefaults restores the full VisibleActions list, so LeftClick
        // stays at index 0 on an 8-item ring.
        var settings = new Dictionary<string, object?>
        {
            ["selectionMode"] = selectionMode,
            ["cursor"] = new Dictionary<string, object?> { ["circleSize"] = circleSize },
            ["general"] = new Dictionary<string, object?> { ["startPaused"] = false },
            ["supervision"] = new Dictionary<string, object?> { ["enabled"] = false },
            ["externalDwell"] = new Dictionary<string, object?> { ["enabled"] = false }
        };
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            JsonSerializer.Serialize(settings, SeedOptions));

        return new AppHarness(dir, circleSize);
    }

    /// <summary>Launches the app and waits for its toolbar window to appear in the UIA tree.</summary>
    /// <param name="startPaused">Passes <c>--paused</c>, the deterministic way to reach J10's state.</param>
    public async Task StartAsync(TimeSpan timeout, bool startPaused = false)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EyeCtrl.App.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"EyeCtrl.App.exe not found at {exe}. It should be copied there by the "
                + "ProjectReference from EyeCtrl.E2E.Tests to EyeCtrl.App.", exe);

        _automation = new UIA3Automation();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = startPaused ? "--test-mode --paused" : "--test-mode",
            UseShellExecute = false
        };
        psi.Environment["EYECTRL_APPDATA_DIR"] = _profileDir;

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start EyeCtrl.App.");

        await WaitForWindowAsync("Toolbar_Window", timeout);
        await Task.Delay(1000);   // WPF registers child AutomationPeers after the window appears
    }

    /// <summary>The app process, for registration with <see cref="RunDeadline"/>.</summary>
    public Process Process => _process
        ?? throw new InvalidOperationException("AppHarness has not been started.");

    /// <summary>Finds a top-level window of this app process by AutomationId.</summary>
    public AutomationElement? FindWindow(string automationId)
    {
        var cf = _automation!.ConditionFactory;
        foreach (var w in _automation.GetDesktop().FindAllDescendants(cf.ByAutomationId(automationId)))
        {
            try
            {
                if (w.Properties.ProcessId.Value == _process!.Id) return w;
            }
            catch { /* stale element */ }
        }
        return null;
    }

    /// <summary>Waits for a window with the given AutomationId to appear.</summary>
    public async Task<AutomationElement> WaitForWindowAsync(string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var window = FindWindow(automationId);
            if (window != null) return window;
            await Task.Delay(250);
        }

        throw new TimeoutException($"Window '{automationId}' did not appear within {timeout.TotalSeconds}s.");
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _automation?.Dispose();

        // Best-effort: the app may still be flushing Serilog when we kill it.
        await Task.Delay(300);
        try { Directory.Delete(_profileDir, recursive: true); } catch { }
    }
}
```

- [ ] **Step 7: Confirm the three assumptions the harness bakes in**

```bash
grep -n '"--paused"' src/EyeCtrl.App/App.xaml.cs
grep -rn 'Toolbar_Window' src/EyeCtrl.App/Overlays/ToolbarWindow.xaml
grep -n 'PropertyNamingPolicy\|JsonStringEnumConverter' src/EyeCtrl.Core/Settings/SettingsService.cs
```

Expected: a hit for `--paused` at `App.xaml.cs:76`; an `AutomationProperties.AutomationId="Toolbar_Window"` on the toolbar (`AppFixture` already waits on that id, so it should match); and `SettingsService.cs:30-31` showing `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` plus `new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`.

Those two lines are what `AppHarness.SeedOptions` mirrors. If either has changed, update `SeedOptions` to match — a mismatch means the seeded keys or enum values are silently dropped on load, the app starts on defaults, and J1 fails in a way that looks like a product bug.

- [ ] **Step 8: Prove the seeding round-trips before any journey depends on it**

Add to `tests/EyeCtrl.E2E.Tests/Infrastructure/ActionMenuGeometryTests.cs`:

```csharp
    [Fact]
    public void SeededProfile_IsReadBackByTheRealSettingsService()
    {
        var harness = AppHarness.Create(SelectionMode.AutoSelection, circleSize: 64);
        try
        {
            var service = new SettingsService(
                Path.Combine(harness.ProfileDir, "settings.json"), SettingsWriter.App);
            var loaded = service.Load();

            Check.That(loaded.SelectionMode).IsEqualTo(SelectionMode.AutoSelection);
            Check.That(loaded.Cursor.CircleSize).IsEqualTo(64);
        }
        finally
        {
            try { Directory.Delete(harness.ProfileDir, recursive: true); } catch { }
        }
    }
```

Add `using System.IO;` and `using EyeCtrl.Core.Settings;` to that file's using block.

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~ActionMenuGeometryTests"
```

Expected: `Fehler: 0, erfolgreich: 5`. **A failure here means `SeedOptions` has drifted from `SettingsService.JsonOptions`** — fix `SeedOptions`, not the assertion. Headless, so it catches the drift at desk-time rather than inside a cursor-taking journey.

- [ ] **Step 9: Commit**

```bash
git add tests/EyeCtrl.E2E.Tests
git commit -m "test(e2e): add the app harness with a scratch profile and ring geometry"
```

---

## Task 10: J1 — a left click lands where the user looked

The flagship. With `SelectionMode=None`, `IsPreciseTarget` is **false** ([GazeEngineAdapter.cs:198-200](../../../src/EyeCtrl.App/Services/GazeEngineAdapter.cs:198)), so [EventCoordinator.cs:1454](../../../src/EyeCtrl.App/Services/EventCoordinator.cs:1454) discards the `FixationPoint` the state machine chose and clicks at `GazeEngine.StableCentroid` instead. This is the only mode where the override fires, and whether the two agree is the spec's open question — so the test **asserts against the centroid's proxy (the hold point) and reports the observed offset**, rather than pretending to know the answer.

Path shape, following the mechanic established above:

1. saccade to the target point, hold ~1100ms — 300ms establishes the fixation, then ≥700ms dwells the activation button *at the same point*, opening the action menu;
2. saccade to the LeftClick ring button at `(target.X, target.Y - ringRadius)`, hold ~800ms to clear the 600ms menu dwell;
3. drain and assert.

**Files:**
- Create: `tests/EyeCtrl.E2E.Tests/Journeys/LeftClickJourneyTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.E2E.Tests/Journeys/LeftClickJourneyTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Journeys;

using EyeCtrl.Core.Models;
using EyeCtrl.E2E.Tests.Infrastructure;
using EyeCtrl.TestTarget;

// ITestOutputHelper lives in the `Xunit` namespace in xUnit v3 (the global using from
// tests/Directory.Build.props covers it). There is no `Xunit.Abstractions` in v3.

/// <summary>
/// J1 — the flagship journey. Proves the assembled system dispatches exactly one real left click,
/// at the coordinate the user held their gaze on.
/// </summary>
[Trait("Category", "E2E")]
public class LeftClickJourneyTests
{
    private readonly ITestOutputHelper _output;

    public LeftClickJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task LeftClick_InSelectionModeNone_LandsAtTheHeldGazePoint()
    {
        E2EGate.SkipUnlessRunnable();

        using var cursor = CursorGuard.Capture();
        using var abort = new AbortWatch();
        using var deadline = new RunDeadline(TimeSpan.FromSeconds(60));

        await using var target = TargetHarness.Create();
        await target.StartAsync(TimeSpan.FromSeconds(15));
        deadline.Register(target.Process);

        await using var app = AppHarness.Create(SelectionMode.None, circleSize: 40);
        await app.StartAsync(TimeSpan.FromSeconds(30));
        deadline.Register(app.Process);

        var gazeTarget = (X: 700, Y: 450);
        var ringRadius = ActionMenuGeometry.RingRadiusLogical(app.CircleSize);   // 90 at CircleSize=40
        var leftClickButton = (X: gazeTarget.X, Y: gazeTarget.Y - (int)ringRadius);

        target.Clear();

        // 1. Look at the target and hold: 300ms fixation + 700ms activation dwell, plus margin.
        //    The activation button's DWELL TARGET is at the fixation point itself
        //    (ActivationButtonViewModel.cs:33-37) — gaze holds still, it does not chase the
        //    button that is DRAWN 75px to the side.
        await GazePath.PlayAsync(
            GazePath.SaccadeThenHold((100, 100), gazeTarget, saccadeMs: 210, holdMs: 1200),
            abort.Token);

        deadline.ThrowIfExpired();

        // 2. Move to the LeftClick ring button (index 0 => straight up) and hold past the
        //    600ms action-menu dwell.
        await GazePath.PlayAsync(
            GazePath.SaccadeThenHold(gazeTarget, leftClickButton, saccadeMs: 150, holdMs: 900),
            abort.Token);

        var events = await target.DrainAsync(quietFor: TimeSpan.FromMilliseconds(700));

        Check.That(abort.Aborted).IsFalse();

        var downs = events.Where(e => e.Kind == TargetPipe.Kind.LeftDown).ToArray();
        var ups = events.Where(e => e.Kind == TargetPipe.Kind.LeftUp).ToArray();

        foreach (var e in events)
            _output.WriteLine($"{e.Kind} at ({e.X:F1},{e.Y:F1}) clickCount={e.ClickCount} t={e.TimestampMs}");

        Check.That(downs).HasSize(1);
        Check.That(ups).HasSize(1);
        Check.That(events.Where(e => e.Kind is TargetPipe.Kind.RightDown or TargetPipe.Kind.Wheel))
             .IsEmpty();

        var dx = downs[0].X - gazeTarget.X;
        var dy = downs[0].Y - gazeTarget.Y;
        var offset = Math.Sqrt(dx * dx + dy * dy);

        // The click point is GazeEngine.StableCentroid, NOT InteractionManager.FixationPoint
        // (EventCoordinator.cs:1454). The centroid is the mean of the slow samples during the
        // hold, so the hold point is its expected value. This number IS the spec's open question
        // about whether the two agree — record it.
        _output.WriteLine($"CLICK OFFSET FROM HELD GAZE POINT: {offset:F2}px "
                          + $"(dx={dx:F2} dy={dy:F2}, tolerance={E2ETolerance.ClickPlacementPx})");

        Check.That(offset).IsStrictlyLessThan(E2ETolerance.ClickPlacementPx);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~LeftClickJourneyTests"
```

Expected: **skipped** (no opt-in). That is not a red test — go to step 3.

- [ ] **Step 3: Ask, then run the journey**

**STOP.** This is the first run that takes the cursor for several seconds while the real app drives it. Ask for an explicit go-ahead and confirm the user is ready. Confirm no other `EyeCtrl.App` is running — the gate will skip if one is, but say so up front.

```bash
$env:EYECTRL_E2E_TESTS=1; dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~LeftClickJourneyTests"
```

Expected on the first attempt: **possibly red.** Read the `_output` lines before changing anything. The three likely causes, in order:

1. **Zero events recorded** — the click did not reach the target. Check whether an EyeCtrl overlay above the target is not click-through. This is the spec's open question 3; the fallback is a `WH_MOUSE_LL` recorder for coordinates plus a smaller positioned window for hit-testing.
2. **Two `LeftDown`s** — the ring dwell fired twice, or the hold on the target itself produced a second activation. Lengthen the gap or shorten the second hold toward the 600ms dwell + 400ms cooldown.
3. **One `LeftDown` but a large offset** — the interesting failure. Report the number; it is a real finding about `StableCentroid` versus the hold point, not a tolerance to widen.

- [ ] **Step 4: Record the observed offset**

Update `E2ETolerance.ClickPlacementPx` to the smallest round value comfortably above the observed offset, and replace its doc comment with the measured figure and the date of the run. Then update the design spec's "Open questions" section — the first bullet ("whether `StableCentroid` and `FixationPoint` agree in practice") now has partial evidence; record it.

- [ ] **Step 5: Re-run to confirm green, then commit**

```bash
$env:EYECTRL_E2E_TESTS=1; dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~LeftClickJourneyTests"
```

Expected: `Fehler: 0, erfolgreich: 1`.

```bash
git add tests/EyeCtrl.E2E.Tests docs/superpowers/specs/2026-08-08-core-e2e-test-plan-design.md
git commit -m "test(e2e): J1 — prove a left click lands at the held gaze point"
```

---

## Task 11: J10 — paused dispatches nothing

The spec calls J10 and J11 the highest-value tests in the set and the two that must not be dropped for schedule. A spurious click is worse than a missed one.

J10 runs the **exact same script as J1** against an app launched with `--paused`, and asserts the target recorded **zero** events. Reusing the script verbatim is the point: any divergence would leave room for the test to pass because the script was wrong rather than because pause works.

`GazeEngine.OnGazePoint` returns early when `InteractionManager.CurrentState == InteractionState.Paused` ([GazeEngine.cs:383-384](../../../src/EyeCtrl.Core/GazeEngine.cs:383)) — after publishing `CursorMoved`, so the toolbar still gets gaze, but before the fixation detector ever sees it.

**Files:**
- Create: `tests/EyeCtrl.E2E.Tests/Journeys/SuppressionTests.cs`
- Modify: `tests/EyeCtrl.E2E.Tests/Journeys/LeftClickJourneyTests.cs` (extract the shared script)

- [ ] **Step 1: Extract the J1 script so J10 can reuse it verbatim**

Create `tests/EyeCtrl.E2E.Tests/Journeys/ClickScript.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Journeys;

using EyeCtrl.E2E.Tests.Infrastructure;

/// <summary>
/// The gaze script that produces one left click in <c>SelectionMode=None</c>.
/// <para>
/// Shared verbatim between J1 (asserts a click lands) and J10 (asserts a paused app produces
/// nothing). If J10 used its own script, it could pass because the script was wrong rather than
/// because pause works — which is the entire question J10 exists to answer.
/// </para>
/// </summary>
public static class ClickScript
{
    /// <summary>Where the journey looks and, in the unpaused case, clicks.</summary>
    public static readonly (int X, int Y) GazeTarget = (700, 450);

    /// <summary>Plays the full look-hold-activate-select sequence.</summary>
    public static async Task PlayAsync(int circleSize, CancellationToken token)
    {
        var ringRadius = (int)ActionMenuGeometry.RingRadiusLogical(circleSize);
        var leftClickButton = (X: GazeTarget.X, Y: GazeTarget.Y - ringRadius);

        // Hold on the target: 300ms fixation + 700ms activation dwell + margin. The activation
        // button's dwell target sits at the fixation point (ActivationButtonViewModel.cs:33-37),
        // so gaze holds still rather than chasing the button drawn 75px to the side.
        await GazePath.PlayAsync(
            GazePath.SaccadeThenHold((100, 100), GazeTarget, saccadeMs: 210, holdMs: 1200), token);

        // LeftClick is index 0 on the default 8-item ring => straight up. Hold past the 600ms
        // action-menu dwell.
        await GazePath.PlayAsync(
            GazePath.SaccadeThenHold(GazeTarget, leftClickButton, saccadeMs: 150, holdMs: 900), token);
    }
}
```

Then in `LeftClickJourneyTests.cs`, replace the two `GazePath.PlayAsync(...)` calls and the `gazeTarget`/`ringRadius`/`leftClickButton` locals with:

```csharp
        var gazeTarget = ClickScript.GazeTarget;
        target.Clear();
        await ClickScript.PlayAsync(app.CircleSize, abort.Token);
        deadline.ThrowIfExpired();
```

- [ ] **Step 2: Write the failing test**

Create `tests/EyeCtrl.E2E.Tests/Journeys/SuppressionTests.cs`:

```csharp
namespace EyeCtrl.E2E.Tests.Journeys;

using EyeCtrl.Core.Models;
using EyeCtrl.E2E.Tests.Infrastructure;

/// <summary>
/// The negative journeys. A spurious click is worse than a missed one, so these assert on the
/// ABSENCE of input — which only means something because J1 proves the same script does produce
/// a click when it should.
/// </summary>
[Trait("Category", "E2E")]
public class SuppressionTests
{
    private readonly ITestOutputHelper _output;

    public SuppressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task J10_WhilePaused_TheFullClickScriptDispatchesNothing()
    {
        E2EGate.SkipUnlessRunnable();

        using var cursor = CursorGuard.Capture();
        using var abort = new AbortWatch();
        using var deadline = new RunDeadline(TimeSpan.FromSeconds(60));

        await using var target = TargetHarness.Create();
        await target.StartAsync(TimeSpan.FromSeconds(15));
        deadline.Register(target.Process);

        await using var app = AppHarness.Create(SelectionMode.None, circleSize: 40);
        await app.StartAsync(TimeSpan.FromSeconds(30), startPaused: true);
        deadline.Register(app.Process);

        target.Clear();
        await ClickScript.PlayAsync(app.CircleSize, abort.Token);

        var events = await target.DrainAsync(quietFor: TimeSpan.FromMilliseconds(700));

        foreach (var e in events)
            _output.WriteLine($"UNEXPECTED {e.Kind} at ({e.X:F1},{e.Y:F1})");

        Check.That(abort.Aborted).IsFalse();
        Check.That(events).IsEmpty();
    }

    [Fact]
    public async Task J11_AbandonedFixation_DispatchesNothing()
    {
        E2EGate.SkipUnlessRunnable();

        using var cursor = CursorGuard.Capture();
        using var abort = new AbortWatch();
        using var deadline = new RunDeadline(TimeSpan.FromSeconds(60));

        await using var target = TargetHarness.Create();
        await target.StartAsync(TimeSpan.FromSeconds(15));
        deadline.Register(target.Process);

        await using var app = AppHarness.Create(SelectionMode.None, circleSize: 40);
        await app.StartAsync(TimeSpan.FromSeconds(30));
        deadline.Register(app.Process);

        target.Clear();

        // Hold for 550ms: past the 300ms FixationDetectionMs (so the activation button appears
        // and its dwell begins accumulating) but well short of the 700ms ActivationButtonDwellMs
        // needed to open the action menu. Then look away — 400px is far outside the 50px
        // FixationRadiusPixels, so OnFixationEnded fires and the state returns to FreeLook.
        await GazePath.PlayAsync(
            GazePath.SaccadeThenHold((100, 100), (700, 450), saccadeMs: 210, holdMs: 550), abort.Token);
        await GazePath.PlayAsync(
            GazePath.SaccadeThenHold((700, 450), (200, 850), saccadeMs: 210, holdMs: 900), abort.Token);

        var events = await target.DrainAsync(quietFor: TimeSpan.FromMilliseconds(700));

        foreach (var e in events)
            _output.WriteLine($"UNEXPECTED {e.Kind} at ({e.X:F1},{e.Y:F1})");

        Check.That(abort.Aborted).IsFalse();
        Check.That(events).IsEmpty();
    }
}
```

Note J11's second hold: 900ms at the *new* point is itself longer than fixation + activation dwell, so it would open an action menu there. That is fine and deliberate — an open menu dispatches nothing on its own, and asserting zero events proves the abandoned first fixation produced no click. If a later run shows a click from the second hold, shorten that hold to 500ms.

- [ ] **Step 3: Run to verify they skip without the opt-in**

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~SuppressionTests"
```

Expected: 2 skipped.

- [ ] **Step 4: Ask, then run**

**STOP.** Two more cursor-taking runs, ~30s total. Ask for an explicit go-ahead.

```bash
$env:EYECTRL_E2E_TESTS=1; dotnet test tests/EyeCtrl.E2E.Tests --filter "FullyQualifiedName~SuppressionTests"
```

Expected: `Fehler: 0, erfolgreich: 2`.

**If either fails, that is a real defect, not a test problem.** A recorded event under J10 means pause does not suppress dispatch; under J11 it means an abandoned fixation still clicks. Report the recorded events verbatim and stop — do not adjust timings to make the assertion pass.

- [ ] **Step 5: Confirm J1 still passes after the script extraction**

```bash
$env:EYECTRL_E2E_TESTS=1; dotnet test tests/EyeCtrl.E2E.Tests --filter "Category=E2E"
```

Expected: all E2E tests green — `TargetHarnessTests` (2), `CoordinateRoundTripTests` (1), `SafetyHarnessTests` cursor test (1), `LeftClickJourneyTests` (1), `SuppressionTests` (2).

- [ ] **Step 6: Commit**

```bash
git add tests/EyeCtrl.E2E.Tests
git commit -m "test(e2e): J10 and J11 — paused and abandoned fixations dispatch nothing"
```

---

## Task 12: Document how to run it

**Files:**
- Modify: `AGENTS.md`
- Modify: `docs/superpowers/specs/2026-08-08-core-e2e-test-plan-design.md`

- [ ] **Step 1: Add the E2E commands to `AGENTS.md`**

In the `## Test` fenced block, after the existing `Category=UI` line, add:

```bash
dotnet test tests/EyeCtrl.E2E.Tests --filter "Category=E2E"   # end-to-end (needs EYECTRL_E2E_TESTS=1)
```

Then, immediately after that block, add:

```markdown
End-to-end tests (`Category=E2E`, `tests/EyeCtrl.E2E.Tests`) launch the real app, take the OS
cursor for several seconds per journey, and send real `SendInput` clicks into a full-screen
recording target (`tests/EyeCtrl.TestTarget`). They are gated twice — the `Category=E2E` trait
and the `EYECTRL_E2E_TESTS=1` opt-in — and additionally skip if another `EyeCtrl.App` holds the
single-instance mutex, because a second instance exits at startup and every journey would fail
as "Toolbar_Window did not appear".

They run against a scratch `%APPDATA%` profile via `EYECTRL_APPDATA_DIR` (see
`EyeCtrl.Core.Services.AppPaths`), so a run cannot read or rewrite the live configuration. Never
launch one without asking first. Not run in CI, for the same reason `Category=UI` is not: the
`windows-latest` runner has no interactive desktop session.
```

- [ ] **Step 2: Add the `EYECTRL_APPDATA_DIR` override to the Dependencies & Quirks section**

In `AGENTS.md`, under `## Dependencies & Quirks`, add a bullet:

```markdown
- **`EYECTRL_APPDATA_DIR`** overrides `%APPDATA%\EyeCtrl` for settings, logs, models, calibration
  and quick actions. Resolved once in `EyeCtrl.Core.Services.AppPaths`; the three former
  hard-coded `Path.Combine` sites now delegate there. Exists so end-to-end runs get a scratch
  profile instead of mutating the live one.
```

- [ ] **Step 3: Mark the spec's build-order progress**

In `docs/superpowers/specs/2026-08-08-core-e2e-test-plan-design.md`, change the `**Status:**` line to:

```markdown
**Status:** Build-order steps 1–4 implemented (see
`docs/superpowers/plans/2026-08-10-core-e2e-test-plan.md`). Steps 5–6 (J2–J9, tier 2) not started.
```

- [ ] **Step 4: Run the full headless suite one more time**

```bash
dotnet test --filter "Category!=UI"
```

Expected: `Fehler: 0` on every assembly. Verify by counting `Fehler <FQN>` lines, not by grepping for "Failed" — the output is German and a naive grep reports green on a red run.

- [ ] **Step 5: Commit**

```bash
git add AGENTS.md docs/superpowers/specs/2026-08-08-core-e2e-test-plan-design.md
git commit -m "docs(testing): document the end-to-end suite and the appdata override"
```

---

## What this plan deliberately leaves out

Carried to a follow-up plan once the tolerance and execution mode are known:

- **J2–J9** — the centroid-vs-menu-traversal test, right/double click, `AutoSelection` snapping, `ManualSelection` precise dwell, drag, joystick scroll, discrete scroll, keyboard. J9 also needs keyboard events added to `RecordedEvent`, which the `Kind` discriminator makes additive.
- **All of tier 2** — the nine breadth smokes (toolbar, keyboard→text, settings persistence round-trip, zoom entry, quick actions, eye care, adaptation, pause, tray).
- **Unattended execution** — gated on Task 3's verdict. If the alternate desktop does not work, every run stays supervised and needs an explicit go-ahead, which is the assumption baked into this plan.
- **Horizontal wheel recording** — `WM_MOUSEHWHEEL` needs an `HwndSource` hook that WPF does not surface. Only J8 needs it.
