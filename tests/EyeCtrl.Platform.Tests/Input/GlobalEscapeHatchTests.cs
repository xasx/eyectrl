namespace EyeCtrl.Platform.Tests.Input;

using System.Runtime.InteropServices;
using EyeCtrl.Platform.Input;

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

            Check.That(raised.Wait(2000)).IsTrue();
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
