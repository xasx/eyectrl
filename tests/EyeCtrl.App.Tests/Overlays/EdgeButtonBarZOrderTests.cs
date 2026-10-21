namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The right-edge Close and Recapture buttons are two of the three gaze-driven ways out of the
/// manual-selection magnifier, so "the bar exists and holds the right buttons" is not the property
/// that matters — "the user can SEE it" is. Those are different properties, and everything this
/// repo asserted until now was the first one.
///
/// <para>
/// The defect this pins: <c>EdgeButtonBarViewModel.ShowButtons</c> ends with
/// <c>IsVisible = true</c>, and <c>OverlayManager</c> calls <c>Window.Show()</c> only from the
/// <c>PropertyChanged</c> handler for that property. Returning from the action menu to the
/// magnifier re-shows the bar while it is ALREADY up (the ring parks its own Close/Help on the
/// same shared bar), so the write is equal-value, <c>[ObservableProperty]</c> short-circuits, and
/// the window is never re-shown. In the same notification, the sibling handler subscribed one line
/// earlier (<c>OverlayVmBindingCoordinator</c> :49 before :55) has already raised the fullscreen
/// glass backdrop over it — leaving both exits rendered, hit-testable for dwell, and invisible
/// underneath an acrylic sheet.
/// </para>
///
/// <para>
/// A <c>Show()</c> would not have saved it either: <c>OverlayWindow.RaiseToTop</c>'s own doc
/// comment already records that "a second Show() is a no-op that would not re-raise it". That
/// comment was written FOR the gaze indicator and never generalised to the bar, which is the whole
/// bug in one sentence.
/// </para>
///
/// <para>
/// This fails on <c>main</c> too, deliberately. The defect predates the manual-selection feature
/// branches; what those branches did was delete the <c>HideBackdrop()</c> call in the recapture
/// path that had been accidentally UN-masking it. A test that only failed on the merge would pin
/// the un-masking rather than the cause.
/// </para>
///
/// <para>
/// Reads the real Win32 Z-order through real HWNDs rather than <c>Window.IsVisible</c>, because
/// <c>IsVisible</c> is <c>true</c> throughout the failure — that is exactly why every existing
/// test stayed green. Needs the token dictionary (the windows resolve <c>StaticResource</c> at
/// construction), hence the gate and <c>WpfApplicationCollection</c>; it never writes
/// <c>DpiHelper</c>, so it stays out of the exclusive DpiHelper collection and is scale-agnostic —
/// only the RELATIVE order of three of our own windows is asserted.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class EdgeButtonBarZOrderTests
{
    [StaFact]
    public void ReturningFromTheActionMenu_LeavesTheEdgeBarInFrontOfTheBackdrop()
    {
        var z = RunBackFromActionMenu();

        // Lower index == closer to the front. The bar carries the user's exits; the backdrop is
        // decoration, and decoration must never be in front of an escape hatch.
        Check.WithCustomMessage($"edge bar Z={z.Bar}, backdrop Z={z.Backdrop} (lower index = in front)")
             .That(z.Bar).IsStrictlyLessThan(z.Backdrop);
    }

    [StaFact]
    public void ReturningFromTheActionMenu_LeavesTheGazeIndicatorInFrontOfTheEdgeBar()
    {
        var z = RunBackFromActionMenu();

        // The fix for the assertion above raises the bar to the top of the top-most band, which
        // would otherwise put an opaque 100 DIP button over the live gaze dot — the dot is drawn in
        // a window spanning the whole hold radius around the gaze, so it overlaps any button the
        // user looks at. Taking "where am I looking" away at the moment they aim at their way out
        // is the worst place in the app to lose it, so the indicator is re-raised last and this
        // pins that ordering against a later change that raises the bar and forgets the dot.
        Check.WithCustomMessage($"gaze indicator Z={z.Indicator}, edge bar Z={z.Bar} (lower index = in front)")
             .That(z.Indicator).IsStrictlyLessThan(z.Bar);
    }

    /// <summary>
    /// Drives the real state machine and both real coordinators through the sequence that
    /// reproduces it: fixate, dwell the activation button into manual selection, dwell inside the
    /// magnifier to open the ring, then take the ring's Back step to return to the magnifier. The
    /// bar is already showing at that last step, which is the precondition for the defect.
    /// </summary>
    private static (int Bar, int Backdrop, int Indicator) RunBackFromActionMenu()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var dispatcher = Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();

        using var engine = new GazeEngine(
            new TestFakes.FakeGazeProvider(), new TestFakes.FakeInputSimulator(), settings);
        var im = engine.InteractionManager;

        // The real adapter, so the Core events reach the messenger through production's own
        // dispatcher marshalling. Hand-rolling that wrapping here would replicate the wiring under
        // test, and the ordering between HideActionMenu and RestoreManualSelectionView is precisely
        // what a reader would want this test to be honest about.
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, dispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());

        var barVm = new EdgeButtonBarViewModel(settings, messenger);
        var barWindow = InvisibleTestWindow.Prepare(new EdgeButtonBarWindow { DataContext = barVm });
        var backdrop = new GlassBackdropWindow();
        var indicator = new ManualSelectionGazeIndicatorWindow();
        indicator.Configure(
            settings.ManualSelection.GazeIndicatorOuterDiameter,
            settings.ManualSelection.GazeIndicatorCenterDotDiameter,
            settings.ManualSelection.GazeIndicatorStrokeThickness,
            settings.ManualSelection.GazeIndicatorOpacity,
            settings.ManualSelection.HoldRadiusLogical);

        // OverlayManager.Initialize's edge-bar wiring, verbatim and in its subscription order —
        // ButtonSetChanged before PropertyChanged. The order matters: the rebuild runs after the
        // show, which is how a Show() that never happens leaves a correctly rebuilt bar underneath.
        barVm.ButtonSetChanged += () => barWindow.SyncButtons();
        barVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EdgeButtonBarViewModel.IsVisible))
            {
                if (barVm.IsVisible) barWindow.Show();
                else ((Window)barWindow).Hide();
            }
        };

        var modal = new ModalInteractionState();
        var vmBinding = new OverlayVmBindingCoordinator(modal);
        var messageCoordinator = new OverlayMessageCoordinator(
            new ToolbarLifecycleCoordinator(modal), vmBinding);

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            InteractionManager = im,
            GazeEngine = engine,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            ManualSelectionVM = new ManualSelectionOverlayViewModel(settings, messenger),
            ManualSelectionIndicatorVM = new ManualSelectionGazeIndicatorViewModel(messenger),
            CursorVM = new CursorCircleViewModel(settings, messenger),
            ActivationVM = new ActivationButtonViewModel(messenger),
            ActionMenuVM = new ActionMenuViewModel(settings, messenger),
            ToolbarVM = new ToolbarViewModel(settings, messenger),
            BackdropVM = new GlassBackdropViewModel(settings, messenger),
            TaskSwitcherVM = new TaskSwitcherViewModel(windowManager: null!, settings, messenger),
            TargetHighlightVM = new TargetHighlightOverlayViewModel(),
            OverlayManager = new ZOrderOverlays(barWindow, backdrop, indicator),
        };

        try
        {
            messageCoordinator.Initialize(ctx);
            vmBinding.Initialize(ctx);

            im.SelectionMode = EyeCtrl.Core.Models.SelectionMode.ManualSelection;
            im.OnFixationStarted(new PhysicalPoint(300, 400));
            Pump(dispatcher);

            im.OnActivationButtonDwelled();
            Pump(dispatcher);

            im.OnManualSelectionDwell(new PhysicalPoint(310, 410));
            Pump(dispatcher);

            im.OnManualSelectionBack();
            Pump(dispatcher);

            return (ZIndexOf(barWindow), ZIndexOf(backdrop), ZIndexOf(indicator));
        }
        finally
        {
            messageCoordinator.Dispose();
            vmBinding.Dispose();
            indicator.Close();
            backdrop.Close();
            barWindow.Close();
        }
    }

    /// <summary>Runs everything queued at or above Background priority, draining the marshalled work.</summary>
    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    /// <summary>
    /// The window's position in the desktop Z-order: 0 is frontmost. <c>EnumWindows</c> walks
    /// top-level windows front-to-back, so a smaller index means "drawn over". Only differences
    /// between our own three windows are ever compared, so unrelated windows interleaving is
    /// harmless. Returns <see cref="int.MaxValue"/> for a window with no HWND, which fails any
    /// "is in front of" assertion rather than passing it by accident.
    /// </summary>
    private static int ZIndexOf(Window window)
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

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }

    /// <summary>
    /// The three windows whose stacking this test is about are real; everything the manual-selection
    /// path touches beyond them is a no-op, and everything it does not touch throws (the discipline
    /// <c>TestFakes.PartialOverlayManager</c> and <c>ManualSelectionRecaptureTests.FakeOverlays</c>
    /// both use, so a coordinator reaching further fails loudly instead of silently defaulting).
    /// </summary>
    private sealed class ZOrderOverlays : IOverlayManager
    {
        internal ZOrderOverlays(IEdgeButtonBar bar, IGlassBackdropOverlay backdrop,
            IManualSelectionIndicatorOverlay indicator)
        {
            EdgeButtonBar = bar;
            GlassBackdrop = backdrop;
            ManualSelectionIndicator = indicator;
        }

        public IEdgeButtonBar EdgeButtonBar { get; }
        public IGlassBackdropOverlay GlassBackdrop { get; }
        public IManualSelectionIndicatorOverlay ManualSelectionIndicator { get; }

        public IManualSelectionOverlay ManualSelectionOverlay { get; } = new NoOpMagnifier();
        public ICursorCircleOverlay CursorCircle { get; } = new TestFakes.FakeCursorCircleOverlay();
        public IActivationButtonOverlay ActivationButton { get; } = new TestFakes.FakeActivationButtonOverlay();
        public IActionMenuOverlay ActionMenu { get; } = new NoOpActionMenu();

        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public IModesMenuOverlay ModesMenu => throw new NotImplementedException();
        public IRepeatCursorOverlay RepeatCursor => throw new NotImplementedException();
        public IToolbarOverlay Toolbar => throw new NotImplementedException();
        public IPauseIndicatorOverlay PauseIndicator => throw new NotImplementedException();
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();

        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings,
            GazeProviderType providerType) => throw new NotImplementedException();

        /// <summary>
        /// The magnifier takes a real screenshot on Show, which this test has no use for and which
        /// would put GDI capture on the critical path of a Z-order assertion.
        /// </summary>
        private sealed class NoOpMagnifier : IManualSelectionOverlay
        {
            public void Show(PhysicalPoint center, int radius, double magnification,
                PhysicalPoint? gazePoint = null, bool recapture = true) { }

            public void UpdateCenter(PhysicalPoint center, int radius, double magnification,
                PhysicalPoint? gazePoint = null, bool recapture = true) { }

            public void Hide() { }

            public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen) => gazeOnScreen;

            public PhysicalPoint EffectiveCaptureCenter =>
                throw new NotImplementedException("Z-order tests never map a point through the capture");
        }

        private sealed class NoOpActionMenu : IActionMenuOverlay
        {
            public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton,
                int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor,
                PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null,
                int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false, double previewMagnification = 1.0) { }

            public void Hide() { }
        }
    }
}
