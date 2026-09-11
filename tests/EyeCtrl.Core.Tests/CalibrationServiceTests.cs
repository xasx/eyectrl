namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

public class CalibrationServiceTests
{
    [Fact]
    public void StartSession_ThrowsWhenAlreadyCollecting()
    {
        var service = new CalibrationService();
        service.StartSession(5, new PhysicalPoint(1920, 1080));
        Check.ThatCode(() => { service.StartSession(3, new PhysicalPoint(1920, 1080)); }).Throws<InvalidOperationException>();
    }

    [Fact]
    public void CompleteSession_ReturnsValidData()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);

        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 10, targets[i].Y - 5));
            service.EndTarget();
        }

        var data = service.CompleteSession(screen, "DISPLAY1");

        Check.That(data).IsNotNull();
        Check.That(data.Type).IsEqualTo(TransformType.Affine);
        Check.That(data.Points.Length).IsEqualTo(3);
        Check.That(data.TransformParams.Length).IsEqualTo(6);
        Check.That(data.ScreenWidth).IsEqualTo(1920);
        Check.That(data.ScreenHeight).IsEqualTo(1080);
        Check.That(data.MonitorDeviceName).IsEqualTo("DISPLAY1");
        Check.That(service.CurrentTransform).IsNotNull();
        Check.That(data.ComputeMeanErrorPixels() >= 0).IsTrue();
    }

    [Fact]
    public void CurrentTransform_IsNull_WhenNoCalibration()
    {
        var service = new CalibrationService();
        Check.That(service.CurrentTransform).IsNull();
    }

    [Fact]
    public void Clear_ResetsTransformToNull()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);

        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        service.CompleteSession(screen, "DISPLAY1");

        Check.That(service.CurrentTransform).IsNotNull();
        Check.That(service.CurrentData).IsNotNull();
        service.Clear();
        Check.That(service.CurrentTransform).IsNull();
        Check.That(service.CurrentData).IsNull();
    }

    [Fact]
    public void BeginTarget_HonorsMaxSamplesCap()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);

        service.BeginTarget(0, settleWindowMs: 0, maxSamples: 5);
        for (int j = 0; j < 50; j++)
            service.AddRawSample(new PhysicalPoint(100, 100));
        var point = service.EndTarget();

        Check.That(point.SampleCount).IsEqualTo(5);
    }

    [Fact]
    public void Load_DiscardsOnResolutionMismatch()
    {
        var service = new CalibrationService();
        var screen1 = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen1);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen1);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        var data = service.CompleteSession(screen1, "DISPLAY1");

        var path = Path.GetTempFileName();
        service.Save(path, data);

        var service2 = new CalibrationService();
        service2.Load(path, new PhysicalPoint(2560, 1440), "DISPLAY2");
        Check.That(service2.CurrentTransform).IsNull();

        File.Delete(path);
    }

    [Fact]
    public void Load_SucceedsOnMatchingResolution()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        var data = service.CompleteSession(screen, "DISPLAY1");

        var path = Path.GetTempFileName();
        service.Save(path, data);

        var service2 = new CalibrationService();
        service2.Load(path, screen, "DISPLAY1");
        Check.That(service2.CurrentTransform).IsNotNull();

        File.Delete(path);
    }

    [Fact]
    public void BeginTarget_ThrowsWhenAlreadyCollecting()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);
        service.BeginTarget(0);

        InvalidOperationException? ex = null;
        try { service.BeginTarget(1); }
        catch (InvalidOperationException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.Message).IsEqualTo("A target is already being collected. Call EndTarget() first.");
    }

    [Fact]
    public void Load_DoesNotThrowOnInvalidJson()
    {
        var logger = new FakeLogger<CalibrationService>();
        var service = new CalibrationService(logger);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not valid json {{{");

            service.Load(path, new PhysicalPoint(1920, 1080), "DISPLAY1");
            Check.That(service.CurrentTransform).IsNull();
            Check.That(logger.LoggedCount > 0).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DoesNotThrowOnInvalidTransformParams()
    {
        // Valid JSON whose screen/monitor match, but transformParams has the wrong length.
        // AffineTransform.SetParameters would throw ArgumentException — Load must swallow it
        // (and the pre-check must short-circuit) rather than crash startup.
        var logger = new FakeLogger<CalibrationService>();
        var service = new CalibrationService(logger);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"transformParams\":[1.0,0.0,0.0],\"screenWidth\":1920,\"screenHeight\":1080,\"monitorDeviceName\":\"DISPLAY1\"}");

            service.Load(path, new PhysicalPoint(1920, 1080), "DISPLAY1");
            Check.That(service.CurrentTransform).IsNull();
            Check.That(logger.LoggedCount > 0).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompleteSession_Throws_AndDoesNotApply_OnDegenerateCalibration()
    {
        // Reproduces the 2026-06-16 failure: gaze Y barely varied across targets that span
        // the whole screen, so the fit's Y-slope explodes and maps gaze far off-screen.
        // The fit is finite and non-singular, so only an explicit sanity check catches it.
        var service = new CalibrationService();
        var screen = new PhysicalPoint(3840, 2400);
        service.StartSession(5, screen);

        var targets = CalibrationTargetPositionHelper.GetPositions(5, screen);
        for (int i = 0; i < 5; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X, i * 3.0)); // Y collapsed
            service.EndTarget();
        }

        Check.ThatCode(() => service.CompleteSession(screen, "DISPLAY1")).Throws<InvalidOperationException>();
        Check.That(service.CurrentTransform).IsNull();
    }

    [Fact]
    public void Load_IgnoresDegenerateTransform()
    {
        // The exact params persisted on 2026-06-16: they map screen centre millions of px off-screen.
        var logger = new FakeLogger<CalibrationService>();
        var service = new CalibrationService(logger);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"transformParams\":[164.17311285043388,-10147.434961907775,11285.824336762067," +
                "-59.27218454102549,3683.7487218623305,-2847.0947780201545]," +
                "\"screenWidth\":3840,\"screenHeight\":2400,\"monitorDeviceName\":\"DISPLAY1\"}");

            service.Load(path, new PhysicalPoint(3840, 2400), "DISPLAY1");

            Check.That(service.CurrentTransform).IsNull();
            Check.That(logger.LoggedCount > 0).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_IgnoresJitterAmplifyingTransform()
    {
        // The exact params persisted on 2026-07-14: they stay on-screen (so the older IsTransformSane guard
        // accepts them) but flip and amplify gaze, breaking fixation in production. Load must ignore them and
        // leave the pipeline on raw (tracker-calibrated) gaze.
        var logger = new FakeLogger<CalibrationService>();
        var service = new CalibrationService(logger);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"transformParams\":[-1.4792189234724626,-1.3271156575795569,3201.4014928696142," +
                "-0.3181342085970778,0.5928388160665401,1170.8048259684977]," +
                "\"screenWidth\":3840,\"screenHeight\":2400,\"monitorDeviceName\":\"DISPLAY1\"}");

            service.Load(path, new PhysicalPoint(3840, 2400), "DISPLAY1");

            Check.That(service.CurrentTransform).IsNull();
            Check.That(logger.LoggedCount > 0).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddRawSample_DropsImplausibleSamples()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(3840, 2400);
        service.StartSession(3, screen);

        service.BeginTarget(0);
        // On-target samples (the collector's arrival gate: sample 1 establishes arrival, the
        // rest count), then implausible junk that must be dropped before the collector.
        var target0 = service.GetTargetPosition(0);
        for (int j = 0; j < 5; j++)
            service.AddRawSample(new PhysicalPoint(target0.X + j, target0.Y - j));
        service.AddRawSample(new PhysicalPoint(double.NaN, 0));   // junk
        service.AddRawSample(new PhysicalPoint(1_000_000, 1200)); // junk, far off-screen
        var point = service.EndTarget();

        Check.That(point.SampleCount).IsEqualTo(4);
    }

    [Fact]
    public void CompleteSession_Throws_WhenGazeDidNotVaryAcrossTargets()
    {
        // Sensor returned a constant (the 2026-06-16 signature: meanGaze (1,1), stdDev 0)
        // regardless of which target the user looked at.
        var service = new CalibrationService();
        var screen = new PhysicalPoint(3840, 2400);
        service.StartSession(5, screen);

        for (int i = 0; i < 5; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 27; j++)
                service.AddRawSample(new PhysicalPoint(1, 1)); // never moves
            service.EndTarget();
        }

        Check.ThatCode(() => service.CompleteSession(screen, "DISPLAY1")).Throws<InvalidOperationException>();
        Check.That(service.CurrentTransform).IsNull();
    }

    // ── Atomic save ──
    // calibration.json is the one file whose corruption breaks EVERY gaze interaction at once (its
    // transform is applied to every point), so Save writes a temp file and renames it: the destination
    // only ever holds the complete old file or the complete new one. A torn write cannot be simulated
    // here — the shape is the guarantee — so these pin the observable consequences of that shape.

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var path = SaveValidCalibration(screen, "DISPLAY1");
        try
        {
            // Fails if the rename was dropped and the temp file became the real write.
            Check.That(File.Exists(path + ".tmp")).IsFalse();
            Check.That(File.Exists(path)).IsTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_OverAnExistingFile_ReplacesIt()
    {
        // The overwrite flag on the rename: without it the second save throws and the first
        // calibration is what survives, silently ignoring the recalibration the user just did.
        var screen = new PhysicalPoint(1920, 1080);
        var path = SaveValidCalibration(screen, "DISPLAY1");
        try
        {
            var service = new CalibrationService();
            service.StartSession(3, screen);
            var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
            for (int i = 0; i < 3; i++)
            {
                service.BeginTarget(i);
                for (int j = 0; j < 30; j++)
                    service.AddRawSample(new PhysicalPoint(targets[i].X + 2, targets[i].Y));
                service.EndTarget();
            }
            var second = service.CompleteSession(screen, "DISPLAY2");

            Check.ThatCode(() => service.Save(path, second)).DoesNotThrow();

            var reloaded = new CalibrationService();
            reloaded.Load(path, screen, "DISPLAY2");
            Check.That(reloaded.CurrentTransform).IsNotNull();
            Check.That(reloaded.CurrentData!.MonitorDeviceName).IsEqualTo("DISPLAY2");
            Check.That(File.Exists(path + ".tmp")).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MonitorNameRefresh_RoutesThroughSave_AndPersists()
    {
        // The startup rewrite: a hardware-id match under a renumbered display refreshes the stored GDI
        // name through Save, which is what makes that write atomic.
        // NB: crash-atomicity itself is NOT simulated here — it is a property of Save's temp+move. What
        // this pins is that the refresh goes through that path (no leftover temp file) and persists.
        var screen = new PhysicalPoint(1920, 1080);
        var path = SaveValidCalibration(screen, "\\\\.\\DISPLAY1", "EDID-ABC");
        try
        {
            var service = new CalibrationService();
            service.Load(path, screen, "\\\\.\\DISPLAY3", "EDID-ABC");

            Check.That(service.CurrentTransform).IsNotNull();
            Check.That(service.CurrentData!.MonitorDeviceName).IsEqualTo("\\\\.\\DISPLAY3");
            Check.That(File.Exists(path + ".tmp")).IsFalse();

            // And the refreshed name really reached disk, so the legacy name-only path stays accurate.
            var reloaded = new CalibrationService();
            reloaded.Load(path, screen, "\\\\.\\DISPLAY3");
            Check.That(reloaded.CurrentTransform).IsNotNull();
        }
        finally { File.Delete(path); }
    }

    // ── Monitor / screen identity on load ──
    // Until 2026-08-02 a screen-or-monitor mismatch was a bare `return` with no log of any kind, so a
    // calibration silently stopped applying and the only symptom was drifting dwell. Windows renumbers
    // \\.\DISPLAYn whenever monitors are attached, detached, or re-enumerated, so this recurs; these
    // tests pin both that the rejection is observable and that a stable hardware id prevents it.

    /// <summary>Saves a valid calibration for the given monitor identity and returns its file path.</summary>
    private static string SaveValidCalibration(
        PhysicalPoint screen, string monitorDeviceName, string monitorHardwareId = "")
    {
        var service = new CalibrationService();
        service.StartSession(3, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        var data = service.CompleteSession(screen, monitorDeviceName, monitorHardwareId);

        var path = Path.GetTempFileName();
        service.Save(path, data);
        return path;
    }

    [Fact]
    public void Load_LogsWarningNamingBothMonitors_OnMonitorMismatch()
    {
        var screen = new PhysicalPoint(3840, 2400);
        var path = SaveValidCalibration(screen, "\\\\.\\DISPLAY14");
        try
        {
            var logger = new FakeLogger<CalibrationService>();
            var service = new CalibrationService(logger);

            // DISPLAY2, not DISPLAY1, as the current name: "DISPLAY14" contains "DISPLAY1", so a
            // Contains("DISPLAY1") assertion would pass on the persisted value alone and prove nothing.
            service.Load(path, screen, "\\\\.\\DISPLAY2");

            Check.That(service.CurrentTransform).IsNull();

            // The whole point: the log must answer "why did my calibration stop applying?" on its own,
            // which means naming the persisted AND the current monitor.
            var warning = logger.Warnings.FirstOrDefault(w => w.Contains("different monitor"));
            Check.That(warning).IsNotNull();
            Check.That(warning!).Contains("DISPLAY14");
            Check.That(warning!).Contains("DISPLAY2");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LogsWarningNamingBothSizes_OnScreenSizeMismatch()
    {
        var path = SaveValidCalibration(new PhysicalPoint(3840, 2400), "DISPLAY1");
        try
        {
            var logger = new FakeLogger<CalibrationService>();
            var service = new CalibrationService(logger);

            service.Load(path, new PhysicalPoint(1920, 1080), "DISPLAY1");

            Check.That(service.CurrentTransform).IsNull();
            var warning = logger.Warnings.FirstOrDefault(w => w.Contains("3840x2400"));
            Check.That(warning).IsNotNull();
            Check.That(warning!).Contains("1920x1080");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RecordsRejectionNamingBothMonitors_OnMonitorMismatch()
    {
        // LastLoadRejection is what the settings Calibration tab shows, so it must distinguish
        // "never calibrated" from "calibrated, but inert because the monitor changed".
        var screen = new PhysicalPoint(3840, 2400);
        var path = SaveValidCalibration(screen, "\\\\.\\DISPLAY14", "AUOC5AC");
        try
        {
            var service = new CalibrationService();
            // DISPLAY2 rather than DISPLAY1: see Load_LogsWarningNamingBothMonitors_OnMonitorMismatch.
            service.Load(path, screen, "\\\\.\\DISPLAY2", "DELA1CF");

            Check.That(service.CurrentTransform).IsNull();
            Check.That(service.CurrentData).IsNull();
            Check.That(service.LastLoadRejection).IsNotNull();
            Check.That(service.LastLoadRejection!).Contains("DISPLAY14");
            Check.That(service.LastLoadRejection!).Contains("DISPLAY2");
            Check.That(service.LastLoadRejection!).Contains("AUOC5AC");
            Check.That(service.LastLoadRejection!).Contains("DELA1CF");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_AppliesCalibration_WhenHardwareIdMatchesButDisplayWasRenumbered()
    {
        // The fix for the recurring failure: same physical monitor, new \\.\DISPLAYn index.
        var screen = new PhysicalPoint(3840, 2400);
        var path = SaveValidCalibration(screen, "\\\\.\\DISPLAY14", "AUOC5AC");
        try
        {
            var service = new CalibrationService();
            service.Load(path, screen, "\\\\.\\DISPLAY1", "AUOC5AC");

            Check.That(service.CurrentTransform).IsNotNull();
            Check.That(service.LastLoadRejection).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RefreshesStoredDeviceName_AfterHardwareIdMatch()
    {
        // Keeps the legacy name-only path accurate for the day the hardware id cannot be read.
        var screen = new PhysicalPoint(3840, 2400);
        var path = SaveValidCalibration(screen, "\\\\.\\DISPLAY14", "AUOC5AC");
        try
        {
            var service = new CalibrationService();
            service.Load(path, screen, "\\\\.\\DISPLAY1", "AUOC5AC");

            Check.That(service.CurrentData).IsNotNull();
            Check.That(service.CurrentData!.MonitorDeviceName).IsEqualTo("\\\\.\\DISPLAY1");
            Check.That(service.CurrentData!.MonitorHardwareId).IsEqualTo("AUOC5AC");

            // Persisted, not just in memory.
            var reloaded = new CalibrationService();
            reloaded.Load(path, screen, "\\\\.\\DISPLAY1");
            Check.That(reloaded.CurrentTransform).IsNotNull();
            Check.That(reloaded.CurrentData!.MonitorDeviceName).IsEqualTo("\\\\.\\DISPLAY1");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RejectsCalibration_WhenHardwareIdDiffers_EvenThoughDeviceNameMatches()
    {
        // A renumbering can also hand the OLD name to a DIFFERENT monitor, so the hardware id must win.
        var screen = new PhysicalPoint(3840, 2400);
        var path = SaveValidCalibration(screen, "\\\\.\\DISPLAY1", "AUOC5AC");
        try
        {
            var logger = new FakeLogger<CalibrationService>();
            var service = new CalibrationService(logger);

            service.Load(path, screen, "\\\\.\\DISPLAY1", "DELA1CF");

            Check.That(service.CurrentTransform).IsNull();
            Check.That(logger.Warnings.Any(w => w.Contains("different monitor"))).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_FallsBackToDeviceName_ForLegacyRecordWithoutHardwareId()
    {
        // Records written before MonitorHardwareId existed must keep loading on a name match.
        var screen = new PhysicalPoint(1920, 1080);
        var path = SaveValidCalibration(screen, "DISPLAY1");
        try
        {
            var service = new CalibrationService();
            service.Load(path, screen, "DISPLAY1", "AUOC5AC");

            Check.That(service.CurrentTransform).IsNotNull();
            Check.That(service.LastLoadRejection).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LastLoadRejection_IsClearedByASuccessfulLoadAndByClear()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var path = SaveValidCalibration(screen, "DISPLAY1");
        try
        {
            var service = new CalibrationService();

            service.Load(path, screen, "DISPLAY2");
            Check.That(service.LastLoadRejection).IsNotNull();

            service.Load(path, screen, "DISPLAY1");
            Check.That(service.LastLoadRejection).IsNull();

            service.Load(path, screen, "DISPLAY2");
            Check.That(service.LastLoadRejection).IsNotNull();
            service.Clear();
            Check.That(service.LastLoadRejection).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompleteSession_PersistsMonitorHardwareId()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }

        var data = service.CompleteSession(screen, "\\\\.\\DISPLAY1", "AUOC5AC");

        Check.That(data.MonitorDeviceName).IsEqualTo("\\\\.\\DISPLAY1");
        Check.That(data.MonitorHardwareId).IsEqualTo("AUOC5AC");
    }

    private sealed class FakeLogger<T> : ILogger<T>
    {
        public int LoggedCount { get; private set; }

        /// <summary>Formatted text of every entry, so tests can assert what a warning actually names.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <summary>Formatted text of the warning-or-worse entries only.</summary>
        public IEnumerable<string> Warnings =>
            Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LoggedCount++;
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
