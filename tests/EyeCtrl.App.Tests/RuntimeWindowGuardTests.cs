namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.App.Tests.Fixtures;
using NFluent;
using Xunit;

/// <summary>
/// Pins the runtime "no really visible window" check (<see cref="RuntimeWindowMonitor"/>) WITHOUT ever painting on the
/// owner's screen. The probes below show plain windows at -30000 (off every monitor) and give the monitor under test
/// <c>requireOnMonitor: false</c>, so it judges them as if they were on a monitor — that is how a window opened by
/// "production code" can be shown to turn the check RED without being visible to anyone. A strict monitor (the real
/// one) is shown the same windows and must stay silent about them.
/// </summary>
[Collection("WpfApplication")]
public class RuntimeWindowGuardTests
{
    private const int DwmaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Stands in for product code that opens a window on its own (a NoteManager, a coordinator's overlay).</summary>
    private static class SimulatedProduction
    {
        internal static Window OpenWindowOffScreen(Action<Window>? configure = null)
        {
            var window = new Window
            {
                Width = 120, Height = 80, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Title = "simulated production window",
            };
            configure?.Invoke(window);
            window.Show();
            return window;
        }
    }

    private static RuntimeWindowMonitor StartedMonitor(bool requireOnMonitor)
    {
        var monitor = new RuntimeWindowMonitor(requireOnMonitor);
        monitor.EnsureStarted();
        Assert.SkipUnless(monitor.IsActive, "RUNTIME CHECK INACTIVE: no interactive desktop here, the WinEvent hook could not be installed");
        return monitor;
    }

    private static bool Saw(RuntimeWindowMonitor monitor, Window window) =>
        monitor.Peek().Any(o => o.Handle == new WindowInteropHelper(window).Handle);

    // ---- installed and active ---------------------------------------------------------------------------------

    [Fact]
    public void TheCheck_IsInstalledOnThisAssembly()
    {
        var attributes = typeof(RuntimeWindowGuardTests).Assembly.GetCustomAttributes<RuntimeVisibleWindowCheckAttribute>().ToList();
        Check.WithCustomMessage("[assembly: RuntimeVisibleWindowCheck] is missing from AssemblyInfo.cs: no test would fail on a visible window")
            .That(attributes).HasSize(1);
    }

    [Fact]
    public void TheGlobalMonitor_IsActive_WhenADesktopIsAvailable()
    {
        RuntimeWindowMonitor.Global.EnsureStarted();
        Assert.SkipUnless(RuntimeWindowMonitor.Global.IsActive, "RUNTIME CHECK INACTIVE: no interactive desktop here");
        Check.That(RuntimeWindowMonitor.Global.IsActive).IsTrue();
    }

    [Fact]
    public void TheKnownOffenders_StillExist_SoTheWaiverListCannotRot()
    {
        var assembly = typeof(RuntimeWindowGuardTests).Assembly;
        Check.WithCustomMessage("one entry per test: 20 NoteManagerTests members + the AdaptationPreview note test")
            .That(RuntimeVisibleWindowCheckAttribute.KnownVisibleWindowOffenders.Count).IsEqualTo(21);
        foreach (var (entry, reason) in RuntimeVisibleWindowCheckAttribute.KnownVisibleWindowOffenders)
        {
            Check.WithCustomMessage($"entry '{entry}' needs a reason").That(reason.Length).IsStrictlyGreaterThan(20);
            var cut = entry.LastIndexOf('.');
            var method = assembly.GetType(entry[..cut])?.GetMethod(entry[(cut + 1)..]);
            Check.WithCustomMessage($"KnownVisibleWindowOffenders entry '{entry}' names no test any more: delete it")
                .That(method is not null).IsTrue();
        }
    }

    [Fact]
    public void AKnownOffenderThatNoLongerPaintsAWindow_IsStale_AndFails()
    {
        RuntimeVisibleWindowCheckAttribute.JudgeKnownOffender("X.Offending", offended: true);   // expected: no throw
        var thrown = Assert.Throws<InvalidOperationException>(() => RuntimeVisibleWindowCheckAttribute.JudgeKnownOffender("X.Fixed", offended: false));
        Check.That(thrown.Message).Contains("X.Fixed").And.Contains("DELETE its entry");
    }

    // ---- the predicate -------------------------------------------------------------------------------------------

    private static WindowFacts Facts(bool visible = true, bool cloaked = false, bool onMonitor = true, byte? alpha = null, bool prepared = false) =>
        new(IntPtr.Zero, visible, cloaked, onMonitor, alpha, prepared, "synthetic");

    [Fact]
    public void AWindowIsReallyVisible_OnlyWhenVisible_NotCloaked_OnAMonitor_NotTransparent_AndNotPrepared()
    {
        Check.That(RuntimeWindowMonitor.IsReallyVisible(Facts())).IsTrue();
        Check.That(RuntimeWindowMonitor.IsReallyVisible(Facts(alpha: 255))).IsTrue();
        Check.That(RuntimeWindowMonitor.IsReallyVisible(Facts(alpha: 1))).IsTrue();

        Check.WithCustomMessage("not visible").That(RuntimeWindowMonitor.IsReallyVisible(Facts(visible: false))).IsFalse();
        Check.WithCustomMessage("cloaked").That(RuntimeWindowMonitor.IsReallyVisible(Facts(cloaked: true))).IsFalse();
        Check.WithCustomMessage("off every monitor").That(RuntimeWindowMonitor.IsReallyVisible(Facts(onMonitor: false))).IsFalse();
        Check.WithCustomMessage("alpha 0").That(RuntimeWindowMonitor.IsReallyVisible(Facts(alpha: 0))).IsFalse();
        Check.WithCustomMessage("prepared by the helper").That(RuntimeWindowMonitor.IsReallyVisible(Facts(prepared: true))).IsFalse();
    }

    // ---- the OS path: what the hook sees (all off-screen, nothing is painted) ----------------------------------

    [StaFact]
    public void AnUnpreparedWindowOpenedByProductionStyleCode_IsRecorded_AndMakesTheTestFail()
    {
        var lenient = StartedMonitor(requireOnMonitor: false);
        Window? window = null;
        try
        {
            window = SimulatedProduction.OpenWindowOffScreen();
            lenient.Flush();

            Check.WithCustomMessage("the hook did not see the window shown").That(Saw(lenient, window)).IsTrue();
            var thrown = Assert.Throws<InvalidOperationException>(() => RuntimeVisibleWindowCheckAttribute.Verify(lenient, "SomeTest"));
            Check.That(thrown.Message).Contains("REALLY VISIBLE").And.Contains("simulated production window");
            // reported once: the next test is not blamed for it again
            RuntimeVisibleWindowCheckAttribute.Verify(lenient, "NextTest");
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void TheAttribute_FailsTheTest_AfterWhichAWindowBecameVisible_ButNotAKnownOffenderThatDid()
    {
        var lenient = StartedMonitor(requireOnMonitor: false);
        var attribute = new RuntimeVisibleWindowCheckAttribute(lenient);
        var unlisted = typeof(RuntimeWindowGuardTests).GetMethod(nameof(TheCheck_IsInstalledOnThisAssembly))!;
        // a method that is listed in KnownVisibleWindowOffenders (only its MethodInfo is used; the test is not run)
        var listed = typeof(RuntimeWindowGuardTests).Assembly.GetType("EyeCtrl.App.Tests.Services.NoteManagerTests")!.GetMethod("CreateNote_ShowsWindow")!;
        Window? window = null;
        try
        {
            window = SimulatedProduction.OpenWindowOffScreen();
            lenient.Flush();
            // an unlisted test after which a window became visible: RED
            Assert.Throws<InvalidOperationException>(() => attribute.After(unlisted, null!));

            // a LISTED test that painted: passes (its window is drained, so it is not blamed again)
            window.Close();
            lenient.ScanVisibleWindows();      // forgets the closed window's handle, so a reused handle value is reported afresh
            window = SimulatedProduction.OpenWindowOffScreen();
            lenient.Flush();
            attribute.After(listed, null!);

            // a LISTED test that painted nothing: its entry is stale, RED
            var stale = Assert.Throws<InvalidOperationException>(() => attribute.After(listed, null!));
            Check.That(stale.Message).Contains("DELETE its entry");
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void AListedKnownOffender_IsNotStale_WhenItsWindowLandsOffEveryMonitor_OnASmallScreen()
    {
        // CI's screen is smaller than the owner's: a NoteWindow that is on a monitor here can be off every monitor there. Staleness asks only
        // whether the test OPENED an unprepared visible window, wherever it landed; the strict (real) monitor must agree.
        var strict = StartedMonitor(requireOnMonitor: true);
        var attribute = new RuntimeVisibleWindowCheckAttribute(strict);
        var listed = typeof(RuntimeWindowGuardTests).Assembly.GetType("EyeCtrl.App.Tests.Services.NoteManagerTests")!.GetMethod("CreateNote_ShowsWindow")!;
        Window? window = null;
        try
        {
            window = SimulatedProduction.OpenWindowOffScreen();
            strict.Flush();
            attribute.After(listed, null!);          // opened a window (off-screen): not stale, and no strict offence either
            Check.That(Saw(strict, window)).IsFalse();
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void TheRealMonitor_StaysSilent_AboutAWindowThatIsOffEveryMonitor()
    {
        var strict = StartedMonitor(requireOnMonitor: true);
        Window? window = null;
        try
        {
            window = SimulatedProduction.OpenWindowOffScreen();
            strict.Flush();
            strict.ScanVisibleWindows();

            Check.WithCustomMessage("a window at -30000 is not on a monitor").That(Saw(strict, window)).IsFalse();
            RuntimeVisibleWindowCheckAttribute.Verify(strict, "OffScreen");
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void APreparedWindow_IsNeverRecorded()
    {
        var lenient = StartedMonitor(requireOnMonitor: false);
        Window? window = null;
        try
        {
            // AllowsTransparency: the Opacity is composed per pixel, so only the helper's tag can exempt it
            window = SimulatedProduction.OpenWindowOffScreen(w =>
            {
                w.AllowsTransparency = true;
                w.Background = System.Windows.Media.Brushes.Transparent;
                InvisibleTestWindow.Prepare(w);
            });
            lenient.Flush();
            lenient.ScanVisibleWindows();

            Check.That(Saw(lenient, window)).IsFalse();
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void AnUnpreparedLayeredWindow_IsRecorded_BecauseItsOpacityCannotBeReadFromTheOs()
    {
        var lenient = StartedMonitor(requireOnMonitor: false);
        Window? window = null;
        try
        {
            window = SimulatedProduction.OpenWindowOffScreen(w =>
            {
                w.AllowsTransparency = true;
                w.Background = System.Windows.Media.Brushes.Transparent;
            });
            lenient.Flush();

            Check.That(Saw(lenient, window)).IsTrue();
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void ACloakedWindow_IsNeverRecorded_EvenWithoutTheHelperTag()
    {
        var lenient = StartedMonitor(requireOnMonitor: false);
        Window? window = null;
        try
        {
            window = SimulatedProduction.OpenWindowOffScreen(w =>
            {
                var handle = new WindowInteropHelper(w).EnsureHandle();
                var on = 1;
                DwmSetWindowAttribute(handle, DwmaCloak, ref on, sizeof(int));
            });
            lenient.Flush();
            lenient.ScanVisibleWindows();

            Assert.SkipUnless(InvisibleTestWindow.IsCloaked(window) == true, "DWM did not cloak the probe window here");
            Check.That(Saw(lenient, window)).IsFalse();
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void WpfOpacityZero_OnAWindowThatIsNotLayered_DoesNotHideIt_SoItIsRecorded()
    {
        // Window.Opacity only hides a window with AllowsTransparency=True (the helper's proof asserts that for every type it
        // prepares); on an ordinary window WPF leaves it fully visible, and the check says so.
        var lenient = StartedMonitor(requireOnMonitor: false);
        Window? window = null;
        try
        {
            window = SimulatedProduction.OpenWindowOffScreen(w => w.Opacity = 0);
            lenient.Flush();

            Check.That(Saw(lenient, window)).IsTrue();
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void AWindowThatIsStillOpen_IsFoundByTheEndOfTestScan_EvenIfItsShowWasMissed()
    {
        Window? window = null;
        try
        {
            window = SimulatedProduction.OpenWindowOffScreen();
            // a monitor created AFTER the show never got the event; the scan finds the window anyway
            var late = StartedMonitor(requireOnMonitor: false);
            late.ScanVisibleWindows();

            Check.That(Saw(late, window)).IsTrue();
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void TheFlush_AlwaysDeliversTheShownWindow_BeforeItReturns()
    {
        var lenient = StartedMonitor(requireOnMonitor: false);
        for (var i = 0; i < 30; i++)
        {
            Window? window = null;
            try
            {
                window = SimulatedProduction.OpenWindowOffScreen();
                lenient.Flush();
                Check.WithCustomMessage($"round {i}: the show event had not been handled after Flush").That(Saw(lenient, window)).IsTrue();
            }
            finally { window?.Close(); }
        }
    }

    // ---- L1 (#258 review 01): no cross-thread WM_GETTEXT, and only an offender is described ---------------------------------

    [Fact]
    public void TheMonitor_ReadsTitlesWithInternalGetWindowText_NeverWithTheMessageSendingGetWindowText()
    {
        // GetWindowText on a window of ANOTHER thread of this process sends WM_GETTEXT and waits for that thread; InternalGetWindowText sends nothing.
        var raw = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName, "tests", "EyeCtrl.App.Tests", "Fixtures", "RuntimeWindowGuard.cs"));
        var code = NoVisibleTestWindowGuardTests.StripCommentsAndStrings(raw);
        Check.WithCustomMessage("the runtime check calls GetWindowText / GetWindowTextLength (cross-thread WM_GETTEXT): use InternalGetWindowText")
            .That(Regex.IsMatch(code, @"(?<![\w])GetWindowText\w*")).IsFalse();
        Check.WithCustomMessage("an EntryPoint that renames the import back to GetWindowText")
            .That(Regex.IsMatch(raw, "EntryPoint\\s*=\\s*\"[^\"]*GetWindowText")).IsFalse();
        Check.WithCustomMessage("the title must be read with InternalGetWindowText")
            .That(Regex.IsMatch(code, @"\bInternalGetWindowText\s*\(")).IsTrue();
    }

    [StaFact]
    public void OnlyAnOffendingWindow_IsDescribed_NotAPreparedOneNorAnOffMonitorOne()
    {
        // a monitor of its own, never pumped by anything else: it sees only the windows this test shows (all at -30000)
        var lenient = StartedMonitor(requireOnMonitor: false);
        var strict = StartedMonitor(requireOnMonitor: true);
        Window? prepared = null, offScreenForStrict = null, offender = null;
        try
        {
            prepared = SimulatedProduction.OpenWindowOffScreen(w => { w.AllowsTransparency = true; w.Background = System.Windows.Media.Brushes.Transparent; InvisibleTestWindow.Prepare(w); });
            offScreenForStrict = SimulatedProduction.OpenWindowOffScreen();
            lenient.Flush();
            strict.Flush();
            // the lenient monitor saw a prepared and an unprepared window show: only the unprepared one is an offender, so only it was described
            Check.That(Saw(lenient, prepared)).IsFalse();
            Check.That(Saw(lenient, offScreenForStrict)).IsTrue();
            Check.WithCustomMessage("a prepared window must not be described: one description per recorded offence")
                .That(lenient.DescriptionsBuilt).IsEqualTo(lenient.Peek().Count);
            Check.WithCustomMessage("a window off every monitor must not be described by the strict monitor")
                .That(strict.DescriptionsBuilt).IsEqualTo(0);
            Check.That(strict.Peek()).IsEmpty();

            var before = lenient.DescriptionsBuilt;
            offender = SimulatedProduction.OpenWindowOffScreen();
            lenient.Flush();
            Check.WithCustomMessage("every recorded offence carries exactly one description").That(lenient.DescriptionsBuilt).IsEqualTo(lenient.Peek().Count);
            Check.That(lenient.DescriptionsBuilt).IsStrictlyGreaterThan(before);
            Check.That(lenient.Peek().First(o => o.Handle == new WindowInteropHelper(offender).Handle).Description).Contains("simulated production window");
        }
        finally { prepared?.Close(); offScreenForStrict?.Close(); offender?.Close(); }
    }

    // ---- L2 (#258 review 01): a show that was hidden again before the hook read it is still a flash ---------------------------

    private static Window HiddenAfterAShow(Action<Window>? configure = null)
    {
        var window = new Window
        {
            Width = 120, Height = 80, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Title = "flash window",
        };
        configure?.Invoke(window);
        window.Show();
        window.Hide();
        return window;
    }

    [StaFact]
    public void AShowThatIsHiddenAgainBeforeTheEventIsRead_IsStillAnOffence()
    {
        // The hook callback runs after the show; here the event is handled by hand AFTER the Hide, which is exactly what a sub-millisecond
        // Show -> Hide does to the real hook. Everything is at -30000 (a lenient monitor judges it as if it were on a monitor).
        var late = new RuntimeWindowMonitor(requireOnMonitor: false);
        Window? window = null;
        try
        {
            window = HiddenAfterAShow();
            var handle = new WindowInteropHelper(window).Handle;
            Check.WithCustomMessage("the probe window must really be hidden when the event is read").That(RuntimeWindowMonitor.Read(handle, false).Visible).IsFalse();

            late.HandleShow(handle);

            Check.WithCustomMessage("a Show -> Hide flash got away").That(Saw(late, window)).IsTrue();
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void AHiddenAgainShow_IsNotAnOffence_WhenTheWindowIsPrepared_Cloaked_OffEveryMonitor_OrGone()
    {
        Window? prepared = null, cloaked = null, offMonitor = null, gone = null;
        try
        {
            // prepared by the helper: tagged before it shows
            prepared = HiddenAfterAShow(w => { w.AllowsTransparency = true; w.Background = System.Windows.Media.Brushes.Transparent; InvisibleTestWindow.Prepare(w); });
            var lenient = new RuntimeWindowMonitor(requireOnMonitor: false);
            lenient.HandleShow(new WindowInteropHelper(prepared).Handle);
            Check.WithCustomMessage("a prepared window").That(Saw(lenient, prepared)).IsFalse();

            // cloaked before it shows (PrepareCloaked does exactly this)
            cloaked = HiddenAfterAShow(w =>
            {
                var handle = new WindowInteropHelper(w).EnsureHandle();
                var on = 1;
                DwmSetWindowAttribute(handle, DwmaCloak, ref on, sizeof(int));
            });
            var cloakedHandle = new WindowInteropHelper(cloaked).Handle;
            if (RuntimeWindowMonitor.Read(cloakedHandle, false).Cloaked)
            {
                lenient.HandleShow(cloakedHandle);
                Check.WithCustomMessage("a cloaked window").That(Saw(lenient, cloaked)).IsFalse();
            }

            // off every monitor, judged by the real (strict) monitor
            offMonitor = HiddenAfterAShow();
            var strict = new RuntimeWindowMonitor(requireOnMonitor: true);
            strict.HandleShow(new WindowInteropHelper(offMonitor).Handle);
            Check.WithCustomMessage("a window off every monitor").That(Saw(strict, offMonitor)).IsFalse();

            // closed again before the event was read: no rectangle, no window, no offence
            gone = HiddenAfterAShow();
            var goneHandle = new WindowInteropHelper(gone).Handle;
            gone.Close();
            lenient.HandleShow(goneHandle);
            Check.WithCustomMessage("a window that no longer exists").That(lenient.Peek().Any(o => o.Handle == goneHandle)).IsFalse();
        }
        finally { prepared?.Close(); cloaked?.Close(); offMonitor?.Close(); gone?.Close(); }
    }

    [StaFact]
    public void ADeadHandle_HasNoRectangle_SoItIsNeverOnAMonitor_EvenForALenientMonitor()
    {
        // HandleShow stops at a window that no longer exists (GetAncestor), and Read must say the same on its own: no rectangle, no monitor.
        var window = HiddenAfterAShow();
        var handle = new WindowInteropHelper(window).Handle;
        window.Close();

        var facts = RuntimeWindowMonitor.Read(handle, requireOnMonitor: false);

        Check.That(facts.OnMonitor).IsFalse();
        Check.That(RuntimeWindowMonitor.IsReallyVisible(facts with { Visible = true })).IsFalse();
    }

    [StaFact]
    public void TheHook_SeesEveryShowHideFlash_NotOnlyTheOnesThatWereStillVisibleWhenItReadThem()
    {
        var lenient = StartedMonitor(requireOnMonitor: false);
        for (var i = 0; i < 30; i++)
        {
            Window? window = null;
            try
            {
                window = HiddenAfterAShow();
                lenient.Flush();
                Check.WithCustomMessage($"round {i}: a Show -> Hide flash was missed").That(Saw(lenient, window)).IsTrue();
            }
            finally { window?.Close(); }
        }
    }

    [StaFact]
    public void MarkPrepared_TagsTheHandle_AndRemovesTheTagWhenTheWindowCloses()
    {
        var window = new Window { Left = -30000, Top = -30000, Width = 50, Height = 50, ShowInTaskbar = false };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        RuntimeWindowMonitor.MarkPrepared(window);
        Check.That(RuntimeWindowMonitor.Read(handle, requireOnMonitor: false).Prepared).IsTrue();

        window.Close();
        // the handle is gone after Close; a fresh read of a dead handle must not claim the tag
        Check.That(RuntimeWindowMonitor.Read(handle, requireOnMonitor: false).Prepared).IsFalse();
    }
}
