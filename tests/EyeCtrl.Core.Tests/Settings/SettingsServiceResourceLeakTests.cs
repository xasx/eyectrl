namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tests for SettingsService CancellationTokenSource disposal behavior.
/// Bug: Line 19-20 in SettingsService.cs doesn't dispose the old CancellationTokenSource
/// before creating a new one, causing a resource leak.
/// </summary>
public class SettingsServiceResourceLeakTests : IDisposable
{
    private readonly string _tempFile;
    private readonly SettingsService _service;
    private readonly RecordingLogger _logger;

    public SettingsServiceResourceLeakTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        // Box-wide rule for named kernel objects (AGENTS.md): the production writer mutex
        // (Local\EyeCtrl.Settings.Writer) is machine-global, so a test instance holding it can
        // contend with a live EyeCtrl or a sibling worktree's suite on this box. Every test
        // here constructs its service with a GUID-suffixed mutex name instead.
        _logger = new RecordingLogger();
        _service = new SettingsService(
            _tempFile,
            SettingsWriter.App,
            $"Local\\EyeCtrl.Settings.Writer.Test.{Guid.NewGuid():N}",
            _logger);
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

        // The final state must actually land: a non-default final value (456) proves the file
        // reflects the last SaveDebounced rather than a default-valued Load() on a missing file.
        settings.Dwell.FixationDetectionMs = 456;
        _service.SaveDebounced(settings);
        var loaded = await WaitForValueAsync(456, TestContext.Current.CancellationToken);
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(456);

        // The disposal race this test guards surfaced as an ObjectDisposedException on the
        // background save path — none of that (or any other error) may have been logged.
        Check.WithCustomMessage(
                "The debounce loop must complete without a single error-level log entry " +
                "(ObjectDisposedException or otherwise).")
            .That(_logger.Errors).IsEmpty();
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
        settings.Dwell.FixationDetectionMs = 150;
        _service.SaveDebounced(settings);

        // Small delay, then second call should cancel and dispose the first CTS
        await Task.Delay(100, TestContext.Current.CancellationToken);
        settings.Dwell.FixationDetectionMs = 250;
        _service.SaveDebounced(settings);

        // Small delay, then third call should cancel and dispose the second CTS. The final
        // value is 350, NOT 300 — 300 is DwellSettings.FixationDetectionMs's default, so a
        // poll for "300" could be satisfied by a default-valued Load() on a file that was
        // never written.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        settings.Dwell.FixationDetectionMs = 350;
        _service.SaveDebounced(settings);

        // Poll until the final save (350) settles rather than a fixed delay — a loaded CI runner
        // may run the background debounce late.
        var loaded = await WaitForValueAsync(350, TestContext.Current.CancellationToken);
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(350);
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
    // Fails EXPLICITLY on timeout rather than falling back to a default-valued Load(): the old
    // fallback could satisfy an expected value that coincides with a settings default even when
    // the file was never written.
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
        Check.WithCustomMessage(
                $"The debounced save never settled: expected FixationDetectionMs {expected} " +
                "within ~5s (file missing or stuck at an earlier value).")
            .That(File.Exists(_tempFile)).IsTrue();
        return _service.Load(); // exists but stale — the caller's value assert fails loudly here
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

    // No mocking library (AGENTS.md): a private recording ILogger that captures error-level
    // entries so the tests can assert the background save path stayed clean.
    private sealed class RecordingLogger : ILogger<SettingsService>
    {
        public List<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }
}
