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

    [Fact]
    public void TobiiProvider_IncrementsTheConnectEdgeCount_OnTheSuccessBranch()
    {
        // Review 01 F1: this is the ONLY production producer of B3's aliasing hardening. Everything
        // downstream is pinned (model, service, recorder), but polling is native-bound, so deleting
        // or moving this line in a refactor would leave the hardening dead with every suite green.
        // A source ratchet (the pattern this file already uses for ResolveEagerServices) names the
        // exact success branch. The ORDER matters: IsConnected must become true before the count
        // moves, or the service's "snapshot first, counter second" read could see false + an old
        // count in the middle of a genuine connect (review 01's thread-safety note).
        var path = Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs");
        var source = File.ReadAllText(path);

        var branch = Regex.Match(
            source,
            @"else if \(!IsConnected\)\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        Check.WithCustomMessage(
                "TobiiStreamEngineProvider's poll loop no longer has the `else if (!IsConnected)` "
                + "success branch the connect-edge counter lives in.")
            .That(branch.Success).IsTrue();

        var body = branch.Groups["body"].Value;
        var connected = body.IndexOf("IsConnected = true;", StringComparison.Ordinal);
        var increment = body.IndexOf(
            "Interlocked.Increment(ref _connectEdgeCount);", StringComparison.Ordinal);

        Check.WithCustomMessage(
                "The `else if (!IsConnected)` success branch must set IsConnected = true (B1).")
            .That(connected).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The success branch must increment the connect-edge count. Without it, a connect "
                + "window between two polls never resets the continuous-disconnect clock (B1 F4).")
            .That(increment).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "IsConnected = true must precede the increment: the service reads IsConnected before "
                + "the counter, and the reverse order could let both reads miss a genuine connect.")
            .That(connected).IsStrictlyLessThan(increment);
    }
}
