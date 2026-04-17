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
}
