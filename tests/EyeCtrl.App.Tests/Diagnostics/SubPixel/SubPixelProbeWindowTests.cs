namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// W6 — the probe window's class contract (build plan C6). Headless: the window gets an HWND from
/// <c>EnsureHandle</c> and is NEVER shown. Each asserted property is a mutation target: flipping it
/// in <c>SubPixelProbeWindow</c> must turn this test red.
/// </summary>
[Collection("WpfApplication")]
public class SubPixelProbeWindowTests
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [StaFact]
    public void WindowClass_HasTheProbeProperties_AndRegistersEveryDwellTarget()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        using var vm = new SubPixelProbeViewModel(
            new WeakReferenceMessenger(),
            new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
            SubPixelAbSchedule.Create(seed: 7, new PhysicalRect(0, 0, 1920, 1080)),
            new EyeCtrlSettings().Dwell,
            () => true);

        var ended = 0;
        var window = new SubPixelProbeWindow(null, vm, new CursorSettings(), () => ended++);
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            Check.That(handle).IsNotEqualTo(IntPtr.Zero);

            Check.That(window.Cursor).IsSameReferenceAs(System.Windows.Input.Cursors.None);
            Check.That(window.ShowActivated).IsFalse();
            Check.That(window.ShowInTaskbar).IsFalse();
            Check.That(window.AllowsTransparency).IsFalse();
            Check.That(window.Topmost).IsTrue();
            Check.That(window.IsClickThrough).IsFalse();
            Check.That(window.ExcludeFromScreenCapture).IsFalse();

            // The ring's host must not snap or round (SP-2 review 01's cheap insurance).
            Check.WithCustomMessage("The probe window must not enable layout rounding")
                .That(window.UseLayoutRounding).IsFalse();
            Check.WithCustomMessage("The probe window must not snap to device pixels")
                .That(window.SnapsToDevicePixels).IsFalse();

            var style = GetWindowLong(handle, GwlExStyle);
            Check.WithCustomMessage("WS_EX_NOACTIVATE must be set (the probe never activates)")
                .That((style & WsExNoActivate) != 0).IsTrue();
            Check.WithCustomMessage("WS_EX_TOOLWINDOW must be set (not in Alt-Tab)")
                .That((style & WsExToolWindow) != 0).IsTrue();
            Check.WithCustomMessage("WS_EX_TRANSPARENT must be CLEAR: the probe is not click-through")
                .That((style & WsExTransparent) != 0).IsFalse();

            // The view registers all seven C5 targets through the registrar (SP-3 review 02).
            foreach (var id in new[]
                     {
                         SubPixelProbeViewModel.AnswerOneTargetId,
                         SubPixelProbeViewModel.AnswerTwoTargetId,
                         SubPixelProbeViewModel.EndTestTargetId,
                         SubPixelProbeViewModel.IntroStartTargetId,
                         SubPixelProbeViewModel.AskYesTargetId,
                         SubPixelProbeViewModel.AskLaterTargetId,
                         SubPixelProbeViewModel.ResultDoneTargetId,
                     })
            {
                Check.WithCustomMessage($"The view did not register dwell target '{id}'")
                    .That(vm.HasRegisteredTarget(id)).IsTrue();
            }

            // The close path is the session-end callback captured at construction.
            window.Close();
            Check.That(window.ClosedObserved).IsTrue();
            Check.That(ended).IsEqualTo(1);
        }
        finally
        {
            if (!window.ClosedObserved) window.Close();
        }
    }
}
