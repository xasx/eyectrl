namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Reflection;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// T3's composition-root wiring, pinned against the REAL container. The switcher's session log
/// arrives through two optional constructor parameters (a logger and the clock), and optional
/// means a registration that silently went missing leaves the app building fine with the log
/// quietly off — green everywhere except the owner's day log, which is the only source for the
/// debate's look-time ladder. The clock half matters just as much: the container owns the one
/// system clock (<c>AppBootstrapper</c> registers <see cref="TimeProvider.System"/>), so a
/// second source here would make every <c>openMs</c> incomparable with the rest of the log.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class TaskSwitcherSessionLogWiringTests
{
    private static object? Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner);

    [Fact]
    public void TheTaskSwitcherViewModel_GetsTheContainersLoggerAndClock()
    {
        var appData = TestAppDataDir.Create("task-switcher-session-log");
        using var provider = AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData);

        var vm = provider.GetRequiredService<TaskSwitcherViewModel>();

        var logger = Field(vm, "_logger");
        Check.WithCustomMessage("The switcher got no logger from the container: both session lines would be "
                + "silently absent while every other test stays green.")
            .That(logger).IsNotNull();
        Check.That(logger!.GetType().FullName).Contains("TaskSwitcherViewModel");

        Check.WithCustomMessage("The switcher must read the container's clock, not its own TimeProvider.System "
                + "fallback, or its timings come from a second source.")
            .That(Field(vm, "_timeProvider"))
            .IsSameReferenceAs(provider.GetRequiredService<TimeProvider>());
    }
}
