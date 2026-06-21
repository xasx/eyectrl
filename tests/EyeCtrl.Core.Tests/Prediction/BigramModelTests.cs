namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using NFluent;

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
        Check.That(nextWords).HasSize(1);
        Check.That(nextWords[0].word).IsEqualTo("world");
        Check.That(nextWords[0].frequency).IsEqualTo(2);
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
        Check.That(nextWords).HasSize(1);
        Check.That(nextWords[0].word).IsEqualTo("you");
        Check.That(nextWords[0].frequency).IsEqualTo(3);
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
        Check.That(nextWords).HasSize(3);

        // Should be ordered by frequency
        Check.That(nextWords[0].word).IsEqualTo("you");
        Check.That(nextWords[0].frequency).IsEqualTo(3);
        Check.That(nextWords[1].word).IsEqualTo("be");
        Check.That(nextWords[1].frequency).IsEqualTo(2);
        Check.That(nextWords[2].word).IsEqualTo("do");
        Check.That(nextWords[2].frequency).IsEqualTo(2);
    }

    [Fact]
    public void GetNextWords_IsCaseSensitive()
    {
        // Arrange
        var model = new BigramModel();
        // Learn each case variant multiple times to exceed MinFrequency threshold
        model.Learn("Hello", "World");
        model.Learn("Hello", "World");
        model.Learn("HELLO", "WORLD");
        model.Learn("HELLO", "WORLD");

        // Act
        var nextWords1 = model.GetNextWords("Hello", 10);
        var nextWords2 = model.GetNextWords("HELLO", 10);
        var nextWords3 = model.GetNextWords("hello", 10);

        // Assert - Case-sensitive: "Hello" and "HELLO" are distinct keys
        Check.That(nextWords1).HasSize(1);
        Check.That(nextWords1[0].word).IsEqualTo("World");
        Check.That(nextWords1[0].frequency).IsEqualTo(2);

        Check.That(nextWords2).HasSize(1);
        Check.That(nextWords2[0].word).IsEqualTo("WORLD");
        Check.That(nextWords2[0].frequency).IsEqualTo(2);

        // "hello" doesn't exist, should return empty
        Check.That(nextWords3).IsEmpty();
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
        Check.That(nextWords).HasSize(1);
        Check.That(nextWords[0].word).IsEqualTo("high");
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
        Check.That(nextWords).HasSize(3);
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
        Check.That(nextWords).IsEmpty();
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
        Check.That(nextWords).IsEmpty();
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
        Check.That(nextWords).IsEmpty();
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
        Check.That(allBigrams).IsEmpty();
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
        Check.That(nextWords).HasSize(2);
        Check.That(nextWords[0].word).IsEqualTo("are");
        Check.That(nextWords[0].frequency).IsEqualTo(2);
        Check.That(nextWords[1].word).IsEqualTo("do");
        Check.That(nextWords[1].frequency).IsEqualTo(2);
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

        Check.That(oldWords).IsEmpty();
        Check.That(newWords).HasSize(1);
        Check.That(newWords[0].word).IsEqualTo("data");
        Check.That(newWords[0].frequency).IsEqualTo(5);
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
        Check.That(bigrams).HasSize(1);
        Check.That(bigrams).ContainsKey("word1");
        Check.That(bigrams["word1"]).HasSize(2);
        Check.That(bigrams["word1"]["word2"]).IsEqualTo(1);
        Check.That(bigrams["word1"]["word3"]).IsEqualTo(1);
    }
}
