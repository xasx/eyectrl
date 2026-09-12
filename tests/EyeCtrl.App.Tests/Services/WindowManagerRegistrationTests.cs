namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <c>Win32WindowManager</c> gained a constructor dependency (<c>ILogger</c>) so a failed
/// task switch says why. A constructor parameter the container cannot supply does not fail a
/// unit test — it throws at startup, on the user's machine, with the overlays half-built. This
/// resolves it through the real bootstrapper so that break is caught here instead.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class WindowManagerRegistrationTests
{
    // Temp, never the real folder: this container resolves CalibrationService transitively and the
    // load can rewrite the user's calibration.json. See TestAppDataDir.
    private static readonly string AppData = TestAppDataDir.Create("window-manager");

    private static ServiceProvider BuildProvider()
        => AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    [Fact]
    public void WindowManager_ResolvesFromTheContainer_WithItsLogger()
    {
        using var sp = BuildProvider();

        var manager = sp.GetService<IWindowManager>();

        Check.That(manager).IsNotNull();
        Check.That(manager).IsInstanceOf<Win32WindowManager>();
    }
}
