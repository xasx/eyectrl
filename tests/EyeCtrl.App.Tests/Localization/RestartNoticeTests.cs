namespace EyeCtrl.App.Tests.Localization;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// The rest of the restart flow (D5, surface <c>language-recovery</c>): the in-progress notice,
/// the refusal acknowledgement, and the failure explanations. Tests on crisis surfaces assert the
/// ENGLISH LITERALS — never <c>T(key)</c> on both sides, which would pass on a raw key. The German
/// routing itself is pinned by <see cref="LanguageRecoveryTests"/> with an injected table.
/// </summary>
public class RestartNoticeTests
{
    [Fact]
    public void TheInProgressNotice_ReadsItsEnglishLiterals()
    {
        Check.That(RestartNotices.InProgressMessage()).IsEqualTo(
            "Restarting EyeCtrl…\n"
            + "Waiting for the new EyeCtrl to start. If it cannot, this one keeps running.\n"
            + "Hiding this notice does not stop the restart.");

        var choices = RestartNotices.InProgressChoices();
        Check.That(choices).HasSize(1);
        Check.That(choices[0].Id).IsEqualTo("restart_progress_hide");
        Check.That(choices[0].Label).IsEqualTo("✕ Hide");
        Check.That(choices[0].Kind).IsEqualTo(ModalChoiceKind.Neutral);
        Check.That(choices[0].OnChosen).IsNull();
    }

    [Fact]
    public void TheRefusalNotice_ReadsItsEnglishLiteral()
    {
        var choices = RestartNotices.RefusedChoices();
        Check.That(choices).HasSize(1);
        Check.That(choices[0].Id).IsEqualTo("restart_failed_ok");
        Check.That(choices[0].Label).IsEqualTo("✓ OK");
        Check.That(choices[0].Kind).IsEqualTo(ModalChoiceKind.Affirmative);
        Check.That(choices[0].OnChosen).IsNull();
    }

    [Theory]
    [InlineData(RestartOutcome.NoExePath,
        "Restart failed: EyeCtrl could not determine its own program path. EyeCtrl is still running.")]
    [InlineData(RestartOutcome.LaunchFailed,
        "Restart failed: the new EyeCtrl could not be started. EyeCtrl is still running.")]
    [InlineData(RestartOutcome.ChildExitedEarly,
        "Restart failed: the new EyeCtrl closed immediately. EyeCtrl is still running.")]
    [InlineData(RestartOutcome.ChildNotReady,
        "Restart failed: the new EyeCtrl did not start in time. EyeCtrl is still running.")]
    public void EveryFailureExplanation_ReadsItsEnglishLiteral(RestartOutcome outcome, string expected)
    {
        Check.That(SelfRestartStrategy.Describe(outcome)).IsEqualTo(expected);
    }

    [Fact]
    public void TheCoordinatorsFailureFrame_WrapsTheRawDetail_AndReadsItsEnglishLiteral()
    {
        // The frame around a raw exception message is language-recovery; the raw detail stays
        // English (or the OS language) — it is interpolated, never translated.
        var settings = new EyeCtrlSettings();
        var reported = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => throw new InvalidOperationException("boom"),
            shutdown: () => { },
            reportInProgress: () => { },
            reportUnavailable: reported.Add,
            runInBackground: work => work(),
            postToUi: work => work(),
            supervisorInstalled: () => false,
            bringUp: new SupervisorBringUp(
                settings,
                enablePublishing: () => { },
                launchSupervisor: () => false,
                persistEnabled: _ => { },
                reconcileRunKey: () => { },
                tryReachSupervisor: () => false));
        _ = coordinator;

        messenger.Send(new EyeCtrl.ViewModels.Messages.RestartAppRequestedMessage());

        Check.That(reported).HasSize(1);
        Check.That(reported[0]).IsEqualTo("Restart failed: boom EyeCtrl is still running.");
    }
}
