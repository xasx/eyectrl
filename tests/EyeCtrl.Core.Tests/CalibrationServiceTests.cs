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
        var ex = Assert.Throws<InvalidOperationException>(() => { service.StartSession(3, new PhysicalPoint(1920, 1080)); });
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

        Assert.NotNull(data);
        Assert.Equal(TransformType.Affine, data.Type);
        Assert.Equal(3, data.Points.Length);
        Assert.Equal(6, data.TransformParams.Length);
        Assert.Equal(1920, data.ScreenWidth);
        Assert.Equal(1080, data.ScreenHeight);
        Assert.Equal("DISPLAY1", data.MonitorDeviceName);
        Assert.NotNull(service.CurrentTransform);
        Assert.True(data.ComputeMeanErrorPixels() >= 0);
    }

    [Fact]
    public void CurrentTransform_IsNull_WhenNoCalibration()
    {
        var service = new CalibrationService();
        Assert.Null(service.CurrentTransform);
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

        Assert.NotNull(service.CurrentTransform);
        Assert.NotNull(service.CurrentData);
        service.Clear();
        Assert.Null(service.CurrentTransform);
        Assert.Null(service.CurrentData);
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

        Assert.Equal(5, point.SampleCount);
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
        Assert.Null(service2.CurrentTransform);

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
        Assert.NotNull(service2.CurrentTransform);

        File.Delete(path);
    }

    [Fact]
    public void BeginTarget_ThrowsWhenAlreadyCollecting()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);
        service.BeginTarget(0);

        var ex = Assert.Throws<InvalidOperationException>(() => service.BeginTarget(1));
        Assert.Equal("A target is already being collected. Call EndTarget() first.", ex.Message);
    }

    [Fact]
    public void Load_DoesNotThrowOnInvalidJson()
    {
        var logger = new FakeLogger<CalibrationService>();
        var service = new CalibrationService(logger);
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not valid json {{{");

        service.Load(path, new PhysicalPoint(1920, 1080), "DISPLAY1");
        Assert.Null(service.CurrentTransform);
        Assert.True(logger.LoggedCount > 0);

        File.Delete(path);
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
