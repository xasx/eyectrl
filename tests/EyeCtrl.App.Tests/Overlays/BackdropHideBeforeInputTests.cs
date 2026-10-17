namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// The clicking investigation's R1 fix, pinned off-screen: a menu action and the drag's MouseDown
/// inject REAL OS input, and the backdrop is the one overlay that is not layered — WS_EX_TRANSPARENT
/// and HTTRANSPARENT do not make it click-through for another process, so input injected while it is
/// still visible lands on the backdrop itself. Since #214 its hide is deferred to
/// <see cref="DispatcherPriority.Background"/> (the magnifier → menu hand-off), which runs AFTER
/// every Normal item — i.e. after the click's two Normal posts — so the injection paths must
/// complete the pending hide first (<c>CompleteHideNow</c>).
///
/// <para>
/// These are the real windows shown OFF-SCREEN through <c>PositionForShow</c> (nothing is ever
/// painted on a visible screen), the real <see cref="GlassBackdropWindow"/>,
/// <see cref="KeyboardInteractionDispatcher"/>, <see cref="DragCoordinator"/> and
/// <see cref="OverlayVmBindingCoordinator"/> (its ActionMenu handler wired the way its own tests
/// wire it), the real production order of dispatcher posts, and a recording
/// <see cref="TestFakes.FakeInputSimulator"/> that reads the overlays' visibility AT the injection
/// call. Deliberately NOT <c>Category=UI</c>: CI runs them, and they need no interactive session.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class BackdropHideBeforeInputTests
{
    [StaTheory]
    [InlineData(InteractionAction.LeftClick)]
    [InlineData(InteractionAction.RightClick)]
    [InlineData(InteractionAction.DoubleClick)]
    [InlineData(InteractionAction.ScrollDown)]
    public void AMenuAction_InjectsOnlyAfterTheBackdropIsHidden(InteractionAction action)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0); // the user's scale; the assembly hook restores it
        GlassBackdropWindow? window = null;
        try
        {
            var harness = MenuHarness.Start();
            window = harness.Window;

            harness.Input.InjectionStateProbe =
                () => $"backdrop={harness.Window.IsVisible};menu={harness.MenuOverlay.IsVisible}";

            // Production's order (origin/main 86146fcb): InteractionManager.OnActionSelected invokes
            // HideActionMenu (the adapter posts the VM hide at Normal) and then ActionRequested (the
            // adapter posts the message; the dispatcher's own registration posts the handler). The
            // backdrop's hide is posted at Background by the menu hide — after every Normal item.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => harness.MenuVm.IsVisible = false));
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => harness.Messenger.Send(
                    new ActionRequestedMessage(action, new PhysicalPoint(400, 300), IsPreciseTarget: true))));

            PumpUntil(() => harness.Input.InjectionState is not null);

            Check.WithCustomMessage(
                    $"the injection ran with {harness.Input.InjectionState}; the backdrop must already be hidden "
                    + "(the deferred Background hide runs after every Normal item — this is not a race).")
                .That(harness.Input.InjectionState).IsEqualTo("backdrop=False;menu=False");
            Check.That(harness.Window.IsVisible).IsFalse();
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [StaFact]
    public void TheActionLog_CarriesTheInjectedInstantVisibility()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        GlassBackdropWindow? window = null;
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        try
        {
            var harness = MenuHarness.Start();
            window = harness.Window;

            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => harness.MenuVm.IsVisible = false));
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => harness.Messenger.Send(
                    new ActionRequestedMessage(InteractionAction.LeftClick, new PhysicalPoint(400, 300), IsPreciseTarget: true))));

            PumpUntil(() => sink.Messages.Any(m => m.StartsWith("Action: ", StringComparison.Ordinal)));

            var line = sink.Messages.FirstOrDefault(m => m.StartsWith("Action: ", StringComparison.Ordinal));
            Check.WithCustomMessage($"no Action log line was emitted; saw: {string.Join(" | ", sink.Messages)}")
                .That(line).IsNotNull();
            Check.WithCustomMessage(
                    $"E3(c): the Action line must keep its fields and append the visibility read after CompleteHideNow, saw: {line}")
                .That(line!.Contains("[PreciseTarget=True] [Modifiers=null] [BackdropVisible=False]", StringComparison.Ordinal))
                .IsTrue();
        }
        finally
        {
            Log.Logger = previousLogger;
            CloseQuietly(window);
        }
    }

    [StaFact]
    public void TheDragsMouseDown_SeesTheMenuAndBackdropHidden()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        GlassBackdropWindow? window = null;
        DragCoordinator? drag = null;
        try
        {
            // The drag is the pre-existing §4 shape: OnActionSelected invokes HideActionMenu
            // SYNCHRONOUSLY and then raises DragStarted, so at the grab point the posted hide has
            // not run — both the menu window and the backdrop are still up.
            var harness = MenuHarness.Start();
            window = harness.Window;
            var input = harness.Input;
            var ctx = harness.Context with
            {
                InteractionManager = new InteractionManager(),
                DragTrailVM = new DragTrailViewModel(harness.Settings, harness.Messenger),
            };
            harness.RewireOverlayManager(ctx);

            drag = new DragCoordinator();
            drag.Initialize(ctx);

            input.InjectionStateProbe =
                () => $"backdrop={window.IsVisible};menu={harness.MenuOverlay.IsVisible}";

            drag.OnDragStarted(new PhysicalPoint(300, 300));

            Check.WithCustomMessage(
                    $"the MouseDown ran with {input.InjectionState}; the menu and the backdrop must already be "
                    + "hidden (OnDragStarted completes the same chain the posted hide would have).")
                .That(input.InjectionState).IsEqualTo("backdrop=False;menu=False");
            Check.That(window.IsVisible).IsFalse();
            // The failure-free ordering must not leave the OS button pressed (the manager below has
            // no edge bar, so the drag SETUP fails after the press — the release path is the one the
            // sibling coordinator tests pin; here the point is what MouseDown saw).
            Check.That(input.ButtonsBalanced).IsTrue();
        }
        finally
        {
            drag?.Dispose();
            CloseQuietly(window);
        }
    }

    [StaFact]
    public void ABackdropShownWithNoPendingHide_SurvivesCompleteHideNow()
    {
        // The contract's other half: CompleteHideNow completes a DEFERRED hide and nothing else. A
        // backdrop that is legitimately on screen with no hide pending must stay on screen — this
        // line had NO holder before this ticket (the review's mutant (d) probe), and the injection
        // paths' call sites would otherwise be "hide the backdrop on every action", which is not
        // what they mean.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        GlassBackdropWindow? window = null;
        try
        {
            window = new GlassBackdropWindow();
            window.PositionForShow = OffScreenPositioning();
            window.ShowBackdrop(
                new BackdropSettings { Type = BackdropType.Glass, Enabled = true, Opacity = 0.3 },
                new PhysicalPoint(100, 100));
            Check.That(window.IsVisible).IsTrue();

            window.CompleteHideNow();   // no Hide() ever ran: nothing is pending

            Check.WithCustomMessage("CompleteHideNow must not hide a backdrop that has no pending hide.")
                .That(window.IsVisible).IsTrue();
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    /// <summary>
    /// One shared start-up: the real backdrop window shown OFF-SCREEN, a real
    /// <see cref="ActionMenuViewModel"/> (true = the menu is up), a recording menu overlay, and the
    /// REAL <see cref="OverlayVmBindingCoordinator"/> with only its ActionMenu handler wired — the
    /// same reflection shape <c>OverlayVmBindingCoordinatorTests</c> uses to skip the nine-VM
    /// Initialize.
    /// </summary>
    private sealed class MenuHarness
    {
        internal GlassBackdropWindow Window { get; private init; } = null!;
        internal TestFakes.FakeInputSimulator Input { get; private init; } = null!;
        internal ActionMenuViewModel MenuVm { get; private init; } = null!;
        internal RecordingActionMenuOverlay MenuOverlay { get; private init; } = null!;
        internal WeakReferenceMessenger Messenger { get; private init; } = null!;
        internal EyeCtrlSettings Settings { get; private init; } = null!;
        internal EventCoordinationContext Context { get; private set; } = null!;
        internal KeyboardInteractionDispatcher KeyDispatcher { get; private set; } = null!;

        internal static MenuHarness Start()
        {
            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();
            var input = new TestFakes.FakeInputSimulator();
            var menuOverlay = new RecordingActionMenuOverlay();
            var menuVm = new ActionMenuViewModel(settings, messenger);
            menuVm.IsVisible = true;   // before any listener is wired: the menu is up, nothing fires

            var window = new GlassBackdropWindow();
            window.PositionForShow = OffScreenPositioning();
            window.ShowBackdrop(
                new BackdropSettings { Type = BackdropType.Glass, Enabled = true, Opacity = 0.3 },
                new PhysicalPoint(100, 100));
            Check.WithCustomMessage("the harness precondition: the backdrop is on screen (off-screen).")
                .That(window.IsVisible).IsTrue();

            var harness = new MenuHarness
            {
                Window = window,
                Input = input,
                MenuVm = menuVm,
                MenuOverlay = menuOverlay,
                Messenger = messenger,
                Settings = settings,
            };
            harness.RewireOverlayManager(harness.CreateContext());
            WireActionMenuHideChain(harness);

            // The real registration seam: the dispatcher subscribes on the context's messenger and
            // its handler posts HandleActionRequested at Normal — the second of production's two
            // posts behind the hide's Background completion.
            harness.KeyDispatcher = new KeyboardInteractionDispatcher();
            harness.KeyDispatcher.Initialize(harness.Context);
            return harness;
        }

        private EventCoordinationContext CreateContext() =>
            TestFakes.MakeContext(Input) with
            {
                Messenger = Messenger,
                Settings = Settings,
                ActionMenuVM = MenuVm,
                TargetHighlightVM = new TargetHighlightOverlayViewModel(),
                Dispatcher = Dispatcher.CurrentDispatcher,
                OverlayManager = TestFakes.MakeOverlayManager(
                    cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                    activationButton: new TestFakes.FakeActivationButtonOverlay(),
                    actionMenu: MenuOverlay,
                    glassBackdrop: Window),
            };

        /// <summary>Replaces the context a drag gate builds by copying (records are immutable), so
        /// the same harness can feed both the dispatcher and the drag coordinator.</summary>
        internal void RewireOverlayManager(EventCoordinationContext ctx)
        {
            Context = ctx;
            typeof(CoordinatorBase)
                .GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_coordinator, ctx);
            typeof(OverlayVmBindingCoordinator)
                .GetField("_glassBackdrop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_coordinator, Window);
        }

        private readonly OverlayVmBindingCoordinator _coordinator = new(new ModalInteractionState());

        private static void WireActionMenuHideChain(MenuHarness harness)
        {
            var handler = (PropertyChangedEventHandler)typeof(OverlayVmBindingCoordinator)
                .GetMethod("OnActionMenuVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(PropertyChangedEventHandler), harness._coordinator);
            harness.MenuVm.PropertyChanged += handler;
        }
    }

    /// <summary>Records menu show/hide and mirrors the production menu window's visibility state.</summary>
    private sealed class RecordingActionMenuOverlay : IActionMenuOverlay
    {
        public bool IsVisible { get; private set; } = true;
        public int ShowCalls { get; private set; }

        public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton,
            int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor,
            PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null,
            int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false, double previewMagnification = 1.0)
        {
            ShowCalls++;
            IsVisible = true;
        }

        public void Hide() => IsVisible = false;
    }

    /// <summary>
    /// Production's <c>CoverPrimaryScreen</c> sizing, then OFF-SCREEN — with the proof run BEFORE
    /// the show: <c>ShowBackdrop</c> calls the positioner first and <c>base.Show()</c> last, so the
    /// assertion here runs while the window is still hidden. The bounds must not intersect the
    /// virtual screen at all: this suite is CI-runnable, and a future coordinate change must fail
    /// here rather than paint the backdrop on the owner's screen. The same check
    /// <c>BackdropDarknessLookTests</c>' dry run uses.
    /// </summary>
    private static Action<Window> OffScreenPositioning() => w =>
    {
        OverlayPositioning.CoverPrimaryScreen(w);
        w.Left = -30000;
        w.Top = -30000;
        var bounds = new Rect(w.Left, w.Top, w.Width, w.Height);
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Check.WithCustomMessage(
                $"the window must be off-screen BEFORE it is shown (bounds {bounds} vs screen {screen}).")
            .That(screen.IntersectsWith(bounds)).IsFalse();
    };

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < timeoutMs)
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(10));
        if (!condition())
            throw new TimeoutException("the pumped dispatcher never reached the expected state");
    }

    private static void CloseQuietly(GlassBackdropWindow? window)
    {
        try { window?.Hide(); } catch { /* the window may already be gone */ }
        try { window?.Close(); } catch { /* the window may already be gone */ }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
