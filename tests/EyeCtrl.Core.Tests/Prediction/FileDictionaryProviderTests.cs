namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using System.IO;
using System.Text.Json;

public class FileDictionaryProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileDictionaryProvider _provider;

    public FileDictionaryProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"dict_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        var basePath = Path.Combine(_testDir, "base.json");
        _provider = new FileDictionaryProvider(basePath, _testDir, "en");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void LoadBigrams_ReturnsEmptyWhenFileDoesNotExist()
    {
        // Act
        var bigrams = _provider.LoadBigrams();

        // Assert
        Check.That(bigrams).IsNotNull();
        Check.That(bigrams).IsEmpty();
    }

    [Fact]
    public void LoadBigrams_LoadsExistingData()
    {
        // Arrange
        var testData = new Dictionary<string, Dictionary<string, int>>
        {
            ["hello"] = new Dictionary<string, int> { ["world"] = 5, ["there"] = 3 },
            ["good"] = new Dictionary<string, int> { ["morning"] = 2 }
        };
        var path = Path.Combine(_testDir, "user_dictionary_en_bigrams.json");
        File.WriteAllText(path, JsonSerializer.Serialize(testData));

        // Act
        var bigrams = _provider.LoadBigrams();

        // Assert
        Check.That(bigrams.Count).IsEqualTo(2);
        Check.That(bigrams["hello"].Count).IsEqualTo(2);
        Check.That(bigrams["hello"]["world"]).IsEqualTo(5);
        Check.That(bigrams["hello"]["there"]).IsEqualTo(3);
        Check.That(bigrams["good"]).HasSize(1);
        Check.That(bigrams["good"]["morning"]).IsEqualTo(2);
    }

    [Fact]
    public void SaveBigrams_CreatesFileWithData()
    {
        // Arrange
        var testData = new Dictionary<string, Dictionary<string, int>>
        {
            ["test"] = new Dictionary<string, int> { ["data"] = 10 }
        };

        // Act
        _provider.SaveBigrams(testData);

        // Assert
        var path = Path.Combine(_testDir, "user_dictionary_en_bigrams.json");
        Check.That(File.Exists(path)).IsTrue();
        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
        Check.That(loaded).IsNotNull();
        Check.That(loaded!).HasSize(1);
        Check.That(loaded!["test"]["data"]).IsEqualTo(10);
    }

    [Fact]
    public void SaveBigrams_OverwritesExistingFile()
    {
        // Arrange
        var initialData = new Dictionary<string, Dictionary<string, int>>
        {
            ["old"] = new Dictionary<string, int> { ["data"] = 1 }
        };
        _provider.SaveBigrams(initialData);

        var newData = new Dictionary<string, Dictionary<string, int>>
        {
            ["new"] = new Dictionary<string, int> { ["data"] = 2 }
        };

        // Act
        _provider.SaveBigrams(newData);

        // Assert
        var loaded = _provider.LoadBigrams();
        Check.That(loaded).HasSize(1);
        Check.That(loaded.ContainsKey("new")).IsTrue();
        Check.That(loaded.ContainsKey("old")).IsFalse();
    }

    [Fact]
    public void LoadPhrases_ReturnsEmptyWhenFileDoesNotExist()
    {
        // Act
        var phrases = _provider.LoadPhrases();

        // Assert
        Check.That(phrases).IsNotNull();
        Check.That(phrases).IsEmpty();
    }

    [Fact]
    public void LoadPhrases_LoadsExistingData()
    {
        // Arrange
        var testData = new Dictionary<string, Dictionary<string, int>>
        {
            ["looking"] = new Dictionary<string, int> { ["looking forward"] = 10 },
            ["looking forward"] = new Dictionary<string, int> { ["looking forward to"] = 8 }
        };
        var path = Path.Combine(_testDir, "user_dictionary_en_phrases.json");
        File.WriteAllText(path, JsonSerializer.Serialize(testData));

        // Act
        var phrases = _provider.LoadPhrases();

        // Assert
        Check.That(phrases.Count).IsEqualTo(2);
        Check.That(phrases["looking"]).HasSize(1);
        Check.That(phrases["looking"]["looking forward"]).IsEqualTo(10);
        Check.That(phrases["looking forward"]).HasSize(1);
        Check.That(phrases["looking forward"]["looking forward to"]).IsEqualTo(8);
    }

    [Fact]
    public void SavePhrases_CreatesFileWithData()
    {
        // Arrange
        var testData = new Dictionary<string, Dictionary<string, int>>
        {
            ["thank"] = new Dictionary<string, int> { ["thank you"] = 15 }
        };

        // Act
        _provider.SavePhrases(testData);

        // Assert
        var path = Path.Combine(_testDir, "user_dictionary_en_phrases.json");
        Check.That(File.Exists(path)).IsTrue();
        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
        Check.That(loaded).IsNotNull();
        Check.That(loaded!).HasSize(1);
        Check.That(loaded!["thank"]["thank you"]).IsEqualTo(15);
    }

    [Fact]
    public void SavePhrases_OverwritesExistingFile()
    {
        // Arrange
        var initialData = new Dictionary<string, Dictionary<string, int>>
        {
            ["old"] = new Dictionary<string, int> { ["phrase"] = 1 }
        };
        _provider.SavePhrases(initialData);

        var newData = new Dictionary<string, Dictionary<string, int>>
        {
            ["new"] = new Dictionary<string, int> { ["phrase"] = 2 }
        };

        // Act
        _provider.SavePhrases(newData);

        // Assert
        var loaded = _provider.LoadPhrases();
        Check.That(loaded).HasSize(1);
        Check.That(loaded.ContainsKey("new")).IsTrue();
        Check.That(loaded.ContainsKey("old")).IsFalse();
    }

    [Fact]
    public void SaveBigrams_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var newTestDir = Path.Combine(Path.GetTempPath(), $"dict_test_new_{Guid.NewGuid()}");
        var basePath = Path.Combine(newTestDir, "base.json");
        var provider = new FileDictionaryProvider(basePath, newTestDir, "en");

        try
        {
            var testData = new Dictionary<string, Dictionary<string, int>>
            {
                ["test"] = new Dictionary<string, int> { ["data"] = 1 }
            };

            // Act
            provider.SaveBigrams(testData);

            // Assert
            var path = Path.Combine(newTestDir, "user_dictionary_en_bigrams.json");
            Check.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(newTestDir))
                Directory.Delete(newTestDir, true);
        }
    }

    [Fact]
    public void SavePhrases_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var newTestDir = Path.Combine(Path.GetTempPath(), $"dict_test_new_{Guid.NewGuid()}");
        var basePath = Path.Combine(newTestDir, "base.json");
        var provider = new FileDictionaryProvider(basePath, newTestDir, "en");

        try
        {
            var testData = new Dictionary<string, Dictionary<string, int>>
            {
                ["test"] = new Dictionary<string, int> { ["phrase"] = 1 }
            };

            // Act
            provider.SavePhrases(testData);

            // Assert
            var path = Path.Combine(newTestDir, "user_dictionary_en_phrases.json");
            Check.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(newTestDir))
                Directory.Delete(newTestDir, true);
        }
    }

    [Fact]
    public void Load_LoadsWordsAndAbbreviations()
    {
        // Arrange
        var testData = new
        {
            words = new Dictionary<string, int>
            {
                ["hello"] = 100,
                ["world"] = 50
            },
            abbreviations = new List<string> { "dr.", "mr.", "ms." }
        };
        var basePath = Path.Combine(_testDir, "base.json");
        File.WriteAllText(basePath, JsonSerializer.Serialize(testData));

        // Act
        var words = _provider.Load();
        var abbreviations = _provider.Abbreviations;

        // Assert
        Check.That(words.Count).IsEqualTo(2);
        Check.That(words["hello"]).IsEqualTo(100);
        Check.That(words["world"]).IsEqualTo(50);
        Check.That(abbreviations.Count).IsEqualTo(3);
        Check.That(abbreviations).Contains("dr.");
        Check.That(abbreviations).Contains("mr.");
        Check.That(abbreviations).Contains("ms.");
    }

    [Fact]
    public void Load_HandlesCorruptedJsonGracefully()
    {
        // Arrange
        var basePath = Path.Combine(_testDir, "base.json");
        File.WriteAllText(basePath, "{ this is not valid json }");

        // Act
        var words = _provider.Load();
        var abbreviations = _provider.Abbreviations;

        // Assert - Should return empty dictionaries without throwing
        Check.That(words).IsNotNull();
        Check.That(words).IsEmpty();
        Check.That(abbreviations).IsNotNull();
        Check.That(abbreviations).IsEmpty();
    }

    [Fact]
    public void Load_HandlesEmptyAbbreviationsList()
    {
        // Arrange
        var testData = new
        {
            words = new Dictionary<string, int> { ["test"] = 5 },
            abbreviations = new List<string>()
        };
        var basePath = Path.Combine(_testDir, "base.json");
        File.WriteAllText(basePath, JsonSerializer.Serialize(testData));

        // Act
        var words = _provider.Load();
        var abbreviations = _provider.Abbreviations;

        // Assert
        Check.That(words).HasSize(1);
        Check.That(words["test"]).IsEqualTo(5);
        Check.That(abbreviations).IsNotNull();
        Check.That(abbreviations).IsEmpty();
    }

    [Fact]
    public void Load_HandlesNullAbbreviations()
    {
        // Arrange
        var testData = new
        {
            words = new Dictionary<string, int> { ["test"] = 5 },
            abbreviations = (List<string>?)null
        };
        var basePath = Path.Combine(_testDir, "base.json");
        File.WriteAllText(basePath, JsonSerializer.Serialize(testData));

        // Act
        var words = _provider.Load();
        var abbreviations = _provider.Abbreviations;

        // Assert
        Check.That(words).HasSize(1);
        Check.That(abbreviations).IsNotNull();
        Check.That(abbreviations).IsEmpty();
    }

    [Fact]
    public void Abbreviations_IsCaseInsensitive()
    {
        // Arrange
        var testData = new
        {
            words = new Dictionary<string, int> { ["test"] = 5 },
            abbreviations = new List<string> { "Dr.", "Mr." }
        };
        var basePath = Path.Combine(_testDir, "base.json");
        File.WriteAllText(basePath, JsonSerializer.Serialize(testData));

        // Act
        _provider.Load();
        var abbreviations = _provider.Abbreviations;

        // Assert - case-insensitive abbreviation matching is supported
        Check.That(abbreviations).HasElementThatMatches(a => a.Equals("dr.", StringComparison.OrdinalIgnoreCase));
        Check.That(abbreviations).HasElementThatMatches(a => a.Equals("DR.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadCasingPreferences_ReturnsEmptyWhenFileDoesNotExist()
    {
        // Act
        var casingPreferences = _provider.LoadCasingPreferences();

        // Assert
        Check.That(casingPreferences).IsNotNull();
        Check.That(casingPreferences).IsEmpty();
    }

    [Fact]
    public void LoadCasingPreferences_LoadsExistingData()
    {
        // Arrange
        var testData = new Dictionary<string, Dictionary<string, int>>
        {
            ["haus"] = new Dictionary<string, int> { ["Haus"] = 10, ["haus"] = 1 },
            ["hello"] = new Dictionary<string, int> { ["Hello"] = 5 }
        };
        var path = Path.Combine(_testDir, "user_dictionary_en_casing.json");
        File.WriteAllText(path, JsonSerializer.Serialize(testData));

        // Act
        var casingPreferences = _provider.LoadCasingPreferences();

        // Assert
        Check.That(casingPreferences.Count).IsEqualTo(2);
        Check.That(casingPreferences["haus"].Count).IsEqualTo(2);
        Check.That(casingPreferences["haus"]["Haus"]).IsEqualTo(10);
        Check.That(casingPreferences["haus"]["haus"]).IsEqualTo(1);
        Check.That(casingPreferences["hello"]).HasSize(1);
        Check.That(casingPreferences["hello"]["Hello"]).IsEqualTo(5);
    }

    [Fact]
    public void SaveCasingPreferences_CreatesFileWithData()
    {
        // Arrange
        var testData = new Dictionary<string, Dictionary<string, int>>
        {
            ["word"] = new Dictionary<string, int> { ["Word"] = 7 }
        };

        // Act
        _provider.SaveCasingPreferences(testData);

        // Assert
        var path = Path.Combine(_testDir, "user_dictionary_en_casing.json");
        Check.That(File.Exists(path)).IsTrue();
        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
        Check.That(loaded).IsNotNull();
        Check.That(loaded!).HasSize(1);
        Check.That(loaded!["word"]["Word"]).IsEqualTo(7);
    }

    [Fact]
    public void SaveCasingPreferences_OverwritesExistingFile()
    {
        // Arrange
        var initialData = new Dictionary<string, Dictionary<string, int>>
        {
            ["old"] = new Dictionary<string, int> { ["Old"] = 1 }
        };
        _provider.SaveCasingPreferences(initialData);

        var newData = new Dictionary<string, Dictionary<string, int>>
        {
            ["new"] = new Dictionary<string, int> { ["New"] = 2 }
        };

        // Act
        _provider.SaveCasingPreferences(newData);

        // Assert
        var loaded = _provider.LoadCasingPreferences();
        Check.That(loaded).HasSize(1);
        Check.That(loaded.ContainsKey("new")).IsTrue();
        Check.That(loaded.ContainsKey("old")).IsFalse();
    }

    [Fact]
    public void SaveCasingPreferences_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var newTestDir = Path.Combine(Path.GetTempPath(), $"dict_test_new_{Guid.NewGuid()}");
        var basePath = Path.Combine(newTestDir, "base.json");
        var provider = new FileDictionaryProvider(basePath, newTestDir, "en");

        try
        {
            var testData = new Dictionary<string, Dictionary<string, int>>
            {
                ["test"] = new Dictionary<string, int> { ["Test"] = 1 }
            };

            // Act
            provider.SaveCasingPreferences(testData);

            // Assert
            var path = Path.Combine(newTestDir, "user_dictionary_en_casing.json");
            Check.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(newTestDir))
                Directory.Delete(newTestDir, true);
        }
    }

    [Fact]
    public void LoadCasingPreferences_HandlesCorruptedJsonGracefully()
    {
        // Arrange
        var path = Path.Combine(_testDir, "user_dictionary_en_casing.json");
        File.WriteAllText(path, "{ this is not valid json }");

        // Act
        var casingPreferences = _provider.LoadCasingPreferences();

        // Assert - Should return empty dictionary without throwing
        Check.That(casingPreferences).IsNotNull();
        Check.That(casingPreferences).IsEmpty();
    }

    [Fact]
    public void LoadCasingPreferences_HandlesIOExceptionGracefully()
    {
        // Arrange - Create the expected file, then lock it exclusively so reading throws IOException
        var path = Path.Combine(_testDir, "user_dictionary_en_casing.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>>()));

        using var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // Act
        var casingPreferences = _provider.LoadCasingPreferences();

        // Assert - Should return empty dictionary without throwing
        Check.That(casingPreferences).IsNotNull();
        Check.That(casingPreferences).IsEmpty();
    }
}
