namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Interop;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The calibration must be loaded by the time anything resolves the service — not eighty lines later
/// in <c>App.OnStartup</c>.
/// <para>
/// The bug this pins was invisible for exactly that reason. <c>CalibrationService</c> was constructed
/// by the container and loaded afterwards by the caller, so "is a calibration active?" had different
/// answers depending on where in startup you asked. The raw-gaze recorder asked when the gaze provider
/// was resolved, which was earlier, and stamped every recording on a calibrated machine
/// <c>calibrationActive:false</c> with a null transform. Since replay reapplies that transform, those
/// recordings measure the tracker's raw coordinate space while claiming to be the calibrated session —
/// a wrong number that looks like a measurement, and the reason the 2026-09-12 operator session could
/// not be audited.
/// </para>
/// <para>
/// Asserted against the REAL container rather than a hand-built one: the defect was in the composition,
/// so a test that composed its own services would have agreed with the broken code.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class CalibrationLoadOrderingTests : IDisposable
{
    private readonly string _appData = Path.Combine(
        Path.GetTempPath(), "eyectrl-calload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_appData)) Directory.Delete(_appData, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The screen the container will compare a saved calibration against — computed exactly as the
    /// production factory does, so the fixture matches whatever display this test runs on.
    /// </summary>
    private static PhysicalPoint CurrentScreen() => new(
        System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
        System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);

    private static string CurrentMonitorName() =>
        System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";

    private ServiceProvider BuildProvider()
        => AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(_appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            // NEVER the real %APPDATA%\EyeCtrl: that folder holds the user's live calibration, and this
            // test writes one. Clobbering it would take away the calibration their only input method
            // depends on. The parameter is required precisely so this cannot be forgotten.
            appDataDir: _appData);

    /// <summary>Writes a valid calibration for the current display into the fake app-data folder.</summary>
    private void WriteCalibrationForThisMachine()
    {
        Directory.CreateDirectory(_appData);
        var screen = CurrentScreen();

        var source = new CalibrationService();
        source.StartSession(3, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            source.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                source.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            source.EndTarget();
        }

        // Stamped with THIS machine's monitor identity, or Load would reject it on the monitor check
        // and the test would pass for the wrong reason — an unloaded service proving nothing.
        var data = source.CompleteSession(
            screen, CurrentMonitorName(), MonitorIdentity.GetHardwareId(CurrentMonitorName()));
        Check.That(data).IsNotNull();

        source.Save(Path.Combine(_appData, "calibration.json"), data);
    }

    [Fact]
    public void ACalibratedMachine_ResolvesAServiceThatIsAlreadyLoaded()
    {
        WriteCalibrationForThisMachine();

        using var sp = BuildProvider();
        var service = sp.GetRequiredService<CalibrationService>();

        // No Load() call anywhere in this test: resolution is what loads it. Under the old composition
        // this was null until App.OnStartup got round to calling Load.
        Check.That(service.CurrentTransform).IsNotNull();
        Check.That(service.CurrentData).IsNotNull();
        Check.That(service.LastLoadRejection).IsNull();
    }

    [Fact]
    public void ACalibratedMachine_DescribesItsTransformForTheRecordingHeader()
    {
        // The consumer that was actually broken. The recorder turns CurrentTransform into the header's
        // six coefficients at the moment the gaze provider is built; if the service is unloaded then,
        // the recording is stamped uncalibrated and replay silently measures the wrong coordinate space.
        WriteCalibrationForThisMachine();

        using var sp = BuildProvider();
        var service = sp.GetRequiredService<CalibrationService>();

        var described = EyeCtrl.Core.Diagnostics.AffineTransformParameters
            .TryDescribe(service.CurrentTransform);

        Check.That(described).IsNotNull();
    }

    [Fact]
    public void AnUncalibratedMachine_ResolvesAServiceWithNoTransform()
    {
        // The other half of the contract, and the reason the fix is a load rather than a default: a
        // machine with no calibration.json must still report false/null, not a fabricated identity
        // transform. An absent file is not a rejection either — nothing was wrong with it.
        using var sp = BuildProvider();
        var service = sp.GetRequiredService<CalibrationService>();

        Check.That(service.CurrentTransform).IsNull();
        Check.That(service.LastLoadRejection).IsNull();
        Check.That(EyeCtrl.Core.Diagnostics.AffineTransformParameters
            .TryDescribe(service.CurrentTransform)).IsNull();
    }

    [Fact]
    public void TheServiceIsASingleton_SoTheLoadHappensOnce()
    {
        // Loading in the factory would be a per-resolution file read if the registration were not a
        // singleton — on the gaze path, during startup. Same instance, one load.
        WriteCalibrationForThisMachine();

        using var sp = BuildProvider();

        Check.That(sp.GetRequiredService<CalibrationService>())
             .IsSameReferenceAs(sp.GetRequiredService<CalibrationService>());
    }
}
