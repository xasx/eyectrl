namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels;

/// <summary>
/// C9 S6f's „Erweitert" rows: the visibility matrix the ticket lists (installed or not; a verified
/// offer, a pending probe, none; the policy block on BOTH rows; the previous folder), the confirm
/// pattern (the window's own modal, with the ticket's labels), the hand-offs each row starts, and
/// the review-01 additions: the same-height probe placeholder (N2), the blocked rollback gate (M4),
/// and the failure line a refused hand-off shows (N3). The facts themselves — what makes an offer
/// exist (dot-folders, older, unverified, foreign signer) — are pinned at the gateways level in
/// <see cref="EyeCtrl.App.Tests.Services.SetupGatewaysTests" />; a page source only renders facts.
/// </summary>
public class SetupRowsTests
{
    private sealed class FakeGateways : ISetupGateways
    {
        public bool Installed { get; set; }
        public bool PreviousAvailable { get; set; }
        public string? PreviousVersion { get; set; } = "1.0.0";
        public bool PolicyBlocks { get; set; }
        public SetupUpdateOffer? UpdateOffer { get; set; }
        public bool UpdateOfferPending { get; set; }
        public int BeginProbeCalls { get; private set; }
        public List<SetupUpdateOffer> UpdateHandoffs { get; } = [];
        public int RollbackHandoffs { get; private set; }
        public int UninstallHandoffs { get; private set; }
        public bool UpdateHandoffResult { get; set; } = true;
        public bool RollbackHandoffResult { get; set; } = true;
        public bool UninstallHandoffResult { get; set; } = true;

        public void BeginUpdateOfferProbe(Action whenDone) => BeginProbeCalls++;

        public bool StartUpdateHandoff(SetupUpdateOffer offer)
        {
            UpdateHandoffs.Add(offer);
            return UpdateHandoffResult;
        }

        public bool StartRollbackHandoff()
        {
            RollbackHandoffs++;
            return RollbackHandoffResult;
        }

        public bool StartUninstallHandoff()
        {
            UninstallHandoffs++;
            return UninstallHandoffResult;
        }
    }

    private static readonly SetupUpdateOffer Offer =
        new(@"C:\inbox\2.0.0", @"C:\inbox\2.0.0\EyeCtrl.Setup.exe", "2.0.0", "1.0.0");

    private static SettingsWindowViewModel BuildRows(
        FakeGateways gateways, out List<SettingsRowSpec> rows, List<string>? errors = null)
    {
        var viewModel = QaTestSupport.MakeViewModel();
        var context = TestSettingsPageContext.Build(
            viewModel: viewModel,
            setupGateways: gateways,
            showError: errors is null ? null : errors.Add);
        rows = new AdvancedPageSource(context).Build().SelectMany(page => page.Rows).ToList();
        return viewModel;
    }

    private static ButtonSpec Button(List<SettingsRowSpec> rows, string id) =>
        rows.OfType<ButtonSpec>().Single(spec => spec.Id == id);

    private static bool HasRow(List<SettingsRowSpec> rows, string id) =>
        rows.Any(spec => spec.Id == id);

    private static string BlockedText() => StringTable.Current.T("settings.advanced.update.blocked");

    [Fact]
    public void NotInstalled_HidesAllThreeRows()
    {
        // Ticket's chosen option: a dev build hides every setup row (no dead not-to-dwell rows).
        var gateways = new FakeGateways
        {
            Installed = false,
            UpdateOffer = Offer,
            PreviousAvailable = true,
        };

        BuildRows(gateways, out var rows);

        Check.That(HasRow(rows, "av_update")).IsFalse();
        Check.That(HasRow(rows, "av_rollback")).IsFalse();
        Check.That(HasRow(rows, "av_uninstall")).IsFalse();
    }

    [Fact]
    public void InstalledWithoutOfferOrPrevious_ShowsOnlyUninstall()
    {
        BuildRows(new FakeGateways { Installed = true }, out var rows);

        Check.That(HasRow(rows, "av_update")).IsFalse();
        Check.That(HasRow(rows, "av_rollback")).IsFalse();
        Check.That(Button(rows, "av_uninstall").Label)
            .IsEqualTo(StringTable.Current.T("settings.advanced.uninstall.label"));
        Check.That(Button(rows, "av_uninstall").Kind).IsEqualTo(ButtonKind.Danger);
    }

    [Fact]
    public void VerifiedNewerOffer_ShowsTheUpdateRow_WithBothVersionsInTheDescription()
    {
        BuildRows(new FakeGateways { Installed = true, UpdateOffer = Offer }, out var rows);

        var update = Button(rows, "av_update");
        Check.That(update.Label).IsEqualTo(StringTable.Current.T("settings.advanced.update.label"));
        Check.That(update.Description).IsNotNull();
        Check.That(update.Description!.Contains("2.0.0", StringComparison.Ordinal)).IsTrue();
        Check.That(update.Description.Contains("1.0.0", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void WhileTheProbeRuns_ASameHeightPlaceholderStandsInForTheUpdateRow()
    {
        // Review 01 N2: the catalog check is off-thread; the placeholder keeps the row's geometry
        // so nothing shifts under the gaze when the result lands. The build also (re)starts the
        // idempotent probe for a host that did not start it at window open.
        var gateways = new FakeGateways { Installed = true, UpdateOfferPending = true };

        BuildRows(gateways, out var rows);

        var placeholder = Button(rows, "av_update_probe");
        Check.That(placeholder.Label).IsEqualTo(StringTable.Current.T("settings.advanced.update.checking"));
        Check.That(placeholder.Width).IsEqualTo(380.0);
        Check.That(placeholder.Height).IsEqualTo(100.0);
        Check.That(gateways.BeginProbeCalls).IsEqualTo(1);

        // Same height as the real update button it will become.
        var real = new FakeGateways { Installed = true, UpdateOffer = Offer };
        BuildRows(real, out var realRows);
        Check.That(Button(realRows, "av_update").Height).IsEqualTo(placeholder.Height);
    }

    [Fact]
    public void AFinishedProbeWithoutAnOffer_ShowsNoUpdateRow()
    {
        BuildRows(
            new FakeGateways { Installed = true, UpdateOffer = null, UpdateOfferPending = false },
            out var rows);

        Check.That(HasRow(rows, "av_update")).IsFalse();
        Check.That(HasRow(rows, "av_update_probe")).IsFalse();
    }

    [Fact]
    public void BlockedPolicy_ShowsTheBlockedText_AndNoDwellableUpdateButton()
    {
        BuildRows(
            new FakeGateways { Installed = true, UpdateOffer = Offer, PolicyBlocks = true },
            out var rows);

        Check.That(HasRow(rows, "av_update")).IsFalse();
        Check.That(rows.OfType<LabelSpec>().Single(spec => spec.Text() == BlockedText())).IsNotNull();
    }

    [Fact]
    public void BlockedPolicy_AlsoGatesTheRollbackRow_OneBlockedLineForBoth()
    {
        // Review 01 M4: plan §10 gives both rows the blocked text; a blocked rollback must not
        // hand off and exit (Setup then snapshots a still-exiting App and starts nothing).
        BuildRows(
            new FakeGateways
            {
                Installed = true,
                PreviousAvailable = true,
                UpdateOffer = Offer,
                PolicyBlocks = true,
            },
            out var rows);

        Check.That(HasRow(rows, "av_update")).IsFalse();
        Check.That(HasRow(rows, "av_rollback")).IsFalse();
        var blocked = rows.OfType<LabelSpec>().Where(spec => spec.Text() == BlockedText()).ToList();
        Check.That(blocked).HasSize(1);
        Check.That(HasRow(rows, "av_uninstall")).IsTrue();
    }

    [Fact]
    public void BlockedPolicy_RollbackOnly_StillShowsTheBlockedLine()
    {
        BuildRows(
            new FakeGateways { Installed = true, PreviousAvailable = true, PolicyBlocks = true },
            out var rows);

        Check.That(HasRow(rows, "av_rollback")).IsFalse();
        Check.That(rows.OfType<LabelSpec>().Single(spec => spec.Text() == BlockedText())).IsNotNull();
    }

    [Fact]
    public void PreviousPresent_ShowsTheRollbackRow_NamingThePreviousVersion()
    {
        BuildRows(
            new FakeGateways { Installed = true, PreviousAvailable = true, PreviousVersion = "1.2.3" },
            out var rows);

        var rollback = Button(rows, "av_rollback");
        Check.That(rollback.Label).IsEqualTo(StringTable.Current.T("settings.advanced.rollback.label"));
        Check.That(rollback.Description!.Contains("1.2.3", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void UpdateActivation_OpensTheExistingConfirmModal_WithTheTicketsChoices()
    {
        var gateways = new FakeGateways { Installed = true, UpdateOffer = Offer };
        var viewModel = BuildRows(gateways, out var rows);

        Button(rows, "av_update").OnActivate();

        Check.That(viewModel.IsModalVisible).IsTrue();
        Check.That(viewModel.ModalChoices).HasSize(2);
        Check.That(viewModel.ModalChoices[0].Label)
            .IsEqualTo(StringTable.Current.T("update.confirm.install"));
        Check.That(viewModel.ModalChoices[0].Kind).IsEqualTo(ModalChoiceKind.Affirmative);
        Check.That(viewModel.ModalChoices[1].Label)
            .IsEqualTo(StringTable.Current.T("update.confirm.later"));
        Check.That(viewModel.ModalChoices[1].Kind).IsEqualTo(ModalChoiceKind.Negative);

        // The question names the offered version; the detail rides in the same body.
        Check.That(viewModel.ModalMessage.Contains("2.0.0", StringComparison.Ordinal)).IsTrue();
        Check.That(viewModel.ModalMessage.Contains(
            StringTable.Current.T("update.confirm.detail"), StringComparison.Ordinal)).IsTrue();

        // Confirming hands the offer over; nothing happens before the dwell.
        Check.That(gateways.UpdateHandoffs).IsEmpty();
        viewModel.ModalChoices[0].OnChosen!.Invoke();
        Check.That(gateways.UpdateHandoffs).HasSize(1);
        Check.That(gateways.UpdateHandoffs[0].Directory).IsEqualTo(Offer.Directory);
    }

    [Fact]
    public void RollbackActivation_OpensTheConfirmModal_AndGoHandsOff()
    {
        var gateways = new FakeGateways { Installed = true, PreviousAvailable = true, PreviousVersion = "1.2.3" };
        var viewModel = BuildRows(gateways, out var rows);

        Button(rows, "av_rollback").OnActivate();

        Check.That(viewModel.ModalChoices[0].Label)
            .IsEqualTo(StringTable.Current.T("rollback.confirm.go"));
        Check.That(viewModel.ModalChoices[1].Label)
            .IsEqualTo(StringTable.Current.T("rollback.confirm.later"));
        Check.That(gateways.RollbackHandoffs).IsZero();

        viewModel.ModalChoices[0].OnChosen!.Invoke();
        Check.That(gateways.RollbackHandoffs).IsEqualTo(1);
    }

    [Fact]
    public void UninstallActivation_NeedsNoAppConfirm_AndHandsOffImmediately()
    {
        var gateways = new FakeGateways { Installed = true };
        var viewModel = BuildRows(gateways, out var rows);

        Button(rows, "av_uninstall").OnActivate();

        // Setup's own window asks (S6e); the App neither asks nor exits.
        Check.That(viewModel.IsModalVisible).IsFalse();
        Check.That(gateways.UninstallHandoffs).IsEqualTo(1);
    }

    [Fact]
    public void ARefusedUpdateHandoff_KeepsRunning_AndShowsTheFailureLine()
    {
        // Review 01 N3: after a completed dwell, silence reads as the button not working.
        var errors = new List<string>();
        var gateways = new FakeGateways
        {
            Installed = true,
            UpdateOffer = Offer,
            UpdateHandoffResult = false,
        };
        var viewModel = BuildRows(gateways, out var rows, errors);

        Button(rows, "av_update").OnActivate();
        viewModel.ModalChoices[0].OnChosen!.Invoke();

        Check.That(errors).HasSize(1);
        Check.That(errors[0]).IsEqualTo(StringTable.Current.T("settings.advanced.setup.startFailed"));
    }

    [Fact]
    public void ARefusedRollbackHandoff_ShowsTheFailureLine()
    {
        var errors = new List<string>();
        var gateways = new FakeGateways
        {
            Installed = true,
            PreviousAvailable = true,
            RollbackHandoffResult = false,
        };
        var viewModel = BuildRows(gateways, out var rows, errors);

        Button(rows, "av_rollback").OnActivate();
        viewModel.ModalChoices[0].OnChosen!.Invoke();

        Check.That(errors).HasSize(1);
        Check.That(errors[0]).IsEqualTo(
            StringTable.Current.T("settings.advanced.setup.startFailed"));
    }

    [Fact]
    public void ARefusedUninstallHandoff_ShowsTheFailureLine()
    {
        var errors = new List<string>();
        var gateways = new FakeGateways { Installed = true, UninstallHandoffResult = false };
        BuildRows(gateways, out var rows, errors);

        Button(rows, "av_uninstall").OnActivate();

        Check.That(errors).HasSize(1);
        Check.That(errors[0]).IsEqualTo(
            StringTable.Current.T("settings.advanced.setup.startFailed"));
    }

    [Fact]
    public void EverySetupRow_IsAdvancedOnly()
    {
        BuildRows(
            new FakeGateways { Installed = true, UpdateOffer = Offer, PreviousAvailable = true },
            out var rows);

        var setupRows = rows.Where(spec => spec.Id is "av_update" or "av_rollback" or "av_uninstall");
        Check.That(setupRows).HasSize(3);
        Check.That(setupRows).ContainsOnlyElementsThatMatch(spec => !spec.Simple);
    }
}
