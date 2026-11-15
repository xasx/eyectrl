namespace EyeCtrl.Platform.Tests.Icons;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using EyeCtrl.Platform.Icons;

/// <summary>
/// S3-B's cross-process bound, proven against a REAL hung window: a window whose owning thread never
/// pumps a message. <c>SendMessage</c> would block until that thread answered (i.e. forever); the
/// reader must abandon it within <see cref="WindowMessageIconReader.TimeoutMs"/> and let the decision
/// fall through. The window is created hidden (<c>WS_VISIBLE</c> is never set) on a background thread
/// and is never shown, moved or activated: nothing on the owner's desktop is touched.
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
    public void AHungWindow_IsAbandonedWithinTheBound_AndTheSourceDeclines()
    {
        var hang = HungWindow.Start(withClassIcon: false);

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
        var hang = HungWindow.Start(withClassIcon: true);

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
    /// A hidden window on a background thread that creates it and then never pumps a message: the
    /// textbook hung window. It is never shown (no WS_VISIBLE), never activated and never destroyed
    /// from another thread (DestroyWindow must run on the owning thread, which is asleep); the OS
    /// reclaims it at process exit, and it is invisible either way.
    /// </summary>
    private sealed class HungWindow
    {
        private const int IdiApplication = 32512;

        // Rooted for the window's lifetime: a collected delegate would leave the class's WndProc
        // dangling (the thread never answers, but the pointer must still be valid).
        private static readonly WndProc WindowProc = DefWindowProcW;

        private readonly string _className = "EyeCtrlIconProbe_" + Guid.NewGuid().ToString("N");
        private readonly bool _withClassIcon;
        private readonly ManualResetEventSlim _ready = new();
        private IntPtr _hwnd;

        private HungWindow(bool withClassIcon) => _withClassIcon = withClassIcon;

        internal IntPtr Handle => _hwnd;

        internal static HungWindow Start(bool withClassIcon)
        {
            var window = new HungWindow(withClassIcon);
            new Thread(window.Run) { IsBackground = true, Name = "hung-icon-probe" }.Start();

            if (!window._ready.Wait(TestTimeouts.PositiveWaitMs) || window._hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the hidden probe window.");

            return window;
        }

        private void Run()
        {
            try
            {
                var wndProc = Marshal.GetFunctionPointerForDelegate(WindowProc);
                var instance = GetModuleHandleW(null);
                var wc = new WndClassEx
                {
                    Size = (uint)Marshal.SizeOf<WndClassEx>(),
                    WndProc = wndProc,
                    Instance = instance,
                    ClassName = _className,
                    Icon = _withClassIcon ? LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication)) : IntPtr.Zero,
                };

                if (RegisterClassExW(ref wc) == 0)
                {
                    _ready.Set();
                    return;
                }

                _hwnd = CreateWindowExW(0, _className, "", 0, 0, 0, 100, 100,
                    IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                _ready.Set();

                // The point of the test: this thread never calls GetMessage/DispatchMessage.
                Thread.Sleep(Timeout.Infinite);
            }
            catch
            {
                _ready.Set();
            }
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

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WndClassEx wndClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIconW(IntPtr instance, IntPtr icon);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? moduleName);
    }
}
