namespace EyeCtrl.App.Tests.Services;

using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The probe's POSITION source. A toolbar yield and the gaze-off un-yield move the keyboard window
/// by setting <c>Left</c>/<c>Top</c> only: the size does not change, the VM does not flip, and the
/// edge bar's buttons do not change — every source the probe had before this fix stays silent. The
/// note in Edit then keeps a placement derived against the rect the keyboard vacated and the
/// un-yielded (or yielded) window lands on its text and controls.
/// <para>
/// The window is NEVER shown. An EnsureHandle-only probe (the repo's HandleOnlyProbe pattern) has
/// a real HWND, so <c>Left</c>/<c>Top</c> go through <c>SetWindowPos</c> and WPF raises
/// <c>LocationChanged</c> exactly as it does for the production window — while nothing is painted
/// and no visual tree is arranged.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardProbeMoveTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private const double WidthDip = 1000;
    private const double HeightDip = 400;
    private const double ToolbarExtentDip = 132;

    // Bottom-docked Top: 1040 - 400 = 640. The yielded Top: 640 - 132 = 508.
    private const double DockedTop = 640;
    private const double YieldedTop = DockedTop - ToolbarExtentDip;

    [StaFact]
    public void MovingTheKeyboard_RaisesChanged_ExactlyOnce_AfterADispatcherPump()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = NewWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        // A position to move FROM: Left/Top start unset (NaN) on a never-shown window.
        window.Left = 100;
        window.Top = 100;
        PumpBackgroundHop();

        using var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => null);
        int raises = 0;
        probe.Changed += () => raises++;

        try
        {
            // One vertical move — the shape a yield / un-yield makes.
            window.Top += 40;
            Check.WithCustomMessage("the raise must be queued one Background hop, not delivered synchronously")
                .That(raises).IsEqualTo(0);
            PumpBackgroundHop();
            Check.WithCustomMessage("one move, one raise").That(raises).IsEqualTo(1);

            window.Top += 40;
            PumpBackgroundHop();
            Check.WithCustomMessage("a second move must raise again").That(raises).IsEqualTo(2);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void AfterDispose_MovingTheKeyboard_RaisesNothing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = NewWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        window.Left = 100;
        window.Top = 100;
        PumpBackgroundHop();
        var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => null);
        int raises = 0;
        probe.Changed += () => raises++;

        // The premise: the subscription really is live at this point.
        Check.WithCustomMessage("the probe never subscribed to LocationChanged")
            .That(LocationChangedHandlers(window)).IsNotNull();
        window.Top += 40;
        PumpBackgroundHop();
        Check.That(raises).IsEqualTo(1);

        probe.Dispose();
        window.Top += 40;
        PumpBackgroundHop();

        Check.WithCustomMessage("a move after Dispose raised Changed").That(raises).IsEqualTo(1);
        // The raise after Dispose is already swallowed by the probe's own _disposed guard, so a
        // missing unsubscribe is invisible through Changed. Pin the subscription itself: a
        // leftover handler keeps the probe (and its dispatcher hop) alive for the window's life.
        Check.WithCustomMessage(
                "Dispose left the probe's handler on the window's LocationChanged event")
            .That(LocationChangedHandlers(window)).IsNull();
        window.Close();
    }

    /// <summary>
    /// The window's live <c>LocationChanged</c> handlers, read from WPF's own event list
    /// (the same reflection-into-internals style the keyboard harness uses for its geometry).
    /// </summary>
    private static Delegate? LocationChangedHandlers(Window window)
    {
        var key = typeof(Window)
            .GetField("EVENT_LOCATIONCHANGED", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var events = typeof(Window)
            .GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)
            as System.ComponentModel.EventHandlerList;
        return key is null || events is null ? null : events[key];
    }

    [StaFact]
    public void TheGazeOffUnYield_ThroughTheProductionPath_RaisesChanged_ForTheMoveItMakes()
    {
        // The bug's own path: the toolbar appears (a yield), then hides; once the gaze has been
        // outside the window long enough the gaze-off gate applies UnYield. Both moves must reach
        // the probe — otherwise the note never re-derives against the moved keyboard.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = NewWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        window.SetScreenEdge("bottom");
        window.PlaceForShow(WorkArea, WidthDip, HeightDip);
        Check.That(window.Top).IsEqualTo(DockedTop);
        // The gate's geometry reads, supplied the way the existing headless un-yield tests do
        // (the window is never presented, so its own PointToScreen has no rect to give).
        window.UnYieldProbeGeometry = new KeyboardWindow.UnYieldGeometry(
            WorkArea, WidthDip, HeightDip,
            new PhysicalRect(460, YieldedTop, WidthDip, HeightDip), PixelsPerDip: 1.0);

        using var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => null);
        int raises = 0;
        probe.Changed += () => raises++;

        // The toolbar appears on the keyboard's edge, then the production placement runs. The
        // SetToolbarOccupancy state half and the Yield placement are the two halves of the
        // coordinator's call; the placement is explicit here because RepositionOnScreen reads
        // ActualWidth/ActualHeight, which stay 0 on a window this never-shown harness cannot
        // arrange (the placement RULE is the same one it calls).
        window.SetToolbarOccupancy(true, "bottom", ToolbarExtentDip);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        PumpBackgroundHop();
        Check.That(window.Top).IsEqualTo(YieldedTop);
        Check.WithCustomMessage("the yield move must raise Changed").That(raises).IsEqualTo(1);

        // The toolbar hides: that alone must NOT move the keyboard back (C1) and so must not raise.
        window.SetToolbarOccupancy(false, "none", 0);
        window.Place(WorkArea, WidthDip, HeightDip, KeyboardMove.Yield);
        Check.That(window.Top).IsEqualTo(YieldedTop);
        Check.WithCustomMessage("a hiding toolbar is not a move").That(raises).IsEqualTo(1);

        // 300 ms of continuous samples outside the keep-clear area opens the gate: the un-yield.
        var away = new PhysicalPoint(4000, 2000);
        const long start = 1_000_000;
        for (var t = start; t < start + KeyboardUnYieldGate.OffThresholdUs; t += 50_000)
        {
            window.UpdateGaze(away, t);
            Check.WithCustomMessage("the keyboard un-yielded before the off time was complete")
                .That(window.Top).IsEqualTo(YieldedTop);
        }
        window.UpdateGaze(away, start + KeyboardUnYieldGate.OffThresholdUs);
        PumpBackgroundHop();

        Check.WithCustomMessage("the un-yield did not move the keyboard back")
            .That(window.Top).IsEqualTo(DockedTop);
        Check.WithCustomMessage("one un-yield move, one more raise").That(raises).IsEqualTo(2);
        window.Close();
    }

    /// <summary>The HandleOnlyProbe window: an HWND for real Left/Top moves, never a Show.</summary>
    private static KeyboardWindow NewWindow()
    {
        var window = new KeyboardWindow();
        _ = new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    /// <summary>Drains the probe's raise: it is queued at Background, one hop after the move.</summary>
    private static void PumpBackgroundHop() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
