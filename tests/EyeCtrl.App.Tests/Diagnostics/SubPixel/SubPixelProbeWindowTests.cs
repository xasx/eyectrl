namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
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

    // ---- B2: the seven dwell targets are hit at their RENDERED centres ---------------------------

    private static readonly string[] AllTargetIds =
    [
        SubPixelProbeViewModel.AnswerOneTargetId,
        SubPixelProbeViewModel.AnswerTwoTargetId,
        SubPixelProbeViewModel.EndTestTargetId,
        SubPixelProbeViewModel.IntroStartTargetId,
        SubPixelProbeViewModel.AskYesTargetId,
        SubPixelProbeViewModel.AskLaterTargetId,
        SubPixelProbeViewModel.ResultDoneTargetId,
    ];

    private static readonly (string Id, SubPixelProbePhase Phase)[] TargetPhases =
    [
        (SubPixelProbeViewModel.IntroStartTargetId, SubPixelProbePhase.Intro),
        (SubPixelProbeViewModel.AnswerOneTargetId, SubPixelProbePhase.Answer),
        (SubPixelProbeViewModel.AnswerTwoTargetId, SubPixelProbePhase.Answer),
        (SubPixelProbeViewModel.EndTestTargetId, SubPixelProbePhase.Intro),   // End test is on every screen
        (SubPixelProbeViewModel.AskYesTargetId, SubPixelProbePhase.Ask),
        (SubPixelProbeViewModel.AskLaterTargetId, SubPixelProbePhase.Ask),
        (SubPixelProbeViewModel.ResultDoneTargetId, SubPixelProbePhase.Result),
    ];

    /// <summary>
    /// B2: every one of the seven targets must be hittable at the rendered centre of its button, in
    /// the phase where that button is visible, at DPI 1.0 and 2.0 (scale injected, C4's pattern).
    /// The screen buttons live inside StackPanels, so their Canvas attachments are NaN — the mutant
    /// that reads Canvas.Left/Top leaves them unhittable and this test goes red. The mutant that
    /// computes geometry only once (without the phase recompute) leaves the four screen buttons at
    /// their registration-time centre.
    /// </summary>
    [StaFact]
    public void EveryTarget_IsHitAtItsRenderedCentre_InItsPhase()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        foreach (double scale in new[] { 1.0, 2.0 })
        {
            foreach (var (id, phase) in TargetPhases)
            {
                AssertTargetHitAtScale(id, phase, scale);
            }
        }
    }

    private static void AssertTargetHitAtScale(string id, SubPixelProbePhase phase, double scale)
    {
        using var vm = new SubPixelProbeViewModel(
            new WeakReferenceMessenger(),
            new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
            SubPixelAbSchedule.Create(seed: 11, new PhysicalRect(0, 0, 1920, 1080)),
            new EyeCtrlSettings().Dwell,
            () => true);
        vm.Start();

        var window = new SubPixelProbeWindow(null, vm, new CursorSettings(), () => { }, scale);
        window.Width = 1920;
        window.Height = 1080;
        try
        {
            new WindowInteropHelper(window).EnsureHandle();

            // Drive the phase through the view's own PhaseChanged path (Idle -> phase), so the
            // geometry recompute is exercised exactly as production does it. No explicit Layout()
            // call: computing once at construction must not pass this test.
            SetPhase(vm, SubPixelProbePhase.Idle);
            SetPhase(vm, phase);

            var element = window.TargetElementForTest(id)!;
            Check.WithCustomMessage($"{id}@x{scale}: the button is not visible in {phase}")
                .That(element.Visibility).IsEqualTo(Visibility.Visible);

            Point centreDip = element
                .TransformToAncestor((Visual)window.Content)
                .Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            var centrePhysical = new PhysicalPoint(centreDip.X * scale, centreDip.Y * scale);

            // A point outside every button (the top-left region) hits nothing.
            Feed(vm, new PhysicalPoint(2, 2), 0);
            Feed(vm, new PhysicalPoint(2, 2), 100_000);
            Check.WithCustomMessage($"{id}@x{scale}: a point outside every button hit a target")
                .That(AllTargetIds.Select(i => vm.GetProgress(i, DwellCategory.Control)))
                .ContainsOnlyElementsThatMatch(progress => progress == 0);

            // A short dwell at the rendered centre registers on exactly this target.
            Feed(vm, centrePhysical, 200_000);
            Feed(vm, centrePhysical, 300_000);
            Check.WithCustomMessage($"{id}@x{scale}: a dwell at the rendered centre did not register")
                .That(vm.GetProgress(id, DwellCategory.Control)).IsStrictlyGreaterThan(0);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Feed(SubPixelProbeViewModel vm, PhysicalPoint position, long timestampUs) =>
        vm.Receive(new GazeCursorMovedMessage(position, timestampUs));

    /// <summary>
    /// Drives the view to a phase headlessly. C5's Phase is observable for exactly this purpose
    /// (SP-3 review 01 F8); the reflection setter raises PropertyChanged, which is what the window's
    /// screen switch listens to.
    /// </summary>
    private static void SetPhase(SubPixelProbeViewModel vm, SubPixelProbePhase phase) =>
        typeof(SubPixelProbeViewModel)
            .GetProperty(nameof(SubPixelProbeViewModel.Phase), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(vm, phase);
}
