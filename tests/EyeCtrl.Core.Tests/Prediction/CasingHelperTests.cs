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
        Check.That(CasingHelper.IsAllCaps(text)).IsEqualTo(expected);
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
        Check.That(CasingHelper.IsAllCaps(text)).IsEqualTo(expected);
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
        Check.That(CasingHelper.IsKnownAcronym(acronym)).IsTrue();
    }

    [Theory]
    [InlineData("HELLO")]
    [InlineData("WORLD")]
    [InlineData("CUSTOM")]
    public void IsKnownAcronym_UnknownWords_ReturnsFalse(string word)
    {
        Check.That(CasingHelper.IsKnownAcronym(word)).IsFalse();
    }

    [Theory]
    [InlineData("HELLO", "Hello")]
    [InlineData("WORLD", "World")]
    [InlineData("TESTING", "Testing")]
    public void NormalizeAllCaps_RegularWords_ConvertToTitleCase(string input, string expected)
    {
        Check.That(CasingHelper.NormalizeAllCaps(input)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData("USA", "USA")]
    [InlineData("NATO", "NATO")]
    [InlineData("HTML", "HTML")]
    [InlineData("JSON", "JSON")]
    [InlineData("API", "API")]
    public void NormalizeAllCaps_KnownAcronyms_RemainsUnchanged(string input, string expected)
    {
        Check.That(CasingHelper.NormalizeAllCaps(input)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("Hello", "Hello")]
    [InlineData("HeLLo", "HeLLo")]
    [InlineData("H", "H")]
    public void NormalizeAllCaps_NonAllCaps_RemainsUnchanged(string input, string expected)
    {
        Check.That(CasingHelper.NormalizeAllCaps(input)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeAllCaps_NullOrEmpty_ReturnsInput(string? input)
    {
        Check.That(CasingHelper.NormalizeAllCaps(input)).IsEqualTo(input);
    }
}
