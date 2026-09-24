namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The lazy-singleton trap (spec §Mechanism step 4, plan B4): nothing depends on
/// DwellCueService, so a bare registration never constructs it, it never subscribes, and the
/// feature silently does nothing. This test runs the REAL eager-resolution path
/// (AppBootstrapper.ResolveEagerServices — the same method App.xaml.cs calls at startup) over
/// the real container, sends DwellActivationMessage on the container's own messenger, and
/// observes the resolved INSTANCE's HandledCount move. The App.xaml.cs call site itself is
/// pinned by the source-scan fact below. Fresh settings keep CueEnabled=false, so no real
/// audio path is touched; the temp app-data folder mirrors DwellRoutingRegistrationWiringTests
/// (calibration rewrite hazard).
/// </summary>
/// <remarks>
/// Same collection discipline as <see cref="DwellRoutingRegistrationWiringTests"/>: "DpiHelper
/// Tests (App)" (DisableParallelization), temp app-data (resolving through
/// ManualSelectionDwellService loads CalibrationService, which can rewrite calibration.json),
/// and provider disposal — which disposes the DwellCueService singleton and unregisters it
/// from the process-wide messenger. No window is constructed here (plain services only), so
/// unlike that file's two [StaFact]s this needs no WpfApplicationGate.
/// </remarks>
[Collection("DpiHelper Tests (App)")]
public class DwellCueWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("dwell-cue");

    private static ServiceProvider BuildProvider() =>
        AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    [Fact]
    public void RealContainer_EagerResolvePath_ServiceSubscribes()
    {
        using var provider = BuildProvider();
        AppBootstrapper.ResolveEagerServices(provider);

        var cue = provider.GetRequiredService<DwellCueService>();
        var before = cue.HandledCount;

        provider.GetRequiredService<IMessenger>().Send(new DwellActivationMessage());

        Check.That(cue.HandledCount - before).IsStrictlyGreaterThan(0);
        // Fresh settings ⇒ CueEnabled=false ⇒ HandledCount moved but nothing played: wiring
        // proven without any audio device dependency.
    }

    [Fact]
    public void AppXamlCs_CallsTheEagerResolvePath()
    {
        // B4b: deleting the startup call would otherwise keep every other fact green.
        var appXamlCs = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src/EyeCtrl.App/App.xaml.cs"));
        Check.WithCustomMessage(
            "App.xaml.cs must call AppBootstrapper.ResolveEagerServices(provider) at startup — "
            + "DI singletons are lazy; without this line DwellCueService never subscribes.")
            .That(appXamlCs.Contains("ResolveEagerServices(")).IsTrue();
    }
}
