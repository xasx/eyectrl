namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Reflection;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Pins condition 1 of the adaptation gate at the only place it can be observed: the REAL container's
/// construction of <see cref="StruggleMonitor"/>. The Core loop test proves what the armed and
/// disarmed monitor each do; nothing there can see <c>AppBootstrapper</c>, so this fact is what stops
/// someone "restoring" the null argument and silently re-arming the
/// wrong-key → Backspace → longer-dwell ratchet.
///
/// <para>
/// The argument is <c>backspaceTargetId</c>, an OPTIONAL constructor parameter — passing
/// <c>"backspace"</c> fails no build and throws nothing, which is why the wiring needs a resolved
/// instance rather than a compile-time check. This is the <see cref="DwellRoutingRegistrationWiringTests"/>
/// pattern one level over: build the real container, resolve the owner, assert the wiring the factory
/// must land. It does not resolve ViewModels (see <see cref="TelemetrySinkRegistrationTests"/>).
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class StruggleMonitorRegistrationTests
{
    // A temp app-data folder, never the real one — the container resolves CalibrationService
    // transitively and that load can rewrite the user's calibration.json. See TestAppDataDir.
    private static readonly string AppData = TestAppDataDir.Create("struggle-monitor");

    private static ServiceProvider BuildProvider() =>
        AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    [Fact]
    public void ResolvedMonitor_CarriesNoBackspaceTarget()
    {
        using var sp = BuildProvider();

        var monitor = sp.GetRequiredService<StruggleMonitor>();
        var field = typeof(StruggleMonitor).GetField(
            "_backspaceTargetId", BindingFlags.NonPublic | BindingFlags.Instance);
        Check.WithCustomMessage(
                "StruggleMonitor's backspaceTargetId field moved or was renamed; this pin can no "
                + "longer see the wiring it guards.")
            .That(field).IsNotNull();

        Check.WithCustomMessage(
                "The app resolved a StruggleMonitor with a backspace target id. That re-arms the " +
                "corroboration that scores the user's Backspace correction as accidental activation " +
                "and lengthens the dwell — the keyboard answers a struggling user by getting slower. " +
                "See the comment at the registration site before changing this back; the null is the " +
                "fix, not an omission.")
            .That(field!.GetValue(monitor)).IsNull();
    }
}
