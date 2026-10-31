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

    [StaTheory]
    [InlineData(1.0, SelectionMode.AutoSelection)]
    [InlineData(2.0, SelectionMode.AutoSelection)]
    [InlineData(1.0, SelectionMode.ManualSelection)]
    [InlineData(2.0, SelectionMode.ManualSelection)]
    public void ARestPastTheEdge_NeverDrops_AndPauseAfterTheSummonCancels(
        double scale, SelectionMode mode)
    {
        // H1 (fixup-03): with the toolbar auto-hidden, the way to Pause is the past-edge summon.
        // That gaze must never settle or release the drag: in AutoSelection the 500 ms settle used
        // to drop at 533 ms, and in ManualSelection the release target dropped on a slow summon.
        DpiHelper.Initialize(scale, scale);
        try
        {
            using var world = Build(mode);
            world.Toolbar.SetActive(false);   // auto-hidden after 5 s off the band
            world.StartDrag();

            var off = new PhysicalPoint(PauseCentre.X, -100);
            world.Rest(off, 2_000);           // the summon gesture, held for 2 s

            Check.WithCustomMessage($"DPI {scale}/{mode}: a past-edge rest dropped the drag.")
                 .That(world.Drops).IsEqualTo(0);
            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.Dragging);
            Check.WithCustomMessage("the past-edge rest settled into the manual-selection view.")
                 .That(world.Context.DragTrailVM.ShowBack).IsFalse();
            Check.That(world.Input.MouseUpCount).IsEqualTo(0);

            // The summon completes; Pause is now dwellable and the gaze moves to it: cancel at the
            // grab point, then Paused.
            world.Toolbar.SetActive(true);
            world.Rest(PauseCentre, 800);

            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.Paused);
            Check.That(world.Drops).IsEqualTo(0);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(GrabOrigin);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaTheory]
    [InlineData(1.0, SelectionMode.AutoSelection)]
    [InlineData(2.0, SelectionMode.AutoSelection)]
    [InlineData(1.0, SelectionMode.ManualSelection)]
    [InlineData(2.0, SelectionMode.ManualSelection)]
    public void ARestInTheWithheldBand_NeverDrops_AndPauseAfterTheSummonCancels(
        double scale, SelectionMode mode)
    {
        // C6: 10–30 px past the edge is the WITHHELD band, not yet the summon gesture. It must still
        // never drop: the settle is not advanced there (so AutoSelection's 500 ms settle cannot
        // complete off-screen, A5 H1) and the release dwell sees the sample as outside (so
        // ManualSelection's release pauses and expires instead of crediting). This is the sibling
        // the two-band rule deliberately adds: before C6 this point was covered by the single exit
        // band; now it is covered by withholding, and the summon that follows still reaches Pause.
        DpiHelper.Initialize(scale, scale);
        try
        {
            using var world = Build(mode);
            world.Toolbar.SetActive(false);   // auto-hidden after 5 s off the band
            world.StartDrag();

            var off = new PhysicalPoint(PauseCentre.X, -25);
            world.Rest(off, 2_000);           // held in the withheld band for 2 s

            Check.WithCustomMessage($"DPI {scale}/{mode}: a withheld-band rest dropped the drag.")
                 .That(world.Drops).IsEqualTo(0);
            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.Dragging);
            Check.WithCustomMessage("the withheld-band rest settled into the manual-selection view.")
                 .That(world.Context.DragTrailVM.ShowBack).IsFalse();
            Check.That(world.Input.MouseUpCount).IsEqualTo(0);

            // The summon completes; Pause is now dwellable and the gaze moves to it: cancel at the
            // grab point, then Paused.
            world.Toolbar.SetActive(true);
            world.Rest(PauseCentre, 800);

            Check.That(world.Interaction.CurrentState).IsEqualTo(InteractionState.Paused);
            Check.That(world.Drops).IsEqualTo(0);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(GrabOrigin);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaTheory]
    [InlineData(1.0, SelectionMode.AutoSelection)]
    [InlineData(2.0, SelectionMode.AutoSelection)]
    [InlineData(1.0, SelectionMode.ManualSelection)]
    [InlineData(2.0, SelectionMode.ManualSelection)]
    public void ARestOnScreenNearTheEdge_StillDrops(double scale, SelectionMode mode)
    {
        // The on-screen tolerance: the edge row itself (or a few px of tracker jitter at the
        // border) is a normal rest and must keep today's drop.
        DpiHelper.Initialize(scale, scale);
        try
        {
            using var world = Build(mode);
            world.StartDrag();
            var near = new PhysicalPoint(PauseCentre.X, 5);   // inside the 10 px tolerance

            world.Rest(near, 900);
            world.Pump();   // the drop's teardown is posted to the dispatcher

            Check.WithCustomMessage($"DPI {scale}/{mode}: an on-screen rest near the edge did not drop.")
                 .That(world.Drops).IsEqualTo(1);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(near);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ARestJustPastTheEdge_InsideTheTolerance_StillDrops(double scale)
    {
        // T1: the 10 px tolerance is what keeps the border usable. A rest 5 px ABOVE the screen is
        // still an ordinary rest — it must drop, not vanish into the summon suppression.
        DpiHelper.Initialize(scale, scale);
        try
        {
            using var world = Build();
            world.StartDrag();
            var justPast = new PhysicalPoint(PauseCentre.X, -5);

            world.Rest(justPast, 900);
            world.Pump();   // the drop's teardown is posted to the dispatcher

            Check.WithCustomMessage(
                    $"DPI {scale}: a rest 5 px past the edge (inside the tolerance) did not drop.")
                 .That(world.Drops).IsEqualTo(1);
            Check.That(world.Input.MouseUpCount).IsEqualTo(1);
            Check.That(world.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
                 .IsEqualTo(justPast);
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

            // C2 fixup-01 R1: the drop arms only once the gaze has left the spot of the first
            // sample after the drag starts (the hidden Drag menu item). One sample far from every
            // spot these rows rest on, before their t = 0, is that saccade — without it a
            // ManualSelection rest would pass by never arming, and a no-drop row would prove nothing.
            Context.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(2000, 1500), -30_000));
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

    private World Build(SelectionMode mode = SelectionMode.AutoSelection)
    {
        var input = new TestFakes.FakeInputSimulator();
        var (madeContext, bar) = TestFakes.MakeDragContextWithBar(input);
        var settings = madeContext.Settings;
        // Auto Selection is the mode where the 500 ms settle DROPS (Manual Selection enters the
        // magnifier first); both matter, so the H1 tests run in each.
        settings.SelectionMode = mode;

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
