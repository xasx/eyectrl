namespace EyeCtrl.App.Tests.Backstage;

using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// The three proactive surfaces that can paint over Backstage, and the explicit decisions Ticket 03
/// made about them (documented in the report and here so a later edit cannot silently revert them):
/// <list type="bullet">
/// <item><c>SetupNoticeWatch</c> DEFERS — it shows through the presenter directly, bypassing every
/// host guard, so it must gate itself on the menu's open state.</item>
/// <item>The eye-care break DEFERS — <c>canInterrupt</c> excludes Backstage, so a break cannot
/// replace a surface whose dwell the owner is using to reach Quit.</item>
/// <item>The caregiver banner may INTERRUPT — the banner slot is held for ordinary banners, while
/// <c>BannerSlot.Preempt</c> bypasses the hold for the safety channel by design.</item>
/// </list>
/// The full App composition is too heavy to construct headlessly, so these are source pins over
/// comment-stripped App code — the same precedent <c>EventCoordinatorTests</c> uses for its
/// startup wiring.
/// </summary>
public class BackstagePrecedenceWiringTests
{
    private static string AppSource()
    {
        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "App.xaml.cs");
        return Regex.Replace(SourceScanner.StripComments(File.ReadAllText(path)), @"\s+", string.Empty);
    }

    [Fact]
    public void SetupNoticeWatch_DefersWhileBackstageIsOpen_AndPokesOnClose()
    {
        var source = AppSource();

        Check.WithCustomMessage(
                "The setup-notice watch no longer gates on Backstage: it shows through the presenter directly, "
                + "so an undismissable card can paint over the menu for up to 90 s.")
            .That(source.Contains("isBackstageOpen:()=>_eventCoordinator.IsBackstageOpen")).IsTrue();
        Check.WithCustomMessage("The setup-notice watch no longer pokes on the Backstage close edge.")
            .That(source.Contains("_eventCoordinator.BackstageOpenChanged+=_setupNoticeWatch.OnBackstageOpenChanged")).IsTrue();
    }

    [Fact]
    public void EyeCareBreak_CannotInterruptBackstage()
    {
        var source = AppSource();

        Check.WithCustomMessage(
                "The eye-care break can interrupt Backstage again: canInterrupt must exclude the fullscreen menu.")
            .That(source.Contains(
                "canInterrupt:im.CurrentState==InteractionState.FreeLook&&!_eventCoordinator.IsGuideOpen&&!_eventCoordinator.IsBackstageOpen"))
            .IsTrue();
    }

    [Fact]
    public void BannerSlot_IsHeldForBackstage_WhileCaregiverPreemptionStaysTheException()
    {
        var source = AppSource();

        Check.WithCustomMessage(
                "Backstage no longer holds the banner slot; an ordinary banner could paint over the menu.")
            .That(source.Contains(
                "_eventCoordinator.BackstageOpenChanged+=open=>{if(open)bannerSlot.Hold();elseif(!_eventCoordinator.IsGuideOpen)bannerSlot.Release();};"))
            .IsTrue();
        // The hold must release only when the other fullscreen owner is down, or closing Backstage
        // under an open guide would release the guide's hold.
        Check.WithCustomMessage("The guide's banner release no longer respects Backstage's hold.")
            .That(source.Contains(
                "elseif(!_eventCoordinator.IsBackstageOpen)bannerSlot.Release();")).IsTrue();
    }

    [Fact]
    public void SettingsClose_CannotRestoreTheCursorRingUnderBackstage()
    {
        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");
        var source = Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(path)), @"\s+", string.Empty);

        Check.WithCustomMessage(
                "Settings' Closed handler can restore the cursor ring while Backstage owns the screen; the "
                + "Settings close is posted by CloseSettingsMessage and lands AFTER Backstage hid the ring.")
            .That(source.Contains(
                "if(Context.CursorVM.IsVisible&&!_modal.ProbeOpen&&!_modal.BackstageOpen)Context.OverlayManager.CursorCircle.Show();"))
            .IsTrue();
    }

    [Fact]
    public void QuitShutdownAction_IsHandedToTheEventCoordinator()
    {
        // The chain is: App → EventCoordinator.SetShutdownAction → BackstageUiCoordinator.SetShutdown
        // → the window's Quit confirm. Only this first link lives in App.xaml.cs; the other two are
        // pinned by EventCoordinatorTests and QuitConfirm_InvokesTheInjectedShutdown. A null seam
        // here clears every target before dispatch and then invokes nothing, leaving a gaze-only
        // owner on a dead Quit dialog with no close disc and no way back.
        var source = AppSource();

        Check.WithCustomMessage(
                "App no longer calls _eventCoordinator.SetShutdownAction(Shutdown): the Backstage Quit "
                + "confirm would clear every target and invoke a null shutdown, leaving the owner on a "
                + "dead dialog with no close disc.")
            .That(source.Contains("_eventCoordinator.SetShutdownAction(Shutdown);")).IsTrue();
    }
}
