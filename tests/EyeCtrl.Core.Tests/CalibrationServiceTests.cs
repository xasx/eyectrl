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
        var ex = Check.ThatCode(() => { service.StartSession(3, new PhysicalPoint(1920, 1080)); }).Throws<InvalidOperationException>();
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
    public void AddRawSample_DropsImplausibleSamples()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(3840, 2400);
        service.StartSession(3, screen);

        service.BeginTarget(0);
        for (int j = 0; j < 5; j++)
            service.AddRawSample(new PhysicalPoint(1920, 1200)); // plausible, on-screen
        service.AddRawSample(new PhysicalPoint(double.NaN, 0));   // junk
        service.AddRawSample(new PhysicalPoint(1_000_000, 1200)); // junk, far off-screen
        var point = service.EndTarget();

        Check.That(point.SampleCount).IsEqualTo(5);
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

    private sealed class FakeLogger<T> : ILogger<T>
    {
        public int LoggedCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LoggedCount++;
        }
    }
}
