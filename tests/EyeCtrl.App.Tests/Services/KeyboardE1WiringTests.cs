namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Reflection;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Services;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// E1's composition-root wiring, pinned against the REAL container. The keyboard's log is fed
/// through three optional constructor parameters (a logger, the OS probe, the sensitive-context
/// detector). Optional means a registration that silently went missing leaves the keyboard
/// building fine with logging quietly off — green everywhere except the owner's day log. So the
/// test reads what the container actually injected, the way the sibling wiring tests do.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class KeyboardE1WiringTests
{
    private static object? Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner);

    [Fact]
    public void TheKeyboardViewModel_IsBuiltWithTheContainersLoggerProbeAndSensitiveDetector()
    {
        var appData = TestAppDataDir.Create("kb-e1-wiring");
        using var provider = AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData);

        var vm = provider.GetRequiredService<KeyboardViewModel>();

        var probe = provider.GetRequiredService<IKeyboardContextProbe>();
        Check.That(probe).IsInstanceOf<Win32KeyboardContextProbe>();
        Check.WithCustomMessage("The keyboard must get the container's OS probe, or every 'shown' line says "
                + "'unknown' and every mirror check is 'unsupported'.")
            .That(Field(vm, "_contextProbe")).IsSameReferenceAs(probe);
        Check.WithCustomMessage("The mirror check must honour the container's sensitive-context detector, "
                + "or it would read a password field.")
            .That(Field(vm, "_sensitiveContext"))
            .IsSameReferenceAs(provider.GetRequiredService<ISensitiveContextDetector>());
        Check.WithCustomMessage("The recorder got no logger from the container: E1 would write nothing to "
                + "the day log while every other test stays green.")
            .That(Field(vm.E1, "_logger")).IsNotNull();
        Check.That(Field(vm.E1, "_logger")!.GetType().FullName)
            .Contains("KeyboardViewModel");   // the category lines are attributed to
    }

    [Fact]
    public void TheProductionKeyboardViewModel_RecordsTheEdgeTallyAtTheRealTwentyDwellMinimum()
    {
        // Every view-model and app test swaps the recorder through UseE1Recorder (often with a minimum of
        // one, to pin line formats on a few dwells), so nothing else would notice the production view-model
        // being built with a smaller minimum: a one-word show's tally would be written again, and the leak
        // the minimum closes would be back while every other test stays green.
        var appData = TestAppDataDir.Create("kb-e1-tally-min");
        using var provider = AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData);

        var vm = provider.GetRequiredService<KeyboardViewModel>();

        Check.That(KeyboardE1Recorder.EdgeTallyMinFires).IsEqualTo(20);
        Check.WithCustomMessage("The keyboard's recorder must apply the real edge-tally minimum.")
            .That(vm.E1.EdgeTallyMinFiresInUse).IsEqualTo(KeyboardE1Recorder.EdgeTallyMinFires);
    }
}
