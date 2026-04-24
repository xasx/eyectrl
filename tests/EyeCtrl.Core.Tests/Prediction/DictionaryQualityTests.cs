namespace EyeCtrl.Core.Tests.Prediction;

using System.IO;
using System.Text.Json;
using Xunit;

/// <summary>
/// Tests to validate the quality and size of dictionary files after Phase 6 expansion.
/// </summary>
public class DictionaryQualityTests
{
    private readonly string _dictionaryPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "EyeCtrl.App", "Resources", "Dictionaries"
    );

    [Theory]
    [InlineData("en.json", 10000)]
    [InlineData("fr.json", 10000)]
    [InlineData("es.json", 10000)]
    [InlineData("de.json", 10000)]
    [InlineData("pl.json", 10000)]
    public void Dictionary_HasTargetWordCount(string fileName, int expectedCount)
    {
        // Arrange
        var fullPath = Path.Combine(_dictionaryPath, fileName);

        // Act
        var json = File.ReadAllText(fullPath);
        var dict = JsonSerializer.Deserialize<JsonElement>(json);
        var words = dict.GetProperty("words");
        var wordCount = words.EnumerateObject().Count();

        // Assert
        Assert.Equal(expectedCount, wordCount);
    }

    [Theory]
    [InlineData("en.json")]
    [InlineData("fr.json")]
    [InlineData("es.json")]
    [InlineData("de.json")]
    [InlineData("pl.json")]
    public void Dictionary_HasValidStructure(string fileName)
    {
        // Arrange
        var fullPath = Path.Combine(_dictionaryPath, fileName);

        // Act
        var json = File.ReadAllText(fullPath);
        var dict = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert - Must have 'words' property
        Assert.True(dict.TryGetProperty("words", out var words));
        Assert.Equal(JsonValueKind.Object, words.ValueKind);

        // Assert - Must have 'abbreviations' property
        Assert.True(dict.TryGetProperty("abbreviations", out var abbrevs));
        Assert.Equal(JsonValueKind.Array, abbrevs.ValueKind);
    }

    [Theory]
    [InlineData("en.json")]
    [InlineData("fr.json")]
    [InlineData("es.json")]
    [InlineData("de.json")]
    [InlineData("pl.json")]
    public void Dictionary_AllWordsHaveFrequency(string fileName)
    {
        // Arrange
        var fullPath = Path.Combine(_dictionaryPath, fileName);

        // Act
        var json = File.ReadAllText(fullPath);
        var dict = JsonSerializer.Deserialize<JsonElement>(json);
        var words = dict.GetProperty("words");

        // Assert - Every word must have a positive integer frequency
        foreach (var word in words.EnumerateObject())
        {
            Assert.True(word.Value.TryGetInt32(out var freq),
                $"Word '{word.Name}' does not have a valid integer frequency");
            Assert.True(freq > 0,
                $"Word '{word.Name}' has non-positive frequency: {freq}");
        }
    }

    [Theory]
    [InlineData("en.json")]
    [InlineData("fr.json")]
    [InlineData("es.json")]
    [InlineData("de.json")]
    [InlineData("pl.json")]
    public void Dictionary_NoEmptyWords(string fileName)
    {
        // Arrange
        var fullPath = Path.Combine(_dictionaryPath, fileName);

        // Act
        var json = File.ReadAllText(fullPath);
        var dict = JsonSerializer.Deserialize<JsonElement>(json);
        var words = dict.GetProperty("words");

        // Assert - No word should be empty or whitespace
        foreach (var word in words.EnumerateObject())
        {
            Assert.False(string.IsNullOrWhiteSpace(word.Name),
                "Dictionary contains empty or whitespace word");
        }
    }

    [Theory]
    [InlineData("en.json")]
    [InlineData("fr.json")]
    [InlineData("es.json")]
    [InlineData("de.json")]
    [InlineData("pl.json")]
    public void Dictionary_WordsAreReasonableLength(string fileName)
    {
        // Arrange
        var fullPath = Path.Combine(_dictionaryPath, fileName);

        // Act
        var json = File.ReadAllText(fullPath);
        var dict = JsonSerializer.Deserialize<JsonElement>(json);
        var words = dict.GetProperty("words");

        // Assert - Words should be between 1 and 25 characters (reasonable limits)
        foreach (var word in words.EnumerateObject())
        {
            Assert.InRange(word.Name.Length, 1, 25);
        }
    }

    [Theory]
    [InlineData("en.json")]
    [InlineData("fr.json")]
    [InlineData("es.json")]
    [InlineData("de.json")]
    [InlineData("pl.json")]
    public void Dictionary_HasAbbreviations(string fileName)
    {
        // Arrange
        var fullPath = Path.Combine(_dictionaryPath, fileName);

        // Act
        var json = File.ReadAllText(fullPath);
        var dict = JsonSerializer.Deserialize<JsonElement>(json);
        var abbrevs = dict.GetProperty("abbreviations");

        // Assert - Should have at least a few common abbreviations
        Assert.True(abbrevs.GetArrayLength() > 0,
            $"Dictionary {fileName} has no abbreviations");
    }
}
