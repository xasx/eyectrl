namespace EyeCtrl.Platform.Tests.Icons;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using EyeCtrl.Platform.Icons;

/// <summary>
/// S3-B's window-icon source against REAL windows created by this test process, both kinds the reader
/// must survive:
///
/// <list type="bullet">
/// <item><b>A responsive window</b> (a pumping thread) with its own icon set through
/// <c>WM_SETICON</c>: this is the case that exposed fixup-01 F1 — the icon comes back in
/// <c>SendMessageTimeout</c>'s <c>lpdwResult</c>, not in its return value, so the source was dead for
/// every LIVE window until the fix.</item>
/// <item><b>A hung window</b> (a thread that never pumps): the bound must hold, and its class icon
/// must still be returned without waiting on it.</item>
/// </list>
///
/// <para>
/// Every window here is created hidden (<c>WS_VISIBLE</c> is never set) on its own thread and is
/// never shown, moved or activated: nothing on the owner's desktop is touched. The pumping windows are
/// closed by their own thread through <c>WM_CLOSE</c> (the hung ones are left to process exit, which
/// is the only safe choice — <c>DestroyWindow</c> must run on the owning thread, which is asleep).
/// </para>
/// </summary>
public class WindowMessageIconReaderTests
{
    /// <summary>
    /// The bound plus generous scheduling slack. A loaded CI runner may overshoot the 100 ms timeout,
    /// but nothing that waits without a bound can come near this; an unbounded wait would hang the
    /// test and be caught by <c>--blame-hang-timeout</c>.
    /// </summary>
    private const long MaxObservedMs = 2_000;

    [Fact]
    public void AResponsiveWindowWithItsOwnIcon_YieldsThatIcon()
    {
        // The F1 case: the window ANSWERS WM_GETICON with an icon handle. Reading the call's return
        // value (1) instead of lpdwResult made the copy throw, and the class fallback was skipped, so
        // this returned null for every live window.
        using var window = ProbeWindow.Start(pumping: true, messageIcon: true, classIcon: false);

        var watch = Stopwatch.StartNew();
        var icon = new WindowMessageIconReader().TryReadIcon(window.Handle);
        watch.Stop();

        Check.WithCustomMessage(
                "A responsive window that answers WM_GETICON with its icon produced no bitmap: the icon "
                + "handle lives in SendMessageTimeout's lpdwResult, not in its return value.")
            .That(icon).IsNotNull();
        // InteropBitmap (what the HICON conversion returns) is a BitmapSource subclass.
        var bitmap = icon as BitmapSource;
        Check.That(bitmap).IsNotNull();
        Check.That(bitmap!.PixelWidth).IsStrictlyGreaterThan(0);
        Check.That(bitmap.PixelHeight).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage($"The responsive read took {watch.ElapsedMilliseconds} ms.")
            .That(watch.ElapsedMilliseconds).IsLessOrEqualThan(MaxObservedMs);
    }

    [Fact]
    public void AResponsiveWindowWithoutAMessageIcon_FallsBackToItsClassIcon()
    {
        // The window answers WM_GETICON with 0 — exactly what live ApplicationFrameWindows do. The
        // class icon (independent of the message icon) must still be read and copied.
        using var window = ProbeWindow.Start(pumping: true, messageIcon: false, classIcon: true);

        var icon = new WindowMessageIconReader().TryReadIcon(window.Handle);

        var bitmap = icon as BitmapSource;
        Check.That(bitmap).IsNotNull();
        Check.That(bitmap!.PixelWidth).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void AHungWindow_IsAbandonedWithinTheBound_AndTheSourceDeclines()
    {
        using var hang = ProbeWindow.Start(pumping: false);

        var watch = Stopwatch.StartNew();
        var icon = new WindowMessageIconReader().TryReadIcon(hang.Handle);
        watch.Stop();

        Check.That(icon).IsNull();
        Check.WithCustomMessage(
                $"The window-icon read took {watch.ElapsedMilliseconds} ms on a hung window: without "
                + "SendMessageTimeout's bound the UI/gaze thread waits for the app to answer.")
            .That(watch.ElapsedMilliseconds).IsLessOrEqualThan(MaxObservedMs);
    }

    [Fact]
    public void AHungWindowWithAClassIcon_StillYieldsItsIcon_AfterTheBoundedWait()
    {
        // The fallback chain: WM_GETICON times out (the window never answers), then the class icon is
        // read WITHOUT waiting on the window — so a hung app still shows its icon instead of a hole.
        using var hang = ProbeWindow.Start(pumping: false, classIcon: true);

        var watch = Stopwatch.StartNew();
        var icon = new WindowMessageIconReader().TryReadIcon(hang.Handle);
        watch.Stop();

        Check.That(icon).IsNotNull();
        var bitmap = icon as BitmapSource;
        Check.That(bitmap).IsNotNull();
        Check.That(bitmap!.PixelWidth).IsStrictlyGreaterThan(0);
        Check.That(bitmap.PixelHeight).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage($"The class-icon path took {watch.ElapsedMilliseconds} ms.")
            .That(watch.ElapsedMilliseconds).IsLessOrEqualThan(MaxObservedMs);
    }

    [Fact]
    public void NoWindowHandle_DeclinesWithoutCallingTheOs()
    {
        Check.That(new WindowMessageIconReader().TryReadIcon(IntPtr.Zero)).IsNull();
    }

    [Fact]
    public void TheTimeout_IsShort_AndTheCallIsTheAbortIfHungVariant()
    {
        // The number itself is a design decision (100 ms x the windows that reach this source); the
        // flags are the other half — SMTO_ABORTIFHUNG returns at once for an already-hung window.
        Check.That(WindowMessageIconReader.TimeoutMs).IsStrictlyLessThan(250u);
        Check.That(IconNativeMethods.SMTO_ABORTIFHUNG).IsEqualTo(0x0002u);
        Check.That(IconNativeMethods.SMTO_BLOCK).IsEqualTo(0x0001u);
    }

    /// <summary>
    /// A hidden window of its own class on its own thread. <paramref name="pumping"/> decides whether
    /// the thread processes messages (a live window) or sleeps forever (a hung window);
    /// <paramref name="messageIcon"/> sets the window's big icon through <c>WM_SETICON</c>, and
    /// <paramref name="classIcon"/> registers one on the class.
    /// </summary>
    private sealed class ProbeWindow : IDisposable
    {
        private const int IdiApplication = 32512;
        private const uint WmSetIcon = 0x0080;
        private const uint WmClose = 0x0010;
        private const uint WmQuit = 0x0012;
        private const int IconBig = 1;

        // Rooted for the window's lifetime: a collected delegate would leave the class's WndProc
        // dangling.
        private static readonly WndProc WindowProc = DefWindowProcW;

        private readonly string _className = "EyeCtrlIconProbe_" + Guid.NewGuid().ToString("N");
        private readonly bool _pumping;
        private readonly bool _messageIcon;
        private readonly bool _classIcon;
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private IntPtr _hwnd;
        private uint _threadId;

        private ProbeWindow(bool pumping, bool messageIcon, bool classIcon)
        {
            _pumping = pumping;
            _messageIcon = messageIcon;
            _classIcon = classIcon;
            _thread = new Thread(Run) { IsBackground = true, Name = "icon-probe-window" };
        }

        internal IntPtr Handle => _hwnd;

        internal static ProbeWindow Start(bool pumping, bool messageIcon = false, bool classIcon = false)
        {
            var window = new ProbeWindow(pumping, messageIcon, classIcon);
            window._thread.Start();

            if (!window._ready.Wait(TestTimeouts.PositiveWaitMs) || window._hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the hidden probe window.");

            return window;
        }

        private void Run()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                var wndProc = Marshal.GetFunctionPointerForDelegate(WindowProc);
                var instance = GetModuleHandleW(null);
                var wc = new WndClassEx
                {
                    Size = (uint)Marshal.SizeOf<WndClassEx>(),
                    WndProc = wndProc,
                    Instance = instance,
                    ClassName = _className,
                    Icon = _classIcon ? LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication)) : IntPtr.Zero,
                };

                if (RegisterClassExW(ref wc) == 0)
                {
                    _ready.Set();
                    return;
                }

                _hwnd = CreateWindowExW(0, _className, "", 0, 0, 0, 100, 100,
                    IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (_hwnd != IntPtr.Zero && _messageIcon)
                {
                    _ = SendMessageW(_hwnd, WmSetIcon, new IntPtr(IconBig),
                        LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication)));
                }

                _ready.Set();

                if (!_pumping)
                {
                    // The point of the hung case: this thread never calls GetMessage/DispatchMessage.
                    Thread.Sleep(Timeout.Infinite);
                    return;
                }

                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    _ = TranslateMessage(ref msg);
                    _ = DispatchMessageW(ref msg);
                }
            }
            catch
            {
                _ready.Set();
            }
        }

        public void Dispose()
        {
            // A hung thread cannot be asked to destroy its window (DestroyWindow must run on the
            // owning thread, and that thread never wakes); the OS reclaims it at process exit. A
            // pumping one closes itself: WM_CLOSE reaches DefWindowProc → DestroyWindow, WM_QUIT ends
            // the loop.
            if (!_pumping || _hwnd == IntPtr.Zero)
                return;

            _ = PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            _ = PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _ = _thread.Join(TestTimeouts.PositiveWaitMs);
        }

        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public IntPtr WndProc;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
            public IntPtr SmallIcon;
        }

        /// <summary>Win32 MSG (48 bytes on x64).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PointX;
            public int PointY;
        }

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WndClassEx wndClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIconW(IntPtr instance, IntPtr icon);

        [DllImport("user32.dll", EntryPoint = "GetMessageW")]
        private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern int TranslateMessage(ref Msg msg);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        private static extern IntPtr DispatchMessageW(ref Msg msg);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
        private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? moduleName);
    }
}
