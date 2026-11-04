namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Review 01 M1 + MUT3, pinned as source (the same style as <see cref="SettingsProbeWiringTests" />):
/// the two hops that make the „Erweitert" rows fresh per Settings window and make the hand-off's
/// clean exit carry the reviewed restart flag are unreachable headlessly — the first constructs a
/// WPF window, the second is App startup. Comment-stripped, whitespace-collapsed <c>Contains</c>:
/// the factory shape is load-bearing (an instance again would freeze the offer and the policy for
/// the App's lifetime), and the pass-through lambda is what a wrong <c>true</c> would break.
/// </summary>
public class SettingsSetupWiringTests
{
    [Fact]
    public void SettingsUiCoordinator_BuildsAFreshGatewaysInstancePerSettingsWindow()
    {
        var source = Collapsed("src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");

        Check.WithCustomMessage(
                "SettingsUiCoordinator no longer takes a gateways FACTORY, or no longer invokes it when "
                + "it builds a window: one cached instance would freeze the update offer and the "
                + "elevation policy until the App restarts (review 01 M1).")
            .That(source.Contains("privateFunc<ISetupGateways>?_setupGatewaysFactory;")).IsTrue();
        Check.That(source.Contains("publicvoidSetSetupGateways(Func<ISetupGateways>gatewaysFactory)")).IsTrue();
        Check.That(source.Contains("setupGateways:_setupGatewaysFactory?.Invoke());")).IsTrue();
    }

    [Fact]
    public void App_WiresTheGatewaysAsAFactory_AndPassesTheRestartFlagThrough()
    {
        var source = Collapsed("src", "EyeCtrl.App", "App.xaml.cs");

        Check.WithCustomMessage(
                "App.xaml.cs no longer hands SetupGateways a factory: a single startup instance would "
                + "freeze the offer/policy (review 01 M1).")
            .That(source.Contains("SetSetupGateways(()=>SetupGateways.CreateForProduction(setupExitFlow,")).IsTrue();
        Check.WithCustomMessage(
                "The hand-off exit flow must pass its restartRequested argument through to the "
                + "heartbeat: a hard-coded true makes the Supervisor relaunch mid-swap (review 01 "
                + "MUT3).")
            .That(source.Contains(
                "restartRequested=>_heartbeatPublisher?.SignalShuttingDown(restartRequested)")).IsTrue();
    }

    [Fact]
    public void TheProbeCompletion_JudgesBothConditionsInsideTheDispatchedAction()
    {
        // Review 02 R2/R3: rebuild only on „Erweitert", and evaluate the closed-window check
        // INSIDE the queued action — the worker-thread check leaves a close-then-dispatch window
        // where a disposed view model would be rebuilt.
        var source = Collapsed("src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs");

        Check.WithCustomMessage(
                "The probe completion must go through SetupProbeRebuild.ShouldRebuild inside the "
                + "dispatched action (review 02 R2: the tab check; R3: the closed check in the action).")
            .That(source.Contains(
                "if(SetupProbeRebuild.ShouldRebuild(_windowClosed,"
                + "_viewModel.Settings.CurrentTabName))RebuildContent();"))
            .IsTrue();
    }

    /// <summary>The file's code with comments blanked and every whitespace run collapsed.</summary>
    private static string Collapsed(params string[] parts) =>
        Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(
                Path.Combine(SourceScanner.RepoRoot().FullName, Path.Combine(parts)))),
            @"\s+", string.Empty);
}
