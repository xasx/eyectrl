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
            Check.WithCustomMessage("the seam replays the drawn tree")
                .That(window.CellFillForTest(0)).IsNotNull();
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
