namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        bool closedObservedInCallback = false;
        SubPixelProbeWindow? windowRef = null;
        var window = new SubPixelProbeWindow(null, vm, new CursorSettings(), () =>
        {
            // W6-closedflag: "already closed" must be true before the session-end callback runs, or a
            // re-entrant Restore inside it would try to close the window again.
            closedObservedInCallback = windowRef!.ClosedObserved;
            ended++;
        }, viewFault: () => { });
        windowRef = window;
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
            Check.WithCustomMessage("The close flag was set after the session-end callback")
                .That(closedObservedInCallback).IsTrue();
        }
        finally
        {
            if (!window.ClosedObserved) window.Close();
        }
    }

    // ---- M5: the view's phases the plan describes but no test pinned ------------------------------

    [StaFact]
    public void VoidHold_ShowsTheRestYourEyesPanel_WithNoRing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        var window = new SubPixelProbeWindow(null, vm, new CursorSettings(), () => { }, viewFault: () => { });
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            SetPhase(vm, SubPixelProbePhase.VoidHold);

            Check.WithCustomMessage("VoidHold must not draw the ring")
                .That(window.IsRingDrawnForTest).IsFalse();
            Check.WithCustomMessage("VoidHold must show the rest-your-eyes panel")
                .That(window.VoidHoldPanelForTest.Visibility).IsEqualTo(Visibility.Visible);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Result_ShowsTheC12VerdictText_AndNeverTheLogLine()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        const string logLine = "Sub-pixel A/B — drift 9/10 → session 1 CONVICTS (LOG-LINE-SENTINEL)";
        typeof(SubPixelProbeViewModel)
            .GetProperty(nameof(SubPixelProbeViewModel.Session1Verdict))!
            .SetValue(vm, new SubPixelAbSession1Verdict(
                SubPixelAbOutcome.Convicts, SubPixelAbStaticReadout.CannotTellStillRingsApart, logLine));

        var window = new SubPixelProbeWindow(null, vm, new CursorSettings(), () => { }, viewFault: () => { });
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            SetPhase(vm, SubPixelProbePhase.Result);

            Check.WithCustomMessage("The Result screen must render the C12 key text")
                .That(window.ResultTextForTest)
                .Contains(EyeCtrl.Core.Localization.StringTable.Current.T("overlays.subpixel.result_convicted"));
            Check.WithCustomMessage("The Result screen rendered the English log line")
                .That(window.ResultTextForTest.Contains("LOG-LINE-SENTINEL", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            window.Close();
        }
    }

    // ---- M2: the backdrop at the owner's DPI ------------------------------------------------------

    /// <summary>
    /// M2: at scale 2.0 the backdrop is one image pixel per device pixel. Rendered with the repo's
    /// R1 pattern — <see cref="RenderTargetBitmap"/> at 96×scale DPI over a headless, arranged
    /// visual — which is the deterministic way to ask "what lands on device pixels at 192 DPI"
    /// without a visible window.
    /// </summary>
    [StaFact]
    public void Backdrop_AtScale2_IsDrawnOneImagePixelPerDevicePixel()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        // A 1-px checker: pixel identity is exactly what a 1:1 draw preserves.
        var checker = CheckerBitmap(8, 8);
        using var vm = NewViewModel();
        var window = new SubPixelProbeWindow(checker, vm, new CursorSettings(), () => { }, viewFault: () => { }, scale: 2.0);
        window.Width = 4;    // 8 device px at 2.0, so the bitmap covers the scene exactly
        window.Height = 4;
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var content = (FrameworkElement)window.Content!;
            content.Measure(new Size(4, 4));
            content.Arrange(new Rect(0, 0, 4, 4));
            content.UpdateLayout();

            // 192 DPI: 8 device px for 4 DIP, so a 1:1 backdrop reproduces the 8-px checker.
            var bitmap = new RenderTargetBitmap(8, 8, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(content);

            var pixels = new byte[8 * 8 * 4];
            bitmap.CopyPixels(pixels, 8 * 4, 0);
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int i = (y * 8 + x) * 4;
                    byte expected = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
                    Check.WithCustomMessage(
                            $"The backdrop at scale 2.0 is not 1:1 at device pixel ({x},{y}): "
                            + $"B={pixels[i]} G={pixels[i + 1]} R={pixels[i + 2]} expected {(int)expected}")
                        .That(pixels[i]).IsEqualTo(expected);
                    Check.That(pixels[i + 1]).IsEqualTo(expected);
                    Check.That(pixels[i + 2]).IsEqualTo(expected);
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ---- N2: hidden targets are parked after their phase ------------------------------------------

    private static readonly (SubPixelProbePhase Phase, string[] Visible)[] PhaseVisibility =
    [
        (SubPixelProbePhase.Intro, [SubPixelProbeViewModel.IntroStartTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Ask, [SubPixelProbeViewModel.AskYesTargetId, SubPixelProbeViewModel.AskLaterTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Result, [SubPixelProbeViewModel.ResultDoneTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Answer, [SubPixelProbeViewModel.AnswerOneTargetId, SubPixelProbeViewModel.AnswerTwoTargetId, SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.VoidHold, [SubPixelProbeViewModel.EndTestTargetId]),
        (SubPixelProbePhase.Ended, []),
    ];

    /// <summary>
    /// N2: a target is dwell-live only in the phase where its button is shown. A child of a collapsed
    /// StackPanel keeps its own Visibility, its size and its transform, so the ancestor check is what
    /// parks the screen buttons after their phase; otherwise a hidden button steals up to 48 % of a
    /// visible button's disc through the shared nearest-centre detector.
    /// </summary>
    [StaFact]
    public void HiddenTargets_AreParkedAfterTheirPhase_AndVisibleButtonsWinTheirDisc()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        foreach (double scale in new[] { 1.0, 2.0 })
        {
            using var vm = NewViewModel();
            vm.Start();
            var window = new SubPixelProbeWindow(null, vm, new CursorSettings(), () => { }, viewFault: () => { }, scale);
            try
            {
                ProbeWindowTestHarness.EnsureHandleAt(window, 1920, 1080);
                var scene = (Visual)window.Content!;
                var detector = (DwellDetector)typeof(SubPixelProbeViewModel)
                    .GetField("_targets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
                var recorded = new Dictionary<string, PhysicalPoint>();

                foreach (var (phase, visible) in PhaseVisibility)
                {
                    SetPhase(vm, SubPixelProbePhase.Idle);
                    SetPhase(vm, phase);

                    var stolen = new List<string>();
                    var stillLive = new List<string>();

                    foreach (var id in AllTargetIds)
                    {
                        if (visible.Contains(id))
                        {
                            var element = window.TargetElementForTest(id)!;
                            Check.WithCustomMessage($"{id}@{scale}: not arranged in {phase}")
                                .That(element.ActualWidth).IsStrictlyGreaterThan(0);

                            var centreDip = element.TransformToAncestor(scene)
                                .Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
                            var centre = new PhysicalPoint(centreDip.X * scale, centreDip.Y * scale);
                            recorded[id] = centre;

                            double radius = Math.Min(element.ActualWidth, element.ActualHeight) * scale / 2.0;
                            for (double dx = -radius; dx <= radius; dx += 4)
                            {
                                for (double dy = -radius; dy <= radius; dy += 4)
                                {
                                    if (dx * dx + dy * dy > radius * radius) continue;
                                    var point = new PhysicalPoint(centre.X + dx, centre.Y + dy);
                                    if (detector.NearestHittingTargetId(point) != id)
                                        stolen.Add($"{id}@{scale} {phase} at {point}");
                                }
                            }
                        }
                        else
                        {
                            if (recorded.TryGetValue(id, out var old) && detector.HitTest(id, old))
                                stillLive.Add($"{id}@{scale} at its old centre {old} in {phase}");

                            for (double x = 16; x < 1920; x += 64)
                            {
                                for (double y = 16; y < 1080; y += 64)
                                {
                                    if (detector.HitTest(id, new PhysicalPoint(x, y)))
                                        stillLive.Add($"{id}@{scale} at ({x},{y}) in {phase}");
                                }
                            }
                        }
                    }

                    Check.WithCustomMessage(
                            $"Hidden targets are dwell-live or steal a visible disc in {phase} at {scale}: "
                            + string.Join("; ", stillLive.Concat(stolen).Take(6)))
                        .That(stillLive.Concat(stolen).ToList()).IsEmpty();
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    [Fact]
    public void TheProductionFrameClock_IsStopwatch()
    {
        // N5 / M4-clock: the production default must be the Stopwatch, not TickCount64.
        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Diagnostics", "SubPixel", "SubPixelProbeWindow.cs");
        var code = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(path)), @"\s+", string.Empty);

        Check.WithCustomMessage("The frame clock default is no longer Stopwatch.GetTimestamp")
            .That(code.Contains("_frameTicks=frameTicks??Stopwatch.GetTimestamp;")).IsTrue();
        Check.WithCustomMessage("The frame rate default is no longer Stopwatch.Frequency/1000")
            .That(code.Contains("_frameTicksPerMs=frameTicksPerMs>0?frameTicksPerMs:Stopwatch.Frequency/1000.0;")).IsTrue();
        Check.WithCustomMessage("The live target dot no longer uses the injected scale (HR9)")
            .That(code.Contains("Canvas.SetLeft(_targetDot,target.X/_scale-DotDiameter/2);")).IsTrue();
        Check.WithCustomMessage("The live target dot no longer uses the injected scale (HR9)")
            .That(code.Contains("Canvas.SetTop(_targetDot,target.Y/_scale-DotDiameter/2);")).IsTrue();
    }

    // ---- HR1: a view fault must never kill the process -------------------------------------------

    [StaFact]
    public void AThrowingFrame_IsContained_RequestedOnce_AndDetached()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        int faultRequests = 0;
        int injectorCalls = 0;
        var window = new SubPixelProbeWindow(
            null, vm, new CursorSettings(), () => { }, viewFault: () => faultRequests++);
        try
        {
            new WindowInteropHelper(window).EnsureHandle();

            // Arm after EnsureHandle: a real Rendering frame fires during HWND creation (fixup 05
            // F1). Arming before it faults the window there — and with the frame catch removed,
            // hangs inside EnsureHandle instead of failing the explicit frames below.
            window.FaultInjectorForTest = () =>
            {
                injectorCalls++;
                throw new InvalidOperationException("injected frame fault");
            };
            window.FaultSitesForTest = SubPixelProbeWindow.FaultSiteForTest.Frame;

            Check.WithCustomMessage("A frame fault escaped to the dispatcher (ten of those kill the app)")
                .ThatCode(() => { window.OnFrame(); window.OnFrame(); window.OnFrame(); })
                .DoesNotThrow();

            Check.WithCustomMessage("The fault was not requested exactly once")
                .That(faultRequests).IsEqualTo(1);
            Check.WithCustomMessage("Drawing continued after the fault")
                .That(injectorCalls).IsEqualTo(1);
            Check.WithCustomMessage("The frame hook stayed attached (the fault would repeat every frame)")
                .That(window.IsFrameHookAttachedForTest).IsFalse();
        }
        finally
        {
            // Disarm before Close: a mutant that leaves Rendering attached must not fault again
            // inside WPF's render pass while the test tears down (that is the F1 HANG).
            window.FaultInjectorForTest = null;
            window.Close();
        }
    }

    [StaFact]
    public void AThrowingPhaseChange_IsContained_RequestedOnce_AndDetached()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        int faultRequests = 0;
        int injectorCalls = 0;
        var window = new SubPixelProbeWindow(
            null, vm, new CursorSettings(), () => { }, viewFault: () => faultRequests++);
        try
        {
            new WindowInteropHelper(window).EnsureHandle();

            // Phase only, armed after the handle: the injector calls below prove the fault came
            // from the phase path, never from a real frame (fixup 05 F1).
            window.FaultInjectorForTest = () =>
            {
                injectorCalls++;
                throw new InvalidOperationException("injected phase fault");
            };
            window.FaultSitesForTest = SubPixelProbeWindow.FaultSiteForTest.Phase;

            Check.WithCustomMessage("A phase fault escaped to the property-changed raise")
                .ThatCode(() =>
                {
                    SetPhase(vm, SubPixelProbePhase.Intro);
                    SetPhase(vm, SubPixelProbePhase.Ask);
                })
                .DoesNotThrow();

            Check.WithCustomMessage("The fault was not requested exactly once")
                .That(faultRequests).IsEqualTo(1);
            Check.WithCustomMessage(
                    "The phase handler kept working after the fault (the _faulted early-return is gone)")
                .That(injectorCalls).IsEqualTo(1);
            Check.WithCustomMessage("The frame hook stayed attached")
                .That(window.IsFrameHookAttachedForTest).IsFalse();
        }
        finally
        {
            window.FaultInjectorForTest = null;
            window.Close();
        }
    }

    [StaFact]
    public void AThrowingSizeChange_IsContained()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        int faultRequests = 0;
        int injectorCalls = 0;
        var window = new SubPixelProbeWindow(
            null, vm, new CursorSettings(), () => { }, viewFault: () => faultRequests++);
        try
        {
            new WindowInteropHelper(window).EnsureHandle();

            window.FaultInjectorForTest = () =>
            {
                injectorCalls++;
                throw new InvalidOperationException("injected resize fault");
            };
            window.FaultSitesForTest = SubPixelProbeWindow.FaultSiteForTest.Layout;

            // A never-shown HWND raises no SizeChanged headlessly, so the test drives the very
            // handler the event is subscribed to (SizeChanged += OnSizeChanged); its args are unused.
            Check.WithCustomMessage("A size-change fault escaped to the dispatcher (fixup 05 F5)")
                .ThatCode(() => window.OnSizeChanged(window, null!))
                .DoesNotThrow();

            Check.WithCustomMessage("The size path never ran")
                .That(injectorCalls).IsEqualTo(1);
            Check.WithCustomMessage("The fault was not requested exactly once")
                .That(faultRequests).IsEqualTo(1);
        }
        finally
        {
            window.FaultInjectorForTest = null;
            window.Close();
        }
    }

    [Fact]
    public void TheProductionWindowFactory_PassesTheViewFaultThrough()
    {
        // F2: viewFault is a required C6 constructor parameter, and this pins C7's wiring through
        // C14. A dropped or no-op argument would contain the fault but never end the run, leaving
        // the owner behind a frozen opaque window (fixup 04 F2/HR1-prod-wiring).
        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Diagnostics", "SubPixel", "ProbeHost.cs");
        var code = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(path)), @"\s+", string.Empty);

        Check.WithCustomMessage(
                "ProbeHost no longer passes viewFault to SubPixelProbeWindow (HR1-prod-wiring)")
            .That(code.Contains(
                "newSubPixelProbeWindow(backdrop,vm,cursorSettings,sessionEnded,viewFault);"))
            .IsTrue();
    }

    private static SubPixelProbeViewModel NewViewModel() => new(
        new WeakReferenceMessenger(),
        new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
        SubPixelAbSchedule.Create(seed: 7, new PhysicalRect(0, 0, 1920, 1080)),
        new EyeCtrlSettings().Dwell,
        () => true);

    private static BitmapSource CheckerBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                byte value = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }

        return BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
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

        var window = new SubPixelProbeWindow(null, vm, new CursorSettings(), () => { }, viewFault: () => { }, scale);
        try
        {
            ProbeWindowTestHarness.EnsureHandleAt(window, 1920, 1080);

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
