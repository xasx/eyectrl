namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Reflection;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Thumbnails;
using EyeCtrl.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// S4-C's composition root, pinned against the REAL container. The switcher's live previews arrive
/// through <see cref="OverlayManager"/>'s constructor and one <c>Configure</c> call; both are
/// optional-parameter shaped, so a registration that silently went missing leaves the app building
/// fine, every test green, and every tile showing an icon box — the owner's first install is the
/// only place the loss would surface. The container's own instance is compared by reference: the
/// production registration and a null fallback are distinguishable only that way.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class TaskSwitcherPreviewWiringTests
{
    private static object? Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner);

    [Fact]
    public void TheOverlayManager_GetsTheContainersRealThumbnailService()
    {
        var appData = TestAppDataDir.Create("task-switcher-preview-wiring");
        using var provider = AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData);

        var service = provider.GetRequiredService<IWindowThumbnailService>();
        Check.WithCustomMessage("the container must resolve the real dwmapi thumbnail seam")
            .That(service).IsInstanceOf<Win32WindowThumbnailService>();

        var manager = (OverlayManager)provider.GetRequiredService<IOverlayManager>();
        Check.WithCustomMessage(
                "OverlayManager got no thumbnail service: the switcher would show icon boxes only, " +
                "and no test that does not build the real container would notice.")
            .That(Field(manager, "_thumbnailService")).IsSameReferenceAs(service);
    }

    /// <summary>
    /// Source pin: the service the manager holds must reach the switcher's <c>Configure</c> call —
    /// a field that is stored but never handed over has no effect at all.
    /// </summary>
    [Fact]
    public void OverlayManager_HandsTheThumbnailServiceToTheSwitcher()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Services", "OverlayManager.cs"));

        Check.WithCustomMessage("the switcher is not configured with the thumbnail service")
            .That(source.Contains(
                "_taskSwitcher.Configure(viewModels.TaskSwitcher, _iconProvider, _thumbnailService)"))
            .IsTrue();
    }
}
