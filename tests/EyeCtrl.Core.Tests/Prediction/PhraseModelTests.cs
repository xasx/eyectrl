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
        Check.That(phrases).HasSize(1);
        Check.That(phrases[0].phrase).IsEqualTo("thank you");
        Check.That(phrases[0].frequency).IsEqualTo(2);
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

        Check.That(phrases2).HasSize(1);
        Check.That(phrases2[0].phrase).IsEqualTo("how are");

        Check.That(phrases3).HasSize(1);
        Check.That(phrases3[0].phrase).IsEqualTo("how are you");
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
        Check.That(phrases).HasSize(1);
        Check.That(phrases[0].phrase).IsEqualTo("looking forward to hearing");
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

        Check.That(fromI).HasElementThatMatches(p => p.phrase == "I am");
        Check.That(fromIAm).HasElementThatMatches(p => p.phrase == "I am looking");
        Check.That(fromIAmLooking).HasElementThatMatches(p => p.phrase == "I am looking forward");
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
        Check.That(phrases1).HasSize(1);
        Check.That(phrases1[0].phrase).IsEqualTo("thank you");
        Check.That(phrases1[0].frequency).IsEqualTo(2);

        // Only "THANK" prefix has 2 occurrences
        Check.That(phrases2).HasSize(1);
        Check.That(phrases2[0].phrase).IsEqualTo("THANK YOU");
        Check.That(phrases2[0].frequency).IsEqualTo(2);

        // Only "Thank" prefix has 2 occurrences
        Check.That(phrases3).HasSize(1);
        Check.That(phrases3[0].phrase).IsEqualTo("Thank You");
        Check.That(phrases3[0].frequency).IsEqualTo(2);
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
        Check.That(allPhrases).IsEmpty();
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
        Check.That(allPhrases).IsEmpty();
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
        Check.That(phrases8).IsEmpty(); // No 8-word phrase stored

        var phrases7 = model.GetPhrases("one two three four five six", 10);
        Check.That(phrases7).HasSize(1);
        Check.That(phrases7[0].phrase).IsEqualTo("one two three four five six seven");
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

        Check.That(highPhrases).HasSize(1);
        Check.That(lowPhrases).IsEmpty();
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
        Check.That(phrases.Count).IsEqualTo(2);
        Check.That(phrases[0].phrase).IsEqualTo("very common");
        Check.That(phrases[0].frequency).IsEqualTo(5);
        Check.That(phrases[1].phrase).IsEqualTo("very rare");
        Check.That(phrases[1].frequency).IsEqualTo(3);
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
        Check.That(phrases.Count).IsEqualTo(3);
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
        Check.That(phrases).IsEmpty();
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
        Check.That(phrases).IsEmpty();
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
        Check.That(phrases.Count).IsEqualTo(2);
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

        Check.That(oldPhrases).IsEmpty();
        Check.That(newPhrases).HasSize(1);
        Check.That(newPhrases[0].phrase).IsEqualTo("new data");
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
        Check.That(allPhrases).HasSize(1);
        Check.That(allPhrases.ContainsKey("test")).IsTrue();
        Check.That(allPhrases["test"].Count).IsEqualTo(2);
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

        Check.That(fromA).HasElementThatMatches(p => p.phrase == "a b");
        Check.That(fromB).HasElementThatMatches(p => p.phrase == "b c");
        Check.That(fromAB).HasElementThatMatches(p => p.phrase == "a b c");
        Check.That(fromBC).HasElementThatMatches(p => p.phrase == "b c d");
    }
}
