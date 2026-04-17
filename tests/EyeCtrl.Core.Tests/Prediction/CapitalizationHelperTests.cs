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
}
