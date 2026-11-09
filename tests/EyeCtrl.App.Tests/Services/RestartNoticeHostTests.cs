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
        public bool ProbeOpen { get; set; }
        public UiLanguage Target { get; set; } = UiLanguage.English;
        public RestartNoticeHost Host { get; }

        public Rig() =>
            Host = new RestartNoticeHost(
                Messenger, Presenter, () => SettingsOpen, () => ProbeOpen, () => Target, work => work());
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
        // B6: the close target must read the LIVE DwellSettings object at each show (its
        // RestartNoticeDwellMs, 0 = follow ActivationButtonDwellMs), not a startup snapshot.
        Check.WithCustomMessage(
                "the card no longer receives the live DwellSettings, so its new dwell knob cannot be read")
            .That(System.Text.RegularExpressions.Regex.IsMatch(
                arguments, @"dwellSettings\s*:\s*settings\s*\.\s*Dwell")).IsTrue();
        // The card keeps off the gaze keyboard and the toolbar only if it is told where they are: the
        // zones the activation offer already keeps clear of.
        Check.WithCustomMessage("the card is no longer given the chrome zones, so it could cover the keys or the toolbar")
            .That(System.Text.RegularExpressions.Regex.IsMatch(
                arguments,
                @"keepClear\s*:\s*\(\)\s*=>\s*RestartNoticePlacement\s*\.\s*ToDip\s*\(\s*(?:EyeCtrl\s*\.\s*App\s*\.\s*Services\s*\.\s*Coordinators\s*\.\s*)?ActivationChromeZones\s*\.\s*CollectFromProduction\s*\(\s*coordinationContext\s*\)"))
            .IsTrue();
        // B4: the card keeps clear of the bottom-centre banner slot's live occupant too. Without this
        // the card's close button would sit over a drift notice or caregiver alert.
        Check.WithCustomMessage(
                "the card is no longer told where the live banner is (B4), so it could cover it")
            .That(System.Text.RegularExpressions.Regex.IsMatch(
                arguments,
                @"\.\s*Concat\s*\(\s*bannerSlot\s*\.\s*LiveBoundsOrEmpty\s*\(\s*\)\s*\)"))
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

    // ────── SP-8 review 01 F1: the sub-pixel probe is the second screen-owning surface ──────

    [Fact]
    public void WhileTheProbeIsOpen_NoNoticeIsShown()
    {
        var rig = new Rig { ProbeOpen = true };

        rig.Messenger.Send(new RestartInProgressMessage());
        rig.Messenger.Send(new RestartUnavailableMessage("anything"));

        Check.That(rig.Presenter.Notices).IsEmpty();

        // A NEW request after the probe closes speaks again; the suppression is not sticky.
        rig.ProbeOpen = false;
        rig.Messenger.Send(new RestartUnavailableMessage("anything"));
        Check.That(rig.Presenter.Notices).HasSize(1);
    }

    /// <summary>
    /// SP-8 review 01 F1: on the probe's Intro the card's close button overlapped Start's hit area
    /// (320 × 83 DIP), so one dwell closed the card AND started the trials.
    /// </summary>
    [Fact]
    public void OpeningTheProbe_TakesAShowingNoticeDown()
    {
        var rig = new Rig();
        rig.Messenger.Send(new RestartInProgressMessage());
        Check.That(rig.Presenter.IsShowing).IsTrue();

        rig.ProbeOpen = true;
        rig.Host.OnProbeOpenChanged(true);

        Check.That(rig.Presenter.IsShowing).IsFalse();
        Check.That(rig.Presenter.HideCalls).IsEqualTo(1);

        // Closing the probe neither hides nor brings anything back (the Settings precedent).
        rig.ProbeOpen = false;
        rig.Host.OnProbeOpenChanged(false);
        Check.That(rig.Presenter.HideCalls).IsEqualTo(1);
        Check.That(rig.Presenter.Notices).HasSize(1);
    }

    [Fact]
    public void WhileTheProbeIsOpen_NoGazeReachesTheCard()
    {
        var rig = new Rig();
        rig.Messenger.Send(new RestartInProgressMessage());
        rig.ProbeOpen = true;   // opened over the card, before the host heard of it

        rig.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 2_000));

        Check.WithCustomMessage("gaze reached a card that the probe covers")
            .That(rig.Presenter.Gaze).IsEmpty();
    }

    [Fact]
    public void TheHost_IsWiredToTheProbesProbeOpenFlag()
    {
        // SP-8 review 01 F1: App.xaml.cs is integration wiring that cannot be composed headlessly;
        // pin the probe seam the way T15 pinned the Settings one. Dropping either half leaves every
        // host test above green (mutant f4).
        var text = LocalizationTestData.SourceFiles()
            .Single(file => file.Relative == "src/EyeCtrl.App/App.xaml.cs").Text;

        var construction = System.Text.RegularExpressions.Regex.Match(
            text, @"new\s+RestartNoticeHost\s*\(([^;]*)\)\s*;");
        Check.WithCustomMessage("App.xaml.cs no longer constructs the RestartNoticeHost.")
            .That(construction.Success).IsTrue();
        Check.WithCustomMessage("App.xaml.cs no longer passes the probe flag into the RestartNoticeHost.")
            .That(System.Text.RegularExpressions.Regex.IsMatch(
                construction.Groups[1].Value,
                @"isProbeOpen\s*:\s*\(\)\s*=>\s*_eventCoordinator\s*\.\s*IsProbeOpen")).IsTrue();
        // The probe opening over a card that is up takes it down (review 01 F1).
        Check.WithCustomMessage("App.xaml.cs no longer takes the card down when the probe opens")
            .That(System.Text.RegularExpressions.Regex.IsMatch(
                text,
                @"_eventCoordinator\s*\.\s*ProbeOpenChanged\s*\+=\s*_restartNoticeHost\s*\.\s*OnProbeOpenChanged"))
            .IsTrue();
    }

    // ────── N2 (fixup-01): the sub-pixel probe's start-failure notice rides the same card ──────

    [Fact]
    public void TheProbeStartFailedNotice_ShowsWithTheShortHold_WhenNoSurfaceIsOpen()
    {
        var rig = new Rig();

        rig.Host.ShowProbeStartFailed("The sub-pixel test did not start.", "✓ OK");

        Check.That(rig.Presenter.Notices).HasSize(1);
        var notice = rig.Presenter.Notices[0];
        Check.That(notice.Message).IsEqualTo("The sub-pixel test did not start.");
        Check.That(notice.CloseLabel).IsEqualTo("✓ OK");
        // fixup-01 F2, pinned as the VALUE: the card suspends the activation offers while it is up,
        // and this owner is gaze-only — the refusals' 30 s would leave no gaze click for half a
        // minute right after they asked a test to run. A mutant back to RefusedHold fails here.
        Check.That(notice.Hold).IsEqualTo(TimeSpan.FromSeconds(15));
        Check.That(RestartNoticeHost.StartFailedHold).IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void TheProbeStartFailedNotice_StaysDownWhileSettingsOrTheProbeIsOpen()
    {
        // fixup-01 F3: the host's own guard (ShowRefused's), so the card never shows over a
        // surface that owns the screen.
        var settings = new Rig { SettingsOpen = true };
        settings.Host.ShowProbeStartFailed("text", "✓ OK");
        Check.WithCustomMessage("the start-failure card showed over an open Settings window")
            .That(settings.Presenter.Notices).IsEmpty();

        var probe = new Rig { ProbeOpen = true };
        probe.Host.ShowProbeStartFailed("text", "✓ OK");
        Check.WithCustomMessage("the start-failure card showed over an open probe")
            .That(probe.Presenter.Notices).IsEmpty();

        // And it speaks as soon as the surface is gone — the suppression is not sticky.
        probe.ProbeOpen = false;
        probe.Host.ShowProbeStartFailed("text", "✓ OK");
        Check.That(probe.Presenter.Notices).HasSize(1);
    }

    [Fact]
    public void TheProbeStartFailedNotice_IsAttachedAfterTheHostIsBuilt()
    {
        // N2 fixup-01 F1: the sink reaches production through two lines no container can see, so
        // both are source-pinned (this one, BannerSlotWiringTests.cs:315 block style; the
        // EventCoordinator half in EventCoordinatorTests). The App half: the attach must exist,
        // route through the host's guarded method, and sit AFTER the host construction — before
        // it, _restartNoticeHost is null, the attach drops, and every start failure is log-only.
        var app = LocalizationTestData.SourceFiles()
            .Single(file => file.Relative == "src/EyeCtrl.App/App.xaml.cs").Text;

        int host = app.IndexOf("_restartNoticeHost = new RestartNoticeHost(", StringComparison.Ordinal);
        Check.WithCustomMessage("App.xaml.cs no longer builds the restart notice host")
            .That(host).IsStrictlyGreaterThan(-1);

        int attach = app.IndexOf("AttachProbeStartFailedNoticeSink(", StringComparison.Ordinal);
        Check.WithCustomMessage("App.xaml.cs no longer attaches the probe's start-failure notice sink")
            .That(attach).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "the sink is attached BEFORE the restart notice host exists: the wiring would drop " +
                "it and every start failure would be log-only again")
            .That(attach).IsStrictlyGreaterThan(host);

        var statement = app[attach..Math.Min(app.Length, attach + 220)];
        Check.WithCustomMessage(
                "the sink no longer routes through the host's guarded ShowProbeStartFailed " +
                "(fixup-01 F3): the card would show over Settings or the probe again")
            .That(statement.Contains("_restartNoticeHost.ShowProbeStartFailed", StringComparison.Ordinal)).IsTrue();
    }
}
