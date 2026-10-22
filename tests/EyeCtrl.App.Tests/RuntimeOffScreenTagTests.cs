namespace EyeCtrl.App.Tests;

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using NFluent;
using Xunit;

/// <summary>
/// R1 (#260 review): a <c>PrepareOffScreen</c> window (the non-layered glass backdrop) is only safe through <c>ShowBackdrop</c>, which goes through the
/// <c>PositionForShow</c> seam; a plain <c>Show()</c> lands it on a monitor. So the runtime check must NOT take its word for it: the window carries a
/// DISTINCT tag, and is judged by its rectangle (off every monitor = fine, on a monitor = an offence). Every window here is shown at -30000 (off
/// every monitor), and a lenient monitor (<c>requireOnMonitor: false</c>) judges it as if it were on one, the way <c>RuntimeWindowGuardTests</c>
/// proves the rest of the check: nothing is ever visible to anyone. The one probe of the strict judge's monitor lookup uses an HWND at (100,100)
/// that is never shown (it only has a handle) and is DWM-cloaked as well.
/// </summary>
[Collection("WpfApplication")]
public class RuntimeOffScreenTagTests
{
    private static Window OpenOffScreen(Action<Window> tag)
    {
        var window = new Window
        {
            Width = 120, Height = 80, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Title = "off-screen tag probe",
        };
        tag(window);
        window.Show();
        return window;
    }

    private static RuntimeWindowMonitor Started(bool requireOnMonitor)
    {
        var monitor = new RuntimeWindowMonitor(requireOnMonitor);
        monitor.EnsureStarted();
        Assert.SkipUnless(monitor.IsActive, "RUNTIME CHECK INACTIVE: no interactive desktop here, the WinEvent hook could not be installed");
        return monitor;
    }

    private static bool Saw(RuntimeWindowMonitor monitor, Window window) =>
        monitor.Peek().Any(o => o.Handle == new WindowInteropHelper(window).Handle);

    [StaFact]
    public void AnOffScreenTaggedWindow_IsJudgedByItsRectangle_SoOneThatLandsOnAMonitorIsAnOffence()
    {
        // the lenient monitor stands in for "the rectangle meets a monitor": the off-screen tag must NOT excuse the window
        var lenient = Started(requireOnMonitor: false);
        Window? window = null;
        try
        {
            window = OpenOffScreen(w => RuntimeWindowMonitor.MarkPrepared(w, offScreen: true));
            lenient.Flush();
            lenient.ScanVisibleWindows();

            Check.WithCustomMessage("an off-screen-tagged window that lands on a monitor must be an offence (a plain Show() of the backdrop)")
                .That(Saw(lenient, window)).IsTrue();
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void AnOffScreenTaggedWindow_ThatStaysOffEveryMonitor_IsNotAnOffence_OnTheRealMonitor()
    {
        // what PrepareOffScreen + ShowBackdrop does: -30000, on no monitor. The real (strict) monitor stays silent about it.
        var strict = Started(requireOnMonitor: true);
        Window? window = null;
        try
        {
            window = OpenOffScreen(w => RuntimeWindowMonitor.MarkPrepared(w, offScreen: true));
            strict.Flush();
            strict.ScanVisibleWindows();

            Check.That(Saw(strict, window)).IsFalse();
            RuntimeVisibleWindowCheckAttribute.Verify(strict, "BackdropOffScreen");
        }
        finally { window?.Close(); }
    }

    [StaFact]
    public void ATrustedTag_StillExcusesAWindow_EvenOnALenientMonitor()
    {
        // Prepare / PrepareCloaked keep the trusted tag: only the off-screen kind is judged by position
        var lenient = Started(requireOnMonitor: false);
        Window? window = null;
        try
        {
            window = OpenOffScreen(w => RuntimeWindowMonitor.MarkPrepared(w));
            lenient.Flush();
            lenient.ScanVisibleWindows();

            Check.That(Saw(lenient, window)).IsFalse();
        }
        finally { window?.Close(); }
    }

    private const int DwmaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StaFact]
    public void TheStrictJudge_FindsTheMonitor_AndAnOffScreenTagThereOffends_NeverShown()
    {
        // #261 review 01 (F1): the REAL judge path, MonitorFromRect on the strict monitor, reporting "on a monitor". The window only gets an
        // HWND: it is NEVER shown (no WS_VISIBLE, nothing painted, no SHOW event for the hook) and it is DWM-cloaked as well. `Visible` is
        // supplied the way HandleShow supplies it from the SHOW event; the cloak is lifted the same way, on the facts only.
        foreach (var (label, offScreen) in new[] { ("offscreen", (bool?)true), ("trusted", (bool?)false), ("untagged", (bool?)null) })
        {
            var window = new Window { Left = 100, Top = 100, Width = 50, Height = 50, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                var on = 1;
                _ = DwmSetWindowAttribute(handle, DwmaCloak, ref on, sizeof(int));
                if (offScreen is bool off) RuntimeWindowMonitor.MarkPrepared(window, offScreen: off);

                var facts = RuntimeWindowMonitor.Read(handle, requireOnMonitor: true);

                Check.WithCustomMessage($"{label}: the probe window must never be visible").That(facts.Visible).IsFalse();
                Check.WithCustomMessage($"{label}: the strict judge must find the monitor under (100,100)").That(facts.OnMonitor).IsTrue();
                Check.WithCustomMessage($"{label}: only the trusted tag excuses a window that is on a monitor")
                    .That(RuntimeWindowMonitor.IsReallyVisible(facts with { Visible = true, Cloaked = false })).IsEqualTo(label != "trusted");
            }
            finally { window.Close(); }
        }
    }

    [StaFact]
    public void TheHelpers_TagTheirWindows_PrepareAndPrepareCloakedTrusted_PrepareOffScreenDistinct()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        // handles only: nothing is shown
        var opacity = InvisibleTestWindow.Prepare(new Window());
        var cloaked = InvisibleTestWindow.PrepareCloaked(new Window());
        var backdrop = InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow());
        try
        {
            Check.WithCustomMessage("Prepare").That(RuntimeWindowMonitor.Read(new WindowInteropHelper(opacity).EnsureHandle(), false).Prepared).IsTrue();
            Check.WithCustomMessage("PrepareCloaked").That(RuntimeWindowMonitor.Read(new WindowInteropHelper(cloaked).EnsureHandle(), false).Prepared).IsTrue();
            Check.WithCustomMessage("PrepareOffScreen must NOT be trusted: it is judged by its rectangle")
                .That(RuntimeWindowMonitor.Read(new WindowInteropHelper(backdrop).EnsureHandle(), false).Prepared).IsFalse();
        }
        finally
        {
            opacity.Close();
            cloaked.Close();
            backdrop.Close();
        }
    }
}
