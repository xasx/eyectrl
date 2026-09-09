namespace EyeCtrl.Platform.Input;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Hook-proc signature for WH_KEYBOARD_LL. StdCall to match the native callback.</summary>
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

/// <summary>
/// <see cref="IGlobalEscapeHatch"/> over WH_KEYBOARD_LL. The hook is installed on a dedicated
/// background thread that owns the message pump (an LL hook is delivered to the installing
/// thread, which must pump), and lives for the process lifetime; the OS removes it when the
/// process exits, so a crash cannot leak it (design H1). The callback body is two comparisons
/// plus, on an ESC match only, one thread-pool dispatch (design H2) — anything slower delays
/// keyboard input system-wide, which the Supervisor's hang thresholds would observe.
///
/// <para>
/// <b>Degradation contract (design H5):</b> ANY start-up failure — a zero hook handle, a
/// throwing interop call, a policy-blocked install — leaves the hatch inactive and the app
/// running. The hook thread catches everything and logs a warning; the gaze-reachable exits
/// (stop button, lost-gaze auto-exit, drag cancel button) remain.
/// </para>
/// </summary>
public sealed class GlobalEscapeHatch : IGlobalEscapeHatch
{
    private const int WhKeyboardLl = 13;
    private const long WmKeyDown = 0x0100;
    private const long WmSysKeyDown = 0x0104;
    private const uint WmQuit = 0x0012;
    private const int VkEscape = 0x1B;

    /// <summary>The native seam, so Platform.Tests can drive the lifecycle without a real hook.</summary>
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
    private readonly ILogger<GlobalEscapeHatch> _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private LowLevelHookProc? _hookProc;   // GC root: the native side holds a raw pointer to this delegate
    private volatile IntPtr _hookHandle;
    private uint _hookThreadId;
    private volatile bool _disposed;

    public event Action? EscapePressed;

    public bool IsActive => _hookHandle != IntPtr.Zero && !_disposed;

    public GlobalEscapeHatch(ILogger<GlobalEscapeHatch>? logger = null)
        : this(LowLevelKeyboardInterop.Instance, logger) { }

    internal GlobalEscapeHatch(ILowLevelKeyboardInterop interop, ILogger<GlobalEscapeHatch>? logger = null)
    {
        _interop = interop;
        _logger = logger ?? NullLogger<GlobalEscapeHatch>.Instance;
        _thread = new Thread(HookThreadProc) { IsBackground = true, Name = "escape-hatch" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    private void HookThreadProc()
    {
        try
        {
            _hookThreadId = _interop.GetCurrentThreadId();
            _hookProc = HookCallback;
            _hookHandle = _interop.SetWindowsHookEx(
                WhKeyboardLl, _hookProc, _interop.GetModuleHandle(IntPtr.Zero), 0);
        }
        catch (Exception ex)
        {
            // Design H5: an interop failure must degrade, never crash — this thread has no
            // other handler, so an escaped exception would kill the process on every launch.
            _hookHandle = IntPtr.Zero;
            _started.Set();
            _logger.LogWarning(ex,
                "Global escape hatch failed to start — ESC will not end drag/repeat; the gaze exits remain available");
            return;
        }

        _started.Set();   // before the pump: the constructor must return while this thread pumps

        if (_hookHandle == IntPtr.Zero)
        {
            _logger.LogWarning(
                "Global escape hatch: SetWindowsHookEx failed (Win32 error {ErrorCode}) — ESC will not end drag/repeat; the gaze exits remain available",
                Marshal.GetLastWin32Error());
            return;
        }

        _logger.LogInformation("Global escape hatch installed: ESC anywhere ends drag or repeat mode");

        try
        {
            while (_interop.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Global escape hatch pump failed; unhooking");
        }
        finally
        {
            try { _interop.UnhookWindowsHookEx(_hookHandle); }
            catch { /* teardown is best-effort; process exit removes the hook anyway */ }
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsEscapeKeyDown(wParam.ToInt64(), Marshal.ReadInt32(lParam)))
        {
            // Escalate off the hook thread (design H2): handlers may do real work; the hook must
            // be back in CallNextHookEx before they start. Each subscriber is isolated so one
            // throwing handler cannot kill the process — or skip its neighbours.
            var handlers = EscapePressed;
            if (handlers is not null)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    foreach (var handler in handlers.GetInvocationList().Cast<Action>())
                    {
                        try { handler(); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "An EscapePressed subscriber threw; continuing with the rest");
                        }
                    }
                });
            }
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

