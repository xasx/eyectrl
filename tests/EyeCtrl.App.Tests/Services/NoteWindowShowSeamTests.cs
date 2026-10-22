namespace EyeCtrl.App.Tests.Services;

using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T6 fixup-08 R4: the note manager's show seam. This assembly runs on the OWNER's machine, so a
/// note or "Place here" window must never reach the OS - a topmost note window is exactly the
/// thing the owner must not have to look at while a test runs. The seam itself is switched off
/// once in <c>HeadlessNoteWindowDefaults</c>; this pins the manager's own half, which is what a
/// production path calling <c>Show()</c> directly would break.
/// <para>
/// The mutant is "the seam ignored" (the manager calls <c>Show()</c> regardless): the first
/// assertion about <c>IsVisible</c> is false only because the HWND was never shown, so it goes red
/// the moment the seam stops being honoured.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteWindowShowSeamTests
{
    [StaFact]
    public void TheCapacityNotice_NeverPaintsWhenTheSeamIsOff_EvenWithoutItsOwnOptOut()
    {
        // T6 fixup-09: the notice follows the manager's show seam too, so a future test that
        // forgets its per-instance opt-out (NoticeWindowShowsForTest stays true here) still puts
        // nothing on the owner's screen - while its visible-bounds answer keeps working.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        Check.WithCustomMessage("this test deliberately keeps the notice's own opt-out ON")
            .That(manager.NoticeWindowShowsForTest).IsTrue();

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.ShowCapacityNotice(new PhysicalPoint(1600, 1000));

            var notice = manager.NoticeWindowForTest;
            Check.That(notice).IsNotNull();
            Check.WithCustomMessage("the capacity notice must never reach the screen")
                .That(notice!.IsVisible).IsFalse();
            Check.WithCustomMessage("but its visible-bounds answer still works")
                .That(notice.TryGetVisibleBounds(out _)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheDefaultRun_KeepsTheNoteShowSeamOff()
    {
        // T6 fixup-09: the two UI-gated tests that need a real HWND switch RealNoteWindows on for
        // their own scope and restore it in a finally. A DEFAULT run must never inherit that, and
        // this pin is window-free - it talks to no manager and shows nothing.
        Check.WithCustomMessage("the default run must keep the note show seam off")
            .That(NoteManager.RealNoteWindows).IsFalse();
    }

    [StaFact]
    public void AHeadlessManager_ShowsItsNoteWindow_WithoutPuttingItOnTheScreen()
    {
        Check.WithCustomMessage("this assembly must run the note manager headless")
            .That(NoteManager.RealNoteWindows).IsFalse();

        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            var window = manager.WindowForTest(id)!;

            Check.WithCustomMessage("the note window must never reach the screen")
                .That(window.IsVisible).IsFalse();
            Check.WithCustomMessage("but it still counts as displayed")
                .That(window.IsDisplayed).IsTrue();
            Check.WithCustomMessage("and the manager's own answer agrees")
                .That(manager.AllWindowsVisible).IsTrue();

            // Everything Show() would have built is there: the placement's bounds and the drawn
            // tree (the Loaded half is replayed by the seam).
            var placement = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the window carries its placement")
                .That((window.Width, window.Height))
                .IsEqualTo((placement.Window.Width, placement.Window.Height));
            // No "the seam replays the drawn tree" assert here (T6 fixup-09): ApplyPlacement
            // already drew, so the Loaded replay is belt-and-braces and a mutant that drops it
            // stays green - the assert was vacuous (review-08).
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AHeadlessManager_ShowsThePlaceHereWindow_WithoutPuttingItOnTheScreen()
    {
        Check.WithCustomMessage("this assembly must run the note manager headless")
            .That(NoteManager.RealNoteWindows).IsFalse();

        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("saved text"));
            manager.FinishEdit(id, commit: true);
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present); // Move
            Check.That(manager.IsMoving).IsTrue();
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);       // settle

            Check.That(manager.PlaceShownForTest).IsTrue();
            var place = manager.PlaceWindowForTest;
            Check.That(place).IsNotNull();
            Check.WithCustomMessage("the Place-here window must never reach the screen")
                .That(place!.IsVisible).IsFalse();
            Check.WithCustomMessage("but it still counts as displayed")
                .That(place.IsDisplayed).IsTrue();
            Check.WithCustomMessage("and its rect is the one the manager placed")
                .That((place.Width, place.Height))
                .IsEqualTo((manager.PlaceWindowRectForTest.Width, manager.PlaceWindowRectForTest.Height));

            // T6 fixup-09: hiding it clears the seam's displayed state - a hidden window is not
            // displayed until the next show.
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present); // Caution: Cancel
            Check.That(manager.PlaceShownForTest).IsFalse();
            Check.WithCustomMessage("a hidden Place-here window must not read as displayed")
                .That(place.IsDisplayed).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>Looks away past every control for 200 ms, then holds past the 600 ms dwell.</summary>
    private static long HoldGaze(NoteManager manager, PhysicalPoint point, long present, int ms = 1200)
    {
        var away = new PhysicalPoint(point.X + 10_000, point.Y);
        for (var t = present; t <= present + 220_000; t += 33_000)
            manager.OnGazeMoved(away, t);
        for (var t = present + 240_000; t <= present + ms * 1000; t += 33_000)
            manager.OnGazeMoved(point, t);
        return present + ms * 1000 + 33_000;
    }
}
