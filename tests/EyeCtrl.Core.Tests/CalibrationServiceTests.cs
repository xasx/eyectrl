namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

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
            service.BeginTarget(i, screen);
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
            service.BeginTarget(i, screen);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        service.CompleteSession(screen, "DISPLAY1");

        Assert.NotNull(service.CurrentTransform);
        service.Clear();
        Assert.Null(service.CurrentTransform);
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
            service.BeginTarget(i, screen1);
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
            service.BeginTarget(i, screen);
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
}
