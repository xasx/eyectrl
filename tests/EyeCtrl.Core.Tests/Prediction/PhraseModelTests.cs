namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class PhraseModelTests
{
    [Fact]
    public void LearnPhrase_StoresTwoWordPhrase()
    {
        // Arrange
        var model = new PhraseModel();

        // Act
        model.LearnPhrase(new[] { "thank", "you" });
        model.LearnPhrase(new[] { "thank", "you" }); // Need frequency 2+

        // Assert
        var phrases = model.GetPhrases("thank", 10);
        Assert.Single(phrases);
        Assert.Equal("thank you", phrases[0].phrase);
        Assert.Equal(2, phrases[0].frequency);
    }

    [Fact]
    public void LearnPhrase_StoresThreeWordPhrase()
    {
        // Arrange
        var model = new PhraseModel();

        // Act
        model.LearnPhrase(new[] { "how", "are", "you" });
        model.LearnPhrase(new[] { "how", "are", "you" });

        // Assert
        var phrases2 = model.GetPhrases("how", 10);
        var phrases3 = model.GetPhrases("how are", 10);

        Assert.Single(phrases2);
        Assert.Equal("how are", phrases2[0].phrase);

        Assert.Single(phrases3);
        Assert.Equal("how are you", phrases3[0].phrase);
    }

    [Fact]
    public void LearnPhrase_StoresFourWordPhrase()
    {
        // Arrange
        var model = new PhraseModel();

        // Act
        model.LearnPhrase(new[] { "looking", "forward", "to", "hearing" });
        model.LearnPhrase(new[] { "looking", "forward", "to", "hearing" });

        // Assert
        var phrases = model.GetPhrases("looking forward to", 10);
        Assert.Single(phrases);
        Assert.Equal("looking forward to hearing", phrases[0].phrase);
    }

    [Fact]
    public void LearnPhrase_ExtractsAllSubphrases()
    {
        // Arrange
        var model = new PhraseModel();

        // Act - Learn a 4-word phrase multiple times
        for (int i = 0; i < 3; i++)
            model.LearnPhrase(new[] { "I", "am", "looking", "forward" });

        // Assert - Should create multiple sub-phrases (case-sensitive)
        var fromI = model.GetPhrases("I", 10);
        var fromIAm = model.GetPhrases("I am", 10);
        var fromIAmLooking = model.GetPhrases("I am looking", 10);

        Assert.Contains(fromI, p => p.phrase == "I am");
        Assert.Contains(fromIAm, p => p.phrase == "I am looking");
        Assert.Contains(fromIAmLooking, p => p.phrase == "I am looking forward");
    }

    [Fact]
    public void LearnPhrase_IsCaseSensitive()
    {
        // Arrange
        var model = new PhraseModel();

        // Act - Learn each case variant multiple times to exceed MinFrequency threshold
        model.LearnPhrase(new[] { "Thank", "You" });
        model.LearnPhrase(new[] { "Thank", "You" });
        model.LearnPhrase(new[] { "THANK", "YOU" });
        model.LearnPhrase(new[] { "THANK", "YOU" });
        model.LearnPhrase(new[] { "thank", "you" });
        model.LearnPhrase(new[] { "thank", "you" });

        // Assert - Case-sensitive: each variant is distinct
        var phrases1 = model.GetPhrases("thank", 10);
        var phrases2 = model.GetPhrases("THANK", 10);
        var phrases3 = model.GetPhrases("Thank", 10);

        // Only "thank" prefix has 2 occurrences
        Assert.Single(phrases1);
        Assert.Equal("thank you", phrases1[0].phrase);
        Assert.Equal(2, phrases1[0].frequency);

        // Only "THANK" prefix has 2 occurrences
        Assert.Single(phrases2);
        Assert.Equal("THANK YOU", phrases2[0].phrase);
        Assert.Equal(2, phrases2[0].frequency);

        // Only "Thank" prefix has 2 occurrences
        Assert.Single(phrases3);
        Assert.Equal("Thank You", phrases3[0].phrase);
        Assert.Equal(2, phrases3[0].frequency);
    }

    [Fact]
    public void LearnPhrase_IgnoresSingleWordArray()
    {
        // Arrange
        var model = new PhraseModel();

        // Act
        model.LearnPhrase(new[] { "single" });

        // Assert
        var allPhrases = model.GetAllPhrases();
        Assert.Empty(allPhrases);
    }

    [Fact]
    public void LearnPhrase_IgnoresEmptyArray()
    {
        // Arrange
        var model = new PhraseModel();

        // Act
        model.LearnPhrase(Array.Empty<string>());

        // Assert
        var allPhrases = model.GetAllPhrases();
        Assert.Empty(allPhrases);
    }

    [Fact]
    public void LearnPhrase_IgnoresPhrasesLongerThanSevenWords()
    {
        // Arrange
        var model = new PhraseModel();

        // Act - Learn an 8-word phrase twice
        model.LearnPhrase(new[] { "one", "two", "three", "four", "five", "six", "seven", "eight" });
        model.LearnPhrase(new[] { "one", "two", "three", "four", "five", "six", "seven", "eight" });

        // Assert - Should only store sub-phrases up to 7 words
        var phrases8 = model.GetPhrases("one two three four five six seven", 10);
        Assert.Empty(phrases8); // No 8-word phrase stored

        var phrases7 = model.GetPhrases("one two three four five six", 10);
        Assert.Single(phrases7);
        Assert.Equal("one two three four five six seven", phrases7[0].phrase);
    }

    [Fact]
    public void GetPhrases_FiltersLowFrequency()
    {
        // Arrange
        var model = new PhraseModel();

        // Act
        model.LearnPhrase(new[] { "high", "frequency" });
        model.LearnPhrase(new[] { "high", "frequency" }); // Freq 2
        model.LearnPhrase(new[] { "low", "frequency" }); // Freq 1 - filtered

        // Assert
        var highPhrases = model.GetPhrases("high", 10);
        var lowPhrases = model.GetPhrases("low", 10);

        Assert.Single(highPhrases);
        Assert.Empty(lowPhrases);
    }

    [Fact]
    public void GetPhrases_OrdersByFrequency()
    {
        // Arrange
        var model = new PhraseModel();

        // Act
        for (int i = 0; i < 5; i++)
            model.LearnPhrase(new[] { "very", "common" });

        for (int i = 0; i < 3; i++)
            model.LearnPhrase(new[] { "very", "rare" });

        // Assert
        var phrases = model.GetPhrases("very", 10);
        Assert.Equal(2, phrases.Count);
        Assert.Equal("very common", phrases[0].phrase);
        Assert.Equal(5, phrases[0].frequency);
        Assert.Equal("very rare", phrases[1].phrase);
        Assert.Equal(3, phrases[1].frequency);
    }

    [Fact]
    public void GetPhrases_RespectsMaxCount()
    {
        // Arrange
        var model = new PhraseModel();

        // Act - Create many phrases with same prefix
        for (int i = 0; i < 10; i++)
        {
            model.LearnPhrase(new[] { "test", $"word{i}" });
            model.LearnPhrase(new[] { "test", $"word{i}" });
        }

        // Assert
        var phrases = model.GetPhrases("test", maxCount: 3);
        Assert.Equal(3, phrases.Count);
    }

    [Fact]
    public void GetPhrases_ReturnsEmptyForUnknownPrefix()
    {
        // Arrange
        var model = new PhraseModel();
        model.LearnPhrase(new[] { "known", "phrase" });
        model.LearnPhrase(new[] { "known", "phrase" });

        // Act
        var phrases = model.GetPhrases("unknown", 10);

        // Assert
        Assert.Empty(phrases);
    }

    [Fact]
    public void GetPhrases_ReturnsEmptyForEmptyPrefix()
    {
        // Arrange
        var model = new PhraseModel();
        model.LearnPhrase(new[] { "test", "phrase" });
        model.LearnPhrase(new[] { "test", "phrase" });

        // Act
        var phrases = model.GetPhrases("", 10);

        // Assert
        Assert.Empty(phrases);
    }

    [Fact]
    public void LoadFromDictionary_RestoresState()
    {
        // Arrange
        var model = new PhraseModel();
        model.LearnPhrase(new[] { "hello", "world" });
        model.LearnPhrase(new[] { "hello", "world" });
        model.LearnPhrase(new[] { "hello", "there" });
        model.LearnPhrase(new[] { "hello", "there" });
        var saved = model.GetAllPhrases();

        // Act
        var newModel = new PhraseModel();
        newModel.LoadFromDictionary(saved);

        // Assert
        var phrases = newModel.GetPhrases("hello", 10);
        Assert.Equal(2, phrases.Count);
    }

    [Fact]
    public void LoadFromDictionary_ClearsPreviousState()
    {
        // Arrange
        var model = new PhraseModel();
        model.LearnPhrase(new[] { "old", "data" });
        model.LearnPhrase(new[] { "old", "data" });

        var newData = new Dictionary<string, Dictionary<string, int>>
        {
            ["new"] = new Dictionary<string, int> { ["new data"] = 5 }
        };

        // Act
        model.LoadFromDictionary(newData);

        // Assert
        var oldPhrases = model.GetPhrases("old", 10);
        var newPhrases = model.GetPhrases("new", 10);

        Assert.Empty(oldPhrases);
        Assert.Single(newPhrases);
        Assert.Equal("new data", newPhrases[0].phrase);
    }

    [Fact]
    public void GetAllPhrases_ReturnsInternalState()
    {
        // Arrange
        var model = new PhraseModel();
        model.LearnPhrase(new[] { "test", "one" });
        model.LearnPhrase(new[] { "test", "one" });
        model.LearnPhrase(new[] { "test", "two" });
        model.LearnPhrase(new[] { "test", "two" });

        // Act
        var allPhrases = model.GetAllPhrases();

        // Assert
        Assert.Single(allPhrases);
        Assert.True(allPhrases.ContainsKey("test"));
        Assert.Equal(2, allPhrases["test"].Count);
    }

    [Fact]
    public void LearnPhrase_HandlesOverlappingPhrases()
    {
        // Arrange
        var model = new PhraseModel();

        // Act - Learn overlapping sequences
        model.LearnPhrase(new[] { "a", "b", "c" });
        model.LearnPhrase(new[] { "a", "b", "c" });
        model.LearnPhrase(new[] { "b", "c", "d" });
        model.LearnPhrase(new[] { "b", "c", "d" });

        // Assert
        var fromA = model.GetPhrases("a", 10);
        var fromB = model.GetPhrases("b", 10);
        var fromAB = model.GetPhrases("a b", 10);
        var fromBC = model.GetPhrases("b c", 10);

        Assert.Contains(fromA, p => p.phrase == "a b");
        Assert.Contains(fromB, p => p.phrase == "b c");
        Assert.Contains(fromAB, p => p.phrase == "a b c");
        Assert.Contains(fromBC, p => p.phrase == "b c d");
    }
}
