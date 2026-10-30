namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Status;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Pins the composition-root half of Wave B ticket B1, which no headless model/service test can see:
/// <c>IControlStatus</c> resolves to the LIVE service, and the RESOLVED provider type reaches the
/// model. Test mode forces the Mouse provider — a DELIBERATE one (Wave C ticket C5 H1), so the only
/// correct answer is Normal, never NoTracker; a registration that dropped or defaulted the fallback
/// flag the other way would show the "No eye tracker" strip permanently in test mode.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ControlStatusWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("control-status");

    private static ServiceProvider BuildProvider() =>
        AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    [Fact]
    public void TheLiveService_IsTheRegisteredIControlStatus_AndCarriesTheDeliberateMouseReading()
    {
        using var sp = BuildProvider();

        var service = sp.GetRequiredService<ControlStatusService>();
        Check.That(sp.GetRequiredService<ControlStatusService>()).IsSameReferenceAs(service);
        Check.That(sp.GetRequiredService<IControlStatus>()).IsSameReferenceAs(service);

        service.Start();

        // isTestMode forces AppBootstrapper's MouseGazeProvider: a deliberate Mouse provider, not a
        // fallback, so it reports Normal (C5 H1). The old Mouse -> NoTracker reading is the bug.
        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(service.Current.Disconnected).IsFalse();
    }

    [Fact]
    public void TheCompositionRoot_FeedsTheSplashFallbackPredicateIntoTheService()
    {
        // Only a real tracker-to-mouse FALLBACK may read NoTracker. The runtime path cannot build
        // one headlessly (test mode always forces the mouse, and a real probe would touch the
        // owner's tracker), so pin the argument at the one registration that decides it. Mutant:
        // drop the `mouseIsFallback:` argument (it defaults to false) -> this goes red while the
        // deliberate-mouse test above still passes.
        var boot = SourceScanner.StripComments(Source("src/EyeCtrl.App/Services/AppBootstrapper.cs"));
        Check.WithCustomMessage(
                "ControlStatusService must be built with the splash fallback predicate and the RESOLVED provider type")
            .That(Regex.IsMatch(boot,
                @"new\s+ControlStatusService\s*\([\s\S]*?mouseIsFallback\s*:\s*SplashWindow\s*\.\s*ShowsNoTrackerSentence\s*\(\s*settings\s*\.\s*General\s*\.\s*GazeProviderType\s*,\s*providerType\s*,\s*isTestMode\s*\)"))
            .IsTrue();
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
}
