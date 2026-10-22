namespace EyeCtrl.App.Tests;

using System;
using System.Linq;
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
/// proves the rest of the check: nothing is ever visible to anyone, and nothing is ever placed on a monitor.
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
