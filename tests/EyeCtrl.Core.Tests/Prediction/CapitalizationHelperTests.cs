namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class CapitalizationHelperTests
{
    [Fact]
    public void ShouldCapitalize_ReturnsTrueAtStartOfSentence()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsTrueAfterPeriod()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("Hello world.");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsTrueAfterExclamation()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("Wow!");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsTrueAfterQuestionMark()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("Really?");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsTrueAfterPeriodWithSpaces()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("Hello.   ");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsFalseInMiddleOfSentence()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("Hello world");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsFalseWithComma()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("Hello, world");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsFalseWithAbbreviation()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("Mr. Smith");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCapitalize_HandlesPeriodWithinSentence()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("I.e. this");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCapitalize_HandlesMultipleSentences()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ShouldCapitalize("First. Second. Third.");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Capitalize_CapitalizesFirstLetter()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize("hello");

        // Assert
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void Capitalize_PreservesRestOfString()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize("hELLO");

        // Assert
        Assert.Equal("HELLO", result);
    }

    [Fact]
    public void Capitalize_HandlesEmptyString()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize("");

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void Capitalize_HandlesNullString()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize(null!);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void Capitalize_HandlesSingleCharacter()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize("a");

        // Assert
        Assert.Equal("A", result);
    }

    [Fact]
    public void Capitalize_HandlesAlreadyCapitalized()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize("Hello");

        // Assert
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void Capitalize_HandlesWhitespace()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize("   ");

        // Assert
        Assert.Equal("   ", result);
    }

    [Fact]
    public void Capitalize_HandlesNumbers()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize("123abc");

        // Assert
        Assert.Equal("123Abc", result);
    }

    [Fact]
    public void Capitalize_HandlesSpecialCharacters()
    {
        // Arrange & Act
        var result = CapitalizationHelper.Capitalize("!hello");

        // Assert
        Assert.Equal("!Hello", result);
    }

    [Fact]
    public void ApplySmartCapitalization_CapitalizesWhenNeeded()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ApplySmartCapitalization("hello", "");

        // Assert
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void ApplySmartCapitalization_DoesNotCapitalizeInMiddle()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ApplySmartCapitalization("hello", "start");

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ApplySmartCapitalization_CapitalizesAfterPeriod()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ApplySmartCapitalization("world", "Hello.");

        // Assert
        Assert.Equal("World", result);
    }

    [Fact]
    public void ApplySmartCapitalization_PreservesExistingCapitalization()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ApplySmartCapitalization("Hello", "start");

        // Assert
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void ApplySmartCapitalization_HandlesEmptyWord()
    {
        // Arrange & Act
        var result = CapitalizationHelper.ApplySmartCapitalization("", "text");

        // Assert
        Assert.Equal("", result);
    }

    // Language-specific abbreviation tests

    [Fact]
    public void ShouldCapitalize_ReturnsFalseWithGermanAbbreviation()
    {
        // Arrange
        var germanAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "z.b", "bzw", "usw", "evtl" };

        // Act
        var result = CapitalizationHelper.ShouldCapitalize("z.B. ", germanAbbreviations);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsFalseWithFrenchAbbreviation()
    {
        // Arrange
        var frenchAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "m", "mme", "mlle", "dr" };

        // Act
        var result = CapitalizationHelper.ShouldCapitalize("M. ", frenchAbbreviations);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsFalseWithSpanishAbbreviation()
    {
        // Arrange
        var spanishAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sr", "sra", "srta", "dr" };

        // Act
        var result = CapitalizationHelper.ShouldCapitalize("Sr. ", spanishAbbreviations);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsFalseWithPolishAbbreviation()
    {
        // Arrange
        var polishAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "np", "itd", "dr", "prof" };

        // Act
        var result = CapitalizationHelper.ShouldCapitalize("np. ", polishAbbreviations);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCapitalize_ReturnsTrueAfterNonAbbreviationPeriodInGerman()
    {
        // Arrange
        var germanAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "z.b", "bzw", "usw", "evtl" };

        // Act - "Wort" is 4 chars, so not caught by heuristic, and not an abbreviation
        var result = CapitalizationHelper.ShouldCapitalize("Das ist ein Wort. ", germanAbbreviations);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ApplySmartCapitalization_UsesLanguageSpecificAbbreviations()
    {
        // Arrange
        var germanAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "z.b", "bzw", "evtl", "prof" };

        // Act - German abbreviation "prof" should not trigger capitalization (>3 chars, not caught by heuristic)
        var result = CapitalizationHelper.ApplySmartCapitalization("beispiel", "zum Beispiel prof. ", germanAbbreviations);

        // Assert - Should not capitalize after German abbreviation
        Assert.Equal("beispiel", result);
    }

    [Fact]
    public void ApplySmartCapitalization_CapitalizesWithoutAbbreviations()
    {
        // Arrange - No abbreviations provided, so "prof" will not be recognized
        // Act
        var result = CapitalizationHelper.ApplySmartCapitalization("word", "for example prof. ", null);

        // Assert - Should capitalize because "prof" is not recognized without abbreviations
        Assert.Equal("Word", result);
    }

    [Fact]
    public void ShouldCapitalize_DefaultsToNoAbbreviationsWhenNotProvided()
    {
        // Arrange & Act
        var resultWithout = CapitalizationHelper.ShouldCapitalize("Mr. Smith");
        var resultWith = CapitalizationHelper.ShouldCapitalize("Mr. Smith", null);

        // Assert - Both should behave the same way
        Assert.Equal(resultWith, resultWithout);
    }

    [Fact]
    public void ApplySmartCapitalization_DefaultsToNoAbbreviationsWhenNotProvided()
    {
        // Arrange & Act
        var resultWithout = CapitalizationHelper.ApplySmartCapitalization("hello", "");
        var resultWith = CapitalizationHelper.ApplySmartCapitalization("hello", "", null);

        // Assert - Both should behave the same way
        Assert.Equal(resultWith, resultWithout);
    }
}
