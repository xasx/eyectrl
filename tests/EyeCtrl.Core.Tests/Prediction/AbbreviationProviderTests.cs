namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class AbbreviationProviderTests
{
    [Fact]
    public void GetAbbreviations_ReturnsEnglishForEnCode()
    {
        // Arrange & Act
        var abbreviations = AbbreviationProvider.GetAbbreviations("en");

        // Assert
        Assert.NotEmpty(abbreviations);
        Assert.Contains("mr", abbreviations);
        Assert.Contains("dr", abbreviations);
        Assert.Contains("etc", abbreviations);
    }

    [Fact]
    public void GetAbbreviations_ReturnsGermanForDeCode()
    {
        // Arrange & Act
        var abbreviations = AbbreviationProvider.GetAbbreviations("de");

        // Assert
        Assert.NotEmpty(abbreviations);
        Assert.Contains("z.b", abbreviations);
        Assert.Contains("bzw", abbreviations);
        Assert.Contains("usw", abbreviations);
    }

    [Fact]
    public void GetAbbreviations_ReturnsFrenchForFrCode()
    {
        // Arrange & Act
        var abbreviations = AbbreviationProvider.GetAbbreviations("fr");

        // Assert
        Assert.NotEmpty(abbreviations);
        Assert.Contains("m", abbreviations);
        Assert.Contains("mme", abbreviations);
        Assert.Contains("etc", abbreviations);
    }

    [Fact]
    public void GetAbbreviations_ReturnsSpanishForEsCode()
    {
        // Arrange & Act
        var abbreviations = AbbreviationProvider.GetAbbreviations("es");

        // Assert
        Assert.NotEmpty(abbreviations);
        Assert.Contains("sr", abbreviations);
        Assert.Contains("sra", abbreviations);
        Assert.Contains("etc", abbreviations);
    }

    [Fact]
    public void GetAbbreviations_ReturnsPolishForPlCode()
    {
        // Arrange & Act
        var abbreviations = AbbreviationProvider.GetAbbreviations("pl");

        // Assert
        Assert.NotEmpty(abbreviations);
        Assert.Contains("dr", abbreviations);
        Assert.Contains("itd", abbreviations);
        Assert.Contains("np", abbreviations);
    }

    [Fact]
    public void GetAbbreviations_DefaultsToEnglishForUnknownCode()
    {
        // Arrange & Act
        var abbreviations = AbbreviationProvider.GetAbbreviations("xx");

        // Assert
        Assert.NotEmpty(abbreviations);
        Assert.Contains("mr", abbreviations);
        Assert.Contains("etc", abbreviations);
    }

    [Fact]
    public void GetAbbreviations_IsCaseInsensitive()
    {
        // Arrange & Act
        var abbreviations = AbbreviationProvider.GetAbbreviations("en");

        // Assert
        Assert.Contains("MR", abbreviations);  // Should find "mr" with case-insensitive comparison
        Assert.Contains("Dr", abbreviations);
        Assert.Contains("ETC", abbreviations);
    }

    [Fact]
    public void GetAbbreviations_CachesResults()
    {
        // Arrange & Act
        var first = AbbreviationProvider.GetAbbreviations("en");
        var second = AbbreviationProvider.GetAbbreviations("en");

        // Assert - Should return the same instance (cached)
        Assert.Same(first, second);
    }

    [Fact]
    public void GetAbbreviations_HandlesNullLanguageCode()
    {
        // Arrange & Act
        var abbreviations = AbbreviationProvider.GetAbbreviations(null);

        // Assert - Should default to English
        Assert.NotEmpty(abbreviations);
        Assert.Contains("mr", abbreviations);
    }

    [Fact]
    public void GetAbbreviations_NormalizesLanguageCode()
    {
        // Arrange & Act
        var upper = AbbreviationProvider.GetAbbreviations("EN");
        var lower = AbbreviationProvider.GetAbbreviations("en");

        // Assert - Should return the same cached instance
        Assert.Same(upper, lower);
    }
}
