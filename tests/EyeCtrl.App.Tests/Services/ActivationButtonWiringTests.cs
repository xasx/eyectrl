namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// B2 fixup-01 F1: the real container must build <see cref="ActivationButtonViewModel"/> with the
/// LIVE settings, not the messenger-only constructor's throwaway defaults. Mutation MC (make the DI
/// registration use the one-argument ctor) leaves both assertions below seeing `None`, i.e. the menu
/// face for the ManualSelection offer — the bug the reviewer found survived the whole suite.
///
/// <para>
/// The provider is the production one, built exactly as the wiring tests build it, with a temp app
/// data folder (never the real profile). No window is constructed or shown.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ActivationButtonWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("activation-button-wiring");

    [Fact]
    public void TheRealContainer_GivesTheButtonTheLiveSelectionMode()
    {
        var settings = new EyeCtrlSettings { SelectionMode = SelectionMode.ManualSelection };
        using var sp = AppBootstrapper.BuildServiceProvider(
            settings,
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

        var vm = sp.GetRequiredService<ActivationButtonViewModel>();

        // Direct Receive: the container's messenger is the process-global WeakReferenceMessenger, and
        // this test is about the CONSTRUCTOR's dependency, not about delivery.
        vm.Receive(new ShowActivationButtonMessage(
            new PhysicalPoint(500, 300), LayoutConstants.ActivationButtonHitRadiusPx));
        Check.WithCustomMessage("the container did not give the VM the live ManualSelection settings")
            .That(vm.Content).IsEqualTo(ActivationButtonContent.Magnifier);

        // The same live instance drives the next offer.
        settings.SelectionMode = SelectionMode.AutoSelection;
        vm.Receive(new ShowActivationButtonMessage(
            new PhysicalPoint(520, 320), LayoutConstants.ActivationButtonHitRadiusPx));
        Check.WithCustomMessage("the container gave the VM a copy, not the live settings")
            .That(vm.Content).IsEqualTo(ActivationButtonContent.Menu);
    }
}
