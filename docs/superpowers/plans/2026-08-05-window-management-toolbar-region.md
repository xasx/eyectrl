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
        // isWindow:true so only the zero check is exercised (not the !IsWindow check).
        => Check.That(Eligible(IntPtr.Zero, isWindow: true, Shell, "Notepad", AppPid)).IsFalse();

    [Fact]
    public void DestroyedHwnd_IsRejected()
        => Check.That(Eligible(App, isWindow: false, Shell, "Notepad", AppPid)).IsFalse();

    [Fact]
    public void ShellDesktopHwnd_IsRejected()
        // className:"Notepad" so only the hwnd==shellHwnd check is exercised (not the class denylist).
        => Check.That(Eligible(Shell, isWindow: true, Shell, "Notepad", AppPid)).IsFalse();

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
    internal static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

- [ ] **Step 3a: Implement `TryGetEligibleForegroundWindow`**

Add to `Win32WindowManager.cs`. Gathers the Win32 facts and delegates to the pure predicate (Task 2). Note: inputs are gathered eagerly, so `GetClassName`/`GetWindowThreadProcessId` run on a possibly-zero/destroyed HWND before the predicate rejects it — harmless (both fail benignly, returning 0/empty) but the fail-closed *order* is enforced by the predicate, not the gathering:

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

        // IsEligible is fail-closed: zero/!IsWindow/shell/class/own-process all reject.
        return ForegroundWindowEligibility.IsEligible(candidate) ? hwnd : null;
    }
```

- [ ] **Step 3b: Implement `Minimize` + `MaximizeOrRestore`**

```csharp
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
```

- [ ] **Step 3c: Implement `CloseWindow`**

```csharp
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

- [ ] **Step 2: Write the failing tests for `HandleWindowAction`**

The harness exists — use it. `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs` already provides `MakeContext(IInputSimulator)` (`:160-190`, `WindowManager:` at `:185` takes a fake), `SetContext(coordinator, ctx)` (`:192-197`, sets `_context` by reflection), and private-method invocation by reflection (`:102-104`, `:228-233`). `HandleWindowAction` is private — invoke it the same way.

Add these tests to `EventCoordinatorTests.cs` (or a new partial in the same namespace). The fake `IWindowManager` records the last op + eligibility query count:

```csharp
    private sealed class FakeWindowActionManager : IWindowManager
    {
        public IntPtr? EligibleHwnd { get; set; }
        public WindowAction? LastOp { get; private set; }
        public int EligibilityQueries { get; private set; }
        public bool ClosePosted { get; private set; } = true;

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => Array.Empty<AppWindow>();
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow()
        {
            EligibilityQueries++;
            return EligibleHwnd;
        }
        public void Minimize(IntPtr hwnd) => LastOp = WindowAction.Minimize;
        public void MaximizeOrRestore(IntPtr hwnd) => LastOp = WindowAction.MaximizeRestore;
        public bool CloseWindow(IntPtr hwnd) { LastOp = WindowAction.Close; return ClosePosted; }
    }

    private static void InvokeHandleAction(EventCoordinator coord, WindowAction action)
    {
        // Mirror the reflection pattern at EventCoordinatorTests.cs:102-104 / :228-233
        var mi = typeof(EventCoordinator).GetMethod("HandleWindowAction",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        mi.Invoke(coord, new object[] { action });
    }

    [Theory]
    [InlineData(WindowAction.Minimize)]
    [InlineData(WindowAction.MaximizeRestore)]
    [InlineData(WindowAction.Close)]
    public void HandleWindowAction_RoutesToTheRightOp(WindowAction action)
    {
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeHandleAction(coord, action);

        Check.That(fake.LastOp).IsEqualTo(action);
        Check.That(fake.EligibilityQueries).IsEqualTo(1);
    }

    [Fact]
    public void HandleWindowAction_IneligibleForeground_NoOpsAndLogs()
    {
        var fake = new FakeWindowActionManager { EligibleHwnd = null };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeHandleAction(coord, WindowAction.Close);

        Check.That(fake.LastOp).IsNull();
    }

    /// <summary>
    /// Pins the spec §4 BLOCKER A.1 fix: the recipient is synchronous (no BeginInvoke), so the
    /// foreground HWND is queried once, at op-fire time, and the op hits exactly that window.
    /// A queued hop would allow the foreground to change between dwell completion and the query.
    /// </summary>
    [Fact]
    public void HandleWindowAction_QueriesForegroundOnce_Synchronously()
    {
        // The fake's EligibilityQueries counter is the proxy for "no deferred re-query":
        // one synchronous call → exactly one query.
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeHandleAction(coord, WindowAction.Minimize);

        Check.That(fake.EligibilityQueries).IsEqualTo(1);
    }

    /// <summary>
    /// Pins the spec §4 BLOCKER A.1 fix end-to-end: drive the REAL messenger path, not the
    /// private method by reflection. If a BeginInvoke is re-introduced in the registration
    /// (Step 3), the handler runs on a queued dispatch that never pumps in a unit test, so
    /// fake.LastOp stays null when Send returns. A synchronous registration fires inline →
    /// LastOp is set before Send returns. This is the discriminating test; the
    /// QueriesForegroundOnce test above only pins query count.
    /// </summary>
    [Fact]
    public void WindowActionRecipient_IsSynchronous_FiresBeforeSendReturns()
    {
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var messenger = new WeakReferenceMessenger();
        var coord = MakeCoordinatorWithMessenger(messenger, fake); // see helper below

        // Send drives the registration. Synchronous → LastOp set now; queued → still null.
        messenger.Send(new WindowActionRequestedMessage(WindowAction.Minimize));

        Check.That(fake.LastOp).IsEqualTo(WindowAction.Minimize);
    }
```

`MakeCoordinatorWithMessenger` constructs an `EventCoordinator` whose `_context` carries the given `messenger` + fake window manager, then invokes the private setup method that contains the `Register<EventCoordinator, WindowActionRequestedMessage>(…)` line (Step 3) by reflection — mirroring how existing tests invoke private methods. **Caveat (round-2 MAJOR):** that setup method also touches `_context.TaskSwitcherVM.PropertyChanged` (`:122`), which is `null!` in `MakeContext` — if invoking it throws NRE, the setup method isn't unit-testable in isolation. In that case, **fall back to the weaker `QueriesForegroundOnce` test above** (which pins query count but not synchrony) and mark §9 in the Self-Review coverage table as *"partially covered — the synchronous registration is spec-pinned + code-reviewed; the BeginInvoke-regression is not unit-guarded because the setup method can't be invoked in isolation"*. Do NOT mark §9 as ✓ without the discriminating test.

If `MakeContext` doesn't already accept a window-manager override, add a small `MakeContextWithWindowManager(IWindowManager)` helper to the test class that calls `MakeContext` then returns a context with `WindowManager:` swapped (mirror how `MakeContext(IInputSimulator)` overrides the simulator at `:160-190`). **Do not extract `HandleWindowAction` to a testable helper** — the spec requires it as a method on `EventCoordinator`; the reflection pattern in this test class is the established way to test private methods here.

- [ ] **Step 3: Register the recipient in `EventCoordinator`**

Near the `TaskSwitcherSelectionMessage` registration (`:135-136`). **Critical: no `BeginInvoke`** — call `HandleWindowAction` directly (the spec's BLOCKER A.1 fix; contrast with the Task Switcher registration right above it which DOES use `BeginInvoke`, harmlessly, because its HWND rides the message):

```csharp
        _context.Messenger.Register<EventCoordinator, WindowActionRequestedMessage>(this, (_, msg) =>
            HandleWindowAction(msg.Action));
```

- [ ] **Step 4: Implement `HandleWindowAction`**

Add as a method on `EventCoordinator` (near `HandleTaskSwitcherSelection`, `:141`). Use Serilog's static `Log` (already imported, `using Serilog;` at `:22`, used at `:478`, `:486`, `:1210`) — **not** an `ILogger<T>` (none is injected here). The spec §2/§3/§9 mandate logging at `Information` on the null + UIPI-blocked paths:

```csharp
    private void HandleWindowAction(WindowAction action)
    {
        if (_context == null) return;

        var hwnd = _context.WindowManager.TryGetEligibleForegroundWindow();
        if (hwnd is null)
        {
            // Foreground was the shell, this process, zero, or an invalid/destroyed window.
            Log.Information("Window-action {Action} skipped: no eligible foreground window", action);
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
                if (!posted)
                    Log.Information("Window-action Close: PostMessage(WM_CLOSE) returned false (UIPI-blocked by elevated foreground?)");
                break;
        }
    }
```

Confirm `_context.WindowManager` exists — it does (`HandleTaskSwitcherSelection` uses `_context.WindowManager.BringToFront`, `:144`). Add `using EyeCtrl.Core.Models;` if `WindowAction` isn't already imported.

- [ ] **Step 5: Build + run the new tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HandleWindowAction"`
Expected: PASS (3 routing + 1 null-eligibility + 1 synchronous-query = 5 tests). Verify the whole `EyeCtrl.App.Tests` project still builds (`dotnet build tests/EyeCtrl.App.Tests`).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs
git commit -m "feat(window-mgmt): WindowActionRequestedMessage + synchronous HandleWindowAction recipient"
```

---

## Task 6: Toolbar buttons + dispatch (View + VM arrays)

Add the three buttons to both arrays (View renders them; VM creates them) and the three dispatch cases in `OnTargetActivated`.

> **Ordering note:** Task 6 does NOT reference `ShowWindowActions` — it adds the three buttons unconditionally. Task 9 (settings toggle + reconciliation) is what gates them. Task 6 compiles standalone; execute it before Task 9.

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` (`:66-76` array, `:131-144` switch)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (`:39-46` array, `:31` signature)
- Test: extend an existing `ToolbarViewModelTests` or add `ToolbarWindowActionDispatchTests`

**Interfaces:**
- Consumes: `WindowAction` (Task 1), `WindowActionRequestedMessage` (Task 5).

- [ ] **Step 1: Write the failing test for dispatch**

Test that dwelling each of the three new ids sends the right `WindowActionRequestedMessage`, AND that the VM array actually contains the three buttons (the dwell test alone passes with just the dispatch switch — `UpdateButtonScreenPosition` (`:41-58`) creates a target for any id; the array assertion pins Step 3). Use the established single-generic `Register<TMessage>(this, …)` form (`ToolbarViewModelTests.cs:153`) — **not** `Register<TRecipient, TMessage>` (CommunityToolkit constrains `TRecipient : class`; a struct violates CS0452):

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
        messenger.Register<WindowActionRequestedMessage>(this, (_, msg) => received = msg.Action);

        // OnTargetActivated is protected; drive it via Receive on a positioned button, mirroring
        // GazeDwellOnPositionedButton_ActivatesIt (ToolbarViewModelTests.cs:144-160).
        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition(id, center);
        var dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(received).IsEqualTo(expected);
    }

    [Fact]
    public void ToolbarViewModel_ContainsWindowActionButtons()
    {
        // Pins Step 3 (VM array) independently of the dispatch switch — UpdateButtonScreenPosition
        // would create a target for any id, so the dwell test alone can't catch a missing array entry.
        var vm = new ToolbarViewModel(_settings, new WeakReferenceMessenger());
        Check.That(vm.Buttons.Select(b => b.Id).ToArray())
             .Contains(new[] { "Minimize", "MaximizeRestore", "Close" });
    }
```

Add `using EyeCtrl.Core.Models;` + `using EyeCtrl.ViewModels.Messages;` + `using EyeCtrl.ViewModels;` to the test file.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OnTargetActivated_WindowButton|FullyQualifiedName~ToolbarViewModel_ContainsWindowAction"`
Expected: FAIL — the dispatch switch (`:131-144`) lacks the three cases (dwell test), and the VM `Buttons` collection lacks the three ids (array test).

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

Two tests. First, the safety case — a hidden toolbar must not dwell. Use the single-generic `Register<TMessage>(this, …)` form (the established pattern at `ToolbarViewModelTests.cs:153`) — **not** `Register<TRecipient, TMessage>` (CS0452; `TRecipient : class`):

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
        messenger.Register<PauseToggledMessage>(this, (_, _) => fired = true);

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
        vm.Receive(new GazeCursorMovedMessage(center, 100_000)); // partial (100ms < 700ms dwell)
        vm.SetActive(false);
        vm.SetActive(true);

        bool fired = false;
        messenger.Register<PauseToggledMessage>(this, (_, _) => fired = true);

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

Add a field + a public `SetActive` method. Initial state is `true` (dwell active). Gate `Receive`. **Telemetry note (spec §9):** `ResetAllState()` emits `TargetProgress(id,0)` but no `Abandoned` `DwellOutcome`, so a dwell in flight at hide/calibration-start vanishes from struggle telemetry — add a one-line comment documenting this intentional bias:

```csharp
    private bool _dwellingActive = true;

    /// <summary>
    /// Activates/deactivates dwell processing for the toolbar. Called by the auto-hide
    /// lifecycle (hide → false, show/ForceShow → true) and calibration (Collecting/Training → false,
    /// Active/Failed/Cancelled → true). When deactivated, gaze samples are not fed to the detector
    /// and any in-progress dwell state is reset (preventing both hidden-toolbar fires and stale-
    /// timestamp instant activation on resume).
    ///
    /// Telemetry bias: ResetAllState fires TargetProgress(id,0) but NOT an Abandoned DwellOutcome,
    /// so a mid-flight dwell at hide/calibration-start drops out of struggle telemetry. Intentional —
    /// both numerator and denominator of ScoreAbandonment drop together; impact negligible.
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

- [ ] **Step 4a: Add `SetActive` to `IToolbarVisibility` + the adapter**

`IToolbarVisibility.cs` — add:
```csharp
    /// <summary>
    /// Activates or deactivates toolbar dwell processing. Hide/ForceShow/calibration call this.
    /// When deactivated, in-progress dwell state is reset (DwellDetector.ResetAllState) so the
    /// next activation cannot fire from a stale timestamp.
    /// </summary>
    void SetActive(bool active);
```

`ToolbarVisibilityAdapter.cs` — add `using EyeCtrl.ViewModels;` (currently only `System.Windows.Threading` + `EyeCtrl.App.Overlays`), inject `ToolbarViewModel`:

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

- [ ] **Step 4b: Wire Show/Hide/ForceShow + update the single ctor site**

In `ToolbarVisibilityAdapter`:
- `ShowToolbar()` body: after `_overlays.Toolbar.Show()`, add `_toolbarVm.SetActive(true);`
- `HideToolbar(bool paused)` body: before `_overlays.Toolbar.Hide()`, add `_toolbarVm.SetActive(false);`
- `ForceShow()` body: after `_overlays.Toolbar.Show()`, add `_toolbarVm.SetActive(true);`

Update the adapter's construction site — `new ToolbarVisibilityAdapter(` has exactly ONE call site, `EventCoordinator.SetupToolbarAutoHide:590`. Pass the VM: `new ToolbarVisibilityAdapter(_context.OverlayManager, _context.Dispatcher, _context.ToolbarVM)`. (No DI registration to update — the adapter is hand-constructed, not in `AppBootstrapper`.)

- [ ] **Step 4c: Update the `FakeVisibility` test fake (compile fix — BLOCKER)**

Adding `SetActive` to `IToolbarVisibility` breaks the private `FakeVisibility` fake at `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs:395` (the parallel of the `FakeWindowManager` fix in Task 4). Add the method:

```csharp
        public List<bool> SetActiveCalls { get; } = new();
        public void SetActive(bool active) => SetActiveCalls.Add(active);
```

- [ ] **Step 5: Wire calibration (the `CalibrationState` mapping is the trap)**

`EventCoordinator` already holds `_toolbarVisibility` (assigned at `:592`, used at `:89` and `:1192`) — **reuse that field**, do NOT add a new one or change `EventCoordinationContext`.

The calibration handler is `CalibrationStateChangedMessage` (`:490-519`) switching on `CalibrationState`. **Read `CalibrationState` (`EyeCtrl.Core/Calibration/CalibrationState.cs`) before writing the arms — the enum names are a trap:** `Active` means *"calibration is complete and the transform is applied"* (success), **not** "in progress." An implementer reading `case CalibrationState.Active:` as "calibration active" would call `SetActive(false)` on *success*, leaving the toolbar permanently un-dwellable on configs where `ShowToolbar()` never fires (floating toolbar / mouse provider — auto-hide disabled) — no recovery short of restart. The correct mapping mirrors how the existing arms set `SuppressFixation`:

- `CalibrationState.Collecting` → `_toolbarVisibility.SetActive(false)` (gaze is being collected at targets, including screen edges/corners where a docked toolbar lives)
- `CalibrationState.Training` → `SetActive(false)` (still in session)
- `CalibrationState.Active` (success/done) → `SetActive(true)`
- `CalibrationState.Failed` → `SetActive(true)`
- `CalibrationState.Cancelled` → `SetActive(true)`
- `CalibrationState.Idle` / `default` → `SetActive(true)`

Add the `SetActive` call inside each existing arm of the calibration switch, alongside the cursor-circle / fixation-suppression logic already there.

> **Calibration/hide refcount invariant (spec §4):** hide and calibration cannot overlap — calibration only starts from controller-state `Visible` (`ToolbarAutoHideController._state`, `:48`), and `Feed` can't transition while `_calibrationActive` (`:151-154`). The flag is a single bool, not a refcount. **Note the asymmetry:** `ToolbarAutoHideController` derives its calibration gate from `_context.CalibrationService.IsSessionActive` (`:588`), while this wiring derives from `CalibrationStateChangedMessage` — two sources for one condition. The non-overlap is an invariant of *the controller's* source, not the message's. Add a one-line code comment at the `SetActive` definition stating this, so a future change that breaks the non-overlap doesn't silently re-enable dwell. If overlap ever becomes possible, switch to a refcount.

- [ ] **Step 6: Build + run tests to verify they pass**

Run: `dotnet build EyeCtrl.slnx -clp:ErrorsOnly` (verify the WHOLE solution builds — `FakeVisibility` is in `EyeCtrl.App.Tests`, so `dotnet test tests/EyeCtrl.ViewModels.Tests` alone won't catch it).
Then: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SetActive_"` + `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarAutoHideControllerTests"`
Expected: build 0 errors; both test runs PASS.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs src/EyeCtrl.App/Services/IToolbarVisibility.cs src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs
git commit -m "feat(window-mgmt): target-lifecycle contract — hide/calibration deactivates toolbar dwell (ResetAllState + activity flag)"
```

---

## Task 8: Manual smoke matrix (no automated test) — MANUAL GATE

> ⚠️ **Manual gate — requires explicit go-ahead from the user; do NOT execute autonomously.** This task launches the app, drives real dwells, and closes real foreground windows on a machine whose only input channel is eye tracking. A mis-dwelled Close hits whatever is foreground. Run the Close rows last. Bound the session (quit the app when done).
>
> **⏭️ SKIP UNTIL AFTER TASK 10 IS COMMITTED.** This task is numbered 8 but executes LAST — run it after the drift tests (Task 10) are green and the settings toggle (Task 9) is in. Executing it in document order (between 7 and 9) runs against an incomplete feature.

The Win32 ops need a real foreground window; they're covered by the Task 4/5 unit fakes + this manual matrix. **Run before considering the feature done, and after Task 10** (so drift tests are green and the settings toggle is present).

**Files:** none (verification only — record results in the PR description)

- [ ] **Step 1: Build the app**

Run: `dotnet build EyeCtrl.slnx -clp:ErrorsOnly`
Expected: 0 errors.

- [ ] **Step 2: Pass A — mouse-gaze provider (op correctness)**

`--test-mode` forces `MouseGazeProvider` (`AppBootstrapper.cs:87-88`) → `ActiveProviderType == Mouse` → `providerIsRealTracker == false` → auto-hide is **disabled** (`enabled = false` at `EventCoordinator.cs:574`), `_autoHideGazeHandler` never subscribes, the toolbar never hides, and the tray "Show toolbar" callback is `null` (`App.xaml.cs:223-229`). So Pass A tests only the op routing — the toolbar stays visible throughout.

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`

Verify the **op-correctness** rows (toolbar is visible; dwell each button):

| Case | Expected |
|------|----------|
| Normal app foreground → Minimize | Window minimizes |
| Normal app foreground → MaximizeRestore (normal) | Window maximizes |
| Maximized foreground → MaximizeRestore | Window restores |
| Desktop focused (click desktop first) → Minimize | **No-op** (shell rejected); nothing happens |
| **Taskbar focused** → Minimize | **No-op** (Shell_TrayWnd rejected) |
| **Start menu / shell frame foreground** → Minimize | **No-op** (shell class rejected) |
| **UWP app foreground** → Minimize | Minimizes (or no-op if suspended/cloaked — document which) |
| **IME (e.g. typing in a CJK IME candidate window) foreground** → Minimize | **No-op** or minimizes the IME's owner — document observed behavior |
| Console window foreground → Close | Console closes (documented: no save prompt) |
| Owned modal dialog foreground → Close | Dialog closes, **owning app stays open** |
| `MB_OK` message box foreground → Close | Warning sound, box stays (documented; `PostMessage` returns true) |
| Elevated app foreground → Close | **No-op** (UIPI-blocked); logged |
| **Fullscreen (F11) app foreground → MaximizeRestore** | Document observed behavior (spec §8 defers the guard — note if it wedges) |
| Normal app foreground → Close | App's save-changes prompt (or closes gracefully) |

- [ ] **Step 3: Pass B — real Tobii tracker (auto-hide + calibration rows)**

The load-bearing Task 7 rows (hidden-toolbar-doesn't-dwell, no-instant-activation-on-resume, calibration-deactivation) **require auto-hide to be active**, which needs a real tracker (`providerIsRealTracker == true`). Run with the Tobii provider (no `--test-mode`), docked toolbar (the default).

| Case | Expected |
|------|----------|
| Summon by looking past the docked edge → toolbar appears | Toolbar slides in (~400ms summon delay) |
| Look away (off-toolbar) > hide delay (~5s) → toolbar hides | Toolbar hides |
| **Toolbar auto-hidden → gaze over former Close position** | **No dwell fires** (Task 7 contract — the safety case) |
| **Mid-dwell on a button, then auto-hide → re-summon → resume gaze** | Progress reset; **no instant activation** on re-show (ResetAllState cleared the timestamp) |
| **Start calibration → gaze over the (visible) toolbar's buttons** | **No dwell fires** during calibration (Task 7 calibration deactivation) |
| **Calibration completes (CalibrationState.Active) → gaze over toolbar** | Dwell re-activated (SetActive(true) on Active/Failed/Cancelled) |

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
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (DwellToggle + Reset copy, `:730-765`)

**Interfaces:**
- Consumes: Tasks 1-7.

- [ ] **Step 1: Add the settings field + snapshot**

`ToolbarSettings.cs`:
```csharp
    /// <summary>Whether the toolbar shows the Minimize/MaximizeRestore/Close region. Default true.</summary>
    [ObservableProperty] private bool _showWindowActions = true;
```

`SettingsSnapshots.cs` — add `ShowWindowActions` to the `ToolbarSnapshot` record (mirror the existing `IsDocked`/`DockedEdge`/`Size` fields). Also add a `ShowWindowActions` case to `SettingsSnapshotsTests.ToolbarSnapshot_OnlyToolbarFieldsMatter` (`:83`) so a future `Capture` omission is caught (the test exists precisely for this class of bug).

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

- [ ] **Step 4: Notify the VM from the live-settings block + rebind new buttons in the View**

Two consumers change on a toggle, and **ordering matters** (round-2 MAJOR):

**(a) VM** — In `EventCoordinator`'s `ToolbarSnapshot`-handling block (`:1187-1203`), call `_context.ToolbarVM.SetWindowActionsVisible(snapshot.ShowWindowActions);` **AFTER** `Initialize` + `SetMode` (so the View has already rebuilt for the new button set). The VM add/remove then runs against the post-rebuild state.

**(b) View — the rebind problem (round-1 MAJOR; round-2 narrowed the fix).** `ToolbarWindow.BindToViewModel()` is called exactly once, from `App.xaml.cs:460`, and `+=`-subscribes `PropertyChanged` per item in `vm.Buttons` *at that moment* (`:227-261`). `SetWindowActionsVisible(true)` after a prior removal creates **new** `ToolbarButtonViewModel` instances that nobody subscribes → the buttons render and dwell but their progress bar never updates (on this input modality that reads as "the button is broken"). Naively re-calling `BindToViewModel()` double-subscribes every other button (no `-=` anywhere).

**Fix: an idempotent per-button bind path, called AFTER `SetWindowActionsVisible`.** Add a `BindButton(ToolbarButtonViewModel btn)` method to `ToolbarWindow` that does the `PropertyChanged += ...` subscription for one button (extract the per-item body from `BindToViewModel`'s loop, `:227-261`) and **tracks already-bound ids in a `HashSet<string> _boundIds`**, no-opping on repeats. Do NOT put the bind call inside `Initialize` — two reasons (round-2): (1) ordering — at `Initialize` time the VM still holds the old button set, so newly-added buttons don't exist yet; (2) `Initialize` runs on every toolbar-snapshot change (size/dock/edge), so binding all current buttons there re-subscribes every already-bound button on each save.

Two viable call sites for `BindButton`, pick one:
- **`Buttons.CollectionChanged`** — the View subscribes once (in `BindToViewModel`) to `vm.Buttons.CollectionChanged`; on `Add`, `BindButton(e.ItemsAdded)`; on `Remove`, unbind. Drives off the VM's own add/remove in `SetWindowActionsVisible`.
- **explicit rebind after the snapshot block** — after Step 4(a)'s `SetWindowActionsVisible` call, the coordinator invokes `Toolbar.BindNewButtons()` (a public method on `ToolbarWindow` that iterates `vm.Buttons` and calls `BindButton` for any id not in `_boundIds`). Idempotent because of the `_boundIds` guard.

Either way the load-bearing requirement: a newly-added button's `DwellProgress` reaches its progress `Rectangle`, and no existing button double-subscribes.

Pick the simpler one at implementation; the load-bearing requirement is that a newly-added button's `DwellProgress` reaches its progress `Rectangle`. Test: toggle off then on, dwell a re-added button, assert the progress bar advanced (or at least that the subscription exists — a unit test can assert `btn.DwellProgress = 0.5` propagates to the View's recorded visual state).

- [ ] **Step 5: Settings UI — `DwellToggle`, not XAML**

`SettingsWindow.xaml.cs:730-765` (`BuildToolbarPages`) has **no XAML and no checkboxes** — it builds controls in code: `DwellToggle` / `DwellComboBox` / `DwellSlider` / `DwellButton`. The correct primitive for a bool toggle is `DwellToggle`, mirroring `"t_dock"` at `:738-741`. Add (wrapped in a lambda — `BuildToolbarPages` builds a `List<List<Action>>` of deferred `Add` calls, mirroring `t_dock` at `:738`; a bare `Add(...)` statement won't compile in that context):

```csharp
    () => Add(new DwellToggle(_viewModel, "t_winactions", "Show Window Actions",
        () => _settings.Toolbar.ShowWindowActions,
        v => { _settings.Toolbar.ShowWindowActions = v; ApplyAndSave(); },
        cat, r, _dwellMs)),
```

(`cat`, `r`, `_dwellMs` are the existing layout locals in `BuildToolbarPages` — mirror the `t_dock` row's argument pattern.) Add `ShowWindowActions` to the Reset-copy path at `:756-758` (which currently copies `IsDocked`/`DockedEdge`/`Size` — add a 4th line).

- [ ] **Step 6: Write tests**

- `ToolbarViewModelTests`: toggle true→false removes all three from `Buttons` + `_buttonLookup`; the removed ids are no longer dwellable (position a removed id, dwell, no activation). Toggle false→true re-adds them.
- A settings test asserting `ShowWindowActions` defaults true.
- **Rebind test (round-2 MINOR — was missing from this list):** toggle off then on, set a re-added button's `DwellProgress = 0.5`, assert it propagates to the View's recorded visual state (the `BindButton` subscription from Step 4(b)). Without `BindButton`, the re-added button renders + dwells but its progress bar never updates — this test catches that.

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
- Modify: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs:13-18` (8 → 11; rename method)
- Modify: `tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs:38-49` + `:51-71` (4 → 8 count; 4 → 8 id list)

- [ ] **Step 1: Update `ToolbarViewModelTests` count + rename**

The test at `:14-18` (method `InitializesWith8Buttons`) asserts 8 logical buttons. After Task 6 the VM has 11 (8 + Minimize/MaximizeRestore/Close). Update the literal to 11 **and rename the method to `InitializesWith11Buttons`** — the name is itself a drift-tripwire (round-2 MINOR); leaving `InitialisesWith8Buttons` asserting 11 defeats the test's purpose.

- [ ] **Step 2: Update `AppLaunchTests` (TWO tests) — fix the pre-existing gap too**

`Toolbar_HasFourButtons` (`:38-49`) and `Toolbar_ButtonsHaveCorrectIds` (`:51-71`, hardcoded 4-id list: Pause, SelectionMode, Keyboard, Settings). **Both are already stale today** — the View renders 5 (`ResetAdaptation` was added) but the tests still assert 4. The id list is **missing `ResetAdaptation`** (the pre-existing gap, `:58-64`). After Task 6 the View renders 8 (5 + 3 window buttons, with `ShowWindowActions=true` default). So:

- **Count:** 4 → 8.
- **Id list:** add `ResetAdaptation` (the pre-existing gap) **plus** the three new ids → 8 entries total. The round-2 reviewer flagged that "add 3 to 4" produces 7 and `Toolbar_ButtonsHaveCorrectIds` loops the expected list without a count check, so `ResetAdaptation` would stay permanently unpinned — fix the gap while you're here.

- [ ] **Step 3: Run the affected tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"` then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AppLaunchTests&Category=UI"` (single `--filter` with `&` — two `--filter` args don't AND; the second overrides the first). The `AppLaunchTests` are UI tests (`Category=UI`, class-level trait at `:9`) and need a display session — run locally, not in CI. Note these tests are **environmental**: they fail headless with "Toolbar_Window did not appear within 15s" on clean `main` too, so a red result here isn't necessarily a regression.

- [ ] **Step 4: Commit**

```bash
git add tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs
git commit -m "test(window-mgmt): update toolbar-count drift tests for +3 window buttons"
```

---

## Self-Review (completed, revised after round-1 plan review)

**Spec coverage:**
- §2 (targeting/eligibility) → Tasks 2, 4 ✓
- §3 (action set, DecideWindowOp, op semantics, no-confirm Close, logging) → Tasks 1, 4, 5 (Serilog `Log.Information`), 7 (Close conditional on 7) ✓
- §4 (placement, dispatch, synchronous recipient, target-lifecycle, auto-hide, calibration, telemetry bias) → Tasks 5, 6, 7 ✓
- §5 (ShowWindowActions + reconciliation, rebind) → Task 9 ✓
- §6 (toolbar-disabled — no such state exists; documented) → no task needed (Global Constraint) ✓
- §7 (seams table) → every row maps to a task ✓
- §8 (edge cases incl. fullscreen/taskbar/Start/UWP/IME/console/dialog/MB_OK/elevated) → Task 8 smoke matrix (Pass A + Pass B) ✓
- §9 (tests incl. the synchronous-recipient regression + telemetry-bias note) → Task 5 (regression test, **caveat**: the `Send`-path test is the strong guard; if the setup method can't be invoked in isolation, fall back to the query-count test and downgrade this entry to "partially covered — design-reviewed, not regression-guarded"), Task 7 (telemetry comment), Task 8 (smoke matrix), distributed unit tests ✓
- §11 (failure-mapping) → no task (rationale section); the "no InteractionManager changes" Global Constraint enforces it ✓

**Placeholder scan:** the round-1 "Task 5 harness discovery" gap is **closed** — `EventCoordinatorTests.cs:160-233` (`MakeContext`/`SetContext`/reflection) is cited by name and the test is pre-rendered. No remaining placeholders; every code step contains actual code.

**Type consistency:** `WindowAction`/`WindowStateOp`/`WindowStateOps.DecideWindowOp`/`ForegroundWindowCandidate`/`ForegroundWindowEligibility.IsEligible`/`WindowActionRequestedMessage`/`SetActive`/`SetWindowActionsVisible`/`TryGetEligibleForegroundWindow`/`BindButton` — all defined once, used consistently. `WindowActionIds` in Task 9 matches the three ids in Task 6.

**Round-1 review fixes applied:** BLOCKERs (Register<TRecipient>→Register<TMessage>(this) in 3 snippets; FakeVisibility added to Task 7; §9 synchronous-recipient regression test added to Task 5); MAJORs (Task 5 pre-rendered via EventCoordinatorTests harness + Serilog Log; Task 7 CalibrationState mapping pinned Collecting/Training→false vs Active/Failed/Cancelled→true + _toolbarVisibility reuse + build-the-whole-solution step + using; Task 8 split into mouse/real-tracker passes + 5 missing smoke rows + manual-gate warning; Task 9 rebind BindButton mechanism + DwellToggle not XAML; SettingsSnapshotsTests case); MINORs/NITs (Task 2 test isolation; Task 3 uint msg; Task 4 step split + eager-gather comment; Task 10 single-filter + environmental note).
