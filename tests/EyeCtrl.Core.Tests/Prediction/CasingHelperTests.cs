namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class CasingHelperTests
{
    [Theory]
    [InlineData("HELLO", true)]
    [InlineData("WORLD", true)]
    [InlineData("USA", true)]
    [InlineData("HTML", true)]
    public void IsAllCaps_AllUppercaseLetters_ReturnsTrue(string text, bool expected)
    {
        Assert.Equal(expected, CasingHelper.IsAllCaps(text));
    }

    [Theory]
    [InlineData("hello", false)]
    [InlineData("Hello", false)]
    [InlineData("hELLO", false)]
    [InlineData("HeLLo", false)]
    [InlineData("H", false)]      // Single letter
    [InlineData("", false)]       // Empty
    [InlineData("123", false)]    // No letters
    public void IsAllCaps_NotAllCaps_ReturnsFalse(string text, bool expected)
    {
        Assert.Equal(expected, CasingHelper.IsAllCaps(text));
    }

    [Theory]
    [InlineData("USA")]
    [InlineData("NATO")]
    [InlineData("FBI")]
    [InlineData("CIA")]
    [InlineData("NASA")]
    [InlineData("HTML")]
    [InlineData("JSON")]
    [InlineData("API")]
    public void IsKnownAcronym_CommonAcronyms_ReturnsTrue(string acronym)
    {
        Assert.True(CasingHelper.IsKnownAcronym(acronym));
    }

    [Theory]
    [InlineData("HELLO")]
    [InlineData("WORLD")]
    [InlineData("CUSTOM")]
    public void IsKnownAcronym_UnknownWords_ReturnsFalse(string word)
    {
        Assert.False(CasingHelper.IsKnownAcronym(word));
    }

    [Theory]
    [InlineData("HELLO", "Hello")]
    [InlineData("WORLD", "World")]
    [InlineData("TESTING", "Testing")]
    public void NormalizeAllCaps_RegularWords_ConvertToTitleCase(string input, string expected)
    {
        Assert.Equal(expected, CasingHelper.NormalizeAllCaps(input));
    }

    [Theory]
    [InlineData("USA", "USA")]
    [InlineData("NATO", "NATO")]
    [InlineData("HTML", "HTML")]
    [InlineData("JSON", "JSON")]
    [InlineData("API", "API")]
    public void NormalizeAllCaps_KnownAcronyms_RemainsUnchanged(string input, string expected)
    {
        Assert.Equal(expected, CasingHelper.NormalizeAllCaps(input));
    }

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("Hello", "Hello")]
    [InlineData("HeLLo", "HeLLo")]
    [InlineData("H", "H")]
    public void NormalizeAllCaps_NonAllCaps_RemainsUnchanged(string input, string expected)
    {
        Assert.Equal(expected, CasingHelper.NormalizeAllCaps(input));
    }

    [Fact]
    public void AddAcronym_CustomAcronym_BecomesKnown()
    {
        var customAcronym = "XYZTEST";

        // Verify it's not known initially
        Assert.False(CasingHelper.IsKnownAcronym(customAcronym));

        // Add it
        CasingHelper.AddAcronym(customAcronym);

        // Verify it's now known
        Assert.True(CasingHelper.IsKnownAcronym(customAcronym));

        // Verify normalization preserves it
        Assert.Equal(customAcronym, CasingHelper.NormalizeAllCaps(customAcronym));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeAllCaps_NullOrEmpty_ReturnsInput(string input)
    {
        Assert.Equal(input, CasingHelper.NormalizeAllCaps(input));
    }
}
