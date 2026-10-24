namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// A5 fixup-01 F2 (owner decision): drag + Pause = CANCEL, never a drop. In Auto Selection the drag
/// settles after 500 ms while the toolbar Pause dwell is 700 ms, so a rest on the toolbar Pause
/// used to DROP at the toolbar before Pause could fire. The Pause button's hit circle is now a drag
/// exit zone (the PX1 pattern): the trail neither settles nor releases there, the toolbar's own
/// dwell fires, and Pause cancels the drag at the grab point.
/// <para>
/// Both tests run at DPI 1 and 2: the zone is built from PHYSICAL coordinates, so a zone that is
/// not DPI-converted fails one of the two. The companion test rests 1 cm OUTSIDE the button's hit
/// circle and expects the ordinary 500 ms settle drop — the zone must not swallow the band.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DragToolbarPauseExitZoneTests
{
    private static readonly PhysicalPoint PauseCentre = new(600, 300);
    private static readonly PhysicalPoint GrabOrigin = new(200, 200);

    /// <summary>1 cm on the owner's display, in physical px (a centimetre does not scale).</summary>
    private const double OneCentimetrePhysical = 37.8;

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ARestOnTheToolbarPauseDuringADrag_CancelsThenPauses_AtTheGrabPoint(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        try
        {
            using var world = Build();
            world.StartDrag();

            // 800 ms on the Pause button: past the drag's 500 ms settle AND its 700 ms dwell.
            world.Rest(PauseCentre, 800);

            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.Paused);
            Check.WithCustomMessage(
                    $"DPI {scale}: the toolbar Pause dropped the drag instead of cancelling it.")
                 .That(world.Drops).IsEqualTo(0);
            Check.That(world.Input.MouseDownCount).IsEqualTo(1);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.WithCustomMessage($"DPI {scale}: the release did not happen at the grab point.")
                 .That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(GrabOrigin);
            Check.That(world.Context.DragTrailVM.IsDragActive).IsFalse();
            Check.That(world.Bar.ShownButtonIds).IsEmpty();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ARestOneCentimetreOffTheToolbarPause_StillDrops(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        try
        {
            using var world = Build();
            // 1 cm beyond the Pause button's live hit radius: the ordinary band behaviour.
            var radius = DpiHelper.LogicalToPhysicalX(world.Settings.Toolbar.Size / 2);
            var off = new PhysicalPoint(PauseCentre.X + radius + OneCentimetrePhysical, PauseCentre.Y);

            world.StartDrag();

            // 900 ms: past the 500 ms settle and the 600 ms release dwell.
            world.Rest(off, 900);
            world.Pump();   // the drop's teardown is posted to the dispatcher

            Check.WithCustomMessage($"DPI {scale}: a rest 1 cm off Pause did not drop.")
                 .That(world.Drops).IsEqualTo(1);
            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.FreeLook);
            Check.That(world.Input.MouseDownCount).IsEqualTo(1);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.WithCustomMessage($"DPI {scale}: the drop released at the wrong point.")
                 .That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(off);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void APauseMovedMidDrag_IsStillAnExitAtItsNewCentre()
    {
        // G2: the zone is a LIVE query, not the drag-start snapshot. A re-dock or re-layout moves
        // the button and re-reports its centre mid-drag; the new spot must not settle.
        DpiHelper.Initialize(1.0, 1.0);
        try
        {
            using var world = Build();
            world.StartDrag();

            var moved = new PhysicalPoint(PauseCentre.X + 300, PauseCentre.Y + 120);
            world.Toolbar.UpdateButtonScreenPosition("Pause", moved);

            world.Rest(moved, 800);

            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.Paused);
            Check.WithCustomMessage("the moved Pause dropped the drag instead of cancelling it.")
                 .That(world.Drops).IsEqualTo(0);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(GrabOrigin);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void AToolbarThatIsNotDwellActive_DoesNotZoneTheRest()
    {
        // G2: a hidden/calibrating band cannot fire Pause, so its old spot must behave like any
        // other rest and DROP. The live query reads the same SetActive state the auto-hide drives.
        DpiHelper.Initialize(1.0, 1.0);
        try
        {
            using var world = Build();
            world.StartDrag();
            world.Toolbar.SetActive(false);

            world.Rest(PauseCentre, 900);
            world.Pump();   // the drop's teardown is posted to the dispatcher

            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.FreeLook);
            Check.WithCustomMessage("an inactive toolbar still zoned the drag's settle.")
                 .That(world.Drops).IsEqualTo(1);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(PauseCentre);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void AToolbarSummonedMidDrag_MakesItsPauseAnExit()
    {
        // G2: a drag can start while the band is auto-hidden; summoning it mid-drag makes Pause
        // dwellable again, and the zone must appear with it (a drag-start snapshot never did).
        DpiHelper.Initialize(1.0, 1.0);
        try
        {
            using var world = Build();
            world.Toolbar.SetActive(false);
            world.StartDrag();

            world.Toolbar.SetActive(true);   // the summon, as ToolbarVisibilityAdapter does on Show
            world.Rest(PauseCentre, 800);

            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.Paused);
            Check.WithCustomMessage("a Pause summoned mid-drag dropped the drag.")
                 .That(world.Drops).IsEqualTo(0);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(GrabOrigin);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    private sealed class World : IDisposable
    {
        public required EventCoordinationContext Context { get; init; }
        public required InteractionManager Interaction { get; init; }
        public required EyeCtrlSettings Settings { get; init; }
        public required TestFakes.FakeInputSimulator Input { get; init; }
        public required TestFakes.FakeEdgeButtonBar Bar { get; init; }
        public required DragCoordinator Drag { get; init; }
        public required Dispatcher Dispatcher { get; init; }
        public required ToolbarViewModel Toolbar { get; init; }

        public int Drops { get; set; }

        public void StartDrag()
        {
            Interaction.OnFixationStarted(GrabOrigin);
            Interaction.OnActivationButtonDwelled();
            Interaction.OnActionSelected(InteractionAction.Drag);
            Check.That(Interaction.CurrentState).IsEqualTo(InteractionState.Dragging);
            Check.That(Drag.LastSetupFailure).IsNull();
        }

        /// <summary>Feeds gaze samples at one point through the real messenger fan-out.</summary>
        public void Rest(PhysicalPoint at, int durationMs)
        {
            for (long t = 0; t <= durationMs * 1000L; t += 30_000)
            {
                Context.Messenger.Send(new GazeCursorMovedMessage(at, t)
                {
                    IsPaused = Interaction.CurrentState == InteractionState.Paused,
                });
            }
        }

        public void Pump() => Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        public void Dispose() => Drag.Dispose();
    }

    private World Build()
    {
        var input = new TestFakes.FakeInputSimulator();
        var (madeContext, bar) = TestFakes.MakeDragContextWithBar(input);
        var settings = madeContext.Settings;
        // Auto Selection is the mode where the 500 ms settle DROPS (Manual Selection enters the
        // magnifier instead), so it is the one that used to defeat the Pause button.
        settings.SelectionMode = SelectionMode.AutoSelection;

        var messenger = madeContext.Messenger;
        // THE PRODUCTION SHAPE (fixup-02 G1): the full-arity factory from AppBootstrapper.cs, and
        // NOTHING sets IsVisible — production never does (auto-hide drives SetActive instead). The
        // old zone was gated on IsVisible and was therefore dead here; the assertion below pins the
        // production state so a test cannot force the zone alive again.
        var toolbar = new ToolbarViewModel(
            settings,
            messenger,
            sink: null,
            dwellRegistry: null,
            isFeedbackSuppressed: static () => false,
            logger: null);
        Check.WithCustomMessage("the production toolbar VM must not need IsVisible for its Pause zone.")
             .That(toolbar.IsVisible).IsFalse();
        toolbar.UpdateButtonScreenPosition("Pause", PauseCentre);
        var dispatcher = Dispatcher.CurrentDispatcher;

        var context = madeContext with { Dispatcher = dispatcher, ToolbarVM = toolbar };
        var interaction = context.InteractionManager;
        var drag = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
        drag.Initialize(context);

        var world = new World
        {
            Context = context,
            Interaction = interaction,
            Settings = settings,
            Input = input,
            Bar = bar,
            Drag = drag,
            Dispatcher = dispatcher,
            Toolbar = toolbar,
        };

        // Production's chain, in miniature: the toolbar's Pause dwell sends PauseToggledMessage,
        // and GazeEngineAdapter forwards it to InteractionManager.TogglePause.
        messenger.Register<PauseToggledMessage>(context, (_, _) => interaction.TogglePause());
        messenger.Register<DragDropMessage>(context, (_, _) => world.Drops++);
        return world;
    }
}
