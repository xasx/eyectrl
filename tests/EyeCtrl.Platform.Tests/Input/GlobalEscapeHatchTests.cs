namespace EyeCtrl.Platform.Tests.Input;

using System.Runtime.InteropServices;
using EyeCtrl.Platform.Input;
using static EyeCtrl.Platform.Tests.TestTimeouts;

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
    [InlineData(0x0101, 0x1B, false)]  // WM_KEYUP — a release must not trigger the hatch
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
        int? handlerThreadId = null;
        hatch.EscapePressed += () => { handlerThreadId = Environment.CurrentManagedThreadId; raised.Set(); };

        var lParam = AllocLParam(0x1B);
        try
        {
            interop.CapturedProc!(0, (IntPtr)0x0100, lParam);

            Check.That(raised.Wait(PositiveWaitMs)).IsTrue();
            Check.That(interop.ForwardedToNextHook).IsTrue(); // the hook ALWAYS calls CallNextHookEx
            // Escalated off the hook thread (design H2): neither the test thread nor (trivially) the
            // pump thread — the pool dispatch is what makes the callback safe to return immediately.
            Check.That(handlerThreadId).IsNotNull();
            Check.That(handlerThreadId!.Value).IsNotEqualTo(Environment.CurrentManagedThreadId);
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
    public void Dispose_Unhooks_AndIsIdempotent()
    {
        var interop = new FakeLowLevelKeyboardInterop();
        var hatch = new GlobalEscapeHatch(interop);
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

    [Fact]
    public void ThrowingInterop_DegradesToInactive_WithoutCrashing()
    {
        // Review PR #142: the first native call threw EntryPointNotFoundException on the hook
        // thread — an unhandled background-thread exception is a process kill, on every launch.
        // ANY start-up failure must degrade to IsActive=false instead (design H5).
        using var hatch = new GlobalEscapeHatch(new ThrowingInterop());
        Check.That(hatch.IsActive).IsFalse();
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotKillTheDispatch_OthersStillRun()
    {
        var interop = new FakeLowLevelKeyboardInterop();
        using var hatch = new GlobalEscapeHatch(interop);

        var healthy = new ManualResetEventSlim(false);
        hatch.EscapePressed += () => throw new InvalidOperationException("subscriber boom");
        hatch.EscapePressed += () => healthy.Set();

        var lParam = AllocLParam(0x1B);
        try { interop.CapturedProc!(0, (IntPtr)0x0100, lParam); }
        finally { Marshal.FreeHGlobal(lParam); }

        // The throwing subscriber must neither kill the process nor skip its neighbours.
        Check.That(healthy.Wait(PositiveWaitMs)).IsTrue();
    }

    [Fact]
    public void RealHook_InstallsAndDisposes_WhenSmokeEnabled()
    {
        // Opt-in only (set EYECTRL_HATCH_SMOKE=1): installs a REAL system-wide keyboard hook,
        // which CI and parallel test runs must not do. This is the automated counterpart of the
        // reviewer's probe: it exists to prove the interop declarations actually resolve and
        // the lifecycle runs — the failure it replaced was a launch crash.
        if (Environment.GetEnvironmentVariable("EYECTRL_HATCH_SMOKE") != "1") return;

        using var hatch = new GlobalEscapeHatch();
        Check.That(hatch.IsActive).IsTrue();
        hatch.Dispose();
        Check.That(hatch.IsActive).IsFalse();
    }

    [Fact]
    public void ReArmMessage_InstallsFreshHook_BeforeUnhookingTheStaleOne()
    {
        // Windows silently removes an LL hook whose callback exceeded LowLevelHooksTimeout —
        // no notification, the stale handle keeps IsActive true, ESC dead system-wide. The
        // pump's re-arm path heals it. The safety property under test is ORDER, observed
        // through one journal: the fresh hook's install must precede the stale handle's
        // unhook, so ESC coverage never gaps. (An earlier version of this test asserted only
        // presence — it stayed green with the order inverted, found by review mutation.)
        var interop = new ReArmingFakeInterop();
        using var hatch = new GlobalEscapeHatch(interop);

        // The pump consumes the scripted WmReArm while the hatch is still live; wait for BOTH
        // halves of the swap so the journal is quiescent when asserted.
        Check.That(interop.SecondInstall.Wait(PositiveWaitMs)).IsTrue();
        Check.That(interop.StaleUnhooked.Wait(PositiveWaitMs)).IsTrue();
        Check.That(hatch.IsActive).IsTrue();                                 // fresh handle active
        Check.That(interop.Journal).ContainsExactly(
            "install",          // initial
            "install",          // re-arm: fresh FIRST...
            "unhook:1");        // ...stale dropped only after

        hatch.Dispose();
        Check.That(interop.Journal).ContainsExactly(
            "install", "install", "unhook:1", "unhook:2");                   // final unhook at dispose
    }

    private sealed class ReArmingFakeInterop : GlobalEscapeHatch.ILowLevelKeyboardInterop
    {
        private readonly Queue<uint> _messages = new(new uint[] { GlobalEscapeHatch.WmReArm });
        private readonly ManualResetEventSlim _quit = new(false);

        public ManualResetEventSlim SecondInstall { get; } = new(false);
        public ManualResetEventSlim StaleUnhooked { get; } = new(false);
        public List<string> Journal { get; } = new();

        public IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId)
        {
            Journal.Add("install");
            if (Journal.Count(c => c == "install") == 2)
                SecondInstall.Set();
            return new(Journal.Count(c => c == "install"));
        }

        public int CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam) => 0;

        public bool UnhookWindowsHookEx(IntPtr hhk)
        {
            Journal.Add($"unhook:{hhk}");
            if (hhk.ToInt64() == 1) StaleUnhooked.Set();
            return true;
        }

        public uint GetCurrentThreadId() => 7u;

        public bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == 0x0012) _quit.Set(); // WM_QUIT
            return true;
        }

        // Scripts the re-arm message, then blocks like the real pump until WM_QUIT.
        public int GetMessage(out NativeMethods.MSG msg, IntPtr hWnd, uint min, uint max)
        {
            msg = default;
            if (_messages.Count > 0)
            {
                msg.Message = _messages.Dequeue();
                return 1;
            }
            _quit.Wait(TimeSpan.FromSeconds(5));
            return 0;
        }

        public IntPtr GetModuleHandle(IntPtr name) => new(2);
    }

    private sealed class ThrowingInterop : GlobalEscapeHatch.ILowLevelKeyboardInterop
    {
        public IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId)
            => throw new EntryPointNotFoundException("simulated bad declaration");
        public int CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam) => 0;
        public bool UnhookWindowsHookEx(IntPtr hhk) => true;
        public uint GetCurrentThreadId() => throw new EntryPointNotFoundException("simulated bad declaration");
        public bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam) => true;
        public int GetMessage(out NativeMethods.MSG msg, IntPtr hWnd, uint min, uint max)
        { msg = default; return 0; }
        public IntPtr GetModuleHandle(IntPtr name) => new(2);
    }

    private sealed class FakeLowLevelKeyboardInterop : GlobalEscapeHatch.ILowLevelKeyboardInterop
    {
        private readonly ManualResetEventSlim _quit = new(false);

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

        public bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == 0x0012) _quit.Set(); // WM_QUIT
            Calls.Add($"quit:{msg}");
            return true;
        }

        // Blocks like the real pump until WM_QUIT is posted, then reports it.
        public int GetMessage(out NativeMethods.MSG msg, IntPtr hWnd, uint min, uint max)
        {
            msg = default;
            _quit.Wait(TimeSpan.FromSeconds(5));
            return 0;
        }

        public IntPtr GetModuleHandle(IntPtr name) => new(2);
    }
}
