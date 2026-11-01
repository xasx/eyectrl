namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C7: the drag's edge-aware band is published by <see cref="DragCoordinator"/> at every drag start
/// — the view model never reads settings. These rows drive the REAL coordinator and the REAL
/// <see cref="DragTrailViewModel"/> through the context's messenger (no window, no OS input), and
/// read the band off the only observable that differs: a credited sample re-centres the drop disc
/// (it is an ordinary sample); a withheld one does not move it.
/// <para>
/// The rules pinned here are the coordinator's approved readings:
/// <list type="number">
/// <item>the toolbar's <c>DockedEdge</c> is a summon edge while <c>Toolbar.IsDocked</c> (there is
/// no separate auto-hide switch; a docked toolbar with a real tracker auto-hides);</item>
/// <item>the Quick Actions edge is a summon edge while <c>QuickActions.Enabled</c>, and it is the
/// DOCK-RESOLVED edge (<c>ComputeActualSummonEdge</c>'s flip when it collides with the dock).</item>
/// </list>
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class DragEdgeAwareBandWiringTests
{
    private const long StepUs = 33_333;   // 30 Hz, as in the ViewModels pins

    private static PhysicalRect Screen => ScreenGeometry.PrimaryScreenBoundsPhysical();

    private static PhysicalPoint Away() => new(Screen.CenterX, Screen.CenterY);

    private static PhysicalPoint PastTop(double depth) => new(Screen.CenterX, Screen.Y - depth);

    private static PhysicalPoint PastBottom(double depth)
        => new(Screen.CenterX, Screen.Y + Screen.Height + depth);

    private static PhysicalPoint PastLeft(double depth) => new(Screen.X - depth, Screen.CenterY);

    private static PhysicalPoint PastRight(double depth)
        => new(Screen.X + Screen.Width + depth, Screen.CenterY);

    /// <summary>Starts a drag on the fake overlay / fake edge bar; nothing is shown or clicked.</summary>
    private static DragCoordinator Start(EventCoordinationContext context)
    {
        var coordinator = new DragCoordinator
        {
            CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay(),
        };
        coordinator.Initialize(context);
        return coordinator;
    }

    /// <summary>The first sample is the leave that arms C2's latch; the second is the band sample.</summary>
    private static void Send(EventCoordinationContext context, PhysicalPoint at, ref long t)
    {
        t += StepUs;
        context.Messenger.Send(new GazeCursorMovedMessage(at, t));
    }

    [Fact]
    public void AFloatingToolbar_IsNotASummonEdge()
    {
        // Auto-hide off == floating: there is no summon gesture on any edge from the toolbar.
        var (context, _) = TestFakes.MakeDragContextWithBar(new TestFakes.FakeInputSimulator());
        context.Settings.Toolbar.IsDocked = false;
        using var coordinator = Start(context);

        coordinator.OnDragStarted(new PhysicalPoint(100, 100));
        long t = 0;
        var away = Away();
        Send(context, away, ref t);

        var above = PastTop(25);
        Send(context, above, ref t);

        Check.WithCustomMessage("a floating toolbar still withheld the top band.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(above);
    }

    [Fact]
    public void EachDragStartRepublishesTheToolbarSummonEdge()
    {
        // The set is snapshotted per drag, so a re-dock between two drags must take effect on the
        // second. Mutation: publish only once → the second drag keeps Top and both rows below fail.
        var (context, _) = TestFakes.MakeDragContextWithBar(new TestFakes.FakeInputSimulator());
        using var coordinator = Start(context);
        var away = Away();
        var above = PastTop(25);
        var below = PastBottom(25);

        // Drag 1: the default dock is Top, so the top band is withheld.
        coordinator.OnDragStarted(new PhysicalPoint(100, 100));
        long t = 0;
        Send(context, away, ref t);
        Send(context, above, ref t);
        Check.WithCustomMessage("the docked Top edge did not withhold its band.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(away);
        coordinator.OnDragCancel();

        context.Settings.Toolbar.DockedEdge = "Bottom";

        // Drag 2: Top is now a plain edge (credited); Bottom is the docked summon edge (withheld).
        coordinator.OnDragStarted(new PhysicalPoint(100, 100));
        long t2 = 0;
        Send(context, away, ref t2);
        Send(context, above, ref t2);
        Check.WithCustomMessage("Top was still treated as a summon edge after the re-dock.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(above);
        Send(context, below, ref t2);
        Check.WithCustomMessage("Bottom was not treated as the summon edge after the re-dock.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(above);
    }

    [Fact]
    public void QuickActionsDisabled_CreditsItsEdge()
    {
        // Approved reading 2: QA disabled means no summon can fire, so its edge is a plain edge.
        // Mutation: drop the Enabled guard → the second drag still withholds Left (RED).
        var (context, _) = TestFakes.MakeDragContextWithBar(new TestFakes.FakeInputSimulator());
        using var coordinator = Start(context);
        var away = Away();
        var left = PastLeft(25);

        // Drag 1: QA enabled (Left by default) → withheld.
        coordinator.OnDragStarted(new PhysicalPoint(100, 100));
        long t = 0;
        Send(context, away, ref t);
        Send(context, left, ref t);
        Check.WithCustomMessage("the enabled Quick Actions edge did not withhold its band.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(away);
        coordinator.OnDragCancel();

        context.Settings.QuickActions.Enabled = false;

        // Drag 2: disabled → credited.
        coordinator.OnDragStarted(new PhysicalPoint(100, 100));
        long t2 = 0;
        Send(context, away, ref t2);
        Send(context, left, ref t2);
        Check.WithCustomMessage("the disabled Quick Actions edge still withheld its band.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(left);
    }

    [Fact]
    public void TheQuickActionsEdgeIsTheDockResolvedOne()
    {
        // Toolbar docked Left and QA configured Left: the summon itself flips QA to Right, so the
        // band must be withheld on Left (toolbar) and Right (QA), and credited on Top.
        // Mutation: use the configured edge instead of ComputeActualSummonEdge → Right credits (RED).
        var (context, _) = TestFakes.MakeDragContextWithBar(new TestFakes.FakeInputSimulator());
        context.Settings.Toolbar.DockedEdge = "Left";
        using var coordinator = Start(context);

        coordinator.OnDragStarted(new PhysicalPoint(100, 100));
        long t = 0;
        var away = Away();
        Send(context, away, ref t);

        var left = PastLeft(25);
        Send(context, left, ref t);
        Check.WithCustomMessage("the docked Left edge did not withhold its band.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(away);

        var right = PastRight(25);
        Send(context, right, ref t);
        Check.WithCustomMessage("the flipped Quick Actions edge (Right) did not withhold its band.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(away);

        var top = PastTop(25);
        Send(context, top, ref t);
        Check.WithCustomMessage("Top withheld its band although neither summon uses it.")
             .That(context.DragTrailVM.ReleaseButtonCenter).IsEqualTo(top);
    }
}
