namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using Microsoft.Extensions.Logging;
using System.IO;

/// <summary>
/// Review-01 R4: the user dictionary was the one save the exit watchdog could corrupt — it was a
/// direct <c>File.WriteAllText</c>, so a FailFast kill mid-write truncated the owner's learned words.
/// <see cref="FileDictionaryProvider.Save(Dictionary{string, int})"/> now stages the JSON in a temp
/// file and swaps it in (the same pattern <c>SaveJson</c> already used), so a write that cannot
/// complete leaves the previous file whole.
/// </summary>
public class FileDictionaryProviderAtomicSaveTests : IDisposable
{
    private readonly string _dir;

    public FileDictionaryProviderAtomicSaveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dict_atomic_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void Save_WhenTheTempFileCannotBeWritten_LeavesTheExistingDictionaryIntact()
    {
        // Arrange: a saved dictionary, and a directory squatting on the staging path so the temp
        // write fails before the destination is ever opened for writing.
        string userPath = Path.Combine(_dir, "user_dictionary_en.json");
        const string oldJson = "{\"old\":3}";
        File.WriteAllText(userPath, oldJson);
        Directory.CreateDirectory(userPath + ".tmp");

        var provider = new FileDictionaryProvider(Path.Combine(_dir, "base.json"), _dir, "en");

        // Act
        Exception? failure = null;
        try
        {
            provider.Save(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["neu"] = 2 });
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Assert: the save reports the failure instead of silently overwriting through it, and the
        // file the owner already had is untouched. A direct WriteAllText replaces it here.
        Check.WithCustomMessage("the save must fail when its temp file cannot be created")
            .That(failure).IsNotNull();
        Check.WithCustomMessage("a failed save must leave the previous dictionary whole")
            .That(File.ReadAllText(userPath)).IsEqualTo(oldJson);
    }

    [Fact]
    public void Save_WhenTheAtomicReplaceIsRefused_WritesDirectly_AndLogsOneWarning()
    {
        // The fallback is the one write that can still be cut short, so it must be visible in the
        // log. Holding the destination WITHOUT delete sharing refuses File.Replace (IOException),
        // while the plain overwrite only needs write sharing (which the holder grants).
        string userPath = Path.Combine(_dir, "user_dictionary_en.json");
        File.WriteAllText(userPath, "{\"old\":3}");
        var logger = new CapturingLogger();
        var provider = new FileDictionaryProvider(Path.Combine(_dir, "base.json"), _dir, "en", logger);

        using (var holder = new FileStream(userPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            provider.Save(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["neu"] = 2 });
        }

        Check.That(File.ReadAllText(userPath)).Contains("neu");
        Check.That(logger.Warnings).HasSize(1);
        Check.That(logger.Warnings[0]).Contains("wrote the file directly");
    }

    private sealed class CapturingLogger : ILogger<FileDictionaryProvider>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                lock (Warnings) Warnings.Add(formatter(state, exception));
        }
    }
}
