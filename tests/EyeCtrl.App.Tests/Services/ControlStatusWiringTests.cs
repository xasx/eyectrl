namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Status;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Pins the composition-root half of Wave B ticket B1, which no headless model/service test can see:
/// <c>IControlStatus</c> resolves to the LIVE service (B3 will inject it), and the RESOLVED provider
/// type reaches the model. Test mode forces the Mouse provider, so a registration that dropped or
/// defaulted the provider-type argument would report Normal here instead of NoTracker.
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
    public void TheLiveService_IsTheRegisteredIControlStatus_AndCarriesTheResolvedProviderType()
    {
        using var sp = BuildProvider();

        var service = sp.GetRequiredService<ControlStatusService>();
        Check.That(sp.GetRequiredService<ControlStatusService>()).IsSameReferenceAs(service);
        Check.That(sp.GetRequiredService<IControlStatus>()).IsSameReferenceAs(service);

        service.Start();

        // isTestMode forces AppBootstrapper's MouseGazeProvider, so Mouse -> NoTracker is the only
        // correct answer. A missing/wrong provider-type argument reads as an eye tracker.
        Check.That(service.Current.State).IsEqualTo(ControlState.NoTracker);
        Check.That(service.Current.Disconnected).IsFalse();
    }
}
