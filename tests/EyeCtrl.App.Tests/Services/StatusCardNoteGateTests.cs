namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Census term (d), agreed with the sticky owner: with a STATUS CARD as the banner slot's live
/// occupant, the slot's one aggregate occluder source gates a note target under it. The real slot,
/// the real presenter and the real card window (Opacity 0 and DWM-cloaked, so nothing is painted)
/// feed the manager exactly as <c>NoteOccluderWiring</c> does in production.
/// </summary>
[Collection("WpfApplication")]
public class StatusCardNoteGateTests
{
    [StaFact]
    public void ALiveStatusCard_OverClose_NeverFiresTheNote()
    {
        var window = StatusCardPresenterTests.HiddenCardWindow();
        var slot = new BannerSlot();
        var status = new StatusCardPresenterTests.FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);

        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.SetOccluderSources(slotOccupantBounds: () => slot.TryGetVisibleBounds(out var b) ? b : null);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var closeCenter = NoteTestBench.CellCentreFromAnchor(anchor, 3);

            presenter.Start();
            status.Enter(ControlState.Paused);
            Check.That(slot.IsLive(presenter)).IsTrue();
            // Park the live card over ✕ (its production spot is the work area's bottom centre).
            window.Left = DpiHelper.PhysicalToLogicalX(closeCenter.X) - window.Width / 2;
            window.Top = DpiHelper.PhysicalToLogicalY(closeCenter.Y) - window.Height / 2;

            for (long t = 0; t <= 1_500_000; t += 100_000)
                manager.OnGazeMoved(closeCenter, t);
            Check.WithCustomMessage("a live status card must occlude ✕ beneath it")
                .That(manager.ActiveNotes).HasSize(1);
            Check.That(manager.ActiveNotes[0].State).IsNotEqualTo(NoteState.PendingUndo);

            // The card leaves the slot: the same held gaze now dwells ✕ — the hold above was the card.
            presenter.HideFromSlot();
            for (long t = 1_600_000; t <= 2_400_000; t += 100_000)
                manager.OnGazeMoved(closeCenter, t);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
            window.Close();
        }
    }
}
