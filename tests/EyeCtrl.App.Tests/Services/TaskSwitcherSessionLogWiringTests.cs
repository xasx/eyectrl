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
/// debate's look-time ladder. The clock half is pinned with a DISTINCT instance: the container's
/// production registration and the view model's <c>?? TimeProvider.System</c> fallback are the
/// same object, so only a substituted clock can tell them apart.
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
        var clock = new DistinctClock();
        using var provider = AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData,
            clock: clock);

        var vm = provider.GetRequiredService<TaskSwitcherViewModel>();

        var logger = Field(vm, "_logger");
        Check.WithCustomMessage("The switcher got no logger from the container: both session lines would be "
                + "silently absent while every other test stays green.")
            .That(logger).IsNotNull();
        Check.That(logger!.GetType().FullName).Contains("TaskSwitcherViewModel");

        // A FAKE clock, not TimeProvider.System: production's registered clock and the view
        // model's `?? TimeProvider.System` fallback are the same instance, so asserting against
        // the system clock would pass even if the parameter were never injected. With a distinct
        // instance the assertion can fail.
        Check.WithCustomMessage("The switcher must read the container's clock, not its own TimeProvider.System "
                + "fallback, or its timings come from a second source.")
            .That(Field(vm, "_timeProvider"))
            .IsSameReferenceAs(clock);
    }

    /// <summary>
    /// A clock that is provably NOT <see cref="TimeProvider.System"/>, so the identity assertion
    /// can fail. Nothing subscribes a timer to it — only the switcher view model is resolved.
    /// </summary>
    private sealed class DistinctClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
