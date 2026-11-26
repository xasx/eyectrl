namespace EyeCtrl.App.Tests.Backstage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// Ticket 04's Install &amp; Maintenance row (migrated from the Settings „Erweitert" rows): the
/// visibility matrix (installed or not; a verified offer, a pending probe, none; the policy block on
/// BOTH rows; the previous folder), the same-height non-interactive probe placeholder, the
/// confirmations (reusing the already-switched-on update/rollback wording and EyeCtrl's own
/// uninstall question), the hand-offs each activation starts, and the deferred rebuild that must
/// never clobber an open confirmation.
/// <para>
/// Every test goes through <see cref="InvisibleTestWindow.Prepare"/>, so the owner's gaze is never
/// seized by a test window.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class BackstageInstallRowTests
{
    private sealed class FakeGateways : ISetupGateways
    {
        public bool Installed { get; set; } = true;
        public bool PreviousAvailable { get; set; }
        public string? PreviousVersion { get; set; } = "1.0.0";
        public bool PolicyBlocks { get; set; }
        public SetupUpdateOffer? UpdateOffer { get; set; }
        public bool UpdateOfferPending { get; set; }
        public int ProbeStarts { get; private set; }
        public Action? WhenDone { get; private set; }

        public List<SetupUpdateOffer> UpdateHandoffs { get; } = [];
        public int RollbackHandoffs { get; private set; }
        public int UninstallHandoffs { get; private set; }
        public SetupHandoffStart UpdateHandoffResult { get; set; } = SetupHandoffStart.Started;
        public SetupHandoffStart RollbackHandoffResult { get; set; } = SetupHandoffStart.Started;
        public SetupHandoffStart UninstallHandoffResult { get; set; } = SetupHandoffStart.Started;

        public void BeginUpdateOfferProbe(Action whenDone)
        {
            ProbeStarts++;
            WhenDone = whenDone;
        }

        public SetupHandoffStart StartUpdateHandoff(SetupUpdateOffer offer)
        {
            UpdateHandoffs.Add(offer);
            return UpdateHandoffResult;
        }

        public SetupHandoffStart StartRollbackHandoff()
        {
            RollbackHandoffs++;
            return RollbackHandoffResult;
        }

        public SetupHandoffStart StartUninstallHandoff()
        {
            UninstallHandoffs++;
            return UninstallHandoffResult;
        }
    }

    private sealed class RecordingHandoff : IUninstallHandoffPresenter
    {
        public int Starts { get; private set; }
        public int Cancels { get; private set; }
        public ISetupGateways? LastGateways { get; private set; }
        public SetupHandoffStart Result { get; set; } = SetupHandoffStart.Started;

        public SetupHandoffStart Start(ISetupGateways gateways)
        {
            Starts++;
            LastGateways = gateways;
            return Result;
        }

        public void CancelWatch() => Cancels++;

        public void Dispose() { }
    }

    private sealed class Harness : IDisposable
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public EyeCtrlSettings Settings { get; } = new();
        public BackstageViewModel ViewModel { get; }
        public BackstageWindow Window { get; }
        public FakeGateways? Gateways { get; }
        public RecordingHandoff? Handoff { get; }
        public bool Paused { get; set; }

        public Harness(FakeGateways? gateways = null, RecordingHandoff? handoff = null)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            Gateways = gateways;
            Handoff = handoff;
            ViewModel = new BackstageViewModel(Messenger, Settings);
            Window = InvisibleTestWindow.Prepare(new BackstageWindow());
            Window.Configure(
                Messenger,
                ViewModel,
                () => new BackstageStatusView(
                    new BackstageStatusSnapshot("Version 1.0", "Installed · Standard", "Supervisor connected"),
                    ShowSupervisor: true),
                () => false,
                () => { },
                _ => { },
                () => UiLanguage.English,
                gateways,
                handoff,
                () => Paused);
            Window.ShowBackstage();
            Window.UpdatePositions();
        }

        /// <summary>Completes the probe on the same thread, then drains the window's queued action.</summary>
        public void CompleteProbe()
        {
            Gateways!.WhenDone!.Invoke();
            Pump();
        }

        public void Pump() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        public PhysicalRect? Bounds(string cardId) =>
            ViewModel.DetectorForTest.Targets.SingleOrDefault(t => t.Id == cardId)?.Bounds;

        public bool IsRegistered(string cardId) =>
            ViewModel.DetectorForTest.Targets.Any(t => t.Id == cardId);

        public void Dispose()
        {
            Window.HideBackstage();
            ViewModel.Dispose();
        }
    }

    private static readonly SetupUpdateOffer Offer =
        new(@"C:\inbox\2.0.0", @"C:\inbox\2.0.0\EyeCtrl.Setup.exe", "2.0.0", "1.0.0");

    [StaFact]
    public void DevBuild_HidesTheWholeInstallSection_WithNoInstallTargets()
    {
        // The ticket's chosen option: a dev build shows no install surface at all. A dead card is
        // worse than an absent one for a gaze user.
        var gateways = new FakeGateways
        {
            Installed = false,
            UpdateOffer = Offer,
            PreviousAvailable = true,
        };
        using var h = new Harness(gateways);

        Check.That(h.Window.InstallSectionVisible).IsFalse();
        Check.That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNull();
        Check.That(h.Window.CardFace(BackstageViewModel.RollbackCardId)).IsNull();
        Check.That(h.Window.CardFace(BackstageViewModel.UninstallCardId)).IsNull();
        Check.That(h.IsRegistered(BackstageViewModel.UpdateCardId)).IsFalse();
        Check.That(h.IsRegistered(BackstageViewModel.RollbackCardId)).IsFalse();
        Check.That(h.IsRegistered(BackstageViewModel.UninstallCardId)).IsFalse();
        Check.WithCustomMessage("the probe must not run on a build that can never offer an update")
            .That(gateways.ProbeStarts).IsEqualTo(0);
    }

    [StaFact]
    public void InstalledWithoutOfferOrPrevious_ShowsOnlyUninstall()
    {
        using var h = new Harness(new FakeGateways { Installed = true });
        h.Pump();

        Check.That(h.Window.InstallSectionVisible).IsTrue();
        Check.That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNull();
        Check.That(h.Window.CardFace(BackstageViewModel.RollbackCardId)).IsNull();
        var uninstall = h.Window.CardFace(BackstageViewModel.UninstallCardId);
        Check.That(uninstall).IsNotNull();
        Check.That(uninstall!.Root.BorderBrush).IsSameReferenceAs(Tokens.BrushAccentDanger);
        Check.That(h.IsRegistered(BackstageViewModel.UninstallCardId)).IsTrue();
        var bounds = h.Bounds(BackstageViewModel.UninstallCardId);
        Check.That(bounds).IsNotNull();
        Check.That(bounds!.Value.Width).IsStrictlyGreaterThan(0);
        Check.That(h.Gateways!.ProbeStarts).IsEqualTo(1);
    }

    [StaFact]
    public void ProbeCompletion_RegistersTheNewCardsWithLiveRectangles()
    {
        // Review-01 F1: the rebuild runs in the same dispatcher turn that adds the faces. A
        // synchronous UpdatePositions reads ActualWidth/Height == 0 and registers the whole row
        // (including the Uninstall card that worked before) at a zero-size rectangle. This pins
        // the registered Bounds, not just the id: the id-only assertion passed against the bug.
        var gateways = new FakeGateways
        {
            Installed = true,
            UpdateOfferPending = true,
            PreviousAvailable = true,
        };
        using var h = new Harness(gateways);
        h.Pump();

        // Control: the open path's Uninstall rectangle is live.
        var before = h.Bounds(BackstageViewModel.UninstallCardId);
        Check.WithCustomMessage("the open path's Uninstall card is already dead")
            .That(before).IsNotNull();
        Check.That(before!.Value.Width).IsStrictlyGreaterThan(0);

        h.Gateways!.UpdateOffer = Offer;
        h.Gateways.UpdateOfferPending = false;
        h.CompleteProbe();
        h.Pump();

        Check.That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNotNull();
        var update = h.Bounds(BackstageViewModel.UpdateCardId);
        Check.WithCustomMessage("the rebuilt Update card is registered at a zero-size rectangle")
            .That(update).IsNotNull();
        Check.That(update!.Value.Width).IsStrictlyGreaterThan(0);
        Check.That(update.Value.Height).IsStrictlyGreaterThan(0);

        var rollback = h.Bounds(BackstageViewModel.RollbackCardId);
        Check.That(rollback).IsNotNull();
        Check.That(rollback!.Value.Width).IsStrictlyGreaterThan(0);

        var uninstall = h.Bounds(BackstageViewModel.UninstallCardId);
        Check.WithCustomMessage("the rebuild left the previously working Uninstall card at zero")
            .That(uninstall).IsNotNull();
        Check.That(uninstall!.Value.Width).IsStrictlyGreaterThan(0);
    }

    [StaFact]
    public void VerifiedNewerOffer_ShowsTheUpdateCard_WithBothVersionsInTheDescription()
    {
        using var h = new Harness(new FakeGateways { Installed = true, UpdateOffer = Offer });
        h.Pump();

        var update = h.Window.CardFace(BackstageViewModel.UpdateCardId);
        Check.That(update).IsNotNull();
        Check.That(update!.Title.Text)
            .IsEqualTo(StringTable.Current.T("overlays.backstage.update.title"));
        Check.That(update.Description.Text).Contains("2.0.0").And.Contains("1.0.0");
        Check.That(h.IsRegistered(BackstageViewModel.UpdateCardId)).IsTrue();
        var bounds = h.Bounds(BackstageViewModel.UpdateCardId);
        Check.That(bounds).IsNotNull();
        Check.That(bounds!.Value.Width).IsStrictlyGreaterThan(0);
    }

    [StaFact]
    public void WhileTheProbeRuns_ASameHeightNonInteractivePlaceholderStandsInForTheUpdateCard()
    {
        var gateways = new FakeGateways { Installed = true, UpdateOfferPending = true };
        using var h = new Harness(gateways);

        Check.That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNull();
        Check.WithCustomMessage("a placeholder that registered a dwell target would start nothing")
            .That(h.IsRegistered(BackstageViewModel.UpdateCardId)).IsFalse();

        var placeholder = Placeholder(h.Window);
        Check.That(placeholder).IsNotNull();
        Check.That(placeholder!.MinHeight).IsEqualTo(BackstageWindow.CardMinHeightDip);
        Check.That(placeholder.Child).IsInstanceOf<TextBlock>();
        Check.That(((TextBlock)placeholder.Child).Text)
            .IsEqualTo(StringTable.Current.T("overlays.backstage.update.checking"));
    }

    [StaFact]
    public void AFinishedProbeWithoutAnOffer_ShowsNoUpdateCard_AndKeepsUninstallInPlace()
    {
        var gateways = new FakeGateways
        {
            Installed = true,
            UpdateOfferPending = true,
            PreviousAvailable = true,
        };
        using var h = new Harness(gateways);
        h.Gateways!.UpdateOffer = null;
        h.Gateways.UpdateOfferPending = false;

        h.CompleteProbe();

        Check.That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNull();
        Check.That(Placeholder(h.Window)).IsNull();
        Check.That(h.Window.CardFace(BackstageViewModel.UninstallCardId)).IsNotNull();
        Check.That(h.IsRegistered(BackstageViewModel.UninstallCardId)).IsTrue();
    }

    [StaFact]
    public void BlockedPolicy_ShowsOneBlockedLine_AndNeitherUpdateNorRollbackCard()
    {
        var gateways = new FakeGateways
        {
            Installed = true,
            UpdateOffer = Offer,
            PreviousAvailable = true,
            PolicyBlocks = true,
        };
        using var h = new Harness(gateways);

        Check.That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNull();
        Check.That(h.Window.CardFace(BackstageViewModel.RollbackCardId)).IsNull();
        var blocked = InstallChildren(h.Window)
            .OfType<TextBlock>()
            .Where(t => AutomationProperties.GetAutomationId(t) == "Backstage_InstallBlocked")
            .ToList();
        Check.That(blocked).HasSize(1);
        Check.That(blocked[0].Text)
            .IsEqualTo(StringTable.Current.T("overlays.backstage.update.blocked"));
        Check.That(h.Window.CardFace(BackstageViewModel.UninstallCardId)).IsNotNull();
    }

    [StaFact]
    public void BlockedPolicy_RollbackOnly_StillShowsTheBlockedLine()
    {
        var gateways = new FakeGateways
        {
            Installed = true,
            PreviousAvailable = true,
            PolicyBlocks = true,
        };
        using var h = new Harness(gateways);

        Check.That(h.Window.CardFace(BackstageViewModel.RollbackCardId)).IsNull();
        Check.That(InstallChildren(h.Window).OfType<TextBlock>()
            .Any(t => AutomationProperties.GetAutomationId(t) == "Backstage_InstallBlocked")).IsTrue();
    }

    [StaFact]
    public void PreviousPresent_ShowsTheRollbackCard_NamingThePreviousVersion()
    {
        using var h = new Harness(new FakeGateways
        {
            Installed = true,
            PreviousAvailable = true,
            PreviousVersion = "1.2.3",
        });

        var rollback = h.Window.CardFace(BackstageViewModel.RollbackCardId);
        Check.That(rollback).IsNotNull();
        Check.That(rollback!.Title.Text)
            .IsEqualTo(StringTable.Current.T("overlays.backstage.rollback.title"));
        Check.That(rollback.Description.Text).Contains("1.2.3");
        Check.That(h.IsRegistered(BackstageViewModel.RollbackCardId)).IsTrue();
    }

    [StaFact]
    public void UpdateActivation_OpensTheApprovedConfirm_AndConfirmHandsOffTheOffer()
    {
        var gateways = new FakeGateways { Installed = true, UpdateOffer = Offer };
        using var h = new Harness(gateways);

        h.ViewModel.ActivateForMouse(BackstageViewModel.UpdateCardId);

        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.UpdateModalId);
        Check.That(h.Window.ModalChoiceIds).ContainsExactly(
            BackstageWindow.UpdateInstallChoiceId, BackstageWindow.CancelChoiceId);
        Check.That(h.Window.ModalChoiceLabel(BackstageWindow.UpdateInstallChoiceId))
            .IsEqualTo(StringTable.Current.T("update.confirm.install"));
        Check.That(h.Window.ModalChoiceLabel(BackstageWindow.CancelChoiceId))
            .IsEqualTo(StringTable.Current.T("update.confirm.later"));
        Check.That(h.Window.ModalBodyText)
            .Contains(StringTable.Current.T("update.confirm.detail"));

        Check.That(gateways.UpdateHandoffs).IsEmpty();
        h.ViewModel.ActivateForMouse(BackstageWindow.UpdateInstallChoiceId);
        Check.That(gateways.UpdateHandoffs).HasSize(1);
        Check.That(gateways.UpdateHandoffs[0].Directory).IsEqualTo(Offer.Directory);
    }

    [StaFact]
    public void RollbackActivation_OpensTheApprovedConfirm_AndGoHandsOff()
    {
        var gateways = new FakeGateways
        {
            Installed = true,
            PreviousAvailable = true,
            PreviousVersion = "1.2.3",
        };
        using var h = new Harness(gateways);

        h.ViewModel.ActivateForMouse(BackstageViewModel.RollbackCardId);

        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.RollbackModalId);
        Check.That(h.Window.ModalChoiceIds).ContainsExactly(
            BackstageWindow.RollbackGoChoiceId, BackstageWindow.CancelChoiceId);
        Check.That(h.Window.ModalChoiceLabel(BackstageWindow.RollbackGoChoiceId))
            .IsEqualTo(StringTable.Current.T("rollback.confirm.go"));

        Check.That(gateways.RollbackHandoffs).IsEqualTo(0);
        h.ViewModel.ActivateForMouse(BackstageWindow.RollbackGoChoiceId);
        Check.That(gateways.RollbackHandoffs).IsEqualTo(1);
    }

    [StaFact]
    public void UninstallActivation_OpensItsOwnConfirm_AndGoesThroughThePresenter()
    {
        var gateways = new FakeGateways { Installed = true };
        var handoff = new RecordingHandoff();
        using var h = new Harness(gateways, handoff);

        h.ViewModel.ActivateForMouse(BackstageViewModel.UninstallCardId);

        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.UninstallModalId);
        Check.That(h.Window.ModalChoiceIds).ContainsExactly(
            BackstageWindow.UninstallConfirmChoiceId, BackstageWindow.CancelChoiceId);
        Check.That(h.Window.ModalChoiceLabel(BackstageWindow.UninstallConfirmChoiceId))
            .IsEqualTo(StringTable.Current.T("overlays.backstage.uninstall.confirm.action"));
        Check.WithCustomMessage("the uninstall body must be exactly the approved question")
            .That(h.Window.ModalBodyText)
            .IsEqualTo(StringTable.Current.T("overlays.backstage.uninstall.confirm.body"));
        Check.That(h.Window.ModalBodyText)
            .Not.Contains(StringTable.Current.T("overlays.backstage.uninstall.confirm.paused"));

        h.ViewModel.ActivateForMouse(BackstageWindow.UninstallConfirmChoiceId);

        Check.That(handoff.Starts).IsEqualTo(1);
        Check.That(handoff.LastGateways).IsSameReferenceAs(gateways);
        Check.That(gateways.UninstallHandoffs).IsEqualTo(0);
    }

    [StaFact]
    public void UninstallActivation_WhilePaused_AddsThePausedNote()
    {
        using var h = new Harness(new FakeGateways { Installed = true }) { Paused = true };

        h.ViewModel.ActivateForMouse(BackstageViewModel.UninstallCardId);

        Check.That(h.Window.ModalBodyText).IsEqualTo(
            StringTable.Current.T("overlays.backstage.uninstall.confirm.body")
            + "\n\n"
            + StringTable.Current.T("overlays.backstage.uninstall.confirm.paused"));
    }

    [StaFact]
    public void AFailedUpdateLaunch_ShowsTheSingleDismissAcknowledgement()
    {
        var gateways = new FakeGateways
        {
            Installed = true,
            UpdateOffer = Offer,
            UpdateHandoffResult = SetupHandoffStart.Failed,
        };
        using var h = new Harness(gateways);

        h.ViewModel.ActivateForMouse(BackstageViewModel.UpdateCardId);
        h.ViewModel.ActivateForMouse(BackstageWindow.UpdateInstallChoiceId);

        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.InstallFailedModalId);
        Check.That(h.Window.ModalBodyText)
            .IsEqualTo(StringTable.Current.T("overlays.backstage.install.startFailed"));
        Check.That(h.Window.ModalChoiceIds).ContainsExactly(BackstageWindow.InstallDismissChoiceId);
    }

    [StaFact]
    public void ADuplicateActivation_WhileSetupIsAlreadyActive_IsSilent()
    {
        // The sitting's exact case: a resting gaze re-fired the row 2.1 s after a good start.
        // Setup's own window is the answer; the owner must not be told the launch failed.
        var gateways = new FakeGateways
        {
            Installed = true,
            UpdateOffer = Offer,
            PreviousAvailable = true,
            UpdateHandoffResult = SetupHandoffStart.RefusedWhileActive,
            RollbackHandoffResult = SetupHandoffStart.RefusedWhileActive,
            UninstallHandoffResult = SetupHandoffStart.RefusedWhileActive,
        };
        using var h = new Harness(gateways);

        h.ViewModel.ActivateForMouse(BackstageViewModel.UpdateCardId);
        h.ViewModel.ActivateForMouse(BackstageWindow.UpdateInstallChoiceId);
        Check.That(h.ViewModel.IsModalOpen).IsFalse();

        h.ViewModel.ActivateForMouse(BackstageViewModel.RollbackCardId);
        h.ViewModel.ActivateForMouse(BackstageWindow.RollbackGoChoiceId);
        Check.That(h.ViewModel.IsModalOpen).IsFalse();

        h.ViewModel.ActivateForMouse(BackstageViewModel.UninstallCardId);
        h.ViewModel.ActivateForMouse(BackstageWindow.UninstallConfirmChoiceId);
        Check.That(h.ViewModel.IsModalOpen).IsFalse();
    }

    [StaFact]
    public void AFailedRollbackOrUninstallLaunch_ShowsTheAcknowledgement()
    {
        var gateways = new FakeGateways
        {
            Installed = true,
            PreviousAvailable = true,
            RollbackHandoffResult = SetupHandoffStart.Failed,
            UninstallHandoffResult = SetupHandoffStart.Failed,
        };
        using var h = new Harness(gateways);

        h.ViewModel.ActivateForMouse(BackstageViewModel.RollbackCardId);
        h.ViewModel.ActivateForMouse(BackstageWindow.RollbackGoChoiceId);
        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.InstallFailedModalId);
        h.Window.DismissModal();

        h.ViewModel.ActivateForMouse(BackstageViewModel.UninstallCardId);
        h.ViewModel.ActivateForMouse(BackstageWindow.UninstallConfirmChoiceId);
        Check.That(h.ViewModel.ModalId).IsEqualTo(BackstageWindow.InstallFailedModalId);
    }

    [StaFact]
    public void ProbeLandingWhileAConfirmationIsOpen_DefersTheRebuildUntilItCloses()
    {
        // Review-02 R3's second half: a probe that lands while an answer is on screen must not
        // rebuild the grid under it. The install row stays as it was until the modal closes.
        var gateways = new FakeGateways
        {
            Installed = true,
            UpdateOfferPending = true,
            PreviousAvailable = true,
        };
        using var h = new Harness(gateways);
        h.ViewModel.ActivateForMouse(BackstageViewModel.QuitCardId);
        Check.That(h.ViewModel.IsModalOpen).IsTrue();

        h.Gateways!.UpdateOffer = Offer;
        h.Gateways.UpdateOfferPending = false;
        h.CompleteProbe();

        Check.WithCustomMessage("the rebuild clobbered an open confirmation")
            .That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNull();

        h.Window.DismissModal();
        h.Pump();

        Check.That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNotNull();
        Check.That(h.Window.CardFace(BackstageViewModel.UninstallCardId)).IsNotNull();
    }

    [StaFact]
    public void LateProbeCompletion_AfterTheWindowClosed_DoesNotTouchTheClosedWindow()
    {
        var gateways = new FakeGateways { Installed = true, UpdateOfferPending = true };
        var h = new Harness(gateways);
        try
        {
            h.Window.HideBackstage();   // closes: the update callback is still outstanding

            h.Gateways!.UpdateOffer = Offer;
            h.Gateways.UpdateOfferPending = false;
            h.CompleteProbe();

            Check.That(h.Window.CardFace(BackstageViewModel.UpdateCardId)).IsNull();
        }
        finally
        {
            h.ViewModel.Dispose();
        }
    }

    private static Border? Placeholder(BackstageWindow window) =>
        InstallChildren(window).OfType<Border>().SingleOrDefault(b =>
            AutomationProperties.GetAutomationId(b) == "Backstage_InstallPlaceholder");

    private static IEnumerable<UIElement> InstallChildren(BackstageWindow window) =>
        ((Grid)window.FindName("InstallCards")).Children.Cast<UIElement>();
}
