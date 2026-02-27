namespace EyeCtrl.Core.Tests.Settings;
using EyeCtrl.Core.Settings;

public class SettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips_AllSettings()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            settings.Cursor.SmoothingFactor = 0.7;
            var service = new SettingsService(tempPath);
            service.Save(settings);
            var loaded = service.Load();
            Assert.Equal(600, loaded.Dwell.ActivationButtonDwellMs);
            Assert.Equal(0.7, loaded.Cursor.SmoothingFactor);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var service = new SettingsService("/nonexistent/path.json");
        var settings = service.Load();
        Assert.NotNull(settings);
        Assert.Equal(500, settings.Dwell.ActivationButtonDwellMs);
    }
}
