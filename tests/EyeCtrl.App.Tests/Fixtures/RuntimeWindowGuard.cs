namespace EyeCtrl.App.Tests.Fixtures;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Xunit.v3;

/// <summary>
/// What the OS says about one top-level window of this process, and whether that means "really visible to the
/// user": visible, not cloaked by DWM, on a monitor, and not fully transparent. A window a test made invisible on
/// purpose (<see cref="InvisibleTestWindow"/>) is tagged and never offends.
/// </summary>
/// <param name="Handle">The HWND.</param>
/// <param name="Visible">WS_VISIBLE on the window and its ancestors (<c>IsWindowVisible</c>).</param>
/// <param name="Cloaked">DWM reports it cloaked (by the app, the shell or the system).</param>
/// <param name="OnMonitor">Its rectangle is non-empty and meets a monitor.</param>
/// <param name="Alpha">The constant layered alpha when the window has one (an Opacity written through
/// <c>SetLayeredWindowAttributes</c>), else null. A WPF window with <c>AllowsTransparency=True</c> composes its
/// Opacity per pixel and reports null.</param>
/// <param name="Prepared">The window was prepared by <see cref="InvisibleTestWindow"/> (a tag on the HWND).</param>
/// <param name="Description">Class name, title and rectangle, for the failure message.</param>
internal readonly record struct WindowFacts(
    IntPtr Handle, bool Visible, bool Cloaked, bool OnMonitor, byte? Alpha, bool Prepared, string Description);

/// <summary>One window that became really visible during the run.</summary>
internal sealed record VisibleWindowOffence(IntPtr Handle, string Description, string How, DateTime WhenUtc);

/// <summary>
/// The RUNTIME half of the "no App test paints a visible window" guard. The static scan
/// (<c>NoVisibleTestWindowGuardTests</c>) reads test code only; a window that PRODUCTION code opens during a test
/// (NoteManager's note windows, a coordinator's overlay) is invisible to it. This watches the OS instead: it sees
/// every top-level window this process makes visible, whoever asked, and reports one that the user could see.
///
/// <para>
/// <b>How, and why it cannot show anything.</b> A WinEvent hook (<c>EVENT_OBJECT_SHOW</c>, out of context, this
/// process only) delivers an event to a dedicated background thread whenever a window is shown; the thread reads
/// the window's facts with read-only Win32 calls (<c>IsWindowVisible</c>, <c>GetWindowRect</c>, DWM's cloak
/// attribute, the layered alpha). After each test <see cref="ReportAfterTest"/> flushes pending events, then also
/// scans the process's currently visible windows (it catches one that was shown off-screen and moved on, or left
/// open). Nothing here creates, shows, moves or touches a window, and the hook thread does no WPF work.
/// </para>
///
/// <para>
/// <b>Cost.</b> One thread; a few microseconds per shown window; per test one flush round trip and one
/// <c>EnumWindows</c> over the desktop's top-level windows.
/// </para>
///
/// <para>
/// <b>Residual, stated.</b> A window tagged by <see cref="InvisibleTestWindow"/> is trusted: a product change that
/// raised its Opacity after the show would not be seen here (the static pins in <c>InvisibleTestWindowTests</c>
/// carry that). A window shown while its rectangle is off every monitor and moved on screen later is only seen if
/// it is still open when the test ends. Tests that run in parallel can blame the wrong test (the message says so).
/// A machine without an interactive desktop cannot install the hook; <see cref="IsActive"/> says so and
/// <c>RuntimeWindowGuardTests</c> skips loudly instead of passing silently.
/// </para>
/// </summary>
internal sealed class RuntimeWindowMonitor
{
    /// <summary>The window property that marks a window prepared by <see cref="InvisibleTestWindow"/>.</summary>
    internal const string PreparedProperty = "EyeCtrl.InvisibleTestWindow";

    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0;
    private const uint WmApp = 0x8000;
    private const uint PmRemove = 1;
    private const uint GaRoot = 2;
    private const uint MonitorDefaultToNull = 0;
    private const uint LwaAlpha = 2;
    private const int DwmaCloaked = 14;

    /// <summary>The monitor every test in the assembly runs under (installed by <see cref="RuntimeVisibleWindowCheckAttribute"/>).</summary>
    internal static RuntimeWindowMonitor Global { get; } = new(requireOnMonitor: true);

    private readonly bool _requireOnMonitor;
    private readonly object _gate = new();
    private readonly List<VisibleWindowOffence> _offences = new();
    private readonly HashSet<IntPtr> _reported = new();
    private readonly HashSet<IntPtr> _opened = new();
    private readonly HashSet<IntPtr> _openedSeen = new();
    private readonly ConcurrentQueue<ManualResetEventSlim> _flushRequests = new();
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private WinEventProc? _proc;
    private int _startState; // 0 = not started, 1 = starting/started

    /// <param name="requireOnMonitor">False only in the probes that show a window off every monitor on purpose.</param>
    internal RuntimeWindowMonitor(bool requireOnMonitor) => _requireOnMonitor = requireOnMonitor;

    /// <summary>True when the hook is installed (an interactive desktop was available).</summary>
    internal bool IsActive => _hook != IntPtr.Zero;

    /// <summary>Starts the hook thread once; safe to call from any test thread.</summary>
    internal void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _startState, 1) == 0)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "RuntimeWindowMonitor" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnWinEvent;
        // The queue must exist before the first PostThreadMessage; PeekMessage creates it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _proc,
            (uint)Environment.ProcessId, 0, WinEventOutOfContext);
        _started.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmApp)
            {
                Drain();
                while (_flushRequests.TryDequeue(out var done)) done.Set();
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>Dispatches whatever is already queued for this thread (the pending WinEvent callbacks).</summary>
    private void Drain()
    {
        while (PeekMessage(out var m, IntPtr.Zero, 0, 0, PmRemove))
        {
            if (m.message == WmApp) { while (_flushRequests.TryDequeue(out var done)) done.Set(); continue; }
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType != EventObjectShow || idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        try
        {
            if (GetAncestor(hwnd, GaRoot) != hwnd) return;
            Consider(Read(hwnd, _requireOnMonitor), "shown");
        }
        catch (Exception)
        {
            // A hook callback must never throw into the message loop.
        }
    }

    private void Consider(WindowFacts facts, string how)
    {
        // "Opened": visible, not cloaked, not prepared — wherever it is (the screen's size differs between machines). Only the
        // known-offender judgement reads this: a listed test that opens a window is not stale even where the window lands off-screen.
        if (IsReallyVisible(facts with { OnMonitor = true }))
            lock (_gate)
                if (_openedSeen.Add(facts.Handle)) _opened.Add(facts.Handle);   // once per window: a window left open is not "opened" again by the next test
        if (!IsReallyVisible(facts)) return;
        lock (_gate)
            _offences.Add(new VisibleWindowOffence(facts.Handle, facts.Description, how, DateTime.UtcNow));
    }

    /// <summary>
    /// For a LISTED known offender: flushes, scans, drains everything, and says whether it opened an unprepared visible window
    /// at all (on any monitor or none). Null when the check is inactive on this machine (nothing can be judged).
    /// </summary>
    internal bool? DrainAndWasAnythingOpened(string testName)
    {
        if (!IsActive) return null;
        Flush();
        ScanVisibleWindows();
        bool opened;
        lock (_gate) opened = _opened.Count > 0;
        TakeReport(testName);   // drains the offences (and the opened set) so the next test is not blamed
        return opened;
    }

    /// <summary>Waits until every WinEvent queued before this call has been handled by the hook thread.</summary>
    internal void Flush()
    {
        if (!IsActive) return;
        using var done = new ManualResetEventSlim(false);
        _flushRequests.Enqueue(done);
        if (!PostThreadMessage(_threadId, WmApp, IntPtr.Zero, IntPtr.Zero)) return;
        done.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// The failure text for the windows that became really visible since the last report (a window is reported once),
    /// or null. Flushes the hook first and scans the windows that are still visible now.
    /// </summary>
    internal string? ReportAfterTest(string testName)
    {
        if (!IsActive) return null;
        Flush();
        ScanVisibleWindows();
        return TakeReport(testName);
    }

    /// <summary>The report for what has been recorded so far (no flush, no scan); null when there is nothing new.</summary>
    internal string? TakeReport(string testName)
    {
        List<VisibleWindowOffence> fresh;
        lock (_gate)
        {
            _opened.Clear();
            fresh = _offences.Where(o => _reported.Add(o.Handle)).ToList();
            _offences.Clear();
        }
        if (fresh.Count == 0) return null;
        return $"{testName}: {fresh.Count} window(s) became REALLY VISIBLE on the screen during the run (visible, not cloaked, on a monitor, " +
               "not prepared by InvisibleTestWindow): " + string.Join("; ", fresh.Select(o => $"[{o.How}] {o.Description}")) +
               ". The owner controls the PC by eye-tracking: make the test's window invisible with InvisibleTestWindow.Prepare, " +
               "or find the production code that opened it and stub it (note: tests running in parallel can be blamed for each other's windows).";
    }

    /// <summary>Records every currently visible top-level window of this process that would offend.</summary>
    internal void ScanVisibleWindows()
    {
        var pid = (uint)Environment.ProcessId;
        var visibleNow = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == pid && IsWindowVisible(hwnd))
                {
                    visibleNow.Add(hwnd);
                    Consider(Read(hwnd, _requireOnMonitor), "still open");
                }
            }
            catch (Exception)
            {
                // a window that vanished mid-scan
            }
            return true;
        }, IntPtr.Zero);

        // A handle that is no longer visible is forgotten, so a later window that reuses the handle value is reported afresh.
        lock (_gate)
        {
            _reported.RemoveWhere(handle => !visibleNow.Contains(handle));
            _openedSeen.RemoveWhere(handle => !visibleNow.Contains(handle));
        }
    }

    /// <summary>Everything recorded and not yet reported, for the probes (no flush, no scan).</summary>
    internal IReadOnlyList<VisibleWindowOffence> Peek()
    {
        lock (_gate) return _offences.ToList();
    }

    /// <summary>"Really visible": visible, not cloaked, on a monitor, not fully transparent, not prepared by the helper.</summary>
    internal static bool IsReallyVisible(WindowFacts f) =>
        f.Visible && !f.Cloaked && f.OnMonitor && f.Alpha != 0 && !f.Prepared;

    internal static WindowFacts Read(IntPtr hwnd, bool requireOnMonitor)
    {
        var visible = IsWindowVisible(hwnd);
        var cloaked = DwmGetWindowAttribute(hwnd, DwmaCloaked, out var cloak, sizeof(int)) == 0 && cloak != 0;
        var haveRect = GetWindowRect(hwnd, out var rect);
        var sized = haveRect && rect.Right > rect.Left && rect.Bottom > rect.Top;
        var onMonitor = sized && (!requireOnMonitor || MonitorFromRect(ref rect, MonitorDefaultToNull) != IntPtr.Zero);
        byte? alpha = GetLayeredWindowAttributes(hwnd, out _, out var a, out var flags) && (flags & LwaAlpha) != 0 ? a : null;
        var prepared = GetProp(hwnd, PreparedProperty) != IntPtr.Zero;
        return new WindowFacts(hwnd, visible, cloaked, onMonitor, alpha, prepared, Describe(hwnd, rect, haveRect));
    }

    private static string Describe(IntPtr hwnd, RECT rect, bool haveRect)
    {
        var cls = new StringBuilder(128);
        GetClassName(hwnd, cls, cls.Capacity);
        var title = new StringBuilder(128);
        GetWindowText(hwnd, title, title.Capacity);
        var wpf = "";
        try { wpf = HwndSource.FromHwnd(hwnd)?.RootVisual?.GetType().Name is { } n ? $" wpf={n}" : ""; }
        catch (Exception) { /* another thread's dispatcher object: class and title are enough */ }
        return $"hwnd=0x{hwnd.ToInt64():X} class={cls} title='{title}'{wpf}" +
               (haveRect ? $" rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})" : "");
    }

    /// <summary>Tags a window as deliberately invisible; the tag goes on as soon as the window has a handle and is removed when it closes.</summary>
    internal static void MarkPrepared(Window window)
    {
        void Tag()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetProp(handle, PreparedProperty, (IntPtr)1);
        }
        Tag();
        window.SourceInitialized += (_, _) => Tag();
        window.Closed += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) RemoveProp(handle, PreparedProperty);
        };
    }

    // ---- Win32 --------------------------------------------------------------------------------------------

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr hwnd, string name);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

/// <summary>
/// Installs <see cref="RuntimeWindowMonitor.Global"/> and fails the test after which a window was seen really visible
/// (see that class). Assembly-wide: <c>[assembly: RuntimeVisibleWindowCheck]</c> in <c>AssemblyInfo.cs</c>. The
/// failure is thrown from <c>After</c>, never from <c>Before</c>, for the reason
/// <c>DpiHelperScaleObserverAttribute</c> documents (a throw from Before skips the rest of the attribute list).
/// </summary>
public sealed class RuntimeVisibleWindowCheckAttribute : BeforeAfterTestAttribute
{
    private readonly RuntimeWindowMonitor _monitor;

    /// <summary>The assembly-wide check, on the real (strict) monitor.</summary>
    public RuntimeVisibleWindowCheckAttribute() => _monitor = RuntimeWindowMonitor.Global;

    /// <summary>For the probes: the same check against a monitor of their own.</summary>
    internal RuntimeVisibleWindowCheckAttribute(RuntimeWindowMonitor monitor) => _monitor = monitor;

    /// <inheritdoc/>
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) => _monitor.EnsureStarted();

    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var name = $"{methodUnderTest.DeclaringType?.FullName}.{methodUnderTest.Name}";
        if (KnownVisibleWindowOffenders.ContainsKey(name))
        {
            // It is expected to open a visible window (drained, so the next test is not blamed for it); one that no longer does is stale.
            var opened = _monitor.DrainAndWasAnythingOpened(name);
            if (opened is not null) JudgeKnownOffender(name, offended: opened.Value);
            return;
        }
        Verify(_monitor, name);
    }

    /// <summary>Throws when a listed known offender did NOT paint a window: the entry is stale and must be deleted.</summary>
    internal static void JudgeKnownOffender(string testName, bool offended)
    {
        if (offended) return;
        throw new InvalidOperationException(
            $"{testName} is listed in KnownVisibleWindowOffenders but no window became really visible any more (or the runtime check is inactive): " +
            "the test is fixed (or renamed) — DELETE its entry from RuntimeVisibleWindowCheckAttribute.KnownVisibleWindowOffenders.");
    }

    /// <summary>
    /// ⚠ THESE TESTS STILL PAINT REAL WINDOWS ON THE SCREEN. They are listed only so that <c>main</c> stays green while the
    /// sticky-notes fix (which stops <c>NoteManager</c> from showing real <c>NoteWindow</c>s in them) is outstanding: the runtime
    /// check found them on its first full run. Agents EXCLUDE them from local runs until this list is empty
    /// (<c>FullyQualifiedName!~NoteManagerTests&amp;FullyQualifiedName!~NoteOverThePanel_ThroughTheRealInventory</c>).
    /// One entry per TEST, never per class. An entry that stops offending FAILS its own test (see
    /// <see cref="JudgeKnownOffender"/>), so the list can only shrink; a test that is not listed and opens a visible window
    /// fails as usual. <c>RuntimeWindowGuardTests.TheKnownOffenders_StillExist</c> fails when an entry names a test that is gone.
    /// The listed tests run in exclusive (non-parallel) collections, so a window is never blamed on the wrong test.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> KnownVisibleWindowOffenders = new Dictionary<string, string>
    {
    };

    /// <summary>Throws when <paramref name="monitor"/> saw a window become really visible since its last report.</summary>
    internal static void Verify(RuntimeWindowMonitor monitor, string testName)
    {
        var report = monitor.ReportAfterTest(testName);
        if (report is not null)
            throw new InvalidOperationException(report);
    }
}
