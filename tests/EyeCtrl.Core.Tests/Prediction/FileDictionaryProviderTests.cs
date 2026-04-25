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
        Assert.NotNull(bigrams);
        Assert.Empty(bigrams);
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
        Assert.Equal(2, bigrams.Count);
        Assert.Equal(2, bigrams["hello"].Count);
        Assert.Equal(5, bigrams["hello"]["world"]);
        Assert.Equal(3, bigrams["hello"]["there"]);
        Assert.Single(bigrams["good"]);
        Assert.Equal(2, bigrams["good"]["morning"]);
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
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
        Assert.NotNull(loaded);
        Assert.Single(loaded);
        Assert.Equal(10, loaded["test"]["data"]);
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
        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey("new"));
        Assert.False(loaded.ContainsKey("old"));
    }

    [Fact]
    public void LoadPhrases_ReturnsEmptyWhenFileDoesNotExist()
    {
        // Act
        var phrases = _provider.LoadPhrases();

        // Assert
        Assert.NotNull(phrases);
        Assert.Empty(phrases);
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
        Assert.Equal(2, phrases.Count);
        Assert.Single(phrases["looking"]);
        Assert.Equal(10, phrases["looking"]["looking forward"]);
        Assert.Single(phrases["looking forward"]);
        Assert.Equal(8, phrases["looking forward"]["looking forward to"]);
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
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
        Assert.NotNull(loaded);
        Assert.Single(loaded);
        Assert.Equal(15, loaded["thank"]["thank you"]);
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
        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey("new"));
        Assert.False(loaded.ContainsKey("old"));
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
            Assert.True(File.Exists(path));
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
            Assert.True(File.Exists(path));
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
        Assert.Equal(2, words.Count);
        Assert.Equal(100, words["hello"]);
        Assert.Equal(50, words["world"]);
        Assert.Equal(3, abbreviations.Count);
        Assert.Contains("dr.", abbreviations);
        Assert.Contains("mr.", abbreviations);
        Assert.Contains("ms.", abbreviations);
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
        Assert.NotNull(words);
        Assert.Empty(words);
        Assert.NotNull(abbreviations);
        Assert.Empty(abbreviations);
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
        Assert.Single(words);
        Assert.Equal(5, words["test"]);
        Assert.NotNull(abbreviations);
        Assert.Empty(abbreviations);
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
        Assert.Single(words);
        Assert.NotNull(abbreviations);
        Assert.Empty(abbreviations);
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

        // Assert
        Assert.Contains("dr.", abbreviations);
        Assert.Contains("DR.", abbreviations);
        Assert.Contains("Dr.", abbreviations);
    }

    [Fact]
    public void LoadCasingPreferences_ReturnsEmptyWhenFileDoesNotExist()
    {
        // Act
        var casingPreferences = _provider.LoadCasingPreferences();

        // Assert
        Assert.NotNull(casingPreferences);
        Assert.Empty(casingPreferences);
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
        Assert.Equal(2, casingPreferences.Count);
        Assert.Equal(2, casingPreferences["haus"].Count);
        Assert.Equal(10, casingPreferences["haus"]["Haus"]);
        Assert.Equal(1, casingPreferences["haus"]["haus"]);
        Assert.Single(casingPreferences["hello"]);
        Assert.Equal(5, casingPreferences["hello"]["Hello"]);
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
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
        Assert.NotNull(loaded);
        Assert.Single(loaded);
        Assert.Equal(7, loaded["word"]["Word"]);
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
        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey("new"));
        Assert.False(loaded.ContainsKey("old"));
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
            Assert.True(File.Exists(path));
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
        Assert.NotNull(casingPreferences);
        Assert.Empty(casingPreferences);
    }

    [Fact]
    public void LoadCasingPreferences_HandlesIOExceptionGracefully()
    {
        // Arrange - Create a directory with the same name as the expected file to cause IOException
        var path = Path.Combine(_testDir, "user_dictionary_en_casing.json");
        Directory.CreateDirectory(path);

        try
        {
            // Act
            var casingPreferences = _provider.LoadCasingPreferences();

            // Assert - Should return empty dictionary without throwing
            Assert.NotNull(casingPreferences);
            Assert.Empty(casingPreferences);
        }
        finally
        {
            Directory.Delete(path);
        }
    }
}
