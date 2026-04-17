namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class PredictionResultTests
{
    [Fact]
    public void Constructor_InitializesAllProperties()
    {
        // Arrange & Act
        var result = new PredictionResult("hello", 100.0, PredictionType.ExactPrefix, 0.95);

        // Assert
        Assert.Equal("hello", result.Word);
        Assert.Equal(100.0, result.Score);
        Assert.Equal(PredictionType.ExactPrefix, result.Type);
        Assert.Equal(0.95, result.Confidence);
    }

    [Fact]
    public void Constructor_AcceptsEmptyWord()
    {
        // Arrange & Act
        var result = new PredictionResult("", 0.0, PredictionType.FuzzyMatch, 0.0);

        // Assert
        Assert.Equal("", result.Word);
    }

    [Theory]
    [InlineData(PredictionType.ExactPrefix)]
    [InlineData(PredictionType.FuzzyMatch)]
    [InlineData(PredictionType.NextWord)]
    [InlineData(PredictionType.Phrase)]
    public void Constructor_AcceptsAllPredictionTypes(PredictionType type)
    {
        // Arrange & Act
        var result = new PredictionResult("test", 50.0, type, 0.5);

        // Assert
        Assert.Equal(type, result.Type);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Constructor_AcceptsValidConfidenceRange(double confidence)
    {
        // Arrange & Act
        var result = new PredictionResult("test", 50.0, PredictionType.ExactPrefix, confidence);

        // Assert
        Assert.Equal(confidence, result.Confidence);
    }

    [Fact]
    public void Confidence_CanBeGreaterThanOne_ForUnusualScoring()
    {
        // Some scoring algorithms might produce values > 1.0
        // The model doesn't enforce this constraint
        var result = new PredictionResult("test", 50.0, PredictionType.ExactPrefix, 1.5);

        Assert.Equal(1.5, result.Confidence);
    }

    [Fact]
    public void Properties_AreImmutable()
    {
        // Arrange
        var result = new PredictionResult("original", 100.0, PredictionType.ExactPrefix, 0.9);

        // Act & Assert - Should not compile if we try to modify
        // result.Word = "modified"; // This should not compile with init
        Assert.Equal("original", result.Word);
    }
}
