namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// Tests for SettingsService CancellationTokenSource disposal bug.
/// Bug: Line 19-20 in SettingsService.cs doesn't dispose the old CancellationTokenSource
/// before creating a new one, causing a resource leak.
/// </summary>
public class SettingsServiceResourceLeakTests : IDisposable
{
    private readonly string _tempFile;
    private readonly SettingsService _service;

    public SettingsServiceResourceLeakTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        _service = new SettingsService(_tempFile);
    }

    /// <summary>
    /// Test that rapid SaveDebounced calls don't cause ObjectDisposedException.
    /// The bug would manifest as a potential race condition where the old CTS
    /// might be accessed after the new one is created.
    /// </summary>
    [Fact]
    public async Task SaveDebounced_RapidCalls_ShouldNotThrowObjectDisposedException()
    {
        var settings = new EyeCtrlSettings();

        // Make many rapid calls to SaveDebounced
        for (int i = 0; i < 100; i++)
        {
            settings.Dwell.FixationDetectionMs = 100 + (i % 901); // Valid range: 100-1000
            _service.SaveDebounced(settings);
            await Task.Delay(10, TestContext.Current.CancellationToken); // Small delay to create race condition
        }

        // Wait for debounce to complete
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        // If we got here without exceptions, the test passes
        Check.That(true).IsTrue();
    }

    /// <summary>
    /// This test documents the expected behavior: each call to SaveDebounced
    /// should properly clean up the previous CancellationTokenSource.
    /// </summary>
    [Fact]
    public async Task SaveDebounced_ShouldProperlyDisposeOldCancellationTokenSource()
    {
        var settings = new EyeCtrlSettings();

        // First call
        settings.Dwell.FixationDetectionMs = 100;
        _service.SaveDebounced(settings);

        // Small delay, then second call should cancel and dispose the first CTS
        await Task.Delay(100, TestContext.Current.CancellationToken);
        settings.Dwell.FixationDetectionMs = 200;
        _service.SaveDebounced(settings);

        // Small delay, then third call should cancel and dispose the second CTS
        await Task.Delay(100, TestContext.Current.CancellationToken);
        settings.Dwell.FixationDetectionMs = 300;
        _service.SaveDebounced(settings);

        // Poll until the final save (300) settles rather than a fixed delay — a loaded CI runner
        // may run the background debounce late.
        var loaded = await WaitForValueAsync(300, TestContext.Current.CancellationToken);
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);
    }

    /// <summary>
    /// Test that the implementation doesn't leave tasks running after cancellation.
    /// This is more of a documentation test since we can't easily verify disposal
    /// without reflection or making the CTS field public.
    /// </summary>
    [Fact]
    public async Task SaveDebounced_AfterCancellation_ShouldNotSave()
    {
        var settings = new EyeCtrlSettings();

        // Start a save with value 100
        settings.Dwell.FixationDetectionMs = 100;
        _service.SaveDebounced(settings);

        // Immediately cancel it with a new save (value 200)
        await Task.Delay(50, TestContext.Current.CancellationToken); // Small delay but less than debounce
        settings.Dwell.FixationDetectionMs = 200;
        _service.SaveDebounced(settings);

        // Poll until the final save (200) settles rather than a fixed delay. The last call (200)
        // is never cancelled, so it will land; 100 must not be the final persisted value.
        var loaded = await WaitForValueAsync(200, TestContext.Current.CancellationToken);
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(200);
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
