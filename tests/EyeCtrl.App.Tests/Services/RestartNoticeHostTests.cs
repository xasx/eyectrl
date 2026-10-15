namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The App-level restart notices (holistic review F5). A Supervisor-initiated language restart runs
/// with no Settings window open; the notices used to exist only as that window's handlers, so a
/// refused restart was invisible. The host shows them when no Settings window is open and never when
/// one is (that window shows its own modal), in the same target language, with the same text.
/// Exclusive with the other table-switching tests: German tests swap the process table.
/// </summary>
[Collection("StringTable Tests (App)")]
public class RestartNoticeHostTests
{
    private sealed record Shown(string Message, string CloseLabel, TimeSpan Hold);

    private sealed class FakePresenter : IRestartNoticePresenter
    {
        public List<Shown> Notices { get; } = [];
        public List<(PhysicalPoint Gaze, long Timestamp)> Gaze { get; } = [];
        public int HideCalls { get; private set; }
        public bool IsShowing { get; set; }

        public void ShowNotice(string message, string closeLabel, TimeSpan hold)
        {
            Notices.Add(new Shown(message, closeLabel, hold));
            IsShowing = true;
        }

        public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds) => Gaze.Add((gaze, timestampMicroseconds));

        public void HideNotice()
        {
            HideCalls++;
            IsShowing = false;
        }
    }

    private sealed class Rig
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public FakePresenter Presenter { get; } = new();
        public bool SettingsOpen { get; set; }
        public UiLanguage Target { get; set; } = UiLanguage.English;
        public RestartNoticeHost Host { get; }

        public Rig() =>
            Host = new RestartNoticeHost(
                Messenger, Presenter, () => SettingsOpen, () => Target, work => work());
    }

    [Fact]
    public void TheInProgressNotice_AppearsWhenNoSettingsWindowIsOpen()
    {
        var rig = new Rig();

        rig.Messenger.Send(new RestartInProgressMessage());

        Check.That(rig.Presenter.Notices).HasSize(1);
        var notice = rig.Presenter.Notices[0];
        Check.That(notice.Message).IsEqualTo(
            "Restarting EyeCtrl…\n"
            + "Waiting for the new EyeCtrl to start. If it cannot, this one keeps running.\n"
            + "Hiding this notice does not stop the restart.");
        Check.That(notice.CloseLabel).IsEqualTo("✕ Hide");
        Check.That(notice.Hold).IsEqualTo(RestartNoticeHost.InProgressHold);
    }

    [Fact]
    public void TheRefusedNotice_ShowsTheRefusalReason_WithOk()
    {
        var rig = new Rig();

        rig.Messenger.Send(new RestartUnavailableMessage(
            "Restart failed: the new EyeCtrl did not start in time. EyeCtrl is still running."));

        Check.That(rig.Presenter.Notices).HasSize(1);
        var notice = rig.Presenter.Notices[0];
        Check.That(notice.Message).IsEqualTo(
            "Restart failed: the new EyeCtrl did not start in time. EyeCtrl is still running.");
        Check.That(notice.CloseLabel).IsEqualTo("✓ OK");
        Check.That(notice.Hold).IsEqualTo(RestartNoticeHost.RefusedHold);
    }

    [Fact]
    public void WhileASettingsWindowIsOpen_TheHostStaysSilent_SoTheNoticeIsNeverShownTwice()
    {
        var rig = new Rig { SettingsOpen = true };

        rig.Messenger.Send(new RestartInProgressMessage());
        rig.Messenger.Send(new RestartUnavailableMessage("anything"));

        Check.That(rig.Presenter.Notices).IsEmpty();

        // And it speaks again the moment that window is gone.
        rig.SettingsOpen = false;
        rig.Messenger.Send(new RestartUnavailableMessage("anything"));
        Check.That(rig.Presenter.Notices).HasSize(1);
    }

    [Fact]
    public void AnInProgressNotice_IsReplacedByTheRefusal_NotStackedUnderIt()
    {
        var rig = new Rig();

        rig.Messenger.Send(new RestartInProgressMessage());
        rig.Messenger.Send(new RestartUnavailableMessage("Restart failed. EyeCtrl is still running."));

        // The presenter replaces whatever is up (one window); the host sends the refusal LAST.
        Check.That(rig.Presenter.Notices).HasSize(2);
        Check.That(rig.Presenter.Notices[1].Message).IsEqualTo("Restart failed. EyeCtrl is still running.");
        Check.That(rig.Presenter.Notices[1].CloseLabel).IsEqualTo("✓ OK");
    }

    [Fact]
    public void TheNoticeSpeaksTheTargetLanguage_NotTheRunningOne()
    {
        // The caretaker chose German while English runs: the notice resolves in German, like the
        // Settings modal does. (The table must have the German loaded for the named lookup to find it.)
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var german = new Rig { Target = UiLanguage.German };
            german.Messenger.Send(new RestartInProgressMessage());
            german.Messenger.Send(new RestartUnavailableMessage("Neustart fehlgeschlagen."));

            Check.That(german.Presenter.Notices[0].Message).IsEqualTo(
                "EyeCtrl wird neu gestartet.\n"
                + "Das neue EyeCtrl startet gerade; klappt das nicht, läuft dieses weiter.\n"
                + "Ausblenden hält den Neustart nicht an.");
            Check.That(german.Presenter.Notices[0].CloseLabel).IsEqualTo("✕ Ausblenden");
            Check.That(german.Presenter.Notices[1].Message).IsEqualTo("Neustart fehlgeschlagen.");
            Check.That(german.Presenter.Notices[1].CloseLabel).IsEqualTo("✓ OK");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void Gaze_IsForwardedOnlyWhileANoticeShows_AndNeverFromAHeldSample()
    {
        var rig = new Rig();
        var point = new PhysicalPoint(100, 200);

        rig.Messenger.Send(new GazeCursorMovedMessage(point, 1_000));
        Check.WithCustomMessage("gaze was fed to a notice that is not showing")
            .That(rig.Presenter.Gaze).IsEmpty();

        rig.Messenger.Send(new RestartInProgressMessage());
        rig.Messenger.Send(new GazeCursorMovedMessage(point, 2_000));
        rig.Messenger.Send(new GazeCursorMovedMessage(new GazeFrame(point, 3_000, IsInGap: true)));

        Check.That(rig.Presenter.Gaze).HasSize(1);
        Check.That(rig.Presenter.Gaze[0].Timestamp).IsEqualTo(2_000);
    }

    [Fact]
    public void Dispose_UnregistersTheHost_AndTakesTheNoticeDown()
    {
        var rig = new Rig();
        rig.Messenger.Send(new RestartInProgressMessage());

        rig.Host.Dispose();

        Check.That(rig.Presenter.HideCalls).IsEqualTo(1);
        rig.Messenger.Send(new RestartInProgressMessage());
        Check.That(rig.Presenter.Notices).HasSize(1);
    }

    [Fact]
    public void TheHost_IsWiredToTheAppsRestartMessagesAndTheEventCoordinatorsSettingsFlag()
    {
        // App.xaml.cs is integration wiring that cannot be composed headlessly; pin the arguments
        // that make the host speak exactly when Settings is closed, in the Settings window's own
        // target-language rule. Dropping either leaves every test above green.
        var text = LocalizationTestData.SourceFiles()
            .Single(file => file.Relative == "src/EyeCtrl.App/App.xaml.cs").Text;

        var construction = System.Text.RegularExpressions.Regex.Match(
            text, @"new\s+RestartNoticeHost\s*\(([^;]*)\)\s*;");
        Check.WithCustomMessage("App.xaml.cs no longer constructs the RestartNoticeHost.")
            .That(construction.Success).IsTrue();
        var arguments = construction.Groups[1].Value;
        Check.That(System.Text.RegularExpressions.Regex.IsMatch(
            arguments, @"isSettingsOpen\s*:\s*\(\)\s*=>\s*_eventCoordinator\s*\.\s*IsSettingsOpen")).IsTrue();
        Check.That(System.Text.RegularExpressions.Regex.IsMatch(
            arguments, @"targetLanguage\s*:\s*\(\)\s*=>\s*RestartNotices\s*\.\s*TargetLanguage\s*\(")).IsTrue();
        Check.That(arguments.Contains("new RestartNoticeWindow(", StringComparison.Ordinal)).IsTrue();
        // The card keeps off the gaze keyboard and the toolbar only if it is told where they are: the
        // zones the activation offer already keeps clear of.
        Check.WithCustomMessage("the card is no longer given the chrome zones, so it could cover the keys or the toolbar")
            .That(System.Text.RegularExpressions.Regex.IsMatch(
                arguments,
                @"keepClear\s*:\s*\(\)\s*=>\s*RestartNoticePlacement\s*\.\s*ToDip\s*\(\s*(?:EyeCtrl\s*\.\s*App\s*\.\s*Services\s*\.\s*Coordinators\s*\.\s*)?ActivationChromeZones\s*\.\s*CollectFromProduction\s*\(\s*coordinationContext\s*\)\s*\)"))
            .IsTrue();
        // Settings opening over a card that is up takes it down (review R3).
        Check.WithCustomMessage("App.xaml.cs no longer takes the card down when Settings opens")
            .That(System.Text.RegularExpressions.Regex.IsMatch(
                text,
                @"_eventCoordinator\s*\.\s*SettingsOpenChanged\s*\+=\s*_restartNoticeHost\s*\.\s*OnSettingsOpenChanged"))
            .IsTrue();
        // D17 (T15 merge): the card is a collision source for the preview panel's gaze-consumer
        // inventory; if startup stops attaching it to RestartNoticeBridge the source silently
        // degrades to empty and the panel may cover the close button.
        Check.WithCustomMessage(
                "App.xaml.cs no longer attaches the card to the D17 collision bridge — the " +
                "preview panel would not see the card's close button as a collision source")
            .That(System.Text.RegularExpressions.Regex.IsMatch(
                text,
                @"RestartNoticeBridge\(\)\s*\.\s*Attach\(\(RestartNoticeWindow\)_restartNoticeHost\s*\.\s*Presenter\)"))
            .IsTrue();
    }

    /// <summary>
    /// Review R3: the host checked Settings only when a notice ARRIVED, so Settings opening over a
    /// card that was up left the card's button live underneath it: an invisible target, whose dwell
    /// would play the activation cue for nothing the user can see.
    /// </summary>
    [Fact]
    public void OpeningSettings_TakesAShowingNoticeDown()
    {
        var rig = new Rig();
        rig.Messenger.Send(new RestartInProgressMessage());
        Check.That(rig.Presenter.IsShowing).IsTrue();

        rig.SettingsOpen = true;
        rig.Host.OnSettingsOpenChanged(true);

        Check.That(rig.Presenter.IsShowing).IsFalse();
        Check.That(rig.Presenter.HideCalls).IsEqualTo(1);

        // Closing Settings again neither hides nor brings anything back.
        rig.SettingsOpen = false;
        rig.Host.OnSettingsOpenChanged(false);
        Check.That(rig.Presenter.HideCalls).IsEqualTo(1);
        Check.That(rig.Presenter.Notices).HasSize(1);
    }

    [Fact]
    public void WhileSettingsIsOpen_NoGazeReachesTheCard()
    {
        var rig = new Rig();
        rig.Messenger.Send(new RestartInProgressMessage());
        rig.SettingsOpen = true;   // opened over the card, before the host heard of it

        rig.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 2_000));

        Check.WithCustomMessage("gaze reached a card that Settings covers")
            .That(rig.Presenter.Gaze).IsEmpty();
    }
}
