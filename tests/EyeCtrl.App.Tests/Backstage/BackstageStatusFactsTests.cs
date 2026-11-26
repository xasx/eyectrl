namespace EyeCtrl.App.Tests.Backstage;

using System;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// The composition-root pin for the Backstage status strip (Ticket 03). Ticket 02's builder
/// deliberately takes resolved facts, which made it testable but left the RESOLUTION unpinned: a
/// constant <c>installed: true</c> or a dropped <c>ReadSelf()</c> would still pass every builder
/// test. These tests go through <see cref="BackstageStatusFacts.Capture"/>, the one method the
/// coordinator calls, and prove each badge fact comes from its real source.
/// </summary>
public class BackstageStatusFactsTests
{
    private sealed class FakeGateways : ISetupGateways
    {
        public bool Installed { get; set; }
        public bool PreviousAvailable => false;
        public string? PreviousVersion => null;
        public bool PolicyBlocks => false;
        public SetupUpdateOffer? UpdateOffer => null;
        public bool UpdateOfferPending => false;
        public void BeginUpdateOfferProbe(Action whenDone) => whenDone();
        public SetupHandoffStart StartUpdateHandoff(SetupUpdateOffer offer) => SetupHandoffStart.Failed;
        public SetupHandoffStart StartRollbackHandoff() => SetupHandoffStart.Failed;
        public SetupHandoffStart StartUninstallHandoff() => SetupHandoffStart.Failed;
    }

    private sealed class FakeIntegrity : IInputIntegrityProbe
    {
        public bool HasUIAccess { get; set; }
        public int ReadSelfCalls { get; private set; }

        public SelfTokenFacts ReadSelf()
        {
            ReadSelfCalls++;
            return new SelfTokenFacts(IntegrityLevel: 3, Elevated: false, HasUIAccess: HasUIAccess);
        }

        public ProcessTokenFacts InspectForeground() => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => ProcessTokenFacts.Unknown;
    }

    private static SupervisionToggleService NewSupervision(EyeCtrlSettings settings) =>
        new(settings,
            new SupervisorBringUp(settings,
                enablePublishing: () => { },
                launchSupervisor: () => false,
                persistEnabled: _ => { },
                reconcileRunKey: () => { },
                tryReachSupervisor: () => false),
            new SupervisorStartupEnsure(settings,
                enablePublishing: () => { },
                launchSupervisor: () => false,
                tryReachSupervisor: () => false),
            persistEnabled: _ => { },
            supervisorInstalled: () => true,
            isSupervisorConnected: () => false,
            reconcileRunKey: () => { },
            runInBackground: work => work(),
            postToUi: work => work());

    [Fact]
    public void Installed_IsReadFromTheGateways_AndUIAccessFromTheProbe()
    {
        var previous = StringTable.Current;
        var english = StringTable.EmbeddedEnglish;
        StringTable.Activate(english);
        try
        {
            var gateways = new FakeGateways { Installed = true };
            var integrity = new FakeIntegrity { HasUIAccess = true };
            var settings = new EyeCtrlSettings();

            var view = BackstageStatusFacts.Capture(gateways, integrity, NewSupervision(settings), "1.4.2.0");
            Check.That(integrity.ReadSelfCalls).IsEqualTo(1);
            Check.That(view.Snapshot.InstallText)
                .IsEqualTo(english.T("overlays.backstage.status.installed.uiAccess"));

            integrity.HasUIAccess = false;
            view = BackstageStatusFacts.Capture(gateways, integrity, null, "1.4.2.0");
            Check.That(view.Snapshot.InstallText)
                .IsEqualTo(english.T("overlays.backstage.status.installed.standard"));

            // installed ← gateways?.Installed ?? false, independently of the probe.
            gateways.Installed = false;
            integrity.HasUIAccess = true;
            view = BackstageStatusFacts.Capture(gateways, integrity, null, "1.4.2.0");
            Check.That(view.Snapshot.InstallText).IsEqualTo(english.T("overlays.backstage.status.devBuild"));

            // Null gateways is a dev build; null probe is no UIAccess.
            view = BackstageStatusFacts.Capture(null, integrity, null, "1.4.2.0");
            Check.That(view.Snapshot.InstallText).IsEqualTo(english.T("overlays.backstage.status.devBuild"));

            gateways.Installed = true;
            view = BackstageStatusFacts.Capture(gateways, null, null, "1.4.2.0");
            Check.That(view.Snapshot.InstallText)
                .IsEqualTo(english.T("overlays.backstage.status.installed.standard"));
        }
        finally
        {
            StringTable.Activate(previous);
        }
    }

    [Fact]
    public void SupervisorBadge_IsHiddenOnlyWhenTheServiceWasNotComposed()
    {
        var settings = new EyeCtrlSettings();
        var hidden = BackstageStatusFacts.Capture(null, null, supervision: null, "1.0");
        Check.That(hidden.ShowSupervisor).IsFalse();

        var shown = BackstageStatusFacts.Capture(null, null, NewSupervision(settings), "1.0");
        Check.That(shown.ShowSupervisor).IsTrue();
    }

    [Fact]
    public void TheCoordinator_BuildsTheStripThroughTheCapturedFacts_FromTheContextProbe()
    {
        // The pure resolver is only half the contract; the coordinator must call it with its
        // context probe and its per-visit gateways. A source pin is the available guard here: the
        // coordinator cannot be constructed without the whole window/VM graph.
        var source = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Services", "Coordinators", "BackstageUiCoordinator.cs")));
        var collapsed = System.Text.RegularExpressions.Regex.Replace(source, @"\s+", string.Empty);

        Check.WithCustomMessage(
                "BackstageUiCoordinator no longer resolves the strip through BackstageStatusFacts.Capture with the "
                + "context's IInputIntegrityProbe; the status facts would silently stop coming from the real sources.")
            .That(collapsed.Contains(
                "BackstageStatusFacts.Capture(_openGateways,context?.InputIntegrityProbe,_supervisionToggle,_assemblyVersion)"))
            .IsTrue();
    }
}
