namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Represents a word prediction with metadata for ranking and UI display.
/// Immutable record type following functional programming best practices.
/// </summary>
public record PredictionResult
{
    /// <summary>
    /// The predicted word or phrase.
    /// </summary>
    public string Word { get; init; } = string.Empty;

    /// <summary>
    /// The computed score used for ranking predictions.
    /// Higher scores indicate higher relevance.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// The type of prediction algorithm that generated this result.
    /// </summary>
    public PredictionType Type { get; init; }

    /// <summary>
    /// Confidence level from 0.0 to 1.0.
    /// Used for UI feedback (e.g., background shading).
    /// Values may exceed 1.0 in some scoring algorithms.
    /// </summary>
    public double Confidence { get; init; }

    public PredictionResult(string word, double score, PredictionType type, double confidence)
    {
        Word = word;
        Score = score;
        Type = type;
        Confidence = confidence;
    }
}

/// <summary>
/// Categorizes the type of prediction algorithm used.
/// </summary>
public enum PredictionType
{
    /// <summary>
    /// Direct prefix match (e.g., "hel" → "hello").
    /// </summary>
    ExactPrefix,

    /// <summary>
    /// Keyboard-aware fuzzy match tolerating typos.
    /// </summary>
    FuzzyMatch,

    /// <summary>
    /// Bigram-based next word prediction (e.g., after "thank" → "you").
    /// </summary>
    NextWord,

    /// <summary>
    /// Multi-word phrase suggestion (e.g., "thank you").
    /// </summary>
    Phrase
}
