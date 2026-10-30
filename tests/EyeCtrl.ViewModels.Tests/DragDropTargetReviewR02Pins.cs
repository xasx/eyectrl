namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C2 fixup-02 T1: the ring-team r02 pin (adopted as written). Window-free (DragTrailViewModel
/// only). The trail view model is a DI singleton, so the leave-before-arm latch must be re-armed
/// AND re-anchored at every drag start.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DragDropTargetReviewR02Pins
{
    private const long StepUs = 33_333;

    [Fact]
    public void ASecondDrag_StartsDisarmed_SoALingerOnItsMenuItemNeverDrops()
    {
        // DragTrailViewModel is a DI singleton (AppBootstrapper AddSingleton): every drag of the
        // session reuses it. The latch must re-anchor on EACH drag's first sample; an anchor left
        // over from the previous drag arms the second drag at once and the item drops on its menu
        // item again (ring r01 F1). Kills "anchor not cleared at a new drag" (r02 N13).
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var s = new EyeCtrlSettings();
            s.SelectionMode = SelectionMode.ManualSelection;
            s.Dwell.FixationRadiusPixels = 80;
            s.Dwell.DwellPauseGraceMs = 500;
            var m = new WeakReferenceMessenger();
            using var trail = new DragTrailViewModel(s, m);
            var drops = 0;
            m.Register<DragDropMessage>(this, (_, _) => drops++);
            long t = 0;

            // Drag 1: starts on its menu item, leaves, rests on the destination and drops.
            m.Send(new StartDragTrailMessage(new PhysicalPoint(1000, 1000)));
            m.Send(new GazeCursorMovedMessage(new PhysicalPoint(1300, 1000), t)); t += StepUs;
            for (var i = 0; i < 30 && drops == 0; i++, t += StepUs)
                m.Send(new GazeCursorMovedMessage(new PhysicalPoint(1900, 1500), t));
            Check.That(drops).IsEqualTo(1);
            t += 1_000_000; // past the cooldown

            // Drag 2, far from drag 1's anchor: a 2 s linger on ITS menu item must not drop.
            m.Send(new StartDragTrailMessage(new PhysicalPoint(400, 1800)));
            var r = trail.ReleaseRadiusPhysical;
            var menu = new PhysicalPoint(700, 1800);
            var rng = new Random(5);
            var at = menu;
            for (var i = 0; i < 60; i++, t += StepUs)
            {
                var next = new PhysicalPoint(at.X + ((rng.NextDouble() - 0.5) * 0.3 * r), at.Y + ((rng.NextDouble() - 0.5) * 0.3 * r));
                if (next.DistanceTo(menu) <= 0.6 * r) at = next;
                m.Send(new GazeCursorMovedMessage(at, t));
            }
            Check.WithCustomMessage("the second drag dropped on its menu item: the latch kept the first drag's anchor.")
                .That(drops).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
