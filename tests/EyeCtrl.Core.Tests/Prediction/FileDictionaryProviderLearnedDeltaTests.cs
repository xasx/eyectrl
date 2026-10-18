namespace EyeCtrl.Core.Tests.Prediction;

using System.IO;
using System.Text.Json;
using EyeCtrl.Core.Prediction;
using Microsoft.Extensions.Logging;

/// <summary>
/// The user dictionary file stores only the LEARNED DELTA, not the merged dictionary. These
/// gates also pin the one-time repair of the owner's file, which had the whole base dictionary
/// written back on every save (base x 57-59).
/// </summary>
public class FileDictionaryProviderLearnedDeltaTests : IDisposable
{
    private readonly string _dir;

    public FileDictionaryProviderLearnedDeltaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dict_delta_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string BasePath => Path.Combine(_dir, "base.json");
    private string UserPath => Path.Combine(_dir, "user_dictionary_en.json");
    private string BackupPath => UserPath + ".bak-inflated";

    private void WriteBase(Dictionary<string, int> words)
        => File.WriteAllText(BasePath, JsonSerializer.Serialize(new { words }));

    private void WriteUser(Dictionary<string, int> words)
        => File.WriteAllText(UserPath, JsonSerializer.Serialize(words));

    private Dictionary<string, int> ReadUser()
        => JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(UserPath))!;

    private FileDictionaryProvider CreateProvider(ILogger<FileDictionaryProvider>? logger = null)
        => new(BasePath, _dir, "en", logger);

    [Fact]
    public void LoadSaveLoad_RepeatedCycles_NeverChangeCounts()
    {
        WriteBase(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 50 });
        WriteUser(new Dictionary<string, int> { ["hello"] = 3 });

        var provider = CreateProvider();
        var first = provider.Load();
        Check.That(first["hello"]).IsEqualTo(103);
        Check.That(first["world"]).IsEqualTo(50);

        for (var cycle = 0; cycle < 5; cycle++)
        {
            provider.Save(first);

            var reloaded = CreateProvider().Load();
            Check.WithCustomMessage($"cycle {cycle}: hello")
                .That(reloaded["hello"]).IsEqualTo(103);
            Check.WithCustomMessage($"cycle {cycle}: world")
                .That(reloaded["world"]).IsEqualTo(50);
            first = reloaded;
        }

        // The file itself holds the learned delta, never the merged count.
        Check.That(ReadUser()["hello"]).IsEqualTo(3);
        Check.That(ReadUser()).HasSize(1);
    }

    [Fact]
    public void Save_WritesOnlyTheLearnedDelta()
    {
        WriteBase(new Dictionary<string, int> { ["hello"] = 100, ["the"] = 10 });
        var provider = CreateProvider();
        var merged = provider.Load();
        merged["hello"] = 103;   // +3 learned
        merged["world"] = 1;     // absent from the base: full count

        provider.Save(merged);

        var written = ReadUser();
        Check.That(written["hello"]).IsEqualTo(3);
        Check.That(written["world"]).IsEqualTo(1);
        Check.That(written.ContainsKey("the")).IsFalse();
    }

    [Fact]
    public void Load_InflatedFile_RemovesWholeBaseCopiesAndKeepsResiduals()
    {
        WriteBase(new Dictionary<string, int> { ["weiss"] = 100, ["hallo"] = 5 });
        // 57 copies of the base plus the learned residuals.
        WriteUser(new Dictionary<string, int>
        {
            ["weiss"] = 57 * 100 + 3,
            ["hallo"] = 57 * 5 + 2,
        });

        var provider = CreateProvider();
        var loaded = provider.Load();

        Check.That(loaded["weiss"]).IsEqualTo(103);
        Check.That(loaded["hallo"]).IsEqualTo(7);
    }

    [Fact]
    public void Save_AfterInflatedLoad_BacksUpOnceAndWritesTheDelta()
    {
        WriteBase(new Dictionary<string, int> { ["weiss"] = 100, ["hallo"] = 5 });
        WriteUser(new Dictionary<string, int>
        {
            ["weiss"] = 57 * 100 + 3,
            ["hallo"] = 57 * 5 + 2,
        });
        var originalBytes = File.ReadAllText(UserPath);

        var logger = new CapturingLogger();
        var provider = CreateProvider(logger);
        var loaded = provider.Load();
        provider.Save(loaded);

        var written = ReadUser();
        Check.That(written["weiss"]).IsEqualTo(3);
        Check.That(written["hallo"]).IsEqualTo(2);
        Check.That(File.Exists(BackupPath)).IsTrue();
        Check.That(File.ReadAllText(BackupPath)).IsEqualTo(originalBytes);
        Check.That(logger.Messages).HasElementThatMatches(m => m.Contains("original", StringComparison.OrdinalIgnoreCase));

        // The backup is never overwritten by a later save.
        File.WriteAllText(BackupPath, "keep me");
        var reloaded = CreateProvider().Load();
        provider.Save(reloaded);
        Check.That(File.ReadAllText(BackupPath)).IsEqualTo("keep me");
    }

    [Fact]
    public void Save_BackupFails_LeavesTheFileUntouchedAndRetriesLater()
    {
        WriteBase(new Dictionary<string, int> { ["weiss"] = 100, ["hallo"] = 5 });
        WriteUser(new Dictionary<string, int>
        {
            ["weiss"] = 57 * 100 + 3,
            ["hallo"] = 57 * 5 + 2,
        });
        var original = File.ReadAllText(UserPath);

        // A directory at the backup path makes the copy fail.
        Directory.CreateDirectory(BackupPath);

        var provider = CreateProvider();
        var loaded = provider.Load();
        provider.Save(loaded);

        Check.WithCustomMessage("The inflated original was overwritten even though no backup landed")
            .That(File.ReadAllText(UserPath)).IsEqualTo(original);

        Directory.Delete(BackupPath);
        provider.Save(loaded);   // the retry can back up and repair

        Check.That(File.Exists(BackupPath)).IsTrue();
        Check.That(File.ReadAllText(BackupPath)).IsEqualTo(original);
        Check.That(ReadUser()["weiss"]).IsEqualTo(3);
    }

    [Fact]
    public void Save_ExistingBackup_IsNeverOverwrittenAndIsNamedInTheLog()
    {
        WriteBase(new Dictionary<string, int> { ["weiss"] = 100, ["hallo"] = 5 });
        WriteUser(new Dictionary<string, int>
        {
            ["weiss"] = 57 * 100 + 3,
            ["hallo"] = 57 * 5 + 2,
        });
        File.WriteAllText(BackupPath, "older snapshot");

        var logger = new CapturingLogger();
        var provider = CreateProvider(logger);
        provider.Save(provider.Load());

        Check.That(File.ReadAllText(BackupPath)).IsEqualTo("older snapshot");
        Check.That(ReadUser()["weiss"]).IsEqualTo(3);
        Check.WithCustomMessage($"log: {string.Join(" | ", logger.Messages)}")
            .That(logger.Messages).HasElementThatMatches(
                m => m.Contains("existing backup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_GenuineDeltaFile_IsNeverTreatedAsInflated()
    {
        // A realistic base size: one rare word with a large learned delta is far below 90%.
        var baseWords = new Dictionary<string, int> { ["rare"] = 1 };
        for (var i = 0; i < 20; i++) baseWords[$"word{i}"] = 100;
        WriteBase(baseWords);
        WriteUser(new Dictionary<string, int> { ["rare"] = 5 });   // the user typed it five times

        var provider = CreateProvider();
        var loaded = provider.Load();

        Check.That(loaded["rare"]).IsEqualTo(6);   // base + the learned 5, not modulo-repaired
        Check.That(File.Exists(BackupPath)).IsFalse();

        provider.Save(loaded);
        var written = ReadUser();
        Check.That(written["rare"]).IsEqualTo(5);
        Check.That(CreateProvider().Load()["rare"]).IsEqualTo(6);
    }

    [Fact]
    public void Load_InflatedFile_KeepsWordsThatAreNotInTheBase()
    {
        var baseWords = new Dictionary<string, int> { ["weiss"] = 100 };
        for (var i = 0; i < 9; i++) baseWords[$"word{i}"] = 100;
        WriteBase(baseWords);

        var userWords = new Dictionary<string, int> { ["weiss"] = 57 * 100, ["ownword"] = 4 };
        for (var i = 0; i < 9; i++) userWords[$"word{i}"] = 57 * 100;
        WriteUser(userWords);

        var loaded = CreateProvider().Load();

        Check.That(loaded["weiss"]).IsEqualTo(100);
        Check.That(loaded["ownword"]).IsEqualTo(4);
    }


    private sealed class CapturingLogger : ILogger<FileDictionaryProvider>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
