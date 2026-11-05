namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Views;
using EyeCtrl.Core;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// C9 F2 fixup-01: every overlay must reach ZBID_UIACCESS (band 2), the band Start, Search and the
/// notification centre cannot cover, and the band must SURVIVE the hide/show cycles the keyboard,
/// action menu, magnifier, toolbar, activation button and ring go through. The creation raise is
/// kept; every show verifies the band under UIAccess and self-heals only when it reads exactly 1
/// (ZBID_DESKTOP), once, with the same flags, and asks for the order repair when a non-ring window
/// took the fallback.
///
/// <para>
/// The creation gates use <c>EnsureHandle</c> only (nothing shown). The show/fallback gates use the
/// established cloaked recipe (<see cref="InvisibleTestWindow.PrepareCloaked"/>): DWM never
/// composes them and they never take activation, so nothing appears on the owner's screen. The
/// backdrop case is the one window that is positioned off-screen through its own production seam
/// (<see cref="InvisibleTestWindow.PrepareOffScreen"/>).
/// </para>
///
/// <para>
/// Mutants for this fixup (each build every test project, then RED): no creation raise; RM9, a
/// raise on every <c>IsVisibleChanged</c>; the fallback without the UIAccess gate; the fallback on
/// band 2; and no <see cref="IOverlayManager.ReassertOverlayOrder"/> after a non-ring fallback.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class UiAccessBandTests
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const long HwndTopmost = -1;

    private const long GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    // ── Creation raise (kept from the first PR) ───────────────────────────────────────────────

    /// <summary>
    /// Every overlay the A2 evidence found in band 1, plus the band-2 strip and the three note
    /// windows: each one raises itself into the UIAccess band exactly once, when its HWND is
    /// created, and a repeat of the one-time path cannot raise again.
    /// </summary>
    [StaFact]
    public void EveryOverlay_RaisesIntoTheUiAccessBand_ExactlyOnce_OnHandleCreation()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        foreach (var (name, window) in CreateOverlays())
        {
            try
            {
                var calls = new List<RaiseCall>();
                window.SetWindowPosForBand =
                    (hwnd, insertAfter, flags) => calls.Add(new RaiseCall(hwnd, insertAfter, flags));

                var hwnd = new WindowInteropHelper(window).EnsureHandle();

                Check.WithCustomMessage(
                        $"{name}: the band raise must run as soon as its HWND exists (one per window lifetime)")
                    .That(calls).HasSize(1);
                AssertBandRaise(name, calls[0], hwnd);
                Check.WithCustomMessage($"{name}: one raise, not one per show").That(window.BandRaiseCount).IsEqualTo(1);

                window.RaiseIntoUiAccessBandOnce();
                Check.WithCustomMessage($"{name}: the one-time band raise must be idempotent").That(calls).HasSize(1);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>Sticky L1: a note really created by the manager (its EnsureHandle path) raises once.</summary>
    [StaFact]
    public void ANoteWindow_RaisesOnce_WhenItsHandleIsCreated_UnderTheHeadlessNoteSeam()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        Check.WithCustomMessage("the assembly default must keep real note windows off")
            .That(NoteManager.RealNoteWindows).IsFalse();

        var note = InvisibleTestWindow.Prepare(new NoteWindow("c9f2-note"));
        try
        {
            var calls = new List<RaiseCall>();
            note.SetWindowPosForBand = (hwnd, insertAfter, flags) => calls.Add(new RaiseCall(hwnd, insertAfter, flags));
            var hwnd = new WindowInteropHelper(note).EnsureHandle();

            Check.That(calls).HasSize(1);
            AssertBandRaise(nameof(NoteWindow), calls[0], hwnd);
            Check.That(note.BandRaiseCount).IsEqualTo(1);
        }
        finally
        {
            note.Close();
        }
    }

    // ── No position path may read or raise ────────────────────────────────────────────────────

    /// <summary>
    /// Ring team constraint 1: the ring moves on every gaze frame and a topmost re-insert from a
    /// position update is the stutter / z-order-thrash bug. The fixup's show check must also never
    /// run from a position write: no band read, no raise.
    /// </summary>
    [StaFact]
    public void PositionUpdates_NeverReadOrRaiseTheBand()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var ring = InvisibleTestWindow.Prepare(new CursorCircleWindow());
        var indicator = InvisibleTestWindow.Prepare(new ManualSelectionGazeIndicatorWindow());
        var reads = 0;
        try
        {
            var calls = new List<RaiseCall>();
            ring.SetWindowPosForBand = (hwnd, insertAfter, flags) => calls.Add(new RaiseCall(hwnd, insertAfter, flags));
            indicator.SetWindowPosForBand =
                (hwnd, insertAfter, flags) => calls.Add(new RaiseCall(hwnd, insertAfter, flags));
            ring.UiAccessEnabled = () => true;
            indicator.UiAccessEnabled = () => true;
            ring.ReadBandForTest = _ => { reads++; return 2; };
            indicator.ReadBandForTest = _ => { reads++; return 2; };

            _ = new WindowInteropHelper(ring).EnsureHandle();
            _ = new WindowInteropHelper(indicator).EnsureHandle();
            Check.That(calls).HasSize(2); // one per window, at HWND creation

            for (var i = 0; i < 120; i++)
            {
                ring.UpdatePosition(new PhysicalPoint(100 + i * 3, 200 + i * 2));
                indicator.UpdatePosition(400 + i, 500 + i);
            }

            Check.WithCustomMessage(
                    "a position update read or raised the band: the ring does this on every gaze frame, "
                    + "which is exactly the stutter/z-order-thrash bug the ring constraint forbids")
                .That(reads).IsEqualTo(0);
            Check.That(calls).HasSize(2);

            // Teeth: an explicit ordering raise is visible to the same seam.
            ring.RaiseToTop();
            Check.That(calls).HasSize(3);
        }
        finally
        {
            ring.Close();
            indicator.Close();
        }
    }

    // ── The show-time check ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fixup F1: the trigger is the hidden→visible edge (<c>IsVisibleChanged</c>), not a
    /// <c>WM_WINDOWPOSCHANGED</c> hook, and the one post runs at Loaded priority so the read sees
    /// the final post-ShowWindow state. UIAccess is off here, so the post itself does nothing.
    /// </summary>
    [StaFact]
    public void TheShowTimeCheck_IsScheduledFromVisibilityChange_AtLoadedPriority()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new EdgeButtonBarWindow();
        InvisibleTestWindow.PrepareCloaked(window);
        try
        {
            window.UiAccessEnabled = () => false;
            window.Show();

            var pending = window.PendingBandCheckForTest;
            Check.WithCustomMessage("Show() did not schedule the show-time band check")
                .That(pending).IsNotNull();
            Check.WithCustomMessage($"the check must run at Loaded, not {pending!.Priority}")
                .That(pending.Priority).IsEqualTo(DispatcherPriority.Loaded);
            Check.That(pending.Status).IsEqualTo(DispatcherOperationStatus.Pending);

            Pump();
            Check.WithCustomMessage("the check must clear its pending slot when it runs")
                .That(window.PendingBandCheckForTest).IsNull();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The band line is a show-time line, on change only, and an ordinary window (the Settings
    /// shape: <c>EntersUiAccessBand=false</c>) writes it without ever raising (review F4).
    /// </summary>
    [StaFact]
    public void TheBandLine_IsWrittenOnShow_OnChangeOnly_AndOrdinaryWindowsNeverRaise()
    {
        var window = new OrdinaryProbeWindow();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        var band = 2u;
        window.UiAccessEnabled = () => true;
        window.ReadBandForTest = _ => band;
        InvisibleTestWindow.PrepareCloaked(window);
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();

            window.Show();
            Pump();
            Check.WithCustomMessage($"the first show must write one line; saw: {Describe(sink)}")
                .That(BandLines(sink)).HasSize(1);

            window.Hide();
            window.Show();
            Pump();
            Check.WithCustomMessage("an unchanged band on a re-show must not write another line")
                .That(BandLines(sink)).HasSize(1);

            band = 1;
            window.Hide();
            window.Show();
            Pump();
            Check.WithCustomMessage("a changed band must write a new line")
                .That(BandLines(sink)).HasSize(2);
            Check.WithCustomMessage($"the changed line must carry band 1: {BandLines(sink)[1]}")
                .That(BandLines(sink)[1]).Contains("band=");

            Check.WithCustomMessage("an ordinary window must never be raised or fall back")
                .That(window.BandRaiseCount).IsEqualTo(0);
            Check.That(BandLines(sink).Any(m => m.Contains("fallback re-raise"))).IsFalse();
        }
        finally
        {
            Log.Logger = previousLogger;
            window.Close();
        }
    }

    /// <summary>Review F2: UIAccess false ⇒ no band read and no raise at all (dev/test/CI unchanged).</summary>
    [StaFact]
    public void WithoutUiAccess_TheShowCheckReadsNothingAndRaisesNothing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new EdgeButtonBarWindow();
        var reads = 0;
        window.UiAccessEnabled = () => false;
        window.ReadBandForTest = _ => { reads++; return 1; };
        InvisibleTestWindow.PrepareCloaked(window); // the creation raise runs (1); the show check must not
        try
        {
            var raisesAtCreation = window.BandRaiseCount;
            window.Show();
            Pump();

            Check.WithCustomMessage("UIAccess=false must skip the band read entirely").That(reads).IsEqualTo(0);
            Check.That(window.BandRaiseCount).IsEqualTo(raisesAtCreation);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Band 2 is the healthy state: read it, log the first show, raise nothing.</summary>
    [StaFact]
    public void BandTwo_IsHealthy_NoFallback()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var rig = FallbackRig.Create(InvisibleTestWindow.Prepare(new ToolbarWindow()), 2);

        rig.Window.Show();
        Pump();

        Check.That(rig.Calls).HasSize(1); // creation only
        Check.That(rig.Repairs).IsEmpty();
        Check.That(rig.Reads).IsEqualTo(1);
    }

    /// <summary>An unknown read is not evidence of demotion: no raise, no repair.</summary>
    [StaFact]
    public void AnUnknownBand_IsNotActedOn()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var rig = FallbackRig.Create(InvisibleTestWindow.Prepare(new ToolbarWindow()), (uint?)null);

        rig.Window.Show();
        Pump();

        Check.That(rig.Calls).HasSize(1); // creation only
        Check.That(rig.Repairs).IsEmpty();
    }

    // ── The fallback ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UIAccess=true + band 1 ⇒ exactly one fallback re-raise with the exact flags, and one order
    /// repair for a non-ring window. The probe is a toolbar, which is not in the repair list, so
    /// the fallback's own count stays one.
    /// </summary>
    [StaFact]
    public void ADemotedNonRingWindow_FallsBackOnce_AndRepairsTheOrderOnce()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var rig = FallbackRig.Create(InvisibleTestWindow.Prepare(new ToolbarWindow()), 1, 2);

        rig.Window.Show();
        Pump();

        Check.WithCustomMessage($"the demoted show must read before and after: {rig.Reads}").That(rig.Reads).IsEqualTo(2);
        Check.WithCustomMessage("one creation raise plus one fallback re-raise").That(rig.Calls).HasSize(2);
        AssertBandRaise(nameof(ToolbarWindow) + " fallback", rig.Calls[1], new WindowInteropHelper(rig.Window).Handle);
        Check.WithCustomMessage("a non-ring fallback must repair the visible order exactly once")
            .That(rig.Repairs).HasSize(1);
        Check.That(rig.Window.BandRaiseCount).IsEqualTo(2);
    }

    /// <summary>Ring-team (b): a ring fallback re-raises once and needs no order repair.</summary>
    [StaFact]
    public void ADemotedRing_FallsBackOnce_WithoutRepairingTheOrder()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var rig = FallbackRig.Create(InvisibleTestWindow.Prepare(new CursorCircleWindow()), 1, 2);

        rig.Window.Show();
        Pump();

        Check.That(rig.Calls).HasSize(2); // creation + fallback
        Check.WithCustomMessage("the ring must not trigger the order repair").That(rig.Repairs).IsEmpty();
        Check.That(rig.Window.BandRaiseCount).IsEqualTo(2);
    }

    /// <summary>
    /// A fallback that fails to reach band 2 logs once and gives up for that HWND: later shows may
    /// read the band (for the change-only line) but never raise again.
    /// </summary>
    [StaFact]
    public void AFailedFallback_LogsOnce_AndGivesUpForTheHwnd()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        using var rig = FallbackRig.Create(InvisibleTestWindow.Prepare(new ToolbarWindow()), 1);
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();

            rig.Window.Show();
            Pump();
            Check.That(rig.Calls).HasSize(2); // creation + the one failed fallback
            Check.That(BandLines(sink).Count(m => m.Contains("did not reach band 2"))).IsEqualTo(1);

            rig.Window.Hide();
            rig.Window.Show();
            Pump();
            Check.WithCustomMessage("a later show must not retry the fallback for the same HWND")
                .That(rig.Calls).HasSize(2);
            Check.That(BandLines(sink).Count(m => m.Contains("did not reach band 2"))).IsEqualTo(1);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    /// <summary>Ring-team F2 / RM9: Show/Hide ×3 with a healthy band raises only the creation raise.</summary>
    [StaFact]
    public void TheRing_ShowHideThreeTimes_RaisesOnlyOnce()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var ring = new CursorCircleWindow();
        var calls = new List<RaiseCall>();
        var reads = 0;
        ring.SetWindowPosForBand = (hwnd, insertAfter, flags) => calls.Add(new RaiseCall(hwnd, insertAfter, flags));
        ring.UiAccessEnabled = () => true;
        ring.ReadBandForTest = _ => { reads++; return 2; };
        InvisibleTestWindow.PrepareCloaked(ring);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                ring.Show();
                Pump();
                ring.Hide();
                Pump();
            }

            Check.WithCustomMessage(
                    "a raise on every show is the surviving RM9 mutant: the ring must keep its one creation raise")
                .That(calls).HasSize(1);
            Check.WithCustomMessage("one band read per show, and never a raise").That(reads).IsEqualTo(3);
            Check.That(ring.BandRaiseCount).IsEqualTo(1);
        }
        finally
        {
            ring.Close();
        }
    }

    // ── Order repair: the backdrop demoted under the magnifier and menu ──────────────────────

    /// <summary>
    /// The production show order from <c>OverlayVmBindingCoordinator</c>: the backdrop appears
    /// FIRST, the magnifier (manual selection) and the action menu are shown after it. When the
    /// backdrop is the demoted window, its fallback re-raise would put it above both — the repair
    /// must put them back on top. This mirrors the EdgeButtonBarZOrderTests scenario with the real
    /// magnifier/menu windows, which that rig replaces by no-ops, and asserts the property that
    /// rig cannot: magnifier and menu end above the backdrop.
    /// </summary>
    [StaFact]
    public void ADemotedBackdrop_IsRepairedUnderTheMagnifierAndMenu()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var backdrop = InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow());
        var magnifier = new ManualSelectionOverlayWindow
        {
            Capture = (_, _) => null,
            ScreenSize = () => (1920, 1080),
        };
        var menu = new ActionMenuWindow { PreviewCapture = (_, _) => null };
        var bar = new EdgeButtonBarWindow();
        var indicator = new ManualSelectionGazeIndicatorWindow();
        var strip = new PauseIndicatorWindow();

        InvisibleTestWindow.PrepareCloaked(magnifier);
        InvisibleTestWindow.PrepareCloaked(menu);
        InvisibleTestWindow.PrepareCloaked(bar);
        InvisibleTestWindow.PrepareCloaked(indicator);
        InvisibleTestWindow.PrepareCloaked(strip);

        foreach (var window in new OverlayWindow[] { magnifier, menu, bar, indicator, strip })
        {
            window.UiAccessEnabled = () => true;
            window.ReadBandForTest = _ => 2;
        }
        backdrop.UiAccessEnabled = () => true;

        var repairs = 0;
        var previousRepair = OverlayWindow.ProcessReassertOverlayOrder;
        OverlayWindow.ProcessReassertOverlayOrder = () =>
        {
            repairs++;
            RaiseIfVisible(magnifier);
            RaiseIfVisible(menu);
            RaiseIfVisible(bar);
            RaiseIfVisible(indicator);
            RaiseIfVisible(strip);
        };

        try
        {
            // The backdrop is first, like the coordinator's manual-selection/action-menu paths.
            backdrop.ShowBackdrop(
                new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.3 },
                new PhysicalPoint(100, 100));
            var backdropHwnd = new WindowInteropHelper(backdrop).Handle;
            backdrop.ReadBandForTest = hwnd => hwnd == backdropHwnd ? 1u : 2u;

            // Then the magnifier (manual selection) and the action menu, the real windows the
            // EdgeButtonBar rig replaces with no-ops.
            magnifier.Show(new PhysicalPoint(900, 500), radius: 100, magnification: 2.0);
            menu.Show(
                new PhysicalPoint(900, 500), new List<InteractionAction>(),
                showBackButton: false, dwellTimeMs: 600, ringRadius: 100, previewCircleSize: 80,
                borderColor: "#FFFFFF", positionIndicatorStyle: PositionIndicatorStyle.Crosshair);

            Pump();

            Check.WithCustomMessage($"the backdrop must have taken the fallback; repairs={repairs}")
                .That(repairs).IsEqualTo(1);
            Check.WithCustomMessage(
                    $"magnifier Z={ZIndexOf(magnifier)}, backdrop Z={ZIndexOf(backdrop)} (lower is in front)")
                .That(ZIndexOf(magnifier)).IsStrictlyLessThan(ZIndexOf(backdrop));
            Check.WithCustomMessage(
                    $"menu Z={ZIndexOf(menu)}, backdrop Z={ZIndexOf(backdrop)} (lower is in front)")
                .That(ZIndexOf(menu)).IsStrictlyLessThan(ZIndexOf(backdrop));
            Check.WithCustomMessage("the backdrop's fallback re-raise must be the second raise")
                .That(backdrop.BandRaiseCount).IsEqualTo(2);
        }
        finally
        {
            OverlayWindow.ProcessReassertOverlayOrder = previousRepair;
            strip.Close();
            indicator.Close();
            bar.Close();
            menu.Close();
            magnifier.Close();
            backdrop.Close();
        }
    }

    // ── Guard rails ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Style constraint: the real fallback/raise must not alter the overlay's extended styles (the
    /// native call is NOT stubbed here — review F2's vacuous-test fix).
    /// </summary>
    [StaFact]
    public void TheBandRaise_LeavesTheExtendedWindowStylesUnchanged()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = InvisibleTestWindow.Prepare(new ToolbarWindow());
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            var before = (long)GetWindowLong(hwnd, (int)GwlExStyle);
            window.RaiseToTop();
            var after = (long)GetWindowLong(hwnd, (int)GwlExStyle);

            Check.WithCustomMessage($"styles changed from 0x{before:X} to 0x{after:X}")
                .That(after).IsEqualTo(before);
            Check.WithCustomMessage("the overlay must stay WS_EX_NOACTIVATE").That(after & WsExNoActivate).IsNotEqualTo(0);
            Check.WithCustomMessage("the overlay must stay WS_EX_TOOLWINDOW").That(after & WsExToolWindow).IsNotEqualTo(0);
            Check.WithCustomMessage("the overlay must stay WS_EX_TOPMOST").That(after & WsExTopmost).IsNotEqualTo(0);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Review F3: a reflection scan over the whole App assembly, not eight hand-listed types and a
    /// source string — only SettingsWindow may opt out of the automatic band raise.
    /// </summary>
    [Fact]
    public void OnlyTheSettingsWindow_OverridesTheBandOptOut()
    {
        var overriders = typeof(OverlayWindow).Assembly.GetTypes()
            .Where(t => t != typeof(OverlayWindow) && typeof(OverlayWindow).IsAssignableFrom(t))
            .Where(t => t.GetMethod(
                "get_EntersUiAccessBand",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null)
            .Select(t => t.FullName)
            .ToList();

        Check.WithCustomMessage(
                "Only SettingsWindow may override EntersUiAccessBand; these also do: "
                + string.Join(", ", overriders))
            .That(overriders).HasSize(1);
        Check.That(overriders[0]).IsEqualTo(typeof(SettingsWindow).FullName);
    }

    /// <summary>The banner slot re-raises only its LIVE occupant (the repair's banner/notices end).</summary>
    [Fact]
    public void TheBannerSlot_ReassertsOnlyItsLiveOccupant()
    {
        var slot = new BannerSlot();
        var first = new RecordingOccupant();
        var second = new RecordingOccupant();

        slot.Enqueue(first);
        slot.ReassertForOrder();

        Check.That(first.Reasserts).IsEqualTo(1);
        Check.That(second.Reasserts).IsEqualTo(0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static void RaiseIfVisible(OverlayWindow window)
    {
        if (window.IsVisible)
            window.RaiseToTop();
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static List<string> BandLines(CapturingSink sink) =>
        sink.Messages.Where(m => m.StartsWith("Overlay band: ", StringComparison.Ordinal)).ToList();

    private static string Describe(CapturingSink sink) => string.Join(" | ", sink.Messages);

    private static void AssertBandRaise(string name, RaiseCall call, IntPtr hwnd)
    {
        Check.WithCustomMessage($"{name}: raised a different HWND").That(call.Hwnd).IsEqualTo(hwnd);
        Check.WithCustomMessage($"{name}: the raise must insert at HWND_TOPMOST")
            .That(call.InsertAfter.ToInt64()).IsEqualTo(HwndTopmost);
        Check.WithCustomMessage(
                $"{name}: flags must be SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE — no activation, and no "
                + "position change in the raise")
            .That(call.Flags).IsEqualTo(SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// The band-1 overlays (A2 evidence), the three note windows (sticky L1) and the pause strip,
    /// which already had an explicit raise and must still get the base's one raise.
    /// </summary>
    private static IEnumerable<(string Name, OverlayWindow Window)> CreateOverlays()
    {
        yield return (nameof(ToolbarWindow), InvisibleTestWindow.Prepare(new ToolbarWindow()));
        yield return (nameof(CursorCircleWindow), InvisibleTestWindow.Prepare(new CursorCircleWindow()));
        yield return (nameof(ManualSelectionOverlayWindow),
            InvisibleTestWindow.Prepare(new ManualSelectionOverlayWindow()));
        yield return (nameof(ActionMenuWindow), InvisibleTestWindow.Prepare(new ActionMenuWindow()));
        yield return (nameof(KeyboardWindow), InvisibleTestWindow.Prepare(new KeyboardWindow()));
        yield return (nameof(ActivationButtonWindow), InvisibleTestWindow.Prepare(new ActivationButtonWindow()));
        yield return (nameof(GlassBackdropWindow),
            InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow()));
        yield return (nameof(PauseIndicatorWindow), InvisibleTestWindow.Prepare(new PauseIndicatorWindow()));
        yield return (nameof(NoteWindow), InvisibleTestWindow.Prepare(new NoteWindow("c9f2-list")));
        yield return (nameof(NoteNoticeWindow), InvisibleTestWindow.Prepare(new NoteNoticeWindow()));
        yield return (nameof(NotePlaceHereWindow), InvisibleTestWindow.Prepare(new NotePlaceHereWindow(24)));
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    /// <summary>The window's position in the desktop Z-order: 0 is frontmost.</summary>
    private static int ZIndexOf(System.Windows.Window window)
    {
        var target = new WindowInteropHelper(window).Handle;
        if (target == IntPtr.Zero) return int.MaxValue;

        var found = int.MaxValue;
        var index = 0;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == target) { found = index; return false; }
            index++;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private readonly record struct RaiseCall(IntPtr Hwnd, IntPtr InsertAfter, uint Flags);

    /// <summary>
    /// A cloaked probe window with the fixed seams for the fallback gates: a recorder for the
    /// native raise, a scripted band reader (one value per read, the last value repeats), and a
    /// recording process-wide order repair restored on dispose. The creation raise runs through the
    /// same recorder, so <see cref="Calls"/> always starts with it.
    /// </summary>
    private sealed class FallbackRig : IDisposable
    {
        private readonly uint?[] _bands;
        private readonly Action? _previousRepair;
        private int _bandIndex;

        private FallbackRig(OverlayWindow window, uint?[] bands)
        {
            Window = window;
            _bands = bands;
            Calls = new List<RaiseCall>();
            Repairs = new List<string>();

            window.SetWindowPosForBand =
                (hwnd, insertAfter, flags) => Calls.Add(new RaiseCall(hwnd, insertAfter, flags));
            window.UiAccessEnabled = () => true;
            window.ReadBandForTest = _ =>
            {
                Reads++;
                var index = Math.Min(_bandIndex++, _bands.Length - 1);
                return _bands[index];
            };

            InvisibleTestWindow.PrepareCloaked(window);

            _previousRepair = OverlayWindow.ProcessReassertOverlayOrder;
            OverlayWindow.ProcessReassertOverlayOrder = () => Repairs.Add("repair");
        }

        public static FallbackRig Create(OverlayWindow window, params uint?[] bands)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            return new FallbackRig(window, bands);
        }

        public OverlayWindow Window { get; }

        public List<RaiseCall> Calls { get; }

        public List<string> Repairs { get; }

        public int Reads { get; private set; }

        public void Dispose()
        {
            OverlayWindow.ProcessReassertOverlayOrder = _previousRepair;
            Window.Close();
        }
    }

    /// <summary>A window that opts out like Settings does: band line only, never raised.</summary>
    private sealed class OrdinaryProbeWindow : OverlayWindow
    {
        protected override bool EntersUiAccessBand => false;
    }

    private sealed class RecordingOccupant : IBannerSlotOccupant
    {
        public int Reasserts { get; private set; }

        public event EventHandler? Closed;

        public void ShowInSlot() { }

        public void HideFromSlot() => Closed?.Invoke(this, EventArgs.Empty);

        public bool TryGetVisibleBounds(out PhysicalRect bounds)
        {
            bounds = default;
            return false;
        }

        public void ReassertForOrder() => Reasserts++;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();

        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
