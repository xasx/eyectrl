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
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var service = new SettingsService("/nonexistent/path.json");
        var settings = service.Load();
        Check.That(settings).IsNotNull();
        Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(700);
    }
}
