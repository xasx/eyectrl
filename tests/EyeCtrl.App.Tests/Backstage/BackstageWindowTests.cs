namespace EyeCtrl.App.Tests.Backstage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// The fullscreen Backstage window (Ticket 03): the round neutral close disc, the status strip's
/// show/hide rules, the two modal shapes, Quit/Restart/Calibrate dispatch, and the restart notices
/// that must be SHOWN while Backstage owns the screen. Every showing test goes through
/// <see cref="InvisibleTestWindow.Prepare"/>, so the owner's gaze is never seized by a test.
/// </summary>
// WpfApplication is DisableParallelization, which is also what DpiHelperWriterIsolationTests
// requires of this file: it calls DpiHelper.Update in the DPI-change pin. It gives every window
// test the Application's own dispatcher thread, which the DpiHelper collection does not.
[Collection("WpfApplication")]
public class BackstageWindowTests
{
    private static EyeCtrlSettings NewSettings() => new();

    private sealed class Harness : IDisposable
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public EyeCtrlSettings Settings { get; } = NewSettings();
        public BackstageViewModel ViewModel { get; }
        public BackstageWindow Window { get; }
        public List<string> Shutdowns { get; } = new();
        public List<bool> Restarts { get; } = new();
        public bool OfferSupervisor { get; set; }
        public List<string> Calibrations { get; } = new();
        public List<string> Dismissals { get; } = new();

        public Harness(Func<DateTime>? clockNow = null)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            ViewModel = new BackstageViewModel(Messenger, Settings);
            Window = InvisibleTestWindow.Prepare(new BackstageWindow());
            if (clockNow is not null)
                Window.ClockNow = clockNow;
            Window.Configure(
                Messenger,
                ViewModel,
                () => new BackstageStatusView(
                    new BackstageStatusSnapshot("Version 1.0", "Installed · Standard", "Supervisor connected"),
                    ShowSupervisor: true),
                () => OfferSupervisor,
                () => Shutdowns.Add("shutdown"),
                startSupervisor => Restarts.Add(startSupervisor),
                () => RestartNotices.TargetLanguage(Settings.General.UiLanguage));
            Window.DismissRequested += () => Dismissals.Add("dismiss");
            Window.CalibrateRequested += () => Calibrations.Add("calibrate");
            Window.ShowBackstage();
            Window.UpdatePositions();
        }

        public void Dispose()
        {
            Window.HideBackstage();
            ViewModel.Dispose();
        }
    }

    [StaFact]
    public void CloseDisc_IsTheSharedRoundNeutralDwellArcButton()
    {
        using var h = new Harness();
        Check.That(h.Window.IsVisible).IsTrue();
        Check.That(h.Window.CloseDisc).IsNotNull();
        var disc = h.Window.CloseDisc!;

        Check.That(disc.Background.Width).IsEqualTo(UIConstants.ButtonRadius * 2);
        Check.That(disc.Background.Height).IsEqualTo(UIConstants.ButtonRadius * 2);

        // Compare against the WINDOW's resolved resources, never Tokens.* — the #392 lesson:
        // WPF materialises the {x:Static} palette entry on the key's first lookup, under the
        // theme active then, and a Tokens comparison is order-dependent across the suite.
        Check.That(disc.Background.Fill).IsSameReferenceAs(h.Window.TryFindResource("BrushSurfaceMedium"));
        Check.That(disc.Background.Stroke).IsSameReferenceAs(h.Window.TryFindResource("BrushBorderSubtle"));
        Check.That(disc.ProgressPath.Stroke).IsSameReferenceAs(h.Window.TryFindResource("BrushAccent"));
        Check.That(ReferenceEquals(disc.ProgressPath.Stroke,
            h.Window.TryFindResource("BrushAccentDanger"))).IsFalse();
        Check.That(disc.RimHalo.Stroke).IsNotNull();
        // The Close glyph is fill-only (ActionIcons.Close); its stroke layer is legitimately null.
        Check.That(disc.IconFill).IsNotNull();
        Check.That(disc.IconFill!.Fill).IsSameReferenceAs(h.Window.TryFindResource("BrushTextPrimary"));
        Check.That(disc.AutomationAnchor?.Text).IsEqualTo(StringTable.Current.T("overlays.edge.close"));
    }

    [StaFact]
    public void Window_IsFullscreen_NotClickThrough_AndEntersTheUiAccessBand()
    {
        using var h = new Harness();

        Check.That(h.Window.WindowStyle).IsEqualTo(WindowStyle.None);
        Check.That(h.Window.AllowsTransparency).IsTrue();
        Check.That(h.Window.Topmost).IsTrue();
        Check.That(h.Window.ResizeMode).IsEqualTo(ResizeMode.NoResize);
        Check.That(h.Window.IsClickThrough).IsFalse();
        Check.That(h.Window.Width).IsEqualTo(SystemParameters.PrimaryScreenWidth);
        Check.That(h.Window.Height).IsEqualTo(SystemParameters.PrimaryScreenHeight);
    }

    [StaFact]
    public void Opening_RegistersTheThreeSessionCards_AndTheCloseDisc()
    {
        using var h = new Harness();
        h.Window.UpdatePositions();

        var ids = h.ViewModel.DetectorForTest.Targets.Select(t => t.Id).ToList();
        Check.That(ids).Contains(BackstageViewModel.QuitCardId);
        Check.That(ids).Contains(BackstageViewModel.RestartCardId);
        Check.That(ids).Contains(BackstageViewModel.CalibrateCardId);
        Check.That(ids).Contains(BackstageViewModel.CloseTargetId);
    }

    [StaFact]
    public void StatusStrip_HidesTheSupervisorBadge_WhenTheServiceWasNotComposed()
    {
        using var h = new Harness();
        WpfApplicationGate.EnsureApplicationWithTokens();

        // A fresh window built without Show semantics would still need Configure; reuse the
        // harness's window but drive its provider through a second window is not possible, so this
        // test checks the public rule through a separately configured window.
        var vm = new BackstageViewModel(h.Messenger, h.Settings);
        var window = InvisibleTestWindow.Prepare(new BackstageWindow());
        try
        {
            window.Configure(
                h.Messenger,
                vm,
                () => new BackstageStatusView(
                    new BackstageStatusSnapshot("Version 1.0", "Dev build", "Supervisor not installed"),
                    ShowSupervisor: false),
                () => false,
                () => { },
                _ => { },
                () => UiLanguage.English);
            window.ShowBackstage();
            window.RefreshStatus();

            var supervisorHost = (ContentControl)window.FindName("SupervisorBadgeHost");
            Check.That(supervisorHost.Content).IsNull();
            Check.That(supervisorHost.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally
        {
            window.HideBackstage();
            vm.Dispose();
        }
    }

    [StaFact]
    public void PausedBadge_FollowsTheVmSampleState()
    {
        using var h = new Harness();
        var host = (ContentControl)h.Window.FindName("PausedBadgeHost");
        Check.That(host.Visibility).IsEqualTo(Visibility.Collapsed);

        h.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(10, 10), 0) { IsPaused = true });
        Check.That(host.Visibility).IsEqualTo(Visibility.Visible);

        h.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(10, 10), 30_000) { IsPaused = false });
        Check.That(host.Visibility).IsEqualTo(Visibility.Collapsed);
    }

    [StaFact]
    public void Clock_IsReadOnly_AndAddsNoGazeTarget()
    {
        using var h = new Harness();
        var clock = (FrameworkElement)h.Window.FindName("ClockBlock");

        Check.That(clock.IsHitTestVisible).IsFalse();
        var ids = h.ViewModel.DetectorForTest.Targets.Select(t => t.Id).ToList();
        Check.That(ids).HasSize(4);   // three cards + close disc, nothing else
        Check.WithCustomMessage("A clock-shaped dwell target appeared: the read-only clock must never be one.")
            .That(ids.Count(id => id.Contains("clock", StringComparison.OrdinalIgnoreCase))).IsEqualTo(0);
    }

    [StaFact]
    public void ShowBackstage_RendersTheClockImmediately_WithoutWaitingForATick()
    {
        var fixedTime = new DateTime(2025, 11, 25, 14, 32, 0);
        using var h = new Harness(() => fixedTime);
        var expected = BackstageClock.Format(
            fixedTime, StringTable.Current.FormatProviderFor("overlays"));

        Check.That(h.Window.ClockTimeForTest).IsEqualTo(expected.Time);
        Check.That(h.Window.ClockDateForTest).IsEqualTo(expected.Date);
    }

    [StaFact]
    public void Clock_ChangesOnlyWhenTheRenderedTextChanges_AndStopsWhenHidden()
    {
        using var h = new Harness();
        var window = h.Window;
        var fixedTime = new DateTime(2026, 11, 25, 14, 32, 0);

        window.ClockNow = () => fixedTime;
        window.RefreshClock();
        var expected = BackstageClock.Format(
            fixedTime, StringTable.Current.FormatProviderFor("overlays"));
        Check.That(window.ClockTimeForTest).IsEqualTo(expected.Time);
        Check.That(window.ClockDateForTest).IsEqualTo(expected.Date);
        var firstTime = window.ClockTimeForTest;

        var changes = window.ClockTextChangesForTest;
        window.RefreshClock();
        Check.WithCustomMessage("A same-minute poll re-rendered identical clock text.")
            .That(window.ClockTextChangesForTest).IsEqualTo(changes);

        window.ClockNow = () => fixedTime.AddMinutes(1);
        window.RefreshClock();
        Check.That(window.ClockTextChangesForTest).IsEqualTo(changes + 1);
        Check.That(window.ClockTimeForTest).IsNotEqualTo(firstTime);

        // After close the clock is not a live surface: a queued/late tick must not touch it.
        window.HideBackstage();
        var timeBlock = (TextBlock)window.FindName("ClockTimeText");
        var dateBlock = (TextBlock)window.FindName("ClockDateText");
        timeBlock.Text = "sentinel-time";
        dateBlock.Text = "sentinel-date";
        var afterClose = window.ClockTextChangesForTest;
        window.ClockNow = () => fixedTime.AddHours(2);
        window.RefreshClock();

        Check.That(timeBlock.Text).IsEqualTo("sentinel-time");
        Check.That(dateBlock.Text).IsEqualTo("sentinel-date");
        Check.That(window.ClockTextChangesForTest).IsEqualTo(afterClose);
    }

    [StaFact]
    public void HeaderAtThe1280Minimum_ClockClearsTheTitleAndClose_AndWidestGermanBadgesFit()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new BackstageViewModel(messenger, settings);
        var window = InvisibleTestWindow.Prepare(new BackstageWindow());
        try
        {
            window.Configure(
                messenger,
                vm,
                () => new BackstageStatusView(
                    new BackstageStatusSnapshot(
                        "Version 10.99.9999.9999",
                        "Installiert · UIAccess aktiv",
                        "Supervisor antwortet nicht"),
                    ShowSupervisor: true),
                () => false, () => { }, _ => { }, () => UiLanguage.German);
            window.ShowBackstage();
            window.Width = BackstageWindow.ReferenceScreenWidthDip;
            window.Height = 760;

            var date = (TextBlock)window.FindName("ClockDateText");
            date.Text = "Donnerstag, 30. September";   // the longest German date at the ruling
            window.UpdateLayout();

            var titleGroup = (FrameworkElement)window.FindName("TitleGroup");
            var titleText = (FrameworkElement)window.FindName("TitleText");
            var statusStrip = (FrameworkElement)window.FindName("StatusStrip");
            var clock = (FrameworkElement)window.FindName("ClockBlock");
            var close = (FrameworkElement)window.FindName("CloseHost");
            var titleRect = RectInWindow(titleText, window);
            var statusRect = RectInWindow(statusStrip, window);
            var clockRect = RectInWindow(clock, window);
            var closeRect = RectInWindow(close, window);

            Check.WithCustomMessage($"clock {clockRect} overlaps the title text {titleRect}.")
                .That(clockRect.Left >= titleRect.Right).IsTrue();
            Check.WithCustomMessage($"clock {clockRect} reaches the close disc at {closeRect}.")
                .That(clockRect.Right <= closeRect.Left).IsTrue();
            Check.WithCustomMessage($"clock {clockRect} overlaps the status strip {statusRect}.")
                .That(clockRect.Bottom <= statusRect.Top).IsTrue();
            Check.WithCustomMessage("The clock runs past the right content margin.")
                .That(clockRect.Right <= BackstageWindow.ReferenceScreenWidthDip - BackstageWindow.OuterMarginDip)
                .IsTrue();

            Check.That(titleGroup.ActualWidth).IsStrictlyGreaterThan(0);
            Check.That(date.Text).IsEqualTo("Donnerstag, 30. September");
            var cell = titleGroup.ActualWidth / 3;
            foreach (var hostName in new[] { "VersionBadgeHost", "InstallBadgeHost", "SupervisorBadgeHost" })
            {
                var host = (ContentControl)window.FindName(hostName);
                var badge = (FrameworkElement)host.Content;
                Check.WithCustomMessage($"{hostName} drew no pill: the fit assertion would be vacuous.")
                    .That(badge.ActualWidth).IsStrictlyGreaterThan(0);
                Check.WithCustomMessage($"{hostName}: pill {badge.ActualWidth} vs strip cell {cell}.")
                    .That(badge.ActualWidth <= cell).IsTrue();
            }
        }
        finally
        {
            window.HideBackstage();
            vm.Dispose();
        }
    }

    private static Rect RectInWindow(FrameworkElement element, Window window) =>
        element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));

    [StaFact]
    public void Quit_OpensTheDestructiveConfirmation_AndTheConfirmCallsShutdown()
    {
        using var h = new Harness();

        h.ViewModel.ActivateForMouse(BackstageViewModel.QuitCardId);
        Check.That(h.ViewModel.IsModalOpen).IsTrue();
        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.QuitModalId);
        Check.That(h.Window.ModalChoiceIds)
            .ContainsExactly(BackstageWindow.QuitConfirmChoiceId, BackstageWindow.CancelChoiceId);

        h.ViewModel.ActivateForMouse(BackstageWindow.QuitConfirmChoiceId);
        Check.That(h.Shutdowns).ContainsExactly("shutdown");
        Check.That(h.Restarts).IsEmpty();
    }

    [StaFact]
    public void Quit_Cancel_DismissesWithoutShuttingDown()
    {
        using var h = new Harness();

        h.ViewModel.ActivateForMouse(BackstageViewModel.QuitCardId);
        h.ViewModel.ActivateForMouse(BackstageWindow.CancelChoiceId);

        Check.That(h.Shutdowns).IsEmpty();
        Check.That(h.ViewModel.IsModalOpen).IsFalse();
    }

    [StaFact]
    public void Restart_WithoutASupervisorOffered_IsTwoChoices_AndCallsRestartFalse()
    {
        using var h = new Harness { OfferSupervisor = false };

        h.ViewModel.ActivateForMouse(BackstageViewModel.RestartCardId);
        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.RestartModalId);
        Check.That(h.Window.ModalChoiceIds)
            .ContainsExactly(BackstageWindow.RestartChoiceId, BackstageWindow.CancelChoiceId);

        h.ViewModel.ActivateForMouse(BackstageWindow.RestartChoiceId);
        Check.That(h.Restarts).ContainsExactly(false);
    }

    [StaFact]
    public void Restart_WithASupervisorOffered_IsThreeChoices_AndTheSupervisorChoiceCallsRestartTrue()
    {
        using var h = new Harness { OfferSupervisor = true };

        h.ViewModel.ActivateForMouse(BackstageViewModel.RestartCardId);
        Check.That(h.Window.ModalChoiceIds).ContainsExactly(
            BackstageWindow.RestartSupervisorChoiceId,
            BackstageWindow.RestartChoiceId,
            BackstageWindow.CancelChoiceId);

        h.ViewModel.ActivateForMouse(BackstageWindow.RestartSupervisorChoiceId);
        Check.That(h.Restarts).ContainsExactly(true);
    }

    [StaFact]
    public void Calibrate_RaisesCalibrateRequested_WithNoConfirmation()
    {
        using var h = new Harness();

        h.ViewModel.ActivateForMouse(BackstageViewModel.CalibrateCardId);
        Check.That(h.Calibrations).ContainsExactly("calibrate");
        Check.That(h.ViewModel.IsModalOpen).IsFalse();
    }

    [StaFact]
    public void RestartUnavailable_WhileOpen_IsShown_AndIsNotSwallowed()
    {
        // The acceptance pin. RestartNoticeHost suppresses its floating card while Backstage owns
        // the screen and does NOT replay on close; this window's subscription is the only surface
        // that can show the refusal the owner just triggered from a Backstage card. Remove the
        // registration and this test must go red.
        using var h = new Harness();
        var reason = "The new EyeCtrl did not start in time.";

        h.Messenger.Send(new RestartUnavailableMessage(reason));

        Check.That(h.ViewModel.IsModalOpen).IsTrue();
        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.RestartRefusedModalId);
        Check.That(h.Window.ModalBodyText).IsEqualTo(reason);
        var target = RestartNotices.TargetLanguage(h.Settings.General.UiLanguage);
        Check.That(h.Window.ModalChoiceIds).ContainsExactly("restart_failed_ok");
        Check.That(h.ViewModel.DetectorForTest.Targets.Single().Id).IsEqualTo("restart_failed_ok");
        Check.That(RestartNotices.RefusedChoices(target)[0].Id).IsEqualTo("restart_failed_ok");
    }

    [StaFact]
    public void RestartInProgress_WhileOpen_IsShown()
    {
        using var h = new Harness();

        h.Messenger.Send(new RestartInProgressMessage());

        Check.That(h.ViewModel.IsModalOpen).IsTrue();
        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.RestartInProgressModalId);
        Check.That(h.Window.ModalChoiceIds).ContainsExactly("restart_progress_hide");
    }

    [StaFact]
    public void Closing_UnregistersTheRestartNotices_SoTheyCannotShowOnAClosedMenu()
    {
        // "Unregistered early" is as bad as missing: a notice delivered after the menu closed must
        // go to the App-level floating card, not into a window nobody can see.
        var h = new Harness();
        h.Window.HideBackstage();
        h.ViewModel.Dispose();

        h.Messenger.Send(new RestartUnavailableMessage("late reason"));

        Check.That(h.ViewModel.IsModalOpen).IsFalse();
    }

    [StaFact]
    public void Escape_DismissesTheOpenModalFirst_AndOtherwiseRequestsClose()
    {
        using var h = new Harness();

        h.ViewModel.ActivateForMouse(BackstageViewModel.QuitCardId);
        h.Window.HandleEscape();
        Check.That(h.ViewModel.IsModalOpen).IsFalse();
        Check.That(h.Dismissals).IsEmpty();

        h.Window.HandleEscape();
        Check.That(h.Dismissals).ContainsExactly("dismiss");
    }

    [StaFact]
    public void DpiChange_RebuildsTheCloseDiscRadius_NotJustTheCentre()
    {
        // The circular target is only centre-updated by UpdateCloseButtonPosition; without the
        // RestoreMainSurface pass after the position push, its radius stays at the old DPI.
        using var h = new Harness();
        var before = h.ViewModel.DetectorForTest.Targets
            .Single(t => t.Id == BackstageViewModel.CloseTargetId).Radius;
        Check.That(before).IsCloseTo(DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius), 1e-9);

        double expected;
        double after;
        try
        {
            DpiHelper.Update(2.0, 2.0);
            expected = DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);
            h.Window.ApplyDpiChangeAfterLayout();
            after = h.ViewModel.DetectorForTest.Targets
                .Single(t => t.Id == BackstageViewModel.CloseTargetId).Radius;
        }
        finally
        {
            DpiHelper.Update(1.0, 1.0);
        }

        Check.That(after).IsCloseTo(expected, 1e-9);
        Check.That(after).IsStrictlyGreaterThan(before);
    }

    [StaFact]
    public void MouseClickingACard_RunsTheSameClearBeforeDispatchAsADwell()
    {
        // Calibrate does not open a modal, so the handler sees the target state the clear left
        // behind. A card that opens a modal registers the modal's own choices inside the window's
        // handler, which runs before a later subscriber — that is the modal-swap contract, not a
        // failure to clear the main surface.
        using var h = new Harness();
        var targetsAtDispatch = -1;
        h.ViewModel.CardActivated += _ => targetsAtDispatch = h.ViewModel.DetectorForTest.Targets.Count;

        h.ViewModel.ActivateForMouse(BackstageViewModel.CalibrateCardId);

        Check.That(targetsAtDispatch).IsEqualTo(0);
        Check.That(h.Calibrations).ContainsExactly("calibrate");
    }
}
