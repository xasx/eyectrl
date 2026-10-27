namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The lazy-singleton trap, again for B3: nothing depends on <see cref="StatusChimeService"/>, so a
/// bare registration never constructs it, it never subscribes, and a real tracker disconnect is
/// silent. This runs the REAL eager-resolution path over the real container and proves construction
/// by sending a status message BEFORE the test resolves the service: a not-yet-constructed recipient
/// would miss it (the messenger has no replay) and HandledCount would stay 0.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class StatusChimeWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("status-chime-wiring");

    private static ServiceProvider BuildProvider(EyeCtrlSettings settings) =>
        AppBootstrapper.BuildServiceProvider(
            settings,
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    [Fact]
    public void RealContainer_EagerResolvePath_ServiceSubscribes_WithLiveSettingsAndTheShippedAsset()
    {
        // The chime is ON by default, so the wiring test turns it off: constructing the player is
        // what this test proves, and the non-edging message below must not reach the sound card.
        var root = new EyeCtrlSettings();
        root.Dwell.StatusChimeEnabled = false;
        using var provider = BuildProvider(root);

        AppBootstrapper.ResolveEagerServices(provider);
        provider.GetRequiredService<IMessenger>()
            .Send(new ControlStatusChangedMessage(ControlState.Normal, Disconnected: false));

        var chime = provider.GetRequiredService<StatusChimeService>();

        Check.WithCustomMessage(
                "StatusChimeService did not receive a message sent after ResolveEagerServices: the "
                + "eager path does not construct it, so it never subscribes and a disconnect is silent.")
            .That(chime.HandledCount).IsEqualTo(1);

        var settingsField = typeof(StatusChimeService)
            .GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(chime);
        Check.WithCustomMessage(
                "StatusChimeService must capture the LIVE root settings (EyeCtrlSettings.Dwell) - a "
                + "fresh DwellSettings here means the d_status_chime toggle does nothing.")
            .That(settingsField).IsSameReferenceAs(provider.GetRequiredService<EyeCtrlSettings>().Dwell);

        var playerField = typeof(StatusChimeService)
            .GetField("_player", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(chime);
        Check.WithCustomMessage(
                "The production factory must build a SoundPlayerCuePlayer over the shipped asset - a "
                + "null player here means the chime is silently dead in production.")
            .That(playerField is SoundPlayerCuePlayer).IsTrue();
    }

    [Fact]
    public void ResolveEagerServices_ResolvesTheChimeService()
    {
        // The behavioural pin above proves the CURRENT body. This names the call in the one method
        // every startup path runs, so deleting the line fails with the reason spelled out.
        var source = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src/EyeCtrl.App/Services/AppBootstrapper.cs"));
        var isResolved = new Regex(
            @"^\s*_ = sp\.GetRequiredService<StatusChimeService>\(\);",
            RegexOptions.Multiline).IsMatch(source);
        Check.WithCustomMessage(
                "AppBootstrapper.ResolveEagerServices must resolve StatusChimeService - DI "
                + "singletons are lazy and the service subscribes in its ctor.")
            .That(isResolved).IsTrue();
    }
}
