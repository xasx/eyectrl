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

        // Poll until the LAST debounced save (300) settles. Waiting only for the file to exist is
        // not enough: on a thread-pool-starved CI runner an earlier, not-yet-cancelled save can
        // write an intermediate value first, so we must wait for the final value.
        var loaded = await WaitForValueAsync(300, TestContext.Current.CancellationToken);
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);
    }

    // Polls for the debounced save to produce the file (up to ~5s), returning as soon as it
    // appears. Deterministic on slow CI runners, unlike a fixed Task.Delay.
    private async Task WaitForFileAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100 && !File.Exists(_tempFile); i++)
            await Task.Delay(50, ct);
    }

    // Polls until the persisted FixationDetectionMs reaches the expected final value (up to ~5s).
    // Requires the file to exist so a default-valued Load() can't produce a false positive.
    private async Task<EyeCtrlSettings> WaitForValueAsync(int expected, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(_tempFile))
            {
                var loaded = _service.Load();
                if (loaded.Dwell.FixationDetectionMs == expected)
                    return loaded;
            }
            await Task.Delay(50, ct);
        }
        return _service.Load();
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
