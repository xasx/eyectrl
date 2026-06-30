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

        // Wait for the debounced save (500ms debounce + file I/O) to land — poll instead of a
        // fixed delay so a slow/loaded CI runner doesn't flake.
        await WaitForFileAsync(TestContext.Current.CancellationToken);

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

        // Wait for the debounced save to land — poll instead of a fixed delay (see above).
        await WaitForFileAsync(TestContext.Current.CancellationToken);

        var loaded = _service.Load();
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);
    }

    // Polls for the debounced save to produce the file (up to ~5s), returning as soon as it
    // appears. Deterministic on slow CI runners, unlike a fixed Task.Delay.
    private async Task WaitForFileAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100 && !File.Exists(_tempFile); i++)
            await Task.Delay(50, ct);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(_tempFile)) File.Delete(_tempFile);
                break;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }
}
