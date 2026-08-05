# Window-Management Toolbar Region Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Minimize, Maximize/Restore, and Close buttons to the existing auto-hiding toolbar, acting on the foreground window.

**Architecture:** Three new toolbar buttons dispatch through the existing `ToolbarViewModel.OnTargetActivated` switch → a new `WindowActionRequestedMessage` → a synchronous `EventCoordinator` recipient → new `IWindowManager` ops backed by Win32 P/Invokes. Targeting uses `GetForegroundWindow` (no gaze resolver). A target-lifecycle contract (`DwellDetector.ResetAllState` + an activity flag) ensures a hidden/calibrating toolbar cannot dwell — the load-bearing safety fix that makes no-confirm Close defensible.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (`[ObservableProperty]`), NFluent 3.1.0 + xUnit v3, `[LibraryImport]` P/Invoke.

**Spec:** `docs/superpowers/specs/2026-08-05-window-management-toolbar-region-design.md` (approved, round-3). Read it before starting; this plan does not repeat its rationale.

## Global Constraints

- **Conventions:** file-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled project-wide. New P/Invokes use `[LibraryImport]`, not `[DllImport]`.
- **Tests:** NFluent (`Check.That(...)`), not xUnit asserts. No mocking library — private nested fakes. Test classes named `XxxTests`, underscore-PascalCase methods. Don't run two test suites on this box at once (named-pipe contention across worktrees).
- **`WindowAction` lives in `EyeCtrl.Core.Models`** (pinned — `InteractionMessages.cs` already imports that namespace; no reverse dependency).
- **No `InteractionManager`/`InteractionState`/`InteractionAction`/`ActionMenuViewModel`/`GazeEngineAdapter` changes** — this is the whole point of B2. If a task seems to require touching those, stop and reconsider.
- **Close = no-confirm, conditional on Task 7 (target-lifecycle) being implemented.** Do not ship Close without Task 7.
- **Don't consolidate `ExternalDwellService.RootWindowAtPointImpl`** — it feeds an `h == r` equality gate; retargeting breaks the SDK.

## File Structure

**New files (Core, no Win32):**
- `src/EyeCtrl.Core/Models/WindowAction.cs` — enum `{Minimize, MaximizeRestore, Close}`
- `src/EyeCtrl.Core/Models/WindowStateOps.cs` — static class with pure `DecideWindowOp` + `WindowStateOp` enum
- `src/EyeCtrl.Core/Models/ForegroundWindowEligibility.cs` — pure eligibility predicate + input record

**New files (App/Platform):** none (extend existing).

**Modified files:**
- `src/EyeCtrl.Platform/Input/NativeMethods.cs` — 8 new P/Invokes + constants
- `src/EyeCtrl.Platform/Services/Win32WindowManager.cs` + `src/EyeCtrl.Core/Interfaces/IWindowManager.cs` — 4 new methods
- `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` — `WindowActionRequestedMessage`
- `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` — 3 buttons, 3 dispatch cases, activity flag, `SetWindowActionsVisible`
- `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs` — *not touched* (listed for clarity; B2 doesn't use it)
- `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` — widen `Initialize`, 3 View buttons
- `src/EyeCtrl.App/Services/EventCoordinator.cs` — recipient + `HandleWindowAction` + lifecycle wiring
- `src/EyeCtrl.App/Services/IToolbarVisibility.cs` + `ToolbarVisibilityAdapter.cs` — `SetActive`
- `src/EyeCtrl.Core/Settings/ToolbarSettings.cs` + `src/EyeCtrl.App/Services/SettingsSnapshots.cs` — `ShowWindowActions`

---

## Task 1: Pure Core types (`WindowAction`, `WindowStateOps`)

**Files:**
- Create: `src/EyeCtrl.Core/Models/WindowAction.cs`
- Create: `src/EyeCtrl.Core/Models/WindowStateOps.cs`
- Test: `tests/EyeCtrl.Core.Tests/Models/WindowStateOpsTests.cs`

**Interfaces:**
- Produces: `WindowAction { Minimize, MaximizeRestore, Close }`; `WindowStateOp { Maximize, Restore }`; `WindowStateOps.DecideWindowOp(bool isZoomed, bool isIconic) → WindowStateOp`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Core.Tests.Models;

public class WindowStateOpsTests
{
    [Fact]
    public void DecideWindowOp_NormalWindow_Maximizes()
    {
        Check.That(WindowStateOps.DecideWindowOp(isZoomed: false, isIconic: false))
             .IsEqualTo(WindowStateOp.Maximize);
    }

    [Fact]
    public void DecideWindowOp_Maximized_Restores()
    {
        Check.That(WindowStateOps.DecideWindowOp(isZoomed: true, isIconic: false))
             .IsEqualTo(WindowStateOp.Restore);
    }

    [Fact]
    public void DecideWindowOp_MinimizedNormal_Restores()
    {
        // Minimized-from-normal: un-minimize.
        Check.That(WindowStateOps.DecideWindowOp(isZoomed: false, isIconic: true))
             .IsEqualTo(WindowStateOp.Restore);
    }

    [Fact]
    public void DecideWindowOp_MinimizedFromMaximized_Restores()
    {
        // IsIconic wins over IsZoomed — un-minimize first (a minimized-from-maximized window
        // keeps WS_MAXIMIZE, so IsZoomed is also true, but the right op is Restore, not a
        // second maximize). This is the truth-table row v1 duplicated incorrectly.
        Check.That(WindowStateOps.DecideWindowOp(isZoomed: true, isIconic: true))
             .IsEqualTo(WindowStateOp.Restore);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~WindowStateOpsTests"`
Expected: FAIL — `WindowStateOps` not found.

- [ ] **Step 3: Write minimal implementation**

`src/EyeCtrl.Core/Models/WindowAction.cs`:
```csharp
namespace EyeCtrl.Core.Models;

/// <summary>Window-management actions fired from the toolbar's window-actions region.</summary>
public enum WindowAction
{
    Minimize,
    MaximizeRestore,
    Close
}
```

`src/EyeCtrl.Core/Models/WindowStateOps.cs`:
```csharp
namespace EyeCtrl.Core.Models;

/// <summary>The toggle decision for the Maximize/Restore button.</summary>
public enum WindowStateOp
{
    Maximize,
    Restore
}

/// <summary>
/// Pure deciding logic for the Maximize/Restore toolbar button. No Win32 dependency —
/// the caller reads <c>IsZoomed</c>/<c>IsIconic</c> and passes the booleans here.
/// </summary>
public static class WindowStateOps
{
    /// <summary>
    /// Minimized windows un-minimize first (IsIconic takes precedence), so a window
    /// minimized-from-maximized restores rather than maximizing a second time.
    /// </summary>
    public static WindowStateOp DecideWindowOp(bool isZoomed, bool isIconic)
        => (isZoomed, isIconic) switch
        {
            (_, true) => WindowStateOp.Restore,  // minimized → un-minimize first
            (true, _) => WindowStateOp.Restore,  // maximized → restore
            _ => WindowStateOp.Maximize          // normal → maximize
        };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~WindowStateOpsTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/WindowAction.cs src/EyeCtrl.Core/Models/WindowStateOps.cs tests/EyeCtrl.Core.Tests/Models/WindowStateOpsTests.cs
git commit -m "feat(window-mgmt): pure Core types — WindowAction enum + WindowStateOps.DecideWindowOp"
```

---

## Task 2: Foreground-window eligibility predicate

The pure predicate that decides whether a foreground HWND is safe to operate on. Factored out of the eventual `Win32WindowManager` impl so it's unit-testable without Win32.

**Files:**
- Create: `src/EyeCtrl.Core/Models/ForegroundWindowEligibility.cs`
- Test: `tests/EyeCtrl.Core.Tests/Models/ForegroundWindowEligibilityTests.cs`

**Interfaces:**
- Produces: `ForegroundWindowCandidate` record; `ForegroundWindowEligibility.IsEligible(ForegroundWindowCandidate) → bool`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class ForegroundWindowEligibilityTests
{
    private static readonly IntPtr Shell = new(0x10001);
    private static readonly IntPtr App = new(0x20002);
    private const uint AppPid = 1234u;
    private const uint SelfPid = 5678u;

    private static bool Eligible(IntPtr hwnd, bool isWindow, IntPtr shellHwnd,
        string className, uint processId)
        => ForegroundWindowEligibility.IsEligible(new ForegroundWindowCandidate(
            hwnd, isWindow, shellHwnd, className, processId, SelfPid));

    [Fact]
    public void NormalAppWindow_IsEligible()
        => Check.That(Eligible(App, isWindow: true, Shell, "Notepad", AppPid)).IsTrue();

    [Fact]
    public void ZeroHwnd_IsRejected()
        => Check.That(Eligible(IntPtr.Zero, isWindow: false, Shell, "", AppPid)).IsFalse();

    [Fact]
    public void DestroyedHwnd_IsRejected()
        => Check.That(Eligible(App, isWindow: false, Shell, "Notepad", AppPid)).IsFalse();

    [Fact]
    public void ShellDesktopHwnd_IsRejected()
        => Check.That(Eligible(Shell, isWindow: true, Shell, "Progman", 0)).IsFalse();

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void ShellClasses_AreRejected(string className)
        => Check.That(Eligible(App, isWindow: true, Shell, className, AppPid)).IsFalse();

    [Fact]
    public void OwnProcess_IsRejected()
        => Check.That(Eligible(App, isWindow: true, Shell, "EyeCtrl.Overlay", SelfPid)).IsFalse();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ForegroundWindowEligibilityTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Write minimal implementation**

`src/EyeCtrl.Core/Models/ForegroundWindowEligibility.cs`:
```csharp
namespace EyeCtrl.Core.Models;

/// <summary>Inputs to the foreground-window eligibility check. Pure data — no Win32.</summary>
public readonly record struct ForegroundWindowCandidate(
    IntPtr Hwnd,
    bool IsWindow,
    IntPtr ShellWindowHwnd,
    string ClassName,
    uint ProcessId,
    uint OwnProcessId);

/// <summary>
/// Decides whether a foreground HWND is safe to minimize/maximize/close. Pure: the caller
/// (Win32WindowManager) gathers the Win32 facts and passes them in. Fail-closed: anything
/// not explicitly eligible is rejected.
/// </summary>
public static class ForegroundWindowEligibility
{
    // Progman is the usual shell desktop; WorkerW layers over it under animated wallpaper
    // (NOT returned by GetShellWindow — the class denylist carries it); the two taskbar
    // classes cover single + multi-monitor.
    private static readonly HashSet<string> ShellClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
    };

    public static bool IsEligible(ForegroundWindowCandidate c)
    {
        if (c.Hwnd == IntPtr.Zero) return false;
        if (!c.IsWindow) return false;
        if (c.Hwnd == c.ShellWindowHwnd) return false;
        if (ShellClasses.Contains(c.ClassName)) return false;
        if (c.ProcessId == c.OwnProcessId) return false;
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ForegroundWindowEligibilityTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/ForegroundWindowEligibility.cs tests/EyeCtrl.Core.Tests/Models/ForegroundWindowEligibilityTests.cs
git commit -m "feat(window-mgmt): pure ForegroundWindowEligibility predicate (shell/self/zero/!IsWindow)"
```

---

## Task 3: Win32 P/Invokes

Add the 8 new P/Invokes + constants to `NativeMethods`. No tests here — these are thin declarations exercised through `Win32WindowManager` (Task 4) and the manual smoke matrix.

**Files:**
- Modify: `src/EyeCtrl.Platform/Input/NativeMethods.cs` (append before the constants block, then add constants in the constants block)

**Interfaces:**
- Produces: `NativeMethods.GetForegroundWindow`, `GetShellWindow`, `GetClassNameW`, `ShowWindow`, `PostMessageW`, `IsZoomed`, `IsIconic`, `IsWindow` + `SW_*`/`WM_CLOSE` constants.

- [ ] **Step 1: Add the P/Invoke declarations**

Append into the `NativeMethods` class body (after the existing `AllowSetForegroundWindow` declaration, before the DWM import — keep window-activation calls grouped). Match the file's `[LibraryImport]` + `[return: MarshalAs(UnmanagedType.Bool)]` style exactly:

```csharp
    // ── Window-management (toolbar window-actions region) ──

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetShellWindow();

    // [LibraryImport] does not support StringBuilder (SYSLIB1051). Use [Out] char[].
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);
```

- [ ] **Step 2: Add the constants**

In the constants block (after `DWMWA_CLOAKED`):

```csharp
    // ShowWindow nCmdShow values + WM_CLOSE for the window-actions region.
    internal const int SW_MINIMIZE = 6;
    internal const int SW_MAXIMIZE = 3;
    internal const int SW_RESTORE = 9;
    internal const uint WM_CLOSE = 0x0010;
```

- [ ] **Step 3: Verify the build compiles**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj -clp:ErrorsOnly`
Expected: 0 errors. (The P/Invokes aren't called yet — source generators emit the partials.)

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Platform/Input/NativeMethods.cs
git commit -m "feat(window-mgmt): Win32 P/Invokes for foreground-window operations"
```

---

## Task 4: `IWindowManager` ops + `Win32WindowManager` impl

Add the 4 new methods. `TryGetEligibleForegroundWindow` returns `IntPtr?` (fail-closed); the ops take a validated `IntPtr`.

**Files:**
- Modify: `src/EyeCtrl.Core/Interfaces/IWindowManager.cs`
- Modify: `src/EyeCtrl.Platform/Services/Win32WindowManager.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/TaskSwitcherViewModelTests.cs` (update `FakeWindowManager` — compile fix)

**Interfaces:**
- Consumes: `ForegroundWindowEligibility.IsEligible` (Task 2), `WindowStateOps.DecideWindowOp` (Task 1), `NativeMethods` (Task 3).
- Produces: `IWindowManager.TryGetEligibleForegroundWindow()`, `Minimize(IntPtr)`, `MaximizeOrRestore(IntPtr)`, `CloseWindow(IntPtr)`.

- [ ] **Step 1: Widen the interface**

Add to `IWindowManager` (and update the class XML doc to mention window management alongside enumeration):

```csharp
    /// <summary>
    /// Returns the foreground window's HWND if it is eligible for window-management actions
    /// (not the shell, not this process, still a valid window), or <c>null</c> if ineligible.
    /// Fail-closed: callers must no-op on <c>null</c>. Read at op-fire time, not stashed.
    /// </summary>
    IntPtr? TryGetEligibleForegroundWindow();

    /// <summary>Minimizes the window. Fire-and-forget after the eligibility guard.</summary>
    void Minimize(IntPtr hwnd);

    /// <summary>Maximizes a normal window or restores a maximized/minimized one.</summary>
    void MaximizeOrRestore(IntPtr hwnd);

    /// <summary>Closes the window gracefully via WM_CLOSE (the app's save-changes prompt fires).</summary>
    /// <returns><c>true</c> if the message was posted; UIPI-blocked sends to elevated windows return <c>false</c>.</returns>
    bool CloseWindow(IntPtr hwnd);
```

- [ ] **Step 2: Update the Task Switcher test fake (compile fix)**

In `tests/EyeCtrl.ViewModels.Tests/TaskSwitcherViewModelTests.cs`, the private `FakeWindowManager` class (`:123-132`) must implement the 4 new methods. Add stubs that record calls (the Task Switcher doesn't use them, but the interface now requires them):

```csharp
        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
```

- [ ] **Step 3: Implement on `Win32WindowManager`**

Add to `Win32WindowManager.cs`. `TryGetEligibleForegroundWindow` gathers the Win32 facts and delegates to the pure predicate (Task 2):

```csharp
    /// <inheritdoc />
    public IntPtr? TryGetEligibleForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var shellHwnd = NativeMethods.GetShellWindow();

        // GetClassName: allocate a buffer (256 is the documented max for a window class name).
        var classNameBuffer = new char[256];
        int len = NativeMethods.GetClassName(hwnd, classNameBuffer, classNameBuffer.Length);
        var className = len > 0 ? new string(classNameBuffer, 0, len) : string.Empty;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        var candidate = new ForegroundWindowCandidate(
            hwnd,
            IsWindow: NativeMethods.IsWindow(hwnd),
            ShellWindowHwnd: shellHwnd,
            ClassName: className,
            ProcessId: pid,
            OwnProcessId: (uint)Environment.ProcessId);

        return ForegroundWindowEligibility.IsEligible(candidate) ? hwnd : null;
    }

    /// <inheritdoc />
    public void Minimize(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    }

    /// <inheritdoc />
    public void MaximizeOrRestore(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;
        var op = WindowStateOps.DecideWindowOp(
            isZoomed: NativeMethods.IsZoomed(hwnd),
            isIconic: NativeMethods.IsIconic(hwnd));
        NativeMethods.ShowWindow(hwnd, op == WindowStateOp.Maximize
            ? NativeMethods.SW_MAXIMIZE
            : NativeMethods.SW_RESTORE);
    }

    /// <inheritdoc />
    public bool CloseWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;
        // WM_CLOSE is graceful: DefWindowProc derives it from SC_CLOSE. For a modal dialog
        // it posts IDCANCEL (like Esc) — closes the dialog, NOT the owning app.
        return NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
```

Add `using EyeCtrl.Core.Models;` to the `Win32WindowManager.cs` usings (for `ForegroundWindowCandidate`, `ForegroundWindowEligibility`, `WindowStateOps`, `WindowStateOp`).

- [ ] **Step 4: Build + run the Task Switcher tests (compile-fix verification)**

Run: `dotnet build EyeCtrl.slnx -clp:ErrorsOnly` then `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~TaskSwitcherViewModelTests"`
Expected: build 0 errors; Task Switcher tests pass (the fake now compiles + still returns the enumerated windows for the existing tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/IWindowManager.cs src/EyeCtrl.Platform/Services/Win32WindowManager.cs tests/EyeCtrl.ViewModels.Tests/TaskSwitcherViewModelTests.cs
git commit -m "feat(window-mgmt): IWindowManager.TryGetEligibleForegroundWindow + Minimize/MaximizeOrRestore/CloseWindow"
```

---

## Task 5: `WindowActionRequestedMessage` + `EventCoordinator` recipient

The message + the synchronous recipient that calls the ops. **No `BeginInvoke`** — the toolbar dwell chain is already on the UI dispatcher, so the recipient runs on the same tick as dwell completion (spec §4 BLOCKER A.1).

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/HandleWindowActionTests.cs` (new — or extend an existing EventCoordinator test class)

**Interfaces:**
- Consumes: `WindowAction` (Task 1), `IWindowManager` ops (Task 4).
- Produces: `WindowActionRequestedMessage`; `EventCoordinator.HandleWindowAction`.

- [ ] **Step 1: Add the message**

In `InteractionMessages.cs`, near the other action messages (after `TaskSwitcherSelectionMessage`, `:146`):

```csharp
/// <summary>
/// Sent by the toolbar's window-actions region when the user dwells Minimize/MaximizeRestore/Close.
/// The recipient queries the foreground window synchronously (no HWND on the message —
/// <see cref="EyeCtrl.Core.Interfaces.IWindowManager.TryGetEligibleForegroundWindow"/> is read
/// at op-fire time in the same tick as the dwell completion).
/// </summary>
public record WindowActionRequestedMessage(WindowAction Action);
```

- [ ] **Step 2: Write the failing test for `HandleWindowAction`**

The recipient must route each action to the right op on the eligible foreground HWND, and no-op (+ log) when the foreground is ineligible. Use a fake `IWindowManager` that records calls:

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

// Minimal fake: records the last op + whether eligibility was queried.
// (Full EventCoordinator construction is heavy; if a test harness already exists
// for EventCoordinator recipients, prefer extending it. Otherwise test HandleWindowAction
// as a unit by extracting it to take an IWindowManager + ILogger parameter, OR test via
// the recipient registration with a constructed context. See TaskSwitcherViewModelTests
// for the fake-IWindowManager pattern.)

public class HandleWindowActionTests
{
    // Sketch: assert that WindowAction.Minimize → fake.Minimize(lastForegroundHwnd),
    // MaximizeRestore → fake.MaximizeOrRestore, Close → fake.CloseWindow.
    // Assert that when TryGetEligibleForegroundWindow returns null, NO op is called.
    // (The exact test harness depends on EventCoordinator's existing test setup —
    // check tests/EyeCtrl.App.Tests/Services/ for the established pattern and mirror it.
    // The key assertions are the three routing cases + the null-eligibility no-op.)
}
```

> **Note:** `EventCoordinator` has substantial constructor dependencies. Before writing the real test, check `tests/EyeCtrl.App.Tests/Services/` for an existing `EventCoordinatorTests` harness. If one exists, extend it. If not, the cleanest seam is to extract `HandleWindowAction` to a small testable helper that takes `(IWindowManager, ILogger)` — but **do not over-extract**; the spec wants it as a method on `EventCoordinator`. Prefer the existing-test-harness route.

- [ ] **Step 3: Register the recipient in `EventCoordinator`**

Near the `TaskSwitcherSelectionMessage` registration (`:135-136`). **Critical: no `BeginInvoke`** — call `HandleWindowAction` directly (the spec's BLOCKER A.1 fix):

```csharp
        _context.Messenger.Register<EventCoordinator, WindowActionRequestedMessage>(this, (_, msg) =>
            HandleWindowAction(msg.Action));
```

- [ ] **Step 4: Implement `HandleWindowAction`**

Add as a method on `EventCoordinator` (near `HandleTaskSwitcherSelection`, `:141`):

```csharp
    private void HandleWindowAction(WindowAction action)
    {
        if (_context == null) return;

        var hwnd = _context.WindowManager.TryGetEligibleForegroundWindow();
        if (hwnd is null)
        {
            // Foreground was the shell, this process, zero, or an invalid/destroyed window.
            // No-op; the user's dwell completed but there is nothing safe to act on.
            // (Logging at Information per spec §3 — add if an ILogger<EventCoordinator> is available.)
            return;
        }

        switch (action)
        {
            case WindowAction.Minimize:
                _context.WindowManager.Minimize(hwnd.Value);
                break;
            case WindowAction.MaximizeRestore:
                _context.WindowManager.MaximizeOrRestore(hwnd.Value);
                break;
            case WindowAction.Close:
                var posted = _context.WindowManager.CloseWindow(hwnd.Value);
                // posted == false means UIPI-blocked (elevated target). Log at Information if logger available.
                break;
        }
    }
```

Confirm `_context.WindowManager` exists — it does (`HandleTaskSwitcherSelection` uses `_context.WindowManager.BringToFront`, `:144`). Add `using EyeCtrl.Core.Models;` if `WindowAction` isn't already imported.

- [ ] **Step 5: Build + run the new test**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HandleWindowActionTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/HandleWindowActionTests.cs
git commit -m "feat(window-mgmt): WindowActionRequestedMessage + synchronous HandleWindowAction recipient"
```

---

## Task 6: Toolbar buttons + dispatch (View + VM arrays)

Add the three buttons to both arrays (View renders them; VM creates them) and the three dispatch cases in `OnTargetActivated`. Gate on `ShowWindowActions` (Task 9 adds the settings field + reconciliation; for now read `true` — the field is added before this ships, but the button additions can land first with a TODO-free inline check against a field that Task 9 introduces).

> **Ordering note:** This task adds the buttons assuming `ShowWindowActions` exists. The cleanest sequencing is to do Task 9 (settings field + snapshot) immediately after this one, before any end-to-end test. But the button arrays + dispatch cases are independent enough to commit on their own.

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` (`:66-76` array, `:131-144` switch)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (`:39-46` array, `:31` signature)
- Test: extend an existing `ToolbarViewModelTests` or add `ToolbarWindowActionDispatchTests`

**Interfaces:**
- Consumes: `WindowAction` (Task 1), `WindowActionRequestedMessage` (Task 5).

- [ ] **Step 1: Write the failing test for dispatch**

Test that dwelling each of the three new ids sends the right `WindowActionRequestedMessage`. Use a `WeakReferenceMessenger`, register a recipient, drive `OnTargetActivated` directly (the established pattern in `ToolbarViewModelTests.cs:144-160`):

```csharp
    [Theory]
    [InlineData("Minimize", WindowAction.Minimize)]
    [InlineData("MaximizeRestore", WindowAction.MaximizeRestore)]
    [InlineData("Close", WindowAction.Close)]
    public void OnTargetActivated_WindowButton_SendsWindowActionMessage(string id, WindowAction expected)
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        WindowAction? received = null;
        messenger.Register<Recipient, WindowActionRequestedMessage>(default, (_, msg) => received = msg.Action);

        // OnTargetActivated is protected; drive it via Receive on a positioned button, mirroring
        // GazeDwellOnPositionedButton_ActivatesIt. Position the button then dwell at its center.
        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition(id, center);
        var dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(received).IsEqualTo(expected);
    }
```

(`Recipient` is a tiny placeholder `readonly struct Recipient;` for the messenger registration, or use the existing pattern in the test file.) Add `using EyeCtrl.Core.Models;` + `using EyeCtrl.ViewModels.Messages;` to the test file.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OnTargetActivated_WindowButton"`
Expected: FAIL — the ids aren't in the VM's button array, so `UpdateButtonScreenPosition` won't find them / dwell won't fire them.

- [ ] **Step 3: Add the three buttons to the VM array**

In `ToolbarViewModel.cs:66-76`, append to the `buttons` array (after `ResetAdaptation`):

```csharp
            ("Minimize", "\u2581"),        // ▁ U+2581 (BMP — avoids Supplementary-Plane tofu)
            ("MaximizeRestore", "\u25A1"), // □ U+25A1
            ("Close", "\u2715"),           // ✕ U+2715
```

- [ ] **Step 4: Add the three dispatch cases**

In `ToolbarViewModel.cs:131-144` `OnTargetActivated` switch, after the `ResetAdaptation` case:

```csharp
            case "Minimize":        Messenger.Send(new WindowActionRequestedMessage(WindowAction.Minimize)); break;
            case "MaximizeRestore": Messenger.Send(new WindowActionRequestedMessage(WindowAction.MaximizeRestore)); break;
            case "Close":           Messenger.Send(new WindowActionRequestedMessage(WindowAction.Close)); break;
```

Add `using EyeCtrl.Core.Models;` + `using EyeCtrl.ViewModels.Messages;` to `ToolbarViewModel.cs` usings.

- [ ] **Step 5: Add the three buttons to the View array**

In `ToolbarWindow.xaml.cs:39-46`, append the same three entries to the View's `(Id, Symbol)` array (must match the VM's ids/symbols exactly — the View reports positions per id, the VM creates dwell targets per id; mismatch = silent no-dwell):

```csharp
            ("Minimize", "\u2581"),
            ("MaximizeRestore", "\u25A1"),
            ("Close", "\u2715"),
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OnTargetActivated_WindowButton"`
Expected: PASS (3 theory cases).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs
git commit -m "feat(window-mgmt): 3 toolbar buttons (Minimize/MaximizeRestore/Close) + dispatch cases"
```

---

## Task 7: Target-lifecycle contract — hide/calibration deactivate dwell (LOAD-BEARING)

This is the safety fix that makes no-confirm Close defensible. Without it, a hidden toolbar can still fire dwell from former screen positions (spec §4 BLOCKER C.2). **Do not ship Close without this task.**

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` (activity flag + gate `Receive`)
- Modify: `src/EyeCtrl.App/Services/IToolbarVisibility.cs` (add `SetActive`)
- Modify: `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs` (implement `SetActive`)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs` (wire calibration)
- Test: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs` (extend)

**Interfaces:**
- Consumes: `DwellDetector.ResetAllState()` (exists, `DwellDetector.cs:86-97`).

- [ ] **Step 1: Write failing tests**

Two tests. First, the safety case — a hidden toolbar must not dwell:

```csharp
    [Fact]
    public void SetActive_False_HiddenToolbarDoesNotDwell()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);
        vm.SetActive(false);  // hidden

        bool fired = false;
        messenger.Register<Recipient, PauseToggledMessage>(default, (_, _) => fired = true);

        var dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(fired).IsFalse();
    }
```

Second, no instant-activation on resume — `ResetAllState` cleared the stale timestamp:

```csharp
    [Fact]
    public void SetActive_True_AfterHide_DoesNotInstantlyActivate()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);

        // Partially dwell, then hide mid-dwell.
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, 100_000)); // partial
        vm.SetActive(false);
        vm.SetActive(true);

        bool fired = false;
        messenger.Register<Recipient, PauseToggledMessage>(default, (_, _) => fired = true);

        // A single sample immediately after resume must NOT complete a dwell — the stale
        // EnteredTimestamp was cleared by ResetAllState on hide.
        vm.Receive(new GazeCursorMovedMessage(center, 200_000));
        Check.That(fired).IsFalse();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SetActive_"`
Expected: FAIL — `SetActive` doesn't exist.

- [ ] **Step 3: Add the activity flag to `ToolbarViewModel`**

Add a field + a public `SetActive` method. Initial state is `true` (dwell active). Gate `Receive`:

```csharp
    private bool _dwellingActive = true;

    /// <summary>
    /// Activates/deactivates dwell processing for the toolbar. Called by the auto-hide
    /// lifecycle (hide → false, show/ForceShow → true) and calibration (start → false,
    /// end → true). When deactivated, gaze samples are not fed to the detector and any
    /// in-progress dwell state is reset (preventing both hidden-toolbar fires and stale-
    /// timestamp instant activation on resume).
    /// </summary>
    public void SetActive(bool active)
    {
        _dwellingActive = active;
        if (!active)
            DwellDetector.ResetAllState();
    }
```

Gate `Receive` (`:92-95`) — replace the unconditional `DwellDetector.Update`:

```csharp
    public override void Receive(GazeCursorMovedMessage message)
    {
        if (!_dwellingActive) return;
        DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }
```

- [ ] **Step 4: Add `SetActive` to `IToolbarVisibility` + implement on the adapter**

`IToolbarVisibility.cs` — add:
```csharp
    /// <summary>
    /// Activates or deactivates toolbar dwell processing. Hide/ForceShow/calibration call this.
    /// When deactivated, in-progress dwell state is reset (DwellDetector.ResetAllState) so the
    /// next activation cannot fire from a stale timestamp.
    /// </summary>
    void SetActive(bool active);
```

`ToolbarVisibilityAdapter.cs` — the adapter holds only `IOverlayManager` + `Dispatcher`. To reach the VM, inject `ToolbarViewModel` into the adapter constructor:

```csharp
public sealed class ToolbarVisibilityAdapter : IToolbarVisibility
{
    private readonly IOverlayManager _overlays;
    private readonly Dispatcher _dispatcher;
    private readonly ToolbarViewModel _toolbarVm;  // NEW

    public ToolbarVisibilityAdapter(IOverlayManager overlays, Dispatcher dispatcher, ToolbarViewModel toolbarVm)  // NEW param
    {
        _overlays = overlays;
        _dispatcher = dispatcher;
        _toolbarVm = toolbarVm;
    }

    // ... existing methods unchanged ...

    public void SetActive(bool active) => _dispatcher.BeginInvoke(() => _toolbarVm.SetActive(active));
}
```

Update the adapter's construction site (search for `new ToolbarVisibilityAdapter(` — it's in `EventCoordinator.SetupToolbarAutoHide`, `:590`) to pass the VM: `new ToolbarVisibilityAdapter(_context.OverlayManager, _context.Dispatcher, _context.ToolbarVM)`.

Then wire the existing `HideToolbar`/`ShowToolbar`/`ForceShow` to also call `SetActive`. In `ToolbarVisibilityAdapter`:
- `ShowToolbar()` body: after `_overlays.Toolbar.Show()`, add `_toolbarVm.SetActive(true);`
- `HideToolbar(bool paused)` body: before `_overlays.Toolbar.Hide()`, add `_toolbarVm.SetActive(false);`
- `ForceShow()` body: after `_overlays.Toolbar.Show()`, add `_toolbarVm.SetActive(true);`

(The adapter already marshals to the dispatcher, so `SetActive` runs on the UI thread alongside the show/hide.)

- [ ] **Step 5: Wire calibration**

In `EventCoordinator`'s calibration handler (`:490-519`), on calibration start call `SetActive(false)` on the toolbar visibility seam, and on calibration end/cancel call `SetActive(true)`. The calibration handler already toggles cursor circle / fixation suppression — add the toolbar-dwell deactivation alongside. Save the `ToolbarVisibilityAdapter` (or `IToolbarVisibility`) reference in `_context` or as a field so the calibration handler can reach it.

> **Calibration/hide refcount invariant (spec §4):** hide and calibration cannot overlap (calibration only starts from controller-state `Visible`; `Feed` can't transition while `_calibrationActive`). The flag is a single bool, not a refcount. Add a one-line code comment at the `SetActive` definition stating this invariant, so a future change that breaks the non-overlap doesn't silently re-enable dwell.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SetActive_"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs src/EyeCtrl.App/Services/IToolbarVisibility.cs src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs
git commit -m "feat(window-mgmt): target-lifecycle contract — hide/calibration deactivates toolbar dwell (ResetAllState + activity flag)"
```

---

## Task 8: Manual smoke matrix (no automated test)

The Win32 ops need a real foreground window; they're covered by the Task 4/5 unit fakes + this manual matrix. **Run this before considering the feature done.**

**Files:** none (verification only — record results in the PR description)

- [ ] **Step 1: Build the app**

Run: `dotnet build EyeCtrl.slnx -clp:ErrorsOnly`
Expected: 0 errors.

- [ ] **Step 2: Run the app with a mouse-gaze provider**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`

- [ ] **Step 3: Smoke matrix — verify each case**

For each: summon the toolbar (gaze past the docked edge), dwell the button, observe.

| Case | Expected |
|------|----------|
| Normal app foreground → Minimize | Window minimizes |
| Normal app foreground → MaximizeRestore (normal) | Window maximizes |
| Maximized foreground → MaximizeRestore | Window restores |
| Normal app foreground → Close | App's save-changes prompt (or closes gracefully) |
| Desktop focused (click desktop first) → Minimize | **No-op** (shell rejected); nothing happens |
| Console window foreground → Close | Console closes (documented: no save prompt) |
| Owned modal dialog foreground → Close | Dialog closes, **owning app stays open** |
| `MB_OK` message box foreground → Close | Warning sound, box stays (documented; `PostMessage` returns true) |
| Elevated app foreground → Close | **No-op** (UIPI-blocked); logged |
| Toolbar auto-hidden → gaze over former Close position | **No dwell fires** (Task 7 contract) |
| Mid-dwell, then auto-hide → re-show | Progress reset; no instant activation on re-show |
| Start calibration → gaze over toolbar | **No dwell fires** during calibration |

- [ ] **Step 4: Record results in the PR description; commit any fixups**

If any case fails, file the fix as a follow-up task (don't expand this plan mid-execution).

---

## Task 9: `ShowWindowActions` settings + live-reconciliation

Adds the settings toggle (default true) and the runtime add/remove path. **Must remove dwell targets atomically on toggle-off** (else an invisible Close — the same hazard as the hidden-toolbar bug).

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ToolbarSettings.cs` (add field)
- Modify: `src/EyeCtrl.App/Services/SettingsSnapshots.cs` (add to `ToolbarSnapshot`)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (widen `Initialize` signature)
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs:107` + `EventCoordinator.cs:1190` (call sites)
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` (`SetWindowActionsVisible`)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs` (notify VM from the snapshot block, `:1187-1203`)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (checkbox + Reset copy, `:730-764`)

**Interfaces:**
- Consumes: Tasks 1-7.

- [ ] **Step 1: Add the settings field + snapshot**

`ToolbarSettings.cs`:
```csharp
    /// <summary>Whether the toolbar shows the Minimize/MaximizeRestore/Close region. Default true.</summary>
    [ObservableProperty] private bool _showWindowActions = true;
```

`SettingsSnapshots.cs` — add `ShowWindowActions` to the `ToolbarSnapshot` record (mirror the existing `IsDocked`/`DockedEdge`/`Size` fields).

- [ ] **Step 2: Widen `ToolbarWindow.Initialize` + update call sites**

Change the signature to take `showWindowActions`:
```csharp
    public void Initialize(double buttonSize, int dwellTimeMs, bool showWindowActions)
```

Gate the View array: only include the three window buttons when `showWindowActions` is true. Update both call sites:
- `OverlayManager.cs:107`: `Toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs, settings.Toolbar.ShowWindowActions);`
- `EventCoordinator.cs:1190`: same, passing the snapshot's `ShowWindowActions`.

(`SetMode` at `:1191` follows `Initialize` and resets `_targetsInitialized` → triggers `UpdateTargetPositions` on the next gaze frame → fresh position reports. `SetMode` also recomputes `Left = (screen.Width - ActualWidth)/2` centering, so adding/removing buttons re-centers the toolbar.)

- [ ] **Step 3: Add `ToolbarViewModel.SetWindowActionsVisible`**

The VM must add/remove the three buttons' bookkeeping atomically. Removal is the safety-critical half — without it, toggle-off leaves invisible dwellable targets:

```csharp
    private static readonly string[] WindowActionIds = { "Minimize", "MaximizeRestore", "Close" };

    /// <summary>
    /// Adds or removes the three window-action buttons (and their dwell targets) at runtime.
    /// Removal is atomic per id: DwellTarget + _positionedButtons + _buttonLookup + Buttons,
    /// else an invisible Close stays dwellable (the hidden-toolbar hazard).
    /// </summary>
    public void SetWindowActionsVisible(bool visible)
    {
        foreach (var id in WindowActionIds)
        {
            if (visible)
            {
                if (_buttonLookup.ContainsKey(id)) continue; // already present
                var btn = new ToolbarButtonViewModel(id, symbol: SymbolFor(id));
                Buttons.Add(btn);
                _buttonLookup[id] = btn;
                // _positionedButtons is deliberately NOT pre-populated — the target is created
                // lazily when the View reports the position (same pattern as the ctor).
            }
            else
            {
                if (_buttonLookup.TryGetValue(id, out var btn))
                {
                    Buttons.Remove(btn);
                    _buttonLookup.Remove(id);
                }
                _positionedButtons.Remove(id);
                DwellDetector.RemoveTarget(id);
            }
        }
    }

    private static string SymbolFor(string id) => id switch
    {
        "Minimize" => "\u2581",
        "MaximizeRestore" => "\u25A1",
        "Close" => "\u2715",
        _ => "?"
    };
```

- [ ] **Step 4: Notify the VM from the live-settings block**

In `EventCoordinator`'s `ToolbarSnapshot`-handling block (`:1187-1203`), after `Initialize` + `SetMode`, call `_context.ToolbarVM.SetWindowActionsVisible(snapshot.ShowWindowActions);`. (The View rebuild happens via `Initialize`; the VM add/remove happens via this call — both driven by the same snapshot change.)

- [ ] **Step 5: Settings UI — checkbox + Reset copy**

In `SettingsWindow.xaml.cs:730-764` (`BuildToolbarPages`), add a "Show window actions" checkbox bound to `ShowWindowActions`, and add the field to the Reset-copy list (`:753-757` copies `IsDocked`/`DockedEdge`/`Size` — add a 4th line for `ShowWindowActions`). The XAML for the checkbox follows the existing toolbar-checkbox pattern in the same file.

- [ ] **Step 6: Write tests**

- `ToolbarViewModelTests`: toggle true→false removes all three from `Buttons` + `_buttonLookup`; the removed ids are no longer dwellable (position a removed id, dwell, no activation). Toggle false→true re-adds them.
- A settings test asserting `ShowWindowActions` defaults true.

- [ ] **Step 7: Run tests + commit**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SetWindowActionsVisible"
git add -A
git commit -m "feat(window-mgmt): ShowWindowActions settings toggle + live-reconciliation"
```

---

## Task 10: Drift-test updates

The pre-existing tests that pin toolbar counts break once the 3 buttons land.

**Files:**
- Modify: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs:13-18` (8 → 11)
- Modify: `tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs:38-50` + `:52-71` (5 → 8; both already stale at 4)

- [ ] **Step 1: Update `ToolbarViewModelTests` count**

The test at `:13-18` asserts 8 logical buttons. After Task 6 the VM has 11 (8 + Minimize/MaximizeRestore/Close). Update to 11.

- [ ] **Step 2: Update `AppLaunchTests` (TWO tests)**

`Toolbar_HasFourButtons` (`:38-50`) and `Toolbar_ButtonsHaveCorrectIds` (`:52-71`, hardcoded 4-id list) — **both are already stale today** (the View renders 5, not 4 — `ResetAdaptation` was added). After Task 6 the View renders 8 (5 + 3 window buttons; *if* `ShowWindowActions=true`, which is the default). Update the count 5 → 8 and extend the id list with `Minimize`, `MaximizeRestore`, `Close`.

- [ ] **Step 3: Run the affected tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"` then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AppLaunchTests" --filter "Category=UI"` (the AppLaunch tests are UI tests — `Category=UI`; they need a display session, so run them locally, not in CI).
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs
git commit -m "test(window-mgmt): update toolbar-count drift tests for +3 window buttons"
```

---

## Self-Review (completed)

**Spec coverage:**
- §2 (targeting/eligibility) → Tasks 2, 4 ✓
- §3 (action set, DecideWindowOp, op semantics, no-confirm Close) → Tasks 1, 4, 7 (Close conditional on 7) ✓
- §4 (placement, dispatch, synchronous recipient, target-lifecycle, auto-hide) → Tasks 5, 6, 7 ✓
- §5 (ShowWindowActions + reconciliation) → Task 9 ✓
- §6 (toolbar-disabled — no such state exists; documented) → no task needed (Global Constraint) ✓
- §7 (seams table) → every row maps to a task ✓
- §8 (edge cases) → covered by Task 8 smoke matrix + Task 7 safety ✓
- §9 (tests) → distributed across tasks; Task 8 smoke matrix ✓
- §11 (failure-mapping) → no task (it's a rationale section); the "no InteractionManager changes" Global Constraint enforces it ✓

**Placeholder scan:** Task 5 Step 2's test harness note is a real gap — `EventCoordinator`'s test setup needs to be discovered at execution time (the plan says to check `tests/EyeCtrl.App.Tests/Services/` for the established pattern). This is the one place the plan can't pre-render exact code; the implementer must inspect first. Flagged, not hidden.

**Type consistency:** `WindowAction`/`WindowStateOp`/`WindowStateOps.DecideWindowOp`/`ForegroundWindowCandidate`/`ForegroundWindowEligibility.IsEligible`/`WindowActionRequestedMessage`/`SetActive`/`SetWindowActionsVisible`/`TryGetEligibleForegroundWindow` — all defined once, used consistently. `WindowActionIds` in Task 9 matches the three ids in Task 6.
