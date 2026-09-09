# Global Escape Hatch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Issue #141 — a real global ESC hatch (`WH_KEYBOARD_LL`) so a caregiver can end repeat mode / cancel a drag while a third-party app holds the foreground.

**Architecture:** `GlobalEscapeHatch` in EyeCtrl.Platform installs the LL hook on a dedicated pumping thread at startup; ESC matches escalate off the hook thread to a `EscapePressed` event; both coordinators subscribe (state-guarded) and send their existing cancel messages. The old `ComponentDispatcher` filters are removed. Spec: `docs/superpowers/specs/2026-09-08-global-escape-hatch-design.md` (ledger H1–H10).

**Tech Stack:** .NET 10, `[LibraryImport]` source-gen P/Invoke into `NativeMethods`, xUnit v3 + NFluent, no mocking libs (hand fakes).

## Global Constraints

- Worktree `.worktrees/global-keyboard-hatch`, branch `feat/global-keyboard-hatch` (stacked on `feat/repeat-click-mode`, PR #139 — do NOT rebase).
- Callback discipline (H2): the hook proc does two comparisons + one pool dispatch max, then `CallNextHookEx`. No logging, no locks, no allocations beyond the rare dispatch.
- CI never installs a real hook: Platform tests use the fake interop only; App tests use `FakeEscapeHatch`.
- NFluent assertions; file-scoped namespaces; Allman braces; `_camelCase` fields.
- One test project per invocation.

---

### Task 1: Platform — GlobalEscapeHatch + native interop

**Files:**
- Create: `src/EyeCtrl.Platform/Input/IGlobalEscapeHatch.cs`
- Create: `src/EyeCtrl.Platform/Input/GlobalEscapeHatch.cs`
- Modify: `src/EyeCtrl.Platform/Input/NativeMethods.cs`
- Test: `tests/EyeCtrl.Platform.Tests/Input/GlobalEscapeHatchTests.cs` (new)

**Interfaces (produced):**
- `EyeCtrl.Platform.Input.IGlobalEscapeHatch { event Action? EscapePressed; bool IsActive { get; } } : IDisposable`
- `GlobalEscapeHatch` (public sealed, implements it); `internal delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);`; `internal interface ILowLevelKeyboardInterop`; `internal static bool IsEscapeKeyDown(long wParam, int vkCode)`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.Platform.Tests.Input;

using System.Runtime.InteropServices;

public class GlobalEscapeHatchTests
{
    private static IntPtr AllocLParam(int vkCode)
    {
        var p = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(p, vkCode);
        return p;
    }

    [Theory]
    [InlineData(0x0100, 0x1B, true)]   // WM_KEYDOWN + ESC
    [InlineData(0x0104, 0x1B, true)]   // WM_SYSKEYDOWN + ESC
    [InlineData(0x0101, 0x1B, false)]  // WM_KEYUP
    [InlineData(0x0100, 0x41, false)]  // WM_KEYDOWN + 'A'
    [InlineData(0x0000, 0x1B, false)]  // anything else
    public void IsEscapeKeyDown_MatchesOnlyEscKeyDown(long wParam, int vkCode, bool expected)
    {
        Check.That(GlobalEscapeHatch.IsEscapeKeyDown(wParam, vkCode)).IsEqualTo(expected);
    }

    [Fact]
    public void EscKeyDown_RaisesEscapePressed_OffTheHookThread()
    {
        var interop = new FakeLowLevelKeyboardInterop();
        using var hatch = new GlobalEscapeHatch(interop);
        Check.That(hatch.IsActive).IsTrue();

        var raised = new ManualResetEventSlim(false);
        int? threadId = null;
        hatch.EscapePressed += () => { threadId = Environment.CurrentManagedThreadId; raised.Set(); };

        var lParam = AllocLParam(0x1B);
        try
        {
            var result = interop.CapturedProc!(0, (IntPtr)0x0100, lParam);

            Check.That(raised.Wait(2000)).IsTrue();          // escalated off the hook thread
            Check.That(interop.ForwardedToNextHook).IsTrue(); // callback ALWAYS calls CallNextHookEx
            Check.That(threadId).IsNotNull();
            Check.That(threadId!.Value).IsNotEqualTo(Environment.CurrentManagedThreadId);
        }
        finally { Marshal.FreeHGlobal(lParam); }
    }

    [Fact]
    public void NonEscKey_DoesNotRaise()
    {
        var interop = new FakeLowLevelKeyboardInterop();
        using var hatch = new GlobalEscapeHatch(interop);
        var raised = new ManualResetEventSlim(false);
        hatch.EscapePressed += () => raised.Set();

        var lParam = AllocLParam(0x41);
        try { interop.CapturedProc!(0, (IntPtr)0x0100, lParam); }
        finally { Marshal.FreeHGlobal(lParam); }

        Check.That(raised.Wait(200)).IsFalse();
    }

    [Fact]
    public void Dispose_Unhooks()
    {
        var interop = new FakeLowLevelKeyboardInterop();
        var hatch = new GlobalEscapeHatch(interop);
        // The fake's GetMessage returns 0 (WM_QUIT) immediately, so the pump thread exits on
        // its own; Dispose must still have observed an Unhook call and be idempotent.
        hatch.Dispose();
        hatch.Dispose();
        Check.That(interop.Calls).Contains("unhook");
    }

    [Fact]
    public void FailedInstall_Inactive_WithoutThrowing()
    {
        var interop = new FakeLowLevelKeyboardInterop { HookHandle = IntPtr.Zero };
        using var hatch = new GlobalEscapeHatch(interop);
        Check.That(hatch.IsActive).IsFalse();
    }

    private sealed class FakeLowLevelKeyboardInterop : GlobalEscapeHatch.ILowLevelKeyboardInterop
    {
        public LowLevelHookProc? CapturedProc { get; private set; }
        public bool ForwardedToNextHook { get; private set; }
        public List<string> Calls { get; } = new();
        public IntPtr HookHandle { get; set; } = new(1);

        public IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId)
        {
            Calls.Add($"set:{idHook}");
            CapturedProc = lpfn;
            return HookHandle;
        }

        public int CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam)
        {
            ForwardedToNextHook = true;
            return 0;
        }

        public bool UnhookWindowsHookEx(IntPtr hhk) { Calls.Add("unhook"); return true; }
        public uint GetCurrentThreadId() => 42u;
        public bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam) { Calls.Add($"quit:{msg}"); return true; }
        public int GetMessage(out NativeMethods.MSG msg, IntPtr hWnd, uint min, uint max)
        { msg = default; return 0; } // WM_QUIT immediately → pump exits
        public IntPtr GetModuleHandle(IntPtr name) => new(2);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~GlobalEscapeHatch"` → compile errors.

- [ ] **Step 3: Implement**

`IGlobalEscapeHatch.cs`:

```csharp
namespace EyeCtrl.Platform.Input;

/// <summary>
/// Caregiver escape hatch (issue #141): a global low-level keyboard listener that observes ESC
/// pressed ANYWHERE in the system — including while a third-party app holds the foreground,
/// which is exactly when a gaze-driven mode (repeat, drag) is running and the old
/// ComponentDispatcher filters could never fire.
/// </summary>
public interface IGlobalEscapeHatch : IDisposable
{
    /// <summary>
    /// Raised when ESC is pressed system-wide. Delivered on a THREAD-POOL thread (never the
    /// hook thread — the hook must return immediately); handlers must marshal to the UI thread
    /// themselves and must not block.
    /// </summary>
    event Action? EscapePressed;

    /// <summary>True while the low-level hook is installed and its pump thread is running.</summary>
    bool IsActive { get; }
}
```

`GlobalEscapeHatch.cs`:

```csharp
namespace EyeCtrl.Platform.Input;

using System.Runtime.InteropServices;

/// <summary>Hook-proc delegate for WH_KEYBOARD_LL. StdCall to match the native callback.</summary>
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

/// <summary>
/// <see cref="IGlobalEscapeHatch"/> over WH_KEYBOARD_LL. The hook is installed on a dedicated
/// background thread that owns the message pump (an LL hook is delivered to the installing
/// thread, which must pump), and lives for the process lifetime; the OS removes it when the
/// process exits, so a crash cannot leak it. The callback body is two comparisons plus, on an
/// ESC match only, one thread-pool dispatch (design H2) — anything slower delays keyboard
/// input system-wide, which the Supervisor's hang thresholds would observe.
/// </summary>
public sealed class GlobalEscapeHatch : IGlobalEscapeHatch
{
    private const int WhKeyboardLl = 13;
    private const long WmKeyDown = 0x0100;
    private const long WmSysKeyDown = 0x0104;
    private const uint WmQuit = 0x0012;
    private const int VkEscape = 0x1B;

    /// <summary>The native seam. Internal so Platform.Tests can drive the lifecycle without a real hook.</summary>
    internal interface ILowLevelKeyboardInterop
    {
        IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);
        int CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        bool UnhookWindowsHookEx(IntPtr hhk);
        uint GetCurrentThreadId();
        bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        int GetMessage(out NativeMethods.MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        IntPtr GetModuleHandle(IntPtr lpModuleName);
    }

    /// <summary>Pure decision core: is this hook invocation an ESC key-down?</summary>
    internal static bool IsEscapeKeyDown(long wParam, int vkCode) =>
        (wParam == WmKeyDown || wParam == WmSysKeyDown) && vkCode == VkEscape;

    private readonly ILowLevelKeyboardInterop _interop;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private LowLevelHookProc? _hookProc;   // GC root: native holds a raw pointer to this delegate
    private volatile IntPtr _hookHandle;
    private uint _hookThreadId;
    private volatile bool _disposed;

    public event Action? EscapePressed;

    public bool IsActive => _hookHandle != IntPtr.Zero && !_disposed;

    public GlobalEscapeHatch() : this(LowLevelKeyboardInterop.Instance) { }

    internal GlobalEscapeHatch(ILowLevelKeyboardInterop interop)
    {
        _interop = interop;
        _thread = new Thread(HookThreadProc) { IsBackground = true, Name = "escape-hatch" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    private void HookThreadProc()
    {
        _hookThreadId = _interop.GetCurrentThreadId();
        _hookProc = HookCallback;
        _hookHandle = _interop.SetWindowsHookEx(
            WhKeyboardLl, _hookProc, _interop.GetModuleHandle(IntPtr.Zero), 0);
        _started.Set();

        if (_hookHandle == IntPtr.Zero) return;   // install failed (H5): inactive, app unaffected

        while (_interop.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        _interop.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsEscapeKeyDown(wParam.ToInt64(), Marshal.ReadInt32(lParam)))
        {
            // Escalate off the hook thread (H2): the event handlers may do real work; the hook
            // must be back in CallNextHookEx before they start.
            var handlers = EscapePressed;
            if (handlers is not null)
                ThreadPool.QueueUserWorkItem(_ => handlers.Invoke());
        }
        return _interop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookThreadId != 0)
            _interop.PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    private sealed class LowLevelKeyboardInterop : ILowLevelKeyboardInterop
    {
        internal static readonly LowLevelKeyboardInterop Instance = new();
        private LowLevelKeyboardInterop() { }

        public IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId)
            => NativeMethods.SetWindowsHookEx(idHook, lpfn, hMod, dwThreadId);
        public int CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam)
            => NativeMethods.CallNextHookEx(hhk, nCode, wParam, lParam);
        public bool UnhookWindowsHookEx(IntPtr hhk) => NativeMethods.UnhookWindowsHookEx(hhk);
        public uint GetCurrentThreadId() => NativeMethods.GetCurrentThreadId();
        public bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam)
            => NativeMethods.PostThreadMessage(threadId, msg, wParam, lParam);
        public int GetMessage(out NativeMethods.MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax)
            => NativeMethods.GetMessage(out msg, hWnd, wMsgFilterMin, wMsgFilterMax);
        public IntPtr GetModuleHandle(IntPtr lpModuleName) => NativeMethods.GetModuleHandle(lpModuleName);
    }
}
```

`NativeMethods.cs` additions (respect the LibraryImport entry-point rule from the file header):

```csharp
    // ── Global escape hatch (issue #141): WH_KEYBOARD_LL ──

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    internal static partial int CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    // BOOL: >0 = message, 0 = WM_QUIT, -1 = error.
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    internal static partial uint GetCurrentThreadId();

    // lpModuleName = IntPtr.Zero resolves to the current executable's module handle.
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    internal static partial IntPtr GetModuleHandle(IntPtr lpModuleName);
```

- [ ] **Step 4: Run to verify pass** — `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~GlobalEscapeHatch"`, then the whole project `--filter "Category!=UI"`.

- [ ] **Step 5: Commit** — `feat(platform): GlobalEscapeHatch - WH_KEYBOARD_LL on a dedicated pump thread (issue #141)`

---

### Task 2: App wiring — context, DI, coordinators

**Files:**
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs` (context record + using)
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs` (context argument)
- Modify: `src/EyeCtrl.App/Services/Coordinators/DragCoordinator.cs` (replace filter)
- Modify: `src/EyeCtrl.App/Services/Coordinators/RepeatModeController.cs` (replace filter)
- Test: `tests/EyeCtrl.App.Tests/Services/TestFakes.cs` (+ FakeEscapeHatch)

- [ ] **Step 1: TestFakes fake first (fails to compile until the context param exists)**

```csharp
/// <summary>No-op hatch: App.Tests never install a real keyboard hook.</summary>
internal sealed class FakeEscapeHatch : IGlobalEscapeHatch
{
    public event Action? EscapePressed;
    public bool IsActive => true;
    public void Dispose() { }
    public void Press() => EscapePressed?.Invoke();
}
```
(plus `EscapeHatch: new FakeEscapeHatch(),` in `MakeContext` and `using EyeCtrl.Platform.Input;`)

- [ ] **Step 2: Implement the seam changes**

- `IEventCoordinator.cs`: add `using EyeCtrl.Platform.Input;` and the record parameter after
  `IInstalledAppProvider InstalledAppProvider`: `IGlobalEscapeHatch EscapeHatch`.
- `AppBootstrapper.cs` beside `IInputSimulator`: `services.AddSingleton<IGlobalEscapeHatch, GlobalEscapeHatch>();`
- `App.xaml.cs` context construction: append `_serviceProvider.GetRequiredService<IGlobalEscapeHatch>()`.

- [ ] **Step 3: Swap the coordinators' ESC path (H4/H9)**

`DragCoordinator`: delete the `ComponentDispatcher.ThreadFilterMessage` subscription, the
`_disposeEscapeHook` field + its Dispose line, and `OnThreadFilterMessage`; replace with:

```csharp
        context.EscapeHatch.EscapePressed += OnEscapePressed;   // in OnInitialize
```

```csharp
    /// <summary>
    /// Pool thread (global hatch, issue #141). Guarded: OnDragCancel sends an unconditional
    /// MouseUp, and a stray one could release a caregiver's physically held button — ESC only
    /// cancels while a drag is actually in flight.
    /// </summary>
    private void OnEscapePressed()
    {
        if (Context?.InteractionManager.CurrentState == InteractionState.Dragging)
            Context.Messenger.Send(new DragCancelMessage());
    }
```
(unsubscribe in OnDispose; drop `using System.Windows.Interop;` if nothing else in the file uses it)

`RepeatModeController`: same swap — subscription in OnInitialize, state-guarded handler sending
`ExitRepeatModeMessage` (its registration already BeginInvokes into the state-guarded
`ExitRepeatMode`), unsubscribe in OnDispose, remove `OnThreadFilterMessage` + the
`System.Windows.Interop` filter lines. Update the class doc's ESC paragraph: the hatch is now
global (issue #141), `ComponentDispatcher` limitation gone.

- [ ] **Step 4: Run App tests** — `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` (TestFakes drives every context construction).

- [ ] **Step 5: Commit** — `feat(app): wire the global escape hatch into drag + repeat; drop the ComponentDispatcher filters`

---

### Task 3: Docs + full verification + PR

- [ ] **Step 1:** Issue #141 acceptance criteria that code can cover → checked by Task 1 tests; note the manual smoke items. Update `docs/COMPETITIVE_FEATURE_ANALYSIS.md` change-log only if the owner asks (the hatch is an internal fix, not a gap row).
- [ ] **Step 2:** Full bar, one project per invocation: build slnx; Core / ViewModels / Platform / Controls / Supervisor / Architecture / EyeCare / LM / Sdk×3 / App, all `--filter "Category!=UI"`.
- [ ] **Step 3:** `detect_changes` (compare vs `feat/repeat-click-mode`) — explain or fix findings.
- [ ] **Step 4:** Push + open PR: base `feat/repeat-click-mode` (retarget to `main` after #139 merges — same stacked-PR dance as last time; if #139 has already merged, rebase onto `main` first and base the PR there). Body: issue link, ledger pointer, callback-discipline note, test evidence, manual-smoke checklist for the owner. Comment on #141 with the PR link.
