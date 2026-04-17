namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class BigramModelTests
{
    [Fact]
    public void Learn_StoresSingleBigram()
    {
        // Arrange
        var model = new BigramModel();

        // Act
        model.Learn("hello", "world");
        model.Learn("hello", "world"); // Need frequency 2+ to pass filter

        // Assert
        var nextWords = model.GetNextWords("hello", 10);
        Assert.Single(nextWords);
        Assert.Equal("world", nextWords[0].word);
        Assert.Equal(2, nextWords[0].frequency);
    }

    [Fact]
    public void Learn_IncrementsFrequencyForRepeatedBigram()
    {
        // Arrange
        var model = new BigramModel();

        // Act
        model.Learn("thank", "you");
        model.Learn("thank", "you");
        model.Learn("thank", "you");

        // Assert
        var nextWords = model.GetNextWords("thank", 10);
        Assert.Single(nextWords);
        Assert.Equal("you", nextWords[0].word);
        Assert.Equal(3, nextWords[0].frequency);
    }

    [Fact]
    public void Learn_StoresMultipleFollowersForSameWord()
    {
        // Arrange
        var model = new BigramModel();

        // Act
        model.Learn("can", "you");
        model.Learn("can", "you"); // Frequency 2
        model.Learn("can", "be");
        model.Learn("can", "be");  // Frequency 2
        model.Learn("can", "do");
        model.Learn("can", "do");  // Frequency 2
        model.Learn("can", "you"); // Extra to make "you" have highest frequency

        // Assert
        var nextWords = model.GetNextWords("can", 10);
        Assert.Equal(3, nextWords.Count);

        // Should be ordered by frequency
        Assert.Equal("you", nextWords[0].word);
        Assert.Equal(3, nextWords[0].frequency);
        Assert.Equal("be", nextWords[1].word);
        Assert.Equal(2, nextWords[1].frequency);
        Assert.Equal("do", nextWords[2].word);
        Assert.Equal(2, nextWords[2].frequency);
    }

    [Fact]
    public void GetNextWords_IsCaseInsensitive()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("Hello", "World");
        model.Learn("HELLO", "WORLD");

        // Act
        var nextWords1 = model.GetNextWords("hello", 10);
        var nextWords2 = model.GetNextWords("HELLO", 10);
        var nextWords3 = model.GetNextWords("HeLLo", 10);

        // Assert
        Assert.Single(nextWords1);
        Assert.Equal("world", nextWords1[0].word);
        Assert.Equal(2, nextWords1[0].frequency);

        Assert.Equal(nextWords1, nextWords2);
        Assert.Equal(nextWords1, nextWords3);
    }

    [Fact]
    public void GetNextWords_FiltersLowFrequencyWords()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("test", "high");
        model.Learn("test", "high"); // Frequency 2
        model.Learn("test", "low");  // Frequency 1 - should be filtered

        // Act
        var nextWords = model.GetNextWords("test", 10);

        // Assert
        Assert.Single(nextWords);
        Assert.Equal("high", nextWords[0].word);
    }

    [Fact]
    public void GetNextWords_RespectsMaxCount()
    {
        // Arrange
        var model = new BigramModel();
        for (int i = 0; i < 10; i++)
        {
            model.Learn("number", $"word{i}");
            model.Learn("number", $"word{i}"); // All have frequency 2
        }

        // Act
        var nextWords = model.GetNextWords("number", maxCount: 3);

        // Assert
        Assert.Equal(3, nextWords.Count);
    }

    [Fact]
    public void GetNextWords_ReturnsEmptyForUnknownWord()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("known", "word");

        // Act
        var nextWords = model.GetNextWords("unknown", 10);

        // Assert
        Assert.Empty(nextWords);
    }

    [Fact]
    public void GetNextWords_ReturnsEmptyForEmptyString()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("test", "word");

        // Act
        var nextWords = model.GetNextWords("", 10);

        // Assert
        Assert.Empty(nextWords);
    }

    [Fact]
    public void GetNextWords_ReturnsEmptyForNullString()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("test", "word");

        // Act
        var nextWords = model.GetNextWords(null!, 10);

        // Assert
        Assert.Empty(nextWords);
    }

    [Fact]
    public void Learn_IgnoresNullOrWhiteSpaceWords()
    {
        // Arrange
        var model = new BigramModel();

        // Act
        model.Learn(null!, "word");
        model.Learn("word", null!);
        model.Learn("", "word");
        model.Learn("word", "");
        model.Learn("   ", "word");

        // Assert
        var allBigrams = model.GetAllBigrams();
        Assert.Empty(allBigrams);
    }

    [Fact]
    public void LoadFromDictionary_RestoresState()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("how", "are");
        model.Learn("how", "are");
        model.Learn("how", "do");
        model.Learn("how", "do"); // Need frequency 2+ to pass filter
        var saved = model.GetAllBigrams();

        // Act
        var newModel = new BigramModel();
        newModel.LoadFromDictionary(saved);

        // Assert
        var nextWords = newModel.GetNextWords("how", 10);
        Assert.Equal(2, nextWords.Count);
        Assert.Equal("are", nextWords[0].word);
        Assert.Equal(2, nextWords[0].frequency);
        Assert.Equal("do", nextWords[1].word);
        Assert.Equal(2, nextWords[1].frequency);
    }

    [Fact]
    public void LoadFromDictionary_ClearsPreviousState()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("old", "data");

        var newData = new Dictionary<string, Dictionary<string, int>>
        {
            ["new"] = new Dictionary<string, int> { ["data"] = 5 }
        };

        // Act
        model.LoadFromDictionary(newData);

        // Assert
        var oldWords = model.GetNextWords("old", 10);
        var newWords = model.GetNextWords("new", 10);

        Assert.Empty(oldWords);
        Assert.Single(newWords);
        Assert.Equal("data", newWords[0].word);
        Assert.Equal(5, newWords[0].frequency);
    }

    [Fact]
    public void GetAllBigrams_ReturnsInternalState()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("word1", "word2");
        model.Learn("word1", "word3");

        // Act
        var bigrams = model.GetAllBigrams();

        // Assert
        Assert.Single(bigrams);
        Assert.True(bigrams.ContainsKey("word1"));
        Assert.Equal(2, bigrams["word1"].Count);
        Assert.Equal(1, bigrams["word1"]["word2"]);
        Assert.Equal(1, bigrams["word1"]["word3"]);
    }
}
