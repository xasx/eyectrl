namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Reflection;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The clock the calibration timers run on is PRODUCTION wiring, and the failure it prevents is
/// invisible from every other test: the measurement tests supply their own FakeTimeProvider, so a
/// production container that passed a frozen clock (or a different instance per consumer) would keep
/// them all green — while every real calibration session hung on caps that never fire.
/// <para>
/// Same discipline as <see cref="DwellCueWiringTests"/>: build the real container over a throwaway
/// app-data folder (resolving <c>CalibrationService</c> loads <c>calibration.json</c>), and assert
/// the identity the factories actually captured rather than trusting the registration to be read.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class CalibrationClockWiringTests
{
    [Fact]
    public void TheCalibrationTimers_RunOnTheSystemClock()
    {
        var appData = TestAppDataDir.Create("cal-clock-wiring");
        using var provider = AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData);

        Check.WithCustomMessage(
            "The registered clock must BE the system clock — a frozen substitute here hangs every "
            + "calibration session, and no other test can see it (they inject their own fake).")
            .That(provider.GetRequiredService<TimeProvider>()).IsSameReferenceAs(TimeProvider.System);

        // Resolved through the factories that production uses, so this catches a substitution at the
        // registration OR at either call site.
        Check.WithCustomMessage(
            "CalibrationViewModel's timers must run on the container's clock (AppBootstrapper must "
            + "pass sp.GetRequiredService<TimeProvider>()); a per-factory system/frozen clock breaks "
            + "the single-timeline contract the settle gate shares with the caps and the ring.")
            .That(ReadClock(provider.GetRequiredService<CalibrationViewModel>()))
            .IsSameReferenceAs(TimeProvider.System);

        Check.WithCustomMessage(
            "CalibrationService's collector must run on the same container clock, so the settle gate "
            + "and the view model's windows share one timeline.")
            .That(ReadClock(provider.GetRequiredService<CalibrationService>()))
            .IsSameReferenceAs(TimeProvider.System);
    }

    /// <summary>The private clock field each consumer keeps, read the DwellCueWiringTests way.</summary>
    private static object? ReadClock(object consumer) =>
        consumer.GetType()
            .GetField("_timeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(consumer);
}
