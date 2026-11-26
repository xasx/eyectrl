namespace EyeCtrl.App.Tests.Backstage;

using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.App.Tests.Services;

/// <summary>
/// Ticket 04's wiring pins, migrated from <c>SettingsSetupWiringTests</c> with the ownership move:
/// (a) Backstage builds a FRESH <see cref="EyeCtrl.App.Services.ISetupGateways"/> per visit (review
/// 01 M1); (b) App hands that factory in and the hand-off's clean exit carries the reviewed restart
/// flag (MUT3); (c) the probe completion judges <c>_windowClosed</c> INSIDE the dispatched action and
/// defers a rebuild while a confirmation owns the surface (review 02 R3).
/// <para>
/// These are source pins in the same style as the Settings originals: the first hop constructs a
/// WPF window and the second is App startup, neither reachable headlessly; comment-stripped,
/// whitespace-collapsed <c>Contains</c> makes the shape load-bearing.
/// </para>
/// </summary>
public class BackstageSetupWiringTests
{
    [Fact]
    public void BackstageUiCoordinator_BuildsAFreshGatewaysInstancePerOpen()
    {
        var source = Collapsed("src", "EyeCtrl.App", "Services", "Coordinators", "BackstageUiCoordinator.cs");

        Check.WithCustomMessage(
                "BackstageUiCoordinator no longer takes a gateways FACTORY, or no longer invokes it when " +
                "it opens: one cached instance would freeze the update offer and the elevation policy " +
                "until the App restarts (review 01 M1).")
            .That(source.Contains("privateFunc<ISetupGateways>?_setupGatewaysFactory;")).IsTrue();
        Check.That(source.Contains("SetSetupGateways(Func<ISetupGateways>factory)")).IsTrue();
        Check.WithCustomMessage(
                "The open path must invoke the factory per visit, not cache the result.")
            .That(source.Contains("_openGateways=_setupGatewaysFactory?.Invoke();")).IsTrue();
    }

    [Fact]
    public void EventCoordinator_RoutesTheFactoryToBackstage_AndNotToSettings()
    {
        var source = Collapsed("src", "EyeCtrl.App", "Services", "EventCoordinator.cs");

        Check.WithCustomMessage(
                "EventCoordinator must still hand the factory to Backstage (Ticket 04 moved the install " +
                "surface there).")
            .That(source.Contains("_backstage!.SetSetupGateways(gatewaysFactory);")).IsTrue();
        Check.WithCustomMessage(
                "Settings no longer owns any install surface; a Settings forward would leave a second, " +
                "unused gateways instance created per Settings window.")
            .That(source.Contains("_settingsUi!.SetSetupGateways")).IsFalse();
    }

    [Fact]
    public void App_WiresTheGatewaysAsAFactory_AndPassesTheRestartFlagThrough()
    {
        var source = Collapsed("src", "EyeCtrl.App", "App.xaml.cs");

        Check.WithCustomMessage(
                "App.xaml.cs no longer hands SetupGateways a factory: a single startup instance would " +
                "freeze the offer/policy (review 01 M1).")
            .That(source.Contains("SetSetupGateways(()=>SetupGateways.CreateForProduction(setupExitFlow,")).IsTrue();
        Check.WithCustomMessage(
                "The hand-off exit flow must pass its restartRequested argument through to the " +
                "heartbeat: a hard-coded true makes the Supervisor relaunch mid-swap (review 01 MUT3).")
            .That(source.Contains(
                "restartRequested=>_heartbeatPublisher?.SignalShuttingDown(restartRequested)")).IsTrue();
    }

    [Fact]
    public void TheProbeCompletion_JudgesClosedAndModalInsideTheDispatchedAction()
    {
        // Review 02 R3: the closed-window check runs INSIDE the queued action — a window that closes
        // between the worker's completion and the dispatch must not touch a disposed view model. A
        // probe that lands while a modal is up must defer the rebuild, never rebuild under it.
        var source = Collapsed("src", "EyeCtrl.App", "Overlays", "BackstageWindow.xaml.cs");

        Check.WithCustomMessage(
                "The probe completion must marshal first and judge _windowClosed inside the dispatched " +
                "action (review 02 R3).")
            .That(source.Contains(
                "gate.BeginUpdateOfferProbe(()=>Dispatcher.BeginInvoke(newAction(()=>{if(_windowClosed)return;"))
            .IsTrue();
        Check.WithCustomMessage(
                "An open confirmation must defer the install-row rebuild until it closes; rebuilding " +
                "under it clobbers the answer the owner is reading.")
            .That(source.Contains(
                "if(_vm?.IsModalOpen==true){_installRebuildDeferred=true;return;}")).IsTrue();
    }

    /// <summary>The file's code with comments blanked and every whitespace run collapsed.</summary>
    private static string Collapsed(params string[] parts) =>
        Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(
                Path.Combine(SourceScanner.RepoRoot().FullName, Path.Combine(parts)))),
            @"\s+", string.Empty);
}
