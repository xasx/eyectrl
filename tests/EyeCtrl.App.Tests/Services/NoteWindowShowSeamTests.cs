namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// The note manager's show seam, moved down from T6 by sync 4. This assembly runs on the OWNER's
/// machine, so a note window must never reach the OS: every window the manager "shows" counts as
/// displayed while its real HWND stays hidden. The seam itself is switched off once in
/// <c>HeadlessNoteWindowDefaults</c>; this pins the manager's half.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteWindowShowSeamTests
{
    [StaFact]
    public void TheDefaultRun_KeepsTheNoteShowSeamOff()
    {
        // A default run must never inherit the UI-gated tests' temporary opt-in.
        Check.WithCustomMessage("the default run must keep the note show seam off")
            .That(NoteManager.RealNoteWindows).IsFalse();
    }

    [StaFact]
    public void AHeadlessManager_ShowsItsNoteWindow_WithoutPuttingItOnTheScreen()
    {
        Check.WithCustomMessage("this assembly must run the note manager headless")
            .That(NoteManager.RealNoteWindows).IsFalse();

        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
        var id = manager.ActiveNotes[0].Id;
        var window = manager.WindowForTest(id)!;

        Check.WithCustomMessage("the note window must never reach the screen")
            .That(window.IsVisible).IsFalse();
        Check.WithCustomMessage("but it still counts as displayed")
            .That(window.IsDisplayed).IsTrue();
        Check.WithCustomMessage("and the manager's own answer agrees")
            .That(manager.AllWindowsVisible).IsTrue();
    }

    [StaFact]
    public void ThePlaceHereWindow_AlsoHonoursTheSeam()
    {
        // ShowPlaceHere goes through the same seam (one line in the manager); the window's own half
        // is pinned here: it counts as displayed while its HWND stays hidden.
        var place = new NotePlaceHereWindow();
        InvisibleTestWindow.Prepare(place);
        place.ShowForDisplay(realHwnd: false);
        Check.WithCustomMessage("the Place-here window must never reach the screen")
            .That(place.IsVisible).IsFalse();
        Check.WithCustomMessage("but it still counts as displayed")
            .That(place.IsDisplayed).IsTrue();
    }

    [StaFact]
    public void TheCapacityNotice_NeverPaintsWhenTheSeamIsOff_EvenWithoutItsOwnOptOut()
    {
        // The notice follows the manager's show seam too, so a test that keeps its own opt-out ON
        // still puts nothing on the owner's screen - while its visible-bounds answer keeps working.
        Check.WithCustomMessage("this assembly must run the note manager headless")
            .That(NoteManager.RealNoteWindows).IsFalse();

        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        Check.WithCustomMessage("this test deliberately keeps the notice's own opt-out ON")
            .That(manager.NoticeWindowShowsForTest).IsTrue();

        EyeCtrl.Core.Services.DpiHelper.Initialize(2.0, 2.0);
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
        finally { EyeCtrl.Core.Services.DpiHelper.Initialize(1.0, 1.0); }
    }
}
