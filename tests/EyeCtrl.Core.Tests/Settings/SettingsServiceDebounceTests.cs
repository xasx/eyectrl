namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class SettingsServiceDebounceTests : IDisposable
{
    private readonly string _tempFile;
    private readonly SettingsService _service;

    public SettingsServiceDebounceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        _service = new SettingsService(_tempFile);
    }

    [Fact]
    public async Task SaveDebounced_SingleCall_SavesAfterDelay()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationDetectionMs = 999;

        _service.SaveDebounced(settings);

        // Not saved yet immediately
        Check.That(File.Exists(_tempFile)).IsFalse();

        // Wait for debounce (500ms + buffer)
        await Task.Delay(800, TestContext.Current.CancellationToken);

        Check.That(File.Exists(_tempFile)).IsTrue();
        var loaded = _service.Load();
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(999);
    }

    [Fact]
    public async Task SaveDebounced_RapidCalls_OnlySavesLast()
    {
        var settings = new EyeCtrlSettings();

        settings.Dwell.FixationDetectionMs = 100;
        _service.SaveDebounced(settings);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        settings.Dwell.FixationDetectionMs = 200;
        _service.SaveDebounced(settings);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        settings.Dwell.FixationDetectionMs = 300;
        _service.SaveDebounced(settings);

        // Wait for debounce
        await Task.Delay(800, TestContext.Current.CancellationToken);

        var loaded = _service.Load();
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }
}
