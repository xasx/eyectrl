namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
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
}
